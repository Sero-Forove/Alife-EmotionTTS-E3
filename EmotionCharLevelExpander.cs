using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 把 EmotionVoiceDirective + 对白文本展开为逐字控制表（CharLevelDirective[]）。
/// 切字规则：中文逐字；日文假名逐字（汉字按词近似）；英文按词。
/// 标点不发音，但作为"停顿单元"保留（句末标点→长停顿，逗号→短停顿）。
/// </summary>
static class EmotionCharLevelExpander
{
    // 句末标点（长停顿）
    static readonly HashSet<char> SentenceEnders = new() { '。', '！', '？', '!', '?', '…', '.' };
    // 短停顿标点
    static readonly HashSet<char> PauseMarks = new() { '，', ',', '、', '；', ';', '：', ':' };

    /// <summary>把文本 + 指令展开为逐字控制表。</summary>
    public static List<CharLevelDirective> Expand(string text, EmotionVoiceDirective? directive)
    {        var result = new List<CharLevelDirective>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        List<string> units = Tokenize(text);

        // 全局默认
        double globalSpeed = directive?.Speed ?? 1.0;
        double globalPitch = directive?.PitchOffset ?? 0;
        double globalVolume = directive?.Volume ?? 1.0;
        string globalBreath = directive?.Breath ?? "";
        string globalTimbre = directive?.Timbre ?? "";
        double globalVibRate = directive?.VibratoRate ?? 0;
        double globalVibDepth = directive?.VibratoDepth ?? 0;

        for (int i = 0; i < units.Count; i++)
        {
            string unit = units[i];
            var cd = new CharLevelDirective
            {
                Char = unit,
                SpeedFactor = EmotionDirectiveParser.ClampSpeed(globalSpeed),
                PitchOffset = EmotionDirectiveParser.ClampPitch(globalPitch),
                Volume = EmotionDirectiveParser.ClampVolume(globalVolume),
                Breath = globalBreath,
                Timbre = globalTimbre,
                VibratoRate = globalVibRate,
                VibratoDepth = globalVibDepth,
            };

            // 标点 → 停顿（不应用 pitch/speed，但保留 volume）。
            // GPT-SoVITS 已按标点切句（cut0）自带句间停顿，DSP 不再叠加任何停顿（0ms）——
            // 避免"双重停顿"导致句号后停太久。seg/字 的显式 pause 不受影响。
            if (unit.Length == 1 && (SentenceEnders.Contains(unit[0]) || PauseMarks.Contains(unit[0])))
            {
                cd.SpeedFactor = 1.0;
                cd.PitchOffset = 0;
                cd.Breath = "";
                cd.Volume = EmotionDirectiveParser.ClampVolume(globalVolume);
                cd.PauseAfterMs = 0;
                result.Add(cd);
                continue;
            }

            // 段级覆盖（seg）：span 精确段 或 start/end 范围，向后兼容
            ApplySegOverrides(cd, directive, units, i);

            // 字级显式覆盖（字）
            ApplyCharOverrides(cd, directive, unit);

            // 呼吸：若该字是 seg 起点且 seg 带 breath → 用 seg 的
            result.Add(cd);
        }

        return result;
    }

