using UglyToad.PdfPig.Content;
using UglyToad.PdfPig;

namespace ShineosQA.Backend;

/// <summary>PDFテキスト抽出の結果。Runsは単語単位（原点左下・pt）。W/Hはページ1の寸法</summary>
public sealed record PdfExtractResult(string Text, int Pages, List<PdfTextRun> Runs, float PageWidth, float PageHeight);

public sealed record PdfTextRun(string Text, int Page, float X, float Y, float W, float H);

/// <summary>PdfPigによる本格PDFテキスト抽出（ToUnicode CMap対応）。従来の自製抽出器は
/// CIDフォント（日本語PDFの大多数）で文字化け/失敗するため、T1で本体をこれに置き換えた。
/// ExtractTextはフォールバック判定のため、抽出が空の場合も例外を投げず空テキストを返す</summary>
public static class PdfText
{
    public static PdfExtractResult ExtractAll(byte[] pdf)
    {
        var runs = new List<PdfTextRun>();
        var pageTexts = new List<string>();
        float pw = 0, ph = 0;
        int pages = 0;
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            pages++;
            // ページ寸法は単語座標の最大値から求める（PageSize型のバージョン差異を避ける。
            // 表題欄の領域判定に使う程度の精度で十分: 端の余白分だけ内側に寄る）
            var words = page.GetWords().ToList();
            if (pages == 1 && words.Count > 0)
            {
                pw = (float)words.Max(w => w.BoundingBox.Right);
                ph = (float)words.Max(w => w.BoundingBox.Top);
            }
            // 単語を行（ベースラインYで±3ptのバケット）にまとめ、読み順（上→下・左→右）で組む
            var lines = new List<List<Word>>();
            foreach (var w in words.OrderBy(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
            {
                var line = lines.LastOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < 3);
                if (line == null) { lines.Add(new List<Word> { w }); }
                else line.Add(w);
            }
            var sb = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                foreach (var w in line.OrderBy(w => w.BoundingBox.Left))
                {
                    if (sb.Length > 0 && sb[^1] != '\n') sb.Append(' ');
                    sb.Append(w.Text);
                    runs.Add(new PdfTextRun(w.Text, page.Number,
                        (float)w.BoundingBox.Left, (float)w.BoundingBox.Bottom,
                        (float)(w.BoundingBox.Right - w.BoundingBox.Left),
                        (float)(w.BoundingBox.Top - w.BoundingBox.Bottom)));
                }
                sb.Append('\n');
            }
            pageTexts.Add(sb.ToString());
        }
        return new PdfExtractResult(string.Join('\n', pageTexts).Trim(), pages, runs, pw, ph);
    }
}
