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

/// <summary>横断質問の期間フレーズ抽出（SearchPhrase直接保証の入力）</summary>
public class RagEnumerationPhraseTests
{
    [Theory]
    [InlineData("「7日」という日数が出てくる規定をすべて教えてください", "7日")]
    [InlineData("「2週間」に関係する規定を2つ教えてください", "2週間")]
    [InlineData("「２週間」に関係する規定", "2週間")] // 全角数字はNFKCで半角化
    [InlineData("1か月以内の申請方法は", "1か月")]
    [InlineData("22時に関する規則にはどんなものがありますか", "22時")]
    public void QuotedDuration_ReturnsPhrase(string q, string expected)
    {
        Assert.Equal(expected, Rag.EnumerationPhrase(q));
    }

    [Theory]
    [InlineData("出張のホテル代、いくらまで大丈夫？")]
    [InlineData("会社のPCでUSBメモリを使う場合の条件を教えてください")]
    [InlineData("")]
    [InlineData(null)]
    public void NoDuration_ReturnsNull(string? q)
    {
        Assert.Null(Rag.EnumerationPhrase(q));
    }

    [Fact]
    public void 何日_IsNotCaptured()
    {        Assert.Null(Rag.EnumerationPhrase("慶弔休暇は何日もらえますか"));
    }
}

/// <summary>拒否応答の文言統一（自然文→定型・部分回答の保護込み）</summary>
public class RagNormalizeRefusalWordingTests
{
    [Fact]
    public void NaturalRefusal_IsNormalizedToFixedPhrase()
    {
        var t = "参照情報に記載がないため、社内Wi-Fiのパスワードを教えていただけないです。\nパスワードの設定は、組織の情報システム課に確認が必要です。";
        var r = Rag.NormalizeRefusalWording(t);
        Assert.NotNull(r);
        Assert.StartsWith("該当する記載がありません。", r);
        Assert.Contains("情報システム課に確認が必要です。", r); // 続きの文は保持
    }

    [Fact]
    public void FixedPhraseRefusal_Unchanged()
    {
        Assert.Null(Rag.NormalizeRefusalWording("該当する記載がありません。"));
    }

    [Fact]
    public void PartialAnswerWithButClause_Unchanged()
    {
        var t = "参照情報に記載がありませんが、手続きは勤怠システムから行えます。";
        Assert.Null(Rag.NormalizeRefusalWording(t));
    }

    [Fact]
    public void AnswerWithSourceLine_Unchanged()
    {
        var t = "参照情報に記載がないため回答できません。\n出典：QA_list.md";
        Assert.Null(Rag.NormalizeRefusalWording(t));
    }

    [Fact]
    public void NormalAnswer_Unchanged()
    {
        Assert.Null(Rag.NormalizeRefusalWording("日当は1日あたり1,500円です。"));
    }

    [Fact]
    public void ContainsWordingRefusal_Normalized()
    {
        // s13実測: 「含まれていません」型の自然文拒否も定型へ
        var r = Rag.NormalizeRefusalWording("参照情報に天気の情報は含まれていません。");
        Assert.NotNull(r);
        Assert.Equal("該当する記載がありません。", r);
    }

    [Fact]
    public void TopicPrefixedRefusal_Normalized()
    {
        // s14実測: 話題の接頭辞（副業に関する規定は…）が付いた拒否も定型へ
        var r = Rag.NormalizeRefusalWording("副業に関する規定は社内文書に記載されていません。");
        Assert.NotNull(r);
        Assert.Equal("該当する記載がありません。", r);
    }

    [Fact]
    public void DateAnswerWithKbMissDisclaimer_Normalized()
    {
        // s13実測: 日時応答＋「参照情報に記載されていない」の複合はKB欠けとして定型へ
        var t = "現在の日時は2026年10月30日（金曜日）22時19分です。天気は参照情報に記載されていないため、具体的な天気情報は提供できません。";
        var r = Rag.NormalizeRefusalWording(t);
        Assert.NotNull(r);
        Assert.Equal("該当する記載がありません。", r);
    }

