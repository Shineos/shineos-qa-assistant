using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>T8スパイク: Windows内蔵OCR（Windows.Media.Ocr）が自己完結publish環境で動くことの実証。
/// ja-JP言語パック未導入環境では IsAvailable=false のため検証をスキップする（機能無効化経路の設計どおり）</summary>
public class OcrTests
{
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
        var png = await System.IO.File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "ocr-sample.png"));
        var (text, zubans) = await Ocr.RecognizeAsync(png);
        Assert.Contains("1042", text);       // OCRが数字を読めていること
        Assert.Contains(zubans, z => z.Norm == "st1042a"); // 図番候補の正規形が取れること
    }
}
