using System;
using System.Collections.Generic;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 注入门控：限制 [EmotionTTS] 系统消息（Poke）的注入频率，防止对话被系统消息刷屏卡死。
/// 三类注入各自独立节流 + 全局冷却：
/// - Hint（结构/增强效果提醒）：低频（默认 10 分钟一次）
/// - Error（报错，如情感未命中）：中频（默认 60 秒一次，避免刷屏但能及时纠正）
/// - Eval（声音评价反馈）：低频（默认 5 分钟一次，评价不必每次都注入）
/// 全局冷却：任意两次注入之间至少间隔 GlobalCooldownMs（默认 30 秒）。
/// </summary>
sealed class EmotionPokeGate
{
    public const string KindHint = "hint";
    public const string KindError = "error";
    public const string KindEval = "eval";

    readonly object gate = new();
    readonly Dictionary<string, long> lastByKind = new(StringComparer.Ordinal);
    long lastAny;
    bool anyEver;

    public long HintCooldownMs { get; set; } = 10 * 60 * 1000;   // 10 分钟
    public long ErrorCooldownMs { get; set; } = 60 * 1000;       // 60 秒
    public long EvalCooldownMs { get; set; } = 5 * 60 * 1000;    // 5 分钟
    public long GlobalCooldownMs { get; set; } = 30 * 1000;      // 全局 30 秒

    /// <summary>
    /// 尝试放行一次注入。返回 true 表示应该注入（并记录时间）；false 表示节流拦截。
    /// </summary>
    public bool TryAllow(string kind)
    {
        long now = Environment.TickCount64;
        lock (gate)
        {
            // 全局冷却：任意注入后至少间隔 GlobalCooldownMs
            if (anyEver && now - lastAny < GlobalCooldownMs)
                return false;

            long kindCooldown = kind switch
            {
                KindHint => HintCooldownMs,
                KindError => ErrorCooldownMs,
                KindEval => EvalCooldownMs,
                _ => GlobalCooldownMs,
            };
            if (lastByKind.TryGetValue(kind, out long last) && now - last < kindCooldown)
                return false;

            lastByKind[kind] = now;
            lastAny = now;
            anyEver = true;
            return true;
        }
    }
}
