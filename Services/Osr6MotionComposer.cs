using Hexa.Models;

namespace Hexa.Services;

/// <summary>Creates coordinated OSR6 poses around L0 as the primary stroke rhythm.</summary>
public static class Osr6MotionComposer
{
    public static double BaseFrequency(string pattern) => pattern switch
    {
        "slow_sine" => 0.32,
        "gentle_wave" => 0.38,
        "deep_pulse" => 0.56,
        "spiral_tease" => 0.48,
        "edge_swirl" => 0.58,
        "random_micro" => 0.64,
        "organic_flow" => 0.68,
        "game_flow" => 0.82,
        "intense_thrust" => 1.18,
        "climax_burst" => 1.42,
        _ => 0.68,
    };

    // ── 副轴「微差」参数：数值刻意做小，只用来打散「六轴一起往复」 ──────────
    /// <summary>副轴噪声的时间尺度（1/秒）：越小越慢，0.35 大约每 2–3 秒换一次「性格」。</summary>
    private const double AxisNoiseRate = 0.35;

    /// <summary>副轴耦合正弦的相位慢漂移幅度（弧度，±）。</summary>
    private const double AxisPhaseWobbleRadians = 0.45;

    /// <summary>副轴幅度慢漂移幅度（比例，±）。</summary>
    private const double AxisSwingAmount = 0.18;

    /// <summary>「细微变化」里噪声分量的时间尺度与幅度。</summary>
    private const double RandomMicroNoiseRate = 0.55;
    private const double RandomMicroNoiseAmount = 0.30;

    /// <summary>
    /// 兼容入口：<paramref name="timeSeconds"/> 是「真实秒」，内部按该动作的基准频率折算成相位。
    /// 自检与单测用它（同一个动作、同一个时刻，输出与旧实现逐位一致）。
    /// </summary>
    public static double[] Compose(
        string pattern,
        double timeSeconds,
        IReadOnlyList<double> amplitudes,
        double intensity,
        double arousal,
        ComfortProfile profile) =>
        Compose(pattern, timeSeconds * BaseFrequency(pattern), timeSeconds,
            amplitudes, intensity, arousal, profile, noiseSeed: 0);

