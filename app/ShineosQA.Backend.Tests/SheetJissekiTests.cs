using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>実績表抽出(SheetJisseki)の検証。実績Excel/CSVのヘッダゆれ（見積額/見積金額 等）を
/// 吸収して図番キーの辞書を構築し、図面QAの回答文脈へ過去実績を接続する（v2.3.0 C1）</summary>
public class SheetJissekiTests
{
    private static string[][] Sheet(params string[][] rows) => rows;

    [Fact]
    public void MapColumns_DetectsLabelVariants()
    {
        var (z, m, g, n) = SheetJisseki.MapColumns(new[] { "品名", "図番", "見積額", "原価", "納期" });
        Assert.Equal(1, z);
        Assert.Equal(2, m);
        Assert.Equal(3, g);
        Assert.Equal(4, n);
    }

    [Theory]
    [InlineData("図番号")]
    [InlineData("品番")]
    [InlineData("型番")]
    public void MapColumns_ZubanLabelVariants(string header)
    {
        var (z, _, _, _) = SheetJisseki.MapColumns(new[] { header, "その他" });
        Assert.Equal(0, z);
    }

    [Fact]
    public void MapColumns_NoZubanColumn_ReturnsMinus1()
    {
        var (z, _, _, _) = SheetJisseki.MapColumns(new[] { "品名", "数量" });
        Assert.Equal(-1, z);
    }

    [Fact]
    public void ExtractRows_KeepsRowsWithZuban_SkipsEmpty()
    {
        var sheets = new List<(string, string[][])>
        {
            ("Sheet1", Sheet(
                new[] { "図番", "見積額", "原価", "納期" },
                new[] { "ST-1042A", "150,000", "98,000", "2週間" },
                new[] { "", "999", "999", "即日" },       // 図番空行はスキップ
                new[] { "AB-9999", "80,000", "50,000", "1週間" })),
        };
        var rows = SheetJisseki.ExtractRows(sheets);
        Assert.Equal(2, rows.Count);
        Assert.Equal("ST-1042A", rows[0].ZubanRaw);
        Assert.Equal("150,000", rows[0].Mitumori);
        Assert.Equal("1週間", rows[1].Nouki);
    }

    [Fact]
    public void ExtractRows_NormalizedZuban_MatchesDrawingMeta()
    {
        // drawing_meta の ST-1042A と実績表の st1042a / ＳＴ－１０４２Ａ が同一図番として結合すること
        var rows = SheetJisseki.ExtractRows(new List<(string, string[][])>
        {
            ("Sheet1", Sheet(
                new[] { "図番", "見積額" },
                new[] { "st1042a", "120,000" },
                new[] { "ＳＴ－１０４２Ａ", "130,000" })),
        });
        Assert.All(rows, r => Assert.Equal("st1042a", r.ZubanNorm));
    }

    [Fact]
    public void ExtractRows_SheetWithoutZubanColumn_Skipped()
    {
        var rows = SheetJisseki.ExtractRows(new List<(string, string[][])>
        {
            ("Sheet1", Sheet(new[] { "品名", "数量" }, new[] { "ブラケット", "10" })),
        });
        Assert.Empty(rows);
    }

    [Fact]
    public void FormatJisseki_EmptyValues_Omitted()
    {
        var s = SheetJisseki.FormatJisseki("ST-1042A", "150,000", null, "2週間");
        Assert.Contains("過去見積 150,000", s);
        Assert.Contains("納期 2週間", s);
        Assert.DoesNotContain("原価", s);
    }
}
