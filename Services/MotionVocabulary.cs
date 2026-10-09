namespace Hexa.Services;

/// <summary>
/// 「有事发生」的四种形态。沉浸感的关键不是动得多，而是<b>动作和刚刚发生的事对得上</b>：
/// 冲击该快而深、渐强该慢而连贯、节拍该轻而准、人声该是一进一出的一次抽插，安静就该静止。
/// </summary>
public enum MotionEventKind
{
    /// <summary>没有事件（安静 / 维持）。</summary>
    None,

    /// <summary>瞬态冲击：爆炸、枪声、撞击、打击 —— 短促、快、深，带明显回落。</summary>
    Impact,

    /// <summary>渐强 / 长音：音乐起伏、引擎轰鸣、风声 —— 慢、深、连贯。</summary>
    Swell,

    /// <summary>节拍：有节奏的重复点 —— 短、浅、跟拍。</summary>
    Beat,

    /// <summary>
    /// 人声：呻吟 / 喘息 / 说话。一次发声 = <b>一次抽插</b>（插进去、再抽出来），
    /// 不是 Swell 那种缓缓涨落的周期 —— 用户要的是「呻吟机器就插入」这件事本身。
    /// </summary>
    Voice,
}

/// <summary>一次检测到的动作事件。</summary>
/// <param name="Kind">事件类型。</param>
/// <param name="Strength">强度 0–1。</param>
/// <param name="AtSeconds">距播放开始的秒数（用于延迟补偿）。</param>
public readonly record struct MotionEvent(MotionEventKind Kind, double Strength, double AtSeconds);

/// <summary>
/// 动作语汇：把「一个事件」翻译成六轴各自的落点（0–100）。
///
/// 这是纯函数、可单测：给定事件类型 / 强度 / 事件内部相位，输出六轴位置。
/// 「六轴」而不是只用 L0 是刻意的——只用一根轴，机器就只是"会动的棒子"；
/// 冲击加一点扭转、慢段落加一点倾斜，才像这台机器自己在回应画面。
/// </summary>
public static class MotionVocabulary
{
    /// <summary>中位（各轴静止位置）。</summary>
    public const double Center = 50.0;

    /// <summary>人声语汇里「插进去」占的相位比例（剩下的是抽出来）。0.45 = 进得快、退得稍慢。</summary>
    private const double VoiceThrustRatio = 0.45;

    /// <summary>
    /// 渲染一个事件在某一相位的六轴落点。
    /// </summary>
    /// <param name="kind">事件类型。</param>
    /// <param name="strength">事件强度 0–1。</param>
    /// <param name="phase">事件内部相位 0–1（Impact/Beat 用衰减曲线，Swell 用正弦周期，Voice 用「进→退」的一整套）。</param>
    /// <param name="amplitude">半幅（轴行程百分比，5–60）。</param>
    /// <param name="multiAxis">是否启用多轴语汇（关掉则只有 L0 动，其余保持中位）。</param>
    public static double[] Render(MotionEventKind kind, double strength, double phase, double amplitude, bool multiAxis = true)
    {
        double s = Math.Clamp(strength, 0, 1);
        double amp = Math.Clamp(amplitude, 0, 60);
        double p = Math.Clamp(phase, 0, 1);

        double main;
        double twist = 0;     // R0/R1 的扭转量（相对中位）
        double tilt = 0;      // L2/R2 的倾斜量（相对中位）
        double surge = 0;     // L1 的次要推进

        switch (kind)
        {
            case MotionEventKind.Impact:
            {
                // 冲击：前 15% 快速冲出，之后按平方衰减回落 —— 有 attack 也有余韵。
                double attack = p < 0.15 ? p / 0.15 : 1.0;
                double decay = p < 0.15 ? 1.0 : Math.Pow(1.0 - (p - 0.15) / 0.85, 2.0);
                main = amp * s * attack * decay;
                twist = amp * 0.22 * s * attack * decay;      // 撞击带一点扭转
                tilt = amp * 0.12 * s * attack * decay;       // 轻微下压
                surge = amp * 0.18 * s * attack * decay;
                break;
            }

            case MotionEventKind.Swell:
            {
                // 渐强：一个完整正弦周期，慢、深、连贯，没有突变。
                double wave = Math.Sin(p * Math.Tau);
                main = amp * s * wave;
                twist = amp * 0.30 * s * Math.Sin(p * Math.Tau + Math.PI / 3);   // 相位错开 → 螺旋感
                tilt = amp * 0.25 * s * Math.Sin(p * Math.Tau + Math.PI / 2);
                surge = amp * 0.20 * s * Math.Sin(p * Math.Tau + Math.PI / 4);
                break;
            }

            case MotionEventKind.Beat:
            {
                // 节拍：短促、浅、准；回落快，方便连着跟下一拍。
                double decay = Math.Pow(1.0 - p, 2.2);
                main = amp * 0.65 * s * decay;
                twist = amp * 0.15 * s * decay;
                tilt = 0;
                surge = 0;
                break;
            }

            case MotionEventKind.Voice:
            {
                // 人声（呻吟）= 一次抽插：一次事件就是「插进去、再抽出来」一个来回。
                // 相位 0 = 完全抽出来（中位），0→0.45 往里推到底（快、干脆），
                // 0.45→1 抽回来（略慢，收得住）—— 不是正弦那种"涨上去又落回来"的对称起伏，
                // 也不是 Impact 那种"冲一下就没"，而是进得干脆、退得明显的一整套动作。
                double punch = p < VoiceThrustRatio
                    ? 1.0 - Math.Pow(1.0 - p / VoiceThrustRatio, 2.0)
                    : Math.Pow((1.0 - p) / (1.0 - VoiceThrustRatio), 1.35);
                main = amp * s * punch;
                // 次要轴只做轻微跟随：抽插的主角是主轴，别的轴跟着晃太多会像「抖」而不是「抽插」。
                twist = amp * 0.10 * s * punch;
                tilt = amp * 0.08 * s * punch;
                surge = amp * 0.14 * s * punch;
                break;
            }

            default:
                return [Center, Center, Center, Center, Center, Center];
        }

        if (!multiAxis) { twist = 0; tilt = 0; surge = 0; }

        return
        [
            Math.Clamp(Center + main, 0, 100),          // L0 主轴：推拉
            Math.Clamp(Center + surge, 0, 100),         // L1
            Math.Clamp(Center + tilt, 0, 100),          // L2 倾斜
            Math.Clamp(Center + twist, 0, 100),         // R0 扭转
            Math.Clamp(Center + twist * 0.6, 0, 100),   // R1
            Math.Clamp(Center + tilt * 0.6, 0, 100),    // R2
        ];
    }

    /// <summary>一个事件应该持续多久才自然（秒）。</summary>
    public static double DurationSeconds(MotionEventKind kind, double strength) => kind switch
    {
        MotionEventKind.Impact => 0.42 - 0.10 * Math.Clamp(strength, 0, 1),   // 越强越干脆
        MotionEventKind.Swell => 1.60,
        MotionEventKind.Voice => 1.05 + 0.30 * Math.Clamp(strength, 0, 1),   // 一次呻吟 1.05–1.35s：比节拍长、比渐强短
        MotionEventKind.Beat => 0.26,
        _ => 0.0,
    };

    /// <summary>事件类型的中文名（给界面显示"现在在响应什么"）。</summary>
    public static string Label(MotionEventKind kind) => kind switch
    {
        MotionEventKind.Impact => "冲击",
        MotionEventKind.Swell => "渐强",
        MotionEventKind.Voice => "呻吟",
        MotionEventKind.Beat => "节拍",
        _ => "安静",
    };
}
