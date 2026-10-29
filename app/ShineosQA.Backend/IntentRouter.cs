using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>回答ルート。DifyのQuestion Classifierに相当するが、LLMは使わずルールで判定する
/// （分類のためのLLM呼び出しはCPUで1〜数秒かかり、低遅延の目的に反するため）。
/// 決定論ルート（DrawingMeta/Jisseki）は高精度パターンのみ。外れたらDocQaへフォールバックする。</summary>
public enum AnswerRoute
{
    DocQa,        // 通常のRAG
    DrawingMeta,  // 図面台帳（drawing_meta）からの決定論回答
    DrawingCompare, // 同一図番の改訂差分（drawing_meta）
    Jisseki,      // 実績表（jisseki_meta）からの決定論回答
    MultiPart,    // 複合質問（現時点ではDocQaと同じRAG。分解はPhase C）
    DrawingQa,    // 図面意図のRAG（注記・公差など）
    Clarify,      // 短すぎる/曖昧
}

/// <summary>ルート判定結果。Zuban/Attributes は決定論ルートの実行に使う。</summary>
public sealed record RouteDecision(
    AnswerRoute Route,
    double Confidence,
    string Reason,
    string? Zuban = null,
    IReadOnlyList<DrawingAttribute>? Attributes = null);

/// <summary>図面台帳の属性（質問語 → drawing_meta 列）。</summary>
public sealed record DrawingAttribute(string Key, string Label);

/// <summary>入力の意図分類（純粋関数）。DB・LLM・時刻に依存しないため単体テストで固定できる。</summary>
public static partial class IntentRouter
{
    // 図面台帳で答えられる属性。複数指定（「材質と改訂」）も許す
    private static readonly (Regex Re, string Key, string Label)[] AttributePatterns =
    {
        (MaterialRegex(), "zairyo", "材質"),
        (HinmeiRegex(), "hinmei", "品名"),
        (RevisionRegex(), "revision", "改訂"),
        (ScaleRegex(), "scale", "尺度"),
        (ApprovedAtRegex(), "approved_at", "承認日"),
        (ZubanLabelRegex(), "zuban_raw", "図番"),
    };

    // 図面台帳では答えられない（本文・図面の内容を読む必要がある）語。
    // これらを含む質問はDocQa/DrawingQaへ倒し、決定論ルートで誤って単純化しない。
    private static readonly Regex ComplexRegex = new(
        @"公差|寸法|径|穴|重量|注記|手順|方法|なぜ|理由|比較|一覧|すべて|全般|見積|原価|仕入|納期|納入|数量|合計|平均|客先|検査|品質|規格|材質証明|ミルシート|熱処理|表面|粗さ|ねじ|加工",
        RegexOptions.Compiled);

    private static readonly Regex JissekiRegex = new(
        @"見積|原価|仕入|納期|納入|実績|コスト",
        RegexOptions.Compiled);

    private static readonly Regex CompareRegex = new(
        @"比較|違い|差分|変更点|何が変わ|どこが変わ|旧版|新版",
        RegexOptions.Compiled);

    // 複合質問の目安。現時点ではルートを分けず、将来の分解パイプラインの判定に使う
    private static readonly Regex MultiPartRegex = new(
        @"それぞれ|両方|および|ならびに|と比べ|比較(して|する)|すべて|一覧|いくつ|何日|何件|合計|平均|項目|誰に|窓口|内線|禁止|入社|対象とならない|どんな|挙げて|関係する",
        RegexOptions.Compiled);

    [GeneratedRegex(@"材質|材料|ザイリョウ|マテリアル", RegexOptions.Compiled)]
    private static partial Regex MaterialRegex();

    [GeneratedRegex(@"品名|名称|部品名|何の部品", RegexOptions.Compiled)]
    private static partial Regex HinmeiRegex();

    [GeneratedRegex(@"改訂|リビジョン|版数|何版|第[0-9]+版", RegexOptions.Compiled)]
    private static partial Regex RevisionRegex();

    [GeneratedRegex(@"尺度|縮尺|スケール", RegexOptions.Compiled)]
    private static partial Regex ScaleRegex();

    [GeneratedRegex(@"承認日|作成年月日|作成日|図面の日付", RegexOptions.Compiled)]
    private static partial Regex ApprovedAtRegex();

    [GeneratedRegex(@"図番|図面番号|ドラ番", RegexOptions.Compiled)]
    private static partial Regex ZubanLabelRegex();

    public static RouteDecision Classify(string message)
    {
        var text = (message ?? "").Trim();
        if (text.Length == 0)
            return new(AnswerRoute.Clarify, 1.0, "empty");
        if (text.Length <= 3)
            return new(AnswerRoute.Clarify, 0.9, "too-short");

        var zubans = DrawingIngest.ZubansIn(text);
        var attrs = FindDrawingAttributes(text);
        bool complex = ComplexRegex.IsMatch(text);

        // 同一図番の改訂比較（「改訂AとBの違いは？」「改訂Bの変更点は？」）
        if (zubans.Count > 0 && CompareRegex.IsMatch(text))
            return new(AnswerRoute.DrawingCompare, 0.9, "drawing-compare", zubans[0]);

        // 図番＋属性のみ（公差・寸法・見積など、表題欄の外側を聞いていない）
        if (zubans.Count > 0 && attrs.Count > 0 && !complex)
            return new(AnswerRoute.DrawingMeta, 0.95, "drawing-attribute",
                zubans[0], attrs);

        // 図番＋実績語（材質など表題欄の属性を同時に聞いていない）
        if (zubans.Count > 0 && attrs.Count == 0 && JissekiRegex.IsMatch(text))
            return new(AnswerRoute.Jisseki, 0.9, "jisseki", zubans[0]);

        if (Rag.HasDrawingIntent(text))
            return new(AnswerRoute.DrawingQa, 0.85, "drawing-intent");

        if (MultiPartRegex.IsMatch(text))
            return new(AnswerRoute.MultiPart, 0.6, "multi-part-marker");

        return new(AnswerRoute.DocQa, 0.5, "default");
    }

    /// <summary>質問に含まれる図面属性を出現順に返す（重複なし）。</summary>
    public static List<DrawingAttribute> FindDrawingAttributes(string text)
    {
        var found = new List<(int Index, DrawingAttribute Attr)>();
        foreach (var (re, key, label) in AttributePatterns)
        {
            var m = re.Match(text);
            if (m.Success) found.Add((m.Index, new DrawingAttribute(key, label)));
        }
        return found.OrderBy(x => x.Index).Select(x => x.Attr).ToList();
    }
}
