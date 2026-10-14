using NAudio.Wave;

namespace Musician.Audio;

public static class WavWriter
{
    /// <summary>把渲染结果写成 32 位浮点立体声 WAV。</summary>
    public static void Write(string path, RenderResult result)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // 采样率与格式必须和渲染时用的一致，别让两边漂移
        var format = WaveFormat.CreateIeeeFloatWaveFormat(result.SampleRate, result.Channels);

        using var writer = new WaveFileWriter(full, format);
        writer.WriteSamples(result.Interleaved, 0, result.Interleaved.Length);
    }
}
