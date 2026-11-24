namespace Hexa.Services;

/// <summary>画面同步「信号怎么了」。必须区分：切窗丢信号 / 画面被挡 / 内容本来静止 / 真退出 / 用户主动停。</summary>
public enum FollowLossKind
{
    None = 0,
    ForegroundLost = 1,   // 窗口还活着，只是不在前台（切窗 / 打字）
    CaptureLost = 2,      // 窗口在，但抓不到帧（被遮挡 / 最小化恢复中）
    ContentStill = 3,     // 画面好好的，就是本来没在动
    TargetExited = 4,     // 进程/窗口真没了
    PausedByUser = 5,     // 用户主动暂停
    StoppedByUser = 6,    // 用户主动关闭同步 / 急停
}

public enum FollowLossResponse
{
    Keep = 0,      // 照旧跟
    Degrade = 1,   // 平滑降到慢速 + 小幅，继续有信号
    Hold = 2,      // 停在最后姿态附近等，不起新动作
    Ease = 3,      // 真退出：缓降回中
    Wait = 4,      // 主动停 / 急停：不动，也不自动重启
}

/// <summary>
/// 把「丢信号」分成可执行的四类响应。原则：
/// - 只有「真退出」才缓降收场；切窗/遮挡只是降级跑，切回来平滑接上；
/// - 内容本来静止不算丢信号，不许当成退出；
/// - 主动暂停 / 关闭 / 急停一律 Wait，绝不自动重启。
/// </summary>
public static class FollowContinuityPolicy
{
    /// <summary>降级时速度乘 0.55、深度乘 0.35（慢速小幅），仍受用户限位和舒适档约束。</summary>
    public const double DegradedSpeedScale = 0.55;
    public const double DegradedDepthScale = 0.35;
    public const double DegradedHz = 0.55;
    /// <summary>恢复时的平滑时间常数（秒）：从降级/停住平滑爬回正常，不跳变。</summary>
    public const double ResumeSeconds = 0.9;

    public static FollowLossResponse Decide(FollowLossKind kind) => kind switch
    {
        FollowLossKind.None => FollowLossResponse.Keep,
        FollowLossKind.ForegroundLost => FollowLossResponse.Degrade,
        FollowLossKind.CaptureLost => FollowLossResponse.Degrade,
        FollowLossKind.ContentStill => FollowLossResponse.Hold,
        FollowLossKind.TargetExited => FollowLossResponse.Ease,
        _ => FollowLossResponse.Wait,
    };

    public static string Describe(FollowLossKind kind, string? boundName = null) => kind switch
    {
        FollowLossKind.ForegroundLost =>
            $"切到别的窗口了：先慢速小幅动着（切回{(string.IsNullOrWhiteSpace(boundName) ? "游戏" : boundName)}就平滑接上）",
        FollowLossKind.CaptureLost =>
            "这块画面这会儿抓不到（被挡住/最小化）：先慢速小幅动着，恢复就接上",
        FollowLossKind.ContentStill => "画面本来没在动：停在这儿等，不当成退出",
        FollowLossKind.TargetExited => "游戏退出了：缓降回中",
        FollowLossKind.PausedByUser => "你按了暂停：保持停住，不会自己重启",
        FollowLossKind.StoppedByUser => "你主动停了：保持停住，不会自己重启",
        _ => "",
    };

    /// <summary>时间平滑（指数逼近）。用于降级/恢复时的速度与深度，避免阶跃。</summary>
    public static double Smooth(double current, double target, double deltaSeconds, double seconds = ResumeSeconds)
    {
        if (!double.IsFinite(current)) return target;
        if (!double.IsFinite(target)) return current;
        double dt = Math.Clamp(deltaSeconds, 0, 0.5);
        double k = 1.0 - Math.Exp(-dt / Math.Max(0.05, seconds));
        return current + (target - current) * Math.Clamp(k, 0, 1);
    }
}
