using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Azuma.EmotionTTS.E5;

/// <summary>E3 播放：AudioFileReader + WaveOutEvent（整段拼接后一次播放）。</summary>
static class GptSovitsAudioPlayer
{
    static readonly SemaphoreSlim PlaybackGate = new(1, 1);
    const int MaxWavHeaderBytes = 64 * 1024;

    /// <summary>
    /// 播放 wav 文件到播放完成。
    /// <paramref name="whenPlaybackStarted"/> 在 speaker.Play() 后置位。
    /// </summary>
    public static async Task PlayFileAsync(
        string filePath,
        CancellationToken cancellationToken = default,
        TaskCompletionSource? whenPlaybackStarted = null)
    {
        await PlaybackGate.WaitAsync(cancellationToken);
        TaskCompletionSource tcs = new();

        try
        {
            await using AudioFileReader reader = new(filePath);
            using WaveOutEvent speaker = new();
            speaker.Init(reader);
            speaker.PlaybackStopped += OnPlaybackStopped;
            speaker.Play();
            whenPlaybackStarted?.TrySetResult();

            await using CancellationTokenRegistration registration =
                cancellationToken.Register(() => speaker.Stop());
            await tcs.Task;
        }
        catch (Exception ex)
        {
            whenPlaybackStarted?.TrySetException(ex);
            throw;
        }
        finally
        {
            PlaybackGate.Release();
        }

        void OnPlaybackStopped(object? _, StoppedEventArgs e)
        {
            if (e.Exception != null)
                tcs.TrySetException(e.Exception);
            else
                tcs.TrySetResult();
        }
    }

    /// <summary>
    /// api_v2 流式 WAV：首包为 WAV 头，后续为 raw PCM。
    /// <paramref name="whenPlaybackStarted"/> 在 speaker.Play() 之后置位，供 Content 提前 return 以贴近桌宠气泡。
    /// </summary>
    public static async Task PlayAndSaveWavStreamAsync(
        Stream httpStream,
        string? cacheFilePath,
        CancellationToken cancellationToken = default,
        TaskCompletionSource? whenPlaybackStarted = null)
    {
        await PlaybackGate.WaitAsync(cancellationToken);
        using MemoryStream headerBuf = new();
        byte[] readBuf = new byte[8192];
        WaveFormat? format = null;
        int pcmStartInHeader = -1;
        BufferedWaveProvider? buffer = null;
        WaveOutEvent? speaker = null;
        TaskCompletionSource playbackDone = new();
        WaveFileWriter? cacheWriter = null;
        string? cacheTmpPath = string.IsNullOrEmpty(cacheFilePath)
            ? null
            : cacheFilePath + $".{Guid.NewGuid():N}.tmp";
        bool streamCompleted = false;
        byte[] carry = Array.Empty<byte>(); // 不足一帧（BlockAlign）的残留字节，跨调用累积

        try
        {
            while (format == null)
            {
                int n = await httpStream.ReadAsync(readBuf, cancellationToken);
                if (n <= 0)
                    throw new InvalidDataException("流式 WAV 在收到格式头之前结束");

                headerBuf.Write(readBuf, 0, n);
                if (TryParseWavHeader(headerBuf.GetBuffer(), (int)headerBuf.Length, out format, out pcmStartInHeader))
                    break;
                if (headerBuf.Length > MaxWavHeaderBytes)
                    throw new InvalidDataException("Streaming WAV header exceeds the 64 KiB safety limit");
            }

            // 绝不丢采样：溢出时等待播放腾出空间，否则会「漏字/漏段」
            buffer = new BufferedWaveProvider(format!)
            {
                BufferDuration = TimeSpan.FromMinutes(10),
                DiscardOnBufferOverflow = false
            };

            speaker = new WaveOutEvent();
            speaker.Init(buffer);
            speaker.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null)
                    playbackDone.TrySetException(e.Exception);
                else
                    playbackDone.TrySetResult();
            };
            speaker.Play();
            whenPlaybackStarted?.TrySetResult();

            if (cacheTmpPath != null)
            {
                try
                {
                    cacheWriter = new WaveFileWriter(cacheTmpPath, format!);
                }
                catch
                {
                    GptSovitsFileUtil.TryDelete(cacheTmpPath);
                    cacheTmpPath = null;
                }
            }

            if (pcmStartInHeader < (int)headerBuf.Length)
            {
                int pcmLen = (int)headerBuf.Length - pcmStartInHeader;
                byte[] pcm = headerBuf.GetBuffer().AsSpan(pcmStartInHeader, pcmLen).ToArray();
                carry = await FeedAlignedAsync(buffer, pcm, 0, pcm.Length, carry, cancellationToken);
                TryWriteCache(ref cacheWriter, ref cacheTmpPath, pcm, 0, pcm.Length);
            }

