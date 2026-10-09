using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hexa.Models;
using Hexa.Services;
using Hexa.ViewModels;
using Microsoft.Win32;

namespace Hexa.Views;

public partial class ScriptLibraryPage : Page
{
    private sealed class ScriptRow
    {
        public string FilePath { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>mm:ss，解析失败为「—」。</summary>
        public string Duration { get; init; } = "";
        /// <summary>会驱动哪些轴（「全部 6 轴」或「L0+R0」这样的轴清单）。</summary>
        public string Axes { get; init; } = "";
        public string Size { get; init; } = "";
        /// <summary>来源：脚本制作（本软件按轴导出）/ 多轴（多根轴都有动作）/ 单文件（只有一根轴）。</summary>
        public string Source { get; init; } = "";
        public bool Valid { get; init; }
        /// <summary>解析失败原因（有效脚本为 null）。</summary>
        public string? Error { get; init; }
        /// <summary>整行悬停提示：写明时长/轨道/大小/来源；无效脚本写明失败原因。</summary>
        public string Tip { get; init; } = "";
        /// <summary>名称后面的章节/书签标记（「📑3🔖2」；没有就是空字符串）。</summary>
        public string Marker { get; init; } = "";
        /// <summary>标记的悬停提示（为什么带标记、点了能看到什么）。</summary>
        public string MarkerTip { get; init; } = "";
    }

    /// <summary>
    /// 章节/书签列表里的一项。章节和书签共用一种条目（都只是「一个时间点 + 一个名字」），
    /// 用 <see cref="IsChapter"/> 区分图标和提示文字。
    /// </summary>
    private sealed class ChapterItem
    {
        public string Label { get; init; } = "";
        public string Detail { get; init; } = "";
        /// <summary>秒。</summary>
        public double Seconds { get; init; }
        public bool IsChapter { get; init; }
    }

    // 固定列宽与 XAML 一致（时长 56 / 轨道 66 / 大小 56 / 来源 62 / 操作 80）；
    // 名称列 = 视口宽 − 固定列 − 边框/内边距/滚动条。560px 窗口下也要能整行显示，不能把「操作」列挤出可视区。
    private const double DurationColumnWidth = 56;
    private const double AxesColumnWidth     = 66;
    private const double SizeColumnWidth     = 56;
    private const double SourceColumnWidth   = 62;
    private const double ActionsColumnWidth  = 80;
    private const double NameColumnMinWidth  = 130;
    /// <summary>App.xaml 里 ScrollBar 样式的宽度（不是 SystemParameters 的 17）。</summary>
    private const double ScrollBarWidth      = 8;
    /// <summary>A-B 循环的检查节拍（毫秒）。进度刷新的 150ms 太粗，冲过 B 点会明显「多走一截」。</summary>
    private const int AbLoopTickMs           = 40;
    /// <summary>
    /// 为了列表里的章节/书签标记，最多愿意多读多大的文件（4 MB）。
    /// 脚本库刷新是逐文件跑的，为了一个多半不存在的装饰把几十 MB 全读一遍不值得。
    /// </summary>
    private const long MaxMetadataProbeBytes = 4 * 1024 * 1024;

    private readonly Action _playerStatusRelay;
    private readonly Action _engineStatusRelay;
    /// <summary>滑杆拖动时不要每 1 像素写一次盘：停手 400ms 再存。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
    /// <summary>播放中每 150ms 刷一次进度：播放器只在状态变化时广播 StatusChanged，进度条不会自己动。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _progressTimer;
    /// <summary>自动匹配脚本的结果只显示几秒，之后自己收起来（免得常驻一行过期信息）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _noteTimer;

    private int _scriptCount;
    private bool _refreshing;
    private bool _statusHooked;
    /// <summary>正在按设置初始化界面（此时不要回写设置）。</summary>
    private bool _loadingUi;
    /// <summary>最近一次「播放被拒」的原因，播放控制卡常显（成功开始播放后清空）。</summary>
    private string? _blockedReason;

    // ── 媒体同步（外挂播放器时间码从动）──────────────────────────────
    /// <summary>
    /// 同步服务。**懒创建**：页面被构造时 App 还没初始化完（自检会直接 new 这个页面），
    /// 所以要到用户真的点「连接」才建它 —— 也就天然满足了「没点连接就不许自动连播放器」。
    /// </summary>
    private MediaSyncService? _mediaSync;
    /// <summary>正在拖进度条（拖动期间同步循环不抢位置，松手才交还）。</summary>
    private bool _scrubbing;
    /// <summary>拖动中准备跳到的秒数（只用于读数显示，真正的跳转已经交给播放器）。</summary>
    private double _scrubSeconds;
    /// <summary>章节列表当前属于哪个脚本（避免同一次选择重复解析文件）。</summary>
    private string? _chapterPath;

    // ── A-B 循环 ─────────────────────────────────────────────────────
    /// <summary>循环起点（秒）；null = 没设。</summary>
    private double? _abLoopA;
    /// <summary>循环终点（秒）；null = 不循环（只有 A 时是「从 A 播到片尾」）。</summary>
    private double? _abLoopB;

    public ScriptLibraryPage()
    {
        InitializeComponent();
        FooterText.Text = "正在读取脚本库…";

        _playerStatusRelay = () => App.Dispatch(RefreshStatus);
        _engineStatusRelay = () => App.Dispatch(RefreshStatus);

        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); App.Settings.Save(); };

        _progressTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        // 心跳里先查 A-B 循环再刷界面：A-B 是有时限要求的（冲过 B 点会多走一截），
        // 界面刷新只是好看，顺序反了也不影响。见 TickPlaybackTimer。
        _progressTimer.Tick += (_, _) => TickPlaybackTimer();

        _noteTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _noteTimer.Tick += (_, _) =>
        {
            _noteTimer.Stop();
            MediaSyncNoteText.Visibility = Visibility.Collapsed;
        };

        InitEnhanceControls();
        InitGenerateReadouts();

