using Musician.Core.Theory;
using Xunit;

namespace Musician.Core.Tests;

public class PitchTests
{
    [Theory]
    [InlineData("C4", 60)]      // 中央 C
    [InlineData("D5", 74)]
    [InlineData("Bb4", 70)]
    [InlineData("A#4", 70)]     // Bb 的等音写法
    [InlineData("F#5", 78)]
    [InlineData("C-1", 0)]      // MIDI 最低音
    [InlineData("G9", 127)]     // MIDI 最高音
    public void 音名解析为正确的MIDI音号(string text, int expected)
        => Assert.Equal(expected, Pitch.Parse(text));

    [Theory]
    [InlineData("H4")]          // 没有 H 这个音名
    [InlineData("D")]           // 缺八度
    [InlineData("Dx5")]         // 八度位置有非法字符
    [InlineData("C10")]         // 超出 MIDI 上限
    [InlineData("C-2")]         // 低于 MIDI 下限
    [InlineData("")]
    public void 非法音名被拒绝(string text)
        => Assert.False(Pitch.TryParse(text, out _, out _));

    [Theory]
    [InlineData(60, "C4")]
    [InlineData(74, "D5")]
    [InlineData(70, "A#4")]     // 统一输出升号记法
    [InlineData(0, "C-1")]
    [InlineData(127, "G9")]
    public void 音号格式化回音名(int midi, string expected)
        => Assert.Equal(expected, Pitch.Format(midi));

    [Fact]
    public void 音名与音号往返一致()
    {
        for (var midi = 0; midi <= 127; midi++)
            Assert.Equal(midi, Pitch.Parse(Pitch.Format(midi)));
    }

    [Theory]
    [InlineData(60, 0, 4)]      // C4 → 音级 0，第 4 八度
    [InlineData(61, 1, 4)]      // C#4
    [InlineData(69, 9, 4)]      // A4
    [InlineData(74, 2, 5)]      // D5
    public void 音级与八度换算正确(int midi, int pitchClass, int octave)
    {
        Assert.Equal(pitchClass, Pitch.PitchClass(midi));
        Assert.Equal(octave, Pitch.OctaveOf(midi));
    }
}
