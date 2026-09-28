using UglyToad.PdfPig.Content;
using UglyToad.PdfPig;

namespace ShineosQA.Backend;

/// <summary>PdfPigによる本格PDFテキスト抽出（ToUnicode CMap対応）。従来の自製抽出器は
/// CIDフォント（日本語PDFの大多数）で文字化け/失敗するため本体をこれに置き換えた
/// （mainからバックポート）。抽出が空の場合も例外を投げず空テキストを返す
/// （呼び出し側が自製抽出器へフォールバックする）。出典プレビュー用の座標・行情報は
/// v2.1.0には不要のため main の実装から省略している</summary>
public static class PdfText
{
    /// <summary>全文抽出。行は「単語をスペース結合＋末尾改行」で組み、全文は全ページの行を
    /// 改行で連結してTrimしたもの。単語を読み順（上→下・左→右、ベースラインYで±3ptの
    /// バケット）に並べる点は main と同じ</summary>
    public static string ExtractAll(byte[] pdf)
    {
        var full = new System.Text.StringBuilder();
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            var grouped = new List<List<Word>>();
            foreach (var w in page.GetWords().OrderBy(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
            {
                var line = grouped.LastOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < 3);
                if (line == null) { grouped.Add(new List<Word> { w }); }
                else line.Add(w);
            }
            foreach (var g in grouped)
            {
                foreach (var w in g.OrderBy(w => w.BoundingBox.Left))
                {
                    if (full.Length > 0 && full[^1] != '\n') full.Append(' ');
                    full.Append(w.Text);
                }
                full.Append('\n');
            }
        }
        return full.ToString().Trim();
    }
}
