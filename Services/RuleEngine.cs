using System.Diagnostics;
using System.Runtime.InteropServices;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 规则引擎 — 每 100ms 评估信号（音频能量 / 手柄活跃度 / 前台进程名），
/// 输出目标(自动模式 pattern, 强度)，并按 50ms 步进 ramp 平滑过渡到 MotionEngine。
/// 激活时抑制手动/热键冲突（MotionEngine.RuleEngineActive），关闭时恢复手动模式。
/// </summary>
public sealed class RuleEngine : IDisposable
{
    private static readonly TimeSpan TelemetryFreshness = TimeSpan.FromMilliseconds(250);
    private readonly object _sync = new();
    private readonly MotionEngine         _engine;
    private readonly AppSettings          _cfg;
    private readonly AudioReactiveService _audio;
    private readonly GameTelemetryService _telemetry;

    private System.Timers.Timer? _evalTimer;   // 100ms 评估
    private System.Timers.Timer? _rampTimer;   // 50ms 平滑 ramp

    private string _targetMode      = "free_play";
    private double _targetIntensity = 0.72;
    private double _gamepadActivity;            // x64 下 double 读写原子
    private bool   _active;
    private bool   _targetActive = true;
    private bool   _telemetryActive;
    private string _detectedEngineProcess = ""; // 缓存引擎识别的进程名，避免每 tick 都读 MainModule
    private DateTime _activeRuleSinceUtc;
    private RuleConfig? _activeReaction;

    // ── 伴随反应：事件驱动（见 EvaluateCompanionReaction）─────────────
    // 这几个字段记的是「最近一次值得反应的事」，不是「最近一次评估」：
    // 一次事件的保持窗口要跨好几个 100ms 评估周期，所以必须自己记住，不能每 tick 重算。
    private MotionEventKind _companionEventKind = MotionEventKind.None;
    private double _companionEventStrength;      // 0–1
    private DateTime _companionEventAtUtc;
    private double _companionEventHoldSeconds;
    private double _companionReactionIntensity = 1.0;

    /// <summary>脚本播放器叫停伴随期间为真（见 <see cref="YieldToManualPlayback"/>）。</summary>
    private bool _yieldedForManualPlayback;

    /// <summary>画面信号正在驱动设备、伴随让位期间为真（见 <see cref="ScreenWatchService.FollowWantsDevice"/>）。</summary>
    private bool _yieldedForScreen;

    /// <summary>伴随反应：事件要多新才算「刚发生」（秒）。比它旧的只可能落在保持窗口里。</summary>
    private const double CompanionEventFreshSeconds = 0.4;
    /// <summary>伴随反应：事件强度下限 —— 比这更轻的事件不值得抬一次底色。</summary>
    private const double CompanionEventMinStrength = 0.3;
    /// <summary>伴随反应：最短保持时长（秒）。太短会让动作在相邻两个事件之间来回抽搐。</summary>
    private const double CompanionEventMinHoldSeconds = 0.8;

    /// <summary>目标离开 / 规则停用时的缓降时长（秒）。</summary>
    private const double EaseSeconds = 0.6;

    public bool Active => _active;
    /// <summary>手柄活跃度 0..1（由 PlaygroundPage 手柄循环上报）。</summary>
    public double GamepadActivity => _gamepadActivity;
    /// <summary>最近一次评估时的前台进程名（不含 .exe）。</summary>
    public string CurrentProcessName { get; private set; } = "";
    /// <summary>当前命中的规则（无命中为 null）。</summary>
    public RuleConfig? ActiveRule { get; private set; }
    public string TargetMode => _targetMode;
    public double TargetIntensity => _targetIntensity;
    public bool TargetActive => _targetActive;
    public bool TelemetryActive => _telemetryActive;
    /// <summary>是否正在做过渡（引擎缓降 / 姿态混合中）。新增状态：既有属性语义不变。</summary>
    public bool Transitioning => _engine.IsEasing;
    /// <summary>最近一次「进出场 / 规则切换」是怎么落地的（软启动接管 / 原地平滑调整 / 缓降 / 立即停止）。新增诊断状态。</summary>
    public string LastTransition { get; private set; } = "";
    public string SyncQuality { get; private set; } = "基础";
    public string DetectedEngine { get; private set; } = "未识别";

    /// <summary>
    /// 脚本播放器叫停伴随期间为真（见 <see cref="YieldToManualPlayback"/>）。
    /// 状态板据此显示「刚好在放脚本（伴随让位中）」—— 用户要知道「伴随没反应」不是坏了，是让位了。
    /// </summary>
    public bool YieldedForManualPlayback => _yieldedForManualPlayback;

    /// <summary>
    /// 画面信号正在驱动设备、伴随让位期间为真。状态板据此显示「正在跟画面里的动作走」——
    /// 用户选了「画面内容」这条来源，最想知道的就是「画面里没事发生时它到底动不动」。
    /// </summary>
    public bool YieldedForScreen => _yieldedForScreen;

    /// <summary>
    /// 当前动作来源（<see cref="AppSettings.CompanionSource"/> 的小写形式）。
    /// 取值：<c>auto</c> / <c>sound</c> / <c>telemetry</c> / <c>screen</c>。
    /// </summary>
    private string SourceId => (_cfg.CompanionSource ?? "").Trim().ToLowerInvariant();

    /// <summary>「画面内容」这一路：设备由画面信号独占驱动，伴随自己不产生任何自动动作。</summary>
    private bool ScreenOnlySource() => string.Equals(SourceId, "screen", StringComparison.Ordinal);

    /// <summary>画面信号能不能参与驱动（「画面内容」明确指定，或「自动」里作为遥测之后、声音之前的那一层）。</summary>
    private bool ScreenSourceAllowed() => SourceId is "screen" or "auto";

    /// <summary>画面信号此刻是不是要接管设备（只看信号自己，见 ScreenWatchService.FollowWantsDevice）。</summary>
    private bool ScreenWantsDevice()
    {
        if (!ScreenSourceAllowed()) return false;
        ScreenWatchService? watch = ScreenWatchIfNeeded();
        return watch is { IsRunning: true, FollowWantsDevice: true };
    }

