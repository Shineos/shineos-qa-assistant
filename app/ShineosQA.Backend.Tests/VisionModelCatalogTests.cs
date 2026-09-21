using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>視覚言語モデル（図面キャプチャAI読取）のカタログ登録。
/// 本体+mmprojの2ファイルを1エントリ（1行・1クリックDL）として扱うことを検証する</summary>
public class VisionModelCatalogTests
{
    private sealed class NullLogger : ILogger
    {
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
    }

    [Fact]
    public void Catalog_ContainsVisionModelAsSingleEntry()
    {
        var mm = new ModelManager(
            new AppConfig { ModelsDir = Path.Combine(Path.GetTempPath(), "vision-cat-" + Guid.NewGuid().ToString("N")) },
            new NullLogger());
        var st = mm.Status();
        // 1エントリで本体+mmproj（セット品）を扱う: ユーザー視点では1行・1クリックで導入完了
        var vision = st.FirstOrDefault(m => m.Id == "vision-qwen3vl");
        Assert.NotNull(vision);
        Assert.Equal("vision", vision!.Kind);
        Assert.False(vision.Installed);
        // mmprojを別行にしない（ユーザーが2行の違いを意識しなくてよい設計）
        Assert.DoesNotContain(st, m => m.Id == "vision-qwen3vl-mmproj");
    }
}
