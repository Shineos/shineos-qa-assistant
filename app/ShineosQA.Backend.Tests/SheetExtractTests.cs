using System.IO.Compression;
using System.Text;
using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>表計算ファイル取り込み（Excel・CSV拡張パック）。xlsxはテスト内で最小OpenXMLをZIP合成して検証</summary>
public class SheetExtractTests
{
    private static byte[] MakeXlsx(string sharedXml, string sheet1Xml)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, xml) in new[]
            {
                ("[Content_Types].xml", "<Types/>"),
                ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"部品表\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
                ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
                ("xl/sharedStrings.xml", sharedXml),
                ("xl/worksheets/sheet1.xml", sheet1Xml),
            })
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void Csv_BasicRows_ProduceChunkWithHeader()
    {
        var bytes = Encoding.UTF8.GetBytes("品名,材質,数量\nブラケット,SS400,10\nプレート,SUS304,5\n");
        var chunks = SheetExtract.ExtractChunks("buhin.csv", bytes);
        var c = Assert.Single(chunks);
        Assert.Contains("【表】buhin.csv", c);
        Assert.Contains("品名 | 材質 | 数量", c);
        Assert.Contains("ブラケット | SS400 | 10", c);
        Assert.Contains("プレート | SUS304 | 5", c);
    }

    [Fact]
    public void Csv_ShiftJis_Decoded()
    {
        // 日本語CSVの実勢エンコーディング（Shift-JIS）をUTF-8判定フォールバックで読めること
        var bytes = Encoding.GetEncoding("shift_jis").GetBytes("品名,材質\nブラケット,SS400\n");
        var chunks = SheetExtract.ExtractChunks("sjis.csv", bytes);
        Assert.Contains("ブラケット", Assert.Single(chunks));
    }

    [Fact]
    public void Csv_QuotedCommaAndEscapedQuote()
    {
        var bytes = Encoding.UTF8.GetBytes("備考,値\n\"カンマ, あり\"\"二重\"\",\",1\n");
        var rows = SheetExtract.ParseCsv(bytes, ',');
        Assert.Equal(2, rows.Length);
        Assert.Equal("カンマ, あり\"二重\",", rows[1][0]);
    }

    [Fact]
    public void Xlsx_SharedStringsAndNumbers_Parsed()
    {
        // 合成xlsx: 共有文字列2件・セルは文字列(t=s)と数値(無属性)を混在
        var bytes = MakeXlsx(
            "<sst><si><t>品名</t></si><si><t>材質</t></si><si><t>サポートブラケット</t></si></sst>",
            "<worksheet><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\"><v>1042</v></c></row>" +
            "</sheetData></worksheet>");
        var (name, rows) = Assert.Single(SheetExtract.ParseXlsx(bytes));
        Assert.Equal("部品表", name);
        Assert.Equal(2, rows.Length);
        Assert.Equal("品名", rows[0][0]);
        Assert.Equal("材質", rows[0][1]);
        Assert.Equal("サポートブラケット", rows[1][0]);
        Assert.Equal("1042", rows[1][1]);
    }

    [Fact]
    public void Xlsx_Chunks_RepeatHeaderAndBatchRows()
    {
        // 75データ行 → 30行ずつ3チャンク。各チャンクにヘッダ行が複製されること
        var rows = new StringBuilder("<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row>");
        var data = new StringBuilder();
        for (int i = 0; i < 75; i++)
            data.Append($"\n<row><c><v>{i + 1}</v></c></row>");
        var bytes = MakeXlsx(
            "<sst><si><t>番号</t></si></sst>",
            $"<worksheet><sheetData>{rows}{data}</sheetData></worksheet>");
        var chunks = SheetExtract.ExtractChunks("lots.xlsx", bytes);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Contains("番号", c));
        Assert.Contains("2〜31行目", chunks[0]); // ヘッダ=1行目、データは2行目から（Excel行番号）
    }

    [Fact]
    public void SupportedExtensions_IncludeSheetFormats()
    {
        // Excel・CSV取り込みは本体標準機能（設定不要）: 基本拡張子に常に含まれる
        Assert.Contains(".xlsx", Ingest.SupportedExtensions);
        Assert.Contains(".csv", Ingest.SupportedExtensions);
        Assert.Contains(".tsv", Ingest.SupportedExtensions);
    }
}
