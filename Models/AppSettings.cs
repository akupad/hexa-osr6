using System.Text.Json;
using System.Security.Cryptography;
using Hexa.Services;

namespace Hexa.Models;

public class AppSettings
{
    /// <summary>轴顺序（唯一真源：<see cref="Hexa.Services.Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] Axes = Hexa.Services.Osr6DeviceProfile.InstalledAxes;
    private readonly object _saveLock = new();

    public AppSettings()
    {
        // 自动模式参数以本实例为唯一来源：序列器在 Services 层拿不到 UI/设置引用，
        // 通过全局提供器在每次 Step 读取一份值类型快照，面板改动即可即时生效。
        AutoBehaviorSequencer.SetParametersProvider(BuildAutoPlayParameters);
        // 「参与随机的动作」同样以本实例为唯一来源：取样时读取当前引用，勾选改动立即生效。
        AutoBehaviorSequencer.SetEnabledPatternsProvider(() => EnabledAutoPatterns);
    }

    public string Port        { get; set; } = "";
    public string FallbackPort { get; set; } = "";
    public bool AutoConnect   { get; set; } = true;
    public bool PreferWired   { get; set; } = true;
    public bool SimulationMode { get; set; } = false;

    /// <summary>
    /// Ayva 网页入口（<c>ws://localhost:8581/ws</c>）：让 Ayva 系的网页遥控器直接驱动设备。
    /// 默认关；在「测试台 → ⑤ 网页入口」里开。
    /// </summary>
    public bool AyvaWebSocketEnabled { get; set; } = false;

    /// <summary>多输出总开关：把发给设备的同一条 TCode 同时转给下面这些目标。</summary>
    public bool FanoutEnabled { get; set; } = false;

    /// <summary>多输出目标，一行一个：<c>udp://127.0.0.1:8000</c> 或 <c>ws://127.0.0.1:8581/ws</c>。</summary>
    public string FanoutTargets { get; set; } = "udp://127.0.0.1:8000";

    /// <summary>脚本库网页（<c>http://localhost:8582/</c>）：在浏览器/手机上浏览脚本库并点播。</summary>
    public bool LibraryWebEnabled { get; set; } = false;

    public double Speed       { get; set; } = 1.0;
    public double ScriptPlaybackSpeed { get; set; } = 1.0;
    public double IntensityScale { get; set; } = 1.0;
    public string ComfortProfile { get; set; } = "immersive";
    public string LastStroke  { get; set; } = "";

    // ── 自由游玩参数（自动模式内部参数，可在「游玩」页折叠面板调整）──────
    /// <summary>自由游玩 BPM 下限（1.0x 速度下，20–200），默认 45。</summary>
    public double AutoBpmMin            { get; set; } = 45;
    /// <summary>自由游玩 BPM 上限（1.0x 速度下，20–200），默认 110。</summary>
    public double AutoBpmMax            { get; set; } = 110;
    /// <summary>单个动作最短持续时间（秒，3–120），默认 8。</summary>
    public double AutoPatternMinSeconds { get; set; } = 8;
    /// <summary>单个动作最长持续时间（秒，3–120），默认 24。</summary>
    public double AutoPatternMaxSeconds { get; set; } = 24;
    /// <summary>动作之间的过渡（crossfade）时长（秒，0.2–3），默认 0.9。</summary>
    public double AutoTransitionSeconds { get; set; } = 0.9;
    /// <summary>变速加速度（BPM/秒，0–60，0 = 立即到位），默认 8；仅连续变速开启时生效。</summary>
    public double AutoAcceleration      { get; set; } = 8;
    /// <summary>true = BPM 持续漂移；false = 仅在换动作时改变 BPM。默认 true。</summary>
    public bool   AutoContinuousBpm     { get; set; } = true;
    /// <summary>
    /// 参与自动模式随机播放的动作 Id（取自 AutoBehaviorSequencer.AllPatternIds）。
    ///
    /// **语义（2026-09 收紧）**：空列表 = **只用内置波形**（自建动作必须显式勾选才会进池）。
    /// 旧注释写的是"空列表 = 全部启用"，那是自建动作还进不了池的年代 —— 现在自建动作也在池里，
    /// 若继续沿用"空 = 全选"，用户新存一个动作就会莫名其妙被自动模式抽到。
    /// Id 形如内置 `organic_flow` / 自建 `stroke:<presetId>`；Normalize 会去空白、去重并剔除未知 Id。
    /// </summary>
    public List<string> EnabledAutoPatterns { get; set; } = new();

    /// <summary>本地控制接口开关。⚠ 待开发：服务当前停用，Normalize 会强制置 false。</summary>
    public bool WebApiEnabled { get; set; } = false;
    public bool GlobalHotkeysEnabled { get; set; } = true;
    public HotkeyConfig Hotkeys { get; set; } = new();
    public string ApiToken    { get; set; } = "";
    public Dictionary<string, int> AxisMin { get; set; } = new()
        { ["L0"]=0,["L1"]=0,["L2"]=0,["R0"]=0,["R1"]=0,["R2"]=0 };
    public Dictionary<string, int> AxisMax { get; set; } = new()
        { ["L0"]=9999,["L1"]=9999,["L2"]=9999,["R0"]=9999,["R1"]=9999,["R2"]=9999 };

    // ── 手柄映射 ──────────────────────────────────────────────────────
    public GamepadConfig GamepadMap { get; set; } = new();

    // ── 悬浮窗 ───────────────────────────────────────────────────────
    public bool   OverlayVisible { get; set; } = false;
    public double OverlayLeft    { get; set; } = -1;   // -1 = 首次启动自动定位
    public double OverlayTop     { get; set; } = -1;
    /// <summary>悬浮窗底板不透明度（0.2–1.0）。</summary>
    public double OverlayOpacity { get; set; } = 0.95;
    /// <summary>悬浮窗显示项开关（默认全部显示）。</summary>
    public bool   OverlayShowConnection { get; set; } = true;   // 连接状态点 + 文字
    public bool   OverlayShowMode       { get; set; } = true;   // 运行模式
    public bool   OverlayShowSpeed      { get; set; } = true;   // 速度
    public bool   OverlayShowIntensity  { get; set; } = true;   // 强度
    public bool   OverlayShowBpm        { get; set; } = true;   // BPM

    // ── 音频响应 ──────────────────────────────────────────────────────
    public bool   AudioReactiveEnabled { get; set; } = false;
    public double AudioSensitivity     { get; set; } = 1.0;   // 0–2.0（对应 0–200%）
    public bool   AudioLowPassEnabled  { get; set; } = false;
    public string AudioDeviceId        { get; set; } = "";
    public string AudioResponseMode    { get; set; } = "energy";   // energy/bass/beat=音乐律动；voice=角色声音（呻吟=抽插）
    public double AudioNoiseGate       { get; set; } = 0.08;

    // ── 沉浸感（事件化动作 / 多轴语汇 / 延迟对齐）────────────────────
    /// <summary>声音响应是否用「事件化动作」：从声音里识别冲击/渐强/节拍，按事件给动作，而不是一直跟着音量循环。</summary>
    public bool AudioEventMotion { get; set; } = true;

