using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
/// 「画面观察 / 画面跟随」：抓一个窗口的画面，算出几个能解释的特征（肤色占比 / 运动量 / 节奏性 /
/// 呻吟事件 / 融合分），再加上本机训练好的识别模型给出的「内容判定分」；
/// 特征与判定分既供界面实时显示，也可以在用户明确选择「画面内容」这条动作来源后<b>驱动设备</b>
/// —— 跟随用的是「画面里的节奏 + 场景强度」，不是逐帧复刻画面。
///
/// 四条硬规矩（改这个类的人请一并守住）：
/// ① <b>默认绝不动设备</b>：只有同时满足
///    · 用户打开了「画面信号」（<see cref="AppSettings.ScreenWatchEnabled"/>，默认关），且
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

    /// <summary>目标帧率 13 fps（要求 12–15 之间）。再慢看不出节奏，再快是白烧 CPU。</summary>
    private const int TargetFps = 13;
    private const int FrameIntervalMs = 1000 / TargetFps;   // ≈76ms

    /// <summary>抓一次超过它就算「这帧太慢」：跳过后面两帧只睡觉（自适应降频）。</summary>
    private const int SlowFrameMs = 60;
    private const int SlowFrameSkip = 2;

    /// <summary>整帧平均亮度低于这个值（全黑）或高于上限（全白）= 没抓到画面。</summary>
    private const double BlankLumaLow = 2;
    private const double BlankLumaHigh = 252;

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

    // ── 画面跟随（驱动设备）的参数 ────────────────────────────────────
    /// <summary>本类在设备上使用的控制权名字（见 MotionEngine.TryClaimDirectInput）。</summary>
    private const string FollowOwner = "screen";

    /// <summary>跟随的半幅基准（轴行程百分比）：深度再乘场景强度、再乘舒适档上限。30 = 深度满时主轴在中位 ±30%。</summary>
    private const double FollowAmplitudeBase = 30;

    /// <summary>没测出节奏频率、但场景确实过线时用的兜底频率（Hz）：总比让设备定住不动强。</summary>
    private const double FollowFallbackHz = 1.2;

    /// <summary>跟随频率的上下限（Hz）：0.5 = 两秒一次，2.5 = 每秒两下半。再快就该由舒适档说话了。</summary>
    private const double FollowHzMin = 0.5;
    private const double FollowHzMax = 2.5;

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

    // 抓取缓冲：只有一个源图 + 一张 160×90 小图 + 两个复用数组，来回用同一块内存。
    private System.Drawing.Bitmap? _sourceBitmap;
    private System.Drawing.Bitmap? _smallBitmap;
    private int _sourceWidth;
    private int _sourceHeight;
    private readonly byte[] _bgra = new byte[FrameWidth * FrameHeight * 4];
    private readonly byte[] _prevLuma = new byte[FrameWidth * FrameHeight];
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
    private int _captureFailed;
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

    // ── 跟随（驱动设备）─────────────────────────────────────────────
    // 这些字段会被三条线程碰：抓帧线程（主循环）、看门狗定时器、界面线程（Stop/Dispose）。
    // 全部是 int/double/long 的原子读写 + 一把小锁保护「状态切换」，不做长操作。
    private readonly Lock _followLock = new();
    private int _followActive;                        // 正在驱动设备（已拿到控制权且这一拍发了指令）
    private int _followWant;                          // 信号过线了、想驱动设备（规则引擎据它让位）
    private int _followEasing;                        // 正在缓降回中
    private long _followEaseStartMs;
    private double _followEaseRange;                  // 缓降的起点幅度
    private double _followEaseHz;                      // 缓降期间用的频率
    private double _followPhase;                      // 跟随相位 0–1（= 动作语汇「抽插」的内部相位）
    private double _followHz = FollowFallbackHz;      // 当前用的频率
    private double _followDepth;                      // 当前深度 0–1（= 场景强度过线之后的归一化值）
    private double _lastFollowPhaseSeconds;           // 上一拍的时刻（抓帧时钟，秒）
    private long _lastFollowSendMs;                   // 最近一次成功下发的时刻（看门狗用，TickCount64）
    private int _followPhaseMisses;                   // 连续几拍相位误差过大（到 FollowPhaseResyncBeats 才重锁）
    private string _followReason = "";                // 白话状态（界面直接显示）
    private System.Threading.Timer? _followWatchdog;  // 独立于抓帧线程的看门狗

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
        // 登记成「当前实例」：伴随的规则引擎（RuleEngine）要靠它判断画面信号要不要接管设备。
        lock (_instances) _instances.Add(this);
    }

    /// <summary>
    /// 当前那个<b>正在观察</b>的实例（没有正在跑的就返回最后建出来的那个，可能是停止状态；一个都没有则为 null）。
    ///
    /// 为什么是静态的：本服务由「测试页」懒建、跨页面共享，App.xaml.cs 里没有它的字段，
    /// 而规则引擎必须能拿到<b>同一条</b>信号（不然「自动：遥测 &gt; 画面 &gt; 声音」这条优先级就无从判断）。
    /// 改 App.xaml.cs 属于动公共资产，这里用一个静态登记口替代。
    ///
    /// 为什么要留一个列表而不是「最后一次 new 的那个」：自检会临时 new 出好几个服务并 Dispose，
    /// 只记一个的话，自检跑完就会把真正在观察的那个挤掉，伴随那边再也收不到画面信号。
    /// </summary>
    public static ScreenWatchService? Current
    {
        get
        {
            lock (_instances)
            {
                if (_instances.Count == 0) return null;
                // 挑法（顺序即优先级）：
                // ① 正在跑的、不是自检注入句柄的那个 —— 这是唯一「真正在观察用户那个窗口」的实例；
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
                return IsRunning ? "模型已加载，正在算第一帧…" : "模型已加载（开启观察后开始判定）";
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

    // ── 画面跟随（驱动设备）对外状态 ─────────────────────────────────

    /// <summary>这一刻设备是不是正由画面信号驱动。</summary>
    public bool FollowActive => Volatile.Read(ref _followActive) != 0;

    /// <summary>
    /// 画面信号此刻「想要」驱动设备 —— <b>只看信号本身</b>：来源允许 + 目标在前台 + 窗口可用 +
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

    /// <summary>跟随状态的白话说明（为什么没在动 / 正在跟什么）。没在跟随时为空串。</summary>
    public string FollowReason => Volatile.Read(ref _followReason);

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
                Name = "Hexa 画面观察",
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
        AppLogger.Info($"画面观察已启动：{(shown.Length == 0 ? "(还没对上窗口)" : shown)}");
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
                AppLogger.Warn($"画面观察：识别模型线程起不来（判定退回手工特征）—— {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 停止观察并等线程真的退出（最多等 0.7 秒；极端情况下 PrintWindow 卡住时线程是后台线程，不会吊住界面）。
    /// 停下之前先把设备收尾：画面跟随会缓降回中并交出控制权，绝不留着设备挂在某个位置上。
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
        // 那几秒里设备不能还停在画面跟随留下的位置上。
        StopFollowNow("画面观察已停止");

        if (thread is not null && thread.IsAlive)
        {
            try { thread.Join(700); }
            catch (Exception ex) { AppLogger.Warn($"画面观察：等线程收尾时出错 —— {ex.Message}"); }
        }
        if (modelThread is not null && modelThread.IsAlive)
        {
            try { modelThread.Join(700); }
            catch (Exception ex) { AppLogger.Warn($"画面观察：等模型线程收尾时出错 —— {ex.Message}"); }
        }
        if (poseThread is not null && poseThread.IsAlive)
        {
            try { poseThread.Join(700); }
            catch (Exception ex) { AppLogger.Warn($"画面观察：等姿态线程收尾时出错 —— {ex.Message}"); }
        }
        StopFollowWatchdog();
        ClearPoseState();
        Volatile.Write(ref _fps, 0);
        ResetAnalysis();
        AppLogger.Info("画面观察已停止");
    }

    /// <summary>
    /// 重新枚举窗口并重新对上目标（换窗口、开/关窗口之后调用）。
    /// 正在观察时也会立即换到新目标，不用先关再开。
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        List<ScreenWatchWindow> list;
        try { list = EnumerateWindows(); }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面观察：枚举窗口失败 —— {ex.Message}");
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
            AppLogger.Warn($"画面观察：EnumWindows 失败 —— {ex.Message}");
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
                    if (skip > 0) skip--;              // 上一帧太慢：这几帧只睡觉，把 CPU 让出去
                    else CaptureAndAnalyze(clock);
                }
                catch (Exception ex)
                {
                    // 线程绝不能因为一帧出错就死掉（界面还写着「正在看」，那才是真的骗人）。
                    SetFailure($"抓取这一帧出错（{ex.GetType().Name}），正在重试");
                    AppLogger.Warn($"画面观察：抓取异常已跳过 —— {ex.Message}");
                }

                // 每拍都跑一次画面跟随 —— 成功、抓不到画面、这一帧出错，三种情况都要覆盖：
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
            AppLogger.Error("画面观察线程异常退出", ex);
        }
        finally
        {
            Volatile.Write(ref _fps, 0);
            _hasPrevFrame = false;
        }
    }

    private void CaptureAndAnalyze(Stopwatch clock)
    {
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
            if (!IsBlankFrame()) { AnalyzeFrame(clock); MarkCaptured(clock); return; }
            // 抓到全黑/全白 = 这条路对这个窗口不管用，往下走屏幕抓取。
        }

        // ② 退化：按窗口矩形从屏幕 DC 抓（等价 BitBlt + SRCCOPY）。
        //    代价：窗口必须露在桌面上，被别的窗口挡住就会抓到挡住它的东西。
        if (ScreenCopyToSource(rect, sourceWidth, sourceHeight))
        {
            DownscaleToSmall(sourceWidth, sourceHeight);
            if (!IsBlankFrame()) { AnalyzeFrame(clock); MarkCaptured(clock); return; }
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
            AppLogger.Warn($"画面观察：PrintWindow 失败，改用屏幕抓取 —— {ex.Message}");
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
            AppLogger.Warn($"画面观察：屏幕抓取失败 —— {ex.Message}");
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
        var small = _smallBitmap!;
        using (var graphics = System.Drawing.Graphics.FromImage(small))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImage(_sourceBitmap!,
                new System.Drawing.Rectangle(0, 0, FrameWidth, FrameHeight),
                0, 0, width, height, System.Drawing.GraphicsUnit.Pixel);
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
    }

    /// <summary>整帧几乎全黑 / 全白 = 这一帧没抓到（PrintWindow 对独占全屏、受保护内容就是这样）。</summary>
    private bool IsBlankFrame()
    {
        long sum = 0;
        for (int i = 0; i < _bgra.Length; i += 4)
            sum += (_bgra[i] * 28 + _bgra[i + 1] * 151 + _bgra[i + 2] * 77) >> 8;
        double mean = sum / (double)(FrameWidth * FrameHeight);
        return mean < BlankLumaLow || mean > BlankLumaHigh;
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
    /// 画面跟随不在这里：它由 <see cref="WorkerLoop"/> 每拍统一调一次 —— 抓不到画面、这一帧出错
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

        for (int y = 0; y < FrameHeight; y++)
        {
            bool rowInCenter = y >= centerY0 && y < centerY1;
            bool rowInMotion = y >= motionY0 && y < motionY1;
            int rowBase = y * FrameWidth;
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
                if (hadPrev && rowInMotion && x >= motionX0 && x < motionX1)
                {
                    motionPixels++;
                    diffSum += Math.Abs(luma - _prevLuma[index]);
                }
                _prevLuma[index] = (byte)luma;
            }
        }
        _hasPrevFrame = true;

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

        // ③ 节奏性（0.5–3Hz 的周期性起伏）+ 顺带记下频率与「动得最猛」的时刻（画面跟随要用）
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

        // ⑦ 把这一帧排给识别模型（每 300ms 一次；推理在模型线程上做，这里只做一次缩放到短边 236）
        MaybeQueueModelFrame(clock);
        // ⑧ 同样的办法把这一帧排给姿态模型（每 250ms 一次，推理在姿态线程上做）
        MaybeQueuePoseFrame(clock);
        // ⑨ 画面跟随不在这里跑：抓帧线程每拍（成功、抓不到、出错）都会统一跑一次（见 WorkerLoop），
        //    否则「抓不到画面」的那些帧就没人负责把设备缓降回中了。
        Interlocked.Increment(ref _frameCount);
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
                AppLogger.Warn($"画面观察：准备识别模型的缓冲失败 —— {ex.Message}");
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
            AppLogger.Warn($"画面观察：给识别模型准备这一帧时出错 —— {ex.Message}");
            return;
        }

        _lastModelJobMs = now;
        try { _modelWake?.Set(); }
        catch (Exception ex) { AppLogger.Warn($"画面观察：叫醒识别模型失败 —— {ex.Message}"); }
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
                    AppLogger.Warn($"画面观察：识别模型推理异常已跳过 —— {ex.Message}");
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
            AppLogger.Error("画面观察：识别模型线程异常退出", ex);
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
                AppLogger.Warn($"画面观察：姿态线程起不来（运动量退回整幅画面）—— {ex.Message}");
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
                    AppLogger.Warn($"画面观察：姿态推理异常已跳过 —— {ex.Message}");
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
            AppLogger.Error("画面观察：姿态线程异常退出", ex);
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
    private void MaybeQueuePoseFrame(Stopwatch clock)
    {
        if (!_cfg.ScreenUsePose) return;
        if (!PoseTracker.IsInstalled && !PoseTracker.IsReady) return;
        // 姿态线程没起来（线程创建失败这种极端情况）：别排帧 —— 排了也没人算，
        // 而「算完才放掉」的标记会永远挂着，后面再也排不进去。
        if (_poseWake is null) return;

        long now = clock.ElapsedMilliseconds;
        if (now - _lastPoseJobMs < PoseIntervalDefaultMs) return;

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
                AppLogger.Warn($"画面观察：准备姿态模型的画布失败 —— {ex.Message}");
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
            AppLogger.Warn($"画面观察：给姿态模型准备这一帧时出错 —— {ex.Message}");
            return;
        }
        lock (_poseSync) _poseMaps[slot] = map;

        _lastPoseJobMs = now;
        try { _poseWake?.Set(); }
        catch (Exception ex) { AppLogger.Warn($"画面观察：叫醒姿态线程失败 —— {ex.Message}"); }
    }

    /// <summary>取这一帧要用的 ROI（160×90 小图上的像素矩形）。返回 false = 退回中央 60% 那块。</summary>
    private bool TryPoseRoi(out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = x1 = y1 = 0;
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

    // ══ 画面跟随：把这条信号接成「能驱动设备」 ═════════════════════════
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
        catch (Exception ex) { AppLogger.Warn($"画面观察：停看门狗出错 —— {ex.Message}"); }
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
            AppLogger.Warn("画面跟随：看门狗发现信号停了（这段时间一条指令都没发出去），缓降回中并交还控制权");
            StopFollowNow("画面信号断了（看门狗）：缓降回中");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面跟随：看门狗出错 —— {ex.Message}");
        }
    }

    /// <summary>每个抓帧周期跑一次：决定这一拍要不要驱动设备、驱动到哪。任何异常都消化在这里。</summary>
    private void FollowTick(Stopwatch clock)
    {
        try { FollowTickCore(clock.Elapsed.TotalSeconds); }
        catch (Exception ex)
        {
            // 跟随逻辑绝不能把抓帧线程搞死；出错就按「信号断了」处理：缓降 + 交还控制权。
            AppLogger.Warn($"画面跟随：这一拍出错，按信号断了处理 —— {ex.Message}");
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

        // ① 正在缓降：先把幅度线性收到中位，收完再交控制权（绝不急停）。
        if (Volatile.Read(ref _followEasing) != 0) { StepFollowEase(engine, now, nowSeconds); return; }

        // ② 用户没让画面驱动设备 —— 默认就是这一条（ScreenWatchEnabled 默认关，伴随默认也关）。
        //    三道门都要过：伴随开着（这条来源属于伴随）、画面信号开着、来源是「画面内容」或「自动」。
        string source = (_cfg.CompanionSource ?? "").Trim().ToLowerInvariant();
        bool sourceAllows = source is "screen" or "auto";
        if (!_cfg.ScreenWatchEnabled || !_cfg.RuleEngineEnabled || !sourceAllows)
        {
            string why = !_cfg.ScreenWatchEnabled
                ? "要跟着画面动：先在上面点「开启观察」"
                : !_cfg.RuleEngineEnabled
                    ? "要跟着画面动：先在「游戏伴随」里点「开启游戏伴随」"
                    : "要让画面驱动设备：在「游戏伴随」里把「动作来源」选成「画面内容 · 跟着画面里的动作走」";
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
            if (Volatile.Read(ref _followActive) != 0) BeginFollowEase(engine, now, blocker);
            else SetFollowReason(blocker);
            return;
        }

        // ④ 目标：绑定的那个程序在前台，而且看的正是它的窗口。
        string targetProblem = FollowTargetProblem();
        if (targetProblem.Length > 0)
        {
            Volatile.Write(ref _followWant, 0);
            if (Volatile.Read(ref _followActive) != 0) BeginFollowEase(engine, now, targetProblem);
            else SetFollowReason(targetProblem);
            return;
        }

        // ⑤ 场景强度过线（Suspect 自带迟滞：进去难、掉下来也要掉得够低）。
        if (Volatile.Read(ref _suspect) == 0 || Volatile.Read(ref _captureFailed) != 0)
        {
            Volatile.Write(ref _followWant, 0);
            if (Volatile.Read(ref _followActive) != 0) BeginFollowEase(engine, now, "画面里没事发生了：缓降回中");
            else SetFollowReason("画面里没事发生时不动（场景强度过线才动）");
            return;
        }

        // ⑥ 走到这里 = 画面信号要驱动设备了。
        //    必须在真的拿到控制权**之前**就把「想要」立起来：伴随的自动动作还在跑时本类拿不到
        //    直接下发权，而规则引擎正是靠这个标记决定让位 —— 立晚了两边会互相等。
        Volatile.Write(ref _followWant, 1);

        if (!engine.CanAcceptDirectInput)
        {
            SetFollowReason("已经过线了，但设备正在过渡/被别人占着，等它让出来");
            return;
        }
        if (!engine.TryClaimDirectInput(FollowOwner))
        {
            SetFollowReason($"「{engine.DriverLabel}」正在控制设备，画面跟随让位");
            return;
        }

        // ⑦ 深度 / 速度 / 锁相 / 下发。
        double depth = FollowComputeDepth();
        double comfort = Math.Clamp(engine.ActiveComfortProfile.MaxIntensity, 0.3, 1.0);
        double hz = FollowComputeHz(engine, depth);
        double dt = FollowDeltaSeconds(nowSeconds);

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

        double range = Math.Clamp(FollowAmplitudeBase * comfort * depth, 5, 60);
        double[] values = MotionVocabulary.Render(
            MotionEventKind.Voice, 1.0, _followPhase, range, engine.MultiAxisMotion);
        // 插值时间比下发间隔略长：13fps 配 77ms 插值会一顿一顿，0.12 秒左右才连得起来。
        double interpolation = Math.Clamp(dt * 1.6, 0.06, 0.30);

        if (engine.TrySendDirectAxes(values, interpolation))
        {
            Volatile.Write(ref _followActive, 1);
            Volatile.Write(ref _followDepth, depth);
            Volatile.Write(ref _followHz, hz);
            _lastFollowSendMs = now;
            double lockPercent = Math.Clamp(_cfg.ScreenPhaseLock, 0, 1);
            SetFollowReason($"正在跟画面动：深度 {depth * 100:0}% · 每秒 {hz:0.0} 下"
                + (lockPercent > 0 ? $" · 相位锁定 {lockPercent * 100:0}%" : " · 不锁相位（只跟速度）"));
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
    /// 深度 0–1 ∝ 场景强度：从<b>退出判定线</b>到 1.0 之间线性映射。
    /// 为什么不直接用 SceneScore 当深度：判定线（灵敏度 1.0 时约 0.38）刚过线时深度就已经 0.38，
    /// 一过线设备就猛地动起来。从退出线开始归一化，过线那一刻深度接近 0，之后随强度长上去。
    /// </summary>
    private double FollowComputeDepth()
    {
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
    private double FollowComputeHz(MotionEngine engine, double depth)
    {
        double hz = Volatile.Read(ref _rhythmHz);
        if (!double.IsFinite(hz) || hz < RhythmMinHz) hz = FollowFallbackHz;   // 节奏没测出来：用兜底频率，别定住不动
        hz = Math.Clamp(hz, FollowHzMin, FollowHzMax);

        double comfort = Math.Clamp(engine.ActiveComfortProfile.MaxIntensity, 0.3, 1.0);
        double travel = Math.Max(5, FollowAmplitudeBase * comfort * Math.Max(0.15, depth));
        double maxHz = engine.ActiveComfortProfile.MaxAxisSpeedPerSecond / (Math.Tau * travel);
        return Math.Clamp(Math.Min(hz, maxHz), FollowHzMin, FollowHzMax);
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
        if (engine.EmergencyStopped) return "设备处于急停锁定（点左下角「全部归中」解锁），画面跟随不会去动它";
        if (!engine.CanRun) return "设备没连上（或正在归中），画面跟随先不动";
        // 脚本播放：本类**不下发也不抢**。脚本每帧都在写同一批轴，两边交替覆盖就是设备乱抖。
        if (App.FunscriptPlayer is { IsPlaying: true })
            return "脚本正在播放，画面跟随让位（脚本一停就自己接回来）";
        string? owner = engine.DirectInputOwner;
        if (owner is { Length: > 0 } && !string.Equals(owner, FollowOwner, StringComparison.Ordinal))
            return owner == "bridge"
                ? "游戏桥正在控制设备，画面跟随让位"
                : $"「{owner}」正在控制设备，画面跟随让位";
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
    private string FollowTargetProblem()
    {
        string bound = GameTelemetryProtocol.NormalizeProcessName(_cfg.CompanionProcess);
        if (bound.Length == 0) return "还没绑定游戏：先在「游戏伴随」里填上要跟着的程序";
        if (Volatile.Read(ref _captureFailed) != 0) return $"抓不到画面（{_failureReason}），画面跟随不动";

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
            return $"绑定的 {bound} 不在前台，画面跟随停着（切回它就自己接上）";

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
    private void BeginFollowEase(MotionEngine engine, long now, string reason)
    {
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
            _followEaseRange = Math.Clamp(FollowAmplitudeBase
                * Math.Clamp(engine.ActiveComfortProfile.MaxIntensity, 0.3, 1.0)
                * Math.Max(0.15, Volatile.Read(ref _followDepth)), 5, 60);
            _followEaseHz = Math.Clamp(Volatile.Read(ref _followHz), FollowHzMin, FollowHzMax);
            _followEaseStartMs = now;
            Volatile.Write(ref _followActive, 0);
            Volatile.Write(ref _followEasing, 1);
        }
        AppLogger.Info($"画面跟随：{reason} —— 缓降回中 {FollowEaseSeconds:0.0} 秒后交还控制权");
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
        AppLogger.Info("画面跟随：已缓降回中并交还设备控制权");
    }

    /// <summary>
    /// 立刻收尾（不发逐帧缓降）：给一个 0.45–0.7 秒的插值让它自己滑回中位，然后交出控制权。
    /// 用于「停止观察」「看门狗发现信号断了」「跟随逻辑出错」这三条路 —— 它们都可能发生在
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
                    AppLogger.Info($"画面跟随：{reason} —— 回中并交还控制权");
                }
                ReleaseFollowOwnership(engine);
            }
            Volatile.Write(ref _followActive, 0);
            Volatile.Write(ref _followDepth, 0);
            SetFollowReason(reason);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"画面跟随：收尾时出错 —— {ex.Message}");
        }
    }

    /// <summary>交还控制权（只交还自己握着的那一份，别人的不动）。</summary>
    private static void ReleaseFollowOwnership(MotionEngine engine)
    {
        try { engine.ReleaseDirectInput(FollowOwner); }
        catch (Exception ex) { AppLogger.Warn($"画面跟随：交还控制权出错 —— {ex.Message}"); }
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
    /// 把这一拍的运动量压进环形缓冲，顺手找「动得最猛」的局部极大 —— 画面跟随的参考相位就靠它。
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
    /// 顺带把「峰值在哪个延迟上」（→ 频率 Hz）记进 <see cref="_rhythmHz"/>：画面跟随的速度就用它。
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
        Volatile.Write(ref _followDepth, 0);
        _followPhase = 0;
        _followPhaseMisses = 0;
        _lastFollowPhaseSeconds = 0;
        _lastFollowSendMs = 0;
        _followReason = "";
        _lastFrameMs = 0;
        _frameIntervalEmaMs = 1000.0 / TargetFps;
        _failureReason = "";
        Volatile.Write(ref _captureFailed, 0);
    }

    private void SetFailure(string reason)
    {
        _failureReason = reason;
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

    /// <summary>最前面的那个窗口（画面跟随用它判断「绑定的程序在不在前台」）。</summary>
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