    [Fact]
    public void PureDateAnswer_Unchanged()
    {
        // 「今日は何日」等の純粋な日時応答は拒否にしない
        Assert.Null(Rag.NormalizeRefusalWording("現在の日時は2026年10月30日（金曜日）22時19分です。"));
    }

    [Fact]
    public void DateAnswerWithKbFacts_Unchanged()
    {
        // 日時応答＋KBの実答（拒否文なし）は部分回答として保持
        var t = "現在の日時は2026年10月30日です。出張申請は出発日の5営業日前までに行います。";
        Assert.Null(Rag.NormalizeRefusalWording(t));
    }
}

/// <summary>フレーズ直参照（列挙質問の「7日」等の取りこぼし保証）</summary>
public class ChunkIndexSearchPhraseTests
{
    private static ChunkIndex MakeIndex(params string[] texts)
    {
        var idx = new ChunkIndex();
        idx.AddRange("t.md", 1, texts.Select((t, i) => (i, t, new float[] { 1f, 0f })));
        return idx;
    }

    [Fact]
    public void PhraseHits_ContainPhraseOnly_AndRankByCount()
    {
        var idx = MakeIndex("駐車場の利用申請は7日前まで", "忌引きは7日。7日以内に取得", "日当は1,500円");
        var hits = idx.SearchPhrase("7日", 4);
        Assert.Equal(2, hits.Count); // 「日当」チャンクは含まない
        Assert.Equal("忌引きは7日。7日以内に取得", hits[0].Rec.Text); // 出現2回が先頭
    }

    [Fact]
    public void NoHit_ReturnsEmpty()
    {
        var idx = MakeIndex("日当は1,500円");
        Assert.Empty(idx.SearchPhrase("7日", 4));
    }

    [Fact]
    public void EmptyPhrase_ReturnsEmpty()
    {
        var idx = MakeIndex("日当");
        Assert.Empty(idx.SearchPhrase("", 4));
    }
}

/// <summary>フレーズ中心スニペット（列挙質問の目的行の窓外切断対策）</summary>
public class RagSnippetAroundTests
{
    private static readonly HashSet<string> EmptyTokens = new();

    [Fact]
    public void PhraseSentenceCentered_EvenWhenTokenOverlapPrefersElsewhere()
    {
        var text = "オフィスの空調は18時30分に自動停止する。自転車置き場は登録制である。駐車場の利用申請は使用日の7日前までに提出すること。ゴミの分別は各階のルールに従う。名刺の注文は月1回までとする。段ボールは月曜と木曜に回収する。制服は入社時に支給する。郵便物の私物発送は禁止されている。";
        var r = Rag.SnippetAround(text, "7日", EmptyTokens, 60);
        Assert.Contains("7日前", r); // トークン中心だと切れる行が窓に残る
    }

    [Fact]
    public void NoPhraseSentence_FallsBackToTokenSnippet()
    {
        var text = "日当は1,500円です。海外出張は3,000円です。移動日を含めて支給されます。";
        var r = Rag.SnippetAround(text, "7日", Rag.Tokenize("日当").ToHashSet(), 240);
        Assert.Contains("1,500", r);
    }

    [Fact]
    public void ShortText_ReturnedAsIs()
    {
        Assert.Equal("7日前まで", Rag.SnippetAround("7日前まで", "7日", EmptyTokens, 240));
    }
}

/// <summary>列挙質問の転写ヒント（引用語を含む行の抽出）</summary>
public class RagExtractPhraseLinesTests
{
    [Fact]
    public void ExtractsLinesContainingPhrase_AndAltDigitNotation()
    {
        var text = "タクシーは原則禁止。ただし、22時以降に帰宅する場合は利用できる\n深夜労働（22:00〜翌5:00）の割増賃金は 25%増\n始業は9:00です";
        var lines = Rag.ExtractPhraseLines(text, "22時", "22");
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Contains("タクシー"));
        Assert.Contains(lines, l => l.Contains("25%増"));
        Assert.DoesNotContain(lines, l => l.Contains("9:00"));
    }

    [Fact]
    public void CapsAtMaxLines_AndTrimsLongSentences()
    {
        var text = "あ7日い。\nう7日え。\nお7日か。\nき7日く。\nけ7日こ。";
        var lines = Rag.ExtractPhraseLines(text, "7日", null, maxLines: 3, maxChars: 150);
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void EmptyInputs_ReturnEmpty()
    {
        Assert.Empty(Rag.ExtractPhraseLines("", "7日", null));
        Assert.Empty(Rag.ExtractPhraseLines("テキスト", "", null));
    }
}

