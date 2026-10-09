using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hexa.Services;

namespace Hexa.Views;

/// <summary>
/// 「手动动轴」页：左边一组滑杆手动驱动六轴，右边 <see cref="AxisPreview3D"/> 实时跟着动。
///
/// 安全约定（改这个页面时不要绕过）：
/// ① 默认<b>不</b>驱动设备 —— 必须用户显式勾选「允许手动动轴」，勾选之前滑杆是灰的、
///    本页一条指令都不发。
/// ② 「谁在驱动设备」只有<b>一个</b>事实源：<see cref="MotionEngine.DirectInputOwner"/>。
///    本页用 <c>TryClaimDirectInput("manual")</c> 拿控制权、<c>ReleaseDirectInput("manual")</c> 交还，
///    自己<b>不</b>记「我正在驱动」这类标志位（这里只读引擎、不写第二份状态）。
/// ③ 拿不到控制权就让位：页面写清「现在由 X 在驱动，先停掉它」，绝不 force 抢占。
/// ④ 取消勾选 / <see cref="UIElement.IsVisibleChanged"/> 变 false / Unloaded / 急停 /
///    断线 / 归中 / 被别人接管 —— 任何一条都要立刻停发并交还控制权。
///    停手<b>不会</b>把轴移回原位：设备就停在停下那一刻的位置。
/// ⑤ 下发节流 ≤20Hz，每帧 6 个值一次性发（插值 0.12 秒），实际速度交给引擎的安全限速器。
/// ⑥ 本页还有一个「〰 让它自己摆」的正弦发生器：它<b>不</b>另造控制权和标志位，
///    就挂在同一个 <c>_sendTimer</c> 上、用同一个 owner；摆动与手动拖动互斥（同一时刻只有一路在发）。
///    任何停手路径都收敛到 <see cref="StopManual"/>，所以它跟着一起停。
/// </summary>
public partial class ManualControlPage : Page
{
    /// <summary>本页在 <see cref="MotionEngine.DirectInputOwner"/> 里的名字。</summary>
    private const string InputOwner = "manual";

    /// <summary>滑杆量程（与设备固件一致）：0 = 一端，9999 = 另一端，5000 = 正中间。</summary>
    private const double SliderMax = 9999;
    private const double SliderCenter = 5000;

    /// <summary>下发节流：50ms = 20Hz，是允许的上限。</summary>
    private const double SendIntervalMs = 50;

    /// <summary>状态/安全闸刷新间隔：急停一按下，最多 200ms 内滑杆就会变灰并停手。</summary>
    private const double StatusIntervalMs = 200;

    /// <summary>每帧的插值时间：比 50ms 的下发间隔长，设备才不会一顿一顿。</summary>
    private const double InterpolationSeconds = 0.12;

    /// <summary>正弦摆动的插值时间（秒）：同样比 50ms 的帧间隔长一点，摆动才顺。</summary>
    private const double SineInterpolationSeconds = 0.08;

    /// <summary>速度滑杆的范围与默认值（次/秒 = 每秒摆多少个来回）。</summary>
    private const double SineMinHz = 0.05;
    private const double SineMaxHz = 2.0;
    private const double SineDefaultHz = 0.5;

    /// <summary>默认只给 L0 一个非 0 幅度：一上来六轴齐动太吓人。</summary>
    private const double SineDefaultAmplitudePercent = 40;

    /// <summary>每根轴错开 1/6 个周期（六轴完全同步看起来像机械抽搐）。</summary>
    private const double SinePhaseStepRadians = Math.PI / 3;

    /// <summary>轴 id（唯一真源：<see cref="Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxisIds = Osr6DeviceProfile.InstalledAxes;

    /// <summary>给人看的中文轴名。后半段与 <c>Sr6Rig</c> 的部件对应关系一致：L0 行程 / L1 前后 / L2 左右 / R0 扭转 / R1 滚转 / R2 俯仰。</summary>
    private static readonly string[] AxisNames = ["L0 行程", "L1 前后", "L2 左右", "R0 扭转", "R1 滚转", "R2 俯仰"];

