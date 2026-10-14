using System.Globalization;

namespace Musician.Core.Theory;

public enum Mode
{
    Major,
    NaturalMinor,
    HarmonicMinor,
}

/// <summary>
/// 音阶。职责有三个：校验某个音是否在调内、把音吸附到调内、
/// 以及**按音级做级进移位**（动机发展里的 sequence±n 靠它）。
///
/// 级进移位必须按「音阶度数」走而不是按半音走 —— 在 C 大调里把 E 上移一个音级
/// 应该得到 F（半音 +1），把 D 上移一个音级应该得到 E（半音 +2）。
/// 按半音硬移会跑出调外，听起来就不像同一首曲子了。
/// </summary>
public sealed class Scale
{
    /// <summary>大调音阶的半音结构。</summary>
    private static readonly int[] MajorOffsets = [0, 2, 4, 5, 7, 9, 11];

    private static readonly int[] NaturalMinorOffsets = [0, 2, 3, 5, 7, 8, 10];

    /// <summary>和声小调：第七级升高，是小调里产生导音（leading tone）的常见做法。</summary>
    private static readonly int[] HarmonicMinorOffsets = [0, 2, 3, 5, 7, 8, 11];

    private readonly int[] _offsets;

    private Scale(int tonicPitchClass, Mode mode)
    {
        TonicPitchClass = tonicPitchClass;
        Mode = mode;
        _offsets = mode switch
        {
            Mode.Major => MajorOffsets,
            Mode.NaturalMinor => NaturalMinorOffsets,
            Mode.HarmonicMinor => HarmonicMinorOffsets,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知调式"),
        };
    }

    public int TonicPitchClass { get; }

    public Mode Mode { get; }

    /// <summary>音阶各级相对主音的半音数。</summary>
    public IReadOnlyList<int> Offsets => _offsets;

    /// <summary>
    /// 解析 "D minor" / "F major" / "Bb" / "c# minor" 这样的调号。
    /// 省略调式时按大调处理。
    /// </summary>
    public static Scale Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("调号为空");

        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tonicText = parts[0];

        // 主音借道 Pitch 解析，但补一个八度好让它接受 "D" / "Bb" 这种不带八度的写法
        if (!Pitch.TryParse(tonicText, out var tonicMidi, out var tonicError)
            && !Pitch.TryParse(tonicText + "4", out tonicMidi, out tonicError))
        {
            throw new FormatException($"调号 '{text}' 的主音无法解析：{tonicError}");
        }

        var mode = Mode.Major;
        if (parts.Length > 1)
        {
            mode = parts[1].ToLowerInvariant() switch
            {
                "major" or "maj" => Mode.Major,
                "minor" or "min" or "m" => Mode.NaturalMinor,
                "harmonicminor" or "harmonic" => Mode.HarmonicMinor,
                _ => throw new FormatException(
                    $"调号 '{text}' 的调式 '{parts[1]}' 无法识别（可用 major / minor / harmonic minor）"),
            };
        }
        else if (parts.Length == 1 && tonicText.Length > 1 && char.IsLower(tonicText[0]))
        {
            // "d minor" 的简写 "dm"：首字母小写视为小调
            mode = Mode.NaturalMinor;
        }

        return new Scale(Pitch.PitchClass(tonicMidi), mode);
    }

    /// <summary>该音高是否属于本调。</summary>
    public bool Contains(int midi) => _offsets.Contains(Pitch.PitchClass(midi - TonicPitchClass));

    /// <summary>把任意音高吸附到最近的调内音。已经在调内就原样返回。</summary>
    public int Snap(int midi)
    {
        if (Contains(midi)) return midi;

        // 往两个方向各找最近的调内音，取距离小的那个
        for (var distance = 1; distance <= 6; distance++)
        {
            if (Contains(midi - distance)) return midi - distance;
            if (Contains(midi + distance)) return midi + distance;
        }

        return midi;    // 理论上到不了这里
    }

    /// <summary>
    /// 按音阶度数移位。degrees 为正是上行，为负是下行。
    /// 例：C 大调里 60(C4) 上移 1 度 → 62(D4)；上移 2 度 → 64(E4)。
    /// </summary>
    public int TransposeDiatonic(int midi, int degrees)
    {
        if (degrees == 0) return midi;

        var (octave, degree) = Decompose(midi);
        var absoluteDegree = octave * 7 + degree + degrees;

        // 用 FloorDiv/FloorMod，负方向移位时才能正确进位
        var newOctave = (int)Math.Floor(absoluteDegree / 7.0);
        var newDegree = absoluteDegree - newOctave * 7;

        return TonicPitchClass + newOctave * 12 + _offsets[newDegree];
    }

    /// <summary>把音高拆成「第几八度 + 音阶第几级」。</summary>
    private (int Octave, int Degree) Decompose(int midi)
    {
        var relative = midi - TonicPitchClass;
        var pitchClass = Pitch.PitchClass(relative);

        // 非调内音先吸附，保证度数有定义
        var degree = Array.IndexOf(_offsets, pitchClass);
        if (degree < 0)
        {
            var snapped = Snap(midi);
            return Decompose(snapped);
        }

        var octave = (int)Math.Round((relative - _offsets[degree]) / 12.0);
        return (octave, degree);
    }

    public override string ToString()
    {
        // 常见降号调（Db / Eb / Ab / Bb）按降号拼写，其余用升号
        var preferFlats = TonicPitchClass is 1 or 3 or 8 or 10;
        var mode = Mode switch
        {
            Mode.Major => "major",
            Mode.NaturalMinor => "minor",
            Mode.HarmonicMinor => "harmonic minor",
            _ => Mode.ToString(),
        };
        return $"{Pitch.PitchClassName(TonicPitchClass, preferFlats)} {mode}";
    }
}
