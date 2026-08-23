using System;

namespace Azuma.EmotionTTS.E3;

/// <summary>单次合成相对配置的临时覆盖（不写入用户 JSON）。</summary>
readonly struct GptSovitsSynthOverrides
{
    public string? TextSplitMethod { get; init; }
    public double? RepetitionPenalty { get; init; }
    public int? MinChunkLength { get; init; }

    public static GptSovitsSynthOverrides Resolve(
        EmotionTTSConfig config,
        string text,
        bool streaming,
        int splitPartTotal = 1)
    {
        if (!streaming)
            return default;

        if (splitPartTotal > 1)
            return ResolveSplitPart(config, text);

        if (text.Length <= GptSovitsLongTextGuard.StreamCut0SafeMaxChars)
            return default;

        string? split = null;
        double? rep = null;
        int? minChunk = null;

        if (string.Equals(config.V2_TextSplitMethod, "cut0", StringComparison.OrdinalIgnoreCase))
            split = "cut1";

        if (text.Length > GptSovitsLongTextGuard.ClientSplitThreshold)
        {
            rep = Math.Max(config.V2_RepetitionPenalty, 1.55);
            minChunk = 12;
        }
        else
        {
            rep = Math.Max(config.V2_RepetitionPenalty, 1.45);
        }

        if (text.Length > 200)
            rep = Math.Max(rep ?? config.V2_RepetitionPenalty, 1.65);

        return new GptSovitsSynthOverrides
        {
            TextSplitMethod = split,
            RepetitionPenalty = rep,
            MinChunkLength = minChunk,
        };
    }

    static GptSovitsSynthOverrides ResolveSplitPart(EmotionTTSConfig config, string text)
    {
        string? split = null;
        double? rep = null;
        int? minChunk = null;

        if (string.Equals(config.V2_TextSplitMethod, "cut0", StringComparison.OrdinalIgnoreCase))
            split = "cut1";

        if (text.Length < GptSovitsLongTextGuard.ShortTailSynthChars)
        {
            rep = Math.Max(config.V2_RepetitionPenalty, 1.58);
            minChunk = 12;
        }
        else if (text.Length >= 80)
        {
            rep = Math.Max(config.V2_RepetitionPenalty, 1.48);
        }

        return new GptSovitsSynthOverrides
        {
            TextSplitMethod = split,
            RepetitionPenalty = rep,
            MinChunkLength = minChunk,
        };
    }

    public string EffectiveSplitMethod(EmotionTTSConfig config) =>
        TextSplitMethod ?? config.V2_TextSplitMethod;

    public double EffectiveRepetitionPenalty(EmotionTTSConfig config) =>
        RepetitionPenalty ?? config.V2_RepetitionPenalty;

    public int EffectiveMinChunkLength(EmotionTTSConfig config)
    {
        if (MinChunkLength.HasValue)
            return MinChunkLength.Value;
        int configured = config.V2_MinChunkLength <= 0 ? 16 : config.V2_MinChunkLength;
        return configured;
    }
}
