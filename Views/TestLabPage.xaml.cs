using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hexa.Services;

namespace Hexa.Views;

public partial class TestLabPage : Page
{
    private readonly AudioReactiveService _audio = App.AudioReactive;
    private readonly RuleEngine _rules = App.RuleEngine;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Border? _levelFill;
    // 实时电平的历史曲线：150ms 一个点 × 100 个 = 最近 15 秒。
    // 用户抱怨过「对女性呻吟没反应」，所以除了瞬时电平，还要能看出"刚才那一下它到底听到没有"。
    private readonly List<double> _levelHistory = new();
    private const int LevelHistorySamples = 100;
    private bool _loadingUi;
    // 手动输入进程名：输入时只刷新识别，失焦才真正应用绑定（见 CompanionProcessBox 的两个事件）。
    private bool _applyProcessOnLostFocus;
    private string _boundEngineDisplay = "未识别";
    private string _boundEngineProcess = "";
    private bool _lastAudioToggleState;
    private bool _lastRuleToggleState;
    // ── 功能切换（2026-09）──────────────────────────────────────────────
    // 用户原话「我要的是两排、方便我选功能，我不想一直往下滑」：页面一次只显示一块卡片（ShowCard），
    // 其余两块 Collapsed。原来的两栏（CardColumnsGrid / ApplyResponsiveColumns / MeasureOverride）
    // 随之删掉 —— 只有一张卡片可见时，分两栏只会让内容更窄、更需要滚动。
    // 选中状态只记在内存里：它表示「我正在看哪一块」，不是设备行为，落盘只会让下次打开莫名停在半路。
    private string _activeCardName = "AudioCard";

    // P1：150ms 轮询不再每次都枚举前台进程；1 秒缓存一次即可。
    private string _foregroundCache = "";
    private long _foregroundCacheAt;
    private bool _foregroundCacheValid;

    // P0：声音响应首次开启会立即启动自动动作，先确认一次（按进程记忆，重进页面不重复弹）。
    private bool _audioRangeLoaded;

    // 「响应对象」和「律动方式」在设置里是同一个字段（AudioResponseMode）：
    // voice = 角色声音；energy/bass/beat = 音乐律动的三种跟法。
    // 切到角色声音时把上一次的音乐跟法记在这儿，切回来时不至于被重置成「自然起伏」。
    // （不新增设置字段：那是 AppSettings 的事，这一页只做这两行的换算。）
    private string _lastMusicMode = "energy";

    // P0：一键停止后，游戏伴随状态栏要持续提示“切回游戏不会自动启动”。
    private bool _autoFollowStoppedByEmergency;

    // ── 画面信号（观察模式 / 画面跟随）────────────────────────────────
    // 归属：这条信号是「游戏伴随」的一个动作来源（画面内容），不是声音那一路的附属。
    // 默认只测量：只有用户在伴随里把来源选成「画面内容」、绑定的程序在前台、场景强度过线，
    // 三条同时成立时服务才会下发动作（服务自己管设备，这一页只显示状态 + 改设置）。
    // 服务是**跨页面实例共享**的：自检会把这一页反复实例化，各建一个就会出现"两个页面各抓一份画面"，
    // 而抓取线程只该有一个。懒建（字段初始化时机不受页面构造顺序影响）。
    private static ScreenWatchService? _screenWatchShared;
    /// <summary>
    /// 画面信号的服务实例：<b>全进程只有一个</b>，所以先问服务自己的登记口
    /// （<see cref="ScreenWatchService.Current"/>）—— 伴随那边在开机直接进游戏时也会自动把它开起来，
    /// 这里要是各建一个，就会出现「两个实例各抓一份画面、各下一次指令」，设备会乱抖。
    /// 一个都没有时才新建（构造函数会把自己登记进去，之后 Current 就认得它了）。
    /// </summary>
    private static ScreenWatchService ScreenWatch =>
        ScreenWatchService.Current ?? (_screenWatchShared ??= new ScreenWatchService(App.Settings));
    private Border? _screenSkinFill;
    private Border? _screenSkinCenterFill;
    private Border? _screenMotionFill;
    private Border? _screenRhythmFill;
    private Border? _screenScoreFill;
    private Border? _screenModelScoreFill;
    private Border? _screenModelProgressFill;
    private Border? _screenPoseProgressFill;
    private bool _loadingScreenUi;
    private bool _lastScreenToggleState;
    // 识别模型的下载：只由用户点「下载模型」触发（先弹确认框写清档位和体积），进度在 150ms 的刷新里画。
    private bool _modelDownloading;
    private double _modelDownloadProgress;
    // 姿态模型的下载（13MB，同样是「点了才下」）。
    private bool _poseDownloading;
    private double _poseDownloadProgress;

