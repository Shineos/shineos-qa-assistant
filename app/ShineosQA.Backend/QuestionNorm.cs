using System.Text;

namespace ShineosQA.Backend;

/// <summary>質問の照合用正規化（FAQの完全一致・別名一致）。表記ゆれを吸収するだけで、
/// 検索クエリやLLM入力の正規化には使わない（意味を変えないため）。
/// ・NFKC（全角英数→半角、互換文字の統一）
/// ・小文字化
/// ・空白・句読点・括弧・長音の除去
/// ・カタカナ→ひらがな（「ケイヒ」と「けいひ」を同一視）
/// 既存の Rag.NormalizeZuban（図番専用・英数だけ）とは役割を分ける。</summary>
public static class QuestionNorm
{
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (IsIgnored(c)) continue;
            sb.Append(KatakanaToHiragana(c));
        }
        var normalized = sb.ToString();
        // 日本語の疑問文で落ちやすい末尾助詞（「〜は？」「〜を？」）を除去して同一視する。
        // 語幹を削りすぎないよう「は/が/を」だけに限定する。
        while (normalized.Length > 1 && normalized[^1] is 'は' or 'が' or 'を')
            normalized = normalized[..^1];
        return normalized;
    }

    private static bool IsIgnored(char c) => c switch
    {
        '?' or '？' or '!' or '！' or '。' or '、' or ',' or '，' or '.' or '．' => true,
        ':' or '：' or ';' or '；' or '・' or '／' or '/' or '\\' or '-' or 'ー' or '～' or '〜' => true,
        '「' or '」' or '『' or '』' or '(' or ')' or '（' or '）' or '[' or ']' or '【' or '】' => true,
        '"' or '\'' or '“' or '”' or '‘' or '’' => true,
        _ => false,
    };

    private static char KatakanaToHiragana(char c) =>
        c is >= '\u30A1' and <= '\u30F6' ? (char)(c - 0x60) : c;
}
