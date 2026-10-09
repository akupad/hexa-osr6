using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hexa.Views;

namespace Hexa;

public partial class MainWindow : Window
{
    private readonly Button[] _navBtns;
    private bool _allowClose;      // 托盘“退出”置位后才真正关闭窗口
    private System.Windows.Threading.DispatcherTimer? _statusTimer;
    private bool _exitConfirmed;   // 已问过“未保存的合成”，避免 Shutdown 触发 OnClosing 时重复弹窗

    // 页面按需创建并缓存（懒加载）：启动只构造首屏，避免 7 个页面同时初始化拖慢启动。
    private readonly Dictionary<string, Page> _pages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 已缓存的「脚本制作」页实例；没构造过就返回 null。
    /// 不能调 GetPage("editor")——那会把页面提前构造出来，破坏懒加载。
    /// </summary>
    private EditorPage? CachedEditor =>
        _pages.TryGetValue("editor", out Page? page) ? page as EditorPage : null;

    private Page GetPage(string tag)
    {
        if (_pages.TryGetValue(tag, out Page? cached)) return cached;
        Page page = tag switch
        {
            "manual"   => new ManualControlPage(),
            "strokes"  => new StrokesPage(),
            "editor"   => new EditorPage(),
            "testlab"  => new TestLabPage(),
            "settings" => new SettingsPage(),
            "scripts"  => new ScriptLibraryPage(),
            "ai"       => new AiAssistantPage(),
            _          => new PlaygroundPage(),
        };
        _pages[tag] = page;
        return page;
    }

    public MainWindow()
    {
        InitializeComponent();

        App.Serial.ConnectionChanged += _ => App.Dispatch(UpdateDeviceState);
        App.Engine.StateChanged += () => App.Dispatch(UpdateDeviceState);
        UpdateDeviceState();

        // 兜底刷新：桥的"最近指令"、输出是否被锁这类状态不经引擎事件，
        // 只靠事件驱动会让侧栏那一行长期停在旧值（子代理审计发现）。
        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) => UpdateDeviceState();
        _statusTimer.Start();

        _navBtns = new[] { NavPlayground, NavManual, NavStrokes, NavEditor, NavAi, NavScripts, NavSettings, NavTestLab };

        // Hotkeys need the HWND — attach after the window source is created
        SourceInitialized += (_, _) =>
        {
            App.Hotkeys.Attach(this, App.Settings.GlobalHotkeysEnabled, App.Settings.Hotkeys);
            App.Hotkeys.OnInsert        += () => App.Dispatch(() => App.Engine.TogglePlayback());
            App.Hotkeys.OnEnd           += () => App.Dispatch(() => App.Engine.EmergencyStop());
            App.Hotkeys.OnHome          += () => App.Dispatch(() => App.StrokesVm.NextStroke(+1));
            App.Hotkeys.OnUp            += () => App.Dispatch(() => App.StrokesVm.NextStroke(-1));
            App.Hotkeys.OnDelete        += () => _ = App.Engine.ClimaxBurstAsync();
            App.Hotkeys.OnIntensityUp   += () => { App.Engine.AdjustIntensity(+0.1); App.Settings.Save(); };
            App.Hotkeys.OnIntensityDown += () => { App.Engine.AdjustIntensity(-0.1); App.Settings.Save(); };
        };

        // 回到上次那个页面（不再每次都跳游玩页）
        Navigate(App.Settings.LastPageTag);

