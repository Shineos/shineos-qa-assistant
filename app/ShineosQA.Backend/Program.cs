using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;

namespace ShineosQA.Backend;

/// <summary>簡易ロガー（構造化・日次ローテーション: error-codes-v2.md §8）</summary>
public interface ILogger
{
    void Info(string msg);
    void Warn(string msg);
    void Error(string msg);
}

public sealed class Logger : ILogger
{
    private readonly string _dir;
    public Logger(string dir) { _dir = dir; Directory.CreateDirectory(dir); }
    private void Write(string level, string msg)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg.Replace("\n", " ")}";
            Console.WriteLine(line);
            File.AppendAllText(Path.Combine(_dir, $"backend-{DateTime.Now:yyyyMMdd}.log"), line + "\n");
        }
        catch { }
    }
    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg) => Write("ERROR", msg);
}

public static class Api
{
    public static void MapRoutes(WebApplication app, AppCtx ctx)
    {
        var cfg = ctx.Cfg; var db = ctx.Db; var sup = ctx.Sup; var index = ctx.Index; var ingest = ctx.Ingest; var flow = ctx.Flow;

        app.MapGet("/health", () => Results.Json(new { status = true }));

        app.MapGet("/api/status", () => Results.Json(new
        {
            version = "2.0.0",
            tier = cfg.EffectiveTier,
            chat_model = cfg.ChatModelFile,
            chunks = index.Count,
            engines = sup.Status(),
            ram_gb = AppConfig.TotalRamGb(),
        }));

        app.MapGet("/api/settings", () => Results.Json(new
        {
            web_search = bool.TryParse(db.GetSetting("web_search", cfg.WebSearch.ToString()), out var w) && w,
            tier = db.GetSetting("tier", "auto"),
            idle_unload_minutes = cfg.IdleUnloadMinutes,
            bg_friendly = bool.TryParse(db.GetSetting("bg_friendly", cfg.BgFriendly.ToString()), out var b) && b,
            extensions = new { drawing = Extensions.IsEnabled(db, Extensions.DrawingId) },
        }));

        app.MapPost("/api/settings", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name == "web_search") db.SetSetting("web_search", p.Value.GetBoolean().ToString());
                if (p.Name == "bg_friendly")
                {
                    var v = p.Value.GetBoolean();
                    db.SetSetting("bg_friendly", v.ToString());
                    cfg.BgFriendly = v;
                    // 稼働中エンジンを停止して次の質問で新しい優先度を反映させる
                    sup.ApplyLlmPriority();
                }
                if (p.Name == "tier" && p.Value.GetString() is { } t && t is "auto" or "standard" or "quick" or "quality")
                {
                    db.SetSetting("tier", t); cfg.Tier = t;
                    // LLMのみ入れ替え（embed/rankは不変）。読み込みはバックグラウンドで即開始し、
                    // ユーザーが質問を入力している間に完了させる（初回回答の体感短縮）
                    var eff = t == "auto" ? (AppConfig.TotalRamGb() >= 12 ? "standard" : "quick") : t;
                    if (sup.SwitchLlmTier(eff))
                        _ = Task.Run(() => { try { sup.EnsureLlm(); } catch (Exception ex2) { ctx.Log.Warn($"llm reload after tier change failed: {ex2.Message}"); } });
                }
                // 拡張パック: {extensions:{drawing:true}} と 平坦キー ext.drawing の両方を受け付ける（即時反映）
                if (p.Name == "extensions" && p.Value.ValueKind == JsonValueKind.Object)
                    foreach (var e in p.Value.EnumerateObject())
                        if (e.Name == Extensions.DrawingId && e.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            Extensions.SetEnabled(db, Extensions.DrawingId, e.Value.GetBoolean());
                if (p.Name == "ext.drawing" && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    Extensions.SetEnabled(db, Extensions.DrawingId, p.Value.GetBoolean());
            }
            return Results.Ok(new { ok = true });
        });

        // ---- チャット履歴（uuidベース・URLルーティング /c/{uuid} 対応） ----
        app.MapGet("/api/chats", (HttpRequest req) => Results.Json(db.Query(
            "SELECT uuid, id, title, updated_at, archived FROM chats WHERE archived = $a ORDER BY updated_at DESC, id DESC LIMIT 200",
            ("$a", req.Query["archived"].ToString() == "1" ? 1 : 0))));