    /// <summary>是否启用多轴动作语汇（冲击带扭转、慢段落带倾斜）。关掉就只有 L0 动。</summary>
    public bool MotionMultiAxis { get; set; } = true;

    /// <summary>
    /// 画面与设备的对齐偏移（毫秒，−200…+200）。正值 = 设备晚一点动。
    /// 用于声音驱动这条路径（游戏桥有自己的「延迟补偿」）。
    /// </summary>
    public int MotionCalibrationMs { get; set; } = 0;

    /// <summary>
    /// 「安静时停住」：游戏伴随 / 自动跟随启动的动作，在没有声音（持续 0.7 秒）时缓慢回中并停发，
    /// 有声音再继续。**默认关**（保持原来的连续动作行为，由用户自己决定要不要）。
    /// 用户自己在游玩页按的「开始自动」不受这个开关影响。
    /// </summary>
    public bool GateCompanionOnSilence { get; set; } = false;

    /// <summary>
    /// 每个游戏一套配置：进游戏时按进程名应用（动作风格 + 强度），不用每次重调。
    /// 键 = 进程名（不含 .exe，忽略大小写）。
    /// </summary>
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    // ── 输入融合：精确动作 + 氛围叠加 ────────────────────────────────
    /// <summary>
    /// 氛围叠加：在精确动作（脚本 / 遥测 / 游戏桥）之上，再叠一层很轻的、只走次要轴的微动。
    /// **默认关**：精确动作是游戏/作者的意图，加不加气氛应该由用户决定。
    /// </summary>
    public bool AmbientOverlay { get; set; } = false;

    /// <summary>氛围叠加幅度（轴行程百分比，5–30，默认 12）：越大越明显，也会越「抢戏」。</summary>
    public double AmbientOverlayAmount { get; set; } = 12;

    /// 声音响应时主轴（L0）的半幅行程，单位是轴行程的百分比（5–60，默认 30）。
    /// 意思是「声音最大时，设备在 50±这个值 之间来回」——越大动得越深。
    /// </summary>
    public double AudioMotionRange     { get; set; } = 30;

    // ── 脚本播放增强（只影响播放时的动作，不改脚本文件本身）────────────
    /// <summary>
    /// 多轴联动：播放 funscript 时，脚本没写动作的轴跟着主轴一起动（快的地方加扭转、上下带倾斜）。
    /// **默认关**：这是「加戏」，用户没要求就别替他加。
    /// 关掉「多轴动作」总开关时联动也不会生效（出口处的门控会把次要轴钉回中位）。
    /// </summary>
    public bool ScriptAxisLinkEnabled { get; set; } = false;

    /// <summary>联动幅度：派生轴最多偏离中位多少（轴行程百分比，5–60，默认 30）。</summary>
    public double ScriptAxisLinkAmount { get; set; } = ScriptAxisLinker.DefaultAmount;

    /// <summary>
    /// 空档填缝：脚本里长时间不动（两端位置几乎相同）的段落，跟着音乐事件补上小动作。
    /// **默认关**；需要能听到声音（见 <see cref="AudioListenOnly"/>）。
    /// </summary>
    public bool ScriptGapFillEnabled { get; set; } = false;

    /// <summary>多长的静止区间才算「空档」（毫秒，800–6000，默认 1500）。</summary>
    public int ScriptGapFillMinMs { get; set; } = (int)ScriptGapFiller.DefaultMinGapMs;

    /// <summary>
    /// 填缝的摆动幅度（轴行程百分比，5–40，默认 20）。
    /// 刻意比「声音响应 → 动作幅度」小：填缝是补白，不该抢脚本的风头；
    /// 幅度越大，限速器跟着换向时的余摆也越大（见 MotionSafetyLimiter 的说明）。
    /// </summary>
    public double ScriptGapFillRange { get; set; } = 20;

    /// <summary>
    /// 脚本平滑：播放时把「动作点之间走直线」换成单调三次曲线（速度连续、不冲过极值）。
    /// **默认开**（用户 2026-10-04 要求）：换向不再硬折返、声音更小、齿轮更省；
    /// 代价是极快抖动段落会被柔化一点，想要逐位原样播放就在「播放增强」里关掉它。
    /// </summary>
    public bool ScriptSmoothingEnabled { get; set; } = true;

    /// <summary>平滑强度 0–100：0 = 完全等于原来的直线插值，100 = 完全平滑。</summary>
    public double ScriptSmoothingStrength { get; set; } = ScriptSmoothing.DefaultStrength;

    /// <summary>
    /// 只监听系统声音、不驱动设备。
    /// 「空档填缝」需要有声音事件可用，而完整的「声音响应」会接管设备（与脚本播放互斥），
    /// 所以填缝走这条「只采集、不输出」的通道。
    /// </summary>
    public bool AudioListenOnly { get; set; } = false;

    // ── 画面观察（给「游戏伴随」用的画面信号；现在只测量、不驱动设备）────
    /// <summary>
    /// 「画面信号（观察模式）」开关。<b>默认关</b>：它只是盯住一个窗口算几个数字给用户看，
    /// 一条动作指令都不发（见 <see cref="Hexa.Services.ScreenWatchService"/>）。
    /// 等这个信号在真实内容上验证可用了，再考虑把它接成伴随的「动作来源」之一。
    /// </summary>
    public bool ScreenWatchEnabled { get; set; } = false;

    /// <summary>要看哪个窗口：窗口标题或进程名都行（对不上就如实报「找不到那个窗口」）。留空 = 还没选。</summary>
    public string ScreenWatchTarget { get; set; } = "";

    /// <summary>
    /// 画面判定的灵敏度（0.2–3.0，默认 1.0）：越大判定线越低、越容易判成「疑似情色场景」。
    /// </summary>
    public double ScreenWatchSensitivity { get; set; } = 1.0;

    /// <summary>
    /// 本地识别模型多久跑一次（毫秒，150–2000，默认 300）。
    /// 中档一次推理实测 13–20ms，300ms 一次 ≈ 每秒 3 次：跟得上画面，CPU 只占一点点。
    /// 只影响「模型分多久更新一次」，不影响抓帧 —— 运动量/节奏始终是每秒 13 次。
    /// </summary>
    public int ScreenWatchModelEveryMs { get; set; } = 300;

    /// <summary>
    /// 识别模型的档位："s" 小（22MB，最快）/ "m" 中（44MB，默认，综合最好）/ "l" 大（106MB，最准）。
    /// 三档是三个独立的文件（文件名带档位后缀），换档要重新下载那一档的模型。
    /// </summary>
    public string NsfwModelTier { get; set; } = "m";

    // ── 游戏桥的动作整形 ─────────────────────────────────────────────
    /// <summary>
    /// 游戏桥的指令平滑（去抖）：游戏每 20–50ms 发一条指令、常带噪声，直接透传就是"抖"。
    /// 默认开，代价是少量延迟（平滑时间常数见 <see cref="BridgeSmoothingMs"/>）。
    /// </summary>
    public bool BridgeInputSmoothing { get; set; } = true;

