using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// WhisperX 常驻对齐进程（E2 对齐引擎）。
/// - 启动时预加载 whisper 模型 + 强制对齐器（一次 ~10-20s），之后每句通过 stdin/stdout 复用（~1-3s）。
/// - 共享引用计数（static）：与 CosyVoice 服务同一套多桌宠共存管理——
///   所有角色实例共享一个常驻进程，对接数 +1；最后一个释放（计数归零）才停进程。
/// - 外部/失败兜底：进程启动失败或崩溃 → 调用方回退"分摊"（按字数均分时长），功能不丢。
/// - 超时保护：对齐一句最长 8s、启动 READY 最长 90s——绝不拖慢说话（对齐只是给 DSP 提供字边界）。
/// </summary>
sealed class WhisperXDaemon : IDisposable
{
    // ===== 共享注册表（static，跨角色实例共享）=====
    static readonly object daemonGate = new();
    static Process? sharedDaemon;
    static int sharedUsers;
    static readonly object sharedIoGate = new(); // 序列化对齐请求（单进程单请求）

    readonly string pythonPath;
    readonly string? modelName;
    bool released;

    /// <summary>对齐一句的最长等待（正常 ~1-3s；超时视为 daemon 卡死，杀进程回退——不让说话等太久）。</summary>
    const int AlignTimeoutMs = 8_000;
    /// <summary>daemon 启动（模型加载）最长等待。</summary>
    const int ReadyTimeoutMs = 90_000;

    public WhisperXDaemon(string pythonPath, string? modelName = null)
    {
        this.pythonPath = pythonPath;
        this.modelName = modelName;
    }

    /// <summary>对接共享常驻进程（启动或复用），返回 true 表示本实例参与共享。</summary>
    bool Acquire()
    {
        lock (daemonGate)
        {
            if (sharedDaemon == null || sharedDaemon.HasExited)
            {
                try
                {
                    sharedDaemon = SpawnProcess();
                }
                catch (Exception)
                {
                    sharedDaemon = null;
                    return false;
                }
            }
            sharedUsers++;
            return true;
        }
    }