        app.MapPost("/api/chats", () => Results.Json(new { uuid = db.NewChatUuid() }));

        // チャットのアーカイブ切替（サイドバーの既定一覧から外す。データは残る）
        app.MapPost("/api/chats/{uuid}/archive", async (string uuid, HttpRequest req) =>
        {
            var id = db.ChatIdFromUuid(uuid);
            if (id == 0) return Results.NotFound(new { error = "not found" });
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
            bool archived = doc.RootElement.TryGetProperty("archived", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True;
            db.Exec("UPDATE chats SET archived=$v WHERE id=$i", ("$v", archived ? 1 : 0), ("$i", id));
            return Results.Ok(new { ok = true, archived });
        });

        app.MapGet("/api/chats/{uuid}", (string uuid) =>
        {
            var id = db.ChatIdFromUuid(uuid);
            if (id == 0) return Results.NotFound(new { error = "not found" });
            var chat = db.Query("SELECT uuid, title, archived FROM chats WHERE id=$i", ("$i", id));
            var msgs = db.Query("SELECT id, role, content, image, sources_json, created_at FROM messages WHERE chat_id=$i ORDER BY id", ("$i", id));
            return Results.Json(new { uuid, title = chat[0]["title"], archived = chat[0]["archived"], messages = msgs });
        });

        // キャプチャ画像の配信（ローカル保存された過去チャット添付画像。messages.image は files/captures/{name}.png の相対パス）
        app.MapGet("/api/messages/{id}/image", (long id) =>
        {
            var rows = db.Query("SELECT image FROM messages WHERE id=$i", ("$i", id));
            if (rows.Count == 0 || rows[0]["image"] is not string rel || rel.Length == 0)
                return Results.NotFound();
            var path = Path.Combine(ingest.FilesDir, "captures", Path.GetFileName(rel));
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "image/png");
        });

        app.MapDelete("/api/chats/{uuid}", (string uuid) =>
        {
            var id = db.ChatIdFromUuid(uuid);
            if (id != 0) db.Exec("DELETE FROM chats WHERE id=$i", ("$i", id));
            return Results.Ok(new { ok = true });
        });

        // ---- チャット本体（SSE） ----
        app.MapPost("/api/chat", (HttpContext http) => ctx.Flow.RunAsync(http));

        // 回答キャッシュ全消去（開発・運用用）
        app.MapPost("/api/cache-clear", () => { ctx.Flow.ClearCache(); return Results.Ok(new { ok = true }); });

