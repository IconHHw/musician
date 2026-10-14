using Musician.Core.Compile;
using Musician.Core.Notation;
using Musician.Core.Theory;
using Xunit;

namespace Musician.Core.Tests;

public class MotifDeveloperTests
{
    private const int TicksPerBar = 1920;

    private static readonly Scale DMinor = Scale.Parse("D minor");

    /// <summary>两小节的动机，D5 起头。</summary>
    private static IReadOnlyList<MelodyEvent> Motif() =>
        MotifParser.Parse("D5/q F5/e E5/e D5/h | F5/h A4/h");

    private static IReadOnlyList<MelodyEvent> Apply(params string[] operations) =>
        MotifDeveloper.Apply(Motif(), operations, DMinor, TicksPerBar);

    /// <summary>
    /// 具体的算子取值。<see cref="MotifDeveloper.Available"/> 里放的是给人看的模板
    /// （"sequence+n"），带 +n 的模板本身是不能直接执行的，所以测试要用实参。
    /// </summary>
    private static readonly string[] ConcreteOperations =
    [
        "sequence+2", "sequence-2", "octave+1", "octave-1",
        "invert", "retrograde", "ornament",
    ];

    // ---------- 级进移位 ----------

    [Fact]
    public void 级进移位按音阶度数走()
    {
        var moved = MotifDeveloper.Apply(Motif(), ["sequence+2"], DMinor, TicksPerBar);

        // D 上行两个音级是 F（不是 Eb）
        Assert.Equal(77, moved[0].MidiNote);      // D5 → F5
        Assert.Equal(81, moved[1].MidiNote);      // F5 → A5
        Assert.Equal(79, moved[2].MidiNote);      // E5 → G5
    }

    [Fact]
    public void 级进移位后仍在调内()
    {
        var moved = MotifDeveloper.Apply(Motif(), ["sequence+3"], DMinor, TicksPerBar);

        Assert.All(
            moved.Where(e => !e.IsRest),
            e => Assert.True(DMinor.Contains(e.MidiNote!.Value),
                $"{Pitch.Format(e.MidiNote!.Value)} 跑到调外了"));
    }

    [Fact]
    public void 下行级进同样正确()
    {
        var moved = MotifDeveloper.Apply(Motif(), ["sequence-1"], DMinor, TicksPerBar);
        Assert.Equal(72, moved[0].MidiNote);      // D5 下行一级 → C5
    }

    // ---------- 八度 ----------

    [Fact]
    public void 八度移位就是十二个半音()
    {
        var up = MotifDeveloper.Apply(Motif(), ["octave+1"], DMinor, TicksPerBar);
        var down = MotifDeveloper.Apply(Motif(), ["octave-1"], DMinor, TicksPerBar);

        Assert.Equal(86, up[0].MidiNote);         // D5 → D6
        Assert.Equal(62, down[0].MidiNote);       // D5 → D4
    }

    // ---------- 倒影 ----------

    [Fact]
    public void 倒影以第一个音为轴上下翻转()
    {
        var inverted = MotifDeveloper.Apply(Motif(), ["invert"], DMinor, TicksPerBar);

        // 轴上的音不动
        Assert.Equal(74, inverted[0].MidiNote);   // D5 还是 D5

        // F5 在轴上方 3 个半音，翻转后应落到轴下方 —— 吸附到调内是 Bb4
        Assert.Equal(70, inverted[1].MidiNote);
    }

    [Fact]
    public void 倒影结果仍在调内()
    {
        var inverted = MotifDeveloper.Apply(Motif(), ["invert"], DMinor, TicksPerBar);

        Assert.All(
            inverted.Where(e => !e.IsRest),
            e => Assert.True(DMinor.Contains(e.MidiNote!.Value)));
    }

    // ---------- 逆行 ----------

