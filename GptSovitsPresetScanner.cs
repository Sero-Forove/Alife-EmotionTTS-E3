using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Azuma.EmotionTTS.E5;

public sealed class GptSovitsScannedPreset
{
    public required string Name { get; init; }
    public required string GptWeight { get; init; }
    public required string SovitsWeight { get; init; }
    public string RefAudio { get; init; } = "";
    public string RefText { get; init; } = "";
    public string RefLanguage { get; init; } = "zh";
    /// <summary>权重目录推断的模型代际（如 v2Pro），与 api_v2/api.py 接口版本不同</summary>
    public string WeightVersion { get; init; } = "v2";
}

/// <summary>扫描 InstallPath 后得到的安装能力与接口支持情况</summary>
public sealed class GptSovitsInstallCapabilities
{
    public bool DirectoryExists { get; init; }
    public bool HasPythonRuntime { get; init; }
    public bool HasApiV2 { get; init; }
    public bool HasApiV1 { get; init; }
    public bool HasV2InferConfig { get; init; }
    public int GptWeightFileCount { get; init; }
    public int SovitsWeightFileCount { get; init; }
    public int? SuggestedPort { get; init; }

    public bool SupportsV2 => DirectoryExists && HasPythonRuntime && HasApiV2;
    public bool SupportsV1 => DirectoryExists && HasPythonRuntime && HasApiV1;

    public string BuildSummaryChinese()
    {
        if (!DirectoryExists)
            return "安装目录不存在，请填写 GPT-SoVITS 解压后的根目录";

        List<string> lines = new();
        lines.Add(HasPythonRuntime ? "已找到内置 Python（runtime/python.exe）" : "未找到 runtime/python.exe，无法自动启动服务");

        if (HasApiV2)
            lines.Add("支持 V2 接口（api_v2.py）：可流式合成、推荐搭配 v2/v2Pro 权重");
        else
            lines.Add("未找到 api_v2.py，不能使用 V2 接口");

        if (HasApiV1)
            lines.Add("支持 V1 接口（api.py）：旧版 API，仅支持落盘 WAV 后播放");
        else
            lines.Add("未找到 api.py，不能使用 V1 接口");

        if (HasApiV2 && !HasV2InferConfig)
            lines.Add("警告：未找到默认 GPT_SoVITS/configs/tts_infer.yaml，请检查 V2 配置路径");

        if (GptWeightFileCount > 0 || SovitsWeightFileCount > 0)
            lines.Add($"权重文件：GPT {GptWeightFileCount} 个、SoVITS {SovitsWeightFileCount} 个");
        if (SuggestedPort is int port)
            lines.Add($"检测到服务端口建议值：{port}");

        if (SupportsV2 && !SupportsV1)
            lines.Add("结论：请使用「V2 接口（api_v2）」");
        else if (SupportsV1 && !SupportsV2)
            lines.Add("结论：此目录为旧版包，请使用「V1 接口（api.py）」");
        else if (SupportsV2 && SupportsV1)
            lines.Add("结论：同时检测到 V1/V2，新版包请优先选 V2");
        else
            lines.Add("结论：目录不完整，请确认是否为 GPT-SoVITS 根目录");

        return string.Join("\n", lines);
    }
}

public sealed class GptSovitsScanResult
{
    public List<GptSovitsScannedPreset> Presets { get; init; } = [];
    public GptSovitsInstallCapabilities Capabilities { get; init; } = new();
    public string Message { get; init; } = "";
    /// <summary>扫描到的全部可用文件（引导步骤 3 全量列表）</summary>
    public GptSovitsFileInventory Inventory { get; init; } = new();
}

/// <summary>扫描目录中的单个文件项</summary>
public sealed class GptSovitsScannedFileItem
{
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public string Tag { get; init; } = "";
    public bool Exists { get; init; } = true;

    public string SizeDisplay =>
        SizeBytes < 1024 ? $"{SizeBytes} B" :
        SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:0.#} KB" :
        $"{SizeBytes / (1024.0 * 1024.0):0.#} MB";
}