    private static readonly string[] AxisTooltips =
    [
        "L0 行程：接收器前后推进的那根轴（最常用的主轴）",
        "L1 前后：四根主臂一起前后倾",
        "L2 左右：四根连杆一起左右摆",
        "R0 扭转：机头外壳左右拧",
        "R1 滚转：左右两条俯仰臂一上一下",
        "R2 俯仰：俯仰连杆带动接收头抬头 / 低头",
    ];

    private readonly Slider[] _sliders = new Slider[6];
    private readonly TextBlock[] _readouts = new TextBlock[6];

    /// <summary>复用的下发缓冲（6 个 0~100 的引擎口径值），避免每 50ms 分配一个数组。</summary>
    private readonly double[] _sendBuffer = new double[6];

    // ── 「〰 让它自己摆」正弦发生器 ──
    /// <summary>六根轴各自的摆动幅度滑杆（0–100%，0 = 这根轴不参与）。</summary>
    private readonly Slider[] _ampSliders = new Slider[6];
    private readonly TextBlock[] _ampReadouts = new TextBlock[6];

    /// <summary>正弦帧缓冲（和 <see cref="_sendBuffer"/> 分开：自检要能在停止后读到最后一帧）。</summary>
    private readonly double[] _sineValues = new double[6];

    /// <summary>摆动是否正在进行。<b>它只是"本页这一路在发"</b> —— 谁握着设备仍然只看引擎。</summary>
    private bool _sineRunning;

    /// <summary>摆动计时（t 从按下「开始摆动」算起）。</summary>
    private Stopwatch? _sineClock;

    private readonly DispatcherTimer _sendTimer;
    private readonly DispatcherTimer _statusTimer;

    /// <summary>程序改勾选状态时抑制事件（避免自己触发自己的「用户取消勾选」路径）。</summary>
    private bool _suppressCheck;

    /// <summary>停止后要写在状态条里的一句话（"已停手 —— …"），下一次开开关时清掉。</summary>
    private string? _stopNote;

    private bool _ready;

    // ── 窄窗口重排（照抄游玩页：宽 ≥900 并排、620~900 等分、<620 上下堆叠）──
    private const double SideBySideWidth = 900;
    private const double StackWidth = 620;
    private int _layoutMode = -1;

