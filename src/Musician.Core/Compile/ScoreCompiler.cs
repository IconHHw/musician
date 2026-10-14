using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Composing;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Standards;
using Musician.Core.Notation;
using Musician.Core.Spec;
using Musician.Core.Theory;
// Note 在 Interaction 和 MusicTheory 下都存在，必须消歧
using MusicNote = Melanchall.DryWetMidi.MusicTheory.Note;

namespace Musician.Core.Compile;

/// <summary>
/// 乐谱规格 → MIDI。
///
/// 这一层负责所有「换算」：音名→音号、小节/拍→tick、和弦记号→实际音符、
/// 段落列表→绝对时间轴。这些都是确定性的事，交给代码既快又不会错，
/// 而让 LLM 去做恰恰是它最不擅长的部分。
/// </summary>
public static class ScoreCompiler
{
    public const int TicksPerQuarter = 480;

    private const int PianoChannel = 0;
    private const int ViolinChannel = 1;

    // 钢琴左手（低音）与右手（和弦）各自的音区。分开是为了避免糊成一团。
    private const int BassLow = 38;     // D2
    private const int BassHigh = 50;    // D3
    private const int ChordLow = 55;    // G3
    private const int ChordHigh = 74;   // D5

    private const int PianoVelocity = 68;
    private const int ViolinVelocity = 86;

    private static readonly SevenBitNumber SustainPedal = (SevenBitNumber)Controllers.SustainPedal;

    /// <summary>同一 tick 上的发射顺序：控制器 → 抬踏板 → 踩踏板 → 起音。</summary>
    private enum EmitOrder { Control = 0, PedalUp = 1, PedalDown = 2, NoteOn = 3 }

    public static MidiFile Compile(SongSpec spec, HumanizeSettings? humanize = null)
    {
        spec.Validate();

        var scale = Scale.Parse(spec.Key);
        var ticksPerBar = TicksPerBar(spec.TimeSignature);
        var beatsPerBar = ticksPerBar / TicksPerQuarter;

        var pianoNotes = new List<PlacedNote>();
        var violinNotes = new List<PlacedNote>();
        var pedalPoints = new List<long>();

        // 声部连接要跨段落连续 —— 一整首曲子里和弦转位应该是平滑过渡的
        IReadOnlyList<int>? previousVoicing = null;
        var absoluteBar = 0;

        foreach (var section in spec.Form)
        {
            var material = spec.GetMaterial(section.Material);
            var motif = MotifParser.Parse(material.Motif, beatsPerBar);
            ValidateMaterialCycle(material, motif, section);

            // 动机发展。所有算子在设计上都保持小节数不变，所以这一段做完之后
            // 动机与和弦进行依然对齐 —— 但既然依赖于这个前提，就再验一次。
            if (section.Develop.Count > 0)
            {
                motif = MotifDeveloper.Apply(motif, section.Develop, scale, ticksPerBar);
                ValidateMaterialCycle(material, motif, section);
            }

            var cycleBars = material.Chords.Count;

            var wantsPiano = !section.Piano.Equals("rest", StringComparison.OrdinalIgnoreCase);
            var wantsViolin = section.Violin.Equals("motif", StringComparison.OrdinalIgnoreCase);

            for (var bar = 0; bar < section.Bars; bar++)
            {
                var cycleBar = bar % cycleBars;
                var chord = ChordSymbol.Parse(material.Chords[cycleBar]);
                var barStart = (long)(absoluteBar + bar) * ticksPerBar;

                if (wantsPiano)
                {
                    var voicing = ChordSymbol.VoiceLead(previousVoicing, chord, ChordLow, ChordHigh);
                    previousVoicing = voicing;

                    var bass = PlacePitchClass(chord.RootPitchClass, BassLow, BassHigh);

                    pianoNotes.AddRange(PianoTextures.Realize(
                        section.Piano, voicing, bass, barStart, ticksPerBar, TicksPerQuarter, PianoVelocity));

                    pedalPoints.Add(barStart);
                }

                if (wantsViolin)
                {
                    foreach (var melodyEvent in motif)
                    {
                        if (melodyEvent.BarIndex != cycleBar || melodyEvent.IsRest)
                            continue;

                        violinNotes.Add(new PlacedNote(
                            barStart + melodyEvent.StartTicksInBar,
                            melodyEvent.MidiNote!.Value,
                            melodyEvent.DurationTicks,
                            ViolinVelocity));
                    }
                }
            }

            absoluteBar += section.Bars;
        }

        // 人味化。放在编译之后、发射之前：这一层只改时序、力度和控制器，
        // 不碰音高和时值，所以乐理上一定还是对的。
        var humanizeSettings = humanize ?? new HumanizeSettings();
        var ticksPerMillisecond = Humanizer.TicksPerMillisecond(spec.Tempo, TicksPerQuarter);

        var piano = Humanizer.HumanizePiano(pianoNotes, humanizeSettings, ticksPerMillisecond);
        var violin = Humanizer.HumanizeViolin(violinNotes, humanizeSettings, ticksPerMillisecond);

        return BuildMidiFile(spec, piano, violin, pedalPoints);
    }

