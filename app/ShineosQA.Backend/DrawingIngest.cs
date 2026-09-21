using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>図面判定・表題欄（タイトルブロック）抽出。設計: docs/impl-drawing-search.md §2 T4。
/// 閾値・パターンはPilot先の様式に合わせて調整する前提でここに集約する</summary>
public static partial class DrawingIngest
{
    public const int MaxPages = 3;
    public const int MaxTextChars = 1200;

    /// <summary>図番パターン（例: ST-1042A / A1234 / KB-305-2）。大文字英字始まりを要求して誤検出を抑える。
    /// .NETの\bはCJK隣接で境界にならないため、英数字のlookaroundで境界を表現する</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{1,4}[-_/]?[0-9]{2,6}(?:[-_/][0-9]{1,4})?[A-Z]?(?![A-Za-z0-9])")]
    public static partial Regex ZubanRegex();

    public sealed record DrawingMeta(string? ZubanRaw, string? Hinmei, string? Zairyo, string? Scale, string? Revision, string? ApprovedAt)
    {
        public string? ZubanNorm => ZubanRaw is null ? null : Rag.NormalizeZuban(ZubanRaw);
    }

    /// <summary>図面判定（保守的: 誤検出より取りこぼしを許容。取りこぼれは通常文書として検索可能なまま）</summary>
    public static bool LooksLikeDrawing(string fileName, int pages, string text) =>
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        && pages <= MaxPages
        && text.Length <= MaxTextChars
        && ZubanRegex().IsMatch(text);

    /// <summary>図面シート（表題欄のあるページ）の選択。表紙付きPDF（表紙+図面）で表題欄を表紙から
    /// 読もうとしてLLMが寸法・尺度を図番として返した実図面の誤り（技能検定解答例PDF）を防ぐ。
    /// スコア = 表題欄ラベル3点 + 図番パターン1点。同点は後のページ（表紙は前、図面は後がJIS慣行）</summary>
    public static int PickSheetPage(IReadOnlyList<PdfTextRun> runs, int pages)
    {
        int bestPage = pages, bestScore = -1;
        for (int p = 1; p <= pages; p++)
        {
            var pageRuns = runs.Where(r => r.Page == p).ToList();
            if (pageRuns.Count == 0) continue;
            var text = string.Join("\n", ToLines(pageRuns));
            int score = SheetLabelRegex().Matches(text).Count * 3 + ZubanRegex().Matches(text).Count;
            if (score >= bestScore) { bestScore = score; bestPage = p; }
        }
        return bestPage;
    }

    /// <summary>指定ページの寸法（runs座標から導出。PdfExtractResultは1ページ目の寸法しか保持しないため）</summary>
    public static (float W, float H) PageDims(IReadOnlyList<PdfTextRun> runs, int page)
    {
        var pr = runs.Where(r => r.Page == page).ToList();
        if (pr.Count == 0) return (0, 0);
        return (pr.Max(r => r.X + r.W), pr.Max(r => r.Y + r.H));
    }

    /// <summary>表題欄抽出。渡すrunsは図面シート1ページ分（PickSheetPageで選択したページ。呼び出し側でフィルタ済み）。
    /// JIS慣行の右下領域の行テキストからラベル近接で各欄を取り出し、寸法・尺度等の誤採用を検証で排除する。
    /// 取得できなかった欄はnull（呼び出し側でLLM構造化のフォールバック判定に使う）</summary>
    public static DrawingMeta ExtractTitleBlock(IReadOnlyList<PdfTextRun> runs, float pageWidth, float pageHeight)
    {
        var region = runs
            .Where(r => pageWidth > 0 && pageHeight > 0
                && r.X > pageWidth * 0.55f && r.Y < pageHeight * 0.40f)
            .ToList();
        var lines = ToLines(region);
        string? zuban = FindZuban(lines);
        if (!IsPlausibleZuban(zuban)) zuban = null;
        var hinmei = ValueAfterLabel(lines, "品名", "名称", "TITLE");
        var zairyo = ValueAfterLabel(lines, "材質", "材料", "MATERIAL");
        var scale = ValueAfterLabel(lines, "縮尺", "尺度", "SCALE");
        var revision = RevisionOf(lines, zuban);
        var approvedAt = ValueAfterLabel(lines, "承認", "日付", "DATE");
        return new DrawingMeta(
            ZubanRaw: zuban,
            Hinmei: IsPlausibleHinmei(hinmei) ? hinmei : null,
            Zairyo: IsPlausibleZairyo(zairyo) ? zairyo : null,
            Scale: IsPlausibleScale(scale) ? scale : null,
            Revision: IsPlausibleRevision(revision) ? revision : null,
            ApprovedAt: string.IsNullOrWhiteSpace(approvedAt) ? null : approvedAt.Trim());
    }

