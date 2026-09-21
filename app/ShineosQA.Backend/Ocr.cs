using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ShineosQA.Backend;

/// <summary>画面キャプチャ・写真のOCR（拡張パック・図面）。Windows内蔵エンジン（Windows.Media.Ocr）を使うため
/// 完全オフライン・モデル追加DLなし・CPUのみ（PowerToys Text Extractorと同じ経路）。
/// ja-JP言語パックが無い環境では IsAvailable=false（機能無効＋ガイド表示でクラッシュしない）</summary>
public static class Ocr
{
    private static OcrEngine? _engine;
    private static bool _tried;

    public static bool IsAvailable()
    {
        if (!_tried)
        {
            _tried = true;
            try { _engine = OcrEngine.TryCreateFromLanguage(new Language("ja-JP")); }
            catch { _engine = null; }
        }
        return _engine is not null;
    }

    public sealed record OcrZuban(string Raw, string Norm);

    /// <summary>画像(PNG/JPEG)をOCRし、テキストと図番候補（正規形）を返す。
    /// タプルはJSON直列化で空オブジェクトになるためレコード型を使用（/api/ocr の契約: zubans[{raw,norm}]）</summary>
    public static async Task<(string Text, List<OcrZuban> Zubans)> RecognizeAsync(byte[] image)
    {
        if (!IsAvailable() || _engine is null)
            throw new InvalidOperationException("ja-JP OCR engine unavailable");
        using var ms = new MemoryStream(image);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var software = await decoder.GetSoftwareBitmapAsync();
        var result = await _engine.RecognizeAsync(software);
        var text = result.Text ?? "";
        var zubans = new List<OcrZuban>();
        foreach (var m in DrawingIngest.ZubanRegex().Matches(text).Cast<Match>())
        {
            var norm = Rag.NormalizeZuban(m.Value);
            if (norm.Length >= 2 && !zubans.Any(z => z.Norm == norm)) zubans.Add(new OcrZuban(m.Value, norm));
        }
        return (text, zubans);
    }
}