    /// <summary>
    /// 画面信号的服务实例。正常情况下就是「测试台 · 画面信号」那一页懒建并跨页面共享的那个；
    /// 用户没打开过那一页时（开机直接进游戏）在这里兜底把它开起来 —— 否则「画面内容」这条来源
    /// 在重启后会静默失效（设置里明明开着画面信号，设备却一动不动，用户只会以为坏了）。
    ///
    /// 三条自我约束：
    /// ① 只有「设置里开着画面信号 + 伴随开着 + 来源要画面」时才动手，绝不偷偷抓别人的画面；
    /// ② <b>复用</b>已有实例（<see cref="ScreenWatchService.Current"/>），不新建第二个 ——
    ///    两个实例会各抓一份画面、各下一次指令，设备会乱抖；
    /// ③ 每 5 秒最多试一次：Start() 会枚举一次窗口，不该被 100ms 的评估周期反复触发。
    /// </summary>
    private ScreenWatchService? ScreenWatchIfNeeded()
    {
        if (!ScreenSourceAllowed() || !_cfg.ScreenWatchEnabled || !_cfg.RuleEngineEnabled) return null;

        ScreenWatchService? service = ScreenWatchService.Current;
        if (service is { IsRunning: true }) return service;

        long now = Environment.TickCount64;
        if (now - _screenStartAttemptMs < 5000) return null;
        _screenStartAttemptMs = now;
        try
        {
            service ??= new ScreenWatchService(_cfg);
            if (!service.IsRunning) service.Start();
            AppLogger.Info("画面信号：为了「画面内容」这条来源自动开启了观察（设置里「画面信号」是开着的）");
            return service.IsRunning ? service : null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面信号：自动开启失败（「画面内容」这条来源暂时用不了）—— {ex.Message}");
            return null;
        }
    }

    /// <summary>上次尝试自动开启画面信号观察的时刻（Environment.TickCount64），做节流用。</summary>
    private long _screenStartAttemptMs;

    /// <summary>
    /// 「现在在响应什么」——状态板用的一句话。顺序 = 谁在真正决定动作：
    /// 让位中 &gt; 游戏遥测 &gt; 刚发生的声音事件 &gt; 你的操作（手柄 / 动作键） &gt; 只是底色。
    /// 刻意做成无副作用的只读属性：UI 每 150ms 轮询一次，它不能顺带改任何状态。
    /// </summary>
    public string ResponseLabel
    {
        get
        {
            if (_yieldedForManualPlayback) return "刚好在放脚本（伴随让位中）";
            if (_yieldedForScreen) return "画面内容（跟着画面里的动作走）";
            if (_telemetryActive) return "游戏遥测";
            if (CompanionEventHeld())
                return $"{MotionVocabulary.Label(_companionEventKind)}({_companionEventStrength:0.00})";
            if (_engine.ActionLinkEngaged) return "你的操作";
            return "只是底色";
        }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public RuleEngine(
        MotionEngine engine,
        AppSettings cfg,
        AudioReactiveService audio,
        GameTelemetryService telemetry)
    {
        _engine = engine;
        _cfg    = cfg;
        _audio  = audio;
        _telemetry = telemetry;
        _engine.StateChanged += OnEngineStateChanged;
        _telemetry.FrameReceived += OnTelemetryFrame;
        if (cfg.RuleEngineEnabled) Start();
    }

    /// <summary>依据当前设置开启/关闭（测试页开关变更后调用）。</summary>
    public void Refresh()
    {
        if (_cfg.RuleEngineEnabled) Start();
        else Stop();
    }

    /// <summary>由手柄轮询循环上报活跃度（0..1）。</summary>
    public void SetGamepadActivity(double v) => _gamepadActivity = Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1);

    private void OnEngineStateChanged()
    {
        lock (_sync)
        {
            if (!_cfg.RuleEngineEnabled) return;
            if (_engine.CanRun && !_active) StartLocked();
            else if (!_engine.CanRun && _active) StopLocked();
        }
    }

    /// <summary>读取当前前台进程名（供测试页在未激活时也显示）。</summary>
    public string ReadForegroundProcessName() => GetForegroundProcessName();

    public GameEngineInfo DetectEngine(string? processName) =>
        GameEngineDetector.DetectProcess(processName);

    // ── Lifecycle ─────────────────────────────────────────────────────
    public void Start()
    {
        lock (_sync) StartLocked();
    }

