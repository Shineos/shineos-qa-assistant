using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>スキーマ移行の回帰テスト: リリース2.6.2（schema v4）と同一DDLでDBを作成し、
/// 現行コード（v5）で開いたときの移行・データ保持・新機能テーブルの利用可否を検証する。
/// DDLは 4eeebea 時点の Db.cs から抽出（CREATE＋ALTER両方=2.6.2新規インストール相当）。
/// 今後の移行（テーブル/列追加）では本テストの期待値を更新する</summary>
public class SchemaUpgradeTests : IDisposable
{
    private const string V4SchemaSql = @"            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS files(
                file_id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
                error TEXT, chunk_count INTEGER DEFAULT 0, added_at TEXT DEFAULT (datetime('now')));
            CREATE TABLE IF NOT EXISTS chunks(
                chunk_id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER NOT NULL REFERENCES files(file_id) ON DELETE CASCADE,
                seq INTEGER NOT NULL, text TEXT NOT NULL, emb BLOB);
            CREATE TABLE IF NOT EXISTS chats(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT DEFAULT '新しいチャット', created_at TEXT DEFAULT (datetime('now')),
                updated_at TEXT DEFAULT (datetime('now')));
            CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                role TEXT NOT NULL, content TEXT NOT NULL,
                sources_json TEXT, created_at TEXT DEFAULT (datetime('now')),
                kind TEXT);
            CREATE TABLE IF NOT EXISTS answer_cache(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                question TEXT NOT NULL, answer TEXT NOT NULL, sources_json TEXT,
                emb BLOB NOT NULL, model TEXT NOT NULL DEFAULT '', hits INTEGER DEFAULT 0, created_at TEXT DEFAULT (datetime('now')));
            CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);
            CREATE INDEX IF NOT EXISTS idx_messages_chat ON messages(chat_id);
            CREATE TABLE IF NOT EXISTS drawing_meta(
              file_id INTEGER PRIMARY KEY REFERENCES files(file_id) ON DELETE CASCADE,
              zuban_raw TEXT, zuban_norm TEXT, hinmei TEXT, zairyo TEXT,
              scale TEXT, revision TEXT, approved_at TEXT);
            CREATE INDEX IF NOT EXISTS idx_drawing_meta_norm ON drawing_meta(zuban_norm);
            CREATE TABLE IF NOT EXISTS jisseki_meta(
              file_id INTEGER NOT NULL REFERENCES files(file_id) ON DELETE CASCADE,
              row_no INTEGER NOT NULL,
              zuban_raw TEXT, zuban_norm TEXT,
              mitumori TEXT, genka TEXT, nouki TEXT,
              PRIMARY KEY(file_id, row_no));
            CREATE INDEX IF NOT EXISTS idx_jisseki_meta_norm ON jisseki_meta(zuban_norm);
            CREATE TABLE IF NOT EXISTS faq(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              question TEXT NOT NULL,
              question_norm TEXT NOT NULL UNIQUE,
              answer TEXT NOT NULL,
              sources_json TEXT,
              tags TEXT NOT NULL DEFAULT '',
              status TEXT NOT NULL DEFAULT 'published',
              source_hashes TEXT,
              hits INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL DEFAULT (datetime('now')),
              updated_at TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE TABLE IF NOT EXISTS faq_variants(
              faq_id INTEGER NOT NULL REFERENCES faq(id) ON DELETE CASCADE,
              variant_norm TEXT NOT NULL,
              PRIMARY KEY(faq_id, variant_norm));
            CREATE INDEX IF NOT EXISTS idx_faq_variants_norm ON faq_variants(variant_norm);
            CREATE INDEX IF NOT EXISTS idx_faq_status ON faq(status);
            CREATE TABLE IF NOT EXISTS feedback(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              message_id INTEGER,
              chat_id INTEGER,
              rating INTEGER NOT NULL,
              comment TEXT,
              corrected_answer TEXT,
              created_at TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE INDEX IF NOT EXISTS idx_feedback_created ON feedback(created_at);
            CREATE TABLE IF NOT EXISTS schema_version(
              id INTEGER PRIMARY KEY CHECK(id = 1),
              version INTEGER NOT NULL,
              updated_at TEXT NOT NULL DEFAULT (datetime('now')));";

    private static readonly string[] V4Alters =
    {
        "ALTER TABLE answer_cache ADD COLUMN model TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE files ADD COLUMN kind TEXT NOT NULL DEFAULT 'doc'",
        "ALTER TABLE messages ADD COLUMN image TEXT",
        "ALTER TABLE messages ADD COLUMN kind TEXT",
        "ALTER TABLE chats ADD COLUMN archived INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE chats ADD COLUMN bookmarked INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE files ADD COLUMN content_hash TEXT",
    };

    private readonly string _dir;
    private readonly string _dbPath;

    public SchemaUpgradeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shineqa-upg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "knowledge.db");
        BuildV4Database(_dbPath);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>2.6.2（v4）と同一のDDL＋ALTERでDBを作成し、利用者データを投入する</summary>
    private static void BuildV4Database(string path)
    {
        using var conn = new SqliteConnection("Data Source=" + path);
        conn.Open();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = V4SchemaSql; cmd.ExecuteNonQuery(); }
        foreach (var alter in V4Alters)
            using (var cmd = conn.CreateCommand())
            {
                // v4本体と同じく列追加はtry/catch（旧DBからの更新で重複列になっても握りつぶす）
                cmd.CommandText = alter;
                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { /* duplicate column 等 */ }
            }
        Exec(conn, "INSERT INTO files(file_id, name, status, kind, chunk_count) VALUES(1,'規程.md','ready','doc',1)");
        Exec(conn, "INSERT INTO chunks(file_id, seq, text) VALUES(1, 0, '移行前の本文')");
        Exec(conn, "INSERT INTO chats(id, title) VALUES(1, '移行前チャット')");
        Exec(conn, "INSERT INTO messages(chat_id, role, content) VALUES(1, 'user', '移行前の質問')");
        Exec(conn, "INSERT INTO messages(chat_id, role, content) VALUES(1, 'assistant', '移行前の回答')");
        Exec(conn, "INSERT INTO faq(question, question_norm, answer, status) VALUES('移行前の質問','移行前の質問','移行前の回答','published')");
        Exec(conn, "INSERT INTO feedback(message_id, chat_id, rating, comment) VALUES(2, 1, -1, '移行前のコメント')");
        Exec(conn, "INSERT INTO jisseki_meta(file_id, row_no, zuban_raw, zuban_norm, mitumori) VALUES(1, 0, 'ST-1000', 'st1000', '100,000円')");
        Exec(conn, "INSERT INTO settings(key, value) VALUES('tier','quick')");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void V4Database_MigratesToCurrent_AndPreservesAllData()
    {
        using var db = new Db(_dbPath);
        // v4（2.6.2）から現行スキーマ（v6・files.hit_count/enabled追加）へ移行する
        Assert.Equal(6, db.GetSchemaVersion());
        // v5の新テーブルが作られている
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM jisseki_colmap")));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM faq_candidates")));
        // v4データが全て保持されている
        Assert.Equal("移行前の本文", Convert.ToString(db.Scalar("SELECT text FROM chunks WHERE file_id=1")));
        Assert.Equal(2L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM messages WHERE chat_id=1")));
        Assert.Equal("移行前の回答", Convert.ToString(db.Scalar("SELECT answer FROM faq WHERE question='移行前の質問'")));
        Assert.Equal("100,000円", Convert.ToString(db.Scalar("SELECT mitumori FROM jisseki_meta WHERE zuban_norm='st1000'")));
        Assert.Equal("doc", Convert.ToString(db.Scalar("SELECT kind FROM files WHERE file_id=1")));
    }

    [Fact]
    public void V4Database_NewFeatureTables_AreUsableAfterMigration()
    {
        using var db = new Db(_dbPath);
        // 新機能（列上書き・FAQ候補）が移行済みDBに対して動く
        db.Exec("INSERT INTO jisseki_colmap(file_id, zuban, mitumori, genka, nouki) VALUES(1, 0, 1, -1, -1)");
        db.Exec("INSERT INTO faq_candidates(question, answer, comment) VALUES('候補の質問','候補の回答','テスト')");
        Assert.Equal(0, Convert.ToInt32(db.Query("SELECT zuban FROM jisseki_colmap WHERE file_id=1")[0]["zuban"]));
        Assert.Equal("pending", Convert.ToString(db.Query("SELECT status FROM faq_candidates")[0]["status"]));
    }

    [Fact]
    public void V4Database_FaqInstantAnswer_StillMatchesAfterMigration()
    {
        using var db = new Db(_dbPath);
        var faq = new FaqStore(db);
        var hit = faq.TryMatch("移行前の質問");
        Assert.NotNull(hit);
        Assert.Equal("移行前の回答", hit.Answer);
    }
}