    [Fact]
    public void 逆行把节奏也一起倒过来()
    {
        var retrograde = MotifDeveloper.Apply(Motif(), ["retrograde"], DMinor, TicksPerBar);

        // 原动机最后一个小节是 F5/h A4/h，逆行后开头应是 A4 那条线
        var firstBar = retrograde.Where(e => e.BarIndex == 0).OrderBy(e => e.StartTicksInBar).ToList();

        Assert.NotEmpty(firstBar);
        Assert.Equal(69, firstBar[0].MidiNote);   // A4 —— 原动机的最后一个音
    }

    // ---------- 加花 ----------

    [Fact]
    public void 加花把长音拆成音加邻音()
    {
        var ornamented = MotifDeveloper.Apply(Motif(), ["ornament"], DMinor, TicksPerBar);

        // 动机前三个音是 D5/q F5/e E5/e（都短于二分音符，不动），
        // 第四个音 D5/h 才会被拆开
        Assert.Equal(74, ornamented[3].MidiNote);
        Assert.Equal(480, ornamented[3].DurationTicks);

        // 后半段换成上方邻音 E5
        Assert.Equal(76, ornamented[4].MidiNote);
        Assert.Equal(480, ornamented[4].DurationTicks);
        Assert.Equal(960 + 480, ornamented[4].StartTicksInBar);
    }

    [Fact]
    public void 加花不动短音()
    {
        var ornamented = MotifDeveloper.Apply(Motif(), ["ornament"], DMinor, TicksPerBar);

        // 八分音符原样保留
        Assert.Equal(240, ornamented[1].DurationTicks);
        Assert.Equal(240, ornamented[2].DurationTicks);
    }

    // ---------- 通用约束 ----------

    [Fact]
    public void 所有算子都保持小节数不变()
    {
        var originalBars = Motif().Max(e => e.BarIndex) + 1;

        foreach (var operation in ConcreteOperations)
        {
            var developed = MotifDeveloper.Apply(Motif(), [operation], DMinor, TicksPerBar);
            var bars = developed.Max(e => e.BarIndex) + 1;

            Assert.True(bars == originalBars,
                $"算子 '{operation}' 把动机从 {originalBars} 小节变成了 {bars} 小节，" +
                "会导致与和弦进行错位");
        }
    }

    [Fact]
    public void 所有算子产出的音都在MIDI音域内()
    {
        foreach (var operation in ConcreteOperations)
        {
            var developed = MotifDeveloper.Apply(Motif(), [operation], DMinor, TicksPerBar);

            Assert.All(
                developed.Where(e => !e.IsRest),
                e => Assert.InRange(e.MidiNote!.Value, Pitch.MinMidi, Pitch.MaxMidi));
        }
    }

    [Fact]
    public void 多个算子按顺序叠加()
    {
        var developed = MotifDeveloper.Apply(Motif(), ["sequence+2", "octave-1"], DMinor, TicksPerBar);

        // D5 先上移两个音级到 F5，再降八度到 F4
        Assert.Equal(65, developed[0].MidiNote);
    }

    [Fact]
    public void 空算子列表原样返回()
    {
        Assert.Equal(Motif(), MotifDeveloper.Apply(Motif(), [], DMinor, TicksPerBar));
    }

    [Fact]
    public void 未知算子报错并列出可用写法()
    {
        var ex = Assert.Throws<FormatException>(
            () => MotifDeveloper.Apply(Motif(), ["随便写"], DMinor, TicksPerBar));

        Assert.Contains("未知的动机发展手法", ex.Message);
        Assert.Contains("sequence+n", ex.Message);      // 报错里给出的是模板写法
    }

    [Fact]
    public void 移位量写得不对时报错()
    {
        Assert.Throws<FormatException>(
            () => MotifDeveloper.Apply(Motif(), ["sequence"], DMinor, TicksPerBar));
        Assert.Throws<FormatException>(
            () => MotifDeveloper.Apply(Motif(), ["sequence+x"], DMinor, TicksPerBar));
    }

    [Fact]
    public void 位移过大越界时报错而不是悄悄夹住()
    {
        var ex = Assert.Throws<FormatException>(
            () => MotifDeveloper.Apply(Motif(), ["octave+9"], DMinor, TicksPerBar));

        Assert.Contains("超出 MIDI 音域", ex.Message);
    }
}
