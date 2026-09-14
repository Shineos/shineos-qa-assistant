using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir;
    public ConfigTests() { _dir = Path.Combine(Path.GetTempPath(), "shineqa-cfg-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteConfig(string json, string name = "config.json")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void Load_ValidConfig_AppliesValues()
    {
        var p = WriteConfig("""{ "port": 8500, "tier": "quick", "ctx_size": 4096, "web_search": true, "data_dir": "D:/data" }""");
        var cfg = AppConfig.Load(new[] { "--config", p });
        Assert.Equal(8500, cfg.Port);
        Assert.Equal("quick", cfg.Tier);
        Assert.Equal(4096, cfg.CtxSize);
        Assert.True(cfg.WebSearch);
        Assert.Equal("D:/data", cfg.DataDir);
    }

    [Fact]
    public void Load_MissingFile_UsesDefaults()
    {
        var p = Path.Combine(_dir, "absent.json");
        var cfg = AppConfig.Load(new[] { "--config", p });
        Assert.Equal(8300, cfg.Port);
        Assert.Equal("auto", cfg.Tier);
        Assert.False(cfg.WebSearch);
        Assert.Equal(8301, cfg.EnginePortLlm);
    }

    [Fact]
    public void Load_RelativePath_ResolvedAgainstAppBase()
    {
        WriteConfig("""{ "port": 8600 }""", "rel.json");
        var cfg = AppConfig.Load(new[] { "--config", Path.GetFileName(Path.Combine(_dir, "rel.json")) });
        // 相対パスはアプリディレクトリ基準なので、このテストの意図は「拒否されず既定が使われる」こと
        Assert.Equal(8300, cfg.Port);
    }

    [Fact]
    public void Load_PathTraversal_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => AppConfig.Load(new[] { "--config", "..\\..\\evil.json" }));
        Assert.Throws<ArgumentException>(() => AppConfig.Load(new[] { "--config", "C:/x/../y.json" }));
    }

    [Fact]
    public void Load_NonJsonExtension_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => AppConfig.Load(new[] { "--config", Path.Combine(_dir, "config.txt") }));
    }

    [Fact]
    public void Load_BrokenJson_Throws()
    {
        var p = WriteConfig("{ broken json !!");
        Assert.ThrowsAny<Exception>(() => AppConfig.Load(new[] { "--config", p }));
    }

    [Fact]
    public void Load_UnknownKeys_AreIgnored()
    {
        var p = WriteConfig("""{ "port": 8700, "future_option": 1 }""");
        var cfg = AppConfig.Load(new[] { "--config", p });
        Assert.Equal(8700, cfg.Port);
    }

    [Fact]
    public void Load_TuningKeys_AreApplied()
    {
        // 回帰: idle_unload_minutes / bg_friendly / threads はプロパティがあっても
        // switchにマッピングがないと黙って無視されていた（idle解放テストで発見）
        var p = WriteConfig("""{ "idle_unload_minutes": 5, "bg_friendly": false, "threads": 3 }""");
        var cfg = AppConfig.Load(new[] { "--config", p });
        Assert.Equal(5, cfg.IdleUnloadMinutes);
        Assert.False(cfg.BgFriendly);
        Assert.Equal(3, cfg.Threads);
        // 既定値の確認（他テストの前提）
        Assert.True(new AppConfig().BgFriendly);
        Assert.Equal(60, new AppConfig().IdleUnloadMinutes);
    }

    [Fact]
    public void ModelFiles_TierDefaults_AreConsistent()
    {
        var cfg = new AppConfig();
        Assert.EndsWith(".gguf", cfg.QuickModel);
        Assert.EndsWith(".gguf", cfg.StandardModel);
        Assert.EndsWith(".gguf", cfg.QualityModel);
        Assert.EndsWith(".gguf", cfg.EmbedModel);
        Assert.EndsWith(".gguf", cfg.RankModel);
        Assert.All(new[] { cfg.EnginePortLlm, cfg.EnginePortEmb, cfg.EnginePortRank }, p => Assert.NotEqual(cfg.Port, p));
    }
}
