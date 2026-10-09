using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
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
    private int _consecutiveFailures;
    /// <summary>输出锁定期间被丢弃的指令数（用于日志与诊断）。</summary>
    private int _droppedWhileLocked;
    private int _droppedFramesWhileLocked;   // 与 _droppedWhileLocked 分开计数：帧路径是所有连续运动的必经之路

    private DateTime _nextReconnectUtc = DateTime.MinValue;
    private long _connectionGeneration;
    private bool _disposed;

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
        }
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
                    double seconds = Math.Min(60, Math.Pow(2, Math.Min(_consecutiveFailures, 5)));
                    _nextReconnectUtc = DateTime.UtcNow.AddSeconds(seconds);
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
            discovered = RankPortOptions(GetAvailablePortOptions(), preferWired, primary, fallback);
            candidates.AddRange(discovered.Select(option => option.PortName));
        }

        string? lastError = null;
        foreach (string candidate in candidates.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lock (_lock)
            {
                if (_disposed || generation != _connectionGeneration)
                    return (false, "连接请求已取消");
                if (_port?.IsOpen == true && string.Equals(_activePortName, candidate, StringComparison.OrdinalIgnoreCase))
                    return (true, null);
            }

            var (opened, error) = await TryOpenPortAsync(candidate, generation).ConfigureAwait(false);
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
            }
            AppLogger.Info($"串口已连接: {candidate}（输出保持锁定）");
            return (true, null);
        }

        return (false, lastError ?? "没有可用端口");
    }

    private async Task<(SerialPort? port, string? error)> TryOpenPortAsync(string name, long generation)
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
        var openTask = Task.Run(() => OpenPort(name, cts.Token), CancellationToken.None);
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

    private static SerialPort? OpenPort(string name, CancellationToken cancellationToken)
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
            port.Close();
            port.Dispose();
            if (cancellationToken.IsCancellationRequested) return null;
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
            port.Write("D0\nD1\nD2\n");
            // 这台固件（ESP32-C3 的 TCode 分支）实测只对 CRLF 结尾的命令回 D1；两种都发一遍，握手更稳。
            port.Write("D0\r\nD1\r\nD2\r\n");
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
                port.Write("D0\nD1\nD2\n");
            // 这台固件（ESP32-C3 的 TCode 分支）实测只对 CRLF 结尾的命令回 D1；两种都发一遍，握手更稳。
            port.Write("D0\r\nD1\r\nD2\r\n");
                long startedAt = Stopwatch.GetTimestamp();
                var response = new System.Text.StringBuilder();
                while (Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < timeoutMs)
                {
                    if (port.BytesToRead > 0) response.Append(port.ReadExisting());
                    Thread.Sleep(25);
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
        lock (_lock)
        {
            if (_simulationEnabled)
            {
                _simulationCommands.Add(cmd);
                if (_simulationCommands.Count > 2000) _simulationCommands.RemoveAt(0);
                return true;
            }
            // 输出锁定（急停中）时不写固件设置：那一刻要的是「什么都别动」，而且设置会写 Flash，
            // 跟正在进行的急停保护混在一起更难排查。
            if (!_outputEnabled || _port?.IsOpen != true) return false;
            try { _port.Write(cmd + (char)10); return true; }
            catch (Exception ex)
            {
                failure = ex;
                MarkDisconnectedLocked(ex.Message);
            }
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
                port.Write(command.Trim() + "\n");
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
            if (_simulationEnabled)
            {
                _simulationCommands.Add(safeCommand);
                if (_simulationCommands.Count > 2000) _simulationCommands.RemoveAt(0);
                mirrored = safeCommand;      // 模拟设备下也镜像：没插设备也能验证多输出
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
            try { port.Write(safeCommand + (char)10); mirrored = safeCommand; }
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

    public void SendFrame(
        IEnumerable<TCodeAxisTarget> targets,
        Dictionary<string, int> axisMin,
        Dictionary<string, int> axisMax,
        bool changedOnly = true)
    {
        Exception? failure = null;
        string? mirrored = null;
        SerialPort? port = null;
        string frame = "";
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
            frame = TCodeFrameFormatter.FormatTargets(
                targets, axisMin, axisMax, _lastSentRaw, _lastSentValid, changedOnly);
            if (frame.Length == 0) return;

            if (_simulationEnabled)
            {
                _simulationCommands.Add(frame);
                if (_simulationCommands.Count > 2000) _simulationCommands.RemoveAt(0);
                mirrored = frame;      // 模拟设备下也镜像：没插设备也能验证多输出
            }
            else
            {
                // 只取端口引用，**写放在锁外**：串口写最坏要等 WriteTimeout(500ms)，
                // 拿 _lock 去写会把急停（它必须先拿 _lock/_dispatchLock）堵住半秒。
                port = _port;
            }
        }

        if (port is not null)
        {
            try { port.Write(frame + '\n'); mirrored = frame; }
            catch (Exception ex)
            {
                failure = ex;
                lock (_lock) { MarkDisconnectedLocked(ex.Message); }
            }
        }

        if (failure != null)
        {
            AppLogger.Error("串口写入失败，输出已锁定", failure);
            ConnectionChanged?.Invoke(false);
            return;
        }
        if (mirrored != null) Mirror?.Invoke(mirrored);
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
                _simulationCommands.Add("DSTOP");
                ResetSentAxesLocked();
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
                stopPort.Write("DSTOP\n");
                lock (_lock) { ResetSentAxesLocked(); }
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
            return;
        }
        // 急停也要镜像出去：网络那一头的设备同样必须停。
        if (mirrored) Mirror?.Invoke("DSTOP");
    }

    private void MarkDisconnectedLocked(string error)
    {
        LastError = error;
        _outputEnabled = false;
        ClosePortLocked();
        _consecutiveFailures++;
        double seconds = Math.Min(60, Math.Pow(2, Math.Min(_consecutiveFailures, 5)));
        _nextReconnectUtc = DateTime.UtcNow.AddSeconds(seconds);
    }

    private void ClosePortLocked()
    {
        try { _port?.Close(); } catch { }
        _port?.Dispose();
        _port = null;
        _activePortName = "";
        ResetSentAxesLocked();
    }

    private void ResetSentAxesLocked() => Array.Clear(_lastSentValid);

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
