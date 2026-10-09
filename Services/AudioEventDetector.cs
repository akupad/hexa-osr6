using NAudio.Dsp;

namespace Hexa.Services;

/// <summary>
/// 音频事件检测器：把「一直在变的音量」变成「刚刚发生了什么事」。
///
/// <b>为什么现在可以上 FFT</b>（推翻本文件老注释里「跑在 WASAPI 回调线程上所以不能做 FFT」那条）：
/// NAudio 的 DataAvailable 并不在实时音频线程上，而是 WasapiRecorder 自己起的后台采集线程
/// （captureThread，没有 SetThreadPriority），周期 10ms。一次 1024 点实数 FFT 在这个线程上
/// 只要十几微秒（见 <see cref="SpectralFluxOnset"/>；用的是 NAudio 包内自带的 NAudio.Dsp.FftProcessor，
/// 零新依赖），占一次回调预算不到 1%。所以频谱分析在这里不是买不起的东西 ——
/// 它带来的判别力（谱通量起音、真频带能量占比）恰恰是几个时域 O(1) 统计量给不了的。
/// 真正不能做的是<b>分配</b>和<b>阻塞</b>，不是 FFT。
///
/// 保留的时域统计量（便宜且够用）：
///   ① 分帧 RMS（10ms 一帧，时域能量）—— 绝对门槛与宽带跃升；
///   ② 一阶低通（150Hz）之后的低频 RMS —— 「低频能量走高」这条需求用它，不必真的做频谱分解；
///   ③ 双时间常数 EMA（快线 40ms / 慢线 400ms）：快线 =「现在多响」，慢线 =「刚才多响」；
///   ④ 起音间隔的均值与标准差：判断「有没有节奏」。
///
/// 四种事件靠「跃升幅度 + 跃升用时 + 谱通量」互相分开：
///   Impact 冲击：快线相对慢线跃升 ≥9dB，且这次上升从基线越过阈值只用了 ≤80ms（10ms × 8 帧）。
///   Swell  渐强：跃升 ≥4.5dB（或低频跃升 ≥3dB 且最近 200ms 还在往上爬）并<b>连续保持 ≥400ms</b>。
///   Beat   节拍：<b>谱通量起音</b>（不再看「宽带电平越线」），且这次起音不是冲击级的（跃升 &lt;9dB），
///                且最近 4 次起音间隔的标准差 ≤ max(60ms, 均值×20%)、均值落在 0.25–1.2s。
///   Voice  人声：真频带（100–1200Hz）能量占全带能量的比例 ≥25%（即 −6dB），
///                且这个频带相对自己的长时均值跃升 ≥3dB、最近 250ms 净涨 ≥1.2dB。
///
/// 防误触发：
///   ① 绝对电平门槛 −55dB：低于它就是噪声底，什么都不算事件（与界面电平条的归一化下限一致）；
///   ② 各类事件各有最小间隔：Impact 120ms / Beat 180ms / Swell 1.0s / Voice 250ms；
///   ③ Impact 要求「上升用时 ≤80ms」：100–400ms 的慢爬升属于渐强，不是冲击；
///   ④ Impact 用 40ms 快线判定：几百毫秒的长响里慢线会追上来，跃升自然跌破 9dB ——
///      一声长响不会被报成几十次冲击；
///   ⑤ Swell 要求连续保持 400ms：一次性瞬态根本撑不到；
///   ⑥ 冷启动 500ms 内只更新统计、不报事件：慢线还没收敛，否则开头必然误报一次冲击
///      （谱通量的阈值历史只有 200ms，500ms 足够它填满）；
///   ⑦ Beat/节拍起音用谱通量的自适应阈值（近 200ms 中位数 × 1.55 + 绝对下限 + 170ms 反跳），
///      不是固定门槛：安静时不会被数值噪声点着，吵的时候也不会被伴奏淹没。
///
/// 线程模型：<see cref="Process"/> 只在音频采集线程调用；只读属性可从别的线程读（Volatile 发布）。
/// 稳态零分配：全部是字段 + 定长数组，没有 LINQ、闭包和 new。
/// </summary>
public sealed class AudioEventDetector
{
    /// <summary>一个采样块最多可能检出的事件数（调用方按它准备缓冲）。</summary>
    public const int MaxEventsPerBlock = 4;

    // ── 判定阈值：动任何一个都要连着看上面注释里的取舍 ────────────────────
    private const int    FrameRingSize       = 64;      // 帧历史环（只用于「低频还在往上走」的回看）
    private const double FrameSeconds        = 0.010;   // 分帧长度：10ms
    private const double DbFloor             = -90.0;   // dB 下限，静音时不至于算成 −∞
    private const double EventFloorDb        = -55.0;   // 绝对电平门槛
    private const double ImpactRiseDb        = 9.0;     // 冲击：相对慢线的跃升
    private const double ImpactHysteresisDb  = 3.0;     // 「回到过基线」的判定线
    private const int    ImpactRiseFrames    = 8;       // 上升用时上限：8 帧 = 80ms
    private const double ImpactMinInterval   = 0.120;   // 冲击最小间隔
    private const double SwellRiseDb         = 4.5;     // 渐强：宽带跃升
    private const double SwellBassRiseDb     = 3.0;     // 渐强：低频跃升

