using System.Text;
using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>RAG検索・プロンプト構築（実測済みv2-fastパイプラインの忠実実装）。
/// 根拠: docs/latency-verification.md（スニペット240字・リランクtop8→2・ガード閾値-2.0・キャッシュ閾値0.97）</summary>
public static partial class Rag
{
    public const double GuardThreshold = -5.0;      // NO-HIT判定（nohit実測≤-7.8、口語表現の関連質問が-2前後になり得るため-5.0に設定。hit誤ガード< nohit分離を両立）
    public const double AnswerCacheCos = 0.97;      // 回答キャッシュ判定
    public const int SnippetMaxChars = 240;         // 事実切断を防ぐ関連文中心スニペット
    public const int RerankPool = 8;                // top8未満だと関連chunkが候補外に漏れる（実測）
    public const double RerankSkipCos = 0.62;       // 高信頼ショートカット: ベクトル一致が強ければリランク(~2秒)を省略
    public const double RerankSkipKw = 0.40;        // かつキーワード一致も強い場合のみ（0.50→0.40: 高cos質問のリランクを省略しTTFTを短縮。
                                                    // 品質は18問・図面10問・118問のゲートで確認する。弱一致は従来どおりリランクへ）
    public const double MinAcceptKw = 0.15;         // 弱一致ガード(バックストップ): リランカーが無い環境での最終防波堤。
                                                    // kw≈0は語彙的に無関係なので、リランクを経由しない採択でも該当なし拒否へ。
                                                    // 対象外条件は IsWeakMatch 参照（図面質問/Web文脈/時間感応/リランク済み）
    public const double RerankHighConf = 2.0;       // リランク高信頼判定（≥なら1件注入・<なら2件採用）。グレー帯(<2.0)で
                                                    // かつキーワード一致もほぼ無い(maxKw<MinAcceptKw)組み合わせは
                                                    // IsLowConfidenceWeak で該当なし拒否へ（偽出典の最後の経路）
    public const string PromptVersion = "2026-10-30-cross-doc-21"; // プロンプト/キャッシュ仕様変更時は回答キャッシュを無効化
    // (-15→-16: SystemPromptの「手順・条件・金額など」が1.7Bに項目ラベル形式（金額:なし 等）の
    //  テンプレ回答を誘発する実測（d06図面の注記）のため、カテゴリ列挙をやめた表現に変更)
    // (-13→-14: 横断質問の期間フレーズ直接保証（SearchPhrase）・連絡先列挙指示・拒否文言の定型化を
    //  回答文脈に加えたため、旧プロンプトで生成したキャッシュを無効化する)
    // (-2: 混同禁止規則を独立文に復元。1文への圧縮合并で「長期出張の承認者」問が所属長/部長を混同する回帰が発生したため)
    // (-3: 規則⑤追加。Web検索OFF時に【参照情報】が無いのに「参照情報によると晴天」等の外部状況を捏造する揺らぎ(s13)への防御)
    // (-4〜-6: 規則⑥(前提質問の捏造防御・t76解消)と⑦(対象置換防御・t74へは効果薄)を試行
    // -7: ⑦を撤回。規則リスト肥大により複数事実回答の網羅性が低下(s09/s11)したため。t74(対象置換)はmain既存の既知問題として
    //     生成後バリデーションガードでの対応が正攻法(backlog))
    // -9: 意図不明な短い質問(例: 「テスト」)に無関係な文書断片を列挙する問題への防御をSystemPrompt末尾に追加。
    //     実測: 「テスト」はREADME「テスト用ナレッジサンプル」にkw=1.00一致するため検索ガードを通過し、quick 1.7Bが
    //     無関係な事実を列挙していた。外部調査(arXiv 2412)では文脈無関係時の明示的拒否指示が棄却率を上げると報告
    // -10: -9は正常質問(タクシー条件)を自己矛盾させた(「22時以降は可能」と述べた直後に「明記されていないため禁止」)ため撤回。
    //     代わりにChatFlowで短クエリ(3文字以下)のときのみ動的に確認促進の注記を付ける(通常質問には影響しない)
    // -13: 繰り返し出力クセ（冒頭文が1回繰り返される・2026-10-19実測）への対応 — 生成後の決定的畳み込み
    //     (CollapseDuplicateSentences)を導入。penalty系サンプリングは不使用。
    //     ※SystemPromptへの「繰り返すな」指示は試したが撤回: 1.7Bが手順リスト生成時に冒頭を飛ばして
    //      再掲する変形出力（s04実測・18問セットで基準比-1）を誘発したため。畳み込みだけで対処する

    /// <summary>相対日時語を含む質問の判定（「今日は何日」等）。これらは回答が日付で変わるため
    /// 回答キャッシュの対象外とする（ChatFlowで読み書き両方をスキップ）。
    /// 天気・ニュース・市況など現実の状態で変わる質問も対象（古い回答の恒久化防止）</summary>
    [GeneratedRegex(@"今日|本日|昨日|明日|明後日|一昨日|今週|来週|先週|今月|来月|先月|今年|昨年|去年|来年|現在|日付|曜日|何日|何時|時刻|天気|気温|天候|降水確率|気象|ニュース|株価|為替|レート")]
    public static partial Regex TimeSensitiveQuestion();

    /// <summary>質問の図面意図: 「図面/図番」を含むか図番パターン（キャプチャチップからの質問含む）。
    /// 図面チャンクは寸法数値のノイズでキーワード一致が希薄になり濃密な文書チャンクに埋もれるため
    /// （実図面検証cr02: JIS B 0405の質問が規格一覧文書に取って代わられた）、この判定で図面チャンクを
    /// 候補プールへ優先的に残す（ChatFlowのブーストで使用）</summary>
    public static bool HasDrawingIntent(string question) =>
        question.Contains("図面") || question.Contains("図番") || DrawingIngest.ZubanRegex().IsMatch(question);

    /// <summary>弱一致判定(バックストップガード)。参照文脈が弱いまま生成すると、モデルが学習知識(一般知識)で質問に答え、
    /// 出典行の正規化によって偽の出典が付く実害がある（10-01実測: KB外の「富士山の高さは?」が会議室文書
    /// kw=0.37でリランクを素通りし「約3776米 出典:会議室文書」）。識別の主役はリランクスコア(GuardThreshold)で、
    /// 本関数はリランクを経由しない採択(リランカー未導入環境など)での最終防波堤としてkw≈0を拒否する。
    /// 図面質問（寸法数値ノイズでkwが構造的に希薄）/Web文脈あり/時間感応/リランク済み(ガード通過済み)は対象外</summary>
    public static bool IsWeakMatch(double maxChosenKw, bool hasWebContext, bool timeSensitive, bool drawingIntent, bool rerankValidated) =>
        !hasWebContext && !timeSensitive && !drawingIntent && !rerankValidated && maxChosenKw < MinAcceptKw;

