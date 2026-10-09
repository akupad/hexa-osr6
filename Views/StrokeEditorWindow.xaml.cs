using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hexa.Models;
using Hexa.Services;
using Hexa.ViewModels;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Polyline = System.Windows.Shapes.Polyline;

namespace Hexa.Views;

/// <summary>
/// 动作编辑器（对应 ayva-stroker-lite 的 TempestStrokeEditor + TempestMotion）。
/// 每轴一行：Range 双旋钮（两个旋钮可互相穿过）+ Phase(−4..4) + Eccentricity(−1..1)
/// + Noise(from/to, 0..1) + 运动函数（正弦/抛物/线性）；
/// 下方 Canvas 用 MotionEngine.SampleTempestAxis 采样 200 点实时画波形，并画相位进度点。
/// 注意：本窗口只做波形预览，不驱动真实设备（避免误动作）。
/// </summary>
public partial class StrokeEditorWindow : Window
{
    private static readonly string[] AxisNames = ["L0", "L1", "L2", "R0", "R1", "R2"];

    private static readonly Color[] AxisColors =
    [
        Color.FromRgb(0xa7, 0x8b, 0xfa), Color.FromRgb(0x67, 0xe8, 0xf9),
        Color.FromRgb(0x86, 0xef, 0xac), Color.FromRgb(0xfb, 0xa3, 0x4a),
        Color.FromRgb(0xf4, 0x72, 0x72), Color.FromRgb(0xf9, 0xd7, 0x2e),
    ];

    /// <summary>动作分类：Value = 写进 presets.json 的单字（和动作库筛选一致），显示时带上中文解释。</summary>
    private static readonly CategoryItem[] CatNames =
    [
        new("推", "推进"),
        new("滚", "滚动"),
        new("长", "长行程"),
        new("碾", "碾压"),
        new("挑", "挑逗"),
        new("涡", "漩涡"),
        new("自", "自定义"),
    ];

    /// <summary>分类下拉项：下拉里显示「推 · 推进」，保存时只取 Value 那个单字。</summary>
    private sealed record CategoryItem(string Value, string Name)
    {
        public override string ToString() => $"{Value} · {Name}";
    }

    private readonly StrokePreset? _editPreset;
    private readonly HashSet<int> _selectedAxes = [];
    private readonly List<AxisRow> _rows = [];
    private readonly CheckBox[] _axisChecks = new CheckBox[6];
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private bool _suspendAxisEvents;
    private double _previewAngle;
    private long _lastTick;
    private bool _previewPaused;   // 「暂停」只让那张预览图停下，不碰任何参数
    private bool _rowsBuilt;       // 行控件建出来之前不做「轴」这项校验，免得刚开窗就报错

    // ── 未保存守卫（P0：点「取消」/ 点窗口 X 不再静默丢掉调好的参数）──
    private bool _isDirty;        // 用户改过任一参数（轴勾选 / Range / 相位 / 偏心 / 噪声 / 运动函数 / 名称 / 分类）
    private bool _suppressDirty;  // 初始化、程序化赋值期间抑制 MarkDirty
    private bool _saved;          // 保存成功；关闭时不再提示「放弃改动」

    // 每轴编辑行缓存：取消勾选再勾回来时沿用之前调好的参数，而不是退回预设原值（P1）
    private readonly AxisRow?[] _rowCache = new AxisRow?[6];

    /// <summary>保存成功后写回的预设实例（原地编辑时就是被改的那个对象）。</summary>
    public StrokePreset? SavedPreset { get; private set; }

    /// <summary>true = 本次保存新增了动作（调用方需要把它并入列表显示）。</summary>
    public bool CreatedNew { get; private set; }

    public StrokeEditorWindow() : this(null) { }

