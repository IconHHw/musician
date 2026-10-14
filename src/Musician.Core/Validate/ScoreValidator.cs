using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Musician.Core.Compile;
using Musician.Core.Spec;
using Musician.Core.Theory;

namespace Musician.Core.Validate;

public enum IssueSeverity
{
    /// <summary>确定的错误：谱面上就是错的，放出去一定难听或根本不对。</summary>
    Error,

    /// <summary>可疑之处：可能是有意为之，但值得确认。</summary>
    Warning,
}

public sealed record ValidationIssue(
    IssueSeverity Severity,
    int Bar,
    string Message,
    string? Section = null)
{
    /// <summary>
    /// 带上段落名。只报"第 10 小节"的话，作者得自己做加法才知道那是哪一段 ——
    /// 而这个加法在改稿时会做好几十遍。
    /// </summary>
    public override string ToString()
    {
        var where = Bar > 0
            ? $"第 {Bar,3} 小节" + (Section is null ? "" : $"（{Section}）")
            : "";

        return $"{(Severity == IssueSeverity.Error ? "✗" : "⚠")}  {where,-24}{Message}";
    }
}

/// <summary>
/// 一个段落的概览。
///
/// 存在的理由：`analyze` 只能看**混音**的能量，看不见音区和密度 ——
/// 而「主题升八度做高潮」「对比要换音区」这些作曲建议，操作的恰恰是音区和密度。
/// 只靠 analyze 的话，反馈闭环只闭了一半。这份摘要补上另一半。
/// </summary>
public sealed record SectionSummary(
    string Name,
    int FirstBar,
    int LastBar,
    int ViolinNotes,
    int? ViolinLow,
    int? ViolinHigh)
{
    public bool HasViolin => ViolinNotes > 0;

    public override string ToString()
    {
        var bars = FirstBar == LastBar ? $"第 {FirstBar} 小节" : $"第 {FirstBar,2}–{LastBar,2} 小节";

        if (!HasViolin)
            return $"  {Name,-9} {bars,-12} 小提琴休止";

        var span = ViolinHigh!.Value - ViolinLow!.Value;
        return $"  {Name,-9} {bars,-12} {ViolinNotes,3} 个音   " +
               $"{Pitch.Format(ViolinLow.Value)}–{Pitch.Format(ViolinHigh.Value)}（跨度 {span} 半音）";
    }
}

/// <summary>
/// 乐理校验器。
///
/// 它检查的是**编译之后的实际音符**，而不是 JSON 本身 —— 因为要连编译器的 bug
/// 一起抓住。之前几轮出的问题（同音高被掐、音量逐音抽动）都是"JSON 看着没问题、
/// 出来的声音有问题"，只校验 JSON 是抓不到那类问题的。
///
/// 校验器挡不住"难听"，但能挡住"错"。它和耳朵是两道互补的防线，不是替代关系。
/// </summary>
public static class ScoreValidator
{
    // 乐器音域（MIDI 音号）
    private const int ViolinLow = 55;      // G3
    private const int ViolinHigh = 93;     // A6
    private const int PianoLow = 21;       // A0
    private const int PianoHigh = 108;     // C8

    /// <summary>小提琴整曲音域跨度小于这个值就显得单调。</summary>
    private const int MonotonySpanThreshold = 12;

    /// <summary>
    /// 判定"落在拍点上"的容差（tick）。
    ///
    /// 人味化会给每个音加几毫秒的时序抖动，所以强拍上的音**不会**精确落在 tick 0。
    /// 如果这里要求严格相等，校验器就会被自己制造出来的抖动骗过去 ——
    /// 它会安静地一个音都不检查，然后报告"没有问题"。那比不检查更糟。
    /// 76 BPM 下 30 tick 约 50 毫秒，远大于抖动幅度，又远小于最小音符时值。
    /// </summary>
    private const long OnBeatToleranceTicks = 30;

