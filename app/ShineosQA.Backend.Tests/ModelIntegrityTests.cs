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
    public void Verify_MismatchedHash_ThrowsModelHash_AndRecordsBadVerdict()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            ModelIntegrity.Verify(_model, _dataDir, new string('0', 64), new NoopLogger()));
        Assert.Contains("SHINE_E_MODEL_HASH", ex.Message);
        // 破損判定もキャッシュされる（UIの破損バッジ表示に使う。ハッシュ計算なしで参照可能）
        Assert.False(ModelIntegrity.CachedOk(_model, _dataDir));
        Assert.Contains("|bad", File.ReadAllText(Path.Combine(_dataDir, "model-verify.txt")));
    }

    [Fact]
    public void CachedOk_UnknownFile_ReturnsNull()
    {
        Assert.Null(ModelIntegrity.CachedOk(_model, _dataDir)); // 一度も検証していない
    }

    [Fact]
    public void CachedOk_AfterGoodVerify_ReturnsTrue()
    {
        var sha = ShaOf(_model);
        ModelIntegrity.Verify(_model, _dataDir, sha, new NoopLogger());
        Assert.True(ModelIntegrity.CachedOk(_model, _dataDir));
    }

    [Fact]
    public void CachedOk_AfterFileChange_ReturnsNullAgain()
    {
        var sha = ShaOf(_model);
        ModelIntegrity.Verify(_model, _dataDir, sha, new NoopLogger());
        File.WriteAllText(_model, "changed content changes mtime");
        Assert.Null(ModelIntegrity.CachedOk(_model, _dataDir)); // 別バージョン扱い→要再検証
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

    [Fact]
    public void DownloadProgress_SerializesFields()
    {
        // /api/models/progress の回帰: フィールド宣言に戻ると System.Text.Json が {} を返し
        // UIのDL進捗%が一切表示されなくなる（実障害として発見）
        var p = new ModelManager.DownloadProgress { State = "downloading", CurrentId = "chat-quick", Bytes = 5, Total = 10 };
        var json = System.Text.Json.JsonSerializer.Serialize(p);
        Assert.Contains("downloading", json);
        Assert.Contains("chat-quick", json);
        Assert.Contains("Bytes", json);
    }

    private sealed class NoopLogger : ILogger
    {
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
    }
}
