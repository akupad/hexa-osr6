using System.Windows.Threading;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 前台窗口监视器 — 每秒轮询 GetForegroundWindow 的进程名（DispatcherTimer）。
/// 进入绑定游戏 → 自动应用「底色动作 / 底色强度」(CompanionBaseMode + CompanionBaseIntensity，
/// 有该游戏的专属配置就用专属的)，交给引擎的软启动接管（不再「停 → 起」两段式）；
/// 切回桌面/非游戏 → 缓降回中待机（EaseDown），不再当场刹住。
/// 它与「游戏伴随」（规则引擎）是同一件事的两条路：规则引擎在跑的时候它整段跳过（见 Tick 里的
/// RuleEngineActive 判断），所以这一路实际只在伴随关着时接管 —— 界面上的文案就是这么写的。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;
    private readonly FunscriptPlayerService? _funscript;
    private DispatcherTimer? _timer;
    private bool _inGame;

    /// <summary>离开游戏时缓降回中位的时长（秒）。</summary>
    private const double EaseSeconds = 0.6;

    /// <summary>是否正处于绑定游戏的前台（供测试台显示状态）。</summary>
    public bool InGame => _inGame;

    /// <summary>是否正在做过渡（引擎缓降 / 姿态混合中）。新增状态：既有属性语义不变。</summary>
    public bool Transitioning => _engine.IsEasing;

    /// <summary>最近一次进/出游戏是怎么落地的（软启动接管 / 原地平滑调整 / 缓降回中 / 立即停止）。新增诊断状态。</summary>
    public string LastTransition { get; private set; } = "";

    /// <summary>状态变化（进入/离开游戏、自动跟随开关状态），在 UI 线程触发。</summary>
    public event Action? StateChanged;

    public ForegroundWatcher(
        MotionEngine engine,
        AppSettings cfg,
        FunscriptPlayerService? funscript = null)
    {
        _engine = engine;
        _cfg = cfg;
        _funscript = funscript;
    }

    public void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Tick()
    {
        try
        {
            if (!_cfg.CompanionAuto)
            {
                if (_inGame)
                {
                    _inGame = false;
                    StateChanged?.Invoke();
                }
                return;
            }
            string target = (_cfg.CompanionProcess ?? "").Trim();
            if (target.Length == 0)
            {
                if (_inGame)
                {
                    _inGame = false;
                    StateChanged?.Invoke();
                }
                return;
            }

            // 规则引擎接管输出时不要自动切换，避免打架。
            if (_engine.RuleEngineActive) return;

            // 「动作来源 = 画面内容」时也一律让开：那条路由画面信号独占驱动，
            // 而它**刻意不置 RuleEngineActive**（置了它会被判成"别人独占接管"、永远拿不到下发权）。
            // 所以这里必须单独挡一次，否则 CompanionAuto 还勾着的话，进游戏时这边会抢先起底色动作，
            // 和画面跟随抢同一根轴 —— 表现就是设备乱抖。
            if (_cfg.RuleEngineEnabled
                && string.Equals(_cfg.CompanionSource?.Trim(), "screen", StringComparison.OrdinalIgnoreCase))
                return;

            // funscript 播放期间不抢占，避免自动切预设覆盖手动播放。
            bool manualPlayback = _funscript?.IsPlaying == true;

            string foreground = ForegroundProcess.GetName();
            bool game = string.Equals(foreground, target, StringComparison.OrdinalIgnoreCase);

            if (game)
            {
                // 仅在“进入游戏”的边沿应用一次绑定预设，避免与手动停止/归中打架。
                if (!_inGame && !manualPlayback && _engine.CanRun)
                {
                    // 这个游戏有自己的配置就用它，没有才回落到全局的「底色动作 / 底色强度」。
                    // （和规则引擎 GameCompanionRules.Apply 读的是同一套设置，两条路的覆盖规则一致。）
                    GameProfile? profile = null;
                    _cfg.GameProfiles?.TryGetValue(target, out profile);
                    string mode = !string.IsNullOrWhiteSpace(profile?.Mode) ? profile!.Mode : _cfg.CompanionBaseMode;
                    double intensity = profile is not null ? profile.Intensity : _cfg.CompanionBaseIntensity;

                    // 判定要拿「引擎当前真正在用的」模式与强度，所以先读再写。
                    string appliedMode = _engine.AutoPattern;
                    double appliedIntensity = _engine.IntensityScale;
                    string targetMode = string.IsNullOrWhiteSpace(mode) ? appliedMode : mode;
                    bool alreadyRunning = _engine.AutoRunning;
                    // 从「刚离开游戏、正在缓降」折返：这次接管会打断缓降（任何 Start* 都会）。
                    bool interruptingEase = _engine.IsEasing;

                    if (!string.IsNullOrWhiteSpace(mode)) _engine.AutoPattern = mode;
                    _engine.IntensityScale = Math.Clamp(intensity, 0.1, 2.0);

                    if (alreadyRunning)
                    {
                        // 已经在动（换了绑定预设 / 规则刚交给伴随）→ 原地平滑调整：不 StopAll、不重启。
                        NoteTransition(GameCompanionRules.CanAdjustSmoothly(appliedMode, appliedIntensity, targetMode, intensity)
                            ? "进入游戏：原地平滑调整"
                            : "进入游戏：换动作族，交给引擎交叉淡入");
                    }
                    else
                    {
                        // 用「伴随动作」启动：安静时会自动停住（见 MotionEngine.AutoTick 的静音门）。
                        // 这是一次「接管」而不是「停 → 起」两段式：StartCompanionAuto → StartAutoCore
                        // 不再发 DSTOP，只 StopAllLocked 清模式，随后由首帧长插值（离得远时 300ms）
                        // 与 ApplySoftStart 包络从「上一次实际输出位置」平滑滑进伴随动作。
                        // 调用侧刻意不预混合（BlendTo）：Start* 会打断正在进行的过渡，预混合等于白做。
                        NoteTransition(_engine.StartCompanionAuto()
                            ? (interruptingEase ? "进入游戏：打断缓降并接管" : "进入游戏：引擎软启动接管")
                            : "进入游戏：伴随动作未能启动", log: true);
                    }
                }
                if (!_inGame)
                {
                    _inGame = true;
                    StateChanged?.Invoke();
                }
            }
            else if (_inGame)
            {
                // 切回桌面/非游戏 → 自动待机（funscript 播放中不打断）。
                _inGame = false;
                if (!manualPlayback && _engine.CanRun) StandbyGracefully();
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("前台监视器切换预设失败", ex);
        }
    }

    /// <summary>
    /// 离开游戏的待机：先让引擎缓降回中位再停发（0.6s，20ms 一步，不发 DSTOP），而不是当场刹住。
    /// 缓降本身会把正在跑的模式停掉（引擎 EaseDown → StopAllLocked），所以：
    /// · 已经静止（例如被静音门停下）→ 只做一次归中，不发多余的停指令；
    /// · 调用后引擎仍在跑 → 说明缓降没有接管（引擎行为若有变化），补一次立即停，
    ///   否则会出现「已经离开游戏、设备还在动」的悬挂状态。
    /// 设备不可动 / funscript 播放中保持原逻辑（什么都不做）。
    /// </summary>
    private void StandbyGracefully()
    {
        bool moving = _engine.IsRunning;
        _engine.EaseDown(EaseSeconds);
        if (moving && _engine.IsRunning)
        {
            _engine.StopAll();
            NoteTransition("离开游戏：立即停止（缓降未接管）", log: true);
            return;
        }
        NoteTransition(moving ? $"离开游戏：缓降回中 {EaseSeconds:0.0} 秒" : "离开游戏：已静止");
    }

    /// <summary>记录最近一次过渡方式；文本变化时才写日志，避免每秒刷屏。</summary>
    private void NoteTransition(string text, bool log = false)
    {
        bool changed = !string.Equals(LastTransition, text, StringComparison.Ordinal);
        LastTransition = text;
        if (changed && log) AppLogger.Info($"自动跟随过渡：{text}");
    }

    public void Dispose() => Stop();
}