/// <summary>外国語クエリ判定（クロスリンガル救済の発火条件）</summary>
public class RagForeignQueryTests
{
    [Theory]
    [InlineData("Khi quên mật khẩu thì liên hệ với ai?")]   // vi（Latin）
    [InlineData("忘记密码时应该联系谁？")]                    // zh（漢字のみ・かななし）
    [InlineData("Berapa biaya parkir per jam?")]            // id
    [InlineData("Magkano ang bayad sa paradahan?")]         // tl
    public void ForeignQueries_AreDetected(string q) => Assert.True(Rag.IsForeignQuery(q));

    [Theory]
    [InlineData("パスワードを忘れたときの連絡先を教えてください")] // かなあり=日本語
    [InlineData("おすすめのパスワード管理ツールを教えて")]
    [InlineData("会議室の床材は何ですか")]
    [InlineData("社内のWi-Fiのパスワードを教えてください")]
    public void JapaneseQueries_AreNotForeign(string q) => Assert.False(Rag.IsForeignQuery(q));
}

/// <summary>回答サニタイズ（文書名ラベルの除去・重複行の除去）</summary>
public class RagSanitizeAnswerTests
{
    [Fact]
    public void RemovesDocumentLabelsAtLineStart()
    {
        var t = "【経費精算】宿泊費が上限を超える場合は、事前に所属長の承認が必要です。";
        Assert.Equal("宿泊費が上限を超える場合は、事前に所属長の承認が必要です。", Rag.SanitizeAnswer(t));
    }

    [Fact]
    public void RemovesDuplicateLinesAfterNormalization()
    {
        var t = "【経費精算】パスワードは12文字以上とし、90日ごとに変更すること。\n【ITサポート】パスワードは12文字以上とし、90日ごとに変更すること。";
        Assert.Equal("パスワードは12文字以上とし、90日ごとに変更すること。", Rag.SanitizeAnswer(t));
    }

    [Fact]
    public void KeepsDistinctLines_AndNumbers()
    {
        var t = "国内出張: 1日あたり 1,500円\n海外出張: 1日あたり 3,000円";
        Assert.Equal(t, Rag.SanitizeAnswer(t));
    }

    [Fact]
    public void DoesNotTouchLabelsNotInWhitelist()
    {
        var t = "【重要】期限は2週間以内です。";
        Assert.Equal(t, Rag.SanitizeAnswer(t)); // 未知ラベルは保持（文書見出しの写しのみ対象）
    }
}

/// <summary>箇条書き番号違いの重複除去（非数値の水増し対策）</summary>
public class RagSanitizeListDedupTests
{
    [Fact]
    public void DuplicateItemsWithDifferentNumbers_AreCollapsed()
    {
        var t = "1. 運賃の領収書（税抜きまたは税込）\n2. 電車・バスの利用履歴\n3. 運賃の領収書（税抜きまたは税込）";
        var r = Rag.SanitizeAnswer(t);
        Assert.Single(r.Split('\n').Where(l => l.Contains("運賃の領収書")));
        Assert.Contains("電車・バスの利用履歴", r);
    }

    [Fact]
    public void DistinctItems_AllKept()
    {
        var t = "1. 電車・バスの利用履歴\n2. タクシーの利用履歴\n3. マイカーの利用履歴";
        Assert.Equal(3, Rag.SanitizeAnswer(t).Split('\n').Length);
    }
}