    public StrokeEditorWindow(StrokePreset? edit)
    {
        _suppressDirty = true;          // 下面的赋值都算初始化，不是「用户改动」
        InitializeComponent();
        _editPreset = edit;

        BuildAxisChecks();
        BuildCatCombo();

        if (edit != null)
        {
            NameBox.Text = edit.Id;
            DisplayNameBox.Text = edit.Label ?? "";
            CatCombo.SelectedItem = CatNames.FirstOrDefault(c => c.Value == edit.Cat) ?? CatNames[0];
        }
        else
        {
            CatCombo.SelectedIndex = 0;
        }

        InitSelection();
        RebuildRows();
        ValidateName();

        // 分类也算动作内容；事件挂在初始化赋值之后，避免刚开窗就变「脏」
        CatCombo.SelectionChanged += (_, _) => { MarkDirty(); UpdateSaveState(); };
        _suppressDirty = false;

        // 标题直接说明「新建」还是「改哪个动作」，省得用户不确定自己在改谁
        Title = edit == null ? "新建动作 · 动作编辑器" : $"编辑「{edit.Label ?? edit.Id}」· 动作编辑器";

        _previewTimer.Tick += _previewTick;                 // 上面声明的方法引用，关闭时能解绑
        WaveCanvas.SizeChanged += _waveCanvasResized;
        AxisRowsPanel.SizeChanged += _rowsPanelResized;
        Loaded += (_, _) =>
        {
            // 想改名字就能直接打字，不用先点一下输入框
            NameBox.Focus();
            NameBox.SelectAll();
            _lastTick = Stopwatch.GetTimestamp();
            _previewTimer.Start();
        };
        Deactivated += (_, _) => _previewTimer.Stop();   // 窗口不在前台就别空转动画

        // 关闭守卫：有未保存改动时先确认，避免误点「取消」或点 X 丢掉参数
        Closing += (_, e) =>
        {
            if (_isDirty && !_saved && DialogResult != true &&
                System.Windows.MessageBox.Show("有未保存的改动，确定放弃？", "动作编辑器",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer.Stop();
        _previewTimer.Tick -= _previewTick;
        WaveCanvas.SizeChanged -= _waveCanvasResized;
        AxisRowsPanel.SizeChanged -= _rowsPanelResized;
        base.OnClosed(e);
    }

    /// <summary>命名方法而非 lambda：Closed 时才能精确解绑，不残留事件引用。</summary>
    private void _previewTick(object? sender, EventArgs e) => AdvancePreview();
    private void _waveCanvasResized(object sender, SizeChangedEventArgs e) => RedrawWave();
    private void _rowsPanelResized(object sender, SizeChangedEventArgs e) => ApplyTrackWidths();

    /// <summary>标记「有未保存改动」；初始化/程序化赋值期间由 _suppressDirty 抑制。</summary>
    private void MarkDirty()
    {
        if (_suppressDirty) return;
        _isDirty = true;
    }

    // ══════════════════════════════════════════════════════════════
    //  单轴行模型
    // ══════════════════════════════════════════════════════════════

    /// <summary>一条轴的编辑状态 + 对应控件引用（波形绘制、UI 同步都靠它）。</summary>
    private sealed class AxisRow
    {
        /// <summary>行程条轨道宽度：随窗口宽度自动伸缩，避免窄窗口把右边的控件挤出画面。</summary>
        public double TrackWidth = 220;

        public int Index;
        public string Name = "";
        public Color Color = Colors.White;

        public double From = 0.5;
        public double To = 0.5;
        public double Phase;
        public double Ecc;
        public double NoiseFrom;
        public double NoiseTo;
        public StrokeMotion Motion = StrokeMotion.Sinusoidal;

        public Canvas Panel = null!;
        public Border Rail = null!;
        public Border Fill = null!;
        public Thumb FromKnob = null!;
        public Thumb ToKnob = null!;
        public TextBlock RangeText = null!;
        public Slider PhaseSlider = null!;
        public Slider EccSlider = null!;
        public Slider NoiseFromSlider = null!;
        public Slider NoiseToSlider = null!;
        public TextBlock PhaseText = null!;
        public TextBlock EccText = null!;
        public TextBlock NoiseFromText = null!;
        public TextBlock NoiseToText = null!;
    }

    // ══════════════════════════════════════════════════════════════
    //  初始化
    // ══════════════════════════════════════════════════════════════

    private void BuildAxisChecks()
    {
        for (int i = 0; i < AxisNames.Length; i++)
        {
            int index = i;
            var check = new CheckBox
            {
                Content = AxisNames[i],
                Foreground = new SolidColorBrush(AxisColors[i]),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = true,
                ToolTip = i < 3
                    ? $"{AxisNames[i]}：左侧第 {i + 1} 条轴。打勾后这条轴会跟着动作运动。"
                    : $"{AxisNames[i]}：右侧第 {i - 2} 条轴。打勾后这条轴会跟着动作运动。",
            };
            check.Checked += (_, _) => Axis_Toggled(index, true);
            check.Unchecked += (_, _) => Axis_Toggled(index, false);
            _axisChecks[i] = check;
            AxisChecks.Children.Add(check);
        }
    }

    private void BuildCatCombo()
    {
        foreach (CategoryItem cat in CatNames) CatCombo.Items.Add(cat);
    }

    /// <summary>
    /// 编辑已有动作时只勾选「非中性」的轴：ayva 也是过滤掉 from==to==0.5 的轴。
    /// 注意 from==to 但不在 0.5 的轴（例如碾压类预设 L0=[0,0]）是有意义的「停在某位置」，必须保留。
    /// </summary>
    private void InitSelection()
    {
        _suspendAxisEvents = true;
        _selectedAxes.Clear();

        if (_editPreset == null)
        {
            for (int i = 0; i < 6; i++) _selectedAxes.Add(i);
        }
        else
        {
            var axes = _editPreset.AllAxes;
            for (int i = 0; i < 6; i++)
            {
                bool neutral = Math.Abs(axes[i][0] - 0.5) < 1e-9 && Math.Abs(axes[i][1] - 0.5) < 1e-9;
                if (!neutral) _selectedAxes.Add(i);
            }
            if (_selectedAxes.Count == 0)
                for (int i = 0; i < 6; i++) _selectedAxes.Add(i);
        }

        for (int i = 0; i < 6; i++) _axisChecks[i].IsChecked = _selectedAxes.Contains(i);
        _suspendAxisEvents = false;
    }

    private void Axis_Toggled(int index, bool isChecked)
    {
        if (_suspendAxisEvents) return;

        if (isChecked) _selectedAxes.Add(index);
        else _selectedAxes.Remove(index);

        if (_selectedAxes.Count == 0)
        {
            // 至少保留一条轴：回滚这次取消
            _suspendAxisEvents = true;
            _selectedAxes.Add(index);
            _axisChecks[index].IsChecked = true;
            _suspendAxisEvents = false;
            AxisHint.Text = "至少要留一条轴，最后这条不能取消";
            return;
        }

        AxisHint.Text = "";
        MarkDirty();
        RebuildRows();
        UpdateSaveState();
    }

    // ══════════════════════════════════════════════════════════════
    //  每轴一行
    // ══════════════════════════════════════════════════════════════

    private void RebuildRows()
    {
        // 未显示的轴从 _rowCache 取上一次的编辑状态：取消勾选再勾回来不会丢参数；
        // 当前显示的行以 _rows 里的实例为准（它们被滑块原地修改，值最新）。
        var carry = new AxisRow?[6];
        for (int i = 0; i < 6; i++) carry[i] = _rowCache[i];
        foreach (var row in _rows) carry[row.Index] = row;

        _rows.Clear();
        AxisRowsPanel.Children.Clear();

        for (int i = 0; i < 6; i++)
        {
            if (!_selectedAxes.Contains(i)) continue;
            var row = MakeRow(i, carry[i]);
            _rowCache[i] = row;
            _rows.Add(row);
            AxisRowsPanel.Children.Add(BuildRowUi(row));
        }

        _rowsBuilt = true;
        ApplyTrackWidths();
        RedrawWave();
    }

    /// <summary>
    /// 行程条宽度跟着可用宽度走（窄窗口也不会把右边的「运动方式」挤出画面），
    /// 行数少的时候也不会被拉成横跨整屏的长条。
    /// </summary>
    private void ApplyTrackWidths()
    {
        if (_rows.Count == 0) return;

        double available = AxisRowsPanel.ActualWidth;
        // 面板还没量出宽度（窗口刚建、正在布局）时保持上一次的值，别把宽度算成 0
        if (available > 60)
        {
            const double fixedWidth = 46 + 84 + 88 + 16;   // 轴名 + 数值 + 运动方式 + 间距
            double track = Math.Clamp(available - fixedWidth, 110, 240);
            foreach (var row in _rows) row.TrackWidth = track;
        }

        foreach (var row in _rows)
        {
            LayoutTrack(row);
            RefreshRow(row);
        }
    }

    /// <summary>把行程条的轨道/数值宽度按当前轨道宽度同步一遍。</summary>
    private static void LayoutTrack(AxisRow row)
    {
        row.Panel.Width = row.TrackWidth;
        row.Rail.Width = row.TrackWidth;
        row.RangeText.Width = 84;
    }

    private AxisRow MakeRow(int index, AxisRow? carry)
    {
        var row = new AxisRow { Index = index, Name = AxisNames[index], Color = AxisColors[index] };

        if (carry != null)
        {
            row.From = carry.From;
            row.To = carry.To;
            row.Phase = carry.Phase;
            row.Ecc = carry.Ecc;
            row.NoiseFrom = carry.NoiseFrom;
            row.NoiseTo = carry.NoiseTo;
            row.Motion = carry.Motion;
        }
        else if (_editPreset != null)
        {
            double[] axis = _editPreset.AllAxes[index];
            row.From = axis[0];
            row.To = axis[1];
            row.Phase = axis[2];
            row.Ecc = axis[3];
            row.NoiseFrom = axis.Length > 4 ? axis[4] : 0;
            row.NoiseTo = axis.Length > 5 ? axis[5] : 0;
            row.Motion = _editPreset.MotionOf(index);
        }
        else
        {
            // 新建动作的默认值：小幅行程（0.4↔0.6），避免 0.5↔0.5 画出一条死平线
            row.From = 0.4;
            row.To = 0.6;
        }

        return row;
    }

    private UIElement BuildRowUi(AxisRow row)
    {
        var block = new Border
        {
            Background = (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── 第一行：轴名 + 行程双旋钮 + 数值 + 运动方式 ──
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock
        {
            Text = row.Name,
            Width = 32,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(row.Color),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 行程条：Canvas + 两个 Thumb，两个圆点可以互相穿过（0 = 最近、1 = 最远）
        var track = new Canvas
        {
            Width = row.TrackWidth,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "拖动左右两个圆点改变行程：左边=起点、右边=终点，拖过去交换就是反向。0 = 最近，1 = 最远。",
        };
        var rail = new Border
        {
            Width = row.TrackWidth,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("SurfaceRaised"),
            IsHitTestVisible = false,
        };
        Canvas.SetTop(rail, 8);
        track.Children.Add(rail);

        var fill = new Border
        {
            Width = 0,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(row.Color),
            IsHitTestVisible = false,
        };
        Canvas.SetTop(fill, 8);
        track.Children.Add(fill);

        var fromKnob = MakeKnob(row, true);
        var toKnob = MakeKnob(row, false);
        track.Children.Add(fromKnob);
        track.Children.Add(toKnob);

        row.Panel = track;
        row.Rail = rail;
        row.Fill = fill;
        row.FromKnob = fromKnob;
        row.ToKnob = toKnob;
        top.Children.Add(track);

        row.RangeText = new TextBlock
        {
            Width = 84,
            FontSize = 11,
            Foreground = (Brush)FindResource("Faint"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            ToolTip = "这条轴的行程（起点 → 终点），0 = 最近，1 = 最远。",
        };
        top.Children.Add(row.RangeText);

        var motionCombo = new ComboBox
        {
            Width = 88,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "速度变化方式：正弦 = 两头慢中间快（最常用）；抛物 = 一头更急；线性 = 全程匀速。",
        };
        motionCombo.Items.Add(StrokePreset.MotionLabel(StrokeMotion.Sinusoidal));
        motionCombo.Items.Add(StrokePreset.MotionLabel(StrokeMotion.Parabolic));
        motionCombo.Items.Add(StrokePreset.MotionLabel(StrokeMotion.Linear));
        motionCombo.SelectedIndex = (int)row.Motion;
        motionCombo.SelectionChanged += (_, _) =>
        {
            row.Motion = (StrokeMotion)Math.Clamp(motionCombo.SelectedIndex, 0, 2);
            MarkDirty();
            RedrawWave();
        };
        top.Children.Add(motionCombo);

        Grid.SetRow(top, 0);
        grid.Children.Add(top);

        // ── 第二行：相位 / 偏摆 / 起点收缩 / 终点收缩（放不下会自动折成两行） ──
        var bottom = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };

        row.PhaseSlider = MakeSlider(-4, 4, row.Phase, 90);
        row.PhaseText = MakeValueText();
        row.PhaseSlider.ValueChanged += (_, e) =>
        {
            row.Phase = e.NewValue;
            row.PhaseText.Text = FormatSigned(e.NewValue);
            MarkDirty();
            RedrawWave();
        };
        bottom.Children.Add(MakeParamGroup("错开", row.PhaseSlider, row.PhaseText,
            $"错开 {row.Name} 的往返时机，用来让几条轴不同步。现在 {FormatSigned(row.Phase)}，0 = 不偏移，±2 = 错开半圈。"));

        row.EccSlider = MakeSlider(-1, 1, row.Ecc, 90);
        row.EccText = MakeValueText();
        row.EccSlider.ValueChanged += (_, e) =>
        {
            row.Ecc = e.NewValue;
            row.EccText.Text = FormatSigned(e.NewValue);
            MarkDirty();
            RedrawWave();
        };
        bottom.Children.Add(MakeParamGroup("偏摆", row.EccSlider, row.EccText,
            $"偏摆 {row.Name} 的力度分布，让行程两头停得不对称。现在 {FormatSigned(row.Ecc)}，0 = 两头一样。"));

        row.NoiseFromSlider = MakeSlider(0, 1, row.NoiseFrom, 90);
        row.NoiseFromText = MakeValueText();
        row.NoiseFromSlider.ValueChanged += (_, e) =>
        {
            row.NoiseFrom = e.NewValue;
            row.NoiseFromText.Text = FormatPercent(e.NewValue);
            MarkDirty();
            RedrawWave();
        };
        bottom.Children.Add(MakeParamGroup("起点收缩", row.NoiseFromSlider, row.NoiseFromText,
            $"每圈在起点随机收一点，让动作不那么规律。现在 {FormatPercent(row.NoiseFrom)}，0 = 每次都顶到起点。"));

        row.NoiseToSlider = MakeSlider(0, 1, row.NoiseTo, 90);
        row.NoiseToText = MakeValueText();
        row.NoiseToSlider.ValueChanged += (_, e) =>
        {
            row.NoiseTo = e.NewValue;
            row.NoiseToText.Text = FormatPercent(e.NewValue);
            MarkDirty();
            RedrawWave();
        };
        bottom.Children.Add(MakeParamGroup("终点收缩", row.NoiseToSlider, row.NoiseToText,
            $"每圈在终点随机收一点。现在 {FormatPercent(row.NoiseTo)}，0 = 每次都顶到终点。"));

        Grid.SetRow(bottom, 1);
        grid.Children.Add(bottom);

        block.Child = grid;
        RefreshRow(row);
        return block;
    }

    /// <summary>一个参数块：标题 + 滑杆 + 右侧读数，窄窗口下整块换行，不会把读数挤掉。</summary>
    private StackPanel MakeParamGroup(string caption, Slider slider, TextBlock value, string tip)
    {
        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 14, 0),
            ToolTip = tip,
        };
        group.Children.Add(new TextBlock
        {
            Text = caption,
            Width = 56,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
        group.Children.Add(slider);
        group.Children.Add(value);
        return group;
    }

    /// <summary>滑杆读数：带正负号，比裸数字更容易看懂往哪边调。</summary>
    private static string FormatSigned(double value) => value.ToString("+0.00;-0.00;0.00");

    /// <summary>噪声读数用百分比，比 0.00–1.00 更直观。</summary>
    private static string FormatPercent(double value) => $"{value * 100:0}%";

    private Thumb MakeKnob(AxisRow row, bool isFrom)
    {
        var knob = new Thumb
        {
            Width = 14,
            Height = 14,
            Style = (Style)FindResource("SliderThumb"),
            Cursor = Cursors.Hand,
            Tag = row,
            ToolTip = isFrom
                ? "起点：往右拖 = 行程起点更远。可以拖到终点右边（反向行程）。"
                : "终点：往右拖 = 行程终点更远。可以拖到起点左边（反向行程）。",
        };
        if (isFrom) knob.DragDelta += FromKnob_DragDelta;
        else knob.DragDelta += ToKnob_DragDelta;
        return knob;
    }

    private static Slider MakeSlider(double min, double max, double value, double width) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        Width = width,
        SmallChange = 0.05,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock MakeValueText() => new()
    {
        Width = 40,
        FontSize = 10,
        TextAlignment = TextAlignment.Right,
        Foreground = (Brush)FindResource("Faint"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void RefreshRow(AxisRow row)
    {
        double trackWidth = row.TrackWidth;
        double fromX = row.From * trackWidth;
        double toX = row.To * trackWidth;

        Canvas.SetLeft(row.FromKnob, fromX - 7);
        Canvas.SetLeft(row.ToKnob, toX - 7);
        Canvas.SetTop(row.FromKnob, 3);
        Canvas.SetTop(row.ToKnob, 3);

        Canvas.SetLeft(row.Fill, Math.Min(fromX, toX));
        row.Fill.Width = Math.Max(Math.Abs(toX - fromX), 1);

        row.RangeText.Text = $"{row.From:F2} → {row.To:F2}";
        row.PhaseText.Text = FormatSigned(row.Phase);
        row.EccText.Text = FormatSigned(row.Ecc);
        row.NoiseFromText.Text = FormatPercent(row.NoiseFrom);
        row.NoiseToText.Text = FormatPercent(row.NoiseTo);
    }

    // ══════════════════════════════════════════════════════════════
    //  Range 双旋钮拖拽（允许互相穿过）
    // ══════════════════════════════════════════════════════════════

    private void FromKnob_DragDelta(object sender, DragDeltaEventArgs e) => DragRangeKnob(sender, e, true);

    private void ToKnob_DragDelta(object sender, DragDeltaEventArgs e) => DragRangeKnob(sender, e, false);

    private void DragRangeKnob(object sender, DragDeltaEventArgs e, bool isFrom)
    {
        if (sender is not Thumb { Tag: AxisRow row }) return;
        double delta = e.HorizontalChange / Math.Max(row.TrackWidth, 1);
        if (isFrom) row.From = Math.Clamp(row.From + delta, 0, 1);
        else row.To = Math.Clamp(row.To + delta, 0, 1);
        MarkDirty();
        RefreshRow(row);
        RedrawWave();
    }

    // ══════════════════════════════════════════════════════════════
    //  波形预览
    // ══════════════════════════════════════════════════════════════

    /// <summary>「暂停」只停这张预览图，参数和设备都不受影响。</summary>
    private void PreviewPause_Click(object sender, RoutedEventArgs e)
    {
        _previewPaused = !_previewPaused;
        PreviewPauseBtn.Content = _previewPaused ? "继续" : "暂停";
        PreviewPauseBtn.ToolTip = _previewPaused
            ? "让预览图继续动起来（只是画图，不碰设备）。"
            : "暂停只是让这张图停下来，不影响动作参数，更不影响设备。";
        RedrawWave();
    }

    private void AdvancePreview()
    {
        if (_previewPaused) return;                 // 暂停时不重画，省 CPU
        if (!IsVisible || WindowState == WindowState.Minimized) { _lastTick = 0; return; }

        long now = Stopwatch.GetTimestamp();
        double deltaSeconds = _lastTick == 0 ? 0.04 : Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        _lastTick = now;
        if (deltaSeconds < 0 || deltaSeconds > 0.5) deltaSeconds = 0.04;   // 卡顿后不要跳一大步
        _previewAngle += deltaSeconds * 2 * Math.PI;                       // 预览 1 圈 / 秒
        RedrawWave();
    }

    private void RedrawWave()
    {
        if (WaveCanvas == null) return;
        double width = WaveCanvas.ActualWidth;
        double height = WaveCanvas.ActualHeight;
        if (width <= 4 || height <= 4 || _rows.Count == 0) return;

        WaveCanvas.Children.Clear();

        const int samples = 200;
        const double margin = 10;
        double plotHeight = Math.Max(height - margin * 2, 1);

        // 参考网格：0% / 50% / 100% 横线 + 1/4、1/2、3/4 竖线
        var gridBrush = (Brush)FindResource("Border");
        for (int g = 0; g <= 2; g++)
        {
            double y = margin + plotHeight * g / 2.0;
            WaveCanvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
        }
        for (int g = 1; g <= 3; g++)
        {
            double x = width * g / 4.0;
            WaveCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = margin, Y2 = height - margin, Stroke = gridBrush, StrokeThickness = 1 });
        }

        long cycleIndex = (long)Math.Floor(_previewAngle / (Math.PI * 2));
        double phaseAngle = PositiveMod(_previewAngle, Math.PI * 2);

        foreach (var row in _rows)
        {
            var points = new PointCollection(samples);
            for (int i = 0; i < samples; i++)
            {
                double ratio = i / (double)(samples - 1);
                double angle = ratio * Math.PI * 2;
                points.Add(new Point(ratio * width, margin + (1 - SampleAxis(row, angle, cycleIndex, null)) * plotHeight));
            }

            WaveCanvas.Children.Add(new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(row.Color),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });

            // 噪声包络：把端点按最大噪声收缩后的淡虚线（对应 ayva 的 noise 预览）
            if (row.NoiseFrom > 0) AddFaintCurve(row, width, margin, plotHeight, samples, cycleIndex, true);
            if (row.NoiseTo > 0) AddFaintCurve(row, width, margin, plotHeight, samples, cycleIndex, false);

            // 相位进度点
            double dotX = phaseAngle / (Math.PI * 2) * width;
            double dotY = margin + (1 - SampleAxis(row, _previewAngle, cycleIndex, null)) * plotHeight;
            var dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(row.Color),
                Stroke = Brushes.White,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(dot, dotX - 4.5);
            Canvas.SetTop(dot, dotY - 4.5);
            WaveCanvas.Children.Add(dot);
        }
    }

    private void AddFaintCurve(AxisRow row, double width, double margin, double plotHeight, int samples, long cycleIndex, bool onlyFromNoise)
    {
        var points = new PointCollection(samples);
        for (int i = 0; i < samples; i++)
        {
            double ratio = i / (double)(samples - 1);
            double angle = ratio * Math.PI * 2;
            points.Add(new Point(ratio * width, margin + (1 - SampleAxis(row, angle, cycleIndex, onlyFromNoise)) * plotHeight));
        }

        WaveCanvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(row.Color) { Opacity = 0.35 },
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
        });
    }

    /// <summary>
    /// 用 MotionEngine 的同一采样公式算该轴 0..1 输出。
    /// onlyNoiseEnd: null = 两端噪声都算；true = 只保留 from 端噪声；false = 只保留 to 端噪声。
    /// </summary>
    private static double SampleAxis(AxisRow row, double angle, long cycleIndex, bool? onlyNoiseEnd)
    {
        double noiseFrom = row.NoiseFrom;
        double noiseTo = row.NoiseTo;
        if (onlyNoiseEnd == true) noiseTo = 0;
        else if (onlyNoiseEnd == false) noiseFrom = 0;

        return MotionEngine.SampleTempestAxis(
            row.From,
            row.To,
            row.Phase,
            row.Ecc,
            row.Motion,
            angle,
            1.0,
            noiseFrom,
            noiseTo,
            row.Index,
            cycleIndex);
    }

    private static double PositiveMod(double value, double modulus)
    {
        double remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    // ══════════════════════════════════════════════════════════════
    //  名称校验 + 保存
    // ══════════════════════════════════════════════════════════════

    private void DisplayName_Changed(object sender, TextChangedEventArgs e)
    {
        MarkDirty();
        UpdateSaveState();
    }

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        // 实时净化：只保留小写字母 / 数字 / 连字符（与 ayva 的 strokeName 一致）
        string cleaned = SanitizeName(NameBox.Text);
        if (cleaned != NameBox.Text)
        {
            int caret = NameBox.CaretIndex;
            NameBox.Text = cleaned;
            NameBox.CaretIndex = Math.Clamp(caret, 0, cleaned.Length);
            return;   // 赋值会再次触发 TextChanged，交给那一轮做校验
        }

        MarkDirty();
        ValidateName();
    }

    private static string SanitizeName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var builder = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            char lower = char.ToLowerInvariant(c);
            if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') || lower == '-')
                builder.Append(lower);
        }
        return builder.ToString();
    }

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 48 &&
        name.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');

    private bool IsDuplicateName(string name)
    {
        if (_editPreset != null && string.Equals(_editPreset.Id, name, StringComparison.OrdinalIgnoreCase))
            return false;
        return StrokesViewModel.AllPresets.Any(p => string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase));
    }

