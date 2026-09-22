namespace ShineosQA.Backend;

/// <summary>設定の不変条件を一元化する（v2.1.12 の web_search 反映漏れ対策）。
///
/// 設定には2つの真実源がある:
///   - settings テーブル = 永続の真実（GET /api/settings や Extensions.IsEnabled はDBを読む）
///   - AppConfig (cfg)   = 実行時スナップショット（ChatFlow・Supervisor などホットパスはDBではなくcfgを見る）
///
/// したがって不変条件は「起動時に ApplyPersisted で DB→cfg へ全キー反映すること」かつ
/// 「POST /api/settings の各キーはDBとcfgの両方を更新すること」。
/// 新しい設定キーを追加するときは (1) GET の表示 (2) POST のDB+cfg更新 (3) ここへの反映 の3点セット。
/// （web_search は (2)(3) が欠落し、UIでOFFにしてもAPI経由の質問はWeb検索を試み続ける不具合になった）</summary>
public static class SettingsSync
{
    /// <summary>settingsテーブルの永続値をcfgへ反映する（起動時。上書きは無害）。
    /// tierは "auto/standard/quick" のみ復元する（qualityは実験的で13GBモデルを起動時に
    /// 自動ロードさせないため、永続されても再起動後はcfg既定のまま）</summary>
    public static void ApplyPersisted(Db db, AppConfig cfg)
    {
        if (bool.TryParse(db.GetSetting("web_search", ""), out var web)) cfg.WebSearch = web;
        if (db.GetSetting("tier", "") is "auto" or "standard" or "quick") cfg.Tier = db.GetSetting("tier", "");
        if (bool.TryParse(db.GetSetting("bg_friendly", ""), out var bg)) cfg.BgFriendly = bg;
    }
}
