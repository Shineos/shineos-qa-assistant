using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ShineosQA.Backend;

/// <summary>モデルGGUFの起動前整合性検証（SHA256・キャッシュ永続付き）。
/// 設計書 error-codes-v2.md §4.2 の SHINE_E_MODEL_HASH 実装。
/// 検証結果は {data}/model-verify.txt に「ファイル|長さ|mtime|ok」で永続化し、
/// ファイルが変わらない限り再ハッシュしない（大容量モデルの起動遅延防止）。
/// ファイル破損（バイト反転等）は mtime が変わるため必ず再検証にかかる</summary>
public static class ModelIntegrity
{
    public static void Verify(string modelPath, string dataDir, string? expectedSha, ILogger log)
    {
        if (string.IsNullOrEmpty(expectedSha)) return; // カタログ外のモデル（ユーザー任意指定）は検証できない
        var fi = new FileInfo(modelPath);
        var cachePath = Path.Combine(dataDir, "model-verify.txt");
        var key = Path.GetFileName(modelPath);
        try
        {
            foreach (var line in File.ReadAllLines(cachePath))
            {
                var parts = line.Split('|');
                if (parts.Length == 4 && parts[0] == key &&
                    long.TryParse(parts[1], out var len) && long.TryParse(parts[2], out var ticks) &&
                    len == fi.Length && ticks == fi.LastWriteTimeUtc.Ticks && parts[3] == "ok")
                    return; // 同一バージョン検証済み
            }
        }
        catch { /* キャッシュ不在・破損は初回検証として扱う */ }

        string hash;
        using (var fs = File.OpenRead(modelPath))
            hash = Convert.ToHexString(SHA256.HashData(fs));
        if (!hash.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            log.Error($"model integrity check FAILED: {key} expected {expectedSha[..12]}… got {hash[..12]}… (SHINE_E_MODEL_HASH)");
            // 破損判定もキャッシュする（UIが再ダウンロード案内を出すための材料。ハッシュ不要で参照できる）
            WriteCache(dataDir, cachePath, key, fi, "bad");
            throw new InvalidDataException($"model file corrupted: {key} (再ダウンロードが必要です) (SHINE_E_MODEL_HASH)");
        }
        log.Info($"model integrity ok: {key} ({fi.Length / 1024 / 1024}MB, sha {hash[..12]}…)");
        WriteCache(dataDir, cachePath, key, fi, "ok");
    }

    /// <summary>検証済みキャッシュの判定。true=検証済み正常 / false=破損判定済み / null=未検証（キャッシュなし）。
    /// ハッシュ計算をしないため /api/models のような頻出呼び出しで使える</summary>
    public static bool? CachedOk(string modelPath, string dataDir)
    {
        var fi = new FileInfo(modelPath);
        var cachePath = Path.Combine(dataDir, "model-verify.txt");
        var key = Path.GetFileName(modelPath);
        try
        {
            foreach (var line in File.ReadAllLines(cachePath))
            {
                var parts = line.Split('|');
                if (parts.Length == 4 && parts[0] == key &&
                    long.TryParse(parts[1], out var len) && long.TryParse(parts[2], out var ticks) &&
                    len == fi.Length && ticks == fi.LastWriteTimeUtc.Ticks)
                    return parts[3] == "ok";
            }
        }
        catch { }
        return null;
    }

    private static void WriteCache(string dataDir, string cachePath, string key, FileInfo fi, string verdict)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            // 他モデルの検証済みエントリは残し、同ファイルの旧エントリ（別バージョン）のみ置換する
            var lines = new List<string>();
            try
            {
                foreach (var line in File.ReadAllLines(cachePath))
                    if (line.Split('|') is { Length: 4 } p && p[0] != key) lines.Add(line);
            }
            catch { }
            lines.Add($"{key}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{verdict}");
            File.WriteAllLines(cachePath, lines);
        }
        catch { /* キャッシュ書込失敗は致命的ではない */ }
    }
}

