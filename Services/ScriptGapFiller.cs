namespace Hexa.Services;

/// <summary>
/// 脚本里的一段「空档」：从 <paramref name="StartMs"/> 到 <paramref name="EndMs"/> 之间主轴基本不动。
/// <paramref name="HoldPosition"/> 是这一整段停在的位置（0–100）。
/// </summary>
public readonly record struct ScriptGap(long StartMs, long EndMs, double HoldPosition)
{
    public long LengthMs => EndMs - StartMs;
}

/// <summary>
/// 空档填缝：脚本里「作者什么都没写、设备就那么停着」的段落，跟着音乐补上小动作。
///
/// 为什么要有这个：很多社区脚本在间奏、对白、片头片尾会有一段完全静止（位置一动不动几秒），
/// 这时候设备死在那里，人会觉得「是不是卡住了」。填缝不是替作者改脚本，只是让静止的段落
/// 仍然跟着当前的声音有一点呼吸感。
///
/// 三条自我约束（都是刻意的）：
/// ① 只填「作者没写动作」的段落——写了的地方是作者的意图，抢过来就是篡改创作；
/// ② 只填「两端位置几乎相同」的静止段落——位置差得多的区间是正常的慢动作，不是空档；
/// ③ 没有声音时一个字节都不动——安静就该静止，这才是沉浸感。
///
/// 动作形状用 <see cref="MotionVocabulary"/> 的「渐强」语汇：一条慢正弦，各轴相位错开，
/// 所以是又慢又连的呼吸感。**慢是关键**：实测限速器跟得住 0.5 Hz 的慢摆（误差 ~1.4），
/// 但跟不上每 0.4 秒一次的冲击形状（目标来回太快，它会冲过目标再追回来，绕着保持位置摆 20 多个点）。
/// </summary>
public static class ScriptGapFiller
{
    public const long MinGapMs = 800;
    public const long MaxGapMs = 6000;
    public const long DefaultMinGapMs = 1500;

    /// <summary>空档两端的过渡时长：进出空档时动作要淡入淡出，不能在空档边界突然蹦一下。</summary>
    public const double EaseMs = 250;

    /// <summary>低于这个响度就当安静：一个字节都不动（安静就该静止）。</summary>
    public const double SilenceLevel = 0.02;

    /// <summary>填缝幅度的取值范围与默认值（轴行程百分比）。</summary>
    public const double MinFillRange = 5;
    public const double MaxFillRange = 40;
    public const double DefaultFillRange = 20;

    /// <summary>填缝的摆动频率（赫兹）：0.35–0.70 之间随响度变化（越响摆得略快）。</summary>
    public static double Frequency(double level) => 0.35 + 0.35 * Math.Clamp(level, 0, 1);

    /// <summary>
    /// 找出主轴轨道里的空档（相邻两个动作之间「停着不动」且时间足够长的区间）。
    /// <paramref name="actions"/> 为空或不足两点时返回空列表；入参不会被修改。
    /// </summary>
    /// <param name="actions">主轴（L0）动作点。</param>
    /// <param name="minGapMs">多长才算空档；会被夹到 <see cref="MinGapMs"/>–<see cref="MaxGapMs"/>。</param>
    /// <param name="flatTolerance">两端位置差不超过这个值才算「停着不动」（0–100 的位置单位）。</param>
    public static IReadOnlyList<ScriptGap> FindGaps(
        IReadOnlyList<WaveScriptCodec.ActionPoint>? actions, long minGapMs, int flatTolerance = 4)
    {
        if (actions == null || actions.Count < 2) return [];
        long threshold = Math.Clamp(minGapMs, MinGapMs, MaxGapMs);
        int tolerance = Math.Clamp(flatTolerance, 0, 100);

        // 入参顺序不保证：先自己排一份，绝不改动调用方的集合。
        var ordered = actions.OrderBy(point => point.At).ToArray();
        var gaps = new List<ScriptGap>();
        for (int i = 0; i + 1 < ordered.Length; i++)
        {
            var current = ordered[i];
            var next = ordered[i + 1];
            if (next.At - current.At < threshold) continue;
            if (Math.Abs(next.Pos - current.Pos) > tolerance) continue;
            gaps.Add(new ScriptGap(current.At, next.At, current.Pos));
        }
        return gaps;
    }

