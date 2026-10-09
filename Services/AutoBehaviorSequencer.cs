using Hexa.Models;

namespace Hexa.Services;

public sealed record AutoBehaviorFrame(
    double[] Axes,
    string Pattern,
    string PatternLabel,
    double Bpm,
    double Transition);

/// <summary>
/// 自由游玩（自动模式）可调参数快照。值类型：渲染线程每帧读取一份，无需加锁。
/// 这里的范围常量同时是 UI 滑块与 AppSettings.Normalize 的唯一来源。
/// </summary>
public readonly record struct AutoPlayParameters(
    double BpmMin,
    double BpmMax,
    double PatternMinSeconds,
    double PatternMaxSeconds,
    double TransitionSeconds,
    double Acceleration,
    bool ContinuousBpm)
{
    public const double BpmLimitMin = 20;
    public const double BpmLimitMax = 200;
    public const double PatternLimitMin = 3;
    public const double PatternLimitMax = 120;
    public const double TransitionLimitMin = 0.2;
    public const double TransitionLimitMax = 3;
    public const double AccelerationLimitMin = 0;
    public const double AccelerationLimitMax = 60;

    /// <summary>默认值：BPM 45–110、动作 8–24 秒、过渡 0.9 秒、加速度 8 BPM/s、连续变速。</summary>
    public static AutoPlayParameters Default => new(45, 110, 8, 24, 0.9, 8, true);

    /// <summary>从设置读取并夹紧：非法值回退默认，min &gt; max 时交换。</summary>
    public static AutoPlayParameters FromSettings(AppSettings? settings)
    {
        if (settings is null) return Default;
        double bpmMin = ClampOr(settings.AutoBpmMin, 45, BpmLimitMin, BpmLimitMax);
        double bpmMax = ClampOr(settings.AutoBpmMax, 110, BpmLimitMin, BpmLimitMax);
        if (bpmMin > bpmMax) (bpmMin, bpmMax) = (bpmMax, bpmMin);
        double patternMin = ClampOr(settings.AutoPatternMinSeconds, 8, PatternLimitMin, PatternLimitMax);
        double patternMax = ClampOr(settings.AutoPatternMaxSeconds, 24, PatternLimitMin, PatternLimitMax);
        if (patternMin > patternMax) (patternMin, patternMax) = (patternMax, patternMin);
        return new AutoPlayParameters(
            bpmMin,
            bpmMax,
            patternMin,
            patternMax,
            ClampOr(settings.AutoTransitionSeconds, 0.9, TransitionLimitMin, TransitionLimitMax),
            ClampOr(settings.AutoAcceleration, 8, AccelerationLimitMin, AccelerationLimitMax),
            settings.AutoContinuousBpm);
    }

    private static double ClampOr(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// 自由游玩可选动作（Id + 中文标签 + 是否自建）。
/// 公开类型，供「参与随机的动作」面板与「波形」下拉<b>枚举同一份列表</b> ——
/// 这一份列表就是"同一个页面两套动作口径"的修复点。
/// </summary>
/// <param name="IsCustom">
/// true = 这一项来自动作页的动作库（用户自建 / 改过的 <see cref="StrokePreset"/>），不是内置波形。
/// 界面据此打「自建」标记，取样池据此决定"要不要把它算进默认池"。
/// </param>
public sealed record AutoPlayPattern(string Id, string Label, bool IsCustom = false);

/// <summary>Ayva-inspired behavior selection with continuous tempo drift and crossfades.</summary>
public sealed class AutoBehaviorSequencer
{
    /// <summary>
    /// 自建动作在池子里的 Id 前缀。为什么要加前缀：用户完全可以把自建动作起名成 "gentle_wave"，
    /// 直接混用 Id 会与内置波形撞车（同一个 Id 两种语义）；加前缀后两套命名空间永不重叠，
    /// 而且"这是个自建动作"这件事本身就写在字符串里（日志与设置文件里一眼可辨）。
    /// </summary>
    public const string CustomStrokePrefix = "stroke:";

    /// <summary>
    /// 内置波形池 —— <b>顺序即界面顺序</b>。
    /// 「递进爆发」以前只能从「波形」下拉选到（快速预设「偷袭一下」用的就是它），却不在随机池里，
    /// 这正是两处口径不一致的另一半原因；现在它是池子的正式成员，于是下拉与勾选列表同源同内容。
    /// </summary>
    private static readonly (string Id, string Label)[] FreePlayPatterns =
    [
        ("gentle_wave", "温柔波浪"),
        ("organic_flow", "自然流动"),
        ("game_flow", "游戏沉浸"),
        ("deep_pulse", "深层脉冲"),
        ("spiral_tease", "螺旋游走"),
        ("edge_swirl", "渐进漩涡"),
        ("random_micro", "细微变化"),
        ("intense_thrust", "动感往复"),
        ("climax_burst", "递进爆发"),
    ];

    /// <summary>
    /// 全部可选动作 = 内置波形 + 当前自建动作，顺序与取样池一致，供 UI 列出下拉项与勾选项。
    /// 每次读取都重新拼：动作库里新建 / 删除 / 改名一个动作，两处列表立刻跟上，不需要重启，
    /// 也不需要谁去"刷新一份全局缓存"。
    /// </summary>
    public static IReadOnlyList<AutoPlayPattern> AllPatterns
    {
        get
        {
            var list = new List<AutoPlayPattern>(FreePlayPatterns.Length + 4);
            foreach (var (id, label) in FreePlayPatterns)
                list.Add(new AutoPlayPattern(id, label));
            foreach (var stroke in CustomStrokes())
                list.Add(new AutoPlayPattern(CustomStrokePrefix + stroke.Id, stroke.Label, IsCustom: true));
            return list;
        }
    }

    /// <summary>
    /// 全部合法动作 Id，供 AppSettings.Normalize 校验配置里的 Id
    /// （「参与随机的动作」勾选列表就存在那里）。
    /// <b>必须是动态属性</b>：自建动作的 Id 也要算合法，否则用户勾过的自建动作会在下次启动
    /// 被当成"未知 Id"清掉 —— 那就等于又回到了"自建动作永远进不了自动模式"。
    /// </summary>
    public static IReadOnlyList<string> AllPatternIds =>
        AllPatterns.Select(pattern => pattern.Id).ToArray();

    /// <summary>
    /// 池子项 → 收藏 / 最近列表里的键：自建动作去掉 <see cref="CustomStrokePrefix"/> 前缀，
    /// 就是动作库里的动作 id（收藏、最近用过、动作页筛选三处用的是同一个 id）。
    /// </summary>
    public static string UsageKey(AutoPlayPattern item) =>
        item.IsCustom && item.Id.Length > CustomStrokePrefix.Length
            ? item.Id[CustomStrokePrefix.Length..]
            : item.Id;

    /// <summary>
    /// 全局参数源（由 AppSettings 构造时安装）。序列器位于 Services 层、拿不到 UI/设置引用，
    /// 因此每次 Step 通过该提供器读取最新快照：面板改动即时生效，且无需改动 Step 签名。
    /// </summary>
    private static volatile Func<AutoPlayParameters>? _parametersProvider;

    public static void SetParametersProvider(Func<AutoPlayParameters>? provider) =>
        _parametersProvider = provider;

    /// <summary>
    /// 「参与随机的动作」Id 源（由 AppSettings 构造时安装）。空集合 = 全部启用（向后兼容）。
    /// 与参数提供器分离：勾选只在换动作/Reset 时读取，不进每帧热路径。
    /// </summary>
    private static volatile Func<IReadOnlyList<string>?>? _enabledPatternsProvider;

    public static void SetEnabledPatternsProvider(Func<IReadOnlyList<string>?>? provider) =>
        _enabledPatternsProvider = provider;

    /// <summary>
    /// 取一份启用动作 Id 快照：去空白、转小写、去重。UI 线程写入与音频线程读取可能并发，
    /// 这里复制一份数组并兜底异常，保证取样永不因集合被改写而抛错。
    /// </summary>
    private static string[] SnapshotEnabledPatterns()
    {
        Func<IReadOnlyList<string>?>? source = _enabledPatternsProvider;
        if (source is null) return Array.Empty<string>();
        try
        {
            IReadOnlyList<string>? ids = source();
            if (ids is null || ids.Count == 0) return Array.Empty<string>();
            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            // 集合被并发改写时退回「全部启用」，避免打断自动模式
            return Array.Empty<string>();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  自建动作（动作页动作库）→ 随机池
    //  这是本次最大的修复：以前随机池只认上面那 8 个硬编码 pattern，
    //  用户在动作页新建 / 改出来的 StrokePreset 根本抽不到。
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 自建动作来源（由动作页的 StrokesViewModel 安装）：返回<b>当前动作库快照</b>。
    /// 与参数提供器同一套路数 —— 序列器在 Services 层拿不到 ViewModel，只能通过提供器读；
    /// 动作页新建 / 删除 / 改名后池子立刻跟上，不必重启。
    /// </summary>
    private static volatile Func<IReadOnlyList<StrokePreset>?>? _customStrokesProvider;

    public static void SetCustomStrokesProvider(Func<IReadOnlyList<StrokePreset>?>? provider) =>
        _customStrokesProvider = provider;

    /// <summary>
    /// 收藏动作 Id 来源（存的是动作库里的 id，不带前缀）。收藏过的动作在取样时权重 ×2：
    /// 用户既然点了星，就说明"想常看到它"。
    /// </summary>
    private static volatile Func<IReadOnlyList<string>?>? _favoriteIdsProvider;

    public static void SetFavoriteIdsProvider(Func<IReadOnlyList<string>?>? provider) =>
        _favoriteIdsProvider = provider;

    /// <summary>「最近用过」动作 Id 来源（越靠前越新）。近期用过的适当降权：刚看过的先歇一会儿。</summary>
    private static volatile Func<IReadOnlyList<string>?>? _recentIdsProvider;

    public static void SetRecentIdsProvider(Func<IReadOnlyList<string>?>? provider) =>
        _recentIdsProvider = provider;

    /// <summary>
    /// 「刚用过这个自建动作」回调（动作页据此写「最近用过」）。
    /// 序列器在音频线程抽到自建动作时调用，回调实现方负责切回 UI 线程。
    /// </summary>
    private static volatile Action<string>? _usageReporter;

    public static void SetUsageReporter(Action<string>? reporter) =>
        _usageReporter = reporter;

    /// <summary>
    /// 自建动作的<b>冻结快照</b>。为什么不在每帧合成时直接读 <see cref="StrokePreset"/>：
    /// <c>Compose</c> 在音频线程每帧都跑，而 <c>StrokePreset.AllAxes</c> 每次访问都会新建 6 个数组 ——
    /// 每帧几十次小分配纯属给 GC 找活干。抽中它时（换动作，几十秒一次）做一次，之后只读。
    /// </summary>
    private sealed record CustomStroke(string Id, string Label, double[][] Axes, StrokeMotion[] Motions, double MaxRange);

    /// <summary>六轴行程都小于这个值的自建动作不进随机池：抽到它等于让设备停住十几秒。</summary>
    private const double MinCustomStrokeRange = 0.02;

    /// <summary>
    /// 「轻柔」档不抽的自建动作行程阈值。舒适档的本意就是"别太猛"，
    /// 内置的动感往复（最猛那一档）本来就被挡；用户自建的动作完全可能更极端，所以按行程给它同一道闸。
    /// 只影响「轻柔」档，别的档位不受影响。
    /// </summary>
    private const double GentleMaxCustomRange = 0.6;

    /// <summary>
    /// 没有安装提供器时的兜底：直接读 presets.json（<see cref="StrokePresetStore"/> 与动作页读写同一个文件）。
    /// 为什么需要它：AppSettings.Normalize 在启动早期就会问 <see cref="AllPatternIds"/>
    /// 来校验「参与随机的动作」，那时动作页的 ViewModel 还没构造 —— 只认提供器的话，
    /// 用户勾过的自建动作会在启动瞬间被判成"未知 Id"清掉。读一次、缓存住；
    /// 提供器一旦装上就以它为准（那边永远是最新的内存快照）。
    /// </summary>
    private static volatile CustomStroke[]? _fileStrokesCache;

    /// <summary>当前可参与随机的自建动作（动作库里 <c>IsCustom</c> 的那批，已过滤无效项）。</summary>
    private static CustomStroke[] CustomStrokes()
    {
        Func<IReadOnlyList<StrokePreset>?>? source = _customStrokesProvider;
        if (source is null)
        {
            var cached = _fileStrokesCache;
            if (cached is null)
            {
                try { cached = BuildCustomStrokes(StrokePresetStore.LoadAll()); }
                catch (Exception) { cached = []; }
                _fileStrokesCache = cached;
            }
            return cached;
        }

        try { return BuildCustomStrokes(source()); }
        catch (Exception)
        {
            // 动作库被并发替换（copy-on-write）时退回"没有自建动作"，绝不打断自动模式
            return [];
        }
    }

    /// <summary>把动作库快照筛成池子项：只留自建、去重（忽略大小写）、剔除六轴都不动的。</summary>
    private static CustomStroke[] BuildCustomStrokes(IReadOnlyList<StrokePreset>? presets)
    {
        if (presets is null || presets.Count == 0) return [];

        var result = new List<CustomStroke>(presets.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            if (preset is null || !preset.IsCustom) continue;
            string id = (preset.Id ?? "").Trim().ToLowerInvariant();
            if (id.Length == 0 || !seen.Add(id)) continue;

            double[][] axes = preset.AllAxes;   // 一次性取出（该属性每次都会新建 6 个数组）
            var motions = new StrokeMotion[6];
            double maxRange = 0;
            for (int i = 0; i < 6; i++)
            {
                var axis = axes[i];
                if (axis is not { Length: >= 4 }) axis = axes[i] = [0.5, 0.5, 0, 0, 0, 0];
                maxRange = Math.Max(maxRange, Math.Abs(axis[1] - axis[0]));
                motions[i] = preset.MotionOf(i);
            }
            if (maxRange < MinCustomStrokeRange) continue;

            string label = string.IsNullOrWhiteSpace(preset.Label) ? id : preset.Label.Trim();
            result.Add(new CustomStroke(id, label, axes, motions, Math.Clamp(maxRange, 0, 1)));
        }
        return result.ToArray();
    }

    /// <summary>按 Id 找一份自建动作快照（找不到 = 动作被删了 → 退回内置波形的处理方式）。</summary>
    private static CustomStroke? FindCustomStroke(string id)
    {
        if (!id.StartsWith(CustomStrokePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        string presetId = id[CustomStrokePrefix.Length..];
        foreach (var stroke in CustomStrokes())
            if (string.Equals(stroke.Id, presetId, StringComparison.OrdinalIgnoreCase)) return stroke;
        return null;
    }

    /// <summary>取一份收藏 Id 快照（小写、去空白、去重）。与启用列表快照同一套并发兜底。</summary>
    private static string[] SnapshotIds(Func<IReadOnlyList<string>?>? provider)
    {
        if (provider is null) return [];
        try
        {
            IReadOnlyList<string>? ids = provider();
            if (ids is null || ids.Count == 0) return [];
            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            // 集合被并发改写时当作"没有收藏 / 没有最近"，只影响权重，不影响能不能跑
            return [];
        }
    }

    /// <summary>
    /// 把一个池子项记进「最近用过」（只有自建动作有这条记录：内置波形不在动作库里，
    /// 动作页的「🕘 最近用过」筛选也筛不到它）。回调异常一律吞掉，绝不能影响自动模式。
    /// </summary>
    private static void ReportUsage(string poolId)
    {
        Action<string>? reporter = _usageReporter;
        if (reporter is null) return;
        if (!poolId.StartsWith(CustomStrokePrefix, StringComparison.OrdinalIgnoreCase)) return;
        string presetId = poolId[CustomStrokePrefix.Length..];
        if (presetId.Length == 0) return;
        try { reporter(presetId); }
        catch (Exception) { /* 「最近用过」只是便利功能，失败不该打断自动动作 */ }
    }

    private readonly Random _random;

    /// <summary>
    /// 副轴微差/噪声的种子：给了 seed（自检、单测）就用它，正常运行取「启动时刻」派生的一个整数。
    /// 于是每次运行听起来都不一样（不会每次都一样地「随机」），而单次运行内部完全可复现
    /// （日志里记着这个种子，出问题能按同一条轨迹复算）。
    /// </summary>
    private readonly int _noiseSeed;

    /// <summary>副轴一阶滞后：「主轴先走、副轴跟着走」，而不是六根轴一起到两端（见跟随器的注释）。</summary>
    private readonly Osr6MotionComposer.SecondaryAxisFollower _follow = new();

    /// <summary>最近抽到过的动作（新 → 旧）。用来避免 ABAB 式来回重复。</summary>
    private readonly List<string> _recent = new(RecentMemory);

    /// <summary>「最近 N 个动作不重复」里的 N。</summary>
    private const int RecentMemory = 3;

    /// <summary>最近记忆里的动作再被抽到的权重折扣（1 = 不折扣，0 = 完全排除）。</summary>
    private const double RecentPenalty = 0.08;

    /// <summary>
    /// 收藏过的动作权重倍数：用户点过星 = 明确表达"想常看到它"，抽到的机会翻倍。
    /// </summary>
    private const double FavoriteBoost = 2.0;

    /// <summary>
    /// 近期（跨启动的「最近用过」12 个）用过、但不在最近 3 个记忆里的动作权重折扣 ——
    /// 比 <see cref="RecentPenalty"/> 温和得多：刚玩过的先歇一会儿，不是几乎排除。
    /// </summary>
    private const double RecentUsedPenalty = 0.5;

    /// <summary>
    /// 自建动作的基准频率（Hz）：动作页播放它就是「1 圈/秒 × 速度」，自动模式沿用同一把尺子，
    /// 于是"自动抽到它"和"在动作页播放它"是同一个动作、同一个速率含义。
    /// 物理频率 = 基准频率 × _tempo（_tempo 由目标 BPM 反解），所以面板上的 BPM 就是真实来回次数。
    /// </summary>
    private const double CustomStrokeBaseHz = 1.0;

    /// <summary>过渡窗口末尾留出的尾巴（秒）：比它还短就不再走频率斜坡，交给常规调速逻辑收尾。</summary>
    private const double TransitionSlewTailSeconds = 0.05;

    private string _selection = "free_play";
    private string _current = "organic_flow";
    private string _previous = "organic_flow";
    /// <summary>当前 / 上一个动作若是自建动作，这里放它的冻结快照（null = 内置波形）。</summary>
    private CustomStroke? _currentStroke;
    private CustomStroke? _previousStroke;
    /// <summary>当前动作的显示名，换动作时解析一次 —— Step 每帧都要报它，不能每帧去翻列表。</summary>
    private string _currentLabel = "自然流动";
    private double _phaseTime;                    // 相位（单位「圈」= 物理频率的积分，跨动作共享）
    private double _slowSeconds;                  // 真实时间（秒）：只给慢漂移与噪声用，不受 tempo 影响
    private double _behaviorElapsed;
    private double _behaviorDuration = 12;
    private double _transitionElapsed = 1;
    private double _transitionDuration = 0.9;
    private double _tempo = 1;
    private double _tempoTargetBpm = 60;          // 目标 BPM（不含音频调制）
    private double _tempoTargetElapsed;
    private double _baseSpeed = 1.0;              // 最近一次 Reset/Step 的速度倍率（供 Next 等入口使用）
    private AutoPlayParameters _lastParameters = AutoPlayParameters.Default;
    private bool _holdCurrent;
    private double _temporaryRemaining;

    public AutoBehaviorSequencer(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        _noiseSeed = seed ?? Environment.TickCount;
        AppLogger.Info($"[Auto] 动作噪声种子 = {_noiseSeed}（副轴微差与「细微变化」都由它派生，同一次运行内可复现）");
    }

    private static AutoPlayParameters Parameters =>
        _parametersProvider?.Invoke() ?? AutoPlayParameters.Default;

    public void Reset(string selection, double baseSpeed, ComfortProfile profile)
    {
        AutoPlayParameters parameters = Parameters;
        _selection = NormalizeSelection(selection);
        _current = _selection == "free_play" ? PickNext(except: null, profile) : _selection;
        _previous = _current;
        _currentStroke = FindCustomStroke(_current);   // 抽中的若自建动作，冻结一份参数域快照（每帧只读，不分配）
        _previousStroke = _currentStroke;
        _currentLabel = LabelFor(_current);
        _phaseTime = 0;
        _slowSeconds = 0;
        _behaviorElapsed = 0;
        _behaviorDuration = NextDuration(parameters);
        _transitionDuration = TransitionDuration(parameters);
        _transitionElapsed = _transitionDuration;
        _baseSpeed = double.IsFinite(baseSpeed) ? Math.Clamp(baseSpeed, 0.1, 3.0) : 1.0;
        _tempoTargetBpm = PickTargetBpm(parameters, _baseSpeed);
        _tempo = Math.Clamp(_tempoTargetBpm / BaseBpm(_current), 0.05, 12.0);
        _tempoTargetElapsed = 0;
        _lastParameters = parameters;
        _holdCurrent = false;
        _temporaryRemaining = 0;
        // 跟随状态从中位起步：自动动作刚开的时候，副轴也该是「被带起来」而不是一上来就满幅。
        _follow.Reset();
        RememberPattern(_current);
        ReportUsage(_current);      // 自建动作一开跑就记进「最近用过」
    }

    public AutoBehaviorFrame Step(
        double deltaSeconds,
        double baseSpeed,
        IReadOnlyList<double> amplitudes,
        double intensity,
        double arousal,
        ComfortProfile profile,
        double audioEnergy,
        double audioBass,
        double beatPulse)
    {
        double dt = Math.Clamp(deltaSeconds, 0.005, 0.1);
        _baseSpeed = double.IsFinite(baseSpeed) ? Math.Clamp(baseSpeed, 0.1, 3.0) : 1.0;
        AutoPlayParameters parameters = Parameters;
        // BPM 范围被改动时立即生效：连续模式下一拍重取目标，非连续模式直接换目标（仍受平滑限制）
        if (parameters.BpmMin != _lastParameters.BpmMin || parameters.BpmMax != _lastParameters.BpmMax)
        {
            if (parameters.ContinuousBpm) _tempoTargetElapsed = double.PositiveInfinity;
            else _tempoTargetBpm = PickTargetBpm(parameters, _baseSpeed);
        }
        _lastParameters = parameters;

        string normalizedSelection = NormalizeSelection(_selection);
        if (normalizedSelection != _selection) _selection = normalizedSelection;

        _behaviorElapsed += dt;
        _transitionElapsed += dt;
        _tempoTargetElapsed += dt;
        _slowSeconds += dt;          // 真实时间：慢漂移与噪声用它，不受 tempo 影响（快段落不该让「慢漂移」也变快）
        if (_temporaryRemaining > 0)
        {
            _temporaryRemaining = Math.Max(0, _temporaryRemaining - dt);
            if (_temporaryRemaining == 0 && _selection == "free_play")
                BeginTransition(PickNext(_current, profile), profile);
        }

        if (_temporaryRemaining <= 0 && !_holdCurrent
            && _selection == "free_play" && _behaviorElapsed >= _behaviorDuration)
            BeginTransition(PickNext(_current, profile), profile);
        else if (_temporaryRemaining <= 0 && _selection != "free_play" && _current != _selection)
            BeginTransition(_selection, profile);

        double baseBpm = BaseBpm(_current);
        double driftInterval = 3.5 + (1 - profile.Variation) * 5.0;
        if (parameters.ContinuousBpm && _tempoTargetElapsed >= driftInterval)
        {
            _tempoTargetElapsed = 0;
            _tempoTargetBpm = PickTargetBpm(parameters, _baseSpeed);
        }

        // 目标 BPM → 速度倍率。
        //
        // 过渡窗口内走「参数域频率斜坡」：按<b>剩余过渡时间</b>反解这一步最多能走多少 BPM，
        // 于是物理频率以恒定速率线性滑向目标，并且正好在过渡结束的那一刻到达
        //（剩余时间越少、允许的步长按比例越小，两者正好抵消 —— 不是指数逼近、也不会永远差一点）。
        // 为什么必须限速而不是直接跳：换动作时频率若瞬间跳变，混合出来的信号在切换点有一次
        // 相位速度突变，听感就是「顿一下」；限速之后频率是连续量，过渡全程只是「同一个动作换了波形」。
        //
        // 窗口之外沿用原有逻辑（连续模式按用户设置的 BPM/s 限加速度，非连续模式指数平滑），
        // 所以稳态行为与设置项的含义一点没变。
        double currentBpm = baseBpm * _tempo;
        double transitionRemaining = _transitionDuration - _transitionElapsed;
        double nextBpm;
        if (transitionRemaining > TransitionSlewTailSeconds)
        {
            double desired = _tempoTargetBpm - currentBpm;
            double maxStep = Math.Abs(desired) / transitionRemaining * dt;
            nextBpm = currentBpm + Math.Clamp(desired, -maxStep, maxStep);
        }
        else
        {
            double tempoStep = (_tempoTargetBpm - currentBpm) * (1 - Math.Exp(-dt / 1.8));
            if (parameters.ContinuousBpm)
            {
                if (parameters.Acceleration <= 0.001)
                    tempoStep = _tempoTargetBpm - currentBpm;
                else
                {
                    double maxStep = parameters.Acceleration * dt;
                    tempoStep = Math.Clamp(tempoStep, -maxStep, maxStep);
                }
            }
            nextBpm = currentBpm + tempoStep;
        }
        _tempo = Math.Clamp(nextBpm / baseBpm, 0.05, 12.0);

        double audioTempo = 1 + Math.Clamp(audioBass, 0, 1) * 0.14 + Math.Clamp(beatPulse, 0, 1) * 0.05;
        // 相位推进：_phaseTime 的单位是「圈」，不是秒 —— 它才是两个动作共享的那条相位。
        // 物理频率 = 基准频率(当前动作) × _tempo × 音频调制，和旧实现完全一致（CurrentAutoBpm 的含义不变）。
        // 基准频率走 BaseFrequencyOf：自建动作用的是"1 圈/秒"这把和动作页同一把尺子。
        _phaseTime += dt * _tempo * audioTempo * BaseFrequencyOf(_current);

        // 两个动作拿<b>同一条相位</b>取样：淡入淡出时频率完全相同，混合结果没有拍频；
        // 相位又从不重置，所以切换点位置连续（t=0 时混合结果就是旧动作刚才那一帧）。
        double[] current = ComposeWave(_current, _currentStroke, _phaseTime, amplitudes, intensity, arousal, profile);
        double transition = _transitionDuration <= 0
            ? 1
            : Smooth01(Math.Clamp(_transitionElapsed / _transitionDuration, 0, 1));
        double[] values = current;
        if (transition < 1)
        {
            double[] previous = ComposeWave(_previous, _previousStroke, _phaseTime, amplitudes, intensity, arousal, profile);
            values = previous.Zip(current, (from, to) => from + (to - from) * transition).ToArray();
        }

        // 副轴一阶滞后：把「六根轴一起到达两端」改成「主轴先走、副轴跟着走」。
        // 刻意放在混合之后、音频/节拍点缀之前：底色的姿态该有跟随过程，
        // 而声音/节拍是「事件」，延迟它等于把「跟着音乐走」变成「慢半拍」。
        _follow.Apply(values, dt);

        double energy = Math.Clamp(audioEnergy, 0, 1);
        double beat = Math.Clamp(beatPulse, 0, 1);
        values[1] = Math.Clamp(50 + (values[1] - 50) * (1 + energy * 0.10), 0, 100);
        values[3] = Math.Clamp(values[3] + beat * Math.Sin(_phaseTime * Math.Tau) * 3.5, 0, 100);
        values[5] = Math.Clamp(values[5] + beat * Math.Cos(_phaseTime * Math.Tau) * 2.2, 0, 100);

        double bpm = BaseFrequencyOf(_current) * _tempo * audioTempo * 60;
        return new AutoBehaviorFrame(values, _current, _currentLabel, bpm, transition);
    }

    public void SetSelection(string selection) => _selection = NormalizeSelection(selection);

    public bool ToggleHold()
    {
        _holdCurrent = !_holdCurrent;
        return _holdCurrent;
    }

    public void Next(ComfortProfile profile)
    {
        _holdCurrent = false;
        _temporaryRemaining = 0;
        BeginTransition(PickNext(_current, profile), profile);
    }

    public void RequestTemporary(string pattern, double seconds, ComfortProfile profile)
    {
        _holdCurrent = false;
        _temporaryRemaining = Math.Clamp(seconds, 2, 120);
        BeginTransition(NormalizeSelection(pattern), profile);
    }

    /// <summary>
    /// 换动作 = <b>参数域过渡</b>（这是「换动作会顿一下」的修复点）。
    ///
    /// 旧实现只交叉淡化**位置**：两个动作各自按自己的基准频率（0.32–1.18Hz）取样，
    /// 同一时刻两条波形的频率差最多 3.7 倍 —— 混出来是拍频信号（一会儿同相、一会儿反相，
    /// 幅度忽大忽小），这就是那个「卡一下」；同时 <c>_tempo</c> 的定义是
    /// 「目标 BPM ÷ 当前动作的基准频率」，换动作后它<b>不重算</b>，于是物理频率在切换瞬间
    /// 直接跳到新基准上，再由 8 BPM/s 的加速度限制花 4 秒多爬回目标。
    ///
    /// 现在四件事都在参数域做：
    /// ① 相位只有一条（<c>_phaseTime</c>，单位「圈」），两个动作都拿它取样 ——
    ///    频率因此完全相同，混合不会产生拍频，相位不重置所以位置连续；
    /// ② 这里把 <c>_tempo</c> 按「旧物理频率 ÷ 新动作基准频率」重标定，
    ///    物理频率 baseHz(动作)·_tempo 在切换瞬间<b>不变</b>；
    /// ③ 目标 BPM 的到达交给 Step 里的斜坡，在过渡时长内限速滑过去（不是瞬间跳变）；
    /// ④ 物理频率与交叉淡化用同一个 <c>_transitionDuration</c>，所以「波形淡完」和
    ///    「频率到位」是同一时刻，不会出现「过渡早完了、频率还在爬」的两段式。
    ///
    /// 判据（自检/单测可断言）：切换点物理频率连续（|Δf| 只受斜坡速率限制）、
    /// 输出位置的二阶差分不出现尖峰（限速器的 jerk 上限本来也保证这一点）。
    /// </summary>
    private void BeginTransition(string next, ComfortProfile profile)
    {
        if (next == _current) return;
        AutoPlayParameters parameters = Parameters;
        // ① 先记下切换前的物理频率（Hz），换完动作再按新基准折算回 _tempo —— 频率因此连续。
        //    自建动作也走这里：它的基准频率是 1 圈/秒（动作页播放同一把尺子），
        //    所以「内置波形 ↔ 自建动作」之间的换动作与「内置 ↔ 内置」完全同一条路径，
        //    不会出现"抽到自建动作就顿一下"。
        double previousHz = BaseFrequencyOf(_current) * _tempo;
        _previous = _current;
        _previousStroke = _currentStroke;
        _current = next;
        _currentStroke = FindCustomStroke(next);
        _currentLabel = LabelFor(next);
        double nextBaseHz = Math.Max(BaseFrequencyOf(next), 1e-6);
        // 夹紧只在极端参数下才会碰到（自由游玩动作的基准频率范围 × BPM 上限仍在界内）；
        // 真碰上了也只是频率被拉一点，不会跳变。
        _tempo = Math.Clamp(previousHz / nextBaseHz, 0.05, 12.0);
        _behaviorElapsed = 0;
        _behaviorDuration = NextDuration(parameters);
        _transitionDuration = TransitionDuration(parameters);
        _transitionElapsed = 0;
        RememberPattern(next);
        ReportUsage(next);      // 被自动抽到的自建动作也算"用过"（动作页「最近用过」）
        // 非连续变速：BPM 只在换动作时重取目标，动作进行中保持稳定
        if (!parameters.ContinuousBpm)
            _tempoTargetBpm = PickTargetBpm(parameters, _baseSpeed);
    }

    /// <summary>
    /// 取样下一个动作。池子 = <b>内置波形 + 当前自建动作</b>（<see cref="AllPatterns"/>），
    /// 再按「参与随机的动作」勾选集筛（勾上 = 有权重，没勾 = 权重 0），最后排除舒适档禁用项、按权重抽。
    ///
    /// <b>默认（没勾任何东西）时池子只有内置波形</b> —— 与改动前逐位一致：
    /// 用户没有明确勾上自建动作，就不该在自动模式里突然多出他自己做的动作（默认手感不变）。
    /// 勾选列表里自建动作默认是"没勾"的（见 PlaygroundPage），两边口径因此对得上。
    ///
    /// 另外把「最近刚出现过的动作」的权重压到 <see cref="RecentPenalty"/>：
    /// 于是 A→B→A→B 这种来回重复基本不会发生（N=<see cref="RecentMemory"/>）。
    /// 勾选池只剩当前动作时仍允许重复自己，好过把用户的选择丢掉。
    /// </summary>
    private string PickNext(string? except, ComfortProfile profile)
    {
        string[] enabled = SnapshotEnabledPatterns();
        AutoPlayPattern[] all = AllPatterns.ToArray();
        AutoPlayPattern[] builtinOnly = all.Where(item => !item.IsCustom).ToArray();

        AutoPlayPattern[] pool = enabled.Length == 0
            ? builtinOnly
            : all.Where(item => Array.IndexOf(enabled, item.Id) >= 0).ToArray();
        // 勾选项全部失效（例如旧配置残留未知 Id、自建动作已被删掉）时退回内置波形，避免取样池为空
        if (pool.Length == 0) pool = builtinOnly;

        bool AllowedByComfort(AutoPlayPattern item) => profile.Id != "gentle" || !IsAggressive(item);

        var candidates = pool
            .Where(item => item.Id != except && AllowedByComfort(item))
            .ToArray();
        // 勾选池只剩当前动作：允许重复自己，好过把用户的选择丢掉
        if (candidates.Length == 0)
            candidates = pool.Where(AllowedByComfort).ToArray();
        // 勾选项全被舒适档位挡下：保持现状（舒适档位优先于勾选）
        if (candidates.Length == 0) return except ?? FreePlayPatterns[0].Id;
        return WeightedPick(candidates);
    }

    /// <summary>
    /// 「轻柔」档不抽的动作：内置里最快的两个（动感往复、递进爆发），以及自建动作里行程特别大的
    /// （≥ <see cref="GentleMaxCustomRange"/>）。舒适档的本意就是"别太猛"，
    /// 用户自建的动作完全可能比内置的更极端，所以给它同一道闸；其它档位不受影响。
    /// 递进爆发是这次并入池子的：不挡它的话，「轻柔」档的默认手感会比改动前猛一档。
    /// </summary>
    private static bool IsAggressive(AutoPlayPattern item)
    {
        if (!item.IsCustom) return item.Id is "intense_thrust" or "climax_burst";
        CustomStroke? stroke = FindCustomStroke(item.Id);
        return stroke is not null && stroke.MaxRange >= GentleMaxCustomRange;
    }

    /// <summary>
    /// 按权重抽一个候选。候选只有一个时直接返回，不必消耗随机数（保持既有随机序列）。
    /// 权重：收藏 ×<see cref="FavoriteBoost"/>；最近 3 个记忆里的压到 <see cref="RecentPenalty"/>；
    /// 跨启动的「最近用过」打 <see cref="RecentUsedPenalty"/> 折（比前者温和得多）。
    /// 内置波形不在收藏 / 最近列表里（那两处存的是动作库的 id），所以默认手感与改动前一致。
    /// </summary>
    private string WeightedPick(AutoPlayPattern[] candidates)
    {
        if (candidates.Length == 1) return candidates[0].Id;

        string[] favorites = SnapshotIds(_favoriteIdsProvider);
        string[] usedRecently = SnapshotIds(_recentIdsProvider);

        var weights = new double[candidates.Length];
        double total = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            // _recent[0] 一定是当前动作（不在这里，PickNext 已经排除），
            // 所以「最近记忆里的其它动作」就是 _recent 的第 1..RecentMemory-1 位。
            int ago = _recent.IndexOf(candidates[i].Id);
            double weight = ago >= 0 && ago < RecentMemory ? RecentPenalty : 1.0;

            string key = UsageKey(candidates[i]);
            if (weight >= 1.0 && Array.IndexOf(usedRecently, key) >= 0) weight *= RecentUsedPenalty;
            if (Array.IndexOf(favorites, key) >= 0) weight *= FavoriteBoost;

            weights[i] = weight;
            total += weight;
        }
        if (total <= 0) return candidates[_random.Next(candidates.Length)].Id;

        double roll = _random.NextDouble() * total;
        for (int i = 0; i < candidates.Length; i++)
        {
            roll -= weights[i];
            if (roll <= 0) return candidates[i].Id;
        }
        return candidates[^1].Id;
    }

    /// <summary>把动作记进「最近出现过」的短记忆（新的在前，重复的只留最新一次）。</summary>
    private void RememberPattern(string id)
    {
        _recent.RemoveAll(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, id);
        if (_recent.Count > RecentMemory) _recent.RemoveRange(RecentMemory, _recent.Count - RecentMemory);
    }

    private double NextDuration(AutoPlayParameters parameters)
    {
        double min = Math.Min(parameters.PatternMinSeconds, parameters.PatternMaxSeconds);
        double max = Math.Max(parameters.PatternMinSeconds, parameters.PatternMaxSeconds);
        return RandomRange(min, max);
    }

    /// <summary>在 [BpmMin, BpmMax] 内取目标 BPM，按速度倍率缩放并夹到面板上限。</summary>
    private double PickTargetBpm(AutoPlayParameters parameters, double baseSpeed)
    {
        double min = Math.Min(parameters.BpmMin, parameters.BpmMax);
        double max = Math.Max(parameters.BpmMin, parameters.BpmMax);
        double scaled = RandomRange(min, max) * Math.Clamp(baseSpeed, 0.1, 3.0);
        return Math.Clamp(scaled, AutoPlayParameters.BpmLimitMin, AutoPlayParameters.BpmLimitMax);
    }

    private static double BaseBpm(string pattern) =>
        Math.Max(BaseFrequencyOf(pattern) * 60.0, 1e-6);

    /// <summary>
    /// 某个池子项（内置波形 Id 或 <c>stroke:xxx</c>）的基准频率。
    /// 过渡重标定（<see cref="BeginTransition"/>）、相位推进、实时 BPM 显示三处都用它 ——
    /// 换成函数而不是直接调 <see cref="Osr6MotionComposer.BaseFrequency"/>，
    /// 就是为了让自建动作和内置波形走同一条频率/过渡路径。
    /// </summary>
    private static double BaseFrequencyOf(string id) =>
        id.StartsWith(CustomStrokePrefix, StringComparison.OrdinalIgnoreCase)
            ? CustomStrokeBaseHz
            : Osr6MotionComposer.BaseFrequency(id);

    /// <summary>
    /// 合成某一帧。<b>内置波形走 Osr6MotionComposer（一字未改）</b>，自建动作走
    /// <see cref="ComposeCustomStroke"/>；两条路吃的是同一组输入
    /// （相位「圈」+ 真实秒 + 各轴幅度 + 强度 + 兴奋度 + 舒适档），
    /// 所以换动作时的频率重标定、交叉淡化、副轴一阶滞后、音频/节拍点缀对两者完全一样 ——
    /// 这正是"自建动作也必须走同一条过渡路径、不能绕过它造成顿挫"的实现方式。
    /// </summary>
    private double[] ComposeWave(
        string id,
        CustomStroke? stroke,
        double phaseCycles,
        IReadOnlyList<double> amplitudes,
        double intensity,
        double arousal,
        ComfortProfile profile) =>
        stroke is null
            ? Osr6MotionComposer.Compose(id, phaseCycles, _slowSeconds, amplitudes, intensity, arousal, profile, _noiseSeed)
            : ComposeCustomStroke(stroke, phaseCycles, amplitudes, intensity, arousal, profile);

    /// <summary>
    /// 自建动作 → 六轴姿态。用的是<b>设备播放时那条公式</b>（<see cref="MotionEngine.SampleTempestAxis"/>，
    /// 动作页试看用的也是它），所以「自动模式抽到它」和「动作页点播放」是同一个动作；
    /// 区别只是时间轴换成了自动模式共享的那条相位 <paramref name="phaseCycles"/>（单位：圈）。
    ///
    /// 幅度怎么给：给 SampleTempestAxis 传满行程（intensity = 1），拿到 0..1 的采样后按
    ///     50 +（采样 − 0.5）× 100 ×（本轴幅度 / 50）× 舒适档强度 × 兴奋度增益
    /// 换算 —— 于是"幅度 50 + 强度 100%"时输出与动作页播放逐位一致，幅度调小则围着中位等比收缩，
    /// 而预设有偏心（例如只在 80%–100% 之间动）时偏心也被原样保留。
    /// 内置波形用的是同一把尺子（50 + 幅度×…×波形），两个来源在面板上的含义因此统一。
    ///
    /// 噪声（noiseFrom / noiseTo）按「圈」变化：管线里传的 cycleIndex 就是相位取整，
    /// 与设备播放"同一圈内恒定、跨圈才变化"的语义一致。
    /// </summary>
    private static double[] ComposeCustomStroke(
        CustomStroke stroke,
        double phaseCycles,
        IReadOnlyList<double> amplitudes,
        double intensity,
        double arousal,
        ComfortProfile profile)
    {
        double angle = Math.Tau * phaseCycles;
        long cycleIndex = (long)Math.Floor(phaseCycles);
        double safeIntensity = Math.Clamp(double.IsFinite(intensity) ? intensity : 1, 0.1, profile.MaxIntensity);
        double arousalScale = 1.0 + Math.Clamp(arousal - 30, 0, 70) / 280.0;

        var values = new double[6];
        for (int i = 0; i < values.Length; i++)
        {
            double[] axis = stroke.Axes[i];
            double amplitude = i < amplitudes.Count && double.IsFinite(amplitudes[i])
                ? Math.Clamp(amplitudes[i], 0, 50)
                : 0;
            double sample = MotionEngine.SampleTempestAxis(
                axis[0],
                axis[1],
                axis[2],
                axis[3],
                stroke.Motions[i],
                angle,
                1.0,                                    // 满行程采样：幅度在下面统一给，与内置波形同一把尺子
                axis.Length > 4 ? axis[4] : 0,
                axis.Length > 5 ? axis[5] : 0,
                i,
                cycleIndex);
            values[i] = Math.Clamp(
                50 + (sample - 0.5) * 100.0 * (amplitude / 50.0) * safeIntensity * arousalScale,
                0,
                100);
        }
        return values;
    }

    /// <summary>池子项 → 界面显示名（自建动作用它自己的名字，日志/状态条里不再是英文 id）。</summary>
    private static string LabelFor(string pattern)
    {
        foreach (var (id, label) in FreePlayPatterns)
            if (string.Equals(id, pattern, StringComparison.OrdinalIgnoreCase)) return label;
        return FindCustomStroke(pattern)?.Label ?? pattern;
    }

    private static double TransitionDuration(AutoPlayParameters parameters) =>
        Math.Clamp(parameters.TransitionSeconds,
            AutoPlayParameters.TransitionLimitMin,
            AutoPlayParameters.TransitionLimitMax);

    private static string NormalizeSelection(string? selection) =>
        string.IsNullOrWhiteSpace(selection) ? "free_play" : selection.Trim().ToLowerInvariant();

    private double RandomRange(double min, double max) => min + _random.NextDouble() * (max - min);

    private static double Smooth01(double value) => value * value * (3 - 2 * value);
}
