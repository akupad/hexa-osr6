using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hexa.Services;

namespace Hexa.Views;

/// <summary>
/// 「🌉 游戏桥」面板（Intiface WebSocket → TCode）。
///
/// 为什么抽成控件而不是在测试台和游玩页各放一份：这是一模一样的一块界面，
/// 复制两份就得维护两套 x:Name、两套事件处理器、两处端口提示文案；改一处忘一处，
/// 用户看到的就是「同一个开关在两个页面行为不一样」。所以只有一份 XAML + 一份逻辑，
/// 测试台不再有它，游玩页用 &lt;views:BridgePanel/&gt; 引用。
///
/// 控件自持的东西（原来借的是页面级的）：
/// ① <c>_loadingUi</c>：回填设置时抑制保存/驱动，语义与原来页面里那份一样；
/// ② 定时器：状态栏 / 指令监视的刷新改由这里自己的 DispatcherTimer 负责，
///    并在 Loaded 启动、Unloaded 停止（见构造函数）—— 控件不可见时不该还在刷。
/// </summary>
public partial class BridgePanel : UserControl
{
    // 控件自己的「正在回填设置」抑制标志：回填期间不许落盘、不许让桥重新读设置 ——
    // 默认关闭的桥不能因为用户打开了一眼界面就被连上。
    private bool _loadingUi;

    // 状态栏 + 指令监视的刷新：150ms 一拍（与原来测试台的 _uiTimer 同频）。
    // 挂在 Loaded/Unloaded（见构造函数末尾）：控件只在「游玩」页可见，
    // 页面切走时必须停 —— 否则一个看不见的面板还在每 150ms 读桥状态、往日志列表里塞行。
    private readonly DispatcherTimer _bridgeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // 滑块 / 勾选改动 350ms 防抖后才落盘并通知桥重读设置：拖动一次会触发几十次 ValueChanged，
    // 每次都写盘 + Refresh()（重开监听）会让 UI 卡顿。
    private readonly DispatcherTimer _latencySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    // null = 还没同步过：Loaded 时置回 null，强制把按钮文案/配色重刷一次 ——
    // 桥的开关可能被别处改掉（测试台的「⛔ 全部停止」直接走 App.Bridge），那种改动不经过本控件的事件。
    private bool? _lastBridgeToggleState;
    private long _lastBridgeLogCount = -1;

    // 游戏桥日志：ObservableCollection 增量追加，避免每次整表重建 + 逐行 FindResource。
    private readonly ObservableCollection<BridgeLogLine> _bridgeLogLines = new();
    private IntifaceBridgeService.BridgeLogEntry? _lastBridgeLogEntry;
    private ScrollViewer? _bridgeLogScroll;
    private Brush? _rxLogBrush;
    private Brush? _txLogBrush;

