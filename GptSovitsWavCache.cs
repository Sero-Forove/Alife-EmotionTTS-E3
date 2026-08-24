using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Alife.Platform;
using NAudio.Wave;

namespace Azuma.EmotionTTS.E5;

static class GptSovitsWavCache
{
    public const int MinWavBytes = 100;

    public static bool IsValid(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                new FileInfo(path).Length < MinWavBytes)
                return false;

            using var reader = new WaveFileReader(path);
            WaveFormat format = reader.WaveFormat;
            return reader.Length > 0 &&
                   format.SampleRate is >= 8000 and <= 384000 &&
                   format.Channels is >= 1 and <= 8 &&
                   format.BitsPerSample is 8 or 16 or 24 or 32 &&
                   format.BlockAlign > 0 &&
                   format.AverageBytesPerSecond > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   ArgumentException or FormatException or NotSupportedException)
        {
            return false;
        }
    }

    public static async Task<string?> SaveWavStreamAsync(Stream content, string preferredPath,
        CancellationToken cancellationToken = default)
    {
        if (IsValid(preferredPath))
            return preferredPath;

        string dir = Path.GetDirectoryName(preferredPath) ?? AlifePath.TempFolderPath;
        string tmpPath = Path.Combine(dir, $"gptsovits_dl_{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var fileStream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await content.CopyToAsync(fileStream, cancellationToken);
            }

            if (!IsValid(tmpPath))
            {
                GptSovitsFileUtil.TryDelete(tmpPath);
                return null;
            }

            if (IsValid(preferredPath))
            {
                GptSovitsFileUtil.TryDelete(tmpPath);
                return preferredPath;
            }

            string? published = TryPublish(tmpPath, preferredPath);
            if (published != null)
                return published;

            string fallback = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(preferredPath) + $"_{Guid.NewGuid():N}.wav");
            File.Move(tmpPath, fallback);
            return IsValid(fallback) ? fallback : null;
        }
        catch
        {
            GptSovitsFileUtil.TryDelete(tmpPath);
            throw;
        }
    }

    static string? TryPublish(string tmpPath, string outputPath)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (IsValid(outputPath))
                {
                    GptSovitsFileUtil.TryDelete(tmpPath);
                    return outputPath;
                }

                if (File.Exists(outputPath))
                    File.Delete(outputPath);

                File.Move(tmpPath, outputPath);
                return outputPath;
            }
            catch (IOException)
            {
                if (IsValid(outputPath))
                {
                    GptSovitsFileUtil.TryDelete(tmpPath);
                    return outputPath;
                }

                Thread.Sleep(80 * (attempt + 1));
            }
        }

        return null;
    }

    /// <summary>
    /// 将多段 WAV 顺序合并为单文件（E3 增强）：
    /// 1) **段间交叉淡化**（60ms 重叠淡入淡出）——消除 ref 切换的"硬切咔声"；
    /// 2) **整段峰值归一**（统一增益到 -1dBFS）——在后续 DSP 调音量**之前**做整体防削波，
    ///    保持段间相对响度（归一后 DSP 曲线再按需调音量）。
    /// 读取时统一转为 float（支持 8/16/24/32-bit），写出 16-bit PCM。
    /// </summary>
    public static string? MergeWavFiles(IReadOnlyList<string> parts, string outputPath)
    {
        if (parts == null || parts.Count == 0)
            return null;

        List<string> valid = new();
        foreach (string p in parts)
        {
            if (IsValid(p))
                valid.Add(p);
        }

        if (valid.Count == 0)
            return null;
        if (valid.Count == 1)
            return valid[0];

        string dir = Path.GetDirectoryName(outputPath) ?? AlifePath.TempFolderPath;
        Directory.CreateDirectory(dir);
        string tmpPath = Path.Combine(dir, $"gptsovits_merge_{Guid.NewGuid():N}.tmp.wav");

        try
        {
            // 1) 读入全部段（浮点），交叉淡化拼接
            WaveFormat? targetFormat = null;
            var segments = new List<(float[] samples, int channels, int sampleRate)>();
            foreach (string part in valid)
            {
                using var reader = new WaveFileReader(part);
                targetFormat ??= reader.WaveFormat;
                float[] all = ReadAllFloat(reader);
                segments.Add((all, targetFormat.Channels, targetFormat.SampleRate));
            }

            if (segments.Count == 0 || targetFormat == null)
                return null;

            int fadeSamples = targetFormat.SampleRate * 60 / 1000; // 60ms 交叉淡化

            // 拼接（段间重叠 fadeSamples/2 淡出 + fadeSamples/2 淡入）
            var merged = new List<float>(segments[0].samples.Length + 1024);
            for (int i = 0; i < segments.Count; i++)
            {
                float[] cur = segments[i].samples;
                if (i == 0)
                {
                    merged.AddRange(cur);
                    continue;
                }

                // 当前段起点与已拼尾部重叠区（交叉淡化）
                int overlap = Math.Min(fadeSamples, Math.Min(cur.Length, merged.Count));
                if (overlap <= 0)
                {
                    merged.AddRange(cur);
                    continue;
                }

                int tailStart = merged.Count - overlap;
                for (int k = 0; k < overlap; k++)
                {
                    double t = k / (double)overlap;               // 0→1 当前段淡入
                    double prevT = 1.0 - t;                        // 1→0 前段淡出
                    merged[tailStart + k] = (float)(merged[tailStart + k] * prevT + cur[k] * t);
                }
                merged.AddRange(cur.AsSpan(overlap));
            }

            // 2) 整段峰值归一（-1dBFS ≈ 0.8913）：统一增益，保持段间相对响度，防后续 DSP 削波
            float peak = 0f;
            foreach (float s in merged)
            {
                float a = Math.Abs(s);
                if (a > peak)
                    peak = a;
            }
            if (peak > 0f && peak < 1f)
            {
                const float targetPeak = 0.8913f; // -1 dBFS
                float gain = Math.Min(1f, targetPeak / peak); // 只放大不压限（压限会破坏动态）
                if (gain > 1.001f)
                {
                    for (int i = 0; i < merged.Count; i++)
                        merged[i] *= gain;
                }
            }

            // 写盘
            WriteFloatWav(merged.ToArray(), targetFormat, tmpPath);

            if (!File.Exists(tmpPath) || new FileInfo(tmpPath).Length < MinWavBytes)
            {
                GptSovitsFileUtil.TryDelete(tmpPath);
                return null;
            }

            if (File.Exists(outputPath))
                GptSovitsFileUtil.TryDelete(outputPath);
            File.Move(tmpPath, outputPath);
            return IsValid(outputPath) ? outputPath : null;
        }
        catch
        {
            GptSovitsFileUtil.TryDelete(tmpPath);
            return null;
        }
    }

    /// <summary>读整个 WaveStream 为 float[]（多声道交织）。</summary>
    static float[] ReadAllFloat(WaveStream reader)
    {
        var list = new List<float>();
        byte[] buf = new byte[reader.WaveFormat.AverageBytesPerSecond];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            int samples = read / reader.WaveFormat.BlockAlign;
            for (int i = 0; i < samples; i++)
            {
                int offset = i * reader.WaveFormat.BlockAlign;
                if (reader.WaveFormat.BitsPerSample == 16)
                    list.Add(BitConverter.ToInt16(buf, offset) / 32768f);
                else if (reader.WaveFormat.BitsPerSample == 24)
                    list.Add((short)((buf[offset + 1] << 8) | buf[offset + 2]) / 32768f);
                else if (reader.WaveFormat.BitsPerSample == 32)
                    list.Add(BitConverter.ToInt32(buf, offset) / 2147483648f);
                else // 8-bit
                    list.Add((buf[offset] - 128) / 128f);
            }
        }
        return list.ToArray();
    }

    /// <summary>写 float[] 为 16-bit PCM wav（与源格式对齐：非 16-bit 统一转 16-bit，GPT-SoVITS 输出本就是 16-bit）。</summary>
    static void WriteFloatWav(float[] samples, WaveFormat sourceFormat, string path)
    {
        using var writer = new WaveFileWriter(path, sourceFormat);
        byte[] buf = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp((int)Math.Round(samples[i] * 32768f), short.MinValue, short.MaxValue);
            buf[i * 2] = (byte)(s & 0xFF);
            buf[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        writer.Write(buf, 0, buf.Length);
    }
}
