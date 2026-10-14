using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Musician.Core.Compile;
using Musician.Core.Spec;
using Musician.Core.Theory;
using Xunit;

namespace Musician.Core.Tests;

public class ScoreCompilerTests
{
    private const int OneBarTicks = 1920;      // 4/4 @ 480 TPPQ

    private static SongSpec SampleSpec() => new()
    {
        Title = "测试曲",
        Tempo = 76,
        Key = "D minor",
        TimeSignature = "4/4",
        Material = new Dictionary<string, Material>
        {
            ["A"] = new Material
            {
                Chords = ["Dm", "Bb", "F", "C"],
                Motif = "D5/q F5/e E5/e D5/h | F5/h D5/h | C5/q D5/q E5/h | E5/q C5/q D5/h",
            },
        },
        Form =
        [
            new SectionSpec { Section = "intro", Bars = 4, Material = "A", Violin = "rest", Piano = "block" },
            new SectionSpec { Section = "theme", Bars = 4, Material = "A", Violin = "motif", Piano = "block" },
        ],
    };

    /// <summary>编译 → 序列化 → 读回。往返一遍才是真正要验证的东西。</summary>
    private static MidiFile RoundTrip(SongSpec spec)
    {
        var midi = ScoreCompiler.Compile(spec);
        using var stream = new MemoryStream();
        midi.Write(stream, MidiFileFormat.MultiTrack);
        stream.Position = 0;
        return MidiFile.Read(stream);
    }

    private static IReadOnlyList<Note> NotesOf(MidiFile file, int trackIndex) =>
        [.. file.GetTrackChunks().ElementAt(trackIndex).GetNotes()];

    /// <summary>
    /// 取出控制器事件及其**绝对**时间。
    ///
    /// 事件流里存的是 DeltaTime（相对上一个事件的增量），不是绝对位置 ——
    /// 直接拿 DeltaTime 排序会得到完全错误的时间顺序。必须累加。
    /// </summary>
    private static IReadOnlyList<(long Time, ControlChangeEvent Event)> ControlsOf(MidiFile file, int trackIndex)
    {
        var results = new List<(long, ControlChangeEvent)>();
        long time = 0;

        foreach (var midiEvent in file.GetTrackChunks().ElementAt(trackIndex).Events)
        {
            time += midiEvent.DeltaTime;
            if (midiEvent is ControlChangeEvent control)
                results.Add((time, control));
        }

        return results;
    }

    // ---------- 约束 2 与 3：时基与速度 ----------

    /// <summary>
    /// 约束 3：漏掉 ReplaceTempoMap 时 SetTempo 不会写进文件，整曲会按默认 120 BPM
    /// 播放 —— 表现只是「曲子莫名其妙变快」，极难定位。这个测试是唯一的防线。
    /// </summary>
    [Fact]
    public void 速度经序列化往返后不变()
    {
        var reloaded = RoundTrip(SampleSpec());

        var tempo = reloaded.GetTempoMap().GetTempoAtTime(new MetricTimeSpan(0));

        Assert.Equal(76, tempo.BeatsPerMinute, precision: 3);
    }

    /// <summary>
    /// 约束 2：MeltySynth 把 resolution 按有符号 Int16 读，SMPTE 时基会解析错乱。
    /// </summary>
    [Fact]
    public void 时基是每四分音符480tick()
    {
        var reloaded = RoundTrip(SampleSpec());

        var division = Assert.IsType<TicksPerQuarterNoteTimeDivision>(reloaded.TimeDivision);
        Assert.Equal(480, division.TicksPerQuarterNote);
    }

    [Fact]
    public void 总长度等于小节数()
    {
        var notes = NotesOf(RoundTrip(SampleSpec()), trackIndex: 0);
        var lastEnd = notes.Max(note => note.Time + note.Length);

        // 8 小节 = 15360 tick；人味化会给和弦起音加一点点偏移，所以留一点余量
        Assert.InRange(lastEnd, 8 * OneBarTicks, 8 * OneBarTicks + 40);
    }

    // ---------- 小提琴：单声部与表现力 ----------

    /// <summary>
    /// 一个「拉和弦」的小提琴是最明显的穿帮 —— 真实的小提琴一次只能拉两个音，
    /// 而且编曲里通常就是单音线条。
    /// </summary>
    [Fact]
    public void 小提琴不同时起音()
    {
        var violin = NotesOf(RoundTrip(SampleSpec()), trackIndex: 1);
        Assert.NotEmpty(violin);

        var startsByTick = violin.GroupBy(note => note.Time).Where(g => g.Count() > 1).ToList();

        Assert.Empty(startsByTick);
    }

    /// <summary>
    /// 同音高的两个音绝不能重叠。连奏会让前一个音多留一点，若前后恰好是同一个音高，
    /// 前一个音的「止音」事件会把刚起的新音一起掐掉 —— 注意 MIDI 里的止音不带力度，
    /// 它关掉的是这个音高上所有正在发声的音。听感上就是音符刚起就断，像演奏被打断。
    /// </summary>
    [Fact]
    public void 同音高的小提琴音不得重叠()
    {
        var violin = NotesOf(RoundTrip(SampleSpec()), trackIndex: 1)
            .OrderBy(note => note.Time)
            .ToList();

        for (var i = 0; i < violin.Count; i++)
        {
            for (var j = i + 1; j < violin.Count; j++)
            {
                if (violin[j].Time >= violin[i].Time + violin[i].Length)
                    break;

                Assert.True(
                    violin[i].NoteNumber != violin[j].NoteNumber,
                    $"音高 {Pitch.Format((int)violin[i].NoteNumber)} 在 " +
                    $"{violin[i].Time}–{violin[i].Time + violin[i].Length} 与 " +
                    $"{violin[j].Time} 起的新音重叠，新音会被前一个音的止音掐掉");
            }
        }
    }

