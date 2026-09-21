using System.Text;

namespace ShineosQA.Backend;

/// <summary>ASCII DXF（CAD図面の交換形式）からのテキスト抽出。拡張パック（図面）で
/// 「PDFに変換しないでもCADファイルをそのまま検索したい」需要に応える最小実装。
/// TEXT / MTEXT / ATTRIB / ATTDEF エンティティの文字列と挿入点(X,Y)を読み取り、
/// 表題欄抽出（DrawingIngest）にそのまま渡せる PDFStyle の座標付きrunへ変換する。
/// DWG（バイナリ・クローズド形式）は対象外: 取り込み時にDXF/PDF形式での再エクスポートを案内する</summary>
public static class DxfText
{
    static DxfText() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); // shift_jis用

    public sealed record DxfRun(string Text, double X, double Y, double H)
    {
        /// <summary>推定幅（文字数×字高×0.6）。表題欄の領域判定（X比率）に使う精度で十分</summary>
        public double EstW => Text.Length * H * 0.6;
    }

    public sealed record DxfResult(string Text, List<DxfRun> Runs);

    /// <summary>グループコード/値の行ペア列を走査し、文字系エンティティを収集する。
    /// DXFのY軸は上向き正 → PDF式（左下原点）と同じ向きなので座標変換は不要</summary>
    public static DxfResult Extract(byte[] dxf)
    {
        string content;
        try { content = new UTF8Encoding(false, true).GetString(dxf); }
        catch (DecoderFallbackException) { content = Encoding.GetEncoding("shift_jis").GetString(dxf); } // 日本語DXFの実勢

        var lines = content.Split('\n');
        string Get(int i) => i < lines.Length ? lines[i].TrimEnd('\r').Trim() : "";

        var runs = new List<DxfRun>();
        string entity = "";              // 現在のエンティティ型（TEXT/MTEXT/ATTRIB/ATTDEF）
        var parts = new List<string>();  // MTEXTの継続(3)→本体(1)の連結用
        double x = 0, y = 0, h = 2.5;

        void Flush()
        {
            var joined = string.Join("", parts).Trim();
            if (entity is "TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF" && joined.Length > 0)
                runs.Add(new DxfRun(joined, x, y, h));
            parts.Clear(); entity = "";
        }

        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            var code = Get(i);
            var value = Get(i + 1);
            if (code.Length == 0 || !int.TryParse(code, out var g)) continue; // 奇数行ずれ等はスキップ
            switch (g)
            {
                case 0:
                    Flush();
                    entity = value;
                    break;
                case 3 when entity == "MTEXT":
                    parts.Add(value); // MTEXT継続行（本体1の前に並ぶ）
                    break;
                case 1:
                    parts.Add(value);
                    break;
                case 10 when entity is "TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF":
                    _ = double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out x);
                    break;
                case 20 when entity is "TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF":
                    _ = double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out y);
                    break;
                case 40 when entity is "TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF":
                    _ = double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out h);
                    if (h <= 0) h = 2.5;
                    break;
            }
        }
        Flush();

        // 表示順（上→下・左→右。Yは上向き正なので降順）に並べてチャンク本文を組む
        var ordered = runs.OrderByDescending(r => Math.Round(r.Y)).ThenBy(r => r.X).ToList();
        var text = string.Join('\n', ordered.Select(r => r.Text));
        return new DxfResult(text, ordered);
    }

    /// <summary>DXFのrunを表題欄抽出（DrawingIngest）に渡す PDF式runへ変換（1ページ扱い）</summary>
    public static List<PdfTextRun> ToPdfRuns(IEnumerable<DxfRun> runs) =>
        runs.Select(r => new PdfTextRun(r.Text, 1, (float)r.X, (float)r.Y, (float)r.EstW, (float)r.H)).ToList();
}
