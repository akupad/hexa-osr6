using Hexa.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hexa.Services;

public enum MotionMode { Auto, Stroke, Custom, Telemetry }

public sealed class MotionEngine : IDisposable
{
    private static readonly string[] Axes = Osr6DeviceProfile.InstalledAxes;

    private readonly ICommandTransport _transport;
    private readonly AppSettings _cfg;
    private readonly object _stateLock = new();
    private readonly object _dispatchLock = new();
    private readonly MotionSafetyLimiter _safetyLimiter = new();
    private readonly AutoBehaviorSequencer _autoSequencer = new();
    private readonly System.Threading.Timer _watchdogTimer;

    /// <summary>
    /// 输出节拍：专用高优先级后台线程 + 固定节拍（原来的 System.Timers.Timer 全部换成挂在它上面的任务）。
    /// 引擎各模式的 tick 与 funscript 逐帧播放共用这一条线程 —— 同一时刻只有一个线程在写串口，
    /// 限速器状态不会被两条线程交错推进。见 <see cref="HighResolutionTickThread"/> 里的抖动说明。
    /// </summary>
    private readonly HighResolutionTickThread _outputClock = new("Hexa-OutputTick");

    private long _generation;
    private int _tickBusy;
    private CancellationTokenSource? _burstCts;
    private CancellationTokenSource? _homingCts;
    private bool _burstRunning;
    private double _intensityScale = 1.0;
    private readonly object _outputLock = new();
    private readonly object _telemetryLock = new();
    private readonly double[] _lastOutput = [50, 50, 50, 50, 50, 50];
    private readonly double[] _envelopeStartOutput = [50, 50, 50, 50, 50, 50];
    private readonly double[] _telemetryStart = [50, 50, 50, 50, 50, 50];
    private readonly double[] _telemetryTarget = [50, 50, 50, 50, 50, 50];
    private long _modeStartedAt;
    private long _lastOutputAt;
    private long _lastSchedulerTickAt;
    private double _audioEnergy;
    private long _audioFeaturesAt;      // 最近一次收到音频特征的时间（判断捕获是否还在跑）
    private readonly double[] _ambientScratch = new double[6];   // 氛围叠加层的临时缓冲（避免每帧分配）
    private double _ambientPhase;                                // 氛围微动的相位 0–1
    private bool _autoFromCompanion;    // 当前自动动作是不是「游戏伴随/自动跟随」启动的
    private bool _silenceHold;          // 安静超时后已停住
    private double _silenceMs;
    private double _audioBass;
    private double _audioBeat;

    // ── 动作键联动（手柄活跃度 ramp + 动作键微脉冲）────────────────
    private readonly object _actionLinkLock = new();
    private double _gamepadActivityLink;    // 0..1，来自手柄活跃度
    private double _actionPulse;            // 0..0.5，动作键触发的短暂强度脉冲
    private long _actionPulseAt;
    /// <summary>手柄活跃度到多少才算「用户正在操作」（状态板显示「你的操作」的下限，避免抖字）。</summary>
    private const double GamepadEngagedLevel = 0.2;
    /// <summary>动作键脉冲的有效窗口（秒）—— 与 EffectiveIntensity 里的线性衰减时长一致。</summary>
    private const double ActionPulseWindowSeconds = 1.2;

