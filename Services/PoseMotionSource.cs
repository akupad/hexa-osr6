using System;

namespace Hexa.Services;

/// <summary>
/// 姿态驱动的运动源：把「髋中点相对踝中点 / 画面下缘的竖直位移」变成 0–100 的深度，
/// 把相邻两帧的深度变化率变成速度，交给 <see cref="ScreenWatchService"/> 现有的画面同步链路
/// （深度 → 半幅、速度 → 频率，再经舒适档 / 限速 / 限位）。
///
/// <b>深度公式</b>（<see cref="DepthFromKeypoints"/>，纯函数、可单测）：
/// <code>
///   髋 y    = 有效髋点(11,12) 的 y 均值；一个都没有 → 不输出（NaN）
///   参考线  = 画面内踝点(15,16) 的 y 均值（y 越出 [1, frameHeight-1] 的按幻觉丢弃）；踝全丢 → 画面下缘 frameHeight - 1
///   hipAbove = 参考线 y − 髋 y            // 髋在参考线上方多少像素
///   depth    = clamp(100 × (1 − hipAbove / 参考线 y), 0, 100)
/// </code>
/// 站着（髋约在脚踝一半高处）→ 约 50；往下沉（髋贴近踝/下缘）→ 接近 100；髋高到画面顶 → 接近 0。
/// 置信度 &lt; <see cref="MinKeypointConfidence"/> 的关键点直接丢弃，不参与任何均值。
///
/// <b>为什么这样定义</b>：比例而不是像素，换窗口大小/缩放都不用重新标定；参考线优先用踝，
/// 是因为「同一个人」的腿长在一段画面里基本不变，而画面下缘只说明镜头怎么裁。
/// 踝一时看不见时退回下缘，会在深度上留一个台阶（一两帧内被平滑吃掉），这是已知取舍。
///
/// <b>线程模型</b>：抓帧线程只做一次 <see cref="Submit"/>（节流到 10–15fps + 拷一块 160×90 的 BGRA），
/// 推理在私有后台线程上跑，和识别模型 / 姿态 ROI 两条线程同一个套路。推理结果只写本对象的字段，
/// 调用方用 <see cref="TryRead"/> 读，永远读不到半帧。
///
/// <b>默认不开</b>：调用方（<see cref="ScreenWatchService"/> 按 <c>AppSettings.PoseVisionEnabled</c>）不喂帧时
/// 这条线程根本不会起来，缓冲也不会分配 —— 关掉就是零开销。
/// </summary>
public sealed class PoseMotionSource : IDisposable
{
    /// <summary>关键点置信度下限：低于它的点一律丢掉（手掌边角、被挡住的那半边身体）。</summary>
    public const double MinKeypointConfidence = 0.30;

    /// <summary>抽帧间隔（毫秒）：80ms ≈ 12.5fps，落在要求的 10–15fps 中间。</summary>
    public const int SampleIntervalMs = 80;

    /// <summary>信号的保鲜期（毫秒）：超过它还没算出新结果就当作没有姿态信号，调用方退回画面同步。</summary>
    public const int FreshMs = 600;

    /// <summary>算速度时允许的帧间隔（秒）：太小是同一帧重算，太大是掉帧/时钟跳变，两种都不算速度。</summary>
    public const double MinSpeedDtSeconds = 0.02;
    public const double MaxSpeedDtSeconds = 0.50;

    /// <summary>速度的物理上限（深度单位/秒）：正常人再快也到不了 2000，超过就是关键点飞了。</summary>
    public const double MaxSpeedPerSecond = 2000;

    /// <summary>深度的平滑系数（每次采样）：下沉（深度变大）跟得快，回弹跟得慢 —— 别让一帧跳点把设备拽一下。</summary>
    private const double DepthAttack = 0.55;
    private const double DepthRelease = 0.22;

    /// <summary>速度的平滑系数：速度是差值算出来的，噪声比深度大，压得更狠一点。</summary>
    private const double SpeedSmoothing = 0.35;

