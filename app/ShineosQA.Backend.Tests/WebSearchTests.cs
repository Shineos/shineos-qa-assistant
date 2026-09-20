using System.Text;
using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public class WebSearchTests
{
    [Fact]
    public void HtmlToText_RemovesInvisibleBlocksAndTags()
    {
        var html = """
            <html><head><style>body{color:red}</style><script>var x=1;</script></head>
            <body><h1>神奈川県の週間予報</h1><p>9月21日は<strong>雨</strong>、降水確率<strong>90%</strong>。</p>
            <noscript>JS無効</noscript></body></html>
            """;
        var text = WebSearch.HtmlToText(html);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("var x", text);
        Assert.DoesNotContain("JS無効", text);
        Assert.DoesNotContain("<", text);
        Assert.Contains("神奈川県の週間予報", text);
        Assert.Contains("雨", text);
        Assert.Contains("90%", text);
    }

    [Fact]
    public void HtmlToText_DecodesEntities()
    {
        Assert.Contains("A & B", WebSearch.HtmlToText("<p>A &amp; B</p>"));
    }

    [Fact]
    public void DecodeHtml_UsesMetaCharsetUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("<html><head><meta charset=\"utf-8\"></head><body>横浜 25℃</body></html>");
        var text = WebSearch.DecodeHtml(bytes, bytes.Length, null);
        Assert.Contains("横浜 25℃", text);
    }

    [Fact]
    public void DecodeHtml_HeaderCharsetWins()
    {
        var bytes = Encoding.UTF8.GetBytes("<meta charset=\"shift_jis\">こんにちは");
        var text = WebSearch.DecodeHtml(bytes, bytes.Length, "UTF-8");
        Assert.Contains("こんにちは", text);
    }

    [Fact]
    public void BestWindow_PicksQueryRelevantSection_NotNavigation()
    {
        // ナビゲーションだらけの天気サイト: クエリ関連の見出しから始まり予報表を含む窓が選ばれること
        var nav = string.Concat(Enumerable.Repeat("天気予報 世界天気 気圧予報 長期予報 雨雲レーダー サイトマップ ヘルプ 検索 ", 30));
        var forecast = "神奈川県の2週間天気 20日18:00発表 21日(月) 曇時々雨 降水確率50% 22/29℃ 22日(火) 曇一時雨 40% 21/27℃ 23日(水) 曇 40% 20/26℃";
        var tail = string.Concat(Enumerable.Repeat("関連記事 天気ニュース 気象予報士の解説 特集 コラム ", 40));
        var tokens = Rag.Tokenize("神奈川県の今後の一週間の天気を教えて").ToHashSet();
        var win = WebSearch.BestWindow(nav + forecast + tail, tokens, 120);
        Assert.Contains("神奈川県の2週間天気", win);
        Assert.Contains("21日(月)", win); // 見出しに続く予報表（本文）が同じ窓に入る
        Assert.StartsWith("神奈川県", win.TrimStart()); // ナビから始まらない
    }

    [Fact]
    public void BestWindow_ShortTextReturnedAsIs()
    {
        var tokens = Rag.Tokenize("何かの質問").ToHashSet();
        Assert.Equal("短い本文です", WebSearch.BestWindow("短い本文です", tokens, 400));
    }

    [Fact]
    public void ToContext_ContainsTitleAndSnippet_WithoutUrl()
    {
        var ctx = WebSearch.ToContext(new List<WebSearch.WebResult>
        {
            new("気象庁 天気予報 - tenki.jp", "https://www.jma.go.jp/bosai/forecast/", "晴のち曇"),
        });
        Assert.Contains("気象庁 天気予報 - tenki.jp", ctx);
        Assert.Contains("晴のち曇", ctx);
        Assert.DoesNotContain("https://", ctx); // ctx=2048予算対策: URLは注入しない
    }
}
