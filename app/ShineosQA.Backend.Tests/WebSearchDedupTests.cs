using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>検索結果の重複排除: DuckDuckGoが同一サイト/同一スニペットを複数返すと
/// 参照情報が重複し、クイック1.7Bの「情報なし」誤判定（横浜天気の実測）を誘発する</summary>
public class WebSearchDedupTests
{
    [Fact]
    public void Dedup_RemovesSameUrlAndSameContent()
    {
        var input = new List<WebSearch.WebResult>
        {
            new("横浜市の天気 - tenki.jp", "https://tenki.jp/forecast/3/17/", "今日は曇り 気温23度"),
            new("横浜市の天気 - tenki.jp", "https://tenki.jp/forecast/3/17/?param=x", "今日は曇り 気温23度"), // 同一URL(クエリ差)+同一内容
            new("横浜 の天気 - weather.com", "https://weather.example/yo", "今日は曇り 気温23度"),        // 別URL・同一内容（サイトが違うので残す）
            new("週間予報 - tenki.jp", "https://tenki.jp/forecast/week/", "来週は晴れ間が多い"),
        };
        var r = WebSearch.Dedup(input);
        Assert.Equal(3, r.Count); // 同一URL+同一内容の2件目のみ排除（1,3,4を保持）
        Assert.DoesNotContain(r, x => x.Url.Contains("?param=x"));
        Assert.Contains(r, x => x.Url.StartsWith("https://weather.example/"));
        Assert.Contains(r, x => x.Url.StartsWith("https://tenki.jp/forecast/week/"));
    }

    [Fact]
    public void ToContext_EmitsUniqueLinesOnly()
    {
        var input = new List<WebSearch.WebResult>
        {
            new("横浜の天気", "https://a.example/", "晴れ"),
            new("横浜の天気", "https://b.example/", "晴れ"),
        };
        var ctx = WebSearch.ToContext(input);
        var nonEmpty = ctx.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        Assert.Single(nonEmpty); // 同一内容（タイトル+スニペット）の2行目は落とす
        Assert.Contains("晴れ", nonEmpty[0]);
    }
}