    /// <summary>速度→频率的标定增益：纯物理式 f=v/(2π·半幅) 会把实测深蹲的中速（43.2 深度单位/秒、半幅≈36）
    /// 钉到 0.19Hz，被调用方 0.5Hz 地板吃成常数。以 398 帧真实深蹲视频标定：目标常规幅度动作落在 1Hz 中段
    /// → (2π×36)/43.2 ≈ 5.24 → 5.2；慢速仍由调用方 FollowHzMin=0.5 兜底（2026-11 复测标定）。</summary>
    public const double FollowHzGain = 5.2;

    /// <summary>速度→频率的上限：与 ScreenWatchService.FollowHzMax（2.5）对齐，快速动作最多 2.5Hz。</summary>
    public const double FollowHzCap = 2.5;

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);

    // 两块 BGRA 缓冲交替用：抓帧线程写一块，推理线程读另一块（_busy 保证任何时刻只有一个人碰同一块）。
    private byte[]? _pending;
    private byte[]? _work;
    private int _pendingLength;
    private int _workWidth, _workHeight;
    private long _workStampMs;
    private bool _busy;
    private long _lastSubmitMs;
    private Thread? _thread;
    private volatile bool _stop;

    // 信号：推理线程写、抓帧线程读。double 在 x64 下读写原子，用 Volatile 保证可见性（同 ScreenWatchService）。
    private double _depth = double.NaN;         // 平滑后的深度 0–100
    private double _speed;                      // 平滑后的速度（深度单位/秒）
    private double _rawDepth = double.NaN;      // 上一帧的原始深度（算速度用）
    private double _rawSeconds;
    private long _signalMs;                     // 最近一次算出信号的时刻（Environment.TickCount64）
    private int _hasSignal;
    private int _sampleCount;                   // 成功算出的帧数（诊断用）
    private int _submitCount, _detectCount, _personCount;   // 交帧数 / 真跑推理的帧数 / 其中检出人的帧数（诊断用）

    /// <summary>已经算出过多少帧有效信号（诊断 / 自检用；关着时永远是 0）。</summary>
    public int SampleCount => Volatile.Read(ref _sampleCount);

    /// <summary>这条推理线程起来了吗（诊断用：关着时是 false）。</summary>
    public bool IsRunning { get { lock (_gate) return _thread is { IsAlive: true }; } }

    /// <summary>交给推理线程的帧数（诊断用：0 = 抓帧侧根本没喂过）。</summary>
    public int SubmitCount => Volatile.Read(ref _submitCount);

    /// <summary>真的跑过推理的帧数（诊断用：比 SubmitCount 少很多就是推理跟不上）。</summary>
    public int DetectCount => Volatile.Read(ref _detectCount);

    /// <summary>推理结果里检出人的帧数（诊断用：0 就是「画面里没人」或被阈值挡住）。</summary>
    public int PersonCount => Volatile.Read(ref _personCount);

    /// <summary>最近一次平滑后的深度（0–100，NaN = 还没算过）。</summary>
    public double LastDepth => _depth;

    // ── 纯函数（可独立单测）──────────────────────────────────────────

    /// <summary>这个关键点能不能用：坐标有限且置信度过线。</summary>
    public static bool IsUsable(PosePoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y)
        && double.IsFinite(point.Confidence) && point.Confidence >= MinKeypointConfidence;

    /// <summary>
    /// 从 COCO 17 关键点算深度（0–100）。<b>没有可用髋点时返回 false</b>（此时调用方必须不输出，
    /// 宁可不动也别乱动）；踝点一个都没有时退回画面下缘当参考线。
    /// </summary>
    public static bool TryDepthFromKeypoints(PosePoint[] keypoints, int frameHeight, out double depth)
    {
        depth = 0;
        if (keypoints is null || keypoints.Length < 17 || frameHeight < 2) return false;

        double hipSum = 0;
        int hips = 0;
        for (int i = 11; i <= 12; i++)            // 11 左髋、12 右髋
        {
            if (!IsUsable(keypoints[i])) continue;
            hipSum += keypoints[i].Y;
            hips++;
        }
        if (hips == 0) return false;              // 髋都没有：这一帧不算数
        double hipY = hipSum / hips;

        double ankleSum = 0;
        int ankles = 0;
        for (int i = 15; i <= 16; i++)            // 15 左踝、16 右踝
        {
            if (!IsUsable(keypoints[i])) continue;
            // 画面外/贴边的踝是检测幻觉（真实视频上会把参考线拽到髋以上 → 深度假满 100），整点丢弃不进均值。
            if (keypoints[i].Y < 1 || keypoints[i].Y > frameHeight - 1) continue;
            ankleSum += keypoints[i].Y;
            ankles++;
        }
        // 踝丢了就退回画面下缘：至少「髋沉向画面底部」这层信息还在。
        double referenceY = Math.Clamp(ankles > 0 ? ankleSum / ankles : frameHeight - 1, 1, frameHeight - 1);
        if (!double.IsFinite(referenceY) || referenceY < 1) return false;

        double hipAbove = referenceY - hipY;      // 髋在参考线上方多少像素；≤0 = 已经沉到参考线或更下
        double raw = 100.0 * (1.0 - hipAbove / referenceY);
        if (!double.IsFinite(raw)) return false;

        depth = Math.Clamp(raw, 0, 100);          // 所有输出一律夹在 0–100
        return true;
    }

    /// <summary>
    /// 同上，但没有可用髋点时返回 <see cref="double.NaN"/>（= 这一帧不输出）。
    /// 用 NaN 而不是 0：0 是一个合法的深度（髋在画面顶上），调用方分不清「没有信号」和「很浅」。
    /// </summary>
    public static double DepthFromKeypoints(PosePoint[] keypoints, int frameHeight) =>
        TryDepthFromKeypoints(keypoints, frameHeight, out double depth) ? depth : double.NaN;

    /// <summary>
    /// 相邻两帧的深度变化率（深度单位/秒，恒 ≥ 0）。两种帧不算：dt &lt; <see cref="MinSpeedDtSeconds"/>
    /// （同一帧重算）和 dt &gt; <see cref="MaxSpeedDtSeconds"/>（掉帧，差值除以大会凭空放大）。
    /// 任一深度是 NaN（上一帧没信号）时也算 0 —— 断档之后的第一帧只重建基线，不冒充「猛地一下」。
    /// </summary>
    public static double SpeedFromDepths(double previousDepth, double currentDepth, double dtSeconds)
    {
        if (!double.IsFinite(previousDepth) || !double.IsFinite(currentDepth)) return 0;
        if (!double.IsFinite(dtSeconds) || dtSeconds < MinSpeedDtSeconds || dtSeconds > MaxSpeedDtSeconds) return 0;
        return Math.Clamp(Math.Abs(currentDepth - previousDepth) / dtSeconds, 0, MaxSpeedPerSecond);
    }

    /// <summary>
    /// 速度 → 频率（Hz）：正弦的峰值速度 = 2π·f·半幅，所以 f = 速度 ÷ (2π·半幅)，乘标定增益
    /// <see cref="FollowHzGain"/>（纯物理式把实测深蹲中速钉到 0.19Hz、被 0.5Hz 地板吃成常数）并夹到
    /// <see cref="FollowHzCap"/>。半幅（<paramref name="amplitude"/>）必须传这一拍实际要下发的半幅，
    /// 否则限速器会把实得行程吃掉（和 ScreenWatchService.FollowComputeHz 里那条注释同一个道理）。
    /// </summary>
    public static double SpeedToHz(double speedPerSecond, double amplitude)
    {
        if (!double.IsFinite(speedPerSecond) || !double.IsFinite(amplitude)) return 0;
        double travel = Math.Max(5, Math.Abs(amplitude));
        return Math.Clamp(FollowHzGain * Math.Max(0, speedPerSecond) / (Math.Tau * travel), 0, FollowHzCap);
    }

    // ── 画面同步的信号源裁决（纯函数、可单测）────────────────────────

    /// <summary>姿态门控这一拍的裁决：本轮采用的深度（0–100）与速度（深度单位/秒），以及是否回落画面分。
    /// <see cref="Fallback"/> 为 true 时 Depth/Speed 无意义（调用方走画面分那套兜底，别再读它们）。</summary>
    public readonly record struct PoseFollowSignal(double Depth, double Speed, bool Fallback);

    /// <summary>
    /// 画面同步这一拍用哪个信号源（纯函数，状态由调用方持有）：
    /// 姿态有效 → 深度/速度全部来自姿态，画面分一个字节都不参与；
    /// 姿态刚断（距上次有效 &lt; <paramref name="holdMs"/>）→ 原样保持最后一次有效值，不跳、不混画面分；
    /// 断到超过保持期 → 才回落画面分（<see cref="Fallback"/>=true，这是唯一的回落点）。
    /// 保持期是开区间：距上次有效<b>达到</b> <paramref name="holdMs"/> 就算超时回落。
    /// <paramref name="lastValidMs"/> &lt;= 0 表示本会话还没出现过有效姿态信号 —— 没有旧值可保持，直接回落，
    /// 绝不把 0 当深度（和 <see cref="TryRead"/>「没信号不当 0」是同一条纪律）。
    /// </summary>
    public static PoseFollowSignal ResolveFollowSignal(
        bool poseOk, double poseDepth, double poseSpeed,
        long nowMs, long lastValidMs, double heldDepth, double heldSpeed, long holdMs)
    {
        if (poseOk) return new PoseFollowSignal(poseDepth, poseSpeed, false);
        if (lastValidMs > 0 && nowMs - lastValidMs < holdMs)
            return new PoseFollowSignal(heldDepth, heldSpeed, false);   // 保持：沿用最后一次有效姿态值
        return new PoseFollowSignal(0, 0, true);                        // 回落：唯一的兜底点
    }

    // ── 抓帧线程这一侧：节流 + 交帧 ─────────────────────────────────

    /// <summary>
    /// 把当前这一帧（BGRA32，宽×高，通常是 160×90 的分析图）交给姿态线程。
    /// 抓帧线程只做：一次时间戳比较（节流到 10–15fps）和一次 57KB 的内存拷贝 —— 推理不在这里。
    /// 返回 true = 这一帧真的交出去了。
    /// </summary>
    public bool Submit(byte[] bgra, int width, int height)
    {
        if (bgra is null || width <= 0 || height <= 0) return false;
        if (bgra.Length < width * height * 4) return false;

        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_stop) return false;
            if (now - _lastSubmitMs < SampleIntervalMs) return false;   // 节流：最多 12.5fps
            _lastSubmitMs = now;
            if (_busy) return false;                                    // 上一帧还在推理：丢掉这一帧，不排队堆积
            EnsureThreadLocked();                                       // 第一次交帧才起推理线程（关着时零开销）
            EnsureBuffersLocked(width, height);
            Buffer.BlockCopy(bgra, 0, _pending!, 0, width * height * 4);
            (_pending, _work) = (_work, _pending);                      // 把刚填好的那块交给推理线程
            _workWidth = width;
            _workHeight = height;
            _workStampMs = now;
            _busy = true;
            _submitCount++;
            // Set 必须在锁内：Dispose 是「锁内置 _stop → 锁外 Set → Join → Dispose(_wake)」，
            // 放到锁外就会出现「这里刚判过 _stop=false，那边已经 Dispose 了句柄」的 Set-after-Dispose。
            try { _wake.Set(); } catch (ObjectDisposedException) { /* 认了：这一帧不唤醒，等于丢弃 */ }
        }
        return true;
    }

    // ── 跟随侧：读信号 ──────────────────────────────────────────────

    /// <summary>
    /// 读最近一次姿态信号。返回 false = 这一拍没有可用信号（还没算出来 / 过期 / 画面里没有可用髋踝），
    /// 调用方必须照旧用原来的画面同步，绝不能拿 0 当「没有」。
    /// </summary>
    public bool TryRead(out double depth0To100, out double speedPerSecond)
    {
        depth0To100 = 0;
        speedPerSecond = 0;
        if (Volatile.Read(ref _hasSignal) == 0) return false;
        if (Environment.TickCount64 - Interlocked.Read(ref _signalMs) > FreshMs) return false;   // 过期：跟不上了
        depth0To100 = Math.Clamp(Volatile.Read(ref _depth), 0, 100);
        speedPerSecond = Math.Max(0, Volatile.Read(ref _speed));
        return true;
    }

    /// <summary>作废当前信号（停止同步 / 关了开关时调用）：线程留着，但下一次 <see cref="TryRead"/> 一定返回 false。</summary>
    public void Reset()
    {
        Volatile.Write(ref _hasSignal, 0);
        _rawDepth = double.NaN;
        _rawSeconds = 0;
    }

    // ── 推理线程 ────────────────────────────────────────────────────

    private void EnsureThreadLocked()
    {
        if (_thread is { IsAlive: true }) return;
        _stop = false;
        var thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Hexa 姿态运动",
            Priority = ThreadPriority.BelowNormal,   // 和抓帧 / 识别线程一样礼貌：别抢设备通信的核
        };
        _thread = thread;
        thread.Start();
    }

    private void WorkerLoop()
    {
        // 循环顶先查 _stop 再等：Dispose 的 Join(500) 超时后就会释放 _wake，此时绝不能再进 WaitOne
        // （已释放句柄 → 未捕获 ObjectDisposedException 会崩进程）；WaitOne 挪进 try 兜「正等时被释放」的最后一线。
        while (!_stop)
        {
            try { _wake.WaitOne(200); }              // 有界等待：Dispose 时即使 Wake 被上一轮吃掉也能退出
            catch (ObjectDisposedException) { return; }
            if (_stop) return;

            byte[]? frame;
            int width, height;
            long stampMs;
            lock (_gate)
            {
                if (!_busy) continue;
                frame = _work;
                width = _workWidth;
                height = _workHeight;
                stampMs = _workStampMs;
            }

            try
            {
                // 没装模型：一个字节都不推理（帧已经在缓冲里，直接丢掉 —— 不刷日志、不报错）。
                // 这里判 IsInstalled（文件在不在）而不是 IsReady（会话加载没加载）：IsReady 在首帧之前恒为 false，
                // 拿它当门就等于永不推理。会话由 Detect 内部懒加载，失败一次就闩住，不会每 80ms 重试。
                if (PoseTracker.IsInstalled)
                {
                    PosePerson? person = PoseTracker.Detect(frame!, width, height);
                    Interlocked.Increment(ref _detectCount);
                    if (person is not null) Interlocked.Increment(ref _personCount);
                    Accept(person, height, stampMs);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"姿态运动：这一帧推理失败，跳过 —— {ex.Message}");
            }
            finally
            {
                lock (_gate) _busy = false;
            }
        }
    }

    /// <summary>把一次推理结果变成信号（或明确地「不变成信号」）。任何异常都不该把我抛出去。</summary>
    private void Accept(PosePerson? person, int frameHeight, long stampMs)
    {
        double raw = person is null ? double.NaN : DepthFromKeypoints(person.Keypoints, frameHeight);
        if (!double.IsFinite(raw))
        {
            // 没有可用髋+踝：不输出。但**不立刻**清信号 —— 偶尔一帧丢失（人侧了下身）不该让设备跳回画面同步；
            // 超过保鲜期还没恢复，调用方本来就会当成没有信号。
            _rawDepth = double.NaN;
            return;
        }

        double seconds = stampMs / 1000.0;
        double speed = SpeedFromDepths(_rawDepth, raw, seconds - _rawSeconds);

        double depth = double.IsFinite(_depth) ? _depth + (raw - _depth) * (raw > _depth ? DepthAttack : DepthRelease) : raw;
        double smoothedSpeed = _sampleCount == 0 ? speed : _speed + (speed - _speed) * SpeedSmoothing;

        _depth = Math.Clamp(depth, 0, 100);
        _speed = Math.Clamp(smoothedSpeed, 0, MaxSpeedPerSecond);
        _rawDepth = raw;
        _rawSeconds = seconds;
        Interlocked.Exchange(ref _signalMs, stampMs);
        Volatile.Write(ref _hasSignal, 1);
        Interlocked.Increment(ref _sampleCount);
    }

    private void EnsureBuffersLocked(int width, int height)
    {
        int bytes = width * height * 4;
        if (_pending is null || _pendingLength != bytes)
        {
            _pending = new byte[bytes];
            _work = new byte[bytes];
            _pendingLength = bytes;
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_stop) return;
            _stop = true;
            thread = _thread;
            _thread = null;
        }
        try { _wake.Set(); } catch (ObjectDisposedException) { /* 已经收过了 */ }
        if (thread is { IsAlive: true })
        {
            try { thread.Join(500); } catch (Exception ex) { AppLogger.Warn($"姿态运动：等线程收尾时出错 —— {ex.Message}"); }
        }
        try { _wake.Dispose(); } catch (ObjectDisposedException) { }
    }
}
