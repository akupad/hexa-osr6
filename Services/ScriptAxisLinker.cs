namespace Hexa.Services;

/// <summary>
/// 多轴联动：脚本没写的轴跟着脚本主轴（L0）一起动。
///
/// <para>
/// <b>为什么只联动「脚本没写」的轴</b>：社区里的 funscript 绝大多数只写了 L0，其余 5 轴全程停在
/// <see cref="MotionVocabulary.Center"/>，设备就退化成一「只会上下动的棒子」。把空轴按主轴的运动推导出来，
/// 机器才像一台整机在动。但作者真写了动作的轴一律不碰 —— 那是创作者的意图：
/// 叠加推导量等于篡改别人的作品，而且两套节奏叠在一起往往互相打架，比不动更难看。
/// </para>
/// <para>
/// <b>为什么速度要 tanh + 一阶低通（EMA）</b>：直接拿 Δ位置/Δt 当速度用，播放器每帧的采样间隔本来就不均匀
/// （60fps 只是理想值），一帧抖动就足以让扭转轴乱甩。先用 tanh 把速度压到 -1..1：
/// 「一秒走完整个行程」约 0.96，再快也只是逼近 1 而不会失控；再用 EMA 把剩下的采样噪声磨平，
/// 于是「快的地方才有扭转」成了一条平滑曲线，而不是噪声放大器。
/// </para>
/// <para>
/// <b>为什么位置用整条轨道的 min/max 归一化</b>：联动幅度应该只跟「脚本自己在动多少」有关，
/// 而不是跟 0–100 这个坐标刻度有关 —— 否则同一份动作、换个录制量程，联动强度就完全变了。
/// 归一化到轨道自身范围后，全行程脚本给满量程信号，小行程脚本按比例给；下限 6.0 则挡住另一种失真：
/// 轨道几乎不动（例如 49.5–50.5）时若不设下限，这点抖动会被除成一个满量程的 ±1，
/// 脚本明明静止，其它轴却甩出整个振幅。
/// </para>
/// <para>
/// <b>线程模型</b>：本类<b>不是线程安全的</b>，状态只在 <see cref="Apply"/> 被调用时更新。
/// 只允许播放器的 UI 定时器这一条线程调用，不要从设备线程或后台任务里再插一帧进来。
/// </para>
/// </summary>
public sealed class ScriptAxisLinker
{
    /// <summary>联动幅度的下限（轴行程百分比）：再小就等于没联动，界面上没必要给更低的档。</summary>
    public const double MinAmount = 5;

    /// <summary>联动幅度的上限（轴行程百分比）：派生轴最多偏离中位这么多，超过就该让作者自己写轴了。</summary>
    public const double MaxAmount = 60;

    /// <summary>默认联动幅度（轴行程百分比）：够看出整机在动，又不至于喧宾夺主。</summary>
    public const double DefaultAmount = 30;

    /// <summary>上一帧的主轴位置（轴行程百分比），用来算帧间速度。</summary>
    private double _previousPosition = MotionVocabulary.Center;

    /// <summary>快速度信号（EMA 时间常数 0.06s）：跟得上快段落。</summary>
    private double _velocity;

    /// <summary>慢速度信号（EMA 时间常数 0.28s）：只要趋势、不要细节，给滞后感用的。</summary>
    private double _slowVelocity;

    /// <summary>最近一帧的「速度信号」（-1..1）：快的地方为 1，慢/停住为 0。</summary>
    public double VelocitySignal { get; private set; }

    /// <summary>最近一帧的「位置信号」（-1..1）：主轴在最下方为 -1，最上方为 +1。</summary>
    public double PositionSignal { get; private set; }

    /// <summary>清空速度跟踪状态（重新载入脚本 / 跳转播放位置 / 暂停后重新播放时必须调用）。</summary>
    /// <remarks>
    /// 不调用就会拿「跳转后的位置」减去「跳转前的位置」再除以一帧的时间，算出一个几百倍的假速度，
    /// 派生轴会跟着抽一下；这既难看又让用户以为设备出故障。
    /// </remarks>
    public void Reset()
    {
        _previousPosition = MotionVocabulary.Center;
        _velocity = 0;
        _slowVelocity = 0;
        VelocitySignal = 0;
        PositionSignal = 0;
    }

