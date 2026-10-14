using Musician.Core.Compile;
using Xunit;

namespace Musician.Core.Tests;

public class PianoTexturesTests
{
    private const int TicksPerBar = 1920;     // 4/4
    private const int TicksPerQuarter = 480;
    private const int WaltzBar = 1440;        // 3/4

    private static readonly int[] Voicing = [60, 64, 67];    // C E G
    private const int BassMidi = 48;                          // C3

    private static IReadOnlyList<PlacedNote> Realize(
        string texture, int ticksPerBar = TicksPerBar, int velocity = 68) =>
        PianoTextures.Realize(texture, Voicing, BassMidi, 0, ticksPerBar, TicksPerQuarter, velocity);

    [Fact]
    public void block是低音加整块和弦()
    {
        var notes = Realize("block");

        Assert.Equal(4, notes.Count);                                     // 低音 + 三个和弦音
        Assert.All(notes, n => Assert.Equal(0, n.StartTick));             // 全部同时
        Assert.All(notes, n => Assert.Equal(TicksPerBar, n.DurationTicks));
        Assert.Contains(notes, n => n.Midi == BassMidi);
    }

    [Fact]
    public void sustain_pad不要低音让旋律更突出()
    {
        var notes = Realize("sustain-pad");

        Assert.Equal(3, notes.Count);
        Assert.DoesNotContain(notes, n => n.Midi == BassMidi);
    }

    [Fact]
    public void 分解八分音符一小节八个音()
    {
        var notes = Realize("broken-8th");

        Assert.Equal(8, notes.Count);
        Assert.Equal([48, 60, 64, 67, 48, 60, 64, 67], notes.Select(n => n.Midi));
        Assert.Equal(notes.Select(n => n.StartTick), [0, 240, 480, 720, 960, 1200, 1440, 1680]);
        Assert.All(notes, n => Assert.Equal(240, n.DurationTicks));
    }

    [Fact]
    public void 阿尔贝蒂低音是高音中音高音的循环()
    {
        var notes = Realize("alberti");

        // 低音 → 高音 → 中音 → 高音，这是经典的阿尔贝蒂低音形态
        Assert.Equal([48, 67, 64, 67, 48, 67, 64, 67], notes.Select(n => n.Midi));
    }

    [Fact]
    public void 琶音是整体向上走的()
    {
        var notes = Realize("arpeggio-up");

        Assert.Equal(8, notes.Count);

        // 后半小节整体比前半高一个八度
        var firstHalf = notes.Take(4).Select(n => n.Midi).ToList();
        var secondHalf = notes.Skip(4).Select(n => n.Midi).ToList();

        Assert.Equal(firstHalf.Select(m => m + 12), secondHalf);
    }

    [Fact]
    public void 圆舞曲低音在第一拍()
    {
        var notes = Realize("waltz", WaltzBar);

        // 3/4：低音 1 个 + 后两拍各一个三音和弦
        Assert.Equal(7, notes.Count);

        var downbeat = notes.Where(n => n.StartTick == 0).ToList();
        Assert.Single(downbeat);
        Assert.Equal(BassMidi, downbeat[0].Midi);
    }

    [Fact]
    public void 圆舞曲每个和弦音落在同一拍上()
    {
        var notes = Realize("waltz", WaltzBar);

        var beatTwo = notes.Where(n => n.StartTick == TicksPerQuarter).ToList();
        Assert.Equal(Voicing, beatTwo.Select(n => n.Midi));
    }

    [Fact]
    public void 圆舞曲用在非三拍子时报错而不是静默算出一个错结果()
    {
        // 4/4 下 waltz 能算出音符，但没有音乐意义 —— 静默出错比报错难查得多
        var ex = Assert.Throws<FormatException>(() => Realize("waltz", TicksPerBar));

        Assert.Contains("三拍子", ex.Message);
        Assert.Contains("3/4", ex.Message);       // 给出两条可行出路
    }

    [Fact]
    public void 未知织体报错并列出可用的名字()
    {
        var ex = Assert.Throws<FormatException>(() => Realize("随便写的"));

        Assert.Contains("未知的钢琴织体", ex.Message);
        Assert.Contains("alberti", ex.Message);       // 报错要能指导改正
    }

    [Fact]
    public void 所有织体都在小节内不越界()
    {
        foreach (var texture in PianoTextures.Available)
        {
            var ticksPerBar = texture == "waltz" ? WaltzBar : TicksPerBar;
            var notes = Realize(texture, ticksPerBar);

            Assert.NotEmpty(notes);
            Assert.All(notes, n => Assert.InRange(n.StartTick, 0, ticksPerBar - 1));
            Assert.All(notes, n => Assert.InRange(n.StartTick + n.DurationTicks, 1, ticksPerBar));
            Assert.All(notes, n => Assert.InRange(n.Midi, 0, 127));
        }
    }

    [Fact]
    public void 织体在小节内的位置相对于小节起点()
    {
        var notes = PianoTextures.Realize(
            "broken-8th", Voicing, BassMidi, barStart: 7680, TicksPerBar, TicksPerQuarter, 68);

        Assert.All(notes, n => Assert.InRange(n.StartTick, 7680, 7680 + TicksPerBar - 1));
        Assert.Equal(7680, notes[0].StartTick);
    }

    [Fact]
    public void 和弦音不足时不会产生重复的同刻同音()
    {
        // 只有两个和弦音（比如某些 sus 和弦的简化写法）
        var notes = PianoTextures.Realize(
            "alberti", [60, 64], BassMidi, 0, TicksPerBar, TicksPerQuarter, 68);

        var duplicates = notes
            .GroupBy(n => (n.StartTick, n.Midi))
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }
}
