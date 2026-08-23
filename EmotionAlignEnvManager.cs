using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Platform;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 对齐环境管理器（E2，插件级）：检测 Python / whisperx，pip 安装，模型下载，状态查询。
/// 供 WebUI 调用（GUI 按钮 → 本管理器 → 后台进程执行）。
/// 设计原则：绝不破坏任何合成环境；WhisperX 用插件专属独立 venv（Cache\EmotionTTS\whisperx-venv），
/// 不污染/不依赖其他环境；numpy 锁 [2.1, 2.4) 兼容 numba+whisperx。
/// </summary>
public sealed class EmotionAlignEnvManager
{
    /// <summary>WhisperX 独立 venv 相对 Storage 的路径（插件专属，创建后 ResolvePython 自动优先）。</summary>
    public const string WhisperxVenvRelative = @"Cache\EmotionTTS\whisperx-venv";
    public enum AlignBackend
    {
        WhisperX,       // 中日英，词级（E2 默认）
        Proportional,   // 零依赖兜底
    }

    public sealed class EnvStatus
    {
        public string PythonPath { get; set; } = "";
        public string PythonVersion { get; set; } = "";
        public bool CudaAvailable { get; set; }
        public string GpuName { get; set; } = "";
        public bool WhisperXInstalled { get; set; }
        public string? WhisperXVersion { get; set; }
        public bool NumpyCompatible { get; set; } = true;
        public string? NumpyVersion { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>进度事件（GUI 订阅显示）。</summary>
    public event Action<string>? Progress;

    /// <summary>当前是否在安装/下载中。</summary>
    public bool IsBusy { get; private set; }

    // ==== Python 探测 ====

    /// <summary>插件专属 WhisperX venv 的 python.exe（存在才返回，否则空串）。</summary>
    public static string GetWhisperxVenvPython()
    {
        try
        {
            string p = Path.Combine(AlifePath.StorageFolderPath, WhisperxVenvRelative, "Scripts", "python.exe");
            return File.Exists(p) ? p : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// 自动探测可用于建 venv 的基础 Python（完整安装版）——开箱即用，无需手动配置：
    /// ① Windows py launcher（3.13→3.12→3.11→3.10→任意）→ ② PATH 的 python → ③ 常见安装路径。
    /// 返回第一个可用的 python.exe；找不到返回 null。
    /// </summary>
    public static string? FindBasePython()
    {
        // ① py launcher（Windows 官方启动器，能指定主版本）
        foreach (string tag in new[] { "-3.13", "-3.12", "-3.11", "-3.10", "-3" })
        {
            string? p = ProbeExecutable("py", $"{tag} -c \"import sys; print(sys.executable)\"");
            if (!string.IsNullOrEmpty(p))
                return p;
        }

        // ② PATH 的 python
        string? pathPy = ProbeExecutable("python", "-c \"import sys; print(sys.executable)\"");
        if (!string.IsNullOrEmpty(pathPy))
            return pathPy;

        // ③ 常见安装路径（%LOCALAPPDATA%\Programs\Python\Python3x、C:\Python3x）
        try
        {
            var roots = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                roots.Add(Path.Combine(local, "Programs", "Python"));
            roots.Add(@"C:\Python");
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;
                foreach (string dir in Directory.EnumerateDirectories(root, "Python3*", SearchOption.TopDirectoryOnly))
                {
                    string exe = Path.Combine(dir, "python.exe");
                    if (File.Exists(exe))
                        return exe;
                }
            }
        }
        catch (Exception) { }

        return null;
    }

    /// <summary>执行一次简单探测命令，返回输出的可执行文件路径（有效才返回）。</summary>
    static string? ProbeExecutable(string fileName, string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            proc.Start();
            string outPath = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10_000);
            if (!string.IsNullOrWhiteSpace(outPath) && File.Exists(outPath))
                return outPath;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// 解析对齐用 Python 路径：插件 WhisperX venv（创建后自动优先）→ 配置 AlignPythonPath → PATH。
    /// </summary>
    public static string? ResolvePython(EmotionTTSConfig config)
    {
        // ① 插件专属 WhisperX venv（独立环境，最优先）
        string venvPy = GetWhisperxVenvPython();
        if (!string.IsNullOrWhiteSpace(venvPy))
            return venvPy;

        // ② 显式配置
        if (!string.IsNullOrWhiteSpace(config.AlignPythonPath) && File.Exists(config.AlignPythonPath))
            return config.AlignPythonPath;

        // ③ PATH 兜底
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-c \"import sys; print(sys.executable)\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            proc.Start();
            string outPath = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10_000);
            if (!string.IsNullOrWhiteSpace(outPath) && File.Exists(outPath))
                return outPath;
        }
        catch { }

        return null;
    }

