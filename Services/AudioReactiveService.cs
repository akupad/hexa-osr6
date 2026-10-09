using Hexa.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace Hexa.Services;

public sealed record AudioOutputDevice(string Id, string Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Id) ? $"系统默认 · {Name}" : Name;
}

public sealed class AudioReactiveService : IDisposable
{
    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;
    private readonly Lock _lock = new();

    private WasapiRecorder? _capture;
    private MMDevice? _captureDevice;
    private System.Timers.Timer? _applyTimer;

    // 由 WASAPI 回调线程写入、UI/规则引擎线程读取；x64 下 double 读写原子，
    // 用 Volatile.Read/Write 保证跨线程可见性（见下方访问器）。
    private double _smoothedRms;
    private double _smoothedBass;
    private double _energy;
    private double _beatPulse;
    private double _lowPassState;
    private double _baseIntensity = 1.0;
    private int _restartScheduled;

    // ── 声音驱动动作（「声音响应」的实际行为）──────────────────────────
    // 以前：开启后直接 StartAuto()，设备不管有没有声音都在跑，声音只改强度。
    // 现在：安静 → 不动（缓慢回中后停发）；有声音 → 位置跟着声音来回，越响越快越深。
    private const double ApplyIntervalMs = 50;
    private const double SilenceStopMs   = 700;
    private double _phase;
    private double _silenceMs;
    private bool _motionActive;
    private double _envelope;
    private int _blocked;
    private int _motionActiveFlag;
    private volatile bool _disposed;

    // ── 事件化动作（AudioEventMotion = true 时走这条路径）───────────────
    // 音频线程：AudioEventDetector 检出事件 → 无锁单生产者/单消费者环 → 定时器线程按标定值对齐后渲染。
    // 两条路径（事件 / 音量循环）共用静音与安全逻辑，拨开关随时切换。
    private const int PendingEventCapacity = 32;               // 2 的幂：取模用位与
    private const int PendingEventMask     = PendingEventCapacity - 1;
    private const int HeldEventCapacity    = 8;
    private const double EventInterpolationRatio = 0.35;       // 插值时间 = 事件时长 × 0.35（有起落，不是瞬移）
    private const double EventSettleSeconds      = 0.35;       // 事件结束后回中的缓动时长
    private const double EventLabelLingerSeconds = 2.0;        // 界面「最近事件」保鲜期（覆盖最长的渐强 1.6s）
    private const double MaxPhaseOffset          = 0.5;        // 负向标定最多吞掉事件前半段，保证还有可见动作
    private const double SensitivityGainMin      = 0.75;
    private const double SensitivityGainMax      = 1.25;

    private static readonly double[] CenterPose = [50, 50, 50, 50, 50, 50];   // 回中用（引擎只读入参，不修改它）

    // ── 人声模式（「响应对象 = 角色声音」：一次呻吟 = 一次抽插）──────────────
    // 与事件路径的分工：事件路径是「有事发生才动一下」的音乐口径；人声要的是**持续抽插**，
    // 所以这条路自己算频率、自己管余韵，并且**忽略**「事件化动作」那个勾选（它管的是音乐那三种方式）。
    private const double VoiceMinRate       = 0.5;      // 抽插频率下限：每秒 0.5 次
    private const double VoiceMaxRate       = 2.2;      // 上限：每秒 2.2 次（再快设备跟不出来，也不像抽插）
    private const double VoiceFallbackBase  = 0.6;      // 数不出她的节奏时：0.6 + 1.2 × 强度
    private const double VoiceFallbackGain  = 1.2;
    private const double VoiceRateSmoothing = 0.25;     // 频率平滑（数出来的节奏会一跳一跳，直接换会让设备一顿）
    private const double VoiceAfterglowMs   = 1500;     // 余韵：人声掉下去后再抽 1.5 秒、幅度递减
    /// <summary>脚本播放器声明的控制权名字（与 FunscriptPlayerService 里那个字符串一致）。</summary>
    private const string ScriptInputOwner   = "脚本播放";

    // ── 拍点：谱通量起音（FFT）────────────────────────────────────────
    // 为什么本服务自己也留一个实例，而不是读检测器里那个：
    //   检测器只在「事件化动作 / 只监听 / 人声模式」时才被喂数据，而拍点脉冲（界面电平、
    //   MotionEngine 的音频特征、"节拍"响应模式）在任何采集状态下都要有 —— 所以这个实例
    //   无条件跟着采集走。两个实例是同一个类、同一套参数，读数一致；一次 FFT 约 20µs / 10ms 帧
    //   （不到 0.3% 一个核），两套同时跑也不到 0.6%，换来的是"拍点永远在线"。
    private readonly SpectralFluxOnset _flux = new();

    private readonly AudioEventDetector _detector = new();

    /// <summary>
    /// 节拍跟踪器（界面显示 BPM / 拍点相位用）。没在监听时锁不上，但对象一直在。
    /// 注意：起音/频谱那一套在 <see cref="SpectralFluxOnset"/>（_flux）里，不是 _detector —— 两者是同一个文件里的两个类。
    /// </summary>
    public TempoTracker? Tempo => _flux?.Tempo;
    private readonly MotionEvent[] _eventScratch = new MotionEvent[AudioEventDetector.MaxEventsPerBlock];
    private readonly MotionEvent[] _pendingEvents = new MotionEvent[PendingEventCapacity];
    private int _pendingWrite;                       // 音频线程独占写
    private int _pendingRead;                        // 定时器线程独占写
    private long _pendingDropped;                    // 音频线程独占，只用于诊断
    private readonly MotionEvent[] _heldEvents = new MotionEvent[HeldEventCapacity];   // 定时器线程独占
    private readonly double[] _heldDueSeconds = new double[HeldEventCapacity];
    private int _heldCount;
    private MotionEventKind _activeKind;
    private double _activeStrength;
    private double _activeDuration;
    private double _activeElapsed;
    private double _activePhaseOffset;
    private bool _active;
    private long _respondedCount;
    // ── 人声模式的状态 ──
    // 相位按「次」算：0 = 完全抽出来，0.45 = 插到底（形态见 MotionVocabulary 的 Voice 分支）。
    private double _voicePhase;
    private double _voiceRate;                                   // 平滑后的抽插频率（次/秒）
    private double _voiceAfterglowMs;                            // 余韵剩余时间
    private double _voiceHoldLevel;                              // 「她刚刚有多响」：余韵用它打底，一次比一次浅
    private double _lastVoiceEventAt = double.NegativeInfinity;   // 音频线程写：最近一次人声起音的时刻
    private double _voiceConsumedAt = double.NegativeInfinity;    // 定时器线程：已经用过的那一次起音
    private double[] _monoScratch = new double[4096];          // 单声道解码缓冲（只在块变大时重建一次）
    private readonly long _clockOrigin = Stopwatch.GetTimestamp();

