using System;

namespace Hexa.Services;

/// <summary>
/// 自动扭转（R0 跟 L0 联动）的共享小助手：Lovense、游戏桥、画面同步三处共用一份逻辑。
/// 规则：近 2 秒内收到过显式旋转命令（Lovense Rotate / 桥 RotateCmd）→ 听游戏的；
/// 否则 R0 = 50 ± amp × (L0 偏离中心的归一化量)，走各路径自己的插值，尊重 R0 限位/端点。
/// </summary>
public sealed class AutoTwistLink
{
    private DateTime _lastExplicitRotateUtc = DateTime.MinValue;

    /// <summary>近 2 秒内是否收到过显式旋转命令。</summary>
    public bool ExplicitRotateActive(DateTime nowUtc)
        => _lastExplicitRotateUtc != DateTime.MinValue && (nowUtc - _lastExplicitRotateUtc).TotalSeconds <= 2.0;

    public void MarkExplicitRotate(DateTime nowUtc) => _lastExplicitRotateUtc = nowUtc;

    /// <summary>
    /// 联动后的 R0 轴值：50 ± amp% × (L0 偏离中心 0..50 的归一化量)，再夹进用户已设的 R0 端点区间。
    /// L0 轴值 0=最高（抬满），100=最低（到底），所以插入（L0 大）转一边、拔出（L0 小）转回。
    /// </summary>
    public static double ComputeLinked(double l0AxisValue, double amplitudePercent, int rotateTopPercent, int rotateDeepPercent)
    {
        double lo = Math.Min(rotateTopPercent, rotateDeepPercent);
        double hi = Math.Max(rotateTopPercent, rotateDeepPercent);
        double dev = Math.Clamp((l0AxisValue - 50) / 50.0, -1, 1);
        double amp = Math.Clamp(amplitudePercent, 0, 100);
        return Math.Clamp(50 + dev * amp, lo, hi);
    }
}
