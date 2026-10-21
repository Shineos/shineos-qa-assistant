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

/// <summary>低信頼×低Kwの組み合わせ判定(IsLowConfidenceWeak)。リランク検証済みでも、グレー帯
/// (GuardThreshold≤top1&lt;RerankHighConf)×キーワードほぼ無関係(maxKw&lt;MinAcceptKw)は該当なし拒否へ。
/// 実測(2026-10-19): 正解top1≥+7.3 / 無関係・表層類似≤-10.6 と強分離 → グレー帯の半端な組み合わせのみ拒否</summary>
public class RagLowConfidenceWeakTests
{
    [Fact]
    public void GrayBand_WithNearZeroKw_Refuses()
    {
        // リランク通過済みだが確信できないグレー帯 × キーワード一致なし → 拒否
        Assert.True(Rag.IsLowConfidenceWeak(-4.9, 0.00, rerankValidated: true));
        Assert.True(Rag.IsLowConfidenceWeak(+1.9, 0.14, rerankValidated: true));
    }

    [Fact]
    public void HighConfidenceTop1_Passes_PureParaphrase()
    {
        // 純言い換えヒット: top1が高信頼(RerankHighConf以上)ならkw≈0でも通す
        Assert.False(Rag.IsLowConfidenceWeak(+2.0, 0.00, rerankValidated: true));
        Assert.False(Rag.IsLowConfidenceWeak(+7.3, 0.05, rerankValidated: true));
    }

    [Fact]
    public void KeywordConfirmed_Passes_EvenInGrayBand()
    {
        // キーワード確認済み(maxKw≥MinAcceptKw)ならグレー帯でも通す（語彙的根拠あり）
        Assert.False(Rag.IsLowConfidenceWeak(+1.0, 0.15, rerankValidated: true));
        Assert.False(Rag.IsLowConfidenceWeak(-3.0, 0.37, rerankValidated: true));
    }

    [Fact]
    public void NotRerankValidated_NeverFires()
    {
        // 本ガードはリランク検証済み経路専用。未検証経路はIsWeakMatch(バックストップ)が担う
        Assert.False(Rag.IsLowConfidenceWeak(+1.0, 0.00, rerankValidated: false));
    }

    [Fact]
    public void HighConfThreshold_SitsAboveGuardThreshold()
    {
        // グレー帯が成立するための不変条件: 高信頼判定がno-hit判定より上にある
        Assert.True(Rag.RerankHighConf > Rag.GuardThreshold);
        Assert.True(Rag.RerankHighConf > 0);
        Assert.True(Rag.MinAcceptKw < Rag.RerankSkipKw);
    }
}

/// <summary>連続同一文の畳み込み(CollapseDuplicateSentences)。繰り返し出力クセ対策
/// （2026-10-19実測: 「〜です。」が直後に1回繰り返される）。非連続の反復・言い換えは触らない</summary>
public class RagCollapseTests
{
    [Fact]
    public void ConsecutiveDuplicate_Collapses()
    {
        var r = Rag.CollapseDuplicateSentences("タイムカードの締め日は毎月25日です。タイムカードの締め日は毎月25日です。");
        Assert.Equal("タイムカードの締め日は毎月25日です。", r);
    }

    [Fact]
    public void NonConsecutiveRepeat_Kept()
    {
        var t = "Aです。Bです。Aです。";
        Assert.Equal(t, Rag.CollapseDuplicateSentences(t));
    }

    [Fact]
    public void Decimals_NotTreatedAsSentenceEnd()
    {
        Assert.Equal("公差は3.5です。", Rag.CollapseDuplicateSentences("公差は3.5です。"));
    }

    [Fact]
    public void DifferentSentences_Kept()
    {
        var t = "見積は150,000円です。原価は98,000円です。";
        Assert.Equal(t, Rag.CollapseDuplicateSentences(t));
    }

    [Fact]
    public void WhitespaceOnlyDifference_Collapses()
    {
        Assert.Equal("締め日は25日です。", Rag.CollapseDuplicateSentences("締め日は25日です。締め日は 25日です。"));
    }