    /// <summary>单调时钟（秒）：音频线程与定时器线程共用一条时间轴，用来算事件年龄和延迟对齐。</summary>
    private double NowSeconds() => (Stopwatch.GetTimestamp() - _clockOrigin) / (double)Stopwatch.Frequency;

    /// <summary>音频线程 → 定时器线程：无锁单生产者/单消费者环。满了丢新的（20Hz 排空，正常不会满）。</summary>
    private void EnqueueEvent(in MotionEvent ev)
    {
        int write = _pendingWrite;
        if (write - Volatile.Read(ref _pendingRead) >= PendingEventCapacity)
        {
            _pendingDropped++;
            return;
        }
        _pendingEvents[write & PendingEventMask] = ev;
        Volatile.Write(ref _pendingWrite, write + 1);
    }

    public double Energy => Volatile.Read(ref _energy);
    /// <summary>当前声音强度（0–1，已过门限与灵敏度）。界面电平条用它。</summary>
    public double Envelope => Volatile.Read(ref _envelope);
    /// <summary>设备此刻是否正由声音驱动（静音超时后会变 false）。</summary>
    public bool MotionActive => Volatile.Read(ref _motionActiveFlag) != 0;
    /// <summary>非空 = 声音在响但指令下不去（被游戏伴随接管 / 急停 / 未连接等）。</summary>
    public string BlockedReason => Volatile.Read(ref _blocked) != 0 ? DescribeBlocked() : "";

    private string DescribeBlocked()
    {
        if (_engine.EmergencyStopped) return "设备处于急停锁定（点左下角「全部归中」解锁）";
        if (!_engine.CanRun) return "设备未连接或正在归中";
        if (_engine.DirectInputOwner is { Length: > 0 } owner && owner != "audio")
            return owner switch
            {
                "bridge" => "游戏桥正在控制设备（游戏里在动，声音响应先让位）",
                "ayva" => "网页遥控器正在控制设备（网页上在动，声音响应先让位）",
                ScriptInputOwner => ScriptPlayingReason,
                _ => $"「{owner}」正在控制设备",
            };
        // 脚本播放时它不一定抢得到控制权（本服务先拿到 "audio" 之后，它那边 force:false 的声明会被拒），
        // 但它照样每帧在写同一批轴 —— 两个写入源交替覆盖就是设备乱抖。这种情况 DirectInputOwner
        // 还写着 "audio"，从控制权上根本看不出来，所以这里单独认一次。
        if (ScriptIsPlaying()) return ScriptPlayingReason;
        if (_engine.RuleEngineActive) return "游戏伴随正在接管，先关闭它";
        if (_engine.IsRunning) return "引擎正被其它动作占用";
        return "设备当前不接受直接下发";
    }

    private const string ScriptPlayingReason = "脚本正在播放，设备归脚本管（脚本一停就自动还给声音响应）";

    /// <summary>脚本播放器是不是正在驱动设备（它每帧都往同一批轴上写）。</summary>
    private static bool ScriptIsPlaying() =>
        App.FunscriptPlayer is { IsPlaying: true };

    /// <summary>
    /// 除了本服务，还有别人在写同一批轴吗？
    ///
    /// 为什么光靠 <see cref="MotionEngine.TryClaimDirectInput"/> 不够：它只能拦「别人**先**声明」的情况。
    /// 本服务先拿到 "audio" 之后，脚本播放器那边的声明会被拒（它有意用 force:false，不硬抢），
    /// 但它照样逐帧下发 —— 两根写入源交替覆盖同一根轴，表现出来就是设备乱抖，
    /// 而 DirectInputOwner 仍然写着 "audio"，从控制权上完全看不出异常。
    /// 所以这里是本服务自己的硬规矩：<b>有人在写，本服务就不写、也不去抢。</b>
    /// </summary>
    private bool OthersAreWriting() =>
        _engine.DirectInputOwner is { Length: > 0 } owner && !string.Equals(owner, "audio", StringComparison.Ordinal)
        || ScriptIsPlaying();

    /// <summary>当前是不是「角色声音 · 呻吟」这一路（设置里的 AudioResponseMode = "voice"）。</summary>
    private bool IsVoiceMode() =>
        _cfg.AudioReactiveEnabled
        && string.Equals(_cfg.AudioResponseMode, "voice", StringComparison.OrdinalIgnoreCase);
    public double Rms => Volatile.Read(ref _smoothedRms);
    public double Bass => Volatile.Read(ref _smoothedBass);
    public double BeatPulse => Volatile.Read(ref _beatPulse);
    public bool Capturing => _capture != null;

    // ── 事件状态（界面显示「现在在响应什么 / 已响应多少次」）───────────
    /// <summary>最近一次事件类型；超过 2 秒没有新事件就回到 None（安静）。</summary>
    public MotionEventKind LastEventKind =>
        _detector.LastEventKind == MotionEventKind.None || LastEventAgeSeconds > EventLabelLingerSeconds
            ? MotionEventKind.None
            : _detector.LastEventKind;

    /// <summary>最近一次事件的强度 0–1（检测器的原始强度，不含灵敏度缩放）。</summary>
    public double LastEventStrength => _detector.LastEventStrength;

    /// <summary>累计「已经响应过」的事件数（界面「已响应 N 次」）；被抢占/静音而没渲染的不计数。</summary>
    public long EventCount => Volatile.Read(ref _respondedCount);

    /// <summary>距最近一次事件的秒数；还没有过事件就是 +∞。</summary>
    public double LastEventAgeSeconds => _detector.AgeSeconds(NowSeconds());

    public AudioReactiveService(MotionEngine engine, AppSettings cfg)
    {
        _engine = engine;
        _cfg = cfg;
        if (ShouldCapture()) StartCapture();
    }

