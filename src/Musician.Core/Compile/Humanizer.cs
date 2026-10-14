namespace Musician.Core.Compile;

/// <summary>人味化参数。</summary>
public sealed record HumanizeSettings
{
    /// <summary>
    /// 关掉之后渲染出的是「未加工」版本。存在的意义是做 A/B 对比 ——
    /// 人味化改的是几毫秒和几个力度单位，不对比根本听不出它到底做了什么。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 随机种子。**固定种子是刻意设计** —— 同一份乐谱每次渲染必须得到完全相同的结果，
    /// 否则改了一个地方之后没法判断听感差异到底来自改动还是来自随机。
    /// </summary>
    public int Seed { get; init; } = 20261007;

    /// <summary>时序抖动上限（毫秒）。真实演奏的偏差大致在这个量级。</summary>
    public double TimingJitterMs { get; init; } = 8;

    /// <summary>力度抖动上限。</summary>
    public int VelocityJitter { get; init; } = 5;

    /// <summary>钢琴和弦的滚奏间隔（毫秒）：每个更高音比前一个稍晚一点。</summary>
    public double ChordRollMs { get; init; } = 4;

    /// <summary>小提琴连奏时前后音的重叠量（毫秒）。</summary>
    public double LegatoOverlapMs { get; init; } = 30;

    /// <summary>超过这个长度的音才加颤音 —— 很短的音真实演奏者也不会揉弦。</summary>
    public long VibratoMinTicks { get; init; } = 480;

    /// <summary>颤音深度（CC1 取值）。</summary>
    public int VibratoDepth { get; init; } = 70;

    /// <summary>颤音渐入所需时间（毫秒）—— 弦乐的揉弦是"揉进去"的，不是一上来就有。</summary>
    public double VibratoRampMs { get; init; } = 200;

    /// <summary>表情曲线的采样间隔（毫秒）。越小越平滑，控制点也越多。</summary>
    public double ExpressionGridMs { get; init; } = 250;

    /// <summary>表情的基础值。</summary>
    public int ExpressionBase { get; init; } = 70;

    /// <summary>表情随音高走势的摆幅（张力越大越响）。</summary>
    public int ExpressionPitchRange { get; init; } = 30;

    /// <summary>表情的全曲拱形摆幅。</summary>
    public int ExpressionArchRange { get; init; } = 20;
}

/// <summary>小提琴轨的处理结果：音符之外还带一串控制器事件。</summary>
public sealed record ViolinResult(IReadOnlyList<PlacedNote> Notes, IReadOnlyList<ControlPoint> Controls);

/// <summary>
/// 把"精确到 tick、力度整齐划一"的音符改造成"像人弹的"。
///
/// 这是整个项目里性价比最高的一步：同样的音符，加了这几毫秒的抖动和力度变化之后，
/// 听感提升远大于在合成器上做的任何调参。
/// </summary>
public static class Humanizer
{
    private const int MinVelocity = 1;
    private const int MaxVelocity = 127;

    /// <summary>
    /// 钢琴：和弦不能完全齐奏（真手弹的和弦有几个毫秒的先后），
    /// 力度和时序也要有微微的起伏。
    /// </summary>
    public static List<PlacedNote> HumanizePiano(
        IReadOnlyList<PlacedNote> notes,
        HumanizeSettings settings,
        double ticksPerMillisecond)
    {
        var random = new Random(settings.Seed);
        var result = new List<PlacedNote>(notes.Count);

        if (!settings.Enabled)
            return [.. notes];

        foreach (var group in notes.GroupBy(note => note.StartTick))
        {
            // 按音高排序：低音先落，高音稍晚 —— 就是手从低往高"滚"上去的样子
            var ordered = group.OrderBy(note => note.Midi).ToList();

            // 抖动对整个和弦取同一个值。若逐音各自抖动，抖动幅度会盖过滚奏间隔，
            // 和弦内部的高低音先后就被打乱了 —— 听起来反而更假。
            var jitter = (long)Math.Round(Jitter(random, settings.TimingJitterMs) * ticksPerMillisecond);

            for (var i = 0; i < ordered.Count; i++)
            {
                var note = ordered[i];

                var roll = (long)Math.Round(i * settings.ChordRollMs * ticksPerMillisecond);

                result.Add(note with
                {
                    StartTick = Math.Max(0, note.StartTick + roll + jitter),
                    Velocity = ClampVelocity(note.Velocity + random.Next(-settings.VelocityJitter, settings.VelocityJitter + 1)),
                });
            }
        }

        // 钢琴同样要防：换和弦时常有共同音停在同一个音高上
        return PreventSamePitchOverlap(result);
    }