/// <summary>安装目录全量文件清单（接口/权重/参考音等）</summary>
public sealed class GptSovitsFileInventory
{
    public List<GptSovitsScannedFileItem> ApiScripts { get; init; } = [];
    public List<GptSovitsScannedFileItem> PythonRuntimes { get; init; } = [];
    public List<GptSovitsScannedFileItem> GptWeights { get; init; } = [];
    public List<GptSovitsScannedFileItem> SovitsWeights { get; init; } = [];
    public List<GptSovitsScannedFileItem> RefAudios { get; init; } = [];
    public List<GptSovitsScannedFileItem> OrphanGptWeights { get; init; } = [];
    public List<GptSovitsScannedFileItem> OrphanSovitsWeights { get; init; } = [];
}

public static class GptSovitsPresetScanner
{
    static readonly string[] GptDirs =
    [
        "GPT_weights_v2ProPlus", "GPT_weights_v2Pro", "GPT_weights_v4",
        "GPT_weights_v3", "GPT_weights_v2", "GPT_weights",
    ];

    static readonly string[] SovitsDirs =
    [
        "SoVITS_weights_v2ProPlus", "SoVITS_weights_v2Pro", "SoVITS_weights_v4",
        "SoVITS_weights_v3", "SoVITS_weights_v2", "SoVITS_weights",
    ];

    static readonly Regex GptEpochRegex = new(@"-e(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SovitsEpochRegex = new(@"_e(\d+)_s\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly string DefaultV2ConfigRelative = "GPT_SoVITS/configs/tts_infer.yaml";


    public static GptSovitsScanResult ScanFull(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return new GptSovitsScanResult
            {
                Message = "请先填写安装目录",
            };
        }

        string root = installPath.Trim().TrimEnd('\\', '/');
        if (!Directory.Exists(root))
        {
            return new GptSovitsScanResult
            {
                Capabilities = new GptSovitsInstallCapabilities { DirectoryExists = false },
                Message = $"目录不存在: {root}",
            };
        }

        var gptFiles = CollectWeights(root, GptDirs, "*.ckpt");
        var sovitsFiles = CollectWeights(root, SovitsDirs, "*.pth");
        GptSovitsInstallCapabilities caps = BuildCapabilities(root, gptFiles, sovitsFiles, DetectSuggestedPort(root));
        (List<GptSovitsScannedPreset> presets, string presetMessage) = ScanPresets(root, caps, gptFiles, sovitsFiles);
        GptSovitsFileInventory inventory = BuildInventory(root, gptFiles, sovitsFiles, presets);
        string message = presetMessage;
        if (caps.DirectoryExists)
            message = string.IsNullOrWhiteSpace(presetMessage)
                ? caps.BuildSummaryChinese()
                : $"{presetMessage}\n{caps.BuildSummaryChinese()}";
        return new GptSovitsScanResult
        {
            Presets = presets,
            Capabilities = caps,
            Message = message,
            Inventory = inventory,
        };
    }