/// <summary>llama-server 子プロセス管理（スーパーバイザ）。設計: architecture-v2.md §5.1/§7
/// 耐障害: Job Object(kill-on-close)で孤児化防止・ログStreamの確実な破棄・OOM時の階級フォールバック</summary>
public sealed class Supervisor
{
    private readonly AppConfig _cfg;
    private readonly LlmGateway _gw;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly object _lock = new();
    private EngineProc? _llm, _emb, _rank, _vision;
    private DateTime _llmLastUsed = DateTime.UtcNow;
    private DateTime _visionLastUsed = DateTime.UtcNow;
    private Task? _idleTask;
    private readonly ILogger _log;
    private int _llmStartFails;      // 連続起動失敗（サーキットブレーカー）
    private string _llmFailReason = "";

    public Supervisor(AppConfig cfg, LlmGateway gw, ILogger log) { _cfg = cfg; _gw = gw; _log = log; }

    public sealed class EngineProc // EnsureVision(公開)の戻り値
    {
        public required string Name;
        public required string File; // 読み込んだモデルファイル（tier整合チェック用）
        public required Process Proc;
        public required int Port;
        public required FileStream LogStream;
    }

    public void StartEmbedAndRank()
    {
        lock (_lock)
        {
            // 自己修復: 未起動・クラッシュ・tier変更後のStopAllのいずれでも再起動する
            if (_emb is null || _emb.Proc.HasExited)
            {
                if (_emb != null) DisposeEngine(_emb);
                _emb = Start("embed", _cfg.EmbedModel, _cfg.EnginePortEmb, extra: new[] { "--embedding", "--pooling", "cls" });
            }
            // リランカは任意モデル（README: 精度向上・任意）: 未DLでもQ&Aを止めない。
            // 無いのに起動しようとするとFNFでチャット全体が死ぬため（軽量インストーラ実機検証で発覚）
            var rankModelPath = Path.Combine(_cfg.ModelsDir, _cfg.RankModel);
            if (!File.Exists(rankModelPath))
            {
                if (_rank != null) { DisposeEngine(_rank); _rank = null; }
                _log.Warn("rank model not installed — running without reranker (hybrid search only)");
            }
            else if (_rank is null || _rank.Proc.HasExited)
            {
                if (_rank != null) DisposeEngine(_rank);
                _rank = Start("rank", _cfg.RankModel, _cfg.EnginePortRank, extra: new[] { "--rerank", "--pooling", "rank" });
            }
        }
    }

    /// <summary>LLMエンジン確保（idle unload後の再起動・OOM時の階級フォールバック込み）。
    /// 起動中エンジンのモデルが現在の階級と不一致（起動プリロードとtier切替の競合）なら入れ替える。
    /// 連続起動失敗時はサーキットブレーカーが即時エラーを返す（破損モデル等の決定的失敗で
    /// チャットのたびに2分×回の待ちが積み重なるのを防ぐ。実測: 破損GGUFで240秒無応答）</summary>
    public void EnsureLlm()
    {
        lock (_lock)
        {
            if (_llmStartFails >= 3)
                throw new InvalidOperationException($"{_llmFailReason} — 連続{_llmStartFails}回失敗のため再試行を停止しました (SHINE_E_ENGINE_DOWN)");
            if (_llm != null)
            {
                if (!_llm.Proc.HasExited && _llm.File == _cfg.ChatModelFile)
                { _llmLastUsed = DateTime.UtcNow; StartIdleWatcher(); return; }
                DisposeEngine(_llm);
                _llm = null;
            }
            try
            {
                _llm = StartLlmLocked();
                _llmStartFails = 0;
            }
            catch (Exception ex)
            {
                _llmStartFails++;
                _llmFailReason = ex.Message;
                // モデル破損（SHA不一致）は決定的失敗: 下位階級へフォールバックしても同じ破損を引く
                // 可能性が高く、再試行の意味がないため即座に利用者へエラーを届ける
                if (ex is InvalidDataException) throw;
                // RAM不足（OOM）想定の失敗のみ段階的に下位モデルへフォールバック（quality→standard→quick）。
                // フォールバック先が現在同一ファイル（quick階級で失敗等）なら再試行しない
                var fb = _cfg.EffectiveTier == "quality" && File.Exists(Path.Combine(_cfg.ModelsDir, _cfg.StandardModel)) ? "standard"
                    : File.Exists(Path.Combine(_cfg.ModelsDir, _cfg.QuickModel)) ? "quick"
                    : null;
                var fbFile = fb == "quick" ? _cfg.QuickModel : _cfg.StandardModel;
                if (fb is null || fbFile == _cfg.ChatModelFile) throw;
                _log.Warn($"LLM start failed ({ex.Message}) — OOM fallback: switching to {fb} tier ({_cfg.ChatModelFile}→{fbFile})");
                _cfg.Tier = fb;
                _llm = StartLlmLocked();
                _llmStartFails = 0;
            }
            _llmLastUsed = DateTime.UtcNow;
            StartIdleWatcher();
            WarmupAsync(); // 初回推論ウォームアップ（計算グラフ構築＋システムプロンプトの接頭キャッシュ）
        }
    }

