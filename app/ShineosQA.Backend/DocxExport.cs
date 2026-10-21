using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ShineosQA.Backend;

/// <summary>下書きのMarkdownをWord(.docx)へ変換して出力する（v2.5.4）。
/// 対象は作成モードが自分で生成する制約付きMarkdown（# タイトル / ## 章 / - 箇条書き /
/// *注記行* / --- 区切り / 通常行）に限るため、汎用Markdownパーサではなく
/// 行単位の軽い変換。インラインの**強調**にも対応</summary>
public static class DocxExport
{
    public static byte[] MdToDocx(string title, string markdown)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            // 見出しスタイルの定義（空のDocumentにはstyles.xmlが無いため自前で定義する。
            // 無いとParagraphStyleId参照が解決されず平文になる）
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                HeadingStyle("Heading1", "見出し 1", "28", true),   // 14pt
                HeadingStyle("Heading2", "見出し 2", "22", true));  // 11pt
            var body = main.Document.Body!;

            // タイトル（見出し1スタイル）+ 文書プロパティ
            doc.PackageProperties!.Title = title;
            body.Append(Paragraph(title, Heading1));
            foreach (var rawLine in (markdown ?? "").Replace("\r", "").Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith("# ")) continue;                       // タイトル行は上で出済み
                if (line.StartsWith("## ")) body.Append(Paragraph(line[3..].Trim(), Heading2));
                else if (line.Trim() == "---") body.Append(new Paragraph(new Run(new Text(""))) { ParagraphProperties = new ParagraphProperties(new SpacingBetweenLines { After = "200" }) });
                else if (line.StartsWith("- ") || line.StartsWith("・")) body.Append(Bullet(Runs(line.StartsWith("・") ? line : line[2..])));
                else if (line.StartsWith("*") && line.EndsWith("*") && line.Length > 2) body.Append(Paragraph(Runs(line[1..^1]), note: true));
                else body.Append(Paragraph(Runs(line)));
            }
        }
        return ms.ToArray();
    }

    // ---- ヘルパ（DocumentFormat.OpenXmlの冗長さを畳む） ----

    /// <summary>最小の見出しスタイル定義（半値ドット単位のサイズ）</summary>
    private static Style HeadingStyle(string id, string name, string halfPt, bool bold)
    {
        var s = new Style(new StyleName { Val = name })
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            BasedOn = new BasedOn { Val = "Normal" },
            CustomStyle = true,
        };
        s.Append(new StyleRunProperties(
            new FontSize { Val = halfPt },
            bold ? new Bold() : new Italic()));
        return s;
    }

    private static ParagraphProperties Heading1 => new(new ParagraphStyleId { Val = "Heading1" }, new SpacingBetweenLines { After = "120" });
    private static ParagraphProperties Heading2 => new(new ParagraphStyleId { Val = "Heading2" }, new SpacingBetweenLines { Before = "160", After = "60" });

    private static Paragraph Paragraph(string text, ParagraphProperties props) =>
        new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })) { ParagraphProperties = props };

    private static Paragraph Paragraph(IEnumerable<Run> runs, bool note = false)
    {
        var p = new Paragraph();
        foreach (var r in runs)
        {
            if (note)
            {
                r.RunProperties ??= new RunProperties();
                r.RunProperties.Italic = new Italic();
                r.RunProperties.Color = new Color { Val = "666666" };
                r.RunProperties.FontSize = new FontSize { Val = "18" }; // 9pt
            }
            p.Append(r);
        }
        return p;
    }

    private static Paragraph Bullet(IEnumerable<Run> runs)
    {
        // 番号付け定義(numbering.xml)を必要とするNumberingPropertiesは使わず「• 」前置き+
        // ぶら下げインデントで表現（定義パート無しでも全環境で確実に描画される）
        var p = new Paragraph
        {
            ParagraphProperties = new ParagraphProperties(new Indentation { Left = "360", Hanging = "240" }),
        };
        p.Append(new Run(new Text("•  ") { Space = SpaceProcessingModeValues.Preserve }));
        foreach (var r in runs) p.Append(r);
        return p;
    }

    /// <summary>1行をインライン**強調**を考慮したRun列へ（*単体アスタリスクは無視）</summary>
    private static IEnumerable<Run> Runs(string text)
    {
        foreach (var (seg, bold) in SplitBold(text))
        {
            var run = new Run(new Text(seg) { Space = SpaceProcessingModeValues.Preserve });
            if (bold) run.RunProperties = new RunProperties(new Bold());
            yield return run;
        }
    }

    /// <summary>**太字**区切りの純関数（テスト対象）</summary>
    public static IEnumerable<(string Text, bool Bold)> SplitBold(string text)
    {
        var parts = (text ?? "").Split("**");
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                yield return (parts[i], i % 2 == 1);
    }
}
