using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>Web検索（DuckDuckGo・APIキー不要・既定OFF）。
/// ON時のみ質問が外部へ送信される（PRIVACY.mdの方針を継承）。
/// 実測: html.duckduckgo.com はGETでは安定するがPOSTはチャレンジを返すことがあるためGET優先＋POSTフォールバック＋リトライ</summary>
public sealed partial class WebSearch
{
    private readonly HttpClient _http;

    public WebSearch()
    {
        // shift_jis等の日本語レガシーエンコーディング対応（共有フレームワーク内蔵）
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ShineosQA/2.0");
    }

    public sealed record WebResult(string Title, string Url, string Snippet);

    [GeneratedRegex(@"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"<a[^>]*class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex SnippetRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(?is)<(script|style|noscript)[^>]*>.*?</(script|style|noscript)>")]
    private static partial Regex InvisibleBlockRegex();

    [GeneratedRegex(@"(?i)<meta[^>]+charset=[""']?([a-zA-Z0-9_\-]+)")]
    private static partial Regex MetaCharsetRegex();

    public async Task<List<WebResult>> SearchAsync(string query, int topN = 4, CancellationToken ct = default)
    {
        // 安定化: ①同一クエリのTTLキャッシュ ②検索の最小間隔ゲート ③空結果時のバックオフ再試行。
        // DuckDuckGoのHTMLエンドポイントは非公式のため、短時間の連続クエリで0件（レート制限）に
        // なることがある（横浜天気の実測）。無料で使い続けるための安定化策
        lock (_gateLock)
        {
            if (_cache.TryGetValue(query, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
                return hit.Results;
            var since = DateTime.UtcNow - _lastSearchUtc;
            if (since < MinSearchInterval) Thread.Sleep(MinSearchInterval - since);
        }
        var results = await SearchCoreAsync(query, topN, ct);
        lock (_gateLock)
        {
            _lastSearchUtc = DateTime.UtcNow;
            _cache[query] = (DateTime.UtcNow, results);
            if (_cache.Count > 32) // 溜まりすぎ防止（古いものから削除）
                foreach (var k in _cache.Keys.OrderBy(k => _cache[k].At).Take(_cache.Count - 32).ToList())
                    _cache.Remove(k);
        }
        return results;
    }

    private static readonly object _gateLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinSearchInterval = TimeSpan.FromSeconds(2.5);
    private static readonly Dictionary<string, (DateTime At, List<WebResult> Results)> _cache = new();
    private static DateTime _lastSearchUtc = DateTime.MinValue;

    /// <summary>検索本体。GET→POST→GETに加え、0件のときはバックオフしてもう1ラウンド試す
    /// （レート制限の空応答は数秒置くと回復する実測）</summary>
    private async Task<List<WebResult>> SearchCoreAsync(string query, int topN, CancellationToken ct)
    {
        Exception? lastErr = null;
        foreach (var (method, delayMs) in new[] { ("GET", 0), ("POST", 1000), ("GET", 2000), ("GET", 5000) })
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            try
            {
                var results = method == "GET" ? await GetAsync(query, topN * 3, ct) : await PostAsync(query, topN * 3, ct);
                results = Dedup(results);
                if (results.Count > 0) return results.Take(topN).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastErr = ex; }
        }
        if (lastErr != null) throw lastErr;
        return new List<WebResult>();
    }

    /// <summary>同一URL・同一内容（タイトル+スニペット）の重複を排除する。
    /// DuckDuckGoは同一サイトの複数URLや同一スニペットの重複を返すことがあり、
    /// 参照情報の重複はクイック1.7Bの「情報なし」誤判定（横浜天気の実測）を誘発する</summary>
    public static List<WebResult> Dedup(List<WebResult> results)
    {
        var seenUrl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<WebResult>();
        foreach (var r in results)
        {
            if (!seenUrl.Add(r.Url.Split('?', '#')[0])) continue;           // 同一URL（クエリ・フラグメント除く）
            if (!seenContent.Add(r.Title + "\n" + r.Snippet)) continue;     // 同一タイトル+スニペット
            list.Add(r);
        }
        return list;
    }

    private async Task<List<WebResult>> GetAsync(string query, int topN, CancellationToken ct)
    {
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}&kl=jp-jp";
        var uri = new Uri(url);
        NetGuard.EnsurePublicHttp(uri); // SSRFガード: http/Https・公開アドレスのみ
        using var resp = await _http.GetAsync(uri, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);
        return Parse(html, topN);
    }

    private async Task<List<WebResult>> PostAsync(string query, int topN, CancellationToken ct)
    {
        var target = new Uri("https://html.duckduckgo.com/html/");
        NetGuard.EnsurePublicHttp(target);
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query, ["kl"] = "jp-jp" });
        using var resp = await _http.PostAsync(target, form, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);
        return Parse(html, topN);
    }

