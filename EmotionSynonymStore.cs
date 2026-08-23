using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 动态同义词表（LLM 维护）：LLM 写的未知情感 → ref 表可用情感的映射。
/// - 报错时由插件注入对话流，LLM 建议映射 → 插件写入
/// - 每次报错时附上整表，LLM 标记过时/错误条目 → 插件删除
/// - 持久化到 json（重启不丢）
/// </summary>
public sealed class EmotionSynonymStore
{
    /// <summary>单条同义词映射（带来源时间戳，便于 LLM 审查清除）。</summary>
    public sealed class SynonymEntry
    {
        [JsonPropertyName("from")] public string From { get; set; } = "";   // LLM 写的词
        [JsonPropertyName("to")] public string To { get; set; } = "";       // ref 表里的标准情感
        [JsonPropertyName("source")] public string Source { get; set; } = "llm";
        [JsonPropertyName("ts")] public string Timestamp { get; set; } = ""; // 写入时间
    }

    readonly string storePath;
    readonly object gate = new();
    List<SynonymEntry> entries = new();

    public EmotionSynonymStore(string storePath)
    {
        this.storePath = storePath;
        Load();
    }

    /// <summary>查同义词：LLM 写的词 → ref 标准情感；未命中返回 null。</summary>
    public string? Resolve(string from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return null;
        lock (gate)
        {
            foreach (SynonymEntry e in entries)
            {
                if (string.Equals(e.From, from.Trim(), StringComparison.OrdinalIgnoreCase))
                    return e.To;
            }
        }
        return null;
    }

    /// <summary>写入同义词（LLM 建议）。已存在则更新时间戳。</summary>
    public void Upsert(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return;
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock (gate)
        {
            foreach (SynonymEntry e in entries)
            {
                if (string.Equals(e.From, from, StringComparison.OrdinalIgnoreCase))
                {
                    e.To = to;
                    e.Timestamp = now;
                    Save();
                    return;
                }
            }
            entries.Add(new SynonymEntry { From = from.Trim(), To = to.Trim(), Source = "llm", Timestamp = now });
            Save();
        }
    }

    /// <summary>删除指定条目（LLM 审查清除过时/错误词汇）。</summary>
    public void Remove(string from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return;
        lock (gate)
        {
            int removed = entries.RemoveAll(e => string.Equals(e.From, from, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                Save();
        }
    }

    /// <summary>当前全部条目（供注入给 LLM 审查）。</summary>
    public IReadOnlyList<SynonymEntry> All
    {
        get
        {
            lock (gate)
                return entries.ToArray();
        }
    }

    /// <summary>当前是否有任何条目。</summary>
    public bool HasAny
    {
        get
        {
            lock (gate)
                return entries.Count > 0;
        }
    }

    /// <summary>格式化供注入：每行 "from → to (来源/时间)"。</summary>
    public string FormatForInjection()
    {
        lock (gate)
        {
            if (entries.Count == 0)
                return "（当前同义词表为空）";
            var lines = new List<string>();
            foreach (SynonymEntry e in entries)
                lines.Add($"  {e.From} → {e.To}（{e.Source}/{e.Timestamp}）");
            return string.Join("\n", lines);
        }
    }

    void Load()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storePath) || !File.Exists(storePath))
                return;
            string json = File.ReadAllText(storePath);
            if (string.IsNullOrWhiteSpace(json))
                return;
            entries = JsonSerializer.Deserialize<List<SynonymEntry>>(json) ?? new List<SynonymEntry>();
        }
        catch (Exception)
        {
            entries = new List<SynonymEntry>();
        }
    }

    void Save()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storePath))
                return;
            string dir = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(storePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch (Exception)
        {
            // 保存失败不影响运行
        }
    }
}