    static GptSovitsFileInventory BuildInventory(
        string root,
        List<WeightFile> gptFiles,
        List<WeightFile> sovitsFiles,
        List<GptSovitsScannedPreset> presets)
    {
        HashSet<string> pairedGpt = new(
            presets.Select(p => p.GptWeight), StringComparer.OrdinalIgnoreCase);
        HashSet<string> pairedSovits = new(
            presets.Select(p => p.SovitsWeight), StringComparer.OrdinalIgnoreCase);

        List<GptSovitsScannedFileItem> gptItems = gptFiles
            .Select(w => ToFileItem(root, w.RelativePath, w.FullPath,
                InferWeightVersion(w.RelativePath, "")))
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<GptSovitsScannedFileItem> sovitsItems = sovitsFiles
            .Select(w => ToFileItem(root, w.RelativePath, w.FullPath,
                InferWeightVersion("", w.RelativePath)))
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<GptSovitsScannedFileItem> apiScripts = new();
        AddProbeFile(apiScripts, root, "api_v2.py", "V2 接口");
        AddProbeFile(apiScripts, root, "api.py", "V1 接口");
        AddProbeFile(apiScripts, root, DefaultV2ConfigRelative.Replace('/', Path.DirectorySeparatorChar), "V2 推理配置");

        List<GptSovitsScannedFileItem> pyRuntimes = new();
        AddProbeFile(pyRuntimes, root, Path.Combine("runtime", "python.exe"), "内置 Python");

        List<GptSovitsScannedFileItem> wavs = CollectReferenceAudios(root)
            .Select(w =>
            {
                string full = Path.IsPathRooted(w.RelativePath)
                    ? w.RelativePath
                    : Path.Combine(root, w.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                return ToFileItem(root, w.RelativePath, full, "参考音频");
            })
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GptSovitsFileInventory
        {
            ApiScripts = apiScripts,
            PythonRuntimes = pyRuntimes,
            GptWeights = gptItems,
            SovitsWeights = sovitsItems,
            RefAudios = wavs,
            OrphanGptWeights = gptItems.Where(f => !pairedGpt.Contains(f.RelativePath)).ToList(),
            OrphanSovitsWeights = sovitsItems.Where(f => !pairedSovits.Contains(f.RelativePath)).ToList(),
        };
    }

    static void AddProbeFile(List<GptSovitsScannedFileItem> list, string root, string relative, string tag)
    {
        string full = Path.Combine(root, relative);
        bool exists = File.Exists(full);
        list.Add(new GptSovitsScannedFileItem
        {
            RelativePath = relative.Replace('\\', '/'),
            FileName = Path.GetFileName(relative),
            SizeBytes = exists ? new FileInfo(full).Length : 0,
            LastWriteUtc = exists ? File.GetLastWriteTimeUtc(full) : DateTime.MinValue,
            Tag = tag,
            Exists = exists,
        });
    }

    static GptSovitsScannedFileItem ToFileItem(string root, string relative, string fullPath, string tag)
    {
        long size = 0;
        DateTime mtime = DateTime.MinValue;
        try
        {
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                size = fi.Length;
                mtime = fi.LastWriteTimeUtc;
            }
        }
        catch { /* ignore */ }

        return new GptSovitsScannedFileItem
        {
            RelativePath = relative.Replace('\\', '/'),
            FileName = Path.GetFileName(relative),
            SizeBytes = size,
            LastWriteUtc = mtime,
            Tag = tag,
            Exists = true,
        };
    }

    static GptSovitsInstallCapabilities BuildCapabilities(
        string root,
        List<WeightFile> gptFiles,
        List<WeightFile> sovitsFiles,
        int? suggestedPort) =>
        new()
        {
            DirectoryExists = true,
            HasPythonRuntime = File.Exists(Path.Combine(root, "runtime", "python.exe")),
            HasApiV2 = File.Exists(Path.Combine(root, "api_v2.py")),
            HasApiV1 = File.Exists(Path.Combine(root, "api.py")),
            HasV2InferConfig = File.Exists(Path.Combine(root, DefaultV2ConfigRelative.Replace('/', Path.DirectorySeparatorChar))),
            GptWeightFileCount = gptFiles.Count,
            SovitsWeightFileCount = sovitsFiles.Count,
            SuggestedPort = suggestedPort,
        };