    private const int MaxIssuesPerCategory = 8;

    public static IReadOnlyList<ValidationIssue> Validate(MidiFile score, SongSpec spec)
    {
        var issues = new List<ValidationIssue>();
        var ticksPerBar = ScoreCompiler.TicksPerBar(spec.TimeSignature);
        var scale = Scale.Parse(spec.Key);
        var chordMap = BuildChordMap(spec);
        var sections = BuildSectionMap(spec);

        var tracks = score.GetTrackChunks().ToList();
        var piano = NotesOf(tracks, 0);
        var violin = NotesOf(tracks, 1);

        CheckRange(issues, piano, PianoLow, PianoHigh, "钢琴", ticksPerBar, sections);
        CheckRange(issues, violin, ViolinLow, ViolinHigh, "小提琴", ticksPerBar, sections);
        CheckMonophony(issues, violin, ticksPerBar, sections);
        CheckHarmony(issues, violin, scale, chordMap, ticksPerBar, sections);
        CheckUnison(issues, violin, piano, ticksPerBar, sections);
        CheckTessitura(issues, violin, ticksPerBar, sections);

        return issues;
    }

    private static void CheckRange(
        List<ValidationIssue> issues,
        IReadOnlyList<Note> notes,
        int low,
        int high,
        string instrument,
        int ticksPerBar,
        IReadOnlyDictionary<int, string> sections)
    {
        var offenders = notes
            .Where(n => (int)n.NoteNumber < low || (int)n.NoteNumber > high)
            .GroupBy(n => (Bar: BarOf(n.Time, ticksPerBar), Pitch: (int)n.NoteNumber))
            .Select(g => g.First())
            .ToList();

        foreach (var note in offenders.Take(MaxIssuesPerCategory))
        {
            var midi = (int)note.NoteNumber;
            var bar = BarOf(note.Time, ticksPerBar);

            issues.Add(new ValidationIssue(
                IssueSeverity.Error,
                bar,
                $"{instrument}出现 {Pitch.Format(midi)}，超出该乐器音域 {Pitch.Format(low)}–{Pitch.Format(high)}",
                sections.GetValueOrDefault(bar)));
        }

        ReportOverflow(issues, offenders.Count, "音域越界");
    }

    /// <summary>
    /// 小提琴只能一次拉一个音。同时起多个音是最明显的穿帮 ——
    /// 一听就知道不是小提琴，是键盘在模仿小提琴。
    /// </summary>
    private static void CheckMonophony(
        List<ValidationIssue> issues,
        IReadOnlyList<Note> violin,
        int ticksPerBar,
        IReadOnlyDictionary<int, string> sections)
    {
        var chords = violin
            .GroupBy(n => n.Time)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in chords.Take(MaxIssuesPerCategory))
        {
            var pitches = string.Join("、", group.Select(n => Pitch.Format((int)n.NoteNumber)));
            var bar = BarOf(group.Key, ticksPerBar);

            issues.Add(new ValidationIssue(
                IssueSeverity.Error,
                bar,
                $"小提琴同时起了 {group.Count()} 个音（{pitches}）——它是单声部乐器，一次只能拉一个音",
                sections.GetValueOrDefault(bar)));
        }