    /// <summary>
    /// 就地修改 <paramref name="values"/>：只改「脚本没有提供」的轴（<paramref name="scriptedAxes"/> 为 false 的位）。
    /// 轴顺序固定为 L0, L1, L2, R0, R1, R2（索引 0..5）。
    /// </summary>
    /// <param name="values">六轴当前位置 0–100（就地修改）。</param>
    /// <param name="scriptedAxes">长度 6；true = 该轴由脚本自己写了动作，绝不改动。</param>
    /// <param name="amountPercent">联动幅度（轴行程百分比，5–60）：派生轴最多偏离中位多少。</param>
    /// <param name="deltaSeconds">距上一帧的秒数（≤0 或非有限值按 0 处理：不更新速度）。</param>
    /// <param name="trackMin">整条 L0 轨道的最小位置（0–100），用来把「位置」归一化成 -1..1。</param>
    /// <param name="trackMax">整条 L0 轨道的最大位置（0–100）。</param>
    public void Apply(double[] values, bool[] scriptedAxes, double amountPercent,
                      double deltaSeconds, double trackMin, double trackMax)
    {
        // 没有六轴就没有可写的东西；长度不足说明调用方拿错了帧格式，静默跳过好过越界写坏数据。
        if (values is null || values.Length < 6) return;

        // 拿不到「哪些轴是脚本写的」就一律当作全是脚本写的 —— 宁可不动，也不要去改作者写好的轴。
        if (scriptedAxes is null || scriptedAxes.Length != 6) return;

        // 没有 L0 主轴就没有推导基准：基准缺失时任何联动都是凭空捏造。
        // 这种脚本大概率自带全套轴编排，这里连跟踪状态也不必更新 —— 没有基准的速度信号本来就没有意义。
        if (!scriptedAxes[0]) return;

        // 主轴可能是 NaN/Infinity（坏脚本、跳转瞬间的空帧）：按中位处理，别让它污染速度和位置信号 ——
        // 一旦 NaN 进了 _previousPosition 或 EMA，后面每一帧都会是 NaN，派生轴会直接卡死在 50。
        double position = double.IsFinite(values[0]) ? values[0] : MotionVocabulary.Center;

        double mid;
        double halfRange;
        if (!double.IsFinite(trackMin) || !double.IsFinite(trackMax))
        {
            // 还没拿到整条轨道的范围（例如刚载入、第一帧）：退回「全行程」假设，等价于不归一化。
            mid = MotionVocabulary.Center;
            halfRange = 50.0;
        }
        else
        {
            if (trackMax < trackMin) (trackMin, trackMax) = (trackMax, trackMin);
            mid = (trackMin + trackMax) / 2.0;
            // 下限 6.0：轨道几乎不走时，不设下限会把这点抖动归一化成满量程 ±1，其它轴反而甩出整个振幅。
            halfRange = Math.Max(6.0, (trackMax - trackMin) / 2.0);
        }

        PositionSignal = Math.Clamp((position - mid) / halfRange, -1.0, 1.0);

        // 时间差本身也可能是垃圾值（暂停恢复、计时器跳变）：按 0 处理 = 本帧不产生速度。
        double dt = double.IsFinite(deltaSeconds) && deltaSeconds > 0 ? deltaSeconds : 0;
        double rawVelocity = dt > 0 ? (position - _previousPosition) / dt : 0;

        // tanh 把无上界的轴向速度（跳转时能到几千）压进 -1..1：
        // 用「一秒走完整个行程」当参考点，对应 tanh(1) ≈ 0.76 / tanh(2) ≈ 0.96，再快也只会逼近 1。
        double normalized = Math.Tanh(rawVelocity / Math.Max(8.0, 2.0 * halfRange));

        // 两条 EMA 共用同一个归一化速度：快的给扭转和推进，慢的给前后倾（滞后感来自时间常数，不是来自额外计算）。
        _velocity += (normalized - _velocity) * Weight(dt, 0.06);
        _slowVelocity += (normalized - _slowVelocity) * Weight(dt, 0.28);
        VelocitySignal = _velocity;

        _previousPosition = position;   // 必须在算完速度之后更新，否则本帧速度恒为 0

        // 幅度非法（0/NaN = 用户把联动关掉了或输入框是空的）时，速度与位置跟踪仍照常更新：
        // 否则用户从 0 往上调的那一瞬间，_previousPosition 还停在很久以前的位置，会凭空算出一个巨大的速度尖峰，
        // 派生轴会先抽一下再回到正常联动 —— 这个「起步抽搐」比联动本身更显眼。
        if (!double.IsFinite(amountPercent) || amountPercent <= 0) return;

        for (int i = 1; i < 6; i++)   // 索引 0 是主轴，绝不改写
        {
            if (scriptedAxes[i]) continue;   // 作者写了的轴，保持原样

            // 系数 = 「相对中位的偏离量」相对联动幅度的比例；符号决定各轴是同相还是反相。
            double deviation = i switch
            {
                1 => 0.45 * _velocity,        // L1 推进：快的地方略向前顶，跟住节奏
                2 => 0.85 * PositionSignal,   // L2 左右摆：跟着主轴的深度左右摆
                3 => 1.00 * VelocitySignal,   // R0 扭转：最明显的一条，快的地方加扭转
                4 => 0.90 * PositionSignal,   // R1 左右倾：与 L2 同相、幅度略大，整体像一个斜面在动
                5 => -0.70 * _slowVelocity,   // R2 前后倾：反相 + 慢信号，做出「先冲后倾」的滞后感
                _ => 0.0,
            };

            values[i] = Math.Clamp(MotionVocabulary.Center + amountPercent * deviation, 0, 100);
        }
    }

    /// <summary>
    /// 一阶低通的插值权重 <c>1 - e^(-dt/tau)</c>：dt 越大越贴近新值，与采样率无关。
    /// </summary>
    /// <param name="dt">距上一帧的秒数。</param>
    /// <param name="tau">时间常数（秒）：越大越慢。</param>
    /// <remarks>
    /// dt ≤ 0 或非有限（暂停、时钟回跳、暂停后首帧）时返回 0 = 不更新：
    /// 拿一帧的时间差去推进滤波状态，等于用伪数据污染滤波器，恢复播放时会看到一次假的扭转扫过。
    /// </remarks>
    private static double Weight(double dt, double tau) =>
        !double.IsFinite(dt) || dt <= 0 ? 0 : Math.Clamp(1.0 - Math.Exp(-dt / tau), 0, 1);
}
