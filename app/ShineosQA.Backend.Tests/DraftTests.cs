using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>下書き作成（v2.5.0）の純関数群: テンプレート自動判定・アウトライン防御パース・
/// 参照公平cap・組み立て</summary>
public class DraftTests
{
    // ---- InferTemplate ----

    [Theory]
    [InlineData("PCの貸出の手順書を作成して", "procedure")]
    [InlineData("新しい手順を作りたい", "procedure")]
    [InlineData("今月の作業報告書をお願い", "report")]
    [InlineData("日報の下書き", "report")]
    [InlineData("新サービスの提案書を作成", "proposal")]
    [InlineData("FAQ集にまとめて", "faq")]
    [InlineData("よくある質問をまとめて", "faq")]
    [InlineData("何かいい感じの文書を", "free")]
    [InlineData("", "free")]
    public void InferTemplate_ByKeyword(string instruction, string expected)
    {
        Assert.Equal(expected, Draft.InferTemplate(instruction));
    }

    [Fact]
    public void InferTemplate_FaqCaseSensitive_Known()
    {
        Assert.Equal("faq", Draft.InferTemplate("FAQに追記したい"));
    }

    // ---- ParseOutline ----

    [Fact]
    public void ParseOutline_ValidJson_UsesSections()
    {
        var json = "{\"title\":\"PC貸出手順\",\"sections\":[{\"title\":\"準備\",\"goal\":\"前提\"},{\"title\":\"手順\",\"goal\":\"流れ\"},{\"title\":\"確認\",\"goal\":\"チェック\"}]}";
        var r = Draft.ParseOutline(json, Draft.Templates["procedure"], "fallback");
        Assert.Equal("PC貸出手順", r.Title);
        Assert.Equal(3, r.Sections.Count);
        Assert.Equal("準備", r.Sections[0].Title);
    }

    [Fact]
    public void ParseOutline_Garbage_FallsBack()
    {
        var r = Draft.ParseOutline("これはJSONではない出力です", Draft.Templates["faq"], "指示文です");
        Assert.Equal(3, r.Sections.Count); // フォールバック章立て
        Assert.Equal("指示文です", r.Title);
    }

    [Fact]
    public void ParseOutline_TooFewSections_FallsBack()
    {
        var json = "{\"title\":\"t\",\"sections\":[{\"title\":\"ひとつだけ\"}]}";
        var r = Draft.ParseOutline(json, Draft.Templates["report"], "x");
        Assert.Equal(3, r.Sections.Count);
    }

    [Fact]
    public void ParseOutline_EmptySections_MeansNoIntent()
    {
        var json = "{\"title\":\"\",\"sections\":[]}";
        var r = Draft.ParseOutline(json, Draft.Templates["free"], "指示");
        Assert.Empty(r.Sections);
    }

    [Fact]
    public void ParseOutline_JsonInProse_Extracted()
    {
        var json = "章立ては以下です。\n{\"title\":\"T\",\"sections\":[{\"title\":\"A\",\"goal\":\"\"},{\"title\":\"B\",\"goal\":\"\"}]}";
        var r = Draft.ParseOutline(json, Draft.Templates["free"], "x");
        Assert.Equal(2, r.Sections.Count);
    }

    // ---- BuildReferenceText ----

    [Fact]
    public void BuildReferenceText_FairCapPerFile()
    {
        var long1 = new string('あ', 3000);
        var long2 = new string('い', 3000);
        var r = Draft.BuildReferenceText(new List<(string, string)> { ("a.md", long1), ("b.md", long2) }, null, 2400);
        // ファイルあたり 2400/2=1200字 cap。全体は大幅に2400字未満に収まる
        Assert.Contains("【参照文書: a.md】", r);
        Assert.Contains("【参照文書: b.md】", r);
        Assert.True(r.Length < 3000);
    }

    [Fact]
    public void BuildReferenceText_SeedClipped()
    {
        var q = new string('Q', 500);
        var a = new string('A', 2000);
        var r = Draft.BuildReferenceText(new List<(string, string)>(), (q, a));
        Assert.Contains(new string('Q', 200), r);
        Assert.False(r.Contains(new string('Q', 300)));
        Assert.Contains(new string('A', 800), r);
        Assert.False(r.Contains(new string('A', 1000)));
    }

    [Fact]
    public void BuildReferenceText_Empty_ReturnsEmpty()
    {
        Assert.Equal("", Draft.BuildReferenceText(new List<(string, string)>(), null));
    }

    // ---- AssembleMarkdown ----

    [Fact]
    public void AssembleMarkdown_Structure()
    {
        var md = Draft.AssembleMarkdown("FAQ集", true, "社内FAQ", new List<(string, string)> { ("Q&A", "内容") }, new List<string> { "QA_list.md", "kitei.pdf" });
        Assert.StartsWith("# 社内FAQ", md);
        Assert.Contains("種類: FAQ集（自動判定）", md);
        Assert.Contains("## Q&A", md);
        Assert.Contains("参照: QA_list.md、kitei.pdf", md);
    }

    [Fact]
    public void Templates_RegistryComplete()
    {
        foreach (var id in new[] { "procedure", "report", "proposal", "faq", "free" })
        {
            Assert.Contains(id, Draft.Templates.Keys);
            Assert.True(Draft.Templates[id].FallbackOutline.Length >= 3);
        }
    }
}
