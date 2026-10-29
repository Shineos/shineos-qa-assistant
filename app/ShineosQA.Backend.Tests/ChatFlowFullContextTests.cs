using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>全量コンテキストモードの不変条件（Phase 1.2修正）:
/// ①status='ready' のチャンクだけを読む（索引 ChunkIndex.LoadFrom と母集団を揃える）
/// ②合成Recは実seqを保持する（旧実装は0固定で、隣接chunk連結が全チャンクにseq1を付加していた）
/// ③全量モードの出典本文は隣接chunk連結をしない（same文の二重注入を防ぐ）。</summary>
public class ChatFlowFullContextTests : IDisposable
{
    private readonly string _path;
    private readonly Db _db;

    public ChatFlowFullContextTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "shineqa-fullctx-" + Guid.NewGuid().ToString("N") + ".db");
        _db = new Db(_path);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var p in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(p); } catch { /* 一時ファイルの掃除失敗は無視 */ }
        }
    }

    private long AddFile(string name, string status)
    {
        _db.Exec("INSERT INTO files(name, status) VALUES($n,$s)", ("$n", name), ("$s", status));
        return _db.LastInsertId();
    }

    private void AddChunk(long fileId, int seq, string text) =>
        _db.Exec("INSERT INTO chunks(file_id, seq, text, emb) VALUES($f,$s,$t,$e)",
            ("$f", fileId), ("$s", seq), ("$t", text), ("$e", ChunkIndex.FloatsToBytes(new[] { 0f })));

    [Fact]
    public void LoadFullContextChunks_OnlyReadyFiles()
    {
        var ready = AddFile("ready.md", "ready");
        AddChunk(ready, 0, "ready chunk");
        var embedding = AddFile("pending.md", "embedding");
        AddChunk(embedding, 0, "pending chunk");
        var error = AddFile("broken.md", "error");
        AddChunk(error, 0, "error chunk");

        var chunks = ChatFlow.LoadFullContextChunks(_db);

        Assert.Single(chunks);
        Assert.Equal("ready.md", chunks[0].Rec.FileName);
        Assert.Equal("ready chunk", chunks[0].Rec.Text);
    }

    [Fact]
    public void LoadFullContextChunks_PreservesRealSeqAndOrder()
    {
        var fileA = AddFile("a.md", "ready");
        AddChunk(fileA, 0, "a0");
        AddChunk(fileA, 1, "a1");
        AddChunk(fileA, 2, "a2");
        var fileB = AddFile("b.md", "ready");
        AddChunk(fileB, 0, "b0");

        var chunks = ChatFlow.LoadFullContextChunks(_db);

        Assert.Equal(new[] { 0, 1, 2, 0 }, chunks.Select(c => c.Rec.Seq).ToArray());
        Assert.Equal(new[] { "a0", "a1", "a2", "b0" }, chunks.Select(c => c.Rec.Text).ToArray());
        Assert.Equal(new[] { "a.md", "a.md", "a.md", "b.md" }, chunks.Select(c => c.Rec.FileName).ToArray());
    }

    [Fact]
    public void LoadFullContextChunks_EmptyDb_ReturnsEmpty()
    {
        Assert.Empty(ChatFlow.LoadFullContextChunks(_db));
    }

    [Fact]
    public void SourceTextFor_FullContext_DoesNotAppendAdjacentChunk()
    {
        // 全量モードで seq=0 固定だった旧実装では、NextChunkText が全チャンクに同じ続きを
        // 連結してしまう（重複）ため、全量モードでは連結しないことを固定する。
        var rec = new ChunkIndex.Rec(1, 10, "a.md", 3, "本文", Array.Empty<float>(), new HashSet<string>());
        Assert.Equal("本文", ChatFlow.SourceTextFor(true, rec, "次の本文"));
    }

    [Fact]
    public void SourceTextFor_StandardRag_AppendsAdjacentChunk()
    {
        var rec = new ChunkIndex.Rec(1, 10, "a.md", 3, "本文", Array.Empty<float>(), new HashSet<string>());
        Assert.Equal("本文\n次の本文", ChatFlow.SourceTextFor(false, rec, "次の本文"));
        Assert.Equal("本文", ChatFlow.SourceTextFor(false, rec, null));
    }
}
