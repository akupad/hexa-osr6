namespace Hexa.Services;

public static class MotionInterpolation
{
    public static double SampleLinear(IReadOnlyList<(double X, double Y)> points, double x) =>
        Sample(points, x, smooth: false);

    public static double SamplePchip(IReadOnlyList<(double X, double Y)> points, double x) =>
        Sample(points, x, smooth: true);

    private static double Sample(IReadOnlyList<(double X, double Y)> points, double x, bool smooth)
    {
        if (points.Count == 0) return 0;
        if (points.Count == 1 || x <= points[0].X) return points[0].Y;
        if (x >= points[^1].X) return points[^1].Y;

        int lo = 0;
        int hi = points.Count - 1;
        while (lo + 1 < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (points[mid].X <= x) lo = mid;
            else hi = mid;
        }

        double span = Math.Max(1e-9, points[hi].X - points[lo].X);
        double t = Math.Clamp((x - points[lo].X) / span, 0, 1);
        if (!smooth || points.Count < 3)
            return Lerp(points[lo].Y, points[hi].Y, t);

        double m0 = Tangent(points, lo);
        double m1 = Tangent(points, hi);
        double t2 = t * t;
        double t3 = t2 * t;
        double value = (2 * t3 - 3 * t2 + 1) * points[lo].Y
            + (t3 - 2 * t2 + t) * span * m0
            + (-2 * t3 + 3 * t2) * points[hi].Y
            + (t3 - t2) * span * m1;

        double min = Math.Min(points[lo].Y, points[hi].Y);
        double max = Math.Max(points[lo].Y, points[hi].Y);
        return Math.Clamp(value, min, max);
    }

    private static double Tangent(IReadOnlyList<(double X, double Y)> points, int index)
    {
        if (index == 0) return Secant(points[0], points[1]);
        if (index == points.Count - 1) return Secant(points[^2], points[^1]);

        double left = Secant(points[index - 1], points[index]);
        double right = Secant(points[index], points[index + 1]);
        if (left == 0 || right == 0 || Math.Sign(left) != Math.Sign(right)) return 0;

        double leftSpan = points[index].X - points[index - 1].X;
        double rightSpan = points[index + 1].X - points[index].X;
        double w1 = 2 * rightSpan + leftSpan;
        double w2 = rightSpan + 2 * leftSpan;
        return (w1 + w2) / (w1 / left + w2 / right);
    }

    private static double Secant((double X, double Y) a, (double X, double Y) b) =>
        (b.Y - a.Y) / Math.Max(1e-9, b.X - a.X);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
