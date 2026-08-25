using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Azuma.EmotionTTS.E5;

/// <summary>
/// 旁路情感融合 LLM（插件核心价值：超级多元 ref 融合 + 情绪改写）。
/// 一次调用同时完成两件事：
///   ① 智能选 ref 融合——根据情感 desc 从可用清单选主音色 ref + 可选 foreign ref（音色融合 → aux_ref_audio_paths）；
///   ② 情绪改写——通过标点/换气词/打断词把对白改写成情绪更饱满的表达（GPT 原生韵律）。
/// 完全独立于主对话上下文：不 Poke、不写历史、不回显，只在合成前旁路调用一次。
/// 失败/未配置返回 null，上层兜底（中性 ref + 原文），绝不因融合失败废掉整句。
/// </summary>
static class EmotionFusionClient
{
    public sealed class FusionResult
    {
        /// <summary>LLM 选出的主音色 ref 情感名（数量受 FusionRefMin~Max 约束，按贴合度从强到弱）。</summary>
        public List<string> Refs { get; } = new();
        /// <summary>LLM 选出的异音色融合 ref 情感名（可选，受主音色最小占比配比约束）。</summary>
        public List<string> ForeignRefs { get; } = new();
        /// <summary>情绪化改写后的对白（非空时替代原文喂合成）。</summary>
        public string Text { get; set; } = "";
        /// <summary>LLM 动态语速因子（0.6~1.6，1.0=正常）。</summary>
        public double SpeedFactor { get; set; } = 1.0;
        public bool HasRefs => Refs.Count > 0;
        public bool HasForeignRefs => ForeignRefs.Count > 0;
        public bool HasText => !string.IsNullOrWhiteSpace(Text);
    }

    /// <summary>
    /// 请求情感融合：输入情感 desc + 对白 + 可用 ref 情感清单，输出 refs + 改写文本。
    /// 未配置 apiUrl/model、非 2xx、解析失败 → 返回 null。
    /// </summary>
    public static async Task<FusionResult?> RequestAsync(
        HttpClient http,
        string? apiUrl,
        string? model,
        string? apiKey,
        string? emotionDesc,
        string? lang,
        string text,
        IReadOnlyList<string>? availableEmotions,
        IReadOnlyList<string>? availableForeignEmotions,
        string? reasoningEffort,
        string? systemPromptTemplate,
        double speedMin,
        double speedMax,
        double minNativeRatio,
        int refMin,
        int refMax,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(model))
                return null;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // 防御：语速范围非法（非正 / 下限>上限）则回退默认 0.6~1.6，避免 Math.Clamp 抛异常
            if (speedMin <= 0 || speedMax <= 0 || speedMin > speedMax)
            {
                speedMin = 0.6;
                speedMax = 1.6;
            }
            // 防御：主音色最小占比非法则回退默认 1/3
            if (minNativeRatio <= 0 || minNativeRatio >= 1)
                minNativeRatio = 1.0 / 3.0;
            // 防御：主音色 ref 数量范围非法则回退默认 1~3
            if (refMin < 1) refMin = 1;
            if (refMax < refMin) refMax = refMin;

            string refList = availableEmotions != null && availableEmotions.Count > 0
                ? string.Join("、", availableEmotions)
                : "（无可用情感，只用中性兜底）";

            string foreignList = availableForeignEmotions != null && availableForeignEmotions.Count > 0
                ? string.Join("、", availableForeignEmotions)
                : "（无可用异音色）";