    // ── 人声频段（呻吟 / 喘息 / 说话）─────────────────────────────────
    // 不加这一条就"对呻吟没反应"的原因：
    //   呻吟是 100–1200Hz 的**中频**，而且**慢起慢落**（几百毫秒到一两秒）；
    //   它既没有冲击要求的"80ms 内跃升 9dB"，也几乎不带低频能量
    //   （女性基频常在 200–400Hz，150Hz 低通基本拿不到），
    //   所以老的「宽带跃升 || 低频爬升」两个条件都不成立 → 一个事件都不出。
    // 这个频带现在由 FFT 直接量（SpectralFluxOnset.BandDb），不再用一阶高通凑 ——
    // 一阶高通是 6dB/倍频程，对 55Hz 只衰减 9dB，低音会整段漏进来冒充人声。
    // 同一路信号还带出两个量（见 VoiceLevel / VoicePulseRate）：人声模式的「深度」和「她的节奏」。
    private const double VoiceRiseDb         = 3.0;     // 人声：相对长时均值的中频跃升
    private const int    VoiceSlopeFrames    = 25;      // 「还在往上走」的回看窗口：250ms
    private const double VoiceSlopeDb        = 1.2;
    private const double VoiceMinInterval    = 0.250;   // 人声最小间隔：同一次呻吟里的抖动不该连报
    private const double VoiceRearmDb        = 1.5;     // 落回这条线以下 = 下一次呻吟可以再报（迟滞）
    private const double VoiceFloorDb        = -60.0;   // 人声强度归一的下限
    private const double VoiceCeilDb         = -20.0;   // 归一上限：−20dB 往上就算「满」
    private const double VoicePulseRise      = 0.08;    // 节奏：包络冲上慢线多少算一次起伏
    private const double VoicePulseFloor     = 0.12;    // 节奏：绝对下限，太轻的起伏不算
    private const double VoicePulseRefractory = 0.180;  // 节奏：一次起伏只算一次（反跳保护）
    private const double VoicePulseWindow    = 2.000;   // 数节奏的回看窗口：最近 2 秒
    // 中频必须**占据主要能量**才算人声。宽带噪声（呼吸声、房间底噪、空调声）在 100–1200Hz 里
    // 也有能量，只靠"跃升 3dB"会把喘气声也认成呻吟 —— 用户要的是"呻吟才抽插"，
    // 安静时乱插是最糟的体验。人声的能量几乎全落在这个频段里（≈ 0dB 差），
    // 白噪声只有约 13% 落在这里（≈ −9dB 差），55Hz 低音更是低到 −50dB 以下（FFT 的砖墙带边，
    // 不是一阶滤波器那种漏法）。差值 = 宽带电平 − 中频电平，阈值 6dB 就是"中频至少占 25%"。
    private const double VoiceBandDominanceDb = 6.0;
    private const double VoicePulseMinSpan   = 0.400;   // 首尾相隔太短除出来的频率不可信
    private const int    VoicePulseCapacity  = 12;      // 窗口内最多记 12 次起伏（12/2s = 6Hz 够用）
    private const int    SwellHoldFrames     = 40;      // 连续保持 400ms 才算渐强
    private const int    BassSlopeFrames     = 20;      // 「低频还在往上走」的回看窗口：200ms
    private const double BassSlopeDb         = 1.5;
    private const double SwellMinInterval    = 1.000;   // 渐强最小间隔
    private const double SwellRepeatAfter    = 1.200;   // 一直轰鸣时允许再报一次的间隔
    private const double BeatMinInterval     = 0.180;   // 节拍最小间隔
    private const double BeatOnsetRefractory = 0.120;   // 同一次起音内的反跳保护
    private const int    BeatIntervalCount   = 4;       // 「最近 4 次间隔」判周期
    private const double BeatMinPeriod       = 0.250;   // 25–120 BPM 之外不当节拍
    private const double BeatMaxPeriod       = 1.200;
    private const double BeatToleranceBase   = 0.060;   // 标准差允许值 = max(60ms, 均值×20%)
    private const double BeatToleranceRatio  = 0.200;
    private const int    WarmupFrames        = 50;      // 冷启动 500ms 内只收敛、不报事件
    private const double SilenceSeedDb       = -70.0;   // 统计量初值（明显低于门槛，避免开局误报）

    private static readonly double AlphaFast     = AlphaFor(0.040);
    private static readonly double AlphaSlow     = AlphaFor(0.400);
    private static readonly double AlphaBassSlow = AlphaFor(0.500);
    private static readonly double AlphaVoiceAttack   = AlphaFor(0.080);   // 人声强度：起音跟得上（80ms）
    private static readonly double AlphaVoiceRelease  = AlphaFor(0.300);   // 回落慢一点（300ms）
    private static readonly double AlphaVoicePulseRef = AlphaFor(0.400);   // 数节奏用的慢线（400ms）

    /// <summary>EMA 的每帧系数：分帧长度固定，所以系数只算一次（静态初始化，不在回调里算）。</summary>
    private static double AlphaFor(double tauSeconds) => 1.0 - Math.Exp(-FrameSeconds / tauSeconds);

    // ── 分帧状态 ──
    private int _sampleRate;
    private int _hopSamples;
    private int _frameSamples;
    private double _frameSumSquares;
    private double _bassSumSquares;
    private double _lowPassState;
    private double _lowPassAlpha;
    private long _frameCount;
    private int _frameIndex;
    private int _warmupFrames;

    /// <summary>谱通量起音 + 真频带能量占比（FFT，每帧一次）：Beat 的起音判定与人声频带都用它。</summary>
    private readonly SpectralFluxOnset _flux = new();

    private readonly double[] _bassDbRing = new double[FrameRingSize];

    // ── 电平统计（dB）──
    private double _fastDb = SilenceSeedDb;
    private double _slowDb = SilenceSeedDb;
    private double _bassFastDb = SilenceSeedDb;
    private double _bassSlowDb = SilenceSeedDb;

    // 人声频带电平：FFT 直接算 100–1200Hz 的能量（见 SpectralFluxOnset.BandDb）。
    // 以前是"低频那一路减掉得到高通信号、再低通一次"：一阶高通对 55Hz 只衰减 9dB，
    // 低音整段漏进来 —— 于是"放音乐时角色声音模式跟着鼓点乱插"。
    private readonly double[] _voiceDbRing = new double[FrameRingSize];
    private double _voiceFastDb = SilenceSeedDb;
    private double _voiceSlowDb = SilenceSeedDb;

    // ── 人声强度与「她的节奏」（给人声模式用；与事件判定无关）────────────
    // 强度：中频电平归一成 0–1，起音快、回落慢 —— 人声模式的「深度」直接用它（安静时正好 0）。
    // 节奏：人声包络每鼓一次算一次「起伏」，最近 2 秒数出几次就是每秒抽插几次。
    private double _voiceLevel;
    private double _voiceLevelSlow;
    private double _voicePulseRate;
    private readonly double[] _voicePulseAt = new double[VoicePulseCapacity];
    private int _voicePulseHead;
    private int _voicePulseCount;
    private bool _voicePulseAbove;
    private double _lastVoicePulseAt = double.NegativeInfinity;

    // ── Impact ──
    private int _framesSinceQuiet;
    private double _lastImpactAt = double.NegativeInfinity;

    // ── Swell ──
    private int _elevatedFrames;
    private bool _swellArmed = true;
    private double _lastSwellAt = double.NegativeInfinity;

    // ── Voice（呻吟 / 喘息）──
    private bool _voiceArmed = true;
    private double _lastVoiceAt = double.NegativeInfinity;

    // ── Beat ──
    private readonly double[] _pulseIntervals = new double[BeatIntervalCount];
    private int _pulseHead;
    private int _pulseCount;
    private bool _aboveBeatRef;
    private bool _hasOnset;
    private double _lastOnsetAt;
    private double _lastBeatAt = double.NegativeInfinity;

    // ── 最近一次事件（跨线程只读，用 Volatile 发布）──
    private int _lastKind;
    private double _lastStrength;
    private double _lastAtSeconds = double.NegativeInfinity;
    private long _eventCount;

    /// <summary>当前采样率（0 = 还没喂过数据）。</summary>
    public int SampleRate => _sampleRate;

    /// <summary>最近一次检出的事件类型；从没检出过就是 None。</summary>
    public MotionEventKind LastEventKind => (MotionEventKind)Volatile.Read(ref _lastKind);

