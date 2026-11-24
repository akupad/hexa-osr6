using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 下拉框里的一行：进程名 + 窗口标题。用户认标题、程序认进程名，所以两个都留着。
/// </summary>
public sealed record ScreenWatchWindow(
    IntPtr Handle, string Title, string ProcessName, int Width, int Height, bool Minimized)
{
    /// <summary>显示用「进程名 · 标题」。标题过长只截显示，匹配仍然用完整的 <see cref="Title"/>。</summary>
    public string DisplayName
    {
        get
        {
            string title = Title.Length <= 58 ? Title : Title[..58] + "…";
            if (ProcessName.Length == 0) return title.Length == 0 ? "（无标题窗口）" : title;
            return title.Length == 0 ? ProcessName : $"{ProcessName} · {title}";
        }
    }
}

/// <summary>
/// 「画面同步」：抓一个窗口的画面，算出几个能解释的特征（肤色占比 / 运动量 / 节奏性 /
/// 呻吟事件 / 融合分），再加上本机训练好的识别模型给出的「内容判定分」；
/// 特征与判定分既供界面实时显示，也可以在用户明确选择「画面内容」这条动作来源后<b>驱动设备</b>
/// —— 跟随用的是「画面里的节奏 + 场景强度」，不是逐帧复刻画面。
///
/// 四条硬规矩（改这个类的人请一并守住）：
/// ① <b>默认绝不动设备</b>：只有同时满足
///    · 用户打开了「画面同步」（<see cref="AppSettings.ScreenWatchEnabled"/>，默认关），且
///    · 动作来源是 <c>screen</c>（用户明确选「画面内容」）或 <c>auto</c>（自动：遥测 &gt; 画面 &gt; 声音），且
///    · 绑定的那个程序在前台、看的窗口就是那个程序的窗口、场景强度过判定线，
///    四个条件时才下发；其余任何时刻设备都不归本类管。
///    设备调用只有两条：<c>TryClaimDirectInput("screen")</c> 与 <c>TrySendDirectAxes</c>；
///    <b>本类绝不改 MotionEngine</b>，也绝不绕过限速 / 舒适档（急停、限位、舒适档一律照旧生效）。
/// ② <b>画面不写盘、不上传</b>：不保存任何图像、不生成缩略图、不上传、不落临时文件；
///    只保留「当前这一帧」的 160×90 小图、给识别模型用的短边 236 缓冲、给姿态模型用的 640×640 画布，
///    且反复复用同一块内存（稳态零分配）。
///    唯一的网络行为是「用户点了下载模型」时把模型文件下到本机
///    （见 <see cref="NsfwClassifier"/> 与 <see cref="PoseTracker"/>），
///    那是**单向下载一个公开模型**，画面一个字节都不外传。
/// ③ <b>异常绝不外抛</b>：抓取线程、识别模型线程、姿态线程、看门狗里的一切异常都在线程内消化掉并记日志。
///    否则线程一死，界面上还写着「正在看」，用户以为在看、其实早就没了。
/// ④ <b>停不下来必须有兜底</b>：信号一断（抓不到画面 / 帧率掉成 0 / 线程卡住）就<b>缓降回中再交控制权</b>，
///    绝不留着设备挂在某个位置上；看门狗独立于抓帧线程，抓帧卡死也照样收尾。
///
/// 抓取路径：优先 <c>PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)</c>（对多数硬件加速窗口有效，
/// 窗口不必露在最前面）；返回 false 或抓到全黑/全白时，退化为「按窗口矩形从屏幕 DC 抓」
/// （<c>Graphics.CopyFromScreen</c>，内部就是 BitBlt + SRCCOPY，代价是窗口得露在桌面上、不能被挡住）；
/// 两条路都拿不到东西就如实报「抓不到画面」并把原因交给界面显示。
/// 抓到后立刻缩到 <see cref="FrameWidth"/>×<see cref="FrameHeight"/>，后面所有计算都在这张小图上做
/// —— 大图逐像素在每秒十几帧下扛不住。
///
/// 三条线程：抓帧线程（13fps，算运动量/节奏 + 每 250ms 排一帧给姿态模型 + 下发跟随动作）
/// + 识别模型线程（每 300ms 一次）+ 姿态线程（每 250ms 一次）。推理都在自己的线程上做，
/// 抓帧线程只做「缩小一张图」这种毫秒级的事，13fps 的节拍不会被拖慢。
/// </summary>
public sealed class ScreenWatchService : IDisposable
{
    // ── 尺寸与节拍 ────────────────────────────────────────────────────
    /// <summary>分析分辨率。所有特征都在这张小图上算（160×90 = 14400 像素，一次遍历零点几毫秒）。</summary>
    public const int FrameWidth = 160;
    public const int FrameHeight = 90;
    // 姿态模型单独一份更大的图：160×90 上人只有几十像素高，关键点（髋/踝）一半的帧会丢；
    // 模型内部反正固定缩到 640，多给点原始像素几乎不增加推理时间。
    public const int PoseWidth = 320;
    public const int PoseHeight = 180;

    /// <summary>
    /// 目标帧率 20 fps。原来 13 fps（76ms 一拍），而节奏峰值时间是「帧到帧」量出来的，
    /// 量化误差 ±半个帧间隔 = ±38ms 直接变成相位锁定误差，13 fps 时就是 ±38ms 的抖动；
    /// 20 fps 降到 ±25ms。160×90 的小图一次遍历不到 1ms，多出来的开销是 sleep 变短，可忽略。
    /// </summary>
    private const int TargetFps = 20;
    private const int FrameIntervalMs = 1000 / TargetFps;   // 50ms

    /// <summary>抓一次超过它就算「这帧太慢」：跳过下一帧只睡觉（自适应降频，20fps 时退到 10fps 而不是 6fps）。</summary>
    private const int SlowFrameMs = 50;
    private const int SlowFrameSkip = 1;

    /// <summary>整帧平均亮度低于这个值（全黑）或高于上限（全白）= 没抓到画面。</summary>
    private const double BlankLumaLow = 2;
    private const double BlankLumaHigh = 252;
    /// <summary>整帧最亮与最暗之差小于这个值 = 一整片均匀色，当作没抓到（D3D 窗口被 PrintWindow 抓成均匀深灰就靠它认出来）。</summary>
    private const int BlankSpanMin = 8;

    /// <summary>候选窗口的最小尺寸：比这更小的多半是工具栏、输入法候选框、气泡提示。</summary>
    private const int MinWindowWidth = 200;
    private const int MinWindowHeight = 150;

    /// <summary>源图边长上限（超大窗口的保护，见 CaptureAndAnalyze）。</summary>
    private const int MaxSourceSize = 4096;

    /// <summary>找不回目标时，隔多久重新枚举一次窗口（毫秒）。</summary>
    private const int ResolveRetryMs = 1000;

    // ── 特征参数（报告里的公式与阈值都对着这里）──────────────────────
    /// <summary>「中心区域」= 中间 60%（左右各留 20%）。主体通常在这一块。</summary>
    private const double CenterLeftRatio = 0.20;
    private const double CenterRightRatio = 0.80;

    /// <summary>运动量归一化：中心区域平均绝对亮度差 ÷ 32。32/255 ≈ 每像素平均变 12.5%，实测已是明显运动。</summary>
    private const double MotionFullScale = 32;
    private const double MotionSmoothing = 0.5;

    /// <summary>节奏性看最近约 3.2 秒（42 帧 @13fps）的 Motion 序列。</summary>
    private const int RhythmSamples = 42;
    /// <summary>去趋势后标准差到这个量级才算「画面真在动」（否则不给节奏分）。</summary>
    private const double MotionActiveScale = 0.05;
    private const double RhythmMinHz = 0.5;
    private const double RhythmMaxHz = 3.0;
    /// <summary>「动得最猛的那一拍」的门槛：运动量低于它的局部极大不算一拍（噪声）。</summary>
    private const double MotionPeakFloor = 0.08;
    /// <summary>节奏分低于它就不报频率（跟随那边会退回兜底频率），免得拿噪声里的假峰定速度。</summary>
    private const double RhythmHzGate = 0.20;

    /// <summary>呻吟事件的新鲜度：最近 1 秒内有人声事件才算「刚刚有」。</summary>
    private const double VoiceEventFreshSeconds = 1.0;
    /// <summary>有呻吟事件时场景强度的提升倍数。</summary>
    private const double VoiceBoost = 1.25;

    /// <summary>场景强度的进入判定阈值（灵敏度 1.0 时）。</summary>
    private const double SceneThresholdBase = 0.38;
    /// <summary>退出阈值 = 进入阈值 × 这个系数（迟滞：进去难、出来也要掉得够低）。</summary>
    private const double SceneThresholdRelease = 0.68;
    /// <summary>灵敏度系数的上下限（灵敏度 1/3 时最难触发、3 倍时最敏感）。</summary>
    private const double SensitivityFactorMin = 0.15;
    private const double SensitivityFactorMax = 1.6;

    /// <summary>肤色占比的平滑（快起慢落）：一闪而过能留住，静止后慢慢回落，数字不会每秒跳 13 次。</summary>
    private const double SkinAttack = 0.45;
    private const double SkinRelease = 0.12;
    /// <summary>融合分的平滑（同样快起慢落，慢落是为了不让判定灯闪）。</summary>
    private const double ScoreAttack = 0.45;
    private const double ScoreRelease = 0.10;

    /// <summary>灵敏度的合法区间（设置页/AppSettings.Normalize 共用这一份）。</summary>
    public const double SensitivityMin = 0.2;
    public const double SensitivityMax = 3.0;

    // ── 识别模型（本地分类器）的参数 ──────────────────────────────────
    /// <summary>
    /// 模型多久跑一次（毫秒）。<b>不是每帧</b>：中档一次推理 13–20ms，虽然跑得起，
    /// 但每帧都做纯属浪费（画面 300ms 内的变化模型也看不出来）；300ms 一次 ≈ 每秒 3 次，
    /// 判定跟得上画面，CPU 只占一点点。
    /// </summary>
    public const int ModelIntervalMinMs = 150;
    public const int ModelIntervalMaxMs = 2000;
    /// <summary>默认间隔（毫秒）。设置层清洗坏值时也用它，所以是公开的。</summary>
    public const int ModelIntervalDefaultMs = 300;

    /// <summary>融合权重：模型就绪时 = 0.7 × 模型分 + 0.3 × 手工特征分（模型是主判据）。</summary>
    private const double ModelWeight = 0.70;

    /// <summary>模型分的平滑（每 300ms 才有一个新样本，快起慢落；和特征分一样是为了不让判定灯闪）。</summary>
    private const double ModelScoreAttack = 0.60;
    private const double ModelScoreRelease = 0.15;

    // ── 姿态定位（运动主体）的参数 ────────────────────────────────────
    /// <summary>姿态模型多久跑一次（毫秒）。<b>不是每帧</b>：一次推理 20–40ms，250ms 一次足够跟上人的移动，
    /// 而 ROI 本身是慢变量（人在画面里不会每 77ms 就换个地方）。</summary>
    public const int PoseIntervalMinMs = 150;
    public const int PoseIntervalMaxMs = 600;
    public const int PoseIntervalDefaultMs = 250;

    /// <summary>一次姿态结果的保鲜期（毫秒）：超过它没用上新结果就退回「整幅画面」。
    /// 比采样间隔大 3 倍多，是为了让偶尔漏一次不抖动，真断了又能很快退回去。</summary>
    private const int PoseFreshMs = 900;

    /// <summary>ROI 的坐标平滑（快起慢落）：姿态框每 250ms 才更新一次，直接用会在两帧之间跳。</summary>
    private const double PoseRoiAttack = 0.55;
    private const double PoseRoiRelease = 0.25;

    /// <summary>ROI 在 160×90 小图上的最小尺寸（像素）：比这更小的框说明主体几乎看不见，用它算运动量只会更糟。</summary>
    private const int PoseRoiMinWidth = 24;
    private const int PoseRoiMinHeight = 14;

    // ── 画面同步（驱动设备）的参数 ────────────────────────────────────
    /// <summary>本类在设备上使用的控制权名字（见 MotionEngine.TryClaimDirectInput）。</summary>
    private const string FollowOwner = "screen";

    /// <summary>跟随的半幅基准（轴行程百分比）：深度再乘场景强度、再乘舒适档上限。42 = 深度满时主轴在中位 ±42%。
    /// 实测（_work/probe_limiter 仿真 + 真机）：幅度是「动了多少%」的第一感知，30 时安静场景只剩 3–9 点，
    /// 用户以为设备没动；42 配合下面的速度上限（360/(2π·42)=1.36Hz）实得行程 99%，比 30@1.9Hz 的 87% 更好。</summary>
    private const double FollowAmplitudeBase = 42;

    /// <summary>方向位移的平滑系数（每帧向新值靠 35%）：20fps 下约 6 帧到 90%，抖动看得见但不迟钝。</summary>
    private const double FlowSmoothing = 0.35;

    /// <summary>位移「满偏」多少像素算 +1/-1：90 行里 4 像素≈4.4% 画面高度，正常平移就已经明显了。</summary>
    private const double FlowFullScalePx = 4.0;
    // 主体方向：亮度变化超过这个值才算「这个像素在动」（低于它的多半是编码噪声）。
    private const int SubjectPixelThreshold = 10;
    // 包围盒至少要这么高（行）且至少这么多运动像素，才认为「画面里有个主体」。
    private const int SubjectMinSpan = 6;
    private const int SubjectMinPixels = 40;
    // 包围盒高度的平滑与增长率的平滑：两层低通，避免一像素抖动就摆动整个行程。
    private const double SubjectSpanSmoothing = 0.15;
    private const double SubjectGrowthSmoothing = 0.25;
    // 每秒增长 50% 算满幅靠近；死区 6%/秒。
    private const double SubjectGrowthFullScale = 0.5;

    // ── 主体块：没有人体骨架时的跟随源（2D 游戏、手绘动画、拍得远的真人）──
    // YOLO 对这类画面基本不出人（实测 2D 手绘：最新最大的 yolo26x 只有 1–5/20 帧），
    // 而画面分那套分不清「角色在动」和「雨在下/火花在炸」，所以要单独找「哪一块在动」。
    private const int BlobCols = 16;
    private const int BlobRows = 9;
    /// <summary>相对最亮格的切割线：格子的平均亮度差达到最亮格的这个比例才算主体。</summary>
    private const double BlobCellCut = 0.45;
    /// <summary>最亮格的平均亮度差下限（0–255）：低于它就是编码噪声，整帧不算有主体。</summary>
    private const double BlobMinEnergy = 4.0;
    /// <summary>主体最多能占多少格：超过说明整屏都在动（下雨、全屏特效、镜头晃），不认。</summary>
    private const double BlobMaxCoverage = 0.45;
    /// <summary>包围盒的平滑系数（每帧向新值靠 30%）。</summary>
    private const double BlobSmoothing = 0.30;
    /// <summary>主体块的保鲜期（毫秒）：超过就当这一拍没有主体。</summary>
    private const long BlobFreshMs = 700;
    /// <summary>算幅度/速度用的时间窗（毫秒）：一个动作周期 0.4–2 秒，取 2.4 秒能装下两拍。</summary>
    private const long BlobWindowMs = 2400;
    /// <summary>
    /// 主体块的保持期（毫秒）：信号断了之后深度/速度沿用最后一次有效值这么久，超过才回落画面分。
    /// 和姿态的 <see cref="PoseHoldMs"/> 一个道理 —— 画面静一下（人物停住、转场）就立刻缓降回中，
    /// 用户感到的是「一顿一顿」，而不是「跟着动作」。
    /// </summary>
    private const long BlobHoldMs = 1200;
    private const int BlobHistorySize = 64;
    private readonly double[] _blobCellNow = new double[BlobCols * BlobRows];
    private readonly double[] _blobHistY = new double[BlobHistorySize];
    private readonly long[] _blobHistMs = new long[BlobHistorySize];
    private int _blobHistCount;
    private int _blobHistHead;
    private double _blobX0;
    private double _blobY0 = 1;
    private double _blobX1 = 1;
    private double _blobY1 = 1;
    private bool _blobValid;
    private long _blobMs;
    /// <summary>换挡限速用的平滑深度（NaN = 还没开始，下一拍直接取目标值）。</summary>
    private double _followDepthSmooth = double.NaN;
    private long _blobSignalMs;
    private double _blobHeldDepth = 50;
    private double _blobHeldSpeed;

    /// <summary>方向对行程重心的最大偏移（占半幅的比例）：只改「这一下推多深」，不改幅度。</summary>
    private const double FollowDirectionLean = 0.35;

    /// <summary>深度低于此值用「渐强」语汇（慢、连贯、多轴螺旋），否则用「抽插」语汇（进得干脆、退得明显）。</summary>
    private const double FollowSlowShapeDepth = 0.45;

    /// <summary>没测出节奏频率、但场景确实过线时用的兜底频率（Hz）：总比让设备定住不动强。</summary>
    private const double FollowFallbackHz = 1.2;

    /// <summary>跟随频率的上下限（Hz）：0.5 = 两秒一次，2.5 = 每秒两下半。再快就该由舒适档说话了。</summary>
    private const double FollowHzMin = 0.5;
    private const double FollowHzMax = 2.5;

    /// <summary>
    /// 姿态保持期（毫秒）：姿态信号断掉之后，深度/频率沿用最后一次有效值这么久，超过才回落画面分。
    /// 为什么是 1.5 秒：姿态源自身的保鲜期 FreshMs=600ms 只够盖住「一两帧没算出来」，
    /// 再叠一次常见的短暂遮挡/推理抖动（几百毫秒）加半个动作周期（节奏动作一拍 0.4–2 秒），
    /// 1.5 秒基本盖住最常见的单次空档，深度和频率不会中途跳回画面分再跳回来；
    /// 再长就是真的没信号了，该老实承认、回落画面分，别拿旧值硬撑。
    /// </summary>
    private const long PoseHoldMs = 1500;

    /// <summary>
    /// 相位对齐偏移：本类的跟随相位就是动作语汇「人声（抽插）」的内部相位，
    /// 那个语汇里 <b>0 = 完全抽出来、0.45 = 推到最里面</b>（见 MotionVocabulary.Render）。
    /// 画面动得最猛的那一刻（参考相位 0）应当对应「推到底」，所以目标相位 = 参考相位 + 这个偏移。
    /// </summary>
    private const double FollowPhaseOffset = 0.45;

    /// <summary>误差超过这么多拍（且连续两拍）就重锁相位 —— 平稳时只用有界修正，不硬重置。</summary>
    private const double FollowPhaseResyncError = 0.35;
    private const int FollowPhaseResyncBeats = 2;

    /// <summary>离场缓降时长（秒）：从当前幅度线性收回中位再交控制权。<b>不是急停</b>。</summary>
    private const double FollowEaseSeconds = 0.7;

    /// <summary>看门狗的判据（毫秒）：正在跟随却这么久没成功下发过一条，就强制缓降回中并交控制权。</summary>
    private const int FollowStallMs = 900;
    /// <summary>看门狗的巡检间隔（毫秒）。</summary>
    private const int FollowWatchdogMs = 400;

    /// <summary>PrintWindow 的 nFlags：2 = PW_RENDERFULLCONTENT（Win8.1+，能拿到硬件加速/合成后的内容）。</summary>
    private const uint PW_RENDERFULLCONTENT = 2;
    /// <summary>DwmGetWindowAttribute 的 DWMWA_CLOAKED：UWP 那些「看不见但存在」的窗口会被它标出来。</summary>
    private const int DWMWA_CLOAKED = 14;

    private const string DefaultFailureText =
        "这个程序可能是独占全屏，改成无边框窗口再试（也可能是内容受保护，比如带 DRM 的播放器）";

    // ── 状态 ──────────────────────────────────────────────────────────
    private readonly AppSettings _cfg;
    private readonly Lock _lock = new();

    private readonly List<ScreenWatchWindow> _windows = new();
    private ScreenWatchWindow? _target;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _disposed;
    private long _lastResolveMs;
    private long _lastAutoBindMs;                     // 「跟着当前窗口自动动」上次对前台窗口的时刻
    private const int AutoBindIntervalMs = 500;
    private static readonly string SelfProcessName = Process.GetCurrentProcess().ProcessName;

    /// <summary>
    /// 「跟着当前窗口自动动」：每半秒对一次前台窗口，把观察目标与绑定程序一起挪到它上面。
    /// 为什么要两个一起改：画面驱动要求「绑定的程序在前台、而且看的窗口属于它」
    /// （见 <see cref="FollowTargetProblem"/>），只改一个就永远驱动不起来。
    /// 目标写成进程名而不是标题：标题每换一集就变，进程名稳定；同一进程多个窗口时
    /// <see cref="MatchScore"/> + 面积最大的规则会挑到那个视频/游戏窗口。
    /// 只在抓帧线程里调用（它会顺带 <see cref="Refresh"/>，不要从界面线程重复触发）。
    /// </summary>
    private void TryAutoBindForeground(Stopwatch clock)
    {
        if (Volatile.Read(ref _manualEditing) != 0 || ManualBinding.Bound) return;
        if (clock.ElapsedMilliseconds - _lastAutoBindMs < AutoBindIntervalMs) return;
        _lastAutoBindMs = clock.ElapsedMilliseconds;

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return;
        string process = ProcessNameOf(pid);
        if (process.Length == 0) return;
        if (string.Equals(process, SelfProcessName, StringComparison.OrdinalIgnoreCase)) return;   // 自己在前面：不算在看内容
        if (process is "explorer" or "ShellExperienceHost" or "StartMenuExperienceHost" or "SearchHost"
            or "LockApp" or "TextInputHost") return;                                             // 桌面 / 开始菜单 / 锁屏不是内容窗口

        string bound = GameTelemetryProtocol.NormalizeProcessName(_cfg.CompanionProcess);
        string target = (_cfg.ScreenWatchTarget ?? "").Trim();
        string normalized = GameTelemetryProtocol.NormalizeProcessName(process);
        if (string.Equals(bound, normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target, normalized, StringComparison.OrdinalIgnoreCase)) return;

        _cfg.CompanionProcess = normalized;
        _cfg.ScreenWatchTarget = normalized;
        AppLogger.Info($"跟屏：目标换成 {normalized}（换窗口自动跟，绑定的程序也一起改）");
        try { _cfg.Save(); } catch (Exception ex) { AppLogger.Warn($"跟屏：记下目标时写设置失败 —— {ex.Message}"); }
        Refresh();
    }

    // 抓取缓冲：只有一个源图 + 一张 160×90 小图 + 两个复用数组，来回用同一块内存。
    private System.Drawing.Bitmap? _sourceBitmap;
    private System.Drawing.Bitmap? _smallBitmap;
    private int _sourceWidth;
    private int _sourceHeight;
    private readonly byte[] _bgra = new byte[FrameWidth * FrameHeight * 4];
    private readonly byte[] _prevLuma = new byte[FrameWidth * FrameHeight];
    private System.Drawing.Bitmap? _poseBitmap;
    private readonly byte[] _poseBgra = new byte[PoseWidth * PoseHeight * 4];

    // 排障口子：设了环境变量 HEXA_DUMP_FRAME=<目录> 就把前 3 帧分析图（就是喂模型的那张 160×90）存成 PNG。
    // 「姿态/场景看不到东西」时先看这张图：抓不到画面（全黑）和模型不认（图是对的）一眼就分开。
    private readonly string? _dumpDir = Environment.GetEnvironmentVariable("HEXA_DUMP_FRAME");
    private int _dumpCount;

    // 预览：最近一帧的 WPF 位图（已 Freeze，跨线程读安全），界面每 150ms 读一次；没抓到画面时是 null。
    private BitmapSource? _previewImage;
    private string _previewTarget = "";
    private string _previewRect = "";
    private int _previewFrameCounter;

    // ── 方向感知（画面整体上下位移）──────────────────────────────────
    /// <summary>每一行的亮度和：0.4ms 量级的互相关输入，比逐像素光流便宜得多。</summary>
    private readonly double[] _rowProfile = new double[FrameHeight];
    private readonly double[] _prevRowProfile = new double[FrameHeight];
    private bool _hasProfile;
    /// <summary>平滑后的位移（像素，正 = 内容向下移动）。</summary>
    private double _flowShiftPx;
    /// <summary>推导给跟随用的方向量：+1 = 主体在靠近（画面里变大），-1 = 在退开。</summary>
    private double _followApproach;
    // 主体运动：运动像素的上下包围盒 = 「主体在画面里占多高」。它变大就是在走近镜头，
    // 这跟镜头自己动没动无关 —— 固定机位里人自己动也算数，比整幅位移靠谱。
    private double _subjectSpanEma;
    private double _subjectGrowthEma;
    private double _lastDirectionSeconds;
    // 不用 volatile 修饰：取值一律走 Volatile.Read/Write，否则 Volatile.Write(ref …) 会报 CS0420。
    private bool _subjectDirection = true;
    private bool _hasPrevFrame;

    // 节奏性：Motion 环形缓冲 + 两次计算的临时数组（避免每帧 new）。
    private readonly double[] _motionRing = new double[RhythmSamples];
    private readonly double[] _rhythmScratch = new double[RhythmSamples];
    private readonly double[] _rhythmHp = new double[RhythmSamples];
    private readonly double[] _rhythmCorr = new double[RhythmSamples + 1];
    private int _motionCount;
    private int _motionWrite;
    private double _frameIntervalEmaMs = 1000.0 / TargetFps;
    private long _lastFrameMs;

