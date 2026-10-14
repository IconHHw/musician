using System.Text;

namespace Musician.Core.Theory;

public enum ChordQuality
{
    Major,
    Minor,
    Diminished,
    Augmented,
    Dominant7,
    Major7,
    Minor7,
    HalfDiminished7,
    Sus2,
    Sus4,
}

/// <summary>
/// 和弦记号，如 "Dm" / "Bb" / "A7" / "Fmaj7" / "Gm7b5"。
///
/// 除了把记号变成音符，还负责**声部连接**：相邻两个和弦自动选总移动量最小的转位。
/// 没有这一步，伴奏会变成一堆平行排列的和弦块，听起来很机械 ——
/// 真实演奏者弹和弦时手指总是尽量少动。
/// </summary>
public sealed class ChordSymbol
{
    private ChordSymbol(int rootPitchClass, ChordQuality quality)
    {
        RootPitchClass = rootPitchClass;
        Quality = quality;
        Intervals = IntervalsOf(quality);
    }

    public int RootPitchClass { get; }

    public ChordQuality Quality { get; }

    /// <summary>相对根音的半音数，从低到高。</summary>
    public IReadOnlyList<int> Intervals { get; }

    public static ChordSymbol Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("和弦记号为为空");

        var s = text.Trim();
        var index = 0;

        var letter = char.ToUpperInvariant(s[index]);
        if (letter is < 'A' or > 'G')
            throw new FormatException($"和弦 '{text}' 应以 A–G 开头");

