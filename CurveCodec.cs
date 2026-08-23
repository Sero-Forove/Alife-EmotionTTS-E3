using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 50 占位坐标 + DSP 曲线编解码（E2 核心）。
/// 占位系统：每个「空隙单元」和「字」都拆 10 个占位——序列：
///   前空×10 | 字0×10 | 字间空×10 | 字1×10 | … | 字N×10 | 后空×10
/// 坐标（0 起）：前空 0~9；字 i：10+20i ~ 19+20i；字间空 i：20+20i ~ 29+20i；后空：20N ~ 20N+9。
/// 总占位 = 20N + 10（N=字数）。
///
/// DSP-LLM 输出曲线（受限格式，值域 ±100）：
///   pitch: 10@-20,25@+10,35@+30     关键帧：占位坐标@值（中间插值）
///   speed: 22@-15,32@+20
///   volume:40@-30
///   vibrato:5:0.3                    参数式：频率Hz:深度
///   timbre:25@空灵,33@金属           离散切换：占位@音色名
///   env:   10@ease-in                字内包络附加：占位@包络名
/// 解析失败/无曲线 → 返回 null（DSP 中性，Cosy 情感底料照播）。
/// </summary>
static class CurveCodec
{
    public const int UnitSlots = 10;

    /// <summary>总占位数。</summary>
    public static int TotalSlots(int charCount) => 20 * Math.Max(0, charCount) + 10;

    /// <summary>字 i 的占位起点。</summary>
    public static int CharStart(int i) => 10 + 20 * i;

    /// <summary>字间空 i（字 i 之后）的占位起点。</summary>
    public static int GapStart(int i) => 20 + 20 * i;

    /// <summary>后空起点。</summary>
    public static int TailStart(int charCount) => 20 * Math.Max(0, charCount);

    /// <summary>判断占位坐标属于哪个字/空隙（供 DSP 阶段映射）：返回 "char:i" / "gap:i" / "head" / "tail"。</summary>
    public static string SlotOwner(int slot, int charCount)
    {
        if (slot < 0 || slot >= TotalSlots(charCount))
            return "";
        if (slot < 10)
            return "head";
        int tail = TailStart(charCount);
        if (slot >= tail)
            return "tail";
        int idx = (slot - 10) / 20;
        int rem = (slot - 10) % 20;
        return rem < 10 ? $"char:{idx}" : $"gap:{idx}";
    }

    // ===== DSP-LLM 系统提示词（简短、教维度 + 占位 + 格式）=====

    public static string BuildSystemPrompt(int charCount, bool hasHistory = false)
    {
        int total = TotalSlots(charCount);
        string s = "你是语音表现设计师。输入是一段中文对白。请输出该对白的逐段表现曲线。\n" +
               "占位坐标：每字、每处字间空隙、句首句尾空隙各拆10个占位，共 " + total + " 个（0~" + (total - 1) + "）。" +
               "第1个字的占位大致在 10~19，字间空隙 20~29，依此类推；字内 0=字头 9=字尾。\n" +
               "全部 8 个维度（值域 -100~+100，0=不变）：\n" +
               "- pitch 音调：正=升高（尖/上扬），负=降低（低沉）\n" +
               "- speed 语速：正=加快，负=放慢\n" +
               "- volume 音量：正=大声/爆发，负=轻声/耳语\n" +
               "- vibrato 颤音：参数式 频率Hz:深度(0~1)\n" +
               "- timbre 音色：空灵/金属/混响/失真（离散，按占位切换）\n" +
               "- env 包络：ease-in/ease-out/ADSR（给指定占位附近的字做字内渐变）\n" +
               "- pause 停顿：字后插入停顿（毫秒 0~2000，戏剧性停顿/悬念/句间呼吸用）\n" +
               "- breath 气口：吸气/喘/叹息（离散，按占位切换，如句首吸气、叹气）\n" +
               "输出格式（每维度一行，逗号分隔关键帧「占位@值」，只输出曲线，禁止任何解释/分析/思考过程/前后缀）：\n" +
               "pitch:10@-20,25@+10\nspeed:22@-15\nvibrato:5:0.3\npause:25@180\nbreath:40@吸气\n" +
               "发挥要求：8 个维度中，凡本句能用来表达情感的都要给出（无变化才省略）。关键帧数量按句子的情绪复杂度自由决定——" +
               "短句 2~5 帧即可，长句/多转折/强情绪可多帧（10+ 也允许），不要逐占位铺满即可。\n" +
               "大胆运用对比与层次：句首低抑→句中扬起→句尾收束；重点词夸张、铺垫词收敛；情绪越强变化幅度越大；" +
               "停顿与气口是戏剧性武器，在悬念、强调、情绪转折处善用。\n";
        if (hasHistory)
            s += "重要：本句是长段对白中的一句，之前句子的曲线已作为上下文给出（坐标是各句局部坐标，仅供参考数值走势）。" +
                 "请让本句开头的关键帧值尽量贴近前句末尾值（前句末尾值 = 前句曲线最后几个关键帧的值），" +
                 "避免音量/音调/语速在句边界突变，保持整段语音曲线连续自然；句内仍按本句情感自由发挥。";
        return s;
    }

