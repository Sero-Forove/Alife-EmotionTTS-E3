using System.Collections.Generic;

namespace Azuma.EmotionTTS.E5;

/// <summary>
/// E5 配置契约（GPT-SoVITS 引擎 + speak/emotion 标签 + 旁路 LLM 多元 ref 融合）。
/// 主 LLM 只输出 speak + emotion desc；旁路融合 LLM 智能选 ref（音色融合）+ 情绪改写（韵律）。
/// 无逐字 DSP、无 WhisperX 对齐、无学习表——音调/音量/语速全由 GPT-SoVITS 原生 + ref 融合决定。
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

    // === 音色预设（中性兜底 ref，即主 preset）===
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

    // === E1 兼容保留字段（E5 未使用，仅保证引擎文件编译）===
    public string ApiVersion { get; set; } = "v2";
    public string ApiUrl { get; set; } = "";
    public string StartCommand { get; set; } = "";
    public bool KeepAlive { get; set; } = false;
    public bool MergeSpeakContent { get; set; } = false;
    public bool CoalesceSpeakChunks { get; set; } = false;
    public bool EnablePrefetch { get; set; } = false;
    public string PlaybackMode { get; set; } = "File";
    public int SpeakChunkMinLength { get; set; } = 1;
    public int SpeakChunkMaxLength { get; set; } = 50;
    public int V2_StreamingMode { get; set; } = 2;
    public double V2_FragmentInterval { get; set; } = 0.15;
    public int V2_MinChunkLength { get; set; } = 10;
    public string V1_Device { get; set; } = "cuda";
    public bool V1_HalfPrecision { get; set; } = true;
    public int V1_TopK { get; set; } = 15;
    public double V1_TopP { get; set; } = 0.8;
    public double V1_Temperature { get; set; } = 0.8;
    public float V1_Speed { get; set; } = 1.0f;
    public string V1_CutPunc { get; set; } = "";

    /// <summary>启动时若端口已被占用，记录警告（不自动杀外部进程）。</summary>
    public bool WarnOnExternalPort { get; set; } = true;

    // === 情感 ref 库（音色融合的来源：情感→ref_audio/ref_text/ref_lang）===
    /// <summary>情感参考音频库（配置优先，与目录扫描 ref/{情感}_{强度}/ 互补）。旁路融合 LLM 从中选主 ref + 辅助 ref。</summary>
    public List<EmotionRefLibrary.EmotionRef> EmotionRefs { get; set; } = new();

    // === 旁路融合 LLM（插件核心：emotion desc → 智能选 ref 融合 + 情绪改写）===
    /// <summary>旁路融合 LLM API 根地址（OpenAI 兼容；插件自动拼 /chat/completions，如 https://api.deepseek.com）。</summary>
    public string DspLlmUrl { get; set; } = "";

    /// <summary>旁路融合 LLM 模型名。</summary>
    public string DspLlmModel { get; set; } = "";

    /// <summary>旁路融合 LLM API Key（可为空）。</summary>
    public string DspLlmKey { get; set; } = "";

    /// <summary>旁路融合 LLM 思考强度（reasoning_effort）：max/high/medium/low/none/custom。默认 none（关思考，最快）。</summary>
    public string DspThinkingMode { get; set; } = "none";

    /// <summary>旁路融合 LLM 自定义思考强度（DspThinkingMode=custom 时使用，原样作为 reasoning_effort 值下发）。</summary>
    public string DspThinkingCustom { get; set; } = "";

    /// <summary>
    /// 启用旁路情感融合（核心）：合成前一次独立 LLM 调用，根据 emotion desc + 对白
    /// 智能选 1~3 个 ref 做音色融合 + 把对白改写成情绪更饱满的表达（GPT 原生韵律）。
    /// 完全旁路、不污染主对话上下文。默认开——未配置 LLM（地址/模型）时自动降级中性兜底。
    /// </summary>
    public bool EnableFusion { get; set; } = true;

    // === 提示词（可编辑；空字符串 = 用内置默认）===
    /// <summary>主 LLM 完整提示词模板（注入给主 LLM 的 speak/emotion/lang 用法）。支持占位符 {{defaultLang}}、{{emotionSection}}。空 = 用内置默认。</summary>
    public string MainPrompt { get; set; } = "";

    /// <summary>emotion desc 严格写作规范段（主 LLM 的 emotion 用法说明）。空 = 用内置默认。</summary>
    public string EmotionPromptSection { get; set; } = "";

    /// <summary>旁路融合 LLM 的 system prompt 模板（选 ref + 改写规则）。支持占位符 {{refList}}。空 = 用内置默认。</summary>
    public string FusionSystemPrompt { get; set; } = "";

    // === 打断 ===
    /// <summary>打断环节位掩码：1=播放，2=合成。用户新消息打断正在进行的语音时，选择要打断哪些环节。默认 3（播放+合成）。</summary>
    public int InterruptOnUserMessageTargets { get; set; } = 3;

    /// <summary>打断环节位掩码：1=播放，2=合成。新 speak 打断上一句时，选择要打断哪些环节。默认 0（不打断——保持「多 speak 句间停顿感」按序播放）。</summary>
    public int InterruptOnNewSpeakTargets { get; set; } = 0;
}

static class GptSovitsConfigHelper
{
    /// <summary>是否已配置 GPT-SoVITS（有安装路径即视为已配置）。</summary>
    public static bool IsConfigured(EmotionTTSConfig? c) =>
        c != null && !string.IsNullOrWhiteSpace(c.InstallPath);

    // ==== E1 兼容保留方法（E5 固定 v2 非流式，以下恒 false；仅保证旧引擎文件编译）====
    public static bool IsV1ZipMode(EmotionTTSConfig? c) => false;
    public static bool IsV2FastMode(EmotionTTSConfig? c) => c != null && !string.IsNullOrWhiteSpace(c.InstallPath);
    public static bool IsLegacyMode(EmotionTTSConfig c) => false;
    public static bool IsStreamPlayback(EmotionTTSConfig c) => false;
    public static bool ShouldMergeSpeakContent(EmotionTTSConfig? c) => false;
    public static bool ShouldPrefetch(EmotionTTSConfig? c) => false;
}