        ReportOverflow(issues, chords.Count, "小提琴叠音");
    }

    /// <summary>
    /// 和声检查：调外音是否"有理由"。
    ///
    /// 判断逻辑的关键在于**不能只看调号**。D 小调里的 A7 和弦天然需要 C#，
    /// 而 C# 不在自然小调音阶里 —— 它是有理由的调外音。规则是：
    /// 在调内 → 通过；不在调内但是当前和弦的和弦音 → 通过；否则才报警。
    /// </summary>
    private static void CheckHarmony(
        List<ValidationIssue> issues,
        IReadOnlyList<Note> violin,
        Scale scale,
        IReadOnlyDictionary<int, ChordSymbol> chordMap,
        int ticksPerBar,
        IReadOnlyDictionary<int, string> sections)
    {
        var suspicious = new List<(long Tick, int Bar, int Midi, string Reason)>();

        foreach (var note in violin)
        {
            var midi = (int)note.NoteNumber;
            if (scale.Contains(midi)) continue;

            var bar = BarOf(note.Time, ticksPerBar);
            if (chordMap.TryGetValue(bar - 1, out var chord) && chord.PitchClasses().Contains(Pitch.PitchClass(midi)))
                continue;       // 和弦要求的调外音，正当

            suspicious.Add((note.Time, bar, midi, "既不在调内，也不是当前和弦的和弦音"));
        }

        foreach (var (_, bar, midi, reason) in suspicious.Take(MaxIssuesPerCategory))
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning, bar,
                $"小提琴的 {Pitch.Format(midi)} {reason}",
                sections.GetValueOrDefault(bar)));
        }

        ReportOverflow(issues, suspicious.Count, "可疑调外音");

        // 强拍上的音最好是和弦音 —— 否则和声会站不住
        var weakDownbeats = new List<(int Bar, int Midi)>();

        foreach (var note in violin)
        {
            // 容差处理：抖动可能让音稍微落在小节线之前或之后
            var offsetInBar = note.Time % ticksPerBar;
            var onDownbeat = offsetInBar <= OnBeatToleranceTicks
                          || offsetInBar >= ticksPerBar - OnBeatToleranceTicks;

            if (!onDownbeat) continue;

            var bar = BarOf(note.Time, ticksPerBar);
            if (!chordMap.TryGetValue(bar - 1, out var chord)) continue;

            if (!chord.PitchClasses().Contains(Pitch.PitchClass((int)note.NoteNumber)))
                weakDownbeats.Add((bar, (int)note.NoteNumber));
        }

        foreach (var (bar, midi) in weakDownbeats.Take(MaxIssuesPerCategory))
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning, bar,
                $"小节强拍上的 {Pitch.Format(midi)} 不是和弦音，和声可能站不稳",
                sections.GetValueOrDefault(bar)));
        }

        ReportOverflow(issues, weakDownbeats.Count, "强拍非和弦音");
    }

    /// <summary>小提琴和钢琴在同一个音高上同时发声，会产生拍频和相位抵消。</summary>
    private static void CheckUnison(
        List<ValidationIssue> issues,
        IReadOnlyList<Note> violin,
        IReadOnlyList<Note> piano,
        int ticksPerBar,
        IReadOnlyDictionary<int, string> sections)
    {
        var pianoSpans = piano
            .Select(n => (Start: n.Time, End: n.Time + n.Length, Pitch: (int)n.NoteNumber))
            .ToList();

        var clashes = new List<(int Bar, int Midi)>();

        foreach (var note in violin)
        {
            var midi = (int)note.NoteNumber;
            var end = note.Time + note.Length;

            var clash = pianoSpans.Any(p =>
                p.Pitch == midi && p.Start < end && p.End > note.Time);

            if (clash)
                clashes.Add((BarOf(note.Time, ticksPerBar), midi));
        }

        foreach (var (bar, midi) in clashes.Distinct().Take(MaxIssuesPerCategory))
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning, bar,
                $"小提琴与钢琴在 {Pitch.Format(midi)} 上同度重叠，会产生拍频",
                sections.GetValueOrDefault(bar)));
        }

        ReportOverflow(issues, clashes.Count, "同度重叠");
    }

    /// <summary>整曲音域太窄会显得平淡 —— 提示可以考虑做点八度或音区的变化。</summary>
    private static void CheckTessitura(
        List<ValidationIssue> issues,
        IReadOnlyList<Note> violin,
        int ticksPerBar,
        IReadOnlyDictionary<int, string> sections)
    {
        if (violin.Count == 0) return;

        var lowest = violin.Min(n => (int)n.NoteNumber);
        var highest = violin.Max(n => (int)n.NoteNumber);
        var span = highest - lowest;

        if (span < MonotonySpanThreshold)
        {
            var bar = BarOf(violin[0].Time, ticksPerBar);

            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                bar,
                $"小提琴整曲只用了 {span} 个半音的音域（{Pitch.Format(lowest)}–{Pitch.Format(highest)}），" +
                "听感容易平淡；可以在某一段用 octave+1 换个音区",
                sections.GetValueOrDefault(bar)));
        }
    }

    private static void ReportOverflow(List<ValidationIssue> issues, int total, string category)
    {
        var shown = Math.Min(total, MaxIssuesPerCategory);
        if (total > shown)
        {
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning, 0, $"另有 {total - shown} 处{category}未逐条列出"));
        }
    }

    /// <summary>
    /// 逐段落摘要：小提琴有没有在场、有几个音、音区在哪。
    /// 用来验证「对比」是否真的做出来了 —— 相邻两段的音区和音数如果一模一样，
    /// 那就不叫对比。
    /// </summary>
    public static IReadOnlyList<SectionSummary> Summarize(MidiFile score, SongSpec spec)
    {
        var ticksPerBar = ScoreCompiler.TicksPerBar(spec.TimeSignature);
        var violin = NotesOf(score.GetTrackChunks().ToList(), 1);

        var summaries = new List<SectionSummary>();
        var bar = 1;

        foreach (var section in spec.Form)
        {
            var first = bar;
            var last = bar + section.Bars - 1;

            var inSection = violin
                .Where(n =>
                {
                    var at = BarOf(n.Time, ticksPerBar);
                    return at >= first && at <= last;
                })
                .ToList();

            summaries.Add(new SectionSummary(
                section.Section,
                first,
                last,
                inSection.Count,
                inSection.Count > 0 ? inSection.Min(n => (int)n.NoteNumber) : null,
                inSection.Count > 0 ? inSection.Max(n => (int)n.NoteNumber) : null));

            bar = last + 1;
        }

        return summaries;
    }

    /// <summary>第几小节（从 1 数）属于哪个段落。</summary>
    private static Dictionary<int, string> BuildSectionMap(SongSpec spec)
    {
        var map = new Dictionary<int, string>();
        var bar = 1;

        foreach (var section in spec.Form)
        {
            for (var i = 0; i < section.Bars; i++)
                map[bar++] = section.Section;
        }

        return map;
    }

    private static IReadOnlyList<Note> NotesOf(IReadOnlyList<TrackChunk> tracks, int index) =>
        index < tracks.Count ? [.. tracks[index].GetNotes()] : [];

    /// <summary>
    /// tick → 第几小节（从 1 数）。
    ///
    /// 不能直接整除。人味化的抖动会把小节线上的音稍微推到前一小节里，
    /// 整除算出的小节号就会少 1 —— 于是查到**错误的那一小节的和弦**，
    /// 本该干净的地方冒出一堆假警告。靠近小节线时要归到下一小节。
    /// </summary>
    private static int BarOf(long tick, int ticksPerBar)
    {
        var bar = tick / ticksPerBar;
        var offset = tick - bar * ticksPerBar;

        if (offset > ticksPerBar - OnBeatToleranceTicks)
            bar++;

        return (int)bar + 1;
    }

    /// <summary>第几小节（从 0 数）用哪个和弦 —— 从曲式和材料推出来。</summary>
    private static Dictionary<int, ChordSymbol> BuildChordMap(SongSpec spec)
    {
        var map = new Dictionary<int, ChordSymbol>();
        var bar = 0;

        foreach (var section in spec.Form)
        {
            var material = spec.GetMaterial(section.Material);

            for (var i = 0; i < section.Bars; i++)
            {
                map[bar] = ChordSymbol.Parse(material.Chords[i % material.Chords.Count]);
                bar++;
            }
        }

        return map;
    }
}
