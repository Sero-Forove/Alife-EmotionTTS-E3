using System;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// E3 标签 handler：注册 emotion（自闭合）与 ref（非自闭合）。
/// - &lt;emotion desc="自然语言情感描述"/&gt;：整句情感描述，**只进 DSP-LLM 曲线提示词，绝不念出**。
/// - &lt;ref emotion="开心"&gt;要用这个 ref 情感念出的对白&lt;/ref&gt;：**非自闭合**，
///   包住对白（主 LLM 用它切句，可不依赖标点），emotion 属性选 GPT-SoVITS 参考音频；
///   ref 标签本身（含属性）绝不念出，只有包住的对白进合成。
/// 主 LLM 只输出 speak + emotion + ref，不再写 tune/seg/字（曲线 DSP 由外部 DSP-LLM 整段生成）。
/// </summary>
public sealed class EmotionTuneHandler
{
    readonly ILogger logger;
    readonly EmotionDirectiveSlot slot;

    public EmotionTuneHandler(ILogger logger, EmotionDirectiveSlot slot)
    {
        this.logger = logger;
        this.slot = slot;
    }

    public XmlHandler BuildHandler()
    {
        var handler = new XmlHandler("EmotionTuneHandler");

        // ---- emotion：整句情感描述（自闭合，desc 只进 DSP-LLM）----
        handler.Functions.Add(new XmlFunction
        {
            Name = "emotion",
            Mode = FunctionMode.All,
            Invoker = (ctx, _) =>
            {
                try
                {
                    // 自闭合（OneShot）或闭合（Opening）时取 desc 一次；Content/Closing 透传
                    if (ctx.CallMode == CallMode.OneShot || ctx.CallMode == CallMode.Opening)
                    {
                        if (ctx.Parameters.TryGetValue("desc", out string? desc) && !string.IsNullOrWhiteSpace(desc))
                        {
                            slot.EmotionDesc = desc.Trim();
                            logger.LogInformation("[EmotionTTS] 情感描述：{Desc}", desc.Trim());
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[EmotionTTS] emotion 解析异常（兜底中性）");
                }
                return Task.CompletedTask;
            }
        });

        // ---- ref：情感段（非自闭合，包对白；emotion 属性选参考音频）----
        handler.Functions.Add(new XmlFunction
        {
            Name = "ref",
            Mode = FunctionMode.All,
            Invoker = (ctx, _) =>
            {
                try
                {
                    switch (ctx.CallMode)
                    {
                        case CallMode.Opening:
                            // 新段开始：记录情感（emotion 属性；缺省中性）
                            string emotion = "";
                            if (ctx.Parameters.TryGetValue("emotion", out string? emo) && !string.IsNullOrWhiteSpace(emo))
                                emotion = emo.Trim();
                            slot.RefInProgress = new EmotionDirectiveSlot.EmotionSegment { Emotion = emotion };
                            break;
                        case CallMode.Content:
                            // 累积对白（ref 标签包住的是要念的内容），并**消费 Content 阻止上推给 speak**
                            // （否则真实宿主 FlushContentBuffer 会把 ref 文本 append 到 aboveContentBuffer
                            //   再推给 speak，导致「ref 段 + speak 直接 Content」双重累积）
                            if (slot.RefInProgress != null && !string.IsNullOrWhiteSpace(ctx.Content))
                                slot.RefInProgress.Text += ctx.Content;
                            ctx.Content = "";
                            break;
                        case CallMode.Closing:
                            // 段结束：落进列表（ref 标签本身绝不进合成文本）
                            slot.CommitRefSegment();
                            ctx.Content = "";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[EmotionTTS] ref 解析异常");
                }
                return Task.CompletedTask;
            }
        });

        return handler;
    }
}