    // 特征值：抓取线程写、界面线程读（x64 下 double 读写原子，用 Volatile 保证可见性）。
    private double _skinRatio;
    private double _skinCenterRatio;
    private double _motion;
    private double _rhythm;
    /// <summary>测出来的节奏频率（Hz，0.5–3；没测出节奏时为 0）。跟随的速度用它。</summary>
    private double _rhythmHz;
    /// <summary>最近一次「运动量局部极大」的时刻（抓帧时钟，秒）。跟随的参考相位由它推出来。</summary>
    private double _rhythmPeakSeconds = double.NegativeInfinity;
    private double _sceneScore;
    private double _featureScore;
    private int _voiceEvent;
    private int _suspect;

    /// <summary>「画面在动」的连续拍数 / 「没在动」的连续拍数（只抓帧线程写，见 FollowTickCore 的确认拍数）。</summary>
    private int _liveStreak;
    private int _deadStreak;
    private int _captureFailed;
    /// <summary>1 = 这一帧的 160×90 小图是「按手动框裁剪后再放大」得来的，分析坐标已经是框内整幅。</summary>
    private int _smallZoomed;
    /// <summary>上一次真正发出去的六轴（挡屏 / 抓不到画面时用它接着动一小会儿）。</summary>
    private double[]? _lastSentAxes;
    private long _lastSentMs;
    private long _holdUntilMs;
    /// <summary>遮挡后接着刚才动作的最长时间：够撑过「鼠标划过盖住一秒」，又不至于让人以为卡死。</summary>
    private const int FollowHoldMs = 1200;
    private int _fps;
    private long _frameCount;
    private string _failureReason = "";

    // ── 识别模型（本地分类器）────────────────────────────────────────
    // 它在**另一条线程**上跑：抓帧线程只负责把当前这帧按比例缩到"短边 236"画进缓冲再叫醒模型线程，
    // 推理（中档十几毫秒）绝不占用抓帧线程 —— 运动量和节奏必须一秒 13 次，不能被模型拖慢。
    private readonly Lock _modelSync = new();
    private readonly int _modelIntervalMs;
    // 两块缓冲交替用：模型线程读一块，抓帧线程画另一块（尺寸随窗口走，窗口不变就一直复用）。
    private readonly ModelFrameBuffer[] _modelFrames = { new(), new() };
    private int _modelFrameSlot;                      // 下一次要画进哪一块
    private int _modelJobSlot;                        // 已经交给模型线程的那一块
    private bool _modelBusy;                          // 模型线程手里还有没算完的帧
    private long _lastModelJobMs;
    private AutoResetEvent? _modelWake;
    private Thread? _modelThread;
    private volatile bool _modelStopRequested;
    private int _modelReady;                          // 已经出过分（true 才用 0.7 的权重参与融合）
    private double _modelScore;                       // 平滑后的 P(nsfw)
    private long _lastInferenceMs;
    private int _modelDownloading;

    // ── 姿态线程（运动主体）：和识别模型线程同一个套路 —— 抓帧线程只负责把图准备好，推理在别的线程上做 ──
    private readonly Lock _poseSync = new();
    private readonly PoseFrameBuffer[] _poseFrames = { new(), new() };
    /// <summary>每一块画布对应的 letterbox 换算表（关键点是在带灰边的画布上给出的，要换算回窗口坐标）。</summary>
    private readonly LetterboxMap[] _poseMaps = new LetterboxMap[2];
    private int _poseFrameSlot;                       // 下一次要画进哪一块
    private int _poseJobSlot;                         // 已经交给姿态线程的那一块
    private bool _poseBusy;                           // 姿态线程手里还有没算完的帧
    private long _lastPoseJobMs;
    /// <summary>上一次排给姿态模型那帧的粗略签名（平均亮度×1000）：一样就不排队，省掉一次 640×640 推理。</summary>
    private long _lastPoseSig;
    private int _poseSigValid;
    private int _poseStillSkips;
    private const long PoseStillSigMin = 800;
    private AutoResetEvent? _poseWake;
    private Thread? _poseThread;
    private volatile bool _poseStopRequested;
    private int _poseDownloading;

    // 姿态结果：姿态线程写、抓帧线程/界面线程读。全是 double/int，x64 下读写原子，用 Volatile 保证可见性。
    private double _poseLeft, _poseTop, _poseRight, _poseBottom;   // 平滑后的 ROI（窗口内 0–1）
    private double _poseConfidence;
    private int _poseKeypoints;
    private long _poseAtMs;                           // 最近一次有效姿态的时刻（Environment.TickCount64）
    private int _poseLocated;                         // 最近一次姿态推理有没有找到人
    private int _poseInUse;                           // 这一帧的运动量是不是真的在 ROI 里算的（界面读它）
    private int _poseEverWorked;                      // 至少成功定位过一次（区分「没装/没跑」和「画面里没人」）
    private long _poseInferenceMs;

    // 姿态运动源（默认关）：从同一路分析画面里算「髋 ↔ 踝的竖直位移」当深度、「相邻帧位移」当速度。
    // 它自己带线程和缓冲，这里只负责喂帧（AnalyzeFrame）和读信号（FollowComputeDepth / FollowComputeHz）。
    // 没开 PoseVisionEnabled 时连 Submit 都不会调，线程不会起、缓冲不会分配 —— 关掉就是零开销。
    private readonly PoseMotionSource _poseMotion = new();

    // 姿态门控的保持状态（只有跟随 tick 一条线程读写）：姿态短暂断掉时沿用这里缓存的
    // 最后一次有效值，避免深度/频率瞬间跳回画面分、信号恢复时又跳回去。
    private long _poseLastValidMs;                    // 上次有效姿态信号的时刻（TickCount64；0 = 还没出现过）
    private double _poseHeldDepth, _poseHeldSpeed;    // 上次有效姿态的深度（0–100）与速度（深度单位/秒）
    private int _poseGateFellBack;                    // 1 = 已处于「回落画面分」状态（回落日志只在翻转那一刻写一条）

    // ── 跟随（驱动设备）─────────────────────────────────────────────
    // 这些字段会被三条线程碰：抓帧线程（主循环）、看门狗定时器、界面线程（Stop/Dispose）。
    // 全部是 int/double/long 的原子读写 + 一把小锁保护「状态切换」，不做长操作。
    private readonly Lock _followLock = new();
    private int _followActive;                        // 正在驱动设备（已拿到控制权且这一拍发了指令）
    private int _followWant;
    private long _previewPauseUntil;                  // 拖「活动范围」把手期间让开（TickCount64 到期）                          // 信号过线了、想驱动设备（规则引擎据它让位）
    private int _followEasing;                        // 正在缓降回中
    private long _followEaseStartMs;
    private double _followEaseRange;                  // 缓降的起点幅度
    private double _followEaseHz;                      // 缓降期间用的频率
    private double _followPhase;                      // 跟随相位 0–1（= 动作语汇「抽插」的内部相位）
    // 降级运行（切窗 / 抓不到画面但目标还在）：0 = 正常，1 = 完全降到慢速小幅。
    // 用混合系数而不是开关，所以「掉信号」和「切回来」都是平滑过渡，不是跳变。
    private double _degradeBlend;
    private string _degradeWhy = "";
    private double _followHz = FollowFallbackHz;      // 当前用的频率
    private double _followDepth;                      // 当前深度 0–1（= 场景强度过线之后的归一化值）
    private double _followMainAxis = 50;              // 最近一次成功下发的 L0 主轴位置 0–100（预览画条用）
    private double _lastFollowPhaseSeconds;           // 上一拍的时刻（抓帧时钟，秒）
    private double _lastCaptureSeconds;               // 上一帧抓取+分析用了多久（= 这份画面信息到手时已经过时多久）
    private int _lastCapturePath;                     // 上一帧走的是哪条抓取路：1 = PrintWindow（后台窗口也行），2 = 屏幕抓取（要窗口没被挡）
    private long _lastFollowSendMs;                   // 最近一次成功下发的时刻（看门狗用，TickCount64）
    private int _followPhaseMisses;                   // 连续几拍相位误差过大（到 FollowPhaseResyncBeats 才重锁）
    private string _followReason = "";                // 白话状态（界面直接显示）
    private System.Threading.Timer? _followWatchdog;  // 独立于抓帧线程的看门狗

    // 逐帧位置跟随（画面同步）：吃 320×180 灰度，抓帧线程上毫秒级、缓冲复用零分配。
    // 质量门不过线时 FollowTickCore 退回原来的「节奏 + 语汇」路径（见 FollowTickCore ⑦）。
    private readonly Lock _manualLock = new();
    private readonly ManualPointTracker _manualTracker = new(PoseWidth, PoseHeight);
    private ManualBindingFrame? _manualPreview;
    private IntPtr _manualWindow;
    private int _manualWidth, _manualHeight;
    private string _manualTarget = "";
    private string _manualProcess = "";
    private long _manualAtMs;
    private int _manualEditing;
    private int _manualFrozen;          // 选择器已定格一帧：后台不要再覆盖这张图
    private readonly ScreenSyncTracker _sync = new();
    private readonly double[] _syncAxes = new double[6];  // 逐帧同步的六轴缓冲（复用，不新建）

    /// <summary>
    /// 交给模型的那张图：一块可复用的位图。<b>尺寸只随窗口变</b>，窗口不变就一直用同一块，
    /// 不会每帧新建大图。
    /// </summary>
    private sealed class ModelFrameBuffer
    {
        public System.Drawing.Bitmap? Bitmap;
        public int Width;
        public int Height;

        /// <summary>拿到一块正好 width×height 的位图（尺寸变了才重建）。</summary>
        public System.Drawing.Bitmap Ensure(int width, int height)
        {
            if (Bitmap is null || Width != width || Height != height)
            {
                Bitmap?.Dispose();
                Bitmap = new System.Drawing.Bitmap(width, height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Width = width;
                Height = height;
            }
            return Bitmap;
        }

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }
    }

    /// <summary>
    /// 交给姿态模型的那张图：一块固定 640×640 的可复用画布（letterbox 的灰底由 PoseTracker 画）。
    /// 尺寸恒定，所以建一次就用到底，不会每 250ms 新建一张大图。
    /// </summary>
    private sealed class PoseFrameBuffer
    {
        public System.Drawing.Bitmap? Bitmap;

        /// <summary>拿到那块 640×640 的画布（只建一次）。</summary>
        public System.Drawing.Bitmap Ensure()
        {
            Bitmap ??= new System.Drawing.Bitmap(PoseTracker.InputSize, PoseTracker.InputSize,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            return Bitmap;
        }

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }
    }

    public ScreenWatchService(AppSettings cfg)
    {
        _cfg = cfg;
        _modelIntervalMs = Math.Clamp(cfg.ScreenWatchModelEveryMs, ModelIntervalMinMs, ModelIntervalMaxMs);
        // 档位是全进程共享的（分类器是静态类）：构造时按设置对齐一次，之后由界面改。
        NsfwClassifier.SetTier(cfg.NsfwModelTier);
        // 登记成「当前实例」：伴随的规则引擎（RuleEngine）要靠它判断画面同步要不要接管设备。
        lock (_instances) _instances.Add(this);
    }

    /// <summary>
    /// 当前那个<b>正在同步</b>的实例（没有正在跑的就返回最后建出来的那个，可能是停止状态；一个都没有则为 null）。
    ///
    /// 为什么是静态的：本服务由「测试页」懒建、跨页面共享，App.xaml.cs 里没有它的字段，
    /// 而规则引擎必须能拿到<b>同一条</b>信号（不然「自动：遥测 &gt; 画面 &gt; 声音」这条优先级就无从判断）。
    /// 改 App.xaml.cs 属于动公共资产，这里用一个静态登记口替代。
    ///
    /// 为什么要留一个列表而不是「最后一次 new 的那个」：自检会临时 new 出好几个服务并 Dispose，
    /// 只记一个的话，自检跑完就会把真正在同步的那个挤掉，伴随那边再也收不到画面同步。
    /// </summary>
    public static ScreenWatchService? Current
    {
        get
        {
            lock (_instances)
            {
                if (_instances.Count == 0) return null;
                // 挑法（顺序即优先级）：
                // ① 正在跑的、不是自检注入句柄的那个 —— 这是唯一「真正在同步用户那个窗口」的实例；
                // ② 正在跑的（哪怕是自检的，也好过没有）；
                // ③ 还没在跑的、不是自检的那个 —— 调用方可以把它启动起来（复用比新建好：
                //    两个实例会各抓一份画面、各下一次指令，设备会乱抖）；
                // ④ 实在没有就返回最后建出来的那个。
                ScreenWatchService? started = null;
                ScreenWatchService? idle = null;
                ScreenWatchService? idleTest = null;
                for (int i = _instances.Count - 1; i >= 0; i--)
                {
                    ScreenWatchService item = _instances[i];
                    bool test = item.ForcedTargetHandleForTest != IntPtr.Zero;
                    if (item.IsRunning)
                    {
                        if (!test) return item;
                        started ??= item;
                    }
                    else if (!test) idle ??= item;
                    else idleTest ??= item;
                }
                return started ?? idle ?? idleTest;
            }
        }
    }

    private static readonly List<ScreenWatchService> _instances = new();

    // ── 对外只读状态（界面只读这些，不碰内部缓冲）────────────────────

    /// <summary>抓取线程是不是还活着。为 false 时界面应当显示「未开启」。</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>当前窗口列表的快照（界面下拉框用）。</summary>
    public IReadOnlyList<ScreenWatchWindow> Windows
    {
        get { lock (_lock) return _windows.ToArray(); }
    }

    /// <summary>当前正在看的窗口的显示名（还没对上任何窗口时为空串）。</summary>
    public string TargetDisplay
    {
        get { lock (_lock) return _target?.DisplayName ?? ""; }
    }

    /// <summary>实测帧率（每秒抓到的帧数），抓不到时是 0。</summary>
    public int Fps => Volatile.Read(ref _fps);

    /// <summary>最近一帧抓到的画面（已 Freeze，可直接给 Image.Source；没抓到或未开启时是 null）。</summary>
    public BitmapSource? PreviewImage
    {
        get { lock (_lock) return _previewImage; }
    }

    public sealed record ManualBindingFrame(BitmapSource Image, byte[] Pixels, IntPtr Handle,
        string Target, string ProcessName, int SourceWidth, int SourceHeight, long AtMs);

    public ManualBindingFrame? ManualPreview
    {
        get { lock (_manualLock) return _manualPreview; }
    }
    public ManualTrackSignal ManualBinding
    {
        get { lock (_manualLock) return _manualTracker.Signal; }
    }
    public void BeginManualBinding()
    {
        lock (_manualLock) { _manualPreview = null; _manualAtMs = 0; }
        Interlocked.Exchange(ref _manualFrozen, 0);
        Interlocked.Exchange(ref _manualEditing, 1);
        StopFollowNow("正在手动选目标：画面跟随暂停");
    }
    public void EndManualBinding()
    {
        Interlocked.Exchange(ref _manualEditing, 0);
        Interlocked.Exchange(ref _manualFrozen, 0);
    }
    /// <summary>选择器点「换一帧」：丢掉定格图，等抓帧线程送来下一张新图。</summary>
    public void RefreshManualPreview()
    {
        lock (_manualLock) { _manualPreview = null; _manualAtMs = 0; }
        Interlocked.Exchange(ref _manualFrozen, 0);
    }
    public bool TryBindManual(ManualBindingFrame frame, ManualTrackPoint point,
        ManualTrackPoint shallow, ManualTrackPoint deep, out string message)
    {
        lock (_manualLock)
        {
            // 固定的是「你正在看的那一帧」，不是「后台最新一帧」：
            // 后台每 4 帧刷新一次预览，拿两帧时间戳比相等会把正常绑定误判成画面已切换。
            if (frame.Handle == IntPtr.Zero || frame.SourceWidth <= 0 || frame.SourceHeight <= 0
                || Environment.TickCount64 - frame.AtMs > 2000)
            { message = "这一帧已经太旧或窗口已经关了：点「换一帧」取新图再选"; return false; }
            if (frame.Target != (_cfg.ScreenWatchTarget ?? "").Trim())
            { message = "画面目标已经换了：点「换一帧」取新图再选"; return false; }
            ScreenWatchWindow? live = CurrentTarget();
            if (live is not null && live.Handle != frame.Handle)
            { message = "画面已切到别的窗口：点「换一帧」取新图再选"; return false; }
            if (!_manualTracker.TryBind(frame.Pixels, point, shallow, deep, out message)) return false;
            _manualWindow = frame.Handle; _manualTarget = frame.Target; _manualProcess = frame.ProcessName;
            _manualWidth = frame.SourceWidth; _manualHeight = frame.SourceHeight;
            _manualAtMs = 0;
            return true;
        }
    }
    public void ClearManualBinding()
    {
        lock (_manualLock) { _manualTracker.Clear(); _manualAtMs = 0; }
    }
    private void TrackManualFrame(ScreenWatchWindow? target, int width, int height)
    {
        lock (_manualLock)
        {
            if (!_manualTracker.Signal.Bound) return;
            bool identityChanged = target?.Handle != _manualWindow || width != _manualWidth || height != _manualHeight
                || _manualTarget != (_cfg.ScreenWatchTarget ?? "").Trim()
                || !string.Equals(target?.ProcessName ?? "", _manualProcess, StringComparison.OrdinalIgnoreCase);
            if (identityChanged) _manualTracker.Invalidate("窗口或尺寸变了，请重绑");
            else _manualTracker.SubmitBgra(_poseBgra);
            _manualAtMs = Environment.TickCount64;
        }
    }

    /// <summary>预览对应的目标窗口显示名（还没对上窗口时是空串）。</summary>
    public string PreviewTarget
    {
        get { lock (_lock) return _previewTarget; }
    }

    /// <summary>预览对应的抓取范围一句话（例如「1920x1080 · 抓整窗」）。</summary>
    public string PreviewRect
    {
        get { lock (_lock) return _previewRect; }
    }

    /// <summary>
    /// 预览要框住的那块分析 ROI（160×90 小图上的像素矩形，X/Y 为左上角、Width/Height 为大小）。
    /// null = 没定位到/退回整幅 —— 界面就不画框。
    /// </summary>
    public (int X, int Y, int Width, int Height)? PreviewRoi
    {
        get
        {
            // 按框裁剪放大时，预览里那一整小块就是框本身，再画一个满屏的矩形没有意义。
            if (Volatile.Read(ref _smallZoomed) != 0) return null;
            if (!TryPoseRoi(out int x0, out int y0, out int x1, out int y1)) return null;
            return (x0, y0, x1 - x0, y1 - y0);
        }
    }

    /// <summary>肤色像素占整幅的比例 0–1（已平滑）。</summary>
    public double SkinRatio => Volatile.Read(ref _skinRatio);
    /// <summary>肤色像素占中心 60% 区域的比例 0–1（已平滑）。主体大多在这里，所以它比整幅更能说明问题。</summary>
    public double SkinCenterRatio => Volatile.Read(ref _skinCenterRatio);

    /// <summary>中心区域相邻帧的平均绝对亮度差，归一化到 0–1。</summary>
    public double Motion => Volatile.Read(ref _motion);
    /// <summary>周期性运动强度 0–1：最近约 3.2 秒的运动里有没有 0.5–3Hz 的规律起伏。</summary>
    public double Rhythm => Volatile.Read(ref _rhythm);
    /// <summary>
    /// 场景强度 0–1 —— 界面判定灯用的就是它。
    /// 识别模型就绪时 = <b>0.7 × 模型分 + 0.3 × 手工特征分</b>；模型没就绪或没装时 = 手工特征分（和加模型之前一样）。
    /// 两种情况都有呻吟事件再 ×1.25，最后都做同样的快起慢落平滑。
    /// </summary>
    public double SceneScore => Volatile.Read(ref _sceneScore);

    /// <summary>手工特征分 0–1（中央肤色占比 × 节奏加权，已平滑）—— 只给界面做对照，不参与判定以外的用途。</summary>
    public double FeatureScore => Volatile.Read(ref _featureScore);

    /// <summary>最近 1 秒内有没有人声/呻吟事件（直接读声音那条链的检测结果，不自己重做音频分析）。</summary>
    public bool VoiceEvent => Volatile.Read(ref _voiceEvent) != 0;
    /// <summary>场景强度是否超过判定线（带迟滞）。</summary>
    public bool Suspect => Volatile.Read(ref _suspect) != 0;

    /// <summary>抓不到画面（全黑/全白/窗口最小化/找不到窗口）。</summary>
    public bool CaptureFailed => Volatile.Read(ref _captureFailed) != 0;

    /// <summary>入场闸门（<c>ScreenWatchService</c> ⑤）那一刻的判定量，只给 /status 排障用。
    /// <c>Blob</c> = 主体块这一拍有没有信号（没有骨架的画面靠它驱动），后四个是它当前的包围盒（0–1 归一化）。</summary>
    public (bool Suspect, bool CaptureFailed, string Failure, bool PoseLive, bool Blob,
            double BlobX0, double BlobY0, double BlobX1, double BlobY1) FollowGateDiagnostics =>
        (Volatile.Read(ref _suspect) != 0, Volatile.Read(ref _captureFailed) != 0, _failureReason,
         _cfg.PoseVisionEnabled && _poseMotion.TryRead(out _, out _),
         TryReadSubjectBlob(out _, out _),
         Math.Round(_blobX0, 3), Math.Round(_blobY0, 3), Math.Round(_blobX1, 3), Math.Round(_blobY1, 3));
    /// <summary>抓不到画面的原因（白话，直接给用户看）。</summary>
    public string FailureReason => _failureReason;

    /// <summary>进入判定的阈值（= 0.38 × 灵敏度系数）。界面把它画在条上，用户才知道离判定线多远。</summary>
    public double ThresholdOn =>
        Math.Clamp(SceneThresholdBase * SensitivityFactor(), 0.06, 0.95);
    /// <summary>退出判定的阈值（低于它才收回「疑似」）：迟滞，防止在临界线上反复跳。</summary>
    public double ThresholdOff => ThresholdOn * SceneThresholdRelease;

    /// <summary>已经分析过的帧数（诊断用）。</summary>
    public long FrameCount => Volatile.Read(ref _frameCount);

    // ── 识别模型（本地）对外状态 ──────────────────────────────────────

    /// <summary>模型分是不是已经在参与判定（true 才走 0.7 的融合权重）。</summary>
    public bool ModelReady => Volatile.Read(ref _modelReady) != 0;
    /// <summary>平滑后的「这段画面是情色内容的概率」0–1；模型没就绪时是 0。</summary>
    public double ModelScore => Volatile.Read(ref _modelScore);
    /// <summary>一次推理的耗时（毫秒，实测 81–97ms）。没跑过时为 0。</summary>
    public long LastInferenceMs => Interlocked.Read(ref _lastInferenceMs);

    /// <summary>模型文件是否已经下载且完整（够大）。</summary>
    public bool ModelInstalled => NsfwClassifier.IsInstalled;
    /// <summary>当前档位模型文件占了多少字节（0 = 没下载）。</summary>
    public long ModelBytes => NsfwClassifier.InstalledBytes;
    /// <summary>本机所有档位的模型一共占了多少磁盘（删除按钮要如实告诉用户能腾出多少）。</summary>
    public long ModelDiskBytes => NsfwClassifier.TotalInstalledBytes;
    /// <summary>下载 / 加载 / 删除有没有失败过（界面据此把状态标成警告色）。</summary>
    public bool ModelFailed => NsfwClassifier.HasError;
    /// <summary>正在下载模型（按钮要置灰、进度条要露出来）。</summary>
    public bool ModelDownloading => Volatile.Read(ref _modelDownloading) != 0;

    /// <summary>当前档位（"s" / "m" / "l"）。</summary>
    public string ModelTier => NsfwClassifier.Variant.Id;
    /// <summary>当前档位的中文名（"小档" / "中档" / "大档"）。</summary>
    public string ModelTierName => NsfwClassifier.TierName(NsfwClassifier.Variant.Id);
    /// <summary>当前档位的体积（"44MB"）。</summary>
    public string ModelSizeText => NsfwClassifier.Variant.SizeText;
    /// <summary>当前档位的说明（体积 / 自报准确率 / 实测耗时），界面写在小字里。</summary>
    public string ModelTierHint => $"{NsfwClassifier.Variant.Label}：{NsfwClassifier.Variant.HintText}";
    /// <summary>最近一次推理是不是被判成「血腥暴力压过情色」（界面要如实说明为什么这一帧不算数）。</summary>
    public bool ModelViolent => ModelReady && NsfwClassifier.LastWasViolent;

    /// <summary>
    /// 换档位（小 / 中 / 大）。不同档位是不同文件，换档后如果本机没有这一档的模型，
    /// 界面要提示重新下载。已经加载的旧档会话会被释放，模型分立刻归零（判定先退回手工特征）。
    /// </summary>
    public void SetModelTier(string? tierId)
    {
        if (string.Equals(NsfwClassifier.Resolve(tierId).Id, NsfwClassifier.Variant.Id, StringComparison.Ordinal))
            return;
        NsfwClassifier.SetTier(tierId);
        Volatile.Write(ref _modelReady, 0);
        Volatile.Write(ref _modelScore, 0);
        Interlocked.Exchange(ref _lastInferenceMs, 0);
        _lastModelJobMs = 0;    // 换了模型：下一次到点就重新排帧
    }