        // 先恢复上次的窗口几何（没记过就保持默认），再按工作区夹回来。
        if (App.Settings.WindowLeft >= 0 && App.Settings.WindowTop >= 0)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            Left = App.Settings.WindowLeft;
            Top = App.Settings.WindowTop;
        }
        Width = App.Settings.WindowWidth;
        Height = App.Settings.WindowHeight;

        // 窗口尺寸适配：150% 缩放或小屏时 WorkArea 可能小于设计尺寸，加载后收窄并夹回工作区内。
        Loaded += (_, _) =>
        {
            Rect wa = SystemParameters.WorkArea;   // 主屏 DIP 工作区
            Width  = Math.Min(Width,  wa.Width  - 16);
            Height = Math.Min(Height, wa.Height - 16);
            if (Top + Height > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
            if (Left + Width > wa.Right)  Left = Math.Max(wa.Left, wa.Right - Width);
            if (App.Settings.WindowMaximized) WindowState = System.Windows.WindowState.Maximized;
        };

        // Minimize → hide to tray (keeps message pump alive for hotkeys)
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                Hide();
        };
    }

    /// <summary>把窗口位置/大小与所在页面记下来（下次打开回到原处）。</summary>
    private void SavePlacement()
    {
        try
        {
            App.Settings.WindowMaximized = WindowState == System.Windows.WindowState.Maximized;
            System.Windows.Rect bounds = WindowState == System.Windows.WindowState.Normal
                ? new System.Windows.Rect(Left, Top, Width, Height)
                : RestoreBounds;
            if (bounds.Width >= 900 && bounds.Height >= 600)
            {
                App.Settings.WindowLeft = bounds.Left;
                App.Settings.WindowTop = bounds.Top;
                App.Settings.WindowWidth = bounds.Width;
                App.Settings.WindowHeight = bounds.Height;
            }
            App.Settings.Save();
        }
        catch (Exception ex) { Hexa.Services.AppLogger.Warn("保存窗口位置失败（忽略）: " + ex.Message); }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        // X button → hide to tray instead of closing；只有托盘“退出”置了 _allowClose 才放行
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // 真实关闭路径也守一遍：任何绕过 ForceClose 直接置 _allowClose 的代码，
        // 都必须先过“未保存的合成”确认，否则会静默丢数据。
        if (!_exitConfirmed && CachedEditor?.ConfirmExitWithUnsavedWork() is false)
        {
            e.Cancel = true;
            _allowClose = false;   // 退回“隐藏到托盘”的常态，别让下一次关闭绕过确认
        }
    }

    /// <summary>
    /// 托盘“退出”入口：先确认未保存的动作合成，确认通过才真正退出。
    /// </summary>
    /// <returns>true = 已开始退出（Shutdown 已调用）；false = 用户取消或保存失败，调用方必须中止退出。</returns>
    public bool ForceClose()
    {
        // 守卫必须在 _allowClose = true 之前：用户取消时窗口保持打开、进程不退出。
        if (CachedEditor?.ConfirmExitWithUnsavedWork() is false) return false;
        _exitConfirmed = true;   // 已确认过，OnClosing 的守卫不必重复弹窗
        _allowClose = true;
        // Hotkeys disposed in App.OnExit — don't double-dispose here
        System.Windows.Application.Current.Shutdown();
        return true;
    }

    private void Navigate(string tag)
    {
        if (App.Settings.LastPageTag != tag)
        {
            App.Settings.LastPageTag = tag;
            App.Settings.Save();
        }
        foreach (var b in _navBtns)
        {
            bool active = (string)b.Tag! == tag;
            b.Background = active ? (Brush)FindResource("Primary") : Brushes.Transparent;
            // 选中态底色是亮绿 Primary：白字只有 2.5:1，改用深墨字（约 7:1）
            b.Foreground  = active ? (Brush)FindResource("OnPrimaryInk") : (Brush)FindResource("Text");
        }

        ContentFrame.Navigate(GetPage(tag));
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
            Navigate(btn.Tag as string ?? "playground");
    }

    /// <summary>Called from pages to trigger navigation programmatically.</summary>
    public void NavigateTo(string tag) => Navigate(tag);

    private void UpdateDeviceState()
    {
        bool connected = App.Serial.IsOpen;
        bool stopped = App.Engine.EmergencyStopped;
        bool runnable = App.Engine.CanRun;

        ConnDot.Fill = new SolidColorBrush(!connected
            ? Color.FromRgb(0xEF, 0x44, 0x44)
            : stopped ? Color.FromRgb(0xF4, 0x72, 0x72)
            : runnable ? Color.FromRgb(0x22, 0xC5, 0x7A) : Color.FromRgb(0xF5, 0xB8, 0x42));
        ConnLabel.Text = connected ? App.Serial.PortName : "未连接";
        SafetyLabel.Text = App.Engine.SafetyStatus;

        // 一行说清「现在谁在动 / 输出锁没锁 / 游戏最近发了什么」——以前这些散在三处，用户只能猜。
        var why = new List<string> { App.Engine.DriverLabel };
        if (!App.Serial.OutputEnabled && connected) why.Add("输出已锁（点全部归中解锁）");
        if (App.Bridge.Active) why.Add(App.Bridge.ClientCount > 0 ? $"游戏已连（{App.Bridge.ClientCount}）" : "桥开着·等游戏连");
        string last = App.Bridge.LastCommand;
        if (!string.IsNullOrWhiteSpace(last) && last != "—") why.Add("最近：" + last);
        WhyLabel.Text = string.Join(" · ", why);
        SafetyLabel.Foreground = new SolidColorBrush(stopped
            ? Color.FromRgb(0xF4, 0x72, 0x72)
            : runnable ? Color.FromRgb(0x6E, 0xD6, 0xA0) : Color.FromRgb(0xF5, 0xB8, 0x42));

        // 急停文案要自解释：解锁入口是“全部归中”，动态值与 MainWindow.xaml 里的默认值保持一致。
        EStopBtn.Content = stopped ? "⛔  已急停（点归中解锁）" : "⛔  锁定急停";

        HomeBtn.IsEnabled = connected;
    }

    private void EStopBtn_Click(object sender, RoutedEventArgs e) => App.Engine.EmergencyStop();

    private void HomeBtn_Click(object sender, RoutedEventArgs e)
    {
        // 归中会解除急停并重新使能输出：急停锁定时先确认，避免侧栏常驻按钮误触直接解锁。
        if (App.Engine.EmergencyStopped && System.Windows.MessageBox.Show(
                "设备处于急停锁定，归中会解除急停并重新使能输出，是否继续？",
                "解除急停", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        App.Engine.Home();
    }
}
