using System;
using System.Text;

namespace Azuma.EmotionTTS.E5;

static class GptSovitsUrlBuilder
{
    public static string BuildLegacyUrl(EmotionTTSConfig config, string text, string lang)
    {
        string url = config.ApiUrl;
        url = url.Replace("{text}", Uri.EscapeDataString(text));
        url = url.Replace("{lang}", Uri.EscapeDataString(lang));
        if (!url.Contains("text_language=", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("text_lang=", StringComparison.OrdinalIgnoreCase))
        {
            url += url.Contains('?') ? "&" : "?";
            url += "text_language=" + Uri.EscapeDataString(lang);
        }
        return url;
    }

    public static string BuildV1Url(EmotionTTSConfig config, string text, string lang)
    {
        var sb = new StringBuilder();
        sb.Append($"http://127.0.0.1:{config.Port}/?text={Uri.EscapeDataString(text)}");
        sb.Append($"&text_language={Uri.EscapeDataString(lang)}");
        sb.Append($"&top_k={config.V1_TopK}");
        sb.Append($"&top_p={config.V1_TopP.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append($"&temperature={config.V1_Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append($"&speed={config.V1_Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(config.V1_CutPunc))
            sb.Append($"&cut_punc={Uri.EscapeDataString(config.V1_CutPunc)}");
        return sb.ToString();
    }

    /// <summary>
    /// V1 桌宠兼容：在 ApiUrl 模板上替换 {text}，并强制/补全 text_language（支持 speak lang 动态切换）。
    /// </summary>
    public static string ApplyV1TextAndLang(string apiUrlTemplate, string text, string lang)
    {
        string url = apiUrlTemplate.Replace("{text}", Uri.EscapeDataString(text));
        url = url.Replace("{lang}", Uri.EscapeDataString(lang));

        // 覆盖已有 text_language / text_lang
        if (System.Text.RegularExpressions.Regex.IsMatch(url, @"([?&])text_language=",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            url = System.Text.RegularExpressions.Regex.Replace(
                url,
                @"([?&])text_language=[^&]*",
                m => m.Groups[1].Value + "text_language=" + Uri.EscapeDataString(lang),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        else if (System.Text.RegularExpressions.Regex.IsMatch(url, @"([?&])text_lang=",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            url = System.Text.RegularExpressions.Regex.Replace(
                url,
                @"([?&])text_lang=[^&]*",
                m => m.Groups[1].Value + "text_lang=" + Uri.EscapeDataString(lang),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        else
        {
            url += (url.Contains('?') ? "&" : "?") + "text_language=" + Uri.EscapeDataString(lang);
        }

        return url;
    }

    public static string BuildV2Url(EmotionTTSConfig config, GptSovitsPresetConfig preset,
        string text, string lang, bool streaming, GptSovitsSynthOverrides overrides = default)
    {
        string root = config.InstallPath.TrimEnd('\\', '/');
        // api_v2 的 streaming_mode 是布尔
        bool streamMode = streaming;
        string splitMethod = overrides.EffectiveSplitMethod(config);
        double repPenalty = overrides.EffectiveRepetitionPenalty(config);

        var sb = new StringBuilder();
        sb.Append($"http://127.0.0.1:{config.Port}/tts?");
        sb.Append($"text={Uri.EscapeDataString(text)}");
        sb.Append($"&text_lang={Uri.EscapeDataString(lang)}");
        sb.Append($"&ref_audio_path={Uri.EscapeDataString(GptSovitsPresetResolver.ResolvePath(root, preset.RefAudio))}");
        sb.Append($"&prompt_text={Uri.EscapeDataString(preset.RefText ?? "")}");
        sb.Append($"&prompt_lang={Uri.EscapeDataString(preset.RefLanguage)}");
        sb.Append($"&text_split_method={Uri.EscapeDataString(splitMethod)}");
        sb.Append($"&fragment_interval={config.V2_FragmentInterval.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append($"&batch_size={config.V2_BatchSize}");
        sb.Append($"&parallel_infer={(config.V2_ParallelInfer ? "true" : "false")}");
        sb.Append($"&top_k={config.V2_TopK}");
        sb.Append($"&top_p={config.V2_TopP.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append($"&temperature={config.V2_Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append($"&repetition_penalty={repPenalty.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append($"&speed_factor={config.V2_SpeedFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append($"&streaming_mode={streamMode.ToString().ToLowerInvariant()}");
        sb.Append("&media_type=wav");
        return sb.ToString();
    }

    public static string BuildCacheKey(EmotionTTSConfig config, string text, string lang, bool streaming,
        GptSovitsSynthOverrides overrides = default)
    {
        if (GptSovitsConfigHelper.IsLegacyMode(config))
            return $"legacy|{config.ApiUrl}|{lang}|{text}";

        var sb = new StringBuilder();
        sb.Append(config.ApiVersion).Append('|');
        if (string.Equals(config.ApiVersion, "v1", StringComparison.OrdinalIgnoreCase))
            sb.Append(config.ApiUrl).Append('|');
        sb.Append(config.PresetName).Append('|');
        sb.Append(streaming ? 's' : 'f').Append('|');
        sb.Append(lang).Append('|');
        sb.Append(config.GptWeight).Append('|');
        sb.Append(config.SovitsWeight).Append('|');
        sb.Append(config.RefAudio).Append('|');
        sb.Append(config.RefText).Append('|');
        sb.Append(config.RefLanguage).Append('|');
        AppendSamplingParams(sb, config, overrides);
        sb.Append('|').Append(text);
        return sb.ToString();
    }

    static void AppendSamplingParams(StringBuilder sb, EmotionTTSConfig config,
        GptSovitsSynthOverrides overrides = default)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (string.Equals(config.ApiVersion, "v2", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(overrides.EffectiveSplitMethod(config)).Append('|');
            sb.Append(config.V2_FragmentInterval.ToString(inv)).Append('|');
            sb.Append(config.V2_BatchSize).Append('|');
            sb.Append(config.V2_ParallelInfer).Append('|');
            sb.Append(config.V2_TopK).Append('|');
            sb.Append(config.V2_TopP.ToString(inv)).Append('|');
            sb.Append(config.V2_Temperature.ToString(inv)).Append('|');
            sb.Append(overrides.EffectiveRepetitionPenalty(config).ToString(inv)).Append('|');
            sb.Append(config.V2_SpeedFactor.ToString(inv)).Append('|');
            sb.Append(config.V2_StreamingMode).Append('|');
            sb.Append(overrides.EffectiveMinChunkLength(config));
            return;
        }

        sb.Append(config.V1_Device).Append('|');
        sb.Append(config.V1_HalfPrecision).Append('|');
        sb.Append(config.V1_TopK).Append('|');
        sb.Append(config.V1_TopP.ToString(inv)).Append('|');
        sb.Append(config.V1_Temperature.ToString(inv)).Append('|');
        sb.Append(config.V1_Speed.ToString(inv)).Append('|');
        sb.Append(config.V1_CutPunc);
    }
}

