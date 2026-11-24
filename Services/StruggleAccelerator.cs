namespace Hexa.Services;

/// <summary>
/// 可选「挣扎连点」：只在绑定的游戏处于前台时，统计真实的快速按下边沿（按下-松开才算一个新的点），
/// 同一个键连点或不同键交替都算。系统长按重复、全局热键、切窗打字、切回时已经按住的键都不算。
/// 它只给「当前动作」加节奏和深度，不换招；漏一拍有宽容期，结束平滑回落。
/// </summary>
public sealed class StruggleAccelerator
{
    /// <summary>两次按下至少隔这么久才算「另一次」——防键盘抖动/系统连发。</summary>
    public const double MinTapGapSeconds = 0.035;
    /// <summary>漏一拍宽容：间隔到「允许间隔 × 这个系数」之前都不算断。</summary>
    public const double MissToleranceFactor = 2.2;
    /// <summary>多久没有新的按下就开始平滑回落。</summary>
    public const double DecayDelaySeconds = 0.55;

    private readonly HashSet<int> _held = new();
    private readonly List<double> _taps = new(8);
    private double _lastTapSeconds = double.NegativeInfinity;
    private double _level;
    private bool _foreground;
    private bool _armed;

    public bool Enabled { get; set; }
    /// <summary>每游戏可调的灵敏度：high = 更容易触发（允许的间隔更大、需要的连点更少）。</summary>
    public string Sensitivity { get; set; } = "standard";
    /// <summary>反馈强度（独立于灵敏度）：乘在最终加成上，0 = 只有节奏没有加深。</summary>
    public double Feedback { get; set; } = 1.0;

    public double Level => Math.Clamp(_level, 0, 1);
    public int TapCount => _taps.Count;
    /// <summary>节奏加成：最多 +55%。</summary>
    public double SpeedScale => 1.0 + 0.55 * Level * Math.Clamp(Feedback, 0, 1.5);
    /// <summary>深度加成：最多 +0.35 强度（再被舒适档夹住）。</summary>
    public double DepthBoost => 0.35 * Level * Math.Clamp(Feedback, 0, 1.5);

    public void Reset()
    {
        _held.Clear();
        _taps.Clear();
        _lastTapSeconds = double.NegativeInfinity;
        _level = 0;
        _armed = false;
    }

    /// <summary>前台切换。切回前台时先把「此刻还按着的键」记下来，它们的下一次边沿不算新点。</summary>
    public void SetForeground(bool foreground, IEnumerable<int>? downKeys = null)
    {
        if (foreground == _foreground) return;
        _foreground = foreground;
        _taps.Clear();
        _held.Clear();
        if (foreground)
        {
            _armed = true;
            if (downKeys is not null)
                foreach (int key in downKeys) _held.Add(key);   // 切回时已按住的键：等松开再按才算
        }
        else
        {
            _armed = false;
            _level = Math.Max(0, _level - 0.4);
        }
    }

    /// <summary>
    /// 一个按键边沿。返回 true 表示这一点被计入了连点。
    /// injected = 全局热键 / 模拟输入，一律不计。
    /// </summary>
    public bool OnKeyEdge(int virtualKey, bool down, double nowSeconds, bool foreground, bool injected)
    {
        if (!Enabled || injected) return false;
        SetForeground(foreground);
        if (!foreground || !_armed) return false;

        if (!down)
        {
            _held.Remove(virtualKey);
            return false;
        }
        if (!_held.Add(virtualKey)) return false;          // 还按着（系统重复）不算新点

        double gap = nowSeconds - _lastTapSeconds;
        if (gap < MinTapGapSeconds) return false;

        double allowed = AllowedGapSeconds();
        if (_taps.Count > 0 && gap > allowed * MissToleranceFactor) _taps.Clear();   // 断了就重新起算，但等级平滑回落
        _taps.Add(nowSeconds);
        if (_taps.Count > 8) _taps.RemoveAt(0);
        _lastTapSeconds = nowSeconds;
        return true;
    }

    /// <summary>推进等级：连点期间快速上升，停下后延迟一会儿平滑回落（不突然断）。</summary>
    public double Advance(double nowSeconds)
    {
        if (!Enabled) { _level = 0; return 0; }
        double sinceLast = nowSeconds - _lastTapSeconds;
        int needed = RequiredTaps();
        int recent = CountRecent(nowSeconds);
        if (_foreground && recent >= needed && sinceLast <= AllowedGapSeconds() * MissToleranceFactor)
        {
            double target = Math.Clamp(0.35 + (recent - needed + 1) * 0.18, 0, 1);
            _level = Math.Min(target, _level + Math.Max(0.9, target - _level) * 0.35);
        }
        else if (sinceLast > DecayDelaySeconds)
        {
            _level = Math.Max(0, _level - 0.9 * Math.Min(0.25, sinceLast - DecayDelaySeconds));
        }
        return Level;
    }

    /// <summary>窗口内有效连点数（使用灵敏度决定的窗口）。</summary>
    public int CountRecent(double nowSeconds)
    {
        double window = AllowedGapSeconds() * MissToleranceFactor * RequiredTaps();
        int count = 0;
        for (int i = _taps.Count - 1; i >= 0; i--)
        {
            if (nowSeconds - _taps[i] <= window) count++;
            else break;
        }
        return count;
    }

    public double AllowedGapSeconds() => Sensitivity switch
    {
        "high" => 0.40,
        "low" => 0.20,
        _ => 0.30,
    };

    public int RequiredTaps() => Sensitivity switch
    {
        "high" => 2,
        "low" => 4,
        _ => 3,
    };
}
