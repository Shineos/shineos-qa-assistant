using System.Text;
using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>RAG検索・プロンプト構築（実測済みv2-fastパイプラインの忠実実装）。
/// 根拠: docs/latency-verification.md（スニペット240字・リランクtop8→2・ガード閾値-2.0・キャッシュ閾値0.97）</summary>
public static partial class Rag
{
    public const double GuardThreshold = -5.0;      // NO-HIT判定（nohit実測≤-7.8、口語表現の関連質問が-2前後になり得るため-5.0に設定。hit誤ガード< nohit分離を両立）
    public const double AnswerCacheCos = 0.97;      // 回答キャッシュ判定
    public const int SnippetMaxChars = 240;         // 事実切断を防ぐ関連文中心スニペット
    public const int RerankPool = 8;                // top8未満だと関連chunkが候補外に漏れる（実測）
    public const double RerankSkipCos = 0.62;       // 高信頼ショートカット: ベクトル一致が強ければリランク(~2秒)を省略
    public const double RerankSkipKw = 0.10;        // かつキーワード一致もある場合のみ（実測: hit問cos0.68-0.75 / nohit0.46）
    public const string PromptVersion = "2026-09-14-quality-tier"; // プロンプト/キャッシュ仕様変更時は回答キャッシュを無効化

    [GeneratedRegex(@"[^。．.\n]+[。．.]?")]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"[\u3040-\u30FF\u4E00-\u9FFF]+|[A-Za-z0-9]+")]
    private static partial Regex TokenRegex();

    /// <summary>日本語バイグラム＋英数字トークン（検証済みトークナイザと同一仕様）</summary>
    public static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        foreach (var m in TokenRegex().Matches(s).Cast<Match>())
        {
            var v = m.Value;
            bool isCjk = v.Length > 0 && v.All(ch => ch >= 0x3040 && ch <= 0x9FFF);
            if (isCjk)
            {
                if (v.Length == 1) tokens.Add(v);
                else for (int i = 0; i < v.Length - 1; i++) tokens.Add(v.Substring(i, 2));
            }
            else tokens.Add(v.ToLowerInvariant());
        }
        return tokens;
    }

    /// <summary>文末境界チャンカー（350字/オーバーラップ50）</summary>
    public static List<string> Chunk(string text, int target = 350, int overlap = 50)
    {
        var chunks = new List<string>();
        var cur = new StringBuilder();
        foreach (var m in SentenceRegex().Matches(text).Cast<Match>())
        {
            var s = m.Value.Trim();
            if (s.Length == 0) continue;
            if (cur.Length + s.Length > target && cur.Length > 0)
            {
                chunks.Add(cur.ToString().Trim());
                var tail = cur.Length > overlap ? cur.ToString(cur.Length - overlap, overlap) : "";
                cur.Clear(); cur.Append(tail);
            }
            cur.Append(s);
        }
        if (cur.ToString().Trim().Length > 0) chunks.Add(cur.ToString().Trim());
        return chunks;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / Math.Sqrt(na * nb);
    }

    /// <summary>クエリ関連文を中心としたスニペット抽出。
    /// 語彙が重ならない場合（例: 日本語質問↔英語PDF）は関連文を特定できないため全文を返す
    /// （スニペット化による誤ガード回帰の防止: latency-verification §5）</summary>
    public static string Snippet(string text, IReadOnlySet<string> queryTokens, int max = SnippetMaxChars)
    {
        if (text.Length <= max) return text;
        var sents = SentenceRegex().Matches(text).Cast<Match>().Select(m => m.Value).ToArray();
        if (sents.Length == 0) return text.Substring(0, max);
        int bestI = 0, bestScore = 0;
        for (int i = 0; i < sents.Length; i++)
        {
            var sTok = Tokenize(sents[i]).ToHashSet();
            int sc = queryTokens.Count(t => sTok.Contains(t));
            if (sc > bestScore) { bestScore = sc; bestI = i; }
        }
        if (bestScore == 0) return text; // 重複なし→クロスリンガル等→全文
        int lo = bestI, hi = bestI;
        var sb = new StringBuilder(sents[bestI]);
        while (sb.Length < max)
        {
            if (hi + 1 < sents.Length && sb.Length + sents[hi + 1].Length <= max) { hi++; sb.Append(sents[hi]); }
            else if (lo - 1 >= 0 && sb.Length + sents[lo - 1].Length <= max) { lo--; sb.Insert(0, sents[lo]); }
            else break;
        }
        return sb.ToString();
    }

    // 出典の列挙はバックエンドが生成後に正規化して保証するため、プロンプトには含めない
    public static readonly string SystemPrompt =
        "あなたは社内文書を主な根拠とするQ&Aアシスタント。日本語で結論から答える。" +
        "短い質問は1〜2文の文章で。手順・条件・金額など複数項目の長い回答のみ箇条書き（- ）と改行で整理（手段ごとの条件を混同しない）。" +
        "社内文書になければ【参照情報】のWeb検索結果から回答してよい（根拠のサイト名を示す）。それにも無ければ「Web検索の結果からは具体的な情報が得られませんでした。最新の情報は各サイトをご確認ください」と伝える。" +
        "どちらにもなければ「該当する記載がありません」。部分該当は該当部分のみ。推測と一般知識は禁止。金額・日付・回数は文書どおり正確に。" +
        "承認者・期限など「○○の場合は△△」という条件と対象の対応は文書の記載どおり正確に答え、類似する別条件と混同しないこと。";

    public static string BuildContext(IReadOnlyList<(string doc, string snippet)> docs, string? webContext)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var d in docs) sb.Append($"【文書{++i}: {d.doc}】{d.snippet}\n");
        if (!string.IsNullOrEmpty(webContext)) sb.Append($"【Web検索結果】{webContext}\n");
        return sb.ToString();
    }
}