    private double _speed = 1.0;
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.1, 3.0);
    }
    private double _scriptSpeed = 1.0;
    public double ScriptSpeed
    {
        get => _scriptSpeed;
        set => _scriptSpeed = Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.25, 2.0);
    }
    public double IntensityScale
    {
        get => _intensityScale;
        set => _intensityScale = Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.1, 2.0);
    }

    /// <summary>
    /// 实际用于输出的有效强度 = 用户强度 + 手柄活跃度弱增益 + 动作键微脉冲。
    /// 由 SafetyStatus 无关的安全渠道导出，仅参与输出幅度；默认增益（ActionLinkEnabled
    /// 关闭）时恒等于 IntensityScale。
    ///
    /// <b>为什么「游戏伴随」接管时反而允许叠加</b>（这一条反直觉，别改回去）：
    /// 遥测 / 用户自定义规则接管时确实该独占强度 —— 那是「谁在编动作」的问题，
    /// 残留的手柄活跃度叠上去只会把规则算好的强度弄糊。
    /// 但游戏伴随不是另一个编动作的人，它是<b>背景层</b>：只负责给一个不重复的底色，
    /// 而「用户自己正在操作」（手柄在动、按了动作键）恰恰是它该跟着有反应的东西之一。
    /// 以前这里一句 <c>if (RuleEngineActive) return _intensityScale;</c> 把伴随和遥测一起挡掉，
    /// 结果 TestLab 上「操作带动机器」勾了跟没勾一样：开着伴随，设备对手柄毫无反应。
    /// 所以条件收窄成「被别人（遥测 / 用户自定义规则）独占时才拒绝」。
    /// 叠加后的值另外夹在舒适档内：舒适档是硬件安全边界，不能因为「手柄在动」被顶穿。
    /// </summary>
    private double EffectiveIntensity
    {
        get
        {
            // 非伴随的接管（遥测 / 用户自定义规则）：完全由规则/遥测强度驱动，
            // 避免残留的手柄活跃度继续叠加 —— 这里保持原行为不变。
            if (TakenOverByOthers) return _intensityScale;
            lock (_actionLinkLock)
            {
                double gamepad = _cfg.ActionLinkEnabled ? _gamepadActivityLink : 0;
                double pulse = 0;
                if (_cfg.ActionLinkEnabled && _actionPulse > 0 && _actionPulseAt > 0)
                {
                    double elapsed = Stopwatch.GetElapsedTime(_actionPulseAt).TotalSeconds;
                    pulse = _actionPulse * Math.Max(0, 1 - elapsed / ActionPulseWindowSeconds);   // 1.2s 内线性衰减
                }
                double combined = Math.Clamp(_intensityScale + gamepad * 0.25 + pulse, 0.1, 2.0);
                // 伴随接管时叠加量另设上限（见上面的「为什么」）：不超过舒适档允许的最大强度。
                return CompanionOwnsDevice
                    ? Math.Min(combined, Math.Max(0.1, ActiveComfortProfile.MaxIntensity))
                    : combined;
            }
        }
    }

    public MotionMode ActiveMode { get; set; } = MotionMode.Auto;
    public bool RuleEngineActive { get; set; }

    /// <summary>
    /// 当前接管设备的是不是「游戏伴随 / 自动跟随」的底色动作，而不是遥测或用户自定义规则。
    ///
    /// 依据 <c>_autoFromCompanion</c>：<see cref="StartRuleAuto"/>（规则引擎的伴随那条路）与
    /// <see cref="StartCompanionAuto"/>（自动跟随那条路）都置位；用户在游玩页自己按的「开始自动」不置位。
    /// 为什么要区分这两种接管：伴随是<b>背景层</b> —— 允许「操作带动机器」叠加、允许快捷键脚本叫它让位；
    /// 遥测与用户自定义规则是<b>独占层</b> —— 行为保持原样（一律拒绝别人插手）。
    /// </summary>
    public bool CompanionOwnsDevice => _autoFromCompanion && RuleEngineActive;

    /// <summary>规则引擎确实在管，但这次接管不是伴随启动的（遥测 / 用户自定义规则）= 独占接管。</summary>
    private bool TakenOverByOthers => RuleEngineActive && !CompanionOwnsDevice;

    /// <summary>
    /// 「已使能」标记。历史上它只被赋 true、Disarm() 是空实现、CanRun 也不看它 ⇒ 是个死字段。
    /// 现在让它真的有语义：Disarm() 会把它置 false（引擎拒绝一切运动），TryArm/Home 置回 true。
    /// </summary>
    public bool IsArmed { get; private set; } = true;
    public bool IsHoming { get; private set; }
    public bool EmergencyStopped { get; private set; }
    public bool CanRun => !IsHoming && !EmergencyStopped && _transport.IsOpen;
    public bool IsRunning => AutoRunning || StrokeRunning || CustomRunning || TelemetryRunning || _burstRunning || TeasingMode;

    /// <summary>
    /// 脚本（funscript）正在驱动设备。
    ///
    /// <b>为什么需要这个单独的属性</b>：脚本走的是 <see cref="TrySendDirectAxes"/> 这条"直接下发"路径，
    /// 从不置 <see cref="IsRunning"/>（那套标志位是给自动/波形/遥测这些模式用的）。于是"设备在不在动"
    /// 这个判断在各个界面各算了一遍、而且**全都漏掉了脚本**：
    /// 悬浮窗的急停因此在脚本真正驱动设备时是**禁用**的（还写着"现在没有东西在动，不用停"），
    /// 侧栏状态也显示"待机/已解锁"。这是"用户以为没在动、其实设备正在动"的安全漏洞。
    /// 现在统一以这个属性为准，别再各算一套。
    /// </summary>
    public bool ScriptPlaying { get; private set; }

    /// <summary>
    /// 统一口径：设备现在到底在不在动。界面判断"要不要能停"一律用这个。
    ///
    /// 为什么不能只列模式标志位：**直接下发源根本不置那些标志**——游戏桥与声音响应都走
    /// <see cref="TrySendDirectAxes"/>（只认 <see cref="_directInputOwner"/>），funscript 也一样。
    /// 曾经因此漏掉脚本，修完之后又发现同样的洞对"游戏桥 / 声音响应"依然存在：
    /// 桥正在驱动机器时，悬浮窗显示"待机"、急停按钮是**灰的**并提示"现在没有东西在动，不用停"，
    /// 侧栏写"已解锁"。这是"用户以为没在动、其实设备正在动"的安全漏洞。
    ///
    /// 所以这里加一层与驱动源无关的事实判据：**最近是否真的往设备写过字节**
    ///（<c>_lastOutputAt</c> 由唯一的输出出口 <see cref="TryDispatchAxes"/> 刷新）。
    /// 任何驱动源（自动/脚本/桥/音频/遥测）只要在动，这条就一定为真。
    /// </summary>
    public bool DeviceIsMoving =>
        IsRunning || ScriptPlaying || IsEasing || IsHoming
        // 直接下发源（游戏桥 / 声音响应 / 脚本）都靠这个控制权标记；它们在停下时都会
        // 主动 ReleaseDirectInput（见各自的停止路径），所以"控制权在手"就等于"正在驱动"。
        || _directInputOwner is { Length: > 0 };

    /// <summary>
    /// 最近 0.6 秒内确实往设备发过指令。
    /// **注意它不能用来判断"在不在动"**：引擎为了保活每 250ms 会发一次心跳帧（见 TryDispatchAxes
    /// 里的 heartbeatDue），空闲时也会写字节 —— 拿它当"在动"会让状态永远显示运行中。
    /// 它的用途是诊断/抖动统计（"输出还活着吗"）。
    /// </summary>
    public bool OutputActiveRecently
    {
        get
        {
            long last;
            lock (_outputLock) last = _lastOutputAt;
            return last > 0 && Stopwatch.GetElapsedTime(last).TotalSeconds < 0.6;
        }
    }

    /// <summary>只给 <see cref="Services.FunscriptPlayerService"/> 用：进/出脚本播放时置位。</summary>
    internal void SetScriptPlaying(bool playing)
    {
        if (ScriptPlaying == playing) return;
        ScriptPlaying = playing;
        NotifyStateChanged();
    }
    // 优雅过渡（BlendTo / EaseDown）期间也算「引擎在动」：这 0.3–0.6 秒里别的模块（音频响应 /
    // 游戏桥 / funscript）先让位，否则它们每 50ms 一帧会和过渡帧互相盖，看起来就是过渡被打断。
    // IsEasing 有超时自动复位兜底，所以这个收窄不会永久挡掉直接下发。
    //
    // 「被规则引擎占用」这一条也要分两种接管，理由同 CompanionOwnsDevice：
    // 伴随是背景层，它可以被脚本叫停（Play 里会先 YieldToManualPlayback）—— 这里若一律算「被占用」，
    // 「开着游戏伴随就别想放脚本」这个结论就写死在判断里了，界面侧根本走不到让位那一步。
    // 遥测 / 用户自定义规则是独占层，照样拒绝。
    public bool CanAcceptDirectInput => CanRun && !IsRunning && !IsEasing && !TakenOverByOthers;

    /// <summary>
    /// 「操作带动机器」这一刻是不是真的在叠加（手柄活跃度或动作键脉冲还有贡献）。
    /// 给状态板显示「正在响应：你的操作」用 —— 用户开着这个勾选框，最想知道的就是它到底生效没有。
    /// </summary>
    public bool ActionLinkEngaged
    {
        get
        {
            if (!_cfg.ActionLinkEnabled) return false;
            lock (_actionLinkLock)
            {
                if (_gamepadActivityLink >= GamepadEngagedLevel) return true;
                if (_actionPulse <= 0 || _actionPulseAt <= 0) return false;
                return Stopwatch.GetElapsedTime(_actionPulseAt).TotalSeconds < ActionPulseWindowSeconds;
            }
        }
    }

    // ── 直接下发的控制权（谁在驱动设备）──────────────────────────────
    // 音频响应（20Hz）和游戏桥（游戏指令）都会走 TrySendDirectAxes，
    // 以前没有任何互斥：两边交替覆盖同一根轴，表现为设备乱抖。
    // 规则：游戏桥优先——桥一有客户端就 force 抢占；音频响应拿不到就让位并说明原因。
    private volatile string? _directInputOwner;

    /// <summary>当前控制直接下发的模块（"bridge" / "audio" / null）。</summary>
    public string? DirectInputOwner => _directInputOwner;

    /// <summary>
    /// 声明直接下发控制权。force=false 时若已被别的模块占用则返回 false（调用方应让位）。
    /// </summary>
    public bool TryClaimDirectInput(string owner, bool force = false)
    {
        string? current = _directInputOwner;
        if (!force && current is not null && !string.Equals(current, owner, StringComparison.Ordinal))
            return false;
        bool changed = !string.Equals(current, owner, StringComparison.Ordinal);
        _directInputOwner = owner;
        // 控制权变化必须广播：侧栏/悬浮窗都是纯事件驱动刷新，不广播的话
        // "谁在动"要等到下一次别的事件才更新（界面会短暂说谎）。
        if (changed) NotifyStateChanged();
        return true;
    }

    /// <summary>释放控制权（只释放自己持有的）。</summary>
    public void ReleaseDirectInput(string owner)
    {
        if (!string.Equals(_directInputOwner, owner, StringComparison.Ordinal)) return;
        _directInputOwner = null;
        NotifyStateChanged();
    }
    public ComfortProfile ActiveComfortProfile => ComfortProfile.Resolve(_cfg.ComfortProfile);

    public string SafetyStatus
    {
        get
        {
            // 「必须他动手」的处置建议优先显示（例：串口卡死要拔插 USB 再点全部归中）：
            // 这比"未连接/已解锁"有用得多。注意：这条链路以前只加了接口与实现、**没有消费方**，
            // 等于那句话永远不会出现在界面上（子代理审计发现的）。
            string? advice = _transport.UserAdvice;
            if (!string.IsNullOrEmpty(advice)) return "⚠ " + advice;
            return !_transport.IsOpen
                ? "未连接"
                : EmergencyStopped
                    ? "急停锁定"
                    : IsHoming
                        ? "安全归中中"
                        : DeviceIsMoving
                            ? $"运行中 · {DriverLabel}"
                            : "已解锁";
        }
    }

    /// <summary>
    /// 这一刻是谁在驱动设备（给状态显示用）。顺序 = 谁在真正决定动作。
    /// 「脚本」这一项以前在所有界面都是缺的，用户因此看不到"脚本正在动"。
    /// </summary>
    public string DriverLabel =>
        EmergencyStopped ? "急停锁定"
        : ScriptPlaying ? "脚本播放"
        : TelemetryRunning ? "游戏遥测"
        : RuleEngineActive ? "游戏伴随"
        : AutoRunning ? "自动动作"
        : StrokeRunning || CustomRunning ? "动作播放"
        : TeasingMode || _burstRunning ? "挑逗"
        : _directInputOwner is { Length: > 0 } owner ? owner switch
        {
            "audio" => "声音响应",
            "bridge" => "游戏桥",
            "ayva" => "网页遥控器",
            "脚本播放" => "脚本播放",
            "gamepad" => "手柄",
            "editor" => "编排预览",
            "ai" => "AI 助手",
            "manual" => "手动动轴",
            "screen" => "画面跟随",
            _ => owner,
        }
        // 引擎自己的收尾动作也要有名字：DeviceIsMoving 含 IsEasing/IsHoming，
        // 少了这两个分支时会出现「运行中 · 待机」这种自相矛盾的一行（子代理审计发现）。
        : IsHoming ? "归中"
        : IsEasing ? "过渡中"
        : "待机";

    public event Action? StateChanged;

    /// <summary>
    /// 输出节拍（内部）：<see cref="FunscriptPlayerService"/> 的逐帧播放挂在这同一条线程上，
    /// 于是「脚本」与「引擎模式」永远不会同时各写一帧。类型是 internal 的，不对外暴露。
    /// </summary>
    internal HighResolutionTickThread OutputClock => _outputClock;

    private HighResolutionTickThread.Slot? _autoTimer;
    private long _autoLastTick;
    private double _arousal = 30;
    private readonly double[] _axisAmp = [35, 13, 11, 14, 10, 8];
    public string AutoPattern { get; set; } = "free_play";
    public string CurrentAutoPattern { get; private set; } = "organic_flow";
    public string CurrentAutoPatternLabel { get; private set; } = "自然流动";
    public double CurrentAutoBpm { get; private set; }
    public bool AutoBehaviorHeld { get; private set; }
    public bool AutoRunning { get; private set; }

    private HighResolutionTickThread.Slot? _telemetryTimer;
    private long _telemetryLastTick;
    private long _telemetryReceivedAt;
    private long _telemetryTargetStartedAt;
    private int _telemetryTransitionMs = 20;
    private string _telemetrySessionId = "";
    private long _telemetrySequence;
    private double _telemetrySourceIntensity = 1.0;
    public bool TelemetryRunning { get; private set; }
    public string TelemetrySource { get; private set; } = "";
    public string ActiveTelemetrySessionId
    {
        get { lock (_telemetryLock) return _telemetrySessionId; }
    }

    private HighResolutionTickThread.Slot? _strokeTimer;
    private double _strokeTime;
    private long _strokeLastTick;
    public bool StrokeRunning { get; private set; }
    public StrokePreset? CurrentStroke { get; set; }

    private HighResolutionTickThread.Slot? _customTimer;
    private double _customTime;
    private long _customLastTick;
    private double _customCycleLength = 2.0;
    public bool CustomRunning { get; private set; }
    public Dictionary<string, List<(double X, double Y)>> EditWaves { get; } = new()
    {
        ["L0"] = [], ["L1"] = [], ["L2"] = [],
        ["R0"] = [], ["R1"] = [], ["R2"] = [],
    };
    public bool[] WfAxisEnabled { get; } = new bool[6];
    public double CwCycleLen
    {
        get => _customCycleLength;
        set => _customCycleLength = Math.Clamp(double.IsFinite(value) ? value : 2.0, 0.1, 60.0);
    }
    public string EditAxis { get; set; } = "L0";
    public bool UseSmoothCustomInterpolation { get; set; } = true;

    // ── TeasingMode：精确「动 N 秒 → 停 N 秒 → 歇 N 秒」循环（对齐 c.py start_teasing）──
    private HighResolutionTickThread.Slot? _teaseTimer;
    private long _teaseLastTick;
    private long _teaseStartTs;
    private double _teaseMoveSec = 3;
    private double _teaseStopSec = 5;
    private double _teasePauseSec = 5;
    private double _teaseCycleSec = 8;
    public bool TeasingMode { get; private set; }
    public double[] CurrentTeaseParams => [_teaseMoveSec, _teaseStopSec, _teasePauseSec];

    // 各轴挑逗振幅 / 角速度(rad/s) / 相位偏置，数值来源对齐 c.py start_teasing 的振幅与速度。
    private static readonly double[] TeaseAmp = [45, 25, 20, 35, 20, 15];
    private static readonly double[] TeaseOmega =
    [
        Math.PI,                    // L0  1000ms/半周期
        Math.PI * 1000 / 1600,      // L1
        Math.PI * 1000 / 1800,      // L2
        Math.PI * 1000 / 1300,      // R0
        Math.PI * 1000 / 1800,      // R1
        Math.PI * 1000 / 2000,      // R2
    ];
    private static readonly double[] TeasePhase = [0.0, 0.9, 1.8, 2.7, 0.5, 1.4];

    private readonly List<(double X, double Y)>[] _customWaveSnapshots =
        Enumerable.Range(0, 6).Select(_ => new List<(double X, double Y)>()).ToArray();
    private readonly object _waveLock = new();

    private static readonly Dictionary<string, (int Speed, string Pattern, double L0, double L1, double L2)> QuickPresets = new()
    {
        ["gentle"]    = (40, "gentle_wave",    25,  7,  7),
        ["daily"]     = (55, "organic_flow",   38, 16, 15),
        ["crazy"]     = (90, "intense_thrust", 48, 25, 24),
        ["tease"]     = (48, "spiral_tease",   30, 10, 10),
        ["wave"]      = (62, "game_flow",      40, 20, 20),
        ["heartbeat"] = (70, "deep_pulse",     38, 12, 12),
        ["escalate"]  = (68, "edge_swirl",     35, 14, 14),
        ["edging"]    = (55, "random_micro",   26,  8,  8),
        ["ambush"]    = (85, "climax_burst",   46, 20, 20),
    };

    public MotionEngine(ICommandTransport transport, AppSettings cfg)
    {
        _transport = transport;
        _cfg = cfg;
        Speed = cfg.Speed;
        ScriptSpeed = cfg.ScriptPlaybackSpeed;
        IntensityScale = cfg.IntensityScale;
        _transport.OutputEnabled = false;
        _transport.ConnectionChanged += OnConnectionChanged;
        ResetOutputState([50, 50, 50, 50, 50, 50]);
        // 看门狗在 ThreadPool 上跑：必须兜住异常，否则设备掉线时未处理异常会直接结束进程。
        _watchdogTimer = new System.Threading.Timer(
            _ =>
            {
                try { WatchdogTick(); }
                catch (Exception ex) { AppLogger.Error("看门狗异常（已忽略，进程继续运行）", ex); }
            },
            null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public bool TryArm(out string? error)
    {
        if (!_transport.IsOpen)
        {
            error = "设备尚未连接";
            return false;
        }

        CancellationTokenSource homingCts;
        long generation;
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                StopAllLocked();
                _homingCts?.Cancel();
                _homingCts?.Dispose();
                homingCts = _homingCts = new CancellationTokenSource();
                EmergencyStopped = false;
                IsHoming = true;
                _transport.OutputEnabled = true;
                generation = _generation;
                ResetOutputState([50, 50, 50, 50, 50, 50]);
            }
            _transport.StopMotion();
            _transport.SendAxes(
                [50, 50, 50, 50, 50, 50],
                _cfg.AxisMin,
                _cfg.AxisMax,
                interpolationMs: 1200,
                changedOnly: false);
        }
        error = null;
        AppLogger.Info($"设备正在安全归中: {_transport.PortName}");
        NotifyStateChanged();
        _ = CompleteHomingAsync(generation, homingCts);
        return true;
    }

    public void Disarm()
    {
        // 设备默认始终可动：保留为兼容 no-op，不再锁定输出。
        AppLogger.Info("设备输出保持已解锁（Disarm 已兼容为 no-op）");
        NotifyStateChanged();
    }

    public void EmergencyStop()
    {
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                StopAllLocked();
                CancelHomingLocked();
                IsHoming = false;
                EmergencyStopped = true;
                RuleEngineActive = false;
                _transport.OutputEnabled = false;
            }
            _transport.StopMotion();
        }
        AppLogger.Warn("急停已触发：全部任务取消，输出保持锁定");
        NotifyStateChanged();
    }

    /// <summary>
    /// 全部归中。注意：本方法会解除急停并重新使能输出（EmergencyStopped = false），
    /// 所以所有 UI 入口在 EmergencyStopped 为真时都必须先向用户确认再调用它。
    /// </summary>
    /// <summary>用户显式点「全部归中」：会解除急停并重新使能输出（既有语义）。</summary>
    public void Home() => HomeCore(force: true);

    /// <summary>
    /// 自动收尾用（例如游戏断开后回中位）：<b>只有在"此刻没有急停"时才归中</b>。
    /// 为什么不写成"先判 EmergencyStopped 再调 Home()"：两行之间没有锁，用户正好在那个窗口按下急停时，
    /// Home() 会把急停解开并重新给设备通电 —— 一个自动路径悄悄取消了用户按下的急停。
    /// 这个入口把"判断"与"清除"放进同一把 <see cref="_stateLock"/> 里，从根上消掉竞态。
    /// </summary>
    public bool HomeUnlessEstopped() => HomeCore(force: false);

    private bool HomeCore(bool force)
    {
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                if (!force && EmergencyStopped) return false;   // 原子判断：与急停置位/清位同一把锁
                StopAllLocked();
                CancelHomingLocked();
                EmergencyStopped = false;           // 急停后按“全部归中”恢复并重新解锁输出
                IsHoming = false;
                _transport.OutputEnabled = _transport.IsOpen;
            }
            double[] center = [50, 50, 50, 50, 50, 50];
            _transport.StopMotion();
            _transport.SendAxes(center, _cfg.AxisMin, _cfg.AxisMax, interpolationMs: 1000, changedOnly: false);
            ResetOutputState(center);
        }
        NotifyStateChanged();
        return true;
    }

    /// <summary>Move one axis during an explicit calibration step.</summary>
    public bool TryMoveCalibrationAxis(string axisId, int rawValue, int interpolationMs = 1200)
    {
        int index = Array.IndexOf(Axes, axisId);
        if (index < 0 || !CanRun || RuleEngineActive) return false;
        // 输出被锁定时 SerialService.Send 会**静默丢弃**指令；这里必须先拦住并如实返回 false，
        // 否则界面会显示"已发送"，用户却看到机器一动不动（"拖到最大机器居中不动"就是这么来的）。
        if (!_transport.OutputEnabled) return false;
        lock (_dispatchLock)
        {
            if (!CanRun) return false;
            lock (_stateLock) StopAllLocked();
            _transport.StopMotion();
            int value = Math.Clamp(rawValue, 0, 9999);
            var values = GetLastOutput();
            values[index] = value / 99.99;
            _transport.Send($"{axisId}{value:D4}I{Math.Clamp(interpolationMs, 250, 5000)}");
            ResetOutputState(values);
            return true;
        }
    }

    private async Task CompleteHomingAsync(long generation, CancellationTokenSource owner)
    {
        try
        {
            await Task.Delay(1250, owner.Token).ConfigureAwait(false);
            lock (_dispatchLock)
            {
                lock (_stateLock)
                {
                    if (!ReferenceEquals(_homingCts, owner)
                        || generation != _generation
                        || !_transport.IsOpen
                        || EmergencyStopped)
                        return;
                    _homingCts = null;
                    IsHoming = false;
                    IsArmed = true;
                    _transport.OutputEnabled = true;
                    ResetOutputState([50, 50, 50, 50, 50, 50]);
                }
            }
            AppLogger.Info($"设备输出已解锁: {_transport.PortName}");
            NotifyStateChanged();
        }
        catch (OperationCanceledException) { }
        finally
        {
            owner.Dispose();
            lock (_stateLock)
            {
                if (ReferenceEquals(_homingCts, owner)) _homingCts = null;
            }
        }
    }

    /// <summary>六轴最近一次实际输出（0–100 的副本）。供 UI 预览使用，避免外部反射私有字段。</summary>
    /// <summary>
    /// 外部直接下发源（游戏桥这类"绕过引擎直写串口"的通道）把**实际发出去的那一帧**回报进来。
    /// 只更新姿态快照与时间戳，**不参与任何安全决策、不改变控制权**。
    /// 不这么做的话：3D 预览与"让位后重新对齐"看到的都是引擎自己的旧姿态 —— 用户会以为机器没动，
    /// 交接时也会因为拿错起点而跳变。
    /// </summary>
    public void ReportExternalOutput(IReadOnlyList<double> values)
    {
        if (values is null || values.Count < _lastOutput.Length) return;
        lock (_outputLock)
        {
            for (int i = 0; i < _lastOutput.Length; i++)
            {
                double v = values[i];
                if (double.IsFinite(v)) _lastOutput[i] = Math.Clamp(v, 0, 100);
            }
            _lastOutputAt = Environment.TickCount64;
        }
    }

    public double[] GetLastOutputSnapshot()
    {
        var copy = new double[6];
        lock (_outputLock) Array.Copy(_lastOutput, copy, 6);
        return copy;
    }

    private double[] GetLastOutput()
    {
        lock (_outputLock) return _lastOutput.ToArray();
    }

    public void AdjustIntensity(double delta)
    {
        if (RuleEngineActive) return;
        IntensityScale += delta;
        _cfg.IntensityScale = IntensityScale;
        NotifyStateChanged();
    }

    /// <summary>
    /// 由手柄轮询上报活跃度（0..1），映射为强度弱 ramp（见 EffectiveIntensity）。
    /// 伴随接管时照样收：伴随是背景层，用户的操作用来「带动机器」正是它该有的反应之一
    /// （见 EffectiveIntensity 的说明）。只有被遥测 / 用户自定义规则独占时才记都不记。
    /// </summary>
    public void ApplyGamepadActivity(double activity)
    {
        if (TakenOverByOthers) return;
        lock (_actionLinkLock)
            _gamepadActivityLink = Math.Clamp(double.IsFinite(activity) ? activity : 0, 0, 1);
    }

    /// <summary>
    /// 动作键按下瞬间触发一次轻量强度脉冲（边沿触发，见 EffectiveIntensity）。
    /// 判定同 ApplyGamepadActivity：伴随接管时允许，被遥测 / 自定义规则独占时拒绝。
    /// </summary>
    public void TriggerActionPulse()
    {
        if (TakenOverByOthers) return;
        lock (_actionLinkLock)
        {
            _actionPulse = Math.Min(_actionPulse + 0.12, 0.5);
            _actionPulseAt = Stopwatch.GetTimestamp();
        }
    }

    public void ApplyQuickPreset(string id)
    {
        if (RuleEngineActive || !QuickPresets.TryGetValue(id, out var preset)) return;
        AutoPattern = preset.Pattern;
        Speed = preset.Speed / 50.0;
        _axisAmp[0] = preset.L0;
        _axisAmp[1] = preset.L1;
        _axisAmp[2] = preset.L2;
        NotifyStateChanged();
    }

    public void ToggleAutoBehaviorHold()
    {
        if (!AutoRunning) return;
        AutoBehaviorHeld = _autoSequencer.ToggleHold();
        NotifyStateChanged();
    }

    public void NextAutoBehavior()
    {
        if (!AutoRunning) return;
        _autoSequencer.Next(ActiveComfortProfile);
        AutoBehaviorHeld = false;
        NotifyStateChanged();
    }

    public void EaseAutoBehavior()
    {
        if (!AutoRunning) return;
        IntensityScale = Math.Max(0.35, IntensityScale - 0.2);
        _autoSequencer.RequestTemporary("gentle_wave", 20, ActiveComfortProfile);
        AutoBehaviorHeld = false;
        NotifyStateChanged();
    }

    public void ApplyComfortProfile(string id)
    {
        _cfg.ComfortProfile = ComfortProfile.Resolve(id).Id;
        if (IntensityScale > ActiveComfortProfile.MaxIntensity)
            IntensityScale = ActiveComfortProfile.MaxIntensity;
        _cfg.IntensityScale = IntensityScale;
        _cfg.Save();
        NotifyStateChanged();
    }

    public double[] GetAxisAmp() => _axisAmp.ToArray();

    public void SetAxisAmp(int index, double value)
    {
        if ((uint)index >= _axisAmp.Length) return;
        _axisAmp[index] = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);
    }

    public bool TrySendDirectAxes(double[] values)
    {
        if (values is null) return false;
        if (!CanAcceptDirectInput) return false;
        var safe = values.Take(6).Select(value => Math.Clamp(value, 0, 100)).ToArray();
        if (safe.Length < 6) return false;
        long generation = Interlocked.Read(ref _generation);
        double deltaSeconds = SecondsSinceLastOutput(0.06);
        // ambientOverlay：精确动作（脚本/遥测/游戏桥）之上允许叠一层很轻的氛围微动（见 ApplyAmbientOverlay）。
        return TryDispatchAxes(safe, deltaSeconds, generation, changedOnly: true, ambientOverlay: true);
    }

    /// <summary>
    /// 按指定插值时间下发六轴。用于「音频响应」这类需要连续平滑运动的场景：
    /// <see cref="TrySendDirectAxes(double[])"/> 的插值时间跟着调用间隔走，
    /// 50ms 一帧配 50ms 插值会让设备一顿一顿，这里由调用方指定更长的插值时间。
    /// </summary>
    public bool TrySendDirectAxes(double[] values, double interpolationSeconds)
    {
        if (!CanAcceptDirectInput) return false;
        var safe = values.Take(6).Select(value => Math.Clamp(value, 0, 100)).ToArray();
        if (safe.Length < 6) return false;
        long generation = Interlocked.Read(ref _generation);
        return TryDispatchAxes(safe, Math.Clamp(interpolationSeconds, 0.02, 9.0), generation,
            changedOnly: true, ambientOverlay: true);
    }

    // ── 沉浸感：姿态混合 / 优雅停止 / 多轴语汇 ─────────────────────
    /// <summary>是否启用多轴动作语汇（界面上的开关，动作生成器读它）。</summary>
    public bool MultiAxisMotion => _cfg.MotionMultiAxis;

    /// <summary>画面与设备的对齐偏移（毫秒，正 = 设备晚一点动）。</summary>
    public int MotionCalibrationMs => _cfg.MotionCalibrationMs;

    /// <summary>是否正在做优雅过渡（缓降 / 姿态混合）。过渡期间为 true，结束、异常或被打断都会复位。</summary>
    public bool IsEasing { get; private set; }

    // ── 过渡（BlendTo / EaseDown）的取消与生命周期 ──────────────────
    // 每次过渡都取一个递增令牌 + 一个取消源：只有「还持有当前令牌」的收尾任务才能复位 IsEasing，
    // 于是旧过渡的延迟收尾绝不会清掉新过渡的状态；急停 / StopAll / 新过渡接手都会立刻打断旧的。
    private readonly object _easeLock = new();
    private CancellationTokenSource? _easeCts;
    private long _easeToken;

    /// <summary>缓降的步长（秒）：20ms 一步，与调度器同一个量级。</summary>
    private const double EaseStepSeconds = 0.02;

    /// <summary>每步的插值时间略长于步长（倍率），两步之间设备才不会停一下。</summary>
    private const double EaseStepInterpolationFactor = 1.35;

    /// <summary>
    /// 过渡的「到位宽限」（秒）：曲线已经走完但限速器还没把设备送到目标姿态时，最多再推这么久。
    /// 之所以需要它：限速器把单帧 dt 钳在 0.1 秒，单帧长插值在大行程时会被截断在半路，
    /// 于是长距离过渡的正确做法是「同一段曲线分帧推进」，并在曲线之后补一段到位的推力。
    /// </summary>
    private const double EaseArrivalGraceSeconds = 1.0;

    // ── 进入模式的第一帧（模式切换不跳变）───────────────────────────
    /// <summary>进入模式后的第一帧还没 dispatch：各 Start* 置位，被该模式的第一次 dispatch 消费一次。</summary>
        /// <summary>
    /// 「进入模式的第一帧要用长插值滑进去」的标记。用 int 而不是 bool：它被启动路径（持 _stateLock）
    /// 与 tick 线程（TakeBlendInSeconds，在进锁之前求值）同时读写，标成 int 才能用 Volatile/Interlocked
    /// 做无锁原子访问——加锁会在 tick 路径上引入新的锁顺序风险。
    /// </summary>
    private int _blendInPending;

    /// <summary>任一轴与当前输出姿态相差超过它，就认为「离新模式的起始姿态很远」。</summary>
    private const double BlendInThreshold = 12.0;

    /// <summary>「很远」时第一帧用的插值时间（秒）：250–400ms 区间取 300ms，替代默认的 20–60ms 硬切。</summary>
    private const double BlendInSeconds = 0.30;

    // ── 多轴语汇（引擎侧）：把「刚刚发生了什么」翻译成次要轴的附加动作 ──
    // 只用次要轴（L1/L2/R0/R1/R2）：L0 的主轴节奏归 sequencer，交给语汇会把动作风格改掉。
    private MotionEventKind _vocabKind = MotionEventKind.None;
    private double _vocabPhase = 1;         // 当前事件内部相位 0–1（1 = 没有事件在进行）
    private double _vocabDuration = 1;      // 当前事件的自然时长（秒，取自 MotionVocabulary）
    private double _vocabStrength;          // 当前事件强度 0–1（指数平滑，避免每帧跳）
    private double _vocabLastEnergy;        // 上一帧音频能量：用来识别「骤升」

    // ── 外部注入的事件（AI 当导演时用）：优先于音频判定，消费一次即失效 ──
    private int _injectedKindRaw = -1;      // MotionEventKind 的 int 值；-1 = 没有注入
    private double _injectedStrength;
    private long _injectedAtTicks;
    private const double InjectedVocabMaxAgeSeconds = 3.0;   // 3 秒没被消费就作废（别过半天突然动一下）
    private double _vocabHighSeconds;       // 能量持续偏高的累计秒数

    /// <summary>能量超过它才算「偏高」（与音频响应的安静门限同量级）。</summary>
    private const double VocabHighEnergy = 0.5;

    /// <summary>能量至少要涨这么多、且本身够响，才算一次「骤升」（爆炸 / 枪声 / 撞击）。</summary>
    private const double VocabImpactRise = 0.15;
    private const double VocabImpactEnergy = 0.35;

    /// <summary>能量持续偏高这么久才开始算「渐强」：一两个尖峰不该被当成一段长音。</summary>
    private const double VocabSwellHoldSeconds = 0.35;

    /// <summary>「其它」情况用弱化版渐强：有回应，但不抢戏。</summary>
    private const double VocabSoftSwellScale = 0.45;

    /// <summary>多轴关闭时用于钉住次要轴的复用缓冲；只在 _dispatchLock 内读写。</summary>
    private readonly double[] _gatedAxes = new double[6];

    /// <summary>
    /// 平滑过渡到目标姿态：停掉正在跑的模式（锁顺序与现有代码一致：_dispatchLock → _stateLock），
    /// 然后从<b>上一次实际下发的姿态</b>（<see cref="GetLastOutputSnapshot"/>）起，
    /// 在 <paramref name="seconds"/> 内把六轴连续送到目标姿态；期间 <see cref="IsEasing"/> 为真。
    /// 本来就没在跑模式时也照做（不重启任何东西）。
    /// 这是「一次成型的插值」而不是「一帧硬切」：整段曲线分帧推进，设备全程连续移动、
    /// 且最后一定停在目标姿态。之所以不靠单帧长插值：限速器把单帧 dt 钳在 0.1 秒，
    /// 大行程的单帧长插值会被限速器截断在中途，反而到不了目标。
    /// 安全路径不走这里：急停仍然是立即 <see cref="EmergencyStop"/> / <see cref="StopAll"/> + DSTOP。
    /// </summary>
    /// <returns>
    /// 目标为 null / 不足六轴 / 含非有限值、设备不可动（<see cref="CanRun"/> 为假）时返回 false，
    /// 且不改状态、不抛异常；过渡已启动则返回 true（过渡在后台推进，可用 <see cref="IsEasing"/> 观察）。
    /// </returns>
    public bool BlendTo(double[] target, double seconds = 0.35)
    {
        // ① 参数校验（不改任何状态）：非法目标直接安全失败。
        if (target is null || target.Length < 6 || !CanRun) return false;
        var pose = new double[6];
        for (int i = 0; i < 6; i++)
        {
            if (!double.IsFinite(target[i])) return false;
            pose[i] = Math.Clamp(target[i], 0, 100);
        }

        double blendSeconds = Math.Clamp(double.IsFinite(seconds) ? seconds : 0.35, 0.05, 3.0);
        double[] fromPose;
        long generation;
        bool stoppedMode;

        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                if (!CanRun) return false;
                stoppedMode = IsRunning || RuleEngineActive;
                fromPose = GetLastOutputSnapshot();     // 起点 = 上一次实际下发的姿态
                // ② StopAllLocked 会递增 generation：所有旧 tick / 旧 dispatch 立刻失效，
                //    不会再有帧盖掉这次混合（同时也打断上一次没跑完的缓降 / 混合）。
                if (stoppedMode) StopAllLocked();
                Volatile.Write(ref _blendInPending, 0);                // 混合接管进入过渡，作废挂起的「第一帧长插值」
                generation = ++_generation;             // 这次混合自己的 generation
            }
            // ③ 耗时 / IO 不放锁内：DSTOP 让设备停在当前位置，正好与刚记下的起点姿态对齐。
            if (stoppedMode) _transport.StopMotion();
        }

        CancellationToken token = BeginEaseWindow(blendSeconds + EaseArrivalGraceSeconds, out long easeToken);
        _ = RunPoseRampAsync(generation, easeToken, fromPose, pose, blendSeconds, token);
        return true;
    }

    /// <summary>
    /// 优雅停止：在 <paramref name="seconds"/> 内分步（20ms 一步）缓降到中位，结束后停发
    /// （设备保持中位静止）。每步的插值时间略长于步长，运动连续、不出现停顿感。
    /// 起点是「上一次实际下发的姿态」，终点是中位；曲线走完还没真的停在中位时，
    /// 最多再多花 <see cref="EaseArrivalGraceSeconds"/> 把它推到位（限速器比曲线慢的档位才会用到）。
    /// 本方法<b>立即返回</b>，缓降在后台线程上跑；<see cref="IsEasing"/> 在整个缓降期间为真，
    /// 正常结束 / 异常 / 被打断都会复位。
    /// 急停、<see cref="StopAll"/>、<see cref="CancelEase"/> 或任何 Start* 都会立刻打断它。
    /// 与急停的区别：这里刻意<b>不</b>发 DSTOP —— 优雅停止的意义就是不急停；
    /// 需要立刻停住的场景仍然走 <see cref="EmergencyStop"/> / <see cref="StopAll"/>。
    /// </summary>
    public void EaseDown(double seconds = 0.6)
    {
        if (!CanRun) return;
        double totalSeconds = Math.Clamp(double.IsFinite(seconds) ? seconds : 0.6, 0.1, 3.0);
        double[] fromPose;
        long generation;

        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                if (!CanRun) return;
                fromPose = GetLastOutputSnapshot();     // 从哪个姿态开始缓降
                StopAllLocked();                        // 停掉正在跑的模式（缓降自己接管剩下的帧）
                Volatile.Write(ref _blendInPending, 0);
                generation = ++_generation;
            }
        }

        var center = new double[6];
        for (int i = 0; i < 6; i++) center[i] = MotionVocabulary.Center;
        CancellationToken token = BeginEaseWindow(totalSeconds + EaseArrivalGraceSeconds, out long easeToken);
        _ = RunPoseRampAsync(generation, easeToken, fromPose, center, totalSeconds, token);
    }

    /// <summary>
    /// 立即打断正在进行的优雅停止 / 姿态混合（界面上的「立即停止」）：
    /// 不再发后续的缓降帧、复位 <see cref="IsEasing"/>，设备停在当前位置。
    /// 这不是急停路径：要让设备硬停请用 <see cref="StopAll"/> / <see cref="EmergencyStop"/>。
    /// </summary>
    public void CancelEase() => InterruptEase();

    /// <summary>
    /// 过渡的执行体（BlendTo 的目标姿态 / EaseDown 的缓降到中位共用）：
    /// 在 <paramref name="seconds"/> 内把六轴从 <paramref name="fromPose"/> 沿平滑曲线送到
    /// <paramref name="toPose"/>。每步 20ms 一帧：dt 用步长、设备插值时间用「比步长略长」的值，
    /// 于是设备是连续滑过去的，两步之间不会停一下。
    /// 曲线走完还没到位（限速器把速度压得比曲线更慢）时，继续以目标姿态推进，最多多花
    /// <see cref="EaseArrivalGraceSeconds" />；到位即停发，设备保持该姿态静止。
    /// 每步都检查 generation（急停 / StopAll / 新模式都会递增它）与取消令牌，任一失效就立刻收手。
    /// </summary>
    private async Task RunPoseRampAsync(
        long generation,
        long easeToken,
        double[] fromPose,
        double[] toPose,
        double seconds,
        CancellationToken token)
    {
        try
        {
            int steps = Math.Max(1, (int)Math.Round(seconds / EaseStepSeconds));
            double stepSeconds = seconds / steps;
            double interpolation = Math.Min(9.0, stepSeconds * EaseStepInterpolationFactor);
            int graceSteps = Math.Max(1, (int)Math.Round(EaseArrivalGraceSeconds / EaseStepSeconds));
            var target = new double[6];

            for (int step = 1; step <= steps + graceSteps; step++)
            {
                if (token.IsCancellationRequested
                    || generation != Interlocked.Read(ref _generation)
                    || !CanRun)
                    return;

                // 曲线段用 Smooth01（缓入缓出：起步与收尾都不生硬）；宽限段就是终点姿态本身。
                double curved = step <= steps ? Smooth01((double)step / steps) : 1.0;
                for (int i = 0; i < 6; i++)
                    target[i] = Math.Clamp(
                        fromPose[i] + (toPose[i] - fromPose[i]) * curved, 0, 100);

                // dt 用步长、插值时间用「比步长略长」的那个值：两步之间设备不会停一下；
                // changedOnly: false —— 过渡每一步都必须真的发出去（否则设备会停在半路）。
                if (!TryDispatchAxes(
                    target,
                    stepSeconds,
                    generation,
                    changedOnly: false,
                    interpolationSeconds: interpolation)) return;

                if (step >= steps && ReachedPose(target)) return;   // 到位 → 停发，设备保持该姿态
                await Task.Delay(TimeSpan.FromSeconds(stepSeconds), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("优雅过渡异常（已忽略，设备保持当前位置）", ex);
        }
        finally
        {
            EndEaseWindow(easeToken);        // 正常结束 / 异常 / 被打断都在这里复位 IsEasing
        }
    }

    /// <summary>限速器输出是否已经跟到目标姿态（每轴都在半个单位以内）—— 决定「到位就停发」。</summary>
    private bool ReachedPose(IReadOnlyList<double> target)
    {
        double[] current = GetLastOutputSnapshot();
        for (int i = 0; i < 6 && i < target.Count; i++)
            if (Math.Abs(current[i] - Math.Clamp(target[i], 0, 100)) > 0.5) return false;
        return true;
    }

    /// <summary>
    /// 进入一个「优雅过渡」窗口：IsEasing = true，并在 <paramref name="seconds"/> 后自动复位。
    /// 自动复位同样带令牌校验：即使过渡被打断、或执行体来不及收尾，状态也不会卡在 true。
    /// </summary>
    private CancellationToken BeginEaseWindow(double seconds, out long token)
    {
        CancellationTokenSource cts;
        lock (_easeLock)
        {
            _easeCts?.Cancel();          // 作废旧窗口（不 Dispose：由它自己的收尾任务释放）
            cts = _easeCts = new CancellationTokenSource();
            token = ++_easeToken;
            IsEasing = true;
        }
        _ = ClearEaseWindowAsync(token, cts, seconds);
        return cts.Token;
    }

    /// <summary>过渡结束（带令牌校验）：只有还是当前窗口时才复位，避免旧窗口清掉新窗口的状态。</summary>
    private void EndEaseWindow(long token) => InterruptEaseCore(token);

    /// <summary>无条件打断当前过渡：急停 / StopAll / CancelEase / 新过渡接手时使用。</summary>
    private void InterruptEase() => InterruptEaseCore(null);

    private void InterruptEaseCore(long? onlyToken)
    {
        CancellationTokenSource? cts = null;
        lock (_easeLock)
        {
            if (onlyToken.HasValue && onlyToken.Value != _easeToken) return;
            _easeToken++;
            cts = _easeCts;
            _easeCts = null;
            IsEasing = false;
        }
        if (cts is null) return;
        try { cts.Cancel(); }        // 锁外取消，避免续体在本线程内联执行时嵌套拿锁
        catch (ObjectDisposedException) { }
    }

    /// <summary>过渡窗口的自动收尾：到点后复位 IsEasing，并释放自己的取消源。</summary>
    private async Task ClearEaseWindowAsync(long token, CancellationTokenSource owner, double seconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, seconds)), owner.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            EndEaseWindow(token);
            owner.Dispose();
        }
    }

    /// <summary>
    /// 进入模式后的第一帧该用多长的插值时间。
    /// 触发条件：本帧是进入模式后的第一次 dispatch，且新模式的起始姿态（本帧<b>未</b>经过软启动的
    /// 原始落点）与当前输出姿态在任一轴上相差超过 <see cref="BlendInThreshold"/>（12）。
    /// 触发时返回 <see cref="BlendInSeconds"/>（300ms）滑过去；不触发时原样返回按帧插值，
    /// 于是各模式的稳态行为一点没变。标记只消费一次。
    /// 补充：软启动在起步阶段本就把落点拉回旧姿态，那种情况下这一帧本来就不远，长插值等于空操作；
    /// 真正被它兜住的是「软启动已经走完 / 起始姿态确实很远」的进入。
    /// </summary>
    private double TakeBlendInSeconds(IReadOnlyList<double> rawTarget, double deltaSeconds)
    {
        if (Interlocked.Exchange(ref _blendInPending, 0) == 0) return deltaSeconds;
        if (!CanRun) return deltaSeconds;

        double[] current = GetLastOutputSnapshot();
        double maxDelta = 0;
        for (int i = 0; i < 6 && i < rawTarget.Count; i++)
        {
            if (!double.IsFinite(rawTarget[i])) continue;
            maxDelta = Math.Max(maxDelta, Math.Abs(Math.Clamp(rawTarget[i], 0, 100) - current[i]));
        }
        return maxDelta > BlendInThreshold ? Math.Max(deltaSeconds, BlendInSeconds) : deltaSeconds;
    }

    public bool StartAuto()
    {
        if (RuleEngineActive) return false;
        return StartAutoCore(AutoPattern, fromCompanion: false);
    }

    /// <summary>
    /// 由「自动跟随」在进入游戏时启动：标记为伴随动作，从而受「安静就停」约束
    /// （用户自己在游玩页按的「开始自动」不受这个约束——他按了就该一直动）。
    /// </summary>
    public bool StartCompanionAuto()
    {
        if (RuleEngineActive) return false;
        return StartAutoCore(AutoPattern, fromCompanion: true);
    }

    public bool StartRuleAuto(string pattern)
    {
        if (!RuleEngineActive) return false;
        return StartAutoCore(pattern, fromCompanion: true);
    }

    private bool StartAutoCore(string pattern, bool fromCompanion)
    {
        if (!CanRun) return false;
        lock (_stateLock)
        {
            if (!CanRun) return false;
            StopAllLocked();
            ActiveMode = MotionMode.Auto;
            AutoPattern = pattern;
            _autoLastTick = Stopwatch.GetTimestamp();
            _arousal = 30;
            AutoRunning = true;
            _autoFromCompanion = fromCompanion;
            _silenceHold = false;
            _silenceMs = 0;
            ResetComfortEnvelope();
            Volatile.Write(ref _blendInPending, 1);     // 第一帧：离当前姿态远就长插值滑进去（见 TakeBlendInSeconds）
            long generation = ++_generation;
            _autoSequencer.Reset(pattern, Speed, ActiveComfortProfile);
            AutoBehaviorHeld = false;
            _autoTimer = MakeTimer(20, generation, AutoTick);
        }
        NotifyStateChanged();
        return true;
    }

    public void StopAuto()
    {
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                _generation++;
                AutoRunning = false;
                _autoFromCompanion = false;
                _silenceHold = false;
                DisposeTimer(ref _autoTimer);
            }
            _transport.StopMotion();
        }
        NotifyStateChanged();
    }

    public bool StartTelemetrySync()
    {
        if (!RuleEngineActive || !CanRun) return false;
        lock (_stateLock)
        {
            if (!RuleEngineActive || !CanRun) return false;
            StopAllLocked();
            lock (_telemetryLock)
            {
                Array.Copy(GetLastOutput(), _telemetryStart, 6);
                Array.Copy(_telemetryStart, _telemetryTarget, 6);
                _telemetryTargetStartedAt = Stopwatch.GetTimestamp();
                _telemetryTransitionMs = 20;
                _telemetrySessionId = "manual";
                _telemetrySequence = 0;
                _telemetrySourceIntensity = 1.0;
                TelemetrySource = "";
            }
            // 遥测刻意不置 _blendInPending：它的「起始姿态」按定义就是当前姿态
            // （上面刚把 GetLastOutput 抄进 _telemetryStart），条件天然不成立；
            // 而且游戏桥自带 transitionMs 与延迟补偿，这里再拉长插值只会让画面与设备错开。
            ActiveMode = MotionMode.Telemetry;
            TelemetryRunning = true;
            _telemetryLastTick = Stopwatch.GetTimestamp();
            Volatile.Write(ref _telemetryReceivedAt, _telemetryLastTick);
            ResetComfortEnvelope();
            long generation = ++_generation;
            _telemetryTimer = MakeTimer(20, generation, TelemetryTick);
        }
        NotifyStateChanged();
        return true;
    }

    public bool SetTelemetryTarget(IReadOnlyList<double> values, string source, int transitionMs = 20)
    {
        string sessionId;
        long sequence;
        lock (_telemetryLock)
        {
            sessionId = string.IsNullOrWhiteSpace(_telemetrySessionId) ? "manual" : _telemetrySessionId;
            sequence = _telemetrySequence + 1;
        }
        return ApplyTelemetryFrame(sessionId, sequence, values, source, transitionMs, 1.0);
    }

    /// <summary>
    /// Atomically starts or updates one game telemetry session. The caller must
    /// provide a strictly increasing sequence for each session.
    /// </summary>
    public bool ApplyTelemetryFrame(
        string sessionId,
        long sequence,
        IReadOnlyList<double> values,
        string source,
        int transitionMs = 20,
        double sourceIntensity = 1.0)
    {
        if (values.Count < 6 || sequence <= 0) return false;
        sessionId = (sessionId ?? "").Trim();
        if (sessionId.Length == 0 || sessionId.Length > 128) return false;

        bool started = false;
        lock (_stateLock)
        {
            if (!RuleEngineActive || !CanRun) return false;
            lock (_telemetryLock)
            {
                bool newSession = !TelemetryRunning
                    || !string.Equals(_telemetrySessionId, sessionId, StringComparison.Ordinal);
                if (!newSession && sequence <= _telemetrySequence) return false;

                long now = Stopwatch.GetTimestamp();
                if (newSession)
                {
                    StopAllLocked();
                    Array.Copy(GetLastOutput(), _telemetryStart, 6);
                    Array.Copy(_telemetryStart, _telemetryTarget, 6);
                    _telemetrySessionId = sessionId;
                    _telemetrySequence = 0;
                    _telemetrySourceIntensity = 1.0;
                    ActiveMode = MotionMode.Telemetry;
                    TelemetryRunning = true;
                    _telemetryLastTick = now;
                    ResetComfortEnvelope();
                    long generation = ++_generation;
                    _telemetryTimer = MakeTimer(20, generation, TelemetryTick);
                    started = true;
                }

                double[] current = SampleTelemetryTargetLocked(now);
                Array.Copy(current, _telemetryStart, 6);
                for (int i = 0; i < 6; i++)
                    _telemetryTarget[i] = Math.Clamp(double.IsFinite(values[i]) ? values[i] : 50, 0, 100);
                _telemetryTargetStartedAt = now;
                _telemetryTransitionMs = Math.Clamp(transitionMs, 1, 9999);
                _telemetrySourceIntensity = Math.Clamp(
                    double.IsFinite(sourceIntensity) ? sourceIntensity : 1.0, 0.1, 2.0);
                _telemetrySequence = sequence;
                TelemetrySource = source?.Trim() ?? "";
                Volatile.Write(ref _telemetryReceivedAt, now);
            }
        }
        if (started) NotifyStateChanged();
        return true;
    }

    public void StopTelemetrySync() => StopTelemetrySession(null);

    public bool StopTelemetrySession(string? sessionId)
    {
        bool stopped;
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                if (sessionId != null
                    && (!TelemetryRunning
                        || !string.Equals(ActiveTelemetrySessionId, sessionId, StringComparison.Ordinal)))
                    return false;
                stopped = TelemetryRunning;
                StopAllLocked();
            }
            if (stopped || sessionId == null) _transport.StopMotion();
        }
        if (stopped) NotifyStateChanged();
        return stopped;
    }

    public bool StartStroke(StrokePreset stroke)
    {
        if (RuleEngineActive || !CanRun) return false;
        lock (_stateLock)
        {
            if (!CanRun) return false;
            StopAllLocked();
            ActiveMode = MotionMode.Stroke;
            CurrentStroke = stroke;
            _strokeTime = 0;
            _strokeLastTick = Stopwatch.GetTimestamp();
            StrokeRunning = true;
            ResetComfortEnvelope();
            Volatile.Write(ref _blendInPending, 1);
            long generation = ++_generation;
            _strokeTimer = MakeTimer(16, generation, StrokeTick);
        }
        NotifyStateChanged();
        return true;
    }

    public void StopStroke()
    {
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                _generation++;
                StrokeRunning = false;
                DisposeTimer(ref _strokeTimer);
            }
            _transport.StopMotion();
        }
        NotifyStateChanged();
    }

    public bool StartCustom()
    {
        if (RuleEngineActive || !CanRun) return false;
        lock (_stateLock)
        {
            if (!CanRun) return false;
            StopAllLocked();
            ActiveMode = MotionMode.Custom;
            _customTime = 0;
            _customLastTick = Stopwatch.GetTimestamp();
            CustomRunning = true;
            MarkAllWavesDirty();
            ResetComfortEnvelope();
            Volatile.Write(ref _blendInPending, 1);
            long generation = ++_generation;
            _customTimer = MakeTimer(20, generation, CustomTick);
        }
        NotifyStateChanged();
        return true;
    }

    public void StopCustom()
    {
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                _generation++;
                CustomRunning = false;
                DisposeTimer(ref _customTimer);
            }
            _transport.StopMotion();
        }
        NotifyStateChanged();
    }

    public void StopAll()
    {
        lock (_dispatchLock)
        {
            lock (_stateLock) StopAllLocked();
            _transport.StopMotion();
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// 启动精确的「动 N 秒 → 停 N 秒 → 歇 N 秒」挑逗循环（对齐 c.py start_teasing）。
    /// moveSec 夹到 1-10，stopSec/pauseSec 夹到 1-15；再次调用可动态调整参数。
    /// 循环运行于 Engine 层、受 MotionSafetyLimiter / ComfortProfile 约束；stop / 急停 / 切其它模式即清除。
    /// </summary>
    public bool StartTease(double moveSec, double stopSec, double pauseSec)
    {
        if (RuleEngineActive || !CanRun) return false;
        lock (_stateLock)
        {
            if (!CanRun || RuleEngineActive) return false;
            StopAllLocked();
            TeasingMode = true;
            ActiveMode = MotionMode.Auto;
            _teaseMoveSec = Math.Clamp(double.IsFinite(moveSec) ? moveSec : 3, 1, 10);
            _teaseStopSec = Math.Clamp(double.IsFinite(stopSec) ? stopSec : 5, 1, 15);
            _teasePauseSec = Math.Clamp(double.IsFinite(pauseSec) ? pauseSec : 5, 1, 15);
            _teaseCycleSec = _teaseMoveSec + _teaseStopSec + _teasePauseSec;
            _teaseStartTs = Stopwatch.GetTimestamp();
            _teaseLastTick = Stopwatch.GetTimestamp();
            ResetComfortEnvelope();
            Volatile.Write(ref _blendInPending, 1);
            long generation = ++_generation;
            _teaseTimer = MakeTimer(20, generation, TeaseTick);
        }
        NotifyStateChanged();
        return true;
    }

    public void StopTease()
    {
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                _generation++;
                TeasingMode = false;
                DisposeTimer(ref _teaseTimer);
            }
            _transport.StopMotion();
        }
        NotifyStateChanged();
    }

    /// <summary>挑逗循环单拍：处于「动」窗口时按挑逗参数振荡；「停/歇」窗口保持不动（不倒发，心跳由 RunTick 维持）。</summary>
    private void TeaseTick(long generation)
    {
        double deltaSeconds = NextDeltaSeconds(ref _teaseLastTick, 0.02);
        double elapsed = Stopwatch.GetElapsedTime(_teaseStartTs).TotalSeconds;
        if (_teaseCycleSec <= 0) return;
        if (elapsed % _teaseCycleSec >= _teaseMoveSec) return;   // 停/歇阶段：保持位置不动

        double scale = Math.Min(EffectiveIntensity, ActiveComfortProfile.MaxIntensity);
        double phase = elapsed * Speed;
        var values = new double[6];
        for (int i = 0; i < values.Length; i++)
        {
            double osc = Math.Sin(phase * TeaseOmega[i] + TeasePhase[i]);
            values[i] = Math.Clamp(50 + osc * TeaseAmp[i] * scale, 0, 100);
        }
        TryDispatchAxes(
            ApplySoftStart(values, ActiveComfortProfile),
            TakeBlendInSeconds(values, deltaSeconds),
            generation);
    }

    private void StopAllLocked()
    {
        _generation++;
        Volatile.Write(ref _blendInPending, 0);     // 没有模式在跑，也就没有「进入模式的第一帧」
        InterruptEase();             // 停掉一切时也打断没跑完的缓降 / 混合（急停走的正是这条路）
        AutoRunning = false;
        StrokeRunning = false;
        CustomRunning = false;
        TelemetryRunning = false;
        TelemetrySource = "";
        DisposeTimer(ref _autoTimer);
        DisposeTimer(ref _strokeTimer);
        DisposeTimer(ref _customTimer);
        DisposeTimer(ref _telemetryTimer);
        _burstCts?.Cancel();
        _burstRunning = false;
        TeasingMode = false;
        DisposeTimer(ref _teaseTimer);
        lock (_telemetryLock)
        {
            _telemetrySessionId = "";
            _telemetrySequence = 0;
            _telemetrySourceIntensity = 1.0;
            Volatile.Write(ref _telemetryReceivedAt, 0);
        }
    }

    public void TogglePlayback()
    {
        if (!CanRun || RuleEngineActive) return;
        switch (ActiveMode)
        {
            case MotionMode.Auto:
                if (AutoRunning) StopAuto(); else StartAuto();
                break;
            case MotionMode.Stroke:
                if (StrokeRunning) StopStroke();
                else if (CurrentStroke != null) StartStroke(CurrentStroke);
                break;
            case MotionMode.Custom:
                if (CustomRunning) StopCustom(); else StartCustom();
                break;
            case MotionMode.Telemetry:
                if (TelemetryRunning) StopTelemetrySync();
                break;
        }
    }

    public async Task ClimaxBurstAsync()
    {
        CancellationToken token;
        CancellationTokenSource owner;
        long generation;
        lock (_stateLock)
        {
            if (!CanRun || RuleEngineActive || _burstRunning) return;
            StopAllLocked();
            _burstRunning = true;
            owner = _burstCts = new CancellationTokenSource();
            token = owner.Token;
            generation = ++_generation;
        }
        NotifyStateChanged();

        try
        {
            for (int i = 0; i < 30; i++)
            {
                token.ThrowIfCancellationRequested();
                double amp = 0.3 + i * 0.023;
                double frequency = 1.5 + i * 0.05;
                double pulse = 50 + 45 * amp * Math.Sin(i * frequency);
                TryDispatchAxes([pulse, 50, 50, 50, 50, 50], 0.06, generation);
                await Task.Delay(60, token).ConfigureAwait(false);
            }
            for (int i = 0; i < 5; i++)
            {
                token.ThrowIfCancellationRequested();
                TryDispatchAxes([95, 50, 50, 60, 50, 50], 0.08, generation);
                await Task.Delay(80, token).ConfigureAwait(false);
                TryDispatchAxes([5, 50, 50, 40, 50, 50], 0.08, generation);
                await Task.Delay(80, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_burstCts, owner))
                {
                    _burstRunning = false;
                    _burstCts = null;
                }
            }
            owner.Dispose();
            NotifyStateChanged();
        }
    }

    public void MarkWaveDirty(string axisId)
    {
        int index = Array.IndexOf(Axes, axisId);
        if (index >= 0) RebuildSorted(index);
    }

    public void MarkAllWavesDirty()
    {
        for (int i = 0; i < Axes.Length; i++) RebuildSorted(i);
    }

    /// <summary>低于这个音量就算「安静」（与音频响应的默认门限同量级）。</summary>
    private const double SilenceGateLevel = 0.06;

    /// <summary>氛围叠加层：低于这个音量不叠（安静就该静止）。</summary>
    private const double AmbientSilenceLevel = 0.05;

    /// <summary>氛围叠加层一个完整来回的时长（秒）：慢，才像"呼吸"而不是"抖动"。</summary>
    private const double AmbientPeriodSeconds = 2.6;

    /// <summary>安静持续多久才停住（毫秒）：太短会被句子停顿切断，太长又显得迟钝。</summary>
    private const double SilenceHoldMs = 700;

    /// <summary>音频特征是否还在持续更新（停止采集后 0.5 秒就认为没有了 → 不做静音停）。</summary>
    private bool AudioCaptureAlive()
    {
        long at = Volatile.Read(ref _audioFeaturesAt);
        return at > 0 && Stopwatch.GetElapsedTime(at).TotalSeconds < 0.5;
    }

    private void RebuildSorted(int index)
    {
        lock (_waveLock)
            _customWaveSnapshots[index] = WaveScriptCodec.NormalizePoints(EditWaves[Axes[index]]);
    }

    private void AutoTick(long generation)
    {
        double deltaSeconds = NextDeltaSeconds(ref _autoLastTick, 0.02);
        ComfortProfile profile = ActiveComfortProfile;

        // 「安静就停」：只作用于游戏伴随 / 自动跟随开的动作，而且必须真的在采集音频
        // （用户自己按的「开始自动」不受影响，他按了就该一直动）。
        // 目的：平时静止、有事才动——一直轻微动是背景噪音，静止本身才更沉浸。
        if (_autoFromCompanion && _cfg.GateCompanionOnSilence && AudioCaptureAlive())
        {
            if (Volatile.Read(ref _audioEnergy) < SilenceGateLevel)
            {
                _silenceMs += deltaSeconds * 1000.0;
                if (_silenceMs >= SilenceHoldMs)
                {
                    if (!_silenceHold)
                    {
                        // 缓慢回到中位再停发，不突然跳、也不停在半路
                        _silenceHold = true;
                        TryDispatchAxes([50, 50, 50, 50, 50, 50], 0.6, generation);
                    }
                    return;
                }
            }
            else
            {
                _silenceMs = 0;
                _silenceHold = false;
            }
        }

        _autoSequencer.SetSelection(AutoPattern);
        AutoBehaviorFrame frame = _autoSequencer.Step(
            deltaSeconds,
            Speed,
            _axisAmp,
            EffectiveIntensity,
            _arousal,
            profile,
            Volatile.Read(ref _audioEnergy),
            Volatile.Read(ref _audioBass),
            Volatile.Read(ref _audioBeat));
        CurrentAutoPattern = frame.Pattern;
        CurrentAutoPatternLabel = frame.PatternLabel;
        CurrentAutoBpm = frame.Bpm;
        // P2 多轴语汇：只在「自动动作」这条路上叠加，而且只叠次要轴（L0 仍是 sequencer 的原值）。
        // 遥测同步（TelemetryTick）与 funscript 播放是外来精确动作，不叠加——它们也不走这里。
        double[] raw = frame.Axes;
        double[] pose = MultiAxisMotion ? OverlayMotionVocabulary(raw, deltaSeconds, profile) : raw;
        TryDispatchAxes(ApplySoftStart(pose, profile), TakeBlendInSeconds(raw, deltaSeconds), generation);
        if (AutoRunning) _arousal = Math.Min(100, _arousal + 0.02);
    }

    private void TelemetryTick(long generation)
    {
        double deltaSeconds = NextDeltaSeconds(ref _telemetryLastTick, 0.02);
        long receivedAt = Volatile.Read(ref _telemetryReceivedAt);
        double age = receivedAt <= 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(receivedAt).TotalSeconds;
        if (age > 0.30)
        {
            StopTelemetrySession(ActiveTelemetrySessionId);
            return;
        }

        double[] target;
        double sourceIntensity;
        lock (_telemetryLock)
        {
            target = SampleTelemetryTargetLocked(Stopwatch.GetTimestamp());
            sourceIntensity = _telemetrySourceIntensity;
        }

        double scale = Math.Min(EffectiveIntensity * sourceIntensity, ActiveComfortProfile.MaxIntensity);
        for (int i = 0; i < target.Length; i++)
            target[i] = Math.Clamp(50 + (target[i] - 50) * scale, 0, 100);
        TryDispatchAxes(ApplySoftStart(target, ActiveComfortProfile), deltaSeconds, generation);
    }

    private double[] SampleTelemetryTargetLocked(long now)
    {
        if (_telemetryTargetStartedAt <= 0) return _telemetryTarget.ToArray();
        double progress = Math.Clamp(
            Stopwatch.GetElapsedTime(_telemetryTargetStartedAt, now).TotalMilliseconds / _telemetryTransitionMs,
            0,
            1);
        var result = new double[6];
        for (int i = 0; i < result.Length; i++)
            result[i] = _telemetryStart[i] + (_telemetryTarget[i] - _telemetryStart[i]) * progress;
        return result;
    }

    private void StrokeTick(long generation)
    {
        StrokePreset? stroke = CurrentStroke;
        if (stroke == null) return;
        double deltaSeconds = NextDeltaSeconds(ref _strokeLastTick, 0.016);
        _strokeTime += deltaSeconds * Speed;
        double angle = _strokeTime * 2 * Math.PI;
        long cycleIndex = (long)Math.Floor(_strokeTime);   // 圈序号：噪声在同一圈内恒定
        double effectiveIntensity = Math.Min(EffectiveIntensity, ActiveComfortProfile.MaxIntensity);
        var values = new double[6];
        var axes = stroke.AllAxes;
        for (int i = 0; i < values.Length; i++)
        {
            double[] axis = axes[i];
            values[i] = Math.Clamp(
                SampleTempestAxis(
                    axis[0],
                    axis[1],
                    axis[2],
                    axis[3],
                    stroke.MotionOf(i),
                    angle,
                    effectiveIntensity,
                    axis.Length > 4 ? axis[4] : 0,
                    axis.Length > 5 ? axis[5] : 0,
                    i,
                    cycleIndex) * 100,
                0,
                100);
        }
        TryDispatchAxes(
            ApplySoftStart(values, ActiveComfortProfile),
            TakeBlendInSeconds(values, deltaSeconds),
            generation);
    }

    // ══════════════════════════════════════════════════════════════
    //  Tempest 采样（动作页播放 / 动作编辑器波形预览共用同一公式）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 单轴 Tempest 采样，返回 0..1。θ = angle + ½π·phase。
    /// 正弦（ayva tempestMotion）  ：mid − amp·cos(θ + ecc·sin(θ))，amp = (to−from)/2·intensity
    /// 抛物（ayva parabolicMotion）：to′ − (to′−from′)·x²，x = mod(θ,2π)/π − 1 + (ecc/π)·sin(θ)
    /// 线性（ayva linearMotion）   ：to′ − (to′−from′)·|x|，x 同上
    /// noiseFrom / noiseTo（0..1）按 ayva 语义收缩端点：偏移量 = noise·半程·伪随机(0..1)，
    /// 方向恒指向行程内部（只收窄、不越界）；伪随机由 axisIndex + cycleIndex 决定，
    /// 因此同一轴同一圈内恒定、跨圈才变化，不会每帧抖动。
    /// </summary>
    public static double SampleTempestAxis(
        double from,
        double to,
        double phase,
        double eccentricity,
        StrokeMotion motion,
        double angle,
        double intensity,
        double noiseFrom = 0,
        double noiseTo = 0,
        int axisIndex = 0,
        long cycleIndex = 0)
    {
        if (noiseFrom > 0) from += TempestNoise(axisIndex, cycleIndex, 0) * noiseFrom * (to - from) * 0.5;
        if (noiseTo > 0) to += TempestNoise(axisIndex, cycleIndex, 1) * noiseTo * (from - to) * 0.5;

        double middle = (from + to) * 0.5;
        double half = (to - from) * 0.5 * intensity;    // 负值 = from > to（反向行程）
        double theta = angle + 0.5 * Math.PI * phase;
        double low = middle - half;
        double high = middle + half;

        return motion switch
        {
            StrokeMotion.Parabolic => high - (high - low) * TempestX(theta, eccentricity) * TempestX(theta, eccentricity),
            StrokeMotion.Linear    => high - (high - low) * Math.Abs(TempestX(theta, eccentricity)),
            // 与 ayva 权威实现一致：θ 含相位偏移，偏心项用同一个 θ
            _ => middle - half * Math.Cos(theta + eccentricity * Math.Sin(theta)),
        };
    }

    /// <summary>抛物/线性共用的归一化角度项（ayva: mod(θ,2π)/π − 1 + (ecc/π)·sin θ）。</summary>
    private static double TempestX(double theta, double eccentricity) =>
        PositiveMod(theta, 2 * Math.PI) / Math.PI - 1 + eccentricity / Math.PI * Math.Sin(theta);

    private static double PositiveMod(double value, double modulus)
    {
        double remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    /// <summary>
    /// 确定性伪随机 0..1（splitmix64 混合）。固定种子 → 同一 (轴, 圈, 端点) 永远同一值，
    /// 于是同一轴同一圈内噪声恒定、跨圈才变化（ayva 用 Math.random()，这里刻意改成确定性）。
    /// </summary>
    private static double TempestNoise(int axisIndex, long cycleIndex, int end)
    {
        ulong hash = 0x9E3779B97F4A7C15UL;
        hash ^= (ulong)(uint)axisIndex * 0xBF58476D1CE4E5B9UL;
        hash ^= (ulong)cycleIndex * 0x94D049BB133111EBUL;
        hash ^= (ulong)(uint)end * 0x2545F4914F6CDD1DUL;
        hash ^= hash >> 30; hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27; hash *= 0x94D049BB133111EBUL;
        hash ^= hash >> 31;
        return (hash >> 11) * (1.0 / 9007199254740992.0);
    }

    private void CustomTick(long generation)
    {
        double deltaSeconds = NextDeltaSeconds(ref _customLastTick, 0.02);
        _customTime += deltaSeconds * ScriptSpeed;
        double effectiveIntensity = Math.Min(EffectiveIntensity, ActiveComfortProfile.MaxIntensity);
        var values = new double[6];
        for (int i = 0; i < values.Length; i++)
        {
            if (!WfAxisEnabled[i])
            {
                values[i] = 50;
                continue;
            }

            List<(double X, double Y)> points;
            lock (_waveLock) points = _customWaveSnapshots[i];
            if (points.Count < 2)
            {
                values[i] = 50;
                continue;
            }

            double cycleX = (_customTime % _customCycleLength) / _customCycleLength * WaveScriptCodec.CanvasWidth;
            double sampledY = UseSmoothCustomInterpolation
                ? MotionInterpolation.SamplePchip(points, cycleX)
                : MotionInterpolation.SampleLinear(points, cycleX);
            double value = 100 - sampledY / WaveScriptCodec.CanvasHeight * 100;
            values[i] = Math.Clamp(50 + (value - 50) * effectiveIntensity, 0, 100);
        }
        TryDispatchAxes(
            ApplySoftStart(values, ActiveComfortProfile),
            TakeBlendInSeconds(values, deltaSeconds),
            generation);
    }

    /// <summary>
    /// 多轴语汇总闸：<see cref="MultiAxisMotion"/> 关闭时，除 L0 以外的轴一律钉在中位 50。
    /// 闸门放在 <see cref="TryDispatchAxes"/> 这一个出口上 —— 自动 / 挑逗 / 往复 / 自定义 / 遥测、
    /// 直接下发（音频响应 / 游戏桥 / funscript / 编辑器预览）、BlendTo / EaseDown 全都从这里出去，
    /// 所以「关掉多轴 = 只有 L0 动」是结构性保证，不靠各模式自觉（以前是各模式各自写轴）。
    /// 多轴开启时原样透传，不改变任何现有行为。
    /// 不经此处的只有三条显式路径：布防 / 全归中本来就发全 50；单轴校准是用户点名「只动这一根轴」，
    /// 而且不写其它轴。
    /// </summary>
    /// <summary>
    /// 氛围叠加层（输入融合的"叠加"部分）：在**精确动作**（脚本播放 / 遥测 / 游戏桥）之上，
    /// 叠一层很轻、只走次要轴的微动，让"游戏在动"和"现场有气氛"同时成立。
    ///
    /// 为什么只叠次要轴、不动 L0：
    /// L0 是游戏/脚本的精确意图（后坐力、行程、节奏），在上面加东西就是篡改；
    /// 而次要轴的"立体感"本来就是氛围，叠上去不抢意图。
    ///
    /// 三条自我约束：
    /// ① 默认关（<see cref="AppSettings.AmbientOverlay"/>），幅度上限很小（5–30，默认 12）；
    /// ② 只认"新鲜的"音频特征（采集停了就不叠），安静时也不叠；
    /// ③ 叠完照样走多轴门控与限速器，所以任何限位/速度/加速度上限都不会被突破。
    /// </summary>
    private IReadOnlyList<double> ApplyAmbientOverlay(IReadOnlyList<double> target, double deltaSeconds)
    {
        if (!_cfg.AmbientOverlay || target.Count < 6) return target;

        // 采集停了或很久没更新 → 不叠（避免"声音早停了设备还在呼吸"）。
        long at = Volatile.Read(ref _audioFeaturesAt);
        if (at <= 0 || Stopwatch.GetElapsedTime(at) > TimeSpan.FromMilliseconds(400)) return target;
        double energy = Math.Clamp(Volatile.Read(ref _audioEnergy), 0, 1);
        if (energy <= AmbientSilenceLevel) return target;

        double dt = Math.Clamp(double.IsFinite(deltaSeconds) ? deltaSeconds : 0.02, 0.001, 0.1);
        _ambientPhase = (_ambientPhase + dt / AmbientPeriodSeconds) % 1.0;
        double amount = Math.Clamp(_cfg.AmbientOverlayAmount, 5, 30) * energy;
        double wave = Math.Sin(_ambientPhase * Math.Tau);

        for (int i = 0; i < 6; i++)
        {
            // L0 归精确动作，氛围只走次要轴，而且各轴相位错开（不然五根轴一起上下摆，像抽风）。
            double sign = i == 0 ? 0 : (i % 2 == 0 ? 1 : -1);
            double phaseOffset = i == 0 ? 0 : (i - 1) * 0.6;
            double value = i == 0
                ? target[0]
                : target[i] + sign * amount * Math.Sin(_ambientPhase * Math.Tau + phaseOffset);
            _ambientScratch[i] = Math.Clamp(value, 0, 100);
        }
        return _ambientScratch;
    }

    private IReadOnlyList<double> ApplyMultiAxisGate(IReadOnlyList<double> target)
    {
        if (MultiAxisMotion || target.Count < 6) return target;
        for (int i = 0; i < 6; i++)
            _gatedAxes[i] = i == 0 ? target[0] : MotionVocabulary.Center;
        return _gatedAxes;
    }

    /// <summary>
    /// 把语汇的<b>次要轴分量</b>叠加到自动动作的帧上（L0 保持 sequencer 的原值）。
    /// 事件类型由当前音频能量判定：骤升 → Impact；持续偏高 → Swell；其余 → 弱化版 Swell。
    /// 叠加量 = (语汇次要轴 − 中位) × 舒适档 Variation，结果夹在 0–100；
    /// 之后照常走 <see cref="ApplySoftStart"/> 与限速器，所以依旧受舒适配置约束
    /// （软启动 / MaxAxisSpeedPerSecond / 加速度 / 抖动）。
    /// 只服务「自动动作」这条路：遥测同步与 funscript 播放是外来精确动作，不叠加（也不经过这里）。
    /// </summary>
    /// <summary>
    /// 外部注入一次动作事件（「AI 当导演」用）：下一次渲染按它来，消费一次即失效，3 秒没被消费就作废。
    /// 只决定<b>事件形态与强度</b>，具体动作仍由本地语汇 / 软启动 / 限速器生成 ——
    /// 所以照样受急停、限位与舒适档约束，不会绕开任何安全闸。
    /// 返回 false ＝ 当前没有在自动运动（语汇只挂在自动动作那条路上），调用方应提示"先让它动起来"。
    /// </summary>
    public bool PulseVocab(MotionEventKind kind, double strength)
    {
        if (!AutoRunning) return false;
        _injectedStrength = Math.Clamp(strength, 0.05, 1.5);
        Volatile.Write(ref _injectedAtTicks, Stopwatch.GetTimestamp());
        Volatile.Write(ref _injectedKindRaw, (int)kind);
        return true;
    }

    /// <summary>取一次外部注入（有就消费掉）。timer 线程调用，和 UI 线程的注入用原子操作对话。</summary>
    private bool TakeInjectedVocab(out MotionEventKind kind, out double strength)
    {
        kind = MotionEventKind.None;
        strength = 0;
        int raw = Interlocked.Exchange(ref _injectedKindRaw, -1);
        if (raw < 0) return false;
        if (Stopwatch.GetElapsedTime(Volatile.Read(ref _injectedAtTicks)).TotalSeconds > InjectedVocabMaxAgeSeconds)
            return false;
        kind = (MotionEventKind)raw;
        strength = Math.Clamp(_injectedStrength, 0.05, 1.5);
        return true;
    }

    private double[] OverlayMotionVocabulary(double[] axes, double deltaSeconds, ComfortProfile profile)
    {
        double energy = Math.Clamp(Volatile.Read(ref _audioEnergy), 0, 1);
        double dt = Math.Clamp(double.IsFinite(deltaSeconds) ? deltaSeconds : 0.02, 0.001, 0.1);

        // ① 事件判定：能量骤升 → 冲击；持续偏高 → 渐强；其余 → 弱化版渐强。
        double rise = energy - _vocabLastEnergy;
        _vocabLastEnergy = energy;
        _vocabHighSeconds = energy >= VocabHighEnergy ? _vocabHighSeconds + dt : 0;

        MotionEventKind kind;
        double strength;
        // ⓪ 外部注入优先（AI 当导演）：它已经定了"现在要什么形态"，就不该再被音频能量盖掉。
        if (TakeInjectedVocab(out MotionEventKind injectedKind, out double injectedStrength))
        {
            kind = injectedKind;
            strength = injectedStrength;
        }
        else if (rise >= VocabImpactRise && energy >= VocabImpactEnergy)
        {
            kind = MotionEventKind.Impact;
            strength = energy;
        }
        else if (_vocabHighSeconds >= VocabSwellHoldSeconds)
        {
            kind = MotionEventKind.Swell;
            strength = energy;
        }
        else
        {
            kind = MotionEventKind.Swell;
            strength = energy * VocabSoftSwellScale;
        }

        // ② 事件相位：换类型、或者上一拍已经走完，就重新起一拍；同类型事件强度做指数平滑。
        if (kind != _vocabKind || _vocabPhase >= 1)
        {
            _vocabKind = kind;
            _vocabPhase = 0;
            _vocabStrength = strength;
            _vocabDuration = Math.Max(0.05, MotionVocabulary.DurationSeconds(kind, strength));
        }
        else
        {
            _vocabStrength += (strength - _vocabStrength) * 0.25;
        }

        double phase = Math.Clamp(_vocabPhase, 0, 1);
        // 半幅跟着有效强度走，但不超过舒适档上限；再按 Variation 收到「这个档位允许多少变化」。
        double amplitude = Math.Clamp(48 * Math.Min(EffectiveIntensity, profile.MaxIntensity), 5, 60);
        double[] pose = MotionVocabulary.Render(_vocabKind, _vocabStrength, phase, amplitude, multiAxis: true);
        double scale = profile.Variation > 0.001 ? Math.Clamp(profile.Variation, 0.05, 0.6) : 0.3;

        var result = new double[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            double value = double.IsFinite(axes[i]) ? axes[i] : MotionVocabulary.Center;
            // 主轴 L0 归 sequencer：语汇只贡献次要轴，动作风格不会被语汇改掉。
            double overlay = i == 0 ? 0 : (pose[i] - MotionVocabulary.Center) * scale;
            result[i] = Math.Clamp(value + overlay, 0, 100);
        }

        _vocabPhase = Math.Min(1, phase + dt / _vocabDuration);
        return result;
    }

    private double[] ApplySoftStart(double[] target, ComfortProfile profile)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - Interlocked.Read(ref _modeStartedAt)) / (double)Stopwatch.Frequency;
        double ramp = profile.SoftStartSeconds <= 0
            ? 1.0
            : Smooth01(Math.Clamp(elapsed / profile.SoftStartSeconds, 0, 1));
        var output = new double[6];

        lock (_outputLock)
        {
            for (int i = 0; i < output.Length; i++)
                output[i] = Math.Clamp(
                    _envelopeStartOutput[i] + (Math.Clamp(target[i], 0, 100) - _envelopeStartOutput[i]) * ramp,
                    0,
                    100);
        }
        return output;
    }

    /// <summary>
    /// 唯一的下发出口。<paramref name="deltaSeconds"/> 是给限速器的 dt（决定这一步最多能走多远），
    /// <paramref name="interpolationSeconds"/> 是给设备的插值时间（不给就与 dt 相同）。
    /// 缓降这类场景需要两者分开：dt 按帧走、插值时间略长于步长，设备才连续跟随。
    /// </summary>
    private bool TryDispatchAxes(
        IReadOnlyList<double> target,
        double deltaSeconds,
        long generation,
        bool changedOnly = true,
        double? interpolationSeconds = null,
        bool ambientOverlay = false)
    {
        if (target.Count < 6) return false;
        lock (_dispatchLock)
        {
            if (generation != Interlocked.Read(ref _generation) || !CanRun) return false;
            double[] output;
            lock (_outputLock)
            {
                // 氛围叠加必须在「多轴门控」之前：用户关掉「多轴动作」时，次要轴要照样被钉回中位，
                // 不能被氛围层偷偷又抬起来。
                IReadOnlyList<double> staged = ambientOverlay ? ApplyAmbientOverlay(target, deltaSeconds) : target;
                output = _safetyLimiter.Step(ApplyMultiAxisGate(staged), deltaSeconds, ActiveComfortProfile);
                Array.Copy(output, _lastOutput, 6);
                long now = Stopwatch.GetTimestamp();
                bool heartbeatDue = _lastOutputAt <= 0
                    || Stopwatch.GetElapsedTime(_lastOutputAt, now) >= TimeSpan.FromMilliseconds(250);
                _lastOutputAt = now;
                _lastSchedulerTickAt = now;
                changedOnly &= !heartbeatDue;
            }
            _transport.SendAxes(
                output,
                _cfg.AxisMin,
                _cfg.AxisMax,
                ToInterpolationMs(interpolationSeconds ?? deltaSeconds),
                changedOnly);
            return true;
        }
    }

    public void SetAudioFeatures(double energy, double bass, double beatPulse)
    {
        Volatile.Write(ref _audioFeaturesAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref _audioEnergy, Math.Clamp(double.IsFinite(energy) ? energy : 0, 0, 1));
        Volatile.Write(ref _audioBass, Math.Clamp(double.IsFinite(bass) ? bass : 0, 0, 1));
        Volatile.Write(ref _audioBeat, Math.Clamp(double.IsFinite(beatPulse) ? beatPulse : 0, 0, 1));
    }

    private static int ToInterpolationMs(double deltaSeconds) =>
        Math.Clamp((int)Math.Round(Math.Clamp(deltaSeconds, 0.001, 9.999) * 1000), 1, 9999);

    private void ResetComfortEnvelope()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_outputLock)
        {
            Array.Copy(_lastOutput, _envelopeStartOutput, _lastOutput.Length);
            _modeStartedAt = now;
            _lastOutputAt = now;
        }
    }

    private void ResetOutputState(double[] values)
    {
        if (values.Length < 6) return;
        long now = Stopwatch.GetTimestamp();
        lock (_outputLock)
        {
            for (int i = 0; i < 6; i++)
                _lastOutput[i] = _envelopeStartOutput[i] = Math.Clamp(values[i], 0, 100);
            _modeStartedAt = now;
            _lastOutputAt = now;
            _lastSchedulerTickAt = now;
            _safetyLimiter.Reset(_lastOutput);
        }
    }

    private double SecondsSinceLastOutput(double fallback)
    {
        lock (_outputLock)
        {
            if (_lastOutputAt <= 0) return fallback;
            return Math.Clamp(Stopwatch.GetElapsedTime(_lastOutputAt).TotalSeconds, 0.005, 0.1);
        }
    }

    private static double NextDeltaSeconds(ref long lastTimestamp, double fallback)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Exchange(ref lastTimestamp, now);
        if (previous <= 0) return fallback;
        return Math.Clamp((now - previous) / (double)Stopwatch.Frequency, 0.001, 0.1);
    }

    private static double Smooth01(double value)
    {
        double x = Math.Clamp(value, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>
    /// 把一个模式的 tick 挂到输出节拍上（原来这里是 <c>System.Timers.Timer</c>，见
    /// <see cref="HighResolutionTickThread"/> 的说明：Windows 定时器粒度 15.6ms 会把 20ms 的拍子
    /// 量化成 15.6/31.2ms 交替）。
    /// generation 语义完全没变：每次醒来仍然先校验 generation 与 CanRun（见 <see cref="RunTick"/>）。
    /// </summary>
    private HighResolutionTickThread.Slot MakeTimer(int intervalMs, long generation, Action<long> tick) =>
        _outputClock.Register(intervalMs, () => RunTick(generation, tick), tick.Method.Name);

    private void RunTick(long generation, Action<long> tick)
    {
        if (generation != Interlocked.Read(ref _generation) || !CanRun) return;
        if (Interlocked.Exchange(ref _tickBusy, 1) != 0) return;
        try
        {
            if (generation == Interlocked.Read(ref _generation) && CanRun)
            {
                Volatile.Write(ref _lastSchedulerTickAt, Stopwatch.GetTimestamp());
                tick(generation);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("运动调度异常，已触发急停", ex);
            EmergencyStop();
        }
        finally
        {
            Volatile.Write(ref _tickBusy, 0);
        }
    }

    /// <summary>摘掉一个模式的节拍任务（原来 Dispose 的是 System.Timers.Timer）。</summary>
    private static void DisposeTimer(ref HighResolutionTickThread.Slot? slot)
    {
        slot?.Dispose();
        slot = null;
    }

    /// <summary>上一拍看门狗是否看到"正在跑"（用来给刚起跑的那一拍一个宽限）。</summary>
    private bool _watchdogSawRunning;

    private void WatchdogTick()
    {
        if (!IsRunning || !CanRun)
        {
            _watchdogSawRunning = false;
            return;
        }

        // 刚起跑：这一拍只把时间戳重置，不判超时。
        // 否则任何"非调度器路径先开始驱动"的场景（游戏桥断开后规则引擎接管、遥测接管、Home/TryArm…）
        // 都会因为"第一个 tick 还没到"被判成 750ms 未刷新 → 锁存急停。
        // 实测证据：30 次看门狗急停里 29 次，其前 15 秒内都有"游戏桥客户端会话异常"。
        if (!_watchdogSawRunning)
        {
            _watchdogSawRunning = true;
            Volatile.Write(ref _lastSchedulerTickAt, Stopwatch.GetTimestamp());
            return;
        }

        // 直接下发源（脚本播放等）不走调度器、不产生 tick，原来完全不在看门狗视野里：
        // 它们的线程真卡死时，机器会停在最后一个位置、界面却什么都不说（用户看到的就是
        // 「播着播着不动了，也没有任何提示」）。这里补一条**窄**判据，避免误伤：
        //   只在「脚本确实在驱动设备」＋「连续 2 秒一个字节都没发出去」时才判卡死。
        // 不把游戏桥算进来的原因：桥空闲时本来就不发帧（游戏不发指令＝正常），会误触发。
        if (ScriptPlaying && _directInputOwner is { Length: > 0 })
        {
            long lastOut;
            lock (_outputLock) lastOut = _lastOutputAt;
            if (lastOut > 0 && Stopwatch.GetElapsedTime(lastOut) > TimeSpan.FromSeconds(2))
            {
                AppLogger.Error("脚本正在驱动设备，但已 2 秒没有发出任何帧 —— 判定卡死并锁存急停");
                EmergencyStop();
            }
            return;
        }

        long lastTick = Volatile.Read(ref _lastSchedulerTickAt);
        if (lastTick <= 0 || Stopwatch.GetElapsedTime(lastTick) < TimeSpan.FromMilliseconds(750)) return;
        AppLogger.Error("运动调度超过 750ms 未刷新，已触发锁存急停");
        EmergencyStop();
    }

    private void CancelHomingLocked()
    {
        _homingCts?.Cancel();
        _homingCts = null;
    }

    private void OnConnectionChanged(bool connected)
    {
        lock (_dispatchLock)
        {
            lock (_stateLock)
            {
                StopAllLocked();
                CancelHomingLocked();
                IsHoming = false;
                RuleEngineActive = false;
                // 急停锁存**绝不能**被连接事件清掉：用户按了急停之后，如果设备掉线又自动重连
                // （或者一次串口写失败触发了重连），设备会自己恢复可动、自动动作随之开跑 ——
                // 用户没碰任何按钮，机器却动了。锁存只能由用户显式「全部归中」/「解锁」清除。
                _transport.OutputEnabled = connected && !EmergencyStopped;
                if (EmergencyStopped)
                    AppLogger.Warn("设备已重新连接，但处于急停锁定：输出保持锁定，点「全部归中」才解锁");
            }
        }
        AppLogger.Info(connected ? "设备连接已建立，输出解锁可动" : "设备连接已断开，输出已锁定");
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        _transport.ConnectionChanged -= OnConnectionChanged;
        EmergencyStop();
        _watchdogTimer.Dispose();
        // 停掉节拍线程（EmergencyStop → StopAllLocked 已经把各模式的任务摘干净了）；
        // 线程收尾时会配对调用 timeEndPeriod，把进程定时器分辨率还回去。
        _outputClock.Dispose();
    }
}

/// <summary>
/// 输出节拍：专用后台线程 + 固定节拍。
///
/// <b>为什么不用 System.Timers.Timer / DispatcherTimer</b>：
/// ① Windows 自 2004 起进程级定时器粒度约 15.6ms，.NET 的 Timer 内部就是
///    <c>AutoResetEvent.WaitOne(超时)</c> —— 等待被量化到 15.6ms 的网格上，于是「20ms 一拍」
///    实际是 15.6/31.2ms 交替，抖动 ±8ms 级：决定到字节出串口因此典型 10–15ms、最差 35ms。
/// ② DispatcherTimer 挂在 UI 线程上，界面一忙（渲染 / 布局 / 列表刷新）就被顶掉，
///    funscript 逐帧播放尤其明显（20/秒会掉到 12–18/秒）。
///
/// <b>这里怎么保证不漂移</b>：
/// ① 每个任务有自己的<b>绝对</b>时刻表：<c>Anchor + 序号 × 周期</c>（Stopwatch 时间戳），
///    不是「上次醒来 + 周期」—— 所以既不累积浮点误差，也不受上一次回调耗时的影响；
/// ② 醒来只做「还差多少 → WaitOne（粗等）+ 最后 1.2ms 自旋」，
///    粗等的残差由自旋吃掉，抖动压到亚毫秒；
/// ③ 落后超过两拍（系统挂起 / 断点）时重新锚定，绝不一次性补发一串帧 ——
///    补发对设备就是「突然连动好几下」。
///
/// <b>急停不等节拍</b>：急停走 <see cref="MotionEngine.EmergencyStop"/>，
/// 它直接停任务、落 DSTOP、锁输出（并让 generation 失效使在跑的这一帧作废），
/// 完全不经过这里；本线程最多把当前这一帧发完（一帧串口写，微秒级）。
/// 线程用 Background 优先级 + <see cref="ThreadPriority.AboveNormal"/>：不与线程池抢，
/// 但比普通后台活儿更早被调度。
/// </summary>
internal sealed class HighResolutionTickThread : IDisposable
{
    /// <summary>粗等之后留给自己精调的时间（毫秒）：这段时间自旋，把抖动压到亚毫秒。</summary>
    private const double SpinWindowMs = 1.0;

    /// <summary>单次粗等的上限（毫秒）：等太久会听不到「新任务注册 / 要停」的信号。</summary>
    private const double MaxCoarseWaitMs = 20.0;

    /// <summary>落后超过这么多拍就重新锚定（不补发）。</summary>
    private const double ResyncPeriods = 2.0;

    private readonly object _sync = new();
    private readonly List<Slot> _slots = new();
    private readonly ManualResetEvent _wake = new(false);
    private readonly List<Slot> _due = new(4);
    private Thread? _thread;
    private volatile bool _running;
    private int _disposed;

    public HighResolutionTickThread(string name) => Name = name;

    public string Name { get; }

    /// <summary>注册一个周期任务（毫秒）。返回的句柄 Dispose 即摘掉；节拍线程按需启动。</summary>
    /// <param name="name">只用于抖动日志（见 <see cref="Slot"/> 里的统计）。</param>
    public Slot Register(int periodMs, Action tick, string name = "tick")
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(HighResolutionTickThread));

        var slot = new Slot(this, Math.Clamp(periodMs, 1, 1000), tick, name);
        lock (_sync)
        {
            slot.AnchorLocked(Stopwatch.GetTimestamp());
            _slots.Add(slot);
            if (!_running)
            {
                _running = true;
                _thread = new Thread(Loop)
                {
                    IsBackground = true,                       // 主程序退出时不被它拖住
                    Priority = ThreadPriority.AboveNormal,     // 比普通后台活儿更早被调度
                    Name = Name,
                };
                _thread.Start();
            }
        }
        _wake.Set();     // 叫醒可能正在空等的循环，让它按新的时刻表重算
        return slot;
    }

    private void Remove(Slot slot)
    {
        lock (_sync) _slots.Remove(slot);
        _wake.Set();
    }

    private void Loop()
    {
        // 进入时提高进程定时器分辨率（winmm），退出时配对还原 —— finally 保证异常路径也还原。
        TimerResolution.Enter();
        try
        {
            while (true)
            {
                long earliest = long.MaxValue;
                lock (_sync)
                {
                    if (!_running) return;
                    foreach (Slot slot in _slots)
                        if (slot.NextDue < earliest) earliest = slot.NextDue;
                }

                if (earliest == long.MaxValue)
                {
                    // 没有任务：睡着等注册/停止信号（50ms 兜底，避免信号竞态时永久睡死）
                    _wake.Reset();
                    _wake.WaitOne(50);
                    continue;
                }

                WaitUntil(earliest);

                long now = Stopwatch.GetTimestamp();
                _due.Clear();
                lock (_sync)
                {
                    if (!_running) return;
                    foreach (Slot slot in _slots)
                    {
                        if (slot.NextDue > now) continue;
                        long due = slot.NextDue;
                        slot.AdvanceLocked(now);
                        slot.RecordLateness(due, now);   // 抖动/间隔统计（纯内存，日志丢给线程池）
                        _due.Add(slot);
                    }
                }

                // 回调一律在锁外执行：任务里会去拿引擎的锁、写串口，绝不能在节拍锁里做。
                for (int i = 0; i < _due.Count; i++) _due[i].Invoke();
            }
        }
        finally
        {
            TimerResolution.Exit();
        }
    }

    /// <summary>
    /// 等到 <paramref name="due"/> 这个绝对时刻。粗等用 <see cref="WaitHandle.WaitOne(int)"/>，
    /// 最后 <see cref="SpinWindowMs"/> 毫秒自旋 —— 只靠 WaitOne 会被系统时钟粒度量化（±1ms 起），
    /// 只靠自旋又要烧掉整段等待的 CPU。返回表示「该重算了」（有新任务注册，或者要停）。
    ///
    /// 「够长才值得粗等」这个判断是有意的：`remaining` 落在自旋窗口附近时（不足粗等一格）
    /// 直接自旋，免得为了省 1ms 的 CPU 换来一整格（1ms）的迟到。
    /// 自旋窗口本身只有 1ms 量级，而且 WaitOne 早醒才需要自旋、晚醒直接返回，
    /// 平均每拍烧掉的 CPU 不到一个毫秒 —— 16ms 拍子下约 3%。
    /// </summary>
    private void WaitUntil(long due)
    {
        while (_running)
        {
            double remainingMs = (due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainingMs <= 0) return;
            if (remainingMs > SpinWindowMs + 1.0)
            {
                // 四舍五入而不是向下取整：粗等结束后剩下的自旋时间更接近一个格，抖动更小。
                int coarse = (int)Math.Min(Math.Round(remainingMs - SpinWindowMs), MaxCoarseWaitMs);
                if (coarse < 1) coarse = 1;
                if (_wake.WaitOne(coarse))
                {
                    _wake.Reset();
                    return;
                }
            }
            else
            {
                Thread.SpinWait(80);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Thread? thread;
        lock (_sync)
        {
            _running = false;
            thread = _thread;
            _thread = null;
            _slots.Clear();
        }
        _wake.Set();
        try { thread?.Join(1000); }
        catch (Exception ex) { AppLogger.Warn($"节拍线程收尾异常（已忽略）：{ex.GetType().Name}"); }
        // 刻意不 Dispose _wake：Join 超时的情况下它可能还在被线程使用；
        // 引擎是进程级单例，一个事件句柄随进程结束回收即可。
    }

    /// <summary>挂在节拍上的一个周期任务。</summary>
    internal sealed class Slot : IDisposable
    {
        private readonly HighResolutionTickThread _owner;
        private readonly Action _tick;
        private readonly long _periodTicks;
        private readonly JitterMeter _jitter;
        private long _anchor;
        private long _index;
        private volatile bool _active = true;

        internal Slot(HighResolutionTickThread owner, int periodMs, Action tick, string name)
        {
            _owner = owner;
            _tick = tick;
            _periodTicks = Math.Max(1, (long)Math.Round(periodMs * Stopwatch.Frequency / 1000.0));
            _jitter = new JitterMeter(name);
        }

        /// <summary>下一次该执行的绝对时刻（Stopwatch 时间戳）。只在节拍线程与 <see cref="_sync"/> 内读写。</summary>
        internal long NextDue { get; private set; }

        /// <summary>重新锚定：下一次 = 现在 + 一个周期。</summary>
        internal void AnchorLocked(long now)
        {
            _anchor = now;
            _index = 1;
            NextDue = now + _periodTicks;
        }

        /// <summary>
        /// 按「Anchor + 序号 × 周期」推进到下一拍的绝对时刻（不是「上一次 + 周期」，
        /// 所以回调耗时与浮点误差都不会累积成漂移）；落后超过 <see cref="ResyncPeriods"/> 拍就重新锚定。
        /// </summary>
        internal void AdvanceLocked(long now)
        {
            _index++;
            NextDue = _anchor + _index * _periodTicks;
            if (now - NextDue > (long)(ResyncPeriods * _periodTicks)) AnchorLocked(now);
        }

        internal void Invoke()
        {
            if (!_active) return;
            try
            {
                _tick();
            }
            catch (Exception ex)
            {
                // 单次任务异常不能打死节拍线程（后台线程未处理异常会直接结束进程）：
                // 引擎自己的 RunTick 已经 catch 并触发急停，这里只是最后一道兜底。
                AppLogger.Error("输出节拍任务异常（已忽略，节拍继续）", ex);
            }
        }

        /// <summary>记录这一拍的迟到量（= 实际时刻 − 应到时刻）与实际间隔，见 <see cref="JitterMeter"/>。</summary>
        internal void RecordLateness(long due, long now) => _jitter.Record(due, now);

        public void Dispose()
        {
            _active = false;
            _owner.Remove(this);
        }
    }

    /// <summary>
    /// 节拍抖动 / 漂移统计（每个任务一份，只有节拍线程写）。
    ///
    /// 记两件事：① 每一拍的<b>迟到量</b>（实际唤醒时刻 − 应到绝对时刻）；
    /// ② 相邻两拍的<b>实际间隔</b>（用来证明没有长期漂移：平均间隔应当等于设定周期）。
    /// 桶宽 100µs、64 桶（0–6.4ms），超出进溢出桶；每 <see cref="ReportTicks"/> 拍打一行日志。
    ///
    /// 这一行就是「抖动从 ±8ms 级降到 ±1ms 级」的硬证据（旧的 System.Timers.Timer 会看到
    /// 15.6/31.2ms 两个尖峰、P95 落在 6ms 以上）。日志本身丢给线程池写，绝不占用节拍线程的 IO。
    /// </summary>
    internal sealed class JitterMeter
    {
        private const int Buckets = 64;
        private const int BucketMicros = 100;
        private const int ReportTicks = 3000;

        private readonly string _name;
        private readonly int[] _histogram = new int[Buckets + 1];
        private int _count;
        private long _lateMaxMicros;
        private long _sumIntervalMicros;
        private long _intervals;
        private long _lastAt;

        public JitterMeter(string name) => _name = name;

        public void Record(long due, long now)
        {
            long lateMicros = Math.Max(0, (long)((now - due) * 1_000_000.0 / Stopwatch.Frequency));
            _histogram[(int)Math.Min(lateMicros / BucketMicros, Buckets)]++;
            if (lateMicros > _lateMaxMicros) _lateMaxMicros = lateMicros;
            if (_lastAt > 0)
            {
                _sumIntervalMicros += (long)((now - _lastAt) * 1_000_000.0 / Stopwatch.Frequency);
                _intervals++;
            }
            _lastAt = now;
            if (++_count < ReportTicks) return;

            string line = BuildReport();
            // 写文件丢给线程池：节拍线程上做磁盘 IO 会自己把自己拖迟到。
            ThreadPool.QueueUserWorkItem(_ => AppLogger.Info(line));
        }

        private string BuildReport()
        {
            int total = 0;
            for (int i = 0; i < _histogram.Length; i++) total += _histogram[i];
            double meanIntervalMs = _intervals > 0 ? _sumIntervalMicros / 1000.0 / _intervals : 0;
            string line = $"[节拍] {_name} 最近 {total} 拍：迟到 中位 {BucketToMs(PercentileIndex(total, 0.50)):0.0}ms" +
                          $" / P95 {BucketToMs(PercentileIndex(total, 0.95)):0.0}ms" +
                          $" / P99 {BucketToMs(PercentileIndex(total, 0.99)):0.0}ms" +
                          $" / 最大 {_lateMaxMicros / 1000.0:0.00}ms（>6.4ms {_histogram[Buckets]} 次）" +
                          $"；实际间隔均值 {meanIntervalMs:0.00}ms";
            Array.Clear(_histogram);
            _count = 0;
            _lateMaxMicros = 0;
            _sumIntervalMicros = 0;
            _intervals = 0;
            return line;
        }

        /// <summary>返回第 q 分位所在的桶号（0..64），桶号 × 100µs = 迟到量下界。</summary>
        private int PercentileIndex(int total, double q)
        {
            if (total <= 0) return 0;
            int target = Math.Max(1, (int)Math.Ceiling(total * q));
            int seen = 0;
            for (int i = 0; i < _histogram.Length; i++)
            {
                seen += _histogram[i];
                if (seen >= target) return i;
            }
            return Buckets;
        }

        private static double BucketToMs(int bucket) => bucket * BucketMicros / 1000.0;
    }

    /// <summary>
    /// 进程级定时器分辨率（winmm 的 timeBeginPeriod / timeEndPeriod）。
    /// 引用计数配对：多个节拍线程 / 多次进入只提升一次、也只还原一次，
    /// 不会出现「甲线程 timeEndPeriod 把乙线程还需要的高精度关掉」。
    /// </summary>
    private static class TimerResolution
    {
        private static readonly object Gate = new();
        private static int _users;

        public static void Enter()
        {
            lock (Gate)
            {
                if (_users++ > 0) return;
                try
                {
                    uint result = TimeBeginPeriod(1);
                    if (result != 0) AppLogger.Warn($"timeBeginPeriod(1) 返回 {result}：节拍仍可运行，抖动会稍大");
                }
                catch (Exception ex)
                {
                    // 理论上不会失败（winmm 是系统库）；失败也不许影响运动调度。
                    AppLogger.Warn($"提高定时器分辨率失败（节拍仍可运行，抖动会稍大）：{ex.GetType().Name}");
                }
            }
        }

        public static void Exit()
        {
            lock (Gate)
            {
                if (_users == 0) return;
                if (--_users > 0) return;
                try { TimeEndPeriod(1); }
                catch (Exception) { /* 还原失败无所谓：进程退出时系统会自己清 */ }
            }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint milliseconds);
    }
}