    public static IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var result = new List<AudioOutputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            result.Add(new AudioOutputDevice("", defaultDevice.FriendlyName));
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (result.Any(item => string.Equals(item.Id, device.ID, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("枚举音频输出设备失败", ex);
            result.Add(new AudioOutputDevice("", "系统默认输出"));
        }
        return result;
    }

    public void Refresh()
    {
        if (_disposed) return;   // Dispose 之后又被 UI 调一次 → 会重建采集与定时器，且没人再释放
        if (ShouldCapture())
        {
            // 设备切换/重启用：保留当前强度与自动动作，避免切设备时产生可见的“闪断”落差。
            StopCapture(resetIntensity: false, stopAuto: false);
            StartCapture();
        }
        else
        {
            StopCapture();
        }
    }

    private void StartCapture()
    {
        if (_disposed) return;   // 同上：已销毁的服务不能再重建采集（否则它会继续往设备下发指令）
        lock (_lock)
        {
            if (_capture != null) return;
            try
            {
                _baseIntensity = _cfg.IntensityScale;
                var capture = CreateCapture();
                capture.DataAvailable += (buffer, _, _, _) => OnDataAvailable(buffer);
                capture.RecordingStopped += OnRecordingStopped;
                ResetEventState();      // 重启采集（换设备/断线恢复）后从干净状态重新收敛
                capture.StartRecording();
                _capture = capture;

                _applyTimer ??= new System.Timers.Timer(50) { AutoReset = true };
                _applyTimer.Elapsed -= ApplyTimerElapsed;
                _applyTimer.Elapsed += ApplyTimerElapsed;
                _applyTimer.Start();

                AppLogger.Info($"音频响应已启动: {capture.WaveFormat.SampleRate} Hz / {capture.WaveFormat.Channels} ch");
            }
            catch (Exception ex)
            {
                _capture?.Dispose();
                _capture = null;
                _captureDevice?.Dispose();
                _captureDevice = null;
                // 留一句给人看的原因：以前只写日志，界面永远只说"正在准备监听声音…换成别的来源再试"，
                // 用户根本不知道是设备没了还是被独占（子代理审计发现）。
                StartError = "音频设备打不开：" + ex.Message + "（换一个「声音来源」，或看看是不是被别的软件独占）";
                AppLogger.Error("音频响应启动失败", ex);
            }
        }
    }

    /// <summary>上次启动采集失败的原因（给界面显示用；成功启动时清空）。</summary>
    public string StartError { get; private set; } = "";

    private WasapiRecorder CreateCapture()
    {
        // NAudio 3.x：WasapiLoopbackCapture 已过时，改用 WasapiRecorderBuilder().WithLoopbackCapture()
        var builder = new WasapiRecorderBuilder().WithLoopbackCapture();
        if (!string.IsNullOrWhiteSpace(_cfg.AudioDeviceId))
        {
            using var enumerator = new MMDeviceEnumerator();
            _captureDevice = enumerator.GetDevice(_cfg.AudioDeviceId);
            builder = builder.WithDevice(_captureDevice);
        }
        return builder.Build();
    }

    private void StopCapture(bool resetIntensity = true, bool stopAuto = true)
    {
        WasapiRecorder? capture;
        lock (_lock)
        {
            _applyTimer?.Stop();
            capture = _capture;
            _capture = null;
            Volatile.Write(ref _smoothedRms, 0);
            Volatile.Write(ref _smoothedBass, 0);
            Volatile.Write(ref _energy, 0);
            _lowPassState = 0;
            Volatile.Write(ref _beatPulse, 0);
        }

        if (capture != null)
        {
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        _captureDevice?.Dispose();
        _captureDevice = null;
        _engine.SetAudioFeatures(0, 0, 0);

        if (resetIntensity && !_engine.RuleEngineActive) _engine.IntensityScale = _cfg.IntensityScale;
        _engine.ReleaseDirectInput("audio");   // 静音超时：交出控制权，方便游戏桥/其它模式接管
        _motionActive = false;
        Volatile.Write(ref _motionActiveFlag, 0);
        _phase = 0;
        _silenceMs = 0;

        // 事件化路径：检测器统计与事件队列一起清空，下次采集从干净状态重新收敛。
        ResetEventState();
        if (_pendingDropped > 0)
        {
            AppLogger.Warn($"音频事件队列处理不过来，已丢弃 {_pendingDropped} 个事件");
            _pendingDropped = 0;
        }
    }

    /// <summary>清空事件路径的全部状态（停止采集 / 切换设备 / 采集中断后重启时调用）。</summary>
    private void ResetEventState()
    {
        _detector.Reset();
        _flux.Reset();          // 谱通量：清掉阈值历史，重启采集后重新收敛
        DiscardEventQueues();
        _active = false;
        _activeElapsed = 0;
        _activePhaseOffset = 0;
        _activeDuration = 0;
        _activeStrength = 0;
        _activeKind = MotionEventKind.None;
        // 人声模式的状态一起清（换设备 / 重新开始采集后从"没听见过"重新起步）
        _voicePhase = 0;
        _voiceRate = 0;
        _voiceAfterglowMs = 0;
        _voiceHoldLevel = 0;
        _voiceConsumedAt = double.NegativeInfinity;
        Volatile.Write(ref _lastVoiceEventAt, double.NegativeInfinity);
    }

    /// <summary>设备不可动 / 静音 / 被抢占：正在渲染的事件也要断掉，别在恢复后补播旧动作。</summary>
    private void CancelActiveEvent()
    {
        if (!_active) return;
        _active = false;
        _activeElapsed = 0;
        _activePhaseOffset = 0;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_capture, sender)) _capture = null;
            _applyTimer?.Stop();
        }
        (sender as IDisposable)?.Dispose();
        _captureDevice?.Dispose();
        _captureDevice = null;
        if (e.Exception != null) AppLogger.Error("音频捕获意外停止", e.Exception);
        else AppLogger.Warn("音频捕获已停止，可能发生了默认输出设备切换");