    static (List<GptSovitsScannedPreset> Presets, string Message) ScanPresets(
        string root,
        GptSovitsInstallCapabilities caps,
        List<WeightFile> gptFiles,
        List<WeightFile> sovitsFiles)
    {
        if (!caps.DirectoryExists)
            return ([], $"目录不存在: {root}");

        if (gptFiles.Count == 0 && sovitsFiles.Count == 0)
            return ([], "未找到 .ckpt / .pth 权重，请确认 GPT-SoVITS 安装目录");

        var wavIndex = CollectReferenceAudios(root);
        var listEntries = ParseListFiles(root);

        Dictionary<string, WeightFile> gptByKey = GroupGpt(gptFiles);
        Dictionary<string, WeightFile> sovitsByKey = GroupSovits(sovitsFiles);

        HashSet<string> allKeys = new(gptByKey.Keys, StringComparer.OrdinalIgnoreCase);
        allKeys.UnionWith(sovitsByKey.Keys);

        List<GptSovitsScannedPreset> presets = new();
        foreach (string key in allKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            gptByKey.TryGetValue(key, out WeightFile? gpt);
            sovitsByKey.TryGetValue(key, out WeightFile? sovits);
            if (gpt == null || sovits == null)
                continue;

            string displayName = gpt.DisplayName;
            string? refAudio = FindRefAudio(root, displayName, wavIndex);
            string refText = "";
            string refLang = "zh";
            if (!string.IsNullOrEmpty(refAudio))
                (refText, refLang) = FindRefText(root, refAudio, displayName, listEntries);

            presets.Add(new GptSovitsScannedPreset
            {
                Name = displayName,
                GptWeight = gpt.RelativePath,
                SovitsWeight = sovits.RelativePath,
                RefAudio = refAudio ?? "",
                RefText = refText,
                RefLanguage = refLang,
                WeightVersion = InferWeightVersion(gpt.RelativePath, sovits.RelativePath),
            });
        }

        if (presets.Count == 0)
        {
            return ([], gptFiles.Count > 0 && sovitsFiles.Count > 0
                ? "找到权重但无法配对（GPT 与 SoVITS 名称不匹配），请检查文件名"
                : $"仅找到 GPT {gptFiles.Count} 个 / SoVITS {sovitsFiles.Count} 个，需成对存在");
        }

        return (presets, $"扫描完成：找到 {presets.Count} 个可用音色预设");
    }


    /// <summary>全量写入预设：参考音/文本空则清空，禁止沿用上一音色文本。</summary>
    public static void ApplyToConfig(EmotionTTSConfig config, GptSovitsScannedPreset preset)
    {
        config.PresetName = preset.Name;
        config.GptWeight = preset.GptWeight;
        config.SovitsWeight = preset.SovitsWeight;
        config.RefAudio = preset.RefAudio ?? "";
        config.RefText = preset.RefText ?? "";
        if (!string.IsNullOrWhiteSpace(preset.RefLanguage))
            config.RefLanguage = preset.RefLanguage;
        else if (string.IsNullOrWhiteSpace(config.RefLanguage))
            config.RefLanguage = "zh";
    }

    static List<WeightFile> CollectWeights(string root, string[] dirs, string pattern)
    {
        List<WeightFile> files = new();
        foreach (string dirName in dirs)
        {
            string dir = Path.Combine(root, dirName);
            if (!Directory.Exists(dir))
                continue;

            foreach (string full in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(full);
                if (name.StartsWith("pretrained", StringComparison.OrdinalIgnoreCase))
                    continue;
                files.Add(new WeightFile
                {
                    RelativePath = ToRelative(root, full),
                    DisplayName = pattern == "*.ckpt" ? ExtractGptName(name) : ExtractSovitsName(name),
                    MatchKey = NormalizeKey(pattern == "*.ckpt" ? ExtractGptName(name) : ExtractSovitsName(name)),
                    Epoch = ParseEpoch(name, pattern == "*.ckpt"),
                    FullPath = full,
                });
            }
        }
        return files;
    }

    static Dictionary<string, WeightFile> GroupGpt(List<WeightFile> files)
    {
        Dictionary<string, WeightFile> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (WeightFile f in files.OrderByDescending(x => x.Epoch).ThenByDescending(x => File.GetLastWriteTimeUtc(x.FullPath)))
        {
            if (!map.ContainsKey(f.MatchKey))
                map[f.MatchKey] = f;
        }
        return map;
    }

    static Dictionary<string, WeightFile> GroupSovits(List<WeightFile> files) => GroupGpt(files);

