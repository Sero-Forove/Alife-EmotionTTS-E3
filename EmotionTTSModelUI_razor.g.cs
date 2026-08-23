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

namespace Azuma.EmotionTTS.E3;

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

    // ===== 对齐环境 + 知识表 UI 状态 =====
    readonly EmotionAlignEnvManager _envManager = new();
    EmotionAlignEnvManager.EnvStatus _envStatus;
    bool _envProbing;
    string _envLog = "";
    bool _envBusy;
    /// <summary>知识表重建操作反馈（临时提示）。</summary>
    string _knowledgeMsg = "";

    static readonly List<(string Value, string Label, bool Enabled)> LangOptions = new()
    {
        ("zh", "中文 zh", true),
        ("ja", "日语 ja", true),
        ("en", "英语 en", true),
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
        b.AddContent(i++, "EmotionTTS E3");
        b.CloseElement();

        BuildHero(b, ref i);

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
        });

        SectionPanel(b, ref i, "情感参考音频库", () =>
        {
            var configRefs = Configuration!.EmotionRefs ??= new List<EmotionRefLibrary.EmotionRef>();

            AddButton(b, ref i, "一键识别 ref 目录", "gs-btn", false, () =>
            {
                _ = AutoDetectRefsAsync();
            });
            AddHint(b, ref i, "扫描 {InstallPath}/ref/{情感}_{强度}/ 目录并回填下方配置（配置优先，已存在的情感不覆盖）。");
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
        });

        SectionPanel(b, ref i, "DSP-LLM 曲线（整段一次）", () =>
        {
            AddSwitch(b, ref i, "启用曲线 DSP", Configuration!.EnableCurveDsp,
                v => Configuration.EnableCurveDsp = v);
            AddHint(b, ref i, "整段一次生成 8 维曲线（pitch/speed/volume/vibrato/timbre/env/pause/breath），情感看全局、省 token。关：纯 GPT 合成拼接。");

            AddInput(b, ref i, "API 地址（OpenAI 兼容）", Configuration.DspLlmUrl, v =>
            {
                Configuration.DspLlmUrl = v;
            }, "如 http://127.0.0.1:8000/v1/chat/completions");
            AddInput(b, ref i, "模型名", Configuration.DspLlmModel, v =>
            {
                Configuration.DspLlmModel = v;
            }, "如 qwen3-flash");
            AddInput(b, ref i, "API Key（可为空）", Configuration.DspLlmKey, v =>
            {
                Configuration.DspLlmKey = v;
            }, "本地服务通常留空");
            AddHint(b, ref i, "主 LLM 只输出 <speak><emotion desc=\"...\"/>对白</speak>；DSP-LLM 对拼接后的整段音频一次性生成 8 维曲线。");
        });

        SectionPanel(b, ref i, "字级对齐设置", () =>
        {
            AddLabeledSelect(b, ref i, Configuration!.AlignEngine ?? "Auto",
                v => Configuration.AlignEngine = v,
                new List<(string, string, bool)>
                {
                    ("Auto", "自动（有环境用 WhisperX，否则分摊）", true),
                    ("WhisperX", "WhisperX（中日英，需下载模型）", true),
                    ("Proportional", "分摊（零依赖兜底）", true),
                });
            AddHint(b, ref i, "默认 Auto：有 Python/模型时自动用 WhisperX 常驻进程（GPU 对齐），否则按字数分摊时长。");

            AddInput(b, ref i, "对齐 Python 路径（可留空自动探测）", Configuration.AlignPythonPath, v =>
            {
                Configuration.AlignPythonPath = v;
            }, "留空自动探测独立 venv → PATH");
            AddHint(b, ref i, "自动探测优先用插件专属独立 venv（Cache\\EmotionTTS\\whisperx-venv），其次 PATH。开箱即用，无需手动配置。");

            AddSwitch(b, ref i, "缓存对齐结果", Configuration.EnableAlignCache,
                v => Configuration.EnableAlignCache = v);
            AddHint(b, ref i, "开：同文本+同 wav 的对齐结果缓存，重复说话零成本。");

            AddLabel(b, ref i, "对齐环境管理");
            AddButton(b, ref i, _envProbing ? "检测中…" : "检测环境", "gs-btn", _envProbing || _envBusy, () =>
            {
                _ = ProbeEnvAsync();
            });
            AddButton(b, ref i, "创建 WhisperX 独立 venv（含 CUDA torch）", "gs-scan-btn", _envBusy, () =>
            {
                _ = CreateWhisperxVenvAsync();
            });
            AddHint(b, ref i, $"独立环境位置：{{Storage}}\\Cache\\EmotionTTS\\whisperx-venv。创建完成后对齐 Python 自动优先使用（无需再填 AlignPythonPath）。首次约 3-4GB 下载。");
            AddButton(b, ref i, "安装 WhisperX（到当前 Python）", "gs-btn", _envBusy, () =>
            {
                _ = InstallAsync(EmotionAlignEnvManager.AlignBackend.WhisperX);
            });
            AddButton(b, ref i, "修复 numpy 版本", "gs-btn", _envBusy, () =>
            {
                _ = FixNumpyAsync();
            });
            AddButton(b, ref i, "预下载 WhisperX 模型", "gs-btn", _envBusy, () =>
            {
                _ = PreloadModelAsync();
            });

            if (_envStatus == null)
            {
                AddHint(b, ref i, "点「检测环境」查看 Python / GPU / whisperx 状态。");
            }
            else
            {
                var s = _envStatus;
                AddHint(b, ref i, $"Python: {s.PythonPath}（{s.PythonVersion}）");
                AddHint(b, ref i, $"GPU: {(s.CudaAvailable ? s.GpuName : "不可用（走 CPU）")}");
                AddHint(b, ref i, $"WhisperX: {(s.WhisperXInstalled ? "已安装 " + s.WhisperXVersion : "未安装")}");
                AddHint(b, ref i, $"numpy: {s.NumpyVersion} {(s.NumpyCompatible ? "（兼容）" : "（需修复：与 numba 冲突）")}");
                if (!string.IsNullOrEmpty(s.Message))
                    AddHint(b, ref i, s.Message);
            }

            if (!string.IsNullOrEmpty(_envLog))
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "gs-hint2");
                b.AddAttribute(i++, "style", "white-space:pre-wrap;max-height:180px;overflow-y:auto;");
                b.AddContent(i++, _envLog);
                b.CloseElement();
            }
        });

        // 发声反馈 + 音调偏好学习（O/X 按钮；对话自然反馈）
        SectionPanel(b, ref i, "发声反馈（学习音调偏好）", () =>
        {
            var vocal = module?.VocalStore;
            if (vocal == null)
            {
                AddHint(b, ref i, "发声记录未就绪（角色激活后可用）。");
            }
            else
            {
                AddHint(b, ref i, "对最近一次说话打 O（效果好）/ X（不好），插件会学习该情感的音调/速度偏好。也可在对话里说「这句好听」「太尖了」等。");
                var recent = vocal.RecentRecords(8);
                if (recent.Count == 0)
                {
                    AddHint(b, ref i, "还没有发声记录。等 AI 用 <speak> 说过话后再来反馈。");
                }
                else
                {
                    foreach (var r in recent)
                    {
                        string mark = r.Feedback == "good" ? " [O]" : r.Feedback == "bad" ? " [X]" : "";
                        AddLabel(b, ref i, $"[{r.Emotion} p={r.Pitch:+0.#;-0.#} s={r.Speed:0.##}] {r.Text}{mark}");
                        if (string.IsNullOrEmpty(r.Feedback))
                        {
                            long rid = r.Id;
                            EmotionTTSSpeechModel m = module!;
                            AddButton(b, ref i, "O", "gs-btn-sm", false, () =>
                            {
                                m.ApplyVocalFeedback(rid, true);
                                StateHasChanged();
                            });
                            AddButton(b, ref i, "X", "gs-btn-sm", false, () =>
                            {
                                m.ApplyVocalFeedback(rid, false);
                                StateHasChanged();
                            });
                        }
                    }
                }
                var prefs = vocal.AllPreferences;
                if (prefs.Count > 0)
                {
                    AddHint(b, ref i, "已学到的偏好：");
                    foreach (var p in prefs)
                        AddLabel(b, ref i, $"[{p.Emotion}] pitch {p.PitchMin:+0.#;-0.#}~{p.PitchMax:+0.#;-0.#}st speed {p.SpeedMin:0.##}~{p.SpeedMax:0.##}（{p.Samples}好{p.BadCount}差）");
                }
            }
        });

        // 统一语音知识表（工作表 + 备份表）
        SectionPanel(b, ref i, "语音知识表（工作表/备份表）", () =>
        {
            var k = module?.KnowledgeStore;
            if (k == null)
            {
                AddHint(b, ref i, "知识表未就绪（角色激活后可用）。");
            }
            else
            {
                AddHint(b, ref i, "工作表=学习沉淀（可删改、按重要度排序、可重建）；备份表=全历史档案（只更新重要度，永不删除）。LLM 可发 ADDPREF/MERGEX/DELX/EXTEND/CLRWORK/SCOREX 指令操作。");
                var work = k.WorkEntries;
                var backup = k.BackupEntries;
                if (work.Count == 0)
                {
                    AddHint(b, ref i, "工作表为空。等 AI 说过话后会逐步沉淀，或由 LLM 学习沉淀。");
                }
                else
                {
                    foreach (var e in work.Take(20))
                        AddLabel(b, ref i, e.Display);
                    if (work.Count > 20)
                        AddLabel(b, ref i, $"...（共 {work.Count} 条，仅显示前 20）");
                }
                AddHint(b, ref i, $"备份表共 {backup.Count} 条（全历史档案）。");
                AddButton(b, ref i, "从备份重建工作表", "gs-btn-sm", false, () =>
                {
                    int backupCount = k.BackupEntries.Count;
                    if (backupCount == 0)
                    {
                        _knowledgeMsg = "备份表为空，没有可恢复的条目（等 AI 说过话沉淀后再试）。";
                    }
                    else
                    {
                        k.RebuildWorkFromBackup();
                        _knowledgeMsg = $"已从备份重建工作表，共恢复 {backupCount} 条。";
                    }
                    StateHasChanged();
                });
                if (!string.IsNullOrEmpty(_knowledgeMsg))
                    AddHint(b, ref i, _knowledgeMsg);
            }
        });

        SectionPanel(b, ref i, "安全兜底", () =>
        {
            AddSwitch(b, ref i, "DSP 失败回退原音频", Configuration!.DspFailSafe,
                v => Configuration.DspFailSafe = v);
            AddHint(b, ref i, "开：对齐/DSP 失败时用原音频，绝不因处理失败而静音。");
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
        b.AddContent(i++, "GPT-SoVITS api_v2 合成 + speak/emotion/ref 标签 + 整段曲线 DSP + WhisperX 对齐。AI 用 <speak><emotion desc=\"...\"/>对白</speak> 说话，情感按 ref 库切参考音频。");
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
            library.Rebuild(refs, Configuration.InstallPath);
            var merged = library.All;
            int added = merged.Count - refs.Count;
            Configuration.EmotionRefs = merged.ToList();
            _refScanMsg = added > 0
                ? $"识别完成：新增 {added} 个情感 ref（共 {merged.Count} 个）。配置优先，已存在的不会覆盖。"
                : merged.Count > 0
                    ? $"扫描完成：共 {merged.Count} 个情感 ref，无新增（配置优先）。"
                    : "未在 ref/ 目录识别到情感音频（需建 {InstallPath}/ref/{情感}_{强度}/xxx.wav）。";
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

    // ===== 环境操作（异步，进度回 UI）=====

    async Task ProbeEnvAsync()
    {
        _envProbing = true;
        _envBusy = true;
        StateHasChanged();
        try
        {
            _envStatus = _envManager.Probe(Configuration!);
        }
        finally
        {
            _envProbing = false;
            _envBusy = false;
            StateHasChanged();
        }
    }

    async Task InstallAsync(EmotionAlignEnvManager.AlignBackend backend)
    {
        _envBusy = true;
        _envLog = "";
        _envManager.Progress += AppendLog;
        StateHasChanged();
        try
        {
            await _envManager.InstallAsync(Configuration!, backend);
        }
        finally
        {
            _envManager.Progress -= AppendLog;
            _envBusy = false;
            _envStatus = _envManager.Probe(Configuration!);
            StateHasChanged();
        }
    }

    async Task CreateWhisperxVenvAsync()
    {
        _envBusy = true;
        _envLog = "";
        _envManager.Progress += AppendLog;
        StateHasChanged();
        try
        {
            await _envManager.CreateWhisperxVenvAsync(Configuration!);
        }
        finally
        {
            _envManager.Progress -= AppendLog;
            _envBusy = false;
            _envStatus = _envManager.Probe(Configuration!);
            StateHasChanged();
        }
    }

    async Task FixNumpyAsync()
    {
        _envBusy = true;
        _envLog = "";
        _envManager.Progress += AppendLog;
        StateHasChanged();
        try
        {
            await _envManager.FixNumpyAsync(Configuration!);
        }
        finally
        {
            _envManager.Progress -= AppendLog;
            _envBusy = false;
            _envStatus = _envManager.Probe(Configuration!);
            StateHasChanged();
        }
    }

    async Task PreloadModelAsync()
    {
        _envBusy = true;
        _envLog = "";
        _envManager.Progress += AppendLog;
        StateHasChanged();
        try
        {
            await _envManager.PreloadWhisperXModelAsync(Configuration!);
        }
        finally
        {
            _envManager.Progress -= AppendLog;
            _envBusy = false;
            StateHasChanged();
        }
    }

    void AppendLog(string line)
    {
        _envLog += line + "\n";
        if (_envLog.Length > 4000)
            _envLog = _envLog[^3000..];
        StateHasChanged();
    }
}
