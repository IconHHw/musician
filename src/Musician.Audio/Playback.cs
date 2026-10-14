using NAudio.Wave;

namespace Musician.Audio;

public static class Playback
{
    /// <summary>播放已渲染的音频，播完才返回。</summary>
    public static void Play(RenderResult result)
    {
        using var waveOut = new WaveOutEvent();
        waveOut.Init(new FloatBufferWaveProvider(result.Interleaved, result.SampleRate, result.Channels));
        waveOut.Play();

        while (waveOut.PlaybackState == PlaybackState.Playing)
            Thread.Sleep(100);
    }
}

/// <summary>
/// ★ 约束 5：绝不要用 <c>BufferedWaveProvider</c> 来播放有限长度的缓冲。
/// 它是个「永不结束」的流 —— 数据放完后仍然返回音频（静音）而不是 0，
/// 于是 PlaybackStopped 永不触发，进程直接挂死。
/// 这是个读到结尾老老实实返回 0 的 provider。
/// </summary>
internal sealed class FloatBufferWaveProvider : IWaveProvider
{
    private readonly byte[] _bytes;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public FloatBufferWaveProvider(float[] interleaved, int sampleRate, int channels)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _bytes = new byte[interleaved.Length * sizeof(float)];
        Buffer.BlockCopy(interleaved, 0, _bytes, 0, _bytes.Length);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _bytes.Length - _position;
        if (remaining <= 0)
            return 0;                       // 0 == 流结束
        var n = Math.Min(count, remaining);
        Buffer.BlockCopy(_bytes, _position, buffer, offset, n);
        _position += n;
        return n;
    }
}
