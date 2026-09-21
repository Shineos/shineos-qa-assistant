using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>図面出典プレビュー: 元PDFのシートページ描画＋スニペット該当領域の図上矩形</summary>
public class SourcePreviewTests
{
    private static string TestDataPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public async Task Build_RendersSheetPageAndHighlightRects()
    {
        var bytes = await File.ReadAllBytesAsync(TestDataPath("drawing-sample.pdf"));
        var ex = ShineosQA.Backend.PdfText.ExtractWithLines(bytes);
        var line = ex.Lines.First(l => l.Text.Contains("ST-1042A"));
        var snip = ex.Text.Substring(line.Start, Math.Min(120, ex.Text.Length - line.Start));
        var r = await ShineosQA.Backend.SourcePreview.BuildAsync(bytes, snip, System.Threading.CancellationToken.None);
        Assert.NotNull(r);
        Assert.StartsWith("data:image/png;base64,", r!.ImageDataUrl);
        Assert.True(r.ImageWidth > 0 && r.ImageHeight > 0);
        Assert.NotEmpty(r.Rects); // 図番行に対応するハイライト矩形が取れる
        Assert.All(r.Rects, rc => { Assert.InRange(rc.Left, 0, 100); Assert.InRange(rc.Top, 0, 100); });
    }

    [Fact]
    public async Task Build_UnknownSnippet_ReturnsImageWithoutRects()
    {
        var bytes = await File.ReadAllBytesAsync(TestDataPath("drawing-sample.pdf"));
        var r = await ShineosQA.Backend.SourcePreview.BuildAsync(bytes, "存在しないスニペットXYZ123", System.Threading.CancellationToken.None);
        Assert.NotNull(r);
        Assert.Empty(r.Rects); // 画像は返すがハイライトなし
    }
}