/// <summary>QA見出しの一致チェック（s14型すり替え防止・v2.7.0）</summary>
public class RagQaHeadingMismatchTests
{
    private const string QaListRef = @"## 【経費精算】経費精算の手順は？
A: 経費精算システムにログインします。
## 【経費精算】交通費（スイクヒ）の申請方法は？
A: 交通費は「交通費精算」メニューから申請します。ICカードの利用履歴または領収書を添付してください。
## 【ITサポート】パスワードを忘れた場合は？
A: ITヘルプデスク（内線1234）にご連絡ください。
## 【ITサポート】PC の貸出申請は？
A: 情報システム課に申請します。
## 【人事規程】在宅勤務の申請条件は？
A: 所属長の承認を得たうえで申請できます。電話対応や顧客訪問を伴わない業務に限ります。";

    [Fact]
    public void MatchingHeading_NotMismatch()
    {
        Assert.False(Rag.QaHeadingMismatch("経費精算の手順を教えてください", QaListRef));
        Assert.False(Rag.QaHeadingMismatch("パスワードを忘れたときの連絡先を教えてください", QaListRef));
    }

    [Fact]
    public void UnrelatedTopic_IsMismatch()
    {
        Assert.True(Rag.QaHeadingMismatch("副業をするときの規定を教えてください", QaListRef));
        // 英語等のかな無しクエリは外国語判定（IsForeignQuery）で対象外になるため、
        // ここでは日本語のみを対象にする（ForeignQuery_Skipped を参照）
    }

    [Fact]
    public void QuestionContentWordInHeading_NotMismatch()
    {
        // 質問の内容語が見出しに含まれる場合は一致とみなす救済（cmp23実測のt26/t44/t41誤発火対策）:
        // 「貸出」→「PCの貸出申請は？」「経費精算」→「経費精算の手順は？」
        Assert.False(Rag.QaHeadingMismatch("貸出PCの返却期限は？", QaListRef)); // 「貸出」が見出しに含まれる
        Assert.False(Rag.QaHeadingMismatch("経費精算は最後に誰に承認申請するのですか", QaListRef)); // 「経費精算」
    }

        [Fact]
    public void NonQaFormat_Ref_ReturnsFalse()
    {
        Assert.False(Rag.QaHeadingMismatch("なんでも質問", "普通の文書です。箇条書きはありません。"));
    }

    [Fact]
    public void ForeignQuery_Skipped()
    {
        // 外国語クエリ（かななし・vi/zh/id/tl）は見出し照合の対象外（多言語回帰22/32→31復帰のため）
        Assert.False(Rag.QaHeadingMismatch("Khi quên mật khẩu thì liên hệ ai", QaListRef));
        Assert.False(Rag.QaHeadingMismatch("密码忘记了怎么办", QaListRef));
        Assert.False(Rag.QaHeadingMismatch("bagaimana cara reset password", QaListRef));
        Assert.False(Rag.QaHeadingMismatch("Saan tatawag kapag nakalimutan ang password", QaListRef));
    }

    [Fact]
    public void ConcatenatedChunkText_HeadingsStillExtracted()
    {
        // チャンク化で改行が失われたテキストでも「## 【タグ】...？」を抽出できる
        // （s14再発対策・2026-10-31: 行頭判定だと先頭行しか見ずQA形式と誤判定していた）
        var joined = QaListRef.Replace("\n", "");
        Assert.True(Rag.QaHeadingMismatch("副業をするときの規定を教えてください", joined));
        Assert.False(Rag.QaHeadingMismatch("経費精算の手順を教えてください", joined));
        Assert.False(Rag.QaHeadingMismatch("パスワードを忘れたときの連絡先を教えてください", joined));
    }
}

/// <summary>QA見出しガードの過剰発火防止（混合KBで非QA文書が根拠を持つ質問の保護・2026-10-31）</summary>
public class RagQaGuardOverfireTests
{
    private const string NonQaText = "出張の申請は、出発日の5営業日前までに出張申請システムで行うこと。国内出張: 1日あたり 1,500円。私物のUSBメモリの利用は禁止。フレックスタイムのコアタイムは 11:00〜15:00。";

    private const string QaText = "## 【経費精算】経費精算の手順は？A: ログインして新規精算を開きます。## 【経費精算】交通費の申請方法は？A: 交通費精算メニューから申請します。";

