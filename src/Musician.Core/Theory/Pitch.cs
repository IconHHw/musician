using System.Globalization;

namespace Musician.Core.Theory;

/// <summary>
/// 音高换算：科学音高记法（Scientific Pitch Notation）↔ MIDI 音号。
///
/// 约定 C4 = 中央 C = MIDI 60，所以 midi = (八度 + 1) × 12 + 半音。
///
/// 之所以让乐谱里写音名（"D5"）而不是直接写数字（74），是因为音名属于乐理知识，
/// 无论人还是 LLM 都更容易写对；数字换算交给这里，这里不会出错。
/// </summary>
public static class Pitch
{
    public const int MiddleC = 60;
    public const int MinMidi = 0;
    public const int MaxMidi = 127;

    /// <summary>把 "D5" / "Bb4" / "F#5" 解析成 MIDI 音号。失败时抛出带原因的异常。</summary>
    public static int Parse(string text)
    {
        if (TryParse(text, out var midi, out var error))
            return midi;
        throw new FormatException(error);
    }

    public static bool TryParse(string text, out int midi, out string error)
    {
        midi = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "音高为空";
            return false;
        }

        var s = text.Trim();
        var index = 0;

        var letter = char.ToUpperInvariant(s[index]);
        if (letter is < 'A' or > 'G')
        {
            error = $"'{text}' 不是合法音高：应以 A–G 开头";
            return false;
        }

        var semitone = letter switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            _ => 11,        // 'B'
        };
        index++;

        // 升降号，允许叠加（## 重升、bb 重降）
        while (index < s.Length && s[index] is '#' or 'b' or '♯' or '♭')
        {
            semitone += s[index] is '#' or '♯' ? 1 : -1;
            index++;
        }

        var octaveText = s[index..];
        if (octaveText.Length == 0)
        {
            error = $"'{text}' 缺少八度：应写成 D5、Bb4 这样（C4 = 中央 C）";
            return false;
        }

        if (!int.TryParse(octaveText, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var octave))
        {
            error = $"'{text}' 的八度 '{octaveText}' 不是整数";
            return false;
        }

        // 先把 MIDI 音号算出来再校验范围，否则负八度会绕过检查
        midi = (octave + 1) * 12 + semitone;
        if (midi is < MinMidi or > MaxMidi)
        {
            error = $"'{text}' 超出 MIDI 音域（换算为 {midi}，允许 {MinMidi}–{MaxMidi}）";
            return false;
        }

        return true;
    }

    private static readonly string[] SharpNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly string[] FlatNames = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    /// <summary>MIDI 音号 → 音名，升号记法（74 → "D5"）。</summary>
    public static string Format(int midi)
    {
        if (midi is < MinMidi or > MaxMidi)
            throw new ArgumentOutOfRangeException(nameof(midi), midi, "超出 MIDI 音域");

        return PitchClassName(PitchClass(midi)) + OctaveOf(midi).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 音级 → 音名（不带八度）。同一个音级有升号/降号两种拼法，
    /// 显示调号或和弦名时要按习惯选，比如 Bb 大调不该显示成 A# 大调。
    /// </summary>
    public static string PitchClassName(int pitchClass, bool preferFlats = false)
    {
        var table = preferFlats ? FlatNames : SharpNames;
        return table[((pitchClass % 12) + 12) % 12];
    }

    /// <summary>音级 0–11，C 为 0。</summary>
    public static int PitchClass(int midi) => ((midi % 12) + 12) % 12;

    /// <summary>所在八度。C4 = 中央 C 时返回 4。</summary>
    public static int OctaveOf(int midi) => midi / 12 - 1;
}