    public BridgePanel()
    {
        InitializeComponent();
        BridgeLogList.ItemsSource = _bridgeLogLines;

        // 先按设置回填、再挂交互事件（与原来测试台的顺序一致）：
        // 回填阶段事件还没挂，天然不会触发保存，也不会去驱动设备。
        LoadBridgeUi();
        WireEvents();

        _latencySaveTimer.Tick += (_, _) => { _latencySaveTimer.Stop(); ApplyBridgeSettings(); };
        _bridgeRefreshTimer.Tick += (_, _) => RefreshBridgeUi();

        // 为什么把定时器挂在 Loaded/Unloaded，而不是构造时直接 Start：
        // 主窗口把页面缓存起来来回切换，控件会反复 Unloaded/Loaded。只有可见时才需要刷状态与日志，
        // 不可见时停掉能省下每 150ms 一次的状态读取与列表写入。
        Loaded += (_, _) =>
        {
            _lastBridgeToggleState = null;   // 强制同步：桥可能被别处关掉（例如测试台的「⛔ 全部停止」）
            RefreshBridgeUi();
            _bridgeRefreshTimer.Start();
        };
        Unloaded += (_, _) => _bridgeRefreshTimer.Stop();

        // 可见性也要管：面板在游玩页是折叠的（默认 Collapsed），折叠时它并没有 Unloaded，
        // 光靠 Unloaded 会让一个看不见的面板继续每 150ms 读状态、往日志列表里塞行。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _lastBridgeToggleState = null;   // 展开这一刻可能已经被别处改过开关
                RefreshBridgeUi();
                _bridgeRefreshTimer.Start();
            }
            else
            {
                _bridgeRefreshTimer.Stop();
            }
        };
    }

    /// <summary>把设置回填进界面（期间 <see cref="_loadingUi"/> 抑制保存）。</summary>
    private void LoadBridgeUi()
    {
        _loadingUi = true;
        try
        {
            SelectByTag(BridgeModeCombo, App.Settings.GameBridgeMode);
            SelectByTag(BridgeVibrateModeCombo, App.Settings.GameBridgeVibrateMode);
            BridgePortBox.Text = App.Settings.GameBridgePort.ToString();
            SyncBridgeLatencyUi();
            BridgeLanCheck.IsChecked = App.Settings.GameBridgeAllowLan;
            BridgeSmoothCheck.IsChecked = App.Settings.BridgeInputSmoothing;
            BridgeSmoothSlider.Value = App.Settings.BridgeSmoothingMs;
            BridgeSmoothLabel.Text = $"{App.Settings.BridgeSmoothingMs}ms";
            BridgeLinkCheck.IsChecked = App.Settings.BridgeAxisLink;
            BridgeLinkSlider.Value = App.Settings.BridgeAxisLinkAmount;
            BridgeLinkLabel.Text = $"{(int)Math.Round(App.Settings.BridgeAxisLinkAmount)}%";
            BridgeComfortCheck.IsChecked = App.Settings.BridgeUseComfortLimits;
            BridgeRawTcodeCheck.IsChecked = App.Settings.AllowRawTCodeUdp;
            RefreshRawTcodeHint();
            SyncBridgeShapeUi();
            RefreshBridgePortHint(false, App.Settings.GameBridgePort);
        }
        finally
        {
            _loadingUi = false;
        }

        RefreshBridgeUi();
    }

    /// <summary>交互事件统一在这儿挂：XAML 里只留按钮 / 下拉的连接，滑杆与勾选走代码。</summary>
    private void WireEvents()
    {
        BridgePortBox.LostFocus += (_, _) => { if (!_loadingUi) ApplyBridgeSettings(); };
        BridgeLatencySlider.ValueChanged += (_, _) =>
        {
            if (_loadingUi) return;
            BridgeLatencyLabel.Text = ((int)Math.Round(BridgeLatencySlider.Value)).ToString();
            _latencySaveTimer.Stop();
            _latencySaveTimer.Start();
        };
        BridgeLanCheck.Checked += (_, _) => { if (!_loadingUi) ApplyBridgeSettings(); };
        BridgeLanCheck.Unchecked += (_, _) => { if (!_loadingUi) ApplyBridgeSettings(); };

        // 动作整形三件套：勾选/拖动都直接落盘（拖动用同一个防抖计时器）
        BridgeSmoothCheck.Checked += (_, _) => { if (!_loadingUi) { SyncBridgeShapeUi(); ApplyBridgeSettings(); } };
        BridgeSmoothCheck.Unchecked += (_, _) => { if (!_loadingUi) { SyncBridgeShapeUi(); ApplyBridgeSettings(); } };
        BridgeLinkCheck.Checked += (_, _) => { if (!_loadingUi) { SyncBridgeShapeUi(); ApplyBridgeSettings(); } };
        BridgeLinkCheck.Unchecked += (_, _) => { if (!_loadingUi) { SyncBridgeShapeUi(); ApplyBridgeSettings(); } };
        BridgeComfortCheck.Checked += (_, _) => { if (!_loadingUi) ApplyBridgeSettings(); };
        BridgeComfortCheck.Unchecked += (_, _) => { if (!_loadingUi) ApplyBridgeSettings(); };
        BridgeRawTcodeCheck.Checked += (_, _) => { if (!_loadingUi) { RefreshRawTcodeHint(); ApplyBridgeSettings(); } };
        BridgeRawTcodeCheck.Unchecked += (_, _) => { if (!_loadingUi) { RefreshRawTcodeHint(); ApplyBridgeSettings(); } };
        BridgeSmoothSlider.ValueChanged += (_, e) =>
        {
            BridgeSmoothLabel.Text = $"{(int)Math.Round(e.NewValue)}ms";
            if (_loadingUi) return;
            _latencySaveTimer.Stop();
            _latencySaveTimer.Start();
        };
        BridgeLinkSlider.ValueChanged += (_, e) =>
        {
            BridgeLinkLabel.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_loadingUi) return;
            _latencySaveTimer.Stop();
            _latencySaveTimer.Start();
        };
    }

    /// <summary>控件自己的「刷一遍」：开关文案 + 状态条 + 指令监视。</summary>
    private void RefreshBridgeUi()
    {
        UpdateBridgeToggle();
        RefreshBridgeStatus();
        RefreshBridgeLog();
    }

    // ── 游戏桥（Intiface WebSocket → TCode） ───────────────────────────
    private void BridgeToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.GameBridgeEnabled = !App.Settings.GameBridgeEnabled;
        App.Settings.Save();
        App.Bridge.Refresh();
        UpdateBridgeToggle();
        RefreshBridgeStatus();
    }

    private void BridgeSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        ApplyBridgeSettings();
    }

    /// <summary>按勾选状态启用/灰掉两个滑杆，并把现状摘要写在折叠按钮右边（收起来也看得见当前值）。</summary>
    private void SyncBridgeShapeUi()
    {
        bool smooth = BridgeSmoothCheck.IsChecked == true;
        bool link = BridgeLinkCheck.IsChecked == true;
        BridgeSmoothSlider.IsEnabled = smooth;
        BridgeLinkSlider.IsEnabled = link;
        if (BridgeShapeSummary is not null)
        {
            BridgeShapeSummary.Text =
                (smooth ? $"平滑 {App.Settings.BridgeSmoothingMs}ms" : "平滑关") + " · " +
                (link ? $"联动 {App.Settings.BridgeAxisLinkAmount:0}%" : "联动关") + " · " +
                (BridgeComfortCheck.IsChecked == true ? "舒适档开" : "舒适档关");
        }
    }

    /// <summary>
    /// 原始 TCode 那行的提示：常显「实际在听哪个端口」；绑不上时写明失败原因与换端口建议。
    /// 以前端口绑不上只进日志：界面照旧显示配置里的 26781，用户照提示在 VAM 里填好却一条都收不到，
    /// 也看不出是「端口被别的程序占着」。
    /// </summary>
    private void RefreshRawTcodeHint()
    {
        if (BridgeRawTcodeHint is null) return;
        int configured = App.Settings.GameTelemetryPort;
        GameTelemetryService telemetry = App.GameTelemetry;
        if (!telemetry.Listening)
        {
            int suggestion = GameTelemetryService.SuggestFreePort(configured);
            string fix = suggestion != configured
                ? $"把设置页最下面「数据目录」里那份 settings.json 的 GameTelemetryPort 改成 {suggestion}" +
                  $"（或关掉占用 {configured} 的程序）再重启 Hexa。"
                : $"端口 {configured} 附近的端口也都绑不上：先关掉占用它的程序再重启 Hexa。";
            BridgeRawTcodeHint.Text =
                $"⚠ 遥测端口没监听上：{telemetry.ListenError ?? "未启动"}。" +
                $"现在发到 127.0.0.1:{configured} 的包（含原始 TCode）一条都收不到 —— {fix}";
            return;
        }
        string listen = $"正在监听 127.0.0.1:{telemetry.Port}";
        BridgeRawTcodeHint.Text = App.Settings.AllowRawTCodeUdp
            ? $"{listen}（已开启原始 TCode）。VAM 插件里 Address 填 127.0.0.1、Port 填 {telemetry.Port}（默认的 tcode.local 在本机解析不了，别用）。"
            : $"{listen}（只收带口令的 Hexa 遥测帧）。打开后，任何软件用 UDP 发 TCode 文本到 127.0.0.1:{telemetry.Port} 就能直接驱动设备。";
    }

    private void ApplyBridgeSettings()
    {
        App.Settings.GameBridgeMode = (SelectedItem(BridgeModeCombo)?.Tag as string) ?? "single";
        App.Settings.GameBridgeVibrateMode = (SelectedItem(BridgeVibrateModeCombo)?.Tag as string) ?? "off";

        // P1：端口非法时不能静默丢弃并改写回旧值，要明确提示并保留旧端口。
        bool portOk = int.TryParse(BridgePortBox.Text.Trim(), out int port) && port is >= 1024 and <= 65535;
        if (portOk) App.Settings.GameBridgePort = port;
        else BridgePortBox.Text = App.Settings.GameBridgePort.ToString();   // 先摆回旧值，提示行会说明原因

        App.Settings.GameBridgeLatencyMs = (int)Math.Round(BridgeLatencySlider.Value);
        App.Settings.BridgeInputSmoothing = BridgeSmoothCheck.IsChecked == true;
        App.Settings.BridgeSmoothingMs = (int)Math.Round(BridgeSmoothSlider.Value);
        App.Settings.BridgeAxisLink = BridgeLinkCheck.IsChecked == true;
        App.Settings.BridgeAxisLinkAmount = BridgeLinkSlider.Value;
        App.Settings.BridgeUseComfortLimits = BridgeComfortCheck.IsChecked == true;
        App.Settings.GameBridgeAllowLan = BridgeLanCheck.IsChecked == true;
        App.Settings.AllowRawTCodeUdp = BridgeRawTcodeCheck.IsChecked == true;
        App.Settings.Normalize();
        App.Settings.Save();
        BridgePortBox.Text = App.Settings.GameBridgePort.ToString();
        RefreshBridgePortHint(!portOk, App.Settings.GameBridgePort);
        RefreshRawTcodeHint();
        SyncBridgeLatencyUi();
        SyncBridgeShapeUi();
        if (App.Settings.GameBridgeEnabled) App.Bridge.Refresh();
        // 原来这里调的是测试台页的 RefreshStatus()（会顺带刷声音/伴随）。控件不该反过来依赖页面，
        // 所以只刷自己这一块（走的还是 App.* 服务层），页面那几块由页面自己的定时器负责。
        RefreshBridgeUi();
    }

    /// <summary>「动作整形 / 少见情况」两个进阶分组的展开与收起（默认收起，别一次摊一屏控件）。</summary>
    private void BridgeShapeToggle_Click(object sender, RoutedEventArgs e) =>
        ToggleFold(BridgeShapeToggle, BridgeShapePanel, "动作整形（进阶）");

    private void BridgeRareToggle_Click(object sender, RoutedEventArgs e) =>
        ToggleFold(BridgeRareToggle, BridgeRarePanel, "少见情况（延迟补偿 · 振动 · 局域网）");

    private static void ToggleFold(Button toggle, UIElement panel, string title)
    {
        bool expand = panel.Visibility != Visibility.Visible;
        panel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        toggle.Content = (expand ? "▾ " : "▸ ") + title;
    }

    private void BridgePortBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // KeyEventArgs 需全限定：本项目隐式 using 了 System.Windows.Forms，同名类型会冲突。
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyBridgeSettings();
    }

    /// <summary>把延迟补偿配置回写到滑块与数值标签（值未变时不写回，避免 ValueChanged 递归）。</summary>
    private void SyncBridgeLatencyUi()
    {
        int ms = Math.Clamp(App.Settings.GameBridgeLatencyMs, 0, 300);
        if ((int)Math.Round(BridgeLatencySlider.Value) != ms) BridgeLatencySlider.Value = ms;
        int smoothMs = App.Settings.BridgeSmoothingMs;
        if ((int)Math.Round(BridgeSmoothSlider.Value) != smoothMs) BridgeSmoothSlider.Value = smoothMs;
        BridgeSmoothLabel.Text = $"{smoothMs}ms";
        double linkAmount = App.Settings.BridgeAxisLinkAmount;
        if (Math.Abs(BridgeLinkSlider.Value - linkAmount) > 0.01) BridgeLinkSlider.Value = linkAmount;
        BridgeLinkLabel.Text = $"{(int)Math.Round(linkAmount)}%";
        BridgeLatencyLabel.Text = ms.ToString();
    }

    private void UpdateBridgeToggle()
    {
        bool enabled = App.Settings.GameBridgeEnabled;
        if (enabled == _lastBridgeToggleState) return;
        _lastBridgeToggleState = enabled;
        BridgeToggleBtn.Content = enabled ? "关闭游戏桥" : "开启游戏桥";
        BridgeToggleBtn.Style = (Style)FindResource(enabled ? "BtnDanger" : "BtnSecondary");
    }

    /// <summary>
    /// 「监听端口」下面那行小字：常显（用户要求「信息一直显示」，不许再靠悬停才知道范围与默认值），
    /// 内容随当前端口更新；端口非法时换成警告色并直接说怎么改 ——
    /// 原来那条只在出错时才出现的提示就并进这一行了。
    /// </summary>
    private void RefreshBridgePortHint(bool invalid, int port)
    {
        // 12345 是 AppSettings.GameBridgePort 的出厂默认值（Models 这次不让动，这里只写一个展示用的数字）。
        BridgePortHint.Text = invalid
            ? $"⚠ 端口要是 1024–65535 的整数，已保留 {port} 不变 · 改好按回车生效 · 游戏里填 ws://127.0.0.1:改后的数字"
            : $"默认 12345 · 范围 1024–65535 · 游戏里填 ws://127.0.0.1:{port}";
        BridgePortHint.Foreground = (Brush)FindResource(invalid ? "Danger" : "Muted");
    }

    /// <summary>指令监视面板的一行（RX=游戏→Hexa，TX=Hexa→设备）。</summary>
    private sealed record BridgeLogLine(string Time, string Direction, string Text, Brush Brush);

    private Brush RxLogBrush => _rxLogBrush ??= (Brush)FindResource("Accent");
    private Brush TxLogBrush => _txLogBrush ??= (Brush)FindResource("Success");
    private Brush? _noteLogBrush;
    /// <summary>「收到了但故意不执行」的说明行（振动指令被忽略等）用警告色，别和正常 TX 混在一起。</summary>
    private Brush NoteLogBrush => _noteLogBrush ??= (Brush)FindResource("Warning");

    private void RefreshBridgeLog()
    {
        var bridge = App.Bridge;
        BridgeCountText.Text = $"RX {bridge.RxCount} · TX {bridge.TxCount}";
        long total = bridge.RxCount + bridge.TxCount;
        if (total != _lastBridgeLogCount)
        {
            _lastBridgeLogCount = total;
            AppendBridgeLogLines(bridge);
        }

        // 空状态（"一片黑会让人以为坏了"）必须按「追加完之后列表里到底有没有行」来算：
        // 原先是追加之前算的，第一拍列表还空着就显示空状态文字，紧接着日志被补进来 ——
        // 渲染图里能看到那一下文字压在日志行上。
        BridgeLogEmpty.Visibility = _bridgeLogLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把桥的新日志补进列表（首屏只补最近 120 条；用户正往上翻时不把他弹回底部）。</summary>
    private void AppendBridgeLogLines(IntifaceBridgeService bridge)
    {
        // P1：先记住用户是否停在底部，只有停在底部时才自动跟随新日志。
        bool wasAtBottom = IsBridgeLogAtBottom();

        var entries = bridge.RecentLog;
        if (entries.Count == 0)
        {
            if (_bridgeLogLines.Count > 0) _bridgeLogLines.Clear();
            _lastBridgeLogEntry = null;
            return;
        }

        int start;
        if (_lastBridgeLogEntry == null)
        {
            // 首次显示或清空后：只补最近 120 条。
            start = Math.Max(0, entries.Count - 120);
        }
        else
        {
            // 找到上次追加到的条目；找不到说明它已被环形队列淘汰，整表重建。
            start = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!ReferenceEquals(entries[i], _lastBridgeLogEntry)) continue;
                start = i + 1;
                break;
            }
            if (start < 0)
            {
                _bridgeLogLines.Clear();
                start = Math.Max(0, entries.Count - 120);
            }
        }

        long now = Environment.TickCount64;
        for (int i = start; i < entries.Count; i++)
        {
            var entry = entries[i];
            _bridgeLogLines.Add(new BridgeLogLine(
                DateTime.Now.AddMilliseconds(-(now - entry.AtMs)).ToString("HH:mm:ss.fff"),
                entry.Direction,
                entry.Text,
                entry.Direction == "RX" ? RxLogBrush : entry.Direction == "TX" ? TxLogBrush : NoteLogBrush));
        }

        // 显示上限 120 条，超出从头部移除。
        while (_bridgeLogLines.Count > 120) _bridgeLogLines.RemoveAt(0);

        _lastBridgeLogEntry = entries[^1];

        if (wasAtBottom && _bridgeLogLines.Count > 0)
            BridgeLogList.ScrollIntoView(_bridgeLogLines[^1]);
    }

    /// <summary>日志是否已滚到底部（容差 4px）；用户向上翻看时不要把他弹回底部。</summary>
    private bool IsBridgeLogAtBottom()
    {
        _bridgeLogScroll ??= FindVisualChild<ScrollViewer>(BridgeLogList);
        if (_bridgeLogScroll == null) return true;   // 模板尚未生成时按“在底部”处理
        return _bridgeLogScroll.VerticalOffset >= _bridgeLogScroll.ScrollableHeight - 4;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target) return target;
            T? nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void BridgeClearLog_Click(object sender, RoutedEventArgs e)
    {
        App.Bridge.ClearLog();
        _lastBridgeLogCount = -1;
        _lastBridgeLogEntry = null;
        RefreshBridgeLog();
    }

    private void BridgeSelfTest_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Bridge.Active) return;
        App.Bridge.RunSelfTest(5);
    }

    private void RefreshBridgeStatus()
    {
        var bridge = App.Bridge;
        // 自测会真的让设备动：设备没连、或输出被锁（急停/掉线）时点它只会刷一屏"⚠ 输出已锁定"，
        // 看不出任何结论 ⇒ 直接禁用，并把原因写进 ToolTip（子代理审计：按钮名承诺了"自测"却不给判定）。
        bool canSelfTest = bridge.Active && App.Serial.IsOpen && App.Serial.OutputEnabled
            && !App.Engine.EmergencyStopped;
        BridgeSelfTestBtn.IsEnabled = canSelfTest;
        BridgeSelfTestBtn.ToolTip = canSelfTest
            ? "真让设备动 5 秒，结束后会在下面写一行结论（发出多少条 / 链路通不通）"
            : !bridge.Active
                ? "桥没开：先在左边把游戏桥打开"
                : !App.Serial.IsOpen
                    ? "设备没连上：先到「设置」页连接设备"
                    : App.Engine.EmergencyStopped
                        ? "急停锁定中：先点侧栏「全部归中」解锁"
                        : "输出被锁：先点侧栏「全部归中」解锁";
        int port = App.Settings.GameBridgePort;
        string brushKey;
        string urlText;

        if (!App.Settings.GameBridgeEnabled)
        {
            BridgeStatusText.Text = "未开启：不占端口、不影响设备（要用的时候再开）";
            brushKey = "Muted";
            urlText = $"开启后，游戏里填：ws://127.0.0.1:{port}";
        }
        else if (!bridge.Active)
        {
            BridgeStatusText.Text = $"启动失败：{bridge.LastError ?? "未知错误"} —— 换一个「监听端口」再开一次。";
            brushKey = "Danger";
            urlText = "桥没起来，游戏连不上；换端口重开即可。";
        }
        else
        {
            string modeLabel = bridge.Mode switch
            {
                "dual" => "双桥（两台设备）",
            "six" => "单桥·六轴直驱",
                _ => "单桥（一台设备）",
            };
            if (bridge.ClientCount > 0)
            {
                BridgeStatusText.Text = $"已开启 · {modeLabel} · 游戏已连上（{bridge.ClientCount} 个连接）";
                brushKey = "Success";
            }
            else
            {
                BridgeStatusText.Text = $"已开启 · {modeLabel} · 还在等游戏连上来（端口 {bridge.Port} 已就绪）";
                brushKey = "Warning";
            }
            urlText = App.Settings.GameBridgeAllowLan
                ? $"本机游戏填：ws://127.0.0.1:{bridge.Port}；VR 头显 / 手机填：ws://这台电脑的局域网地址:{bridge.Port}"
                : $"游戏里填：ws://127.0.0.1:{bridge.Port}（要让 VR 头显/手机连，先展开「少见情况」打开局域网）";
        }

        BridgeStatusText.Foreground = (Brush)FindResource(brushKey);
        if (BridgeStatusBar is not null) BridgeStatusBar.Background = (Brush)FindResource(brushKey);
        if (BridgeUrlText is not null && BridgeUrlText.Text != urlText) BridgeUrlText.Text = urlText;
        BridgeLastCmdText.Text = "最近指令：" + bridge.LastCommand
            + (string.IsNullOrWhiteSpace(bridge.StatusNote) || bridge.StatusNote == "—"
                ? "" : $"    ｜ 状态：{bridge.StatusNote}");
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (combo.SelectedItem == null && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static ComboBoxItem? SelectedItem(ComboBox combo) => combo.SelectedItem as ComboBoxItem;
}