    [Fact]
    public void QuestionAnsweredByNonQaDoc_SkipGuard()
    {
        // 日当・私物USB等、QA_list以外の規程文書が答えを持つ質問は照合しない（不発火）
        Assert.True(Rag.NonQaChunkGrounded("国内出張の日当はいくらですか", new[] { NonQaText }));
        Assert.True(Rag.NonQaChunkGrounded("私物のUSBメモリを使ってもよいですか", new[] { NonQaText }));
        Assert.True(Rag.NonQaChunkGrounded("フレックスタイムのコアタイムは何時から何時ですか", new[] { NonQaText }));
    }

    [Fact]
    public void QuestionOutsideKb_VocabularyNotInNonQaDocs_Fire()
    {
        // KB外話題（副業）の質問は非QA文書に語彙が無い→照合して発火（拒否へ）
        Assert.False(Rag.NonQaChunkGrounded("副業をするときの規定を教えてください", new[] { NonQaText }));
    }

    [Fact]
    public void HiraganaOnlyQuestion_Fire()
    {
        // 内容語が抽出できない質問は保守的に照合へ進む
        Assert.False(Rag.NonQaChunkGrounded("どうすればよいですか", new[] { NonQaText }));
    }

    [Fact]
    public void IsQaFormatChunk_DetectsRegardlessOfNewlines()
    {
        Assert.True(Rag.IsQaFormatChunk(QaText));
        Assert.True(Rag.IsQaFormatChunk(QaText.Replace("\n", "")));
        Assert.False(Rag.IsQaFormatChunk(NonQaText));
    }
}

/// <summary>疑問詞の要点外し判定（t44型対策・v2.7.0）</summary>
public class RagFocusMismatchTests
{
    [Fact]
    public void WhoQuestion_AnswerContainsApprover_NotMismatch()
    {
        // 「誰に承認申請」→回答に「上司」があれば要点に合致
        Assert.False(Rag.FocusMismatch("経費精算は最後に誰に承認申請するのですか", "1. 項目を入力します 2. 上司の承認申請を行います"));
        Assert.False(Rag.FocusMismatch("誰に連絡すればよいですか", "ITヘルプデスクの田中さんにご連絡ください"));
    }

    [Fact]
    public void WhoQuestion_ProcedureOnly_IsMismatch()
    {
        // 手順だけが列挙され、承認者・役職・人名が一切出ない回答は要点外し
        Assert.True(Rag.FocusMismatch("経費精算は最後に誰に承認申請するのですか", "1. 経費精算システムにログインします 2. 領収書の画像を添付します 3. 申請を確定させます"));
    }

    [Fact]
    public void WhenQuestion_AnswerWithoutDate_IsMismatch()
    {
        Assert.True(Rag.FocusMismatch("出張申請はいつまでに提出すればよいですか", "出張申請は出張申請システムから行います"));
        Assert.False(Rag.FocusMismatch("出張申請はいつまでに提出すればよいですか", "出発日の5営業日前までに提出してください"));
    }

    [Fact]
    public void WhereQuestion_AnswerWithoutPlace_IsMismatch()
    {
        Assert.True(Rag.FocusMismatch("交通費はどこで申請しますか", "交通費は月に1回まとめて精算します"));
        Assert.False(Rag.FocusMismatch("交通費はどこで申請しますか", "交通費精算メニューから申請します"));
    }

    [Fact]
    public void HowMuchQuestion_AnswerWithoutNumber_IsMismatch()
    {
        Assert.True(Rag.FocusMismatch("国内出張の日当はいくらですか", "日当は出張の期間に応じて支給されます"));
        Assert.False(Rag.FocusMismatch("国内出張の日当はいくらですか", "1日あたり1,500円です"));
    }

    [Fact]
    public void WhenQuestion_TimeRangeAnswer_NotMismatch()
    {
        // t58実測: 「11:00〜15:00」形式（時の文字なし）も時刻表現として認める
        Assert.False(Rag.FocusMismatch("フレックスタイムのコアタイムは何時から何時ですか", "フレックスタイムのコアタイムは11:00〜15:00です。"));
    }

    [Fact]
    public void RefusalAnswer_NeverMismatch()
    {
        Assert.False(Rag.FocusMismatch("誰に承認申請するのですか", "該当する記載がありません。"));
    }

