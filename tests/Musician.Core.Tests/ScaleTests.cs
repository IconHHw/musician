using Musician.Core.Theory;
using Xunit;

namespace Musician.Core.Tests;

public class ScaleTests
{
    [Theory]
    [InlineData("D minor", 2, Mode.NaturalMinor)]
    [InlineData("F major", 5, Mode.Major)]
    [InlineData("Bb major", 10, Mode.Major)]
    [InlineData("C", 0, Mode.Major)]              // 省略调式 = 大调
    [InlineData("d minor", 2, Mode.NaturalMinor)]
    [InlineData("A harmonic minor", 9, Mode.HarmonicMinor)]
    public void 解析调号(string text, int tonicPitchClass, Mode mode)
    {
        var scale = Scale.Parse(text);
        Assert.Equal(tonicPitchClass, scale.TonicPitchClass);
        Assert.Equal(mode, scale.Mode);
    }

    [Fact]
    public void D小调的音阶成员正确()
    {
        var scale = Scale.Parse("D minor");

        // D E F G A Bb C
        Assert.True(scale.Contains(62));    // D4
        Assert.True(scale.Contains(64));    // E4
        Assert.True(scale.Contains(65));    // F4
        Assert.True(scale.Contains(67));    // G4
        Assert.True(scale.Contains(69));    // A4
        Assert.True(scale.Contains(70));    // Bb4
        Assert.True(scale.Contains(72));    // C5

        Assert.False(scale.Contains(63));   // Eb4 —— 小调里没有
        Assert.False(scale.Contains(66));   // F#4
        Assert.False(scale.Contains(71));   // B4 —— 自然小调没有导音
    }

    [Fact]
    public void 和声小调升高第七级()
    {
        var scale = Scale.Parse("D harmonic minor");
        Assert.True(scale.Contains(73));    // C#5 —— 导音
        Assert.False(scale.Contains(72));   // C5 被替换掉了
    }

    [Fact]
    public void 级进移位按音阶度数走而不是按半音()
    {
        var scale = Scale.Parse("D minor");       // D E F G A Bb C

        // 在 D 小调里，D 上移一个音级是 E（+2 半音），不是 Eb（+1）
        Assert.Equal(64, scale.TransposeDiatonic(62, 1));

        // 上移两个音级到 F（+3 半音）
        Assert.Equal(65, scale.TransposeDiatonic(62, 2));

        // 上移七个音级正好是一个八度
        Assert.Equal(74, scale.TransposeDiatonic(62, 7));
    }

    [Fact]
    public void 级进移位可以下行并正确跨八度()
    {
        var scale = Scale.Parse("D minor");

        Assert.Equal(60, scale.TransposeDiatonic(62, -1));   // D4 下行一级 → C4
        Assert.Equal(58, scale.TransposeDiatonic(62, -2));   // 再下一级 → Bb3
        Assert.Equal(50, scale.TransposeDiatonic(62, -7));   // 正好下行一个八度
    }

    [Fact]
    public void 调内音吸附不动调外音就近归位()
    {
        var scale = Scale.Parse("D minor");

        Assert.Equal(62, scale.Snap(62));    // 已在调内，原样返回
        Assert.Equal(65, scale.Snap(66));    // F#4 → F4
        Assert.Equal(70, scale.Snap(71));    // B4 → Bb4
    }

    [Fact]
    public void 显示时按习惯拼写调号()
    {
        Assert.Equal("Bb major", Scale.Parse("Bb major").ToString());
        Assert.Equal("D minor", Scale.Parse("D minor").ToString());
        Assert.Equal("F major", Scale.Parse("F").ToString());
    }
}
