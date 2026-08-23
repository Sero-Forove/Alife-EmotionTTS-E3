using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Azuma.EmotionTTS.E3;

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
    readonly object gate = new();

    /// <summary>当前配置的中性兜底 ref（原 PresetName 的主 ref）。</summary>
    public string FallbackRefAudio { get; set; } = "";
    public string FallbackRefText { get; set; } = "";
    public string FallbackRefLanguage { get; set; } = "zh";

    /// <summary>重建库（配置变更时调用）。</summary>
    public void Rebuild(IEnumerable<EmotionRef> configItems, string installPath)
    {
        lock (gate)
        {
            items.Clear();
            if (configItems != null)
                items.AddRange(configItems);

            // 目录扫描补全
            if (!string.IsNullOrWhiteSpace(installPath))
                ScanDirectory(installPath.TrimEnd('\\', '/'));
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
                    tier = name[(idx + 1)..];
                    tier = EmotionDirectiveParser.Normalize(tier, new Dictionary<string, string>
                    {
                        ["弱"] = "弱", ["轻"] = "弱", ["中"] = "中", ["强"] = "强", ["重"] = "强",
                    }, "中");
                }
                // 目录名即情感名：直接采用（新增情感无需改代码；同义词纠错由 DirectiveParser 层负责）
                emotion = EmotionDirectiveParser.NormalizeKeepUnknown(emotion, new Dictionary<string, string>(), "正常");

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
}
