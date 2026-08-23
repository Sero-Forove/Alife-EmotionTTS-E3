using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 一个字的时间边界（秒，相对于音频开头）。
/// </summary>
public sealed class CharBoundary
{
    public string Char { get; set; } = "";
    public double StartSec { get; set; }
    public double EndSec { get; set; }
}

/// <summary>
/// 对齐抽象层：把"文本 + 整句 wav"对齐为逐字时间边界。
/// 三级策略：
///  - ForcedAlignment（WhisperX，中日英，GPU）——最准（±10~30ms）
///  - Fallback 字数分摊——零依赖兜底（±100ms），保证永远可用
///  - AlignCache——同文本+同 ref 缓存对齐结果，重复说话零成本
/// 对齐失败自动回退分摊，不报废整句。
/// </summary>
public sealed class EmotionAligner : IDisposable
{
    /// <summary>对齐引擎类型。</summary>
    public enum AlignEngine
    {
        /// <summary>纯字数分摊（零依赖兜底）。</summary>
        Proportional,
        /// <summary>WhisperX（wav2vec2，中日英，GPU）——E2 默认。</summary>
        WhisperX,
        /// <summary>自动：有 Python/模型用 WhisperX，否则分摊。</summary>
        Auto,
    }

    public AlignEngine Engine { get; set; } = AlignEngine.WhisperX;
    public string PythonPath { get; set; } = "";
    public bool EnableCache { get; set; } = true;
    /// <summary>是否启用 WhisperX 常驻进程（共享引用计数，跟随插件生命周期）。关闭时回退分摊。默认开启。</summary>
    public bool EnableWhisperxDaemon { get; set; } = true;
    /// <summary>WhisperX 模型名（small/medium/large-v3 等）。</summary>
    public string? WhisperxModel { get; set; } = "small";
    /// <summary>WhisperX 常驻进程（共享引用计数；本实例持有一份对接）。</summary>
    WhisperXDaemon? whisperxDaemon;

    readonly Dictionary<string, List<CharBoundary>> cache = new(StringComparer.Ordinal);
    readonly object gate = new();

    /// <summary>
    /// 对齐：text + wavPath → 逐字边界。
    /// 内部自动：缓存命中→返回；对齐失败→回退分摊；结果写缓存。
    /// </summary>
    public List<CharBoundary> Align(string text, string wavPath)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            return ProportionalSplit(text, wavPath);

        string cacheKey = ComputeCacheKey(text, wavPath);
        if (EnableCache)
        {
            lock (gate)
            {
                if (cache.TryGetValue(cacheKey, out List<CharBoundary>? hit))
                    return hit;
            }
        }

        List<CharBoundary> result;
        try
        {
            // 对齐总预算 8s：无论 WhisperX daemon / 冷启动多慢（GPU 争抢、显存不足、
            // 模型加载卡），超预算直接回退分摊——对齐只是给 DSP 提供字边界，绝不能拖慢说话。
            var alignTask = Task.Run(() => Engine switch
            {
                AlignEngine.WhisperX => RunWhisperX(text, wavPath),
                AlignEngine.Auto => RunWhisperX(text, wavPath),
                _ => ProportionalSplit(text, wavPath),
            });
            if (alignTask.Wait(TimeSpan.FromSeconds(8)))
            {
                result = alignTask.Result ?? ProportionalSplit(text, wavPath);
            }
            else
            {
                // 超预算：后台引擎继续跑（无害，进程会自行结束/被杀），此处立即分摊，说话不卡
                result = ProportionalSplit(text, wavPath);
            }
        }
        catch (Exception)
        {
            // 对齐失败 → 回退分摊，不报废
            result = ProportionalSplit(text, wavPath);
        }

        if (result == null || result.Count == 0)
            result = ProportionalSplit(text, wavPath);

        if (EnableCache)
        {
            lock (gate)
                cache[cacheKey] = result;
        }