    /// <summary>
    /// 如果 <paramref name="positionMs"/> 落在某个空档里、而且现在有声音，就把六轴<b>就地</b>改成
    /// 「跟着声音的慢呼吸」。返回 true 表示这一帧确实填了缝（界面与测试据此判断）。
    /// </summary>
    /// <param name="values">六轴当前位置 0–100（就地修改），轴顺序 L0, L1, L2, R0, R1, R2。</param>
    /// <param name="positionMs">当前播放位置（毫秒）。</param>
    /// <param name="gaps">由 <see cref="FindGaps"/> 算出的空档表。</param>
    /// <param name="amplitudePercent">动作幅度（轴行程百分比，通常取「声音响应 → 动作幅度」那一项）。</param>
    /// <param name="multiAxis">是否允许次要轴一起动（关掉时只有 L0 动）。</param>
    /// <param name="level">当前响度 0–1（调用方已经做过平滑；安静时传 0）。</param>
    /// <param name="phase">摆动相位 0–1（由调用方按 <see cref="Frequency"/> 推进，保证跨帧连续）。</param>
    public static bool TryFill(
        double[] values, long positionMs, IReadOnlyList<ScriptGap> gaps,
        double amplitudePercent, bool multiAxis, double level, double phase)
    {
        if (values == null || values.Length < 6 || gaps == null || gaps.Count == 0) return false;

        int index = FindGapIndex(gaps, positionMs);
        if (index < 0) return false;
        var gap = gaps[index];

        // 淡入淡出：空档边界处权重为 0，往中间升到 1（smoothstep，避免动作突然起步）。
        double edge = Math.Min(positionMs - gap.StartMs, gap.EndMs - positionMs);
        double weight = Math.Clamp(edge / EaseMs, 0, 1);
        weight = weight * weight * (3 - 2 * weight);
        if (weight <= 0) return false;

        if (!double.IsFinite(level) || level <= SilenceLevel) return false;
        if (!double.IsFinite(amplitudePercent) || amplitudePercent <= 0) return false;
        if (!double.IsFinite(phase)) return false;

        // 慢正弦 + 各轴相位错开（渐强语汇）：越响摆幅越大，安静时语汇本身就会把落点收回中位。
        double[] pose = MotionVocabulary.Render(
            MotionEventKind.Swell, Math.Clamp(level, 0, 1), phase, amplitudePercent, multiAxis);

        // 逐轴算「还能往这个方向走多远」，取全局最小值做等比缩放。
        // 空档时主轴可能停在 8 或 92 这种极端位置，直接叠加会撞到 0/100 被截平、动作形状全变形；
        // 等比缩放既保证不越界，又保留原本的动作比例。
        double scale = 1.0;
        for (int i = 0; i < 6; i++)
        {
            double delta = pose[i] - MotionVocabulary.Center;
            if (Math.Abs(delta) < 0.001) continue;
            double baseValue = double.IsFinite(values[i]) ? values[i] : MotionVocabulary.Center;
            double room = delta > 0 ? 100 - baseValue : baseValue;
            scale = Math.Min(scale, Math.Max(0, room / Math.Abs(delta)));
        }

        for (int i = 0; i < 6; i++)
        {
            double delta = pose[i] - MotionVocabulary.Center;
            if (Math.Abs(delta) < 0.001) continue;
            double baseValue = double.IsFinite(values[i]) ? values[i] : MotionVocabulary.Center;
            values[i] = Math.Clamp(baseValue + delta * scale * weight, 0, 100);
        }
        return true;
    }

    /// <summary>二分查找包含 <paramref name="positionMs"/> 的空档（空档按时间升序且互不重叠）。</summary>
    private static int FindGapIndex(IReadOnlyList<ScriptGap> gaps, long positionMs)
    {
        int lo = 0, hi = gaps.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (positionMs < gaps[mid].StartMs) hi = mid - 1;
            else if (positionMs > gaps[mid].EndMs) lo = mid + 1;
            else return mid;
        }
        return -1;
    }
}

/// <summary>
/// 「现在有多响」的来源（0–1）。抽成接口是为了让它可替换：
/// 自检（不开声音也能验证填缝链路）和单元测试都注入假响度，不必真的去放一段音乐。
/// </summary>
public interface IAudioLevelSource
{
    double Level { get; }
}

/// <summary>
/// 从 <see cref="AudioReactiveService"/> 读当前音量包络。
/// 注意读的是已经平滑过的包络（40/400ms 双 EMA），本身就不会一帧一跳；
/// 而且「只监听」模式一样会算它——不需要声音响应开着驱动设备。
/// </summary>
public sealed class AudioReactiveLevelSource : IAudioLevelSource
{
    private readonly AudioReactiveService _audio;

    public AudioReactiveLevelSource(AudioReactiveService audio) => _audio = audio;

    public double Level
    {
        get
        {
            double level = _audio.Envelope;
            return double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        }
    }
}
