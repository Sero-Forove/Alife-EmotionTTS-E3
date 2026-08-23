using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Azuma.EmotionTTS.E3;

static class GptSovitsV2TtsClient
{
    public static async Task<HttpResponseMessage> RequestTtsAsync(
        HttpClient http,
        EmotionTTSConfig config,
        GptSovitsPresetConfig preset,
        string text,
        string lang,
        bool streaming,
        GptSovitsSynthOverrides overrides,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        // E3：统一 POST /tts（非流式；每段独立 ref 走 POST body 最稳，避免 GET URL 过长/编码问题）。
        // 流式（E1 兼容保留）或超长文本也走 POST。
        string url = $"http://127.0.0.1:{config.Port}/tts";
        string json = BuildPostJson(config, preset, text, lang, streaming, overrides);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return await http.SendAsync(request, completion, cancellationToken);
    }

    static string BuildPostJson(
        EmotionTTSConfig config,
        GptSovitsPresetConfig preset,
        string text,
        string lang,
        bool streaming,
        GptSovitsSynthOverrides overrides)
    {
        string root = config.InstallPath.TrimEnd('\\', '/');
        // api_v2 的 streaming_mode 是布尔（true=流式响应）
        bool streamMode = streaming;
        var payload = new Dictionary<string, object>
        {
            ["text"] = text,
            ["text_lang"] = lang,
            ["ref_audio_path"] = GptSovitsPresetResolver.ResolvePath(root, preset.RefAudio),
            ["prompt_text"] = preset.RefText ?? "",
            ["prompt_lang"] = preset.RefLanguage,
            ["text_split_method"] = overrides.EffectiveSplitMethod(config),
            ["fragment_interval"] = config.V2_FragmentInterval,
            ["batch_size"] = config.V2_BatchSize,
            ["parallel_infer"] = config.V2_ParallelInfer,
            ["top_k"] = config.V2_TopK,
            ["top_p"] = config.V2_TopP,
            ["temperature"] = config.V2_Temperature,
            ["repetition_penalty"] = overrides.EffectiveRepetitionPenalty(config),
            ["speed_factor"] = config.V2_SpeedFactor,
            ["streaming_mode"] = streamMode,
            ["media_type"] = "wav",
        };
        return JsonSerializer.Serialize(payload);
    }
}