    private void ValidateName() => UpdateSaveState();

    /// <summary>
    /// 校验结果写进底部那一条状态栏（不是只在输入框旁边闪一行小字），
    /// 同时决定保存按钮能不能按。保存按钮保留可用状态，按下去时会说明哪里不对。
    /// </summary>
    private void UpdateSaveState()
    {
        // 先清掉输入框旁边那行即时提示，下面按情况重新填
        NameHint.Text = "";

        if (NameBox.Text.Length == 0)
        {
            SetStatus("还差一个英文名：这个动作保存进动作库时用哪个名字（只能用 a-z、0-9、-）。", true);
            SaveBtn.IsEnabled = false;
            return;
        }

        if (!IsValidName(NameBox.Text))
        {
            NameHint.Text = "只能用 a-z、0-9 和连字符 -，最长 48 位";
            SetStatus("英文名里有不能用的字符。名字只能用 a-z、0-9 和连字符 -；想写中文请填「显示名」。", true);
            SaveBtn.IsEnabled = false;
            return;
        }

        if (IsDuplicateName(NameBox.Text))
        {
            NameHint.Text = "这个英文名已经有人用了";
            SetStatus($"已经有一个动作叫「{NameBox.Text}」了，换一个英文名再保存。", true);
            SaveBtn.IsEnabled = false;
            return;
        }

        if (!_rowsBuilt || _rows.Count == 0)
        {
            SetStatus("至少要留一条轴跟着动。", true);
            SaveBtn.IsEnabled = false;
            return;
        }

        SaveBtn.IsEnabled = true;

        if (_editPreset != null && string.Equals(_editPreset.Id, NameBox.Text, StringComparison.OrdinalIgnoreCase))
        {
            // 改的是原动作本身
            if (!_editPreset.IsCustom)
                SetStatus($"这是内置动作「{_editPreset.Label}」。保存会覆盖它的参数（会先留一份 presets.json.bak 备份），继续前会再问一次。", false);
            else
                SetStatus("改好就按「保存到动作库」，会直接覆盖这个动作现在的参数。", false);
            return;
        }

        if (_editPreset != null)
            SetStatus($"英文名和原来的不一样，保存会另存成一个新动作；原来的「{_editPreset.Label ?? _editPreset.Id}」保持不动。", false);
        else
            SetStatus("可以保存。保存后这个动作会立刻出现在动作库里，重启程序也还在。", false);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = (isError ? "⚠ " : "✓ ") + text;
        StatusText.Foreground = (Brush)FindResource(isError ? "Warning" : "Success");
    }