    /// <summary>低信頼×低Kwの組み合わせ判定。リランクはno-hit帯(GuardThreshold)を逃れたものの確信が持てない
    /// (top1&lt;RerankHighConf)うえ、キーワード一致もほぼ無い(maxKw&lt;MinAcceptKw)ケース。
    /// 表層のみ類似の無関係チャンクが回答文脈に入り、一般知識+偽出典で答えられる最後の経路を塞ぐ。
    /// 実測(2026-10-19・bge-reranker-v2-m3): 正解はtop1≥+7.3、無関係/表層類似は≤-10.6と強く分離するため、
    /// このグレー帯に落ちる半端な組み合わせのみを拒否しても正解への影響は小さい。
    /// 純言い換えヒット(top1≥RerankHighConf)とキーワード確認済み(maxKw≥MinAcceptKw)は通す。
    /// 対象外(Web文脈/時間感応/図面質問)は呼び出し側で判定（rerankガードと同じ免除条件）</summary>
    public static bool IsLowConfidenceWeak(double rerankTop1, double maxChosenKw, bool rerankValidated) =>
        rerankValidated && rerankTop1 < RerankHighConf && maxChosenKw < MinAcceptKw;

    /// <summary>システムプロンプト末尾に付与する現在日付行。「今日は何日」等の質問に
    /// モデルが正確に答えられるようにする（学習時点で知識が止まっているため）。
    /// 時刻は含めない: ChatFlowの前置きキャッシュ（プリフィックスキャッシュ）効率のため
    /// system部は1日単位で固定する</summary>
    public static string CurrentDateLine()
    {
        var now = DateTime.Now;
        var dow = "日月火水木金土"[(int)now.DayOfWeek];
        return $"\n現在の日付: {now:yyyy年M月d日}（{dow}曜日）。日付・曜日・時期（「今日」「今月」等）の質問はこの日付を基準に回答すること。";
    }

    /// <summary>日付感応質問のuserメッセージに付与するシステム日時（時刻込み）。
    /// system側の日付行が日単位で固定なのに対し、こちらはリクエストごとの正確な時刻を与える
    /// （「今何時？」等に対応）。userメッセージは前置きキャッシュ対象外なので何度更新しても影響なし</summary>
    public static string SystemInfoLine()
    {
        var now = DateTime.Now;
        var dow = "日月火水木金土"[(int)now.DayOfWeek];
        return $"【システム情報】現在の日時: {now:yyyy年M月d日}（{dow}曜日） {now:HH:mm}。このPCのシステム時計による。";
    }

    [GeneratedRegex(@"[^。．.\n]+[。．.]?")]
    private static partial Regex SentenceRegex();

    /// <summary>列挙・横断質問に引用された期間・時刻フレーズ（「7日」「2週間」「22時」等）の抽出。
    /// NFKCで全角数字を半角化し、チャンク本文とのContains照合に使える実表記で返す。無ければnull。
    /// 純関数（テスト直接検証）。「22時」等の時刻は索引側が「22:00」表記のこともあるため
    /// 呼び出し側（ChatFlow）が数字接頭辞（「22」）の直参照も併せて行う</summary>
    public static string? EnumerationPhrase(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = DurationPhraseRegex().Match(message.Normalize(NormalizationForm.FormKC));
        return m.Success ? m.Value.Trim() : null;
    }

    [GeneratedRegex(@"[0-9]{1,3}(日間|日|週間|か月|ヶ月|ケ月|箇月|年間|年|時)")]
    private static partial Regex DurationPhraseRegex();