    private void StartLocked()
    {
        if (_active) return;
        // 刚好在放脚本：伴随让位中，不抢回设备（脚本每帧下发都会和它打架）。
        // 脚本停下时由 ResumeAfterManualPlayback 重新武装，所以这里只是「等」。
        // 用 NoteTransition 而不是 AppLogger 直写：它按文本去重，而 StartLocked 会被
        // 引擎的每一次状态变化（StopAll 等）调到，直写日志会刷屏。
        if (_yieldedForManualPlayback)
        {
            NoteTransition("伴随让位中：等脚本结束再接管", log: true);
            return;
        }
        if (!_engine.CanRun)
        {
            _engine.RuleEngineActive = false;
            AppLogger.Warn("规则引擎未启动：设备尚未连接并解锁");
            return;
        }

        // 动作来源 = 画面内容：设备由画面信号独占驱动，规则这一路**一个自动动作都不启动**。
        //
        // 两个必须这么做的理由：
        // ① 引擎在跑自动动作时 CanAcceptDirectInput 为假，画面信号根本拿不到直接下发权；
        // ② 更要紧的是 RuleEngineActive：它一旦为真、而这次接管又不是伴随启动的
        //    （_autoFromCompanion 为假），引擎会把它判成「独占接管」而一律拒绝直接下发 ——
        //    画面信号会被永久挡在门外。所以这里<b>不</b>置 RuleEngineActive。
        // 只留评估定时器：状态板要显示「目标在不在前台 / 画面里有没有事发生」。
        if (ScreenOnlySource())
        {
            _active = true;
            _engine.RuleEngineActive = false;
            _evalTimer ??= MakeTimer(100, EvaluateTick);
            _evalTimer.Start();
            NoteTransition("伴随：动作来源是「画面内容」，自动动作交给画面信号（规则这一路不启动）", log: true);
            return;
        }

        _active = true;
        _engine.RuleEngineActive = true;

        // 「先过渡、后接管」：接管只有这一次调用，启动本身就是过渡。
        // StartRuleAuto → StartAutoCore 不再发 DSTOP：它只 StopAllLocked（清模式、作废旧 tick），
        // 然后置 _blendInPending，并在 ApplySoftStart 里把「上一次实际输出位置」当起点：
        // 第一帧离得远就用 300ms 长插值滑过去（TakeBlendInSeconds），随后在 SoftStartSeconds
        // （0.45–2.2s）内 ramp 到规则动作。所以这里刻意不做 StopAll，也不做 BlendTo 预混合 ——
        // 任何 Start* 都会 InterruptEase，预混合会被这次接管打断，等于白停一次再硬起。
        if (!_engine.StartRuleAuto(_targetMode))
        {
            _active = false;
            _engine.RuleEngineActive = false;
            AppLogger.Warn("规则引擎接管失败，输出保持锁定");
            return;
        }
        NoteTransition("规则接管：引擎软启动过渡", log: true);

        _evalTimer ??= MakeTimer(100, EvaluateTick);
        _rampTimer ??= MakeTimer(50,  RampTick);
        _evalTimer.Start();
        _rampTimer.Start();
        AppLogger.Info("规则引擎已启动");
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
    }

    private void StopLocked()
    {
        bool wasActive = _active;
        _active = false;
        _engine.RuleEngineActive = false;
        _evalTimer?.Stop();
        _rampTimer?.Stop();

        ActiveRule = null;
        _activeReaction = null;
        _telemetryActive = false;
        _yieldedForScreen = false;   // 伴随停了：让位标记一起清（否则状态板会一直写着「画面内容」）
        SyncQuality = "基础";
        if (!wasActive) return;
        // 「画面内容」这条来源下本类从来没起过自动动作，设备归画面信号管 ——
        // 这里要是照样走一遍 EaseDown，会白白把引擎设成「正在过渡」0.6 秒，
        // 那 0.6 秒里画面信号反而拿不到直接下发权。
        if (ScreenOnlySource())
        {
            NoteTransition("伴随停止：画面跟随由画面信号自己收尾");
            AppLogger.Info("规则引擎已停止");
            return;
        }
        // 停用规则不再当场刹住：缓降回中位再停发（急停 / 串口掉线 / 用户按「■ 停止」才走立即停）。
        EaseDownOrStopLocked("规则停用");
        _engine.IntensityScale = _cfg.IntensityScale;
        AppLogger.Info("规则引擎已停止");
    }

    // ── 让位给快捷键脚本（调用方：FunscriptPlayerService.Play / Stop）────
    /// <summary>
    /// 让位：脚本要驱动设备了，伴随把设备交出去 —— 「让位」而不是「拒绝脚本」。
    ///
    /// 以前的做法是反过来：脚本一开播就发现 <c>RuleEngineActive</c> 为真，直接告诉用户
    /// 「声音响应 / 游戏伴随正在接管」，于是「边玩游戏边放脚本」这条路根本不通；
    /// 而伴随自己只提供一个固定底色 + 音量过阈值换个动作，比脚本还单调 —— 用户当然会说
    /// 「还不如用快捷键放脚本」。现在伴随让位，脚本放它的、伴随等回来。
    ///
    /// 只停调度，<b>不</b>清「绑定了哪个游戏 / 目标在不在前台 / 上一个动作是什么」：
    /// 这些状态留着，脚本一停才能判断该不该接回去，而不是从头再走一遍进出场。
    /// </summary>
    public void YieldToManualPlayback()
    {
        lock (_sync)
        {
            bool wasActive = _active;
            _yieldedForManualPlayback = true;
            if (!wasActive)
            {
                // 本来就没接管（伴随没开 / 目标不在前台）：没有设备要交出去，只立让位标记。
                // 标记照样要立 —— 它同时挡住「脚本放到一半时伴随自己接管回去」。
                // 这条日志一次播放只写一条（Play 才调这里），不会刷屏，留着方便排查。
                AppLogger.Info("伴随让位：脚本开始播放（伴随本来就没在接管，只标记让位）");
                return;
            }
            StopLocked();   // 停调度 + 释放 RuleEngineActive + 缓降让出设备
            AppLogger.Info("伴随让位：脚本开始播放，规则调度已暂停");
        }
    }

    /// <summary>
    /// 让位结束：脚本停了 / 放完了，伴随该接回来就接回来。调用是可以重复的（幂等）。
    ///
    /// <b>会接回来</b>：伴随仍然开着 + 设备可动 + 绑定了游戏。
    /// <b>不接回来</b>：伴随被关掉 / 设备掉线或在急停锁定 / 还没绑定游戏。
    /// 目标游戏此刻在不在前台<b>不影响</b>接不接回来 —— 不在前台时 <see cref="Evaluate"/> 本来
    /// 就不产生动作（设备停着），切回游戏即刻接上，这也正是界面上「切回游戏就会继续」的说法。
    /// </summary>
    public void ResumeAfterManualPlayback()
    {
        lock (_sync)
        {
            bool wasYielded = _yieldedForManualPlayback;
            _yieldedForManualPlayback = false;

            if (!_cfg.RuleEngineEnabled)
            {
                if (wasYielded) AppLogger.Info("伴随恢复：伴随已关闭，保持停止");
                return;
            }
            if (!_engine.CanRun)
            {
                if (wasYielded) AppLogger.Info("伴随恢复：设备不可动（未连接 / 急停锁定），保持停止");
                return;
            }
            if (!CompanionBound())
            {
                if (wasYielded) AppLogger.Info("伴随恢复：还没绑定游戏，保持停止");
                return;
            }
            if (_active)
            {
                if (wasYielded) AppLogger.Info("伴随恢复：已经在接管，无需重来");
                return;
            }
            StartLocked();
            AppLogger.Info("伴随恢复：脚本已结束，重新接管");
        }
    }