    /// <summary>出问题时把光标送进英文名输入框并全选，用户直接改就行。</summary>
    private void FocusNameBox()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text;
        if (!IsValidName(name))
        {
            SetStatus("英文名还没填或者有不能用的字符。只能用 a-z、0-9 和连字符 -；想写中文请填「显示名」。", true);
            FocusNameBox();
            return;
        }
        if (IsDuplicateName(name))
        {
            SetStatus($"已经有一个动作叫「{name}」了，换一个英文名再保存。", true);
            FocusNameBox();
            return;
        }
        if (_rows.Count == 0)
        {
            AxisHint.Text = "至少要留一条轴，最后这条不能取消";
            SetStatus("至少要留一条轴跟着动。", true);
            return;
        }

        // 名称没变且正在编辑 → 原地改（AllPresets 里是同一个实例，动作页/引擎立刻生效）；
        // 改名保存 → 视为「另存为新动作」，原动作保持不变。
        StrokePreset target = _editPreset != null && string.Equals(_editPreset.Id, name, StringComparison.OrdinalIgnoreCase)
            ? _editPreset
            : new StrokePreset();
        CreatedNew = !ReferenceEquals(target, _editPreset);

        // 覆盖内置动作前二次确认：内置参数一旦写盘就没有「恢复默认」的入口
        if (_editPreset is { IsCustom: false } &&
            string.Equals(_editPreset.Id, name, StringComparison.OrdinalIgnoreCase))
        {
            if (System.Windows.MessageBox.Show(
                    $"会改掉内置动作「{_editPreset.Label}」原来的参数，改动存进 presets.json 后没有一键还原。继续？\n（保存前会自动复制一份 presets.json.bak）",
                    "覆盖内置动作", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            // 写盘前留一份 presets.json.bak，万一覆盖错了还能手工还原
            if (!BackupPresetsFile())
            {
                SetStatus("备份 presets.json 失败，为了不丢原参数，这次没有保存。", true);
                return;
            }
        }

        // 先写盘、再改活对象：写盘失败时卡片/引擎正在用的那个实例保持原样，
        // 不会出现「界面已经按新参数跑、文件却没改」的不一致。
        string displayName = DisplayNameBox.Text?.Trim() ?? "";

        var copy = new StrokePreset();
        ApplyRowValues(copy, name);
        if (ReferenceEquals(target, _editPreset))
        {
            // 原地编辑：写盘副本沿用目标的显示名与「自建」标记，
            // 否则内置动作会被写成英文名 + IsCustom=true（下次启动就变成可删除的自建动作）
            copy.Label    = string.IsNullOrWhiteSpace(target.Label) ? name : target.Label;
            copy.IsCustom = target.IsCustom;
        }
        // 「显示名称」优先：用户填了中文就用它当列表里显示的名字（留空则退回英文标识）
        if (!string.IsNullOrWhiteSpace(displayName)) copy.Label = displayName;

        if (!StrokePresetStore.SaveOrAppend(copy, StrokesViewModel.AllPresets))
        {
            SetStatus("写 presets.json 失败（程序装的目录只读？），这个动作没有保存。", true);
            return;
        }

        // 落盘成功后才把新参数写回活对象
        ApplyRowValues(target, name);
        SavedPreset = target;
        _isDirty = false;
        _saved   = true;
        DialogResult = true;
    }

    /// <summary>
    /// 覆盖内置动作前把 presets.json 复制为 presets.json.bak（覆盖同名旧备份）。
    /// 文件还不存在时视为成功（没有可丢的内容）；复制失败返回 false，调用方据此放弃保存。
    /// </summary>
    private static bool BackupPresetsFile()
    {
        try
        {
            string path = StrokePresetStore.FilePath;
            if (!File.Exists(path)) return true;
            File.Copy(path, path + ".bak", overwrite: true);
            return true;
        }
        catch { return false; }
    }

    private void ApplyRowValues(StrokePreset preset, string name)
    {
        var axes = new double[6][];
        var motions = new string[6];

        for (int i = 0; i < 6; i++)
        {
            AxisRow? row = _rows.FirstOrDefault(r => r.Index == i);
            if (row == null)
            {
                // 未勾选的轴沿用「该轴原有值」（原地编辑/另存时取被编辑动作的原参数），
                // 不再写死中位 —— 否则取消勾选一条轴再保存会把这条轴的运动悄悄改掉。
                if (_editPreset != null)
                {
                    double[] origin = _editPreset.AllAxes[i];
                    axes[i] =
                    [
                        origin[0],
                        origin[1],
                        origin[2],
                        origin[3],
                        origin.Length > 4 ? origin[4] : 0,
                        origin.Length > 5 ? origin[5] : 0,
                    ];
                    motions[i] = StrokePreset.MotionName(_editPreset.MotionOf(i));
                }
                else
                {
                    // 新建动作：没参与编辑的轴保持中位静止（与旧行为一致）
                    axes[i] = [0.5, 0.5, 0, 0, 0, 0];
                    motions[i] = StrokePreset.MotionName(StrokeMotion.Sinusoidal);
                }
            }
            else
            {
                axes[i] = [row.From, row.To, row.Phase, row.Ecc, row.NoiseFrom, row.NoiseTo];
                motions[i] = StrokePreset.MotionName(row.Motion);
            }
        }

        preset.Id = name;
        // 原地编辑时保留原有 Label（内置预设的中文名不被 Id 覆盖）；新建/另存才用名称当 Label
        if (!ReferenceEquals(preset, _editPreset) || string.IsNullOrWhiteSpace(preset.Label))
            preset.Label = name;
        preset.Cat = (CatCombo.SelectedItem as CategoryItem)?.Value ?? CatNames[0].Value;
        if (!ReferenceEquals(preset, _editPreset)) preset.IsCustom = true;
        preset.L0 = axes[0];
        preset.L1 = axes[1];
        preset.L2 = axes[2];
        preset.R0 = axes[3];
        preset.R1 = axes[4];
        preset.R2 = axes[5];
        preset.Motion = motions;
        preset.Normalize();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 键盘：Esc = 取消，Ctrl+P = 暂停/继续预览。
    /// 保存走窗口上那个 IsDefault 的按钮（按钮不可用时不会触发），这里不重复处理回车，
    /// 免得「按钮灰着但回车仍然尝试保存」。
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            PreviewPause_Click(PreviewPauseBtn, new RoutedEventArgs());
        }
    }
}