    // ===== DSP-LLM 请求（OpenAI 兼容，不开思考、低 max_tokens）=====

    /// <summary>
    /// 请求曲线（E3：整段一次调用——fullText 与 sentenceText 传同一整段文本，historyCurves 传 null；
    /// 提示词自动识别"整段"模式：不再要求"不要为整段输出"）。
    /// fullText=完整对白；sentenceText=输出曲线的对象（占位坐标按它的字数）；
    /// emotionDesc=完整情感描述；historyCurves=前句曲线（E2 逐句衔接用，E3 传 null）。
    /// </summary>
    public static async Task<string?> RequestCurvesAsync(
        HttpClient http,
        string apiUrl,
        string model,
        string? apiKey,
        string fullText,
        string sentenceText,
        string? emotionDesc,
        IReadOnlyList<string>? historyCurves,
        CancellationToken cancellationToken)
    {
        try
        {
            int charCount = sentenceText.Replace(" ", "").Replace("\n", "").Length;
            bool hasHistory = historyCurves != null && historyCurves.Count > 0;
            // E3 整段模式：fullText 与 sentenceText 相同（同一整段）→ 直接为整段输出曲线
            bool wholePassage = string.Equals(fullText, sentenceText, StringComparison.Ordinal) &&
                                !string.IsNullOrWhiteSpace(fullText);
            var messages = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt(charCount, hasHistory) },
            };