    /// <summary>平滑时间常数（毫秒，0–300，默认 90）：越大越柔、越滞后；0 = 不平滑。</summary>
    public int BridgeSmoothingMs { get; set; } = (int)BridgeMotionShaper.DefaultSmoothingMs;

    /// <summary>
    /// 游戏桥的多轴联动：游戏通常只驱动一根轴（LinearCmd 只有一个位置），
    /// 打开后按同一套规则把其余轴带起来，设备才不是"只会上下动的棒子"。默认关。
    /// </summary>
    public bool BridgeAxisLink { get; set; } = false;

    /// <summary>游戏桥的联动幅度（轴行程百分比，5–60，默认 25）。</summary>
    public double BridgeAxisLinkAmount { get; set; } = 25;

    /// <summary>
    /// 游戏桥是否也遵守「舒适档」的速度上限。
    /// 默认开：桥原来只有"插值不小于 60ms"这道阀（≈1667/秒），比沉浸档的 360/秒 快四倍多，
    /// 游戏一条指令就能把设备抽到撞限位。关掉等于回到旧行为。
    /// </summary>
    public bool BridgeUseComfortLimits { get; set; } = true;

    // 简洁的游戏伴随预设；底层规则仍由 RuleEngine 使用固定的 gallery/reaction 两层执行。
    public string CompanionProcess     { get; set; } = "";
    /// <summary>
    /// 底色动作（「平时用哪套动作」）：两条路共用 —— 规则引擎的 companion-base 规则，
    /// 以及前台监视器（ForegroundWatcher）进游戏时套用的预设。这个游戏有专属配置时被专属配置覆盖。
    /// </summary>
    // 默认「自由巡游 · 自动换动作」：伴随的价值就是"不重复"，固定一个 pattern 循环等于自带一个很差的脚本。
    // 老配置里存了自己选的动作就照旧用它（这个默认只影响全新安装）。
    public string CompanionBaseMode    { get; set; } = "free_play";
    /// <summary>
    /// 底色强度（0.1–2.0，默认 1.0）：底色动作跑多猛，和 <see cref="CompanionBaseMode"/> 一样两条路共用。
    /// 旧配置里没有这个字段（反序列化保持默认 1.0），Normalize 会从旧的「服务强度」迁移一次；
    /// 界面改动会同时写本字段与 <see cref="CompanionIntensity"/>，所以迁移不会反过来覆盖用户的新选择。
    /// </summary>
    public double CompanionBaseIntensity { get; set; } = 1.0;
    public string CompanionReaction    { get; set; } = "sound";
    public string CompanionSensitivity { get; set; } = "standard";
    public int GameTelemetryPort       { get; set; } = GameTelemetryService.DefaultPort;
    public string GameTelemetryToken   { get; set; } = "";
    public bool AllowRawTCodeUdp       { get; set; } = false;

    // ── AI 助手（DeepSeek / 硅基流动，可留空禁用）───────────────────────
    public string AiApiKey  { get; set; } = "";
    public string AiApiBase { get; set; } = "https://api.deepseek.com";
    public string AiModel   { get; set; } = "deepseek-v4-flash";

    // ── AI 实时语音通话（Realtime WebSocket，第二阶段；默认关闭）─────────
    /// <summary>总开关：true 才允许在「AI 助手」页发起实时通话。</summary>
    public bool   AiRealtimeEnabled    { get; set; } = false;
    /// <summary>Realtime WebSocket 地址，例如 wss://api.openai.com/v1/realtime。</summary>
    public string AiRealtimeUrl        { get; set; } = "";
    /// <summary>Realtime 模型名，例如 gpt-4o-realtime-preview。</summary>
    public string AiRealtimeModel      { get; set; } = "";
    /// <summary>Realtime 专用 API Key；留空则复用 AiApiKey。</summary>
    public string AiRealtimeKey        { get; set; } = "";
    /// <summary>实时通话采样率（Hz，16bit 单声道），夹在 8000–48000，默认 24000。</summary>
    public int    AiRealtimeSampleRate { get; set; } = 24000;

    // ── 规则引擎 ──────────────────────────────────────────────────────
    public bool RuleEngineEnabled { get; set; } = false;
    public List<RuleConfig> Rules { get; set; } = DefaultRules();

    // ── 前台自动服务（自动切预设） ──────────────────────────────────
    public bool   CompanionAuto       { get; set; } = false;   // 进游戏自动切服务预设

    /// <summary>
    /// （已被「动作来源」<see cref="CompanionSource"/> 取代的旧字段，只为读老配置而保留：
    /// Normalize 仍会清洗它，UI 与运行逻辑都不再使用。）
    /// "simple" = 当年「进游戏套用一套固定动作」；"advanced" = 当年「按规则 + 声音反应」。
    /// </summary>
    // 默认空 = "这份配置从来没设过做法" → 迁移成推荐的「自动」。
    // （老配置里存的是 "simple"/"advanced"，所以能被区分出来；见 CompanionSource 的取值器。）
    public string CompanionKind       { get; set; } = "";
    /// <summary>（旧字段，已被 <see cref="CompanionBaseMode"/> 取代，只为读老配置而保留。）</summary>
    public string CompanionMode        { get; set; } = "organic_flow";
    /// <summary>（旧字段，已被 <see cref="CompanionBaseIntensity"/> 取代，只为读老配置而保留。）</summary>
    public double CompanionIntensity   { get; set; } = 1.0;

    // 动作来源的存储（空 = 旧配置里没有这个字段，读到时会按 CompanionKind 迁移，见下）。
    private string _companionSource = "";

    /// <summary>
    /// 动作来源 —— 决定「动作到底从哪来」的唯一开关：
    /// "auto" = 有游戏信号（遥测）就跟游戏，没有就用声音兜底；"sound" = 只用声音，不理会游戏信号；
    /// "telemetry" = 只用游戏信号，收不到就完全不动（装过桥接插件的游戏选它）。
    /// 空 = 旧配置文件里没有这个字段（反序列化会保持空）：这时按旧「做法」<see cref="CompanionKind"/> 的语义解释 ——
    /// 旧「进阶」= 规则 + 声音反应 + 游戏信号 → auto；其它（含「简易」）= 固定底色动作、不看游戏信号 → sound。
    /// Normalize / 保存之后这个值就固化了（配置文件里写的是真正的来源）。
    /// </summary>
    public string CompanionSource
    {
        get => _companionSource.Length > 0
            ? _companionSource
            : LegacySourceFromKind();
        set => _companionSource = (value ?? "").Trim();
    }