    /// <summary>
    /// 绑定了一个游戏进程名。留空 = 不接管，所以不会误动别人的程序（与 GameCompanionRules.Apply
    /// 里 <c>gallery.Enabled = process.Length &gt; 0</c> 是同一个前提）。
    /// </summary>
    private bool CompanionBound() => (_cfg.CompanionProcess ?? "").Trim().Length > 0;

    // ── Ticks ─────────────────────────────────────────────────────────
    private void EvaluateTick()
    {
        lock (_sync) EvaluateTickLocked();
    }

    private void EvaluateTickLocked()
    {
        try
        {
            if (!_active) return;
            if (!_engine.CanRun)
            {
                Stop();
                return;
            }

            // 画面信号要接管设备（用户把来源选成「画面内容」，或「自动」里遥测没有、画面又过了线）：
            // 伴随把自动动作缓降停掉、让出设备，由 ScreenWatchService 独占驱动。
            if (ScreenSourceAllowed() && ScreenWantsDevice())
            {
                YieldToScreenLocked();
                return;
            }
            ResumeFromScreenLocked();

            var (mode, intensity) = Evaluate();
            _targetMode      = mode;
            _targetIntensity = Math.Clamp(intensity, 0.1, 2.0);

            if (!_targetActive)
            {
                _telemetryActive = false;
                // 目标游戏离开 / 规则停用 / 没有规则命中：缓降回中后停发，不再当场刹住。
                // 只在「确实还在动」时触发一次 —— 缓降一开始就把运行标志清掉（IsRunning 随即为假），
                // 所以不会每 100ms 重复下发。
                if (_engine.IsRunning) EaseDownOrStopLocked("目标离开");
                return;
            }

            if (_telemetryActive)
            {
                if (_telemetry.TryGetLatest(_cfg.CompanionProcess, TelemetryFreshness, out GameTelemetrySnapshot latest)
                    && !latest.Frame.IsStop
                    && latest.Frame.Confidence >= 0.25)
                {
                    _engine.IntensityScale = _cfg.IntensityScale;
                    _engine.ApplyTelemetryFrame(
                        latest.Frame.SessionId,
                        latest.Frame.Sequence,
                        GameTelemetryProtocol.ToAxes(latest.Frame, applyIntensity: false),
                        TelemetrySourceLabel(latest.Frame),
                        latest.Frame.TransitionMs,
                        latest.Frame.Intensity);
                }
                else
                {
                    // 帧过期 / 置信度掉下来：不在这里 StopTelemetrySync()（那是 DSTOP 立即停）。
                    // 只标记遥测结束，交给下一个评估周期接管：改走自动动作时 StartRuleAuto 会一并
                    // 清掉遥测会话；万一没有任何东西接手，引擎自己的「0.3s 无帧」看门狗也会停掉它。
                    _telemetryActive = false;
                }
                return;
            }

            // 遥测 → 自动：不再无条件 StopTelemetrySync()（那是 DSTOP 立即停）。
            // 正常情况下交给下面的接管一并清掉遥测会话（StartAutoCore → StopAllLocked 会清），
            // 于是这段握手里少一次多余的硬停；只有「自动动作已经在跑、遥测会话也还活着」
            // 这种理论上的双调度器才需要单独停。
            if (_engine.AutoRunning && _engine.TelemetryRunning) _engine.StopTelemetrySync();
            // 被急停打断后，也是在这里重新接管
            EnsureTargetAppliedLocked();
        }
        catch (Exception ex)
        {
            AppLogger.Error("规则引擎评估异常", ex);
        }
    }

    // ── 让位给画面信号（「画面内容」这条动作来源）─────────────────────
    /// <summary>
    /// 画面信号要驱动设备了：伴随把自动动作缓降停掉、让出设备。
    ///
    /// 为什么必须让位而不是各动各的：引擎的「直接下发」有互斥 —— 自动动作在跑
    /// （<c>AutoRunning</c>）时 <c>CanAcceptDirectInput</c> 为假，画面信号一条指令都发不出去。
    /// 让位走的是和脚本一样的缓降（不是急停），停完画面那边下一拍就能拿到控制权。
    ///
    /// 「源不在前台就停」的语义没变：画面信号自己也会在离开前台时缓降回中
    /// （见 ScreenWatchService.FollowTargetProblem），本方法只管把规则这一路腾干净。
    /// </summary>
    private void YieldToScreenLocked()
    {
        _telemetryActive = false;
        _targetActive = false;
        ActiveRule = null;
        _activeReaction = null;
        if (!_yieldedForScreen)
        {
            _yieldedForScreen = true;
            AppLogger.Info("伴随让位：画面信号开始接管设备（规则这一路不再下发自动动作）");
        }
        if (_engine.AutoRunning) EaseDownOrStopLocked("画面信号接管");
        else NoteTransition("画面信号接管：伴随不再下发自动动作");
    }

    /// <summary>画面信号不再要设备了：伴随恢复自己的规则行为（下一次评估就会重新接管/缓降）。</summary>
    private void ResumeFromScreenLocked()
    {
        if (!_yieldedForScreen) return;
        _yieldedForScreen = false;
        NoteTransition("画面信号结束：伴随重新接管", log: true);
    }

    private void RampTick()
    {
        lock (_sync) RampTickLocked();
    }    private void RampTickLocked()
    {
        try
        {
            if (!_active || !_engine.CanRun || _telemetryActive || !_engine.AutoRunning) return;

            // 规则切换就落在这里：只写模式 + ramp 强度，不重启模式（判定见 EnsureTargetAppliedLocked）。
            _engine.AutoPattern = _targetMode;
            double cur  = _engine.IntensityScale;
            double next = cur + (_targetIntensity - cur) * 0.08;   // 平滑 ramp
            _engine.IntensityScale = Math.Clamp(next, 0.1, 2.0);
        }
        catch (Exception ex)
        {
            AppLogger.Error("规则引擎 ramp 异常", ex);
        }
    }

