using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 延迟标定：算清楚"从发出指令到设备真正到位"要多久，以及"设备比画面慢多少"。
///
/// 为什么要单独做这件事：沉浸感最容易被破坏的地方就是**动作和画面差半拍**，
/// 而现在的补偿值只有一根手动滑杆（−200…+200ms / 桥 0…500ms），用户只能瞎试。
/// 这里面其实有一半是**可以算出来的**，另一半只能人测——所以分成两块：
///
/// ① <b>设备侧（可自动）</b>：设备走完一段行程的时间 = 加速段 + 匀速段，受舒适档的
///    速度/加速度上限约束，再加上 TCode 的插值时间。这一段是物理事实，可以算，
///    也可以用仿真验证（见单测：用限速器真跑一遍，比对误差）。
/// ② <b>画面侧（只能人测）</b>：游戏渲染、显示器、VR 合成各有延迟，Hexa 看不到，
///    只能让用户"跟着感觉调"——所以这里只给建议值，不装作能自动测出来。
/// </summary>
public static class LatencyCalibrator
{
    /// <summary>补偿值允许的范围（与设置里的 MotionCalibrationMs 一致）。</summary>
    public const int MinCompensationMs = -200;
    public const int MaxCompensationMs = 200;

    /// <summary>默认测距：一次走 60 个行程点（0–100 尺度），足够看出滞后又不吓人。</summary>
    public const double DefaultTravelDistance = 60;

    /// <summary>
    /// 实测式估算：用**真正的安全限速器**（同一套速度/加速度/加加速度约束和每轴系数）
    /// 把设备从 <paramref name="from"/> 走到 <paramref name="to"/> 模拟一遍，返回耗时（毫秒）。
    ///
    /// 为什么不用解析公式：解析的梯形速度曲线只是"物理下限"，限速器实际是逐帧的
    /// 追击式控制（带停止距离判断和加加速度限制），实测比解析值慢 2 倍以上。
    /// 拿解析值当标定结果会差得离谱，所以这里直接用同一个限速器算——它就是设备真正会走的曲线。
    /// </summary>
    /// <param name="from">起点位置 0–100。</param>
    /// <param name="to">目标位置 0–100。</param>
    /// <param name="profile">舒适档；null = 沉浸。</param>
    /// <param name="axisIndex">轴序号（0=L0…5=R2）：次要轴的速度系数更小，走得更慢。</param>
    /// <param name="interpolationMs">TCode 插值时间（设备自己还会在这段时间内渐变）。</param>
    public static double EstimateTravelMs(
        double from, double to, ComfortProfile? profile = null, int axisIndex = 0, double interpolationMs = 0)
    {
        profile ??= ComfortProfile.Immersive;
        axisIndex = Math.Clamp(axisIndex, 0, 5);
        double start = double.IsFinite(from) ? Math.Clamp(from, 0, 100) : 50;
        double goal = double.IsFinite(to) ? Math.Clamp(to, 0, 100) : 50;

        var limiter = new MotionSafetyLimiter();
        var begin = new double[6];
        var target = new double[6];
        for (int i = 0; i < 6; i++) { begin[i] = 50; target[i] = 50; }
        begin[axisIndex] = start;
        target[axisIndex] = goal;
        limiter.Reset(begin);

        // 量到「走完 90% 行程」为止，而不是「完全稳定在目标上」：
        // 限速器在目标附近会有余摆（这是它的既有特性），量到完全停下来会把余摆的尾巴也算进去，
        // 结果反而随速度上限升高而变长（越快越摆），完全不能反映"手感上的滞后"。
        // 90% 行程对应的是"动作已经基本到位"的那一刻，也正是玩家感知到的时刻。
        const double stepMs = 16;
        double span = goal - start;
        double threshold = Math.Abs(span) < 0.5 ? goal : start + span * 0.9;
        bool rising = span >= 0;
        double elapsed = 0;
        for (int i = 0; i < 2000; i++)
        {
            double value = limiter.Step(target, stepMs / 1000.0, profile)[axisIndex];
            elapsed += stepMs;
            if (rising ? value >= threshold : value <= threshold) break;
        }

        double interpolation = double.IsFinite(interpolationMs) ? Math.Clamp(interpolationMs, 0, 9999) : 0;
        return elapsed + interpolation;
    }

    /// <summary>
    /// 按默认测距估一次：从 20 走到 80（60 个行程点的大行程，两端都留余量），
    /// 省得调用方自己凑参数。
    /// </summary>
    public static double EstimateDefaultTravelMs(ComfortProfile? profile = null, int axisIndex = 0, double interpolationMs = 0)
        => EstimateTravelMs(DefaultTravelFrom, DefaultTravelTo, profile, axisIndex, interpolationMs);

    /// <summary>默认测距的起点/终点（0–100）。</summary>
    public const double DefaultTravelFrom = 20;
    public const double DefaultTravelTo = 80;

    /// <summary>
    /// 建议的「画面与设备的对齐偏移」：设备到位要比"事件发生"晚 travel 毫秒，
    /// 而用户感知到的动作大致发生在行程中段，所以先补一半；剩下的一半交给用户手感微调。
    /// 返回值可直接写进 <see cref="AppSettings.MotionCalibrationMs"/>（负 = 让动作提前）。
    /// </summary>
    public static int SuggestCompensationMs(double travelMs)
    {
        if (!double.IsFinite(travelMs) || travelMs <= 0) return 0;
        return (int)Math.Clamp(Math.Round(-travelMs / 2), MinCompensationMs, MaxCompensationMs);
    }

    /// <summary>
    /// 跟手标定的中位数（用户敲击时刻 − 参考脉冲时刻），抗一次误触。
    /// 返回偏移与离散度（离散度太大说明用户没敲准，界面应提示重测）。
    /// </summary>
    public static (int OffsetMs, double SpreadMs) EstimateFromTaps(
        IReadOnlyList<double> tapOffsetsMs, double tolerableSpreadMs = 90)
    {
        if (tapOffsetsMs == null || tapOffsetsMs.Count == 0) return (0, 0);
        var sorted = tapOffsetsMs.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return (0, 0);
        double median = sorted[sorted.Length / 2];
        double spread = sorted[^1] - sorted[0];
        int offset = (int)Math.Clamp(Math.Round(median), MinCompensationMs, MaxCompensationMs);
        return (offset, spread <= 0 ? 0 : spread);
    }
}