    /// <summary>检测环境状态（GUI 打开时调用）。</summary>
    public EnvStatus Probe(EmotionTTSConfig config)
    {
        var status = new EnvStatus();
        string? python = ResolvePython(config);
        if (python == null)
        {
            status.Message = "未找到 Python。请配置 AlignPythonPath 或加入 PATH（WhisperX 环境）。";
            return status;
        }

        status.PythonPath = python;
        ProbePython(python, status);
        return status;
    }

    static void ProbePython(string python, EnvStatus status)
    {
        try
        {
            string probe = """
                import sys, json
                out = {"py": sys.version.split()[0]}
                try:
                    import torch
                    out["cuda"] = torch.cuda.is_available()
                    out["gpu"] = torch.cuda.get_device_name(0) if torch.cuda.is_available() else ""
                except Exception: out["cuda"] = False
                try:
                    import numpy
                    out["numpy"] = numpy.__version__
                except Exception: pass
                try:
                    import whisperx
                    try:
                        from importlib.metadata import version as _v
                        out["whisperx"] = _v("whisperx")
                    except Exception:
                        out["whisperx"] = getattr(whisperx, "__version__", "?")
                except Exception: pass
                print(json.dumps(out))
                """;
            string scriptPath = Path.Combine(Path.GetTempPath(), $"etts_probe_{Guid.NewGuid():N}.py");
            File.WriteAllText(scriptPath, probe, new UTF8Encoding(false));
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(scriptPath);
                using var proc = Process.Start(psi);
                if (proc == null) return;
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(30_000);

                string line = stdout.Trim().Split('\n').LastOrDefault(l => l.TrimStart().StartsWith("{"));
                if (string.IsNullOrWhiteSpace(line)) return;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("py", out var py)) status.PythonVersion = py.GetString() ?? "";
                if (root.TryGetProperty("cuda", out var cuda)) status.CudaAvailable = cuda.GetBoolean();
                if (root.TryGetProperty("gpu", out var gpu)) status.GpuName = gpu.GetString() ?? "";
                if (root.TryGetProperty("numpy", out var np))
                {
                    status.NumpyVersion = np.GetString();
                    status.NumpyCompatible = IsNumpyCompatible(status.NumpyVersion);
                }
                if (root.TryGetProperty("whisperx", out var wx)) { status.WhisperXInstalled = true; status.WhisperXVersion = wx.GetString(); }
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }
        catch (Exception) { }
    }

    static bool IsNumpyCompatible(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return true;
        return Version.TryParse(version.TrimStart('v'), out Version? v) && v >= new Version(2, 1) && v < new Version(2, 4);
    }

    // ==== 安装 / 修复 ====

