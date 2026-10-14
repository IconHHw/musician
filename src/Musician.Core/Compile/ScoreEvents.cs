namespace Musician.Core.Compile;

/// <summary>时间轴上待放置的一个音符。</summary>
public sealed record PlacedNote(long StartTick, int Midi, int DurationTicks, int Velocity);

/// <summary>
/// 时间轴上的一个控制器事件。
///
/// 小提琴的"像人拉"很大程度上靠这个：颤音深度（CC1）和表情（CC11）都不是音符，
/// 而是连续变化的控制量。只放音符的话，拉出来就是风琴。
/// </summary>
public sealed record ControlPoint(long Tick, int Controller, int Value);

/// <summary>乐器表现力用到的 MIDI 控制器编号。</summary>
public static class Controllers
{
    /// <summary>调制轮。在 SoundFont 音源里普遍被映射成颤音深度。</summary>
    public const int Modulation = 1;

    /// <summary>表情。控制音量，用来做乐句的起伏。</summary>
    public const int Expression = 11;

    /// <summary>延音踏板。</summary>
    public const int SustainPedal = 64;
}
