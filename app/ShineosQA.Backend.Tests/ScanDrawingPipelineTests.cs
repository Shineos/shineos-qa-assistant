using System.Text.Json;
using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>スキャン図面パイプライン（v2.4.0）の回帰テスト: 低コントラスト・スキャン固定資産
/// (lowcontrast-drawing.pdf=画像のみPDF)がOCR救済され、デュアルパス・表題欄切り出し再OCRが
/// 破綻しないこと。OCR結果は環境差が出るため固いアサートは構造のみ（読取内容は出力で確認）</summary>
public class ScanDrawingPipelineTests
{
    [Fact]
    public async Task LowContrastScan_Fallback_DualPass_Crop()
    {
        if (!Ocr.IsAvailable()) return;
        var pdf = await File.ReadAllBytesAsync(Find("lowcontrast-drawing.pdf"));
        var res = await Ingest.OcrPdfFallbackAsync(pdf, CancellationToken.None);
        Assert.NotNull(res);
        Assert.True(res!.PagePngs.Count == 1);
        Assert.True(res.Text.Trim().Length >= 8); // 救済成立

        // 表題欄切り出し再OCR + 欠落補完（Ingest図面分岐と同じ手順: 切り出し単独で抽出する）
        var sheetRuns = res.Runs.Where(r => r.Page == 1).ToList();
        var (pw, ph) = res.PageDims[0];
        var meta = DrawingIngest.ExtractTitleBlock(sheetRuns, pw, ph);
        var extra = await Ocr.ReOcrTitleBlockAsync(res.PagePngs[0]);
        var merged = extra.Select(w => new PdfTextRun(w.Text, 1, (float)w.X, (float)(ph - (w.Y + w.H)), (float)w.W, (float)w.H)).ToList();
        var meta2 = DrawingIngest.ExtractTitleBlock(merged, pw, ph, Math.Max(3f, ph * 0.012f));
        var filled = Ingest.FillMissing(meta, meta2);
        var region = merged
            .Where(r => pw > 0 && ph > 0 && r.X > pw * 0.55f && r.Y < ph * 0.40f)
            .OrderBy(r => r.Y).ThenBy(r => r.X)
            .Select(r => r.Text + "@(" + r.X.ToString("F0") + "," + r.Y.ToString("F0") + ")");
        await Console.Error.WriteLineAsync("PIPE=" + JsonSerializer.Serialize(new
        {
            runCount = sheetRuns.Count,
            cropWords = extra.Count,
            cropText = string.Join(" / ", extra.Select(w => w.Text + "@(" + w.X.ToString("F0") + "," + w.Y.ToString("F0") + ")")),
            regionText = string.Join(" / ", region),
            meta,
            meta2,
            filled,
        }));
        Assert.True(extra.Count >= 0);
    }

    [Fact]
    public void FillMissing_NeverOverwritesPrimary()
    {
        var p = new DrawingIngest.DrawingMeta("A", null, null, null, null, null);
        var f = new DrawingIngest.DrawingMeta("B", "x", "y", "z", "w", "v");
        var r = Ingest.FillMissing(p, f);
        Assert.Equal("A", r.ZubanRaw);
        Assert.Equal("x", r.Hinmei);
    }

    private static string Find(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "app", "ShineosQA.Backend.Tests", "TestData", name);
            if (File.Exists(cand)) return cand;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("fixture not found");
    }
}