    // ── 过渡（进出场 / 规则切换）────────────────────────────────────────
    /// <summary>
    /// 把当前规则目标落到引擎上，尽量不「重来」。
    ///
    /// ① 引擎已经在跑自动动作 → 只改 AutoPattern / IntensityScale（RampTick 每 50ms 逼近目标强度，
    ///    换动作由引擎序列器自己做交叉淡入）：没有 StopAll、没有重启。
    ///    判定见 <see cref="GameCompanionRules.CanAdjustSmoothly"/>：同一 base 动作族且强度差 &lt; 0.2
    ///    算「只是强度变了」；即使换了动作族这里也不重起 —— 重起要走 StartRuleAuto
    ///    （序列器 Reset + 重新软启动），比引擎自己的交叉淡入更生硬。
    /// ② 引擎没在跑（首次接管 / 刚被缓降或急停停下）→ StartRuleAuto 起一次：
    ///    这次启动本身就是过渡（首帧长插值 + ApplySoftStart 包络），正在缓降时接管还会顺带
    ///    打断缓降（任何 Start* 都会 InterruptEase）。
    /// </summary>
    private void EnsureTargetAppliedLocked()
    {
        // 自动动作的第一道门就是 RuleEngineActive（见 MotionEngine.StartRuleAuto）。
        // 正常路径上它早就为真，但「动作来源」从「画面内容」切回其它值时，那一档**有意**把它置成 false
        // （否则画面信号会被判成「别人独占接管」而永远拿不到直接下发权）——
        // 这里是唯一能把它复位的地方；不复位就会出现「切回去以后再也不会动」。
        _engine.RuleEngineActive = true;

        if (_engine.AutoRunning)
        {
            string appliedMode = _engine.AutoPattern;
            double appliedIntensity = _engine.IntensityScale;
            _engine.AutoPattern = _targetMode;
            NoteTransition(GameCompanionRules.CanAdjustSmoothly(appliedMode, appliedIntensity, _targetMode, _targetIntensity)
                ? "规则切换：原地平滑调整"
                : "规则切换：换动作族，交给引擎交叉淡入");
            return;
        }

        bool interruptedEase = _engine.IsEasing;      // 正在缓降/混合时接管 → 这次接管打断了过渡
        if (_engine.StartRuleAuto(_targetMode))
        {
            NoteTransition(interruptedEase ? "规则接管：打断过渡并软启动" : "规则接管：引擎软启动过渡", log: true);
            return;
        }
        NoteTransition("规则接管失败：自动动作未能启动", log: true);
    }

    /// <summary>
    /// 优雅停止：让引擎缓降回中位后停发（20ms 一步，不发 DSTOP），而不是当场刹住。
    /// 急停 / 串口掉线 / 用户按「■ 停止」不走这里，它们要的是立即停。
    ///
    /// 两条兜底：
    /// · 设备不可动（掉线 / 急停锁定）→ 缓降发不出去，保持原逻辑：立即停（顺带清掉运行标志）；
    /// · 调用时确实还有模式在跑、调用后却仍在跑 → 说明缓降没有接管（引擎行为若有变化），
    ///   补一次立即停，否则会出现「规则已经停了、设备还在动」的悬挂状态。
    /// </summary>
    private void EaseDownOrStopLocked(string reason)
    {
        if (!_engine.CanRun)
        {
            _engine.StopAll();
            NoteTransition($"{reason}：立即停止（设备不可动）");
            return;
        }

        bool moving = _engine.IsRunning;
        _engine.EaseDown(EaseSeconds);
        if (moving && _engine.IsRunning)
        {
            _engine.StopAll();
            NoteTransition($"{reason}：立即停止（缓降未接管）", log: true);
            return;
        }
        NoteTransition(moving ? $"{reason}：缓降回中 {EaseSeconds:0.0} 秒" : $"{reason}：设备已静止");
    }

    /// <summary>记录最近一次过渡方式；文本变化时才写日志，避免每 100ms 刷屏。</summary>
    private void NoteTransition(string text, bool log = false)
    {
        bool changed = !string.Equals(LastTransition, text, StringComparison.Ordinal);
        LastTransition = text;
        if (changed && log) AppLogger.Info($"规则引擎过渡：{text}");
    }