    /// <summary>最近一次检出的强度 0–1。</summary>
    public double LastEventStrength => Volatile.Read(ref _lastStrength);

    /// <summary>最近一次事件的时刻（秒，与调用方传入的时间轴一致）；没有过事件就是 −∞。</summary>
    public double LastEventAtSeconds => Volatile.Read(ref _lastAtSeconds);

    /// <summary>累计检出的事件数（不受调用方输出缓冲大小影响）。</summary>
    public long DetectedCount => Volatile.Read(ref _eventCount);

    /// <summary>
    /// 现在人声响到几分（0–1，带平滑）。<b>不是</b>事件：它就是「这一刻中频有多少能量」，
    /// 安静时正好 0 —— 人声模式的「越响抽得越深」用它当深度。
    /// </summary>
    public double VoiceLevel => Volatile.Read(ref _voiceLevel);

    /// <summary>
    /// 最近 <see cref="VoicePulseWindow"/> 秒里人声包络起伏了几次 → 次/秒（她的节奏）。
    /// 数不出来（不足两次起伏、或首尾挨得太近）返回 0 —— 调用方这时候应该回落到自己的兜底频率，
    /// 而不是当成「零速度」。
    /// </summary>
    public double VoicePulseRate => Volatile.Read(ref _voicePulseRate);

    /// <summary>最近一次事件的完整信息（没有过事件时 Kind = None）。</summary>
    public MotionEvent LastEvent => new(LastEventKind, LastEventStrength, LastEventAtSeconds);

    /// <summary>距最近一次事件过了多少秒；从没检出过就是 +∞。</summary>
    public double AgeSeconds(double nowSeconds)
    {
        double at = LastEventAtSeconds;
        return double.IsNegativeInfinity(at) ? double.PositiveInfinity : Math.Max(0, nowSeconds - at);
    }

    /// <summary>喂入一块单声道采样，返回检出的事件数（已写入 <paramref name="output"/>）。</summary>
    public int Process(ReadOnlySpan<double> mono, int sampleRate, double timestampSeconds, Span<MotionEvent> output)
    {
        if (mono.Length == 0 || sampleRate <= 0) return 0;
        EnsureFormat(sampleRate);
        double inverseRate = 1.0 / sampleRate;
        int count = 0;
        for (int i = 0; i < mono.Length; i++)
        {
            double sample = mono[i];
            OnSample(double.IsFinite(sample) ? sample : 0.0, timestampSeconds + (i + 1) * inverseRate, output, ref count);
        }
        return count;
    }

    /// <summary>喂入一块单声道采样（float，WASAPI 常见格式），语义同 double 版本。</summary>
    public int Process(ReadOnlySpan<float> mono, int sampleRate, double timestampSeconds, Span<MotionEvent> output)
    {
        if (mono.Length == 0 || sampleRate <= 0) return 0;
        EnsureFormat(sampleRate);
        double inverseRate = 1.0 / sampleRate;
        int count = 0;
        for (int i = 0; i < mono.Length; i++)
        {
            float sample = mono[i];
            OnSample(float.IsFinite(sample) ? sample : 0.0, timestampSeconds + (i + 1) * inverseRate, output, ref count);
        }
        return count;
    }

    /// <summary>清空全部统计与历史（换设备 / 重新开始采集时调用）。</summary>
    public void Reset()
    {
        _frameSamples = 0;
        _frameSumSquares = 0;
        _bassSumSquares = 0;
        _lowPassState = 0;
        _frameCount = 0;
        _frameIndex = 0;
        _warmupFrames = WarmupFrames;
        Array.Clear(_bassDbRing);
        _flux.Reset();                                           // 谱通量：连 200ms 的阈值历史一起清掉
        Array.Clear(_pulseIntervals);
        _pulseHead = 0;
        _pulseCount = 0;
        _aboveBeatRef = false;
        _hasOnset = false;
        _lastOnsetAt = 0;
        _fastDb = SilenceSeedDb;
        _slowDb = SilenceSeedDb;
        _bassFastDb = SilenceSeedDb;
        _bassSlowDb = SilenceSeedDb;
        _voiceFastDb = SilenceSeedDb;
        _voiceSlowDb = SilenceSeedDb;
        Array.Clear(_voiceDbRing);
        Volatile.Write(ref _voiceLevel, 0.0);
        _voiceLevelSlow = 0;
        Volatile.Write(ref _voicePulseRate, 0.0);
        Array.Clear(_voicePulseAt);
        _voicePulseHead = 0;
        _voicePulseCount = 0;
        _voicePulseAbove = false;
        _lastVoicePulseAt = double.NegativeInfinity;
        _voiceArmed = true;
        _lastVoiceAt = double.NegativeInfinity;
        _framesSinceQuiet = 0;
        _elevatedFrames = 0;
        _swellArmed = true;
        _lastImpactAt = double.NegativeInfinity;
        _lastSwellAt = double.NegativeInfinity;
        _lastBeatAt = double.NegativeInfinity;
        Volatile.Write(ref _lastKind, (int)MotionEventKind.None);
        Volatile.Write(ref _lastStrength, 0.0);
        Volatile.Write(ref _lastAtSeconds, double.NegativeInfinity);
        Volatile.Write(ref _eventCount, 0);
    }

    private void EnsureFormat(int sampleRate)
    {
        if (sampleRate == _sampleRate) return;
        _sampleRate = sampleRate;
        _hopSamples = Math.Max(8, sampleRate / 100);             // 10ms
        _lowPassAlpha = LowPassAlpha(sampleRate);
        _flux.EnsureFormat(sampleRate);                          // 谱通量：换采样率 = 重建 FFT 与 bin 边界
        _frameSamples = 0;
        _frameSumSquares = 0;
        _bassSumSquares = 0;
        _warmupFrames = WarmupFrames;                            // 换了采样率 ⇒ 统计量重来
    }

    /// <summary>与 AudioReactiveService 的老低频同一口味：150Hz 一阶低通。</summary>
    private static double LowPassAlpha(int sampleRate) => LowPassAlpha(sampleRate, 150.0);

    /// <summary>一阶低通的系数（默认 150Hz；中频那一路用 1200Hz）。</summary>
    private static double LowPassAlpha(int sampleRate, double cutoff)
    {
        double rc = 1.0 / (2 * Math.PI * cutoff);
        double dt = 1.0 / Math.Max(1, sampleRate);
        return dt / (dt + rc);
    }

    private void OnSample(double sample, double frameAtSeconds, Span<MotionEvent> output, ref int count)
    {
        _frameSumSquares += sample * sample;
        _lowPassState += _lowPassAlpha * (sample - _lowPassState);
        _bassSumSquares += _lowPassState * _lowPassState;
        // 逐样本喂谱通量：它自己按 10ms 一跳分帧，和自己的 _hopSamples 完全同步，
        // 所以在 OnFrame 里读到的就是"这一帧"的频谱量（稳态零分配，见 SpectralFluxOnset）。
        _flux.Push(sample);
        if (++_frameSamples < _hopSamples) return;
        OnFrame(
            ToDb(Math.Sqrt(_frameSumSquares / _frameSamples)),
            ToDb(Math.Sqrt(_bassSumSquares / _frameSamples)),
            frameAtSeconds,
            output,
            ref count);
        _frameSumSquares = 0;
        _bassSumSquares = 0;
        _frameSamples = 0;
    }