    // ══ 媒体同步（外挂播放器时间码从动）2026-09 ══════════════════════════
    /// <summary>是否让脚本跟着外挂播放器的时间码走（默认关）。</summary>
    public bool   MediaSyncEnabled   { get; set; } = false;
    /// <summary>外挂播放器类型：mpv / mpc / vlc / potplayer / deovr / heresphere（默认 mpv）。</summary>
    public string MediaSyncPlayer    { get; set; } = "mpv";
    /// <summary>时间码偏移（毫秒，正 = 脚本晚于画面）。</summary>
    public int    MediaSyncOffsetMs  { get; set; } = 0;
    /// <summary>播放器 Web/管道端口（0 = 自动探测常见端口）。MPC-HC 默认 13579、VLC 默认 8080。</summary>
    public int    MediaSyncPort      { get; set; } = 0;
    /// <summary>打开媒体时按同名自动载入脚本（默认开）。</summary>
    public bool   MediaSyncAutoLoad  { get; set; } = true;

    // ══ 画面跟随（姿态推断节奏）2026-09 ══════════════════════════════════
    /// <summary>用姿态模型定位运动主体再测节奏（比全画面稳，默认开）。</summary>
    public bool   ScreenUsePose      { get; set; } = true;
    /// <summary>画面跟随的相位锁定强度 0–1（0 = 只跟速度，1 = 强锁相）。</summary>
    public double ScreenPhaseLock    { get; set; } = 0.35;

    // ══ 动作库：收藏 / 最近 / 落盘 ═════════════════════════════════════════
    /// <summary>收藏的动作 id（游玩页与动作页共用）。</summary>
    public List<string> FavoriteStrokes { get; set; } = [];
    /// <summary>最近用过的动作 id（越靠前越新，最多留 12 个）。</summary>
    public List<string> RecentStrokes   { get; set; } = [];
    /// <summary>上次的快速预设 id（重启后高亮回到它）。</summary>
    public string LastQuickPreset { get; set; } = "";
    /// <summary>上次自动模式用的波形/动作（重启后回到它；空 = 回自由巡游）。</summary>
    public string LastAutoPattern { get; set; } = "";

    // ══ AI：流式 / 思考强度 / 危险动作确认 ═════════════════════════════════
    /// <summary>流式输出（首字更快，默认开）。</summary>
    public bool   AiStreaming        { get; set; } = true;
    /// <summary>思考强度：none / low / medium / high（默认 none：实时对话不该先想半天）。</summary>
    public string AiReasoningEffort  { get; set; } = "none";
    /// <summary>高幅度/长时间/从静止启动的动作，执行前先确认（默认开）。</summary>
    public bool   AiConfirmRiskyTools { get; set; } = true;

    // ══ 日志 ═══════════════════════════════════════════════════════════════
    /// <summary>日志文件上限（MB，超过就轮转；0 = 不限制）。</summary>
    public int    LogMaxMegabytes    { get; set; } = 10;
    /// <summary>日志里是否记录被观察窗口的标题（默认关：标题可能暴露在看什么）。</summary>
    public bool   LogWindowTitles    { get; set; } = false;

    /// <summary>
    /// 上一次加载设置时如果发现文件坏了，这里留一句给界面用的说明（启动后弹一次气泡）。
    /// 静默回落默认值最危险的地方：**轴限位会被清成 0–9999**，用户以为设置还在。
    /// </summary>
    public static string? LastLoadNotice { get; private set; }

