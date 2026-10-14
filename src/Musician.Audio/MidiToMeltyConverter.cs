using Melanchall.DryWetMidi.Core;

namespace Musician.Audio;

/// <summary>
/// ★ 全项目唯一的接缝，也是风险最高的文件。
///
/// DryWetMIDI 的 <see cref="MidiFile"/> 和 MeltySynth 的 <c>MeltySynth.MidiFile</c>
/// 是两套**完全无关**的类型，没有任何适配器或隐式转换。唯一能过去的桥是
/// 标准 MIDI 文件（SMF）的字节流。
///
/// 所有阻抗不匹配都集中在这一个方法里，别让它扩散到其它文件。
/// </summary>
internal static class MidiToMeltyConverter
{
    /// <summary>把乐谱序列化成 SMF 字节并倒回开头，交给 MeltySynth 解析。</summary>
    public static MeltySynth.MidiFile Convert(MidiFile dryWetMidiFile)
    {
        using var stream = new MemoryStream(capacity: 64 * 1024);
        dryWetMidiFile.Write(stream, MidiFileFormat.MultiTrack);

        // ★ 约束 1：Write 之后位置停在末尾，而 MeltySynth 的 Load 不会 Seek 到 0，
        //   它从当前位置开始读 —— 漏掉这一行必定抛
        //   InvalidDataException: The chunk type must be 'MThd'。
        stream.Position = 0;

        // MeltySynth 在构造函数内部即时读完（eager），并且用 leaveOpen: true，
        // 不接管这个 stream，所以 using 作用域结束时释放它是安全的。
        return new MeltySynth.MidiFile(stream);
    }
}
