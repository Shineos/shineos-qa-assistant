using System.Text;
using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>同一図番の改訂差分を drawing_meta から決定論的に返す。
/// 「改訂AとBの違いは？」「改訂Bの変更点は？」を、LLMの推測ではなく表題欄の値で比較する。
/// 注記・寸法の差分は表題欄に無いため対象外（Phase Dで本文差分を追加）。</summary>
public static partial class DrawingCompareQuery
{
    private sealed record Row(long FileId, string FileName, string? ZubanRaw, string? Hinmei,
        string? Zairyo, string? Scale, string? Revision, string? ApprovedAt);

    [GeneratedRegex(@"改訂\s*([A-Za-z0-9]+)\s*(?:と|/|・|,|、)\s*(?:改訂)?\s*([A-Za-z0-9]+)", RegexOptions.Compiled)]
    private static partial Regex TwoRevisionsRegex();

    [GeneratedRegex(@"改訂\s*([A-Za-z0-9]+)|第?\s*([0-9]{1,3})\s*版", RegexOptions.Compiled)]
    private static partial Regex RevisionRegex();

    public static bool TryAnswer(Db db, string message, string zubanNorm,
        out string answer, out List<ChatFlow.SourceInfo> sources, out double confidence)
    {
        answer = "";
        sources = new();
        confidence = 0;

        var rows = db.Query(
            "SELECT f.file_id AS file_id, f.name AS file_name, d.zuban_raw AS zuban_raw, " +
            "       d.hinmei AS hinmei, d.zairyo AS zairyo, d.scale AS scale, " +
            "       d.revision AS revision, d.approved_at AS approved_at " +
            "FROM drawing_meta d JOIN files f ON f.file_id=d.file_id " +
            "WHERE d.zuban_norm=$z AND f.status='ready' ORDER BY d.revision, d.file_id",
            ("$z", zubanNorm))
            .Select(r => new Row(
                Convert.ToInt64(r["file_id"] ?? 0L),
                Convert.ToString(r["file_name"]) ?? "",
                Nullable(r["zuban_raw"]), Nullable(r["hinmei"]), Nullable(r["zairyo"]),
                Nullable(r["scale"]), Nullable(r["revision"]), Nullable(r["approved_at"])))
            .Where(r => r.FileId > 0)
            .ToList();
        if (rows.Count < 2) return false;

        // 質問に2つの改訂指定（「改訂AとB」「改訂A/B」）があればそれを使う。
        // 1つだけなら、その改訂と直前の改訂を比較する（「改訂Bの変更点」）。
        Row left, right;
        var two = TwoRevisionsRegex().Match(message);
        if (two.Success)
        {
            var a = FindRevision(rows, two.Groups[1].Value);
            var b = FindRevision(rows, two.Groups[2].Value);
            if (a is null || b is null)
            {
                answer = $"指定された改訂（{two.Groups[1].Value} / {two.Groups[2].Value}）の図面が登録されていません。";
                sources = rows.Select(ToSource).ToList();
                confidence = 0.5;
                return true;
            }
            left = a; right = b;
        }
        else
        {
            var rev = RevisionRegex().Match(message);
            if (rev.Success)
            {
                var target = FindRevision(rows, rev.Groups[1].Success ? rev.Groups[1].Value : rev.Groups[2].Value);
                if (target is null)
                {
                    answer = $"指定された改訂（{rev.Value.Trim()}）の図面が登録されていません。";
                    sources = rows.Select(ToSource).ToList();
                    confidence = 0.5;
                    return true;
                }
                var idx = rows.IndexOf(target);
                if (idx <= 0)
                {
                    answer = $"改訂 {target.Revision} は最初の改訂のため、比較対象の前改訂がありません。";
                    sources = new List<ChatFlow.SourceInfo> { ToSource(target) };
                    confidence = 0.7;
                    return true;
                }
                left = rows[idx - 1]; right = target;
            }
            else
            {
                // 指定なし: 2件ならその2件、3件以上なら最新2件を比較（候補が多い場合はconfidenceを下げる）
                left = rows[^2]; right = rows[^1];
            }
        }

        var diffs = new List<string>();
        var same = new List<string>();
        AddDiff(diffs, same, "品名", left.Hinmei, right.Hinmei);
        AddDiff(diffs, same, "材質", left.Zairyo, right.Zairyo);
        AddDiff(diffs, same, "尺度", left.Scale, right.Scale);
        AddDiff(diffs, same, "承認日", left.ApprovedAt, right.ApprovedAt);

        var sb = new StringBuilder();
        sb.Append($"図番 {left.ZubanRaw ?? zubanNorm} の改訂 {DisplayRev(left.Revision)} と {DisplayRev(right.Revision)} の差分:");
        if (diffs.Count == 0) sb.Append("\n- 表題欄の項目に変更はありません。");
        else foreach (var d in diffs) sb.Append("\n- ").Append(d);
        if (same.Count > 0) sb.Append("\n- 変更なし: ").Append(string.Join("、", same));
        answer = sb.ToString();
        sources = new List<ChatFlow.SourceInfo> { ToSource(left), ToSource(right) };
        confidence = rows.Count == 2 ? 1.0 : 0.8;
        return true;
    }

    private static Row? FindRevision(List<Row> rows, string rev)
    {
        var want = NormalizeRevision(rev);
        return rows.FirstOrDefault(r => NormalizeRevision(r.Revision) == want);
    }

    private static void AddDiff(List<string> diffs, List<string> same, string label, string? a, string? b)
    {
        var va = Display(a);
        var vb = Display(b);
        if (string.Equals(va, vb, StringComparison.Ordinal)) same.Add(label);
        else diffs.Add($"{label}: {va} → {vb}");
    }

    private static ChatFlow.SourceInfo ToSource(Row r)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.ZubanRaw)) text.Append($"図番: {r.ZubanRaw}\n");
        if (!string.IsNullOrWhiteSpace(r.Hinmei)) text.Append($"品名: {r.Hinmei}\n");
        if (!string.IsNullOrWhiteSpace(r.Zairyo)) text.Append($"材質: {r.Zairyo}\n");
        if (!string.IsNullOrWhiteSpace(r.Scale)) text.Append($"尺度: {r.Scale}\n");
        if (!string.IsNullOrWhiteSpace(r.Revision)) text.Append($"改訂: {r.Revision}\n");
        if (!string.IsNullOrWhiteSpace(r.ApprovedAt)) text.Append($"承認日: {r.ApprovedAt}\n");
        return new ChatFlow.SourceInfo
        {
            File = r.FileName,
            Snippet = text.ToString().Trim(),
            Text = text.ToString().Trim(),
            Kind = "kb",
            FileId = r.FileId,
            IsDrawing = true,
            Zuban = r.ZubanRaw,
            Hinmei = r.Hinmei,
            Revision = r.Revision,
        };
    }

    private static string Display(string? v) => string.IsNullOrWhiteSpace(v) ? "（なし）" : v!.Trim();
    private static string DisplayRev(string? v) => string.IsNullOrWhiteSpace(v) ? "（なし）" : v!.Trim();

    private static string? Nullable(object? v)
    {
        var s = Convert.ToString(v);
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static string NormalizeRevision(string? rev)
    {
        if (string.IsNullOrWhiteSpace(rev)) return "";
        var t = rev.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(t.Length);
        foreach (var c in t) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}