    /// <summary>
    /// 创建 WhisperX 独立 venv（插件专属，Cache\EmotionTTS\whisperx-venv）：
    /// ① 基础 Python 建 venv → ② 升级 pip → ③ 装 CUDA torch（torch 2.8 默认 wheel 即 cu128，适配 RTX 50 系）
    /// → ④ 装 whisperx → ⑤ numpy 锁 [2.1, 2.4)（numba 兼容）。
    /// 创建完成后 ResolvePython 自动优先该 venv，无需再填 AlignPythonPath。
    /// </summary>
    public async Task<bool> CreateWhisperxVenvAsync(EmotionTTSConfig config)
    {
        // 基础 Python：显式配置优先（高级），否则自动探测（开箱即用）
        string basePy = (!string.IsNullOrWhiteSpace(config.AlignPythonPath) && File.Exists(config.AlignPythonPath))
            ? config.AlignPythonPath
            : (FindBasePython() ?? "");
        if (string.IsNullOrWhiteSpace(basePy))
        {
            Progress?.Invoke("未找到可用 Python 基础环境（自动探测 py launcher / PATH / 常见安装路径均未命中）。请安装 Python 3.11 或在配置文件中设置 AlignPythonPath。");
            return false;
        }
        Progress?.Invoke($"[base] 使用基础 Python：{basePy}");
        if (IsBusy)
        {
            Progress?.Invoke("已有任务进行中，请稍候。");
            return false;
        }

        IsBusy = true;
        try
        {
            string venvDir = Path.Combine(AlifePath.StorageFolderPath, WhisperxVenvRelative);
            string venvPy = Path.Combine(venvDir, "Scripts", "python.exe");

            // ① venv
            if (!File.Exists(venvPy))
            {
                Progress?.Invoke($"[venv] 创建独立环境 {venvDir} ...");
                if (!await RunModuleAsync(basePy, new[] { "-m", "venv", venvDir }, "venv"))
                {
                    Progress?.Invoke("[venv] 创建失败（基础 Python 需为完整安装版，能执行 -m venv）。");
                    return false;
                }
            }
            else
            {
                Progress?.Invoke("[venv] 独立环境已存在，跳过创建。");
            }

            // ② pip 升级
            await RunModuleAsync(venvPy, new[]
            {
                "-m", "pip", "install", "--upgrade", "pip",
                "-i", "https://pypi.tuna.tsinghua.edu.cn/simple",
            }, "pip");

            // ③ torch（CUDA 版；默认 PyPI wheel 即 cu128，失败回退 PyTorch 官方源）
            Progress?.Invoke("[torch] 安装 CUDA 版 torch（约 2.5GB 下载，请耐心等待）...");
            bool torchOk = await RunModuleAsync(venvPy,
                new[] { "-m", "pip", "install", "torch", "torchaudio" }, "torch");
            if (!torchOk)
            {
                Progress?.Invoke("[torch] 默认源失败，改用 PyTorch 官方源重试（cu128）...");
                torchOk = await RunModuleAsync(venvPy, new[]
                {
                    "-m", "pip", "install", "torch", "torchaudio",
                    "--index-url", "https://download.pytorch.org/whl/cu128",
                }, "torch");
            }
            if (!torchOk)
                Progress?.Invoke("[torch] CUDA torch 安装失败，可稍后重试（WhisperX 需要 torch 才能运行）。");

            // ④ whisperx
            Progress?.Invoke("[whisperx] 安装 whisperx（含 faster-whisper/numba/transformers 等依赖）...");
            bool wxOk = await RunModuleAsync(venvPy, new[]
            {
                "-m", "pip", "install", "-U", "whisperx",
                "-i", "https://pypi.tuna.tsinghua.edu.cn/simple",
            }, "whisperx");
            if (!wxOk)
            {
                Progress?.Invoke("[whisperx] 清华镜像失败，改用官方 PyPI 重试...");
                wxOk = await RunModuleAsync(venvPy,
                    new[] { "-m", "pip", "install", "-U", "whisperx" }, "whisperx");
            }

            // ⑤ numpy 兼容区间（numba 需要 [2.1, 2.4)）
            await RunModuleAsync(venvPy,
                new[] { "-m", "pip", "install", "numpy>=2.1,<2.4" }, "numpy");

            Progress?.Invoke(wxOk
                ? $"✅ WhisperX 独立环境就绪：{venvPy}\n对齐 Python 已自动优先使用该环境（无需再填 AlignPythonPath）。可点「检测环境」验证。"
                : "whisperx 安装失败，请查看上方日志后重试。");
            return wxOk;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>通用进程执行：以参数列表方式调用 python -m ...，进度/错误回 UI。失败返回 false。</summary>
    async Task<bool> RunModuleAsync(string python, IReadOnlyList<string> args, string label)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Progress?.Invoke($"[{label}] 进程启动失败。");
                return false;
            }

            var outputTask = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.Contains("Successfully installed") || line.Contains("Downloading") ||
                        line.Contains("Installing collected") || line.Contains("ERROR") ||
                        line.Contains("Requirement already satisfied"))
                        Progress?.Invoke($"[{label}] {line.Trim()}");
                }
            });
            var errTask = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardError.ReadLine()) != null)
                {
                    if (line.Contains("ERROR") || line.Contains("error"))
                        Progress?.Invoke($"[{label}] {line.Trim()}");
                }
            });

            await Task.WhenAll(outputTask, errTask);
            bool ok = proc.ExitCode == 0;
            Progress?.Invoke(ok ? $"[{label}] 完成。" : $"[{label}] 失败（exit={proc.ExitCode}）。");
            return ok;
        }
        catch (Exception ex)
        {
            Progress?.Invoke($"[{label}] 异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>安装对齐后端（whisperx）。后台执行，进度事件。</summary>
    public async Task<bool> InstallAsync(EmotionTTSConfig config, AlignBackend backend)
    {
        string? python = ResolvePython(config);
        if (python == null)
        {
            Progress?.Invoke("未找到 Python，无法安装。");
            return false;
        }

        if (IsBusy)
        {
            Progress?.Invoke("已有安装任务进行中，请稍候。");
            return false;
        }

        IsBusy = true;
        try
        {
            switch (backend)
            {
                case AlignBackend.WhisperX:
                    return await RunPipAsync(python, "install -U whisperx", "whisperx");
                default:
                    Progress?.Invoke("Proportional 无需安装。");
                    return true;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>修复 numpy 版本（兼容 numba + whisperx 交集 [2.1, 2.4)）。</summary>
    public async Task<bool> FixNumpyAsync(EmotionTTSConfig config)
    {
        string? python = ResolvePython(config);
        if (python == null) return false;
        return await RunPipAsync(python, "install \"numpy>=2.1,<2.4\"", "numpy");
    }

    async Task<bool> RunPipAsync(string python, string args, string label)
    {
        Progress?.Invoke($"[{label}] 开始安装：pip {args}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("pip");
            psi.ArgumentList.Add("install");
            foreach (string a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Progress?.Invoke($"[{label}] 进程启动失败。");
                return false;
            }

            var outputTask = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.Contains("Successfully installed") || line.Contains("Downloading") ||
                        line.Contains("Installing collected") || line.Contains("ERROR") ||
                        line.Contains("Requirement already satisfied"))
                        Progress?.Invoke($"[{label}] {line.Trim()}");
                }
            });
            var errTask = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardError.ReadLine()) != null)
                {
                    if (line.Contains("ERROR") || line.Contains("error"))
                        Progress?.Invoke($"[{label}] {line.Trim()}");
                }
            });

            await Task.WhenAll(outputTask, errTask);
            bool ok = proc.ExitCode == 0;
            Progress?.Invoke(ok ? $"[{label}] 安装完成。" : $"[{label}] 安装失败（exit={proc.ExitCode}）。");
            return ok;
        }
        catch (Exception ex)
        {
            Progress?.Invoke($"[{label}] 安装异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>下载 whisperx 模型（small 档，供首次对齐预热）。</summary>
    public async Task<bool> PreloadWhisperXModelAsync(EmotionTTSConfig config)
    {
        string? python = ResolvePython(config);
        if (python == null) return false;

        Progress?.Invoke("[whisperx] 开始下载 small 模型（约 500MB，首次对齐需要）...");
        string script = """
            import sys
            try:
                import whisperx, torch
                device = "cuda" if torch.cuda.is_available() else "cpu"
                _ = whisperx.load_model("small", device=device,
                    compute_type="float16" if device == "cuda" else "int8")
                print("MODEL_READY")
            except Exception as e:
                print("MODEL_FAIL:", repr(e))
                sys.exit(1)
            """;
        string scriptPath = Path.Combine(Path.GetTempPath(), $"etts_model_{Guid.NewGuid():N}.py");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(scriptPath);
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            string stdout = proc.StandardOutput.ReadToEnd();
            await proc.WaitForExitAsync();
            bool ok = proc.ExitCode == 0 && stdout.Contains("MODEL_READY");
            Progress?.Invoke(ok ? "[whisperx] 模型下载完成，可开始 GPU 对齐。" : "[whisperx] 模型下载失败。");
            return ok;
        }
        catch (Exception ex)
        {
            Progress?.Invoke($"[whisperx] 模型下载异常：{ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }
}