    private static double ToDb(double amplitude) =>
        Math.Max(DbFloor, 20 * Math.Log10(Math.Max(amplitude, 1e-7)));

    // ── 每帧判定：全部 O(1) ──────────────────────────────────────────────
    private void OnFrame(double db, double bassDb, double atSeconds,
        Span<MotionEvent> output, ref int count)
    {
        // 人声频带电平 = FFT 直接量出来的 100–1200Hz 能量（RMS 口径，和 db 同一把尺子）。
        // 它和宽带电平之差就是"中频占了多少能量"的分贝数 —— 老的"中频占比"判据说的就是这件事，
        // 只不过以前是拿一阶高通近似（55Hz 只衰减 9dB，低音整段漏进来），现在是砖墙带边。
        double voiceDb = _flux.BandDb;
        _fastDb     += (db - _fastDb) * AlphaFast;
        _slowDb     += (db - _slowDb) * AlphaSlow;
        _voiceFastDb += (voiceDb - _voiceFastDb) * AlphaFast;
        _voiceSlowDb += (voiceDb - _voiceSlowDb) * AlphaBassSlow;
        _voiceDbRing[_frameIndex % FrameRingSize] = voiceDb;
        _bassFastDb += (bassDb - _bassFastDb) * AlphaFast;
        _bassSlowDb += (bassDb - _bassSlowDb) * AlphaBassSlow;

        _bassDbRing[_frameIndex] = bassDb;
        _frameIndex = (_frameIndex + 1) % FrameRingSize;
        _frameCount++;

        if (_warmupFrames > 0)
        {
            // 冷启动：慢线（400ms 时间常数）还没收敛，这时候算出来的「跃升」几乎全是假的。
            _warmupFrames--;
            _framesSinceQuiet = 0;
            _elevatedFrames = 0;
            _aboveBeatRef = false;
            return;
        }

        // ── 人声强度（0–1，带平滑）：给人声模式的「深度」用，和下面四个事件判定各走各的 ──
        // 起音用 80ms 时间常数（呻吟一起来就得跟上），回落用 300ms（一句呻吟里的小停顿
        // 不该把抽插幅度打断成一顿一顿的）。
        // 关键：人声电平不能只看"中频有多少能量"，还要看"中频占了多少"。
        // 这里的 voiceBassGapDb 就是"宽带电平 − 真频带电平"，也就是中频占比的分贝数：
        // FFT 的带边是砖墙（见 SpectralFluxOnset），55Hz 这类带外低音落在 −50dB 以下，
        // 纯低音再也推不高"人声电平"，于是「角色声音」模式不会跟着鼓点乱插。
        // 系数：差值 0–3dB 算全占 → 9dB 以上算没占（中频不到全带的 12.5%）；
        // 与事件判定里的 VoiceBandDominanceDb 是同一个判据（那里是 6dB 的硬门槛）。
        double voiceBassGapDb = _fastDb - _voiceFastDb;
        double voiceShare = Math.Clamp(1.0 - Math.Max(0.0, voiceBassGapDb - 3.0) / 6.0, 0, 1);
        double voiceTarget = Math.Clamp((_voiceFastDb - VoiceFloorDb) / (VoiceCeilDb - VoiceFloorDb), 0, 1) * voiceShare;
        double voiceLevel = Volatile.Read(ref _voiceLevel);
        voiceLevel += (voiceTarget - voiceLevel) * (voiceTarget > voiceLevel ? AlphaVoiceAttack : AlphaVoiceRelease);
        Volatile.Write(ref _voiceLevel, voiceLevel);

        // ── 她的节奏：人声包络每鼓一次算一次「起伏」，最近 2 秒数出几次就是每秒抽插几次 ──
        // 门槛比事件判定低得多（0.08 相对慢线）：节奏要跟的是她自己的频率
        //（颤音、喘息、一句一句地叫），不是「够不够响到算一件事」。
        double voiceSlow = _voiceLevelSlow;
        voiceSlow += (voiceLevel - voiceSlow) * AlphaVoicePulseRef;
        _voiceLevelSlow = voiceSlow;
        bool voicePulse = voiceLevel - voiceSlow >= VoicePulseRise && voiceLevel >= VoicePulseFloor;
        if (voicePulse && !_voicePulseAbove && atSeconds - _lastVoicePulseAt >= VoicePulseRefractory)
        {
            _lastVoicePulseAt = atSeconds;
            PushVoicePulse(atSeconds);
        }
        _voicePulseAbove = voicePulse;
        Volatile.Write(ref _voicePulseRate, MeasureVoicePulseRate(atSeconds));

        double rise = _fastDb - _slowDb;              // 相对长时均值的跃升（dB）
        double bassRise = _bassFastDb - _bassSlowDb;
        bool loud = _fastDb >= EventFloorDb;          // 绝对门槛：噪声底一律不算事件

        if (rise < ImpactHysteresisDb) _framesSinceQuiet = 0;
        else _framesSinceQuiet++;

        // ── Impact：跃升够大 + 上升够快 ──
        if (loud && rise >= ImpactRiseDb && _framesSinceQuiet <= ImpactRiseFrames
            && atSeconds - _lastImpactAt >= ImpactMinInterval)
        {
            double jump = Math.Clamp((rise - ImpactRiseDb) / 15.0, 0, 1);       // 9→24dB 折成 0→1
            double level = Math.Clamp((_fastDb - EventFloorDb) / 35.0, 0, 1);   // −55→−20dB 折成 0→1
            _lastImpactAt = atSeconds;
            _framesSinceQuiet = ImpactRiseFrames + 1;    // 同一次上升沿只报一次
            Emit(MotionEventKind.Impact, 0.25 + 0.55 * jump + 0.30 * level, atSeconds, output, ref count);
        }

        // ── Swell：跃升不算猛，但「一直高着」，或者低频一直在往上爬 ──
        double voiceRise = _voiceFastDb - _voiceSlowDb;
        bool broadband = rise >= SwellRiseDb;
        bool bassClimb = bassRise >= SwellBassRiseDb && BassRising(BassSlopeFrames, BassSlopeDb);
        // 人声不再并进 Swell：中频爬升现在是 Voice（见下面那块）——
        // 混在一起的话，一次呻吟只能得到"缓缓涨落"的渐强形态，而不是用户要的抽插。
        bool elevated = loud && (broadband || bassClimb);
        if (elevated)
        {
            _elevatedFrames++;
        }
        else
        {
            _elevatedFrames = 0;
            _swellArmed = true;                          // 落回基线 = 可以报下一次渐强
        }

        // 一直轰鸣（引擎、风声）时每 1.2s 允许再报一次 —— 串起来是连绵的慢动作，而不是响一下就没。
        if (!_swellArmed && elevated && atSeconds - _lastSwellAt >= SwellRepeatAfter) _swellArmed = true;

        if (_swellArmed && _elevatedFrames >= SwellHoldFrames && atSeconds - _lastSwellAt >= SwellMinInterval)
        {
            double excess = Math.Max(rise / SwellRiseDb, bassRise / SwellBassRiseDb);   // ≥1 才叫「够高」
            double climb = Math.Clamp((excess - 1.0) / 2.0, 0, 1);
            double level = Math.Clamp((_fastDb - EventFloorDb) / 35.0, 0, 1);
            _lastSwellAt = atSeconds;
            _swellArmed = false;
            Emit(MotionEventKind.Swell, 0.30 + 0.40 * climb + 0.30 * level, atSeconds, output, ref count);
        }

        // ── Voice：人声起音（呻吟 / 喘息）—— 真频带在往上爬，且绝对电平过得去 ──
        // 阈值一个没放松（3dB 跃升 + 250ms 内净涨 1.2dB）：放松会让说话和音乐也触发。
        // 最后那条"中频占比"是这轮改的重点：现在由 FFT 量真频带能量占比（中频 ≥ 全带的 25%），
        // 不再是"一阶高通后的电平 vs 宽带电平" —— 一阶高通对 55Hz 只衰减 9dB，低音能整段漏进来；
        // 换成砖墙带边之后 55Hz 落在带外 50dB 以下，放低音音乐不会再乱报呻吟。
        bool voiceOnset = loud && voiceRise >= VoiceRiseDb && VoiceRising(VoiceSlopeFrames, VoiceSlopeDb)
                          && _voiceFastDb >= _fastDb - VoiceBandDominanceDb;
        if (voiceOnset && _voiceArmed && atSeconds - _lastVoiceAt >= VoiceMinInterval)
        {
            double excess = voiceRise / VoiceRiseDb;                                 // ≥1 才叫「够高」
            double climb = Math.Clamp((excess - 1.0) / 2.0, 0, 1);
            _lastVoiceAt = atSeconds;
            _voiceArmed = false;
            Emit(MotionEventKind.Voice, 0.30 + 0.40 * climb + 0.30 * voiceLevel, atSeconds, output, ref count);
        }
        if (voiceRise < VoiceRearmDb) _voiceArmed = true;

        // ── Beat：谱通量起音 + 间隔稳定 ──
        // 起音判据从"宽带电平越过 1.2s 参考线 3dB"换成谱通量（见 SpectralFluxOnset）：
        //   ① 电平越线要涨 3dB 才算，军鼓/踩镲/钢琴/拨弦常常只让宽带电平涨 1–2dB，整条拍子就丢了；
        //   ② 反过来，渐强、贝斯线条这种"慢慢涨上去"的段落天天越线，报出来的"拍"其实是段落；
        //   ③ 谱通量量的是频谱形状的变化，只对"这一帧比上一帧多出东西"给分，两类毛病一起治。
        // 反跳、间隔统计、周期容差一字未动：只是"什么算一次起音"换了更准的度量。
        bool aboveRef = loud && _flux.IsOnset;
        if (aboveRef && !_aboveBeatRef && atSeconds - _lastOnsetAt >= BeatOnsetRefractory)
        {
            if (_hasOnset) PushInterval(atSeconds - _lastOnsetAt);
            _hasOnset = true;
            _lastOnsetAt = atSeconds;

            // 冲击级起音已经由 Impact 回应过，不再报一次 Beat（同一下声音不重复触发）。
            // 但它的间隔照样进统计：节拍的「稳」应该按真正的重音来算。
            bool medium = rise < ImpactRiseDb;
            if (medium && atSeconds - _lastBeatAt >= BeatMinInterval && TryStablePeriod(out double stability))
            {
                // 强度直接取谱通量给出的一次起音强度（越过自适应阈值的程度，0.30–1.00）。
                double jump = Math.Clamp(_flux.LastOnsetStrength, 0, 1);
                _lastBeatAt = atSeconds;
                Emit(MotionEventKind.Beat, 0.25 + 0.40 * jump + 0.30 * stability, atSeconds, output, ref count);
            }
        }
        _aboveBeatRef = aboveRef;
    }

