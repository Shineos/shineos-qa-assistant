using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public class DbTests : IDisposable
{
    private readonly Db _db;

    public DbTests()
    {
        _db = new Db(Path.Combine(Path.GetTempPath(), "shineqa-test-" + Guid.NewGuid().ToString("N") + ".db"));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Settings_Roundtrip()
    {
        _db.SetSetting("tier", "standard");
        Assert.Equal("standard", _db.GetSetting("tier", ""));
        Assert.Equal("fallback", _db.GetSetting("missing-key", "fallback"));
        _db.SetSetting("tier", "quick"); // 上書き
        Assert.Equal("quick", _db.GetSetting("tier", ""));
    }

    [Fact]
    public void Chat_CreateAndLookup()
    {
        var uuid = _db.NewChatUuid();
        Assert.NotEmpty(uuid);
        var id = _db.ChatIdFromUuid(uuid);
        Assert.True(id > 0);
        Assert.Equal(uuid, _db.ChatUuid(id));
        Assert.Equal(0, _db.ChatIdFromUuid("no-such-uuid"));
    }

    [Fact]
    public void Chunk_InsertAndLoadReady()
    {
        _db.Exec("INSERT INTO files(name, status) VALUES(@name, 'ready')", ("@name", "test.md"));
        var fileId = _db.LastInsertId();
        _db.InsertChunk(fileId, 1, "日当は1,500円", ChunkIndex.FloatsToBytes(new float[] { 1, 0, 0 }));
        _db.InsertChunk(fileId, 2, "宿泊費は15,000円", ChunkIndex.FloatsToBytes(new float[] { 0, 1, 0 }));
        var rows = _db.LoadReadyChunks();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("test.md", r.FileName));
        Assert.Contains(rows, r => r.Seq == 1 && r.Text.Contains("1,500"));
        Assert.Contains(rows, r => r.Seq == 2 && r.Text.Contains("15,000"));
        Assert.All(rows, r => Assert.Equal(3 * 4, r.Emb.Length));
    }

    [Fact]
    public void AnswerCache_ModelColumnExists_AfterMigration()
    {
        // v2.0.0マイグレーション: answer_cache に model 列（モデル別キャッシュ分離）
        // Dbコンストラクタが ALTER TABLE を行うため、insert が model 列付きで成功するはず
        _db.Exec("INSERT INTO answer_cache(question, answer, model, emb, sources_json) VALUES(@q, @a, @m, @e, @s)",
            ("@q", "国内出張の日当はいくらですか"), ("@a", "1,500円です"), ("@m", "Qwen3-1.7B-IQ4_XS.gguf"),
            ("@e", ChunkIndex.FloatsToBytes(new float[] { 0.5f, 0.5f })), ("@s", "[]"));
        var rows = _db.Query("SELECT question, model FROM answer_cache WHERE model = @m", ("@m", "Qwen3-1.7B-IQ4_XS.gguf"));
        Assert.Single(rows);
        Assert.Equal("国内出張の日当はいくらですか", rows[0]["question"]);
    }

    [Fact]
    public void Query_ParametersAreBound_NotConcatenated()
    {
        // パラメータ結合の回帰: SQLインジェクション風の値がリテラルとして扱われる
        _db.Exec("INSERT INTO files(name, status) VALUES(@name, 'ready')", ("@name", "x'; DROP TABLE files;--"));
        var rows = _db.Query("SELECT * FROM files WHERE name = @n", ("@n", "x'; DROP TABLE files;--"));
        Assert.Single(rows);
        Assert.True(_db.LoadReadyChunks().Count >= 0); // files テーブルが生きている
    }
}

public class ChunkIndexTests
{
    private static Db NewDb() => new(Path.Combine(Path.GetTempPath(), "shineqa-idx-" + Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public void AddRange_Search_RanksByHybridScore()
    {
        using var db = NewDb();
        db.Exec("INSERT INTO files(name, status) VALUES('doc.md', 'ready')");
        var fileId = db.LastInsertId();
        var idx = new ChunkIndex();
        // クエリベクトルに近いチャンクと、語彙が一致するチャンクを登録
        idx.AddRange("doc.md", fileId, new[]
        {
            (1, "パスワードは90日ごとに変更する", new float[] { 0.9f, 0.1f }),
            (2, "会議室は予約システムから借りる", new float[] { 0.1f, 0.9f }),
        });
        var hits = idx.Search(new float[] { 1, 0 }, new HashSet<string>(Rag.Tokenize("パスワード 変更")), 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Rec.Seq); // ベクトル+語彙一致のチャンクが上位
    }

    [Fact]
    public void NextChunkText_ReturnsAdjacentChunk()
    {
        using var db = NewDb();
        db.Exec("INSERT INTO files(name, status) VALUES('旅費.md', 'ready')");
        var fileId = db.LastInsertId();
        var idx = new ChunkIndex();
        idx.AddRange("旅費.md", fileId, new[]
        {
            (1, "前半: 日当は1,500円である。", new float[] { 1, 0 }),
            (2, "後半: 宿泊費の上限は15,000円である。", new float[] { 0, 1 }),
        });
        Assert.Equal("後半: 宿泊費の上限は15,000円である。", idx.NextChunkText(fileId, 1));
        Assert.Null(idx.NextChunkText(fileId, 2)); // 最終チャンクの隣は無い
        Assert.Null(idx.NextChunkText(fileId + 99, 1)); // 他ファイル
    }

    [Fact]
    public void RemoveFile_DropsAllItsChunks()
    {
        using var db = NewDb();
        db.Exec("INSERT INTO files(name, status) VALUES('a.md', 'ready')");
        var f1 = db.LastInsertId();
        db.Exec("INSERT INTO files(name, status) VALUES('b.md', 'ready')");
        var f2 = db.LastInsertId();
        var idx = new ChunkIndex();
        idx.AddRange("a.md", f1, new[] { (1, "A1", new float[] { 1, 0 }) });
        idx.AddRange("b.md", f2, new[] { (1, "B1", new float[] { 0, 1 }) });
        Assert.Equal(2, idx.Count);
        idx.RemoveFile(f1);
        Assert.Equal(1, idx.Count);
        var hits = idx.Search(new float[] { 1, 0 }, new HashSet<string>(), 5);
        Assert.All(hits, h => Assert.Equal("b.md", h.Rec.FileName));
    }

    [Fact]
    public void LoadFrom_RebuildsFromDb()
    {
        using var db = NewDb();
        db.Exec("INSERT INTO files(name, status) VALUES('c.md', 'ready')");
        var fileId = db.LastInsertId();
        db.InsertChunk(fileId, 1, "空調は18時30分に停止する", ChunkIndex.FloatsToBytes(new float[] { 1, 0 }));
        var idx = new ChunkIndex();
        idx.LoadFrom(db);
        Assert.Equal(1, idx.Count);
    }
}
