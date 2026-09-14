using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShineosQA.Backend;

/// <summary>llama-server 子プロセス管理（スーパーバイザ）。設計: architecture-v2.md §5.1/§7
/// 耐障害: Job Object(kill-on-close)で孤児化防止・ログStreamの確実な破棄・OOM時の階級フォールバック</summary>
public sealed class Supervisor
{
    private readonly AppConfig _cfg;
    private readonly LlmGateway _gw;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly object _lock = new();
    private EngineProc? _llm, _emb, _rank;
    private DateTime _llmLastUsed = DateTime.UtcNow;
    private Task? _idleTask;
    private readonly ILogger _log;

    public Supervisor(AppConfig cfg, LlmGateway gw, ILogger log) { _cfg = cfg; _gw = gw; _log = log; }

    private sealed class EngineProc
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
            if (_rank is null || _rank.Proc.HasExited)
            {
                if (_rank != null) DisposeEngine(_rank);
                _rank = Start("rank", _cfg.RankModel, _cfg.EnginePortRank, extra: new[] { "--rerank", "--pooling", "rank" });
            }
        }
    }

    /// <summary>LLMエンジン確保（idle unload後の再起動・OOM時の階級フォールバック込み）。
    /// 起動中エンジンのモデルが現在の階級と不一致（起動プリロードとtier切替の競合）なら入れ替える</summary>
    public void EnsureLlm()
    {
        lock (_lock)
        {
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
            }
            catch (Exception ex)
            {
                // RAM不足等で起動失敗時は段階的に下位モデルへフォールバック（quality→standard→quick）
                var fb = _cfg.EffectiveTier == "quality" && File.Exists(Path.Combine(_cfg.ModelsDir, _cfg.StandardModel)) ? "standard"
                    : File.Exists(Path.Combine(_cfg.ModelsDir, _cfg.QuickModel)) ? "quick"
                    : null;
                if (fb is null) throw;
                _log.Warn($"LLM start failed ({ex.Message}) — OOM fallback: switching to {fb} tier ({_cfg.ChatModelFile}→{(fb == "quick" ? _cfg.QuickModel : _cfg.StandardModel)})");
                _cfg.Tier = fb;
                _llm = StartLlmLocked();
            }
            _llmLastUsed = DateTime.UtcNow;
            StartIdleWatcher();
            WarmupAsync(); // 初回推論ウォームアップ（計算グラフ構築＋システムプロンプトの接頭キャッシュ）
        }
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
                    new List<(string, string)> { ("system", Rag.SystemPrompt), ("user", "こんにちは") },
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

    /// <summary>idleタイマー: 一定時間無操作でLLM子プロセスを停止（Ollama keep_alive相当）</summary>
    private async Task IdleLoopAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            EngineProc? toStop = null;
            lock (_lock)
            {
                if (_llm != null && !_llm.Proc.HasExited &&
                    DateTime.UtcNow - _llmLastUsed > TimeSpan.FromMinutes(Math.Max(1, _cfg.IdleUnloadMinutes)))
                {
                    toStop = _llm; _llm = null; _idleTask = null;
                }
            }
            if (toStop != null)
            {
                _log.Info($"idle unload: stopping llm engine (port {toStop.Port})");
                lock (_lock) DisposeEngine(toStop);
                return;
            }
        }
    }

    private EngineProc Start(string name, string modelFile, int port, int? threads = null, string[]? extra = null)
    {
        var exe = Path.Combine(_cfg.EngineDir, "llama-server.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"engine not found: {exe} (SHINE_E_ENGINE_DOWN)");
        var model = Path.Combine(_cfg.ModelsDir, modelFile);
        if (!File.Exists(model)) throw new FileNotFoundException($"model not found: {model} (SHINE_E_MODEL_NOT_FOUND)");
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
            // LLMは通常優先（BelowNormalだとpp実測-24%）。embed/rankは常駐のため低優先のまま
            p.PriorityClass = name == "llm" ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { var b = System.Text.Encoding.UTF8.GetBytes(e.Data + "\n"); logFs.Write(b, 0, b.Length); logFs.Flush(); } };
        p.BeginErrorReadLine();
        var ep = new EngineProc { Name = name, File = modelFile, Proc = p, Port = port, LogStream = logFs };
        if (!WaitHealthy(port, 120))
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

    private bool WaitHealthy(int port, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
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

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var e in new[] { _llm, _emb, _rank }) if (e != null) DisposeEngine(e);
            _llm = _emb = _rank = null;
        }
    }

    public object Status() => new
    {
        tier = _cfg.EffectiveTier,
        chat_model = _cfg.ChatModelFile,
        llm = _llm == null ? "unloaded" : (_llm.Proc.HasExited ? "crashed" : $"running(pid={_llm.Proc.Id})"),
        embed = _emb is { } e1 && !e1.Proc.HasExited ? $"running(pid={e1.Proc.Id})" : "stopped",
        rank = _rank is { } e2 && !e2.Proc.HasExited ? $"running(pid={e2.Proc.Id})" : "stopped",
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
