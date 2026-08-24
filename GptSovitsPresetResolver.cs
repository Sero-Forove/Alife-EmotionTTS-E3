using System;
using System.Collections.Generic;
using System.IO;

namespace Azuma.EmotionTTS.E5;

static class GptSovitsPresetResolver
{
    public static GptSovitsPresetConfig Resolve(EmotionTTSConfig config) =>
        new()
        {
            GptWeight = config.GptWeight,
            SovitsWeight = config.SovitsWeight,
            RefAudio = config.RefAudio,
            RefText = config.RefText,
            // prompt_lang 统一归一（api_v2 校验枚举，变体直发会 400）
            RefLanguage = NormalizeLang(config.RefLanguage, "zh"),
        };

    public static string ResolvePath(string installPath, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        if (Path.IsPathRooted(path))
            return path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(installPath))
            return path.Replace('\\', '/'); // 无安装目录时按相对路径原样返回，避免误解析到进程 CWD
        string root = installPath.TrimEnd('\\', '/');
        return Path.GetFullPath(Path.Combine(root, path)).Replace('\\', '/');
    }

    public static string PythonPath(string installPath) =>
        Path.Combine(installPath.TrimEnd('\\', '/'), "runtime", "python.exe");

    /// <summary>
    /// 语种归一：小写化 + 别名映射（zh-cn/zh-hans/zh-tw → zh 等）。
    /// 未知语种回落 fallback，避免把不合法的 text_lang / prompt_lang 直发 API 触发 400。
    /// </summary>
    public static string NormalizeLang(string? lang, string fallback)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return fallback.Trim().ToLowerInvariant();

        string v = lang.Trim().ToLowerInvariant();
        return v switch
        {
            "zh" or "zh-cn" or "zh-sg" or "zh-tw" or "zh-hk" or "zh-mo" or "zh-hans" or "zh-hant" => "zh",
            "ja" or "ja-jp" => "ja",
            "en" or "en-us" or "en-gb" or "en-au" or "en-ca" or "en-uk" => "en",
            "ko" or "yue" => v,
            _ => fallback.Trim().ToLowerInvariant(),
        };
    }

    /// <summary>UI 下拉用：仅保留 zh/ja/en/ko/yue，其它回落 zh。</summary>
    public static string NormalizeUiLang(string? lang)
    {
        string v = NormalizeLang(lang, "zh");
        return v is "zh" or "ja" or "en" or "ko" or "yue" ? v : "zh";
    }
}

public class GptSovitsPresetConfig
{
    public string GptWeight { get; set; } = "";
    public string SovitsWeight { get; set; } = "";
    public string RefAudio { get; set; } = "";
    public string RefText { get; set; } = "";
    public string RefLanguage { get; set; } = "zh";
    /// <summary>辅助参考音频路径（多说话人音色融合 → api_v2 aux_ref_audio_paths）。</summary>
    public List<string> AuxRefAudios { get; set; } = new();
    /// <summary>语速因子（0.5~2.0；1.0=用配置 V2_SpeedFactor，否则覆盖）。</summary>
    public double SpeedFactor { get; set; } = 1.0;
}