    private static List<WebResult> Parse(string html, int topN)
    {
        var links = LinkRegex().Matches(html).Cast<Match>().ToList();
        var snippets = SnippetRegex().Matches(html).Cast<Match>().ToList();
        var results = new List<WebResult>();
        for (int i = 0; i < links.Count && results.Count < topN; i++)
        {
            var url = WebUtility.HtmlDecode(links[i].Groups[1].Value);
            // DuckDuckGoのリダイレクト形式 //duckduckgo.com/l/?uddg=<encoded> を展開
            var m = Regex.Match(url, @"uddg=([^&]+)");
            if (m.Success) url = Uri.UnescapeDataString(m.Groups[1].Value);
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            var title = StripTags(links[i].Groups[2].Value);
            var snip = i < snippets.Count ? StripTags(snippets[i].Groups[1].Value) : "";
            if (title.Length > 0) results.Add(new WebResult(title, url, snip));
        }
        return results;
    }

    private static string StripTags(string s) => WebUtility.HtmlDecode(TagRegex().Replace(s, "")).Trim();

    /// <summary>検索結果のページ本文を取得してスニペットへ事実を補強する。
    /// SERPスニペットはサイト説明文が多く事実（日付・数値・天気等）を含まないため、
    /// そのまま注入すると回答が検索結果と食い違う原因になる。
    /// 補強する結果はクエリとの関連度順（タイトル・スニペット・URLの語一致）に選ぶ:
    /// SERP順位上位が必ずしも内容の濃いページではないため。
    /// 最関連ページ1件のみ・窓700字で補強する（ctx=2048の予算内に収めるため。
    /// 表組みのページは400字だと見出し行で切れて日付だけが入り、かえって捏造を誘う）。
    /// SPA等で本文が取れない場合は次点のページを試す。失敗時は元スニペットのまま</summary>
    public async Task EnrichAsync(List<WebResult> results, string query, CancellationToken ct = default)
    {
        if (results.Count == 0) return;
        var tokens = Rag.Tokenize(query).ToHashSet();
        var ranked = results.Select((r, i) => (r, i, score: tokens.Count(t => r.Title.Contains(t) || r.Snippet.Contains(t) || r.Url.Contains(t))))
            .OrderByDescending(x => x.score).ThenBy(x => x.i).Take(2).ToList();
        foreach (var (r, i, _) in ranked)
        {
            var enriched = await EnrichOneAsync(r, tokens, 700, ct);
            if (!ReferenceEquals(enriched, r)) { results[i] = enriched; break; } // 1件成功したら打ち切り
        }
    }

    private async Task<WebResult> EnrichOneAsync(WebResult r, IReadOnlySet<string> tokens, int size, CancellationToken ct)
    {
        try
        {
            var text = await FetchPageTextAsync(r.Url, ct);
            if (text.Length == 0) return r;
            // 本文からクエリ関連の最多の窓を抽出（サイト固有構造に依らない汎用の窓選択）
            var excerpt = BestWindow(text, tokens, size);
            if (excerpt.Length < 40) return r; // 抽出できた情報が薄い場合は元のまま
            // {{item.xxx}} 等: JS未レンダリングの雛形断片（SPA）は本文として扱わない
            if (excerpt.Contains("{{")) return r;
            // 抜粋を先頭に置く: 出典プレビューはスニペット先頭の110字を表示するため、
            // サイト説明文ではなく実際の本文が見えるようにする
            return r with { Snippet = excerpt + (r.Snippet.Length > 0 ? "\n" + r.Snippet : "") };
        }
        catch (OperationCanceledException) { return r; }
        catch (Exception) { return r; }
    }

