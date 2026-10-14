using Musician.Core.Theory;

namespace Musician.Core.Notation;

/// <summary>
/// 动机里的一个事件（音符或休止）。位置用「第几小节 + 小节内第几 tick」表示，
/// 而不是绝对 tick —— 这样同一段动机可以被搬到任意小节复用。
/// </summary>
/// <param name="MidiNote">null 表示休止。</param>
public sealed record MelodyEvent(int? MidiNote, int BarIndex, int StartTicksInBar, int DurationTicks)
{
    public bool IsRest => MidiNote is null;
}

/// <summary>
/// 紧凑记谱解析器。
///
/// 形如：<c>"D5/q F5/e E5/e | D5/h A4/h"</c>
///
/// <list type="bullet">
/// <item><c>|</c> 小节分隔（必须用空格隔开）</item>
/// <item><c>D5</c> <c>Bb4</c> <c>F#5</c> 科学音高记法，C4 = 中央 C</item>
/// <item><c>/q</c> <c>/h</c> <c>/e</c> <c>/w</c> <c>/s</c> 四分/二分/八分/全/十六分</item>
/// <item><c>/q.</c> 附点</item>
/// <item><c>r/q</c> 四分休止</item>
/// </list>
///
/// 选这个格式而不是 JSON 对象数组的原因：写一条长旋律时能省 5–10 倍 token，
/// 而 token 数是 LLM 作曲的硬约束。同时它人类可读可改。
///
/// 所有错误都带小节号和出错的那个音 —— 写错的往往是 LLM，而它需要看得懂报错才能改对。
/// </summary>
public static class MotifParser
{
    public const int TicksPerQuarter = 480;

    public static IReadOnlyList<MelodyEvent> Parse(string notation, int beatsPerBar = 4)
    {
        if (string.IsNullOrWhiteSpace(notation))
            throw new FormatException("动机记谱为空");
        if (beatsPerBar <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatsPerBar), beatsPerBar, "每小节拍数必须为正");

        var ticksPerBar = beatsPerBar * TicksPerQuarter;
        var events = new List<MelodyEvent>();
        var bar = 0;
        var position = 0;

        var tokens = notation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (token == "|")
            {
                if (position != ticksPerBar)
                {
                    throw new FormatException(
                        $"第 {bar + 1} 小节只有 {Ticks(ticksPerBar, position)} 拍，应为 {beatsPerBar} 拍 —— " +
                        "检查是不是漏了音符、或者时值算错了");
                }
                bar++;
                position = 0;
                continue;
            }

            var (note, duration) = ParseToken(token, bar + 1);
            events.Add(new MelodyEvent(note, bar, position, duration));
            position += duration;

            if (position > ticksPerBar)
            {
                throw new FormatException(
                    $"第 {bar + 1} 小节在 '{token}' 处溢出：加起来超过 {beatsPerBar} 拍");
            }
        }

        // 收尾：允许末尾省略小节线（刚好填满即可），但不能半途而废
        if (position != 0 && position != ticksPerBar)
        {
            throw new FormatException(
                $"最后一小节（第 {bar + 1} 小节）只有 {Ticks(ticksPerBar, position)} 拍，应为 {beatsPerBar} 拍。" +
                "若想留白，请补上休止符（如 r/q）");
        }

        return events;
    }

    private static (int? Note, int Duration) ParseToken(string token, int barNumber)
    {
        var slash = token.IndexOf('/');
        if (slash < 0)
        {
            throw new FormatException(
                $"第 {barNumber} 小节的 '{token}' 缺少时值：应写成 D5/q 这样（音名/时值）");
        }

        var pitchText = token[..slash];
        var durationText = token[(slash + 1)..];

        var duration = ParseDuration(durationText, token, barNumber);

        // 休止符
        if (pitchText is "r" or "R" or "-")
            return (null, duration);

        if (!Pitch.TryParse(pitchText, out var midi, out var error))
            throw new FormatException($"第 {barNumber} 小节的 '{token}'：{error}");

        return (midi, duration);
    }

    private static int ParseDuration(string text, string token, int barNumber)
    {
        if (text.Length == 0)
            throw new FormatException($"第 {barNumber} 小节的 '{token}' 的时值为空");

        var dots = 0;
        var end = text.Length;
        while (end > 0 && text[end - 1] == '.')
        {
            dots++;
            end--;
        }

        var letter = text[..end];
        var baseTicks = letter switch
        {
            "w" => TicksPerQuarter * 4,
            "h" => TicksPerQuarter * 2,
            "q" => TicksPerQuarter,
            "e" => TicksPerQuarter / 2,
            "s" => TicksPerQuarter / 4,
            _ => throw new FormatException(
                $"第 {barNumber} 小节的 '{token}'：无法识别的时值 '{text}'。" +
                "可用 w 全音符 / h 二分 / q 四分 / e 八分 / s 十六分，后面可加附点"),
        };

        // 附点：每多一个点，就再加前一个附加值的一半（复附点 q.. = 1 + 0.5 + 0.25）
        var factor = 1.0;
        var increment = 0.5;
        for (var d = 0; d < dots; d++)
        {
            factor += increment;
            increment /= 2;
        }

        return (int)Math.Round(baseTicks * factor);
    }

    private static string Ticks(int ticksPerBar, int ticks) =>
        (ticks / (double)TicksPerQuarter).ToString("0.##");

    /// <summary>
    /// 把事件列表写回记谱字符串 —— 用于往返测试和排错时显示。
    /// </summary>
    public static string Format(IEnumerable<MelodyEvent> events, int beatsPerBar = 4)
    {
        var ticksPerBar = beatsPerBar * TicksPerQuarter;
        var byBar = events.GroupBy(e => e.BarIndex).OrderBy(g => g.Key);
        var bars = new List<string>();

        foreach (var group in byBar)
        {
            var parts = group
                .OrderBy(e => e.StartTicksInBar)
                .Select(e => $"{(e.IsRest ? "r" : Pitch.Format(e.MidiNote!.Value))}/{DurationLetter(e.DurationTicks)}");
            bars.Add(string.Join(' ', parts));
        }

        return string.Join(" | ", bars);
    }

    private static string DurationLetter(int ticks)
    {
        var whole = TicksPerQuarter * 4;
        return ticks switch
        {
            var t when t == whole => "w",
            var t when t == whole / 2 => "h",
            var t when t == whole / 4 => "q",
            var t when t == whole / 8 => "e",
            var t when t == whole / 16 => "s",
            var t when t == whole * 3 / 4 => "h.",
            var t when t == whole * 3 / 8 => "q.",
            var t when t == whole * 3 / 16 => "e.",
            _ => ticks.ToString(),
        };
    }
}
