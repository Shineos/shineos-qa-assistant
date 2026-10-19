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
