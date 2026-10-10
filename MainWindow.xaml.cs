using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hexa.Services;
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

        // 界面语言：App 启动时已按设置定好，这里把侧栏（导航按钮 + 连接/安全/为什么不动 三行 + 归中·急停）应用一遍。
        LocalizationService.Apply(this);

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

        Page page = GetPage(tag);
        ContentFrame.Navigate(page);
        ApplyLanguageToPage(page);
    }

    /// <summary>
    /// 切页后立刻把这一页按当前语言应用一遍。
    /// 另外挂一次性的 Loaded 钩子：列头这类"只有排完版才在可视树里生成"的文字，导航那一刻还没被创建。
    /// </summary>
    private void ApplyLanguageToPage(Page page)
    {
        LocalizationService.Apply(page);
        page.Loaded += OnPageLoadedApplyLanguage;
    }

    private static void OnPageLoadedApplyLanguage(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement page) return;
        page.Loaded -= OnPageLoadedApplyLanguage;   // 只补一次，之后每次切页都重挂会积钩子
        LocalizationService.Apply(page);
    }

    /// <summary>切换界面语言后，把侧栏与当前页面一起重刷（设置页的下拉改完立刻调用，不用重启）。</summary>
    public void ApplyLanguage()
    {
        LocalizationService.Apply(this);
        if (ContentFrame.Content is FrameworkElement page) LocalizationService.Apply(page);
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
        ConnLabel.Text = connected ? App.Serial.PortName : LocalizationService.T("未连接");
        SafetyLabel.Text = TranslateStatus(App.Engine.SafetyStatus);

        // 一行说清「现在谁在动 / 输出锁没锁 / 游戏最近发了什么」——以前这些散在三处，用户只能猜。
        var why = new List<string> { TranslateStatus(App.Engine.DriverLabel) };
        if (!App.Serial.OutputEnabled && connected) why.Add(LocalizationService.T("输出已锁（点全部归中解锁）"));
        if (App.Bridge.Active) why.Add(App.Bridge.ClientCount > 0
            ? string.Format(LocalizationService.T("游戏已连（{0}）"), App.Bridge.ClientCount)
            : LocalizationService.T("桥开着·等游戏连"));
        string last = App.Bridge.LastCommand;
        if (!string.IsNullOrWhiteSpace(last) && last != "—") why.Add(LocalizationService.T("最近：") + last);
        WhyLabel.Text = string.Join(" · ", why);
        SafetyLabel.Foreground = new SolidColorBrush(stopped
            ? Color.FromRgb(0xF4, 0x72, 0x72)
            : runnable ? Color.FromRgb(0x6E, 0xD6, 0xA0) : Color.FromRgb(0xF5, 0xB8, 0x42));

        // 急停文案要自解释：解锁入口是“全部归中”，动态值与 MainWindow.xaml 里的默认值保持一致。
        EStopBtn.Content = LocalizationService.T(stopped ? "⛔  已急停（点归中解锁）" : "⛔  锁定急停");

        HomeBtn.IsEnabled = connected;
    }

    /// <summary>
    /// 引擎拼出来的状态串（例「运行中 · 脚本播放」）的翻译：词表只认整串，所以按固定分隔符拆开分别查表。
    /// 拆不动（例「⚠ 串口卡死，请拔插 USB…」这类带原因的建议）就整串查表，查不到原样显示中文 ——
    /// 一句看得懂的中文建议，比半截英文有用。
    /// </summary>
    private static string TranslateStatus(string status)
    {
        int separator = status.IndexOf(" · ", StringComparison.Ordinal);
        if (separator < 0) return LocalizationService.T(status);
        return LocalizationService.T(status[..separator]) + " · " + LocalizationService.T(status[(separator + 3)..]);
    }

    private void EStopBtn_Click(object sender, RoutedEventArgs e) => App.Engine.EmergencyStop();

    private void HomeBtn_Click(object sender, RoutedEventArgs e)
    {
        // 归中会解除急停并重新使能输出：急停锁定时先确认，避免侧栏常驻按钮误触直接解锁。
        if (App.Engine.EmergencyStopped && System.Windows.MessageBox.Show(
                LocalizationService.T("设备处于急停锁定，归中会解除急停并重新使能输出，是否继续？"),
                LocalizationService.T("解除急停"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        App.Engine.Home();
    }
}
