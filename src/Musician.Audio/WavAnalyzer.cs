using NAudio.Wave;

namespace Musician.Audio;

/// <summary>
/// 把 WAV 的客观指标打印出来。
///
/// 这个命令存在的意义：作曲的一方（AI 或人）可能听不见声音，需要一个能「看」
/// 音频的通道。峰值/RMS 能发现静音、削波、电平失衡；能量包络能看出曲式结构和
/// 尾音长度。它不能替代耳朵，但能挡住绝大多数低级错误。
/// </summary>
public static class WavAnalyzer
{
    /// <summary>低于此 RMS 视为静音（约 -80 dBFS）。</summary>
    private const double SilenceFloor = 1e-4;

    private const int MaxEnvelopeLines = 60;
    private const int BarWidth = 40;

    /// <param name="bucketSecondsOverride">
    /// 强制指定的包络精度（秒）。默认自适应到 60 行左右，排查半秒级的凹陷时太粗，
    /// 这时就该手动指定。
    /// </param>
    /// <param name="fromSeconds">只显示这个时间之后的部分，null 表示从头。</param>
    public static void Report(
        string path,
        TextWriter output,
        double? bucketSecondsOverride = null,
        double? fromSeconds = null)
    {
        float[] samples;
        int channels;
        int sampleRate;

        using (var reader = new AudioFileReader(path))
        {
            channels = reader.WaveFormat.Channels;
            sampleRate = reader.WaveFormat.SampleRate;
            samples = ReadAll(reader);
        }

        var frames = channels > 0 ? samples.Length / channels : 0;
        if (frames == 0)
        {
            output.WriteLine("⚠ 文件里没有音频数据");
            return;
        }

        var duration = (double)frames / sampleRate;

        // 混成单声道用于分析 —— 立体声左右差异不该影响能量判断
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += samples[i * channels + c];
            mono[i] = sum / channels;
        }

        // 峰值要取所有声道的最大绝对值 —— 削波由单声道分量决定。
        // 用混音后的 mono 去算峰值会偏低（左右声道不完全相同时）。
        var peak = 0f;
        foreach (var sample in samples)
        {
            var abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
        }

        // RMS 则用混音后的信号，这才是听感上的平均电平
        double sumSquares = 0;
        foreach (var sample in mono)
            sumSquares += (double)sample * sample;
        var rms = Math.Sqrt(sumSquares / frames);

        output.WriteLine($"文件    {Path.GetFullPath(path)}");
        output.WriteLine($"格式    {sampleRate} Hz · {channels} 声道 · 32 位浮点");
        output.WriteLine($"时长    {duration:F2}s");
        output.WriteLine($"峰值    {peak:F4}        RMS {rms:F4} ({ToDb(rms):F1} dBFS)");
        output.WriteLine();

        // 包络：桶宽自适应，保证输出不超过 MaxEnvelopeLines 行
        var bucketSeconds = bucketSecondsOverride ?? Math.Max(0.25, duration / MaxEnvelopeLines);
        var bucketFrames = Math.Max(1, (int)(bucketSeconds * sampleRate));
        var bucketCount = (int)Math.Ceiling((double)frames / bucketFrames);

        var bucketRms = new double[bucketCount];
        for (var b = 0; b < bucketCount; b++)
        {
            var start = b * bucketFrames;
            var end = Math.Min(start + bucketFrames, frames);
            double acc = 0;
            for (var i = start; i < end; i++)
                acc += (double)mono[i] * mono[i];
            bucketRms[b] = Math.Sqrt(acc / (end - start));
        }

        var maxBucketRms = bucketRms.Max();

        output.WriteLine(
            $"能量包络（每格 {bucketSeconds:F2}s，静音阈值 {ToDb(SilenceFloor):F0} dBFS）" +
            (fromSeconds is { } from ? $"，从 {from:F2}s 起" : ""));

        for (var b = 0; b < bucketCount; b++)
        {
            var time = b * bucketSeconds;
            if (fromSeconds is { } start && time < start)
                continue;

            if (bucketRms[b] <= SilenceFloor)
            {
                output.WriteLine($"  {time,6:F2}s  · (静音)");
                continue;
            }

            var length = maxBucketRms > 0
                ? Math.Max(1, (int)Math.Round(bucketRms[b] / maxBucketRms * BarWidth))
                : 0;
            output.WriteLine($"  {time,6:F2}s  {new string('█', length)} {ToDb(bucketRms[b]):F0} dB");
        }

        // 最后有声位置
        var lastAudible = -1;
        for (var b = bucketCount - 1; b >= 0; b--)
        {
            if (bucketRms[b] > SilenceFloor) { lastAudible = b; break; }
        }

        output.WriteLine();
        if (lastAudible < 0)
        {
            output.WriteLine("⚠ 整段都是静音 —— 链路某处断了");
            return;
        }

        // 用**固定的绝对参照**报告尾部衰减，而不是渲染器自己的那个阈值 ——
        // 拿渲染器的标准去检验渲染器的输出是循环论证，验不出东西。
        output.WriteLine("尾部衰减：各阈值下最后一个高于它的位置");
        double[] probes = [-30, -40, -50, -60];
        foreach (var db in probes)
        {
            var linear = Math.Pow(10, db / 20.0);
            var last = -1;
            for (var b = bucketCount - 1; b >= 0; b--)
            {
                if (bucketRms[b] > linear) { last = b; break; }
            }

            var where = last < 0
                ? "从未达到"
                : $"{Math.Min((last + 1) * bucketSeconds, duration):F2}s 为止";
            output.WriteLine($"    高于 {db,4} dBFS    {where}");
        }

        output.WriteLine();
        output.WriteLine("  参照：-40 dBFS 以下在正常音量下已接近静音；-60 dBFS 以下基本听不见。");
        output.WriteLine("        若「高于 -40 dBFS」的位置远早于文件结尾，说明尾部是空转。");
    }

    private static float[] ReadAll(ISampleProvider reader)
    {
        var buffer = new float[8192];
        var all = new List<float>();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
                all.Add(buffer[i]);
        }
        return all.ToArray();
    }

    private static double ToDb(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-12));
}
