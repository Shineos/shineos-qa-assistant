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

    // ---- Phase 0.2: FK有効化・schema_version・content_hash・ExecTransaction・孤児掃除 ----

    [Fact]
    public void ForeignKeys_Enabled_DeleteFileCascadesChunksAndMetas()
    {
        _db.Exec("INSERT INTO files(name, status) VALUES('cascade.md', 'ready')");
        var fileId = _db.LastInsertId();
        _db.InsertChunk(fileId, 1, "cascade対象の本文", ChunkIndex.FloatsToBytes(new float[] { 1, 0 }));
        _db.Exec("INSERT INTO drawing_meta(file_id, zuban_raw) VALUES($f, 'ST-1042A')", ("$f", fileId));
        _db.Exec("INSERT INTO jisseki_meta(file_id, row_no, zuban_raw) VALUES($f, 0, 'ST-1042A')", ("$f", fileId));

        _db.Exec("DELETE FROM files WHERE file_id=$f", ("$f", fileId));

        Assert.Equal(0L, Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM chunks")));
        Assert.Equal(0L, Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM drawing_meta")));
        Assert.Equal(0L, Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM jisseki_meta")));
    }

    [Fact]
    public void SchemaVersion_AndContentHashColumn_Exist()
    {
        // schema_version は1行のみ・現行版が記録される
        Assert.Equal(Db.SchemaVersion, _db.GetSchemaVersion());
        var versionRows = _db.Query("SELECT version FROM schema_version");
        Assert.Single(versionRows);

        // files.content_hash 列（旧DBはALTERで追加）と検索用インデックス
        var cols = _db.Query("PRAGMA table_info(files)").Select(r => (string)r["name"]!).ToList();
        Assert.Contains("content_hash", cols);
        var indexes = _db.Query("PRAGMA index_list(files)").Select(r => (string)r["name"]!).ToList();
        Assert.Contains("idx_files_content_hash", indexes);
    }

    [Fact]
    public void ExecTransaction_CommitsAllCommands()
    {
        _db.Exec("INSERT INTO files(name, status) VALUES('tx.md', 'parsing')");
        var fileId = _db.LastInsertId();
        _db.ExecTransaction(new (string Sql, (string Name, object? Value)[] Params)[]
        {
            ("INSERT INTO chunks(file_id, seq, text, emb) VALUES($f, 1, '本文1', NULL)", new[] { ("$f", (object?)fileId) }),
            ("INSERT INTO chunks(file_id, seq, text, emb) VALUES($f, 2, '本文2', NULL)", new[] { ("$f", (object?)fileId) }),
            ("UPDATE files SET status='ready', chunk_count=2 WHERE file_id=$f", new[] { ("$f", (object?)fileId) }),
        });

        Assert.Equal(2L, Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM chunks WHERE file_id=$f", ("$f", fileId))));
        Assert.Equal("ready", Convert.ToString(_db.Scalar("SELECT status FROM files WHERE file_id=$f", ("$f", fileId))));
    }

    [Fact]
    public void ExecTransaction_RollsBack_OnAnyFailure()
    {
        _db.Exec("INSERT INTO files(name, status) VALUES('rollback.md', 'parsing')");
        var fileId = _db.LastInsertId();

        // 2件目が存在しないテーブルを参照して失敗 → 1件目のINSERTも取り消されること
        var ex = Assert.ThrowsAny<Exception>(() => _db.ExecTransaction(new (string Sql, (string Name, object? Value)[] Params)[]
        {
            ("INSERT INTO chunks(file_id, seq, text, emb) VALUES($f, 1, '残ってはいけない', NULL)", new[] { ("$f", (object?)fileId) }),
            ("INSERT INTO no_such_table(x) VALUES(1)", Array.Empty<(string, object?)>()),
        }));
        Assert.Contains("no_such_table", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0L, Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM chunks")));
        // files行（進捗用のparsing行）はトランザクション外なので残る（呼び出し側がerror化する契約）
        Assert.Equal("parsing", Convert.ToString(_db.Scalar("SELECT status FROM files WHERE file_id=$f", ("$f", fileId))));
        // 以後のトランザクションが使えること（ROLLBACKで接続が壊れていない）
        _db.ExecTransaction(new[] { ("UPDATE files SET status='error', error='failed' WHERE file_id=$f", new[] { ("$f", (object?)fileId) }) });
        Assert.Equal("error", Convert.ToString(_db.Scalar("SELECT status FROM files WHERE file_id=$f", ("$f", fileId))));
    }

    [Fact]
    public void FindReadyFileByHash_ReturnsReadyOnly_AndExcludesErrors()
    {
        _db.Exec("INSERT INTO files(name, status, content_hash) VALUES('ready.md', 'ready', 'hash-ready')");
        var readyId = _db.LastInsertId();
        _db.Exec("INSERT INTO files(name, status, content_hash) VALUES('error.md', 'error', 'hash-error')");
        _db.Exec("INSERT INTO files(name, status, content_hash) VALUES('parsing.md', 'parsing', 'hash-parsing')");
        _db.Exec("INSERT INTO files(name, status, content_hash) VALUES('nohash.md', 'ready', NULL)");

        Assert.Equal(readyId, _db.FindReadyFileByHash("hash-ready"));
        Assert.Null(_db.FindReadyFileByHash("hash-error"));   // エラー行は再取り込みを許可
        Assert.Null(_db.FindReadyFileByHash("hash-parsing")); // 取り込み途中も対象外
        Assert.Null(_db.FindReadyFileByHash(""));             // 空hashは検索しない
        Assert.Null(_db.FindReadyFileByHash("unknown"));
    }

    [Fact]
    public void OrphanRows_AreCleanedUpOnStartup()
    {
        // 旧DB（foreign_keys未実効時代）を再現: FKを切って files 行だけを消し、孤児を作る
        var path = Path.Combine(Path.GetTempPath(), "shineqa-orphan-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            long fileId;
            using (var db = new Db(path))
            {
                db.Exec("INSERT INTO files(name, status) VALUES('orphan.md', 'ready')");
                fileId = db.LastInsertId();
                db.InsertChunk(fileId, 1, "孤児になる本文", ChunkIndex.FloatsToBytes(new float[] { 1 }));
                db.Exec("INSERT INTO drawing_meta(file_id, zuban_raw) VALUES($f, 'ST-9999')", ("$f", fileId));
                db.Exec("INSERT INTO jisseki_meta(file_id, row_no, zuban_raw) VALUES($f, 0, 'ST-9999')", ("$f", fileId));
                db.Exec("PRAGMA foreign_keys=OFF");
                db.Exec("DELETE FROM files WHERE file_id=$f", ("$f", fileId));
                // FK OFFなので孤児が残っていることを確認（テスト前提のサニティ）
                Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM chunks")));
            }

            // 再起動（Dbコンストラクタ）で孤児行が掃除されること
            using var reopened = new Db(path);
            Assert.Equal(0L, Convert.ToInt64(reopened.Scalar("SELECT COUNT(*) FROM chunks")));
            Assert.Equal(0L, Convert.ToInt64(reopened.Scalar("SELECT COUNT(*) FROM drawing_meta")));
            Assert.Equal(0L, Convert.ToInt64(reopened.Scalar("SELECT COUNT(*) FROM jisseki_meta")));
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                try { File.Delete(f); } catch { }
        }
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