    /// <summary>拒否応答の文言統一（自然文→定型）。出典行を含まず、最初の文が「参照情報に記載がない」系の
    /// 拒否そのものの場合、冒頭を「該当する記載がありません。」へ置き換えた全文を返す。
    /// 部分回答（〜ませんが、◯◯は記載があります）・すでに定型の拒否はnull（変更なし）。
    /// 実測(t73): 同じ拒否でも「〜教えていただけないです」等の揺れが出て、UI表示・拒否判定が文言依存になる
    /// 純関数（テスト直接検証）</summary>
    public static string? NormalizeRefusalWording(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Contains("出典")) return null;
        var lines = text.Split('\n');
        var first = lines[0].Trim();
        if (first.Length == 0 || first.Contains("該当する記載がありません")) return null;
        // 部分回答の保護: 1文目に「〜ませんが/ただし」等の但し書きがあれば拒否そのものではない
        if (System.Text.RegularExpressions.Regex.IsMatch(first, @"ませんが|ありませんが|ないが、|ですが、|ただし|けど|けれど")) return null;
        // 日時応答＋KB欠けの複合（s13実測・timeSensitive質問の天気等）: 1文目がシステム日時の応答で、
        // 以降に「参照情報に記載されていない」系の打ち切り文が続く場合、KB側の答えは無し。
        // 全体を定型の拒否へ統一する（「今日は何日」等の純粋な日時応答は打ち切り文が無いため対象外）
        if (first.StartsWith("現在の日時は")
            && System.Text.RegularExpressions.Regex.IsMatch(text, @"(参照情報|社内文書|社内ナレッジ)[^。\n]{0,20}(記載されて(いない|いません)|含まれて(いない|いません)|提供できません|見つかりません)"))
            return "該当する記載がありません。";
        bool refusalFirst = System.Text.RegularExpressions.Regex.IsMatch(first,
            @"^[^。\n]{0,16}(参照情報|社内文書|社内ナレッジ|提供された文書|ナレッジ)[^。\n]{0,30}(記載が(ない|ありません)|記載されて(いません|おりません)|含まれて(いません|おりません)|見当たりません|見つかりません|確認できません)"
            + @"|^(申し訳ございません|申し訳ありません)[^。\n]{0,40}(見つかり|該当|わかりかね)"
            + @"|[^。\n]{0,20}(答えられません|回答できません|お答えできません|教えられません|わかりかねます)[。]?$");
        if (!refusalFirst) return null;
        var rest = string.Join('\n', lines.Skip(1));
        return "該当する記載がありません。" + (rest.Length > 0 ? "\n" + rest : "");
    }

    // ---- 回答の水増し・すり替え検出（v2.7.0・未実施3項目の対応）----

    /// <summary>箇条書き項目の根拠判定: 項目文の内容が参照文脈にほぼ出てこない場合を未根拠として検出する。
    /// 小型モデルがKBに無い詳細を付け加える水増し（t47実測）への対策。
    /// 文字2-gramの被覆率で判定（簡単な言い換えは通す・保守的）。未根拠と判定した元の行を返す。
    /// 箇条書き（- / ・ / 数字.）と箇条書きでない行の両方を対象とする</summary>
    public static List<string> UngroundedListItems(string answer, string reference, double minCoverage = 0.2)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(reference)) return result;
        var refNorm = reference.Replace(" ", "").Replace("\u3000", "").Replace("\r", "").Replace("\n", "");
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Trim();
            if (!(line.StartsWith("- ") || line.StartsWith("・") || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d{1,2}[.．)）]"))) continue;
            if (line.Length < 8) continue;
            var cov = BigramCoverage(line, refNorm);
            if (cov < minCoverage) result.Add(line);
        }
        return result;
    }

    /// <summary>文字2-gramの被覆率（0〜1）。ひらがなのみの2-gramは共通語のため除外</summary>
    private static double BigramCoverage(string item, string reference)
    {
        int hit = 0, total = 0;
        for (int i = 0; i + 2 <= item.Length; i++)
        {
            var g = item.Substring(i, 2);
            if (g.All(c => c >= 'ぁ' && c <= 'ゖ')) continue; // ひらがなのみは除外
            total++;
            if (reference.Contains(g)) hit++;
        }
        return total == 0 ? 1 : (double)hit / total;
    }

    /// <summary>QA見出しの一致チェック: QA集形式文書（見出し＋A: 形式）から回答する際、
    /// 質問文に対応する見出しが参照文脈に存在するかを確認する。存在しない場合、
    /// モデルが近い話題のQAを元に回答を生成してしまう（s14実測: 副業→在宅勤務のすり替え）ため、
    /// 呼び出し側で拒否または再生成を行う。見出しが1つも抽出できない場合（QA形式でない文書）は
    /// false（判定対象外）を返す</summary>
    public static bool QaHeadingMismatch(string question, string reference)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(reference)) return false;
        // 外国語クエリ（かななし・vi/zh/id/tl）は判定対象外（2026-11-01回帰実測: 多言語32問が
        // 31→22に悪化。外国語の内容語は日本語の内容語抽出（一-龥ァ-ヶー）で拾えず、常に
        // 「見出し不一致」と誤判定してクロスリンガル救済の回答を拒否に潰していた）。
        // 見出し照合は日本語前提の設計のため、外国語は従来どおりクロスリンガル経路に任せる
        if (IsForeignQuery(question)) return false;
        // QA集形式の見出し（## 【タグ】...？）を抽出。チャンク化で改行が失われたテキストでは
        // 行頭判定（StartsWith("## ")）が先頭行しか見ないため、QA形式と誤判定して
        // すり替えガードが不発火する実測（s14再発・2026-10-31）→ 改行の有無に依存しない
        // 正規表現抽出に変更（「## 【タグ】...？」パターンを直接マッチ）
        var headings = System.Text.RegularExpressions.Regex.Matches(reference, @"##\s*(【[^】]+】[^#\n]{0,80}？)")
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();
        if (headings.Count == 0) return false; // QA形式でない
        // 質問の正規形（かな＋キーワード）と見出しの重なりで判定。
        // 6文字照合だけでは「経費精算は最後に誰に…」（qa heading誤発火・cmp23実測でt44/t41/t26が
        // 拒否に置換）のような表現ゆれを不一致と誤判定するため、質問の内容語（漢字/カタカナ2字以上）
        // が見出しに1つでも含まれれば一致とみなす救済を追加（「経費精算」→「経費精算の手順は？」等）
        var qKeywords = System.Text.RegularExpressions.Regex.Matches(question, @"[一-龥ァ-ヶー]{2,}")
            .Select(m => m.Value).Distinct().ToList();
        var qNorm = QuestionNorm.Normalize(question);
        foreach (var h in headings)
        {
            if (qKeywords.Count > 0 && qKeywords.Any(h.Contains)) return false; // 一致する見出しがある
            var hNorm = QuestionNorm.Normalize(h);
            if (hNorm.Length >= 4 && (qNorm.Contains(hNorm[..Math.Min(6, hNorm.Length)]) || hNorm.Contains(qNorm[..Math.Min(6, qNorm.Length)])))
                return false; // 一致する見出しがある
        }
        return true;
    }

    /// <summary>チャンクテキストがQA集形式（## 【タグ】...？ 見出し＋回答）か。
    /// 改行の有無に依存しない（チャンク化で改行が失われたテキストでも判定可能）。
    /// 混合KBの過剰発火防止に使用</summary>
    public static bool IsQaFormatChunk(string? text)
        => !string.IsNullOrEmpty(text)
            && System.Text.RegularExpressions.Regex.Matches(text, @"##\s*(【[^】]+】[^#\n]{0,80}？)").Count > 0;

    /// <summary>混合KBのQA見出しガード過剰発火防止: QA形式でない文書のチャンクに質問の内容語
    /// （漢字／カタカナ2字以上の連続）が1つでも含まれる場合、その文書が質問の根拠を持ちうるため
    /// QA見出し照合をスキップする。QA_list以外の文書が答えを持つ質問（日当・私物USB等）の
    /// 正解回答が「見出し不一致」で拒否に置換される事故の防止（2026-10-31実測）</summary>
    public static bool NonQaChunkGrounded(string? question, IEnumerable<string> nonQaTexts)
    {
        var keywords = System.Text.RegularExpressions.Regex.Matches(question ?? "", @"[一-龥ァ-ヶー]{2,}")
            .Select(m => m.Value).Distinct().ToList();
        if (keywords.Count == 0) return false; // ひらがなのみの質問は判定へ進む（保守的に発火側）
        foreach (var t in nonQaTexts)
            if (t != null && keywords.Any(k => t.Contains(k))) return true;
        return false;
    }

    /// <summary>出典スニペット用のプレーン化（v2.7.0）: KBチャンク由来のMarkdown記法
    /// （見出し#・太字**・コード`・箇条書き-）が出典パネルに生表示されるのを解消する。
    /// 見出し記号を除去し、太字・コードの装飾をはがし、行頭の箇条書き記号を「・」に統一して
    /// 改行を空白に丸める。maxLenで切り詰めて「…」を付ける。SnippetとTextの両方に同一変換を
    /// 適用する前提（出典モーダルの該当箇所ハイライトは部分一致のため）</summary>
    public static string PlainSnippet(string? text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Split('\n');
        var parts = new List<string>();
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^#{1,6}\s*", "");          // 見出し（行頭）
            t = System.Text.RegularExpressions.Regex.Replace(t, @"##+", " ");                // 見出し（連結テキスト中・「規程## 慶弔休暇の日数」等）
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\*\*(.+?)\*\*", "$1");      // 太字
            t = System.Text.RegularExpressions.Regex.Replace(t, @"__(.+?)__", "$1");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"`([^`]*)`", "$1");          // コード
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^[-*•]\s+", "・");          // 箇条書き
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^\d{1,2}[.．)）]\s*", "・"); // 番号付き箇条書き
            parts.Add(t.Trim());
        }
        var s = System.Text.RegularExpressions.Regex.Replace(string.Join(" ", parts), @"\s{2,}", " ").Trim();
        if (s.Length > maxLen) s = s[..maxLen] + "…";
        return s;
    }

    /// <summary>非連続の完全重複行を除去（v2.7.0・下書き単発生成のブロック反復対策）。
    /// 1.7BモデルがFAQ形式の項目を2巡書くクセ（実測: 7項目中「申請方法/日数/緊急連絡」の
    /// 3項目が4項目目を挟んで完全同一文で反復）は、既存の畳み込み（連続同一行・A+A半分割）では
    /// 捉われない。行を正規化（空白・箇条書き記号の差を吸収）した集合で判定し、
    /// **箇条書き項目行**の2回目以降の出現を除去する。見出し・出典行・空行は保持。
    /// 同じ行を意図的に2回書く価値のある下書きは実務上ないため、完全一致のみ除去（言い換えは保持）</summary>
    public static string? CollapseDuplicateLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keep = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var norm = NormalizeListLine(line);
            // 見出し・出典行（"!"接頭）・短い行は重複判定対象外（常に保持）
            if (norm.Length < 8 || norm.StartsWith("!")) { keep.Add(line); continue; }
            if (!seen.Add(norm)) continue; // 既出の箇条書き項目 → 除去
            keep.Add(line);
        }
        return string.Join('\n', keep);
    }

    /// <summary>重複判定用の行正規形: 空白・全角空白を除去し、箇条書き記号（- ・ 数字. 数字)）
    /// を「-」に揃える。見出し（#）・出典行は「!」接頭で区別（重複判定対象外マーキング用）。
    /// 8字未満はそのまま（呼び出し側で保持）</summary>
    private static string NormalizeListLine(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("#")) return "!" + t;
        if (t.StartsWith("出典")) return "!" + t;
        if (t.Length == 0) return "";
        var body = System.Text.RegularExpressions.Regex.IsMatch(t, @"^[-・•]")
            ? t[1..]
            : System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{1,2}[.．)）]\s*")
                ? System.Text.RegularExpressions.Regex.Match(t, @"^\d{1,2}[.．)）]\s*(.*)$").Groups[1].Value
                : t;
        // 約物（読点・句点等）も吸収する: 「…から行う。」と「…から行う。」（読点差のみ）を
        // 同一とみなす（2026-10-31実測: 読点1個の差だけで重複判定をすり抜けた）
        return "-" + System.Text.RegularExpressions.Regex.Replace(
            body.Replace(" ", "").Replace("\u3000", ""), "[、。！？]", "").Trim();
    }

    /// <summary>疑問詞の要点外し判定（v2.7.0・t44実測「最後に誰に承認申請するのか」→手順のみ列挙）。
    /// 質問に疑問詞（誰/いつ/どこ/いくら/何日/何時/何円）があるのに、回答がその要素を含まない
    /// 場合にtrue。定型拒否は要点に合致する回答扱い（false）。要素の判定は広い表現で行い、
    /// 言い換え回答を誤って落とさないよう保守的にする（純関数・テスト直接検証）</summary>
    public static bool FocusMismatch(string? question, string? answer)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) return false;
        var a = answer.Replace(" ", "").Replace("\u3000", "").Replace("\r", "");
        if (a.Contains("該当する記載がありません") || a.Contains("わかりかねます")) return false;
        static bool AnyOf(string text, params string[] keys) => keys.Any(text.Contains);
        if (question.Contains("誰"))
            return !AnyOf(a, "部長", "課長", "係長", "工場長", "所長", "上司", "所属長", "上長", "社長", "役員", "担当", "責任者", "管理者", "申請者", "本人", "窓口", "総務", "人事", "サポート", "デスク")
                && !System.Text.RegularExpressions.Regex.IsMatch(a, @"[一-龥]{2,3}(さん|氏)");
        if (question.Contains("いつ") || question.Contains("何日") || question.Contains("何時") || question.Contains("何年"))
            return !System.Text.RegularExpressions.Regex.IsMatch(a, @"[0-9０-９]{1,4}(日|月|年|時|分)|[0-9０-９]{1,2}:[0-9０-９]{2}|翌|当日|前日|週間|営業日|まで|曜日|毎");
        if (question.Contains("どこ"))
            return !AnyOf(a, "システム", "メニュー", "画面", "ページ", "ポータル", "サイト", "フォーム", "部署", "フロア", "受付", "窓口", "フォルダ", "サーバ", "ドライブ", "場所", "階");
        if (question.Contains("いくら") || question.Contains("何円"))
            return !System.Text.RegularExpressions.Regex.IsMatch(a, @"[0-9０-９]");
        return false;
    }

    /// <summary>定型拒否（「該当する記載がありません」系）かどうか。QA見出しガードでの
    /// 置き換え要否の判定に使う（すでに拒否している回答は触らない）</summary>
    public static bool IsRefusal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        return text.Contains("該当する記載がありません") || text.Contains("記載がありません") || text.Contains("わかりかねます");
    }

    /// <summary>繰り返し出力の畳み込み。2段構え:
    /// (1) 行内の連続同一文を除去（空白差のみの一致も同一扱い）
    /// (2) 回答全体が同一ブロックの2回反復（A+A）になっていれば1回分へ畳む —
    ///     箇条書きブロック丸ごと再現されるクセ（2026-10-19 図面注記・v2.3.2実機E2E実測:
    ///     「- 溶接…。- 仕上げ…。」×2）は文単位の連続判定では捉わらない（A,B,A,Bで直前一致が無いため）。
    ///     末尾のモデル自書き出典行は退避して本文だけで判定し、後から再結合する。
    /// サンプリングpenalty（repeat_penalty等）は文書どおりの数値出力を歪める恐れがあるため使わない</summary>
    public static string? CollapseDuplicateSentences(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 連続する同一行の畳み込み（v2.5.3）: 章生成で同一の箇条書き行が2連続することがある
        // （人事規程章の同一行×2を実測）。トリム+空白正規化で一致した完全同一行のみ畳む
        var collapsed = new List<string>();
        string? prevLine = null;
        foreach (var raw in text.Split('\n'))
        {
            var norm = raw.Replace(" ", "").Replace("\u3000", "").TrimEnd('\r');
            if (norm.Length > 0 && norm == prevLine) continue;
            collapsed.Add(raw);
            prevLine = norm.Length > 0 ? norm : null;
        }
        var parts = collapsed.ToArray();
        for (var i = 0; i < parts.Length; i++) parts[i] = CollapseSentencesInLine(parts[i]);
        return CollapseRepeatedBlock(string.Join("\n", parts));
    }

    /// <summary>回答の見た目を整える（v2.7.0）: 小型モデルが参照チャンクの見出しを写す問題への防御。
    /// ①行頭の文書名ラベル（【経費精算】【ITサポート】【人事規程】等）を除去
    /// ②正規化後に同一となる行の重複を除去（同じ規定を2回列挙する実測: 結婚休暇・パスワード）
    /// 全文注入モード（小規模KB）で発生しやすい。事実には触れず、表記だけを整える（純関数）</summary>
    public static string SanitizeAnswer(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var label = DocumentLabelRegex();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outLines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = label.Replace(raw, "").TrimEnd('\r');
            // 箇条書き番号・記号は重複判定から除く（「3. 領収書」と「5. 領収書」を同一視）
            var norm = ListMarkerRegex().Replace(line, "").Replace(" ", "").Replace("\u3000", "").Trim();
            if (norm.Length == 0)
            {
                // 空行は1つに畳む（見出し除去で空になった行の連続を防ぐ）
                if (outLines.Count > 0 && outLines[^1].Trim().Length == 0) continue;
                outLines.Add("");
                continue;
            }
            // 同一内容の行（空白・ラベル・番号差のみ）は2回目以降を捨てる
            if (!seen.Add(norm)) continue;
            outLines.Add(line);
        }
        return string.Join("\n", outLines).Trim();
    }

    /// <summary>行頭の文書名・区分ラベル（参照チャンクの見出しをモデルが写したもの）</summary>
    [GeneratedRegex(@"^\s*(【(?:経費精算|ITサポート|人事規程|総務規程|出張規程|社内文書|社内文書（全文）|社内ナレッジ)】)+\s*")]
    private static partial Regex DocumentLabelRegex();

    /// <summary>箇条書きの番号・記号（重複判定のため表記差を吸収する）</summary>
    [GeneratedRegex(@"^\s*(?:[-・*]|\d{1,2}[.．)）])\s*")]
    private static partial Regex ListMarkerRegex();

    /// <summary>A+A反復の判定: 本文長の半分で分割し、空白を除去した正規形が一致すれば前半のみ返す。
    /// 偶然前後半が一致する通常文は事実上なく、一致=反復と見なしてよい</summary>
    private static string CollapseRepeatedBlock(string text)
    {        var body = text;
        var tail = "";
        // 出典行との区切り（\n\n出典 / \n出典）を本文から丸ごと退離させる（改行数を保持）
        var idx = text.LastIndexOf("\n\n出典", StringComparison.Ordinal);
        if (idx < 0) idx = text.LastIndexOf("\n出典", StringComparison.Ordinal);
        if (idx > 0) { body = text[..idx]; tail = text[idx..]; }
        var half = body.Length / 2;
        if (half < 4) return text;
        var first = body[..half];
        var second = body[half..];
        if (NormForRepeat(first).Length > 0 && NormForRepeat(first) == NormForRepeat(second))
            return first.TrimEnd() + tail;
        return text;
    }

    private static string NormForRepeat(string s) => s.Replace(" ", "").Replace("\u3000", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");

    /// <summary>文書要約（v2.4.1）用の全文組み立て。チャンクをseq順に連結し maxChars 上限で打ち切る。
    /// 打ち切った場合は注記付き（誠実な表示: 大型文書は先頭部分の要約になることを明示する）。
    /// 純関数（テスト直接検証）</summary>
    public static (string Text, bool Truncated) SelectSummaryText(IReadOnlyList<string> chunks, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var c in chunks)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(c);
            if (sb.Length >= maxChars) break;
        }
        if (sb.Length <= maxChars) return (sb.ToString(), false);
        return (sb.ToString(0, maxChars) + "\n\n※長い文書のため、先頭から一部のみを要約しています。", true);
    }

    private static string CollapseSentencesInLine(string line)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c != '。' && c != '．' && c != '.') continue;
            if (c == '.' && i > start && i + 1 < line.Length && char.IsDigit(line[i - 1]) && char.IsDigit(line[i + 1])) continue;
            parts.Add(line[start..(i + 1)]);
            start = i + 1;
        }
        if (start < line.Length) parts.Add(line[start..]);
        if (parts.Count < 2) return line;
        var sb = new StringBuilder(line.Length);
        string? prev = null;
        foreach (var p in parts)
        {
            var norm = p.Replace(" ", "").Replace("\u3000", "");
            if (norm.Length > 0 && norm == prev) continue; // 直前の文と同一（空白差のみ）→ 落とす
            sb.Append(p);
            if (norm.Length > 0) prev = norm;
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"[\u3040-\u30FF\u4E00-\u9FFF]+")]
    private static partial Regex CjkRegex();

    /// <summary>記号を含む英数連結（図番・型番: ST-1042A / KB_305/2 等）。3番目の選択肢は1文字英数字の単独トークン。
    /// 入力はNFKC正規化済みのため全角記号は登場しない</summary>
    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9\-_/]{0,30}[A-Za-z0-9]|[A-Za-z0-9]")]
    private static partial Regex JoinedAlnumRegex();

    /// <summary>図番・型番の正規形（NFKC→小文字→英数以外除去）。「A-1234」「A1234」「Ａ−１２３４」を同一キー化する。
    /// 索引（ChunkIndex）とクエリ（ChatFlow）の両方がTokenizeを通るため、両側へ自動適用される</summary>
    public static string NormalizeZuban(string s)
    {
        var n = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(n.Length);
        foreach (var ch in n)
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
        return sb.ToString();
    }

    // ---- 対象ガード（生成前の前提検証・t74/t76型「対象置換」捏造の防御） ----

    /// <summary>手続き・可否を問う質問の判定（対象ガードの適用条件。一般名詞質問・口語質問には適用しない）。
    /// 可能形（〜せますか/れますか）・可能表現（可能ですか）も対象（t76「落とせますか」実測）</summary>
    [GeneratedRegex(@"できますか|方法|やり方|手順|手続き|再発行|利用したい|使いたい|可能ですか|せますか|れますか|ていいですか|てよいですか")]
    public static partial Regex ProcedureQuestion();

    /// <summary>質問の対象語: 助詞（を/は/が/の/って）直前の3文字以上の漢語または英数字。
    /// ただし方法/手順/手続きで終わる語（質問の聞き方を示す語）は対象から除外する
    /// （「連絡方法は?」の方法を対象と誤認すると、文書が「電話で連絡」とだけ書く場合に誤拒否になる・t18実測）</summary>
    [GeneratedRegex(@"([\u4E00-\u9FFF]{3,}|[A-Za-z0-9][A-Za-z0-9\-_/]{2,})(?=を|は|が|の|って)")]
    private static partial Regex TargetObjectRegex();

    private static bool IsTargetObject(string run) =>
        !(run.EndsWith("方法") || run.EndsWith("手順") || run.EndsWith("手続き"));

    /// <summary>対象ガード: 手続き質問の対象語が取得文書のどれにも現れない場合true。
    /// 例「健康保険証を再発行する方法」で文書に「健康保険証」が無ければ、QA文書の
    /// パスワード再発行等の類似手続きを転用した回答になる前に拒否へ倒せる。
    /// 対象語が1つでも文書に現れればfalse、対象語が抽出できない質問もfalse（保守的）。
    /// 呼び出し側は手続き質問（ProcedureQuestion）かつWeb検索未使用の場合に限る</summary>
    public static bool ProcedureTargetMissing(string question, IEnumerable<string> sourceTexts)
    {
        if (!ProcedureQuestion().IsMatch(question)) return false;
        var hay = string.Join('\n', sourceTexts).Normalize(NormalizationForm.FormKC);
        // 英数字対象（図番）は索引側と同じ正規形で照合するため、hay側も英数のみの小文字列を作る
        // （区切りは削除: 索引側の NormalizeZuban が "A-1234"→"a1234" を含むため）
        var hayAlnum = string.Concat(hay.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant));
        bool any = false;
        foreach (var m in TargetObjectRegex().Matches(question.Normalize(NormalizationForm.FormKC)).Cast<Match>())
        {
            var run = m.Groups[1].Value;
            if (!IsTargetObject(run)) continue;
            any = true;
            var present = char.IsAsciiLetterOrDigit(run[0])
                ? hayAlnum.Contains(NormalizeZuban(run), StringComparison.Ordinal)
                : hay.Contains(run, StringComparison.Ordinal);
            if (present) return false;
        }
        return any;
    }

    /// <summary>手続き質問の対象語のうち、参照テキスト群に見つからないものを列挙する（対象語リルート用）。
    /// ProcedureTargetMissingと違い「1つでも見つかれば通し」の早期リターンをせず、欠落対象をすべて返す。
    /// 手続き質問でない場合は空。ChatFlowはこれが非空のとき、プール内の対象語含有チャンクへ根拠を
    /// 差し替える（リランクの意味的選択が対象語を字面通り含まないケースへの対応・s11実測）</summary>
    public static List<string> MissingProcedureTargets(string question, IEnumerable<string> sourceTexts)
    {
        var result = new List<string>();
        if (!ProcedureQuestion().IsMatch(question)) return result;
        var hay = string.Join('\n', sourceTexts).Normalize(NormalizationForm.FormKC);
        var hayAlnum = string.Concat(hay.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant));
        foreach (var m in TargetObjectRegex().Matches(question.Normalize(NormalizationForm.FormKC)).Cast<Match>())
        {
            var run = m.Groups[1].Value;
            if (!IsTargetObject(run)) continue;
            var present = char.IsAsciiLetterOrDigit(run[0])
                ? hayAlnum.Contains(NormalizeZuban(run), StringComparison.Ordinal)
                : hay.Contains(run, StringComparison.Ordinal);
            if (!present) result.Add(run);
        }
        return result;
    }

    /// <summary>手続き質問の対象語のうち、参照テキスト群に見つかったものを返す（対象語リルートのスコアリング用）。
    /// 手続き質問でない場合は空</summary>
    public static List<string> ContainedProcedureTargets(string question, IEnumerable<string> sourceTexts)
    {
        var found = new List<string>();
        if (!ProcedureQuestion().IsMatch(question)) return found;
        var hay = string.Join('\n', sourceTexts).Normalize(NormalizationForm.FormKC);
        var hayAlnum = string.Concat(hay.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant));
        foreach (var m in TargetObjectRegex().Matches(question.Normalize(NormalizationForm.FormKC)).Cast<Match>())
        {
            var run = m.Groups[1].Value;
            if (!IsTargetObject(run)) continue;
            var present = char.IsAsciiLetterOrDigit(run[0])
                ? hayAlnum.Contains(NormalizeZuban(run), StringComparison.Ordinal)
                : hay.Contains(run, StringComparison.Ordinal);
            if (present) found.Add(run);
        }
        return found;
    }

    /// <summary>回答中の「捏造疑いのある数値」（出典・質問のいずれにも存在しない数値）を抽出する。
    /// 生成後バリデーションガード（quick 1.7B等の小型モデルが文書に無い数値を混同・作話する実測への対策）。
    /// 対象は2桁以上の整数と小数（1桁・箇条書き番号は誤検出のため除外）。カンマ区切り・全角数字は
    /// 正規化して比較し、出典・質問のテキストに同一数値があれば捏造とみなさない</summary>
    public static List<string> FabricatedNumbers(string answer, string evidence)
    {
        static string Norm(string s) => new string(s.Normalize(NormalizationForm.FormKC)
            .Select(c => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c).ToArray())
            .Replace(",", "").Replace("、", "");
        var ev = Norm(evidence);
        var ans = Norm(answer);
        var bad = new List<string>();
        foreach (Match m in Regex.Matches(ans, @"(?<![\d.])\d{2,}(?:\.\d+)?(?![\d.])|(?<![\d.])\d\.\d+(?![\d.])"))
        {
            var n = m.Value;
            if (ev.Contains(n, StringComparison.Ordinal)) continue;
            if (bad.All(b => b != n)) bad.Add(n);
        }
        return bad;
    }

    /// <summary>日本語バイグラム＋英数字トークン（検証済みトークナイザと同一仕様）
    /// ＋記号結合英数の正規形トークン（図番表記ゆれ吸収・T5）。既存トークンも併存するため旧挙動は崩れない。
    /// 冒頭のNFKCで全角英数・全角記号を半角化する（「ＳＴ－１０４２」など全角入力の図番も一致させる）</summary>
    public static List<string> Tokenize(string s)
    {
        s = s.Normalize(NormalizationForm.FormKC);
        var tokens = new List<string>();
        foreach (var m in CjkRegex().Matches(s).Cast<Match>())
        {
            var v = m.Value;
            if (v.Length == 1) tokens.Add(v);
            else for (int i = 0; i < v.Length - 1; i++) tokens.Add(v.Substring(i, 2));
        }
        foreach (var m in JoinedAlnumRegex().Matches(s).Cast<Match>())
        {
            var v = m.Value.ToLowerInvariant();
            tokens.Add(v);
            if (v.Length > 1 && (v.Contains('-') || v.Contains('_') || v.Contains('/')))
            {
                // 旧仕様トークン（区切りで切った英数連結: st / 1042a）も併存させ、既存の一致挙動を壊さない
                foreach (var piece in Regex.Split(v, "[^a-z0-9]"))
                    if (piece.Length > 0) tokens.Add(piece);
                var norm = NormalizeZuban(v);
                if (norm.Length >= 2) tokens.Add(norm); // "st-1042a" → "st1042a"
            }
        }
        foreach (var t in MfgTerms.CanonicalTokens(s))
            if (!tokens.Contains(t)) tokens.Add(t);
        return tokens;
    }

    /// <summary>文末境界チャンカー（350字/オーバーラップ50）</summary>
    public static List<string> Chunk(string text, int target = 350, int overlap = 50)
    {
        var chunks = new List<string>();
        var cur = new StringBuilder();
        foreach (var m in SentenceRegex().Matches(text).Cast<Match>())
        {
            var s = m.Value.Trim();
            if (s.Length == 0) continue;
            if (cur.Length + s.Length > target && cur.Length > 0)
            {
                chunks.Add(cur.ToString().Trim());
                var tail = cur.Length > overlap ? cur.ToString(cur.Length - overlap, overlap) : "";
                cur.Clear(); cur.Append(tail);
            }
            cur.Append(s);
        }
        if (cur.ToString().Trim().Length > 0) chunks.Add(cur.ToString().Trim());
        return chunks;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / Math.Sqrt(na * nb);
    }

    /// <summary>文＋元の区切り文字（句点・改行）を含めて切り出す正規表現。Snippet専用:
    /// 区切り込みで切り出せば再結合が元テキストの完全な部分文字列になり、出典モーダルの
    /// 該当箇所ハイライト（indexOf照合）が必ず成功する（SentenceRegexは句点/改行を捨てるため不使用）</summary>
    [GeneratedRegex(@"[^。．.\n]+[。．.]?\n?")]
    private static partial Regex SnippetSentenceRegex();

    /// <summary>クエリ関連文を中心としたスニペット抽出。
    /// 語彙が重ならない場合（例: 日本語質問↔英語PDF）は関連文を特定できないため全文を返す
    /// （スニペット化による誤ガード回帰の防止: latency-verification §5）</summary>
    public static string Snippet(string text, IReadOnlySet<string> queryTokens, int max = SnippetMaxChars)
    {
        if (text.Length <= max) return text;
        var sents = SnippetSentenceRegex().Matches(text).Cast<Match>().Select(m => m.Value).ToArray();
        if (sents.Length == 0) return text.Substring(0, max);
        int bestI = 0, bestScore = 0;
        for (int i = 0; i < sents.Length; i++)
        {
            var sTok = Tokenize(sents[i]).ToHashSet();
            int sc = queryTokens.Count(t => sTok.Contains(t));
            if (sc > bestScore) { bestScore = sc; bestI = i; }
        }
        if (bestScore == 0) return text; // 重複なし→クロスリンガル等→全文
        int lo = bestI, hi = bestI;
        var sb = new StringBuilder(sents[bestI]);
        while (sb.Length < max)
        {
            // 区切り文字込みで結合するため、再結合結果は常に元テキストの部分文字列になる
            if (hi + 1 < sents.Length && sb.Length + sents[hi + 1].Length <= max) { hi++; sb.Append(sents[hi]); }
            else if (lo - 1 >= 0 && sb.Length + sents[lo - 1].Length <= max) { lo--; sb.Insert(0, sents[lo]); }
            else break;
        }
        return sb.ToString();
    }

    /// <summary>フレーズ中心のスニペット抽出（列挙質問用）。フレーズを含む文を中心にmax字へ窓を広げる。
    /// トークン重複では中心がずれ、「7日前」等の目的行が窓外に切れるため（t92実測）、
    /// フレーズを含む文があればそこを中心にする。altPhrase（「22時」↔「22:00」等の数字表記）を指定すると
    /// フレーズ不一致時に代替表記でも中心を試す。どちらも無ければ従来のTokenize中心へ。
    /// 純関数（テスト直接検証）</summary>
    public static string SnippetAround(string text, string phrase, IReadOnlySet<string> queryTokens, int max = SnippetMaxChars, string? altPhrase = null)
    {
        if (text.Length <= max) return text;
        var sents = SnippetSentenceRegex().Matches(text).Cast<Match>().Select(m => m.Value).ToArray();
        int center = Array.FindIndex(sents, s => s.Contains(phrase, StringComparison.Ordinal));
        if (center < 0 && !string.IsNullOrEmpty(altPhrase))
            center = Array.FindIndex(sents, s => s.Contains(altPhrase, StringComparison.Ordinal));
        if (center < 0) return Snippet(text, queryTokens, max);
        var sb = new StringBuilder(sents[center]);
        int lo = center, hi = center;
        while (sb.Length < max)
        {
            if (hi + 1 < sents.Length && sb.Length + sents[hi + 1].Length <= max) { hi++; sb.Append(sents[hi]); }
            else if (lo - 1 >= 0 && sb.Length + sents[lo - 1].Length <= max) { lo--; sb.Insert(0, sents[lo]); }
            else break;
        }
        return sb.ToString();
    }

    /// <summary>外国語クエリ判定（クロスリンガル救済の発火条件）。ひらがな/カタカナを含まないクエリを
    /// 外国語とみなす（vi/id/tlはLatin表記、zhは漢字のみ。日本語の通常質問・拒否質問はかなを含むため
    /// 救済の対象外 = 従来の拒否動作を維持する）。純関数（テスト直接検証）</summary>
    public static bool IsForeignQuery(string? s) =>
        !string.IsNullOrEmpty(s) && !System.Text.RegularExpressions.Regex.IsMatch(s, @"[\u3040-\u30FF]");

    /// <summary>列挙質問の転写ヒント: フレーズ（と数字代替表記）を含む「行」を抜き出す。
    /// 「22時」と「22:00」のように表記の違う2事実をモデルが統合できず片方を落とす実測（t97・quick/standard
    /// の両階級）のため、該当行をそのまま提示して転記させる。文単位に分割しない（「22:00」等の
    /// 小数点で文分割が壊れるため・実測）。各150字・最大maxLines件。純関数（テスト直接検証）</summary>
    public static List<string> ExtractPhraseLines(string text, string phrase, string? altPhrase, int maxLines = 3, int maxChars = 150)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(phrase)) return lines;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            bool hit = line.Contains(phrase, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(altPhrase) && line.Contains(altPhrase, StringComparison.Ordinal));
            if (!hit) continue;
            var trimmed = line.Length > maxChars ? line[..maxChars] : line;
            var norm = trimmed.Replace(" ", "").Replace("　", "");
            if (lines.Any(l => l.Replace(" ", "").Replace("　", "") == norm)) continue;
            lines.Add(trimmed);
            if (lines.Count >= maxLines) break;
        }
        return lines;
    }

    // 出典の列挙はバックエンドが生成後に正規化して保証するため、プロンプトには含めない。
    // v2.6.3: 規則を約950字から約400字へ圧縮（プリフィル短縮＝TTFT改善）。圧縮で失われる保護は
    // 生成後ガード〔数値捏造→再生成・繰り返し→再生成・低信頼/弱一致→該当なし〕が担保する
    public static readonly string SystemPrompt =
        "あなたは社内文書を根拠とするQ&Aアシスタント。日本語で結論から答える。" +
        "複数の項目にまたがる回答は箇条書きで整理する。" +
        "【参照情報】に書かれた事実のみ答え、書かれていない数値・日付・名前は作らない。表は読み取れた行のみ答える。" +
        "言い換えや表記の違い（例: 公差の問いに±0.2の記載）は記載ありとして扱う。" +
        "質問の前提が参照情報に無い場合は手順を創作せず「該当する記載がありません」と答える。部分該当は該当部分のみ。" +
        "【参照情報】にWeb検索結果が含まれる場合はその内容から根拠のサイト名を添えて答え、サイトごとに数字が異なれば併記する。" +
        "含まれない場合、天気・時事など外部の状況は推測で答えない。" +
        "【システム情報】の日時が示されていれば日付・時刻の質問はそれで答えてよい。" +
        "推測と一般知識は禁止。金額・日付・回数は文書どおり正確に。条件と対象の対応を混同しない。";

    /// <summary>回答言語の指示（多言語対応）。参照情報は日本語のまま、回答だけ指定言語で出させる。
    /// 未対応・jaは空文字（従来プロンプトを変えない＝prefixキャッシュ維持）。</summary>
    public static string AnswerLanguageLine(string? lang) => lang switch
    {
        "en" => "\n\n【回答言語】英語（English）で回答してください。出典の文書名は原文のままでよい。",
        "vi" => "\n\n【回答言語】ベトナム語（Tiếng Việt）で回答してください。出典の文書名は原文のままでよい。",
        "zh" => "\n\n【回答言語】中国語（简体中文）で回答してください。出典の文書名は原文のままでよい。",
        "id" => "\n\n【回答言語】インドネシア語（Bahasa Indonesia）で回答してください。出典の文書名は原文のままでよい。",
        "tl" => "\n\n【回答言語】タガログ語（Tagalog）で回答してください。出典の文書名は原文のままでよい。",
        _ => "",
    };

    public static string BuildContext(IReadOnlyList<(string doc, string snippet)> docs, string? webContext)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var d in docs) sb.Append($"【文書{++i}: {d.doc}】{d.snippet}\n");
        if (!string.IsNullOrEmpty(webContext)) sb.Append($"【Web検索結果】{webContext}\n");
        return sb.ToString();
    }
}

