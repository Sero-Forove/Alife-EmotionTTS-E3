using System.Collections.Generic;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// E3 配置契约（GPT-SoVITS 引擎 + speak/emotion/ref 标签 + 整段曲线 DSP）。
/// 基于 E1 GPT-SoVITS 配置裁剪：去掉 V1 兼容/流式播放/预合成（E3 统一走
/// 「主LLM切句 → 并行合成 → 拼接整段 → 一次 DSP-LLM 曲线 + 一次对齐 + 整段 DSP → 播放/QQ」）。
/// 保留：GPT-SoVITS 全套参数、情感 ref 库、DSP-LLM 曲线、WhisperX 对齐、QQ 语音。
/// </summary>
public class EmotionTTSConfig
{
    // === 语种（speak lang / QQ 语音切语种）===
    /// <summary>默认目标语种（speak 未指定 lang 时使用；QQ 语音按当前 speak lang 合成）。</summary>
    public string DefaultLang { get; set; } = "zh";

    // === GPT-SoVITS 引擎 ===
    /// <summary>GPT-SoVITS 整合包根目录（含 GPT_SoVITS/、runtime/、pretrained_models/）。</summary>
    public string InstallPath { get; set; } = "";

    /// <summary>GPT-SoVITS api_v2 服务端口。</summary>
    public int Port { get; set; } = 9881;

    /// <summary>是否已完成安装向导（false 时打开 UI 自动进入安装引导）。</summary>
    public bool SetupWizardCompleted { get; set; } = false;

    // === 音色预设（中性兜底 ref，即 E1 主 preset）===
    public string PresetName { get; set; } = "";
    public string GptWeight { get; set; } = "";
    public string SovitsWeight { get; set; } = "";
    public string RefAudio { get; set; } = "";
    public string RefText { get; set; } = "";
    public string RefLanguage { get; set; } = "zh";

    // === api_v2 参数 ===
    public string V2_TtsConfigPath { get; set; } = "GPT_SoVITS/configs/tts_infer.yaml";
    public string V2_TextSplitMethod { get; set; } = "cut5";
    public int V2_TopK { get; set; } = 15;
    public double V2_TopP { get; set; } = 0.8;
    public double V2_Temperature { get; set; } = 0.8;
    public double V2_RepetitionPenalty { get; set; } = 1.35;
    public double V2_SpeedFactor { get; set; } = 1.0;
    public bool V2_ParallelInfer { get; set; } = true;
    public int V2_BatchSize { get; set; } = 4;

    // === E1 兼容保留字段（E3 未使用，仅保证引擎文件编译；后续清理）===
    /// <summary>E1 遗留：v1/v2 模式切换（E3 固定 v2）。</summary>
    public string ApiVersion { get; set; } = "v2";
    /// <summary>E1 遗留：v1 GET 地址（E3 不用）。</summary>
    public string ApiUrl { get; set; } = "";
    /// <summary>E1 遗留：启动命令（E3 用 CommandBuilder 自动生成）。</summary>
    public string StartCommand { get; set; } = "";
    /// <summary>E1 遗留：常驻（E3 由 RuntimeSync 管理）。</summary>
    public bool KeepAlive { get; set; } = false;
    /// <summary>E1 遗留：合并 speak 内容（E3 整段累积）。</summary>
    public bool MergeSpeakContent { get; set; } = false;
    /// <summary>E1 遗留：句级合成（E3 主 LLM 切句）。</summary>
    public bool CoalesceSpeakChunks { get; set; } = false;
    /// <summary>E1 遗留：预合成（E3 并行合成）。</summary>
    public bool EnablePrefetch { get; set; } = false;
    /// <summary>E1 遗留：播放模式 Stream/File（E3 拼接播放）。</summary>
    public string PlaybackMode { get; set; } = "File";
    /// <summary>E1 遗留：句级最小字数（E3 不用）。</summary>
    public int SpeakChunkMinLength { get; set; } = 1;
    /// <summary>E1 遗留：句级最长缓冲（E3 不用）。</summary>
    public int SpeakChunkMaxLength { get; set; } = 50;
    /// <summary>E1 遗留：流式档位（E3 不用）。</summary>
    public int V2_StreamingMode { get; set; } = 2;
    /// <summary>E1 遗留：流式片段间隔（E3 不用）。</summary>
    public double V2_FragmentInterval { get; set; } = 0.15;
    /// <summary>E1 遗留：流式 min_chunk_length（E3 不用）。</summary>
    public int V2_MinChunkLength { get; set; } = 10;
    /// <summary>E1 遗留：v1 设备（E3 不用）。</summary>
    public string V1_Device { get; set; } = "cuda";
    /// <summary>E1 遗留：v1 半精度（E3 不用）。</summary>
    public bool V1_HalfPrecision { get; set; } = true;
    /// <summary>E1 遗留：v1 参数（E3 不用）。</summary>
    public int V1_TopK { get; set; } = 15;
    public double V1_TopP { get; set; } = 0.8;
    public double V1_Temperature { get; set; } = 0.8;
    public float V1_Speed { get; set; } = 1.0f;
    public string V1_CutPunc { get; set; } = "";

