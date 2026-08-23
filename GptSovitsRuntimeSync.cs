using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// GPT-SoVITS 运行时管理（E3）：
/// - EnsureReadyAsync：探测端口 → 已开放则对接（验证 api_v2 兼容）→ 未开放则自启 api_v2.py 并等待端口。
/// - 权重同步已移除：E3 权重由 tts_infer.yaml 在服务启动时加载，运行期只按段换 ref_audio，
///   不做 set_gpt_weights 热切换（实测热重载会让服务进入坏状态，后续合成 Errno 22）。
/// 自启进程生命周期：由模块持有句柄；销毁时 StopSelf 停止（外部服务永不杀）。
/// </summary>
sealed class GptSovitsRuntimeSync
{
    readonly ILogger logger;
    Process? serviceProcess;
    bool _ready;
    readonly object gate = new();
    readonly SemaphoreSlim readyGate = new(1, 1); // 启动互斥：并行段合成时只启动一次服务

    public GptSovitsRuntimeSync(ILogger logger)
    {
        this.logger = logger;
    }

    public Process? ServiceProcess => serviceProcess;

    /// <summary>确保 GPT-SoVITS api_v2 服务就绪（探测→对接或自启）。并发安全：只启动一次。</summary>
    public async Task<bool> EnsureReadyAsync(HttpClient httpClient, EmotionTTSConfig config,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (_ready)
                return true;
        }
        await readyGate.WaitAsync(cancellationToken);
        try
        {
            lock (gate)
            {
                if (_ready)
                    return true;
            }
            if (string.IsNullOrWhiteSpace(config.InstallPath))
            {
                logger.LogWarning("[EmotionTTS] GPT-SoVITS 未配置 InstallPath");
                return false;
            }

            if (await IsPortOpenAsync(config.Port, cancellationToken))
            {
                // 端口已开放：验证是 api_v2 兼容服务（避免对接无关进程）
                if (await IsV2ApiAsync(httpClient, config.Port, cancellationToken))
                {
                    logger.LogInformation("[EmotionTTS] 对接外部 GPT-SoVITS api_v2 服务（端口 {Port}）", config.Port);
                    lock (gate) { _ready = true; }
                    return true;
                }
                logger.LogWarning("[EmotionTTS] 端口 {Port} 已占用但不是 api_v2，请检查端口", config.Port);
                return false;
            }

            // 自启 api_v2.py
            try
            {
                string cmd = GptSovitsCommandBuilder.BuildStartCommand(config);
                var (fileName, args) = ParseCommand(cmd);
                string? workDir = null;
                string[] argParts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (argParts.Length > 0)
                {
                    string firstArg = argParts[0].Trim('"');
                    if (firstArg.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && File.Exists(firstArg))
                        workDir = Path.GetDirectoryName(Path.GetFullPath(firstArg));
                }
                workDir ??= config.InstallPath.TrimEnd('\\', '/');

                serviceProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = args,
                        WorkingDirectory = workDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    }
                };
                // 环境净化：Alife 宿主进程的 PATH 可能含其他 Python（如 Python310）的 Scripts/DLL 目录，
                // 会污染 GPT-SoVITS 的 Python311 运行时——实测 torchaudio 读到错误 libsndfile 抛
                // "Errno 22 Invalid argument"（手动干净启动正常）。用系统级 PATH（Machine+User）重建，
                // 再补上整合包自身 runtime 目录，剔除宿主注入的干扰路径。
                try
                {
                    string machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
                    string userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
                    string cleanPath = string.Join(";",
                        new[] { machinePath, userPath }
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    // 整合包 runtime 目录置前（若其 PATH 不在系统级则补上）
                    string rtDir = Path.Combine(config.InstallPath.TrimEnd('\\', '/'), "runtime");
                    serviceProcess.StartInfo.Environment["PATH"] =
                        rtDir + ";" + cleanPath;
                }
                catch { /* 环境净化失败不阻塞启动（沿用继承环境） */ }
                serviceProcess.Start();
                CaptureOutput(serviceProcess.StandardOutput);
                CaptureOutput(serviceProcess.StandardError);

                logger.LogInformation("[EmotionTTS] 正在启动 GPT-SoVITS api_v2：{FileName} {Args}", fileName, args);
                int retries = 60;
                while (retries-- > 0)
                {
                    await Task.Delay(2000, cancellationToken);
                    if (await IsPortOpenAsync(config.Port, cancellationToken))
                    {
                        // 端口开后再等 /openapi.json 可用（服务真正就绪）
                        if (await IsV2ApiAsync(httpClient, config.Port, cancellationToken))
                        {
                            lock (gate) { _ready = true; }
                            logger.LogInformation("[EmotionTTS] GPT-SoVITS api_v2 已就绪（端口 {Port}）", config.Port);
                            return true;
                        }
                    }
                    if (serviceProcess.HasExited)
                    {
                        logger.LogError("[EmotionTTS] GPT-SoVITS 服务进程已退出，退出码 {Code}；请检查安装目录/权重", serviceProcess.ExitCode);
                        return false;
                    }
                }
                logger.LogError("[EmotionTTS] GPT-SoVITS 服务启动超时（120秒）");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[EmotionTTS] GPT-SoVITS 自启失败");
                return false;
            }
        }
        finally
        {
            readyGate.Release();
        }
    }

    /// <summary>停止自启服务（模块销毁时）。外部对接的服务不杀。</summary>
    public void StopSelf()
    {
        Process? p;
        lock (gate)
        {
            p = serviceProcess;
            serviceProcess = null;
            _ready = false;
        }
        if (p != null && !p.HasExited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            _ready = false;
        }
    }

    // ===== E3：权重同步移除 =====
    // E3 权重由 tts_infer.yaml 在服务启动时加载（yaml custom 段指定 GptWeight/SovitsWeight），
    // 运行期只按段换 ref_audio（/tts 请求里带），**不做 set_gpt_weights 热切换**——
    // 实测：热重载权重会让服务进入坏状态（后续所有合成 Errno 22，需重启服务才恢复）。
    // 保留方法签名（调用方已 try-catch），实现为 no-op。

    public async Task EnsureSyncedAsync(EmotionTTSConfig config, HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        // E3：权重已由 yaml 在启动时加载，不做热切换（避免服务坏状态）。
        await Task.CompletedTask;
    }

    // ===== 工具 =====

    /// <summary>探测端口上的服务是否为 api_v2 兼容（FastAPI /openapi.json 含 set_gpt_weights 或 tts 路径）。</summary>
    static async Task<bool> IsV2ApiAsync(HttpClient httpClient, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(3000);
            using var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/openapi.json", cts.Token);
            if (!response.IsSuccessStatusCode)
                return false;
            string body = await response.Content.ReadAsStringAsync(cts.Token);
            return body.Contains("set_gpt_weights", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("\"/tts\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(1000);
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static (string fileName, string args) ParseCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int endQuote = command.IndexOf('"', 1);
            if (endQuote > 0)
            {
                string fileName = command[1..endQuote];
                string args = command[(endQuote + 1)..].Trim();
                return (fileName, args);
            }
        }

        int firstSpace = command.IndexOf(' ');
        if (firstSpace > 0)
            return (command[..firstSpace], command[(firstSpace + 1)..]);

        return (command, "");
    }

    void CaptureOutput(StreamReader reader)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!reader.EndOfStream)
                {
                    string? line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                        logger.LogDebug("[GPT-SoVITS] {Line}", line);
                }
            }
            catch (Exception) { }
        });
    }
}
