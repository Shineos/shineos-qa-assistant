using System.Text;
using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>Ingest入口（ExtractText）の拡張子ルーティングとエラー契約。
/// .dwg拒否の個別検証は DxfTextTests、PDF抽出の中身は PdfTextTests を参照</summary>
public class IngestTests
{
    [Theory]
    [InlineData("a.zip")]
    [InlineData("b.exe")]
    [InlineData("c.dat")]
    public void ExtractText_UnsupportedExtension_ThrowsClearError(string name)
    {
        // 対応外拡張子は、無説明の500ではなくエラーコード付きの明確な例外で拒否されること
        using var s = new MemoryStream(new byte[] { 1, 2, 3 });
        var ex = Assert.Throws<NotSupportedException>(() => Ingest.ExtractText(name, s));
        Assert.Contains("SHINE_E_DOC_PARSE_FAILED", ex.Message);
        Assert.Contains(Path.GetExtension(name), ex.Message);
    }

    [Fact]
    public void SupportedExtensions_ContainsDocumentedSet()
    {
        // 本体4種（md/txt/docx/pdf）+ 表計算3種（xlsx/csv/tsv）+ 図面1種（dxf）の計8種が契約
        var expected = new[] { ".md", ".txt", ".docx", ".pdf", ".xlsx", ".csv", ".tsv", ".dxf" };
        foreach (var ext in expected) Assert.Contains(ext, Ingest.SupportedExtensions);
        Assert.Equal(expected.Length, Ingest.SupportedExtensions.Length);
    }
}

/// <summary>バイナリジャンク判定(LooksLikeBinaryJunkText)。ICC辞書断片330字が「テキスト」として
/// 取り込まれた実害（Edge印字PDF・2026-10-16）の回帰防止。印字不可能・規格外文字が1割超でジャンク</summary>
public class BinaryJunkTextTests
{
    [Fact]
    public void NormalJapaneseText_NotJunk()
    {
        Assert.False(Ingest.LooksLikeBinaryJunkText("タイムカードの締め日は毎月25日です。残業は事前申請が必要です。"));
    }

    [Fact]
    public void NormalEnglishText_NotJunk()
    {
        Assert.False(Ingest.LooksLikeBinaryJunkText("The timecard cutoff date is the 25th of every month."));
    }

    [Fact]
    public void ControlCodeHeavyText_IsJunk()
    {
        // ICC/PDF辞書断片の模倣: 印字可能文字に制御コードが混ざる（1割超）
        var junk = "gTRC\u0000\u0001(bTRC\u0002\u0003(cprt\u0004\u0005desc\u0006";
        Assert.True(Ingest.LooksLikeBinaryJunkText(junk));
    }

    [Fact]
    public void Latin1ExtendedChars_AreJunk()
    {
        // Latin-1拡張領域（U+00A0〜U+00FF）は印字可能扱いにしない（バイト列の誤デコード断片）
        var junk = new string('\u00C0', 30) + "abc";
        Assert.True(Ingest.LooksLikeBinaryJunkText(junk));
    }

    [Fact]
    public void EmptyOrWhitespace_IsJunk()
    {
        Assert.True(Ingest.LooksLikeBinaryJunkText(""));
        Assert.True(Ingest.LooksLikeBinaryJunkText("   \n  "));
    }

    [Fact]
    public void Boundary_AtExactly10Percent_IsJunk()
    {
        // 100文字中10文字が規格外 → 10%でジャンク（整数除算の境界仕様）
        var t = new string('a', 90) + new string('\u0001', 10);
        Assert.True(Ingest.LooksLikeBinaryJunkText(t));
        var below = new string('a', 91) + new string('\u0001', 9);
        Assert.False(Ingest.LooksLikeBinaryJunkText(below));
    }
}

/// <summary>表題欄メタの欠落補完(Ingest.FillMissing)。切り出し再OCR（v2.4.0）の結果は
/// 全景OCRで取れた欄を上書きせず、null欄のみ充填する保守ルールの固定</summary>
public class FillMissingTests
{
    [Fact]
    public void NullFields_FilledFromFallback()
    {
        var primary = new DrawingIngest.DrawingMeta("ST-1042A", null, null, null, null, null);
        var fallback = new DrawingIngest.DrawingMeta("ST-9999Z", "サポートブラケット", "SS400", "1:2", "B", "2026-10-19");
        var r = Ingest.FillMissing(primary, fallback);
        Assert.Equal("ST-1042A", r.ZubanRaw); // 既に取れた欄は上書きしない
        Assert.Equal("サポートブラケット", r.Hinmei);
        Assert.Equal("SS400", r.Zairyo);
        Assert.Equal("1:2", r.Scale);
        Assert.Equal("B", r.Revision);
        Assert.Equal("2026-10-19", r.ApprovedAt);
    }

