using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hexa.Models;
using Hexa.Services;
using Hexa.ViewModels;
using Microsoft.Win32;

namespace Hexa.Views;

/// <summary>
/// 「脚本制作」页，两个标签页：
///   1) 编排 —— 从动作库拖动作到时间轴拼装动作序列，实时预览，导出标准多轴 funscript；
///   2) 微调波形 —— 原有的手画波形编辑器（像素画布 + 关键帧 + 正弦/方波/锯齿 + 导入导出），功能原样保留。
/// 编排的采样/导出算法在 <see cref="ScriptComposerService"/>，本文件只负责界面与交互。
/// </summary>
public partial class EditorPage : Page
{
    private readonly MotionEngine _engine = App.Engine;
    /// <summary>轴顺序（唯一真源见 <see cref="Hexa.Services.Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxIds = Hexa.Services.Osr6DeviceProfile.InstalledAxes;

    // 与 MotionEngine.CwCycleLen 的内部 clamp 保持一致（0.1..60）
    private const double MaxCycleSeconds = 60.0;

    /// <summary>
    /// 各轴「这份画布数据是从哪份 funscript 导入的」（原始动作点 + 原始时长，见 <see cref="WaveSource"/>）。
    ///
    /// 为什么要留着它：画布只有 650 格宽、拖动的 X 还会被取整，时间一旦经过画布就回不到原样
    /// （30s 脚本一格 ≈ 46ms）。导入时在这里记一份原始点，导出时：
    ///   没动过 → 原样写回原始点（逐点毫秒一致，不过画布网格）；
    ///   动过   → 用画布当前代表的时间反算，但**没动过的点仍用原始时间**，拖一个点不会让整条时间轴变形。
    /// 它跟着草稿一起落盘（<see cref="WaveDraftStore.Sources"/>），所以重启后仍然有效。
    /// </summary>
    private Dictionary<string, WaveSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Color[] AxColors =
    {
        Color.FromRgb(0xa7,0x8b,0xfa), Color.FromRgb(0x67,0xe8,0xf9),
        Color.FromRgb(0x86,0xef,0xac), Color.FromRgb(0xfb,0xa3,0x4a),
        Color.FromRgb(0xf4,0x72,0x72), Color.FromRgb(0xf9,0xd7,0x2e)
    };

    private string _editAxis = "L0";
    private bool _editBezier = true;
    private int _dragIdx = -1;
    private string _dragAxis = "L0";      // P1-7：拖动中的轴，避免拖到一半切换轴后改错数据
    private bool _dragPending;            // P1-7：已按下但还没超过系统拖拽阈值
    private Point _dragStartView;         // P1-7：按下时的视图坐标（用于判断是否真的在拖）
    private bool _dragMoved;          // 标记拖拽过程中是否真正移动了点

    // ── 关键帧选中（点击列表项 / 画布控制点后高亮） ──────────────────
    private string? _selAxis;
    private double  _selX;

    // ── 画布自适应：模型坐标始终是 650x240，只把视图坐标按比例缩放 ────
    private bool _redrawPending;

    // ── 播放头（本地按周期 × 倍率推算，仅用于画布预览） ───────────────
    private readonly Line _playheadLine = new()
    {
        Stroke          = new SolidColorBrush(Color.FromRgb(0x5b,0xa7,0xff)),
        StrokeThickness = 1.5,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        IsHitTestVisible = false,
        Visibility      = Visibility.Collapsed
    };
    private readonly DispatcherTimer _playheadTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _playheadWatch = new();
    private double _playheadPhase;    // 0..1 当前圈进度

    // P1-10：独立状态行 6 秒后自动清空。放在这里而不是 Redraw 里，避免下一次重绘把提示擦掉。
    private readonly DispatcherTimer _waveStatusTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    // ── Undo / Redo（按轴独立维护） ──────────────────────────────────
    private readonly Dictionary<string, Stack<List<(double X, double Y)>>> _undoStacks =
        new() { ["L0"]=new(), ["L1"]=new(), ["L2"]=new(), ["R0"]=new(), ["R1"]=new(), ["R2"]=new() };
    private readonly Dictionary<string, Stack<List<(double X, double Y)>>> _redoStacks =
        new() { ["L0"]=new(), ["L1"]=new(), ["L2"]=new(), ["R0"]=new(), ["R1"]=new(), ["R2"]=new() };

    // ══════════════════════════════════════════════════════════════════
    //  编排（标签页 1）状态
    // ══════════════════════════════════════════════════════════════════

    /// <summary>拖拽动作库条目时使用的数据格式（只放 PresetId 字符串）。</summary>
    private const string LibraryDragFormat = "Hexa.ScriptPresetId";

    /// <summary>段卡片之间的水平间距（标尺 / 播放头换算也要算上它）。</summary>
    private const double CardGap = 4.0;

    private ScriptComposition _compose = new() { Name = "未命名" };
    private string? _compositionPath;          // 当前合成文件路径（null = 还没保存过）
    private bool _composeDirty;                // 有未保存改动
    private ScriptSegment? _selectedSegment;   // 当前选中段

    private Point _libraryDragStart;
    private StrokePreset? _libraryDragPreset;

    // ── 段卡片拖拽排序（拖卡片主体换位置；右边缘 grip 仍然只管改时长）──────
    private const double SegmentDragOpacity = 0.45;   // 拖动中卡片的不透明度
    private ScriptSegment? _reorderCandidate;         // 左键已按下、还没超过拖拽阈值的段
    private Point _reorderStartPoint;                 // 按下点（SegmentPanel 坐标）
    private bool _reordering;                         // 是否已进入拖拽排序
    private ScriptSegment? _reorderingSegment;        // 正在拖的段（数据对象，卡片重建也不失效）
    private Border? _reorderingCard;                  // 正在拖的卡片（用于半透明反馈）
    private Border? _reorderIndicator;                // 目标位置的插入指示条
    private bool _reorderHandlersHooked;              // SegmentPanel 上的拖拽处理器只挂一次

    private readonly DispatcherTimer _composeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _composeWatch = new();
    private bool _composePlaying;
    /// <summary>本次编排播放是否只做预览（起播时设备不可动，用户没要求驱动或驱动不了）。</summary>
    private bool _composePreviewOnly;

    // ── 播放中改动段参数的处理 ───────────────────────────────────────
    // 播放位置 = _playheadOffset + 墙钟。改段时长会让总时长和后续段的起点整体平移，
    // 如果直接按墙钟走，播放头会瞬间跳到另一段（设备也跟着跳）。
    // 这里每帧记下「当前在第几段、段内进度多少」，一旦检测到合成被改动就按新的
    // 几何重新锚定，让播放头留在同一段的同一进度上。
    private double _playheadOffset;
    private ScriptSegment? _playAnchorSegment;
    private double _playAnchorProgress;
    private long _composeSignature;
    private int _composeEditHintTicks;          // 「改动已实时生效」提示的剩余帧数（40ms/帧）

    private bool _syncingSegmentPanel;         // 防止参数面板回写触发递归

    // ══════════════════════════════════════════════════════════════════
    //  编排页的独立撤销 / 重做栈（与「微调波形」页的 _undoStacks 完全分开）
    //
    //  栈里存的是整个 ScriptComposition 的深拷贝快照（名字 / BPM / 全部段参数）
    //  + 当时选中的段下标，最多 100 步；每次会改动合成之前先压栈。
    //  连续型改动（拖滑块、拖卡片右边缘）用 300ms 时间窗合并，一次拖拽只留一个撤销步；
    //  离散操作（添加 / 删除 / 复制 / 移动）每次都单独压栈，连点两次仍是两步。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>一步编排撤销快照：整个合成的深拷贝 + 当时的选中段下标（-1 = 没选中）。</summary>
    private sealed record ComposeUndoSnapshot(ScriptComposition Compose, int SelectedIndex, string Label);

    private const int MaxComposeUndoSteps   = 100;
    private const int ComposeUndoCoalesceMs = 300;   // 连续型改动的合并窗口

    private readonly List<ComposeUndoSnapshot> _composeUndoStack = new();
    private readonly List<ComposeUndoSnapshot> _composeRedoStack = new();
    private string? _composeUndoCoalesceLabel;   // 正在合并的连续改动标签（null = 没有进行中的连续改动）
    private long    _composeUndoCoalesceAt;      // 上一次连续改动的时间戳（Environment.TickCount64）
    private bool    _restoringComposeUndo;       // 恢复快照期间不要再压栈

    /// <summary>段卡片内部控件的引用（拖右边缘改时长时直接改这些，避免重建卡片打断拖拽）。</summary>
    private sealed record SegmentCardRef(
        ScriptSegment Segment, Border Card, TextBlock Title, TextBlock Info, TextBlock Transition);

    private void PushUndo() => PushUndo(_editAxis);

    private void PushUndo(string axisId)
    {
        _undoStacks[axisId].Push(new List<(double X, double Y)>(_engine.EditWaves[axisId]));
        _redoStacks[axisId].Clear();
        UpdateWaveUndoButtons();
    }

    /// <summary>P1-10：写独立状态行；6 秒后自动清空，连续提示会重新计时（避免旧计时器提前清掉新提示）。</summary>
    private void SetWaveStatus(string text, bool warning = false)
    {
        WaveStatusLabel.Text = text;
        WaveStatusLabel.Foreground = (Brush)FindResource(warning ? "Warning" : "Muted");
        _waveStatusTimer.Stop();
        _waveStatusTimer.Start();
    }

    /// <summary>P1-9 可选：让可见的撤销/重做按钮跟着栈状态置灰。</summary>
    private void UpdateWaveUndoButtons()
    {
        WaveUndoBtn.IsEnabled = _undoStacks[_editAxis].Count > 0;
        WaveRedoBtn.IsEnabled = _redoStacks[_editAxis].Count > 0;
    }

