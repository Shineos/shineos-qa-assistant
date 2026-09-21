namespace ShineosQA.Backend;

/// <summary>拡張パック（設定でON/OFFする機能群）。設計: docs/plan-drawing-search.md §3.0。
/// パックは本体フローを変えず、Ingest分岐とパック専用エンドポイントの二重ゲートでのみ効果を持つ</summary>
public sealed record PackDef(string Id, string Title, string Description);

public static class Extensions
{
    public const string DrawingId = "drawing";

    public static readonly PackDef Drawing = new(
        DrawingId,
        "図面PDF検索・Q&A（製造業向け）",
        "図面PDFの取り込み時に表題欄（図番・品名・材質・改訂）を自動で読み取り、図番・品名での検索と出典付きQ&Aを可能にします。無効にしても取り込んだ図面データは残ります。");

    // 注: Excel・CSV取り込みは拡張パックではなく本体標準機能（常に有効・設定不要）

    /// <summary>拡張パックの有効判定。既定OFF・リクエストごとにDBを読む（即時反映・再起動不要）</summary>
    public static bool IsEnabled(Db db, string packId) => db.GetSetting($"ext.{packId}", "0") == "1";

    public static void SetEnabled(Db db, string packId, bool on) => db.SetSetting($"ext.{packId}", on ? "1" : "0");

    /// <summary>表題欄LLM構造化（T6）。内部設定・v1ではUI非公開</summary>
    public static bool DrawingLlmEnabled(Db db) => db.GetSetting("ext.drawing.llm", "1") == "1";
}
