using System.Text;
using System.Text.Json;

namespace ShineosQA.Backend;

/// <summary>下書き作成モード（v2.5.0）: 指示文を章立て（タスク）に分解し、参照チャンクを注入して
/// 章ごとに生成する決定的パイプライン。Difyのワークフロー設計（変数入力→知識検索→アウトライン→
/// 章ごと生成→組み立て）を固定パイプラインとして製品に焼き込んだもの（視覚エディタ不要）。
/// テンプレートは指示文から自動判定（手動指定も可・自由形式あり）</summary>
public static class Draft
{
    public sealed record TemplateDef(string Id, string Label, (string Title, string Goal)[] FallbackOutline,
        string SectionFormat, string Example);

    public static readonly Dictionary<string, TemplateDef> Templates = new()
    {
        ["procedure"] = new("procedure", "手順書", new[]
        {
            ("準備", "必要なもの・前提条件・対象者"),
            ("手順", "作業の手順を実行順に箇条書き"),
            ("確認", "完了の確認方法と注意点"),
        },
        "作業手順は番号付き箇条書き（1. 2. 3.）で実行順に書く。前置きの説明文は書かない。",
        "1. 経費精算システムにログインする\n2. 「新規精算」をクリックし、精算項目を選択して入力する"),
        ["report"] = new("report", "報告書", new[]
        {
            ("概要", "目的と要点"),
            ("詳細", "事実と経緯・数値"),
            ("今後", "対応と今後の予定"),
        },
        "事実と数値を箇条書きで書く。感想や評価を書かない（事実のみ）。",
        "- 宿泊費の上限は1泊あたり15,000円（税込）\n- 上限を超える場合は所属長の承認が必要"),
        ["proposal"] = new("proposal", "提案書", new[]
        {
            ("背景と目的", "課題と狙い"),
            ("提案内容", "内容・条件・見積根拠"),
            ("効果と次のステップ", "期待効果と進め方"),
        },
        "提案の条件と根拠を対にして箇条書きで書く（「〜をする（根拠: ○○）」の形）。",
        "- 過去実績の平均単価を踏まえ見積額を提示する（根拠: 実績表 ST-1042A 150,000円）"),
        ["faq"] = new("faq", "FAQ集", new[]
        {
            ("はじめに", "この文書の対象と使い方"),
            ("よくある質問", "質問と回答（参照文書の記載に基づく）"),
            ("問い合わせ", "わからないときの窓口"),
        },
        "各項目は「- ○○は？」のような質問の行で始め、その直後の行に1〜2文で回答する。質問形式を必ず守る（説明文だけの項目にしない）。",
        "- パスワードを忘れた場合は？\n  ITヘルプデスク（内線1234）に連絡し、本人確認のうえ再発行します。"),
        ["free"] = new("free", "自由形式", new[]
        {
            ("概要", "指示の要点"),
            ("本文", "指示に沿った本文"),
            ("まとめ", "まとめと次のアクション"),
        },
        "要点を箇条書きで書く。見出しは書かない。",
        "- はじめに対象と目的を1行で示す\n- 本題を箇条書きで展開する"),
    };

    /// <summary>指示文からテンプレートを自動判定（純関数）。該当がなければ free。
    /// 手動指定（"auto"以外の有効ID）は呼び出し側で優先する</summary>
    public static string InferTemplate(string instruction)
    {
        var t = instruction ?? "";
        if (Has(t, "手順", "手続き", "作業の流れ", "プロセス書", "マニュアル")) return "procedure";
        if (Has(t, "報告", "レポート", "経緯書", "日報", "週報")) return "report";
        if (Has(t, "提案", "見積書", "企画書", "提案書")) return "proposal";
        if (Has(t, "FAQ", "よくある質問", "質問集")) return "faq";
        return "free";
        static bool Has(string s, params string[] keys) => keys.Any(s.Contains);
    }

    /// <summary>文書作成の意図を検出（2026-10-23・DifyのQuestion Classifier相当）。
    /// ユーザーが作成トグルを押さなくても「〜を作成してください」等の文書生成指示を
    /// 自動で作成パイプラインへ振り分けるための純関数。LLM呼び出しを挟まず正規表現のみで
    /// 判定する（全質問に分類コールを足すとTTFTが伸びるため）。疑問形（〜方法/〜とは）は
    /// 文書作成ではなくQ&Aとして扱う</summary>
    public static bool LooksLikeCreationRequest(string message)
    {
        var t = (message ?? "").Trim();
        if (t.Length < 6) return false;
        // 方法質問（「〜作成方法を教えて」等）は文書生成ではなくQ&A
        if (System.Text.RegularExpressions.Regex.IsMatch(t, "(作成|作り|書き|やり)方|(作成|作る|書く)ことができますか"))
            return false;
        var action = System.Text.RegularExpressions.Regex.IsMatch(
            t, "作成し(て|ます)|作って(くれ|ください|ほしい)?|書いて(くれ|ください|ほしい)?|起草し(て|ます)|作成お願い");
        var docType = System.Text.RegularExpressions.Regex.IsMatch(
            t, "手順書|報告書|提案書|FAQ|よくある質問|議事録|お詫び状|お礼状|案内状|メール文面|マニュアル|仕様書|取扱説明書");
        if (System.Text.RegularExpressions.Regex.IsMatch(t, "とは|とは何|って何\\s*[？?]*$")) return false; // 「報告書とは？」等は用語QA
        return (action && !docType) || (docType && (action || t.EndsWith("して") || t.EndsWith("してください") || t.EndsWith("ください")));
    }

