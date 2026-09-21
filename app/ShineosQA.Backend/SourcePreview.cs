namespace ShineosQA.Backend;

/// <summary>出典プレビュー（図面）: 元PDFのシートページを画像化し、回答の根拠スニペットが
/// 現れている領域を図上の矩形（画像幅高の百分率）で返す。
/// 仕組み: チャンクテキストと同一の行組み立てでスニペットの文字位置を特定 → その行を構成する
/// 単語runの矩形を集約 → WinRT描画画像の解像度へ変換。テキスト層の無いスキャン図面は
/// 画像のみ（ハイライト矩形なし）、DXFは描画不可（UI側でテキストモーダルへフォールバック）</summary>
public static class SourcePreview
{
    public sealed record RectPct(double Left, double Top, double Width, double Height);

    public sealed record PreviewResult(string ImageDataUrl, int ImageWidth, int ImageHeight, List<RectPct> Rects, int Page);

    public static async Task<PreviewResult?> BuildAsync(byte[] pdfBytes, string snippet, CancellationToken ct)
    {
        var ex = PdfText.ExtractWithLines(pdfBytes);
        if (ex.Pages == 0 || ex.Lines.Count == 0) return null;

        // スニペットの全文中の位置を特定。チャンクは「【図面】…前置き\n＋本文」のため、
        // 見つからない場合は先頭行（前置き）を除外して再検索する
        int idx = ex.Text.IndexOf(snippet, StringComparison.Ordinal);
        int matchLen = snippet.Length;
        if (idx < 0)
        {
            var nl = snippet.IndexOf('\n');
            if (nl >= 0)
            {
                var rest = snippet[(nl + 1)..];
                idx = ex.Text.IndexOf(rest, StringComparison.Ordinal);
                matchLen = rest.Length;
            }
        }

        // 該当行（スニペット範囲と重なる行）を収集し、多数決でシートページを決める
        var hitLines = idx >= 0
            ? ex.Lines.Where(l => l.End > idx && l.Start < idx + matchLen).ToList()
            : new List<PdfLineInfo>();
        int page = hitLines.Count > 0
            ? hitLines.GroupBy(l => l.Page).OrderByDescending(g => g.Count()).First().Key
            : DrawingIngest.PickSheetPage(ex.Runs, ex.Pages);

        // シートページを WinRT（Windows.Data.Pdf）で描画。DocnetはスキャンCMYK-JPEGを黒つぶしするため使わない
        var (png, imgW, imgH) = await Ingest.RenderPageWinRtAsync(pdfBytes, page - 1, 1600, ct);

        var rects = new List<RectPct>();
        var hitPage = hitLines.Count > 0 ? hitLines[0].Page : page;
        // 該当ページの真の寸法（pt）で写像する（単語外接範囲は余白を含まないため使用しない）。
        // ページ1にテキストが無いPDF（表紙のみ画像等）でも該当ページの寸法を使えるようにページごとに保持
        var (tpw, tph) = hitPage >= 1 && hitPage <= ex.PageSizes.Count ? ex.PageSizes[hitPage - 1] : (0f, 0f);
        if (hitLines.Count > 0 && tpw > 0 && tph > 0)
        {
            double rx = imgW / tpw, ry = imgH / tph;
            var rectsPts = hitLines.SelectMany(l => l.Runs)
                .Select(r => (r.X, r.Y, r.W, r.H))
                .Distinct()
                .ToList();
            foreach (var (x, y, w, h) in rectsPts)
            {
                // PDF式（左下原点・pt）→ 画像（左上原点・px）→ 画像幅高の百分率
                var left = x * rx / imgW * 100.0;
                var top = (tph - (y + h)) * ry / imgH * 100.0;
                var wd = w * rx / imgW * 100.0;
                var ht = h * ry / imgH * 100.0;
                rects.Add(new RectPct(Math.Max(0, left - 0.3), Math.Max(0, top - 0.3), Math.Min(100, wd + 0.6), Math.Min(100, ht + 0.6)));
            }
        }

        return new PreviewResult("data:image/png;base64," + Convert.ToBase64String(png), imgW, imgH, rects, page);
    }
}
