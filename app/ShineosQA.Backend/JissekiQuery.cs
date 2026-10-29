using System.Text;
using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>実績表（jisseki_meta）からの決定論回答。図番で引ける見積・原価・納期を、
/// モデルの要約を挟まずそのまま返す（金額・納期の取り違え防止）。
/// Phase Dで列型・集計（数量/合計/平均）を追加するまでの最小実装。</summary>
public static partial class JissekiQuery
{
    private sealed record Row(long FileId, string FileName, int RowNo, string? ZubanRaw,
        string? Mitumori, string? Genka, string? Nouki);

    [GeneratedRegex(@"見積|見積額|見積金額", RegexOptions.Compiled)]
    private static partial Regex MitumoriRegex();

    [GeneratedRegex(@"原価|コスト|仕入", RegexOptions.Compiled)]
    private static partial Regex GenkaRegex();

    [GeneratedRegex(@"納期|納入", RegexOptions.Compiled)]
    private static partial Regex NoukiRegex();

    public static bool TryAnswer(
        Db db, string message, string zubanNorm,
        out string answer, out List<ChatFlow.SourceInfo> sources, out double confidence)
    {
        answer = "";
        sources = new();
        confidence = 0;

        var rows = db.Query(
            "SELECT j.file_id AS file_id, f.name AS file_name, j.row_no AS row_no, " +
            "       j.zuban_raw AS zuban_raw, j.mitumori AS mitumori, j.genka AS genka, j.nouki AS nouki " +
            "FROM jisseki_meta j JOIN files f ON f.file_id=j.file_id " +
            "WHERE j.zuban_norm=$z AND f.status='ready' ORDER BY j.row_no",
            ("$z", zubanNorm))
            .Select(r => new Row(
                Convert.ToInt64(r["file_id"] ?? 0L),
                Convert.ToString(r["file_name"]) ?? "",
                Convert.ToInt32(r["row_no"] ?? 0),
                Nullable(r["zuban_raw"]), Nullable(r["mitumori"]),
                Nullable(r["genka"]), Nullable(r["nouki"])))
            .Where(r => r.FileId > 0)
            .ToList();
        if (rows.Count == 0) return false;

        var metrics = new List<(string Key, string Label)>();
        if (MitumoriRegex().IsMatch(message)) metrics.Add(("mitumori", "見積"));
        if (GenkaRegex().IsMatch(message)) metrics.Add(("genka", "原価"));
        if (NoukiRegex().IsMatch(message)) metrics.Add(("nouki", "納期"));
        if (metrics.Count == 0) metrics.AddRange(new[]
        {
            ("mitumori", "見積"), ("genka", "原価"), ("nouki", "納期")
        });

        var sb = new StringBuilder();
        sb.Append($"図番 {rows[0].ZubanRaw ?? zubanNorm} の実績");
        if (rows.Count > 1) sb.Append($"（{rows.Count}件）");
        sb.Append(":");
        foreach (var r in rows)
        {
            sb.Append("\n- ");
            if (rows.Count > 1) sb.Append($"行{r.RowNo}: ");
            var parts = new List<string>();
            foreach (var (key, label) in metrics)
            {
                var v = key switch
                {
                    "mitumori" => r.Mitumori,
                    "genka" => r.Genka,
                    "nouki" => r.Nouki,
                    _ => null,
                };
                parts.Add($"{label} {Display(v)}");
            }
            sb.Append(string.Join(" / ", parts));
        }
        answer = sb.ToString();
        sources = rows.Select(r => new ChatFlow.SourceInfo
        {
            File = r.FileName,
            Snippet = $"図番 {r.ZubanRaw} / 見積 {Display(r.Mitumori)} / 原価 {Display(r.Genka)} / 納期 {Display(r.Nouki)}",
            Text = $"図番 {r.ZubanRaw} / 見積 {r.Mitumori} / 原価 {r.Genka} / 納期 {r.Nouki}",
            Kind = "kb",
            FileId = r.FileId,
        }).ToList();
        confidence = rows.Count == 1 && metrics.Count == 1 ? 1.0 : 0.85;
        return true;
    }

    private static string Display(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "登録なし" : v!.Trim();

    private static string? Nullable(object? v)
    {
        var s = Convert.ToString(v);
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
