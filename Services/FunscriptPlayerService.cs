using System.Diagnostics;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// Funscript 同步播放器 — 把一个已解析的六轴轨道集按时间轴时钟采样，
/// 逐帧驱动 MotionEngine（seek/play/pause/status），并可与外部视频播放器/游戏 mod 以 /time /seek 同步。
///
/// <b>逐帧播放跑在引擎的输出节拍线程上，不在 UI 线程</b>：
/// 原来是 UI 线程的 DispatcherTimer(16ms)，界面一忙（列表渲染、布局、切页）就掉帧，
/// 20/秒会掉到 12–18/秒，而且同样受 15.6ms 定时器粒度量化、抖动 ±8ms 级。
/// 现在挂在 <see cref="MotionEngine.OutputClock"/> 上（专用高优先级线程 + 固定拍子），
/// 代价是状态必须自己保护：
/// ① 逐帧状态用 <see cref="_stateGate"/> 保护（节拍线程逐帧算，UI 线程 Load/Seek/Stop 重置）；
/// ② 任何界面更新都必须走 <see cref="RaiseStatusChanged"/>（内部走 App.Dispatch）——
///    从后台线程直接碰控件会抛跨线程异常；
/// ③ <see cref="_generation"/> 与引擎同构：Pause/Stop/Load/Seek/急停都会 +1，
///    正在跑的那一帧发现令牌变了就整帧作废，绝不把上一份脚本的姿态发出去。
/// </summary>
public sealed class FunscriptPlayerService : IDisposable
{
    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;

    /// <summary>挂在引擎输出节拍上的逐帧任务（原来是 DispatcherTimer）。</summary>
    private HighResolutionTickThread.Slot? _tickSlot;
    /// <summary>保护逐帧状态：节拍线程改，UI 线程 Load/Seek/Stop 也会重置。</summary>
    private readonly object _stateGate = new();
    /// <summary>失效令牌，语义与 <see cref="MotionEngine"/> 的 _generation 一致。</summary>
    private long _generation;
    private volatile FunscriptTrackSet? _trackSet;
    private long _positionMs;
    private long _lastTickAt;
    private volatile bool _playing;
    private volatile bool _disposed;
    /// <summary>上一帧逐帧下发是否被引擎拒绝（只在成功/失败翻转时广播状态，避免每帧刷 UI）。</summary>
    private bool _lastDispatchRejected;

    // ── 播放增强（多轴联动 / 空档填缝），默认都关 ──────────────────────
    private readonly ScriptAxisLinker _linker = new();
    /// <summary>脚本自己写了动作的轴。联动只碰 false 的位——写了的轴是作者的意图，改了就是篡改创作。</summary>
    private readonly bool[] _scriptedAxes = new bool[6];
    /// <summary>整条 L0 轨道的位置范围，用来把联动信号归一化（脚本只用小行程时，派生轴不该甩出大摆幅）。</summary>
    private double _l0Min = 50, _l0Max = 50;
    private readonly double[] _fillBase = new double[6];      // 填缝前的六轴（脚本 + 联动）
    private readonly double[] _fillApplied = new double[6];   // 当前实际挂着的填缝量（斜率限制后的结果）
    /// <summary>每条主轴轨道预处理好的平滑曲线（载入时算一次）。</summary>
    private readonly Dictionary<string, ScriptSmoothingCurve> _curves = new(StringComparer.OrdinalIgnoreCase);
    private double _smoothBlend;                               // 0 = 直线，1 = 曲线（切换时平滑过渡）
    private double _fillPhase;                                 // 填缝摆动相位 0–1（跨帧连续）
    private double _fillGain;                                  // 填缝幅度增益 0–1（跟着响度走，带斜率限制）
    private double _fillReleaseSeconds;                        // 收尾已经走了多久（>0 表示正在收回）
    private double _fillGainAtRelease;                         // 收尾开始时的增益（smoothstep 的起点）
    private IReadOnlyList<ScriptGap> _gaps = [];
    private long _gapsFromMinMs = -1;
    /// <summary>volatile：节拍线程逐帧写，界面线程读（见 GapFilling）。</summary>
    private volatile bool _gapFilledThisFrame;

    /// <summary>开关「脚本平滑」时，用多久从直线过渡到平滑曲线（秒）：避免播到一半切换造成位置跳变。</summary>
    private const double SmoothBlendSeconds = 0.35;

    /// <summary>填缝量每帧最多变化多少（轴行程百分比/秒）。见 ApplyFillSlew 里的实测原因。</summary>
    private const double FillSlewPerSecond = 25.0;

