namespace Hexa.Services;

/// <summary>
/// 播放时的脚本平滑：把「动作点之间走直线」换成<b>单调三次曲线</b>（Fritsch–Carlson / pchip）。
///
/// 要解决的问题不是"采样太稀"，而是"速度在动作点上是断的"：
/// 脚本写的是「0ms 位置 20 → 200ms 位置 80」这样的点，现在播放器按直线插过去，
/// 到点的瞬间速度从 +300/秒 直接跳成 0（甚至反向 −300/秒）。位移没错，速度是断的，
/// 机械听起来就是"嗒、嗒"。补再密的帧也消不掉这个断点——它是曲线形状问题，不是帧率问题。
///
/// 这个实现的三条硬性约束：
/// ① <b>单调</b>：两个动作点之间只走它们之间，绝不冲过脚本写的极值（普通三次样条会过冲，
///    过冲就是"设备跑到脚本没写的位置"，绝对不行）；
/// ② <b>端点精确</b>：脚本写的每一个动作点，曲线都严格经过（不会"平掉"某个点）；
/// ③ <b>可调</b>：强度 0 = 完全等于现在的直线插值，100 = 完全平滑，中间线性混合。
///
/// 只在播放时生效，不改脚本文件、不影响导出、不影响声音响应。
/// </summary>
public static class ScriptSmoothing
{
    public const double MinStrength = 0;
    public const double MaxStrength = 100;
    public const double DefaultStrength = 100;

    /// <summary>
    /// 预处理一条轨道：算出每个动作点上的曲线斜率（位置 / 毫秒）。
    /// 逐帧采样时只做一次二分查找 + 一次三次插值，开销与现在的直线插值同量级。
    /// </summary>
    public static ScriptSmoothingCurve Prepare(IReadOnlyList<WaveScriptCodec.ActionPoint>? points)
    {
        if (points == null || points.Count == 0) return ScriptSmoothingCurve.Empty;
        // 只有一个动作点（退化输入）：整条轨道就是那个位置，不该被当成"没有轨道"而回中位。
        if (points.Count == 1) return new ScriptSmoothingCurve(points, [0]);

        int count = points.Count;
        var slopes = new double[count];
        var secants = new double[count - 1];
        for (int i = 0; i < count - 1; i++)
        {
            long span = points[i + 1].At - points[i].At;
            secants[i] = span > 0 ? (points[i + 1].Pos - points[i].Pos) / (double)span : 0;
        }

        for (int i = 1; i < count - 1; i++)
        {
            double left = secants[i - 1];
            double right = secants[i];
            // 局部极值（上一下、下一下的换向点）：斜率取 0 —— 曲线在这里是"圆角"，
            // 这就是「设备到端点时自然减速再返回」，而不是硬折返。
            if (left * right <= 0)
            {
                slopes[i] = 0;
                continue;
            }
            double slope = (left + right) / 2;
            // Fritsch–Carlson 限制：斜率不超过相邻割线斜率的 3 倍，否则曲线会在点附近鼓包（越过极值）。
            double limit = 3 * Math.Min(Math.Abs(left), Math.Abs(right));
            if (Math.Abs(slope) > limit) slope = Math.Sign(slope) * limit;
            slopes[i] = slope;
        }

        // 两端点：用三点一侧差分（与第一条割线方向一致），再套同样的限制。
        slopes[0] = EndSlope(points[0], points[1], points.Count > 2 ? points[2] : null, secants[0]);
        slopes[count - 1] = EndSlope(points[count - 1], points[count - 2],
            count > 2 ? points[count - 3] : null, secants[count - 2]);
        return new ScriptSmoothingCurve(points, slopes);
    }

    /// <summary>端点斜率：让曲线从端点出发时的速度与作者写的这一段速度一致（不额外"起手停顿"）。</summary>
    private static double EndSlope(
        WaveScriptCodec.ActionPoint end, WaveScriptCodec.ActionPoint neighbour,
        WaveScriptCodec.ActionPoint? far, double secant)
    {
        if (far == null) return secant;
        long h0 = Math.Max(1, Math.Abs(neighbour.At - end.At));
        long h1 = Math.Max(1, Math.Abs(far.At - neighbour.At));
        double d0 = secant;
        double d1 = (far.Pos - neighbour.Pos) / (double)h1;
        // 走向相反（端点是极值）时保持 0：端点该不该"停"由脚本自己写，这里不硬塞速度。
        if (d0 * d1 <= 0) return 0;
        double slope = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
        if (slope * d0 <= 0) return 0;
        double limit = 3 * Math.Abs(d0);
        return Math.Abs(slope) > limit ? Math.Sign(slope) * limit : slope;
    }
}

