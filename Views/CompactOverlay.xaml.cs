using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Hexa.Controls;
using Hexa.Services;

namespace Hexa.Views;

/// <summary>
/// 桌面悬浮小窗（置顶、不抢焦点）：一眼看到「连接状态 + 现在在跑什么 + BPM / 强度 / 速度」，
/// 点右侧 ▾ 还能就地改行程 / 扭转 / 强度 / 速度、套快速预设，不用切回主窗口。
/// 按住窗口任意空白处可拖动；✕ 只是收起来，托盘菜单或设置页都能找回来。
/// 不透明度只作用在底板刷上（文字永远不透明），半透明时再给内容叠一层深色描边，压在亮画面上也读得清。
/// <para>
/// 窗口带 WS_EX_NOACTIVATE：点它只处理鼠标消息，<b>不会把窗口激活</b> —— 全屏游戏里点急停、
/// 拖行程条都不会把自己切出去。再加 WS_EX_TRANSPARENT 就是完全点不到（点击穿透，靠托盘菜单撤销）。
/// </para>
/// </summary>
public partial class CompactOverlay : Window
{
    private readonly DispatcherTimer _timer;

    // 拖拽期间先挂起尺寸自适应，避免与 DragMove 抢位置
    private bool _dragging;
    private bool _fitPending;

    // 上一次已应用的悬浮窗设置（用于检测设置页改动）
    private bool _settingsCached;
    private double _opacity = 0.95;
    private bool _cachedConn, _cachedMode, _cachedSpeed, _cachedIntensity, _cachedBpm;

    // 鼠标停在窗口上时把底板提亮：半透明看不清数值时，移上去就能读
    private bool _hover;

    // 出事（未连接 / 急停锁定）时状态点做 2Hz 呼吸，扫一眼就知道不对
    private bool _blinkOn = true;

    // 「急停 / 解除急停」要连点两次：第一次只是预备，4 秒内不点就自动取消
    private bool _stopArmed;
    private DateTime _stopArmedAtUtc;

    // 画面没变化就整帧跳过，不必每 250ms 白刷一遍
    private string _lastFrame = "";

    // 底板刷只在输入（不透明度 / 底色档位 / 悬停）变化时重建
    private string _backdropKey = "";
    private int _tintLevel;

    // 「小窗已隐藏」的提示每个运行周期只说一次
    private bool _hideHintShown;

    // ── 快捷调节面板 ────────────────────────────────────────────────
    private bool _panelOpen;                 // 展开状态（存设置，下次启动照旧）
    private bool _panelWired;                // 滑杆事件只订阅一次
    private bool _cachedPanel;               // 上次应用的面板状态（变了自己重新量尺寸）
    private DateTime _panelPreviewUtc = DateTime.MinValue;   // 拖动预览节流（30ms 一把）
    private DispatcherTimer? _panelSaveTimer;                // 停手 0.8 秒再写盘