    /// <summary>记录一次人声起伏的时刻（环形，写满后自动丢最老的）。</summary>
    private void PushVoicePulse(double atSeconds)
    {
        _voicePulseAt[_voicePulseHead] = atSeconds;
        _voicePulseHead = (_voicePulseHead + 1) % VoicePulseCapacity;
        if (_voicePulseCount < VoicePulseCapacity) _voicePulseCount++;
    }

    /// <summary>
    /// 最近 <see cref="VoicePulseWindow"/> 秒里人声起伏了几次 → 次/秒。
    ///
    /// 用「(次数 − 1) ÷ 首尾间隔」而不是「次数 ÷ 窗口」：她刚开口时窗口里只数到 2 次，
    /// 除以整个 2 秒会把它算慢一倍 —— 跟得慢半拍比不跟还难受。
    /// 数不出来返回 0（调用方回落到自己的兜底频率）。
    /// </summary>
    private double MeasureVoicePulseRate(double nowSeconds)
    {
        int count = 0;
        double oldest = 0;
        for (int i = 0; i < _voicePulseCount; i++)
        {
            double at = _voicePulseAt[(_voicePulseHead - 1 - i + VoicePulseCapacity * 2) % VoicePulseCapacity];
            if (nowSeconds - at > VoicePulseWindow) break;      // 环里是从新到旧，出了窗口后面都更旧
            oldest = at;
            count++;
        }
        if (count < 2) return 0;
        double span = nowSeconds - oldest;
        return span >= VoicePulseMinSpan ? (count - 1) / span : 0;
    }

    /// <summary>人声频段在回看窗口里是否净涨了阈值（与 <see cref="BassRising"/> 同构）。</summary>
    private bool VoiceRising(int lookbackFrames, double minRiseDb)
    {
        if (_frameCount <= lookbackFrames) return false;
        int latest = (_frameIndex - 1 + FrameRingSize) % FrameRingSize;
        int earlier = (_frameIndex - 1 - lookbackFrames + FrameRingSize * 2) % FrameRingSize;
        return _voiceDbRing[latest] - _voiceDbRing[earlier] >= minRiseDb;
    }

