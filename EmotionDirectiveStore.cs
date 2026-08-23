using System.Collections.Generic;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// E3 语音指令暂存槽（实例级，非 AsyncLocal）。
/// EmotionTuneHandler（emotion/ref 标签）写入，speak 消费。
///
/// 为什么不用 AsyncLocal：Alife 的 XmlStreamExecutor 是异步流式逐字符解析，
/// emotion/ref 的 Opening/Content/Closing 回调跨多个 await 边界（含 Channel 读写），
/// AsyncLocal 值在这些边界会丢失（实测：写入后消费到 null）。
/// 实例槽对象由主模块创建、handler 与 speak 共享引用，无跨上下文问题。
/// 注意：Alife XML 解析是串行的（单 reader 循环），同一时刻只有一个标签回调在跑，
/// 因此实例槽无需加锁；speak Opening 时重置即可。
/// </summary>
public sealed class EmotionDirectiveSlot
{
    /// <summary>E3 情感段（ref 标签切出的句段：文本 + 情感名 → 选 ref 音频）。</summary>
    public sealed class EmotionSegment
    {
        public string Text { get; set; } = "";
        public string Emotion { get; set; } = "";
    }

    /// <summary>当前 speak 回合的整句情感描述（&lt;emotion desc="…"/&gt;，完整自然语言，供 DSP-LLM 曲线）。</summary>
    public string? EmotionDesc { get; set; }

    /// <summary>当前 speak 回合的情感段列表（ref 标签切句；无 ref 时为整段一个中性段）。</summary>
    public List<EmotionSegment> Segments { get; } = new();

    /// <summary>当前 ref 标签的进行中缓冲（Opening 置情感，Content 累积文本，Closing 落段）。</summary>
    public EmotionSegment? RefInProgress { get; set; }

    /// <summary>开始新一轮（speak Opening 时调用）。</summary>
    public void Begin()
    {
        EmotionDesc = null;
        Segments.Clear();
        RefInProgress = null;
    }

    /// <summary>ref Closing：把进行中段落进列表，并清进行中。</summary>
    public void CommitRefSegment()
    {
        if (RefInProgress == null)
            return;
        if (!string.IsNullOrWhiteSpace(RefInProgress.Text))
            Segments.Add(new EmotionSegment
            {
                Text = RefInProgress.Text.Trim(),
                Emotion = RefInProgress.Emotion ?? "",
            });
        RefInProgress = null;
    }
}
