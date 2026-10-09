using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using Hexa.Controls;
using Hexa.Models;
using Hexa.Services;
using Hexa.ViewModels;

namespace Hexa.Views;

public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = App.SettingsVm;

    /// <summary>正文宽度 ≥ 这个值就分两栏；低于它就一栏到底（窄窗并排会把卡片挤窄、还会被边缘切）。</summary>
    private const double TwoColumnWidth = 940;
    private int _settingsLayoutMode = -1;
    /// <summary>轴顺序（唯一真源见 <see cref="Hexa.Services.Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxIds    = Hexa.Services.Osr6DeviceProfile.InstalledAxes;
    private static readonly Color[]  AxColors =
    {
        Color.FromRgb(0xa7,0x8b,0xfa), Color.FromRgb(0x67,0xe8,0xf9),
        Color.FromRgb(0x86,0xef,0xac), Color.FromRgb(0xfb,0xa3,0x4a),
        Color.FromRgb(0xf4,0x72,0x72), Color.FromRgb(0xf9,0xd7,0x2e)
    };
    private readonly RangeSlider[] _limitSliders = new RangeSlider[6];
    // 每行的读数（"最小 x · 最大 y"）：只读文字，放在滑杆正下方。
    // 曾经在行的右侧放过两个可输入的数字框，结果整行太宽、右边的框被挤出可视区，
    // 用户的原话是"边上那个输入去掉""总要滚动才能看另一部分"——这里改成不占宽度的读数。
    private readonly System.Windows.Controls.TextBlock[] _limitRangeTexts = new System.Windows.Controls.TextBlock[6];
    private bool _loadingAxisGrid;
    /// <summary>拖动限位时是否实时驱动设备的安全闸（默认关闭，用户勾选后写入 AppSettings 持久化）。</summary>
    private bool _liveLimitDriveEnabled;
    /// <summary>限位卡的常驻提示（与 XAML 里的初始文案保持一致）。</summary>
    private const string LimitSaveHint = "拖好后点「保存限位」才会记住；不保存就关软件，下次还是原来的范围。";
    // 日志自动刷新（默认关）：只在日志内容真的变了才重建列表，否则会把正在看的那一行滚走。
    private readonly System.Windows.Threading.DispatcherTimer _logAutoTimer =
        new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<string> _lastLogLines = Array.Empty<string>();
    // 拖动限位时的尾随发送：最后一次变更可能正好落在 90ms 限流窗口里被丢掉，
    // 于是「滑到最大但设备没到最大」。停手 140ms 后补发一次最终值。
    private readonly System.Windows.Threading.DispatcherTimer _limitDriveTimer =
        new() { Interval = TimeSpan.FromMilliseconds(140) };
    // 保存/重置限位成功后的 2 秒提示：复用同一个提示行显示 ✅ 摘要，2 秒后恢复原样
    private readonly System.Windows.Threading.DispatcherTimer _limitHintFlashTimer =
        new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _limitHintFlashPreviousText;
    private Brush? _limitHintFlashPreviousBrush;
    private string _pendingDriveAxis = string.Empty;
    private int _pendingDriveValue = -1;
    private bool _loadingOverlaySettings;
    // AI 设置：回填时抑制 TextChanged 误置脏标记 / 三个输入框是否有未保存改动
    private bool _loadingAiSettings;
    private bool _aiDirty;
    private long _lastAxisDragAt;
    private string? _capturingHotkeyAction;
    private Button? _capturingHotkeyButton;

    public SettingsPage()
    {
        InitializeComponent();

        // Connection status
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(_vm.IsConnected) or nameof(_vm.ConnectionStatus))
                UpdateStatus();
        };
        UpdateStatus();

        // Port combo
        PortCombo.SelectionChanged += (_, _) =>
        {
            if (PortCombo.SelectedItem is SerialPortOption option) _vm.Port = option.PortName;
        };
        FallbackPortCombo.SelectionChanged += (_, _) =>
        {
            if (FallbackPortCombo.SelectedItem is SerialPortOption option)
                _vm.FallbackPort = option.PortName;
        };
        ReloadPortCombo();

        AutoConnectCheck.IsChecked = _vm.AutoConnect;
        PreferWiredCheck.IsChecked = _vm.PreferWired;
        GlobalHotkeysCheck.IsChecked = App.Settings.GlobalHotkeysEnabled;
        RefreshStorageFolderTexts();
        AutoConnectCheck.Checked += (_, _) => _vm.AutoConnect = true;
        AutoConnectCheck.Unchecked += (_, _) => _vm.AutoConnect = false;
        PreferWiredCheck.Checked += (_, _) => { _vm.PreferWired = true; ReloadPortCombo(); };
        PreferWiredCheck.Unchecked += (_, _) => _vm.PreferWired = false;
        GlobalHotkeysCheck.Checked += (_, _) => SetGlobalHotkeysEnabled(true);
        GlobalHotkeysCheck.Unchecked += (_, _) => SetGlobalHotkeysEnabled(false);
        UpdatePortState();
        Focusable = true;
        PreviewKeyDown += CaptureHotkey_KeyDown;

        // Build axis limits grid
        _limitDriveTimer.Tick += (_, _) =>
        {
            _limitDriveTimer.Stop();
            FlushPendingAxisDrive();
        };
        _limitHintFlashTimer.Tick += (_, _) =>
        {
            _limitHintFlashTimer.Stop();
            RestoreLimitHint();
        };

        // 限位实时驱动安全闸：默认未勾选 = 只改限位数值，绝不发串口指令。
        _liveLimitDriveEnabled = App.Settings.LimitSliderDriveDevice;
        LiveLimitDriveCheck.IsChecked = _liveLimitDriveEnabled;
        LiveLimitDriveCheck.Checked += (_, _) =>
        {
            _liveLimitDriveEnabled = true;
            App.Settings.LimitSliderDriveDevice = true;
            App.Settings.Save();
            UpdateLimitDriveGate();
            SetLimitDriveHint(LimitSaveHint, danger: false);
        };
        LiveLimitDriveCheck.Unchecked += (_, _) =>
        {
            _liveLimitDriveEnabled = false;
            App.Settings.LimitSliderDriveDevice = false;
            App.Settings.Save();
            // 关闸时丢掉还没补发的尾随值，避免松手 140ms 后仍然发出一次指令。
            _limitDriveTimer.Stop();
            _pendingDriveAxis = string.Empty;
            _pendingDriveValue = -1;
            UpdateLimitDriveGate();
            SetLimitDriveHint("已关掉：拖动只改数字，机器不会动。", danger: false);
        };
        UpdateLimitDriveGate();

        // 日志自动刷新（默认关）：只在内容真的变了才重建列表，免得把正在看的那一行滚走。
        _logAutoTimer.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            RefreshLogIfChanged();
        };
        AutoRefreshLogCheck.Checked += (_, _) =>
        {
            RefreshLog();
            _logAutoTimer.Start();
        };
        AutoRefreshLogCheck.Unchecked += (_, _) => _logAutoTimer.Stop();

        BuildAxisLimitGrid();
        BuildAxisTestPanel();
        BuildHotkeyBindings();
        InitAiSettings();
        InitOverlaySettings();
        InitLogSettings();
        RefreshLog();
        InitAboutCard();
        UpdateFirmwareSummary();   // 固件设置那一行的摘要在构造时就位，别让用户看到一行空白
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _logAutoTimer.Stop();
                return;
            }
            RefreshLog();
            if (AutoRefreshLogCheck.IsChecked == true) _logAutoTimer.Start();
            // 这里原来会弹「AI 设置尚未保存，是否保存？」的模态框 —— 那是个会秒退程序的雷：
            // IsVisibleChanged 是在 WPF 布局 / 应用模板的过程中触发的（栈：ApplyTemplate → Measure
            // → OnIsVisibleChanged），在布局里弹模态框会抛 InvalidOperationException
            //「调度程序处理已暂停，但仍在处理消息」，界面线程直接崩（用户 2026-09-24 快速切页面
            // 踩到，app.log 21:14:22 有完整栈）。现在既不打扰也不丢字：有未保存改动就原样保留
            // 他的输入（不按磁盘值覆盖），存不存由他点「保存 AI 设置」决定，旁边红点一直提示着。
            if (!_aiDirty) InitAiSettings();
            UpdateFirmwareButtons();
            UpdateFirmwareSummary();
            RefreshPortHogs();
            UpdateOverlayStatus();
            UpdateSimulationBanner();
            RefreshFoldSummaries();   // 折叠块的摘要在显示时就填好（以前只在点标题后才填）
        };

        // 拉窗口时跟着换栏数（MeasureOverride 之外再兜一层）
        SizeChanged += (_, _) => ApplySettingsLayout(ActualWidth);
        ApplySettingsLayout(ActualWidth);
    }

    /// <summary>
    /// 设置页分栏：卡片多、每张都不到 520px 宽，单列限宽在宽窗口下左右各空一大片、还要滚很久。
    /// 宽窗口 = 两栏并排（每栏约 570px，滑杆/按钮照样不超过 420px 的横条红线）；
    /// 窄窗口 = 右栏整块搬到左栏下面，一栏到底，不会被窗口边缘切掉。
    /// </summary>
    private void ApplySettingsLayout(double width)
    {
        if (width <= 0 || SettingsColumns is null) return;
        int mode = width >= TwoColumnWidth ? 0 : 1;
        if (mode == _settingsLayoutMode) return;
        _settingsLayoutMode = mode;

        if (mode == 0)
        {
            SettingsLeftCol.Width = new GridLength(1, GridUnitType.Star);
            SettingsGapCol.Width = new GridLength(16);
            SettingsRightCol.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(SettingsRightColumn, 0);
            Grid.SetColumn(SettingsRightColumn, 2);
            Grid.SetColumnSpan(SettingsRightColumn, 1);
            SettingsRightColumn.Margin = new Thickness(0);
        }
        else
        {
            SettingsGapCol.Width = new GridLength(0);
            SettingsRightCol.Width = new GridLength(0);
            Grid.SetRow(SettingsRightColumn, 1);
            Grid.SetColumn(SettingsRightColumn, 0);
            Grid.SetColumnSpan(SettingsRightColumn, 3);
            SettingsRightColumn.Margin = new Thickness(0, 14, 0, 0);
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (!double.IsInfinity(constraint.Width)) ApplySettingsLayout(constraint.Width);
        return base.MeasureOverride(constraint);
    }

    /// <summary>安全闸的状态显示：勾选后整块变红，避免用户忘了自己开过。</summary>
    private void UpdateLimitDriveGate()
    {
        if (LimitDriveGatePanel is null || LimitDriveGateText is null) return;
        if (_liveLimitDriveEnabled)
        {
            LimitDriveGatePanel.Background  = (Brush)FindResource("DangerSoft");
            LimitDriveGatePanel.BorderBrush = (Brush)FindResource("Danger");
            LimitDriveGateText.Foreground   = (Brush)FindResource("Danger");
            LimitDriveGateText.Text =
                "⚠ 已开启：手指一动，机器就跟着动到那个位置。先清空周围、确认没人也没东西会被夹到；"
                + "想立刻停下，点左侧边框的「锁定急停」，或者把这个勾去掉。";
        }
        else
        {
            LimitDriveGatePanel.Background  = (Brush)FindResource("Surface");
            LimitDriveGatePanel.BorderBrush = (Brush)FindResource("Border");
            LimitDriveGateText.Foreground   = (Brush)FindResource("Muted");
            LimitDriveGateText.Text = "现在关着：拖动圆点只改数字，机器不会动。想边拖边找位置，再勾上它。";
        }
    }

    private void InitAiSettings()
    {
        _loadingAiSettings = true;   // 回填期间不要被 TextChanged / PasswordChanged 当成用户编辑
        try
        {
            // API Key 用的是 PasswordBox：取值走 .Password（没有 .Text 这个属性）。
            // Key 在 settings.json 里是 DPAPI 密文（见 Services/SecretProtector.cs）。
            // 这里必须解密后再放进输入框 —— 否则用户看到的是 "dpapi:v1:..." 一串密文，
            // 一按保存就把密文当 Key 存回去。明文（老配置）会被 Unprotect 原样返回，所以兼容。
            AiKeyBox.Password = Hexa.Services.SecretProtector.Unprotect(App.Settings.AiApiKey);
            AiBaseBox.Text = App.Settings.AiApiBase;
            AiModelBox.Text = App.Settings.AiModel;
        }
        finally
        {
            _loadingAiSettings = false;
        }
        _aiDirty = false;
        bool configured = Hexa.Services.SecretProtector.Unprotect(App.Settings.AiApiKey).Length > 0;
        AiStatusText.Text = configured ? "已配置，可以用" : "还没配置，AI 助手暂时用不了";
        AiStatusText.Foreground = (Brush)FindResource(configured ? "Success" : "Warning");
        UpdateAiDirtyHint();
    }

    /// <summary>接口地址 / 模型两个普通输入框被编辑。</summary>
    private void AiBox_TextChanged(object sender, TextChangedEventArgs e) => MarkAiDirty();

    /// <summary>API Key（密码框）被编辑。</summary>
    private void AiKey_PasswordChanged(object sender, RoutedEventArgs e) => MarkAiDirty();

    /// <summary>AI 三个输入框任一被编辑：标记「有未保存改动」，切回本页时不会被磁盘值覆盖。</summary>
    private void MarkAiDirty()
    {
        if (_loadingAiSettings) return;
        _aiDirty = true;
        UpdateAiDirtyHint();
    }

    /// <summary>「有改动还没保存」的小红点：改过就亮，保存或丢弃后灭。</summary>
    private void UpdateAiDirtyHint()
    {
        if (AiDirtyHint is null) return;
        AiDirtyHint.Visibility = _aiDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveAiBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SaveAiSettings()) InitAiSettings();
    }

    /// <summary>把 AI 三个框写进设置并落盘。返回 false = 内存值已改但写盘失败（权限 / 磁盘空间）。</summary>
    private bool SaveAiSettings()
    {
        // 字段名和保存流程都没变，只有 API Key 这个框从 .Text 改成 .Password 取值。
        // 存盘前加密（空串保持空串：不清掉"未配置"的语义）
        string typedKey = AiKeyBox.Password.Trim();
        App.Settings.AiApiKey = typedKey.Length == 0 ? "" : Hexa.Services.SecretProtector.Protect(typedKey);
        App.Settings.AiKeyProtected = typedKey.Length > 0;
        App.Settings.AiApiBase = AiBaseBox.Text.Trim();
        App.Settings.AiModel = AiModelBox.Text.Trim();
        App.Settings.Normalize();
        if (!SaveSettingsOrWarn("AI 设置"))
        {
            AiStatusText.Text = "保存失败：settings.json 写盘出错，当前输入已保留。";
            AiStatusText.Foreground = (Brush)FindResource("Danger");
            return false;
        }
        _aiDirty = false;
        UpdateAiDirtyHint();
        return true;
    }

    /// <summary>写盘并反馈失败：App.Settings.Save() 返回 false = 磁盘写不进去，不能当成已保存。</summary>
    private bool SaveSettingsOrWarn(string what)
    {
        if (App.Settings.Save()) return true;
        System.Windows.MessageBox.Show(
            $"{what}在本次运行中已生效，但写入 settings.json 失败（可能是权限、磁盘空间或文件被占用）。\n重启后这些改动可能丢失，请检查后重试。",
            "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    // ── 悬浮窗设置 ──────────────────────────────────────────────────
    private void InitOverlaySettings()
    {
        _loadingOverlaySettings = true;
        double opacity = Math.Clamp(App.Settings.OverlayOpacity, 0.2, 1.0);
        OverlayOpacitySlider.Value = opacity;
        OverlayOpacityLabel.Text = $"{(int)Math.Round(opacity * 100)}%";
        OverlayShowModeCheck.IsChecked       = App.Settings.OverlayShowMode;
        OverlayShowSpeedCheck.IsChecked      = App.Settings.OverlayShowSpeed;
        OverlayShowIntensityCheck.IsChecked  = App.Settings.OverlayShowIntensity;
        OverlayShowBpmCheck.IsChecked        = App.Settings.OverlayShowBpm;
        OverlayShowConnectionCheck.IsChecked = App.Settings.OverlayShowConnection;
        _loadingOverlaySettings = false;

        // 不透明度：拖动时实时生效（先改设置再刷新悬浮窗），松手才写盘
        OverlayOpacitySlider.ValueChanged += (_, e) =>
        {
            OverlayOpacityLabel.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
            if (_loadingOverlaySettings) return;
            App.Settings.OverlayOpacity = Math.Clamp(e.NewValue, 0.2, 1.0);
            App.RefreshOverlay();
        };
        OverlayOpacitySlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => App.Settings.Save()));

        // 5 个显示开关
        BindOverlayCheck(OverlayShowModeCheck,       value => App.Settings.OverlayShowMode = value);
        BindOverlayCheck(OverlayShowSpeedCheck,      value => App.Settings.OverlayShowSpeed = value);
        BindOverlayCheck(OverlayShowIntensityCheck,  value => App.Settings.OverlayShowIntensity = value);
        BindOverlayCheck(OverlayShowBpmCheck,        value => App.Settings.OverlayShowBpm = value);
        BindOverlayCheck(OverlayShowConnectionCheck, value => App.Settings.OverlayShowConnection = value);

        UpdateOverlayStatus();
    }

    /// <summary>复选框 ↔ 设置项：勾选变化立即刷新悬浮窗并落盘。</summary>
    private void BindOverlayCheck(CheckBox box, Action<bool> setter)
    {
        box.Checked   += (_, _) => { setter(true);  OnOverlayOptionChanged(); };
        box.Unchecked += (_, _) => { setter(false); OnOverlayOptionChanged(); };
    }

    private void OnOverlayOptionChanged()
    {
        if (_loadingOverlaySettings) return;
        App.RefreshOverlay();
        // 悬浮窗开关每次点击都落盘：写盘失败用状态文字提示，避免连点时弹一串对话框。
        if (!App.Settings.Save())
            OverlayStatusText.Text = "⚠ 设置已生效，但写入 settings.json 失败，重启后可能丢失。";
    }

    private void OverlayToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        App.ToggleOverlay();
        UpdateOverlayStatus();
    }

    private void UpdateOverlayStatus()
    {
        bool visible = App.Settings.OverlayVisible;
        OverlayToggleBtn.Content = visible ? "隐藏悬浮窗" : "显示悬浮窗";
        // 与悬浮窗那页的措辞对齐：✕ 只是「收起」，不是退出，设备照常运行。
        OverlayToggleBtn.ToolTip = visible
            ? "只是把小窗收起来，不影响设备运行；随时可以再显示回来"
            : "把小窗显示到桌面上";
        OverlayStatusText.Text   = visible ? "悬浮窗已显示" : "悬浮窗已收起";
    }

    // ── 日志查看 ────────────────────────────────────────────────────
    /// <summary>
    /// 「日志里记不记窗口标题」（默认关＝只记进程名）。回填在挂事件之前，
    /// 所以回填本身不会触发保存；改动立即落盘，下次开始「画面观察」时生效。
    /// </summary>
    private void InitLogSettings()
    {
        if (LogWindowTitlesCheck is null) return;
        LogWindowTitlesCheck.IsChecked = App.Settings.LogWindowTitles;
        LogWindowTitlesCheck.Checked   += (_, _) => SetLogWindowTitles(true);
        LogWindowTitlesCheck.Unchecked += (_, _) => SetLogWindowTitles(false);
    }

    private void SetLogWindowTitles(bool enabled)
    {
        App.Settings.LogWindowTitles = enabled;
        SaveSettingsOrWarn("日志设置");
        // 不重填复选框：写盘失败时设置已经在内存里生效了，把勾改回去反而更让人糊涂（与悬浮窗那组一致）。
    }

    /// <summary>重新读日志文件并刷新列表（用户点「刷新」或刚切到本页时用）。</summary>
    private void RefreshLog()
    {
        _lastLogLines = AppLogger.ReadRecentLines(200);
        LogList.ItemsSource = _lastLogLines;
        LogCountText.Foreground = (Brush)FindResource("Muted");
        LogCountText.Text = _lastLogLines.Count == 0
            ? "还没有日志。用过软件之后这里就会有内容。"
            : $"显示最近 {_lastLogLines.Count} 条（只保留最新 200 条）";
        UpdateLogSizeText();
    }

    /// <summary>
    /// 「日志现在多大 / 上限多少」：把轮转规则也写出来，用户才知道写满了旧内容去哪了
    /// （app.log → app.log.1，只留一代）。
    /// </summary>
    private void UpdateLogSizeText()
    {
        if (LogSizeText is null) return;
        long bytes = AppLogger.CurrentBytes();
        string size = bytes < 1024 ? $"{bytes} 字节"
                    : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB"
                    : $"{bytes / 1024.0 / 1024.0:0.##} MB";
        int limitMb = App.Settings.LogMaxMegabytes;
        LogSizeText.Text = (limitMb <= 0
            ? $"日志现在 {size}；上限已关掉，会一直变大，建议定期点「清空」。"
            : $"日志现在 {size}，上限 {limitMb} MB：写满自动存成上一代 app.log.1（只留一代）。")
            + (File.Exists(AppLogger.RotatedLogFilePath)
                ? "更早的记录就在同一目录的 app.log.1 里。"
                : "");
    }

    /// <summary>自动刷新：内容没变就什么都不做，避免把正在看的那一行滚走。</summary>
    private void RefreshLogIfChanged()
    {
        var lines = AppLogger.ReadRecentLines(200);
        UpdateLogSizeText();
        if (lines.Count == _lastLogLines.Count && lines.SequenceEqual(_lastLogLines)) return;
        _lastLogLines = lines;
        LogList.ItemsSource = lines;
        LogCountText.Foreground = (Brush)FindResource("Muted");
        LogCountText.Text = lines.Count == 0
            ? "还没有日志。用过软件之后这里就会有内容。"
            : $"显示最近 {lines.Count} 条（只保留最新 200 条）";
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

    /// <summary>把最近的日志整段复制到剪贴板，方便贴给别人看。</summary>
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = AppLogger.ReadRecentLines(500);
        if (lines.Count == 0)
        {
            LogCountText.Foreground = (Brush)FindResource("Warning");
            LogCountText.Text = "还没有日志，没有可复制的内容。";
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
            LogCountText.Foreground = (Brush)FindResource("Success");
            LogCountText.Text = $"已复制 {lines.Count} 条日志到剪贴板。";
        }
        catch (Exception ex)
        {
            LogCountText.Foreground = (Brush)FindResource("Danger");
            LogCountText.Text = "复制失败：" + ex.Message;
        }
    }

    /// <summary>用系统默认程序打开完整日志文件（文件不存在就打开它所在的目录）。</summary>
    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = AppLogger.LogFilePath;
            string folder = Path.GetDirectoryName(path) ?? "";
            Directory.CreateDirectory(folder);
            string target = File.Exists(path) ? path : folder;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"打不开日志文件：{ex.Message}", "日志查看",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 清空日志：当前 app.log 和上一代 app.log.1 一起删掉（不可恢复，所以先问一次）。
    /// 只在用户明确点「清空」时才删 —— 日志是排查问题唯一的线索，不能自己悄悄清。
    /// </summary>
    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                "清空会把 app.log 和上一代 app.log.1 一起删掉，删了就找不回来了。\n\n"
                + "排查问题的时候日志是唯一的线索，确认不要了再删。",
                "清空日志", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        bool ok = AppLogger.Clear();
        RefreshLog();                 // 列表先按现在的磁盘状态刷新（清成功就是空的）
        LogCountText.Foreground = (Brush)FindResource(ok ? "Success" : "Danger");
        LogCountText.Text = ok
            ? "日志已清空。"
            : "没删干净：日志文件正被别的程序占着（比如记事本开着它），关掉那个程序再试一次。";
    }

    private void SetGlobalHotkeysEnabled(bool enabled)
    {
        App.Settings.GlobalHotkeysEnabled = enabled;
        App.Hotkeys.SetEnabled(enabled);
        SaveSettingsOrWarn("全局热键设置");
        UpdateHotkeyStatus();
    }

    /// <summary>把七条命令的热键改回出厂设置（会覆盖用户自己改过的组合键，所以先问一句）。</summary>
    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                "七条命令的热键都改回出厂设置（例如「播放 / 暂停」= Ctrl+Alt+Insert）。\n"
                + "你自己改过的组合键会被覆盖。\n\n继续吗？",
                "恢复默认热键", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        App.Settings.Hotkeys = new HotkeyConfig();
        SaveSettingsOrWarn("热键绑定");
        App.Hotkeys.SetBindings(App.Settings.Hotkeys);
        BuildHotkeyBindings();
    }

    private void BuildHotkeyBindings()
    {
        HotkeyBindingsPanel.Children.Clear();
        IReadOnlyDictionary<string, string> errors = App.Hotkeys.RegistrationErrors;
        foreach ((string action, string label) in HotkeyConfig.Actions)
        {
            // 每行 = 一行输入 +（可选）一行说明：冲突 / 已清除都在这行讲清楚，不用去猜。
            var rowStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string binding = App.Settings.Hotkeys.Get(action);
            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var bindingBox = new TextBox
            {
                Text = binding,
                IsReadOnly = true,
                Height = 28,
                FontSize = 11,
                Margin = new Thickness(0, 0, 7, 0),
                ToolTip = "点右边「录入」，再直接按下想用的组合键",
            };
            var capture = new Button
            {
                Content = "录入",
                Tag = action,
                Width = 54,
                Height = 28,
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "点一下，然后按下组合键（Esc 取消）",
            };
            var clear = new Button
            {
                Content = "清除",
                Width = 48,
                Height = 28,
                FontSize = 11,
                ToolTip = "取消这条命令的热键；页面上的按钮照样能点",
            };
            capture.Click += (_, _) => BeginHotkeyCapture(action, capture);
            clear.Click += (_, _) => SaveHotkeyBinding(action, "");

            Grid.SetColumn(bindingBox, 1);
            Grid.SetColumn(capture, 2);
            Grid.SetColumn(clear, 3);
            row.Children.Add(labelBlock);
            row.Children.Add(bindingBox);
            row.Children.Add(capture);
            row.Children.Add(clear);
            rowStack.Children.Add(row);

            var note = new TextBlock
            {
                FontSize = 10,
                Margin = new Thickness(104, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            if (errors.TryGetValue(action, out string? error) && !string.IsNullOrWhiteSpace(error))
            {
                note.Text = $"⚠ 这个组合键装不上：{error}。换一个键，或关掉占着它的软件后重新录入。";
                note.Foreground = (Brush)FindResource("Danger");
                bindingBox.BorderBrush = (Brush)FindResource("Danger");
                rowStack.Children.Add(note);
            }
            else if (string.IsNullOrWhiteSpace(binding))
            {
                note.Text = "这条命令现在没有热键。";
                note.Foreground = (Brush)FindResource("Faint");
                rowStack.Children.Add(note);
            }
            HotkeyBindingsPanel.Children.Add(rowStack);
        }
        UpdateHotkeyStatus();
    }

    private void BeginHotkeyCapture(string action, Button button)
    {
        if (_capturingHotkeyButton != null)
        {
            _capturingHotkeyButton.Content = "录入";
            _capturingHotkeyButton.Style = (Style)FindResource("BtnSecondary");
        }
        _capturingHotkeyAction = action;
        _capturingHotkeyButton = button;
        button.Content = "按下键";
        button.Style = (Style)FindResource("BtnPrimary");
        HotkeyStatusText.Foreground = (Brush)FindResource("Accent");
        HotkeyStatusText.Text = "现在按下想用的组合键（例如 Ctrl+Alt+P）；按 Esc 取消，什么都不改。";
        Focus();
        Keyboard.Focus(this);
    }

    private void CaptureHotkey_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_capturingHotkeyAction == null) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            FinishHotkeyCapture();
            UpdateHotkeyStatus();
            e.Handled = true;
            return;
        }
        if (!HotkeyService.IsBindableKey(key)) return;

        string binding = HotkeyService.FormatBinding(Keyboard.Modifiers, key);
        string action = _capturingHotkeyAction;
        FinishHotkeyCapture();
        SaveHotkeyBinding(action, binding);
        e.Handled = true;
    }

    private void FinishHotkeyCapture()
    {
        if (_capturingHotkeyButton != null)
        {
            _capturingHotkeyButton.Content = "录入";
            _capturingHotkeyButton.Style = (Style)FindResource("BtnSecondary");
        }
        _capturingHotkeyAction = null;
        _capturingHotkeyButton = null;
    }

    private void SaveHotkeyBinding(string action, string binding)
    {
        App.Settings.Hotkeys.Set(action, binding);
        SaveSettingsOrWarn("热键绑定");
        App.Hotkeys.SetBindings(App.Settings.Hotkeys);
        BuildHotkeyBindings();
    }

    private void UpdateHotkeyStatus()
    {
        // 总开关关着时把整块调暗，一眼能看出「这些键现在不生效」。
        HotkeyBindingsPanel.Opacity = App.Settings.GlobalHotkeysEnabled ? 1.0 : 0.5;
        if (!App.Settings.GlobalHotkeysEnabled)
        {
            HotkeyStatusText.Foreground = (Brush)FindResource("Muted");
            HotkeyStatusText.Text = "全局热键已关闭：下面录好的组合键暂时都不生效。";
            return;
        }
        if (App.Hotkeys.RegistrationErrors.Count == 0)
        {
            HotkeyStatusText.Foreground = (Brush)FindResource("Muted");
            HotkeyStatusText.Text = "热键已生效，在别的软件里也管用。";
            return;
        }
        // 用命令的中文名，别把内部英文 id 摆给用户看。
        HotkeyStatusText.Foreground = (Brush)FindResource("Danger");
        HotkeyStatusText.Text = "有热键没装上 —— " + string.Join("；", App.Hotkeys.RegistrationErrors
            .Select(item => $"{HotkeyLabel(item.Key)}：{item.Value}"));
    }

    /// <summary>把热键的内部 id 换成中文命令名（找不到时原样返回）。</summary>
    private static string HotkeyLabel(string action) =>
        HotkeyConfig.Actions.FirstOrDefault(item => item.Action == action).Label ?? action;

    private void UpdateStatus()
    {
        StatusDot.Fill = (Brush)FindResource(!_vm.IsConnected ? "Danger" : _vm.IsArmed ? "Success" : "Warning");
        StatusLabel.Text = _vm.ConnectionStatus;
        StatusLabel.ToolTip = _vm.ConnectionStatus;   // 文字被截断时鼠标停一下能看全
        UpdateConnectButton();
        UpdateSimulationBanner();
        RefreshPortHogs();
    }

    /// <summary>检测并列出正在占用串口的已知程序，每个给一个“关闭”按钮。</summary>
    // ── 固件设置（TCodeESP32 的 #setting / $save / #restart）─────────────────
    // 三件事必须一直成立：① 串口协议读不到设备当前值（固件只在它自己的网页接口里给值），
    // 所以界面填的是「要改成多少」、并在文案里说清楚，绝不假装框里是现状；
    // ② 只管设置，一条会动设备的命令都不发（白名单在 SerialService.IsAllowedFirmwareCommand）；
    // ③ 读列表要在后台线程做 —— 那是一次最长 2 秒的阻塞串口 I/O，放 UI 线程会冻住界面。

    private sealed class FirmwareRow
    {
        public required string Name { get; init; }
        public required FirmwareSettingKind Kind { get; init; }
        public System.Windows.Controls.TextBox? Box { get; init; }
        public System.Windows.Controls.ComboBox? Choice { get; init; }

        /// <summary>用户填的值：文本框直接取；布尔用下拉的「开/关」；没填 = 空串（跳过这一项）。</summary>
        public string Entered => Box is not null
            ? Box.Text.Trim()
            : Choice?.SelectedIndex switch { 1 => "1", 2 => "0", _ => "" };
    }

    private readonly List<FirmwareRow> _firmwareRows = new();
    private int _firmwareKnown;
    private int _firmwareUnsaved;

    private void StorageFold_Click(object sender, RoutedEventArgs e)
    {
        bool expand = StorageFoldBody.Visibility != Visibility.Visible;
        StorageFoldBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        StorageFoldTitle.Text = (expand ? "▾" : "▸") + " 📁 存储位置";
        StorageFoldSummary.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
        if (!expand) UpdateStorageSummary();
    }

    /// <summary>收起时把当前的合成/脚本目录写一行（不用展开就知道文件放哪）。</summary>
    private void UpdateStorageSummary()
    {
        if (StorageFoldSummary is null) return;
        string composition = CompositionFolderText?.Text ?? "";
        string scripts = ScriptFolderText?.Text ?? "";
        StorageFoldSummary.Text = composition.Length + scripts.Length == 0
            ? "动作合成与脚本库的存放位置"
            : $"合成 {composition} · 脚本 {scripts}";
    }

    private void AboutFold_Click(object sender, RoutedEventArgs e)
    {
        bool expand = AboutFoldBody.Visibility != Visibility.Visible;
        AboutFoldBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        AboutFoldTitle.Text = (expand ? "▾" : "▸") + " ℹ️ 关于与维护";
        AboutFoldSummary.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LimitsFold_Click(object sender, RoutedEventArgs e)
    {
        bool expand = LimitsFoldBody.Visibility != Visibility.Visible;
        LimitsFoldBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        LimitsFoldTitle.Text = (expand ? "▾" : "▸") + " 🎚 轴限位";
        LimitsFoldSummary.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
        UpdateLimitsSummary();
    }

    /// <summary>收起状态下把当前限位写成一行（默认全程时明说"六轴都是全程"，省得他点开确认）。</summary>
    /// <summary>构建/显示时就把折叠摘要填上（之前只在点击折叠标题时才填，打开设置页时摘要一直是空白）。</summary>
    private void RefreshFoldSummaries()
    {
        UpdateLimitsSummary();
        UpdateStorageSummary();
    }

    private void UpdateLimitsSummary()
    {
        if (LimitsFoldSummary is null) return;
        var cfg = App.Settings;
        var parts = new List<string>();
        bool allFull = true;
        foreach (string axis in Hexa.Services.Osr6DeviceProfile.InstalledAxes)
        {
            int min = cfg.AxisMin.TryGetValue(axis, out int lo) ? lo : 0;
            int max = cfg.AxisMax.TryGetValue(axis, out int hi) ? hi : 9999;
            if (min != 0 || max != 9999) allFull = false;
            parts.Add($"{axis} {min}–{max}");
        }
        string drive = App.Settings.LimitSliderDriveDevice ? "    ⚠ 拖动即驱动设备：已开" : "";
        LimitsFoldSummary.Text = (allFull ? "六轴都是全程 0–9999（不限位）" : string.Join(" · ", parts)) + drive;
    }

    private void FirmwareFold_Click(object sender, RoutedEventArgs e)
    {
        bool expand = FirmwareFoldBody.Visibility != Visibility.Visible;
        FirmwareFoldBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        FirmwareFoldTitle.Text = expand ? "▾ 🧩 固件设置（进阶）" : "▸ 🧩 固件设置（进阶）";
        // 收起时右侧那行摘要才有意义（展开后下面全是具体设置，摘要就是重复信息）。
        FirmwareFoldSummary.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
        if (expand) UpdateFirmwareButtons();
        UpdateFirmwareSummary();
    }

    private void UpdateFirmwareSummary()
    {
        if (FirmwareFoldSummary is null) return;
        FirmwareFoldSummary.Text = _firmwareKnown == 0
            ? "还没读设备（固件要支持 #setting 才能改）"
            : $"读到 {_firmwareKnown} 项 · 未保存 {_firmwareUnsaved} 项";
    }

    /// <summary>按钮可用性 + 状态行：没连上（或输出被急停锁着）时写清为什么不能改。</summary>
    private void UpdateFirmwareButtons()
    {
        if (FirmwareReadBtn is null) return;   // 构造期极端情况下控件还没建好
        bool open = App.Serial.IsOpen && App.Serial.OutputEnabled;
        FirmwareReadBtn.IsEnabled = true;      // 读取不写设备：没连上就是读不到，按钮不用灰
        FirmwareApplyBtn.IsEnabled = open;
        FirmwareSaveBtn.IsEnabled = open;
        FirmwareRestartBtn.IsEnabled = open;
        if (!open && FirmwareStatusText.Text.Length == 0)
            FirmwareStatusText.Text = "设备没连上（或输出被急停锁着）：连上以后才能改固件设置。";
    }

    private async void FirmwareRead_Click(object sender, RoutedEventArgs e)
    {
        FirmwareReadBtn.IsEnabled = false;
        FirmwareStatusText.Foreground = (Brush)FindResource("Muted");
        FirmwareStatusText.Text = "正在读设备…（最多等 2 秒）";
        string raw;
        try
        {
            raw = await Task.Run(() => App.Serial.QueryFirmwareCommand("#list-settings", 2200));
        }
        catch (Exception ex)
        {
            raw = "";
            AppLogger.Warn("读取固件设置列表失败: " + ex.Message);
        }
        finally
        {
            FirmwareReadBtn.IsEnabled = true;
        }

        IReadOnlyList<FirmwareSetting> all = FirmwareSettingsService.ParseSettingList(raw);
        _firmwareKnown = all.Count;
        BuildFirmwareRows(all);
        if (all.Count == 0)
        {
            FirmwareStatusText.Foreground = (Brush)FindResource("Warning");
            FirmwareStatusText.Text =
                "设备没回话。可能原因：这台固件不是 TCodeESP32 系（不认识 #list-settings 命令）、"
                + "串口被别的程序占着、或者输出正被急停锁着。先点上面的「读取设备信息」确认串口通不通。";
        }
        else
        {
            FirmwareStatusText.Foreground = (Brush)FindResource("Success");
            FirmwareStatusText.Text = $"读到 {all.Count} 项设置。填好要改的值 →「应用改动」→「保存到设备」。";
        }
        UpdateFirmwareButtons();
        UpdateFirmwareSummary();
    }

    private void BuildFirmwareRows(IReadOnlyList<FirmwareSetting> all)
    {
        _firmwareRows.Clear();
        FirmwareCommonPanel.Children.Clear();
        FirmwareOthersPanel.Children.Clear();
        if (all.Count == 0)
        {
            FirmwareOthersHint.Visibility = Visibility.Collapsed;
            FirmwareOthersScroll.Visibility = Visibility.Collapsed;
            return;
        }

        (IReadOnlyList<FirmwareSetting> common, IReadOnlyList<FirmwareSetting> others) =
            FirmwareSettingsService.SplitByCurated(all);
        foreach (FirmwareSetting setting in common)
            FirmwareCommonPanel.Children.Add(BuildFirmwareRow(setting, isCommon: true));

        FirmwareOthersHint.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FirmwareOthersScroll.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FirmwareOthersHint.Text = others.Count > 0
            ? $"设备固件列出的其它 {others.Count} 项（名字是固件里的原名，改法同上面）："
            : "";
        foreach (FirmwareSetting setting in others)
            FirmwareOthersPanel.Children.Add(BuildFirmwareRow(setting, isCommon: false));
    }

    /// <summary>一行设置：左边中文名 + 原名/提示，右边输入框（布尔用「不改/开/关」下拉）。</summary>
    private FrameworkElement BuildFirmwareRow(FirmwareSetting setting, bool isCommon)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, isCommon ? 7 : 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(isCommon ? 220 : 190) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        bool hasMeta = FirmwareSettingsService.CuratedSr6Settings.TryGetValue(
            setting.Name, out (string Label, string Hint) meta);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        label.Children.Add(new TextBlock
        {
            Text = hasMeta ? meta.Label : setting.Name,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        label.Children.Add(new TextBlock
        {
            Text = hasMeta
                ? $"{setting.Name} · {meta.Hint}"
                : $"{setting.Name} · {FirmwareSettingsService.FillHint(setting.Kind)}",
            FontSize = 10,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        if (setting.Kind == FirmwareSettingKind.Boolean)
        {
            var choice = new ComboBox
            {
                Height = 26,
                FontSize = 12,
                MinWidth = 132,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "「不改」= 这一项不动；选开/关后点「应用改动」才发出去",
            };
            choice.Items.Add(new ComboBoxItem { Content = "不改" });
            choice.Items.Add(new ComboBoxItem { Content = "开（1）" });
            choice.Items.Add(new ComboBoxItem { Content = "关（0）" });
            choice.SelectedIndex = 0;
            Grid.SetColumn(choice, 1);
            row.Children.Add(choice);
            _firmwareRows.Add(new FirmwareRow { Name = setting.Name, Kind = setting.Kind, Choice = choice });
        }
        else
        {
            var box = new TextBox
            {
                Height = 26,
                FontSize = 12,
                MinWidth = 160,
                MaxWidth = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = FirmwareSettingsService.FillHint(setting.Kind),
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            _firmwareRows.Add(new FirmwareRow { Name = setting.Name, Kind = setting.Kind, Box = box });
        }
        return row;
    }

    private void FirmwareApply_Click(object sender, RoutedEventArgs e)
    {
        var applied = new List<string>();
        var failed = new List<string>();
        foreach (FirmwareRow row in _firmwareRows)
        {
            string value = row.Entered;
            if (value.Length == 0) continue;                       // 没填 = 不动这一项
            string? problem = FirmwareSettingsService.Validate(row.Kind, value);
            if (problem is not null) { failed.Add($"{row.Name}（{problem}）"); continue; }
            if (App.Serial.SendFirmwareCommand($"#setting:{row.Name}:{value}")) applied.Add($"{row.Name}={value}");
            else failed.Add($"{row.Name}（发不出去：串口没开或被锁着）");
        }

        _firmwareUnsaved += applied.Count;
        FirmwareStatusText.Foreground = (Brush)FindResource(applied.Count > 0 ? "Success" : "Warning");
        if (applied.Count == 0 && failed.Count == 0)
            FirmwareStatusText.Text = "没有要应用的改动：上面的输入框都是空的，填了值再点这个按钮。";
        else
            FirmwareStatusText.Text =
                (applied.Count > 0
                    ? $"已应用到设备 {applied.Count} 项：{string.Join("、", applied)}。还没写进设备的 Flash，记得点「保存到设备」。"
                    : "一项都没发出去。")
                + (failed.Count > 0 ? $"　没成功 {failed.Count} 项：{string.Join("、", failed)}" : "");
        UpdateFirmwareSummary();
    }

    private void FirmwareSave_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Serial.SendFirmwareCommand("$save"))
        {
            FirmwareStatusText.Foreground = (Brush)FindResource("Warning");
            FirmwareStatusText.Text = "保存失败：串口没开、输出被锁着，或者设备没接收。";
            return;
        }
        _firmwareUnsaved = 0;
        FirmwareStatusText.Foreground = (Brush)FindResource("Success");
        FirmwareStatusText.Text = "已让设备把设置写进自己的 Flash（$save）。提示要重启的项，点「重启设备」才生效。";
        UpdateFirmwareSummary();
    }

    private void FirmwareRestart_Click(object sender, RoutedEventArgs e)
    {
        // 重启 = 串口断开几秒、正在跑的动作会中断，是会"看得见后果"的操作，先问一句。
        if (System.Windows.MessageBox.Show(
                "重启设备固件？\n\n正在播放的动作、正在跑的自动模式都会中断，串口会断开几秒（之后 Hexa 会自动重连）。",
                "重启设备", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        if (!App.Serial.SendFirmwareCommand("#restart"))
        {
            FirmwareStatusText.Foreground = (Brush)FindResource("Warning");
            FirmwareStatusText.Text = "重启命令发不出去：串口没开或被锁着。";
            return;
        }
        FirmwareStatusText.Foreground = (Brush)FindResource("Success");
        FirmwareStatusText.Text = "已发送重启命令：串口会断开几秒，之后自动重连；设置要先「保存到设备」才不会丢。";
    }

    private void RefreshPortHogs()
    {
        IReadOnlyList<string> hogs = SerialService.DetectCompetingApps();
        if (hogs.Count == 0)
        {
            PortHogPanel.Visibility = Visibility.Collapsed;
            PortHogButtons.Children.Clear();
            return;
        }
        PortHogPanel.Visibility = Visibility.Visible;
        PortHogTitle.Text = $"检测到可能占用串口的程序：{string.Join("、", hogs)}";
        PortHogButtons.Children.Clear();
        foreach (string hog in hogs)
        {
            string name = hog;
            var button = new Button
            {
                Content = $"关闭 {name}",
                Height = 28,
                FontSize = 11,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 6),
                Style = (Style)FindResource("BtnDanger"),
            };
            button.Click += (_, _) => StopPortHog(name);
            PortHogButtons.Children.Add(button);
        }
    }

    private void StopPortHog(string name)
    {
        MessageBoxResult confirm = System.Windows.MessageBox.Show(
            $"确定要结束 {name} 吗？\n\n它可能正在播放/运行，未保存的进度会丢失。\n关闭后串口需要几秒才释放，稍等再点“立即连接”。",
            "关闭占用串口的程序", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (SerialService.TryStopCompetingApp(name))
        {
            PortHogTitle.Text = $"✅ 已结束 {name}，等 2~3 秒后点“立即连接”重试。";
            // 给进程一点时间释放串口，再重新检测。
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { timer.Stop(); RefreshPortHogs(); };
            timer.Start();
        }
        else
        {
            System.Windows.MessageBox.Show(
                $"结束 {name} 失败，可能需要管理员权限，或它已自行退出。",
                "关闭失败", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshPortHogs();
        }
    }

    private void UpdatePortState()
    {
        // 「模拟设备」开关已按用户要求移除：串口相关控件始终可用。
        PortCombo.IsEnabled = true;
        FallbackPortCombo.IsEnabled = true;
        RefreshBtn.IsEnabled = true;
        UpdateConnectButton();   // 已连接时按钮显示「断开连接」
    }

    /// <summary>
    /// 连接按钮的两种形态：未连接 = 「立即连接」，已连接 = 「断开连接」（见 ConnectBtn_Click）。
    /// 模拟设备下 Serial.IsOpen 也是 true，所以按钮同样显示「断开连接」——点它等于退出模拟并断开。
    /// </summary>
    private void UpdateConnectButton()
    {
        bool connected = _vm.IsConnected;
        ConnectBtn.Content = connected ? "断开连接" : "立即连接";
        ConnectBtn.Style   = (Style)FindResource(connected ? "BtnDanger" : "BtnPrimary");
    }

    /// <summary>
    /// 「模拟设备」状态条：只在 Serial.IsSimulation=true 时显示。
    /// 设置页已按用户要求移除模拟开关，所以这是 settings.json 里 SimulationMode=true 时唯一的退出入口。
    /// </summary>
    private void UpdateSimulationBanner() =>
        SimulationBanner.Visibility = App.Serial.IsSimulation ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>点「模拟设备」状态条：退出模拟（复用 SettingsViewModel.ExitSimulation 的既有逻辑）。</summary>
    private void ExitSimulation_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.ExitSimulation()) SaveSettingsOrWarn("退出模拟设备");
        UpdateStatus();
        UpdatePortState();
    }

    /// <summary>
    /// 轴限位：每个轴一条轨道 + 两个旋钮（左 = 最小、右 = 最大），读数在滑杆正下方。
    /// 拖动任一旋钮时（只有安全闸打开才）设备会实时移到该位置，方便找机械边界。
    /// </summary>
    private void BuildAxisLimitGrid()
    {
        // 重建（重置限位、校准向导返回）前先丢掉待补发的旧值，
        // 否则 140ms 后会把上一轮滑块的旧值补发出去、把轴莫名挪走。
        _limitDriveTimer.Stop();
        _pendingDriveAxis = string.Empty;
        _pendingDriveValue = -1;

        AxisLimitGrid.RowDefinitions.Clear();
        AxisLimitGrid.ColumnDefinitions.Clear();
        AxisLimitGrid.Children.Clear();

        AxisLimitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        // 滑杆列限宽 400：再宽就成「横跨整屏的长条」了（卡片本身限宽 520）。
        AxisLimitGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MaxWidth = 400,
        });

        AxisLimitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Hdr(string text, int col, HorizontalAlignment align)
        {
            var header = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = (Brush)FindResource("Faint"),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = align,
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, col);
            AxisLimitGrid.Children.Add(header);
        }
        Hdr("轴", 0, HorizontalAlignment.Left);
        Hdr("左边圆点 = 最小，右边圆点 = 最大；读数在滑杆下面", 1, HorizontalAlignment.Left);

        int[] minVals = AxIds.Select(id => App.Settings.AxisMin.TryGetValue(id, out int value) ? value : 0).ToArray();
        int[] maxVals = AxIds.Select(id => App.Settings.AxisMax.TryGetValue(id, out int value) ? value : 9999).ToArray();

        _loadingAxisGrid = true;
        for (int i = 0; i < 6; i++)
        {
            AxisLimitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = i + 1;

            var axisLabel = new TextBlock
            {
                Text = AxIds[i],
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(AxColors[i]),
                // 顶端对齐：让轴名和它自己的滑杆站在同一行上，读数在下面，不会串到下一个轴。
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 6, 10),
            };
            Grid.SetRow(axisLabel, row);
            Grid.SetColumn(axisLabel, 0);
            AxisLimitGrid.Children.Add(axisLabel);

            var slider = new RangeSlider
            {
                Minimum = 0,
                Maximum = 9999,
                // 注意：这里**不能**在初始化器里写 LowerValue/UpperValue。
                // RangeSlider 的 CoerceLowerValue 会按"当前 UpperValue"夹下限，而初始化器按书写顺序执行，
                // UpperValue 还是默认值 100 —— 于是保存过的下限（比如 3000）会被静默夹成 100，
                // 用户一按保存就把自己的轴限位改宽了。下面显式先设上限、再设下限。
                SmallChange = 25,
                // 滚轮微调关闭：鼠标滚轮扫过时不得改限位（更不能驱动设备）。
                // 事件不标记 Handled，会冒泡给外层 ScrollViewer 正常滚动页面。
                IsWheelAdjustEnabled = false,
                ToolTip = "拖动左右两个圆点分别设最小 / 最大；上面那个开关勾上后，拖动才同时驱动设备",
            };

            // 读数就放在滑杆正下方：整行只有"轴名 + 滑杆"两列，不会再宽到把右边的框挤出可视区。
            var rangeText = new TextBlock
            {
                FontSize = 11,
                Foreground = (Brush)FindResource("Muted"),
                Margin = new Thickness(0, 2, 10, 10),
            };
            var sliderColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sliderColumn.Children.Add(slider);
            sliderColumn.Children.Add(rangeText);
            Grid.SetRow(sliderColumn, row); Grid.SetColumn(sliderColumn, 1);
            AxisLimitGrid.Children.Add(sliderColumn);

            // 先上限、后下限：CoerceLowerValue 依赖当前的 UpperValue（见上面的说明）。
            slider.UpperValue = maxVals[i];
            slider.LowerValue = minVals[i];

            _limitSliders[i] = slider;
            _limitRangeTexts[i] = rangeText;

            int index = i;
            // 抓住旋钮（哪怕值没变）也把设备驱动到该位置：
            // 默认限位就是 0–9999，旋钮本来停在两端，只靠 ValueChanged 会“拖了没反应”。
            slider.ValueGrabbed += (_, value) =>
            {
                UpdateLimitLabel(index);
                DriveAxisFromSlider(AxIds[index], (int)Math.Round(value));
            };
            slider.LowerValueChanged += (_, _) =>
            {
                UpdateLimitLabel(index);
                DriveAxisFromSlider(AxIds[index], (int)Math.Round(slider.LowerValue));
            };
            slider.UpperValueChanged += (_, _) =>
            {
                UpdateLimitLabel(index);
                DriveAxisFromSlider(AxIds[index], (int)Math.Round(slider.UpperValue));
            };

            // 初次建行时读数就是空的：这里补一次，否则要等用户拖一下才出现数字。
            UpdateLimitLabel(index);
        }
        _loadingAxisGrid = false;
    }

    /// <summary>把滑杆当前值刷新到滑杆下方的读数（"最小 x · 最大 y"）。</summary>
    private void UpdateLimitLabel(int index)
    {
        if (_limitSliders[index] is null) return;
        if (_limitRangeTexts[index] is not { } text) return;
        int lower = (int)Math.Round(_limitSliders[index].LowerValue);
        int upper = (int)Math.Round(_limitSliders[index].UpperValue);
        bool unlimited = lower == 0 && upper == 9999;
        text.Text = unlimited
            ? "最小 0 · 最大 9999（全程，没限制）"
            : $"最小 {lower} · 最大 {upper}（机器只在这个范围里动）";
        // 限过的轴读数亮一点，一眼能看出哪几个轴收窄了。
        text.Foreground = (Brush)FindResource(unlimited ? "Muted" : "Text");
    }

    /// <summary>
    /// 拖动限位滑块时把该轴实时移到对应位置，方便找舒适范围与机械边界。
    /// 立即发送做 90ms 限流（避免刷屏），同时记下最新值并在停手 140ms 后补发一次，
    /// 保证「拖到最大」这一下不会因为限流被吞掉。
    ///
    /// 安全闸：只有勾选「拖动限位时实时驱动设备」后才会走到下面的发送逻辑；
    /// 未勾选时直接返回 —— 拖动只改限位数值，绝不发串口指令。
    /// </summary>
    private void DriveAxisFromSlider(string axis, int value)
    {
        if (_loadingAxisGrid) return;
        if (!_liveLimitDriveEnabled)
        {
            // 原来这里是静默 return：用户拖到最大、机器不动，却没有任何解释。
            // 提示行只说"这次没动、为什么"，开关长什么样由上面的方框负责解释，避免两处写同一句话。
            SetLimitDriveHint("只改了数字，机器没动（实时驱动开关还关着）。", danger: false);
            return;
        }

        if (!App.Serial.IsOpen)
        {
            SetLimitDriveHint("⚠ 设备未连接：拖动只改数值，不会移动设备。", danger: true);
            AppLogger.Warn($"限位拖动 {axis} → {value}：未发送（串口未连接）");
            return;
        }

        if (!App.Engine.CanRun)
        {
            SetLimitDriveHint(App.Engine.EmergencyStopped
                ? "⚠ 输出已锁定（急停中）：先在左下角侧栏点「全部归中」解锁（急停中会先弹确认），再校准。"
                : "⚠ 设备当前不可动（未连接或正被其它模式占用）。", danger: true);
            AppLogger.Warn($"限位拖动 {axis} → {value}：未发送（CanRun=false，急停={App.Engine.EmergencyStopped}）");
            return;
        }

        // 规则引擎（游戏伴随 / 声音响应）接管输出时，MotionEngine.TryMoveCalibrationAxis 会直接
        // 返回 false —— 用户看到的现象就是「拖到最大但机器还停在中间」。这里先把它停掉再驱动。
        // 校准必须独占输出。会抢占输出的有两路，而且机制不同，必须分别停：
        //   ① 规则引擎（游戏伴随）：靠 RuleEngineActive 标记，TryMoveCalibrationAxis 会直接拒绝；
        //   ② 声音响应：它不走规则引擎，而是每 50ms 自己 TrySendDirectAxes 抢一次 ——
        //      于是校准指令和它的动作指令交替下发，设备就卡在两者之间。
        //      **用户看到的现象正是"限位拖到 9999、机器却停在中间"。**
        bool audioWasOn = App.Settings.AudioReactiveEnabled;
        if (App.Engine.RuleEngineActive || audioWasOn)
        {
            App.RuleEngine.Stop();
            App.Settings.RuleEngineEnabled = false;
            App.Settings.AudioReactiveEnabled = false;
            App.Settings.Save();
            App.AudioReactive.Refresh();   // 关掉采集：只改设置不足以让它松手
            SetLimitDriveHint(audioWasOn
                ? "已暂停「声音响应」以便校准（校准完可在测试台重新开启）。"
                : "已暂停「游戏伴随」以便校准（需要时可在测试台重新开启）。", danger: false);
        }
        else
        {
            SetLimitDriveHint("拖动时设备会实时移动到该位置。", danger: false);
        }

        AppLogger.Info($"限位拖动 {axis} → {value}（连接={App.Serial.IsOpen} 可动={App.Engine.CanRun} " +
                       $"急停={App.Engine.EmergencyStopped} 规则接管={App.Engine.RuleEngineActive} 模拟={App.Settings.SimulationMode}）");

        // 换了另一个轴：先把上一个轴的待补发值发掉，别让它被覆盖丢失
        if (_pendingDriveValue >= 0 && !string.Equals(_pendingDriveAxis, axis, StringComparison.Ordinal))
        {
            _limitDriveTimer.Stop();
            FlushPendingAxisDrive();
        }

        _pendingDriveAxis = axis;
        _pendingDriveValue = value;

        long now = Environment.TickCount64;
        if (now - _lastAxisDragAt >= 90)
        {
            _lastAxisDragAt = now;
            bool sent = App.Engine.TryMoveCalibrationAxis(axis, value, 150);
            SetLimitDriveHint(sent
                ? $"已发送：{axis} → {value}（设备正走向该位置）。"
                : $"没发出去：{axis} → {value} 被引擎拒绝（设备不可动或被其它模式占用），日志里有原因。",
                danger: !sent);
        }

        _limitDriveTimer.Stop();
        _limitDriveTimer.Start();
    }

    // ── 存储位置 ──────────────────────────────────────────────────────
    private void RefreshStorageFolderTexts()
    {
        if (CompositionFolderText != null) CompositionFolderText.Text = CompositionStore.DirectoryPath;
        if (ScriptFolderText != null) ScriptFolderText.Text = ScriptLibrary.Folder;
    }

    private void CompositionFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择动作合成目录",
            InitialDirectory = CompositionStore.DirectoryPath,
        };
        if (dialog.ShowDialog() != true) return;
        ApplyStorageFolder(dialog.FolderName, scriptFolder: false);
    }

    private void CompositionFolderReset_Click(object sender, RoutedEventArgs e) =>
        ApplyStorageFolder("", scriptFolder: false);

    private void CompositionFolderOpen_Click(object sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(CompositionStore.DirectoryPath);

    private void ScriptFolderOpen_Click(object sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(ScriptLibrary.Folder);

    /// <summary>在文件管理器里打开目录（不存在就先建出来）。</summary>
    private static void OpenFolderInExplorer(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"打不开这个目录：{ex.Message}", "存储位置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ScriptFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择脚本库目录",
            InitialDirectory = ScriptLibrary.Folder,
        };
        if (dialog.ShowDialog() != true) return;
        ApplyStorageFolder(dialog.FolderName, scriptFolder: true);
    }

    private void ScriptFolderReset_Click(object sender, RoutedEventArgs e) =>
        ApplyStorageFolder("", scriptFolder: true);

    /// <summary>写入自定义目录（空 = 恢复默认），立即生效并落盘。</summary>
    private void ApplyStorageFolder(string folder, bool scriptFolder)
    {
        string cleaned = (folder ?? "").Trim();
        if (cleaned.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(cleaned);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"这个目录没法用：{ex.Message}", "存储位置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (scriptFolder)
        {
            App.Settings.ScriptFolder = cleaned;
            ScriptLibrary.CustomFolder = cleaned;
            ScriptLibrary.EnsureFolder();
        }
        else
        {
            App.Settings.CompositionFolder = cleaned;
            CompositionStore.CustomDirectory = cleaned;
            CompositionStore.EnsureDirectory();
        }
        SaveSettingsOrWarn("存储位置");
        RefreshStorageFolderTexts();
    }

    /// <summary>限位区下方的状态提示（说明拖动会不会真的移动设备）。</summary>
    private void SetLimitDriveHint(string text, bool danger)
    {
        if (LimitDriveHintText is null) return;
        // 拖动反馈优先：取消还没到期的「已保存限位」提示恢复，别让它 2 秒后把新反馈盖回去。
        _limitHintFlashTimer.Stop();
        _limitHintFlashPreviousText  = null;
        _limitHintFlashPreviousBrush = null;
        LimitDriveHintText.Text = text;
        LimitDriveHintText.Foreground = (Brush)FindResource(danger ? "Danger" : "Muted");
    }

    /// <summary>
    /// 限位保存/重置成功后的临时提示：把提示行换成 ✅ 摘要，2 秒后恢复原来的文字与颜色。
    /// 重入安全：计时器已在跑时不覆盖已保存的原提示（连点两次也只恢复一次，不会把 ✅ 当成原提示）。
    /// </summary>
    private void FlashLimitHint(string text, bool danger)
    {
        if (LimitDriveHintText is null) return;
        if (!_limitHintFlashTimer.IsEnabled)
        {
            _limitHintFlashPreviousText  = LimitDriveHintText.Text;
            _limitHintFlashPreviousBrush = LimitDriveHintText.Foreground;
        }
        LimitDriveHintText.Text = text;
        LimitDriveHintText.Foreground = (Brush)FindResource(danger ? "Danger" : "Success");
        _limitHintFlashTimer.Stop();
        _limitHintFlashTimer.Start();
    }

    /// <summary>2 秒后把限位提示行恢复成原样。</summary>
    private void RestoreLimitHint()
    {
        if (_limitHintFlashPreviousText is null) return;
        LimitDriveHintText.Text = _limitHintFlashPreviousText;
        if (_limitHintFlashPreviousBrush is not null) LimitDriveHintText.Foreground = _limitHintFlashPreviousBrush;
        _limitHintFlashPreviousText  = null;
        _limitHintFlashPreviousBrush = null;
    }

    /// <summary>尾随补发：把最后一次拖动的值真正送到设备。</summary>
    private void FlushPendingAxisDrive()
    {
        if (_pendingDriveValue < 0 || _pendingDriveAxis.Length == 0) return;
        if (!_liveLimitDriveEnabled) return;   // 安全闸：关闸后绝不补发
        if (_loadingAxisGrid || !App.Serial.IsOpen) return;
        _lastAxisDragAt = Environment.TickCount64;
        App.Engine.TryMoveCalibrationAxis(_pendingDriveAxis, _pendingDriveValue, 150);
    }

    // 重建端口下拉列表，并按当前端口恢复选中态（端口不在列表时回退到第一项）
    private void ReloadPortCombo()
    {
        PortCombo.Items.Clear();
        foreach (var p in _vm.AvailablePorts) PortCombo.Items.Add(p);
        PortCombo.SelectedItem = _vm.AvailablePorts.FirstOrDefault(option =>
            string.Equals(option.PortName, _vm.Port, StringComparison.OrdinalIgnoreCase));
        if (PortCombo.SelectedItem == null && PortCombo.Items.Count > 0)
            PortCombo.SelectedIndex = 0;

        FallbackPortCombo.Items.Clear();
        FallbackPortCombo.Items.Add(new SerialPortOption("", false));
        foreach (var option in _vm.AvailablePorts.Where(option =>
                     !string.Equals(option.PortName, _vm.Port, StringComparison.OrdinalIgnoreCase)))
            FallbackPortCombo.Items.Add(option);
        FallbackPortCombo.SelectedItem = FallbackPortCombo.Items.Cast<SerialPortOption>().FirstOrDefault(option =>
            string.Equals(option.PortName, _vm.FallbackPort, StringComparison.OrdinalIgnoreCase));
        if (FallbackPortCombo.SelectedItem == null) FallbackPortCombo.SelectedIndex = 0;
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefreshPortsCommand.Execute(null);
        ReloadPortCombo();
        RefreshPortHogs();
    }
    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        // 已连接（含模拟设备）时按钮是「断开连接」：真正断开，而不是再连一次。
        if (_vm.IsConnected)
        {
            // 模拟设备下点「断开连接」= 退出模拟：清掉持久化的 SimulationMode，否则下次启动又进模拟。
            // reconnectIfAutoConnect=false —— 用户要的是断开，不能再自动连回去。
            if (App.Serial.IsSimulation) _vm.ExitSimulation(reconnectIfAutoConnect: false);
            // 用户主动断开：置位抑制「设备已断开 · 正在自动重连…」气泡（Dispatch 在 UI 线程同步执行，finally 前已消费）
            App.SuppressDisconnectBalloon = true;
            try { App.Serial.Disconnect(); }   // 同步方法：关串口 + 关自动重连，并触发 ConnectionChanged → 界面刷新
            finally { App.SuppressDisconnectBalloon = false; }
            UpdateStatus();
            UpdatePortState();
            return;
        }

        if (PortCombo.SelectedItem is SerialPortOption primary) _vm.Port = primary.PortName;
        if (FallbackPortCombo.SelectedItem is SerialPortOption fallback) _vm.FallbackPort = fallback.PortName;
        _vm.ConnectCommand.Execute(null);
    }
    private void SaveLimitsBtn_Click(object sender, RoutedEventArgs e)
    {
        _vm.L0Min = (int)Math.Round(_limitSliders[0].LowerValue); _vm.L1Min = (int)Math.Round(_limitSliders[1].LowerValue); _vm.L2Min = (int)Math.Round(_limitSliders[2].LowerValue);
        _vm.R0Min = (int)Math.Round(_limitSliders[3].LowerValue); _vm.R1Min = (int)Math.Round(_limitSliders[4].LowerValue); _vm.R2Min = (int)Math.Round(_limitSliders[5].LowerValue);
        _vm.L0Max = (int)Math.Round(_limitSliders[0].UpperValue); _vm.L1Max = (int)Math.Round(_limitSliders[1].UpperValue); _vm.L2Max = (int)Math.Round(_limitSliders[2].UpperValue);
        _vm.R0Max = (int)Math.Round(_limitSliders[3].UpperValue); _vm.R1Max = (int)Math.Round(_limitSliders[4].UpperValue); _vm.R2Max = (int)Math.Round(_limitSliders[5].UpperValue);
        _vm.SaveAxisLimitsCommand.Execute(null);
        // SaveAxisLimits 现在会把 _cfg.Save() 的结果放到 LimitSaveFailed 上：
        // 失败 → 提示行变红 + 弹窗；成功 → 显示 2 秒的 ✅ 摘要。
        if (_vm.LimitSaveFailed)
        {
            FlashLimitHint("⚠ 限位已在本次运行中生效，但写入 settings.json 失败（重启后可能丢失）。", danger: true);
            SaveSettingsOrWarn("限位");
            return;
        }
        FlashLimitHint($"✅ 已保存限位：{DescribeLimits()}", danger: false);
    }

    /// <summary>各轴限位区间摘要，用于保存成功后的 2 秒提示。</summary>
    private string DescribeLimits() => string.Join(" · ", new[]
    {
        $"L0 {_vm.L0Min}–{_vm.L0Max}", $"L1 {_vm.L1Min}–{_vm.L1Max}", $"L2 {_vm.L2Min}–{_vm.L2Max}",
        $"R0 {_vm.R0Min}–{_vm.R0Max}", $"R1 {_vm.R1Min}–{_vm.R1Max}", $"R2 {_vm.R2Min}–{_vm.R2Max}",
    });
    private void ResetLimitsBtn_Click(object sender, RoutedEventArgs e)
    {
        // 重置 = 六个轴全部放开到 0–9999 并且立即写盘：等于把之前设的保护一次清掉，先问一句。
        if (System.Windows.MessageBox.Show(
                "把六个轴的限位都改回 0–9999（等于不限制），并立刻保存。\n"
                + "机器之后能走到最远的位置，可能超出你现在觉得舒服的范围。\n\n确定要重置吗？",
                "重置为全程", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _vm.ResetAxisLimitsCommand.Execute(null);
        if (_vm.LimitSaveFailed)
        {
            FlashLimitHint("⚠ 已重置为 0–9999，但写入 settings.json 失败（重启后可能丢失）。", danger: true);
            SaveSettingsOrWarn("限位重置");
        }
        else
        {
            FlashLimitHint("✅ 已重置为 0–9999（全程，没限制）", danger: false);
        }
        BuildAxisLimitGrid();
    }

    private void CalibrationBtn_Click(object sender, RoutedEventArgs e)
    {
        var window = new CalibrationWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        BuildAxisLimitGrid();
    }

    // ── 设备自检：读取固件身份 + 逐轴测试 ────────────────────────────
    private void BuildAxisTestPanel()
    {
        AxisTestPanel.Children.Clear();
        foreach (string axis in AxIds)
        {
            var button = new Button
            {
                Content = axis,
                Width = 56,
                Height = 30,
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 6),
                Style = (Style)FindResource("BtnSecondary"),
                Tag = axis,
            };
            string captured = axis;
            button.Click += async (_, _) => await TestAxisAsync(captured, button);
            AxisTestPanel.Children.Add(button);
        }
    }

    private async Task TestAxisAsync(string axis, Button button)
    {
        if (!App.Serial.IsOpen)
        {
            AxisTestHint.Text = "设备未连接：请先在上方“主通道”选择端口并点击“立即连接”。";
            return;
        }
        button.IsEnabled = false;
        AxisTestHint.Text = $"正在测试 {axis}：请盯着设备，看哪个部位在动……";
        try
        {
            // 只按限位区间的 +15% 点动一次再回中：幅度够看清哪个部位在动，又不会突然走满行程
            // （界面上写的就是「先移到 +15% 再回中」；一次点击就走满行程属于危险动作）。
            int min = App.Settings.AxisMin.TryGetValue(axis, out int mn) ? mn : 0;
            int max = App.Settings.AxisMax.TryGetValue(axis, out int mx) ? mx : 9999;
            int center = (min + max) / 2;
            int testTarget = center + (int)((max - min) * 0.15);
            bool started = App.Engine.TryMoveCalibrationAxis(axis, testTarget, 800);
            if (!started)
            {
                // 说清真实原因：以前无论哪种情况都写"输出未解锁（未连接或急停）"，
            // 声音响应/游戏伴随占着输出时用户会据此误判成"这个轴没装电机"。
            string why = App.Engine.EmergencyStopped
                ? "急停锁定中：先点侧栏「⬆ 全部归中」解锁（会先问一次）。"
                : !App.Serial.IsOpen
                    ? "设备没连上：先到本页上方选端口并点「立即连接」。"
                    : App.Engine.RuleEngineActive
                        ? "游戏伴随正在驱动设备：先到「测试台」把它关掉。"
                        : App.AudioReactive.Capturing && App.Engine.DriverLabel == "声音响应"
                            ? "声音响应正在驱动设备：先到「测试台」把声音响应关掉（它的指令和自测会在同一根轴上打架）。"
                            : $"现在由「{App.Engine.DriverLabel}」在驱动设备，先把它停掉再测。";
            AxisTestHint.Text = $"无法测试 {axis}：{why}";
                return;
            }
            await Task.Delay(1900);
            App.Engine.TryMoveCalibrationAxis(axis, center, 800);
            await Task.Delay(1200);
            AxisTestHint.Text = $"{axis} 测试完成（移动到 {testTarget}（约 +15%）→ 回中 {center}）。如果完全没动，说明这个轴没装电机。";
        }
        catch (Exception ex)
        {
            AxisTestHint.Text = "测试失败：" + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void DeviceInfo_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Serial.IsOpen)
        {
            DeviceInfoBox.Text = "设备未连接。请先在上方选择端口并点击“立即连接”。";
            return;
        }
        // 查询要占用串口锁约 1 秒：放到后台线程执行，避免 UI 冻结、也不让急停指令被堵住。
        DeviceInfoBox.Text = "查询中…";
        DeviceInfoBtn.IsEnabled = false;
        try
        {
            string info = await Task.Run(() => App.Serial.QueryDeviceInfo());
            DeviceInfoBox.Text = string.IsNullOrWhiteSpace(info)
                ? "未获取到响应。请确认：① 设备已连接；② 没有其他程序（MultiFunPlayer / Intiface Central）占用串口。"
                : info;
        }
        catch (Exception ex)
        {
            DeviceInfoBox.Text = "查询失败：" + ex.Message;
        }
        finally
        {
            DeviceInfoBtn.IsEnabled = true;
        }
    }

    private void HomeBtn_Click(object sender, RoutedEventArgs e)
    {
        // 归中会让六个轴真的动起来，并且会解除急停、重新解锁输出：连着设备时先确认。
        // 没连设备时归中不会动到任何东西，就不用弹窗挡人。
        if (App.Serial.IsOpen)
        {
            string text = "六个轴都会移到行程中间的位置（约 1 秒），正在跑的动作会先停下。";
            if (App.Engine.EmergencyStopped)
                text += "\n设备现在处于急停锁定，归中会解除急停并重新解锁输出。";
            if (System.Windows.MessageBox.Show(text + "\n\n继续吗？", "全部归中",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }
        App.Engine.Home();
        FlashLimitHint(App.Serial.IsOpen
            ? "✅ 已让六个轴回到中间位置"
            : "设备没连上：这次归中只在程序里执行，机器没有动。", danger: false);
    }

    // ── 关于与维护：版本 / 数据目录 / 整份配置的导入导出 ──────────────
    /// <summary>设置文件名。它所在的目录 = <see cref="AppSettings.DataDirectory"/>（自检沙箱也走这里）。</summary>
    private const string SettingsFileName = "settings.json";
    /// <summary>动作预设文件名（与 StrokesViewModel / StrokePresetStore 读的是同一个：&lt;程序目录&gt;/presets.json）。</summary>
    private const string PresetsFileName = "presets.json";
    /// <summary>导入时单个文件的大小上限：正常的 settings.json 只有几十 KB，超过这个数一定不是我们的包（防解压炸弹）。</summary>
    private const int MaxImportBytes = 8 * 1024 * 1024;

    /// <summary>回填版本 / 运行时 / 数据目录。这三样运行中不会变，构造时填一次就够。</summary>
    private void InitAboutCard()
    {
        if (AboutVersionText is null) return;
        AboutVersionText.Text = ProgramVersion();
        AboutRuntimeText.Text = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        AboutDataFolderText.Text = AppSettings.DataDirectory;
        AboutDataFolderText.ToolTip = AppSettings.DataDirectory;
    }

    /// <summary>
    /// 程序版本：优先读 AssemblyInformationalVersion（csproj 写了就用它），
    /// 没写就退回 AssemblyVersion，再不行给 1.0.0 —— 绝不显示空白：
    /// 用户报问题时的第一句话就是「你用的哪个版本」。
    /// </summary>
    private static string ProgramVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = info.IndexOf('+');           // "1.0.0+3f2a1c" 里的构建哈希不用给用户看
        if (plus > 0) info = info[..plus];
        if (!string.IsNullOrWhiteSpace(info)) return info.Trim();
        return assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    private void AboutDataFolderOpen_Click(object sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(AppSettings.DataDirectory);

    /// <summary>
    /// 导出整份配置：settings.json（轴限位 / 热键 / 游戏 / AI 全在里面）+ 有的话再带上 presets.json，
    /// 打成一个 zip。用 .NET 自带的 System.IO.Compression，不引任何新依赖，也不需要系统装压缩软件。
    /// </summary>
    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        string settingsSource = Path.Combine(AppSettings.DataDirectory, SettingsFileName);
        if (!File.Exists(settingsSource))
        {
            UpdateAboutStatus("导不出来：这台机器上还没有设置文件（" + settingsSource
                              + "）。先在别的卡片上改一个设置并保存，再回来导出。", danger: true);
            return;
        }
        string presetsSource = StrokePresetStore.FilePath;
        bool hasPresets = File.Exists(presetsSource);

        var dialog = new SaveFileDialog
        {
            Title = "导出配置",
            FileName = $"hexa-config-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            Filter = "Hexa 配置包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using (var zip = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(settingsSource, SettingsFileName, CompressionLevel.Optimal);
                if (hasPresets) zip.CreateEntryFromFile(presetsSource, PresetsFileName, CompressionLevel.Optimal);
            }
            UpdateAboutStatus(
                $"已导出到 {dialog.FileName}（含轴限位/热键/游戏配置{(hasPresets ? "、动作预设" : "")}）"
                + "。包里有你的 API Key，别随手发给别人。", danger: false);
        }
        catch (Exception ex)
        {
            UpdateAboutStatus($"导出失败：{ex.Message} —— 换个目录再试一次（比如桌面、文档）。", danger: true);
        }
    }

    /// <summary>
    /// 导入配置：选 zip → 校验里面确实有 settings.json（且是合法 JSON）→ 先把当前设置备份一份
    /// → 再覆盖 → 提醒重启。**故意不做热重载**：整套设置里有设备参数，重启才是最省事也最稳的做法。
    /// </summary>
    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入配置",
            Filter = "Hexa 配置包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        // 危险操作先说清楚：覆盖的是整套设置，其中轴限位是「机械边界」——
        // 导错一份，机器就只能在中间一段动（这事以前真发生过）。
        if (System.Windows.MessageBox.Show(
                "导入会用包里的设置覆盖现在的设置（轴限位、热键、游戏配置、AI 设置全都换掉）。\n\n"
                + "导入前会自动把现在的设置备份成一份 settings.import-backup-….json。继续吗？",
                "导入配置", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(dialog.FileName);

            var settingsEntry = zip.Entries.FirstOrDefault(entry => IsNamed(entry, SettingsFileName));
            if (settingsEntry is null)
            {
                UpdateAboutStatus("导入失败：这个 zip 里没有 settings.json，不像是 Hexa 导出的配置包（现在的设置没动）。",
                                  danger: true);
                return;
            }
            if (settingsEntry.Length > MaxImportBytes)
            {
                UpdateAboutStatus($"导入失败：包里的 settings.json 有 {settingsEntry.Length / 1024 / 1024} MB，"
                                  + "大小明显不对，已中止（现在的设置没动）。", danger: true);
                return;
            }

            string json = ReadEntryText(settingsEntry);
            try
            {
                // 先解析一次：坏文件 / 半截文件绝不能拿去覆盖掉一份好配置。
                using (System.Text.Json.JsonDocument.Parse(json)) { }
            }
            catch (Exception ex)
            {
                UpdateAboutStatus("导入失败：包里的 settings.json 读不出内容（" + ex.Message
                                  + "），现在的设置没动。", danger: true);
                return;
            }

            string settingsTarget = Path.Combine(AppSettings.DataDirectory, SettingsFileName);
            Directory.CreateDirectory(AppSettings.DataDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backupPath = Path.Combine(AppSettings.DataDirectory, $"settings.import-backup-{stamp}.json");
            bool hadSettings = File.Exists(settingsTarget);
            if (hadSettings) File.Copy(settingsTarget, backupPath, overwrite: true);   // 备份失败会抛异常 → 下面兜住，此时一个字节都还没写
            File.WriteAllText(settingsTarget, json);

            // 包里有动作预设就一起还原。预设文件在程序目录里（装在 Program Files 时可能没写权限），
            // 所以单独处理、单独报告：设置导入成功就算成功，预设写不进去只在这里说明一句。
            string note = "";
            var presetsEntry = zip.Entries.FirstOrDefault(entry => IsNamed(entry, PresetsFileName));
            if (presetsEntry is not null)
            {
                if (presetsEntry.Length > MaxImportBytes)
                {
                    note = "（动作预设文件太大，没有导入）";
                }
                else
                {
                    try
                    {
                        string presetsTarget = StrokePresetStore.FilePath;
                        if (File.Exists(presetsTarget))
                            File.Copy(presetsTarget,
                                      Path.Combine(Path.GetDirectoryName(presetsTarget)!,
                                                   $"presets.import-backup-{stamp}.json"),
                                      overwrite: true);
                        File.WriteAllText(presetsTarget, ReadEntryText(presetsEntry));
                    }
                    catch (Exception ex)
                    {
                        note = $"（动作预设没能导进去：{ex.Message}）";
                    }
                }
            }

            UpdateAboutStatus(
                "已导入" + (hadSettings ? $"，导入前的设置已备份成 {Path.GetFileName(backupPath)}" : "（原来没有设置文件，所以没有备份）")
                + "。重启 Hexa 后生效 —— 重启之前别在设置页点保存，"
                + "否则现在的旧设置会把刚导入的内容又盖回去。" + note, danger: false);
        }
        catch (Exception ex)
        {
            UpdateAboutStatus($"导入失败：{ex.Message}（现在的设置没动）", danger: true);
        }
    }

    /// <summary>zip 条目是不是我们要的那个文件（包里可能带一层目录，所以按文件名比；大小写不敏感）。</summary>
    private static bool IsNamed(ZipArchiveEntry entry, string fileName) =>
        string.Equals(Path.GetFileName(entry.FullName), fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>按 UTF-8 读一个 zip 条目（settings.json 里有中文；StreamReader 会自动吃掉 BOM）。</summary>
    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>「关于与维护」卡片下面那行状态：成功用绿色、失败用红色，都说人话。</summary>
    private void UpdateAboutStatus(string text, bool danger)
    {
        if (AboutStatusText is null) return;
        AboutStatusText.Text = text;
        AboutStatusText.Foreground = (Brush)FindResource(danger ? "Danger" : "Success");
    }
}