        // SPAルーティング: /c/{uuid} などAPI以外のパスはindex.htmlへフォールバック
        app.MapFallback(async (HttpContext http) =>
        {
            if (http.Request.Path.StartsWithSegments("/api")) { http.Response.StatusCode = 404; return; }
            http.Response.ContentType = "text/html; charset=utf-8";
            await http.Response.SendFileAsync(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
        });

        // ---- モデル管理（初回起動ウィザード・Store対応のアプリ内DL） ----
        app.MapGet("/api/models", () => Results.Json(new { models = ctx.Models.Status(), needs_wizard = ctx.Models.NeedsWizard }));

        app.MapPost("/api/models/install", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            try
            {
                await ctx.Models.InstallAsync(id, req.HttpContext.RequestAborted);
                // 再ダウンロードで破損が修復された可能性があるため、LLM起動失敗のサーキットブレーカーを解放
                ctx.Sup.ResetLlmFailure();
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "SHINE_E_MODEL_DOWNLOAD_FAILED", message = ex.Message }, statusCode: 502);
            }
        });

        app.MapGet("/api/models/progress", () => Results.Json(ctx.Models.Progress));

        app.MapPost("/api/models/delete", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            try { ctx.Models.Delete(id); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.Json(new { ok = false, message = ex.Message }, statusCode: 400); }
        });

        // ---- ナレッジ ----
        app.MapGet("/api/knowledge", (HttpRequest req) =>
        {
            // 拡張パック: 図面メタデータを結合。?q= で図番(正規形)・品名・材質・ファイル名の部分一致フィルタ
            var q = (req.Query["q"].ToString() ?? "").Trim();
            var like = $"%{q}%";
            var norm = q.Length > 0 ? $"%{Rag.NormalizeZuban(q)}%" : "";
            var sql = q.Length == 0
                ? "SELECT f.file_id, f.name, f.status, f.error, f.chunk_count, f.added_at, f.kind, d.zuban_raw, d.hinmei, d.zairyo, d.revision " +
                  "FROM files f LEFT JOIN drawing_meta d ON d.file_id=f.file_id ORDER BY f.file_id DESC"
                : "SELECT f.file_id, f.name, f.status, f.error, f.chunk_count, f.added_at, f.kind, d.zuban_raw, d.hinmei, d.zairyo, d.revision " +
                  "FROM files f LEFT JOIN drawing_meta d ON d.file_id=f.file_id " +
                  "WHERE f.name LIKE $q OR d.zuban_norm LIKE $qn OR d.hinmei LIKE $q OR d.zairyo LIKE $q ORDER BY f.file_id DESC";
            return Results.Json(db.Query(sql, ("$q", like), ("$qn", norm)));
        });

        // 拡張パック（図面）: サムネイルと元ファイル。パック有効時に取り込んだファイルのみ存在する
        // （パックを後からOFFにしてもデータは残るため、ゲートせずファイルの有無で応答する）
        app.MapGet("/api/knowledge/{id}/thumb", (HttpContext http, long id) =>
        {
            var path = Path.Combine(ingest.FilesDir, $"{id}.thumb.png");
            if (!System.IO.File.Exists(path)) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, "image/png");
        });

        app.MapGet("/api/knowledge/{id}/file", (long id) =>
        {
            if (!Directory.Exists(ingest.FilesDir)) return Results.NotFound();
            var file = Directory.EnumerateFiles(ingest.FilesDir, $"{id}.*")
                .FirstOrDefault(p => !p.EndsWith(".thumb.png"));
            if (file is null) return Results.NotFound();
            var ct = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".md" => "text/markdown",
                _ => "text/plain",
            };
            return Results.File(file, ct);
        });

        app.MapPost("/api/knowledge", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "SHINE_E_BAD_REQUEST", message = "multipart/form-data が必要です" });
            var form = await req.ReadFormAsync();
            if (form.Files.Count == 0) return Results.BadRequest(new { error = "SHINE_E_BAD_REQUEST", message = "ファイルがありません" });
            var results = new List<object>();
            foreach (var f in form.Files)
            {
                if (f.Length > 100 * 1024 * 1024)
                { results.Add(new { name = f.FileName, ok = false, error = "SHINE_E_DOC_TOO_LARGE" }); continue; }
                // ファイル名正規化: パーセントエンコードされたUTF-8（filename*形式）をデコードし、パス区切りを除去
                var name = f.FileName;
                try
                {
                    var decoded = Uri.UnescapeDataString(name);
                    if (decoded != name && decoded.Any(c => c > 0x7F) && !decoded.Contains('?') && !decoded.Contains('\uFFFD')) name = decoded;
                }
                catch { }
                name = name.Replace('\\', '_').Replace('/', '_');
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (!Ingest.SupportedExtensions.Contains(ext))
                { results.Add(new { name, ok = false, error = "SHINE_E_DOC_PARSE_FAILED", message = $"未対応形式です: {ext}" }); continue; }
                try
                {
                    using var s = f.OpenReadStream();
                    var id = await ingest.IngestFileAsync(Path.GetFileName(name), s, req.HttpContext.RequestAborted);
                    results.Add(new { name, ok = true, file_id = id });
                }
                catch (Exception ex)
                {
                    results.Add(new { name, ok = false, error = "SHINE_E_DOC_PARSE_FAILED", message = ex.Message });
                }
            }
            return Results.Json(new { results });
        });

        app.MapPost("/api/knowledge/import", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var path = doc.RootElement.GetProperty("path").GetString()!;
            // パス穿越防止: 実在ディレクトリのみ、.. は拒否
            if (path.Contains("..") || !Directory.Exists(path))
                return Results.BadRequest(new { error = "SHINE_E_BAD_REQUEST", message = "無効なパスです" });
            var count = 0;
            var failures = new List<object>();
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!Ingest.SupportedExtensions.Contains(ext)) continue;
                try
                {
                    await using var s = File.OpenRead(f);
                    await ingest.IngestFileAsync(Path.GetFileName(f), s, req.HttpContext.RequestAborted);
                    count++;
                }
                catch (Exception ex)
                {
                    // 1ファイルの解析失敗（破損PDF等）でフォルダ全体の取り込みを止めない
                    ctx.Log.Warn($"import skipped {Path.GetFileName(f)}: {ex.Message}");
                    failures.Add(new { name = Path.GetFileName(f), error = ex.Message });
                }
            }
            return Results.Json(new { imported = count, failed = failures });
        });

        // 拡張パック（図面）: キャプチャ画像のOCR（Windows内蔵エンジン・完全オフライン）
        app.MapPost("/api/ocr", async (HttpRequest req) =>
        {
            if (!Extensions.IsEnabled(db, Extensions.DrawingId))
                return Results.Json(new { error = "SHINE_E_EXTENSION_DISABLED", message = "図面拡張機能が無効です（設定で有効にしてください）" }, statusCode: 503);
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "SHINE_E_BAD_REQUEST", message = "multipart/form-data が必要です" });
            var form = await req.ReadFormAsync();
            var f = form.Files.FirstOrDefault();
            if (f is null || f.Length == 0 || f.Length > 20 * 1024 * 1024)
                return Results.Json(new { error = "SHINE_E_BAD_REQUEST", message = "画像がありません" }, statusCode: 400);
            if (!Ocr.IsAvailable())
                return Results.Json(new { error = "SHINE_E_OCR_UNAVAILABLE", message = "日本語OCRエンジンが利用できません。Windowsの設定で日本語言語パックを導入してください。" }, statusCode: 503);
            using var s = f.OpenReadStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var (text, zubans) = await Ocr.RecognizeAsync(ms.ToArray());
            return Results.Json(new { text, zubans });
        });

        app.MapDelete("/api/knowledge/{id}", (long id) =>
        {
            db.Exec("DELETE FROM files WHERE file_id=$i", ("$i", id));
            index.RemoveFile(id);
            ingest.DeleteStoredFiles(id); // 拡張パック: 元ファイル・サムネイルを掃除
            return Results.Ok(new { ok = true });
        });
    }
}