    /// <summary>
    /// 模型状态（白话，直接显示给用户）：
    /// 「没下载（中档 44MB，点下面的按钮下这一档）」/「下载中 42%」/
    /// 「已就绪 · 中档 · 每帧 15ms · 约 3 次/秒」/「加载失败：原因」。
    /// </summary>
    public string ModelStatus
    {
        get
        {
            if (ModelReady)
            {
                long ms = Math.Max(1, LastInferenceMs);
                // 实际频率取「模型跑得多快」和「我们多勤快地问它」里更小的那个。
                double rate = Math.Min(1000.0 / _modelIntervalMs, 1000.0 / ms);
                return $"已就绪 · {NsfwClassifier.TierName(ModelTier)} · 每帧 {ms}ms · 约 {rate:0} 次/秒";
            }
            if (NsfwClassifier.IsReady)
                return IsRunning ? "模型已加载，正在算第一帧…" : "模型已加载（开启同步后开始判定）";
            return NsfwClassifier.Status;
        }
    }

    /// <summary>
    /// 下载识别模型（当前档位，小 22MB / 中 44MB / 大 106MB）。**只由界面上的「下载模型」按钮调用** —— 本类不会自己发起下载。
    /// 已经装好就直接返回 true；失败会把原因写进 <see cref="ModelStatus"/>，半截文件自动清理。
    /// </summary>
    public async Task<bool> DownloadModelAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _modelDownloading, 1) != 0) return false;   // 已经在下了，别开第二条
        try
        {
            return await NsfwClassifier.DownloadAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _modelDownloading, 0);
        }
    }

    /// <summary>删掉本机模型文件（腾空间）。之后判定退回手工特征，界面上的模型分归零。</summary>
    public bool DeleteModel()
    {
        bool ok = NsfwClassifier.Delete();
        if (ok)
        {
            Volatile.Write(ref _modelReady, 0);
            Volatile.Write(ref _modelScore, 0);
            Interlocked.Exchange(ref _lastInferenceMs, 0);
        }
        return ok;
    }

    // ── 姿态（运动主体）对外状态 ──────────────────────────────────────

    /// <summary>设置里有没有打开「用姿态定位运动主体」（默认开：定位不到会自动退回整幅画面）。</summary>
    public bool PoseEnabled => _cfg.ScreenUsePose;

    /// <summary>这一刻的 ROI 是不是有效（为真时运动量/节奏正在主体那一块里算）。</summary>
    public bool PoseLocated => Volatile.Read(ref _poseLocated) != 0;

    /// <summary>定位到的运动主体的置信度 0–1（有效关键点的平均置信度）。</summary>
    public double PoseConfidence => Volatile.Read(ref _poseConfidence);

    /// <summary>定位用到的关键点个数（少于 6 个就当没定位到）。</summary>
    public int PoseKeypoints => Volatile.Read(ref _poseKeypoints);

    /// <summary>姿态模型文件是否已下载且完整。</summary>
    public bool PoseModelInstalled => PoseTracker.IsInstalled;
    /// <summary>姿态模型文件占了多少字节（0 = 没下载）。</summary>
    public long PoseModelBytes => PoseTracker.InstalledBytes;
    /// <summary>姿态模型下载/加载有没有失败过。</summary>
    public bool PoseModelFailed => PoseTracker.HasError;
    /// <summary>正在下载姿态模型（按钮要置灰、进度条要露出来）。</summary>
    public bool PoseDownloading => Volatile.Read(ref _poseDownloading) != 0;
    /// <summary>一次姿态推理的耗时（毫秒）。</summary>
    public long PoseInferenceMs => Interlocked.Read(ref _poseInferenceMs);

    /// <summary>姿态模型的白话状态（界面直接显示）：「没下载（13MB）」/「已就绪 · 每 25ms 一次」/「加载失败：原因」。</summary>
    public string PoseModelStatus
    {
        get
        {
            if (!PoseEnabled) return "已关掉（运动量按整幅画面算）";
            if (PoseTracker.IsReady) return $"已就绪 · 每 {Math.Max(1, PoseInferenceMs)}ms 一次";
            return PoseTracker.Status;
        }
    }

    /// <summary>
    /// 「运动主体」那一行的白话状态：已定位（置信度 x）/ 未定位（用整幅画面）+ 为什么。
    /// 界面每 150ms 读一次，所以这里绝不能有副作用、也不能读盘。
    /// </summary>
    public string PoseSubjectStatus
    {
        get
        {
            if (!PoseEnabled) return "未定位（用整幅画面）· 姿态定位已关掉";
            if (PoseLocated) return $"已定位（置信度 {PoseConfidence:0.00}）· 只在主体那一块算运动量";
            if (!IsRunning) return "未定位（用整幅画面）";
            if (!PoseTracker.IsInstalled) return "未定位（用整幅画面）· 姿态模型没下载";
            if (PoseTracker.HasError) return $"未定位（用整幅画面）· {PoseTracker.Status}";
            if (Volatile.Read(ref _poseEverWorked) == 0) return "正在算第一帧（还没出结果）";
            return "未定位（用整幅画面）· 这一帧里没找到人";
        }
    }

    /// <summary>
    /// 下载姿态模型（13MB）。**只由界面上的按钮调用** —— 本类不会自己发起下载。
    /// 装好之后不用重开观察：下一次排帧就会用上。
    /// </summary>
    public async Task<bool> DownloadPoseModelAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _poseDownloading, 1) != 0) return false;   // 已经在下了，别开第二条
        try
        {
            return await PoseTracker.DownloadAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _poseDownloading, 0);
        }
    }

    /// <summary>删掉姿态模型（腾空间）。之后运动量自动回到整幅画面。</summary>
    public bool DeletePoseModel()
    {
        bool ok = PoseTracker.Delete();
        if (ok) ClearPoseState();
        return ok;
    }

    // ── 画面同步（驱动设备）对外状态 ─────────────────────────────────

    /// <summary>这一刻设备是不是正由画面同步驱动。</summary>
    public bool FollowActive => Volatile.Read(ref _followActive) != 0;

    /// <summary>
    /// 画面同步此刻「想要」驱动设备 —— <b>只看信号本身</b>：来源允许 + 目标在前台 + 窗口可用 +
    /// 场景强度过线 + 没有脚本/桥/遥测在占设备。不含「有没有真的拿到控制权」。
    ///
    /// 为什么要单独暴露这一条：规则引擎必须在本类真的拿到设备<b>之前</b>就让位
    /// （伴随的自动动作不停，本类的直接下发会被 MotionEngine 拒掉，两边就会永远互相等）。
    /// </summary>
    public bool FollowWantsDevice =>
        Volatile.Read(ref _followWant) != 0
        || Volatile.Read(ref _followActive) != 0
        || Volatile.Read(ref _followEasing) != 0;

    /// <summary>跟随的深度 0–1（场景强度过线之后归一化，界面画条用）。</summary>
    public double FollowDepth => Volatile.Read(ref _followDepth);

    /// <summary>跟随用的频率（Hz）——速度就由它决定。</summary>
    public double FollowHz => Volatile.Read(ref _followHz);

    /// <summary>跟随方向量：+1 = 主体/画面在靠近（往「推」偏），-1 = 在退开（往「收」偏），0 = 没动。</summary>
    public double FollowApproach => Volatile.Read(ref _followApproach);

    /// <summary>最近一次成功下发的主轴（L0）位置 0–100（预览右下角那条横条画它）。</summary>
    public double FollowMainAxis => Volatile.Read(ref _followMainAxis);

    /// <summary>跟随状态的白话说明（为什么没在动 / 正在跟什么）。没在跟随时为空串。</summary>
    public string FollowReason => Volatile.Read(ref _followReason);

    /// <summary>姿态信号源的诊断快照：交帧数 / 真跑推理的帧数 / 检出人的帧数 / 算出信号的帧数 / 线程在不在 / 当前深度。
    /// 这四项一列，「姿态开了但不动」就能立刻分清是没喂帧、推理跟不上，还是画面里根本没人。</summary>
    public (int Submit, int Detect, int Person, int Samples, bool Running, double Depth) PoseDiagnostics
        => (_poseMotion.SubmitCount, _poseMotion.DetectCount, _poseMotion.PersonCount,
            _poseMotion.SampleCount, _poseMotion.IsRunning, _poseMotion.LastDepth);

    // ── 生命周期 ──────────────────────────────────────────────────────

    /// <summary>
    /// 开始观察（幂等：已经在跑就什么都不做）。目标来自 <see cref="AppSettings.ScreenWatchTarget"/>
    /// （窗口标题或进程名），对不上任何窗口时会如实报「找不到那个窗口」而不是假装在看。
    /// </summary>
    public void Start()
    {
        if (_disposed) return;
        Refresh();                       // 先枚举一次窗口，保证上次选的那个还在（枚举很快，只在启动时做）
        lock (_lock)
        {
            if (_thread is { IsAlive: true }) return;
            _stopRequested = false;
            ResetAnalysis();
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,                  // 后台线程：App 退出时不会吊住进程
                Name = "Hexa 画面同步",
                Priority = ThreadPriority.BelowNormal, // 礼貌一点：抓画面不该影响设备通信和界面
            };
            _thread = thread;
            thread.Start();
        }
        StartModelThread();
        StartPoseThread();
        StartFollowWatchdog();
        // 隐私：窗口标题会暴露「用户在看什么」。默认只记进程名，标题一律不写进日志；
        // 要连标题一起记的，去设置页打开「记录窗口标题」（AppSettings.LogWindowTitles）。
        // 读 _cfg 而不是 App.Settings：服务的行为必须只由「构造它时给的那份设置」决定
        //（自检/渲染探针会传入另一份设置，写死 App.Settings 会让它们串味）。
        string shown = TargetDisplay;
        if (!_cfg.LogWindowTitles && shown.Length > 0)
        {
            int sep = shown.IndexOf(" · ", StringComparison.Ordinal);
            shown = sep < 0 ? "(窗口名已隐藏)" : shown[..sep] + " (标题已隐藏)";
        }
        AppLogger.Info($"画面同步已启动：{(shown.Length == 0 ? "(还没对上窗口)" : shown)}");
    }

    /// <summary>
    /// 起识别模型的线程（幂等）。线程起来后先睡着 —— 没装模型时它一个字节都不碰、也不占 CPU；
    /// 抓帧线程每 <see cref="_modelIntervalMs"/> 才叫醒它一次。
    /// </summary>
    private void StartModelThread()
    {
        if (_disposed) return;
        if (_modelThread is { IsAlive: true }) return;
        lock (_modelSync)
        {
            if (_modelThread is { IsAlive: true }) return;
            _modelStopRequested = false;
            try
            {
                _modelWake ??= new AutoResetEvent(false);
                var thread = new Thread(ModelWorkerLoop)
                {
                    IsBackground = true,
                    Name = "Hexa 画面识别",
                    Priority = ThreadPriority.BelowNormal,   // 和抓帧一样礼貌：别抢设备通信和界面的核
                };
                _modelThread = thread;
                thread.Start();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"画面同步：识别模型线程起不来（判定退回手工特征）—— {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 停止同步并等线程真的退出（最多等 0.7 秒；极端情况下 PrintWindow 卡住时线程是后台线程，不会吊住界面）。
    /// 停下之前先把设备收尾：画面同步会缓降回中并交出控制权，绝不留着设备挂在某个位置上。
    /// </summary>
    public void Stop()
    {
        Thread? thread;
        Thread? modelThread;
        Thread? poseThread;
        lock (_lock)
        {
            _stopRequested = true;
            thread = _thread;
        }
        _modelStopRequested = true;
        lock (_modelSync) modelThread = _modelThread;
        _poseStopRequested = true;
        lock (_poseSync) poseThread = _poseThread;
        try { _modelWake?.Set(); } catch { /* 事件已经关了：无所谓 */ }
        try { _poseWake?.Set(); } catch { /* 事件已经关了：无所谓 */ }

        // 先让设备停下来再等线程：抓帧线程可能正卡在 PrintWindow 里几秒不回来，
        // 那几秒里设备不能还停在画面同步留下的位置上。
        StopFollowNow("画面同步已停止");
        lock (_manualLock) _manualPreview = null;

        if (thread is not null && thread.IsAlive)
        {
            try { thread.Join(700); }
            catch (Exception ex) { AppLogger.Warn($"画面同步：等线程收尾时出错 —— {ex.Message}"); }
        }
        if (modelThread is not null && modelThread.IsAlive)
        {
            try { modelThread.Join(700); }
            catch (Exception ex) { AppLogger.Warn($"画面同步：等模型线程收尾时出错 —— {ex.Message}"); }
        }
        if (poseThread is not null && poseThread.IsAlive)
        {
            try { poseThread.Join(700); }
            catch (Exception ex) { AppLogger.Warn($"画面同步：等姿态线程收尾时出错 —— {ex.Message}"); }
        }
        StopFollowWatchdog();
        ClearPoseState();
        _poseMotion.Reset();                      // 作废姿态运动的信号：下次开起来重新算，别用几秒前的旧位移
        Volatile.Write(ref _fps, 0);
        ResetAnalysis();
        AppLogger.Info("画面同步已停止");
    }

    /// <summary>
    /// 重新枚举窗口并重新对上目标（换窗口、开/关窗口之后调用）。
    /// 正在同步时也会立即换到新目标，不用先关再开。
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        List<ScreenWatchWindow> list;
        try { list = EnumerateWindows(); }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面同步：枚举窗口失败 —— {ex.Message}");
            return;
        }
        lock (_lock)
        {
            _windows.Clear();
            _windows.AddRange(list);
            ResolveTargetLocked();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _poseMotion.Dispose();                    // 停掉它自己的推理线程（等最多 0.5 秒）
        lock (_lock)
        {
            _sourceBitmap?.Dispose();
            _sourceBitmap = null;
            _smallBitmap?.Dispose();
            _smallBitmap = null;
        }
        // 模型的缓冲只能在模型线程确实退出之后再放（还在读的位图被 Dispose 会直接抛异常）。
        lock (_modelSync)
        {
            if (_modelThread is not { IsAlive: true })
            {
                foreach (ModelFrameBuffer buffer in _modelFrames) buffer.Dispose();
                try { _modelWake?.Dispose(); } catch { /* 忽略 */ }
                _modelWake = null;
                _modelThread = null;
            }
        }
        // 姿态的画布同理：姿态线程还在跑就交给它自己收（那块位图可能正在被读）。
        lock (_poseSync)
        {
            if (_poseThread is not { IsAlive: true })
            {
                foreach (PoseFrameBuffer buffer in _poseFrames) buffer.Dispose();
                try { _poseWake?.Dispose(); } catch { /* 忽略 */ }
                _poseWake = null;
                _poseThread = null;
            }
        }
        // 注销登记：伴随的规则引擎不该再拿到一个已经废掉的服务。
        lock (_instances) _instances.Remove(this);
    }

    // ── 窗口枚举 ──────────────────────────────────────────────────────

    /// <summary>
    /// 列出当前可见的顶层窗口（标题 + 进程名 + 是否最小化）。
    /// 过滤掉：没有标题的、尺寸小于 200×150 的、自己这个进程的、被 DWM 隐藏的（UWP 常见）。
    /// </summary>
    public static List<ScreenWatchWindow> EnumerateWindows()
    {
        var list = new List<ScreenWatchWindow>();
        uint selfPid = (uint)Environment.ProcessId;
        try
        {
            // 委托必须活到 EnumWindows 返回（写成局部变量再传，别直接写内联 lambda 给 P/Invoke）。
            EnumWindowsProc callback = (hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    int length = GetWindowTextLengthW(hwnd);
                    if (length <= 0) return true;
                    // 标题长度封顶：个别程序会报出很长的标题，没必要为它分配大缓冲（显示时还会再截）
                    var buffer = new StringBuilder(Math.Min(length, 512) + 1);
                    if (GetWindowTextW(hwnd, buffer, buffer.Capacity) <= 0) return true;
                    string title = buffer.ToString().Trim();
                    if (title.Length == 0) return true;

                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0 || pid == selfPid) return true;
                    if (IsCloaked(hwnd)) return true;
                    if (!GetWindowRect(hwnd, out RECT rect)) return true;

                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    if (width < MinWindowWidth || height < MinWindowHeight) return true;

                    list.Add(new ScreenWatchWindow(hwnd, title, ProcessNameOf(pid), width, height, IsIconic(hwnd)));
                }
                catch
                {
                    // 单个窗口读失败就跳过它，别让整次枚举失败（窗口随时可能在关闭）
                }
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面同步：EnumWindows 失败 —— {ex.Message}");
        }
        return list;
    }

    private static string ProcessNameOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName ?? "";
        }
        catch
        {
            return "";   // 进程已经退出 / 没权限：进程名留空，界面上仍然有标题
        }
    }

    /// <summary>DWM 认为这个窗口被「藏起来」了（UWP 应用会保留一堆这样的窗口，列出来只会干扰用户）。</summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        try { return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0; }
        catch { return false; }
    }

    /// <summary>把设置里的「窗口标题或进程名」对到当前真实存在的窗口上；对不上就返回 null。</summary>
    /// <summary>
    /// 仅供自检：直接指定要抓的窗口句柄（跳过"按标题/进程匹配"这一步）。
    /// 存在的理由：服务会主动过滤掉**自己进程**的窗口（避免抓到自己），
    /// 而自检唯一能保证存在的窗口就是 Hexa 自己 —— 不给这个口子，抓取链路就没法自动验证。
    /// </summary>
    internal IntPtr ForcedTargetHandleForTest { get; set; }

    private void ResolveTargetLocked()
    {
        _target = null;
        string want = (_cfg.ScreenWatchTarget ?? "").Trim();
        if (want.Length == 0) return;

        ScreenWatchWindow? best = null;
        int bestScore = 0;
        foreach (ScreenWatchWindow window in _windows)
        {
            int score = MatchScore(window, want);
            if (score <= 0) continue;
            // 同分时取面积大的那个（同一个进程开了多个窗口时，视频/游戏窗口总是更大的那个）
            bool bigger = best is not null && (long)window.Width * window.Height > (long)best.Width * best.Height;
            if (score > bestScore || (score == bestScore && bigger))
            {
                best = window;
                bestScore = score;
            }
        }
        _target = best;
    }

    /// <summary>匹配优先级：标题完全相同 &gt; 标题包含 &gt; 进程名相同 &gt; 进程名包含。</summary>
    private static int MatchScore(ScreenWatchWindow window, string want)
    {
        if (string.Equals(window.Title, want, StringComparison.OrdinalIgnoreCase)) return 4;
        if (window.Title.Contains(want, StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(window.ProcessName, want, StringComparison.OrdinalIgnoreCase)) return 2;
        if (window.ProcessName.Contains(want, StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    // ── 抓取线程 ──────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        var clock = Stopwatch.StartNew();
        int skip = 0;
        try
        {
            while (!_stopRequested)
            {
                long start = clock.ElapsedMilliseconds;
                try
                {
                    // 上一帧太慢：这几帧只睡觉，把 CPU 让出去。
                    // 必须同时作废上一帧的亮度基准：恢复后的第一帧如果拿隔了几拍的
                    // _prevLuma 去减，运动量会被凭空放大（节奏上多出一下并不存在的尖峰）。
                    if (skip > 0) { skip--; _hasPrevFrame = false; }
                    else
                    {
                        long captureStart = clock.ElapsedMilliseconds;
                        CaptureAndAnalyze(clock);
                        _lastCaptureSeconds = (clock.ElapsedMilliseconds - captureStart) / 1000.0;
                    }
                }
                catch (Exception ex)
                {
                    // 线程绝不能因为一帧出错就死掉（界面还写着「正在看」，那才是真的骗人）。
                    SetFailure($"抓取这一帧出错（{ex.GetType().Name}），正在重试");
                    AppLogger.Warn($"画面同步：抓取异常已跳过 —— {ex.Message}");
                }

                // 每拍都跑一次画面同步 —— 成功、抓不到画面、这一帧出错，三种情况都要覆盖：
                // 「信号断了就缓降回中再交还控制权」这条兜底，正因为在这里才管得住抓不到画面的那些帧。
                FollowTick(clock);

                if (_stopRequested) break;
                long elapsed = clock.ElapsedMilliseconds - start;
                if (elapsed > SlowFrameMs) skip = SlowFrameSkip;    // 自适应降频
                Thread.Sleep((int)Math.Clamp(FrameIntervalMs - elapsed, 1, FrameIntervalMs));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("画面同步线程异常退出", ex);
        }
        finally
        {
            Volatile.Write(ref _fps, 0);
            _hasPrevFrame = false;
        }
    }

    private void CaptureAndAnalyze(Stopwatch clock)
    {
        if (_cfg.AutoFollowScreen) TryAutoBindForeground(clock);

        ScreenWatchWindow? target = CurrentTarget();
        if (target is null)
        {
            // 目标可能刚被关掉或改了标题：每秒重找一次，找不到就如实说原因。
            if (clock.ElapsedMilliseconds - _lastResolveMs > ResolveRetryMs)
            {
                _lastResolveMs = clock.ElapsedMilliseconds;
                Refresh();
                target = CurrentTarget();
            }
            if (target is null && ForcedTargetHandleForTest == IntPtr.Zero)
            {
                SetFailure(string.IsNullOrWhiteSpace(_cfg.ScreenWatchTarget)
                    ? "还没选窗口：在上面那个下拉框里挑一个正在放东西的窗口"
                    : "找不到那个窗口了：它可能已经关掉或改了名字，重新挑一个");
                Thread.Sleep(200);
                return;
            }
        }

        // 上面允许了"自检注入句柄时 target 为 null"，所以这里不能直接解引用 target
        //（可空分析会报 CS8602；本项目要求 0 警告）。
        IntPtr hwnd = ForcedTargetHandleForTest != IntPtr.Zero
            ? ForcedTargetHandleForTest
            : (target?.Handle ?? IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            SetFailure("还没选窗口：在上面那个下拉框里挑一个正在放东西的窗口");
            Thread.Sleep(200);
            return;
        }
        if (!IsWindow(hwnd))
        {
            lock (_lock) _target = null;   // 窗口没了：下一轮重新对
            SetFailure("那个窗口已经关掉了：重新挑一个");
            return;
        }
        if (IsIconic(hwnd)) { SetFailure("那个窗口被最小化了：先把它还原到桌面上"); return; }
        if (!GetWindowRect(hwnd, out RECT rect)) { SetFailure(DefaultFailureText); return; }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 32 || height < 32) { SetFailure("那个窗口太小了（可能被折叠或最小化）：先把它还原"); return; }

        // 超大窗口保护：源图按 4096 封顶。再大的只有"双屏拼起来的一个窗口"这种极少数情况，
        // 截左上角那块照样看得出画面在干什么，但省掉了每帧上百 MB 的位图。
        int sourceWidth = Math.Min(width, MaxSourceSize);
        int sourceHeight = Math.Min(height, MaxSourceSize);

        // ① 首选 PrintWindow(PW_RENDERFULLCONTENT)：窗口不用露在最前面。
        if (PrintWindowToSource(hwnd, sourceWidth, sourceHeight))
        {
            DownscaleToSmall(sourceWidth, sourceHeight);
            if (!IsBlankFrame()) { Volatile.Write(ref _lastCapturePath, 1); AnalyzeFrame(clock); TrackManualFrame(target, width, height); MarkCaptured(clock); PublishPreview(target, width, height); return; }
            // 抓到全黑/全白 = 这条路对这个窗口不管用，往下走屏幕抓取。
        }

        // ② 退化：按窗口矩形从屏幕 DC 抓（等价 BitBlt + SRCCOPY）。
        //    代价：窗口必须露在桌面上，被别的窗口挡住就会抓到挡住它的东西。
        if (ScreenCopyToSource(rect, sourceWidth, sourceHeight))
        {
            DownscaleToSmall(sourceWidth, sourceHeight);
            if (!IsBlankFrame()) { Volatile.Write(ref _lastCapturePath, 2); AnalyzeFrame(clock); TrackManualFrame(target, width, height); MarkCaptured(clock); PublishPreview(target, width, height); return; }
        }

        SetFailure(DefaultFailureText);
        ResetFrameState();   // 别拿很久以前的上一帧跟这一帧比运动量（会凭空跳一下）
    }

    private ScreenWatchWindow? CurrentTarget()
    {
        lock (_lock) return _target;
    }

    private void MarkCaptured(Stopwatch clock)
    {
        long now = clock.ElapsedMilliseconds;
        if (_lastFrameMs > 0)
        {
            double interval = Math.Clamp(now - _lastFrameMs, 20, 500);
            _frameIntervalEmaMs += (interval - _frameIntervalEmaMs) * 0.15;
            Volatile.Write(ref _fps, (int)Math.Round(1000.0 / Math.Max(20, _frameIntervalEmaMs)));
        }
        _lastFrameMs = now;
        ClearFailure();
    }

    /// <summary>
    /// 把最近一帧的 160×90 小图拷成 WPF 的 BitmapSource 给界面预览用。
    /// 每 4 帧才更新一次（20fps 下约 5 次/秒），省掉不必要的像素拷贝与对象创建；
    /// 像素拷贝和 Freeze 都在锁外做完，锁里只换引用，绝不在锁里做慢操作。
    /// </summary>
    private void PublishPreview(ScreenWatchWindow? target, int width, int height)
    {
        if ((_previewFrameCounter++ & 3) != 0) return;   // 节流：每 4 帧更新一次

        BitmapSource frame;
        try
        {
            // _bgra 就是 DownscaleToSmall 里从 _smallBitmap 拷出来的那 160×90 像素（BGRA，4 字节/像素），
            // 顺序正好对上 WPF 的 Bgra32；拷贝一份新数组再建图，避免后续帧把这块缓冲覆盖掉。
            var pixels = new byte[FrameWidth * FrameHeight * 4];
            Array.Copy(_bgra, pixels, pixels.Length);
            frame = BitmapSource.Create(FrameWidth, FrameHeight, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, FrameWidth * 4);
            frame.Freeze();                              // 冻结后跨线程读安全，界面线程随便用
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面同步：生成预览帧失败 —— {ex.Message}");
            return;
        }

        // 整窗大图只在打开选择器或已有手动目标时分配；自动模式维持原来的轻量预览开销。
        // 选择器定格之后不再覆盖：用户点的那张图必须和确认时是同一张。
        bool editing = Volatile.Read(ref _manualEditing) != 0;
        if (editing && Volatile.Read(ref _manualFrozen) != 0) return;
        if (editing || ManualBinding.Bound)
        {
            var manualPixels = (byte[])_poseBgra.Clone();
            var manualImage = BitmapSource.Create(PoseWidth, PoseHeight, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, manualPixels, PoseWidth * 4);
            manualImage.Freeze();
            lock (_manualLock)
            {
                _manualPreview = new(manualImage, manualPixels, target?.Handle ?? IntPtr.Zero,
                    (_cfg.ScreenWatchTarget ?? "").Trim(), target?.ProcessName ?? "", width, height, Environment.TickCount64);
            }
            if (editing) Interlocked.Exchange(ref _manualFrozen, 1);
        }

        string rectText = $"{width}x{height} · 抓整窗";
        string targetText = target?.DisplayName ?? "";

        lock (_lock)
        {
            _previewImage = frame;
            _previewTarget = targetText;
            _previewRect = rectText;
        }
    }

    // ── 抓取的两条路（GDI 对象一律 try/finally 释放）────────────────────

    private void EnsureBitmaps(int width, int height)
    {
        if (_sourceBitmap is null || _sourceWidth != width || _sourceHeight != height)
        {
            _sourceBitmap?.Dispose();
            _sourceBitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            _sourceWidth = width;
            _sourceHeight = height;
        }
        _smallBitmap ??= new System.Drawing.Bitmap(FrameWidth, FrameHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        _poseBitmap ??= new System.Drawing.Bitmap(PoseWidth, PoseHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    }

    private bool PrintWindowToSource(IntPtr hwnd, int width, int height)
    {
        EnsureBitmaps(width, height);
        try
        {
            using var graphics = System.Drawing.Graphics.FromImage(_sourceBitmap!);
            IntPtr hdc = graphics.GetHdc();
            try { return PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
            finally { graphics.ReleaseHdc(hdc); }        // HDC 必须还回去，否则 GDI 句柄会一直涨
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面同步：PrintWindow 失败，改用屏幕抓取 —— {ex.Message}");
            return false;
        }
    }

    private bool ScreenCopyToSource(RECT rect, int width, int height)
    {
        EnsureBitmaps(width, height);
        try
        {
            using var graphics = System.Drawing.Graphics.FromImage(_sourceBitmap!);
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
            return true;
        }
        catch (Exception ex)
        {
            // 窗口有一部分在屏幕外 / 副屏拔掉了 / 被远程桌面限制，都可能抛在这里
            AppLogger.Warn($"画面同步：屏幕抓取失败 —— {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 一步缩到 160×90，再把像素拷进复用的 byte[]。
    /// 插值模式特意用 Bilinear 而不是 Low（最近邻）：这是 12:1 的缩小，最近邻等于「每 144 个像素里
    /// 只挑一个」—— 视频里一点点位移就会让挑中的像素整片换掉，运动量会被虚高成"闪烁"。
    /// Bilinear 是 2×2 加权平均，能把这种采样噪声压下去，代价在 1080p 源上也只有几毫秒（预算 60ms）。
    /// </summary>
    private void DownscaleToSmall(int width, int height)
    {
        // 手动框选时只把框里那块放大到 160×90：用户框出来的往往只占窗口两三成（实测 0.228×0.356
        // 在整幅缩到 160×90 之后只剩 36×32 像素），动作细节全被平均掉，才显得「框了也识别不准」。
        // 框内铺满之后同样的动作有约 4–8 倍的像素在工作，运动量和相位都稳得多。
        int srcX = 0, srcY = 0, srcW = width, srcH = height;
        bool zoomed = false;
        if (TryManualRoiNorm(out double nx, out double ny, out double nw, out double nh) && (nw < 0.92 || nh < 0.92))
        {
            int cx = Math.Clamp((int)Math.Round(nx * width), 0, width - 2);
            int cy = Math.Clamp((int)Math.Round(ny * height), 0, height - 2);
            int cw = Math.Clamp((int)Math.Round(nw * width), 8, width - cx);
            int ch = Math.Clamp((int)Math.Round(nh * height), 8, height - cy);
            if (cw >= 16 && ch >= 12) { srcX = cx; srcY = cy; srcW = cw; srcH = ch; zoomed = true; }
        }
        Volatile.Write(ref _smallZoomed, zoomed ? 1 : 0);

        var small = _smallBitmap!;
        using (var graphics = System.Drawing.Graphics.FromImage(small))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImage(_sourceBitmap!,
                new System.Drawing.Rectangle(0, 0, FrameWidth, FrameHeight),
                srcX, srcY, srcW, srcH, System.Drawing.GraphicsUnit.Pixel);
        }

        // LockBits + Marshal.Copy：绝不能在这个循环里用 GetPixel（慢几十倍）。
        System.Drawing.Imaging.BitmapData data = small.LockBits(
            new System.Drawing.Rectangle(0, 0, FrameWidth, FrameHeight),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < FrameHeight; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _bgra, y * FrameWidth * 4, FrameWidth * 4);
        }
        finally { small.UnlockBits(data); }

        DownscalePose(width, height);
    }

    /// <summary>同一帧再缩一份 320×180：姿态模型和逐帧位置跟随（画面同步）共用这份缓冲（约 2–4ms）。</summary>
    private void DownscalePose(int width, int height)
    {
        var pose = _poseBitmap!;
        using (var graphics = System.Drawing.Graphics.FromImage(pose))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImage(_sourceBitmap!,
                new System.Drawing.Rectangle(0, 0, PoseWidth, PoseHeight),
                0, 0, width, height, System.Drawing.GraphicsUnit.Pixel);
        }

        System.Drawing.Imaging.BitmapData data = pose.LockBits(
            new System.Drawing.Rectangle(0, 0, PoseWidth, PoseHeight),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < PoseHeight; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _poseBgra, y * PoseWidth * 4, PoseWidth * 4);
        }
        finally { pose.UnlockBits(data); }
    }

    /// <summary>
    /// 这一帧算不算「没抓到」。两种情况：
    /// ① 整帧几乎全黑 / 全白（PrintWindow 对独占全屏、受保护内容就是这样）；
    /// ② 整帧几乎是同一个颜色（最亮和最暗只差一点点）—— D3D 加速的窗口被 PrintWindow 抓到时不返回黑，
    ///    而是返回一整片均匀的深灰（实测 ffplay 默认渲染就是这样，均值约 16），旧判据只看均值，
    ///    于是这种空帧被当成「画面很安静」分析下去，信号永远是 0。
    /// </summary>
    private bool IsBlankFrame()
    {
        long sum = 0;
        int min = 255, max = 0;
        for (int i = 0; i < _bgra.Length; i += 4)
        {
            int luma = (_bgra[i] * 28 + _bgra[i + 1] * 151 + _bgra[i + 2] * 77) >> 8;
            sum += luma;
            if (luma < min) min = luma;
            else if (luma > max) max = luma;
        }
        double mean = sum / (double)(FrameWidth * FrameHeight);
        if (mean < BlankLumaLow || mean > BlankLumaHigh) return true;
        return max - min < BlankSpanMin;
    }

    // ── 特征计算 ──────────────────────────────────────────────────────

    /// <summary>
    /// 一帧的算法（都在 160×90 上做，总耗时远小于 1ms）：
    /// ① 遍历一次，统计整幅与中心的肤色像素数、<b>运动区域</b>的相邻帧亮度差；
    /// ② 得到肤色占比、中央肤色占比（平滑）、运动量（平滑）；
    /// ③ 把运动量压进 3.2 秒环形缓冲，算周期性（顺带记下频率与「动得最猛」的时刻，跟随要用）；
    ///    ④ 读声音那条链的人声事件；
    /// ⑤ 融合成场景强度（模型就绪时 = 0.7×模型分 + 0.3×特征分，否则纯特征分），按迟滞判「疑似」；
    /// ⑥ 顺手把这一帧排给识别模型 / 姿态模型（各自有间隔，推理都在别的线程上做）。
    /// 画面同步不在这里：它由 <see cref="WorkerLoop"/> 每拍统一调一次 —— 抓不到画面、这一帧出错
    /// 的那几拍也要有人负责把设备缓降回中。
    ///
    /// <b>运动区域</b>：姿态定位到了就只算「运动主体那一块」的相邻帧亮度差，否则退回中央 60%
    ///（= 加姿态之前的行为）。为什么值得这么做：整幅（或固定中央）区域里，镜头一晃、背景里的树一动，
    /// 运动量都会被算成「画面在动」；只盯主体那一块，信号才真是「人在动」。
    /// </summary>
    private void AnalyzeFrame(Stopwatch clock)
    {
        int centerX0 = (int)(FrameWidth * CenterLeftRatio);
        int centerX1 = (int)(FrameWidth * CenterRightRatio);
        int centerY0 = (int)(FrameHeight * CenterLeftRatio);
        int centerY1 = (int)(FrameHeight * CenterRightRatio);

        // 运动量的取样区域：姿态定位到了就用它，否则退回中央 60%。
        // 区域切换时**不**清空节奏环形缓冲：两种区域算出来的都是「0–1 的平均绝对亮度差」，量纲一致，
        // 切换只在单帧上留一个台阶（去趋势那一步会把它吃掉大半）；
        // 反过来，每次切换都清空历史的话，姿态信号一抖动，节奏就永远攒不满 3.2 秒 —— 那才是真的坏。
        bool useRoi = TryPoseRoi(out int motionX0, out int motionY0, out int motionX1, out int motionY1);
        if (!useRoi)
        {
            motionX0 = centerX0; motionX1 = centerX1;
            motionY0 = centerY0; motionY1 = centerY1;
        }
        Volatile.Write(ref _poseInUse, useRoi ? 1 : 0);

        bool hadPrev = _hasPrevFrame;
        int skinAll = 0;
        int skinCenter = 0;
        int centerPixels = 0;
        int motionPixels = 0;
        long diffSum = 0;
        // 主体包围盒：亮度变化够大的像素落在哪几行。高度变化 = 主体在画面里变大或变小。
        int subjectTop = int.MaxValue;
        int subjectBottom = -1;
        int subjectPixels = 0;
        Array.Clear(_blobCellNow);

        for (int y = 0; y < FrameHeight; y++)
        {
            int rowLuma = 0;
            bool rowInCenter = y >= centerY0 && y < centerY1;
            bool rowInMotion = y >= motionY0 && y < motionY1;
            int rowBase = y * FrameWidth;
            int rowCell = (y * BlobRows / FrameHeight) * BlobCols;
            for (int x = 0; x < FrameWidth; x++)
            {
                int p = (rowBase + x) * 4;
                byte b = _bgra[p], g = _bgra[p + 1], r = _bgra[p + 2];
                bool skin = IsSkin(r, g, b);
                if (skin) skinAll++;

                // 中心 60% 的肤色占比：主体一般就在画面中间，它比整幅更能说明问题
                if (rowInCenter && x >= centerX0 && x < centerX1)
                {
                    centerPixels++;
                    if (skin) skinCenter++;
                }

                // 亮度差只在运动区域里累加，但**每个**像素的上一帧亮度都要更新 ——
                // 否则区域切回去的时候会拿很久以前的帧来比，凭空跳一下。
                int luma = (r * 77 + g * 151 + b * 28) >> 8;
                int index = rowBase + x;
                if (hadPrev)
                {
                    int delta = Math.Abs(luma - _prevLuma[index]);
                    // 整帧每格的运动量：没有骨架的画面（2D 游戏/手绘）只能靠「哪一块在动」定主体，见 UpdateSubjectBlob。
                    _blobCellNow[rowCell + x * BlobCols / FrameWidth] += delta;
                    if (rowInMotion && x >= motionX0 && x < motionX1)
                    {
                        motionPixels++;
                        diffSum += delta;
                        if (delta >= SubjectPixelThreshold)
                        {
                            subjectPixels++;
                            if (y < subjectTop) subjectTop = y;
                            if (y > subjectBottom) subjectBottom = y;
                        }
                    }
                }
                _prevLuma[index] = (byte)luma;
                rowLuma += luma;
            }
            _rowProfile[y] = rowLuma;
        }
        _hasPrevFrame = true;

        // 主体块：没有骨架时的跟随源。必须在整帧亮度差算完之后、方向判定之前更新。
        UpdateSubjectBlob(hadPrev);

        // ⓪ 方向：主体是在靠近镜头还是退开。
        //    主判据是**主体的包围盒高度变化率**：人在画面里变大 = 走近，变小 = 退开。
        //    这跟「谁在动」无关 —— 固定机位里人自己动起来，包围盒照样会变，这是 v1 只看整幅位移
        //    漏掉的情况（他一句话点破：多数都是人物在动）。
        //    兜底：运动像素太少 / 包围盒退化（比如全屏就一条横线在动）时，才退回整幅行剖面位移。
        double sinceLast = _lastDirectionSeconds > 0 ? clock.Elapsed.TotalSeconds - _lastDirectionSeconds : 0;
        _lastDirectionSeconds = clock.Elapsed.TotalSeconds;

        int span = subjectBottom >= subjectTop ? subjectBottom - subjectTop + 1 : 0;
        bool haveSubject = span >= SubjectMinSpan && subjectPixels >= SubjectMinPixels;
        // 整幅位移总要算（兜底 + 状态文字），代价是 90 个数做 13 次点积。
        if (hadPrev && _hasProfile)
        {
            double shift = FlowEstimator.VerticalShift(_rowProfile, _prevRowProfile);
            _flowShiftPx += (shift - _flowShiftPx) * FlowSmoothing;
        }
        else
        {
            _flowShiftPx *= 0.5;
        }
        Array.Copy(_rowProfile, _prevRowProfile, FrameHeight);
        _hasProfile = true;

        double approach;
        if (haveSubject && sinceLast > 0.004 && sinceLast < 1.0)
        {
            if (_subjectSpanEma <= 0) _subjectSpanEma = span;
            else
            {
                // 先对包围盒高度做 0.15 的指数平滑（≈0.3 秒时间常数），再对它求导：
                // 直接对原始高度求差会被 ±1 像素的抖动放大成「每秒涨 40%」。
                double previous = _subjectSpanEma;
                _subjectSpanEma += (span - _subjectSpanEma) * SubjectSpanSmoothing;
                double growth = (_subjectSpanEma - previous) / Math.Max(1.0, previous) / sinceLast;
                _subjectGrowthEma += (growth - _subjectGrowthEma) * SubjectGrowthSmoothing;
            }
            approach = FlowEstimator.ApproachFromGrowth(_subjectGrowthEma, SubjectGrowthFullScale);
            Volatile.Write(ref _subjectDirection, true);
        }
        else
        {
            // 主体看不见（运动太少）：尺寸这条线断掉，靠画面整体位移兜，并让增长率慢慢归零，
            // 免得下次主体一出现就带着上一次的旧方向冲一下。
            _subjectGrowthEma *= 0.85;
            approach = Math.Clamp(-_flowShiftPx / FlowFullScalePx, -1, 1);
            Volatile.Write(ref _subjectDirection, false);
        }
        Volatile.Write(ref _followApproach, approach);

        // ① 整幅 / 中央的肤色占比
        double skinNow = skinAll / (double)(FrameWidth * FrameHeight);
        double skinCenterNow = centerPixels == 0 ? 0 : skinCenter / (double)centerPixels;
        Volatile.Write(ref _skinRatio, Smooth(Volatile.Read(ref _skinRatio), skinNow, SkinAttack, SkinRelease));
        double smoothedSkinCenter = Smooth(Volatile.Read(ref _skinCenterRatio), skinCenterNow, SkinAttack, SkinRelease);
        Volatile.Write(ref _skinCenterRatio, smoothedSkinCenter);

        // ② 运动量：运动区域相邻帧的平均绝对亮度差 ÷ 32（姿态没定位到时区域就是中央 60%，与旧行为一致）
        double motionNow = !hadPrev || motionPixels == 0
            ? 0
            : Math.Clamp(diffSum / (double)motionPixels / MotionFullScale, 0, 1);
        double smoothedMotion = Smooth(Volatile.Read(ref _motion), motionNow, MotionSmoothing, MotionSmoothing);
        Volatile.Write(ref _motion, smoothedMotion);

        // ③ 节奏性（0.5–3Hz 的周期性起伏）+ 顺带记下频率与「动得最猛」的时刻（画面同步要用）
        PushMotionSample(smoothedMotion, clock.Elapsed.TotalSeconds);
        double rhythm = ComputeRhythm();
        Volatile.Write(ref _rhythm, rhythm);

        // ④ 呻吟事件：直接借声音那条链的检测结果，不自己重做音频分析
        bool voice = VoiceRecently();
        Volatile.Write(ref _voiceEvent, voice ? 1 : 0);

        // ⑤ 融合：中央肤色占比 ×（0.5 + 0.5 × 节奏性）—— 这是「手工特征分」。
        //    为什么这么合：中央肤色占比是「画面里像人体」的强弱（主体分量的主项）；节奏性只做 0.5–1.0 的加权，
        //    避免「静止的裸体画面」被节奏一票否决，也避免「画面动得厉害但没人」拿到高分。
        //    再往前一步分两条路：
        //      · 识别模型就绪 → SceneScore = 0.7 × 模型分 + 0.3 × 特征分（模型是主判据，特征做佐证）
        //      · 模型没就绪 / 没装 / 加载失败 → SceneScore = 特征分（**完全等于加模型之前的行为**）
        //    呻吟是独立证据，两条路都再 ×1.25。
        double featureNow = smoothedSkinCenter * (0.5 + 0.5 * rhythm);
        Volatile.Write(ref _featureScore,
            Smooth(Volatile.Read(ref _featureScore), featureNow, ScoreAttack, ScoreRelease));   // 界面做对照用

        bool modelReady = Volatile.Read(ref _modelReady) != 0;
        double raw = modelReady
            ? ModelWeight * Math.Clamp(Volatile.Read(ref _modelScore), 0, 1) + (1 - ModelWeight) * featureNow
            : featureNow;
        raw = Math.Clamp(raw * (voice ? VoiceBoost : 1.0), 0, 1);
        double score = Smooth(Volatile.Read(ref _sceneScore), raw, ScoreAttack, ScoreRelease);
        Volatile.Write(ref _sceneScore, score);

        // ⑥ 判定：进入阈值高于退出阈值（迟滞），避免分数在临界线上来回跳、灯一直闪
        bool suspect = Volatile.Read(ref _suspect) != 0;
        if (!suspect && score >= ThresholdOn) suspect = true;
        else if (suspect && score < ThresholdOff) suspect = false;
        Volatile.Write(ref _suspect, suspect ? 1 : 0);

        if (Volatile.Read(ref _manualEditing) == 0 && !ManualBinding.Bound)
        {
            // ⑦ 把这一帧排给识别模型（每 300ms 一次；推理在模型线程上做，这里只做一次缩放到短边 236）
            MaybeQueueModelFrame(clock);
            // ⑧ 同样的办法把这一帧排给姿态模型（每 250ms 一次，推理在姿态线程上做）
            MaybeQueuePoseFrame(clock);
            // 姿态运动：单独那份 320×180 喂给姿态运动源（它自己节流到 10–15fps 并拷一次缓冲）。
            // 门就是姿态识别的总开关 —— 关着时这一行只是读一个 bool，别的什么都不做。
            if (_cfg.PoseVisionEnabled) _poseMotion.Submit(_poseBgra, PoseWidth, PoseHeight);
            // ⑧b 逐帧位置跟随（画面同步）：同一份 320×180 每帧都喂，抓帧线程毫秒级。
            if (Volatile.Read(ref _manualEditing) == 0 && !ManualBinding.Bound) FeedScreenSync();
        }
        // ⑨ 画面同步不在这里跑：抓帧线程每拍（成功、抓不到、出错）都会统一跑一次（见 WorkerLoop），
        //    否则「抓不到画面」的那些帧就没人负责把设备缓降回中了。
        Interlocked.Increment(ref _frameCount);
        MaybeDumpAnalysisFrame();
    }

    /// <summary>按 HEXA_DUMP_FRAME 存前 3 帧分析图（见字段注释）；没设变量时只是一个空判断。</summary>
    private void MaybeDumpAnalysisFrame()
    {
        if (_dumpDir is null || _dumpCount >= 3) return;
        try
        {
            using var bmp = new System.Drawing.Bitmap(FrameWidth, FrameHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, FrameWidth, FrameHeight),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < FrameHeight; y++)
                    Marshal.Copy(_bgra, y * FrameWidth * 4, IntPtr.Add(data.Scan0, y * data.Stride), FrameWidth * 4);
            }
            finally { bmp.UnlockBits(data); }

            string path = System.IO.Path.Combine(_dumpDir, $"frame_{++_dumpCount}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            AppLogger.Info($"画面同步：分析图已存 {path}（排障用，最多 3 张）");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面同步：存分析图失败 —— {ex.Message}");
        }
    }

    /// <summary>
    /// 把当前这一帧排给识别模型。做三件事，全部在抓帧线程上、总耗时只有一次缩放：
    /// ① 每 <see cref="_modelIntervalMs"/> 只排一次（不是每帧）；② 模型线程还没算完就跳过这次（不排队、不堆积）；
    /// ③ 把源图<b>按比例</b>缩到短边 236（模型自己再中心裁 224 —— 等价于 Resize+CenterCrop），画进缓冲再叫醒模型线程。
    ///
    /// 读写不会打架，靠两条：
    /// · <see cref="_modelBusy"/> 说"模型线程手里没有帧"时抓帧线程才画 —— 画的时候模型线程一定在睡觉；
    /// · 交出去的只是"哪一块"（<see cref="_modelJobSlot"/>），模型线程要等到 <c>_modelWake.Set()</c> 之后才去读，
    ///   而 Set() 一定排在缩放之后。
    /// 两块缓冲交替用是第二道保险：即使以后有人把间隔调到比推理还短，也画不到正在读的那一块。
    /// 没装模型 / 加载失败时这里直接返回：一个字节都不碰，CPU 零开销。
    /// </summary>
    private void MaybeQueueModelFrame(Stopwatch clock)
    {
        if (!NsfwClassifier.IsInstalled && !NsfwClassifier.IsReady) return;
        // 模型线程没起来（线程创建失败这种极端情况）：别排帧 —— 排了也没人算，
        // 而"算完才放掉"的标记会永远挂着，后面再也排不进去。
        if (_modelWake is null) return;

        long now = clock.ElapsedMilliseconds;
        if (now - _lastModelJobMs < _modelIntervalMs) return;

        int sourceWidth = _sourceWidth, sourceHeight = _sourceHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0) return;
        // 按短边缩到 236（保持比例，不拉变形）：宽窗口就得到 420×236 这样的图，模型再中心裁 224。
        double scale = NsfwClassifier.ShortSide / (double)Math.Min(sourceWidth, sourceHeight);
        int width = Math.Max(NsfwClassifier.InputSize, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(NsfwClassifier.InputSize, (int)Math.Round(sourceHeight * scale));

        System.Drawing.Bitmap? buffer = null;
        lock (_modelSync)
        {
            if (_modelBusy) return;
            int slot = _modelFrameSlot;
            _modelFrameSlot ^= 1;            // 换一块给下一次用
            _modelJobSlot = slot;
            try
            {
                buffer = _modelFrames[slot].Ensure(width, height);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"画面同步：准备识别模型的缓冲失败 —— {ex.Message}");
                return;                      // 还没把 busy 立起来，直接返回即可
            }
            _modelBusy = true;               // 交给模型线程了：下次要等它算完
        }
        if (buffer is null) return;          // 理论上到不了这儿，挡一下让编译器也放心

        try
        {
            using var graphics = System.Drawing.Graphics.FromImage(buffer);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImage(_sourceBitmap!,
                new System.Drawing.Rectangle(0, 0, width, height),
                0, 0, sourceWidth, sourceHeight, System.Drawing.GraphicsUnit.Pixel);
        }
        catch (Exception ex)
        {
            lock (_modelSync) _modelBusy = false;    // 这一帧没准备好：放掉，下一帧再说
            AppLogger.Warn($"画面同步：给识别模型准备这一帧时出错 —— {ex.Message}");
            return;
        }

        _lastModelJobMs = now;
        try { _modelWake?.Set(); }
        catch (Exception ex) { AppLogger.Warn($"画面同步：叫醒识别模型失败 —— {ex.Message}"); }
    }

    /// <summary>
    /// 识别模型线程：等抓帧线程叫醒 → 对那一块缓冲跑一次推理 → 平滑成模型分。
    /// 推理异常、模型损坏一律被 <see cref="NsfwClassifier.Classify"/> 消化成 −1 和状态文字，
    /// 这里只是"没分就不更新"，线程永不因一帧出错而死。
    /// </summary>
    private void ModelWorkerLoop()
    {
        try
        {
            while (!_modelStopRequested)
            {
                if (_modelWake is null) return;
                if (!_modelWake.WaitOne(200)) continue;
                if (_modelStopRequested) break;

                int slot;
                System.Drawing.Bitmap? frame;
                lock (_modelSync)
                {
                    slot = _modelJobSlot;
                    frame = _modelFrames[slot].Bitmap;
                }

                double score = -1;
                try
                {
                    if (frame is not null) score = NsfwClassifier.Classify(frame);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"画面同步：识别模型推理异常已跳过 —— {ex.Message}");
                }
                finally
                {
                    lock (_modelSync) _modelBusy = false;   // 放掉这一块，抓帧线程可以排下一帧了
                }

                if (score < 0)
                {
                    Volatile.Write(ref _modelReady, 0);     // 这次没分：判定退回手工特征（原因在模型状态里）
                    continue;
                }

                Interlocked.Exchange(ref _lastInferenceMs, NsfwClassifier.LastInferenceMs);
                double smoothed = Smooth(Volatile.Read(ref _modelScore), score, ModelScoreAttack, ModelScoreRelease);
                Volatile.Write(ref _modelScore, smoothed);
                Volatile.Write(ref _modelReady, 1);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("画面同步：识别模型线程异常退出", ex);
        }
        finally
        {
            Volatile.Write(ref _modelReady, 0);
            lock (_modelSync) _modelBusy = false;
        }
    }

    // ══ 姿态线程：把「人在画面哪一块」找出来 ═══════════════════════════
    // 和识别模型线程同一个套路：抓帧线程只负责把源图缩进一块 640×640 的灰底画布（letterbox），
    // 推理（20–40ms）在姿态线程上做，13fps 的抓帧节拍绝不被拖慢。

    /// <summary>
    /// 起姿态线程（幂等）。线程起来后先睡着 —— 设置里关掉了姿态、或模型没下载时，
    /// 它一个字节都不碰、也不占 CPU；抓帧线程每 <see cref="PoseIntervalDefaultMs"/> 才叫醒它一次。
    /// </summary>
    private void StartPoseThread()
    {
        if (_disposed) return;
        if (_poseThread is { IsAlive: true }) return;
        lock (_poseSync)
        {
            if (_poseThread is { IsAlive: true }) return;
            _poseStopRequested = false;
            try
            {
                _poseWake ??= new AutoResetEvent(false);
                var thread = new Thread(PoseWorkerLoop)
                {
                    IsBackground = true,
                    Name = "Hexa 画面姿态",
                    Priority = ThreadPriority.BelowNormal,   // 和抓帧一样礼貌：别抢设备通信和界面的核
                };
                _poseThread = thread;
                thread.Start();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"画面同步：姿态线程起不来（运动量退回整幅画面）—— {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 姿态线程：等抓帧线程叫醒 → 对那块 640×640 画布跑一次推理 → 把主体框平滑下来。
    /// 推理异常、模型损坏一律被 <see cref="PoseTracker.TryLocate"/> 消化成 false，
    /// 这里只是「这一帧没定位到」，线程永不因一帧出错而死。
    /// </summary>
    private void PoseWorkerLoop()
    {
        try
        {
            while (!_poseStopRequested)
            {
                if (_poseWake is null) return;
                if (!_poseWake.WaitOne(200)) continue;
                if (_poseStopRequested) break;

                int slot;
                System.Drawing.Bitmap? canvas;
                LetterboxMap map;
                lock (_poseSync)
                {
                    slot = _poseJobSlot;
                    canvas = _poseFrames[slot].Bitmap;
                    map = _poseMaps[slot];
                }

                bool ok = false;
                PoseSubject subject = default;
                var watch = Stopwatch.StartNew();
                try
                {
                    if (canvas is not null) ok = PoseTracker.TryLocate(canvas, map, out subject);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"画面同步：姿态推理异常已跳过 —— {ex.Message}");
                }
                finally
                {
                    lock (_poseSync) _poseBusy = false;   // 放掉这一块，抓帧线程可以排下一帧了
                }
                Interlocked.Exchange(ref _poseInferenceMs, watch.ElapsedMilliseconds);

                if (!ok)
                {
                    // 这一帧没定位到（画面里没人 / 模型没就绪 / 推理失败）：**不**立刻清掉 ROI。
                    // 什么时候退回「整幅画面」由抓帧线程按「保鲜期」（PoseFreshMs）决定 ——
                    // 一次漏检就让取样区域切回整幅、下一帧再切回来，会把运动量序列搅成锯齿，
                    // 节奏那 3.2 秒的窗口永远攒不出东西。900ms 的保鲜期本身就是一层防抖。
                    Volatile.Write(ref _poseKeypoints, 0);
                    continue;
                }

                // ROI 平滑：姿态框每 250ms 才更新一次，直接用会让取样区域在两帧之间跳。
                // 第一次直接跳过去（否则第一秒会被从 0,0 慢慢「长」出来）。
                bool first = Volatile.Read(ref _poseEverWorked) == 0;
                double attack = first ? 1.0 : PoseRoiAttack;
                Volatile.Write(ref _poseLeft,
                    Smooth(Volatile.Read(ref _poseLeft), subject.Left, attack, PoseRoiRelease));
                Volatile.Write(ref _poseTop,
                    Smooth(Volatile.Read(ref _poseTop), subject.Top, attack, PoseRoiRelease));
                Volatile.Write(ref _poseRight,
                    Smooth(Volatile.Read(ref _poseRight), subject.Right, attack, PoseRoiRelease));
                Volatile.Write(ref _poseBottom,
                    Smooth(Volatile.Read(ref _poseBottom), subject.Bottom, attack, PoseRoiRelease));
                Volatile.Write(ref _poseConfidence, subject.Confidence);
                Volatile.Write(ref _poseKeypoints, subject.Keypoints);
                Volatile.Write(ref _poseAtMs, Environment.TickCount64);
                Volatile.Write(ref _poseLocated, 1);
                Volatile.Write(ref _poseEverWorked, 1);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("画面同步：姿态线程异常退出", ex);
        }
        finally
        {
            Volatile.Write(ref _poseLocated, 0);
            Volatile.Write(ref _poseInUse, 0);
            lock (_poseSync) _poseBusy = false;
        }
    }

    /// <summary>
    /// 把当前这一帧排给姿态模型（每 <see cref="PoseIntervalDefaultMs"/> 一次，不是每帧）。
    /// 抓帧线程上只做一次「画进 640×640 灰底画布」的缩放；排帧规则与识别模型那条完全一致：
    /// 上一帧还没算完就不排队（不堆积）、两块画布交替用（画不到正在读的那一块）。
    /// 设置里关掉了姿态 / 模型没下载 / 模型还没加载起来，这里直接返回：一个字节都不碰。
    /// </summary>
    /// <summary>这一帧的粗略签名：每隔 32 个像素取一个绿通道，换算成「平均亮度×1000」。用来判「画面几乎没变」。</summary>
    private long FrameSignature()
    {
        long sum = 0;
        int n = 0;
        for (int i = 1; i < _bgra.Length; i += 32 * 4) { sum += _bgra[i]; n++; }
        return n == 0 ? 0 : sum * 1000 / n;
    }

    private void MaybeQueuePoseFrame(Stopwatch clock)
    {
        if (!_cfg.ScreenUsePose) return;
        if (!PoseTracker.IsInstalled && !PoseTracker.IsReady) return;
        // 姿态线程没起来（线程创建失败这种极端情况）：别排帧 —— 排了也没人算，
        // 而「算完才放掉」的标记会永远挂着，后面再也排不进去。
        if (_poseWake is null) return;

        long now = clock.ElapsedMilliseconds;
        if (now - _lastPoseJobMs < PoseIntervalDefaultMs) return;

        // 静帧不喂模型：画面几乎没变的时候，姿态结果和上一拍一模一样，推理纯属白烧 CPU
        //（实测画面同步开着时 5 个 ORT 线程能把 2 个核吃满，大头就是每秒十来次 640×640 推理）。
        // 但不能彻底停：静着也要每隔 12 拍补一帧，否则一开始动的那一下要等一拍才发现。
        long sig = FrameSignature();
        if (_poseSigValid != 0 && Math.Abs(sig - _lastPoseSig) < PoseStillSigMin && (++_poseStillSkips % 12) != 0) return;
        if (_poseSigValid == 0 || Math.Abs(sig - _lastPoseSig) >= PoseStillSigMin) _poseStillSkips = 0;
        _lastPoseSig = sig;
        _poseSigValid = 1;

        int sourceWidth = _sourceWidth, sourceHeight = _sourceHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0) return;

        System.Drawing.Bitmap? canvas;
        int slot;
        lock (_poseSync)
        {
            if (_poseBusy) return;
            slot = _poseFrameSlot;
            _poseFrameSlot ^= 1;             // 换一块给下一次用
            _poseJobSlot = slot;
            try
            {
                canvas = _poseFrames[slot].Ensure();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"画面同步：准备姿态模型的画布失败 —— {ex.Message}");
                return;                      // 还没把 busy 立起来，直接返回即可
            }
            _poseBusy = true;                // 交给姿态线程了：下次要等它算完
        }
        if (canvas is null) return;          // 理论上到不了这儿，挡一下让编译器也放心

        LetterboxMap map;
        try
        {
            map = PoseTracker.DrawLetterbox(_sourceBitmap!, canvas);
        }
        catch (Exception ex)
        {
            lock (_poseSync) _poseBusy = false;   // 这一帧没准备好：放掉，下一帧再说
            AppLogger.Warn($"画面同步：给姿态模型准备这一帧时出错 —— {ex.Message}");
            return;
        }
        lock (_poseSync) _poseMaps[slot] = map;

        _lastPoseJobMs = now;
        try { _poseWake?.Set(); }
        catch (Exception ex) { AppLogger.Warn($"画面同步：叫醒姿态线程失败 —— {ex.Message}"); }
    }

    /// <summary>
    /// 手动框选优先：用户在预览图上亲手拖过一块，就只跟那块 —— 这比 YOLO 猜的框准，
    /// 所以压过姿态框（<see cref="TryPoseRoi"/> 开头先问它）。坐标是设置里存着的 0–1 归一化值。
    /// </summary>
    /// <summary>手动框的归一化矩形（0–1）。没开 / 没框 / 解析失败都返回 false。</summary>
    private bool TryManualRoiNorm(out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        if (!_cfg.ScreenUseManualRoi) return false;
        return TryParseRoi(_cfg.ScreenManualRoi, out x, out y, out w, out h);
    }

    private bool TryManualRoi(out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (!_cfg.ScreenUseManualRoi) return false;
        // 已经按框裁剪放大过了：整幅小图就是用户框的那块，不用再切一刀。
        if (Volatile.Read(ref _smallZoomed) != 0)
        {
            x0 = 0; y0 = 0; x1 = FrameWidth; y1 = FrameHeight;
            return true;
        }
        if (!TryParseRoi(_cfg.ScreenManualRoi, out double left, out double top, out double width, out double height)) return false;

        x0 = Math.Clamp((int)Math.Floor(left * FrameWidth), 0, FrameWidth - 1);
        y0 = Math.Clamp((int)Math.Floor(top * FrameHeight), 0, FrameHeight - 1);
        x1 = Math.Clamp((int)Math.Ceiling((left + width) * FrameWidth), x0 + 1, FrameWidth);
        y1 = Math.Clamp((int)Math.Ceiling((top + height) * FrameHeight), y0 + 1, FrameHeight);
        return x1 - x0 >= 8 && y1 - y0 >= 8;
    }

    /// <summary>解析设置里的 "x,y,w,h"（0–1 归一化）。解析不了返回 false，调用方退回按整幅/姿态处理。</summary>
    private static bool TryParseRoi(string? text, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Split(',');
        if (parts.Length != 4) return false;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v[i]) || !double.IsFinite(v[i]))
                return false;
        }
        x = Math.Clamp(v[0], 0, 1);
        y = Math.Clamp(v[1], 0, 1);
        w = Math.Clamp(v[2], 0, 1 - x);
        h = Math.Clamp(v[3], 0, 1 - y);
        return w > 0 && h > 0;
    }

    /// <summary>取这一帧要用的 ROI（160×90 小图上的像素矩形）。返回 false = 退回中央 60% 那块。</summary>
    private bool TryPoseRoi(out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = x1 = y1 = 0;
        // 手动框优先：亲手指的这块比模型猜的准，钩上了就不看 YOLO 框。
        if (TryManualRoi(out x0, out y0, out x1, out y1)) return true;
        if (!_cfg.ScreenUsePose) return false;
        if (Volatile.Read(ref _poseLocated) == 0) return false;

        // 保鲜期：姿态是每 250ms 一次，超过 PoseFreshMs 没用上新结果就说明它跟不上了（或者画面里没人了）。
        long at = Volatile.Read(ref _poseAtMs);
        if (at <= 0 || Environment.TickCount64 - at > PoseFreshMs) return false;

        double left = Volatile.Read(ref _poseLeft), top = Volatile.Read(ref _poseTop);
        double right = Volatile.Read(ref _poseRight), bottom = Volatile.Read(ref _poseBottom);
        double width = right - left, height = bottom - top;
        if (!(width > 0) || !(height > 0)) return false;    // 也顺带挡掉 NaN

        x0 = (int)Math.Floor(left * FrameWidth);
        y0 = (int)Math.Floor(top * FrameHeight);
        x1 = (int)Math.Ceiling(right * FrameWidth);
        y1 = (int)Math.Ceiling(bottom * FrameHeight);
        x0 = Math.Clamp(x0, 0, FrameWidth - 1);
        y0 = Math.Clamp(y0, 0, FrameHeight - 1);
        x1 = Math.Clamp(x1, x0 + 1, FrameWidth);
        y1 = Math.Clamp(y1, y0 + 1, FrameHeight);

        // 太小的框（主体几乎看不见）拿它算运动量只会更糟：退回整幅。
        return x1 - x0 >= PoseRoiMinWidth && y1 - y0 >= PoseRoiMinHeight;
    }

    /// <summary>
    /// 画面同步（逐帧位置跟随）：把这一帧的 320×180 灰度喂给跟踪器，并把姿态框当搜索范围限定。
    /// 姿态框的保鲜期和 <see cref="TryPoseRoi"/> 用同一个 <see cref="PoseFreshMs"/>；
    /// 关掉姿态识别时传 null，跟踪器在整幅画面里找交合区 ROI。
    /// </summary>
    private void FeedScreenSync()
    {
        double? left = null, top = null, right = null, bottom = null;
        if (_cfg.ScreenUsePose && Volatile.Read(ref _poseLocated) != 0)
        {
            long at = Volatile.Read(ref _poseAtMs);
            if (at > 0 && Environment.TickCount64 - at <= PoseFreshMs)
            {
                double l = Volatile.Read(ref _poseLeft), t = Volatile.Read(ref _poseTop);
                double rr = Volatile.Read(ref _poseRight), bb = Volatile.Read(ref _poseBottom);
                if (rr - l > 0 && bb - t > 0) { left = l; top = t; right = rr; bottom = bb; }
            }
        }
        _sync.SetSubjectBox(left, top, right, bottom);
        double dt = Math.Clamp(_frameIntervalEmaMs, 20, 200) / 1000.0;
        _sync.SubmitBgra(_poseBgra, dt);
    }

    private void ClearPoseState()
    {
        Volatile.Write(ref _poseLocated, 0);
        Volatile.Write(ref _poseInUse, 0);
        Volatile.Write(ref _poseKeypoints, 0);
        Volatile.Write(ref _poseConfidence, 0);
        Volatile.Write(ref _poseAtMs, 0);
        Volatile.Write(ref _poseLeft, 0);
        Volatile.Write(ref _poseTop, 0);
        Volatile.Write(ref _poseRight, 0);
        Volatile.Write(ref _poseBottom, 0);
        Volatile.Write(ref _poseEverWorked, 0);
        Interlocked.Exchange(ref _poseInferenceMs, 0);
        _lastPoseJobMs = 0;
        lock (_poseSync) _poseBusy = false;
    }

    // ══ 画面同步：把这条信号接成「能驱动设备」 ═════════════════════════
    //
    // 设计口径（一句话）：<b>跟着画面里的节奏与强度走，不是逐帧复刻画面</b>。
    //   · 深度 ∝ 场景强度（过判定线之后归一化）；· 速度 ∝ 检测到的节奏频率（夹到舒适档允许的范围）；
    //   · 相位按 ScreenPhaseLock 往画面节奏的相位上拉（有界修正，只有连续两拍都对不上才重锁）。
    //
    // 设备侧只碰两个公开入口：TryClaimDirectInput("screen") 与 TrySendDirectAxes(values, 插值秒)。
    // 急停 / 限位 / 舒适档 / 限速器全部照旧生效 —— 本类只是又一个「直接下发」的使用者，
    // 和音频响应、游戏桥走的是同一条路（谁先声明谁写，别人让位）。

    /// <summary>起看门狗（幂等）。它是独立定时器，抓帧线程真卡在 PrintWindow 里也能收尾。</summary>
    private void StartFollowWatchdog()
    {
        if (_disposed) return;
        if (_followWatchdog is not null) return;
        _followWatchdog = new System.Threading.Timer(
            _ => FollowWatchdogTick(), null, FollowWatchdogMs, FollowWatchdogMs);
    }

    private void StopFollowWatchdog()
    {
        System.Threading.Timer? watchdog = _followWatchdog;
        _followWatchdog = null;
        try { watchdog?.Dispose(); }
        catch (Exception ex) { AppLogger.Warn($"画面同步：停看门狗出错 —— {ex.Message}"); }
    }

    /// <summary>
    /// 看门狗：正在跟随却连续 <see cref="FollowStallMs"/> 没有成功下发过一条指令，
    /// 就<b>立刻缓降回中并交还控制权</b>。
    ///
    /// 为什么必须有它：抓帧线程一旦卡住（PrintWindow 卡在某个硬件加速窗口上、进程被挂起）
    /// 就再也不会执行「信号断了」那段逻辑，设备会一直停在最后那一拍的位置上。
    /// 这条路径不做逐帧缓降（线程都没在工作了，没有逐帧可言），直接给一个 0.45 秒的插值回中，
    /// 由引擎把这一段插值平滑地放完 —— 仍然是「缓」而不是「急停」。
    /// </summary>
    private void FollowWatchdogTick()
    {
        try
        {
            if (Volatile.Read(ref _followActive) == 0 && Volatile.Read(ref _followEasing) == 0) return;
            if (Environment.TickCount64 - _lastFollowSendMs < FollowStallMs) return;
            AppLogger.Warn("画面同步：看门狗发现信号停了（这段时间一条指令都没发出去），缓降回中并交还控制权");
            StopFollowNow("画面同步断了（看门狗）：缓降回中");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面同步：看门狗出错 —— {ex.Message}");
        }
    }

    /// <summary>每个抓帧周期跑一次：决定这一拍要不要驱动设备、驱动到哪。任何异常都消化在这里。</summary>
    /// <summary>
    /// 范围预览：用户拖动「活动范围」把手时，设备要停在指定位置给他看。
    /// 这一路每 50ms 发一帧，不主动松手的话预览位置下一拍就被冲掉
    /// （用户原话「滑动时设备也跟着到指定范围」）。到时自己接回来，不用重开。
    /// </summary>
    public void PauseForRangePreview(int ms = 1000) =>
        Interlocked.Exchange(ref _previewPauseUntil, Environment.TickCount64 + Math.Clamp(ms, 200, 5000));

    private void FollowTick(Stopwatch clock)
    {
        try { FollowTickCore(clock.Elapsed.TotalSeconds); }
        catch (Exception ex)
        {
            // 跟随逻辑绝不能把抓帧线程搞死；出错就按「信号断了」处理：缓降 + 交还控制权。
            AppLogger.Warn($"画面同步：这一拍出错，按信号断了处理 —— {ex.Message}");
            try { StopFollowNow("跟随逻辑出错"); } catch { /* 已经尽力了 */ }
        }
    }

    private void FollowTickCore(double nowSeconds)
    {
        // 自检会注入自己的窗口句柄（ForcedTargetHandleForTest）来验证抓取链路 ——
        // 那条路上抓的根本不是用户选的那个窗口，绝不能据此去驱动设备。
        if (ForcedTargetHandleForTest != IntPtr.Zero) return;

        MotionEngine? engine = App.Engine;
        if (engine is null) return;                       // App 还没起来（自检/渲染探针）：不驱动
        long now = Environment.TickCount64;

        if (Volatile.Read(ref _manualEditing) != 0)
        {
            SetFollowReason("正在手动选目标：画面跟随暂停");
            return;
        }

        // ⓪ 正在调活动范围：先把设备让给预览那一路摆位置。
        //    不松手的话本类每 50ms 一发，用户拖完看到的还是旧位置（「滑动时设备没跟着到指定范围」）。
        if (now < Interlocked.Read(ref _previewPauseUntil))
        {
            Volatile.Write(ref _followWant, 0);
            if (Volatile.Read(ref _followActive) != 0)
            {
                Volatile.Write(ref _followActive, 0);
                ReleaseFollowOwnership(engine);
            }
            SetFollowReason("正在调活动范围：画面同步先停手（调完自己接回来）");
            return;
        }

        // 姿态门控一拍只读一次：⑤ 的入场闸门和 ⑦ 的深度/频率必须看同一份裁决。
        // 分开读会出现「这一行判它断了 → 缓降；下一行又判它没断 → 重新接管」，
        // 实测就是每两三秒抖一下（2026-11 真片源复测）。
        PoseMotionSource.PoseFollowSignal poseGate = ReadPoseGate(now);
        // 主体块同样一拍只读一次：⑤ 的入场闸门与 ⑦ 的深度/频率必须看同一份（两处分开读就是逐拍抖动的老毛病）。
        bool blobLive = ReadBlobGate(now, out double blobDepth, out double blobSpeed);

        // ① 正在缓降：先把幅度线性收到中位，收完再交控制权（绝不急停）。
        if (Volatile.Read(ref _followEasing) != 0) { StepFollowEase(engine, now, nowSeconds); return; }

        // ② 用户没让画面驱动设备 —— 默认就是这一条（ScreenWatchEnabled 默认关）。
        //    **只看这一个开关**：画面同步是伴随里独立的一路，不再看「动作来源」，
        //    也不再要求伴随开着 —— 开画面就跟画面，开声音就跟声音，两个都开时画面优先。
        string source = (_cfg.CompanionSource ?? "").Trim().ToLowerInvariant();
        if (!_cfg.ScreenWatchEnabled)
        {
            // 用户说过「你搞的门槛太复杂了我都不知道怎么让它动起来」：原因一律先给一键按钮。
            string why = "要跟着画面动：点上面那个「一键：让画面直接带动设备」，或者手工点「开启同步」";
            Volatile.Write(ref _followWant, 0);
            // 本来在驱动（用户刚把开关关掉 / 换了来源）就走缓降，别让设备停在半路 ——
            // 更别把控制权握在手里（那会让声音响应、游戏桥都发不出去）。
            if (Volatile.Read(ref _followActive) != 0) BeginFollowEase(engine, now, why);
            else SetFollowReason(why);
            return;
        }

        // ③ 让位：脚本 / 游戏桥 / 遥测在占设备时，本类不下发、也不去抢（只把原因说清楚）。
        string? blocker = FollowBlocker(engine, source);
        if (blocker is not null)
        {
            Volatile.Write(ref _followWant, 0);
            // 主动暂停 / 急停 / 关停：原地等，不要缓降（那等于在用户喊停的时候还动一下）。
            // 只有「别人正在用设备」这种让位才收尾缓降。
            FollowLossKind blockerKind = engine.EmergencyStopped || engine.IsPaused
                ? FollowLossKind.StoppedByUser
                : FollowLossKind.TargetExited;
            if (FollowContinuityPolicy.Decide(blockerKind) == FollowLossResponse.Wait)
            {
                SetFollowReason(blocker);
                return;
            }
            if (Volatile.Read(ref _followActive) != 0) BeginFollowEase(engine, now, blocker);
            else SetFollowReason(blocker);
            return;
        }

        // ④ 目标：绑定的那个程序在前台，而且看的正是它的窗口。
        //    丢信号必须分三种：切窗/遮挡（降级继续跑，切回来平滑接上）、真退出（缓降收场）、
        //    主动暂停/关停（不动、不自动重启）。原来一刀切「缓降回中」，
        //    切个窗口再切回来就得从零重新起势，手感是断的。
        bool manualBound = ManualBinding.Bound;
        string targetProblem = manualBound ? ManualTargetProblem() : FollowTargetProblem();
        if (targetProblem.Length > 0)
        {
            FollowLossKind kind = ClassifyTargetProblem(targetProblem);
            if (FollowContinuityPolicy.Decide(kind) == FollowLossResponse.Degrade
                && TryDriveDegraded(engine, now, nowSeconds, kind, targetProblem))
                return;
            Volatile.Write(ref _followWant, 0);
            if (Volatile.Read(ref _followActive) != 0) BeginFollowEase(engine, now, targetProblem);
            else SetFollowReason(targetProblem);
            return;
        }

        // 手动绑定已经明确指定目标，不再依赖自动分类/姿态；丢失也不改跟别的对象。
        if (TryFollowManual(engine, now)) return;

        // ⑤ 入场闸门：像素分过线 **或** 姿态信号正新鲜 —— 二者任一成立就继续。
        //    只用像素分会把「人动得很轻/几乎是静止」判成没事发生，姿态信号根本没机会发力；
        //    姿态开着且这一拍有信号时，直接放行（复位/兜底仍走 ⑦ 的 poseGate 裁决）。
        // 姿态「还算数」：这一拍有效（或还在 PoseHoldMs 保持窗口里），<b>并且</b>速度过了死区。
        // 原来只看「信号有没有」：角色站着不动，姿态关键点自己也会抖，抖出来的速度就能过 1–3 /秒，
        // 于是人没做动作设备也在跟（2026-11-23 用户实测「我角色没有性动作啊」）。
        bool poseValid = _cfg.PoseVisionEnabled && !poseGate.Fallback;
        bool poseLive = poseValid && poseGate.Speed >= Math.Max(0, _cfg.ScreenSyncPoseMinSpeed);
        //    姿态不可用时再看「主体块」：2D 游戏/手绘画面里角色动得很轻，整幅像素分根本不过线（实测 10/10 采样
        //    suspect=false、设备一动不动），而全屏特效又会把它顶过线 —— 只看整幅分两头都不对。
        // 像素这一路还要过四条可拖动的门槛（0 = 不筛）：这是「画面里到底有没有人在干」的第二道闸。
        bool pixelsPass = Volatile.Read(ref _suspect) != 0
            && PassesMin(_cfg.ScreenSyncMinSceneScore, Volatile.Read(ref _sceneScore))
            && PassesMin(_cfg.ScreenSyncMinModelScore, Volatile.Read(ref _modelScore))
            && PassesMin(_cfg.ScreenSyncMinMotion, Volatile.Read(ref _motion))
            && PassesMin(_cfg.ScreenSyncMinRhythm, Volatile.Read(ref _rhythm));
        bool liveNow = pixelsPass || poseLive || blobLive;
        if (Volatile.Read(ref _captureFailed) != 0) liveNow = false;

        // 确认拍数（迟滞）：进门前要连续几拍都在动、收手前要连续几拍都没动，
        // 否则分数在门槛上下漂就会「开始要动 → 没事发生 → 开始要动」每两三秒抽一下。
        if (liveNow) { _liveStreak = Math.Min(_liveStreak + 1, 10000); _deadStreak = 0; }
        else { _deadStreak = Math.Min(_deadStreak + 1, 10000); _liveStreak = 0; }
        int enterFrames = Math.Clamp(_cfg.ScreenSyncEnterFrames, 1, 60);
        int exitFrames = Math.Clamp(_cfg.ScreenSyncExitFrames, 1, 120);
        bool engagedNow = Volatile.Read(ref _followWant) != 0;
        bool live = engagedNow ? _deadStreak < exitFrames : _liveStreak >= enterFrames;

        if (!live)
        {
            // 抓不到帧 = 遮挡/最小化：按「抓取丢失」降级跑（慢速小幅），不是退出。
            if (Volatile.Read(ref _captureFailed) != 0
                && TryDriveDegraded(engine, now, nowSeconds, FollowLossKind.CaptureLost,
                    $"抓不到画面（{_failureReason}）"))
                return;

            // 先别一帧判死：刚还在动、这几帧却「没事发生」，最常见的原因是那块画面被别的窗口盖住了
            // （用户实测：框在浏览器上，鼠标一切窗口就整块变黑，抓回来的不是他的画面）。
            // 这时拿上次真正下发的位置接着跑一小会儿，比当场顿住再重新起势自然得多。
            if (HoldLastMotion(engine, now)) return;

            // 画面好好的、本来就没在动：**不是丢信号**，别缓降、别放控制权 ——
            // 停在原处等着，动起来就平滑接上（急停/暂停/主动关闭由上面几道开关拦住，不会走到这里）。
            if (HoldStill(engine, now)) return;
            Volatile.Write(ref _followWant, 0);
            if (Volatile.Read(ref _followActive) != 0) BeginFollowEase(engine, now, "画面本来没在动，设备还在过渡：先收回到中位");
            else SetFollowReason("画面里本来就没在动：不动，等它动起来（不是退出）");
            return;
        }

        // ⑥ 走到这里 = 画面同步要驱动设备了。
        //    必须在真的拿到控制权**之前**就把「想要」立起来：伴随的自动动作还在跑时本类拿不到
        //    直接下发权，而规则引擎正是靠这个标记决定让位 —— 立晚了两边会互相等。
        if (Volatile.Read(ref _followWant) == 0)
        {
            // 一小节只记一次：这一行是「画面确实想要驱动设备」的唯一凭据 ——
            // 之后不动就是下游的事（没连设备 / 被别的驱动占着），不用怀疑识别。
            AppLogger.Info($"画面同步：开始要动 —— 深度 {Volatile.Read(ref _followDepth) * 100:0}% · 每秒 {Volatile.Read(ref _followHz):0.0} 下");
        }
        Volatile.Write(ref _followWant, 1);

        if (!engine.CanAcceptDirectInput)
        {
            SetFollowReason("已经过线了，但设备正在过渡/被别人占着，等它让出来");
            return;
        }
        if (!engine.TryClaimDirectInput(FollowOwner))
        {
            SetFollowReason($"「{engine.DriverLabel}」正在控制设备，画面同步让位");
            return;
        }

        // ⑦ 逐帧位置跟随（画面同步）优先：质量门过线就按画面位置逐帧下发。
        //    过不了（周期自相关不够 / ROI 几乎没动）才退回原来的「节奏 + 语汇」路径 ——
        //    那条路原样保留，作为信号丢失、周期太差时的兜底。
        // ⑦a 位置镜像（真同步）：框里人往下沉多少，设备就推到那个位置；人动到哪就跟到哪。
        double[]? mirrorAxes = TryBuildMirrorAxes(engine, poseGate, out string mirrorText);
        if (mirrorAxes is not null)
        {
            if (engine.TrySendDirectAxes(mirrorAxes, Math.Clamp(_frameIntervalEmaMs / 1000.0, 0.02, 0.25)))
            {
                Volatile.Write(ref _followActive, 1);
                Volatile.Write(ref _followMainAxis, Math.Clamp(mirrorAxes[0], 0, 100));
                _lastFollowSendMs = now;
                NoteSentAxes(mirrorAxes, now);
                SetFollowReason(mirrorText);
            }
            else
            {
                SetFollowReason("已经过线了，但这一拍没能下发（设备正在过渡），下一拍再试");
                if (Volatile.Read(ref _followActive) == 0 && Volatile.Read(ref _followEasing) == 0)
                    ReleaseFollowOwnership(engine);
            }
            return;
        }

        double[]? syncAxes = TryBuildSyncAxes(engine, out double syncInterpolation, out string syncText);
        if (syncAxes is not null)
        {
            if (engine.TrySendDirectAxes(syncAxes, syncInterpolation))
            {
                Volatile.Write(ref _followActive, 1);
                Volatile.Write(ref _followDepth, _sync.RelativeDepth);
                Volatile.Write(ref _followHz, _sync.Hz);
                Volatile.Write(ref _followMainAxis, Math.Clamp(syncAxes[0], 0, 100));
                _lastFollowSendMs = now;
                NoteSentAxes(syncAxes, now);
                SetFollowReason(syncText);
            }
            else
            {
                SetFollowReason("已经过线了，但这一拍没能下发（设备正在过渡），下一拍再试");
                // 没真发出去就别占着控制权：占着会让声音响应 / 游戏桥也发不出去，
                // 而看门狗只盯「已经在驱动」的情况，不会替这条路径收尾。
                if (Volatile.Read(ref _followActive) == 0 && Volatile.Read(ref _followEasing) == 0)
                    ReleaseFollowOwnership(engine);
            }
            return;
        }

        // ⑦b 兜底：原来的「节奏 + 语汇」路径。姿态裁决在方法开头已经读过（和 ⑤ 同一份），
        //    两路同源同拍，杜绝「深度用姿态、频率却混进画面分」的半拍错位与方向突变。
        //    三种信号源，优先级：姿态（含保持期）> 主体块 > 画面分。
        PoseMotionSource.PoseFollowSignal drive = poseGate;
        if (blobLive && poseGate.Fallback)
            drive = new PoseMotionSource.PoseFollowSignal(blobDepth, blobSpeed, false);
        double depth = SlewFollowDepth(FollowComputeDepth(drive));
        double range = FollowComputeRange(engine, depth);
        double hz = FollowComputeHz(engine, range, drive);
        double dt = FollowDeltaSeconds(nowSeconds);

        // 画面 + 声音同开：两路**各自**都给一份（深度, 节奏），交给 SignalFusion 互证融合 ——
        // 说的是同一件事就取几何平均、分歧就按置信度用强的那一路，弱的那一路只留轻牵引。
        // 不再「声音一有 BPM 就把画面节奏硬覆盖」：那是盲从鼓点，配乐快的素材会把动作带飞。
        bool audioRhythm = TryReadAudioRhythm(out double audioBump, out double audioHz, out double audioConf, out string audioSource);
        string fuseDriver = "画面";
        if (audioRhythm)
        {
            var screenLane = new SignalLane(true, depth, hz, 0.80);
            double audioDepth = Math.Clamp(_cfg.AudioDepthEffective / 100.0 * (0.55 + 0.45 * audioBump), 0.05, 1.0);
            var audioLane = new SignalLane(audioHz > 0, audioDepth,
                audioHz * Math.Clamp(_cfg.AudioSpeedEffective, 0.3, 3.0), audioConf);
            var fused = SignalFusion.Fuse(screenLane, audioLane, true, true);
            if (fused.Confidence > 0)
            {
                depth = SlewFollowDepth(Math.Clamp(fused.Depth, 0.05, 1.0));
                range = FollowComputeRange(engine, depth);
                hz = Math.Clamp(fused.Hz, FollowHzMin, FollowHzMax);
                fuseDriver = audioSource.Length > 0 ? fused.Driver + "（" + audioSource + "）" : fused.Driver;
            }
        }

        // 降级中（刚切走过窗口 / 画面抓不到）：恢复正常这一拍就开始平滑回到原来的深度与节奏。
        if (_degradeBlend > 0) _degradeBlend = FollowContinuityPolicy.Smooth(_degradeBlend, 0, dt, FollowContinuityPolicy.ResumeSeconds);
        if (_degradeBlend > 0.001)
        {
            depth *= 1 - _degradeBlend * (1 - FollowContinuityPolicy.DegradedDepthScale);
            range = FollowComputeRange(engine, depth);
            hz *= 1 - _degradeBlend * (1 - FollowContinuityPolicy.DegradedSpeedScale);
        }

        // 挣扎连点：只在绑定的游戏在前台、用户真的在连点时才有加成。加在当前这个动作上、不换招，
        // 停手后由 StruggleAccelerator 自己衰减回原状态；两路口径统一用引擎暴露的倍数。
        if (engine.StruggleSpeedScale > 1.0001 || engine.StruggleDepthBoost > 0.0001)
        {
            depth = Math.Clamp(depth * (1 + engine.StruggleDepthBoost), 0.05, 1.0);
            range = FollowComputeRange(engine, depth);
            hz = Math.Clamp(hz * Math.Clamp(engine.StruggleSpeedScale, 1.0, 1.4), FollowHzMin, FollowHzMax);
        }

        // 相位推进 + 有界修正：
        //   ① 先按自己的频率走一拍（φ += f·dt）；
        //   ② 再按 ScreenPhaseLock 往画面节奏的相位拉 —— φ += k·wrap(误差)，k = 0.08 + 0.17×锁相强度
        //      （0.35 的默认值 → k≈0.14，落在 0.1–0.25 这个舒服区间里；锁相强度 0 = 完全不锁，只跟速度）；
        //   ③ 误差大到离谱（跟丢了 / 被卡了一拍）也**不**当场重置，连续 FollowPhaseResyncBeats 拍都对不上才重锁。
        _followPhase = Frac(_followPhase + hz * dt);
        double lockGain = _cfg.ScreenPhaseLock <= 0
            ? 0
            : 0.08 + 0.17 * Math.Clamp(_cfg.ScreenPhaseLock, 0, 1);
        if (lockGain > 0 && _rhythmPeakSeconds > double.NegativeInfinity)
        {
            // 画面那一拍「动得最猛」的时刻记为参考相位 0；本类的相位就是动作语汇「抽插」的内部相位，
            // 于是目标 = 参考相位 + FollowPhaseOffset（0.45 = 推到最里面）——
            // 「画面里动到最猛的那一下，设备也正推到最里面」。
            double error = ReferencePhase(nowSeconds, hz) + FollowPhaseOffset - _followPhase;
            error -= Math.Round(error);                  // wrap 到 ±半拍
            _followPhase = Frac(_followPhase + lockGain * error);
            if (Math.Abs(error) > FollowPhaseResyncError) _followPhaseMisses++;
            else _followPhaseMisses = 0;
            if (_followPhaseMisses >= FollowPhaseResyncBeats)
            {
                _followPhase = Frac(ReferencePhase(nowSeconds, hz) + FollowPhaseOffset);
                _followPhaseMisses = 0;
            }
        }

        // 插值时间比下发间隔略长：13fps 配 77ms 插值会一顿一顿，0.12 秒左右才连得起来。
        double interpolation = Math.Clamp(dt * 1.6, 0.06, 0.30);

        // 超前量 = 这份画面到此为止已经过时多久：抓帧+分析耗时 + 设备走到指定位置还要的时间
        //（插值按一半算）。不补这一段，设备永远比画面慢半拍：相位锁追的是**过去**的峰值，
        // 锁定之后再晚一个定值，观感就是「节奏对但总拖在后面」。
        double leadSeconds = Math.Clamp(_lastCaptureSeconds + interpolation * 0.5, 0, 0.25);
        double sendPhase = Frac(_followPhase + hz * leadSeconds);
        // 语汇按深度换：浅场景用「渐强」（正弦往返、多轴螺旋，慢而连续），
        // 深场景用「抽插」（0→0.45 快速推到底、之后慢收）。同一条 Voice 走到底会显得单调。
        ComfortProfile profile = engine.ActiveComfortProfile;
        double[] values = depth < FollowSlowShapeDepth
            ? MotionVocabulary.Render(MotionEventKind.Swell, 1.0, sendPhase, range, engine.MultiAxisMotion)
            : MotionVocabulary.FollowStroke(sendPhase, range, profile.ThrustRatio, profile.DwellRatio, engine.MultiAxisMotion);

        // 方向感知：画面在往上跑（主体靠近）→ 行程重心往「推」偏；往下跑（退开）→ 往「收」偏。
        // 只搬重心、不改幅度，而且顶到端点就停 —— 否则大行程时会撞 0/100 被截平，形状反而变形。
        double approach = Volatile.Read(ref _followApproach);
        double leanHeadroom = Math.Max(0, 50 - range);
        double lean = Math.Clamp(approach * range * FollowDirectionLean, -leanHeadroom, leanHeadroom);
        if (Math.Abs(lean) > 0.01)
            values[0] = Math.Clamp(values[0] + lean, 0, 100);

        // 声音的强弱叠在行程上：响的时候这一下顶得更深，安静时收回浅处。
        if (audioRhythm && audioBump > 0)
            values[0] = Math.Clamp(values[0] + audioBump * range * FollowAudioPush, 0, 100);

        ApplySyncRange(values);
        if (engine.TrySendDirectAxes(values, interpolation))
        {
            Volatile.Write(ref _followActive, 1);
            Volatile.Write(ref _followDepth, depth);
            Volatile.Write(ref _followHz, hz);
            Volatile.Write(ref _followMainAxis, Math.Clamp(values[0], 0, 100));
            _lastFollowSendMs = now;
            NoteSentAxes(values, now);
            double lockPercent = Math.Clamp(_cfg.ScreenPhaseLock, 0, 1);
            // 用主体尺寸判的时候就说「主体在靠近」，退回整幅位移兜底时老实说「画面在靠近」。
            bool fromSubject = Volatile.Read(ref _subjectDirection);
            string what = fromSubject ? "主体" : "画面";
            string direction = Math.Abs(approach) < 0.15
                ? (fromSubject ? " · 没看出主体在靠近还是退开" : " · 画面没有明显上下位移")
                : approach > 0 ? $" · {what}在靠近 {approach * 100:0}%" : $" · {what}在退开 {-approach * 100:0}%";
            string lockText = lockPercent > 0 ? $" · 相位锁定 {lockPercent * 100:0}%" : " · 不锁相位（只跟速度）";
            // 姿态在驱动时别再说「画面没有明显上下位移」这种画面分的话 —— 那一刻设备跟的是髋部，跟画面纹理无关。
            SetFollowReason(!drive.Fallback
                ? blobLive && poseGate.Fallback
                    ? $"正在跟着画面里的动作动：深度 {depth * 100:0}% · 每秒 {hz:0.0} 下{lockText}"
                    : $"正在跟着动作动：深度 {depth * 100:0}% · 每秒 {hz:0.0} 下{lockText}"
                : $"正在跟画面动：深度 {depth * 100:0}% · 每秒 {hz:0.0} 下{lockText}{direction}");
        }
        else
        {
            SetFollowReason("已经过线了，但这一拍没能下发（设备正在过渡），下一拍再试");
            // 没真发出去就别占着控制权：占着会让声音响应 / 游戏桥也发不出去，
            // 而看门狗只盯「已经在驱动」的情况，不会替这条路径收尾。
            if (Volatile.Read(ref _followActive) == 0 && Volatile.Read(ref _followEasing) == 0)
                ReleaseFollowOwnership(engine);
        }
    }

    /// <summary>
    /// 更新「主体块」：整帧切成 <see cref="BlobCols"/>×<see cref="BlobRows"/> 格，每格的平均亮度差就是这一格的运动量；
    /// 取「变化最集中的一块」当主体，包围盒做指数平滑。
    /// 为什么按格而不是按像素：像素级包围盒会被一个亮点拉偏；格子天然带空间聚合，
    /// 雨点、火花、镜头晃这类撒开的噪声落在很多格里，反而过不了 <see cref="BlobMaxCoverage"/> 这一关。
    /// 这一路是给<b>没有骨架的画面</b>准备的（2D 游戏、手绘动画、拍得很远的真人）——
    /// 实测 YOLO 对 2D 手绘角色 20 帧只出 1–5 帧，等它不如直接看哪一块在动。
    /// </summary>
    private void UpdateSubjectBlob(bool hadPrev)
    {
        if (!hadPrev) { _blobValid = false; return; }

        int cellPixels = Math.Max(1, (FrameWidth * FrameHeight) / (BlobCols * BlobRows));
        double peak = 0;
        for (int i = 0; i < _blobCellNow.Length; i++)
        {
            double avg = _blobCellNow[i] / cellPixels;
            _blobCellNow[i] = avg;
            if (avg > peak) peak = avg;
        }
        // 这一帧没找到主体：**不清**上次的结果，让 BlobFreshMs 的保鲜期去判过期。
        // 清了就会变成「有动/没动」两帧一切换，⑤ 的入场闸门随之开关 → 设备每两帧缓降一次（实测就是这个抖动）。
        if (peak < BlobMinEnergy) return;

        double cut = Math.Max(BlobMinEnergy, peak * BlobCellCut);
        double left = 1, top = 1, right = 0, bottom = 0;
        int cells = 0;
        double energySum = 0, ySum = 0;
        for (int r = 0; r < BlobRows; r++)
        {
            for (int c = 0; c < BlobCols; c++)
            {
                double cellEnergy = _blobCellNow[r * BlobCols + c];
                if (cellEnergy < cut) continue;
                cells++;
                energySum += cellEnergy;
                ySum += cellEnergy * ((r + 0.5) / BlobRows);
                double x0 = c / (double)BlobCols;
                double y0 = r / (double)BlobRows;
                if (x0 < left) left = x0;
                if (y0 < top) top = y0;
                if (x0 + 1.0 / BlobCols > right) right = x0 + 1.0 / BlobCols;
                if (y0 + 1.0 / BlobRows > bottom) bottom = y0 + 1.0 / BlobRows;
            }
        }

        // 一格都没过线，或者过了半个画面（整屏都在动）：都当「没有主体」，
        // 宁可退回老的画面分，也别瞎认一块当人。
        if (cells == 0 || cells > BlobCols * BlobRows * BlobMaxCoverage) return;

        if (_blobValid)
        {
            _blobX0 += (left - _blobX0) * BlobSmoothing;
            _blobY0 += (top - _blobY0) * BlobSmoothing;
            _blobX1 += (right - _blobX1) * BlobSmoothing;
            _blobY1 += (bottom - _blobY1) * BlobSmoothing;
        }
        else
        {
            _blobX0 = left; _blobY0 = top; _blobX1 = right; _blobY1 = bottom;
        }
        _blobValid = true;
        long now = Environment.TickCount64;
        _blobMs = now;

        // 主体高度取「热点能量的加权中心」，不取包围盒中心：特写片里热点会散到画面四角，
        // 包围盒一帧 (0,0,1,1)、一帧缩到角落 (0.81,0,0.94,0.11)，盒子中心跳得毫无意义；
        // 加权中心是连续的，动作上下起伏才真的变成深度。
        double centerY = energySum > 0 ? ySum / energySum : (_blobY0 + _blobY1) * 0.5;
        _blobHistMs[_blobHistHead] = now;
        _blobHistY[_blobHistHead] = centerY;
        _blobHistHead = (_blobHistHead + 1) % BlobHistorySize;
        if (_blobHistCount < BlobHistorySize) _blobHistCount++;
    }

    /// <summary>
    /// 这一拍的主体块信号（深度 0–100、速度 深度单位/秒）—— 口径与姿态完全一致，
    /// 好让同一套 <see cref="FollowComputeDepth"/> / <see cref="FollowComputeHz"/> 直接吃。
    /// 深度 = 时间窗里主体上下走动的幅度（动作大 → 行程深）；速度 = 相邻两点的平均抖动速度。
    /// 返回 false 时必须当「没有信号」：绝不拿 0 当深度（那就是「设备不动」）。
    /// </summary>
    private bool TryReadSubjectBlob(out double depth0To100, out double speedPerSecond)
    {
        depth0To100 = 0;
        speedPerSecond = 0;
        if (!_blobValid || _blobHistCount < 4) return false;
        long now = Environment.TickCount64;
        if (now - _blobMs > BlobFreshMs) return false;

        double low = double.MaxValue;
        double high = double.MinValue;
        double travel = 0;
        double spanSeconds = 0;
        double previousY = double.NaN;
        long previousMs = 0;
        int used = 0;
        for (int k = 0; k < _blobHistCount; k++)
        {
            int idx = (_blobHistHead - _blobHistCount + k + BlobHistorySize * 2) % BlobHistorySize;
            long ms = _blobHistMs[idx];
            if (now - ms > BlobWindowMs) continue;
            double y = _blobHistY[idx];
            if (y < low) low = y;
            if (y > high) high = y;
            if (!double.IsNaN(previousY) && ms > previousMs)
            {
                double dt = (ms - previousMs) / 1000.0;
                if (dt >= 0.02 && dt <= 0.5) { travel += Math.Abs(y - previousY); spanSeconds += dt; }
            }
            previousY = y;
            previousMs = ms;
            used++;
        }
        // 主体这一段时间里几乎没上下动（高==低）也要算有信号：那只是「动作小」，不是「没有主体」——
        // 拿它当没有信号会让入场闸门逐拍开关（缓降与接管来回抽，正是姿态那条线当初的同一个病）。
        if (used < 4) return false;

        // 深度：幅度 0.05（几乎不动）→ 35，0.30（大幅起落）→ 90。给个底部，别让深度为 0（=设备不动）。
        depth0To100 = Math.Clamp(24 + (high - low) * 220, 24, 92);
        // 速度：平均每秒走过多少「画面高度」×100 换成深度单位/秒 —— 和姿态是同一量纲。
        speedPerSecond = spanSeconds > 0
            ? Math.Clamp(travel / spanSeconds * 100.0, 0, PoseMotionSource.MaxSpeedPerSecond)
            : 0;
        _blobHeldDepth = depth0To100;
        _blobHeldSpeed = speedPerSecond;
        _blobSignalMs = now;
        return true;
    }

    /// <summary>
    /// 主体块门控（对应姿态的 <see cref="ReadPoseGate"/>）：有效就用这一刻的值；
    /// 断了但还在 <see cref="BlobHoldMs"/> 保持期里就沿用最后一次有效值（不跳、不混画面分）；
    /// 超时才返回 false，交回画面分那条路。这样「画面静一拍」不会把设备踢进缓降。
    /// </summary>
    private bool ReadBlobGate(long nowMs, out double depth0To100, out double speedPerSecond)
    {
        if (TryReadSubjectBlob(out depth0To100, out speedPerSecond)) return true;
        if (_blobSignalMs > 0 && _blobHistCount >= 4 && nowMs - _blobSignalMs < BlobHoldMs)
        {
            depth0To100 = _blobHeldDepth;
            speedPerSecond = _blobHeldSpeed;
            return true;
        }
        depth0To100 = 0;
        speedPerSecond = 0;
        return false;
    }

    /// <summary>
    /// 读姿态运动源这一拍的信号。返回 false = 这一拍没有姿态信号（开关关着 / 还没算出来 / 结果过期 /
    /// 画面里没有可用的髋+踝），调用方必须走原来的画面链路。
    /// </summary>
    private bool TryPoseMotion(out double depth0To100, out double speedPerSecond)
    {
        depth0To100 = 0;
        speedPerSecond = 0;
        return _cfg.PoseVisionEnabled && _poseMotion.TryRead(out depth0To100, out speedPerSecond);
    }

    /// <summary>
    /// 姿态门控：这一拍读<b>一次</b>姿态（深度和速度同拍拿），交给
    /// <see cref="PoseMotionSource.ResolveFollowSignal"/> 裁决本轮信号源，保持状态由本类持有。
    /// 三态：姿态有效 → 深度/速度/频率全用姿态；断了但没超过保持期 → 沿用最后一次有效值（不跳、不混画面分）；
    /// 断超过保持期（<see cref="PoseHoldMs"/>）→ 回落画面分。这是唯一的回落点，
    /// 回落日志只在「跟姿态 → 回落」翻转的那一刻写一条，之后每一拍静默。
    /// </summary>
    /// <summary>
    /// 门槛判定：<paramref name="min"/> 是用户拖出来的线，0 = 不筛（老行为）；
    /// 非 0 时实际值必须达到它才算「过线」。用「大于等于」而不是严格大于：拖到正好等于当前值时就该放行。
    /// </summary>
    private static bool PassesMin(double min, double value)
        => !(min > 0) || value >= min;

    private PoseMotionSource.PoseFollowSignal ReadPoseGate(long nowMs)
    {
        bool poseOk = TryPoseMotion(out double poseDepth, out double poseSpeed);
        PoseMotionSource.PoseFollowSignal gate = PoseMotionSource.ResolveFollowSignal(
            poseOk, poseDepth, poseSpeed, nowMs, _poseLastValidMs, _poseHeldDepth, _poseHeldSpeed, PoseHoldMs);
        if (poseOk)
        {
            _poseLastValidMs = nowMs;         // 保持窗口从「最后一次有效」起算，保持期间不续期
            _poseHeldDepth = poseDepth;
            _poseHeldSpeed = poseSpeed;
        }
        if (gate.Fallback)
        {
            if (Volatile.Read(ref _poseGateFellBack) == 0)
            {
                Volatile.Write(ref _poseGateFellBack, 1);
                AppLogger.Info(_poseLastValidMs > 0
                    ? $"画面同步：姿态信号中断 {nowMs - _poseLastValidMs} 毫秒，改跟画面"
                    : "画面同步：姿态信号还没建立过，直接跟画面");
            }
        }
        else
        {
            Volatile.Write(ref _poseGateFellBack, 0);   // 姿态回来了：清标记，下次再断还会写一条
        }
        return gate;
    }

    /// <summary>
    /// 深度 0–1 ∝ 场景强度：从<b>退出判定线</b>到 1.0 之间线性映射。
    /// 为什么不直接用 SceneScore 当深度：判定线（灵敏度 1.0 时约 0.38）刚过线时深度就已经 0.38，
    /// 一过线设备就猛地动起来。从退出线开始归一化，过线那一刻深度接近 0，之后随强度长上去。
    /// </summary>
    private double FollowComputeDepth(PoseMotionSource.PoseFollowSignal poseGate)
    {
        // 姿态模式（含保持期）：深度直接就是「髋中点相对踝中点（脚踝看不见时用画面下缘）的竖直位移」，
        // 占参考线的比例 → 0–100 → 这里除回 0–1。这条路上不做场景分那套归一化 ——
        // 场景分回答的是「画面里有多少动作」，而这里量的是「沉到多深」。
        // 门控保证 !Fallback 时深度 100% 来自姿态（这一拍的有效值，或保持期里的最后一次有效值），
        // 画面分一个字节都不参与；绝不把「没信号」当成深度 0 或场景强度。
        if (!poseGate.Fallback) return Math.Clamp(poseGate.Depth / 100.0, 0, 1);

        // 唯一回落点：姿态失效超过保持期（PoseHoldMs）才走这里 —— 场景分那套原样保留。
        double score = Math.Clamp(SceneScore, 0, 1);
        double floor = Math.Clamp(ThresholdOff, 0.05, 0.9);
        double depth = (score - floor) / Math.Max(0.05, 1 - floor);
        // 过线了就至少给一点幅度：深度 0 等于设备不动，用户会以为坏了。
        return Math.Clamp(Math.Max(depth, 0.15), 0, 1);
    }

    /// <summary>
    /// 速度（Hz）∝ 检测到的节奏频率，再夹进「舒服范围」。
    /// 舒服范围怎么算：幅度 A、频率 f 的正弦，峰值速度约 2πfA（行程百分比/秒），
    /// 而舒适档给的正是这个上限（170 / 360 / 620 %/s），所以 f 的上限 = 速度上限 ÷ (2πA)。
    /// 引擎里的安全限速器还会再管一道，这里先夹一次是为了别让它一直顶着限速器跑（那样动作会变形）。
    /// </summary>
    /// <summary>
    /// 这一拍给的半幅（% 行程）：基准 × 舒适档上限 × √深度。
    /// 为什么开方：深度是按场景分线性归一化的，线性乘上去会让「刚过线」永远只有十几点；
    /// 开方把中低深度整体抬起来（过线那一刻 already ≈0.39×基准），高深度还是封在基准上。
    /// </summary>
    /// <summary>
    /// 把 0–100 的轴值搬进用户设的「画面同步活动范围」：主轴（L0）按 [下限, 上限] 线性铺满，
    /// 其它轴（旋转 / 倾斜）按同一个比例围绕中位收放 —— 否则缩了范围之后侧轴还是原幅度，看着就不协调。
    /// 为什么不是直接夹：直接夹会把正弦削成梯形，设备在两端「顶住」一下再回来，手感是撞墙。
    /// </summary>
    private static double MapIntoRange(double value, int min, int max, bool mainAxis)
    {
        double lo = Math.Clamp(min, 0, 100);
        double hi = Math.Clamp(max, lo, 100);
        double span = hi - lo;
        double v = Math.Clamp(value, 0, 100);
        if (span >= 99.5) return v;
        if (mainAxis) return Math.Clamp(lo + v * span / 100.0, lo, hi);
        double center = (lo + hi) / 2.0;
        return Math.Clamp(center + (v - 50) * span / 100.0, 0, 100);
    }

    /// <summary>这一拍下发前统一过一遍活动范围（L0 铺满，其余按比例收）。</summary>