    public TestLabPage()
    {
        InitializeComponent();
        BuildLevelMeter();
        LoadAudioDevices();
        LoadGameProfileUi();
        BuildScreenBars();
        LoadUi();
        LoadScreenWatchUi();
        // 需求3/4：游戏伴随（简易/进阶）+ 动作键联动的开关
        // 「做法」二选一：简易（进游戏套用一套动作）/ 进阶（按规则 + 声音反应）。
        // 背后还是原来两个设置（CompanionAuto / RuleEngineEnabled），所以老配置文件照样能用。
        // 事件在 XAML 上接（CompanionKind_Changed），与页面里其它下拉框保持一致。
        AudioEventCheck.Checked += (_, _) =>
        {
            if (_loadingUi) return;
            App.Settings.AudioEventMotion = true;
            App.Settings.Save();
            App.AudioReactive.Refresh();
        };
        AudioEventCheck.Unchecked += (_, _) =>
        {
            if (_loadingUi) return;
            App.Settings.AudioEventMotion = false;
            App.Settings.Save();
            App.AudioReactive.Refresh();
        };
        MultiAxisCheck.Checked += (_, _) =>
        {
            if (_loadingUi) return;
            App.Settings.MotionMultiAxis = true;
            App.Settings.Save();
        };
        MultiAxisCheck.Unchecked += (_, _) =>
        {
            if (_loadingUi) return;
            App.Settings.MotionMultiAxis = false;
            App.Settings.Save();
        };
        SilenceGateCheck.Checked += (_, _) =>
        {
            if (_loadingUi) return;
            App.Settings.GateCompanionOnSilence = true;
            App.Settings.Save();
            _audio.Refresh();   // 这个开关决定要不要采集系统声音（安静判断靠它）
        };
        SilenceGateCheck.Unchecked += (_, _) =>
        {
            if (_loadingUi) return;
            App.Settings.GateCompanionOnSilence = false;
            App.Settings.Save();
            _audio.Refresh();   // 这个开关决定要不要采集系统声音（安静判断靠它）
        };
        ActionLinkCheck.Checked += (_, _) => { if (!_loadingUi) SaveAutoFollow(); };
        AmbientOverlayCheck.Checked += (_, _) => { if (!_loadingUi) { SyncAmbientUi(); SaveAmbient(); } };
        AmbientOverlayCheck.Unchecked += (_, _) => { if (!_loadingUi) { SyncAmbientUi(); SaveAmbient(); } };
        AmbientOverlaySlider.ValueChanged += (_, e) =>
        {
            AmbientOverlayLabel.Text = $"±{(int)Math.Round(e.NewValue)}%";
            if (_loadingUi) return;
            SaveAmbient();
        };
        ActionLinkCheck.Unchecked += (_, _) => { if (!_loadingUi) SaveAutoFollow(); };
        // 「伴随关闭时，也在进入游戏时自动套用底色动作」（设置里的 CompanionAuto）：
        // 它和伴随不是两个独立效果 —— 伴随开着时这一路整段跳过，所以界面会把它置灰并说明。
        AutoPresetCheck.Checked += (_, _) => { if (!_loadingUi) SaveAutoPreset(); };
        AutoPresetCheck.Unchecked += (_, _) => { if (!_loadingUi) SaveAutoPreset(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); App.Settings.Save(); };
        _uiTimer.Tick += (_, _) => RefreshStatus();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && _alignTimer is not null) StopAlignmentTest();   // 离开页面别让测试继续动设备
            if (IsVisible)
            {
                _foregroundCacheValid = false;   // 回到页面立即刷新前台进程，不等待 1 秒缓存
                _uiTimer.Start();
                RefreshStatus();
            }
            else _uiTimer.Stop();
        };

        // 画面观察的开关是记住的：上次开着，回到这一页就继续（它平时只读画面不动设备，
        // 只有「动作来源 = 画面内容」且目标在前台、场景强度过线时才会下发动作；
        // 代价只是每秒十几帧的一点点 CPU）。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && App.Settings.ScreenWatchEnabled && !ScreenWatch.IsRunning)
            {
                ScreenWatch.Start();
                UpdateScreenToggle();
                RefreshScreenWatch();
            }
        };

        // 手动输入进程名：失焦时应用绑定；输入时实时刷新引擎识别（防抖在 RefreshBoundEngine 内）。
        CompanionProcessBox.TextChanged += (_, _) => { if (!_loadingUi) { _applyProcessOnLostFocus = true; RefreshBoundEngine(); } };
        CompanionProcessBox.LostFocus += (_, _) =>
        {
            if (_applyProcessOnLostFocus)
            {
                _applyProcessOnLostFocus = false;
                ApplyCompanionRules();
                RefreshStatus();
            }
        };

        // 自动跟随状态变化（进入/离开游戏）实时反映到状态栏。
        App.ForegroundWatcher.StateChanged += () => App.Dispatch(() => RefreshCompanionBoard());
        RefreshCompanionBoard();

        // 多输出的目标文本：先填好再挂事件，免得初始化时触发一次"用户改了"（会多写一次盘）。
        _fanoutLoading = true;
        FanoutTargetsBox.Text = App.Settings.FanoutTargets;
        _fanoutLoading = false;

        // 首次进入默认显示 ① 声音响应（选中状态记在 _activeCardName 里，见 ShowCard）。
        ShowCard(_activeCardName);
    }

    private void BuildLevelMeter()
    {
        // 绿 → 黄 → 红 的三段渐变，宽度随能量增长，越强越醒目。
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.0));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xF5, 0xB8, 0x42), 0.55));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xE5, 0x53, 0x53), 1.0));
        _levelFill = new Border
        {
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = gradient,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        LevelTrack.Child = _levelFill;
    }

    /// <summary>把当前电平推进历史（固定长度，左侧补 0，所以页面一打开曲线就是满宽）。</summary>
    private void PushLevelHistory(double energy)
    {
        _levelHistory.Add(Math.Clamp(energy, 0, 1));
        while (_levelHistory.Count > LevelHistorySamples) _levelHistory.RemoveAt(0);
    }

    /// <summary>最近 15 秒里的最高电平（比瞬时值更能回答「它听到了没有」）。</summary>
    private double PeakLevel => _levelHistory.Count == 0 ? 0 : _levelHistory.Max();

    /// <summary>把历史画成一条折线。宽度按控件实际尺寸算，窄窗口下也不会溢出。</summary>
    private void UpdateLevelHistoryLine()
    {
        if (LevelHistoryLine is null || LevelHistoryTrack is null) return;
        double width = LevelHistoryTrack.ActualWidth;
        double height = LevelHistoryTrack.ActualHeight;
        if (width <= 1 || height <= 1) return;

        int count = LevelHistorySamples;
        double step = count > 1 ? width / (count - 1) : 0;
        int offset = count - _levelHistory.Count;      // 还没攒够时从左侧留白开始画
        var points = new PointCollection(count);
        for (int i = 0; i < count; i++)
        {
            int index = i - offset;
            double value = index < 0 ? 0 : _levelHistory[index];
            double y = height - 1 - value * (height - 2);
            points.Add(new Point(i * step, y));
        }
        LevelHistoryLine.Points = points;
    }

    /// <summary>事件化动作的实时状态：最近响应了什么、累计多少次（用户能看出「它听懂了没有」）。</summary>
    private void RefreshAudioEventLabel()
    {
        if (AudioEventLabel is null) return;

        // 角色声音模式不看「事件化动作」那个勾选（人声是连续抽插），所以先说清这一路正在干什么。
        if (IsVoiceTargetSelected)
        {
            AudioEventLabel.Text = App.Settings.AudioReactiveEnabled
                ? "角色声音模式：每次呻吟算一次抽插（上面「已响应」的次数就是插了多少下），不看「事件化动作」那个勾选。"
                : "角色声音模式已选好，但声音响应还没开：点右上角「开启声音响应」才会动。";
            return;
        }

        if (!App.Settings.AudioEventMotion)
        {
            AudioEventLabel.Text = "事件化动作已关闭：正在按音量循环（声音大就动得快/深）。";
            return;
        }

        double age = App.AudioReactive.LastEventAgeSeconds;
        string ageText = double.IsPositiveInfinity(age) ? "还没响应过" : $"{age:0.0} 秒前";
        // 把「响应了什么」翻成人话：用户抱怨过「对呻吟没反应」，这里能一眼看出它认成了什么。
        string kindText = App.AudioReactive.LastEventKind switch
        {
            MotionEventKind.Impact => "冲击（爆炸、撞击这类短促的响声）",
            MotionEventKind.Swell => "渐强（音乐起伏、引擎轰鸣这类慢起慢落的声音）",
            MotionEventKind.Voice => "呻吟（一次呻吟 = 一次抽插）",
            MotionEventKind.Beat => "节拍（鼓点、一下一下的响声）",
            _ => "安静（还没识别到事件）",
        };
        AudioEventLabel.Text =
            $"最近响应：{kindText} · 强度 {App.AudioReactive.LastEventStrength:P0} · 累计 {App.AudioReactive.EventCount} 次 · {ageText}";
    }

    /// <summary>刷新「声音响应」的实时状态行：听到多少、在不在动、为什么不动。</summary>
    private void RefreshAudioState()
    {
        if (AudioStateLabel is null) return;

        if (!App.Settings.AudioReactiveEnabled)
        {
            AudioStateLabel.Foreground = (Brush)FindResource("Muted");
            AudioStateLabel.Text = _audio.Capturing
                ? "声音响应没开：上面两条只是在听（游戏伴随判「安静」要用），不会让设备动。点右上角「开启声音响应」才开始跟着声音动。"
                : "未开启。开启后：安静时设备不动，声音越大动得越快越深。";
            return;
        }

        if (!_audio.Capturing)
        {
            AudioStateLabel.Foreground = (Brush)FindResource("Warning");
            AudioStateLabel.Text = _audio.StartError is { Length: > 0 } startError
                ? startError
                : "正在准备监听声音…若一直这样，把「声音来源」换成另一个再试。";
            return;
        }

        if (_audio.BlockedReason is { Length: > 0 } blocked)
        {
            AudioStateLabel.Foreground = (Brush)FindResource("Warning");
            AudioStateLabel.Text = $"听到声音了，但指令下不去：{blocked}";
            return;
        }

        if (_audio.MotionActive)
        {
            AudioStateLabel.Foreground = (Brush)FindResource("Success");
            AudioStateLabel.Text = IsVoiceTargetSelected
                ? $"正在抽插（人声强度 {_audio.Envelope:P0}）：越响插得越深、越快跟得越紧；人声没了还会再抽 1.5 秒、一下比一下浅。"
                : $"正在跟着声音动（当前强度 {_audio.Envelope:P0}）：声音变小动作就变浅，安静 0.7 秒后慢慢回中停下。";
            return;
        }

        // 听得到、但没动 —— 这正是「对呻吟没反应」时用户看到的样子，所以直接告诉他下一步点哪里。
        if (_audio.Energy > 0.05)
        {
            AudioStateLabel.Foreground = (Brush)FindResource("Warning");
            AudioStateLabel.Text = IsVoiceTargetSelected
                ? "听到了，但没到「算一次呻吟」的大小：把「灵敏度」调到高，或让她再响一点（这一路只看人声频段，背景音乐再响也不算）。"
                : App.Settings.AudioEventMotion
                    ? "听到了（上面的条在动），但声音还没够到「触发一次动作」的大小。把「灵敏度」调到高，或者关掉「事件化动作」改成跟着音量一直动。"
                    : "听到了，但还没到引起动作的强度：把「灵敏度」调高，或把「行程」调大试试。";
            return;
        }

        AudioStateLabel.Foreground = (Brush)FindResource("Muted");
        AudioStateLabel.Text = "在听，现在是安静的 —— 设备不会动（放点声音试试，或提高灵敏度）。";
    }

    private void LoadAudioDevices()
    {
        _loadingUi = true;
        AudioDeviceCombo.Items.Clear();
        foreach (AudioOutputDevice device in AudioReactiveService.GetOutputDevices())
            AudioDeviceCombo.Items.Add(device);
        AudioDeviceCombo.SelectedItem = AudioDeviceCombo.Items.Cast<AudioOutputDevice>().FirstOrDefault(device =>
            string.Equals(device.Id, App.Settings.AudioDeviceId, StringComparison.OrdinalIgnoreCase));
        if (!_audioRangeLoaded && AudioRangeSlider != null)
        {
            // 设置里存的是半幅值（设备在 50±值 之间），界面按「占全程的百分比」显示，所以乘 2
            AudioRangeSlider.Value = Math.Clamp(App.Settings.AudioMotionRange * 2, 10, 100);
            AudioRangeLabel.Text = $"{AudioRangeSlider.Value:0}%";
            _audioRangeLoaded = true;
        }
        if (AudioDeviceCombo.SelectedItem == null && AudioDeviceCombo.Items.Count > 0)
        {
            // 设置里存的那个声卡已经不在了（拔掉/换驱动）：**必须把设置也清掉**。
            // 以前只是把下拉选到第 0 项、又不写回设置 ⇒ 服务还照旧 id 去打开设备、失败被吞，
            // 下拉显示"设备A/系统默认"而实际什么都没采——界面与事实不符（子代理审计发现）。
            if (!string.IsNullOrWhiteSpace(App.Settings.AudioDeviceId))
            {
                App.Settings.AudioDeviceId = "";
                App.Settings.Save();
                AudioDeviceHint.Text = "上次选的声音设备已经不在了，已改回「系统默认」——再听不到就把「声音来源」换一个试试。";
                AudioDeviceHint.Visibility = Visibility.Visible;
            }
            AudioDeviceCombo.SelectedIndex = 0;
        }
        // 空状态：一个播放设备都没有时，别让用户对着一片空白找原因
        AudioDeviceHint.Visibility = AudioDeviceCombo.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 「响应对象」+「律动方式」两行合成一个设置（AudioResponseMode）：
        // 设置里是 voice → 选「角色声音」；是 energy/bass/beat → 选「音乐律动」并把律动方式摆上。
        bool voiceMode = string.Equals(App.Settings.AudioResponseMode, "voice", StringComparison.OrdinalIgnoreCase);
        if (!voiceMode) _lastMusicMode = App.Settings.AudioResponseMode;
        SelectByTag(AudioModeCombo, _lastMusicMode);
        SelectByTag(AudioTargetCombo, voiceMode ? "voice" : "music");
        SyncAudioModeVisibility(voiceMode);
        SelectByTag(AudioSensitivityCombo, SensitivityFor(App.Settings.AudioSensitivity));
        _loadingUi = false;
    }

    private void LoadUi()
    {
        _loadingUi = true;
        CompanionProcessBox.Text = App.Settings.CompanionProcess;
        SelectByTag(CompanionBaseCombo, App.Settings.CompanionBaseMode);
        SelectByTag(CompanionReactionCombo, App.Settings.CompanionReaction);
        SelectByTag(CompanionSensitivityCombo, App.Settings.CompanionSensitivity);
        // 动作来源（旧配置里没有这个字段时，AppSettings 已按旧「做法」迁移好）
        SelectByTag(CompanionKindCombo, App.Settings.CompanionSource);
        ActionLinkCheck.IsChecked = App.Settings.ActionLinkEnabled;
        AutoPresetCheck.IsChecked = App.Settings.CompanionAuto;
        // 底色动作/强度：两条路共用的一套
        SelectByTag(CompanionIntensityCombo, IntensityTagFor(App.Settings.CompanionBaseIntensity));
        UpdateCompanionPanels();
        _loadingUi = false;
        _applyProcessOnLostFocus = false;
        RefreshBoundEngine();
        UpdateAudioToggle();
        UpdateRuleToggle();
        RefreshCompanionBoard();
        RefreshStatus();
    }

    private static string IntensityTagFor(double value) => value switch
    {
        <= 0.8 => "0.7",
        >= 1.2 => "1.4",
        _ => "1.0",
    };

    private static void SelectByTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (combo.SelectedItem == null && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string SensitivityFor(double value) => value switch
    {
        <= 0.75 => "low",
        >= 1.35 => "high",
        _ => "standard",
    };

    private static double SensitivityValue(string tag) => tag switch
    {
        "low" => 0.72,
        "high" => 1.45,
        _ => 1.0,
    };

    private static ComboBoxItem? SelectedItem(ComboBox combo) => combo.SelectedItem as ComboBoxItem;

    private void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || AudioDeviceCombo.SelectedItem is not AudioOutputDevice device) return;
        App.Settings.AudioDeviceId = device.Id;
        ScheduleSave();
        if (_audio.Capturing) _audio.Refresh();
    }

    private void AudioMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || SelectedItem(AudioModeCombo) is not { Tag: string mode }) return;
        _lastMusicMode = mode;
        // 角色声音下「律动方式」是收起来的：这时候它不该去改设置（改了会把 voice 顶掉）。
        if (IsVoiceTargetSelected) return;
        App.Settings.AudioResponseMode = mode;
        ScheduleSave();
    }

    /// <summary>「响应对象」二选一：角色声音（voice）/ 音乐律动（用「律动方式」那一项）。</summary>
    private void AudioTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || SelectedItem(AudioTargetCombo) is not { Tag: string target }) return;
        bool voice = string.Equals(target, "voice", StringComparison.Ordinal);
        // 一个设置字段表示两种模式：角色声音写死 "voice"，音乐律动写「律动方式」那一项。
        App.Settings.AudioResponseMode = voice ? "voice" : _lastMusicMode;
        SyncAudioModeVisibility(voice);
        // 换对象 = 换一路信号源：让采集从干净状态重新起步（和「事件化动作」那个勾选的做法一致），
        // 否则检测器里可能还留着很久以前的统计量，切过去头几帧会算出一个假的跃升。
        _audio.Refresh();
        ScheduleSave();
    }

    /// <summary>「响应对象」现在选的是不是「角色声音」（Tag = voice）。</summary>
    private bool IsVoiceTargetSelected =>
        AudioTargetCombo?.SelectedItem is ComboBoxItem { Tag: string tag } && tag == "voice";

    /// <summary>
    /// 切「响应对象」时只改可见性，不删控件：「律动方式」在人声模式下没有意义
    ///（人声走的是抽插，不分自然起伏/低音/鼓点），但它的选择值要留着，切回音乐时接着用。
    /// </summary>
    private void SyncAudioModeVisibility(bool voice)
    {
        if (AudioMusicModePanel is null) return;    // XAML 解析中途可能还没建好
        AudioMusicModePanel.Visibility = voice ? Visibility.Collapsed : Visibility.Visible;
        AudioVoiceNote.Visibility = voice ? Visibility.Visible : Visibility.Collapsed;
        AudioTargetHint.Text = voice
            ? "只认人声频段（150–1200Hz）：呻吟、喘息、说话会让设备动，鼓点、低音、爆炸声推不动它。每次呻吟 = 一次抽插（插进去、再抽出来）：越响抽得越深、越快跟得越紧；声音停了还会再抽约 1.5 秒、一下比一下浅，然后停住。"
            : "跟着鼓点和低音持续律动：安静时不动，有声音就一直在动。选下面的「律动方式」决定怎么跟。";
    }

    private void AudioSensitivity_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || SelectedItem(AudioSensitivityCombo) is not { Tag: string sensitivity }) return;
        App.Settings.AudioSensitivity = SensitivityValue(sensitivity);
        ScheduleSave();
    }

    private void AudioToggle_Click(object sender, RoutedEventArgs e)
    {
        // 现在的行为：开启后设备**不会**自己动，只有真的听到声音才跟着动（静音超时会缓慢回中停下），
        // 所以不需要再弹「设备会立刻动」的确认。
        App.Settings.AudioReactiveEnabled = !App.Settings.AudioReactiveEnabled;
        App.Settings.Save();
        _audio.Refresh();
        UpdateAudioToggle();
        RefreshAudioState();
    }

    /// <summary>行程滑块：写回设置（声音最大时主轴来回的幅度）。</summary>
    private void AudioRange_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析阶段（Minimum 生效）会先触发一次 ValueChanged，此时标签还没建好，
        // 不能把那次的值当成用户设置写回磁盘（否则行程会被记成最小值 5%）。
        if (AudioRangeLabel is null || !_audioRangeLoaded) return;
        AudioRangeLabel.Text = $"{e.NewValue:0}%";
        // 界面是「占全程百分比」，设置存的是半幅值：100% 全程 → 半幅 50。老配置行为不变。
        App.Settings.AudioMotionRange = Math.Clamp(e.NewValue / 2, 5, 60);
        if (_loadingUi) return;
        App.Settings.Save();
    }

    /// <summary>声音响应卡片旁的「■ 停止」：复用底部一键停止（含急停与关闭自动跟随）。</summary>
    private void AudioStopBtn_Click(object sender, RoutedEventArgs e) => StopBtn_Click(sender, e);

    private async void CaptureProcess_Click(object sender, RoutedEventArgs e)
    {
        CaptureProcessBtn.IsEnabled = false;
        CaptureHint.Foreground = (Brush)FindResource("Muted");
        CaptureHint.Text = "现在切到游戏窗口去，3 秒后自动记下当时最前面的那个程序…";
        try
        {
            await Task.Delay(3000);
            string captured = _rules.ReadForegroundProcessName();
            if (string.IsNullOrWhiteSpace(captured))
            {
                // P1：抓取失败不能清空已绑定的游戏名。
                CaptureHint.Foreground = (Brush)FindResource("Warning");
                CaptureHint.Text = $"没读到前台窗口，原来的绑定没动：{App.Settings.CompanionProcess}。再点一次「3 秒后抓取」，这次记得切到游戏里去。";
                return;
            }
            CompanionProcessBox.Text = captured.Trim();
            _applyProcessOnLostFocus = false;
            App.Settings.CompanionProcess = CompanionProcessBox.Text.Trim();
            App.Settings.Save();
            GameEngineInfo engine = _rules.DetectEngine(App.Settings.CompanionProcess);
            CaptureHint.Foreground = (Brush)FindResource("Muted");
            CaptureHint.Text = $"已绑定 {App.Settings.CompanionProcess} · 识别为 {engine.DisplayName}。只有它在前台时设备才会动。";
            ApplyCompanionRules();
            RefreshBoundEngine();
        }
        finally
        {
            CaptureProcessBtn.IsEnabled = true;
        }
    }

    private void CompanionSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.CompanionBaseMode = (SelectedItem(CompanionBaseCombo)?.Tag as string) ?? "organic_flow";
        App.Settings.CompanionReaction = (SelectedItem(CompanionReactionCombo)?.Tag as string) ?? "sound";
        App.Settings.CompanionSensitivity = (SelectedItem(CompanionSensitivityCombo)?.Tag as string) ?? "standard";
        App.Settings.Save();
        ApplyCompanionRules();
        RefreshBoundEngine();
        RefreshCompanionBoard();
        RefreshGameProfileText();
        _audio.Refresh();
    }

    /// <summary>
    /// 「动作来源」下拉框（控件名沿用 CompanionKindCombo，避免动到已有 x:Name）：
    /// auto / sound / telemetry 三选一，决定设备听谁的。
    /// </summary>
    private void CompanionKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        ApplyCompanionSource();
    }

    /// <summary>「底色强度」下拉框（CompanionAuto_Changed 这个名字是历史遗留，页面里没别的下拉框用它）。</summary>
    private void CompanionAuto_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        SaveAutoFollow();
    }

    /// <summary>氛围叠加：勾选状态与滑杆可用性。</summary>
    private void SyncAmbientUi() => AmbientOverlaySlider.IsEnabled = AmbientOverlayCheck.IsChecked == true;

    private void SaveAmbient()
    {
        bool enabled = AmbientOverlayCheck.IsChecked == true;
        App.Settings.AmbientOverlay = enabled;
        App.Settings.AmbientOverlayAmount = AmbientOverlaySlider.Value;
        App.Settings.Save();
        // 这个开关决定要不要采集系统声音（叠加层靠音频特征吃饭）：
        // 不调 Refresh 的话，单独勾上它什么都不会发生（"勾了没反应"的死开关）。
        App.AudioReactive.Refresh();
    }

    /// <summary>
    /// 测「设备到位时间」：用当前舒适档把安全限速器真跑一遍，算出设备走 60 个行程点要多久，
    /// 再给出「对齐偏移」的建议值（设备到位比事件晚，所以让它提前一点发出）。
    /// 画面本身的延迟 Hexa 看不到，只能靠滑杆手感微调——这里只负责能算的那一半。
    /// </summary>
    private void LatencyMeasure_Click(object sender, RoutedEventArgs e)
    {
        var profile = Hexa.Models.ComfortProfile.Resolve(App.Settings.ComfortProfile);
        double travelMs = LatencyCalibrator.EstimateDefaultTravelMs(profile, axisIndex: 0);
        int suggestion = LatencyCalibrator.SuggestCompensationMs(travelMs);
        App.Settings.MotionCalibrationMs = suggestion;
        App.Settings.Save();
        _loadingUi = true;
        AlignmentSlider.Value = suggestion;
        AlignmentLabel.Text = $"{suggestion} ms";
        _loadingUi = false;
        LatencyMeasureText.Foreground = (Brush)FindResource("Muted");
        LatencyMeasureText.Text =
            $"（{profile.Label}档）设备走完一次大行程大约要 {travelMs:0} 毫秒，已把上面的偏移自动设成 {suggestion} 毫秒（让它提前动）。" +
            "这是设备自己的滞后；画面那边 Hexa 看不到，剩下的用滑块凭感觉微调。";
        AppLogger.Info($"延迟测量：{profile.Label}档 到位 {travelMs:0}ms → 建议补偿 {suggestion}ms");
    }

    /// <summary>初始化「安静时停住」勾选与「本游戏配置」文案。</summary>
    private void LoadGameProfileUi()
    {
        _loadingUi = true;
        SilenceGateCheck.IsChecked = App.Settings.GateCompanionOnSilence;
        AmbientOverlayCheck.IsChecked = App.Settings.AmbientOverlay;
        AmbientOverlaySlider.Value = Math.Clamp(App.Settings.AmbientOverlayAmount,
            AmbientOverlaySlider.Minimum, AmbientOverlaySlider.Maximum);
        AmbientOverlayLabel.Text = $"±{(int)Math.Round(AmbientOverlaySlider.Value)}%";
        SyncAmbientUi();
        AudioEventCheck.IsChecked = App.Settings.AudioEventMotion;
        MultiAxisCheck.IsChecked = App.Settings.MotionMultiAxis;
        AlignmentSlider.Value = Math.Clamp(App.Settings.MotionCalibrationMs, -200, 200);
        AlignmentLabel.Text = $"{App.Settings.MotionCalibrationMs:+#;-#;0} ms";
        _loadingUi = false;
        RefreshGameProfileText();
    }

    private void RefreshGameProfileText()
    {
        string process = (App.Settings.CompanionProcess ?? "").Trim();
        if (process.Length == 0)
        {
            GameProfileText.Text = "先在「目标游戏」里填进程名（或点「3 秒后抓取」），才能给它单独记住一套配置。";
            SaveProfileBtn.IsEnabled = false;
            ClearProfileBtn.IsEnabled = false;
            return;
        }

        // 专属配置对两条路都生效（随时可以用，不再是「只在简易做法里管用」）：
        // 规则引擎那一侧由 GameCompanionRules.Apply 在生成 companion-base 时读它，
        // 伴随关闭时由 ForegroundWatcher 进游戏时读它。
        if (App.Settings.GameProfiles.TryGetValue(process, out Hexa.Models.GameProfile? profile))
        {
            string label = ProfileModeLabel(profile.Mode);
            string intensity = profile.Intensity switch
            {
                <= 0.8 => "轻柔",
                >= 1.2 => "激烈",
                _ => "常规",
            };
            GameProfileText.Text = $"已给「{process}」存了专属配置：进这个游戏时自动套用「{label}」、强度{intensity}（伴随开着时也用它）。";
            ClearProfileBtn.IsEnabled = true;
        }
        else
        {
            GameProfileText.Text = $"「{process}」还没有专属配置：进游戏时用上面那套「底色动作 / 底色强度」。专属配置会在进入该游戏时自动套用。";
            ClearProfileBtn.IsEnabled = false;
        }
        SaveProfileBtn.IsEnabled = true;
    }

    /// <summary>动作 Tag → 界面上的中文名（找不到就用 Tag 本身）。</summary>
    private string ProfileModeLabel(string mode) =>
        (CompanionBaseCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))?.Content as string) ?? mode;

    /// <summary>把当前「底色动作 / 底色强度」记成这个游戏的专属配置（两条路都会用它）。</summary>
    private void SaveGameProfile_Click(object sender, RoutedEventArgs e)
    {
        string process = (App.Settings.CompanionProcess ?? "").Trim();
        if (process.Length == 0) return;

        App.Settings.GameProfiles[process] = new Hexa.Models.GameProfile
        {
            Process = process,
            Mode = (SelectedItem(CompanionBaseCombo)?.Tag as string) ?? App.Settings.CompanionBaseMode,
            Intensity = SelectedItem(CompanionIntensityCombo)?.Tag is string tag && double.TryParse(tag, out double value)
                ? value
                : App.Settings.CompanionBaseIntensity,
        };
        App.Settings.Save();
        // 规则引擎那条路是「生成规则时」读专属配置的：存完必须重新生成一次，否则要等下次改设置才生效。
        ApplyCompanionRules();
        RefreshGameProfileText();
        AutoFollowNote.Text = $"已记住「{process}」的专属配置，以后进这个游戏会自动用它。";
        AutoFollowNote.Visibility = Visibility.Visible;
    }

    private void ClearGameProfile_Click(object sender, RoutedEventArgs e)
    {
        string process = (App.Settings.CompanionProcess ?? "").Trim();
        if (process.Length == 0) return;
        if (App.Settings.GameProfiles.Remove(process))
        {
            App.Settings.Save();
            ApplyCompanionRules();   // 同上：让规则引擎立刻回到全局的底色动作/强度
            RefreshGameProfileText();
            AutoFollowNote.Text = $"已删除「{process}」的专属配置，改回跟随全局设置。";
            AutoFollowNote.Visibility = Visibility.Visible;
        }
    }

    // ── 对齐校准（±200ms）：把"画面/声音"和"设备动作"对上 ───────────────
    private DispatcherTimer? _alignTimer;
    private bool _alignPhase;

    private void Alignment_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AlignmentLabel is null || !_audioRangeLoaded) return;
        int ms = (int)Math.Round(e.NewValue);
        AlignmentLabel.Text = $"{ms:+#;-#;0} ms";
        if (_loadingUi) return;
        App.Settings.MotionCalibrationMs = Math.Clamp(ms, -200, 200);   // 写盘在停止测试时统一做
    }

    private void AlignmentTest_Click(object sender, RoutedEventArgs e)
    {
        if (_alignTimer is not null) { StopAlignmentTest(); return; }

        App.Settings.Save();
        if (!App.Engine.CanRun)
        {
            // 光改 ToolTip 用户看不到，所以把原因直接写在面板上
            string why = App.Serial.IsOpen
                ? "设备现在不能动（还在急停锁定，或被别的模式占着）：先点左下角侧栏的「全部归中」解锁。"
                : "设备还没连接：先到「设置」页连接设备，再回来测。";
            AlignmentTestBtn.ToolTip = why;
            LatencyMeasureText.Foreground = (Brush)FindResource("Warning");
            LatencyMeasureText.Text = why;
            return;
        }

        _alignPhase = false;
        _alignTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };   // 1Hz 的半周期
        _alignTimer.Tick += (_, _) => AlignmentTick();
        _alignTimer.Start();
        AlignmentTestBtn.Content = "■ 停止测试";
        AlignmentTestBtn.Style = (Style)FindResource("BtnDanger");
    }

    /// <summary>1Hz 测试：方块亮的同时设备动一下，交替进行，方便肉眼把两边对齐。</summary>
    private void AlignmentTick()
    {
        _alignPhase = !_alignPhase;
        if (_alignPhase)
        {
            AlignmentFlash.Background = (SolidColorBrush)FindResource("Primary");
            App.Engine.BlendTo([78, 50, 50, 50, 50, 50], 0.16);
        }
        else
        {
            AlignmentFlash.Background = (SolidColorBrush)FindResource("SurfaceRaised");
            App.Engine.BlendTo([30, 50, 50, 50, 50, 50], 0.16);
        }
    }

    private void StopAlignmentTest()
    {
        _alignTimer?.Stop();
        _alignTimer = null;
        AlignmentFlash.Background = (SolidColorBrush)FindResource("SurfaceRaised");
        AlignmentTestBtn.Content = "▶ 测试对齐";
        AlignmentTestBtn.Style = (Style)FindResource("BtnSecondary");
        App.Settings.MotionCalibrationMs = Math.Clamp((int)Math.Round(AlignmentSlider.Value), -200, 200);
        App.Settings.Save();
        App.Engine.EaseDown(0.4);      // 缓降回中，别停在测试姿态
    }

    private void SaveAutoFollow()
    {
        App.Settings.ActionLinkEnabled = ActionLinkCheck.IsChecked == true;
        // 底色动作/强度：这是两条路共用的一套设置。旧的 CompanionIntensity 只是跟着写一份，
        // 让 AppSettings.Normalize 的「老配置迁移」不会再把用户的新选择覆盖回去。
        if (SelectedItem(CompanionIntensityCombo)?.Tag is string tag && double.TryParse(tag, out double value))
        {
            App.Settings.CompanionBaseIntensity = value;
            App.Settings.CompanionIntensity = value;
        }
        App.Settings.Save();
        ApplyCompanionRules();
        RefreshCompanionBoard();
        RefreshGameProfileText();
        if (AutoFollowNote.Text.Length > 0) AutoFollowNote.Visibility = Visibility.Visible;
        // 用户重新开启自动跟随/操作联动后，隐藏停止提示，避免误导。
        if (App.Settings.CompanionAuto || App.Settings.RuleEngineEnabled || ActionLinkCheck.IsChecked == true)
        {
            _autoFollowStoppedByEmergency = false;
            StopHintText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 「伴随关闭时，也在进入游戏时自动套用这套底色动作」= 设置里的 CompanionAuto（ForegroundWatcher 那一路）。
    /// 伴随开着时这一路会整段跳过，所以界面上把它置灰并说明 —— 不让用户以为两者是独立生效的。
    /// </summary>
    private void SaveAutoPreset()
    {
        App.Settings.CompanionAuto = AutoPresetCheck.IsChecked == true;
        App.Settings.Save();
        if (App.Settings.CompanionAuto)
        {
            // 用户自己又把它打开了：设备确实会在进游戏时动起来，「不会再自动启动」的提示要撤掉。
            _autoFollowStoppedByEmergency = false;
            StopHintText.Visibility = Visibility.Collapsed;
        }
        RefreshCompanionBoard();
    }

    private void ApplyCompanionRules()
    {
        // 用户很容易连 .exe 一起粘进来，而前台进程名比对时是不带 .exe 的：
        // 不在这里统一剥掉，就会出现「名字填了、开关也开了，设备就是不动」。
        string typed = (CompanionProcessBox.Text ?? "").Trim();
        string process = GameTelemetryProtocol.NormalizeProcessName(typed);
        if (!string.Equals(typed, process, StringComparison.Ordinal))
        {
            bool wasLoading = _loadingUi;
            _loadingUi = true;                  // 避免 TextChanged 又把「失焦时应用」标记置回来
            try { CompanionProcessBox.Text = process; }
            finally { _loadingUi = wasLoading; }
        }
        App.Settings.CompanionProcess = process;
        GameCompanionRules.Apply(App.Settings);
        App.Settings.Save();
        if (App.Settings.RuleEngineEnabled) _rules.Refresh();
    }

    private void RefreshBoundEngine()
    {
        string process = (CompanionProcessBox.Text ?? "").Trim();
        if (string.Equals(_boundEngineProcess, process, StringComparison.OrdinalIgnoreCase)) return;
        _boundEngineProcess = process;
        if (process.Length == 0)
        {
            _boundEngineDisplay = "未识别";
            return;
        }
        // 引擎识别会读取进程 MainModule，放后台执行，避免卡 UI 线程。
        _ = Task.Run(() =>
        {
            try
            {
                GameEngineInfo info = _rules.DetectEngine(process);
                Dispatcher.InvokeAsync(() =>
                {
                    if (string.Equals(_boundEngineProcess, process, StringComparison.OrdinalIgnoreCase))
                        _boundEngineDisplay = info.DisplayName;
                });
            }
            catch
            {
                // 识别失败保持“未识别”，不影响伴随功能。
            }
        });
    }

    /// <summary>当前动作来源：auto / screen / sound / telemetry（空值在 AppSettings 里已按旧「做法」迁移过）。</summary>
    private string CompanionSource => App.Settings.CompanionSource;

    /// <summary>动作来源的白话名（给状态板与提示用）。</summary>
    private static string CompanionSourceLabel(string source) => source switch
    {
        "sound"     => "只用声音",
        "telemetry" => "只用游戏信号",
        "screen"    => "画面内容",
        _           => "自动",
    };

    /// <summary>
    /// 「动作来源」切换：只换动作从哪来 —— 什么时候动、什么时候停完全一样（都只看绑定的游戏在不在前台）。
    /// 换完要重新生成规则（「只用游戏信号」时声音兜底那条 reaction 规则会被停用），但不用重启伴随。
    /// </summary>
    private void ApplyCompanionSource()
    {
        string source = (SelectedItem(CompanionKindCombo)?.Tag as string) ?? "auto";
        App.Settings.CompanionSource = source;
        App.Settings.Save();
        ApplyCompanionRules();   // 按新的来源重新生成规则（重开伴随时才真正接管）
        UpdateCompanionPanels();
        RefreshCompanionBoard();
        RefreshGameProfileText();
        RefreshScreenWatch();    // 画面信号卡里那句「现在这条信号会不会驱动设备」跟着来源变
    }

    /// <summary>刷新「动作来源」下方那句白话解释、伴随开关文案，以及共用的专属配置说明。</summary>
    private void UpdateCompanionPanels()
    {
        if (KindHintText is not null)
        {
            KindHintText.Text = CompanionSource switch
            {
                "sound" => "只按声音来：底色动作照跑，声音变大才加强；游戏发来的画面数据一律不看。",
                "telemetry" => "只认游戏发来的数据（装过游戏桥接插件的游戏选它）：收不到就完全不动 —— 后台再吵也不会突然动起来。",
                "screen" => "跟着画面里那个节奏走：绑定的程序在前台、看的窗口也是它的窗口、画面里场景强度过线时才动；"
                    + "画面里没事发生就缓降回中停着。要先到「测试台 · 画面信号」里开启观察并挑好窗口。",
                _ => "推荐：游戏给动作就跟游戏（最精确）；游戏没给就看画面内容（过线才动）；画面里也没事发生才用声音兜底。",
            };
        }
        // 伴随开着时，进游戏本来就会自动套用底色动作 —— 这个开关这时不生效，置灰并说清楚。
        bool companionOn = App.Settings.RuleEngineEnabled;
        AutoPresetCheck.IsEnabled = !companionOn;
        if (AutoPresetHint is not null)
        {
            AutoPresetHint.Text = companionOn
                ? "伴随开着：进游戏本来就会自动套用上面的底色动作，这个开关现在不生效（关掉伴随后它才管用）。"
                : "伴随关着：勾上它，进游戏照样会自动套用上面的底色动作（这个游戏的专属配置优先）。";
        }
        RefreshGameProfileText();
        UpdateRuleToggle();
    }

    private void RuleToggle_Click(object sender, RoutedEventArgs e)
    {
        // 开关只管「伴随」（规则引擎那条路）；「伴随关闭时自动套底色动作」是下面那个独立勾选。
        if (App.Settings.RuleEngineEnabled)
        {
            App.Settings.RuleEngineEnabled = false;
            _rules.Refresh();
            App.Settings.Save();
            UpdateCompanionPanels();
            RefreshCompanionBoard();
            StopHintText.Visibility = Visibility.Collapsed;
            return;
        }

        // 不绑游戏的话 ForegroundWatcher / 规则引擎都不会动，
        // 用户看到"已开启"但设备永远不动，还以为是坏了。
        if (string.IsNullOrWhiteSpace(CompanionProcessBox.Text))
        {
            CaptureHint.Foreground = (Brush)FindResource("Muted");
            CaptureHint.Text = "还没绑游戏：先在左边填进程名，或点「3 秒后抓取」。不绑的话设备永远不动。";
            CompanionProcessBox.Focus();
            return;
        }
        ApplyCompanionRules();
        App.Settings.RuleEngineEnabled = true;
        App.Settings.Save();
        _rules.Refresh();
        _audio.Refresh();
        UpdateCompanionPanels();
        RefreshCompanionBoard();
    }

    private void SoundFallbackToggle_Click(object sender, RoutedEventArgs e) =>
        ToggleFold(SoundFallbackToggle, SoundFallbackPanel, "声音兜底怎么动（激烈场面时 · 反应灵敏度）");

    private static void ToggleFold(Button toggle, UIElement panel, string title)
    {
        bool expand = panel.Visibility != Visibility.Visible;
        panel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        toggle.Content = (expand ? "▾ " : "▸ ") + title;
    }

    /// <summary>「对不齐 / 延迟校准」的展开与收起（默认收起，主参数不被一屏滚动挤下去）。</summary>
    private void AlignmentFold_Click(object sender, RoutedEventArgs e) =>
        ToggleFold(AlignmentFoldBtn, AlignmentFoldPanel, "对不齐 / 延迟校准（进阶）");

    /// <summary>顶部功能按钮：一次只显示选中的那一块卡片（其余 Collapsed，页面再长也不用一路往下滑）。</summary>
    private void FeatureSwitch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name) ShowCard(name);
    }

    /// <summary>
    /// 只显示一张卡片，并把对应的功能按钮高亮（BtnPrimary），其余按钮降回 BtnGhost。
    /// 名字不认识时回到 ① 声音响应 —— 绝不出现「三块全收起、满屏空白」那种状态。
    /// </summary>
    private void ShowCard(string cardName)
    {
        (string Name, FrameworkElement Card)[] cards =
        {
            ("AudioCard", AudioCard),
            ("CompanionCard", CompanionCard),
            ("ScreenCard", ScreenCard),
            ("SimulatorCard", SimulatorCard),
            ("AyvaCard", AyvaCard),
            ("FanoutCard", FanoutCard),
        };
        if (!cards.Any(card => card.Name == cardName)) cardName = cards[0].Name;
        _activeCardName = cardName;

        foreach ((string name, FrameworkElement card) in cards)
            card.Visibility = name == cardName ? Visibility.Visible : Visibility.Collapsed;

        foreach (Button btn in FeatureSwitchGrid.Children.OfType<Button>())
            btn.Style = (Style)FindResource((btn.Tag as string) == cardName ? "BtnPrimary" : "BtnGhost");

        // 切过来立刻刷一次：否则最多 150ms 显示的是上一块卡片的旧文案。
        RefreshStatus();
    }

    /// <summary>进入/退出模拟设备：没插设备也能把整条链路走一遍（指令只记录、不下发）。</summary>
    private void SimToggle_Click(object sender, RoutedEventArgs e)
    {
        if (App.Serial.IsSimulation)
        {
            App.Settings.SimulationMode = false;
            App.Serial.SetSimulationMode(false);
            App.Settings.Save();
            // 退出模拟后按自动连接的设置去连真机（没设备时它会自己失败，不弹错）
            if (App.Settings.AutoConnect && App.Settings.Port.Length > 0)
                _ = App.Serial.ConnectAsync(App.Settings.Port, App.Settings.FallbackPort);
        }
        else
        {
            // 真串口如果开着先断开，免得两条输出路径打架
            if (App.Serial.IsOpen) App.Serial.Disconnect();
            App.Settings.SimulationMode = true;
            App.Settings.Save();
            App.Serial.SetSimulationMode(true);
        }
        RefreshSimulatorCard();
    }

    /// <summary>模拟器那一格：状态 + 指令计数 + 最后几条指令（只在这一格可见时刷新）。</summary>
    private void RefreshSimulatorCard()
    {
        if (SimulatorCard.Visibility != Visibility.Visible) return;

        bool sim = App.Serial.IsSimulation;
        SimStatusBar.Background = (System.Windows.Media.Brush)FindResource(sim ? "Accent" : "Muted");
        SimStatusText.Text = sim
            ? "模拟中：Hexa 以为连着一台设备，指令只记录、不下发，机器不会动"
            : (App.Serial.IsOpen
                ? $"没在模拟：已连接 {App.Serial.PortName}"
                : "没在模拟，但设备还没连上（去「设置」页连）");
        SimToggleBtn.Content = sim ? "退出模拟设备" : "进入模拟设备";

        IReadOnlyList<string> cmds = App.Serial.SimulationCommands;
        SimCountText.Text = cmds.Count == 0 ? "还没记录到指令" : $"已记录 {cmds.Count} 条指令";
        int take = Math.Min(8, cmds.Count);
        SimLogText.Text = cmds.Count == 0
            ? "（还没收到指令）"
            : string.Join("\n", cmds.Skip(cmds.Count - take));
    }

    /// <summary>网页入口那一格：开关 Ayva 的 WebSocket 服务（端口写在界面上，网页照着填）。</summary>
    private void AyvaToggle_Click(object sender, RoutedEventArgs e)
    {
        if (App.AyvaWeb.IsRunning)
        {
            App.AyvaWeb.Stop();
            App.Settings.AyvaWebSocketEnabled = false;
        }
        else
        {
            App.AyvaWeb.Start();
            App.Settings.AyvaWebSocketEnabled = App.AyvaWeb.IsRunning;
        }
        App.Settings.Save();
        RefreshAyvaCard();
    }

    /// <summary>网页入口卡片：服务状态 + 连上的网页数 + 收到的指令数（只在这一格可见时刷新）。</summary>
    private void RefreshAyvaCard()
    {
        if (AyvaCard.Visibility != Visibility.Visible) return;

        bool running = App.AyvaWeb.IsRunning;
        AyvaStatusBar.Background = (System.Windows.Media.Brush)FindResource(running ? "Accent" : "Muted");
        AyvaToggleBtn.Content = running ? "关闭网页入口" : "开启网页入口";
        if (!running)
        {
            AyvaStatusText.Text = App.AyvaWeb.LastError.Length > 0
                ? "没开（上次启动失败：" + App.AyvaWeb.LastError + "）"
                : "没开：网页连不上来";
        }
        else
        {
            AyvaStatusText.Text = App.AyvaWeb.Clients > 0
                ? $"已连上 {App.AyvaWeb.Clients} 个网页 · 正在 {App.AyvaWeb.Url} 收动作"
                : $"在 {App.AyvaWeb.Url} 等着网页连上来";
        }
        // RuntimeNote 是"网页命令被让位/客户端断开"这类运行期说明，以前只写日志、界面看不到
        // （用户看到的是"已连上 N 个网页"但设备不动，毫无解释）。
        string ayvaNote = App.AyvaWeb.RuntimeNote ?? "";
        string ayvaFrames = App.AyvaWeb.Frames == 0
            ? "还没收到指令"
            : $"已收到 {App.AyvaWeb.Frames} 条指令";
        AyvaCountText.Text = (ayvaNote.Length > 0 ? ayvaNote + " · " : "") + ayvaFrames;
        AyvaLastCmdText.Text = App.AyvaWeb.LastCommand.Length == 0 ? "（还没有）" : App.AyvaWeb.LastCommand;
        AyvaHintText.Text = $"网页里填：主机 localhost、端口 {App.AyvaWeb.ActivePort}"
            + (App.AyvaWeb.ActivePort == 80 ? "（Ayva 的默认值就是这个，不用改）" : "（80 没抢到才用这个，要手改一下）")
            + "。主机名必须是 localhost —— 填 IP 它会走加密连接，Hexa 没有证书，连不上。";
    }

    private bool _fanoutLoading;
    /// <summary>刷新「节拍」那一行：BPM / 信心 / 下一拍倒计时。没锁上时如实说没锁上，不报假数字。</summary>
    private void RefreshAudioTempoLabel()
    {
        if (AudioTempoLabel is null) return;

        TempoTracker? tempo = App.AudioReactive?.Tempo;
        if (tempo is null)
        {
            AudioTempoLabel.Text = "节拍：没在听（要先开声音响应）";
            return;
        }
        if (!tempo.Locked)
        {
            AudioTempoLabel.Text = "节拍：没锁上（鼓点还不够清楚，或者还没开始播）";
            return;
        }

        AudioTempoLabel.Text =
            $"节拍：{tempo.Bpm:0} BPM · 信心 {tempo.Confidence:P0} · 下一拍还有 {tempo.SecondsToNextBeat:0.00} 秒"
            + (App.Settings.AudioEventMotion ? "（已按拍点提前下发）" : "（要开「事件化动作」才会按拍点下发）");
    }

    /// <summary>多输出开关：开着才转发，关掉立刻停。</summary>
    private void FanoutToggle_Click(object sender, RoutedEventArgs e)
    {
        if (App.Fanout.Enabled)
        {
            App.Fanout.Enabled = false;
        }
        else
        {
            App.Fanout.Configure(FanoutTargetsBox.Text);
            App.Fanout.Enabled = true;
        }
        App.Settings.FanoutEnabled = App.Fanout.Enabled;
        App.Settings.Save();
        RefreshFanoutCard();
    }

    /// <summary>目标列表改了：存下来；开着的时候立刻重连到新目标。</summary>
    private void FanoutTargets_Changed(object sender, TextChangedEventArgs e)
    {
        if (_fanoutLoading) return;
        App.Settings.FanoutTargets = FanoutTargetsBox.Text;
        if (App.Fanout.Enabled) App.Fanout.Configure(FanoutTargetsBox.Text);
        App.Settings.Save();
    }

    /// <summary>多输出卡片：状态灯 + 开关文案 + 各目标连上没有 + 真外发了多少条。</summary>
    private void RefreshFanoutCard()
    {
        if (FanoutCard.Visibility != Visibility.Visible) return;

        bool on = App.Fanout.Enabled;
        FanoutToggleBtn.Content = on ? "关闭多输出" : "开启多输出";

        var statuses = App.Fanout.TargetStatuses;
        string statusText = statuses.Count == 0 ? "（还没配置目标）" : string.Join("\n", statuses);
        var badLines = App.Fanout.ConfigurationWarnings;
        if (badLines.Count > 0)
            statusText += "\n看不懂、已跳过的目标行：" + string.Join(" ｜ ", badLines)
                + "\n（要写成 udp://主机:端口 或 ws://主机:端口/路径）";
        FanoutStatusText.Text = statusText;

        long sent = Interlocked.Read(ref App.Fanout.Sent);
        FanoutHintText.Text = on
            ? $"已外发 {sent} 条指令（只有在至少一个目标真的收到时才计数）。编辑上面的目标会立刻重连。"
            : $"现在没开。累计外发过 {sent} 条。";
    }

    /// <summary>前台进程名 1 秒缓存：150ms 轮询不再每次都 Process.GetProcessById。</summary>
    private string ReadForegroundProcessNameCached()
    {
        long now = Environment.TickCount64;
        if (!_foregroundCacheValid || now - _foregroundCacheAt >= 1000)
        {
            _foregroundCache = _rules.ReadForegroundProcessName();
            _foregroundCacheAt = now;
            _foregroundCacheValid = true;
        }
        return _foregroundCache;
    }

    private void RefreshStatus()
    {
        double energy = _audio.Energy;
        bool capturing = _audio.Capturing;
        PushLevelHistory(capturing ? energy : 0);
        UpdateLevelHistoryLine();
        AudioLevelLabel.Text = capturing
            ? $"现在 {energy:P0} · 最近 15 秒最高 {PeakLevel:P0} · 低频 {_audio.Bass:F2}"
            : "还没在听（下面一行会说为什么）";
        if (_levelFill != null && LevelTrack.ActualWidth > 0)
            _levelFill.Width = Math.Clamp(energy, 0, 1) * LevelTrack.ActualWidth;
        RefreshAudioState();
        RefreshAudioEventLabel();
        RefreshAudioTempoLabel();
        RefreshSimulatorCard();
        RefreshAyvaCard();
        RefreshFanoutCard();

        string foreground = ReadForegroundProcessNameCached();
        RefreshCompanionBoard(foreground);

        UpdateAudioToggle();
        UpdateRuleToggle();
        RefreshScreenWatch();
    }

    // ══ 画面信号（观察模式）═══════════════════════════════════════════
    // 这一块只跟 ScreenWatchService 打交道：读它算出来的几个数，画成条和数字。
    // 这里没有任何设备调用 —— 设备怎么动只由上面几张卡（声音响应 / 游戏伴随 / 游戏桥）决定。

    /// <summary>把卡片里的实时条先建好（XAML 只放轨道，填充条在代码里造 —— 和上面电平条同一个做法）。</summary>
    private void BuildScreenBars()
    {
        _screenSkinFill = MakeBarFill(ScreenSkinTrack, 10, (Brush)FindResource("Primary"));
        _screenSkinCenterFill = MakeBarFill(ScreenSkinCenterTrack, 10, (Brush)FindResource("Primary"));
        _screenMotionFill = MakeBarFill(ScreenMotionTrack, 10, (Brush)FindResource("Accent"));
        _screenRhythmFill = MakeBarFill(ScreenRhythmTrack, 10, (Brush)FindResource("Accent"));
        _screenScoreFill = MakeScoreFill(ScreenScoreTrack);
        // 模型分那条比其它条粗（14px）：它是主判据，得比手工特征那几条显眼
        _screenModelScoreFill = MakeScoreFill(ScreenModelScoreTrack, 14);
        _screenModelProgressFill = MakeBarFill(ScreenModelProgressTrack, 6, (Brush)FindResource("Primary"));
        _screenPoseProgressFill = MakeBarFill(ScreenPoseProgressTrack, 6, (Brush)FindResource("Primary"));
    }

    private static Border MakeBarFill(Border track, double height, Brush brush)
    {
        var fill = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = brush,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        track.Child = fill;
        return fill;
    }

    /// <summary>场景强度/模型分那条用「绿 → 黄 → 红」：越靠右越可疑，一眼看得出判定区在哪。</summary>
    private Border MakeScoreFill(Border track, double height = 16)
    {
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        gradient.GradientStops.Add(new GradientStop(ThemeColor("Success"), 0.0));
        gradient.GradientStops.Add(new GradientStop(ThemeColor("Warning"), 0.55));
        gradient.GradientStops.Add(new GradientStop(ThemeColor("Danger"), 1.0));
        return MakeBarFill(track, height, gradient);
    }

    /// <summary>取主题色（资源键写错时退回灰色，绝不因为一个颜色把整页搞崩）。</summary>
    private Color ThemeColor(string key) =>
        FindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Gray;

    private void LoadScreenWatchUi()
    {
        _loadingScreenUi = true;
        try
        {
            ScreenSensitivitySlider.Value = Math.Clamp(App.Settings.ScreenWatchSensitivity,
                ScreenWatchService.SensitivityMin, ScreenWatchService.SensitivityMax);
            ScreenFollowLockSlider.Value = Math.Clamp(App.Settings.ScreenPhaseLock, 0, 1);
            SelectModelTier(App.Settings.NsfwModelTier);   // 档位下拉选回上次那一档
        }
        finally { _loadingScreenUi = false; }
        ScreenSensitivityLabel.Text = $"{ScreenSensitivitySlider.Value:0.0}×";
        ScreenFollowLockLabel.Text = $"{ScreenFollowLockSlider.Value:0.00}";
        ReloadScreenWindows();
        UpdateScreenToggle();
        UpdateModelButtons();     // 模型按钮的初始状态（没装模型时「删除模型」是灰的）
        UpdatePoseButtons();
    }

    /// <summary>重新列一遍窗口，并尽量选回上次那个（标题或进程名对得上就行，都没对上就挑第一个没最小化的）。</summary>
    private void ReloadScreenWindows()
    {
        _loadingScreenUi = true;
        try
        {
            ScreenWatch.Refresh();
            ScreenWindowCombo.Items.Clear();
            IReadOnlyList<ScreenWatchWindow> windows = ScreenWatch.Windows;
            foreach (ScreenWatchWindow window in windows) ScreenWindowCombo.Items.Add(window);

            string want = (App.Settings.ScreenWatchTarget ?? "").Trim();
            ScreenWatchWindow? pick = null;
            foreach (ScreenWatchWindow window in windows)
            {
                if (want.Length == 0) break;
                if (string.Equals(window.Title, want, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(window.ProcessName, want, StringComparison.OrdinalIgnoreCase))
                {
                    pick = window;
                    break;
                }
            }
            pick ??= windows.FirstOrDefault(window => !window.Minimized) ?? windows.FirstOrDefault();
            if (pick is not null) ScreenWindowCombo.SelectedItem = pick;

            ScreenWindowHint.Text = windows.Count == 0
                ? "现在桌面上没有能看的窗口：把要看的程序打开（别最小化），再点「↻ 刷新」。"
                : "列表里没有你要的窗口？先把它从最小化还原、别用独占全屏，再点「↻ 刷新」。";
        }
        finally { _loadingScreenUi = false; }
    }

    /// <summary>把下拉里选中的窗口写进设置（观察开着时立刻换过去，不用关掉重开）。</summary>
    private void ApplyScreenSelection()
    {
        if (ScreenWindowCombo.SelectedItem is not ScreenWatchWindow picked) return;
        string target = picked.Title.Length > 0 ? picked.Title : picked.ProcessName;
        if (string.Equals(App.Settings.ScreenWatchTarget, target, StringComparison.Ordinal)) return;
        App.Settings.ScreenWatchTarget = target;
        ScheduleSave();
        if (ScreenWatch.IsRunning) ScreenWatch.Refresh();
    }

    private void ScreenWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingScreenUi) return;
        ApplyScreenSelection();
        RefreshScreenWatch();
    }

    private void ScreenWindowRefresh_Click(object sender, RoutedEventArgs e)
    {
        ReloadScreenWindows();
        ApplyScreenSelection();
        RefreshScreenWatch();
    }

    /// <summary>「开启观察 / 关闭观察」——只控制看不看画面，不碰设备（这里没有任何设备调用）。</summary>
    private void ScreenWatchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenWatch.IsRunning)
        {
            ScreenWatch.Stop();
            App.Settings.ScreenWatchEnabled = false;
            App.Settings.Save();
            UpdateScreenToggle();
            RefreshScreenWatch();
            return;
        }

        ApplyScreenSelection();
        if (ScreenWindowCombo.SelectedItem is not ScreenWatchWindow)
        {
            ScreenStatusHint.Text = "先在上面挑一个要看的窗口（列表是空的就先点「↻ 刷新」）。";
            return;
        }
        App.Settings.ScreenWatchEnabled = true;
        App.Settings.Save();
        ScreenWatch.Refresh();     // 从选完到点开启可能过了一会儿：开之前再把窗口对一遍
        ScreenWatch.Start();
        UpdateScreenToggle();
        RefreshScreenWatch();
    }

    private void ScreenSensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析阶段（Minimum 生效）会先触发一次 ValueChanged，此时标签还没建好，
        // 也不能把那次的值当成用户设置写回磁盘（和上面行程滑块同一个坑）。
        if (ScreenSensitivityLabel is null) return;
        ScreenSensitivityLabel.Text = $"{e.NewValue:0.0}×";
        if (_loadingScreenUi) return;
        App.Settings.ScreenWatchSensitivity = Math.Clamp(e.NewValue,
            ScreenWatchService.SensitivityMin, ScreenWatchService.SensitivityMax);
        ScheduleSave();          // 拖动时只更新数字，停手 350ms 才落盘
        RefreshScreenWatch();    // 判定线立刻跟着动
    }

    /// <summary>
    /// 「相位锁定」滑块（0–1）：设备动到最深的那一下，往画面里动得最猛的那一刻上贴多紧。
    /// 0 = 只跟速度、不管相位对不对得上；越大贴得越紧（服务那边把它换算成 0.08+0.17×值 的修正增益）。
    /// </summary>
    private void ScreenFollowLock_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScreenFollowLockLabel is null) return;
        ScreenFollowLockLabel.Text = $"{e.NewValue:0.00}";
        if (_loadingScreenUi) return;
        App.Settings.ScreenPhaseLock = Math.Clamp(e.NewValue, 0, 1);
        ScheduleSave();
        RefreshScreenWatch();
    }

    private void UpdateScreenToggle()
    {
        bool running = ScreenWatch.IsRunning;
        if (running == _lastScreenToggleState) return;
        _lastScreenToggleState = running;
        ScreenWatchToggleBtn.Content = running ? "关闭观察" : "开启观察";
        ScreenWatchToggleBtn.Style = (Style)FindResource(running ? "BtnDanger" : "BtnSecondary");
    }

    /// <summary>刷新画面卡：状态行 + 六个数值 + 模型状态 + 判定灯（150ms 一次，和页面其它卡片同一个节拍）。</summary>
    private void RefreshScreenWatch()
    {
        // 页面构造/ XAML 解析中途也可能被调到，控件还没齐就直接跳过
        if (ScreenStatusText is null || ScreenScoreLabel is null) return;

        UpdateScreenToggle();   // 按钮文案跟着真实状态走（比如回到页面时自动续上观察）
        bool running = ScreenWatch.IsRunning;
        if (!running)
        {
            SetScreenStatus("未开启", "Muted",
                "开启后先只算数字（不动设备）；要让它驱动设备，还得在「游戏伴随」里选「画面内容」。");
        }
        else if (ScreenWatch.CaptureFailed)
        {
            SetScreenStatus($"抓不到画面：{ScreenWatch.FailureReason}", "Warning",
                "观察还在跑，每秒会自己重试十几次；把窗口还原到桌面上、或改成无边框窗口就会恢复。");
        }
        else
        {
            string target = ScreenWatch.TargetDisplay;
            string frames = ScreenWatch.Fps > 0 ? $"每秒 {ScreenWatch.Fps} 帧" : "正在抓第一帧…";
            SetScreenStatus(target.Length == 0 ? $"正在看：— · {frames}" : $"正在看：{target} · {frames}",
                "Success", "下面就是「它此刻看到了什么」；会不会驱动设备看最下面那块「⚙ 跟着画面里的动作走」。");
        }

        // 运动主体（姿态定位）与姿态模型：定位到了就只在主体那一块算运动量
        if (ScreenPoseSubjectText is not null)
        {
            ScreenPoseSubjectText.Text = $"运动主体：{(running ? ScreenWatch.PoseSubjectStatus : "未定位（用整幅画面）· 还没开启")}";
            ScreenPoseSubjectText.Foreground = (Brush)FindResource(
                running && ScreenWatch.PoseLocated ? "Success" : "Muted");
        }
        if (ScreenPoseModelText is not null)
        {
            bool poseReady = running && ScreenWatch.PoseEnabled && ScreenWatch.PoseModelInstalled && !ScreenWatch.PoseModelFailed;
            ScreenPoseModelText.Text = $"姿态模型：{ScreenWatch.PoseModelStatus}";
            ScreenPoseModelText.Foreground = (Brush)FindResource(
                ScreenWatch.PoseModelFailed ? "Warning" : poseReady ? "Success" : "Muted");
        }

        // 模型分（主判据）：没就绪时显示「—」而不是 0%，免得让人以为是"模型说 0%"。
        bool modelReady = running && ScreenWatch.ModelReady;
        SetBar(_screenModelScoreFill, ScreenModelScoreTrack, modelReady ? ScreenWatch.ModelScore : 0);
        ScreenModelScoreLabel.Text = modelReady ? Percent(ScreenWatch.ModelScore) : "—";
        ScreenFeatureScoreText.Text = modelReady
            ? $"特征分（手工算法，做对照）：{Percent(ScreenWatch.FeatureScore)}"
            : $"特征分（手工算法）：{Percent(ScreenWatch.FeatureScore)} —— 模型没就绪，现在整个判定都靠它";

        SetBar(_screenSkinFill, ScreenSkinTrack, ScreenWatch.SkinRatio);
        ScreenSkinLabel.Text = Percent(ScreenWatch.SkinRatio);
        SetBar(_screenSkinCenterFill, ScreenSkinCenterTrack, ScreenWatch.SkinCenterRatio);
        ScreenSkinCenterLabel.Text = Percent(ScreenWatch.SkinCenterRatio);
        SetBar(_screenMotionFill, ScreenMotionTrack, ScreenWatch.Motion);
        ScreenMotionLabel.Text = Percent(ScreenWatch.Motion);
        SetBar(_screenRhythmFill, ScreenRhythmTrack, ScreenWatch.Rhythm);
        ScreenRhythmLabel.Text = Percent(ScreenWatch.Rhythm);

        bool voice = running && ScreenWatch.VoiceEvent;
        ScreenVoiceText.Text = voice ? "有（最近 1 秒内听到人声 / 呻吟）" : "没有";
        ScreenVoiceText.Foreground = (Brush)FindResource(voice ? "Accent" : "Muted");

        bool suspect = running && !ScreenWatch.CaptureFailed && ScreenWatch.Suspect;
        SetBar(_screenScoreFill, ScreenScoreTrack, ScreenWatch.SceneScore);
        ScreenScoreLabel.Text = Percent(ScreenWatch.SceneScore);
        ScreenVerdictLamp.Background = (Brush)FindResource(suspect ? "Danger" : "Success");
        ScreenVerdictText.Text = suspect ? "疑似情色场景" : "正常";
        ScreenVerdictText.Foreground = (Brush)FindResource(suspect ? "Danger" : "Muted");
        ScreenThresholdHint.Text = $"判定线：{Percent(ScreenWatch.ThresholdOn)} 以上算「疑似」，"
            + $"掉到 {Percent(ScreenWatch.ThresholdOff)} 以下才收回（免得在线上来回跳）。";

        RefreshFollowRow();
        RefreshModelRow();
    }

    /// <summary>
    /// 「⚙ 跟着画面里的动作走」那一小节：现在到底在不在驱动设备、为什么不动、用什么深度和速度在动。
    /// 这里的每一句话都直接来自服务（<see cref="ScreenWatchService.FollowReason"/>），界面不自己编原因 ——
    /// 用户选了「画面内容」却没反应时，最需要的就是一句能照着修的原因。
    /// </summary>
    private void RefreshFollowRow()
    {
        if (ScreenFollowStatusText is null) return;

        string note;
        if (CompanionSource == "screen")
        {
            note = "现在的来源是「画面内容」：跟着画面里那个节奏走；画面里没事发生时就缓降回中、停着不动。"
                + "要让这条信号生效，上面的「开启观察」必须是开着的，而且绑定的程序得在前台。";
        }
        else if (CompanionSource == "auto")
        {
            note = "现在的来源是「自动」：有游戏信号就跟游戏，没有就用画面内容（画面里没事发生时才退回声音兜底）。";
        }
        else
        {
            note = "现在这条信号不会驱动设备（来源不是「画面内容」）。想用它驱动设备，"
                + "到「游戏伴随」里把「动作来源」选成「画面内容 · 跟着画面里的动作走」。";
        }
        if (ScreenFollowSourceNote.Text != note) ScreenFollowSourceNote.Text = note;

        bool active = ScreenWatch.FollowActive;
        bool easing = !active && ScreenWatch.FollowWantsDevice;
        ScreenFollowStateLabel.Text = active ? "正在跟画面动" : easing ? "正在缓降回中" : "没在动";
        ScreenFollowStateLabel.Foreground = (Brush)FindResource(
            active ? "Danger" : easing ? "Warning" : "Muted");

        string reason = ScreenWatch.FollowReason;
        if (reason.Length == 0) reason = "—";
        if (active)
            reason = $"{reason}（深度 = 场景强度过线之后归一化的值；速度 = 测到的节奏频率，已夹进舒适档允许的范围）";
        if (ScreenFollowStatusText.Text != reason) ScreenFollowStatusText.Text = reason;
    }

    /// <summary>识别模型那一小节：状态 + 进度 + 按钮能不能点（跟着真实状态走，不靠"点了才更新"）。</summary>
    private void RefreshModelRow()
    {
        if (ScreenModelStatusText is null) return;

        ScreenModelStatusText.Text = ScreenWatch.ModelStatus;
        ScreenModelStatusText.Foreground = (Brush)FindResource(
            _modelDownloading ? "Primary"
            : ScreenWatch.ModelReady ? "Success"
            : ScreenWatch.ModelFailed ? "Warning"
            : "Muted");

        string tierHint = ScreenWatch.ModelTierHint;
        if (ScreenModelTierHint.Text != tierHint) ScreenModelTierHint.Text = tierHint;
        ScreenModelTierCombo.IsEnabled = !_modelDownloading;   // 下载中不许换档（换的是另一个文件）

        // 模型说"这一帧是血腥暴力不是情色"时，如实说一句（用户才不会以为模型瞎了）
        ScreenModelViolentText.Visibility = ScreenWatch.ModelViolent ? Visibility.Visible : Visibility.Collapsed;

        ScreenModelProgressTrack.Visibility = _modelDownloading ? Visibility.Visible : Visibility.Collapsed;
        if (_modelDownloading) SetBar(_screenModelProgressFill, ScreenModelProgressTrack, _modelDownloadProgress);

        UpdateModelButtons();
        UpdatePoseRow();
    }

    /// <summary>
    /// 姿态模型那一小节：状态 + 进度 + 按钮可用性。和识别模型同一套做法 ——
    /// 状态每一帧都从服务现读，不靠「点了才更新」；下载中把按钮置灰、进度条露出来。
    /// </summary>
    private void UpdatePoseRow()
    {
        if (ScreenPoseStatusText is null) return;

        ScreenPoseStatusText.Text = ScreenWatch.PoseModelStatus;
        ScreenPoseStatusText.Foreground = (Brush)FindResource(
            _poseDownloading ? "Primary"
            : ScreenWatch.PoseModelFailed ? "Warning"
            : ScreenWatch.PoseModelInstalled ? "Success"
            : "Muted");

        ScreenPoseProgressTrack.Visibility = _poseDownloading ? Visibility.Visible : Visibility.Collapsed;
        if (_poseDownloading) SetBar(_screenPoseProgressFill, ScreenPoseProgressTrack, _poseDownloadProgress);

        UpdatePoseButtons();
    }

    private void UpdatePoseButtons()
    {
        if (ScreenPoseDownloadBtn is null) return;
        bool installed = ScreenWatch.PoseModelInstalled;
        ScreenPoseDownloadBtn.IsEnabled = !_poseDownloading && !installed;
        ScreenPoseDownloadBtn.Content = _poseDownloading ? "下载中…" : installed ? "已下载" : "下载姿态模型";
        ScreenPoseDeleteBtn.IsEnabled = !_poseDownloading && ScreenWatch.PoseModelBytes > 0;
    }

    /// <summary>
    /// 「下载姿态模型」：先弹确认框（写清体积、从哪下、只在本机推理），用户点「是」才真的下。
    /// 和识别模型一样，这是这个功能唯一的联网入口，不点就一个字节都不会下。
    /// </summary>
    private async void ScreenPoseDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_poseDownloading) return;
        if (ScreenWatch.PoseModelInstalled)
        {
            UpdatePoseRow();
            return;
        }

        MessageBoxResult confirm = System.Windows.MessageBox.Show(
            $"下载姿态模型（约 13MB）？\n\n"
            + "· 从国内镜像下载（huggingface 直连不通，所以走镜像）\n"
            + "· 只在本机推理，画面不外传、不上传、不写盘\n"
            + "· 用途：把「人在画面哪一块」找出来，运动量只在这一块里算 —— 镜头晃动不容易把数字顶上去\n"
            + "· 存在你本机的数据目录里，随时可以点「删除」删掉\n\n"
            + "这个过程大概十几秒（看网速），期间可以照常用软件。",
            "下载姿态模型", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _poseDownloading = true;
        _poseDownloadProgress = 0;
        UpdatePoseRow();

        var cts = new CancellationTokenSource();
        bool ok = false;
        try
        {
            // Progress<T> 会把回调转回界面线程，所以这里直接改字段是安全的。
            var progress = new Progress<double>(value => _poseDownloadProgress = Math.Clamp(value, 0, 1));
            ok = await ScreenWatch.DownloadPoseModelAsync(progress, cts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面观察：下载姿态模型时出错 —— {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            _poseDownloading = false;
            _poseDownloadProgress = ok ? 1 : 0;
        }

        UpdatePoseRow();
        RefreshScreenWatch();
    }

    /// <summary>「删除」：腾空间用。删掉后运动量自动退回整幅画面，其它功能不受影响。</summary>
    private void ScreenPoseDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_poseDownloading) return;
        if (ScreenWatch.PoseModelBytes <= 0)
        {
            UpdatePoseRow();
            return;
        }

        MessageBoxResult confirm = System.Windows.MessageBox.Show(
            $"删掉本机的姿态模型（{ScreenWatch.PoseModelBytes / 1048576.0:0.#}MB）？\n\n"
            + "· 删掉后运动量与节奏退回「按整幅画面算」（就是没装它之前的行为）\n"
            + "· 想再用，点「下载姿态模型」重新下即可\n"
            + "· 删除只影响模型文件，不动任何设置",
            "删除姿态模型", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        ScreenWatch.DeletePoseModel();
        UpdatePoseRow();
        RefreshScreenWatch();
    }

    private void UpdateModelButtons()
    {
        if (ScreenModelDownloadBtn is null) return;
        bool installed = ScreenWatch.ModelInstalled;
        ScreenModelDownloadBtn.IsEnabled = !_modelDownloading && !installed;
        ScreenModelDownloadBtn.Content = _modelDownloading ? "下载中…" : installed ? "已下载" : "下载模型";
        ScreenModelDeleteBtn.IsEnabled = !_modelDownloading && ScreenWatch.ModelDiskBytes > 0;
        // 提示文字只在真的变了时才改（这个方法 150ms 跑一次，别每次都造一个新字符串塞给控件）
        string tip = ScreenModelDeleteBtn.IsEnabled
            ? $"把本机下载过的所有档位模型都删掉（一共 {ScreenWatch.ModelDiskBytes / 1048576.0:0.#}MB）。"
              + "删掉后判定退回手工特征算法，随时可以再下。"
            : "本机还没有下载过任何档位的模型。";
        if (!string.Equals(ScreenModelDeleteBtn.ToolTip as string, tip, StringComparison.Ordinal))
            ScreenModelDeleteBtn.ToolTip = tip;
    }

    /// <summary>档位下拉：把设置里存的那一档选上（认不出来就选中档）。</summary>
    private void SelectModelTier(string tier)
    {
        foreach (object item in ScreenModelTierCombo.Items)
        {
            if (item is ComboBoxItem choice && string.Equals(choice.Tag as string, tier, StringComparison.Ordinal))
            {
                ScreenModelTierCombo.SelectedItem = choice;
                return;
            }
        }
        if (ScreenModelTierCombo.Items.Count > 1) ScreenModelTierCombo.SelectedIndex = 1;   // 中档
    }

    /// <summary>
    /// 换档位：三档是三个独立文件，所以换档 = 换一个要找的文件。本机没有这一档时，
    /// 状态行会变成「没下载（…）」并把「下载模型」重新点亮 —— 这里顺便把话说清楚。
    /// </summary>
    private void ScreenModelTier_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingScreenUi) return;
        if (ScreenModelTierCombo.SelectedItem is not ComboBoxItem choice) return;
        string tier = choice.Tag as string ?? "m";
        if (string.Equals(App.Settings.NsfwModelTier, tier, StringComparison.Ordinal))
        {
            RefreshModelRow();
            return;
        }

        App.Settings.NsfwModelTier = tier;
        App.Settings.Save();
        ScreenWatch.SetModelTier(tier);      // 服务转发给分类器：旧档会话会被释放，模型分归零

        ScreenModelHint.Text = ScreenWatch.ModelInstalled
            ? $"已换成{ScreenWatch.ModelTierName}：本机已经有这一档的模型，开启观察就直接用它。"
            : $"已换成{ScreenWatch.ModelTierName}：本机还没有这一档的模型文件，点「下载模型」下这一份"
              + $"（{ScreenWatch.ModelSizeText}）。原来那一档的文件还留在磁盘上，"
              + "可以在「删除模型」里一起清掉。";
        RefreshModelRow();
        RefreshScreenWatch();
    }

    /// <summary>
    /// 「下载模型」：<b>先弹确认框</b>（写清档位体积、从哪下、只在本机推理），用户点「是」才真的下 ——
    /// 这是这个功能唯一的联网入口，不点就一个字节都不会下。
    /// </summary>
    private async void ScreenModelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_modelDownloading) return;
        if (ScreenWatch.ModelInstalled)
        {
            ScreenModelHint.Text = "本机已经有模型了，不用重复下载（想换一份可以先「删除模型」）。";
            RefreshScreenWatch();
            return;
        }

        MessageBoxResult confirm = System.Windows.MessageBox.Show(
            $"下载本地识别模型（{ScreenWatch.ModelTierName} {ScreenWatch.ModelSizeText}）？\n\n"
            + $"· 大小 {ScreenWatch.ModelSizeText}，从国内镜像下载（huggingface 直连不通，所以走镜像）\n"
            + "· 只在本机推理，画面不外传、不上传、不写盘\n"
            + "· 存在你本机的数据目录里，随时可以点「删除模型」删掉\n"
            + "· 想更快选「小」、想更准选「大」，在下面的档位里换\n\n"
            + "这个过程大概几十秒（看网速），期间可以照常用软件。",
            "下载识别模型", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _modelDownloading = true;
        _modelDownloadProgress = 0;
        ScreenModelHint.Text = "正在下载（走第一条线路，失败会自动换第二条）。";
        RefreshModelRow();

        var cts = new CancellationTokenSource();
        bool ok = false;
        try
        {
            // Progress<T> 会把回调转回界面线程，所以这里直接改字段是安全的。
            var progress = new Progress<double>(value => _modelDownloadProgress = Math.Clamp(value, 0, 1));
            ok = await ScreenWatch.DownloadModelAsync(progress, cts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面观察：下载识别模型时出错 —— {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            _modelDownloading = false;
            _modelDownloadProgress = ok ? 1 : 0;
        }

        RefreshModelRow();
        RefreshScreenWatch();

        if (ok)
        {
            ScreenModelHint.Text = $"「{ScreenWatch.ModelTierName}」模型已就绪：下面「模型分（内容判定）」会开始跟着画面走，"
                + "它现在是主判据（模型分 70% + 手工特征 30%）。开启观察后约 1 秒内就能看到数。";
        }
        else
        {
            ScreenModelHint.Text = $"没下成：{ScreenWatch.ModelStatus}。"
                + "已经下到一半的文件会自动清掉（不会留下坏模型），检查网络后可以再点一次「下载模型」，"
                + "或者先换成更小的档位试试。";
        }
    }

    /// <summary>「删除模型」：腾空间用。会把本机下载过的所有档位都删掉，之后判定退回手工特征。</summary>
    private void ScreenModelDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_modelDownloading) return;
        if (ScreenWatch.ModelDiskBytes <= 0)
        {
            ScreenModelHint.Text = "本机还没有下载过模型，不用删。";
            RefreshScreenWatch();
            return;
        }

        MessageBoxResult confirm = System.Windows.MessageBox.Show(
            $"删掉本机下载过的识别模型（一共 {ScreenWatch.ModelDiskBytes / 1048576.0:0.#}MB）？\n\n"
            + "· 三个档位的模型文件都会删掉（包括你现在没在用的那几档）\n"
            + "· 删掉后判定退回手工特征算法（模型分显示「—」）\n"
            + "· 想再用，点「下载模型」重新下当前档位即可\n"
            + "· 删除只影响模型文件，不动任何设置和脚本",
            "删除识别模型", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        bool ok = ScreenWatch.DeleteModel();
        ScreenModelHint.Text = ok
            ? "模型已删除，腾出了磁盘空间。现在判定完全靠手工特征算法。"
            : $"没删掉：{ScreenWatch.ModelStatus}。可能文件正被杀毒软件或其它程序占用，稍后再试。";
        RefreshModelRow();
        RefreshScreenWatch();
    }

    private void SetScreenStatus(string text, string brushKey, string hint)
    {
        if (ScreenStatusText.Text != text)
        {
            Brush brush = (Brush)FindResource(brushKey);
            ScreenStatusText.Text = text;
            ScreenStatusText.Foreground = brush;
            ScreenStatusBar.Background = brush;
        }
        if (ScreenStatusHint.Text != hint) ScreenStatusHint.Text = hint;
    }

    /// <summary>把一个 0–1 的值画成条宽（按轨道实际宽度算，窄窗口下也不会溢出）。</summary>
    private static void SetBar(Border? fill, Border track, double value)
    {
        if (fill is null || track is null) return;
        double width = track.ActualWidth;
        fill.Width = width <= 1 ? 0 : Math.Clamp(value, 0, 1) * width;
    }

    private static string Percent(double value) => $"{Math.Clamp(value, 0, 1) * 100:0}%";

    /// <summary>
    /// 游戏伴随状态板 —— 三行，每一行回答一个问题：
    /// ① 绑没绑、现在在不在前台；② 动作实际用的是哪一条（遥测 / 声音兜底 / 等待遥测）；
    /// ③ 现在在做什么（动作名 + 强度），不动就直接说为什么。
    /// 缓存的参数只为省一次进程枚举；不传就自己读（1 秒缓存）。
    /// </summary>
    private void RefreshCompanionBoard(string? foreground = null)
    {
        bool companionOn = App.Settings.RuleEngineEnabled;   // 伴随 = 规则引擎那条路
        bool presetOn = App.Settings.CompanionAuto;          // 伴随关着时自动套底色动作
        bool anyOn = companionOn || presetOn;
        string source = CompanionSource;
        string sourceLabel = CompanionSourceLabel(source);
        string target = (App.Settings.CompanionProcess ?? "").Trim();
        string front = string.IsNullOrWhiteSpace(foreground) ? ReadForegroundProcessNameCached() : foreground;
        string engineNote = string.IsNullOrWhiteSpace(_boundEngineDisplay) || _boundEngineDisplay == "未识别"
            ? ""
            : $"（{_boundEngineDisplay}）";
        bool inTarget = target.Length > 0 && string.Equals(front, target, StringComparison.OrdinalIgnoreCase);
        string profileNote = target.Length > 0 && App.Settings.GameProfiles.ContainsKey(target)
            ? $"（已套用「{target}」的专属配置）"
            : "";

        // ① 绑定 / 前台
        string line1;
        string brush1;
        if (target.Length == 0)
        {
            line1 = "还没绑定游戏 —— 先填进程名（或点「3 秒后抓取」），不绑的话设备永远不动";
            brush1 = "Warning";
        }
        else if (!anyOn)
        {
            line1 = $"未开启 · 绑定了 {target}{engineNote}";
            brush1 = "Muted";
        }
        else if (!App.Engine.CanRun)
        {
            line1 = $"绑定了 {target}{engineNote} · 但设备没连上（或还在急停）：先到「设置」页连接，或点左下角解锁";
            brush1 = "Danger";
        }
        else if (inTarget)
        {
            line1 = $"绑定了 {target}{engineNote} · 现在就在前台 ✓";
            brush1 = "Success";
        }
        else
        {
            line1 = $"绑定了 {target}{engineNote} · 但现在前台是 {(front.Length == 0 ? "—" : front)}，所以停着（切回游戏就会继续）";
            brush1 = "Accent";
        }

        // ② 动作实际用的是哪一条
        string line2;
        string brush2;
        if (!anyOn)
        {
            if (_autoFollowStoppedByEmergency)
            {
                line2 = "刚刚被「全部停止」关掉了：切回游戏不会再自动启动，要恢复请重新点「开启游戏伴随」。";
                brush2 = "Warning";
            }
            else
            {
                line2 = $"动作来源：{sourceLabel} —— 开启后才生效";
                brush2 = "Muted";
            }
        }
        else if (!companionOn)
        {
            line2 = "动作来源：底色动作 · 伴随关着，进游戏自动套用这一套";
            brush2 = "Muted";
        }
        else if (!inTarget)
        {
            line2 = $"动作来源：{sourceLabel} —— 目标不在前台，现在没在用（切回游戏才开始）";
            brush2 = "Muted";
        }
        else if (_rules.TelemetryActive)
        {
            string from = string.IsNullOrWhiteSpace(App.Engine.TelemetrySource) ? "" : $"（{App.Engine.TelemetrySource}）";
            line2 = $"动作来源：游戏遥测 · {SyncQualityLabel(_rules.SyncQuality)}{from}";
            brush2 = "Success";
        }
        else if (source == "telemetry")
        {
            line2 = "动作来源：等待遥测 —— 这个游戏还没发数据；「只用游戏信号」不会用声音顶替，所以现在完全不动";
            brush2 = "Warning";
        }
        else if (source == "screen")
        {
            line2 = _rules.YieldedForScreen
                ? "动作来源：画面内容 · 正在跟着画面里的动作走"
                : "动作来源：画面内容 —— 画面里有事发生时才动（要走画面，先去「测试台 · 画面信号」开启观察并挑好窗口）";
            brush2 = _rules.YieldedForScreen ? "Success" : "Muted";
        }
        else if (source == "sound")
        {
            line2 = $"动作来源：声音兜底 · {SyncQualityLabel(_rules.SyncQuality)}（不理会游戏信号）";
            brush2 = "Muted";
        }
        else
        {
            line2 = $"动作来源：声音兜底 · {SyncQualityLabel(_rules.SyncQuality)}（游戏没发数据，改用声音）";
            brush2 = "Muted";
        }

        // ③ 现在在响应什么 / 为什么不动
        // 两条约定：① 不动的时候只给「一个」原因（以前「设备没连上（或还在急停）」把两件事
        // 混在一句话里，用户没法照着修）；② 动的时候要说清「现在在响应什么」—— 这是用户最想知道的一件事：
        // 伴随到底在跟着什么动，是那一下冲击、还是我自己的操作、还是它只是在放底色。
        string line3;
        if (target.Length == 0) line3 = "现在不动：还没绑定游戏（先填进程名，或点「3 秒后抓取」）";
        else if (!anyOn) line3 = "现在不动：伴随没开";
        else if (App.Engine.EmergencyStopped) line3 = "现在不动：急停锁着（点左下角「全部归中」解锁）";
        else if (!App.Engine.CanRun) line3 = "现在不动：设备没连上";
        else if (companionOn && _rules.YieldedForManualPlayback) line3 = "正在响应：刚好在放脚本（伴随让位中）—— 脚本停下就会自己接回去";
        else if (!inTarget) line3 = "现在不动：目标游戏不在前台";
        else if (companionOn && source == "telemetry" && !_rules.TelemetryActive) line3 = "现在不动：在等游戏发数据";
        else if (companionOn && _rules.YieldedForScreen) line3 = "正在响应：画面内容 · 完全跟着画面里的节奏走" + profileNote;
        else if (companionOn && source == "screen") line3 = "现在不动：画面里没事发生（过线才动；切回来它会自己接上）";
        else if (companionOn && !_rules.TargetActive) line3 = "现在不动：这一刻没有该动的事（安静着）";
        else if (companionOn && _rules.TelemetryActive) line3 = $"正在响应：游戏遥测 · 完全跟着游戏画面/事件走{profileNote}";
        else if (companionOn) line3 = $"正在响应：{_rules.ResponseLabel} · {ModeLabel(_rules.TargetMode)} · 强度 {_rules.TargetIntensity:0.0}{profileNote}";
        else line3 = $"正在动：{ModeLabel(App.Engine.AutoPattern)} · 强度 {App.Engine.IntensityScale:0.0}{profileNote}";

        // 150ms 轮询也会走到这里：内容不变时跳过赋值，避免无谓的刷帧。
        if (CompanionStatusText.Text != line1) CompanionStatusText.Text = line1;
        CompanionStatusText.Foreground = (Brush)FindResource(brush1);
        CompanionStatusBar.Background = (Brush)FindResource(brush1);
        if (AutoFollowStatusText.Text != line2) AutoFollowStatusText.Text = line2;
        AutoFollowStatusText.Foreground = (Brush)FindResource(brush2);
        if (SignalText.Text != line3) SignalText.Text = line3;
        // 「正在响应」那套词只在伴随开着时才有意义：关着的时候解释它等于多一段没人看的字。
        Visibility hintVisibility = anyOn ? Visibility.Visible : Visibility.Collapsed;
        if (CompanionResponseHint.Visibility != hintVisibility) CompanionResponseHint.Visibility = hintVisibility;
    }

    private void UpdateAudioToggle()
    {
        bool enabled = App.Settings.AudioReactiveEnabled;
        if (enabled == _lastAudioToggleState) return;
        _lastAudioToggleState = enabled;
        AudioToggleBtn.Content = enabled ? "关闭声音响应" : "开启声音响应";
        AudioToggleBtn.Style = (Style)FindResource(enabled ? "BtnDanger" : "BtnSecondary");
    }

    private void UpdateRuleToggle()
    {
        // 只看「伴随」本身（规则引擎那条路）：CompanionAuto 是下面那个独立勾选，
        // 混进来会让按钮文案跟着它翻，用户就分不清自己在开什么了。
        bool enabled = App.Settings.RuleEngineEnabled;
        if (enabled == _lastRuleToggleState) return;
        _lastRuleToggleState = enabled;
        RuleToggleBtn.Content = enabled ? "关闭游戏伴随" : "开启游戏伴随";
        RuleToggleBtn.Style = (Style)FindResource(enabled ? "BtnDanger" : "BtnSecondary");
    }

    /// <summary>
    /// 动作族 → 白话名。名字要**对得上动作本身**：以前 intense_thrust 显示成「声音增强」、
    /// deep_pulse 落到兜底显示成「自然变化」，用户根本猜不到设备会怎么动。
    /// </summary>
    private static string ModeLabel(string mode) => mode switch
    {
        "gentle_wave" => "温柔波动",
        "organic_flow" => "自然流动",
        "game_flow" => "游戏沉浸",
        "slow_sine" => "缓慢起伏",
        "deep_pulse" => "一下一下（脉冲）",
        "spiral_tease" => "螺旋游走",
        "random_micro" => "细微变化",
        "edge_swirl" => "渐进漩涡",
        "intense_thrust" => "抽插往复",
        "climax_burst" => "递进爆发",
        "free_play" => "自由巡游（自动换动作）",
        "telemetry" => "游戏同步",
        _ => mode,
    };

    /// <summary>
    /// 「动作识别」的白话说法：基础 = 没拿到游戏数据，只能用底色动作；
    /// 等待遥测 = 选了「只用游戏信号」但这一刻还没收到（所以现在不动）。
    /// </summary>
    private static string SyncQualityLabel(string quality) => quality switch
    {
        "精确" => "精确",
        "增强" => "增强",
        "基础" => "基础（没有游戏数据）",
        "等待遥测" => "等待遥测",
        "stopped" or "已停止" => "已停止",
        _ => quality,
    };

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        // 全域急停也要停掉网页入口：否则解锁之后远端网页立刻又能驱动设备，而按钮上写着"这条链路上的一切都停"。
        // 多输出**不关**：它是转发通道，急停时 DSTOP 会照常镜像过去，网络那头也一起停。
        if (App.AyvaWeb.IsRunning)
        {
            App.AyvaWeb.Stop();
            App.Settings.AyvaWebSocketEnabled = false;
            App.Settings.Save();
            RefreshAyvaCard();
        }
        // P0：先关闭自动跟随/操作联动。否则用户解锁后切回目标游戏，ForegroundWatcher.Tick
        // 看到 CompanionAuto=true 会再次 StartAuto，设备重新运动。
        _loadingUi = true;
        try
        {
            ActionLinkCheck.IsChecked = false;
            AutoPresetCheck.IsChecked = false;   // 同上：它也会让设备在进游戏时自己动起来
        }
        finally
        {
            _loadingUi = false;
        }
        App.Settings.CompanionAuto = false;   // 伴随关着时的自动套预设也停
        App.Settings.AmbientOverlay = false;   // 氛围叠加也停（不然"全部停止"之后设备还像在自己动）
        _loadingUi = true;
        try { AmbientOverlayCheck.IsChecked = false; SyncAmbientUi(); }
        finally { _loadingUi = false; }
        SaveAutoFollow();

        App.Settings.RuleEngineEnabled = false;
        App.Settings.AudioReactiveEnabled = false;
        App.Settings.GameBridgeEnabled = false;
        // 画面观察也一起关：它可能正在驱动设备（来源选「画面内容」时），Stop 里会缓降回中并交还控制权；
        // 而且「全部停止」之后还留一个抓画面的线程在跑，用户会以为"不是全停了吗"。
        App.Settings.ScreenWatchEnabled = false;
        ScreenWatch.Stop();
        UpdateScreenToggle();
        _rules.Stop();
        _audio.Refresh();
        App.Bridge.Refresh();
        App.Engine.EmergencyStop();
        App.Settings.Save();
        UpdateAudioToggle();
        UpdateRuleToggle();
        // 游戏桥的按钮文案不在这里改：桥面板在「游玩」页，它自己会在 Loaded / 定时器里跟设置对齐（App.Bridge 是同一个服务实例）。
        RefreshScreenWatch();
        _autoFollowStoppedByEmergency = true;
        RefreshCompanionBoard();

        StopHintText.Text = "已关闭游戏伴随，切回游戏不会再自动启动。解锁后如需继续，请重新点「开启游戏伴随」。";
        StopHintText.Visibility = Visibility.Visible;
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
