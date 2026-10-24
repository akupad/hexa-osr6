namespace Hexa.Models;

public sealed record ComfortProfile(
    string Id,
    string Label,
    string Description,
    double MaxIntensity,
    double MaxAxisSpeedPerSecond,
    double MaxAxisAccelerationPerSecond2,
    double MaxAxisJerkPerSecond3,
    double SoftStartSeconds,
    double Variation)
{
    // 加速度/急动度是**软件包络**，不是机器极限。2026-10-20 用正弦目标 60Hz 喂
    // MotionSafetyLimiter 实测（_work/probe_limiter）：舒缓档 900/6000 时相位滞后 161ms，
    // 提到 1350/9000 降到 ~100ms；沉浸档 2400/18000 在速度允许频率（1.36Hz）滞后 270ms，
    // 提到 3600/27000 降到 ~150ms。两者必须一起提——只提 jerk 会让刹车判据更早减速而
    // accel 追不上，实得行程反而从 87% 掉到 60%。
    public static readonly ComfortProfile Gentle = new(
        "gentle", "舒缓", "柔和起步和较低冲击，适合长时间游玩",
        0.9, 170, 1350, 9000, 2.2, 0.16);

    public static readonly ComfortProfile Immersive = new(
        "immersive", "沉浸", "响应清晰并保留流动变化，适合游戏",
        1.3, 360, 3600, 27000, 1.0, 0.34);

    public static readonly ComfortProfile Dynamic = new(
        "dynamic", "动感", "快速、大行程且保留最终机械运动约束",
        1.75, 620, 5200, 42000, 0.45, 0.52);

    public static IReadOnlyList<ComfortProfile> All { get; } = [Gentle, Immersive, Dynamic];

    /// <summary>
    /// 按 id 取档位。用 switch 而不是 LINQ <c>FirstOrDefault</c>：
    /// 这个方法在运动热路径上每帧都会被调用（60Hz 下发 + 音频 20Hz + 桥 20Hz），
    /// LINQ 版本每次都会分配一个闭包和枚举器，白白制造 GC 压力。
    /// </summary>
    public static ComfortProfile Resolve(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "gentle" => Gentle,
        "dynamic" => Dynamic,
        _ => Immersive,
    };
}