    /// <summary>填缝增益每秒最多变化多少（0–1/秒）：声音突然来了也不能瞬间起摆。</summary>
    private const double FillGainPerSecond = 1.5;

    /// <summary>声音停下后，填缝用多久平滑收回保持位置（秒）。见 AdvanceFillGain 的说明。</summary>
    private const double FillReleaseSeconds = 0.7;

    /// <summary>轴顺序（唯一真源见 <see cref="Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxisOrder = Osr6DeviceProfile.InstalledAxes;

    /// <summary>
    /// 脚本播放期间在引擎里登记的「直接下发控制权」名字。
    /// 用中文是有意的：AudioReactiveService.DescribeBlocked 会把它原样拼进
    /// 「「{owner}」正在控制设备」，中文才不会出现「「script」正在控制设备」这种半截话。
    /// </summary>
    private const string ScriptInputOwner = "脚本播放";

    public event Action? StatusChanged;

    public bool IsPlaying => _playing;
    /// <summary>已载入轨道但当前没有推进（界面据此在「暂停 / 继续」之间切文案）。</summary>
    public bool IsPaused => _trackSet != null && !_playing;
    public long PositionMs => Volatile.Read(ref _positionMs);
    public long DurationMs => _trackSet?.DurationMs ?? 0;
    public bool HasTrack => _trackSet != null;
    public string? LoadedFile { get; private set; }

    /// <summary>
    /// 播放位置（秒）。媒体同步是按秒对齐外挂播放器时间码的，这里把 /1000 收在一处，
    /// 免得每个调用点各写一遍换算（写错一个就是 1000 倍的位置跳变）。
    /// <b>刻意只读</b>：要跳转请用 <see cref="SeekSeconds"/> —— 一个看起来人畜无害的属性赋值
    /// 不应该让设备动起来（属性写成可写的话，谁都能顺手把设备甩到别处）。
    /// </summary>
    public double PositionSeconds => PositionMs / 1000.0;

    /// <summary>
    /// 媒体同步专用的倍率修正（1.0 = 不修正，默认值）。
    /// 为什么单独一个字段而不是直接改 <see cref="AppSettings.ScriptPlaybackSpeed"/>：
    /// 那是用户自己调、还要落盘的「脚本倍率」（本页右上角的 1.00x），同步循环在后台线程
    /// 每 80ms 改一次的话，会把用户的选择冲掉、还顺带每次写盘。这里做成一个**叠乘因子**：
    /// 实际推进速度 = ScriptPlaybackSpeed × MediaSyncRate，媒体同步没开时它恒为 1.0，
    /// 逐帧推进的算法与以前逐位相同（见 <see cref="Tick"/>）。
    /// 用千分比整数存（double 不能 volatile，也不保证原子读写）：这样同步线程写、
    /// 节拍线程读，永远不会读到一个撕裂的值。
    /// </summary>
    public double MediaSyncRate
    {
        get => Volatile.Read(ref _mediaSyncRatePermille) / 1000.0;
        set
        {
            double clamped = double.IsFinite(value)
                ? Math.Clamp(value, MediaSyncRateMin, MediaSyncRateMax)
                : 1.0;
            Volatile.Write(ref _mediaSyncRatePermille, (int)Math.Round(clamped * 1000.0));
        }
    }

    /// <summary>
    /// 跳到指定秒（<see cref="Seek"/> 的秒版本，媒体同步按秒对齐时用；内部仍走同一个 Seek，
    /// 语义完全一致：跳转是位置的不连续，联动/填缝状态一起清零，暂停时只渲染一帧）。
    /// </summary>
    public void SeekSeconds(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        Seek((long)Math.Round(Math.Max(0, seconds) * 1000.0));
    }

    /// <summary>媒体同步倍率的允许区间（下界够慢放、上界够 2 倍速播放器 + 追赶量）。</summary>
    public const double MediaSyncRateMin = 0.25;
    public const double MediaSyncRateMax = 4.0;
    /// <summary>
    /// 脚本实际推进速度的硬区间（0.25–2.0）。与 <see cref="AppSettings.Normalize"/> 对
    /// ScriptPlaybackSpeed 的夹取范围一致：用户自己调的倍率本来就在这个区间里，
    /// 这里多夹一道是防**媒体同步**那条路（外挂播放器倍速）把速度带出规格。
    /// </summary>
    private const double ScriptPlaybackRateMin = 0.25;
    private const double ScriptPlaybackRateMax = 2.0;
    /// <summary>
    /// 媒体同步倍率的存储（千分比，见 <see cref="MediaSyncRate"/>）。
    /// 刻意<b>不</b>写 volatile：这个 int 全部读写都走 Volatile.Read/Write
    /// （把 volatile 字段当 ref 传给 Volatile 会产生 CS0420 警告，等于白写）。
    /// </summary>
    private int _mediaSyncRatePermille = 1000;

