using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>DLストール監視(CopyWithStallWatchdogAsync)。実測2026-10-14: huggingfaceへのDLが
/// 54MBで接続生きたまま停止し、ReadAsyncが無期限に待ちミラーへも落ちない実害の再発防止。
/// ストールは「決して完了しないStream」で再現し、短いstallTimeoutで発火を検証する</summary>
public class ModelStallWatchdogTests
{
    /// <summary>ReadAsyncがct取消まで完了しないストリーム（ネットワークスタールの再現）</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }
    }

    [Fact]
    public async Task StalledStream_ThrowsIOException_AfterStallTimeout()
    {
        using var src = new StalledStream();
        using var dst = new MemoryStream();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            ModelManager.CopyWithStallWatchdogAsync(src, dst, null, TimeSpan.FromMilliseconds(500), CancellationToken.None));
        sw.Stop();
        Assert.Contains("stalled", ex.Message);
        // 監視が実際に短時間で発火すること（1秒以内に切断 = ポーリングが動いている）
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"watchdog fired at {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task FlowingStream_CopiesAllBytes_WithoutStall()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes("GGUF" + new string('x', 5000));
        using var src = new MemoryStream(payload);
        using var dst = new MemoryStream();
        long copied = await ModelManager.CopyWithStallWatchdogAsync(src, dst, null, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Equal(payload.Length, copied);
        Assert.Equal(payload, dst.ToArray());
    }

    [Fact]
    public async Task UserCancellation_PropagatesAsOperationCanceled_NotIOException()
    {
        using var src = new StalledStream();
        using var dst = new MemoryStream();
        using var cts = new CancellationTokenSource(200); // ユーザー中断を200ms後に模擬
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelManager.CopyWithStallWatchdogAsync(src, dst, null, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task ProgressCallback_ReportsChunkBytes()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(new string('y', 3000));
        using var src = new MemoryStream(payload);
        using var dst = new MemoryStream();
        long reported = 0;
        await ModelManager.CopyWithStallWatchdogAsync(src, dst, n => reported += n, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(payload.Length, reported);
    }
}
