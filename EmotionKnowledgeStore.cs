using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 唯一语音知识表（工作表 work + 备份表 backup，格式相同）。
/// - 工作表：不限长度，按重要程度排序；学习机制（频率/人评/LLM清理）自我收敛；允许删改。
/// - 备份表：不限长度（无限增长），只按重要程度排序 + 更新重要程度，不删改（全历史档案）。
/// - 条目类型：synonym（同义词，靠 count/score，不给人评）/ pref（音调/音量偏好，可人评）/ pattern（完整表达模式，可人评）。
/// - 自然语言操作：LLM 可发 ADDPREF/MERGEX/DELX/EXTEND/CLRWORK 指令操作工作表。
/// </summary>
public sealed class EmotionKnowledgeStore
{
    public const string TypeSynonym = "synonym";   // 同义词：词 → 标准情感（靠调用频率，不给人评）
    public const string TypePreference = "pref";   // 偏好：情感 → 单一参数好值（可人评）
    public const string TypePattern = "pattern";   // 方法链条：情感+场景 → 完整声音设计（可人评）
    public const string TypeEnvelope = "envelope"; // 平滑/包络方法（可人评）

    /// <summary>阶位（E3 统一一阶：所有学习数据固定 Level1；字段保留兼容旧 JSON）。</summary>
    public const int Level1 = 1;

    /// <summary>评分上限（LLM 输出超大值封顶用）。</summary>
    public const int MaxScore = 100;
    /// <summary>评分下限（LLM 输出极负值移除阈值）。</summary>
    public const int RemoveScoreThreshold = -50;
    /// <summary>单次评分最大幅度（LLM 一次能加/扣的分数上限；超出按此钳制，防止一击必杀）。</summary>
    public const int MaxDeltaPerScore = 30;

    /// <summary>知识条目（工作表与备份表共用结构，按 score 排序）。</summary>
    public sealed class KnowledgeEntry
    {
        [JsonPropertyName("type")] public string Type { get; set; } = TypeSynonym;
        [JsonPropertyName("key")] public string Key { get; set; } = "";        // 触发词/情感/方法名
        [JsonPropertyName("value")] public string Value { get; set; } = "";    // 映射目标或参数组合或方法标签快照
        [JsonPropertyName("level")] public int Level { get; set; } = Level1;   // 阶位（E3 统一 1；保留兼容旧 JSON）
        [JsonPropertyName("count")] public int Count { get; set; }             // 调用频率（所有类型都有）
        [JsonPropertyName("good")] public int Good { get; set; }               // 好评（synonym 恒 0，不给人评）
        [JsonPropertyName("bad")] public int Bad { get; set; }                 // 差评（synonym 恒 0）
        [JsonPropertyName("score")] public int Score { get; set; }             // 综合分（重要程度）
        [JsonPropertyName("src")] public string Source { get; set; } = "llm";
        [JsonPropertyName("ts")] public string Timestamp { get; set; } = "";

        /// <summary>是否可被人 O/X 评价（同义词不评，靠频率）。</summary>
        public bool Ratable => !string.Equals(Type, TypeSynonym, StringComparison.OrdinalIgnoreCase);

        public string Display =>
            $"[{Type}] {Key} → {Value}（用{Count}次{(Ratable ? $"，好评{Good}/差评{Bad}" : "")}，分{Score}，{Timestamp}）";
    }

    readonly string workPath;
    readonly string backupPath;
    readonly object gate = new();
    List<KnowledgeEntry> work = new();     // 工作表（插件实际用，可删改，学习收敛）
    List<KnowledgeEntry> backup = new();   // 备份表（全历史，只排序+更新重要程度）
    bool dirty;                            // 脏标记：查询计数等低危变化只标脏，由 FlushIfDirty 批量落盘

    public EmotionKnowledgeStore(string workPath, string backupPath)
    {
        this.workPath = workPath;
        this.backupPath = backupPath;
        Load();
    }

    // ==== 查询（工作表）====

