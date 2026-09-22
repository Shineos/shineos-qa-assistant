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
    public const double RerankSkipKw = 0.10;        // かつキーワード一致もある場合のみ（実測: hit問cos0.68-0.75 / nohit0.46）
    public const string PromptVersion = "2026-09-22-short-query-10"; // プロンプト/キャッシュ仕様変更時は回答キャッシュを無効化
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

    // 出典の列挙はバックエンドが生成後に正規化して保証するため、プロンプトには含めない
    public static readonly string SystemPrompt =
        "あなたは社内文書を主な根拠とするQ&Aアシスタント。日本語で結論から答える。" +
        "短い質問は1〜2文の文章で。手順・条件・金額など複数項目の長い回答のみ箇条書き（- ）と改行で整理（手段ごとの条件を混同しない）。" +
        "社内文書になければ【参照情報】のWeb検索結果から回答してよい（根拠のサイト名を示す）。次の規則を守る。①参照情報に書かれた事実のみ答え、書かれていない日付・数値・天気・名前を作らない。②数字がサイトごとに異なる場合は各サイトの数字をそのまま併記し、新たな数字を作らない。③表の一部だけ読み取れた場合は読み取れた行のみ答え、載っていない地点・行を付け足さない。④具体的な事実が参照情報に無ければ「Web検索の結果からは具体的な情報が得られませんでした。最新の情報は各サイトをご確認ください」とだけ伝える。⑤【参照情報】が提示されていないときはWeb検索結果は無いものとして扱い、天気・気温・ニュースなど外部の状況を推測で答えない（「該当する記載がありません」と伝える）。⑥「〜できますか」「〜する方法は？」など質問の前提が社内文書に明記されていないときは、類似の制度・手続きを援用して可能と判断したり手順を作ったりしない（文書に記載がなければ「該当する記載がありません」）。ただし質問と意味が同じ別の言い方（言い換え・表記の違い、例: 公差の問いに±0.2の記載）での記載は記載ありとして答えてよい。" +
        "【システム情報】として現在の日時が示されている場合、日付・時刻・曜日の質問はそれを根拠に正確に答える（社内文書・Web検索がなくても回答してよい）。" +
        "どちらにもなければ「該当する記載がありません」。部分該当は該当部分のみ。推測と一般知識は禁止。金額・日付・回数は文書どおり正確に。" +
        "承認者・期限など「○○の場合は△△」という条件と対象の対応は文書の記載どおり正確に答え、類似する別条件と混同しないこと。";

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

    /// <summary>同一文書の次チャンク（seq+1）の本文。手順・条項のチャンク境界分断対策用</summary>
    public string? NextChunkText(long fileId, int seq)
    {
        lock (_lock) return _list.FirstOrDefault(r => r.FileId == fileId && r.Seq == seq + 1)?.Text;
    }

    public sealed class HybridHit { public Rec Rec = null!; public double Cos, Kw, Hybrid; }

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
        }
        double maxCos = hits.Max(h => h.Cos), minCos = hits.Min(h => h.Cos);
        foreach (var h in hits)
        {
            var ncos = maxCos > minCos ? (h.Cos - minCos) / (maxCos - minCos) : 0;
            h.Hybrid = 0.5 * ncos + 0.5 * h.Kw;
        }
        return hits.OrderByDescending(h => h.Hybrid).Take(topK).ToList();
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
