using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>レイテンシ計測（Diagnostics）の不変条件（Phase 0.1）:
/// ①パーセンタイルはnearest-rankの純関数 ②リングバッファは直近200件のみ保持
/// ③未計測(-1)はTTFT集計から除外 ④テレメトリ送信・ファイル書き込みは無い（ローカル完結）。</summary>
public class DiagnosticsTests
{
    [Fact]
    public void Percentile_Empty_IsZero() =>
        Assert.Equal(0, Diagnostics.Percentile(Array.Empty<double>(), 50));

    [Fact]
    public void Percentile_SingleValue_IsThatValue()
    {
        Assert.Equal(7, Diagnostics.Percentile(new[] { 7.0 }, 50));
        Assert.Equal(7, Diagnostics.Percentile(new[] { 7.0 }, 95));
    }

    /// <summary>nearest-rank法: 1..100 のとき p50=50 / p95=95 / p1=1 / p100=100。</summary>
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(50.0, 50.0)]
    [InlineData(95.0, 95.0)]
    [InlineData(100.0, 100.0)]
    public void Percentile_NearestRank_OnOneToHundred(double p, double expected)
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Assert.Equal(expected, Diagnostics.Percentile(values, p));
    }

    [Fact]
    public void Percentile_ClampsOutOfRange()
    {
        var values = new[] { 1.0, 2.0, 3.0 };
        Assert.Equal(1, Diagnostics.Percentile(values, -10));
        Assert.Equal(3, Diagnostics.Percentile(values, 500));
    }

    [Fact]
    public void Percentile_DoesNotMutateInputOrder()
    {
        var values = new[] { 5.0, 1.0, 3.0 };
        _ = Diagnostics.Percentile(values, 50);
        Assert.Equal(new[] { 5.0, 1.0, 3.0 }, values);
    }

    [Fact]
    public void RingBuffer_KeepsOnlyLatest200Samples()
    {
        var d = new Diagnostics();
        for (var i = 0; i < 250; i++)
            d.Record(new Diagnostics.Sample("rag", "answer", false, i, 0, 0, 0, 0, 0, i));

        Assert.Equal(Diagnostics.Capacity, d.Count);
        var snap = d.Snapshot();
        Assert.Equal(200, snap.Samples);
        // 古い50件(ttfb 0..49)は捨てられ、残るのは50..249
        Assert.Equal(149, snap.TtfbP50Ms); // nearest-rank: ceil(0.5*200)-1 = 99 → 50+99
        Assert.Equal(239, snap.TtfbP95Ms); // ceil(0.95*200)-1 = 189 → 50+189
    }

    [Fact]
    public void Snapshot_ExcludesUnmeasuredTtfb()
    {
        var d = new Diagnostics();
        d.Record(new Diagnostics.Sample("refusal", "refusal", false, -1, 5, 0, 1, 0, 0, 10));
        d.Record(new Diagnostics.Sample("rag", "answer", false, 10, 5, 0, 1, 0, 3, 100));
        d.Record(new Diagnostics.Sample("rag", "answer", false, 30, 5, 0, 1, 0, 3, 120));

        var snap = d.Snapshot();
        Assert.Equal(3, snap.Samples);
        Assert.Equal(10, snap.TtfbP50Ms); // [-1を除外] 10, 30 の下位
        Assert.Equal(30, snap.TtfbP95Ms);
    }

    [Fact]
    public void Snapshot_CountsCachedSamples()
    {
        var d = new Diagnostics();
        d.Record(new Diagnostics.Sample("cache", "answer", true, 1, 1, 0, 0, 0, 0, 1));
        d.Record(new Diagnostics.Sample("cache", "answer", true, 2, 1, 0, 0, 0, 0, 2));
        d.Record(new Diagnostics.Sample("rag", "answer", false, 3, 1, 1, 1, 1, 1, 3));

        var snap = d.Snapshot();
        Assert.Equal(3, snap.Samples);
        Assert.Equal(2, snap.CachedSamples);
    }

    [Fact]
    public void Snapshot_StageKeys_MatchChatDoneFields()
    {
        var d = new Diagnostics();
        d.Record(new Diagnostics.Sample("rag", "answer", false, 100, 10, 20, 40, 50, 50, 200));

        var snap = d.Snapshot();
        foreach (var key in new[] { "t_embed_ms", "t_search_ms", "t_retrieve_ms", "t_prompt_ms", "t_prefill_ms", "t_first_token_ms", "t_total_ms" })
            Assert.True(snap.Stages.ContainsKey(key), $"stage key missing: {key}");

        Assert.Equal(10, snap.Stages["t_embed_ms"].P50);
        Assert.Equal(40, snap.Stages["t_retrieve_ms"].P50);
        Assert.Equal(100, snap.Stages["t_first_token_ms"].P50);
        Assert.Equal(200, snap.Stages["t_total_ms"].P50);
    }

    [Fact]
    public void Snapshot_Empty_ReturnsZeros()
    {
        var snap = new Diagnostics().Snapshot();
        Assert.Equal(0, snap.Samples);
        Assert.Equal(0, snap.TtfbP50Ms);
        Assert.Equal(0, snap.TtfbP95Ms);
        Assert.All(snap.Stages.Values, s => { Assert.Equal(0, s.P50); Assert.Equal(0, s.P95); });
    }
}