public sealed class AppCtx
{
    public required AppConfig Cfg;
    public required ILogger Log;
    public required Db Db;
    public required Supervisor Sup;
    public required ChunkIndex Index;
    public required Ingest Ingest;
    public required ChatFlow Flow;
    public required ModelManager Models;
}

public sealed class Program
{
    // 終了コード（docs/error-codes-v2.md §5・インストーラ終了コードと重複しない番号帯）:
    //   0=正常停止 / 20=config.json破損 / 21=ポートバインド失敗 / 22=knowledge.db破損 / 30=想定外例外
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        AppConfig? cfg = null;
        Logger? log = null;
        try
        {
            // MSIX（WindowsApps）検知: パッケージインストール先は読み取り専用のため、
            // 書き込み先（data/models/config）を LocalState へ、読み取り専用（engine/同梱モデル）はパッケージ内を指す
            if (IsRunningFromMsix())
            {
                ApplyMsixPaths();
            }
            EnsureDefaultConfig(args);
            cfg = AppConfig.Load(args);
            // エンコーディング防御: UTF-8以外で保存されたconfig.jsonは置換文字(U+FFFD)を含む。
            // 化けたパスで予期しない場所にディレクトリを作る前に検出して失敗させる（終了コード20）
            if (cfg.DataDir.Contains('\uFFFD') || cfg.EngineDir.Contains('\uFFFD') || cfg.ModelsDir.Contains('\uFFFD'))
                throw new InvalidDataException("config.json をUTF-8として読み取れません（エンコーディング不正）(SHINE_E_CONFIG_ENCODING)");
            var dataDir = Path.IsPathRooted(cfg.DataDir) ? cfg.DataDir : Path.Combine(AppContext.BaseDirectory, cfg.DataDir);
            Directory.CreateDirectory(dataDir);
            log = new Logger(Path.Combine(dataDir, "logs"));
            if (IsRunningFromMsix()) CopyBundledModels(cfg);
            await RunAsync(cfg, log);
        }
        catch (InvalidDataException ex)
        {
            log?.Error($"config invalid: {ex.Message}");
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 20;
        }
        catch (Exception ex) when (IsPortInUse(ex))
        {
            log?.Error($"port bind failed: {ex.Message}");
            Environment.ExitCode = 21;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            log?.Error($"knowledge.db error: {ex.Message}");
            Environment.ExitCode = 22;
        }
        catch (Exception ex)
        {
            // 設定読込に失敗している（ログ初期化前）なら 20、それ以外の致命的例外は 30
            log?.Error($"fatal: {ex}");
            Environment.ExitCode = cfg is null ? 20 : 30;
        }
    }

    static bool IsPortInUse(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException se &&
                se.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                return true;
            var msg = e.Message;
            if (msg.Contains("already in use", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>MSIX（WindowsApps）から実行されているか。パッケージインストール先は読み取り専用のため
    /// 書き込み先を LocalState へ切り替える必要がある</summary>
    static bool IsRunningFromMsix()
    {
        return AppContext.BaseDirectory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>MSIX環境でのパス切替: config.json を LocalState に置き、読み取り専用のengine/モデルはパッケージ内を指す。
    /// 書き込み先（data/models）は LocalState に変更し、同梱モデルは初回起動時に LocalState 側へコピーする</summary>
    static void ApplyMsixPaths()
    {
        var pkgDir = AppContext.BaseDirectory; // C:\Program Files\WindowsApps\<pkg>\
        var localState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            GetPackageName(pkgDir),
            "LocalState");
        MsixLocalState = localState;
        MsixPkgDir = pkgDir;
        // config.json を LocalState にリダイレクト（EnsureDefaultConfigが localState 側に書く）
        // engine と同梱モデルはパッケージ内（読み取り専用で問題ない）
        Console.WriteLine($"MSIX detected: pkg={pkgDir}, localState={localState}");
    }

    static string? MsixLocalState;
    static string? MsixPkgDir;

    static string GetPackageName(string pkgDir)
    {
        // C:\Program Files\WindowsApps\ShineosQA_2.0.3.0_x64__n5zmjbd5e3v64\ → ShineosQA_n5zmjbd5e3v64
        var name = Path.GetFileName(Path.GetDirectoryName(pkgDir)?.TrimEnd(Path.DirectorySeparatorChar) ?? "");
        // バージョンとアーキテクチャ部分を除去
        var parts = name.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[^1] : name;
    }

    /// <summary>MSIX: 同梱モデルをパッケージ内（読み取り専用）から LocalState\models へコピー。
    /// config.json の models_dir が LocalState 側を指すため、初回起動時に必須</summary>
    static void CopyBundledModels(AppConfig cfg)
    {
        if (MsixPkgDir is null || MsixLocalState is null) return;
        var srcDir = Path.Combine(MsixPkgDir, "models");
        var dstDir = Path.Combine(MsixLocalState, "models");
        if (!Directory.Exists(srcDir)) return;
        Directory.CreateDirectory(dstDir);
        foreach (var f in Directory.GetFiles(srcDir, "*.gguf"))
        {
            var dst = Path.Combine(dstDir, Path.GetFileName(f));
            if (!File.Exists(dst))
            {
                File.Copy(f, dst, overwrite: false);
                Console.WriteLine($"copied bundled model: {Path.GetFileName(f)} ({new FileInfo(f).Length / 1024 / 1024}MB)");
            }
        }
    }

    /// <summary>config.json が無い場合（インストール直後）にUTF-8で既定configを生成する。
    /// パスは実行ディレクトリ基準の絶対パス（フォワードスラッシュ）。
    /// MSIX実行時は config.json を LocalState に置き、data/models も LocalState 側、engine はパッケージ内を指す。
    /// --config で外部configを指定された場合は生成しない（検証・開発用にpublishフォルダから起動した際に
    /// 既定config.jsonが書き出され、それが意図せずインストーラに同梱される事故を防ぐ）</summary>
    static void EnsureDefaultConfig(string[]? args = null)
    {
        if (args is not null)
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--config" && !string.IsNullOrWhiteSpace(args[i + 1])) return;
        var isMsix = MsixLocalState is not null;
        var configDir = isMsix ? MsixLocalState! : AppContext.BaseDirectory;
        var path = Path.Combine(configDir, "config.json");
        if (File.Exists(path)) return;
        // LocalState等の親ディレクトリが未作成の場合（MSIX初回起動・手動テスト）に作成する
        Directory.CreateDirectory(configDir);
        var baseDir = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/');
        var localDir = isMsix ? MsixLocalState!.Replace('\\', '/').TrimEnd('/') : baseDir;
        var engineDir = isMsix ? baseDir + "/engine" : baseDir + "/engine"; // engineは常にパッケージ/exe内（読み取り専用）
        var modelsDir = isMsix ? localDir + "/models" : baseDir + "/models"; // 追加DLがあるため書き込み可能な場所
        var dataDir = isMsix ? localDir + "/data" : baseDir + "/data";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            port = 8300,
            data_dir = dataDir,
            engine_dir = engineDir,
            engine_variant = "cpu",
            models_dir = modelsDir,
            standard_model = "Qwen3-4B-Instruct-2507-IQ4_XS.gguf",
            quick_model = "Qwen3-1.7B-IQ4_XS.gguf",
            quality_model = "Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf",
            tier = "quick",
            ctx_size = 4096,
            bg_friendly = true
        }, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(path, json); // UTF-8（BOMなし）
        Console.WriteLine($"created default config.json at {path} (first run, msix={isMsix})");
    }

    static async Task RunAsync(AppConfig cfg, Logger log)
    {
        // tierは設定DBで上書き
        log.Info($"ShineosQA.Backend starting: tier={cfg.Tier} ram={AppConfig.TotalRamGb()}GB port={cfg.Port}");
        var db = new Db(Path.Combine(Path.IsPathRooted(cfg.DataDir) ? cfg.DataDir : Path.Combine(AppContext.BaseDirectory, cfg.DataDir), "knowledge.db"));
        var savedTier = db.GetSetting("tier", "");
        if (savedTier is "auto" or "standard" or "quick") cfg.Tier = savedTier;
        else db.SetSetting("tier", cfg.Tier); // 初回はconfig.jsonの階級を永続化
        if (bool.TryParse(db.GetSetting("bg_friendly", ""), out var savedBg)) cfg.BgFriendly = savedBg;

        var gw = new LlmGateway();
        var sup = new Supervisor(cfg, gw, log);
        var index = new ChunkIndex();
        index.LoadFrom(db);
        var ingest = new Ingest(db, gw, sup, index, cfg, log);
        var flow = new ChatFlow(cfg, db, sup, gw, index, new WebSearch(), log);
        var ctx = new AppCtx { Cfg = cfg, Db = db, Sup = sup, Index = index, Ingest = ingest, Flow = flow, Models = new ModelManager(cfg, log), Log = log };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, cfg.Port));
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        Api.MapRoutes(app, ctx);

        // embed+rankは常駐、LLMは初回チャット時に起動（アイドルで自動解放）
        try { sup.StartEmbedAndRank(); } catch (Exception ex) { log.Error($"engine startup failed: {ex.Message}"); }
        // エンジン事前ロード＋ウォームアップ: LLMはモデル読込後にダミー推論で計算グラフと
        // システムプロンプトの接頭キャッシュを済ませる（Supervisor内）。embed/rankも初回呼出を暖める
        _ = Task.Run(async () =>
        {
            try
            {
                sup.EnsureLlm();
                await gw.EmbedAsync(cfg.EnginePortEmb, new[] { "ウォームアップ" }, CancellationToken.None);
                await gw.RerankAsync(cfg.EnginePortRank, "ウォームアップ", new[] { "ウォームアップ" }, 1, CancellationToken.None);
                log.Info("engine warmup done (embed/rank; llm warmed in supervisor)");
            }
            catch (Exception ex) { log.Warn($"engine warmup failed: {ex.Message}"); }
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            log.Info("shutting down: stopping engines");
            sup.StopAll();
            db.Dispose();
        });

        log.Info($"listening on http://127.0.0.1:{cfg.Port}  (chunks={index.Count})");
        await app.RunAsync();
    }
}
