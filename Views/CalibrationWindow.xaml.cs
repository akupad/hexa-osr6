using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Hexa.Services;

namespace Hexa.Views;

public partial class CalibrationWindow : Window
{
    /// <summary>轴顺序（唯一真源见 <see cref="Hexa.Services.Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] Axes = Hexa.Services.Osr6DeviceProfile.InstalledAxes;

    /// <summary>
    /// 慢速移动：3 秒走完一次指令。本设备是开环的（固件不回报位置），一次跨满行程的快速移动
    /// 可能中途失步、机器停在半路而软件以为到位了，所以「慢一点」默认勾选。
    /// </summary>
    private const int SlowMoveMs = 3000;
    private const int NormalMoveMs = 1200;

    /// <summary>单次移动超过全程（0–9999）的 30% 就先提醒一次，建议分几次小步走。</summary>
    private const int LongMoveWarnRaw = 3000;

    private int _axisIndex;
    private bool _suppressAxisChange;   // 程序回退下拉框时置位，避免 SelectionChanged 递归
    private int? _lastSentValue;        // 本次打开后对本轴发出的最后一个位置；null = 还没发过
    private DispatcherTimer? _moveTimer;
    private bool _moving;               // 「正在移动」提示还在计时
    private string _afterMoveHint = "";
    private bool _closed;

    public CalibrationWindow()
    {
        InitializeComponent();
        foreach (var axis in Axes) AxisCombo.Items.Add(axis);
        AxisCombo.SelectedIndex = 0;
        UpdateAxisUi();
        RefreshStatus();
        // 窗口开着时设备可能掉线 / 急停 / 归中：顶部状态条跟着实时刷新。
        App.Engine.StateChanged += OnEngineStateChanged;
        Closed += OnWindowClosed;
    }

    // ── 状态条 与 常驻急停 ────────────────────────────────────────────

    private void OnEngineStateChanged()
    {
        if (_closed) return;
        App.Dispatch(() =>
        {
            if (_closed) return;
            // 急停是安全状态：**先**按它算一遍按钮能不能点。放在下面那个 return 之前，
            // 是因为「移动中」的提示要等计时器收尾，但急停一旦按下，按钮必须立刻变灰。
            UpdateEStopLockUi();
            if (_moving) return;   // 移动中的提示由计时器收尾，别被状态刷新盖掉
            RefreshStatus();
        });
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        App.Engine.StateChanged -= OnEngineStateChanged;
        _moveTimer?.Stop();
        _moveTimer = null;
    }

    /// <summary>
    /// 急停状态的界面反应：按钮文案 + 禁用所有「会让设备动」的按钮。
    ///
    /// 为什么要有这个：本窗口是模态的，主窗口侧栏的急停在这里点不到，只剩全局热键；
    /// 所以窗口自带一个常驻急停，并且急停锁定后必须把「移动到目标 / 记录为最小 / 记录为最大 /
    /// 全部归中」全部停用 —— 急停之后还点得动「动设备」的按钮，等于急停是假的。
    /// 急停按钮本身永远不禁用：它是这个窗口里唯一的「停」按钮，任何时候都得点得到。
    /// </summary>
    private void UpdateEStopLockUi()
    {
        if (MoveBtn is null) return;   // 构造期极端情况下还没建好控件
        bool locked = App.Engine.EmergencyStopped;
        if (locked && _moving) EndMoveTimer();   // 急停之后还写「正在移动」，那是在骗人

        MoveBtn.IsEnabled = !locked;
        MinBtn.IsEnabled  = !locked;
        MaxBtn.IsEnabled  = !locked;
        HomeBtn.IsEnabled = !locked;

        EStopBtn.Content = locked ? "⛔  已急停（点此解锁）" : "⛔  锁定急停";
        EStopBtn.ToolTip = locked
            ? "设备现在停着、输出锁着。点这里解除：会先问你一次，确认后六个轴回到中间位置，然后才能继续校准。"
            : "立刻停住所有动作并锁住输出。这个按钮任何时候都能点。";
        EStopHint.Text = locked
            ? "急停锁定中：移动 / 记录 / 归中都已停用，先点左边的按钮解除"
            : "停住后设备不会再动，要再动它得先解除急停（会先问你一次）";
    }

