using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public class RagChunkTests
{
    [Fact]
    public void Chunk_ShortText_ReturnsSingleChunk()
    {
        var text = "宿泊費の上限は1泊あたり15,000円です。";
        var chunks = Rag.Chunk(text);
        Assert.Single(chunks);
        Assert.Contains("15,000", chunks[0]);
    }

    [Fact]
    public void Chunk_LongText_SplitsWithOverlap()
    {
        // 350字超の文が2つ → 2チャンク以上、後続チャンクに直前の末尾（オーバーラップ）が含まれる
        var sentence = "これはテスト文です。文書の内容を長くします。";
        var text = string.Concat(Enumerable.Repeat(sentence, 40)); // 約2,000字
        var chunks = Rag.Chunk(text);
        Assert.True(chunks.Count >= 2, $"expected >=2 chunks, got {chunks.Count}");
        Assert.True(chunks[1].Length > 0);
        // オーバーラップ検証: 2番目チャンクの先頭付近に1番目チャンクの末尾部分文字列が現れる
        var tail = chunks[0][^30..];
        var overlapFound = tail.Where((c, i) => i + 10 <= tail.Length && chunks[1].Contains(tail.Substring(i, 10))).Any();
        Assert.True(overlapFound, "second chunk should contain tail of the first (overlap)");
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsNoChunks()
    {
        Assert.Empty(Rag.Chunk(""));
    }

    [Fact]
    public void Tokenize_Japanese_BigramSplit()
    {
        // CJK連続は2文字bi-gram、アルファベットは小文字化
        var tokens = Rag.Tokenize("経費精算Expense");
        Assert.Contains("経費", tokens);
        Assert.Contains("expense", tokens);
        Assert.DoesNotContain("Expense", tokens);
    }

    [Fact]
    public void Tokenize_Mixed_AlphanumKept()
    {
        var tokens = Rag.Tokenize("パスワードは12文字以上");
        Assert.Contains("12", tokens);
        Assert.True(tokens.Count > 1);
    }
}

public class RagMathTests
{
    [Fact]
    public void Cosine_ParallelVectors_IsOne()
    {
        float[] a = { 1, 2, 3 };
        float[] b = { 2, 4, 6 };
        Assert.True(Math.Abs(Rag.Cosine(a, b) - 1.0) < 1e-6);
    }

    [Fact]
    public void Cosine_OrthogonalVectors_IsZero()
    {
        float[] a = { 1, 0 };
        float[] b = { 0, 1 };
        Assert.True(Math.Abs(Rag.Cosine(a, b)) < 1e-6);
    }

    [Fact]
    public void Cosine_OppositeVectors_IsMinusOne()
    {
        float[] a = { 1, 1 };
        float[] b = { -1, -1 };
        Assert.True(Math.Abs(Rag.Cosine(a, b) + 1.0) < 1e-6);
    }

    [Fact]
    public void FloatSerialization_Roundtrip()
    {
        float[] emb = { 0.1f, -0.5f, 3.14f, 0f };
        var bytes = ChunkIndex.FloatsToBytes(emb);
        Assert.Equal(emb.Length * 4, bytes.Length);
        var back = ChunkIndex.BytesToFloats(bytes);
        Assert.Equal(emb, back);
    }

    [Fact]
    public void GuardThresholds_AreSeparatedFromSkipThresholds()
    {
        // 設計不変条件: NO-HITガード(-5.0)はリランク省略cos(0.62)より十分小さい /
        // キャッシュ判定(0.97)は省略閾値より高い。意図しない閾値変更を検出する
        Assert.True(Rag.GuardThreshold < 0);
        Assert.True(Rag.RerankSkipCos < Rag.AnswerCacheCos);
        Assert.True(Rag.RerankSkipKw > 0 && Rag.RerankSkipKw < 1);
        // 弱一致バックストップ(MinAcceptKw)は省略閾値より下に敷く:
        // 0.15〜0.50の帯はリランカーに識別を委ね、バックストップはkw≈0(語彙的無関係)のみ拒否する
        Assert.True(Rag.MinAcceptKw < Rag.RerankSkipKw);
        Assert.Equal(8, Rag.RerankPool);
    }
}

public class RagSnippetTests
{
    [Fact]
    public void Snippet_ShortText_ReturnedAsIs()
    {
        var text = "短い文です。";
        Assert.Equal(text, Rag.Snippet(text, new HashSet<string> { "短い" }));
    }

    [Fact]
    public void Snippet_LongText_CenteredOnRelevantSentence()
    {
        var noise = new string('あ', 120) + "。";
        var relevant = "タクシーは22時以降に帰宅する場合は利用できる。";
        var text = string.Concat(Enumerable.Repeat(noise, 4)) + relevant + string.Concat(Enumerable.Repeat(noise, 4));
        var tokens = new HashSet<string>(Rag.Tokenize("タクシー 22時 帰宅"));
        var snip = Rag.Snippet(text, tokens, 100);
        Assert.Contains("タクシー", snip);
        Assert.True(snip.Length <= 240, $"snippet too long: {snip.Length}");
    }

    [Fact]
    public void Snippet_NoVocabularyOverlap_ReturnsFullText()
    {
        // クロスリンガル（日本語質問↔英語文書）では全文返却（誤ガード防止の仕様）
        var text = "The telework allowance is 5,000 yen per month. Employees working remotely 3 days per week are eligible. " +
                   "Applications must be submitted via the attendance system by the end of each month. Payment starts the following month.";
        var tokens = new HashSet<string>(Rag.Tokenize("テレワーク手当はいくら"));
        var snip = Rag.Snippet(text, tokens);
        Assert.Equal(text, snip);
    }

    [Fact]
    public void BuildContext_IncludesDocHeadersAndWeb()
    {
        var ctx = Rag.BuildContext(new List<(string, string)> { ("旅費規程.md", "日当は1,500円"), ("QA.md", "手順") }, "Webの結果");
        Assert.Contains("【文書1: 旅費規程.md】", ctx);
        Assert.Contains("【文書2: QA.md】", ctx);
        Assert.Contains("【Web検索結果】Webの結果", ctx);
    }

    [Fact]
    public void BuildContext_NullWeb_OmitsWebSection()
    {
        var ctx = Rag.BuildContext(new List<(string, string)> { ("a.md", "x") }, null);
        Assert.DoesNotContain("【Web検索結果】", ctx);
    }
}

public class RagCurrentDateTests
{
    [Fact]
    public void CurrentDateLine_ContainsDateAndWeekday()
    {
        var line = Rag.CurrentDateLine();
        var now = DateTime.Now;
        Assert.Contains($"現在の日付: {now:yyyy年M月d日}", line);
        Assert.Contains("曜日", line);
        Assert.Contains("日月火水木金土"[(int)now.DayOfWeek].ToString(), line);
    }

    [Fact]
    public void TimeSensitiveQuestion_MatchesRelativeDateQueries()
    {
        foreach (var q in new[] { "今日は何日ですか", "今日の日付は？", "今月の締めはいつ？", "今年の改正内容は？", "現在の税率は？", "明日は何曜日？" })
            Assert.True(Rag.TimeSensitiveQuestion().IsMatch(q), $"should match: {q}");
    }

    [Fact]
    public void TimeSensitiveQuestion_MatchesVolatileWorldStateQueries()
    {
        // 天気・市況は現実の状態で正解が変わるため古い回答キャッシュを使わない
        foreach (var q in new[] { "神奈川県の今後の一週間の天気を教えて", "明日の気温は？", "今日のニュース", "今の円の為替" })
            Assert.True(Rag.TimeSensitiveQuestion().IsMatch(q), $"should match: {q}");
    }

    [Fact]
    public void TimeSensitiveQuestion_DoesNotMatchStableQueries()
    {
        foreach (var q in new[] { "経費精算の手順は？", "タクシー費の上限額を教えて", "宿泊費はいくらですか" })
            Assert.False(Rag.TimeSensitiveQuestion().IsMatch(q), $"should not match: {q}");
    }

    [Fact]
    public void SystemInfoLine_ContainsDateWeekdayAndTime()
    {
        var line = Rag.SystemInfoLine();
        var now = DateTime.Now;
        Assert.StartsWith("【システム情報】現在の日時: ", line);
        Assert.Contains($"{now:yyyy年M月d日}", line);
        Assert.Contains("曜日", line);
        Assert.Matches(@"\d{2}:\d{2}", line); // 時刻（HH:mm）
    }

    [Fact]
    public void TimeSensitiveQuestion_MatchesClockQueries()
    {
        foreach (var q in new[] { "今何時？", "現在の時刻を教えて", "日付は？" })
            Assert.True(Rag.TimeSensitiveQuestion().IsMatch(q), $"should match: {q}");
    }
}

/// <summary>弱一致バックストップ(IsWeakMatch)。識別の主役はリランクスコア(GuardThreshold)で、RerankSkipKw=0.50
/// により弱一致は必ずリランクを経由する。本バックストップはリランカー未導入環境でkw≈0(語彙的無関係)を拒否する。
/// 0.15〜0.50の帯はリランカーが無いと識別不能（10-01実測の富士山誤答kw=0.37はこの帯 → リランク経由でGuardThresholdが拒否）</summary>
public class RagWeakMatchTests
{
    [Fact]
    public void NearZeroKw_WithoutExemptions_Refuses()
    {
        // 語彙的にほぼ無関係な採択(リランカー未導入環境の最終防波堤)
        Assert.True(Rag.IsWeakMatch(0.00, hasWebContext: false, timeSensitive: false, drawingIntent: false, rerankValidated: false));
        Assert.True(Rag.IsWeakMatch(0.14, hasWebContext: false, timeSensitive: false, drawingIntent: false, rerankValidated: false));
    }

    [Fact]
    public void MidBand_PassesBackstop_RerankIsTheDiscriminator()
    {
        // 正解質問(実測0.39-0.48)もKB外質問(0.37)もこの帯 — kwでは分離不可のためバックストップは通し、
        // RerankSkipKw=0.50により必ずリランクへ流して GuardThreshold で識別する
        Assert.False(Rag.IsWeakMatch(0.37, hasWebContext: false, timeSensitive: false, drawingIntent: false, rerankValidated: false));
        Assert.False(Rag.IsWeakMatch(0.46, hasWebContext: false, timeSensitive: false, drawingIntent: false, rerankValidated: false));
    }

    [Theory]
    [InlineData(true, false, false)]   // Web文脈あり
    [InlineData(false, true, false)]   // 時間感応
    [InlineData(false, false, true)]   // 図面意図(寸法ノイズでkwが構造的に希薄)
    public void ExemptedQuestions_Pass_EvenWithNearZeroKw(bool hasWebContext, bool timeSensitive, bool drawingIntent)
    {
        Assert.False(Rag.IsWeakMatch(0.10, hasWebContext, timeSensitive, drawingIntent, rerankValidated: false));
    }

    [Fact]
    public void RerankValidated_Passes_EvenWithNearZeroKw()
    {
        // リランクを実行しガード(GuardThreshold)を通過した採択はクロスエンコーダ検証済み → 対象外
        Assert.False(Rag.IsWeakMatch(0.10, hasWebContext: false, timeSensitive: false, drawingIntent: false, rerankValidated: true));
    }
}
