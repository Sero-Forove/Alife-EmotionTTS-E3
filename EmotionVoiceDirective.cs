using System.Collections.Generic;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 主 LLM 通过 &lt;tune&gt; 表达的全部语音指令（tune 顶层属性 + 内嵌 seg/字）。
/// 由 TuneHandler 解析写入，speak 消费。
/// </summary>
public sealed class EmotionVoiceDirective
{
    /// <summary>情感语义段（映射到情感 ref 库），如 正常/喜悦/悲伤/愤怒/惊讶/兴奋/阴沉/虚弱。</summary>
    public string Emotion { get; set; } = "正常";

    /// <summary>强度档：弱/中/强（选 ref 档位）。</summary>
    public string Tier { get; set; } = "中";

    /// <summary>全局语速系数（0.7~1.6），未指定=1.0。</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>全局音调偏移（半音，-3~+3），未指定=0。</summary>
    public double PitchOffset { get; set; } = 0;

    /// <summary>全局音量系数（0.0~2.0，1.0=正常），未指定=1.0。</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>全局呼吸：吸气/喘/空。</summary>
    public string Breath { get; set; } = "";

    /// <summary>全局音色：混响/失真/空灵/金属（可组合"混响+失真"）。</summary>
    public string Timbre { get; set; } = "";

    /// <summary>全局颤音频率（Hz，0=关；如 5 表示每秒 5 次）。</summary>
    public double VibratoRate { get; set; }

    /// <summary>全局颤音深度（半音，0=关；0.3≈轻微颤抖）。</summary>
    public double VibratoDepth { get; set; }

    /// <summary>段级指令（按文本起点匹配覆盖）。</summary>
    public List<SegDirective> Segments { get; } = new();

    /// <summary>显式字级覆盖（精确到字）。</summary>
    public List<CharDirective> CharOverrides { get; } = new();

    /// <summary>是否有任何指令内容（用于快速判断是否需要字级管线）。</summary>
    public bool HasAny =>
        Emotion != "正常" || Tier != "中" || Speed != 1.0 || PitchOffset != 0 ||
        Volume != 1.0 || !string.IsNullOrEmpty(Breath) ||
        !string.IsNullOrEmpty(Timbre) || VibratoRate > 0 ||
        Segments.Count > 0 || CharOverrides.Count > 0;
}

/// <summary>
/// 段级指令：支持三种粒度——
/// - start 起点（从 start 到句末或到 end）
/// - span 精确段（只控制 span 指定的字/词/衔接，前后不受影响）
/// - end 结束（配合 start 控制中间段）
/// </summary>
public sealed class SegDirective
{
    /// <summary>段起点文本（前缀匹配）。</summary>
    public string Start { get; set; } = "";

    /// <summary>精确控制段（span="不客气" 只控制这几个字；span="，" 控制衔接处）。</summary>
    public string Span { get; set; } = "";

    /// <summary>段结束文本（配合 start 控制中间段）。</summary>
    public string End { get; set; } = "";

    public double? Pitch { get; set; }
    public double? Speed { get; set; }
    public double? Volume { get; set; }
    public string? Breath { get; set; }
    public string? Emotion { get; set; }
    public string? Tier { get; set; }
    /// <summary>段级音色（混响/失真/空灵/金属）。</summary>
    public string? Timbre { get; set; }
    /// <summary>段级颤音（频率 Hz，>0 生效）。</summary>
    public double? VibratoRate { get; set; }
    /// <summary>段级颤音深度（半音）。</summary>
    public double? VibratoDepth { get; set; }
    /// <summary>细分份数（该段每字切 N 份，制造渐强渐弱）。</summary>
    public int SubDivisions { get; set; } = 1;
    /// <summary>包络/平滑：linear/ease-in/ease-out 或 ADSR（"a:0.1,d:0.2,s:0.6,r:0.1"）。</summary>
    public string Envelope { get; set; } = "";
}

/// <summary>显式字级覆盖：精确到某个字/词。</summary>
public sealed class CharDirective
{
    /// <summary>目标字/词（原文匹配）。</summary>
    public string Char { get; set; } = "";

    public double? Pitch { get; set; }
    public double? Speed { get; set; }
    public double? Volume { get; set; }
    public string? Breath { get; set; }
    public string? Timbre { get; set; }
    public double? VibratoRate { get; set; }
    public double? VibratoDepth { get; set; }
    /// <summary>该字后显式停顿（毫秒，字间空隙）。</summary>
    public int PauseAfterMs { get; set; }
}

/// <summary>逐字控制项：由 CharLevelExpander 从 VoiceDirective 展开，供 CharLevelFx 做字级 DSP。</summary>
public sealed class CharLevelDirective
{
    /// <summary>字（或词，日语/英语按词级）。</summary>
    public string Char { get; set; } = "";

    /// <summary>音调偏移（半音，-3~+3，钳制后）。</summary>
    public double PitchOffset { get; set; }

    /// <summary>语速系数（0.7~1.6，钳制后）。</summary>
    public double SpeedFactor { get; set; } = 1.0;

    /// <summary>音量系数（0.0~2.0，1.0=正常；耳语 0.4~0.6，爆发 1.2~1.5）。</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>该字后停顿（毫秒）。</summary>
    public int PauseAfterMs { get; set; }

    /// <summary>呼吸标记：吸气/喘/空。</summary>
    public string Breath { get; set; } = "";

    /// <summary>该字是否为"语义段起点"（情感 ref 在此切换）。</summary>
    public bool EmotionBoundary { get; set; }

    /// <summary>细分：该字切成 N 份（每份可不同音量/音调/速度，制造渐强渐弱/包络）。默认 1。</summary>
    public int SubDivisions { get; set; } = 1;

    /// <summary>细分包络：可选 ADSR 风格（"a:0.1,d:0.2,s:0.6,r:0.1" 或 "linear/ease-in/ease-out"）。</summary>
    public string Envelope { get; set; } = "";

    /// <summary>音色：混响/失真/空灵/金属（空=无）。</summary>
    public string Timbre { get; set; } = "";

    /// <summary>颤音频率（Hz，0=关）。</summary>
    public double VibratoRate { get; set; }

    /// <summary>颤音深度（半音，0=关）。</summary>
    public double VibratoDepth { get; set; }
}
