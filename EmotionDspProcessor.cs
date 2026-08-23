using System;

namespace Azuma.EmotionTTS.E3;

/// <summary>
/// 纯 C# 相位声码器（Phase Vocoder）：字级 DSP 的变速/变调核心。
/// 不依赖 SoundTouch —— 用 STFT + 相位传播实现 time-stretch，配合重采样实现 pitch-shift。
/// 参考经典算法（J. Laroche & M. Dolson, "New phase-vocoder techniques..."）。
/// 
/// 质量目标（接近 SoundTouch）：
///  - 分析窗 Hamming 512（16kHz 下 32ms），hop 分析 128
///  - 相位传播：保持瞬时频率连续，减少"机器人感"
///  - 合成窗 1/2 平方 Hamming（WOLA 完美重建）
/// </summary>
static class EmotionDspProcessor
{
    /// <summary>
    /// 变速（time-stretch）：保持音调不变，改变时长。
    /// ratio &gt; 1 = 变快（时长缩短），&lt; 1 = 变慢（时长拉长）。
    /// </summary>
    public static float[] TimeStretch(float[] input, int sampleRate, double ratio)
    {
        if (input.Length == 0)
            return input;
        if (Math.Abs(ratio - 1.0) < 0.001)
            return (float[])input.Clone();

        // FFT 尺寸自适应：短段（单字等 <512 采样 ≈ 16ms@16kHz）用 256 减小计算量；
        // 长段保持 512 保证质量（相位声码器精度与 FFT 尺寸相关，长段不宜缩小）。
        const int fullFft = 512;
        const int smallFft = 256;
        int fftSize = input.Length < fullFft ? smallFft : fullFft;
        const int analysisHop = 128;
        // 变速 ratio>1 = 更快更短：合成 hop = 分析 hop / ratio（帧间隔变短 → 输出变短）
        int synthesisHop = (int)Math.Round(analysisHop / ratio);
        if (synthesisHop < 16)
            synthesisHop = 16;

        int nFrames = Math.Max(1, (input.Length - fftSize) / analysisHop + 1);
        int outLen = (nFrames - 1) * synthesisHop + fftSize;
        var output = new float[outLen];

        // 窗（Hamming 分析窗）
        var win = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
            win[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / (fftSize - 1))); // Hamming

        // 合成窗 = 分析窗；重叠相加后**逐点除以 Σw²**（WOLA 完美重建）——
        // 固定 2/3 缩放会产生 hop-rate 幅度调制（电音/啁啾感），逐点归一化消除它。
        var synthWin = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
            synthWin[i] = win[i];

        // 每个输出点的重叠权重平方和（WOLA 归一化分母）
        var weightSum = new float[outLen];
        var lastPhase = new float[fftSize / 2 + 1];
        var sumPhase = new float[fftSize / 2 + 1];

        for (int f = 0; f < nFrames; f++)
        {
            int start = f * analysisHop;
            // 分析帧
            var frame = new float[fftSize];
            for (int i = 0; i < fftSize && start + i < input.Length; i++)
                frame[i] = input[start + i] * win[i];

            // FFT（NAudio 2.3：Complex[] 数组）
            int n = fftSize;
            var buf = new NAudio.Dsp.Complex[n];
            for (int i = 0; i < n; i++)
                buf[i].X = frame[i];
            NAudio.Dsp.FastFourierTransform.FFT(true, (int)Math.Log(n, 2), buf);

            // 相位传播（瞬时频率估计 + 累积）
            float omega = 2.0f * (float)Math.PI * analysisHop / n;
            var mags = new float[n / 2 + 1];
            for (int k = 0; k <= n / 2; k++)
            {
                float mag = (float)Math.Sqrt(buf[k].X * buf[k].X + buf[k].Y * buf[k].Y);
                float phase = (float)Math.Atan2(buf[k].Y, buf[k].X);
                mags[k] = mag;

                float dphi = phase - lastPhase[k] - omega * k;
                dphi -= (float)(2.0 * Math.PI * Math.Round(dphi / (2.0 * Math.PI)));
                lastPhase[k] = phase;

                sumPhase[k] += omega * k * (float)synthesisHop / analysisHop + dphi * (float)synthesisHop / analysisHop;
            }

            // 合成帧（重建谱 → IFFT）
            int synStart = f * synthesisHop;
            if (synStart + fftSize > outLen)
                break;

            var sbuf = new NAudio.Dsp.Complex[n];
            for (int k = 0; k <= n / 2; k++)
            {
                sbuf[k].X = mags[k] * (float)Math.Cos(sumPhase[k]);
                sbuf[k].Y = mags[k] * (float)Math.Sin(sumPhase[k]);
                if (k > 0 && k < n / 2)
                {
                    sbuf[n - k].X = mags[k] * (float)Math.Cos(sumPhase[k]);
                    sbuf[n - k].Y = -mags[k] * (float)Math.Sin(sumPhase[k]);
                }
            }
            NAudio.Dsp.FastFourierTransform.FFT(false, (int)Math.Log(n, 2), sbuf);

            // 重叠相加（WOLA）+ 记录权重平方和
            for (int i = 0; i < fftSize && synStart + i < outLen; i++)
            {
                output[synStart + i] += sbuf[i].X * synthWin[i];
                weightSum[synStart + i] += synthWin[i] * synthWin[i];
            }
        }