            while (true)
            {
                int n;
                try
                {
                    n = await httpStream.ReadAsync(readBuf, cancellationToken);
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException ||
                                            ex is System.IO.IOException ||
                                            ex.InnerException is System.Net.Http.HttpRequestException ||
                                            ex.InnerException is System.IO.IOException)
                {
                    // 流中断（服务端提前关闭/连接重置）：已收到的音频已进 buffer 播放中，
                    // 不丢弃——正常收尾（播放完已缓冲部分），避免整句静音。
                    streamCompleted = false;
                    break;
                }
                if (n <= 0)
                    break;
                carry = await FeedAlignedAsync(buffer, readBuf, 0, n, carry, cancellationToken);
                TryWriteCache(ref cacheWriter, ref cacheTmpPath, readBuf, 0, n);
            }

            streamCompleted = true;

            // 等缓冲播完；尾部留足余量，避免句尾被截断。
            // 设备故障/停止时 playbackDone 会置位（可能带异常），必须一并检查，
            // 否则 BufferedBytes 不再被消费且循环不退出 → 流式播放死循环。
            while (buffer.BufferedBytes > 0 && !cancellationToken.IsCancellationRequested &&
                   !playbackDone.Task.IsCompleted)
            {
                int buffered = buffer.BufferedBytes;
                int delayMs = buffered > format!.AverageBytesPerSecond / 4 ? 40 : 20;
                await Task.Delay(delayMs, cancellationToken);
            }

