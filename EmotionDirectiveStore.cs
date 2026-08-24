namespace Azuma.EmotionTTS.E5;

/// <summary>
/// E5 语音指令暂存槽（实例级，非 AsyncLocal）。
/// EmotionTuneHandler（emotion 标签）写入，speak 消费。
///
/// 为什么不用 AsyncLocal：Alife 的 XmlStreamExecutor 是异步流式逐字符解析，
/// emotion 的 Opening/Content/Closing 回调跨多个 await 边界（含 Channel 读写），
/// AsyncLocal 值在这些边界会丢失。实例槽对象由主模块创建、handler 与 speak 共享引用。
/// Alife XML 解析是串行的（单 reader 循环），实例槽无需加锁；speak Opening 时重置即可。
/// </summary>
public sealed class EmotionDirectiveSlot
{
    /// <summary>当前 speak 回合的整句情感描述（&lt;emotion desc="…"/&gt;，完整自然语言，供旁路融合 LLM）。</summary>
    public string? EmotionDesc { get; set; }

    /// <summary>开始新一轮（speak Opening 时调用）。</summary>
    public void Begin()
    {
        EmotionDesc = null;
    }
}
