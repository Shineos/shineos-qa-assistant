using Microsoft.Data.Sqlite;

namespace ShineosQA.Backend;

/// <summary>SQLite永続化（knowledge.db）。設計: architecture-v2.md §5.4</summary>
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection($"Data Source={path};Cache=Shared");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL");
        InitSchema();
    }

    private void InitSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
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
                sources_json TEXT, created_at TEXT DEFAULT (datetime('now')));
            CREATE TABLE IF NOT EXISTS answer_cache(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                question TEXT NOT NULL, answer TEXT NOT NULL, sources_json TEXT,
                emb BLOB NOT NULL, model TEXT NOT NULL DEFAULT '', hits INTEGER DEFAULT 0, created_at TEXT DEFAULT (datetime('now')));
            CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);
            CREATE INDEX IF NOT EXISTS idx_messages_chat ON messages(chat_id);
            """);
        // 旧DBからのマイグレーション: 回答キャッシュにモデル識別列を追加（初回のみ成功）
        try { Exec("ALTER TABLE answer_cache ADD COLUMN model TEXT NOT NULL DEFAULT ''"); } catch { }
        // 拡張パック（図面検索）: files種別列 + 図面メタデータ（docs/impl-drawing-search.md §1.4）
        try { Exec("ALTER TABLE files ADD COLUMN kind TEXT NOT NULL DEFAULT 'doc'"); } catch { }
        // キャプチャ画像: ユーザーメッセージに添付画像の保存相対パス（data/files/captures/...）。NULL=画像なし
        try { Exec("ALTER TABLE messages ADD COLUMN image TEXT"); } catch { }
        // チャットアーカイブ: 0=通常 / 1=アーカイブ済み（一覧の既定表示から除外）
        try { Exec("ALTER TABLE chats ADD COLUMN archived INTEGER NOT NULL DEFAULT 0"); } catch { }
        Exec("""
            CREATE TABLE IF NOT EXISTS drawing_meta(
              file_id INTEGER PRIMARY KEY REFERENCES files(file_id) ON DELETE CASCADE,
              zuban_raw TEXT, zuban_norm TEXT, hinmei TEXT, zairyo TEXT,
              scale TEXT, revision TEXT, approved_at TEXT);
            CREATE INDEX IF NOT EXISTS idx_drawing_meta_norm ON drawing_meta(zuban_norm);
            """);
        MigrateChatsUuid();
    }

    /// <summary>chats.uuid カラムの追加と既存行へのバックフィル（URLルーティング /c/{uuid} 用）</summary>
    private void MigrateChatsUuid()
    {
        var cols = Query("PRAGMA table_info(chats)");
        if (cols.All(c => (string)c["name"]! != "uuid"))
            Exec("ALTER TABLE chats ADD COLUMN uuid TEXT");
        foreach (var row in Query("SELECT id FROM chats WHERE uuid IS NULL"))
        {
            var uuid = Guid.NewGuid().ToString("N");
            Exec("UPDATE chats SET uuid=$u WHERE id=$i", ("$u", uuid), ("$i", row["id"]));
        }
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS idx_chats_uuid ON chats(uuid)");
    }

    public string NewChatUuid()
    {
        var uuid = Guid.NewGuid().ToString("N");
        Exec("INSERT INTO chats(title, uuid) VALUES('新しいチャット', $u)", ("$u", uuid));
        return uuid;
    }

    /// <summary>uuid でチャットIDを解決（未指定・不在なら0）</summary>
    public long ChatIdFromUuid(string uuidOrId)
    {
        var row = Query("SELECT id FROM chats WHERE uuid=$u", ("$u", uuidOrId));
        if (row.Count > 0) return (long)row[0]["id"]!;
        if (long.TryParse(uuidOrId, out var id))
        {
            var byId = Query("SELECT id FROM chats WHERE id=$i", ("$i", id));
            if (byId.Count > 0) return id;
        }
        return 0;
    }

    public string? ChatUuid(long id)
    {
        var v = Scalar("SELECT uuid FROM chats WHERE id=$i", ("$i", id));
        return v as string;
    }

    public int Exec(string sql, params (string, object?)[] p)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            return cmd.ExecuteNonQuery();
        }
    }

    public object? Scalar(string sql, params (string, object?)[] p)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            return cmd.ExecuteScalar();
        }
    }

    public List<Dictionary<string, object?>> Query(string sql, params (string, object?)[] p)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (r.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
    }

    public long LastInsertId() => Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));

    public sealed record ChunkRow(long ChunkId, long FileId, string FileName, int Seq, string Text, byte[] Emb);

    /// <summary>取り込み済みチャンクの一括取得（RAG索引ロード用）</summary>
    public List<ChunkRow> LoadReadyChunks()
    {
        const string sql =
            "SELECT c.chunk_id, c.file_id, f.name, c.seq, c.text, c.emb FROM chunks c JOIN files f ON f.file_id=c.file_id WHERE f.status=$s";
        return Query(sql, ("$s", "ready"))
            .Select(r => new ChunkRow((long)r["chunk_id"]!, (long)r["file_id"]!, (string)r["name"]!, Convert.ToInt32(r["seq"]!), (string)r["text"]!, (byte[])r["emb"]!))
            .ToList();
    }

    public void InsertChunk(long fileId, int seq, string text, byte[] emb) =>
        Exec("INSERT INTO chunks(file_id, seq, text, emb) VALUES($f,$s,$t,$e)",
            ("$f", fileId), ("$s", seq), ("$t", text), ("$e", emb));

    public string GetSetting(string key, string def)
    {
        var v = Scalar("SELECT value FROM settings WHERE key=$k", ("$k", key));
        return v as string ?? def;
    }

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));

    public void Dispose() => _conn.Dispose();
}
