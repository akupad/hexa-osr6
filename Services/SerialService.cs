using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace Hexa.Services;

public sealed class SerialService : ICommandTransport, IDisposable
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromMilliseconds(1600);

    private readonly Lock _lock = new();

    /// <summary>
    /// 串行化"发命令 + 等响应"这类长操作（例如设备自检，最长 1.2 秒），**不占用 <see cref="_lock"/>**：
    /// 急停、锁定输出、断连都要 _lock，拿着它睡会把急停堵住（界面冻住、设备还在动）。
    /// </summary>
    private readonly Lock _ioLock = new();

    /// <summary>
    /// 串口写闸门。SerialPort.Write 不是线程安全的：运动帧（30–60Hz）、脚本、游戏桥、固件命令
    /// 分属不同线程，同时写会把一行劈开，或者一起卡在驱动内部信号量上，500ms 后抛「信号灯超时」
    /// （日志里这种超时出现过 300 多次，每次都连带锁输出、重连、停两三秒）。
    /// 只管写动作本身，绝不与 <see cref="_lock"/> 嵌套，所以急停照样快。
    /// </summary>
    private static readonly object _writeGate = new();

    /// <summary>
    /// 最近几块原始串口输出（读线程单写者，不加密）。
    /// 只为一件事：板子重启或掉链时把复位原因行留下来 —— ESP32 开机时会打
    /// ESP-ROM / rst:0x… / Brownout detector / Guru Meditation，没有这些行，
    /// 主机侧永远只能证明「banner 出现了」，查不出为什么重启。
    /// </summary>
    private readonly Queue<string> _recentRxChunks = new();

    /// <summary>
    /// 板子重启后静默发帧的截止时刻（TickCount64）。重启那几秒写进去的帧不但无效，
    /// 还会把驱动内部信号量堵住 500ms → 抛「信号灯超时」→ 锁输出 + 重连，
    /// 用户感觉就是「卡住，然后又好，又卡住」。
    /// </summary>
    private long _quietUntilMs;

    /// <summary>
    /// 重启后"重新接手串口"是否已有人在跑（0/1）。读线程只负责举手，重活交给后台任务：
    /// 板子重启后原来那个端口句柄经常还"能写"——写进驱动缓冲就返回成功，于是主机盲发十几秒
    /// 才发现设备根本没在听（日志里最长的一次盲发了 27 秒）。既然横幅已经把"它刚重启"写在脸上，
    /// 就趁静默期把端口重开一遍，把这段盲发压到两秒内。
    /// </summary>
    private int _rebootRecoveryPending;

    /// <summary>重启后等板子重新枚举 CDC 再接手端口的延迟。</summary>
    private const int RebootReattachDelayMs = 800;

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly System.Timers.Timer _reconnectTimer;

    private SerialPort? _port;
    private Task<SerialPort?>? _pendingOpenTask;
    private string _primaryPortName = "";
    private string _fallbackPortName = "";
    private string _activePortName = "";
    private bool _outputEnabled;
    private bool _simulationEnabled;

    /// <summary>
    /// 镜像出口：每一条真正写出去的 TCode（含 DSTOP）都会再交给它一份。
    /// 用途＝「同时发给网络目标」（第二台设备 / VAM 里的 BusDriver / 别的软件）；
    /// 模拟设备下也照样镜像，所以没插设备也能验证多输出。调用点都在锁外，网络卡住不会堵住设备输出。
    /// </summary>
    public Action<string>? Mirror { get; set; }
    private readonly List<string> _simulationCommands = new();
    private bool _reconnectEnabled;
    private bool _autoDiscoveryEnabled;
    private bool _preferWired = true;
    private readonly int[] _lastSentRaw = new int[6];
    private readonly bool[] _lastSentValid = new bool[6];
    private readonly int[] _lastWrittenRaw = new int[6];
    private bool _hasWrittenFrame;

    /// <summary>
    /// 单帧允许的最大位移（TCode 单位）。旧固件（c90bc29）每帧 500 连跑 90 秒没事、1000 七秒就崩，所以当初卡 400；
    /// 2026-11 的新固件上重测：800 / 1200 / 2000 各跑 40 秒、549 帧全部零 panic，于是放到 2000
    /// （＝50ms 内走 20% 行程，合 400%/秒），刚好够游戏按节拍跑满而不被削平。
    /// 它只是「别把固件写崩」的保底，不是舒适度限制。
    /// </summary>
    public const int MaxStepPerFrame = 2000;
    private readonly int[] _lastSentInterval = new int[6];
    private long _lastComposeLogMs;
    /// <summary>固件卡死（看门狗刷屏、心跳停）第一次被发现的时刻；0 = 没卡。</summary>
    private long _wedgedSinceMs;
    private long _nextHardResetMs;
    /// <summary>硬复位冷却：一次不成就别每轮都拉线，否则会把刚起来的板子又按下去。</summary>
    private const int HardResetCooldownMs = 30000;
    // 帧节拍器：真机固件只有在「固定节拍 + I 恒等于节拍」下才稳（25 Hz / I40 连跑 5 分钟不崩；
    // 换成抖动间距几十秒到两分钟必 panic）。所以上层只管把期望位置放进帧状态，
    // 真正写串口由这个节拍器按 40ms 一拍统一发——快慢不再受来源数量影响。
    public const int FrameIntervalMs = 50;
    // 节拍诊断：真机对「实际发送间隔」敏感（固件按 I 参数排插值窗口），
    // 每 200 帧记一次实测间隔的 min/max/avg，出问题直接看日志就知道节拍歪没歪。
    private long _pacerLastWriteMs;
    private int _pacerGapMin = int.MaxValue;
    private int _pacerGapMax;
    private long _pacerGapSum;
    private int _pacerGapCount;
    /// <summary>启动以来累计写成功帧数（只做诊断，节拍日志里带出来，便于核实"真的在发"）。</summary>
    private int _pacerTotalFrames;
    /// <summary>最近写出的帧（诊断：崩了以后要知道最后发的是什么，才能拿去离线复现）。</summary>
    private readonly Queue<string> _txRing = new();

    private Thread? _pacerThread;
    private int _consecutiveFailures;
    /// <summary>输出锁定期间被丢弃的指令数（用于日志与诊断）。</summary>
    private int _droppedWhileLocked;
    private int _droppedFramesWhileLocked;   // 与 _droppedWhileLocked 分开计数：帧路径是所有连续运动的必经之路

    private DateTime _nextReconnectUtc = DateTime.MinValue;
    private long _connectionGeneration;
    private bool _disposed;

    // ---- 链路存活 + 黑匣子（见 SerialLinkMonitor / BlackBoxRecorder 的类注释）----

    /// <summary>串口读线程：端口打开到关闭之间常驻，读到的东西只喂监控、不进业务。</summary>
    private Thread? _readThread;              // 线程本身只在 _lock 与 _ioLock 之外启动/停止
    private CancellationTokenSource? _readCts = new();

    /// <summary>
    /// 链路判定器。<see cref="Feed"/> 只有读线程调（单写者），界面线程调 <see cref="SerialLinkMonitor.Snapshot"/> 是安全的。
    /// </summary>
    public SerialLinkMonitor LinkMonitor { get; } = new();

    /// <summary>黑匣子（最近 800 条 / 约一分钟的现场）。界面「导出故障包」会把它落盘。</summary>
    public BlackBoxRecorder BlackBox { get; } = new();

    public bool IsOpen
    {
        get { lock (_lock) return _simulationEnabled || _port?.IsOpen == true; }
    }

    public bool OutputEnabled
    {
        get { lock (_lock) return _outputEnabled; }
        set
        {
            lock (_lock)
            {
                bool enabled = value && (_simulationEnabled || _port?.IsOpen == true);
                if (enabled && !_outputEnabled) ResetSentAxesLocked();
                _outputEnabled = enabled;
            }
        }
    }

    /// <summary>
    /// 输出被锁期间丢掉的指令条数。悬浮窗「为什么不动」直接读它：
    /// 锁着的时候发什么都没用，得说清「丢了几条、怎么解锁」。
    /// </summary>
    public int DroppedWhileLocked
    {
        get { lock (_lock) return _droppedWhileLocked; }
    }

    /// <summary>串口卡死时给用户的处置建议（详见 ICommandTransport.UserAdvice 的说明）。</summary>
    public string? UserAdvice
    {
        get
        {
            lock (_lock)
            {
                if (_port?.IsOpen == true || _consecutiveFailures < 3) return null;
                return "串口卡死：拔插一次 USB，再点侧栏「全部归中」解锁";
            }
        }
    }

    public string PortName
    {
        get { lock (_lock) return _activePortName; }
    }

    public bool IsSimulation
    {
        get { lock (_lock) return _simulationEnabled; }
    }

    public IReadOnlyList<string> SimulationCommands
    {
        get { lock (_lock) return _simulationCommands.ToArray(); }
    }

    /// <summary>模拟设备下记录一条指令。四处入口共用，上限 2000 条，超出丢最旧的一条。</summary>
    private void RecordSimulationCommandLocked(string command)
    {
        _simulationCommands.Add(command);
        if (_simulationCommands.Count > 2000) _simulationCommands.RemoveAt(0);
    }

    /// <summary>Enable a deterministic local device. It never opens a hardware port.</summary>
    public void SetSimulationMode(bool enabled)
    {
        bool changed;
        lock (_lock)
        {
            changed = _simulationEnabled != enabled;
            if (!changed) return;
            _connectionGeneration++;
            _simulationEnabled = enabled;
            _reconnectEnabled = !enabled;
            _outputEnabled = false;
            if (enabled)
            {
                ClosePortLocked();
                _activePortName = "SIMULATOR";
                _simulationCommands.Clear();
                ResetSentAxesLocked();
            }
            else
            {
                _activePortName = "";
            }
            // 换设备形态后，上一条连接错误已经不描述当前状态了，清掉免得面板一直挂着旧错误。
            LastError = null;
        }
        ConnectionChanged?.Invoke(enabled);
    }

    public string PrimaryPortName
    {
        get { lock (_lock) return _primaryPortName; }
    }

    public string FallbackPortName
    {
        get { lock (_lock) return _fallbackPortName; }
    }

    public string? LastError { get; private set; }
    public event Action<bool>? ConnectionChanged;

    public void EnableAutoDiscovery(bool preferWired)
    {
        lock (_lock)
        {
            _connectionGeneration++;
            _preferWired = preferWired;
            _autoDiscoveryEnabled = true;
            _reconnectEnabled = true;
            _nextReconnectUtc = DateTime.MinValue;
        }
    }

    public void DisableAutoDiscovery()
    {
        lock (_lock)
        {
            _connectionGeneration++;
            _autoDiscoveryEnabled = false;
        }
    }

    public SerialService()
    {
        _reconnectTimer = new System.Timers.Timer(1000) { AutoReset = true };
        // async void 必须自己兜异常：退出时 Dispatcher 已关闭，回调里 Invoke 抛出的异常会直接崩溃进程。
        _reconnectTimer.Elapsed += async (_, _) =>
        {
            try { await TryReconnectAsync().ConfigureAwait(false); }
            catch (Exception ex) { AppLogger.Error("串口重连异常（已忽略）", ex); }
        };
        _reconnectTimer.Start();
        // 节拍器必须用独立线程 + 单调时钟排程：System.Threading.Timer 在回调被锁或写阻塞时
        // 会排队补发（实测 min=0ms / max=5000ms 的乱节拍），而真机固件对乱节拍零容忍。
        _pacerThread = new Thread(PacerLoop) { IsBackground = true, Name = "HexaPacer", Priority = ThreadPriority.AboveNormal };
        _pacerThread.Start();
    }

    public async Task<bool> ConnectAsync(string primaryPortName, string? fallbackPortName = null)
    {
        string primary = NormalizePort(primaryPortName);
        string fallback = NormalizePort(fallbackPortName);
        if (primary.Length == 0)
        {
            LastError = "主端口为空";
            ConnectionChanged?.Invoke(false);
            return false;
        }

        lock (_lock)
        {
            if (_simulationEnabled)
            {
                LastError = null;
                _outputEnabled = false;
                return true;
            }
        }

        lock (_lock)
        {
            _connectionGeneration++;
            _primaryPortName = primary;
            _fallbackPortName = string.Equals(primary, fallback, StringComparison.OrdinalIgnoreCase) ? "" : fallback;
            _reconnectEnabled = true;
            _nextReconnectUtc = DateTime.MinValue;
        }

        return await TryConnectCoreAsync(notify: true).ConfigureAwait(false);
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _connectionGeneration++;
            _reconnectEnabled = false;
            _autoDiscoveryEnabled = false;
            _outputEnabled = false;
            _simulationEnabled = false;
            ClosePortLocked();
            // 用户主动断开：不是错误，别让面板继续显示上一次的失败原因。
            LastError = null;
        }
        BlackBox.Record("state", "用户断开连接");
        ConnectionChanged?.Invoke(false);
    }

    private async Task<bool> TryConnectCoreAsync(bool notify)
    {
        lock (_lock)
        {
            if (_disposed) return false;
        }
        if (!await _connectGate.WaitAsync(0).ConfigureAwait(false))
            return IsOpen;

        bool ok;
        string? error;
        long generation;
        try
        {
            lock (_lock) generation = _connectionGeneration;
            (ok, error) = await OpenCandidatesAsync(generation).ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed || generation != _connectionGeneration) return false;
                if (ok)
                {
                    _consecutiveFailures = 0;
                    _nextReconnectUtc = DateTime.MinValue;
                }
                else
                {
                    _consecutiveFailures++;
                    _nextReconnectUtc = DateTime.UtcNow.AddSeconds(BackoffSeconds());
                }
            }
        }
        finally
        {
            _connectGate.Release();
        }

        LastError = ok ? null : EnrichPortError(error);
        if (notify) ConnectionChanged?.Invoke(ok);
        return ok;
    }

    /// <summary>已知会独占串口的常见程序（用户机器上装过的看片/桥工具）。</summary>
    private static readonly string[] CompetingProcessNames =
        ["MultiFunPlayer", "intiface_central", "intiface-central", "IntifaceCentral", "IntifaceEngine", "osr6_bridge"];

    /// <summary>检测当前正在运行、可能占用串口的程序。</summary>
    public static IReadOnlyList<string> DetectCompetingApps()
    {
        var found = new List<string>();
        try
        {
            // 一次枚举所有进程再过滤：比每个名字各做一次全量枚举快 6 倍（连接失败时会被反复调用）。
            var wanted = new HashSet<string>(CompetingProcessNames, StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (wanted.Contains(process.ProcessName) && !found.Contains(process.ProcessName))
                            found.Add(process.ProcessName);
                    }
                    catch
                    {
                        // 单个进程读不到名字（已退出/权限不足）时跳过
                    }
                }
            }
        }
        catch
        {
            // 进程枚举失败不影响连接流程
        }
        return found;
    }

    /// <summary>
    /// 结束一个已知的串口占用程序（仅限白名单，避免误杀任意进程）。
    /// 返回是否至少结束了一个进程。
    /// </summary>
    public static bool TryStopCompetingApp(string processName)
    {
        if (!CompetingProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)) return false;
        bool stopped = false;
        try
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                        stopped = true;
                        AppLogger.Info($"已结束占用串口的程序：{processName}（PID {process.Id}）");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"结束 {processName} 失败：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"结束 {processName} 时出错：{ex.Message}");
        }
        return stopped;
    }

    /// <summary>
    /// 连接失败时补充可执行的原因：串口被别的程序独占是最常见的情况
    /// （MultiFunPlayer / Intiface Central / 旧桥），只显示“连接失败”用户无从下手。
    /// </summary>
    private static string EnrichPortError(string? error)
    {
        string message = error ?? "连接失败";
        IReadOnlyList<string> competing = DetectCompetingApps();
        if (competing.Count > 0)
            return $"{message}｜检测到 {string.Join("、", competing)} 正在运行，可能占用串口，请先关闭后重试";
        if (message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return $"{message}｜端口被其他程序独占，请关闭占用它的程序（看片工具/桥/另一个 Hexa）后重试";
        return message;
    }

    /// <summary>常见的 USB 转串口芯片：乐鑫原生 USB、CH340、CP210x、FTDI。蓝牙口一律不算。</summary>
    private static readonly string[] KnownUsbBridgeVendors = ["VID_303A", "VID_1A86", "VID_10C4", "VID_0403"];

    /// <summary>这个口是真 USB 线插着的（而不是虚拟口/蓝牙口）。换固件后没身份信息时，只对这类口硬连。</summary>
    private static bool IsKnownUsbBridgeHardware(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return false;
        foreach (string vendor in KnownUsbBridgeVendors)
            if (hardwareId.Contains(vendor, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task<(bool ok, string? error)> OpenCandidatesAsync(long generation)
    {
        string primary;
        string fallback;
        bool autoDiscovery;
        bool preferWired;
        lock (_lock)
        {
            primary = _primaryPortName;
            fallback = _fallbackPortName;
            autoDiscovery = _autoDiscoveryEnabled;
            preferWired = _preferWired;
        }

        var candidates = new List<string> { primary, fallback };
        IReadOnlyList<SerialPortOption> discovered = [];
        if (autoDiscovery)
        {
            discovered = WithoutBluetoothWhenWired(
                RankPortOptions(GetAvailablePortOptions(), preferWired, primary, fallback), preferWired);
            candidates.AddRange(discovered.Select(option => option.PortName));
        }

        var ordered = candidates.Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        async Task<(bool Ok, string? Error)> AttemptAsync(List<string> names, bool requireIdentity)
        {
        string? lastError = null;
        foreach (string candidate in names)
        {
            lock (_lock)
            {
                if (_disposed || generation != _connectionGeneration)
                    return (false, "连接请求已取消");
                if (_port?.IsOpen == true && string.Equals(_activePortName, candidate, StringComparison.OrdinalIgnoreCase))
                    return (true, null);
            }

            var (opened, error) = await TryOpenPortAsync(candidate, generation, requireIdentity).ConfigureAwait(false);
            if (opened == null)
            {
                lastError = $"{candidate}: {error}";
                AppLogger.Warn($"串口连接失败: {lastError}");
                continue;
            }

            lock (_lock)
            {
                if (_disposed || generation != _connectionGeneration)
                {
                    opened.Dispose();
                    return (false, "连接请求已取消");
                }
                ClosePortLocked();
                _port = opened;
                _activePortName = candidate;
                if (!string.Equals(candidate, _primaryPortName, StringComparison.OrdinalIgnoreCase))
                {
                    _primaryPortName = candidate;
                    _fallbackPortName = discovered
                        .FirstOrDefault(option => !string.Equals(option.PortName, candidate, StringComparison.OrdinalIgnoreCase)
                            && option.IsKnownBluetoothDevice)
                        ?.PortName ?? _fallbackPortName;
                }
                _outputEnabled = false;
                ResetSentAxesLocked();
                StartReaderLocked(opened);   // 读线程与端口同生命周期：连接即开、关闭即停
            }
            AppLogger.Info($"串口已连接: {candidate}（输出保持锁定）");
            BlackBox.Record("state", $"已连接 {candidate}（输出保持锁定）");
            return (true, null);
        }

        return (false, lastError ?? "没有可用端口");
        }

        var firstPass = await AttemptAsync(ordered, requireIdentity: true).ConfigureAwait(false);
        if (firstPass.Ok) return firstPass;

        // 第二遍：只对「已知 USB 转串口」（CH340 / ESP32 原生 USB CDC）放宽身份要求。
        // 换固件后 D1 可能完全不回话，硬连总比连不上强；蓝牙口不放进来——它没配对也能打开，
        // 会白白占住 5 秒（真机实测过）。
        Dictionary<string, PortMetadata> metadata = ReadPortMetadata();
        var wiredOnly = ordered.Where(name =>
            metadata.TryGetValue(name, out PortMetadata info)
            && IsKnownUsbBridgeHardware(info.HardwareId)).ToList();
        if (wiredOnly.Count > 0 && wiredOnly.Count != ordered.Count)
        {
            var secondPass = await AttemptAsync(wiredOnly, requireIdentity: false).ConfigureAwait(false);
            if (secondPass.Ok)
            {
                AppLogger.Warn("这一版固件没回身份信息，已按已知 USB 设备硬连上。");
                return secondPass;
            }
        }
        return firstPass;
    }

    private async Task<(SerialPort? port, string? error)> TryOpenPortAsync(string name, long generation, bool requireIdentity = true)
    {
        Task<SerialPort?>? pending;
        lock (_lock)
        {
            pending = _pendingOpenTask;
            if (pending is { IsCompleted: false })
                return (null, "上一次打开操作仍未结束，已抑制重复尝试");
            _pendingOpenTask = null;
        }

        using var cts = new CancellationTokenSource(OpenTimeout);
        var openTask = Task.Run(() => OpenPort(name, cts.Token, requireIdentity), CancellationToken.None);
        lock (_lock) _pendingOpenTask = openTask;

        try
        {
            SerialPort? opened = await openTask.WaitAsync(cts.Token).ConfigureAwait(false);
            lock (_lock)
            {
                if (ReferenceEquals(_pendingOpenTask, openTask)) _pendingOpenTask = null;
                if (_disposed || generation != _connectionGeneration || _simulationEnabled)
                {
                    opened?.Dispose();
                    return (null, "连接请求已取消");
                }
            }
            return opened == null ? (null, "打开操作已取消") : (opened, null);
        }
        catch (OperationCanceledException)
        {
            _ = ObservePendingOpenAsync(openTask);
            return (null, $"打开超时（{OpenTimeout.TotalSeconds:0} 秒）");
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_pendingOpenTask, openTask)) _pendingOpenTask = null;
            }
            return (null, ex.Message);
        }
    }

    private async Task ObservePendingOpenAsync(Task<SerialPort?> task)
    {
        try
        {
            var port = await task.ConfigureAwait(false);
            port?.Dispose();
        }
        catch { }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_pendingOpenTask, task)) _pendingOpenTask = null;
            }
        }
    }

    private static SerialPort? OpenPort(string name, CancellationToken cancellationToken, bool requireIdentity = true)
    {
        var port = new SerialPort(name, 115200)
        {
            NewLine = "\n",
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = false,
            RtsEnable = false,
        };

        try
        {
            port.Open();
            if (!cancellationToken.IsCancellationRequested && ProbeTCodeIdentity(port, cancellationToken)) return port;
            if (cancellationToken.IsCancellationRequested)
            {
                port.Close();
                port.Dispose();
                return null;
            }
            // 换了固件以后 D1 可能一句都不回（2026-11 那版就是：1.5 秒只有日志）。
            // 已知的 USB 转串口设备仍然放行，按 TCode 硬连——连不上再让用户自己换口。
            if (!requireIdentity)
            {
                AppLogger.Warn($"{name}: 没回 TCode 身份，按已知 USB 设备硬连。");
                return port;
            }
            port.Close();
            port.Dispose();
            throw new IOException("端口没有返回 SR6 / TCode 身份信息");
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    private static bool ProbeTCodeIdentity(SerialPort port, CancellationToken cancellationToken)
    {
        try
        {
            port.DiscardInBuffer();
            // 只发 D1，绝不和 D0/D2 拼成一批：真机实测（COM3 / VID_303A&PID_1001）D0 与 D1 同批到达时
            // 固件不吐 D1 回话，1.6 秒窗口里一条 "TCode" 都收不到 ⇒ 身份判定失败、整台设备连不上；
            // 单独发 D1 立刻回 "TCode v0.3"。LF 与 CRLF 各发一遍，两种行尾固件都认。
            bool sentLf = TryWriteGated(port, "D1\n", 1000);
            bool sentCrlf = sentLf && TryWriteGated(port, "D1\r\n", 1000);
            if (!sentLf || !sentCrlf) AppLogger.Warn("串口写闸门被占，身份探测没能两条都发出去。");
            long startedAt = Stopwatch.GetTimestamp();
            var response = new System.Text.StringBuilder();
            while (!cancellationToken.IsCancellationRequested
                && Stopwatch.GetElapsedTime(startedAt) < IdentityTimeout)
            {
                if (port.BytesToRead > 0)
                {
                    response.Append(port.ReadExisting());
                    if (Osr6DeviceProfile.IsCompatibleIdentityResponse(response.ToString())) return true;
                }
                Thread.Sleep(20);
            }
            return Osr6DeviceProfile.IsCompatibleIdentityResponse(response.ToString());
        }
        catch
        {
            return false;
        }
    }

    private async Task TryReconnectAsync()
    {
        bool shouldReconnect;
        lock (_lock)
        {
            shouldReconnect = _reconnectEnabled
                && !_disposed
                && !_simulationEnabled
                && (_primaryPortName.Length > 0 || _autoDiscoveryEnabled)
                && _port?.IsOpen != true
                && DateTime.UtcNow >= _nextReconnectUtc
                && _pendingOpenTask is not { IsCompleted: false };
        }
        if (shouldReconnect)
            await TryConnectCoreAsync(notify: true).ConfigureAwait(false);
    }

    /// <summary>
    /// 查询设备身份与固件声明的轴表（D0/D1/D2），返回固件原始响应。
    /// 供设置页“设备自检”显示——注意固件声明 ≠ 实际安装的电机，需配合逐轴测试确认。
    /// </summary>
    public string QueryDeviceInfo(int timeoutMs = 1200)
    {
        // 这里**绝不能**持 _lock：整个查询要等 timeoutMs（默认 1.2 秒），而 _lock 是急停、输出锁定、
        // Send 判定共用的那把锁 —— 持着它睡 1.2 秒，等于"点一下读设备信息"期间急停指令排队发不出去
        //（审计把这条列为安全缺陷）。正确做法：锁内只取"能不能查"与端口引用，真正的写与读放锁外，
        // 用 _ioLock 保证串口不被两个操作同时占用（_ioLock 就是为这个场景准备的）。
        if (_simulationEnabled) return "SIMULATOR\n（模拟设备：无真实固件信息，逐轴测试仅记录指令）";
        SerialPort? port;
        lock (_lock)
        {
            port = _port;
            if (port?.IsOpen != true) return "";
        }

        lock (_ioLock)
        {
            try
            {
                port.DiscardInBuffer();
                // 逐条发、每条单独收：D0 与 D1 同批到达时固件会吞掉 D1 的回话（真机实测），
                // 所以 D1 先发，D0 隔一段再发，回话按 D1→D0 顺序拼回。
                // D2 不再发：2026-11 那版固件收到 D2 当场 panic（MCAUSE 0x2 / Illegal instruction → Rebooting）。
                var response = new System.Text.StringBuilder();
                foreach (string command in new[] { "D1\n", "D0\n" })
                {
                    if (!TryWriteGated(port, command, 1000)) AppLogger.Warn("串口写闸门被占，设备自检命令没发出去。");
                    long sliceStart = Stopwatch.GetTimestamp();
                    while (Stopwatch.GetElapsedTime(sliceStart).TotalMilliseconds < Math.Max(250, timeoutMs / 3.0))
                    {
                        if (port.BytesToRead > 0) response.Append(port.ReadExisting());
                        Thread.Sleep(20);
                    }
                }
                return response.ToString().Trim();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("设备信息查询失败: " + ex.Message);
                return "";
            }
        }
    }

    // ── 固件设置协议（TCodeESP32 的 # / $ 系统命令）──────────────────────────
    // 这是第二条串口通道，所以白名单是硬约束：只放行「列设置 / 改设置 / 保存 / 重启」。
    // 会动设备的命令一律不放 —— #device-home（回原点）、#motion-enable/disable/toggle（固件自己跑动作）、
    // #pause/#resume（会顶掉 Hexa 自己的控制权）、#channel-ranges-*（改限位行为）全在名单外。
    // 运动只能走引擎那条被 MotionSafetyLimiter 与急停管着的路（TryDispatchAxes）。
    private static readonly string[] AllowedFirmwareCommands = ["#list-settings", "#help", "$save", "#restart"];
    private static readonly Regex FirmwareSettingNameRegex = new("^[A-Za-z0-9_.-]{1,40}$", RegexOptions.Compiled);

    /// <summary>固件设置命令是否在白名单里。<c>#setting:名字:值</c> 会校验名字与值，防止一条命令里塞进第二条。</summary>
    public static bool IsAllowedFirmwareCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string cmd = command.Trim();
        foreach (string allowed in AllowedFirmwareCommands)
            if (string.Equals(cmd, allowed, StringComparison.OrdinalIgnoreCase)) return true;

        const string settingPrefix = "#setting:";
        if (!cmd.StartsWith(settingPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string body = cmd[settingPrefix.Length..];
        int separator = body.LastIndexOf(':');
        if (separator <= 0 || separator == body.Length - 1) return false;
        string name = body[..separator];
        string value = body[(separator + 1)..];
        return FirmwareSettingNameRegex.IsMatch(name)
               && value.Length <= 64
               && value.All(ch => !char.IsWhiteSpace(ch) && ch != ':');
    }

    /// <summary>发一条固件设置命令。返回 false = 不在白名单 / 端口没开 / 输出被锁 / 写失败。</summary>
    public bool SendFirmwareCommand(string command)
    {
        if (!IsAllowedFirmwareCommand(command)) return false;
        string cmd = command.Trim();

        Exception? failure = null;
        SerialPort? port;
        lock (_lock)
        {
            if (_simulationEnabled)
            {
                RecordSimulationCommandLocked(cmd);
                return true;
            }
            // 输出锁定（急停中）时不写固件设置：那一刻要的是「什么都别动」，而且设置会写 Flash，
            // 跟正在进行的急停保护混在一起更难排查。
            if (!_outputEnabled || _port?.IsOpen != true) return false;
            port = _port;
        }

        // 写放在 _lock 外面（与 Send/SendFrame 同理）：这里要取写闸门，拿着 _lock 等会把急停堵住。
        try
        {
            return TryWriteGated(port, cmd + (char)10, 500);
        }
        catch (Exception ex)
        {
            failure = ex;
            lock (_lock) { MarkDisconnectedLocked(ex.Message); }
        }

        if (failure != null)
        {
            AppLogger.Error("固件设置命令写入失败", failure);
            ConnectionChanged?.Invoke(false);
        }
        return false;
    }

    /// <summary>
    /// 发一条固件设置命令并收集固件的回话（<c>#list-settings</c> 用），返回原始文本。
    /// 与 <see cref="QueryDeviceInfo"/> 同样：I/O 走 <see cref="_ioLock"/>，**绝不占 <see cref="_lock"/>**
    /// —— 这里要等最多 2 秒，占着那把锁会把急停堵死。会阻塞，调用方自己放后台线程。
    /// </summary>
    public string QueryFirmwareCommand(string command, int timeoutMs = 2000)
    {
        if (!IsAllowedFirmwareCommand(command)) return "";
        if (_simulationEnabled) return "";
        SerialPort? port;
        lock (_lock)
        {
            port = _port;
            if (port?.IsOpen != true || !_outputEnabled) return "";
        }

        lock (_ioLock)
        {
            try
            {
                port.DiscardInBuffer();
                if (!TryWriteGated(port, command.Trim() + "\n", 1000)) AppLogger.Warn("串口写闸门被占，固件查询命令没发出去。");
                long startedAt = Stopwatch.GetTimestamp();
                var response = new System.Text.StringBuilder();
                while (Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < timeoutMs)
                {
                    if (port.BytesToRead > 0) response.Append(port.ReadExisting());
                    Thread.Sleep(25);
                }
                return response.ToString();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("固件设置查询失败: " + ex.Message);
                return "";
            }
        }
    }

    public void Send(string command)
    {
        string safeCommand = Osr6DeviceProfile.SanitizeTCode(command);
        if (safeCommand.Length == 0) return;

        // 锁内只判断"能不能发"并取出端口引用；真正的 Write 放到锁外。
        // SerialPort.Write 是阻塞调用（写超时最长 500ms），占着 _lock 会把急停/锁定输出一起堵住——
        // 而急停恰恰是最不能等的操作。并发下端口可能刚好被关掉：写会抛异常，走下面的失败分支
        //（标记掉线）即可，不会静默丢指令。
        SerialPort? port = null;
        string? mirrored = null;
        string payload = safeCommand;
        lock (_lock)
        {
            if (!_outputEnabled)
            {
                // 输出锁定期间丢掉的指令要留痕：否则用户看到的是"发了但机器不动"，无从排查。
                // 只记第一次和每 200 次，避免刷屏。
                _droppedWhileLocked++;
                if (_droppedWhileLocked == 1 || _droppedWhileLocked % 200 == 0)
                    AppLogger.Warn($"输出已锁定，丢弃指令（累计 {_droppedWhileLocked} 条）：{safeCommand}。" +
                                   "解锁请点侧栏「全部归中」。");
                return;
            }
            // 游戏桥与校准只发一根轴的 TCode 行（"L08222I60"），原样写下去就是残帧；
            // 真机固件收到缺轴帧会在 20 秒内 panic 重启（灌流量实验复现），所以先并进帧状态、
            // 补成完整六轴帧再写。非轴指令（DSTOP / $save / V0xx…）照原样走。
            if (TryComposeAxisLineLocked(safeCommand, out string composed))
            {
                payload = composed;
                if (ReserveRecordSlot(ref _lastComposeLogMs, 5000))
                    AppLogger.Info($"残帧补齐：{safeCommand} → {payload}");
                // 位置已经并进帧状态，什么时候真写由节拍器决定（见 PaceFrame）。
                if (!_simulationEnabled) return;
            }

            if (_simulationEnabled)
            {
                RecordSimulationCommandLocked(payload);
                mirrored = payload;      // 模拟设备下也镜像：没插设备也能验证多输出
            }
            else
            {
                port = _port;
                if (port?.IsOpen != true) return;
            }
        }

        if (port != null)
        {
            Exception? failure = null;
            try
            {
                if (!TryWriteGated(port, payload + (char)10, 250))
                {
                    AppLogger.Warn("串口正忙，丢弃这条指令：" + safeCommand);
                    return;
                }
                mirrored = payload;
                NoteWriteSucceeded(payload, payload.Length + 1);
            }
            catch (Exception ex)
            {
                failure = ex;
                lock (_lock) MarkDisconnectedLocked(ex.Message);
            }
            if (failure != null)
            {
                AppLogger.Error("串口写入失败，输出已锁定", failure);
                ConnectionChanged?.Invoke(false);
                return;
            }
        }

        // 镜像在网络层做，故意不放锁里：目标连不上时不能把设备输出堵住。
        if (mirrored != null) Mirror?.Invoke(mirrored);
    }

    /// <summary>
    /// 硬复位设备：拉低 RTS（接芯片 EN）150ms 再放开，DTR 保持高电平，
    /// 这样芯片走正常启动而不是进下载模式。卡死时只有这一招能救回来（不用拔插 USB）。
    /// </summary>
    public bool HardResetDevice()
    {
        SerialPort? port;
        lock (_lock)
        {
            if (_simulationEnabled) return false;
            port = _port;
            if (port?.IsOpen != true) return false;
        }
        if (!Monitor.TryEnter(_writeGate, 500)) return false;
        try
        {
            port.DtrEnable = false;
            port.RtsEnable = true;
            Thread.Sleep(150);
            port.RtsEnable = false;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("硬复位失败：" + ex.Message);
            return false;
        }
        finally
        {
            Monitor.Exit(_writeGate);
        }
        Interlocked.Exchange(ref _quietUntilMs, Environment.TickCount64 + 3000);
        lock (_lock) ResetSentAxesLocked();
        return true;
    }

    /// <summary>
    /// 只含 TCode 轴标记的一行（游戏桥/校准命令）→ 补成完整帧；不是这种行就返回 false 原样发送。
    /// </summary>
    private bool TryComposeAxisLineLocked(string command, out string frame)
    {
        frame = "";
        var updates = new List<(int Index, int Raw, int IntervalMs)>(6);
        foreach (string token in command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 6) return false;
            int index = Array.IndexOf(Osr6DeviceProfile.InstalledAxes, token[..2].ToUpperInvariant());
            if (index < 0) return false;
            if (!int.TryParse(token.AsSpan(2, 4), out int raw)) return false;
            int interpolation = 0;
            if (token.Length > 6)
            {
                if (token.Length < 8) return false;
                if (token[6] is not ('I' or 'S' or 'G')) return false;
                if (!int.TryParse(token.AsSpan(7), out interpolation)) return false;
            }
            updates.Add((index, raw, interpolation));
        }
        if (updates.Count == 0) return false;
        frame = TCodeFrameFormatter.FormatRaw(updates, _lastSentRaw, _lastSentValid, _lastSentInterval);
        return frame.Length > 0;
    }

    public void SendAxes(
        double[] values,
        Dictionary<string, int> axisMin,
        Dictionary<string, int> axisMax,
        int interpolationMs = 16,
        bool changedOnly = true)
    {
        if (values.Length < Osr6DeviceProfile.InstalledAxes.Length) return;
        SendFrame(
            Osr6DeviceProfile.InstalledAxes.Select((axis, index) =>
                new TCodeAxisTarget(axis, values[index], interpolationMs)),
            axisMin,
            axisMax,
            changedOnly);
    }

    /// <summary>
    /// 整行写入的唯一出口：拿不到闸门就不写（丢掉一帧没人看得出来，写半行才是事故）。
    /// </summary>
    private static bool TryWriteGated(SerialPort port, string text, int waitMs)
    {
        if (!Monitor.TryEnter(_writeGate, waitMs)) return false;
        try
        {
            port.Write(text);
            return true;
        }
        finally
        {
            Monitor.Exit(_writeGate);
        }
    }

    public void SendFrame(
        IEnumerable<TCodeAxisTarget> targets,
        Dictionary<string, int> axisMin,
        Dictionary<string, int> axisMax,
        bool changedOnly = true)
    {
        string frame;
        lock (_lock)
        {
            if (!_outputEnabled || (!_simulationEnabled && _port?.IsOpen != true))
            {
                // 与 Send 一样留痕：波形/脚本/游戏桥整形帧全都走这条路，静默丢弃会让
                //「发了但机器不动」完全无从排查（子代理审计发现 SendFrame 从来没有过日志）。
                _droppedFramesWhileLocked++;
                if (_droppedFramesWhileLocked == 1 || _droppedFramesWhileLocked % 200 == 0)
                    AppLogger.Warn($"输出已锁定，丢弃帧（累计 {_droppedFramesWhileLocked} 帧）。" +
                                   "解锁请点侧栏「全部归中」。");
                return;
            }
            if (Environment.TickCount64 < Interlocked.Read(ref _quietUntilMs)) return;
            frame = TCodeFrameFormatter.FormatTargets(
                targets, axisMin, axisMax, _lastSentRaw, _lastSentValid, changedOnly, _lastSentInterval);
            if (frame.Length == 0) return;
            // 真机：这里只把位置并进帧状态。真的写串口交给 PaceFrame 按固定 40ms 一拍发，
            // 这样引擎 30 Hz + 游戏桥 + Lovense 三个来源叠加也不会把节拍打歪。
            if (!_simulationEnabled) return;
            RecordSimulationCommandLocked(frame);
        }

        // 模拟设备下也镜像：没插设备也能验证多输出。
        Mirror?.Invoke(frame);
    }

    /// <summary>
    /// 帧节拍器：固定 40ms 一拍，把当前帧状态整帧发出去（六轴全带 I40）。
    /// 真机实测只有这种形态能长时间不崩——固件的插值窗口必须恒定接上下一条命令。
    /// </summary>
    /// <summary>
    /// 节拍线程：按单调时钟排程，每 40ms 调一次 <see cref="PaceFrame"/>。
    /// 用独立线程而不是 Timer，是因为 Timer 回调被阻塞时会排队补发，节拍就乱了。
    /// </summary>
    private void PacerLoop()
    {
        var clock = Stopwatch.StartNew();
        double next = 0;
        while (!_disposed)
        {
            next += FrameIntervalMs / 1000.0;
            double wait = next - clock.Elapsed.TotalSeconds;
            if (wait > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            }
            else if (clock.Elapsed.TotalSeconds - next > 1.0)
            {
                // 掉队超过 1 秒（锁等待/写超时之后）就重设基准，避免连续补发。
                next = clock.Elapsed.TotalSeconds;
            }
            try { PaceFrame(); }
            catch (Exception ex) { AppLogger.Error("节拍器异常（已忽略）", ex); }
        }
    }

    private void PaceFrame()
    {
        if (_disposed || _simulationEnabled) return;
        SerialPort? port;
        string frame;
        lock (_lock)
        {
            if (_disposed || _simulationEnabled || !_outputEnabled) return;
            port = _port;
            if (port?.IsOpen != true) return;
            if (Environment.TickCount64 < Interlocked.Read(ref _quietUntilMs)) return;
            // 一条运动指令都还没收到时不发帧：否则一开串口就往中位下命令（机器会自己动一下）。
            bool known = false;
            for (int i = 0; i < _lastSentValid.Length; i++) known |= _lastSentValid[i];
            if (!known) return;
            // 大跳跃必须削平：目标可能一帧从 1735 跳到 9148（实测抓到的真实帧），
            // 固件处理这种位移会写坏堆并 panic，所以这里限速，剩下的位移交给后面几帧慢慢走。
            if (!_hasWrittenFrame)
            {
                Array.Copy(_lastSentRaw, _lastWrittenRaw, _lastWrittenRaw.Length);
                _hasWrittenFrame = true;
            }
            for (int i = 0; i < _lastSentRaw.Length; i++)
            {
                if (!_lastSentValid[i]) continue;
                _lastWrittenRaw[i] = TCodeFrameFormatter.LimitStep(_lastWrittenRaw[i], _lastSentRaw[i], MaxStepPerFrame);
            }
            frame = TCodeFrameFormatter.FormatState(_lastWrittenRaw, _lastSentValid, FrameIntervalMs);
            if (frame.Length == 0) return;
        }

        try
        {
            // 拿不到闸门说明有别的线程正卡在一次慢写里；这一拍就算了，下一拍会带着最新位置再发。
            // 闸门只等 5ms：写闸门被别人占着就跳过这一拍，绝不为了凑一帧把节拍拖歪。
            if (!TryWriteGated(port, frame + '\n', 5)) return;
            lock (_txRing) { _txRing.Enqueue(frame); while (_txRing.Count > 400) _txRing.Dequeue(); }
            NoteWriteSucceeded(frame, frame.Length + 1);
            NotePacerGap();
        }
        catch (Exception ex)
        {
            lock (_lock) { MarkDisconnectedLocked(ex.Message); }
            AppLogger.Error("串口写入失败，输出已锁定", ex);
            ConnectionChanged?.Invoke(false);
            return;
        }
        Mirror?.Invoke(frame);
    }

    public void StopMotion()
    {
        Exception? failure = null;
        bool mirrored = false;
        SerialPort? stopPort = null;
        lock (_lock)
        {
            if (!_simulationEnabled && _port?.IsOpen != true) return;
            if (_simulationEnabled)
            {
                // 与其它三处入口同样封顶：Stop 可能被反复调用，缺这一句就是唯一会无界增长的地方。
                // 不清帧状态：DSTOP 只是停在原地，各轴位置没变，后面的帧继续按完整六轴发。
                RecordSimulationCommandLocked("DSTOP");
                mirrored = true;
            }
            else
            {
                // 同上：DSTOP 本身最该快，绝不能拿锁去等串口写超时。
                stopPort = _port;
            }
        }

        if (stopPort is not null)
        {
            try
            {
                // 急停优先于流整齐：先排队 300ms，排不上也照写（宁可插进半行，也不能不发停机）。
                if (!TryWriteGated(stopPort, "DSTOP\n", 300))
                {
                    stopPort.Write("DSTOP\n");
                    AppLogger.Warn("急停没排到写闸门，已强插一条 DSTOP。");
                }
                NoteWriteSucceeded("DSTOP", "DSTOP\n".Length);
                mirrored = true;
            }
            catch (Exception ex)
            {
                failure = ex;
                lock (_lock) { MarkDisconnectedLocked(ex.Message); }
            }
        }
        if (failure != null)
        {
            AppLogger.Error("DSTOP 发送失败，连接已关闭", failure);
            ConnectionChanged?.Invoke(false);
        }
        // 急停也要镜像出去：网络那一头的设备同样必须停。本机串口写失败时**尤其**要发——
        // 那一刻正是最需要另一个出口停下的时刻。
        if (mirrored || stopPort is not null) Mirror?.Invoke("DSTOP");
    }

    /// <summary>
    /// 把最近写出的帧落盘（每次崩溃覆盖）：复现固件崩溃要用**软件真实发过的帧**，
    /// 拿这些帧喂 probe 的 replay 模式就能分辨"崩是内容引起"还是"端口/驱动引起"。
    /// </summary>
    private void DumpTxRing(string reason)
    {
        string[] frames;
        lock (_txRing) frames = _txRing.ToArray();
        if (frames.Length == 0) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append("=== ").Append(reason).Append(" @ ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" frames=").Append(frames.Length).AppendLine();
            foreach (string frame in frames) sb.AppendLine(frame);
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(AppLogger.LogFilePath)!, "tx-before-reset.log"),
                sb.ToString());
        }
        catch { /* 诊断用，失败不影响输出 */ }
    }

    /// <summary>节拍诊断：累计 200 次实际写出的间隔，记 min/max/avg（出问题看日志就知道节拍歪没歪）。</summary>
    private void NotePacerGap()
    {
        // 用 Stopwatch（微秒级）而不是 TickCount64（约 15.6ms 粒度，会把 40ms 读成 31/47）。
        long now = Stopwatch.GetTimestamp();
        if (_pacerLastWriteMs != 0)
        {
            int gap = (int)Math.Clamp(Stopwatch.GetElapsedTime(_pacerLastWriteMs, now).TotalMilliseconds, 0, 5000);
            if (gap < _pacerGapMin) _pacerGapMin = gap;
            if (gap > _pacerGapMax) _pacerGapMax = gap;
            _pacerGapSum += gap;
            if (++_pacerGapCount >= 200)
            {
                _pacerTotalFrames += _pacerGapCount;
                AppLogger.Info($"节拍：最近 {_pacerGapCount} 帧间隔 min={_pacerGapMin}ms max={_pacerGapMax}ms avg={_pacerGapSum / _pacerGapCount}ms 累计={_pacerTotalFrames}");
                _pacerGapMin = int.MaxValue;
                _pacerGapMax = 0;
                _pacerGapSum = 0;
                _pacerGapCount = 0;
            }
        }
        _pacerLastWriteMs = now;
    }

    private void MarkDisconnectedLocked(string error)
    {
        LastError = error;
        _outputEnabled = false;
        ClosePortLocked();
        _consecutiveFailures++;
        _nextReconnectUtc = DateTime.UtcNow.AddSeconds(BackoffSeconds());
        // 写失败是黑匣子最该留下的一条：它能解释"为什么输出突然锁了"。
        BlackBox.Record("state", "写入失败，输出已锁定: " + error);
    }

    /// <summary>
    /// 重连退避秒数：2^n 指数上升，封顶 60 秒。指数取 6 是因为 2^6=64 才能被 60 截到（取 5 时最大只有 32，
    /// 上限永远到不了）。失败计数越大越不该再频繁试探端口，所以封顶值是刻意留在这的。
    /// </summary>
    private double BackoffSeconds() => Math.Min(60, Math.Pow(2, Math.Min(_consecutiveFailures, 6)));

    private void ClosePortLocked()
    {
        StopReaderLocked();          // 先停读线程再关端口：免得它拿 _ioLock 去读一个正在被 Dispose 的端口
        try { _port?.Close(); } catch { }
        _port?.Dispose();
        _port = null;
        _activePortName = "";
        ResetSentAxesLocked();
    }

    private void ResetSentAxesLocked()
    {
        // 重连（含板子自己重启）后设备侧插值参数也回到默认，必须整帧重发一遍。
        Array.Clear(_lastSentValid);
        Array.Clear(_lastSentInterval);
    }

    // ---- 链路存活 + 黑匣子 ----

    /// <summary>in 通道限流：固件每秒吐两行 Stats、每帧还回两行，全记会在十几秒内把整圈 800 条冲光。</summary>
    private const int InRecordIntervalMs = 200;

    /// <summary>out 通道限流：50Hz 写帧同理 —— 黑匣子要的是"最近一分钟"，不是"最后一秒"。</summary>
    private const int OutRecordIntervalMs = 100;

    private long _lastInRecordMs;
    private long _lastOutRecordMs;

    /// <summary>上次为"设备重启标记"记过日志的计数（只用于去重，不参与任何判定）。</summary>
    private int _loggedResetCount;
    /// <summary>上次记过日志时的"判死"状态（状态切换才记，避免刷屏）。</summary>
    private bool _loggedDead;

    /// <summary>读线程是否真的在跑（诊断与故障包用；不参与任何判定）。</summary>
    public bool IsReaderActive
    {
        get { Thread? thread = Volatile.Read(ref _readThread); return thread is { IsAlive: true }; }
    }

    /// <summary>
    /// 启动读线程。**只在持有 _lock 时调用**——它和 <see cref="StopReaderLocked"/> 一起构成"端口的生命周期"，
    /// 全部在 _lock 内成对出现，所以 _readThread/_readCts 不需要额外的锁。
    /// </summary>
    private void StartReaderLocked(SerialPort port)
    {
        _readCts?.Cancel();
        var cts = new CancellationTokenSource();
        _readCts = cts;
        var thread = new Thread(() => ReadLoop(port, cts.Token))
        {
            IsBackground = true,
            Name = "HexaSerialReader",
        };
        _readThread = thread;
        thread.Start();
    }

    /// <summary>
    /// 停读线程。只 Cancel，**不 Join、不 Dispose**：调用方正持有 _lock，而读线程可能卡在 _ioLock 上
    /// （设备自检正在独占），Join 会把界面和急停一起堵住；CTS 不 Dispose 也没关系（没有注册回调，
    /// 由 GC 回收），反过来 Dispose 还可能让读线程撞上 ObjectDisposedException。
    /// 线程自己会在 ~50ms 内看到取消或端口已关而退出。
    /// </summary>
    private void StopReaderLocked()
    {
        _readThread = null;
        CancellationTokenSource? cts = _readCts;
        _readCts = null;
        try { cts?.Cancel(); } catch { }
    }

    /// <summary>
    /// 读线程主体：三处串口探针（身份探测 / 设备信息 / 固件命令）之外，这里是唯一的稳态读者，
    /// 只为把字节喂给 <see cref="LinkMonitor"/> 和黑匣子 —— 不解析、不显示、不改任何状态。
    ///
    /// 锁纪律：只取 <see cref="_ioLock"/>（探针要"先清缓冲再独占"，必须互斥），**绝不取 <see cref="_lock"/>**；
    /// 反向的 Start/Stop 都在 _lock 内调，于是永远只有 _lock → _ioLock 这一个方向，不会互等死。
    /// 读失败/端口被拔只记录不抛 —— 红线是"绝不因为没回话锁输出或断线"，误伤比漏报贵得多。
    /// </summary>
    private void ReadLoop(SerialPort port, CancellationToken token)
    {
        var buffer = new char[2048];
        while (!token.IsCancellationRequested)
        {
            int read = 0;
            try
            {
                lock (_ioLock)
                {
                    if (!port.IsOpen) break;
                    if (port.BytesToRead > 0) read = port.Read(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                // 只 Warn 不 Error：读线程挂了不影响输出，报成错误会让人以为设备出事了。
                AppLogger.Warn("串口读线程结束（输出状态不受影响）: " + ex.Message);
                BlackBox.Record("state", "读线程结束: " + ex.Message);
                break;
            }

            if (read <= 0)
            {
                // 轮询而不是阻塞 Read：阻塞读会一直占着 _ioLock 等字节，把设备自检饿死。
                Thread.Sleep(50);
                LogLinkTransitionIfNeeded();   // 设备安静时只有这里能发现"它不说话了"
                continue;
            }

            string chunk = new string(buffer, 0, read);
            LinkMonitor.Feed(chunk);
            _recentRxChunks.Enqueue(chunk);
            while (_recentRxChunks.Count > 4) _recentRxChunks.Dequeue();
            if (ReserveRecordSlot(ref _lastInRecordMs, InRecordIntervalMs))
                BlackBox.Record("in", chunk);
            AppendRawTelemetry(chunk);
            LogLinkTransitionIfNeeded();
        }
    }

    /// <summary>
    /// 链路状态"变了才记一条日志"。按状态切换（而不是按时间）记录，天然满足"同类消息每秒最多一条"；
    /// 而且这里**只写日志**：绝不锁输出、绝不断线 —— 没回话的误伤比漏报贵得多。
    /// </summary>
    private void LogLinkTransitionIfNeeded()
    {
        SerialLinkSnapshot snapshot = LinkMonitor.Snapshot();

        int resetSeen = Volatile.Read(ref _loggedResetCount);
        if (snapshot.ResetSeenCount != resetSeen)
        {
            Interlocked.Exchange(ref _loggedResetCount, snapshot.ResetSeenCount);
            // 重启后给它 2.5 秒起启动，期间不写串口：这一静默既保住了输出锁定状态，
            // 也避开了「端口还在但设备没起来」那几秒必定失败、且要堵 500ms 的写。
            Interlocked.Exchange(ref _quietUntilMs, Environment.TickCount64 + 2500);
            AppLogger.Warn($"链路：检测到设备重启标记（累计 {snapshot.ResetSeenCount} 次）。已暂停发帧 2.5 秒并重新接手串口，不必拔插 USB。");
            LogRecentRx("设备重启现场");
            DumpTxRing("设备重启现场");
            RequestRebootRecovery();
        }

        long nowMs = Environment.TickCount64;
        if (snapshot.IsWedged)
        {
            if (_wedgedSinceMs == 0)
            {
                _wedgedSinceMs = nowMs;
                AppLogger.Warn("链路：固件执行器卡住了（看门狗刷屏、心跳已停），4 秒后硬复位板子。");
                LogRecentRx("卡死现场");
            }
            else if (nowMs - _wedgedSinceMs > 4000 && nowMs >= _nextHardResetMs)
            {
                _nextHardResetMs = nowMs + HardResetCooldownMs;
                _wedgedSinceMs = nowMs;
                AppLogger.Warn(HardResetDevice()
                    ? "链路：已硬复位板子（拉低 RTS），等它重新启动后自动续上输出。"
                    : "链路：想硬复位板子但串口正忙，下一轮再试。");
            }
        }
        else if (!snapshot.IsWedged && _wedgedSinceMs != 0 && snapshot.HasHeartbeat)
        {
            _wedgedSinceMs = 0;
            AppLogger.Info("链路：固件恢复正常，心跳回来了。");
        }

        if (snapshot.IsDead != _loggedDead)
        {
            _loggedDead = snapshot.IsDead;
            if (snapshot.IsDead)
            {
                AppLogger.Warn("链路：" + SerialLinkMonitor.Describe(snapshot) + "（仅提示，未锁输出）");
                LogRecentRx("掉链现场");
            }
        }
    }

    /// <summary>
    /// 板子刚重启：等它把 CDC 重新枚举完，就把端口关掉重开，让输出尽快续上。
    /// 只在本进程还在用真串口、端口仍然开着时动手；重连退避计数清零，因为这不是"连不上"，是"设备刚起"。
    /// </summary>
    private void RequestRebootRecovery()
    {
        if (Interlocked.Exchange(ref _rebootRecoveryPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RebootReattachDelayMs).ConfigureAwait(false);

                bool wasUnlocked;
                lock (_lock)
                {
                    if (_disposed || _simulationEnabled) return;
                    if (_port?.IsOpen != true) return;            // 已经断了，交给常规重连循环
                    if (_pendingOpenTask is { IsCompleted: false }) return;
                    wasUnlocked = _outputEnabled;
                    ClosePortLocked();
                    _outputEnabled = false;
                    _consecutiveFailures = 0;
                    _nextReconnectUtc = DateTime.MinValue;
                }

                AppLogger.Info("链路：重启后重新接手串口，尽快恢复输出。");
                if (!await TryConnectCoreAsync(notify: false).ConfigureAwait(false)) return;
                // 板子重启后必须自己把输出解锁回来：手机会连着崩好几次，每次都要人去点「全部归中」才动，
                // 等于一整局都废了。只在崩之前本来就是解锁状态（用户自己点开过）时才恢复。
                if (wasUnlocked && IsOpen)
                {
                    OutputEnabled = true;
                    AppLogger.Info("链路：板子起来了，输出已自动恢复。");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("链路：重启后重新接手串口失败: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _rebootRecoveryPending, 0);
            }
        });
    }

    private static readonly object _rawLogGate = new();
    private static long _rawLogBytes = -1;

    /// <summary>
    /// 把设备自己说的话落到 serial-raw.log（丢掉每帧都重复的收发回显，否则 30–60Hz 几分钟就写满盘）。
    /// 为什么要单独存：复位那一刻端口是断的，重启后才拿到的 banner 里没有原因码；
    /// 真正值钱的是它崩之前自己打的行（panic / Guru Meditation / 掉压 / 看门狗），
    /// 而 ring 只在重启后翻，翻不到「死之前」。
    /// </summary>
    private static void AppendRawTelemetry(string chunk)
    {
        StringBuilder? kept = null;
        int start = 0;
        while (start < chunk.Length)
        {
            int nl = chunk.IndexOf('\n', start);
            int end = nl < 0 ? chunk.Length : nl;
            ReadOnlySpan<char> line = chunk.AsSpan(start, end - start).TrimEnd('\r');
            start = end + 1;
            if (line.Length == 0) continue;
            if (line.Contains("UART received", StringComparison.Ordinal)) continue;
            if (line.Contains("UART echoed", StringComparison.Ordinal)) continue;
            if (line.Contains("TCode: preprocess", StringComparison.Ordinal)) continue;
            (kept ??= new StringBuilder()).Append(line).Append('\n');
        }
        if (kept is null) return;

        string text = kept.ToString();
        lock (_rawLogGate)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexa");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "serial-raw.log");
                if (_rawLogBytes < 0)
                    _rawLogBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (_rawLogBytes > 4L * 1024 * 1024)
                {
                    File.Move(path, path + ".1", overwrite: true);
                    _rawLogBytes = 0;
                }
                File.AppendAllText(path, text);
                _rawLogBytes += text.Length;
            }
            catch
            {
                // 取证失败绝不能影响串口：这里静默。
            }
        }
    }

    /// <summary>
    /// 把最近收到的原始串口文本写进日志。只读缓冲区、不改任何状态，所以不违反
    /// 「读线程绝不锁输出、绝不因没回话断线」的红线。换行压成 ⏎，一次复位在日志里占一行。
    /// </summary>
    private void LogRecentRx(string reason)
    {
        string text = string.Join("", _recentRxChunks).Replace("\r", "").Replace("\n", " ⏎ ");
        if (text.Length == 0) return;
        if (text.Length > 1200) text = text[^1200..];
        AppLogger.Warn(reason + "（原始串口）: " + text);
        BlackBox.Record("boot", reason + ": " + text);
    }

    /// <summary>
    /// 写成功后的记账：喂监控（回显判活的依据）+ 黑匣子（限流）。50Hz 热路径，无锁、无大对象。
    /// </summary>
    private void NoteWriteSucceeded(string text, int bytes)
    {
        LinkMonitor.NoteFrameWritten(bytes);
        if (ReserveRecordSlot(ref _lastOutRecordMs, OutRecordIntervalMs))
            BlackBox.Record("out", text);
    }

    /// <summary>限流闸门：距上次记录够久才放行。CompareExchange 抢占，多线程同时来也只有一个能过。</summary>
    private static bool ReserveRecordSlot(ref long lastMs, int intervalMs)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref lastMs);
        if (last != 0 && now - last < intervalMs) return false;
        return Interlocked.CompareExchange(ref lastMs, now, last) == last;
    }

    /// <summary>
    /// 导出黑匣子故障包，返回实际写出的绝对路径（界面「导出故障包」按钮走这里）。
    /// 默认落在 %LOCALAPPDATA%\Hexa\，文件名带时间戳；导出不在热路径上，建目录与整段落盘都在这里做。
    /// </summary>
    public string ExportBlackBox(string? directory = null)
    {
        SerialLinkSnapshot link = LinkMonitor.Snapshot();
        string dir = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexa")
            : directory;
        string path = Path.Combine(dir, $"hexa-blackbox-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        return BlackBox.Export(path,
        [
            ("端口", IsOpen ? PortName : "（未连接）"),
            ("模拟设备", IsSimulation ? "是" : "否"),
            ("输出已解锁", OutputEnabled ? "是" : "否"),
            ("读线程", IsReaderActive ? "运行中" : "未运行"),
            ("链路", SerialLinkMonitor.Describe(link)),
            ("ResetSeenCount", link.ResetSeenCount.ToString()),
            ("EchoRatio", link.EchoRatio.ToString("0.00")),
            ("写入帧数", link.FramesWritten.ToString()),
            ("收到字节", link.BytesSeen.ToString()),
            ("最后错误", LastError ?? "（无）"),
        ]);
    }

    public static IReadOnlyList<SerialPortOption> GetAvailablePortOptions()
    {
        var bluetoothPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key != null)
            {
                foreach (string valueName in key.GetValueNames())
                {
                    if (valueName.Contains("BthModem", StringComparison.OrdinalIgnoreCase)
                        && key.GetValue(valueName) is string portName)
                        bluetoothPorts.Add(portName);
                }
            }
        }
        catch { }

        Dictionary<string, PortMetadata> metadata = ReadPortMetadata();

        var options = SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                metadata.TryGetValue(name, out PortMetadata info);
                bool isBluetooth = bluetoothPorts.Contains(name)
                    || info.SearchText.Contains("BTH", StringComparison.OrdinalIgnoreCase)
                    || info.SearchText.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                    || info.SearchText.Contains("蓝牙", StringComparison.OrdinalIgnoreCase);
                bool inbound = isBluetooth && (info.SearchText.Contains("Incoming", StringComparison.OrdinalIgnoreCase)
                    || info.SearchText.Contains("Inbound", StringComparison.OrdinalIgnoreCase)
                    || info.SearchText.Contains("传入", StringComparison.OrdinalIgnoreCase));
                return new SerialPortOption(name, isBluetooth, info.FriendlyName, info.HardwareId, inbound);
            })
            .ToArray();
        return RankPortOptions(options, preferWired: true, primaryPortName: "", fallbackPortName: "");
    }

    /// <summary>
    /// 有线优先时，把蓝牙串口从「自动发现」的候选里剔掉。
    /// 真机实测（2026-10-20）：拔掉 SR6 后自动重连会去试配对的蓝牙 COM4，Open 卡 5.1 秒才抛
    /// “信号灯超时时间已到”，于是「设备已断开、输出已锁定」比实际拔线晚 5.7 秒才发生。
    /// 一台都没剩（用户真的只有蓝牙串口）时保留原列表，不改变那种场景的行为。
    /// </summary>
    public static IReadOnlyList<SerialPortOption> WithoutBluetoothWhenWired(
        IReadOnlyList<SerialPortOption> ranked,
        bool preferWired)
    {
        if (!preferWired) return ranked;
        var wiredOnly = ranked.Where(option => !option.IsBluetooth).ToArray();
        return wiredOnly.Length > 0 ? wiredOnly : ranked;
    }

    public static IReadOnlyList<SerialPortOption> RankPortOptions(
        IEnumerable<SerialPortOption> options,
        bool preferWired,
        string? primaryPortName,
        string? fallbackPortName)
    {
        string primary = NormalizePort(primaryPortName);
        string fallback = NormalizePort(fallbackPortName);
        return options
            .Where(option => !option.IsBluetoothInbound)
            .OrderBy(option =>
            {
                int score = preferWired && option.IsBluetooth ? 100 : 0;
                if (option.IsKnownWiredDevice || option.IsKnownBluetoothDevice) score -= 30;
                if (string.Equals(option.PortName, primary, StringComparison.OrdinalIgnoreCase)) score -= 20;
                if (string.Equals(option.PortName, fallback, StringComparison.OrdinalIgnoreCase)) score -= 10;
                return score;
            })
            .ThenBy(option => option.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private readonly record struct PortMetadata(string FriendlyName, string HardwareId, string SearchText);

    // 注册表 Enum 递归全扫很贵（设备多时几百毫秒），而它在连接/重连热路径上会被反复调用。
    // 加 30 秒缓存：插拔设备后最多等半分钟即可被识别，换来重连时不再周期性卡顿。
    private static readonly object _portMetaCacheLock = new();
    private static Dictionary<string, PortMetadata>? _portMetaCache;
    private static DateTime _portMetaCacheAtUtc;

    private static Dictionary<string, PortMetadata> ReadPortMetadata()
    {
        lock (_portMetaCacheLock)
        {
            if (_portMetaCache != null && (DateTime.UtcNow - _portMetaCacheAtUtc).TotalSeconds < 30)
                return _portMetaCache;
        }

        var result = new Dictionary<string, PortMetadata>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (enumKey != null) ReadPortMetadataRecursive(enumKey, "", 0, result);
        }
        catch { }

        lock (_portMetaCacheLock)
        {
            _portMetaCache = result;
            _portMetaCacheAtUtc = DateTime.UtcNow;
        }
        return result;
    }

    private static void ReadPortMetadataRecursive(
        RegistryKey key,
        string inheritedText,
        int depth,
        Dictionary<string, PortMetadata> result)
    {
        if (depth > 5) return;

        string friendly = key.GetValue("FriendlyName") as string ?? "";
        string description = key.GetValue("DeviceDesc") as string ?? "";
        string[] hardwareIds = key.GetValue("HardwareID") as string[] ?? [];
        string localText = string.Join(' ', inheritedText, key.Name, friendly, description, string.Join(' ', hardwareIds));
        Match match = Regex.Match(localText, @"\((COM\d+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            string portName = match.Groups[1].Value.ToUpperInvariant();
            result[portName] = new PortMetadata(
                string.IsNullOrWhiteSpace(friendly) ? description.Split(';').LastOrDefault() ?? "" : friendly,
                string.Join(' ', hardwareIds),
                localText);
        }

        foreach (string subKeyName in key.GetSubKeyNames())
        {
            try
            {
                using RegistryKey? child = key.OpenSubKey(subKeyName);
                if (child != null) ReadPortMetadataRecursive(child, localText, depth + 1, result);
            }
            catch { }
        }
    }

    public static string[] GetAvailablePorts() =>
        GetAvailablePortOptions().Select(option => option.PortName).ToArray();

    private static string NormalizePort(string? name) => (name ?? "").Trim().ToUpperInvariant();

    public void Dispose()
    {
        _reconnectTimer.Stop();
        _reconnectTimer.Dispose();

        lock (_lock)
        {
            _disposed = true;
            _connectionGeneration++;
            _reconnectEnabled = false;
            _autoDiscoveryEnabled = false;
            _outputEnabled = false;
            _simulationEnabled = false;
            ClosePortLocked();
        }
        // Do not dispose the gate here. A timed-out SerialPort.Open task may still
        // be unwinding and its owner must be allowed to release the gate safely.
    }
}