    /// <summary>視覚言語エンジン（図面キャプチャのAI読取）を確保する。モデル未導入ならnull
    /// （呼び出し側はWinRT OCRへフォールバック）。--mmproj付きで起動し画像入力を有効化する。
    /// 初回起動はモデル1.7GBのロードで数十秒かかるため、拡張パックON時にウォームアップする</summary>
    public EngineProc? EnsureVision()
    {
        lock (_lock)
        {
            if (!VisionInstalled) return null;
            if (_vision is { } v && !v.Proc.HasExited)
            { _visionLastUsed = DateTime.UtcNow; StartIdleWatcher(); return v; }
            if (_vision != null) DisposeEngine(_vision);
            var mmproj = Path.Combine(_cfg.ModelsDir, _cfg.VisionMmprojFile);
            _vision = Start("vision", _cfg.VisionModelFile, _cfg.EnginePortVision,
                extra: new[] { "--mmproj", mmproj, "-c", "8192" }); // 画像トークン分の余裕を持たせたctx
            _visionLastUsed = DateTime.UtcNow;
            StartIdleWatcher();
            return _vision;
        }
    }

    /// <summary>視覚モデル2ファイル（本体+mmproj）がモデルディレクトリに揃っているか</summary>
    public bool VisionInstalled =>
        File.Exists(Path.Combine(_cfg.ModelsDir, _cfg.VisionModelFile)) &&
        File.Exists(Path.Combine(_cfg.ModelsDir, _cfg.VisionMmprojFile));

    /// <summary>LLM起動失敗の連続カウントをリセット（モデル再ダウンロード完了・階級切替時に呼ぶ）</summary>
    public void ResetLlmFailure()
    {
        lock (_lock) { _llmStartFails = 0; _llmFailReason = ""; }
    }

    /// <summary>LLM起動直後にダミー1トークン生成を流し、初回質問のTTFBを短縮する（非同期・失敗は記録のみ）</summary>
    private void WarmupAsync()
    {
        var model = _cfg.ChatModelFile;
        _ = Task.Run(async () =>
        {
            try
            {
                await _gw.ChatStreamAsync(_cfg.EnginePortLlm,
                    new List<(string, string)> { ("system", Rag.SystemPrompt + Rag.CurrentDateLine()), ("user", "こんにちは") },
                    0.0, 1, _ => Task.CompletedTask, CancellationToken.None);
                _log.Info($"llm warmup done: model={model}");
            }
            catch (Exception ex) { _log.Warn($"llm warmup failed: {ex.Message}"); }
        });
    }

    /// <summary>チャットモデル階級の切替（送信フォームからのモデル指定）。
    /// 現行と異なる階級ならLLMエンジンのみ入れ替える（embed/rankは不変）。次のEnsureLlmで新モデルが読み込まれる</summary>
    public bool SwitchLlmTier(string tier)
    {
        lock (_lock)
        {
            if (tier is not ("quick" or "standard" or "quality") || _cfg.EffectiveTier == tier) return false;
            if (_llm != null) { DisposeEngine(_llm); _llm = null; }
            _cfg.Tier = tier;
            _llmStartFails = 0; // 階級切替で別モデルになるため失敗カウントはリセット
            return true;
        }
    }