    public sealed record OutlineSection(string Title, string Goal);
    public sealed record OutlineDoc(string Title, List<OutlineSection> Sections);

    /// <summary>アウトラインLLM応答の防御的パース（agent経路と同じ方式）。
    /// {"title":"..","sections":[{"title":"..","goal":".."}]} を期待。
    /// パース失敗/章2未満 → フォールバック章立て。パース成功かつ sections が空配列
    /// → 作成意図なし（空リストを返し、呼び出し側で通常案内へ）</summary>
    public static OutlineDoc ParseOutline(string? llmOutput, TemplateDef fallbackFor, string titleFallback)
    {
        try
        {
            var text = (llmOutput ?? "").Trim();
            int s = text.IndexOf('{'), e = text.LastIndexOf('}');
            if (s >= 0 && e > s) text = text[s..(e + 1)];
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var te) && te.ValueKind == JsonValueKind.String
                ? te.GetString()!.Trim() : "";
            var list = new List<OutlineSection>();
            if (root.TryGetProperty("sections", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                {
                    var st = el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                    var goal = el.TryGetProperty("goal", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()!.Trim() : "";
                    if (st.Length > 0) list.Add(new OutlineSection(st, goal));
                }
            if (list.Count == 0 && root.TryGetProperty("sections", out var a2) && a2.ValueKind == JsonValueKind.Array)
                return new OutlineDoc(string.IsNullOrWhiteSpace(title) ? Clip(titleFallback) : title, new List<OutlineSection>()); // 意図なし
            if (list.Count >= 2)
                return new OutlineDoc(string.IsNullOrWhiteSpace(title) ? Clip(titleFallback) : title, list.Take(6).ToList());
        }
        catch { }
        return new OutlineDoc(Clip(titleFallback), fallbackFor.FallbackOutline.Select(o => new OutlineSection(o.Title, o.Goal)).ToList());

        static string Clip(string s) => s.Length > 40 ? s[..40] : s;
    }

    /// <summary>参照テキストの組み立て（公平分配・純関数）: 各ファイルをファイルあたり
    /// totalCap/n（最低400字）で打ち切り。seedは質問200字/回答800字。ctx=4096に収める設計</summary>
    public static string BuildReferenceText(IReadOnlyList<(string file, string text)> fileTexts, (string question, string answer)? seed, int totalCap = 2400)
    {
        var sb = new StringBuilder();
        int per = fileTexts.Count > 0 ? Math.Max(400, totalCap / fileTexts.Count) : totalCap;
        foreach (var (file, text) in fileTexts)
        {
            var (capped, _) = Rag.SelectSummaryText(new List<string> { text }, per);
            sb.Append($"【参照文書: {file}】\n{capped}\n\n");
        }
        if (seed is ({ } q, { } a))
        {
            sb.Append($"【種となる質問】\n{(q.Length > 200 ? q[..200] : q)}\n\n");
            sb.Append($"【種となる回答】\n{(a.Length > 800 ? a[..800] : a)}\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>章をまたいで重複したFAQ質問項目を除去（v2.5.5）。実測: 参照なし生成で
    /// 同一の「- 質問は？」＋回答が別の章に繰り返された。前方の章で出た質問と
    /// （空白除去・末尾？?を無視して）一致する項目は、後方の章から項目ブロック
    /// （質問行+直後の字下げ回答行）ごと落とす。質問形（？/?を含む箇条書き）のみ対象</summary>
    public static List<(string Title, string Body)> DedupRepeatedQuestions(IReadOnlyList<(string Title, string Body)> sections)
    {
        var seen = new HashSet<string>();
        var result = new List<(string, string)>();
        foreach (var (title, body) in sections)
        {
            var lines = body.Replace("\r", "").Split('\n');
            var kept = new List<string>();
            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                var q = NormalizeQuestion(line);
                if (q is null) { kept.Add(line); i++; continue; }
                if (!seen.Add(q))
                {
                    // 重複: 質問行と直後の字下げ回答行（- で始まらない行）を落とす
                    i++;
                    while (i < lines.Length && lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith("- "))
                        i++;
                    continue;
                }
                kept.Add(line);
                i++;
            }
            // 落とした結果末尾が寂しくならないよう、空行の連続を1つに詰める
            var bodyOut = string.Join("\n", kept).TrimEnd();
            result.Add((title, bodyOut));
        }
        return result;

        static string? NormalizeQuestion(string line)
        {
            var t = line.TrimStart();
            if (!t.StartsWith("- ")) return null;
            var raw = t[2..].Trim();
            // 質問形の判定は末尾の？/?（確実）。「- ○○は？」のような短い質問も対象。
            // 〜は/〜ですで終わる除外は「デジタルデバイスの使用は？」を miss したため廃止
            if (!raw.EndsWith("？") && !raw.EndsWith("?")) return null;
            var q = raw.Replace(" ", "").Replace("\u3000", "").TrimEnd('？', '?');
            return q.Length >= 4 ? q : null;
        }
    }

    /// <summary>①空章の除去（v2.5.8）: 本文がthreshold字未満の章（見出しのみ・実質なし）を落とす。
    /// 実測: 参照なし生成で「承認プロセス」章が見出しのみになった</summary>
    public static List<(string Title, string Body)> RemoveEmptySections(IReadOnlyList<(string Title, string Body)> sections, int threshold = 20)
    {
        return sections.Where(s => s.Body.Replace(" ", "").Replace("\u3000", "").Trim().Length >= threshold).ToList();
    }

    /// <summary>②同一動詞パターンのトリム（v2.5.8）: 章内の箇条書き項目のうち、同一の
    /// 動詞パターン（「○○をXXする」のXX部分）が50%超を占める場合、後半の同パターン項目を落として
    /// 多様性を確保する。実測: 参照なしで「申請書をAに提出/申請書をBに提出…」が92%を占めた</summary>
    public static List<(string Title, string Body)> TrimRepetitivePattern(IReadOnlyList<(string Title, string Body)> sections)
    {
        var result = new List<(string, string)>();
        foreach (var (title, body) in sections)
        {
            var lines = body.Replace("\r", "").Split('\n');
            var bulletIdx = new List<int>(); // 箇条書き項目のインデックス
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].TrimStart().StartsWith("- ")) bulletIdx.Add(i);

            if (bulletIdx.Count < 4) { result.Add((title, body)); continue; } // 項目が少ないなら対象外

            // 各箇条書きから動詞パターンを抽出（FAQ質問形「○○をXXする/した？」と回答形の両方に対応）
            var patterns = new Dictionary<int, string>();
            foreach (var i in bulletIdx)
            {
                var t = lines[i].TrimStart()[2..].Trim();
                // 質問形: 末尾に「XXする？」/「XXした？」が来る → 動詞XXを取り出す
                var m = System.Text.RegularExpressions.Regex.Match(t,
                    @"(提出|返還|承認|確認|申請|登録|保存|削除|変更|更新|設定|利用|使用|作成|発行|受領|却下|差し戻し)[しましたる？?]*$");
                if (m.Success)
                    patterns[i] = m.Groups[1].Value;
            }

            if (patterns.Count == 0) { result.Add((title, body)); continue; }
            // 最頻出パターン
            var top = patterns.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First();
            var topVerb = top.Key;
            var topCount = top.Count();
            // 50%超かつ4個以上なら後半をトリム
            if (topCount >= 4 && topCount * 100 > bulletIdx.Count * 50)
            {
                var keep = new bool[lines.Length];
                for (var i = 0; i < lines.Length; i++) keep[i] = true;
                var removed = 0;
                var seen = 0;
                foreach (var bi in bulletIdx)
                {
                    if (patterns.TryGetValue(bi, out var v) && v == topVerb)
                    {
                        seen++;
                        if (seen > 3) // 同一動詞は最大3項目まで
                        {
                            // この項目（質問行+直後の回答行）を落とす
                            keep[bi] = false;
                            var j = bi + 1;
                            while (j < lines.Length && lines[j].Trim().Length > 0 && !lines[j].TrimStart().StartsWith("- "))
                            {
                                keep[j] = false;
                                j++;
                            }
                            removed++;
                        }
                    }
                }
                if (removed > 0)
                {
                    var keptLines = lines.Where((l, i) => keep[i]).ToArray();
                    result.Add((title, string.Join("\n", keptLines).TrimEnd()));
                    continue;
                }
            }
            result.Add((title, body));
        }
        return result;
    }

    /// <summary>下書き品質の後処理パイプライン（v2.5.8・純関数）:
    /// ①空章除去 → ②FAQ質問重複除去 → ③パターントリム の順に適用</summary>
    public static List<(string Title, string Body)> PostProcess(IReadOnlyList<(string Title, string Body)> sections)
    {
        var r = RemoveEmptySections(sections);
        r = DedupRepeatedQuestions(r);
        r = TrimRepetitivePattern(r);
        return r;
    }

    /// <summary>完成ドラフトのMarkdown組み立て（純関数）</summary>
    public static string AssembleMarkdown(string templateLabel, bool autoInferred, string title,
        IReadOnlyList<(string title, string body)> sections, IReadOnlyList<string> refFiles)
    {
        var sb = new StringBuilder();
        sb.Append($"# {title}\n\n");
        sb.Append($"*種類: {templateLabel}{(autoInferred ? "（自動判定）" : "")}・AIが生成した下書きです。内容を確認のうえ編集してご利用ください*\n\n");
        foreach (var (t, b) in sections)
            sb.Append($"## {t}\n\n{b.Trim()}\n\n");
        if (refFiles.Count > 0)
            sb.Append($"---\n参照: {string.Join("、", refFiles)}\n");
        return sb.ToString().TrimEnd();
    }
}