    [Fact]
    public void NoInterrogative_AlwaysFalse()
    {
        Assert.False(Rag.FocusMismatch("経費精算の手順を教えてください", "ログインして入力します"));
    }
}

/// <summary>箇条書き項目の根拠判定（t47型水増し検出・v2.7.0）</summary>
public class RagUngroundedListItemsTests
{
    private const string Ref = "交通費は「交通費精算」メニューから申請します。ICカードの利用履歴または領収書を添付してください。電車・バス・タクシーが対象です。";

    [Fact]
    public void GroundedItems_NotDetected()
    {
        var a = "1. ICカードの利用履歴\n2. 領収書";
        Assert.Empty(Rag.UngroundedListItems(a, Ref));
    }

    [Fact]
    public void FabricatedItem_Detected()
    {
        // KBに無い「マイカー」項目の水増し（t47実測）
        var a = "1. ICカードの利用履歴\n2. マイカー利用証明書の写し";
        var bad = Rag.UngroundedListItems(a, Ref);
        Assert.Single(bad);
        Assert.Contains("マイカー", bad[0]);
    }

    [Fact]
    public void NonListAnswer_NotChecked()
    {
        Assert.Empty(Rag.UngroundedListItems("交通費は領収書を添付して申請します。", Ref));
    }

    [Fact]
    public void ShortLines_Skipped()
    {
        Assert.Empty(Rag.UngroundedListItems("- あ\n- い", Ref));
    }
}

/// <summary>非連続の完全重複行除去（下書き単発生成のブロック反復対策・v2.7.0）</summary>
public class RagCollapseDuplicateLinesTests
{
    [Fact]
    public void NonConsecutiveBlockRepeat_Collapsed()
    {
        // 実測パターン: FAQ項目が4項目目を挟んで2巡（完全同一文）
        var t = "- 慶弔休暇の申請は勤怠システムの「休暇申請」から行う。\n"
            + "- 忌引きの日数は、父母・配偶者は7日、祖父母・兄弟姉妹・子は3日、おじ・おば・いとこは1日と定められている。\n"
            + "- 緊急で慶弔休暇を取得した場合は、所属長に電話で連絡し、事後申請を行う。\n"
            + "- 慶弔休暇は通夜・葬儀の日の含めず連続した日数として計上される。\n"
            + "- 慶弔休暇の申請は勤怠システムの「休暇申請」から行う。\n"
            + "- 忌引きの日数は、父母・配偶者は7日、祖父母・兄弟姉妹・子は3日、おじ・おば・いとこは1日と定められている。\n"
            + "- 緊急で慶弔休暇を取得した場合は、所属長に電話で連絡し、事後申請を行う。";
        var r = Rag.CollapseDuplicateLines(t)!;
        Assert.Equal(4, r.Split('\n').Length);
        Assert.Equal(1, r.Split('\n').Count(l => l.Contains("休暇申請")));
    }

    [Fact]
    public void ListMarkerAndWhitespaceDifferences_AreSame()
    {
        // 箇条書き記号の差・番号差・空白差を吸収した重複判定
        var t = "- 慶弔休暇の申請は勤怠システムの休暇申請から行う。\n"
            + "・慶弔休暇の申請は勤怠システムの休暇申請から行う。\n"
            + "1. 慶弔休暇の申請は勤怠システムの休暇申請から行う。";
        var r = Rag.CollapseDuplicateLines(t)!;
        Assert.Single(r.Split('\n'));
    }

    [Fact]
    public void ParaphrasedItems_Kept()
    {
        // 言い換え（完全一致でない）は重複とみなさない（保守的）
        var t = "- 忌引きの日数は父母・配偶者は7日である。\n- 忌引きの日数は配偶者の場合7日となっている。";
        Assert.Equal(2, Rag.CollapseDuplicateLines(t)!.Split('\n').Length);
    }

    [Fact]
    public void PunctuationOnlyDifference_IsSame()
    {
        // 2026-10-31実測: 「…から行う。」と読点の差だけの行が重複判定をすり抜けた
        var t = "- 慶弔休暇の申請は勤怠システムの「休暇申請」から行う。\n"
            + "- 忌引きの日数は7日と定められている。\n"
            + "- 慶弔休暇の申請は、勤怠システムの「休暇申請」から行う。";
        var r = Rag.CollapseDuplicateLines(t)!;
        Assert.Equal(2, r.Split('\n').Length);
    }