/// <summary>メモリ上のチャンク索引（起動時にDBからロード、取り込み時に更新）</summary>
public sealed class ChunkIndex
{
    public sealed record Rec(long Id, long FileId, string FileName, int Seq, string Text, float[] Emb, HashSet<string> Tokens);

    private readonly List<Rec> _list = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _list.Count; } }

    public void LoadFrom(Db db)
    {
        var rows = db.LoadReadyChunks();
        lock (_lock) _list.Clear();
        foreach (var row in rows)
        {
            var emb = BytesToFloats(row.Emb);
            var rec = new Rec(row.ChunkId, row.FileId, row.FileName, row.Seq, row.Text, emb, Rag.Tokenize(row.Text).ToHashSet());
            lock (_lock) _list.Add(rec);
        }
    }

    public void AddRange(string fileName, long fileId, IEnumerable<(int seq, string text, float[] emb)> items)
    {
        lock (_lock)
        {
            foreach (var (seq, text, emb) in items)
                _list.Add(new Rec(0, fileId, fileName, seq, text, emb, Rag.Tokenize(text).ToHashSet()));
        }
    }

    public void RemoveFile(long fileId)
    {
        lock (_lock) _list.RemoveAll(r => r.FileId == fileId);
    }

    /// <summary>同一文書の次チャンク（seq+1）の本文。手順・条項のチャンク境界分断対策用</summary>
    public string? NextChunkText(long fileId, int seq)
    {
        lock (_lock) return _list.FirstOrDefault(r => r.FileId == fileId && r.Seq == seq + 1)?.Text;
    }

    public sealed class HybridHit { public Rec Rec = null!; public double Cos, Kw, Hybrid; }

    /// <summary>ハイブリッド検索（ベクトル0.5＋キーワード0.5・min-max正規化）</summary>
    public List<HybridHit> Search(float[] queryEmb, IReadOnlySet<string> queryTokens, int topK)
    {
        List<HybridHit> hits;
        lock (_lock) hits = _list.Select(r => new HybridHit { Rec = r, Cos = Rag.Cosine(queryEmb, r.Emb), Kw = 0 }).ToList();
        if (hits.Count == 0) return hits;
        foreach (var h in hits)
        {
            int matches = queryTokens.Count(t => h.Rec.Tokens.Contains(t));
            h.Kw = (double)matches / Math.Max(1, queryTokens.Count);
        }
        double maxCos = hits.Max(h => h.Cos), minCos = hits.Min(h => h.Cos);
        foreach (var h in hits)
        {
            var ncos = maxCos > minCos ? (h.Cos - minCos) / (maxCos - minCos) : 0;
            h.Hybrid = 0.5 * ncos + 0.5 * h.Kw;
        }
        return hits.OrderByDescending(h => h.Hybrid).Take(topK).ToList();
    }

    public static byte[] FloatsToBytes(float[] f)
    {
        var b = new byte[f.Length * 4];
        for (int i = 0; i < f.Length; i++) Array.Copy(BitConverter.GetBytes(f[i]), 0, b, i * 4, 4);
        return b;
    }

    public static float[] BytesToFloats(byte[] b)
    {
        var f = new float[b.Length / 4];
        for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToSingle(b, i * 4);
        return f;
    }
}