    // ── 记住上次的窗口位置/大小与所在页面（用户不用每次重新摆窗口）──────────
    public double WindowWidth  { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public double WindowLeft   { get; set; } = -1;    // <0 = 没记过（不用 NaN，JSON 存不了）
    public double WindowTop    { get; set; } = -1;
    public bool   WindowMaximized { get; set; }
    public string LastPageTag  { get; set; } = "playground";

    // ══ AI 助手相关：是否已把明文 Key 迁移成加密（内部标记）════════════════
    /// <summary>API Key 是否已用 DPAPI 加密存储（内部用，界面不显示）。</summary>
    public bool   AiKeyProtected     { get; set; } = false;

    // ── 动作键联动（需求4）──────────────────────────────────────────
    public bool ActionLinkEnabled { get; set; } = false;

    // ── 游戏桥（Intiface WebSocket → 串口 TCode；单桥 / 双桥两种呈现） ──
    public bool   GameBridgeEnabled { get; set; } = false;
    public string GameBridgeMode    { get; set; } = "single";   // single / dual
    public int    GameBridgePort    { get; set; } = 12345;
    /// <summary>振动指令的响应方式：position = 强度直接驱动 V0（保留游戏节奏）；off = 忽略振动指令（不下发任何动作）。</summary>
    public string GameBridgeVibrateMode { get; set; } = "off";
    /// <summary>延迟补偿（毫秒）：越大设备动作越滞后，用于对齐画面。
    /// UI 滑块范围 0–300（TestLabPage.BridgeLatencySlider），配置层仍夹在 0–500 以兼容旧值。</summary>
    public int    GameBridgeLatencyMs { get; set; } = 0;
    /// <summary>是否允许局域网设备连接桥（VR 头显/手机）。默认关闭，仅本机回环，避免防火墙弹窗与暴露。</summary>
    public bool   GameBridgeAllowLan  { get; set; } = false;

    /// <summary>上次编辑的动作合成文件路径（打开/保存时更新，供下次继续编辑）。</summary>
    public string LastCompositionPath { get; set; } = "";

    // ── 存储位置（留空 = 默认 %LOCALAPPDATA%\Hexa\...）─────────────
    /// <summary>编排页「同步驱动设备」勾选状态（记住上次选择，避免以为没反应）。</summary>
    public bool ScriptDriveDevice { get; set; } = false;

    /// <summary>
    /// 拖动「轴限位」滑块时是否实时驱动设备。默认 false（安全默认）：拖动只改限位数值。
    /// 用户主动勾选后才持久化为 true，避免重启后意外恢复「一拖就动设备」的危险状态。
    /// </summary>
    public bool LimitSliderDriveDevice { get; set; } = false;

    /// <summary>动作合成目录（.json）。空 = 默认 compositions 目录。</summary>
    public string CompositionFolder { get; set; } = "";

    /// <summary>脚本库目录（.funscript）。空 = 默认 Scripts 目录。</summary>
    public string ScriptFolder { get; set; } = "";

    // ── 首次启动引导 ────────────────────────────────────────────────
    public bool FirstRunDone { get; set; } = false;

    /// <summary>自定义目录清洗：去空白、去掉不存在的盘符风险（只做 Trim，存在性由 UI 校验）。</summary>
    private static string NormalizeFolder(string? folder)
    {
        string trimmed = (folder ?? "").Trim().Trim('"');
        if (trimmed.Length == 0) return "";
        try { return System.IO.Path.GetFullPath(trimmed); }
        catch { return ""; }
    }

    // ── Persistence ──────────────────────────────────────────────────
    private static string CfgPath => Environment.GetEnvironmentVariable("HEXA_SETTINGS_PATH") is { Length: > 0 } overridePath
        ? overridePath
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hexa", "settings.json");

    /// <summary>
    /// 旧「做法」→ 新「动作来源」的迁移规则（只有一处定义，取值器和 Normalize 共用）：
    /// 旧「进阶」= 规则 + 声音反应 + 游戏信号 → auto；旧「简易」= 固定动作、不看游戏信号 → sound；
    /// 空（从没设过，也就是全新安装）→ auto（推荐值）。
    /// </summary>
    private string LegacySourceFromKind() => CompanionKind?.Trim().ToLowerInvariant() switch
    {
        "advanced" => "auto",
        "simple" => "sound",
        _ => "auto",
    };

    /// <summary>
    /// 数据目录（settings.json 所在目录）。HEXA_SETTINGS_PATH 覆盖时跟随覆盖路径，
    /// 这样自检/渲染探针不会污染真实用户目录。
    /// </summary>
    public static string DataDirectory =>
        Path.GetDirectoryName(CfgPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// 旧版本用过的数据目录名（软件先后叫过 Helix、Spindle）。
    /// 只用于一次性迁移，不要在新代码里引用。按「越新越先」的顺序找。
    /// </summary>
    private static readonly string[] LegacyFolderNames = ["Spindle", "Helix"];

    /// <summary>
    /// 从旧目录迁移数据（改名 Helix → Hexa 时用）。
    /// 规则：**只复制、不删除**；新目录里已有同名文件/目录时一律跳过（不覆盖新数据）；
    /// 任何异常都只记日志，绝不让启动失败。
    /// </summary>
    public static void MigrateLegacyDataIfNeeded()
    {
        try
        {
            string newDir = DataDirectory;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            Directory.CreateDirectory(newDir);
            int copied = 0;
            var sources = new List<string>();

            foreach (string legacyName in LegacyFolderNames)
            {
                string legacyDir = Path.Combine(localAppData, legacyName);
                if (string.Equals(newDir, legacyDir, StringComparison.OrdinalIgnoreCase)) continue;   // 覆盖路径指到旧目录
                if (!Directory.Exists(legacyDir)) continue;

                // 文件：设置 / 动作预设 / 波形草稿 / 自定义人格
                foreach (string name in new[] { "settings.json", "presets.json", "wave-draft.json", "custom_personas.json" })
                {
                    string src = Path.Combine(legacyDir, name);
                    string dst = Path.Combine(newDir, name);
                    if (File.Exists(src) && !File.Exists(dst))
                    {
                        File.Copy(src, dst, overwrite: false);
                        copied++;
                    }
                }

                // 目录：合成 / 脚本库（用户自定义过目录时，路径存在 settings.json 里，这里只搬默认目录）
                foreach (string name in new[] { "compositions", "Scripts" })
                {
                    string src = Path.Combine(legacyDir, name);
                    string dst = Path.Combine(newDir, name);
                    if (!Directory.Exists(src)) continue;
                    foreach (string file in Directory.EnumerateFiles(src, "*", System.IO.SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(src, file);
                        string target = Path.Combine(dst, relative);
                        if (File.Exists(target)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: false);
                        copied++;
                    }
                }

                sources.Add(legacyDir);
            }

            if (copied > 0)
                AppLogger.Info($"已从旧数据目录迁移 {copied} 个文件：{string.Join("、", sources)} → {newDir}（旧目录保留，可自行删除）");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"旧数据迁移失败（不影响使用，可手动把文件复制到 {DataDirectory}）：{ex.Message}");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(CfgPath))
            {
                var fresh = new AppSettings();
                fresh.Normalize();
                return fresh;
            }
            var json = File.ReadAllText(CfgPath);
            // 允许 Infinity/NaN 这类字面量：某些数值设置一旦变成 ±Infinity，默认序列化会抛
        // ArgumentException（实测 app.log 里 122 次"设置保存失败"），用户改的所有设置就全丢了。
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
            var cfg = JsonSerializer.Deserialize<AppSettings>(json, opts) ?? new AppSettings();
            cfg.Normalize();
            return cfg;
        }
        catch (Exception ex)
        {
            AppLogger.Error("设置加载失败，已使用安全默认值", ex);
            // 关键：**先把坏文件留证**再让调用方用默认值。
            // 否则 App.OnStartup 紧接着就会 Save() 一次，把损坏的配置直接覆盖掉 ——
            // 用户那些手工调好的参数（轴限位、热键、脚本目录、AI 配置）就永久没了，
            // 而日志里只有一句"加载失败"，连坏在哪都看不到。
            try
            {
                string broken = CfgPath + ".corrupt.bak";
                File.Copy(CfgPath, broken, overwrite: true);
                AppLogger.Warn($"损坏的设置文件已备份到 {broken}（可人工检查/恢复；程序这次用默认值启动）");
                LastLoadNotice = "设置文件读不出来，已备份到 " + broken +
                    "，这次用默认值启动 —— 轴限位、热键、脚本目录等已回到出厂值，需要你重新设一遍。";
            }
            catch (Exception copyError)
            {
                AppLogger.Warn("备份损坏的设置文件失败：" + copyError.Message);
            }
            var fallback = new AppSettings();
            fallback.Normalize();
            return fallback;
        }
    }

    /// <summary>内置默认规则（示例，用户可在 settings.json 中编辑）。</summary>
    private static List<RuleConfig> DefaultRules() => new()
    {
        new() { Id = "audio-high",   Type = "audio",   Role = "reaction", Threshold = 0.72, ReleaseThreshold = 0.56, MinHoldMs = 2200, Mode = "intense_thrust", Intensity = 1.35, Priority = 100 },
        new() { Id = "audio-mid",    Type = "audio",   Role = "reaction", Threshold = 0.42, ReleaseThreshold = 0.28, MinHoldMs = 1800, Mode = "game_flow",      Intensity = 1.05, Priority = 90  },
        new() { Id = "gamepad-busy", Type = "gamepad", Role = "reaction", Threshold = 0.22, ReleaseThreshold = 0.10, MinHoldMs = 1500, Mode = "organic_flow",   Intensity = 0.85, Priority = 80  },
    };

    /// <summary>加载后补齐缺失字段/键，防止手工改坏或旧版配置导致运行时异常。</summary>
    public void Normalize()
    {
        AxisMin ??= new Dictionary<string, int>();
        AxisMax ??= new Dictionary<string, int>();
        foreach (var ax in Axes)
        {
            if (!AxisMin.ContainsKey(ax)) AxisMin[ax] = 0;
            if (!AxisMax.ContainsKey(ax)) AxisMax[ax] = 9999;
            AxisMin[ax] = Math.Clamp(AxisMin[ax], 0, 9999);
            AxisMax[ax] = Math.Clamp(AxisMax[ax], 0, 9999);
            // min>max 时交换，避免后续 Math.Clamp 抛异常
            if (AxisMin[ax] > AxisMax[ax])
                (AxisMin[ax], AxisMax[ax]) = (AxisMax[ax], AxisMin[ax]);
        }

        GamepadMap ??= new GamepadConfig();
        Hotkeys ??= new HotkeyConfig();
        Hotkeys.Normalize();
        if (GamepadMap.AxisSources is not { Length: 6 })
            GamepadMap.AxisSources = ["RightTrigger", "LeftStickY", "LeftStickX", "LeftTrigger", "RightStickY", "RightStickX"];
        if (GamepadMap.AxisInvert is not { Length: 6 })
            GamepadMap.AxisInvert = new bool[6];

        Speed = double.IsFinite(Speed) ? Math.Clamp(Speed, 0.1, 3.0) : 1.0;
        ScriptPlaybackSpeed = double.IsFinite(ScriptPlaybackSpeed)
            ? Math.Clamp(ScriptPlaybackSpeed, 0.25, 2.0)
            : 1.0;
        IntensityScale = double.IsFinite(IntensityScale) ? Math.Clamp(IntensityScale, 0.1, 2.0) : 1.0;
        ComfortProfile = Hexa.Models.ComfortProfile.Resolve(ComfortProfile).Id;
        GamepadMap.Deadzone = double.IsFinite(GamepadMap.Deadzone)
            ? Math.Clamp(GamepadMap.Deadzone, 0, 0.95)
            : 0.12;

        // 画面观察：目标只做 Trim（窗口随时会开/关，这里不校验存在性，交给服务运行时如实反馈）；
        // 灵敏度夹在服务认可的区间里，非法值回落默认 1.0。
        ScreenWatchTarget = (ScreenWatchTarget ?? "").Trim();
        ScreenWatchSensitivity = double.IsFinite(ScreenWatchSensitivity)
            ? Math.Clamp(ScreenWatchSensitivity, ScreenWatchService.SensitivityMin, ScreenWatchService.SensitivityMax)
            : 1.0;
        // 识别模型的跑动间隔：夹在服务认可的区间里，坏值回落默认值。
        ScreenWatchModelEveryMs = ScreenWatchModelEveryMs is >= ScreenWatchService.ModelIntervalMinMs
            and <= ScreenWatchService.ModelIntervalMaxMs
            ? ScreenWatchModelEveryMs
            : ScreenWatchService.ModelIntervalDefaultMs;
        // 识别模型档位：只认小/中/大，写坏的（或老配置没有这个字段）一律回落到默认的「中」。
        string modelTier = (NsfwModelTier ?? "").Trim().ToLowerInvariant();
        NsfwModelTier = modelTier is "s" or "m" or "l" ? modelTier : NsfwClassifier.DefaultTierId;

        // 悬浮窗：不透明度夹在 [0.2, 1.0]，非法值回退默认
        OverlayOpacity = double.IsFinite(OverlayOpacity)
            ? Math.Clamp(OverlayOpacity, 0.2, 1.0)
            : 0.95;

        Port = NormalizePort(Port);
        FallbackPort = NormalizePort(FallbackPort);
        if (string.Equals(Port, FallbackPort, StringComparison.OrdinalIgnoreCase)) FallbackPort = "";

        if (string.IsNullOrWhiteSpace(ApiToken) || ApiToken == "helix-local")
            ApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        // 音频灵敏度夹在 [0,2]（0–200%）
        if (double.IsNaN(AudioSensitivity) || double.IsInfinity(AudioSensitivity))
            AudioSensitivity = 1.0;
        AudioSensitivity = Math.Clamp(AudioSensitivity, 0, 2);
        AudioResponseMode = AudioResponseMode.Trim().ToLowerInvariant();
        // energy/bass/beat = 音乐律动的三种跟法；voice = 角色声音（呻吟 = 一次抽插）。
        if (AudioResponseMode is not ("energy" or "bass" or "beat" or "voice")) AudioResponseMode = "energy";
        AudioNoiseGate = double.IsFinite(AudioNoiseGate) ? Math.Clamp(AudioNoiseGate, 0, 0.5) : 0.08;
        AudioMotionRange = double.IsFinite(AudioMotionRange) ? Math.Clamp(AudioMotionRange, 5, 60) : 30;
        MotionCalibrationMs = Math.Clamp(MotionCalibrationMs, -200, 200);
        // 脚本播放增强：幅度与最短空档都夹在界面滑杆的范围内，坏值一律回默认（别把设备推到极端行程）。
        ScriptAxisLinkAmount = double.IsFinite(ScriptAxisLinkAmount)
            ? Math.Clamp(ScriptAxisLinkAmount, ScriptAxisLinker.MinAmount, ScriptAxisLinker.MaxAmount)
            : ScriptAxisLinker.DefaultAmount;
        ScriptGapFillMinMs = (int)Math.Clamp(ScriptGapFillMinMs, ScriptGapFiller.MinGapMs, ScriptGapFiller.MaxGapMs);
        BridgeSmoothingMs = (int)Math.Clamp(BridgeSmoothingMs,
            BridgeMotionShaper.MinSmoothingMs, BridgeMotionShaper.MaxSmoothingMs);
        BridgeAxisLinkAmount = double.IsFinite(BridgeAxisLinkAmount)
            ? Math.Clamp(BridgeAxisLinkAmount, ScriptAxisLinker.MinAmount, ScriptAxisLinker.MaxAmount)
            : 25;
        ScriptSmoothingStrength = double.IsFinite(ScriptSmoothingStrength)
            ? Math.Clamp(ScriptSmoothingStrength, ScriptSmoothing.MinStrength, ScriptSmoothing.MaxStrength)
            : ScriptSmoothing.DefaultStrength;
        ScriptGapFillRange = double.IsFinite(ScriptGapFillRange)
            ? Math.Clamp(ScriptGapFillRange, ScriptGapFiller.MinFillRange, ScriptGapFiller.MaxFillRange)
            : ScriptGapFiller.DefaultFillRange;
        // 旧「做法」字段：只保留原值（读老配置用），新配置留空 —— 空表示"没设过"，
        // 迁移时按"全新安装"处理（默认推荐的「自动」）。别把它强行写成 "simple"，
        // 否则全新安装会被当成老用户的「简易」，永远落不到 auto。
        CompanionKind = string.Equals(CompanionKind?.Trim(), "advanced", StringComparison.OrdinalIgnoreCase)
            ? "advanced"
            : string.Equals(CompanionKind?.Trim(), "simple", StringComparison.OrdinalIgnoreCase) ? "simple" : "";
        // 动作来源：只认 auto / sound / telemetry。空 = 旧配置里没有这个字段（读出来会按旧「做法」迁移），
        // 这里把迁移结果固化下来；写坏的值不猜，回落到最安全的「自动」。
        string rawSource = (CompanionSource ?? "").Trim().ToLowerInvariant();
        CompanionSource = rawSource switch
        {
            // screen = 跟着画面里的动作走（姿态/节奏推断，见 Services/PoseTracker.cs）
            "auto" or "sound" or "telemetry" or "screen" => rawSource,
            "" => LegacySourceFromKind(),
            _ => "auto",
        };
        AmbientOverlayAmount = double.IsFinite(AmbientOverlayAmount)
            ? Math.Clamp(AmbientOverlayAmount, 5, 30)
            : 12;
        GameProfiles ??= new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, GameProfile profile) in GameProfiles.ToArray())
        {
            if (profile is null || string.IsNullOrWhiteSpace(key)) { GameProfiles.Remove(key); continue; }
            profile.Process = key.Trim();
            profile.Intensity = double.IsFinite(profile.Intensity) ? Math.Clamp(profile.Intensity, 0.1, 2.0) : 1.0;
            if (string.IsNullOrWhiteSpace(profile.Mode)) profile.Mode = "organic_flow";
        }

