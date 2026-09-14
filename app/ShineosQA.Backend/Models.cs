using System.Security.Cryptography;

namespace ShineosQA.Backend;

/// <summary>モデル配布マネージャ（Store対応: 実行ファイルは同梱、GGUFは初回起動時にアプリ内DL）。
/// カタログ値は実測検証済み: docs/phase0-report.md §1。qwen2.5系はQwen Research Licenseのため配布対象外</summary>
public sealed class ModelManager
{
    public sealed record CatalogEntry(string Id, string Name, string File, string Kind, long SizeBytes,
        string UrlPrimary, string UrlMirror, string Sha256, string License, bool Required, int MinRamGb);

    private static readonly CatalogEntry[] Catalog =
    {
        new("embed-bge-m3", "埋め込みモデル bge-m3（必須）", "bge-m3-Q8_0.gguf", "embed", 634553760,
            "https://huggingface.co/gpustack/bge-m3-GGUF/resolve/main/bge-m3-Q8_0.gguf",
            "https://hf-mirror.com/gpustack/bge-m3-GGUF/resolve/main/bge-m3-Q8_0.gguf",
            "950f4a8e5e19477a6d3c26d2f162233c20002c601f75e4b002e3239997821167", "MIT", true, 0),
        new("rerank-bge-v2-m3", "リランカ bge-reranker-v2-m3（精度向上・任意）", "bge-reranker-v2-m3-Q8_0.gguf", "rerank", 635676416,
            "https://huggingface.co/gpustack/bge-reranker-v2-m3-GGUF/resolve/main/bge-reranker-v2-m3-Q8_0.gguf",
            "https://hf-mirror.com/gpustack/bge-reranker-v2-m3-GGUF/resolve/main/bge-reranker-v2-m3-Q8_0.gguf",
            "a43c7c9b11a4c1517e5bf95151960e1621d1b72f7a493364b01e386cf1aaa1d3", "Apache-2.0", false, 0),
        new("chat-quick", "クイック Qwen3-1.7B（8GB機向・約1.0GB）", "Qwen3-1.7B-IQ4_XS.gguf", "chat_quick", 1010383424,
            "https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-IQ4_XS.gguf",
            "https://hf-mirror.com/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-IQ4_XS.gguf",
            "a02e41d3208e97a7cb224297e8d3abb22e5bb8d664362c6be4f48948a3797eec", "Apache-2.0", false, 0),
        new("chat-standard", "標準 Qwen3-4B-2507（16GB機向・約2.2GB）", "Qwen3-4B-Instruct-2507-IQ4_XS.gguf", "chat_standard", 2270751840,
            "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-IQ4_XS.gguf",
            "https://hf-mirror.com/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-IQ4_XS.gguf",
            "cfd15a69e4801abfa16f0263b77ffbf97cbb8af67782a70cc95cf627b3dfa27d", "Apache-2.0", false, 8),
        new("chat-quality", "高品質 Qwen3-30B-A3B（実験的・16GB機向・約12.9GB）", "Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf", "chat_quality", 13833048480,
            "https://huggingface.co/unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF/resolve/main/Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf",
            "https://hf-mirror.com/unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF/resolve/main/Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf",
            "36c21449a36760933709aa8fe6ffafe946961a6dc9174b6ad10ba6000e649121", "Apache-2.0", false, 16),
    };

    public sealed record ModelStatus(string Id, string Name, string File, string Kind, long SizeBytes, string License,
        bool Installed, bool Required, int MinRamGb);

    public sealed class DownloadProgress
    {
        public string State = "idle"; // idle | downloading | verifying | done | error
        public string? CurrentId;
        public long Bytes, Total;
        public string? Error;
        public DateTime StartedAt = DateTime.MinValue;
    }

    private readonly AppConfig _cfg;
    private readonly ILogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly SemaphoreSlim _lock = new(1, 1);
    public volatile DownloadProgress Progress = new();

    public ModelManager(AppConfig cfg, ILogger log) { _cfg = cfg; _log = log; }

    public List<ModelStatus> Status() => Catalog.Select(e => new ModelStatus(
        e.Id, e.Name, e.File, e.Kind, e.SizeBytes, e.License,
        File.Exists(Path.Combine(_cfg.ModelsDir, e.File)), e.Required, e.MinRamGb)).ToList();

    /// <summary>チャットモデルが1つも無い=初回起動ウィザードが必要</summary>
    public bool NeedsWizard => !Catalog.Any(e => e.Kind.StartsWith("chat_") && File.Exists(Path.Combine(_cfg.ModelsDir, e.File)));

    public async Task InstallAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entry = Catalog.FirstOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException($"unknown model id: {id}");
            var dest = Path.Combine(_cfg.ModelsDir, entry.File);
            if (File.Exists(dest)) return;
            Directory.CreateDirectory(_cfg.ModelsDir);
            Exception? lastErr = null;
            foreach (var url in new[] { entry.UrlPrimary, entry.UrlMirror })
            {
                try
                {
                    var uri = new Uri(url);
                    NetGuard.EnsurePublicHttp(uri); // SSRFガード: http/Https・公開アドレスのみ
                    Progress = new DownloadProgress { State = "downloading", CurrentId = id, StartedAt = DateTime.UtcNow };
                    var tmp = dest + ".part";
                    using (var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        resp.EnsureSuccessStatusCode();
                        Progress.Total = resp.Content.Headers.ContentLength ?? entry.SizeBytes;
                        await using var src = await resp.Content.ReadAsStreamAsync(ct);
                        await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                        var buf = new byte[1 << 20];
                        int n;
                        while ((n = await src.ReadAsync(buf, ct)) > 0)
                        {
                            await fs.WriteAsync(buf.AsMemory(0, n), ct);
                            Progress.Bytes += n;
                        }
                    }
                    Progress = new DownloadProgress { State = "verifying", CurrentId = id, Bytes = Progress.Bytes, Total = Progress.Total };
                    string hash;
                    await using (var fs = File.OpenRead(tmp))
                        hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                    if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"sha256 mismatch: expected {entry.Sha256[..12]}… got {hash[..12]}… (SHINE_E_MODEL_VERIFY_FAILED)");
                    File.Move(tmp, dest);
                    Progress = new DownloadProgress { State = "done", CurrentId = id, Bytes = Progress.Bytes, Total = Progress.Total };
                    _log.Info($"model installed: {entry.File} ({Progress.Bytes / 1024 / 1024}MB, sha verified)");
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastErr = ex;
                    _log.Warn($"model download failed from {url}: {ex.Message}");
                    Progress.Bytes = 0;
                }
            }
            Progress = new DownloadProgress { State = "error", CurrentId = id, Error = lastErr?.Message };
            throw lastErr ?? new InvalidOperationException("download failed (SHINE_E_MODEL_DOWNLOAD_FAILED)");
        }
        finally { _lock.Release(); }
    }

    public void Delete(string id)
    {
        var entry = Catalog.FirstOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException($"unknown model id: {id}");
        if (entry.Required) throw new InvalidOperationException("required model cannot be deleted");
        var p = Path.Combine(_cfg.ModelsDir, entry.File);
        if (File.Exists(p)) File.Delete(p);
        _log.Info($"model deleted: {entry.File}");
    }
}