    /// <summary>クエリトークンと最も重なる短い探査窓（約120字）を特定し、その中で最も固有な語
    /// （文中の出現数が最も少ないクエリ語）の出現位置から約size字を返す。
    /// 見出し・ナビが長いページや表組み（文末記号が無く文分割できない）でも関連箇所を拾うため、
    /// 文分割ではなく位置スキャンで選ぶ。見出しから始めると後に本文・表が続く構造が多いため、
    /// 固有語の位置を窓の「先頭」に置く（「天気」等の弱い語がナビに多く出ても巻き込まない）</summary>
    public static string BestWindow(string text, IReadOnlySet<string> tokens, int size)
    {
        text = WhitespaceRegex().Replace(text, " ").Trim();
        if (text.Length <= size) return text;
        const int probe = 120;
        int step = Math.Max(20, probe / 3);
        int bestScore = -1, bestPos = 0;
        for (int pos = 0; pos + probe <= text.Length; pos += step)
        {
            var p = text.Substring(pos, probe);
            int score = tokens.Count(t => p.Contains(t));
            if (score > bestScore) { bestScore = score; bestPos = pos; }
        }
        // 探査窓周辺（±探査窓幅）で最も固有なクエリ語（全文での出現数が最も少ない語）を窓の先頭にする
        int lo = Math.Max(0, bestPos - probe);
        bool FoundNear(string t)
        {
            var i = text.IndexOf(t, lo, StringComparison.Ordinal);
            return i >= 0 && i < bestPos + probe;
        }
        var specific = tokens.Where(FoundNear).OrderBy(CountOfText).FirstOrDefault();
        int anchor = specific is null ? bestPos : text.IndexOf(specific, lo, StringComparison.Ordinal);
        int start = Math.Clamp(anchor, 0, Math.Max(0, text.Length - size));
        return text.Substring(start, Math.Min(size, text.Length - start));

        int CountOfText(string t) => CountOf(text, t);
    }

    private static int CountOf(string text, string t)
    {
        int c = 0, i = 0;
        while ((i = text.IndexOf(t, i, StringComparison.Ordinal)) >= 0) { c++; i += t.Length; }
        return c;
    }

    /// <summary>ページ本文の取得（最大256KB）。script/style等は除去し、文字コードは
    /// レスポンスヘッダ→meta charset の順に判定（対応しない場合はUTF-8）</summary>
    private async Task<string> FetchPageTextAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        NetGuard.EnsurePublicHttp(uri);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode) return "";
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        var buf = new byte[256 * 1024];
        int n = 0, read;
        while (n < buf.Length && (read = await stream.ReadAsync(buf.AsMemory(n, buf.Length - n), cts.Token)) > 0) n += read;
        return HtmlToText(DecodeHtml(buf, n, resp.Content.Headers.ContentType?.CharSet));
    }

    public static string DecodeHtml(byte[] buf, int len, string? headerCharset)
    {
        Encoding? enc = null;
        foreach (var name in new[] { headerCharset, SniffCharset(buf, len) })
        {
            if (string.IsNullOrEmpty(name)) continue;
            try { enc = Encoding.GetEncoding(name); break; } catch (ArgumentException) { }
        }
        return (enc ?? Encoding.UTF8).GetString(buf, 0, len);
    }

    private static string? SniffCharset(byte[] buf, int len)
    {
        var head = Encoding.ASCII.GetString(buf, 0, Math.Min(len, 2048));
        var m = MetaCharsetRegex().Match(head);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string HtmlToText(string html)
    {
        var text = InvisibleBlockRegex().Replace(html, " ");
        text = TagRegex().Replace(text, " ");
        return WebUtility.HtmlDecode(text);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>プロンプト注入用の整形。URLは省く（サイト名はタイトルに含まれる。
    /// ctx=2048の予算対策で、モデルには事実の本文を優先して与える）</summary>
    public static string ToContext(List<WebResult> results)
    {
        // 念のためここでも重複排除（二重防御: 上流で重複が混入しても同一行の反復を防ぐ）
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (!seen.Add(r.Title + "\n" + r.Snippet)) continue;
            sb.Append($"・{r.Title}: {r.Snippet}\n");
        }
        return sb.ToString();
    }
}
