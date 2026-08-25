using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using AntDesign;

namespace Azuma.EmotionTTS.E5;

public partial class EmotionTTSModelUI : ModuleUIBase<EmotionTTSSpeechModel, EmotionTTSConfig>
{
    // ===== GPT-SoVITS 引擎状态 =====
    string _gptStatus = "";
    bool _gptBusy;
    /// <summary>杀端口进程：目标端口输入。</summary>
    int _killPort = 9881;
    /// <summary>杀端口进程操作反馈。</summary>
    string _killPortMsg = "";
    /// <summary>端口归属评估反馈。</summary>
    string _probePortMsg = "";
    bool _killingPort;

    // ===== 音色预设扫描状态 =====
    bool _scanning;
    string _scanMsg = "";
    List<GptSovitsScannedPreset> _scannedPresets = new();

    // ===== 情感 ref 目录识别状态 =====
    string _refScanMsg = "";

    // ===== 默认提示词预览折叠状态 =====
    bool _showMainPromptRef;
    bool _showEmotionSectionRef;
    bool _showFusionPromptRef;

    // ===== 首次运行向导 =====
    /// <summary>当前向导步骤（0=隐藏，1~5=对应步骤）。</summary>
    int _setupStep = 0;
    /// <summary>向导折叠状态（用户主动收起后不再自动弹出）。</summary>
    bool _setupDismissed;

