using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ShineosQA.Backend;

/// <summary>画面キャプチャ・写真・スキャンPDFページのOCR（拡張パック・図面）。Windows内蔵エンジン（Windows.Media.Ocr）を使うため
/// 完全オフライン・モデル追加DLなし・CPUのみ（PowerToys Text Extractorと同じ経路）。
/// スキャン図面・スクショは回転していることが多いため 0/90/180/270°を試し読み方向スコアで最良を選ぶ
/// （実図面検証: 90°回転の組立図が文字化けしていた問題の根本対策）。
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

    /// <summary>OCR入力の上限辺長（エンジン実装値。取得できない環境は2600の保守値）。
    /// スキャンPDFページのレンダリング解像度上限としてIngestから参照する</summary>
    public static uint MaxImageDimension
    {
        get
        {
            try { var d = OcrEngine.MaxImageDimension; return d > 0 ? d : 2600u; }
            catch { return 2600u; }
        }
    }

    public sealed record OcrZuban(string Raw, string Norm);
    public sealed record OcrWordBox(string Text, double X, double Y, double W, double H);
    public sealed record OcrBest(string Text, BitmapRotation Rotation, List<OcrWordBox> Words);

    /// <summary>画像(PNG/JPEG)をOCRし、テキストと図番候補（正規形）を返す。
    /// タプルはJSON直列化で空オブジェクトになるためレコード型を使用（/api/ocr の契約: zubans[{raw,norm}]）</summary>
    public static async Task<(string Text, List<OcrZuban> Zubans)> RecognizeAsync(byte[] image)
    {
        var best = await RecognizeBestAsync(image);
        var zubans = new List<OcrZuban>();
        foreach (var m in DrawingIngest.ZubanRegex().Matches(best.Text).Cast<Match>())
        {
            var norm = Rag.NormalizeZuban(m.Value);
            if (norm.Length >= 2 && !zubans.Any(z => z.Norm == norm)) zubans.Add(new OcrZuban(m.Value, norm));
        }
        return (best.Text, zubans);
    }

    /// <summary>4方向でOCRし最良を返す（Wordsは最良方向の単語矩形・画像ピクセル座標・左上原点）。
    /// 一方向のデコード失敗は他方向で救済する</summary>
    public static async Task<OcrBest> RecognizeBestAsync(byte[] image)
    {
        if (!IsAvailable() || _engine is null)
            throw new InvalidOperationException("ja-JP OCR engine unavailable");
        using var ms = new MemoryStream(image);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var best = (Text: "", Words: new List<OcrWordBox>(), Score: -1.0, Rot: BitmapRotation.None);
        foreach (var rot in new[] { BitmapRotation.None, BitmapRotation.Clockwise90Degrees, BitmapRotation.Clockwise180Degrees, BitmapRotation.Clockwise270Degrees })
        {
            try
            {
                // 回転付きデコード: GetSoftwareBitmapAsync の transform 指定はプロジェクションで使えないため
                // GetPixelDataAsync（ScaledWidth/Height に元サイズ＝WinRTの回転仕様）→ SoftwareBitmap 生成
                var transform = new BitmapTransform
                {
                    Rotation = rot,
                    ScaledWidth = (uint)decoder.PixelWidth,
                    ScaledHeight = (uint)decoder.PixelHeight,
                };
                var provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                var pixels = provider.DetachPixelData();
                // 90°/270°は回転後の寸法が元の縦横入れ替えになる
                bool swapped = rot is BitmapRotation.Clockwise90Degrees or BitmapRotation.Clockwise270Degrees;
                int tw = swapped ? (int)decoder.PixelHeight : (int)decoder.PixelWidth;
                int th = swapped ? (int)decoder.PixelWidth : (int)decoder.PixelHeight;
                using var bmp = SoftwareBitmap.CreateCopyFromBuffer(
                    pixels.AsBuffer(), BitmapPixelFormat.Bgra8, tw, th, BitmapAlphaMode.Premultiplied);
                var result = await _engine.RecognizeAsync(bmp);
                var words = result.Lines.SelectMany(l => l.Words)
                    .Select(w => new OcrWordBox(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
                    .ToList();
                var text = result.Text ?? "";
                var score = DirectionScore(text);
                if (score > best.Score) best = (text, words, score, rot);
            }
            catch { /* この方向は諦める */ }
        }
        return new OcrBest(best.Text, best.Rot, best.Words);
    }

    /// <summary>読み方向の良さ = 文字量 × 平均語長²。正位置の文は語が長い一方、回転誤読は1文字語が並ぶだけで
    /// 語長が伸びない（実測: 回転図面のOCRは文字数は稼ぐが語長が短く化けた）ため二乗で利かせる</summary>
    public static double DirectionScore(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        var avg = words.Average(w => w.Length);
        return text.Replace(" ", "").Replace("　", "").Length * avg * avg;
    }
}
