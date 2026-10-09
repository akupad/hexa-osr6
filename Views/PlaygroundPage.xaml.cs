using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Hexa.Models;
using Hexa.Services;
using Hexa.ViewModels;

namespace Hexa.Views;

public partial class PlaygroundPage : Page
{
    private readonly PlaygroundViewModel _vm = App.PlaygroundVm;

    // ── 实时轴可视化 / 概览（需求8/9）────────────────────────────
    /// <summary>轴顺序（唯一真源：<see cref="Hexa.Services.Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxisLabels = Hexa.Services.Osr6DeviceProfile.InstalledAxes;

    // ── Gamepad ──
    private volatile bool _gpActive;
    private Thread? _gpThread;
    private readonly double[] _prevGpVals = new double[6];
    private bool   _gpPrevInit;      // 首次采样先填充基线，避免误报活跃度
    private double _gpActivity;      // 手柄活跃度 0..1（摇杆/扳机变化率）

    // ── XInput P/Invoke ──
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);

    // ── BPM tap tempo ──
    private readonly List<long> _tapTimes = new();         // 记录每次 tap 的 Stopwatch ticks
    private const double TapResetMs = 2500;                // 超过此间隔则重置
    private const double RefBpm     = 60.0;                // Speed=1.0 对应 60 BPM
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    // ── 自由游玩参数（自动模式内部参数面板）──
    private readonly System.Windows.Threading.DispatcherTimer _autoParamSaveTimer;
    private bool _autoParamDirty;      // 有未落盘的参数改动

    // ── 参与随机的动作（勾选项 = 自动模式随机池）──
    private readonly List<CheckBox> _patternChecks = new();
    private bool _patternUpdating;     // 程序回填勾选状态时抑制事件，避免递归保存
    private bool _patternComboUpdating; // 程序回填下拉选中项时抑制事件，避免误判成"用户改了波形"

    // P1：Tap 打拍写 SpeedSlider 时抑制 ValueChanged，否则会清空 _tapTimes、BPM 永远算不出平均。
    private bool _suppressSpeedUi;

    // 快速预设的选中态：点过的那个高亮；用户自己再动波形/强度/速度就算离开了该预设。
    private readonly Dictionary<string, Button> _presetButtons = new();
    private string _activePresetId = string.Empty;

    // ── 顶部「为什么不能动」原因条 + 点击被挡下时的闪烁 ──
    private readonly System.Windows.Threading.DispatcherTimer _bannerPulseTimer;
    private string? _bannerKey;         // 当前显示的原因：内容没变就不重复写 UI
    private int _bannerPulseTicks;

    // ── 窄窗口重排 ──
    // 宽：左列固定 344 + 右列余下（原来认可的版式）。
    // 中：两列等分（344 放不下、又还没窄到必须上下堆）。
    // 窄：上下堆叠，两列都占满整行（否则右列会被窗口边缘切掉）。
    private const double SideBySideWidth = 900;   // 低于此宽度，344 + 3D 会互相挤
    private const double StackWidth = 620;        // 低于此宽度，并排已经排不下
    private int _layoutMode = -1;
    private bool _ready;                          // 控件字段已就绪（XAML 解析完成）

    public PlaygroundPage()
    {
        InitializeComponent();

        // 参数改动节流写盘：拖动中不落盘，停手 700ms 后保存一次。
        // 提到最前面建：下面的下拉 / 预设恢复也可能排队落盘（LastAutoPattern / LastQuickPreset）。
        _autoParamSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _autoParamSaveTimer.Tick += (_, _) => FlushAutoParamSave();

        BuildPresetButtons();
        BuildComfortProfiles();
        BuildPatternCombo();
        WireSliderEvents();
        WireAutoParams();
        RestoreLastSelection();   // 回到上次那套（波形 + 快速预设高亮）

        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _statusTimer.Tick += (_, _) => { UpdateLiveBehavior(); RefreshBridgeSectionSummary(); };
        _statusTimer.Start();

        // 被挡下时闪一下原因条：不弹窗打断，但一定看得见"为什么没动"。
        _bannerPulseTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _bannerPulseTimer.Tick += (_, _) =>
        {
            _bannerPulseTicks++;
            if (_bannerPulseTicks >= 6)
            {
                _bannerPulseTimer.Stop();
                DeviceBanner.Opacity = 1;
                return;
            }
            DeviceBanner.Opacity = _bannerPulseTicks % 2 == 0 ? 1 : 0.45;
        };

        // 拉窗口时也要跟着换排法（MeasureOverride 之外再兜一层）
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth);

        // P1：热键/急停/自动结束等非本页操作也会改变 AutoRunning，按钮文案要跟着变。
        // 波形被别处改掉（例：点快速预设、从托盘点预设）时把下拉回填过去，避免下拉显示和实际不符。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.AutoRunning)) UpdateAutoBtn();
            else if (e.PropertyName == nameof(_vm.AutoPattern)) SyncPatternComboFromVm();
        };

        // Sync UI state whenever this cached page becomes visible again
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                FlushAutoParamSave();   // 离开页面时立即落盘，避免未保存的参数改动丢失
                _statusTimer.Stop();    // 看不见的时候不用每 250ms 刷一遍
                return;
            }
            if (!_statusTimer.IsEnabled) _statusTimer.Start();
            RefreshBridgeSectionSummary();
            // 页面被缓存：回到本页时重建一次随机列表 —— 用户可能刚在动作页
            // 新建 / 删除 / 收藏了动作（收藏要排到最前、自建动作要出现在列表里）。
            BuildPatternChecks();
            UpdateAutoBtn();
        };

        // 首次进入时先把状态条 / 按钮可用性摆对，不等 250ms 后的第一拍
        _ready = true;
        ApplyResponsiveLayout(ActualWidth);
        UpdateLiveBehavior();
        RefreshBridgeSectionSummary();   // 桥那一行的摘要在构造时就位，别让用户看到一行空白
    }

    /// <summary>
    /// 重启后回到「上次那套」：
    /// ① 波形 = <c>AppSettings.LastAutoPattern</c>（下拉里没有的 id 一律回自由巡游 ——
    ///    动作被删、配置被手改都不会留下空下拉）；
    /// ② 快速预设高亮 = <c>AppSettings.LastQuickPreset</c>（以前只在内存里，重启就丢了）。
    /// 用户手动改波形 / 强度 / 速度会把它清掉（见 <see cref="ClearActivePreset"/>）。
    /// </summary>
    private void RestoreLastSelection()
    {
        var cfg = App.Settings;
        string last = (cfg.LastAutoPattern ?? "").Trim();
        bool known = PlaygroundViewModel.Patterns
            .Any(item => string.Equals(item.Id, last, StringComparison.OrdinalIgnoreCase));
        _vm.AutoPattern = known ? last.ToLowerInvariant() : "free_play";
        SyncPatternComboFromVm();

        string preset = (cfg.LastQuickPreset ?? "").Trim().ToLowerInvariant();
        if (preset.Length > 0 && _presetButtons.ContainsKey(preset))
        {
            _activePresetId = preset;
            RefreshPresetButtons();
        }
    }

    /// <summary>
    /// 主体排法在测量阶段就定下来：约束宽度 = 页面可用宽度，比只看 SizeChanged 更稳
    /// （自检等场合会自己走 Measure/Arrange，不一定触发 SizeChanged）。
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (_ready && !double.IsInfinity(constraint.Width)) ApplyResponsiveLayout(constraint.Width);
        return base.MeasureOverride(constraint);
    }

    /// <summary>
    /// 按可用宽度重排主体：
    /// 宽窗口 = 左 344px + 右 3D 占满余下（认可的原版式）；
    /// 中等窗口 = 两列等分（硬撑 344 会把 3D 压没、也会把左列控件挤扁）；
    /// 窄窗口（正文约 660px 及以下）= 上下堆叠，两列都占满整行，
    /// 这样既不会被窗口边缘切掉，也不会把滑杆拉成长条。
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;
        int mode = width >= SideBySideWidth ? 0 : width >= StackWidth ? 1 : 2;
        if (mode == _layoutMode) return;
        _layoutMode = mode;

        switch (mode)
        {
            case 0:
                LeftCol.Width  = new GridLength(344);
                GapCol.Width   = new GridLength(14);
                RightCol.Width = new GridLength(1, GridUnitType.Star);
                RightCol.MinWidth = 260;
                Grid.SetRow(RightCard, 0);
                Grid.SetColumn(RightCard, 2);
                RightCard.Margin = new Thickness(0);
                break;

            case 1:
                LeftCol.Width  = new GridLength(1, GridUnitType.Star);
                GapCol.Width   = new GridLength(12);
                RightCol.Width = new GridLength(1, GridUnitType.Star);
                RightCol.MinWidth = 200;
                Grid.SetRow(RightCard, 0);
                Grid.SetColumn(RightCard, 2);
                RightCard.Margin = new Thickness(0);
                break;

            default:
                LeftCol.Width  = new GridLength(1, GridUnitType.Star);
                GapCol.Width   = new GridLength(0);
                RightCol.Width = new GridLength(0);
                RightCol.MinWidth = 0;
                Grid.SetRow(RightCard, 1);
                Grid.SetColumn(RightCard, 0);
                RightCard.Margin = new Thickness(0, 14, 0, 0);
                break;
        }

    }

    private void BuildComfortProfiles()
    {
        ComfortPanel.Children.Clear();
        foreach (var profile in PlaygroundViewModel.ComfortProfiles)
        {
            var button = new Button
            {
                Content = profile.Label,
                Tag = profile.Id,
                Height = 30,
                MinWidth = 72,
                Margin = new Thickness(0, 0, 8, 8),
                Style = (Style)FindResource(profile.Id == _vm.ComfortProfile ? "BtnPrimary" : "BtnSecondary"),
                ToolTip = $"最高强度 {profile.MaxIntensity:P0}",
            };
            button.Click += (_, _) =>
            {
                _vm.ApplyComfortProfileCommand.Execute(profile.Id);
                RefreshComfortButtons();
            };
            ComfortPanel.Children.Add(button);
        }
        RefreshComfortButtons();
    }

    private void RefreshComfortButtons()
    {
        var selected = PlaygroundViewModel.ComfortProfiles.First(profile => profile.Id == _vm.ComfortProfile);
        // 顺手把「这一档最高能到多少强度」写出来：用户不用切过去试才知道被限到哪。
        ComfortDescription.Text = $"{selected.Description} · 强度上限 {selected.MaxIntensity:P0}";
        foreach (Button button in ComfortPanel.Children)
            button.Style = (Style)FindResource((string)button.Tag == selected.Id ? "BtnPrimary" : "BtnSecondary");
        IntensitySlider.Maximum = selected.MaxIntensity;
        if (IntensitySlider.Value > selected.MaxIntensity)
        {
            // 每个舒适档有自己的强度上限，切到更保守的档会把强度压下来。
            // 原来这是静默的（200% 悄悄变成 90%，切回去也不会还原），用户只会觉得"怎么变弱了"。
            double clamped = selected.MaxIntensity;
            IntensitySlider.Value = clamped;
            App.Settings.IntensityScale = clamped;
            App.Settings.Save();
            ComfortHint.Text = $"「{selected.Label}」档最高只能到 {clamped:P0}，已把强度降到 {clamped:P0}。"
                + "想更强请换更激进的档位。";
            ComfortHint.Visibility = Visibility.Visible;
        }
        else if (ComfortHint.Text.StartsWith("「", StringComparison.Ordinal))
        {
            ComfortHint.Visibility = Visibility.Collapsed;
        }
    }

    // ── Quick preset buttons ──
    private void BuildPresetButtons()
    {
        foreach (var (id, label) in PlaygroundViewModel.QuickPresets)
        {
            var btn = new Button
            {
                Content  = label,
                Style    = (Style)FindResource("BtnSecondary"),
                MinWidth = 92,
                Height   = 30,
                Margin   = new Thickness(0, 0, 8, 8),
                Padding  = new Thickness(12, 4, 12, 4),
                FontSize = 12,
                Tag      = id
            };
            btn.Click += (_, _) =>
            {
                // 设备未连接 / 归中 / 急停 / 被游戏伴随接管时不能开始：
                // 不弹窗打断，改成把顶部原因条滚到眼前并闪一下，同时保留"拒绝开始"这道保护。
                if (BlockedReason() is not null)
                {
                    ShowBlockedHint();
                    return;
                }
                _vm.ApplyPresetCommand.Execute(id);
                SpeedSlider.Value = _vm.Speed;
                _activePresetId = id;
                RefreshPresetButtons();
                // 「正在用的快速预设」要能在重启后恢复。
                // 注意顺序：上面 SpeedSlider.Value 赋值会触发 ValueChanged → ClearActivePreset()，
                // 那一步会把刚写下的 LastQuickPreset 清掉，所以这里在它之后再写一次并排队节流落盘。
                App.Settings.LastQuickPreset = id;
                QueueAutoParamSave();
                UpdateAutoBtn();
            };
            _presetButtons[id] = btn;
            PresetPanel.Children.Add(btn);
        }
    }

    /// <summary>高亮当前正在用的快速预设（其它情况一律回到普通样式）。</summary>
    private void RefreshPresetButtons()
    {
        foreach ((string id, Button button) in _presetButtons)
            button.Style = (Style)FindResource(id == _activePresetId ? "BtnPrimary" : "BtnSecondary");
    }

    /// <summary>
    /// 用户手动改了波形 / 强度 / 速度：已经不代表那个预设了，取消高亮，
    /// 并把它从设置里一起清掉 —— 否则下次启动又"回到"一个其实已经改过的预设。
    /// </summary>
    private void ClearActivePreset()
    {
        if (_activePresetId.Length > 0)
        {
            _activePresetId = string.Empty;
            RefreshPresetButtons();
        }
        if (!string.IsNullOrEmpty(App.Settings.LastQuickPreset))
        {
            App.Settings.LastQuickPreset = "";
            QueueAutoParamSave();
        }
    }

    // ── Pattern combo（波形下拉：与「参与随机的动作」同一份列表）──
    /// <summary>
    /// 下拉内容 = <see cref="PlaygroundViewModel.Patterns"/>（自由巡游 + 内置波形 + 自建动作），
    /// 与下面「参与随机的动作」勾选列表同源 —— 同一个页面不会再出现两套"动作"口径。
    /// 结构：值先建完再回填选中项，最后才挂事件，避免初始化阶段被当成"用户改了波形"。
    /// </summary>
    private void BuildPatternCombo()
    {
        _patternComboUpdating = true;
        try
        {
            PatternCombo.Items.Clear();
            foreach (var (id, label) in PlaygroundViewModel.Patterns)
                PatternCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        }
        finally
        {
            _patternComboUpdating = false;
        }

        SyncPatternComboFromVm();

        PatternCombo.SelectionChanged += (_, _) =>
        {
            if (_patternComboUpdating) return;
            if (PatternCombo.SelectedItem is not ComboBoxItem item) return;
            _vm.AutoPattern = (string)item.Tag!;      // 落盘 AppSettings.LastAutoPattern 由 VM 负责
            QueueAutoParamSave();
            ClearActivePreset();
            // P1：自动运行中切波形要立刻写给引擎，否则只改了 VM 的待用值、本次运行无效果。
            if (App.Engine.AutoRunning) App.Engine.AutoPattern = _vm.AutoPattern;
        };
    }

    /// <summary>
    /// 把 VM 里的当前波形回填到下拉（点快速预设、从托盘切预设、重启恢复时都用它）。
    /// 回填过程中抑制 SelectionChanged：那不是"用户改了波形"，不该清预设高亮、也不该再写一次设置。
    /// 下拉里没有这个 id（例如那个自建动作被删了）时退回「自由巡游」，不留一个空的下拉框。
    /// </summary>
    private void SyncPatternComboFromVm()
    {
        if (PatternCombo is null) return;
        _patternComboUpdating = true;
        try
        {
            var item = PatternCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(candidate =>
                string.Equals((string)candidate.Tag, _vm.AutoPattern, StringComparison.OrdinalIgnoreCase));
            if (item is null && PatternCombo.Items.Count > 0)
            {
                _vm.AutoPattern = "free_play";
                item = PatternCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(candidate =>
                    string.Equals((string)candidate.Tag, "free_play", StringComparison.OrdinalIgnoreCase))
                    ?? (ComboBoxItem)PatternCombo.Items[0];
            }
            PatternCombo.SelectedItem = item;
        }
        finally
        {
            _patternComboUpdating = false;
        }
    }

    // ── Slider initial values + wire events ──
    private void WireSliderEvents()
    {
        SpeedSlider.Value = _vm.Speed;
        SpeedLabel.Text   = _vm.Speed.ToString("F1") + "x";
        IntensitySlider.Value = App.Engine.IntensityScale;
        IntensityLabel.Text = $"{App.Engine.IntensityScale:P0}";

        SpeedSlider.ValueChanged += (_, e) =>
        {
            if (_suppressSpeedUi) return;   // Tap 打拍写滑块时不要清 BPM
            _vm.Speed = e.NewValue;
            SpeedLabel.Text = e.NewValue.ToString("F1") + "x";
            // 手动拖速度时清除「点按测得」的显示与预设高亮
            BpmLabel.Text = "";
            ClearActivePreset();
            Views.CompactOverlay.BpmLabel = "";
            _tapTimes.Clear();
        };

        IntensitySlider.ValueChanged += (_, e) =>
        {
            App.Engine.IntensityScale = e.NewValue;
            App.Settings.IntensityScale = e.NewValue;
            IntensityLabel.Text = $"{e.NewValue:P0}";
            ClearActivePreset();
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  自由游玩参数（自动模式内部参数面板）
    //  改动即时写入 AppSettings：序列器每帧读取快照，立即生效；
    //  写盘走 _autoParamSaveTimer 节流，避免拖动时每帧 I/O。
    // ══════════════════════════════════════════════════════════════
    private void WireAutoParams()
    {
        var cfg = App.Settings;

        // 范围取自 AutoPlayParameters 常量，与 AppSettings.Normalize 同源
        BpmRangeSlider.Minimum = AutoPlayParameters.BpmLimitMin;
        BpmRangeSlider.Maximum = AutoPlayParameters.BpmLimitMax;
        DurationRangeSlider.Minimum = AutoPlayParameters.PatternLimitMin;
        DurationRangeSlider.Maximum = AutoPlayParameters.PatternLimitMax;
        TransitionSlider.Minimum = AutoPlayParameters.TransitionLimitMin;
        TransitionSlider.Maximum = AutoPlayParameters.TransitionLimitMax;
        AccelerationSlider.Minimum = AutoPlayParameters.AccelerationLimitMin;
        AccelerationSlider.Maximum = AutoPlayParameters.AccelerationLimitMax;

        // 先写值再挂事件，避免初始化阶段误触发保存（RangeSlider 内部保证 左 ≤ 右）
        BpmRangeSlider.LowerValue = Math.Clamp(cfg.AutoBpmMin,
            BpmRangeSlider.Minimum, BpmRangeSlider.Maximum);
        BpmRangeSlider.UpperValue = Math.Clamp(cfg.AutoBpmMax,
            BpmRangeSlider.Minimum, BpmRangeSlider.Maximum);
        DurationRangeSlider.LowerValue = Math.Clamp(cfg.AutoPatternMinSeconds,
            DurationRangeSlider.Minimum, DurationRangeSlider.Maximum);
        DurationRangeSlider.UpperValue = Math.Clamp(cfg.AutoPatternMaxSeconds,
            DurationRangeSlider.Minimum, DurationRangeSlider.Maximum);
        TransitionSlider.Value = Math.Clamp(cfg.AutoTransitionSeconds,
            TransitionSlider.Minimum, TransitionSlider.Maximum);
        AccelerationSlider.Value = Math.Clamp(cfg.AutoAcceleration,
            AccelerationSlider.Minimum, AccelerationSlider.Maximum);
        ContinuousBpmCheck.IsChecked = cfg.AutoContinuousBpm;
        AccelerationSlider.IsEnabled = cfg.AutoContinuousBpm;
        UpdateAutoParamLabels();

        BpmRangeSlider.LowerValueChanged += (_, _) => ApplyAutoParamsFromUi();
        BpmRangeSlider.UpperValueChanged += (_, _) => ApplyAutoParamsFromUi();
        DurationRangeSlider.LowerValueChanged += (_, _) => ApplyAutoParamsFromUi();
        DurationRangeSlider.UpperValueChanged += (_, _) => ApplyAutoParamsFromUi();
        TransitionSlider.ValueChanged += (_, _) => ApplyAutoParamsFromUi();
        AccelerationSlider.ValueChanged += (_, _) => ApplyAutoParamsFromUi();
        ContinuousBpmCheck.Checked += (_, _) => ApplyAutoParamsFromUi();
        ContinuousBpmCheck.Unchecked += (_, _) => ApplyAutoParamsFromUi();

        BuildPatternChecks();
    }

    /// <summary>面板 → AppSettings（序列器立即读取生效），并排队节流落盘。</summary>
    private void ApplyAutoParamsFromUi()
    {
        var cfg = App.Settings;
        cfg.AutoBpmMin = BpmRangeSlider.LowerValue;
        cfg.AutoBpmMax = BpmRangeSlider.UpperValue;
        cfg.AutoPatternMinSeconds = DurationRangeSlider.LowerValue;
        cfg.AutoPatternMaxSeconds = DurationRangeSlider.UpperValue;
        cfg.AutoTransitionSeconds = TransitionSlider.Value;
        cfg.AutoAcceleration = AccelerationSlider.Value;
        cfg.AutoContinuousBpm = ContinuousBpmCheck.IsChecked == true;
        AccelerationSlider.IsEnabled = cfg.AutoContinuousBpm;
        UpdateAutoParamLabels();
        QueueAutoParamSave();
    }

    private void UpdateAutoParamLabels()
    {
        BpmRangeLabel.Text = $"{BpmRangeSlider.LowerValue:0} – {BpmRangeSlider.UpperValue:0} BPM";
        DurationRangeLabel.Text = $"{DurationRangeSlider.LowerValue:0} – {DurationRangeSlider.UpperValue:0} 秒";
        TransitionLabel.Text = $"{TransitionSlider.Value:0.0} 秒";
        AccelerationLabel.Text = $"{AccelerationSlider.Value:0} 拍/秒";
        UpdateAutoParamsSummary();
    }

    /// <summary>收起时标题右侧的当前值摘要：不展开也能看出现在是什么设置。</summary>
    private void UpdateAutoParamsSummary()
    {
        int checkedCount = _patternChecks.Count(check => check.IsChecked == true);
        string patterns = checkedCount == 0
            ? "只用内置波形"
            : checkedCount == _patternChecks.Count
                ? "全部动作参与"
                : $"{checkedCount}/{_patternChecks.Count} 个动作参与";
        AutoParamsSummary.Text =
            $"快慢 {BpmRangeSlider.LowerValue:0}–{BpmRangeSlider.UpperValue:0} · "
            + $"动作 {DurationRangeSlider.LowerValue:0}–{DurationRangeSlider.UpperValue:0} 秒 · "
            + $"衔接 {TransitionSlider.Value:0.0} 秒 · {patterns}";
    }

    /// <summary>节流保存：停手 700ms 后写盘一次，拖动过程中不落盘。</summary>
    private void QueueAutoParamSave()
    {
        _autoParamDirty = true;
        _autoParamSaveTimer.Stop();
        _autoParamSaveTimer.Start();
    }

    /// <summary>立即落盘（节流计时器到期、或离开页面时调用）。</summary>
    private void FlushAutoParamSave()
    {
        _autoParamSaveTimer.Stop();
        if (!_autoParamDirty) return;
        _autoParamDirty = false;
        App.Settings.Save();
    }

    // ── 参与随机的动作（勾选哪些动作进入自动模式随机池）──────────────
    /// <summary>
    /// 按 <see cref="AutoBehaviorSequencer.AllPatterns"/> 生成两列勾选列表（内置波形 + 自建动作，
    /// 与上面的「波形」下拉同源），并按设置回填初始状态。
    ///
    /// 回填规则（也是"默认手感不变"的地方）：
    /// ・空列表 = 只有内置波形参与（旧行为，用户没勾过任何东西时不该突然多出自己做的动作）；
    /// ・收藏的动作排在最前并标 ⭐，自建动作的标签带「自建 · 」前缀，一眼可分；
    /// ・自建动作默认<b>不勾</b>：要用户明确勾上才进随机池（勾上就是池子的正式成员）。
    /// 勾选改动即时写入 AppSettings，写盘复用参数面板的节流。
    /// </summary>
    private void BuildPatternChecks()
    {
        var cfg = App.Settings;
        var enabled = new HashSet<string>(
            cfg.EnabledAutoPatterns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        bool noneConfigured = enabled.Count == 0;   // 空列表 = 只用内置波形
        var favorites = new HashSet<string>(_vm.FavoriteStrokeIds, StringComparer.OrdinalIgnoreCase);

        // 收藏的排最前（OrderByDescending 是稳定排序，其余项保持原顺序）
        var items = AutoBehaviorSequencer.AllPatterns
            .OrderByDescending(pattern => favorites.Contains(AutoBehaviorSequencer.UsageKey(pattern)))
            .ToList();

        _patternUpdating = true;
        try
        {
            _patternChecks.Clear();
            PatternCheckPanel.Children.Clear();
            foreach (var pattern in items)
            {
                bool favorite = favorites.Contains(AutoBehaviorSequencer.UsageKey(pattern));
                string label = (favorite ? "⭐ " : "")
                    + (pattern.IsCustom ? "自建 · " + pattern.Label : pattern.Label);

                // 先写值（IsChecked）再挂事件，避免初始化阶段误触发保存
                var check = new CheckBox
                {
                    // 用 TextBlock 而不是字符串：动作名可能很长（自建动作尤其），
                    // 这样能在单元格里省略号收尾，不会把勾选框挤出卡片。
                    Content = new TextBlock
                    {
                        Text = label,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = pattern.IsCustom
                            ? $"自建动作（{pattern.Id}）—— 来自动作页的动作库"
                            : $"内置波形（{pattern.Id}）",
                    },
                    Tag = pattern.Id,
                    // 自建动作没人勾过就不参与；内置波形在"没配置过"时全参与（旧行为）
                    IsChecked = pattern.IsCustom ? enabled.Contains(pattern.Id)
                                                 : noneConfigured || enabled.Contains(pattern.Id),
                    FontSize = 12,
                    Margin = new Thickness(0, 3, 8, 3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _patternChecks.Add(check);
                PatternCheckPanel.Children.Add(check);
            }
        }
        finally
        {
            _patternUpdating = false;
        }

        foreach (var check in _patternChecks)
        {
            check.Checked += PatternCheck_Changed;
            check.Unchecked += PatternCheck_Changed;
        }
        RefreshPatternSelectAll();
    }

    /// <summary>
    /// 有自建动作、但一个都没勾进随机池时，在波形下拉下面明说一句。
    /// 为什么需要：勾选列表藏在收起的面板里，不提示的话用户只会觉得
    /// "我自己做的动作怎么永远抽不到"（这正是这次要修的那个 bug 的观感）。
    /// </summary>
    private void UpdateCustomPoolHint()
    {
        if (CustomPoolHint is null) return;

        int customTotal = _patternChecks.Count(check =>
            ((string)check.Tag).StartsWith(AutoBehaviorSequencer.CustomStrokePrefix, StringComparison.OrdinalIgnoreCase));
        int customChecked = _patternChecks.Count(check =>
            check.IsChecked == true
            && ((string)check.Tag).StartsWith(AutoBehaviorSequencer.CustomStrokePrefix, StringComparison.OrdinalIgnoreCase));

        if (customTotal == 0 || customChecked > 0)
        {
            CustomPoolHint.Visibility = Visibility.Collapsed;
            return;
        }

        CustomPoolHint.Text =
            $"你有 {customTotal} 个自建动作还没进随机池：展开下面的「自由游玩参数」，"
            + "在「参与随机的动作」里勾上它们；想单独一直做它，就在上面的「波形」里直接选。";
        CustomPoolHint.Visibility = Visibility.Visible;
    }

    private void PatternCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_patternUpdating) return;
        ApplyPatternSelectionFromUi();
    }

    /// <summary>
    /// 顶部「全选 / 全不选」：按子项当前状态决定——未全选则全选，已全选则全不选。
    /// 三态复选框点击后会自行循环到 Indeterminate，这里在 _patternUpdating 保护下强制回写两态。
    /// </summary>
    private void PatternSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_patternUpdating) return;
        bool selectAll = _patternChecks.Any(check => check.IsChecked != true);
        _patternUpdating = true;
        try
        {
            foreach (var check in _patternChecks) check.IsChecked = selectAll;
        }
        finally
        {
            _patternUpdating = false;
        }
        ApplyPatternSelectionFromUi();
    }

    /// <summary>勾选 → AppSettings（序列器取样时立即读取），并排队节流落盘。</summary>
    private void ApplyPatternSelectionFromUi()
    {
        if (_patternUpdating) return;
        var cfg = App.Settings;
        var selected = _patternChecks
            .Where(check => check.IsChecked == true)
            .Select(check => (string)check.Tag)
            .ToList();
        // 一个都没勾 → 写空列表 = 只用内置波形（兼容旧配置，序列器不会陷入空池）；
        // 勾了东西就<b>明写整份列表</b>：自建动作必须显式出现在列表里才会进随机池
        // （空列表的含义是"只用内置波形"，否则全选之后自建动作反而抽不到）。
        // 整体替换引用而不是原地增删，避免音频线程枚举时读到半成品。
        cfg.EnabledAutoPatterns = selected.Count == 0 ? new List<string>() : selected;
        RefreshPatternSelectAll();
        QueueAutoParamSave();
    }

    /// <summary>按子项勾选数量刷新三态复选框与右侧提示文字。</summary>
    private void RefreshPatternSelectAll()
    {
        int checkedCount = _patternChecks.Count(check => check.IsChecked == true);
        _patternUpdating = true;
        try
        {
            if (checkedCount == 0) PatternSelectAllCheck.IsChecked = false;
            else if (checkedCount == _patternChecks.Count) PatternSelectAllCheck.IsChecked = true;
            else PatternSelectAllCheck.IsChecked = null;
        }
        finally
        {
            _patternUpdating = false;
        }
        PatternSelectionHint.Text = checkedCount == 0
            ? "没勾 = 只用内置波形"
            : checkedCount == _patternChecks.Count
                ? "全部参与"
                : $"已选 {checkedCount}/{_patternChecks.Count} 项";
        UpdateCustomPoolHint();
        UpdateAutoParamsSummary();
    }

    private void AutoParamsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool expand = AutoParamsBody.Visibility != Visibility.Visible;
        AutoParamsBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        AutoParamsToggleText.Text = expand ? "▾ 自由游玩参数" : "▸ 自由游玩参数";
        // 收起时右侧那行摘要才有意义；展开后下面全是具体数值，摘要就是重复信息了。
        AutoParamsSummary.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 🌉 游戏桥：默认收起。它整卡带「指令监视」日志，摊开就是半页，而这类游戏是少数人偶尔用；
    /// 收成一行后，摘要里照样写着「开没开 / 游戏里该填什么 / 收发了多少条」。
    /// </summary>
    private void BridgeSection_Click(object sender, RoutedEventArgs e)
    {
        bool expand = BridgeSectionBody.Visibility != Visibility.Visible;
        BridgeSectionBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        BridgeSectionTitle.Text = expand ? "▾ 🌉 游戏桥" : "▸ 🌉 游戏桥";
        BridgeSectionSummary.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
        if (!expand) RefreshBridgeSectionSummary();   // 收起时补一次，别留着展开前的旧数字
    }

    /// <summary>桥收起时那一行摘要：开没开 / 游戏里该填什么 / 收发计数。</summary>
    private void RefreshBridgeSectionSummary()
    {
        if (BridgeSectionBody.Visibility == Visibility.Visible) return;   // 展开了就不用刷这行（看不见）
        if (!App.Settings.GameBridgeEnabled)
        {
            BridgeSectionSummary.Text = "没开：不占端口、游戏连不上；要用时点开";
            return;
        }
        var bridge = App.Bridge;
        if (!bridge.Active)
        {
            // 以前只看"设置里开着没"，端口被占导致桥没起来时这一行仍写"已开启"，与桥面板的"启动失败"互相打脸。
            BridgeSectionSummary.Text = string.IsNullOrWhiteSpace(bridge.LastError)
                ? "没开起来：点开这一栏看原因"
                : $"启动失败：{bridge.LastError} —— 点开换一个「监听端口」再开";
            return;
        }
        BridgeSectionSummary.Text =
            $"已开启 · 游戏里填 ws://127.0.0.1:{bridge.Port} · 收到 {bridge.RxCount} / 发出 {bridge.TxCount}"
            + (bridge.ClientCount > 0 ? $" · 游戏已连（{bridge.ClientCount}）" : " · 等游戏连");
    }

    // ── Auto mode ──
    private void AutoBtn_Click(object sender, RoutedEventArgs e)
    {
        // 停止不受限制：急停/断连时也要允许点“停止自动”。
        if (!App.Engine.AutoRunning && BlockedReason() is not null)
        {
            ShowBlockedHint();
            return;
        }
        _vm.ToggleAutoCommand.Execute(null);
        UpdateAutoBtn();
    }

    /// <summary>
    /// 现在不能开始的原因（白话 + 去哪里解决）；能正常开始就返回 null。
    /// 与 <see cref="MotionEngine.CanRun"/> 相比多了一档「被游戏伴随接管」——
    /// 那种情况下 CanRun 为真、但 StartAuto 会直接返回 false，不说明白就是"点了没反应"。
    /// </summary>
    private static string? BlockedReason()
    {
        if (!App.Serial.IsOpen)
            return "⚠ 设备还没连接：现在点「开始自动」或快速预设都不会动。到「⚙ 设置」页点「立即连接」。";
        if (App.Engine.EmergencyStopped)
            return "⛔ 设备处于急停锁定：点左下角侧栏的「全部归中」解锁（会先弹确认）。";
        if (App.Engine.IsHoming)
            return "⏳ 设备正在归中：等它停稳就能开始。";
        if (App.Engine.RuleEngineActive)
            return "🎮「声音响应 / 游戏伴随」正在接管设备：先到「🧪 测试台」把它关掉，再回来手动开始。";
        return null;
    }

    /// <summary>把「为什么不能开始」的原因条刷新到当前状态；能跑就整条收起。</summary>
    private void UpdateDeviceBanner()
    {
        string? reason = BlockedReason();
        if (reason is null)
        {
            if (_bannerKey is null) return;
            _bannerKey = null;
            DeviceBanner.Visibility = Visibility.Collapsed;
            return;
        }

        // 两档颜色：未连接 / 急停是"真不能动"（红），归中 / 被接管是"稍等就能动"（黄）
        bool danger = !App.Serial.IsOpen || App.Engine.EmergencyStopped;
        string key = (danger ? "danger:" : "warn:") + reason;
        if (key == _bannerKey) return;
        _bannerKey = key;

        DeviceBanner.Background   = (Brush)FindResource(danger ? "DangerSoft" : "WarningSoft");
        DeviceBanner.BorderBrush  = (Brush)FindResource(danger ? "Danger" : "Warning");
        DeviceBannerText.Foreground = (Brush)FindResource(danger ? "Danger" : "Warning");
        DeviceBannerText.Text = reason;
        DeviceBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 点了开始（或快速预设）却动不了时的反馈：把原因条摆到眼前闪一下。
    /// 以前是弹 MessageBox——模态框打断操作、还会挡住刚点的东西；原因条本身已经写清
    /// "为什么不能动 + 去哪儿解决"，所以这里只做"让你看见"，拒绝开始的保护一点没少。
    /// </summary>
    private void ShowBlockedHint()
    {
        UpdateDeviceBanner();
        DeviceBanner.Visibility = Visibility.Visible;
        DeviceBanner.BringIntoView();
        _bannerPulseTicks = 0;
        _bannerPulseTimer.Stop();
        DeviceBanner.Opacity = 1;
        _bannerPulseTimer.Start();
    }

    private void UpdateAutoBtn()
    {
        bool on = _vm.AutoRunning;
        AutoBtn.Content = on ? "⏹ 停止自动" : "▶ 开始自动";
        AutoBtn.Style   = (Style)FindResource(on ? "BtnDanger" : "BtnPrimary");
        UpdateLiveBehavior();
    }

    /// <summary>没在跑时三个动作按钮无效（引擎里也是直接 return），灰掉比"点了没反应"清楚。</summary>
    private void UpdateAutoActionButtons()
    {
        bool running = App.Engine.AutoRunning;
        HoldBehaviorBtn.IsEnabled = running;
        NextBehaviorBtn.IsEnabled = running;
        EaseBehaviorBtn.IsEnabled = running;

        bool held = running && App.Engine.AutoBehaviorHeld;
        HoldBehaviorBtn.Content = held ? "🔒 已锁住" : "⏸ 锁住动作";
        HoldBehaviorBtn.Style = (Style)FindResource(held ? "BtnPrimary" : "BtnSecondary");
    }

    private void GamepadFold_Click(object sender, RoutedEventArgs e)
    {
        bool show = GamepadBody.Visibility != Visibility.Visible;
        GamepadBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GamepadFoldSummary.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        GamepadFoldBtn.Content = (show ? "▾" : "▸") + " 🕹 游戏手柄";
        if (!show) RefreshGamepadSummary();
    }

    /// <summary>收起状态下把当前手柄状态写成一行摘要（不用展开就知道插没插、能不能用）。</summary>
    private void RefreshGamepadSummary()
    {
        if (GamepadFoldBtn is null || GamepadFoldSummary is null) return;
        if (GamepadBody.Visibility == Visibility.Visible) return;
        string status = GamepadStatus?.Text ?? "";
        GamepadFoldSummary.Text = string.IsNullOrWhiteSpace(status) || status.StartsWith("还没检测", StringComparison.Ordinal)
            ? "没启用：手柄不影响设备；要用时点开"
            : status;
    }

    private void UpdateLiveBehavior()
    {
        if (!App.Engine.AutoRunning)
        {
            AutoStateDot.Fill = (Brush)FindResource("Muted");
            AutoBehaviorLabel.Text = "还没开始";
            AutoBpmLiveLabel.Text = "";
            // 右侧只留"现在能不能动"一句话，原因和解决办法由顶部原因条负责，不重复。
            string state;
            string color;
            if (!App.Serial.IsOpen)                { state = "不可动 · 未连接"; color = "Warning"; }
            else if (App.Engine.EmergencyStopped)  { state = "不可动 · 急停";   color = "Danger"; }
            else if (App.Engine.IsHoming)          { state = "不可动 · 归中中"; color = "Warning"; }
            else if (App.Engine.RuleEngineActive)  { state = "被游戏伴随接管";  color = "Warning"; }
            else if (App.Engine.DriverLabel is string driver && driver != "待机")
                                                   { state = $"正在动 · {driver}"; color = "Warning"; }
            else                                   { state = "待机 · 可动";     color = "Muted"; }
            LiveStateLabel.Text = state;
            LiveStateLabel.Foreground = (Brush)FindResource(color);
        }
        else
        {
            // 顶部状态条只在这里显示实时 BPM（"当前"）；点按测出来的那个在下面 Tap 行写"测得"，
            // 两个数字不会再被看混。
            AutoStateDot.Fill = (Brush)FindResource("Accent");
            AutoBehaviorLabel.Text = $"正在跑：{App.Engine.CurrentAutoPatternLabel}";
            AutoBpmLiveLabel.Text = $"当前 {App.Engine.CurrentAutoBpm:0} BPM";
            LiveStateLabel.Text = App.Engine.AutoBehaviorHeld ? "运行中 · 已锁住" : "运行中";
            LiveStateLabel.Foreground = (Brush)FindResource("Accent");
        }
        UpdateDeviceBanner();
        UpdateAutoActionButtons();
    }

    private void NextBehavior_Click(object sender, RoutedEventArgs e)
    {
        App.Engine.NextAutoBehavior();
        UpdateLiveBehavior();
    }

    private void EaseBehavior_Click(object sender, RoutedEventArgs e)
    {
        App.Engine.EaseAutoBehavior();
        IntensitySlider.Value = App.Engine.IntensityScale;
        UpdateLiveBehavior();
    }

    /// <summary>锁住当前动作（再点一次解锁）：引擎本来就有这个能力，只是界面一直没有入口。</summary>
    private void ToggleHold_Click(object sender, RoutedEventArgs e)
    {
        App.Engine.ToggleAutoBehaviorHold();
        UpdateLiveBehavior();
    }

    // ══════════════════════════════════════════════════════════════
    //  BPM TAP TEMPO
    // ══════════════════════════════════════════════════════════════
    private void TapBtn_Click(object sender, RoutedEventArgs e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double freqMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

        // 距上次 tap 超过 TapResetMs → 重置
        if (_tapTimes.Count > 0)
        {
            double gapMs = (now - _tapTimes[^1]) / freqMs;
            if (gapMs > TapResetMs) _tapTimes.Clear();
        }

        _tapTimes.Add(now);

        // 保留最近 8 次，避免窗口漂移
        if (_tapTimes.Count > 8) _tapTimes.RemoveAt(0);

        if (_tapTimes.Count < 4)
        {
            // 界面文案写的是"点 4 下以上"，这里就按 4 下判定：不足时给进度，别 2 下就宣布测到 BPM。
            BpmLabel.Text = $"再点 {4 - _tapTimes.Count} 下…";
            return;
        }
        if (_tapTimes.Count < 2)
        {
            BpmLabel.Text = "再点 1 下…";
            Views.CompactOverlay.BpmLabel = "";
            return;
        }

        // 用首尾 interval 平均计算 BPM
        double totalMs = (_tapTimes[^1] - _tapTimes[0]) / freqMs;
        double bpm     = (_tapTimes.Count - 1) * 60000.0 / totalMs;
        double speed   = Math.Clamp(bpm / RefBpm, 0.1, 3.0);

        // 应用到引擎与滑块
        App.Engine.Speed = speed;
        _vm.Speed        = speed;
        Dispatcher.Invoke(() =>
        {
            // P1：抑制 ValueChanged，避免它清空 _tapTimes 并把 BPM 标签写回“-- BPM”。
            _suppressSpeedUi = true;
            try
            {
                SpeedSlider.Value = speed;
            }
            finally
            {
                _suppressSpeedUi = false;
            }
            SpeedLabel.Text = $"{speed:F1}x";
        });
        ClearActivePreset();

        string display = $"测得 {bpm:F0} BPM";
        BpmLabel.Text = display;
        Views.CompactOverlay.BpmLabel = display;
    }

    // ══════════════════════════════════════════════════════════════
    //  GAMEPAD — XInput polling on a background thread
    // ══════════════════════════════════════════════════════════════
    private void GamepadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_gpActive)
        {
            _gpActive = false;
            _gpActivity = 0;
            // 停用手柄必须交还控制权：否则"手柄"会一直挂在驱动者上（设备明明静止、界面却写"运行中 · 手柄"），
            // 而且急停/归中都不清这个标记，只能重启软件。
            App.Engine.ReleaseDirectInput("gamepad");
            App.RuleEngine?.SetGamepadActivity(0);
            App.Engine.ApplyGamepadActivity(0);
            GamepadBtn.Content = "启用手柄";
            GamepadBtn.Style   = (Style)FindResource("BtnSecondary");
            GamepadStatus.Text = "手柄已停用（设备不会再跟着手柄动）";
            GamepadStatus.Foreground = (Brush)FindResource("Muted");
        }
        else
        {
            _gpActive = true;
            GamepadBtn.Content = "停用手柄";
            GamepadBtn.Style   = (Style)FindResource("BtnDanger");
            GamepadStatus.Text = "正在找手柄…";
            GamepadStatus.Foreground = (Brush)FindResource("Muted");
            _gpThread = new Thread(GamepadLoop) { IsBackground = true };
            _gpThread.Start();
        }
    }

    /// <summary>手柄连上了、但推不动设备时的原因（白话，跟页面顶部的写法一致）。</summary>
    private static string GamepadBusyReason()
    {
        if (!App.Engine.CanRun) return "设备现在不能动（原因见页面顶部提示）";
        if (App.Engine.RuleEngineActive) return "游戏伴随正在接管设备";
        if (App.Engine.ScriptPlaying) return "脚本正在播放（它会一直占着设备，先停脚本）";
        if (App.Engine.IsRunning) return "设备正在跑自动动作";
        if (App.Engine.IsEasing) return "设备正在过渡中";
        return "设备暂时不接受手柄直控";
    }

    private void GamepadLoop()
    {
        const uint ERROR_DEVICE_NOT_CONNECTED = 1167;
        var state  = new XINPUT_STATE();
        var cfg    = App.Settings.GamepadMap;   // 可配置映射

        while (_gpActive)
        {
            uint result = XInputGetState(0, ref state);
            if (result == ERROR_DEVICE_NOT_CONNECTED)
            {
                App.Dispatch(() =>
                {
                    GamepadStatus.Text       = "没检测到手柄：插上 USB 手柄，这里会自动认出来";
                    GamepadStatus.Foreground = (Brush)FindResource("Muted");
                });
                Thread.Sleep(1000);
                continue;
            }

            // 构造原始值字典（供 GamepadConfig.ReadAxis 解析）
            var gp = state.Gamepad;
            var raw = new Dictionary<string, double>
            {
                ["LeftTrigger"]   = gp.bLeftTrigger,
                ["RightTrigger"]  = gp.bRightTrigger,
                ["LeftStickX"]    = gp.sThumbLX,
                ["LeftStickY"]    = gp.sThumbLY,
                ["RightStickX"]   = gp.sThumbRX,
                ["RightStickY"]   = gp.sThumbRY,
            };

            var vals = new double[6];
            for (int i = 0; i < 6; i++)
                vals[i] = cfg.ReadAxis(i, raw);

            // 计算手柄活跃度（摇杆/扳机变化率）供规则引擎使用
            if (_gpPrevInit)
            {
                double deltaSum = 0;
                for (int i = 0; i < 6; i++)
                {
                    deltaSum += Math.Abs(vals[i] - _prevGpVals[i]);
                    _prevGpVals[i] = vals[i];
                }
                double inst = Math.Clamp(deltaSum / 6.0 / 100.0, 0, 1);
                _gpActivity += (inst - _gpActivity) * 0.2;   // EMA 平滑
            }
            else
            {
                for (int i = 0; i < 6; i++) _prevGpVals[i] = vals[i];
                _gpPrevInit = true;
            }
            App.RuleEngine?.SetGamepadActivity(_gpActivity);
            App.Engine.ApplyGamepadActivity(_gpActivity);

            App.Dispatch(() =>
            {
                // 手柄同样属于「直接下发源」：必须认领控制权，否则
                //  ① 悬浮窗/侧栏看不到"真在动"（急停按钮会变灰、ToolTip 还写"现在没有东西在动"）；
                //  ② 脚本正在播时两路交替写同一根轴（机器抖）。
                // 摇杆回到中位就交还，避免"只是插着手柄"也一直占着控制权。
                bool engaged = _gpActivity > 0.02;
                bool claimed = false;
                if (engaged)
                {
                    claimed = App.Engine.TryClaimDirectInput("gamepad");
                    if (claimed && App.Engine.CanAcceptDirectInput) App.Engine.TrySendDirectAxes(vals);
                }
                else
                {
                    App.Engine.ReleaseDirectInput("gamepad");
                }

                // 连上了不等于推得动：自动 / 脚本 / 游戏伴随跑着的时候，手柄下发会被引擎拒绝。
                // 这里把"为什么推不动"直接写出来，省得用户以为手柄坏了。
                // engaged=摇杆在中位。空闲不是故障：此时 claimed 为 false 是正常的，
                // 以前照抄成"手柄暂时无效"，用户会以为手柄坏了（摇杆一动文字就翻回来）。
                bool usable = engaged ? claimed : App.Engine.CanAcceptDirectInput;
                string text = usable
                    ? "✓ 手柄已连接，动摇杆设备就跟着动"
                    : $"✓ 手柄已连接，但{GamepadBusyReason()}，手柄暂时无效";
                if (GamepadStatus.Text != text)
                {
                    GamepadStatus.Text = text;
                    GamepadStatus.Foreground = (Brush)FindResource(usable ? "Success" : "Warning");
                }
                RefreshGamepadSummary();
            });
            Thread.Sleep(50);
        }
    }

}
