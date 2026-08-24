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

namespace Azuma.EmotionTTS.E5;

[Module("Azuma.EmotionTTS.E5",
    "本地 GPT-SoVITS 情感语音。speak + emotion 标签 + 旁路 LLM 多元 ref 融合 + 情绪改写。",
    defaultCategory: "Azuma",
    EditorUI = typeof(EmotionTTSModelUI))]
[Description("本地 GPT-SoVITS 情感语音合成：speak 自管 + emotion 情感标签 + 旁路 LLM 智能 ref 融合与情绪改写。")]
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
    /// <summary>GPT-SoVITS 运行时同步（探测端口→对接或自启服务）。</summary>
    GptSovitsRuntimeSync? gptSync;
    /// <summary>整段对白累积缓冲（主 LLM 切句后整段一次融合+合成）。</summary>
    readonly StringBuilder speakContentBuffer = new();
    /// <summary>情感 ref 库（按情感名选参考音频；旁路融合 LLM 从中选主 ref + 辅助 ref）。</summary>
    readonly EmotionRefLibrary refLibrary = new();
    readonly SemaphoreSlim initGate = new(1, 1);
    string? pendingSpeakLang;
    Task playbackTask = Task.CompletedTask;
    bool IsSpeaking => playbackTask is { IsCompleted: false };
    /// <summary>合成打断源（GPT 合成 + 旁路融合）：Cancel 后未完成合成中断。</summary>
    CancellationTokenSource synthInterruptCts = new();
    /// <summary>播放打断源：Cancel 后正在播放的音频立即停止。</summary>
    CancellationTokenSource playbackInterruptCts = new();

    /// <summary>emotion 指令暂存槽（实例级，EmotionTuneHandler 与 speak 共享；避免 AsyncLocal 跨 await 丢失）。</summary>
    readonly EmotionDirectiveSlot directiveSlot = new();
    /// <summary>当前是否处于 speak 标签内（拦截子标签 Content 上推：防 speak 念出 qchat/python 等内容）。
    /// 真实宿主的 FlushContentBuffer 会把子标签的 Content 上推给父标签（除非子标签 handler 清空 Content），
    /// 本插件在 speak 打开期间把其他标签的 Content 消费掉（不改变其自身逻辑，只阻止上推）。</summary>
    volatile bool inSpeak;

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        // 注册 speak handler（本插件自管 speak）
        registeredHandler = new XmlHandler(this);
        functionService.RegisterHandlerWithoutDocument(
            registeredHandler,
            DestroyCancellationToken);

        // 拦截子标签 Content 上推（防 speak 念出 qchat/python 等内容）
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

        // AI 可能从 Prompt 注入的 "[EmotionTTSSpeechModel]说明:" 前缀误学类名标签 → 重定向到总览文档。
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
                             "- <speak><emotion desc=\"...\"/>对白</speak>：整句情感描述（自然语言，供智能 ref 融合 + 情绪改写）\n" +
                             "- emotion 是自闭合指令，绝不念出来\n" +
                             "- 查全部教学：<emotionttsspeechmodel/>");
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

        // 初始化情感 ref 库（确保 Prompt 注入时状态已就绪）
        try
        {
            SyncEmotionRuntime();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[EmotionTTS] 运行时初始化失败（回退默认）");
        }

        // 注入用法 Prompt
        InjectTtsUsagePrompt();

        // 用户新消息打断正在播放的语音（InterruptOnUserMessage 开启时）
        try
        {
            if (ChatBot != null)
                ChatBot.ChatSent += OnUserMessageSent;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 订阅用户消息打断失败");
        }
    }

    /// <summary>用户发新消息：按位掩码打断（1=播放 2=合成）。</summary>
    void OnUserMessageSent(string message)
    {
        try
        {
            int targets = Configuration?.InterruptOnUserMessageTargets ?? 0;
            if (targets == 0 || !IsSpeaking)
                return;
            logger.LogInformation("[EmotionTTS] 用户新消息打断（环节掩码={Targets}）", targets);
            if ((targets & 2) != 0)
                try { synthInterruptCts.Cancel(); } catch { }
            if ((targets & 1) != 0)
                try { playbackInterruptCts.Cancel(); } catch { }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 打断失败");
        }
    }

    /// <summary>把配置同步到情感 ref 库（配置变更时也调用）。</summary>
    void SyncEmotionRuntime()
    {
        var cfg = Configuration;
        if (cfg == null)
            return;

        // 重建情感 ref 库（配置条目 + 目录扫描 ref/{情感}_{强度}/）
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

    /// <summary>ref 库增删后由 UI 调用：重新注入 Prompt（覆盖式），让 LLM 立即看到最新的标准情感清单。</summary>
    public void RefreshPromptAfterRefRebuild()
    {
        try
        {
            InjectTtsUsagePrompt();
            logger.LogInformation("[EmotionTTS] 已刷新语音用法提示词（ref 情感清单更新）");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 刷新提示词失败");
        }
    }

    /// <summary>向上下文显式注入 TTS / speak / QQ 语音用法。</summary>
    void InjectTtsUsagePrompt()
    {
        string defaultLang = CosyTextUtil.NormalizeLang(Configuration?.DefaultLang, "zh");

        string modeLine = "当前为 **EmotionTTS 情感语音**：本插件自管 `<speak>` 合成与播放（本地 GPT-SoVITS）。角色若开着「语音说话」，请关闭它避免双 speak。";

        string emotionSection = BuildEmotionPromptSection();
        string refInventorySection = BuildRefInventorySection();

        Prompt($$"""
            ## EmotionTTS 语音输出（请积极使用）

            {{modeLine}}
            默认目标语种：`{{defaultLang}}`（未写 `lang` 时使用；可用标签临时切换）

            ### 本地语音 / 桌宠气泡（默认对外说话）
            **说话时可用 `<emotion desc="..."/>` 指定整句情感**（自闭合，放 speak 内最前面）：
            ```
            <speak><emotion desc="开心，语速稍快"/>今天心情不错！</speak>
            <speak lang="ja"><emotion desc="平静，慢速"/>こんにちは、元気ですか？</speak>
            <speak lang="zh"><emotion desc="温柔"/>你好呀。</speak>
            <speak lang="en"><emotion desc="平静"/>Hello there.</speak>
            ```
            - `emotion desc`：整句情感/语气/节奏的**声音维度**描述，写法见下方「emotion desc 严格写作规范」（**绝不念出**）
            - 不写 emotion = 中性自然语气
            {{refInventorySection}}
            - `lang` 可选：`zh` / `ja` / `en` 等，指定**本段**合成目标语种
            - 不写 `lang` 时使用默认目标语种 `{{defaultLang}}`
            - 同一轮可多次切换语种，每段 speak 独立生效
            - ⚠️ **不输出 `<speak>` 就没有任何声音（错误）**——写了完整话必须紧跟 `<speak>`。
              ⚠️ **收到关于说话方式的指令/评价时：直接输出改进的 `<speak>` 实际说话，不要只用文字确认**。
            - **句间停顿感**：想营造句间停顿/留白/节奏感时，**多输出几个独立的 `<speak>...</speak>`**（每句一个），
              插件会按顺序一句句合成播放，句与句之间自然停顿（天然比一段长 speak 更有节奏）；想连续快说才用一个大 speak 包多句。
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

    /// <summary>emotion 情感指令的 Prompt 段（精简：只教 speak + emotion desc）。</summary>
    string BuildEmotionPromptSection()
    {
        return """

            ### 语音情感指令（emotion，可选但推荐）
            `<speak>` 包对白；说话时可在 speak 内**最前面**放 `<emotion desc="..."/>`（自闭合）指定整句情感。
            **emotion desc 严格写作规范**（desc 只描述「怎么念」，绝不描述「念什么」或「神态动作」）：
            - **只写声音维度**：情绪、语气、语速、轻重、节奏、音色状态（哭腔/颤抖/耳语/沙哑/撒娇/疲惫…）。
            - **禁止**写神态、表情、动作、心理活动（如「眼睛看着他」「嘴角上扬」「心里难受」）——那会让情绪指向失准。
            - **情感词优先用 ref 清单里的标准情感名**（见上方清单，如「害羞」「不满」「委屈」），不要自造生僻词。
            - **音量/语速只写感受词、不写数值**：写「轻声」「急促」「拖长」，不写「音量-3」「语速1.2」。
            - **简洁**：3~10 字，几个短语逗号隔开即可，不要写成一段文学描写。
            - desc **绝不念出**，只供智能 ref 融合与语气改写。
            ```
            <speak><emotion desc="无奈带点宠溺，慢速轻声"/>都这个点了，你还不睡。</speak>
            <speak><emotion desc="开心，语速快，声音清亮"/>今天心情不错！</speak>
            <speak><emotion desc="委屈，带哭腔，尾音拖长"/>可你都不理我。</speak>
            ```
            - 不写 emotion = 中性自然语气
            - **被评价声音后**（平淡/难听/好听等）：**直接重新输出改进的 `<speak>`**（调整 emotion desc），**不要只用文字解释或承诺**
            """;
    }

    /// <summary>动态生成「ref 情感清单」提示段，让主 LLM 写 desc 时用词贴近可用情感。</summary>
    string BuildRefInventorySection()
    {
        var emotions = refLibrary.AvailableEmotions();
        string emotionList = emotions.Count > 0
            ? string.Join("、", emotions)
            : "（尚未配置任何情感 ref，先用中性兜底）";

        return $"""
            - **ref 可用情感（标准清单，动态更新）**：{emotionList}
              `emotion desc` 里的情感词**优先贴合上面清单里的情感名**（如「害羞」「不满」），
              这样智能情感融合能更精准地选到对应参考音频。
            """;
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);

        // 禁用官方 speak（排除自身），避免双 speak
        try
        {
            DisableOfficialSpeechFunction();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[EmotionTTS] 禁用官方 SpeechService speak 失败");
        }

        // 启动自检：清理本插件启动的孤儿进程（Alife 异常退出/多次重启残留的 GPT-SoVITS 服务）
        CleanupOrphanTtsProcesses();

        logger.LogInformation("[EmotionTTS] GPT-SoVITS 情感语音就绪（服务按需启动）");
    }

    /// <summary>
    /// 包装所有"非本插件"标签的 Content Invoker：真实宿主的 FlushContentBuffer 会把子标签的 Content
    /// 上推给父标签，导致 `<speak>对白<qchat>消息</qchat>对白</speak>` 把"消息"念出来。本方法给其他标签的
    /// Content 分支包一层：先执行原逻辑，再把该 Content **值**登记到 upPushedContents，speak 据此跳过。
    /// </summary>
    void InstallContentInterceptor()
    {
        if (functionService?.HandlerTable == null)
            return;
        foreach (XmlHandler handler in functionService.HandlerTable.GetAllHandlers())
        {
            string hName = handler.Name ?? "";
            if (hName.StartsWith("EmotionTTS", StringComparison.OrdinalIgnoreCase) ||
                hName.StartsWith("EmotionTune", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (XmlFunction function in handler.Functions)
            {
                if (function.Invoker == null)
                    continue;
                if (function.Name.Equals("speak", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!contentIntercepted.Add(function))
                    continue;
                var original = function.Invoker;
                function.Invoker = (ctx, ct) =>
                {
                    Task t = original(ctx, ct);
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

    readonly HashSet<XmlFunction> contentIntercepted = new();
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

    /// <summary>order -12：早于官方 SpeechService(-10)，先写入 lang。完整自管 speak。</summary>
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
                    inSpeak = true;
                    try { InstallContentInterceptor(); } catch { }
                    try
                    {
                        // 打断：新 speak 打断旧的（按位掩码选择：1=播放 2=合成）
                        if (IsSpeaking)
                        {
                            int targets = Configuration?.InterruptOnNewSpeakTargets ?? 0;
                            if (targets != 0)
                            {
                                logger.LogInformation("[EmotionTTS] 打断上一句（新 speak，环节掩码={Targets}）", targets);
                                if ((targets & 2) != 0)
                                    try { synthInterruptCts.Cancel(); } catch { }
                                if ((targets & 1) != 0)
                                    try { playbackInterruptCts.Cancel(); } catch { }
                            }
                            else
                            {
                                await playbackTask;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }

                    ApplySpeakLang(lang, context.Parameters, fallback, resetWhenMissing: true);
                    directiveSlot.Begin();
                    speakContentBuffer.Clear();
                    break;
                case CallMode.Closing:
                    await FlushSpeakBufferAsync(cancellationToken);
                    inSpeak = false;
                    lock (upPushedContents)
                        upPushedContents.Clear();
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

                    // 跳过"子标签上推"的内容
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        lock (upPushedContents)
                        {
                            if (upPushedContents.Remove(content))
                                break;
                        }
                    }

                    // 剥离 emotion 标签（desc 只作指令，绝不能进合成文本被念出）
                    content = StripControlTags(content);
                    if (string.IsNullOrWhiteSpace(content))
                        break;

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
        @"<\s*(emotion|ref)\b[^>]*>|</\s*(emotion|ref)\s*>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>剥离 emotion/ref 标签（desc 属性只作指令，防止标签文本进合成被念出）。</summary>
    static string StripControlTags(string content)
    {
        if (string.IsNullOrWhiteSpace(content) ||
            (!content.Contains("<emotion", StringComparison.OrdinalIgnoreCase) &&
             !content.Contains("<ref", StringComparison.OrdinalIgnoreCase)))
            return content;
        return ControlTagRegex.Replace(content, "");
    }

    /// <summary>QQ 语音等外部入口：中性整段合成（**只合成不播放**）。失败返回 null。</summary>
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
            var preset = ResolvePresetFromRefs(config, null);
            return await SynthesizeWholeAsync(config, preset, text, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] QQ 语音合成失败");
            return null;
        }
    }

    static string TruncateForLog(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 30 ? s : s[..30] + "…");

    /// <summary>评估目标端口占用者（供 UI 展示）；不杀任何进程。</summary>
    public string ProbePortProcess(int port) => PortProcessUtil.ProbePortProcess(port);

    /// <summary>杀掉占用指定端口的进程（WebUI 调用）。</summary>
    public string KillPortProcess(int port) => PortProcessUtil.KillPortProcess(port);

    // ==================== E5：整段融合管线 ====================

    /// <summary>flush 整段缓冲 → 旁路融合 → 整段合成 → 播放。</summary>
    async Task FlushSpeakBufferAsync(CancellationToken cancellationToken)
    {
        string text = CosyTextUtil.Sanitize(speakContentBuffer.ToString()).Trim();
        speakContentBuffer.Clear();
        if (string.IsNullOrWhiteSpace(text))
            return;
        logger.LogInformation("【EmotionTTS】整段合成，字数={Length}", text.Length);
        await QueueSpeakAsync(text, cancellationToken);
    }

    /// <summary>整段：旁路融合（refs + 改写文本）→ 整段一次合成 → 播放。</summary>
    async Task QueueSpeakAsync(string text, CancellationToken cancellationToken)
    {
        // 本句专属打断源
        var mySynthInterrupt = new CancellationTokenSource();
        var myPlaybackInterrupt = new CancellationTokenSource();
        var oldSynth = Interlocked.Exchange(ref synthInterruptCts, mySynthInterrupt);
        var oldPlayback = Interlocked.Exchange(ref playbackInterruptCts, myPlaybackInterrupt);
        try { oldSynth?.Dispose(); } catch { }
        try { oldPlayback?.Dispose(); } catch { }

        using CancellationTokenSource synthLinked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, mySynthInterrupt.Token);
        using CancellationTokenSource playbackLinked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, myPlaybackInterrupt.Token);
        CancellationToken synthToken = synthLinked.Token;
        CancellationToken playbackToken = playbackLinked.Token;

        try
        {
            string? emotionDesc = directiveSlot.EmotionDesc;
            directiveSlot.Begin();

            var config = Configuration;
            if (config == null)
                return;

            // 旁路融合：emotion desc + 对白 → 智能选 ref（音色）+ 情绪改写文本（韵律），一次调用，不碰主上下文
            string synthText = text;
            GptSovitsPresetConfig preset = ResolvePresetFromRefs(config, null);
            if (config.EnableFusion)
            {
                string? reasoningEffort = EmotionFusionClient.ResolveReasoningEffort(
                    config.DspThinkingMode, config.DspThinkingCustom);
                EmotionFusionClient.FusionResult? fusion = await EmotionFusionClient.RequestAsync(
                    httpClient, config.DspLlmUrl, config.DspLlmModel, config.DspLlmKey,
                    emotionDesc, text, refLibrary.AvailableEmotions(), reasoningEffort, synthToken);
                if (fusion != null)
                {
                    preset = ResolvePresetFromRefs(config, fusion.Refs);
                    if (fusion.HasText)
                    {
                        string rewritten = CosyTextUtil.Sanitize(fusion.Text).Trim();
                        if (!string.IsNullOrWhiteSpace(rewritten))
                        {
                            synthText = rewritten;
                            logger.LogInformation("[EmotionTTS] 情绪改写：{Src} → {Dst}",
                                TruncateForLog(text), TruncateForLog(rewritten));
                        }
                    }
                }
            }

            // 合成被打断 → 不再播放
            if (mySynthInterrupt.IsCancellationRequested)
                return;

            string? wav = await SynthesizeWholeAsync(config, preset, synthText, synthToken);
            if (string.IsNullOrEmpty(wav))
                return;

            var whenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            playbackTask = GptSovitsAudioPlayer.PlayFileAsync(wav, playbackToken, whenStarted);
            try
            {
                await whenStarted.Task.WaitAsync(playbackToken);
            }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException)
        {
            // 被打断：静默退出（不再播放）
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[EmotionTTS] 整段播放失败");
        }
    }

    /// <summary>整段一次合成（确保服务就绪 → api_v2 非流式合成）。无 DSP、无逐字音量、无对齐——全由 GPT 原生 + ref 融合决定。</summary>
    async Task<string?> SynthesizeWholeAsync(EmotionTTSConfig config, GptSovitsPresetConfig preset, string text,
        CancellationToken cancellationToken)
    {
        if (gptSync == null)
            gptSync = new GptSovitsRuntimeSync(logger);
        if (!await gptSync.EnsureReadyAsync(httpClient, config, cancellationToken))
        {
            logger.LogWarning("[EmotionTTS] GPT-SoVITS 服务未就绪（未安装或启动失败），静默");
            return null;
        }
        try { await gptSync.EnsureSyncedAsync(config, httpClient, cancellationToken); } catch { }

        var overrides = GptSovitsSynthOverrides.Resolve(config, text, false, 1);
        return await SynthesizeSegmentWavAsync(httpClient, config, preset, text, overrides, cancellationToken);
    }

    /// <summary>按旁路 LLM 选出的 refs 解析 preset：第一个命中的 ref 作主 ref（ref_audio_path），其余作辅助 ref（aux_ref_audio_paths，音色融合）。</summary>
    GptSovitsPresetConfig ResolvePresetFromRefs(EmotionTTSConfig config, IReadOnlyList<string>? refs)
    {
        var result = new GptSovitsPresetConfig();
        var auxAudios = new List<string>();
        bool primarySet = false;

        if (refs != null)
        {
            foreach (string emo in refs)
            {
                if (string.IsNullOrWhiteSpace(emo))
                    continue;
                EmotionRefLibrary.EmotionRef? refEntry = refLibrary.Resolve(emo, "中");
                if (refEntry == null || string.IsNullOrWhiteSpace(refEntry.RefAudio))
                    continue;
                if (!primarySet)
                {
                    result.RefAudio = refEntry.RefAudio;
                    result.RefText = refEntry.RefText ?? "";
                    result.RefLanguage = string.IsNullOrWhiteSpace(refEntry.RefLanguage) ? "zh" : refEntry.RefLanguage;
                    primarySet = true;
                }
                else if (!auxAudios.Contains(refEntry.RefAudio))
                {
                    auxAudios.Add(refEntry.RefAudio);
                }
            }
        }

        // 中性兜底：主 preset
        if (!primarySet)
        {
            result.RefAudio = config.RefAudio ?? "";
            result.RefText = config.RefText ?? "";
            result.RefLanguage = string.IsNullOrWhiteSpace(config.RefLanguage) ? "zh" : config.RefLanguage;
        }

        result.AuxRefAudios = auxAudios;
        return result;
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

    // ===== 孤儿进程清理（只杀本插件特征进程，绝不误伤）=====

    /// <summary>启动自检：清理本插件启动的孤儿进程（Alife 异常退出后残留的 GPT-SoVITS 服务）。</summary>
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
                            // GPT-SoVITS api_v2.py：仅清理"启动早于本 Alife 实例"的残留
                            if (cmd.Contains("api_v2.py", StringComparison.OrdinalIgnoreCase))
                            {
                                if (p.StartTime < selfStart)
                                    orphans.Add(p.Id);
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            // 命令行/StartTime 读取失败：保守跳过
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

    /// <summary>读进程命令行。优先 wmic；Windows 11 已移除 wmic，回退 PowerShell Get-CimInstance。</summary>
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
        string? viaPs = RunQuery(
            $"powershell.exe -NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"");
        return string.IsNullOrWhiteSpace(viaPs) ? null : viaPs.Trim();
    }

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
            gptSync?.StopSelf();
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
