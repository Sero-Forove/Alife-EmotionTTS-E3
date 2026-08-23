using System;
using System.Diagnostics;
using System.IO;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 端口进程工具（静态，WebUI 端口管理用）：
/// - ProbePortProcess：评估端口占用者是否本插件拉起的进程（CosyVoice server / WhisperX daemon）。
/// - KillPortProcess：杀掉端口监听进程（必定杀，调用方负责先评估）。
/// 静态实现：不依赖 Module 实例（Alife 事件回调中 Module 可能为 null，UI 直接调用本类）。
/// </summary>
static class PortProcessUtil
{
    /// <summary>评估目标端口占用者。返回描述文本；不杀任何进程。</summary>
    public static string ProbePortProcess(int port)
    {
        try
        {
            if (port is < 1 or > 65535)
                return $"端口 {port} 无效（1-65535）";

            int pid = FindPidByPort(port);
            if (pid == 0)
                return $"端口 {port} 无进程监听（无需处理）。";

            string name;
            try { name = Process.GetProcessById(pid).ProcessName; }
            catch { name = "?"; }
            string cmd = GetCommandLine(pid) ?? "";

            string kind;
            if (cmd.Contains("fastapi", StringComparison.OrdinalIgnoreCase) &&
                cmd.Contains("server.py", StringComparison.OrdinalIgnoreCase))
                kind = "本插件 CosyVoice 服务（安全）";
            else if (cmd.Contains("etts_whisperx_daemon", StringComparison.OrdinalIgnoreCase))
                kind = "本插件 WhisperX 对齐进程（安全）";
            else
                kind = "外部进程（非本插件，谨慎）";

            string preview = cmd.Length > 120 ? cmd[..120] + "…" : cmd;
            return $"端口 {port} 被 PID={pid}（{name}）占用，判定：**{kind}**。\n命令行：{preview}";
        }
        catch (Exception ex)
        {
            return $"评估失败：{ex.Message}";
        }
    }

    /// <summary>杀掉占用指定端口的进程（必定杀）。返回结果消息。</summary>
    public static string KillPortProcess(int port)
    {
        try
        {
            if (port is < 1 or > 65535)
                return $"端口 {port} 无效（1-65535）";

            int ownerPid = FindPidByPort(port);
            if (ownerPid == 0)
                return $"端口 {port} 无进程监听（无需清理）";

            string name;
            try { name = Process.GetProcessById(ownerPid).ProcessName; }
            catch { name = "?"; }

            try
            {
                using var killer = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/F /T /PID {ownerPid}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                killer.Start();
                killer.WaitForExit(15000);
            }
            catch
            {
                try { Process.GetProcessById(ownerPid).Kill(true); }
                catch (Exception ex2)
                {
                    return $"杀进程失败（可能权限不足）：{ex2.Message}";
                }
            }

            System.Threading.Thread.Sleep(500);
            if (FindPidByPort(port) == 0)
                return $"端口 {port} 的进程（{name}）已杀掉，端口已释放";
            return $"端口 {port} 进程已尝试杀掉，但端口仍被占用（可能权限不足，需以管理员身份重试）";
        }
        catch (Exception ex)
        {
            return $"操作失败：{ex.Message}";
        }
    }

    /// <summary>查指定端口监听进程 PID（0=无）。</summary>
    static int FindPidByPort(int port)
    {
        try
        {
            string output = RunNetstat();
            foreach (string line in output.Split('\n'))
            {
                string t = line.Trim();
                if (t.Contains($":{port}", StringComparison.Ordinal) && t.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[^1], out int pid))
                        return pid;
                }
            }
        }
        catch (Exception) { }
        return 0;
    }

    static string RunNetstat()
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            }
        };
        p.Start();
        string out1 = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return out1;
    }

    /// <summary>读进程命令行（wmic → PowerShell CIM 兜底）。失败/不可见返回 null。</summary>
    static string? GetCommandLine(int pid)
    {
        string? viaWmic = RunQuery($"wmic process where ProcessId={pid} get CommandLine /value");
        if (!string.IsNullOrWhiteSpace(viaWmic) &&
            !viaWmic.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) &&
            !viaWmic.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            int eq = viaWmic.IndexOf('=');
            if (eq >= 0)
                return viaWmic[(eq + 1)..].Trim();
        }
        string? viaPs = RunQuery(
            $"powershell.exe -NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"");
        return string.IsNullOrWhiteSpace(viaPs) ? null : viaPs.Trim();
    }

    static string? RunQuery(string arguments)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
