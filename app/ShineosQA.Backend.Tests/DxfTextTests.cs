using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>DXF（CAD図面・ASCII形式）からのテキスト抽出。TEXT/MTEXTエンティティと挿入点の読取、
/// 表題欄抽出（DrawingIngest）への接続を検証する</summary>
public class DxfTextTests
{
    private static string Pairs(params (string Code, string Val)[] ps)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (code, val) in ps) { sb.AppendLine(code); sb.AppendLine(val); }
        return sb.ToString();
    }

    private static byte[] Dxf()
    {
        var dxf = Pairs(
            ("0", "SECTION"), ("2", "HEADER"),
            ("9", "$ACADVER"), ("1", "AC1021"),
            ("0", "ENDSEC"),
            ("0", "SECTION"), ("2", "ENTITIES"))
            // 寸法テキスト（左下エリア）
            + Pairs(("0", "MTEXT"), ("8", "DIM"), ("10", "50.0"), ("20", "120.0"), ("40", "3.5"), ("1", "Ø160"))
            + Pairs(("0", "TEXT"), ("8", "DIM"), ("10", "30.0"), ("20", "60.0"), ("40", "3.5"), ("1", "Rc1/16"))
            // 表題欄（右下エリア: X比率>0.55 / Y比率<0.40。DXFのYは上向き正）
            + Pairs(("0", "TEXT"), ("8", "TITLE"), ("10", "150.0"), ("20", "30.0"), ("40", "5.0"), ("1", "図番 ST-1042A"))
            + Pairs(("0", "TEXT"), ("8", "TITLE"), ("10", "150.0"), ("20", "20.0"), ("40", "5.0"), ("1", "品名 サポートブラケット"))
            + Pairs(("0", "TEXT"), ("8", "TITLE"), ("10", "150.0"), ("20", "10.0"), ("40", "5.0"), ("1", "材質 SS400"))
            + Pairs(
            ("0", "ENDSEC"),
            ("0", "EOF"));
        return System.Text.Encoding.UTF8.GetBytes(dxf);
    }

    [Fact]
    public void Extract_ReadsTextEntitiesWithPositions()
    {
        var r = DxfText.Extract(Dxf());
        Assert.Equal(5, r.Runs.Count);
        Assert.Contains(r.Runs, x => x.Text.Contains("ST-1042A") && x.X > 100 && x.Y < 50);
        Assert.Contains(r.Runs, x => x.Text == "Ø160");
        Assert.Contains("サポートブラケット", r.Text);
    }

    [Fact]
    public void TitleBlock_ExtractsFromDxfRuns()
    {
        var r = DxfText.Extract(Dxf());
        var runs = DxfText.ToPdfRuns(r.Runs);
        var (w, h) = DrawingIngest.PageDims(runs, 1);
        Assert.True(w > 0 && h > 0);
        var meta = DrawingIngest.ExtractTitleBlock(runs, w, h);
        Assert.Equal("ST-1042A", meta.ZubanRaw);      // ルール抽出で図番が取れる（DXFはテキスト層が確実）
        Assert.Equal("サポートブラケット", meta.Hinmei);
        Assert.Equal("SS400", meta.Zairyo);
    }

    /// <summary>29.dxf実測（2026-11-01）: 表題欄領域（X&gt;55%・Y&lt;40%）からラベルTEXTが漏れる境界ケース。
    /// ①領域外の「Material SS400」で材質が取れず「材質は登録なし」になった ②領域内のゆるい一致が
    /// 規格参照「JIS B 0405」を図番と誤認 ③「REV B」の頭文字「V」を改訂と誤認 —— の3点の回帰防止</summary>
    [Fact]
    public void TitleBlock_OutOfRegionLabels_FilledAndZubanOverride()
    {
        // ページ: 幅150×高さ40。領域は X>82.5・Y<16 → 領域内は (150,10) と (150,0) のみ
        var runs = new List<PdfTextRun>
        {
            new("Zuban ST-1042A", 1, 150f, 30f, 30f, 4f),          // 領域外（Y=30）
            new("Material SS400", 1, 150f, 20f, 30f, 4f),          // 領域外（Y=20）
            new("TITLE サポートブラケット", 1, 150f, 40f, 40f, 4f), // 領域外（Y=40）
            new("REV B", 1, 150f, 10f, 12f, 4f),                   // 領域内
            new("Note 公差は JIS B 0405 による", 1, 150f, 0f, 45f, 4f), // 領域内（ゆるい一致の誤認源）
        };
        var meta = DrawingIngest.ExtractTitleBlock(runs, 150f, 40f);
        Assert.Equal("ST-1042A", meta.ZubanRaw);        // JISB0405（規格参照の誤認）ではなくラベル付きを採用
        Assert.Equal("サポートブラケット", meta.Hinmei);  // 領域外のTITLEラベルから補完
        Assert.Equal("SS400", meta.Zairyo);             // 領域外のMATERIALラベルから補完
        Assert.Equal("B", meta.Revision);               // 「REV B」→「V」誤認の修正
    }

    [Fact]
    public void Dwg_IsRejectedWithGuidance()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => Ingest.ExtractText("part.dwg", new MemoryStream(new byte[] { 1 })));
        Assert.Contains("DXF", ex.Message); // DXFエクスポートの案内を含む
    }

    [Fact]
    public void SupportedExtensions_IncludesDxf()
    {
        Assert.Contains(".dxf", Ingest.SupportedExtensions);
        Assert.DoesNotContain(".dwg", Ingest.SupportedExtensions);
    }
}
