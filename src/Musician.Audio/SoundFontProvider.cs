namespace Musician.Audio;

/// <summary>
/// 定位音色库文件。路径解析顺序：CLI 参数 → 环境变量 → exe 同级 → 当前目录 → LocalAppData。
/// 音色库约 140MB，不入版本库。
/// </summary>
public static class SoundFontProvider
{
    public const string DefaultFileName = "FluidR3_GM.sf2";
    public const string EnvVarName = "MUSICIAN_SF2";

    public static IReadOnlyList<string> SearchPaths { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, "soundfonts", DefaultFileName),
        Path.Combine(Environment.CurrentDirectory, "assets", "soundfonts", DefaultFileName),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Musician", "soundfonts", DefaultFileName),
    ];

    /// <summary>解析音色库路径；找不到就抛出带全部候选位置的异常。</summary>
    public static string Resolve(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full))
                throw new FileNotFoundException($"--sf2 指定的音色库不存在：{full}");
            return full;
        }

        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        foreach (var candidate in SearchPaths)
            if (File.Exists(candidate))
                return candidate;

        throw new FileNotFoundException(
            $"找不到音色库 {DefaultFileName}（约 140MB，需单独下载）。\n" +
            "已查找以下位置：\n  " + string.Join("\n  ", SearchPaths) + "\n" +
            $"可以：1) 用 --sf2 <路径> 指定；2) 把文件放到上面任一路径；" +
            $"3) 设置环境变量 {EnvVarName}。");
    }
}
