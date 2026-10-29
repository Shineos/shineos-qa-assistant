using System.Text.RegularExpressions;

namespace ShineosQA.Backend;

/// <summary>製造業の表記ゆれ・類義語・記号を検索トークンへ展開する。
/// `Rag.Tokenize` はチャンクと質問の両方に使われるため、ここで別名→正規トークンを追加すると
/// 「SUS304」と「ステンレス」、「見積書」と「見積」が同じトークンで一致する。
/// LLMも埋め込みも使わない（CPUマイクロ秒〜数ms）。辞書はコード内に置き、Phase DでJSONパック化する。
/// 注意: ASCIIの短い別名は単語境界で判定する（"ra" が "drawing" に誤マッチするのを防ぐ）。</summary>
public static partial class MfgTerms
{
    private sealed record Group(string Canonical, string[] Aliases);

    private static readonly Group[] Groups =
    {
        new("sus304", new[] { "sus304", "sus-304", "ステンレス", "stainless" }),
        new("s45c", new[] { "s45c", "s45c材", "機械構造用炭素鋼" }),
        new("ss400", new[] { "ss400", "一般構造用圧延鋼" }),
        new("spcc", new[] { "spcc", "冷間圧延鋼板" }),
        new("a5052", new[] { "a5052", "アルミ", "aluminum", "aluminium" }),
        new("adc12", new[] { "adc12", "ダイカスト" }),
        new("scm435", new[] { "scm435", "クロムモリブデン鋼" }),
        new("skd11", new[] { "skd11", "合金工具鋼" }),
        new("c3604", new[] { "c3604", "快削黄銅" }),
        new("pom", new[] { "pom", "ポリアセタール", "ジュラコン" }),
        new("mcny", new[] { "mcナイロン", "mcny", "ナイロン" }),
        new("mekki", new[] { "めっき", "メッキ", "鍍金", "plating" }),
        new("netsushori", new[] { "熱処理", "焼入れ", "焼戻し", "焼なまし", "高周波" }),
        new("youketsu", new[] { "溶接", "welding", "アーク溶接" }),
        new("kousa", new[] { "公差", "tolerance" }),
        new("arasa", new[] { "表面粗さ", "粗さ", "roughness" }),
        new("neji", new[] { "ねじ", "ネジ", "ビス", "ボルト", "screw", "bolt" }),
        new("zuban", new[] { "図番", "図面番号", "ドラ番", "品番" }),
        new("zumen", new[] { "図面", "drawing" }),
        new("zaishitsu", new[] { "材質", "材料", "マテリアル", "material" }),
        new("mitumori", new[] { "見積", "見積書", "見積額", "見積り", "quote", "quotation" }),
        new("genka", new[] { "原価", "コスト", "仕入", "cost" }),
        new("nouki", new[] { "納期", "納入日", "デリバリー", "delivery" }),
        new("suuryo", new[] { "数量", "個数", "ロット", "qty", "quantity" }),
        new("furyou", new[] { "不良", "不具合", "クレーム", "defect" }),
        new("kensa", new[] { "検査", "測定", "試験", "inspection", "test" }),
        new("koutei", new[] { "工程", "プロセス", "process" }),
        new("kyakusaki", new[] { "客先", "顧客", "取引先", "customer" }),
        new("kaizen", new[] { "改善", "カイゼン", "kaizen" }),
        new("anzen", new[] { "安全", "ヒヤリハット", "危険" }),
        new("5s", new[] { "5s", "整理", "整頓", "清掃", "清潔", "躾" }),
        new("iso9001", new[] { "iso9001", "iso 9001" }),
        new("iatf16949", new[] { "iatf16949", "iatf 16949" }),
        new("jis", new[] { "jis", "日本工業規格" }),
        new("cad", new[] { "cad", "dxf", "dwg" }),
        new("tanka", new[] { "単価", "unit price" }),
        new("kousuu", new[] { "工数", "人工", "man-hour" }),
        new("shiyou", new[] { "仕様", "spec", "specification" }),
        new("torai", new[] { "トライ", "試作", "試作部品" }),
    };

    [GeneratedRegex(@"(?<![A-Za-z0-9])ra\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RaRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])rz\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RzRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])m\s*([0-9]{1,2})\s*[x×]\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MetricThreadRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])jis\s*([a-z])\s*([0-9]{3,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex JisRegex();

    /// <summary>文字列に含まれる製造業別名から、検索用の正規トークンを返す（重複なし）。</summary>
    public static IEnumerable<string> CanonicalTokens(string s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in Groups)
        {
            foreach (var alias in g.Aliases)
            {
                if (!ContainsAlias(s, alias)) continue;
                if (seen.Add(g.Canonical)) yield return g.Canonical;
                break;
            }
        }
        // 記号・数値パターン（"Ra3.2" "M8×1.25" "JIS B 0405"）
        foreach (Match m in RaRegex().Matches(s))
            if (seen.Add("arasa")) yield return "arasa";
        foreach (Match m in RzRegex().Matches(s))
            if (seen.Add("arasa")) yield return "arasa";
        foreach (Match m in MetricThreadRegex().Matches(s))
        {
            var canonical = "m" + m.Groups[1].Value + "x" + m.Groups[2].Value.Replace(".", "");
            if (seen.Add(canonical)) yield return canonical;
            if (seen.Add("neji")) yield return "neji";
        }
        foreach (Match m in JisRegex().Matches(s))
        {
            var canonical = "jis" + m.Groups[1].Value.ToLowerInvariant() + m.Groups[2].Value;
            if (seen.Add(canonical)) yield return canonical;
        }
        if (s.Contains('μ') || s.Contains('µ') || s.Contains("ミクロン"))
            if (seen.Add("um")) yield return "um";
    }

    private static bool ContainsAlias(string s, string alias)
    {
        if (alias.Length == 0) return false;
        // ASCIIだけの別名は単語境界で判定（"ra" が "drawing" の内部に一致するのを防ぐ）
        if (alias.All(c => c < 128))
        {
            var pattern = $@"(?<![a-z0-9]){Regex.Escape(alias)}(?![a-z0-9])";
            return Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase);
        }
        return s.Contains(alias, StringComparison.Ordinal);
    }
}
