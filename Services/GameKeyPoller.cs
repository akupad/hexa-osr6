using System.Runtime.InteropServices;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 动作键轮询 — 后台线程用 GetAsyncKeyState 的低位（"自上次调用以来按下过"）抓真实按下边沿。
///
/// 普通按键的「轻量强度脉冲」已经摘掉了（需求 ⑤）：按键不再直接改强度，而是喂给
/// <see cref="StruggleAccelerator"/>。它只在「绑定的游戏处于前台」时统计快速连点（按下-松开才算一个新点，
/// 同键/不同键都算），系统长按重复（还按着）、全局热键（注入/非前台）、切窗打字都不计；
/// 不读取、不保存任何字符。默认关闭，按游戏可调灵敏度与反馈强度。
/// </summary>
public sealed class GameKeyPoller : IDisposable
{
    private const int VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44;
    private const int VK_UP = 0x26, VK_DOWN = 0x28, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_SPACE = 0x20;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static readonly int[] TrackingKeys =
        { VK_W, VK_A, VK_S, VK_D, VK_UP, VK_DOWN, VK_LEFT, VK_RIGHT, VK_SPACE };

    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;
    private readonly bool[] _prevDown = new bool[TrackingKeys.Length];
    private (bool Enabled, string Sensitivity, double Feedback, string Process) _struggleConfig;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private bool _disposed;

    public GameKeyPoller(MotionEngine engine, AppSettings cfg)
    {
        _engine = engine;
        _cfg = cfg;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_thread != null || _disposed) return;
            // 重置按键状态，避免重启后把“按住中”误判成新的边沿触发。
            Array.Clear(_prevDown);
            _cts = new CancellationTokenSource();
            _thread = new Thread(() => Loop(_cts.Token))
            {
                IsBackground = true,
                Name = "HexaGameKeyPoller",
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _thread = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                string process = (_cfg.CompanionProcess ?? "").Trim();
                string foreground = ForegroundProcess.GetName();
                bool inBoundGame = process.Length > 0
                    && string.Equals(process, foreground, StringComparison.OrdinalIgnoreCase);
                SyncStruggleConfig(process);

                double now = Environment.TickCount64 / 1000.0;
                List<int>? held = null;
                // 挣扎没开就不用 15ms 抖动：降频到 40ms，但前台判定照跑（切进切出立刻生效）。
                // 同时照旧同步「此刻按着哪些键」，避免刚打开挣扎时把已经按住的键当成新点。
                if (!_struggleConfig.Enabled)
                {
                    List<int>? stillDown = null;
                    for (int i = 0; i < TrackingKeys.Length; i++)
                    {
                        bool down = (GetAsyncKeyState(TrackingKeys[i]) & 0x8000) != 0;
                        _prevDown[i] = down;
                        if (down) (stillDown ??= new List<int>(TrackingKeys.Length)).Add(TrackingKeys[i]);
                    }
                    _engine.SetStruggleForeground(inBoundGame, stillDown);
                    Thread.Sleep(40);
                    continue;
                }

                for (int i = 0; i < TrackingKeys.Length; i++)
                {
                    short state = GetAsyncKeyState(TrackingKeys[i]);
                    bool down = (state & 0x8000) != 0;
                    // 低位 = 自上次调用以来按下过：60ms 轮询也能抓到一瞬即放的快速点按。
                    bool pressed = (state & 0x0001) != 0 || (down && !_prevDown[i]);
                    _prevDown[i] = down;
                    if (down) (held ??= new List<int>(TrackingKeys.Length)).Add(TrackingKeys[i]);
                    if (pressed) _engine.FeedStruggleKey(TrackingKeys[i], true, now, inBoundGame, injected: false);
                    else if (!down) _engine.FeedStruggleKey(TrackingKeys[i], false, now, inBoundGame, injected: false);
                }
                // 切回绑定游戏时，此刻还按着的键不算新点（要松开再按才算）。
                _engine.SetStruggleForeground(inBoundGame, held);
            }
            catch
            {
                // 轮询容错：单个周期失败不中断
            }
            Thread.Sleep(15);
        }
    }

    /// <summary>按「当前游戏」刷新挣扎参数：GameProfile 覆盖全局，切换游戏时立即生效。</summary>
    private void SyncStruggleConfig(string process)
    {
        GameProfile? profile = null;
        if (process.Length > 0) _cfg.GameProfiles.TryGetValue(process, out profile);
        var (enabled, sensitivity, feedback) = CompanionPlayPolicy.ResolveGameStruggle(
            profile, _cfg.CompanionStruggleEnabled, _cfg.CompanionStruggleSensitivity, _cfg.CompanionStruggleFeedback);
        var next = (enabled, sensitivity, feedback, process);
        if (next == _struggleConfig) return;
        _struggleConfig = next;
        _engine.ConfigureStruggle(enabled, sensitivity, feedback);
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