    /// <summary>
    /// 参数域合成：<b>相位与时间分开</b>。
    ///
    /// <paramref name="phaseCycles"/> 的单位是「圈」（= 物理频率对时间的积分），
    /// <paramref name="slowSeconds"/> 是真实秒（只给慢漂移与噪声用）。
    ///
    /// 为什么要拆开（这是换动作「顿一下」的根因）：
    /// 原实现把两者绑在一起（<c>phase = 2π·秒·基准频率</c>），于是两个动作各自有自己的频率 ——
    /// 交叉淡化时同一时刻两条波形频率差最多 3.7 倍，混出来是拍频信号（一会儿同相一会儿反相、
    /// 幅度忽大忽小），手感就是「卡一下」。改成共用一条相位之后，任何两个动作在淡入淡出时
    /// <b>频率完全相同</b>（只是波形不同），混合结果依然是一个周期信号，没有拍频；
    /// 相位也从不重置，所以切换点位置连续。
    /// </summary>
    /// <param name="noiseSeed">
    /// 副轴噪声种子：同一种子 + 同一时刻 = 同一输出（可复现）；换种子整段「微差」就换一套。
    /// 调用方（自由游玩序列器）用启动时刻派生，所以每次运行不重复、单次运行内可复现。
    /// </param>
    public static double[] Compose(
        string pattern,
        double phaseCycles,
        double slowSeconds,
        IReadOnlyList<double> amplitudes,
        double intensity,
        double arousal,
        ComfortProfile profile,
        int noiseSeed = 0)
    {
        double phase = Math.Tau * phaseCycles;
        double slow = Math.Sin(Math.Tau * slowSeconds * 0.071);
        double variation = Math.Clamp(profile.Variation, 0, 1);

        // 副轴的两条「微差」都来自定种子 fBm 值噪声：
        // ① wobble —— 每根副轴的耦合正弦有自己的慢漂移相位，于是「副轴和主轴同时到达两端」
        //    这种整齐感被打散成「L1 稍晚、R2 再晚一点」；
        // ② swing —— 每根副轴的幅度有自己的慢变化增益，长期听感不再是六根轴等比例缩放。
        // L0 不参与：它是节奏本身，动它会改掉动作风格（与 MotionEngine 里「语汇只叠次要轴」同一条原则）。
        var wobble = new double[6];
        var swing = new double[6];
        swing[0] = 1;
        for (int i = 1; i < 6; i++)
        {
            wobble[i] = AxisPhaseWobbleRadians * Fbm(slowSeconds * AxisNoiseRate + i * 13.7, noiseSeed + i * 977, 2);
            swing[i] = 1 + AxisSwingAmount * Fbm(slowSeconds * AxisNoiseRate * 0.7 + i * 5.1, noiseSeed + i * 613, 2);
        }

        double stroke = pattern switch
        {
            "deep_pulse" => 0.78 * -Math.Cos(phase) + 0.22 * -Math.Cos(phase * 2),
            "spiral_tease" => 0.82 * Math.Sin(phase) + 0.18 * Math.Sin(phase * 0.5),
            "edge_swirl" => 0.68 * Math.Sin(phase) + 0.32 * Math.Sin(phase * 1.5 + slow),
            // 「细微变化」以前是三个固定正弦之和 —— 跑一万次一模一样，其实毫无变化。
            // 现在第三个分量换成定种子 fBm 值噪声：真的每次都不一样，
            // 但同一个种子下逐帧完全可复现（单测与自检能钉住它）。
            // 三个分量系数之和刻意保持 ≤ 1（0.49 + 0.20 + 0.30），免得被下面的 Clamp 削平 ——
            // 削平就是导数不连续，等于给限速器一个台阶。
            "random_micro" => (1 - RandomMicroNoiseAmount) * 0.70 * Math.Sin(phase)
                + 0.20 * Math.Sin(phase * 1.73 + 1.1)
                + RandomMicroNoiseAmount * Fbm(slowSeconds * RandomMicroNoiseRate, noiseSeed + 917, 3),
            "organic_flow" => (1 - variation * 0.55) * Math.Sin(phase)
                + variation * 0.36 * Math.Sin(phase * 0.53 + 0.8)
                + variation * 0.19 * Math.Sin(phase * 1.47 + 2.0),
            "game_flow" => 0.72 * Math.Sin(phase) + 0.19 * Math.Sin(phase * 1.5 + 0.7) + 0.09 * slow,
            "intense_thrust" => 0.84 * -Math.Cos(phase) + 0.16 * -Math.Cos(phase * 2),
            "climax_burst" => 0.76 * -Math.Cos(phase) + 0.24 * Math.Sin(phase * 2.5),
            "gentle_wave" => 0.88 * Math.Sin(phase) + 0.12 * Math.Sin(phase * 0.5 + 0.6),
            _ => Math.Sin(phase),
        };
        stroke = Math.Clamp(stroke, -1, 1);

        // The remaining axes describe one coupled pose, rather than six unrelated oscillators.
        // 「一起动」的机器感原本就来自这里写死的相位偏移；现在每根副轴的偏移都带上自己的慢漂移，
        // 幅度也各带一点慢增益。夹紧放在乘完 swing 之后，波形仍然严格在 ±1 内。
        double surge = Math.Clamp((-0.70 * stroke + 0.30 * Math.Sin(phase * 0.5 + 0.4 + wobble[1])) * swing[1], -1, 1);
        double sway = Math.Clamp((0.72 * Math.Sin(phase * 0.5 + 1.2 + wobble[2]) + 0.28 * Math.Sin(phase + 0.2 + wobble[2])) * swing[2], -1, 1);
        double twist = Math.Clamp((0.70 * Math.Sin(phase + Math.PI / 2 + wobble[3]) + 0.30 * slow) * swing[3], -1, 1);
        double roll = Math.Clamp((-0.64 * sway + 0.36 * Math.Sin(phase + 1.9 + wobble[4])) * swing[4], -1, 1);
        double pitch = Math.Clamp((0.66 * stroke + 0.34 * Math.Sin(phase * 0.5 - 0.6 + wobble[5])) * swing[5], -1, 1);
        double[] waves = [stroke, surge, sway, twist, roll, pitch];

        double arousalScale = 1.0 + Math.Clamp(arousal - 30, 0, 70) / 280.0;
        double safeIntensity = Math.Clamp(double.IsFinite(intensity) ? intensity : 1, 0.1, profile.MaxIntensity);
        var values = new double[6];
        for (int i = 0; i < values.Length; i++)
        {
            double amplitude = i < amplitudes.Count && double.IsFinite(amplitudes[i])
                ? Math.Clamp(amplitudes[i], 0, 50)
                : 0;
            values[i] = Math.Clamp(50 + amplitude * safeIntensity * arousalScale * waves[i], 0, 100);
        }
        return values;
    }

