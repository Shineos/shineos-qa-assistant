using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>オフラインモデル取り込み(ImportAsync)の検証。閉域PCへの正規モデル持ち込み（USB等）が
/// 主ユースケース。拡張子・GGUFマジック・カスタム配置の契約を検証する（カタログ一致は実ファイルの
/// SHA256が必要なためE2Eで検証する）</summary>
public class ModelImportTests : IDisposable
{
    private readonly string _dir;
    private readonly ModelManager _models;

    public ModelImportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shineosqa-import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _models = new ModelManager(new AppConfig { ModelsDir = _dir }, new NoopLogger());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] GgufBytes(string body = "test-model-payload") =>
        System.Text.Encoding.ASCII.GetBytes("GGUF" + body);

    [Fact]
    public async Task Import_MissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _models.ImportAsync(Path.Combine(_dir, "nope.gguf"), CancellationToken.None));
    }

    [Theory]
    [InlineData("model.bin")]
    [InlineData("model.txt")]
    public async Task Import_NonGgufExtension_Rejected(string name)
    {
        var p = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(p, GgufBytes()); // 中身がGGUFでも拡張子で拒否
        await Assert.ThrowsAsync<InvalidDataException>(() => _models.ImportAsync(p, CancellationToken.None));
    }

    [Fact]
    public async Task Import_BadMagic_Rejected()
    {
        var p = Path.Combine(_dir, "fake.gguf");
        await File.WriteAllBytesAsync(p, System.Text.Encoding.ASCII.GetBytes("XXXXnot-a-gguf"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _models.ImportAsync(p, CancellationToken.None));
    }

    [Fact]
    public async Task Import_CustomGguf_CopiedIntoModelsDir()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        var p = Path.Combine(src, "my-custom-model.gguf");
        await File.WriteAllBytesAsync(p, GgufBytes());

        var (catalogId, destFile, bytes) = await _models.ImportAsync(p, CancellationToken.None);

        Assert.Null(catalogId); // SHAがカタログに無い → カスタム
        Assert.Equal("my-custom-model.gguf", destFile);
        Assert.True(File.Exists(Path.Combine(_dir, destFile))); // ModelsDirへ配置済み
        Assert.Equal(GgufBytes().Length, bytes);
        Assert.Equal("done", _models.Progress.State);
    }

    [Fact]
    public async Task Import_SameNameAsCatalogButDifferentSha_Rejected()
    {
        // 破損した公式モデルの持ち込みを、正式配置として誤認させない
        var src = Path.Combine(_dir, "src2");
        Directory.CreateDirectory(src);
        var p = Path.Combine(src, "Qwen3-1.7B-IQ4_XS.gguf"); // カタログ(chat-quick)と同名
        await File.WriteAllBytesAsync(p, GgufBytes("corrupted"));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => _models.ImportAsync(p, CancellationToken.None));
        Assert.Contains("SHA256", ex.Message);
    }

    private class NoopLogger : ILogger
    {
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
    }
}
