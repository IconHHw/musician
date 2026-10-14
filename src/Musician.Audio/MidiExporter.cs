using Melanchall.DryWetMidi.Core;

namespace Musician.Audio;

/// <summary>
/// 把编译好的乐谱导出成 .mid 文件。
///
/// 存在的意义：出了问题要能拿出去查。把 MIDI 丢进任何一个 DAW 或 MuseScore，
/// 音符对不对、节奏准不准、踏板落在哪，一眼就能看出来 ——
/// 比在代码里猜快得多。
/// </summary>
public static class MidiExporter
{
    public static void Write(string path, MidiFile score)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        score.Write(full, overwriteFile: true);
    }
}