    private EngineProc StartLlmLocked()
    {
        var threads = _cfg.Threads > 0 ? _cfg.Threads : Environment.ProcessorCount / 2;
        var fa = _cfg.EngineVariant == "vulkan" ? "off" : "on";
        var extra = _cfg.EngineVariant == "vulkan"
            ? new[] { "-ngl", "99", "-fa", fa }
            : new[] { "-fa", fa, "-ub", "2048" }; // pp大きなバッチ=スループット優先（初トークン前に一括処理のため純増）
        // 大型MoEモデル（>5GB）はREPACKでRAM内コピーが作られてOOMするため --no-repack 必須（ds4方式）
        var modelPath = Path.Combine(_cfg.ModelsDir, _cfg.ChatModelFile);
        if (new FileInfo(modelPath).Length > 5L * 1024 * 1024 * 1024)
            extra = extra.Concat(new[] { "--no-repack" }).ToArray();
        return Start("llm", _cfg.ChatModelFile, _cfg.EnginePortLlm, threads, extra);
    }

    private void StartIdleWatcher()
    {
        if (_idleTask != null) return;
        _idleTask = Task.Run(IdleLoopAsync);
    }

    /// <summary>idleタイマー: 一定時間無操作でLLM子プロセスを停止（Ollama keep_alive相当）。
    /// さらに長く使わなければ rank（Q&A中のみ使用）→ embed（取り込み・キャッシュ照合で使用）も
    /// 順に解放し、アプリを開いたままの常駐メモリを最小化する。次の質問・取り込みでは
    /// ChatFlow/Ingest の自己修復呼び出し（StartEmbedAndRank）がオンデマンドで再起動する</summary>
    private async Task IdleLoopAsync()
    {
        var llmIdle = TimeSpan.FromMinutes(Math.Max(1, _cfg.IdleUnloadMinutes));
        DateTime? rankDueAt = null, embDueAt = null;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            EngineProc? toStopLlm = null, toStopRank = null, toStopEmb = null, toStopVision = null;
            lock (_lock)
            {
                var idle = DateTime.UtcNow - _llmLastUsed;
                var visionIdle = DateTime.UtcNow - _visionLastUsed;
                if (idle <= llmIdle)
                {
                    // 利用が再開されたら解放予定を取り消す
                    rankDueAt = null; embDueAt = null;
                }
                else if (_llm != null && !_llm.Proc.HasExited)
                {
                    toStopLlm = _llm; _llm = null;
                    rankDueAt ??= DateTime.UtcNow + TimeSpan.FromMinutes(5);
                    embDueAt ??= DateTime.UtcNow + llmIdle;
                }
                if (visionIdle > llmIdle && _vision != null && !_vision.Proc.HasExited)
                { toStopVision = _vision; _vision = null; }
                if (rankDueAt is { } r && _rank != null && !_rank.Proc.HasExited && DateTime.UtcNow > r)
                { toStopRank = _rank; _rank = null; rankDueAt = null; }
                if (embDueAt is { } e && _emb != null && !_emb.Proc.HasExited && DateTime.UtcNow > e)
                { toStopEmb = _emb; _emb = null; embDueAt = null; }
                if (toStopLlm != null && _rank == null && _emb == null) _idleTask = null; // 全停止で監視終了
            }
            if (toStopLlm != null) { _log.Info($"idle unload: stopping llm engine (port {toStopLlm.Port})"); lock (_lock) DisposeEngine(toStopLlm); }
            if (toStopRank != null) { _log.Info("idle unload: stopping rank engine"); lock (_lock) DisposeEngine(toStopRank); }
            if (toStopEmb != null) { _log.Info("idle unload: stopping embed engine"); lock (_lock) DisposeEngine(toStopEmb); }
            if (toStopVision != null) { _log.Info("idle unload: stopping vision engine"); lock (_lock) DisposeEngine(toStopVision); }
            lock (_lock)
            {
                // 全エンジン停止で監視ループも終了（次のEnsureLlmが新しい監視を起動する）
                if (_llm == null && _rank == null && _emb == null && _vision == null)
                {
                    _idleTask = null;
                    return;
                }
            }
        }
    }

    /// <summary>他アプリ優先設定の変更を稼働中LLMへ即時反映するため、稼働中なら停止する
    /// （次の質問のEnsureLlmで新しい優先度で再起動される。未稼働なら何もしない）</summary>
    public void ApplyLlmPriority()
    {
        lock (_lock)
        {
            if (_llm != null) { DisposeEngine(_llm); _llm = null; }
        }
    }

    private EngineProc Start(string name, string modelFile, int port, int? threads = null, string[]? extra = null)
    {
        var exe = Path.Combine(_cfg.EngineDir, "llama-server.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"engine not found: {exe} (SHINE_E_ENGINE_DOWN)");
        var model = Path.Combine(_cfg.ModelsDir, modelFile);
        if (!File.Exists(model)) throw new FileNotFoundException($"model not found: {model} (SHINE_E_MODEL_NOT_FOUND)");
        // 起動前整合性検証（SHA256・永続キャッシュ付き）: 破損GGUFを起動するとllama-serverは
        // 瞬時に異常終了し、ヘルス待ちの無駄とリトライループを生む。検証は数秒で終わり、
        // 決定的失敗を即座にSHINE_E_MODEL_HASHとして利用者に届けられる
        ModelIntegrity.Verify(model, _cfg.DataDir, ModelManager.ShaForFile(modelFile), _log);
        // 起動引数は構成ファイル由来のみでシェルは経由しない（ArgumentList）。パスの正当性も明示検証する
        foreach (var (launchPath, what) in new[] { (exe, "engine"), (model, "model") })
            if (launchPath.Contains("..") || launchPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new InvalidOperationException($"invalid {what} path for subprocess launch");
        Directory.CreateDirectory(Path.Combine(_cfg.DataDir, "logs"));
        var logPath = Path.Combine(_cfg.DataDir, "logs", $"engine-{name}.log");
        // ReadWrite共有: 例外でリークした旧ハンドルがあっても再起動がファイルロックで永久失敗しない
        var logFs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var args = new List<string> { "-m", model, "--host", "127.0.0.1", "--port", port.ToString(), "-c", _cfg.CtxSize.ToString(), "-np", "1" };
        if (threads is int t) args.AddRange(new[] { "-t", t.ToString() });
        if (extra != null) args.AddRange(extra);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        JobObject.Assign(p); // 親（バックエンド）死亡時に子も確実に終了（孤児化防止）
            // 他アプリ優先モード（既定ON）: LLM生成もBelowNormalで実行し、利用者が同時に使う
            // 他アプリ（ブラウザ・Office等）の操作を優先させる。OFFなら通常優先で最速（pp+24%程度）
            p.PriorityClass = name == "llm"
                ? (_cfg.BgFriendly ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal)
                : ProcessPriorityClass.BelowNormal;
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { var b = System.Text.Encoding.UTF8.GetBytes(e.Data + "\n"); logFs.Write(b, 0, b.Length); logFs.Flush(); } };
        p.BeginErrorReadLine();
        var ep = new EngineProc { Name = name, File = modelFile, Proc = p, Port = port, LogStream = logFs };
        if (!WaitHealthy(port, 120, p))
        {
            DisposeEngine(ep);
            throw new InvalidOperationException($"llama-server '{name}' did not become healthy on port {port} (SHINE_E_ENGINE_DOWN)");
        }
        _log.Info($"engine '{name}' up: port={port} model={Path.GetFileName(model)} pid={p.Id}");
        return ep;
    }

    private void DisposeEngine(EngineProc ep)
    {
        try
        {
            if (!ep.Proc.HasExited)
            {
                ep.Proc.Kill(entireProcessTree: true);
                ep.Proc.WaitForExit(5000); // 後続Startが同portのbind失敗を踏まないよう終了を待つ
            }
        }
        catch { }
        try { ep.Proc.Dispose(); } catch { }
        try { ep.LogStream.Dispose(); } catch { } // ログファイルロック解放（再起動失敗の原因だった）
    }

    /// <summary>エンジンのヘルス待ち。起動直後にプロセスが異常終了した場合（破損モデル等）は
    /// 残り時間に関係なく即座にfalseを返す（死んだプロセスのために2分間待つ無駄を排除）</summary>
    private bool WaitHealthy(int port, int seconds, Process? watched = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (watched is { } w && w.HasExited)
            {
                _log.Warn($"engine on port {port} exited during startup (code {w.ExitCode}) — skipping remaining health wait");
                return false;
            }
            try
            {
                using var r = _http.GetAsync($"http://127.0.0.1:{port}/health", HttpCompletionOption.ResponseHeadersRead).Result;
                if (r.IsSuccessStatusCode) return true;
            }
            catch { }
            Thread.Sleep(500);
        }
        return false;
    }

    public bool IsLlmAlive => _llm is { } l && !l.Proc.HasExited;

    /// <summary>リランクエンジンの稼働状態。未起動（モデル未DL・アイドル解放後）でも
    /// Q&Aはハイブリッド順で続行できるため、ChatFlowはこれを見てリランクをスキップする</summary>
    public bool IsRankAlive => _rank is { } r && !r.Proc.HasExited;

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var e in new[] { _llm, _emb, _rank, _vision }) if (e != null) DisposeEngine(e);
            _llm = _emb = _rank = _vision = null;
        }
    }

    /// <summary>指定モデルファイルを読み込んでいるエンジンだけを停止する（モデル削除前のファイルロック解放）。
    /// 止めたエンジンは次回利用時に自動で再起動される</summary>
    public void StopEnginesUsing(IEnumerable<string> modelFiles)
    {
        var set = modelFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (_llm is { } l && set.Contains(Path.GetFileName(l.File))) { DisposeEngine(l); _llm = null; }
            if (_emb is { } e && set.Contains(Path.GetFileName(e.File))) { DisposeEngine(e); _emb = null; }
            if (_rank is { } r && set.Contains(Path.GetFileName(r.File))) { DisposeEngine(r); _rank = null; }
            if (_vision is { } v && set.Contains(Path.GetFileName(v.File))) { DisposeEngine(v); _vision = null; }
        }
    }

    public object Status() => new
    {
        tier = _cfg.EffectiveTier,
        chat_model = _cfg.ChatModelFile,
        llm = _llm == null ? "unloaded" : (_llm.Proc.HasExited ? "crashed" : $"running(pid={_llm.Proc.Id})"),
        embed = _emb is { } e1 && !e1.Proc.HasExited ? $"running(pid={e1.Proc.Id})" : "stopped",
        rank = _rank is { } e2 && !e2.Proc.HasExited ? $"running(pid={e2.Proc.Id})" : "stopped",
        vision = _vision is { } v && !v.Proc.HasExited ? $"running(pid={v.Proc.Id})" : (VisionInstalled ? "stopped" : "未導入"),
    };
}

/// <summary>Windows Job Object（KILL_ON_JOB_CLOSE）: バックエンドがどんな理由で死んでも子プロセスを道連れにする</summary>
internal static class JobObject
{
    private static readonly IntPtr _job = Create();

    /// <summary>診断用: 最後のAssign結果（0=成功、それ以外はWin32エラーコード）</summary>
    public static int LastAssignError { get; private set; }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0x2000, // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            },
        };
        if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new InvalidOperationException($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
        return job;
    }

    public static bool Assign(Process p)
    {
        LastAssignError = 0;
        try
        {
            if (AssignProcessToJobObject(_job, p.Handle)) return true;
            LastAssignError = Marshal.GetLastWin32Error();
        }
        catch (Exception ex) { LastAssignError = ex.HResult & 0xFFFF; }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity; // ULONG_PTR（x64では8バイト。uintにすると構造体サイズが不一致になりSetInformationJobObjectが失敗する）
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbInfo);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