        return result;
    }

    /// <summary>清缓存（配置变更/换音色时）。</summary>
    public void ClearCache()
    {
        lock (gate)
            cache.Clear();
    }

    /// <summary>释放 WhisperX 常驻进程对接（共享计数递减；最后一个释放时停进程）。</summary>
    public void Dispose()
    {
        try
        {
            whisperxDaemon?.Dispose();
            whisperxDaemon = null;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 预热 WhisperX 常驻进程（角色启动时后台调用）：立即触发模型加载（~10-20s），
    /// 让首次说话时已就绪。异步 fire-and-forget，不阻塞启动；失败静默（首次使用时再兜底）。
    /// </summary>
    public void WarmUpDaemon()
    {
        try
        {
            if (!EnableWhisperxDaemon || string.IsNullOrWhiteSpace(PythonPath) || !File.Exists(PythonPath))
                return;
            whisperxDaemon ??= new WhisperXDaemon(PythonPath, WhisperxModel);
            _ = Task.Run(() =>
            {
                try
                {
                    // 触发 Acquire（启动进程 + 等 READY）——预热模型加载
                    string tmpWav = Path.Combine(Path.GetTempPath(), "etts_warmup_empty.wav");
                    if (!File.Exists(tmpWav))
                    {
                        var ms = new MemoryStream();
                        var bw = new BinaryWriter(ms);
                        bw.Write("RIFF"); bw.Write(36 + 1600); bw.Write("WAVE");
                        bw.Write("fmt "); bw.Write(16);
                        bw.Write((short)1); bw.Write((short)1);
                        bw.Write(16000); bw.Write(32000);
                        bw.Write((short)2); bw.Write((short)16);
                        bw.Write("data"); bw.Write(1600);
                        for (int i = 0; i < 800; i++) { bw.Write((short)0); }
                        File.WriteAllBytes(tmpWav, ms.ToArray());
                    }
                    whisperxDaemon!.Align(tmpWav);
                }
                catch (Exception)
                {
                    // 预热失败静默；首次使用时回退分摊
                }
            });
        }
        catch (Exception)
        {
        }
    }

    // ==== 字数分摊（零依赖兜底）====

    static List<CharBoundary> ProportionalSplit(string text, string wavPath)
    {
        var result = new List<CharBoundary>();
        double duration = GetWavDuration(wavPath);
        var units = new List<string>();
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
                continue;
            // 标点并入前一单元（分摊时避免空段）
            if (c is '。' or '！' or '？' or '，' or '、' or ';' or '：' or '!' or '?' or ',' or '.')
            {
                if (units.Count > 0)
                    units[^1] += c;
                else
                    units.Add(c.ToString());
                continue;
            }
            units.Add(c.ToString());
        }

        if (units.Count == 0)
            return result;

        double unitDuration = duration / units.Count;
        for (int i = 0; i < units.Count; i++)
        {
            result.Add(new CharBoundary
            {
                Char = units[i],
                StartSec = i * unitDuration,
                EndSec = (i + 1) * unitDuration,
            });
        }
        return result;
    }

    static double GetWavDuration(string wavPath)
    {
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(wavPath);
            return reader.TotalTime.TotalSeconds;
        }
        catch
        {
            return 1.0;
        }
    }

    // ==== 缓存 key ====

    static string ComputeCacheKey(string text, string wavPath)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes($"{text}|{wavPath}|{new FileInfo(wavPath).Length}"));
        return Convert.ToHexString(hash);
    }

    // ==== 外部引擎（WhisperX，E2 默认）====

    List<CharBoundary> RunAuto(string text, string wavPath)
    {
        if (string.IsNullOrWhiteSpace(PythonPath) || !File.Exists(PythonPath))
            return ProportionalSplit(text, wavPath);

        // WhisperX（daemon 优先，冷启动兜底；失败分摊）
        try
        {
            List<CharBoundary> r = RunWhisperX(text, wavPath);
            if (r.Count > 0)
                return r;
        }
        catch (Exception)
        {
            // fall through
        }

        return ProportionalSplit(text, wavPath);
    }

    /// <summary>
    /// WhisperX：常驻进程优先（复用模型，~1-3s），失败回退每句冷启动；再失败分摊。
    /// 中日英词级；中文做字级聚合。
    /// </summary>
    List<CharBoundary> RunWhisperX(string text, string wavPath)
    {
        if (string.IsNullOrWhiteSpace(PythonPath) || !File.Exists(PythonPath))
            return ProportionalSplit(text, wavPath);

        // 常驻优先（共享引用计数；模型常驻免冷启动）
        if (EnableWhisperxDaemon)
        {
            try
            {
                whisperxDaemon ??= new WhisperXDaemon(PythonPath, WhisperxModel);
                List<CharBoundary>? b = whisperxDaemon.Align(wavPath);
                if (b != null && b.Count > 0)
                    return b;
            }
            catch (Exception)
            {
                // daemon 失败 → 冷启动兜底
            }
        }

        // 冷启动兜底（临时脚本 + 对齐器）
        string script = """
            import sys, json
            try:
                import whisperx
                import torch
            except Exception as e:
                print(json.dumps({"error": str(e)})); sys.exit(0)
            audio = whisperx.load_audio(sys.argv[1])
            model = whisperx.load_model("small", device="cuda" if torch.cuda.is_available() else "cpu", compute_type="float16" if torch.cuda.is_available() else "int8")
            result = model.transcribe(audio, language="auto")
            print(json.dumps(result["segments"], ensure_ascii=False))
            """;

        string scriptPath = Path.Combine(Path.GetTempPath(), $"etts_align_{Guid.NewGuid():N}.py");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = PythonPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(wavPath);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return ProportionalSplit(text, wavPath);

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return ProportionalSplit(text, wavPath);

            // 解析 segments → 字边界（这里做粗聚合，逐字精确聚合在后续版本细化）
            return ParseSegments(stdout, text);
        }
        catch (Exception)
        {
            return ProportionalSplit(text, wavPath);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>把时间边界映射到文本的逐字（处理标点/空白差异）。</summary>
    static List<CharBoundary> MapToText(List<CharBoundary> boundaries, string text)
    {
        // 提取文本有效字符（去空白）
        var chars = new List<char>();
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
                chars.Add(c);
        }

        if (chars.Count == 0)
            return boundaries;

        var result = new List<CharBoundary>();
        for (int i = 0; i < chars.Count; i++)
        {
            int bi = Math.Min(i, boundaries.Count - 1);
            if (bi < 0) break;
            result.Add(new CharBoundary
            {
                Char = chars[i].ToString(),
                StartSec = boundaries[bi].StartSec,
                EndSec = boundaries[bi].EndSec,
            });
        }
        return result;
    }

    static List<CharBoundary> ParseSegments(string json, string text)
    {
        var result = new List<CharBoundary>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (System.Text.Json.JsonElement seg in doc.RootElement.EnumerateArray())
            {
                if (!seg.TryGetProperty("start", out var s) || !seg.TryGetProperty("end", out var e))
                    continue;
                if (!seg.TryGetProperty("text", out var t))
                    continue;
                string segText = t.GetString() ?? "";
                double start = s.GetDouble();
                double end = e.GetDouble();
                // 按字符拆（粗聚合）
                double span = end - start;
                int charCount = Math.Max(1, segText.Length);
                double per = span / charCount;
                int i = 0;
                foreach (char c in segText)
                {
                    if (char.IsWhiteSpace(c))
                        continue;
                    result.Add(new CharBoundary
                    {
                        Char = c.ToString(),
                        StartSec = start + i * per,
                        EndSec = start + (i + 1) * per,
                    });
                    i++;
                }
            }
        }
        catch (Exception)
        {
            return new List<CharBoundary>();
        }
        return result;
    }
}
