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
    private const double CardHeight   = 64;   // 脚本名常常两行才放得下（2026-11-25 用户说文字看不全）

    // 脚本架默认展开：导入的脚本要一进来就能看见（收起只是个人偏好，不记忆）
    // 脚本库里的脚本直接摆在同一面卡片墙上（2026-11-25 m11673：不要单独的脚本栏，省得改来改去）
    private List<ScriptEntry> _scriptEntries = new();

    // ── 多选（2026-11-25 m11293）：一次勾好几个动作 → 一起删 / 按勾选顺序轮流播 ──
    // 顺序就是点击顺序：先点的先播，播完最后一个回到第一个，一直循环到再点一次停止。
    private readonly List<string> _multiPicked = new();          // 动作 Id，按勾选顺序
    private bool _multiMode;
    private readonly DispatcherTimer _multiSeqTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // ── 多选也能勾脚本（2026-11-25 用户：「脚本栏要像动作栏，多选对它也得有用」）──
    // 勾到的脚本和勾到的动作共用一个「删除选中 / 顺序播放」，两边序号各自从 1 排。
    private readonly List<string> _multiScriptPicked = new();                                  // 勾到的脚本路径（按勾选顺序）
    private readonly List<string> _multiScriptSeq = new();                                     // 顺序播放时的播放清单
    private readonly DispatcherTimer _multiScriptTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _multiScriptSeqIndex;
    private DateTime _multiScriptSeqStartUtc;
    private long _multiScriptSeqDurationMs;
    private readonly List<StrokePreset> _multiSeqList = new();
    private int _multiSeqIndex;
    private DateTime _multiSeqSegmentStartUtc;
    private double _multiSeqSegmentSeconds = 2.0;
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

    /// <summary>左栏那个全应用共用的六轴模型（MainWindow 里的 SharedPreview3D）。</summary>
    private static AxisPreview3D Preview3D => App.SharedPreview;
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
        else ShowDetailEmpty();

        _multiSeqTimer.Tick += (_, _) => MultiSeqTick();
        _multiScriptTimer.Tick += (_, _) => MultiScriptSeqTick();
        _shelfHoverTimer.Tick += (_, _) => EndShelfHover();
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
            if (IsVisible)
            {
                Preview3D.AutoRefreshEnabled = false;   // 本页自己喂帧（试看 / 脚本预览）
                StartPreviewTimer();
                if (_scriptPreviewTracks is not null) { _scriptPreviewWatch.Restart(); _scriptPreviewTimer.Start(); }
                LoadScriptShelf();   // 从脚本库那一栏回来时把刚导入的脚本带出来
            }
            else
            {
                StopPreviewTimer();
                StopPresetPreview();   // 页面不可见时不残留试看
                // 模型是左栏共用的：离页就交回控件自己刷设备实时，别让它停在最后一帧试看数据
                Preview3D.AutoRefreshEnabled = true;
                App.SetPreviewCaption("实时输出（跟设备同步）");
            }
        };
        Unloaded += (_, _) =>
        {
            StopPreviewTimer();
            StopPresetPreview();
            if (_scriptPreviewTimer is not null) _scriptPreviewTimer.Stop();
            Preview3D.AutoRefreshEnabled = true;
        };
        Loaded += (_, _) => { UpdateResponsiveLayout(); if (IsVisible) StartPreviewTimer(); LoadScriptShelf(); };

        // 窗口宽度变了就重算右栏宽度和卡片宽度（最小 880 窗口下正文只有约 660px）
        SizeChanged += (_, _) => UpdateResponsiveLayout();

        // 本页自己按 120ms 喂左栏那台模型（App.SharedPreview），关掉控件内置的引擎刷新，避免两个定时器打架
        Preview3D.AutoRefreshEnabled = false;

        // 卡片悬停 → 主预览试看该动作；离开卡片区延迟恢复实时输出
        CardScroll.MouseLeave += (_, _) => CardMouseLeave();
        _presetPreviewTimer.Tick += (_, _) => PresetPreviewTick();
        _scriptPreviewTimer.Tick += (_, _) => ScriptPreviewTick();
        _hoverLeaveTimer.Tick += (_, _) => RestoreLivePreview();
        _usageDwellTimer.Tick += (_, _) => NoteHoveredStrokeUsed();   // 悬停停留够久 = 用过（记进「最近用过」）
    }

    // ══════════════════════════════════════════════════════════════
    //  排版：右栏宽度 + 卡片宽度都按窗口实算
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 正文可用宽度 = 页宽 − 左右各 24 留白 − 左边详情列（270 + 10 间隙）。
    /// 剩下的全给卡片墙；卡片按「放得下几张就几张」均分，保证每张等宽且不被切。
    /// 窗口很窄时详情列也保留：它是这个页面最主要的说明区，宁可卡片一列。
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        double pageWidth = ActualWidth > 0 ? ActualWidth : 900;      // 首次布局前给个保守值
        double avail = Math.Max(pageWidth - 48 - 280, 240);

        // 20px 余量 = 竖向滚动条 8 + ScrollViewer 右侧留白 6 + 取整误差
        double cardsAvail = Math.Max(avail - 20, CardMinWidth);
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
    private void UpdateCountLabel(int shown, int shownScripts)
    {
        if (CountLabel is null) return;
        int total = StrokesViewModel.AllPresets.Length;
        int totalScripts = _scriptEntries.Count;
        bool filtering = IsFiltering() || !string.IsNullOrWhiteSpace(_vm.SearchText);
        string scriptPart = totalScripts > 0 ? $" · 脚本 {shownScripts} 个" : "";
        CountLabel.Text = filtering ? $"显示 {shown} / {total} 个{scriptPart}" : $"共 {total} 个动作{scriptPart}";
        CountLabel.Foreground = (Brush)FindResource(filtering && shown == 0 && shownScripts == 0 ? "Warning" : "Muted");
    }

    /// <summary>搜索框对脚本一视同仁：名字或文件名里含这几个字就留下（大小写不敏感）。</summary>
    private bool ScriptMatchesSearch(ScriptEntry entry)
    {
        string q = (_vm.SearchText ?? "").Trim();
        if (q.Length == 0) return true;
        if ((entry.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        string file = entry.FilePath is { Length: > 0 } path ? System.IO.Path.GetFileName(path) : "";
        return file.Contains(q, StringComparison.OrdinalIgnoreCase);
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
        // 动作和脚本都能收藏（脚本只区别于「不可删」），数量一起算
        int favoriteCount = _vm.FavoriteIds.Count + _vm.FavoriteScriptIds.Count;
        CategoryTabs.Children.Add(MakeChip(
            "⭐ 只看收藏", _vm.FavoriteOnly,
            () =>
            {
                _vm.FavoriteOnly = !_vm.FavoriteOnly;
                BuildCategoryTabs();
                RefreshCards();
            },
            favoriteCount == 0
                ? "还没有收藏的动作或脚本：在卡片右下角点一下 ☆ 就收藏了"
                : $"只看收藏过的 {favoriteCount} 个动作 / 脚本（在卡片右下角点 ★ 取消收藏）"));

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
        if (_previewPreset is not null) return;   // 正在试看动作：这一路先让位
        if (_scriptPreviewTracks is not null) return;   // 正在演选中脚本：这一路先让位
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
        _hoverLeaveTimer.Stop();
        StartPresetPreview(preset);
    }

    /// <summary>
    /// 鼠标离开整个卡片区（不是单张卡片）：延迟恢复引擎实时输出，避免在卡片之间移动时闪。
    /// 挂在 ScrollViewer 上而不是每张卡片上 —— 卡片会随搜索 / 筛选整批重建，
    /// 挂在卡片上的话重建后新旧对象对不上，实时预览会一直停在被清掉的那一帧。
    /// </summary>
    private void CardMouseLeave()
    {
        CancelUsageDwell();     // 鼠标已经离开卡片区：这不算"用过"
        _hoverLeaveTimer.Stop();
        _hoverLeaveTimer.Start();
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
    private void StartPresetPreview(StrokePreset preset)
    {
        if (!IsVisible) return;
        _previewPreset = preset;
        _presetPreviewTime = 0;
        _presetPreviewWatch.Restart();
        StopPreviewTimer();
        _presetPreviewTimer.Stop();
        _scriptPreviewTimer.Stop();             // 悬停动作卡时先让位，移开再接回来（RefreshPreview 之后的 RestoreLivePreview）
        _presetPreviewTimer.Start();
        PresetPreviewTick();                    // 立刻出一帧，别等 40ms
        UpdatePreviewCaption();
        // 悬停试看要停留够久才算"用过"（扫过一排卡片不该冲掉「最近用过」）
        StartUsageDwell(preset);
    }

    /// <summary>停止试看（含解除钉住）并交还实时输出。</summary>
    private void StopPresetPreview()
    {
        _presetPreviewTimer.Stop();
        _previewPreset = null;
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
        if (_previewPreset is not null) StopPresetPreview();
        if (_scriptPreviewTracks is not null)
        {
            // 选中脚本的预览还在：接回脚本预览，别再让引擎实时姿态盖掉它
            _scriptPreviewWatch.Restart();
            if (IsVisible) _scriptPreviewTimer.Start();
            return;
        }
        if (IsVisible) StartPreviewTimer();
    }


    // ══════════════════════════════════════════════════════════════
    //  选中脚本 → 3D 模型直接演这条脚本（50ms 一帧，循环；不碰设备）
    // ══════════════════════════════════════════════════════════════

    /// <summary>选中脚本后立刻在左栏模型里循环演示它。只做可视化，绝不往设备发指令。</summary>
    private async void StartScriptPreview(string path, string name)
    {
        _scriptPreviewName = name;
        _scriptPreviewMs = 0;
        Services.AppLogger.Info("脚本预览开始：" + name);
        _scriptPreviewTracks = null;
        _scriptPreviewTimer.Stop();
        UpdatePreviewCaption();
        try
        {
            FunscriptTrackSet tracks = await Task.Run(() => FunscriptTrackLoader.LoadCompanionSet(path));
            if (_shelfSelectedPath != path) return;      // 期间用户又点了别的脚本
            ApplyScriptPreviewTracks(tracks, name, showDetail: true);
        }
        catch (Exception ex)
        {
            _scriptPreviewTracks = null;
            App.SetPreviewCaption("脚本预览读不出来：" + ex.Message, accent: true);
        }
    }

    /// <summary>把读好的脚本挂到预览上（选中预览与悬停预览共用）。showDetail＝顺便刷左栏详情。</summary>
    private void ApplyScriptPreviewTracks(FunscriptTrackSet tracks, string name, bool showDetail)
    {
        _scriptPreviewName = name;
        _scriptPreviewTracks = tracks;
        _scriptPreviewMs = 0;
        _scriptPreviewWatch.Restart();
        StopPreviewTimer();                          // 引擎实时刷新先让位
        ScriptPreviewTick();
        if (IsVisible) _scriptPreviewTimer.Start();
        UpdatePreviewCaption();
        if (!showDetail) return;
        // 左栏详情卡跟着换成这条脚本的六轴动作（画失败不能影响预览，但要在日志里留痕）
        try { ShowScriptDetail(tracks, name); }
        catch (Exception ex) { Services.AppLogger.Warn("ShowScriptDetail 失败：" + ex.Message); }
    }

    // ── 悬停脚本架里的一块 ＝ 直接在左栏模型上演它（和动作卡片一样，2026-11-25 m11358）──
    private readonly DispatcherTimer _shelfHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(HoverLeaveDelayMs) };
    private string? _shelfHoverPath;

    private void ShelfChip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || path.Length == 0) return;
        _shelfHoverTimer.Stop();
        if (path == _shelfSelectedPath) return;     // 已选中的那条本来就在演，别重载
        if (_shelfHoverPath == path) return;
        _shelfHoverPath = path;
        StartShelfHoverPreview(path);
    }

    private void ShelfChip_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_shelfHoverPath is null) return;
        _shelfHoverTimer.Stop();
        _shelfHoverTimer.Start();                   // 150ms 宽限：在脚本块之间移动不闪
    }

    private void EndShelfHover()
    {
        _shelfHoverTimer.Stop();
        if (_shelfHoverPath is null) return;
        _shelfHoverPath = null;
        if (_shelfSelectedPath is { Length: > 0 } sel)
        {
            StartScriptPreview(sel, System.IO.Path.GetFileNameWithoutExtension(sel));   // 回到选中的那条
            return;
        }
        StopScriptPreview();                        // 没选中任何脚本：交回设备实时姿态
    }

    private async void StartShelfHoverPreview(string path)
    {
        Services.AppLogger.Info("脚本悬停预览：" + System.IO.Path.GetFileNameWithoutExtension(path));
        try
        {
            FunscriptTrackSet tracks = await Task.Run(() => FunscriptTrackLoader.LoadCompanionSet(path));
            if (_shelfHoverPath != path) return;    // 期间鼠标已移开 / 换到别的脚本
            ApplyScriptPreviewTracks(tracks, System.IO.Path.GetFileNameWithoutExtension(path), showDetail: false);
        }
        catch (Exception ex)
        {
            Services.AppLogger.Warn("脚本悬停预览失败：" + ex.Message);
        }
    }

    /// <summary>停止脚本预览并把模型交还给设备实时姿态。</summary>
    private void StopScriptPreview()
    {
        if (_scriptPreviewTracks is null && _scriptPreviewName is null) return;
        _scriptPreviewTimer.Stop();
        _scriptPreviewTracks = null;
        _scriptPreviewName = null;
        _scriptPreviewMs = 0;
        UpdatePreviewCaption();
        // 交回给动作卡：有选中动作就显示它的六轴，没有就回到占位说明
        if (ReferenceEquals(_scriptDetailOwner, this)) _scriptDetailOwner = null;
        if (_vm.SelectedStroke is { } back) ShowDetail(back);
        else ShowDetailEmpty();
        if (IsVisible) StartPreviewTimer();
        try { Preview3D?.RefreshPreview(); } catch (Exception) { }
    }

    private void ScriptPreviewTick()
    {
        FunscriptTrackSet? tracks = _scriptPreviewTracks;
        if (tracks is null || !IsVisible) return;

        double delta = _scriptPreviewWatch.Elapsed.TotalMilliseconds;
        _scriptPreviewWatch.Restart();
        if (delta < 0 || delta > 500) delta = 50;        // 卡顿后不跳大步

        long duration = tracks.DurationMs > 0 ? tracks.DurationMs : 1000;
        _scriptPreviewMs += delta;
        while (_scriptPreviewMs >= duration) _scriptPreviewMs -= duration;   // 循环演

        var order = AxisPreview3D.AxisOrder;
        for (int i = 0; i < 6; i++)
        {
            string axis = i < order.Count ? order[i] : "L0";
            _scriptPreviewValues[i] = tracks.Tracks.TryGetValue(axis, out var actions)
                ? WaveScriptCodec.SamplePos(actions, _scriptPreviewMs)
                : 50;                                     // 这条脚本没有这个轴：摆中位
        }

        try
        {
            Preview3D.SetStatusText("脚本预览");
            Preview3D.UpdateAxes(_scriptPreviewValues);
        }
        catch (Exception) { /* 预览是纯可视化，任何异常都不允许影响主流程 */ }

        // 时间码每 200ms 刷一次就够，50ms 一次会把界面刷得发烫
        if ((DateTime.UtcNow - _scriptPreviewCaptionUtc).TotalMilliseconds > 200)
        {
            _scriptPreviewCaptionUtc = DateTime.UtcNow;
            UpdatePreviewCaption();
        }
    }

    private static string Clock(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
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
    /// 左栏模型下面那行说明：现在看的是「设备实时」、「某个动作的试看」还是「选中的脚本」。
    /// （模型角标只显示固定文案，所以由这里补一条会变的说明。）
    /// </summary>
    private void UpdatePreviewCaption()
    {
        if (_scriptPreviewTracks is not null)
        {
            App.SetPreviewCaption($"🎬 预览脚本「{_scriptPreviewName}」{Clock(_scriptPreviewMs)} / {Clock(_scriptPreviewTracks.DurationMs)} · 只演给你看，不动设备", accent: true);
            return;
        }
        if (_previewPreset is { } preset)
        {
            App.SetPreviewCaption($"👁 试看「{preset.Label}」· 移开鼠标回到实时", accent: true);
            return;
        }
        App.SetPreviewCaption("实时输出（跟设备同步）");
    }

    // ══════════════════════════════════════════════════════════════
    //  卡片墙
    // ══════════════════════════════════════════════════════════════

    private void RefreshCards()
    {
        if (CardPanel is null) return;

        // 卡片重建会先拆掉旧控件，悬停中的试看要接着做下去，不能因为重建就断
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
        // 脚本和动作摆同一面墙（脚本在后，一眼能分出「📚 脚本」徽标）
        int shownScripts = 0;
        foreach (ScriptEntry entry in _scriptEntries)
        {
            if (!ScriptMatchesSearch(entry)) continue;
            if (_vm.FavoriteOnly && !_vm.IsScriptFavorite(entry.FilePath)) continue;   // 和动作一样受「只看收藏」约束
            CardPanel.Children.Add(MakeScriptCard(entry));
            shownScripts++;
        }

        if (shown == 0 && shownScripts == 0) CardPanel.Children.Add(MakeEmptyState());

        UpdateCountLabel(shown, shownScripts);
        UpdateEditButton();

        if (mouseInCards && keepHover is not null && seen.Contains(keepHover.Id))
            StartPresetPreview(keepHover);
        else if (IsVisible) StartPreviewTimer();   // 没在试看就把实时输出接回来，别让预览停在最后一帧
    }

    private Border MakeCard(StrokePreset p)
    {
        int ci = Array.IndexOf(_cats, p.Cat);
        if (ci < 0) ci = 1;
        var catColor = _catColors[Math.Min(ci, _catColors.Length - 1)];
        // 多选模式下高亮＝勾上了；单选模式下高亮＝当前选中的那个动作。两者互斥，只认一种。
        bool multiPicked = _multiMode && _multiPicked.Exists(id => string.Equals(id, p.Id, StringComparison.OrdinalIgnoreCase));
        bool selected = multiPicked || (!_multiMode && _vm.SelectedStroke?.Id == p.Id);
        bool playing  = _vm.Playing && _vm.SelectedStroke?.Id == p.Id;
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
            // 卡片高 64：名字最多折两行，长名字不再一行砍掉（和脚本卡一致）
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 30,
            LineHeight = 14.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        head.Children.Add(label);

        // 多选：勾上的卡片右上角标出它在顺序里的序号（单选时的 ▶ / 自 标记让位给序号）
        int multiOrder = _multiMode && multiPicked
            ? _multiPicked.FindIndex(id => string.Equals(id, p.Id, StringComparison.OrdinalIgnoreCase)) + 1
            : 0;
        if (multiOrder > 0)
        {
            var order = new Border
            {
                Background = (Brush)FindResource("Primary"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"第 {multiOrder} 个播（按勾选顺序）",
                Child = new TextBlock
                {
                    Text = multiOrder.ToString(),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("OnPrimaryInk")
                }
            };
            Grid.SetColumn(order, 2);
            head.Children.Add(order);
        }
        else if (playing || p.IsCustom)
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
            ClearShelfSelection();      // 动作与脚本互斥：点动作就把脚本那边的选中/预览撤掉
            if (_multiMode)
            {
                ToggleMultiPick(p);
                return;
            }
            _vm.SelectStrokeCommand.Execute(p);
            RefreshCards();
            ShowDetail(p);
            // 选中换了就要刷新「编辑 / 删除」两个键：以前这里漏了，卡片换了按钮还是上一次的样子。
            UpdateEditButton();
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
            : "点一下＝选中它；鼠标停在卡片上＝在左下角模型上试看（不会让设备动）");
        sb.Append("\n点右下角的 ☆ ＝收藏（收藏的动作在游玩页的随机列表里排最前、更容易被抽到）");
        return sb.ToString();
    }

    /// <summary>六轴里变化最大那条轴的行程（0–1）：卡片行程条、详情顶部、幅度排序都用它。</summary>
    private static double MaxRange(StrokePreset p) => StrokesViewModel.MaxRange(p);

    private void UpdateEditButton()
    {
        UpdateMultiUi();
        if (_multiMode)
        {
            // 多选模式下「编辑」不适用于单个动作，直接关掉并说明原因
            EditStrokeBtn.IsEnabled = false;
            EditStrokeBtn.ToolTip = "多选时不改单个动作：先点「☑ 多选中」回到单选";
            int customCount = MultiPickedPresets().Count(p => p.IsCustom);
            DeleteStrokeBtn.IsEnabled = _multiPicked.Count > 0 || _multiScriptPicked.Count > 0;
            DeleteStrokeBtn.ToolTip = customCount > 0
                ? $"删除勾选里的 {customCount} 个自建动作（内置动作不能删，会自动跳过）"
                : _multiPicked.Count > 0
                    ? "勾的都是内置动作：内置动作不能删，删了下次启动也会回来"
                    : "多选：先在卡片上勾要删的动作";
            return;
        }

        var selected = _vm.SelectedStroke;
        EditStrokeBtn.IsEnabled   = selected != null;
        // 内置动作不可删（用户 2026-11-25 m11606 明确）：选中的是内置动作时「🗑 删除选中」直接灰掉，
        // 只有自建动作才亮。删不删得动由这里说清，而不是点下去才弹一句「不能删」。
        DeleteStrokeBtn.IsEnabled = selected is { IsCustom: true };
        // 禁用时把「为什么点不动」也写进提示里。
        // 这里用 if/else 而不是嵌套三元：三元里连续访问 _vm.SelectedStroke 的派生值，
        // 可空分析给不出"一定非空"，会报 CS8602（本项目要求 0 警告）。
        if (selected is null)
        {
            const string hint = "先在左边点一个动作，再回来看这里";
            EditStrokeBtn.ToolTip = hint;
            DeleteStrokeBtn.ToolTip = hint;
            return;
        }

        EditStrokeBtn.ToolTip = $"改「{selected.Label}」；内置动作改完会存成一份新的自建动作，原动作不动";
        DeleteStrokeBtn.ToolTip = selected.IsCustom
            ? $"删除「{selected.Label}」（会从 presets.json 里移除，删了不能恢复）"
            : $"「{selected.Label}」是内置动作，不能删；点一下会说明怎么留一份自己的";
    }

    // ══════════════════════════════════════════════════════════════
    //  选中动作 / 脚本详情（画在主窗口左栏那块共享面板里）
    // ══════════════════════════════════════════════════════════════


    // 左栏详情卡全应用只有一块，谁正在演脚本就归谁：别的动作页实例（自检里会建两个）不能把它冲掉
    private static StrokesPage? _scriptDetailOwner;
    private bool ScriptDetailOwnsPanel => _scriptDetailOwner is not null;

    private void ShowDetailEmpty()
    {
        if (ScriptDetailOwnsPanel) return;
        DetailPanel?.Children.Clear();
        DetailPanel?.Children.Add(new TextBlock { Text = "还没选动作", FontSize = 13, FontWeight = FontWeights.SemiBold });
        DetailPanel?.Children.Add(new TextBlock
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
        if (ScriptDetailOwnsPanel) return;
        DetailPanel?.Children.Clear();
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
        DetailPanel?.Children.Add(titleRow);

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
        DetailPanel?.Children.Add(sub);
        DetailPanel?.Children.Add(MakeDivider(new Thickness(0, 10, 0, 0)));

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
            DetailPanel?.Children.Add(head);

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

            DetailPanel?.Children.Add(new Border
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
        DetailPanel?.Children.Add(MakeDivider(new Thickness(0, 12, 0, 0)));
        DetailPanel?.Children.Add(new TextBlock
        {
            Text = "正弦＝来回都平滑；抛物＝两端慢、中间快；线性＝匀速来回。"
                 + "各轴的开始时机可以错开，动作才有层次；「抖动」会给每次到顶加一点随机变化。",
            FontSize = 10,
            Foreground = (Brush)FindResource("Faint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
    }

    /// <summary>
    /// 选中「脚本库」里的脚本时，左栏详情卡按动作卡同一套画法显示这条脚本六根轴各自怎么动：
    /// 真实点位采样出来的范围 + 一条行程条 + 往返次数。只是读文件算范围，不动设备。
    /// </summary>
    private void ShowScriptDetail(FunscriptTrackSet tracks, string name)
    {
        if (DetailPanel is null) return;
        _scriptDetailOwner = this;          // 脚本详情占住左栏，动作卡片重建不许盖回来
        DetailPanel.Children.Clear();
        long durationMs = Math.Max(tracks.DurationMs, 1);
        int axisCount = tracks.Tracks.Count;

        // ① 名称
        DetailPanel?.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Text"),
            TextWrapping = TextWrapping.Wrap
        });

        // ② 「脚本」标记 + 时长 + 几根轴
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        sub.Children.Add(MakePill("脚本", (Brush)FindResource("PrimarySoft"), (Brush)FindResource("Primary")));
        sub.Children.Add(new TextBlock
        {
            Text = $"{Clock(durationMs)} · {axisCount} 轴",
            FontSize = 10,
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        DetailPanel?.Children.Add(sub);
        DetailPanel?.Children.Add(MakeDivider(new Thickness(0, 10, 0, 0)));

        // ③ 六轴：与动作卡完全同一套行结构（名称 / 手感 + 频率 / 范围 + 行程条）
        int movingAxes = 0;
        double fastestRate = 0;
        string fastestAxis = "—";
        var order = AxisPreview3D.AxisOrder;
        for (int ai = 0; ai < 6; ai++)
        {
            string axisId = ai < order.Count ? order[ai] : AxisNames[ai];
            bool has = tracks.Tracks.TryGetValue(axisId, out var actions) && actions is { Count: > 0 };

            // 采样：范围、调头次数、以及「在两端各待了多久」——后者用来推手感像正弦 / 线性 / 跳变。
            // 最多 4000 个采样点，92 分钟的脚本也不会卡界面。
            double lo = 100, hi = 0;
            int turns = 0;
            double dwellRatio = 0;
            if (has)
            {
                IReadOnlyList<WaveScriptCodec.ActionPoint> pts = actions!;
                double step = Math.Max(25, durationMs / 4000.0);
                var samples = new List<double>(4096);
                for (double ms = 0; ms <= durationMs; ms += step)
                {
                    double pos = WaveScriptCodec.SamplePos(pts, ms);
                    samples.Add(pos);
                    if (pos < lo) lo = pos;
                    if (pos > hi) hi = pos;
                }

                double span = hi - lo;
                if (samples.Count > 2 && span >= 0.5)
                {
                    // 端点区＝最上面 / 最下面 12% 的行程。正弦在端点会慢下来（驻留多），
                    // 线性匀速来回（驻留中等），几乎所有时间都在端点附近＝跳变（点位几乎瞬移）。
                    double topTh = hi - 0.12 * span, botTh = lo + 0.12 * span;
                    int dwell = 0, prevDir = 0;
                    double prev = samples[0];
                    for (int si = 1; si < samples.Count; si++)
                    {
                        double pos = samples[si];
                        if (pos >= topTh || pos <= botTh) dwell++;
                        double d = pos - prev;
                        if (Math.Abs(d) > 0.4)                 // 死区：小抖动不算一次调头
                        {
                            int dir = d > 0 ? 1 : -1;
                            if (prevDir != 0 && dir != prevDir) turns++;
                            prevDir = dir;
                        }
                        prev = pos;
                    }
                    dwellRatio = (double)dwell / samples.Count;
                }
            }

            bool still = !has || hi - lo < 0.5;
            // 「像什么运动」：脚本里只有关键帧，没有动作那种类型字段，所以按点位形状推出来。
            string shape = dwellRatio >= 0.34 ? "正弦" : dwellRatio >= 0.16 ? "线性" : "跳变";
            // 每分钟来回几次（turns 是一次方向翻转＝半个来回）
            double perMin = durationMs > 0 ? turns * 30000.0 / durationMs : 0;
            if (!still)
            {
                movingAxes++;
                if (perMin > fastestRate) { fastestRate = perMin; fastestAxis = AxisNames[ai]; }
            }
            var color = AxisColors[ai];

            var head = new Grid { Margin = new Thickness(0, ai == 0 ? 6 : 10, 0, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            head.Children.Add(new TextBlock
            {
                Text = AxisNames[ai], FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center
            });

            var middle = new TextBlock
            {
                Text = !has ? "脚本里没有这根轴"
                     : still ? "不动"
                     : turns > 0 ? $"{shape} · {perMin:0.#} 次/分"
                     : "单程（只往一个方向）",
                ToolTip = !has ? null
                     : still ? "这根轴在整条脚本里都没动"
                     : turns > 0 ? $"{shape}：约每分钟 {perMin:0.#} 个来回（平均每个来回 {60.0 / Math.Max(perMin, 0.01):0.#} 秒）"
                     : "整条脚本只朝一个方向走了一次",
                FontSize = 10,
                Foreground = (Brush)FindResource(still ? "Faint" : "Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(middle, 1);
            head.Children.Add(middle);

            var range = new TextBlock
            {
                Text = still ? "—" : $"{lo / 100:P0} → {hi / 100:P0}",
                FontSize = 9,
                Foreground = (Brush)FindResource("Faint"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(range, 2);
            head.Children.Add(range);
            DetailPanel?.Children.Add(head);

            double from = still ? 0 : Math.Clamp(lo / 100.0, 0, 1);
            double to   = still ? 1 : Math.Clamp(hi / 100.0, 0, 1);
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

            DetailPanel?.Children.Add(new Border
            {
                Background = (Brush)FindResource("Surface"),
                CornerRadius = new CornerRadius(3),
                Height = 6,
                Margin = new Thickness(0, 4, 0, 0),
                ClipToBounds = true,
                Child = trackGrid
            });
        }

        // ④ 一句人话
        DetailPanel?.Children.Add(MakeDivider(new Thickness(0, 12, 0, 0)));
        DetailPanel?.Children.Add(new TextBlock
        {
            Text = movingAxes == 0
                ? "这条脚本的六根轴都不动（可能只有一两个点，或者解析出来是空的）。"
                : $"六根轴里有 {movingAxes} 根在动，最快的是 {fastestAxis}（约 {fastestRate:0.#} 次/分）。"
                  + "「正弦 / 线性 / 跳变」是按点位形状推出来的手感：正弦＝到两端会慢下来，线性＝匀速来回，跳变＝几乎瞬间换向。"
                  + "现在只是让左栏模型演给你看；双击这块脚本（或到脚本库页点行内 ▶）才会真的让设备动。",
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

    // ══════════════════════════════════════════════════════════════
    //  多选：勾选（卡片右上角标序号）→ 一起删 / 顺序播
    // ══════════════════════════════════════════════════════════════

    /// <summary>多选里还在动作库里的那些动作（按勾选顺序）；动作被删掉后不会留下空洞。</summary>
    internal List<StrokePreset> MultiPickedPresets()
    {
        var list = new List<StrokePreset>();
        foreach (string id in _multiPicked)
        {
            var hit = StrokesViewModel.AllPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) list.Add(hit);
        }
        return list;
    }

    internal Hexa.ViewModels.StrokesViewModel Vm => _vm;
    internal bool MultiSelectMode => _multiMode;
    internal int MultiPickedCount => _multiPicked.Count;

    private void MultiSelect_Click(object sender, RoutedEventArgs e)
    {
        _multiMode = !_multiMode;
        _multiPicked.Clear();
        _multiScriptPicked.Clear();
        StopMultiSequence();
        if (_multiMode)
        {
            // 单选与多选不并存：进了多选就把单选那一个取消（否则两种高亮同时出现，用户分不清）
            _vm.SelectedStroke = null;
            if (_shelfSelectedPath is not null) { _shelfSelectedPath = null; StopScriptPreview(); }
            LoadScriptShelf();
            ShowDetailEmpty();
        }
        RefreshCards();
        SyncPlayButton();
        UpdateEditButton();
    }

    /// <summary>自检用：不进 UI 也能切换多选模式。</summary>
    internal void SetMultiSelectMode(bool on)
    {
        if (_multiMode == on) return;
        MultiSelect_Click(this, new RoutedEventArgs());
    }

    /// <summary>卡片在多选模式下的点击：勾上 / 取消（勾上时详情显示它的六轴）。</summary>
    internal void ToggleMultiPick(StrokePreset p)
    {
        int at = _multiPicked.FindIndex(id => string.Equals(id, p.Id, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _multiPicked.RemoveAt(at);
        else _multiPicked.Add(p.Id);

        if (_multiPicked.Count > 0) ShowDetail(p);
        else ShowDetailEmpty();
        RefreshCards();
        SyncPlayButton();
        UpdateEditButton();
    }

    private void UpdateMultiUi()
    {
        if (MultiSelectBtn is null) return;
        int totalPicked = _multiPicked.Count + _multiScriptPicked.Count;
        MultiSelectBtn.Content = _multiMode ? $"☑ 多选中（{totalPicked}）" : "☑ 多选";
        MultiSelectBtn.Style = (Style)FindResource(_multiMode ? "BtnPrimary" : "BtnSecondary");
        MultiSelectBtn.ToolTip = _multiMode
            ? "正在多选：点卡片或脚本＝勾上 / 取消（「第 N 个」是播放顺序），再点这里回到单选"
            : "一次挑好几个动作或脚本：勾完可以「🗑 删除选中」一起删，也可以「▶ 顺序播放」按勾选顺序轮流播";
        if (MultiHintBar is not null && MultiHint is not null)
        {
            MultiHintBar.Visibility = _multiMode ? Visibility.Visible : Visibility.Collapsed;
            MultiHint.Text = totalPicked == 0
                ? "多选中：点下面的卡片，或上面脚本栏里的脚本，＝勾上（会出现播放序号），再点一下取消。勾了动作「▶ 播放」变「▶ 顺序播放」；「🗑 删除选中」把勾到的一起删（脚本是直接从脚本库删掉）。"
                : $"多选中：已勾 {_multiPicked.Count} 个动作、{_multiScriptPicked.Count} 个脚本（序号各自从 1 排）。「▶ 顺序播放」先播动作再播脚本；「🗑 删除选中」一起删。";
        }
    }

    private void DeleteMultiPicked()
    {
        var picked = MultiPickedPresets();
        var deletable = picked.Where(p => p.IsCustom).ToList();
        var scripts = new List<string>(_multiScriptPicked);   // 勾到的脚本：直接删文件
        if (deletable.Count == 0 && scripts.Count == 0)
        {
            System.Windows.MessageBox.Show(
                picked.Count > 0
                    ? "勾的动作都是内置动作，内置动作不能删（删了下次启动也会回来）。想留自定义版，用「✏ 编辑选中」另存一份。"
                    : "还没勾东西：在多选模式下点卡片或脚本勾上要删的。",
                "删除选中", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var parts = new List<string>();
        if (deletable.Count > 0)
        {
            string names = string.Join("、", deletable.Take(6).Select(p => p.Label)) + (deletable.Count > 6 ? " 等" : "");
            parts.Add($"{deletable.Count} 个自建动作（{names}）");
        }
        if (scripts.Count > 0)
        {
            string names = string.Join("、", scripts.Take(6).Select(System.IO.Path.GetFileNameWithoutExtension))
                           + (scripts.Count > 6 ? " 等" : "");
            parts.Add($"{scripts.Count} 个脚本文件（{names}）");
        }
        var result = System.Windows.MessageBox.Show(
            $"确定删除勾选的 {string.Join("和", parts)} 吗？\n（动作从 presets.json 里移除；脚本是从脚本库目录里删文件）\n（都删了就找不回来）",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        int failed = 0;
        foreach (var p in deletable) { if (!_vm.DeletePreset(p.Id)) failed++; }

        int failedScripts = 0;
        foreach (string sp in scripts)
        {
            try { Hexa.Models.ScriptLibrary.Delete(sp); }
            catch (Exception ex) { failedScripts++; AppLogger.Warn("删除脚本失败：" + sp + "：" + ex.Message); }
        }

        if (failed > 0 || failedScripts > 0)
        {
            System.Windows.MessageBox.Show(
                $"有 {failed} 个动作、{failedScripts} 个脚本没删掉（presets.json 写不进去？脚本文件被占用？）。",
                "删除选中", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _multiPicked.Clear();
        _multiScriptPicked.Clear();
        LoadScriptShelf();
        _vm.SelectedStroke = null;
        App.Settings.LastStroke = "";
        RefreshCards();
        ShowDetailEmpty();
        UpdateEditButton();
        SyncPlayButton();
    }

    private void ToggleMultiSequence()
    {
        if (_multiSeqTimer.IsEnabled || _multiScriptTimer.IsEnabled)
        {
            bool wasScript = _multiScriptTimer.IsEnabled;
            StopMultiSequence();
            if (wasScript) App.FunscriptPlayer.Stop();
            else _vm.TogglePlayCommand.Execute(null);   // 顺序播在跑：这一下就是全停
            SyncPlayButton();
            RefreshCards();
            return;
        }

        var list = MultiPickedPresets();
        if (list.Count == 0)
        {
            if (_multiScriptPicked.Count == 0)
            {
                System.Windows.MessageBox.Show("还没勾东西：在多选模式下点卡片或脚本勾上要播的。", "顺序播放",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!EnsureCanRunForSequence()) { SyncPlayButton(); return; }
            StartScriptSequence();
            return;
        }
        if (!EnsureCanRunForSequence())
        {
            SyncPlayButton();
            return;
        }

        _multiSeqList.Clear();
        _multiSeqList.AddRange(list);
        _multiSeqIndex = 0;
        _vm.SelectStrokeCommand.Execute(_multiSeqList[0]);
        if (!_vm.Playing) _vm.TogglePlayCommand.Execute(null);
        if (!_vm.Playing)
        {
            System.Windows.MessageBox.Show("播放失败：设备未就绪或引擎拒绝了本次动作。", "顺序播放",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SyncPlayButton();
            return;
        }

        StartMultiSegment();
        SyncPlayButton();
        RefreshCards();
    }

    /// <summary>一段的时长＝该速度下 3 个来回（引擎一圈 = 1/Speed 秒），最短 0.4 秒。</summary>
    private void StartMultiSegment()
    {
        _multiSeqSegmentStartUtc = DateTime.UtcNow;
        _multiSeqSegmentSeconds = Math.Max(0.4, 3.0 / Math.Max(0.1, _vm.Speed));
        _multiSeqTimer.Start();
    }

    internal void StopMultiSequence()
    {
        _multiSeqTimer.Stop();
        _multiSeqList.Clear();
        _multiScriptTimer.Stop();
        _multiScriptSeq.Clear();
        _multiScriptSeqIndex = 0;
    }

    /// <summary>顺序播放前的同一道门：设备不在可动状态就一句话说清楚（别让按钮亮着点下去没反应）。</summary>
    private bool EnsureCanRunForSequence()
    {
        if (App.Engine.CanRun) return true;
        string reason = App.Engine.EmergencyStopped ? "设备处于急停锁定，先点侧栏「⬆ 全部归中」解锁。"
            : App.Engine.IsHoming ? "设备正在归中，等它停下来再播放。"
            : !App.Serial.IsOpen ? "设备没连上，先到「设置」页连接。"
            : "输出通道锁着，先点侧栏「⬆ 全部归中」解锁。";
        System.Windows.MessageBox.Show(reason, "顺序播放", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>顺序播放脚本：按勾选顺序一条播完自动接下一条。</summary>
    private async void StartScriptSequence()
    {
        if (!EnsureCanRunForSequence())
        {
            SyncPlayButton();
            return;
        }

        _multiScriptSeq.Clear();
        _multiScriptSeq.AddRange(_multiScriptPicked);
        _multiScriptSeqIndex = 0;
        SyncPlayButton();
        await PlayScriptSeqStepAsync();
    }

    private async Task PlayScriptSeqStepAsync()
    {
        if (_multiScriptSeqIndex < 0 || _multiScriptSeqIndex >= _multiScriptSeq.Count)
        {
            StopMultiSequence();
            SyncPlayButton();
            return;
        }

        string path = _multiScriptSeq[_multiScriptSeqIndex];
        string name = System.IO.Path.GetFileNameWithoutExtension(path);

        bool loaded = true;
        try { await Task.Run(() => App.FunscriptPlayer.Load(path)); }
        catch (Exception ex)
        {
            loaded = false;
            AppLogger.Warn("顺序播放加载失败：" + name + "：" + ex.Message);
        }

        if (!loaded || !App.FunscriptPlayer.HasTrack)
        {
            // 坏文件跳过，别让整串卡在这一条
            _multiScriptSeqIndex++;
            if (_multiScriptSeqIndex >= _multiScriptSeq.Count) { StopMultiSequence(); SyncPlayButton(); return; }
            await PlayScriptSeqStepAsync();
            return;
        }

        App.Engine.StopAll();
        App.FunscriptPlayer.Play();
        _multiScriptSeqDurationMs = App.FunscriptPlayer.DurationMs;
        _multiScriptSeqStartUtc = DateTime.UtcNow;
        _multiScriptTimer.Start();
        StartScriptPreview(path, name);   // 左栏模型跟着演正在播的那一条
        SyncPlayButton();
    }

    private void MultiScriptSeqTick()
    {
        if (!_multiScriptTimer.IsEnabled) return;
        if (!App.Engine.CanRun)
        {
            StopMultiSequence();
            App.FunscriptPlayer.Stop();
            SyncPlayButton();
            return;
        }

        double elapsed = (DateTime.UtcNow - _multiScriptSeqStartUtc).TotalMilliseconds;
        bool ended = _multiScriptSeqDurationMs > 0 && elapsed > _multiScriptSeqDurationMs + 400;
        if (!ended && App.FunscriptPlayer.IsPlaying) return;   // 还在播就等着

        _multiScriptSeqIndex++;
        if (_multiScriptSeqIndex >= _multiScriptSeq.Count)
        {
            StopMultiSequence();
            App.FunscriptPlayer.Stop();
            SyncPlayButton();
            return;
        }
        _ = PlayScriptSeqStepAsync();
    }

    private void MultiSeqTick()
    {
        if (!_multiSeqTimer.IsEnabled) return;
        // 设备停了 / 被别人接管 / 列表空了 → 收工，别让按钮一直显示"停止"却在空转
        if (!_vm.Playing || _multiSeqList.Count == 0 || !App.Engine.CanRun)
        {
            StopMultiSequence();
            SyncPlayButton();
            RefreshCards();
            return;
        }
        if ((DateTime.UtcNow - _multiSeqSegmentStartUtc).TotalSeconds < _multiSeqSegmentSeconds) return;

        int nextIndex = _multiSeqIndex + 1;
        if (nextIndex >= _multiSeqList.Count)
        {
            if (_multiScriptPicked.Count > 0)
            {
                // 动作按序号走完一遍 → 接着按序号播勾到的脚本（换播放源前要先把动作停掉，否则两路同时驱动设备）
                _multiSeqTimer.Stop();
                _multiSeqList.Clear();
                _vm.TogglePlayCommand.Execute(null);
                StartScriptSequence();
                RefreshCards();
                return;
            }
            nextIndex = 0;   // 只勾了动作：照旧一圈一圈循环
        }

        _multiSeqIndex = nextIndex;
        var next = _multiSeqList[_multiSeqIndex];
        _vm.SelectStrokeCommand.Execute(next);   // 播放中换动作：SelectStroke 内部会直接 StartStroke
        StartMultiSegment();
        ShowDetail(next);
        RefreshCards();
    }

    private void DeleteStroke_Click(object sender, RoutedEventArgs e)
    {
        if (_multiMode) { DeleteMultiPicked(); return; }

        var target = _vm.SelectedStroke;
        if (target == null)
        {
            System.Windows.MessageBox.Show(
                "还没选中动作：先在右边卡片墙里点一个动作，再回来点这里删除。\n想一次删好几个，点「☑ 多选」勾选后再删。",
                "删除动作", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!target.IsCustom)
        {
            System.Windows.MessageBox.Show(
                $"「{target.Label}」是内置动作，不能删（删了下次启动也会回来）。\n想要一份自己的版本：点「✏ 编辑选中」改完另存，它就是自建动作，可以删。",
                "删除动作", MessageBoxButton.OK, MessageBoxImage.Information);
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

        if (_multiMode) { ToggleMultiSequence(); return; }

        if (_vm.SelectedStroke == null)
        {
            System.Windows.MessageBox.Show("请先在左侧选一个动作。", "播放动作",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_vm.Playing && !App.Engine.CanRun)
        {
            // CanRun 现在把「没连设备 / 急停 / 归中 / 输出锁着」四种情况都算进去了，
            // 按真实原因给一句具体提示，别再用「设备未连接或急停」这种笼统说法糊弄人。
            string reason = App.Engine.EmergencyStopped ? "设备处于急停锁定，先点侧栏「⬆ 全部归中」解锁。"
                : App.Engine.IsHoming ? "设备正在归中，等它停下来再播放。"
                : !App.Serial.IsOpen ? "设备没连上，先到「设置」页连接。"
                : "输出通道锁着，先点侧栏「⬆ 全部归中」解锁。";
            System.Windows.MessageBox.Show(reason, "播放动作",
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

        if (_multiMode)
        {
            bool sequencing = _multiSeqTimer.IsEnabled || _multiScriptTimer.IsEnabled;
            PlayBtn.Content = sequencing ? "⏹ 停止" : "▶ 顺序播放";
            PlayBtn.Style = (Style)FindResource(sequencing ? "BtnDanger" : "BtnPrimary");
            PlayBtn.IsEnabled = sequencing || (App.Engine.CanRun && (_multiPicked.Count > 0 || _multiScriptPicked.Count > 0));
            PlayBtn.ToolTip = sequencing
                ? "正在按勾选顺序播；再点一下全停"
                : $"按序号轮流播（动作每个 3 个来回，脚本一条播完接下一条），勾了 {_multiPicked.Count} 个动作、{_multiScriptPicked.Count} 个脚本";
            SyncDeviceHint();
            return;
        }

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
    /// 「现在为什么不能播放」要写在脸上：没连设备 / 急停 / 正在归中 / 输出锁着 / 没选动作，各有各的去处，
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
        else if (!App.Serial.OutputEnabled)
            // 设备连上了、也没急停/归中，但输出闸门还关着（比如刚经历过板子重启恢复的瞬间窗口）：
            // 这状态下帧会被串口层静默丢弃、机器不会动，必须说清楚，而不是让播放按钮「点了没反应」。
            message = "⚠ 输出锁着：设备已连上，但输出通道还没解锁，先点左边侧栏的「⬆ 全部归中」解锁，之后才能播放。试看、编辑、搜索都不受影响。";
        else if (App.StrokesVm.SelectedStroke is null)
            // 设备没问题、输出也解锁，但还没选动作：播放按钮是灰的，得说明原因，别让人以为软件坏了。
            message = "⚠ 还没选动作：在左侧点一张动作卡片选中它，右上角「▶ 播放」才会亮。试看、编辑、搜索都不受影响。";

        DeviceHintBar.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (message is not null) DeviceHintText.Text = message;
    }

    // ══ 页签：动作 ←→ 脚本库（2026-11-24 用户要求把脚本库并进这一页）═══════════════
    // ScriptLibraryPage 本身就是 Page，塞不进 Grid，所以用 Frame 承载；第一次点才构造，
    // 不点就不占内存（脚本库要扫库目录、读 funscript 头）。
    private ScriptLibraryPage? _libraryPage;
    private bool _libraryShown;

    private void TabStrokes_Click(object sender, RoutedEventArgs e) => ShowLibrary(false);
    private void TabLibrary_Click(object sender, RoutedEventArgs e) => ShowLibrary(true);

    /// <summary>外部要直接打开脚本库（例如上次停在脚本库的旧设置）时用这个。</summary>
    public void ShowScriptLibrary() => ShowLibrary(true);

    private void ShowLibrary(bool show)
    {
        if (show && _libraryPage is null)
        {
            _libraryPage = new ScriptLibraryPage();
            // Frame 只能放 Page，而且 Navigate 是排队异步的（切栏那一拍 Content 还空着）
            LibraryHost.Navigate(_libraryPage);
        }

        _libraryShown = show;
        LibraryHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        StrokePane.Visibility  = show ? Visibility.Collapsed : Visibility.Visible;

        // 当前那一栏用主色按钮，另一栏用次要样式 —— 一眼看出自己在哪
        TabStrokesBtn.Style = (Style)FindResource(show ? "BtnSecondary" : "BtnPrimary");
        TabLibraryBtn.Style = (Style)FindResource(show ? "BtnPrimary" : "BtnSecondary");
    }

    /// <summary>当前是不是停在脚本库那一栏（自检和外部调用判断用）。</summary>
    public bool IsLibraryShown => _libraryShown;

    /// <summary>右侧 3D 模型现在是不是在演「脚本架里选中的那条脚本」（自检/诊断用）。</summary>
    internal bool ScriptPreviewActive => _scriptPreviewTracks is not null;

    /// <summary>脚本架上被选中的脚本（null＝没选）。单击只选中，不跳页；播放走「▶ 播放选中」或双击。</summary>
    private string? _shelfSelectedPath;

    // ── 选中脚本后，右边那台 3D 模型直接把这条脚本的动作演一遍（纯预览，绝不碰设备）──
    // 用户 2026-11-25：「为什么选中没有 3d 建模预览没展示动作」——之前只有悬停动作卡才试看，
    // 选脚本只高亮了一下，模型还停在设备实时姿态上。
    // ⚠ 这个 Tick 必须在构造里挂上：之前只在声明处 new 出来、没订阅 Tick，
    // 结果脚本预览只被喂了第 0 帧，模型端着不动（2026-11-25 用户报「预览不会动起来」）。
    private readonly DispatcherTimer _scriptPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly System.Diagnostics.Stopwatch _scriptPreviewWatch = new();
    private readonly double[] _scriptPreviewValues = { 50, 50, 50, 50, 50, 50 };
    private FunscriptTrackSet? _scriptPreviewTracks;   // 非 null = 正在演这条脚本
    private string? _scriptPreviewName;
    private double _scriptPreviewMs;
    private DateTime _scriptPreviewCaptionUtc = DateTime.MinValue;

    // ══════════════════════════════════════════════════════════════════
    // 脚本架：把脚本库里的脚本（含刚导入的）直接列在动作栏
    // 单击＝在这一排里选中它（不跳页）；双击 或 「▶ 播放选中」＝直接载入播放
    // ══════════════════════════════════════════════════════════════════
    /// <summary>
    /// 重新读一遍脚本库目录，把它们当成卡片摆进同一面墙（2026-11-25 m11673：不要单独的脚本栏）。
    /// 读失败不算致命：动作照常显示，只是墙上少几张脚本卡。
    /// </summary>
    private void LoadScriptShelf()
    {
        List<ScriptEntry> entries;
        try
        {
            entries = ScriptLibrary.List();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[脚本库] 读目录失败：" + ex.Message);
            entries = new List<ScriptEntry>();
        }

        // 勾过 / 选中的脚本可能被删、改名：对不上的就摘掉，别让按钮还认着一个不存在的文件
        _multiScriptPicked.RemoveAll(p => entries.All(e => e.FilePath != p));
        if (_shelfSelectedPath is { } picked && entries.All(e => e.FilePath != picked)) _shelfSelectedPath = null;
        if (_shelfSelectedPath is null && _scriptPreviewTracks is not null) StopScriptPreview();

        _scriptEntries = entries;
        RefreshCards();
    }

    /// <summary>
    /// 一块脚本卡片：和动作卡片同尺寸、摆在同一面墙上（2026-11-25 m11673「直接在动作栏里显示」）。
    /// 单击＝选中并让左栏模型演它；双击＝直接让设备播；多选模式下单击＝勾上。
    /// </summary>
    private Border MakeScriptCard(ScriptEntry entry)
    {
        string path = entry.FilePath ?? "";
        int multiOrder = _multiMode && path.Length > 0
            ? _multiScriptPicked.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) + 1
            : 0;
        bool selected = multiOrder > 0 || (path.Length > 0 && path == _shelfSelectedPath);
        bool playing = selected && App.FunscriptPlayer.IsPlaying
            && string.Equals(App.FunscriptPlayer.LoadedFile ?? "", path, StringComparison.OrdinalIgnoreCase);
        bool favorite = _vm.IsScriptFavorite(path);

        var baseBack = (Brush)FindResource(selected ? "PrimarySoft" : entry.Valid ? "CardBg" : "DangerSoft");
        var baseBorder = (Brush)FindResource(selected ? "Primary" : entry.Valid ? "Border" : "BorderStrong");

        // 和内置动作卡同构（用户 2026-11-25 m11795：「脚本选项和内置动作选项没区别，只区别于不可删」）：
        // 一样的尺寸、一样的徽标/名字/星标/序号/播放标记，就是不能从这里删掉。
        var card = new Border
        {
            Width = _cardWidth,
            Height = CardHeight,
            Margin = new Thickness(0, 0, CardGap, CardGap),
            Padding = new Thickness(9, 7, 9, 7),
            CornerRadius = new CornerRadius(8),
            Background = baseBack,
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            BorderBrush = baseBorder,
            Cursor = Cursors.Hand,
            Tag = path,
            Uid = "script",     // 自检用来区分「这是脚本卡」和动作卡
            ToolTip = entry.Valid
                ? entry.Name + "\n脚本 · 时长 " + TimeSpan.FromMilliseconds(entry.DurationMs).ToString(@"m\:ss")
                  + $" · {entry.TrackCount} 轴\n{System.IO.Path.GetFileName(path)}\n鼠标停在这里＝在左栏模型上演它；单击＝选中；双击＝让设备直接播（脚本不能在这里删，去脚本库页）"
                : entry.Name + "\n无法解析：" + (entry.Error ?? "未知原因"),
        };
        if (!entry.Valid) card.Opacity = 0.72;

        var sp = new StackPanel();

        // 第一行：📚 脚本 徽标 + 名字（最多两行）+ 多选序号 / 播放中
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Background = (Brush)FindResource(entry.Valid ? "PrimarySoft" : "DangerSoft"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.Valid ? "📚 脚本" : "⚠ 脚本",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(entry.Valid ? "Primary" : "Warning"),
            },
        };
        Grid.SetColumn(badge, 0);
        head.Children.Add(badge);

        var name = new TextBlock
        {
            Text = entry.Name,
            FontSize = 12,
            Foreground = (Brush)FindResource(entry.Valid ? "Text" : "Warning"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 30,
            LineHeight = 14.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        if (multiOrder > 0)
        {
            var order = new Border
            {
                Background = (Brush)FindResource("Primary"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"第 {multiOrder} 个播（按勾选顺序）",
                Child = new TextBlock
                {
                    Text = multiOrder.ToString(),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("OnPrimaryInk"),
                },
            };
            Grid.SetColumn(order, 2);
            head.Children.Add(order);
        }
        else if (playing)
        {
            var flag = new Border
            {
                Background = (Brush)FindResource("Primary"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "▶",
                    FontSize = 9,
                    Foreground = (Brush)FindResource("OnPrimaryInk"),
                },
            };
            Grid.SetColumn(flag, 2);
            head.Children.Add(flag);
        }
        sp.Children.Add(head);

        // 第二行：细条（有几根轴就占多少）+ 时长/轴数 + 星标（收藏）
        var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        double filled = entry.Valid ? Math.Clamp(entry.TrackCount / 6.0, 0.08, 1.0) : 0.08;
        var track = new Grid
        {
            Height = 5,
            Background = (Brush)FindResource("Surface"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(filled, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - filled + 0.02, GridUnitType.Star) });
        var fillBar = new Border
        {
            Background = (Brush)FindResource(entry.Valid ? "Primary" : "Danger"),
            CornerRadius = new CornerRadius(2),
        };
        Grid.SetColumn(fillBar, 0);
        track.Children.Add(fillBar);
        row.Children.Add(track);

        var metaText = new TextBlock
        {
            Text = entry.Valid
                ? (playing ? "播放中" : TimeSpan.FromMilliseconds(entry.DurationMs).ToString(@"m\:ss") + $" · {entry.TrackCount} 轴")
                : "无法解析",
            FontSize = 9,
            Foreground = (Brush)FindResource(playing ? "Primary" : "Muted"),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(metaText, 1);
        row.Children.Add(metaText);

        var star = new Button
        {
            Content = favorite ? "★" : "☆",
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 11,
            Style = (Style)FindResource("BtnGhost"),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource(favorite ? "Accent" : "Faint"),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = favorite ? "取消收藏这个脚本" : "收藏这个脚本（和动作一样能在「⭐ 只看收藏」里筛出来）",
        };
        star.Click += (_, _) =>
        {
            _vm.ToggleScriptFavorite(path);
            LoadScriptShelf();      // 重建卡片墙：星标状态与「只看收藏」筛选都要跟上
            UpdateEditButton();
        };
        Grid.SetColumn(star, 2);
        row.Children.Add(star);

        sp.Children.Add(row);
        card.Child = sp;

        card.MouseLeftButtonUp += ShelfChip_MouseUp;
        card.MouseEnter += (_, _) =>
        {
            if (!selected)
            {
                card.Background = (Brush)FindResource("SurfaceRaised");
                card.BorderBrush = (Brush)FindResource("BorderStrong");
            }
            ShelfChip_MouseEnter(card, null!);      // 这里只用 sender.Tag，事件参数不参与
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = baseBack;
            card.BorderBrush = baseBorder;
            ShelfChip_MouseLeave(card, null!);
        };
        return card;
    }

    /// <summary>点到卡片里的按钮（星标）就把事件让给按钮，别当成点卡片。</summary>
    private static bool IsInsideButton(object? source)
    {
        var node = source as System.Windows.DependencyObject;
        while (node is not null)
        {
            if (node is System.Windows.Controls.Button) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void ShelfChip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || path.Length == 0) return;
        // 星标是卡片里的小按钮：点它只收藏，不算选中这条脚本
        if (IsInsideButton(e.OriginalSource)) return;
        // 多选模式下点脚本＝勾上 / 取消（和动作卡片一样），不跳页、也不去播
        if (_multiMode) { ToggleScriptPick(path); return; }
        if (e.ClickCount >= 2)
        {
            // 双击＝直接播（先选中再播，播放中被删也看得出是哪一条）
            bool same = _shelfSelectedPath == path;
            _shelfSelectedPath = path;
            LoadScriptShelf();
            if (!same) StartScriptPreview(path, System.IO.Path.GetFileNameWithoutExtension(path));
            PlaySelectedShelf();
            return;
        }
        // 单击＝就地选中：留在动作页，不跳脚本库（再点同一个＝取消选中）
        SelectShelfScript(path);
    }

    internal int MultiPickedScriptCount => _multiScriptPicked.Count;

    /// <summary>多选模式下勾 / 取消一个脚本；勾上时左栏模型跟着演它，方便挑。</summary>
    internal void ToggleScriptPick(string path)
    {
        if (path.Length == 0) return;
        int at = _multiScriptPicked.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _multiScriptPicked.RemoveAt(at);
        else _multiScriptPicked.Add(path);

        LoadScriptShelf();
        SyncPlayButton();
        UpdateEditButton();

        if (_multiScriptPicked.Count == 0) { StopScriptPreview(); return; }
        string show = _multiScriptPicked.FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                      ?? _multiScriptPicked[0];
        StartScriptPreview(show, System.IO.Path.GetFileNameWithoutExtension(show));
    }

    /// <summary>动作与脚本互斥：选了动作就把脚本这边的选中与预览一起撤掉（2026-11-25 m11293）。</summary>
    private void ClearShelfSelection()
    {
        _shelfHoverPath = null;
        _shelfHoverTimer.Stop();
        if (_shelfSelectedPath is null && _scriptPreviewTracks is null) return;
        _shelfSelectedPath = null;
        StopScriptPreview();
        LoadScriptShelf();
    }

    /// <summary>
    /// 脚本架里选中 / 取消选中一个脚本：选中＝高亮那一块 + 左栏 3D 模型开始演它，全程不切页。
    /// 单击和自检都走这里，保证测的就是用户点的那条路。
    /// </summary>
    internal void SelectShelfScript(string path)
    {
        if (path.Length == 0) return;
        // 动作与脚本互斥（2026-11-25 m11293）：只要碰脚本，多选和动作那边的选中就一起撤掉，
        // 否则两张卡同时高亮、详情面板被两边来回覆盖，用户看到的永远是脚本那一份。
        // 放在最前面（连「再点一下取消选中」也走这里），不然取消选中时多选状态会残留。
        if (_multiMode)
        {
            _multiMode = false;
            _multiPicked.Clear();
            StopMultiSequence();
        }
        if (_vm.SelectedStroke is not null)
        {
            _vm.SelectedStroke = null;
            App.Settings.LastStroke = "";
        }
        if (_shelfSelectedPath == path)
        {
            _shelfSelectedPath = null;
            LoadScriptShelf();
            RefreshCards();
            SyncPlayButton();
            UpdateEditButton();
            return;
        }
        _shelfSelectedPath = path;
        LoadScriptShelf();
        RefreshCards();
        SyncPlayButton();
        UpdateEditButton();
        StartScriptPreview(path, System.IO.Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// 播放脚本架上选中的那个脚本（入口只有双击/自检；单独那个「▶ 播放选中」按钮按用户要求去掉了）。
    /// 检查与「脚本库」页一致：先清自动/动作，再确认还能逐帧下发。
    /// </summary>
    internal void PlaySelectedShelf() => _ = PlaySelectedShelfAsync();

    private async Task PlaySelectedShelfAsync()
    {
        if (_shelfSelectedPath is not { Length: > 0 } path)
        {
            System.Windows.MessageBox.Show("先在下面这一排里点一个脚本，再双击它播放。", "还没选脚本",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await Task.Run(() => App.FunscriptPlayer.Load(path));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("播放失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        App.Engine.StopAll();
        if (!App.Engine.CanAcceptDirectInput)
        {
            string reason = App.FunscriptPlayer.DirectInputBlockReason ?? "设备当前不接受逐帧下发";
            string hint = App.Engine.RuleEngineActive
                ? "\n\n声音响应 / 游戏伴随正在接管，请先到「测试台」关掉再播。"
                : "";
            System.Windows.MessageBox.Show($"无法播放脚本：{reason}。{hint}", "无法播放",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StopScriptPreview();          // 真要播了：模型改成跟设备实时走，别和本地预览打架
        App.FunscriptPlayer.Play();
        SyncPlayButton();
    }

/// <summary>动作栏里直接导入脚本：和脚本库那一栏走同一条导入路径（伴生轴文件、同名覆盖确认都一样）。</summary>
    private async void ShelfImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入脚本（可多选）",
            Filter = "funscript 脚本 (*.funscript)|*.funscript|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sources = ScriptLibrary.ExpandCompanionFiles(dlg.FileNames);
            var conflicts = ScriptLibrary.ConflictingTargets(sources);
            bool overwrite = false;
            if (conflicts.Count > 0)
            {
                string names = string.Join("、", conflicts.Take(5).Select(dest => System.IO.Path.GetFileNameWithoutExtension(dest)));
                if (conflicts.Count > 5) names += $" 等 {conflicts.Count} 个";
                if (System.Windows.MessageBox.Show($"库内已有同名脚本：{names}。覆盖它们？", "覆盖确认",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                overwrite = true;
            }

            var summary = ScriptLibrary.ImportMany(sources, overwrite);
            LoadScriptShelf();
            _libraryPage?.ReloadFromDisk();
            if (summary.Scripts.Count > 0)
                Services.AppLogger.Info($"导入 {summary.Scripts.Count} 个脚本，已经摆进动作墙（双击就能播）。");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导入失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