    // ══════════════════════════════════════════════════════════════
    //  副轴一阶滞后（「跟着走」而不是「一起动」）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 副轴跟随器：给除 L0 以外的轴加一阶滞后。
    ///
    /// 为什么需要它：六轴的耦合姿态是同一时刻算出来的，于是每个来回五根副轴和主轴一起到达两端 ——
    /// 人一眼就看得出「六根轴一起往复」的机器感。真机是「主推一下，其余几根被带着走」，
    /// 中间有一个几十到一百多毫秒的跟随过程。这里把这个过程补回来：
    ///     v += (target − v) · α，α = 1 − e^(−dt/τ)
    ///
    /// 为什么用指数形式而不是 dt/τ：帧长最大 0.1 秒、τ 最小 0.06 秒时 dt/τ 会大于 1
    /// （轻则过冲、重则振荡发散）。指数形式是同一个一阶系统的精确解，任何帧长下都稳定，
    /// 帧率变化时行为也不变。滞后只会降低带宽，所以下游的速度/加速度/jerk 限幅与多轴总闸
    /// 一点都不会被顶穿（限速器本来就在它后面）。
    /// 主轴 L0 不滞后：它就是节奏本身，滞后等于把整个动作拖慢半拍。
    /// </summary>
    public sealed class SecondaryAxisFollower
    {
        /// <summary>
        /// 每根轴的一阶滞后时间常数（秒）。越靠运动链末端的轴越慢：
        /// L1（推进）紧跟主轴 60ms；L2（倾斜）110ms；R0（扭转）85ms；R1 125ms；R2（倾摆）150ms。
        /// </summary>
        private static readonly double[] LagSeconds = [0.0, 0.060, 0.110, 0.085, 0.125, 0.150];

        private readonly double[] _lagged = [50, 50, 50, 50, 50, 50];

        /// <summary>把跟随状态复位到中位（重新开始自动动作时调用，避免上一次的姿态被带进来）。</summary>
        public void Reset(double value = 50)
        {
            for (int i = 0; i < _lagged.Length; i++)
                _lagged[i] = Math.Clamp(double.IsFinite(value) ? value : 50, 0, 100);
        }

        /// <summary>就地改写六轴姿态：L0 原样透传，其余五根按各自的时间常数跟上去。</summary>
        public void Apply(double[] values, double deltaSeconds)
        {
            if (values.Length < 6) return;
            double dt = Math.Clamp(double.IsFinite(deltaSeconds) ? deltaSeconds : 0.02, 0.001, 0.1);
            for (int i = 1; i < 6; i++)
            {
                double target = double.IsFinite(values[i]) ? Math.Clamp(values[i], 0, 100) : _lagged[i];
                double tau = LagSeconds[i];
                double alpha = tau <= 0 ? 1.0 : 1 - Math.Exp(-dt / tau);
                _lagged[i] += (target - _lagged[i]) * alpha;
                values[i] = Math.Clamp(_lagged[i], 0, 100);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  定种子 1D 值噪声 / fBm（零依赖手写）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 2–3 个八度的 fBm 值噪声，返回 −1..1（已去均值）。
    /// lacunarity 2.0 / gain 0.5；八度数由调用方给（本文件统一用 2–3 个）。
    /// </summary>
    private static double Fbm(double x, int seed, int octaves)
    {
        int count = Math.Clamp(octaves, 1, 4);
        double total = 0, amplitude = 1, weight = 0, frequency = 1;
        for (int i = 0; i < count; i++)
        {
            total += (ValueNoise1D(x * frequency, seed + i * 131) * 2 - 1) * amplitude;
            weight += amplitude;
            amplitude *= 0.5;
            frequency *= 2.0;
        }
        return weight > 0 ? total / weight : 0;
    }

    /// <summary>
    /// 一维值噪声：格点上放一个由 (种子, 格点序号) 散列出来的随机值，格点之间用 smoothstep 插值。
    /// 用 smoothstep 而不是线性插值是必须的：线性插值在格点处一阶导不连续，
    /// 每过一个格点就是一次「折角」，正好是限速器最怕的目标突变。
    /// 同 seed + 同 x 永远同一值 —— 这正是「每次运行不重复、单次运行可复现」要的性质。
    /// </summary>
    private static double ValueNoise1D(double x, int seed)
    {
        double floor = Math.Floor(x);
        long cell = (long)floor;
        double t = x - floor;
        double a = Hash01(cell, seed);
        double b = Hash01(cell + 1, seed);
        double smooth = t * t * (3 - 2 * t);
        return a + (b - a) * smooth;
    }

    /// <summary>把 (格点序号, 种子) 散列成 0..1（splitmix64 混合，与 MotionEngine 的 Tempest 噪声同族）。</summary>
    private static double Hash01(long cell, int seed)
    {
        ulong hash = 0x9E3779B97F4A7C15UL;
        hash ^= (ulong)cell * 0xBF58476D1CE4E5B9UL;
        hash ^= (ulong)(uint)seed * 0x94D049BB133111EBUL;
        hash ^= hash >> 30; hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27; hash *= 0x94D049BB133111EBUL;
        hash ^= hash >> 31;
        return (hash >> 11) * (1.0 / 9007199254740992.0);
    }
}
