using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Azuma.EmotionTTS.E5;

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
                    // **合成能力验证**：openapi 接口列表无法区分"同协议但坏/错安装"的服务
                    // （实测残留坏服务 openapi 正常但 /tts 全 Errno 22）。发最小合成请求，
                    // 成功才对接——失败视为不可用，转入自启（若端口被占自启失败，日志提示）。
                    if (await ProbeSynthesisAsync(httpClient, config, cancellationToken))
                    {
                        logger.LogInformation("[EmotionTTS] 对接外部 GPT-SoVITS api_v2 服务（端口 {Port}，合成验证通过）", config.Port);
                        lock (gate) { _ready = true; }
                        return true;
                    }
                    logger.LogWarning("[EmotionTTS] 端口 {Port} 的 api_v2 服务合成验证失败（可能是残留/异常服务），尝试自启新服务", config.Port);
                    // 不 return：尝试杀掉占用者后自启（见下方自启前清理）
                }
                else
                {
                    logger.LogWarning("[EmotionTTS] 端口 {Port} 已占用但不是 api_v2，请检查端口", config.Port);
                    return false;
                }
            }

            // 自启 api_v2.py
            try
            {
                // 端口仍被占用且验证失败（残留/坏服务）：清理占用者后自启干净服务。
                // 只杀监听该端口的进程（大概率是本插件的坏服务/残留；用户手动外部服务在
                // 上面已因"不是 api_v2"或"验证失败"被排除，这里清理合理）。
                if (await IsPortOpenAsync(config.Port, cancellationToken))
                {
                    logger.LogWarning("[EmotionTTS] 端口 {Port} 被异常服务占用，清理后自启", config.Port);
                    PortProcessUtil.KillPortProcess(config.Port);
                    await Task.Delay(1000, cancellationToken);
                }

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

    /// <summary>
    /// 合成能力探测：发一个最小 POST /tts 请求，验证服务能真正合成（openapi 接口列表无法区分
    /// "同协议但坏/错安装"的服务——实测残留坏服务 openapi 正常但 /tts 全 Errno 22）。
    /// 成功返回 true；失败/超时返回 false（不抛）。
    /// </summary>
    async Task<bool> ProbeSynthesisAsync(HttpClient httpClient, EmotionTTSConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            string root = config.InstallPath.TrimEnd('\\', '/');
            // 用配置的中性 ref 做最小合成（短文本，快速验证）
            string refAudio = GptSovitsPresetResolver.ResolvePath(root, config.RefAudio);
            if (string.IsNullOrWhiteSpace(refAudio) || !File.Exists(refAudio))
                return false;
            var payload = new Dictionary<string, object?>
            {
                ["text"] = "测试。",
                ["text_lang"] = "zh",
                ["ref_audio_path"] = refAudio.Replace('\\', '/'),
                ["prompt_text"] = config.RefText ?? "",
                ["prompt_lang"] = string.IsNullOrWhiteSpace(config.RefLanguage) ? "zh" : config.RefLanguage,
                ["text_split_method"] = "cut5",
                ["batch_size"] = 1,
                ["parallel_infer"] = true,
                ["streaming_mode"] = false,
                ["media_type"] = "wav",
            };
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"http://127.0.0.1:{config.Port}/tts")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload),
                    System.Text.Encoding.UTF8,
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(20000); // 20s 超时：合成验证不能拖太久
            using var response = await httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                return false;
            byte[] data = await response.Content.ReadAsByteArrayAsync(cts.Token);
            return data.Length > 100; // 有实际音频数据才算成功
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