    /// <summary>
    /// 常驻急停按钮。没锁定时 = 立刻停住（和主窗口那个按钮同一件事，重复点也无害）；
    /// 已锁定时 = 解除入口，走的是和主窗口「全部归中」完全一样的确认流程。
    /// 之所以让这一个按钮兼职：锁定后「全部归中」已经按要求停用了，这个窗口里总得留一个解除入口。
    /// </summary>
    private void EStop_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.EmergencyStopped)
        {
            UnlockByHoming();
            return;
        }
        App.Engine.EmergencyStop();
        RefreshStatus();
    }

    /// <summary>按当前设备状态刷新顶部状态条；hint 传入时覆盖默认的下一步提示。</summary>
    private void RefreshStatus(string? hint = null)
    {
        UpdateEStopLockUi();   // 状态条和按钮可用性永远一起刷，免得出现「写着急停、按钮还能点」
        bool open = App.Serial.IsOpen;
        bool estop = App.Engine.EmergencyStopped;
        bool homing = App.Engine.IsHoming;

        if (!open)
            SetStatus("Warning", "设备没连接", hint ??
                (estop
                    ? "设备没连接，急停也还锁着：先在设置页连上设备，再点「⛔ 已急停（点此解锁）」恢复。"
                    : "先回设置页连上设备，再回来校准。"));
        else if (estop)
            SetStatus("Danger", "急停锁定中", hint ??
                "设备已经停住、输出锁着，所以「移动 / 记录 / 归中」都停用了。要接着校准，"
                + "点右下角的「⛔ 已急停（点此解锁）」并确认：设备会先回到中间。");
        else if (homing)
            SetStatus("Warning", "正在归中", hint ?? "设备正在回中间，等它停稳再操作。");
        else if (!App.Engine.CanRun)
            SetStatus("Warning", "设备现在不能动", hint ?? "现在发指令设备也不会有反应，先看主窗口左下角的状态。");
        else if (App.Engine.RuleEngineActive)
            SetStatus("Warning", "别的功能正占着设备", hint ?? "游戏伴随 / 声音响应开着时会挡住校准，先去关掉它们。");
        else if (App.Settings.SimulationMode)
            SetStatus("Accent", "模拟设备（不会真的动）", hint ?? "现在没有真实设备接着，点了按钮也不会有动静。");
        else
            SetStatus("Success", "可以校准", hint ?? "输出已解锁；点下面的按钮设备才会动。");
    }

    private void SetStatus(string tone, string title, string hint)
    {
        StatusText.Text = title;
        StatusHint.Text = hint;
        var brush = (Brush)FindResource(tone);
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
        StatusCard.Background = (Brush)FindResource(tone switch
        {
            "Success" => "PrimarySoft",
            "Accent" => "AccentSoft",
            "Danger" => "DangerSoft",
            _ => "WarningSoft",
        });
    }

    // ── 轴与滑杆 ──────────────────────────────────────────────────────

    private void Axis_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAxisChange) return;

        int target = Math.Clamp(AxisCombo.SelectedIndex, 0, Axes.Length - 1);
        if (target == _axisIndex) return;   // 构造期与程序回退都不需要重填

        // 离开当前轴前先比对本框输入与该轴已保存值：不一致说明用户刚改过还没保存，
        // 直接 UpdateAxisUi() 会把输入覆盖掉，所以先确认。
        if (HasUnsavedLimits(Axes[_axisIndex]) &&
            System.Windows.MessageBox.Show(
                $"「{Axes[_axisIndex]}」的限位改了还没保存，换到别的轴就丢了，继续吗？",
                "还没保存", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            // 用户选择不切换：把下拉框改回原轴（置标志位屏蔽本次回退触发的递归）
            _suppressAxisChange = true;
            try { AxisCombo.SelectedIndex = _axisIndex; }
            finally { _suppressAxisChange = false; }
            return;
        }

        _axisIndex = target;
        _lastSentValue = null;   // 换轴后「上一次发到哪」作废
        EndMoveTimer();
        UpdateAxisUi();
        RefreshStatus();
    }

    /// <summary>文本框里的限位是否与该轴已保存的值不一致（解析失败也算未保存）。</summary>
    private bool HasUnsavedLimits(string axis)
    {
        if (!int.TryParse(MinBox.Text, out int min) || !int.TryParse(MaxBox.Text, out int max))
            return true;
        return min != GetMin(axis) || max != GetMax(axis);
    }

    private void UpdateAxisUi()
    {
        string axis = Axes[_axisIndex];
        int min = GetMin(axis), max = GetMax(axis);
        MinBox.Text = min.ToString(); MaxBox.Text = max.ToString();
        TargetSlider.Minimum = 0; TargetSlider.Maximum = 9999;
        TargetSlider.Value = Math.Clamp((min + max) / 2.0, 0, 9999);
        TargetLabel.Text = ((int)TargetSlider.Value).ToString();
        RangeHint.Text = $"现在是 {min}–{max}。范围只能填 0–9999，两头至少差 50；"
                       + "改完要点右下角「保存限位」才算数。";
    }

    private static int GetMin(string axis) => App.Settings.AxisMin.TryGetValue(axis, out var v) ? v : 0;
    private static int GetMax(string axis) => App.Settings.AxisMax.TryGetValue(axis, out var v) ? v : 9999;

    private void Target_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TargetLabel is null) return;   // 构造期可能先于 TargetLabel 触发
        TargetLabel.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    /// <summary>限位框只收数字（0–9999，最多 4 位），省得保存时才发现填错。</summary>
    private void Digits_Only(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(c => !char.IsAsciiDigit(c));

    // ── 移动 ──────────────────────────────────────────────────────────

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        string axis = Axes[_axisIndex];
        int target = (int)Math.Round(TargetSlider.Value);

        if (!App.Engine.CanRun)
        {
            RefreshStatus();   // 状态条已经写明是没连接 / 急停 / 归中 / 被接管
            return;
        }

        // 开环设备没有位置反馈：这里只能用「软件上次发到哪」当参考位置（没发过就用限位中点）。
        int from = _lastSentValue ?? (GetMin(axis) + GetMax(axis)) / 2;
        int distance = Math.Abs(target - from);
        if (distance >= LongMoveWarnRaw &&
            System.Windows.MessageBox.Show(
                $"要把 {axis} 从大概 {from} 挪到 {target}，差不多是全程的 {distance / 100}%。\n\n"
                + "这台设备不会回报自己停在哪：一次挪这么远，中途卡住了软件也不知道。\n"
                + "建议分两三次、每次挪一点地过去，或者勾上「慢一点」。\n\n现在就让它走吗？",
                "跨得有点远", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int ms = SlowMoveCheck.IsChecked == true ? SlowMoveMs : NormalMoveMs;
        if (!App.Engine.TryMoveCalibrationAxis(axis, target, ms))
        {
            // 引擎在输出被锁 / 规则引擎接管时会拒发；不能报成「已发出」，否则用户会以为机器坏了。
            RefreshStatus("设备没接受这条指令：可能还没解锁，或正被游戏 / 声音占着。");
            return;
        }

        _lastSentValue = target;
        ShowMoving("正在移动…",
                   $"{axis} 正用 {ms / 1000.0:0.#} 秒走向 {target}。软件不知道它有没有走到，等它停稳。",
                   "看一眼设备真的到位了没有：到了就点下面的「记录」，没到就再挪一点。",
                   ms);
    }

    private void Home_Click(object sender, RoutedEventArgs e) => UnlockByHoming();

    /// <summary>
    /// 归中 —— 也是本软件里解除急停的唯一办法（Home() 会把 EmergencyStopped 清掉并重新使能输出）。
    /// 所以急停锁定状态下必须先问一句，和主窗口侧栏那个「全部归中」的确认文案保持一致，
    /// 免得用户在校准窗口误触就把急停解了。
    /// </summary>
    private void UnlockByHoming()
    {
        if (!App.Serial.IsOpen)
        {
            SetStatus("Warning", "设备没连接", "连上设备之后才能让它回中间。");
            return;
        }
        // 归中会解除急停并重新使能输出：急停锁定状态下先确认，避免校准窗口误触直接解锁。
        if (App.Engine.EmergencyStopped && System.Windows.MessageBox.Show(
                "设备处于急停锁定，归中会解除急停并重新使能输出，是否继续？",
                "解除急停", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        App.Engine.Home();
        _lastSentValue = null;
        ShowMoving("正在回中间…",
                   "六个轴正用 1 秒回到行程中间；软件不知道它们有没有走到。",
                   "看一眼六个轴是不是都回到中间了。",
                   1000);
    }

    private void ShowMoving(string title, string detail, string afterMove, int ms)
    {
        _moving = true;
        _afterMoveHint = afterMove;
        SetStatus("Accent", title, detail);
        _moveTimer ??= new DispatcherTimer();
        _moveTimer.Stop();
        _moveTimer.Interval = TimeSpan.FromMilliseconds(ms + 500);
        _moveTimer.Tick -= MoveTimer_Tick;
        _moveTimer.Tick += MoveTimer_Tick;
        _moveTimer.Start();
    }

    private void MoveTimer_Tick(object? sender, EventArgs e)
    {
        EndMoveTimer();
        RefreshStatus(_afterMoveHint);
    }

    private void EndMoveTimer()
    {
        _moveTimer?.Stop();
        _moving = false;
    }

    // ── 记录 / 保存 / 关闭 ────────────────────────────────────────────

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        MinBox.Text = ((int)Math.Round(TargetSlider.Value)).ToString();
        MinBox.Focus(); MinBox.SelectAll();   // 焦点落到刚填的框上，让人一眼看到填了什么
        WarnIfReversed();
    }

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        MaxBox.Text = ((int)Math.Round(TargetSlider.Value)).ToString();
        MaxBox.Focus(); MaxBox.SelectAll();
        WarnIfReversed();
    }

    /// <summary>记了两个数但明显填反了（上下限贴在一起）时立刻说清楚，别等到点保存才被打回。</summary>
    private void WarnIfReversed()
    {
        if (!int.TryParse(MinBox.Text, out int min) || !int.TryParse(MaxBox.Text, out int max)) return;
        if (max - min >= 50) return;
        SetStatus("Warning", "这两个数不对",
                  $"最小 {min}、最大 {max} 只差 {max - min}；至少要差 50，不然这个轴会动不了。");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinBox.Text, out var min) || !int.TryParse(MaxBox.Text, out var max))
        {
            SetStatus("Warning", "没保存：数字不对", "最小和最大都要填 0–9999 的整数。");
            return;
        }
        min = Math.Clamp(min, 0, 9999); max = Math.Clamp(max, 0, 9999);
        // 上下限太接近（含 min > max）该轴会被永久钉死：不再静默互换，直接让用户重新校准。
        if (max - min < 50)
        {
            SetStatus("Warning", "没保存：两头离得太近", "最小和最大至少差 50，不然这个轴会动不了。重新量一次再记录。");
            return;
        }
        string axis = Axes[_axisIndex];
        int prevMin = GetMin(axis), prevMax = GetMax(axis);
        App.Settings.AxisMin[axis] = min; App.Settings.AxisMax[axis] = max;
        if (!App.Settings.Save())
        {
            // 写盘失败：回滚内存值，别让界面显示“已保存”而磁盘上还是旧值。
            App.Settings.AxisMin[axis] = prevMin; App.Settings.AxisMax[axis] = prevMax;
            SetStatus("Danger", "保存失败：设置文件写不进去",
                      "设置文件被占用或没有写权限。限位没变，稍后再试一次。");
            return;
        }
        UpdateAxisUi();
        CloseBtn.Content = "完成，回设置页";   // 保存过就顺手把出口写成下一步该去哪
        SetStatus("Success", $"✓ {axis} 已保存 {min}–{max}",
                  "已经写进设置文件了：关掉本窗口，设置页的「轴限位」表就是这套范围。");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedLimits(Axes[_axisIndex]) &&
            System.Windows.MessageBox.Show(
                $"「{Axes[_axisIndex]}」的限位改了还没保存，关掉就丢了。确定关掉吗？",
                "还没保存", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Close();
    }
}
