using System;
using System.Collections.Generic;
using System.Globalization;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 指令解析 + 容错归一：把主 LLM 输出的自由文本属性值做同义词纠错。
/// 设计目标：写错不报错、不尴尬失败循环——同义词归一，未知情感保留原值（新增情感无需改代码）。
/// </summary>
static class EmotionDirectiveParser
{
    // ==== 同义词纠错表（非白名单！只做 LLM 同义词/拼写纠错）====
    // 新情感不需要加到这里：NormalizeKeepUnknown 会把未知值原样保留，
    // ref 库按 ref/{情感}/ 目录名匹配（有目录就命中，没有才回退中性）。
    // 只有想给某情感加"别名/同义词"（如 LLM 说"骄傲"也应命中"自豪"）时才加。

    static readonly Dictionary<string, string> EmotionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["正常"] = "正常", ["neutral"] = "正常", ["平静"] = "正常", ["淡定"] = "正常",
        ["中性"] = "中性", ["冷静"] = "中性", ["平淡"] = "中性",
        ["喜悦"] = "喜悦", ["开心"] = "喜悦", ["高兴"] = "喜悦", ["快乐"] = "喜悦", ["happy"] = "喜悦", ["joy"] = "喜悦",
        ["悲伤"] = "悲伤", ["难过"] = "悲伤", ["伤心"] = "悲伤", ["低落"] = "悲伤", ["sad"] = "悲伤",
        ["愤怒"] = "愤怒", ["生气"] = "愤怒", ["怒"] = "愤怒", ["angry"] = "愤怒", ["anger"] = "愤怒",
        ["惊讶"] = "惊讶", ["吃惊"] = "惊讶", ["惊"] = "惊讶", ["surprise"] = "惊讶",
        ["兴奋"] = "兴奋", ["激动"] = "兴奋", ["excited"] = "兴奋",
        ["阴沉"] = "阴沉", ["威胁"] = "阴沉", ["冷漠"] = "阴沉", ["阴森"] = "阴沉", ["dark"] = "阴沉",
        ["虚弱"] = "虚弱", ["疲惫"] = "虚弱", ["有气无力"] = "虚弱", ["weak"] = "虚弱",
        // 角色 VO 情感素材（对应 ref/{情感}/ 目录）
        ["不满"] = "不满", ["嫌弃"] = "不满", ["抱怨"] = "不满",
        ["害羞"] = "害羞", ["羞涩"] = "害羞", ["不好意思"] = "害羞",
        ["请求"] = "请求", ["拜托"] = "请求", ["央求"] = "请求", ["恳求"] = "请求",
    };

    static readonly Dictionary<string, string> TierMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["弱"] = "弱", ["轻"] = "弱", ["轻微"] = "弱", ["low"] = "弱",
        ["中"] = "中", ["正常"] = "中", ["medium"] = "中",
        ["强"] = "强", ["重"] = "强", ["强烈"] = "强", ["high"] = "强",
    };

    static readonly Dictionary<string, string> BreathMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["吸气"] = "吸气", ["吸"] = "吸气", ["in"] = "吸气", ["inhale"] = "吸气",
        ["喘"] = "喘", ["喘气"] = "喘", ["喘息"] = "喘", ["pant"] = "喘", ["breath"] = "喘",
    };

    static readonly Dictionary<string, string> SpeedMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["慢"] = "慢", ["缓慢"] = "慢", ["slow"] = "慢",
        ["正常"] = "正常", ["normal"] = "正常",
        ["快"] = "快", ["快速"] = "快", ["fast"] = "快",
        ["极快"] = "极快", ["veryfast"] = "极快",
    };

    // ==== 钳制范围 ====
    // 大幅放宽到"不可听 ↔ 可听"：下限/上限只做物理安全防护（防 DSP 崩溃/超长），
    // 听感好坏由 AI 自主学习（反馈 + 知识表配方），插件不预设"好听的窄范围"。
    public const double MinSpeedFactor = 0.2;   // 极慢≈不可听（拉长 5 倍）
    public const double MaxSpeedFactor = 3.0;   // 极快（语无伦次级）
    public const double MinPitch = -12.0;       // 低八度（接近听不清）
    public const double MaxPitch = 12.0;        // 高八度（尖锐失真级）

    /// <summary>
    /// 档位词数值查询委托：输入 __word_{维度}_{档位}（如 "__word_speed_快"），返回数值串。
    /// 由 EmotionTTSSpeechModel 注入（查知识表），AI 可 ADDPREF 改写档位词数值。
    /// </summary>
    public static Func<string, string?>? WordPresetResolver;

    /// <summary>查档位词数值（知识表优先），未命中返回 null。</summary>
    static double? ResolveWordValue(string dimension, string word)
    {
        try
        {
            if (WordPresetResolver == null)
                return null;
            string? s = WordPresetResolver($"__word_{dimension}_{word}");
            if (string.IsNullOrWhiteSpace(s))
                return null;
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 方法解析委托：输入方法名（如 "睡前低语"），返回知识表 __method_{名} 的完整快照串。
    /// 由 EmotionTTSSpeechModel 注入。
    /// </summary>
    public static Func<string, string?>? MethodResolver;

    /// <summary>
    /// 教学文档查询回调：`method="__doc_*"` 自闭合查询时触发（参数=文档名，内容=文档文本），
    /// 由宿主延迟注入给 LLM（PokeSilent，避开 executor 锁链）。普通方法引用不触发。
    /// </summary>
    public static Action<string, string>? DocumentRequested;

    /// <summary>
    /// 应用方法引用（tune method="方法名"）：查知识表方法快照，把快照里的参数
    /// 填进 directive（仅填 directive 未显式写的字段——AI 显式属性优先）。
    /// 方法快照格式："emotion:害羞 tier:中 speed:0.5 volume:0.3 ..."。
    /// `__doc_*` 前缀 = 教学文档查询：注入给 LLM，不合并语音参数。
    /// </summary>
    public static void ApplyMethod(EmotionVoiceDirective directive, string methodName)
    {
        try
        {
            if (MethodResolver == null || string.IsNullOrWhiteSpace(methodName))
                return;
            string name = methodName.Trim();

            // __doc_* = 教学文档查询：返回文档给 LLM（宿主延迟 Poke），不合并语音参数
            if (name.StartsWith("__doc_", StringComparison.OrdinalIgnoreCase))
            {
                string? doc = MethodResolver(name);
                if (!string.IsNullOrWhiteSpace(doc))
                    DocumentRequested?.Invoke(name, doc);
                return;
            }

            string? snapshot = MethodResolver(name);
            if (string.IsNullOrWhiteSpace(snapshot))
                return;

            // 解析快照 "key:value key:value"（含 vibrato:4:0.12 特殊）
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < snapshot.Length)
            {
                // 找 key:
                int colon = snapshot.IndexOf(':', i);
                if (colon < 0) break;
                int keyStart = colon;
                while (keyStart > i && snapshot[keyStart - 1] != ' ' && snapshot[keyStart - 1] != ',')
                    keyStart--;
                string key = snapshot[keyStart..colon].Trim();
                if (key.Length == 0) { i = colon + 1; continue; }
                // 找值结束（下一个 空格后跟"词:" 或 逗号）
                int valEnd = snapshot.Length;
                for (int j = colon + 1; j < snapshot.Length; j++)
                {
                    if (snapshot[j] == ',' || (snapshot[j] == ' ' && j + 1 < snapshot.Length && snapshot[j + 1] != ' '))
                    {
                        // 空格后若跟 key: 形态则结束，否则继续
                        int nxt = snapshot.IndexOf(':', j + 1);
                        if (nxt > j && nxt - j <= 14 && snapshot[(j + 1)..nxt].Trim().Length > 0 &&
                            !snapshot[(j + 1)..nxt].Contains(' '))
                        {
                            valEnd = j;
                            break;
                        }
                    }
                    if (snapshot[j] == '，') { valEnd = j; break; }
                }
                string val = snapshot[(colon + 1)..valEnd].Trim().Trim(',', '，');
                if (key.Length > 0 && val.Length > 0)
                    pairs[key] = val;
                i = valEnd;
                while (i < snapshot.Length && (snapshot[i] == ' ' || snapshot[i] == ',' || snapshot[i] == '，'))
                    i++;
            }

            // 应用：只填 directive 未显式写的字段
            if (directive.Emotion == "正常" && pairs.TryGetValue("emotion", out string? emo))
                directive.Emotion = NormalizeKeepUnknown(emo, EmotionMap, "正常");
            if ((directive.Tier == "中" || string.IsNullOrEmpty(directive.Tier)) && pairs.TryGetValue("tier", out string? tier))
                directive.Tier = NormalizeKeepUnknown(tier, TierMap, "中");
            if (directive.Speed == 1.0 && pairs.TryGetValue("speed", out string? spd))
                directive.Speed = ParseSpeed(spd);
            if (directive.PitchOffset == 0 && pairs.TryGetValue("pitch", out string? pch))
                directive.PitchOffset = ParsePitch(pch);
            if (directive.Volume == 1.0 && pairs.TryGetValue("volume", out string? vol))
                directive.Volume = ParseVolume(vol);
            if (string.IsNullOrEmpty(directive.Breath) && pairs.TryGetValue("breath", out string? br))
                directive.Breath = Normalize(br, BreathMap, "");
            if (string.IsNullOrEmpty(directive.Timbre) && pairs.TryGetValue("timbre", out string? tm))
                directive.Timbre = ParseTimbre(tm);
            if (directive.VibratoRate == 0 && pairs.TryGetValue("vibrato", out string? vib))
            {
                ParseVibrato(vib, out double vr, out double vd);
                directive.VibratoRate = vr;
                directive.VibratoDepth = vd;
            }
        }
        catch (Exception)
        {
            // 方法应用失败不影响发声（兜底）
        }
    }

    /// <summary>解析 tune 顶层属性 → VoiceDirective（只填出现过的字段，未出现保持默认）。</summary>
    public static void ApplyTopLevel(EmotionVoiceDirective directive, IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("emotion", out string? emotion))
            directive.Emotion = NormalizeKeepUnknown(emotion, EmotionMap, "正常");
        // tier 用 KeepUnknown：强/中/弱 归一，**自定义档位（如"爆裂"）保留原值**，
        // 以便命中知识表 __tier_爆裂 配方（新增配方机制，AI 可自创档位）
        if (parameters.TryGetValue("tier", out string? tier))
            directive.Tier = NormalizeKeepUnknown(tier, TierMap, "中");
        if (parameters.TryGetValue("speed", out string? speed))
            directive.Speed = ParseSpeed(speed);
        if (parameters.TryGetValue("pitch", out string? pitch))
            directive.PitchOffset = ParsePitch(pitch);
        if (parameters.TryGetValue("volume", out string? volume))
            directive.Volume = ParseVolume(volume);
        if (parameters.TryGetValue("vol", out string? vol))
            directive.Volume = ParseVolume(vol);
        if (parameters.TryGetValue("breath", out string? breath))
            directive.Breath = Normalize(breath, BreathMap, "");
        if (parameters.TryGetValue("timbre", out string? timbre))
            directive.Timbre = ParseTimbre(timbre);
        if (parameters.TryGetValue("vibrato", out string? vibrato))
        {
            ParseVibrato(vibrato, out double vRate, out double vDepth);
            directive.VibratoRate = vRate;
            directive.VibratoDepth = vDepth;
        }
        else if (parameters.TryGetValue("vib", out string? vib))
        {
            ParseVibrato(vib, out double vRate2, out double vDepth2);
            directive.VibratoRate = vRate2;
            directive.VibratoDepth = vDepth2;
        }
        // 方法引用：method="方法名" → 查知识表快照合并（AI 显式属性优先）
        string? methodName = null;
        if (parameters.TryGetValue("method", out string? method) && !string.IsNullOrWhiteSpace(method))
            methodName = method;
        else if (parameters.TryGetValue("use", out string? useMethod) && !string.IsNullOrWhiteSpace(useMethod))
            methodName = useMethod;
        if (methodName != null)
            ApplyMethod(directive, methodName);
        // map="愤怒|强" 兼容简写：| 分隔的情感|强度
        if (parameters.TryGetValue("map", out string? map) && !string.IsNullOrWhiteSpace(map))
            ApplyMapShorthand(directive, map);
    }

    /// <summary>map 简写："愤怒" 或 "愤怒|强"。</summary>
    static void ApplyMapShorthand(EmotionVoiceDirective directive, string map)
    {
        string[] parts = map.Split('|', '，', ',');
        if (parts.Length == 0) return;
        string emo = parts[0].Trim();
        if (!string.IsNullOrWhiteSpace(emo))
            directive.Emotion = NormalizeKeepUnknown(emo, EmotionMap, directive.Emotion);
        if (parts.Length >= 2)
        {
            string tier = parts[1].Trim();
            if (!string.IsNullOrWhiteSpace(tier))
                directive.Tier = Normalize(tier, TierMap, directive.Tier);
        }
    }

    /// <summary>把属性值归一为白名单内合法值；未命中返回 fallback。</summary>
    public static string Normalize(string value, IReadOnlyDictionary<string, string> map, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        string v = value.Trim().Trim('*', '！', '!', '，', ',', '。');
        if (map.TryGetValue(v, out string? norm))
            return norm;
        // 尝试包含匹配（如 "很生气" → 愤怒）
        foreach (KeyValuePair<string, string> kv in map)
        {
            if (v.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) && kv.Key.Length >= 2)
                return kv.Value;
        }
        return fallback;
    }

    /// <summary>
    /// 归一但"未知值保留原值"（不清洗成 fallback）。
    /// 用于情感：白名单只做同义词纠错，新增情感无需改代码——
    /// LLM 写"自豪"（白名单没有）→ 保留"自豪" → ref 库有 ref/自豪/ 就命中，没有才回退中性。
    /// </summary>
    public static string NormalizeKeepUnknown(string value, IReadOnlyDictionary<string, string> map, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        string v = value.Trim().Trim('*', '！', '!', '，', ',', '。');
        if (map.TryGetValue(v, out string? norm))
            return norm;
        // 同义词/包含匹配
        foreach (KeyValuePair<string, string> kv in map)
        {
            if (v.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) && kv.Key.Length >= 2)
                return kv.Value;
        }
        // 未知：保留原值（trim 后），让 ref 库按目录名匹配
        return v;
    }

    /// <summary>解析语速：数值（1.2）或档位词（快/慢…），钳制到 [0.2,3.0]。档位词数值可被知识表改写。</summary>
    public static double ParseSpeed(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 1.0;
        string v = value.Trim().Trim('*', '！', '!');
        // 数值
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            return ClampSpeed(num);
        // 档位词：先查知识表改写值，未命中用内置默认
        string norm = Normalize(v, SpeedMap, "");
        double? learned = ResolveWordValue("speed", norm);
        if (learned.HasValue)
            return ClampSpeed(learned.Value);
        return norm switch
        {
            "慢" => 0.7,
            "快" => 1.6,
            "极快" => 2.4,
            _ => 1.0,
        };
    }

    /// <summary>解析音调：数值（+2/-1）或档位词，钳制到 [-12,12]。档位词数值可被知识表改写。</summary>
    public static double ParsePitch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        string v = value.Trim().Trim('*', '！', '!');
        // 数值（含 + 号）
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            return ClampPitch(num);
        // 档位词：尖/低沉（先查知识表改写值）
        if (v.Contains("尖", StringComparison.Ordinal) || v.Contains("高", StringComparison.Ordinal))
        {
            double? learned = ResolveWordValue("pitch", "尖");
            return learned.HasValue ? ClampPitch(learned.Value) : 5.0;
        }
        if (v.Contains("低沉", StringComparison.Ordinal) || v.Contains("低", StringComparison.Ordinal) ||
            v.Contains("沉", StringComparison.Ordinal))
        {
            double? learned = ResolveWordValue("pitch", "低沉");
            return learned.HasValue ? ClampPitch(learned.Value) : -5.0;
        }
        return 0;
    }

    /// <summary>
    /// 解析音色：已知预设/别名归一（混响/失真/空灵/金属 及中英别名），
    /// **未知音色名保留原值**（作为知识表 __timbre_{名} 的键，AI 可自创音色档位）。
    /// 组合（"混响+失真" / "混响+自创名"）按 + 分隔保留各段。
    /// </summary>
    public static string ParseTimbre(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string v = value.Trim().Trim('*', '！', '!');
        if (v is "无" or "正常" or "none" or "off")
            return "";

        var found = new List<string>();
        foreach (string raw in v.Split('+', '，', ','))
        {
            string part = raw.Trim();
            if (part.Length == 0)
                continue;
            string norm = NormalizeTimbreName(part);
            if (!found.Contains(norm))
                found.Add(norm);
        }
        return string.Join("+", found);
    }

    /// <summary>归一单个音色名：已知预设/别名 → 标准名；未知 → 原样保留（自创档位键）。</summary>
    static string NormalizeTimbreName(string part)
    {
        string p = part.Trim();
        if (p.Length == 0)
            return "";
        // 已知预设/别名（含包含匹配）
        if (p.Contains("混响", StringComparison.Ordinal) || p.Contains("reverb", StringComparison.OrdinalIgnoreCase))
            return "混响";
        if (p.Contains("空灵", StringComparison.Ordinal) || p.Contains("回声", StringComparison.Ordinal) ||
            p.Contains("echo", StringComparison.OrdinalIgnoreCase))
            return "空灵";
        if (p.Contains("失真", StringComparison.Ordinal) || p.Contains("沙哑", StringComparison.Ordinal) ||
            p.Contains("distortion", StringComparison.OrdinalIgnoreCase))
            return "失真";
        if (p.Contains("金属", StringComparison.Ordinal) || p.Contains("机械", StringComparison.Ordinal) ||
            p.Contains("metal", StringComparison.OrdinalIgnoreCase))
            return "金属";
        // 未知：原样保留（trim；去掉首尾多余符号）
        return p.Trim('*', '！', '!', '，', ',', ' ');
    }

    /// <summary>
    /// 解析颤音："频率:深度"（如 "5:0.3"=5Hz/0.3半音）或档位词（颤/微颤/大颤/抖音）。
    /// </summary>
    public static void ParseVibrato(string value, out double rate, out double depth)
    {
        rate = 0;
        depth = 0;
        if (string.IsNullOrWhiteSpace(value))
            return;
        string v = value.Trim().Trim('*', '！', '!');
        if (v is "无" or "正常" or "none" or "off")
            return;

        // 数值格式：频率:深度
        if (v.Contains(':'))
        {
            string[] parts = v.Split(':');
            if (parts.Length >= 2 &&
                double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double r) &&
                double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                rate = Math.Clamp(r, 1.0, 15.0);
                depth = Math.Clamp(d, 0.05, 1.5);
                return;
            }
        }
        else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double single))
        {
            // 纯数字：默认频率 5Hz，数字当深度
            rate = 5.0;
            depth = Math.Clamp(single, 0.05, 1.5);
            return;
        }

        // 档位词
        if (v.Contains("微颤", StringComparison.Ordinal) || v.Contains("轻颤", StringComparison.Ordinal))
        {
            rate = 4.0; depth = 0.12;
        }
        else if (v.Contains("大颤", StringComparison.Ordinal) || v.Contains("抖音", StringComparison.Ordinal))
        {
            rate = 6.0; depth = 0.5;
        }
        else if (v.Contains("颤", StringComparison.Ordinal))
        {
            rate = 5.0; depth = 0.25;
        }
    }

    public static double ClampSpeed(double v) => Math.Clamp(v, MinSpeedFactor, MaxSpeedFactor);
    public static double ClampPitch(double v) => Math.Clamp(v, MinPitch, MaxPitch);

    // ==== 音量钳制（0.0~2.0：耳语~爆发）====
    public const double MinVolume = 0.0;        // 0 = 完全静音（不可听）
    public const double MaxVolume = 3.0;        // 3 = 极响（爆发/呐喊级）

    /// <summary>
    /// 解析音量：数值（0.4 耳语 / 1.3 爆发）或档位词（耳语/正常/大声/爆发）。
    /// 支持渐变简写："0.4->1.2"（渐强）——渐变在展开层处理，这里取终点值。
    /// </summary>
    public static double ParseVolume(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 1.0;
        string v = value.Trim().Trim('*', '！', '!');
        // 渐变简写 "0.4->1.2"：取终点（展开层做插值）
        int arrow = v.IndexOf("->", StringComparison.Ordinal);
        if (arrow > 0)
            v = v[(arrow + 2)..].Trim();
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            return ClampVolume(num);
        // 档位词（先查知识表改写值）
        if (v.Contains("爆发", StringComparison.Ordinal) || v.Contains("大喊", StringComparison.Ordinal) ||
            v.Contains("大声", StringComparison.Ordinal))
        {
            double? learned = ResolveWordValue("volume", "爆发");
            return learned.HasValue ? ClampVolume(learned.Value) : 1.4;
        }
        if (v.Contains("耳语", StringComparison.Ordinal) || v.Contains("轻声", StringComparison.Ordinal) ||
            v.Contains("悄悄", StringComparison.Ordinal) || v.Contains("低语", StringComparison.Ordinal))
        {
            double? learned = ResolveWordValue("volume", "耳语");
            return learned.HasValue ? ClampVolume(learned.Value) : 0.5;
        }
        if (v.Contains("渐强", StringComparison.Ordinal))
        {
            double? learned = ResolveWordValue("volume", "渐强");
            return learned.HasValue ? ClampVolume(learned.Value) : 1.3;
        }
        if (v.Contains("渐弱", StringComparison.Ordinal))
        {
            double? learned = ResolveWordValue("volume", "渐弱");
            return learned.HasValue ? ClampVolume(learned.Value) : 0.6;
        }
        return 1.0;
    }

    public static double ClampVolume(double v) => Math.Clamp(v, MinVolume, MaxVolume);

    /// <summary>解析 seg 的 pitch/speed/breath/emotion/tier 属性到 SegDirective。</summary>
    public static void ApplySeg(SegDirective seg, IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("start", out string? start))
            seg.Start = start.Trim();
        if (parameters.TryGetValue("span", out string? span))
            seg.Span = span.Trim();
        if (parameters.TryGetValue("end", out string? end))
            seg.End = end.Trim();
        if (parameters.TryGetValue("pitch", out string? pitch))
            seg.Pitch = ParsePitch(pitch);
        if (parameters.TryGetValue("speed", out string? speed))
            seg.Speed = ParseSpeed(speed);
        if (parameters.TryGetValue("volume", out string? volume))
            seg.Volume = ParseVolume(volume);
        if (parameters.TryGetValue("vol", out string? vol))
            seg.Volume = ParseVolume(vol);
        if (parameters.TryGetValue("breath", out string? breath))
            seg.Breath = Normalize(breath, BreathMap, "");
        if (parameters.TryGetValue("emotion", out string? emotion))
            seg.Emotion = NormalizeKeepUnknown(emotion, EmotionMap, "");
        if (parameters.TryGetValue("tier", out string? tier))
            seg.Tier = NormalizeKeepUnknown(tier, TierMap, "");
        if (parameters.TryGetValue("timbre", out string? timbre))
            seg.Timbre = ParseTimbre(timbre);
        if (parameters.TryGetValue("vibrato", out string? vibrato))
        {
            ParseVibrato(vibrato, out double vr, out double vd);
            seg.VibratoRate = vr;
            seg.VibratoDepth = vd;
        }
        else if (parameters.TryGetValue("vib", out string? vib))
        {
            ParseVibrato(vib, out double vr2, out double vd2);
            seg.VibratoRate = vr2;
            seg.VibratoDepth = vd2;
        }
        if (parameters.TryGetValue("subdiv", out string? subdiv) &&
            int.TryParse(subdiv, out int sd))
            seg.SubDivisions = Math.Clamp(sd, 1, 8);
        if (parameters.TryGetValue("env", out string? env))
            seg.Envelope = env.Trim();
        if (parameters.TryGetValue("envelope", out string? envelope))
            seg.Envelope = envelope.Trim();
    }

    /// <summary>解析 字 的 pitch/speed/breath/volume/timbre/vibrato 属性到 CharDirective。</summary>
    public static void ApplyChar(CharDirective cd, IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("char", out string? c))
            cd.Char = c.Trim();
        if (parameters.TryGetValue("pitch", out string? pitch))
            cd.Pitch = ParsePitch(pitch);
        if (parameters.TryGetValue("speed", out string? speed))
            cd.Speed = ParseSpeed(speed);
        if (parameters.TryGetValue("volume", out string? volume))
            cd.Volume = ParseVolume(volume);
        if (parameters.TryGetValue("vol", out string? vol))
            cd.Volume = ParseVolume(vol);
        if (parameters.TryGetValue("breath", out string? breath))
            cd.Breath = Normalize(breath, BreathMap, "");
        if (parameters.TryGetValue("timbre", out string? timbre))
            cd.Timbre = ParseTimbre(timbre);
        if (parameters.TryGetValue("vibrato", out string? vibrato))
        {
            ParseVibrato(vibrato, out double vr, out double vd);
            cd.VibratoRate = vr;
            cd.VibratoDepth = vd;
        }
        else if (parameters.TryGetValue("vib", out string? vib))
        {
            ParseVibrato(vib, out double vr2, out double vd2);
            cd.VibratoRate = vr2;
            cd.VibratoDepth = vd2;
        }
        if (parameters.TryGetValue("pause", out string? pause) &&
            int.TryParse(pause, out int pauseMs))
            cd.PauseAfterMs = Math.Clamp(pauseMs, 0, 5000);
    }

    /// <summary>
    /// 把一次发声指令格式化为统一知识表的偏好值（LLM 可读的参数组合串）。
    /// 只含"有实际效果"的字段，保持精简可注入。
    /// </summary>
    public static string FormatDirectiveForKnowledge(EmotionVoiceDirective d)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.Emotion) && d.Emotion != "正常")
            parts.Add($"emotion={d.Emotion}");
        if (!string.IsNullOrWhiteSpace(d.Tier) && d.Tier != "中")
            parts.Add($"tier={d.Tier}");
        if (d.Speed != 1.0)
            parts.Add($"speed={d.Speed:0.##}");
        if (d.PitchOffset != 0)
            parts.Add($"pitch={d.PitchOffset:+0.#;-0.#}");
        if (d.Volume != 1.0)
            parts.Add($"volume={d.Volume:0.##}");
        if (!string.IsNullOrWhiteSpace(d.Breath))
            parts.Add($"breath={d.Breath}");
        if (d.Segments.Count > 0)
            parts.Add($"seg={d.Segments.Count}段");
        if (parts.Count == 0)
            parts.Add("emotion=" + (string.IsNullOrWhiteSpace(d.Emotion) ? "正常" : d.Emotion));
        return string.Join(" ", parts);
    }
}