        // WOLA 逐点归一化（完美重建；除零保护）
        for (int i = 0; i < output.Length; i++)
        {
            if (weightSum[i] > 1e-6f)
                output[i] /= weightSum[i];
            else
                output[i] = 0f;
        }

        // 归一化（防削波 + 能量补偿）
        float maxAbs = 0;
        foreach (float s in output)
        {
            float a = Math.Abs(s);
            if (a > maxAbs) maxAbs = a;
        }
        float gain = maxAbs > 1.0f ? 0.95f / maxAbs : 1.0f;
        float energyComp = (float)Math.Sqrt(analysisHop / (double)synthesisHop);
        for (int i = 0; i < output.Length; i++)
            output[i] *= gain * energyComp;

        return output;
    }

    /// <summary>
    /// 变调（pitch-shift）：保持时长不变，改变音调（半音）。
    /// 两步法：先变速（ratio=2^(n/12) 的倒数？——标准做法：变速 ratio → 重采样 1/ratio）。
    /// pitchOffset &gt; 0 = 升高（尖），&lt; 0 = 降低（低沉）。
    /// </summary>
    public static float[] PitchShift(float[] input, int sampleRate, double pitchOffset)
    {
        if (input.Length == 0)
            return input;
        if (Math.Abs(pitchOffset) < 0.001)
            return (float[])input.Clone();

        // 变调比：升高 n 半音 → 频率 ×2^(n/12)
        double pitchRatio = Math.Pow(2.0, pitchOffset / 12.0);
        double factor = 1.0 / pitchRatio;

        // 两步法（推导见注释）：
        // TimeStretch(input, f)：时长 ÷f（f>1 更快更短）
        // Resample(stretched, f)：时长 ×f、音调 ×(1/f)
        // 目标音调 ×p、时长 ×1：
        //   音调：1/f = p → f = 1/p；时长：(1/f_ts) × f_res = 1 → f_ts = f_res = f
        float[] stretched = TimeStretch(input, sampleRate, factor);
        float[] resampled = Resample(stretched, sampleRate, factor);
        return resampled;
    }

    /// <summary>
    /// 带限窗口 sinc 插值重采样：ratio &gt; 1 = 采样点变密（音调升高），&lt; 1 = 变稀。
    /// 线性插值会产生高频混叠（金属/电音感）——sinc 插值是抗混叠的标准做法。
    /// </summary>
    static float[] Resample(float[] input, int sampleRate, double ratio)
    {
        if (input.Length == 0)
            return input;

        int outLen = (int)Math.Round(input.Length * ratio);
        if (outLen < 1)
            outLen = 1;
        var output = new float[outLen];

        // 每侧 sinc 瓣数（8 瓣足够语音级质量；更多瓣更锐但开销更大）
        const int taps = 8;
        for (int i = 0; i < outLen; i++)
        {
            double srcPos = i / ratio;
            if (srcPos < 0 || srcPos >= input.Length)
            {
                output[i] = 0f;
                continue;
            }

            int center = (int)Math.Floor(srcPos);
            double sum = 0, wsum = 0;
            for (int t = -taps; t <= taps; t++)
            {
                int idx = center + t;
                if (idx < 0 || idx >= input.Length)
                    continue;
                double d = srcPos - idx;
                double sinc = Math.Abs(d) < 1e-9 ? 1.0 : Math.Sin(Math.PI * d) / (Math.PI * d);
                // Hamming 窗限制瓣范围（截断 sinc 的振铃）
                double w = 0.54 + 0.46 * Math.Cos(Math.PI * d / (taps + 1));
                sum += input[idx] * sinc * w;
                wsum += sinc * w;
            }
            output[i] = wsum != 0 ? (float)(sum / wsum) : 0f;
        }
        return output;
    }
}