        // ── 媒体同步 / 画面跟随 / AI / 日志（2026-09 新增，全部要清洗）──────────
        string rawPlayer = (MediaSyncPlayer ?? "").Trim().ToLowerInvariant();
        MediaSyncPlayer = rawPlayer switch
        {
            "mpc" or "vlc" or "potplayer" or "deovr" or "heresphere" or "mpv" => rawPlayer,
            _ => "mpv",
        };
        MediaSyncOffsetMs = Math.Clamp(MediaSyncOffsetMs, -5000, 5000);
        MediaSyncPort = Math.Clamp(MediaSyncPort, 0, 65535);
        ScreenPhaseLock = double.IsFinite(ScreenPhaseLock) ? Math.Clamp(ScreenPhaseLock, 0, 1) : 0.35;
        string rawEffort = (AiReasoningEffort ?? "").Trim().ToLowerInvariant();
        AiReasoningEffort = rawEffort switch
        {
            "low" or "medium" or "high" or "none" => rawEffort,
            _ => "none",
        };
        LogMaxMegabytes = Math.Clamp(LogMaxMegabytes, 0, 500);
        FavoriteStrokes ??= [];
        RecentStrokes ??= [];
        FavoriteStrokes = FavoriteStrokes.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(200).ToList();
        RecentStrokes = RecentStrokes.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(12).ToList();
        LastQuickPreset = (LastQuickPreset ?? "").Trim();
        LastAutoPattern = (LastAutoPattern ?? "").Trim();