    /// <summary>低频在最近 <paramref name="lookbackFrames"/> 帧里是否净涨了 <paramref name="minRiseDb"/>。</summary>
    private bool BassRising(int lookbackFrames, double minRiseDb)
    {
        if (_frameCount <= lookbackFrames) return false;
        int latest = (_frameIndex - 1 + FrameRingSize) % FrameRingSize;                       // 刚写入的那一帧
        int earlier = (_frameIndex - 1 - lookbackFrames + FrameRingSize * 2) % FrameRingSize;
        return _bassDbRing[latest] - _bassDbRing[earlier] >= minRiseDb;
    }

    /// <summary>记录一次起音间隔；间隔离谱（断句 / 静音）就重新攒。</summary>
    private void PushInterval(double interval)
    {
        if (interval <= 0 || interval > 3.0) { _pulseCount = 0; return; }
        _pulseIntervals[_pulseHead] = interval;
        _pulseHead = (_pulseHead + 1) % BeatIntervalCount;
        if (_pulseCount < BeatIntervalCount) _pulseCount++;
    }

    /// <summary>最近 4 次起音间隔够不够稳：均值落在 0.25–1.2s，标准差 ≤ max(60ms, 均值×20%)。</summary>
    private bool TryStablePeriod(out double stability)
    {
        stability = 0;
        if (_pulseCount < BeatIntervalCount) return false;
        double sum = 0;
        for (int i = 0; i < BeatIntervalCount; i++) sum += _pulseIntervals[i];
        double mean = sum / BeatIntervalCount;
        if (mean < BeatMinPeriod || mean > BeatMaxPeriod) return false;
        double variance = 0;
        for (int i = 0; i < BeatIntervalCount; i++)
        {
            double delta = _pulseIntervals[i] - mean;
            variance += delta * delta;
        }
        double deviation = Math.Sqrt(variance / BeatIntervalCount);
        double tolerance = Math.Max(BeatToleranceBase, mean * BeatToleranceRatio);
        if (deviation > tolerance) return false;
        stability = Math.Clamp(1.0 - deviation / tolerance, 0, 1);
        return true;
    }

    /// <summary>发布事件：无论调用方的输出缓冲够不够，最近事件与累计数都要更新（界面靠它显示）。</summary>
    private void Emit(MotionEventKind kind, double strength, double atSeconds, Span<MotionEvent> output, ref int count)
    {
        double value = Math.Clamp(strength, 0, 1);
        Volatile.Write(ref _lastKind, (int)kind);
        Volatile.Write(ref _lastStrength, value);
        Volatile.Write(ref _lastAtSeconds, atSeconds);
        Volatile.Write(ref _eventCount, _eventCount + 1);
        if (count < output.Length) output[count++] = new MotionEvent(kind, value, atSeconds);
    }
}

/// <summary>
/// 谱通量（spectral flux）起音检测 + 真频带能量占比：给音频采集线程每 10ms 用一次。
///
/// <b>为什么这里可以用 FFT</b>：NAudio 的 <c>DataAvailable</c> 跑在 WasapiRecorder 自己起的后台采集线程上
/// （captureThread，没有 SetThreadPriority），不是 WASAPI 的实时音频线程 —— 它的预算是整个 10ms 回调周期。
/// 一次 1024 点实数 FFT 在这个线程上约 12–15µs（NAudio.Dsp.FftProcessor 用 N/2 复数 FFT + 展开，
/// 比同尺寸全复数 FFT 省一半），加上幅度谱/对数压缩/通量求和/最大值滤波/频带求和，一帧约 20–25µs，
/// 占一次回调预算 0.2–0.3%。稳态<b>零分配</b>：所有缓冲都在构造或换采样率时建好，每帧只做算术，
/// 没有 LINQ、没有闭包、没有 new（真正不能做的是分配和阻塞，不是 FFT 本身）。
///
/// <b>为什么谱通量比「低音平滑值越过自适应均值」准</b>：
///   ① 越线量的是<b>能量</b>：底鼓余韵、贝斯线、渐强段落会让那条曲线长期高于均值，
///      于是要么一直连报、要么均值被抬上去彻底漏拍；谱通量量的是<b>频谱形状的变化</b>，
///      只对"这一帧比上一帧多出东西"给正分，持续音的正差分为 0，天然不把持续段当拍。
///   ② 越线只有一条（实际上还是低频的）曲线：军鼓、踩镲、钢琴、拨弦的能量主要在中高频，
///      在那条曲线上几乎没有起伏；对数谱通量对 30Hz–8kHz 逐 bin 求和，中高频起音一样出得来。
///   ③ 先做 SuperFlux 的 3 点最大值滤波（在过去一帧的 ±<see cref="MaxFilterBins"/> 个 bin 里取最大）
///      再作差：颤音/滑音只是同一根谐波在相邻 bin 之间挪了一下，取过最大之后这点挪动不产生正差分 ——
///      这是 madmom（fps=200）那套 SuperFlux 的标准做法，专治"颤音被当成连拍"。
///
/// 阈值仍是自适应的：最近 <see cref="FluxWindowSeconds"/> 秒通量的<b>中位数 × 倍数</b>，再叠绝对下限与
/// 最小间隔（<see cref="OnsetRefractorySeconds"/>，沿用老拍点判定里 170ms 反跳的意图）。
/// 用中位数而不是均值：一次很响的撞击会把 200ms 窗口内的均值抬高一大截，紧接着的真拍就被吃掉了。
/// </summary>
internal sealed class SpectralFluxOnset
{
    /// <summary>FFT 点数（2 的幂）：1024 @48k = 21.3ms 窗、46.9Hz 一个 bin，和 10ms 帧步长刚好搭。</summary>
    public const int FftSize = 1024;

    /// <summary>实数 FFT 输出的半谱长度（N/2 + 1）。</summary>
    private const int SpectrumLength = FftSize / 2 + 1;

    /// <summary>人声频带下沿：男声基频常在 85–180Hz，起点定在 100Hz 才收得住男声；
    /// 而 55Hz 那种低音离这条线还有一整个主瓣的距离（Hann 窗下泄漏约 −57dB），照样进不来。</summary>
    public const double VoiceBandLowHz = 100.0;

    /// <summary>人声频带上沿：呻吟/说话的基频与第一、第二共振峰都在 1200Hz 以下。</summary>
    public const double VoiceBandHighHz = 1200.0;

    // ── 谱通量参数 ──────────────────────────────────────────────────────
    /// <summary>对数压缩系数：log(1 + γ·|X|)。不压缩的话一段很响的素材会把弱起音全淹没。</summary>
    private const double LogCompression = 1000.0;

    /// <summary>SuperFlux 最大值滤波半径（bin）：在过去一帧的 ±3 个 bin 里取最大再作差。</summary>
    private const int MaxFilterBins = 3;

    private const double FluxLowHz  = 30.0;      // 通量统计下沿：更低的是直流/轰鸣，没有起音信息
    private const double FluxHighHz = 8000.0;    // 上沿：再往上是噪声主导，白花 CPU

    /// <summary>软膝：per-bin 平均通量 → 0..1（1.0 已经是很强的起音）。归一化之后绝对下限才有意义。</summary>
    private const double FluxKnee = 1.0;

