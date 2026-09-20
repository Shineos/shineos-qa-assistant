using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>図面判定ヒューリスティックと表題欄抽出（T4）。フィクスチャdrawing-sample.pdf（自作合成図面）で検証</summary>
public class DrawingIngestTests
{
    private static PdfExtractResult FixtureDrawing()
    {
        var bytes = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "drawing-sample.pdf"));
        return PdfText.ExtractAll(bytes);
    }

    [Fact]
    public void ZubanRegex_MatchesTypicalPatterns()
    {
        foreach (var ok in new[] { "ST-1042A", "A1234", "KB-305-2", "E-12" })
            Assert.True(DrawingIngest.ZubanRegex().IsMatch($"図番 {ok} の材質"), ok);
        foreach (var ng in new[] { "15,000円", "JIS Z 3601", "Q1", "2026-09-20" })
            Assert.False(DrawingIngest.ZubanRegex().IsMatch($"規定 {ng} です"), ng);
    }

    [Fact]
    public void Detect_DrawingSamplePdf_ClassifiedAsDrawing()
    {
        var r = FixtureDrawing();
        Assert.True(DrawingIngest.LooksLikeDrawing("drawing-sample.pdf", r.Pages, r.Text));
    }

    [Fact]
    public void Detect_TextDocument_NotDrawing()
    {
        var bytes = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "cid-japanese.pdf"));
        var r = PdfText.ExtractAll(bytes);
        Assert.False(DrawingIngest.LooksLikeDrawing("travel.pdf", r.Pages, r.Text)); // 図番パターンなし
        var longText = new string('あ', DrawingIngest.MaxTextChars + 1) + "AB1234"; // テキスト量上限超過
        Assert.False(DrawingIngest.LooksLikeDrawing("spec.pdf", 1, longText));
    }

    [Fact]
    public void ExtractTitleBlock_DrawingSample_ParsesFields()
    {
        var r = FixtureDrawing();
        var meta = DrawingIngest.ExtractTitleBlock(r.Runs, r.PageWidth, r.PageHeight);
        Assert.Equal("ST-1042A", meta.ZubanRaw);
        Assert.Equal("サポートブラケット", meta.Hinmei);
        Assert.Equal("SS400", meta.Zairyo);
        Assert.Equal("B", meta.Revision);
        Assert.Equal("st1042a", meta.ZubanNorm);
    }

    [Fact]
    public void BuildChunkText_IncludesPrefix()
    {
        var meta = new DrawingIngest.DrawingMeta("ST-1042A", "サポートブラケット", "SS400", "1/2", "B", null);
        var chunk = DrawingIngest.BuildChunkText(meta, "注記: 溶接はJIS Z 3601に従うこと。");
        Assert.StartsWith("【図面】", chunk);
        Assert.Contains("図番: ST-1042A", chunk);
        Assert.Contains("注記", chunk); // 全テキストが後置される
    }

    [Fact]
    public void ToLines_GroupsRunsByBaseline()
    {
        var runs = new List<PdfTextRun>
        {
            new("図番", 1, 400, 50, 20, 10), new("ST-1042A", 1, 425, 50, 40, 10),
            new("品名", 1, 400, 65, 20, 10), new("部品", 1, 425, 65, 20, 10),
        };
        var lines = DrawingIngest.ToLines(runs);
        Assert.Equal(2, lines.Count);
        Assert.Equal("図番 ST-1042A", lines[0]);
    }
}
