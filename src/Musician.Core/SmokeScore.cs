using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Composing;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.MusicTheory;
using Melanchall.DryWetMidi.Standards;
// Note 在 Interaction 和 MusicTheory 两个命名空间下都存在，必须消歧
using MusicNote = Melanchall.DryWetMidi.MusicTheory.Note;

namespace Musician.Core;

/// <summary>
/// M0 冒烟乐谱：一个硬编码的 Dm–Bb–F–C 四小节进行，钢琴 + 小提琴。
/// 存在的唯一目的是打通「编译 → 序列化 → 渲染 → 写盘」整条链路，
/// 并把五条约束钉死在真实代码里。M1 会用真正的 SongSpec 编译器取代它。
/// </summary>
public static class SmokeScore
{
    /// <summary>约束 2：时基必须是 ticks-per-quarter-note。MeltySynth 把 resolution
    /// 按有符号 Int16 读，SMPTE 时基会解析错乱。</summary>
    public const int TicksPerQuarterNote = 480;

    /// <summary>延音踏板。MIDI 里固定是 CC64。</summary>
    private static readonly SevenBitNumber SustainPedal = (SevenBitNumber)64;

    public static MidiFile Build(bool useSustainPedal = true)
    {
        // 约束 2 落点
        var tempoMap = TempoMap.Create(
            new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
            Tempo.FromBeatsPerMinute(76),
            new TimeSignature(4, 4));

        var piano = BuildPiano(useSustainPedal);
        var violin = BuildViolin();

        // 通道在 ToTrackChunk 上给，不在 PatternBuilder 上
        var pianoChunk = piano.ToTrackChunk(tempoMap, (FourBitNumber)0);
        var violinChunk = violin.ToTrackChunk(tempoMap, (FourBitNumber)1);

        var midiFile = new MidiFile(pianoChunk, violinChunk);

        // 约束 3：不调这个，SetTempo/TimeSignature 不会写进文件，
        // 整曲会按默认 120 BPM 播放，MidiFile.Length 也会是错的。
        // 注意 ReplaceTempoMap 是 Interaction 命名空间下的扩展方法。
        midiFile.ReplaceTempoMap(tempoMap);

        return midiFile;
    }

    private static Pattern BuildPiano(bool useSustainPedal)
    {
        if (!useSustainPedal)
        {
            return new PatternBuilder()
                .SetNoteLength(MusicalTimeSpan.Quarter)
                .SetVelocity((SevenBitNumber)70)
                .SetOctave(Octave.Get(4))
                .ProgramChange(GeneralMidiProgram.AcousticGrandPiano)
                .Chord(new[] { NoteName.D, NoteName.F, NoteName.A }, MusicalTimeSpan.Whole)
                .Chord(new[] { NoteName.ASharp, NoteName.D, NoteName.F }, MusicalTimeSpan.Whole)
                .Chord(new[] { NoteName.F, NoteName.A, NoteName.C }, MusicalTimeSpan.Whole)
                .Chord(new[] { NoteName.C, NoteName.E, NoteName.G }, MusicalTimeSpan.Whole)
                .Build();
        }

        // Chord 没有 (NoteName[], Octave, …) 这个重载 —— 八度由 SetOctave 决定。
        // Bb 写作 ASharp：十二音枚举里没有 BFlat 这个名字。
        //
        // CC64 延音踏板：没有它，钢琴一松键就被制音器掐断，结尾会很干。
        // 换和弦时「抬—踩」是真实钢琴的换踏板动作：抬起来给上一个和弦制音，
        // 立刻再踩下让新和弦延续。结尾**故意不抬**，终止和弦才能自然衰减。
        return new PatternBuilder()
            .SetNoteLength(MusicalTimeSpan.Quarter)
            .SetVelocity((SevenBitNumber)70)
            .SetOctave(Octave.Get(4))
            .ProgramChange(GeneralMidiProgram.AcousticGrandPiano)

            .ControlChange(SustainPedal, (SevenBitNumber)127)
            .Chord(new[] { NoteName.D, NoteName.F, NoteName.A }, MusicalTimeSpan.Whole)

            .ControlChange(SustainPedal, (SevenBitNumber)0)
            .ControlChange(SustainPedal, (SevenBitNumber)127)
            .Chord(new[] { NoteName.ASharp, NoteName.D, NoteName.F }, MusicalTimeSpan.Whole)

            .ControlChange(SustainPedal, (SevenBitNumber)0)
            .ControlChange(SustainPedal, (SevenBitNumber)127)
            .Chord(new[] { NoteName.F, NoteName.A, NoteName.C }, MusicalTimeSpan.Whole)

            .ControlChange(SustainPedal, (SevenBitNumber)0)
            .ControlChange(SustainPedal, (SevenBitNumber)127)
            .Chord(new[] { NoteName.C, NoteName.E, NoteName.G }, MusicalTimeSpan.Whole)

            .Build();
    }

    private static Pattern BuildViolin()
    {
        // 小提琴严格单声部 —— 一个「拉和弦」的小提琴是最明显的穿帮。
        // 每个音自带八度（Note.Get），不走 SetOctave。
        return new PatternBuilder()
            .SetNoteLength(MusicalTimeSpan.Quarter)
            .SetVelocity((SevenBitNumber)88)
            .ProgramChange(GeneralMidiProgram.Violin)
            .Note(MusicNote.Get(NoteName.D, 5), MusicalTimeSpan.Half)
            .Note(MusicNote.Get(NoteName.F, 5), MusicalTimeSpan.Half)
            .Note(MusicNote.Get(NoteName.A, 5), MusicalTimeSpan.Half)
            .Note(MusicNote.Get(NoteName.G, 5), MusicalTimeSpan.Half)
            .Note(MusicNote.Get(NoteName.F, 5), MusicalTimeSpan.Whole)
            .Note(MusicNote.Get(NoteName.E, 5), MusicalTimeSpan.Whole)
            .Build();
    }
}
