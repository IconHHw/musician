namespace Musician.Core.Compile;

/// <summary>
/// 钢琴织体实现器：把一个和弦变成一小节的伴奏音型。
///
/// 这是「让 LLM 只写和弦记号、不写伴奏音符」这条设计的关键落点。
/// 让模型逐音符写出分解和弦，它一定会写歪；但让它从六个名字里挑一个
/// （"这里用分解和弦"、"这里用琶音"），它挑得很好。剩下的展开交给代码 ——
/// 展开规则是确定的，不会出错，而且改起来只改一处。
/// </summary>
public static class PianoTextures
{
    public static readonly IReadOnlyList<string> Available =
    [
        "block", "broken-8th", "alberti", "arpeggio-up", "sustain-pad", "waltz",
    ];

    /// <summary>音型里的一个位置：取低音（-1）或和弦的第几个音，可带八度移位。</summary>
    private readonly record struct Slot(int Source, int OctaveShift = 0);

    private const int Bass = -1;

    /// <param name="voicing">已经过声部连接的和弦音，从低到高。</param>
    /// <param name="bassMidi">左手低音。</param>
    public static IReadOnlyList<PlacedNote> Realize(
        string texture,
        IReadOnlyList<int> voicing,
        int bassMidi,
        long barStart,
        int ticksPerBar,
        int ticksPerQuarter,
        int velocity)
    {
        if (voicing.Count == 0)
            throw new ArgumentException("和弦音为空", nameof(voicing));

        var name = texture.Trim().ToLowerInvariant();

        // 八分音符槽位数 —— 4/4 就是 8 个
        var eighthTicks = ticksPerQuarter / 2;
        var slotsPerBar = Math.Max(1, ticksPerBar / eighthTicks);

        return name switch
        {
            "block" => Held(barStart, ticksPerBar, voicing, bassMidi, velocity, includeBass: true),
            "sustain-pad" => Held(barStart, ticksPerBar, voicing, bassMidi, velocity, includeBass: false),
            "waltz" => WaltzChecked(barStart, ticksPerBar, ticksPerQuarter, voicing, bassMidi, velocity),
            "broken-8th" => Patterned(
                [new Slot(Bass), new Slot(0), new Slot(1), new Slot(2)],
                slotsPerBar, eighthTicks, barStart, voicing, bassMidi, velocity),
            "alberti" => Patterned(
                [new Slot(Bass), new Slot(2), new Slot(1), new Slot(2)],
                slotsPerBar, eighthTicks, barStart, voicing, bassMidi, velocity),
            "arpeggio-up" => Patterned(
                [
                    new Slot(Bass), new Slot(0), new Slot(1), new Slot(2),
                    new Slot(Bass, 1), new Slot(0, 1), new Slot(1, 1), new Slot(2, 1),
                ],
                slotsPerBar, eighthTicks, barStart, voicing, bassMidi, velocity),
            _ => throw new FormatException(
                $"未知的钢琴织体 '{texture}'。可用：{string.Join("、", Available)}"),
        };
    }

    /// <summary>整小节按住：block 是低音 + 和弦，sustain-pad 只要和弦（让旋律更突出）。</summary>
    private static List<PlacedNote> Held(
        long barStart,
        int ticksPerBar,
        IReadOnlyList<int> voicing,
        int bassMidi,
        int velocity,
        bool includeBass)
    {
        var notes = new List<PlacedNote>(voicing.Count + 1);

        if (includeBass)
            notes.Add(new PlacedNote(barStart, bassMidi, ticksPerBar, velocity));

        foreach (var midi in voicing)
            notes.Add(new PlacedNote(barStart, midi, ticksPerBar, velocity));

        return notes;
    }

    /// <summary>
    /// 圆舞曲音型只对三拍子有意义。
    ///
    /// 在别的拍号下它**能算出一个结果**（低音加满其余拍），但那个结果没有音乐意义，
    /// 而且不会报错 —— 属于"静默产生错误输出"，比直接报错难查得多。
    /// 所以这里宁可拒绝。
    /// </summary>
    private static List<PlacedNote> WaltzChecked(
        long barStart, int ticksPerBar, int ticksPerQuarter,
        IReadOnlyList<int> voicing, int bassMidi, int velocity)
    {
        var beatsPerBar = ticksPerBar / ticksPerQuarter;

        if (beatsPerBar != 3)
        {
            throw new FormatException(
                $"waltz 织体只适用于三拍子（每小节 3 拍），当前是每小节 {beatsPerBar} 拍。" +
                "请改用 block / broken-8th / alberti / arpeggio-up / sustain-pad，" +
                "或把 timeSignature 改成 3/4");
        }

        return Waltz(barStart, ticksPerBar, ticksPerQuarter, voicing, bassMidi, velocity);
    }

    /// <summary>低音在第一拍，其余每一拍是整块和弦 —— 典型的三拍子圆舞曲伴奏。</summary>
    private static List<PlacedNote> Waltz(
        long barStart,
        int ticksPerBar,
        int ticksPerQuarter,
        IReadOnlyList<int> voicing,
        int bassMidi,
        int velocity)
    {
        var notes = new List<PlacedNote>
        {
            new(barStart, bassMidi, ticksPerQuarter, velocity),
        };

        for (long tick = ticksPerQuarter; tick < ticksPerBar; tick += ticksPerQuarter)
        {
            foreach (var midi in voicing)
                notes.Add(new PlacedNote(barStart + tick, midi, ticksPerQuarter, velocity));
        }

        return notes;
    }

    /// <summary>按给定的音型循环铺满一小节。</summary>
    private static List<PlacedNote> Patterned(
        IReadOnlyList<Slot> pattern,
        int slotsPerBar,
        int slotTicks,
        long barStart,
        IReadOnlyList<int> voicing,
        int bassMidi,
        int velocity)
    {
        var notes = new List<PlacedNote>(slotsPerBar);

        for (var i = 0; i < slotsPerBar; i++)
        {
            var slot = pattern[i % pattern.Count];

            var midi = slot.Source == Bass
                ? bassMidi
                : voicing[slot.Source % voicing.Count];

            var note = new PlacedNote(
                barStart + (long)i * slotTicks,
                midi + slot.OctaveShift * 12,
                slotTicks,
                velocity);

            // 和弦音不够三个时（比如 sus2 只有三个音，没问题；但万一只有两个音），
            // 循环取音会产生重复，这里去掉同一时刻的重复音
            if (!notes.Any(existing => existing.StartTick == note.StartTick && existing.Midi == note.Midi))
                notes.Add(note);
        }

        return notes;
    }
}