    private void WaveUndo_Click(object sender, RoutedEventArgs e) => UndoWave();
    private void WaveRedo_Click(object sender, RoutedEventArgs e) => RedoWave();

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        // 「编排」标签页里 Ctrl+Z 改的是不可见的微调波形数据，属于误伤，直接忽略
        if (MainTabs.SelectedIndex == 0) return;
        UndoWave();
    }

    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 0) return;
        RedoWave();
    }

    private void UndoWave()
    {
        var undoStack = _undoStacks[_editAxis];
        if (undoStack.Count == 0) return;
        _redoStacks[_editAxis].Push(new List<(double X, double Y)>(_engine.EditWaves[_editAxis]));
        var pts = _engine.EditWaves[_editAxis];
        pts.Clear();
        pts.AddRange(undoStack.Pop());
        _engine.MarkWaveDirty(_editAxis);
        SetWaveStatus($"已撤销 {_editAxis} 的上一次波形改动。");
        Redraw();
        UpdateWaveUndoButtons();
    }

    private void RedoWave()
    {
        var redoStack = _redoStacks[_editAxis];
        if (redoStack.Count == 0) return;
        _undoStacks[_editAxis].Push(new List<(double X, double Y)>(_engine.EditWaves[_editAxis]));
        var pts = _engine.EditWaves[_editAxis];
        pts.Clear();
        pts.AddRange(redoStack.Pop());
        _engine.MarkWaveDirty(_editAxis);
        SetWaveStatus($"已重做 {_editAxis} 的波形改动。");
        Redraw();
        UpdateWaveUndoButtons();
    }

    // ── 构造 ──────────────────────────────────────────────────────────
    public EditorPage()
    {
        InitializeComponent();
        // 导入来源跟着草稿一起恢复（App 启动时已经读过草稿）：没有的一段就是空表，一切照旧
        _sources = WaveDraftStore.Sources.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        // 这里**不能**无条件写成 true：草稿里存过用户的「平滑插值 / 线性插值」选择
        //（App 启动时已按草稿恢复，见 App.xaml.cs 的 Engine.UseSmoothCustomInterpolation = draft.UseSmooth），
        // 而 EditorPage 一构造就会执行到这里 —— 以前等于"用户关掉平滑插值，重启后一进脚本制作页又被强制打开"。
        // 正确做法：以引擎当前值为准，并把按钮文案/样式同步过去。
        _editBezier = _engine.UseSmoothCustomInterpolation;
        BezierBtn.Content = _editBezier ? "平滑插值" : "线性插值";
        BezierBtn.Style   = (Style)FindResource(_editBezier ? "BtnPrimary" : "BtnSecondary");
        BuildAxisTabs();
        BuildAxisEnablePanel();
        CycleSlider.Value = _engine.CwCycleLen;
        CycleLabel.Text   = _engine.CwCycleLen.ToString("F1") + "s";
        CycleSlider.ValueChanged += (_, e) =>
        {
            _engine.CwCycleLen = e.NewValue;
            CycleLabel.Text = e.NewValue.ToString("F1") + "s";
            UpdateExportInfo();
            // P1-12：周期改的是整幅画布对应的时间，点的 X 不变，所以所有点的时间会按比例缩放
            SetWaveStatus($"周期已改为 {e.NewValue:0.##}s：所有控制点的时间按比例缩放（不可撤销）。");
            RequestRedraw();   // 时间轴刻度与关键帧时间随周期变化
        };
        LoopSlider.ValueChanged += (_, _) => UpdateExportInfo();
        ScriptSpeedSlider.Value = _engine.ScriptSpeed;
        ScriptSpeedLabel.Text = $"{_engine.ScriptSpeed:0.00}x";
        ScriptSpeedSlider.ValueChanged += (_, e) =>
        {
            _engine.ScriptSpeed = e.NewValue;
            App.Settings.ScriptPlaybackSpeed = _engine.ScriptSpeed;
            ScriptSpeedLabel.Text = $"{_engine.ScriptSpeed:0.00}x";
            App.Settings.Save();
        };

        // 播放头：本地按 周期 × 脚本倍率 推算，只移动一根虚线，不整体重绘
        _playheadTimer.Tick += (_, _) =>
        {
            double cycle = Math.Max(0.1, _engine.CwCycleLen);
            double seconds = _playheadWatch.Elapsed.TotalSeconds * _engine.ScriptSpeed;
            _playheadPhase = seconds % cycle / cycle;
            PositionPlayhead();
        };

        // P1-10：独立状态行到点自动清空；每次 SetWaveStatus 会 Stop/Start 重新计时
        _waveStatusTimer.Tick += (_, _) =>
        {
            _waveStatusTimer.Stop();
            WaveStatusLabel.Text = "";
        };

        // 编排页：动作库 + 参数面板 + 播放定时器
        BuildComposeLibrary();
        WireComposeControls();
        // 「播放」在编排页与「微调波形」页同名不同义：这里只是本地 3D 预览，只有勾了
        // 「同步驱动设备」才会真的向设备发指令（微调页的「播放」是直接驱动设备的）。
        // 按钮在 XAML 里（不归本文件），但 ToolTip 是运行时属性，在这里设置不冲突。
        ComposePlayBtn.ToolTip =
            "预览播放：只让 3D 预览按时间轴走，不会动设备。\n" +
            "要真的驱动设备，先勾选上面的「同步驱动设备」。\n" +
            "（「微调波形」页的「播放」是直接驱动设备的，两者不一样。）";
        _composeTimer.Tick += (_, _) => ComposeTick();
        MainTabs.SelectionChanged += (_, _) =>
        {
            // 编排页由本页按帧喂 ComposePreview3D.UpdateAxes，必须关掉控件内置的引擎刷新，
            // 否则两路数据互相覆盖，预览里的动作会闪（用户反馈）。
            ComposePreview3D.AutoRefreshEnabled = MainTabs.SelectedIndex != 0;
            // 离开编排页就停掉编排试听，避免和微调页的播放抢设备
            if (MainTabs.SelectedIndex != 0) ComposeStopPlayback();
            // 切回编排页时立刻按当前合成重画一帧（切标签会重载控件内容，别让它停在引擎值上）
            else UpdateComposePreview(_previewSeconds, false);
            // P0-1：切到「编排」页必须停掉波形试跑，否则设备继续动，而停止按钮在隐藏的微调标签页里
            if (MainTabs.SelectedIndex == 0 && _engine.CustomRunning)
            {
                _engine.StopCustom();
                SyncPlayState();
            }
            // 切走时别留下画布上的鼠标捕获/半途拖动
            if (MainTabs.SelectedIndex != 1) EndWaveDrag();
        };
        ComposePreview3D.AutoRefreshEnabled = MainTabs.SelectedIndex != 0;
        RefreshComposeUi();

        // 首次布局完成后用真实画布尺寸重绘（构造时 ActualWidth 还是 0）
        Loaded += (_, _) =>
        {
            Redraw();
            // Frame 重新导航回本页时 Loaded / IsVisibleChanged 都会触发；
            // 两处都调一次，指纹没变的那次直接空转（见 RefreshComposeLibraryIfChanged）。
            RefreshComposeLibraryIfChanged();
            ShowWaveDraftHintOnce();
            UpdateComposePreview(_previewSeconds, false);
            OfferLastCompositionOnce();
        };

        // 播放状态可能被其它页面/急停改变，统一同步按钮与播放头
        // 用 BeginInvoke 异步回 UI 线程，避免从引擎定时器线程同步 Invoke 造成卡顿
        App.Engine.StateChanged += () =>
            Dispatcher.BeginInvoke(new Action(SyncPlayState), DispatcherPriority.Background);

        Redraw();

        // 页面缓存 → 切换回来时同步 EditAxis（游玩页点击波形传来的）
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _playheadTimer.Stop();   // 页面不可见时不再刷播放头
                ComposeStopPlayback();
                // P0-1：离开页面必须停掉波形试跑，否则设备会一直动，而停止按钮已经不在屏幕上
                if (_engine.CustomRunning) _engine.StopCustom();
                EndWaveDrag();   // 别把鼠标捕获/半途拖动留到下次进页面
                return;
            }
            // 回到页面时按当前标签页重新决定：编排页由本页喂值，微调页交给控件自己刷
            ComposePreview3D.AutoRefreshEnabled = MainTabs.SelectedIndex != 0;
            if (_engine.EditAxis != _editAxis)
            {
                _editAxis = _engine.EditAxis;
                BuildAxisTabs();
                Redraw();
            }
            // 页面被 MainWindow 缓存：构造时建的动作库列表不会自己更新。
            // 在「动作」页新建/导入/删除动作后切回本页，必须重建才能看到（以前要重启程序）。
            RefreshComposeLibraryIfChanged();
            SyncPlayState();
        };
    }

    // ── 播放状态同步（按钮文案 + 播放头启停） ─────────────────────────
    private void SyncPlayState()
    {
        // 急停后编排播放必须停：否则定时器继续跑、3D 继续动，用户以为还在播
        if (App.Engine.EmergencyStopped && _composePlaying) ComposeStopPlayback();
        bool running = _engine.CustomRunning;
        PlayCwBtn.Content = running ? "⏹ 停止" : "▶ 播放";
        PlayCwBtn.Style   = (Style)FindResource(running ? "BtnDanger" : "BtnSecondary");
        if (running)
        {
            if (!_playheadWatch.IsRunning) _playheadWatch.Restart();
            if (IsVisible && !_playheadTimer.IsEnabled) _playheadTimer.Start();
        }
        else
        {
            _playheadWatch.Reset();
            _playheadTimer.Stop();
            _playheadPhase = 0;
        }
        PositionPlayhead();
    }

    // ── Axis tab buttons ──────────────────────────────────────────────
    private void BuildAxisTabs()
    {
        AxisTabs.Children.Clear();
        for (int i = 0; i < 6; i++)
        {
            var id     = AxIds[i];
            bool active = id == _editAxis;
            var btn = new Button
            {
                Content  = id,
                Width    = 40, Height = 28, FontSize = 12,
                Margin   = new Thickness(0, 0, 4, 0),
                Style    = (Style)FindResource(active ? "BtnPrimary" : "BtnSecondary"),
                Tag      = id
            };
            btn.Click += (_, _) => { _editAxis = id; BuildAxisTabs(); Redraw(); };
            AxisTabs.Children.Add(btn);
        }
    }

    // ── Axis enable checkboxes ────────────────────────────────────────
    private void BuildAxisEnablePanel()
    {
        AxisEnablePanel.Children.Clear();
        for (int i = 0; i < 6; i++)
        {
            var idx = i;
            var cb  = new CheckBox
            {
                Content   = AxIds[i],
                IsChecked = _engine.WfAxisEnabled[i],
                Foreground = new SolidColorBrush(AxColors[i]),
                FontSize  = 12,
                Margin    = new Thickness(0, 0, 12, 0),
                // P0-3：把「勾选 = 参与试跑」写在控件上，减少「画完点播放设备不动」的困惑
                ToolTip   = "勾选后该轴参与试跑；点数 <2 的轴会保持中位 50"
            };
            cb.Checked   += (_, _) => _engine.WfAxisEnabled[idx] = true;
            cb.Unchecked += (_, _) => _engine.WfAxisEnabled[idx] = false;
            AxisEnablePanel.Children.Add(cb);
        }
    }

    // ── Canvas interaction ────────────────────────────────────────────
    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p   = e.GetPosition(WaveCanvas);
        var pts = _engine.EditWaves[_editAxis];

        // 检查是否点中已有控制点（拖拽模式）
        int hit = HitTestPoint(pts, p);
        if (hit >= 0)
        {
            // P1-7：先只记状态并捕获鼠标，不立刻 PushUndo。
            // 超过系统拖拽阈值才算拖动，避免「点一下」就清空重做栈、留下撤销步。
            _dragAxis      = _editAxis;
            _dragIdx       = hit;
            _dragPending   = true;
            _dragMoved     = false;
            _dragStartView = p;
            WaveCanvas.CaptureMouse();
            SelectKeyframe(_editAxis, pts[hit].X);   // 点中控制点也高亮对应关键帧
            return;
        }

        // 新增控制点（视图坐标 → 模型坐标）
        var model = ViewToModel(p);
        double x = Math.Round(model.X), y = Math.Round(model.Y);
        PushUndo();
        // P1-6 / P0-7：同一 X 上只保留一个点，否则画布/列表显示两个点而导出只有一个
        int dup = pts.FindIndex(q => Math.Abs(q.X - x) < 0.5);
        if (dup >= 0)
        {
            pts[dup] = (x, y);
            SetWaveStatus($"X = {x:0} 处已有关键帧，已替换为当前位置（同一 X 会合并成一个动作）。");
        }
        else
        {
            pts.Add((x, y));
        }
        _engine.MarkWaveDirty(_editAxis);
        SelectKeyframe(_editAxis, x);   // P1-6：传取整后的 X，新点才会高亮/选中

        // P0-3 补充：给未勾选的轴加点时提醒一句，否则用户画完点播放设备不会动
        if (dup < 0 && !_engine.WfAxisEnabled[Array.IndexOf(AxIds, _editAxis)])
            SetWaveStatus($"已给 {_editAxis} 加点，但该轴尚未勾选，试跑不会动。", warning: true);
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndWaveDrag();

    private void Canvas_MouseLeave(object sender, MouseEventArgs e)
    {
        // P1-7：有鼠标捕获时移出画布仍要继续拖；真正结束由 MouseLeftButtonUp / LostMouseCapture 兜底
        if (!WaveCanvas.IsMouseCaptured) EndWaveDrag();
    }

    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e) => EndWaveDrag();

    /// <summary>P1-7：结束拖动。只有真正拖动过才 Redraw 补关键帧列表；没拖动则不留撤销步。</summary>
    private void EndWaveDrag()
    {
        // ReleaseMouseCapture 会再触发 LostMouseCapture，靠 _dragIdx/_dragPending 复位避免重入
        if (_dragIdx < 0 && !_dragPending)
        {
            // 状态已经复位但捕获还在（异常路径）：也顺手释放，别让画布一直吃鼠标事件
            if (WaveCanvas.IsMouseCaptured) WaveCanvas.ReleaseMouseCapture();
            return;
        }
        bool dragged = _dragMoved;
        _dragIdx     = -1;
        _dragPending = false;
        _dragMoved   = false;
        if (WaveCanvas.IsMouseCaptured) WaveCanvas.ReleaseMouseCapture();
        if (dragged) Redraw();   // 拖动期间跳过了关键帧列表，这里补一次
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIdx < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(WaveCanvas);

        // P1-7：超过系统阈值才认定为拖动；到这一步才 PushUndo（一次拖动只留一步撤销）
        if (_dragPending)
        {
            double dx = Math.Abs(p.X - _dragStartView.X);
            double dy = Math.Abs(p.Y - _dragStartView.Y);
            if (dx < SystemParameters.MinimumHorizontalDragDistance
                && dy < SystemParameters.MinimumVerticalDragDistance)
                return;
            _dragPending = false;
            _dragMoved   = true;
            PushUndo(_dragAxis);
        }

        var pts = _engine.EditWaves[_dragAxis];
        if (_dragIdx < pts.Count)
        {
            var model = ViewToModel(p);
            pts[_dragIdx] = (Math.Round(model.X), Math.Round(model.Y));
            _selAxis   = _dragAxis;      // 拖动时选中跟随该点
            _selX      = pts[_dragIdx].X;
            _engine.MarkWaveDirty(_dragAxis);
            Redraw(rebuildKeyframeList: false);
        }
    }

    private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p   = e.GetPosition(WaveCanvas);
        var pts = _engine.EditWaves[_editAxis];
        if (pts.Count == 0) return;
        int near = HitTestPoint(pts, p, 12);
        if (near >= 0)
        {
            double removedX = pts[near].X;
            PushUndo();
            pts.RemoveAt(near);
            if (IsSelected(_editAxis, removedX)) { _selAxis = null; _selX = 0; }
            _engine.MarkWaveDirty(_editAxis);
            Redraw();
        }
    }

    // ── 视图坐标 ↔ 模型坐标（模型固定 650x240，视图等比缩放 + letterbox 居中） ──
    private double ViewWidth  => WaveCanvas.ActualWidth  > 1 ? WaveCanvas.ActualWidth  : WaveScriptCodec.CanvasWidth;
    private double ViewHeight => WaveCanvas.ActualHeight > 1 ? WaveCanvas.ActualHeight : WaveScriptCodec.CanvasHeight;

    // P1-8：横纵必须用同一个缩放比，否则波形会被拉伸/压扁，和模型坐标/设备输出对不上。
    // 多出来的边距用 Offset 居中，形成 letterbox。
    private double Scale => Math.Min(ViewWidth / WaveScriptCodec.CanvasWidth,
                                     ViewHeight / WaveScriptCodec.CanvasHeight);
    private double OffsetX => (ViewWidth  - WaveScriptCodec.CanvasWidth  * Scale) / 2;
    private double OffsetY => (ViewHeight - WaveScriptCodec.CanvasHeight * Scale) / 2;
    private double ModelViewWidth  => WaveScriptCodec.CanvasWidth  * Scale;
    private double ModelViewHeight => WaveScriptCodec.CanvasHeight * Scale;

    private Point V(double modelX, double modelY) =>
        new(OffsetX + modelX * Scale, OffsetY + modelY * Scale);

    private Point ViewToModel(Point view) => new(
        Math.Clamp((view.X - OffsetX) / Scale, 0, WaveScriptCodec.CanvasWidth),
        Math.Clamp((view.Y - OffsetY) / Scale, 0, WaveScriptCodec.CanvasHeight));

    // 命中测试在视图坐标里做，阈值保持固定像素，不随缩放变化
    private int HitTestPoint(IReadOnlyList<(double X, double Y)> pts, Point view, double radius = 9)
    {
        int best = -1; double bestDist = radius;
        for (int i = 0; i < pts.Count; i++)
        {
            var v  = V(pts[i].X, pts[i].Y);
            double dx = v.X - view.X;
            double dy = v.Y - view.Y;
            double d  = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // 画布尺寸变化（窗口缩放）→ 合并多次事件后重绘
    private void WaveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged && !e.HeightChanged) return;
        RequestRedraw();
    }

    private void RequestRedraw()
    {
        if (_redrawPending) return;
        _redrawPending = true;
        Dispatcher.BeginInvoke(new Action(() => { _redrawPending = false; Redraw(); }),
            DispatcherPriority.Background);
    }

    private bool IsSelected(string axisId, double x) =>
        _selAxis == axisId && Math.Abs(_selX - x) < 0.001;

    // 选中一个关键帧：切换轴 + 高亮 + 重绘（同时刷新列表里的高亮行）
    private void SelectKeyframe(string axisId, double x)
    {
        _selAxis = axisId;
        _selX    = x;
        if (_engine.EditAxis != axisId) _engine.EditAxis = axisId;
        if (_editAxis != axisId)
        {
            _editAxis = axisId;
            BuildAxisTabs();
        }
        Redraw();
    }

    // ── Redraw canvas ─────────────────────────────────────────────────
    // 模型坐标固定 650x240（导出换算不变），绘制时按视图尺寸等比缩放
    /// <summary>
    /// 重绘画布。rebuildKeyframeList=false 时跳过右下角「关键帧列表」的重建——
    /// 拖动控制点时每帧重建上百行 Border+Button 是这里最大的开销，而列表内容在拖动过程中
    /// 并不需要实时更新（松手时再刷一次）。波形本身照常重画，拖动反馈不受影响。
    /// </summary>
    private void Redraw(bool rebuildKeyframeList = true)
    {
        WaveCanvas.Children.Clear();
        RulerCanvas.Children.Clear();

        double w = ViewWidth, h = ViewHeight;
        double cycle = Math.Max(0.1, _engine.CwCycleLen);
        double step  = NiceTimeStep(cycle);

        // P1-8：画布内容只画在等比缩放后的模型矩形里，多出来的区域就是 letterbox。
        double offX = OffsetX, offY = OffsetY;
        double modelW = ModelViewWidth, modelH = ModelViewHeight;

        // 选中的点若已被删除/覆盖，自动清除高亮
        if (_selAxis != null
            && !_engine.EditWaves[_selAxis].Any(p => Math.Abs(p.X - _selX) < 0.001))
        {
            _selAxis = null;
            _selX    = 0;
        }

        // 模型区域描边，让 letterbox 的边界可见
        var frame = new System.Windows.Shapes.Rectangle
        {
            Width  = modelW,
            Height = modelH,
            Stroke = new SolidColorBrush(Color.FromRgb(0x22,0x22,0x2a)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        System.Windows.Controls.Canvas.SetLeft(frame, offX);
        System.Windows.Controls.Canvas.SetTop(frame, offY);
        WaveCanvas.Children.Add(frame);

        // 网格：竖线与时间轴刻度对齐（每格 = step 秒），横线四等分；只画在模型矩形内
        for (double t = 0; t <= cycle + 1e-9; t += step)
        {
            double x = offX + t / cycle * modelW;
            WaveCanvas.Children.Add(new Line { X1 = x, Y1 = offY, X2 = x, Y2 = offY + modelH,
                Stroke = new SolidColorBrush(Color.FromRgb(0x22,0x22,0x2a)) });
        }
        for (int i = 0; i <= 4; i++)
        {
            double y = offY + modelH * i / 4.0;
            WaveCanvas.Children.Add(new Line { X1 = offX, Y1 = y, X2 = offX + modelW, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(0x22,0x22,0x2a)) });
        }

        // Draw all axes
        for (int ai = 0; ai < 6; ai++)
        {
            var ap    = _engine.EditWaves[AxIds[ai]].OrderBy(p => p.X).ToList();
            if (ap.Count < 2) continue;
            byte alpha = AxIds[ai] == _editAxis ? (byte)255 : (byte)45;
            var  col   = Color.FromArgb(alpha, AxColors[ai].R, AxColors[ai].G, AxColors[ai].B);

            if (_editBezier && AxIds[ai] == _editAxis)
            {
                var geo = new PathGeometry();
                var fig = new PathFigure { StartPoint = V(ap[0].X, ap[0].Y) };
                for (int i = 0; i < ap.Count - 1; i++)
                {
                    var p0 = ap[Math.Max(0, i - 1)];
                    var p1 = ap[i]; var p2 = ap[i + 1];
                    var p3 = ap[Math.Min(ap.Count - 1, i + 2)];
                    // 均匀缩放不改变贝塞尔形状：控制点在模型空间算好再映射到视图
                    fig.Segments.Add(new BezierSegment(
                        V(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                        V(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6),
                        V(p2.X, p2.Y), true));
                }
                geo.Figures.Add(fig);
                WaveCanvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = geo,
                    Stroke = new SolidColorBrush(col),
                    StrokeThickness = AxIds[ai] == _editAxis ? 2 : 0.8
                });
            }
            else
            {
                var poly = new Polyline
                {
                    Stroke = new SolidColorBrush(col),
                    StrokeThickness = AxIds[ai] == _editAxis ? 2 : 0.8
                };
                foreach (var p in ap) poly.Points.Add(V(p.X, p.Y));
                WaveCanvas.Children.Add(poly);
            }
        }

        // Control points for active axis（选中的点加一圈高亮环）
        var ps    = _engine.EditWaves[_editAxis].OrderBy(p => p.X).ToList();
        int axIdx = Array.IndexOf(AxIds, _editAxis);
        foreach (var p in ps)
        {
            var v = V(p.X, p.Y);
            bool selected = IsSelected(_editAxis, p.X);
            if (selected)
            {
                var ring = new Ellipse
                {
                    Width = 16, Height = 16,
                    Stroke = (Brush)FindResource("Accent"),
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent
                };
                System.Windows.Controls.Canvas.SetLeft(ring, v.X - 8);
                System.Windows.Controls.Canvas.SetTop(ring, v.Y - 8);
                WaveCanvas.Children.Add(ring);
            }
            var dot = new Ellipse
            {
                Width  = selected ? 10 : 8,
                Height = selected ? 10 : 8,
                Fill   = new SolidColorBrush(AxColors[axIdx])
            };
            System.Windows.Controls.Canvas.SetLeft(dot, v.X - dot.Width / 2);
            System.Windows.Controls.Canvas.SetTop(dot, v.Y - dot.Height / 2);
            WaveCanvas.Children.Add(dot);
        }

        // 播放头（重绘后重新挂回，位置由计时器更新）
        WaveCanvas.Children.Add(_playheadLine);
        PositionPlayhead();

        // 时间轴刻度
        DrawTimeRuler(cycle, step);

        // Info + keyframe list（P1-8：这里显示模型尺寸，视图尺寸只是实际像素）
        InfoLabel.Text = $"轴: {_editAxis}  |  点数: {ps.Count}  |  画布 650×240（视图 {w:0}×{h:0}）";
        CanvasHintLabel.Foreground = (Brush)FindResource("Muted");
        // 这一行必须"常显"：时长/上限是用户判断"画布上到底装了多少"的唯一依据，
        // 不能用 6 秒就消失的状态行（WaveStatusLabel）来承担。
        CanvasHintLabel.Text =
            $"时间轴：1 格 = {step:0.##}s，整幅画布 = {cycle:0.##}s（一圈，上限 {MaxCycleSeconds:0}s）"
            + SourceHintText()
            + "  ·  左键加点 / 拖动控制点 / 右键删除 / Ctrl+Z 撤销";

        UpdateWaveUndoButtons();   // P1-9 可选：栈空时撤销/重做按钮置灰

        if (!rebuildKeyframeList) return;

        KeyframeList.Children.Clear();
        KeyframeList.Children.Add(new TextBlock
        {
            Text       = "时间(s)  |  位置(%)  |  轴",
            FontSize   = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55,0x55,0x66)),
            Margin     = new Thickness(0, 0, 0, 2)
        });
        // 六轴总点数很多时只列当前轴，避免全量重建几百行 Border+Button 卡顿
        int totalPoints = AxIds.Sum(id => _engine.EditWaves[id].Count);
        if (totalPoints > 300)
        {
            int ai = Array.IndexOf(AxIds, _editAxis);
            foreach (var p in _engine.EditWaves[_editAxis].OrderBy(q => q.X))
                KeyframeList.Children.Add(BuildKeyframeRow(_editAxis, ai, p));

            int otherPoints = totalPoints - _engine.EditWaves[_editAxis].Count;
            if (otherPoints > 0)
            {
                KeyframeList.Children.Add(new TextBlock
                {
                    Text       = $"其它轴共 {otherPoints} 点（切到对应轴查看）",
                    FontSize   = 10,
                    Foreground = (Brush)FindResource("Muted"),
                    Margin     = new Thickness(6, 4, 0, 0)
                });
            }
        }
        else
        {
            // 当前轴排最前，其它轴跟在后面；点击行会切到对应轴
            foreach (string axisId in AxIds.OrderBy(id => id == _editAxis ? 0 : 1))
            {
                int ai = Array.IndexOf(AxIds, axisId);
                foreach (var p in _engine.EditWaves[axisId].OrderBy(q => q.X))
                    KeyframeList.Children.Add(BuildKeyframeRow(axisId, ai, p));
            }
        }

        UpdateExportInfo();
    }

    // ── 时间轴刻度：整幅画布 = 一圈（P1-8：和画布一样用 OffsetX/ModelViewWidth） ──
    private void DrawTimeRuler(double cycle, double step)
    {
        double offX   = OffsetX;
        double modelW = ModelViewWidth;
        double pxPerSecond = modelW / cycle;
        double minor = step / 2;
        int count = (int)Math.Round(cycle / minor);
        for (int i = 0; i <= count; i++)
        {
            double t = i * minor;
            if (t > cycle + 1e-6) break;
            double x = offX + t * pxPerSecond;
            bool major = i % 2 == 0;
            RulerCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = major ? 0 : 10, X2 = x, Y2 = 16,
                Stroke = new SolidColorBrush(major
                    ? Color.FromRgb(0x55,0x5c,0x66)
                    : Color.FromRgb(0x33,0x38,0x40)),
                StrokeThickness = 1
            });
            if (!major) continue;
            var label = new TextBlock
            {
                Text       = t.ToString("0.##") + "s",
                FontSize   = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6b,0x76,0x80))
            };
            System.Windows.Controls.Canvas.SetLeft(label,
                Math.Min(x + 2, Math.Max(offX, offX + modelW - 30)));
            System.Windows.Controls.Canvas.SetTop(label, 0);
            RulerCanvas.Children.Add(label);
        }
    }

    // 取「好看」的刻度步长，使整幅画布约 10 格
    private static double NiceTimeStep(double cycleSeconds)
    {
        double raw = cycleSeconds / 10.0;
        double[] candidates = { 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5, 10, 15, 30, 60, 120, 300 };
        foreach (double c in candidates)
            if (raw <= c + 1e-9) return c;
        return 600;
    }

    // ── 播放头（仅播放中显示；P1-8：只覆盖模型矩形，和波形/刻度对齐） ──
    private void PositionPlayhead()
    {
        bool show = _engine.CustomRunning;
        _playheadLine.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        double x = OffsetX + Math.Clamp(_playheadPhase, 0, 1) * ModelViewWidth;
        _playheadLine.X1 = x; _playheadLine.X2 = x;
        _playheadLine.Y1 = OffsetY; _playheadLine.Y2 = OffsetY + ModelViewHeight;
    }

    // ── 关键帧行：点击定位高亮 / ✕ 删除 ──────────────────────────────
    private sealed record KeyframeRef(string AxisId, double X);

    private FrameworkElement BuildKeyframeRow(string axisId, int axisIndex, (double X, double Y) p)
    {
        double cycle = Math.Max(0.1, _engine.CwCycleLen);
        double t      = p.X / WaveScriptCodec.CanvasWidth * cycle;
        double pct    = 100 - p.Y / WaveScriptCodec.CanvasHeight * 100;
        bool selected = IsSelected(axisId, p.X);
        bool active   = axisId == _editAxis;

        var text = new TextBlock
        {
            Text       = $"{t:0.###}s  →  {pct:0}%  [{axisId}]",
            FontSize   = 10,
            Foreground = new SolidColorBrush(AxColors[axisIndex]),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal
        };

        var del = new Button
        {
            Content = "✕", Width = 22, Height = 18, FontSize = 10,
            Padding = new Thickness(0),
            Style   = (Style)FindResource("BtnDanger"),
            ToolTip = "删除该关键帧",
            Tag     = new KeyframeRef(axisId, p.X)
        };
        del.Click += KeyframeDelete_Click;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(del, 1);
        grid.Children.Add(text);
        grid.Children.Add(del);

        var row = new Border
        {
            Background      = selected ? (Brush)FindResource("AccentSoft") : Brushes.Transparent,
            BorderBrush     = selected ? (Brush)FindResource("Accent") : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6, 1, 4, 1),
            Margin          = new Thickness(0, 1, 0, 1),
            Cursor          = Cursors.Hand,
            Child           = grid,
            Tag             = new KeyframeRef(axisId, p.X),
            ToolTip         = "点击定位并高亮该关键帧"
        };
        row.MouseLeftButtonDown += KeyframeRow_Click;
        return row;
    }

    private void KeyframeRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not KeyframeRef keyframe) return;
        SelectKeyframe(keyframe.AxisId, keyframe.X);
        e.Handled = true;
    }

    private void KeyframeDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not KeyframeRef keyframe) return;
        var pts = _engine.EditWaves[keyframe.AxisId];
        int idx = -1;
        for (int i = 0; i < pts.Count; i++)
            if (Math.Abs(pts[i].X - keyframe.X) < 0.001) { idx = i; break; }
        if (idx < 0) return;

        PushUndo(keyframe.AxisId);
        pts.RemoveAt(idx);
        if (IsSelected(keyframe.AxisId, keyframe.X)) { _selAxis = null; _selX = 0; }
        _engine.MarkWaveDirty(keyframe.AxisId);
        Redraw();
    }

    // ── 导出信息：圈数上限 + 导出时长 ─────────────────────────────────
    private void UpdateExportInfo()
    {
        double cycle = Math.Max(0.1, _engine.CwCycleLen);

        // 上限取三个约束的最小值：滑块 60 圈、funscript 最长 3600s、单文件动作数 ≤ MaxActions
        int maxLoops = (int)Math.Floor(WaveScriptCodec.MaxDurationMs / 1000.0 / cycle);
        foreach (string id in AxIds)
        {
            int n = _engine.EditWaves[id].Count;
            if (n > 0) maxLoops = Math.Min(maxLoops, Math.Max(1, WaveScriptCodec.MaxActions / n));
        }
        maxLoops = Math.Clamp(maxLoops, 1, 60);

        if (Math.Abs(LoopSlider.Maximum - maxLoops) > 0.5) LoopSlider.Maximum = maxLoops;
        if (LoopSlider.Value > maxLoops) LoopSlider.Value = maxLoops;

        int loops = Math.Max(1, (int)Math.Round(LoopSlider.Value));
        LoopLabel.Text = $"×{loops}";
        double total = cycle * loops;
        ExportDurationLabel.Text = loops > 1
            ? $"导出时长 {cycle:0.##}s × {loops} = {total:0.##}s"
            : $"导出时长 {total:0.##}s";
    }

    // ── Toolbar buttons ───────────────────────────────────────────────
    private void BezierBtn_Click(object sender, RoutedEventArgs e)
    {
        _editBezier     = !_editBezier;
        _engine.UseSmoothCustomInterpolation = _editBezier;
        BezierBtn.Content = _editBezier ? "平滑插值" : "线性插值";
        BezierBtn.Style   = (Style)FindResource(_editBezier ? "BtnPrimary" : "BtnSecondary");
        Redraw();
    }

    private void Sine_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        const int samples = 65;
        var points = new List<(double X, double Y)>(samples);
        for (int i = 0; i < samples; i++)
            points.Add((i * (double)WaveScriptCodec.CanvasWidth / (samples - 1),
                        WaveScriptCodec.CanvasHeight / 2.0 - Math.Sin(Math.Tau * i / (samples - 1)) * 100));
        ReplaceActiveWave(points);
        SetWaveStatus($"轴 {_editAxis}：已生成正弦波（{samples} 个控制点）。");
    }

    private void Square_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        ReplaceActiveWave([(0, 30), (324, 30), (326, 210), (648, 210), (650, 30)]);
        SetWaveStatus($"轴 {_editAxis}：已生成方波。");
    }

    private void Saw_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        ReplaceActiveWave([(0, 210), (648, 30), (650, 210)]);
        SetWaveStatus($"轴 {_editAxis}：已生成锯齿波。");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        _engine.EditWaves[_editAxis].Clear();
        _selAxis = null;
        _selX    = 0;
        _engine.MarkWaveDirty(_editAxis);
        // 这条轴已经没有波形了，跟着它一起记的导入来源也没用了：留着的话，
        // 下次启动会拿一份"孤儿来源"去比对（而且再次导入别的脚本时还会混进来）
        if (_sources.Remove(_editAxis)) SyncSourcesToDraft();
        Redraw();
    }

    private void Smooth_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.EditWaves[_editAxis].Count < 3)
        {
            SetWaveStatus($"轴 {_editAxis} 少于 3 个控制点，无法平滑（先加点或用波形按钮生成）。", warning: true);
            return;
        }
        var source = WaveScriptCodec.NormalizePoints(_engine.EditWaves[_editAxis]);
        if (source.Count < 3) return;
        PushUndo();
        for (int pass = 0; pass < 2; pass++)
        {
            var next = new List<(double X, double Y)>(source.Count) { source[0] };
            for (int i = 1; i < source.Count - 1; i++)
                next.Add((source[i].X, (source[i - 1].Y + source[i].Y * 2 + source[i + 1].Y) / 4));
            next.Add(source[^1]);
            source = next;
        }
        ReplaceActiveWave(source);
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        var source = WaveScriptCodec.NormalizePoints(_engine.EditWaves[_editAxis]);
        if (source.Count == 0)
        {
            SetWaveStatus($"轴 {_editAxis} 还没有波形，无法反向（先用波形按钮生成或导入脚本）。", warning: true);
            return;
        }
        PushUndo();
        ReplaceActiveWave(source.Select(point => (point.X, WaveScriptCodec.CanvasHeight - point.Y)));
    }

    private void ReplaceActiveWave(IEnumerable<(double X, double Y)> points)
    {
        var target = _engine.EditWaves[_editAxis];
        target.Clear();
        target.AddRange(WaveScriptCodec.NormalizePoints(points));
        _selAxis = null;   // 整条波形被替换，取消选中
        _selX    = 0;
        _engine.MarkWaveDirty(_editAxis);
        Redraw();
    }

    // ── Funscript 导入（修复：更新 CwCycleLen） ──────────────────────
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Funscript|*.funscript;*.json" };
        if (dlg.ShowDialog() != true) return;
        ImportFunscriptIntoWaveEditor(dlg.FileName);
    }

    /// <summary>
    /// 把 funscript / 多轴脚本集读进「微调波形」页的画布（编排页的「导入 funscript」也走这里）。
    /// P0-4：先全部解析到临时结构（失败不改内存）→ 列出会覆盖的轴并确认 → 每个受影响轴都 PushUndo → 应用。
    /// P0-5：导入结果就是唯一启用轴，其它轴一律关闭。
    /// </summary>
    /// <returns>成功 true；失败已弹窗提示并返回 false。</returns>
    private bool ImportFunscriptIntoWaveEditor(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024)
                throw new FormatException("脚本文件过大，最大支持 8 MB。");

            FunscriptTrackSet trackSet = FunscriptTrackLoader.LoadCompanionSet(path);

            // ── 画布整幅宽度 = 一圈 = canvasDurationMs ─────────────────────────────
            // 「微调波形」本质是**单循环波形**编辑器，一圈最长 60s（引擎 CwCycleLen 的 clamp）。
            // 以前的写法是：先按脚本原始时长把 120s/180s 铺进画布，再把周期 clamp 到 60s →
            // 导出时整份脚本被压成 60s（2 倍 / 3 倍速），用户完全看不出来。现在改成：
            // 铺画布和周期用**同一个**时长，超过 60s 就明确问用户要不要只截前 60 秒，绝不静默压缩。
            long canvasDurationMs = Math.Clamp(
                trackSet.DurationMs,
                (long)Math.Round(0.5 * 1000),                  // 下限同周期滑块（0.5s）
                (long)Math.Round(MaxCycleSeconds * 1000));     // 上限同引擎 CwCycleLen（60s）
            bool truncated = trackSet.DurationMs > canvasDurationMs;
            if (truncated &&
                System.Windows.MessageBox.Show(
                    $"这份脚本 {trackSet.DurationMs / 1000.0:0.#}s，而「微调波形」一次只能编辑一圈（最长 {MaxCycleSeconds:0}s）。\n\n" +
                    $"是 = 只取前 {MaxCycleSeconds:0} 秒继续微调；\n" +
                    "否 = 取消导入。\n\n" +
                    $"取前 {MaxCycleSeconds:0} 秒的话，后面 {trackSet.DurationMs / 1000.0 - MaxCycleSeconds:0.#}s 看不到，导出的也只有这 {MaxCycleSeconds:0} 秒。\n" +
                    "想编辑或播放完整脚本，请用「脚本库」页播放，或者在「编排」页把它们拼起来。",
                    "脚本比编辑器长", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                SetWaveStatus($"已取消导入：脚本 {trackSet.DurationMs / 1000.0:0.#}s 超过微调页上限 {MaxCycleSeconds:0}s。"
                    + "完整脚本请到「脚本库」页播放，或用「编排」页拼装。", warning: true);
                return false;
            }

            // P0-4：先把所有轴都转成画布点；任何一个轴转换失败都不会碰到 _engine.EditWaves
            var converted = new Dictionary<string, List<(double X, double Y)>>(StringComparer.Ordinal);
            // 原始动作点：导出时「没动过就原样写回」全靠它（画布 X 只有 650 格，走一趟就回不到原样）
            var sources = new Dictionary<string, WaveSource>(StringComparer.Ordinal);
            foreach ((string axisId, IReadOnlyList<WaveScriptCodec.ActionPoint> actions) in trackSet.Tracks)
            {
                if (!AxIds.Contains(axisId)) continue;
                List<WaveScriptCodec.ActionPoint> kept = WaveScriptCodec.TakeUpTo(actions, canvasDurationMs);
                if (kept.Count < 2) continue;   // 截断线之前不足两个动作的轴没法画成波形，跳过
                converted[axisId] = WaveScriptCodec.ToCanvasPoints(kept, canvasDurationMs);
                sources[axisId]   = new WaveSource(canvasDurationMs, trackSet.DurationMs, kept);
            }
            string[] affected = AxIds.Where(id => converted.ContainsKey(id)).ToArray();
            if (affected.Length == 0) throw new FormatException("脚本没有可用的六轴波形数据。");

            // P0-4：只对「已有数据、会被覆盖」的轴确认，列出具体轴名
            string[] overwritten = affected.Where(id => _engine.EditWaves[id].Count > 0).ToArray();
            if (overwritten.Length > 0 &&
                System.Windows.MessageBox.Show(
                    $"将覆盖 {string.Join("、", overwritten)} 的现有波形，继续？",
                    "导入 funscript", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;

            // P0-4：每个会被覆盖的轴都压一份撤销，导入后 Ctrl+Z 可以逐轴还原
            foreach (string id in affected) PushUndo(id);

            // P0-5：先全部关掉，再只打开导入到的轴，避免旧的启用轴和新波形一起驱动设备
            for (int i = 0; i < 6; i++) _engine.WfAxisEnabled[i] = false;
            foreach (string id in affected)
            {
                int axisIndex = Array.IndexOf(AxIds, id);
                if (axisIndex >= 0) _engine.WfAxisEnabled[axisIndex] = true;

                var pts = _engine.EditWaves[id];
                pts.Clear();
                pts.AddRange(converted[id]);
                _engine.MarkWaveDirty(id);
            }

            // 记下这次导入的原始动作点。没被这次导入覆盖的轴不动它们的来源 ——
            // 那些轴的画布波形也原样留着，来源当然还成立。
            foreach (KeyValuePair<string, WaveSource> pair in sources) _sources[pair.Key] = pair.Value;
            SyncSourcesToDraft();

            // 周期 = 画布整幅宽度代表的时间，正好等于导入时铺画布用的 canvasDurationMs，
            // 这样「画布 ↔ 一圈 ↔ 导出时间轴」三者永远一致（旧代码在这里把 180s 直接 clamp 成 60s）。
            double rawSeconds = trackSet.DurationMs / 1000.0;
            double newCycle   = canvasDurationMs / 1000.0;
            _selAxis = null;
            CycleSlider.Maximum = MaxCycleSeconds;
            _engine.CwCycleLen = newCycle;
            CycleSlider.Value  = newCycle;
            CycleLabel.Text    = newCycle.ToString("F1") + "s";

            BuildAxisEnablePanel();
            Redraw();

            // P1-10：导入成功/时长截断/启用轴都写独立状态行，不再被 Redraw 覆盖
            string detail = affected.Length == 1
                ? $"{affected[0]} {converted[affected[0]].Count} 点"
                : string.Join("、", affected.Select(id => $"{id} {converted[id].Count} 点"));
            SetWaveStatus(truncated
                ? $"已载入 {detail}（只取了前 {newCycle:0.#}s，原脚本 {rawSeconds:0.#}s）· 已启用 {string.Join("、", affected)}（其它轴已关闭）"
                : $"已载入 {detail} · 已启用 {string.Join("、", affected)}（其它轴已关闭）· 周期 {newCycle:0.###}s");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Funscript 导入失败", ex);
            System.Windows.MessageBox.Show($"导入失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetWaveStatus($"导入失败：{ex.Message}", warning: true);
            return false;
        }
    }

    /// <summary>
    /// 把页面里的导入来源同步给草稿存储（改动后必须调一次，否则退出时不会落盘）。
    /// 草稿是在 App.OnExit 里保存的、那里只传 4 个参数，所以来源只能通过
    /// <see cref="WaveDraftStore.Sources"/> 这个入口搭上同一份草稿文件。
    /// </summary>
    private void SyncSourcesToDraft() => WaveDraftStore.ReplaceSources(_sources);

    /// <summary>
    /// 画布下面那行**常显**小字：当前这一轴的脚本有多长、单循环上限多少、
    /// 是不是从脚本导入的（以及有没有被截掉一截）。
    ///
    /// 为什么常显而不是走状态行：状态行 6 秒后自动清空，而「这份脚本只装了前 60 秒」
    /// 恰恰是用户最需要一直看得见的一句话 —— 不然他会以为画布上就是整个脚本。
    /// 写短一点：这行跟时间轴提示共用一个会自动换行的 TextBlock，太长会把画布挤扁。
    /// </summary>
    private string SourceHintText()
    {
        if (!_sources.TryGetValue(_editAxis, out WaveSource? source) || source is null)
            return $"  ·  当前轴 {_editAxis} 是手画波形";

        double original = source.OriginalDurationMs / 1000.0;
        double canvas   = source.CanvasDurationMs / 1000.0;
        if (source.OriginalDurationMs > source.CanvasDurationMs + 1)
            return $"  ·  {_editAxis} 来自脚本：原脚本 {original:0.#}s，只装了前 {canvas:0.#}s（导出也只有这么多）";
        return $"  ·  {_editAxis} 来自脚本：原脚本 {original:0.#}s，整段都在画布上";
    }

    // ── Funscript 导出（单轴写用户选定的文件名；多轴每轴一个派生文件） ──
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        int    loops       = Math.Max(1, (int)Math.Round(LoopSlider.Value));
        double cycle       = _engine.CwCycleLen;
        double durationSec = cycle * loops;

        // P0-6③：先规范化并统计；点数 <2 的轴跳过并说明，不让它写出无法再导入的 funscript
        var normalized = new Dictionary<string, List<(double X, double Y)>>();
        var rawCounts  = new Dictionary<string, int>();
        foreach (string id in AxIds)
        {
            rawCounts[id]  = _engine.EditWaves[id].Count;
            normalized[id] = WaveScriptCodec.NormalizePoints(_engine.EditWaves[id]);
        }
        string[] usable = AxIds.Where(id => normalized[id].Count >= 2).ToArray();
        string[] tooFew = AxIds.Where(id => rawCounts[id] > 0 && normalized[id].Count < 2).ToArray();
        if (usable.Length == 0)
        {
            string detail = tooFew.Length > 0 ? $"\n点数不足的轴：{string.Join("、", tooFew)}" : "";
            System.Windows.MessageBox.Show(
                "没有可导出的波形数据：至少一个轴需要 ≥2 个控制点。" + detail,
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            SetWaveStatus("没有可导出的波形数据：至少一个轴需要 ≥2 个控制点。", warning: true);
            return;
        }

        // ── 导出前自检：待导出的时间轴和这份脚本本来的时长对不上就拦一次（默认不导出）──
        // 最典型的场景：180s 的脚本按用户选择只截了前 60 秒，直接导出就变成 3 倍速的废脚本。
        if (!ConfirmExportTimeline(cycle, loops, usable)) return;

        // 用 SaveFileDialog 确定文件名；单轴写这个文件，多轴用它做派生文件的基础名
        // 直接落在「脚本库」目录：原来没有 InitialDirectory、默认名 output.funscript，
        // 用户既不知道存到哪去了，也不会想到去脚本库找。
        ScriptLibrary.EnsureFolder();
        var sfd = new SaveFileDialog
        {
            Filter           = "Funscript|*.funscript",
            DefaultExt       = ".funscript",
            AddExtension     = true,
            InitialDirectory = ScriptLibrary.Folder,
            FileName         = $"wave-preset-{DateTime.Now:yyyyMMdd-HHmm}.funscript",
            Title            = usable.Length == 1
                ? $"导出 Funscript（单轴，时长 {durationSec:0.##}s / {loops} 圈）"
                : $"导出 Funscript（{usable.Length} 个轴，每轴一个文件，时长 {durationSec:0.##}s / {loops} 圈）"
        };
        if (sfd.ShowDialog() != true) return;

        // 每项：轴、实际写出的路径、总动作数、被合并的点数（含同 X/同毫秒/圈边界）
        var outputs = new List<(string Axis, string Path, int Actions, int Merged)>();
        // 多轴时可选写出的「社区通用命名」副本：与 outputs 共用同一份 actions，汇总时不再重复点数说明
        var communityOutputs = new List<(string Axis, string Path)>();
        try
        {
            if (usable.Length == 1)
            {
                // P0-6①：只有一个轴有数据时，写用户选定的文件名（不再自动加 .L0 后缀）
                string id = usable[0];
                var actions = BuildExportActions(id, normalized[id], cycle, loops);
                if (actions.Count == 0)
                    throw new FormatException($"{id} 没有可写出的动作。");

                File.WriteAllText(sfd.FileName, SerializeActions(actions));
                outputs.Add((id, sfd.FileName, actions.Count,
                    Math.Max(0, rawCounts[id] * loops - actions.Count)));
            }
            else
            {
                // P0-6②：多轴时先问要不要额外导出一份「社区通用命名」副本（MultiFunPlayer 等播放器用），
                //          再用 CompanionFilePath 算出全部要写的路径（Hexa 约定 N 个 + 选了「是」再 N 个），
                //          已存在的文件统一列出来，只弹一次覆盖确认。
                string basePath = Path.ChangeExtension(sfd.FileName, null);
                bool communityNaming = System.Windows.MessageBox.Show(
                    "是否同时导出一份「社区通用命名」副本？\n\n" +
                    "名称.stroke.funscript（L0）\n名称.surge.funscript（L1）\n名称.sway.funscript（L2）\n" +
                    "名称.twist.funscript（R0）\n名称.roll.funscript（R1）\n名称.pitch.funscript（R2）\n\n" +
                    "给 MultiFunPlayer 等支持多轴的播放器用；Hexa 两种命名都能读。",
                    "导出命名", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

                string[] plannedPaths = usable
                    .Select(id => FunscriptTrackLoader.CompanionFilePath(basePath, id, false))
                    .Concat(communityNaming
                        ? usable.Select(id => FunscriptTrackLoader.CompanionFilePath(basePath, id, true))
                        : Enumerable.Empty<string>())
                    .ToArray();
                string[] existing = plannedPaths.Where(path => File.Exists(path)).ToArray();
                if (existing.Length > 0 &&
                    System.Windows.MessageBox.Show(
                        $"以下文件已存在，会覆盖：\n{string.Join("\n", existing)}",
                        "覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                foreach (string id in usable)
                {
                    var actions = BuildExportActions(id, normalized[id], cycle, loops);
                    if (actions.Count == 0) continue;

                    string hexaPath = FunscriptTrackLoader.CompanionFilePath(basePath, id, false);
                    File.WriteAllText(hexaPath, SerializeActions(actions));
                    outputs.Add((id, hexaPath, actions.Count,
                        Math.Max(0, rawCounts[id] * loops - actions.Count)));

                    // 社区副本写同一份 actions（不重算），保证两份数据完全一致
                    if (communityNaming)
                    {
                        string communityPath = FunscriptTrackLoader.CompanionFilePath(basePath, id, true);
                        File.WriteAllText(communityPath, SerializeActions(actions));
                        communityOutputs.Add((id, communityPath));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Funscript 导出失败", ex);
            System.Windows.MessageBox.Show($"导出失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetWaveStatus($"导出失败：{ex.Message}", warning: true);
            return;
        }

        if (outputs.Count == 0)
        {
            System.Windows.MessageBox.Show("没有可导出的波形数据。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            SetWaveStatus("没有可导出的波形数据。", warning: true);
            return;
        }

        // P0-6④⑤：列出全部实际路径；有合并时明确写「N 个点 → M 个动作（K 个被合并）」
        string[] lines = outputs.Select(output =>
        {
            string line = $"{output.Axis}: {output.Path}";
            if (output.Merged > 0)
                line += $"（{rawCounts[output.Axis] * loops} 个点 → {output.Actions} 个动作，{output.Merged} 个被合并）";
            return line;
        }).Concat(communityOutputs.Select(copy => $"{copy.Axis}: {copy.Path}（社区命名副本）"))
          .ToArray();
        string skipNote = tooFew.Length > 0
            ? $"\n\n以下轴点数不足 2，已跳过：{string.Join("、", tooFew)}"
            : "";
        System.Windows.MessageBox.Show(
            $"已导出 {outputs.Count} 个轴（时长 {durationSec:0.##}s / {loops} 圈）：\n" +
            string.Join("\n", lines) + skipNote
            + "\n\n下一步：到「脚本库」页选中它，点「▶ 载入并播放」就能放"
            + "（社区命名副本是给 MultiFunPlayer 这类支持多轴的播放器用的）。",
            "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        int totalMerged = outputs.Sum(output => output.Merged);
        int totalFiles  = outputs.Count + communityOutputs.Count;   // 含社区命名副本
        SetWaveStatus($"导出完成：{totalFiles} 个文件"
            + (totalMerged > 0 ? $"，{totalMerged} 个点被合并（详见弹窗）" : "")
            + (tooFew.Length > 0 ? $"，跳过 {string.Join("、", tooFew)}" : ""));
    }

    /// <summary>
    /// 导出前自检：这份脚本导入时的**原始时长**和这次要导出的时间轴对不上（差 &gt; 1%）就先问一句。
    ///
    /// 什么情况会触发：
    ///   · 超过 60s 的脚本按用户选择只截了前 60 秒（180s → 60s，导出就等于 3 倍速）；
    ///   · 导入之后又把「周期」滑杆拉到了别的值（整条时间轴会被拉长/压短）。
    ///
    /// 为什么问句反过来写：WPF 的 MessageBox 没有 MB_DEFBUTTON2，默认焦点永远在第一个按钮上，
    /// 所以把「取消导出」放在默认键那一侧（是 = 取消导出，回车 = 安全的一侧；否 = 还是按新时长导出）。
    /// </summary>
    /// <returns>true = 可以继续导出。</returns>
    private bool ConfirmExportTimeline(double cycle, int loops, IEnumerable<string> axes)
    {
        var lines = new List<string>();
        foreach (string id in axes)
        {
            if (!_sources.TryGetValue(id, out WaveSource? source) || source is null) continue;
            long original = source.OriginalDurationMs;
            if (original <= 0) continue;

            // 只跟「一圈」比：圈数是用户明确要多导几遍（导出文件本来就该更长），不算时长对不上
            long exported = (long)Math.Round(cycle * 1000);
            if (Math.Abs(exported - original) <= Math.Max(10, original / 100)) continue;
            lines.Add($"{id}：原脚本 {original / 1000.0:0.#}s → 导出 {exported / 1000.0:0.#}s"
                + $"（{exported * 100.0 / original:0}%）");
        }
        if (lines.Count == 0) return true;

        string body = string.Join("\n", lines);
        bool cancel = System.Windows.MessageBox.Show(
            "导出的时间轴和原脚本对不上：\n\n" + body
            + (loops > 1 ? $"\n\n（这次导出 {loops} 圈，文件总长 {cycle * loops:0.#}s）" : "")
            + "\n\n时间轴一变，整段脚本的节奏就跟着变快或变慢，别人拿到的就不是原脚本了。\n"
            + "要取消导出吗？（推荐点「是」；确实想按上面的时长导出，点「否」）",
            "导出前检查", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!cancel)
        {
            AppLogger.Warn($"导出时长与原始脚本不一致，用户选择继续导出：{body.Replace("\n", "；")}");
            return true;
        }
        SetWaveStatus("已取消导出：导出的时间轴和原脚本对不上（详见弹窗）。", warning: true);
        return false;
    }

    /// <summary>
    /// 一个轴的导出动作点，按优先级三条路：
    ///   1) 画布点跟导入时**逐点一样**、周期也没改过 → 原样写回原始动作点
    ///      （时间和位置逐点一致，完全不过画布那层 650 格网格 —— 30s 脚本一格 ≈ 46ms）；
    ///   2) 改过点（拖动 / 增删）→ 用画布当前代表的一圈时长反算 X → At，但**没动过的点仍用原始时间**，
    ///      所以拖一个点不会让整条脚本的时间轴跟着变形；
    ///   3) 手画波形 / 没有来源 / 周期被改过 → 老算法（圈次 × 周期 + X / 画布宽度 × 周期）。
    /// </summary>
    private List<WaveScriptCodec.ActionPoint> BuildExportActions(
        string axisId, IReadOnlyList<(double X, double Y)> points, double cycleSeconds, int loops)
    {
        if (_sources.TryGetValue(axisId, out WaveSource? source) && source is not null
            && TimelineMatchesSource(source, cycleSeconds))
        {
            if (SameAsSourceCanvas(source, points))
                return RepeatSourceActions(source, loops);                  // 没动过：原样写回
            return BuildActions(points, cycleSeconds, loops, source);       // 动过：保住未编辑点的原始时间
        }
        return BuildActions(points, cycleSeconds, loops, null);
    }

    /// <summary>画布当前代表的一圈时长和导入时是否一致（差 &gt;1% 就认为用户改过周期，"原样写回"就说不通了）。</summary>
    private static bool TimelineMatchesSource(WaveSource source, double cycleSeconds)
    {
        long canvasMs = (long)Math.Round(cycleSeconds * 1000);
        return Math.Abs(canvasMs - source.CanvasDurationMs) <= Math.Max(10, source.CanvasDurationMs / 100);
    }

    /// <summary>
    /// 画布点是不是和导入时逐点一致（一个点都没拖过、没增删过）。
    ///
    /// 做法：拿原始动作点按导入时的同一套换算重新铺一遍，和当前画布点逐个比。
    /// 为什么不用「编辑时置个标志位」：改动入口太多（拖动、加点、删点、六个波形按钮、
    /// 撤销/重做、加载预设……），漏掉任何一个都会把改过的波形当成没改过、把用户的改动覆盖掉；
    /// 反过来（把没改过的当成改过）只是损失一点精度，不会丢数据。所以用这个"重新算一遍"的判断。
    /// 注意参数 points 必须是 NormalizePoints 过的（按 X 排序）。
    /// </summary>
    private static bool SameAsSourceCanvas(WaveSource source, IReadOnlyList<(double X, double Y)> points)
    {
        List<(double X, double Y)> expected = WaveScriptCodec.NormalizePoints(
            WaveScriptCodec.ToCanvasPoints(source.Actions, source.CanvasDurationMs));
        if (expected.Count != points.Count) return false;
        for (int i = 0; i < expected.Count; i++)
        {
            if (Math.Abs(expected[i].X - points[i].X) > 1e-6) return false;
            if (Math.Abs(expected[i].Y - points[i].Y) > 1e-6) return false;
        }
        return true;
    }

    /// <summary>
    /// 原样写回：圈数 1 时就是原始动作点本身；圈数 &gt;1 时按**原始时长**偏移复制
    /// （不用被 clamp 过的周期，否则圈与圈之间会挤进或漏掉一段时间）。
    /// </summary>
    private static List<WaveScriptCodec.ActionPoint> RepeatSourceActions(WaveSource source, int loops)
    {
        if (loops <= 1) return new List<WaveScriptCodec.ActionPoint>(source.Actions);

        var result = new List<WaveScriptCodec.ActionPoint>(source.Actions.Count * loops);
        for (int loop = 0; loop < loops; loop++)
        {
            long offset = loop * source.CanvasDurationMs;
            foreach (WaveScriptCodec.ActionPoint point in source.Actions)
                result.Add(new WaveScriptCodec.ActionPoint(offset + point.At, point.Pos));
        }
        return result;
    }

    // 画布点 → 真实时间戳：t = 圈次 × 周期 + X / 画布宽度 × 周期
    // 这样与 WaveScriptCodec.ToCanvasPoints（导入时 X = at / 总时长 × 画布宽度）互为逆运算
    private static List<WaveScriptCodec.ActionPoint> BuildActions(
        IReadOnlyList<(double X, double Y)> pts, double cycleSeconds, int loops, WaveSource? source = null)
    {
        // 有来源时：没动过的点按 X 找回它导入时的原始时间戳（X 本来就是从 At 算出来的，这份对照表是精确的），
        // 用它顶掉"X / 画布宽度 × 周期"的四舍五入结果 —— 时间轴就不会被 650 格网格整体推歪。
        // 对不上的 X = 用户新加/拖动过的点，还是用画布反算。
        Dictionary<double, long>? originalAtByX = null;
        if (source is not null)
        {
            originalAtByX = new Dictionary<double, long>(source.Actions.Count);
            foreach (WaveScriptCodec.ActionPoint point in source.Actions)
            {
                double x = Math.Round(point.At * (double)WaveScriptCodec.CanvasWidth / source.CanvasDurationMs, 6);
                originalAtByX[x] = point.At;
            }
        }

        var byTime = new SortedDictionary<long, WaveScriptCodec.ActionPoint>();
        for (int loop = 0; loop < loops; loop++)
        {
            long offset = (long)Math.Round(loop * cycleSeconds * 1000);
            foreach (var p in pts)
            {
                long at;
                if (originalAtByX is not null && originalAtByX.TryGetValue(Math.Round(p.X, 6), out long exactAt))
                    at = offset + exactAt;
                else
                    at = offset + (long)Math.Round(p.X / WaveScriptCodec.CanvasWidth * cycleSeconds * 1000);

                int  pos = (int)Math.Round(Math.Clamp(
                    100 - p.Y / WaveScriptCodec.CanvasHeight * 100, 0, 100));
                byTime[at] = new WaveScriptCodec.ActionPoint(at, pos);  // 同一时刻后写覆盖，与解析端一致
            }
        }
        return byTime.Values.ToList();
    }

    // 与 WaveScriptCodec.SerializeFunscript 输出同构：{"actions":[{"at","pos"}]}
    private static string SerializeActions(IReadOnlyList<WaveScriptCodec.ActionPoint> actions) =>
        JsonSerializer.Serialize(new { actions }, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    private void PlayCw_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.CustomRunning)
        {
            _engine.StopCustom();
            SetWaveStatus("波形试跑已停止。");
            SyncPlayState();
            return;
        }

        // P1-11：开头先记下脚本库播放器的状态；真正要开始试跑时才 Stop（没有可试跑轴就别打扰它）
        bool scriptBusy = App.FunscriptPlayer.IsPlaying || App.FunscriptPlayer.IsPaused;

        // P0-3：没有「已勾选且点数 ≥2」的轴时，明确提示并直接 return（不变按钮、不启动播放头）
        string[] usable = Enumerable.Range(0, 6)
            .Where(i => _engine.WfAxisEnabled[i] && _engine.EditWaves[AxIds[i]].Count >= 2)
            .Select(i => AxIds[i]).ToArray();
        if (usable.Length == 0)
        {
            SetWaveStatus("没有可试跑的轴：先勾选至少一个轴，并保证该轴有 ≥2 个控制点。", warning: true);
            return;
        }

        string[] weak = Enumerable.Range(0, 6)
            .Where(i => _engine.WfAxisEnabled[i] && _engine.EditWaves[AxIds[i]].Count < 2)
            .Select(i => AxIds[i]).ToArray();

        // 脚本库播放器还在播放/暂停时，先停掉它，避免波形停止后设备跳到 funscript 当前位置
        string scriptNote = "";
        if (scriptBusy)
        {
            App.FunscriptPlayer.Stop();
            scriptNote = "脚本库播放已先停止；";
        }

        if (!_engine.StartCustom())
        {
            // StartCustom 返回 false = 设备不可动（未连接 / 急停 / 被其它模式占用）。
            // 以前这里丢掉返回值，点播放毫无反应，用户只能对着不动的机器猜。
            SetWaveStatus(App.Serial.IsOpen
                ? "设备当前不可动（急停锁定中，或被其它模式占用），波形没有下发。"
                : "设备未连接：到「设置」页点「立即连接」（连接不上先点「刷新设备」看串口是否被占用）。",
                warning: true);
            SyncPlayState();
            return;
        }

        string running = $"波形试跑中：{string.Join("、", usable)}（圈数只影响导出；试跑会一直循环到手动停止）。";
        string weakNote = weak.Length > 0
            ? $"{string.Join("、", weak)} 点数不足，会保持中位 50；"
            : "";
        SetWaveStatus(scriptNote + weakNote + running, warning: weak.Length > 0 || scriptNote.Length > 0);
        SyncPlayState();
    }

    // ── 波形预设 保存 / 加载 ─────────────────────────────────────────
    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Filter   = "Hexa 波形预设|*.hwp",
            FileName = "wave_preset.hwp",
            Title    = "保存波形预设"
        };
        if (sfd.ShowDialog() != true) return;
        try
        {
            var preset = new WavePresetFile
            {
                Version   = 2,   // P0-2：>=2 表示文件里带 AxisEnabled，旧文件用点数回退
                CycleLen  = _engine.CwCycleLen,
                AxisEnabled = (bool[])_engine.WfAxisEnabled.Clone(),
                Waves    = AxIds.ToDictionary(
                    id  => id,
                    id  => _engine.EditWaves[id].Select(p => new double[] { p.X, p.Y }).ToList())
            };
            File.WriteAllText(sfd.FileName, JsonSerializer.Serialize(preset,
                new JsonSerializerOptions { WriteIndented = true }));
            int enabledCount = preset.AxisEnabled.Count(v => v);
            SetWaveStatus($"已保存波形预设：{sfd.FileName}（含 {enabledCount} 个启用轴）");
        }
        catch (Exception ex)
        {
            AppLogger.Error("波形预设保存失败", ex);
            System.Windows.MessageBox.Show($"保存失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetWaveStatus($"保存预设失败：{ex.Message}", warning: true);
        }
    }

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Hexa 波形预设|*.hwp;*.json" };
        if (dlg.ShowDialog() != true) return;

        // 加载会覆盖全部 6 条轴的手画波形，先确认（撤销只能逐轴还原，不是一键回退）
        if (AxIds.Any(id => _engine.EditWaves[id].Count > 0) &&
            System.Windows.MessageBox.Show(
                "加载预设会覆盖当前 6 条轴的波形（之后可用 Ctrl+Z 逐轴撤销），继续？",
                "加载波形预设", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            if (new FileInfo(dlg.FileName).Length > 8 * 1024 * 1024)
                throw new FormatException("预设文件过大，最大支持 8 MB。");
            var json   = File.ReadAllText(dlg.FileName);
            var preset = JsonSerializer.Deserialize<WavePresetFile>(json);
            if (preset?.Waves == null) throw new FormatException("预设格式无效。");

            // 每轴各自 PushUndo，使得加载后可以逐轴撤销（实际按 Undo 会还原当前轴）
            foreach (var id in AxIds) _undoStacks[id].Push(new List<(double X, double Y)>(_engine.EditWaves[id]));
            foreach (var id in AxIds) _redoStacks[id].Clear();
            UpdateWaveUndoButtons();

            foreach (var id in AxIds)
            {
                var pts = _engine.EditWaves[id]; pts.Clear();
                if (preset.Waves.TryGetValue(id, out var raw))
                {
                    if (raw.Count > WaveScriptCodec.MaxActions) throw new FormatException($"{id} 的控制点过多。");
                    pts.AddRange(WaveScriptCodec.NormalizePoints(raw
                        .Where(item => item is { Length: >= 2 })
                        .Select(item => (item[0], item[1]))));
                }
                _engine.MarkWaveDirty(id);
            }

            // 预设把六轴波形整体换掉了：导入来源跟着作废（否则导出时会拿一份早就不相干的
            // 原始时长来做自检、还会把预设里的点当成"没动过"）。
            _sources.Clear();
            SyncSourcesToDraft();

            // P0-2：恢复启用轴；旧预设没有 Version/AxisEnabled 时按「有 ≥2 个点的轴默认勾选」回退
            bool[] enabled = preset.Version >= 2 && preset.AxisEnabled is { Length: 6 }
                ? preset.AxisEnabled
                : AxIds.Select(id => _engine.EditWaves[id].Count >= 2).ToArray();
            for (int i = 0; i < 6; i++) _engine.WfAxisEnabled[i] = enabled[i];
            BuildAxisEnablePanel();

            // 引擎的 CwCycleLen 上限是 60s，预设里的超长周期同样按上限截断
            double newCycle = Math.Clamp(double.IsFinite(preset.CycleLen) ? preset.CycleLen : 2,
                0.5, MaxCycleSeconds);
            _selAxis = null;
            CycleSlider.Maximum = MaxCycleSeconds;
            _engine.CwCycleLen = newCycle;
            CycleSlider.Value  = newCycle;
            CycleLabel.Text    = newCycle.ToString("F1") + "s";

            Redraw();

            string[] enabledIds = AxIds.Where((_, i) => enabled[i]).ToArray();
            SetWaveStatus($"预设已加载：周期 {newCycle:0.###}s，启用轴 "
                + (enabledIds.Length > 0 ? string.Join("、", enabledIds) : "无（试跑不会动）"));
        }
        catch (Exception ex)
        {
            AppLogger.Error("波形预设加载失败", ex);
            System.Windows.MessageBox.Show($"加载失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetWaveStatus($"加载预设失败：{ex.Message}", warning: true);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  编排页（标签页 1）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>动作库当前快照（导入动作库后会换成新数组，所以每次都现取）。</summary>
    private static IReadOnlyList<StrokePreset> ComposePresets => StrokesViewModel.AllPresets;

    /// <summary>
    /// 上次建列表时动作库的指纹（见 <see cref="ComposeLibraryFingerprint"/>）。
    /// 页面被 MainWindow 缓存，只有指纹变了才重建列表 —— 每次切页都重建会闪烁并丢掉选中项。
    /// </summary>
    private string? _composeLibraryFingerprint;

    /// <summary>时间轴当前 BPM（10–240）。</summary>
    private int ComposeBpm => Math.Clamp(
        (int)Math.Round(ComposeBpmSlider.Value), ScriptComposerService.MinBpm, ScriptComposerService.MaxBpm);

    private static StrokePreset? FindPresetById(string? presetId) =>
        ScriptComposerService.FindPreset(ComposePresets, presetId);

    // ── 动作库列表 ────────────────────────────────────────────────────
    /// <summary>
    /// 重建左侧动作库列表（构造函数 / 搜索框变化 / 切回本页发现动作库变了时调用）。
    /// ItemsSource 一换，ListBox 的选中项就会被清掉，所以先记下选中动作的 Id，重建后按 Id 选回来
    /// （动作库是 copy-on-write 的，换成新数组后元素都是新实例，不能用引用比较）。
    /// 搜索关键字不用另存：它一直躺在 ComposeLibrarySearch 里，下面每次现读。
    /// </summary>
    private void BuildComposeLibrary()
    {
        IReadOnlyList<StrokePreset> presets = ComposePresets;
        string[] categories = StrokesViewModel.Categories;
        string filter = ComposeLibrarySearch?.Text?.Trim() ?? "";
        string? selectedId = (ComposeLibraryList.SelectedItem as StrokePreset)?.Id;

        StrokePreset[] visible = presets
            .Select((preset, index) => (preset, index))
            .Where(item => filter.Length == 0
                           || (item.preset.Label?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                           || (item.preset.Cat?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(item => CategoryRank(item.preset.Cat, categories))
            .ThenBy(item => item.index)
            .Select(item => item.preset)
            .ToArray();
        ComposeLibraryList.ItemsSource = visible;
        _composeLibraryFingerprint = ComposeLibraryFingerprint(presets);

        // 空状态：搜不到东西时列表只有一片空白，用户不知道是「没有这类动作」还是「没加载出来」
        if (ComposeLibraryEmptyLabel is not null)
        {
            ComposeLibraryEmptyLabel.Visibility = visible.Length == 0
                ? Visibility.Visible : Visibility.Collapsed;
            ComposeLibraryEmptyLabel.Text = presets.Count == 0
                ? "动作库是空的。到「动作」页新建几个动作再回来。"
                : $"没有叫「{filter}」的动作，换个词试试。";
        }

        if (string.IsNullOrEmpty(selectedId)) return;
        StrokePreset? restored = visible.FirstOrDefault(preset =>
            string.Equals(preset.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (restored is null) return;   // 该动作已被删除，或当前搜索关键字把它过滤掉了
        ComposeLibraryList.SelectedItem = restored;
        ComposeLibraryList.ScrollIntoView(restored);   // 重建会丢滚动位置，把选中项滚回可见区
    }

    /// <summary>
    /// 切回本页时调用：动作库内容真的变了才重建列表。
    /// 判断依据是 <see cref="ComposeLibraryFingerprint"/> 指纹，而不是「每次可见都重建」——
    /// 后者会让 ListBox 每次切页都重建，既闪烁又丢选中项。
    /// </summary>
    private void RefreshComposeLibraryIfChanged()
    {
        if (string.Equals(_composeLibraryFingerprint, ComposeLibraryFingerprint(ComposePresets),
                StringComparison.Ordinal)) return;
        BuildComposeLibrary();
    }

    /// <summary>
    /// 动作库指纹：元素个数 + 每条动作的 Id / 显示名 / 分类。
    /// 新建 / 导入 / 删除动作都走 copy-on-write（StrokesViewModel 整体换新数组），内容必变；
    /// 「同名原地编辑」只改分类或显示名时数组引用不变，靠内容部分也能抓到。
    /// </summary>
    private static string ComposeLibraryFingerprint(IReadOnlyList<StrokePreset> presets)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(presets.Count);
        foreach (StrokePreset preset in presets)
            builder.Append('\n')
                   .Append(preset.Id).Append('\u001f')
                   .Append(preset.Label).Append('\u001f')
                   .Append(preset.Cat);
        return builder.ToString();
    }

    private void ComposeLibrarySearch_TextChanged(object sender, TextChangedEventArgs e) =>
        BuildComposeLibrary();

    /// <summary>双击动作库条目：直接追加一段到时间轴末尾（不必拖拽）。</summary>
    private void ComposeLibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ComposeLibraryList.SelectedItem is StrokePreset preset) AppendComposeSegment(preset);
    }

    /// <summary>「＋ 添加」按钮：把当前选中的动作追加到末尾。</summary>
    private void ComposeLibraryAdd_Click(object sender, RoutedEventArgs e)
    {
        if (ComposeLibraryList.SelectedItem is StrokePreset preset)
        {
            AppendComposeSegment(preset);
        }
        else
        {
            // 非破坏性提示不弹模态框（与游玩页/手动页的内联原因条同一口径）：直接写在预览状态那一行。
            if (ComposePreviewLabel is not null)
                ComposePreviewLabel.Text = "先在左侧动作库里选一个动作，再点「＋ 添加」（或直接双击那个动作）。";
        }
    }

    /// <summary>把动作追加到时间轴末尾（拖拽/双击/按钮共用）。</summary>
    private void AppendComposeSegment(StrokePreset preset)
    {
        var segment = new ScriptSegment
        {
            PresetId          = preset.Id,
            Label             = preset.Label,
            DurationSeconds   = 2.0,
            Intensity         = 1.0,
            TransitionSeconds = 0.0,
        };
        segment.Normalize();
        PushComposeUndo("添加段");
        _compose.Segments.Add(segment);
        _selectedSegment = segment;
        _composeDirty    = true;
        ComposeStopPlayback();
        RefreshComposeUi();
    }

    private static int CategoryRank(string? category, string[] categories)
    {
        int rank = Array.IndexOf(categories, category ?? "");
        return rank < 0 ? int.MaxValue : rank;
    }

    // ══════════════════════════════════════════════════════════════════
    //  窄窗口下的三列宽度
    //
    //  窗口最小 880（左侧栏 200、页面留白 48）时正文只剩约 660px，而「动作库 200 + 段参数 240」
    //  会把中间那列 3D 预览挤到只剩 ~156px 的一条 —— 三个区域里最没用的就是它。
    //  正文不够宽时两列各收一点（180 / 220），预览能多拿 ~40px；窗口够宽时维持原样。
    //  做法与游玩页一致：在测量阶段按「可用宽度」定下来（自检等场合自己走 Measure/Arrange，
    //  不一定触发 SizeChanged，只看 SizeChanged 会漏掉）。
    // ══════════════════════════════════════════════════════════════════
    private const double ComposeWideLibraryColumn  = 200.0;
    private const double ComposeWideParamsColumn   = 240.0;
    private const double ComposeNarrowLibraryColumn = 180.0;
    private const double ComposeNarrowParamsColumn  = 220.0;
    /// <summary>页面宽度小于这个值就切到窄列宽（约等于窗口宽 960）。</summary>
    private const double ComposeNarrowPageWidth = 760.0;

    private bool _composeNarrowColumns;

    // 注意：Size 要写全名。本项目 UseWindowsForms + ImplicitUsings 会全局引入 System.Drawing，
    // 那里也有一个 Size，光写 Size 会和 System.Windows.Size 撞成 CS0104
    // （GlobalUsings.cs 里为 Point / Color / Button 打全局别名就是同一个原因）。
    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        if (ComposeLibraryColumn is not null && ComposeParamsColumn is not null
            && !double.IsInfinity(constraint.Width) && constraint.Width > 0)
        {
            ApplyComposeColumnWidths(constraint.Width < ComposeNarrowPageWidth);
        }
        return base.MeasureOverride(constraint);
    }

    private void ApplyComposeColumnWidths(bool narrow)
    {
        // 初值 false 与 XAML 里的 200 / 240 一致，所以第一次测量不会白改一次列宽（改列宽会再触发一轮测量）。
        if (narrow == _composeNarrowColumns) return;
        _composeNarrowColumns = narrow;
        ComposeLibraryColumn.Width = new GridLength(narrow ? ComposeNarrowLibraryColumn : ComposeWideLibraryColumn);
        ComposeParamsColumn.Width  = new GridLength(narrow ? ComposeNarrowParamsColumn : ComposeWideParamsColumn);
    }

    // ── 参数面板与 BPM 的事件接线（放在 InitializeComponent 之后，避免构造期事件乱序） ──
    private void WireComposeControls()
    {
        ComposeBpmSlider.ValueChanged += (_, e) =>
        {
            ComposeBpmLabel.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
            UpdateComposeTotals();
            if (!_composePlaying) UpdateComposePreview(_previewSeconds, false);
        };
        SegDurationSlider.ValueChanged   += SegDurationSlider_ValueChanged;
        SegIntensitySlider.ValueChanged  += SegIntensitySlider_ValueChanged;
        SegTransitionSlider.ValueChanged += SegTransitionSlider_ValueChanged;
        SegDurationBox.LostFocus += (_, _) => CommitDurationBox();
        SegDurationBox.KeyDown   += SegDurationBox_KeyDown;
        ComposeDriveCheck.Checked += (_, _) =>
        {
            App.Settings.ScriptDriveDevice = true;
            App.Settings.Save();
            if (!_composePlaying) UpdateComposePreview(_previewSeconds, false);
        };
        ComposeDriveCheck.Unchecked += (_, _) =>
        {
            App.Settings.ScriptDriveDevice = false;
            App.Settings.Save();
            // 取消勾选也要刷新那一行状态文字，否则它还停在「已勾选驱动设备」
            if (!_composePlaying) UpdateComposePreview(_previewSeconds, false);
        };
        ComposeDriveCheck.IsChecked = App.Settings.ScriptDriveDevice;   // 记住上次选择

        // Del 删除选中段（在输入框里按 Del 时不动它）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete || MainTabs.SelectedIndex != 0) return;
            if (e.OriginalSource is TextBox) return;
            if (_selectedSegment is null) return;
            RemoveSegment(_selectedSegment);
            e.Handled = true;
        };
    }

    // ── 拖拽：动作库 → 时间轴 ─────────────────────────────────────────
    private void Library_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _libraryDragStart  = e.GetPosition(ComposeLibraryList);
        _libraryDragPreset = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?
            .DataContext as StrokePreset;
    }

    private void Library_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _libraryDragPreset is null) return;
        Point now = e.GetPosition(ComposeLibraryList);
        if (Math.Abs(now.X - _libraryDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _libraryDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        StrokePreset preset = _libraryDragPreset;
        _libraryDragPreset = null;   // 只发起一次拖拽
        var data = new System.Windows.DataObject(LibraryDragFormat, preset.Id);
        System.Windows.DragDrop.DoDragDrop(ComposeLibraryList, data, System.Windows.DragDropEffects.Copy);
    }

    /// <summary>点击时间轴空白处：把播放头与预览定位到该时间点。</summary>
    private void TimelineHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement source && FindAncestor<System.Windows.Controls.Border>(source) != null
            && source is not Grid && source is not Canvas) return;   // 点卡片时不定位
        double x = e.GetPosition(SegmentPanel).X;
        double seconds = Math.Max(0, SecondsAtTimelineX(x));   // 与卡片宽度/标尺同一套换算
        ComposeStopPlayback();
        UpdatePlayhead(seconds);
        UpdateComposePreview(seconds, false);
        _playheadOffset = seconds;   // 从这里继续播时，墙钟要带上这个偏移
        _composeWatch.Restart();
    }

    private void Timeline_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(LibraryDragFormat)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        // 从动作库往时间轴上拖时，先把「会插到哪一格」标出来 ——
        // 原来只有鼠标旁边一个「+」，松手才知道插在哪儿。
        if (e.Data.GetDataPresent(LibraryDragFormat)) UpdateDropIndicator(e);
        else ShowReorderIndicator(false);
        e.Handled = true;
    }

    /// <summary>拖库条目悬停时的落点提示：复用排序用的那条 2px 竖线。</summary>
    private void UpdateDropIndicator(System.Windows.DragEventArgs e)
    {
        ShowReorderIndicator(true);
        if (_reorderIndicator is null) return;

        int index = InsertIndexAt(e.GetPosition(SegmentPanel));
        double x = ReorderIndicatorX(index);
        // SegmentPanel 与 TimelineOverlay 在同一个 Grid 单元格里，显式换算一次更稳
        Point? inOverlay = SegmentPanel.TranslatePoint(new Point(x, 0), TimelineOverlay);
        Canvas.SetLeft(_reorderIndicator, Math.Max(0, (inOverlay?.X ?? x) - 1));
    }

    private void Timeline_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // 鼠标在时间轴内部从一个子元素换到另一个，DragLeave 也会冒泡上来；
        // 只有真的离开这块区域才收掉落点提示，否则线条会一闪一闪。
        Point p = e.GetPosition(TimelineScroll);
        if (p.X < 0 || p.Y < 0 || p.X > TimelineScroll.ActualWidth || p.Y > TimelineScroll.ActualHeight)
            ShowReorderIndicator(false);
    }

    private void Timeline_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ShowReorderIndicator(false);   // 收掉 DragOver 期间画着的落点提示
        if (e.Data.GetData(LibraryDragFormat) is not string presetId) return;
        StrokePreset? preset = FindPresetById(presetId);
        if (preset is null) return;

        int index = InsertIndexAt(e.GetPosition(SegmentPanel));
        var segment = new ScriptSegment
        {
            PresetId          = preset.Id,
            Label             = preset.Label,
            DurationSeconds   = 2.0,
            Intensity         = 1.0,
            TransitionSeconds = 0.0,
        };
        segment.Normalize();
        PushComposeUndo("拖入段");
        _compose.Segments.Insert(index, segment);
        _selectedSegment = segment;
        _composeDirty    = true;
        RefreshComposeUi();
        e.Handled = true;
    }

    /// <summary>按落点 X 坐标算出插入到第几段之前（落在卡片左半边就插在它前面）。</summary>
    private int InsertIndexAt(Point positionInPanel)
    {
        for (int i = 0; i < SegmentPanel.Children.Count && i < _compose.Segments.Count; i++)
        {
            if (SegmentPanel.Children[i] is not FrameworkElement child) continue;
            Point topLeft = child.TranslatePoint(new Point(0, 0), SegmentPanel);
            // 卡片还没完成布局时用同一套换算兜底，缩放后落点判定不会错位
            double width = child.ActualWidth > 0
                ? child.ActualWidth
                : SegmentWidthFor(_compose.Segments[i].DurationSeconds);
            if (positionInPanel.X < topLeft.X + width / 2) return i;
        }
        return _compose.Segments.Count;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node switch
            {
                Visual => VisualTreeHelper.GetParent(node),
                System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
                _ => LogicalTreeHelper.GetParent(node),
            };
        }
        return null;
    }

    // ── 时间轴卡片 ────────────────────────────────────────────────────
    private FrameworkElement BuildSegmentCard(ScriptSegment segment, int index)
    {
        bool selected = ReferenceEquals(segment, _selectedSegment);
        StrokePreset? preset = FindPresetById(segment.PresetId);
        string label = preset?.Label ?? (string.IsNullOrWhiteSpace(segment.Label) ? segment.PresetId : segment.Label);
        string category = preset?.Cat ?? "?";

        var title = new TextBlock
        {
            Text = $"{index + 1}. {label}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var info = new TextBlock
        {
            Text = $"{segment.DurationSeconds:0.##}s · 强度 {segment.Intensity:0.00}",
            FontSize = 10,
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(0, 2, 0, 0),
            // 最短的段卡片只有 80px 宽（2s × 40px/秒），这两行不加省略号会被硬切在半个字上。
            // 完整数值在卡片 ToolTip 和右侧「段参数」里都有。
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var transition = new TextBlock
        {
            Text = $"过渡 {segment.TransitionSeconds:0.##}s",
            FontSize = 9,
            Foreground = (Brush)FindResource("Accent"),
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = segment.TransitionSeconds > 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        var content = new Grid();
        content.Children.Add(new StackPanel
        {
            Margin = new Thickness(6, 5, 8, 5),
            Children = { title, info, transition },
        });

        var card = new Border
        {
            Width = SegmentWidthFor(segment.DurationSeconds),
            MinHeight = 64,
            Margin = new Thickness(0, 0, CardGap, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = (Brush)FindResource(selected ? "Accent" : "Border"),
            Background = (Brush)FindResource(selected ? "AccentSoft" : "Surface"),
            Cursor = Cursors.Hand,
            Tag = segment,
            Child = content,
            ToolTip = $"{label}（{category}）· {segment.DurationSeconds:0.##}s · 强度 {segment.Intensity:0.00}",
        };
        card.MouseLeftButtonDown += SegmentCard_Click;
        // 拖拽排序：只在卡片主体按下左键时登记候选（右边缘 grip 交给 SegmentResize_DragDelta）
        card.PreviewMouseLeftButtonDown += SegmentCard_ReorderMouseDown;

        // 右边缘拖拽改时长：拖拽过程中只改宽度与文字，不重建卡片（重建会打断 Thumb 的鼠标捕获）
        var cardRef = new SegmentCardRef(segment, card, title, info, transition);
        var grip = new Thumb
        {
            Width = 8,   // 命中宽度：6px 太窄，贴着卡片边缘经常点不中（视觉线仍是 4px，见 HexaResizeThumb）
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeWE,
            Style = (Style)FindResource("HexaResizeThumb"),
            Tag = cardRef,
            ToolTip = "左右拖动改这一段的时长（当前时长和强度写在卡片上）",
        };
        grip.DragDelta     += SegmentResize_DragDelta;
        grip.DragCompleted += (_, _) => RefreshComposeUi();
        content.Children.Add(grip);

        var menu = new System.Windows.Controls.ContextMenu();
        var delete = new System.Windows.Controls.MenuItem { Header = "删除" };
        delete.Click += (_, _) => RemoveSegment(segment);
        var moveUp = new System.Windows.Controls.MenuItem { Header = "前移" };
        moveUp.Click += (_, _) => MoveSegment(segment, -1);
        var moveDown = new System.Windows.Controls.MenuItem { Header = "后移" };
        moveDown.Click += (_, _) => MoveSegment(segment, +1);
        var duplicate = new System.Windows.Controls.MenuItem { Header = "复制该段" };
        duplicate.Click += (_, _) => DuplicateSegment(segment);
        menu.Items.Add(duplicate);
        menu.Items.Add(delete);
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        card.ContextMenu = menu;

        return card;
    }

    private void SegmentCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ScriptSegment segment) return;
        _selectedSegment = segment;
        RefreshComposeUi();
        e.Handled = true;
    }

    // ── 段卡片拖拽排序 ────────────────────────────────────────────────
    // 触发方式：在卡片主体上按住左键移动超过系统拖拽阈值（SystemParameters.
    // MinimumHorizontal/VerticalDragDistance）即进入排序；只点一下不拖 = 原来的点击选中。
    // 右边缘 8px 的 grip（Thumb，Style=HexaResizeThumb，模板是整块透明 Grid，
    // 所以命中范围就是卡片内容区最右 8px × 全高）是改时长用的：命中它时本方法直接
    // return，让 Thumb 自己捕获鼠标走 SegmentResize_DragDelta，两种拖拽互不抢。
    /// <summary>卡片主体按下左键：登记拖拽候选并捕获鼠标（grip 区域不登记）。</summary>
    private void SegmentCard_ReorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ScriptSegment segment) return;
        if (FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null) return;   // 右边缘改时长手柄

        CancelSegmentReorder();                       // 清掉可能残留的上一次拖拽视觉
        _reorderCandidate  = segment;
        _reorderStartPoint = e.GetPosition(SegmentPanel);
        EnsureSegmentReorderHandlers();
        // 捕获挂在 SegmentPanel 上：点击选中会 RefreshComposeUi() 重建所有卡片，
        // 捕获若挂在卡片上会随卡片一起消失；SegmentPanel 本身不会被重建。
        SegmentPanel.CaptureMouse();
    }

    /// <summary>SegmentPanel 上的鼠标/捕获处理器只挂一次（构造函数不在本补丁的改动范围内）。</summary>
    private void EnsureSegmentReorderHandlers()
    {
        if (_reorderHandlersHooked) return;
        _reorderHandlersHooked = true;
        SegmentPanel.MouseMove         += SegmentPanel_ReorderMouseMove;
        SegmentPanel.MouseLeftButtonUp += SegmentPanel_ReorderMouseLeftButtonUp;
        SegmentPanel.LostMouseCapture  += (_, _) => CancelSegmentReorder();
    }

    private void SegmentPanel_ReorderMouseMove(object sender, MouseEventArgs e)
    {
        if (_reorderCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        Point now = e.GetPosition(SegmentPanel);

        if (!_reordering)
        {
            if (Math.Abs(now.X - _reorderStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - _reorderStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _reordering        = true;
            _reorderingSegment = _reorderCandidate;
            _reorderingCard    = FindSegmentCard(_reorderingSegment);
            if (_reorderingCard is not null) _reorderingCard.Opacity = SegmentDragOpacity;
            ShowReorderIndicator(true);
        }

        UpdateReorderIndicator(now);
    }

    private void SegmentPanel_ReorderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ScriptSegment? moved = _reordering ? _reorderingSegment : null;
        int insertBefore = moved is null ? -1 : InsertIndexAt(e.GetPosition(SegmentPanel));

        CancelSegmentReorder();          // 先还原半透明/收起指示条（卡片可能随后被重建）
        SegmentPanel.ReleaseMouseCapture();

        if (moved is null) return;       // 只是点了一下：选中已由 SegmentCard_Click 处理
        ApplySegmentReorder(moved, insertBefore);
        e.Handled = true;
    }

    /// <summary>把段移动到 insertBefore 指定的位置（InsertIndexAt 的语义：插到第几段之前）。</summary>
    private void ApplySegmentReorder(ScriptSegment segment, int insertBefore)
    {
        int from = _compose.Segments.IndexOf(segment);
        if (from < 0 || insertBefore < 0) return;

        int to = insertBefore > from ? insertBefore - 1 : insertBefore;   // RemoveAt 之后后面的索引会左移
        to = Math.Clamp(to, 0, _compose.Segments.Count - 1);
        if (to == from) return;                                          // 位置没变，不必弄脏合成

        PushComposeUndo("拖拽排序");
        _compose.Segments.RemoveAt(from);
        _compose.Segments.Insert(to, segment);
        _selectedSegment = segment;          // 保持选中被拖动的段
        _composeDirty    = true;
        ComposeStopPlayback();
        RefreshComposeUi();
    }

    private Border? FindSegmentCard(ScriptSegment segment)
    {
        foreach (UIElement child in SegmentPanel.Children)
            if (child is Border border && ReferenceEquals(border.Tag, segment)) return border;
        return null;
    }

    /// <summary>插入指示条：一条 2px 竖线，塞进已存在的 TimelineOverlay（它在卡片层之上）。</summary>
    private void ShowReorderIndicator(bool show)
    {
        if (_reorderIndicator is null)
        {
            _reorderIndicator = new Border
            {
                Width = 2,
                Height = 64,
                CornerRadius = new CornerRadius(1),
                Background = (Brush)FindResource("Accent"),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            TimelineOverlay.Children.Add(_reorderIndicator);
            Canvas.SetTop(_reorderIndicator, 2);
        }
        _reorderIndicator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateReorderIndicator(Point positionInPanel)
    {
        if (_reorderIndicator is null || _reorderingSegment is null) return;

        // 万一拖拽途中面板被重建（例如播放停止刷新），重新抓一次卡片并恢复半透明
        if (_reorderingCard is null || !SegmentPanel.Children.Contains(_reorderingCard))
        {
            _reorderingCard = FindSegmentCard(_reorderingSegment);
            if (_reorderingCard is not null) _reorderingCard.Opacity = SegmentDragOpacity;
        }
        if (_reorderingCard is not null)
            _reorderIndicator.Height = Math.Max(24, _reorderingCard.ActualHeight);

        int index = InsertIndexAt(positionInPanel);
        double x = ReorderIndicatorX(index);

        // SegmentPanel 与 TimelineOverlay 在同一个 Grid 单元格里，这里显式换算一次更稳
        Point? inOverlay = SegmentPanel.TranslatePoint(new Point(x, 0), TimelineOverlay);
        Canvas.SetLeft(_reorderIndicator, Math.Max(0, (inOverlay?.X ?? x) - 1));
    }

    /// <summary>插入指示条应画在的 X 坐标（SegmentPanel 坐标系）。</summary>
    private double ReorderIndicatorX(int index)
    {
        int count = SegmentPanel.Children.Count;
        if (count == 0) return 0;

        if (index >= count)
        {
            if (SegmentPanel.Children[count - 1] is FrameworkElement last)
            {
                Point? tail = last.TranslatePoint(new Point(last.ActualWidth, 0), SegmentPanel);
                return (tail?.X ?? 0) + CardGap / 2;
            }
            return 0;
        }

        if (SegmentPanel.Children[index] is FrameworkElement child)
        {
            Point? head = child.TranslatePoint(new Point(0, 0), SegmentPanel);
            return (head?.X ?? 0) - CardGap / 2;
        }
        return 0;
    }

    private void CancelSegmentReorder()
    {
        if (_reorderingCard is not null) _reorderingCard.Opacity = 1.0;
        if (_reorderIndicator is not null) _reorderIndicator.Visibility = Visibility.Collapsed;
        _reorderCandidate  = null;
        _reordering        = false;
        _reorderingSegment = null;
        _reorderingCard    = null;
    }

    private void SegmentResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not SegmentCardRef cardRef) return;
        ScriptSegment segment = cardRef.Segment;
        PushComposeUndoCoalesced("拖拽段时长");
        double deltaSeconds = e.HorizontalChange / PxPerSecond;
        segment.DurationSeconds = Math.Clamp(
            segment.DurationSeconds + deltaSeconds,
            ScriptSegment.MinDurationSeconds, ScriptSegment.MaxDurationSeconds);
        if (segment.TransitionSeconds > segment.DurationSeconds)
            segment.TransitionSeconds = segment.DurationSeconds;

        _selectedSegment = segment;
        _composeDirty = true;
        UpdateSegmentCardTexts(cardRef);
        DrawTimelineRuler();
        UpdateComposeTotals();
        UpdateSegmentPanel();      // 同步右侧滑块（有 _syncingSegmentPanel 保护，不会回写）
        UpdatePlayhead();
    }

    private void UpdateSegmentCardTexts(SegmentCardRef cardRef)
    {
        ScriptSegment segment = cardRef.Segment;
        StrokePreset? preset = FindPresetById(segment.PresetId);
        string label = preset?.Label ?? (string.IsNullOrWhiteSpace(segment.Label) ? segment.PresetId : segment.Label);
        int index = _compose.Segments.IndexOf(segment);

        cardRef.Card.Width = SegmentWidthFor(segment.DurationSeconds);
        cardRef.Title.Text = $"{(index < 0 ? 0 : index + 1)}. {label}";
        cardRef.Info.Text  = $"{segment.DurationSeconds:0.##}s · 强度 {segment.Intensity:0.00}";
        cardRef.Transition.Text = $"过渡 {segment.TransitionSeconds:0.##}s";
        cardRef.Transition.Visibility =
            segment.TransitionSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 段的增删移动 ──────────────────────────────────────────────────
    private void RemoveSegment(ScriptSegment segment)
    {
        int index = _compose.Segments.IndexOf(segment);
        if (index < 0) return;
        PushComposeUndo("删除段");
        _compose.Segments.RemoveAt(index);
        if (ReferenceEquals(_selectedSegment, segment))
        {
            _selectedSegment = _compose.Segments.Count == 0
                ? null
                : _compose.Segments[Math.Min(index, _compose.Segments.Count - 1)];
        }
        _composeDirty = true;
        RefreshComposeUi();
    }

    /// <summary>复制一段到它后面（同名动作+同参数，重复编排时不用重新拖和调）。</summary>
    private void DuplicateSegment(ScriptSegment segment)
    {
        int index = _compose.Segments.IndexOf(segment);
        if (index < 0) return;
        ScriptSegment copy = segment.Clone();
        PushComposeUndo("复制段");
        _compose.Segments.Insert(index + 1, copy);
        _selectedSegment = copy;
        _composeDirty = true;
        ComposeStopPlayback();
        RefreshComposeUi();
    }

    private void MoveSegment(ScriptSegment segment, int delta)
    {
        int index = _compose.Segments.IndexOf(segment);
        if (index < 0) return;
        int target = index + delta;
        if (target < 0 || target >= _compose.Segments.Count) return;

        PushComposeUndo(delta < 0 ? "前移段" : "后移段");
        ScriptSegment temp = _compose.Segments[index];
        _compose.Segments[index] = _compose.Segments[target];
        _compose.Segments[target] = temp;

        _selectedSegment = segment;
        _composeDirty = true;
        RefreshComposeUi();
    }

    private void SegmentDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSegment is not null) RemoveSegment(_selectedSegment);
    }

    // ── 编排页撤销 / 重做（独立栈，不复用微调波形那套 _undoStacks） ─────
    private void ComposeUndo_Click(object sender, RoutedEventArgs e) => ComposeUndo();
    private void ComposeRedo_Click(object sender, RoutedEventArgs e) => ComposeRedo();

    /// <summary>
    /// 离散改动（添加 / 删除 / 复制 / 移动）在修改之前调用：把当前整个合成深拷贝压入撤销栈，并清空重做栈。
    /// 注意：本类已有微调波形用的 PushUndo(string axisId)，同名同签名无法重载（编译报错），
    /// 所以编排栈统一用 PushComposeUndo* 前缀。
    /// </summary>
    private void PushComposeUndo(string label)
    {
        _composeUndoCoalesceLabel = null;   // 离散操作打断连续合并窗口
        PushComposeUndoCore(label);
    }

    /// <summary>
    /// 连续型改动（拖段时长 / 强度 / 过渡滑块、拖卡片右边缘）专用：
    /// 同一个 label 的连续事件间隔小于 300ms 只压一次栈，直到用户停手超过 300ms 才允许压下一个。
    /// 这样一次拖拽 = 一个撤销步，而不是几百个。
    /// </summary>
    private void PushComposeUndoCoalesced(string label)
    {
        if (_restoringComposeUndo) return;
        long now = Environment.TickCount64;
        if (label == _composeUndoCoalesceLabel && now - _composeUndoCoalesceAt < ComposeUndoCoalesceMs)
        {
            _composeUndoCoalesceAt = now;   // 还在同一次拖拽里：只把窗口往后推，不压栈
            return;
        }
        _composeUndoCoalesceLabel = label;
        _composeUndoCoalesceAt    = now;
        PushComposeUndoCore(label);
    }

    /// <summary>
    /// 段时长输入框专用（回车和失焦都会提交一次）：值没变就不压栈，
    /// 避免「回车一次 + 随后失焦一次」压出一个按下去毫无效果的空撤销步。
    /// </summary>
    private void PushComposeUndoForDurationBox(ScriptSegment segment)
    {
        string text = SegDurationBox.Text?.Trim() ?? "";
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                      double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (!parsed || !double.IsFinite(value)) return;   // 解析失败 = 不会改任何东西
        double clamped = Math.Clamp(value, ScriptSegment.MinDurationSeconds, ScriptSegment.MaxDurationSeconds);
        if (Math.Abs(clamped - segment.DurationSeconds) < 0.0001) return;
        PushComposeUndo("段时长(输入)");
    }

    private void PushComposeUndoCore(string label)
    {
        if (_restoringComposeUndo) return;
        _composeUndoStack.Add(CaptureComposeSnapshot(label));
        if (_composeUndoStack.Count > MaxComposeUndoSteps) _composeUndoStack.RemoveAt(0);
        _composeRedoStack.Clear();
        UpdateComposeUndoButtons();
    }

    /// <summary>当前合成 + 选中段的深拷贝快照（段逐个 Clone，之后改段参数不会污染栈里的旧状态）。</summary>
    private ComposeUndoSnapshot CaptureComposeSnapshot(string label)
    {
        var copy = new ScriptComposition
        {
            Name     = _compose.Name,
            Bpm      = ComposeBpm,
            Segments = _compose.Segments.Select(segment => segment.Clone()).ToList(),
        };
        int selectedIndex = _selectedSegment is null ? -1 : _compose.Segments.IndexOf(_selectedSegment);
        return new ComposeUndoSnapshot(copy, selectedIndex, label);
    }

    /// <summary>把快照恢复成当前合成（恢复时再深拷贝一次，栈里的快照保持不变，重做才有东西可用）。</summary>
    private void ApplyComposeSnapshot(ComposeUndoSnapshot snapshot)
    {
        // 撤销会重建时间轴卡片；如果焦点正好在被重建的卡片上（比如刚拖完右边缘手柄），
        // 焦点会被踢出本页，连续按 Ctrl+Z 就失效了 —— 这里补回本页内。
        bool hadKeyboardFocus = IsKeyboardFocusWithin;
        _restoringComposeUndo = true;
        try
        {
            _compose = new ScriptComposition
            {
                Name     = snapshot.Compose.Name,
                Bpm      = snapshot.Compose.Bpm,
                Segments = snapshot.Compose.Segments.Select(segment => segment.Clone()).ToList(),
            };
            ComposeStopPlayback();
            ComposeBpmSlider.Value = Math.Clamp(
                snapshot.Compose.Bpm, ScriptComposerService.MinBpm, ScriptComposerService.MaxBpm);
            // 选中段按快照里的下标恢复；越界（那段已被删掉）就当作没选中（等价于下标 -1）
            _selectedSegment = snapshot.SelectedIndex >= 0 && snapshot.SelectedIndex < _compose.Segments.Count
                ? _compose.Segments[snapshot.SelectedIndex]
                : null;
            _composeDirty = true;
            RefreshComposeUi();
        }
        finally
        {
            _restoringComposeUndo = false;
        }
        if (hadKeyboardFocus && !IsKeyboardFocusWithin) MainTabs.Focus();
        UpdateComposeUndoButtons();
    }

    /// <summary>撤销一步：当前状态进重做栈，弹出撤销栈顶恢复。</summary>
    private void ComposeUndo()
    {
        if (_composeUndoStack.Count == 0) return;
        int last = _composeUndoStack.Count - 1;
        ComposeUndoSnapshot snapshot = _composeUndoStack[last];
        _composeUndoStack.RemoveAt(last);
        _composeRedoStack.Add(CaptureComposeSnapshot(snapshot.Label));
        if (_composeRedoStack.Count > MaxComposeUndoSteps) _composeRedoStack.RemoveAt(0);
        _composeUndoCoalesceLabel = null;
        ApplyComposeSnapshot(snapshot);
    }

    /// <summary>重做一步：当前状态进撤销栈，弹出重做栈顶恢复。</summary>
    private void ComposeRedo()
    {
        if (_composeRedoStack.Count == 0) return;
        int last = _composeRedoStack.Count - 1;
        ComposeUndoSnapshot snapshot = _composeRedoStack[last];
        _composeRedoStack.RemoveAt(last);
        _composeUndoStack.Add(CaptureComposeSnapshot(snapshot.Label));
        if (_composeUndoStack.Count > MaxComposeUndoSteps) _composeUndoStack.RemoveAt(0);
        _composeUndoCoalesceLabel = null;
        ApplyComposeSnapshot(snapshot);
    }

    /// <summary>清空编排撤销 / 重做历史（新建 / 打开其它合成时调用，避免 Ctrl+Z 把旧合成撤回来）。</summary>
    private void ClearComposeUndoHistory()
    {
        _composeUndoStack.Clear();
        _composeRedoStack.Clear();
        _composeUndoCoalesceLabel = null;
        UpdateComposeUndoButtons();
    }

    /// <summary>工具栏两个按钮的可用状态与提示（栈空时禁用）。</summary>
    private void UpdateComposeUndoButtons()
    {
        if (ComposeUndoBtn is null || ComposeRedoBtn is null) return;   // 构造期 InitializeComponent 之前被调用时兜底
        ComposeUndoBtn.IsEnabled = _composeUndoStack.Count > 0;
        ComposeRedoBtn.IsEnabled = _composeRedoStack.Count > 0;
        ComposeUndoBtn.ToolTip = _composeUndoStack.Count > 0
            ? $"撤销：{_composeUndoStack[_composeUndoStack.Count - 1].Label}（Ctrl+Z）"
            : "撤销（Ctrl+Z）";
        ComposeRedoBtn.ToolTip = _composeRedoStack.Count > 0
            ? $"重做：{_composeRedoStack[_composeRedoStack.Count - 1].Label}（Ctrl+Y）"
            : "重做（Ctrl+Y）";
    }

    /// <summary>
    /// Ctrl+Z / Ctrl+Y：只在「编排」标签页且本页可见时生效；微调波形页仍走页面自己的
    /// Undo_Executed / Redo_Executed（它们在 SelectedIndex == 0 时直接 return）。
    /// 焦点在输入框里时让给文本框自身的文字撤销，不抢。
    /// </summary>
    private void ComposeUndo_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (MainTabs.SelectedIndex != 0 || !IsVisible) return;
        // 注意：本项目隐式 using 了 System.Windows.Forms，KeyEventArgs / TextBoxBase 都有同名类型，
        // 必须写全名，否则 CS0104 二义性编译错误。
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        if (e.Key == Key.Z)
        {
            ComposeUndo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            ComposeRedo();
            e.Handled = true;
        }
    }


    // ── 右侧参数面板 ──────────────────────────────────────────────────
    private void UpdateSegmentPanel()
    {
        _syncingSegmentPanel = true;
        try
        {
            ScriptSegment? segment = _selectedSegment;
            bool has = segment is not null && _compose.Segments.Contains(segment);
            SegParamEmptyLabel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            SegParamBody.Visibility       = has ? Visibility.Visible : Visibility.Collapsed;
            SegDeleteBtn.IsEnabled        = has;
            if (!has) return;

            StrokePreset? preset = FindPresetById(segment!.PresetId);
            SegNameLabel.Text   = preset?.Label ?? segment.Label;
            // 动作还在时没必要把内部 Id 念给用户听（卡片上已经有名字了），只说分类；
            // 动作被删掉时才把 Id 露出来，方便用户回「动作」页找是哪一条。
            SegPresetLabel.Text = preset is null
                ? $"动作「{segment.PresetId}」在动作库里已经没有了，导出时会跳过这一段。"
                : $"类别：{preset.Cat}";

            SegDurationSlider.Value = segment.DurationSeconds;
            SegDurationBox.Text     = segment.DurationSeconds.ToString("0.##");
            SegIntensitySlider.Value = segment.Intensity;
            SegIntensityLabel.Text   = segment.Intensity.ToString("0.00");
            SegTransitionSlider.Maximum = Math.Max(
                ScriptSegment.MinTransitionSeconds, Math.Min(ScriptSegment.MaxTransitionSeconds, segment.DurationSeconds));
            SegTransitionSlider.Value   = segment.TransitionSeconds;
            SegTransitionLabel.Text     = segment.TransitionSeconds.ToString("0.00") + " s";
        }
        finally
        {
            _syncingSegmentPanel = false;
        }
    }

    private void SegDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSegmentPanel) return;
        ScriptSegment? segment = _selectedSegment;
        if (segment is null) return;

        PushComposeUndoCoalesced("段时长");
        segment.DurationSeconds = Math.Clamp(
            e.NewValue, ScriptSegment.MinDurationSeconds, ScriptSegment.MaxDurationSeconds);
        if (segment.TransitionSeconds > segment.DurationSeconds)
        {
            segment.TransitionSeconds = segment.DurationSeconds;
            _syncingSegmentPanel = true;
            SegTransitionSlider.Maximum = Math.Max(
                ScriptSegment.MinTransitionSeconds, Math.Min(ScriptSegment.MaxTransitionSeconds, segment.DurationSeconds));
            SegTransitionSlider.Value   = segment.TransitionSeconds;
            SegTransitionLabel.Text     = segment.TransitionSeconds.ToString("0.00") + " s";
            _syncingSegmentPanel = false;
        }
        SegDurationBox.Text = segment.DurationSeconds.ToString("0.##");
        _composeDirty = true;
        RefreshSegmentCard(segment);
    }

    private void SegIntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSegmentPanel) return;
        ScriptSegment? segment = _selectedSegment;
        if (segment is null) return;

        PushComposeUndoCoalesced("段强度");
        segment.Intensity = Math.Clamp(e.NewValue, ScriptSegment.MinIntensity, ScriptSegment.MaxIntensity);
        SegIntensityLabel.Text = segment.Intensity.ToString("0.00");
        _composeDirty = true;
        RefreshSegmentCard(segment);
    }

    private void SegTransitionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSegmentPanel) return;
        ScriptSegment? segment = _selectedSegment;
        if (segment is null) return;

        PushComposeUndoCoalesced("段过渡");
        double maximum = Math.Min(ScriptSegment.MaxTransitionSeconds, segment.DurationSeconds);
        segment.TransitionSeconds = Math.Clamp(
            e.NewValue, ScriptSegment.MinTransitionSeconds, Math.Max(ScriptSegment.MinTransitionSeconds, maximum));
        SegTransitionLabel.Text = segment.TransitionSeconds.ToString("0.00") + " s";
        _composeDirty = true;
        RefreshSegmentCard(segment);
    }

    private void SegDurationBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitDurationBox();
        e.Handled = true;
    }

    private void CommitDurationBox()
    {
        ScriptSegment? segment = _selectedSegment;
        if (segment is null) return;

        PushComposeUndoForDurationBox(segment);
        string text = SegDurationBox.Text?.Trim() ?? "";
        bool parsedOk = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) ||
                        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        // double.TryParse 会接受 "NaN"/"Infinity"，夹取后仍是 NaN，会把总时长/卡片宽度/导出全搞坏
        if (parsedOk && double.IsFinite(parsed))
        {
            segment.DurationSeconds = Math.Clamp(
                parsed, ScriptSegment.MinDurationSeconds, ScriptSegment.MaxDurationSeconds);
            if (segment.TransitionSeconds > segment.DurationSeconds)
                segment.TransitionSeconds = segment.DurationSeconds;
        }

        _syncingSegmentPanel = true;
        SegDurationSlider.Value = segment.DurationSeconds;
        SegTransitionSlider.Maximum = Math.Max(
            ScriptSegment.MinTransitionSeconds, Math.Min(ScriptSegment.MaxTransitionSeconds, segment.DurationSeconds));
        SegTransitionSlider.Value = segment.TransitionSeconds;
        SegTransitionLabel.Text   = segment.TransitionSeconds.ToString("0.00") + " s";
        SegDurationBox.Text       = segment.DurationSeconds.ToString("0.##");
        _syncingSegmentPanel = false;

        _composeDirty = true;
        RefreshSegmentCard(segment);

        // 非法输入以前是静默回填旧值，用户以为改成功了 —— 这里明确说清楚
        if (!parsedOk || !double.IsFinite(parsed))
        {
            ComposeHintLabel.Foreground = (Brush)FindResource("Warning");
            ComposeHintLabel.Text = $"「{text}」不是有效数字，该段时长仍为 {segment.DurationSeconds:0.##}s。";
        }
    }

    /// <summary>只重建某一段的卡片（滑块连续拖动时用，避免整条时间轴重建）。</summary>
    private void RefreshSegmentCard(ScriptSegment segment)
    {
        int index = _compose.Segments.IndexOf(segment);
        if (index >= 0 && index < SegmentPanel.Children.Count)
            SegmentPanel.Children[index] = BuildSegmentCard(segment, index);

        DrawTimelineRuler();
        UpdateComposeTotals();
        UpdatePlayhead();
        if (!_composePlaying) UpdateComposePreview(_previewSeconds, false);
    }

    // ── 整体刷新 ──────────────────────────────────────────────────────
    private void RefreshComposeUi()
    {
        SegmentPanel.Children.Clear();
        for (int i = 0; i < _compose.Segments.Count; i++)
            SegmentPanel.Children.Add(BuildSegmentCard(_compose.Segments[i], i));

        DrawTimelineRuler();
        UpdateComposeTotals();
        UpdateSegmentPanel();
        UpdatePlayhead();
        if (!_composePlaying) UpdateComposePreview(_previewSeconds, false);
    }

    private void UpdateComposeTotals()
    {
        (int resolvable, double total) = ScriptComposerService.Estimate(_compose, ComposePresets, ComposeBpm);
        ComposeNameLabel.Text = _composeDirty ? $"合成：{_compose.Name} *" : $"合成：{_compose.Name}";
        ComposeTotalLabel.Text = $"总时长 {total:0.##}s · {resolvable} 段";

        // 空状态只写在时间轴正中间那一句（TimelineEmptyLabel）：底部这行再说一遍就是重复。
        bool empty = _compose.Segments.Count == 0;
        if (TimelineEmptyLabel is not null)
            TimelineEmptyLabel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            ComposeHintLabel.Text = "";
            return;
        }

        int missing = _compose.Segments.Count - resolvable;
        ComposeHintLabel.Foreground = (Brush)FindResource("Muted");
        // 原来这里还写着「每段宽度 = 时长 × 40px/秒（最小 80px）」——缩放控件和标尺上已经能直接看到，
        // 属于重复信息，去掉；留下的是用户不会从界面上看出来的那件事：BPM 决定一圈多长。
        ComposeHintLabel.Text = missing > 0
            ? $"有 {missing} 段的动作在动作库里已不存在，导出时会跳过这些段（时间轴仍保留其时长）。"
            : $"BPM {ComposeBpm} = 每 {ScriptComposerService.CycleSeconds(ComposeBpm):0.##} 秒走完一圈动作；试听和导出都用这个速度。";
    }

    // ── 时间轴缩放（px/秒） ───────────────────────────────────────────
    // 注意：ScriptComposerService.PixelsPerSecond 是服务层常量（导出/其它页面在用），不能改。
    // 页面自己维护可调缩放，所有「时间 ↔ 像素」换算统一走 PxPerSecond / SegmentWidthFor，
    // 卡片宽度、标尺刻度、播放头、点击定位、拖拽插入、自动跟随滚动全部共用这一套。
    private const double TimelineZoomMin     = 8.0;
    private const double TimelineZoomMax     = 160.0;
    private const double TimelineZoomDefault = 40.0;

    /// <summary>相邻标尺标签至少间隔的像素数（挑刻度步长用，防止标签叠在一起）。</summary>
    private const double RulerMinLabelGap = 46.0;

    /// <summary>候选刻度步长（秒），由密到疏，取第一个满足最小标签间距的。</summary>
    private static readonly double[] RulerStepCandidates =
        { 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };

    private double _timelineZoom = TimelineZoomDefault;

    /// <summary>当前缩放：每秒占多少像素（8–160，默认 40）。</summary>
    private double PxPerSecond => Math.Clamp(_timelineZoom, TimelineZoomMin, TimelineZoomMax);

    /// <summary>段卡片宽度（卡片、标尺、播放头、点击定位共用这一套换算）。</summary>
    private double SegmentWidthFor(double durationSeconds) =>
        Math.Max(ScriptComposerService.MinSegmentWidth, Math.Max(0, durationSeconds) * PxPerSecond);

    /// <summary>时间轴 X → 秒；与 TimelineX 严格互逆（含短段被最小宽度撑开的情况）。</summary>
    private double SecondsAtTimelineX(double x)
    {
        double cursor = 0;
        double elapsed = 0;
        foreach (ScriptSegment segment in _compose.Segments)
        {
            double duration = Math.Max(0, segment.DurationSeconds);
            double width = SegmentWidthFor(duration);
            if (x < cursor + width)
            {
                // 落在卡片间隙（CardGap）里时夹到边界，保证 x → 秒 单调不回跳
                double ratio = width > 1e-9 ? Math.Clamp((x - cursor) / width, 0, 1) : 0;
                return elapsed + ratio * duration;
            }
            cursor += width + CardGap;
            elapsed += duration;
        }
        return elapsed;
    }

    private void TimelineZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _timelineZoom = Math.Clamp(e.NewValue, TimelineZoomMin, TimelineZoomMax);
        UpdateTimelineZoomLabel();
        // XAML 解析到 Slider 时 TimelineRuler / SegmentPanel 还没生成，构造期先只更新标签
        if (TimelineRuler is null || SegmentPanel is null) return;
        RefreshTimelineLayout();
    }

    private void TimelineZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (TimelineZoomSlider is not null) TimelineZoomSlider.Value = TimelineZoomDefault;
    }

    private void UpdateTimelineZoomLabel()
    {
        if (TimelineZoomLabel is null) return;
        TimelineZoomLabel.Text = $"{PxPerSecond:0} px/s";
    }

    /// <summary>缩放变化后立刻重排卡片宽度、重画标尺、重定位播放头（不重建可视化树，选中态保留）。</summary>
    private void RefreshTimelineLayout()
    {
        for (int i = 0; i < SegmentPanel.Children.Count && i < _compose.Segments.Count; i++)
        {
            if (SegmentPanel.Children[i] is Border card)
                card.Width = SegmentWidthFor(_compose.Segments[i].DurationSeconds);
        }
        DrawTimelineRuler();
        UpdateComposeTotals();
        UpdatePlayhead();
    }

    /// <summary>挑一个刻度步长，保证相邻标签至少隔 ~46px（短段 / 小缩放下不叠字）。</summary>
    private double PickRulerStep(double totalSeconds)
    {
        foreach (double candidate in RulerStepCandidates)
        {
            if (totalSeconds / candidate > 4000) continue;   // 极端时长下别把标尺画爆
            double previousX = double.NegativeInfinity;
            bool ok = true;
            for (double t = 0; t <= totalSeconds + 1e-9; t += candidate)
            {
                double x = TimelineX(t);
                if (x - previousX < RulerMinLabelGap - 1e-9) { ok = false; break; }
                previousX = x;
            }
            if (ok) return candidate;
        }
        return RulerStepCandidates[^1];
    }

    private void DrawTimelineRuler()
    {
        TimelineRuler.Children.Clear();
        if (_compose.Segments.Count == 0) return;

        double total = _compose.Segments.Sum(segment => Math.Max(0, segment.DurationSeconds));
        if (total <= 0) return;

        // 刻度按时间均匀取点，再用 TimelineX 换成像素：卡片宽度同时受缩放与最小宽度（80px）影响，
        // 标尺 / 卡片 / 播放头必须共用这一套换算才对得齐。
        double majorStep = PickRulerStep(total);
        double minorStep = majorStep / 5.0;
        bool drawMinor = minorStep > 1e-9
                         && minorStep * PxPerSecond >= 6.0
                         && total / minorStep <= 2000.0;

        // 次刻度：只画短线，不写字
        if (drawMinor)
        {
            int minorCount = (int)Math.Floor((total - 1e-9) / minorStep);
            double lastMinorX = double.NegativeInfinity;
            for (int i = 1; i <= minorCount; i++)
            {
                if (i % 5 == 0) continue;          // 正好落在主刻度上，交给主刻度画
                double x = TimelineX(i * minorStep);
                if (x - lastMinorX < 3.0) continue;
                AddRulerTick(x, false);
                lastMinorX = x;
            }
        }

        // 主刻度：长线 + 文字
        double majorCount = Math.Floor(total / majorStep + 1e-9);
        for (double k = 0; k <= majorCount - 1e-9; k += 1)
        {
            double x = TimelineX(k * majorStep);
            AddRulerTick(x, true);
            AddRulerLabel(x, k * majorStep, false);
        }

        // 末尾总时长：右对齐画（避免超出内容宽度被裁掉）；离前一个标签太近就只画线不写字
        double endX = TimelineX(total);
        double lastMajorX = majorCount > 0 ? TimelineX(majorCount * majorStep) : 0;
        AddRulerTick(endX, true);
        if (majorCount <= 0 || endX - lastMajorX >= RulerMinLabelGap)
            AddRulerLabel(endX, total, true);
    }

    private void AddRulerTick(double x, bool major)
    {
        // 刻度线原来分别是 #555c66 / #33383f，压在 #151a1e 的卡片底上几乎看不见；
        // 主刻度提亮到 #6E7883、次刻度留一档更暗的灰，主次才分得清。
        TimelineRuler.Children.Add(new Line
        {
            X1 = x, Y1 = major ? 4 : 10, X2 = x, Y2 = 22,
            Stroke = new SolidColorBrush(major
                ? Color.FromRgb(0x6E, 0x78, 0x83)
                : Color.FromRgb(0x45, 0x4C, 0x55)),
            StrokeThickness = 1,
        });
    }

    private void AddRulerLabel(double x, double seconds, bool rightAlign)
    {
        var label = new TextBlock
        {
            Text = $"{seconds:0.##}s",
            FontSize = 9,
            // 原色 #6b7680 在深底上对比度太低（时间和刻度都看不清），改用主题里的 Muted。
            Foreground = (Brush)FindResource("Muted"),
        };
        System.Windows.Controls.Canvas.SetLeft(label, rightAlign ? Math.Max(0, x - 34) : x + 2);
        System.Windows.Controls.Canvas.SetTop(label, 0);
        TimelineRuler.Children.Add(label);
    }

    /// <summary>时间 → 时间轴 X 坐标（与卡片宽度、标尺刻度保持同一套换算）。</summary>
    private double TimelineX(double seconds)
    {
        double x = 0;
        double elapsed = 0;
        foreach (ScriptSegment segment in _compose.Segments)
        {
            double duration = Math.Max(1e-9, segment.DurationSeconds);
            double width = SegmentWidthFor(duration);
            if (seconds < elapsed + duration)
                return x + (seconds - elapsed) / duration * width;
            x += width + CardGap;
            elapsed += duration;
        }
        return Math.Max(0, x - CardGap);
    }

    // ── 播放 / 停止 ───────────────────────────────────────────────────
    private void ComposePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_compose.Segments.Count == 0)
        {
            System.Windows.MessageBox.Show("合成里还没有动作段，先从左侧动作库拖一个到时间轴。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        double total = _compose.Segments.Sum(segment => segment.DurationSeconds);
        if (total <= 0) return;

        // 勾了「同步驱动设备」却没法动设备时，先明确告诉用户，别让人对着不动的机器猜。
        // 并且真的把这次播放降级成"只做 3D 预览"：否则用户之后去点「全部归中」解锁（或设备自动重连）时，
        // 播放头还在走，设备会立刻跟着动起来 —— 而用户以为自己只是点了个预览。
        _composePreviewOnly = ComposeDriveCheck.IsChecked == true && !App.Engine.CanRun;
        if (_composePreviewOnly)
        {
            System.Windows.MessageBox.Show(
                App.Serial.IsOpen
                    ? "设备当前不可动（急停锁定中，或被其它模式占用），这次只做 3D 预览。"
                    : "设备未连接，这次只做 3D 预览。到「设置」页连接设备，或取消勾选「同步驱动设备」。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 从当前预览位置起播（点过时间轴定位后按播放不该又跳回 0）；到末尾则从头开始
        double from = _previewSeconds >= 0 && _previewSeconds < total - 0.01 ? _previewSeconds : 0;
        _playheadOffset = from;
        _playAnchorSegment = null;
        _composeSignature = ComposeSignature();
        _composeEditHintTicks = 0;
        _composeWatch.Restart();
        _composeTimer.Start();
        _composePlaying = true;
        ComposePlayBtn.Content = _composePreviewOnly ? "▶ 预览中（未驱动设备）" : "▶ 播放中";
        ComposePlayBtn.Style = (Style)FindResource("BtnPrimary");
        ComposeStopBtn.IsEnabled = true;   // 没在播放时「停止」不可点，省得用户点了没反应
        UpdatePlayhead(from);
    }

    private void ComposeStop_Click(object sender, RoutedEventArgs e) => ComposeStopPlayback();

    private void ComposeStopPlayback()
    {
        _engine.ReleaseDirectInput("editor");   // 停止试看就交还控制权（否则"待机"里一直挂着编排预览）
        _playheadOffset = 0;
        _playAnchorSegment = null;
        _composeEditHintTicks = 0;
        if (ComposeStopBtn is not null) ComposeStopBtn.IsEnabled = false;

        if (!_composePlaying)
        {
            _composeTimer.Stop();
            _composeWatch.Reset();
            UpdatePlayhead(0);
            return;
        }

        _composeTimer.Stop();
        _composeWatch.Reset();
        _composePlaying = false;
        ComposePlayBtn.Content = "▶ 播放";
        ComposePlayBtn.Style = (Style)FindResource("BtnSecondary");
        RefreshComposeUi();   // 恢复段卡片选中态（播放时会临时改成「正在播放」高亮）
        ComposePreviewLabel.Text = "已停止（不再向设备发送指令）";
    }

    /// <summary>上次预览的时间点：改参数时保持在这个位置，不再每次跳回 0（看不到正在改的那一段）。</summary>
    private double _previewSeconds;

    /// <summary>真实播放位置 = 墙钟 + 锚定偏移（改动段参数后会重设偏移，见 RebasePlayheadAfterEdit）。</summary>
    private double PlayheadSeconds => _playheadOffset + _composeWatch.Elapsed.TotalSeconds;

    private void ComposeTick()
    {
        if (!_composePlaying) return;
        double total = _compose.Segments.Sum(segment => segment.DurationSeconds);
        if (total <= 0)
        {
            ComposeStopPlayback();
            return;
        }

        if (_composeEditHintTicks > 0) _composeEditHintTicks--;

        double seconds = PlayheadSeconds;
        if (seconds >= total)
        {
            // 播到结尾即停（不循环，避免设备在无人看管时一直动）
            UpdateComposePreview(total, ComposeDriveCheck.IsChecked == true);
            UpdatePlayhead(total);
            ComposeStopPlayback();
            return;
        }

        // 播放中被编辑（拖时长/改强度/删段/排序…）：把播放头重新锚定到「同一段的同一进度」，
        // 否则按墙钟会瞬间跳到别的段，设备跟着抖一下。这里只在检测到变化的那一帧做一次。
        long signature = ComposeSignature();
        if (signature != _composeSignature)
        {
            _composeSignature = signature;
            seconds = RebasePlayheadAfterEdit(seconds, total);
            _composeEditHintTicks = 50;      // 约 2s：告诉用户改动已经生效
        }

        RecordPlayAnchor(seconds);
        UpdateComposePreview(seconds, ComposeDriveCheck.IsChecked == true);
        UpdatePlayhead(seconds);
        FollowPlayhead(seconds);
    }

    /// <summary>
    /// 段参数指纹：时长 / 强度 / 过渡 / 动作，任一项变化都会让指纹变。用于在播放中检测编辑。
    /// </summary>
    private long ComposeSignature()
    {
        long hash = 17;
        for (int i = 0; i < _compose.Segments.Count; i++)
        {
            ScriptSegment segment = _compose.Segments[i];
            hash = hash * 31 + segment.DurationSeconds.GetHashCode();
            hash = hash * 31 + segment.Intensity.GetHashCode();
            hash = hash * 31 + segment.TransitionSeconds.GetHashCode();
            hash = hash * 31 + (segment.PresetId?.GetHashCode() ?? 0);
        }
        return hash;
    }

    /// <summary>记录当前播放头所在的段与段内相对进度，供编辑后重新锚定使用。</summary>
    private void RecordPlayAnchor(double seconds)
    {
        int index = ScriptComposerService.SegmentIndexAt(_compose, seconds);
        if (index < 0) { _playAnchorSegment = null; return; }

        double start = 0;
        for (int i = 0; i < index; i++) start += _compose.Segments[i].DurationSeconds;
        ScriptSegment segment = _compose.Segments[index];
        _playAnchorSegment  = segment;
        _playAnchorProgress = segment.DurationSeconds > 0
            ? Math.Clamp((seconds - start) / segment.DurationSeconds, 0, 1)
            : 0;
    }

    /// <summary>
    /// 播放中改了段参数后，把播放头放回「同一段的同一进度」。返回修正后的播放位置。
    /// 被编辑的段如果不在播放头之后，它的起点会平移，播放头跟着一起平移即可。
    /// </summary>
    private double RebasePlayheadAfterEdit(double seconds, double total)
    {
        ScriptSegment? anchor = _playAnchorSegment;
        if (anchor is null) return seconds;

        int index = _compose.Segments.IndexOf(anchor);
        if (index < 0) return seconds;       // 段被删掉了，保持当前墙钟位置

        double start = 0;
        for (int i = 0; i < index; i++) start += _compose.Segments[i].DurationSeconds;
        double target = Math.Clamp(start + _playAnchorProgress * anchor.DurationSeconds, 0, total);

        _playheadOffset = target;
        _composeWatch.Restart();
        return target;
    }

    /// <summary>播放时让时间轴跟着播放头横向滚动，长编排才看得见进度。</summary>
    private void FollowPlayhead(double seconds)
    {
        if (TimelineScroll is null) return;
        double x = TimelineX(seconds);
        double viewport = TimelineScroll.ViewportWidth;
        if (viewport <= 0) return;
        double left = TimelineScroll.HorizontalOffset;
        if (x < left + 40 || x > left + viewport - 80)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - viewport * 0.35));
    }

    /// <summary>
    /// 取某一时刻的六轴值并刷新 3D 预览；driveDevice 为 true 时同时直接下发给设备。
    /// </summary>
    private void UpdateComposePreview(double seconds, bool driveDevice)
    {
        // 起播时设备不可动 → 这次播放只做预览（见 ComposePlay_Click 的说明）：
        // 中途设备被解锁也不会突然开始动。
        if (_composePreviewOnly) driveDevice = false;
        _previewSeconds = seconds;
        double[] values = ScriptComposerService.SampleComposition(_compose, ComposePresets, seconds, ComposeBpm);

        // AxisPreview3D 自带 120ms 定时器会读引擎输出覆盖外部写入的值。
        // 先调用它的 RefreshPreview()（刷新其内部去重时间戳），再写入本次采样值，
        // 这样它的定时器在 50ms 去重窗口内会跳过，渲染出来的始终是编排预览。
        ComposePreview3D.RefreshPreview();
        ComposePreview3D.SetStatusText("编排预览");
        ComposePreview3D.UpdateAxes(values);

        if (!driveDevice)
        {
            // 没驱动时的文案要区分三种情况：本次被降级成预览 / 勾了但还没按播放 / 压根没勾。
            // 原来一律写「未驱动设备」，勾了开关的人会以为开关坏了。
            ComposePreviewLabel.Text = _composePreviewOnly
                ? "本次只做 3D 预览（设备当前不可动，不会驱动）" + EditHintSuffix()
                : ComposeDriveCheck.IsChecked == true
                    ? "已勾选驱动设备：点「▶ 预览播放」才会真的动设备"
                    : "仅 3D 预览（不会动设备）" + EditHintSuffix();
            return;
        }
        // 编排试看同样是"直接下发源"：认领控制权，否则悬浮窗急停会变灰、侧栏写"待机"——设备真在动。
        _engine.TryClaimDirectInput("editor");
        ComposePreviewLabel.Text = _engine.TrySendDirectAxes(values)
            ? "已同步驱动设备" + EditHintSuffix()
            : "同步驱动失败：设备未连接 / 正忙 / 急停中（预览不受影响）";
    }

    /// <summary>播放中改段参数后的短提示（约 2s），让用户知道改动已经实时生效、播放头没跳段。</summary>
    private string EditHintSuffix() =>
        _composePlaying && _composeEditHintTicks > 0 ? " · 改动已实时生效" : "";

    private void UpdatePlayhead(double? seconds = null)
    {
        double total = _compose.Segments.Sum(segment => segment.DurationSeconds);
        bool show = _composePlaying && total > 0;
        ComposePlayhead.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        // 无参调用（改参数后刷新）也要用真实播放位置：改段时长后播放头带偏移，
        // 直接用墙钟会让播放头闪回 0 附近再被下一帧拉回来。
        double t = Math.Clamp(seconds ?? PlayheadSeconds, 0, total);
        System.Windows.Controls.Canvas.SetLeft(ComposePlayhead, TimelineX(t));
        HighlightPlayingSegment(t);
    }

    /// <summary>播放时把当前段描边换成主色（不改背景，避免频繁重建卡片）。</summary>
    private void HighlightPlayingSegment(double seconds)
    {
        int playing = ScriptComposerService.SegmentIndexAt(_compose, seconds);
        for (int i = 0; i < SegmentPanel.Children.Count && i < _compose.Segments.Count; i++)
        {
            if (SegmentPanel.Children[i] is not Border card) continue;
            bool selected = ReferenceEquals(_compose.Segments[i], _selectedSegment);
            bool current  = i == playing;
            card.BorderBrush = (Brush)FindResource(current ? "Primary" : selected ? "Accent" : "Border");
            card.BorderThickness = new Thickness(current || selected ? 2 : 1);
        }
    }

    // ── 新建 / 打开 / 保存 / 另存为 ────────────────────────────────────
    private void ComposeNew_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges("新建合成")) return;

        ComposeStopPlayback();
        _compose = new ScriptComposition { Name = "未命名" };
        _compositionPath = null;
        _composeDirty = false;
        _selectedSegment = null;
        ClearComposeUndoHistory();   // 新合成不能撤销回上一个合成
        RefreshComposeUi();
    }

    /// <summary>
    /// 打开已保存的合成。之前工具栏只有「保存」没有「打开」，存了以后根本载不回来
    /// （用户反馈「保存的动作都没办法导回」），这里补上。
    /// 优先从 compositions 目录里挑，也允许打开任意 .json。
    /// </summary>
    private void ComposeOpen_Executed(object sender, ExecutedRoutedEventArgs e) => ComposeOpen_Click(this, new RoutedEventArgs());

    private void ComposeOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter           = "Hexa 动作合成|*.json",
            InitialDirectory = CompositionStore.EnsureDirectory(),
            Title            = "打开动作合成",
        };
        if (dialog.ShowDialog() != true) return;
        LoadCompositionFromPath(dialog.FileName);
    }

    private void LoadCompositionFromPath(string path)
    {
        if (!ConfirmDiscardChanges("打开其它合成")) return;
        try
        {
            ScriptComposition loaded = ScriptComposition.FromJson(File.ReadAllText(path));
            ComposeStopPlayback();                       // 先停播放，否则旧合成还在驱动设备
            _compose = loaded;
            _compose.Name = ScriptComposition.SanitizeName(
                System.IO.Path.GetFileNameWithoutExtension(path));
            _compositionPath = path;
            _composeDirty = false;
            _selectedSegment = _compose.Segments.Count > 0 ? _compose.Segments[0] : null;
            ComposeBpmSlider.Value = Math.Clamp(
                _compose.Bpm, ScriptComposerService.MinBpm, ScriptComposerService.MaxBpm);
            ClearComposeUndoHistory();   // 打开别的合成后撤销历史必须清空，否则会撤回到旧合成
            RefreshComposeUi();
            App.Settings.LastCompositionPath = path;
            App.Settings.Save();
        }
        catch (FormatException ex)
        {
            System.Windows.MessageBox.Show($"这个文件不是有效的动作合成：\n{ex.Message}", "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            AppLogger.Error("合成载入失败", ex);
            System.Windows.MessageBox.Show($"打开失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 未保存改动确认：取消 = 中止当前操作；是 = 先保存（保存失败/取消另存为则中止）；
    /// 否 = 丢弃改动继续。
    /// </summary>
    private bool _waveDraftHintShown;

    /// <summary>
    /// 启动时从草稿恢复的手画波形：第一次进页面提示一句，免得用户以为是别人留下的数据。
    /// </summary>
    private void ShowWaveDraftHintOnce()
    {
        if (_waveDraftHintShown) return;
        _waveDraftHintShown = true;
        if (!AxIds.Any(id => _engine.EditWaves[id].Count > 0)) return;
        // 如果草稿里还带着"这份波形是从哪份脚本导入的"，一并说明 —— 用户要知道导出的时间戳从哪来
        string sourceNote = _sources.Count > 0
            ? $"{string.Join("、", _sources.Keys)} 还记着上次导入的脚本原始时间（没动过的点会按原时间原样导回）。"
            : "";
        SetWaveStatus("已恢复上次退出时的手画波形草稿（用「保存预设」导出，或「清空」丢弃）。" + sourceNote);
    }

    private bool _offeredLastComposition;

    /// <summary>
    /// 启动后第一次进入编排页时，提示继续上次编辑的合成。
    /// AppSettings.LastCompositionPath 之前只写不读（存了也用不上），这里补上。
    /// </summary>
    private void OfferLastCompositionOnce()
    {
        if (_offeredLastComposition) return;
        _offeredLastComposition = true;

        string path = App.Settings.LastCompositionPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (_compose.Segments.Count > 0 || _composeDirty) return;   // 当前已有内容就别打扰

        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (System.Windows.MessageBox.Show(
                $"继续上次编辑的合成「{name}」？\n\n{path}",
                "继续上次的合成", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            LoadCompositionFromPath(path);
    }

    private bool ConfirmDiscardChanges(string action)
    {
        if (!_composeDirty || _compose.Segments.Count == 0) return true;
        MessageBoxResult result = System.Windows.MessageBox.Show(
            $"当前合成「{_compose.Name}」还有未保存的改动，是否先保存？", action,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) return SaveComposition();
        return true;
    }

    /// <summary>退出程序前由 MainWindow / 托盘「退出」调用：有未保存的合成时询问用户。</summary>
    /// <returns>false = 用户取消退出（或保存失败），调用方必须中止退出。</returns>
    public bool ConfirmExitWithUnsavedWork()
    {
        if (!_composeDirty) return true;

        MessageBoxResult result = System.Windows.MessageBox.Show(
            $"合成「{_compose.Name}」还有未保存的改动，退出后就找不回来了。\n\n" +
            "是 = 先保存再退出；否 = 不保存直接退出；取消 = 留在程序里。",
            "退出 Hexa", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) return SaveComposition();   // 保存失败也不退，别让用户白丢
        return true;
    }

    /// <summary>供外部（退出确认）查询是否有未保存的合成改动。</summary>
    public bool HasUnsavedWork => _composeDirty;

    private void ComposeSave_Click(object sender, RoutedEventArgs e) => SaveComposition();

    /// <summary>保存：有名字就写回 compositions 目录，没有名字（未命名）走另存为。</summary>
    private bool SaveComposition()
    {
        _compose.Bpm = ComposeBpm;
        string name = ScriptComposition.SanitizeName(_compose.Name);
        if (string.IsNullOrEmpty(_compositionPath) || name == "未命名")
            return SaveCompositionAs();

        // 从库外打开的合成：直接写回原文件，别悄悄在 compositions 目录另存一份
        // （之前的行为是原文件保持旧内容、用户以为已经更新了）。
        string target = CompositionStore.PathFor(name);
        if (string.Equals(_compositionPath, target, StringComparison.OrdinalIgnoreCase))
            return WriteComposition(target);

        string current = _compositionPath ?? "";
        bool outsideLibrary = current.Length > 0 &&
            !string.Equals(System.IO.Path.GetDirectoryName(current),
                CompositionStore.EnsureDirectory().TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        if (outsideLibrary && File.Exists(current))
        {
            // 名字没改就写回原文件；改了名字再问要不要落到库目录
            string currentName = System.IO.Path.GetFileNameWithoutExtension(current);
            if (string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase))
                return WriteComposition(current);
        }

        // 换了名字/文件不在库里时，确认一次，避免悄悄覆盖别人的合成
        if (File.Exists(target) &&
            System.Windows.MessageBox.Show(
                $"compositions 目录里已经有同名合成「{name}」，覆盖它吗？", "同名合成",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return false;
        }
        return WriteComposition(target);
    }

    private bool SaveCompositionAs()
    {
        _compose.Bpm = ComposeBpm;
        var dialog = new SaveFileDialog
        {
            Filter           = "Hexa 动作合成|*.json",
            InitialDirectory = CompositionStore.EnsureDirectory(),
            FileName         = ScriptComposition.SanitizeName(_compose.Name) + ".json",
            Title            = "另存为动作合成",
        };
        if (dialog.ShowDialog() != true) return false;
        return WriteComposition(dialog.FileName);
    }

    private void ComposeSaveAs_Click(object sender, RoutedEventArgs e) => SaveCompositionAs();

    /// <summary>写盘；成功返回 true（供「先保存再继续」的流程判断）。</summary>
    private bool WriteComposition(string path)
    {
        try
        {
            _compose.Name = ScriptComposition.SanitizeName(
                System.IO.Path.GetFileNameWithoutExtension(path));
            _compose.Bpm = ComposeBpm;
            File.WriteAllText(path, _compose.ToJson());
            _compositionPath = path;
            _composeDirty = false;
            UpdateComposeTotals();
            App.Settings.LastCompositionPath = path;
            App.Settings.Save();
            System.Windows.MessageBox.Show(
                $"已保存：\n{path}\n\n" +
                "注意：这是编排工程文件（.json），「脚本库」只列 .funscript，不会出现在那里。\n" +
                "要在「脚本库」里播放，请点「导出 funscript」。",
                "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("合成保存失败", ex);
            System.Windows.MessageBox.Show($"保存失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ── 导入 / 导出 funscript ─────────────────────────────────────────
    private void ComposeImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Funscript 或 Hexa 合成|*.funscript;*.json",
            Title  = "导入 funscript（或直接打开 Hexa 合成）",
        };
        if (dialog.ShowDialog() != true) return;

        // 合成文件也是 .json，误选时别报「没有可用的六轴 actions」，直接当合成打开
        try
        {
            string head = File.ReadAllText(dialog.FileName);
            if (head.Contains("\"Segments\"", StringComparison.OrdinalIgnoreCase))
            {
                LoadCompositionFromPath(dialog.FileName);
                return;
            }
        }
        catch (Exception) { /* 读不出来就走下面的 funscript 导入，由它报错 */ }

        if (!ImportFunscriptIntoWaveEditor(dialog.FileName)) return;

        MainTabs.SelectedIndex = 1;
        // 超长脚本被截过的话这里必须再点一句：用户是从「编排」页点进来的，看不到刚才那个确认框的上下文
        bool anyTruncated = _sources.Values.Any(source => source.OriginalDurationMs > source.CanvasDurationMs + 1);
        System.Windows.MessageBox.Show(
            "已导入到「微调波形」页，可以继续画/改波形或直接导出。\n" +
            "（编排页无法把 funscript 反向拆成动作段，所以导入落在微调页。）"
            + (anyTruncated ? "\n\n注意：脚本比一圈上限长，只载入了前 60 秒，导出也只有这 60 秒。" : ""),
            "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ComposeExport_Click(object sender, RoutedEventArgs e)
    {
        if (_compose.Segments.Count == 0)
        {
            System.Windows.MessageBox.Show("合成里还没有动作段，先从左侧动作库拖一个到时间轴。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 先校验再让用户选文件名（原来选完才报错，白选一次）
        string json;
        try
        {
            json = ScriptComposerService.ComposeFunscript(_compose, ComposePresets, ComposeBpm);
        }
        catch (Exception ex)
        {
            AppLogger.Error("编排 funscript 生成失败", ex);
            System.Windows.MessageBox.Show($"导出失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ScriptLibrary.EnsureFolder();
        // 导出前把「这次会导出什么」写进标题栏：文件里有多少段、多长、几轴，用户选路径时就能看到
        (int segments, double total) = ScriptComposerService.Estimate(_compose, ComposePresets, ComposeBpm);
        var dialog = new SaveFileDialog
        {
            Filter           = "Funscript|*.funscript",
            FileName         = ScriptComposition.SanitizeName(_compose.Name) + ".funscript",
            InitialDirectory = ScriptLibrary.Folder,   // 直接进「脚本库」，省得再手动导入
            Title            = $"导出 funscript（{segments} 段 / {total:0.##}s / 一个文件含 L0..R2 六轴）",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, json);
        }
        catch (Exception ex)
        {
            AppLogger.Error("编排 funscript 写入失败", ex);
            System.Windows.MessageBox.Show($"写入文件失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 说清楚「导到哪了、下一步做什么」：落在脚本库目录里就能直接播，落在别处还得手动导入一次
        bool inLibrary = string.Equals(
            System.IO.Path.GetDirectoryName(dialog.FileName)?.TrimEnd(System.IO.Path.DirectorySeparatorChar),
            ScriptLibrary.Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        System.Windows.MessageBox.Show(
            $"已导出 {segments} 段 / {total:0.##}s（BPM {ComposeBpm}）：\n{dialog.FileName}\n\n" +
            (inLibrary
                ? "下一步：到左侧「脚本库」页就能看到它，选中后点播放即可。"
                : "注意：这个文件不在「脚本库」目录里，所以要先去「脚本库」页点「导入」把它加进来才能播放。"),
            "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── JSON 序列化模型 ──────────────────────────────────────────────
    private class WavePresetFile
    {
        // 0 = 旧文件（没有 AxisEnabled 字段），2 = 当前版本；用它区分「旧文件」和「显式全不勾」
        public int Version { get; set; }
        public double CycleLen { get; set; } = 2.0;

        // P0-2：保存预设时的六轴启用状态；加载时恢复并刷新轴开关面板
        public bool[] AxisEnabled { get; set; } = new bool[6];
        public Dictionary<string, List<double[]>> Waves { get; set; } = new();
    }
}
