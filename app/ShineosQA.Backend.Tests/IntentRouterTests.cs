using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public class QuestionNormTests
{
    [Theory]
    [InlineData("経費精算の手順は？", "経費精算の手順")]
    [InlineData("ケイヒセイサン", "けいひせいさん")]
    [InlineData("ＳＴ－１０４２Ａ", "st1042a")]
    [InlineData(" 材質 は？ ", "材質")]
    [InlineData("Ra 3.2／Rz", "ra32rz")]
    public void Normalize_UnifiesVariants(string input, string expected) =>
        Assert.Equal(expected, QuestionNorm.Normalize(input));

    [Fact]
    public void Normalize_Empty() => Assert.Equal("", QuestionNorm.Normalize(null));
}

public class IntentRouterTests
{
    [Theory]
    [InlineData("ST-1042Aの材質は？", AnswerRoute.DrawingMeta, "zairyo")]
    [InlineData("ST-1042Aの品名と改訂を教えて", AnswerRoute.DrawingMeta, "hinmei")]
    [InlineData("図面 ST-1042A の尺度は？", AnswerRoute.DrawingMeta, "scale")]
    [InlineData("ST-1042Aの寸法は？", AnswerRoute.DrawingQa, null)]
    [InlineData("ST-1042Aの見積は？", AnswerRoute.Jisseki, null)]
    [InlineData("ST-1042Aの改訂AとBの違いは？", AnswerRoute.DrawingCompare, null)]
    [InlineData("ST-1042Aの変更点を教えて", AnswerRoute.DrawingCompare, null)]
    [InlineData("経費精算の手順は？", AnswerRoute.DocQa, null)]
    [InlineData("図面の見方は？", AnswerRoute.DrawingQa, null)]
    [InlineData("テスト", AnswerRoute.Clarify, null)]
    public void Classify_Routes(string message, AnswerRoute route, string? firstAttr)
    {
        var d = IntentRouter.Classify(message);
        Assert.Equal(route, d.Route);
        if (firstAttr is not null)
            Assert.Contains(d.Attributes!, a => a.Key == firstAttr);
    }

    [Fact]
    public void Classify_MultipleAttributes_KeepsQuestionOrder()
    {
        var d = IntentRouter.Classify("ST-1042Aの改訂と材質は？");
        Assert.Equal(AnswerRoute.DrawingMeta, d.Route);
        Assert.Equal(new[] { "revision", "zairyo" }, d.Attributes!.Select(a => a.Key).ToArray());
    }

    [Fact]
    public void Classify_MaterialAndEstimate_DoesNotUseDrawingMeta()
    {
        var d = IntentRouter.Classify("ST-1042Aの材質と見積は？");
        Assert.NotEqual(AnswerRoute.DrawingMeta, d.Route);
    }
}
