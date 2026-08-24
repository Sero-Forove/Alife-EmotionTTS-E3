using System;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Azuma.EmotionTTS.E5;

/// <summary>
/// E5 标签 handler：只注册 emotion（自闭合）。
/// - &lt;emotion desc="自然语言情感描述"/&gt;：整句情感描述，**只进旁路融合 LLM 的提示词，绝不念出**。
/// 主 LLM 只输出 speak + emotion；ref 标签已删除（ref 库保留，由旁路 LLM 智能选融合）。
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

        // ---- emotion：整句情感描述（自闭合，desc 只进旁路融合 LLM）----
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

        return handler;
    }
}
