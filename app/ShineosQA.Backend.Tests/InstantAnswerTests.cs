using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public sealed class InstantAnswerTests : IDisposable
{
    private readonly Db _db;

    public InstantAnswerTests()
    {
        _db = new Db(Path.Combine(Path.GetTempPath(), "shineqa-instant-" + Guid.NewGuid().ToString("N") + ".db"));
    }

    public void Dispose() => _db.Dispose();

    private long AddFile(string name, string status = "ready")
    {
        _db.Exec("INSERT INTO files(name,status) VALUES($n,$s)", ("$n", name), ("$s", status));
        return _db.LastInsertId();
    }

    [Fact]
    public void Faq_ExactAndVariantMatch_IncrementsHits()
    {
        var faq = new FaqStore(_db);
        var id = faq.Upsert("経費精算の手順は？", "経費精算の手順は…", "[]", new[] { "経費精算のやり方" });
        Assert.True(id > 0);

        var hit = faq.TryMatch("経費精算の手順は");
        Assert.NotNull(hit);
        Assert.Equal("経費精算の手順は…", hit!.Answer);
        Assert.Equal(1, hit.Hits);

        var variant = faq.TryMatch("経費精算のやり方は？");
        Assert.NotNull(variant);
        Assert.Equal(2, variant!.Hits);

        Assert.Null(faq.TryMatch("経費精算の期限は？"));
    }

    [Fact]
    public void Faq_UpsertSameNorm_UpdatesInsteadOfDuplicating()
    {
        var faq = new FaqStore(_db);
        var id1 = faq.Upsert("経費精算の手順", "A", "[]");
        var id2 = faq.Upsert("経費精算の手順は？", "B", "[]");
        Assert.Equal(id1, id2);
        Assert.Equal("B", faq.TryMatch("経費精算の手順")!.Answer);
        Assert.Single(faq.List());
    }

    [Fact]
    public void DrawingQuery_AnswersMaterial_FromMetadata()
    {
        var fid = AddFile("ST-1042A.pdf");
        _db.Exec("INSERT INTO drawing_meta(file_id,zuban_raw,zuban_norm,hinmei,zairyo,scale,revision,approved_at) " +
                 "VALUES($f,'ST-1042A','st1042a','ブラケット','SUS304','1:2','B','2026-04-01')", ("$f", fid));

        Assert.True(DrawingQuery.TryAnswer(_db, "ST-1042Aの材質は？", "st1042a",
            out var answer, out var sources, out var conf));
        Assert.Contains("SUS304", answer);
        Assert.Contains("改訂 B", answer);
        Assert.Equal(1.0, conf);
        Assert.Single(sources);
        Assert.True(sources[0].IsDrawing);
        Assert.Equal("ST-1042A", sources[0].Zuban);
    }

    [Fact]
    public void DrawingQuery_MultipleRevisions_AreNotMixed()
    {
        var fidA = AddFile("ST-2000-A.pdf");
        _db.Exec("INSERT INTO drawing_meta(file_id,zuban_raw,zuban_norm,zairyo,revision) VALUES($f,'ST-2000','st2000','SUS304','A')", ("$f", fidA));
        var fidB = AddFile("ST-2000-B.pdf");
        _db.Exec("INSERT INTO drawing_meta(file_id,zuban_raw,zuban_norm,zairyo,revision) VALUES($f,'ST-2000','st2000','S45C','B')", ("$f", fidB));

        Assert.True(DrawingQuery.TryAnswer(_db, "ST-2000の材質は？", "st2000",
            out var all, out _, out var cAll));
        Assert.Contains("改訂 A", all);
        Assert.Contains("改訂 B", all);
        Assert.Equal(0.75, cAll);

        Assert.True(DrawingQuery.TryAnswer(_db, "ST-2000の改訂Bの材質は？", "st2000",
            out var b, out _, out var cB));
        Assert.Contains("S45C", b);
        Assert.DoesNotContain("SUS304", b);
        Assert.Equal(1.0, cB);
    }

    [Fact]
    public void JissekiQuery_AnswersEstimateAndCost()
    {
        var fid = AddFile("jisseki.xlsx");
        _db.Exec("INSERT INTO jisseki_meta(file_id,row_no,zuban_raw,zuban_norm,mitumori,genka,nouki) " +
                 "VALUES($f,1,'ST-1042A','st1042a','120,000','85,000','2026-04-10')", ("$f", fid));

        Assert.True(JissekiQuery.TryAnswer(_db, "ST-1042Aの見積と原価は？", "st1042a",
            out var answer, out var sources, out var conf));
        Assert.Contains("120,000", answer);
        Assert.Contains("85,000", answer);
        Assert.Single(sources);
        Assert.Equal(0.85, conf);
    }
}
