using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>図番正規化・トークナイザ拡張（T5）。表記ゆれ（ハイフン有無・大小・全角）が同一トークンに揃うこと</summary>
public class ZubanTests
{
    [Theory]
    [InlineData("ST-1042A", "st1042a")]
    [InlineData("st1042a", "st1042a")]
    [InlineData("ＳＴ－１０４２Ａ", "st1042a")]   // 全角英数・全角ハイフン
    [InlineData("ST 1042 A", "st1042a")]         // 空白区切り
    [InlineData("ST_1042_A", "st1042a")]
    [InlineData("kb-305/2", "kb3052")]
    [InlineData("A", "a")]
    public void Normalize_Variants_ProduceExpectedKey(string input, string expected)
    {
        Assert.Equal(expected, Rag.NormalizeZuban(input));
    }

    [Theory]
    [InlineData("ST-1042A")]
    [InlineData("st1042a")]
    [InlineData("ＳＴ－１０４２Ａ")]
    public void Tokenize_ZubanVariants_AllContainNormalizedToken(string input)
    {
        Assert.Contains("st1042a", Rag.Tokenize(input));
    }

    [Fact]
    public void Tokenize_ZubanWithSeparator_KeepsLegacyTokensToo()
    {
        // 旧仕様トークン（st / 1042a）も併存し、既存の一致挙動を壊さない
        var tokens = Rag.Tokenize("ST-1042A");
        Assert.Contains("st", tokens);
        Assert.Contains("1042a", tokens);
    }

    [Fact]
    public void Tokenize_PlainAlnum_Unchanged()
    {
        var tokens = Rag.Tokenize("Expense2026");
        Assert.Contains("expense2026", tokens);
    }

    [Fact]
    public void Tokenize_JapaneseSentence_NotAffected()
    {
        // 日本語文に英数字トークンがない場合、正規形トークンは増えない
        var tokens = Rag.Tokenize("宿泊費の上限は泊あたりです。");
        Assert.All(tokens, t => Assert.False(t.Contains('-') || t.Contains('_') || t.Contains('/')));
    }

    [Fact]
    public void Tokenize_FullwidthDigits_NormalizedToHalfwidth()
    {
        // NFKC: 全角数字も半 width トークン化され、半角文書と一致する
        Assert.Contains("15000", Rag.Tokenize("１５０００円"));
    }

    [Fact]
    public void ChunkIndex_Search_ZubanVariant_FindsChunk()
    {
        // 索引側「ST-1042A」をクエリ「st1042a」でヒットさせる（ハイフンなし小文字）
        var idx = new ChunkIndex();
        idx.AddRange("ST-1042A.pdf", 1, new[] { (0, "【図面】図番: ST-1042A / 品名: サポートブラケット / 材質: SS400", new float[] { 1f, 0f }) });
        var qTokens = Rag.Tokenize("st1042a").ToHashSet();
        var hits = idx.Search(new float[] { 1f, 0f }, qTokens, 5);
        Assert.True(hits.Count > 0);
        Assert.Contains("st1042a", hits[0].Rec.Tokens); // 正規形トークンが索引側にも入っている
    }
}
