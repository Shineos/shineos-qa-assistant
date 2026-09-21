using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>視覚言語モデル（Qwen3-VL）によるキャプチャ画像のAI読取（拡張パック・図面）。
/// WinRT OCRが苦手な表題欄（ハイフン付き図番・文字切れ）を画像から直接読み、
/// 文字の無い図形のみのキャプチャからは形状の検索キーワードを生成する。
/// モデル未導入時はnullを返し、呼び出し側はWinRT OCRの結果だけで動く（機能劣化なし）</summary>
public static partial class VisionRead
{
    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    public sealed record Result(string Zuban, string Hinmei, string Zairyo, string Revision, string Shape, string Raw);

    private const string Prompt =
        "これは機械図面（またはその一部のキャプチャ）です。次をJSONのみで返してください。" +
        "{\"zuban\":\"表題欄の図番\",\"hinmei\":\"品名\",\"zairyo\":\"材質\",\"revision\":\"改訂\",\"shape\":\"図形の特徴（検索用キーワードを日本語で3個まで、スペース区切り）\"}。" +
        "表題欄が見えなければ zuban〜revision は null。図形のみなら shape を必ず返す。推測禁止。";

    /// <summary>視覚エンジンを確保して画像を読み、JSONを解析する。失敗時はOk=false（例外は握りつぶさず伝播させない設計）</summary>
    public static async Task<Result?> ReadAsync(LlmGateway gw, Supervisor sup, AppConfig cfg,
        byte[] image, string imageMime, ILogger log, CancellationToken ct)
    {
        var engine = sup.EnsureVision();
        if (engine is null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(180)); // 画像エンコード＋初回モデルロード込みの上限
            var raw = await gw.VisionOnceAsync(cfg.EnginePortVision, Prompt, image, imageMime, 220, cts.Token);
            var json = ExtractJson(raw);
            if (json is null) { log.Warn($"vision read: no json in response: {raw[..Math.Min(80, raw.Length)]}"); return null; }
            using var doc = JsonDocument.Parse(json);
            // 小模型は「null」を文字列として返すことがあるため、リテラル"null"は値なし扱いにする
            string? Get(string k) =>
                doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                    ? (string.Equals(v.GetString(), "null", StringComparison.OrdinalIgnoreCase) ? null : v.GetString())
                    : null;
            var res = new Result(
                Zuban: Get("zuban") ?? "",
                Hinmei: Get("hinmei") ?? "",
                Zairyo: Get("zairyo") ?? "",
                Revision: Get("revision") ?? "",
                Shape: (Get("shape") ?? "").Trim(),
                Raw: raw);
            log.Info($"vision read ok: zuban={res.Zuban} hinmei={res.Hinmei} shape={res.Shape}");
            return res;
        }
        catch (Exception ex) { log.Warn($"vision read failed: {ex.Message}"); return null; }
    }

    private static string? ExtractJson(string raw)
    {
        var m = FenceRegex().Match(raw);
        if (m.Success) return m.Groups[1].Value;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }
}
