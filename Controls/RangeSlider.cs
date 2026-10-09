using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Hexa.Controls;

/// <summary>
/// 双滑块范围选择器：一条轨道 + 两个可拖动旋钮（左 = <see cref="LowerValue"/> 最小值，
/// 右 = <see cref="UpperValue"/> 最大值），中间用高亮色填充表示当前区间。
///
/// 为什么不用两个 <see cref="Slider"/> 拼：两个独立滑块各自有轨道、各自有端点内缩，
/// 视觉上两条轨道接不上、两个旋钮也对不齐。这里两个旋钮共用一条轨道，
/// 圆心按「(值 − 最小) / (最大 − 最小) × 宽度」精确落位，能真正走到轨道两端。
///
/// 交互：
///   拖动旋钮     调整该端（左旋钮不会超过右旋钮，反之亦然）
///   点击轨道     最近的那个旋钮跳过去
///   滚轮         在旋钮上滚动按 <see cref="SmallChange"/> 微调（默认关闭：只有把
///                <see cref="IsWheelAdjustEnabled"/> 设为 true 才生效；关闭时事件不标记 Handled，
///                会冒泡给外层 ScrollViewer 正常滚动页面）
///
/// 样式在 App.xaml（TargetType = RangeSlider 的隐式样式），模板里必须有
/// PART_Canvas / PART_Track / PART_Fill / PART_LowerThumb / PART_UpperThumb 五个部件。
/// </summary>
public class RangeSlider : System.Windows.Controls.Control
{
    /// <summary>旋钮直径（模板里的 Thumb 尺寸必须一致）。</summary>
    public const double KnobSize = 14;

    /// <summary>轨道高度。</summary>
    public const double TrackHeight = 6;

    private Canvas? _canvas;
    private Border? _track;
    private Border? _fill;
    private Thumb? _lowerThumb;
    private Thumb? _upperThumb;

