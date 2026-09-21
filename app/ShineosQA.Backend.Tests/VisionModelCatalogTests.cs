using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>視覚言語モデル（図面キャプチャAI読取）のカタログ登録。設定画面のモデル一覧に
/// 自動表示されること（wizard.tsはカタログ全体を列挙する）が前提となる登録の検証</summary>
public class VisionModelCatalogTests
{
    private sealed class NullLogger : ILogger
    {
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
    }

    [Fact]
    public void Catalog_ContainsVisionModelPair()
    {
        var mm = new ModelManager(
            new AppConfig { ModelsDir = Path.Combine(Path.GetTempPath(), "vision-cat-" + Guid.NewGuid().ToString("N")) },
            new NullLogger());
        var st = mm.Status();
        Assert.Contains(st, m => m.Id == "vision-qwen3vl" && m.Kind == "vision");
        Assert.Contains(st, m => m.Id == "vision-qwen3vl-mmproj" && m.Kind == "vision");
        // 2ファイルとも未導入の状態では「視覚エンジンは起動しない」判断になることをPropertiesで確認
        Assert.False(st.First(m => m.Id == "vision-qwen3vl").Installed);
    }
}
