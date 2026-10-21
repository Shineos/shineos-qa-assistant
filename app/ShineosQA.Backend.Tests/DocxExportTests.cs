using System.IO.Compression;
using System.Text;
using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>下書きのWord出力（v2.5.4）。生成bytesが妥当なdocx（OPC zip）で、
/// 想定したテキスト・見出しスタイルが word/document.xml に含まれることを検証する</summary>
public class DocxExportTests
{
    private static string DocumentXml(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidOperationException("word/document.xml missing");
        using var r = new StreamReader(entry.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    [Fact]
    public void MdToDocx_ProducesValidOpcWithContent()
    {
        var md = "# タイトル行\n\n*注記行です*\n\n## 第一章\n\n- 項目A\n- 項目B\n\n本文で**強調**があります。\n\n---\n参照: なし";
        var bytes = DocxExport.MdToDocx("テスト文書", md);
        Assert.True(bytes.Length > 1000);
        var xml = DocumentXml(bytes);
        Assert.Contains("テスト文書", xml);         // タイトル
        Assert.Contains("第一章", xml);             // 見出し2
        Assert.Contains("Heading2", xml);           // 見出しスタイル参照
        Assert.Contains("項目A", xml);              // 箇条書き
        Assert.Contains("•", xml);                  // 箇条書き行頭
        Assert.Contains("強調", xml);               // イライン強調
        Assert.Contains("注記行です", xml);          // 注記
        Assert.Contains("参照: なし", xml);
        Assert.DoesNotContain("# タイトル行", xml);  // md記法の残骸は無い
    }

    [Fact]
    public void MdToDocx_EmptyMarkdown_StillValid()
    {
        var bytes = DocxExport.MdToDocx("空", "");
        Assert.Contains("空", DocumentXml(bytes));
    }

    [Fact]
    public void SplitBold_Pairs()
    {
        var r = DocxExport.SplitBold("前**太字**後").ToList();
        Assert.Equal(3, r.Count);
        Assert.Equal(("前", false), r[0]);
        Assert.Equal(("太字", true), r[1]);
        Assert.Equal(("後", false), r[2]);
    }
}
