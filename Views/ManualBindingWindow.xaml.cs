using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using Hexa.Services;

namespace Hexa.Views;

public partial class ManualBindingWindow : Window
{
    private readonly ScreenWatchService _service;
    private ScreenWatchService.ManualBindingFrame? _frame;
    private readonly ManualTrackPoint?[] _points = new ManualTrackPoint?[3];
    private int _step;
    private readonly DispatcherTimer _previewTimer;

    public ManualBindingWindow(ScreenWatchService service)
    {
        InitializeComponent();
        _service = service;
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _previewTimer.Tick += PreviewTimer_Tick;
        Loaded += ManualBindingWindow_Loaded;
        Closed += ManualBindingWindow_Closed;
        LoadFrame();
    }

    private void ManualBindingWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_frame is null) _previewTimer.Start();
    }

    private void ManualBindingWindow_Closed(object? sender, EventArgs e) => _previewTimer.Stop();

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_frame is not null) { _previewTimer.Stop(); return; }
        LoadFrame(resetPoints: false);
    }

    private void LoadFrame(bool resetPoints = true)
    {
        var frame = _service.ManualPreview;
        if (frame is null || Environment.TickCount64 - frame.AtMs > 1500)
        {
            StepHint.Text = "正在等实时画面…首帧到来后会自动定格；也可点「换一帧 / 重选」。";
            _frame = null;
            FrozenImage.Source = null;
        }
        else
        {
            _previewTimer.Stop();
            _frame = frame;
            FrozenImage.Source = frame.Image;
        }
        if (resetPoints) { Array.Clear(_points); _step = 0; }
        DrawSelection();
    }

    private void RefreshFrame_Click(object sender, RoutedEventArgs e)
    {
        _service.RefreshManualPreview();
        LoadFrame();
        if (_frame is null && IsLoaded) _previewTimer.Start();
    }
    private void SelectStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int step))
        { _step = Math.Clamp(step, 0, 2); DrawSelection(); }
    }
    private void SwapEnds_Click(object sender, RoutedEventArgs e)
    {
        (_points[1], _points[2]) = (_points[2], _points[1]);
        DrawSelection();
    }
    private void PickCanvas_Click(object sender, MouseButtonEventArgs e)
    {
        if (_frame is null) return;
        SelectPoint(e.GetPosition(PickCanvas));
        e.Handled = true;
    }
    private void SelectPoint(Point point)
    {
        if (_frame is null) return;
        if (point.X < 0 || point.Y < 0 || point.X >= 320 || point.Y >= 180) return;
        _points[_step] = new(Math.Clamp(point.X / 319.0, 0, 1), Math.Clamp(point.Y / 179.0, 0, 1));
        if (_step < 2) _step++;
        DrawSelection();
    }
    private void DrawSelection()
    {
        PickCanvas.Children.Clear();
        Brush[] colors = [Brushes.Cyan, Brushes.Gold, Brushes.Violet];
        string[] labels = ["目标", "浅", "深"];
        if (_points[1] is { } a && _points[2] is { } b)
            PickCanvas.Children.Add(new Line { X1 = a.X * 319, Y1 = a.Y * 179,
                X2 = b.X * 319, Y2 = b.Y * 179, Stroke = Brushes.White, StrokeThickness = 0.8 });
        for (int i = 0; i < _points.Length; i++)
        {
            if (_points[i] is not { } p) continue;
            double x = p.X * 319, y = p.Y * 179;
            var circle = new Ellipse { Width = 6, Height = 6, Stroke = colors[i], StrokeThickness = 1.1 };
            Canvas.SetLeft(circle, x - 3); Canvas.SetTop(circle, y - 3); PickCanvas.Children.Add(circle);
            var label = new TextBlock { Text = labels[i], FontSize = 7, Foreground = colors[i], Background = Brushes.Black };
            Canvas.SetLeft(label, Math.Clamp(x + 4, 0, 303)); Canvas.SetTop(label, Math.Clamp(y - 9, 0, 168));
            PickCanvas.Children.Add(label);
        }
        bool complete = _points.All(p => p.HasValue);
        ApplyBindingButton.IsEnabled = complete && _frame is not null;
        SwapEndsButton.IsEnabled = _points[1].HasValue && _points[2].HasValue;
        Button[] steps = [TargetStep, ShallowStep, DeepStep];
        for (int i = 0; i < steps.Length; i++) steps[i].FontWeight = i == _step ? FontWeights.Bold : FontWeights.Normal;
        if (_frame is not null)
            StepHint.Text = complete ? "三个点已选好。确认方向后点「绑定这个目标」；也可点上面的步骤重选。"
                : _step switch { 0 => "① 点一下要跟的目标，尽量选有细节的位置。",
                    1 => "② 点这次运动最浅时，目标所在的位置。", _ => "③ 点这次运动最深时，目标所在的位置。" };
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_frame is null || _points.Any(p => !p.HasValue)) return;
        if (!_service.TryBindManual(_frame, _points[0]!.Value, _points[1]!.Value, _points[2]!.Value, out string message))
        {
            StepHint.Text = message;
            _service.RefreshManualPreview();
            LoadFrame();
            if (_frame is null && IsLoaded) _previewTimer.Start();
            return;
        }
        DialogResult = true;
    }
}
