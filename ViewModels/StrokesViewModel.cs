using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexa.Models;
using Hexa.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hexa.ViewModels;

public partial class StrokesViewModel : ObservableObject
{
    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;

    // 当前存活的 VM 实例：ImportPresetsJson 是静态入口，导入后靠它刷新 FilteredPresets
    private static StrokesViewModel? _instance;

    // 模型里没有的附加字段（noise / motion 等），按最终 Id 暂存，导出时原样带回
    private static Dictionary<string, JsonObject> _presetExtras = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _playing;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private string _selectedCategory = "全部";
    /// <summary>动作页搜索框关键字（匹配显示名 / 标识 / 分类，忽略大小写）。</summary>
    [ObservableProperty] private string _searchText = "";
    /// <summary>只看自己新建的动作（动作页「只看自建」开关）。</summary>
    [ObservableProperty] private bool _customOnly;
    /// <summary>只看收藏过的动作（动作页「⭐ 只看收藏」开关）。</summary>
    [ObservableProperty] private bool _favoriteOnly;
    /// <summary>只看最近用过的动作（动作页「🕘 最近用过」开关；打开时按新旧排，最新在前）。</summary>
    [ObservableProperty] private bool _recentOnly;
    /// <summary>按行程从大到小排（动作页「幅度排序」开关）；false = 保持动作库原有顺序。</summary>
    [ObservableProperty] private bool _sortByAmplitude;
    [ObservableProperty] private StrokePreset? _selectedStroke;
    [ObservableProperty] private ObservableCollection<StrokePreset> _filteredPresets = new();

    /// <summary>「最近用过」最多留多少个（与 AppSettings.Normalize 的上限一致）。</summary>
    private const int RecentStrokeLimit = 12;

    public static string[] Categories => ["全部", "推", "滚", "长", "碾", "挑", "涡"];

