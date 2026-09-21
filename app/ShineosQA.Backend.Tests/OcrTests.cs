using System.Runtime.InteropServices.WindowsRuntime;
using Xunit;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ShineosQA.Backend.Tests;

/// <summary>T8スパイク: Windows内蔵OCR（Windows.Media.Ocr）が自己完結publish環境で動くことの実証。
/// ja-JP言語パック未導入環境では IsAvailable=false のため検証をスキップする（機能無効化経路の設計どおり）。
/// 回転対応・画像のみPDF救済は実図面検証（90°回転の組立図が文字化け／スキャン図面が取り込み不可）の根本対策</summary>
public class OcrTests
{
    private static string TestDataPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    private static async Task<byte[]> ReadTestDataAsync(string name) =>
        await File.ReadAllBytesAsync(TestDataPath(name));

    [Fact]
    public void IsAvailable_DoesNotThrow()
    {
        // この呼び出しがtrue/falseどちらでも、例外なく判定できることが最低条件
        _ = Ocr.IsAvailable();
    }

    [Fact]
    public async Task Recognize_Screenshot_FindsZuban()
    {
        if (!Ocr.IsAvailable()) return; // 言語パック未導入環境: 機能無効化経路はE2Eで確認
        var png = await ReadTestDataAsync("ocr-sample.png");
        var (text, zubans) = await Ocr.RecognizeAsync(png);
        Assert.Contains("1042", text);       // OCRが数字を読めていること
        Assert.Contains(zubans, z => z.Norm == "st1042a"); // 図番候補の正規形が取れること
    }

    [Fact]
    public async Task Recognize_Rotated90_StillReadsText()
    {
        if (!Ocr.IsAvailable()) return;
        var rotated = await RotatePng90Async(await ReadTestDataAsync("ocr-sample.png"));
        var (text, _) = await Ocr.RecognizeAsync(rotated);
        Assert.Contains("1042", text); // 90°回転画像でも4方向最良選択で読めること
    }

    [Fact]
    public async Task OcrPdfFallback_ImageOnlyPdf_ExtractsText()
    {
        if (!Ocr.IsAvailable()) return;
        // 画像のみPDF（スキャン相当）を実行時に組んで救済経路を検証する
        var jpeg = PngToJpeg(await ReadTestDataAsync("ocr-sample.png"), out var w, out var h);
        var pdf = BuildImageOnlyPdf(jpeg, w, h);
        var res = await Ingest.OcrPdfFallbackAsync(pdf, CancellationToken.None);
        Assert.NotNull(res);
        Assert.Equal(1, res!.Pages);
        Assert.Contains("1042", res.Text);
        Assert.NotEmpty(res.Runs); // 単語矩形（表題欄抽出の入力）が取れること
    }

    [Fact]
    public async Task OcrPdfFallback_BlankPagePdf_ReturnsNull()
    {
        if (!Ocr.IsAvailable()) return;
        // no-text.pdf（青背景のみ）: OCRしても読むものが無い → 救済不能としてnull（呼び出し側で誠実なエラー）
        var bytes = await ReadTestDataAsync("no-text.pdf");
        var res = await Ingest.OcrPdfFallbackAsync(bytes, CancellationToken.None);
        Assert.Null(res);
    }

    // ---- ヘルパー: WinRTでの画像変換と最小PDFビルダ ----

    private static async Task<byte[]> RotatePng90Async(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var transform = new BitmapTransform
        {
            Rotation = BitmapRotation.Clockwise90Degrees,
            ScaledWidth = (uint)decoder.PixelWidth,
            ScaledHeight = (uint)decoder.PixelHeight,
        };
        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var pixels = provider.DetachPixelData();
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            (int)decoder.PixelHeight, (int)decoder.PixelWidth, BitmapAlphaMode.Premultiplied);
        return await EncodeAsync(bmp, BitmapEncoder.PngEncoderId);
    }

    private static byte[] PngToJpeg(byte[] png, out int width, out int height)
    {
        using var ms = new MemoryStream(png);
        var decoder = BitmapDecoder.CreateAsync(ms.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
        width = (int)decoder.PixelWidth;
        height = (int)decoder.PixelHeight;
        using var soft = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
        return EncodeAsync(soft, BitmapEncoder.JpegEncoderId).GetAwaiter().GetResult();
    }

    private static async Task<byte[]> EncodeAsync(SoftwareBitmap bmp, Guid encoderId)
    {
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetSoftwareBitmap(bmp);
        await encoder.FlushAsync();
        var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>JPEG(DCTDecode)を1ページに埋め込んだ最小PDFをコードで組む（テキスト層なし=スキャン相当）</summary>
    private static byte[] BuildImageOnlyPdf(byte[] jpeg, int w, int h)
    {
        var parts = new List<byte[]>();
        var offsets = new List<int>();
        int pos = 0;
        void Add(string s) { var b = System.Text.Encoding.Latin1.GetBytes(s); parts.Add(b); pos += b.Length; }
        void AddObj(int num, string body) { offsets.Add(pos); Add($"{num} 0 obj{body}endobj\n"); }

        Add("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        AddObj(1, "<</Type/Catalog/Pages 2 0 R>>");
        AddObj(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        AddObj(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {w} {h}]/Resources<</XObject<</Im0 4 0 R>>/ProcSet[/PDF /Image]>>/Contents 5 0 R>>");
        offsets.Add(pos);
        Add($"4 0 obj<</Type/XObject/Subtype/Image/Width {w}/Height {h}/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/DCTDecode/Length {jpeg.Length}>>stream\n");
        parts.Add(jpeg); pos += jpeg.Length;
        Add("\nendstream\nendobj\n");
        var content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";
        AddObj(5, $"<</Length {content.Length}>>stream\n{content}\nendstream\nendobj\n");

        int xrefPos = pos;
        var sb = new System.Text.StringBuilder();
        sb.Append("xref\n0 6\n0000000000 65535 f \r\n");
        foreach (var off in offsets) sb.Append($"{off:D10} 00000 n \r\n");
        sb.Append($"trailer<</Size 6/Root 1 0 R>>\nstartxref\n{xrefPos}\n%%EOF\n");
        Add(sb.ToString());
        return parts.SelectMany(p => p).ToArray();
    }
}