            var userParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(emotionDesc))
                userParts.Add("情感（完整）：\"" + emotionDesc + "\"");
            if (wholePassage)
            {
                // E3 整段：完整对白即输出对象，一次输出整段曲线
                userParts.Add("完整对白（整段，请为整段输出曲线，占位坐标按整段字数计算）：\n" + sentenceText);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(fullText))
                    userParts.Add("完整对白（整段，供理解全局内容与情绪走向，不要为整段输出曲线）：\n" + fullText);
                userParts.Add("本句（请只为本句输出曲线，占位坐标按本句字数计算）：\n" + sentenceText);
            }
            if (hasHistory)
            {
                string hist = string.Join("\n\n", historyCurves!.Select(h => "--- 前句曲线 ---\n" + h));
                userParts.Add("之前句子已生成的完整曲线（仅作衔接参考：坐标是各句局部坐标，勿直接照搬坐标，参考数值走势与末尾值，让本句开头自然衔接）：\n" + hist);
            }
            messages.Add(new { role = "user", content = string.Join("\n\n", userParts) });

            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["temperature"] = 0.3,
                // 推理模型（如 deepseek-v4-flash）默认思考会耗尽 token 且 content 为空：显式关闭思考
                // （显式：thinking disabled；隐式：提示词已禁止任何解释/分析/思考过程输出）
                ["thinking"] = new { type = "disabled" },
                // 不设置 max_tokens：全维度曲线需要完整输出，交由服务端默认上限
                ["messages"] = messages,
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content))
            {
                return content.GetString();
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ===== 曲线解析 =====

    public sealed class CurveSpec
    {
        public List<(int slot, int value)> Pitch = new();
        public List<(int slot, int value)> Speed = new();
        public List<(int slot, int value)> Volume = new();
        public List<(int slot, int value)> Pause = new();          // 字后停顿毫秒
        public List<(int slot, string name)> Timbre = new();
        public List<(int slot, string name)> Env = new();
        public List<(int slot, string name)> Breath = new();       // 吸气/喘/叹息
        public double VibratoRate;
        public double VibratoDepth;
        public bool HasAny => Pitch.Count > 0 || Speed.Count > 0 || Volume.Count > 0 ||
                              Pause.Count > 0 || Timbre.Count > 0 || Env.Count > 0 ||
                              Breath.Count > 0 || VibratoRate > 0;
    }

    /// <summary>解析 LLM 曲线输出。格式错/空 → null（DSP 中性）。值钳制 ±100。</summary>
    public static CurveSpec? ParseCurves(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var spec = new CurveSpec();
        bool any = false;
        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Trim();
            int colon = t.IndexOf(':');
            if (colon <= 0)
                continue;
            string dim = t[..colon].Trim().ToLowerInvariant();
            string body = t[(colon + 1)..].Trim();
            if (body.Length == 0)
                continue;
            try
            {
                switch (dim)
                {
                    case "pitch":
                        any |= ParseFrames(body, spec.Pitch);
                        break;
                    case "speed":
                        any |= ParseFrames(body, spec.Speed);
                        break;
                    case "volume":
                        any |= ParseFrames(body, spec.Volume);
                        break;
                    case "pause":
                        any |= ParsePauseFrames(body, spec.Pause);
                        break;
                    case "vibrato":
                    case "vib":
                        ParseVibrato(body, spec);
                        any = true;
                        break;
                    case "timbre":
                        any |= ParseNamedFrames(body, spec.Timbre);
                        break;
                    case "env":
                    case "envelope":
                        any |= ParseNamedFrames(body, spec.Env);
                        break;
                    case "breath":
                        any |= ParseNamedFrames(body, spec.Breath);
                        break;
                }
            }
            catch (Exception)
            {
                // 单维度解析失败：跳过该维度
            }
        }
        return any ? spec : null;
    }

    static bool ParseFrames(string body, List<(int, int)> target)
    {
        bool ok = false;
        foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            int at = p.IndexOf('@');
            if (at <= 0)
                continue;
            if (int.TryParse(p[..at].Trim(), out int slot) &&
                int.TryParse(p[(at + 1)..].Trim(), out int value))
            {
                target.Add((Math.Max(0, slot), Math.Clamp(value, -100, 100)));
                ok = true;
            }
        }
        return ok;
    }

    static bool ParseNamedFrames(string body, List<(int, string)> target)
    {
        bool ok = false;
        foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            int at = p.IndexOf('@');
            if (at <= 0)
                continue;
            if (int.TryParse(p[..at].Trim(), out int slot) && p[(at + 1)..].Trim().Length > 0)
            {
                target.Add((Math.Max(0, slot), p[(at + 1)..].Trim()));
                ok = true;
            }
        }
        return ok;
    }

    /// <summary>pause 关键帧：占位@毫秒（0~5000），不做 ±100 钳制。</summary>
    static bool ParsePauseFrames(string body, List<(int, int)> target)
    {
        bool ok = false;
        foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            int at = p.IndexOf('@');
            if (at <= 0)
                continue;
            if (int.TryParse(p[..at].Trim(), out int slot) &&
                int.TryParse(p[(at + 1)..].Trim(), out int value))
            {
                target.Add((Math.Max(0, slot), Math.Clamp(value, 0, 5000)));
                ok = true;
            }
        }
        return ok;
    }

    static void ParseVibrato(string body, CurveSpec spec)
    {
        var parts = body.Split(new[] { ':', ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            double.TryParse(parts[0].Trim(), out double rate) &&
            double.TryParse(parts[1].Trim(), out double depth))
        {
            spec.VibratoRate = Math.Clamp(rate, 1, 15);
            spec.VibratoDepth = Math.Clamp(depth, 0.05, 1.5);
        }
    }

    /// <summary>
    /// 曲线库维护请求（独立维护 LLM）：**开思考**（不传 thinking，推理归纳需要）、max_tokens 放宽。
    /// 输入：高分曲线条目（带分）；输出：维护指令（ADDPREF:__style_经典名->曲线 / DELX:pref:__curve_xxx）。
    /// 与 DSP-LLM（关思考、只输出曲线）区分——维护是低频批处理，允许推理。
    /// </summary>
    public static async Task<string?> RequestMaintenanceAsync(
        HttpClient http,
        string apiUrl,
        string model,
        string? apiKey,
        IReadOnlyList<string> curveEntries,
        CancellationToken cancellationToken)
    {
        try
        {
            string system = "你是语音风格库管理员。输入是若干条已评分的语音表现曲线（8 维 DSP 曲线，分数=被认可程度）。\n" +
                "任务：\n" +
                "1. 归纳高分曲线（分数高者优先）中反复出现的共同特征，沉淀为 1~3 条经典风格模板，输出：ADDPREF:__style_风格名->曲线文本（风格名用简短中文，曲线保留原格式）。\n" +
                "2. 清理明显低分（负分或接近 -50）或与其他高度重复的曲线，输出：DELX:pref:__curve_xxx。\n" +
                "只输出维护指令（每行一条），不要任何解释。";
            string user = "评分曲线样本：\n" + string.Join("\n\n", curveEntries);

            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["temperature"] = 0.4,
                // 开思考：不传 thinking（维护需要推理归纳；与 DSP-LLM 的 thinking:disabled 区分）
                // 不设 max_tokens：交给服务端默认上限
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content))
            {
                string? c = content.GetString();
                return string.IsNullOrWhiteSpace(c) ? null : c;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>关键帧插值：给定占位坐标，返回该坐标的值（线性插值，越界取最近端点）。</summary>
    public static int Interpolate(List<(int slot, int value)> frames, int slot)
    {
        if (frames.Count == 0)
            return 0;
        if (frames.Count == 1)
            return frames[0].Item2;
        if (slot <= frames[0].Item1)
            return frames[0].Item2;
        var last = frames[^1];
        if (slot >= last.Item1)
            return last.Item2;
        for (int i = 1; i < frames.Count; i++)
        {
            var (s0, v0) = frames[i - 1];
            var (s1, v1) = frames[i];
            if (slot >= s0 && slot <= s1)
            {
                if (s1 == s0)
                    return v0;
                double f = (slot - s0) / (double)(s1 - s0);
                return (int)Math.Round(v0 + (v1 - v0) * f);
            }
        }
        return 0;
    }
}
