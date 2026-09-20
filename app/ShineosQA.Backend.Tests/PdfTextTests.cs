using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>PdfPig抽出器（T1）のフィクスチャテスト。フィクスチャはEdgeヘッドレスで生成した実物相当のPDF
/// （TestData/src/*.html から再生成可能。生成コマンドはTestData/src/README.md参照）</summary>
public class PdfTextTests
{
    private static byte[] Fixture(string name) =>
        System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name))
        ?? throw new InvalidOperationException($"fixture missing: {name}（TestData/srcから再生成してください）");

    [Fact]
    public void ExtractAll_JapanesePdf_ExtractsKanji()
    {
        // 現行の自製抽出器が対応できないCID/Type0系日本語PDFでも文字化けなく抽出できること
        var r = PdfText.ExtractAll(Fixture("cid-japanese.pdf"));
        Assert.Contains("旅費規程", r.Text);
        Assert.Contains("15,000", r.Text);
        Assert.True(r.Pages >= 1);
    }

    [Fact]
    public void ExtractAll_DrawingSample_HasRunsInTitleBlockRegion()
    {
        var r = PdfText.ExtractAll(Fixture("drawing-sample.pdf"));
        Assert.Equal(1, r.Pages);
        Assert.True(r.PageWidth > 0 && r.PageHeight > 0);
        // 表題欄（右下領域: x>0.55W, y<0.40H・原点左下）にrunsが存在すること
        var region = r.Runs.Where(x => x.X > r.PageWidth * 0.55f && x.Y < r.PageHeight * 0.40f).ToList();
        Assert.Contains(region, x => x.Text.Contains("ST-1042A"));
        Assert.Contains(region, x => x.Text.Contains("サポートブラケット"));
    }

    [Fact]
    public void ExtractText_NoTextPdf_ThrowsInvalidData()
    {
        // テキスト層ゼロ: PdfPigも空・フォールバックの自製抽出器も空 → 従来どおりのエラー
        using var s = new MemoryStream(Fixture("no-text.pdf"));
        Assert.Throws<InvalidDataException>(() => Ingest.ExtractText("no-text.pdf", s));
    }

    [Fact]
    public void ExtractText_JapanesePdf_ViaPublicApi()
    {
        using var s = new MemoryStream(Fixture("cid-japanese.pdf"));
        var text = Ingest.ExtractText("travel.pdf", s);
        Assert.Contains("旅費", text); // PdfPig経路（フォールバック不要）で日本語が取れる
    }

    [Fact]
    public void Png_EncodeRgba_ProducesPngSignature()
    {
        // サムネイル用エンコーダの最小検証（1x1 RGBA）
        var png = Png.EncodeRgba(new byte[] { 255, 0, 0, 255 }, 1, 1);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Contains(png, b => b == (byte)'I'); // IHDR/IDATチャンクが書かれている
    }
}
