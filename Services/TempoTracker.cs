using System.Diagnostics;

namespace Hexa.Services;

/// <summary>
/// 节拍跟踪：从 onset 强度包络里估出 BPM，并锁住拍点相位 —— 也就是"下一拍什么时候来"。
///
/// 为什么需要它：现在的「声音响应」是**事后跟能量**（声音响了才动），所以永远慢半拍。
/// 有了 BPM 与相位，才能把动作**提前排在拍点**上，这才是"跟着音乐演奏"和"跟着音量抽动"的分界。
///
/// 算法（不引入任何新依赖）：
///   ① onset 包络进环形缓冲（6 秒窗口，10ms 帧）；
///   ② 每 0.5 秒对去均值后的包络做自相关，在 40–200 BPM 的滞后区间里找峰值 → BPM；
///   ③ 用最近若干次强起音在拍格上的**相位残差做圆均值** → 相位偏移 φ0；
///   ④ 对外只需两个数：当前拍内相位 φ∈[0,1) 与"到下一拍还有几秒"。
/// </summary>
public sealed class TempoTracker
{
    private const double FrameSeconds = 0.01;   // 与 AudioEventDetector 的 10ms 帧一致
    private const int WindowFrames = 600;       // 6 秒
    private const double MinBpm = 40;
    private const double MaxBpm = 200;
    private const double OnsetThreshold = 0.35; // 多强才算"一次起音"（用于锁相位）
    private const double AnalyzeEverySeconds = 0.5;

    private readonly double[] _envelope = new double[WindowFrames];
    private readonly List<(double Time, double Strength)> _recentOnsets = [];
    private int _head;
    private int _count;
    private double _sinceAnalyze;
    private double _clockSeconds;
    private double _phaseOffset;                // 拍点发生在 (k + _phaseOffset) × 周期
    private bool _phaseValid;

    /// <summary>估出的 BPM；0 ＝ 还没估出来。</summary>
    public double Bpm { get; private set; }

    /// <summary>自相关峰值的归一化强度 0–1，用来判断"这个 BPM 靠不靠谱"。</summary>
    public double Confidence { get; private set; }

    /// <summary>BPM 有了、信心也够，才算锁上（没锁上时调用方应退回能量驱动）。</summary>
    public bool Locked => Bpm > 0 && _phaseValid && Confidence >= 0.30;

    public double BeatPeriodSeconds => Bpm > 0 ? 60.0 / Bpm : 0;

    /// <summary>当前处在拍内什么位置 0–1（0 ＝ 正好在拍点上）；没锁上返回 -1。</summary>
    public double BeatPhase01
    {
        get
        {
            if (!Locked) return -1;
            double x = _clockSeconds / BeatPeriodSeconds - _phaseOffset;
            return x - Math.Floor(x);
        }
    }

    /// <summary>到下一拍还有几秒；没锁上返回 -1。</summary>
    public double SecondsToNextBeat => Locked ? (1 - BeatPhase01) * BeatPeriodSeconds : -1;

    /// <summary>喂一帧 onset 强度（0–1）与帧间隔（秒）。10ms 调一次。</summary>
    public void Push(double onsetStrength, double deltaSeconds)
    {
        double dt = double.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, 0.001, 0.1) : FrameSeconds;
        _clockSeconds += dt;

        _envelope[_head] = double.IsFinite(onsetStrength) ? Math.Clamp(onsetStrength, 0, 1) : 0;
        _head = (_head + 1) % WindowFrames;
        if (_count < WindowFrames) _count++;

        if (onsetStrength >= OnsetThreshold)
        {
            _recentOnsets.Add((_clockSeconds, Math.Clamp(onsetStrength, 0, 1)));
            if (_recentOnsets.Count > 32) _recentOnsets.RemoveAt(0);
        }
        while (_recentOnsets.Count > 0 && _clockSeconds - _recentOnsets[0].Time > 12) _recentOnsets.RemoveAt(0);

