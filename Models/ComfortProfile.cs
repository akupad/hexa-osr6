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
    public static readonly ComfortProfile Gentle = new(
        "gentle", "舒缓", "柔和起步和较低冲击，适合长时间游玩",
        0.9, 170, 900, 6000, 2.2, 0.16);

    public static readonly ComfortProfile Immersive = new(
        "immersive", "沉浸", "响应清晰并保留流动变化，适合游戏",
        1.3, 360, 2400, 18000, 1.0, 0.34);

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
