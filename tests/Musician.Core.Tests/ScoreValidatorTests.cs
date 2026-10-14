using Musician.Core.Compile;
using Musician.Core.Spec;
using Musician.Core.Validate;
using Xunit;

namespace Musician.Core.Tests;

public class ScoreValidatorTests
{
    /// <summary>
    /// 构造一份最小的可编译乐谱：一段材料、两个段落。
    ///
    /// theme 段的小节数 = 和弦个数，这样动机**正好完整播放一遍** ——
    /// 否则同一个问题会在多轮循环里重复出现，断言很难写准。
    /// </summary>
    private static SongSpec Spec(
        string[] chords,
        string motif,
        string key = "D minor",
        string piano = "block",
        string violin = "motif")
    {
        var bars = chords.Length;

        return new SongSpec
        {
            Title = "校验测试",
            Tempo = 76,
            Key = key,
            TimeSignature = "4/4",
            Material = new Dictionary<string, Material>
            {
                ["A"] = new Material { Chords = [.. chords], Motif = motif },
            },
            Form =
            [
                new SectionSpec { Section = "intro", Bars = bars, Material = "A", Violin = "rest", Piano = piano },
                new SectionSpec { Section = "theme", Bars = bars, Material = "A", Violin = violin, Piano = piano },
            ],
        };
    }

    private static IReadOnlyList<ValidationIssue> Validate(SongSpec spec) =>
        ScoreValidator.Validate(ScoreCompiler.Compile(spec), spec);

    private static IEnumerable<string> Messages(SongSpec spec) =>
        Validate(spec).Select(i => i.Message);

    // ---------- 干净的谱子不该有错误 ----------

    [Fact]
    public void 正常的谱子没有错误()
    {
        var spec = Spec(
            ["Dm", "Bb"],
            "D5/q F5/e E5/e D5/h | F5/h D5/h");

        Assert.DoesNotContain(Validate(spec), i => i.Severity == IssueSeverity.Error);
    }

    // ---------- 音域 ----------

    [Fact]
    public void 小提琴超出音域报错并指出小节()
    {
        // B6 = MIDI 95，超出小提琴上限 A6
        var spec = Spec(["Dm"], "B6/w");

        var issues = Validate(spec);
        var error = Assert.Single(issues, i => i.Severity == IssueSeverity.Error);

        Assert.Contains("小提琴", error.Message);
        Assert.Contains("B6", error.Message);
        Assert.Contains("音域", error.Message);
        Assert.InRange(error.Bar, 1, 4);
    }

    [Fact]
    public void 音域内的音不报错()
    {
        // A6 = MIDI 93，正好在上限
        var spec = Spec(["Dm"], "A6/w");

        Assert.DoesNotContain(Validate(spec), i => i.Message.Contains("超出该乐器音域"));
    }

    // ---------- 和声：调外音是否"有理由" ----------

    [Fact]
    public void 和弦要求的调外音不算问题()
    {
        // D 小调里的 A7 和弦天然需要 C#，而 C# 不在自然小调音阶里。
        // 只看调号会误报 —— 这是校验器最容易做错的地方。
        var spec = Spec(["A7"], "C#5/w");

        Assert.DoesNotContain(Validate(spec), i => i.Message.Contains("不在调内"));
    }

    [Fact]
    public void 没有理由的调外音给出警告()
    {
        // G#5 既不在 D 小调里，也不是 A7 的和弦音（A7 = A C# E G）
        var spec = Spec(["A7"], "G#5/w");

        // G#5 同时会触发"调外音"和"强拍非和弦音"两条，这里只看前一条
        var warning = Assert.Single(
            Validate(spec), i => i.Message.Contains("G#5") && i.Message.Contains("不在调内"));

        Assert.Equal(IssueSeverity.Warning, warning.Severity);
    }

    [Fact]
    public void 强拍上的非和弦音给出警告()
    {
        // Bb 和弦 = Bb D F，E5 不是它的和弦音，却放在小节强拍上
        var spec = Spec(["Bb"], "E5/h D5/h");

        var warning = Assert.Single(
            Validate(spec), i => i.Message.Contains("强拍") && i.Message.Contains("E5"));

        Assert.Equal(IssueSeverity.Warning, warning.Severity);
    }

    [Fact]
    public void 强拍上的和弦音不报警()
    {
        var spec = Spec(["Bb"], "F5/h D5/h");

        Assert.DoesNotContain(Validate(spec), i => i.Message.Contains("强拍"));
    }

    // ---------- 音区单调 ----------

    [Fact]
    public void 音域太窄给出平淡警告()
    {
        // 整曲只用了 D5 和 E5 两个音，跨度 2 个半音
        var spec = Spec(["Dm"], "D5/h E5/h");

        var warning = Assert.Single(Validate(spec), i => i.Message.Contains("平淡"));

        Assert.Contains("半音的音域", warning.Message);
        Assert.Contains("octave+1", warning.Message);       // 给出可行的修改建议
    }

    [Fact]
    public void 音域足够宽不报平淡()
    {
        // A4 到 A5 正好一个八度，达到不"平淡"的门槛
        var spec = Spec(["Dm"], "A4/q D5/q F5/q A5/q");

        Assert.DoesNotContain(Validate(spec), i => i.Message.Contains("平淡"));
    }

    // ---------- 同度重叠 ----------

    [Fact]
    public void 小提琴与钢琴同度重叠给出警告()
    {
        // 钢琴在 Bb 和弦上会奏出 F4（65），小提琴同时拉 F4 会撞在一起
        var spec = Spec(["Bb"], "F4/w");

        var warning = Assert.Single(Validate(spec), i => i.Message.Contains("同度重叠"));

        Assert.Contains("F4", warning.Message);
    }

    // ---------- 汇总输出 ----------

    [Fact]
    public void 问题列表可读且带小节号()
    {
        var spec = Spec(["Dm"], "B6/w");

        var issue = Validate(spec).First(i => i.Severity == IssueSeverity.Error);
        var text = issue.ToString();

        Assert.Contains("第", text);
        Assert.Contains("小节", text);
        Assert.StartsWith("✗", text);
    }

    [Fact]
    public void 空段落不炸()
    {
        var spec = Spec(["Dm"], "D5/w", violin: "rest");

        // 小提琴全程休止时，不该因为"没有音符"而抛异常
        var issues = Validate(spec);
        Assert.NotNull(issues);
    }
}
