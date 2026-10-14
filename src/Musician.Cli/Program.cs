using System.Globalization;
using System.Text;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Musician.Audio;
using Musician.Core;
using Musician.Core.Compile;
using Musician.Core.Spec;
using Musician.Core.Validate;

Console.OutputEncoding = Encoding.UTF8;
return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
            return PrintUsage();

        var command = args[0];
        Options options;

        try
        {
            options = Options.Parse(args[1..]);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"✗ {ex.Message}");
            return 1;
        }

        try
        {
            return command switch
            {
                "render" => RunRender(options),
                "validate" => RunValidate(options),
                "midi" => RunMidi(options),
                "smoke" => RunSmoke(options),
                "analyze" => RunAnalyze(options),
                "help" or "-h" or "--help" => PrintUsage(),
                _ => Unknown(command),
            };
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"✗ {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✗ {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>主命令：读乐谱 → 编译 → 渲染 → 写 WAV。</summary>
    private static int RunRender(Options options)
    {
        var (path, _, score) = LoadAndCompile(options);
        var soundFont = SoundFontProvider.Resolve(options.SoundFont);

        var result = MidiRenderer.Render(soundFont, score);

        Console.WriteLine(
            $"MIDI {result.MidiLength.TotalSeconds:F1}s  →  音频 {result.AudioLength.TotalSeconds:F1}s  " +
            $"原始峰值 {result.PeakBeforeNormalize:F3}  " +
            $"增益 {(result.GainApplied >= 1 ? "+" : "")}{20 * Math.Log10(result.GainApplied):F1} dB");

        var outPath = options.Output
            ?? Path.Combine("out", Path.GetFileNameWithoutExtension(path) + ".wav");

        WavWriter.Write(outPath, result);
        Console.WriteLine($"✓ 已写出 {Path.GetFullPath(outPath)}");

        if (options.Play)
            Playback.Play(result);

        return 0;
    }

    /// <summary>
    /// 只校验，不出声。检查的是**编译之后的实际音符**，所以也能抓住编译器自身的问题。
    /// 有错误时返回非零退出码，方便串进脚本。
    /// </summary>
    private static int RunValidate(Options options)
    {
        var (_, spec, score) = LoadAndCompile(options);

        // 分段摘要：音区与音数。这是 analyze 看不见的两样东西 ——
        // 它只看混音能量，而"对比要换音区""高潮要更高更密"操作的正是这两个量。
        Console.WriteLine();
        Console.WriteLine("段落结构（音区与音数）");
        foreach (var summary in ScoreValidator.Summarize(score, spec))
            Console.WriteLine(summary);

        var issues = ScoreValidator.Validate(score, spec);

        Console.WriteLine();

        if (issues.Count == 0)
        {
            Console.WriteLine("✓ 未发现问题");
            return 0;
        }

        foreach (var issue in issues.OrderBy(i => i.Severity).ThenBy(i => i.Bar))
            Console.WriteLine(issue);

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);

        Console.WriteLine();
        Console.WriteLine(errors > 0
            ? $"✗ {errors} 处错误、{warnings} 处警告"
            : $"⚠ {warnings} 处警告（不算错，但值得确认）");

        return errors > 0 ? 1 : 0;
    }

    /// <summary>只导出 MIDI，不渲染 —— 丢进 DAW 检查音符和踏板用。</summary>
    private static int RunMidi(Options options)
    {
        var (path, _, score) = LoadAndCompile(options);

        var outPath = options.Output
            ?? Path.Combine("out", Path.GetFileNameWithoutExtension(path) + ".mid");

        MidiExporter.Write(outPath, score);
        Console.WriteLine($"✓ 已写出 {Path.GetFullPath(outPath)}");

        return 0;
    }

    private static (string Path, SongSpec Spec, MidiFile Score) LoadAndCompile(Options options)
    {
        var path = options.Positional.FirstOrDefault()
            ?? throw new ArgumentException("需要给出乐谱路径，例如 songs/autumn.json");

        var spec = SongSpec.Load(path);
        Console.WriteLine(
            $"《{spec.Title}》 — {spec.Key}，{spec.Tempo} BPM，{spec.TimeSignature}，{spec.TotalBars} 小节");

        var score = ScoreCompiler.Compile(spec, new HumanizeSettings { Enabled = !options.NoHumanize });
        Console.WriteLine(
            $"编译完成：{score.GetNotes().Count()} 个音符" +
            (options.NoHumanize ? "（未加工 —— 用于 A/B 对比）" : ""));

        return (path, spec, score);
    }

    /// <summary>M0：把硬编码的 Dm–Bb–F–C 走完整条链路，验证接缝与五条约束。</summary>
    private static int RunSmoke(Options options)
    {
        var soundFont = SoundFontProvider.Resolve(options.SoundFont);
        Console.WriteLine($"音色库  {soundFont}");

        Console.WriteLine("编译乐谱 …");
        var score = SmokeScore.Build(useSustainPedal: !options.NoPedal);

        Console.WriteLine("渲染中 …");
        var result = MidiRenderer.Render(soundFont, score);

        Console.WriteLine(
            $"MIDI {result.MidiLength.TotalSeconds:F1}s  →  " +
            $"音频 {result.AudioLength.TotalSeconds:F1}s  " +
            $"原始峰值 {result.PeakBeforeNormalize:F3}  " +
            $"增益 {(result.GainApplied >= 1 ? "+" : "")}{20 * Math.Log10(result.GainApplied):F1} dB");

        var outPath = options.Output ?? Path.Combine("out", "smoke.wav");
        WavWriter.Write(outPath, result);
        Console.WriteLine($"✓ 已写出 {Path.GetFullPath(outPath)}");

        if (options.Play)
            Playback.Play(result);

        return 0;
    }

    /// <summary>把 WAV 的客观指标打出来 —— 给听不见声音的人（或模型）一个反馈通道。</summary>
    private static int RunAnalyze(Options options)
    {
        var path = options.Positional.FirstOrDefault()
            ?? throw new ArgumentException("analyze 需要给出 WAV 路径：musician analyze <file.wav>");

        if (!File.Exists(path))
            throw new FileNotFoundException($"文件不存在：{Path.GetFullPath(path)}");

        WavAnalyzer.Report(path, Console.Out, options.BucketSeconds, options.FromSeconds);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"✗ 未知命令：{command}");
        Console.Error.WriteLine();
        PrintUsage();
        return 1;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            musician —— 乐谱规格 → 钢琴小提琴二重奏 WAV

            用法：
              musician <命令> [选项]

            命令：
              render <spec.json>          乐谱 → WAV（主命令）
              validate <spec.json>        只做乐理校验，不出声（有错误时退出码非零）
              midi   <spec.json>          乐谱 → .mid，丢进 DAW 检查用
              analyze <file.wav>          打印峰值/RMS/能量包络，检查静音与尾音
              smoke                       渲染一段硬编码的 Dm–Bb–F–C，验证音频链路

            选项：
              -o, --out <路径>   输出路径（默认 out/<乐谱名>.wav 或 .mid）
                  --sf2 <路径>   指定音色库，覆盖自动查找
                  --play         渲染完成后立即播放
                  --no-humanize  关闭人味化，渲染"未加工"版本用于 A/B 对比
            """);
        return 0;
    }
}

internal sealed class Options
{
    public string? Output { get; private set; }
    public string? SoundFont { get; private set; }
    public bool Play { get; private set; }
    public bool NoPedal { get; private set; }
    public bool NoHumanize { get; private set; }

    /// <summary>analyze 的包络精度（秒）。默认自适应。</summary>
    public double? BucketSeconds { get; private set; }

    /// <summary>analyze 只显示这个时间之后的部分。</summary>
    public double? FromSeconds { get; private set; }
    public List<string> Positional { get; } = [];

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--out":
                    options.Output = TakeValue(args, ref i);
                    break;
                case "--sf2":
                    options.SoundFont = TakeValue(args, ref i);
                    break;
                case "--play":
                    options.Play = true;
                    break;
                case "--no-pedal":
                    options.NoPedal = true;
                    break;
                case "--no-humanize":
                    options.NoHumanize = true;
                    break;
                case "--bucket":
                    options.BucketSeconds = TakeNumber(args, ref i);
                    break;
                case "--from":
                    options.FromSeconds = TakeNumber(args, ref i);
                    break;
                default:
                    // 以 - 开头的当未知选项报错，其余当位置参数
                    if (args[i].StartsWith('-'))
                        throw new ArgumentException($"无法识别的参数：{args[i]}");
                    options.Positional.Add(args[i]);
                    break;
            }
        }

        return options;
    }

    private static string TakeValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"参数 {args[i]} 缺少取值");
        return args[++i];
    }

    private static double TakeNumber(string[] args, ref int i)
    {
        var name = args[i];
        var text = TakeValue(args, ref i);

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"参数 {name} 的取值 '{text}' 不是数字");

        return value;
    }
}
