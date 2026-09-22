using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>生成後バリデーションガード（FabricatedNumbers）の不変条件。
/// 出典・質問のいずれにも存在しない数値だけを検出し、正常回答を誤検出しないこと</summary>
public class ValidationGuardTests
{
    private const string Evidence =
        "検査基準: 取付穴のピッチ 80mm ±0.2 を測定で確認する。材質はSS400。\n" +
        "日当: 国内 1,500円 海外 3,000円。改訂Bからは溶接部の浸透探傷試験を追加。";

    [Fact]
    public void NumbersPresentInEvidence_AreNotFlagged()
    {
        var ans = "取付穴のピッチは 80mm ±0.2 です。材質はSS400。日当は国内 1500円 です。";
        Assert.Empty(Rag.FabricatedNumbers(ans, Evidence));
    }

    [Fact]
    public void FabricatedNumber_IsDetected()
    {
        // 540 は出典に存在しない（実図面検証cr01で発生した取り違えパターン）
        var ans = "この図面は 540 の図面です。";
        var bad = Rag.FabricatedNumbers(ans, Evidence);
        Assert.Contains("540", bad);
    }

    [Fact]
    public void CommaAndFullWidthDigits_AreNormalized()
    {
        // 出典「1,500円」に対し回答「1500」/「１５００」も同一扱い
        var ans = "日当は国内 1500円 です。";
        Assert.Empty(Rag.FabricatedNumbers(ans, Evidence));
        var ansFw = "日当は国内１５００円です。";
        Assert.Empty(Rag.FabricatedNumbers(ansFw, Evidence));
    }

    [Fact]
    public void SingleDigitsAndListNumbers_AreIgnored()
    {
        // 箇条書き番号「1.」や1桁の言及は誤検出対象にしない
        var ans = "1. 手順を確認する\n2. 3級の図面です";
        Assert.Empty(Rag.FabricatedNumbers(ans, Evidence));
    }

    [Fact]
    public void Decimals_AreChecked()
    {
        var ans = "ピッチは 0.5mm です。"; // 0.2 ではなく 0.5（捏造）
        var bad = Rag.FabricatedNumbers(ans, Evidence);
        Assert.NotEmpty(bad);
    }
}
