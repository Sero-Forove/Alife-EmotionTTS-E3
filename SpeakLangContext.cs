using System.Threading;

namespace Azuma.EmotionTTS.E3;

/// <summary>AI &lt;speak lang="..."&gt; Opening 拦截的每请求语言（AsyncLocal，跨 speak → QQ 语音调用传递）。</summary>
static class SpeakLangContext
{
    static readonly AsyncLocal<string?> CurrentLang = new();

    public static string? Lang
    {
        get => CurrentLang.Value;
        set => CurrentLang.Value = value;
    }
}