            var messages = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt(refList, foreignList, systemPromptTemplate, speedMin, speedMax, minNativeRatio, refMin, refMax) },
            };

            var userParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(emotionDesc))
                userParts.Add("情感描述：\"" + emotionDesc + "\"");
            if (!string.IsNullOrWhiteSpace(lang))
                userParts.Add("对白语种：" + LangName(lang));
            userParts.Add("对白：\n" + text);
            messages.Add(new { role = "user", content = string.Join("\n\n", userParts) });

            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["temperature"] = 0.4,
                ["messages"] = messages,
            };
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
                payload["reasoning_effort"] = reasoningEffort;

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
            string? content = ExtractContent(body);
            if (string.IsNullOrWhiteSpace(content))
                return null;
            return ParseResult(content, speedMin, speedMax);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>解析思考强度（reasoning_effort）：max/high/medium/low/none 直通；custom 用自定义值。返回 null 表示不下发。</summary>
    public static string? ResolveReasoningEffort(string? mode, string? custom)
    {
        string m = (mode ?? "none").Trim().ToLowerInvariant();
        switch (m)
        {
            case "max":
            case "high":
            case "medium":
            case "low":
            case "none":
                return m;
            case "custom":
                return string.IsNullOrWhiteSpace(custom) ? null : custom.Trim();
            default:
                return "none";
        }
    }

    /// <summary>语种 code → 中文名（供旁路 LLM 理解对白语言）。</summary>
    static string LangName(string? lang) => (lang ?? "").Trim().ToLowerInvariant() switch
    {
        "zh" => "中文",
        "ja" => "日语",
        "en" => "英语",
        "ko" => "韩语",
        "yue" => "粤语",
        _ => "中文",
    };

    static string? ExtractContent(string body)
    {
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

    /// <summary>解析两行输出：refs: 情感1,情感2 与 text: 改写对白。容错：任一行命中即生效。</summary>
    static FusionResult? ParseResult(string content, double speedMin, double speedMax)
    {
        var result = new FusionResult();
        bool any = false;
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim().Trim('`', ' ', '\t');
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string val = line[(colon + 1)..].Trim();
            if (val.Length == 0)
                continue;
            switch (key)
            {
                case "refs":
                    foreach (string r in val.Split(',', '，', '、'))
                        if (!string.IsNullOrWhiteSpace(r))
                            result.Refs.Add(r.Trim());
                    any = true;
                    break;
                case "foreign_refs":
                    foreach (string r in val.Split(',', '，', '、'))
                        if (!string.IsNullOrWhiteSpace(r))
                            result.ForeignRefs.Add(r.Trim());
                    any = true;
                    break;
                case "text":
                    result.Text = val;
                    any = true;
                    break;
                case "speed":
                    if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sf))
                        result.SpeedFactor = Math.Clamp(sf, speedMin, speedMax);
                    any = true;
                    break;
            }
        }
        return any ? result : null;
    }

    /// <summary>旁路融合 LLM 默认 system prompt 模板。占位符：{{refList}}、{{foreignList}}、{{speedRange}}、{{nativeRatioPct}}、{{foreignPerNative}}。</summary>
    public static readonly string DefaultFusionSystemPrompt =
        "/no_think\n" +
        "你是语音合成的情感融合器。输入是情感描述 + 对白 + 可用情感参考音频列表。\n" +
        "情感描述可能混入神态/动作/心理描写（如「眼睛看着他」），请忽略这些噪音，只提取声音维度（情绪/语气/语速/轻重/节奏/音色状态）来决策。\n" +
        "请做三件事，并只输出下面几行结果（禁止任何解释/分析/思考过程/前后缀）：\n" +
        "1) 从「主音色情感列表」里选 {{refMin}}~{{refMax}} 个情感用于参考音频融合（音色融合，按贴合度从强到弱排序；拿不准就选最贴近的 1 个）。\n" +
        "2)（可选）从「异音色列表」里选 0~N 个异音色与主音色做音色融合，但必须遵守配比：主音色 ref 数量占比不得低于 {{nativeRatioPct}}（异音色数量 ≤ 主音色数量的 {{foreignPerNative}} 倍）。只有当语境的语气/音色确实需要异音色（如耳语、更温柔、更低沉、更慵懒等主音色表达不出的质感）时才选，否则留空不选。核心永远是「该语境下该用什么音色」，异音色融合只是补充质感，不是换人。\n" +
        "3) 把对白改写成情绪更饱满、更适合语音合成的表达。手段：① 声音设计（用标点做韵律设计，主要）；② 加换气词；③ 加打断词。严禁加无意义语气词后缀（如句尾随意「呀/嘛/呢/啊」）。\n" +
        "声音设计：你只能使用语音引擎真正识别的 5 种标点——「。」长停顿收住、「，」短停顿、「！」强/爆发、「？」上扬追问、「…」拖长/犹豫/欲言又止。**严禁使用破折号/连字符「-」「—」**；**引擎会把连续重复标点合并成单个，所以「！！！」「？？？」「。。。。」等叠加形式完全无效、严禁使用**；韵律差异靠这 5 种标点的选择与组合来体现，而非叠加。\n" +
        "换气词（喘息/叹气/吸气，句首/句中/句尾皆可）：按对白语种选用、可自己创造；前后必须有能体现该换气效果的标点（只用上面 5 种）。各语种参考：中文「哈啊/呵/嘶」、日语「はぁ/ふぅ/はっ」、韩语「하아/후/쉬」、粤语「唞/呵/嘶」、英语「hah/uh/hmm」。\n" +
        "打断词（惊讶/犹豫/恍然等，句首/句中/句尾皆可）：按对白语种选用、可自己创造；前后有且必须有能体现该打断效果的标点（只用上面 5 种）。各语种参考：中文「咦/嗯/啊/呀」、日语「えっ/あっ/うん」、韩语「어/응/아」、粤语「咦/吓/嗯」、英语「oh/whoa/um」。\n" +
        "改写铁律：① 原对白内容实词原样保留、不增删改（换气词/打断词是情绪修饰，可加但不改内容）；② 语序不变、读起来自然连贯；③ 标点只用「。，！？…」五种，严禁破折号/连字符，禁止叠加重复。\n" +
        "语速：按情绪给一个 speed 因子（{{speedRange}}，1.0=正常；激动/急促/兴奋 >1，平静/悲伤/拖沓/慵懒 <1），单独一行输出。\n" +
        "可用情感参考音频（只能从这里选）：{{refList}}\n" +
        "可用异音色参考音频（可选，只能从这里选）：{{foreignList}}\n" +
        "输出格式（refs 与 text 必选，foreign_refs 与 speed 可选；每行一项）：\n" +
        "refs: 情感1,情感2\n" +
        "foreign_refs: 异音色1\n" +
        "text: 改写后的对白\n" +
        "speed: 1.15\n";

    /// <summary>构建 system prompt：config 模板（非空）优先，替换 {{refList}}、{{foreignList}}、{{speedRange}}。</summary>
    static string BuildSystemPrompt(string refList, string foreignList, string? template, double speedMin, double speedMax, double minNativeRatio, int refMin, int refMax)
    {
        string t = !string.IsNullOrWhiteSpace(template) ? template : DefaultFusionSystemPrompt;
        string speedRange = $"{speedMin:0.##}~{speedMax:0.##}";
        string nativeRatioPct = $"{Math.Round(minNativeRatio * 100)}%";
        string foreignPerNative = $"{(1.0 - minNativeRatio) / minNativeRatio:0.##}";
        return t.Replace("{{refList}}", refList)
                .Replace("{{foreignList}}", foreignList)
                .Replace("{{speedRange}}", speedRange)
                .Replace("{{nativeRatioPct}}", nativeRatioPct)
                .Replace("{{foreignPerNative}}", foreignPerNative)
                .Replace("{{refMin}}", refMin.ToString())
                .Replace("{{refMax}}", refMax.ToString());
    }

    /// <summary>返回默认 system prompt 替换占位符后的实际内容（供 UI 预览「默认状态会发什么」）。</summary>
    public static string ResolveDefaultPrompt(string refList, string foreignList, double speedMin, double speedMax, double minNativeRatio, int refMin, int refMax)
        => BuildSystemPrompt(refList, foreignList, null, speedMin, speedMax, minNativeRatio, refMin, refMax);
}