    // ── 预设加载：优先读 <app_dir>/presets.json，不存在则用内置数组并写出文件 ──
    private static readonly string PresetsPath = System.IO.Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "presets.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented           = true,
        PropertyNameCaseInsensitive = true,
    };

    // 预设集合：导入时整体替换为新数组（copy-on-write），读取方拿到的始终是稳定快照
    private static StrokePreset[] _allPresets = LoadPresets();

    /// <summary>全部动作预设（静态快照；导入后指向新数组，现有调用方无需改动）。</summary>
    public static StrokePreset[] AllPresets => _allPresets;

    private static StrokePreset[] LoadPresets()
    {
        bool loadFailed = false;

        if (System.IO.File.Exists(PresetsPath))
        {
            try
            {
                var text = System.IO.File.ReadAllText(PresetsPath);
                var loaded = JsonSerializer.Deserialize<StrokePreset[]>(text, _jsonOpts);
                if (loaded is { Length: > 0 })
                {
                    var valid = loaded.Where(preset => preset.Normalize())
                        .GroupBy(preset => preset.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToArray();
                    if (valid.Length > 0) return valid;
                    throw new InvalidDataException("文件里没有任何可用动作（id 缺失或字段格式不正确）");
                }
                throw new InvalidDataException("文件内容为空或不是动作数组");
            }
            catch (Exception ex)
            {
                // 【P0】解析失败绝不覆盖原文件：先把坏文件改名备份，再回退内置动作。
                // 只有「备份成功」或「备份失败但原文件仍在」两种情况，后面都不会写盘。
                loadFailed = true;
                ReportPresetsLoadFailure(ex, BackupBrokenPresetsFile());
            }
        }

        // 内置预设：只有 presets.json 完全不存在时才写出（首次运行）；
        // 解析失败路径靠 loadFailed 兜底，任何情况下都不会把用户文件覆盖成内置数组。
        var builtin = BuiltinPresets;
        foreach (var preset in builtin) preset.Normalize();
        if (!loadFailed && !System.IO.File.Exists(PresetsPath))
        {
            try { System.IO.File.WriteAllText(PresetsPath, JsonSerializer.Serialize(builtin, _jsonOpts)); }
            catch { }
        }
        return builtin;
    }

    /// <summary>
    /// 把解析失败的 presets.json 改名备份为 presets.json.bad-yyyyMMddHHmmss，返回备份后的完整路径。
    /// 备份本身失败（文件被占用等）时返回 null —— 此时原文件保持不动，调用方也不会再写盘覆盖它。
    /// </summary>
    private static string? BackupBrokenPresetsFile()
    {
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        foreach (string suffix in new[] { "", "-1", "-2", "-3" })
        {
            string backup = PresetsPath + ".bad-" + stamp + suffix;
            try
            {
                if (System.IO.File.Exists(backup)) continue;   // 同一秒已经备份过 → 换个后缀
                System.IO.File.Move(PresetsPath, backup);
                return backup;
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// 告知用户 presets.json 解析失败（原因）、坏文件备份到哪、本次使用内置动作。
    /// LoadPresets 由静态字段初始化触发，可能在非 UI 线程执行，所以先切到 UI 线程再弹框；
    /// 用 InvokeAsync 而不是 Invoke，避免 UI 线程正在等类型初始化锁时死锁。
    /// </summary>
    private static void ReportPresetsLoadFailure(Exception ex, string? backupPath)
    {
        string reason = ex.Message;
        string message = backupPath != null
            ? $"presets.json 解析失败（{reason}），已备份为 {System.IO.Path.GetFileName(backupPath)}，本次使用内置动作。"
            : $"presets.json 解析失败（{reason}），备份失败（原文件未改动），本次使用内置动作。";

        void Show() =>
            System.Windows.MessageBox.Show(message, "动作预设",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;          // 没有 UI（单元测试/设计期）：只回退，不弹框
        if (dispatcher.CheckAccess()) Show();
        else dispatcher.InvokeAsync((Action)Show);
    }

    // ── 内置预设：已按运动学等价性精简，去掉近重复项（保留各类别经典动作）──
    private static StrokePreset[] BuiltinPresets =>
    [
        new() { Id="down-forward",Label="向下前推",Cat="推",L0=[0.0,1.0,0.0,.5],L1=[.8,.2,1.0,.8],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.2,.8,1.0,.8]},
        new() { Id="down-backward",Label="向下后退",Cat="推",L0=[0.0,1.0,0.0,.5],L1=[.2,.8,1.0,.8],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.8,.2,1.0,.8]},
        new() { Id="back-thrust-down",Label="后拉下压",Cat="推",L0=[0.0,.7,0.0,.5],L1=[1.0,0,0,.5],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.4,1.0,0,.5]},
        new() { Id="back-thrust-down-swirl",Label="后拉下压旋",Cat="涡",L0=[0.0,.7,0.0,.5],L1=[.9,.1,0,.5],L2=[.8,.2,1.0,.5],R0=[.5,.5,0,0],R1=[.8,.2,1.0,.5],R2=[.5,1.0,0,.5]},
        new() { Id="thrust-forward",Label="直推前进",Cat="推",L0=[0.0,.5,0.0,.5],L1=[.2,1.0,0,.5],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.5,1.0,0,.5]},
        new() { Id="thrust-forward-swirl",Label="直推前进旋",Cat="涡",L0=[0.0,.5,0.0,.5],L1=[.2,1.0,0,.5],L2=[.8,.2,1.0,0],R0=[.5,.5,0,0],R1=[.8,.2,1.0,0],R2=[.5,1.0,0,.5]},
        new() { Id="lean-forward-thrust-down",Label="前倾下推",Cat="推",L0=[0.0,.7,0.0,.5],L1=[.5,.5,0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[1.0,0,0,.5]},
        new() { Id="lean-forward-thrust-down-swirl",Label="前倾下推旋",Cat="涡",L0=[0.0,.7,0.0,.5],L1=[.5,.5,0,0],L2=[.8,.2,1.0,.5],R0=[.5,.5,0,0],R1=[.8,.2,1.0,.5],R2=[1.0,0,0,.5]},
        new() { Id="diagonal-down-back",Label="斜角下后",Cat="推",L0=[0.0,.5,0.0,.2],L1=[1.0,.2,0,.2],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[1.0,0,1.0,.6]},
        new() { Id="diagonal-down-forward",Label="斜角下前",Cat="推",L0=[0.0,.5,0.0,.2],L1=[0,.8,0,.2],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[0,1.0,1.0,.6]},
        new() { Id="orbit-tease",Label="轨道挑逗",Cat="挑",L0=[.8,1.0,0,.3],L1=[.9,.1,0,-.3],L2=[.1,.9,1.0,-.3],R0=[.5,.5,0,0],R1=[.1,.9,1.0,-.3],R2=[.1,.9,0,-.3]},
        new() { Id="left-right-tease",Label="左右挑逗",Cat="挑",L0=[.9,.9,0,0],L1=[.5,.5,0,0],L2=[0,1.0,0,0],R0=[.5,.5,0,0],R1=[1.0,0,1.0,0],R2=[.5,.5,0,0]},
        new() { Id="forward-back-tease",Label="前后挑逗",Cat="挑",L0=[.9,.9,0,0],L1=[0,1.0,0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[0,1.0,1.0,0]},
        new() { Id="vortex-tease",Label="漩涡挑逗",Cat="挑",L0=[.8,1.0,0,.3],L1=[.6,.4,0,0],L2=[.4,.6,1.0,0],R0=[.5,.5,0,0],R1=[.9,.1,1.0,0],R2=[.9,.1,0,0]},
        new() { Id="swirl-tease",Label="旋涡挑逗",Cat="挑",L0=[.5,1.0,0,0],L1=[1.0,.3,0,0],L2=[1.0,0,1.0,0],R0=[.5,.5,0,0],R1=[.9,.1,1.0,0],R2=[.4,1.0,0,0]},
        new() { Id="forward-back-grind",Label="前后碾压",Cat="碾",L0=[0,0,0,0],L1=[.3,.7,0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[0,1.0,.5,0]},
        new() { Id="orbit-grind",Label="轨道碾压",Cat="碾",L0=[0,.3,0,.3],L1=[0,.6,0,-.3],L2=[.2,.8,1.0,-.3],R0=[.5,.5,0,0],R1=[.1,.9,1.0,-.3],R2=[.9,.1,0,-.3]},
        new() { Id="short-low-roll-forward",Label="短低滚前",Cat="滚",L0=[0,.5,0,.25],L1=[.5,.5,1.0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.4,.6,1.0,0]},
        new() { Id="short-mid-roll-forward",Label="短中滚前",Cat="滚",L0=[.25,.75,0,.25],L1=[.5,.5,1.0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.4,.6,1.0,0]},
        new() { Id="short-high-roll-forward",Label="短高滚前",Cat="滚",L0=[.5,1.0,0,.25],L1=[.5,.5,1.0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.6,.4,1.0,0]},
        new() { Id="long-stroke-3",Label="长行程3",Cat="长",L0=[0,.7,0,0],L1=[0,1.0,0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[1.0,.3,0,.5]},
        new() { Id="long-stroke-4",Label="长行程4",Cat="长",L0=[0,.7,0,0],L1=[.6,.4,0,0],L2=[.6,.4,1.0,0],R0=[.5,.5,0,0],R1=[.7,.3,1.0,0],R2=[.5,1.0,0,.25]},
        new() { Id="long-stroke-5",Label="长行程5",Cat="长",L0=[0,.8,0,0],L1=[.4,.6,0,0],L2=[.4,.6,1.0,0],R0=[.5,.5,0,0],R1=[.3,.7,1.0,0],R2=[.5,1.0,0,.25]},
        new() { Id="grind-circular",Label="圆碾压",Cat="碾",L0=[0,0,0,0],L1=[.7,.3,0,0],L2=[.3,.7,1.0,0],R0=[.5,.5,0,0],R1=[.3,.7,1.0,0],R2=[.3,.7,0,0]},
        new() { Id="grind-vortex",Label="漩涡碾压",Cat="碾",L0=[0,0,0,0],L1=[.7,.3,0,0],L2=[.7,.3,1.0,0],R0=[.5,.5,0,0],R1=[.7,.3,1.0,0],R2=[.3,.7,0,0]},
        new() { Id="grind-forward-back-phased",Label="前后碾压相",Cat="碾",L0=[0,.1,0,0],L1=[0,1.0,0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.3,.7,1.0,0]},
        new() { Id="grind-forward-back-tilt",Label="前后碾压倾",Cat="碾",L0=[0,.2,0,0],L1=[.2,.8,0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.7,0,0,0]},
        new() { Id="grind-forward-tilt",Label="前倾碾压",Cat="碾",L0=[0,.2,0,0],L1=[.3,.3,0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.1,.7,0,0]},
        new() { Id="tease-left-right-rock",Label="左右摇挑逗",Cat="挑",L0=[.8,.8,0,0],L1=[.5,.5,0,0],L2=[.9,.1,0,0],R0=[.5,.5,0,0],R1=[1.0,0,0,0],R2=[.5,.5,0,0]},
        new() { Id="tease-down-back",Label="下后挑逗",Cat="挑",L0=[.8,1.0,0,0],L1=[.2,.8,1.0,0],L2=[.5,.5,0,0],R0=[.5,.5,0,0],R1=[.5,.5,0,0],R2=[.9,.3,1.0,0]},
        new() { Id="tease-up-down-circle-right",Label="上下圈右挑逗",Cat="挑",L0=[.7,1.0,0,0],L1=[.5,.5,0,0],L2=[.3,.7,1.0,0],R0=[.5,.5,0,0],R1=[.3,.7,1.0,0],R2=[.8,.8,0,0]},
        new() { Id="tease-up-down-circle-left",Label="上下圈左挑逗",Cat="挑",L0=[.7,1.0,0,0],L1=[.5,.5,0,0],L2=[.7,.3,1.0,0],R0=[.5,.5,0,0],R1=[.7,.3,1.0,0],R2=[.8,.8,0,0]},
    ];

    public StrokesViewModel(MotionEngine engine, AppSettings cfg)
    {
        _engine = engine;
        _cfg = cfg;
        _instance = this;   // 供静态导入入口回调刷新列表
        Speed = cfg.Speed;
        RefreshFiltered();

        // ── 把动作库接到自动模式上（本次最大修复的接线处）──
        // 以前随机池只认硬编码的 8 个内置波形，动作页里自建 / 改过的动作永远抽不到；
        // 现在池子会读这份动作库（只取「自建」那批），并读收藏 / 最近用过算权重。
        // 提供器在动作页 VM 构造时装上（App 启动时就会构造），断线期间序列器退回读 presets.json。
        AutoBehaviorSequencer.SetCustomStrokesProvider(() => AllPresets);
        AutoBehaviorSequencer.SetFavoriteIdsProvider(() => cfg.FavoriteStrokes);
        AutoBehaviorSequencer.SetRecentIdsProvider(() => cfg.RecentStrokes);
        AutoBehaviorSequencer.SetUsageReporter(NoteStrokeUsed);   // 被自动抽到的自建动作也进「最近用过」

        // Restore last selected
        if (!string.IsNullOrEmpty(cfg.LastStroke))
            SelectedStroke = AllPresets.FirstOrDefault(p => p.Id == cfg.LastStroke);
        _engine.StateChanged += () => App.Dispatch(() => Playing = _engine.StrokeRunning);
        Playing = _engine.StrokeRunning;
    }

    partial void OnSelectedCategoryChanged(string value) => RefreshFiltered();
    partial void OnSearchTextChanged(string value) => RefreshFiltered();
    partial void OnCustomOnlyChanged(bool value) => RefreshFiltered();
    partial void OnFavoriteOnlyChanged(bool value) => RefreshFiltered();
    partial void OnRecentOnlyChanged(bool value) => RefreshFiltered();
    partial void OnSortByAmplitudeChanged(bool value) => RefreshFiltered();

    // ══════════════════════════════════════════════════════════════
    //  我的常用：收藏 / 最近用过（唯一真源 = AppSettings，与游玩页共用一份）
    // ══════════════════════════════════════════════════════════════

    /// <summary>这个动作收藏了没有（卡片上的星标、随机权重都读它）。</summary>
    public bool IsFavorite(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && (_cfg.FavoriteStrokes ?? []).Any(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>收藏的动作 id（只读快照：动作页用来显示数量、给卡片画星标）。</summary>
    public IReadOnlyList<string> FavoriteIds => _cfg.FavoriteStrokes ?? [];

    /// <summary>「最近用过」的记录条数。</summary>
    public int RecentCount => (_cfg.RecentStrokes ?? []).Count;

    /// <summary>
    /// 收藏 / 取消收藏。写的是 AppSettings.FavoriteStrokes（动作页筛选、游玩页随机列表排序、
    /// 序列器权重三处共用），并走节流写盘 —— 不会点一下就落一次盘。
    /// </summary>
    [RelayCommand]
    private void ToggleFavorite(StrokePreset? stroke)
    {
        if (stroke is null || string.IsNullOrWhiteSpace(stroke.Id)) return;
        var list = new List<string>(_cfg.FavoriteStrokes ?? []);
        int index = list.FindIndex(item => string.Equals(item, stroke.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) list.RemoveAt(index);
        else list.Add(stroke.Id);

        // 整体替换引用：音频线程（序列器算权重）读到的永远是完整快照
        _cfg.FavoriteStrokes = list;
        QueueUsageSave();
        RefreshFiltered();      // 「只看收藏」开着时取消收藏要立刻从墙上消失
    }

    /// <summary>
    /// 记一次「用过这个动作」：试看（悬停超过半秒 / 钉住试看）、播放、被自动模式抽到都算。
    /// 去重、最新的排最前、最多留 <see cref="RecentStrokeLimit"/> 个；写盘走节流。
    /// 序列器在音频线程抽到自建动作时也会调这里，所以先切回 UI 线程再改集合。
    /// </summary>
    public void NoteStrokeUsed(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (App.Dispatch(() => ApplyUsage(id))) return;
        ApplyUsage(id);   // 没有 UI（自检 / 单测）：直接改，反正没人画界面
    }

    private void ApplyUsage(string id)
    {
        var list = new List<string>(_cfg.RecentStrokes ?? []);
        list.RemoveAll(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, id);
        if (list.Count > RecentStrokeLimit)
            list.RemoveRange(RecentStrokeLimit, list.Count - RecentStrokeLimit);
        _cfg.RecentStrokes = list;
        QueueUsageSave();
    }

    /// <summary>「最近用过」的 id → 新旧序号（0 = 最近），供筛选与排序。</summary>
    private Dictionary<string, int> RecentOrder()
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var recent = _cfg.RecentStrokes ?? [];
        for (int i = 0; i < recent.Count; i++)
        {
            string id = (recent[i] ?? "").Trim();
            if (id.Length > 0) order.TryAdd(id, i);
        }
        return order;
    }

    private HashSet<string> FavoriteIdSet() =>
        new(_cfg.FavoriteStrokes ?? [], StringComparer.OrdinalIgnoreCase);

    // ── 节流写盘（与游玩页参数面板同一套思路：停手 700ms 才写一次）──
    private System.Windows.Threading.DispatcherTimer? _usageSaveTimer;

    private void QueueUsageSave()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _cfg.Save();
            return;
        }
        if (_usageSaveTimer is null)
        {
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(700),
            };
            timer.Tick += (_, _) => { timer.Stop(); _cfg.Save(); };
            _usageSaveTimer = timer;
        }
        _usageSaveTimer.Stop();
        _usageSaveTimer.Start();
    }

    /// <summary>
    /// 按「分类 → 只看自建 / 只看收藏 / 最近用过 → 关键字 → 排序」组装当前要显示的卡片列表。
    /// 只影响动作页显示顺序，不动 AllPresets（热键「下一个 / 上一个动作」仍按动作库原顺序走）。
    /// </summary>
    private void RefreshFiltered()
    {
        string keyword = SearchText?.Trim() ?? "";
        var favorites = FavoriteOnly ? FavoriteIdSet() : null;
        var recent = RecentOnly ? RecentOrder() : null;

        var result = new List<StrokePreset>();
        foreach (var p in AllPresets)
        {
            if (SelectedCategory != "全部" && p.Cat != SelectedCategory) continue;
            if (CustomOnly && !p.IsCustom) continue;
            if (favorites is not null && !favorites.Contains(p.Id ?? "")) continue;
            if (recent is not null && !recent.ContainsKey(p.Id ?? "")) continue;
            if (keyword.Length > 0 && !Matches(p, keyword)) continue;
            result.Add(p);
        }

        // 排序：最近用过 → 按新旧（这个筛选的用处就是"接着上次继续"，所以它优先于幅度排序）
        //       OrderBy 是稳定排序：行程一样大的动作保持动作库里的先后
        if (recent is not null)
            result = result
                .OrderBy(p => recent.TryGetValue(p.Id ?? "", out int index) ? index : int.MaxValue)
                .ToList();
        else if (SortByAmplitude)
            result = result.OrderByDescending(MaxRange).ToList();

        FilteredPresets.Clear();
        foreach (var p in result) FilteredPresets.Add(p);
    }

    /// <summary>六轴里变化最大那条轴的行程（0–1）：卡片行程条、幅度排序都用它。</summary>
    public static double MaxRange(StrokePreset preset)
    {
        double max = 0;
        foreach (var axis in preset.AllAxes) max = Math.Max(max, Math.Abs(axis[1] - axis[0]));
        return Math.Clamp(max, 0, 1);
    }

    /// <summary>关键字匹配显示名 / 标识 / 分类（忽略大小写）。</summary>
    private static bool Matches(StrokePreset preset, string keyword) =>
        (preset.Label?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
        || (preset.Id?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
        || (preset.Cat?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>用当前 AllPresets 重建 FilteredPresets（导入/删除预设后调用，自动切到 UI 线程）。</summary>
    public void RefreshPresets()
    {
        App.Dispatch(() =>
        {
            RefreshFiltered();
            // StrokesPage 被导航栈缓存，只在 SelectedStroke 变化时重建卡片 → 主动通知一次刷新
            OnPropertyChanged(nameof(SelectedStroke));
        });
    }

    /// <summary>
    /// 新增或按 Id 覆盖一个动作（动作编辑器保存成功后调用）：先落盘，再 copy-on-write 替换
    /// _allPresets 并刷新列表 —— 与 ImportPresetsJson 同一套路径。这样全局热键
    /// 「下一个 / 上一个动作」、动作编辑器同名查重、编排页都能立刻看到它，不必重启程序。
    /// </summary>
    /// <returns>false = 写盘失败，内存集合保持原样。</returns>
    public bool AddOrReplacePreset(StrokePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var merged = _allPresets.ToList();
        int index = merged.FindIndex(p => string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) merged[index] = preset;   // 同名覆盖：保持原有位置
        else merged.Add(preset);                  // 新动作：追加到末尾

        var snapshot = merged.ToArray();
        if (!StrokePresetStore.SaveAll(snapshot)) return false;   // 写盘失败 → 内存不变
        _allPresets = snapshot;
        RefreshPresets();
        return true;
    }

    /// <summary>
    /// 删除一个自定义动作（内置动作拒绝删除，IsCustom == false 直接返回 false）：
    /// 先落盘，再 copy-on-write 替换 _allPresets 并刷新列表；写盘失败则整体不生效。
    /// </summary>
    /// <returns>false = 不是自建动作 / id 不存在 / 写盘失败。</returns>
    public bool DeletePreset(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        var target = _allPresets.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (target is not { IsCustom: true }) return false;       // 只允许删自建动作

        var snapshot = _allPresets.Where(p => !ReferenceEquals(p, target)).ToArray();
        if (!StrokePresetStore.SaveAll(snapshot)) return false;   // 写盘失败 → 内存不变

        _allPresets = snapshot;
        if (string.Equals(SelectedStroke?.Id, id, StringComparison.OrdinalIgnoreCase))
            SelectedStroke = null;                                // 别让播放/编辑指向已删除的动作
        RefreshPresets();
        return true;
    }

    [RelayCommand]
    private void SelectStroke(StrokePreset stroke)
    {
        SelectedStroke = stroke;
        _cfg.LastStroke = stroke.Id;
        NoteStrokeUsed(stroke.Id);          // 「最近用过」：点选就算用过（含播放中直接换动作）
        if (_engine.StrokeRunning) _engine.StartStroke(stroke);
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (_engine.StrokeRunning)
        {
            _engine.StopStroke();
        }
        else
        {
            if (SelectedStroke == null) return;
            _engine.ActiveMode = MotionMode.Stroke;
            _engine.StartStroke(SelectedStroke);
            NoteStrokeUsed(SelectedStroke.Id);   // 「最近用过」：真的驱动了设备，一定算用过
        }
        Playing = _engine.StrokeRunning;
    }

    [RelayCommand] private void Home() { _engine.Home(); Playing = _engine.StrokeRunning; }

    partial void OnSpeedChanged(double value) { _engine.Speed = value; _cfg.Speed = value; }

    // ══════════════════════════════════════════════════════════════════
    //  动作库 JSON 导入 / 导出
    //  实现搬到了 Services/StrokePresetIO.cs（动作页与脚本库页共用同一套），
    //  这里保留原来的两个静态入口，既有调用方（脚本库页等）一个字都不用改。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>模型里没有的附加字段（noise / motion）：导入时暂存，导出时原样带回。</summary>
    public static IReadOnlyDictionary<string, JsonObject> PresetExtras => _presetExtras;

    /// <summary>把当前全部动作预设序列化为 JSON 文本（[{name,type,data}]）。</summary>
    public static string ExportPresetsJson() =>
        StrokePresetIO.ExportToJson(_allPresets, _presetExtras);

    /// <summary>
    /// 解析 JSON 并合并进现有动作预设集合，写回 presets.json 并刷新列表。
    /// Id / 名称冲突时自动追加 -2 / -3 后缀。
    /// </summary>
    /// <exception cref="FormatException">JSON 语法或结构不正确（中文说明）。</exception>
    /// <exception cref="IOException">合并成功但写回 presets.json 失败（中文说明）。</exception>
    public static StrokePresetIO.ImportOutcome ImportPresets(string json)
    {
        var outcome = StrokePresetIO.ImportFromJson(json, _allPresets, _presetExtras);
        // 先落盘再替换内存：写文件失败则本次导入整体不生效，避免界面与 presets.json 不一致
        SavePresets(outcome.Presets);
        _allPresets   = outcome.Presets;
        _presetExtras = outcome.Extras;
        _instance?.RefreshPresets();
        return outcome;
    }

    /// <summary>兼容旧签名（脚本库页仍在用）：返回新增 / 改名数量。</summary>
    public static (int added, int renamed, List<string> renamedNames) ImportPresetsJson(string json)
    {
        var outcome = ImportPresets(json);
        return (outcome.Added, outcome.Renamed, outcome.RenamedNames);
    }

    private static void SavePresets(StrokePreset[] presets)
    {
        try
        {
            System.IO.File.WriteAllText(PresetsPath, JsonSerializer.Serialize(presets, _jsonOpts));
        }
        catch (Exception ex)
        {
            throw new IOException($"动作库写入 presets.json 失败：{ex.Message}", ex);
        }
    }

    public void NextStroke(int dir)
    {
        var all = AllPresets;
        int idx = Array.FindIndex(all, p => p.Id == SelectedStroke?.Id);
        if (idx < 0) idx = 0;
        else idx = (idx + dir + all.Length) % all.Length;
        SelectStroke(all[idx]);
    }
}
