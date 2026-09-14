using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public class ModelIntegrityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dataDir;
    private readonly string _model;

    public ModelIntegrityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shineqa-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dataDir = Path.Combine(_dir, "data");
        _model = Path.Combine(_dir, "Qwen3-1.7B-IQ4_XS.gguf");
        File.WriteAllText(_model, "GGUF fake model body for integrity tests");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string ShaOf(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    [Fact]
    public void Verify_MatchingHash_PassesAndWritesCache()
    {
        var sha = ShaOf(_model);
        ModelIntegrity.Verify(_model, _dataDir, sha, new NoopLogger());
        Assert.True(File.Exists(Path.Combine(_dataDir, "model-verify.txt")));
    }

    [Fact]
    public void Verify_MismatchedHash_ThrowsModelHash()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            ModelIntegrity.Verify(_model, _dataDir, new string('0', 64), new NoopLogger()));
        Assert.Contains("SHINE_E_MODEL_HASH", ex.Message);
        // 不一致時は検証済みキャッシュを書かない（修復後に再検証させる）
        Assert.False(File.Exists(Path.Combine(_dataDir, "model-verify.txt")));
    }

    [Fact]
    public void Verify_CacheHit_SkipsRehash()
    {
        var sha = ShaOf(_model);
        ModelIntegrity.Verify(_model, _dataDir, sha, new NoopLogger());
        // キャッシュ済みの状態で「期待SHAを壊した値に変えても」キャッシュが効いていれば検証を通る
        // （同一 len+mtime のファイルは再ハッシュしない設計であることの証明）
        ModelIntegrity.Verify(_model, _dataDir, new string('1', 64), new NoopLogger());
    }

    [Fact]
    public void Verify_FileChanged_RetestsAndDetectsCorruption()
    {
        var sha = ShaOf(_model);
        ModelIntegrity.Verify(_model, _dataDir, sha, new NoopLogger());
        // 破損（バイト書き換え→mtime変化）: キャッシュミスになり必ず再検証にはかる
        File.WriteAllText(_model, "GGUF fake model body for integrity tests (corrupted)");
        Assert.Throws<InvalidDataException>(() => ModelIntegrity.Verify(_model, _dataDir, sha, new NoopLogger()));
    }

    [Fact]
    public void Verify_NullExpectedSha_SkipsVerification()
    {
        // カタログ外モデル（ユーザー任意のGGUF）は検証せず素通りする
        ModelIntegrity.Verify(_model, _dataDir, null, new NoopLogger());
        Assert.False(File.Exists(Path.Combine(_dataDir, "model-verify.txt")));
    }

    [Fact]
    public void ShaForFile_CatalogEntries()
    {
        Assert.NotNull(ModelManager.ShaForFile("Qwen3-1.7B-IQ4_XS.gguf"));
        Assert.Equal(64, ModelManager.ShaForFile("Qwen3-1.7B-IQ4_XS.gguf")!.Length);
        Assert.NotNull(ModelManager.ShaForFile("bge-m3-Q8_0.gguf"));
        Assert.Null(ModelManager.ShaForFile("unknown-model.gguf"));
    }

    private sealed class NoopLogger : ILogger
    {
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
    }
}