            await Task.Delay(100, cancellationToken);
            speaker.Stop();
            // 超时兜底：极端情况下 Stop 不触发 PlaybackStopped 时避免永久等待
            await playbackDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            whenPlaybackStarted?.TrySetException(ex);
            throw;
        }
        finally
        {
            try
            {
                try { speaker?.Dispose(); }
                finally
                {
                    bool cacheFinalized = TryFinalizeCache(ref cacheWriter, cacheTmpPath);
                    if (cacheTmpPath != null)
                        PublishOrDiscardCache(cacheTmpPath, cacheFilePath!, streamCompleted && cacheFinalized);
                }
            }
            finally
            {
                PlaybackGate.Release();
            }
        }
    }

    /// <summary>纯 PCM 流式播放（api_v2 media_type=raw，无 WAV 头；format 由调用方指定）。</summary>
    public static async Task PlayRawPcmStreamAsync(
        Stream httpStream,
        WaveFormat format,
        CancellationToken cancellationToken = default,
        TaskCompletionSource? whenPlaybackStarted = null)
    {
        await PlaybackGate.WaitAsync(cancellationToken);
        byte[] readBuf = new byte[8192];
        TaskCompletionSource playbackDone = new();

        try
        {
            var buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMinutes(10),
                DiscardOnBufferOverflow = false
            };

            using WaveOutEvent speaker = new();
            speaker.Init(buffer);
            speaker.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null)
                    playbackDone.TrySetException(e.Exception);
                else
                    playbackDone.TrySetResult();
            };
            speaker.Play();
            whenPlaybackStarted?.TrySetResult();

            while (true)
            {
                int n;
                try
                {
                    n = await httpStream.ReadAsync(readBuf, cancellationToken);
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException ||
                                            ex is System.IO.IOException ||
                                            ex.InnerException is System.Net.Http.HttpRequestException ||
                                            ex.InnerException is System.IO.IOException)
                {
                    break; // 流中断：已收到部分正常收尾
                }
                if (n <= 0)
                    break;
                await AddSamplesReliableAsync(buffer, readBuf, 0, n, cancellationToken);
            }

            while (buffer.BufferedBytes > 0 && !cancellationToken.IsCancellationRequested &&
                   !playbackDone.Task.IsCompleted)
            {
                int buffered = buffer.BufferedBytes;
                int delayMs = buffered > format.AverageBytesPerSecond / 4 ? 40 : 20;
                await Task.Delay(delayMs, cancellationToken);
            }

            await Task.Delay(100, cancellationToken);
            speaker.Stop();
            await playbackDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            whenPlaybackStarted?.TrySetException(ex);
            throw;
        }
        finally
        {
            PlaybackGate.Release();
        }
    }

    static void TryWriteCache(ref WaveFileWriter? writer, ref string? tmpPath,
        byte[] data, int offset, int count)
    {
        if (writer == null)
            return;
        try
        {
            writer.Write(data, offset, count);
        }
        catch
        {
            try { writer.Dispose(); } catch { }
            writer = null;
            if (tmpPath != null)
                GptSovitsFileUtil.TryDelete(tmpPath);
            tmpPath = null;
        }
    }

    static bool TryFinalizeCache(ref WaveFileWriter? writer, string? tmpPath)
    {
        if (writer == null)
            return false;
        try
        {
            writer.Dispose();
            writer = null;
            return true;
        }
        catch
        {
            writer = null;
            if (tmpPath != null)
                GptSovitsFileUtil.TryDelete(tmpPath);
            return false;
        }
    }

    /// <summary>
    /// 按帧（BlockAlign）对齐喂 PCM：绝不把半帧（奇数）字节写进 buffer，半帧残留字节缓存在 carry 跨调用累积。
    /// 原因：NAudio 的 BufferedWaveProvider.Read 在 BufferedBytes 不是 BlockAlign 整数倍时，
    /// 返回的帧里会混入上一轮 buffer 的脏数据（旧 PCM 字节）→ 爆音/杂音。
    /// 返回新的 carry（不足一帧的残留字节）。
    /// </summary>
    static async Task<byte[]> FeedAlignedAsync(
        BufferedWaveProvider buffer, byte[] data, int offset, int count,
        byte[] carry, CancellationToken cancellationToken)
    {
        int frame = Math.Max(1, buffer.WaveFormat.BlockAlign);

        // 拼接 carry（不足一帧的残留）+ 新数据
        byte[] combined;
        int start;
        if (carry.Length == 0)
        {
            combined = data;
            start = offset;
        }
        else
        {
            combined = new byte[carry.Length + count];
            Array.Copy(carry, 0, combined, 0, carry.Length);
            Array.Copy(data, offset, combined, carry.Length, count);
            start = 0;
        }

        int total = carry.Length + count;
        int aligned = total - (total % frame);   // 对齐到帧边界

        if (aligned > 0)
            await AddSamplesReliableAsync(buffer, combined, start, aligned, cancellationToken);

        // 尾部不足一帧的字节作为新 carry 返回
        int remain = total - aligned;
        if (remain > 0)
        {
            var newCarry = new byte[remain];
            Array.Copy(combined, start + aligned, newCarry, 0, remain);
            return newCarry;
        }
        return Array.Empty<byte>();
    }

    /// <summary>
    /// 缓冲满时等待播放消费，禁止丢弃采样（DiscardOnBufferOverflow 会直接漏音）。
    /// </summary>
    static async Task AddSamplesReliableAsync(
        BufferedWaveProvider buffer, byte[] data, int offset, int count,
        CancellationToken cancellationToken)
    {
        int remaining = count;
        int pos = offset;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int free = buffer.BufferLength - buffer.BufferedBytes;
            if (free <= 0)
            {
                await Task.Delay(20, cancellationToken);
                continue;
            }

            int write = Math.Min(remaining, free);
            // 对齐到帧边界，避免半帧写入
            int frame = Math.Max(1, buffer.WaveFormat.BlockAlign);
            if (write > frame)
                write -= write % frame;
            if (write <= 0)
            {
                await Task.Delay(20, cancellationToken);
                continue;
            }

            buffer.AddSamples(data, pos, write);
            pos += write;
            remaining -= write;
        }
    }

    static void PublishOrDiscardCache(string tmpPath, string finalPath, bool completed)
    {
        if (!completed)
        {
            GptSovitsFileUtil.TryDelete(tmpPath);
            return;
        }

        try
        {
            if (!GptSovitsWavCache.IsValid(tmpPath))
            {
                GptSovitsFileUtil.TryDelete(tmpPath);
                return;
            }

            File.Move(tmpPath, finalPath, overwrite: true);
        }
        catch
        {
            GptSovitsFileUtil.TryDelete(tmpPath);
        }
    }

    static bool TryParseWavHeader(byte[] data, int length, out WaveFormat? format, out int dataStart)
    {
        format = null;
        dataStart = -1;
        if (length < 44)
            return false;

        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
            return false;

        int pos = 12;
        ushort audioFormat = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;

        while (pos + 8 <= length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            int chunkSize = BitConverter.ToInt32(data, pos + 4);
            if (chunkSize < 0)
                return false;

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16 || pos + 24 > length)
                    return false;
                audioFormat = BitConverter.ToUInt16(data, pos + 8);
                channels = BitConverter.ToUInt16(data, pos + 10);
                sampleRate = BitConverter.ToInt32(data, pos + 12);
                bitsPerSample = BitConverter.ToUInt16(data, pos + 22);
            }
            else if (chunkId == "data")
            {
                dataStart = pos + 8;
                break;
            }

            long next = (long)pos + 8 + chunkSize;
            if (next > int.MaxValue)
                return false;
            pos = (int)next;
            if (pos % 2 != 0)
                pos++;
        }

        if (dataStart < 0 || audioFormat != 1 || channels is < 1 or > 8 ||
            sampleRate is < 8000 or > 384000 || bitsPerSample is not (8 or 16 or 24 or 32))
            return false;

        format = new WaveFormat(sampleRate, bitsPerSample, channels);
        return true;
    }
}
