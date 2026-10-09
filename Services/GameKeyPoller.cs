using System.Runtime.InteropServices;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 动作键联动 — 后台线程轮询 GetAsyncKeyState 检测 W/A/S/D、方向键、空格等高频动作键，
/// 按下瞬间（边沿触发）对 MotionEngine 施加一次轻量强度脉冲。仅当 ActionLinkEnabled
/// 且设备可动时生效，不干扰急停/归中。
///
/// 规则引擎接管<b>不</b>是拒绝条件：游戏伴随接管时用户的按键恰恰是它该跟着反应的东西之一
/// （伴随是背景层，见 MotionEngine.EffectiveIntensity 的说明）。真正该拒绝的只有
/// 「被遥测 / 用户自定义规则独占」这一种，那个判断由引擎侧的 TriggerActionPulse 自己把关 ——
/// 放在这里等于把伴随一起挡掉了，TestLab 上「操作带动机器」就变成一个开了没反应的死开关。
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
                bool action = false;
                for (int i = 0; i < TrackingKeys.Length; i++)
                {
                    bool down = (GetAsyncKeyState(TrackingKeys[i]) & 0x8000) != 0;
                    if (down && !_prevDown[i]) action = true;   // 边沿触发，避免按住的持续增强
                    _prevDown[i] = down;
                }

                // 是否放行交给引擎判断（见类注释）：这里只看「按键边沿 + 开关 + 设备可动」。
                if (action && _cfg.ActionLinkEnabled && _engine.CanRun)
                    _engine.TriggerActionPulse();
            }
            catch
            {
                // 轮询容错：单个周期失败不中断
            }
            Thread.Sleep(60);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