    // ── Evaluation ────────────────────────────────────────────────────
    private (string Mode, double Intensity) Evaluate()
    {
        double audio = _audio.Energy;
        double gp    = _gamepadActivity;
        CurrentProcessName = GetForegroundProcessName();
        if (!string.Equals(_detectedEngineProcess, CurrentProcessName, StringComparison.OrdinalIgnoreCase))
        {
            // 只有前台进程变化时才重新识别引擎，避免每个评估周期都读取进程 MainModule。
            _detectedEngineProcess = CurrentProcessName;
            DetectedEngine = GameEngineDetector.DetectProcess(CurrentProcessName).DisplayName;
        }

        bool targetIsForeground = string.Equals(
            CurrentProcessName,
            GameTelemetryProtocol.NormalizeProcessName(_cfg.CompanionProcess),
            StringComparison.OrdinalIgnoreCase);

        // 动作来源（界面上的「动作来源」四选一）：
        //   sound     = 只用声音 —— 即使这个游戏在发遥测也不看（免得用户以为「不理会游戏信号」是空话）；
        //   telemetry = 只用游戏信号 —— 收不到新鲜遥测就当目标不活跃，绝不用声音顶替（静默待机）；
        //   screen    = 只用画面内容 —— 设备由 ScreenWatchService 独占驱动（画面上过线才动，
        //               离开前台就缓降回停）；本引擎**不启动任何自动动作**，也完全不看遥测；
        //   auto      = 按优先级自动挑一路：<b>有遥测用遥测 → 有画面信号且过线用画面 → 否则声音兜底</b>
        //               （前两层见 EvaluateTickLocked 的让位逻辑与 ScreenWatchService.FollowBlocker；
        //                走到这里就说明前两层都没有，于是用下面的规则：底色动作 + 声音兜底）。
        string source = _cfg.CompanionSource;
        bool soundOnly = string.Equals(source, "sound", StringComparison.OrdinalIgnoreCase);
        bool telemetryOnly = string.Equals(source, "telemetry", StringComparison.OrdinalIgnoreCase);
        bool screenOnly = string.Equals(source, "screen", StringComparison.OrdinalIgnoreCase);

        if (!soundOnly && !screenOnly
            && targetIsForeground
            && _telemetry.TryGetLatest(_cfg.CompanionProcess, TelemetryFreshness, out GameTelemetrySnapshot telemetry)
            && !telemetry.Frame.IsStop
            && telemetry.Frame.Confidence >= 0.25)
        {
            _telemetryActive = true;
            SyncQuality = telemetry.Frame.Confidence >= 0.75 ? "精确" : "增强";
            _targetActive = true;
            ActiveRule = null;
            return ("telemetry", Math.Clamp(telemetry.Frame.Intensity, 0.1, 2));
        }
        _telemetryActive = false;

        if (telemetryOnly)
        {
            // 只用游戏信号但这一刻没有新鲜遥测（游戏没装桥接插件 / 还没开打 / 帧过期）：
            // 不退到规则、不用声音顶替，直接当作「目标不活跃」—— 走原来的缓降回中路径保持静默。
            SyncQuality = "等待遥测";
            ActiveRule = null;
            _activeReaction = null;
            _targetActive = false;
            return ("free_play", 0.1);
        }

        if (screenOnly)
        {
            // 画面内容这一路：设备归画面信号（它在自己的抓帧循环里下发），本引擎一个自动动作都不产生。
            // 这里返回「不活跃」而不是别的：只要没有该动的事，走的就是「缓降回中后停发」那条老路，
            // 和「目标不在前台就静默」完全一致 —— 语义没变，只是动作的出处换了人。
            SyncQuality = "画面";
            ActiveRule = null;
            _activeReaction = null;
            _targetActive = false;
            return ("free_play", 0.1);
        }

        SyncQuality = "基础";

        RuleConfig[] rules;
        lock (_cfg.Rules) rules = _cfg.Rules.Where(rule => rule.Enabled).OrderByDescending(rule => rule.Priority).ToArray();

        RuleConfig? gallery = rules.FirstOrDefault(rule => RoleOf(rule) == "gallery"
            && Matches(rule, audio, gp, CurrentProcessName, useReleaseThreshold: false));
        bool companionOnly = rules.Any(rule =>
            rule.Id.StartsWith("companion-", StringComparison.OrdinalIgnoreCase));
        string galleryMode = gallery == null ? "free_play" : SafeMode(gallery);
        double galleryIntensity = gallery?.Intensity ?? 0.72;

        // 伴随反应是本周期里唯一「有副作用」的判定（命中新事件会刷新保持窗口），
        // 所以整个评估只算这一次，下面的「保持中 / 新命中」都读这个结果 ——
        // 同一个事件不会被重复刷新时间戳（那会让保持窗口无限延长）。
        RuleConfig? companionReaction = rules.FirstOrDefault(rule =>
            RoleOf(rule) == "reaction" && IsCompanionReactionRule(rule));
        bool companionHot = companionReaction != null
            && EvaluateCompanionReaction(companionReaction, galleryIntensity);

        if (_activeReaction is { Enabled: true } current)
        {
            bool stillReacting = IsCompanionReactionRule(current)
                // 伴随：保持窗口由事件自己的时长决定，不用规则里的 MinHoldMs
                //（那个值是「音量过阈值以后至少压住多久」，是旧的音量路径留下来的）。
                ? companionHot
                // 用户自定义规则：一字未改（最短保持 + 释放阈值）。
                : (DateTime.UtcNow - _activeRuleSinceUtc).TotalMilliseconds < current.MinHoldMs
                  || Matches(current, audio, gp, CurrentProcessName, useReleaseThreshold: true);
            if (stillReacting)
            {
                _targetActive = true;
                ActiveRule = current;
                return ResolveReaction(current);
            }
            _activeReaction = null;
        }

        // 伴随反应走事件驱动（见 EvaluateCompanionReaction），用户自定义的 reaction 规则照旧走 Matches。
        RuleConfig? reaction = rules.FirstOrDefault(rule => RoleOf(rule) == "reaction"
            && (IsCompanionReactionRule(rule)
                ? companionHot
                : Matches(rule, audio, gp, CurrentProcessName, useReleaseThreshold: false)));
        if (reaction != null)
        {
            _targetActive = true;
            _activeReaction = reaction;
            ActiveRule = reaction;
            _activeRuleSinceUtc = DateTime.UtcNow;
            return ResolveReaction(reaction);
        }

        RuleConfig? filler = rules.FirstOrDefault(rule => RoleOf(rule) == "filler"
            && Matches(rule, audio, gp, CurrentProcessName, useReleaseThreshold: false));
        if (companionOnly && gallery == null && filler == null)
        {
            ActiveRule = null;
            _targetActive = false;
            return ("free_play", 0.1);
        }

        _targetActive = true;
        ActiveRule = gallery ?? filler;
        if (gallery != null) return (galleryMode, galleryIntensity);
        if (filler != null) return (SafeMode(filler), filler.Intensity);
        return (galleryMode, galleryIntensity);
    }

