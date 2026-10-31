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

/// <summary>列マッピング手動上書き（v2.7.0）: 管理者が訂正した列設定で抽出する</summary>
public class SheetJissekiManualMapTests
{
    private static List<(string, string[][])> Sheets(string[][] rows) => new() { ("Sheet1", rows) };

    [Fact]
    public void ManualMap_OverridesHeaderDetection()
    {
        // ヘッダは「品番/見積額/原価」だが、管理者が「備考=納期」と訂正したケースを再現:
        // 自動検出では納期は検出されない。明示マッピング (zuban=0, mitumori=1, genka=2, nouki=3) で
        // 4列目の値が納期に入ることを保証する
        var rows = new[] { new[] { "品番", "見積額", "原価", "備考" } }
            .Concat(Enumerable.Range(1, 3).Select(i => new[] { $"ST-{1000 + i}", $"{i * 1000}円", $"{i * 500}円", $"{i}日" }))
            .ToArray();
        var rowsList = Sheets(rows);
        var rowsResult = SheetJisseki.ExtractRowsWithMap(rowsList, (0, 1, 2, 3));
        Assert.Equal(3, rowsResult.Count);
        Assert.Equal("1日", rowsResult[0].Nouki);
        Assert.Equal("3日", rowsResult[2].Nouki);
    }

    [Fact]
    public void ManualMap_WithoutZuban_ReturnsEmpty()
    {
        var rows = new[] { new[] { "品名", "金額" }, new[] { "ブラケット", "1,000円" } };
        var result = SheetJisseki.ExtractRowsWithMap(Sheets(rows), (-1, 1, -1, -1));
        Assert.Empty(result);
    }

    [Fact]
    public void ManualMap_AppliesToAllSheets()
    {
        var sheets = new List<(string, string[][])>
        {
            ("A", new[] { new[] { "図番", "見積" }, new[] { "ST-1", "100円" } }),
            ("B", new[] { new[] { "図番", "見積" }, new[] { "ST-2", "200円" } }),
        };
        var result = SheetJisseki.ExtractRowsWithMap(sheets, (0, 1, -1, -1));
        Assert.Equal(2, result.Count);
        Assert.Equal("200円", result[1].Mitumori);
    }
}
