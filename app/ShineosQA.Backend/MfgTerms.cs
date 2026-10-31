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
        // 英語資料・外国人材向けの基本語（オフィス/HR系）。日本語質問×英語文書の語彙一致を作る。
        // 汎用動詞（apply/approve等）は誤マッチが増えるため入れない（名詞・複合名詞のみ）。
        // v2.7.0: 回答言語に対応するvi/zh/id/tlの別名を追加（多言語質問×日本語KBのクロスリンガル検索。
        // 実測: 外国語クエリはkw=0.00でlow-confガードが全拒否 → 正規トークンで語彙一致を作り
        // canonicalEvidenceで拒否を免除する）。動詞・疑問詞は入れず名詞句単位
        new("office", new[] { "オフィス", "office" }),
        new("mycar", new[] { "マイカー", "自分の車", "自家用車", "私用車", "my car", "own car",
            "私家车", "xe cá nhân", "mobil pribadi", "sariling kotse" }),
        new("chushajo", new[] { "駐車場", "パーキング", "parking",
            "停车场", "bãi đỗ xe", "gửi xe", "parkir", "paradahan" }),
        new("ryokin", new[] { "料金", "fee", "price", "charge",
            "费用", "phí", "biaya" }),
        new("kaigishitsu", new[] { "会議室", "ミーティングルーム", "meeting room", "conference room",
            "会议室", "phòng họp", "ruang rapat", "kuwarto ng pagpupulong" }),
        new("kucho", new[] { "空調", "air conditioning", "air conditioner" }),
        new("telework", new[] { "テレワーク", "在宅勤務", "リモートワーク", "telework", "remote work", "work from home" }),
        new("teate", new[] { "手当", "手当て", "allowance" }),
        new("password", new[] { "パスワード", "password",
            "密码", "mật khẩu", "kata sandi" }),
        new("kyuka", new[] { "休暇", "有給", "有給休暇", "年次有給休暇", "leave", "vacation", "paid leave", "annual leave",
            "丧假", "nghỉ tang", "cuti duka", "bereavement leave" }),
        new("helpdesk", new[] { "ヘルプデスク", "窓口", "内線", "ITサポート", "help desk",
            "服务台", "quầy hỗ trợ" }),
        new("nyusha", new[] { "入社", "入社時", "hire", "onboarding" }),
        new("kinsen", new[] { "お金が出る", "金銭", "補助", "助成", "給付", "subsidy" }),
        new("shutchou", new[] { "出張", "business trip",
            "出差", "công tác", "perjalanan dinas", "pagbiyahe" }),
        new("shukuhaku", new[] { "宿泊費", "宿泊", "ホテル", "ホテル代", "accommodation", "hotel",
            "住宿", "酒店", "宾馆", "khách sạn", "penginapan", "otel" }),
        new("kotsuhi", new[] { "交通費", "transportation" }),
        new("keihi", new[] { "経費", "expenses" }),
        new("kigen", new[] { "期限", "deadline" }),
        new("joushi", new[] { "上司", "supervisor" }),
        new("buchou", new[] { "部長", "department manager" }),
        new("setsubi", new[] { "設備", "equipment" }),
        new("hozen", new[] { "保全", "maintenance" }),
        new("zaiko", new[] { "在庫", "inventory" }),
        new("shukka", new[] { "出荷", "shipping" }),
    };

    [GeneratedRegex(@"(?<![A-Za-z0-9])ra\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RaRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])rz\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RzRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])m\s*([0-9]{1,2})\s*[x×]\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MetricThreadRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])jis\s*([a-z])\s*([0-9]{3,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex JisRegex();

    private static readonly HashSet<string> CanonicalSet =
        new(Groups.Select(g => g.Canonical), StringComparer.Ordinal) { "arasa", "um" };

    /// <summary>正規トークン（別名展開で付与したトークン）か。キーワード一致の重み付け・
    /// クロスリンガル候補の保持判定に使う。</summary>
    public static bool IsCanonicalToken(string token)
    {
        if (CanonicalSet.Contains(token)) return true;
        // ねじ（m8x125）・JIS（jisb0405）はパターン生成のため接頭辞で判定
        if (token.Length > 2 && token[0] == 'm' && char.IsDigit(token[1]) && token.Contains('x')) return true;
        if (token.Length > 3 && token.StartsWith("jis", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>埋め込み・リランク用のクエリ拡張。質問に含まれる用語の英語別名を末尾に足す。
    /// 日本語質問×英語図面/規格のベクトル一致を改善する（例: 「駐車場の料金」→「… parking fee」）。
    /// 日本語別名は元質問に含まれているため追加しない。LLMコストなし。</summary>
    public static string ExpandQuery(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var extras = new List<string>();
        foreach (var g in Groups)
        {
            if (!g.Aliases.Any(a => ContainsAlias(s, a))) continue;
            foreach (var a in g.Aliases)
                if (a.All(c => c < 128) && !s.Contains(a, StringComparison.OrdinalIgnoreCase))
                    extras.Add(a);
        }
        if (extras.Count == 0) return s;
        return s + " " + string.Join(" ", extras.Distinct(StringComparer.OrdinalIgnoreCase));
    }

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
        // 多言語別名（vi等）は文頭の大文字と一致させるため大文字小文字を無視する
        // （実測: 「Mật khẩu」が別名「mật khẩu」に不一致になり正規トークンが付かなかった）
        return s.Contains(alias, StringComparison.OrdinalIgnoreCase);
    }
}
