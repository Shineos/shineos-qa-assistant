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
        Assert.Contains("尺度: 1/2", chunk); // 尺度も前置きに含める（cr05対策）
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

    // ---- 実図面（技能検定解答例PDF）で発生した誤抽出への検証器テスト ----

    [Theory]
    [InlineData("1:1", false)]      // 尺度表記（LLMが図番として返した実図面の誤り）
    [InlineData("1/2", false)]
    [InlineData("ST-1042A", true)]
    [InlineData("D24", true)]
    [InlineData("160", false)]
    public void IsPlausibleZuban_RejectsScaleAndNonPatterns(string v, bool expected) =>
        Assert.Equal(expected, DrawingIngest.IsPlausibleZuban(v));

    [Theory]
    [InlineData("Ø160", false)]     // 寸法値（LLMが品名として返した実図面の誤り）
    [InlineData("M6×15/Ø4.8×20", false)]
    [InlineData("R5", false)]
    [InlineData("サポートブラケット", true)]
    [InlineData("BRACKET", true)]
    public void IsPlausibleHinmei_RejectsDimensionNotes(string v, bool expected) =>
        Assert.Equal(expected, DrawingIngest.IsPlausibleHinmei(v));

    [Theory]
    [InlineData("8 Ø126 Rc1/16", false)] // 寸法＋ねじ規格（LLMが材質として返した実図面の誤り）
    [InlineData("Rc1/16", false)]
    [InlineData("SS400", true)]
    [InlineData("S45C", true)]
    [InlineData("アルミ", true)]
    public void IsPlausibleZairyo_RejectsDimensionNoise(string v, bool expected) =>
        Assert.Equal(expected, DrawingIngest.IsPlausibleZairyo(v));

    [Fact]
    public void PickSheetPage_PicksSheetPageNotCover()
    {
        // 表紙（図番風のD23/D24のみ・表題欄ラベルなし）+ 図面シート（表題欄ラベルあり）の2頁構成
        var runs = new List<PdfTextRun>
        {
            new("D23", 1, 100, 500, 20, 10), new("D24", 1, 130, 500, 20, 10),
            new("実技試験", 1, 100, 300, 60, 10), new("解答例", 1, 100, 280, 40, 10),
            new("尺度", 2, 700, 40, 20, 10), new("1:1", 2, 725, 40, 20, 10),
            new("投影法", 2, 700, 55, 30, 10),
            new("普通公差", 2, 700, 70, 40, 10),
            new("Ø160", 2, 300, 300, 30, 10),
        };
        Assert.Equal(2, DrawingIngest.PickSheetPage(runs, 2));
    }

    [Fact]
    public void PageDims_ComputesFromRuns()
    {
        var runs = new List<PdfTextRun>
        {
            new("A", 1, 10, 10, 20, 10), new("B", 1, 100, 200, 30, 12),
            new("C", 2, 5, 5, 5, 5),
        };
        var (w, h) = DrawingIngest.PageDims(runs, 1);
        Assert.Equal(130, w);
        Assert.Equal(212, h);
    }
}