    /// <summary>应用 seg 段级覆盖：找到 start 与"当前位置的累计文本"前缀匹配的 seg。</summary>
    static void ApplySegOverrides(CharLevelDirective cd, EmotionVoiceDirective? directive,
        List<string> units, int index)
    {
        if (directive == null || directive.Segments.Count == 0)
            return;

        // 累计文本（从开头到当前字，去标点）——start/end 匹配用
        var sb = new StringBuilder();
        for (int i = 0; i <= index && i < units.Count; i++)
        {
            string u = units[i];
            if (u.Length == 1 && (SentenceEnders.Contains(u[0]) || PauseMarks.Contains(u[0])))
                continue;
            sb.Append(u);
        }
        string running = sb.ToString();

        // 完整原文（span 精确匹配用，含标点）
        string full = string.Concat(units);

        SegDirective? match = null;
        int matchPriority = -1; // 0=span精确, 1=start+end范围, 2=start起点

        foreach (SegDirective seg in directive.Segments)
        {
            // ① span 精确段（最高优先）：控制 span 指定的字/词/衔接，前后不受影响
            if (!string.IsNullOrEmpty(seg.Span))
            {
                // 判断当前字是否落在 span 内：找 span 在 full 中的位置区间
                int spanStart = full.IndexOf(seg.Span, StringComparison.Ordinal);
                if (spanStart >= 0)
                {
                    // 当前字在 full 中的累计位置（含标点）
                    int curPos = 0;
                    for (int i = 0; i <= index && i < units.Count; i++)
                        curPos += units[i].Length;
                    curPos -= units[index].Length; // 当前字起点

                    if (curPos >= spanStart && curPos < spanStart + seg.Span.Length)
                    {
                        match = seg;
                        matchPriority = 0;
                        break;
                    }
                }
            }

            // ② start+end 范围：从 start 到 end 之间的中间段
            if (!string.IsNullOrEmpty(seg.Start) && !string.IsNullOrEmpty(seg.End))
            {
                int s = running.IndexOf(seg.Start, StringComparison.Ordinal);
                if (s >= 0)
                {
                    // 当前字在 running 中的位置
                    int cur = running.Length - (units[index].Length > 0 ? units[index].Length : 0);
                    int eIdx = running.IndexOf(seg.End, StringComparison.Ordinal);
                    bool inRange = cur >= s && (eIdx < 0 || cur < eIdx);
                    if (inRange && (matchPriority < 1))
                    {
                        match = seg;
                        matchPriority = 1;
                    }
                }
            }

            // ③ start 起点（向后兼容）：从 start 到句末或到 end。
            //    多个 start seg 并存时，**后声明的 seg 覆盖前面的**。
            //    匹配粒度：当前字落在 seg.Start 内部即进入该 seg——
            //    扫描 running 尾部 1..min(len) 长度，任一长度下"running 尾部 == Start 前缀"即命中，
            //    这样 start="我还能" 从"我"字（尾部1字=="我"==Start前1字）就开始生效。
            if (!string.IsNullOrEmpty(seg.Start) && string.IsNullOrEmpty(seg.End))
            {
                bool reached = false;
                int maxCmp = Math.Min(running.Length, seg.Start.Length);
                for (int len = 1; len <= maxCmp && !reached; len++)
                {
                    if (string.CompareOrdinal(running, running.Length - len, seg.Start, 0, len) == 0)
                        reached = true;
                }
                if (reached && matchPriority <= 2)
                {
                    // 后声明的 seg 覆盖（同 priority=2 下，遍历到后面的 seg 直接替换）
                    match = seg;
                    matchPriority = 2;
                }
            }
        }

        if (match == null)
            return;

        ApplySegToChar(cd, match);
    }

    /// <summary>把 seg 的参数应用到单个字。</summary>
    static void ApplySegToChar(CharLevelDirective cd, SegDirective match)
    {
        if (match.Pitch.HasValue)
            cd.PitchOffset = EmotionDirectiveParser.ClampPitch(match.Pitch.Value);
        if (match.Speed.HasValue)
            cd.SpeedFactor = EmotionDirectiveParser.ClampSpeed(match.Speed.Value);
        if (match.Volume.HasValue)
            cd.Volume = EmotionDirectiveParser.ClampVolume(match.Volume.Value);
        if (!string.IsNullOrEmpty(match.Breath))
            cd.Breath = match.Breath;
        if (!string.IsNullOrEmpty(match.Emotion))
            cd.EmotionBoundary = true; // 语义段起点（情感 ref 切换点，由合成层处理）
        // 细分与包络：传递给 CharLevelFx 做包络
        if (match.SubDivisions > 1)
            cd.SubDivisions = match.SubDivisions;
        if (!string.IsNullOrEmpty(match.Envelope))
            cd.Envelope = match.Envelope;
        // 音色与颤音（段级）
        if (!string.IsNullOrEmpty(match.Timbre))
            cd.Timbre = match.Timbre;
        if (match.VibratoRate.HasValue && match.VibratoRate.Value > 0)
        {
            cd.VibratoRate = match.VibratoRate.Value;
            cd.VibratoDepth = match.VibratoDepth ?? 0.25;
        }
    }