        var semitone = letter switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            _ => 11,
        };
        index++;

        while (index < s.Length && s[index] is '#' or 'b' or '♯' or '♭')
        {
            semitone += s[index] is '#' or '♯' ? 1 : -1;
            index++;
        }

        var suffix = s[index..];
        var quality = ParseQuality(suffix, text);

        return new ChordSymbol(((semitone % 12) + 12) % 12, quality);
    }

    /// <summary>
    /// 解析和弦后缀。注意 "M7" 是大七和弦、"m7" 是小七和弦 ——
    /// 大小写在这里是有意义的，所以不能先统一转小写再判断。
    /// </summary>
    private static ChordQuality ParseQuality(string suffix, string original)
    {
        if (suffix.Length == 0) return ChordQuality.Major;

        // 先处理大小写敏感的那几个
        switch (suffix)
        {
            case "M" or "maj" or "Maj" or "MAJ" or "Δ":
                return ChordQuality.Major;
            case "M7" or "maj7" or "Maj7" or "MAJ7" or "Δ7":
                return ChordQuality.Major7;
        }

        var t = suffix.ToLowerInvariant();
        return t switch
        {
            "m" or "min" or "-" => ChordQuality.Minor,
            "m7" or "min7" or "-7" => ChordQuality.Minor7,
            "dim" or "°" or "o" => ChordQuality.Diminished,
            "m7b5" or "min7b5" or "ø" or "ø7" or "halfdim" => ChordQuality.HalfDiminished7,
            "aug" or "+" => ChordQuality.Augmented,
            "7" or "dom7" => ChordQuality.Dominant7,
            "sus2" => ChordQuality.Sus2,
            "sus" or "sus4" => ChordQuality.Sus4,
            _ => throw new FormatException(
                $"和弦 '{original}' 的后缀 '{suffix}' 无法识别。" +
                "可用：m（小三）、7（属七）、maj7、m7、dim、aug、sus2、sus4、m7b5"),
        };
    }

    private static int[] IntervalsOf(ChordQuality quality) => quality switch
    {
        ChordQuality.Major => [0, 4, 7],
        ChordQuality.Minor => [0, 3, 7],
        ChordQuality.Diminished => [0, 3, 6],
        ChordQuality.Augmented => [0, 4, 8],
        ChordQuality.Dominant7 => [0, 4, 7, 10],
        ChordQuality.Major7 => [0, 4, 7, 11],
        ChordQuality.Minor7 => [0, 3, 7, 10],
        ChordQuality.HalfDiminished7 => [0, 3, 6, 10],
        ChordQuality.Sus2 => [0, 2, 7],
        ChordQuality.Sus4 => [0, 5, 7],
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "未知和弦性质"),
    };

    /// <summary>原位排列，根音落在 rootMidi。</summary>
    public IReadOnlyList<int> Realize(int rootMidi) =>
        Intervals.Select(interval => rootMidi + interval).ToArray();

    /// <summary>
    /// 在和弦音区 [lowMidi, highMidi] 内挑一个排列：
    /// 优先让每个音离上一个和弦的对应音最近（声部连接），没有参照时取最低的排列。
    /// </summary>
    public static IReadOnlyList<int> VoiceLead(
        IReadOnlyList<int>? previous,
        ChordSymbol chord,
        int lowMidi,
        int highMidi)
    {
        var candidates = EnumerateVoicings(chord, lowMidi, highMidi).ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"和弦 {chord} 在音区 {lowMidi}–{highMidi} 内放不下，请放宽音区或移调");
        }

        if (previous is null || previous.Count == 0)
            return candidates.OrderBy(v => v[0]).First();

        return candidates
            .OrderBy(v => MovementCost(previous, v))
            .ThenBy(v => v[0])
            .First();
    }

    /// <summary>枚举所有能装进指定音区的转位排列。</summary>
    private static IEnumerable<int[]> EnumerateVoicings(ChordSymbol chord, int lowMidi, int highMidi)
    {
        var seen = new HashSet<string>();

        for (var rootMidi = lowMidi - 12; rootMidi <= highMidi + 12; rootMidi++)
        {
            // 根音的音级必须就是和弦的根音音级 —— 否则 Dm 会生成一个根音落在
            // C 上的小三和弦（那就成了 Cm）。漏掉这一句是个很容易犯的错。
            if (Pitch.PitchClass(rootMidi) != chord.RootPitchClass)
                continue;

            for (var inversion = 0; inversion < chord.Intervals.Count; inversion++)
            {
                // 转位：把最低的 inversion 个音整体升八度
                var voicing = chord.Intervals
                    .Select((interval, i) => rootMidi + interval + (i < inversion ? 12 : 0))
                    .OrderBy(n => n)
                    .ToArray();

                if (voicing[0] < lowMidi || voicing[^1] > highMidi)
                    continue;

                // 去重：不同 rootMidi / 转位组合可能得出同一个排列
                var key = string.Join(',', voicing);
                if (seen.Add(key))
                    yield return voicing;
            }
        }
    }

    /// <summary>总移动量：每个目标音到最近参照音的距离之和。越小越平滑。</summary>
    private static int MovementCost(IReadOnlyList<int> from, IReadOnlyList<int> to)
    {
        var total = 0;
        foreach (var target in to)
        {
            var nearest = int.MaxValue;
            foreach (var source in from)
            {
                var distance = Math.Abs(source - target);
                if (distance < nearest) nearest = distance;
            }
            total += nearest;
        }
        return total;
    }

    /// <summary>和弦音的音级集合（用于校验旋律音与和弦是否协和）。</summary>
    public IReadOnlySet<int> PitchClasses()
    {
        var set = new HashSet<int>();
        foreach (var interval in Intervals)
            set.Add((RootPitchClass + interval) % 12);
        return set;
    }

    public override string ToString()
    {
        // 常见降号根音按降号拼写
        var preferFlats = RootPitchClass is 1 or 3 or 8 or 10;
        var builder = new StringBuilder(Pitch.PitchClassName(RootPitchClass, preferFlats));

        builder.Append(Quality switch
        {
            ChordQuality.Major => "",
            ChordQuality.Minor => "m",
            ChordQuality.Diminished => "dim",
            ChordQuality.Augmented => "aug",
            ChordQuality.Dominant7 => "7",
            ChordQuality.Major7 => "maj7",
            ChordQuality.Minor7 => "m7",
            ChordQuality.HalfDiminished7 => "m7b5",
            ChordQuality.Sus2 => "sus2",
            ChordQuality.Sus4 => "sus4",
            _ => "?",
        });

        return builder.ToString();
    }
}