    /// <summary>启动时若端口已被占用，记录警告（不自动杀外部进程）。</summary>
    public bool WarnOnExternalPort { get; set; } = true;

    // === 情感 ref 库（GPT-SoVITS 情感的根本来源：按情感换 ref 音频）===
    /// <summary>情感参考音频库（情感→ref_audio/ref_text/ref_lang）。与目录扫描 ref/{情感}_{强度}/ 互补，配置优先。</summary>
    public List<EmotionRefLibrary.EmotionRef> EmotionRefs { get; set; } = new();

    // === DSP-LLM 曲线（E3：整段一次调用，主 LLM 只输出 speak+emotion+ref）===
    /// <summary>DSP-LLM API 地址（OpenAI 兼容 /v1/chat/completions）。</summary>
    public string DspLlmUrl { get; set; } = "";

    /// <summary>DSP-LLM 模型名。</summary>
    public string DspLlmModel { get; set; } = "";

    /// <summary>DSP-LLM API Key（可为空）。</summary>
    public string DspLlmKey { get; set; } = "";

    /// <summary>启用曲线 DSP：拼接整段后一次 DSP-LLM 生成整段 8 维曲线 → 对齐 → 字级 DSP。关闭则纯 GPT 合成拼接。</summary>
    public bool EnableCurveDsp { get; set; } = true;

    // === 字级对齐（WhisperX daemon 优先；失败分摊）===
    /// <summary>字级对齐引擎：Auto（默认，有 Python/模型用 WhisperX，否则分摊）/ WhisperX / Proportional。</summary>
    public string AlignEngine { get; set; } = "Auto";

    /// <summary>对齐用 Python 路径（WhisperX 需要；建议用 WhisperX 已装的 3.11 环境）。留空自动探测（AlignPythonPath → PATH）。</summary>
    public string AlignPythonPath { get; set; } = "";

    /// <summary>是否缓存对齐结果（同文本+同wav，重复说话零成本）。</summary>
    public bool EnableAlignCache { get; set; } = true;

    // === 安全兜底 ===
    /// <summary>字级 DSP 失败时是否回退原音频（默认 true，绝不因 DSP 报废整句）。</summary>
    public bool DspFailSafe { get; set; } = true;
}

static class GptSovitsConfigHelper
{
    /// <summary>是否已配置 GPT-SoVITS（有安装路径即视为已配置）。</summary>
    public static bool IsConfigured(EmotionTTSConfig? c) =>
        c != null && !string.IsNullOrWhiteSpace(c.InstallPath);

    // ==== E1 兼容保留方法（E3 固定 v2 非流式，以下恒 false；仅保证旧引擎文件编译）====
    public static bool IsV1ZipMode(EmotionTTSConfig? c) => false;
    public static bool IsV2FastMode(EmotionTTSConfig? c) => c != null && !string.IsNullOrWhiteSpace(c.InstallPath);
    public static bool IsLegacyMode(EmotionTTSConfig c) => false;
    public static bool IsStreamPlayback(EmotionTTSConfig c) => false;
    public static bool ShouldMergeSpeakContent(EmotionTTSConfig? c) => false;
    public static bool ShouldPrefetch(EmotionTTSConfig? c) => false;
}
