using MeltySynth;

namespace Musician.Audio;

/// <summary>渲染结果。Interleaved 是 [L0,R0,L1,R1,…] 排列的浮点 PCM。</summary>
/// <param name="PeakBeforeNormalize">归一化之前的原始峰值。</param>
/// <param name="GainApplied">施加的整体增益。&gt;1 是提升，&lt;1 是衰减。</param>
public sealed record RenderResult(
    float[] Interleaved,
    int SampleRate,
    int Channels,
    TimeSpan MidiLength,
    TimeSpan AudioLength,
    float PeakBeforeNormalize,
    float GainApplied);

public static class MidiRenderer
{
    public const int SampleRate = 44100;
    public const int Channels = 2;

    /// <summary>尾音的硬上限。超过这个长度仍未衰减就强制截断，防止无限渲染。</summary>
    public const double MaxTailSeconds = 8.0;

    /// <summary>
    /// 尾部裁剪阈值，**以归一化之后的绝对电平**表示（-45 dBFS）。
    ///
    /// 这里用绝对电平而不是「比音乐低 N dB」，是因为「听不见」本身是一个绝对的
    /// 心理声学事实，不该随曲子的整体响度漂移。之前那版用相对阈值，结果对着
    /// 一首结尾较轻的曲子（钢琴尾声 -26 dB 而全曲平均 -19.7 dB）判错，
    /// 又留下了 6 秒听不见的残留。
    ///
    /// 归一是全曲乘以同一个系数，所以可以先把绝对阈值换算回归一化之前的尺度再比较。
    /// </summary>
    private const double TailCutoffDbFs = -45.0;

    /// <summary>绝对静音下限（约 -80 dBFS），兜底用。</summary>
    private const double SilenceFloor = 1e-4;

    /// <summary>裁掉尾部时多留一点，避免在衰减中途硬切。</summary>
    private const double TrimMarginSeconds = 0.25;

    /// <summary>分析精度：每块 0.25 秒。</summary>
    private const double ChunkSeconds = 0.25;

    /// <summary>输出峰值目标。留一点余量，避免重采样或下游处理的瞬时过冲。</summary>
    private const float TargetPeak = 0.89f;

    public static RenderResult Render(string soundFontPath, Melanchall.DryWetMidi.Core.MidiFile score)
    {
        var settings = new SynthesizerSettings(SampleRate)
        {
            BlockSize = 64,
            MaximumPolyphony = 64,
            // 默认即 true。这是加分项 —— 自带混响尾音，对小提琴听感帮助很大。
            EnableReverbAndChorus = true,
        };

        var synthesizer = new Synthesizer(soundFontPath, settings);
        var meltyMidi = MidiToMeltyConverter.Convert(score);

        var sequencer = new MidiFileSequencer(synthesizer);
        sequencer.Play(meltyMidi, loop: false);

        var midiLength = meltyMidi.Length;
        var midiEndFrame = (int)Math.Ceiling(SampleRate * midiLength.TotalSeconds);

        // 先按上限渲染完，再回头决定裁到哪 —— 比边渲染边判断简单，而且渲染本身很快
        var maxFrames = (int)Math.Ceiling(SampleRate * (midiLength.TotalSeconds + MaxTailSeconds));
        var interleaved = new float[maxFrames * Channels];
        var chunkRms = RenderInChunks(sequencer, interleaved, maxFrames);

        // ★ 约束 4：停止条件来自**音频本身**，绝不是 EndOfSequence。
        //   后者在最后一个消息被「派发」的那一刻就为真，那时释音和混响尾音还在响。
        var keepFrames = FindContentEnd(chunkRms, interleaved, midiEndFrame);

        var trimmed = interleaved.AsSpan(0, keepFrames * Channels).ToArray();
        var (peakBefore, gain) = NormalizeToPeak(trimmed, TargetPeak);

        return new RenderResult(
            trimmed,
            SampleRate,
            Channels,
            midiLength,
            TimeSpan.FromSeconds((double)keepFrames / SampleRate),
            peakBefore,
            gain);
    }