    /// <summary>
    /// 小提琴：连奏重叠、换弓重音、长音颤音、按音高走势做表情起伏。
    /// </summary>
    public static ViolinResult HumanizeViolin(
        IReadOnlyList<PlacedNote> notes,
        HumanizeSettings settings,
        double ticksPerMillisecond)
    {
        var ordered = notes.OrderBy(note => note.StartTick).ToList();
        if (ordered.Count == 0)
            return new ViolinResult([], []);

        if (!settings.Enabled)
            return new ViolinResult(ordered, []);

        var random = new Random(settings.Seed + 1);
        var result = new List<PlacedNote>(ordered.Count);
        var controls = new List<ControlPoint>();

        var lowest = ordered.Min(note => note.Midi);
        var highest = ordered.Max(note => note.Midi);

        for (var i = 0; i < ordered.Count; i++)
        {
            var note = ordered[i];
            var previous = i > 0 ? ordered[i - 1] : null;
            var next = i + 1 < ordered.Count ? ordered[i + 1] : null;

            var jitter = (long)Math.Round(Jitter(random, settings.TimingJitterMs) * ticksPerMillisecond);
            var start = Math.Max(0, note.StartTick + jitter);

            var duration = note.DurationTicks;

            // 连奏：下一个音紧接在结尾处开始时，让前一个音多留一点，两音之间不留缝。
            // 但**同音高时不能连** —— 前一个音的止音会把刚起的新音一起掐掉（见下方说明）。
            var samePitchNext = next is { } candidate && candidate.Midi == note.Midi;

            if (!samePitchNext
                && next is { } following
                && Math.Abs(following.StartTick - (note.StartTick + note.DurationTicks)) <= 1)
            {
                duration += (int)Math.Round(settings.LegatoOverlapMs * ticksPerMillisecond);
            }

            var velocity = note.Velocity + random.Next(-settings.VelocityJitter, settings.VelocityJitter + 1);

            // 换弓 / 换句的重音：前面有空隙，或者音程跳进较大
            if (previous is { } before)
            {
                var gap = note.StartTick - (before.StartTick + before.DurationTicks);
                var leap = Math.Abs(note.Midi - before.Midi);
                if (gap > 0 || leap >= 5)
                    velocity += 6;
            }

            result.Add(note with
            {
                StartTick = start,
                DurationTicks = duration,
                Velocity = ClampVelocity(velocity),
            });

            // 颤音：每个音先把深度归零（避免短音被上一个长音的颤音带跑），
            // 长音再渐入到指定深度
            controls.Add(new ControlPoint(start, Controllers.Modulation, 0));
            if (note.DurationTicks >= settings.VibratoMinTicks)
            {
                var ramp = (long)Math.Round(settings.VibratoRampMs * ticksPerMillisecond);
                controls.Add(new ControlPoint(start + ramp, Controllers.Modulation, settings.VibratoDepth));
            }
        }

        // 表情单独走一条平滑曲线，不在这里逐音赋值 —— 见 BuildExpressionCurve 的说明
        controls.AddRange(BuildExpressionCurve(ordered, lowest, highest, ticksPerMillisecond, settings));

        return new ViolinResult(
            PreventSamePitchOverlap(result),
            [.. controls.OrderBy(point => point.Tick)]);
    }

