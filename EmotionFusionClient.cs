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
///   ① 智能选 ref 融合——根据情感 desc 从可用 ref 情感清单选 1~3 个（音色融合 → aux_ref_audio_paths）；
///   ② 情绪改写——把对白改写成情绪更饱满的表达（加标点/语气词/拟声词，GPT 原生韵律）。
/// 完全独立于主对话上下文：不 Poke、不写历史、不回显，只在合成前旁路调用一次。
/// 失败/未配置返回 null，上层兜底（中性 ref + 原文），绝不因融合失败废掉整句。
/// </summary>
static class EmotionFusionClient
{
    public sealed class FusionResult
    {
        /// <summary>LLM 选出的 ref 情感名（1~3 个，按贴合度从强到弱）。</summary>
        public List<string> Refs { get; } = new();
        /// <summary>情绪化改写后的对白（非空时替代原文喂合成）。</summary>
        public string Text { get; set; } = "";
        public bool HasRefs => Refs.Count > 0;
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
        string text,
        IReadOnlyList<string>? availableEmotions,
        string? reasoningEffort,
        string? systemPromptTemplate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(model))
                return null;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string refList = availableEmotions != null && availableEmotions.Count > 0
                ? string.Join("、", availableEmotions)
                : "（无可用情感，只用中性兜底）";

            var messages = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt(refList, systemPromptTemplate) },
            };

            var userParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(emotionDesc))
                userParts.Add("情感描述：\"" + emotionDesc + "\"");
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
            return ParseResult(content);
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
    static FusionResult? ParseResult(string content)
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
                case "text":
                    result.Text = val;
                    any = true;
                    break;
            }
        }
        return any ? result : null;
    }

    /// <summary>旁路融合 LLM 默认 system prompt 模板。占位符：{{refList}}。</summary>
    static readonly string DefaultFusionSystemPrompt =
        "/no_think\n" +
        "你是语音合成的情感融合器。输入是情感描述 + 对白 + 可用情感参考音频列表。\n" +
        "情感描述可能混入神态/动作/心理描写（如「眼睛看着他」），请忽略这些噪音，只提取声音维度（情绪/语气/语速/轻重/节奏/音色状态）来决策。\n" +
        "请做两件事，并只输出下面两行结果（禁止任何解释/分析/思考过程/前后缀）：\n" +
        "1) 从可用情感列表里选 1~3 个情感用于参考音频融合（音色融合，按贴合度从强到弱排序；拿不准就选最贴近的 1 个）。\n" +
        "2) 把对白改写成情绪更饱满、更适合语音合成的表达。手段：① 声音设计（用标点做韵律设计，主要）；② 加换气词；③ 加打断词。严禁加无意义语气词后缀（如句尾随意「呀/嘛/呢/啊」）。\n" +
        "声音设计：你只能使用语音引擎真正识别的 6 种标点——「。」长停顿收住、「，」短停顿、「！」强/爆发、「？」上扬追问、「…」拖长/犹豫/欲言又止、「-」转折拖音。**引擎会把连续重复标点合并成单个，所以「！！！」「？？？」「。。。。」等叠加形式完全无效、严禁使用**；韵律差异靠这 6 种标点的选择与组合来体现，而非叠加。\n" +
        "换气词（喘息/叹气/吸气，句首/句中/句尾皆可）：如「哈啊」「嗯啊」「哈」「呵」「嘶」等，不限于示例；前后必须有能体现该换气效果的标点（只用上面 6 种），如「哈啊…」「…嘶…」「哈啊！」「嘶-」。\n" +
        "打断词（惊讶/犹豫/恍然等，句首/句中/句尾皆可）：如「咦」「嗯」「啊」「呀」「哦」等，不限于示例；前后有且必须有能体现该打断效果的标点（只用上面 6 种），如「咦？」「嗯…」「啊-」。\n" +
        "改写铁律：① 原对白内容实词原样保留、不增删改（换气词/打断词是情绪修饰，可加但不改内容）；② 语序不变、读起来自然连贯；③ 标点只用「。，！？…-」六种，禁止叠加重复。\n" +
        "可用情感参考音频（只能从这里选）：{{refList}}\n" +
        "输出格式（每行一项，refs 与 text 各一行）：\n" +
        "refs: 情感1,情感2\n" +
        "text: 改写后的对白\n";

    /// <summary>构建 system prompt：config 模板（非空）优先，替换 {{refList}}。</summary>
    static string BuildSystemPrompt(string refList, string? template)
    {
        string t = !string.IsNullOrWhiteSpace(template) ? template : DefaultFusionSystemPrompt;
        return t.Replace("{{refList}}", refList);
    }
}