    public string? ResolveSynonym(string from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;
        lock (gate)
        {
            KnowledgeEntry? e = work.FirstOrDefault(x =>
                x.Type == TypeSynonym && string.Equals(x.Key, from.Trim(), StringComparison.OrdinalIgnoreCase));
            if (e != null) { e.Count++; RecomputeScore(e); MarkDirty(); return e.Value; }
            // 备份兜底（找回后补回工作表）
            e = backup.FirstOrDefault(x =>
                x.Type == TypeSynonym && string.Equals(x.Key, from.Trim(), StringComparison.OrdinalIgnoreCase));
            if (e != null)
            {
                var copy = Clone(e);
                copy.Count++;
                RecomputeScore(copy);
                work.Add(copy);
                MarkDirty();
                return copy.Value;
            }
        }
        return null;
    }

    public string? ResolvePreference(string emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion)) return null;
        lock (gate)
        {
            KnowledgeEntry? e = work.FirstOrDefault(x =>
                x.Type == TypePreference && string.Equals(x.Key, emotion, StringComparison.OrdinalIgnoreCase));
            if (e != null) { e.Count++; RecomputeScore(e); MarkDirty(); return e.Value; }
        }
        return null;
    }

    public string? ResolvePattern(string emotion, string scene = "")
    {
        if (string.IsNullOrWhiteSpace(emotion)) return null;
        lock (gate)
        {
            KnowledgeEntry? e = work.FirstOrDefault(x =>
                x.Type == TypePattern &&
                string.Equals(x.Key, emotion, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(scene) || x.Value.Contains(scene, StringComparison.OrdinalIgnoreCase)));
            if (e != null) { e.Count++; RecomputeScore(e); MarkDirty(); return e.Value; }
        }
        return null;
    }

    // ==== 写入（工作表+备份表都记录；备份只更新不改删）====

    public void AddOrUpdate(string type, string key, string value, string source = "llm")
    {
        AddOrUpdate(type, key, value, Level1, source, persist: true);
    }

    /// <summary>带阶位的写入（方法条目专用）。persist=false 时只标脏（高频路径，由 FlushIfDirty 批量落盘）。</summary>
    public void AddOrUpdate(string type, string key, string value, int level, string source = "llm", bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock (gate)
        {
            KnowledgeEntry? we = work.FirstOrDefault(x =>
                string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (we != null)
            {
                we.Value = value; we.Timestamp = now; we.Level = level; RecomputeScore(we);
            }
            else
            {
                work.Add(new KnowledgeEntry { Type = type, Key = key, Value = value, Source = source, Timestamp = now, Score = 1, Level = level });
            }

            KnowledgeEntry? be = backup.FirstOrDefault(x =>
                string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (be != null)
            {
                be.Value = value; be.Timestamp = now; be.Level = level; RecomputeScore(be);
            }
            else
            {
                backup.Add(new KnowledgeEntry { Type = type, Key = key, Value = value, Source = source, Timestamp = now, Score = 1, Level = level });
            }

            SortEntries(work);
            SortEntries(backup);
            if (persist)
                Save();
            else
                MarkDirty();
        }
    }

    // ==== 方法体系（E3 统一一阶）====

    /// <summary>注册/更新一个方法（自动纳管：不等判断价值直接收进工作表，清理时再筛）。</summary>
    public void RegisterMethod(string key, string value, int level, string source = "auto", bool persist = true)
    {
        // E3 四阶砍一阶：level 参数兼容保留但固定 Level1
        AddOrUpdate(TypePreference, key, value, Level1, source, persist);
    }

    /// <summary>按方法名取方法值（工作表优先；备份兜底找回补回）。</summary>
    public string? ResolveMethod(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        lock (gate)
        {
            KnowledgeEntry? e = work.FirstOrDefault(x =>
                x.Type == TypePreference && string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (e != null) { e.Count++; RecomputeScore(e); MarkDirty(); return e.Value; }
            e = backup.FirstOrDefault(x =>
                x.Type == TypePreference && string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (e != null)
            {
                var copy = Clone(e);
                copy.Count++;
                RecomputeScore(copy);
                work.Add(copy);
                MarkDirty();
                return copy.Value;
            }
        }
        return null;
    }

    /// <summary>列全部方法（工作表 Type=pref，按分排序）。</summary>
    public IReadOnlyList<KnowledgeEntry> MethodsOfLevel(int level)
    {
        lock (gate)
        {
            return work.Where(x => x.Type == TypePreference)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Count)
                .ToArray();
        }
    }

    /// <summary>工作表条目总数（含所有类型）。</summary>
    public int TotalCount()
    {
        lock (gate)
            return work.Count;
    }

    /// <summary>
    /// 自动纳管发声方法：把一次发声的指令摘要（属性串）收进工作表（统一一阶）。
    /// 已有同名方法 → 更新值+计数；没有 → 直接新增（不等判断价值，清理时再筛）。
    /// </summary>
    public void AutoNestMethod(string key, string directiveSummary, int level)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        // 高频路径（每次发声）：不立即落盘，只标脏；由 FlushIfDirty 批量写
        RegisterMethod(key, directiveSummary, level, "auto", persist: false);
        lock (gate)
        {
            KnowledgeEntry? we = work.FirstOrDefault(x =>
                x.Type == TypePreference && string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (we != null) { we.Count++; RecomputeScore(we); }
            SortEntries(work);
            MarkDirty();
        }
    }

    /// <summary>
    /// 清理工作表：保留高分，腾出空间（备份表不动——全历史档案）。
    /// 清理时把被清条目在备份里标记降权（历史记录保留）。
    /// </summary>
    public int CleanupWork(int maxEntries, int minScoreToKeep = 0)
    {
        lock (gate)
        {
            int before = work.Count;
            if (work.Count <= maxEntries)
                return 0;
            // 高分优先保留：分高于阈值且数量不足时保留高分；其余按分数排序清到 maxEntries
            var sorted = work.OrderByDescending(x => x.Score).ThenByDescending(x => x.Count).ToList();
            var keep = new List<KnowledgeEntry>();
            var drop = new List<KnowledgeEntry>();
            foreach (KnowledgeEntry e in sorted)
            {
                if (keep.Count < maxEntries && e.Score >= minScoreToKeep)
                    keep.Add(e);
                else
                    drop.Add(e);
            }
            foreach (KnowledgeEntry e in drop)
            {
                // 备份表降权（历史保留）
                KnowledgeEntry? be = backup.FirstOrDefault(x =>
                    string.Equals(x.Type, e.Type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Key, e.Key, StringComparison.OrdinalIgnoreCase));
                if (be != null)
                {
                    be.Score = Math.Min(be.Score, e.Score);
                    be.Score -= 1;
                }
            }
            work = keep;
            SortEntries(work);
            SortEntries(backup);
            Save();
            return before - work.Count;
        }
    }

    /// <summary>记录调用（count++，频率统计）。高频（每次发声），只标脏由 FlushIfDirty 批量落盘。</summary>
    public void RecordUse(string type, string key)
    {
        lock (gate)
        {
            foreach (List<KnowledgeEntry> list in new[] { work, backup })
            {
                KnowledgeEntry? e = list.FirstOrDefault(x =>
                    string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (e != null) { e.Count++; RecomputeScore(e); }
            }
            SortEntries(work);
            SortEntries(backup);
            MarkDirty();
        }
    }

    /// <summary>人评 O/X（只对可评类型；同义词不评）。标脏由 FlushIfDirty 批量落盘（避免在合成链路阻塞）。</summary>
    public void RecordFeedback(string type, string key, bool good)
    {
        lock (gate)
        {
            foreach (List<KnowledgeEntry> list in new[] { work, backup })
            {
                KnowledgeEntry? e = list.FirstOrDefault(x =>
                    string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (e != null && e.Ratable)
                {
                    if (good) e.Good++; else e.Bad++;
                    RecomputeScore(e);
                }
            }
            SortEntries(work);
            SortEntries(backup);
            MarkDirty();
        }
    }

    // ==== 自然语言操作（LLM 指令，只改工作表）====

    /// <summary>
    /// 解析 LLM 指令操作工作表。返回 true 表示消费。
    /// ADDPREF:key->value | MERGEX:a,b->新 | DELX:type:key | EXTEND:type:key->v | CLRWORK
    /// </summary>
    public bool HandleDirective(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        bool handled = false;
        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Trim();
            try
            {
                if (t.StartsWith("ADDPREF:", StringComparison.OrdinalIgnoreCase))
                {
                    string body = t["ADDPREF:".Length..].Trim();
                    int arrow = body.IndexOf("->", StringComparison.Ordinal);
                    if (arrow > 0)
                    {
                        string key = body[..arrow].Trim();
                        string val = body[(arrow + 2)..].Trim();
                        if (key.Length > 0 && val.Length > 0)
                        {
                            // __method_ 前缀 → 方法条目（自动推断阶位：跨阶引用 use: 或含高阶标记）
                            if (key.StartsWith(MethodKeyPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                RegisterMethod(key, val, InferLevel(val), "llm", persist: false);
                            }
                            else
                            {
                                AddOrUpdate(TypePreference, key, val, Level1, "llm", persist: false);
                            }
                            handled = true;
                        }
                    }
                }
                else if (t.StartsWith("ADDMETHOD:", StringComparison.OrdinalIgnoreCase))
                {
                    // ADDMETHOD:方法名->完整标签快照（@L2 等阶位后缀 E3 忽略，统一一阶）
                    string body = t["ADDMETHOD:".Length..].Trim();
                    int arrow = body.IndexOf("->", StringComparison.Ordinal);
                    if (arrow > 0)
                    {
                        string head = body[..arrow].Trim();
                        string val = body[(arrow + 2)..].Trim();
                        string key = head;
                        int at = head.IndexOf('@');
                        if (at > 0)
                            key = head[..at].Trim(); // 忽略阶位后缀
                        if (key.Length > 0 && val.Length > 0)
                        {
                            RegisterMethod(MethodKeyPrefix + key, val, Level1, "llm");
                            handled = true;
                        }
                    }
                }
                else if (t.StartsWith("MERGEX:", StringComparison.OrdinalIgnoreCase))
                {
                    string body = t["MERGEX:".Length..].Trim();
                    int arrow = body.IndexOf("->", StringComparison.Ordinal);
                    if (arrow > 0)
                    {
                        string src = body[..arrow].Trim();
                        string dest = body[(arrow + 2)..].Trim();
                        var keys = src.Split(',', '，').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
                        if (keys.Count >= 2) { MergeEntries(keys[0], keys.Skip(1).ToArray(), dest); handled = true; }
                    }
                }
                else if (t.StartsWith("DELX:", StringComparison.OrdinalIgnoreCase))
                {
                    string body = t["DELX:".Length..].Trim();
                    var parts = body.Split(':');
                    if (parts.Length == 2) { RemoveEntry(parts[0].Trim(), parts[1].Trim()); handled = true; }
                }
                else if (t.StartsWith("EXTEND:", StringComparison.OrdinalIgnoreCase))
                {
                    string body = t["EXTEND:".Length..].Trim();
                    int arrow = body.IndexOf("->", StringComparison.Ordinal);
                    if (arrow > 0)
                    {
                        string head = body[..arrow].Trim();
                        string val = body[(arrow + 2)..].Trim();
                        var parts = head.Split(':');
                        if (parts.Length == 2)
                        {
                            string type = parts[0].Trim();
                            string key = parts[1].Trim();
                            KnowledgeEntry? e = work.FirstOrDefault(x =>
                                string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                            string merged = e != null && !string.IsNullOrWhiteSpace(e.Value)
                                ? e.Value + " " + val
                                : val;
                            AddOrUpdate(type, key, merged.Trim(), Level1, "llm", persist: false);
                            handled = true;
                        }
                    }
                }
                else if (t.StartsWith("CLRWORK", StringComparison.OrdinalIgnoreCase))
                {
                    RebuildWorkFromBackup();
                    handled = true;
                }
                else if (t.StartsWith("SCOREX:", StringComparison.OrdinalIgnoreCase))
                {
                    // SCOREX:type:key:±delta（LLM 评分；超上限封顶，低于下限移除）
                    string body = t["SCOREX:".Length..].Trim();
                    var parts = body.Split(':');
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[2].Trim(), out int delta))
                    {
                        ApplyScore(parts[0].Trim(), parts[1].Trim(), delta);
                        handled = true;
                    }
                }
            }
            catch (Exception) { }
        }
        return handled;
    }

    /// <summary>
    /// 评分引擎：LLM 打分（delta 可正可负，允许"反骨"纠正）。
    /// - 单次幅度钳制：|delta| ≤ MaxDeltaPerScore(30)，防止一次 +1000 顶满或 -1000 一击移除
    /// - 超上限（MaxScore）→ 封顶；低于下限（RemoveScoreThreshold）→ 移出工作表（备份保留）
    /// - 人话数字（如"加一百"）由 LLM 转成情感信息后再打分，插件只收 LLM 给的 delta
    /// </summary>
    public void ApplyScore(string type, string key, int delta)
    {
        if (string.IsNullOrWhiteSpace(key) || delta == 0) return;
        // 单次幅度钳制（物理护栏；提示词也会约束 LLM，这里双保险）
        delta = Math.Clamp(delta, -MaxDeltaPerScore, MaxDeltaPerScore);
        lock (gate)
        {
            KnowledgeEntry? we = work.FirstOrDefault(x =>
                string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));

            if (we == null)
            {
                // 打分目标不存在 → 自动创建（SCOREX 语义评分如「表达」，不依赖条目预存在；
                // 否则打分会静默丢失）。value 存说明，score 从 delta 起。
                AddOrUpdate(type, key, "（语境表达评分）", Level1, "llm", persist: false);
                we = work.FirstOrDefault(x =>
                    string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (we == null)
                    return;
                we.Score = Math.Clamp(delta, -MaxDeltaPerScore, MaxDeltaPerScore);
                we.Bad = 0;
                we.Good = 0;
            }

            // 封顶：超上限就锁在上限，后续变化仍按 delta 走（但不超过上限）
            int newScore = we.Score + delta;
            if (newScore > MaxScore)
                newScore = MaxScore;
            we.Score = newScore;

            // 极端负分 → 移除工作表（备份保留，负分真实记录参与排名）
            if (we.Score <= RemoveScoreThreshold)
            {
                work.Remove(we);
                // 备份表该项记负分但不删（历史档案）
                KnowledgeEntry? be = backup.FirstOrDefault(x =>
                    string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (be != null)
                {
                    be.Score = we.Score; // 真实负分记录
                    be.Bad++;
                }
            }

            SortEntries(work);
            SortEntries(backup);
            MarkDirty(); // 标脏批量落盘（SCOREX 在 speak Content 链路，避免阻塞合成）
        }
    }

    /// <summary>合并 O/X 与自然评价：O→+2，X→-1；自然评价由 LLM 先转成 delta 再 ApplyScore。</summary>
    public void ApplyHumanFeedback(string type, string key, bool good)
    {
        ApplyScore(type, key, good ? 2 : -1);
    }

    // ==== 删除 / 合并 / 恢复（只改工作表）====

    public void RemoveEntry(string type, string key)
    {
        lock (gate)
        {
            // 工作表删；备份表保留（只作为历史，从工作剔除）
            work.RemoveAll(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            // 标记备份表该项为已淘汰（可选：给 score 减分但保留记录）
            KnowledgeEntry? be = backup.FirstOrDefault(x =>
                string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (be != null) { be.Score -= 1; } // 历史记录降权但保留
            SortEntries(work);
            SortEntries(backup);
            MarkDirty();
        }
    }

    void MergeEntries(string keep, string[] removeKeys, string newKey)
    {
        lock (gate)
        {
            // 工作表合并
            KnowledgeEntry? target = work.FirstOrDefault(x => string.Equals(x.Key, keep, StringComparison.OrdinalIgnoreCase))
                ?? work.FirstOrDefault(x => string.Equals(x.Key, newKey, StringComparison.OrdinalIgnoreCase));
            int totalCount = 0, totalGood = 0, totalBad = 0;
            foreach (string k in removeKeys)
            {
                KnowledgeEntry? e = work.FirstOrDefault(x => string.Equals(x.Key, k, StringComparison.OrdinalIgnoreCase));
                if (e != null) { totalCount += e.Count; totalGood += e.Good; totalBad += e.Bad; work.Remove(e); }
            }
            if (target != null)
            {
                target.Key = string.IsNullOrWhiteSpace(newKey) ? target.Key : newKey;
                target.Count += totalCount; target.Good += totalGood; target.Bad += totalBad;
                RecomputeScore(target);
            }
            else
            {
                work.Add(new KnowledgeEntry
                {
                    Type = TypeSynonym,
                    Key = string.IsNullOrWhiteSpace(newKey) ? keep : newKey,
                    Count = totalCount, Good = totalGood, Bad = totalBad,
                    Score = totalGood * 2 - totalBad + totalCount,
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                });
            }
            SortEntries(work);
            MarkDirty();
        }
    }

    /// <summary>工作表异常/清空时，从备份表重建（备份不动）。</summary>
    public void RebuildWorkFromBackup()
    {
        lock (gate)
        {
            work = backup.Select(Clone).ToList();
            SortEntries(work);
            MarkDirty();
        }
    }

    // ==== 展示 / 注入 ====

    public IReadOnlyList<KnowledgeEntry> WorkEntries
    {
        get { lock (gate) { SortEntries(work); return work.ToArray(); } }
    }

    public IReadOnlyList<KnowledgeEntry> BackupEntries
    {
        get { lock (gate) { SortEntries(backup); return backup.ToArray(); } }
    }

    /// <summary>偏好/模式格式化供 Prompt 注入（高分高频优先）。</summary>
    public string FormatPreferencesForInjection(int minScore = 1, int maxItems = 10)
    {
        lock (gate)
        {
            var items = work.Where(x => x.Type != TypeSynonym && x.Score >= minScore)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Count)
                .Take(maxItems).ToList();
            if (items.Count == 0) return "";
            return string.Join("\n", items.Select(x =>
                $"  [{x.Key}] {x.Value}（好评{x.Good}/差评{x.Bad}，用{x.Count}次）"));
        }
    }

    /// <summary>全表精简格式化（供 LLM 清理评价，含频率/评分）。</summary>
    public string FormatForCleanup()
    {
        lock (gate)
        {
            var lines = new List<string>();
            foreach (var e in work.OrderByDescending(x => x.Score))
                lines.Add(e.Display);
            return lines.Count == 0 ? "（空）" : string.Join("\n", lines);
        }
    }

    /// <summary>强度配方键前缀（LLM 可自创档位，如 __tier_爆裂）。</summary>
    public const string TierRecipeKeyPrefix = "__tier_";

    /// <summary>方法键前缀（LLM 可注册/自创方法，如 __method_深夜低语）。</summary>
    public const string MethodKeyPrefix = "__method_";

    /// <summary>
    /// 阶位推断（E3 四阶砍一阶：恒返回 Level1；签名保留兼容）。
    /// </summary>
    static int InferLevel(string value)
    {
        return Level1;
    }

    /// <summary>音色配方键前缀（LLM 可自创音色，如 __timbre_梦呓）。</summary>
    public const string TimbreRecipeKeyPrefix = "__timbre_";

    /// <summary>档位词数值键前缀（AI 可改写档位词数值，如 __word_speed_快）。</summary>
    public const string WordPresetKeyPrefix = "__word_";

    /// <summary>全部 __ 前缀配方条目格式化（供 Prompt 注入展示）：档位/音色/档位词 → 值。</summary>
    public string FormatPresetsForInjection()
    {
        lock (gate)
        {
            var items = work.Where(x => x.Type == TypePreference &&
                                        (x.Key.StartsWith(TierRecipeKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
                                         x.Key.StartsWith(TimbreRecipeKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
                                         x.Key.StartsWith(WordPresetKeyPrefix, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Key)
                .ToList();
            if (items.Count == 0)
                return "";
            var lines = new List<string>();
            foreach (var e in items)
            {
                string key = e.Key;
                string label;
                if (key.StartsWith(TierRecipeKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    label = $"强度档[{key[TierRecipeKeyPrefix.Length..]}]";
                else if (key.StartsWith(TimbreRecipeKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    label = $"音色[{key[TimbreRecipeKeyPrefix.Length..]}]";
                else
                    label = $"档位词[{key[WordPresetKeyPrefix.Length..]}]";
                lines.Add($"  {label} {e.Value}（好评{e.Good}/差评{e.Bad}，用{e.Count}次）");
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>当前全部强度配方（__tier_* 条目），供 Prompt 注入展示：档位 → 配方。</summary>
    public string FormatTierRecipesForInjection()
    {
        lock (gate)
        {
            var items = work.Where(x => x.Type == TypePreference &&
                                        x.Key.StartsWith(TierRecipeKeyPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Key)
                .ToList();
            if (items.Count == 0)
                return "";
            var lines = new List<string>();
            foreach (var e in items)
            {
                string tier = e.Key[TierRecipeKeyPrefix.Length..];
                lines.Add($"  [{tier}] {e.Value}（好评{e.Good}/差评{e.Bad}，用{e.Count}次）");
            }
            return string.Join("\n", lines);
        }
    }

    // ==== 内部 ====

    static void RecomputeScore(KnowledgeEntry e)
    {
        // 同义词：频率主导（不人评）；其余（含各阶方法）：统一价值体系——人评权重高 + 频率辅助
        if (string.Equals(e.Type, TypeSynonym, StringComparison.OrdinalIgnoreCase))
            e.Score = e.Count; // 同义词靠调用频率判断重要程度
        else
            e.Score = e.Good * 2 - e.Bad + Math.Min(e.Count, 5); // 人评为主，频率辅助（封顶5防刷）
    }

    static void SortEntries(List<KnowledgeEntry> list) =>
        list.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : b.Count.CompareTo(a.Count));

    static KnowledgeEntry Clone(KnowledgeEntry e) => new()
    {
        Type = e.Type, Key = e.Key, Value = e.Value,
        Count = e.Count, Good = e.Good, Bad = e.Bad,
        Score = e.Score, Source = e.Source, Timestamp = e.Timestamp,
        Level = e.Level,
    };

    void Load()
    {
        try
        {
            if (File.Exists(workPath))
                work = JsonSerializer.Deserialize<List<KnowledgeEntry>>(File.ReadAllText(workPath)) ?? new List<KnowledgeEntry>();
            if (File.Exists(backupPath))
                backup = JsonSerializer.Deserialize<List<KnowledgeEntry>>(File.ReadAllText(backupPath)) ?? new List<KnowledgeEntry>();

            if (work.Count == 0 && backup.Count > 0)
                work = backup.Select(Clone).ToList();
            if (backup.Count == 0 && work.Count > 0)
                backup = work.Select(Clone).ToList();

            SortEntries(work);
            SortEntries(backup);
        }
        catch (Exception)
        {
            work = new List<KnowledgeEntry>();
            backup = new List<KnowledgeEntry>();
        }
    }

    /// <summary>标记数据已变（查询计数等低危变化不立即落盘，批量 flush 时写）。</summary>
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

    /// <summary>立即落盘（写入/删除/评分等需要持久化的操作调用）。</summary>
    void Save()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(workPath))
            {
                string dir = Path.GetDirectoryName(workPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                WriteAtomic(workPath, JsonSerializer.Serialize(work));
            }
            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                string dir = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                WriteAtomic(backupPath, JsonSerializer.Serialize(backup));
            }
        }
        catch (Exception) { }
    }

    /// <summary>原子写：先写临时文件再替换，避免写一半崩溃损坏 JSON。</summary>
    static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