    [Fact]
    public void 小提琴的重叠仅限于连奏()
    {
        var violin = NotesOf(RoundTrip(SampleSpec()), trackIndex: 1)
            .OrderBy(note => note.Time)
            .ToList();

        for (var i = 0; i + 1 < violin.Count; i++)
        {
            var overlap = violin[i].Time + violin[i].Length - violin[i + 1].Time;

            // 30ms 的连奏重叠在 76 BPM 下约 18 tick，加抖动余量后不该超过 60
            Assert.InRange(overlap, -60, 60);
        }
    }

    [Fact]
    public void 小提琴轨带颤音与表情控制器()
    {
        var controls = ControlsOf(RoundTrip(SampleSpec()), trackIndex: 1);
        var numbers = controls.Select(c => (int)c.Event.ControlNumber).ToHashSet();

        Assert.Contains(Controllers.Modulation, numbers);
        Assert.Contains(Controllers.Expression, numbers);
    }

    [Fact]
    public void 长音上颤音深度确实推起来了()
    {
        var controls = ControlsOf(RoundTrip(SampleSpec()), trackIndex: 1);

        var vibrato = controls
            .Where(c => (int)c.Event.ControlNumber == Controllers.Modulation && (int)c.Event.ControlValue > 0)
            .ToList();

        Assert.NotEmpty(vibrato);
    }

    // ---------- 钢琴：踏板与和弦 ----------

    [Fact]
    public void 钢琴轨带延音踏板()
    {
        var controls = ControlsOf(RoundTrip(SampleSpec()), trackIndex: 0);

        var pedal = controls
            .Where(c => (int)c.Event.ControlNumber == Controllers.SustainPedal)
            .ToList();

        Assert.NotEmpty(pedal);

        // 结尾故意不抬踏板，终止和弦才能自然衰减
        var last = pedal.OrderBy(c => c.Time).Last();
        Assert.Equal(127, (int)last.Event.ControlValue);
    }

    [Fact]
    public void 钢琴每小节只有一个和弦()
    {
        var piano = NotesOf(RoundTrip(SampleSpec()), trackIndex: 0);

        // 每小节一组同时起音的音（人味化之后只在几 tick 内散开）
        var groups = piano
            .OrderBy(note => note.Time)
            .GroupBy(note => note.Time / OneBarTicks)
            .ToList();

        Assert.Equal(8, groups.Count);
    }

    // ---------- 可复现性 ----------

    [Fact]
    public void 同一份乐谱编译两次结果完全一致()
    {
        static string Fingerprint(MidiFile file)
        {
            using var stream = new MemoryStream();
            file.Write(stream, MidiFileFormat.MultiTrack);
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream.ToArray()));
        }

        Assert.Equal(Fingerprint(ScoreCompiler.Compile(SampleSpec())),
                     Fingerprint(ScoreCompiler.Compile(SampleSpec())));
    }

    // ---------- 结构校验 ----------

    [Fact]
    public void 动机小节数与和弦数不符时报错()
    {
        var spec = SampleSpec();
        spec.Material["A"].Motif = "D5/w | E5/w";       // 只写了 2 小节，和弦却有 4 个

        var ex = Assert.Throws<FormatException>(() => ScoreCompiler.Compile(spec));

        Assert.Contains("2 小节", ex.Message);
        Assert.Contains("4 个", ex.Message);
    }

    [Fact]
    public void 段落引用不存在的材料时报错()
    {
        var spec = SampleSpec();
        spec.Form[0].Material = "Z";

        var ex = Assert.Throws<FormatException>(() => ScoreCompiler.Compile(spec));

        Assert.Contains("不存在的材料", ex.Message);
        Assert.Contains("A", ex.Message);        // 报错里要列出可用的材料
    }

    // ---------- 拍号换算 ----------

    [Theory]
    [InlineData("4/4", 1920)]
    [InlineData("3/4", 1440)]
    [InlineData("6/8", 1440)]
    [InlineData("2/2", 1920)]
    public void 拍号换算成小节长度(string signature, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ScoreCompiler.TicksPerBar(signature));
    }

    [Fact]
    public void 非法拍号报错()
    {
        Assert.Throws<FormatException>(() => ScoreCompiler.TicksPerBar("4"));
        Assert.Throws<FormatException>(() => ScoreCompiler.TicksPerBar("4/0"));
    }

    // ---------- 音域 ----------

    [Fact]
    public void 小提琴音高落在乐器音域内()
    {
        var violin = NotesOf(RoundTrip(SampleSpec()), trackIndex: 1);

        // 小提琴实际音域约 G3–A6（MIDI 55–93）
        Assert.All(violin, note => Assert.InRange((int)note.NoteNumber, 55, 93));
    }
}
