using System.Globalization;
using Musician.Core.Notation;
using Musician.Core.Theory;

namespace Musician.Core.Compile;

/// <summary>
/// 动机发展算子：拿一个短动机，做出它的变体。
///
/// 这是「LLM 写种子、代码做发展」里的"发展"那一半。让模型直接写出四个不同的段落，
/// 它写出来的四段之间往往毫无关系；但如果给它一个动机、让它说"这里做级进模进、
/// 那里升八度"，代码就能保证变体**始终听得出是同一个主题** —— 而这正是"像人写的"
/// 和"随机拼凑"的分界。
///
/// 所有算子都保持小节数不变，和弦进行才能继续对齐。
/// </summary>
public static class MotifDeveloper
{
    public static readonly IReadOnlyList<string> Available =
    [
        "sequence+n", "sequence-n", "octave+n", "octave-n", "invert", "retrograde", "ornament",
    ];

    /// <summary>超过这个长度的音才值得加花（二分音符及以上）。</summary>
    private const int OrnamentMinTicks = MotifParser.TicksPerQuarter * 2;

    public static IReadOnlyList<MelodyEvent> Apply(
        IReadOnlyList<MelodyEvent> motif,
        IReadOnlyList<string> operations,
        Scale scale,
        int ticksPerBar)
    {
        var result = motif;

        foreach (var operation in operations)
            result = ApplyOne(result, operation, scale, ticksPerBar);

        return result;
    }

    private static IReadOnlyList<MelodyEvent> ApplyOne(
        IReadOnlyList<MelodyEvent> motif,
        string operation,
        Scale scale,
        int ticksPerBar)
    {
        var op = operation.Trim().ToLowerInvariant();

        if (op.StartsWith("sequence", StringComparison.Ordinal))
            return Transpose(motif, scale, ParseOffset(op, "sequence"));

        if (op.StartsWith("octave", StringComparison.Ordinal))
            return ShiftOctaves(motif, ParseOffset(op, "octave"));

        return op switch
        {
            "invert" => Invert(motif, scale),
            "retrograde" => Retrograde(motif, ticksPerBar),
            "ornament" => Ornament(motif, scale),
            "none" or "" => motif,
            _ => throw new FormatException(
                $"未知的动机发展手法 '{operation}'。可用：{string.Join("、", Available)}"),
        };
    }

    /// <summary>级进移位：按音阶度数走，所以移完之后仍在调内。</summary>
    private static IReadOnlyList<MelodyEvent> Transpose(
        IReadOnlyList<MelodyEvent> motif, Scale scale, int degrees) =>
        [.. motif.Select(e => e.IsRest
            ? e
            : e with { MidiNote = Guard(scale.TransposeDiatonic(e.MidiNote!.Value, degrees)) })];

    private static IReadOnlyList<MelodyEvent> ShiftOctaves(
        IReadOnlyList<MelodyEvent> motif, int octaves) =>
        [.. motif.Select(e => e.IsRest
            ? e
            : e with { MidiNote = Guard(e.MidiNote!.Value + octaves * 12) })];

    /// <summary>倒影：以第一个音为轴上下翻转。翻完吸附回调内，避免出现调外音。</summary>
    private static IReadOnlyList<MelodyEvent> Invert(IReadOnlyList<MelodyEvent> motif, Scale scale)
    {
        var first = motif.FirstOrDefault(e => !e.IsRest);
        if (first is null) return motif;

        var axis = first.MidiNote!.Value;

        return [.. motif.Select(e => e.IsRest
            ? e
            : e with { MidiNote = Guard(scale.Snap(2 * axis - e.MidiNote!.Value)) })];
    }

    /// <summary>逆行：把整段动机在时间上倒过来（节奏也跟着倒，所以轮廓依然对得上）。</summary>
    private static IReadOnlyList<MelodyEvent> Retrograde(IReadOnlyList<MelodyEvent> motif, int ticksPerBar)
    {
        if (motif.Count == 0) return motif;

        var bars = motif.Max(e => e.BarIndex) + 1;
        var cycleTicks = (long)bars * ticksPerBar;

        return [.. motif.Select(e =>
        {
            var absolute = (long)e.BarIndex * ticksPerBar + e.StartTicksInBar;
            var mirrored = Math.Clamp(cycleTicks - absolute - e.DurationTicks, 0, cycleTicks - e.DurationTicks);

            return e with
            {
                BarIndex = (int)(mirrored / ticksPerBar),
                StartTicksInBar = (int)(mirrored % ticksPerBar),
            };
        })];
    }

    /// <summary>
    /// 加花：把长音一分为二，后半段换成上方邻音。
    /// 单纯重复长音会显得呆板，加个邻音就有了"经过"的感觉。
    /// </summary>
    private static IReadOnlyList<MelodyEvent> Ornament(IReadOnlyList<MelodyEvent> motif, Scale scale)
    {
        var result = new List<MelodyEvent>(motif.Count);

        foreach (var note in motif)
        {
            if (note.IsRest || note.DurationTicks < OrnamentMinTicks)
            {
                result.Add(note);
                continue;
            }

            var firstHalf = note.DurationTicks / 2;

            result.Add(note with { DurationTicks = firstHalf });
            result.Add(note with
            {
                StartTicksInBar = note.StartTicksInBar + firstHalf,
                DurationTicks = note.DurationTicks - firstHalf,
                MidiNote = Guard(scale.TransposeDiatonic(note.MidiNote!.Value, 1)),
            });
        }

        return result;
    }

    /// <summary>解析 "sequence+2" / "octave-1" 末尾的带符号位移量。</summary>
    private static int ParseOffset(string operation, string prefix)
    {
        var tail = operation[prefix.Length..];

        if (tail.Length == 0)
            throw new FormatException($"'{operation}' 缺少位移量，应写成 {prefix}+2 或 {prefix}-1");

        if (!int.TryParse(tail, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset))
            throw new FormatException($"'{operation}' 的位移量 '{tail}' 不是整数");

        return offset;
    }

    /// <summary>
    /// 越界就直接报错，而不是悄悄夹到边界上 ——
    /// 悄悄夹会让"低八度"变成"没变化"，很难查出问题出在哪。
    /// </summary>
    private static int Guard(int midi)
    {
        if (midi is < Pitch.MinMidi or > Pitch.MaxMidi)
        {
            throw new FormatException(
                $"动机发展后得到音高 {midi}，超出 MIDI 音域（{Pitch.MinMidi}–{Pitch.MaxMidi}）。" +
                "多半是位移量太大，改小一点。");
        }

        return midi;
    }
}
