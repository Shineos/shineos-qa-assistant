using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public class MfgTermsTests
{
    [Theory]
    [InlineData("ステンレスの材質", "sus304")]
    [InlineData("SUS304の板", "sus304")]
    [InlineData("アルミ材の強度", "a5052")]
    [InlineData("M8×1.25のねじ", "m8x125")]
    [InlineData("M8x1.25", "neji")]
    [InlineData("表面粗さRa3.2", "arasa")]
    [InlineData("JIS B 0405", "jisb0405")]
    [InlineData("見積書と原価", "mitumori")]
    [InlineData("見積書と原価", "genka")]
    [InlineData("納期はいつ", "nouki")]
    [InlineData("熱処理の条件", "netsushori")]
    [InlineData("ヒヤリハット", "anzen")]
    [InlineData("μm単位", "um")]
    public void Tokenize_AddsManufacturingCanonicalTokens(string text, string expected) =>
        Assert.Contains(expected, Rag.Tokenize(text));

    [Fact]
    public void Tokenize_DoesNotTreatRaInsideEnglishAsRoughness()
    {
        // "drawing" の内部に "ra" はあるが、粗さトークンを付けてはいけない
        Assert.DoesNotContain("arasa", Rag.Tokenize("drawing"));
    }

    [Fact]
    public void CanonicalTokens_AreDeduplicated()
    {
        var tokens = Rag.Tokenize("SUS304 ステンレス stainless").Where(t => t == "sus304").ToList();
        Assert.Single(tokens);
    }
}