    // ── 伴随反应：事件驱动 ─────────────────────────────────────────────
    /// <summary>是不是伴随自己编译出来的规则（companion-base / companion-reaction，见 GameCompanionRules.Apply）。</summary>
    private static bool IsCompanionReactionRule(RuleConfig rule) =>
        rule.Id.StartsWith("companion-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 一条命中的 reaction 规则最终用哪个动作、多强。
    /// 伴随（companion-reaction）的动作族与强度来自事件本身（见 CompanionReactionProfile /
    /// CompanionReactionIntensity），规则里的 Mode / Intensity 只是编译期给的占位值；
    /// 用户自定义规则照旧读规则自己的值 —— 通用规则机制一点没动。
    /// </summary>
    private (string Mode, double Intensity) ResolveReaction(RuleConfig rule) =>
        IsCompanionReactionRule(rule) && _companionEventKind != MotionEventKind.None
            ? (CompanionReactionProfile(_companionEventKind).Pattern, _companionReactionIntensity)
            : (SafeMode(rule), rule.Intensity);

    /// <summary>
    /// 伴随反应的事件驱动判定。返回 true = 这一刻（或它自己的保持窗口内）确实有值得反应的事。
    ///
    /// <b>为什么不再用 <see cref="Matches"/> 里的 "game"（进程 + 音量 ≥ 阈值）</b>：
    /// 音量只回答「够不够大」，枪声、音乐渐强、鼓点会被判成同一件事，于是反应永远是同一个动作
    /// 反复触发 —— 比直接放脚本还单调，这正是用户说的「差点意思」。
    /// 音频事件检测器本来就在区分「冲击 / 渐强 / 节拍」，这里直接读它的结论：
    /// 事件类型决定动作族、事件强度决定抬多高。伴随从此只负责「该不该动」和「给一个不重复的底色」，
    /// 真正的反应交给本来就更擅长的检测器。
    ///
    /// 「该不该动」只剩一个前提：绑定的那个游戏在前台。
    /// </summary>
    private bool EvaluateCompanionReaction(RuleConfig rule, double baseIntensity)
    {
        // 用户选「不反应」就是真的不反应 —— 这条优先于下面任何判定
        //（正常情况下 GameCompanionRules 已经把这条规则 Enabled=false，这里再兜一次，
        //  免得别的路径把规则打开以后「不反应」这个选择被绕过去）。
        if (string.Equals(_cfg.CompanionReaction?.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!RuleTargetIsForeground(rule)) return false;

        // 新事件：必须「刚发生」（< 0.4s）且够强（≥ 0.3）。比 0.4s 更旧的事件只可能落在下面的
        // 保持窗口里 —— 那时候不该再刷新动作，否则同一个事件会被一直重触发。
        double age = _audio.LastEventAgeSeconds;
        MotionEventKind kind = _audio.LastEventKind;
        double strength = _audio.LastEventStrength;
        if (kind != MotionEventKind.None
            && age >= 0 && age < CompanionEventFreshSeconds
            && strength >= CompanionEventMinStrength)
        {
            double clamped = Math.Clamp(strength, 0, 1);
            _companionEventKind = kind;
            _companionEventStrength = clamped;
            _companionEventAtUtc = DateTime.UtcNow;
            // 保持 max(0.8s, 语汇时长 × 2)：一次「冲击」只有 0.3–0.4 秒，
            // 事件一过就立刻退回底色的话，动作会在相邻两个事件之间来回抽搐；
            // ×2 就是留给「这件事刚完、下一件还没来」的那段空隙，让它落在同一个动作里。
            _companionEventHoldSeconds = Math.Max(CompanionEventMinHoldSeconds,
                MotionVocabulary.DurationSeconds(kind, clamped) * 2.0);
            _companionReactionIntensity = CompanionReactionIntensity(kind, clamped, baseIntensity);
        }

        return CompanionEventHeld();
    }

    /// <summary>最近一次事件还在它的保持窗口里。无副作用，状态板每 150ms 也会读它。</summary>
    private bool CompanionEventHeld() =>
        _companionEventKind != MotionEventKind.None
        && (DateTime.UtcNow - _companionEventAtUtc).TotalSeconds < _companionEventHoldSeconds;

    /// <summary>
    /// 事件 → 动作族 + 强度抬升系数。这是伴随反应的<b>唯一</b>映射真源：
    /// 改动作只改这里，将来往 MotionEventKind 里加成员也只在这里加一行。
    /// </summary>
    private static (string Pattern, double Lift) CompanionReactionProfile(MotionEventKind kind) => kind switch
    {
        // 冲击（爆炸 / 枪声 / 撞击）：快而深 + 明显回落，与 MotionVocabulary 里 Impact 的形态一致。
        MotionEventKind.Impact => ("intense_thrust", 0.5),
        // 渐强（音乐起伏 / 引擎轰鸣）：不是「一件事」而是一段，慢而连贯。
        MotionEventKind.Swell => ("organic_flow", 0.5),
        // 节拍：短、浅、跟拍，有节奏的重复点。
        MotionEventKind.Beat => ("deep_pulse", 0.5),
        // 人声（呻吟）：用户要的是「进进出出」的<b>抽插</b>，不是缓缓连绵的起伏 —— 所以动作族
        // 与冲击同为 intense_thrust；抬升系数给到 0.6（比其它事件更高），因为这是用户最在意的那种输入。
        //
        // 人声（呻吟）：检测器现在会真的发这个事件（2026-09 从「渐强」里分出来，
        // 判据是「中频占主要能量的跃升」），所以这一条是可达到的。
        MotionEventKind.Voice => ("intense_thrust", 0.6),
        _ => ("intense_thrust", 0.6),
    };

    /// <summary>
    /// 事件 → 反应强度：底色强度打底，事件越强抬得越高（+0~50%，人声 +0~60%），
    /// 冲击再额外 ×1.1 —— 反应是「底色之上的一次强调」，不该变成另一套强度体系。
    /// 最后按仓库里一贯的做法夹进舒适档：限速与舒适约束在引擎侧
    ///（AutoTick → 序列器 → ApplySoftStart → MotionSafetyLimiter）还会再走一遍，
    /// 这里只是不让目标值一开始就超出去，绝不绕开限速。
    /// </summary>
    private double CompanionReactionIntensity(MotionEventKind kind, double strength, double baseIntensity)
    {
        double basis = Math.Clamp(double.IsFinite(baseIntensity) ? baseIntensity : 1.0, 0.1, 2.0);
        double lift = CompanionReactionProfile(kind).Lift;
        double value = basis * (1.0 + lift * Math.Clamp(strength, 0, 1));
        if (kind == MotionEventKind.Impact) value *= 1.1;   // 冲击最瞬态，比人声再猛一点才「打得住」
        double ceiling = Math.Max(0.1, _engine.ActiveComfortProfile.MaxIntensity);
        return Math.Clamp(value, 0.1, Math.Min(2.0, ceiling));
    }

    /// <summary>规则里的 Match 就是「绑定的那个游戏」，这里判断它此刻是不是前台。</summary>
    private bool RuleTargetIsForeground(RuleConfig rule)
    {
        string target = GameTelemetryProtocol.NormalizeProcessName(rule.Match);
        return target.Length > 0
            && string.Equals(CurrentProcessName, target, StringComparison.OrdinalIgnoreCase);
    }

    private void OnTelemetryFrame(GameTelemetrySnapshot snapshot)
    {
        lock (_sync) OnTelemetryFrameLocked(snapshot);
    }

    private void OnTelemetryFrameLocked(GameTelemetrySnapshot snapshot)
    {
        try
        {
            if (snapshot.Frame.IsStop)
            {
                if (_engine.StopTelemetrySession(snapshot.Frame.SessionId))
                {
                    _telemetryActive = false;
                    _targetActive = false;
                    SyncQuality = "stopped";
                }
                return;
            }

            if (!_active || !_engine.CanRun) return;

            // 动作来源 = 只用声音 / 只用画面内容：游戏发来的帧一律不理会（与 Evaluate 里的门控同一个判断）。
            // 不加这一条，界面写着「不理会游戏信号」，设备却还会被遥测帧直接驱动。
            if (string.Equals(_cfg.CompanionSource, "sound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_cfg.CompanionSource, "screen", StringComparison.OrdinalIgnoreCase)) return;

            string target = GameTelemetryProtocol.NormalizeProcessName(_cfg.CompanionProcess);
            if (target.Length == 0
                || (snapshot.Frame.Process != "*"
                    && !string.Equals(snapshot.Frame.Process, target, StringComparison.OrdinalIgnoreCase))
                || !string.Equals(GetForegroundProcessName(), target, StringComparison.OrdinalIgnoreCase))
            {
                // 目标游戏离开 / 不是目标进程：不再直接 StopTelemetrySession（那是 DSTOP 立即停），
                // 只把遥测标记为结束，交给下一个评估周期（≤100ms）走缓降接管 —— 设备缓回中位而不是当场刹住。
                // 会话语义没变：引擎在 0.3s 收不到新帧时会自行结束；若评估周期改走规则自动动作，
                // StartRuleAuto 会一并清掉遥测会话。急停与游戏显式 STOP 帧仍然是立即停。
                _targetActive = false;
                _telemetryActive = false;
                return;
            }
            if (snapshot.Frame.Confidence < 0.25)
            {
                // 置信度不足：同样不在这里立即停会话，交给评估周期决定（先在规则模式里平滑接管）。
                _telemetryActive = false;
                return;
            }

            _targetActive = true;
            _telemetryActive = true;
            SyncQuality = snapshot.Frame.Confidence >= 0.75 ? "精确" : "增强";
            _targetMode = "telemetry";
            _targetIntensity = Math.Clamp(snapshot.Frame.Intensity, 0.1, 2);
            _engine.IntensityScale = _cfg.IntensityScale;
            _engine.ApplyTelemetryFrame(
                snapshot.Frame.SessionId,
                snapshot.Frame.Sequence,
                GameTelemetryProtocol.ToAxes(snapshot.Frame, applyIntensity: false),
                TelemetrySourceLabel(snapshot.Frame),
                snapshot.Frame.TransitionMs,
                snapshot.Frame.Intensity);
        }
        catch (Exception ex)
        {
            AppLogger.Error("游戏遥测帧应用失败", ex);
        }
    }

    private static string TelemetrySourceLabel(GameTelemetryFrame frame)
    {
        string engine = string.IsNullOrWhiteSpace(frame.Engine) ? "游戏桥接" : frame.Engine;
        return string.IsNullOrWhiteSpace(frame.Scene) ? engine : $"{engine} · {frame.Scene}";
    }

    private static bool Matches(
        RuleConfig rule,
        double audio,
        double gamepad,
        string processName,
        bool useReleaseThreshold)
    {
        double threshold = useReleaseThreshold ? rule.ReleaseThreshold : rule.Threshold;
        bool processMatches = !string.IsNullOrWhiteSpace(rule.Match)
            && string.Equals(processName, rule.Match.Trim(), StringComparison.OrdinalIgnoreCase);
        return rule.Type?.Trim().ToLowerInvariant() switch
        {
            "audio" => audio >= threshold,
            "gamepad" => gamepad >= threshold,
            "process" => processMatches,
            "game" => processMatches && audio >= threshold,
            _ => false,
        };
    }

    private static string SafeMode(RuleConfig r) =>
        string.IsNullOrWhiteSpace(r.Mode) ? "slow_sine" : r.Mode;

    private static string RoleOf(RuleConfig rule) => rule.Role?.Trim().ToLowerInvariant() switch
    {
        "gallery" => "gallery",
        "filler" => "filler",
        _ => "reaction",
    };

    private static string GetForegroundProcessName()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return System.IO.Path.GetFileNameWithoutExtension(p.ProcessName) ?? "";
        }
        catch { return ""; }
    }

    // ── Helpers ───────────────────────────────────────────────────────
    private static System.Timers.Timer MakeTimer(int ms, Action tick)
    {
        // 不在此处 Start：调用方（StartLocked）统一启动，避免“创建即运行”的隐式行为。
        var t = new System.Timers.Timer(ms) { AutoReset = true };
        t.Elapsed += (_, _) => tick();
        return t;
    }

    public void Dispose()
    {
        _engine.StateChanged -= OnEngineStateChanged;
        _telemetry.FrameReceived -= OnTelemetryFrame;
        Stop();
        _evalTimer?.Dispose();
        _rampTimer?.Dispose();
        _evalTimer = _rampTimer = null;
    }
}
