using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>対象ガード（t74/t76型「対象置換」捏造の生成前防御）。トリガ条件・対象抽出・保守性を検証</summary>
public class GuardTests
{
    // 実障害の再現: QA_list.md（パスワード再発行）が取り込まれている状況
    private const string SourceWithPasswordQa =
        "【ITサポート】パスワードを忘れた場合は？ A: パスワードのリセットは IT ヘルプデスク（内線 1234）にご連絡ください。本人確認のうえ、再発行いたします。";

    [Fact]
    public void TargetMissing_InsuranceReissue_AgainstPasswordSource_IsTrue()
    {
        // t74: 「健康保険証」が文書に無い手続き質問 → ガード発動（転用回答を防ぐ）
        Assert.True(Rag.ProcedureTargetMissing(
            "健康保険証を再発行する方法を教えてください",
            new[] { SourceWithPasswordQa }));
    }

    [Fact]
    public void TargetMissing_PersonalTravel_IsTrue()
    {
        // t76: 「個人旅行」は旅費規程に無い → ガード発動
        Assert.True(Rag.ProcedureTargetMissing(
            "個人旅行の費用を経費で落とすことはできますか",
            new[] { "出張の交通費は電車・バスで実費支給、タクシーは原則禁止。宿泊費の上限は1泊あたり15,000円" }));
    }

    [Fact]
    public void TargetMissing_PresentObject_IsFalse()
    {
        // 対象が文書に現れる通常の手続き質問 → ガードしない
        Assert.False(Rag.ProcedureTargetMissing(
            "会議室の予約方法と大会議室の収容人数を教えてください",
            new[] { "会議室は予約システムから予約する。大会議室の定員は20名" }));
    }

    [Fact]
    public void NonProcedureQuestion_IsNeverGuarded()
    {
        // トリガ語のない質問（口語・条件質問）はガード対象外。s07の言い換え（長期出張）も含む
        Assert.False(Rag.ProcedureTargetMissing(
            "出張の申請はいつまでにすればよいですか。長期出張の場合は？",
            new[] { "出張" })); // 対象語が無くても非手続き質問ならガードしない
        Assert.False(Rag.ProcedureTargetMissing(
            "結婚したら休みもらえるって聞いたけど何日もらえるの？",
            new[] { "慶弔休暇" }));
    }

    [Fact]
    public void TargetMissing_AllPresent_IsFalse()
    {
        // 複数対象のうち1つでも文書に現れれば通す（保守的）。無い対象だけでは拒否しない
        Assert.False(Rag.ProcedureTargetMissing(
            "パスワードの再発行と健康保険証の再発行の方法",
            new[] { SourceWithPasswordQa })); // パスワードは有る／健康保険証は無い → 通す
    }

    [Fact]
    public void TargetObject_RequiresThreeOrMoreChars()
    {
        // 2文字の対象（費用・経費など）は抽出しない → 対象語なし → ガードしない
        Assert.False(Rag.ProcedureTargetMissing(
            "費用を経費で落とすことはできますか",
            new[] { "旅費規程" }));
        // 対象語が1つも抽出できない質問（カタカナのみ等）もガードしない
        Assert.False(Rag.ProcedureTargetMissing(
            "マイカー通勤できますか",
            new[] { "旅費規程" }));
    }

    [Fact]
    public void ZubanTarget_NormalizedMatch()
    {
        // 英数字対象は正規形・大小無視で照合（st1042a ↔ ST-1042A）→ 文書に現れるのでガードしない
        Assert.False(Rag.ProcedureTargetMissing(
            "st1042aの検査方法は？",
            new[] { "ST-1042A の検査基準: 外観検査" }));
        // 図番そのものが文書に無い手続き質問は拒否へ
        Assert.True(Rag.ProcedureTargetMissing(
            "zz9999の検査方法は？",
            new[] { "ST-1042A の検査基準: 外観検査" }));
    }
}

public class GuardRegressionTests
{
    [Fact]
    public void T18_ContactMethodSuffix_NotTreatedAsTarget()
    {
        // t18回帰: 「連絡方法」は聞き方であり対象ではない。文書は「電話で所属長へ連絡」としか書かない
        Assert.False(Rag.ProcedureTargetMissing(
            "忌引きで緊急に休む場合の連絡方法を教えてください",
            new[] { "忌引きで緊急に取得する場合は、電話で所属長へ連絡したうえで事後届けを出してよい" }));
    }
}

public class GuardTriggerTests
{
    [Fact]
    public void T76_PotentialForm_TriggersGuard()
    {
        // 118セットの実質問（可能形「落とせますか」）でもガードが発火すること
        Assert.True(Rag.ProcedureTargetMissing(
            "個人旅行の費用を経費で落とせますか",
            new[] { "出張の交通費は電車・バスで実費支給。宿泊費の上限は1泊あたり15,000円" }));
    }

    [Fact]
    public void TriggerAdditions_DoNotFalseFire_OnColloquialOrPresentTargets()
    {
        // れますか/せますか 追加による過剰発火の確認: 対象語が抽出できない・文書に現れる場合は通す
        Assert.False(Rag.ProcedureTargetMissing(
            "テレワークでも働けますか",
            new[] { "在宅勤務（テレワーク）の対象業務は企画業務など" }));
        Assert.False(Rag.ProcedureTargetMissing(
            "電車とバスは支給されますか",
            new[] { "交通費は電車・バスで実費支給" }));
    }
}