    // ── Win32 窗口样式 ─────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x0000_0020;   // 鼠标穿透（点到下面的窗口去）
    private const int WS_EX_NOACTIVATE = 0x0800_0000;    // 永不激活（不抢游戏焦点）

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int nValue);

    /// <summary>当前是不是「点击穿透」状态（托盘菜单勾选状态读它）。</summary>
    public bool ClickThrough { get; private set; }

    // ── 配色全部取自 App.xaml（不自创颜色），首次用到时缓存 ──
    private bool _paletteReady;
    private Brush _brFaint = Brushes.Gray;
    private Brush _brPrimary = Brushes.MediumSeaGreen, _brAccent = Brushes.CornflowerBlue;
    private Brush _brWarning = Brushes.Goldenrod, _brDanger = Brushes.IndianRed;
    private Brush _brSuccess = Brushes.MediumSpringGreen, _brBorderStrong = Brushes.Gray;
    private Color _colBg = Color.FromRgb(0x0E, 0x11, 0x14);
    private Color _colBgRaised = Color.FromRgb(0x12, 0x16, 0x1A);
    private Color _colDanger = Color.FromRgb(0xE5, 0x53, 0x53);

    /// <summary>文字描边：半透明底板压在亮画面上时，靠它保住可读性。</summary>
    private static readonly DropShadowEffect ContentShadow = CreateContentShadow();

    private static DropShadowEffect CreateContentShadow()
    {
        var fx = new DropShadowEffect
        {
            Color = Colors.Black,
            ShadowDepth = 0,
            BlurRadius = 4,
            Opacity = 0.9
        };
        fx.Freeze();
        return fx;
    }

    // 供 App 更新 BPM 显示（由 PlaygroundPage 的 Tap Tempo 写入）
    public static string BpmLabel { get; set; } = "";

    public CompactOverlay()
    {
        InitializeComponent();

        EnsurePalette();

        // 先按设置确定显示项与窗口尺寸，再定位
        ApplySettings();

        // 窗口加载完成后再校准一次尺寸（此时字体与模板已就绪）
        Loaded += (_, _) => FitToContent();

        // 句柄出来后才能改扩展样式：不抢焦点 + 把面板内容接上
        SourceInitialized += (_, _) =>
        {
            ApplyWindowStyle();
            InitPanel();
        };

        // 刚显示出来时立刻按最新状态重画（隐藏期间不刷新，省掉无意义的定时器工作）
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _lastFrame = "";
            Refresh();
        };

        // 恢复上次位置，首次定位到主屏右上角
        var s = App.Settings;
        if (double.IsFinite(s.OverlayLeft) && double.IsFinite(s.OverlayTop)
            && s.OverlayLeft >= 0 && s.OverlayTop >= 0)
        {
            Left = s.OverlayLeft;
            Top  = s.OverlayTop;
            ClampToScreen(allowPartial: false);   // 换过显示器 / 改过分辨率也能自己回来
        }
        else
        {
            Left = SystemParameters.PrimaryScreenWidth - Width - 20;
            Top  = 20;
        }

        // 250ms：状态变化看得更及时，也够状态点做 2Hz 呼吸
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    // ══════════════════════════════════════════════════════════════
    //  设置
    // ══════════════════════════════════════════════════════════════

    /// <summary>按 App.Settings 应用底板透明度与显示项，并重新自适应窗口尺寸。</summary>
    public void ApplySettings()
    {
        EnsurePalette();
        var s = App.Settings;

        // 面板展开状态也要跟着设置走（启动恢复 / 设置页刷新都会走到这里）
        _panelOpen = s.OverlayPanelOpen;
        ApplyPanelVisibility();

        double opacity = CurrentOpacity();
        if (!_settingsCached || !opacity.Equals(_opacity))
        {
            _opacity = opacity;
            _backdropKey = "";      // 不透明度变了：下一帧重建底板刷
        }

        // 显示项是否变化决定要不要重新测量尺寸（拖动不透明度滑块时不重复测量）
        bool layoutChanged = !_settingsCached
            || s.OverlayShowConnection != _cachedConn
            || s.OverlayShowMode       != _cachedMode
            || s.OverlayShowSpeed      != _cachedSpeed
            || s.OverlayShowIntensity  != _cachedIntensity
            || s.OverlayShowBpm        != _cachedBpm
            || s.OverlayPanelOpen      != _cachedPanel;

        // 显示项开关；BPM 没有数值时同样收起，避免留下孤立的分隔符
        CellConn.Visibility      = s.OverlayShowConnection ? Visibility.Visible : Visibility.Collapsed;
        CellMode.Visibility      = s.OverlayShowMode ? Visibility.Visible : Visibility.Collapsed;
        CellSpeed.Visibility     = s.OverlayShowSpeed ? Visibility.Visible : Visibility.Collapsed;
        CellIntensity.Visibility = s.OverlayShowIntensity ? Visibility.Visible : Visibility.Collapsed;
        CellBpm.Visibility       = s.OverlayShowBpm && ExtractBpm(BpmLabel).Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateSeparators();
        UpdateValueRowVisibility();

        if (layoutChanged) FitToContent();

        _cachedConn      = s.OverlayShowConnection;
        _cachedMode      = s.OverlayShowMode;
        _cachedSpeed     = s.OverlayShowSpeed;
        _cachedIntensity = s.OverlayShowIntensity;
        _cachedBpm       = s.OverlayShowBpm;
        _cachedPanel     = s.OverlayPanelOpen;
        _settingsCached  = true;

        ApplyBackdrop();
    }

    private static double CurrentOpacity()
    {
        double v = App.Settings.OverlayOpacity;
        return double.IsFinite(v) ? Math.Clamp(v, 0.2, 1.0) : 0.95;
    }

    /// <summary>设置页改动后无需外部通知即可生效（由 250ms 定时器检测）。</summary>
    private bool SettingsChanged()
    {
        if (!_settingsCached) return true;
        var s = App.Settings;
        return !CurrentOpacity().Equals(_opacity)
            || s.OverlayShowConnection != _cachedConn
            || s.OverlayShowMode       != _cachedMode
            || s.OverlayShowSpeed      != _cachedSpeed
            || s.OverlayShowIntensity  != _cachedIntensity
            || s.OverlayShowBpm        != _cachedBpm
            || s.OverlayPanelOpen      != _cachedPanel;
    }

    /// <summary>每个可见行里，第一个可见项之前不显示分隔符（隐藏项不会留下孤立的 " │ "）。</summary>
    private void UpdateSeparators()
    {
        // 第一行：连接状态没有分隔符，所以它是否可见直接决定模式前面要不要竖线
        bool hasPrev = CellConn.Visibility == Visibility.Visible;
        UpdateSeparator(SepMode, CellMode, ref hasPrev);

        // 第二行：BPM → 强度 → 速度
        hasPrev = false;
        UpdateSeparator(SepBpm, CellBpm, ref hasPrev);
        UpdateSeparator(SepIntensity, CellIntensity, ref hasPrev);
        UpdateSeparator(SepSpeed, CellSpeed, ref hasPrev);
    }

    private static void UpdateSeparator(TextBlock sep, Border cell, ref bool hasPrev)
    {
        if (cell.Visibility != Visibility.Visible)
        {
            sep.Visibility = Visibility.Collapsed;
            return;
        }
        sep.Visibility = hasPrev ? Visibility.Visible : Visibility.Collapsed;
        hasPrev = true;
    }

    /// <summary>数值那一行整行没内容时收起来，窗口就只剩一行，不留空带。</summary>
    private void UpdateValueRowVisibility()
    {
        bool any = CellBpm.Visibility == Visibility.Visible
                || CellIntensity.Visibility == Visibility.Visible
                || CellSpeed.Visibility == Visibility.Visible;
        ValueRow.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    // ══════════════════════════════════════════════════════════════
    //  每帧刷新
    // ══════════════════════════════════════════════════════════════

    private void Refresh()
    {
        // 隐藏时不做无意义的刷新；重新显示时 IsVisibleChanged 会补一次
        if (!IsVisible) return;

        // 设置页改动后自动重建布局（透明度 / 显示项 / 尺寸）
        if (SettingsChanged()) ApplySettings();

        var s = App.Settings;
        var e = App.Engine;
        var serial = App.Serial;

        _blinkOn = !_blinkOn;
        BlinkStopArmIfTimedOut();

        // ── 连接状态 ──
        bool sim      = serial.IsSimulation;
        bool online   = serial.IsOpen;
        bool estopped = e.EmergencyStopped;
        bool homing   = e.IsHoming;
        // 统一口径：脚本播放也算"在动"。以前这里只看 IsRunning，而脚本走的是直接下发路径、
        // 从不置 IsRunning —— 于是脚本真正驱动设备时急停反而是灰的（还写着"不用停"）。
        bool moving   = e.DeviceIsMoving;

        Brush dotBrush  = sim ? _brAccent : online ? _brSuccess : _brDanger;
        string connWord = sim ? "模拟设备" : online ? "已连接" : "未连接";

        // ── 现在在跑什么（白话，不用 IDLE/AUTO 这类英文模式名）──
        Brush modeBrush;
        string modeWord;
        if (estopped)           { modeBrush = _brDanger;  modeWord = "已急停锁定"; }
        else if (homing)        { modeBrush = _brWarning; modeWord = "正在归中"; }
        else if (e.IsPaused)    { modeBrush = _brWarning; modeWord = "已暂停"; }
        else if (moving && e.TeasingMode) { modeBrush = _brPrimary; modeWord = "挑逗循环中"; }
        else if (moving)
        {
            modeBrush = _brPrimary;
            // 用统一口径的 DriverLabel：脚本播放 / 游戏桥 / 声音响应 / 手柄 / 编排预览都不设 ActiveMode，
            // 以前这里会把它们一律显示成"自动播放中"，而侧栏同时写着"脚本播放"——同一事实两个答案。
            modeWord = e.DriverLabel;
        }
        else { modeBrush = _brFaint; modeWord = "待机"; }

        string bpm      = ExtractBpm(BpmLabel);
        bool   showBpm  = s.OverlayShowBpm && bpm.Length > 0;
        bool   blink    = !online || estopped;
        int    intens   = (int)Math.Round(e.IntensityScale * 100);
        string why      = ExplainIdle(online, estopped, sim);

        // 一整帧的内容；和上一帧完全一样就直接收工（闪烁除外，它每帧都要翻）
        string frame =
            $"{connWord}|{modeWord}|{(showBpm ? bpm : "-")}|{intens}|{e.Speed:F1}|" +
            $"{(blink ? (_blinkOn ? "1" : "0") : "1")}|{(_hover ? "h" : "-")}|{(_stopArmed ? "a" : "-")}|" +
            $"{(estopped ? "E" : online ? "O" : "D")}|{(e.IsPaused ? "P" : "-")}|{why}";
        if (frame == _lastFrame) return;
        _lastFrame = frame;

        // 连接：点 + 文字同色，一眼看得出是绿的、蓝的还是红的
        StatusDot.Fill = dotBrush;
        StatusDot.Opacity = blink && !_blinkOn ? 0.25 : 1.0;
        ConnText.Text = connWord;
        ConnText.Foreground = dotBrush;

        // 运行状态：待机是灰的，跑起来变绿，归中是黄，急停是红
        ModeText.Text = modeWord;
        ModeText.Foreground = modeBrush;

        if (s.OverlayShowSpeed)     SpeedText.Text  = $"{e.Speed:F1}×";
        if (s.OverlayShowIntensity) IntensText.Text = $"{intens}%";
        if (s.OverlayShowBpm)       BpmText.Text    = bpm;

        // BPM 由空变非空（或反之）时，重新决定该格与分隔符的显示
        var bpmVisibility = showBpm ? Visibility.Visible : Visibility.Collapsed;
        if (CellBpm.Visibility != bpmVisibility)
        {
            CellBpm.Visibility = bpmVisibility;
            UpdateSeparators();
            UpdateValueRowVisibility();
            FitToContent();
        }

        UpdateStopButton(online, estopped, moving);
        UpdatePauseButton(online, estopped, e);
        UpdateWhyText(why);

        // 底色档位：急停整窗变红；未连接也提示（关掉「显示连接状态」就不再提醒，尊重设置）
        _tintLevel = estopped ? 2 : (s.OverlayShowConnection && !online ? 1 : 0);
        ApplyBackdrop();

        // 内容被撑大时（数值位数变多）才扩窗，不会来回抖
        FitIfNeeded();
    }

    // ══════════════════════════════════════════════════════════════
    //  急停 / 解除急停按钮
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 暂停/继续：暂停只是停住不动（不锁输出、不回中），点继续从原位接着走。
    /// 没在动的时候按不了 —— 那里点了也不会有什么发生，灰掉比让人猜更好。
    /// </summary>
    private void UpdatePauseButton(bool online, bool estopped, MotionEngine engine)
    {
        if (PauseBtn is null) return;
        bool paused = engine.IsPaused;
        PauseBtn.IsEnabled = online && !estopped && (paused || engine.DeviceIsMoving);
        PauseBtn.Content = paused ? "▶" : "⏸";
        PauseBtn.Tag = paused ? "resume" : "pause";
        PauseBtn.ToolTip = !online
            ? "设备没连上"
            : estopped
                ? "急停锁着，先解除急停"
                : paused
                    ? "继续：从停下的那个位置接着走"
                    : engine.DeviceIsMoving
                        ? "暂停：就地停住不动（不会回中、不会锁输出），再点一下就接着走"
                        : "现在没有东西在动";
    }

    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelStopArm();
        if (!App.Engine.TogglePause())
            AppLogger.Info("暂停没生效：现在没有在跑的动作");
        _lastFrame = "";
        Refresh();
    }

    /// <summary>
    /// 急停按钮：只有「设备连着」且「真有东西可停 / 正处在急停锁定」时才可按。
    /// 按第一下只是预备（按钮变成红色「确认急停 / 确认归中」），4 秒内再按一下才真的执行。
    /// </summary>
    private void UpdateStopButton(bool online, bool estopped, bool moving)
    {
        bool actionable = online && (estopped || moving);
        StopBtn.IsEnabled = actionable;

        string verb = estopped ? "解除急停" : "急停";
        if (_stopArmed && actionable && !estopped) verb = "确认急停";
        StopBtn.Content = verb;
        StopBtn.Tag = _stopArmed ? "armed" : "stop";

        string hotkey = "";
        try
        {
            if (App.Settings.GlobalHotkeysEnabled)
                hotkey = App.Settings.Hotkeys.Get(Hexa.Models.HotkeyConfig.EmergencyStop) ?? "";
        }
        catch { /* 热键读不到不影响按钮本身 */ }

        StopBtn.ToolTip = !online
            ? "设备没连上，先到「设置」页连上设备"
            : estopped
                ? "解除急停锁定：设备会重新通电、慢慢回到正中间（会先弹确认框）"
                : !moving
                    ? "现在没有东西在动，不用停"
                    : hotkey.Length > 0
                        ? $"立刻停下并锁住设备输出（要连点两次，防误触）；也可以直接按 {hotkey}"
                        : "立刻停下并锁住设备输出（要连点两次，防误触）";
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        var engine = App.Engine;
        bool estopped = engine.EmergencyStopped;

        // 解除急停会让设备重新通电并动起来，必须和主窗口「全部归中」用同一条路、同一个确认框。
        // 悬浮窗是置顶小窗，光靠"4 秒内点两下"不够 —— 双击就可能误触，而主窗口那条入口是弹框确认。
        if (estopped)
        {
            _stopArmed = false;
            if (System.Windows.MessageBox.Show(
                    "设备处于急停锁定，归中会解除急停并重新使能输出，是否继续？",
                    "解除急停", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                _lastFrame = "";
                Refresh();
                return;
            }
            _lastFrame = "";
            engine.Home();
            AppLogger.Info("悬浮窗：已解除急停并归中");
            Refresh();
            return;
        }

        if (!_stopArmed)
        {
            // 第一次点击只是「预备」：不给设备下任何指令
            _stopArmed = true;
            _stopArmedAtUtc = DateTime.UtcNow;
            _lastFrame = "";
            Refresh();
            return;
        }

        // 第二次点击：真的执行急停
        _stopArmed = false;
        _lastFrame = "";
        engine.EmergencyStop();
        AppLogger.Warn("悬浮窗：已急停（全部任务取消，输出锁定）");
        Refresh();
    }

    private void BlinkStopArmIfTimedOut()
    {
        if (_stopArmed && (DateTime.UtcNow - _stopArmedAtUtc).TotalSeconds > 4) CancelStopArm();
    }

    private void CancelStopArm()
    {
        if (!_stopArmed) return;
        _stopArmed = false;
        _lastFrame = "";
    }

    // ══════════════════════════════════════════════════════════════
    //  为什么不动
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 只在真有问题时给一句话。以前小窗只报「待机 / 已连接」，设备不动的时候看不出区别——
    /// 而这几种不动的原因，处置办法完全不同，所以按「最致命」排优先级，只说第一条。
    /// </summary>
    private static string ExplainIdle(bool online, bool estopped, bool sim)
    {
        if (estopped) return "急停锁着：点右上「解除急停」才会再动";
        if (!online) return sim ? "" : "串口没连上：到「设置」页选通道连上设备";

        var serial = App.Serial;
        if (serial is null) return "";
        if (!serial.OutputEnabled)
            return $"输出锁着，已经丢了 {serial.DroppedWhileLocked} 条指令：点侧栏「全部归中」解锁";
        if (serial.UserAdvice is { Length: > 0 } advice) return advice;
        return "";
    }

    /// <summary>原因行露出来会把窗口撑高，藏起来要跟着缩回去。</summary>
    private void UpdateWhyText(string why)
    {
        if (WhyText is null) return;
        bool want = why.Length > 0;
        if (WhyText.Text != why) WhyText.Text = why;

        var visibility = want ? Visibility.Visible : Visibility.Collapsed;
        if (WhyText.Visibility == visibility) return;

        WhyText.Visibility = visibility;
        FitToContent();     // 撑高 / 缩回都要重新量：只 FitIfNeeded 的话收不回去
    }

    // ══════════════════════════════════════════════════════════════
    //  快捷调节面板（行程 / 扭转 / 强度 / 速度 / 预设）
    // ══════════════════════════════════════════════════════════════

    /// <summary>把滑杆接到当前的引擎与设置值上（只做一次）。</summary>
    private void InitPanel()
    {
        if (_panelWired || PanelRow is null) return;
        _panelWired = true;

        SyncPanelFromState();

        PanelStrokeRange.LowerValueChanged += (_, _) => ApplyPanelStroke(top: true);
        PanelStrokeRange.UpperValueChanged += (_, _) => ApplyPanelStroke(top: false);
        PanelRotateRange.LowerValueChanged += (_, _) => ApplyPanelRotate(top: true);
        PanelRotateRange.UpperValueChanged += (_, _) => ApplyPanelRotate(top: false);
        PanelIntensitySlider.ValueChanged    += (_, e) => ApplyPanelIntensity(e.NewValue);
        PanelSpeedSlider.ValueChanged        += (_, e) => ApplyPanelSpeed(e.NewValue);

        ApplyPanelVisibility();
    }

    /// <summary>外部（设置页 / 预设 / 游戏）改了参数后，把滑杆拨到同一格，别让小窗显示过期的值。</summary>
    private void SyncPanelFromState()
    {
        if (PanelRow is null) return;

        PanelStrokeRange.LowerValue = App.Lovense?.StrokeTopPercent  ?? App.Settings.LovenseStrokeTopPercent;
        PanelStrokeRange.UpperValue = App.Lovense?.StrokeDeepPercent ?? App.Settings.LovenseStrokeDeepPercent;
        PanelRotateRange.LowerValue = App.Lovense?.RotateTopPercent  ?? App.Settings.LovenseRotateTopPercent;
        PanelRotateRange.UpperValue = App.Lovense?.RotateDeepPercent ?? App.Settings.LovenseRotateDeepPercent;

        double intensity = Math.Clamp(App.Engine.IntensityScale, PanelIntensitySlider.Minimum, PanelIntensitySlider.Maximum);
        if (Math.Abs(PanelIntensitySlider.Value - intensity) > 0.001) PanelIntensitySlider.Value = intensity;

        double speed = Math.Clamp(App.Engine.Speed, PanelSpeedSlider.Minimum, PanelSpeedSlider.Maximum);
        if (Math.Abs(PanelSpeedSlider.Value - speed) > 0.001) PanelSpeedSlider.Value = speed;

        UpdatePanelLabels();
    }

    private void ApplyPanelStroke(bool top)
    {
        if (!_panelWired) return;

        // 两个圆点也可能被拖成反的，StrokeWindow 会替我们对调并保证至少 10% 的行程
        (double windowTop, double windowDeep) = LovenseProtocol.StrokeWindow(
            (int)Math.Round(PanelStrokeRange.LowerValue),
            (int)Math.Round(PanelStrokeRange.UpperValue));
        int topPercent = (int)Math.Round(windowTop);
        int deepPercent = (int)Math.Round(windowDeep);

        if (App.Lovense is not null)
        {
            App.Lovense.StrokeTopPercent = topPercent;
            App.Lovense.StrokeDeepPercent = deepPercent;
        }
        App.Settings.LovenseStrokeTopPercent = topPercent;
        App.Settings.LovenseStrokeDeepPercent = deepPercent;

        UpdatePanelLabels();
        // 拖哪个圆点就把机器送到哪一格：左边＝最高、右边＝最深，手上拉到哪儿机器就走到哪儿
        PreviewPanelAxis(0, top ? topPercent : deepPercent);
        QueuePanelSave();
    }

    private void ApplyPanelRotate(bool top)
    {
        if (!_panelWired) return;

        (double windowTop, double windowDeep) = LovenseProtocol.StrokeWindow(
            (int)Math.Round(PanelRotateRange.LowerValue),
            (int)Math.Round(PanelRotateRange.UpperValue));
        int topPercent = (int)Math.Round(windowTop);
        int deepPercent = (int)Math.Round(windowDeep);

        if (App.Lovense is not null)
        {
            App.Lovense.RotateTopPercent = topPercent;
            App.Lovense.RotateDeepPercent = deepPercent;
        }
        App.Settings.LovenseRotateTopPercent = topPercent;
        App.Settings.LovenseRotateDeepPercent = deepPercent;

        UpdatePanelLabels();
        PreviewPanelAxis(3, top ? topPercent : deepPercent);
        QueuePanelSave();
    }

    private void ApplyPanelIntensity(double value)
    {
        if (!_panelWired) return;
        App.Engine.IntensityScale = value;
        App.Settings.IntensityScale = value;
        UpdatePanelLabels();
        QueuePanelSave();
    }

    private void ApplyPanelSpeed(double value)
    {
        if (!_panelWired) return;
        App.Engine.Speed = value;
        App.Settings.Speed = value;
        UpdatePanelLabels();
        QueuePanelSave();
    }

    private void UpdatePanelLabels()
    {
        if (PanelStrokeText is null) return;
        PanelStrokeText.Text   = $"{(int)Math.Round(PanelStrokeRange.LowerValue)}→{(int)Math.Round(PanelStrokeRange.UpperValue)}";
        PanelRotateText.Text   = $"{(int)Math.Round(PanelRotateRange.LowerValue)}→{(int)Math.Round(PanelRotateRange.UpperValue)}";
        PanelIntensityText.Text = $"{App.Engine.IntensityScale:P0}";
        PanelSpeedText.Text     = $"{App.Engine.Speed:F1}×";
    }

    /// <summary>
    /// 「看着机器走到我拉的那一格」：拖动时立刻把那一根轴送过去，其余保持 50（中位）。
    /// 30ms 一把 —— 拖动事件非常密，逐帧发会把串口写满。
    /// </summary>
    private void PreviewPanelAxis(int axis, double percent)
    {
        if (App.Engine is null || axis is < 0 or > 5) return;

        DateTime now = DateTime.UtcNow;
        if ((now - _panelPreviewUtc).TotalMilliseconds < 30) return;
        _panelPreviewUtc = now;

        double[] values = [50, 50, 50, 50, 50, 50];
        // 行程轴要翻一次：条上小的那端才抬得高，而轴值小了反而往下
        values[axis] = axis == 0
            ? LovenseProtocol.StrokeAxisValue(percent)
            : Math.Clamp(percent, 0, 100);
        App.Engine.TrySendDirectAxes(values, 0.3);
    }

    /// <summary>拖滑杆时每帧写盘太浪费：停手 0.8 秒再存一次。</summary>
    private void QueuePanelSave()
    {
        if (_panelSaveTimer is null)
        {
            _panelSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _panelSaveTimer.Tick += (_, _) =>
            {
                _panelSaveTimer!.Stop();
                App.Settings.Save();
            };
        }
        _panelSaveTimer.Stop();
        _panelSaveTimer.Start();
    }

    private void PanelBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelStopArm();
        _panelOpen = !_panelOpen;
        App.Settings.OverlayPanelOpen = _panelOpen;
        ApplyPanelVisibility();
        FitToContent();
        App.Settings.Save();
    }

    private void ApplyPanelVisibility()
    {
        if (PanelRow is null) return;
        PanelRow.Visibility = _panelOpen ? Visibility.Visible : Visibility.Collapsed;
        PanelBtn.Content = _panelOpen ? "▴" : "▾";
        PanelBtn.Tag = _panelOpen ? "panel" : "panel";
        if (_panelOpen) SyncPanelFromState();   // 展开时先拨到当前真实值，不然显示的是上次的
        ApplyBackdrop();                        // 展开时底板加实，压在亮画面上也读得清
        _lastFrame = "";
        _cachedPanel = _panelOpen;
    }

    private void PresetBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelStopArm();
        if (sender is not FrameworkElement { Tag: string id } || id.Length == 0) return;

        App.Engine.ApplyQuickPreset(id);
        SyncPanelFromState();     // 预设会改强度，滑杆拨过去
        _lastFrame = "";
        Refresh();
    }

    // ══════════════════════════════════════════════════════════════
    //  窗口样式：不抢焦点 / 点击穿透
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// WS_EX_NOACTIVATE：点小窗只处理鼠标，永远不激活窗口 ——
    /// 全屏游戏里点急停、拖行程条，游戏不会因此失焦切出去。
    /// </summary>
    private void ApplyWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// 点击穿透：加上 WS_EX_TRANSPARENT 后小窗不再收到任何鼠标消息，点下去直接落到下面的窗口。
    /// 开起来就点不到它自己了，撤销只能走托盘菜单（右键 Hexa 图标）。
    /// </summary>
    public void SetClickThrough(bool on)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        int style = GetWindowLong(handle, GWL_EXSTYLE);
        style = on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(handle, GWL_EXSTYLE, style);
        ClickThrough = on;

        if (ClickThroughBtn is not null)
            ClickThroughBtn.Content = on ? "穿透中" : "穿透";

        if (!on || App.TrayIcon is null) return;
        try
        {
            App.TrayIcon.ShowBalloonTip(4000, "小窗已穿透",
                "现在点悬浮窗会直接点到下面的游戏。要恢复：右键任务栏托盘里的 Hexa 图标 → 取消「悬浮窗点击穿透」。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { /* 托盘不可用时忽略：这只是提示 */ }
    }

    private void ClickThroughBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelStopArm();
        SetClickThrough(true);
        AppLogger.Info("悬浮窗：已开启点击穿透（撤销走托盘菜单）");
    }

    // ══════════════════════════════════════════════════════════════
    //  底板（不透明度 / 状态底色 / 半透明时的文字描边）
    // ══════════════════════════════════════════════════════════════

    private void ApplyBackdrop()
    {
        EnsurePalette();

        // 悬停时提亮，方便在低不透明度下读清数值；展开调节面板时同样加实
        // （面板里全是滑杆和数字，50% 透明压在网页/游戏上根本看不清）
        double alpha = (_hover || _panelOpen) ? Math.Max(_opacity, 0.9) : _opacity;
        byte a = (byte)Math.Round(255 * alpha);

        string key = $"{a}|{_tintLevel}";
        if (key != _backdropKey)
        {
            _backdropKey = key;

            Color c0 = _colBg, c1 = _colBgRaised;
            if (_tintLevel == 1)          // 未连接：淡淡的红，提示但不刺眼
            {
                c0 = Mix(c0, _colDanger, 0.10);
                c1 = Mix(c1, _colDanger, 0.08);
            }
            else if (_tintLevel == 2)     // 急停锁定：整块明显变红
            {
                c0 = Mix(c0, _colDanger, 0.30);
                c1 = Mix(c1, _colDanger, 0.22);
            }

            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            bg.GradientStops.Add(new GradientStop(Color.FromArgb(a, c0.R, c0.G, c0.B), 0));
            bg.GradientStops.Add(new GradientStop(Color.FromArgb(a, c1.R, c1.G, c1.B), 1));
            bg.Freeze();
            RootBorder.Background = bg;

            RootBorder.BorderBrush = _tintLevel == 0 ? _brBorderStrong : _brDanger;
        }

        // 不透明度低的时候给文字加一层深色描边，压在亮画面上也看得清
        bool wantShadow = alpha < 0.72;
        if (wantShadow != (ContentGrid.Effect != null))
            ContentGrid.Effect = wantShadow ? ContentShadow : null;
    }

    private static Color Mix(Color from, Color to, double t)
        => Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));

    // ══════════════════════════════════════════════════════════════
    //  尺寸 / 定位
    // ══════════════════════════════════════════════════════════════

    /// <summary>内容需要更大位置时才扩窗，避免数值位数来回变化把窗口抖来抖去。</summary>
    private void FitIfNeeded()
    {
        if (_dragging) { _fitPending = true; return; }

        RootBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var need = RootBorder.DesiredSize;
        if (!double.IsFinite(need.Width) || !double.IsFinite(need.Height)) return;
        if (need.Width > Width + 1 || need.Height > Height + 1) FitToContent();
    }

    /// <summary>按内容测量窗口尺寸；调整时保持右边缘不动。</summary>
    private void FitToContent()
    {
        // 拖拽中先挂起，等拖拽结束再调整，避免窗口跟着鼠标跳动
        if (_dragging)
        {
            _fitPending = true;
            return;
        }

        double rightEdge = Left + Width;   // 尚未定位时为 NaN

        // 按内容自然测量（列宽为 Auto，不受窗口当前宽度影响）；
        // Size 需全限定：全局 using System.Drawing 会与 System.Windows.Size 冲突
        RootBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        double w = Math.Ceiling(RootBorder.DesiredSize.Width);
        double h = Math.Ceiling(RootBorder.DesiredSize.Height);
        if (!double.IsFinite(w) || w < 96) w = 96;
        if (!double.IsFinite(h) || h < 32) h = 32;

        Width  = w;
        Height = h;

        if (double.IsFinite(rightEdge))
            Left = rightEdge - w;

        ClampToScreen(allowPartial: true);
    }

    /// <summary>别让窗口跑到屏幕外面去（换显示器 / 拖到边角之后还得找得回来）。</summary>
    private void ClampToScreen(bool allowPartial)
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;

        double vl = SystemParameters.VirtualScreenLeft;
        double vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth;
        double vh = SystemParameters.VirtualScreenHeight;
        if (!(vw > 0) || !(vh > 0)) return;

        // allowPartial：至少留 80×28 在屏幕里（用户自己拖到边上的位置别硬掰回来）
        double keepW = allowPartial ? Math.Min(Width, 80) : Math.Max(Width, 1);
        double keepH = allowPartial ? Math.Min(Height, 28) : Math.Max(Height, 1);

        double minLeft = vl - (Width - keepW);
        double maxLeft = vl + vw - keepW;
        double minTop  = vt - (Height - keepH);
        double maxTop  = vt + vh - keepH;

        Left = Math.Min(Math.Max(Left, minLeft), Math.Max(minLeft, maxLeft));
        Top  = Math.Min(Math.Max(Top, minTop),  Math.Max(minTop, maxTop));
    }

    // ══════════════════════════════════════════════════════════════
    //  鼠标：拖动 / 悬停
    // ══════════════════════════════════════════════════════════════

    private void RootBorder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;   // 按钮/滑杆自己处理，别顺手拖窗

        CancelStopArm();      // 点别处 = 放弃「预备」
        _dragging = true;
        try
        {
            DragMove();
        }
        finally
        {
            _dragging = false;
            if (_fitPending)
            {
                _fitPending = false;
                FitToContent();
            }
        }

        ClampToScreen(allowPartial: true);
        App.Settings.OverlayLeft = Left;
        App.Settings.OverlayTop  = Top;

        // 拖完鼠标多半还停在窗口上，按真实情况重新决定悬停状态
        SetHover(RootBorder.IsMouseOver);
    }

    private void RootBorder_MouseEnter(object sender, MouseEventArgs e) => SetHover(true);

    private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging) return;                 // 拖动中窗口跟着鼠标跑，别误判成离开
        if (RootBorder.IsMouseOver) return;    // 子控件（按钮）捕获鼠标时可能误报离开
        SetHover(false);
    }

    private void SetHover(bool on)
    {
        if (_hover == on) return;
        _hover = on;
        _backdropKey = "";          // 悬停会改底板亮度
        _lastFrame = "";
        ApplyBackdrop();
        Refresh();
    }

    /// <summary>
    /// 点到按钮、开关、滑杆（含双把手条）时都算「控件自己在处理」，不能顺手把窗口拖走 ——
    /// 展开面板以后如果只挡 Button，拖行程条会变成拖整个小窗。
    /// </summary>
    private static bool IsInteractive(DependencyObject? node)
    {
        var cur = node;
        while (cur != null)
        {
            if (cur is Button or ToggleButton or Slider or Thumb or RangeSlider) return true;
            cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : null;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════════
    //  隐藏
    // ══════════════════════════════════════════════════════════════

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelStopArm();

        // 走 App 的统一入口：顺带把托盘菜单里的勾选状态同步好
        App.HideOverlay();
        if (IsVisible) Hide();      // App 不认识这个实例时（自检等场景）兜底

        if (_hideHintShown) return;
        _hideHintShown = true;
        try
        {
            App.TrayIcon?.ShowBalloonTip(4000, "小窗已隐藏",
                "悬浮窗收起来了，设备照常运行。想找回来：右键任务栏托盘里的 Hexa 图标 → 勾选「悬浮窗」，或到「设置」页点「显示悬浮窗」。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { /* 托盘不可用时忽略：这只是锦上添花的提示 */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _panelSaveTimer?.Stop();
        base.OnClosed(e);
    }

    // ══════════════════════════════════════════════════════════════
    //  杂项
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 把「测得 120 BPM」这类文本压成纯数字：小窗里只留位数，宽度才不会跳。
    /// 取第一段连续数字；实在没有数字就返回空串（该格收起）。
    /// </summary>
    private static string ExtractBpm(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var digits = new System.Text.StringBuilder(4);
        foreach (char c in label)
        {
            if (char.IsDigit(c)) digits.Append(c);
            else if (digits.Length > 0) break;
        }
        return digits.ToString();
    }

    private void EnsurePalette()
    {
        if (_paletteReady) return;
        _paletteReady = true;

        _brFaint        = Res("Faint",        0x8A, 0x95, 0xA0);
        _brPrimary      = Res("Primary",      0x17, 0xB8, 0x90);
        _brAccent       = Res("Accent",       0x5B, 0xA7, 0xFF);
        _brWarning      = Res("Warning",      0xF5, 0xB8, 0x42);
        _brDanger       = Res("Danger",       0xE5, 0x53, 0x53);
        _brSuccess      = Res("Success",      0x2C, 0xCB, 0x7F);
        _brBorderStrong = Res("BorderStrong", 0x3B, 0x44, 0x4D);

        _colBg       = ColorOf("Bg",       0x0E, 0x11, 0x14);
        _colBgRaised = ColorOf("BgRaised", 0x12, 0x16, 0x1A);
        _colDanger   = ColorOf("Danger",   0xE5, 0x53, 0x53);
    }

    private Brush Res(string key, byte r, byte g, byte b)
    {
        if (TryFindResource(key) is SolidColorBrush found) return found;
        var fallback = new SolidColorBrush(Color.FromRgb(r, g, b));
        fallback.Freeze();
        return fallback;
    }

    private Color ColorOf(string key, byte r, byte g, byte b)
        => TryFindResource(key) is SolidColorBrush sb ? sb.Color : Color.FromRgb(r, g, b);
}
