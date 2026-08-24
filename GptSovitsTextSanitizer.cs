using System.Text;
using System.Text.RegularExpressions;

namespace Azuma.EmotionTTS.E5;

static class GptSovitsTextSanitizer
{
    static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    /// <summary>移除 emoji 等代理对字符，避免 GPT-SoVITS API 返回 400</summary>
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

    /// <summary>合并 speak 缓冲区后：去掉句首 stray 标点、合并重复句号</summary>
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

    /// <summary>
    /// 归一化易触发 GPT-SoVITS Windows GBK print 崩溃的字符（如 U+301C 波浪号）。
    /// </summary>
    static char NormalizeUnicode(char c) => c switch
    {
        '\u301C' => '\uFF5E', // 〜 → ～
        '\u2212' => '-',     // − → -
        '\u3000' => ' ',     // 全角空格
        _ => c,
    };
}
