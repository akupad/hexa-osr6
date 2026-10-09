using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hexa.ViewModels;

namespace Hexa.Views;

public partial class AiAssistantPage : Page
{
    private readonly AiAssistantViewModel _vm = App.AiAssistantVm;

    /// <summary>是否跟随最新消息（用户往上翻看历史时置 false，回到底部后自动恢复）。</summary>
    private bool _followLatest = true;

    public AiAssistantPage()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.Messages.CollectionChanged += (_, _) =>
        {
            ScheduleAutoScroll();
            UpdateAiConfigBanner();   // 空状态自己会讲「去设置填 Key」，别同时再挂一条横幅
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(_vm.IsConnected) or nameof(_vm.SafetyStatus))
                UpdateConnDot();
            else if (e.PropertyName is nameof(_vm.IsAiConfigured))
                UpdateAiConfigBanner();
        };
        UpdateConnDot();
        UpdateAiConfigBanner();

        ChatScroll.ScrollChanged += ChatScroll_ScrollChanged;
        InputBox.TextChanged += (_, _) => ResizeInputBox();
        InputBox.PreviewKeyDown += InputBox_PreviewKeyDown;

        StartThinkingDots();

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _vm.Refresh();
            _followLatest = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ChatScroll.ScrollToEnd();
                UpdateJumpLatestButton();
            }));
        };
    }

    // ── 状态点 / 未配置提示 ────────────────────────────────────────────

    private void UpdateConnDot()
    {
        bool connected = _vm.IsConnected;
        bool stopped = _vm.SafetyStatus.Contains("急停", StringComparison.Ordinal);
        ConnDot.Fill = new SolidColorBrush(!connected
            ? Color.FromRgb(0xEF, 0x44, 0x44)
            : stopped ? Color.FromRgb(0xF5, 0xB8, 0x42)
            : Color.FromRgb(0x22, 0xC5, 0x7A));
    }

    /// <summary>
    /// 未配置 API 的横幅：只在「已经聊起来」之后才顶出来。
    /// 聊天区还是空的时候，空状态里已经有一段同样的引导 + 按钮，两边一起说就是重复。
    /// </summary>
    private void UpdateAiConfigBanner()
    {
        bool show = !_vm.IsAiConfigured && _vm.Messages.Count > 0;
        AiConfigBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 等待回复：三个点轮流亮 ─────────────────────────────────────────

    /// <summary>给「正在思考」的三个圆点加一个循环呼吸动画（放在代码里，避免 XAML 里 Storyboard 解析不到名字）。</summary>
    private void StartThinkingDots()
    {
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddDotPulse(storyboard, Dot1, 0.00);
        AddDotPulse(storyboard, Dot2, 0.15);
        AddDotPulse(storyboard, Dot3, 0.30);
        storyboard.Begin(this, true);
    }

    private static void AddDotPulse(Storyboard storyboard, DependencyObject dot, double beginSeconds)
    {
        var animation = new DoubleAnimation(0.2, 1.0, new Duration(TimeSpan.FromMilliseconds(450)))
        {
            AutoReverse = true,
            BeginTime   = TimeSpan.FromSeconds(beginSeconds),
        };
        Storyboard.SetTarget(animation, dot);
        Storyboard.SetTargetProperty(animation, new PropertyPath("Opacity"));
        storyboard.Children.Add(animation);
    }

    // ── 输入框：Enter 发送 / Shift+Enter 换行 + 随内容长高 ──────────────

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 注意：Key.Enter 和 Key.Return 是同一个值，别写成 or，编译器会判第二个分支不可达。
        if (e.Key != Key.Enter) return;
        // Shift+Enter（以及 Ctrl+Enter）保留给"换行"，其余 Enter 发送。
        if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) return;
        if (!_vm.SendCommand.CanExecute(null))
        {
            // 以前这里静默插一个换行，用户不知道"为什么按了没发出去"（子代理审计发现）。
            string why = _vm.IsBusy
                ? "正在回复中，要停下来请点「取消」。"
                : !_vm.IsAiConfigured
                    ? "还没填 API Key：先点上面的「去设置填 Key」。"
                    : "先写点什么再按 Enter。";
            if (_vm.IsBusy)
            {
                Hexa.Services.AppLogger.Info("AI 助手：Enter 没发出去 —— " + why);
                InputBox.ToolTip = why;   // 忙的时候鼠标停在输入框上就能看到原因（页面已有"取消"按钮可点）
            }
            return;
        }
        _vm.SendCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>输入框最多长到 4 行左右，再多就在框内滚动。</summary>
    private void ResizeInputBox()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            double extent = InputBox.ExtentHeight;
            if (double.IsNaN(extent) || extent <= 0) return;
            double chrome = InputBox.Padding.Top + InputBox.Padding.Bottom + 2;
            double target = Math.Clamp(extent + chrome, 34, 92);
            if (Math.Abs(InputBox.Height - target) > 0.5) InputBox.Height = target;
        }));
    }

    // ── 滚动：只在"贴着底部"时自动跟随，并提供「↓ 最新消息」 ─────────────

    private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // ExtentHeightChange != 0 表示是内容变高（来了新消息），不算用户主动滚动。
        if (Math.Abs(e.ExtentHeightChange) < 0.01)
            _followLatest = IsNearBottom();

        if (_followLatest && (Math.Abs(e.ExtentHeightChange) > 0.01 || Math.Abs(e.VerticalChange) > 0.01))
            ChatScroll.ScrollToVerticalOffset(ChatScroll.ScrollableHeight);

        UpdateJumpLatestButton();
    }

    private bool IsNearBottom() =>
        ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 24;

    private void UpdateJumpLatestButton()
    {
        bool show = _vm.Messages.Count > 0 && !IsNearBottom() && !_followLatest;
        JumpLatestButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleAutoScroll()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_followLatest) ChatScroll.ScrollToEnd();
            UpdateJumpLatestButton();
        }));
    }

    private void JumpLatest_Click(object sender, RoutedEventArgs e)
    {
        _followLatest = true;
        ChatScroll.ScrollToEnd();
        UpdateJumpLatestButton();
    }

    // ── 空状态里的起手话 / 去设置 ──────────────────────────────────────

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Content is not string text) return;
        _vm.InputText = text;
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
        ResizeInputBox();
    }

    /// <summary>
    /// 「⏹ 停一下」：立刻停住设备。
    /// 不走 AI、不填输入框 —— 停设备是安全动作，不该依赖"再点一次发送"或模型是否听话。
    /// </summary>
    private void StopMotion_Click(object sender, RoutedEventArgs e)
    {
        // 归中会解除急停并重新使能输出：急停锁定时必须先问一句（与主窗口/校准窗/设置页/悬浮窗口径一致），
        // 否则用户按"停一下"会把急停悄悄解掉、设备重新带电。
        if (App.Engine.EmergencyStopped && System.Windows.MessageBox.Show(
                "设备处于急停锁定，归中会解除急停并重新使能输出，是否继续？",
                "解除急停", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        App.FunscriptPlayer?.Stop();
        App.RuleEngine?.Stop();
        App.Engine.Home();
        Hexa.Services.AppLogger.Info("AI 助手页：「停一下」已停住设备（脚本 / 游戏伴随已停，输出回中位）");
    }

    /// <summary>跳到「设置」页填 API Key（设置页里就有 AI 助手那一节）。</summary>
    private void GoSettings_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow win)
            win.NavigateTo("settings");
    }

    // ── 导出人格 JSON：复制 / 保存为文件 ──────────────────────────────

    /// <summary>把导出的人格 JSON 复制到剪贴板；剪贴板被占用（COMException）时提示重试。</summary>
    private async void CopyExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.ExportJson))
        {
            System.Windows.MessageBox.Show("还没有可复制的内容，请先点「📤 导出当前人格」。",
                "复制人格 JSON", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(_vm.ExportJson);
        }
        catch (Exception ex)
        {
            // 剪贴板可能被其它程序独占（COMException），重试即可成功。
            System.Windows.MessageBox.Show("复制失败（剪贴板可能被其它程序占用，请稍后重试）：" + ex.Message,
                "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ExportCopyButton.Content = "✅ 已复制";
        await Task.Delay(2000);
        ExportCopyButton.Content = "📋 复制";
    }

    /// <summary>把导出的人格 JSON 另存为 .json 文件。</summary>
    private void SaveExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.ExportJson))
        {
            System.Windows.MessageBox.Show("还没有可保存的内容，请先点「📤 导出当前人格」。",
                "保存人格 JSON", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 人格 Id 可能含非法文件名字符，替换后再拼文件名。
        string personaId = new string(_vm.SelectedPersona.Id
            .Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var dlg = new SaveFileDialog
        {
            Title      = "保存人格 JSON",
            Filter     = "人格卡 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            DefaultExt = ".json",
            FileName   = $"hexa-persona-{personaId}-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, _vm.ExportJson);
            System.Windows.MessageBox.Show($"已保存到：\n{dlg.FileName}", "保存人格 JSON",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("保存失败：" + ex.Message, "保存人格 JSON",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
