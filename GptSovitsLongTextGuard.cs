using System;
using System.Collections.Generic;
using System.Text;

namespace Azuma.EmotionTTS.E3;

/// <summary>长文本兜底：客户端拆段、语气标点归一、避免流式长停与 T2S 重复。</summary>
static class GptSovitsLongTextGuard
{
    /// <summary>流式 cut0 经验安全上限（字）。</summary>
    public const int StreamCut0SafeMaxChars = 100;

    /// <summary>超过此长度且流式时，插件拆成多段合成（每段尽量接近但不超过安全上限）。</summary>
    public const int ClientSplitThreshold = StreamCut0SafeMaxChars;

    /// <summary>尾段低于此长度时尝试与上一段重新平衡切分（如 90+16 → ~53+53）。</summary>
    public const int MinBalancedTailChars = 25;

    /// <summary>过短尾段合并回上一段的最小字数。</summary>
    const int MinTailMergeChars = 20;

    /// <summary>拆段后单段低于此长度时，合成侧启用 cut1 + 更高 repPenalty。</summary>
    public const int ShortTailSynthChars = 25;

    /// <summary>超过此长度优先 POST /tts，避免 GET URL 过长。</summary>
    public const int PreferPostThreshold = 150;

    public static bool ShouldUsePost(string text) => text.Length >= PreferPostThreshold;