        // 意外停止（拔耳机、切默认输出设备、采集崩溃）必须把状态清干净：
        // 否则界面上还写着"正在跟着声音动"、能量条还停在最后一个值，
        // 而"安静时停住"那条静音门也因为没有新特征而失效（伴随动作不再停）。
        Volatile.Write(ref _smoothedRms, 0);
        Volatile.Write(ref _smoothedBass, 0);
        Volatile.Write(ref _energy, 0);
        Volatile.Write(ref _beatPulse, 0);
        _motionActive = false;
        Volatile.Write(ref _motionActiveFlag, 0);
        Volatile.Write(ref _blocked, 1);
        _engine.SetAudioFeatures(0, 0, 0);
        _engine.ReleaseDirectInput("audio");
        ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        if (!ShouldCapture() || Interlocked.Exchange(ref _restartScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // 默认输出设备切换可能持续数秒，循环重试直到恢复或用户手动关闭，
                // 避免 1.5 秒内未就绪就永久停摆。
                for (int attempt = 0; attempt < 150 && !_disposed; attempt++)
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    if (_disposed || !ShouldCapture() || Capturing) break;
                    StartCapture();
                    if (Capturing) break;   // 成功后立即退出，释放重启标记
                }
                if (!_disposed && ShouldCapture() && !Capturing)
                    AppLogger.Warn("音频响应多次重试仍未恢复捕获，请检查输出设备是否可用");
            }
            finally { Volatile.Write(ref _restartScheduled, 0); }
        });
    }

    /// <summary>
    /// 需要采集系统声音的情形：① 开了声音响应；② 伴随动作开了「安静时停住」
    /// （游戏伴随或自动跟随都要靠音量判断该不该动）；③ 游戏伴随的「激烈反应」要用声音；
    /// ④ 脚本播放开了「空档填缝」——填缝要靠声音事件，但**不能**让声音响应顺手接管设备，
    ///    所以只走 <see cref="AppSettings.AudioListenOnly"/> 这条「只采集、不输出」的通道。
    /// </summary>
    private bool ShouldCapture() =>
        _cfg.AudioReactiveEnabled
        || (_cfg.GateCompanionOnSilence && (_cfg.CompanionAuto || _cfg.RuleEngineEnabled))
        || (_cfg.RuleEngineEnabled && _cfg.CompanionReaction != "none")
        || _cfg.AudioListenOnly
        // ⑤ 氛围微动叠加（精确动作之上叠的那层呼吸感）也要音频特征，
        //    漏掉这一条它就是个"勾了没反应"的死开关 —— 单测只喂了特征、没走采集链，所以没测出来。
        || _cfg.AmbientOverlay;

    private void OnDataAvailable(ReadOnlySpan<byte> buffer)
    {
        try
        {
            var format = _capture?.WaveFormat;
            if (format == null) return;
            int bytesPerSample = format.BitsPerSample / 8;
            int channels = Math.Max(1, format.Channels);
            int frames = buffer.Length / Math.Max(1, bytesPerSample * channels);
            if (bytesPerSample <= 0 || frames <= 0) return;

            // 事件化动作要逐样本的单声道数据，直接复用这一轮解码结果（缓冲只在块变大时重建一次）。
            // 只监听模式（空档填缝）**无条件**喂检测器：填缝靠的就是事件，不该被「声音响应」那边的
            // 子开关（AudioEventMotion）牵连——那个开关只管声音响应自己怎么动。
            bool feedDetector = _cfg.AudioListenOnly || (_cfg.AudioEventMotion && _cfg.AudioReactiveEnabled)
                // 人声模式**必须**有检测器：人声强度（深度）和她的节奏（速度）都从它那儿来。
                // 挂在「事件化动作」勾选下面的话，那个勾选一取消（人声模式本来就忽略它），
                // 这条路的信号就全断了 —— 会变成一个"勾了没反应"的死开关。
                || IsVoiceMode();
            if (feedDetector && _monoScratch.Length < frames)
                _monoScratch = new double[Math.Max(frames, _monoScratch.Length * 2)];

            _flux.EnsureFormat(format.SampleRate);   // 采样率没变时是空操作
            double alpha = LowPassAlpha(format.SampleRate);
            double rmsAccumulator = 0;
            double bassAccumulator = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                double mono = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    int offset = (frame * channels + channel) * bytesPerSample;
                    mono += ReadSample(buffer, offset, bytesPerSample, format);
                }
                mono /= channels;
                // 拍点用的谱通量**无条件**跟着采样走（与事件检测器是否在跑无关，见 _flux 的注释）。
                _flux.Push(mono);
                if (feedDetector) _monoScratch[frame] = mono;
                rmsAccumulator += mono * mono;
                _lowPassState += alpha * (mono - _lowPassState);
                bassAccumulator += _lowPassState * _lowPassState;
            }

            double normalizedRms = NormalizeDb(Math.Sqrt(rmsAccumulator / frames), -55);
            double normalizedBass = NormalizeDb(Math.Sqrt(bassAccumulator / frames), -65);
            double smoothedRms = Volatile.Read(ref _smoothedRms);
            double smoothedBass = Volatile.Read(ref _smoothedBass);
            Volatile.Write(ref _smoothedRms, Smooth(smoothedRms, normalizedRms, normalizedRms > smoothedRms));
            Volatile.Write(ref _smoothedBass, Smooth(smoothedBass, normalizedBass, normalizedBass > smoothedBass));

            // 拍点判定：谱通量起音（见 SpectralFluxOnset），不再用「低音平滑值越过自适应均值 + 0.11」。
            // 为什么换：越线只看得见低频能量 —— 军鼓 / 踩镲 / 钢琴 / 拨弦的能量主要在中高频，
            // 在那条曲线上几乎看不出起伏（漏拍）；反过来贝斯线、底鼓余韵、渐强段落一直高于均值（连报）。
            // 谱通量量的是**频谱形状的变化**，再叠 SuperFlux 的 3 点最大值滤波治颤音误报，
            // 阈值仍然是自适应的（近 200ms 中位数 × 1.55），170ms 反跳的意图也照旧保留。
            // 低音能量一点没丢：smoothedBass 照算，界面「低频」和"节拍"模式的兜底都还在用它。
            Volatile.Write(ref _beatPulse, _flux.Pulse);

            Volatile.Write(ref _energy, _cfg.AudioResponseMode switch
            {
                "bass" => smoothedBass,
                // 「节拍」响应模式：以谱通量拍点为主，低音能量当兜底（底鼓再闷也总有低频）。
                "beat" => Math.Max(Volatile.Read(ref _beatPulse), smoothedBass * 0.28),
                // 人声模式：界面上的电平条 / 状态行 / 「安静就停」都跟着人声频带走 ——
                // 背景音乐再响，只要没有人在叫，这个数就是 0。
                "voice" => _detector.VoiceLevel,
                _ => _cfg.AudioLowPassEnabled ? smoothedBass : smoothedRms,
            });

            // 事件化动作：同一次回调里顺手把单声道采样喂给检测器（稳态零分配）。
            // 只在「事件化 + 声音响应都开着」时跑，且不关心当前是否被抢占 —— 界面要照实显示
            // 「现在检出了什么」，能不能下发由 ApplyTick 的抢占逻辑决定。
            if (feedDetector)
            {
                // 时间戳取这一块的**起点**：回调触发时块已经采完了，减掉块自身时长更准（直接影响 P4 对齐精度）。
                double blockStart = NowSeconds() - frames / (double)format.SampleRate;
                int detected = _detector.Process(_monoScratch.AsSpan(0, frames), format.SampleRate, blockStart, _eventScratch);
                for (int i = 0; i < detected; i++)
                {
                    MotionEvent ev = _eventScratch[i];
                    // 人声起音要"立刻抓住那一下"，所以单独记一份时刻：检测器里只留「最近一次事件」，
                    // 后面紧跟一个冲击（爆炸 / 门响）就会把这次呻吟盖掉，而人声模式的起音不能丢。
                    if (ev.Kind == MotionEventKind.Voice) Volatile.Write(ref _lastVoiceEventAt, ev.AtSeconds);
                    EnqueueEvent(ev);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("音频数据处理异常", ex);
        }
    }

    private void ApplyTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e) => ApplyTick();

    private void ApplyTick()
    {
        try
        {
            // 设备不可动（未连接 / 归中 / 急停）时必须如实反馈：
            // 以前这里直接 return，_blocked 不置位、_motionActive 也不清零，
            // 急停之后界面还写着「正在跟着声音动」。
            if (!_engine.CanRun)
            {
                Volatile.Write(ref _blocked, 1);
                if (_motionActive) { _motionActive = false; Volatile.Write(ref _motionActiveFlag, 0); }
                CancelActiveEvent();
                DiscardEventQueues();
                return;
            }

            // 无论当前由声音响应还是规则引擎驱动，都把音频特征喂给 MotionEngine，
            // 让自动动作里的微妙律动（低频/拍子）始终可用。
            _engine.SetAudioFeatures(Volatile.Read(ref _smoothedRms), Volatile.Read(ref _smoothedBass), Volatile.Read(ref _beatPulse));

            // 只监听模式（脚本「空档填缝」用它）：照常算音量、照常记事件（事件在音频线程里就已经记下了），
            // 但**绝不下发任何动作**——这段时间设备归脚本播放器管，声音这一路只是「耳朵」。
            if (_cfg.AudioListenOnly && !_cfg.AudioReactiveEnabled)
            {
                Volatile.Write(ref _envelope, ComputeEnvelope());
                Volatile.Write(ref _blocked, 0);
                if (_motionActive) { _motionActive = false; Volatile.Write(ref _motionActiveFlag, 0); }
                // 不驱动就没有「待播事件」这回事；检测器里「最近一次事件」的状态不受影响（填缝读的是它）。
                DiscardEventQueues();
                return;
            }

            if (!_cfg.AudioReactiveEnabled || _engine.RuleEngineActive)
            {
                // 规则引擎接管时音频不驱动设备，攒下的事件再放出来只会「落后于画面」。
                if (_engine.RuleEngineActive) DiscardEventQueues();
                return;
            }

            double envelope = ComputeEnvelope();
            Volatile.Write(ref _envelope, envelope);

            // ① 有声音但下不去（被游戏伴随 / 游戏桥接管，或急停）：只显示原因，不动设备。
            if (!_engine.CanAcceptDirectInput || !_engine.TryClaimDirectInput("audio"))
            {
                Volatile.Write(ref _blocked, 1);
                if (_motionActive) { _motionActive = false; Volatile.Write(ref _motionActiveFlag, 0); }
                CancelActiveEvent();
                DiscardEventQueues();
                return;
            }

            // ①-b 拿到控制权之后再确认一次：**是不是有别人正在写同一批轴**。
            //     ① 只拦得住「别人先声明」的情况；本服务先拿到 "audio" 之后，脚本播放器那边
            //     force:false 的声明会被拒，但它照样逐帧下发 —— 两边交替覆盖同一根轴，设备就乱抖。
            //     有人在写，本服务就不写、也不去抢（原因见 OthersAreWriting 与 DescribeBlocked）。
            if (OthersAreWriting())
            {
                Volatile.Write(ref _blocked, 1);
                if (_motionActive) { _motionActive = false; Volatile.Write(ref _motionActiveFlag, 0); }
                CancelActiveEvent();
                DiscardEventQueues();
                return;
            }
            Volatile.Write(ref _blocked, 0);

            // ② 人声模式（呻吟 = 抽插）：这条路自己管「起音 → 抽插 → 余韵 → 停」。
            //    它**故意忽略**「事件化动作」那个勾选 —— 那个勾选是音乐口径（一次事件 = 一次短期动作），
            //    而人声要的是连续抽插；勾掉它不该让人声响应跟着失效。
            //    事件队列在这里也用不上（一次呻吟的形状由 ApplyVoiceThrustTick 自己算），照常清掉，
            //    免得队列攒满之后在停止采集时冒出一条"事件队列处理不过来"的假告警。
            if (IsVoiceMode())
            {
                DiscardEventQueues();
                ApplyVoiceThrustTick(envelope);
                return;
            }

            // ③ 事件化路径（默认）：**先处理事件，再判静音**。
            //    冲击、枪声这类事件本来就是"响一下就没了"——如果先判静音，等轮到这一 tick 时
            //    音量早就掉回噪声底，刚检出的事件会被当成"静音期间的事件"丢掉，等于白检测。
            //    所以这里先排空事件队列并渲染，再决定要不要因为长时间安静而交还控制权。
            if (_cfg.AudioEventMotion)
            {
                ApplyEventTick();

                if (_active)
                {
                    _silenceMs = 0;          // 事件还在演，静音计时从事件结束算起
                }
                else if (envelope <= 0.002)
                {
                    _silenceMs += ApplyIntervalMs;
                    if (_motionActive && _silenceMs >= SilenceStopMs) StopAudioMotion();
                }
                else
                {
                    _silenceMs = 0;          // 有声音（只是暂时没有事件）：保持控制权，设备静止
                }
                return;
            }

            // ④ 老的音量循环路径：安静一小会儿（避免句子之间的停顿就把动作切断）后缓慢回中并停发。
            if (envelope <= 0.002)
            {
                _silenceMs += ApplyIntervalMs;
                if (_motionActive && _silenceMs >= SilenceStopMs) StopAudioMotion();
                CancelActiveEvent();
                DiscardEventQueues();
                return;
            }

            _silenceMs = 0;
            ApplyVolumeTick(envelope);
        }
        catch (Exception ex)
        {
            AppLogger.Error("音频响应应用异常", ex);
        }
    }

    // ── 事件化动作（P1）：动作由「刚检出的事件」触发 ─────────────────────
    //
    // 与音量路径的根本区别：这里不会因为「有声音」就自己造动作。
    //   有事件 → 按动作语汇渲染这次事件（冲击快而深 / 渐强慢而连绵 / 节拍短而轻）；
    //   没事件 → 事件渲染完用 0.35s 缓动回中并停发，设备静止等下一个事件；
    //   真安静 → 沿用老的静音逻辑（0.7s）缓慢回中并交出控制权（上面 ① 已处理）。
    // 「有声音但没事件」和「安静」的差别不在动作，而在**控制权**：前者仍然握着 direct input
    // 随时准备响应，只有静音才交出去（否则音乐持续期间会被别的模块抢走）；设备本身保持静止 ——
    // 一直轻微动是背景噪音，静止本身才更沉浸。
    /// <summary>拍点提前量（秒）：拍点到来前这么久就把动作排上去，让设备正好踩在拍上。</summary>
    private const double BeatLeadSeconds = 0.06;

    private long _lastPredictedBeatTicks;   // 上一次"按预测拍点发动作"的时刻
    private int _predictedBeatCount;        // 连续数到第几拍（用来做小节重音；失锁即清零）

    /// <summary>
    /// 拍点预测下发：tempo 锁上之后，不再等 onset 检测（那样要晚 50–100ms 才动），
    /// 而是在拍点到来前 <see cref="BeatLeadSeconds"/> 秒就把一次「节拍」动作排上去。
    /// 这就是「跟着音乐演奏」和「跟着音量抽动」的分界线。
    /// 没锁上（没鼓点 / 刚开播）时什么都不做 —— 行为与以前完全一样，不会更差。
    /// </summary>
    private void TryFirePredictedBeat()
    {
        TempoTracker? tempo = _flux.Tempo;
        if (tempo is null || !tempo.Locked) { _predictedBeatCount = 0; return; }   // 失锁就把小节重新数过
        if (tempo.SecondsToNextBeat > BeatLeadSeconds) return;              // 离下一拍还早
        if (!_engine.CanAcceptDirectInput) return;                          // 急停/归中/被接管时别抢

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastPredictedBeatTicks != 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(_lastPredictedBeatTicks, now).TotalSeconds
               < tempo.BeatPeriodSeconds * 0.5)
            return;                                                          // 同一个拍点只发一次

        _lastPredictedBeatTicks = now;

        // 小节重音：4/4 里每 4 拍来一次"重的"。
        // 全是同样轻的拍点 = 机械；有了轻重对比，听着才像在演奏而不是在抽。
        // 重音用「冲击」语汇——它比「节拍」长一点、而且自带扭转，副轴会跟着动，
        // 所以这一条同时把"副轴跟着走"也带上了。
        _predictedBeatCount++;
        bool strongBeat = _predictedBeatCount % 4 == 1;
        double baseStrength = Math.Clamp(_flux.LastOnsetStrength, 0.30, 1.0);

        MotionEventKind kind = strongBeat ? MotionEventKind.Impact : MotionEventKind.Beat;
        double strength = strongBeat
            ? Math.Clamp(baseStrength, 0.55, 1.0)          // 重音：不许太轻
            : Math.Clamp(baseStrength * 0.6, 0.30, 0.75);  // 弱拍：压下去，对比才出得来

        ActivateEvent(new MotionEvent(kind, strength, 0), 0, predicted: true);
    }

    private void ApplyEventTick()
    {
        double now = NowSeconds();
        int calibrationMs = Math.Clamp(_engine.MotionCalibrationMs, -200, 200);
        double calibrationSeconds = calibrationMs / 1000.0;

        // ① 音频线程检出的事件 → 按标定值分流：正 = 排进到点队列（设备晚点动），
        //    负 = 立刻执行（设备该早点动，补不回的时间记在相位上，见 ActivateEvent）。
        bool activated = false;
        while (true)
        {
            int read = _pendingRead;
            if (read >= Volatile.Read(ref _pendingWrite)) break;
            MotionEvent ev = _pendingEvents[read & PendingEventMask];
            _pendingRead = read + 1;
            if (calibrationSeconds > 0) HoldEvent(ev, ev.AtSeconds + calibrationSeconds);
            else { ActivateEvent(ev, -calibrationSeconds); activated = true; }
        }

        // ② 到点的延迟事件开始渲染（单调时钟判定，不受系统时间调整影响）。
        for (int i = 0; i < _heldCount; )
        {
            if (_heldDueSeconds[i] > now) { i++; continue; }
            MotionEvent ev = _heldEvents[i];
            RemoveHeldEvent(i);
            ActivateEvent(ev, 0);
            activated = true;
        }

        if (!_active)
        {
            // 趁没有事件在渲染，先按预测的拍点排一次节拍动作（有事件在渲染就不打断它）。
            TryFirePredictedBeat();
            if (!_active) return;
        }

        // ③ 推进相位并下发。本 tick 刚激活的事件从相位 0 开始（保住冲击的 attack）——
        //    20Hz 的轮询延迟不该吃掉动作的起手式。
        //    同一时刻只渲染一个事件：更新的那个直接接管（冲击不该等一个没做完的渐强）。
        if (activated) _activeElapsed = 0;
        else _activeElapsed += ApplyIntervalMs / 1000.0;

        double duration = Math.Max(0.05, _activeDuration);
        double phase = _activeElapsed / duration + _activePhaseOffset;
        if (phase >= 1.0) { FinishActiveEvent(); return; }

        double comfort = Math.Clamp(_engine.ActiveComfortProfile.MaxIntensity, 0.3, 1.0);
        double range = Math.Clamp(_cfg.AudioMotionRange, 5, 60) * comfort;
        double strength = Math.Clamp(_activeStrength * SensitivityGain(), 0, 1);
        double[] values = MotionVocabulary.Render(_activeKind, strength, phase, range, _engine.MultiAxisMotion);
        if (_engine.TrySendDirectAxes(values, Math.Max(0.02, duration * EventInterpolationRatio)))
        {
            _motionActive = true;
            Volatile.Write(ref _motionActiveFlag, 1);
        }
    }

    /// <summary>
    /// 开始渲染一个事件。<paramref name="leadSeconds"/> &gt; 0 = 负向标定（设备该早动）：
    /// 没法穿越回过去，就把相位起点往后挪同样的时间 —— 等于「假装它已经开始了这么久」。
    /// 挪动量封顶 <see cref="MaxPhaseOffset"/>（半个事件），保证前半段被吞掉后仍有可见动作。
    /// </summary>
    private void ActivateEvent(in MotionEvent ev, double leadSeconds, bool predicted = false)
    {
        // 同一拍如果已经被"拍点预测"发过了，这个 onset 的 Beat 就丢掉：
        // 否则一拍两次起手（观感是抖一下而不是干净的一下），界面上的"已响应 N 次"也会翻倍。
        if (!predicted && ev.Kind == MotionEventKind.Beat && _lastPredictedBeatTicks != 0)
        {
            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastPredictedBeatTicks, nowTicks).TotalSeconds < 0.25) return;
        }
        double duration = MotionVocabulary.DurationSeconds(ev.Kind, ev.Strength);
        if (duration <= 0) return;                       // None 之类没有时长的语汇直接忽略
        _activeKind = ev.Kind;
        _activeStrength = Math.Clamp(ev.Strength, 0, 1);
        _activeDuration = duration;
        _activePhaseOffset = leadSeconds > 0 ? Math.Clamp(leadSeconds / duration, 0, MaxPhaseOffset) : 0;
        _activeElapsed = 0;
        _active = true;
        Interlocked.Increment(ref _respondedCount);      // 界面「已响应 N 次」
    }

    /// <summary>事件渲染结束：一次较长的缓动回中后停发 —— 不发明没有事件支撑的动作。</summary>
    private void FinishActiveEvent()
    {
        _active = false;
        _activeElapsed = 0;
        _activePhaseOffset = 0;
        // 回中后不再发指令（引擎 changedOnly 会去重），设备静止等下一个事件；
        // 控制权仍然握在手上，直到上面的静音分支把控制权交出去。
        if (_engine.CanAcceptDirectInput) _engine.TrySendDirectAxes(CenterPose, EventSettleSeconds);
    }

    /// <summary>正标定：把事件排进「到点队列」，到点再渲染（设备晚一点动）。</summary>
    private void HoldEvent(in MotionEvent ev, double dueSeconds)
    {
        // 队列满 = 标定值把事件压住了：丢最老的保最新的，新事件更可能是用户此刻看到的那一下。
        if (_heldCount >= HeldEventCapacity) RemoveHeldEvent(0);
        _heldEvents[_heldCount] = ev;
        _heldDueSeconds[_heldCount] = dueSeconds;
        _heldCount++;
    }

    private void RemoveHeldEvent(int index)
    {
        for (int i = index; i < _heldCount - 1; i++)
        {
            _heldEvents[i] = _heldEvents[i + 1];
            _heldDueSeconds[i] = _heldDueSeconds[i + 1];
        }
        _heldCount--;
    }

    /// <summary>丢掉还没渲染的事件（被抢占 / 静音 / 停止采集）：这些事件已经没有「当下」可言。</summary>
    private void DiscardEventQueues()
    {
        _pendingRead = Volatile.Read(ref _pendingWrite);   // 读指针追平写指针 = 全部丢弃
        _heldCount = 0;
    }

    /// <summary>
    /// 灵敏度在事件模式下只做 0.75–1.25 的小幅缩放（默认 1.0 → 正好 1.0 倍）：
    /// 判定的 dB 阈值是固定的，灵敏度不该改变「什么算事件」（否则同一段素材换个灵敏度就多报/漏报），
    /// 只该改变动作幅度。
    /// </summary>
    private double SensitivityGain() =>
        SensitivityGainMin + (SensitivityGainMax - SensitivityGainMin) * Math.Clamp(_cfg.AudioSensitivity, 0, 2) / 2.0;

    /// <summary>旧路径（AudioEventMotion = false）：动作跟着音量循环 —— 越响行程越大、来回越快。</summary>
    private void ApplyVolumeTick(double envelope)
    {
        // 旧路径的行为一字未改：越响 → 行程越大、来回越快；插值留长一点，动作才顺。
        double comfort = Math.Clamp(_engine.ActiveComfortProfile.MaxIntensity, 0.3, 1.0);
        double range = Math.Clamp(_cfg.AudioMotionRange, 5, 60) * comfort;
        double amplitude = range * envelope;
        double strokesPerSecond = 0.45 + 1.75 * envelope;      // 0.45–2.2 次/秒
        _phase += Math.Tau * strokesPerSecond * (ApplyIntervalMs / 1000.0);
        if (_phase > Math.Tau * 1000) _phase -= Math.Tau * 1000;

        double main = 50 + amplitude * Math.Sin(_phase);
        double[] values = [main, 50, 50, 50, 50, 50];
        if (_engine.TrySendDirectAxes(values, ApplyIntervalMs / 1000.0 * 1.6))
        {
            _motionActive = true;
            Volatile.Write(ref _motionActiveFlag, 1);
        }
    }

    /// <summary>
    /// 人声模式：一次呻吟 = 一次抽插（插进去、抽出来）。
    ///
    /// 深度 = 行程 × 舒适档 × 人声强度（<paramref name="envelope"/>；安静时正好 0 → 幅度 0 → 停在"抽出来"的位置）
    /// 速度 = 她自己的节奏：最近 2 秒人声包络起伏的疏密（次/秒，夹在 0.5–2.2），数不出节奏时用 0.6 + 1.2 × 强度
    /// 起音 = 一收到新的 Voice 事件就把相位归零，立刻往里推（0.45 个周期插到底）
    /// 余韵 = 人声掉回门限后再抽 1.5 秒、幅度一次比一次浅，然后缓慢回中并停发
    ///
    /// 形态直接复用 MotionVocabulary 的 Voice 语汇：设备在「事件化动作」那条路上看到的
    /// 「一次呻吟怎么动」和这里**是同一个形状**，不会出现"两种模式两套手感"。
    /// </summary>
    private void ApplyVoiceThrustTick(double envelope)
    {
        // ① 起音：检测器刚报了新的 Voice 事件 → 相位归零。
        //    归零不是"瞬间跳到底"，而是"从现在开始往里推"：0.45 个周期内插到底，
        //    这样呻吟一起来设备就跟着那一下走，而不是接在上一次的半路上。
        double at = Volatile.Read(ref _lastVoiceEventAt);
        if (at > _voiceConsumedAt)
        {
            _voiceConsumedAt = at;
            _voicePhase = 0;
            Interlocked.Increment(ref _respondedCount);      // 界面「已响应 N 次」= 插了多少下
        }

        // ② 强度与余韵。余韵拿"她刚刚有多响"（_voiceHoldLevel）打底再乘随时间递减的 tail ——
        //    这样"停"不是突然断掉，而是一下比一下浅地停下来。
        double level;
        if (envelope > 0.002)
        {
            _voiceHoldLevel = envelope;
            _voiceAfterglowMs = VoiceAfterglowMs;            // 还在响：余韵窗口一直是满的
            _silenceMs = 0;
            level = envelope;
        }
        else
        {
            _voiceAfterglowMs -= ApplyIntervalMs;
            if (_voiceAfterglowMs <= 0)
            {
                _voiceAfterglowMs = 0;
                _voiceHoldLevel = 0;
                if (_motionActive) StopAudioMotion();        // 缓慢回中 + 停发（和另外两条路径同一个收尾）
                CancelActiveEvent();
                DiscardEventQueues();
                return;
            }
            level = _voiceHoldLevel * Math.Clamp(_voiceAfterglowMs / VoiceAfterglowMs, 0, 1);
        }

        // ③ 速度 = 她的节奏。VoicePulseRate 是检测器在音频线程上按 100Hz 数出来的
        //    （人声包络每鼓一次算一次起伏）；数不出来就用 0.6 + 1.2 × 强度兜底，再夹进 0.5–2.2。
        double measured = _detector.VoicePulseRate;
        double target = measured > 0 ? measured : VoiceFallbackBase + VoiceFallbackGain * level;
        target = Math.Clamp(target, VoiceMinRate, VoiceMaxRate);
        _voiceRate = _voiceRate <= 0 ? target : _voiceRate + (target - _voiceRate) * VoiceRateSmoothing;

        _voicePhase += _voiceRate * (ApplyIntervalMs / 1000.0);   // 相位按"次"算：0→1 是一次完整抽插
        if (_voicePhase > 1000) _voicePhase -= 1000;              // 防溢出（同 ApplyVolumeTick 的做法）
        double phase = _voicePhase - Math.Floor(_voicePhase);

        // ④ 深度：行程 × 舒适档 × 强度，形态交给 Voice 语汇（次要轴只做轻微跟随）。
        double comfort = Math.Clamp(_engine.ActiveComfortProfile.MaxIntensity, 0.3, 1.0);
        double range = Math.Clamp(_cfg.AudioMotionRange, 5, 60) * comfort;
        double strength = Math.Clamp(level, 0, 1);
        double[] values = MotionVocabulary.Render(MotionEventKind.Voice, strength, phase, range, _engine.MultiAxisMotion);
        if (_engine.TrySendDirectAxes(values, ApplyIntervalMs / 1000.0 * 1.6))
        {
            _motionActive = true;
            Volatile.Write(ref _motionActiveFlag, 1);
        }
    }

    /// <summary>把当前音量换算成 0–1 的「有效响度」：低于门限算 0，噪声底不会被当成声音。</summary>
    private double ComputeEnvelope()
    {
        double gate = Math.Clamp(_cfg.AudioNoiseGate, 0, 0.5);
        double sensitivity = Math.Clamp(_cfg.AudioSensitivity, 0, 2);
        // 灵敏度同时作用于触发门限与增益：高灵敏度更易触发，低灵敏度更迟钝。
        double effectiveGate = gate * Math.Clamp(1.35 - 0.35 * sensitivity, 0, 1.5);
        double energy = _energy;
        if (energy <= effectiveGate) return 0;
        return Math.Clamp((energy - effectiveGate) / Math.Max(0.01, 1 - effectiveGate), 0, 1);
    }

    /// <summary>静音超时：缓慢回到中位（不是突然跳），然后不再发送任何指令。</summary>
    private void StopAudioMotion()
    {
        _motionActive = false;
        Volatile.Write(ref _motionActiveFlag, 0);
        _phase = 0;
        _voicePhase = 0;          // 人声模式同理：下一次从"完全抽出来"重新起步
        if (_engine.CanAcceptDirectInput)
            _engine.TrySendDirectAxes([50, 50, 50, 50, 50, 50], 0.45);
    }

    private static double NormalizeDb(double amplitude, double floorDb)
    {
        double db = 20 * Math.Log10(Math.Max(amplitude, 1e-7));
        return Math.Clamp((db - floorDb) / -floorDb, 0, 1);
    }

    private static double LowPassAlpha(int sampleRate)
    {
        const double cutoff = 150.0;
        double rc = 1.0 / (2 * Math.PI * cutoff);
        double dt = 1.0 / Math.Max(1, sampleRate);
        return dt / (dt + rc);
    }

    private static double Smooth(double current, double sample, bool rising)
    {
        double factor = rising ? 0.42 : 0.07;
        return current + factor * (sample - current);
    }

    private static double ReadSample(ReadOnlySpan<byte> buffer, int offset, int bytesPerSample, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.Extensible);
        if (isFloat && bytesPerSample == 4)
            return Math.Clamp(BitConverter.ToSingle(buffer.Slice(offset, 4)), -1, 1);
        if (bytesPerSample == 2)
            return BitConverter.ToInt16(buffer.Slice(offset, 2)) / 32768.0;
        if (bytesPerSample == 4)
            return BitConverter.ToInt32(buffer.Slice(offset, 4)) / 2147483648.0;
        return 0;
    }

    public void Dispose()
    {
        _disposed = true;
        StopCapture();
        _applyTimer?.Dispose();
        _applyTimer = null;
        _engine.SetAudioFeatures(0, 0, 0);
    }
}