    // ---- 表題欄値の検証（実図面で発生した誤抽出の根本対策: ルール・LLM双方の出力に適用する） ----

    [GeneratedRegex(@"^\d+(?:\.\d+)?\s*[:／/=＝]\s*\d+(?:\.\d+)?[a-zA-Z]?$")]
    private static partial Regex ScaleRegex();

    [GeneratedRegex("図番|品名|材質|材料|尺度|縮尺|改訂|投影法|承認|DWG|TITLE|MATERIAL|SCALE|REV")]
    private static partial Regex SheetLabelRegex();

    /// <summary>図番らしさ: 図番パターンに合致すること。LLMが尺度「1:1」を図番として返した実図面の誤りを排除</summary>
    public static bool IsPlausibleZuban(string? v) =>
        !string.IsNullOrWhiteSpace(v) && ZubanRegex().IsMatch(v) && !ScaleRegex().IsMatch(v.Trim());

    /// <summary>品名らしさ: 寸法・呼び・仕上げ記号のみの表記（Ø160、M6×15/Ø4.8×20 等）は品名になり得ない。
    /// LLMが寸法値を品名として返した実図面の誤りを排除する</summary>
    public static bool IsPlausibleHinmei(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var t = v.Trim();
        if (t.Length < 2 || t.Length > 40) return false;
        // 寸法表記の文字種（数字・空白・寸法記号・呼び記号）だけで構成される文字列は品名ではない
        bool dimensionOnly = t.All(c => char.IsAsciiDigit(c) || char.IsWhiteSpace(c)
            || "Øφ⌀°.,、×xX*CMR-+/()（）".Contains(c));
        if (dimensionOnly) return false;
        return t.Any(c => char.IsLetter(c) || c >= 0x3000); // 文字（かな漢字・英字）を1つは含む
    }

    /// <summary>材質らしさ: 数値開始・寸法記号・ねじ規格（Rc1/16 等）・寸法対（20×15）を排除。
    /// LLMが「8 Ø126 Rc1/16」を材質として返した実図面の誤りを排除する</summary>
    public static bool IsPlausibleZairyo(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var t = v.Trim();
        if (t.Length > 24) return false;
        if (t.Contains('Ø') || t.Contains('φ') || t.Contains('⌀') || t.Contains('°')) return false;
        if (char.IsAsciiDigit(t[0])) return false;
        if (ThreadSpecRegex().IsMatch(t) || DimPairRegex().IsMatch(t)) return false;
        return t.Any(c => char.IsLetter(c) || c >= 0x3000);
    }

    [GeneratedRegex(@"(^|\s)(Rc|R|G|NPT|PT|PF)\s?[0-9]")]
    private static partial Regex ThreadSpecRegex();

    [GeneratedRegex(@"[0-9]\s?[x×]\s?[0-9]")]
    private static partial Regex DimPairRegex();

    public static bool IsPlausibleScale(string? v) => v != null && ScaleRegex().IsMatch(v.Trim());

    public static bool IsPlausibleRevision(string? v) => v != null && System.Text.RegularExpressions.Regex.IsMatch(v, "^[A-Z][0-9]?$");

