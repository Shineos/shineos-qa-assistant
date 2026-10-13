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