    [Fact]
    public void LineStructure_Preserved()
    {
        var t = "- 手順1\n- 手順2\n- 手順1";
        Assert.Equal(t, Rag.CollapseDuplicateSentences(t));
        Assert.Equal("", Rag.CollapseDuplicateSentences(""));
        Assert.Null(Rag.CollapseDuplicateSentences(null));
    }

    [Fact]
    public void WholeBlockRepeat_Collapses()
    {
        // 箇条書きブロック丸ごと2回反復（v2.3.2実機E2E実測・図面注記）:
        // 文単位では A,B,A,B で直前一致が無いため、ブロック単位のA+A判定で畳む
        var t = "- 溶接はJIS Z 3601に従う。  \n- 仕上げ面のバリ取りを完了後、検査すること。- 溶接はJIS Z 3601に従う。  \n- 仕上げ面のバリ取りを完了後、検査すること。\n\n出典：drawing-sample.pdf";
        var r = Rag.CollapseDuplicateSentences(t);
        Assert.Equal("- 溶接はJIS Z 3601に従う。  \n- 仕上げ面のバリ取りを完了後、検査すること。\n\n出典：drawing-sample.pdf", r);
    }

    [Fact]
    public void DifferentHalves_Kept()
    {
        // 前後半が一致しない通常回答は触らない
        var t = "見積は150,000円です。\n原価は98,000円です。";
        Assert.Equal(t, Rag.CollapseDuplicateSentences(t));
    }
}

/// <summary>文書要約の全文組み立て(SelectSummaryText・v2.4.1)。上限内は全文連結・
/// 超過は先頭から打ち切り+注記。seq順（=渡された順）を維持する</summary>
public class RagSelectSummaryTextTests
{
    [Fact]
    public void WithinLimit_JoinsAllInOrder()
    {
        var (text, truncated) = Rag.SelectSummaryText(new List<string> { "第一章", "第二章", "第三章" }, 100);
        Assert.Equal("第一章\n\n第二章\n\n第三章", text);
        Assert.False(truncated);
    }

    [Fact]
    public void OverLimit_TruncatesWithNote()
    {
        var chunks = new List<string> { new('あ', 50), new('い', 50) };
        var (text, truncated) = Rag.SelectSummaryText(chunks, 60);
        Assert.True(truncated);
        Assert.StartsWith(new string('あ', 50), text);
        Assert.Contains("先頭から一部のみ", text);
        Assert.DoesNotContain(new string('い', 10), text); // 2チャンク目は入らない
    }

    [Fact]
    public void ExactBoundary_NotTruncated()
    {
        var (text, truncated) = Rag.SelectSummaryText(new List<string> { "12345" }, 5);
        Assert.Equal("12345", text);
        Assert.False(truncated);
    }

    [Fact]
    public void EmptyChunks_ReturnsEmpty()
    {
        var (text, truncated) = Rag.SelectSummaryText(new List<string>(), 100);
        Assert.Equal("", text);
        Assert.False(truncated);
    }
}

/// <summary>連続同一行の畳み込み（v2.5.3・章生成の箇条書き重複対策）</summary>
public class RagCollapseLinesTests
{
    [Fact]
    public void ConsecutiveIdenticalBullet_Collapses()
    {
        var t = "- 人事規程に基づくFAQを記載します。\n- 人事規程に基づくFAQを記載します。\n- 別の行";
        var r = Rag.CollapseDuplicateSentences(t);
        Assert.Equal("- 人事規程に基づくFAQを記載します。\n- 別の行", r);
    }

    [Fact]
    public void WhitespaceOnlyDifferenceLines_Collapse()
    {
        Assert.Equal("- 同じ行", Rag.CollapseDuplicateSentences("- 同じ行\n- 同じ 行"));
    }

    [Fact]
    public void NonConsecutiveSameLine_Kept()
    {
        var t = "- A\n- B\n- A";
        Assert.Equal(t, Rag.CollapseDuplicateSentences(t));
    }

    [Fact]
    public void DifferentLines_Kept()
    {
        var t = "- 宿泊費の上限は15,000円です。\n- 交通費は電車・バス・タクシーが対象です。";
        Assert.Equal(t, Rag.CollapseDuplicateSentences(t));
    }
}
