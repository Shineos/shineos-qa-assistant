using UglyToad.PdfPig.Content;
using UglyToad.PdfPig;

namespace ShineosQA.Backend;

/// <summary>PDFテキスト抽出の結果。Runsは単語単位（原点左下・pt）。W/Hはページ1の寸法</summary>
public sealed record PdfExtractResult(string Text, int Pages, List<PdfTextRun> Runs, float PageWidth, float PageHeight);

public sealed record PdfTextRun(string Text, int Page, float X, float Y, float W, float H);

/// <summary>組み立て後の1行と、その行を構成する単語run（出典プレビューの図上ハイライト用）。
/// Start/Endは全文テキスト（Trim後）中の文字位置</summary>
public sealed record PdfLineInfo(int Page, string Text, List<PdfTextRun> Runs, int Start, int End);

/// <summary>抽出の詳細結果。PageSizesはページごとの真の寸法（pt・表題欄の図上ハイライト変換に使用）</summary>
public sealed record PdfExtractFull(
    string Text, List<PdfLineInfo> Lines, List<PdfTextRun> Runs, int Pages,
    List<(float W, float H)> PageSizes, float PageWidth, float PageHeight);

/// <summary>PdfPigによる本格PDFテキスト抽出（ToUnicode CMap対応）。従来の自製抽出器は
/// CIDフォント（日本語PDFの大多数）で文字化け/失敗するため、T1で本体をこれに置き換えた。
/// ExtractTextはフォールバック判定のため、抽出が空の場合も例外を投げず空テキストを返す</summary>
public static class PdfText
{
    /// <summary>抽出の共通実装。行は「単語をスペース結合＋末尾改行」で組み、全文は全ページの行を
    /// 改行で連結してTrimしたもの。行情報（行テキスト・構成runs・全文中の文字位置）と
    /// ページ寸法（ページごと）は出典プレビューの図上ハイライトに使う。
    /// 注意: ページ寸法は単語座標の最大値（PageSize型のバージョン差異を避けるため）。
    /// ページ1にテキストが無い場合（表紙のみ画像等）は0になるため、
    /// 図上ハイライトでは該当ページの寸法を別途使う</summary>
    public static PdfExtractFull ExtractWithLines(byte[] pdf)
    {
        var runs = new List<PdfTextRun>();
        var lines = new List<PdfLineInfo>();
        var pageSizes = new List<(float W, float H)>();
        var full = new System.Text.StringBuilder();
        float pw = 0, ph = 0;
        int pages = 0;
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            pages++;
            var words = page.GetWords().ToList();
            // ページの真の寸法（pt）を優先。図上ハイライトは「ページ原点基準の単語座標」を
            // ページ全体の描画画像へ写像するため、単語外接範囲（余白を含まない）ではずれる
            float wpw, wph;
            try { wpw = (float)page.Width; wph = (float)page.Height; }
            catch
            {
                // ページ寸法APIが取得できない環境では単語座標の最大値で代替（端の余白分ずれる）
                wpw = words.Count > 0 ? (float)words.Max(w => w.BoundingBox.Right) : 0;
                wph = words.Count > 0 ? (float)words.Max(w => w.BoundingBox.Top) : 0;
            }
            pageSizes.Add((wpw, wph));
            if (pages == 1) { pw = wpw; ph = wph; }
            // 単語を行（ベースラインYで±3ptのバケット）にまとめ、読み順（上→下・左→右）で組む
            var grouped = new List<List<Word>>();
            foreach (var w in words.OrderBy(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
            {
                var line = grouped.LastOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < 3);
                if (line == null) { grouped.Add(new List<Word> { w }); }
                else line.Add(w);
            }
            foreach (var g in grouped)
            {
                int lineStart = full.Length;
                var lineRuns = new List<PdfTextRun>();
                foreach (var w in g.OrderBy(w => w.BoundingBox.Left))
                {
                    if (full.Length > 0 && full[^1] != '\n') full.Append(' ');
                    full.Append(w.Text);
                    var run = new PdfTextRun(w.Text, page.Number,
                        (float)w.BoundingBox.Left, (float)w.BoundingBox.Bottom,
                        (float)(w.BoundingBox.Right - w.BoundingBox.Left),
                        (float)(w.BoundingBox.Top - w.BoundingBox.Bottom));
                    runs.Add(run);
                    lineRuns.Add(run);
                }
                full.Append('\n');
                var lineText = full.ToString(lineStart, full.Length - lineStart).TrimEnd('\n');
                lines.Add(new PdfLineInfo(page.Number, lineText, lineRuns, lineStart, lineStart + lineText.Length));
            }
        }
        var text = full.ToString().Trim();
        int leading = full.Length - full.ToString().TrimStart().Length;
        var adjusted = lines.ConvertAll(l => l with
        {
            Start = Math.Max(0, l.Start - leading),
            End = Math.Min(text.Length, l.End - leading),
        });
        return new PdfExtractFull(text, adjusted, runs, pages, pageSizes, pw, ph);
    }

    public static PdfExtractResult ExtractAll(byte[] pdf)
    {
        var r = ExtractWithLines(pdf);
        return new PdfExtractResult(r.Text, r.Pages, r.Runs, r.PageWidth, r.PageHeight);
    }
}
