using Musician.Core.Notation;
using Xunit;

namespace Musician.Core.Tests;

public class MotifParserTests
{
    [Fact]
    public void 解析基本动机()
    {
        var events = MotifParser.Parse("D5/q F5/e E5/e D5/h | D5/h A4/h");

        Assert.Equal(6, events.Count);

        Assert.Equal(new MelodyEvent(74, 0, 0, 480), events[0]);      // D5 四分
        Assert.Equal(new MelodyEvent(77, 0, 480, 240), events[1]);    // F5 八分
        Assert.Equal(new MelodyEvent(76, 0, 720, 240), events[2]);    // E5 八分
        Assert.Equal(new MelodyEvent(74, 0, 960, 960), events[3]);    // D5 二分
        Assert.Equal(new MelodyEvent(74, 1, 0, 960), events[4]);      // 第 2 小节 D5 二分
        Assert.Equal(new MelodyEvent(69, 1, 960, 960), events[5]);    // A4 二分
    }

    [Fact]
    public void 小节拍数不足时报错并指出是第几小节()
    {
        // 第 1 小节只有 q + e + e = 2 拍，4/4 下应为 4 拍
        var ex = Assert.Throws<FormatException>(
            () => MotifParser.Parse("D5/q F5/e E5/e | D5/h A4/h"));

        Assert.Contains("第 1 小节", ex.Message);
    }

    [Fact]
    public void 超出小节容量时报错()
    {
        var ex = Assert.Throws<FormatException>(
            () => MotifParser.Parse("D5/w D5/w | D5/w"));

        Assert.Contains("溢出", ex.Message);
    }

    [Fact]
    public void 附点时值正确()
    {
        var events = MotifParser.Parse("D5/q. E5/e D5/h | D5/w");

        Assert.Equal(720, events[0].DurationTicks);      // 附点四分 = 480 × 1.5
        Assert.Equal(240, events[1].DurationTicks);
        Assert.Equal(960, events[2].DurationTicks);
    }

    [Fact]
    public void 复附点时值正确()
    {
        var events = MotifParser.Parse("D5/h.. E5/e | D5/w");

        Assert.Equal(1680, events[0].DurationTicks);     // 960 × (1 + 0.5 + 0.25)
    }

    [Fact]
    public void 休止符解析为空音高()
    {
        var events = MotifParser.Parse("D5/q r/q D5/h | r/w");

        Assert.False(events[0].IsRest);
        Assert.True(events[1].IsRest);
        Assert.Equal(480, events[1].DurationTicks);
        Assert.True(events[3].IsRest);
    }

    [Fact]
    public void 缺少时值时报错()
    {
        var ex = Assert.Throws<FormatException>(
            () => MotifParser.Parse("D5 F5 G5 A5 | D5/w"));

        Assert.Contains("缺少时值", ex.Message);
    }

    [Fact]
    public void 未知时值字母报错并列出可用字母()
    {
        var ex = Assert.Throws<FormatException>(
            () => MotifParser.Parse("D5/x F5/q G5/q A5/q | D5/w"));

        Assert.Contains("无法识别的时值", ex.Message);
        Assert.Contains("四分", ex.Message);
    }

    [Fact]
    public void 非法音名报错并指出所在小节()
    {
        var ex = Assert.Throws<FormatException>(
            () => MotifParser.Parse("D5/q F5/q G5/q H5/q | D5/w"));

        Assert.Contains("第 1 小节", ex.Message);
    }

    [Fact]
    public void 末尾小节可以不写小节线但必须写满()
    {
        // 写满：允许
        Assert.Equal(3, MotifParser.Parse("D5/h A4/h | D5/w").Count);

        // 只写一半：报错，提示补休止符
        var ex = Assert.Throws<FormatException>(
            () => MotifParser.Parse("D5/h A4/h | D5/h"));
        Assert.Contains("休止符", ex.Message);
    }

    [Fact]
    public void 记谱往返一致()
    {
        const string notation = "D5/q F5/e E5/e D5/h | D5/h A4/h";
        var roundTripped = MotifParser.Format(MotifParser.Parse(notation));

        Assert.Equal(notation, roundTripped);
    }
}