    [GeneratedRegex(@"^(SS[0-9]|SUS[0-9]*|S45C|S50C|S55C|SM[0-9A-Z]+|SCM[0-9]*|FC[DT]?[0-9]*|FCD[0-9]*|A5052|A6061|A6063|AC[0-9A-Z]+|C[0-9]{4}|AL)[0-9A-Za-z\- ]*$")]
    private static partial Regex MaterialTokenRegex();

    /// <summary>材質トークンの判定（SS400・SUS304 等）。LLMが材質を品名フィールドに返す誤りを実図面検証で
    /// 確認したため、品名の検証でこれを排除する（「SS400ブラケット」等の複合語は排除しない）</summary>
    public static bool IsMaterialToken(string? v) => v != null && MaterialTokenRegex().IsMatch(v.Trim());

    /// <summary>チャンク前置き。出典表示とRAGコンテキストが図面情報を自然に運ぶ（ChatFlowの出典正規化はFile名ベースのため変更不要）。
    /// 抽出できた欄のみを載せる（「不明」の断言も誤りの一種。実図面検証cr05: スキャン図面の「尺度: 不明」が
    /// ベクトル図面の「尺度: 1:1」への回答を妨げた）。尺度和らメタは前置きに含め、ノイズの多い本体テキストに頼らない</summary>
    public static string BuildChunkText(DrawingMeta m, string fullText)
    {
        var parts = new List<string>();
        if (m.ZubanRaw != null) parts.Add($"図番: {m.ZubanRaw}");
        if (m.Hinmei != null) parts.Add($"品名: {m.Hinmei}");
        if (m.Zairyo != null) parts.Add($"材質: {m.Zairyo}");
        if (m.Revision != null) parts.Add($"改訂: {m.Revision}");
        if (m.Scale != null) parts.Add($"尺度: {m.Scale}");
        var head = parts.Count > 0 ? $"【図面】{string.Join(" / ", parts)}" : "【図面】";
        return $"{head}\n{fullText.Trim()}";
    }

    // ---- 内部 ----

    public static List<string> ToLines(IEnumerable<PdfTextRun> runs)
    {
        return runs
            .GroupBy(r => Math.Round(r.Y / 3f)) // ベースラインYを3ptバケットに行化
            .Select(g => string.Join(" ", g.OrderBy(r => r.X).Select(r => r.Text)).Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string? FindZuban(List<string> lines)
    {
        // ラベル行（図番/No./DWG）の同じ行または直後2行の図番パターン → なければ領域全体の最初の一致
        for (int i = 0; i < lines.Count; i++)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"図番|Drawing\s*No|DWG|No\."))
                for (int j = i; j < Math.Min(i + 3, lines.Count); j++)
                {
                    var m = ZubanRegex().Match(lines[j]);
                    if (m.Success) return m.Value;
                }
        }
        return lines.Select(l => ZubanRegex().Match(l)).FirstOrDefault(m => m.Success)?.Value;
    }

    private static string? ValueAfterLabel(List<string> lines, params string[] labels)
    {
        foreach (var line in lines)
            foreach (var label in labels)
            {
                var idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var value = line[(idx + label.Length)..].Trim(' ', ':', '：', '\u3000');
                if (value.Length > 0) return value;
            }
        return null;
    }

    private static string? RevisionOf(List<string> lines, string? zuban)
    {
        // 改訂ラベル行の末尾1文字（A-Z）。図番末尾のサフィックス(A)は改訂と紛らわしいので、
        // ラベルによる取り出しを優先し、なければ「改訂」表記の隣接1文字のみを採用
        foreach (var line in lines)
        {
            var idx = line.IndexOf("改訂", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = line.IndexOf("REV", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var value = line[(idx + 2)..].Trim(' ', ':', '：', '\u3000');
            var m = System.Text.RegularExpressions.Regex.Match(value, "^[A-Z]");
            if (m.Success) return m.Value;
        }
        return null;
    }
}