    /// <summary>
    /// 材料的动机小节数必须与和弦个数一致 —— 和弦列表是每小节一个。
    /// 这两者一旦对不上，后面所有段落都会错位，所以宁可在这里直接报错。
    /// </summary>
    private static void ValidateMaterialCycle(
        Material material, IReadOnlyList<MelodyEvent> motif, SectionSpec section)
    {
        var motifBars = motif.Count == 0 ? 1 : motif.Max(e => e.BarIndex) + 1;

        if (motifBars != material.Chords.Count)
        {
            throw new FormatException(
                $"段落 '{section.Section}' 的材料里，动机写了 {motifBars} 小节，" +
                $"但和弦给了 {material.Chords.Count} 个（应为每小节一个）。两者必须一致。");
        }
    }

    private static MidiFile BuildMidiFile(
        SongSpec spec,
        IReadOnlyList<PlacedNote> pianoNotes,
        ViolinResult violin,
        IReadOnlyList<long> pedalPoints)
    {
        var (numerator, denominator) = ParseTimeSignature(spec.TimeSignature);

        // ★ 时基必须是 ticks-per-quarter-note：MeltySynth 把 resolution 按 Int16 读，
        //   SMPTE 时基会解析错乱。
        var tempoMap = TempoMap.Create(
            new TicksPerQuarterNoteTimeDivision(TicksPerQuarter),
            Tempo.FromBeatsPerMinute(spec.Tempo),
            new TimeSignature(numerator, denominator));

        var piano = new PatternBuilder()
            .SetVelocity((SevenBitNumber)PianoVelocity)
            .ProgramChange(GeneralMidiProgram.AcousticGrandPiano);
        EmitPiano(piano, pianoNotes, pedalPoints);

        var violinBuilder = new PatternBuilder()
            .SetVelocity((SevenBitNumber)ViolinVelocity)
            .ProgramChange(GeneralMidiProgram.Violin);
        EmitViolin(violinBuilder, violin);

        var pianoChunk = piano.Build().ToTrackChunk(tempoMap, (FourBitNumber)PianoChannel);
        var violinChunk = violinBuilder.Build().ToTrackChunk(tempoMap, (FourBitNumber)ViolinChannel);

        var midiFile = new MidiFile(pianoChunk, violinChunk);

        // ★ 不调这一句，SetTempo 不会写进文件，整曲会按默认 120 BPM 播放，
        //   而且 MidiFile.Length 也是错的。它读起来像实例方法，其实是扩展方法。
        midiFile.ReplaceTempoMap(tempoMap);

        return midiFile;
    }