    /// <summary>释放对接（计数归零时停进程）。</summary>
    void Release()
    {
        Process? toKill = null;
        lock (daemonGate)
        {
            if (sharedUsers > 0)
                sharedUsers--;
            if (sharedUsers <= 0 && sharedDaemon != null)
            {
                toKill = sharedDaemon;
                sharedDaemon = null;
            }
        }
        if (toKill != null)
        {
            try
            {
                if (toKill.HasExited == false)
                {
                    try
                    {
                        toKill.StandardInput.WriteLine("__QUIT__");
                        toKill.StandardInput.Flush();
                    }
                    catch { }
                    if (!toKill.WaitForExit(3000))
                        toKill.Kill(true);
                }
            }
            catch (Exception) { }
            finally
            {
                try { toKill.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// 对齐一句：传 wav 路径，返回逐字时间戳（CharBoundary 列表）。
    /// 失败返回 null（调用方回退分摊）。
    /// </summary>
    public List<CharBoundary>? Align(string wavPath)
    {
        if (!Acquire())
            return null;
        try
        {
            Process? proc;
            lock (daemonGate)
                proc = sharedDaemon;
            if (proc == null || proc.HasExited)
                return null;

            lock (sharedIoGate)
            {
                // 写请求行（wav 路径）
                proc.StandardInput.WriteLine(wavPath.Replace('\\', '/'));
                proc.StandardInput.Flush();
                // 读响应行（JSON 或空行）；超时=daemon 卡死 → 杀进程回退（下次重新拉起）
                string? line = ReadLineWithTimeout(proc.StandardOutput, AlignTimeoutMs);
                if (line == null)
                {
                    KillSharedDaemon();
                    return null;
                }
                if (string.IsNullOrWhiteSpace(line))
                    return null;
                return ParseBoundaries(line);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 daemon 输出：{"chars":[{"ch":"你","start":0.12,"end":0.28},...]}
    /// 由 WhisperX 词级时间戳聚合到中文逐字（或按词，日语/英语词级）。
    /// </summary>
    static List<CharBoundary>? ParseBoundaries(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("chars", out JsonElement chars) ||
                chars.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<CharBoundary>();
            foreach (JsonElement e in chars.EnumerateArray())
            {
                string? ch = e.TryGetProperty("ch", out var c) ? c.GetString() : null;
                double start = e.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
                double end = e.TryGetProperty("end", out var en) ? en.GetDouble() : start;
                if (string.IsNullOrEmpty(ch))
                    continue;
                list.Add(new CharBoundary { Char = ch, StartSec = start, EndSec = end });
            }
            return list.Count > 0 ? list : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    Process SpawnProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        // 国内网络：HuggingFace 直连不稳，走 hf-mirror.com 镜像下载 whisperx 模型
        psi.Environment["HF_ENDPOINT"] = "https://hf-mirror.com";

        string model = string.IsNullOrWhiteSpace(modelName) ? "small" : modelName.Trim();
        // 常驻脚本：启动时加载模型（whisper + 强制对齐器），循环读 wav 路径 → 对齐 → 输出 JSON 行
        string script = $$"""
            # -*- coding: utf-8 -*-
            import sys, json
            sys.stdout.reconfigure(encoding='utf-8', line_buffering=True)
            sys.stderr.reconfigure(encoding='utf-8', line_buffering=True)
            try:
                import whisperx, torch
                device = "cuda" if torch.cuda.is_available() else "cpu"
                compute = "float16" if device == "cuda" else "int8"
                model = whisperx.load_model("{{model}}", device=device, compute_type=compute)
                align_model, metadata = whisperx.load_align_model(language_code="zh", device=device)
                print("__READY__", flush=True)
            except Exception as e:
                print(json.dumps({"fatal": repr(e)}, ensure_ascii=False), flush=True)
                sys.exit(0)

            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                if line == "__QUIT__":
                    break
                try:
                    audio = whisperx.load_audio(line)
                    result = model.transcribe(audio, batch_size=8)
                    aligned = whisperx.align(result["segments"], align_model, metadata, audio, device,
                        return_char_alignments=True)
                    out = []
                    for seg in aligned.get("segments", []):
                        for c in seg.get("chars", []):
                            out.append({"ch": c.get("char", ""), "start": round(c.get("start", 0), 4), "end": round(c.get("end", 0), 4)})
                    print(json.dumps({"chars": out}, ensure_ascii=False), flush=True)
                except Exception as e:
                    print(json.dumps({"error": repr(e)}, ensure_ascii=False), flush=True)
            """;

        string scriptPath = Path.Combine(Path.GetTempPath(), $"etts_whisperx_daemon_{Guid.NewGuid():N}.py");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        psi.ArgumentList.Add(scriptPath);

        var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("WhisperX 常驻进程启动失败");

        // 等 READY（模型加载完成）或致命错误；超时=模型加载卡死 → 杀进程并抛异常（调用方回退分摊）
        string? first = ReadLineWithTimeout(proc.StandardOutput, ReadyTimeoutMs);
        if (!string.Equals(first, "__READY__", StringComparison.Ordinal))
        {
            try { proc.Kill(true); } catch { }
            proc.Dispose();
            throw new InvalidOperationException($"WhisperX 常驻进程未就绪: {first ?? "(无输出/超时)"}");
        }
        return proc;
    }

    /// <summary>带超时读一行（避免 daemon 卡死时无限阻塞调用方 → 卡死整条 speak 链）。</summary>
    static string? ReadLineWithTimeout(StreamReader reader, int timeoutMs)
    {
        var t = Task.Run(() => reader.ReadLine());
        if (t.Wait(TimeSpan.FromMilliseconds(timeoutMs)))
            return t.Result;
        return null;
    }

    /// <summary>杀掉共享 daemon（卡死/无响应时），下次 Acquire 重新拉起。</summary>
    void KillSharedDaemon()
    {
        Process? toKill = null;
        lock (daemonGate)
        {
            if (sharedDaemon != null && !sharedDaemon.HasExited)
            {
                toKill = sharedDaemon;
                sharedDaemon = null;
            }
        }
        if (toKill != null)
        {
            try { toKill.Kill(true); } catch { }
            try { toKill.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (released)
            return;
        released = true;
        try { Release(); } catch { }
    }
}