/// <summary>一条轨道预处理后的曲线：原始动作点 + 每点斜率。</summary>
public sealed class ScriptSmoothingCurve
{
    public static readonly ScriptSmoothingCurve Empty = new([], []);

    public ScriptSmoothingCurve(
        IReadOnlyList<WaveScriptCodec.ActionPoint> points, IReadOnlyList<double> slopes)
    {
        Points = points;
        Slopes = slopes;
    }

    public IReadOnlyList<WaveScriptCodec.ActionPoint> Points { get; }
    public IReadOnlyList<double> Slopes { get; }

    public bool IsEmpty => Points.Count == 0;

    /// <summary>
    /// 采样：<paramref name="strength"/> 0–100，0 = 直线插值（现在的手感），100 = 完全平滑。
    /// 混合是对两种插值结果做线性混合，所以中间档位也是单调的、也经过每个动作点。
    /// </summary>
    public double Sample(long timeMs, double strength)
    {
        if (IsEmpty) return 50;
        if (Points.Count == 1) return Points[0].Pos;
        double blend = Math.Clamp(
            double.IsFinite(strength) ? strength : ScriptSmoothing.DefaultStrength, 0, 100) / 100.0;

        double linear = SampleLinear(timeMs);
        if (blend <= 0) return linear;

        double curved = SampleCurved(timeMs);
        return linear + (curved - linear) * blend;
    }

    /// <summary>现在的直线插值（与 <c>FunscriptPlayerService.SampleTrack</c> 的行为一致）。</summary>
    public double SampleLinear(long timeMs)
    {
        if (IsEmpty) return 50;
        if (Points.Count == 1) return Points[0].Pos;
        if (timeMs <= Points[0].At) return Points[0].Pos;
        if (timeMs >= Points[^1].At) return Points[^1].Pos;

        int lo = IndexBefore(timeMs);
        var a = Points[lo];
        var b = Points[lo + 1];
        long span = Math.Max(1, b.At - a.At);
        return a.Pos + (b.Pos - a.Pos) * ((double)(timeMs - a.At) / span);
    }

    /// <summary>单调三次 Hermite 插值（两点之间是圆角，经过每个动作点，不越过极值）。</summary>
    public double SampleCurved(long timeMs)
    {
        if (IsEmpty) return 50;
        if (Points.Count == 1) return Points[0].Pos;
        if (timeMs <= Points[0].At) return Points[0].Pos;
        if (timeMs >= Points[^1].At) return Points[^1].Pos;

        int lo = IndexBefore(timeMs);
        var a = Points[lo];
        var b = Points[lo + 1];
        double span = Math.Max(1, b.At - a.At);
        double t = (timeMs - a.At) / span;
        double t2 = t * t;
        double t3 = t2 * t;

        double y0 = a.Pos, y1 = b.Pos;
        double m0 = Slopes[lo] * span;
        double m1 = Slopes[lo + 1] * span;
        double h00 = 2 * t3 - 3 * t2 + 1;
        double h10 = t3 - 2 * t2 + t;
        double h01 = -2 * t3 + 3 * t2;
        double h11 = t3 - t2;
        double value = h00 * y0 + h10 * m0 + h01 * y1 + h11 * m1;

        // 单调插值本来就不会越界，这里只是防坏数据：夹到这一段两端之间，宁可钝一点也不许冲过脚本写的值。
        double low = Math.Min(y0, y1);
        double high = Math.Max(y0, y1);
        return double.IsFinite(value) ? Math.Clamp(value, low, high) : y0;
    }

    /// <summary>二分查找：返回 &lt;= timeMs 的最后一个动作点下标（调用前保证在范围内）。</summary>
    private int IndexBefore(long timeMs)
    {
        int lo = 0, hi = Points.Count - 1;
        while (lo + 1 < hi)
        {
            int mid = (lo + hi) / 2;
            if (Points[mid].At <= timeMs) lo = mid;
            else hi = mid;
        }
        return lo;
    }
}