    /// <summary>波浪号/省略号式停顿易触发流式首包切分，改为逗号语气。</summary>
    public static string NormalizeProsodyPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '～' or '~')
            {
                sb.Append('，');
                continue;
            }

            if (c is '…')
            {
                sb.Append('，');
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> PartitionForPlayback(EmotionTTSConfig? config, string text)
    {
        if (config == null || string.IsNullOrWhiteSpace(text))
            return string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : new[] { text };

        // 流式与文件模式均对超长文本拆段（V1 文件模式同样需要，避免单请求过大）
        if (text.Length <= ClientSplitThreshold)
            return new[] { text };

        int maxChunk = config.SpeakChunkMaxLength <= 0 ? StreamCut0SafeMaxChars : config.SpeakChunkMaxLength;
        maxChunk = Math.Min(maxChunk, StreamCut0SafeMaxChars);
        return SplitAtSentenceBoundaries(text, maxChunk);
    }

    /// <summary>按句切分后贪心合并，使每段尽量接近 maxChunkLen 且不超过，避免流式 cut0 长段 T2S 循环。</summary>
    public static List<string> SplitAtSentenceBoundaries(string text, int maxChunkLen)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        if (maxChunkLen <= 0)
            maxChunkLen = StreamCut0SafeMaxChars;

        List<string> sentences = ExtractMajorSentences(text);
        if (sentences.Count == 0)
            return new List<string>();

        return RebalanceChunks(PackSentences(sentences, maxChunkLen), maxChunkLen);
    }

    static List<string> ExtractMajorSentences(string text)
    {
        var result = new List<string>();
        var buf = new StringBuilder();
        foreach (char c in text)
        {
            buf.Append(c);
            if (c is '。' or '！' or '？' or '!' or '?' or '…' or '\n')
                FlushTrimmedSentence(result, buf);
        }

        FlushTrimmedSentence(result, buf);
        return result;
    }

    static void FlushTrimmedSentence(List<string> result, StringBuilder buf)
    {
        string seg = buf.ToString().Trim();
        buf.Clear();
        if (seg.Length > 0)
            result.Add(seg);
    }

    static List<string> PackSentences(IReadOnlyList<string> sentences, int maxChunkLen)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        void FlushCurrent()
        {
            string s = current.ToString().Trim();
            current.Clear();
            if (s.Length > 0)
                chunks.Add(s);
        }

        foreach (string sentence in sentences)
        {
            if (string.IsNullOrWhiteSpace(sentence))
                continue;

            if (sentence.Length > maxChunkLen)
            {
                FlushCurrent();
                chunks.AddRange(HardSplit(sentence, maxChunkLen));
                continue;
            }

            if (current.Length > 0 && current.Length + sentence.Length > maxChunkLen)
                FlushCurrent();

            current.Append(sentence);
        }

        FlushCurrent();
        return chunks;
    }

    static List<string> RebalanceChunks(List<string> chunks, int maxChunkLen)
    {
        if (chunks.Count < 2)
            return chunks;

        MergeTinyTail(chunks, maxChunkLen);

        while (chunks.Count >= 2)
        {
            string last = chunks[^1];
            if (last.Length >= MinBalancedTailChars)
                break;

            string prev = chunks[^2];
            if (prev.Length + last.Length <= maxChunkLen)
            {
                MergeTinyTail(chunks, maxChunkLen);
                break;
            }

            if (!TryRebalanceLastPair(chunks, maxChunkLen))
                break;
        }

        return chunks;
    }

    static void MergeTinyTail(List<string> chunks, int maxChunkLen)
    {
        if (chunks.Count < 2)
            return;

        string last = chunks[^1];
        string prev = chunks[^2];
        if (last.Length < MinTailMergeChars && prev.Length + last.Length <= maxChunkLen)
        {
            chunks.RemoveAt(chunks.Count - 1);
            chunks[^1] = prev + last;
        }
    }

    static bool TryRebalanceLastPair(List<string> chunks, int maxChunkLen)
    {
        string prev = chunks[^2];
        string last = chunks[^1];
        string combined = prev + last;
        int target = combined.Length / 2;
        int splitAt = FindBalancedSplitIndex(combined, target, maxChunkLen);
        if (splitAt <= 0)
            return false;

        string newPrev = combined[..splitAt].Trim();
        string newLast = combined[splitAt..].Trim();
        if (newPrev.Length == 0 || newLast.Length == 0)
            return false;
        if (newPrev.Length > maxChunkLen || newLast.Length > maxChunkLen)
            return false;
        if (newLast.Length <= last.Length)
            return false;

        chunks[^2] = newPrev;
        chunks[^1] = newLast;
        return true;
    }

    static int FindBalancedSplitIndex(string text, int target, int maxChunkLen)
    {
        int best = -1;
        int bestDist = int.MaxValue;

        for (int i = 1; i < text.Length; i++)
        {
            if (!IsSplitPoint(text[i - 1]))
                continue;

            string left = text[..i].Trim();
            string right = text[i..].Trim();
            if (left.Length == 0 || right.Length == 0)
                continue;
            if (left.Length > maxChunkLen || right.Length > maxChunkLen)
                continue;
            if (right.Length < MinBalancedTailChars)
                continue;

            int dist = Math.Abs(i - target);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    static bool IsSplitPoint(char c) =>
        c is '。' or '！' or '？' or '!' or '?' or '…' or '\n' or '，' or '、' or ',' or ';' or '；';

    static List<string> HardSplit(string text, int maxChunkLen)
    {
        var parts = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int remaining = text.Length - start;
            if (remaining <= maxChunkLen)
            {
                string tail = text[start..].Trim();
                if (tail.Length > 0)
                    parts.Add(tail);
                break;
            }

            int splitAt = FindHardSplitIndex(text, start, maxChunkLen);
            string piece = text[start..splitAt].Trim();
            if (piece.Length > 0)
                parts.Add(piece);
            start = splitAt;
        }

        return parts;
    }

    static int FindHardSplitIndex(string text, int start, int maxChunkLen)
    {
        int end = Math.Min(start + maxChunkLen, text.Length);
        int minSplit = start + Math.Max(1, maxChunkLen / 2);
        for (int i = end - 1; i >= minSplit; i--)
        {
            if (text[i] is '，' or '、' or ',' or ';' or '；')
                return i + 1;
        }

        return end;
    }
}
