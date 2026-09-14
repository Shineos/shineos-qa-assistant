using System.Text.Json;

namespace ShineosQA.Backend;

/// <summary>アプリ設定（config.json / 既定値）。設計: docs/architecture-v2.md §5</summary>
public sealed class AppConfig
{
    public int Port { get; set; } = 8300;
    public string DataDir { get; set; } = "data";
    public string EngineDir { get; set; } = "engine";
    public string EngineVariant { get; set; } = "cpu"; // cpu | vulkan
    public string ModelsDir { get; set; } = "models";
    public string StandardModel { get; set; } = "Qwen3-4B-Instruct-2507-IQ4_XS.gguf";
    public string QuickModel { get; set; } = "Qwen3-1.7B-IQ4_XS.gguf";
    public string QualityModel { get; set; } = "Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf"; // 任意の高品質階級（16GB以上）
    public string EmbedModel { get; set; } = "bge-m3-Q8_0.gguf";
    public string RankModel { get; set; } = "bge-reranker-v2-m3-Q8_0.gguf";
    public string Tier { get; set; } = "auto"; // auto | standard | quick
    public bool WebSearch { get; set; } = false; // 既定OFF（社外送信なし）
    public int EnginePortLlm { get; set; } = 8301;
    public int EnginePortEmb { get; set; } = 8302;
    public int EnginePortRank { get; set; } = 8303;
    public int CtxSize { get; set; } = 2048;
    public int Threads { get; set; } = 0; // 0=物理コア数
    public int IdleUnloadMinutes { get; set; } = 60;

    public static AppConfig Load(string[] args)
    {
        var cfg = new AppConfig();
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "config.json");
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config")
            {
                var raw = args[i + 1];
                // パス穿越防止: 相対指定はアプリディレクトリ基準に解決し、生の引数に .. を含む場合は拒否する
                if (raw.Contains("..") || raw.Contains('\0'))
                    throw new ArgumentException("config path must not contain '..'");
                path = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(baseDir, raw));
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("config path must be a .json file");
            }
        }
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "port": cfg.Port = p.Value.GetInt32(); break;
                    case "data_dir": cfg.DataDir = p.Value.GetString()!; break;
                    case "engine_dir": cfg.EngineDir = p.Value.GetString()!; break;
                    case "engine_variant": cfg.EngineVariant = p.Value.GetString()!; break;
                    case "models_dir": cfg.ModelsDir = p.Value.GetString()!; break;
                    case "standard_model": cfg.StandardModel = p.Value.GetString()!; break;
                    case "quick_model": cfg.QuickModel = p.Value.GetString()!; break;
                    case "quality_model": cfg.QualityModel = p.Value.GetString()!; break;
                    case "tier": cfg.Tier = p.Value.GetString()!; break;
                    case "web_search": cfg.WebSearch = p.Value.GetBoolean(); break;
                    case "ctx_size": cfg.CtxSize = p.Value.GetInt32(); break;
                }
            }
        }
        return cfg;
    }

    /// <summary>RAM階級でモデル階級を確定（実測根拠: docs/latency-verification.md §4）</summary>
    public string EffectiveTier => Tier == "auto"
        ? (TotalRamGb() >= 12 ? "standard" : "quick")
        : Tier;

    public string ChatModelFile => EffectiveTier switch
    {
        "quick" => QuickModel,
        "quality" => QualityModel,
        _ => StandardModel,
    };

    private static ulong _totalRamBytes = 0;
    public static ulong TotalRamGb()
    {
        if (_totalRamBytes == 0)
        {
            var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            if (Native.GlobalMemoryStatusEx(ref mem)) _totalRamBytes = mem.ullTotalPhys;
        }
        return _totalRamBytes / (1024 * 1024 * 1024);
    }
}

internal static class Native
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