    [Fact]
    public void CompletePrimary_Unchanged()
    {
        var primary = new DrawingIngest.DrawingMeta("ST-1042A", "ブラケット", "SS400", "1:1", "A", "2026-01-01");
        var fallback = new DrawingIngest.DrawingMeta("X", "Y", "Z", "9:9", "Z", "1999-01-01");
        Assert.Equal(primary, Ingest.FillMissing(primary, fallback));
    }
}

/// <summary>Phase 0.2: 取り込み重複排除（content_hash）のSHA-256キー生成の固定</summary>
public class IngestContentHashTests
{
    [Fact]
    public void ComputeSha256Hex_KnownVector_IsLowercaseHex()
    {
        // SHA-256("abc") の既知値（小文字hex・64桁）
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Ingest.ComputeSha256Hex(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact]
    public void ComputeSha256Hex_EmptyInput_Stable()
    {
        var a = Ingest.ComputeSha256Hex(Array.Empty<byte>());
        var b = Ingest.ComputeSha256Hex(Array.Empty<byte>());
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }
}

/// <summary>Phase 0.2: IngestFileAsync の重複排除と原子性。Supervisorがエンジン未配置で即失敗する
/// 環境を利用し、埋め込み段階の失敗でチャンクが部分残存しないことを検証する（実エンジンは起動しない）</summary>
public class IngestDedupAtomicityTests
{
    private sealed class SilentLogger : ILogger
    {
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
    }

    /// <summary>エンジン・モデル未配置の一時環境で Ingest を構築する（取り込みの重複判定と失敗経路のみ検証）</summary>
    private static (Db Db, Ingest Ingest, string Dir) NewIngest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shineqa-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = new AppConfig
        {
            DataDir = dir,
            EngineDir = Path.Combine(dir, "no-engine"),
            ModelsDir = Path.Combine(dir, "no-models"),
        };
        var db = new Db(Path.Combine(dir, "knowledge.db"));
        var gw = new LlmGateway();
        var sup = new Supervisor(cfg, gw, new SilentLogger());
        return (db, new Ingest(db, gw, sup, new ChunkIndex(), cfg, new SilentLogger()), dir);
    }

    private static void Cleanup(Db db, string dir)
    {
        db.Dispose();
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task SameContent_ReadyExists_ReturnsExistingId_WithoutNewRow()
    {
        var (db, ingest, dir) = NewIngest();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("重複排除テスト用の文書です。同じ内容は再登録しません。");
            var hash = Ingest.ComputeSha256Hex(bytes);
            db.Exec("INSERT INTO files(name, status, content_hash) VALUES('orig.md', 'ready', $h)", ("$h", hash));
            var existingId = db.LastInsertId();

            var returned = await ingest.IngestFileAsync("copy.md", new MemoryStream(bytes), CancellationToken.None);

            Assert.Equal(existingId, returned); // 既存file_idを返す
            Assert.Single(db.Query("SELECT file_id FROM files")); // 新規行を作らない
            Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM chunks")));
        }
        finally { Cleanup(db, dir); }
    }

    [Fact]
    public async Task EmbeddingFailure_MarksError_AndLeavesNoChunksOrMetas()
    {
        var (db, ingest, dir) = NewIngest();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("埋め込み失敗時の原子性テスト用文書です。");
            var hash = Ingest.ComputeSha256Hex(bytes);

            // エンジン未配置 → StartEmbedAndRank が即失敗（埋め込み段階で例外）
            await Assert.ThrowsAnyAsync<Exception>(() =>
                ingest.IngestFileAsync("atomic.txt", new MemoryStream(bytes), CancellationToken.None));

            var rows = db.Query("SELECT file_id, status, chunk_count, content_hash, error FROM files");
            var row = Assert.Single(rows);
            Assert.Equal("error", row["status"]);           // 既存挙動: 失敗はerror行として残す
            Assert.Equal(hash, row["content_hash"]);        // hashは進捗行に保存済み
            Assert.NotEqual(DBNull.Value, row["error"]);    // 原因メッセージあり
            Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM chunks")));
            Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM drawing_meta")));
            Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM jisseki_meta")));

            // エラー行は重複排除の対象外 → 同一内容の再取り込みは新規行を作って再試行される（＝重複で握りつぶさない）
            await Assert.ThrowsAnyAsync<Exception>(() =>
                ingest.IngestFileAsync("atomic.txt", new MemoryStream(bytes), CancellationToken.None));
            Assert.Equal(2, db.Query("SELECT file_id FROM files").Count);
            Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM chunks")));
        }
        finally { Cleanup(db, dir); }
    }
}
