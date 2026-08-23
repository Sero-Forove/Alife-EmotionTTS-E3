using System;
using System.IO;
using System.Text;

namespace Azuma.EmotionTTS.E3;

static class GptSovitsCommandBuilder
{
    public static string BuildStartCommand(EmotionTTSConfig config)
    {
        if (GptSovitsConfigHelper.IsLegacyMode(config))
            return config.StartCommand.Trim();

        // V1 桌宠兼容：优先用户 StartCommand；否则按安装目录 + 预设自动生成 api.py
        if (GptSovitsConfigHelper.IsV1ZipMode(config))
        {
            if (!string.IsNullOrWhiteSpace(config.StartCommand))
                return config.StartCommand.Trim();
            return BuildV1ApiPyCommand(config);
        }

        if (string.IsNullOrWhiteSpace(config.InstallPath))
            throw new InvalidOperationException("【GPT-SoVITS】请填写 InstallPath 或 StartCommand（旧版）");

        string root = config.InstallPath.TrimEnd('\\', '/');
        string python = GptSovitsPresetResolver.PythonPath(root);
        if (!File.Exists(python))
            throw new FileNotFoundException($"未找到 Python: {python}");

        // V2 流式快速模式
        string api = Path.Combine(root, "api_v2.py");
        if (!File.Exists(api))
            throw new FileNotFoundException($"未找到 api_v2.py: {api}");
        string yaml = config.V2_TtsConfigPath;
        return $"\"{python}\" \"{api}\" -p {config.Port} -c \"{yaml}\"";
    }

    /// <summary>
    /// 根据扫描预设生成 api.py 启动命令（桌宠兼容模式）。
    /// </summary>
    public static string BuildV1ApiPyCommand(EmotionTTSConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.InstallPath))
            throw new InvalidOperationException(
                "【GPT-SoVITS/V1】请填写安装目录并扫描预设，或手动填写 StartCommand");

        string root = config.InstallPath.TrimEnd('\\', '/');
        string python = GptSovitsPresetResolver.PythonPath(root);
        if (!File.Exists(python))
            throw new FileNotFoundException($"未找到 Python: {python}");

        string api = Path.Combine(root, "api.py");
        if (!File.Exists(api))
            throw new FileNotFoundException($"未找到 api.py: {api}（桌宠兼容模式需要 api.py）");

        if (string.IsNullOrWhiteSpace(config.SovitsWeight) || string.IsNullOrWhiteSpace(config.GptWeight))
            throw new InvalidOperationException("【GPT-SoVITS/V1】请先扫描并选择音色预设（缺少 GPT/SoVITS 权重）");

        string sovits = GptSovitsPresetResolver.ResolvePath(root, config.SovitsWeight);
        string gpt = GptSovitsPresetResolver.ResolvePath(root, config.GptWeight);
        string refAudio = GptSovitsPresetResolver.ResolvePath(root, config.RefAudio);
        string refText = config.RefText ?? "";
        string refLang = string.IsNullOrWhiteSpace(config.RefLanguage)
            ? (config.DefaultLang ?? "zh")
            : config.RefLanguage;
        refLang = GptSovitsPresetResolver.NormalizeLang(refLang, "zh");

        var sb = new StringBuilder();
        sb.Append($"\"{python}\" \"{api}\"");
        sb.Append($" -s \"{sovits}\"");
        sb.Append($" -g \"{gpt}\"");
        if (!string.IsNullOrWhiteSpace(refAudio))
            sb.Append($" -dr \"{refAudio}\"");
        if (!string.IsNullOrWhiteSpace(refText))
            sb.Append($" -dt \"{EscapeForCmd(refText)}\"");
        sb.Append($" -dl {refLang}");
        sb.Append($" -p {config.Port}");

        if (!string.IsNullOrWhiteSpace(config.V1_Device))
            sb.Append($" -d {config.V1_Device.Trim()}");
        if (config.V1_HalfPrecision)
            sb.Append(" -hp");

        return sb.ToString();
    }

    /// <summary>V1 默认 GET 模板（含 {text}；运行时会按 speak lang 覆盖 text_language）</summary>
    public static string BuildV1ApiUrlTemplate(EmotionTTSConfig config)
    {
        string lang = GptSovitsPresetResolver.NormalizeLang(config.DefaultLang, "zh");
        return $"http://127.0.0.1:{config.Port}/?text={{text}}&text_language={lang}";
    }

    /// <summary>选预设后同步 V1 的 StartCommand / ApiUrl（保留用户已手写的 StartCommand）</summary>
    public static void SyncV1LaunchFromPreset(EmotionTTSConfig config, bool forceStartCommand = false)
    {
        if (!GptSovitsConfigHelper.IsV1ZipMode(config))
            return;

        try
        {
            if (forceStartCommand || string.IsNullOrWhiteSpace(config.StartCommand))
                config.StartCommand = BuildV1ApiPyCommand(config);
        }
        catch
        {
            // 权重未齐时不写命令，留给用户补全
        }

        // 端口/语言变化时刷新默认模板（不覆盖明显自定义的 URL）
        if (string.IsNullOrWhiteSpace(config.ApiUrl) || LooksLikeDefaultApiUrl(config.ApiUrl))
            config.ApiUrl = BuildV1ApiUrlTemplate(config);
    }

    static bool LooksLikeDefaultApiUrl(string url) =>
        url.Contains("text={text}", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("text_language=", StringComparison.OrdinalIgnoreCase);

    static string EscapeForCmd(string text) =>
        text.Replace("\"", "\\\"", StringComparison.Ordinal);
}
