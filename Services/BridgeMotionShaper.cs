namespace Hexa.Services;

/// <summary>
/// 游戏桥的「动作整形」核心：把游戏发来的一维、会抖、可能一步到底的指令，
/// 变成设备做得出、看起来也像样的连续动作。
///
/// 为什么桥需要这个（而脚本播放不太需要）：
/// 脚本是作者按 300–800ms 的间隔排好的动作点，动作本身已经是连贯的；
/// 游戏是**实时**发指令：每 20–50ms 一条、常常只有一根轴、强度还带噪声（帧率抖动、
/// 传感器噪声、mod 的算法抖动）。同一条指令流直接透传给机械，就是"抖 + 平"。
///
/// 两步，都是纯函数、可单测：
/// ① <b>一阶低通</b>（去抖）：按平滑时间常数追目标，越长越柔，代价是少量延迟；
/// ② <b>限速</b>：每个 tick 最多走「舒适档速度上限 × dt」，杜绝"一条指令一步到底"。
/// 关掉平滑 + 限速放宽 = 旧行为（原样透传），所以随时可以退回。
/// </summary>
public static class BridgeMotionShaper
{
    /// <summary>旧行为的速度上限：插值时间下限 60ms 内可以走满整个行程。</summary>
    public const double LegacyMaxSpeedPerSecond = 100.0 / 0.06;

    public const double MinSmoothingMs = 0;
    public const double MaxSmoothingMs = 300;
    public const double DefaultSmoothingMs = 90;

    /// <summary>
    /// 走一个 tick：返回整形后的新位置（0–100）。
    /// </summary>
    /// <param name="current">当前位置 0–100。</param>
    /// <param name="target">目标位置 0–100（游戏最新指令）。</param>
    /// <param name="deltaSeconds">距上一个 tick 的秒数。</param>
    /// <param name="smoothingMs">低通时间常数（毫秒）；0 = 不平滑（等于直接追目标）。</param>
    /// <param name="maxSpeedPerSecond">速度上限（位置单位/秒）。</param>
    public static double Step(
        double current, double target, double deltaSeconds, double smoothingMs, double maxSpeedPerSecond)
    {
        if (!double.IsFinite(current)) current = 50;
        if (!double.IsFinite(target)) target = current;
        double dt = double.IsFinite(deltaSeconds) && deltaSeconds > 0
            ? Math.Clamp(deltaSeconds, 0.001, 0.2)
            : 0;
        if (dt <= 0) return Math.Clamp(current, 0, 100);

        double delta = target - current;

        // ① 一阶低通：dt/τ 的比例逼近。τ 越大，同样的 dt 走得越少 → 越柔、延迟越大。
        double tau = double.IsFinite(smoothingMs) ? Math.Clamp(smoothingMs, 0, MaxSmoothingMs) / 1000.0 : 0;
        if (tau > 0) delta *= Math.Clamp(dt / tau, 0, 1);

        // ② 限速：这一 tick 最多走这么多（和插值时间下限 60ms 的老上限同源，只是换成按舒适档算）
        double maxSpeed = double.IsFinite(maxSpeedPerSecond) && maxSpeedPerSecond > 0
            ? maxSpeedPerSecond
            : LegacyMaxSpeedPerSecond;
        double maxStep = maxSpeed * dt;
        delta = Math.Clamp(delta, -maxStep, maxStep);

        return Math.Clamp(current + delta, 0, 100);
    }
}
