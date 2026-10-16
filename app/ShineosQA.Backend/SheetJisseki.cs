using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>実績表（Excel/CSV）からの図番キー実績抽出（v2.3.0・拡張機能「図面QA」の実績接続）。
/// 製造業の見積・原価・納期の実績表をナレッジに取り込んだ際、図番をキーとした辞書を構築し、
/// 図面QAの回答文脈へ過去実績を接続できるようにする（支払対象: 図面×実績の接続）。
/// ヘッダ行のラベルゆれ（見積額/見積金額 等）を正規表現で吸収。図番列が無いシートはスキップ。
/// 図番の照合は drawing_meta と同じ NormalizeZuban 正規形（表記ゆれ同一視）</summary>
public static partial class SheetJisseki
{
    public sealed record JissekiRow(int RowNo, string? ZubanRaw, string? ZubanNorm,
        string? Mitumori, string? Genka, string? Nouki);

    // ヘッダラベルの分類。図番は「図番/品番/型番」を含む列、見積は「見積」、原価は「原価/仕入」、納期は「納期/納入」
    [GeneratedRegex(@"図番|品番|型番")]
    private static partial Regex ZubanHeader();

    [GeneratedRegex(@"見積")]
    private static partial Regex MitumoriHeader();

    [GeneratedRegex(@"原価|仕入")]
    private static partial Regex GenkaHeader();

    [GeneratedRegex(@"納期|納入")]
    private static partial Regex NoukiHeader();

    /// <summary>ヘッダ行から列の種別を分類する。戻り値: (図番列, 見積列, 原価列, 納期列)。未検出は -1</summary>
    public static (int Zuban, int Mitumori, int Genka, int Nouki) MapColumns(string[] header)
    {
        int zuban = -1, mitumori = -1, genka = -1, nouki = -1;
        for (int i = 0; i < header.Length; i++)
        {
            var h = (header[i] ?? "").Trim();
            if (h.Length == 0) continue;
            if (zuban < 0 && ZubanHeader().IsMatch(h)) zuban = i;
            else if (mitumori < 0 && MitumoriHeader().IsMatch(h)) mitumori = i;
            else if (genka < 0 && GenkaHeader().IsMatch(h)) genka = i;
            else if (nouki < 0 && NoukiHeader().IsMatch(h)) nouki = i;
        }
        return (zuban, mitumori, genka, nouki);
    }

    /// <summary>実績表の生行から図番キーの実績行を抽出する。図番列が無いシートはスキップ。
    /// 図番は NormalizeZuban 正規形で辞書化（drawing_meta と同一照合）。抽出上限は SheetExtract.MaxRows</summary>
    public static List<JissekiRow> ExtractRows(List<(string SheetName, string[][] Rows)> sheets)
    {
        var result = new List<JissekiRow>();
        int rowNo = 0;
        foreach (var (_, rows) in sheets)
        {
            if (rows.Length < 2) continue; // ヘッダのみ
            var (zuban, mitumori, genka, nouki) = MapColumns(rows[0]);
            if (zuban < 0) continue; // 図番キー列が無いシートは実績辞書の対象外
            for (int r = 1; r < rows.Length && rowNo < SheetExtract.MaxRows; r++)
            {
                var cells = rows[r];
                var raw = Cell(cells, zuban);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var norm = Rag.NormalizeZuban(raw);
                if (norm.Length == 0) continue;
                result.Add(new JissekiRow(rowNo++, raw, norm,
                    Cell(cells, mitumori), Cell(cells, genka), Cell(cells, nouki)));
            }
        }
        return result;
    }

    /// <summary>実績行の整形（回答文脈への注入用）。値が空の項目は省略</summary>
    public static string FormatJisseki(string? zubanRaw, string? mitumori, string? genka, string? nouki)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(mitumori)) parts.Add($"過去見積 {mitumori}");
        if (!string.IsNullOrWhiteSpace(genka)) parts.Add($"原価 {genka}");
        if (!string.IsNullOrWhiteSpace(nouki)) parts.Add($"納期 {nouki}");
        return $"図番 {zubanRaw}: " + string.Join(" / ", parts);
    }

    private static string? Cell(string[] cells, int idx) =>
        idx >= 0 && idx < cells.Length ? cells[idx]?.Trim() : null;
}