/// <summary>メモリ上のチャンク索引（起動時にDBからロード、取り込み時に更新）</summary>
public sealed class ChunkIndex
{
    public sealed record Rec(long Id, long FileId, string FileName, int Seq, string Text, float[] Emb, HashSet<string> Tokens);

    private readonly List<Rec> _list = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _list.Count; } }

    public void LoadFrom(Db db)
    {
        var rows = db.LoadReadyChunks();
        lock (_lock) _list.Clear();
        foreach (var row in rows)
        {
            var emb = BytesToFloats(row.Emb);
            var rec = new Rec(row.ChunkId, row.FileId, row.FileName, row.Seq, row.Text, emb, Rag.Tokenize(row.Text).ToHashSet());
            lock (_lock) _list.Add(rec);
        }
    }

    public void AddRange(string fileName, long fileId, IEnumerable<(int seq, string text, float[] emb)> items)
    {
        lock (_lock)
        {
            foreach (var (seq, text, emb) in items)
                _list.Add(new Rec(0, fileId, fileName, seq, text, emb, Rag.Tokenize(text).ToHashSet()));
        }
    }

    public void RemoveFile(long fileId)
    {
        lock (_lock) _list.RemoveAll(r => r.FileId == fileId);
    }

    /// <summary>指定ファイルの全チャンクをseq順で返す（文書要約 v2.4.1: 検索top-kを経ない全文注入に使用）</summary>
    public List<Rec> ByFile(long fileId)
    {
        lock (_lock) return _list.Where(r => r.FileId == fileId).OrderBy(r => r.Seq).ToList();
    }

    /// <summary>同一文書の次チャンク（seq+1）の本文。手順・条項のチャンク境界分断対策用</summary>
    public string? NextChunkText(long fileId, int seq)
    {
        lock (_lock) return _list.FirstOrDefault(r => r.FileId == fileId && r.Seq == seq + 1)?.Text;
    }

    public sealed class HybridHit { public Rec Rec = null!; public double Cos, Kw, Hybrid; public int Canonical; }

    /// <summary>ハイブリッド検索（ベクトル0.5＋キーワード0.5・min-max正規化）</summary>
    public List<HybridHit> Search(float[] queryEmb, IReadOnlySet<string> queryTokens, int topK)
    {
        List<HybridHit> hits;
        lock (_lock) hits = _list.Select(r => new HybridHit { Rec = r, Cos = Rag.Cosine(queryEmb, r.Emb), Kw = 0 }).ToList();
        if (hits.Count == 0) return hits;
        foreach (var h in hits)
        {
            int matches = queryTokens.Count(t => h.Rec.Tokens.Contains(t));
            h.Kw = (double)matches / Math.Max(1, queryTokens.Count);
            h.Canonical = queryTokens.Count(t => MfgTerms.IsCanonicalToken(t) && h.Rec.Tokens.Contains(t));
        }
        double maxCos = hits.Max(h => h.Cos), minCos = hits.Min(h => h.Cos);
        foreach (var h in hits)
        {
            var ncos = maxCos > minCos ? (h.Cos - minCos) / (maxCos - minCos) : 0;
            h.Hybrid = 0.5 * ncos + 0.5 * h.Kw;
        }
        return hits.OrderByDescending(h => h.Hybrid).Take(topK).ToList();
    }

    /// <summary>キーワードのみの検索（複合・列挙質問の再現率補強用）。ベクトル上位に漏れた
    /// 「7日」「22時」等の語を含むチャンクを拾う。全件走査だが社内文書規模では数ms。</summary>
    public List<HybridHit> SearchKeyword(IReadOnlySet<string> queryTokens, int topK)
    {
        if (queryTokens.Count == 0) return new List<HybridHit>();
        List<Rec> copy;
        lock (_lock) copy = _list.ToList();
        return copy.Select(r =>
            {
                int matches = queryTokens.Count(t => r.Tokens.Contains(t));
                return new HybridHit
                {
                    Rec = r,
                    Kw = (double)matches / Math.Max(1, queryTokens.Count),
                    Canonical = queryTokens.Count(t => MfgTerms.IsCanonicalToken(t) && r.Tokens.Contains(t)),
                };
            })
            .Where(h => h.Kw > 0)
            .OrderByDescending(h => h.Kw)
            .Take(topK)
            .ToList();
    }

    /// <summary>フレーズ（部分文字列）を含むチャンクを直接返す（列挙質問の期間語「7日」「2週間」等の
    /// 取りこぼし保証用）。出現回数が多い順・同数はDBロード順（安定）。Cos/Kw/Hybridは未計算の0のまま
    /// （呼び出し側はフレーズ含有を別基準で優先する）。全件走査だが線形Containsで数ms</summary>
    public List<HybridHit> SearchPhrase(string phrase, int take)
    {
        if (string.IsNullOrEmpty(phrase)) return new List<HybridHit>();
        List<Rec> copy;
        lock (_lock) copy = _list.ToList();
        return copy.Select(r => (Rec: r, Count: CountOccurrences(r.Text, phrase)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Take(take)
            .Select(x => new HybridHit { Rec = x.Rec })
            .ToList();
    }

    /// <summary>正規トークン（多言語別名↔日本語KBの語彙一致）を含むチャンクを直接返す。
    /// 外国語クエリはベクトル検索でも日本語チャンクがプールに入らないことがあるため（vi/tl実測）、
    /// ガード救済時に正規トークンで辞書を直接引く。一致トークン数が多い順。Cos/Kwは0のまま。
    /// 全件走査だが線形で数ms</summary>
    public List<HybridHit> SearchCanonical(IEnumerable<string> canonicalTokens, int take)
    {
        var tokens = canonicalTokens.Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        if (tokens.Count == 0) return new List<HybridHit>();
        List<Rec> copy;
        lock (_lock) copy = _list.ToList();
        return copy.Select(r => (Rec: r, Count: tokens.Count(t => r.Tokens.Contains(t))))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Take(take)
            .Select(x => new HybridHit { Rec = x.Rec })
            .ToList();
    }

    private static int CountOccurrences(string text, string phrase)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(phrase, i, StringComparison.Ordinal)) >= 0) { n++; i += phrase.Length; }
        return n;
    }

    public static byte[] FloatsToBytes(float[] f)
    {
        var b = new byte[f.Length * 4];
        for (int i = 0; i < f.Length; i++) Array.Copy(BitConverter.GetBytes(f[i]), 0, b, i * 4, 4);
        return b;
    }

    public static float[] BytesToFloats(byte[] b)
    {
        var f = new float[b.Length / 4];
        for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToSingle(b, i * 4);
        return f;
    }
}
