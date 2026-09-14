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
            version = "2.0.0-dev",
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
        }));

        app.MapPost("/api/settings", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name == "web_search") db.SetSetting("web_search", p.Value.GetBoolean().ToString());
                if (p.Name == "tier" && p.Value.GetString() is { } t && t is "auto" or "standard" or "quick" or "quality")
                {
                    db.SetSetting("tier", t); cfg.Tier = t;
                    // LLMのみ入れ替え（embed/rankは不変）。読み込みはバックグラウンドで即開始し、
                    // ユーザーが質問を入力している間に完了させる（初回回答の体感短縮）
                    var eff = t == "auto" ? (AppConfig.TotalRamGb() >= 12 ? "standard" : "quick") : t;
                    if (sup.SwitchLlmTier(eff))
                        _ = Task.Run(() => { try { sup.EnsureLlm(); } catch (Exception ex2) { ctx.Log.Warn($"llm reload after tier change failed: {ex2.Message}"); } });
                }
            }
            return Results.Ok(new { ok = true });
        });

        // ---- チャット履歴（uuidベース・URLルーティング /c/{uuid} 対応） ----
        app.MapGet("/api/chats", () => Results.Json(db.Query(
            "SELECT uuid, id, title, updated_at FROM chats ORDER BY updated_at DESC, id DESC LIMIT 200")));

        app.MapPost("/api/chats", () => Results.Json(new { uuid = db.NewChatUuid() }));

        app.MapGet("/api/chats/{uuid}", (string uuid) =>
        {
            var id = db.ChatIdFromUuid(uuid);
            if (id == 0) return Results.NotFound(new { error = "not found" });
            var chat = db.Query("SELECT uuid, title FROM chats WHERE id=$i", ("$i", id));
            var msgs = db.Query("SELECT role, content, sources_json, created_at FROM messages WHERE chat_id=$i ORDER BY id", ("$i", id));
            return Results.Json(new { uuid, title = chat[0]["title"], messages = msgs });
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
        app.MapGet("/api/knowledge", () => Results.Json(db.Query(
            "SELECT file_id, name, status, error, chunk_count, added_at FROM files ORDER BY file_id DESC")));

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

        app.MapDelete("/api/knowledge/{id}", (long id) =>
        {
            db.Exec("DELETE FROM files WHERE file_id=$i", ("$i", id));
            index.RemoveFile(id);
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
            cfg = AppConfig.Load(args);
            var dataDir = Path.IsPathRooted(cfg.DataDir) ? cfg.DataDir : Path.Combine(AppContext.BaseDirectory, cfg.DataDir);
            Directory.CreateDirectory(dataDir);
            log = new Logger(Path.Combine(dataDir, "logs"));
            await RunAsync(cfg, log);
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

    static async Task RunAsync(AppConfig cfg, Logger log)
    {
        // tierは設定DBで上書き
        log.Info($"ShineosQA.Backend starting: tier={cfg.Tier} ram={AppConfig.TotalRamGb()}GB port={cfg.Port}");
        var db = new Db(Path.Combine(Path.IsPathRooted(cfg.DataDir) ? cfg.DataDir : Path.Combine(AppContext.BaseDirectory, cfg.DataDir), "knowledge.db"));
        var savedTier = db.GetSetting("tier", "");
        if (savedTier is "auto" or "standard" or "quick") cfg.Tier = savedTier;
        else db.SetSetting("tier", cfg.Tier); // 初回はconfig.jsonの階級を永続化

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
