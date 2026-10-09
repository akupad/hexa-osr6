using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hexa.Models;
using Hexa.Services;
using Hexa.ViewModels;

namespace Hexa.Views;

public partial class StrokesPage : Page
{
    private readonly StrokesViewModel _vm = App.StrokesVm;
    private readonly string[] _cats = StrokesViewModel.Categories;

    // ── 卡片墙：宽度按窗口实算（每张一样宽、文字不会被切），高度固定（网格整齐）──
    private const double CardGap      = 8;
    private const double CardMinWidth = 124;
    private const int    CardMaxCols  = 6;    // 宽屏也别排成密密麻麻的小格子
    private const double CardHeight   = 56;
    private double _cardWidth = 156;

    // 实时 3D 预览刷新（120ms，与 PlaygroundPage 的 150ms 轴条刷新同一节奏）
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _previewTimerRunning;

    // ── 卡片悬停 → 右侧 3D 主预览直接试看这个动作 ──────────────────
    // 之前是弹一个独立小窗（StrokePreviewPopup），用户要求改成「就在右边的模型里显示动作」，
    // 所以弹窗已移除：悬停卡片 = 主预览按 60 BPM 采样该动作；移开 = 恢复引擎实时输出。
    private const int HoverLeaveDelayMs = 150;   // 离开后延迟恢复，避免在卡片之间移动时闪
    private readonly DispatcherTimer _hoverLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(HoverLeaveDelayMs) };
    private StrokePreset? _hoverPreset;          // 当前悬停的动作（卡片重建后用来接回试看）

    // 主预览试看动作：40ms 一帧，相位按真实耗时推进（与编排页的预览同一节奏）
    private readonly DispatcherTimer _presetPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly System.Diagnostics.Stopwatch _presetPreviewWatch = new();
    private readonly double[] _presetPreviewValues = new double[6];   // 复用缓冲，不每帧分配
    private StrokePreset? _previewPreset;                             // 非 null = 正在试看这个动作
    private bool _previewPinned;                                      // true = 用户点了「试看选中」，不被悬停 / 移开打断
    private double _presetPreviewTime;                                // 已推进的圈数（60 BPM = 1 圈/秒）

    private readonly Color[] _catColors =
    {
        Color.FromRgb(0x88,0x88,0x88),
        Color.FromRgb(0xa7,0x8b,0xfa),
        Color.FromRgb(0x67,0xe8,0xf9),
        Color.FromRgb(0x86,0xef,0xac),
        Color.FromRgb(0xfb,0xa3,0x4a),
        Color.FromRgb(0xf4,0x72,0x72),
        Color.FromRgb(0xf9,0xd7,0x2e)
    };

    // 六轴固定配色：与脚本制作 / 设置 / 动作编辑器里的轴色一致（全程序统一，不另造配色）
    private static readonly Color[] AxisColors =
    {
        Color.FromRgb(0xa7,0x8b,0xfa), Color.FromRgb(0x67,0xe8,0xf9),
        Color.FromRgb(0x86,0xef,0xac), Color.FromRgb(0xfb,0xa3,0x4a),
        Color.FromRgb(0xf4,0x72,0x72), Color.FromRgb(0xf9,0xd7,0x2e)
    };
    private static readonly string[] AxisNames = { "L0", "L1", "L2", "R0", "R1", "R2" };

    public StrokesPage()
    {
        InitializeComponent();
        BuildCategoryTabs();
        UpdateResponsiveLayout();     // 先按当前宽度算一次卡片尺寸，再建卡片
        RefreshCards();
        UpdateSearchUi();
        // 上次用过的动作会被恢复成选中态，详情面板直接跟上（否则卡片高亮着、右边却是空的）
        if (_vm.SelectedStroke is { } restored) ShowDetail(restored);

        SpeedSlider.Value = _vm.Speed;
        SpeedLabel.Text   = _vm.Speed.ToString("F1") + "x";
        SpeedSlider.ValueChanged += (_, e) =>
        {
            _vm.Speed = e.NewValue;
            SpeedLabel.Text = e.NewValue.ToString("F1") + "x";
        };

        // 按钮禁用时提示仍然要能看到 —— 否则用户只知道「点不动」，不知道为什么
        ToolTipService.SetShowOnDisabled(PlayBtn, true);
        ToolTipService.SetShowOnDisabled(EditStrokeBtn, true);
        ToolTipService.SetShowOnDisabled(DeleteStrokeBtn, true);
        ToolTipService.SetShowOnDisabled(PreviewToggleBtn, true);

        // 热键 NextStroke 在后台改 SelectedStroke → 触发页面刷新；
        // 播放状态（热键 / 急停 / 托盘都会改 Playing）统一交给 SyncPlayButton，
        // 不再只在两个 Click 里更新文案，否则按钮会被其它入口带偏。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Playing))
            {
                App.Dispatch(() => { SyncPlayButton(); RefreshCards(); });
                return;
            }
            if (e.PropertyName != nameof(_vm.SelectedStroke)) return;
            App.Dispatch(() =>
            {
                RefreshCards();
                // 钉住试看时，选中谁就试看谁（不然点了卡片右边还在放上一个动作，像卡住了）
                if (_previewPinned && _vm.SelectedStroke is { } pinnedTarget)
                    StartPresetPreview(pinnedTarget, pinned: true);
                if (_vm.SelectedStroke != null) ShowDetail(_vm.SelectedStroke);
                else ShowDetailEmpty();
                SyncPlayButton();
            });
        };

        // 连接 / 引擎状态变化 → 播放按钮可用性、设备提示条跟着变
        // （卡片上的「播放中」标记由 VM 的 Playing 变化驱动，这里不重建卡片，省得状态一变就刷一遍）
        App.Engine.StateChanged      += () => App.Dispatch(SyncPlayButton);
        App.Serial.ConnectionChanged += _ => App.Dispatch(SyncPlayButton);
        SyncPlayButton();

        // 实时 3D 六轴预览：页面可见时才跑定时器，避免后台空转
        _previewTimer.Tick += (_, _) => RefreshPreview();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) StartPreviewTimer();
            else
            {
                StopPreviewTimer();
                StopPresetPreview();   // 页面不可见时不残留试看
            }
        };
        Unloaded += (_, _) =>
        {
            StopPreviewTimer();
            StopPresetPreview();
        };
        Loaded += (_, _) => { UpdateResponsiveLayout(); if (IsVisible) StartPreviewTimer(); };

        // 窗口宽度变了就重算右栏宽度和卡片宽度（最小 880 窗口下正文只有约 660px）
        SizeChanged += (_, _) => UpdateResponsiveLayout();

        // 本页自己按 120ms 喂 Preview3D，关掉控件内置的引擎刷新，避免两个定时器打架
        Preview3D.AutoRefreshEnabled = false;

        // 卡片悬停 → 主预览试看该动作；离开卡片区延迟恢复实时输出
        CardScroll.MouseLeave += (_, _) => CardMouseLeave();
        _presetPreviewTimer.Tick += (_, _) => PresetPreviewTick();
        _hoverLeaveTimer.Tick += (_, _) => RestoreLivePreview();
        _usageDwellTimer.Tick += (_, _) => NoteHoveredStrokeUsed();   // 悬停停留够久 = 用过（记进「最近用过」）
    }

    // ══════════════════════════════════════════════════════════════
    //  排版：右栏宽度 + 卡片宽度都按窗口实算
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 正文可用宽度 = 页宽 − 左右各 24 留白。右栏取 42%（夹在 200–300px 之间），
    /// 剩下的全给卡片墙；卡片按「放得下几张就几张」均分，保证每张等宽且不被切。
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        if (SideColumn == null) return;
        double pageWidth = ActualWidth > 0 ? ActualWidth : 900;      // 首次布局前给个保守值
        double avail = Math.Max(pageWidth - 48, 240);

        double side = Math.Round(Math.Clamp(avail * 0.42, 200, 300));
        if (Math.Abs(SideColumn.Width.Value - side) > 0.5) SideColumn.Width = new GridLength(side);

        // 20px 余量 = 竖向滚动条 8 + ScrollViewer 右侧留白 6 + 取整误差
        double cardsAvail = Math.Max(avail - side - 12 - 20, CardMinWidth);
        int cols = Math.Clamp((int)Math.Floor((cardsAvail + CardGap) / (CardMinWidth + CardGap)), 1, CardMaxCols);
        double width = Math.Max(Math.Floor(cardsAvail / cols) - CardGap - 2, 96);

        // 4px 死区：拖窗口时不要每一像素都重建一次卡片墙（卡片本来就有 6px 余量，不会因此被切）
        if (Math.Abs(width - _cardWidth) >= 4)
        {
            _cardWidth = width;
            RefreshCards();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  工具条：分类 / 搜索 / 筛选
    // ══════════════════════════════════════════════════════════════

    /// <summary>搜索框：改关键字后重建卡片墙（这个页面没有 DataContext，统一走 code-behind）。</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CardPanel is null) return;   // XAML 解析期间会先触发一次，此时卡片容器还没建好
        _vm.SearchText = SearchBox.Text ?? "";
        UpdateSearchUi();
        RefreshCards();
    }

    /// <summary>搜索框按 Esc 直接清空（Esc 没有绑定全局热键，不会误触急停）。</summary>
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        ClearSearch_Click(sender, e);
    }

    /// <summary>只清搜索词（分类筛选保持不动，免得用户以为点了「清空」连分类也被重置）。</summary>
    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";             // 触发 TextChanged → 重新筛选
        UpdateSearchUi();
        RefreshCards();
        SearchBox.Focus();
    }

    /// <summary>空状态里的「清除搜索和筛选」：搜索词、分类、只看自建 / 收藏 / 最近用过一起复原。</summary>
    private void ResetFilters()
    {
        SearchBox.Text = "";
        _vm.SelectedCategory = "全部";
        _vm.CustomOnly = false;
        _vm.FavoriteOnly = false;
        _vm.RecentOnly = false;
        BuildCategoryTabs();
        UpdateSearchUi();
        RefreshCards();
    }

    /// <summary>占位提示字与「✕ 清空」按钮只在需要时出现。</summary>
    private void UpdateSearchUi()
    {
        if (SearchHint is null || ClearSearchBtn is null || SearchBox is null) return;
        bool empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchHint.Visibility     = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>结果条数：让用户一眼知道「筛掉了多少」，一个也没有时不至于以为界面坏了。</summary>
    private void UpdateCountLabel(int shown)
    {
        if (CountLabel is null) return;
        int total = StrokesViewModel.AllPresets.Length;
        bool filtering = IsFiltering();
        CountLabel.Text = filtering ? $"显示 {shown} / {total} 个" : $"共 {total} 个动作";
        CountLabel.Foreground = (Brush)FindResource(filtering && shown == 0 ? "Warning" : "Muted");
    }

    private bool IsFiltering() =>
        !string.IsNullOrWhiteSpace(_vm.SearchText) || _vm.SelectedCategory != "全部"
        || _vm.CustomOnly || _vm.FavoriteOnly || _vm.RecentOnly;

    /// <summary>分类标签 + 四个筛选开关（只看自建 / 只看收藏 / 最近用过 / 幅度排序）。</summary>
    private void BuildCategoryTabs()
    {
        if (CategoryTabs is null) return;
        CategoryTabs.Children.Clear();

        var all = StrokesViewModel.AllPresets;
        for (int ci = 0; ci < _cats.Length; ci++)
        {
            string cat = _cats[ci];
            int count = cat == "全部" ? all.Length : all.Count(p => p.Cat == cat);
            bool active = cat == _vm.SelectedCategory;
            CategoryTabs.Children.Add(MakeChip(
                cat, active,
                () =>
                {
                    _vm.SelectedCategory = cat;
                    BuildCategoryTabs();
                    RefreshCards();
                },
                $"{cat}：{count} 个动作" + (active ? "（当前正在看这一类）" : "，点一下只看这一类")));
        }

        CategoryTabs.Children.Add(MakeChip(
            "只看自建", _vm.CustomOnly,
            () =>
            {
                _vm.CustomOnly = !_vm.CustomOnly;
                BuildCategoryTabs();
                RefreshCards();
            },
            "只看自己新建的动作（内置动作先藏起来）"));

        // ⭐ 收藏 / 🕘 最近用过：动作库的"我的常用"。收藏与最近都存在设置里，
        // 与游玩页共用同一份（收藏的动作在随机列表里排最前、也更容易被抽到）。
        int favoriteCount = _vm.FavoriteIds.Count;
        CategoryTabs.Children.Add(MakeChip(
            "⭐ 只看收藏", _vm.FavoriteOnly,
            () =>
            {
                _vm.FavoriteOnly = !_vm.FavoriteOnly;
                BuildCategoryTabs();
                RefreshCards();
            },
            favoriteCount == 0
                ? "还没有收藏的动作：在卡片右下角点一下 ☆ 就收藏了"
                : $"只看收藏过的 {favoriteCount} 个动作（在卡片右下角点 ★ 取消收藏）"));

        int recentCount = _vm.RecentCount;
        CategoryTabs.Children.Add(MakeChip(
            "🕘 最近用过", _vm.RecentOnly,
            () =>
            {
                _vm.RecentOnly = !_vm.RecentOnly;
                BuildCategoryTabs();
                RefreshCards();
            },
            recentCount == 0
                ? "还没有用过的动作：试看、播放、被自动模式抽到都会记在这里"
                : $"只看最近用过的 {recentCount} 个动作，最新的排最前（最多留 12 个）"));

        CategoryTabs.Children.Add(MakeChip(
            "幅度排序", _vm.SortByAmplitude,
            () =>
            {
                _vm.SortByAmplitude = !_vm.SortByAmplitude;
                BuildCategoryTabs();
                RefreshCards();
            },
            _vm.RecentOnly
                ? "把行程最大的动作排在最前面（「🕘 最近用过」开着时按新旧排，这项暂不生效）"
                : "把行程最大的动作排在最前面（再点一下恢复原顺序）"));
    }

    private Button MakeChip(string text, bool active, Action onClick, string tip)
    {
        var chip = new Button
        {
            Content     = active ? text + " ✓" : text,
            Height      = 26,
            FontSize    = 12,
            Padding     = new Thickness(10, 0, 10, 0),
            Margin      = new Thickness(0, 0, 6, 0),
            Style       = (Style)FindResource("BtnGhost"),
            Background  = active ? (Brush)FindResource("PrimarySoft") : Brushes.Transparent,
            Foreground  = (Brush)FindResource(active ? "Primary" : "Muted"),
            FontWeight  = active ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor      = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip     = tip
        };
        chip.Click += (_, _) => onClick();
        return chip;
    }

    // ══════════════════════════════════════════════════════════════
    //  实时 3D 六轴预览
    // ══════════════════════════════════════════════════════════════

    private void StartPreviewTimer()
    {
        RefreshPreview();
        if (_previewTimerRunning) return;
        _previewTimer.Start();
        _previewTimerRunning = true;
    }

    private void StopPreviewTimer()
    {
        if (!_previewTimerRunning) return;
        _previewTimer.Stop();
        _previewTimerRunning = false;
    }

    /// <summary>
    /// 驱动 3D 预览刷新。
    /// MotionEngine 目前没有公开的“当前输出”读取接口（GetLastOutput 为私有，
    /// GetAxisAmp 返回的是振幅而非实时位置），因此实时轴值由 AxisPreview3D
    /// 控件内部自行获取（见该控件的 RefreshPreview 注释），页面只负责刷新节奏。
    /// </summary>
    private void RefreshPreview()
    {
        if (Preview3D == null) return;
        if (_previewPreset is not null) return;   // 正在试看动作：这一路先让位
        try
        {
            Preview3D.RefreshPreview();
        }
        catch (Exception)
        {
            // 预览是纯可视化，任何异常都不允许影响动作页主流程
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  试看：悬停卡片自动试看；也可以点「👁 试看选中」钉住一直看
    // ══════════════════════════════════════════════════════════════

    /// <summary>鼠标进入卡片：立刻在主预览里试看这个动作（无延迟、无弹窗）。</summary>
    private void CardMouseEnter(StrokePreset preset)
    {
        _hoverPreset = preset;
        if (_previewPinned) return;    // 用户自己钉住的试看不被悬停抢走
        _hoverLeaveTimer.Stop();
        StartPresetPreview(preset, pinned: false);
    }

    /// <summary>
    /// 鼠标离开整个卡片区（不是单张卡片）：延迟恢复引擎实时输出，避免在卡片之间移动时闪。
    /// 挂在 ScrollViewer 上而不是每张卡片上 —— 卡片会随搜索 / 筛选整批重建，
    /// 挂在卡片上的话重建后新旧对象对不上，实时预览会一直停在被清掉的那一帧。
    /// </summary>
    private void CardMouseLeave()
    {
        if (_previewPinned) return;
        CancelUsageDwell();     // 鼠标已经离开卡片区：这不算"用过"
        _hoverLeaveTimer.Stop();
        _hoverLeaveTimer.Start();
    }

    /// <summary>「👁 试看选中」：点一下钉住试看，再点一下回到实时。</summary>
    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_previewPinned)
        {
            RestoreLivePreview();
            return;
        }
        if (_vm.SelectedStroke is { } preset)
        {
            StartPresetPreview(preset, pinned: true);
            _vm.NoteStrokeUsed(preset.Id);   // 主动钉住试看 = 明确的"用过"
        }
    }

    // ── 悬停试看满 500ms 才算「用过」（写进最近用过）──
    // 鼠标扫过一排卡片不该把「最近用过」冲掉，所以给一个停留阈值；
    // 真的驱动设备（播放）与主动钉住试看则立刻记账，不看这个计时器。
    private const int HoverUsageDelayMs = 500;
    private readonly DispatcherTimer _usageDwellTimer = new() { Interval = TimeSpan.FromMilliseconds(HoverUsageDelayMs) };
    private StrokePreset? _usageDwellPreset;

    private void StartUsageDwell(StrokePreset preset)
    {
        _usageDwellPreset = preset;
        _usageDwellTimer.Stop();
        _usageDwellTimer.Start();
    }

    private void CancelUsageDwell()
    {
        _usageDwellTimer.Stop();
        _usageDwellPreset = null;
    }

    private void NoteHoveredStrokeUsed()
    {
        _usageDwellTimer.Stop();
        if (_usageDwellPreset is { } preset) _vm.NoteStrokeUsed(preset.Id);
        _usageDwellPreset = null;
    }

    /// <summary>在主预览里开始试看某个动作：停掉引擎驱动的刷新，改用本地采样。</summary>
    private void StartPresetPreview(StrokePreset preset, bool pinned)
    {
        if (!IsVisible) return;
        _previewPreset = preset;
        _previewPinned = pinned;
        _presetPreviewTime = 0;
        _presetPreviewWatch.Restart();
        StopPreviewTimer();
        _presetPreviewTimer.Stop();
        _presetPreviewTimer.Start();
        PresetPreviewTick();                    // 立刻出一帧，别等 40ms
        UpdatePreviewCaption();
        // 悬停试看要停留够久才算"用过"；钉住试看由 PreviewToggle_Click 立刻记账
        if (pinned) CancelUsageDwell();
        else StartUsageDwell(preset);
    }

    /// <summary>停止试看（含解除钉住）并交还实时输出。</summary>
    private void StopPresetPreview()
    {
        _presetPreviewTimer.Stop();
        _previewPreset = null;
        _previewPinned = false;
        _hoverLeaveTimer.Stop();
        CancelUsageDwell();
        UpdatePreviewCaption();
    }

    /// <summary>
    /// 恢复引擎实时输出（鼠标离开卡片、开始播放、卡片重建时调用）。
    /// 这里**不能**因为「当前没在试看」就早退：点卡片会先 StopPresetPreview 再走这条路，
    /// 早退会让被停掉的页面定时器永远不重启。
    /// </summary>
    private void RestoreLivePreview()
    {
        _hoverLeaveTimer.Stop();
        _hoverPreset = null;
        if (_previewPreset is not null || _previewPinned) StopPresetPreview();
        if (IsVisible) StartPreviewTimer();
    }

    /// <summary>
    /// 试看采样：固定 60 BPM（1 圈/秒），与编排预览、动作编辑器波形用同一套
    /// MotionEngine.SampleTempestAxis，所以试看出来的动作与设备真跑的一致。
    /// 只做可视化，绝不碰设备。
    /// </summary>
    private void PresetPreviewTick()
    {
        var preset = _previewPreset;
        if (preset is null || !IsVisible) return;

        double delta = _presetPreviewWatch.Elapsed.TotalSeconds;
        _presetPreviewWatch.Restart();
        if (delta < 0 || delta > 0.5) delta = 0.04;      // 卡顿后不跳大步
        _presetPreviewTime += delta;                      // 60 BPM = 1 圈/秒
        if (_presetPreviewTime > 3600) _presetPreviewTime = 0;

        double angle = _presetPreviewTime * Math.Tau;
        long cycleIndex = (long)Math.Floor(_presetPreviewTime);
        var axes = preset.AllAxes;
        for (int i = 0; i < 6; i++)
        {
            double[] axis = axes[i];
            double sampled = MotionEngine.SampleTempestAxis(
                axis[0],
                axis[1],
                axis[2],
                axis[3],
                preset.MotionOf(i),
                angle,
                1.0,                                      // 预览用满行程
                axis.Length > 4 ? axis[4] : 0,
                axis.Length > 5 ? axis[5] : 0,
                i,
                cycleIndex);
            _presetPreviewValues[i] = Math.Clamp(sampled * 100, 0, 100);
        }

        try
        {
            Preview3D.SetStatusText("试看中");
            Preview3D.UpdateAxes(_presetPreviewValues);
        }
        catch (Exception) { /* 预览是纯可视化，任何异常都不允许影响主流程 */ }
    }

    /// <summary>
    /// 预览下方的状态行：告诉用户右边现在看的是「设备实时」还是「某个动作的试看」。
    /// （AxisPreview3D 控件自己的角标不会因为外部喂数据而改名，所以这里补一条。）
    /// </summary>
    private void UpdatePreviewCaption()
    {
        if (PreviewCaption is null || PreviewToggleBtn is null) return;
        if (_previewPreset is { } preset)
        {
            PreviewCaption.Text = _previewPinned
                ? $"👁 试看「{preset.Label}」· 只演示，不动设备"
                : $"👁 试看「{preset.Label}」· 移开鼠标回到实时";
            PreviewCaption.Foreground = (Brush)FindResource("Accent");
            PreviewToggleBtn.Content = _previewPinned ? "✕ 停止试看" : "👁 试看选中";
        }
        else
        {
            PreviewCaption.Text = "实时输出（跟设备同步）";
            PreviewCaption.Foreground = (Brush)FindResource("Muted");
            PreviewToggleBtn.Content = "👁 试看选中";
        }
        PreviewCaption.ToolTip = PreviewCaption.Text;
        PreviewToggleBtn.IsEnabled = _previewPinned || _vm.SelectedStroke != null;
    }

    // ══════════════════════════════════════════════════════════════
    //  卡片墙
    // ══════════════════════════════════════════════════════════════

    private void RefreshCards()
    {
        if (CardPanel is null) return;

        // 卡片重建会先拆掉旧控件，悬停 / 钉住的试看要接着做下去，不能因为重建就断
        var keepPinned = _previewPinned ? _previewPreset : null;
        var keepHover  = _hoverPreset;
        bool mouseInCards = CardScroll?.IsMouseOver == true;

        StopPresetPreview();
        CardPanel.Children.Clear();

        int shown = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in _vm.FilteredPresets)
        {
            if (preset is null) continue;
            if (!seen.Add(preset.Id)) continue;      // 同 Id 只画一张（presets.json 被手改过也不出重影）
            CardPanel.Children.Add(MakeCard(preset));
            shown++;
        }
        if (shown == 0) CardPanel.Children.Add(MakeEmptyState());

        UpdateCountLabel(shown);
        UpdateEditButton();

        if (keepPinned is not null && seen.Contains(keepPinned.Id)) StartPresetPreview(keepPinned, pinned: true);
        else if (mouseInCards && keepHover is not null && seen.Contains(keepHover.Id))
            StartPresetPreview(keepHover, pinned: false);
        else if (IsVisible) StartPreviewTimer();   // 没在试看就把实时输出接回来，别让预览停在最后一帧
    }

    private Border MakeCard(StrokePreset p)
    {
        int ci = Array.IndexOf(_cats, p.Cat);
        if (ci < 0) ci = 1;
        var catColor = _catColors[Math.Min(ci, _catColors.Length - 1)];
        bool selected = _vm.SelectedStroke?.Id == p.Id;
        bool playing  = selected && _vm.Playing;
        double amp = MaxRange(p);

        var baseBack   = (Brush)FindResource(selected ? "PrimarySoft" : "CardBg");
        var baseBorder = (Brush)FindResource(selected ? "Primary" : "Border");

        var card = new Border
        {
            Width   = _cardWidth,
            Height  = CardHeight,
            Margin  = new Thickness(0, 0, CardGap, CardGap),
            Padding = new Thickness(9, 7, 9, 7),
            CornerRadius    = new CornerRadius(8),
            Background      = baseBack,
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            BorderBrush     = baseBorder,
            Cursor          = Cursors.Hand,
            ToolTip         = BuildCardTooltip(p, playing)
        };

        var sp = new StackPanel();

        // 第一行：分类色徽标 + 名称（超长用省略号，完整名字在提示里）+ 自建 / 播放中标记
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        head.Children.Add(new Border
        {
            Background = new SolidColorBrush(catColor),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = p.Cat, FontSize = 9, Foreground = Brushes.White }
        });

        var label = new TextBlock
        {
            Text = p.Label,
            FontSize = 12,
            Foreground = (Brush)FindResource("Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        head.Children.Add(label);

        if (playing || p.IsCustom)
        {
            // 卡片最窄只有 ~130px：播放中只用一个小 ▶，别把动作名挤没
            var flag = new Border
            {
                Background = (Brush)FindResource(playing ? "Primary" : "AccentSoft"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = playing ? "▶" : "自",
                    FontSize = 9,
                    Foreground = (Brush)FindResource(playing ? "OnPrimaryInk" : "Accent")
                }
            };
            Grid.SetColumn(flag, 2);
            head.Children.Add(flag);
        }
        sp.Children.Add(head);

        // 第二行：行程条（六轴里变化最大的那条）+ 百分比 + 星标
        var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 星标列

        var track = new Grid
        {
            Height = 5,
            Background = (Brush)FindResource("Surface"),
            VerticalAlignment = VerticalAlignment.Center
        };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(amp, 0.02), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - amp, 0.02), GridUnitType.Star) });
        var fill = new Border { Background = new SolidColorBrush(catColor), CornerRadius = new CornerRadius(2) };
        Grid.SetColumn(fill, 0);
        track.Children.Add(fill);
        row.Children.Add(track);

        var ampText = new TextBlock
        {
            Text = playing ? "播放中" : amp.ToString("P0"),
            FontSize = 9,
            Foreground = (Brush)FindResource(playing ? "Primary" : "Muted"),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(ampText, 1);
        row.Children.Add(ampText);

        // 星标：收藏 / 取消收藏。放在第二行最右（第一行要放类别徽标 + 名字 + 自建/播放标记，
        // 卡片最窄只有 ~124px，塞进去名字就没了）；收藏的动作在游玩页的随机列表里排最前、
        // 抽到的机会也翻倍（见 AutoBehaviorSequencer 的权重注释）。
        bool favorite = _vm.IsFavorite(p.Id);
        var star = new Button
        {
            Content   = favorite ? "★" : "☆",
            Width     = 16,
            Height    = 16,
            Padding   = new Thickness(0),
            Margin    = new Thickness(4, 0, 0, 0),
            FontSize  = 11,
            Style     = (Style)FindResource("BtnGhost"),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource(favorite ? "Accent" : "Faint"),
            Cursor    = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip   = favorite
                ? "取消收藏（收藏的动作在游玩页的「参与随机的动作」里排最前，抽到的机会也翻倍）"
                : "收藏这个动作（收藏后在游玩页更容易被自动模式抽到）",
        };
        star.Click += (_, _) =>
        {
            _vm.ToggleFavoriteCommand.Execute(p);
            RefreshCards();          // 重建卡片墙：星标状态、以及「只看收藏」筛选后的结果都要跟上
            UpdateEditButton();
        };
        Grid.SetColumn(star, 2);
        row.Children.Add(star);

        sp.Children.Add(row);

        card.Child = sp;

        card.MouseEnter += (_, _) =>
        {
            if (!selected)
            {
                card.Background  = (Brush)FindResource("SurfaceRaised");
                card.BorderBrush = (Brush)FindResource("BorderStrong");
            }
            CardMouseEnter(p);
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background  = baseBack;
            card.BorderBrush = baseBorder;
            CardMouseLeave();     // 有 150ms 宽限：在卡片之间移动时不会闪
        };
        card.MouseDown += (_, _) =>
        {
            // 点星标只收藏、不选中卡片。星标是卡片内部的按钮，MouseDown 可能冒泡到这里，
            // 所以用 IsMouseOver 判一下（不依赖"按钮会不会把事件标记成已处理"这种细节）。
            if (star.IsMouseOver) return;
            _vm.SelectStrokeCommand.Execute(p);
            RefreshCards();
            ShowDetail(p);
        };
        return card;
    }

    /// <summary>一张卡都没有时的说明（区分「筛没了」和「动作库是空的」），并给出下一步入口。</summary>
    private Border MakeEmptyState()
    {
        bool filtering = IsFiltering();
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = filtering ? "🔍 没有符合条件的动作" : "📭 动作库是空的",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            // 导入入口现在就在本页右上角（以前要跑去脚本库页找），文案跟着改
            Text = filtering
                ? "换个词试试，或者把搜索 / 分类 / 收藏 / 最近用过这几个筛选清掉。"
                : "点上面「＋ 新建动作」自己拼一个；已经有别人分享的动作文件就点「⬇ 导入动作」。",
            FontSize = 11,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        });

        if (filtering)
        {
            var resetButton = new Button
            {
                Content = "清除搜索和筛选",
                Style = (Style)FindResource("BtnSecondary"),
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            resetButton.Click += (_, _) => ResetFilters();
            panel.Children.Add(resetButton);
        }
        else
        {
            // 空库时给两个下一步入口：自己拼一个 / 导入一份（导入以前只在脚本库页，用户找不到）
            var createButton = new Button
            {
                Content = "＋ 新建动作",
                Style = (Style)FindResource("BtnPrimary"),
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
            };
            createButton.Click += (_, _) => OpenStrokeEditor(null);

            var importButton = new Button
            {
                Content = "⬇ 导入动作",
                Style = (Style)FindResource("BtnSecondary"),
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(8, 0, 0, 0),
            };
            importButton.Click += (_, _) => DoImportPresets();

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(createButton);
            row.Children.Add(importButton);
            panel.Children.Add(row);
        }

        double width = CardScroll is { ActualWidth: > 40 } scroll
            ? Math.Max(scroll.ActualWidth - 14, _cardWidth)
            : _cardWidth * 2 + CardGap;
        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(16),
            Width = width,
            Margin = new Thickness(0, 0, CardGap, CardGap),
            Child = panel
        };
    }

    private string BuildCardTooltip(StrokePreset p, bool playing)
    {
        var sb = new StringBuilder();
        sb.Append(p.Label).Append("（").Append(p.Cat).Append(p.IsCustom ? " · 自建" : " · 内置").Append("）\n");
        sb.Append("最大行程 ").Append(MaxRange(p).ToString("P0"))
          .Append("（").Append(p.Id).Append("）\n");
        for (int i = 0; i < 6; i++)
        {
            var axis = p.AllAxes[i];
            double from = axis[0], to = axis[1];
            if (Math.Abs(to - from) < 0.005) continue;
            if (to < from) (from, to) = (to, from);
            sb.Append(AxisNames[i]).Append(' ').Append(from.ToString("P0")).Append(" → ").Append(to.ToString("P0"))
              .Append("　").Append(StrokePreset.MotionLabel(p.MotionOf(i))).Append('\n');
        }
        sb.Append(playing
            ? "点一下＝立刻换成这个动作（正在播放）；要停下来就点工具条右侧的「⏹ 停止」"
            : "点一下＝选中它；鼠标停在卡片上＝在右边试看（不会让设备动）");
        sb.Append("\n点右下角的 ☆ ＝收藏（收藏的动作在游玩页的随机列表里排最前、更容易被抽到）");
        return sb.ToString();
    }

    /// <summary>六轴里变化最大那条轴的行程（0–1）：卡片行程条、详情顶部、幅度排序都用它。</summary>
    private static double MaxRange(StrokePreset p) => StrokesViewModel.MaxRange(p);

    private void UpdateEditButton()
    {
        var selected = _vm.SelectedStroke;
        EditStrokeBtn.IsEnabled   = selected != null;
        // 只有自建动作能删：内置动作删掉也会在下次启动时回来，容易让用户误以为删了
        DeleteStrokeBtn.IsEnabled = selected?.IsCustom == true;
        if (PreviewToggleBtn != null)
            PreviewToggleBtn.IsEnabled = _previewPinned || selected != null;

        // 禁用时把「为什么点不动」也写进提示里。
        // 这里用 if/else 而不是嵌套三元：三元里连续访问 _vm.SelectedStroke 的派生值，
        // 可空分析给不出"一定非空"，会报 CS8602（本项目要求 0 警告）。
        if (selected is null)
        {
            const string hint = "先在左边点一个动作，再回来看这里";
            EditStrokeBtn.ToolTip = hint;
            DeleteStrokeBtn.ToolTip = hint;
            if (PreviewToggleBtn != null) PreviewToggleBtn.ToolTip = hint;
            return;
        }

        EditStrokeBtn.ToolTip = $"改「{selected.Label}」；内置动作改完会存成一份新的自建动作，原动作不动";
        DeleteStrokeBtn.ToolTip = selected.IsCustom
            ? $"删除「{selected.Label}」（会从 presets.json 里移除，删了不能恢复）"
            : "内置动作删不掉（删了下次启动也会回来）；只能删自己新建的动作";
        if (PreviewToggleBtn != null)
            PreviewToggleBtn.ToolTip = "不用一直悬停：点一下就在这里循环演示选中的动作（只动预览，不会让设备动）";
    }

    // ══════════════════════════════════════════════════════════════
    //  选中动作详情
    // ══════════════════════════════════════════════════════════════

    private void ShowDetailEmpty()
    {
        DetailPanel.Children.Clear();
        DetailPanel.Children.Add(new TextBlock { Text = "还没选动作", FontSize = 13, FontWeight = FontWeights.SemiBold });
        DetailPanel.Children.Add(new TextBlock
        {
            Text = "在左边的卡片上点一下，这里会显示它六个轴各自怎么动。",
            FontSize = 11,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
    }

    private void ShowDetail(StrokePreset p)
    {
        DetailPanel.Children.Clear();
        int ci = Array.IndexOf(_cats, p.Cat); if (ci < 0) ci = 1;
        var catColor = _catColors[Math.Min(ci, _catColors.Length - 1)];
        double amp = MaxRange(p);

        // ① 名称（+ 自建标记）
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = p.Label, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (p.IsCustom)
        {
            var badge = MakePill("自建", (Brush)FindResource("AccentSoft"), (Brush)FindResource("Accent"));
            Grid.SetColumn(badge, 1);
            titleRow.Children.Add(badge);
        }
        DetailPanel.Children.Add(titleRow);

        // ② 类别 + 最大行程
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        sub.Children.Add(MakePill(p.Cat, new SolidColorBrush(catColor), Brushes.White));
        sub.Children.Add(new TextBlock
        {
            Text = $"最大行程 {amp:P0}",
            FontSize = 10,
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        DetailPanel.Children.Add(sub);
        DetailPanel.Children.Add(MakeDivider(new Thickness(0, 10, 0, 0)));

        // ③ 六轴：名称 / 运动方式 / 范围 + 一条跟着面板宽度伸缩的行程条
        for (int ai = 0; ai < 6; ai++)
        {
            var axis = p.AllAxes[ai];
            double from = Math.Clamp(axis[0], 0, 1);
            double to   = Math.Clamp(axis[1], 0, 1);
            if (to < from) (from, to) = (to, from);
            var color = AxisColors[ai];
            bool still = to - from < 0.005;

            var head = new Grid { Margin = new Thickness(0, ai == 0 ? 6 : 10, 0, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            head.Children.Add(new TextBlock
            {
                Text = AxisNames[ai], FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center
            });

            string motion = StrokePreset.MotionLabel(p.MotionOf(ai));
            double noiseFrom = axis.Length > 4 ? axis[4] : 0;
            double noiseTo   = axis.Length > 5 ? axis[5] : 0;
            double noise = Math.Max(noiseFrom, noiseTo);
            var middle = new TextBlock
            {
                Text = still ? "不动"
                     : noise > 0 ? $"{motion} · 抖动 {noise:0.0}"
                     : motion,
                FontSize = 10,
                Foreground = (Brush)FindResource(still ? "Faint" : "Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(middle, 1);
            head.Children.Add(middle);

            var range = new TextBlock
            {
                Text = still ? "—" : $"{from:P0} → {to:P0}",
                FontSize = 9,
                Foreground = (Brush)FindResource("Faint"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(range, 2);
            head.Children.Add(range);
            DetailPanel.Children.Add(head);

            // 轨道：前段 / 行程段 / 后段三段星号列，自动跟着面板宽度伸缩
            var trackGrid = new Grid();
            trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(from, 0.001), GridUnitType.Star) });
            trackGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(to - from, 0.02), GridUnitType.Star),
                MinWidth = still ? 0 : 2
            });
            trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - to, 0.001), GridUnitType.Star) });
            var bar = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(3),
                Opacity = still ? 0 : 1
            };
            Grid.SetColumn(bar, 1);
            trackGrid.Children.Add(bar);

            DetailPanel.Children.Add(new Border
            {
                Background = (Brush)FindResource("Surface"),
                CornerRadius = new CornerRadius(3),
                Height = 6,
                Margin = new Thickness(0, 4, 0, 0),
                ClipToBounds = true,
                Child = trackGrid
            });
        }

        // ④ 白话说明：原来的公式（mid−amp·cos…）对普通用户没有意义，换成一句人话
        DetailPanel.Children.Add(MakeDivider(new Thickness(0, 12, 0, 0)));
        DetailPanel.Children.Add(new TextBlock
        {
            Text = "正弦＝来回都平滑；抛物＝两端慢、中间快；线性＝匀速来回。"
                 + "各轴的开始时机可以错开，动作才有层次；「抖动」会给每次到顶加一点随机变化。",
            FontSize = 10,
            Foreground = (Brush)FindResource("Faint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
    }

    private Border MakePill(string text, Brush background, Brush foreground) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 2, 6, 2),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10, Foreground = foreground }
    };

    private Border MakeDivider(Thickness margin) => new()
    {
        Height = 1,
        Background = (Brush)FindResource("Border"),
        Margin = margin
    };

    // ══════════════════════════════════════════════════════════════
    //  动作库导入 / 导出
    //  与脚本库页共用 Services/StrokePresetIO.cs 里的同一套实现
    //  （选文件 → 读写 → 冲突改名 → 提示文案都在那边，两页行为因此完全一致）；
    //  这里只负责把"谁的动作库"告诉它、并在导入成功后刷新本页。
    // ══════════════════════════════════════════════════════════════

    private void ImportPresets_Click(object sender, RoutedEventArgs e) => DoImportPresets();

    /// <summary>导入动作（页头按钮与空库状态里的按钮都走这里）。</summary>
    private void DoImportPresets()
    {
        var outcome = StrokePresetIO.ImportWithDialog(
            Window.GetWindow(this), "导入动作", StrokesViewModel.ImportPresets);
        if (outcome is null) return;      // 用户取消 / 失败（那边已经提示过）

        // 导入成功：VM 已经写回 presets.json 并刷新了 FilteredPresets，
        // 这里补刷分类计数（新动作可能落在任何分类）与卡片墙。
        BuildCategoryTabs();
        RefreshCards();
        UpdateEditButton();
    }

    private void ExportPresets_Click(object sender, RoutedEventArgs e) =>
        StrokePresetIO.ExportWithDialog(
            Window.GetWindow(this), "导出动作",
            StrokesViewModel.AllPresets, StrokesViewModel.PresetExtras);

    // ══════════════════════════════════════════════════════════════
    //  动作编辑器（新建 / 编辑）
    // ══════════════════════════════════════════════════════════════

    private void NewStroke_Click(object sender, RoutedEventArgs e) => OpenStrokeEditor(null);

    private void EditStroke_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedStroke == null) return;
        OpenStrokeEditor(_vm.SelectedStroke);
    }

    private void DeleteStroke_Click(object sender, RoutedEventArgs e)
    {
        var target = _vm.SelectedStroke;
        if (target == null) return;
        if (!target.IsCustom)
        {
            System.Windows.MessageBox.Show("内置动作不能删除。", "删除动作",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 确认文案与 ScriptLibraryPage 的删除确认保持一致（带标题 + Warning 图标）
        var result = System.Windows.MessageBox.Show(
            $"确定删除自定义动作「{target.Label}」吗？\n（会从 presets.json 中移除，删除后无法恢复）",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        if (!_vm.DeletePreset(target.Id))
        {
            System.Windows.MessageBox.Show(
                "删除失败：presets.json 写入失败（程序目录只读？），动作未删除。",
                "删除动作", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _vm.SelectedStroke = null;          // 别让播放/编辑继续指向已删除的动作
        App.Settings.LastStroke = "";
        RefreshCards();
        ShowDetailEmpty();
        UpdateEditButton();
    }

    private void OpenStrokeEditor(StrokePreset? target)
    {
        var window = new StrokeEditorWindow(target) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.SavedPreset is not { } saved) return;

        // 新建 / 另存为：立刻并进全局动作库（AllPresets），否则热键「下一个 / 上一个动作」
        // 和动作编辑器的同名查重都要等重启才能看到它。
        if (window.CreatedNew && !_vm.AddOrReplacePreset(saved))
        {
            System.Windows.MessageBox.Show(
                "动作已写入 presets.json，但内存动作库刷新失败，请重启程序后使用。",
                "动作", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 只改选中项，不调用 SelectStrokeCommand —— 避免编辑保存时意外启动播放
        _vm.SelectedStroke = saved;
        App.Settings.LastStroke = saved.Id;

        RefreshCards();
        ShowDetail(saved);
        UpdateEditButton();
    }

    private void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        RestoreLivePreview();   // 开始/停止播放都要交还实时输出，别让试看数据盖住真实动作

        if (_vm.SelectedStroke == null)
        {
            System.Windows.MessageBox.Show("请先在左侧选一个动作。", "播放动作",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_vm.Playing && !App.Engine.CanRun)
        {
            System.Windows.MessageBox.Show("设备未连接或处于急停，无法播放。", "播放动作",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SyncPlayButton();
            return;
        }

        bool wasPlaying = _vm.Playing;
        _vm.TogglePlayCommand.Execute(null);

        // TogglePlayCommand 里 StartStroke 的返回值被丢弃：这里用 Playing 兜底，
        // 起不来时明确提示，而不是按钮点了没反应。
        if (!wasPlaying && !_vm.Playing)
        {
            System.Windows.MessageBox.Show("播放失败：设备未就绪或引擎拒绝了本次动作。", "播放动作",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SyncPlayButton();
        RefreshCards();
    }

    /// <summary>
    /// 「▶ 播放」按钮文案 / 样式 / 可用性的唯一刷新入口。
    /// 热键、急停、托盘、连接变化都会改 Playing / CanRun，之前只在两个 Click 里更新文案，
    /// 结果按钮显示的状态会和引擎实际状态不一致。
    /// 顺便刷新「设备没连上 / 急停」提示条。
    /// </summary>
    private void SyncPlayButton()
    {
        if (PlayBtn is null) return;
        bool playing = _vm.Playing;
        PlayBtn.Content = playing ? "⏹ 停止" : "▶ 播放";
        PlayBtn.Style   = (Style)FindResource(playing ? "BtnDanger" : "BtnPrimary");

        bool canRun = App.Engine.CanRun;
        // 开始播放还要求"选了动作"：否则按钮亮着、点下去只弹一句"请先选一个动作"（界面在骗人）。
        PlayBtn.IsEnabled = playing || (canRun && App.StrokesVm.SelectedStroke is not null);
        PlayBtn.ToolTip   = canRun ? "用当前选中的动作驱动设备；再点一下就是停止"
                                   : "设备未连接或处于急停，暂时不能播放";
        SyncDeviceHint();
    }

    /// <summary>
    /// 「现在为什么不能播放」要写在脸上：没连设备 / 急停 / 正在归中各有各的去处，
    /// 能用的功能（试看、编辑、搜索）也要说明不受影响。
    /// </summary>
    private void SyncDeviceHint()
    {
        if (DeviceHintBar == null || DeviceHintText == null) return;

        string? message = null;
        if (App.Engine.EmergencyStopped)
            message = "⚠ 急停已锁定：先点左边侧栏的「⬆ 全部归中」解锁（会先问你一次），之后才能播放。试看、编辑、搜索都不受影响。";
        else if (!App.Serial.IsOpen)
            message = "⚠ 设备没连上：现在可以浏览、试看、编辑和搜索动作，但「播放」要连上设备才能用（到「设置」页连接）。";
        else if (App.Engine.IsHoming)
            message = "⏳ 设备正在归中，等它停下来再播放。";

        DeviceHintBar.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (message is not null) DeviceHintText.Text = message;
    }
}
