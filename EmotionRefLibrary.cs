using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Azuma.EmotionTTS.E5;

/// <summary>
/// 情感参考音频库：维护"情感→(ref_audio, ref_text, ref_lang)"映射。
/// 支持两种来源：
///  1. 配置表（EmotionTTSConfig.EmotionRefs，手填）
///  2. 目录扫描：ref/{情感}_{强度}/*.wav 自动归类（弱/中/强三档）
/// 情感按语义段切换 ref（GPT-SoVITS 情感的根本来源）。
/// </summary>
public sealed class EmotionRefLibrary
{
    /// <summary>单个情感 ref 条目。</summary>
    public sealed class EmotionRef
    {
        public string Emotion { get; set; } = "正常";
        public string Tier { get; set; } = "中";
        public string RefAudio { get; set; } = "";   // 相对 InstallPath 或绝对路径
        public string RefText { get; set; } = "";
        public string RefLanguage { get; set; } = "zh";
    }

    readonly List<EmotionRef> items = new();
    readonly List<EmotionRef> foreignItems = new(); // 异音色融合 ref（非本角色音色）
    readonly object gate = new();

    /// <summary>当前配置的中性兜底 ref（原 PresetName 的主 ref）。</summary>
    public string FallbackRefAudio { get; set; } = "";
    public string FallbackRefText { get; set; } = "";
    public string FallbackRefLanguage { get; set; } = "zh";

    /// <summary>重建库（只加载配置，不扫描目录）。目录里的音频由 UI「一键识别」手动扫描并回填配置后才会进库。</summary>
    public void Rebuild(IEnumerable<EmotionRef> configItems)
    {
        lock (gate)
        {
            items.Clear();
            if (configItems != null)
                items.AddRange(configItems);
        }
    }

