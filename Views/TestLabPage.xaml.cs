using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using Rectangle = System.Windows.Shapes.Rectangle;
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

    // ── 画面同步（观察模式 / 画面同步）────────────────────────────────
    // 归属：这条信号是「游戏伴随」的一个动作来源（画面内容），不是声音那一路的附属。
    // 默认只测量：只有用户在伴随里把来源选成「画面内容」、绑定的程序在前台、场景强度过线，
    // 三条同时成立时服务才会下发动作（服务自己管设备，这一页只显示状态 + 改设置）。
    // 服务是**跨页面实例共享**的：自检会把这一页反复实例化，各建一个就会出现"两个页面各抓一份画面"，
    // 而抓取线程只该有一个。懒建（字段初始化时机不受页面构造顺序影响）。
    private static ScreenWatchService? _screenWatchShared;
    /// <summary>
    /// 画面同步的服务实例：<b>全进程只有一个</b>，所以先问服务自己的登记口
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
    private Rectangle? _screenModelScoreThresholdLine;
    private Rectangle? _screenMotionThresholdLine;
    private Rectangle? _screenRhythmThresholdLine;
    private Rectangle? _screenScoreThresholdLine;
    private bool _thresholdDrag;
    private bool _syncingThreshold;
    private bool _loadingScreenUi;
    private bool _lastScreenToggleState;
    // 「更多设置」折叠状态：只记在内存里（用户展开/收起的选择），不写进 AppSettings。
    private bool _screenMoreSettingsOpen;
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
        UpdateManualRoiUi();
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
        AutoFollowScreenCheck.Checked += (_, _) => { if (!_loadingUi) ApplyAutoFollowScreen(true); };
        AutoFollowScreenCheck.Unchecked += (_, _) => { if (!_loadingUi) ApplyAutoFollowScreen(false); };
        ScreenPoseEnableCheck.Checked += (_, _) =>
        {
            if (_loadingUi) return;
            App.Settings.PoseVisionEnabled = true;
            App.Settings.Save();
            UpdatePoseRow();
        };
        ScreenPoseEnableCheck.Unchecked += (_, _) =>
        {
            if (_loadingUi) return;
            App.Settings.PoseVisionEnabled = false;
            App.Settings.Save();
            UpdatePoseRow();
        };
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

        // 画面同步的开关是记住的：上次开着，回到这一页就继续（它平时只读画面不动设备，
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

        // 角色声音模式（或自动模式此刻听成呻吟）不看「事件化动作」那个勾选（人声是连续抽插），所以先说清这一路正在干什么。
        if (IsVoiceTargetSelected || (IsAutoTargetSelected && _audio.SceneKind == AudioSceneKind.Voice))
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

        // 自动模式下"算不算人声"要看它此刻听成什么，而不是看下拉选了什么。
        bool voiceNow = IsVoiceTargetSelected || (IsAutoTargetSelected && _audio.SceneKind == AudioSceneKind.Voice);

        if (_audio.MotionActive)
        {
            AudioStateLabel.Foreground = (Brush)FindResource("Success");
            AudioStateLabel.Text = voiceNow
                ? $"正在抽插（人声强度 {_audio.Envelope:P0}）：越响插得越深、越快跟得越紧；人声没了还会再抽 1.5 秒、一下比一下浅。"
                : IsAutoTargetSelected
                    ? $"自动模式正在跟（听成「{_audio.SceneLabel}」，强度 {_audio.Envelope:P0}）：场景一变跟法就自己换。"
                    : $"正在跟着声音动（当前强度 {_audio.Envelope:P0}）：声音变小动作就变浅，安静 0.7 秒后慢慢回中停下。";
            return;
        }

        // 听得到、但没动 —— 这正是「对呻吟没反应」时用户看到的样子，所以直接告诉他下一步点哪里。
        if (_audio.Energy > 0.05)
        {
            AudioStateLabel.Foreground = (Brush)FindResource("Warning");
            AudioStateLabel.Text = voiceNow
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
        if (!_audioRangeLoaded && AudioRange != null)
        {
            // 声音响应用项目统一的双滑块限定主轴行程：左=最低位置、右=最高位置（0–100 的百分比）。
            double minPos = Math.Clamp(App.Settings.AudioMinPosition, 0, 95);
            double maxPos = Math.Clamp(App.Settings.AudioMaxPosition, Math.Min(100, minPos + AudioRangeGap), 100);
            AudioRange.LowerValue = minPos;
            AudioRange.UpperValue = maxPos;
            AudioRangeLabel.Text = $"{minPos:0}% – {maxPos:0}%";
            AudioGateSlider.Value = Math.Clamp(App.Settings.AudioNoiseGate, 0, 0.4);
            AudioGateLabel.Text = $"{AudioGateSlider.Value * 100:0}%";
            // 深度不再单独调：这一条两端就是最浅/最深，深度倍率固定回自动基准（旧版存的值不再当第二层）
            if (App.Settings.AudioDepth != 50) { App.Settings.AudioDepth = 50; ScheduleSave(); }
            // 「自定义节奏」整块去掉了（用户 2026-11-24：「我觉得没意义」）：
            // 节奏永远由声音自己算；把旧设置里残留的「自定义」标记熄掉，
            // 否则界面上看不见的倍率还在悄悄生效。
            if (App.Settings.AudioCustom) { App.Settings.AudioCustom = false; ScheduleSave(); }
            _audioRangePrevLower = minPos;
            _audioRangePrevUpper = maxPos;
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
        // voice → 角色声音；auto → 自动；energy/bass/beat → 音乐律动（并把律动方式摆上）。
        string audioMode = App.Settings.AudioResponseMode;
        bool voiceMode = string.Equals(audioMode, "voice", StringComparison.OrdinalIgnoreCase);
        bool autoMode = string.Equals(audioMode, "auto", StringComparison.OrdinalIgnoreCase);
        if (!voiceMode && !autoMode) _lastMusicMode = audioMode;
        SelectByTag(AudioModeCombo, _lastMusicMode);
        SelectByTag(AudioTargetCombo, voiceMode ? "voice" : autoMode ? "auto" : "music");
        SyncAudioModeVisibility(voiceMode ? "voice" : autoMode ? "auto" : "music");
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
        // 伴随底色怎么跑：播放方式 / 固定动作 / 节奏档 / 起伏档（老配置没有这些字段时已由 Normalize 补默认）
        SelectByTag(CompanionPlayModeCombo, CompanionPlayPolicy.NormalizePlayMode(App.Settings.CompanionPlayMode));
        SelectByTag(CompanionPaceCombo, CompanionPlayPolicy.NormalizePace(App.Settings.CompanionPace));
        SelectByTag(CompanionVariationCombo, CompanionPlayPolicy.NormalizeVariation(App.Settings.CompanionVariation));
        LoadStruggleUi();
        // 动作来源（旧配置里没有这个字段时，AppSettings 已按旧「做法」迁移好）
        SelectByTag(CompanionKindCombo, App.Settings.CompanionSource);
        AutoPresetCheck.IsChecked = App.Settings.CompanionAuto;
        // 底色动作/强度：两条路共用的一套
        SelectByTag(CompanionIntensityCombo, IntensityTagFor(App.Settings.CompanionBaseIntensity));
        BuildCompanionPoolList();
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


    // ── 伴随动作池：动作页的动作勾选成一组，伴随没信号时轮播 ─────────────────
    private bool _loadingCompanionPool;

    /// <summary>按动作库现况重建勾选列表（动作页新建/改名的动作会立刻出现在这里）。</summary>
    private void BuildCompanionPoolList()
    {
        if (CompanionPoolList is null) return;
        var selected = new HashSet<string>(App.Settings.CompanionPool ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        _loadingCompanionPool = true;
        try
        {
            CompanionPoolList.Children.Clear();
            foreach (var preset in Hexa.ViewModels.StrokesViewModel.AllPresets)
            {
                if (string.IsNullOrWhiteSpace(preset.Id)) continue;
                string label = string.IsNullOrWhiteSpace(preset.Cat) ? preset.Label : $"{preset.Label} · {preset.Cat}";
                var box = new CheckBox
                {
                    Content = label,
                    Tag = preset.Id,
                    FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 1),
                    IsChecked = selected.Contains(preset.Id),
                    ToolTip = $"id：{preset.Id}",
                };
                box.Checked += CompanionPoolItem_Changed;
                box.Unchecked += CompanionPoolItem_Changed;
                CompanionPoolList.Children.Add(box);
            }
        }
        finally
        {
            _loadingCompanionPool = false;
        }
        CompanionPoolIntensitySlider.Value = Math.Clamp(App.Settings.CompanionPoolIntensity, 0.5, 1.6);
        UpdateCompanionPoolCount();
    }

    private void UpdateCompanionPoolCount()
    {
        if (CompanionPoolCountText is null) return;
        int count = App.Settings.CompanionPool?.Count ?? 0;
        CompanionPoolCountText.Text = count == 0
            ? $"没勾（默认自然流动）· 动作库 {CompanionPoolList?.Children.Count ?? 0} 个"
            : $"已选 {count} 个 · 动作库 {CompanionPoolList?.Children.Count ?? 0} 个";
        CompanionPoolIntensityLabel.Text = $"{Math.Clamp(App.Settings.CompanionPoolIntensity, 0.5, 1.6):0.0}×";
    }

    private void CompanionPoolItem_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCompanionPool) return;
        var ids = new List<string>();
        foreach (object child in CompanionPoolList.Children)
            if (child is CheckBox { IsChecked: true, Tag: string id }) ids.Add(id);
        App.Settings.CompanionPool = ids;
        // 池子进的是规则引擎的动作（GameCompanionRules 会把它编译成 free_play + 池子强度），
        // 所以这里必须走 ApplyCompanionRules：只 Save 的话要等下一次改设置才生效。
        ApplyCompanionRules();
        UpdateCompanionPoolCount();
    }

    private void CompanionPoolAll_Click(object sender, RoutedEventArgs e) => SetCompanionPoolAll(true);

    private void CompanionPoolNone_Click(object sender, RoutedEventArgs e) => SetCompanionPoolAll(false);

    private void SetCompanionPoolAll(bool on)
    {
        _loadingCompanionPool = true;
        try
        {
            foreach (object child in CompanionPoolList.Children)
                if (child is CheckBox box) box.IsChecked = on;
        }
        finally
        {
            _loadingCompanionPool = false;
        }
        CompanionPoolItem_Changed(this, new RoutedEventArgs());
    }

    private void CompanionPoolIntensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CompanionPoolIntensityLabel is null) return;
        App.Settings.CompanionPoolIntensity = Math.Clamp(e.NewValue, 0.5, 1.6);
        CompanionPoolIntensityLabel.Text = $"{App.Settings.CompanionPoolIntensity:0.0}×";
        if (_loadingCompanionPool) return;
        GameCompanionRules.Apply(App.Settings);   // 只重编规则，不写盘 —— 拖动时每秒几十次写文件没意义
        ScheduleSave();
        if (App.Settings.RuleEngineEnabled) _rules.Refresh();
    }
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
        // 角色声音 / 自动模式下「律动方式」是收起来的：这时候它不该去改设置（改了会把 voice / auto 顶掉）。
        if (!IsMusicTargetSelected) return;
        App.Settings.AudioResponseMode = mode;
        ScheduleSave();
    }

    /// <summary>「响应对象」三选一：角色声音（voice）/ 音乐律动（用「律动方式」那一项）/ 自动（auto）。</summary>
    private void AudioTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || SelectedItem(AudioTargetCombo) is not { Tag: string target }) return;
        bool voice = string.Equals(target, "voice", StringComparison.Ordinal);
        bool auto = string.Equals(target, "auto", StringComparison.Ordinal);
        // 一个设置字段表示三种模式：角色声音 "voice"、自动 "auto"、音乐律动写「律动方式」那一项。
        App.Settings.AudioResponseMode = voice ? "voice" : auto ? "auto" : _lastMusicMode;
        SyncAudioModeVisibility(target);
        // 换对象 = 换一路信号源：让采集从干净状态重新起步（和「事件化动作」那个勾选的做法一致），
        // 否则检测器里可能还留着很久以前的统计量，切过去头几帧会算出一个假的跃升。
        _audio.Refresh();
        ScheduleSave();
    }

    /// <summary>「响应对象」现在选的是不是「角色声音」（Tag = voice）。</summary>
    private bool IsVoiceTargetSelected =>
        AudioTargetCombo?.SelectedItem is ComboBoxItem { Tag: string tag } && tag == "voice";

    /// <summary>「响应对象」现在选的是不是「自动」（Tag = auto）。</summary>
    private bool IsAutoTargetSelected =>
        AudioTargetCombo?.SelectedItem is ComboBoxItem { Tag: string tag } && tag == "auto";

    /// <summary>「响应对象」现在选的是不是「音乐律动」（Tag = music）—— 只有它才让「律动方式」改设置。</summary>
    private bool IsMusicTargetSelected =>
        AudioTargetCombo?.SelectedItem is ComboBoxItem { Tag: string tag } && tag == "music";

    /// <summary>
    /// 切「响应对象」时只改可见性，不删控件：「律动方式」在人声模式下没有意义
    ///（人声走的是抽插，不分自然起伏/低音/鼓点），但它的选择值要留着，切回音乐时接着用。
    /// </summary>
    private void SyncAudioModeVisibility(string target)
    {
        if (AudioMusicModePanel is null) return;    // XAML 解析中途可能还没建好
        bool voice = target == "voice";
        bool auto = target == "auto";
        AudioMusicModePanel.Visibility = voice || auto ? Visibility.Collapsed : Visibility.Visible;
        AudioVoiceNote.Visibility = voice ? Visibility.Visible : Visibility.Collapsed;
        AudioTargetHint.Text = voice
            ? "只认人声频段（150–1200Hz）：呻吟、喘息、说话会让设备动，鼓点、低音、爆炸声推不动它。每次呻吟 = 一次抽插（插进去、再抽出来）：越响抽得越深、越快跟得越紧；声音停了还会再抽约 1.5 秒、一下比一下浅，然后停住。"
            : auto
                ? "自动判断：它自己在听 —— 听出呻吟就按「一次发声 = 一次抽插」，听出音乐就跟着鼓点律动，听出肉体碰撞就一下一下地顶，安静时不动。判定结果实时显示在下面「当前听到」。"
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

    // ── 声音活动范围：用项目统一的双滑块控件（Controls\RangeSlider）─────
    // 用户原话「我要的是一个条调整两个值」：一条轨两个把手，左=最低位置、右=最高位置。
    private bool _audioRangeSync;      // 正在把值写回控件，别让它再触发一次自己
    private const double AudioRangeGap = 5;

    // ── 画面同步：活动范围（一条轨两个把手） ───────────────────────────
    private bool _screenRangeLoaded;
    private bool _screenRangeSync;
    private const double ScreenRangeGap = 5;

    // ── 活动范围：拖把手时设备直接跟到指定位置（用户原话「滑动时设备也跟着到指定范围」）──
    private DispatcherTimer? _rangeSweepTimer;       // 松手后扫一遍：最低 → 最高 → 回到中间
    private DispatcherTimer? _rangeSettleTimer;      // 松手判定（600ms 没再动才算拖完）
    private double _rangeSweepLower, _rangeSweepUpper, _rangeSweepT;
    private double _rangeSettleLower, _rangeSettleUpper;
    private double _screenRangePrevLower = -1, _screenRangePrevUpper = -1;
    private double _audioRangePrevLower = -1, _audioRangePrevUpper = -1;

    /// <summary>把设置里的 [最低, 最高] 灌进两个把手（页面初始化时一次）。</summary>
    private void InitScreenRange()
    {
        if (ScreenRange is null) return;
        _screenRangeSync = true;
        try
        {
            ScreenRange.LowerValue = Math.Clamp(App.Settings.ScreenSyncMinPosition, 0, 100);
            ScreenRange.UpperValue = Math.Clamp(App.Settings.ScreenSyncMaxPosition, 0, 100);
        }
        finally { _screenRangeSync = false; }
        _screenRangePrevLower = App.Settings.ScreenSyncMinPosition;
        _screenRangePrevUpper = App.Settings.ScreenSyncMaxPosition;
        _screenRangeLoaded = true;
        if (ScreenRangeLabel is not null)
            ScreenRangeLabel.Text = $"{App.Settings.ScreenSyncMinPosition:0}% – {App.Settings.ScreenSyncMaxPosition:0}%";
        {
            // 深浅由「深度范围」两个把手定，深度倍率固定回自动基准
            if (App.Settings.ScreenSyncDepth != 50) { App.Settings.ScreenSyncDepth = 50; ScheduleSave(); }
            // 和声音那边一样：自定义节奏整块去掉，节奏始终由画面自己算；旧的标记熄掉。
            if (App.Settings.ScreenSyncCustom) { App.Settings.ScreenSyncCustom = false; ScheduleSave(); }
        }
    }

    /// <summary>拖动就写回设置：画面同步每帧都读它，拖完立刻生效，不用点保存。</summary>
    private void ScreenRange_Changed(object sender, EventArgs e)
    {
        if (!_screenRangeLoaded || ScreenRange is null || ScreenRangeLabel is null) return;
        if (_screenRangeSync) return;
        double min = Math.Round(Math.Clamp(ScreenRange.LowerValue, 0, 100));
        double max = Math.Round(Math.Clamp(ScreenRange.UpperValue, 0, 100));
        if (max - min < ScreenRangeGap)
        {
            // 两个把手挨到一起时按这次动的是哪一端把另一端推开，永远留 5% 余量。
            if (Math.Abs(ScreenRange.LowerValue - min) >= Math.Abs(ScreenRange.UpperValue - max))
                min = Math.Max(0, max - ScreenRangeGap);
            else
                max = Math.Min(100, min + ScreenRangeGap);
            _screenRangeSync = true;
            ScreenRange.LowerValue = min;
            ScreenRange.UpperValue = max;
            _screenRangeSync = false;
        }
        ScreenRangeLabel.Text = $"{min:0}% – {max:0}%";
        App.Settings.ScreenSyncMinPosition = (int)min;
        App.Settings.ScreenSyncMaxPosition = (int)max;
        // 拖的那一端立刻让设备走过去，「拖到哪就停在哪」；松手后再把整段扫一遍。
        if (_screenRangePrevLower >= 0)
            PreviewAxisTo(Math.Abs(min - _screenRangePrevLower) >= Math.Abs(max - _screenRangePrevUpper) ? min : max);
        _screenRangePrevLower = min;
        _screenRangePrevUpper = max;
        ScheduleRangeSweep(min, max);
        if (_loadingUi) return;
        ScheduleSave();
    }

    private void AudioRange_Changed(object sender, EventArgs e)
    {
        if (!_audioRangeLoaded || AudioRange is null || AudioRangeLabel is null) return;
        if (_audioRangeSync) return;
        double min = Math.Round(Math.Clamp(AudioRange.LowerValue, 0, 100));
        double max = Math.Round(Math.Clamp(AudioRange.UpperValue, 0, 100));
        if (max - min < AudioRangeGap)
        {
            // 两个把手挤到一起时，按这次动的是哪一端把另一端推开，永远留 5% 余量。
            if (Math.Abs(AudioRange.LowerValue - min) >= Math.Abs(AudioRange.UpperValue - max))
                min = Math.Max(0, max - AudioRangeGap);
            else
                max = Math.Min(100, min + AudioRangeGap);
            _audioRangeSync = true;
            AudioRange.LowerValue = min;
            AudioRange.UpperValue = max;
            _audioRangeSync = false;
        }
        AudioRangeLabel.Text = $"{min:0}% – {max:0}%";
        App.Settings.AudioMinPosition = min;
        App.Settings.AudioMaxPosition = max;
        // 声音这条同样要求「滑到哪设备就到哪」，松手后扫一遍让人看见边界。
        if (_audioRangePrevLower >= 0)
            PreviewAxisTo(Math.Abs(min - _audioRangePrevLower) >= Math.Abs(max - _audioRangePrevUpper) ? min : max);
        _audioRangePrevLower = min;
        _audioRangePrevUpper = max;
        ScheduleRangeSweep(min, max);
        if (_loadingUi) return;
        ScheduleSave();
    }

    // 拖动预览的“脉冲”：每 50ms 重发一次目标位置。
    // 为什么不是只发一次：伴随/画面/声音任何一路只要插进来一帧，单次 BlendTo 就被盖掉了，
    // 用户看到的就是「拖了没反应」。连发到松手为止，谁来盖都被下一拍拉回来。
    private DispatcherTimer? _previewPulseTimer;
    private double _previewTarget = double.NaN;
    private long _previewUntilMs;

    /// <summary>
    /// 把设备主轴直接摆到 percent%：拖「活动范围」把手时用（连发到 holdMs 之后）。
    /// </summary>
    private void PreviewAxisTo(double percent, int holdMs = 1000)
    {
        ScreenWatch.PauseForRangePreview(holdMs);
        _audio.PauseForRangePreview(holdMs);
        _rules.PauseForRangePreview(holdMs);

        double l0 = Math.Clamp(percent, 0, 100);
        bool targetChanged = double.IsNaN(_previewTarget) || Math.Abs(_previewTarget - l0) >= 1;
        _previewTarget = l0;
        _previewUntilMs = Environment.TickCount64 + Math.Clamp(holdMs, 200, 5000);

        bool can = App.Engine.CanRun;
        bool ok = false;
        if (can)
        {
            try { ok = App.Engine.BlendTo([l0, 50, 50, 50, 50, 50], 0.12); }
            catch (Exception ex) { AppLogger.Warn($"范围预览：摆位置出错 —— {ex.Message}"); }
        }

        if (RangePreviewHint is not null)
            RangePreviewHint.Text = can
                ? $"预览：设备摆到 {l0:0}%{(ok ? "" : "（引擎没接这一帧，正在重试）")} · 松手后自动扫一遍：最低 → 最高 → 回中"
                : "预览：设备现在动不了（没连上 / 急停 / 输出锁着），范围改了但没发指令";

        // 只在开始新的目标时记一条，免得拖一下刷几百行日志。
        if (targetChanged || !can) AppLogger.Info($"范围预览：目标 L0={l0:0}% · CanRun={can} · 下发={ok}");

        if (_previewPulseTimer is null)
        {
            _previewPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _previewPulseTimer.Tick += PreviewPulse_Tick;
        }
        _previewPulseTimer.Start();
    }

    /// <summary>脉冲：把目标位置按住不放，任何一路插帧都会被下一拍压回来。</summary>
    private void PreviewPulse_Tick(object? sender, EventArgs e)
    {
        if (Environment.TickCount64 > _previewUntilMs || double.IsNaN(_previewTarget))
        {
            _previewPulseTimer?.Stop();
            return;
        }
        if (!App.Engine.CanRun) return;
        try { App.Engine.BlendTo([_previewTarget, 50, 50, 50, 50, 50], 0.12); }
        catch (Exception) { /* 设备刚断开：下一拍再说 */ }
    }
    /// <summary>松手 600ms 后扫一遍这段范围：最低 → 最高 → 回到中间，一眼看出边界在哪。</summary>
    private void ScheduleRangeSweep(double lower, double upper)
    {
        _rangeSweepTimer?.Stop();
        if (_rangeSettleTimer is null)
        {
            _rangeSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _rangeSettleTimer.Tick += RangeSettle_Tick;
        }
        _rangeSettleLower = lower;
        _rangeSettleUpper = upper;
        _rangeSettleTimer.Stop();
        _rangeSettleTimer.Start();
    }

    private void RangeSettle_Tick(object? sender, EventArgs e)
    {
        _rangeSettleTimer?.Stop();
        StartRangeSweep(_rangeSettleLower, _rangeSettleUpper);
    }

    private void StartRangeSweep(double lower, double upper)
    {
        if (upper - lower < 1) return;
        _rangeSweepLower = lower;
        _rangeSweepUpper = upper;
        _rangeSweepT = 0;
        if (_rangeSweepTimer is null)
        {
            _rangeSweepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _rangeSweepTimer.Tick += RangeSweep_Tick;
        }
        _rangeSweepTimer.Start();
    }

    private void RangeSweep_Tick(object? sender, EventArgs e)
    {
        const double total = 2.4;
        _rangeSweepT += 0.05;
        if (_rangeSweepT >= total)
        {
            _rangeSweepTimer?.Stop();
            PreviewAxisTo((_rangeSweepLower + _rangeSweepUpper) / 2, 800);   // 收在中位，别停在边界
            return;
        }
        double u = _rangeSweepT / total;
        double k = u < 0.5 ? u * 2 : (1 - u) * 2;                            // 往复一趟
        PreviewAxisTo(_rangeSweepLower + (_rangeSweepUpper - _rangeSweepLower) * k);
    }

    /// <summary>声音门槛滑块：写回设置（多大声音才算「有声音」）。</summary>
    private void AudioGate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioGateLabel is null || !_audioRangeLoaded) return;
        AudioGateLabel.Text = $"{e.NewValue * 100:0}%";
        App.Settings.AudioNoiseGate = Math.Clamp(e.NewValue, 0, 0.4);
        if (_loadingUi) return;
        App.Settings.Save();
    }

    /// <summary>声音响应卡片旁的「■ 停止」：复用底部一键停止（含急停与关闭自动跟随）。</summary>
    /// <summary>播放方式 / 节奏档 / 起伏档：只写这几个字段，再让规则引擎重算底色。</summary>
    private void CompanionBasePolicy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.CompanionPlayMode = CompanionPlayPolicy.NormalizePlayMode(SelectedItem(CompanionPlayModeCombo)?.Tag as string);
        App.Settings.CompanionPace = CompanionPlayPolicy.NormalizePace(SelectedItem(CompanionPaceCombo)?.Tag as string);
        App.Settings.CompanionVariation = CompanionPlayPolicy.NormalizeVariation(SelectedItem(CompanionVariationCombo)?.Tag as string);
        App.Settings.Save();
        ApplyCompanionRules();
        RefreshBoundEngine();
        RefreshCompanionBoard();
        RefreshGameProfileText();
    }

    /// <summary>挣扎连点：回填开关 / 灵敏度 / 反馈强度（反馈强度滑条默认 1.0）。</summary>
    private void LoadStruggleUi()
    {
        if (StruggleCheck is null) return;
        bool wasLoading = _loadingUi;
        _loadingUi = true;
        try
        {
            StruggleCheck.IsChecked = App.Settings.CompanionStruggleEnabled;
            SelectByTag(StruggleSensitivityCombo, CompanionPlayPolicy.NormalizeSensitivity(App.Settings.CompanionStruggleSensitivity));
            double feedback = double.IsFinite(App.Settings.CompanionStruggleFeedback)
                ? Math.Clamp(App.Settings.CompanionStruggleFeedback, 0, 1.5) : 1.0;
            StruggleFeedbackSlider.Value = feedback;
            StruggleFeedbackLabel.Text = feedback.ToString("0.0");
            SyncStruggleUi();
        }
        finally { _loadingUi = wasLoading; }
    }

    private void SyncStruggleUi()
    {
        bool enabled = StruggleCheck?.IsChecked == true;
        if (StrugglePanel is not null) StrugglePanel.IsEnabled = enabled;
    }

    private void SaveStruggle()
    {
        App.Settings.CompanionStruggleEnabled = StruggleCheck?.IsChecked == true;
        App.Settings.CompanionStruggleSensitivity = CompanionPlayPolicy.NormalizeSensitivity(SelectedItem(StruggleSensitivityCombo)?.Tag as string);
        App.Settings.CompanionStruggleFeedback = double.IsFinite(StruggleFeedbackSlider.Value)
            ? Math.Clamp(StruggleFeedbackSlider.Value, 0, 1.5) : 1.0;
        App.Settings.Save();
        // 让正在跑的那台设备立刻按新灵敏度统计；游戏专属配置优先，键位轮询器自己会在切前台时重算。
        App.Engine?.ConfigureStruggle(App.Settings.CompanionStruggleEnabled,
            App.Settings.CompanionStruggleSensitivity, App.Settings.CompanionStruggleFeedback);
        SyncStruggleUi();
    }

    private void Struggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        SaveStruggle();
        RefreshCompanionBoard();
    }

    private void StruggleSensitivity_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        SaveStruggle();
    }

    private void StruggleFeedback_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StruggleFeedbackLabel is null || _loadingUi) return;
        StruggleFeedbackLabel.Text = e.NewValue.ToString("0.0");
        SaveStruggle();
    }

    /// <summary>
    /// 状态行：现在跟谁 / 在等什么 / 是不是只剩底色。融合与等待的判定与引擎保持一致，
    /// 免得界面说「跟画面」而设备其实在等游戏信号。
    /// </summary>
    private void RefreshCompanionRoute()
    {
        if (CompanionRouteText is null) return;
        string source = CompanionSource;                                  // auto / screen / sound / telemetry
        bool screenWanted = source is "auto" or "screen";
        bool audioWanted = source is "auto" or "sound";
        bool followScreen = screenWanted && App.Settings.ScreenWatchEnabled;
        bool followAudio = audioWanted && App.Settings.AudioReactiveEnabled;
        bool companionOn = App.Settings.RuleEngineEnabled || App.Settings.CompanionAuto;
        bool waitingForGameSignal = source == "telemetry" && !App.RuleEngine.TelemetryActive;
        string route = CompanionPlayPolicy.DescribeRoute(followScreen, followAudio, companionOn, waitingForGameSignal);
        CompanionBaseChoice choice = CompanionPlayPolicy.Resolve(App.Settings, App.Settings.CompanionProcess);
        string play = choice.PlayMode == CompanionPlayPolicy.Pool
            ? $"轮流（{choice.Pool.Count} 个动作，首尾循环）"
            : $"固定「{ModeLabel(choice.Mode)}」";
        // 切窗降级要说清楚：不是退出，只是先慢一点、小一点，切回就接上。
        if (App.Engine?.IsBackgroundDegraded == true) route += "（切窗了：先慢速小幅动着，切回就平滑接上）";
        string pace = choice.Pace switch
        {
            CompanionPlayPolicy.Slow => "慢",
            CompanionPlayPolicy.Fast => "快",
            _ => "中",
        };
        string variation = choice.Variation == CompanionPlayPolicy.Natural ? "自然起伏" : "稳定";
        string struggle = App.Settings.CompanionStruggleEnabled
            ? $" · 挣扎连点开（灵敏度 {CompanionPlayPolicy.NormalizeSensitivity(App.Settings.CompanionStruggleSensitivity)}）"
            : " · 挣扎连点关";
        string text = $"跟谁：{route} · 底色：{play} · 节奏{pace} · {variation} · 强度 {choice.Intensity:0.0}{struggle}";
        if (CompanionRouteText.Text != text) CompanionRouteText.Text = text;
    }

    private void CompanionSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        string pickedBase = (SelectedItem(CompanionBaseCombo)?.Tag as string) ?? "organic_flow";
        App.Settings.CompanionBaseMode = pickedBase;
        // 「固定」模式下用户挑的这一个就是底色动作：记下来，下次进来还跑它（轮流模式不看它）。
        App.Settings.CompanionSelectedAction = pickedBase;
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
        AudioEventCheck.IsChecked = App.Settings.AudioEventMotion;
        MultiAxisCheck.IsChecked = App.Settings.MotionMultiAxis;
        ScreenPoseEnableCheck.IsChecked = App.Settings.PoseVisionEnabled;
        AutoFollowScreenCheck.IsChecked = App.Settings.AutoFollowScreen;
        ScreenFollowForegroundCheck.IsChecked = App.Settings.ScreenFollowOnlyWhenForeground;
        InitScreenRange();
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
            GameProfileText.Text = "先在「目标游戏」下拉里选一个窗口，才能给它单独记住一套配置。";
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
            string play = string.Equals(profile.PlayMode, CompanionPlayPolicy.Pool, StringComparison.OrdinalIgnoreCase)
                ? $"动作池轮流（{profile.Pool?.Count ?? 0} 个）"
                : $"固定「{label}」";
            string pace = CompanionPlayPolicy.NormalizePace(profile.Pace) switch
            {
                CompanionPlayPolicy.Slow => "节奏慢",
                CompanionPlayPolicy.Fast => "节奏快",
                _ => "节奏中",
            };
            string variation = CompanionPlayPolicy.NormalizeVariation(profile.Variation) == CompanionPlayPolicy.Natural ? "自然起伏" : "稳定";
            string struggle = profile.StruggleEnabled is { } own
                ? (own ? "挣扎连点开" : "挣扎连点关")
                : "挣扎跟全局";
            GameProfileText.Text = $"已给「{process}」存了专属底色：{play} · {pace} · {variation} · 强度{intensity} · {struggle}（伴随开着时也用它）。";
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

        // 保存「这个游戏的底色」：动作 / 播放方式 / 节奏 / 起伏 / 池子 / 挣扎都一起固化。
        // 临时调整（热键换的招、拖滑条的临时值）不会走到这里，所以不会被悄悄写死。
        App.Settings.GameProfiles[process] = new Hexa.Models.GameProfile
        {
            Process = process,
            Mode = (SelectedItem(CompanionBaseCombo)?.Tag as string) ?? App.Settings.CompanionBaseMode,
            Intensity = SelectedItem(CompanionIntensityCombo)?.Tag is string tag && double.TryParse(tag, out double value)
                ? value
                : App.Settings.CompanionBaseIntensity,
            Pace = CompanionPlayPolicy.NormalizePace(SelectedItem(CompanionPaceCombo)?.Tag as string),
            PlayMode = CompanionPlayPolicy.NormalizePlayMode(SelectedItem(CompanionPlayModeCombo)?.Tag as string),
            Variation = CompanionPlayPolicy.NormalizeVariation(SelectedItem(CompanionVariationCombo)?.Tag as string),
            Pool = (App.Settings.CompanionPool ?? new List<string>()).ToList(),
            StruggleEnabled = StruggleCheck?.IsChecked == true,
            StruggleSensitivity = CompanionPlayPolicy.NormalizeSensitivity(SelectedItem(StruggleSensitivityCombo)?.Tag as string),
            StruggleFeedback = Math.Clamp(StruggleFeedbackSlider.Value, 0, 1.5),
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
        // 普通按键脉冲已经摘掉：ActionLinkEnabled 只作为老配置的残留字段保留（恒为 false），
        // 交互改成「挣扎连点」那套，见 Struggle_Changed。
        App.Settings.ActionLinkEnabled = false;
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
        if (App.Settings.CompanionAuto || App.Settings.RuleEngineEnabled)
        {
            _autoFollowStoppedByEmergency = false;
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
        RefreshScreenWatch();    // 画面同步卡里那句「现在这条信号会不会驱动设备」跟着来源变
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
                    + "画面里没事发生就缓降回中停着。要先到「测试台 · 画面同步」里开启同步并挑好窗口。",
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
            return;
        }

        // 不绑游戏的话 ForegroundWatcher / 规则引擎都不会动，
        // 用户看到"已开启"但设备永远不动，还以为是坏了。
        if (string.IsNullOrWhiteSpace(CompanionProcessBox.Text))
        {
            CaptureHint.Foreground = (Brush)FindResource("Muted");
            CaptureHint.Text = "还没选游戏：先在上面「目标游戏」下拉里挑一个窗口，列表里没有就点「↻ 刷新」。不选的话设备永远不动。";
            CompanionWindowCombo.Focus();
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
            ("FanoutCard", FanoutCard),
        };
        if (!cards.Any(card => card.Name == cardName)) cardName = cards[0].Name;
        _activeCardName = cardName;

        foreach ((string name, FrameworkElement card) in cards)
            card.Visibility = name == cardName ? Visibility.Visible : Visibility.Collapsed;

        foreach (Button btn in FeatureSwitchGrid.Children.OfType<Button>())
            btn.Style = (Style)FindResource((btn.Tag as string) == cardName ? "FeatureBtnOn" : "FeatureBtnOff");

        // 切过来立刻刷一次：否则最多 150ms 显示的是上一块卡片的旧文案。
        RefreshStatus();
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
        if (AudioSceneLabel != null)
            AudioSceneLabel.Text = capturing
                ? "当前听到：" + _audio.SceneDescribe()
                : "当前听到：—（还没在听）";
        if (_levelFill != null && LevelTrack.ActualWidth > 0)
            _levelFill.Width = Math.Clamp(energy, 0, 1) * LevelTrack.ActualWidth;
        RefreshAudioState();
        RefreshAudioEventLabel();
        RefreshAudioTempoLabel();
        RefreshFanoutCard();

        string foreground = ReadForegroundProcessNameCached();
        RefreshCompanionBoard(foreground);

        UpdateAudioToggle();
        UpdateRuleToggle();
        RefreshScreenWatch();
    }

    // ══ 画面同步（观察模式）═══════════════════════════════════════════
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

        // 四条信号的门槛竖线：叠在填充条之上，左边缘按门槛百分比定位。
        _screenModelScoreThresholdLine = AddThresholdLine(ScreenModelScoreTrack);
        _screenMotionThresholdLine = AddThresholdLine(ScreenMotionTrack);
        _screenRhythmThresholdLine = AddThresholdLine(ScreenRhythmTrack);
        _screenScoreThresholdLine = AddThresholdLine(ScreenScoreTrack);
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
        // 轨道只装一个孩子（填充条），门槛竖线要叠在填充上面，所以先包一层 Grid。
        var host = new Grid();
        host.Children.Add(fill);
        track.Child = host;
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

    /// <summary>在轨道里叠一条 3px 门槛竖线（填充条已包在 Grid 里，竖线加进同一层、叠在填充之上）。</summary>
    private Rectangle AddThresholdLine(Border track)
    {
        var line = new Rectangle
        {
            Width = 3,
            Fill = (Brush)FindResource("Text"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            RadiusX = 1.5,
            RadiusY = 1.5,
        };
        if (track.Child is Grid host) host.Children.Add(line);
        return line;
    }

    private static void PositionThresholdLine(Rectangle? line, Border? track, double threshold)
    {
        if (line is null || track is null) return;
        double width = track.ActualWidth;
        if (width <= 1 || threshold <= 0)
        {
            line.Visibility = Visibility.Collapsed;
            return;
        }
        line.Visibility = Visibility.Visible;
        double left = Math.Clamp(threshold, 0, 0.95) * width - 1.5;   // 3px 线居中在门槛点
        line.Margin = new Thickness(Math.Max(0, left), 0, 0, 0);
    }

    private static string ThresholdText(double value) =>
        value <= 0 ? "门槛 · 不筛" : $"门槛 {value * 100:0}%";

    /// <summary>把某条信号的门槛刷到滑条 + 文字 + 竖线（程序回填 / 条上拖动共用）。</summary>
    private void SetThresholdUi(Slider slider, TextBlock label, Border track, Rectangle? line, double value)
    {
        value = Math.Clamp(value, 0, 0.95);
        _syncingThreshold = true;
        try { slider.Value = value; }
        finally { _syncingThreshold = false; }
        label.Text = ThresholdText(value);
        PositionThresholdLine(line, track, value);
    }

    /// <summary>RefreshScreenWatch 每 150ms 把四条门槛同步回来；拖动中 ValueChanged 靠 _syncingThreshold 跳过回写。</summary>
    private void SyncThresholds()
    {
        if (ScreenModelScoreThresholdSlider is null) return;
        SetThresholdUi(ScreenModelScoreThresholdSlider, ScreenModelScoreThresholdLabel, ScreenModelScoreTrack, _screenModelScoreThresholdLine, App.Settings.ScreenSyncMinModelScore);
        SetThresholdUi(ScreenMotionThresholdSlider, ScreenMotionThresholdLabel, ScreenMotionTrack, _screenMotionThresholdLine, App.Settings.ScreenSyncMinMotion);
        SetThresholdUi(ScreenRhythmThresholdSlider, ScreenRhythmThresholdLabel, ScreenRhythmTrack, _screenRhythmThresholdLine, App.Settings.ScreenSyncMinRhythm);
        SetThresholdUi(ScreenScoreThresholdSlider, ScreenScoreThresholdLabel, ScreenScoreTrack, _screenScoreThresholdLine, App.Settings.ScreenSyncMinSceneScore);
    }

    private void ApplyTrackThreshold(Border track, double value)
    {
        value = Math.Round(value / 0.01) * 0.01;   // 吸附到 0.01
        value = Math.Clamp(value, 0, 0.95);

        if (ReferenceEquals(track, ScreenModelScoreTrack))
        {
            App.Settings.ScreenSyncMinModelScore = value;
            SetThresholdUi(ScreenModelScoreThresholdSlider, ScreenModelScoreThresholdLabel, track, _screenModelScoreThresholdLine, value);
        }
        else if (ReferenceEquals(track, ScreenMotionTrack))
        {
            App.Settings.ScreenSyncMinMotion = value;
            SetThresholdUi(ScreenMotionThresholdSlider, ScreenMotionThresholdLabel, track, _screenMotionThresholdLine, value);
        }
        else if (ReferenceEquals(track, ScreenRhythmTrack))
        {
            App.Settings.ScreenSyncMinRhythm = value;
            SetThresholdUi(ScreenRhythmThresholdSlider, ScreenRhythmThresholdLabel, track, _screenRhythmThresholdLine, value);
        }
        else if (ReferenceEquals(track, ScreenScoreTrack))
        {
            App.Settings.ScreenSyncMinSceneScore = value;
            SetThresholdUi(ScreenScoreThresholdSlider, ScreenScoreThresholdLabel, track, _screenScoreThresholdLine, value);
        }
        ScheduleSave();
    }

    private void SetThresholdFromTrack(Border track, MouseEventArgs e)
    {
        double width = track.ActualWidth;
        if (width <= 1) return;
        double frac = Math.Clamp(e.GetPosition(track).X / width, 0, 1);
        ApplyTrackThreshold(track, frac * 0.95);   // 条最右端 = 门槛 0.95
    }

    private void ScreenThresholdTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border track) return;
        _thresholdDrag = true;
        track.CaptureMouse();
        SetThresholdFromTrack(track, e);
        e.Handled = true;
    }

    private void ScreenThresholdTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_thresholdDrag || sender is not Border track) return;
        if (!track.IsMouseCaptured) { _thresholdDrag = false; return; }
        if (e.LeftButton != MouseButtonState.Pressed) { EndThresholdTrackDrag(track); return; }
        SetThresholdFromTrack(track, e);
    }

    private void ScreenThresholdTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndThresholdTrackDrag(sender as Border);
    }

    private void EndThresholdTrackDrag(Border? track)
    {
        if (track is not null && track.IsMouseCaptured) track.ReleaseMouseCapture();
        _thresholdDrag = false;
        App.Settings.Save();   // 松手立刻落盘
    }

    private void ThresholdSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingScreenUi || _syncingThreshold) return;

        if (ReferenceEquals(sender, ScreenModelScoreThresholdSlider))
        {
            if (ScreenModelScoreThresholdLabel is null) return;
            UpdateThresholdReadout(ScreenModelScoreThresholdLabel, _screenModelScoreThresholdLine, ScreenModelScoreTrack, e.NewValue);
            App.Settings.ScreenSyncMinModelScore = Math.Clamp(e.NewValue, 0, 0.95);
        }
        else if (ReferenceEquals(sender, ScreenMotionThresholdSlider))
        {
            if (ScreenMotionThresholdLabel is null) return;
            UpdateThresholdReadout(ScreenMotionThresholdLabel, _screenMotionThresholdLine, ScreenMotionTrack, e.NewValue);
            App.Settings.ScreenSyncMinMotion = Math.Clamp(e.NewValue, 0, 0.95);
        }
        else if (ReferenceEquals(sender, ScreenRhythmThresholdSlider))
        {
            if (ScreenRhythmThresholdLabel is null) return;
            UpdateThresholdReadout(ScreenRhythmThresholdLabel, _screenRhythmThresholdLine, ScreenRhythmTrack, e.NewValue);
            App.Settings.ScreenSyncMinRhythm = Math.Clamp(e.NewValue, 0, 0.95);
        }
        else if (ReferenceEquals(sender, ScreenScoreThresholdSlider))
        {
            if (ScreenScoreThresholdLabel is null) return;
            UpdateThresholdReadout(ScreenScoreThresholdLabel, _screenScoreThresholdLine, ScreenScoreTrack, e.NewValue);
            App.Settings.ScreenSyncMinSceneScore = Math.Clamp(e.NewValue, 0, 0.95);
        }
        ScheduleSave();
    }

    private void UpdateThresholdReadout(TextBlock? label, Rectangle? line, Border track, double value)
    {
        if (label is null) return;
        label.Text = ThresholdText(value);
        PositionThresholdLine(line, track, value);
    }

    private void ScreenPoseDeadZone_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScreenPoseDeadZoneLabel is null) return;
        ScreenPoseDeadZoneLabel.Text = $"{e.NewValue:0.0} /秒";
        if (_loadingScreenUi) return;
        App.Settings.ScreenSyncPoseMinSpeed = Math.Clamp(e.NewValue, 0, 30);
        ScheduleSave();
    }

    private void LoadScreenWatchUi()
    {
        _loadingScreenUi = true;
        try
        {
            ScreenSensitivitySlider.Value = Math.Clamp(App.Settings.ScreenWatchSensitivity,
                ScreenWatchService.SensitivityMin, ScreenWatchService.SensitivityMax);
            ScreenFollowLockSlider.Value = Math.Clamp(App.Settings.ScreenPhaseLock, 0, 1);
            ScreenPoseDeadZoneSlider.Value = Math.Clamp(App.Settings.ScreenSyncPoseMinSpeed, 0, 30);
            ScreenSyncInvertCheck.IsChecked = App.Settings.ScreenSyncInvertDirection;
            ScreenSyncTiltCheck.IsChecked = App.Settings.ScreenSyncTiltEnabled;
            ScreenMirrorPoseCheck.IsChecked = App.Settings.ScreenSyncMirrorPose;
            ScreenAudioRhythmCheck.IsChecked = App.Settings.ScreenSyncAudioRhythm;
            SelectModelTier(App.Settings.NsfwModelTier);   // 档位下拉选回上次那一档
        }
        finally { _loadingScreenUi = false; }
        // 「更多设置」折叠状态只记内存字段：页面重新载入时按用户上次的选择恢复，不写 AppSettings。
        ScreenMoreSettingsExpander.IsExpanded = _screenMoreSettingsOpen;
        ScreenSensitivityLabel.Text = $"{ScreenSensitivitySlider.Value:0.0}×";
        ScreenFollowLockLabel.Text = $"{ScreenFollowLockSlider.Value:0.00}";
        ScreenPoseDeadZoneLabel.Text = $"{ScreenPoseDeadZoneSlider.Value:0.0} /秒";
        SyncThresholds();        // 四条门槛滑条 + 竖线，从设置回填
        ReloadScreenWindows();
        ReloadCompanionWindows();
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
        // 用户手动选了窗口 = 明确要盯着这个窗口：顺手把「换窗口自动跟」关掉，
        // 否则一切前台它就被带走（用户原话「我希望是紧紧锁在我所选择的窗口而不是当前这种我切窗它就跟」）。
        if (App.Settings.AutoFollowScreen)
        {
            App.Settings.AutoFollowScreen = false;
            _loadingUi = true;
            try { AutoFollowScreenCheck.IsChecked = false; } finally { _loadingUi = false; }
            ScheduleSave();
            if (ScreenWatch.IsRunning) ScreenWatch.Refresh();
            if (ScreenWindowHint is not null)
                ScreenWindowHint.Text = "已锁在你选的这个窗口：切到别的窗口也不会跟着变";
        }
        RefreshScreenWatch();
    }

    private void ScreenWindowRefresh_Click(object sender, RoutedEventArgs e)
    {
        ReloadScreenWindows();
        ApplyScreenSelection();
        RefreshScreenWatch();
    }

    // ── 游戏伴随：目标游戏下拉 ───────────────────────────────────────────
    // 用户原话「伴随的窗口选择我希望也是和画面同步那个一样可以直接选择」：
    // 不再让他记进程名、也不再「3 秒后抓取」，直接把桌面上开着的窗口列出来选。
    private bool _loadingCompanionWindowUi;

    /// <summary>重新列一遍窗口，并尽量选回上次绑定的那个进程（进程名对得上就算）。</summary>
    private void ReloadCompanionWindows()
    {
        _loadingCompanionWindowUi = true;
        try
        {
            ScreenWatch.Refresh();
            IReadOnlyList<ScreenWatchWindow> windows = ScreenWatch.Windows;
            CompanionWindowCombo.Items.Clear();
            foreach (ScreenWatchWindow window in windows) CompanionWindowCombo.Items.Add(window);

            string want = (App.Settings.CompanionProcess ?? "").Trim();
            ScreenWatchWindow? pick = null;
            if (want.Length > 0)
            {
                foreach (ScreenWatchWindow window in windows)
                {
                    if (string.Equals(window.ProcessName, want, StringComparison.OrdinalIgnoreCase)) { pick = window; break; }
                }
            }
            if (pick is not null) CompanionWindowCombo.SelectedItem = pick;

            if (windows.Count == 0)
            {
                CaptureHint.Text = "现在桌面上没有能选的窗口：把游戏打开（别最小化），再点「↻ 刷新」。";
            }
            else if (want.Length > 0 && pick is null)
            {
                CaptureHint.Text = $"当前绑定的是 {want}（这个程序现在没开着）。想换就点上面下拉挑一个。";
            }
            else
            {
                CaptureHint.Text = "下拉里选你要玩的窗口就行（读的是它的进程名）。列表里没有？先把它从最小化还原、别用独占全屏，再点「↻ 刷新」。";
            }
        }
        finally { _loadingCompanionWindowUi = false; }
    }

    /// <summary>下拉换了一个窗口：把进程名写进 CompanionProcessBox（老逻辑唯一的真值来源），再走老应用路径。</summary>
    private void CompanionWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCompanionWindowUi || _loadingUi) return;
        if (CompanionWindowCombo.SelectedItem is not ScreenWatchWindow picked) return;
        string process = GameTelemetryProtocol.NormalizeProcessName(picked.ProcessName ?? "");
        if (process.Length == 0) return;

        bool wasLoading = _loadingUi;
        _loadingUi = true;
        try { CompanionProcessBox.Text = process; }
        finally { _loadingUi = wasLoading; }

        ApplyCompanionRules();     // 写设置 + 重新套伴随规则 + 存盘
        RefreshBoundEngine();      // 重新识别引擎（Unity / Unreal / 其它）
        RefreshStatus();
        RefreshCompanionBoard();
        CaptureHint.Text = $"已绑定 {App.Settings.CompanionProcess}（窗口：{picked.Title}）· 识别为 {_boundEngineDisplay}。";
    }

    private void CompanionWindowRefresh_Click(object sender, RoutedEventArgs e) => ReloadCompanionWindows();

    /// <summary>「开启同步 / 关闭同步」——只控制看不看画面，不碰设备（这里没有任何设备调用）。</summary>
    /// <summary>
    /// 一键让画面带动设备：用户原话「你搞的门槛太复杂了我都不知道怎么让它动起来」。
    /// 一个按钮把挡住信号的几层一次放开，别让他自己找原因：
    /// ① 四条门槛 + 姿态死区全部清零（什么都不拦）；② 进场 1 拍、退场 8 拍（马上动、迟点停）；
    /// ③ 只开画面同步这一路：伴随/操作联动不碰（用户原话「这个按钮怎么还是会连伴随一起开」）；
    /// ④ 立刻启动服务。之后想细调都在「更多设置」里，不影响这次一键的结果。
    /// </summary>
    private void ScreenOneKey_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Settings;
        cfg.ScreenSyncMinSceneScore = 0;
        cfg.ScreenSyncMinModelScore = 0;
        cfg.ScreenSyncMinMotion = 0;
        cfg.ScreenSyncMinRhythm = 0;
        cfg.ScreenSyncPoseMinSpeed = 0;
        cfg.ScreenSyncEnterFrames = 1;
        cfg.ScreenSyncExitFrames = 8;
        // 不再顺手打开「跟着当前窗口自动动」：那会让设备在切到任何绑过的窗口时自己动起来，
        // 用户要单独用手动控制时会被抢。要跟当前窗口，自己在下面勾。
        cfg.ScreenWatchEnabled = true;
        // 画面同步是独立的一路：来源保持「自动」，声音/遥测都还能用（以前这里写 "screen"，
        // 等于把声音响应那条路整个关掉，用户看到的就是「开画面声音就不响了」）。
        cfg.CompanionSource = "auto";
        // 一键只管「画面 → 设备」这一条：伴随（RuleEngine）必须他自己点「开启游戏伴随」才开，
        // 否则一点一键就被伴随接管，想单独用手动控制还得先找地方关。
        cfg.Save();

        ScreenWatch.Refresh();
        ScreenWatch.Start();
        LoadScreenWatchUi();
        RefreshScreenWatch();
        if (ScreenOneKeyHint is not null)
            ScreenOneKeyHint.Text = "已经开好了：现在画面里有人动，设备就跟着动。没反应就看下面那句原因（它会说清是哪一层没通）。"
                + " 这一下只开了画面同步，「游戏伴随」没动。";
    }

    /// <summary>
    /// 「单独使用设备」：把会自动接管的几路一次关干净——游戏伴随、画面同步、跟随前台窗口、
    /// 进游戏自动套预设、氛围叠加、声音反应——设备只剩手动/脚本/网页这几条路。
    /// 用的是「全部停止」同一条逻辑（<see cref="StopBtn_Click"/>），不另写一份。
    /// </summary>
    private void StandaloneOnly_Click(object sender, RoutedEventArgs e)
    {
        StopBtn_Click(sender, e);
        App.Settings.AutoFollowScreen = false;
        _loadingUi = true;
        try { if (AutoFollowScreenCheck is not null) AutoFollowScreenCheck.IsChecked = false; }
        finally { _loadingUi = false; }
        App.Settings.Save();
        if (ScreenOneKeyHint is not null)
            ScreenOneKeyHint.Text = "已经切成单独使用：伴随、画面同步、跟随前台窗口、自动套预设都关了，"
                + "设备现在只听手动控制、脚本和网页的。要用画面带动，再点上面那个一键。";
        RefreshScreenWatch();
        RefreshCompanionBoard();
    }

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

    /// <summary>
    /// 伴随里的「声音响应」：只写它自己的开关，不动画面、不动来源（以前两条共用一个来源，开一个就把另一个顶掉）。
    /// </summary>
    private void CompanionSoundCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || CompanionSoundCheck is null) return;
        bool on = CompanionSoundCheck.IsChecked == true;
        if (App.Settings.AudioReactiveEnabled == on) return;
        App.Settings.AudioReactiveEnabled = on;
        App.Settings.Save();
        _audio.Refresh();
        UpdateAudioToggle();
        RefreshAudioState();
    }

    /// <summary>
    /// 伴随里的「画面同步」：同样只写自己的开关。第一次打开时顺手把窗口选中 + 服务起来，
    /// 免得用户打了勾却因为「没选窗口」一动不动（没窗口就把勾退回去，并说清下一步点哪）。
    /// </summary>
    private void CompanionScreenCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingScreenUi || CompanionScreenCheck is null) return;
        bool on = CompanionScreenCheck.IsChecked == true;
        if (App.Settings.ScreenWatchEnabled == on) return;

        if (on)
        {
            ApplyScreenSelection();
            if (ScreenWindowCombo.SelectedItem is not ScreenWatchWindow)
            {
                CompanionChannelsHint.Text = "画面同步：先在下面「🖥 画面同步」里挑一个窗口，再回来打这个勾（或者直接点那边的一键按钮）。";
                _loadingScreenUi = true;
                CompanionScreenCheck.IsChecked = false;
                _loadingScreenUi = false;
                return;
            }
            App.Settings.ScreenWatchEnabled = true;
            ScreenWatch.Refresh();
            ScreenWatch.Start();
        }
        else
        {
            App.Settings.ScreenWatchEnabled = false;
            ScreenWatch.Stop();
        }
        App.Settings.Save();
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

    /// <summary>方向反转：画面里哪边算「插入」无法自动判断，默认不反转。</summary>

    private void ScreenMirrorPose_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingScreenUi) return;
        App.Settings.ScreenSyncMirrorPose = ScreenMirrorPoseCheck.IsChecked == true;
        ScheduleSave();
    }

    private void ScreenAudioRhythm_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingScreenUi) return;
        App.Settings.ScreenSyncAudioRhythm = ScreenAudioRhythmCheck.IsChecked == true;
        ScheduleSave();
    }
    private void ScreenSyncInvert_Changed(object sender, RoutedEventArgs e)
    {
        if (ScreenSyncInvertCheck is null || _loadingScreenUi) return;
        App.Settings.ScreenSyncInvertDirection = ScreenSyncInvertCheck.IsChecked == true;
        ScheduleSave();
        RefreshScreenWatch();
    }

    /// <summary>倾斜同步（近似，默认关）：2D 画面只能近似，左右较准，前后靠画面上下分量推测。</summary>
    private void ScreenSyncTilt_Changed(object sender, RoutedEventArgs e)
    {
        if (ScreenSyncTiltCheck is null || _loadingScreenUi) return;
        App.Settings.ScreenSyncTiltEnabled = ScreenSyncTiltCheck.IsChecked == true;
        ScheduleSave();
        RefreshScreenWatch();
    }

    private void UpdateScreenToggle()
    {
        // 伴随卡里那个勾跟设置走（从别处关掉同步时也要跟着回去，不然两处显示打架）。
        if (CompanionScreenCheck is not null && !_loadingScreenUi)
        {
            bool wantScreen = App.Settings.ScreenWatchEnabled;
            if (CompanionScreenCheck.IsChecked != wantScreen)
            {
                _loadingScreenUi = true;
                CompanionScreenCheck.IsChecked = wantScreen;
                _loadingScreenUi = false;
            }
        }
        bool running = ScreenWatch.IsRunning;
        if (running == _lastScreenToggleState) return;
        _lastScreenToggleState = running;
        ScreenWatchToggleBtn.Content = running ? "关闭同步" : "开启同步";
        ScreenWatchToggleBtn.Style = (Style)FindResource(running ? "BtnDanger" : "BtnSecondary");
    }

    /// <summary>「更多设置」Expander 展开/收起：只记内存字段，不写设置。</summary>
    private void MoreSettings_Expanded(object sender, RoutedEventArgs e) => _screenMoreSettingsOpen = true;
    private void MoreSettings_Collapsed(object sender, RoutedEventArgs e) => _screenMoreSettingsOpen = false;

    /// <summary>刷新画面卡：状态行 + 六个数值 + 模型状态 + 判定灯（150ms 一次，和页面其它卡片同一个节拍）。</summary>
    private void RefreshScreenWatch()
    {
        // 页面构造/ XAML 解析中途也可能被调到，控件还没齐就直接跳过
        if (ScreenStatusText is null || ScreenScoreLabel is null) return;

        SyncThresholds();        // 门槛文字 + 滑条位置 + 竖线位置，跟着设置走

        UpdateScreenToggle();   // 按钮文案跟着真实状态走（比如回到页面时自动续上观察）
        bool running = ScreenWatch.IsRunning;
        var manual = ScreenWatch.ManualBinding;
        if (ScreenManualBindBtn is not null)
        {
            ScreenManualBindBtn.IsEnabled = running;
            ScreenManualBindBtn.Content = manual.Bound ? "重新绑定…" : "手动绑定…";
        }
        if (ScreenManualAutoBtn is not null)
            ScreenManualAutoBtn.Visibility = manual.Bound ? Visibility.Visible : Visibility.Collapsed;
        if (ScreenManualBindHint is not null)
            ScreenManualBindHint.Text = manual.Bound
                ? manual.Located ? $"手动目标 · 位置 {manual.Position * 100:0}% · 匹配 {manual.Confidence * 100:0}%" : manual.Status
                : "自动识别；识别不准时可手动绑定一个目标。";
        if (ScreenManualPoint is not null)
        {
            ScreenManualPoint.Visibility = running && manual.Located ? Visibility.Visible : Visibility.Collapsed;
            ScreenManualPoint.Margin = new Thickness(Math.Clamp(manual.X * 319 - 7, 0, 306),
                Math.Clamp(manual.Y * 179 - 7, 0, 166), 0, 0);
        }
        if (manual.Bound && ScreenManualRoiRect is not null) ScreenManualRoiRect.Visibility = Visibility.Collapsed;
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

        // 预览区：把服务最近一帧抓到的画面直接摆给用户看（没开 / 还没抓到就清空）。
        if (ScreenPreviewImage is not null || ScreenPreviewLabel is not null)
        {
            var preview = running ? (manual.Bound ? ScreenWatch.ManualPreview?.Image : ScreenWatch.PreviewImage) : null;
            bool hasPreview = preview is not null;
            if (ScreenPreviewOverlay is not null)
                ScreenPreviewOverlay.Visibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
            if (ScreenPreviewImage is not null) ScreenPreviewImage.Source = preview;
            if (ScreenPreviewLabel is not null)
            {
                if (!running) ScreenPreviewLabel.Text = "还没开始抓画面";
                else if (preview is null) ScreenPreviewLabel.Text = "还没抓到第一帧…";
                else
                {
                    string target = ScreenWatch.PreviewTarget;
                    string targetName = target.Length > 0 ? target : "整个屏幕";
                    string rect = ScreenWatch.PreviewRect;
                    if (manual.Bound)
                        ScreenPreviewLabel.Text = $"看的是：{targetName} · {manual.Status} · L0={ScreenWatch.FollowMainAxis:0}%";
                    else if (ScreenWatch.FollowActive)
                    {
                        double approach = ScreenWatch.FollowApproach;
                        string dir = approach > 0.15 ? "往上" : approach < -0.15 ? "往下" : "没动";
                        ScreenPreviewLabel.Text =
                            $"看的是：{targetName} · 方向 {dir} · 深度 {ScreenWatch.FollowDepth * 100:0}% · 每秒 {ScreenWatch.FollowHz:0.0} 下 · L0={ScreenWatch.FollowMainAxis:0}%";
                    }
                    else
                    {
                        ScreenPreviewLabel.Text = $"看的是：{targetName}{(rect.Length > 0 ? $" · {rect}" : "")} · 还没开始跟画面";
                    }
                }
            }

            // ROI 矩形 + 主轴横条：跟预览同频更新（预览每 4 帧由服务刷新一次，这里 150ms 一次不会更频繁）。
            if (preview is { } p)
            {
                if (ScreenPreviewRoi is not null)
                {
                    var roi = manual.Bound ? null : ScreenWatch.PreviewRoi;
                    if (roi is { } r && p.PixelWidth > 0 && p.PixelHeight > 0)
                    {
                        double sx = 320.0 / p.PixelWidth;
                        double sy = 180.0 / p.PixelHeight;
                        ScreenPreviewRoi.Width = Math.Max(2, r.Width * sx);
                        ScreenPreviewRoi.Height = Math.Max(2, r.Height * sy);
                        ScreenPreviewRoi.Margin = new Thickness(r.X * sx, r.Y * sy, 0, 0);
                        ScreenPreviewRoi.Visibility = Visibility.Visible;
                    }
                    else ScreenPreviewRoi.Visibility = Visibility.Collapsed;
                }
                if (ScreenPreviewBarGrid is not null && ScreenPreviewMainFill is not null)
                {
                    bool following = ScreenWatch.FollowActive;
                    ScreenPreviewBarGrid.Visibility = following ? Visibility.Visible : Visibility.Collapsed;
                    if (following)
                        ScreenPreviewMainFill.Width = Math.Clamp(ScreenWatch.FollowMainAxis, 0, 100) / 100.0 * ScreenPreviewBarGrid.Width;
                }
            }
            else
            {
                if (ScreenPreviewRoi is not null) ScreenPreviewRoi.Visibility = Visibility.Collapsed;
                if (ScreenPreviewBarGrid is not null) ScreenPreviewBarGrid.Visibility = Visibility.Collapsed;
            }
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

    private Point _previewDragFrom;
    private bool _previewDragging;

    private void ScreenPreview_Down(object sender, MouseButtonEventArgs e)
    {
        if (ScreenWatch.ManualBinding.Bound) return;
        if (ScreenPreviewOverlay is null || ScreenManualRoiRect is null) return;
        _previewDragFrom = e.GetPosition(ScreenPreviewOverlay);
        _previewDragging = true;
        DrawManualRect(_previewDragFrom, _previewDragFrom);
        ScreenManualRoiRect.Visibility = Visibility.Visible;
        ScreenPreviewOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void ScreenPreview_Move(object sender, MouseEventArgs e)
    {
        if (!_previewDragging || ScreenPreviewOverlay is null) return;
        DrawManualRect(_previewDragFrom, e.GetPosition(ScreenPreviewOverlay));
    }

    private void ScreenPreview_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_previewDragging || ScreenPreviewOverlay is null) return;
        _previewDragging = false;
        ScreenPreviewOverlay.ReleaseMouseCapture();

        Point to = e.GetPosition(ScreenPreviewOverlay);
        double w = ScreenPreviewOverlay.ActualWidth > 1 ? ScreenPreviewOverlay.ActualWidth : 320;
        double h = ScreenPreviewOverlay.ActualHeight > 1 ? ScreenPreviewOverlay.ActualHeight : 180;
        double x0 = Math.Clamp(Math.Min(_previewDragFrom.X, to.X) / w, 0, 1);
        double y0 = Math.Clamp(Math.Min(_previewDragFrom.Y, to.Y) / h, 0, 1);
        double rw = Math.Clamp(Math.Abs(to.X - _previewDragFrom.X) / w, 0, 1 - x0);
        double rh = Math.Clamp(Math.Abs(to.Y - _previewDragFrom.Y) / h, 0, 1 - y0);
        if (rw < 0.05 || rh < 0.05)
        {
            // 只是点了一下（没拖开）：当没框过，别把分析区缩成一条缝。
            UpdateManualRoiUi();
            return;
        }
        App.Settings.ScreenManualRoi = FormattableString.Invariant($"{x0:0.000},{y0:0.000},{rw:0.000},{rh:0.000}");
        App.Settings.ScreenUseManualRoi = true;
        ScheduleSave();
        UpdateManualRoiUi();
    }

    private void ScreenManualBind_Click(object sender, RoutedEventArgs e)
    {
        var service = ScreenWatch;
        service.BeginManualBinding();
        try
        {
            var dialog = new ManualBindingWindow(service);
            var owner = Window.GetWindow(this);
            if (owner is not null) dialog.Owner = owner;
            dialog.ShowDialog();
        }
        finally { service.EndManualBinding(); RefreshScreenWatch(); }
    }

    private void ScreenManualAuto_Click(object sender, RoutedEventArgs e)
    {
        ScreenWatch.ClearManualBinding();
        RefreshScreenWatch();
    }

    private void ScreenManualRoiClear_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.ScreenManualRoi = "";
        App.Settings.ScreenUseManualRoi = false;
        ScheduleSave();
        UpdateManualRoiUi();
    }

    /// <summary>把两个屏幕坐标画成预览上的框（左上 + 宽高，单位是预览格子的像素）。</summary>
    private void DrawManualRect(Point a, Point b)
    {
        if (ScreenManualRoiRect is null || ScreenPreviewOverlay is null) return;
        double w = ScreenPreviewOverlay.ActualWidth > 1 ? ScreenPreviewOverlay.ActualWidth : 320;
        double h = ScreenPreviewOverlay.ActualHeight > 1 ? ScreenPreviewOverlay.ActualHeight : 180;
        double left = Math.Clamp(Math.Min(a.X, b.X), 0, w);
        double top = Math.Clamp(Math.Min(a.Y, b.Y), 0, h);
        ScreenManualRoiRect.Margin = new Thickness(left, top, 0, 0);
        ScreenManualRoiRect.Width = Math.Clamp(Math.Abs(b.X - a.X), 0, w - left);
        ScreenManualRoiRect.Height = Math.Clamp(Math.Abs(b.Y - a.Y), 0, h - top);
    }

    /// <summary>手动框的界面：没框过 → 提示怎么框；框过 → 画出框 + 给「清除手动框」。</summary>
    private void UpdateManualRoiUi()
    {
        if (ScreenManualRoiRect is null || ScreenManualRoiHint is null || ScreenManualRoiClearBtn is null) return;
        bool parsed = TryParseRoiText(App.Settings.ScreenManualRoi,
            out double x, out double y, out double w, out double h);
        bool on = App.Settings.ScreenUseManualRoi && parsed;
        ScreenManualRoiClearBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ScreenManualRoiHint.Text = on
            ? "手动框选：只跟框里这块（按住预览拖个新框可以换，拖框会覆盖自动识别）"
            : "按住预览拖一个框 = 只跟这块（优先于自动框）";
        if (!on)
        {
            ScreenManualRoiRect.Visibility = Visibility.Collapsed;
            return;
        }
        double cw = ScreenPreviewOverlay.ActualWidth > 1 ? ScreenPreviewOverlay.ActualWidth : 320;
        double ch = ScreenPreviewOverlay.ActualHeight > 1 ? ScreenPreviewOverlay.ActualHeight : 180;
        ScreenManualRoiRect.Margin = new Thickness(x * cw, y * ch, 0, 0);
        ScreenManualRoiRect.Width = Math.Max(2, w * cw);
        ScreenManualRoiRect.Height = Math.Max(2, h * ch);
        ScreenManualRoiRect.Visibility = Visibility.Visible;
    }

    /// <summary>解析设置里存的 "x,y,w,h"（0–1 归一化）；坏了就返回 false。</summary>
    private static bool TryParseRoiText(string? text, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Split(',');
        if (parts.Length != 4) return false;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v[i]))
                return false;
        }
        x = v[0]; y = v[1]; w = v[2]; h = v[3];
        return w > 0 && h > 0;
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
                + "要让这条信号生效，上面的「开启同步」必须是开着的，而且绑定的程序得在前台。";
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
    /// <summary>
    /// 「跟着当前窗口自动动」——一键把这条链路配齐：画面同步开着 + 规则引擎开着 + 动作来源 = 画面内容
    /// （缺一条设备就不动，见 RuleEngine.ScreenWatchIfNeeded / ScreenSourceAllowed）；姿态模型装了就把姿态也开上。
    /// 目标窗口和绑定程序由 ScreenWatchService.TryAutoBindForeground 每半秒跟前台窗口自动改 ——
    /// 「明明开着却不动」最常见的原因就是窗口没挑对、或绑定程序跟窗口对不上，这一步专门把它消掉。
    /// 关掉时只停这个模式，其它开关保持原样（不偷偷改用户的设置）。
    /// </summary>
    private void ScreenFollowForeground_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        ApplyScreenFollowForeground(ScreenFollowForegroundCheck.IsChecked == true);
    }

    private void ApplyScreenFollowForeground(bool on)
    {
        App.Settings.ScreenFollowOnlyWhenForeground = on;
        try { App.Settings.Save(); } catch (Exception ex) { AppLogger.Warn($"画面同步：记下「只在前台时动」失败 —— {ex.Message}"); }
    }

    private void ApplyAutoFollowScreen(bool on)
    {
        App.Settings.AutoFollowScreen = on;
        if (on)
        {
            // 这里只负责「跟着前台窗口挑目标」，不动「游戏伴随」：
            // 以前顺手把 RuleEngineEnabled 打开，用户只是想跟窗口，结果伴随被一起点开了。
            App.Settings.ScreenWatchEnabled = true;
            App.Settings.CompanionSource = "auto";   // 画面这条不再独占来源（它自己有开关），声音照样能用
            if (Hexa.Services.PoseTracker.IsInstalled) App.Settings.PoseVisionEnabled = true;
        }
        App.Settings.Save();
        if (on)
        {
            ScreenWatch.Refresh();      // 先把窗口列表对一遍，再开
            ScreenWatch.Start();
        }
        LoadScreenWatchUi();
        RefreshGameProfileText();
        if (on && !App.Settings.RuleEngineEnabled && ScreenOneKeyHint is not null)
            ScreenOneKeyHint.Text = "已经跟着当前窗口换目标了。不过「游戏伴随」还关着，设备不会自己动——"
                + "要让画面真的带动设备，先点上面那个一键，或者在「游戏伴随」那里打开。";
    }

    private void UpdatePoseRow()
    {
        if (ScreenPoseStatusText is null) return;

        string poseMode = !App.Settings.PoseVisionEnabled ? " · 开关没开，不会用"
            : ScreenWatch.PoseModelInstalled ? " · 已启用" : " · 开了但没模型，先按整幅画面算";
        ScreenPoseStatusText.Text = ScreenWatch.PoseModelStatus + poseMode;
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
            AppLogger.Warn($"画面同步：下载姿态模型时出错 —— {ex.Message}");
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
            ? $"已换成{ScreenWatch.ModelTierName}：本机已经有这一档的模型，开启同步就直接用它。"
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
            AppLogger.Warn($"画面同步：下载识别模型时出错 —— {ex.Message}");
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
                + "它现在是主判据（模型分 70% + 手工特征 30%）。开启同步后约 1 秒内就能看到数。";
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
            line1 = "还没绑定游戏 —— 先在「目标游戏」下拉里选一个窗口，不选的话设备永远不动";
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
                : "动作来源：画面内容 —— 画面里有事发生时才动（要走画面，先去「测试台 · 画面同步」开启同步并挑好窗口）";
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
        if (target.Length == 0) line3 = "现在不动：还没绑定游戏（先在「目标游戏」下拉里选一个窗口）";
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
        RefreshCompanionRoute();
    }

    private void UpdateAudioToggle()
    {
        if (CompanionSoundCheck is not null && !_loadingUi)
        {
            bool wantSound = App.Settings.AudioReactiveEnabled;
            if (CompanionSoundCheck.IsChecked != wantSound)
            {
                _loadingUi = true;
                CompanionSoundCheck.IsChecked = wantSound;
                _loadingUi = false;
            }
        }
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
        // P0：先关闭自动跟随/操作联动。否则用户解锁后切回目标游戏，ForegroundWatcher.Tick
        // 看到 CompanionAuto=true 会再次 StartAuto，设备重新运动。
        _loadingUi = true;
        try
        {
            AutoPresetCheck.IsChecked = false;   // 同上：它也会让设备在进游戏时自己动起来
        }
        finally
        {
            _loadingUi = false;
        }
        App.Settings.CompanionAuto = false;   // 伴随关着时的自动套预设也停
        App.Settings.AmbientOverlay = false;   // 氛围叠加是全局的，全停也一起关（开关搬到了「游玩」页）
        SaveAutoFollow();

        App.Settings.RuleEngineEnabled = false;
        App.Settings.AudioReactiveEnabled = false;
        App.Settings.GameBridgeEnabled = false;
        // 画面同步也一起关：它可能正在驱动设备（来源选「画面内容」时），Stop 里会缓降回中并交还控制权；
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

        if (ScreenOneKeyHint is not null)
            ScreenOneKeyHint.Text = "已全部停止：伴随、画面同步、跟随前台窗口、声音响应、游戏桥、氛围微动都关了，切回游戏也不会自己启动。解锁后要继续，重新点「开启游戏伴随」。";
    }


    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
