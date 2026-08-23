using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Azuma.EmotionTTS.E3;

[Module("Azuma.EmotionTTS.E3",
    "本地 GPT-SoVITS 情感语音。speak+emotion+ref 标签 + 整段曲线 DSP + WhisperX 对齐。",
    defaultCategory: "Azuma",
    EditorUI = typeof(EmotionTTSModelUI))]
[Description("本地 GPT-SoVITS 情感语音合成：speak 自管 + emotion/ref 情感标签 + 整段曲线 DSP + WhisperX 对齐。")]
public class EmotionTTSSpeechModel(
    ILogger<EmotionTTSSpeechModel> logger,
    XmlFunctionCaller functionService
) :
    InteractiveModule<EmotionTTSSpeechModel>,
    Alife.Function.AIModelUtility.ISpeechModel,
    IConfigurable<EmotionTTSConfig>
{
    public EmotionTTSConfig? Configuration { get; set; }

    XmlHandler? registeredHandler;
    readonly List<XmlFunction> disabledOfficialSpeechFunctions = new();
    readonly CancellationTokenSource lifetimeCts = new();

    readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    /// <summary>E3：GPT-SoVITS 运行时同步（探测端口→对接或自启服务）。</summary>
    GptSovitsRuntimeSync? gptSync;
    /// <summary>E3：整段对白累积缓冲（主 LLM 切句后整段一次合成+DSP）。</summary>
    readonly StringBuilder speakContentBuffer = new();
    /// <summary>E3：情感 ref 库（按 ref 标签的 emotion 属性选参考音频）。</summary>
    readonly EmotionRefLibrary refLibrary = new();
    readonly SemaphoreSlim initGate = new(1, 1);
    string? pendingSpeakLang;
    Task playbackTask = Task.CompletedTask;
    bool IsSpeaking => playbackTask is { IsCompleted: false };

    // ===== 对齐器 + 知识表（统一工作表/备份表）+ 发声记录 + 每句指令槽 =====
    readonly EmotionAligner aligner = new();
    EmotionSynonymStore? synonymStore;
    EmotionVocalRecordStore? vocalStore;
    /// <summary>统一语音知识表（工作+备份；LLM 指令/人评/清理）。</summary>
    EmotionKnowledgeStore? knowledgeStore;
    /// <summary>emotion 指令暂存槽（实例级，EmotionTuneHandler 与 speak 共享；避免 AsyncLocal 跨 await 丢失）。</summary>
    readonly EmotionDirectiveSlot directiveSlot = new();
    /// <summary>注入门控：Hint/Error/Eval 三类系统消息各自节流 + 全局冷却，防止刷屏卡死对话。
    /// 用 static：Alife 可能重建模块实例（每轮对话/热更），实例级门控会随重建重置导致冷却失效。</summary>
    static readonly EmotionPokeGate pokeGate = new();
    /// <summary>最近一次 speak 的文本（发声记录用）。</summary>
    string? lastSpeakText;
    /// <summary>上次知识表清理时间（懒清理最小间隔控制）。</summary>
    long lastKnowledgeCleanupTick;
    /// <summary>最近一次发声的曲线条目 key（SCOREX 语义打分映射目标，主 LLM 不知内部 key）。</summary>
    string? lastCurveKey;
    /// <summary>曲线库维护（维护 LLM）上次执行时间。</summary>
    long lastCurveMaintenanceTick;
    /// <summary>当前是否处于 speak 标签内（拦截子标签 Content 上推：防 speak 念出 qchat/python 等内容）。
    /// 真实宿主的 FlushContentBuffer 会把子标签的 Content 上推给父标签（除非子标签 handler 清空 Content），
    /// 本插件在 speak 打开期间把其他标签的 Content 消费掉（不改变其自身逻辑，只阻止上推）。</summary>
    volatile bool inSpeak;

    /// <summary>UI 访问：当前同义词表（LLM 学习结果）。</summary>
    public EmotionSynonymStore? SynonymStore => synonymStore;

    /// <summary>UI 访问：发声记录 + 音调偏好学习存储。</summary>
    public EmotionVocalRecordStore? VocalStore => vocalStore;

    /// <summary>UI 访问：统一语音知识表（工作+备份）。</summary>
    public EmotionKnowledgeStore? KnowledgeStore => knowledgeStore;

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        // 注册 speak handler（本插件自管 speak）
        // 使用 WithoutDocument + 显式 Prompt：TTS 高频使用，完整注入用法（含 QQ 语音切语种）
        registeredHandler = new XmlHandler(this);
        functionService.RegisterHandlerWithoutDocument(
            registeredHandler,
            DestroyCancellationToken);

        // 拦截子标签 Content 上推（防 speak 念出 qchat/python 等内容）：
        // 包装所有"非本插件"标签的 Content Invoker——先执行原逻辑，再在 speak 打开期间清空 Content。
        // 幂等：已包装的 Invoker 带标记跳过。
        try
        {
            InstallContentInterceptor();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 子标签 Content 拦截器安装失败（speak 内夹其他标签可能被念出）");
        }

        // EmotionTTS：注册 emotion 标签 handler（自闭合，夹在 speak 内、排最前）
        try
        {
            var tuneHandler = new EmotionTuneHandler(logger, directiveSlot).BuildHandler();
            functionService.RegisterHandlerWithoutDocument(tuneHandler, DestroyCancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[EmotionTTS] emotion 指令 handler 注册失败（emotion 功能不可用，其余正常）");
        }

        // AI 可能从 Prompt 注入的 "[EmotionTTSSpeechModel]说明:" 前缀误学类名标签
        // （Alife 隐式功能正是 <handler/> 激活模式）→ 注册 emotionttsspeechmodel 为合法函数，
        // 重定向到插件总览文档。
        try
        {
            var modelHandler = new XmlHandler("EmotionTTSSpeechModel");
            modelHandler.Functions.Add(new XmlFunction
            {
                Name = "emotionttsspeechmodel",
                Mode = FunctionMode.All,
                Invoker = (ctx, _) =>
                {
                    try
                    {
                        Poke("[EmotionTTS] 插件总览：\n" +
                             "- <speak>对白</speak>：说话（lang 可切语种）\n" +
                             "- <speak><emotion desc=\"...\" instruct=\"...\"/>对白</speak>：情感控制（desc 完整描述供曲线，instruct 2~6 字短词供合成）\n" +
                             "- 对白写在 speak 内；emotion 是自闭合指令不念出来\n" +
                             "- 查全部教学（自闭合，不包对白）：<emotionttsspeechmodel/>");
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[EmotionTTS] 总览文档注入失败");
                    }
                    return Task.CompletedTask;
                }
            });
            functionService.RegisterHandlerWithoutDocument(modelHandler, DestroyCancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] emotionttsspeechmodel 重定向 handler 注册失败");
        }

        // EmotionTTS：先初始化对齐器 + 知识表（确保 Prompt 注入时状态已就绪）
        try
        {
            SyncEmotionRuntime();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[EmotionTTS] 运行时初始化失败（回退默认）");
        }

        // 再注入用法 Prompt
        InjectTtsUsagePrompt();

        // 注意：不再用词表检测"评价声音"（僵化、易误刷）。
        // 评价识别完全交给 LLM 按对话上下文自行判断（主 Prompt 已教学 SCOREX 用法）。

        // EmotionTTS：对话结束后懒清理知识表（自动纳管无门槛，这里统一筛分腾空间）
        try
        {
            if (ChatBot != null)
                ChatBot.ChatFinishedAsync += OnChatFinishedForCleanup;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 订阅对话结束失败（知识表清理不可用）");
        }
    }

    /// <summary>把配置同步到对齐器 + 知识表（配置变更时也调用）。</summary>
    void SyncEmotionRuntime()
    {
        var cfg = Configuration;
        if (cfg == null)
            return;

        // 同义词表（LLM 动态维护，持久化到 Storage/Cache）
        if (synonymStore == null)
        {
            string storePath = Path.Combine(AlifePath.StorageFolderPath, "Cache", "EmotionTTS",
                "emotion_synonyms.json");
            synonymStore = new EmotionSynonymStore(storePath);
        }

        // 发声记录 + 音调偏好学习（A/B 反馈，持久化）
        if (vocalStore == null)
        {
            string vocalPath = Path.Combine(AlifePath.StorageFolderPath, "Cache", "EmotionTTS",
                "vocal_feedback.json");
            vocalStore = new EmotionVocalRecordStore(vocalPath);
        }

        // 统一语音知识表（工作表 + 备份表；LLM 指令操作 / 人评 / 清理）
        if (knowledgeStore == null)
        {
            string cacheDir = Path.Combine(AlifePath.StorageFolderPath, "Cache", "EmotionTTS");
            knowledgeStore = new EmotionKnowledgeStore(
                Path.Combine(cacheDir, "knowledge_work.json"),
                Path.Combine(cacheDir, "knowledge_backup.json"));
        }

        // 音色配方解析器注入：查知识表 __timbre_{名}，未命中回退内置预设（EmotionCharLevelFx 内部处理）
        EmotionCharLevelFx.TimbreRecipeResolver = name =>
        {
            try
            {
                return knowledgeStore?.ResolvePreference("__timbre_" + name);
            }
            catch (Exception)
            {
                return null;
            }
        };
        // 档位词数值解析器注入：查知识表 __word_{维度}_{档位}（AI 可改写档位词数值）
        EmotionDirectiveParser.WordPresetResolver = key =>
        {
            try
            {
                return knowledgeStore?.ResolvePreference(key);
            }
            catch (Exception)
            {
                return null;
            }
        };
        // 方法解析器注入：__doc_* 教学文档**直接返回硬编码 DocTutorial**（不占知识表，避免种子污染）；
        // 普通方法查知识表 __method_{名}（维护 LLN/高级用法，休眠兼容）。
        EmotionDirectiveParser.MethodResolver = name =>
        {
            try
            {
                if (name.StartsWith("__doc_", StringComparison.OrdinalIgnoreCase))
                {
                    // 旧三个文档名重定向到当前教学文档
                    return DocTutorial;
                }
                return knowledgeStore?.ResolveMethod("__method_" + name);
            }
            catch (Exception)
            {
                return null;
            }
        };
        // 教学文档查询注入：__doc_* 内容**立即显式 Poke** 推送给 LLM——
        // Poke 排队后触发新一轮对话，LLM 马上看到文档并回复，无需再发消息。
        EmotionDirectiveParser.DocumentRequested = (name, doc) =>
        {
            try
            {
                logger.LogInformation("[EmotionTTS] 教学文档查询命中：{Name}（内容 {Len} 字），显式 Poke 推送", name, doc.Length);
                Poke($"[EmotionTTS] 教学文档（{name}）：\n{doc}\n请用 <speak> 语音回复你对文档的理解。");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[EmotionTTS] 教学文档显式 Poke 失败");
            }
        };
        // 教学文档/配方种子：不写知识表（避免污染工作/备份表）；__doc_* 由 MethodResolver 硬编码返回，
        // __timbre_ 配方由 EmotionCharLevelFx 内置兜底。知识表只存真实学习数据（__curve_/__style_/打分）。

        // Python 路径：插件 WhisperX venv 优先 → AlignPythonPath → PATH（ResolvePython 内部处理）
        aligner.PythonPath = EmotionAlignEnvManager.ResolvePython(cfg) ?? "";

        aligner.Engine = cfg.AlignEngine?.Trim().ToLowerInvariant() switch
        {
            "whisperx" => EmotionAligner.AlignEngine.WhisperX,
            // 默认 Auto：有 Python 环境时自动用 WhisperX（本地模型），否则分摊
            _ => EmotionAligner.AlignEngine.Auto,
        };
        aligner.EnableCache = cfg.EnableAlignCache;

        // E3：重建情感 ref 库（配置条目 + 目录扫描 ref/{情感}_{强度}/）
        try
        {
            refLibrary.Rebuild(cfg.EmotionRefs, cfg.InstallPath);
            logger.LogInformation("[EmotionTTS] 情感 ref 库已重建：{Count} 项", refLibrary.All.Count);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 情感 ref 库重建失败");
        }
    }

    /// <summary>
    /// 内置预设种子：把 4 个音色配方 + 常用档位词写入知识表（已有条目不覆盖，AI 学习结果优先）。
    /// </summary>
    /// <summary>
    /// 完整教学文档（硬编码返回，不占知识表——避免种子污染工作/备份表）。
    /// E3：主 LLM 只教 speak+emotion+ref+QQ；评分=语境判断的情感表达打分（无参数、无方法体系）。
    /// force 覆盖（跟随代码版本）；不含「用户」字样。
    /// </summary>
    static readonly string DocTutorial =
        "=== EmotionTTS 标签用法 ===\n" +
        "**使用要求**：每次说话用 `<speak>` 包对白；整句情感用 `<emotion desc=\"...\"/>`，需要音色级切换才用 `<ref emotion=\"...\">对白</ref>`；仅单字短回应可裸 speak。\n" +
        "- <speak>对白</speak>：说话容器，包住要说出口的话；lang=zh/ja/en 切语种；必须闭合。\n" +
        "- <emotion desc=\"...\"/>：整句情感描述（自闭合，放 speak 内最前面）。desc 用自然语言写清情感/语气/节奏（如\"无奈带点宠溺，慢速轻声\"）——**只作曲线参考，绝不念出**。\n" +
        "- <ref emotion=\"开心\">要用开心情感念出的对白</ref>：**非自闭合**，包住要用该情感念出的对白；emotion 属性选对应参考音频。**ref 标签本身绝不念出，只有包住的对白进合成**。\n" +
        "- **何时用 ref（重要）**：普通句间情感（快慢/轻重/高低等语气起伏）**不要切 ref**——整句一次合成模型自己会演，最流畅；只有需要**音色级变化**（哭腔/颤抖/爆发/沙哑等换嗓子状态）才用 ref 切段。一段话可多个 ref 连续切换。\n" +
        "- 切句由你（主 LLM）用 ref 边界决定，**不依赖标点**——想断在句子中间也可以。\n" +
        "- 标点由合成引擎自然产生停顿；字级表现（重点词/渐变/颤音）由曲线系统自动处理，无需手写参数。\n" +
        "- **可读性（必须）**：先想好 speak 对白，把同一段对白原文（不加标签）直接写在 `<speak>` 前，再原样包进标签——**前后必须一字不差**；其后必须紧跟 `<speak>` 实际说话\n" +
        "- **被评价声音后**：直接重新输出改进的 `<speak>`（调整 emotion desc / ref 切句），不要只用文字解释或承诺\n" +
        "\n=== 完整示例 ===\n" +
        "我跟你说，今天可逗了，楼下那猫又跑我窗台上了。\n" +
        "<speak lang=\"zh\"><emotion desc=\"开心，语速快，音量稍大\"/>我跟你说，今天可逗了，楼下那猫又跑我窗台上了。</speak>\n" +
        "讲解：普通开心语气**不切 ref**，整句一次合成最流畅；emotion desc 描述情感供曲线。\n" +
        "（音色级切换示例：说到哭腔处才切 `<ref emotion=\"委屈\">可你都不理我。</ref>`）\n" +
        "\n=== QQ 语音 ===\n" +
        "先 `<speak lang=\"目标语种\"></speak>` 切语种，再 `<qchat ... voice=\"true\">文本</qchat>` 发语音。\n" +
        "\n=== 评分（评价声音时自主识别，无提醒）===\n" +
        "听到关于声音的反馈后，**结合对话上下文判断**（留意玩笑、反讽、故意说反话等），对该句的**情感表达**打分：\n" +
        "输出 `SCOREX:pref:表达:+分值` 或 `SCOREX:pref:表达:-分值`。单次 ±1~30：轻微不错+2~5/明显好听+8~15/惊艳+20~30；" +
        "轻微别扭-2~5/明显难听-8~15/很糟糕-20~30。累加 100 封顶、-50 移出工作表（备份保留）。\n" +
        "不打参数、不打具体表现细节——只凭语境判断这句表达值不值得加分/扣分。";

    /// <summary>
    /// 向上下文显式注入 TTS / speak / QQ 语音用法（高频能力，尽量写清楚）。
    /// </summary>
    void InjectTtsUsagePrompt()
    {
        string defaultLang = CosyTextUtil.NormalizeLang(Configuration?.DefaultLang, "zh");

        string modeLine = "当前为 **EmotionTTS 情感语音**：本插件自管 `<speak>` 合成与播放（本地 GPT-SoVITS）。角色若开着「语音说话」，请关闭它避免双 speak。";

        string emotionSection = BuildEmotionPromptSection();

        Prompt($$"""
            ## EmotionTTS 语音输出（请积极使用）

            {{modeLine}}
            默认目标语种：`{{defaultLang}}`（未写 `lang` 时使用；可用标签临时切换）

            ### 本地语音 / 桌宠气泡（默认对外说话）
            **说话时可用 `<emotion desc="..."/>` 指定整句情感**（自闭合，放 speak 内最前面），
            **只在需要音色级变化时用 `<ref emotion="...">对白</ref>` 切段换参考音频**：
            ```
            <speak><emotion desc="开心，语速稍快"/>今天心情不错！</speak>
            <speak lang="ja"><emotion desc="平静，慢速"/>こんにちは、元気ですか？</speak>
            <speak lang="zh"><emotion desc="温柔"/>你好呀。</speak>
            <speak lang="en"><emotion desc="平静"/>Hello there.</speak>
            ```
            - `emotion desc`：自然语言完整描述整句情感/语气/节奏（供曲线系统，**绝不念出**）
            - **`ref` 只在音色级切换时用**（哭腔/颤抖/爆发/沙哑等换嗓子状态）：
              `<ref emotion="委屈">可你都不理我。</ref>` 包住要用该情感念出的对白，ref 标签本身绝不念出。
              **普通句间情感（快慢/轻重/高低等语气起伏）不要切 ref**——整句一次合成模型自己会演，最流畅。
            - `lang` 可选：`zh` / `ja` / `en` 等，指定**本段**合成目标语种
            - 不写 `lang` 时使用默认目标语种 `{{defaultLang}}`
            - 同一轮可多次切换语种，每段 speak 独立生效
            - **可读性（必须）**：先想好 `<speak>` 要念的对白，把**同一段对白原文**（不加任何标签）直接写在 `<speak>` **之前**，再原样包进 `<speak><emotion desc="..."/>...</speak>`——
              **前面那行与 speak 对白必须一字不差**（人读看到什么，听就听到什么）。
              如：`你好呀，今天心情不错！` 然后 `<speak><emotion desc="开心"/>你好呀，今天心情不错！</speak>`。
              ⚠️ **前面那行只是给人看的，不是输出本身**——**不输出 `<speak>` 就没有任何声音（错误）**；写了完整话必须紧跟 `<speak>`。
              ⚠️ **收到关于说话方式的指令/评价时：直接输出改进的 `<speak>` 实际说话，不要只用文字确认**。
            - **其他功能标签照常用**：`<qchat>`（QQ 消息）、`<python>`（执行代码）等其它插件标签的使用方式
              与 EmotionTTS 无关、不受影响，正常按各自的文档使用即可。

            ### QQ 语音（重要！必须先切语种再发语音）
            QQ 发语音（如 `<qchat ... voice="true">`）会直接调用本 TTS 合成，**不会**从 QQ 正文里读 `lang`。
            因此发送**非默认语种**的 QQ 语音时，必须：

            1. **先**用空的或任意 `<speak lang="目标语种">...</speak>` 切换到目标语种  
            2. **再**用目标语种文本发送 QQ 语音  

            **正确示例（日语 QQ 语音）：**
            ```
            <speak lang="ja"></speak>
            <qchat type="group" targetId="群号" voice="true">こんにちは、元気ですか？</qchat>
            ```
            或先说一句日语再发 QQ 语音（语种会保持到下次 speak 重置）：
            ```
            <speak lang="ja">ちょっと待ってね。</speak>
            <qchat type="private" targetId="QQ号" voice="true">今から音声で説明するね。</qchat>
            ```

            **错误示例（不要这样）：**
            ```
            <!-- 未先 speak lang，QQ 语音会按默认目标语种合成，日文可能念成中文模型语种 -->
            <qchat voice="true">こんにちは</qchat>
            ```

            ### 小结
            | 场景 | 做法 |
            |------|------|
            | 桌宠/本地说话 | `<speak>` 或 `<speak lang="ja">` 直接说 |
            | QQ 文字 | `<qchat>` 正常发文字 |
            | QQ 语音 + 切语种 | **先** `<speak lang="ja"></speak>` **再** `<qchat voice="true">日文</qchat>` |
            | 回到默认语种 | `<speak lang="{{defaultLang}}"></speak>` 或下一次不带 lang 的 speak |
            {{emotionSection}}
            """);
    }

    /// <summary>
    /// 动态生成 emotion 情感指令的 Prompt 段（精简核心版）：
    /// 系统提示词只放高频核心（emotion 一句话 + 极简示例），完整教学存知识表（__doc_教学）。
    /// </summary>
    string BuildEmotionPromptSection()
    {
        // E2：主 LLM 只输出 speak+emotion，看不到具体参数（曲线由 DSP-LLM 生成）；
        // 学习只做「语境判断的情感表达打分」（SCOREX:pref:表达），不涉及任何参数/方法。

        string emotionUsageHint = "\n**说话时推荐加 `<emotion desc=\"...\"/>` 表达情感（desc 自由描述供曲线）；普通语气不切 ref，音色级变化（哭腔/爆发/颤抖）才用 `<ref emotion=\"...\">对白</ref>`**。";

        return $"""

            ### 语音情感指令（emotion/ref，可选但推荐）
            `<speak>` 包对白；说话时可在 speak 内**最前面**放 `<emotion desc="..."/>`（自闭合）指定整句情感：
            - **`emotion desc` 用自然语言完整描述情感/语气/节奏**（自由描述，不受列表限制，供曲线系统；**绝不念出**）
            - **`ref` 只在音色级切换时用**——`<ref emotion="委屈">可你都不理我。</ref>` 包住要用该情感念出的对白，**ref 标签本身绝不念出**；哭腔/颤抖/爆发/沙哑等换嗓子状态才切 ref，**普通句间情感（快慢/轻重/高低）不要切**（整句一次合成最流畅）
            ```
            <speak><emotion desc="无奈带点宠溺，慢速轻声"/>都这个点了，你还不睡。</speak>
            <speak><emotion desc="开心，语速快，音量稍大"/>今天心情不错！</speak>
            ```
            - 不写 emotion/ref = 中性自然语气
            - **重点词/渐变/颤音等字级表现由系统按曲线自动处理**，你只需把情感描述写清楚
            - **被评价声音后**（平淡/难听/好听等）：**直接重新输出改进的 `<speak>`**（调整 emotion desc / 是否切 ref），**不要只用文字解释或承诺**
            - **听到关于声音的反馈时**：结合对话上下文语境，判断这句的**情感表达**值不值得加分/扣分，输出 `SCOREX:pref:表达:+分值` 或 `-分值`（±1~30）——不打参数、不打细节，只凭语境判断
            {emotionUsageHint}
            """;
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);

        // 禁用官方 speak（排除自身），避免双 speak；即使服务启动失败也不会残留双播。
        try
        {
            DisableOfficialSpeechFunction();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[EmotionTTS] 禁用官方 SpeechService speak 失败");
        }

        // 启动自检：清理本插件启动的孤儿进程（Alife 异常退出/多次重启残留的
        // WhisperX 对齐 daemon / CosyVoice 服务），防止端口被占 + 显存堆积。
        // 仅清理命令行特征明确指向本插件启动的进程，绝不误伤其他（ComfyUI/用户手动服务）。
        CleanupOrphanTtsProcesses();

        // 预热 WhisperX 常驻进程（后台加载模型 ~10-20s，说话时直接复用——
        // 避免"开始说话前等模型加载"；角色启动时提前加载，首次对齐零等待）
        aligner.WarmUpDaemon();

        logger.LogInformation("[EmotionTTS] CosyVoice 情感语音就绪（服务按需启动）");
    }

    /// <summary>
    /// 包装所有"非本插件"标签的 Content Invoker：
    /// 真实宿主的 FlushContentBuffer 会把子标签的 Content 上推给父标签（除非子标签 handler 清空 Content），
    /// 导致 `<speak>对白<qchat>消息</qchat>对白</speak>` 把"消息"念出来。本方法给其他标签的 Content 分支
    /// 包一层：先执行原逻辑（qchat/deepsearch/python 等照常——它们靠 Closing 的 FullContent 取内容），
    /// 再把该 Content **值**登记到 upPushedContents（**不清空**，避免破坏其 Closing 取消息），
    /// speak 的 Content 分支据此跳过（上推内容不念出）。幂等（已包装的跳过）；外部 handler 可能后注册，
    /// 首次 speak Opening 时补包装（见 Speak Opening）。
    /// </summary>
    void InstallContentInterceptor()
    {
        if (functionService?.HandlerTable == null)
            return;
        foreach (XmlHandler handler in functionService.HandlerTable.GetAllHandlers())
        {
            // 跳过本插件自己的 handler（speak/emotion/ref 的 Content 由自己处理）
            string hName = handler.Name ?? "";
            if (hName.StartsWith("EmotionTTS", StringComparison.OrdinalIgnoreCase) ||
                hName.StartsWith("EmotionTune", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (XmlFunction function in handler.Functions)
            {
                if (function.Invoker == null)
                    continue;
                // 跳过 speak 同名函数（如 DeskPet 的气泡 speak）：它是 speak 的兄弟 handler，
                // 读的 Content 是对白本身（ShowBubble），不是子标签上推，不该登记。
                if (function.Name.Equals("speak", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!contentIntercepted.Add(function))
                    continue; // 已包装
                var original = function.Invoker;
                function.Invoker = (ctx, ct) =>
                {
                    // 先执行原逻辑（qchat 发消息等照常，Content 值不动——qchat 等标签靠 Closing 时
                    // FullContent=AboveContent+Content 取消息，清空 Content 会破坏其累积）
                    Task t = original(ctx, ct);
                    // speak 打开期间、Content 模式：把该 Content 值登记为"上推内容"，
                    // speak 的 Content 分支据此跳过（阻止子标签内容被念出）。
                    if (inSpeak && ctx.CallMode == CallMode.Content &&
                        !string.IsNullOrWhiteSpace(ctx.Content))
                    {
                        lock (upPushedContents)
                            upPushedContents.Add(ctx.Content);
                    }
                    return t;
                };
            }
        }
    }

    /// <summary>已包装的 Content 拦截（幂等；外部 handler 后注册时补包装）。</summary>
    readonly HashSet<XmlFunction> contentIntercepted = new();
    /// <summary>speak 打开期间子标签"上推"的 Content 值（speak Content 分支据此跳过，防念出）。
    /// 只记录值不清空——qchat 等标签靠 Closing 的 FullContent(=AboveContent+Content) 取消息，
    /// 清空会破坏其执行。speak 消费后移除。</summary>
    readonly HashSet<string> upPushedContents = new();

    void DisableOfficialSpeechFunction()
    {
        IReadOnlyList<XmlHandler>? handlers =
            functionService.HandlerTable.GetHandlersOfFunction("speak");
        if (handlers == null)
        {
            logger.LogInformation("[EmotionTTS] SpeechService speak 未注册");
            return;
        }

        int disabled = 0;
        foreach (XmlHandler handler in handlers)
        {
            // 保留本插件自己的 speak；只禁用同名的**官方宿主** SpeechService 函数。
            // 区分：官方 speak Order<0（宿主内置，-10）；本插件 speak Order=-12；
            // 其他插件（如 DeskPet 气泡 speak）Order>=0（默认 0）——**必须保留**，
            // 否则语音与气泡不联动（气泡 speak 被误禁）。
            if (ReferenceEquals(handler, registeredHandler))
                continue;

            foreach (XmlFunction function in handler.Functions
                         .Where(f => f.Name.Equals("speak", StringComparison.OrdinalIgnoreCase)))
            {
                // 只禁用官方宿主 speak（Order<0），保留 DeskPet 等第三方插件 speak（Order>=0）
                if (function.Order >= 0)
                    continue;
                functionService.HandlerTable.DisableFunction(function);
                disabledOfficialSpeechFunctions.Add(function);
                disabled++;
            }
        }

        logger.LogInformation("[EmotionTTS] 已禁用官方 speak 函数 {Count} 个，保留本地 speak", disabled);
    }

    void RestoreOfficialSpeechFunctions()
    {
        foreach (XmlFunction function in disabledOfficialSpeechFunctions)
            functionService.HandlerTable.EnableFunction(function);
        disabledOfficialSpeechFunctions.Clear();
    }

    /// <summary>
    /// order -12：早于官方 SpeechService(-10)，先写入 lang。
    /// E3：完整自管 speak —— Opening 清缓冲，Content 累积对白 + ref 段切句，Closing 整段合成 + DSP + 播放。
    /// </summary>
    [XmlFunction(FunctionMode.Content, name: "speak", order: -12)]
    [Description("语音输出。lang 指定目标语种（zh/ja/en）。发 QQ 语音前须先用本标签切换语种，如 <speak lang=\"ja\"></speak> 再 <qchat voice=\"true\">日文</qchat>")]
    public async Task Speak(XmlExecutorContext context,
        [Description("目标语种，如 zh、ja、en；省略则用默认目标语种")] string? lang = null,
        CancellationToken cancellationToken = default)
    {
        string fallback = Configuration?.DefaultLang ?? "zh";

        try
        {
            switch (context.CallMode)
            {
                case CallMode.Opening:
                    inSpeak = true;   // 打开拦截：子标签 Content 不上推（防念出 qchat/python 等内容）
                    // 外部 handler 可能晚于本插件注册：每次 speak 打开补包装一次（幂等）
                    try { InstallContentInterceptor(); } catch { }
                    try
                    {
                        if (IsSpeaking)
                            await playbackTask;
                    }
                    catch (OperationCanceledException) { }

                    ApplySpeakLang(lang, context.Parameters, fallback, resetWhenMissing: true);
                    directiveSlot.Begin();             // 清空指令槽（防上一句残留误用）
                    speakContentBuffer.Clear();        // 清整段累积
                    break;
                case CallMode.Closing:
                    // 兜底：若有进行中的 ref 段未 commit（ref 未闭合），强制落段
                    directiveSlot.CommitRefSegment();
                    // E3：整段一次合成 + DSP + 播放
                    await FlushSpeakBufferAsync(cancellationToken);
                    inSpeak = false;  // 关闭拦截
                    lock (upPushedContents)
                        upPushedContents.Clear(); // 清残留上推登记
                    try
                    {
                        if (IsSpeaking)
                            await playbackTask;
                    }
                    catch (OperationCanceledException) { }
                    break;
                case CallMode.Content:
                {
                    ApplySpeakLang(lang, context.Parameters, fallback, resetWhenMissing: false);

                    string content = context.Content.Trim();

                    // 跳过"子标签上推"的内容：宿主 FlushContentBuffer 会把子标签的 Content
                    // 原样推给 speak（qchat/python 等），若不拦会被念出。子标签 Content 分支
                    // 已把该值登记到 upPushedContents（不清空，保其自身 Closing 取 FullContent）；
                    // 这里命中则跳过且移除。
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        lock (upPushedContents)
                        {
                            if (upPushedContents.Remove(content))
                                break;
                        }
                    }

                    // 剥离 emotion/ref 标签（desc/emotion 属性只作指令，绝不能进合成文本被念出）
                    content = StripControlTags(content);
                    if (string.IsNullOrWhiteSpace(content))
                        break;

                    lastSpeakText = content;

                    // EmotionTTS：捕获 LLM 输出的知识元指令（SYNONYM/ADDPREF/SCOREX 等），
                    // 处理后跳过合成（不念出来）
                    if (TryHandleSynonymDirectives(content))
                        break;

                    // E3：整段累积，</speak> 一次合成。ref 标签切出的段由 ref handler
                    // 已写入 directiveSlot.Segments（含 OrderedParts 保序）；speak 直接 Content
                    // 是裸对白（无 ref），追加为文本块（保序）——ref 段与直接文本都不丢、顺序正确。
                    directiveSlot.AddTextPart(content);
                    speakContentBuffer.Append(content);
                    if (!string.IsNullOrEmpty(context.AboveSeparator))
                        speakContentBuffer.Append(context.AboveSeparator);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    void ApplySpeakLang(string? langParam, IReadOnlyDictionary<string, string> parameters, string fallback,
        bool resetWhenMissing)
    {
        string? lang = !string.IsNullOrWhiteSpace(langParam) ? langParam : null;
        if (lang == null && TryGetLangAttribute(parameters, out string? fromDict))
            lang = fromDict;

        if (!string.IsNullOrWhiteSpace(lang))
        {
            pendingSpeakLang = CosyTextUtil.NormalizeLang(lang, fallback);
            SpeakLangContext.Lang = pendingSpeakLang;
            logger.LogInformation("【EmotionTTS】目标语种 lang={Lang}", pendingSpeakLang);
            return;
        }

        if (resetWhenMissing)
        {
            pendingSpeakLang = null;
            SpeakLangContext.Lang = null;
        }
    }

    static bool TryGetLangAttribute(IReadOnlyDictionary<string, string> parameters, out string? lang)
    {
        if (parameters.TryGetValue("lang", out lang) && !string.IsNullOrWhiteSpace(lang))
            return true;
        if (parameters.TryGetValue("language", out lang) && !string.IsNullOrWhiteSpace(lang))
            return true;
        lang = null;
        return false;
    }

    static readonly System.Text.RegularExpressions.Regex ControlTagRegex = new(
        @"<\s*(emotion|ref)\b[^>]*>|</\s*ref\s*>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>剥离 emotion/ref 标签（desc/emotion 属性只作指令，防止标签文本进合成被念出）。</summary>
    static string StripControlTags(string content)
    {
        if (string.IsNullOrWhiteSpace(content) ||
            (!content.Contains("<emotion", StringComparison.OrdinalIgnoreCase) &&
             !content.Contains("<ref", StringComparison.OrdinalIgnoreCase)))
            return content;
        return ControlTagRegex.Replace(content, "");
    }

    /// <summary>
    /// QQ 语音等外部入口：GPT-SoVITS 合成（**只合成不播放**），整段一次（无 ref 切句 → 中性整段）。
    /// 失败返回 null。
    /// </summary>
    public async Task<string?> GenerateSpeechFileAsync(string text, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource operationCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCts.Token);
        cancellationToken = operationCts.Token;

        text = CosyTextUtil.Sanitize(text).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var config = Configuration;
        if (config == null)
            return null;

        try
        {
            // 整段一个中性段（QQ 无 ref 标签入口，中性 ref 合成）
            return await GenerateSpeechAsync(text, emotionDesc: null,
                segments: new List<EmotionDirectiveSlot.EmotionSegment>
                {
                    new() { Text = text, Emotion = "" },
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] QQ 语音合成失败");
            return null;
        }
    }

    /// <summary>
    /// 对话结束事件：①无条件 flush 知识表脏数据（批量落盘，零开销当无变化）；②懒清理腾空间。
    /// </summary>
    Task OnChatFinishedForCleanup(ChatContext chatContext)
    {
        try
        {
            if (knowledgeStore == null)
                return Task.CompletedTask;
            // 无条件批量落盘（查询计数/自动纳管的脏数据；无变化时零开销）
            knowledgeStore.FlushIfDirty();
            vocalStore?.FlushIfDirty();

            long now = Environment.TickCount64;
            // 10 分钟内不重复清理
            if (now - lastKnowledgeCleanupTick < 10 * 60 * 1000)
                return Task.CompletedTask;
            int total = knowledgeStore.TotalCount();
            if (total <= 200)
                return Task.CompletedTask;
            int removed = knowledgeStore.CleanupWork(200, minScoreToKeep: 1);
            lastKnowledgeCleanupTick = now;
            if (removed > 0)
                logger.LogInformation("[EmotionTTS] 知识表定时清理：移除 {Removed} 条低分方法（备份表保留历史）", removed);

            // 曲线库维护（独立维护 LLM，开思考）：归纳高分曲线为风格模板 + 清理低分/重复。
            // 低频（10min 间隔 + 曲线条目≥10），不阻塞对话结束（fire-and-forget）。
            if (now - lastCurveMaintenanceTick >= 10 * 60 * 1000)
            {
                int curveCount = knowledgeStore.WorkEntries.Count(e =>
                    string.Equals(e.Type, EmotionKnowledgeStore.TypePreference, StringComparison.OrdinalIgnoreCase) &&
                    e.Key.StartsWith("__curve_", StringComparison.Ordinal));
                if (curveCount >= 10)
                {
                    lastCurveMaintenanceTick = now;
                    _ = Task.Run(MaintenanceAsync);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 知识表清理失败");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 曲线库维护（独立维护 LLM，同 API 但**开思考**，max_tokens 放宽）：
    /// 读工作表高分曲线 Top-20 → 归纳经典风格模板（ADDPREF:__style_经典名->曲线）→
    /// 清理低分/重复（DELX:pref:__curve_xxx）→ 复用 HandleDirective 执行。
    /// 工作/备份表关系不变：评分/清理照旧，-50 移出工作表、备份保留。
    /// </summary>
    async Task MaintenanceAsync()
    {
        try
        {
            var config = Configuration;
            if (config == null || knowledgeStore == null)
                return;
            if (string.IsNullOrWhiteSpace(config.DspLlmUrl) || string.IsNullOrWhiteSpace(config.DspLlmModel))
                return;

            // 读工作表高分曲线 Top-20（含分数）
            var curves = knowledgeStore.WorkEntries
                .Where(e => string.Equals(e.Type, EmotionKnowledgeStore.TypePreference, StringComparison.OrdinalIgnoreCase) &&
                            e.Key.StartsWith("__curve_", StringComparison.Ordinal))
                .OrderByDescending(e => e.Score)
                .Take(20)
                .Select(e => $"[{e.Score}分] {e.Key}:\n{e.Value}")
                .ToList();
            if (curves.Count < 5)
                return;

            string? maintenance = await CurveCodec.RequestMaintenanceAsync(httpClient,
                config.DspLlmUrl, config.DspLlmModel, config.DspLlmKey, curves, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(maintenance))
                return;

            // 执行维护指令（ADDPREF 归纳风格 / DELX 清理低分，复用 HandleDirective）
            int executed = 0;
            foreach (string line in maintenance.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string cmd = line.Trim();
                if ((cmd.StartsWith("ADDPREF:", StringComparison.OrdinalIgnoreCase) ||
                     cmd.StartsWith("DELX:", StringComparison.OrdinalIgnoreCase)) &&
                    knowledgeStore.HandleDirective(cmd))
                    executed++;
            }
            knowledgeStore.FlushIfDirty();
            logger.LogInformation("[EmotionTTS] 曲线库维护完成：执行 {Count} 条指令（曲线样本 {Total} 条）",
                executed, curves.Count);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 曲线库维护失败");
        }
    }

    /// <summary>
    /// 隐式注入：往对话历史插入一条 User 角色消息（LLM 能看到，但不作为独立对话轮推送/不进气泡/不刷屏）。
    /// **上下文卫生**：注入前先删除历史中本插件之前的所有隐式注入（按专属前缀精确匹配），
    /// 始终保持 1 条。为什么用 User 角色而非 System：Alife 的 LLM 请求构造只发送 User/Assistant 历史 +
    /// 提示词块，System 历史消息会被丢弃（实测 Input token 不增反减）。
    /// </summary>
    void PokeSilent(string message, string marker = "[EmotionTTS]")
    {
        try
        {
            if (ChatBot == null || string.IsNullOrWhiteSpace(message))
                return;
            ChatBot.EditChatHistory(thread =>
            {
                var history = thread.ChatHistory;
                if (history == null)
                    return;
                // 1) 移除本插件之前的同款注入（User 新 + System 旧，带 marker 前缀；绝不动其他消息）
                var stale = history
                    .Where(m => (m.Role == AuthorRole.User || m.Role == AuthorRole.System) &&
                                !string.IsNullOrEmpty(m.Content) &&
                                m.Content.StartsWith(marker, StringComparison.Ordinal))
                    .ToList();
                foreach (var m in stale)
                    history.Remove(m);
                // 2) 加入当前最新的一条（User 角色：对 LLM 上下文可见，UI 亦可见）
                history.Add(new ChatMessageContent(AuthorRole.User, message));
            }, $"EmotionTTS 隐式注入（{marker}，先清旧后加新，保持上下文最简）");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 隐式注入失败（回退显式 Poke）");
            try { Poke(message); } catch { }
        }
    }

    /// <summary>
    /// WebUI O/X 按钮统一入口：
    /// 1) vocalStore 记录反馈（好/坏）
    /// 2) **隐式注入给 LLM**：描述"刚才那句话被标记为好/坏"，附分数尺度规则，
    ///    由 LLM **结合上下文语境**判断该句情感表达值不值得加减分（输出 SCOREX:pref:表达）。
    ///    主 LLM 看不到任何具体参数——只做语境判断打分。不刷屏对话上下文。
    /// </summary>
    public void ApplyVocalFeedback(long recordId, bool good)
    {
        try
        {
            if (vocalStore == null)
                return;
            var r = vocalStore.FeedbackById(recordId, good ? "good" : "bad");
            if (r == null)
                return;
            logger.LogInformation("[EmotionTTS] O/X 反馈：{Kind} 记录#{Id} [{Emotion}] {Text}",
                good ? "O" : "X", r.Id, r.Emotion, TruncateForLog(r.Text));

            // Eval 类注入：5 分钟一次，避免连续 O/X 刷屏
            if (!pokeGate.TryAllow(EmotionPokeGate.KindEval))
                return;
            // 隐式注入（不刷屏对话流）
            PokeSilent($"[EmotionTTS] 刚才那句话被标记为{(good ? "好" : "坏")}：\"{TruncateForLog(r.Text)}\"（情感 {r.Emotion}）。" +
                 $"请结合对话上下文判断这个评价是否成立（留意有没有玩笑、反讽、反骨成分——比如表面上在夸实际在损，或故意说反话），" +
                 $"然后对该句的**情感表达**打分，按以下分数规则输出 SCOREX 指令：\n" +
                 $"**分数规则（严格遵守）**：单次只能加或扣 1~30 分（SCOREX 的 ±delta 必须在 1~30 之间，不能超过 30）。" +
                 $"好评加正分：轻微不错 +2~5，明显好听 +8~15，特别惊艳 +20~30；差评扣负分：轻微别扭 -2~5，明显难听 -8~15，很糟糕 -20~30。" +
                 $"分数会累加到表达评分上（上限 100 封顶；累计到 -50 会被移出工作表，备份仍保留）。" +
                 $"输出格式：`SCOREX:pref:表达:+分值` 或 `SCOREX:pref:表达:-分值`。\n" +
                 $"（只凭语境判断这句情感表达值不值得打分；不打参数、不打细节。若你判断这个评价不针对声音表现，就不要输出，等下一次。）");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] O/X 反馈失败");
        }
    }

    static string TruncateForLog(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 30 ? s : s[..30] + "…");

    /// <summary>
    /// 评估目标端口占用者：是否本插件拉起的进程（CosyVoice server / WhisperX daemon）。
    /// 返回描述文本（供 UI 展示）；不杀任何进程。
    /// </summary>
    /// <summary>评估目标端口占用者（WebUI 调用；委托 PortProcessUtil，不依赖实例上下文）。</summary>
    public string ProbePortProcess(int port) => PortProcessUtil.ProbePortProcess(port);

    /// <summary>杀掉占用指定端口的进程（WebUI 调用；委托 PortProcessUtil，不依赖实例上下文）。</summary>
    public string KillPortProcess(int port) => PortProcessUtil.KillPortProcess(port);

    /// <summary>
    /// 处理 LLM 输出的语音知识元指令；返回 true 表示已消费（该内容不应合成语音）。
    /// 支持多行指令：
    /// - SYNONYM:词->标准词 / DELSYNONYM:词（同义词表）
    /// - ADDPREF:key->参数组合（写入统一表偏好）
    /// - MERGEX:a,b->新 / DELX:type:key / EXTEND:type:key->追加 / CLRWORK（工作表操作）
    /// - SCOREX:type:key:±delta（LLM 评分）
    /// </summary>
    bool TryHandleSynonymDirectives(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        bool handled = false;
        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Trim();
            if (t.StartsWith("SYNONYM:", StringComparison.OrdinalIgnoreCase))
            {
                string body = t["SYNONYM:".Length..].Trim();
                int arrow = body.IndexOf("->", StringComparison.Ordinal);
                if (arrow > 0)
                {
                    string from = body[..arrow].Trim();
                    string to = body[(arrow + 2)..].Trim();
                    if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
                    {
                        synonymStore?.Upsert(from, to);
                        // 统一表：同义词条目（count 驱动，不人评）；标脏批量落盘（在 speak 链路避免阻塞）
                        knowledgeStore?.AddOrUpdate(EmotionKnowledgeStore.TypeSynonym, from, to,
                            EmotionKnowledgeStore.Level1, "llm", persist: false);
                        logger.LogInformation("[EmotionTTS] LLM 学习同义词：{From} → {To}", from, to);
                        handled = true;
                    }
                }
            }
            else if (t.StartsWith("DELSYNONYM:", StringComparison.OrdinalIgnoreCase))
            {
                string from = t["DELSYNONYM:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(from))
                {
                    synonymStore?.Remove(from);
                    knowledgeStore?.RemoveEntry(EmotionKnowledgeStore.TypeSynonym, from);
                    logger.LogInformation("[EmotionTTS] LLM 清除过时间义词：{From}", from);
                    handled = true;
                }
            }
            else if (t.StartsWith("SCOREX:", StringComparison.OrdinalIgnoreCase))
            {
                // 主 LLM 语义打分 SCOREX:pref:表达:±delta → 映射到最近发声的曲线条目
                // （主 LLM 永远看不到内部 key/曲线内容，只凭语境判断表达好坏）
                string mapped = t;
                if (lastCurveKey != null &&
                    t.Contains(":表达:", StringComparison.OrdinalIgnoreCase))
                    mapped = t.Replace(":表达:", ":" + lastCurveKey + ":", StringComparison.OrdinalIgnoreCase);
                if (knowledgeStore != null && knowledgeStore.HandleDirective(mapped))
                {
                    logger.LogInformation("[EmotionTTS] 曲线评分：{Line}", t);
                    handled = true;
                }
            }
            else if (t.StartsWith("ADDPREF:", StringComparison.OrdinalIgnoreCase) ||
                     t.StartsWith("MERGEX:", StringComparison.OrdinalIgnoreCase) ||
                     t.StartsWith("DELX:", StringComparison.OrdinalIgnoreCase) ||
                     t.StartsWith("EXTEND:", StringComparison.OrdinalIgnoreCase) ||
                     t.StartsWith("CLRWORK", StringComparison.OrdinalIgnoreCase))
            {
                // 统一知识表自然语言操作（维护 LLM / 高级用法）
                if (knowledgeStore != null && knowledgeStore.HandleDirective(t))
                {
                    logger.LogInformation("[EmotionTTS] 统一知识表指令：{Line}", t);
                    handled = true;
                }
            }
        }

        // 内容里混了普通文本时（如"请用愤怒重说：SYNONYM:...")，也消费掉避免念出指令
        if (handled)
            logger.LogInformation("[EmotionTTS] 已消费语音知识元指令（不合成语音）");
        return handled;
    }

    // ==================== E3：GPT-SoVITS 整段管线 ====================

    /// <summary>E3：flush 整段缓冲 → 并行合成 → 拼接 → 整段 DSP → 播放。</summary>
    async Task FlushSpeakBufferAsync(CancellationToken cancellationToken)
    {
        string text = CosyTextUtil.Sanitize(speakContentBuffer.ToString()).Trim();
        speakContentBuffer.Clear();
        if (string.IsNullOrWhiteSpace(text))
            return;
        logger.LogInformation("【EmotionTTS】整段合成，字数={Length}", text.Length);
        await QueueSpeakAsync(text, cancellationToken);
    }

    /// <summary>E3：整段合成 → 曲线 DSP → 播放（整段一次，播完即返回）。</summary>
    async Task QueueSpeakAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            // E3 情感信息：EmotionDesc（整句描述，供 DSP-LLM 曲线）+ OrderedParts
            // （speak 直接文本块 + ref 段**按出现顺序**，供 GPT 并行合成换 ref、都不丢）。
            string? emotionDesc = directiveSlot.EmotionDesc;
            var parts = directiveSlot.OrderedParts;
            directiveSlot.Begin(); // 清槽（下轮 speak 重新累积）
            string? wav = await GenerateSpeechAsync(text, emotionDesc, parts, cancellationToken);

            // 发声记录（情感表达，供 O/X 语境打分与 UI 展示）
            try { vocalStore?.RecordPlain(lastSpeakText, emotionDesc); } catch { }

            if (string.IsNullOrEmpty(wav))
                return;
            var whenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            playbackTask = GptSovitsAudioPlayer.PlayFileAsync(wav, cancellationToken, whenStarted);
            try
            {
                await whenStarted.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 整段播放失败");
        }
    }

    /// <summary>
    /// E3 合成主路径：整段一次成型。
    /// 1) 切句：ref 标签切出的段（文本+情感）→ 每段独立 ref 并行合成（GPT-SoVITS api_v2）；
    ///    无 ref → 整段一个中性段。
    /// 2) 拼接所有段 wav 为整段。
    /// 3) 整段 DSP：一次 DSP-LLM（完整对白+desc → 整段 8 维曲线）∥ 一次对齐（整段）→ 字级 DSP。
    /// 4) 曲线入库（__curve_ 学习样本）+ lastCurveKey（SCOREX 打分映射）。
    /// </summary>
    /// <param name="text">完整 speak 对白（整段）。</param>
    /// <param name="emotionDesc">整句情感描述（只进 DSP-LLM 曲线提示词）。</param>
    /// <param name="segments">ref 标签切出的段（文本+情感 → 独立 ref 并行合成）；空则整段中性。</param>
    async Task<string?> GenerateSpeechAsync(string text, string? emotionDesc,
        IReadOnlyList<EmotionDirectiveSlot.EmotionSegment> segments, CancellationToken cancellationToken)
    {
        var config = Configuration;
        if (config == null)
            return null;

        bool curveEnabled = config.EnableCurveDsp &&
                            !string.IsNullOrWhiteSpace(config.DspLlmUrl) &&
                            !string.IsNullOrWhiteSpace(config.DspLlmModel);

        // ==== 1) 切句 + 并行合成 ====
        // 段列表：优先用 OrderedParts（speak 直接文本块 + ref 段**按出现顺序**，都不丢）；
        // 空（如 QQ 直接调用无 OrderedParts）→ 整段一个中性段。
        var segs = new List<EmotionDirectiveSlot.EmotionSegment>();
        if (segments != null && segments.Count > 0)
        {
            segs.AddRange(segments);
        }
        else
        {
            segs.Add(new EmotionDirectiveSlot.EmotionSegment { Text = text, Emotion = "" });
        }

        // 并行合成各段（每段独立 ref；GPT-SoVITS api_v2 服务端支持并发请求）
        var synthTasks = new List<Task<string?>>();
        foreach (var seg in segs)
        {
            string segText = CosyTextUtil.Sanitize(seg.Text).Trim();
            if (string.IsNullOrWhiteSpace(segText))
                continue;
            string segEmotion = seg.Emotion ?? "";
            synthTasks.Add(Task.Run(async () =>
            {
                try
                {
                    return await SynthesizeSegmentAsync(config, segText, segEmotion, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[EmotionTTS] 段合成失败：{Seg}", TruncateForLog(segText));
                    return null;
                }
            }, cancellationToken));
        }
        if (synthTasks.Count == 0)
            return null;

        string?[] segWavs = await Task.WhenAll(synthTasks);
        var valid = segWavs.Where(w => !string.IsNullOrEmpty(w)).Cast<string>().ToList();
        if (valid.Count == 0)
            return null;
        if (valid.Count != synthTasks.Count)
            logger.LogWarning("[EmotionTTS] 部分段合成失败：{Ok}/{Total}，用成功段拼接", valid.Count, synthTasks.Count);

        // 完整对白（供 DSP-LLM 曲线与对齐）：所有段文本按序拼接——
        // text 只有 speak 直接文本（ref 段被 ref handler 清空未进 speakContentBuffer），
        // 必须用 segs（OrderedParts：直接文本块 + ref 段）重建，否则曲线/对齐缺 ref 段文本。
        string fullText = string.Join("", segs.Select(s => s.Text)).Trim();
        if (string.IsNullOrWhiteSpace(fullText))
            fullText = text;

        // ==== 2) 拼接整段 ====
        string mergedPath = Path.Combine(AlifePath.TempFolderPath, $"etts_merged_{Guid.NewGuid():N}.wav");
        string? merged = valid.Count == 1
            ? valid[0]
            : GptSovitsWavCache.MergeWavFiles(valid, mergedPath);
        if (string.IsNullOrEmpty(merged))
            return valid[0];
        logger.LogInformation("【EmotionTTS】整段拼接完成：{Count} 段 → {Out}", valid.Count, Path.GetFileName(merged));

        // ==== 3) 整段 DSP（一次曲线 + 一次对齐）====
        if (!curveEnabled)
            return merged; // 纯 GPT 合成拼接，无曲线

        try
        {
            // 一次 DSP-LLM：完整对白 + desc → 整段 8 维曲线（不再逐句，token 省 N 倍、情感看全局）
            string? raw = await CurveCodec.RequestCurvesAsync(httpClient,
                config.DspLlmUrl, config.DspLlmModel, config.DspLlmKey,
                fullText, fullText, emotionDesc, null, cancellationToken);

            // 曲线入库（真实学习数据：__curve_ 样本）
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string curveKey = "__curve_" + DateTime.Now.ToString("HHmmssfff") + "_" + Guid.NewGuid().ToString("N")[..6];
                knowledgeStore?.AddOrUpdate(EmotionKnowledgeStore.TypePreference, curveKey, raw.Trim(),
                    EmotionKnowledgeStore.Level1, "llm", persist: false);
                lastCurveKey = curveKey; // 打分映射目标（主 LLM 只输出 SCOREX:pref:表达）
            }

            var spec = CurveCodec.ParseCurves(raw);
            if (spec != null && spec.HasAny)
            {
                string? dsp = ApplyCurveDsp(config, fullText, merged, spec);
                if (!string.IsNullOrEmpty(dsp))
                {
                    // 拼接产物是临时文件时清理
                    if (!string.Equals(dsp, merged, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(merged, valid[0], StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(merged); } catch { }
                    }
                    return dsp;
                }
            }
            return merged;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 整段 DSP 失败（回退拼接原声）");
            return merged;
        }
    }

    /// <summary>合成一个情感段（独立 ref；无情感/未命中回退中性 ref）。</summary>
    async Task<string?> SynthesizeSegmentAsync(EmotionTTSConfig config, string text, string emotion,
        CancellationToken cancellationToken)
    {
        if (gptSync == null)
        {
            gptSync = new GptSovitsRuntimeSync(logger);
        }
        if (!await gptSync.EnsureReadyAsync(httpClient, config, cancellationToken))
        {
            logger.LogWarning("[EmotionTTS] GPT-SoVITS 服务未就绪（未安装或启动失败），静默");
            return null;
        }
        // 首次就绪后同步 V2 权重（幂等：签名未变不重复请求）
        try { await gptSync.EnsureSyncedAsync(config, httpClient, cancellationToken); } catch { }

        // 选 ref：情感命中 → 该情感 ref；否则中性兜底（主 preset）
        GptSovitsPresetConfig preset = ResolvePresetForSegment(config, emotion);

        var overrides = GptSovitsSynthOverrides.Resolve(config, text, false, 1);
        string? wav = await SynthesizeSegmentWavAsync(httpClient, config, preset, text, overrides, cancellationToken);
        return wav;
    }

    /// <summary>按情感解析合成 preset（ref）；未命中回退中性兜底 ref（主 preset）。</summary>
    GptSovitsPresetConfig ResolvePresetForSegment(EmotionTTSConfig config, string emotion)
    {
        if (!string.IsNullOrWhiteSpace(emotion))
        {
            EmotionRefLibrary.EmotionRef? refEntry = refLibrary.Resolve(emotion, "中");
            if (refEntry != null && !string.IsNullOrWhiteSpace(refEntry.RefAudio))
            {
                return new GptSovitsPresetConfig
                {
                    RefAudio = refEntry.RefAudio,
                    RefText = refEntry.RefText ?? "",
                    RefLanguage = string.IsNullOrWhiteSpace(refEntry.RefLanguage) ? "zh" : refEntry.RefLanguage,
                };
            }
        }
        // 中性兜底：主 preset（配置的 RefAudio/RefText/RefLanguage）
        return new GptSovitsPresetConfig
        {
            RefAudio = config.RefAudio ?? "",
            RefText = config.RefText ?? "",
            RefLanguage = string.IsNullOrWhiteSpace(config.RefLanguage) ? "zh" : config.RefLanguage,
        };
    }

    /// <summary>单段 wav 合成（api_v2 非流式，返回 wav 文件路径）。</summary>
    static async Task<string?> SynthesizeSegmentWavAsync(HttpClient http,
        EmotionTTSConfig config, GptSovitsPresetConfig preset, string text,
        GptSovitsSynthOverrides overrides, CancellationToken cancellationToken)
    {
        string outputPath = Path.Combine(AlifePath.TempFolderPath, $"etts_seg_{Guid.NewGuid():N}.wav");
        using var response = await GptSovitsV2TtsClient.RequestTtsAsync(http, config, preset, text,
            config.DefaultLang, streaming: false, overrides,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.CopyToAsync(fs, cancellationToken);
        }
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
    }

    /// <summary>曲线 → 50 占位 → 字中点 → 时间 → 逐字 CharLevelDirective → EmotionCharLevelFx。</summary>
    string? ApplyCurveDsp(EmotionTTSConfig config, string text, string wavPath, CurveCodec.CurveSpec spec)
    {
        try
        {
            // 去标点后的有效字数（占位坐标按有效字计算；标点保留在文本中不占曲线位）
            string plain = CosyTextUtil.Sanitize(text)
                .Replace(" ", "").Replace("\n", "").Replace("\r", "");
            int charCount = plain.Length;
            if (charCount == 0)
                return wavPath;

            // 对齐（WhisperX daemon 优先；失败分摊）→ 字边界
            List<CharBoundary> boundaries = aligner.Align(text, wavPath);
            if (boundaries == null || boundaries.Count == 0)
                return wavPath;

            // 构建逐字控制表：每个有效字取"字中点占位"的曲线值
            var table = new List<CharLevelDirective>(charCount);
            var bd = boundaries.Count >= charCount ? boundaries : null;
            for (int i = 0; i < charCount; i++)
            {
                int midSlot = CurveCodec.CharStart(i) + 5; // 字中点占位
                var cd = new CharLevelDirective
                {
                    Char = plain[i].ToString(),
                    PitchOffset = EmotionDirectiveParser.ClampPitch(CurveCodec.Interpolate(spec.Pitch, midSlot) / 100.0 * 12.0),
                    SpeedFactor = EmotionDirectiveParser.ClampSpeed(1 + CurveCodec.Interpolate(spec.Speed, midSlot) / 100.0 * 1.6),
                    Volume = EmotionDirectiveParser.ClampVolume(1 + CurveCodec.Interpolate(spec.Volume, midSlot) / 100.0 * 1.0),
                    VibratoRate = spec.VibratoRate,
                    VibratoDepth = spec.VibratoDepth,
                };
                // timbre：取字内最近的关键帧音色
                string? timbre = NearestNamed(spec.Timbre, midSlot);
                if (!string.IsNullOrEmpty(timbre))
                    cd.Timbre = timbre;
                // pause：字后停顿（最近关键帧的毫秒值，字间空隙归属前一字）
                int pauseMs = NearestValue(spec.Pause, midSlot);
                if (pauseMs > 0)
                    cd.PauseAfterMs = pauseMs;
                // breath：气口（吸气/喘/叹息，最近关键帧）
                string? breath = NearestNamed(spec.Breath, midSlot);
                if (!string.IsNullOrEmpty(breath))
                    cd.Breath = breath;
                table.Add(cd);
            }

            if (!EmotionCharLevelFx.IsActive(table))
                return wavPath;

            string outputPath = Path.Combine(AlifePath.TempFolderPath,
                $"etts_curve_{Guid.NewGuid():N}.wav");
            string result = EmotionCharLevelFx.Apply(wavPath, table, bd, outputPath);
            logger.LogInformation("[EmotionTTS] 曲线 DSP 完成：{Chars} 字 → {Out}", charCount,
                Path.GetFileName(result));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[EmotionTTS] 曲线 DSP 失败，回退原音频");
            return config.DspFailSafe ? wavPath : null;
        }
    }

    /// <summary>取占位坐标之前最近的命名关键帧（timbre/env/breath）。</summary>
    static string? NearestNamed(List<(int slot, string name)> frames, int slot)
    {
        if (frames.Count == 0)
            return null;
        string? best = null;
        foreach (var (s, n) in frames)
        {
            if (s <= slot)
                best = n;
            else
                break;
        }
        return best;
    }

    /// <summary>取占位坐标之前最近的关键帧数值（pause 毫秒；无则 0）。</summary>
    static int NearestValue(List<(int slot, int value)> frames, int slot)
    {
        if (frames.Count == 0)
            return 0;
        int best = 0;
        foreach (var (s, v) in frames)
        {
            if (s <= slot)
                best = v;
            else
                break;
        }
        return best;
    }

    // ===== 孤儿进程清理（只杀本插件特征进程，绝不误伤）=====

    /// <summary>
    /// 启动自检：清理本插件启动的孤儿进程（Alife 异常退出/多次重启后残留）。
    /// 判定（命令行特征，精确匹配本插件启动的进程）：
    ///   - etts_whisperx_daemon_*.py（WhisperX 对齐常驻）
    ///   - api_v2.py（GPT-SoVITS 服务；仅清理启动早于当前 Alife 实例的残留——
    ///     不碰本实例/同 Alife 其他角色正在共享的服务）
    /// 安全：命令行不含这些特征（如 ComfyUI、用户手动启动的其他 python）一律不动。
    /// 仅清理，不阻塞启动（后台 Task）。
    /// </summary>
    void CleanupOrphanTtsProcesses()
    {
        try
        {
            _ = Task.Run(() =>
            {
                try
                {
                    DateTime selfStart = Process.GetCurrentProcess().StartTime;
                    var orphans = new List<int>();
                    foreach (Process p in Process.GetProcessesByName("python"))
                    {
                        try
                        {
                            string cmd = GetCommandLine(p.Id) ?? "";
                            // ① WhisperX 对齐 daemon：本插件专属特征，绝对安全
                            if (cmd.Contains("etts_whisperx_daemon", StringComparison.OrdinalIgnoreCase))
                            {
                                orphans.Add(p.Id);
                                continue;
                            }
                            // ② GPT-SoVITS api_v2.py：仅清理"启动早于本 Alife 实例"的残留
                            //    （旧实例遗留，stderr 管道已随父进程失效）；本实例/同 Alife
                            //    其他角色正在共享的服务启动晚于本实例 → 不碰。
                            if (cmd.Contains("api_v2.py", StringComparison.OrdinalIgnoreCase))
                            {
                                if (p.StartTime < selfStart)
                                    orphans.Add(p.Id);
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            // 命令行/StartTime 读取失败：保守跳过（不误杀）
                        }
                    }

                    if (orphans.Count == 0)
                        return;
                    logger.LogWarning("【EmotionTTS】启动自检发现 {Count} 个本插件孤儿进程，正在清理：{Pids}",
                        orphans.Count, string.Join(",", orphans));
                    foreach (int pid in orphans)
                        KillProcessTree(pid);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[EmotionTTS] 孤儿进程清理失败");
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 孤儿进程扫描失败");
        }
    }

    /// <summary>
    /// 读进程命令行。优先 wmic（旧系统）；Windows 11 已移除 wmic，回退 PowerShell Get-CimInstance。
    /// 权限不足/读不到时返回 null（调用方按「命令行不可见」处理）。
    /// </summary>
    static string? GetCommandLine(int pid)
    {
        string? viaWmic = RunQuery($"wmic process where ProcessId={pid} get CommandLine /value");
        if (!string.IsNullOrWhiteSpace(viaWmic) &&
            !viaWmic.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) &&
            !viaWmic.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            int eq = viaWmic.IndexOf('=');
            if (eq >= 0)
                return viaWmic[(eq + 1)..].Trim();
        }
        // Win11 无 wmic：PowerShell CIM 兜底
        string? viaPs = RunQuery(
            $"powershell.exe -NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"");
        return string.IsNullOrWhiteSpace(viaPs) ? null : viaPs.Trim();
    }

    /// <summary>执行一个单行文本查询命令（cmd /c）。失败/空输出返回 null。</summary>
    static string? RunQuery(string arguments)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>杀进程树（taskkill 提权兜底直接 Kill）。</summary>
    void KillProcessTree(int pid)
    {
        try
        {
            using var killer = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/F /T /PID {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            killer.Start();
            killer.WaitForExit(15000);
        }
        catch (Exception)
        {
            try
            {
                using Process? p = Process.GetProcessById(pid);
                p.Kill(true);
            }
            catch (Exception) { }
        }
    }

    public override async Task DestroyAsync()
    {
        // 生命周期令牌先行取消：在途合成/播放全部中止，避免销毁卡在播放上
        lifetimeCts.Cancel();

        Task pending = playbackTask;
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException)
        {
            logger.LogWarning("【EmotionTTS】等待角色播放停止超过 15 秒，继续停止");
            _ = pending.ContinueWith(t => _ = t.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "【EmotionTTS】销毁时等待播放任务异常");
        }

        try
        {
            aligner.Dispose();          // 释放 WhisperX daemon 共享计数（最后一个释放才停进程）
            gptSync?.StopSelf();        // 停止本实例自启的 GPT-SoVITS 服务（外部服务永不杀）
            httpClient.Dispose();
            initGate.Dispose();
            lifetimeCts.Dispose();
        }
        finally
        {
            try
            {
                RestoreOfficialSpeechFunctions();
            }
            finally
            {
                await base.DestroyAsync();
            }
        }
    }
}
