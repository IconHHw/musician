using Musician.Core.Theory;
using Xunit;

namespace Musician.Core.Tests;

public class ChordSymbolTests
{
    [Theory]
    [InlineData("Dm", 2, ChordQuality.Minor)]
    [InlineData("Bb", 10, ChordQuality.Major)]
    [InlineData("F", 5, ChordQuality.Major)]
    [InlineData("A7", 9, ChordQuality.Dominant7)]
    [InlineData("Fmaj7", 5, ChordQuality.Major7)]
    [InlineData("Gm7", 7, ChordQuality.Minor7)]
    [InlineData("Bm7b5", 11, ChordQuality.HalfDiminished7)]
    [InlineData("C#dim", 1, ChordQuality.Diminished)]
    [InlineData("Gsus4", 7, ChordQuality.Sus4)]
    public void 解析和弦记号(string text, int rootPitchClass, ChordQuality quality)
    {
        var chord = ChordSymbol.Parse(text);
        Assert.Equal(rootPitchClass, chord.RootPitchClass);
        Assert.Equal(quality, chord.Quality);
    }

    /// <summary>
    /// CLAUDE.md 里承诺支持的和弦写法，逐条验证 —— 文档写错会误导演奏者。
    /// </summary>
    [Theory]
    [InlineData("D")] [InlineData("Dmaj")] [InlineData("DM")]              // 大三
    [InlineData("Dm")] [InlineData("Dmin")] [InlineData("D-")]             // 小三
    [InlineData("D7")]                                                     // 属七
    [InlineData("Dmaj7")] [InlineData("DM7")]                              // 大七
    [InlineData("Dm7")] [InlineData("Dmin7")]                              // 小七
    [InlineData("Dm7b5")] [InlineData("Dø")] [InlineData("Dø7")]           // 半减七
    [InlineData("Ddim")] [InlineData("D°")]                                // 减三
    [InlineData("Daug")] [InlineData("D+")]                                // 增三
    [InlineData("Dsus4")] [InlineData("Dsus")] [InlineData("Dsus2")]       // 挂留
    [InlineData("F#m7")] [InlineData("Bbmaj7")]                            // 带升降号
    public void 文档承诺支持和弦写法都能解析(string text)
    {
        var chord = ChordSymbol.Parse(text);
        Assert.NotEmpty(chord.Intervals);
    }

    /// <summary>
    /// CLAUDE.md 里明确声明**不支持**的写法，必须真的报错。
    /// 若哪天真加上了这些语法，这个测试会失败，提醒去更新文档。
    /// </summary>
    [Theory]
    [InlineData("D/F#")]      // 斜杠和弦
    [InlineData("Dadd9")]
    [InlineData("D9")]
    [InlineData("D11")]
    [InlineData("D13")]
    public void 文档声明不支持的写法确实报错(string text)
    {
        var ex = Assert.Throws<FormatException>(() => ChordSymbol.Parse(text));
        Assert.Contains("无法识别", ex.Message);
    }

    [Fact]
    public void 大写M与小写m含义不同()
    {
        Assert.Equal(ChordQuality.Major7, ChordSymbol.Parse("CM7").Quality);
        Assert.Equal(ChordQuality.Minor7, ChordSymbol.Parse("Cm7").Quality);

        Assert.Equal(ChordQuality.Major, ChordSymbol.Parse("CM").Quality);
        Assert.Equal(ChordQuality.Minor, ChordSymbol.Parse("Cm").Quality);
    }

    [Fact]
    public void 无法识别的后缀报错并列出可用写法()
    {
        var ex = Assert.Throws<FormatException>(() => ChordSymbol.Parse("Dxyz"));
        Assert.Contains("无法识别", ex.Message);
        Assert.Contains("sus4", ex.Message);
    }

    [Fact]
    public void 原位排列从根音开始()
    {
        Assert.Equal([62, 65, 69], ChordSymbol.Parse("Dm").Realize(62));       // D F A
        Assert.Equal([58, 62, 65], ChordSymbol.Parse("Bb").Realize(58));       // Bb D F
    }

    [Fact]
    public void 声部连接选择移动总量最小的排列()
    {
        // 上一个和弦：Dm 原位 = D4 F4 A4
        IReadOnlyList<int> previous = [62, 65, 69];

        // 换成 Bb 时，理想排列是 D4 F4 Bb4 —— 只有最高音动了 1 个半音
        var voiced = ChordSymbol.VoiceLead(previous, ChordSymbol.Parse("Bb"), 55, 72);

        Assert.Equal([62, 65, 70], voiced);
    }

    [Fact]
    public void 没有参照时取最低的排列()
    {
        var voiced = ChordSymbol.VoiceLead(null, ChordSymbol.Parse("Dm"), 60, 72);
        Assert.Equal(62, voiced[0]);
    }

    [Fact]
    public void 音区放不下时报错而不是静默产出()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ChordSymbol.VoiceLead(null, ChordSymbol.Parse("Dm"), 100, 101));

        Assert.Contains("放不下", ex.Message);
    }

    [Fact]
    public void 和弦音级集合用于协和校验()
    {
        var dMinor = ChordSymbol.Parse("Dm").PitchClasses();

        Assert.Contains(2, dMinor);     // D
        Assert.Contains(5, dMinor);     // F
        Assert.Contains(9, dMinor);     // A
        Assert.DoesNotContain(4, dMinor);   // E 是三音上方的九音，不属三和弦
    }

    [Fact]
    public void 显示时按习惯拼写和弦名()
    {
        Assert.Equal("Bb", ChordSymbol.Parse("Bb").ToString());
        Assert.Equal("Dm", ChordSymbol.Parse("Dm").ToString());
        Assert.Equal("A7", ChordSymbol.Parse("A7").ToString());
    }
}
