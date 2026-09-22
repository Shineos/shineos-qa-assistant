using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>図面出典プレビュー: 元PDFの全ページ描画＋スニペット該当領域の図上矩形（複数ページ対応）</summary>
public class SourcePreviewTests
{
    private static string TestDataPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public async Task Build_RendersAllPagesAndHighlightsHitPage()
    {
        var bytes = await File.ReadAllBytesAsync(TestDataPath("drawing-sample.pdf"));
        var ex = ShineosQA.Backend.PdfText.ExtractWithLines(bytes);
        var line = ex.Lines.First(l => l.Text.Contains("ST-1042A"));
        var snip = ex.Text.Substring(line.Start, Math.Min(120, ex.Text.Length - line.Start));
        var r = await ShineosQA.Backend.SourcePreview.BuildAsync(bytes, snip, System.Threading.CancellationToken.None);
        Assert.NotNull(r);
        Assert.NotEmpty(r!.Pages);
        Assert.All(r.Pages, p => Assert.StartsWith("data:image/png;base64,", p.ImageDataUrl));
        // ハイライトページのみ矩形が付き、他ページは空
        var hit = r.Pages.First(p => p.Page == r.HitPage);
        Assert.NotEmpty(hit.Rects);
        Assert.All(r.Pages.Where(p => p.Page != r.HitPage), p => Assert.Empty(p.Rects));
    }

    [Fact]
    public async Task Build_UnknownSnippet_ReturnsPagesWithoutRects()
    {
        var bytes = await File.ReadAllBytesAsync(TestDataPath("drawing-sample.pdf"));
        var r = await ShineosQA.Backend.SourcePreview.BuildAsync(bytes, "存在しないスニペットXYZ123", System.Threading.CancellationToken.None);
        Assert.NotNull(r);
        Assert.All(r!.Pages, p => Assert.Empty(p.Rects));
    }
}
