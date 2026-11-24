using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Forms;
using Hexa.Models;
using Hexa.Services;
using Hexa.ViewModels;
using Hexa.Views;

namespace Hexa;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceName = @"Local\Hexa.TCode.Controller.SingleInstance";
    private static Mutex? _singleInstanceMutex;
    public static AppSettings Settings { get; private set; } = null!;
    public static SerialService Serial  { get; private set; } = null!;
    public static MotionEngine Engine   { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static AudioReactiveService AudioReactive { get; private set; } = null!;
    public static GameTelemetryService GameTelemetry { get; private set; } = null!;
    public static RuleEngine RuleEngine { get; private set; } = null!;
    public static ForegroundWatcher ForegroundWatcher { get; private set; } = null!;
    public static GameKeyPoller GameKeyPoller { get; private set; } = null!;
    public static FunscriptPlayerService FunscriptPlayer { get; private set; } = null!;
    public static IntifaceBridgeService Bridge { get; private set; } = null!;

    /// <summary>Lovense 兼容服务：游戏把本机当 Lovense 玩具连进来时，命令从这儿落到设备。</summary>
    public static LovenseBridgeService Lovense { get; private set; } = null!;
    public static TcodeFanout Fanout { get; private set; } = null!;
    public static LibraryWebService LibraryWeb { get; private set; } = null!;

    public static PlaygroundViewModel PlaygroundVm { get; private set; } = null!;
    public static StrokesViewModel    StrokesVm    { get; private set; } = null!;
    public static SettingsViewModel   SettingsVm   { get; private set; } = null!;
    public static AiAssistantViewModel AiAssistantVm { get; private set; } = null!;
    public static AiAssistantService  AiAssistant    { get; private set; } = null!;

    public static WebApiService    WebApi   { get; private set; } = null!;
    public static NotifyIcon?      TrayIcon { get; private set; }

    // ── 诊断自动留档（DiagnosticReport.Create 的挂钩）────────────────
    // 同一标签 5 分钟内只打一次包，避免断开重连风暴时反复压 zip。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _autoArchiveLast = new();
    private const long AutoArchiveIntervalMs = 5 * 60 * 1000L;
    private static bool _lastEStopState;   // 急停状态上一次快照（StateChanged 翻转检测用）

    // ── 设置热加载 ────────────────────────────────────────────────
    // 用处：手改 settings.json（或别的工具写它）之后不用关程序重开，1 秒内就生效。
    // 界面里改的设置本来就即时生效，这里补的是「从文件外面改」的那条路。
    private static System.IO.FileSystemWatcher? _settingsWatcher;
    private static System.Windows.Threading.DispatcherTimer? _settingsReloadTimer;

    /// <summary>设置从文件热加载完成后触发（界面可用它刷新显示）。</summary>
    public static event Action? SettingsReloaded;

    /// <summary>
    /// 悬浮窗显示/隐藏（点小窗上的 ✕、托盘菜单、快捷键、设置页按钮都会走到这里）。
    /// 参数 = 现在是不是显示着。设置页靠它同步按钮文案，并把「🪟 悬浮窗」那块顺手收起。
    /// </summary>
    public static event Action<bool>? OverlayVisibilityChanged;
    private static CompactOverlay? _overlay;

    // ── 托盘菜单引用（只构建一次，状态变化时由 SyncTrayMenu 刷新）────
    private static ContextMenuStrip? _trayMenu;
    private static ToolStripMenuItem? _trayStatusItem;      // 连接状态
    private static ToolStripMenuItem? _trayEStopItem;       // 急停状态
    private static ToolStripMenuItem? _trayOverlayItem;     // 悬浮窗（可勾选）
    private static ToolStripMenuItem? _trayClickThroughItem; // 悬浮窗点击穿透（可勾选）
    private static bool _overlayClickThrough;                // 穿透是纯运行期状态，不写盘
    private static ToolStripMenuItem? _trayIntensityMenu;   // 强度子菜单（标题带百分比）
    private static ToolStripMenuItem? _trayIntensityLabel;  // 强度当前值
    private static readonly Dictionary<MotionMode, ToolStripMenuItem> _trayModeItems = new();

    // ── 托盘气泡提示：都遵循“只在真正发生时提示一次”，避免打扰 ──────────
    private static bool _trayHideTipShown;         // 「窗口已隐藏到托盘」只提示一次
    private static bool _connectionBalloonReady;   // 启动时先记下初始连接状态，之后只有“真正翻转”才提示
    private static bool _lastBalloonConnected;     // 上一次已知的连接状态
    private static bool _lastBalloonSimulation;    // 上一次是否为模拟设备（模拟进出都不提示）
    private static string _lastBalloonPort = "";   // 上一次已知的真实串口名（掉线提示要显示它）
    private static bool _shuttingDown;             // 退出中：窗口关闭会触发 IsVisibleChanged，别再弹气泡

    /// <summary>
    /// 用户主动点「断开连接」时置位：这一次断开是用户自己要的，
    /// 不要弹「设备已断开 · 正在自动重连…」气泡（Serial.Disconnect 也不会再自动重连）。
    /// </summary>
    public static bool SuppressDisconnectBalloon { get; set; }

    /// <summary>
    /// 下拉框上的滚轮：不改变选中项，转交给最近的 ScrollViewer 滚动页面。
    /// （用户反馈在测试台/设置页滚动时，经过下拉框会把选项滚掉。）
    /// </summary>
    private void ComboBox_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        if (sender is not System.Windows.DependencyObject node) return;

        System.Windows.DependencyObject? current = node;
        while (current != null)
        {
            if (current is System.Windows.Controls.ScrollViewer viewer)
            {
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 3.0);
                return;
            }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
    }

    /// <summary>
    /// 普通 Slider 上的滚轮：不改数值，改为滚动外层页面（与 ComboBox 同一套处理）。
    /// </summary>
    private void Slider_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        => ComboBox_PreviewMouseWheel(sender, e);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 全局未处理异常兜底（审计发现：全仓库一个钩子都没有，UI 线程一崩就静默退出，
        //    app.log 里连堆栈都没有，等于零线索）──
        // 三个钩子各管一类：UI 线程、任何线程的未捕获异常、被丢弃的 Task 异常。
        // 一律先记日志（含完整堆栈）再放行原有行为 —— 不吞异常、不改变程序语义。
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("界面线程未处理异常", args.Exception);
            AutoArchiveSync("unhandled-exception");

            // 唯一一种"记完日志就放行会白死"的异常：在 WPF 布局 / 应用模板的过程中弹了模态框
            // （MessageBox.Show 内部会 PushFrame），WPF 自己的消息回调里抛
            // InvalidOperationException「调度程序处理已暂停，但仍在处理消息」。
            // 这一刻崩的原因在 WPF 的泵里，不在我们的业务状态里 —— 界面数据是好的，用户看到的
            // 只是"点两下就闪退"。所以这类异常记完日志后吞掉（其他异常照旧放行：未知状态继续跑更危险）。
            // 触发源已经修掉（Views/SettingsPage.xaml.cs 不再在 IsVisibleChanged 里弹框），
            // 这里只是最后一道保险。注意消息是本地化的，中英文都要认。
            string message = args.Exception?.Message ?? "";
            // 其它异常仍然照旧终止（未知状态继续跑更危险），但**必须给用户一个交代**：
            // 以前窗口直接消失、没有任何提示，用户只能自己去翻 app.log（子代理审计发现）。
            if (args.Exception is not InvalidOperationException
                || (!message.Contains("Dispatcher processing has been suspended", StringComparison.OrdinalIgnoreCase)
                    && !message.Contains("调度程序处理已暂停", StringComparison.Ordinal)))
            {
                try
                {
                    string logPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexa", "app.log");
                    System.Windows.MessageBox.Show(
                        "Hexa 遇到一个未处理的错误，需要关闭。\n\n" +
                        "错误已经写进日志：\n" + logPath + "\n\n" +
                        "重新打开 Hexa 即可继续用；如果反复出现，把这个文件发给我。",
                        "Hexa 出错", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch { /* 连提示都弹不出来时保持原行为 */ }
                return;
            }
            if (args.Exception is InvalidOperationException
                && (message.Contains("Dispatcher processing has been suspended", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("调度程序处理已暂停", StringComparison.Ordinal)))
            {
                AppLogger.Warn("已忽略：布局过程中被弹出的模态框（不影响当前界面状态）");
                args.Handled = true;
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Error("未处理异常（进程即将结束）" + (args.IsTerminating ? "（终止）" : ""),
                args.ExceptionObject as Exception);
            AutoArchiveSync("unhandled-exception");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("被丢弃的任务异常（未 await）", args.Exception);
            AutoArchiveSync("unhandled-exception");
            args.SetObserved();   // 已记录，不必再让进程崩
        };

        // 自检（HEXA_SELFTEST）是诊断运行：它在模拟设备上跑，不碰串口，也不该被"已经在运行"挡住。
        // 之前它被单实例互斥体挡回去直接退出，报告文件根本不生成，只能等用户关掉正在用的窗口。
        bool diagnosticRun = Environment.GetEnvironmentVariable("HEXA_SELFTEST") is { Length: > 0 };
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirstInstance);
        if (!isFirstInstance && !diagnosticRun)
        {
            System.Windows.MessageBox.Show("Hexa 已经在运行。请从任务栏托盘打开现有窗口。", "Hexa",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        if (!isFirstInstance) AppLogger.Info("自检运行：跳过单实例检查（不影响正在使用的窗口）。");

        // 诊断运行绝不写用户的真实设置：自检为了验证「轴限位界面显示」会临时把 L0 改成 3000–8000，
        // 只要进程在还原之前被杀掉（或中途崩了），你的限位就永久变成那一段（真实发生过：
        // 用户发现 L0 只能在中间一段动、滑杆圆点不在两端）。这里让没显式指定设置文件的自检
        // 自动改用临时沙箱副本 —— 从真实设置复制一份开跑，写回去也只写副本。
        if (diagnosticRun && Environment.GetEnvironmentVariable("HEXA_SETTINGS_PATH") is not { Length: > 0 })
        {
            try
            {
                string realSettings = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Hexa", "settings.json");
                string sandboxDir = Path.Combine(Path.GetTempPath(), "hexa-selftest");
                string sandboxSettings = Path.Combine(sandboxDir, "settings.json");
                Directory.CreateDirectory(sandboxDir);
                if (File.Exists(realSettings)) File.Copy(realSettings, sandboxSettings, overwrite: true);
                Environment.SetEnvironmentVariable("HEXA_SETTINGS_PATH", sandboxSettings);
                AppLogger.Info($"自检运行：设置改用沙箱副本 {sandboxSettings}（不会碰真实设置）");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("自检沙箱准备失败，将直接用真实设置：" + ex.Message);
            }
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 改名 Helix → Hexa：先把旧目录的数据复制过来（只复制、不删除、不覆盖新数据）
        AppSettings.MigrateLegacyDataIfNeeded();
        Settings = AppSettings.Load();

        // 画面同步：每次启动一律默认关，只有用户在测试台亲手点「开始同步」才跟。
        // 不这么做的话，上次点过的「开」会被存盘带回来，开机就自己跟着画面动 —— 用户 2026-11-23 明确否掉。
        // 测试/排查时可以用 HEXA_KEEP_SCREENWATCH=1 保留上次的开关（正常启动不受影响）。
        bool keepScreenWatchOnStart = Environment.GetEnvironmentVariable("HEXA_KEEP_SCREENWATCH") is { Length: > 0 };
        if (Settings.ScreenWatchEnabled && !keepScreenWatchOnStart)
        {
            Settings.ScreenWatchEnabled = false;
            AppLogger.Info("画面同步：启动时默认关闭（上次是开着的，按要求不自动恢复）");
        }
        if (keepScreenWatchOnStart)
            AppLogger.Info("画面同步：HEXA_KEEP_SCREENWATCH=1，保留上次的开关状态（测试用）");

        Settings.Save();
        StartSettingsWatcher();
        // 界面语言：必须赶在 MainWindow 之前定下来，否则首屏会先按中文渲染一遍。
        // （Normalize 已保证这里只有 zh / en，写坏的值回落中文。）
        Hexa.Services.LocalizationService.UiLanguage = Settings.UiLanguage;
        Serial   = new SerialService();
        Engine   = new MotionEngine(Serial, Settings, Serial.BlackBox);
        Hotkeys  = new HotkeyService();

        // 手画波形的自动草稿：上次退出时存的，启动就恢复（波形只存在内存里，不恢复就丢了）
        // 六轴启用状态 / 平滑插值必须一起恢复：只恢复控制点的话，界面显示「播放中」但六轴全禁用，只输出中位 50。
        if (WaveDraftStore.Load() is { } draft)
        {
            foreach ((string id, List<(double X, double Y)> points) in draft.Waves)
                Engine.EditWaves[id] = points;
            Engine.CwCycleLen = Math.Clamp(draft.CycleLen, 0.5, 60);
            for (int i = 0; i < Engine.WfAxisEnabled.Length && i < draft.AxisEnabled.Length; i++)
                Engine.WfAxisEnabled[i] = draft.AxisEnabled[i];
            Engine.UseSmoothCustomInterpolation = draft.UseSmooth;
            AppLogger.Info($"已恢复波形草稿：{draft.Waves.Count} 条轴 · 启用 {draft.AxisEnabled.Count(flag => flag)} 轴 · 平滑插值 {(draft.UseSmooth ? "开" : "关")}");
        }

        AudioReactive = new AudioReactiveService(Engine, Settings);
        // 底色/伴随这条路的「不盲从鼓点」要用声音分类，把取样口交给引擎。
        Engine.AudioSource = AudioReactive;
        GameTelemetry = new GameTelemetryService(
            Settings.GameTelemetryPort,
            Settings.GameTelemetryToken,
            () => Settings.AllowRawTCodeUdp,
            () => Settings.CompanionProcess);
        RuleEngine    = new RuleEngine(Engine, Settings, AudioReactive, GameTelemetry);
        // 传入音频服务：脚本「空档填缝」要读它的声音事件（只读事件，不让它驱动设备）
        FunscriptPlayer = new FunscriptPlayerService(Engine, Settings, AudioReactive);
        ForegroundWatcher = new ForegroundWatcher(Engine, Settings, FunscriptPlayer);
        ForegroundWatcher.Start();
        GameKeyPoller = new GameKeyPoller(Engine, Settings);
        GameKeyPoller.Start();
        Bridge = new IntifaceBridgeService(Serial, Engine, Settings);
        if (Settings.GameBridgeEnabled) Bridge.Start();
        Lovense = new LovenseBridgeService(Engine)
        {
            ToyName = Settings.LovenseToyName,
            StrokeTopPercent = Settings.LovenseStrokeTopPercent,
            StrokeDeepPercent = Settings.LovenseStrokeDeepPercent,
            RotateTopPercent = Settings.LovenseRotateTopPercent,
            RotateDeepPercent = Settings.LovenseRotateDeepPercent,
            LocalOnly = Settings.LovenseLocalOnly,
            ExtraRotateToy = Settings.LovenseExtraRotateToy,
            AutoTwistEnabled = Settings.AutoTwistEnabled,
            AutoTwistAmplitudePercent = Settings.AutoTwistAmplitudePercent,
        };
        if (Settings.LovenseBridgeEnabled) Lovense.Start(Settings.LovenseBridgePort, LovenseBridgeService.DefaultHttpPort);

        Fanout = new TcodeFanout { Enabled = Settings.FanoutEnabled };
        Fanout.Configure(Settings.FanoutTargets);
        Serial.Mirror = Fanout.Publish;   // 设备发什么，网络目标就收到什么（含 DSTOP）

        LibraryWeb = new LibraryWebService(FunscriptPlayer);
        if (Settings.LibraryWebEnabled) LibraryWeb.Start();

        PlaygroundVm = new PlaygroundViewModel(Engine, Settings);
        StrokesVm    = new StrokesViewModel(Engine, Settings);
        SettingsVm   = new SettingsViewModel(Serial, Engine, Settings);

        AiAssistant = new AiAssistantService(Engine, Settings);
        AiAssistantVm = new AiAssistantViewModel(AiAssistant);

        WebApi = new WebApiService(Engine, Settings, StrokesVm, FunscriptPlayer);
        if (Settings.WebApiEnabled) WebApi.Start();

        if (Settings.SimulationMode)
            Serial.SetSimulationMode(true);
        else if (Settings.AutoConnect)
        {
            Serial.EnableAutoDiscovery(Settings.PreferWired);
            if (!string.IsNullOrWhiteSpace(Settings.Port))
                _ = Serial.ConnectAsync(Settings.Port, Settings.FallbackPort);
        }

        SetupTrayIcon();

        // ── 诊断自动留档 + 停顿检测挂钩（只加事件/调用，不改被诊断的服务本身）──
        // 串口三信号（断开/重连失败/板子重启）由 SerialService.Incident 统一送出；
        // 急停由 Engine.StateChanged 翻转检测；未处理异常在开头三个钩子里各留档一次。
        Serial.Incident += OnSerialIncident;
        Engine.StateChanged += OnEngineStateChangedForEStop;
        _lastEStopState = Engine.EmergencyStopped;
        Hexa.Diagnostics.StallDetector.Start();

        // 存储位置：把设置里的自定义目录套到两个静态仓库上（空 = 默认目录）
        Hexa.Models.CompositionStore.CustomDirectory = Settings.CompositionFolder;
        ScriptLibrary.CustomFolder = Settings.ScriptFolder;

        var win = new MainWindow();
        // ✕ / 最小化都只是把窗口 Hide() 到托盘（见 MainWindow.OnClosing / StateChanged），
        // App 收不到通知，所以在这里挂 IsVisibleChanged 钩子：第一次从可见变隐藏时提示一次。
        // 只改本文件，不动 MainWindow.xaml.cs。
        win.IsVisibleChanged += (_, _) =>
        {
            if (_trayHideTipShown || _shuttingDown || win.IsVisible) return;
            _trayHideTipShown = true;
            TrayIcon?.ShowBalloonTip(3000, "Hexa 仍在运行",
                "窗口已最小化到托盘：双击图标恢复，右键「退出」才真正关闭。", ToolTipIcon.Info);
        };
        win.Show();

        // 首次启动引导（Req10）：给新手一个基本流程说明。
        if (!Settings.FirstRunDone)
        {
            System.Windows.MessageBox.Show(
                "首次使用 Hexa？\n\n" +
                "① 到“设置”页选择串口并“立即连接”（或启动时自动连接）。\n" +
                "② 连接后设备默认解锁可动。\n" +
                "③ 用“全部归中”可让设备回中；用“锁定急停”可立即停止。\n" +
                "④ 到“游玩”页选预设开始。连接不上时，先到“设置”页点“刷新设备”确认串口是否被别的程序占用。",
                "Hexa 首次使用",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Settings.FirstRunDone = true;
            Settings.Save();
        }

        // 恢复悬浮窗状态
        if (Settings.OverlayVisible)
            ShowOverlay();

        // 全方位自检（模拟设备，不会真动机器）：HEXA_SELFTEST=<报告文件> 时跑一遍后退出。
        if (Environment.GetEnvironmentVariable("HEXA_SELFTEST") is { Length: > 0 } reportPath)
            Diagnostics.SelfTest.Run(reportPath);
    }

    // ── 悬浮窗管理 ──────────────────────────────────────────────────
    public static void ShowOverlay()
    {
        if (_overlay == null || !_overlay.IsLoaded)
            _overlay = new CompactOverlay();
        _overlay.ApplySettings();   // 不透明度/显示项按最新设置生效
        _overlay.ShowActivated = false;   // 每次显示都不要抢游戏的前台焦点
        _overlay.Show();
        if (_overlayClickThrough) _overlay.SetClickThrough(true);   // 穿透状态在运行期一直有效
        Settings.OverlayVisible = true;
        Settings.Save();
        SyncTrayMenu();
        OverlayVisibilityChanged?.Invoke(true);
    }

    public static void HideOverlay()
    {
        _overlay?.Hide();
        Settings.OverlayVisible = false;
        Settings.Save();
        SyncTrayMenu();
        OverlayVisibilityChanged?.Invoke(false);
    }

    public static void ToggleOverlay()
    {
        if (_overlay?.IsVisible == true)
            HideOverlay();
        else
            ShowOverlay();
    }

    /// <summary>
    /// 悬浮窗点击穿透开关：开着小窗就完全不接鼠标（点下去落到下面的游戏/窗口）。
    /// 因为开着的时候点不到小窗本身，撤销入口只有这个托盘菜单项。
    /// </summary>
    public static void ToggleOverlayClickThrough()
    {
        _overlayClickThrough = !_overlayClickThrough;
        _overlay?.SetClickThrough(_overlayClickThrough);
        AppLogger.Info(_overlayClickThrough ? "悬浮窗：点击穿透开" : "悬浮窗：点击穿透关");
        SyncTrayMenu();
    }

    /// <summary>
    /// 悬浮窗设置（不透明度 / 显示项）变化后立即生效：
    /// 直接调 CompactOverlay.ApplySettings() 重建底板刷子与显示项，
    /// 不必等悬浮窗自身的 500ms 刷新循环。
    /// </summary>
    public static void RefreshOverlay()
    {
        _overlay?.ApplySettings();
        SyncTrayMenu();
    }

    // ── 托盘图标 ────────────────────────────────────────────────────
    private void SetupTrayIcon()
    {
        var iconPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");

        TrayIcon = new NotifyIcon
        {
            Icon    = System.IO.File.Exists(iconPath)
                        ? new System.Drawing.Icon(iconPath)
                        : SystemIcons.Application,
            Visible = true,
            Text    = "Hexa — TCode Controller"
        };

        // 设置文件损坏时给用户一个交代（否则轴限位被清成 0–9999 而他毫不知情）。
        if (AppSettings.LastLoadNotice is { Length: > 0 } notice)
            TrayIcon.ShowBalloonTip(9000, "Hexa 的设置被重置了", notice, ToolTipIcon.Warning);

        // 启动期失败统一可见：给自动连设备留 6 秒，然后一次性把"没起来的东西"说清楚。
        if (Environment.GetEnvironmentVariable("HEXA_SELFTEST") is not { Length: > 0 })
        {
            var startupCheck = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            startupCheck.Tick += (_, _) =>
            {
                startupCheck.Stop();
                try
                {
                    if (!Serial.IsOpen && !Serial.IsSimulation)
                        TrayIcon?.ShowBalloonTip(9000, "设备还没连上",
                            string.IsNullOrWhiteSpace(Serial.LastError)
                                ? "到「设置」页点「刷新设备」看看有没有串口；插好设备再点「立即连接」。"
                                : Serial.LastError, ToolTipIcon.Warning);
                    else if (Settings.GameBridgeEnabled && !Bridge.Active)
                        TrayIcon?.ShowBalloonTip(9000, "游戏桥没起来",
                            "原因：" + (Bridge.LastError ?? "未知") + " —— 到「游玩 → 游戏桥」换一个监听端口再开一次。",
                            ToolTipIcon.Warning);
                }
                catch { /* 提示失败不影响主流程 */ }
            };
            startupCheck.Start();
        }

        BuildTrayMenu();
        TrayIcon.DoubleClick += (_, _) => ShowMainWindow();

        // 连接/急停/模式/强度变化时刷新托盘菜单（服务线程 → UI 线程）
        Serial.ConnectionChanged += _ => Dispatch(SyncTrayMenu);
        // 设备掉线 / 重连的托盘气泡：与上面的菜单刷新分开，只在状态真正翻转时提示
        Serial.ConnectionChanged += OnSerialConnectionChangedForBalloon;
        Engine.StateChanged      += () => Dispatch(SyncTrayMenu);

        // 记下启动时的初始状态：之后只有“真正翻转”才提示，启动时不会误报
        _lastBalloonConnected   = Serial.IsOpen;
        _lastBalloonSimulation  = Serial.IsSimulation;
        _lastBalloonPort        = Serial.IsSimulation ? "" : Serial.PortName;
        _connectionBalloonReady = true;

        SyncTrayMenu();
    }

    /// <summary>
    /// 设备掉线 / 重连的托盘气泡提示。
    /// 只在连接状态真正翻转时提示：启动时已先记下初始状态（_connectionBalloonReady），
    /// 之后相同状态的重复上报一律忽略，所以启动时不会误报。
    /// 模拟设备（Serial.IsSimulation）不提示——它没有真实硬件会掉线，进出模拟也不该报警。
    /// </summary>
    private static void OnSerialConnectionChangedForBalloon(bool connected)
    {
        // 事件来自服务线程：切回 UI 线程再碰 NotifyIcon
        Dispatch(() =>
        {
            if (TrayIcon is null || _shuttingDown) return;

            string port = Serial.PortName;
            bool simulation = Serial.IsSimulation;
            bool wasSimulation = _lastBalloonSimulation;

            if (!_connectionBalloonReady)
            {
                // 还没记录过初始状态：这一次只记录，不提示
                _connectionBalloonReady = true;
                _lastBalloonConnected   = connected;
                _lastBalloonSimulation  = simulation;
                _lastBalloonPort        = simulation ? "" : port;
                return;
            }

            bool changed = connected != _lastBalloonConnected;
            _lastBalloonConnected  = connected;
            _lastBalloonSimulation = simulation;
            string previousPort = _lastBalloonPort;
            if (!simulation && port.Length > 0) _lastBalloonPort = port;

            if (!changed) return;                       // 状态没翻转：不提示
            if (simulation || wasSimulation) return;    // 模拟设备的进出：不提示

            if (!connected)
            {
                if (SuppressDisconnectBalloon) { SuppressDisconnectBalloon = false; return; }   // 用户主动断开
                TrayIcon.ShowBalloonTip(3000, "设备已断开",
                    previousPort.Length > 0
                        ? $"设备已断开（{previousPort}）· 正在自动重连…"
                        : "设备已断开 · 正在自动重连…",
                    ToolTipIcon.Warning);
            }
            else if (port.Length > 0)
            {
                TrayIcon.ShowBalloonTip(3000, "已连接设备", $"已连接 {port}", ToolTipIcon.Info);
            }
        });
    }

    /// <summary>构建托盘右键菜单。菜单项引用存进静态字段，之后交给 SyncTrayMenu 动态刷新。</summary>
    private void BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        // 每次展开前统一刷新一遍，保证状态不过期
        menu.Opening += (_, _) => SyncTrayMenu();

        // ── 显示/悬浮窗 ──────────────────────────────────────────────
        menu.Items.Add("显示窗口", null, (_, _) => ShowMainWindow());
        // 悬浮窗改成可勾选：Checked = Settings.OverlayVisible，点击切换显示/隐藏
        _trayOverlayItem = new ToolStripMenuItem("悬浮窗 ⧉", null, (_, _) => ToggleOverlay());
        menu.Items.Add(_trayOverlayItem);
        // 点击穿透：勾上后小窗完全不挡鼠标（游戏里不用担心点到它）。开起来就点不到小窗本身了，
        // 撤销只能走这里 —— 所以它必须在托盘菜单里，而不是小窗自己身上。
        _trayClickThroughItem = new ToolStripMenuItem("悬浮窗点击穿透", null, (_, _) => ToggleOverlayClickThrough());
        menu.Items.Add(_trayClickThroughItem);
        menu.Items.Add(new ToolStripSeparator());

        // ── 连接状态 / 急停状态（只读展示，随状态刷新）───────────────
        _trayStatusItem = new ToolStripMenuItem("未连接") { Enabled = false };
        menu.Items.Add(_trayStatusItem);
        _trayEStopItem = new ToolStripMenuItem("急停：正常") { Enabled = false };
        menu.Items.Add(_trayEStopItem);
        menu.Items.Add(new ToolStripSeparator());

        // ── 预设子菜单（快速预设） ───────────────────────────────────
        var presetMenu = new ToolStripMenuItem("预设");
        foreach (var (id, label) in PlaygroundViewModel.QuickPresets)
        {
            string presetId = id;   // 闭包捕获
            string lbl      = label;
            presetMenu.DropDownItems.Add(lbl, null, (_, _) =>
            {
                Engine.ApplyQuickPreset(presetId);
                // 启动失败要说话：以前返回值被丢掉，急停锁定/设备没连时点托盘预设毫无反应。
                if (!Engine.AutoRunning && !Engine.StartAuto())
                    NotifyTrayActionFailed("预设「" + lbl + "」没启动");
                SyncTrayMenu();
            });
        }
        menu.Items.Add(presetMenu);

        // ── 模式子菜单 ───────────────────────────────────────────────
        var modeMenu = new ToolStripMenuItem("模式");
        var modeItems = new (MotionMode mode, string label)[]
        {
            (MotionMode.Auto,   "自动模式"),
            (MotionMode.Stroke, "行程模式"),
            (MotionMode.Custom, "自定义模式"),
        };
        _trayModeItems.Clear();
        foreach (var (mode, label) in modeItems)
        {
            var mi  = new ToolStripMenuItem(label) { Tag = mode };
            var m   = mode;  // 闭包
            mi.Click += (_, _) =>
            {
                Engine.ActiveMode = m;
                switch (m)
                {
                    case MotionMode.Auto:
                        if (!Engine.AutoRunning) Engine.StartAuto(); break;
                    case MotionMode.Stroke:
                        if (!Engine.StrokeRunning && StrokesVm.SelectedStroke != null)
                            Engine.StartStroke(StrokesVm.SelectedStroke); break;
                    case MotionMode.Custom:
                        if (!Engine.CustomRunning && !Engine.StartCustom())
                            NotifyTrayActionFailed("「自定义模式」没启动");
                        break;
                }
                SyncTrayMenu();
            };
            modeMenu.DropDownItems.Add(mi);
            _trayModeItems[m] = mi;
        }
        // 打开前同步 checkmark（与 SyncTrayMenu 同一套逻辑）
        modeMenu.DropDownOpening += (_, _) => SyncTrayMenu();
        menu.Items.Add(modeMenu);

        menu.Items.Add(new ToolStripSeparator());

        // ── 强度子菜单 ───────────────────────────────────────────────
        _trayIntensityMenu = new ToolStripMenuItem("强度");
        _trayIntensityLabel = new ToolStripMenuItem("当前: 100%") { Enabled = false };
        _trayIntensityMenu.DropDownItems.Add(_trayIntensityLabel);
        _trayIntensityMenu.DropDownItems.Add(new ToolStripSeparator());
        _trayIntensityMenu.DropDownItems.Add("强度 +10%", null, (_, _) => { Engine.AdjustIntensity(+0.1); Settings.Save(); SyncTrayMenu(); });
        _trayIntensityMenu.DropDownItems.Add("强度 -10%", null, (_, _) => { Engine.AdjustIntensity(-0.1); Settings.Save(); SyncTrayMenu(); });
        // 打开前刷新当前值
        _trayIntensityMenu.DropDownOpening += (_, _) => SyncTrayMenu();
        menu.Items.Add(_trayIntensityMenu);

        menu.Items.Add(new ToolStripSeparator());

        // ── 急停 / 退出 ──────────────────────────────────────────────
        menu.Items.Add("锁定急停", null, (_, _) => { Engine.EmergencyStop(); SyncTrayMenu(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            // 有未保存的动作合成时 ForceClose 会先询问用户：返回 false = 用户取消（或保存失败），
            // 此时必须中止退出——不关窗口、不 Shutdown，并把刚隐藏的托盘图标恢复回来。
            TrayIcon!.Visible = false;
            bool exiting = true;
            if (Current.MainWindow is MainWindow win) exiting = win.ForceClose();
            else Shutdown();
            if (!exiting) TrayIcon!.Visible = true;
        });

        _trayMenu = menu;
        TrayIcon!.ContextMenuStrip = menu;
    }

    /// <summary>
    /// 按当前连接/急停/模式/强度/悬浮窗状态刷新托盘菜单。
    /// 可从任意线程调用（服务线程会切回 UI 线程再改菜单）。
    /// </summary>
    /// <summary>托盘上的动作没生效时说一句为什么（用户点的是菜单，看不到任何页面提示）。</summary>
    private static void NotifyTrayActionFailed(string what)
    {
        string why = Engine.EmergencyStopped
            ? "急停锁定中：先点侧栏「⬆ 全部归中」解锁（会先问一次）。"
            : !Serial.IsOpen
                ? "设备没连上：先到「设置」页连设备。"
                : $"现在由「{Engine.DriverLabel}」在驱动设备，先把它停掉再试。";
        AppLogger.Warn($"托盘动作未生效：{what} —— {why}");
        TrayIcon?.ShowBalloonTip(6000, what, why, ToolTipIcon.Warning);
    }

    public static void SyncTrayMenu()
    {
        if (_trayMenu == null) return;

        var dispatcher = Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;   // 退出中：调度器已开始关闭，别再切线程改菜单
        if (!dispatcher.CheckAccess())
        {
            // 复用 Dispatch：它同样有 HasShutdownStarted 守卫与回调异常兜底
            Dispatch(SyncTrayMenu);
            return;
        }

        // ── 连接状态：已连接 COM7 / 未连接 / 模拟设备 ────────────────
        bool connected  = Serial.IsOpen;
        bool simulation = Serial.IsSimulation;
        if (_trayStatusItem != null)
        {
            if (simulation)
            {
                _trayStatusItem.Text = "模拟设备";
                _trayStatusItem.ForeColor = System.Drawing.Color.FromArgb(0x67, 0xE8, 0xF9);
            }
            else
            {
                _trayStatusItem.Text = connected ? $"已连接 {Serial.PortName}" : "未连接";
                _trayStatusItem.ForeColor = connected
                    ? System.Drawing.Color.FromArgb(0x22, 0xC5, 0x7A)
                    : System.Drawing.Color.FromArgb(0xEF, 0x44, 0x44);
            }
        }

        // ── 急停状态：正常 / 已急停 ─────────────────────────────────
        bool stopped = Engine.EmergencyStopped;
        if (_trayEStopItem != null)
        {
            _trayEStopItem.Text = stopped ? "急停：已急停" : "急停：正常";
            _trayEStopItem.ForeColor = stopped
                ? System.Drawing.Color.FromArgb(0xF4, 0x72, 0x72)
                : System.Drawing.Color.FromArgb(0x6E, 0xD6, 0xA0);
        }

        // ── 悬浮窗勾选状态 ──────────────────────────────────────────
        if (_trayOverlayItem != null)
            _trayOverlayItem.Checked = Settings.OverlayVisible;
        if (_trayClickThroughItem != null)
            _trayClickThroughItem.Checked = _overlayClickThrough;

        // ── 模式勾选（自动 / 行程 / 自定义）──────────────────────────
        foreach (var (mode, item) in _trayModeItems)
            item.Checked = mode == Engine.ActiveMode;

        // ── 强度百分比 ──────────────────────────────────────────────
        int percent = (int)Math.Round(Engine.IntensityScale * 100);
        if (_trayIntensityLabel != null) _trayIntensityLabel.Text = $"当前: {percent}%";
        if (_trayIntensityMenu  != null) _trayIntensityMenu.Text  = $"强度 ({percent}%)";
    }

    private static void ShowMainWindow()
    {
        if (Current.MainWindow is MainWindow win)
        {
            win.Show();
            win.WindowState = System.Windows.WindowState.Normal;
            win.Activate();
        }
    }

    /// <summary>
    /// 盯住 settings.json：文件一变就把设置读回内存，界面和引擎立刻跟着变，不用重启。
    /// 连续写盘（保存十项设置）靠 600ms 防抖收成一次；自己刚写出去的那份用 LastSaveUtc 跳过。
    /// </summary>
    private static void StartSettingsWatcher()
    {
        try
        {
            string dir = AppSettings.DataDirectory;
            Directory.CreateDirectory(dir);
            _settingsReloadTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            _settingsReloadTimer.Tick += (_, _) =>
            {
                _settingsReloadTimer!.Stop();
                if (!AppSettings.ReloadInto(Settings)) return;
                SettingsReloaded?.Invoke();
                // 「开着同步」这个开关改了要当场生效：外面把 ScreenWatchEnabled 改成 true 就真的开始跟，
                // 改成 false 就停 —— 否则热加载只改了数字，行为还得重启才变。
                try
                {
                    if (Settings.ScreenWatchEnabled)
                    {
                        var svc = ScreenWatchService.Current ?? new ScreenWatchService(Settings);
                        if (!svc.IsRunning) svc.Start();
                        AppLogger.Info("设置热加载：画面同步已开始");
                    }
                    else
                    {
                        ScreenWatchService.Current?.Stop();
                    }
                }
                catch (Exception ex) { AppLogger.Warn("热加载后启停画面同步失败：" + ex.Message); }
            };

            _settingsWatcher = new System.IO.FileSystemWatcher(dir, "settings.json")
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _settingsWatcher.Changed += (_, _) => QueueSettingsReload();
            _settingsWatcher.Created += (_, _) => QueueSettingsReload();
            _settingsWatcher.Renamed += (_, _) => QueueSettingsReload();
            AppLogger.Info($"设置热加载已启用：{AppSettings.SettingsFilePath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("设置热加载没起来（不影响正常使用，只是改文件后要重启才生效）：" + ex.Message);
        }
    }

    private static void QueueSettingsReload()
    {
        // 自己 Save 出来的变动不算：否则「界面里拖滑条 → 存盘 → 重读 → 把拖动中的值顶回去」
        if ((DateTime.UtcNow - AppSettings.LastSaveUtc).TotalSeconds < 2) return;
        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            _settingsReloadTimer?.Stop();
            _settingsReloadTimer?.Start();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;   // 退出中：窗口关闭会触发 IsVisibleChanged，不要再弹气泡
        try { if (_settingsWatcher is not null) { _settingsWatcher.EnableRaisingEvents = false; _settingsWatcher.Dispose(); } } catch { }

        // 退出前把「微调波形」页的手画波形存成草稿，下次启动自动恢复。
        // 控制点 + 一圈时长 + 六轴启用状态 + 平滑插值都要存，缺一项恢复出来的播放状态就是错的。
        try
        {
            if (Engine is not null)
            {
                var waveDraft = new WaveDraft(
                    (bool[])Engine.WfAxisEnabled.Clone(),
                    Engine.CwCycleLen,
                    Engine.UseSmoothCustomInterpolation,
                    Engine.EditWaves);
                if (!WaveDraftStore.Save(waveDraft))
                {
                    AppLogger.Warn("波形草稿保存失败：本次手画波形未落盘，请手动『保存预设』导出。");
                    TrayIcon?.ShowBalloonTip(5000, "波形草稿保存失败",
                        "手画波形没能自动保存，请到「微调波形」页点「保存预设」手动导出，否则下次启动会丢失。",
                        ToolTipIcon.Warning);
                }
            }
        }
        catch (Exception ex) { AppLogger.Warn($"波形草稿保存失败：{ex.Message}"); }

        WebApi?.Dispose();
        RuleEngine?.Dispose();
        GameTelemetry?.Dispose();
        Fanout?.Dispose();
        LibraryWeb?.Dispose();
        AudioReactive?.Dispose();
        ForegroundWatcher?.Dispose();
        GameKeyPoller?.Dispose();
        FunscriptPlayer?.Dispose();
        Bridge?.Dispose();
        TrayIcon?.Dispose();
        _overlay?.Close();
        Engine?.Dispose();
        Settings?.Save();
        Serial?.Dispose();
        Hotkeys?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 服务线程 → UI 线程的统一投递口。
    /// 退出时调度器可能已经开始关闭：这时同步 Invoke 会抛 InvalidOperationException 或挂死，
    /// 所以先看 HasShutdownStarted，已开始关闭就直接丢弃这次 UI 更新。
    /// 回调统一用 try/catch 兜底（记 AppLogger），避免把服务线程/调度器拖垮。
    /// </summary>
    /// <returns>true = 回调已执行或已投递；false = 调度器已开始关闭，回调被丢弃。</returns>
    public static bool Dispatch(Action a)
    {
        if (Current is { Dispatcher: { HasShutdownStarted: false } d })
        {
            if (d.CheckAccess()) { RunGuarded(a); return true; }
            d.BeginInvoke(new Action(() => RunGuarded(a)));
            return true;
        }
        return false;
    }

    private static void RunGuarded(Action a)
    {
        try { a(); }
        catch (Exception ex) { AppLogger.Error("UI 投递回调异常", ex); }
    }

    // ── 诊断自动留档 ─────────────────────────────────────────────────
    private static void OnSerialIncident(string kind) => AutoArchive(kind);

    private static void OnEngineStateChangedForEStop()
    {
        bool estop = Engine.EmergencyStopped;
        bool wasEstop = _lastEStopState;
        _lastEStopState = estop;
        if (estop && !wasEstop)
            AutoArchive("estop");
    }

    /// <summary>限频闸门：同一标签 5 分钟内只打一次包。先占位再后台打包。</summary>
    private static bool TryReserveArchive(string tag)
    {
        long now = Environment.TickCount64;
        if (_autoArchiveLast.TryGetValue(tag, out long last) && now - last < AutoArchiveIntervalMs)
            return false;
        _autoArchiveLast[tag] = now;
        return true;
    }

    /// <summary>串口事件/急停用：后台打包，绝不堵调用线程（可能是串口读线程）。</summary>
    private static void AutoArchive(string tag)
    {
        if (!TryReserveArchive(tag)) return;
        _ = Task.Run(() => DoArchive(tag));
    }

    /// <summary>未处理异常用：同步打包（进程可能马上结束，后台线程来不及跑）。</summary>
    private static void AutoArchiveSync(string tag)
    {
        if (!TryReserveArchive(tag)) return;
        try { DoArchive(tag); } catch { }
    }

    private static void DoArchive(string tag)
    {
        try
        {
            string path = Hexa.Diagnostics.DiagnosticReport.Create(tag);
            if (path.Length == 0) return;
            AppLogger.Info("诊断报告已留存：" + path);
            try
            {
                Dispatch(() => TrayIcon?.ShowBalloonTip(5000, "诊断报告已留存", path, ToolTipIcon.Info));
            }
            catch { }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("诊断自动留档失败：" + ex.Message);
        }
    }
}
