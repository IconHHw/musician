using Musician.Core.Compile;
using Xunit;

namespace Musician.Core.Tests;

public class HumanizerTests
{
    private static readonly HumanizeSettings Settings = new();

    /// <summary>76 BPM、480 TPPQ 下每毫秒对应的 tick 数。</summary>
    private const double TicksPerMs = 76 * 480 / 60000.0;

    private static List<PlacedNote> ChordAt(long tick, params int[] midis) =>
        [.. midis.Select(midi => new PlacedNote(tick, midi, 1920, 68))];

    // ---------- 确定性 ----------

    [Fact]
    public void 相同种子产出完全一致()
    {
        var notes = ChordAt(0, 50, 57, 62, 66);

        var first = Humanizer.HumanizePiano(notes, Settings, TicksPerMs);
        var second = Humanizer.HumanizePiano(notes, Settings, TicksPerMs);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 不同种子产出不同结果()
    {
        var notes = ChordAt(0, 50, 57, 62, 66);

        var first = Humanizer.HumanizePiano(notes, Settings with { Seed = 1 }, TicksPerMs);
        var second = Humanizer.HumanizePiano(notes, Settings with { Seed = 2 }, TicksPerMs);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void 小提琴轨同样可复现()
    {
        var notes = new List<PlacedNote> { new(0, 74, 960, 86), new(960, 76, 480, 86) };

        var first = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs);
        var second = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs);

        Assert.Equal(first.Notes, second.Notes);
        Assert.Equal(first.Controls, second.Controls);
    }

    // ---------- 钢琴滚奏 ----------

    [Fact]
    public void 和弦滚奏保留从低到高的先后顺序()
    {
        var chord = ChordAt(0, 50, 57, 62);

        var result = Humanizer.HumanizePiano(chord, Settings, TicksPerMs);
        var starts = result.Select(note => note.StartTick).ToList();

        // 三个音不再齐奏
        Assert.Equal(3, starts.Distinct().Count());

        // 而且严格低音在前 —— 抖动对整个和弦是同一个值，不会打乱顺序
        Assert.True(starts[0] < starts[1], "最低音应最早");
        Assert.True(starts[1] < starts[2], "中间音应早于最高音");
    }

    [Fact]
    public void 滚奏的最大延迟在一个和弦的自然范围内()
    {
        var chord = ChordAt(0, 50, 57, 62, 66, 69);

        var result = Humanizer.HumanizePiano(chord, Settings, TicksPerMs);
        var spreadTicks = result.Max(n => n.StartTick) - result.Min(n => n.StartTick);
        var spreadMs = spreadTicks / TicksPerMs;

        // 五个音的块状和弦，展开应在几十毫秒内；超过 100ms 就变成刻意的琶音了
        Assert.InRange(spreadMs, 5, 100);
    }

    // ---------- 小提琴连奏 ----------

    [Fact]
    public void 紧接的音之间被连起来不留缝()
    {
        var notes = new List<PlacedNote>
        {
            new(0, 74, 480, 86),
            new(480, 76, 480, 86),      // 紧接前一个音的结尾
        };

        var result = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs);