    /// <summary>扫描 ref/{情感}_{强度}/ 目录，把扫到的 ref 追加进库（供 UI「一键识别」手动调用；配置优先、已存在的跳过）。</summary>
    public void ScanRefDirectory(string installPath)
    {
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(installPath))
                ScanDirectory(installPath.TrimEnd('\\', '/'));
        }
    }

    /// <summary>重建异音色融合 ref 库存（纯配置，不目录扫描）。</summary>
    public void RebuildForeign(IEnumerable<EmotionRef> configItems)
    {
        lock (gate)
        {
            foreignItems.Clear();
            if (configItems != null)
                foreignItems.AddRange(configItems);
        }
    }

    /// <summary>
    /// 扫描 foreign_ref/ 目录（扁平结构，文件名形如「【情感】台词.wav」），
    /// 把扫到的异音色 ref 追加进库（供 UI「一键识别」手动调用；配置优先、同名情感跳过）。
    /// </summary>
    public void ScanForeignDirectory(string installPath)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(installPath))
                return;
            string dir = Path.Combine(installPath.TrimEnd('\\', '/'), "foreign_ref");
            if (!Directory.Exists(dir))
                return;

            foreach (string wav in Directory.EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(wav);
                string emotion = name;
                string text = "";
                if (name.StartsWith("【") && name.IndexOf('】') > 0)
                {
                    int end = name.IndexOf('】');
                    emotion = name.Substring(1, end - 1);
                    text = name.Substring(end + 1);
                }
                if (string.IsNullOrWhiteSpace(emotion))
                    continue;

                // 配置优先：同名情感已存在则跳过
                if (foreignItems.Any(r => string.Equals(r.Emotion, emotion, StringComparison.OrdinalIgnoreCase)))
                    continue;

                foreignItems.Add(new EmotionRef
                {
                    Emotion = emotion,
                    Tier = "中",
                    RefAudio = wav.Replace('\\', '/'),
                    RefText = text,
                    RefLanguage = emotion.Contains("中文") ? "zh" : "ja",
                });
            }
        }
    }

    /// <summary>按 (情感, 强度) 选 ref；未命中回退同情感任意档，再回退中性。</summary>
    public EmotionRef? Resolve(string emotion, string tier)
    {
        lock (gate)
        {
            // 精确匹配 情感+强度
            EmotionRef? exact = items.FirstOrDefault(r =>
                string.Equals(r.Emotion, emotion, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Tier, tier, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            // 同情感任意档
            EmotionRef? anyTier = items.FirstOrDefault(r =>
                string.Equals(r.Emotion, emotion, StringComparison.OrdinalIgnoreCase));
            if (anyTier != null)
                return anyTier;

            return null;
        }
    }

    /// <summary>按 (情感, 强度) 从异音色库存选 ref；未命中回退同情感任意档。</summary>
    public EmotionRef? ResolveForeign(string emotion, string tier)
    {
        lock (gate)
        {
            EmotionRef? exact = foreignItems.FirstOrDefault(r =>
                string.Equals(r.Emotion, emotion, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Tier, tier, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            return foreignItems.FirstOrDefault(r =>
                string.Equals(r.Emotion, emotion, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>当前可用的情感集合（ref 表里的标准情感名，去重）。供 LLM 注入/审查。</summary>
    public IReadOnlyList<string> AvailableEmotions()
    {
        lock (gate)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EmotionRef r in items)
            {
                if (!string.IsNullOrWhiteSpace(r.Emotion))
                    set.Add(r.Emotion);
            }
            return set.ToArray();
        }
    }

    /// <summary>异音色融合库存当前可用的情感名集合（去重）。供 LLM 注入。</summary>
    public IReadOnlyList<string> AvailableForeignEmotions()
    {
        lock (gate)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EmotionRef r in foreignItems)
            {
                if (!string.IsNullOrWhiteSpace(r.Emotion))
                    set.Add(r.Emotion);
            }
            return set.ToArray();
        }
    }

    /// <summary>是否有指定情感的 ref。</summary>
    public bool HasEmotion(string emotion)
    {
        lock (gate)
            return items.Any(r => string.Equals(r.Emotion, emotion, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>已加载的条目（调试/UI）。</summary>
    public IReadOnlyList<EmotionRef> All
    {
        get
        {
            lock (gate)
                return items.ToArray();
        }
    }

    /// <summary>已加载的异音色条目（调试/UI）。</summary>
    public IReadOnlyList<EmotionRef> ForeignAll
    {
        get
        {
            lock (gate)
                return foreignItems.ToArray();
        }
    }

    /// <summary>
    /// 扫描 ref/{情感}_{强度}/ 目录（深度≤3，跳过常见噪音目录）。
    /// 文件名形如：ref/愤怒_强/x.wav → Emotion=愤怒 Tier=强
    /// 兼容：ref/愤怒/x.wav（无强度→中）
    /// </summary>
    void ScanDirectory(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (string dir in Directory.EnumerateDirectories(root, "ref", SearchOption.TopDirectoryOnly))
        {
            ScanRefRoot(dir);
        }

        // 也兼容 InstallPath 根下的 ref 子目录（深度 1）
        string refRoot = Path.Combine(root, "ref");
        if (Directory.Exists(refRoot))
            ScanRefRoot(refRoot);
    }

    void ScanRefRoot(string refRoot)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(refRoot, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(sub);
                // 解析 "情感_强度" 或 "情感"
                string emotion = name;
                string tier = "中";
                int idx = name.IndexOf('_');
                if (idx > 0)
                {
                    emotion = name[..idx];
                    tier = NormalizeTier(name[(idx + 1)..]);
                }
                // 目录名即情感名：直接采用（新增情感无需改代码；同义词纠错由旁路融合 LLM 语义层负责）
                emotion = NormalizeEmotion(emotion);

                foreach (string wav in Directory.EnumerateFiles(sub, "*.wav", SearchOption.TopDirectoryOnly))
                {
                    // 已有精确条目则跳过（配置优先）
                    bool exists = items.Any(r =>
                        string.Equals(r.Emotion, emotion, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Tier, tier, StringComparison.OrdinalIgnoreCase));
                    if (exists)
                        continue;

                    items.Add(new EmotionRef
                    {
                        Emotion = emotion,
                        Tier = tier,
                        RefAudio = wav.Replace('\\', '/'),
                    });
                }
            }
        }
        catch (Exception)
        {
            // 扫描失败不影响主流程
        }
    }

    /// <summary>强度档归一：弱/轻→弱，中→中，强/重→强，未知→中。</summary>
    static string NormalizeTier(string tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
            return "中";
        string v = tier.Trim().Trim('*', '！', '!', '，', ',', '。');
        return v switch
        {
            "弱" or "轻" => "弱",
            "中" or "正常" => "中",
            "强" or "重" => "强",
            _ => "中",
        };
    }

    /// <summary>情感名归一：trim + 去首尾符号 + 原样保留（新增情感无需改代码）。</summary>
    static string NormalizeEmotion(string emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion))
            return "正常";
        string v = emotion.Trim().Trim('*', '！', '!', '，', ',', '。');
        return v.Length == 0 ? "正常" : v;
    }
}