    // ══════════════════════════════════════════════════════════════
    //  依赖属性
    // ══════════════════════════════════════════════════════════════

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(0.0, OnRangePropertyChanged, CoerceMinimum));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(100.0, OnRangePropertyChanged, CoerceMaximum));

    public static readonly DependencyProperty LowerValueProperty = DependencyProperty.Register(
        nameof(LowerValue), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnLowerValueChanged, CoerceLowerValue));

    public static readonly DependencyProperty UpperValueProperty = DependencyProperty.Register(
        nameof(UpperValue), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnUpperValueChanged, CoerceUpperValue));

    public static readonly DependencyProperty SmallChangeProperty = DependencyProperty.Register(
        nameof(SmallChange), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(1.0));

    /// <summary>
    /// 是否允许鼠标滚轮微调数值。默认 <c>false</c>：滚轮扫过滑块时既不改值、也不吞掉滚动事件
    /// （未标记 Handled，事件会冒泡给外层 ScrollViewer 正常滚动页面）。
    /// 只有确实需要滚轮微调的滑块才应把它设为 true。
    /// </summary>
    public static readonly DependencyProperty IsWheelAdjustEnabledProperty = DependencyProperty.Register(
        nameof(IsWheelAdjustEnabled), typeof(bool), typeof(RangeSlider),
        new FrameworkPropertyMetadata(false));

    /// <summary>最小值（区间左端）。</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>最大值（区间右端）。</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>当前区间左端（左旋钮）。</summary>
    public double LowerValue
    {
        get => (double)GetValue(LowerValueProperty);
        set => SetValue(LowerValueProperty, value);
    }

    /// <summary>当前区间右端（右旋钮）。</summary>
    public double UpperValue
    {
        get => (double)GetValue(UpperValueProperty);
        set => SetValue(UpperValueProperty, value);
    }

    /// <summary>滚轮 / 微调步长。</summary>
    public double SmallChange
    {
        get => (double)GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    /// <summary>是否允许滚轮微调（默认 false；false 时滚轮事件直接放行给外层滚动容器）。</summary>
    public bool IsWheelAdjustEnabled
    {
        get => (bool)GetValue(IsWheelAdjustEnabledProperty);
        set => SetValue(IsWheelAdjustEnabledProperty, value);
    }

    /// <summary>左端值变化（拖动 / 赋值 / 代码设置都会触发）。</summary>
    public event EventHandler? LowerValueChanged;

    /// <summary>右端值变化。</summary>
    public event EventHandler? UpperValueChanged;

    /// <summary>
    /// 抓住某个旋钮（或点击轨道）时触发，参数是那一下对应的轴值。
    /// 限位滑块的旋钮默认就停在 0/9999 两端，拖动前值没变，
    /// 只靠 ValueChanged 收不到事件、设备也就不会被驱动到该位置。
    /// </summary>
    public event EventHandler<double>? ValueGrabbed;

    // ══════════════════════════════════════════════════════════════
    //  属性回调
    // ══════════════════════════════════════════════════════════════

    private static object CoerceMinimum(DependencyObject d, object value)
    {
        var slider = (RangeSlider)d;
        double min = (double)value;
        return double.IsFinite(min) ? Math.Min(min, slider.Maximum) : 0.0;
    }

    private static object CoerceMaximum(DependencyObject d, object value)
    {
        var slider = (RangeSlider)d;
        double max = (double)value;
        return double.IsFinite(max) ? Math.Max(max, slider.Minimum) : 100.0;
    }

    private static object CoerceLowerValue(DependencyObject d, object value)
    {
        var slider = (RangeSlider)d;
        double lower = (double)value;
        if (!double.IsFinite(lower)) lower = slider.Minimum;
        return Math.Clamp(lower, slider.Minimum, slider.UpperValue);
    }

    private static object CoerceUpperValue(DependencyObject d, object value)
    {
        var slider = (RangeSlider)d;
        double upper = (double)value;
        if (!double.IsFinite(upper)) upper = slider.Maximum;
        return Math.Clamp(upper, slider.LowerValue, slider.Maximum);
    }

    private static void OnRangePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        // 范围变了，两端值要重新夹紧
        slider.CoerceValue(LowerValueProperty);
        slider.CoerceValue(UpperValueProperty);
        slider.UpdateParts();
    }

    private static void OnLowerValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        slider.CoerceValue(UpperValueProperty);
        slider.UpdateParts();
        slider.LowerValueChanged?.Invoke(slider, EventArgs.Empty);
    }

    private static void OnUpperValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        slider.CoerceValue(LowerValueProperty);
        slider.UpdateParts();
        slider.UpperValueChanged?.Invoke(slider, EventArgs.Empty);
    }

    // ══════════════════════════════════════════════════════════════
    //  模板
    // ══════════════════════════════════════════════════════════════

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_lowerThumb != null)
        {
            _lowerThumb.DragDelta -= OnLowerDragDelta;
            _lowerThumb.DragStarted -= OnLowerDragStarted;
        }
        if (_upperThumb != null)
        {
            _upperThumb.DragDelta -= OnUpperDragDelta;
            _upperThumb.DragStarted -= OnUpperDragStarted;
        }
        if (_canvas != null)
        {
            _canvas.SizeChanged -= OnCanvasSizeChanged;
            _canvas.MouseLeftButtonDown -= OnCanvasMouseDown;
            _canvas.MouseWheel -= OnCanvasMouseWheel;
        }

        _canvas = GetTemplateChild("PART_Canvas") as Canvas;
        _track = GetTemplateChild("PART_Track") as Border;
        _fill = GetTemplateChild("PART_Fill") as Border;
        _lowerThumb = GetTemplateChild("PART_LowerThumb") as Thumb;
        _upperThumb = GetTemplateChild("PART_UpperThumb") as Thumb;

        if (_lowerThumb != null)
        {
            _lowerThumb.DragDelta += OnLowerDragDelta;
            _lowerThumb.DragStarted += OnLowerDragStarted;
        }
        if (_upperThumb != null)
        {
            _upperThumb.DragDelta += OnUpperDragDelta;
            _upperThumb.DragStarted += OnUpperDragStarted;
        }
        if (_canvas != null)
        {
            _canvas.SizeChanged += OnCanvasSizeChanged;
            _canvas.MouseLeftButtonDown += OnCanvasMouseDown;
            _canvas.MouseWheel += OnCanvasMouseWheel;
        }

        UpdateParts();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => UpdateParts();

    /// <summary>按当前值把轨道 / 填充 / 两个旋钮摆到位（只改 Canvas 位置与宽度，不重建元素）。</summary>
    private void UpdateParts()
    {
        if (_canvas is null || _track is null || _fill is null || _lowerThumb is null || _upperThumb is null) return;

        double width = _canvas.ActualWidth;
        double height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        double span = Maximum - Minimum;
        double lowerRatio = span <= 0 ? 0 : Math.Clamp((LowerValue - Minimum) / span, 0, 1);
        double upperRatio = span <= 0 ? 0 : Math.Clamp((UpperValue - Minimum) / span, 0, 1);
        double lowerX = lowerRatio * width;
        double upperX = upperRatio * width;

        double trackTop = (height - TrackHeight) / 2;
        Canvas.SetLeft(_track, 0);
        Canvas.SetTop(_track, trackTop);
        _track.Width = width;

        Canvas.SetLeft(_fill, lowerX);
        Canvas.SetTop(_fill, trackTop);
        _fill.Width = Math.Max(0, upperX - lowerX);

        double knobTop = (height - KnobSize) / 2;
        Canvas.SetLeft(_lowerThumb, lowerX - KnobSize / 2);
        Canvas.SetTop(_lowerThumb, knobTop);
        Canvas.SetLeft(_upperThumb, upperX - KnobSize / 2);
        Canvas.SetTop(_upperThumb, knobTop);
    }

    // ══════════════════════════════════════════════════════════════
    //  交互
    // ══════════════════════════════════════════════════════════════

    private double ValuePerPixel()
    {
        double width = _canvas?.ActualWidth ?? 0;
        double span = Maximum - Minimum;
        return width <= 0 ? 0 : span / width;
    }

    private void OnLowerDragStarted(object sender, DragStartedEventArgs e) =>
        ValueGrabbed?.Invoke(this, LowerValue);

    private void OnUpperDragStarted(object sender, DragStartedEventArgs e) =>
        ValueGrabbed?.Invoke(this, UpperValue);

    private void OnLowerDragDelta(object sender, DragDeltaEventArgs e)
    {
        double perPixel = ValuePerPixel();
        if (perPixel <= 0) return;
        LowerValue = LowerValue + e.HorizontalChange * perPixel;
    }

    private void OnUpperDragDelta(object sender, DragDeltaEventArgs e)
    {
        double perPixel = ValuePerPixel();
        if (perPixel <= 0) return;
        UpperValue = UpperValue + e.HorizontalChange * perPixel;
    }

    /// <summary>点击轨道：离哪个旋钮近就移动哪个。</summary>
    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_canvas is null) return;
        double width = _canvas.ActualWidth;
        if (width <= 0) return;

        double x = Math.Clamp(e.GetPosition(_canvas).X, 0, width);
        double span = Maximum - Minimum;
        double target = Minimum + x / width * span;
        double lowerX = (LowerValue - Minimum) / (span <= 0 ? 1 : span) * width;
        double upperX = (UpperValue - Minimum) / (span <= 0 ? 1 : span) * width;

        if (Math.Abs(x - lowerX) <= Math.Abs(x - upperX)) LowerValue = target;
        else UpperValue = target;
        ValueGrabbed?.Invoke(this, target);
        e.Handled = true;
    }

    /// <summary>
    /// 滚轮微调：指针靠近哪个旋钮就调哪个。默认关闭（<see cref="IsWheelAdjustEnabled"/> = false）时
    /// 直接返回，并且<strong>不</strong>把事件标记 Handled —— 让滚轮冒泡给外层 ScrollViewer 滚动页面，
    /// 避免「鼠标滚轮扫过滑块」就悄悄改值（限位滑块还会因此驱动设备）。
    /// </summary>
    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsWheelAdjustEnabled) return;
        if (_canvas is null) return;
        double step = SmallChange > 0 ? SmallChange : (Maximum - Minimum) / 100.0;
        if (e.Delta > 0)
        {
            if (NearLower(e.GetPosition(_canvas).X)) LowerValue += step;
            else UpperValue += step;
        }
        else
        {
            if (NearLower(e.GetPosition(_canvas).X)) LowerValue -= step;
            else UpperValue -= step;
        }
        e.Handled = true;
    }

    private bool NearLower(double x)
    {
        double width = _canvas?.ActualWidth ?? 0;
        if (width <= 0) return true;
        double span = Maximum - Minimum;
        double lowerX = (LowerValue - Minimum) / (span <= 0 ? 1 : span) * width;
        double upperX = (UpperValue - Minimum) / (span <= 0 ? 1 : span) * width;
        return Math.Abs(x - lowerX) <= Math.Abs(x - upperX);
    }
}
