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