        CompanionProcess = (CompanionProcess ?? "").Trim();
        // 底色动作：两条路共用的一套。旧配置里可能还没有它（空）→ 用旧的「服务动作」CompanionMode 迁移；
        // 清洗（Trim/小写 + 白名单）就在下一行做，所以这里直接搬原始值即可。
        if (string.IsNullOrWhiteSpace(CompanionBaseMode) && !string.IsNullOrWhiteSpace(CompanionMode))
            CompanionBaseMode = CompanionMode;
        CompanionBaseMode = CompanionBaseMode?.Trim().ToLowerInvariant() switch
        {
            "gentle_wave" or "organic_flow" or "game_flow" or "free_play"
                or "slow_sine" or "deep_pulse" or "spiral_tease"
                or "random_micro" or "edge_swirl" or "intense_thrust"
                or "climax_burst" => CompanionBaseMode.Trim().ToLowerInvariant(),
            _ => "free_play",
        };
        CompanionReaction = CompanionReaction?.Trim().ToLowerInvariant() switch
        {
            "none" or "sound" or "burst" => CompanionReaction.Trim().ToLowerInvariant(),
            _ => "sound",
        };
        CompanionSensitivity = CompanionSensitivity?.Trim().ToLowerInvariant() switch
        {
            "low" or "standard" or "high" => CompanionSensitivity.Trim().ToLowerInvariant(),
            _ => "standard",
        };
        CompanionMode = CompanionMode?.Trim().ToLowerInvariant() switch
        {
            "gentle_wave" or "organic_flow" or "game_flow" or "free_play"
                or "slow_sine" or "deep_pulse" or "spiral_tease"
                or "random_micro" or "edge_swirl" or "intense_thrust"
                or "climax_burst" => CompanionMode.Trim().ToLowerInvariant(),
            _ => "organic_flow",
        };
        CompanionIntensity = double.IsFinite(CompanionIntensity)
            ? Math.Clamp(CompanionIntensity, 0.1, 2.0)
            : 1.0;
        // 底色强度：两条路共用的一套。默认 1.0 = 没设过；旧配置里改过「服务强度」→ 迁移一次。
        // 界面改动会同时写 CompanionBaseIntensity 与 CompanionIntensity，两条相等后这里就不会再动手。
        if (CompanionBaseIntensity == 1.0 && Math.Abs(CompanionIntensity - 1.0) > 0.001)
            CompanionBaseIntensity = CompanionIntensity;
        CompanionBaseIntensity = double.IsFinite(CompanionBaseIntensity)
            ? Math.Clamp(CompanionBaseIntensity, 0.1, 2.0)
            : 1.0;