    static readonly List<(string Value, string Label, bool Enabled)> LangOptions = new()
    {
        ("zh", "中文 zh", true),
        ("ja", "日语 ja", true),
        ("en", "英语 en", true),
        ("ko", "韩语 ko", true),
        ("yue", "粤语 yue", true),
    };

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "Configuration NULL");
            return;
        }

        // 安全捕获 Module（Alife 渲染时可能为 null）
        EmotionTTSSpeechModel? module = Module;

        int i = 0;
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "gs-root");

        InjectStyles(b, ref i);

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "gs-title");
        b.AddContent(i++, "EmotionTTS E5");
        b.CloseElement();

        BuildHero(b, ref i);

        BuildSetupWizard(b, ref i);

        SectionPanel(b, ref i, "GPT-SoVITS 引擎", () =>
        {
            AddInput(b, ref i, "安装目录", Configuration!.InstallPath, v =>
            {
                Configuration.InstallPath = v;
            }, "GPT-SoVITS 整合包根目录（含 GPT_SoVITS/、runtime/、api_v2.py）");
            AddHint(b, ref i, "GPT-SoVITS 整合包根目录（含 GPT_SoVITS/、runtime/、api_v2.py）。填写后下方可预览启动命令。");

            AddNumberInput(b, ref i, "服务端口", Configuration.Port, v =>
            {
                Configuration.Port = v;
            }, 1024, 65535);
            AddHint(b, ref i, "api_v2 服务端口，默认 9881。一般不用改。");

            string startCmd;
            try
            {
                startCmd = GptSovitsCommandBuilder.BuildStartCommand(Configuration);
            }
            catch (Exception ex)
            {
                startCmd = $"（无法生成启动命令：{ex.Message}）";
            }
            AddHint(b, ref i, $"启动命令预览：{startCmd}");

            AddButton(b, ref i, _gptBusy ? "检测中…" : "检测服务状态", "gs-btn", _gptBusy, () =>
            {
                _ = ProbeGptStatusAsync();
            });
            if (!string.IsNullOrEmpty(_gptStatus))
                AddHint(b, ref i, _gptStatus);

            AddHint(b, ref i, "端口进程管理：若端口被残留进程占用（重启 Alife 后服务起不来），可在此杀掉该端口上的进程后重启角色。");
            AddNumberInput(b, ref i, "目标端口", _killPort, v => _killPort = v, 1, 65535);
            AddButton(b, ref i, "评估端口归属", "gs-btn", false, () =>
            {
                int port = _killPort;
                _probePortMsg = "评估中…";
                StateHasChanged();
                Task.Run(() =>
                {
                    string result = PortProcessUtil.ProbePortProcess(port);
                    // Blazor：后台线程不能直接 StateHasChanged，须 InvokeAsync 调度回渲染线程
                    InvokeAsync(() =>
                    {
                        _probePortMsg = result;
                        StateHasChanged();
                    });
                });
            });
            AddHint(b, ref i, "⚠️ 杀进程会直接终止目标端口上的进程。若判定为「外部进程」（非本插件），可能影响其他软件——建议先点「评估端口归属」确认后再杀。");
            AddButton(b, ref i, _killingPort ? "杀进程中…" : "杀掉该端口进程", "gs-btn-sm", _killingPort, () =>
            {
                int port = _killPort;
                _killingPort = true;
                _killPortMsg = "";
                StateHasChanged();
                Task.Run(() =>
                {
                    string result = PortProcessUtil.KillPortProcess(port);
                    InvokeAsync(() =>
                    {
                        _killPortMsg = result;
                        _killingPort = false;
                        StateHasChanged();
                    });
                });
            });
            if (!string.IsNullOrEmpty(_probePortMsg))
                AddHint(b, ref i, _probePortMsg);
            if (!string.IsNullOrEmpty(_killPortMsg))
                AddHint(b, ref i, _killPortMsg);
        });

        SectionPanel(b, ref i, "音色预设", () =>
        {
            AddInput(b, ref i, "预设名", Configuration!.PresetName, v =>
            {
                Configuration.PresetName = v;
            }, "当前音色名（如 阿梓）");
            AddInput(b, ref i, "GPT 权重路径", Configuration.GptWeight, v =>
            {
                Configuration.GptWeight = v;
            }, "GPT_weights/xxx.ckpt（相对安装目录）");
            AddHint(b, ref i, "相对安装目录的路径，如 GPT_weights/xxx.ckpt。");
            AddInput(b, ref i, "SoVITS 权重路径", Configuration.SovitsWeight, v =>
            {
                Configuration.SovitsWeight = v;
            }, "SoVITS_weights/xxx.pth（相对安装目录）");
            AddHint(b, ref i, "相对安装目录的路径，如 SoVITS_weights/xxx.pth。");

            AddLabel(b, ref i, "中性兜底参考音频（无情感 ref 命中时用）");
            AddInput(b, ref i, "参考音频路径", Configuration.RefAudio, v =>
            {
                Configuration.RefAudio = v;
            }, "如 ref/neutral.wav 或绝对路径");
            AddInput(b, ref i, "参考文本（可选）", Configuration.RefText, v =>
            {
                Configuration.RefText = v;
            }, "参考音频里的原话");
            AddLabeledSelect(b, ref i, GptSovitsPresetResolver.NormalizeUiLang(Configuration.RefLanguage), v =>
            {
                Configuration.RefLanguage = GptSovitsPresetResolver.NormalizeLang(v, "zh");
            }, LangOptions);

            AddButton(b, ref i, _scanning ? "扫描中…" : "扫描预设", "gs-scan-btn", _scanning, () =>
            {
                _ = ScanPresetsAsync();
            });
            AddHint(b, ref i, "扫描安装目录中的 GPT/SoVITS 权重与参考音频，识别可用音色。");
            if (!string.IsNullOrEmpty(_scanMsg))
                AddHint(b, ref i, _scanMsg);

            if (_scannedPresets.Count > 0)
            {
                AddLabel(b, ref i, "选择音色（自动回填权重与参考）");
                var presetOptions = _scannedPresets.Select(p => (p.Name, p.Name, true)).ToList();
                AddLabeledSelect(b, ref i, Configuration.PresetName, v =>
                {
                    var hit = _scannedPresets.FirstOrDefault(p => p.Name == v);
                    if (hit != null)
                    {
                        GptSovitsPresetScanner.ApplyToConfig(Configuration!, hit);
                        StateHasChanged();
                    }
                }, presetOptions);
            }

            AddLabel(b, ref i, "默认目标语种（speak 未指定 lang 时用）");
            AddLabeledSelect(b, ref i, GptSovitsPresetResolver.NormalizeUiLang(Configuration.DefaultLang), v =>
            {
                Configuration.DefaultLang = GptSovitsPresetResolver.NormalizeLang(v, "zh");
                StateHasChanged();
            }, LangOptions);
            AddHint(b, ref i, "语种说明：zh / ja / en / yue 开箱即用；ko（韩语）需 GPT-SoVITS 环境额外安装 eunjeon（Mecab 韩语分词库，C 扩展需编译），未安装则韩语合成无声。");
        });

        SectionPanel(b, ref i, "情感参考音频库", () =>
        {
            var configRefs = Configuration!.EmotionRefs ??= new List<EmotionRefLibrary.EmotionRef>();

            AddButton(b, ref i, "一键识别 ref 目录", "gs-btn", false, () =>
            {
                _ = AutoDetectRefsAsync();
            });
            AddHint(b, ref i, "主音色 ref 音频放在 GPT-SoVITS 整合包目录（不是插件文件夹）下的 ref/ 里：" + (string.IsNullOrWhiteSpace(Configuration.InstallPath) ? "（先填上面的「安装目录」）" : Configuration.InstallPath.TrimEnd('\\', '/') + "/ref/{情感}_{强度}/xxx.wav") + "。点按钮扫描回填下方配置，配置优先、已存在的跳过。");
            if (!string.IsNullOrEmpty(_refScanMsg))
                AddHint(b, ref i, _refScanMsg);

            if (configRefs.Count == 0)
            {
                AddHint(b, ref i, "尚未配置情感 ref。点「一键识别 ref 目录」自动扫描，或手动添加（情感名 + 参考音频路径），LLM 写对应情感名即命中。");
            }
            else
            {
                foreach (var r in configRefs.ToList())
                {
                    AddLabel(b, ref i, "── 情感配置 ──");
                    AddInput(b, ref i, "情感名", r.Emotion, v =>
                    {
                        r.Emotion = v;
                        StateHasChanged();
                    }, "如 害羞 / 愤怒 / 中性");
                    AddInput(b, ref i, "强度（弱/中/强）", r.Tier, v =>
                    {
                        r.Tier = v;
                        StateHasChanged();
                    }, "弱 / 中 / 强");
                    AddInput(b, ref i, "参考音频路径", r.RefAudio, v =>
                    {
                        r.RefAudio = v;
                        StateHasChanged();
                    }, "如 ref/害羞/a.wav 或绝对路径");
                    AddInput(b, ref i, "参考文本（可选）", r.RefText, v =>
                    {
                        r.RefText = v;
                        StateHasChanged();
                    }, "音频里说的原话，可选");
                    AddLabeledSelect(b, ref i, GptSovitsPresetResolver.NormalizeUiLang(r.RefLanguage), v =>
                    {
                        r.RefLanguage = GptSovitsPresetResolver.NormalizeLang(v, "zh");
                        StateHasChanged();
                    }, LangOptions);
                    AddButton(b, ref i, "删除此项", "gs-btn-sm", false, () =>
                    {
                        Configuration!.EmotionRefs.Remove(r);
                        StateHasChanged();
                    });
                }
            }
            AddButton(b, ref i, "添加情感 ref", "gs-btn", false, () =>
            {
                Configuration!.EmotionRefs.Add(new EmotionRefLibrary.EmotionRef { Emotion = "正常", Tier = "中" });
                StateHasChanged();
            });
            AddButton(b, ref i, "应用 ref 更改", "gs-scan-btn", false, () =>
            {
                try { Module?.RefreshPromptAfterRefRebuild(); } catch { }
                _refScanMsg = "已应用 ref 更改：运行时 ref 库已重建、提示词已刷新。";
                StateHasChanged();
            });
        });

        SectionPanel(b, ref i, "异音色融合 ref 库存（换质感不换人）", () =>
        {
            var foreignRefs = Configuration!.ForeignRefs ??= new List<EmotionRefLibrary.EmotionRef>();

            AddHint(b, ref i, "异音色 = 非本角色音色（如加藤惠耳语/温柔/慵懒）。旁路 LLM 只在语境确实需要时选 1~N 个与主音色做音色融合，且强制「主音色占比不低于下方设定值」。核心仍是「该语境该用什么音色」，异音色只是补充质感，不是换人。");

            AddButton(b, ref i, "一键识别 foreign 目录", "gs-btn", false, () =>
            {
                _ = AutoDetectForeignRefsAsync();
            });
            AddHint(b, ref i, "异音色 ref 音频放在 GPT-SoVITS 整合包目录（不是插件文件夹）下的 foreign_ref/ 里：" + (string.IsNullOrWhiteSpace(Configuration.InstallPath) ? "（先填上面的「安装目录」）" : Configuration.InstallPath.TrimEnd('\\', '/') + "/foreign_ref/【情感】台词.wav") + "。点按钮扫描回填下方配置，配置优先、同名情感跳过。");
            if (!string.IsNullOrEmpty(_refScanMsg))
                AddHint(b, ref i, _refScanMsg);

            AddLabel(b, ref i, "主音色最小占比（异音色融合配比）");
            AddNumberInputD(b, ref i, "主音色最小占比", Configuration.ForeignMixMinNativeRatio, v =>
            {
                Configuration.ForeignMixMinNativeRatio = v;
            }, 0.05, 0.95, 0.05);
            AddHint(b, ref i, "融合时主音色（本角色音色）ref 数量占比不得低于此值。1/3≈0.33（异音色最多为主音色 2 倍）、1/2=0.5（最多 1 倍）、1/4=0.25（最多 3 倍）。值越大音色越接近本角色，越小异音色占比越高。");

            AddLabel(b, ref i, "主音色 ref 数量范围（旁路 LLM 选几个）");
            AddNumberInput(b, ref i, "最小数量 FusionRefMin", Configuration.FusionRefMin, v =>
            {
                Configuration.FusionRefMin = v;
            }, 1, 10);
            AddNumberInput(b, ref i, "最大数量 FusionRefMax", Configuration.FusionRefMax, v =>
            {
                Configuration.FusionRefMax = v;
            }, 1, 10);
            AddHint(b, ref i, "旁路 LLM 从主音色情感列表里选 ref 的数量范围（默认 1~3）。min 只写进提示词建议 LLM 至少选几个；max 是硬上限，代码会强制裁剪超出的主音色 ref。");

            if (foreignRefs.Count == 0)
            {
                AddHint(b, ref i, "尚未配置异音色 ref。点「添加异音色 ref」手动添加（情感名 + 参考音频路径）。");
            }
            else
            {
                foreach (var r in foreignRefs.ToList())
                {
                    AddLabel(b, ref i, "── 异音色配置 ──");
                    AddInput(b, ref i, "情感名", r.Emotion, v =>
                    {
                        r.Emotion = v;
                        StateHasChanged();
                    }, "如 耳语 / 温柔 / 慵懒");
                    AddInput(b, ref i, "强度（弱/中/强）", r.Tier, v =>
                    {
                        r.Tier = v;
                        StateHasChanged();
                    }, "弱 / 中 / 强");
                    AddInput(b, ref i, "参考音频路径", r.RefAudio, v =>
                    {
                        r.RefAudio = v;
                        StateHasChanged();
                    }, "绝对路径或相对 InstallPath");
                    AddInput(b, ref i, "参考文本（可选）", r.RefText, v =>
                    {
                        r.RefText = v;
                        StateHasChanged();
                    }, "音频里说的原话，可选");
                    AddLabeledSelect(b, ref i, GptSovitsPresetResolver.NormalizeUiLang(r.RefLanguage), v =>
                    {
                        r.RefLanguage = GptSovitsPresetResolver.NormalizeLang(v, "zh");
                        StateHasChanged();
                    }, LangOptions);
                    AddButton(b, ref i, "删除此项", "gs-btn-sm", false, () =>
                    {
                        Configuration!.ForeignRefs.Remove(r);
                        StateHasChanged();
                    });
                }
            }
            AddButton(b, ref i, "添加异音色 ref", "gs-btn", false, () =>
            {
                Configuration!.ForeignRefs.Add(new EmotionRefLibrary.EmotionRef { Emotion = "耳语", Tier = "中" });
                StateHasChanged();
            });
            AddButton(b, ref i, "应用异音色 ref 更改", "gs-scan-btn", false, () =>
            {
                try { Module?.RefreshPromptAfterRefRebuild(); } catch { }
                _refScanMsg = "已应用异音色 ref 更改：运行时异音色库存已重建、提示词已刷新。";
                StateHasChanged();
            });
        });

        SectionPanel(b, ref i, "api_v2 参数", () =>
        {
            AddInput(b, ref i, "推理配置路径", Configuration!.V2_TtsConfigPath, v =>
            {
                Configuration.V2_TtsConfigPath = v;
            }, "GPT_SoVITS/configs/tts_infer.yaml");
            AddLabeledSelect(b, ref i, Configuration.V2_TextSplitMethod, v =>
            {
                Configuration.V2_TextSplitMethod = v;
            }, new List<(string, string, bool)>
            {
                ("cut0", "cut0 不切分", true),
                ("cut1", "cut1 按标点", true),
                ("cut2", "cut2 按 4 字", true),
                ("cut3", "cut3 按 50 字", true),
                ("cut4", "cut4 按中文标点", true),
                ("cut5", "cut5 按英文标点", true),
            });
            AddHint(b, ref i, "文本切分方式，影响长句合成稳定性。");
            AddNumberInput(b, ref i, "TopK", Configuration.V2_TopK, v =>
            {
                Configuration.V2_TopK = v;
            }, 1, 100);
            AddNumberInputD(b, ref i, "TopP", Configuration.V2_TopP, v =>
            {
                Configuration.V2_TopP = v;
            }, 0.1, 1.0, 0.05);
            AddNumberInputD(b, ref i, "Temperature", Configuration.V2_Temperature, v =>
            {
                Configuration.V2_Temperature = v;
            }, 0.1, 2.0, 0.05);
            AddNumberInputD(b, ref i, "RepetitionPenalty", Configuration.V2_RepetitionPenalty, v =>
            {
                Configuration.V2_RepetitionPenalty = v;
            }, 0.5, 2.0, 0.05);
            AddNumberInputD(b, ref i, "SpeedFactor", Configuration.V2_SpeedFactor, v =>
            {
                Configuration.V2_SpeedFactor = v;
            }, 0.5, 2.0, 0.05);
            AddNumberInput(b, ref i, "BatchSize", Configuration.V2_BatchSize, v =>
            {
                Configuration.V2_BatchSize = v;
            }, 1, 16);
            AddSwitch(b, ref i, "并行推理（ParallelInfer）", Configuration.V2_ParallelInfer,
                v => Configuration.V2_ParallelInfer = v);
            AddSwitch(b, ref i, "流式合成（边合成边播放）", Configuration.EnableStreaming,
                v => Configuration.EnableStreaming = v);
            AddHint(b, ref i, "开：边合成边播放，首音延迟低（听感更即时）；关：整段合成完再播放。质量不变。注意：本开关只影响桌面 speak 播放；QQ 语音（qchat voice）始终是整段非流式合成（需先产出完整 wav 文件再发送），不受此开关影响。");
        });

        SectionPanel(b, ref i, "旁路融合 LLM（核心：智能 ref 融合 + 情绪改写）", () =>
        {
            AddSwitch(b, ref i, "启用旁路情感融合", Configuration!.EnableFusion,
                v => Configuration.EnableFusion = v);
            AddHint(b, ref i, "合成前一次独立 LLM 调用：根据 emotion desc + 对白，智能选 1~3 个 ref 做音色融合 + 把对白改写成情绪更饱满的表达（GPT 原生韵律）。完全旁路、不污染主对话上下文。关/未配置则中性兜底。");

            AddInput(b, ref i, "API 地址（OpenAI 兼容）", Configuration.DspLlmUrl, v =>
            {
                Configuration.DspLlmUrl = v;
            }, "填服务根地址，插件自动拼 /chat/completions，如 https://api.deepseek.com 或 http://127.0.0.1:8000/v1");
            AddInput(b, ref i, "模型名", Configuration.DspLlmModel, v =>
            {
                Configuration.DspLlmModel = v;
            }, "如 deepseek-v4-pro / deepseek-v4-flash");
            AddInput(b, ref i, "API Key（可为空）", Configuration.DspLlmKey, v =>
            {
                Configuration.DspLlmKey = v;
            }, "本地服务通常留空");

            // 思考强度（reasoning_effort）：6 档下拉
            AddLabel(b, ref i, "思考强度（reasoning_effort）");
            AddLabeledSelect(b, ref i,
                string.IsNullOrWhiteSpace(Configuration.DspThinkingMode) ? "none" : Configuration.DspThinkingMode,
                v =>
                {
                    Configuration.DspThinkingMode = v;
                    StateHasChanged();
                },
                new List<(string, string, bool)>
                {
                    ("none", "none（关思考，最快）", true),
                    ("low", "low", true),
                    ("medium", "medium", true),
                    ("high", "high", true),
                    ("max", "max（最强）", true),
                    ("custom", "自定义", true),
                });
            if (string.Equals(Configuration.DspThinkingMode, "custom", StringComparison.OrdinalIgnoreCase))
            {
                AddInput(b, ref i, "自定义思考强度值", Configuration.DspThinkingCustom, v =>
                {
                    Configuration.DspThinkingCustom = v;
                }, "原样作为 reasoning_effort 下发，如 max / xhigh 等");
            }
            AddHint(b, ref i, "主 LLM 只输出 <speak><emotion desc=\"...\"/>对白</speak>；融合 LLM 同时返回 refs（音色）与改写文本（韵律）。无逐字 DSP、无浮动。");

            AddLabel(b, ref i, "动态语速因子（speed）范围");
            AddNumberInputD(b, ref i, "下限 SpeedFactorMin", Configuration.SpeedFactorMin, v =>
            {
                Configuration.SpeedFactorMin = v;
            }, 0.1, 2.0, 0.05);
            AddNumberInputD(b, ref i, "上限 SpeedFactorMax", Configuration.SpeedFactorMax, v =>
            {
                Configuration.SpeedFactorMax = v;
            }, 0.1, 2.0, 0.05);
            AddHint(b, ref i, "speed 因子含义：1.0=正常语速；>1 变快（激动/急促/兴奋），<1 变慢（平静/悲伤/拖沓/慵懒）。融合 LLM 按情绪输出的 speed 会被夹到这个范围内，并把这个范围注入提示词告知 LLM。范围越窄语速越统一、越稳，越宽则情绪对比（快慢起伏）越强、越夸张。");
        });

        SectionPanel(b, ref i, "emotion desc 的作用与设计理念", () =>
        {
            AddHint(b, ref i, "emotion desc 不仅影响旁路融合 LLM 的 ref 融合和语气增强，还会改变桌宠的说话方式和日常行为——它让主 LLM 在写每句话时多了一次「场景理解」，从而更拟人。");
            AddHint(b, ref i, "desc 写作规范（已注入主 LLM）：只写声音维度（情绪/语气/语速/轻重/节奏/音色状态），不写神态、动作、心理；情感词优先用 ref 清单标准名；音量语速写感受词不写数值；3~10 字即可。旁路融合 LLM 改写对白时会忽略 desc 里的神态噪音，并在增强情绪的同时保持句子连贯。");
            AddHint(b, ref i, "设计理念（作者原话）：一般的 LLM 上下文只带对话文本，有时还附带上动作描写括号（显得很出戏，而且有扮演感）。但 Alife 的标签执行机制让多维度的上下文理解与分内容输出成为可能，桌宠在 LLM 层面就有了一次基础的、系统级的协同推理。虽然这在本质上仍是「动作括号 + 文本」的形式，但这种模式为 LLM 的场景理解提供了一个新的平台。此插件的显式 emotion desc 会间接影响桌宠的说话方式、记忆形式乃至行为模式，是好是坏见仁见智。此外，本插件还采用旁路融合 LLM 对主 LLM 的整个 speak 内容进行修饰，以不污染上下文的方式，丰富 GPT-SoVITS 的表现能力。");
        });

        SectionPanel(b, ref i, "打断设置（按场景多选打断环节）", () =>
        {
            AddHint(b, ref i, "打断环节可多选：播放（停止正在出声）、合成（中断未完成的 GPT 合成）。勾选后，对应场景触发时中断所选环节。");

            AddLabel(b, ref i, "场景一：用户发新消息");
            AddCheckbox(b, ref i, "打断「播放」",
                (Configuration!.InterruptOnUserMessageTargets & 1) != 0,
                v => Configuration.InterruptOnUserMessageTargets =
                    v ? (Configuration.InterruptOnUserMessageTargets | 1) : (Configuration.InterruptOnUserMessageTargets & ~1));
            AddCheckbox(b, ref i, "打断「合成」",
                (Configuration.InterruptOnUserMessageTargets & 2) != 0,
                v => Configuration.InterruptOnUserMessageTargets =
                    v ? (Configuration.InterruptOnUserMessageTargets | 2) : (Configuration.InterruptOnUserMessageTargets & ~2));
            AddHint(b, ref i, "推荐：播放+合成都勾（用户打断时立即停止，不浪费算力）。");

            AddLabel(b, ref i, "场景二：新的 speak 打断上一句");
            AddCheckbox(b, ref i, "打断「播放」",
                (Configuration.InterruptOnNewSpeakTargets & 1) != 0,
                v => Configuration.InterruptOnNewSpeakTargets =
                    v ? (Configuration.InterruptOnNewSpeakTargets | 1) : (Configuration.InterruptOnNewSpeakTargets & ~1));
            AddCheckbox(b, ref i, "打断「合成」",
                (Configuration.InterruptOnNewSpeakTargets & 2) != 0,
                v => Configuration.InterruptOnNewSpeakTargets =
                    v ? (Configuration.InterruptOnNewSpeakTargets | 2) : (Configuration.InterruptOnNewSpeakTargets & ~2));
            AddHint(b, ref i, "推荐：都不勾（保持「多 speak 句间停顿感」的按序播放）。若希望 AI 连续说话时后句立即顶掉前句，再勾选。");
        });

        SectionPanel(b, ref i, "提示词编辑（空 = 用内置默认；改后重启角色生效）", () =>
        {
            AddHint(b, ref i, "三段提示词都可自定义，留空 = 用内置默认。点下面的「查看默认参考」，能看到默认提示词的**实际效果**（占位符已填成当前值）+ **占位符对照表**。改的时候：只改固定文字，`{{xxx}}` 占位符原样保留，它们会根据你的设置自动替换。");

            var cfg = Configuration!;
            // —— 算动态变量的当前值（用于变量列表展示）——
            string defaultLang = CosyTextUtil.NormalizeLang(cfg.DefaultLang, "zh");

            var nativeEmos = new List<string>();
            if (cfg.EmotionRefs != null)
                foreach (var r in cfg.EmotionRefs)
                    if (!string.IsNullOrWhiteSpace(r?.Emotion)) nativeEmos.Add(r.Emotion);
            var foreignEmos = new List<string>();
            if (cfg.ForeignRefs != null)
                foreach (var r in cfg.ForeignRefs)
                    if (!string.IsNullOrWhiteSpace(r?.Emotion)) foreignEmos.Add(r.Emotion);
            string refList = nativeEmos.Count > 0 ? string.Join("、", nativeEmos) : "（无可用情感）";
            string foreignList = foreignEmos.Count > 0 ? string.Join("、", foreignEmos) : "（无可用异音色）";
            string speedRange = $"{cfg.SpeedFactorMin:0.##}~{cfg.SpeedFactorMax:0.##}";
            string nativeRatioPct = $"{Math.Round(cfg.ForeignMixMinNativeRatio * 100)}%";
            string foreignPerNative = $"{(1.0 - cfg.ForeignMixMinNativeRatio) / cfg.ForeignMixMinNativeRatio:0.##}";

            AddLabel(b, ref i, "主 LLM 提示词（speak/emotion/lang 用法）");
            AddTextArea(b, ref i, cfg.MainPrompt, v => cfg.MainPrompt = v,
                "留空则用默认。占位符 {{defaultLang}} {{emotionSection}}");
            AddButton(b, ref i, "恢复默认（主提示词）", "gs-btn-sm", false, () =>
            {
                cfg.MainPrompt = "";
                StateHasChanged();
            });
            AddPromptRef(b, ref i, "主 LLM 提示词", _showMainPromptRef, () => _showMainPromptRef = !_showMainPromptRef,
                new (string, string, string)[]
                {
                    ("默认语种", "{{defaultLang}}", defaultLang),
                    ("emotion 规范段", "{{emotionSection}}", "下方「emotion 写作规范」段（或你在该框填的内容）"),
                },
                EmotionTTSSpeechModel.DefaultMainPrompt);

            AddLabel(b, ref i, "emotion desc 写作规范段");
            AddTextArea(b, ref i, cfg.EmotionPromptSection, v => cfg.EmotionPromptSection = v,
                "留空则用默认（无占位符，纯文本）");
            AddButton(b, ref i, "恢复默认（emotion 规范）", "gs-btn-sm", false, () =>
            {
                cfg.EmotionPromptSection = "";
                StateHasChanged();
            });
            AddPromptRef(b, ref i, "emotion 写作规范", _showEmotionSectionRef, () => _showEmotionSectionRef = !_showEmotionSectionRef,
                null,
                EmotionTTSSpeechModel.DefaultEmotionSection);

            AddLabel(b, ref i, "旁路融合 LLM system prompt（选 ref + 改写规则）");
            AddTextArea(b, ref i, cfg.FusionSystemPrompt, v => cfg.FusionSystemPrompt = v,
                "留空则用默认。占位符 {{refList}} {{foreignList}} {{speedRange}} {{nativeRatioPct}} {{foreignPerNative}} {{refMin}} {{refMax}}");
            AddButton(b, ref i, "恢复默认（融合 prompt）", "gs-btn-sm", false, () =>
            {
                cfg.FusionSystemPrompt = "";
                StateHasChanged();
            });
            AddPromptRef(b, ref i, "融合 system prompt", _showFusionPromptRef, () => _showFusionPromptRef = !_showFusionPromptRef,
                new (string, string, string)[]
                {
                    ("主音色情感清单", "{{refList}}", refList),
                    ("异音色清单", "{{foreignList}}", foreignList),
                    ("语速范围", "{{speedRange}}", speedRange),
                    ("主音色占比", "{{nativeRatioPct}}", nativeRatioPct),
                    ("异音色倍数", "{{foreignPerNative}}", foreignPerNative),
                    ("ref 数量范围", "{{refMin}}/{{refMax}}", cfg.FusionRefMin + "~" + cfg.FusionRefMax),
                },
                EmotionFusionClient.DefaultFusionSystemPrompt);
        });

        b.CloseElement();
    }

    static string PathFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        int idx = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    static void InjectStyles(RenderTreeBuilder b, ref int seq)
    {
        b.OpenElement(seq++, "style");
        b.AddContent(seq++, """
            .gs-root{
                max-width:720px;padding:20px 24px 28px;
                background:linear-gradient(180deg,#faf8ff 0%,#fff 120px);
                border-radius:14px;border:1px solid #e8e4f3;
                box-shadow:0 4px 24px rgba(114,46,209,0.08);
            }
            .gs-title{font-size:20px;font-weight:700;margin-bottom:8px;color:#262626;}
            .gs-hero{
                position:relative;overflow:hidden;
                padding:16px 18px;margin-bottom:14px;border-radius:12px;color:#fff;
                background:linear-gradient(135deg,#312e81 0%,#4c1d95 42%,#1d4ed8 74%,#6d28d9 100%);
                box-shadow:0 8px 28px rgba(49,46,129,0.2);
            }
            .gs-hero::before{
                content:"";position:absolute;top:0;left:-30%;width:30%;height:100%;
                background:linear-gradient(90deg,transparent,rgba(255,255,255,0.18),transparent);
                animation:gs-shine 4.8s linear infinite;
            }
            @keyframes gs-shine{
                0%{transform:translateX(0);}
                100%{transform:translateX(460%);}
            }
            .gs-hero-title{font-size:15px;font-weight:700;position:relative;z-index:1;}
            .gs-hero-sub{font-size:12px;opacity:0.92;margin-top:4px;line-height:1.6;position:relative;z-index:1;}
            .gs-hero-chain{display:flex;align-items:center;gap:6px;flex-wrap:wrap;margin-top:10px;position:relative;z-index:1;}
            .gs-node{padding:3px 10px;border-radius:999px;font-size:11px;font-weight:600;background:rgba(255,255,255,0.16);border:1px solid rgba(255,255,255,0.26);}
            .gs-arrow{font-size:11px;opacity:0.92;}
            .gs-panel{position:relative;margin-bottom:16px;padding:14px 16px 16px 18px;border:1px solid #ebe6f5;border-radius:10px;background:#fafafa;box-shadow:0 2px 8px rgba(114,46,209,0.05);}
            .gs-panel::before{content:"";position:absolute;left:0;top:0;bottom:0;width:4px;background:#722ed1;border-radius:10px 0 0 10px;}
            .gs-panel-head{display:flex;align-items:center;gap:8px;font-size:14px;font-weight:600;color:#434343;margin-bottom:12px;}
            .gs-panel-dot{width:8px;height:8px;border-radius:50%;background:#722ed1;box-shadow:0 0 0 3px rgba(114,46,209,0.12);}
            .gs-label{font-weight:600;margin:10px 0 4px;font-size:13px;color:#434343;}
            .gs-hint2{font-size:11px;color:#8c8c8c;margin:0 0 10px 2px;line-height:1.5;}
            .gs-field{margin-bottom:2px;}
            .gs-root .ant-input,.gs-root .ant-input-number{width:100%;border-radius:10px !important;border:1px solid #e8e0f5 !important;background:linear-gradient(180deg,#fff 0%,#fdfcff 100%) !important;box-shadow:0 1px 4px rgba(114,46,209,0.06),inset 0 1px 0 rgba(255,255,255,0.8) !important;}
            .gs-root .ant-input:hover,.gs-root .ant-input-number:hover{border-color:#d3adf7 !important;box-shadow:0 2px 8px rgba(114,46,209,0.1) !important;}
            .gs-root .ant-input:focus,.gs-root .ant-input-number-focused{border-color:#9254de !important;box-shadow:0 0 0 3px rgba(146,84,222,0.18),0 2px 8px rgba(114,46,209,0.12) !important;}
            .gs-scan-btn{padding:6px 16px;border:none;border-radius:999px;cursor:pointer;font-size:12px;color:#fff;background:linear-gradient(135deg,#1677ff,#722ed1);box-shadow:0 4px 14px rgba(22,119,255,0.35);transition:transform .18s ease,box-shadow .18s ease;}
            .gs-scan-btn:not(:disabled):hover{transform:translateY(-1px) scale(1.02);box-shadow:0 6px 18px rgba(114,46,209,0.4);}
            .gs-scan-btn:disabled{opacity:0.65;cursor:wait;}
            .gs-btn{padding:6px 14px;border:1px solid #d3adf7;border-radius:999px;background:#fff;color:#531dab;cursor:pointer;font-size:12px;font-weight:600;font-family:inherit;}
            .gs-btn:hover{background:#f9f0ff;}
            .gs-btn:disabled{opacity:0.5;cursor:not-allowed;}
            .gs-btn-sm{padding:4px 12px;border-radius:999px;border:1px solid #d3adf7;background:#fff;color:#531dab;cursor:pointer;font-size:11px;font-weight:600;font-family:inherit;}
            .gs-btn-sm:hover{background:#f9f0ff;}
            """);
        b.CloseElement();
    }

    static void BuildHero(RenderTreeBuilder b, ref int i)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "gs-hero");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "gs-hero-title");
        b.AddContent(i++, "GPT-SoVITS 情感语音");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "gs-hero-sub");
        b.AddContent(i++, "GPT-SoVITS api_v2 合成 + speak/emotion 标签 + 旁路 LLM 智能 ref 融合与情绪改写。AI 用 <speak><emotion desc=\"...\"/>对白</speak> 说话，音色由多元 ref 融合、语气由 GPT 原生韵律决定。");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "gs-hero-chain");
        HeroNode(b, ref i, "配置 GPT-SoVITS");
        HeroArrow(b, ref i);
        HeroNode(b, ref i, "预设 / ref 库");
        HeroArrow(b, ref i);
        HeroNode(b, ref i, "开说");
        b.CloseElement();
        b.CloseElement();
    }

    static void HeroNode(RenderTreeBuilder b, ref int i, string text)
    {
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "gs-node");
        b.AddContent(i++, text);
        b.CloseElement();
    }

    static void HeroArrow(RenderTreeBuilder b, ref int i)
    {
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "gs-arrow");
        b.AddContent(i++, "→");
        b.CloseElement();
    }

    /// <summary>
    /// 首次运行向导：检测配置就绪状态，未就绪时显示分步教学（装引擎 → 填路径 → ref 库 → 融合 LLM → 开说）。
    /// 用户主动收起（_setupDismissed）后不再自动弹出；点「重开向导」可再次显示。
    /// </summary>
    void BuildSetupWizard(RenderTreeBuilder b, ref int i)
    {
        var cfg = Configuration;
        if (cfg == null)
            return;

        // 就绪状态检测
        bool installOk = !string.IsNullOrWhiteSpace(cfg.InstallPath) && Directory.Exists(cfg.InstallPath);
        bool engineFilesOk = installOk && File.Exists(Path.Combine(cfg.InstallPath, "api_v2.py"));
        bool presetOk = !string.IsNullOrWhiteSpace(cfg.PresetName) &&
                        !string.IsNullOrWhiteSpace(cfg.GptWeight) && !string.IsNullOrWhiteSpace(cfg.SovitsWeight);
        bool refOk = cfg.EmotionRefs != null && cfg.EmotionRefs.Count > 0;
        bool dspOk = !string.IsNullOrWhiteSpace(cfg.DspLlmUrl) && !string.IsNullOrWhiteSpace(cfg.DspLlmModel);
        bool ready = engineFilesOk && presetOk && refOk;

        // 已就绪且用户未手动收起 → 隐藏向导
        if (ready && !_setupDismissed)
            return;
        // 已就绪但用户点过「重开向导」→ 显示供复查
        // 未就绪且用户主动收起 → 隐藏（但仍可通过按钮重开）

        int step = _setupStep;
        // 自动定位到第一个未完成的步骤（仅未就绪时）
        if (!ready)
        {
            if (!installOk) step = 1;
            else if (!engineFilesOk) step = 1;
            else if (!presetOk) step = 2;
            else if (!refOk) step = 3;
            else if (!dspOk) step = 4;
            else step = 5;
        }
        else if (step == 0)
        {
            step = 5; // 已就绪复查默认显示第 5 步
        }
        _setupStep = step;

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "gs-panel");
        b.AddAttribute(i++, "style", "border-color:#f0d9ff;background:linear-gradient(180deg,#fdf6ff 0%,#fafafa 100%);");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "gs-panel-head");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "gs-panel-dot");
        b.CloseElement();
        b.AddContent(i++, ready ? "✅ 配置检查（全部就绪）" : "🚀 首次运行向导（按步骤配置）");
        b.CloseElement();

        // 步骤导航（横向小按钮）
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;gap:6px;flex-wrap:wrap;margin-bottom:10px;");
        for (int s = 1; s <= 5; s++)
        {
            string label = s switch
            {
                1 => "① 引擎",
                2 => "② 音色",
                3 => "③ ref 库",
                4 => "④ 融合 LLM",
                _ => "⑤ 开说",
            };
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "gs-btn-sm");
            b.AddAttribute(i++, "style", s == step ? "background:#722ed1;color:#fff;border-color:#722ed1;" : "");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () =>
            {
                _setupStep = s;
                StateHasChanged();
            }));
            b.AddContent(i++, label);
            b.CloseElement();
        }
        b.CloseElement();

        switch (step)
        {
            case 1:
                AddWizardStep(b, ref i, "① 准备 GPT-SoVITS 整合包（必须）",
                    "本插件自带启动与合成逻辑，但**引擎本体需要你准备**。请下载 GPT-SoVITS 整合包（含 cu128 PyTorch 的版本），"
                    + "解压后应看到 `api_v2.py`、`GPT_SoVITS/`、`runtime/`、`GPT_weights/`、`SoVITS_weights/` 等目录。"
                    + "\n\n然后在下方的「GPT-SoVITS 引擎 → 安装目录」填入整合包根目录（如 D:\\GPT-SoVITS）。"
                    + "填完点「检测服务状态」，插件会自动启动 api_v2 服务并验证。");
                break;
            case 2:
                AddWizardStep(b, ref i, "② 选择音色预设（可选但推荐）",
                    "插件用 GPT-SoVITS 的 `GPT_weights`（GPT 模型）与 `SoVITS_weights`（SoVITS 模型）合成音色。"
                    + "在「GPT-SoVITS 引擎」面板的预设名/权重路径处选择你训练好的模型（如 Sakura）。"
                    + "\n\n如果还没训练模型，可先留默认，用整合包自带示例权重也能出声。");
                break;
            case 3:
                AddWizardStep(b, ref i, "③ 配置情感 ref 库（情感音色来源）",
                    "`<emotion desc=\"...\"/>` 的 desc 情感词靠**参考音频**融合音色。ref 库来自两处："
                    + "\n1. 「情感 ref 库」面板手填（情感名 → wav/参考文本/语种）"
                    + "\n2. 自动扫描引擎目录 `ref/情感名/*.wav`"
                    + "\n\n至少保留一个「中性」ref（否则无 ref 时回退到主预设）。5 个情感即可覆盖常用情绪。");
                break;
            case 4:
                AddWizardStep(b, ref i, "④ 配置旁路融合 LLM（推荐开）",
                    "这是插件的核心：合成前一次独立 LLM 调用，根据 `emotion desc` + 对白智能选 1~3 个 ref 做音色融合 + 把对白改写成情绪更饱满的表达（GPT 原生韵律）。"
                    + "\n\n在「旁路融合 LLM」面板填入："
                    + "\n- API 地址：OpenAI 兼容服务**根地址**（插件自动拼 /chat/completions），如 https://api.deepseek.com"
                    + "\n- 模型名：如 deepseek-v4-pro / deepseek-v4-flash"
                    + "\n- API Key：你的密钥（本地服务可留空）"
                    + "\n\n不开也可用（中性 ref + 原文合成），开关见同面板「启用旁路情感融合」。");
                break;
            default:
                AddWizardStep(b, ref i, "⑤ 开说（完成）",
                    "配置完成后：重启角色（或等插件热重载），然后让 AI 用标签说话："
                    + "\n```"
                    + "\n<speak><emotion desc=\"开心，语速快\"/>今天心情不错！</speak>"
                    + "\n<speak><emotion desc=\"委屈\"/>可你都不理我。</speak>"
                    + "\n```"
                    + "\n- `emotion desc`：整句情感描述（自然语言，供智能 ref 融合 + 情绪改写，不念出）"
                    + "\n- 不写 emotion = 中性自然语气"
                    + "\n- 想营造句间停顿：多输出几个独立 `<speak>`"
                    + "\n\n有问题回到上一步复查，或点下方「检测服务状态」「评估端口归属」排障。");
                break;
        }

        // 底部操作行
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;gap:8px;margin-top:12px;flex-wrap:wrap;");
        AddButton(b, ref i, "收起向导", "gs-btn-sm", false, () =>
        {
            _setupDismissed = true;
            _setupStep = 0;
            StateHasChanged();
        });
        AddButton(b, ref i, "重开向导", "gs-btn-sm", false, () =>
        {
            _setupDismissed = false;
            _setupStep = 0;
            StateHasChanged();
        });
        b.CloseElement();

        b.CloseElement();
    }

    /// <summary>向导步骤卡片：图标 + 标题 + 说明文字（保留换行）。</summary>
    void AddWizardStep(RenderTreeBuilder b, ref int i, string title, string text)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "padding:10px 14px;border:1px solid #f0d9ff;border-radius:8px;background:#fff;margin-bottom:4px;");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "font-size:13px;font-weight:700;color:#531dab;margin-bottom:6px;");
        b.AddContent(i++, title);
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "font-size:12px;color:#595959;line-height:1.7;white-space:pre-line;");
        b.AddContent(i++, text);
        b.CloseElement();
        b.CloseElement();
    }

    void SectionPanel(RenderTreeBuilder b, ref int seq, string title, Action render)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-panel");
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-panel-head");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "gs-panel-dot");
        b.CloseElement();
        b.AddContent(seq++, title);
        b.CloseElement();
        render();
        b.CloseElement();
    }

    void AddLabel(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-label");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void AddHint(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-hint2");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void AddInput(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> setter,
        string placeholder = "请输入")
    {
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-field");
        b.OpenComponent<Input<string>>(seq++);
        b.AddAttribute(seq++, "Value", value);
        b.AddAttribute(seq++, "Placeholder", placeholder);
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<string>(this, setter));
        b.CloseComponent();
        b.CloseElement();
    }

    void AddTextArea(RenderTreeBuilder b, ref int seq, string value, Action<string> setter,
        string placeholder = "")
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-field");
        b.OpenElement(seq++, "textarea");
        b.AddAttribute(seq++, "value", value);
        b.AddAttribute(seq++, "placeholder", placeholder);
        b.AddAttribute(seq++, "rows", "10");
        b.AddAttribute(seq++, "style", "width:100%;");
        b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => setter(e.Value?.ToString() ?? "")));
        b.AddContent(seq++, value);
        b.CloseElement();
        b.CloseElement();
    }

    void AddReadonlyCode(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "pre");
        b.AddAttribute(seq++, "style", "white-space:pre-wrap;word-break:break-word;background:#1e1e2e;color:#cdd6f4;padding:12px;border-radius:6px;font-size:12px;line-height:1.5;max-height:400px;overflow:auto;margin:4px 0;");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void AddPromptRef(RenderTreeBuilder b, ref int seq, string title, bool expanded, Action toggle,
        IReadOnlyList<(string Name, string Placeholder, string Value)> vars, string templateBody)
    {
        AddButton(b, ref seq, expanded ? "收起「" + title + "」" : "查看「" + title + "」默认参考", "gs-btn-sm", false, () =>
        {
            toggle();
            StateHasChanged();
        });
        // 用 CSS 显隐（而非 if 条件渲染）保持 RenderTree 的 seq 稳定，避免 Blazor diff 因元素增减错位导致展开后看不到内容
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "style", expanded ? "margin-top:4px;" : "display:none;");
        if (vars != null && vars.Count > 0)
        {
            AddHint(b, ref seq, "这个提示词里有几个动态变量（你填的时候用占位符写，运行时会自动替换成下面的值）：");
            foreach (var v in vars)
                AddHint(b, ref seq, "· " + v.Name + "（占位符 " + v.Placeholder + "）= " + v.Value);
        }
        AddHint(b, ref seq, "下面这段就是「你该填进框里的模板原文」。自定义时把它复制进框里，改固定文字、占位符原样保留；留空则直接用这段默认。");
        AddReadonlyCode(b, ref seq, templateBody);
        b.CloseElement();
    }

    void AddNumberInput(RenderTreeBuilder b, ref int seq, string label, int value, Action<int> setter, int min, int max)
    {
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-field");
        b.OpenComponent<InputNumber<int>>(seq++);
        b.AddAttribute(seq++, "Value", value);
        b.AddAttribute(seq++, "Min", min);
        b.AddAttribute(seq++, "Max", max);
        b.AddAttribute(seq++, "Style", "width:100%;");
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<int>(this, setter));
        b.CloseComponent();
        b.CloseElement();
    }

    void AddNumberInputD(RenderTreeBuilder b, ref int seq, string label, double value, Action<double> setter,
        double min, double max, double step)
    {
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-field");
        b.OpenComponent<InputNumber<double>>(seq++);
        b.AddAttribute(seq++, "Value", value);
        b.AddAttribute(seq++, "Min", min);
        b.AddAttribute(seq++, "Max", max);
        b.AddAttribute(seq++, "Step", step);
        b.AddAttribute(seq++, "Style", "width:100%;");
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<double>(this, setter));
        b.CloseComponent();
        b.CloseElement();
    }

    void AddSwitch(RenderTreeBuilder b, ref int seq, string label, bool value, Action<bool> setter)
    {
        AddLabel(b, ref seq, label);
        b.OpenComponent<Switch>(seq++);
        b.AddAttribute(seq++, "Checked", value);
        b.AddAttribute(seq++, "CheckedChildren", "开");
        b.AddAttribute(seq++, "UnCheckedChildren", "关");
        b.AddAttribute(seq++, "CheckedChanged", EventCallback.Factory.Create<bool>(this, setter));
        b.CloseComponent();
    }

    void AddLabeledSelect(RenderTreeBuilder b, ref int seq, string value, Action<string> setter,
        List<(string Value, string Label, bool Enabled)> options)
    {
        AddLabeledSelectCore(b, ref seq, value, setter, options);
    }

    void AddLabeledSelectCore(RenderTreeBuilder b, ref int seq, string value, Action<string> setter,
        List<(string Value, string Label, bool Enabled)> options)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-field");
        b.OpenComponent<Select<string, string>>(seq++);
        b.AddAttribute(seq++, "Value", value);
        b.AddAttribute(seq++, "Style", "width:100%;");
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<string>(this, setter));
        b.AddAttribute(seq++, "ChildContent", (RenderFragment)(childBuilder =>
        {
            int c = 0;
            foreach ((string optValue, string optLabel, bool enabled) in options)
            {
                childBuilder.OpenComponent<SelectOption<string, string>>(c++);
                childBuilder.AddAttribute(c++, "Value", optValue);
                childBuilder.AddAttribute(c++, "Label", optLabel);
                childBuilder.AddAttribute(c++, "Disabled", !enabled);
                childBuilder.CloseComponent();
            }
        }));
        b.CloseComponent();
        b.CloseElement();
    }

    void AddButton(RenderTreeBuilder b, ref int seq, string text, string? cls, bool disabled, Action onClick)
    {
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "class", cls ?? "gs-btn");
        if (disabled)
            b.AddAttribute(seq++, "disabled", true);
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, onClick));
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    /// <summary>多选勾选框：显示单个布尔勾选项（用于打断环节多选）。</summary>
    void AddCheckbox(RenderTreeBuilder b, ref int seq, string label, bool value, Action<bool> setter)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gs-field");
        b.AddAttribute(seq++, "style", "display:flex;align-items:center;gap:8px;margin-bottom:4px;");
        b.OpenComponent<Checkbox>(seq++);
        b.AddAttribute(seq++, "Checked", value);
        b.AddAttribute(seq++, "CheckedChanged", EventCallback.Factory.Create<bool>(this, setter));
        b.AddAttribute(seq++, "ChildContent", (RenderFragment)(cb =>
        {
            cb.AddContent(0, label);
        }));
        b.CloseComponent();
        b.CloseElement();
    }

    // ===== GPT-SoVITS 服务状态探测 =====

    async Task ProbeGptStatusAsync()
    {
        if (Configuration == null)
            return;
        _gptBusy = true;
        _gptStatus = "检测中…";
        StateHasChanged();
        try
        {
            int port = Configuration.Port;
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", port);
            Task done = await Task.WhenAny(connectTask, Task.Delay(1000));
            if (done != connectTask || !client.Connected)
            {
                _gptStatus = $"端口 {port} 未开放（1 秒内无响应，api_v2 服务未启动）";
            }
            else
            {
                _gptStatus = $"端口 {port} 已开放（GPT-SoVITS api_v2 服务可能正在运行）";
            }
        }
        catch (Exception ex)
        {
            _gptStatus = $"检测失败：{ex.Message}";
        }
        finally
        {
            _gptBusy = false;
            StateHasChanged();
        }
    }

    // ===== 音色预设扫描 =====

    async Task ScanPresetsAsync()
    {
        if (Configuration == null)
            return;
        _scanning = true;
        _scanMsg = "正在扫描…";
        StateHasChanged();
        try
        {
            GptSovitsScanResult result = GptSovitsPresetScanner.ScanFull(Configuration.InstallPath);
            _scannedPresets = result.Presets;
            _scanMsg = result.Message;
            if (result.Capabilities.SuggestedPort is int suggested &&
                suggested >= 1 && suggested <= 65535 &&
                Configuration.Port != suggested)
            {
                Configuration.Port = suggested;
                _scanMsg += $"\n已根据安装目录识别服务端口：{suggested}";
            }
        }
        catch (Exception ex)
        {
            _scanMsg = $"扫描失败：{ex.Message}";
            _scannedPresets = new();
        }
        finally
        {
            _scanning = false;
            StateHasChanged();
        }
    }

    // ===== 情感 ref 目录一键识别 =====

    async Task AutoDetectRefsAsync()
    {
        if (Configuration == null)
            return;
        _refScanMsg = "";
        StateHasChanged();
        try
        {
            var refs = Configuration.EmotionRefs ??= new List<EmotionRefLibrary.EmotionRef>();
            var library = new EmotionRefLibrary();
            library.Rebuild(refs);
            library.ScanRefDirectory(Configuration.InstallPath);
            var merged = library.All;
            int added = merged.Count - refs.Count;
            Configuration.EmotionRefs = merged.ToList();
            _refScanMsg = added > 0
                ? $"识别完成：新增 {added} 个情感 ref（共 {merged.Count} 个）。配置优先，已存在的不会覆盖。"
                : merged.Count > 0
                    ? $"扫描完成：共 {merged.Count} 个情感 ref，无新增（配置优先）。"
                    : "未在 ref/ 目录识别到情感音频（需建 {InstallPath}/ref/{情感}_{强度}/xxx.wav）。";

            // 同步模块内的 ref 库 + 刷新注入给 LLM 的标准情感清单（及时更新）
            try
            {
                Module?.RefreshPromptAfterRefRebuild();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EmotionTTS] 刷新提示词失败：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _refScanMsg = $"识别失败：{ex.Message}";
        }
        finally
        {
            StateHasChanged();
        }
    }

    async Task AutoDetectForeignRefsAsync()
    {
        if (Configuration == null)
            return;
        _refScanMsg = "";
        StateHasChanged();
        try
        {
            var refs = Configuration.ForeignRefs ??= new List<EmotionRefLibrary.EmotionRef>();
            var library = new EmotionRefLibrary();
            library.RebuildForeign(refs);
            library.ScanForeignDirectory(Configuration.InstallPath);
            var merged = library.ForeignAll;
            int added = merged.Count - refs.Count;
            Configuration.ForeignRefs = merged.ToList();
            _refScanMsg = added > 0
                ? $"识别完成：新增 {added} 个异音色 ref（共 {merged.Count} 个）。配置优先，已存在的不会覆盖。"
                : merged.Count > 0
                    ? $"扫描完成：共 {merged.Count} 个异音色 ref，无新增（配置优先）。"
                    : "未在 foreign_ref/ 目录识别到音频（需建 {InstallPath}/foreign_ref/【情感】台词.wav）。";

            try
            {
                Module?.RefreshPromptAfterRefRebuild();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EmotionTTS] 刷新提示词失败：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _refScanMsg = $"识别失败：{ex.Message}";
        }
        finally
        {
            StateHasChanged();
        }
    }

}