    public ManualControlPage()
    {
        InitializeComponent();

        BuildAxisRows();
        BuildAmplitudeRows();

        _sendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SendIntervalMs) };
        _sendTimer.Tick += (_, _) => SendTick();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StatusIntervalMs) };
        _statusTimer.Tick += (_, _) => PollSafety();

        // 拉窗口时也要跟着换排法（MeasureOverride 之外再兜一层）
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth);

        Loaded += (_, _) =>
        {
            if (IsVisible) _statusTimer.Start();
            RefreshStatus();
        };
        Unloaded += (_, _) => StopManual("已停手 —— 页面已关闭，设备停在原地不动。");

        // 页面被缓存：切走时 IsVisible 变 false，切回来又变 true。
        // 切走必须先停手（绝不能留着设备在动），切回来只恢复状态刷新。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _statusTimer.Start();
                RefreshStatus();
                return;
            }
            StopManual("已停手 —— 你离开了这个页面，设备停在原地不动。");
        };

        _ready = true;
        RefreshReadouts();
        RefreshSineUi();
        ApplyResponsiveLayout(ActualWidth);
        RefreshStatus();
    }

    /// <summary>是否勾了「允许手动动轴」（用户的意图）。</summary>
    private bool AllowOn => AllowCheck.IsChecked == true;

    /// <summary>
    /// 本页这一刻是不是真的握着设备控制权 —— 只问引擎，不问自己记的变量
    /// （「谁在驱动设备」的单一事实源）。
    /// </summary>
    private static bool IsManualDriving =>
        string.Equals(App.Engine.DirectInputOwner, InputOwner, StringComparison.Ordinal);

    /// <summary>
    /// 主体排法在测量阶段就定下来（自检等场合会自己走 Measure/Arrange，不一定触发 SizeChanged）。
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (_ready && !double.IsInfinity(constraint.Width)) ApplyResponsiveLayout(constraint.Width);
        return base.MeasureOverride(constraint);
    }

    /// <summary>
    /// 宽窗口 = 左 380px + 右 3D 占满余下；中等窗口 = 两列等分（硬撑 380 会把 3D 压没）；
    /// 窄窗口（约 660px 及以下）= 上下堆叠，两列都占满整行，不会被窗口边缘切掉、
    /// 也不会把滑杆拉成横条。
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
                LeftCol.Width = new GridLength(380);
                GapCol.Width = new GridLength(14);
                RightCol.Width = new GridLength(1, GridUnitType.Star);
                RightCol.MinWidth = 260;
                PlaceRightCard(row: 0, column: 2, new Thickness(0));
                break;

            case 1:
                LeftCol.Width = new GridLength(1, GridUnitType.Star);
                GapCol.Width = new GridLength(12);
                RightCol.Width = new GridLength(1, GridUnitType.Star);
                RightCol.MinWidth = 200;
                PlaceRightCard(row: 0, column: 2, new Thickness(0));
                break;

            default:
                LeftCol.Width = new GridLength(1, GridUnitType.Star);
                GapCol.Width = new GridLength(0);
                RightCol.Width = new GridLength(0);
                RightCol.MinWidth = 0;
                PlaceRightCard(row: 1, column: 0, new Thickness(0, 14, 0, 0));
                break;
        }
    }

    private void PlaceRightCard(int row, int column, Thickness margin)
    {
        Grid.SetRow(RightCard, row);
        Grid.SetColumn(RightCard, column);
        RightCard.Margin = margin;
    }

    // ══════════════════════════════════════════════════════════════
    //  六根滑杆
    // ══════════════════════════════════════════════════════════════

    private void BuildAxisRows()
    {
        AxisRows.Children.Clear();
        for (int i = 0; i < 6; i++)
        {
            int index = i;

            var label = new TextBlock
            {
                Text = AxisNames[i],
                FontSize = 11,
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = AxisTooltips[i],
            };

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = SliderMax,
                Value = SliderCenter,
                SmallChange = 20,      // 方向键点一下 = 20 个点，够细
                LargeChange = 400,     // 点空白轨道 = 400 个点，不会被"点一下飞到底"吓到
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "0 = 一端，9999 = 另一端，5000 = 正中间",
            };
            slider.ValueChanged += (_, _) => OnSliderChanged(index);
            _sliders[i] = slider;

            var readout = new TextBlock
            {
                Text = "5000",
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Consolas,Cascadia Mono,monospace"),
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                MinWidth = 36,
                Margin = new Thickness(8, 0, 0, 0),
            };
            _readouts[i] = readout;

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(readout, 2);
            row.Children.Add(label);
            row.Children.Add(slider);
            row.Children.Add(readout);
            AxisRows.Children.Add(row);
        }
    }

    private void OnSliderChanged(int index)
    {
        if (!_ready) return;
        UpdateReadout(index);
        RefreshRawValues();
    }

    private void UpdateReadout(int index)
    {
        if (_readouts[index] is null) return;
        _readouts[index].Text = ((int)Math.Round(_sliders[index].Value)).ToString();
    }

    private void RefreshReadouts()
    {
        for (int i = 0; i < 6; i++) UpdateReadout(i);
        RefreshRawValues();
    }

    /// <summary>3D 下面那行小字：当前六轴的原始值（0–9999，就是滑杆上的数）。</summary>
    private void RefreshRawValues()
    {
        if (RawValues is null) return;
        var parts = new string[6];
        for (int i = 0; i < 6; i++)
            parts[i] = $"{AxisIds[i]} {_sliders[i].Value:0}";
        // 滑杆是"目标值"而不是"当前位置"：以前写着"当前六轴原始值"却读滑杆，
        // 设备停在 8000 而页面写 5000，同一屏里数字与右边 3D 模型互相矛盾。现在把真实姿态也写出来。
        string text = "滑杆目标值（0–9999）：" + string.Join("   ", parts);
        try
        {
            double[] snapshot = App.Engine.GetLastOutputSnapshot();
            if (snapshot.Length >= 6)
            {
                var actual = new string[6];
                for (int i = 0; i < 6; i++)
                    actual[i] = $"{AxisIds[i]} {Math.Round(Math.Clamp(snapshot[i], 0, 100) * 99.99):0}";
                // 措辞改准：开环设备不回报位置，这是"最近一次下发值"，不是测量出来的当前位置。
        text += "     最近下发：" + string.Join("  ", actual);
            }
        }
        catch { /* 读不到就只显示目标值 */ }
        RawValues.Text = text;
    }

    // ══════════════════════════════════════════════════════════════
    //  开 / 停（唯一入口：勾选框、离开页面、安全闸）
    // ══════════════════════════════════════════════════════════════

    private void AllowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCheck || !_ready) return;
        if (AllowOn) TurnOn();
        else StopManual("已停手 —— 你关掉了「允许手动动轴」，设备停在原地不动。");
    }

    /// <summary>
    /// 勾上开关：先过安全闸，再向引擎认领控制权。任何一步不通过都退回「关」，
    /// 并把<b>为什么</b>写在状态条里（不弹窗打断）。
    /// </summary>
    private void TurnOn()
    {
        string? reason = BlockedReason(out _);

        if (reason is null && !App.Engine.TryClaimDirectInput(InputOwner))
            reason = $"现在由「{OwnerLabel()}」在驱动设备，手动动轴让位：先去把它停掉，再回来勾这个开关。";

        // 拿到控制权后再确认一次设备真的收得下（认领与状态检查之间可能有人抢先）
        if (reason is null && !App.Engine.CanAcceptDirectInput)
            reason = "设备现在不收手动指令（可能在放脚本 / 过渡中 / 被别的模式接管），先停下它。";

        if (reason is not null)
        {
            App.Engine.ReleaseDirectInput(InputOwner);
            SetAllowChecked(false);
            _sendTimer.Stop();
            _stopNote = "现在不能动 —— " + reason;
            RefreshStatus();
            return;
        }

        _stopNote = null;
        // 本页按帧喂数据，关掉控件自己的 120ms 自动刷新，免得两路数据互相盖造成闪动
        ManualPreview.AutoRefreshEnabled = false;
        ManualPreview.SetStatusText("手动预览");
        _sendTimer.Start();
        RefreshStatus();
    }

    /// <summary>
    /// 停手：停定时器 → 交还控制权 → 取消勾选 → 3D 回到「跟着设备真实输出刷新」。
    /// <b>不</b>把轴移回原位。
    ///
    /// 本页<b>所有</b>停手路径（取消勾选 / 离开页面 / Unloaded / 急停 / 安全轮询 /
    /// 被别人接管 / 按「■ 停止」）都走这里，所以正弦摆动也在这里一起停 —— 不需要各自记一份。
    /// </summary>
    private void StopManual(string note)
    {
        if (!_ready) return;
        _sineRunning = false;
        _sineClock = null;
        _sendTimer.Stop();
        App.Engine.ReleaseDirectInput(InputOwner);   // 只释放自己持有的
        SetAllowChecked(false);
        _stopNote = note;
        ManualPreview.AutoRefreshEnabled = true;
        ManualPreview.SetStatusText("实时输出");
        RefreshStatus();
    }

    private void SetAllowChecked(bool value)
    {
        _suppressCheck = true;
        try { AllowCheck.IsChecked = value; }
        finally { _suppressCheck = false; }
    }

    // ══════════════════════════════════════════════════════════════
    //  下发（20Hz 节流）
    // ══════════════════════════════════════════════════════════════

    private void SendTick()
    {
        // 控制权被别处拿走 / 已经交还：立刻停发，不在没控制权的时候写设备
        if (!IsManualDriving)
        {
            if (_sineRunning)
            {
                StopManual("已停手 —— 控制权被别的功能拿走了，设备停在原地不动。");
                return;
            }
            _sendTimer.Stop();
            RefreshStatus();
            return;
        }

        string? reason = BlockedReason(out _);
        if (reason is not null)
        {
            StopManual("已停手 —— " + reason);
            return;
        }

        // 同一时刻只有一路在发：要么按正弦摆，要么跟着滑杆走。
        if (_sineRunning)
        {
            SendSineFrame();
            RefreshSineUi();     // 状态行跟着幅度/速度的实时值走
        }
        else
        {
            SendManualFrame();
        }
    }

    /// <summary>手动模式的一帧：六个滑杆值 → 引擎口径的 0–100 一次发出去。</summary>
    private void SendManualFrame()
    {
        for (int i = 0; i < 6; i++)
            _sendBuffer[i] = _sliders[i].Value / SliderMax * 100.0;

        // 6 个值一次性发出去；实际速度由引擎的安全限速器决定
        App.Engine.TrySendDirectAxes(_sendBuffer, InterpolationSeconds);
        ManualPreview.UpdateAxes(_sendBuffer);
    }

    // ══════════════════════════════════════════════════════════════
    //  〰 正弦发生器（让它自己摆）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 正弦的一帧：<c>值 = 50 + 幅度/2 × sin(2π·f·t + 相位)</c>，每根轴相位错开 1/6 周期。
    /// 幅度按滑杆上的百分数直接用，所以 100% 恰好是 0–100 全程来回、绝不会越界。
    /// 六轴一次发出去，速度交给引擎的安全限速器（本页只管给目标值）。
    /// </summary>
    private void SendSineFrame()
    {
        Stopwatch clock = _sineClock ??= Stopwatch.StartNew();
        double omegaT = 2 * Math.PI * SpeedHz * clock.Elapsed.TotalSeconds;
        for (int i = 0; i < 6; i++)
            _sineValues[i] = 50 + _ampSliders[i].Value / 2.0 * Math.Sin(omegaT + i * SinePhaseStepRadians);

        App.Engine.TrySendDirectAxes(_sineValues, SineInterpolationSeconds);
        ManualPreview.UpdateAxes(_sineValues);
    }

    private double SpeedHz => Math.Clamp(SpeedSlider?.Value ?? SineDefaultHz, SineMinHz, SineMaxHz);

    /// <summary>六根轴当前的幅度之和（=0 表示一根都不参与，不许开始）。</summary>
    private double TotalAmplitude()
    {
        double sum = 0;
        foreach (Slider slider in _ampSliders) sum += slider?.Value ?? 0;
        return sum;
    }

    /// <summary>现在摆的是哪几根轴（收起时那行摘要、以及状态行都用它）。</summary>
    private string AmplitudeSummary()
    {
        var parts = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            int percent = (int)Math.Round(_ampSliders[i].Value);
            if (percent > 0) parts.Add($"{AxisIds[i]} {percent}%");
        }
        return parts.Count == 0 ? "六根轴都是 0%" : string.Join("，", parts);
    }

    /// <summary>
    /// 能不能开始摆动；不能就把原因（大白话 + 去哪儿解决）交出去。
    /// 顺序 = 本页前置（勾选 / 幅度 / 控制权）→ 页面共用的安全闸。
    /// </summary>
    private bool CanStartSine(out string? why)
    {
        if (!AllowOn)
        {
            why = "先在左边勾上「允许手动动轴」，这个按钮才会亮。";
            return false;
        }
        if (TotalAmplitude() <= 0)
        {
            why = "至少给一根轴一个幅度（现在六根轴全是 0%）。";
            return false;
        }
        // 控制权是单一事实源：不握着就绝不发（勾选那一步失败 / 中途被交还时都会走到这里）
        if (!IsManualDriving)
        {
            why = "本页现在没握着设备控制权：把「允许手动动轴」重新勾一次。";
            return false;
        }
        string? blocked = BlockedReason(out _);
        if (blocked is not null)
        {
            why = blocked;
            return false;
        }
        why = null;
        return true;
    }

    /// <summary>
    /// 正弦卡的按钮与状态：能不能开始（不能就把原因写在按钮下面）、正在摆时写实时幅度/速度。
    /// 按钮可用性只有这一处说了算。
    /// </summary>
    private void RefreshSineUi()
    {
        if (!_ready || SineToggleBtn is null) return;

        bool canStart = CanStartSine(out string? why);

        string summary = TotalAmplitude() <= 0
            ? "现在六根轴都是 0%：至少给一根轴一个幅度才能开始。"
            : "摆动的轴：" + AmplitudeSummary() + "（其余轴不动）";
        if (SineFoldSummary.Text != summary) SineFoldSummary.Text = summary;

        string state;
        string tone;
        if (_sineRunning)
        {
            state = $"正在摆动：每秒 {SpeedHz:0.00} 个来回，幅度 {AmplitudeSummary()}。按「■ 停止」就停，停在原地不回中间。";
            tone = "Success";
        }
        else if (_stopNote is not null)
        {
            state = _stopNote;      // 停手路径写下的那句话（「已停手 —— …」）也写在卡里
            tone = "Muted";
        }
        else if (canStart)
        {
            state = "还没开始。点「▶ 开始摆动」，设备就按上面的幅度一直摆。";
            tone = "Muted";
        }
        else
        {
            state = "现在不能开始 —— " + why;
            tone = "Warning";
        }
        if (SineState.Text != state)
        {
            SineState.Text = state;
            SineState.Foreground = (Brush)FindResource(tone);
        }

        string speed = $"{SpeedHz:0.00} 次/秒";
        if (SpeedReadout.Text != speed) SpeedReadout.Text = speed;

        var label = _sineRunning ? "■ 停止" : "▶ 开始摆动";
        if (!Equals(SineToggleBtn.Content, label)) SineToggleBtn.Content = label;
        bool enabled = _sineRunning || canStart;
        if (SineToggleBtn.IsEnabled != enabled) SineToggleBtn.IsEnabled = enabled;
    }

    private void BuildAmplitudeRows()
    {
        AmplitudeRows.Children.Clear();
        for (int i = 0; i < 6; i++)
        {
            int index = i;

            var label = new TextBlock
            {
                Text = AxisNames[i],
                FontSize = 11,
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "给这根轴一个摆动幅度：" + AxisTooltips[i],
            };

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                // 默认只给 L0 一个非 0 幅度：一上来六轴齐动太吓人
                Value = i == 0 ? SineDefaultAmplitudePercent : 0,
                SmallChange = 5,
                LargeChange = 25,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "0% = 这根轴不参与摆动，100% = 从一端摆到另一端",
            };
            slider.ValueChanged += (_, _) => OnAmplitudeChanged(index);
            _ampSliders[i] = slider;

            var readout = new TextBlock
            {
                Text = "0%",
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Consolas,Cascadia Mono,monospace"),
                Foreground = (Brush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                MinWidth = 36,
                Margin = new Thickness(8, 0, 0, 0),
            };
            _ampReadouts[i] = readout;

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(readout, 2);
            row.Children.Add(label);
            row.Children.Add(slider);
            row.Children.Add(readout);
            AmplitudeRows.Children.Add(row);

            UpdateAmplitudeReadout(i);
        }
    }

    private void UpdateAmplitudeReadout(int index)
    {
        if (_ampReadouts[index] is null) return;
        _ampReadouts[index].Text = ((int)Math.Round(_ampSliders[index].Value)) + "%";
    }

    /// <summary>拖动幅度滑杆：摆动中立刻生效（下一帧就按新幅度算），并刷新摘要/按钮可用性。</summary>
    private void OnAmplitudeChanged(int index)
    {
        if (!_ready) return;
        UpdateAmplitudeReadout(index);
        RefreshSineUi();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        RefreshSineUi();
    }

    private void SineFold_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool expand = SineFoldBody.Visibility != Visibility.Visible;
        SineFoldBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        SineFoldBtn.Content = expand ? "▾ 每根轴的幅度（进阶）" : "▸ 每根轴的幅度（进阶）";
        if (expand) SineFoldSummary.Visibility = Visibility.Collapsed;
        else SineFoldSummary.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 「▶ 开始摆动 / ■ 停止」。开始前必须过 <see cref="CanStartSine"/>（没勾允许 / 六轴全 0 /
    /// 有安全闸拦着都点不动，真被程序调到了也绝不发一条指令）；停止走 <see cref="StopManual"/>，
    /// 也就是"停在原地、不回中间"。
    /// </summary>
    private void SineToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        if (_sineRunning)
        {
            StopManual("已停手 —— 你按了「■ 停止」，设备停在原地不动（没有回到中间）。");
            return;
        }

        if (!CanStartSine(out _))
        {
            RefreshSineUi();     // 只把「为什么不能开始」写清楚，不弹窗、不发指令
            return;
        }

        _sineClock = Stopwatch.StartNew();
        _sineRunning = true;
        RefreshSineUi();
        RefreshStatus();
    }

    /// <summary>
    /// 安全闸（200ms 一拍）：握着控制权但已经不允许动了（急停 / 断线 / 归中 / 被别人接管），
    /// 立刻停手并写清原因。
    /// </summary>
    private void PollSafety()
    {
        string? reason = BlockedReason(out _);
        if (IsManualDriving && reason is not null)
        {
            StopManual("已停手 —— " + reason);
            return;
        }
        RefreshStatus();
    }

    // ══════════════════════════════════════════════════════════════
    //  安全闸 / 状态
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 现在<b>不能</b>手动驱动的原因（白话 + 去哪儿解决）；可以动就返回 null。
    /// 顺序 = 先真不能动的硬锁，再"稍等 / 先让位"的软挡。
    /// </summary>
    private static string? BlockedReason(out bool danger)
    {
        danger = false;

        if (!App.Serial.IsOpen)
        {
            danger = true;
            return "设备还没连接：到侧栏下面的「⚙ 设置」页点「立即连接」，再回来。";
        }
        if (App.Engine.EmergencyStopped)
        {
            danger = true;
            return "设备处于急停锁定：先点侧栏的「⬆ 全部归中」解锁（会先问你一次），再回来。";
        }
        if (!App.Serial.OutputEnabled)
        {
            danger = true;
            return "设备的输出被锁着：点侧栏「⬆ 全部归中」解锁（会先问你一次）。";
        }
        if (App.Engine.IsHoming) return "设备正在归中：等它停稳就能动。";

        string? owner = App.Engine.DirectInputOwner;
        if (owner is { Length: > 0 } && !string.Equals(owner, InputOwner, StringComparison.Ordinal))
            return $"现在由「{OwnerName(owner)}」在驱动设备：手动动轴让位，先去把它停掉。";

        if (App.Engine.RuleEngineActive)
            return "「声音响应 / 游戏伴随」正在接管设备：先到「🧪 测试台」把它关掉。";
        if (App.Engine.IsRunning) return "设备正在跑自动动作 / 脚本：先停下它再来手动拖。";
        if (App.Engine.IsEasing) return "设备正在过渡中：等它停稳。";
        return null;
    }

    /// <summary>这一刻是谁在驱动设备（给人看的中文名）。</summary>
    private static string OwnerLabel()
    {
        string? owner = App.Engine.DirectInputOwner;
        if (owner is { Length: > 0 }) return OwnerName(owner);
        return App.Engine.DriverLabel;   // 自动动作 / 脚本播放 / 待机 …
    }

    private static string OwnerName(string owner) => owner switch
    {
        InputOwner => "本页滑杆（手动）",
        "audio" => "声音响应",
        "bridge" => "游戏桥",
        "ayva" => "网页遥控器",
        "screen" => "画面跟随",
        _ => owner,
    };

    /// <summary>刷新三行状态 + 「为什么不能动」条 + 滑杆可用性。UI 内容没变就不重写。</summary>
    private void RefreshStatus()
    {
        if (!_ready) return;

        bool open = App.Serial.IsOpen;
        bool output = App.Serial.OutputEnabled;
        bool estop = App.Engine.EmergencyStopped;
        bool homing = App.Engine.IsHoming;

        string connection = !open ? "设备：没连接"
            : output ? "设备：已连接，输出正常"
            : "设备：已连接，但输出被关掉了";
        if (ConnectionLine.Text != connection)
        {
            ConnectionLine.Text = connection;
            ConnectionLine.Foreground = (Brush)FindResource(open && output ? "Text" : "Warning");
        }

        string safety = estop ? "安全：⛔ 急停锁定中"
            : homing ? "安全：⏳ 正在归中"
            : "安全：正常，可以动";
        if (SafetyLine.Text != safety)
        {
            SafetyLine.Text = safety;
            SafetyLine.Foreground = (Brush)FindResource(estop ? "Danger" : homing ? "Warning" : "Success");
        }

        string driver = "现在在驱动设备的是：" + (IsManualDriving ? "本页滑杆（手动）" : OwnerLabel());
        if (DriverLine.Text != driver) DriverLine.Text = driver;

        string? reason = BlockedReason(out bool danger);
        if (reason is not null) ShowBanner((danger ? "⛔ " : "⚠ ") + reason, danger);
        else if (_stopNote is not null) ShowBanner(_stopNote, danger: false);
        else if (BlockedBanner.Visibility != Visibility.Collapsed) BlockedBanner.Visibility = Visibility.Collapsed;

        RefreshGate();
    }

    private void ShowBanner(string text, bool danger)
    {
        BlockedBanner.Background = (Brush)FindResource(danger ? "DangerSoft" : "WarningSoft");
        BlockedBanner.BorderBrush = (Brush)FindResource(danger ? "Danger" : "Warning");
        BlockedText.Foreground = (Brush)FindResource(danger ? "Danger" : "Warning");
        if (BlockedText.Text != text) BlockedText.Text = text;
        BlockedBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 能不能拖：既要握着控制权，也要没有任何一条安全闸拦着。
    /// 不能拖时滑杆和「全部归中」一起禁用 —— 灰掉比"点了没反应"清楚。
    /// 正在按正弦摆动时同理：那一路在发，手动这一路先灰掉（要手动就先按「■ 停止」）。
    /// 急停按钮和勾选框永远可点（勾选框必须能取消，急停必须一键可达）。
    /// </summary>
    private void RefreshGate()
    {
        bool canDrive = IsManualDriving && BlockedReason(out _) is null && !_sineRunning;

        foreach (Slider slider in _sliders)
            if (slider.IsEnabled != canDrive) slider.IsEnabled = canDrive;
        if (CenterBtn.IsEnabled != canDrive) CenterBtn.IsEnabled = canDrive;

        string hint = _sineRunning ? "正在按正弦摆动：先按「■ 停止」，再手动拖"
            : canDrive ? "拖动任一滑杆，设备立刻跟着走"
            : AllowOn ? "现在不能动，原因看下面的提示"
            : "先勾上面的「允许手动动轴」，滑杆才会松开";
        if (AxisHint.Text != hint) AxisHint.Text = hint;

        RefreshSineUi();
    }

    // ══════════════════════════════════════════════════════════════
    //  按钮
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 六根轴一起回 5000 中点。不弹确认、也不是猛冲：滑杆值一改，
    /// 下一次 20Hz 下发就带着它走，插值 0.12 秒 + 引擎的安全限速器决定实际速度。
    /// </summary>
    private void CenterBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (Slider slider in _sliders) slider.Value = SliderCenter;
        RefreshReadouts();
    }

    /// <summary>本页自带的急停：任何时候都点得到（主窗口侧栏那个离得远，这一页在动设备）。</summary>
    private void EStopBtn_Click(object sender, RoutedEventArgs e)
    {
        App.Engine.EmergencyStop();
        StopManual("已停手 —— 你按了急停，设备已锁死。要再动它，先点侧栏的「⬆ 全部归中」解锁（会先问你一次）。");
    }
}
