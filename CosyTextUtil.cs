using System.Text;
using System.Text.RegularExpressions;

namespace Azuma.EmotionTTS.E5;

/// <summary>
/// Cosy 文本工具（E2）：清洗（emoji/代理对/Unicode 归一）、语种归一、句末边界判定。
/// 替代原 GPT-SoVITS 的 TextSanitizer / PresetResolver.NormalizeLang / SpeakChunkHelper.IsMajorSentenceBoundary。
/// </summary>
static class CosyTextUtil
{
    static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>移除 emoji 等代理对字符 + Unicode 归一（避免合成/对齐异常）。</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
                continue;
            }

            sb.Append(NormalizeUnicode(c));
        }

        return sb.ToString();
    }

    /// <summary>合并 speak 缓冲区后：去掉句首 stray 标点、合并重复句号。</summary>
    public static string NormalizeMergedSpeak(string text)
    {
        text = Sanitize(text).Trim();
        if (text.Length == 0)
            return text;

        text = text.TrimStart('。', '！', '？', '，', '.', '!', '?', ',', '、', '…', '~', '～');

        while (text.Contains("。。"))
            text = text.Replace("。。", "。");
        while (text.Contains("！！"))
            text = text.Replace("！！", "！");
        while (text.Contains("？？"))
            text = text.Replace("？？", "？");

        text = text.Replace('、', '，');
        text = MultiSpaceRegex.Replace(text, " ");
        text = text.Trim();

        return text;
    }

    /// <summary>Unicode 归一（波浪号/负号/全角空格等）。</summary>
    static char NormalizeUnicode(char c) => c switch
    {
        '\u301C' => '\uFF5E', // 〜 → ～
        '\u2212' => '-',     // − → -
        '\u3000' => ' ',     // 全角空格
        _ => c,
    };

    /// <summary>
    /// 语种归一：小写化 + 别名映射（zh-cn/zh-hans/zh-tw → zh 等）。
    /// 未知语种回落 fallback。
    /// </summary>
    public static string NormalizeLang(string? lang, string fallback)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return fallback.Trim().ToLowerInvariant();

        string v = lang.Trim().ToLowerInvariant();
        return v switch
        {
            "zh" or "zh-cn" or "zh-sg" or "zh-tw" or "zh-hk" or "zh-mo" or "zh-hans" or "zh-hant" => "zh",
            "ja" or "ja-jp" => "ja",
            "en" or "en-us" or "en-gb" or "en-au" or "en-ca" or "en-uk" => "en",
            "yue" or "ko" or "fr" or "de" or "ru" or "es" or "id" or "pt" or "th" or "vi" or "ar" or "auto" => v,
            _ => fallback.Trim().ToLowerInvariant(),
        };
    }

    /// <summary>UI 下拉用：仅保留 zh/ja/en，其它回落 zh。</summary>
    public static string NormalizeUiLang(string? lang)
    {
        string v = NormalizeLang(lang, "zh");
        return v is "zh" or "ja" or "en" ? v : "zh";
    }

    /// <summary>句末标点判定（不含逗号，避免切分过碎；不含 '.'，避免拆坏小数/URL/缩写）。</summary>
    public static bool IsMajorSentenceBoundary(char c) =>
        c is '。' or '！' or '？' or '…' or '!' or '?';
}