    /// <summary>分块渲染整段（含尾音上限），同时记录每块的 RMS 供后续裁剪决策。</summary>
    private static double[] RenderInChunks(MidiFileSequencer sequencer, float[] interleaved, int maxFrames)
    {
        var chunkFrames = Math.Max(1, (int)(ChunkSeconds * SampleRate));
        var chunkCount = (int)Math.Ceiling((double)maxFrames / chunkFrames);
        var chunkRms = new double[chunkCount];

        var rendered = 0;
        for (var c = 0; c < chunkCount; c++)
        {
            var n = Math.Min(chunkFrames, maxFrames - rendered);
            sequencer.RenderInterleaved(interleaved.AsSpan(rendered * Channels, n * Channels));
            chunkRms[c] = ChunkRms(interleaved, rendered, n);
            rendered += n;
        }

        return chunkRms;
    }

    /// <summary>
    /// 找出音频真正结束的位置：从后往前，找最后一个电平还在听阈之上的块。
    ///
    /// 判据是**归一化之后的 -45 dBFS**（见 TailCutoffDbFs）。因为归一化是全曲同一个
    /// 系数，可以先把绝对阈值换算回原始尺度再比较，这样既不受曲子原始电平影响，
    /// 判据本身又是稳定的心理声学量。
    /// </summary>
    private static int FindContentEnd(double[] chunkRms, float[] interleaved, int midiEndFrame)
    {
        var totalFrames = interleaved.Length / Channels;
        var chunkFrames = Math.Max(1, (int)(ChunkSeconds * SampleRate));

        var peak = 0f;
        foreach (var sample in interleaved)
        {
            var abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
        }

        if (peak <= (float)SilenceFloor)
            return Math.Min(midiEndFrame, totalFrames);      // 全程静音

        var gain = TargetPeak / peak;
        var threshold = Math.Pow(10, TailCutoffDbFs / 20) / gain;

        var lastAudibleChunk = -1;
        for (var c = chunkRms.Length - 1; c >= 0; c--)
        {
            if (chunkRms[c] > threshold) { lastAudibleChunk = c; break; }
        }

        if (lastAudibleChunk < 0)
            return Math.Min(midiEndFrame, totalFrames);

        var marginFrames = (int)(TrimMarginSeconds * SampleRate);
        var keep = (lastAudibleChunk + 1) * chunkFrames + marginFrames;
        return Math.Clamp(keep, 1, totalFrames);
    }

    private static double ChunkRms(float[] interleaved, int startFrame, int frameCount)
    {
        var start = startFrame * Channels;
        var end = start + frameCount * Channels;
        double sumSquares = 0;
        for (var i = start; i < end; i++)
            sumSquares += (double)interleaved[i] * interleaved[i];

        var sampleCount = end - start;
        return sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;
    }

    /// <summary>
    /// 把整体电平归一到目标峰值。
    ///
    /// 为什么不是「只在越界时衰减」：音色库自身输出电平偏低，FluidR3 实测峰值只有
    /// 0.13 左右，不补的话成品基本听不见。整体增益是**全曲同一个系数**，不改变曲内
    /// 的动态对比，只是统一了不同曲子之间的响度 —— 这也是期望的行为。
    ///
    /// 已知取舍：单个瞬态尖峰会拉低整曲响度。钢琴/小提琴音源下很少发生，
    /// 若将来出现，再换成 RMS 归一化或真正的限制器。
    /// </summary>
    private static (float PeakBefore, float GainApplied) NormalizeToPeak(float[] interleaved, float target)
    {
        var peak = 0f;
        foreach (var sample in interleaved)
        {
            var abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
        }

        if (peak <= (float)SilenceFloor)
            return (peak, 1f);              // 全静音，别去除以 0

        var gain = target / peak;
        for (var i = 0; i < interleaved.Length; i++)
            interleaved[i] *= gain;

        return (peak, gain);
    }
}