        Assert.True(
            result.Notes[0].DurationTicks > 480,
            "连奏时前一个音应被延长，把两个音连起来");
    }

    [Fact]
    public void 中间有间隔的音不做连奏延长()
    {
        var notes = new List<PlacedNote>
        {
            new(0, 74, 480, 86),
            new(960, 76, 480, 86),      // 中间空了一拍
        };

        var result = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs);

        Assert.Equal(480, result.Notes[0].DurationTicks);
    }

    // ---------- 小提琴表现力 ----------

    [Fact]
    public void 长音有颤音渐入短音没有()
    {
        var notes = new List<PlacedNote>
        {
            new(0, 74, 960, 86),        // 二分音符 —— 够长，要有颤音
            new(960, 76, 240, 86),      // 八分音符 —— 太短，不该有
        };

        var result = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs);
        var vibrato = result.Controls
            .Where(c => c.Controller == Controllers.Modulation && c.Value > 0)
            .ToList();

        Assert.Single(vibrato);
        Assert.Equal(Settings.VibratoDepth, vibrato[0].Value);

        // 渐入：颤音深度出现在音头之后，而不是一上来就有
        Assert.True(vibrato[0].Tick > 0);
    }

    [Fact]
    public void 每个音都先把颤音深度归零()
    {
        var notes = new List<PlacedNote> { new(0, 74, 960, 86), new(960, 76, 960, 86) };

        var result = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs);
        var resets = result.Controls
            .Where(c => c.Controller == Controllers.Modulation && c.Value == 0)
            .ToList();

        // 否则上一个长音的颤音会一直挂着，把后面的短音也带得发抖
        Assert.Equal(2, resets.Count);
    }

    [Fact]
    public void 表情曲线取值合法且覆盖到最后一个音头()
    {
        var notes = new List<PlacedNote>
        {
            new(0, 74, 960, 86), new(960, 76, 960, 86), new(1920, 74, 960, 86),
        };

        var result = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs);
        var expressions = result.Controls
            .Where(c => c.Controller == Controllers.Expression)
            .OrderBy(c => c.Tick)
            .ToList();

        Assert.NotEmpty(expressions);
        Assert.All(expressions, e => Assert.InRange(e.Value, 1, 127));

        // 必须覆盖到最后一个音头，否则尾巴会一直沿用中间的那个值
        Assert.Equal(notes[^1].StartTick, expressions[^1].Tick);
    }

    /// <summary>
    /// 表情（CC11）是连续量，必须平滑过渡。
    /// 若逐音跳变，听感上就是音量在"抽"，会被描述成「演奏中途有中断感」。
    /// </summary>
    [Fact]
    public void 表情必须平滑过渡不能一格一跳()
    {
        // 一段大幅跳进的旋律，最容易暴露逐音赋值的问题
        var notes = new List<PlacedNote>
        {
            new(0, 72, 480, 86),        // C5
            new(480, 81, 480, 86),      // A5 —— 大跳
            new(960, 72, 480, 86),      // C5
            new(1440, 81, 480, 86),     // A5
        };

        var result = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs);

        var expressions = result.Controls
            .Where(c => c.Controller == Controllers.Expression)
            .OrderBy(c => c.Tick)
            .ToList();

        Assert.True(expressions.Count >= 2, "至少要给出两个表情点才谈得上过渡");

        for (var i = 0; i + 1 < expressions.Count; i++)
        {
            var jump = Math.Abs(expressions[i + 1].Value - expressions[i].Value);
            Assert.True(
                jump <= 8,
                $"表情在第 {i}→{i + 1} 个点之间跳了 {jump}，听感上会像音量在抽动");
        }
    }

    [Fact]
    public void 表情随旋律走向起伏()
    {
        // 上行与下行各八个音。滑动平均需要足够多的音，走势才不会被抹平。
        int[] line = [60, 64, 67, 72, 76, 79, 84, 88];

        var up = line.Select((midi, i) => new PlacedNote(i * 960L, midi, 960, 86)).ToList();
        var down = line.Reverse().Select((midi, i) => new PlacedNote(i * 960L, midi, 960, 86)).ToList();

        static (int First, int Last) Endpoints(IReadOnlyList<PlacedNote> notes)
        {
            var expressions = Humanizer.HumanizeViolin(notes, Settings, TicksPerMs)
                .Controls.Where(c => c.Controller == Controllers.Expression)
                .OrderBy(c => c.Tick)
                .ToList();

            return (expressions[0].Value, expressions[^1].Value);
        }

        var rising = Endpoints(up);
        var falling = Endpoints(down);

        // 旋律走高时张力更大 —— 这是自然的乐句处理
        Assert.True(rising.Last > rising.First, $"上行应渐强，实际 {rising.First} → {rising.Last}");
        Assert.True(falling.Last < falling.First, $"下行应渐弱，实际 {falling.First} → {falling.Last}");
    }

    // ---------- 力度 ----------

    [Fact]
    public void 力度始终被限制在合法范围()
    {
        var loud = new List<PlacedNote> { new(0, 60, 480, 127) };
        var quiet = new List<PlacedNote> { new(0, 60, 480, 1) };

        var loudResult = Humanizer.HumanizePiano(loud, Settings, TicksPerMs);
        var quietResult = Humanizer.HumanizePiano(quiet, Settings, TicksPerMs);

        Assert.All(loudResult, n => Assert.InRange(n.Velocity, 1, 127));
        Assert.All(quietResult, n => Assert.InRange(n.Velocity, 1, 127));
    }

    [Fact]
    public void 时序抖动不会把音推到负时间()
    {
        var notes = new List<PlacedNote> { new(0, 60, 480, 68) };

        var result = Humanizer.HumanizePiano(notes, Settings, TicksPerMs);

        Assert.True(result[0].StartTick >= 0);
    }

    // ---------- 换算 ----------

    [Fact]
    public void 毫秒换算随拍速变化()
    {
        // 76 BPM 下，一毫秒约 0.6 tick
        Assert.Equal(0.608, Humanizer.TicksPerMillisecond(76, 480), precision: 3);

        // 速度翻倍，同样的毫秒数对应两倍的 tick
        Assert.Equal(
            Humanizer.TicksPerMillisecond(76, 480) * 2,
            Humanizer.TicksPerMillisecond(152, 480),
            precision: 6);
    }

    [Fact]
    public void 空输入不炸()
    {
        Assert.Empty(Humanizer.HumanizePiano([], Settings, TicksPerMs));

        var violin = Humanizer.HumanizeViolin([], Settings, TicksPerMs);
        Assert.Empty(violin.Notes);
        Assert.Empty(violin.Controls);
    }
}
