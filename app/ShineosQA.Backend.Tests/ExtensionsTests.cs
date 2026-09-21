using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>拡張パック機構（T2）: 既定OFF・設定の往復・スキーマ移行</summary>
public class ExtensionsTests : IDisposable
{
    private readonly string _dir;

    public ExtensionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shineosqa-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private Db NewDb() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public void IsEnabled_DefaultFalse()
    {
        using var db = NewDb();
        Assert.False(Extensions.IsEnabled(db, Extensions.DrawingId));
    }

    [Fact]
    public void SetEnabled_RoundTrip()
    {
        using var db = NewDb();
        Extensions.SetEnabled(db, Extensions.DrawingId, true);
        Assert.True(Extensions.IsEnabled(db, Extensions.DrawingId));
        Extensions.SetEnabled(db, Extensions.DrawingId, false);
        Assert.False(Extensions.IsEnabled(db, Extensions.DrawingId));
    }

    [Fact]
    public void Schema_MigrationAddsKindAndDrawingMeta()
    {
        using var db = NewDb();
        var cols = db.Query("PRAGMA table_info(files)");
        Assert.Contains(cols, c => (string)c["name"]! == "kind");
        var table = db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name='drawing_meta'");
        Assert.True(table.Count == 1);
        // メタデータ挿入と zuban_norm での引き
        db.Exec("INSERT INTO files(name, status, kind) VALUES('d.pdf','ready','drawing')");
        db.Exec("INSERT INTO drawing_meta(file_id, zuban_raw, zuban_norm, hinmei) VALUES(1,'ST-1042A','st1042a','サポートブラケット')");
        var hit = db.Query("SELECT hinmei FROM drawing_meta WHERE zuban_norm=$z", ("$z", "st1042a"));
        Assert.Single(hit);
    }

    [Fact]
    public void DrawingLlmEnabled_DefaultTrue()
    {
        using var db = NewDb();
        Assert.True(Extensions.DrawingLlmEnabled(db)); // 内部設定（v1はUI非公開）
    }
}
