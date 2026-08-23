using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 字级 DSP：对整句 wav 按"逐字控制表 + 对齐边界"做逐字变速/移调/插停顿/气口。
/// - 变速：EmotionDspProcessor.TimeStretch（纯 C# 相位声码器，保持音调）
/// - 移调：EmotionDspProcessor.PitchShift（相位声码器+重采样，独立于变速）
/// - 停顿：段边界插入静音（PauseAfterMs）
/// - 气口：吸气/喘 → 插入短促气声（依赖 ref 素材，此处插入弱化静音气口）
/// 输出：新 wav 文件（或覆盖）。
/// </summary>
static class EmotionCharLevelFx
{
    /// <summary>字级 DSP 是否激活（有任何非默认控制项）。</summary>
    public static bool IsActive(List<CharLevelDirective> table)
    {
        if (table == null) return false;
        foreach (CharLevelDirective d in table)
        {
            if (Math.Abs(d.PitchOffset) > 0.001 ||
                Math.Abs(d.SpeedFactor - 1.0) > 0.001 ||
                Math.Abs(d.Volume - 1.0) > 0.001 ||
                d.PauseAfterMs > 0 ||
                !string.IsNullOrEmpty(d.Breath) ||
                !string.IsNullOrEmpty(d.Timbre) ||
                d.VibratoRate > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 对整句 wav 做字级 DSP，写回新路径。无控制项时原样复制。
    /// </summary>
    /// <param name="sourceWav">合成好的整句 wav。</param>
    /// <param name="table">逐字控制表。</param>
    /// <param name="boundaries">对齐边界（可选；null 或数量不匹配时按字数分摊切段）。</param>
    /// <param name="outputWav">输出路径。</param>
    public static string Apply(string sourceWav, List<CharLevelDirective> table,
        List<CharBoundary>? boundaries, string outputWav)
    {
        if (!File.Exists(sourceWav))
            return sourceWav;

        if (!IsActive(table))
        {
            // 无控制项：原样复制
            File.Copy(sourceWav, outputWav, overwrite: true);
            return outputWav;
        }

        using var reader = new AudioFileReader(sourceWav);
        WaveFormat format = reader.WaveFormat;
        float[] allSamples = ReadAllSamples(reader);
        if (allSamples.Length == 0)
            return sourceWav;

        int sampleRate = format.SampleRate;
        int channels = format.Channels;
        int samplesPerSec = sampleRate * channels;

        // 切段：有对齐用对齐边界，否则按字数分摊（单位=采样数）
        List<(int start, int end)> segments = BuildSegments(allSamples.Length, table, boundaries, sampleRate, channels);

        // 逐段 DSP 处理
        var output = new List<float>(allSamples.Length + 8192);
        double lastVolume = 1.0; // 跨字音量平滑（从上一字滑到当前字）
        for (int i = 0; i < segments.Count && i < table.Count; i++)
        {
            (int s, int e) = segments[i];
            CharLevelDirective cd = table[i];
            float[] seg = new float[Math.Max(0, e - s)];
            Array.Copy(allSamples, s, seg, 0, seg.Length);

            // 变速/移调
            if (Math.Abs(cd.SpeedFactor - 1.0) > 0.001 || Math.Abs(cd.PitchOffset) > 0.001)
                seg = ProcessWithDsp(seg, format, cd);

            // 音量增益 + 平滑（从上一字音量滑到当前字；支持细分包络）
            if (Math.Abs(cd.Volume - 1.0) > 0.001 || lastVolume > 0 && Math.Abs(lastVolume - cd.Volume) > 0.001
                || cd.SubDivisions > 1 || !string.IsNullOrEmpty(cd.Envelope))
            {
                seg = ApplyVolumeEnvelope(seg, cd, ref lastVolume);
            }
            else
            {
                lastVolume = cd.Volume;
            }

            output.AddRange(seg);

            // 停顿（PauseAfterMs）
            if (cd.PauseAfterMs > 0)
            {
                int pauseSamples = (int)(cd.PauseAfterMs / 1000.0 * samplesPerSec);
                output.AddRange(new float[pauseSamples]);
            }

            // 气口：吸气/喘 → 插入短促弱气声（100~180ms 弱噪声，量级低）
            if (!string.IsNullOrEmpty(cd.Breath))
            {
                int breathMs = cd.Breath == "喘" ? 160 : 120;
                int breathSamples = (int)(breathMs / 1000.0 * samplesPerSec);
                float[] breath = new float[breathSamples];
                var rng = new Random(i * 131 + 17);
                for (int b = 0; b < breathSamples; b++)
                {
                    double env = Math.Sin(Math.PI * b / Math.Max(1, breathSamples)); // 淡入淡出
                    breath[b] = (float)((rng.NextDouble() * 2 - 1) * 0.06 * env);
                }
                output.AddRange(breath);
            }
        }

        // 最终防削波：峰值超 1.0 时整体压缩（timbre 效果器可能引入增益）
        float peakOut = 0;
        foreach (float s in output)
            peakOut = Math.Max(peakOut, Math.Abs(s));
        if (peakOut > 0.97f)
        {
            float scale = 0.97f / peakOut;
            for (int i = 0; i < output.Count; i++)
                output[i] *= scale;
        }

        // 写回 wav
        WriteWav(output, format, outputWav);
        return outputWav;
    }

    /// <summary>
    /// 音量增益 + 平滑 + 细分包络：
    /// - 跨字平滑：从 lastVolume 线性滑到目标 volume（字间不跳变）
    /// - 细分：字内切 SubDivisions 份，可应用包络（linear/ease-in/ease-out/ADSR）
    /// </summary>
    static float[] ApplyVolumeEnvelope(float[] seg, CharLevelDirective cd, ref double lastVolume)
    {
        if (seg.Length == 0)
            return seg;

        double target = EmotionDirectiveParser.ClampVolume(cd.Volume);

        // 细分包络（字内渐变）
        if (cd.SubDivisions > 1 && seg.Length >= cd.SubDivisions)
        {
            int n = cd.SubDivisions;
            int per = seg.Length / n;
            for (int k = 0; k < n; k++)
            {
                int start = k * per;
                int end = (k == n - 1) ? seg.Length : (k + 1) * per;
                // 包络因子：每份的增益（基于平滑的起点+终点）
                double t0 = k / (double)n;
                double t1 = (k + 1) / (double)n;
                double volStart = lastVolume + (target - lastVolume) * t0;
                double volEnd = lastVolume + (target - lastVolume) * t1;
                // 应用包络形状（对每份内部）
                for (int i = start; i < end; i++)
                {
                    double inner = (i - start) / (double)(end - start);
                    double env = ApplyEnvelopeShape(inner, cd.Envelope);
                    // 份内：volStart→volEnd 渐变 × env
                    double v = volStart + (volEnd - volStart) * inner;
                    seg[i] = (float)(seg[i] * v * env);
                }
            }
        }
        else
        {
            // 无细分：整个字从 lastVolume 滑到 target
            int len = seg.Length;
            for (int i = 0; i < len; i++)
            {
                double t = len <= 1 ? 1 : i / (double)(len - 1);
                double v = lastVolume + (target - lastVolume) * t;
                seg[i] = (float)(seg[i] * v);
            }
        }

        lastVolume = target;
        return seg;
    }

    /// <summary>包络形状（0~1 区间内）：linear/ease-in/ease-out/ADSR。</summary>
    static double ApplyEnvelopeShape(double t, string envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope))
            return 1.0;
        string e = envelope.Trim().ToLowerInvariant();
        t = Math.Clamp(t, 0.0, 1.0);
        switch (e)
        {
            case "linear":
                return t;
            case "ease-in":
                return t * t;
            case "ease-out":
                return 1 - (1 - t) * (1 - t);
            case "ease-in-out":
                return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            default:
                // ADSR 简写：a:0.1,d:0.2,s:0.6,r:0.1（起音/衰减/延音/释放比例）
                if (e.StartsWith("a:", StringComparison.Ordinal))
                {
                    double a = ParseEnvPart(e, 'a', 0.1);
                    double d = ParseEnvPart(e, 'd', 0.2);
                    double s = ParseEnvPart(e, 's', 0.6);
                    double r = ParseEnvPart(e, 'r', 0.1);
                    if (t < a)
                        return t / Math.Max(0.0001, a); // attack 0→1
                    if (t < a + d)
                        return 1 - (t - a) / Math.Max(0.0001, d) * (1 - s); // decay 1→s
                    if (t < 1 - r)
                        return s; // sustain
                    return s * (1 - (t - (1 - r)) / Math.Max(0.0001, r)); // release s→0
                }
                return 1.0;
        }
    }

    static double ParseEnvPart(string envelope, char key, double def)
    {
        foreach (string part in envelope.Split(','))
        {
            string p = part.Trim();
            if (p.Length >= 2 && p[0] == key && p[1] == ':')
            {
                if (double.TryParse(p[2..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                    return Math.Clamp(v, 0.0, 1.0);
            }
        }
        return def;
    }

    // ==== 内部 ====

    static float[] ReadAllSamples(AudioFileReader reader)
    {
        var list = new List<float>(4096);
        float[] buf = new float[8192];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                list.Add(buf[i]);
        }
        return list.ToArray();
    }

    /// <summary>构建段边界（采样索引）。优先对齐边界，否则按字数比例。</summary>
    static List<(int start, int end)> BuildSegments(int totalSamples, List<CharLevelDirective> table,
        List<CharBoundary>? boundaries, int sampleRate, int channels)
    {
        int samplesPerSec = sampleRate * channels;
        var result = new List<(int, int)>();

        // 对齐边界可用且数量匹配
        if (boundaries != null && boundaries.Count >= table.Count)
        {
            for (int i = 0; i < table.Count; i++)
            {
                int start = (int)(boundaries[i].StartSec * samplesPerSec);
                int end = (int)(boundaries[i].EndSec * samplesPerSec);
                start = Math.Clamp(start, 0, totalSamples);
                end = Math.Clamp(end, start, totalSamples);
                result.Add((start, end));
            }
            return result;
        }

        // 分摊：按字数比例（每段尽量等长）
        int per = Math.Max(1, totalSamples / Math.Max(1, table.Count));
        for (int i = 0; i < table.Count; i++)
        {
            int start = i * per;
            int end = i == table.Count - 1 ? totalSamples : Math.Min((i + 1) * per, totalSamples);
            result.Add((start, end));
        }
        return result;
    }

    /// <summary>用自研相位声码器对单段做变速+移调（纯 C#，不依赖 SoundTouch）。</summary>
    static float[] ProcessWithDsp(float[] samples, WaveFormat format, CharLevelDirective cd)
    {
        try
        {
            int channels = format.Channels;
            int sampleRate = format.SampleRate;
            if (channels <= 0 || sampleRate <= 0 || samples.Length == 0)
                return samples;

            // 多声道：逐声道独立处理（相位声码器按单声道处理，避免声道间相位干扰）。
            // ⚠️ TimeStretch/PitchShift 会改变段长度——不能写回固定长度数组或按原索引错位写入
            //   （会导致变速被截断 + 信号错位，音频损坏不可听）。必须按各声道处理后的实际长度交错输出。
            float[] result = samples;
            if (channels == 1)
            {
                result = ApplySingleChannel(samples, sampleRate, cd);
            }
            else
            {
                int frames = samples.Length / channels;
                var processedChannels = new List<float[]>(channels);
                int maxLen = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    var channel = new float[frames];
                    for (int i = 0; i < frames; i++)
                        channel[i] = samples[i * channels + ch];

                    float[] processed = ApplySingleChannel(channel, sampleRate, cd);
                    processedChannels.Add(processed);
                    if (processed.Length > maxLen)
                        maxLen = processed.Length;
                }

                // 按最大长度交错输出（短声道补零，保证变速/变调后的真实长度）
                var output = new float[maxLen * channels];
                for (int i = 0; i < maxLen; i++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        float[] pc = processedChannels[ch];
                        output[i * channels + ch] = i < pc.Length ? pc[i] : 0f;
                    }
                }
                result = output;
            }

            return result;
        }
        catch (Exception)
        {
            // DSP 失败：原样返回（不因 DSP 报废整句）
            return samples;
        }
    }

    /// <summary>单声道处理：变速 + 变调 + 颤音 + 音色（顺序：变速→变调→颤音→音色）。</summary>
    static float[] ApplySingleChannel(float[] samples, int sampleRate, CharLevelDirective cd)
    {
        float[] data = samples;

        // 变速（time-stretch，保持音调）
        if (Math.Abs(cd.SpeedFactor - 1.0) > 0.001)
            data = EmotionDspProcessor.TimeStretch(data, sampleRate, cd.SpeedFactor);

        // 变调（pitch-shift，保持时长）
        if (Math.Abs(cd.PitchOffset) > 0.001)
            data = EmotionDspProcessor.PitchShift(data, sampleRate, cd.PitchOffset);

        // 颤音（vibrato：周期性的微小变调/延迟调制，制造颤抖感）
        if (cd.VibratoRate > 0 && cd.VibratoDepth > 0)
            data = ApplyVibrato(data, sampleRate, cd.VibratoRate, cd.VibratoDepth);

        // 音色（混响/失真/空灵/金属）
        if (!string.IsNullOrEmpty(cd.Timbre))
            data = ApplyTimbre(data, sampleRate, cd.Timbre);

        return data;
    }

    /// <summary>
    /// 颤音：对信号做周期性的微小延迟调制（类 chorus/vibrato）。
    /// 频率=每秒颤动次数，深度=最大延迟调制量（毫秒）。纯 C# 延迟线实现。
    /// </summary>
    public static float[] ApplyVibrato(float[] samples, int sampleRate, double rateHz, double depthSemitones)
    {
        if (samples.Length < 16 || sampleRate <= 0)
            return samples;

        // 半音深度 → 延迟调制毫秒（约 0.5 半音 ≈ 4ms @ 440Hz 附近，保守换算）
        double depthMs = depthSemitones * 6.0;
        depthMs = Math.Clamp(depthMs, 0.5, 40.0);
        int maxDelay = (int)Math.Ceiling(depthMs / 1000.0 * sampleRate) + 4;
        if (maxDelay < 4)
            maxDelay = 4;

        // 延迟线（循环缓冲）
        var delayLine = new float[maxDelay];
        int writePos = 0;
        var output = new float[samples.Length];

        for (int n = 0; n < samples.Length; n++)
        {
            // LFO：0~2π，相位累计
            double phase = 2.0 * Math.PI * rateHz * n / sampleRate;
            double lfo = (Math.Sin(phase) + 1.0) / 2.0; // 0~1
            double delaySamples = 1.0 + lfo * (maxDelay - 2);

            // 读位置 = 写位置 - delay（循环）
            double readPos = writePos - delaySamples;
            while (readPos < 0) readPos += maxDelay;
            int r0 = (int)readPos % maxDelay;
            int r1 = (r0 + 1) % maxDelay;
            double frac = readPos - (int)readPos;

            float delayed = (float)(delayLine[r0] * (1 - frac) + delayLine[r1] * frac);

            // 干湿混合：湿声约 40%（颤音是"轻微失谐"，全湿会太飘）
            output[n] = samples[n] * 0.6f + delayed * 0.4f;

            // 写入延迟线
            delayLine[writePos] = samples[n];
            writePos = (writePos + 1) % maxDelay;
        }

        return output;
    }

    /// <summary>
    /// 音色配方查询委托：输入音色名（如"混响"/"自创名"），返回配方串
    /// （如 "reverb:0.4" / "dist:3.0" / "echo:280:0.35" / "metal:5:0.85"，可组合 "+"）。
    /// 由 EmotionTTSSpeechModel 注入（查知识表 __timbre_{名}，未命中回退内置预设）。
    /// </summary>
    public static Func<string, string?>? TimbreRecipeResolver;

    /// <summary>
    /// 音色处理：按配方驱动（每个音色名查配方串 → 解析效果器参数 → 顺序执行）。
    /// 组合（"混响+失真"）按 + 分隔逐个应用。未知音色无配方时静默跳过（不破坏音频）。
    /// </summary>
    static float[] ApplyTimbre(float[] samples, int sampleRate, string timbre)
    {
        float[] data = samples;
        foreach (string part in timbre.Split('+', '，', ','))
        {
            string name = part.Trim();
            if (name.Length == 0)
                continue;
            string? recipe = TimbreRecipeResolver?.Invoke(name) ?? DefaultTimbreRecipe(name);
            if (string.IsNullOrEmpty(recipe))
                continue; // 无配方：跳过（自创音色未注册时静默，不破坏音频）
            data = ApplyTimbreRecipe(data, sampleRate, recipe);
        }
        return data;
    }

    /// <summary>内置预设配方（未注册/未学习时的默认值；AI 注册 __timbre_{名} 后覆盖）。</summary>
    static string? DefaultTimbreRecipe(string name) => name switch
    {
        "混响" => "reverb:0.35",
        "失真" => "dist:3.0",
        "空灵" => "echo:280:0.35",
        "金属" => "metal:5:0.85",
        _ => null,
    };

    /// <summary>按配方串执行效果器链（"reverb:0.4+dist:2.5" 等）。</summary>
    static float[] ApplyTimbreRecipe(float[] samples, int sampleRate, string recipe)
    {
        float[] data = samples;
        foreach (string effect in recipe.Split('+', '，', ','))
        {
            string e = effect.Trim();
            if (e.Length == 0)
                continue;
            int colon = e.IndexOf(':');
            if (colon <= 0)
                continue;
            string op = e[..colon].Trim().ToLowerInvariant();
            string args = e[(colon + 1)..].Trim();
            var parts = args.Split(':', ',', '，');
            double a = parts.Length > 0 && double.TryParse(parts[0].Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double va) ? va : 0;
            double b = parts.Length > 1 && double.TryParse(parts[1].Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vb) ? vb : 0;
            switch (op)
            {
                case "reverb": case "混响":
                    data = ApplyReverb(data, sampleRate, a > 0 ? a : 0.35);
                    break;
                case "dist": case "distortion": case "失真":
                    data = ApplyDistortion(data, a > 0 ? a : 3.0);
                    break;
                case "echo": case "空灵":
                    data = ApplyEcho(data, sampleRate, a > 0 ? a : 280, b > 0 ? b : 0.35);
                    break;
                case "metal": case "金属":
                    data = ApplyMetal(data, sampleRate, a > 0 ? a : 5, b > 0 ? b : 0.85);
                    break;
            }
        }
        return data;
    }

    /// <summary>混响：3 条并行梳状滤波 + 1 个全通，加权混合（Schroeder 简化版）。mix=干湿比（0~1）。</summary>
    static float[] ApplyReverb(float[] samples, int sampleRate, double mix = 0.35)
    {
        // 梳状延迟时间（ms）：经典 Schroeder 比例
        int[] combMs = { 29, 37, 41, 43 };
        double[] combGain = { 0.75, 0.72, 0.69, 0.66 };
        int allpassMs = 5;
        double allpassGain = 0.5;
        mix = Math.Clamp(mix, 0.0, 1.0);

        int n = samples.Length;
        var output = new float[n];

        // 每个梳状滤波
        for (int c = 0; c < combMs.Length; c++)
        {
            int delayMs = combMs[c];
            double gain = combGain[c];
            int delay = Math.Max(1, (int)(delayMs / 1000.0 * sampleRate));
            var buf = new float[delay];
            int wp = 0;
            float[] comb = new float[n];
            for (int i = 0; i < n; i++)
            {
                float rp = buf[wp];
                float y = samples[i] + (float)(rp * gain);
                buf[wp] = y;
                wp = (wp + 1) % delay;
                comb[i] = rp; // 反馈输出
            }
            for (int i = 0; i < n; i++)
                output[i] += comb[i] * 0.25f;
        }

        // 全通（色彩）
        int apDelay = Math.Max(1, (int)(allpassMs / 1000.0 * sampleRate));
        var apBuf = new float[apDelay];
        int apWp = 0;
        for (int i = 0; i < n; i++)
        {
            float x = output[i];
            float d = apBuf[apWp];
            float y = (float)(-allpassGain * x + d);
            apBuf[apWp] = x + (float)(allpassGain * d);
            apWp = (apWp + 1) % apDelay;
            output[i] = y;
        }

        // 干湿混合（mix 比例湿声）
        double wet = mix, dry = 1.0 - mix;
        for (int i = 0; i < n; i++)
            output[i] = (float)(samples[i] * dry + output[i] * wet);
        return output;
    }

    /// <summary>失真：软削波（tanh 近似），模拟沙哑/嘶吼。drive=过载强度。</summary>
    static float[] ApplyDistortion(float[] samples, double drive = 3.0)
    {
        var output = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i] * drive;
            // tanh 近似：x / (1+|x|)，再归一化
            double y = x / (1.0 + Math.Abs(x));
            output[i] = (float)(y * 1.8);
        }
        // 轻微归一化防爆音
        float peak = 0;
        foreach (float s in output)
            peak = Math.Max(peak, Math.Abs(s));
        if (peak > 0.95f)
        {
            float scale = 0.95f / peak;
            for (int i = 0; i < output.Length; i++)
                output[i] *= scale;
        }
        return output;
    }

    /// <summary>空灵：单回声（delayMs 延迟，feedback 反馈），制造空旷感。</summary>
    static float[] ApplyEcho(float[] samples, int sampleRate, double delayMs = 280, double feedback = 0.35)
    {
        int delay = Math.Max(1, (int)(delayMs / 1000.0 * sampleRate));
        if (delay < 8)
            return samples;
        feedback = Math.Clamp(feedback, 0.0, 0.9);
        double wet = Math.Clamp(feedback, 0.0, 0.6);
        var buf = new float[delay];
        int wp = 0;
        var output = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float delayed = buf[wp];
            float y = samples[i] + (float)(delayed * feedback);
            buf[wp] = y;
            wp = (wp + 1) % delay;
            output[i] = (float)(samples[i] * (1 - wet) + delayed * wet);
        }
        return output;
    }

    /// <summary>金属：极短梳状共振（delayMs 延迟，feedback 反馈），机械/金属感。</summary>
    static float[] ApplyMetal(float[] samples, int sampleRate, double delayMs = 5, double feedback = 0.85)
    {
        int delay = Math.Max(2, (int)(delayMs / 1000.0 * sampleRate));
        feedback = Math.Clamp(feedback, 0.0, 0.95);
        var buf = new float[delay];
        int wp = 0;
        var output = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float rp = buf[wp];
            float y = samples[i] + (float)(rp * feedback);
            buf[wp] = y;
            wp = (wp + 1) % delay;
            output[i] = samples[i] * 0.5f + rp * 0.5f;
        }
        // 归一化
        float peak = 0;
        foreach (float s in output)
            peak = Math.Max(peak, Math.Abs(s));
        if (peak > 0.95f)
        {
            float scale = 0.95f / peak;
            for (int i = 0; i < output.Length; i++)
                output[i] *= scale;
        }
        return output;
    }

    static void WriteWav(List<float> samples, WaveFormat format, string path)
    {
        // 用 WaveFileWriter 写 32-bit float
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels));
        writer.WriteSamples(samples.ToArray(), 0, samples.Count);
    }
}