        _sinceAnalyze += dt;
        if (_sinceAnalyze >= AnalyzeEverySeconds && _count >= 200)
        {
            _sinceAnalyze = 0;
            Estimate();
        }
    }

    /// <summary>复位（换歌 / 重新开始听时调）。</summary>
    public void Reset()
    {
        Array.Clear(_envelope);
        _recentOnsets.Clear();
        _head = 0;
        _count = 0;
        _sinceAnalyze = 0;
        _clockSeconds = 0;
        _phaseOffset = 0;
        _phaseValid = false;
        Bpm = 0;
        Confidence = 0;
    }

    private void Estimate()
    {
        // ① 按时间顺序摊平环形缓冲，并去均值（自相关前必须去直流）
        var env = new double[_count];
        for (int i = 0; i < _count; i++)
            env[i] = _envelope[((_head - _count + i) % WindowFrames + WindowFrames) % WindowFrames];

        double mean = 0;
        foreach (double v in env) mean += v;
        mean /= env.Length;
        for (int i = 0; i < env.Length; i++) env[i] -= mean;

        double energy = 0;
        foreach (double v in env) energy += v * v;
        if (energy < 1e-6) { Bpm = 0; Confidence = 0; return; }

        // ② 归一化自相关 r(lag) ∈ [-1,1]。
        // 【踩过的坑】一开始写成 sum/(N-lag)——分母随 lag 变小，等于系统性放大长滞后，
        // 于是 120 BPM 被锁到 3 倍周期（40 BPM）。归一化必须除以"能量积的平方根"，不是除以重叠样本数。
        int minLag = Math.Max(2, (int)Math.Round(60.0 / MaxBpm / FrameSeconds));
        int maxLag = Math.Min(env.Length - 1, (int)Math.Round(60.0 / MinBpm / FrameSeconds));

        double Correlation(int lag)
        {
            double sum = 0, tail = 0;
            for (int i = lag; i < env.Length; i++)
            {
                sum += env[i] * env[i - lag];
                tail += env[i - lag] * env[i - lag];
            }
            double denom = Math.Sqrt(energy * tail);
            return denom > 1e-9 ? sum / denom : 0;
        }

        double maxR = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double r = Correlation(lag);
            if (r > maxR) maxR = r;
        }
        if (maxR <= 0) { Bpm = 0; Confidence = 0; return; }

        // ③ 定"基本周期"：取**达到峰值 90% 的最小 lag**。
        // 周期信号在 lag、2×lag、3×lag 上都有峰，直接取最大会锁到 2 倍/3 倍周期；
        // 取最小强峰才对应最快的那个真实重复单位（倍速问题也从这里解，不再另外消歧）。
        int bestLag = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            if (Correlation(lag) >= maxR * 0.9) { bestLag = lag; break; }
        }
        if (bestLag == 0) bestLag = minLag;

        Confidence = Math.Clamp(maxR, 0, 1);

        double period = bestLag * FrameSeconds;
        double bpm = 60.0 / period;

        // ④ 相位锁定：最近几次起音落在拍格上的位置做圆均值（对 0/1 附近回绕免疫）
        if (_recentOnsets.Count >= 4)
        {
            double sx = 0, sy = 0, wsum = 0;
            foreach ((double t, double s) in _recentOnsets)
            {
                double ph = t / period - Math.Floor(t / period);
                double ang = ph * 2 * Math.PI;
                sx += Math.Cos(ang) * s;
                sy += Math.Sin(ang) * s;
                wsum += s;
            }
            if (wsum > 1e-6)
            {
                double meanAngle = Math.Atan2(sy / wsum, sx / wsum);
                _phaseOffset = ((meanAngle / (2 * Math.PI)) % 1.0 + 1.0) % 1.0;
                _phaseValid = true;
            }
        }

        Bpm = bpm;
    }
}