    private const double FluxWindowSeconds = 0.200;       // 自适应阈值的中位数窗口
    private const int FluxHistoryCapacity  = 64;          // 环形缓冲容量（够放 640ms）
    private const double OnsetThresholdFactor = 1.55;     // 阈值 = 中位数 × 1.55
    private const double OnsetFloor = 0.030;              // 绝对下限（归一化通量），防止静音被数值噪声点着
    private const double OnsetRefractorySeconds = 0.170;  // 最小间隔：沿用老拍点判定里 170ms 反跳的意图
    private const double PulseDecayPerFrame = 0.78;       // 拍点脉冲每帧衰减（与老代码每次回调 ×0.78 同一手感）
    private const double OnsetStrengthFloor = 0.30;       // 刚过线就 0.30，冲到满量程算 1.0
    private const double SilenceLevelDb = -60.0;          // 低于这条线（−60dBFS）不当起音

    /// <summary>Hann 窗的 RMS 增益 sqrt(3/8)：把频谱能量换算回和时域 RMS 同一把尺子。</summary>
    private const double HannRmsGain = 0.6124;

    private const double DbFloor = -90.0;

    // ── 缓冲（全都在换采样率时建好，之后每帧零分配）──────────────────────
    private readonly float[]  _ring        = new float[FftSize];
    private readonly float[]  _fftInput    = new float[FftSize];
    private readonly Complex[] _spectrum   = new Complex[SpectrumLength];
    private readonly double[] _logMag      = new double[SpectrumLength];   // 本帧对数幅度（只在通量范围内写）
    private readonly double[] _logMagRef   = new double[SpectrumLength];   // 上一帧的（供 SuperFlux 取最大）
    private readonly double[] _fluxHistory = new double[FluxHistoryCapacity];
    private readonly double[] _medianScratch = new double[FluxHistoryCapacity];

    private FftProcessor? _fft;
    private int _sampleRate;
    private int _hopSamples;                 // 一跳的样本数（= 采样率 ÷ 100，与检测器分帧完全一致）
    private double _hopSeconds = 0.010;
    private int _fluxWindowFrames = 20;      // 中位数窗口的帧数（200ms）
    private int _binLo, _binHi, _fluxBinCount;
    private int _bandLo, _bandHi;

    private int _ringWrite;
    private int _samplesSinceFrame;
    private double _frameSeconds;
    private int _fluxHead, _fluxCount;

    private double _flux;                    // 本帧归一化通量
    private double _threshold;
    private double _onsetPulse;
    private double _lastOnsetStrength;
    private double _lastOnsetAtSeconds = double.NegativeInfinity;
    private double _bandDb = DbFloor;
    private double _broadbandDb = DbFloor;
    private double _bandRatioDb = DbFloor;
    private bool _isOnset;

    /// <summary>本帧是不是一次起音（已过自适应阈值、最小间隔与静音门）。</summary>
    public bool IsOnset => _isOnset;

    /// <summary>拍点脉冲 0–1：起音当帧跳到这次起音的强度，之后每帧 ×0.78 衰减。</summary>
    public double Pulse => _onsetPulse;

    /// <summary>最近一次起音的强度 0–1（越过阈值的程度，0.30–1.00）。</summary>
    public double LastOnsetStrength => _lastOnsetStrength;

    /// <summary>本帧归一化通量 0–1（诊断用）。</summary>
    public double Flux => _flux;

    /// <summary>
    /// 节拍跟踪：从起音包络里估 BPM 与拍点相位。放在检测器里而不是另一个服务里，
    /// 是因为只有这里才知道"这一帧有多像一次起音"（10ms 分辨率，正是自相关需要的）。
    /// </summary>
    public TempoTracker Tempo { get; } = new();

    /// <summary>本帧的自适应阈值（诊断用）。</summary>
    public double Threshold => _threshold;

    /// <summary>人声频带（100–1200Hz）的 RMS 电平（dBFS）—— 和时域 RMS 同一把尺子。</summary>
    public double BandDb => _bandDb;

    /// <summary>全带 RMS 电平（dBFS），由同一帧频谱算出（诊断用）。</summary>
    public double BroadbandDb => _broadbandDb;

    /// <summary>人声频带能量占全带能量的比例（dB）。0 = 全在带内；−9dB ≈ 只有 12.5%。</summary>
    public double BandRatioDb => _bandRatioDb;

    /// <summary>采样率变了就重建 FFT 与 bin 边界；没变是空操作（每块调一次也不花钱）。</summary>
    public void EnsureFormat(int sampleRate)
    {
        if (sampleRate <= 0 || sampleRate == _sampleRate) return;
        _sampleRate = sampleRate;
        _hopSamples = Math.Max(8, sampleRate / 100);                  // 10ms：与检测器的分帧同步
        _hopSeconds = _hopSamples / (double)sampleRate;
        _fluxWindowFrames = Math.Clamp((int)Math.Round(FluxWindowSeconds / _hopSeconds), 4, FluxHistoryCapacity);
        double binHz = sampleRate / (double)FftSize;
        _binLo = Math.Max(1, (int)Math.Ceiling(FluxLowHz / binHz));
        _binHi = Math.Min(SpectrumLength - 2, (int)Math.Floor(FluxHighHz / binHz));
        if (_binHi - _binLo < 4) { _binLo = 1; _binHi = SpectrumLength - 2; }
        _fluxBinCount = _binHi - _binLo + 1;
        // 频带边界按 bin 中心频率取：宁可少算一格，也不让带外的强低频从主瓣漏进来。
        _bandLo = Math.Max(1, (int)Math.Ceiling(VoiceBandLowHz / binHz));
        _bandHi = Math.Min(SpectrumLength - 2, (int)Math.Floor(VoiceBandHighHz / binHz));
        if (_bandHi <= _bandLo) { _bandLo = 1; _bandHi = SpectrumLength - 2; }
        // 窗口与 FFT 处理器只在这里建一次：稳态每帧不再分配任何东西。
        _fft = new FftProcessor(FftSize, FftWindowType.Hann);
        Reset();
    }

    /// <summary>清空全部历史（换设备 / 重新采集时调用）；FFT 处理器与 bin 边界保留。</summary>
    public void Reset()
    {
        Array.Clear(_ring);
        Array.Clear(_logMag);
        Array.Clear(_logMagRef);
        Array.Clear(_fluxHistory);
        _ringWrite = 0;
        _samplesSinceFrame = 0;
        _frameSeconds = 0;
        _fluxHead = 0;
        _fluxCount = 0;
        _flux = 0;
        _threshold = 0;
        _onsetPulse = 0;
        _lastOnsetStrength = 0;
        _lastOnsetAtSeconds = double.NegativeInfinity;
        _bandDb = _broadbandDb = _bandRatioDb = DbFloor;
        _isOnset = false;
        // 节拍跟踪也要一起清：不清的话换歌之后 6 秒窗口里还混着上一段的包络、
        // _recentOnsets 里还留着最多 12 秒的旧起音，可能立刻 Locked 并按旧相位先踩两下。
        Tempo.Reset();
    }

