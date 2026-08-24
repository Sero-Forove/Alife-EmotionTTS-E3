using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Azuma.EmotionTTS.E5;

static class GptSovitsFileUtil
{
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}

static class GptSovitsHttp
{
    /// <summary>失败时读取并截断响应体、记录日志后抛出（成功则原样返回）。</summary>
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        ILogger logger,
        CancellationToken cancellationToken,
        string? context = null,
        int maxBodyLength = 2000)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > maxBodyLength)
                body = body[..maxBodyLength] + "…";
        }
        catch (OperationCanceledException)
        {
            throw; // 取消必须原样传播，不能被误报为 HTTP 失败
        }
        catch
        {
            body = "(无法读取响应体)";
        }

        string prefix = string.IsNullOrEmpty(context) ? "" : context + " ";
        logger.LogError("【GPT-SoVITS】{Prefix}HTTP {Status} body={Body}",
            prefix, (int)response.StatusCode, body);
        response.EnsureSuccessStatusCode();
    }
}