        // ── 游戏桥 ───────────────────────────────────────────────
        GameBridgePort = GameBridgePort is >= 1024 and <= 65535 ? GameBridgePort : 12345;
        WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 900, 4096) : 1280;
        WindowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 600, 2160) : 800;
        if (!double.IsFinite(WindowLeft) || WindowLeft < -10000) WindowLeft = -1;
        if (!double.IsFinite(WindowTop) || WindowTop < -10000) WindowTop = -1;
        LastPageTag = LastPageTag?.Trim().ToLowerInvariant() switch
        {
            "playground" or "manual" or "strokes" or "editor" or "ai" or "scripts" or "settings" or "testlab"
                => LastPageTag.Trim().ToLowerInvariant(),
            _ => "playground",
        };

        GameBridgeMode = GameBridgeMode?.Trim().ToLowerInvariant() switch
        {
            "single" or "dual" or "six" => GameBridgeMode.Trim().ToLowerInvariant(),
            _ => "six",
        };
        GameBridgeVibrateMode = GameBridgeVibrateMode?.Trim().ToLowerInvariant() switch
        {
            "position" or "off" or "rotate" or "stroke" => GameBridgeVibrateMode.Trim().ToLowerInvariant(),
            _ => "off",
        };
        GameBridgeLatencyMs = Math.Clamp(GameBridgeLatencyMs, 0, 500);

        // 待开发：本地控制接口（WebApiService）暂未开放，无论配置里写了什么都强制关闭
        WebApiEnabled = false;

        CompositionFolder = NormalizeFolder(CompositionFolder);
        ScriptFolder = NormalizeFolder(ScriptFolder);

        GameTelemetryPort = Math.Clamp(GameTelemetryPort, 1024, 65535);
        GameTelemetryToken = (GameTelemetryToken ?? "").Trim();
        if (GameTelemetryToken.Length is < 32 or > 128)
            GameTelemetryToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        // AI 助手：默认值兜底，ApiKey 留空则功能禁用（绝不硬编码）
        AiApiKey  = (AiApiKey ?? "").Trim();
        AiApiBase = string.IsNullOrWhiteSpace(AiApiBase)
            ? "https://api.deepseek.com"
            : AiApiBase.Trim().TrimEnd('/');
        AiModel   = string.IsNullOrWhiteSpace(AiModel)
            ? "deepseek-v4-flash"
            : AiModel.Trim();

        // AI 实时语音通话：字符串统一 Trim；URL 去掉尾部斜杠；采样率夹在 8000–48000（默认 24000）
        AiRealtimeUrl   = (AiRealtimeUrl ?? "").Trim().TrimEnd('/');
        AiRealtimeModel = (AiRealtimeModel ?? "").Trim();
        AiRealtimeKey   = (AiRealtimeKey ?? "").Trim();
        AiRealtimeSampleRate = AiRealtimeSampleRate is >= 8000 and <= 48000
            ? AiRealtimeSampleRate
            : 24000;

        // 规则表兜底：为 null（旧配置缺失）时回填默认；空列表视为用户主动清空，保持空闲
        Rules ??= DefaultRules();
        foreach (var r in Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Type)) r.Type = "audio";
            if (string.IsNullOrWhiteSpace(r.Role)) r.Role = "reaction";
            if (string.IsNullOrWhiteSpace(r.Mode)) r.Mode = "slow_sine";
            r.Type = r.Type.Trim().ToLowerInvariant();
            r.Role = r.Role.Trim().ToLowerInvariant();
            r.Mode = r.Mode.Trim().ToLowerInvariant();
            if (r.Type is not ("audio" or "gamepad" or "process" or "game")) r.Type = "audio";
            if (r.Role is not ("gallery" or "reaction" or "filler")) r.Role = "reaction";
            if (r.Mode is not ("free_play" or "slow_sine" or "deep_pulse" or "spiral_tease" or "edge_swirl"
                or "climax_burst" or "random_micro" or "gentle_wave" or "intense_thrust"
                or "organic_flow" or "game_flow"))
                r.Mode = "slow_sine";
            if (double.IsNaN(r.Threshold) || double.IsInfinity(r.Threshold)) r.Threshold = 0.6;
            if (double.IsNaN(r.Intensity) || double.IsInfinity(r.Intensity)) r.Intensity = 1.2;
            r.Threshold = Math.Clamp(r.Threshold, 0, 1);
            r.ReleaseThreshold = double.IsFinite(r.ReleaseThreshold)
                ? Math.Clamp(r.ReleaseThreshold, 0, r.Threshold)
                : Math.Max(0, r.Threshold - 0.15);
            r.Intensity = Math.Clamp(r.Intensity, 0.1, 2.0);
            r.MinHoldMs = Math.Clamp(r.MinHoldMs, 0, 30000);
        }

        // ── 自由游玩参数：夹紧到合法区间，min > max 时交换（范围与面板滑块共用）──
        AutoBpmMin = ClampFinite(AutoBpmMin, 45, AutoPlayParameters.BpmLimitMin, AutoPlayParameters.BpmLimitMax);
        AutoBpmMax = ClampFinite(AutoBpmMax, 110, AutoPlayParameters.BpmLimitMin, AutoPlayParameters.BpmLimitMax);
        if (AutoBpmMin > AutoBpmMax) (AutoBpmMin, AutoBpmMax) = (AutoBpmMax, AutoBpmMin);
        AutoPatternMinSeconds = ClampFinite(AutoPatternMinSeconds, 8,
            AutoPlayParameters.PatternLimitMin, AutoPlayParameters.PatternLimitMax);
        AutoPatternMaxSeconds = ClampFinite(AutoPatternMaxSeconds, 24,
            AutoPlayParameters.PatternLimitMin, AutoPlayParameters.PatternLimitMax);
        if (AutoPatternMinSeconds > AutoPatternMaxSeconds)
            (AutoPatternMinSeconds, AutoPatternMaxSeconds) = (AutoPatternMaxSeconds, AutoPatternMinSeconds);
        AutoTransitionSeconds = ClampFinite(AutoTransitionSeconds, 0.9,
            AutoPlayParameters.TransitionLimitMin, AutoPlayParameters.TransitionLimitMax);
        AutoAcceleration = ClampFinite(AutoAcceleration, 8,
            AutoPlayParameters.AccelerationLimitMin, AutoPlayParameters.AccelerationLimitMax);

        // ── 参与随机的动作：去空白/去重/剔除未知 Id；结果为空 = 全部启用 ──
        EnabledAutoPatterns = NormalizeEnabledPatterns(EnabledAutoPatterns);
    }

    /// <summary>
    /// 清洗「参与随机的动作」列表：去空白、转小写、去重（忽略大小写），
    /// 内置 Id 必须存在于 <see cref="AutoBehaviorSequencer.AllPatternIds"/>，自建动作只做形状校验。
    ///
    /// **为什么自建动作（<c>stroke:&lt;presetId&gt;</c>）不能一起做存在性过滤**：
    /// 动作库是在启动后期（`StrokesViewModel` 构造时）才把提供器装进序列器的，
    /// 而 `Normalize()` 在那之前就跑了 —— 那时 `AllPatternIds` 里根本没有自建动作，
    /// 过滤一遍就会把用户勾好的"自建动作参与随机"**每次启动都清掉**（静默丢设置）。
    /// 所以这里只校验前缀形状；某个 id 指向的动作真的不存在时，取样阶段自然抽不到，无害。
    /// 返回空列表 = 只用内置波形。
    /// </summary>
    private static List<string> NormalizeEnabledPatterns(IEnumerable<string>? ids)
    {
        var result = new List<string>();
        if (ids is null) return result;
        var known = new HashSet<string>(AutoBehaviorSequencer.AllPatternIds, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in ids)
        {
            string id = (raw ?? "").Trim().ToLowerInvariant();
            if (id.Length == 0) continue;
            bool customStroke = id.StartsWith("stroke:", StringComparison.Ordinal) && id.Length > "stroke:".Length;
            if (!customStroke && !known.Contains(id)) continue;
            if (!seen.Add(id)) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>打包自动模式参数（供 AutoBehaviorSequencer 每帧读取，值类型无锁）。</summary>
    public AutoPlayParameters BuildAutoPlayParameters() => AutoPlayParameters.FromSettings(this);

    public bool Save()
    {
        lock (_saveLock)
        {
            try
            {
                string directory = Path.GetDirectoryName(CfgPath)!;
                Directory.CreateDirectory(directory);
                string tempPath = CfgPath + ".tmp";
                Rules ??= DefaultRules();
                string json;
                lock (Rules)
                    // 自检期间只允许写沙箱设置文件：审计发现的真实风险 —— 只要有人把 HEXA_SETTINGS_PATH
        // 指向真实 settings.json 跑一次自检，那一节会把"出厂默认值"写进去，用户配置就被覆盖了。
        if (Environment.GetEnvironmentVariable("HEXA_SELFTEST") is { Length: > 0 })
        {
            string? selfTestPath = Environment.GetEnvironmentVariable("HEXA_SETTINGS_PATH");
            if (selfTestPath is null || !selfTestPath.Contains("hexa-selftest", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("自检运行中：设置文件不在沙箱内，本次跳过写盘（保护用户配置）");
                return false;
            }
        }

        json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, CfgPath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("设置保存失败", ex);
                return false;
            }
        }
    }

    private static string NormalizePort(string? port) => (port ?? "").Trim().ToUpperInvariant();

    /// <summary>非有限值回退默认，否则夹紧到 [min, max]。</summary>
    private static double ClampFinite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