    /// <summary>
    /// 放置小提琴轨：音符与控制器事件要按时间穿插。
    /// 同一 tick 上控制器排在音符之前，这样颤音和表情在音头就已经生效。
    /// </summary>
    private static void EmitViolin(PatternBuilder builder, ViolinResult violin)
    {
        var timeline = new List<(long Tick, EmitOrder Order, PlacedNote? Note, ControlPoint? Control)>();

        foreach (var note in violin.Notes)
            timeline.Add((note.StartTick, EmitOrder.NoteOn, note, null));

        foreach (var control in violin.Controls)
            timeline.Add((control.Tick, EmitOrder.Control, null, control));

        foreach (var entry in timeline.OrderBy(e => e.Tick).ThenBy(e => (int)e.Order))
        {
            builder.MoveToTime(new MidiTimeSpan(entry.Tick));

            if (entry.Note is { } note)
            {
                builder.Note(ToNote(note.Midi),
                             new MidiTimeSpan(note.DurationTicks),
                             (SevenBitNumber)note.Velocity);
            }
            else if (entry.Control is { } control)
            {
                builder.ControlChange(
                    (SevenBitNumber)control.Controller,
                    (SevenBitNumber)control.Value);
            }
        }
    }

    /// <summary>
    /// MIDI 音号 → DryWetMIDI 的 Note。注意 Note.Get 要的是 SevenBitNumber，
    /// 直接传 int 编译不过。
    /// </summary>
    private static MusicNote ToNote(int midi) => MusicNote.Get((SevenBitNumber)midi);

    /// <summary>
    /// 放置钢琴：音符与踏板要按时间穿插，所以合并到一条时间线上再发射。
    /// 每个换踏板点先抬后踩 —— 抬起来给上一个和弦制音，再踩下让新和弦延续。
    /// 结尾**故意不抬**，终止和弦才能自然衰减。
    /// </summary>
    private static void EmitPiano(
        PatternBuilder builder,
        IReadOnlyList<PlacedNote> notes,
        IReadOnlyList<long> pedalPoints)
    {
        var timeline = new List<(long Tick, EmitOrder Order, PlacedNote? Note, bool PedalDown)>();

        foreach (var note in notes)
            timeline.Add((note.StartTick, EmitOrder.NoteOn, note, false));

        for (var i = 0; i < pedalPoints.Count; i++)
        {
            if (i > 0)
                timeline.Add((pedalPoints[i], EmitOrder.PedalUp, null, false));
            timeline.Add((pedalPoints[i], EmitOrder.PedalDown, null, true));
        }

        foreach (var entry in timeline.OrderBy(e => e.Tick).ThenBy(e => (int)e.Order))
        {
            builder.MoveToTime(new MidiTimeSpan(entry.Tick));

            if (entry.Note is { } note)
            {
                builder.Note(ToNote(note.Midi),
                             new MidiTimeSpan(note.DurationTicks),
                             (SevenBitNumber)note.Velocity);
            }
            else
            {
                builder.ControlChange(SustainPedal, (SevenBitNumber)(entry.PedalDown ? 127 : 0));
            }
        }
    }

    /// <summary>把某个音级放到指定音区里的最低位置。</summary>
    private static int PlacePitchClass(int pitchClass, int lowMidi, int highMidi)
    {
        for (var midi = lowMidi; midi <= highMidi; midi++)
        {
            if (Pitch.PitchClass(midi) == pitchClass) return midi;
        }

        throw new InvalidOperationException(
            $"音级 {pitchClass} 在音区 {lowMidi}–{highMidi} 内找不到落点");
    }

    public static (int Numerator, int Denominator) ParseTimeSignature(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var numerator)
            || !int.TryParse(parts[1], out var denominator)
            || numerator <= 0
            || denominator <= 0)
        {
            throw new FormatException($"拍号 '{text}' 无法解析（应形如 4/4、3/4、6/8）");
        }

        return (numerator, denominator);
    }

    /// <summary>一小节有多少 tick。</summary>
    public static int TicksPerBar(string timeSignature)
    {
        var (numerator, denominator) = ParseTimeSignature(timeSignature);
        var ticksPerBeat = TicksPerQuarter * 4 / denominator;
        return numerator * ticksPerBeat;
    }
}
