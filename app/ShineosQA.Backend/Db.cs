using Microsoft.Data.Sqlite;

namespace ShineosQA.Backend;

/// <summary>SQLite永続化（knowledge.db）。設計: architecture-v2.md §5.4。
/// Phase 0.2: foreign_keys=ON（DELETE CASCADEの実効化）・起動時孤児行クリーンアップ・schema_version・
/// files.content_hash（取り込み重複排除）・ExecTransaction（取り込みの原子的確定）を追加</summary>
public sealed class Db : IDisposable
{
    /// <summary>適用済みスキーマ版。1=初期スキーマ〜図面/実績拡張、2=content_hash＋原子性（Phase 0.2）、
    /// 3=FAQ（現場の正解）＋別名（Phase B）、4=回答フィードバック（Phase F）、
    /// 5=実績列マッピングの手動上書き＋FAQ候補（フィードバック→採用フロー）。マイグレーションを追加したら
    /// この値を上げ、下の適用ブロックも更新する</summary>
    public const int SchemaVersion = 7;

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection($"Data Source={path};Cache=Shared");
        _conn.Open();
        // WALと併用。foreign_keysは接続単位の設定のため、接続を開くたび（＝起動ごと）に有効化する。
        // これにより chunks/drawing_meta/jisseki_meta の ON DELETE CASCADE が実際に効く
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA foreign_keys=ON");
        InitSchema();
        CleanupOrphans();
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
                sources_json TEXT, created_at TEXT DEFAULT (datetime('now')),
                kind TEXT);
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
        // 応答種別（v2.3.0）: 'answer' / 'refusal' / 'clarify'。NULL=旧データ（kind契約導入前）
        try { Exec("ALTER TABLE messages ADD COLUMN kind TEXT"); } catch { }
        // チャットアーカイブ: 0=通常 / 1=アーカイブ済み（一覧の既定表示から除外）
        try { Exec("ALTER TABLE chats ADD COLUMN archived INTEGER NOT NULL DEFAULT 0"); } catch { }
        // チャットブックマーク: 0=通常 / 1=ブックマーク済み（⭐表示）
        try { Exec("ALTER TABLE chats ADD COLUMN bookmarked INTEGER NOT NULL DEFAULT 0"); } catch { }
        // 取り込み重複排除（Phase 0.2）: 元ファイル内容のSHA-256 hex。既存DBはNULLのまま
        // （重複判定は status='ready' かつ同一hashの行のみを対象にするため、NULL行は従来どおり取り込める）
        try { Exec("ALTER TABLE files ADD COLUMN content_hash TEXT"); } catch { }
        Exec("""
            CREATE TABLE IF NOT EXISTS drawing_meta(
              file_id INTEGER PRIMARY KEY REFERENCES files(file_id) ON DELETE CASCADE,
              zuban_raw TEXT, zuban_norm TEXT, hinmei TEXT, zairyo TEXT,
              scale TEXT, revision TEXT, approved_at TEXT);
            CREATE INDEX IF NOT EXISTS idx_drawing_meta_norm ON drawing_meta(zuban_norm);
            """);
        // 実績接続（v2.3.0 C1）: 実績表(.xlsx/.csv)から抽出した図番キー実績（見積・原価・納期）。
        // 1ファイル複数行。図面QAの回答文脈へ過去実績を接続するために使用
        Exec("""
            CREATE TABLE IF NOT EXISTS jisseki_meta(
              file_id INTEGER NOT NULL REFERENCES files(file_id) ON DELETE CASCADE,
              row_no INTEGER NOT NULL,
              zuban_raw TEXT, zuban_norm TEXT,
              mitumori TEXT, genka TEXT, nouki TEXT,
              PRIMARY KEY(file_id, row_no));
            CREATE INDEX IF NOT EXISTS idx_jisseki_meta_norm ON jisseki_meta(zuban_norm);
            """);
        // 現場の正解（FAQ）: DifyのAnnotation Replyに相当。LLMを使わない即答の供給源。
        // question_norm は QuestionNorm.Normalize（NFKC・小文字・空白/句読点除去・カタカナ→ひらがな）。
        // status: published=即答に使う / candidate=改善候補（承認待ち）
        Exec("""
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
            """);
        // 回答フィードバック（Phase F）: 👍/👎と修正回答をローカル保存し、FAQ候補の承認につなげる。
        // 外部送信なし。message_idはassistantメッセージ（sources_json等）への参照
        Exec("""
            CREATE TABLE IF NOT EXISTS feedback(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              message_id INTEGER,
              chat_id INTEGER,
              rating INTEGER NOT NULL,
              comment TEXT,
              corrected_answer TEXT,
              created_at TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE INDEX IF NOT EXISTS idx_feedback_created ON feedback(created_at);
            """);
        // 実績列マッピングの手動上書き（v2.7.0）: 自動検出に対する管理者の訂正を保持する。
        // 値は列インデックス（-1=その役割の列なし）。PUT /api/jisseki/{id}/columns で更新し、
        // 元ファイルの再パース→jisseki_metaの作り直しまでを1処理で行う
        Exec("""
            CREATE TABLE IF NOT EXISTS jisseki_colmap(
              file_id INTEGER PRIMARY KEY REFERENCES files(file_id) ON DELETE CASCADE,
              zuban INTEGER NOT NULL DEFAULT -1,
              mitumori INTEGER NOT NULL DEFAULT -1,
              genka INTEGER NOT NULL DEFAULT -1,
              nouki INTEGER NOT NULL DEFAULT -1,
              updated_at TEXT NOT NULL DEFAULT (datetime('now')));
            """);
        // FAQ候補（v2.7.0）: 👎フィードバックから自動生成し、管理者が編集してFAQへ採用する。
        // status: pending=未処理 / adopted=FAQ採用済み。採用は faq テーブルへのUpsertとセット。
        // rating: 生成元の評価（1=👍役に立った / -1=👎改善してほしい）。v5 DBで列不足の環境には
        // 下のALTER（try/catch）で追加する
        Exec("""
            CREATE TABLE IF NOT EXISTS faq_candidates(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              question TEXT NOT NULL,
              answer TEXT NOT NULL,
              comment TEXT,
              corrected_answer TEXT,
              source_message_id INTEGER,
              chat_id INTEGER,
              rating INTEGER NOT NULL DEFAULT 0,
              status TEXT NOT NULL DEFAULT 'pending',
              faq_id INTEGER,
              created_at TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE INDEX IF NOT EXISTS idx_faq_candidates_status ON faq_candidates(status);
            """);
        try { Exec("ALTER TABLE faq_candidates ADD COLUMN rating INTEGER NOT NULL DEFAULT 0"); }
        catch { /* v5で既に列がある環境 */ }
        // Dify風ナレッジ一覧（v2.7.0）: 検索回数（質問で参照された回数・検索ヒット時に加算）と
        // 有効/無効トグル（0=検索対象から一時除外）。Difyの「検索回数」列・トグルスイッチに対応。
        // スキーマv6
        try { Exec("ALTER TABLE files ADD COLUMN hit_count INTEGER NOT NULL DEFAULT 0"); } catch { }
        try { Exec("ALTER TABLE files ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1"); } catch { }
        // 回答キャッシュの質問一致を生質問の埋め込みに修正（v2.7.1）: 展開済みクエリの埋め込みでは
        // 「国内/海外出張の日当」のような答えが違う近似質問まで誤ヒットする実測のため、
        // 生質問の埋め込みを別列で保持する（旧行はNULL＝正規化質問の完全一致でのみ許可）
        try { Exec("ALTER TABLE answer_cache ADD COLUMN raw_emb BLOB"); } catch { }
        // スキーマ版管理（Phase 0.2）: 1行のみ保持し、コード側の現行版との差で将来のマイグレーションを判定できる。
        // 適用は「記録が古い場合のみ更新」する upsert（現行版を記録するだけ。DOWNは扱わない）
        Exec("""
            CREATE TABLE IF NOT EXISTS schema_version(
              id INTEGER PRIMARY KEY CHECK(id = 1),
              version INTEGER NOT NULL,
              updated_at TEXT NOT NULL DEFAULT (datetime('now')));
            """);
        Exec("INSERT INTO schema_version(id, version, updated_at) VALUES(1, $v, datetime('now')) "
            + "ON CONFLICT(id) DO UPDATE SET version=excluded.version, updated_at=excluded.updated_at "
            + "WHERE schema_version.version < excluded.version", ("$v", SchemaVersion));
        // content_hash の検索用インデックス（重複判定は files(content_hash) の等値検索のみ）
        Exec("CREATE INDEX IF NOT EXISTS idx_files_content_hash ON files(content_hash)");
        MigrateChatsUuid();
    }

    /// <summary>起動時に1回だけ実行する孤児行クリーンアップ（Phase 0.2）。
    /// foreign_keys=ON以前のDBや、当時のFK未実効により files から消えた file_id を持つ
    /// chunks/drawing_meta/jisseki_meta を削除する。通常は孤児が無いため EXISTS の短絡探索のみで終わり、
    /// 大量データでもコストはほぼゼロ（1行でも見つかった時だけトランザクションで削除する）。
    /// 失敗しても起動は止めない（整合性の問題はFK/今後の削除で再発しないため）</summary>
    private void CleanupOrphans()
    {
        try
        {
            var orphan = Convert.ToInt64(Scalar("""
                SELECT EXISTS(SELECT 1 FROM chunks c WHERE NOT EXISTS(SELECT 1 FROM files f WHERE f.file_id=c.file_id))
                    OR EXISTS(SELECT 1 FROM drawing_meta d WHERE NOT EXISTS(SELECT 1 FROM files f WHERE f.file_id=d.file_id))
                    OR EXISTS(SELECT 1 FROM jisseki_meta j WHERE NOT EXISTS(SELECT 1 FROM files f WHERE f.file_id=j.file_id))
                """));
            if (orphan == 0) return;
            ExecTransaction(new[]
            {
                ("DELETE FROM chunks WHERE file_id NOT IN (SELECT file_id FROM files)", Array.Empty<(string, object?)>()),
                ("DELETE FROM drawing_meta WHERE file_id NOT IN (SELECT file_id FROM files)", Array.Empty<(string, object?)>()),
                ("DELETE FROM jisseki_meta WHERE file_id NOT IN (SELECT file_id FROM files)", Array.Empty<(string, object?)>()),
            });
        }
        catch { /* クリーンアップ失敗で起動を妨げない（次回起動で再試行される） */ }
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

    /// <summary>複数コマンドを単一接続・単一トランザクションで実行する（Phase 0.2）。
    /// 取り込みの最終確定（files.status/chunk_count/kind ＋ chunks全件 ＋ drawing_meta/jisseki_meta）を
    /// 原子的に行うための汎用API。1つでも失敗したらROLLBACKして例外を再送出し、部分確定を残さない。
    /// 既存のExec/Queryと同じ _lock 内で実行するため他の読み書きとは直列化される（Monitorは再入可能）</summary>
    public void ExecTransaction(IReadOnlyList<(string Sql, (string Name, object? Value)[] Params)> commands)
    {
        lock (_lock)
        {
            Exec("BEGIN");
            try
            {
                foreach (var (sql, ps) in commands)
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = sql;
                    foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                Exec("COMMIT");
            }
            catch
            {
                try { Exec("ROLLBACK"); } catch { /* BEGIN前に失敗した等。原例外を優先 */ }
                throw;
            }
        }
    }

    /// <summary>適用済みスキーマ版（未記録=0）。テスト・将来のマイグレーション判定用</summary>
    public int GetSchemaVersion() =>
        Convert.ToInt32(Scalar("SELECT version FROM schema_version WHERE id=1") ?? 0);

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

    /// <summary>取り込み済みチャンクの一括取得（RAG索引ロード用）。
    /// enabled=0（トグルで一時無効化された文書）は検索対象から除外する（Difyのトグルと同義）</summary>
    public List<ChunkRow> LoadReadyChunks()
    {
        const string sql =
            "SELECT c.chunk_id, c.file_id, f.name, c.seq, c.text, c.emb FROM chunks c JOIN files f ON f.file_id=c.file_id WHERE f.status=$s AND f.enabled=1";
        return Query(sql, ("$s", "ready"))
            .Select(r => new ChunkRow((long)r["chunk_id"]!, (long)r["file_id"]!, (string)r["name"]!, Convert.ToInt32(r["seq"]!), (string)r["text"]!, (byte[])r["emb"]!))
            .ToList();
    }

    public void InsertChunk(long fileId, int seq, string text, byte[] emb) =>
        Exec("INSERT INTO chunks(file_id, seq, text, emb) VALUES($f,$s,$t,$e)",
            ("$f", fileId), ("$s", seq), ("$t", text), ("$e", emb));

    /// <summary>同一内容（SHA-256 hex）で status='ready' の既存ファイルIDを返す（無ければnull）。
    /// 取り込み重複排除の判定用。status='error'/'parsing'/'embedding' の行は対象外
    /// （失敗した取り込みを「重複」として握りつぶさず、再取り込みを許可する）</summary>
    public long? FindReadyFileByHash(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return null;
        var v = Scalar("SELECT file_id FROM files WHERE content_hash=$h AND status='ready' ORDER BY file_id LIMIT 1",
            ("$h", contentHash));
        return v is null ? null : Convert.ToInt64(v);
    }

    /// <summary>同一ファイル名（Windows慣習どおり大文字小文字を区別しない）で status='ready' の
    /// 既存行を返す。取り込み時の同名置き換え（訂正版の再登録）に使う（v2.7.0）</summary>
    public long? FindReadyFileByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var v = Scalar("SELECT file_id FROM files WHERE name=$n COLLATE NOCASE AND status='ready' ORDER BY file_id LIMIT 1",
            ("$n", name));
        return v is null ? null : Convert.ToInt64(v);
    }

    public string GetSetting(string key, string def)
    {
        var v = Scalar("SELECT value FROM settings WHERE key=$k", ("$k", key));
        return v as string ?? def;
    }

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));

    public void Dispose() => _conn.Dispose();
}
