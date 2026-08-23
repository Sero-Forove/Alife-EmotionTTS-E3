using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 发声记录 + 反馈 + 音调偏好学习存储（json 持久化）。
/// - 记录 AI 每次 <tune> 发声用的参数（emotion/tier/speed/pitch/seg）
/// - 采集 👍/👎 反馈（WebUI 按钮 + 对话自然反馈）
/// - 按情感统计正反馈的参数规律 → 偏好条目（注入 Prompt 引导 LLM）
/// </summary>
public sealed class EmotionVocalRecordStore
{
    /// <summary>一次发声记录。</summary>
    public sealed class VocalRecord
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("ts")] public string Timestamp { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("emotion")] public string Emotion { get; set; } = "";
        [JsonPropertyName("tier")] public string Tier { get; set; } = "";
        [JsonPropertyName("speed")] public double Speed { get; set; } = 1.0;
        [JsonPropertyName("pitch")] public double Pitch { get; set; } = 0;
        [JsonPropertyName("breath")] public string Breath { get; set; } = "";
        [JsonPropertyName("segments")] public int SegmentCount { get; set; }
        [JsonPropertyName("feedback")] public string Feedback { get; set; } = ""; // "good"/"bad"/""
    }

    /// <summary>偏好条目：某情感下效果好的音调/速度规律。</summary>
    public sealed class PitchPreference
    {
        [JsonPropertyName("emotion")] public string Emotion { get; set; } = "";
        [JsonPropertyName("pitchMin")] public double PitchMin { get; set; }
        [JsonPropertyName("pitchMax")] public double PitchMax { get; set; }
        [JsonPropertyName("speedMin")] public double SpeedMin { get; set; }
        [JsonPropertyName("speedMax")] public double SpeedMax { get; set; }
        [JsonPropertyName("samples")] public int Samples { get; set; }
        [JsonPropertyName("badCount")] public int BadCount { get; set; }
    }

    readonly string storePath;
    readonly object gate = new();
    List<VocalRecord> records = new();
    List<PitchPreference> preferences = new();
    long nextId = 1;
    bool dirty;

    public EmotionVocalRecordStore(string storePath)
    {
        this.storePath = storePath;
        Load();
    }

    // ==== 记录 ====

    /// <summary>记录一次发声（合成时调用）。高频：只标脏，由 FlushIfDirty 批量落盘。</summary>
    public long Record(EmotionVoiceDirective directive, string text)
    {
        long id;
        lock (gate)
        {
            id = nextId++;
            records.Add(new VocalRecord
            {
                Id = id,
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Text = Truncate(text, 40),
                Emotion = directive?.Emotion ?? "",
                Tier = directive?.Tier ?? "",
                Speed = directive?.Speed ?? 1.0,
                Pitch = directive?.PitchOffset ?? 0,
                Breath = directive?.Breath ?? "",
                SegmentCount = directive?.Segments?.Count ?? 0,
            });
            if (records.Count > 200)
                records.RemoveRange(0, records.Count - 200);
            MarkDirty();
        }
        return id;
    }

    /// <summary>记录一次 speak（E2：emotion desc 作情感标签；未给则标"普通"）。高频：只标脏。</summary>
    public long RecordPlain(string text, string? emotion = null)
    {
        long id;
        lock (gate)
        {
            id = nextId++;
            records.Add(new VocalRecord
            {
                Id = id,
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Text = Truncate(text, 40),
                Emotion = string.IsNullOrWhiteSpace(emotion) ? "普通" : Truncate(emotion.Trim(), 12),
                Tier = "中",
                Speed = 1.0,
                Pitch = 0,
            });
            if (records.Count > 200)
                records.RemoveRange(0, records.Count - 200);
            MarkDirty();
        }
        return id;
    }

    /// <summary>对最近一次未反馈的发声打反馈。返回是否命中。</summary>
    public bool FeedbackLatest(string feedback)
    {
        lock (gate)
        {
            VocalRecord? latest = records.LastOrDefault(r => string.IsNullOrEmpty(r.Feedback));
            if (latest == null)
                return false;
            latest.Feedback = feedback;
            LearnFromRecord(latest);
            Save();
            return true;
        }
    }

    /// <summary>按 id 打反馈（WebUI 按钮）。返回命中的记录（可用于同步知识表打分）。</summary>
    public VocalRecord? FeedbackById(long id, string feedback)
    {
        lock (gate)
        {
            VocalRecord? r = records.FirstOrDefault(x => x.Id == id);
            if (r == null)
                return null;
            r.Feedback = feedback;
            LearnFromRecord(r);
            Save();
            return r;
        }
    }

    /// <summary>最近记录（UI 显示，最多 N 条）。</summary>
    public IReadOnlyList<VocalRecord> RecentRecords(int n = 10)
    {
        lock (gate)
            return records.TakeLast(n).Reverse().ToArray();
    }

    /// <summary>当前偏好条目（注入 Prompt / UI 显示）。</summary>
    public IReadOnlyList<PitchPreference> AllPreferences
    {
        get
        {
            lock (gate)
                return preferences.ToArray();
        }
    }

    /// <summary>偏好格式化供注入（只显示样本数足够的）。</summary>
    public string FormatPreferencesForInjection(int minSamples = 2)
    {
        lock (gate)
        {
            var lines = new List<string>();
            foreach (PitchPreference p in preferences.Where(p => p.Samples >= minSamples))
            {
                lines.Add($"  [{p.Emotion}] 音调 {p.PitchMin:+0.#;-0.#}~{p.PitchMax:+0.#;-0.#} 半音，语速 {p.SpeedMin:0.##}~{p.SpeedMax:0.##}（{p.Samples} 次正反馈{(p.BadCount > 0 ? $"，{p.BadCount} 次避雷" : "")}）");
            }
            return lines.Count == 0 ? "" : string.Join("\n", lines);
        }
    }

    /// <summary>删除某情感的偏好（LLM 可 DELPREF 清除）。</summary>
    public bool RemovePreference(string emotion)
    {
        lock (gate)
        {
            int removed = preferences.RemoveAll(p => string.Equals(p.Emotion, emotion, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                Save();
            return removed > 0;
        }
    }

    // ==== 学习 ====

    void LearnFromRecord(VocalRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.Emotion))
            return;
        PitchPreference? pref = preferences.FirstOrDefault(p =>
            string.Equals(p.Emotion, r.Emotion, StringComparison.OrdinalIgnoreCase));

        if (r.Feedback == "good")
        {
            if (pref == null)
            {
                pref = new PitchPreference
                {
                    Emotion = r.Emotion,
                    PitchMin = r.Pitch, PitchMax = r.Pitch,
                    SpeedMin = r.Speed, SpeedMax = r.Speed,
                    Samples = 1,
                };
                preferences.Add(pref);
            }
            else
            {
                // 扩展区间（纳入本次值）
                pref.PitchMin = Math.Min(pref.PitchMin, r.Pitch);
                pref.PitchMax = Math.Max(pref.PitchMax, r.Pitch);
                pref.SpeedMin = Math.Min(pref.SpeedMin, r.Speed);
                pref.SpeedMax = Math.Max(pref.SpeedMax, r.Speed);
                pref.Samples++;
            }
        }
        else if (r.Feedback == "bad")
        {
            if (pref != null)
                pref.BadCount++;
        }
    }

    // ==== 持久化 ====

    void Load()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storePath) || !File.Exists(storePath))
                return;
            string json = File.ReadAllText(storePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("records", out var recs))
                records = JsonSerializer.Deserialize<List<VocalRecord>>(recs.GetRawText()) ?? new List<VocalRecord>();
            if (doc.RootElement.TryGetProperty("preferences", out var prefs))
                preferences = JsonSerializer.Deserialize<List<PitchPreference>>(prefs.GetRawText()) ?? new List<PitchPreference>();
            if (doc.RootElement.TryGetProperty("nextId", out var nid))
                nextId = nid.GetInt64();
            if (records.Count > 0)
                nextId = Math.Max(nextId, records.Max(r => r.Id) + 1);
        }
        catch (Exception)
        {
            records = new List<VocalRecord>();
            preferences = new List<PitchPreference>();
            nextId = 1;
        }
    }

    /// <summary>标记数据已变（高频记录不立即落盘，批量 flush 时写）。</summary>
    void MarkDirty()
    {
        dirty = true;
    }

    /// <summary>若数据有变则落盘（对话结束等低频时机调用；无变则零开销）。</summary>
    public void FlushIfDirty()
    {
        lock (gate)
        {
            if (!dirty)
                return;
            dirty = false;
            Save();
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
            var obj = new
            {
                records,
                preferences,
                nextId,
            };
            // 非缩进 + 原子写（临时文件替换），速度快且防写一半损坏
            string tmp = storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(obj));
            File.Move(tmp, storePath, overwrite: true);
        }
        catch (Exception)
        {
            // 保存失败不影响运行
        }
    }

    static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