    /// <summary>应用字级显式覆盖。</summary>
    static void ApplyCharOverrides(CharLevelDirective cd, EmotionVoiceDirective? directive, string unit)
    {
        if (directive == null || directive.CharOverrides.Count == 0)
            return;
        foreach (CharDirective c in directive.CharOverrides)
        {
            if (string.IsNullOrEmpty(c.Char))
                continue;
            if (unit == c.Char || c.Char.Contains(unit, StringComparison.Ordinal) && unit.Length == 1)
            {
                if (c.Pitch.HasValue)
                    cd.PitchOffset = EmotionDirectiveParser.ClampPitch(c.Pitch.Value);
                if (c.Speed.HasValue)
                    cd.SpeedFactor = EmotionDirectiveParser.ClampSpeed(c.Speed.Value);
                if (c.Volume.HasValue)
                    cd.Volume = EmotionDirectiveParser.ClampVolume(c.Volume.Value);
                if (!string.IsNullOrEmpty(c.Breath))
                    cd.Breath = c.Breath;
                if (!string.IsNullOrEmpty(c.Timbre))
                    cd.Timbre = c.Timbre;
                if (c.VibratoRate.HasValue && c.VibratoRate.Value > 0)
                {
                    cd.VibratoRate = c.VibratoRate.Value;
                    cd.VibratoDepth = c.VibratoDepth ?? 0.25;
                }
                // 显式停顿（字间空隙）：字级 pause 覆盖自动标点停顿
                if (c.PauseAfterMs > 0)
                    cd.PauseAfterMs = Math.Max(cd.PauseAfterMs, c.PauseAfterMs);
            }
        }
    }

    /// <summary>
    /// 切字：中文/日文假名逐字；英文按词；标点独立成单元。
    /// </summary>
    static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var latinWord = new StringBuilder();

        void FlushLatin()
        {
            if (latinWord.Length > 0)
            {
                result.Add(latinWord.ToString());
                latinWord.Clear();
            }
        }

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                FlushLatin();
                continue;
            }

            // 拉丁字母/数字 → 词
            if (char.IsLetterOrDigit(c) && c < 0x2E80) // 非 CJK
            {
                latinWord.Append(c);
                continue;
            }

            FlushLatin();

            // 标点独立
            if (SentenceEnders.Contains(c) || PauseMarks.Contains(c))
            {
                result.Add(c.ToString());
                continue;
            }

            // CJK 单字（中文汉字、日文假名、韩文）
            result.Add(c.ToString());
        }

        FlushLatin();
        return result;
    }

    /// <summary>一个情感分段：从某起点文本开始、指定情感、包含的文本。</summary>
    public sealed class EmotionSegment
    {
        public string Emotion = "";
        public string Text = "";
        public string StartMark = ""; // seg 起点（用于诊断/日志）
    }

    /// <summary>
    /// 收集带情感的 seg 划分（一句话内多 ref 分段合成用）：
    /// 按 seg.Emotion 的起点在原文中切段，每段用独立情感 ref 合成后拼接。
    /// 返回 null = 无情感分段（走单 ref 路径）；否则为有序段列表（首段可能是无情感前缀）。
    /// </summary>
    public static List<EmotionSegment>? CollectEmotionSegments(string text, EmotionVoiceDirective? directive)
    {
        if (directive == null || directive.Segments.Count == 0 ||
            !directive.Segments.Any(s => !string.IsNullOrEmpty(s.Emotion)))
            return null;

        // 按 seg 起点在原文中的位置排序（起点文本 = seg.Start；span 用 span）
        var boundaries = new List<(int pos, string emotion)>();
        foreach (SegDirective seg in directive.Segments)
        {
            if (string.IsNullOrEmpty(seg.Emotion))
                continue;
            string mark = !string.IsNullOrEmpty(seg.Span) ? seg.Span : seg.Start;
            if (string.IsNullOrEmpty(mark))
                continue;
            int pos = text.IndexOf(mark, StringComparison.Ordinal);
            if (pos < 0)
                continue;
            boundaries.Add((pos, seg.Emotion));
        }
        if (boundaries.Count == 0)
            return null;

        boundaries.Sort((a, b) => a.pos.CompareTo(b.pos));
        // 合并同位置（保留第一个）
        var merged = new List<(int pos, string emotion)>();
        foreach (var b in boundaries)
        {
            if (merged.Count == 0 || merged[^1].pos != b.pos)
                merged.Add(b);
        }

        var segments = new List<EmotionSegment>();
        int cursor = 0;
        foreach (var (pos, emotion) in merged)
        {
            if (pos > cursor)
            {
                // 前缀（无情感）：用空情感（合成层回退顶层 ref）
                segments.Add(new EmotionSegment { Emotion = "", Text = text[cursor..pos] });
            }
            // 当前边界到下一边界
            int end = text.Length;
            var next = merged.FirstOrDefault(x => x.pos > pos);
            if (next.pos > 0)
                end = next.pos;
            segments.Add(new EmotionSegment
            {
                Emotion = emotion,
                Text = text[pos..end],
                StartMark = text[pos..Math.Min(pos + 8, text.Length)],
            });
            cursor = end;
        }
        if (cursor < text.Length)
        {
            segments.Add(new EmotionSegment { Emotion = "", Text = text[cursor..] });
        }
        return segments;
    }
}