private void ApplySyncRange(double[] values)
    {
        int lo = _cfg.ScreenSyncMinPosition, hi = _cfg.ScreenSyncMaxPosition;
        for (int i = 0; i < values.Length; i++)
            values[i] = MapIntoRange(values[i], lo, hi, i == 0);
    }


    // ── 位置镜像（真同步）+「画面给位置、声音给节奏」──────────────────────
    private const double MirrorDeadzone = 0.010;   // 竖直位置死区（0–1）：小于它当没动，免得设备一直小抖
    private const double FollowAudioPush = 0.30;   // 声音顶多把这一下再推 30% 行程
    private double _mirrorPosition = double.NaN;   // 平滑后的竖直位置 0–1
    private double _mirrorSent = double.NaN;       // 上次真正下发的竖直位置（死区用）

    /// <summary>
    /// 读一眼声音侧：画面和声音同开时，声音负责「节奏」，画面负责「位置」。
    /// 返回 false = 声音没在响（或者这条配合被关掉了），画面就自己按画面节奏走。
    /// </summary>
    private bool TryReadAudioRhythm(out double bump, out double hz, out double confidence, out string source)
    {
        bump = 0;
        hz = 0;
        confidence = 0;
        source = "";
        if (!_cfg.ScreenSyncAudioRhythm || !_cfg.AudioReactiveEnabled) return false;
        AudioReactiveService? audio = App.AudioReactive;
        if (audio is null) return false;
        double energy = Math.Clamp(audio.Energy, 0, 1);
        double pulse = Math.Clamp(audio.BeatPulse, 0, 1);
        // 静音时不掺和：声音一停就纯跟画面，免得「没声音了反而顶得更深」这种怪事。
        if (!audio.WantsDevice && energy <= 0.05 && pulse <= 0.05) return false;
        bump = Math.Clamp(Math.Max(pulse, energy * 0.7), 0, 1);

        // 节奏不直接拿谱通量 BPM：先看这一帧的分类型结论（主体 / 配乐 / 只有配乐要打折）。
        RhythmHint hint = audio.GetRhythmHint();
        if (hint.Valid && hint.Hz > 0)
        {
            hz = hint.Hz;
            confidence = Math.Clamp(hint.Confidence, 0, 1);
            source = hint.Source;
        }
        return true;
    }

    private bool TryReadAudioRhythm(out double bump, out double bpm)
    {
        bool ok = TryReadAudioRhythm(out bump, out double hz, out _, out _);
        bpm = hz * 60.0;
        return ok && bpm >= 20;
    }

    /// <summary>
    /// 位置镜像（画面同步的「真同步」路径）：框里人往下沉多少，设备就推到多深 —— 不是「深度 × 正弦」来回抽。
    /// 返回 null = 这一拍不能镜像（没人在框里 / 用户关掉了），调用方往下退。
    /// </summary>
    private bool TryFollowManual(MotionEngine engine, long now)
    {
        ManualTrackSignal signal;
        lock (_manualLock)
        {
            signal = _manualTracker.Signal;
            if (!signal.Bound) return false;
            if (_manualAtMs > 0 && now - _manualAtMs > 500)
            {
                _manualTracker.Invalidate("画面停更了，请重绑");
                signal = _manualTracker.Signal;
            }
        }
        if (!signal.Located || signal.Lost)
        {
            Volatile.Write(ref _followWant, 0);
            if (Volatile.Read(ref _followActive) != 0) BeginFollowEase(engine, now, signal.Status);
            else SetFollowReason(signal.Status);
            return true;
        }
        Volatile.Write(ref _followWant, 1);
        if (!engine.CanAcceptDirectInput || !engine.TryClaimDirectInput(FollowOwner))
        { SetFollowReason("手动目标已跟住，等待设备让位"); return true; }
        double position = _cfg.ScreenSyncInvertDirection ? 1 - signal.Position : signal.Position;
        double range = Math.Clamp(FollowComputeRange(engine, 1.0), 5, 50);
        double[] values = _syncAxes;
        Array.Fill(values, 50);
        values[0] = Math.Clamp(50 + (position - 0.5) * 2 * range, 0, 100);
        if (_cfg.AutoTwistEnabled && engine.MultiAxisMotion) values[3] = AutoTwistLink.ComputeLinked(values[0],
            _cfg.AutoTwistAmplitudePercent, _cfg.LovenseRotateTopPercent, _cfg.LovenseRotateDeepPercent);
        ApplySyncRange(values);
        if (engine.TrySendDirectAxes(values, Math.Clamp(_frameIntervalEmaMs / 1000.0, 0.02, 0.25)))
        {
            Volatile.Write(ref _followActive, 1);
            Volatile.Write(ref _followDepth, position);
            Volatile.Write(ref _followMainAxis, Math.Clamp(values[0], 0, 100));
            Volatile.Write(ref _followHz, 0);
            _lastFollowSendMs = now;
            _holdUntilMs = 0;
            NoteSentAxes(values, now);
            SetFollowReason($"手动目标 · 位置 {position * 100:0}% · 匹配 {signal.Confidence * 100:0}%");
        }
        else SetFollowReason("手动目标已跟住，设备暂未接收位置");
        return true;
    }

    private double[]? TryBuildMirrorAxes(MotionEngine engine, PoseMotionSource.PoseFollowSignal poseGate, out string statusText)
    {
        statusText = "";
        if (!_cfg.ScreenSyncMirrorPose) return null;
        if (poseGate.Fallback) return null;            // 框里没人：不镜像，交给下面的兜底/缓降
        double norm = Math.Clamp(poseGate.Depth / 100.0, 0, 1);
        if (_cfg.ScreenSyncInvertDirection) norm = 1 - norm;
        // 平滑：关键点逐帧会抖，直接映射就是设备小抖；0.45 约两三帧跟上，手感还跟手。
        _mirrorPosition = double.IsNaN(_mirrorPosition) ? norm : _mirrorPosition + (norm - _mirrorPosition) * 0.45;
        if (!double.IsNaN(_mirrorSent) && Math.Abs(_mirrorPosition - _mirrorSent) < MirrorDeadzone)
            _mirrorPosition = _mirrorSent;             // 抖不到 1% 就当没动
        _mirrorSent = _mirrorPosition;

        double range = Math.Clamp(FollowComputeRange(engine, _mirrorPosition), 5, 90);
        bool audioOn = TryReadAudioRhythm(out double audioBump, out double audioBpm);
        double[] values = _syncAxes;
        double pos = 50 + (_mirrorPosition - 0.5) * 2.0 * range;
        if (audioOn && audioBump > 0) pos += audioBump * range * FollowAudioPush;   // 声音越响，这一下顶得更深
        values[0] = Math.Clamp(pos, 0, 100);
        values[1] = 50;
        values[2] = 50;
        values[3] = 50;
        if (_cfg.AutoTwistEnabled && engine.MultiAxisMotion)
        {
            values[3] = AutoTwistLink.ComputeLinked(
                values[0], _cfg.AutoTwistAmplitudePercent,
                _cfg.LovenseRotateTopPercent, _cfg.LovenseRotateDeepPercent);
        }
        values[4] = 50;
        values[5] = 50;
        if (_cfg.ScreenSyncTiltEnabled && engine.MultiAxisMotion)
        {
            values[4] = Math.Clamp(_sync.R1, 0, 100);
            values[5] = Math.Clamp(_sync.R2, 0, 100);
        }
        ApplySyncRange(values);
        string audioText = audioOn
            ? (audioBpm > 0 ? $" · 跟声音的拍子 {audioBpm:0} BPM" : " · 跟着声音的强弱加重")
            : "";
        statusText = $"正在跟着人同步：人沉到 {_mirrorPosition * 100:0}% · 推 {values[0]:0}%{audioText}";
        return values;
    }

    private double FollowComputeRange(MotionEngine engine, double depth)
    {
        double comfort = Math.Clamp(engine.ActiveComfortProfile.MaxIntensity, 0.3, 1.0);
        double presence = Math.Sqrt(Math.Max(0.15, Math.Clamp(depth, 0, 1)));
        // 深度：50 = 原来的幅度基准；100 = 两倍行程，5 = 几乎不动。只缩放行程，不动活动范围的上下限（那是 MapIntoRange 的事）。
        double depthScale = _cfg.ScreenDepthEffective / 50.0;
        return Math.Clamp(FollowAmplitudeBase * comfort * presence * depthScale, 5, 90);
    }

    private double FollowComputeHz(MotionEngine engine, double range, PoseMotionSource.PoseFollowSignal poseGate)
    {
        double hz;
        // 节奏倍率（界面「节奏」滑条）：1 = 原速，0.3 = 三倍慢，3 = 三倍快。机械上限仍由下面的 maxHz 兜住。
        double speedScale = _cfg.ScreenSpeedEffective;
        if (!poseGate.Fallback)
        {
            // 姿态模式（含保持期）：频率由「相邻帧位移的速度」直接定 —— 这一拍要走的半幅已经在 range 里，
            // 速度 ÷ (2π×半幅) 就是恰好跟上这个速度该用的频率（和下面限速那条推导是同一个式子）。
            // 人停住（速度 0）时落到 FollowHzMin：画面同步本来就是连续动作，定住不动反而像卡住了。
            // 保持期里用的是最后一次有效速度（见 ReadPoseGate），不混画面节奏。
            hz = Math.Max(PoseMotionSource.SpeedToHz(poseGate.Speed, range), FollowHzMin);
        }
        else
        {
            // 唯一回落点：姿态失效超过保持期（PoseHoldMs）才用画面节奏。
            hz = Volatile.Read(ref _rhythmHz);
            if (!double.IsFinite(hz) || hz < RhythmMinHz) hz = FollowFallbackHz;   // 节奏没测出来：用兜底频率，别定住不动
        }
        hz = Math.Clamp(hz * speedScale, FollowHzMin, FollowHzMax * speedScale);

        // travel 必须和实际下发的 range 用同一个数：用 depth 估会让上限偏高，
        // 于是实得行程被限速器吃掉（仿真：30@1.9Hz 只走出 87%，深处还会掉到 53%）。
        double travel = Math.Max(5, range);
        double maxHz = engine.ActiveComfortProfile.MaxAxisSpeedPerSecond / (Math.Tau * travel);
        return Math.Clamp(Math.Min(hz, maxHz), FollowHzMin, FollowHzMax * speedScale);
    }

    /// <summary>
    /// 画面同步（逐帧位置跟随）：质量门过线时组装六轴（L0 跟位置，R1/R2 可选近似倾斜），
    /// 返回 null 表示这一拍不能逐帧跟 —— 调用方必须退回原来的「节奏 + 语汇」路径。
    /// L0 = 50 ± 相对深度 × 行程；行程沿用 <see cref="FollowComputeRange"/>（舒适档/限速都还在）。
    /// </summary>
    private double[]? TryBuildSyncAxes(MotionEngine engine, out double interpolationSeconds, out string statusText)
    {
        interpolationSeconds = 1.0 / TargetFps;
        statusText = "";
        if (!_sync.Ok) return null;
        // 整幅画面在平移（摇镜/推拉）时，画面里的东西全都在动，跟着它抽就是把镜头运动当角色动作。
        // 这一拍先不出位置信号，让上层按原来的节奏路径处理，并如实说明原因。
        if (_sync.PanDominant)
        {
            statusText = $"镜头在平移（每帧 {_sync.PanPixels:0}px，位移 {_sync.PanFraction * 100:0}% 来自整幅移动）：先不把整幅移动当成动作";
            return null;
        }


        if (!_sync.TryGetNormalizedPosition(_cfg.ScreenSyncInvertDirection, out double norm)) return null;

        double relativeDepth = Math.Clamp(_sync.RelativeDepth, 0.25, 1.0);
        double range = Math.Clamp(FollowComputeRange(engine, 1.0) * relativeDepth, 5, 60);
        double[] values = _syncAxes;
        values[0] = Math.Clamp(50 + (norm - 0.5) * 2.0 * range, 0, 100);
        values[1] = 50;
        values[2] = 50;
        values[3] = 50;
        if (_cfg.AutoTwistEnabled && engine.MultiAxisMotion)
        {
            // 自动扭转：R0 跟 L0 联动（插入转一边、拔出转回）。画面同步这个来源没有显式旋转命令，
            // 所以开关开着就联动；限位/端点由 ComputeLinked 夹进用户已设的 R0 区间。
            values[3] = AutoTwistLink.ComputeLinked(
                values[0], _cfg.AutoTwistAmplitudePercent,
                _cfg.LovenseRotateTopPercent, _cfg.LovenseRotateDeepPercent);
        }
        values[4] = 50;
        values[5] = 50;
        if (_cfg.ScreenSyncTiltEnabled && engine.MultiAxisMotion)
        {
            // 近似倾斜：2D 画面只能近似，左右较准，前后靠画面上下分量推测。
            values[4] = Math.Clamp(_sync.R1, 0, 100);
            values[5] = Math.Clamp(_sync.R2, 0, 100);
        }
        ApplySyncRange(values);
        interpolationSeconds = Math.Clamp(_frameIntervalEmaMs / 1000.0, 0.02, 0.25);
        string tiltText = _cfg.ScreenSyncTiltEnabled && engine.MultiAxisMotion
            ? $" · 倾斜 R1 {values[4]:0} / R2 {values[5]:0}"
            : "";
        statusText = $"正在逐帧跟画面位置：深度 {relativeDepth * 100:0}% · 每秒 {_sync.Hz:0.0} 下{tiltText}";
        return values;
    }

    /// <summary>
    /// 深度换挡限速：姿态 ↔ 主体块切换时深度会跳（实测 87% → 49%），一拍到位就是「动作忽然变浅」；
    /// 逐拍限幅 0.10 把它抹成约半秒的过渡，观感是行程慢慢收/放。
    /// 只限换挡，不迟钝：同一个信号源内部的快变化本来就小于这个步长。
    /// </summary>
    private double SlewFollowDepth(double target)
    {
        if (double.IsNaN(_followDepthSmooth)) { _followDepthSmooth = target; return target; }
        _followDepthSmooth += Math.Clamp(target - _followDepthSmooth, -0.10, 0.10);
        return _followDepthSmooth;
    }

    /// <summary>上一拍到现在过了多久（秒）。第一拍按一帧算。</summary>
    private double FollowDeltaSeconds(double nowSeconds)
    {
        double dt = _lastFollowPhaseSeconds <= 0 ? 1.0 / TargetFps : nowSeconds - _lastFollowPhaseSeconds;
        _lastFollowPhaseSeconds = nowSeconds;
        if (!double.IsFinite(dt)) dt = 1.0 / TargetFps;
        return Math.Clamp(dt, 0.02, 0.5);
    }

    /// <summary>画面节奏的参考相位 0–1：0 = 刚刚动到最猛的那一拍。还没测到峰值时返回本类当前相位（等于不修）。</summary>
    private double ReferencePhase(double nowSeconds, double hz)
    {
        double peak = _rhythmPeakSeconds;
        if (peak <= double.NegativeInfinity) return _followPhase;
        double period = 1.0 / Math.Max(0.2, hz);
        double phase = (nowSeconds - peak) / period;
        return Frac(phase);
    }

    /// <summary>0–1 取小数部分（负数也能正确折回）。</summary>
    private static double Frac(double value) => value - Math.Floor(value);

    /// <summary>让位检查：设备被别的东西占着就返回原因，可以下发时返回 null。</summary>
    private string? FollowBlocker(MotionEngine engine, string source)
    {
        if (engine.EmergencyStopped) return "设备处于急停锁定（点左下角「全部归中」解锁），画面同步不会去动它";
        if (!engine.CanRun) return "设备没连上（或正在归中），画面同步先不动";
        // 脚本播放：本类**不下发也不抢**。脚本每帧都在写同一批轴，两边交替覆盖就是设备乱抖。
        if (App.FunscriptPlayer is { IsPlaying: true })
            return "脚本正在播放，画面同步让位（脚本一停就自己接回来）";
        string? owner = engine.DirectInputOwner;
        if (owner is { Length: > 0 } && !string.Equals(owner, FollowOwner, StringComparison.Ordinal))
            return owner == "bridge"
                ? "游戏桥正在控制设备，画面同步让位"
                : $"「{owner}」正在控制设备，画面同步让位";
        // 遥测优先于画面（「自动」这条来源的优先级：遥测 > 画面 > 声音）。
        // 「画面内容」是用户明确指定的一路，那时不看遥测，设备归画面。
        if (string.Equals(source, "auto", StringComparison.Ordinal) && engine.TelemetryRunning)
            return "正在跟游戏遥测（「自动」里遥测优先于画面内容）";
        return null;
    }

    /// <summary>
    /// 目标检查：绑定的程序必须在<b>前台</b>，而且正在看的窗口必须就是它的窗口。
    /// 为什么两条都要：只看前台的话，「绑着游戏、却在看浏览器窗口」会拿浏览器的画面去驱动游戏的动作；
    /// 只看窗口的话，游戏切到后台了设备还在动 —— 这违反这条信号一直以来的「离开前台就停」语义。
    /// </summary>
    private string ManualTargetProblem()
    {
        if (Volatile.Read(ref _captureFailed) != 0) return $"抓不到画面（{_failureReason}），画面同步不动";
        ScreenWatchWindow? target = CurrentTarget();
        if (target is null) return "找不到已手动绑定的窗口：重新选窗并重绑";
        string expectedTarget = (_cfg.ScreenWatchTarget ?? "").Trim();
        if (!string.Equals(expectedTarget, _manualTarget, StringComparison.Ordinal))
            return "画面目标已更改，请重新手动绑定";
        string process = target.ProcessName;
        if (_manualProcess.Length > 0 && !string.Equals(process, _manualProcess, StringComparison.OrdinalIgnoreCase))
            return "画面进程已更改，请重新手动绑定";
        if (target.Handle != _manualWindow || target.Width != _manualWidth || target.Height != _manualHeight)
            return "窗口或尺寸已更改，请重新手动绑定";

        bool ignoreForeground = _cfg.SimulationMode
            && string.Equals(Environment.GetEnvironmentVariable("HEXA_IGNORE_FOREGROUND"), "1", StringComparison.Ordinal);
        bool needForeground = _cfg.ScreenFollowOnlyWhenForeground || Volatile.Read(ref _lastCapturePath) == 2;
        if (needForeground && !ignoreForeground)
        {
            string foreground = "";
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid != 0) foreground = ProcessNameOf(pid);
                }
            }
            catch { /* 读不到就当不在前台，宁可不驱动 */ }
            if (!string.Equals(foreground, _manualProcess, StringComparison.OrdinalIgnoreCase))
                return "手动绑定的窗口不在前台，画面同步停着（切回它就自己接上）";
        }
        return "";
    }

    private string FollowTargetProblem()
    {
        string bound = GameTelemetryProtocol.NormalizeProcessName(_cfg.CompanionProcess);
        if (bound.Length == 0) return "还没绑定游戏：先在「游戏伴随」里填上要跟着的程序";
        if (Volatile.Read(ref _captureFailed) != 0) return $"抓不到画面（{_failureReason}），画面同步不动";

        // 测试绕过：SimulationMode + HEXA_IGNORE_FOREGROUND=1 时跳过「必须前台」这一条。
        // 后台自动化测试里没有前台窗口，但 GetForegroundWindow 拿到的一定不是 ffplay，
        // 不绕过的话 serial.commands 永远是 0。其余检查（绑定了没有 / 抓不抓得到画面 / 看的是不是它）都保留。
        bool ignoreForeground = _cfg.SimulationMode
            && string.Equals(Environment.GetEnvironmentVariable("HEXA_IGNORE_FOREGROUND"), "1", StringComparison.Ordinal);
        // 默认不要求前台：PrintWindow 抓的是窗口自己的内容，切到别的窗口（包括点开 Hexa 自己看信号）
        // 画面照样是对的。只有「屏幕抓取」这条兜底路必须要求前台 —— 被别的窗口挡住就抓到挡住它的东西。
        // 想要旧语义（离开前台就停）就在设置里勾「只在前台时动」。
        bool needForeground = _cfg.ScreenFollowOnlyWhenForeground || Volatile.Read(ref _lastCapturePath) == 2;
        if (needForeground && !ignoreForeground)
        {
            string foreground = "";
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid != 0) foreground = ProcessNameOf(pid);
                }
            }
            catch { /* 读不到就当不在前台，宁可不驱动 */ }
            if (!string.Equals(foreground, bound, StringComparison.OrdinalIgnoreCase))
                return $"绑定的 {bound} 不在前台，画面同步停着（切回它就自己接上）";
        }

        ScreenWatchWindow? target = CurrentTarget();
        if (target is null) return "找不到那个画面窗口：它可能已经关掉或改了名字，重新挑一个";
        if (target.ProcessName.Length > 0
            && !string.Equals(target.ProcessName, bound, StringComparison.OrdinalIgnoreCase))
            return $"现在看的是 {target.ProcessName} 的窗口，不是绑定的 {bound}：在「看哪个窗口」里选 {bound} 的窗口";
        return "";
    }

    /// <summary>
    /// 开始离场缓降：从当前幅度线性收回中位再交控制权。<b>不是急停</b>。
    /// 只有「本来就在驱动设备（或已经握着控制权）」时才需要缓降；没在驱动就只是记个原因。
    /// </summary>
    /// <summary>记住这一拍发出去的六轴 —— 遮挡续跑要用它。</summary>
    private void NoteSentAxes(double[] values, long now)
    {
        if (values.Length < 6) return;
        var copy = _lastSentAxes ?? new double[6];
        for (int i = 0; i < 6; i++) copy[i] = values[i];
        _lastSentAxes = copy;
        _lastSentMs = now;
        _holdUntilMs = 0;                       // 有新动作了，续跑窗口重来
    }

    /// <summary>
    /// 画面突然「看不见了」时的续跑：把上一次真正下发的位置原样再发几拍（最多 <see cref="FollowHoldMs"/>）。
    /// 只在「刚才还在发」的情况下生效（400ms 内有过真实下发），真的停下来时不会硬撑。
    /// </summary>
    private bool HoldLastMotion(MotionEngine engine, long now)
    {
        if (_lastSentAxes is null) return false;
        if (now - _lastSentMs > 400) { _holdUntilMs = 0; return false; }
        if (_holdUntilMs == 0) _holdUntilMs = now + FollowHoldMs;
        if (now >= _holdUntilMs) { _holdUntilMs = 0; return false; }
        if (!engine.TryClaimDirectInput(FollowOwner)) { _holdUntilMs = 0; return false; }
        Volatile.Write(ref _followWant, 1);
        if (!engine.TrySendDirectAxes(_lastSentAxes, Math.Clamp(_frameIntervalEmaMs / 1000.0, 0.02, 0.30)))
        {
            _holdUntilMs = 0;
            return false;
        }
        Volatile.Write(ref _followActive, 1);
        SetFollowReason("这块画面这会儿看不见（被窗口盖住或抓不到）：先按刚才的动作接着走 1 秒，再缓降");
        return true;
    }

    /// <summary>内容本来静止、但又没有「刚才那一拍」可以续：停在原地不动（位置照发，看门狗不误判掉线）。</summary>
    private bool HoldStill(MotionEngine engine, long now)
    {
        if (_lastSentAxes is null) return false;
        if (!engine.TryClaimDirectInput(FollowOwner)) return false;
        Volatile.Write(ref _followWant, 1);
        if (!engine.TrySendDirectAxes(_lastSentAxes, Math.Clamp(_frameIntervalEmaMs / 1000.0, 0.02, 0.30)))
            return false;
        Volatile.Write(ref _followActive, 1);
        _lastFollowSendMs = now;
        SetFollowReason(FollowContinuityPolicy.Describe(FollowLossKind.ContentStill));
        return true;
    }

    /// <summary>
    /// 把目标检查的中文原因归到「丢信号的哪一种」。字符串是同一处生成的，所以这里匹配的是
    /// ManualTargetProblem / FollowTargetProblem 的原话；认不出来就按真退出处理（缓降，最保守）。
    /// </summary>
    private static FollowLossKind ClassifyTargetProblem(string problem)
    {
        if (problem.Length == 0) return FollowLossKind.None;
        if (problem.Contains("不在前台", StringComparison.Ordinal)) return FollowLossKind.ForegroundLost;
        if (problem.Contains("抓不到画面", StringComparison.Ordinal)) return FollowLossKind.CaptureLost;
        if (problem.Contains("找不到", StringComparison.Ordinal)) return FollowLossKind.TargetExited;
        return FollowLossKind.TargetExited;
    }

    /// <summary>
    /// 切窗 / 抓不到时的降级运行：目标没变，只是这一路的信号暂时断了。
    /// 接着刚才的相位，用降级后的深度（×0.35）与速度（×0.55）继续慢速小幅地走，
    /// 混合系数随拍平滑逼近目标 —— 断和接都是渐变，不重起势、不回到中位。
    /// 从没驱动过时不凭空起势，老实等着。
    /// </summary>
    private bool TryDriveDegraded(MotionEngine engine, long now, double nowSeconds, FollowLossKind kind, string detail)
    {
        if (_lastSentAxes is null && Volatile.Read(ref _followActive) == 0)
        {
            _degradeWhy = detail;
            SetFollowReason(detail);
            return true;
        }
        if (!engine.CanAcceptDirectInput || !engine.TryClaimDirectInput(FollowOwner))
        {
            SetFollowReason(detail + " · 等设备让出来");
            return true;
        }

        double dt = FollowDeltaSeconds(nowSeconds);
        _degradeBlend = FollowContinuityPolicy.Smooth(_degradeBlend, 1.0, dt, 0.6);
        double baseDepth = Math.Clamp(Volatile.Read(ref _followDepth), 0.10, 1.0);
        double baseHz = Math.Clamp(Volatile.Read(ref _followHz), FollowHzMin, FollowHzMax);
        double depth = baseDepth * (1 - _degradeBlend * (1 - FollowContinuityPolicy.DegradedDepthScale));
        double range = FollowComputeRange(engine, Math.Max(0.10, depth));
        double hz = baseHz * (1 - _degradeBlend * (1 - FollowContinuityPolicy.DegradedSpeedScale));
        // 降级慢动时也一样吃挣扎加成：连点本来就是在"游戏在前台、我在动"的时候，这时降级不该装没听见。
        if (engine.StruggleSpeedScale > 1.0001 || engine.StruggleDepthBoost > 0.0001)
        {
            depth = Math.Clamp(depth * (1 + engine.StruggleDepthBoost), 0.05, 1.0);
            range = FollowComputeRange(engine, depth);
            hz = Math.Clamp(hz * Math.Clamp(engine.StruggleSpeedScale, 1.0, 1.4), FollowHzMin, FollowHzMax);
        }

        _followPhase = Frac(_followPhase + hz * dt);
        double[] values = MotionVocabulary.Render(MotionEventKind.Swell, 1.0, _followPhase, range, engine.MultiAxisMotion);
        ApplySyncRange(values);
        if (!engine.TrySendDirectAxes(values, Math.Clamp(dt * 1.6, 0.06, 0.30))) return false;

        Volatile.Write(ref _followActive, 1);
        Volatile.Write(ref _followWant, 1);
        Volatile.Write(ref _followDepth, depth);
        Volatile.Write(ref _followHz, hz);
        Volatile.Write(ref _followMainAxis, Math.Clamp(values[0], 0, 100));
        _lastFollowSendMs = now;
        _degradeWhy = detail;
        NoteSentAxes(values, now);
        SetFollowReason(FollowContinuityPolicy.Describe(kind) +
            $" · 深度 {depth * 100:0}% · 每秒 {hz:0.0} 下");
        return true;
    }

    private void BeginFollowEase(MotionEngine engine, long now, string reason)
    {
        _followDepthSmooth = double.NaN;      // 缓降后重新接管从当拍的深度起算，不从上一次留下的值爬
        SetFollowReason(reason);
        if (Volatile.Read(ref _followActive) == 0
            && !string.Equals(engine.DirectInputOwner, FollowOwner, StringComparison.Ordinal))
        {
            Volatile.Write(ref _followWant, 0);
            return;
        }
        lock (_followLock)
        {
            if (Volatile.Read(ref _followEasing) != 0) return;
            _followEaseRange = FollowComputeRange(engine, Volatile.Read(ref _followDepth));
            _followEaseHz = Math.Clamp(Volatile.Read(ref _followHz), FollowHzMin, FollowHzMax);
            _followEaseStartMs = now;
            Volatile.Write(ref _followActive, 0);
            Volatile.Write(ref _followEasing, 1);
        }
        AppLogger.Info($"画面同步：{reason} —— 缓降回中 {FollowEaseSeconds:0.0} 秒后交还控制权");
    }

    /// <summary>缓降的每一拍：把幅度线性收到 0（收完就是中位），相位继续走，所以动作不会突然断在半路。</summary>
    private void StepFollowEase(MotionEngine engine, long now, double nowSeconds)
    {
        double progress = (now - _followEaseStartMs) / (FollowEaseSeconds * 1000.0);
        if (!engine.CanRun || progress >= 1.0) { FinishFollowEase(engine); return; }

        double dt = FollowDeltaSeconds(nowSeconds);
        _followPhase = Frac(_followPhase + _followEaseHz * dt);
        double range = _followEaseRange * (1.0 - Math.Clamp(progress, 0, 1));
        double[] values = MotionVocabulary.Render(
            MotionEventKind.Voice, 1.0, _followPhase, range, engine.MultiAxisMotion);
        if (engine.TrySendDirectAxes(values, Math.Clamp(dt * 1.6, 0.06, 0.30)))
            _lastFollowSendMs = now;
    }

    private void FinishFollowEase(MotionEngine engine)
    {
        if (engine.CanAcceptDirectInput) engine.TrySendDirectAxes(CenterPose, FollowEaseSeconds);
        ReleaseFollowOwnership(engine);
        Volatile.Write(ref _followEasing, 0);
        Volatile.Write(ref _followWant, 0);
        Volatile.Write(ref _followDepth, 0);
        _followPhaseMisses = 0;
        AppLogger.Info("画面同步：已缓降回中并交还设备控制权");
    }

    /// <summary>
    /// 立刻收尾（不发逐帧缓降）：给一个 0.45–0.7 秒的插值让它自己滑回中位，然后交出控制权。
    /// 用于「停止同步」「看门狗发现信号断了」「跟随逻辑出错」这三条路 —— 它们都可能发生在
    /// 抓帧线程已经不能干活的时候，所以不能再指望逐帧缓降。
    /// </summary>
    private void StopFollowNow(string reason)
    {
        try
        {
            MotionEngine? engine = App.Engine;
            Volatile.Write(ref _followWant, 0);
            Volatile.Write(ref _followEasing, 0);
            if (engine is not null)
            {
                if (Volatile.Read(ref _followActive) != 0 || string.Equals(engine.DirectInputOwner, FollowOwner, StringComparison.Ordinal))
                {
                    if (engine.CanAcceptDirectInput) engine.TrySendDirectAxes(CenterPose, FollowEaseSeconds);
                    AppLogger.Info($"画面同步：{reason} —— 回中并交还控制权");
                }
                ReleaseFollowOwnership(engine);
            }
            Volatile.Write(ref _followActive, 0);
            Volatile.Write(ref _followDepth, 0);
            SetFollowReason(reason);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面同步：收尾时出错 —— {ex.Message}");
        }
    }

    /// <summary>交还控制权（只交还自己握着的那一份，别人的不动）。</summary>
    private static void ReleaseFollowOwnership(MotionEngine engine)
    {
        try { engine.ReleaseDirectInput(FollowOwner); }
        catch (Exception ex) { AppLogger.Warn($"画面同步：交还控制权出错 —— {ex.Message}"); }
    }

    /// <summary>中位姿态（各轴 50）。</summary>
    private static readonly double[] CenterPose = { 50, 50, 50, 50, 50, 50 };

    /// <summary>状态文字只在真的变了时才换一个字符串：这个方法每 77ms 会被调到一次。</summary>
    private void SetFollowReason(string reason)
    {
        if (string.Equals(_followReason, reason, StringComparison.Ordinal)) return;
        _followReason = reason;
    }

    /// <summary>
    /// 肤色判定 = 经典 RGB 规则 <b>且</b> YCbCr 区间规则（两条都认为像肤色才算）。
    /// 只用 RGB 规则时木地板、橙色家具、暖色灯光都会中招；测量阶段宁可保守一点，
    /// 反正灵敏度滑块可以让判定线整体上下移动。
    /// </summary>
    private static bool IsSkin(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        if (r <= 95 || g <= 40 || b <= 20) return false;
        if (r <= g || r <= b) return false;
        if (max - min <= 15 || Math.Abs(r - g) <= 15) return false;

        double cb = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b;
        double cr = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b;
        return cb is >= 77 and <= 127 && cr is >= 133 and <= 173;
    }

    /// <summary>
    /// 把这一拍的运动量压进环形缓冲，顺手找「动得最猛」的局部极大 —— 画面同步的参考相位就靠它。
    ///
    /// 为什么要自己找峰值而不是从自相关里推：自相关只给<b>周期</b>，不给「现在处在周期的哪一点」，
    /// 而锁相要的正是后者。峰值判定要求「前一帧比再前一帧高、这一帧不比前一帧高」，
    /// 并且高度要过 <see cref="MotionPeakFloor"/> —— 噪声级别的小起伏不该被当成一拍。
    /// </summary>
    private void PushMotionSample(double value, double nowSeconds)
    {
        double previous = _motionCount > 0 ? _motionRing[(_motionWrite - 1 + RhythmSamples) % RhythmSamples] : 0;
        double before = _motionCount > 1 ? _motionRing[(_motionWrite - 2 + RhythmSamples) % RhythmSamples] : 0;

        _motionRing[_motionWrite] = value;
        _motionWrite = (_motionWrite + 1) % RhythmSamples;
        if (_motionCount < RhythmSamples) _motionCount++;

        if (_motionCount >= 3 && previous > before && previous >= value && previous >= MotionPeakFloor)
            _rhythmPeakSeconds = nowSeconds - 1.0 / TargetFps;   // 峰值出现在上一帧
    }

    /// <summary>
    /// 周期性运动强度：最近约 3.2 秒的 Motion 序列上做自相关，看有没有 0.5–3Hz 的规律起伏。
    ///
    /// 三步（每一步都为同一个问题服务 ——「别把慢变化当成有节奏」）：
    /// ① 先去<b>线性趋势</b>（最小二乘）：镜头缓慢平移、画面慢慢变亮，这种单调变化去掉趋势后几乎什么都不剩。
    ///    用最小二乘而不是滑动平均高通：滑动平均要么在窗口边界留下瞬态（那点瞬态本身自相关很高，
    ///    会把缓慢漂移算成 0.9 的高节奏），要么把 0.5Hz（我们的下限）一起削弱。
    /// ② 归一化自相关，只算 0.3–2 秒（0.5–3Hz）对应的延迟。
    /// ③ 只认<b>带内的局部极大</b>：真周期会在自己的周期处形成一个峰；而缓慢漂移去掉趋势后剩下的是一段
    ///    「鼓包」，它的自相关是单调下降的、带内没有局部极大 → 不给分。这一条是把「漂移」和「节奏」分开的关键。
    ///
    /// 频率标定按**实测帧率**算（丢帧会让标定偏一点），所以这是「有没有节奏」的强指标，不是精确的频率计；
    /// 另外 13fps 采样下高于约 6.5Hz 的运动本来就会混叠，别拿它当频谱仪用。
    ///
    /// 顺带把「峰值在哪个延迟上」（→ 频率 Hz）记进 <see cref="_rhythmHz"/>：画面同步的速度就用它。
    /// 节奏分太低时不给频率（跟随那边会用兜底频率），免得拿一个噪声里的峰去定速度。
    /// </summary>
    private double ComputeRhythm()
    {
        int n = _motionCount;
        if (n < 16) return 0;                     // 攒不到一秒半的样本，先不给分

        // 环形缓冲 → 线性（最新在末尾）
        for (int i = 0; i < n; i++)
            _rhythmScratch[i] = _motionRing[((_motionWrite - n + i) % RhythmSamples + RhythmSamples) % RhythmSamples];

        // ① 去线性趋势（最小二乘拟合一条直线再减掉）
        double sumValue = 0, sumIndex = 0, sumIndexSquare = 0, sumIndexValue = 0;
        for (int i = 0; i < n; i++)
        {
            double sample = _rhythmScratch[i];   // 变量名不能叫 value：外层作用域已有一个 value（CS0136）
            sumValue += sample;
            sumIndex += i;
            sumIndexSquare += (double)i * i;
            sumIndexValue += (double)i * sample;
        }
        double denominator = n * sumIndexSquare - sumIndex * sumIndex;
        double slope = denominator > 1e-9 ? (n * sumIndexValue - sumIndex * sumValue) / denominator : 0;
        double intercept = (sumValue - slope * sumIndex) / n;

        double energy = 0;
        for (int i = 0; i < n; i++)
        {
            double residual = _rhythmScratch[i] - (intercept + slope * i);
            _rhythmHp[i] = residual;
            energy += residual * residual;
        }
        if (energy <= 1e-9)
        {
            // 画面完全不动，或者只是在匀速漂移：没有节奏可言，频率也一起清掉
            //（跟随会退回兜底频率，而不是拿一段早就过去的节奏）。
            Volatile.Write(ref _rhythmHz, 0);
            return 0;
        }
        double std = Math.Sqrt(energy / n);

        // ② 归一化自相关（分母用整段能量：长延迟重叠样本少，自然被压一点，偏保守）
        double fps = 1000.0 / Math.Clamp(_frameIntervalEmaMs, 33, 200);
        int lagMax = Math.Min(n - 3, (int)Math.Round(fps / RhythmMinHz));
        for (int lag = 1; lag <= lagMax; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < n; i++) sum += _rhythmHp[i] * _rhythmHp[i + lag];
            _rhythmCorr[lag] = sum / energy;
        }

        // ③ 带内局部极大才算「有周期」（顺带记下峰值在哪个延迟 → 频率）
        int lagMin = Math.Max(1, (int)Math.Round(fps / RhythmMaxHz));
        double peak = 0;
        int peakLag = 0;
        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            if (_rhythmCorr[lag] < _rhythmCorr[lag - 1]) continue;
            if (lag < lagMax && _rhythmCorr[lag] < _rhythmCorr[lag + 1]) continue;
            if (_rhythmCorr[lag] > peak) { peak = _rhythmCorr[lag]; peakLag = lag; }
        }

        // ④ 动得不够厉害就不给节奏分：std 衡量的是「动得有多厉害」，不是「动得有多规律」。
        double gate = Math.Clamp(std / MotionActiveScale, 0, 1);
        double value = Math.Clamp(peak, 0, 1) * gate;

        // ⑤ 频率（Hz）= 实测帧率 ÷ 峰值延迟。只有节奏分站得住脚时才报出去：
        //    噪声里的一个假峰不该决定设备的速度（跟随那边会退回兜底频率）。
        double hz = peakLag > 0 ? fps / peakLag : 0;
        Volatile.Write(ref _rhythmHz,
            value >= RhythmHzGate && hz >= RhythmMinHz && hz <= RhythmMaxHz ? hz : 0);
        return value;
    }

    /// <summary>最近 1 秒内有没有人声/呻吟事件（读音频那条链，读不到就当没有）。</summary>
    private static bool VoiceRecently()
    {
        try
        {
            AudioReactiveService? audio = App.AudioReactive;
            return audio is not null
                && audio.LastEventKind == MotionEventKind.Voice
                && audio.LastEventAgeSeconds < VoiceEventFreshSeconds;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>灵敏度 → 阈值系数：越灵敏阈值越低（更容易判成「疑似」）。</summary>
    private double SensitivityFactor() => Math.Clamp(
        1.0 / Math.Clamp(_cfg.ScreenWatchSensitivity, SensitivityMin, SensitivityMax),
        SensitivityFactorMin, SensitivityFactorMax);

    /// <summary>快起慢落的指数平滑（上升用 attack，下降用 release）。</summary>
    private static double Smooth(double current, double target, double attack, double release) =>
        current + (target - current) * (target > current ? attack : release);

    private void ResetFrameState()
    {
        _hasPrevFrame = false;
        _motionCount = 0;
        _motionWrite = 0;
        // 「动得最猛的那一拍」也要一起清：它是抓帧时钟上的一个时刻，帧序列断了就不能再当参考。
        _rhythmPeakSeconds = double.NegativeInfinity;
        Volatile.Write(ref _motion, 0);
        Volatile.Write(ref _rhythm, 0);
        Volatile.Write(ref _rhythmHz, 0);
    }

    private void ResetAnalysis()
    {
        lock (_manualLock)
        {
            _manualTracker.Invalidate("同步已重启，请重绑或回到自动");
            _manualPreview = null;
            _manualAtMs = 0;
        }
        ResetFrameState();
        ClearPoseState();
        Volatile.Write(ref _skinRatio, 0);
        Volatile.Write(ref _skinCenterRatio, 0);
        Volatile.Write(ref _sceneScore, 0);
        Volatile.Write(ref _featureScore, 0);
        Volatile.Write(ref _voiceEvent, 0);
        Volatile.Write(ref _suspect, 0);
        Volatile.Write(ref _fps, 0);
        // 模型分跟着观察一起归零：下一次观察的第一帧推理出来之前，判定按手工特征走
        //（模型文件还在内存里，只是"这次观察还没出过分"，界面上模型分会先显示「—」再跳上来）。
        Volatile.Write(ref _modelScore, 0);
        Volatile.Write(ref _modelReady, 0);
        Interlocked.Exchange(ref _lastInferenceMs, 0);
        _lastModelJobMs = 0;
        lock (_modelSync) _modelBusy = false;
        // 跟随的运行状态一起归零（设备早就由 StopFollowNow 收过尾了，这里只清状态）。
        Volatile.Write(ref _followActive, 0);
        Volatile.Write(ref _followWant, 0);
        Volatile.Write(ref _followEasing, 0);
        _liveStreak = 0;
        _deadStreak = 0;
        Volatile.Write(ref _followDepth, 0);
        _followPhase = 0;
        _followPhaseMisses = 0;
        _mirrorPosition = double.NaN;
        _mirrorSent = double.NaN;
        _lastFollowPhaseSeconds = 0;
        _lastFollowSendMs = 0;
        _degradeBlend = 0;
        _degradeWhy = "";
        _followReason = "";
        _lastFrameMs = 0;
        _frameIntervalEmaMs = 1000.0 / TargetFps;
        _failureReason = "";
        Volatile.Write(ref _captureFailed, 0);
    }

    private void SetFailure(string reason)
    {
        _failureReason = reason;
        lock (_manualLock) _manualTracker.Invalidate("抓图中断，请重绑");
        Volatile.Write(ref _captureFailed, 1);
    }

    private void ClearFailure()
    {
        if (Volatile.Read(ref _captureFailed) == 0 && _failureReason.Length == 0) return;
        _failureReason = "";
        Volatile.Write(ref _captureFailed, 0);
    }

    // ── Win32 ────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>最前面的那个窗口（画面同步用它判断「绑定的程序在不在前台」）。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>把窗口画到目标 DC 上。nFlags = 2（PW_RENDERFULLCONTENT）能拿到合成后的完整内容。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