    /// <summary>
    /// 保证同音高的音不重叠，把前一个音截到后一个音的起点。
    ///
    /// **这是一个很容易忽略的 MIDI 陷阱：** 止音（note-off）事件不带力度信息，
    /// 它关掉的是这个音高上所有正在发声的音。所以当前一个音还响着、同音高的新音
    /// 又起了，前一个音的止音会在新音起音之后几毫秒把它一起掐死 ——
    /// 一个本该持续两拍的音可能只响了几十毫秒。
    ///
    /// 时序抖动也可能造成偶发重叠，所以这一步是兜底，两道防线的第二道。
    /// </summary>
    private static List<PlacedNote> PreventSamePitchOverlap(List<PlacedNote> notes)
    {
        var ordered = notes.OrderBy(note => note.StartTick).ThenBy(note => note.Midi).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var note = ordered[i];
            var end = note.StartTick + note.DurationTicks;

            for (var j = i + 1; j < ordered.Count && ordered[j].StartTick < end; j++)
            {
                if (ordered[j].Midi != note.Midi)
                    continue;

                var clamped = ordered[j].StartTick - note.StartTick - 1;
                if (clamped >= 1 && clamped < note.DurationTicks)
                    ordered[i] = note with { DurationTicks = (int)clamped };

                break;
            }
        }

        return ordered;
    }

    /// <summary>
    /// 生成平滑的表情（CC11）曲线。
    ///
    /// **不能逐音直接赋值。** 相邻两个音如果音高有跳进，逐音赋的表情值能差 40 多个
    /// 单位（约 4.5 dB），听感上就是音量在"抽"，会被描述成「演奏中途有中断感」。
    /// 表情是连续量，必须滑过去。
    ///
    /// 做法分三步：给每个音头算一个目标值 → 滑动平均把逐音锯齿抹成缓坡 →
    /// 在时间网格上线性插值，输出足够密的控制点。
    /// </summary>
    private static List<ControlPoint> BuildExpressionCurve(
        IReadOnlyList<PlacedNote> notes,
        int lowest,
        int highest,
        double ticksPerMillisecond,
        HumanizeSettings settings)
    {
        var count = notes.Count;
        var span = Math.Max(1, highest - lowest);

        // 1. 每个音头的原始目标值：音越高张力越大，再叠一层全曲的拱形起伏
        var targets = new double[count];
        for (var i = 0; i < count; i++)
        {
            var contour = (notes[i].Midi - lowest) / (double)span;
            var arch = count > 1 ? Math.Sin(Math.PI * i / (count - 1)) : 0;
            targets[i] = settings.ExpressionBase
                       + settings.ExpressionPitchRange * contour
                       + settings.ExpressionArchRange * arch;
        }

        // 2. 滑动平均，抹掉锯齿
        const int window = 2;
        var smoothed = new double[count];
        for (var i = 0; i < count; i++)
        {
            double sum = 0;
            var weight = 0;
            for (var k = Math.Max(0, i - window); k <= Math.Min(count - 1, i + window); k++)
            {
                sum += targets[k];
                weight++;
            }
            smoothed[i] = sum / weight;
        }

        // 3. 时间网格上线性插值（游标线性推进，不重复扫描）
        var gridTicks = Math.Max(1, (long)Math.Round(settings.ExpressionGridMs * ticksPerMillisecond));
        var start = notes[0].StartTick;
        var end = notes[^1].StartTick;

        var curve = new List<ControlPoint>();
        var cursor = 0;

        for (var tick = start; tick <= end; tick += gridTicks)
        {
            while (cursor + 2 < count && notes[cursor + 1].StartTick < tick)
                cursor++;

            var a = notes[cursor].StartTick;
            var b = notes[Math.Min(cursor + 1, count - 1)].StartTick;
            var value = b > a
                ? smoothed[cursor] + (smoothed[Math.Min(cursor + 1, count - 1)] - smoothed[cursor])
                                     * ((tick - a) / (double)(b - a))
                : smoothed[cursor];

            curve.Add(new ControlPoint(
                tick, Controllers.Expression, Math.Clamp((int)Math.Round(value), 1, 127)));
        }

        // 收尾：最后一个音头也要有表情值，否则尾巴会沿用倒数第二个网格点的值
        curve.Add(new ControlPoint(
            end, Controllers.Expression, Math.Clamp((int)Math.Round(smoothed[^1]), 1, 127)));

        return curve;
    }

    /// <summary>按拍速换算：1 毫秒等于多少 tick。</summary>
    public static double TicksPerMillisecond(double tempo, int ticksPerQuarter) =>
        tempo * ticksPerQuarter / 60000.0;

    private static double Jitter(Random random, double max) => (random.NextDouble() * 2 - 1) * max;

    private static int ClampVelocity(int velocity) => Math.Clamp(velocity, MinVelocity, MaxVelocity);
}
