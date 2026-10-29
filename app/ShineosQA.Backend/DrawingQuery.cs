using System.Text;
using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>図面台帳（drawing_meta）からの決定論回答。LLMを使わないため、
/// 図番・材質・改訂の取り違えが構造的に起きない。表題欄に無い質問（公差・注記・寸法など）は
/// IntentRouter が DrawingMeta にしないため、ここへは来ない。
/// 複数改訂が登録されている場合は値を混ぜず、改訂ごとに分けて返す（同一図番の混在防止）。</summary>
public static partial class DrawingQuery
{
    private sealed record Row(
        long FileId, string FileName, string? ZubanRaw, string? ZubanNorm,
        string? Hinmei, string? Zairyo, string? Scale, string? Revision, string? ApprovedAt);

    [GeneratedRegex(@"改訂\s*([A-Za-z0-9]+)|第?\s*([0-9]{1,3})\s*版", RegexOptions.Compiled)]
    private static partial Regex RevisionInQuestionRegex();

    public static bool TryAnswer(
        Db db, string message, string zubanNorm,
        out string answer, out List<ChatFlow.SourceInfo> sources, out double confidence)
    {
        answer = "";
        sources = new();
        confidence = 0;

        var rows = db.Query(
            "SELECT f.file_id AS file_id, f.name AS file_name, d.zuban_raw AS zuban_raw, " +
            "       d.zuban_norm AS zuban_norm, d.hinmei AS hinmei, d.zairyo AS zairyo, " +
            "       d.scale AS scale, d.revision AS revision, d.approved_at AS approved_at " +
            "FROM drawing_meta d JOIN files f ON f.file_id=d.file_id " +
            "WHERE d.zuban_norm=$z AND f.status='ready' ORDER BY d.revision, d.file_id",
            ("$z", zubanNorm))
            .Select(r => new Row(
                Convert.ToInt64(r["file_id"] ?? 0L),
                Convert.ToString(r["file_name"]) ?? "",
                Nullable(r["zuban_raw"]), Nullable(r["zuban_norm"]),
                Nullable(r["hinmei"]), Nullable(r["zairyo"]),
                Nullable(r["scale"]), Nullable(r["revision"]), Nullable(r["approved_at"])))
            .Where(r => r.FileId > 0)
            .ToList();
        if (rows.Count == 0) return false;

        // 質問に改訂指定があれば絞る。指定が存在しない場合は「未登録」を明示する
        var revReq = RevisionInQuestionRegex().Match(message);
        if (revReq.Success)
        {
            var want = NormalizeRevision(revReq.Groups[1].Success ? revReq.Groups[1].Value : revReq.Groups[2].Value);
            var filtered = rows.Where(r => NormalizeRevision(r.Revision) == want).ToList();
            if (filtered.Count == 0)
            {
                answer = $"図番 {DisplayZuban(rows[0])} の改訂 {revReq.Value.Trim()} は登録されていません。";
                sources = rows.Select(ToSource).ToList();
                confidence = 0.5;
                return true;
            }
            rows = filtered;
        }

        var attrs = IntentRouter.FindDrawingAttributes(message);
        if (attrs.Count == 0)
            attrs = new List<DrawingAttribute> { new("zuban_raw", "図番") };

        var sb = new StringBuilder();
        if (rows.Count == 1)
        {
            var r = rows[0];
            sb.Append($"図番 {DisplayZuban(r)}");
            if (!string.IsNullOrWhiteSpace(r.Revision)) sb.Append($"（改訂 {r.Revision}）");
            sb.Append(" の");
            var parts = new List<string>();
            foreach (var a in attrs)
            {
                var v = ValueOf(r, a.Key);
                parts.Add($"{a.Label}は {Display(v)}");
            }
            sb.Append(string.Join("、", parts)).Append(" です。");
            confidence = 1.0;
        }
        else
        {
            sb.Append($"図番 {DisplayZuban(rows[0])} は複数の改訂が登録されています。");
            foreach (var r in rows)
            {
                sb.Append("\n- ");
                if (!string.IsNullOrWhiteSpace(r.Revision)) sb.Append($"改訂 {r.Revision}: ");
                var parts = new List<string>();
                foreach (var a in attrs)
                    parts.Add($"{a.Label} {Display(ValueOf(r, a.Key))}");
                sb.Append(string.Join(" / ", parts));
            }
            confidence = 0.75;
        }

        answer = sb.ToString();
        sources = rows.Select(ToSource).ToList();
        return true;
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

    private static string? ValueOf(Row r, string key) => key switch
    {
        "zairyo" => r.Zairyo,
        "hinmei" => r.Hinmei,
        "revision" => r.Revision,
        "scale" => r.Scale,
        "approved_at" => r.ApprovedAt,
        "zuban_raw" => r.ZubanRaw,
        _ => null,
    };

    private static string DisplayZuban(Row r) =>
        !string.IsNullOrWhiteSpace(r.ZubanRaw) ? r.ZubanRaw! : (r.ZubanNorm ?? "");

    private static string Display(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "登録なし" : v!.Trim();

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
        foreach (var c in t)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}