    static List<WavFile> CollectReferenceAudios(string root)
    {
        List<WavFile> wavs = new();
        void ScanDir(string dir, int depth)
        {
            if (depth > 2) return;
            try
            {
                foreach (string full in Directory.EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly))
                {
                    string rel = ToRelative(root, full);
                    string fileName = Path.GetFileNameWithoutExtension(full);
                    wavs.Add(new WavFile
                    {
                        RelativePath = rel,
                        FileName = Path.GetFileName(full),
                        NormalizedName = NormalizeKey(fileName),
                    });
                }
                if (depth < 2)
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        string subName = Path.GetFileName(sub);
                        if (ShouldSkipDir(subName)) continue;
                        ScanDir(sub, depth + 1);
                    }
                }
            }
            catch { }
        }

        ScanDir(root, 0);
        return wavs;
    }

    static bool ShouldSkipDir(string name) =>
        name is "runtime" or "logs" or "TEMP" or "tmp" or "node_modules" or ".git" or "__pycache__";

    static string? FindRefAudio(string root, string presetName, List<WavFile> wavIndex)
    {
        string key = NormalizeKey(presetName);
        string[] preferredSuffixes = ["参考音频", "ref", "reference"];

        WavFile? best = null;
        int bestScore = int.MinValue;
        foreach (WavFile wav in wavIndex)
        {
            int score = ScoreRefAudio(key, presetName, wav, preferredSuffixes);
            if (score > bestScore)
            {
                bestScore = score;
                best = wav;
            }
        }

        return bestScore > 0 ? best?.RelativePath : null;
    }

    static int ScoreRefAudio(string key, string presetName, WavFile wav, string[] suffixes)
    {
        string norm = wav.NormalizedName;
        if (norm == key)
            return 100;
        if (norm.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string suf in suffixes)
            {
                if (wav.FileName.Contains(presetName, StringComparison.OrdinalIgnoreCase) &&
                    wav.FileName.Contains(suf, StringComparison.OrdinalIgnoreCase))
                    return 90;
            }
            return 70;
        }
        if (norm.Contains(key, StringComparison.OrdinalIgnoreCase))
            return 50;
        return 0;
    }

    static List<ListEntry> ParseListFiles(string root)
    {
        List<ListEntry> entries = new();
        try
        {
            foreach (string listFile in Directory.EnumerateFiles(root, "*.list", SearchOption.AllDirectories))
            {
                if (listFile.Contains($"{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}") ||
                    listFile.Contains("/logs/"))
                    continue;

                string[] lines;
                try { lines = File.ReadAllLines(listFile, Encoding.UTF8); }
                catch { continue; }

                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#'))
                        continue;
                    string[] parts = line.Split('|');
                    if (parts.Length < 4)
                        continue;
                    entries.Add(new ListEntry
                    {
                        AudioPath = parts[0].Trim().Replace('\\', '/'),
                        Language = parts[2].Trim().ToLowerInvariant(),
                        Text = parts[3].Trim(),
                    });
                }
            }
        }
        catch { }
        return entries;
    }

    static (string Text, string Lang) FindRefText(string root, string refAudioRel, string presetName,
        List<ListEntry> listEntries)
    {
        string refFile = Path.GetFileName(refAudioRel);
        foreach (ListEntry e in listEntries)
        {
            if (e.AudioPath.EndsWith(refFile, StringComparison.OrdinalIgnoreCase) ||
                e.AudioPath.Contains(refFile, StringComparison.OrdinalIgnoreCase))
                return (e.Text, NormalizeListLang(e.Language));
        }

        string full = Path.Combine(root, refAudioRel.Replace('/', Path.DirectorySeparatorChar));
        string txtSidecar = Path.ChangeExtension(full, ".txt");
        if (File.Exists(txtSidecar))
        {
            try
            {
                string text = File.ReadAllText(txtSidecar, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return (text, InferLangFromText(text));
            }
            catch { }
        }

        string namedTxt = Path.Combine(Path.GetDirectoryName(full) ?? root, $"{presetName}.txt");
        if (File.Exists(namedTxt))
        {
            try
            {
                string text = File.ReadAllText(namedTxt, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return (text, InferLangFromText(text));
            }
            catch { }
        }

        return ("", "zh");
    }

    static string InferLangFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "zh";

        foreach (char c in text)
        {
            if (c is >= '\u3040' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF')
                return "ja";
        }

        foreach (char c in text)
        {
            if (c is >= '\uAC00' and <= '\uD7AF')
                return "ko";
        }

        bool hasLatin = false;
        bool hasCjk = false;
        foreach (char c in text)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                hasLatin = true;
            if (c is >= '\u4E00' and <= '\u9FFF')
                hasCjk = true;
        }

        if (hasLatin && !hasCjk)
            return "en";

        return "zh";
    }

    static string NormalizeListLang(string lang) => lang switch
    {
        "zh" or "yue" or "auto" => lang,
        "ja" or "en" or "ko" => lang,
        _ => "zh",
    };

    static string InferWeightVersion(string gptPath, string sovitsPath)
    {
        string combined = (gptPath + sovitsPath).ToLowerInvariant();
        if (combined.Contains("v2proplus")) return "v2ProPlus";
        if (combined.Contains("v2pro")) return "v2Pro";
        if (combined.Contains("v4")) return "v4";
        if (combined.Contains("v3")) return "v3";
        if (combined.Contains("v2")) return "v2";
        return "v1";
    }

    static string ExtractGptName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        Match m = GptEpochRegex.Match(name);
        if (m.Success && m.Index > 0)
            return name[..m.Index];
        return name;
    }

    static string ExtractSovitsName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        Match m = SovitsEpochRegex.Match(name);
        if (m.Success && m.Index > 0)
            return name[..m.Index];
        int idx = name.IndexOf("_e", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
            return name[..idx];
        return name;
    }

    static int ParseEpoch(string fileName, bool isGpt)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        Match m = isGpt ? GptEpochRegex.Match(name) : SovitsEpochRegex.Match(name);
        return m.Success && int.TryParse(m.Groups[1].Value, out int ep) ? ep : 0;
    }

    static string NormalizeKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        return name.Replace("_", "").Replace("-", "").Replace(" ", "")
            .ToLowerInvariant();
    }

    static string ToRelative(string root, string fullPath)
    {
        string rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace('\\', '/');
    }

    static int? DetectSuggestedPort(string root)
    {
        string yaml = Path.Combine(root, DefaultV2ConfigRelative.Replace('/', Path.DirectorySeparatorChar));
        int? fromYaml = TryDetectPortFromTextFile(yaml,
            new Regex(@"(?im)^\s*(?:bind_)?port\s*:\s*(\d{2,5})\s*$", RegexOptions.Compiled));
        if (fromYaml is >= 1 and <= 65535)
            return fromYaml;

        string apiV2 = Path.Combine(root, "api_v2.py");
        int? fromApiV2 = TryDetectPortFromTextFile(apiV2, BuildPortRegexes());
        if (fromApiV2 is >= 1 and <= 65535)
            return fromApiV2;

        string apiV1 = Path.Combine(root, "api.py");
        int? fromApiV1 = TryDetectPortFromTextFile(apiV1, BuildPortRegexes());
        if (fromApiV1 is >= 1 and <= 65535)
            return fromApiV1;

        return null;
    }

    static Regex[] BuildPortRegexes() =>
    [
        // 例如: default=9880 / default="9880" / default='9880'
        new(@"(?im)--port[^\n]*default\s*=\s*[""']?(\d{2,5})[""']?", RegexOptions.Compiled),
        // 例如: help="default: 9880"
        new(@"(?im)default\s*:\s*(\d{2,5})", RegexOptions.Compiled),
    ];

    static int? TryDetectPortFromTextFile(string path, params Regex[] regexes)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            foreach (Regex regex in regexes)
            {
                Match m = regex.Match(text);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int port))
                    return port;
            }
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    sealed class WeightFile
    {
        public required string RelativePath { get; init; }
        public required string DisplayName { get; init; }
        public required string MatchKey { get; init; }
        public int Epoch { get; init; }
        public required string FullPath { get; init; }
    }

    sealed class WavFile
    {
        public required string RelativePath { get; init; }
        public required string FileName { get; init; }
        public required string NormalizedName { get; init; }
    }

    sealed class ListEntry
    {
        public required string AudioPath { get; init; }
        public required string Language { get; init; }
        public required string Text { get; init; }
    }
}
