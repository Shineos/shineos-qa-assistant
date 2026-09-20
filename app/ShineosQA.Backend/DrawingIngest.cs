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

    /// <summary>表題欄抽出。ページ1右下領域（JIS慣行）の行テキストからラベル近接で各欄を取り出す。
    /// 取得できなかった欄はnull（呼び出し側でLLM構造化のフォールバック判定に使う）</summary>
    public static DrawingMeta ExtractTitleBlock(IReadOnlyList<PdfTextRun> runs, float pageWidth, float pageHeight)
    {
        var region = runs
            .Where(r => r.Page == 1 && pageWidth > 0 && pageHeight > 0
                && r.X > pageWidth * 0.55f && r.Y < pageHeight * 0.40f)
            .ToList();
        var lines = ToLines(region);
        string? zuban = FindZuban(lines);
        return new DrawingMeta(
            ZubanRaw: zuban,
            Hinmei: ValueAfterLabel(lines, "品名", "名称", "TITLE"),
            Zairyo: ValueAfterLabel(lines, "材質", "材料", "MATERIAL"),
            Scale: ValueAfterLabel(lines, "縮尺", "SCALE"),
            Revision: RevisionOf(lines, zuban),
            ApprovedAt: ValueAfterLabel(lines, "承認", "日付", "DATE"));
    }

    /// <summary>チャンク前置き。出典表示とRAGコンテキストが図面情報を自然に運ぶ（ChatFlowの出典正規化はFile名ベースのため変更不要）</summary>
    public static string BuildChunkText(DrawingMeta m, string fullText) =>
        $"【図面】図番: {m.ZubanRaw ?? "不明"} / 品名: {m.Hinmei ?? "不明"} / 材質: {m.Zairyo ?? "不明"} / 改訂: {m.Revision ?? "-"}\n{fullText.Trim()}";

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
