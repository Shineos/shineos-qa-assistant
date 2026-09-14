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

    public async Task<List<WebResult>> SearchAsync(string query, int topN = 4, CancellationToken ct = default)
    {
        // GET（実測で安定）→ 1s待ちPOST → 2s待ちGET の順にリトライ
        Exception? lastErr = null;
        foreach (var (method, delayMs) in new[] { ("GET", 0), ("POST", 1000), ("GET", 2000) })
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            try
            {
                var results = method == "GET" ? await GetAsync(query, topN, ct) : await PostAsync(query, topN, ct);
                if (results.Count > 0) return results;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastErr = ex; }
        }
        if (lastErr != null) throw lastErr;
        return new List<WebResult>();
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

    /// <summary>プロンプト注入用の整形</summary>
    public static string ToContext(List<WebResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results) sb.Append($"・{r.Title}（{r.Url}）: {r.Snippet}\n");
        return sb.ToString();
    }
}