    [Fact]
    public void HeadingsAndSourceLines_Kept()
    {
        var t = "## よくある質問\n- パスワードを忘れた場合は連絡する。\n出典：QA_list.md\n## よくある質問";
        var r = Rag.CollapseDuplicateLines(t)!;
        Assert.Equal(4, r.Split('\n').Length); // 見出し・出典行は重複判定対象外で保持
    }

    [Fact]
    public void ShortLines_Kept()
    {
        Assert.Equal("- あ\n- い", Rag.CollapseDuplicateLines("- あ\n- い"));
    }
}

/// <summary>出典スニペットのプレーン化（Markdown記法除去・v2.7.0）</summary>
public class RagPlainSnippetTests
{
    [Fact]
    public void MarkdownSyntax_Stripped()
    {
        var t = "# 【出張規程】出張・旅費規程\n## 日当\n- 国内出張: 1日あたり **1,500円**\n`IT` ヘルプデスク";
        var r = Rag.PlainSnippet(t, 500);
        Assert.DoesNotContain("#", r);
        Assert.DoesNotContain("**", r);
        Assert.DoesNotContain("`", r);
        Assert.Contains("・国内出張: 1日あたり 1,500円", r);
        Assert.Contains("IT ヘルプデスク", r);
    }

    [Fact]
    public void InlineHeadingsInConcatenatedChunk_Stripped()
    {
        // チャンク化で改行が失われた連結テキスト中の「##」も除去（2026-10-31実測）
        var t = "【人事規程】慶弔休暇規程## 慶弔休暇の日数- 結婚: 3日（入籍日から1年以内に取得）";
        var r = Rag.PlainSnippet(t, 500);
        Assert.DoesNotContain("##", r);
        Assert.Contains("慶弔休暇規程 慶弔休暇の日数- 結婚: 3日", r);
    }

    [Fact]
    public void NewlinesCollapsed_SpacesNormalized()
    {
        var r = Rag.PlainSnippet("行A\n\n行B\n行C", 500);
        Assert.Equal("行A 行B 行C", r);
    }

    [Fact]
    public void TruncatedWithEllipsis()
    {
        var r = Rag.PlainSnippet(new string('あ', 200), 10);
        Assert.Equal(new string('あ', 10) + "…", r);
    }
}

/// <summary>回答キャッシュの質問一致判定（生質問埋め込み・2026-11-01実測の誤ヒット対策）</summary>
public class RagCacheQuestionMatchTests
{
    [Fact]
    public void HighCosine_Paraphrase_Hit()
    {
        // 言い換えペア（実測0.97〜0.99）は生質問の埋め込みでヒット（合成ベクトルcos≈0.999）
        var a = new float[] { 0.995f, 0.1f };
        var b = new float[] { 0.99f, 0.14f };
        Assert.True(Rag.CacheQuestionMatch(b, "国内出張の日当はいくらですか", "国内出張の日当は1日あたりいくらですか", a));
    }

    [Fact]
    public void LowerCosine_DangerousPair_Miss()
    {
        // 危険ペア（実測0.93・国内/海外で答えが違う）は閾値未満でヒットしない（合成ベクトルcos≈0.96）
        var a = new float[] { 0.995f, 0.1f };
        var b = new float[] { 0.93f, 0.37f };
        Assert.False(Rag.CacheQuestionMatch(b, "国内出張の日当はいくらですか", "海外出張の日当はいくらですか", a));
    }

    [Fact]
    public void LegacyRow_ExactNormalizedQuestion_Only()
    {
        // 旧行（raw_emb無し）は正規化質問の完全一致でのみ許可
        Assert.True(Rag.CacheQuestionMatch(null, "国内出張の日当はいくらですか?", "国内出張の日当はいくらですか", null));
        Assert.False(Rag.CacheQuestionMatch(null, "国内出張の日当はいくらですか", "海外出張の日当はいくらですか", null));
    }
}
