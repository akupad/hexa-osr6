namespace Hexa.Services;

/// <summary>
/// 画面整体上下位移估计（单位：像素，正 = 画面内容<b>向下</b>移动）。
/// 只做一件事：拿这一帧和上一帧的「每行亮度和」做互相关，找最佳错位。
/// 为什么用行剖面而不是光流：160×90 的灰度图上做光流要几百微秒到毫秒级，
/// 而行剖面只有 90 个数、互相关 13 个候选位置，代价可以忽略，且对压缩噪声更稳。
/// </summary>
public static class FlowEstimator
{
    /// <summary>亚像素位移：<paramref name="now"/> 与 <paramref name="prev"/> 是逐行亮度和。</summary>
    public static double VerticalShift(ReadOnlySpan<double> now, ReadOnlySpan<double> prev, int maxShift = 6)
    {
        int n = Math.Min(now.Length, prev.Length);
        if (n < 4 || maxShift < 1) return 0;

        double sumNow = 0, sumPrev = 0;
        for (int i = 0; i < n; i++) { sumNow += now[i]; sumPrev += prev[i]; }
        double meanNow = sumNow / n, meanPrev = sumPrev / n;

        double varNow = 0, varPrev = 0;
        for (int i = 0; i < n; i++)
        {
            double a = now[i] - meanNow, b = prev[i] - meanPrev;
            varNow += a * a;
            varPrev += b * b;
        }
        // 整幅一模一样平（黑屏 / 白屏 / 静止纯色）：没有任何可对齐的结构，判 0 而不是瞎猜。
        if (varNow < 1e-6 || varPrev < 1e-6) return 0;

        var scores = new double[2 * maxShift + 1];
        int best = 0;
        double bestScore = double.NegativeInfinity;
        for (int shift = -maxShift; shift <= maxShift; shift++)
        {
            // 内容下移 shift 像素 ⇒ now[y] 对应 prev[y - shift]
            double sum = 0;
            int count = 0;
            for (int y = 0; y < n; y++)
            {
                int q = y - shift;
                if (q < 0 || q >= n) continue;
                sum += (now[y] - meanNow) * (prev[q] - meanPrev);
                count++;
            }
            double score = count == 0 ? 0 : sum / count;
            scores[shift + maxShift] = score;
            if (score > bestScore) { bestScore = score; best = shift; }
        }

        // 抛物线插值取亚像素峰值：20fps 下一帧的位移常常不到 1 像素，整数级会把慢速平移判成 0。
        int index = best + maxShift;
        if (index > 0 && index < scores.Length - 1)
        {
            double y0 = scores[index - 1], y1 = scores[index], y2 = scores[index + 1];
            double denominator = y0 - 2 * y1 + y2;
            if (Math.Abs(denominator) > 1e-9)
                return Math.Clamp(best + 0.5 * (y0 - y2) / denominator, -maxShift, maxShift);
        }
        return best;
    }
}
