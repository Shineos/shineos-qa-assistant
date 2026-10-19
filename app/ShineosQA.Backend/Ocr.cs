using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

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
            var r = await RecognizeRotatedAsync(decoder, rot);
            if (r is { } ok && ok.Score > best.Score) best = (ok.Text, ok.Words, ok.Score, rot);
        }
        return new OcrBest(best.Text, best.Rot, best.Words);
    }

    /// <summary>指定1方向のみOCR（デュアルパスの2パス目・表題欄再OCR用）。デコード失敗は例外</summary>
    public static async Task<OcrBest> RecognizeOneAsync(byte[] image, BitmapRotation rot)
    {
        if (!IsAvailable() || _engine is null)
            throw new InvalidOperationException("ja-JP OCR engine unavailable");
        using var ms = new MemoryStream(image);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var r = await RecognizeRotatedAsync(decoder, rot) ?? throw new InvalidOperationException("ocr failed");
        return new OcrBest(r.Text, rot, r.Words);
    }

    private static async Task<(string Text, List<OcrWordBox> Words, double Score)?> RecognizeRotatedAsync(BitmapDecoder decoder, BitmapRotation rot)
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
            var result = await _engine!.RecognizeAsync(bmp);
            var words = result.Lines.SelectMany(l => l.Words)
                .Select(w => new OcrWordBox(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
                .ToList();
            var text = result.Text ?? "";
            return (text, words, DirectionScore(text));
        }
        catch { return null; }
    }

    /// <summary>適応的前処理付きOCR（スキャン図面の実戦強化・v2.4.0）。4方向の通常OCRの後、勝ち方向に限り
    /// 「グレースケール+コントラスト伸張」した画像でも1回だけOCRし、DirectionScoreが1割以上良ければ採用。
    /// 薄いスキャン・低コントラストの紙図面で文字が立つ一方、クリアな画像では前処理版が上回らないため
    /// 固定前処理で撤回した退回帰（CHANGELOG 2026-10-05: クリア画像のキーワード一致4→2）が構造的に起きない。
    /// 前処理・再OCRのいずれかが失敗しても通常結果をそのまま返す（取り込みを止めない）</summary>
    public static async Task<OcrBest> RecognizeBestWithPreprocessAsync(byte[] image)
    {
        var plain = await RecognizeBestAsync(image);
        byte[] pre;
        try { pre = await PreprocessForOcrAsync(image); }
        catch { return plain; }
        try
        {
            var enhanced = await RecognizeOneAsync(pre, plain.Rotation);
            return PickBetter(plain, enhanced);
        }
        catch { return plain; }
    }

    /// <summary>純関数: そのままの結果(A)と前処理結果(B)の良い方。Bが「Aの1.1倍かつ絶対下限10」を
    /// 上回る時だけB。同点・僅差は常にA（既知の良い結果を優先）、白紙ページの微小ノイズ（スコア1程度）
    /// も跳ね除ける。テストから直接検証できる</summary>
    public static OcrBest PickBetter(OcrBest plain, OcrBest enhanced) =>
        DirectionScore(enhanced.Text) > Math.Max(DirectionScore(plain.Text) * 1.1, 10) ? enhanced : plain;

    /// <summary>OCR前処理: グレースケール化+輝度ヒストグラム両端1%を基準にしたコントラスト伸張。
    /// 全体がほぼ単色（伸張余地なし）の場合は例外（呼び出し側で通常結果を使用）</summary>
    public static async Task<byte[]> PreprocessForOcrAsync(byte[] image)
    {
        using var ms = new MemoryStream(image);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = (uint)decoder.PixelWidth, ScaledHeight = (uint)decoder.PixelHeight },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var pixels = provider.DetachPixelData();
        int n = pixels.Length / 4;
        if (n == 0) throw new InvalidOperationException("empty image");
        var hist = new int[256];
        var gray = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * 4; // BGRA
            byte y = (byte)((pixels[o + 2] * 299 + pixels[o + 1] * 587 + pixels[o] * 114) / 1000);
            gray[i] = y;
            hist[y]++;
        }
        // 両端1%点（極端な黒枠・白余白の影響を除く）
        int ymin = 0, ymax = 255;
        long cut = Math.Max(1, n / 100), cum = 0;
        for (int v = 0; v < 256; v++) { cum += hist[v]; if (cum >= cut) { ymin = v; break; } }
        cum = 0;
        for (int v = 255; v >= 0; v--) { cum += hist[v]; if (cum >= cut) { ymax = v; break; } }
        if (ymax - ymin < 20) throw new InvalidOperationException("no contrast to stretch");
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            byte v = (byte)Math.Clamp((gray[i] - ymin) * 255 / (ymax - ymin), 0, 255);
            pixels[o] = v; pixels[o + 1] = v; pixels[o + 2] = v; // Aはそのまま
        }
        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)decoder.PixelWidth, (uint)decoder.PixelHeight, 96.0, 96.0, pixels);
        await encoder.FlushAsync();
        var size = (uint)outStream.Size;
        using var reader = new DataReader(outStream.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        var outBytes = new byte[size];
        reader.ReadBytes(outBytes);
        return outBytes;
    }

    /// <summary>表題欄（JIS慣行の右下領域）を切り出して再OCRする（v2.4.0）。2600pxの全景OCRでは
    /// 小さな表題欄文字の認識が漏れることがあるため、右下45%×55%以降を切り出してOCRに渡し
    /// 実効解像度を上げる。戻り値は全景ピクセル座標（左上原点）へ写像した単語矩形。
    /// 切り出し・OCRに失敗した場合は空リスト（全景OCRの結果をそのまま使う）</summary>
    public static async Task<List<OcrWordBox>> ReOcrTitleBlockAsync(byte[] pagePng)
    {
        var words = new List<OcrWordBox>();
        if (!IsAvailable() || _engine is null) return words;
        try
        {
            using var ms = new MemoryStream(pagePng);
            var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
            if (w < 200 || h < 200) return words;
            var bounds = new BitmapBounds
            {
                X = (uint)(w * 0.45),
                Y = (uint)(h * 0.55),
                Width = (uint)(w - w * 0.45),
                Height = (uint)(h - h * 0.55),
            };
            // ※Bounds使用時はScaledWidth/Heightを設定しない（両立させると
            //   「Value does not fall within the expected range」で拒否される・実測）
            var transform = new BitmapTransform
            {
                Bounds = bounds,
                Rotation = BitmapRotation.None,
            };
            var provider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = provider.DetachPixelData();
            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(
                pixels.AsBuffer(), BitmapPixelFormat.Bgra8, (int)bounds.Width, (int)bounds.Height, BitmapAlphaMode.Premultiplied);
            var result = await _engine.RecognizeAsync(bmp);
            foreach (var word in result.Lines.SelectMany(l => l.Words))
                words.Add(new OcrWordBox(word.Text,
                    bounds.X + word.BoundingRect.X, bounds.Y + word.BoundingRect.Y,
                    word.BoundingRect.Width, word.BoundingRect.Height));
        }
        catch { /* 切り出し失敗時は全景OCRの結果のみで続行 */ }
        return words;
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