    /// <summary>喂一个单声道采样；每满一跳（10ms）算一帧频谱与通量。</summary>
    public void Push(double sample)
    {
        if (_fft == null || _hopSamples <= 0) return;
        _ring[_ringWrite] = (float)sample;
        _ringWrite = (_ringWrite + 1) & (FftSize - 1);
        if (++_samplesSinceFrame < _hopSamples) return;
        _samplesSinceFrame = 0;
        ComputeFrame();
    }

    private void ComputeFrame()
    {
        // ① 取最近 N 个样本（写指针指的就是最老的那个），顺手去掉直流分量：
        //    Hann 窗的主瓣会把直流泄漏到最前面几个 bin；不去掉，低频和人声频带都会被污染
        //    （一阶高通根本没有去直流这一说，这也是老判据漏低频的原因之一）。
        double sum = 0;
        for (int i = 0; i < FftSize; i++)
        {
            double value = _ring[(_ringWrite + i) & (FftSize - 1)];
            _fftInput[i] = (float)value;
            sum += value;
        }
        float mean = (float)(sum / FftSize);
        for (int i = 0; i < FftSize; i++) _fftInput[i] -= mean;

        // NAudio 的 FftProcessor：内部加 Hann 窗（系数预计算）+ 1/N 缩放 + N/2 复数 FFT 展开；
        // Span 重载，传数组不会装箱也不会复制 —— 这是选它而不是自己写 FFT 的原因。
        _fft!.RealForward(_fftInput, _spectrum);

        // ② 幅度谱：totalPower 是全带（当分母），bandPower 是人声频带（当分子）。
        //    直流与 Nyquist 只算一次，其余 bin 乘 2（正负频率各占一半能量）。
        double bandPower = 0, totalPower = 0;
        for (int k = 0; k < SpectrumLength; k++)
        {
            Complex c = _spectrum[k];
            double re = c.Real;
            double im = c.Imaginary;
            double mag = Math.Sqrt(re * re + im * im);
            double power = (k == 0 || k == SpectrumLength - 1) ? mag * mag : 2.0 * mag * mag;
            totalPower += power;
            if (k >= _bandLo && k <= _bandHi) bandPower += power;
            if (k >= _binLo && k <= _binHi) _logMag[k] = Math.Log(1.0 + LogCompression * mag);
        }

        // ③ SuperFlux 谱通量：参考值 = **上一帧**在 ±MaxFilterBins 里的最大值，再取正差分求和。
        //    必须先算完所有 bin 的差分再更新 _logMagRef（否则左邻居会被本帧覆盖掉）。
        double flux = 0;
        for (int k = _binLo; k <= _binHi; k++)
        {
            int lo = Math.Max(_binLo, k - MaxFilterBins);
            int hi = Math.Min(_binHi, k + MaxFilterBins);
            double reference = _logMagRef[lo];
            for (int j = lo + 1; j <= hi; j++)
                if (_logMagRef[j] > reference) reference = _logMagRef[j];
            double diff = _logMag[k] - reference;
            if (diff > 0) flux += diff;
        }
        for (int k = _binLo; k <= _binHi; k++) _logMagRef[k] = _logMag[k];

        // 除以 bin 数：不同采样率的 bin 数不一样，除完阈值才有可比性；再过一个软膝压到 0..1。
        double perBin = flux / _fluxBinCount;
        _flux = perBin / (perBin + FluxKnee);

        // ④ 自适应阈值：最近 200ms 通量的中位数 × 1.55，且不低于绝对下限。
        PushFluxHistory(_flux);
        _threshold = Math.Max(MedianFlux() * OnsetThresholdFactor, OnsetFloor);

        // ⑤ 能量与真频带占比（dB）：除以 Hann 窗的 RMS 增益，换算回时域 RMS 口径，
        //    这样"宽带电平 − 频带电平"就直接是占比的分贝数，和老的判据口径一致。
        _broadbandDb = ToDb(Math.Sqrt(totalPower) / HannRmsGain);
        _bandDb = ToDb(Math.Sqrt(bandPower) / HannRmsGain);
        _bandRatioDb = totalPower > 1e-20
            ? 10.0 * Math.Log10(Math.Max(bandPower / totalPower, 1e-10))
            : DbFloor;

        // ⑥ 起音判定 + 脉冲衰减（帧时钟自己走，不依赖系统时间，回调里也没有锁）。
        _frameSeconds += _hopSeconds;
        _isOnset = false;
        if (_broadbandDb > SilenceLevelDb && _flux > _threshold
            && _frameSeconds - _lastOnsetAtSeconds >= OnsetRefractorySeconds)
        {
            double over = Math.Clamp((_flux - _threshold) / Math.Max(0.02, 1.0 - _threshold), 0, 1);
            _lastOnsetStrength = OnsetStrengthFloor + (1.0 - OnsetStrengthFloor) * over;
            _lastOnsetAtSeconds = _frameSeconds;
            _isOnset = true;
            _onsetPulse = Math.Max(_onsetPulse * PulseDecayPerFrame, _lastOnsetStrength);
        }
        else
        {
            _onsetPulse *= PulseDecayPerFrame;
        }

        // ⑦ 节拍跟踪：把"这一帧有多像一次起音"喂给 tempo 估计。
        //    用衰减脉冲 _onsetPulse 而不是 _lastOnsetStrength —— 前者是连续的包络，后者只在起音那一帧更新。
        Tempo.Push(_onsetPulse, _hopSeconds);
    }

    private void PushFluxHistory(double value)
    {
        _fluxHistory[_fluxHead] = value;
        _fluxHead = (_fluxHead + 1) % FluxHistoryCapacity;
        if (_fluxCount < _fluxWindowFrames) _fluxCount++;
    }

    /// <summary>最近窗口内通量的中位数（插入排序：几十个数，比快排快，而且不分配）。</summary>
    private double MedianFlux()
    {
        int count = _fluxCount;
        if (count <= 0) return 0;
        for (int i = 0; i < count; i++)
            _medianScratch[i] = _fluxHistory[(_fluxHead - 1 - i + FluxHistoryCapacity * 2) % FluxHistoryCapacity];
        for (int i = 1; i < count; i++)
        {
            double value = _medianScratch[i];
            int j = i - 1;
            while (j >= 0 && _medianScratch[j] > value) { _medianScratch[j + 1] = _medianScratch[j]; j--; }
            _medianScratch[j + 1] = value;
        }
        return _medianScratch[count / 2];
    }

    private static double ToDb(double amplitude) =>
        Math.Max(DbFloor, 20 * Math.Log10(Math.Max(amplitude, 1e-7)));
}