    /// <summary>
    /// 声音响度来源（0–1）。默认读 <see cref="AudioReactiveService"/> 的音量包络；
    /// 自检与单元测试可以换成假值（这样不用真的放一段音乐就能验证填缝链路）。
    /// </summary>
    public IAudioLevelSource? LevelSource { get; set; }

    /// <summary>最近一帧是否真的在填缝（界面可以据此显示「正在跟着声音补动作」）。</summary>
    public bool GapFilling => _gapFilledThisFrame;

    /// <summary>
    /// 播放增强的一行摘要（都没开时返回空字符串）。给页脚显示用，让用户一眼知道设备为什么动得更多。
    /// </summary>
    public string EnhanceSummary
    {
        get
        {
            var parts = new List<string>();
            if (_cfg.ScriptAxisLinkEnabled) parts.Add($"多轴联动 {_cfg.ScriptAxisLinkAmount:0}%");
            if (_cfg.ScriptGapFillEnabled) parts.Add($"空档填缝 ≥{_cfg.ScriptGapFillMinMs / 1000.0:0.#}s / ±{_cfg.ScriptGapFillRange:0}%");
            if (_cfg.ScriptSmoothingEnabled) parts.Add($"脚本平滑 {_cfg.ScriptSmoothingStrength:0}%");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 逐帧下发被引擎拒绝的原因（null = 可以正常驱动设备）。
    /// 界面据此常显「为什么设备不动」，避免显示「播放中」却是静默失败。
    ///
    /// 刻意<b>不再</b>把「规则引擎（声音响应 / 游戏伴随）正在接管」算成拒绝理由：
    /// <see cref="Play"/> 会先请伴随让位（App.RuleEngine.YieldToManualPlayback），
    /// 播放中它本来就是让开的状态。以前留这一条，等于在判断里写死
    /// 「开着游戏伴随就别想放脚本」—— 用户想「边玩边放脚本」根本走不通。
    /// 剩下的三条都是真的发不出去：急停锁定 / 设备没连上 / 别的自动任务占着引擎。
    /// </summary>
    public string? DirectInputBlockReason
    {
        get
        {
            if (_engine.EmergencyStopped) return "设备处于急停锁定";
            if (!_engine.CanRun) return "设备未连接或正在安全归中";
            if (_engine.IsRunning) return "引擎正被其它自动任务占用";
            return null;
        }
    }

    public FunscriptPlayerService(MotionEngine engine, AppSettings cfg, AudioReactiveService? audio = null)
    {
        _engine = engine;
        _cfg = cfg;
        if (audio != null) LevelSource = new AudioReactiveLevelSource(audio);
        // 急停必须立刻停下播放器：否则用户再点「全部归中」解锁（Home() 会清 EmergencyStopped）后，
        // 脚本会从急停时的位置继续驱动设备。订阅在 Dispose 里退订，避免播放器被回收后仍被引擎唤醒。
        _engine.StateChanged += OnEngineStateChanged;
    }

    /// <summary>加载一个 .funscript（或后缀轴文件）为六轴轨道集。</summary>
    public void Load(string path)
    {
        // 解析放锁外（可能几百毫秒的 IO）：界面在 Task.Run 上也会调它。
        FunscriptTrackSet set = FunscriptTrackLoader.LoadCompanionSet(path);
        StopTick();
        Interlocked.Increment(ref _generation);      // 上一份脚本那一帧就此作废
        _playing = false;
        lock (_stateGate)
        {
            _trackSet = set;
            _positionMs = 0;
            // 载入新脚本 = 全新的运动参考系：把联动状态、轴清单、空档表全部重算，
            // 否则上一份脚本的速度余量会当成这一份的初始速度，第一帧就甩一下。
            _linker.Reset();
            for (int i = 0; i < AxisOrder.Length; i++)
                _scriptedAxes[i] = set.Tracks.ContainsKey(AxisOrder[i]);
            (_l0Min, _l0Max) = TrackRange(set, "L0");
            _curves.Clear();
            foreach ((string axis, var points) in set.Tracks)
                _curves[axis] = ScriptSmoothing.Prepare(points);
            _smoothBlend = _cfg.ScriptSmoothingEnabled ? 1 : 0;
            _gaps = [];
            _gapsFromMinMs = -1;
            _gapFilledThisFrame = false;
            Array.Clear(_fillApplied);
            _fillGain = 0;
            _fillPhase = 0;
            _fillReleaseSeconds = 0;
        }
        _engine.SetScriptPlaying(false);
        LoadedFile = path;
        AppLogger.Info($"[Funscript] 已加载 {Path.GetFileName(path)}，持续 {set.DurationMs} ms，{set.Tracks.Count} 轨");
        RaiseStatusChanged();
    }

    /// <summary>某条轨道的位置范围（没有这条轨道时返回中位 50/50）。</summary>
    private static (double Min, double Max) TrackRange(FunscriptTrackSet set, string axis)
    {
        if (!set.Tracks.TryGetValue(axis, out var points) || points.Count == 0) return (50, 50);
        double min = 100, max = 0;
        foreach (var point in points)
        {
            if (point.Pos < min) min = point.Pos;
            if (point.Pos > max) max = point.Pos;
        }
        return (min, max);
    }

    public void Play()
    {
        if (_trackSet is not { } set) return;
        // 结束时从头播放
        if (Volatile.Read(ref _positionMs) >= set.DurationMs) Volatile.Write(ref _positionMs, 0);

        // 让位，而不是被拒绝：游戏伴随（规则引擎）正在接管时，脚本不去和它抢设备，
        // 而是请它先让开，等脚本停下（Stop / 放完）再自动接回去。
        // 这样「边玩游戏边放脚本」是个可行组合，而不是以前那样「开着伴随就放不了脚本」。
        // ?. 是必要的：单元测试与自检之外，App.RuleEngine 只在 App.Initialize 之后才存在。
        App.RuleEngine?.YieldToManualPlayback();
        // 顺手声明直接下发的控制权。伴随让位后 RuleEngineActive 变假，声音响应那条路会立刻
        // 重新接管设备（它每 tick 都拿 TryClaimDirectInput("audio")），而它和脚本都不检查
        // 别人是不是已经写过了 —— 两边交替覆盖同一根轴就是设备乱抖。脚本在场时先把控制权
        // 握在手里，Stop 时交还。force:false 是有意的：不抢已经持有控制权的模块
        //（游戏桥一有客户端就 force 抢占，那是它既有的优先级，不该被播放脚本掀掉）。
        if (!_engine.TryClaimDirectInput(ScriptInputOwner))
            AppLogger.Info($"[Funscript] 直接下发控制权在「{_engine.DirectInputOwner}」手里，脚本照常播放");

        // 播放前让引擎空闲（不干扰急停；急停状态 TrySendDirectAxes 会因 CanRun=false 被拒绝）。
        _engine.StopAll();
        _playing = true;
        _engine.SetScriptPlaying(true);
        Volatile.Write(ref _lastTickAt, Stopwatch.GetTimestamp());
        lock (_stateGate) _linker.Reset();   // 每次从头/续播都重新起步：暂停期间攒下的速度余量不能算进这一帧
        Interlocked.Increment(ref _generation);   // 上一轮残留的那一帧作废，这一轮从这里重新计数
        EnsureTick();
        // 未连接 / 急停时逐帧下发会被 TrySendDirectAxes 拒绝：这里记一条日志，
        // 界面侧由 DirectInputBlockReason 常显原因（不再静默）。
        string? blocked = DirectInputBlockReason;
        AppLogger.Info(blocked == null
            ? "[Funscript] 播放开始"
            : $"[Funscript] 播放开始，但当前无法逐帧下发：{blocked}");
        RaiseStatusChanged();
    }

    /// <summary>
    /// 暂停：只停时钟，设备保持当前姿态。
    /// <b>刻意不在这里把伴随叫回来</b>：用户按的是 ⏸，设备该停住不动；
    /// 伴随一旦接回去，中位底色立刻开始跑 —— 那是「按了暂停机器反而动起来」。
    /// 所以让位状态一路保持到 Stop（或播完），那两处才是「脚本不用设备了」的信号。
    /// </summary>
    public void Pause()
    {
        _playing = false;
        Interlocked.Increment(ref _generation);   // 正在跑的那一帧就此作废（它的令牌不再是当前值）
        StopTick();
        _engine.SetScriptPlaying(false);
        // 暂停也要交还控制权：以前只有 Stop/播完/急停才释放，暂停后 owner 还挂在「脚本播放」上，
        // 结果是「一个已经停下的脚本」永久挡住声音响应（而且提示还写着"脚本正在播放"）。
        _engine.ReleaseDirectInput(ScriptInputOwner);
        RaiseStatusChanged();
    }

    public void Toggle()
    {
        if (_playing) Pause(); else Play();
    }

    /// <summary>
    /// 停止：暂停时钟 + 停掉引擎输出 + 播放位置归零。
    /// 归零不渲染（不调 RenderFrame），避免「停止」这个动作本身把设备推到脚本开头姿态。
    /// </summary>
    public void Stop()
    {
        Pause();
        _engine.StopAll();
        Interlocked.Increment(ref _generation);
        lock (_stateGate)
        {
            _positionMs = 0;
            _linker.Reset();
            Array.Clear(_fillApplied);
            _fillGain = 0;
            _fillPhase = 0;
            _fillReleaseSeconds = 0;
        }
        // 脚本不再用设备了：交还控制权 + 请伴随接回来（伴随没开 / 设备不可动 / 没绑游戏时它自己会保持停止）。
        _engine.ReleaseDirectInput(ScriptInputOwner);
        App.RuleEngine?.ResumeAfterManualPlayback();
        RaiseStatusChanged();
    }

    /// <summary>跳到指定时间点（0..DurationMs）。暂停时只渲染一帧，播放中下一帧生效。</summary>
    public void Seek(long ms)
    {
        if (_trackSet is not { } set) return;
        lock (_stateGate)
        {
            _positionMs = Math.Clamp(ms, 0, set.DurationMs);
            // 跳转是位置的不连续：速度跟踪必须清零，否则这一跳会被当成「一帧走了几百个点」的速度，
            // 联动的扭转会瞬间甩到最大。
            _linker.Reset();
            Array.Clear(_fillApplied);   // 跳转后旧的填缝量与相位都没有意义
            _fillGain = 0;
            _fillPhase = 0;
            _fillReleaseSeconds = 0;
        }
        long generation = Interlocked.Increment(ref _generation);
        if (!_playing) RenderFrame(0, generation);
        RaiseStatusChanged();
    }

    /// <summary>把逐帧任务挂到引擎的输出节拍上（原来是在这里新建 DispatcherTimer）。</summary>
    private void EnsureTick()
    {
        lock (_stateGate)
        {
            _tickSlot ??= _engine.OutputClock.Register(16, Tick, "FunscriptTick");
        }
    }

    private void StopTick()
    {
        HighResolutionTickThread.Slot? slot;
        lock (_stateGate)
        {
            slot = _tickSlot;
            _tickSlot = null;
        }
        slot?.Dispose();
    }

    private void Tick()
    {
        // 这一代的令牌：Pause/Stop/Load/Seek/急停 都会 +1，本帧一旦发现令牌变了就整帧作废。
        long generation = Volatile.Read(ref _generation);
        if (_disposed || !_playing || _trackSet is not { } set) return;

        // 以墙钟为基准推进（节拍本身也是绝对时刻表，所以这里的间隔就是真实间隔）。
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Exchange(ref _lastTickAt, now);
        double elapsedMs = previous <= 0 ? 16 : (now - previous) / (double)Stopwatch.Frequency * 1000.0;

        // 推进速度 = 用户自己调的「脚本倍率」× 媒体同步的修正（默认 1.0 = 结果与以前逐位相同）。
        // 出口再夹一次 0.25–2.0：外挂播放器开到 4 倍速时，脚本倍率也必须留在安全区间里
        //（倍率直接决定设备速度，不能被外部播放器带着飞出规格）。
        // 用 MediaSyncRate 而不是乘一个零散字段：同步没连上 / 已断开时它被复位成 1.0，
        // 所以「播放器关了」不会留下一个改过速的脚本在跑。
        double playbackRate = Math.Clamp(
            _cfg.ScriptPlaybackSpeed * Volatile.Read(ref _mediaSyncRatePermille) / 1000.0,
            ScriptPlaybackRateMin, ScriptPlaybackRateMax);
        double advancedMs = Math.Clamp(elapsedMs, 0, 500) * playbackRate;
        long position = Volatile.Read(ref _positionMs) + (long)Math.Round(advancedMs);

        if (position >= set.DurationMs)
        {
            Volatile.Write(ref _positionMs, set.DurationMs);
            RenderFrame(advancedMs / 1000.0, generation);
            Pause();
            _engine.StopAll();
            // 放完了 = 「脚本不用设备了」：和 Stop 一样交还控制权并把伴随叫回来。
            // 这里不走 Stop()：Stop 会把播放位置归零，而放完之后位置应当停在片尾
            //（界面据此显示「已播完」，用户再点一次播放是「从头再来」）。
            _engine.ReleaseDirectInput(ScriptInputOwner);
            App.RuleEngine?.ResumeAfterManualPlayback();
            return;
        }
        Volatile.Write(ref _positionMs, position);
        RenderFrame(advancedMs / 1000.0, generation);
    }

    /// <summary>
    /// 渲染并下发一帧。<paramref name="deltaSeconds"/> 是这一帧推进的<b>脚本时间</b>（秒，已含播放倍率），
    /// 多轴联动用它算主轴速度——不能用墙上时钟，否则 0.5x 慢放时会被算成速度减半（其实脚本没变慢）。
    ///
    /// <b>算与发分开</b>：状态推进（平滑混合 / 联动 / 填缝）在 <see cref="_stateGate"/> 内算完 ——
    /// 那只是内存计算（微秒级）；串口写在锁外，因为 SerialPort.Write 最坏会阻塞几百毫秒，
    /// 不能让 UI 线程的 Load / Seek / Stop 去等它。
    /// <paramref name="generation"/> 是本帧的失效令牌：预期间被 Pause/Stop/Load/急停打断的话，
    /// 这一帧就整帧作废，绝不把上一份脚本的姿态发出去（与引擎的 _generation 同一套语义）。
    /// </summary>
    private void RenderFrame(double deltaSeconds, long generation)
    {
        if (_trackSet is not { } set) return;
        double[] values;
        lock (_stateGate)
        {
            double blendTarget = _cfg.ScriptSmoothingEnabled ? 1 : 0;
            double blendStep = Math.Max(0, deltaSeconds) / SmoothBlendSeconds;
            _smoothBlend += Math.Clamp(blendTarget - _smoothBlend, -blendStep, blendStep);
            values = Sample(set, Volatile.Read(ref _positionMs));

            // ① 多轴联动：必须只看脚本主轴本身的位置，所以放在填缝之前——
            //    先填缝会让速度跟踪读到填进去的动作，联动就变成「自己跟着自己动」。
            if (_cfg.ScriptAxisLinkEnabled)
            {
                _linker.Apply(values, _scriptedAxes,
                    Math.Clamp(_cfg.ScriptAxisLinkAmount, ScriptAxisLinker.MinAmount, ScriptAxisLinker.MaxAmount),
                    deltaSeconds, _l0Min, _l0Max);
            }

            // ② 空档填缝：在联动结果之上叠加（基数是脚本当前保持的位置）。
            //    只在「有声音事件」时动，安静时一帧都不改。
            _gapFilledThisFrame = false;
            if (_cfg.ScriptGapFillEnabled)
            {
                EnsureGaps();
                if (_gaps.Count > 0)
                {
                    Array.Copy(values, _fillBase, 6);
                    AdvanceFillGain(Math.Max(0, deltaSeconds));
                    if (_fillGain > 0.01)
                    {
                        _gapFilledThisFrame = ScriptGapFiller.TryFill(
                            values, Volatile.Read(ref _positionMs), _gaps, _cfg.ScriptGapFillRange,
                            _cfg.MotionMultiAxis, _fillGain, _fillPhase);
                    }
                    ApplyFillSlew(values, deltaSeconds);
                }
            }
            else if (_fillApplied.Any(delta => delta != 0))
            {
                // 用户中途关掉填缝：把还挂着的填缝量按同样的斜率收回去，别让设备突然跳回脚本位置。
                Array.Copy(values, _fillBase, 6);
                _fillGain = 0;
                _fillReleaseSeconds = 0;
                ApplyFillSlew(values, deltaSeconds);
            }
        }

        // 令牌变了（暂停 / 停止 / 换脚本 / 急停）：这一帧不发。
        if (generation != Volatile.Read(ref _generation)) return;

        bool sent = _engine.TrySendDirectAxes(values);
        // 下发成功/失败翻转时才广播（不是每帧），让页面页脚立刻显示「无法下发：原因」。
        if (sent == _lastDispatchRejected)
        {
            _lastDispatchRejected = !sent;
            if (!sent) AppLogger.Warn($"[Funscript] 逐帧下发被拒绝：{DirectInputBlockReason}");
            RaiseStatusChanged();
        }
    }

    /// <summary>
    /// 推进填缝的音量增益。
    ///
    /// 两条规则是分开的，因为两种方向的要求不一样：
    /// ① 有声音、增益要涨：正常往上爬，同时让摆动相位往前走（这才是「跟着音乐呼吸」）；
    /// ② 声音没了、增益要落：**相位冻住**，幅度按 smoothstep 在 <see cref="FillReleaseSeconds"/> 秒内降到 0。
    ///    为什么要冻相位 + S 型收尾：目标是「速度归零地停下来」。如果一边降幅度一边继续摆，
    ///    设备会带着速度停在保持位置上，限速器来不及减速就会冲过头再荡回来（实测空档段能荡 ±17 个行程点）。
    ///    收尾阶段目标速度两端都是 0，设备自然跟着停住，安静就是真的安静。
    /// </summary>
    private void AdvanceFillGain(double deltaSeconds)
    {
        if (deltaSeconds <= 0) return;
        double loudness = Math.Clamp(LevelSource?.Level ?? 0, 0, 1);
        double target = loudness <= ScriptGapFiller.SilenceLevel ? 0 : loudness;

        if (target >= _fillGain)
        {
            _fillGain = Math.Min(target, _fillGain + FillGainPerSecond * deltaSeconds);
            _fillReleaseSeconds = 0;
        }
        else
        {
            if (_fillReleaseSeconds <= 0) _fillGainAtRelease = _fillGain;   // 记下落幅起点
            _fillReleaseSeconds += deltaSeconds;
            double remain = Math.Clamp(1 - _fillReleaseSeconds / FillReleaseSeconds, 0, 1);
            _fillGain = _fillGainAtRelease * remain * remain * (3 - 2 * remain);
            if (remain <= 0) _fillGain = 0;
        }

        // 只有在「涨 / 保持」时才推进相位：收尾阶段相位冻结，见上面的说明。
        if (_fillGain > 0.01 && _fillReleaseSeconds <= 0)
            _fillPhase = (_fillPhase + deltaSeconds * ScriptGapFiller.Frequency(_fillGain)) % 1.0;
    }

    /// <summary>
    /// 把「这一帧想填的缝」按斜率限制后叠加回去。
    ///
    /// 为什么必须限斜率（实测数据）：限速器是「按目标位置追」的，目标来回太快时它来不及减速、
    /// 只能冲过目标再往回追，于是绕着保持位置上下摆（实测摆到 ±13 个行程点、要 0.9 秒才停）。
    /// 事件语汇的冲击本来就只有 60ms 出头的前冲，直接把目标甩个二三十点出去，设备根本跟不上。
    /// 这里限制的是「填缝量本身」每帧最多变化多少（150/秒），所以：
    /// ① 目标永远在限速器跟得住的范围内；② 事件结束时填缝量也是滑回 0，不会突然跳回脚本位置。
    /// </summary>
    private void ApplyFillSlew(double[] values, double deltaSeconds)
    {
        double maxStep = FillSlewPerSecond * (double.IsFinite(deltaSeconds) && deltaSeconds > 0 ? deltaSeconds : 0);
        for (int i = 0; i < 6; i++)
        {
            double ideal = values[i] - _fillBase[i];          // 这一帧理想中的填缝量（没有填缝时是 0）
            double step = ideal - _fillApplied[i];
            if (Math.Abs(step) > maxStep) step = Math.Sign(step) * maxStep;
            _fillApplied[i] += step;
            if (Math.Abs(_fillApplied[i]) < 0.001) _fillApplied[i] = 0;
            values[i] = Math.Clamp(_fillBase[i] + _fillApplied[i], 0, 100);
        }
    }

    /// <summary>
    /// 空档表按「最短空档」设置算一次就缓存（每帧重算 5000 个动作点是白烧 CPU）；
    /// 用户拖动滑杆改变设置时重算。
    /// </summary>
    private void EnsureGaps()
    {
        if (_trackSet == null) return;
        if (_gapsFromMinMs == _cfg.ScriptGapFillMinMs) return;
        _gapsFromMinMs = _cfg.ScriptGapFillMinMs;
        _gaps = _trackSet.Tracks.TryGetValue("L0", out var l0)
            ? ScriptGapFiller.FindGaps(l0, _cfg.ScriptGapFillMinMs)
            : [];
        if (_gaps.Count > 0)
            AppLogger.Info($"[Funscript] 找到 {_gaps.Count} 段空档（≥{_cfg.ScriptGapFillMinMs} ms 不动），播放时会按声音事件填缝");
    }

    /// <summary>
    /// 采样六轴。开了「脚本平滑」时走单调三次曲线，否则走原来的直线插值；
    /// 强度 = 设置值 × 过渡系数，所以播放中随时开关都是"连续地"变成另一种手感。
    /// </summary>
    private double[] Sample(FunscriptTrackSet set, long positionMs)
    {
        double strength = _cfg.ScriptSmoothingEnabled
            ? Math.Clamp(_cfg.ScriptSmoothingStrength, ScriptSmoothing.MinStrength, ScriptSmoothing.MaxStrength) * _smoothBlend
            : 0;

        var values = new double[6];
        for (int i = 0; i < 6; i++)
        {
            if (!set.Tracks.TryGetValue(AxisOrder[i], out var points))
            {
                values[i] = 50;
                continue;
            }
            values[i] = strength > 0.01 && _curves.TryGetValue(AxisOrder[i], out var curve) && !curve.IsEmpty
                ? curve.Sample(positionMs, strength)
                : SampleTrack(points, positionMs);
        }
        return values;
    }

    private static double SampleTrack(IReadOnlyList<WaveScriptCodec.ActionPoint> points, long timeMs)
    {
        if (points.Count == 0) return 50;
        if (timeMs <= points[0].At) return points[0].Pos;
        if (timeMs >= points[^1].At) return points[^1].Pos;

        int lo = 0, hi = points.Count - 1;
        while (lo + 1 < hi)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].At <= timeMs) lo = mid;
            else hi = mid;
        }
        var a = points[lo];
        var b = points[hi];
        long span = Math.Max(1, b.At - a.At);
        double fraction = (double)(timeMs - a.At) / span;
        return a.Pos + (b.Pos - a.Pos) * fraction;
    }

    /// <summary>
    /// 广播状态变化。<b>逐帧播放现在跑在节拍线程上</b>，所以任何界面更新都必须经过 App.Dispatch ——
    /// 不然从后台线程碰控件会直接抛跨线程异常（仓库里既有模式，保持一致）。
    /// 没有 Application 时（单测 / 无界面场景）Dispatch 返回 false，退回本线程直接调用。
    /// </summary>
    private void RaiseStatusChanged()
    {
        if (!App.Dispatch(() => StatusChanged?.Invoke())) StatusChanged?.Invoke();
    }

    /// <summary>
    /// 引擎状态变化（可能在引擎节拍 / 串口 / 热键线程触发）：
    /// ① 急停 → 先在本线程立刻掐掉节拍（不等 UI 线程，见 QuiesceForEmergency），
    ///    再回 UI 线程收尾（见 StopForEmergency）；
    /// ② 其它变化（规则引擎接管 / 自动任务抢占 / 断开连接）→ 播放中也要刷新页脚，
    ///    让用户看到「为什么设备不动」，而不是界面一直显示「播放中」。
    /// </summary>
    private void OnEngineStateChanged()
    {
        if (_disposed) return;
        if (_engine.EmergencyStopped)
        {
            QuiesceForEmergency();
            App.Dispatch(StopForEmergency);
            return;
        }
        if (_playing) App.Dispatch(() => StatusChanged?.Invoke());
    }

    /// <summary>
    /// 急停的第一时间处理：<b>不等节拍、也不等 UI 线程</b>。
    /// 设备侧真正的「立刻停」由引擎负责（EmergencyStop 落 DSTOP、锁输出、CanRun=false，
    /// 在跑的逐帧下发因此全部被拒绝）；这里只做播放器侧的两件事：把逐帧任务摘掉、把这一代令牌作废。
    /// 之所以要和 App.Dispatch 拆开：急停触发线程可能是串口 / 热键 / 节拍线程，而 Dispatch 是异步投递的，
    /// 等它排到 UI 线程才停发就已经晚了几十毫秒。
    /// </summary>
    private void QuiesceForEmergency()
    {
        _playing = false;
        Interlocked.Increment(ref _generation);
        StopTick();
    }

    /// <summary>
    /// 急停反应：暂停播放 + 播放位置归零（等价于 Pause() + Seek(0)，但只广播一次状态，
    /// 且不渲染归零帧，避免急停瞬间再下发一帧）。
    /// 走 UI 线程执行（这里要动让位状态与界面），并且要发生在用户点「全部归中」
    /// 之前——Home() 会清除 EmergencyStopped 并重新使能输出，如果位置还停在急停那一刻，
    /// 脚本会从中断点继续驱动设备（本轮修复的核心安全缺陷）。
    /// </summary>
    private void StopForEmergency()
    {
        if (_disposed) return;
        _playing = false;
        Interlocked.Increment(ref _generation);
        StopTick();
        _engine.SetScriptPlaying(false);
        Volatile.Write(ref _positionMs, 0);
        // 急停打断播放 = 规格里的「播放失败」那条：把控制权交还、把让位标记解掉。
        // 这里不会让设备动起来 —— ResumeAfterManualPlayback 看到 CanRun=false（急停锁定）
        // 就直接保持停止；它只是把「脚本让位中」这个状态清掉，
        // 否则急停之后用户连「开启游戏伴随」都点不动（StartLocked 一直被让位标记挡着）。
        _engine.ReleaseDirectInput(ScriptInputOwner);
        App.RuleEngine?.ResumeAfterManualPlayback();
        AppLogger.Warn("[Funscript] 急停：播放已停止，播放位置已归零（点「全部归中」不会自动续播）");
        RaiseStatusChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        _engine.StateChanged -= OnEngineStateChanged;   // 退订：引擎比播放器活得久，不退订会留着引用
        Interlocked.Increment(ref _generation);
        StopTick();
    }
}