        // 构造函数不能 await：改由 Loaded 事件触发首次（后台）读取。
        // 页面被 MainWindow 缓存复用：订阅成对放在 Loaded/Unloaded，离开页面后不再被引擎线程唤醒刷 UI。
        Loaded += async (_, _) =>
        {
            HookStatusEvents();
            RefreshRateLabel();
            LoadEnhanceControls();
            InitMediaSyncControls();
            UpdateFolderHint();
            await RefreshListAsync();
        };
        Unloaded += (_, _) =>
        {
            UnhookStatusEvents();
            // 拖着滑杆就离开页面时，把还没落盘的那次改动补上（顺带让定时器不再持有本页）
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); App.Settings.Save(); }
            // 页面被缓存复用，离开就别再每 150ms 刷一次。
            // 例外：A-B 循环 / 媒体同步还在跑 —— 那是用户明确设置的**持续行为**，跟界面在不在没关系，
            // 一离开页面就悄悄失效才是真的坑。
            if (_abLoopB is null && _mediaSync?.IsRunning != true) _progressTimer.Stop();
        };

        // 名称列吃掉剩余宽度：最大化时右侧不再空出上千像素，窄窗口也不会把时长/轨道列挤出可视区。
        ScriptList.SizeChanged += (_, _) => ResizeNameColumn();
    }

    /// <summary>
    /// 播放增强区（多轴联动 / 空档填缝 / 脚本平滑）的事件接线。构造函数里只接线、不读设置：
    /// 「Loaded」时设置一定已经就绪（页面可能比 App.Settings 先被构造出来）。
    /// </summary>
    private void InitEnhanceControls()
    {
        LinkCheck.Checked += (_, _) => ApplyLinkToggle(true);
        LinkCheck.Unchecked += (_, _) => ApplyLinkToggle(false);
        LinkAmountSlider.ValueChanged += (_, e) =>
        {
            LinkAmountLabel.Text = $"{e.NewValue:0}%";
            if (_loadingUi) return;
            App.Settings.ScriptAxisLinkAmount = e.NewValue;
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        GapFillCheck.Checked += (_, _) => ApplyGapFillToggle(true);
        GapFillCheck.Unchecked += (_, _) => ApplyGapFillToggle(false);
        GapMinSlider.ValueChanged += (_, e) =>
        {
            GapMinLabel.Text = $"{e.NewValue:0.0} 秒";
            if (_loadingUi) return;
            // 播放器发现这个值变了会重算空档表（见 FunscriptPlayerService.EnsureGaps）。
            App.Settings.ScriptGapFillMinMs = (int)Math.Round(e.NewValue * 1000);
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        GapRangeSlider.ValueChanged += (_, e) =>
        {
            GapRangeLabel.Text = $"±{e.NewValue:0}%";
            if (_loadingUi) return;
            App.Settings.ScriptGapFillRange = e.NewValue;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        SmoothCheck.Checked += (_, _) => ApplySmoothToggle(true);
        SmoothCheck.Unchecked += (_, _) => ApplySmoothToggle(false);
        SmoothStrengthSlider.ValueChanged += (_, e) =>
        {
            SmoothStrengthLabel.Text = $"{e.NewValue:0}%";
            if (_loadingUi) return;
            App.Settings.ScriptSmoothingStrength = e.NewValue;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    /// <summary>
    /// 「从音频生成」面板里两个滑杆的读数（原来只在 XAML 里写死了 55% / 1.0，拖滑杆数字不动，
    /// 用户根本不知道当前用的是多少）。这两个值只在点「开始生成」时读一次，不涉及设置项、不动设备。
    /// </summary>
    private void InitGenerateReadouts()
    {
        GenerateAmpLabel.Text = $"{GenerateAmpSlider.Value:0}%";
        GenerateSensLabel.Text = $"{GenerateSensSlider.Value:0.0}";
        GenerateAmpSlider.ValueChanged += (_, e) => GenerateAmpLabel.Text = $"{e.NewValue:0}%";
        GenerateSensSlider.ValueChanged += (_, e) => GenerateSensLabel.Text = $"{e.NewValue:0.0}";
    }

    private void ApplySmoothToggle(bool enabled)
    {
        if (_loadingUi) return;
        App.Settings.ScriptSmoothingEnabled = enabled;
        SmoothStrengthSlider.IsEnabled = enabled;
        App.Settings.Save();
        RefreshStatus();
    }

    private void ApplyLinkToggle(bool enabled)
    {
        if (_loadingUi) return;
        App.Settings.ScriptAxisLinkEnabled = enabled;
        LinkAmountSlider.IsEnabled = enabled;      // 没开就没必要调幅度，省得用户以为调了会生效
        App.Settings.Save();
        RefreshStatus();
        UpdateEnhanceNote();
    }

    /// <summary>
    /// 空档填缝要能听到声音，但完整的「声音响应」会接管设备（和脚本播放互斥），
    /// 所以这里开的是「只监听、不驱动设备」那条通道。
    /// </summary>
    private void ApplyGapFillToggle(bool enabled)
    {
        if (_loadingUi) return;
        App.Settings.ScriptGapFillEnabled = enabled;
        App.Settings.AudioListenOnly = enabled;
        GapMinSlider.IsEnabled = enabled;
        GapRangeSlider.IsEnabled = enabled;
        App.Settings.Save();
        App.AudioReactive.Refresh();    // 这个开关决定要不要采集系统声音（填缝靠它拿事件）
        RefreshStatus();
        UpdateEnhanceNote();
    }

    /// <summary>把设置读进界面（离开页面再回来也要是用户上次勾的样子）。</summary>
    private void LoadEnhanceControls()
    {
        _loadingUi = true;
        try
        {
            var cfg = App.Settings;
            LinkCheck.IsChecked = cfg.ScriptAxisLinkEnabled;
            LinkAmountSlider.Value = cfg.ScriptAxisLinkAmount;
            LinkAmountLabel.Text = $"{cfg.ScriptAxisLinkAmount:0}%";
            LinkAmountSlider.IsEnabled = cfg.ScriptAxisLinkEnabled;
            GapFillCheck.IsChecked = cfg.ScriptGapFillEnabled;
            GapMinSlider.Value = Math.Clamp(cfg.ScriptGapFillMinMs / 1000.0, GapMinSlider.Minimum, GapMinSlider.Maximum);
            GapMinLabel.Text = $"{GapMinSlider.Value:0.0} 秒";
            GapRangeSlider.Value = Math.Clamp(cfg.ScriptGapFillRange, GapRangeSlider.Minimum, GapRangeSlider.Maximum);
            GapRangeLabel.Text = $"±{GapRangeSlider.Value:0}%";
            GapMinSlider.IsEnabled = cfg.ScriptGapFillEnabled;
            GapRangeSlider.IsEnabled = cfg.ScriptGapFillEnabled;
            SmoothCheck.IsChecked = cfg.ScriptSmoothingEnabled;
            SmoothStrengthSlider.Value = Math.Clamp(cfg.ScriptSmoothingStrength,
                SmoothStrengthSlider.Minimum, SmoothStrengthSlider.Maximum);
            SmoothStrengthLabel.Text = $"{SmoothStrengthSlider.Value:0}%";
            SmoothStrengthSlider.IsEnabled = cfg.ScriptSmoothingEnabled;

        }
        finally { _loadingUi = false; }
        UpdateEnhanceNote();
    }


    /// <summary>
    /// 勾了却不生效时把原因写出来（沉默的开关最让人困惑）：
    /// ①「多轴动作」总开关是关的 → 出口门控会把次要轴钉回中位；② 声音还没采到（设备/权限/静音）。
    /// </summary>
    private void UpdateEnhanceNote()
    {
        var notes = new List<string>();
        bool anyEnabled = LinkCheck.IsChecked == true || GapFillCheck.IsChecked == true;
        if (anyEnabled && !App.Settings.MotionMultiAxis)
            notes.Add("「测试台 → 声音响应 → 多轴动作语汇」总开关是关的：现在只有主轴会动，联动和填缝的次要轴都会被钉回中位。要用就去测试台把它打开。");

        if (GapFillCheck.IsChecked == true && !App.AudioReactive.Capturing)
            notes.Add("「空档填缝」需要一个能听到声音的通道，但当前没在监听（已尝试开始监听）。它只监听系统播放出来的声音（耳机/音箱里放的），不采集麦克风。");

        string text = string.Join("\n", notes);
        EnhanceNote.Text = text;
        EnhanceNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>订阅播放器 / 引擎状态（幂等）：急停、全部归中、连接、规则引擎接管都会改变播放状态与按钮。</summary>
    private void HookStatusEvents()
    {
        if (_statusHooked) return;
        _statusHooked = true;
        App.FunscriptPlayer.StatusChanged += _playerStatusRelay;
        App.Engine.StateChanged           += _engineStatusRelay;
    }

    private void UnhookStatusEvents()
    {
        if (!_statusHooked) return;
        _statusHooked = false;
        App.FunscriptPlayer.StatusChanged -= _playerStatusRelay;
        App.Engine.StateChanged           -= _engineStatusRelay;
    }

    /// <summary>在后台线程枚举并全量解析库内脚本，避免阻塞 UI；目录/IO 异常只提示不崩溃。</summary>
    private async Task RefreshListAsync(string? selectPath = null)
    {
        if (_refreshing) return;
        _refreshing = true;
        string? keepSelection = selectPath ?? Selected?.FilePath;
        FooterText.Text = "正在读取脚本库…";
        try
        {
            var rows = await Task.Run(() => ScriptLibrary.List()
                .Select(BuildRow)
                .ToList());
            _scriptCount = rows.Count;
            ScriptList.ItemsSource = rows;
            // 刷新/改名后把选中行找回来，别让用户重新点一遍
            if (keepSelection != null) SelectRowByPath(keepSelection);
            PlayBtn.IsEnabled = Selected is { Valid: true };
            // 名称列里的 📑/🔖 标记是这次重排出来的：章节按钮也要跟着当前选中行走
            // （ItemsSource 换了之后 SelectionChanged 不一定再触发，所以这里显式来一次）
            LoadChaptersFor(Selected?.FilePath);
            // 没选中就灰掉：点了才弹"请先选择"属于"按钮看起来能用但没用"
            ResizeNameColumn();
            UpdateEmptyState();
            UpdateFolderHint();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            FooterText.Text = "读取脚本库失败：" + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>把一条库内记录整理成列表行（只在后台线程调用）。</summary>
    private static ScriptRow BuildRow(ScriptEntry entry)
    {
        string axes = DescribeAxes(entry, out int axisCount);
        string source = DescribeSource(entry, axisCount);
        string size = FormatSize(FileSize(entry.FilePath));
        string duration = entry.Valid ? FormatClock(entry.DurationMs) : "—";
        string marker = DescribeMarker(entry, out string markerTip);
        return new ScriptRow
        {
            FilePath = entry.FilePath,
            Name = entry.Name,
            Duration = duration,
            Axes = entry.Valid ? axes : "—",
            Size = size,
            Source = source,
            Valid = entry.Valid,
            Error = entry.Error,
            Marker = marker,
            MarkerTip = markerTip,
            // 解析失败的行把原因写进悬停提示，用户不用猜为什么时长/轨道是「—」。
            Tip = entry.Valid
                ? $"{entry.Name}\n时长 {duration} · 轨道 {axes} · 大小 {size} · 来源 {source}"
                  + (markerTip.Length > 0 ? $"\n{markerTip}" : "")
                  + $"\n{entry.FilePath}"
                : $"无法解析：{entry.Error ?? "未知原因"}\n{entry.FilePath}",
        };
    }

    /// <summary>
    /// 列表行上的「这个脚本带章节/书签吗」标记。
    ///
    /// 为什么先用子串预检再解析：脚本库刷新是逐文件跑的，绝大多数 funscript 根本没有 metadata，
    /// 为了一个多半不存在的装饰把每个文件都完整解析一遍是白烧 CPU（和 FunscriptTrackLoader.HasInvertedRoot 同一套路）。
    /// 另外给文件大小设了上限：大文件连读都不读，标记是锦上添花，不值得让列表刷新变慢。
    /// 只在后台线程调用（<see cref="BuildRow"/> 的约定）。
    /// </summary>
    private static string DescribeMarker(ScriptEntry entry, out string tip)
    {
        tip = "";
        if (!entry.Valid) return "";
        try
        {
            if (new FileInfo(entry.FilePath).Length > MaxMetadataProbeBytes) return "";
            string json = File.ReadAllText(entry.FilePath);
            if (!WaveScriptCodec.MightHaveTimelineMetadata(json)) return "";
            var metadata = WaveScriptCodec.ParseFunscriptMetadata(json);
            if (metadata.IsEmpty) return "";

            var parts = new List<string>();
            if (metadata.Chapters.Count > 0) parts.Add($"{metadata.Chapters.Count} 个章节");
            if (metadata.Bookmarks.Count > 0) parts.Add($"{metadata.Bookmarks.Count} 个书签");
            tip = "带 " + string.Join(" · ", parts) + "（选中这一行，下面的章节按钮就能点了跳）";
            return (metadata.Chapters.Count > 0 ? $"📑{metadata.Chapters.Count}" : "")
                 + (metadata.Bookmarks.Count > 0 ? $"🔖{metadata.Bookmarks.Count}" : "");
        }
        catch
        {
            // metadata 是装饰：读不出来就当没有，标记不该让整个列表刷新失败。
            return "";
        }
    }

    /// <summary>
    /// 这个脚本会驱动哪些轴：主轴文件（没有轴后缀的那个）算 L0，同名伴生轴文件（name.L1.funscript 等）
    /// 各算一根。判断方式与 FunscriptTrackLoader.LoadCompanionSet 一致，但只看文件名、不解析 JSON（列表刷新要快）；
    /// 单个文件里内嵌多轴（"axes" 数组）的那种，文件名看不出根数，就用已解析出来的轨道数兜底。
    /// </summary>
    private static string DescribeAxes(ScriptEntry entry, out int axisCount)
    {
        var axes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string withoutExtension = Path.ChangeExtension(entry.FilePath, null);
        bool hasAxisSuffix = FunscriptTrackLoader.AxisFromFileName(entry.FilePath) != null;
        string basePath = hasAxisSuffix ? Path.ChangeExtension(withoutExtension, null) : withoutExtension;
        if (!hasAxisSuffix) axes.Add("L0");

        try
        {
            string stem = Path.GetFileName(basePath);
            string? directory = Path.GetDirectoryName(basePath);
            if (directory != null && stem.Length > 0)
            {
                foreach (string candidate in Directory.EnumerateFiles(directory, stem + ".*.funscript"))
                {
                    if (FunscriptTrackLoader.AxisFromFileName(candidate) is { } axis) axes.Add(axis);
                }
            }
        }
        catch { /* 目录读不到就按已知的算，列表照样能用 */ }

        int parsedCount = entry.Valid ? entry.TrackCount : 0;
        axisCount = Math.Max(axes.Count, parsedCount);
        int installed = Osr6DeviceProfile.InstalledAxes.Length;
        // 六根轴全都有动作：写「全部 6 轴」比列一串 L0+L1+… 好读
        if (axisCount >= installed) return $"全部 {installed} 轴";
        if (parsedCount > axes.Count) return $"{parsedCount} 轴";   // 名字认不全，只报根数
        if (axes.Count == 0) return "—";
        var ordered = Osr6DeviceProfile.InstalledAxes.Where(axes.Contains).ToList();
        return ordered.Count > 0 ? string.Join("+", ordered) : $"{axes.Count} 轴";
    }

    /// <summary>
    /// 来源：本软件「脚本制作」按轴导出的分轴文件（文件名带 L0/R1… 后缀）/ 多根轴都有动作 / 只有一根轴的单文件。
    /// 社区下载的 funscript 基本都是「单文件」；「脚本制作」的那种一次会导出好几个同名的轴文件。
    /// </summary>
    private static string DescribeSource(ScriptEntry entry, int axisCount)
    {
        if (FunscriptTrackLoader.AxisFromFileName(entry.FilePath) != null) return "脚本制作";
        return axisCount > 1 ? "多轴" : "单文件";
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        >= 1024        => $"{bytes / 1024.0:0} KB",
        > 0            => $"{bytes} B",
        _              => "—",
    };

    /// <summary>库里一个脚本都没有时给出引导，而不是只留一片空白。</summary>
    private void UpdateEmptyState()
    {
        bool empty = _scriptCount == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListTitleText.Text = empty ? "脚本列表" : $"脚本列表 · 共 {_scriptCount} 个";
    }

    /// <summary>
    /// 播放状态 + 进度 + 库目录。急停和「逐帧下发被拒」优先显示：
    /// 界面显示「播放中」而设备不动是静默失败，必须把原因写出来。
    /// </summary>
    private void RefreshStatus()
    {
        var fs = App.FunscriptPlayer;
        if (fs == null) return;

        SyncPlaybackButtons();
        RefreshRateLabel();
        UpdatePlaybackPanel();

        // 进度条只在播放中自己走（播放器不会每帧广播状态）。
        // 但下面两种情况也必须一直刷，否则界面会「停在上一秒」：
        // ① 媒体同步连着（状态行要跟着播放器走）；② 开着 A-B 循环（到 B 要跳回 A）。
        // A-B 循环期间把节拍收紧到 40ms —— 150ms 一次的话，冲过 B 点会明显多走一截。
        // 只有在真的播着的时候才收紧：暂停时没必要 25Hz 空转。
        bool abArmed = _abLoopB.HasValue;
        var wantedInterval = TimeSpan.FromMilliseconds(abArmed && fs.IsPlaying ? AbLoopTickMs : 150);
        if (_progressTimer.Interval != wantedInterval) _progressTimer.Interval = wantedInterval;
        bool needTimer = fs.IsPlaying || abArmed || _mediaSync?.IsRunning == true;
        if (needTimer)
        {
            if (!_progressTimer.IsEnabled) _progressTimer.Start();
        }
        else if (_progressTimer.IsEnabled)
        {
            _progressTimer.Stop();
        }

        var parts = new List<string>();
        if (App.Engine.EmergencyStopped)
            parts.Add("⛔ 已急停：播放已停止，播放位置已归零（点左侧「全部归中」可以解除）");

        parts.Add($"📁 脚本库目录：{ScriptLibrary.Folder}");

        // 播放增强开着时常显，用户才知道「设备为什么比脚本写得多动了」。
        string enhance = fs.EnhanceSummary;
        if (enhance.Length > 0) parts.Add($"✨ 当前播放增强：{enhance}");

        FooterText.Text = string.Join("\n", parts);
    }

    /// <summary>
    /// 定时器心跳：① 先查 A-B 循环（有时限要求：到 B 要立刻回 A）；② 再刷播放卡
    /// （进度 / 时间 / 媒体同步状态行）。顺序不能反 —— 反了会让 A-B 的跳转晚一个 40ms。
    /// </summary>
    private void TickPlaybackTimer()
    {
        EnforceAbLoop();
        UpdatePlaybackPanel();
    }

    /// <summary>
    /// 播放控制卡：状态徽章 + 当前脚本 + 进度条 + 时间读数 + 无法下发的原因。
    /// 播放器只在 Load/Play/Pause/Stop/Seek 和引擎状态变化时广播，所以播放中由 _progressTimer 定时调它。
    /// </summary>
    private void UpdatePlaybackPanel()
    {
        var fs = App.FunscriptPlayer;
        if (fs == null) return;

        // ① 状态徽章（急停 > 未载入 > 播放中 > 已播完 > 已暂停）
        string badge;
        string badgeBrush;
        string inkBrush;
        if (App.Engine.EmergencyStopped) { badge = "已急停"; badgeBrush = "DangerSoft"; inkBrush = "Danger"; }
        else if (!fs.HasTrack) { badge = "未载入脚本"; badgeBrush = "SurfaceRaised"; inkBrush = "Muted"; }
        else if (fs.IsPlaying) { badge = "正在播放"; badgeBrush = "PrimarySoft"; inkBrush = "Primary"; }
        else if (fs.DurationMs > 0 && fs.PositionMs >= fs.DurationMs) { badge = "已播完"; badgeBrush = "SurfaceRaised"; inkBrush = "Muted"; }
        else { badge = "已暂停"; badgeBrush = "WarningSoft"; inkBrush = "Warning"; }

        if (!string.Equals(PlayStateText.Text, badge, StringComparison.Ordinal)) PlayStateText.Text = badge;
        var wantBg = (Brush)FindResource(badgeBrush);
        if (!ReferenceEquals(PlayStateBadge.Background, wantBg)) PlayStateBadge.Background = wantBg;
        var wantInk = (Brush)FindResource(inkBrush);
        if (!ReferenceEquals(PlayStateText.Foreground, wantInk)) PlayStateText.Foreground = wantInk;

        // ② 正在播哪个
        NowPlayingText.Text = fs.HasTrack
            ? $"当前脚本：{Path.GetFileName(fs.LoadedFile ?? "")}"
            : "还没有载入脚本：在下面的列表里选一个，点「▶ 载入并播放」。";
        LocateBtn.Visibility = fs.HasTrack ? Visibility.Visible : Visibility.Collapsed;

        // ③ 进度 + 时间。进度条现在**可以拖**（外壳那层 Border 接了鼠标事件，见 PlaybackScrub_*）；
        //    拖动中不覆盖 Value：那 0–1 的读数是用户正在拖的位置，被定时器刷回去就拖不动了。
        if (!_scrubbing)
        {
            PlaybackProgress.Value = fs.DurationMs > 0
                ? Math.Clamp(fs.PositionMs / (double)fs.DurationMs, 0, 1)
                : 0;
        }
        PlaybackTimeText.Text = fs.HasTrack
            ? _scrubbing
                ? $"{FormatClock((long)(_scrubSeconds * 1000))} / {FormatClock(fs.DurationMs)} · 拖动中"
                : $"{FormatClock(fs.PositionMs)} / {FormatClock(fs.DurationMs)} · 剩 {FormatClock(fs.DurationMs - fs.PositionMs)}"
            : "— / —";

        // ④ 逐帧下发被拒的原因（规则引擎接管 / 未连接 / 急停）：常显，绝不静默失败
        SetPlaybackWarn(fs.DirectInputBlockReason ?? _blockedReason);

        // ⑤ 跟着播放器走的状态行（没连的时候显示「未连接」引导）
        UpdateMediaSyncPanel();
    }

    /// <summary>播放控制卡里的警示行（null / 空 = 隐藏）。</summary>
    private void SetPlaybackWarn(string? text)
    {
        bool show = !string.IsNullOrWhiteSpace(text);
        string want = show ? "⚠ " + text : "";
        if (string.Equals(PlaybackWarnText.Text, want, StringComparison.Ordinal)) return;
        PlaybackWarnText.Text = want;
        PlaybackWarnText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>暂停/继续、停止按钮的文案与可用性跟着播放器状态走（IsPlaying / IsPaused）。</summary>
    private void SyncPlaybackButtons()
    {
        var fs = App.FunscriptPlayer;
        PauseResumeBtn.Content = fs.IsPaused ? "▶ 继续" : "⏸ 暂停";
        PauseResumeBtn.IsEnabled = fs.HasTrack;
        StopBtn.IsEnabled = fs.HasTrack;
        // 有东西在放/暂停时，「停止」是最该被一眼看到的出口
        var stopStyle = (Style)FindResource(fs.HasTrack ? "BtnDanger" : "BtnSecondary");
        if (!ReferenceEquals(StopBtn.Style, stopStyle)) StopBtn.Style = stopStyle;

        // A-B 的「设 A / 设 B」要有位置可设：没载入脚本就是灰的（点了只能弹一句「先载入脚本」）
        AbLoopSetABtn.IsEnabled = fs.HasTrack;
        AbLoopSetBBtn.IsEnabled = fs.HasTrack;
    }

    /// <summary>倍率只读显示（与「脚本制作 → 微调波形」的「脚本倍率」是同一个设置项）。</summary>
    private void RefreshRateLabel()
    {
        double set = App.Settings.ScriptPlaybackSpeed;
        // 媒体同步在跑时实际倍率 = 设置的倍率 × 同步微调（±10%），只写设置值会让用户以为设备按 1.00x 在走。
        double effective = _mediaSync?.Status.EffectiveRate ?? 0;
        RateLabel.Text = effective > 0.01 && Math.Abs(effective - set) > 0.005
            ? $"{effective:0.00}x（跟随影片微调中 · 设定 {set:0.00}x）"
            : $"{set:0.00}x";
    }

    /// <summary>「打开库目录」按钮的悬停提示写明真实路径（库目录可以在设置页改）。</summary>
    private void UpdateFolderHint() =>
        OpenFolderBtn.ToolTip = $"在文件资源管理器里打开脚本库目录：\n{ScriptLibrary.Folder}\n（目录可以在「设置 → 存储位置」里改）";

    // ══════════════════════════════════════════════════════════════════════
    //  媒体同步：脚本跟着外挂播放器的时间码走
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 「跟着播放器走」区的初值与事件接线。
    ///
    /// <b>只读设置，绝不自动连播放器</b>：连不连是一个明确的用户动作（点「连接」）。
    /// 设置里的 MediaSyncEnabled 只表示「上次用到哪一步」，页面重新打开时会显示成未连接，
    /// 这样「进软件就悄悄去连一个播放器」永远不会发生。
    /// </summary>
    private void InitMediaSyncControls()
    {
        var cfg = App.Settings;

        // 设置里存的播放器（potplayer / deovr / heresphere 目前没有实现，一律回落到第一项 mpv）
        MediaSyncPlayerCombo.SelectedIndex = cfg.MediaSyncPlayer switch
        {
            "mpc" => 1,
            "vlc" => 2,
            _ => 0,
        };

        _loadingUi = true;
        try
        {
            MediaSyncOffsetSlider.Value = Math.Clamp(cfg.MediaSyncOffsetMs,
                MediaSyncOffsetSlider.Minimum, MediaSyncOffsetSlider.Maximum);
        }
        finally { _loadingUi = false; }
        MediaSyncOffsetLabel.Text = $"{MediaSyncOffsetSlider.Value:0} ms";

        MediaSyncOffsetSlider.ValueChanged += (_, e) =>
        {
            MediaSyncOffsetLabel.Text = $"{e.NewValue:0} ms";
            if (_loadingUi) return;
            App.Settings.MediaSyncOffsetMs = (int)Math.Round(e.NewValue);
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        RefreshAbLoopText();
        SyncMediaSyncControls();
        UpdateMediaSyncPanel();
    }

    /// <summary>当前下拉框选中的播放器 id（Tag 里存的是 mpv / mpc / vlc）。</summary>
    private string SelectedPlayerId() =>
        (MediaSyncPlayerCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "mpv";

    /// <summary>「连接 / 断开」按钮和下拉框的可用性跟着服务的真实状态走。</summary>
    private void SyncMediaSyncControls()
    {
        bool running = _mediaSync?.IsRunning == true;
        string want = running ? "断开" : "连接";
        if (!string.Equals(MediaSyncConnectBtn.Content as string, want, StringComparison.Ordinal))
            MediaSyncConnectBtn.Content = want;
        var style = (Style)FindResource(running ? "BtnDanger" : "BtnSecondary");
        if (!ReferenceEquals(MediaSyncConnectBtn.Style, style)) MediaSyncConnectBtn.Style = style;
        // 连着的时候不让改播放器：换了也没用（要断开再连），不如直接锁住，避免「我换了它怎么不跟」。
        MediaSyncPlayerCombo.IsEnabled = !running;
        // 自动匹配要读播放器的文件名，所以必须先连上
        MediaSyncAutoLoadBtn.IsEnabled = running;
        MediaSyncConnectBtn.ToolTip = running
            ? "停止跟随播放器（脚本会继续按原速播放，不会跟着停）"
            : "开始读这个播放器的时间码，让脚本跟着画面走（点第二次断开）。连上之后：媒体暂停脚本也暂停、媒体快放脚本也快放、差得多就直接跳。";
    }

    /// <summary>连接 / 断开。**这是本页唯一会让脚本去跟播放器的地方**，默认没有任何自动触发。</summary>
    private void MediaSyncConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSync?.IsRunning == true)
        {
            _mediaSync.Disconnect("已断开 · 脚本继续按原速播放");
            App.Settings.MediaSyncEnabled = false;
            App.Settings.Save();
            SyncMediaSyncControls();
            UpdateMediaSyncPanel();
            RefreshStatus();
            return;
        }

        // App.FunscriptPlayer / App.Settings 在 Loaded 之后一定就绪；这里再兜一层，
        // 免得自检直接 new 出页面后点按钮把异常抛到界面上。
        if (App.FunscriptPlayer is null || App.Settings is null) return;

        _mediaSync ??= new MediaSyncService(App.FunscriptPlayer, App.Settings);
        string playerId = SelectedPlayerId();
        App.Settings.MediaSyncPlayer = playerId;
        App.Settings.MediaSyncEnabled = true;
        App.Settings.Save();

        if (!_mediaSync.Connect(playerId))
        {
            App.Settings.MediaSyncEnabled = false;
            App.Settings.Save();
            SetMediaSyncNote("这个播放器还没支持：目前能跟的是 mpv、MPC-HC/BE、VLC。");
        }
        else
        {
            SetMediaSyncNote($"开始找 {MediaSyncPlayerCombo.Text}：确认播放器开着，并且允许远程控制（鼠标停在「连接」上看各自的启动参数）。");
        }
        SyncMediaSyncControls();
        UpdateMediaSyncPanel();
        RefreshStatus();
    }

    /// <summary>
    /// 状态行：没连 → 引导文案；连上 → 服务给的一行状态（媒体时间 / 脚本时间 / 跟上没有）。
    /// 颜色也跟着走（连着=主色，连不上=警示），一眼分得出「在跟」和「没找到播放器」。
    /// </summary>
    private void UpdateMediaSyncPanel()
    {
        if (_mediaSync?.IsRunning != true)
        {
            const string hint = "未连接 · 点「连接」开始跟随（需要播放器开着并允许远程控制）";
            if (!string.Equals(MediaSyncStatusText.Text, hint, StringComparison.Ordinal))
                MediaSyncStatusText.Text = hint;
            SetMediaSyncInk("Muted");
            return;
        }

        var status = _mediaSync.Status;
        string text = status.Message;
        // 误差只在真的对上时才有意义（没连上时它是 0，写出来反而让人以为「正好对齐」）
        if (status.Connected && Math.Abs(status.ErrorSeconds) >= 0.02)
            text += $"（差 {status.ErrorSeconds * 1000:+0;-0;0} ms）";
        if (!string.Equals(MediaSyncStatusText.Text, text, StringComparison.Ordinal))
            MediaSyncStatusText.Text = text;
        SetMediaSyncInk(status.Connected ? "Primary" : "Warning");
    }

    private void SetMediaSyncInk(string resourceKey)
    {
        var brush = (Brush)FindResource(resourceKey);
        if (!ReferenceEquals(MediaSyncStatusText.Foreground, brush)) MediaSyncStatusText.Foreground = brush;
    }

    /// <summary>
    /// 一行临时提示（自动匹配的结果 / 拖不动的原因）。9 秒后自己收起来 ——
    /// 常驻一行过期信息比没有更糟（用户会以为那就是当前状态）。
    /// </summary>
    private void SetMediaSyncNote(string text)
    {
        MediaSyncNoteText.Text = text;
        MediaSyncNoteText.Visibility = Visibility.Visible;
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    /// <summary>
    /// 「↻ 按文件名自动匹配脚本」：拿播放器当前打开的文件名，去脚本库里找同名的 .funscript 载入。
    /// 解析放后台线程（大脚本读盘 + 解析可能几百毫秒），并且**只载入不播放**：
    /// 让设备动起来必须用户再点一次「▶ 载入并播放」。
    /// </summary>
    private async void MediaSyncAutoLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSync is not { } sync || !sync.IsRunning)
        {
            SetMediaSyncNote("先点「连接」，连通了才读得到播放器打开的文件名。");
            return;
        }

        MediaSyncAutoLoadBtn.IsEnabled = false;
        try
        {
            string result = await Task.Run(() => sync.TryAutoLoadMatchingScript());
            SetMediaSyncNote(result);
            RefreshStatus();
            // 自动匹配可能换了脚本：章节/书签列表要跟着换
            LoadChaptersFor(App.FunscriptPlayer?.LoadedFile);
        }
        catch (Exception ex)
        {
            SetMediaSyncNote("自动匹配失败：" + ex.Message);
        }
        finally
        {
            MediaSyncAutoLoadBtn.IsEnabled = _mediaSync?.IsRunning == true;
        }
    }

    /// <summary>
    /// 告诉同步服务「位置现在由用户/循环做主」（拖动进度条、A-B 循环期间）。
    /// 优先级：拖动 &gt; A-B 循环 &gt; 正常跟随 —— 拖动是用户当下的动作，最该听他的。
    /// </summary>
    private void UpdatePositionHold()
    {
        if (_mediaSync is null) return;
        if (_scrubbing) { _mediaSync.SetPositionHold("正在拖动进度条"); return; }
        if (_abLoopB.HasValue) { _mediaSync.SetPositionHold("A-B 循环中"); return; }
        _mediaSync.SetPositionHold(null);
    }

    // ── 进度条拖动 ───────────────────────────────────────────────────

    /// <summary>按下即跳（和播放器一样的手感），并抓住鼠标，之后移动全归这里处理。</summary>
    private void PlaybackScrub_Start(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var fs = App.FunscriptPlayer;
        if (fs is null || !fs.HasTrack || fs.DurationMs <= 0) return;

        _scrubbing = true;
        // 拖动期间让同步循环停手：它每 80ms 纠正一次位置，不喊停根本拖不动（还会和手指打架）。
        UpdatePositionHold();
        if (sender is UIElement element) element.CaptureMouse();
        ScrubTo(sender, e);
        e.Handled = true;
    }

    private void PlaybackScrub_Move(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;
        // 鼠标在别处松开（拖出窗口）时收不到 Up：这里补一次收尾，否则会一直「拖动中」。
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            EndScrub(sender);
            return;
        }
        ScrubTo(sender, e);
        e.Handled = true;
    }

    private void PlaybackScrub_End(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        EndScrub(sender);
        e.Handled = true;
    }

    /// <summary>把鼠标横向位置换算成脚本时间并 Seek（只动脚本，不动播放器）。</summary>
    private void ScrubTo(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not IInputElement reference) return;
        var fs = App.FunscriptPlayer;
        if (fs is null || fs.DurationMs <= 0) return;
        double width = (sender as FrameworkElement)?.ActualWidth ?? 0;
        if (width <= 0) return;

        double fraction = Math.Clamp(e.GetPosition(reference).X / width, 0, 1);
        _scrubSeconds = fraction * fs.DurationMs / 1000.0;
        fs.SeekSeconds(_scrubSeconds);
        // 立刻把进度条和读数画到新位置：不然要等下一次定时器（150ms）才动，手感很黏。
        UpdatePlaybackPanel();
    }

    /// <summary>
    /// 松手：位置交还给同步循环。
    /// 注意：开着「跟着播放器走」时，下一次同步（80ms 内）会发现误差 &gt; 1 秒并把脚本拉回画面位置 ——
    /// 那不是 bug，正是「跟着播放器走」该有的行为（脚本必须和画面对得上）。悬停提示里已经写明。
    /// </summary>
    private void EndScrub(object sender)
    {
        _scrubbing = false;
        if (sender is UIElement element && element.IsMouseCaptured) element.ReleaseMouseCapture();
        UpdatePositionHold();
        RefreshStatus();
    }

    // ── A-B 循环 ─────────────────────────────────────────────────────

    /// <summary>设 A：把当前播放位置记成循环起点。</summary>
    private void AbLoopSetA_Click(object sender, RoutedEventArgs e)
    {
        var fs = App.FunscriptPlayer;
        if (fs is null || !fs.HasTrack)
        {
            SetMediaSyncNote("还没有载入脚本：先在列表里选一个，点「▶ 载入并播放」，再设 A / B。");
            return;
        }
        _abLoopA = fs.PositionSeconds;
        // 新的 A 跑到 B 后面去了：B 就没意义了，清掉（否则循环区间是负的，永远不触发）
        if (_abLoopB is { } end && end <= _abLoopA.Value) _abLoopB = null;
        RefreshAbLoopText();
        UpdatePositionHold();
        SetMediaSyncNote($"A 点已设为 {FormatClock((long)(_abLoopA.Value * 1000))}。");
        RefreshStatus();
    }

    /// <summary>设 B：播放到 B 自动跳回 A，一直循环。</summary>
    private void AbLoopSetB_Click(object sender, RoutedEventArgs e)
    {
        var fs = App.FunscriptPlayer;
        if (fs is null || !fs.HasTrack)
        {
            SetMediaSyncNote("还没有载入脚本：先在列表里选一个，点「▶ 载入并播放」，再设 A / B。");
            return;
        }
        double start = _abLoopA ?? 0;
        double position = fs.PositionSeconds;
        if (position <= start + 0.2)
        {
            SetMediaSyncNote("B 点要比 A 点靠后：先把脚本放到后面一点（拖进度条或直接播一会儿），再点「设 B」。");
            return;
        }
        _abLoopB = position;
        RefreshAbLoopText();
        UpdatePositionHold();
        SetMediaSyncNote($"A-B 循环已开：{FormatClock((long)(start * 1000))} → {FormatClock((long)(position * 1000))}，播到 B 会自动跳回 A。");
        RefreshStatus();
    }

    /// <summary>清除 A-B 循环。</summary>
    private void AbLoopClear_Click(object sender, RoutedEventArgs e)
    {
        _abLoopA = null;
        _abLoopB = null;
        RefreshAbLoopText();
        UpdatePositionHold();
        SetMediaSyncNote("A-B 循环已清除。");
        RefreshStatus();
    }

    /// <summary>循环读数的文案（未设置 / 从 A 到片尾 / A→B）。</summary>
    private void RefreshAbLoopText()
    {
        string text;
        if (_abLoopB is { } end)
            text = $"循环 {FormatClock((long)((_abLoopA ?? 0) * 1000))} → {FormatClock((long)(end * 1000))}";
        else if (_abLoopA is { } start)
            text = $"从 {FormatClock((long)(start * 1000))} 播到片尾";
        else
            text = "未设置";

        if (!string.Equals(AbLoopText.Text, text, StringComparison.Ordinal)) AbLoopText.Text = text;
        AbLoopClearBtn.IsEnabled = _abLoopA.HasValue || _abLoopB.HasValue;
    }

    /// <summary>
    /// 到 B 就回 A。放在 40ms 的定时器里跑（不是 150ms 的进度刷新）：
    /// 150ms 一次的话，冲到 B 之后再走最多 150ms 才跳回，那一段是重复听的、很出戏。
    /// 拖动中不干预（用户正在找 B 点本身）。
    /// </summary>
    private void EnforceAbLoop()
    {
        if (_abLoopB is not { } end || _scrubbing) return;
        var fs = App.FunscriptPlayer;
        if (fs is null || !fs.IsPlaying || !fs.HasTrack) return;
        double start = _abLoopA ?? 0;
        if (end <= start) return;
        if (fs.PositionSeconds >= end) fs.SeekSeconds(start);
    }

    // ── 章节 / 书签 ──────────────────────────────────────────────────

    /// <summary>
    /// 给某个脚本装章节/书签按钮（选中哪一行就装哪一行；没有就整块收起来，不占高度）。
    /// 同一个路径直接返回：列表刷新、状态变化都会走到这里，重复解析文件是白花钱。
    /// </summary>
    private void LoadChaptersFor(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _chapterPath = null;
            ApplyChapterItems([], null);
            return;
        }
        if (string.Equals(path, _chapterPath, StringComparison.OrdinalIgnoreCase)) return;

        _chapterPath = path;
        ApplyChapterItems([], path);   // 先清空：不要短暂显示上一个脚本的章节（点了会跳到莫名其妙的地方）
        _ = LoadChaptersAsync(path);
    }

    /// <summary>读文件 + 解析放后台线程（列表选择是 UI 事件，不能在这里读盘）。</summary>
    private async Task LoadChaptersAsync(string path)
    {
        List<ChapterItem> items;
        try
        {
            items = await Task.Run(() => BuildChapterItems(path));
        }
        catch
        {
            items = [];
        }
        // 读盘期间用户可能已经选了别的脚本：过期结果直接丢掉，别覆盖新的。
        if (!string.Equals(path, _chapterPath, StringComparison.OrdinalIgnoreCase)) return;
        ApplyChapterItems(items, path);
    }

    /// <summary>
    /// 把一份脚本的 metadata 变成可以点的按钮（只在后台线程调用）。
    /// 章节在前、书签在后，各自按时间排序；metadata 坏了就当没有（见 WaveScriptCodec.ParseFunscriptMetadata）。
    /// </summary>
    private static List<ChapterItem> BuildChapterItems(string path)
    {
        var items = new List<ChapterItem>();
        if (new FileInfo(path).Length > MaxMetadataProbeBytes) return items;

        string json = File.ReadAllText(path);
        if (!WaveScriptCodec.MightHaveTimelineMetadata(json)) return items;
        var metadata = WaveScriptCodec.ParseFunscriptMetadata(json);

        foreach (var chapter in metadata.Chapters)
        {
            items.Add(new ChapterItem
            {
                Label = $"📑 {chapter.Name}",
                Detail = $"{chapter.Name}\n{FormatClockStatic(chapter.StartTime)}"
                         + (chapter.EndTime > chapter.StartTime
                             ? $" – {FormatClockStatic(chapter.EndTime)}"
                             : "")
                         + "\n点一下跳到这一段的开头",
                Seconds = chapter.StartTime,
                IsChapter = true,
            });
        }
        foreach (var bookmark in metadata.Bookmarks)
        {
            items.Add(new ChapterItem
            {
                Label = $"🔖 {bookmark.Name}",
                Detail = $"{bookmark.Name}\n{FormatClockStatic(bookmark.Time)}\n点一下跳到这个书签",
                Seconds = bookmark.Time,
                IsChapter = false,
            });
        }
        return items;
    }

    private void ApplyChapterItems(List<ChapterItem> items, string? path)
    {
        ChapterList.ItemsSource = items;
        bool show = items.Count > 0;
        ChapterPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        int chapters = items.Count(item => item.IsChapter);
        int bookmarks = items.Count - chapters;
        var parts = new List<string>();
        if (chapters > 0) parts.Add($"{chapters} 个章节");
        if (bookmarks > 0) parts.Add($"{bookmarks} 个书签");
        ChapterTitleText.Text = $"📑 章节 / 书签 · {string.Join(" · ", parts)}"
                                + (string.IsNullOrEmpty(path) ? "" : $"：{Path.GetFileName(path)}");
    }

    /// <summary>点章节/书签 → 跳到那个时间（前提是脚本已经载入：没载入就没有位置可跳）。</summary>
    private void ChapterJump_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChapterItem item) return;
        var fs = App.FunscriptPlayer;
        if (fs is null || !fs.HasTrack)
        {
            SetMediaSyncNote("还没有载入脚本：先在列表里选一个，点「▶ 载入并播放」，章节按钮就能点了跳。");
            return;
        }

        // 章节列表是按"列表里选中那一行"建的，而 Seek 作用在"正在播的那个脚本"上：
        // 两者不是同一个文件时，以前会静默跳到另一个脚本的同一时刻（提示还说你跳到了这一段）。
        // 认准文件再跳，不一致就直说该先做什么。
        string? loaded = fs.LoadedFile;
        if (!string.IsNullOrWhiteSpace(loaded) && !string.IsNullOrWhiteSpace(_chapterPath)
            && !string.Equals(loaded, _chapterPath, StringComparison.OrdinalIgnoreCase))
        {
            SetMediaSyncNote("现在正在播的是另一个脚本，章节按钮是「列表里选中那一个」的："
                + "先点选中那一行的「▶」把它载入播放，再点章节跳转。");
            return;
        }

        fs.SeekSeconds(item.Seconds);
        SetMediaSyncNote($"已跳到 {FormatClock((long)(item.Seconds * 1000))}（{item.Label}）");
        RefreshStatus();
    }

    /// <summary>秒 → mm:ss（章节/书签的提示文字用；<see cref="FormatClock"/> 的秒版本）。</summary>
    private static string FormatClockStatic(double seconds) =>
        FormatClock((long)Math.Round(Math.Max(0, seconds) * 1000.0));

    /// <summary>
    /// 名称列吃掉剩余宽度：最大化时不再空出上千像素，窗口变窄也不会把时长/轨道列挤出可视区。
    /// 减掉的固定项 = 五列固定宽（56+66+56+62+80）+ ListView 边框 2 + 行内边距 20（ListViewItem Padding="10,5"）
    /// + 竖直滚动条 8 + 6px 余量（ListView 样式禁用了横向滚动条，列宽超一点就会被裁）。
    /// </summary>
    private void ResizeNameColumn()
    {
        double reserved = DurationColumnWidth + AxesColumnWidth + SizeColumnWidth + SourceColumnWidth
            + ActionsColumnWidth + 2 + 20 + ScrollBarWidth + 6;
        NameColumn.Width = Math.Max(NameColumnMinWidth, ScriptList.ActualWidth - reserved);
    }

    /// <summary>毫秒 → mm:ss（页脚与列表共用，超过一小时就继续累加分钟）。</summary>
    private static string FormatClock(long ms)
    {
        long total = Math.Max(0, ms);
        return $"{total / 60000:00}:{total % 60000 / 1000:00}";
    }

    private ScriptRow? Selected =>
        ScriptList.SelectedItem is ScriptRow row ? row : null;

    /// <summary>行内按钮 → 它所在的那一行。</summary>
    private static ScriptRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ScriptRow;

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入脚本（可多选）",
            Filter = "funscript 脚本 (*.funscript)|*.funscript|所有文件 (*.*)|*.*",
            // 微调波形「导出 ALL」把一个脚本按轴拆成 name.L0..R2 共 6 个文件，
            // 只选一个会让其余 5 轴在播放时被静默钉在中位 50 → 必须支持多选。
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // 展开同名伴生轴文件：用户只选 L0 时也把 name.L1..R2 一起复制进库。
            var sources = ScriptLibrary.ExpandCompanionFiles(dlg.FileNames);

            // 先算出库内目标路径：同名冲突必须由用户确认，绝不静默覆盖已有脚本。
            var conflicts = ScriptLibrary.ConflictingTargets(sources);
            bool overwrite = false;
            if (conflicts.Count > 0)
            {
                string names = string.Join("、", conflicts.Take(5).Select(dest => Path.GetFileNameWithoutExtension(dest)));
                if (conflicts.Count > 5) names += $" 等 {conflicts.Count} 个";
                var confirm = System.Windows.MessageBox.Show(
                    $"库内已有同名脚本：{names}。覆盖它们？",
                    "覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
                overwrite = true;
            }

            var summary = ScriptLibrary.ImportMany(sources, overwrite);
            // 导入后选中第一个导入的文件：用户一眼能看到它落在哪一行
            string? first = summary.Scripts.Count > 0
                ? Path.Combine(ScriptLibrary.Folder, summary.Scripts[0].Name + ".funscript")
                : null;
            await RefreshListAsync(first);
            ShowImportResult(summary);
        }
        catch (InvalidOperationException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导入失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 导入结果提示：文件数 + 轴文件数 + 每个文件的「时长 / 轨道数」+ 放进哪个目录 + 怎么用；
    /// 解析失败的文件直接列出原因（列表里也会把原因写进行级 ToolTip）。
    /// </summary>
    private static void ShowImportResult(ImportSummary summary)
    {
        var lines = summary.Scripts.Take(8).Select(script => script.Error == null
            ? $"{script.Name}（时长 {script.DurationMs / 1000.0:F1} 秒 / {script.TrackCount} 轨）"
            : $"{script.Name}（无效：{script.Error}）").ToList();
        if (summary.Scripts.Count > lines.Count)
            lines.Add($"…另有 {summary.Scripts.Count - lines.Count} 个文件");

        string message = $"已导入 {summary.FileCount} 个文件（含 {summary.AxisFileCount} 个轴文件）";
        if (summary.InvalidCount > 0) message += $"，其中 {summary.InvalidCount} 个无法解析";
        message += "：\n  " + string.Join("\n  ", lines);
        message += $"\n\n都放进了脚本库：{ScriptLibrary.Folder}";
        message += "\n用法：在列表里选中它 → 点「▶ 载入并播放」（会驱动设备），或点行内 ▶ 直接播。";

        System.Windows.MessageBox.Show(message, "导入脚本", MessageBoxButton.OK,
            summary.InvalidCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await PlayRowAsync(Selected);

    private async void RowPlay_Click(object sender, RoutedEventArgs e) => await PlayRowAsync(RowOf(sender));

    /// <summary>载入一个脚本并播放（工具栏按钮和行内 ▶ 共用同一套检查，不会两处行为不一致）。</summary>
    private async Task PlayRowAsync(ScriptRow? row)
    {
        if (row == null)
        {
            SetPlaybackWarn("先在下面的列表里选一个脚本，再点「▶ 载入并播放」。");
            return;
        }
        if (!row.Valid)
        {
            SetPlaybackWarn($"「{row.Name}」无法解析，不能播放：{row.Error ?? "未知原因"}");
            System.Windows.MessageBox.Show(
                $"「{row.Name}」无法解析，不能播放：\n{row.Tip}", "无法播放",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 设备未连接/急停锁定时不载入，避免界面显示「播放中」却什么都不动。
        if (!App.Engine.CanRun)
        {
            ShowPlayBlocked(App.FunscriptPlayer.DirectInputBlockReason ?? "设备未连接或处于急停");
            return;
        }
        try
        {
            // 解析 funscript 可能较大，放到后台线程读盘，避免卡 UI。
            await Task.Run(() => App.FunscriptPlayer.Load(row.FilePath));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("播放失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // 读盘期间规则引擎可能接管，所以载入后再判一次能不能真正逐帧下发。
        TryStartPlayback();
        RefreshStatus();
    }

    /// <summary>
    /// 播放前的接管检查 + 启动。逐帧下发要求 CanAcceptDirectInput = CanRun &amp;&amp; !IsRunning &amp;&amp; !RuleEngineActive：
    /// 先 StopAll() 清掉自动/动作/遥测任务（Play() 内部本来也会做），剩下的拒绝原因只可能是
    /// 「未连接 / 急停 / 归中」或「规则引擎（声音响应 / 游戏伴随）接管」——这两种必须当场告诉用户，
    /// 否则界面显示「播放中」而设备一动不动（本轮修的静默失败）。
    /// </summary>
    private bool TryStartPlayback()
    {
        App.Engine.StopAll();
        if (!App.Engine.CanAcceptDirectInput)
        {
            ShowPlayBlocked(App.FunscriptPlayer.DirectInputBlockReason ?? "设备当前不接受逐帧下发");
            return false;
        }
        _blockedReason = null;
        App.FunscriptPlayer.Play();
        return true;
    }

    /// <summary>播放被拒：弹窗说清原因 + 播放卡常显，绝不静默。</summary>
    private void ShowPlayBlocked(string reason)
    {
        string hint = App.Engine.RuleEngineActive
            ? "\n\n声音响应 / 游戏伴随正在接管，请先到「测试台」关闭后再播放。"
            : "";
        _blockedReason = reason;
        System.Windows.MessageBox.Show($"无法播放脚本：{reason}。{hint}", "无法播放",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshStatus();
    }

    /// <summary>暂停 / 继续。继续时走和「载入并播放」相同的接管检查，急停或规则引擎接管会明确拒绝。</summary>
    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        var fs = App.FunscriptPlayer;
        if (fs.IsPlaying)
        {
            fs.Pause();
        }
        else if (!fs.HasTrack)
        {
            SetPlaybackWarn("还没有载入脚本：先在列表里选一个，点「▶ 载入并播放」。");
            return;
        }
        else
        {
            TryStartPlayback();
        }
        RefreshStatus();
    }

    /// <summary>停止：暂停 + 停掉引擎输出 + 播放位置归零（服务内完成，不会把设备推到脚本开头姿态）。</summary>
    private void CommunitySearch_Click(object sender, RoutedEventArgs e) => OpenCommunitySearch();

    private void CommunitySearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        OpenCommunitySearch();
    }

    /// <summary>
    /// 去社区搜脚本：只把搜索页开好，<b>不在后台抓取</b>。
    /// EroScripts 的使用条款明确写着"不得用爬虫／浏览器插件／非浏览器程序自动访问或监视论坛"，
    /// 所以这里刻意不做"一键自动搜下" —— 你自己点进去看到的才是完整的帖子、附件与说明，账号也不会因为自动化被封。
    /// </summary>
    private void OpenCommunitySearch()
    {
        string keyword = (CommunitySearchBox?.Text ?? "").Trim();
        if (keyword.Length == 0)
        {
            if (CommunitySearchBox is { } empty) { empty.ToolTip = "先填一个视频名或作品名，再去搜"; empty.Focus(); }
            return;
        }

        try
        {
            string url = "https://discuss.eroscripts.com/search?q=" + System.Uri.EscapeDataString(keyword);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            if (CommunitySearchBox is { } ok) ok.ToolTip = $"已打开搜索页（关键词：{keyword}）。下到的 .funscript 丢进脚本库目录，再点「刷新」。";
        }
        catch (Exception ex)
        {
            if (CommunitySearchBox is { } bad) bad.ToolTip = "打不开浏览器：" + ex.Message;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        App.FunscriptPlayer.Stop();
        _blockedReason = null;
        RefreshStatus();
    }

    /// <summary>把列表滚到正在播的那一行并选中它（长列表里一眼找到）。</summary>
    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        string? path = App.FunscriptPlayer.LoadedFile;
        if (!string.IsNullOrEmpty(path)) SelectRowByPath(path);
    }

    /// <summary>脚本倍率 −0.05x（下限 0.25x）。</summary>
    private void RateMinus_Click(object sender, RoutedEventArgs e) => AdjustRate(-0.05);

    /// <summary>脚本倍率 +0.05x（上限 2.00x）。</summary>
    private void RatePlus_Click(object sender, RoutedEventArgs e) => AdjustRate(+0.05);

    /// <summary>
    /// 调整脚本播放倍率：与「脚本制作 → 微调波形」的「脚本倍率」共用同一个设置项
    /// （App.Settings.ScriptPlaybackSpeed，FunscriptPlayerService.Tick 每帧读它），写回后立即持久化。
    /// </summary>
    private void AdjustRate(double delta)
    {
        var cfg = App.Settings;
        double target = Math.Round(cfg.ScriptPlaybackSpeed + delta, 2);
        if (target is > 2.0 or < 0.25)
        {
            SetPlaybackWarn(target > 2.0 ? "播放倍率已经到上限 2.00x。" : "播放倍率已经到下限 0.25x。");
            return;
        }
        double next = Math.Clamp(target, 0.25, 2.0);
        cfg.ScriptPlaybackSpeed = next;
        App.Engine.ScriptSpeed = next;   // 引擎侧（自定义模式等）保持同步，和微调页写法一致
        cfg.Save();
        RefreshRateLabel();
    }

    private async void RowRename_Click(object sender, RoutedEventArgs e) => await RenameRowAsync(RowOf(sender));

    private async Task RenameRowAsync(ScriptRow? row)
    {
        if (row == null)
        {
            SetPlaybackWarn("先选一个脚本再改名（也可以直接点那一行右边的 ✏）。");
            return;
        }
        string? newName = PromptText("重命名脚本", "输入新名称：", System.IO.Path.GetFileNameWithoutExtension(row.FilePath));
        if (string.IsNullOrWhiteSpace(newName))
            return;
        try
        {
            // 先算出库内目标路径：改名撞上已有脚本必须由用户确认，绝不静默覆盖。
            string dest = ScriptLibrary.RenamePathFor(newName);
            bool overwrite = false;
            if (File.Exists(dest) && !string.Equals(dest, row.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"库内已有「{Path.GetFileNameWithoutExtension(dest)}」，覆盖它？",
                    "覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
                overwrite = true;
            }
            string renamed = ScriptLibrary.Rename(row.FilePath, newName, overwrite);
            await RefreshListAsync(renamed);
        }
        catch (InvalidOperationException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("重命名失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e) => await DeleteRowAsync(RowOf(sender));

    private async Task DeleteRowAsync(ScriptRow? row)
    {
        if (row == null)
        {
            SetPlaybackWarn("先选一个脚本再删除（也可以直接点那一行右边的 🗑）。");
            return;
        }
        var result = System.Windows.MessageBox.Show(
            $"确定删除库内脚本「{row.Name}」吗？\n\n" +
            $"· 只删脚本库（{ScriptLibrary.Folder}）里的这一份\n" +
            "· 你电脑里的原始文件不受影响\n" +
            "· 删除不进回收站，删了就没了",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            ScriptLibrary.Delete(row.FilePath);
            await RefreshListAsync("");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("删除失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>在文件资源管理器里打开脚本库目录（用户问「脚本放进哪个目录了」时的直接答案）。</summary>
    /// <summary>
    /// 脚本库网页的开关。开着的时候手机/平板/头显里的浏览器可以打开 http://localhost:8582/
    /// 浏览脚本库并点播；只在本机可用，只提供播放与停止，不提供任何写轴的入口。
    /// </summary>
    private void LibraryWeb_Click(object sender, RoutedEventArgs e)
    {
        if (App.LibraryWeb.IsRunning)
        {
            App.LibraryWeb.Stop();
            App.Settings.LibraryWebEnabled = false;
            LibraryWebBtn.Content = "📱 手机上看";
            LibraryWebBtn.ToolTip = "已关闭。再点一次重新开。";
        }
        else
        {
            App.LibraryWeb.Start();
            bool up = App.LibraryWeb.IsRunning;
            App.Settings.LibraryWebEnabled = up;
            LibraryWebBtn.Content = up ? "📱 网页已开" : "📱 开不起来";
            LibraryWebBtn.ToolTip = up
                ? $"已开启：在浏览器打开 {App.LibraryWeb.Url}（手机同一台机器上直接输这个地址）"
                : "开不起来：" + App.LibraryWeb.LastError;
            // 失败原因不能只藏在 ToolTip 里（等于没说）：页面提示区写一行 + 给出下一步。
            if (!up)
                SetPlaybackWarn("手机上看开不起来：" + (App.LibraryWeb.LastError ?? "未知错误")
                    + " —— 端口可能被别的程序占着，关掉它再点一次；或者改用「打开库目录」直接拷脚本。");
        }
        App.Settings.Save();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScriptLibrary.EnsureFolder();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{ScriptLibrary.Folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[脚本库] 打开目录失败：{ex.Message}");
            System.Windows.MessageBox.Show($"打不开这个目录：{ScriptLibrary.Folder}\n{ex.Message}",
                "打开库目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 动作库（StrokePreset）JSON 导入 / 导出 ──────────────────────────

    /// <summary>把全部动作预设导出为 [{name,type,data}] 结构的 JSON 文件。</summary>
    private void ExportPresets_Click(object sender, RoutedEventArgs e)
    {
        // 动作库导入导出现在是一份共用实现（Services/StrokePresetIO.cs），动作页与这里用同一套对话框，
        // 免得两处的文件名、提示文案与冲突处理各写一遍、日久天长跑偏。
        StrokePresetIO.ExportWithDialog(System.Windows.Window.GetWindow(this), "导出动作库",
            StrokesViewModel.AllPresets, StrokesViewModel.PresetExtras);
    }

    /// <summary>从 JSON 文件导入动作预设，冲突自动改名，并报告新增 / 重命名数量。</summary>
    private void ImportPresets_Click(object sender, RoutedEventArgs e)
    {
        StrokePresetIO.ImportWithDialog(System.Windows.Window.GetWindow(this), "导入动作库",
            StrokesViewModel.ImportPresets);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshListAsync();

    // ══════════════════════════════════════════════════════════════
    //  从音频 / 视频自动生成脚本（离线分析 → 写进脚本库）
    // ══════════════════════════════════════════════════════════════
    private string _generateMediaPath = "";
    private CancellationTokenSource? _generateCts;

    private void GenerateToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = GeneratePanel.Visibility != Visibility.Visible;
        GeneratePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GenerateToggleBtn.Content = show ? "🎵 收起生成面板" : "🎵 从音频生成…";
    }

    private void GeneratePick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择音频或视频文件",
            Filter = "音频 / 视频|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.mp4;*.mkv;*.avi;*.mov"
                   + "|音频|*.mp3;*.wav;*.m4a;*.aac;*.wma"
                   + "|视频|*.mp4;*.mkv;*.avi;*.mov"
                   + "|所有文件|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        _generateMediaPath = dialog.FileName;
        GenerateFileText.Text = System.IO.Path.GetFileName(dialog.FileName);
        GenerateFileText.Foreground = (Brush)FindResource("Muted");
        GenerateRunBtn.IsEnabled = true;
        GenerateStatus.Text = "";
        GenerateProgress.Value = 0;
    }

    private async void GenerateRun_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_generateMediaPath) || !System.IO.File.Exists(_generateMediaPath))
        {
            GenerateStatus.Text = "先选一个文件。";
            return;
        }

        var options = new AudioScriptOptions(
            AmplitudePercent: Math.Clamp(GenerateAmpSlider.Value, 10, 100),
            Sensitivity: Math.Clamp(GenerateSensSlider.Value, 0, 2),
            MinGapMs: 90,
            MultiAxis: GenerateMultiAxisCheck.IsChecked == true);

        _generateCts = new CancellationTokenSource();
        GenerateRunBtn.IsEnabled = false;
        GenerateCancelBtn.IsEnabled = true;
        GenerateProgress.Value = 0;
        GenerateStatus.Foreground = (Brush)FindResource("Muted");
        GenerateStatus.Text = "正在分析…";

        var progress = new Progress<double>(value =>
        {
            GenerateProgress.Value = Math.Clamp(value, 0, 1);
            GenerateStatus.Text = $"正在分析… {value:P0}";
        });

        try
        {
            AudioScriptResult result = await AudioScriptGenerator.GenerateAsync(
                _generateMediaPath, options, progress, _generateCts.Token);

            string baseName = System.IO.Path.GetFileNameWithoutExtension(_generateMediaPath);
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) baseName = baseName.Replace(bad, '_');
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "生成的脚本";
            ScriptLibrary.EnsureFolder();
            string target = System.IO.Path.Combine(ScriptLibrary.Folder, baseName + ".funscript");

            if (System.IO.File.Exists(target) &&
                System.Windows.MessageBox.Show(
                    $"脚本库里已经有「{System.IO.Path.GetFileName(target)}」，覆盖它吗？", "同名脚本",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                GenerateStatus.Text = "已取消保存（脚本未写入）。";
                return;
            }

            // 必须写成「无 BOM」的 UTF-8：System.Text.Encoding.UTF8 会带 BOM，
            // 而脚本库读文件用的是 ReadAllText + JSON 解析，带 BOM 会被判成"无效脚本"。
            System.IO.File.WriteAllText(target, AudioScriptGenerator.ToFunscriptJson(result), new System.Text.UTF8Encoding(false));

            GenerateStatus.Foreground = (Brush)FindResource("Success");
            GenerateStatus.Text = $"已生成：{System.IO.Path.GetFileName(target)}";
            await RefreshListAsync(target);
            SelectRowByPath(target);

            System.Windows.MessageBox.Show(
                $"已生成并保存到脚本库：\n{target}\n\n{result.Summary}\n\n" +
                "列表里已经选中它了：可以直接点「▶ 载入并播放」；想手改就到「脚本制作 → 微调波形」加载它。",
                "生成完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            GenerateStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("音频生成脚本失败", ex);
            GenerateStatus.Foreground = (Brush)FindResource("Danger");
            GenerateStatus.Text = ex.Message;
        }
        finally
        {
            _generateCts?.Dispose();
            _generateCts = null;
            GenerateRunBtn.IsEnabled = true;
            GenerateCancelBtn.IsEnabled = false;
        }
    }

    private void GenerateCancel_Click(object sender, RoutedEventArgs e)
    {
        _generateCts?.Cancel();
        GenerateStatus.Text = "正在取消…";
    }

    /// <summary>把列表里那一行选中（导入 / 生成完成后用），用户一眼能看到新脚本。</summary>
    private void SelectRowByPath(string path)
    {
        foreach (object? item in ScriptList.Items)
        {
            if (item is ScriptRow row && string.Equals(row.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                ScriptList.SelectedItem = row;
                ScriptList.ScrollIntoView(row);
                return;
            }
        }
    }

    private void ScriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PlayBtn.IsEnabled = Selected is { Valid: true };
        // 选中无效脚本时把原因写到播放卡里，省得用户只能靠悬停猜（下一次状态刷新会覆盖它）
        if (Selected is { Valid: false } bad)
            SetPlaybackWarn($"「{bad.Name}」无法解析，不能播放：{bad.Error ?? "未知原因"}");
        // 选谁就列谁的章节/书签（列表行上的 📑/🔖 标记就是提示「这一行有东西可以跳」）
        LoadChaptersFor(Selected?.FilePath);
    }

    /// <summary>极简模态输入框（用于重命名）。返回用户输入的文本；取消返回 null。</summary>
    private static string? PromptText(string title, string label, string initial)
    {
        var box = new System.Windows.Window
        {
            Title = title,
            Width = 380,
            Height = 190,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current.MainWindow,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Bg"),
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Text"),
            ResizeMode = System.Windows.ResizeMode.NoResize,
        };
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock { Text = label, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Muted") };
        grid.Children.Add(labelBlock);

        var input = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 16) };
        Grid.SetRow(input, 1);
        grid.Children.Add(input);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var ok = new Button { Content = "确定", Width = 80, Height = 32, Style = (Style)System.Windows.Application.Current.FindResource("BtnPrimary") };
        var cancel = new Button { Content = "取消", Width = 80, Height = 32, Margin = new Thickness(8, 0, 0, 0), Style = (Style)System.Windows.Application.Current.FindResource("BtnSecondary") };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        box.Content = grid;
        string? result = null;
        ok.Click += (_, _) => { result = input.Text; box.DialogResult = true; };
        cancel.Click += (_, _) => box.DialogResult = false;
        box.ShowDialog();
        return result;
    }
}
