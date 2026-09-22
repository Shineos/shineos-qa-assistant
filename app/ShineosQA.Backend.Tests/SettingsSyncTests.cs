using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>起動時の設定同期（SettingsSync.ApplyPersisted）の不変条件。
/// web_search で「DBのみ更新されcfgへ反映されない」不具合が起きたため、
/// 永続キーがすべてcfgへ反映されることをここでロックする</summary>
public class SettingsSyncTests : IDisposable
{
    private readonly Db _db;
    private readonly AppConfig _cfg = new();

    public SettingsSyncTests() => _db = new Db(Path.Combine(Path.GetTempPath(), "shineqa-test-" + Guid.NewGuid().ToString("N") + ".db"));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void WebSearch_DbValue_AppliesToConfig()
    {
        _db.SetSetting("web_search", "true");
        SettingsSync.ApplyPersisted(_db, _cfg);
        Assert.True(_cfg.WebSearch);

        _db.SetSetting("web_search", "false");
        SettingsSync.ApplyPersisted(_db, _cfg);
        Assert.False(_cfg.WebSearch);
    }

    [Fact]
    public void Tier_DbValue_AppliesToConfig_AndQualityIsNotRestored()
    {
        _db.SetSetting("tier", "quick");
        SettingsSync.ApplyPersisted(_db, _cfg);
        Assert.Equal("quick", _cfg.Tier);

        // qualityは実験的: 起動時に自動ロードさせないため永続値があっても既定のまま
        _db.SetSetting("tier", "quality");
        SettingsSync.ApplyPersisted(_db, _cfg);
        Assert.NotEqual("quality", _cfg.Tier);
    }

    [Fact]
    public void BgFriendly_DbValue_AppliesToConfig()
    {
        _db.SetSetting("bg_friendly", "false");
        SettingsSync.ApplyPersisted(_db, _cfg);
        Assert.False(_cfg.BgFriendly);
    }

    [Fact]
    public void MissingKeys_KeepConfigDefaults()
    {
        var defWeb = _cfg.WebSearch;
        var defTier = _cfg.Tier;
        SettingsSync.ApplyPersisted(_db, _cfg); // settings空
        Assert.Equal(defWeb, _cfg.WebSearch);
        Assert.Equal(defTier, _cfg.Tier);
    }

    [Fact]
    public void ToggleThenApply_SimulatesRestart_ReflectsLastSavedValue()
    {
        // UIトグル（DB+cfg更新）→ 再起動（ApplyPersisted）の流れを再現:
        // 最後に保存した値が再起動後も生きていること（v2.1.12で黙って戻る不具合だった）
        _db.SetSetting("web_search", "true");
        SettingsSync.ApplyPersisted(_db, _cfg);
        Assert.True(_cfg.WebSearch);

        _db.SetSetting("web_search", "false"); // ユーザーがOFFへトグル
        SettingsSync.ApplyPersisted(_db, _cfg); // 再起動
        Assert.False(_cfg.WebSearch);
    }
}
