namespace ShineosQA.Backend;

/// <summary>レイテンシ計測（Phase 0.1）。直近200リクエストのステージ別所要時間をリングバッファに保持し、
/// p50/p95 を返す。**ローカル完結**（テレメトリ送信・ファイル書き込みは一切しない）。
/// 目的: 「並列ウォームアップ」「先行refs」「全量コンテキスト修正」の効果を実測で確認できるようにする
/// （数値を改善と言い切る前に、まず計測基盤を置く）。</summary>
public sealed class Diagnostics
{
    /// <summary>保持するサンプル数（古いものから捨てる）</summary>
    public const int Capacity = 200;

    /// <summary>1リクエスト分の計測値。Ms はリクエスト開始からの経過ms（-1=未計測）。
    /// Ttfb=初回deltaまで / Embed=埋め込み / Search=ハイブリッド検索 / Retrieve=検索+リランク+ガード /
    /// Prompt=プロンプト構築 / Prefill=プロンプト完成→初回delta（≒プリフィル+キュー）/ Total=全体。</summary>
    public sealed record Sample(
        string Route, string Kind, bool Cached,
        double TtfbMs, double EmbedMs, double SearchMs, double RetrieveMs,
        double PromptMs, double PrefillMs, double TotalMs);

    public sealed record Percentiles(double P50, double P95);

    public sealed record SnapshotData(
        int Samples, int CachedSamples, double TtfbP50Ms, double TtfbP95Ms,
        IReadOnlyDictionary<string, Percentiles> Stages);

    private readonly object _lock = new();
    private readonly Queue<Sample> _samples = new(Capacity);

    public int Count { get { lock (_lock) return _samples.Count; } }

    public void Record(Sample sample)
    {
        lock (_lock)
        {
            if (_samples.Count >= Capacity) _samples.Dequeue();
            _samples.Enqueue(sample);
        }
    }

    /// <summary>現在のバッファから p50/p95 を算出する（スナップショット）。空なら全項目0。
    /// ステージキーは done イベントの stages と同一（t_embed_ms 等）に揃えている。</summary>
    public SnapshotData Snapshot()
    {
        Sample[] arr;
        lock (_lock) arr = _samples.ToArray();
        var stages = new Dictionary<string, Percentiles>
        {
            ["t_embed_ms"] = Stats(arr.Select(s => s.EmbedMs)),
            ["t_search_ms"] = Stats(arr.Select(s => s.SearchMs)),
            ["t_retrieve_ms"] = Stats(arr.Select(s => s.RetrieveMs)),
            ["t_prompt_ms"] = Stats(arr.Select(s => s.PromptMs)),
            ["t_prefill_ms"] = Stats(arr.Select(s => s.PrefillMs)),
            ["t_first_token_ms"] = Stats(arr.Select(s => s.TtfbMs)),
            ["t_total_ms"] = Stats(arr.Select(s => s.TotalMs)),
        };
        // TTFTは未計測(-1)を除外して集計する（生成前に拒否した経路など）
        var ttfb = arr.Select(s => s.TtfbMs).Where(v => v >= 0).ToArray();
        return new SnapshotData(arr.Length, arr.Count(s => s.Cached),
            Percentile(ttfb, 50), Percentile(ttfb, 95), stages);
    }

    private static Percentiles Stats(IEnumerable<double> values)
    {
        var a = values.Where(v => v >= 0).ToArray();
        return new Percentiles(Percentile(a, 50), Percentile(a, 95));
    }

    /// <summary>パーセンタイル（nearest-rank法）。空配列は0、p は 0..100 にクランプ。
    /// 純関数（テスト直接検証・DiagnosticsTests参照）。</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values is null || values.Count == 0) return 0;
        p = Math.Clamp(p, 0, 100);
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        if (rank < 0) rank = 0;
        if (rank >= sorted.Length) rank = sorted.Length - 1;
        return sorted[rank];
    }
}
