using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 游戏桥 — 在 Hexa 内部实现 Intiface/Buttplug WebSocket 服务器（默认 127.0.0.1:12345），
/// 把游戏的设备指令翻译成 OSR6 串口 TCode。呈现方式对齐 Osr6DualBridge（osr6_bridge.exe）：
///
///   single = 向游戏虚拟 1 台全能力设备（L0 直线 + R0 旋转）
///   dual   = 向游戏虚拟 2 台设备（设备0=L0 直线，设备1=R0 旋转；
///            Linear 发往设备1 映射到 L1，Rotate 发往设备0 映射到 R1）
///
/// 安全：输出复用 SerialService（急停后 OutputEnabled=false 自动断流）；
/// Hexa 其他运动模式（自动/行程/波形/遥测）运行时桥自动让位。
/// </summary>
public sealed class IntifaceBridgeService : IDisposable
{
    private readonly ICommandTransport _transport;
    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;
    private readonly object _lock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private System.Timers.Timer? _tickTimer;
    private readonly Dictionary<string, RotateState> _rotateStates = new(StringComparer.OrdinalIgnoreCase);

    // ── 动作整形（游戏指令 → 连续、立体、不超舒适档的动作）─────────────
    // 游戏每 20–50ms 发一条、常常只有一根轴、还带噪声：直接透传就是「抖 + 平」。
    // 整形放在 50ms 的 TickTimerElapsed 里跑（和延迟补偿同一个节拍）。
    /// <summary>轴顺序（唯一真源：<see cref="Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] ShaperAxes = Osr6DeviceProfile.InstalledAxes;
    private readonly object _shapeLock = new();
    private readonly double[] _shapeTarget = [50, 50, 50, 50, 50, 50];   // 游戏最新指令
    private readonly double[] _shapeValue = [50, 50, 50, 50, 50, 50];    // 整形后的当前位置
    private readonly double[] _shapeSent = [-1, -1, -1, -1, -1, -1];     // 最近一次真正发出去的位置
    private readonly bool[] _shapeDriven = new bool[6];                  // 游戏正在驱动哪些轴
    private readonly ScriptAxisLinker _shapeLinker = new();
    private long _shapeLastTick;
    private bool _shapeLinkActive;

    /// <summary>整形管线是否生效（三个开关全关 = 回到「原样透传」，方便随时退回旧行为）。</summary>
    private bool ShapingActive =>
        _cfg.BridgeInputSmoothing || _cfg.BridgeAxisLink || _cfg.BridgeUseComfortLimits;
    private int _clientCount;

    /// <summary>一个已连接的 Buttplug 客户端：写入要串行化（Tick 线程和会话线程都会写）。</summary>
    private sealed class ClientSession
    {
        public required Stream Stream { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }
    private readonly List<ClientSession> _sessions = new();
    /// <summary>让位说明只写一次（进入让位时），避免每 50ms 刷屏。</summary>
    private bool _yieldNoted;

    /// <summary>每根轴的「电平判定 + 持续动作」状态。</summary>
    private sealed class LevelState
    {
        public double LastValue = double.NaN;   // 上一次的电平（0–100 域）
        public int Repeats;                     // 连续同值次数
        public bool Sustaining;                 // 正在按「持续动作」渲染
        public double Mid;                      // 往复中心（0–100）
        public double Amplitude;                // 往复幅度（0–100）
        public double PeriodMs = 900;           // 往复周期
        public double Phase;                    // 0–1
    }

    private readonly LevelState[] _levels = Enumerable.Range(0, 6).Select(_ => new LevelState()).ToArray();

    /// <summary>连续两秒收不到新指令就停下持续动作（游戏挂机/进菜单时不该一直自己动）。</summary>
    private const long SustainIdleStopMs = 2000;

    /// <summary>tick 重入守卫（0=空闲，1=正在跑）。</summary>
    private int _tickBusy;

    /// <summary>每根轴最后一次被游戏驱动的时间（用于把 _shapeDriven 标回过期）。</summary>
    private readonly long[] _shapeDrivenAt = new long[6];

    /// <summary>超过这么久没有新指令，就不再算「正在被驱动」。</summary>
    private const long ShapeDriveTimeoutMs = 500;
    private readonly object _sessionsLock = new();
    /// <summary>当前有发言权的客户端＝最后连上的那个（多个客户端同时发指令会抢同一根轴，必须仲裁）。</summary>
    private ClientSession? _activeSession;
    private volatile bool _disposed;

    // ── 指令监视（游戏发来的原始消息 + 实际发给设备的 TCode） ──────────
    private readonly object _logLock = new();
    private readonly Queue<BridgeLogEntry> _log = new();
    private const int MaxLogEntries = 200;
    private long _rxCount;
    private long _txCount;

    /// <summary>一条指令记录：方向 RX=游戏→Hexa，TX=Hexa→设备。</summary>
    public sealed record BridgeLogEntry(long AtMs, string Direction, string Text);

    /// <summary>最近指令（旧→新），供测试台监视面板显示。</summary>
    public IReadOnlyList<BridgeLogEntry> RecentLog
    {
        get { lock (_logLock) return _log.ToArray(); }
    }

    public long RxCount => Volatile.Read(ref _rxCount);
    public long TxCount => Volatile.Read(ref _txCount);

    /// <summary>上一次「振动指令被忽略」说明的时间（节流用，最多 5 秒一条）。</summary>
    private long _lastVibrateNoteMs;

    /// <summary>把「振动指令被忽略」写进监视与追踪文件，最多 5 秒一条（游戏可能每秒发几十条）。</summary>
    private void NoteVibrateIgnored(string text)
    {
        long nowMs = Environment.TickCount64;
        if (nowMs - Interlocked.Read(ref _lastVibrateNoteMs) < 5000) return;
        Interlocked.Exchange(ref _lastVibrateNoteMs, nowMs);
        Note(text);
    }

    private void Log(string direction, string text)
    {
        if (direction == "RX") Interlocked.Increment(ref _rxCount);
        else if (direction == "TX") Interlocked.Increment(ref _txCount);
        lock (_logLock)
        {
            _log.Enqueue(new BridgeLogEntry(Environment.TickCount64, direction, text));
            while (_log.Count > MaxLogEntries) _log.Dequeue();
        }
        Trace(direction, text);
    }

    /// <summary>
    /// 只写监视与追踪文件、**不计入 RX/TX 计数**：用于「命令收到了但故意不执行」这类说明
    ///（典型＝振动指令被丢弃）。以前这是完全静默的，用户看到 RX 在涨、机器不动，
    /// 只能猜是哪坏了 —— 静默丢弃是这类困惑的元凶。
    /// </summary>
    public void Note(string text) => Log("!!", text);

    // ── 指令追踪文件 ────────────────────────────────────────────────────
    // 监视面板只有内存里最近几百条，关掉窗口就没了；用户明确要求「监控指令执行得对不对」，
    // 所以每条 RX/TX/说明都追加到 数据目录\bridge-trace.log（4MB 轮转一代，只留一份旧的）。
    // 一行一条「时间 方向 内容」，可以直接给用户看或给我 tail。
    private static readonly object TraceLock = new();
    private static bool _traceWarned;
    private const long TraceMaxBytes = 4 * 1024 * 1024;

    /// <summary>指令追踪文件路径（界面提示里会写出来）。</summary>
    public static string TraceFilePath => Path.Combine(AppSettings.DataDirectory, "bridge-trace.log");

    private static void Trace(string direction, string text)
    {
        try
        {
            lock (TraceLock)
            {
                string path = TraceFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > TraceMaxBytes)
                {
                    string rotated = path + ".1";
                    File.Delete(rotated);          // 不存在也不抛
                    File.Move(path, rotated);
                }
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {direction,-2} {text}{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            // 追踪写不进去绝不影响桥本身干活，只警告一次。
            if (!_traceWarned) { _traceWarned = true; AppLogger.Warn("指令追踪文件写入失败（不影响桥工作）: " + ex.Message); }
        }
    }

    /// <summary>清空指令记录与计数（测试台“清空”按钮）。</summary>
    public void ClearLog()
    {
        lock (_logLock) _log.Clear();
        Interlocked.Exchange(ref _rxCount, 0);
        Interlocked.Exchange(ref _txCount, 0);
    }

    /// <summary>
    /// 自测：按当前呈现/映射模式注入一串模拟游戏指令（1.2Hz 强度起伏），
    /// 用于在不启动游戏的情况下验证「映射 → 串口 → 设备」整条链路。
    /// </summary>
    public void RunSelfTest(int seconds = 5)
    {
        if (!Active) return;
        _ = Task.Run(async () =>
        {
            AppLogger.Info("游戏桥自测开始（模拟游戏指令）");
            long rxBefore = RxCount;
            long txBefore = TxCount;
            long start = Environment.TickCount64;
            try
            {
                while (!_disposed && Active && Environment.TickCount64 - start < seconds * 1000)
                {
                    double t = (Environment.TickCount64 - start) / 1000.0;
                    double speed = 0.5 + 0.45 * Math.Sin(2 * Math.PI * 1.2 * t);
                    string json = $"[{{\"LinearCmd\":{{\"Id\":99,\"DeviceIndex\":0,\"Vectors\":[{{\"Index\":0,\"Position\":{speed:F3},\"Duration\":80}}]}}}}]";
                    Log("RX", "[自测] " + json);
                    IntifaceProtocol.Process(json, Mode, EnqueueAction, _cfg.GameBridgeVibrateMode, NoteVibrateIgnored);
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("游戏桥自测异常: " + ex.Message);
            }
            finally
            {
                lock (_rotateLock) _rotateStates.Clear();
                SendTCode("DSTOP");
                // 自测必须给结论：以前只写"自测结束"，用户得自己去日志里判断链路通不通（子代理审计发现）。
                long rxDelta = RxCount - rxBefore;
                long txDelta = TxCount - txBefore;
                StatusNote = txDelta > 0
                    ? $"自测通过：模拟指令 {rxDelta} 条 → 发出 {txDelta} 条（链路通）"
                    : $"自测没通过：收到 {rxDelta} 条，但一条都没发出去 —— 看输出是不是被锁着、设备是不是没连";
                LastCommand = "自测结束";
                AppLogger.Info($"游戏桥自测结束：RX +{rxDelta} / TX +{txDelta}");
            }
        });
    }

    // ── 延迟补偿队列（0–500ms） ───────────────────────────────────────
    private readonly object _pendingLock = new();
    private readonly Queue<(long DueAt, IntifaceProtocol.BridgeAction Action)> _pending = new();

    public bool Active => _listener != null;
    /// <summary>当前监听地址（默认 127.0.0.1；开启“允许局域网连接”后为 0.0.0.0）。</summary>
    public string BindAddress { get; private set; } = "127.0.0.1";
    public string Mode { get; private set; } = "single";
    public int Port { get; private set; } = 12345;
    public int ClientCount => Math.Max(0, Volatile.Read(ref _clientCount));

    /// <summary>
    /// 桥的“此刻状态”（等游戏连 / 输出锁定 / 让位中…），与 <see cref="LastCommand"/>（最近一条真实指令）分开。
    /// 以前 tick 每 50ms 覆写的是同一个字段，于是"游戏断开 · 急停锁定中，未归中"这类安全文案
    /// 会在 50ms 内被冲掉，用户永远看不到它（子代理审计发现）。
    /// </summary>
    public string StatusNote { get; private set; } = "—";
    /// <summary>最近一条生效的设备指令（供测试台状态栏显示）。</summary>
    public string LastCommand { get; private set; } = "—";
    /// <summary>启动失败原因（端口被占用等），成功为 null。</summary>
    public string? LastError { get; private set; }

    private sealed class RotateState
    {
        public int Position;
        public double Speed;
        public bool Clockwise;
        public int Step;
    }

    public IntifaceBridgeService(ICommandTransport transport, MotionEngine engine, AppSettings cfg)
    {
        _transport = transport;
        _engine = engine;
        _cfg = cfg;
        Mode = cfg.GameBridgeMode;
        Port = cfg.GameBridgePort;
        _engine.StateChanged += OnEngineStateChanged;
    }

    /// <summary>按当前设置启动/重启/停止（开关或设置变更后调用）。</summary>
    public void Refresh()
    {
        if (_cfg.GameBridgeEnabled) Start();
        else Stop();
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (Active && Mode == _cfg.GameBridgeMode && Port == _cfg.GameBridgePort) return true;
            StopLocked();

            Mode = _cfg.GameBridgeMode;
            Port = _cfg.GameBridgePort;
            LastError = null;

            try
            {
                // 默认只监听回环：游戏都在本机运行，这样既不会弹 Windows 防火墙授权窗，
                // 也不会把设备暴露给局域网内的其他机器（原 osr6_bridge 监听 0.0.0.0 存在此风险）。
                // 需要 VR 头显/手机等局域网设备连接时，可开启“允许局域网连接”。
                BindAddress = _cfg.GameBridgeAllowLan ? "0.0.0.0" : "127.0.0.1";
                IPAddress bind = _cfg.GameBridgeAllowLan ? IPAddress.Any : IPAddress.Loopback;
                var listener = new TcpListener(bind, Port);
                listener.Start();
                _listener = listener;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _listener = null;
                AppLogger.Error("游戏桥监听失败（端口可能被占用）", ex);
                return false;
            }

            lock (_pendingLock) _pending.Clear();
            _cts = new CancellationTokenSource();
            ResetShaping();
            _tickTimer = new System.Timers.Timer(50) { AutoReset = true };
            _tickTimer.Elapsed += (_, _) => TickTimerElapsed();
            _tickTimer.Start();

            // 桥接管设备输出：停止 Hexa 其他运动，避免两路指令打架。
            _engine.StopAll();
            SendTCode("DSTOP");
            _ = AcceptLoopAsync(_cts.Token);
            _transport.ConnectionChanged += OnTransportConnectionChanged;
            AppLogger.Info($"游戏桥已启动：模式 {Mode}，监听 {BindAddress}:{Port}（Intiface WebSocket）");
            return true;
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        var listener = _listener;
        _listener = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _tickTimer?.Stop();
        _tickTimer?.Dispose();
        _tickTimer = null;
        try { listener?.Stop(); } catch { /* 已关闭 */ }

        _transport.ConnectionChanged -= OnTransportConnectionChanged;
        lock (_rotateLock) _rotateStates.Clear();
        // 关桥要停住设备、交还控制权、清干净状态：
        // 以前这里什么都没做 —— 游戏最后一条长插值可能还在走，而 DirectInputOwner 永远停在 bridge
        //（侧栏一直显示「运行中 · 游戏桥」，声音响应再也接不回来）。
        SendTCode("DSTOP");
        ResetShaping();
        // 诊断模式不触发引擎的收尾缓动：否则它会一路缓到 ~5000（六轴、I27），
        // 被后面「手动动轴页」那节算成"没人点的动作"而假红。
        if (Environment.GetEnvironmentVariable("HEXA_SELFTEST") is not { Length: > 0 })
        {
            _engine.ReleaseDirectInput("bridge");
        }
        lock (_pendingLock) _pending.Clear();
        // 关掉还连着的会话，让会话自己的 finally 跑完（它会交还控制权并按需归中）。
        // 以前把 _clientCount 硬写成 0，最后一个会话断开时判定不出「最后一个」，泄漏了控制权。
        ClientSession[] open;
        lock (_sessionsLock)
        {
            open = _sessions.ToArray();
            _activeSession = null;
        }
        foreach (ClientSession session in open)
        {
            try { session.Stream.Dispose(); } catch { /* 会话自己会收尾 */ }
        }
        LastCommand = "—";
        if (listener != null) AppLogger.Info("游戏桥已停止");
    }

    /// <summary>设备掉线/恢复时通知所有已连接客户端（DeviceRemoved / DeviceAdded）。</summary>
    private void OnTransportConnectionChanged(bool connected)
    {
        if (_disposed || _listener == null) return;
        try { BroadcastDeviceState(connected); }
        catch (Exception ex) { AppLogger.Warn("桥广播设备状态异常: " + ex.Message); }
    }

    private void BroadcastDeviceState(bool present)
    {
        ClientSession[] sessions;
        lock (_sessionsLock) sessions = _sessions.ToArray();
        if (sessions.Length == 0) return;
        string json = present ? IntifaceProtocol.DeviceAddedJson(0, 0, Mode) : IntifaceProtocol.DeviceRemovedJson(0);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        // 后台发：绝不让"通知客户端"这件事挡住运动线程。
        _ = Task.Run(async () =>
        {
            foreach (ClientSession session in sessions)
            {
                try { await SendAsync(session, bytes).ConfigureAwait(false); }
                catch (Exception ex) { AppLogger.Warn("通知客户端设备状态失败（忽略）: " + ex.Message); }
            }
        });
    }

    /// <summary>串行化写入同一个客户端（Tick 线程广播 + 会话线程回包可能同时发生）。</summary>
    private static async Task SendAsync(ClientSession session, byte[] payload)
    {
        await session.WriteLock.WaitAsync().ConfigureAwait(false);
        try { await WriteFrameAsync(session.Stream, payload).ConfigureAwait(false); }
        finally { session.WriteLock.Release(); }
    }

    private readonly object _rotateLock = new();

    private void OnEngineStateChanged()
    {
        // Hexa 其他模式运行时桥自动让位（状态由 TickTimerElapsed 检查，这里只记录）。
        if (_engine.IsRunning || _engine.ScriptPlaying) LastCommand = "让位给 Hexa 播放";
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                var listener = _listener;
                if (listener == null) return;
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) AppLogger.Warn("游戏桥 accept 失败: " + ex.Message);
                return;
            }
            _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
        }
    }

    // ── 客户端会话（WebSocket 握手 + 帧循环） ──────────────────────────
    private async Task HandleClientAsync(TcpClient tcp)
    {
        using (tcp)
        {
            ClientSession? session = null;
            try
            {
                NetworkStream stream = tcp.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null && line.Length != 0)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                if (!headers.TryGetValue("Sec-WebSocket-Key", out string? key) || string.IsNullOrEmpty(key))
                {
                    AppLogger.Warn("游戏桥拒绝了无 WebSocket 握手的连接");
                    return;
                }
                // 安全：浏览器连接会带 Origin，只允许本机来源，防止任意网页驱动设备。
                if (headers.TryGetValue("Origin", out string? origin) && !IsLocalOrigin(origin))
                {
                    AppLogger.Warn($"游戏桥拒绝非本机来源的连接：{origin}");
                    return;
                }
                string accept = ComputeAccept(key);
                byte[] handshake = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " +
                    accept + "\r\n\r\n");
                await stream.WriteAsync(handshake).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                AppLogger.Info("游戏已连接桥（Intiface 客户端）");
                session = new ClientSession { Stream = stream };
                lock (_sessionsLock)
                {
                    _sessions.Add(session);
                    _activeSession = session;   // 最后连上的说了算
                }
                // 计数与控制权都放在「握手 + 来源校验都通过」之后：
                // 以前放在最前面，任何裸 TCP（浏览器打开 127.0.0.1:12345、端口扫描）都能先抢走控制权，
                // 而且它一断开就会走到 ResetToCenter -> Home() 让六轴自动归中 —— 用户没点任何东西，机器却在动。
                Interlocked.Increment(ref _clientCount);
                _engine.TryClaimDirectInput("bridge", force: true);

                var buffer = new byte[65536];
                while (!_disposed && tcp.Connected)
                {
                    FrameInfo? frame = await ReadFrameAsync(stream, buffer).ConfigureAwait(false);
                    if (frame == null) break;
                    if (frame.Opcode == 0x9)                    // ping → pong
                    {
                        await WriteRawFrameAsync(stream, 0xA, frame.Payload).ConfigureAwait(false);
                        continue;
                    }
                    if (frame.Opcode == 0xA || frame.Opcode != 0x1) continue;   // 只处理文本帧
                    string json = Encoding.UTF8.GetString(frame.Payload);
                    Log("RX", json.Length > 180 ? json[..180] + "…" : json);
                    // 仲裁：不是当前有发言权的客户端时，只回错误、不动设备（否则两边抢同一根轴 -> 设备乱抖）。
                    // 注意：仲裁刻意不拦停车类指令（StopDeviceCmd / StopAllDevices）——「谁都能让机器停」
                    // 是安全语义，把停车也挡掉只会让某个客户端以为自己停了而机器还在动。别顺手补上。
                    bool hasFloor;
                    lock (_sessionsLock) hasFloor = ReferenceEquals(_activeSession, session);
                    if (!hasFloor && (json.Contains("LinearCmd") || json.Contains("RotateCmd") || json.Contains("VibrateCmd")))
                    {
                        byte[] denied = Encoding.UTF8.GetBytes(
                            "[{\"Error\":{\"Id\":0,\"ErrorMessage\":\"another client owns the device\",\"ErrorCode\":4}}]");
                        await SendAsync(session, denied).ConfigureAwait(false);
                        Note("另一个客户端正在驱动设备：已忽略这次指令（最后连上的客户端有发言权）");
                        continue;
                    }
                    foreach (string response in IntifaceProtocol.Process(json, Mode, EnqueueAction, _cfg.GameBridgeVibrateMode, NoteVibrateIgnored))
                    {
                        byte[] payload = Encoding.UTF8.GetBytes(response);
                        await SendAsync(session, payload).ConfigureAwait(false);
                    }
                }
                AppLogger.Info("游戏断开桥连接");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("游戏桥客户端会话异常: " + ex.Message);
            }
            finally
            {
                bool lastSession = false;
                if (session != null)
                {
                    Interlocked.Decrement(ref _clientCount);
                    lock (_sessionsLock)
                    {
                        _sessions.Remove(session);
                        // 主动端走了：把发言权交给还连着的那一个（有的话）。
                        if (ReferenceEquals(_activeSession, session))
                            _activeSession = _sessions.Count > 0 ? _sessions[^1] : null;
                        lastSession = _sessions.Count == 0;
                    }
                    // 不 Dispose 这把写锁：广播的 Task.Run 可能正拿着它（原来会抛 ObjectDisposedException 刷日志）。
                }
                // 判定改用「会话表空了」：以前用计数，而 StopLocked 会把计数硬清成 0，
                // 之后最后一个会话断开时计数变成 -1，条件永不成立 —— bridge 会永久占着直接输入控制权，
                // 表现为「关了桥之后，声音响应/画面跟随/手动动轴都动不了」。
                if (lastSession)
                {
                    _engine.ReleaseDirectInput("bridge");   // 交还控制权（声音响应可以重新接管）
                    ResetToCenter();
                }
            }
        }
    }

    private void ResetToCenter()
    {
        lock (_rotateLock) _rotateStates.Clear();
        SendTCode("DSTOP");

        // 急停锁定中绝不能因为「游戏断开」就把设备重新带电：
        // Home() 会解除急停并重新使能输出，那样用户按下的急停会被一个自动路径悄悄取消。
        if (_engine.EmergencyStopped)
        {
            LastCommand = "游戏断开 · 急停锁定中，未归中（点「全部归中」可解锁）";
            return;
        }

        // 自检/诊断模式绝不自己动机械：那会让后面几节测到「没人点的动作」而假红，
        // 也违反「诊断不动设备」的纪律。
        if (Environment.GetEnvironmentVariable("HEXA_SELFTEST") is { Length: > 0 })
        {
            LastCommand = "游戏断开 · 诊断模式未归中（已发 DSTOP）";
            return;
        }

        // 归中的本意是「玩完别把机器别在端点一直别着劲」，但它是一次自动机械运动，
        // 而且 Home() 会停掉当时在跑的一切。所以只在「没有别的源在动」时才归中：
        // 否则（脚本/声音响应/画面跟随正在驱动）只保持已发的 DSTOP，不去踩别人。
        string? owner = _engine.DirectInputOwner;
        if (_engine.IsRunning || _engine.ScriptPlaying
            || (owner is not null && !string.Equals(owner, "bridge", StringComparison.Ordinal)))
        {
            LastCommand = "游戏断开 · 已停住（别的源在动，未归中）";
            return;
        }

        // 六轴安全归中（原桥只归 L0/R0；Hexa 全轴归中更符合设备安全语义）。
        // 用 HomeUnlessEstopped：上面那次检查与 Home() 之间没有锁，用户正好在这中间按下急停时，
        // Home() 会把急停悄悄解开——自动路径绝不能取消用户按下的急停。
        if (!_engine.HomeUnlessEstopped())
        {
            LastCommand = "游戏断开 · 急停锁定中，未归中（点「全部归中」可解锁）";
            return;
        }
        LastCommand = "游戏断开 · 已归中";
    }

    // ── 指令应用（先过延迟补偿队列；急停立即执行） ──────────────────────
    private void EnqueueAction(IntifaceProtocol.BridgeAction action)
    {
        int latency = Math.Clamp(_cfg.GameBridgeLatencyMs, 0, 500);
        if (latency <= 0 || action is IntifaceProtocol.StopAction)
        {
            ApplyAction(action);
            return;
        }
        lock (_pendingLock) _pending.Enqueue((Environment.TickCount64 + latency, action));
    }

    private void DrainPending()
    {
        long now = Environment.TickCount64;
        while (true)
        {
            (long DueAt, IntifaceProtocol.BridgeAction Action) item;
            lock (_pendingLock)
            {
                if (_pending.Count == 0 || _pending.Peek().DueAt > now) return;
                item = _pending.Dequeue();
            }
            ApplyAction(item.Action);
        }
    }

    private void ApplyAction(IntifaceProtocol.BridgeAction action)
    {
        switch (action)
        {
            case IntifaceProtocol.LinearAction linear:
            {
                // 游戏桥以前直接拼字符串走串口，完全绕过轴限位和舒适限速：
                // 游戏发「1ms 全行程」也会照发，能把设备抽到撞限位。这里补两道安全阀：
                // ① 位置夹进该轴在设置页里配置的限位；② 插值时间不小于 60ms（限制最大速度）。
                int min = AxisLimit(linear.Axis, lower: true);
                int max = AxisLimit(linear.Axis, lower: false);
                int position = Math.Clamp(linear.Position, Math.Min(min, max), Math.Max(min, max));
                int duration = Math.Clamp(linear.DurationMs, BridgeMinInterpolationMs, 9999);

                if (!ShapingActive)
                {
                    // 三个整形开关全关 = 旧行为：原样透传。
                    SendTCode($"{linear.Axis}{position:D4}I{duration}");
                    LastCommand = $"直线 {linear.Axis} → {position / 100.0:P0}（{duration}ms · 原样透传）";
                    break;
                }

                int index = Array.FindIndex(ShaperAxes,
                    axis => string.Equals(axis, linear.Axis, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    SendTCode($"{linear.Axis}{position:D4}I{duration}");
                    LastCommand = $"直线 {linear.Axis} → {position / 100.0:P0}（{duration}ms）";
                    break;
                }
                double level = position / 99.99;   // 0–9999 → 0–100
                lock (_shapeLock)
                {
                    LevelState state = _levels[index];
                    bool same = double.IsFinite(state.LastValue) && Math.Abs(state.LastValue - level) < 0.5;
                    state.Repeats = same ? state.Repeats + 1 : 0;
                    state.LastValue = level;
                    if (!same)
                    {
                        // 新数值＝真轨迹：退出持续动作，照常追过去。
                        if (state.Sustaining) Note("游戏给了新位置，退出「持续动作」渲染");
                        state.Sustaining = false;
                        _shapeTarget[index] = level;
                    }
                    else if (state.Repeats >= IntifaceProtocol.LevelRepeatThreshold)
                    {
                        // 同一个位置反复出现：这是「油门/强度」而不是「轨迹」（实测那款游戏 32 条全是 Position 1.0）。
                        // 忠实翻译＝把轴钉在端点一直别着劲；重解释＝以该电平为中心做往复动作。
                        if (!state.Sustaining) Note($"游戏一直发同一个位置（{level:0}%），已按「持续动作」渲染成往复");
                        state.Sustaining = true;
                        state.Mid = level;
                        state.Amplitude = Math.Max(5.0, Math.Abs(level - 50.0));
                        state.PeriodMs = Math.Clamp(1200.0 - state.Amplitude * 8.0, 500.0, 1200.0);
                        state.Phase = 0;
                    }
                    _shapeDriven[index] = true;
                    _shapeDrivenAt[index] = Environment.TickCount64;
                }
                LastCommand = $"直线 {linear.Axis} → {position / 100.0:P0}（整形中）";
                break;
            }

            case IntifaceProtocol.RotateAction rotate:
                SetRotateState(rotate.Axis, rotate.Speed, rotate.Clockwise);
                LastCommand = rotate.Speed <= 0.001
                    ? $"旋转 {rotate.Axis} 停止"
                    : $"旋转 {rotate.Axis} {rotate.Speed:P0} {(rotate.Clockwise ? "顺" : "逆")}";
                break;

            case IntifaceProtocol.VibrateAction vibrate:
                // 用户口径（2026-10-09）：遇到「只发振动、不发旋转/直线」的游戏时，把振动映射到旋转。
                // 旋转本身已经是往复摆动（位置舵机做不到持续旋转），所以这里直接借用旋转状态机。
                // 新档「小幅高频抽插」：本机没有振动器，但游戏唯一的强度通道值得有反馈
                // （实测那款游戏 32 条 VibrateCmd 全被丢弃，机器一点反应都没有）。
                if (string.Equals(_cfg.GameBridgeVibrateMode, "stroke", StringComparison.OrdinalIgnoreCase))
                {
                    int strokeIndex = Array.FindIndex(ShaperAxes,
                        axis => string.Equals(axis, "L0", StringComparison.OrdinalIgnoreCase));
                    if (strokeIndex >= 0)
                    {
                        lock (_shapeLock)
                        {
                            LevelState state = _levels[strokeIndex];
                            if (vibrate.Speed <= 0.001)
                            {
                                state.Sustaining = false;
                                state.Repeats = 0;
                                state.LastValue = double.NaN;
                            }
                            else
                            {
                                state.Sustaining = true;
                                state.Mid = 50.0;
                                state.Amplitude = Math.Clamp(5.0 + 13.0 * vibrate.Speed, 5.0, 20.0);
                                state.PeriodMs = Math.Clamp(700.0 - 250.0 * vibrate.Speed, 300.0, 700.0);
                                state.Phase = 0;
                                _shapeDriven[strokeIndex] = true;
                                _shapeDrivenAt[strokeIndex] = Environment.TickCount64;
                            }
                        }
                    }
                    LastCommand = vibrate.Speed <= 0.001 ? "振动(→抽插) 停止" : $"振动(→小幅高频抽插) {vibrate.Speed:P0}";
                    break;
                }
                if (string.Equals(_cfg.GameBridgeVibrateMode, "rotate", StringComparison.OrdinalIgnoreCase))
                {
                    SetRotateState("R0", vibrate.Speed, clockwise: true);
                    LastCommand = vibrate.Speed <= 0.001 ? "振动(->旋转) 停止" : $"振动(->旋转) {vibrate.Speed:P0}";
                    break;
                }
                SetVibrateState(vibrate.Axis, vibrate.Speed);
                LastCommand = vibrate.Speed <= 0.001
                    ? $"振动 {vibrate.Axis} 停止"
                    : $"振动 {vibrate.Axis} {vibrate.Speed:P0}";
                break;

            case IntifaceProtocol.StopDeviceAction stopDevice:
            {
                FreezeShaping();   // 同上：停车类指令一律先把整形目标冻住
                // dual 模式：只停这一台 —— 停掉它的旋转、把它的直线轴按住（发它当前的位置），不发全局 DSTOP。
                string linearAxis = IntifaceProtocol.LinearAxisFor(Mode, stopDevice.DeviceIndex);
                string rotateAxis = IntifaceProtocol.RotateAxisFor(Mode, stopDevice.DeviceIndex);
                lock (_rotateLock) _rotateStates.Remove(rotateAxis);
                int hold = 5000;
                int idx = Array.FindIndex(ShaperAxes,
                    axis => string.Equals(axis, linearAxis, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    lock (_shapeLock) hold = (int)Math.Round(_shapeValue[idx] * 99.99);
                }
                int lo = AxisLimit(linearAxis, lower: true);
                int hi = AxisLimit(linearAxis, lower: false);
                SendTCode($"{linearAxis}{Math.Clamp(hold, Math.Min(lo, hi), Math.Max(lo, hi)):D4}I200");
                LastCommand = $"停设备 {stopDevice.DeviceIndex}（按住 {linearAxis} 并停 {rotateAxis}）";
                break;
            }

            case IntifaceProtocol.StopAction:
                FreezeShaping();   // 停车类指令：先把整形目标冻住，否则接下来几百毫秒内整形还会把轴推向游戏最后给的目标
                lock (_rotateLock) _rotateStates.Clear();
                lock (_pendingLock) _pending.Clear();
                SendTCode("DSTOP");
                LastCommand = "游戏急停（DSTOP）";
                break;
        }
    }

    // ── 旋转（原桥语义：按速度步进位置，周期 100ms） ───────────────────
    private void SetRotateState(string axis, double speed, bool clockwise)
    {
        lock (_rotateLock)
        {
            if (speed <= 0.001)
            {
                // 停转＝停在当前位置（跟 Hexa 其它地方同一条纪律：停手不移回原位）。
                // 以前这里一律发「下限」：用户没设限位（0–9999）时等于把扭转轴硬拽到 0000 —— 2026-10-09 实测每次停转都发生。
                if (_rotateStates.Remove(axis, out var stopped))
                    SendTCode($"{axis}{Math.Clamp(stopped.Position, 0, 9999):D4}I200");
                return;
            }
            if (!_rotateStates.TryGetValue(axis, out var state))
            {
                state = new RotateState { Position = 0 };
                _rotateStates[axis] = state;
            }
            state.Speed = speed;
            state.Clockwise = clockwise;
            // 50ms 定时器：速度等价于原桥 100ms 的 step = 9999*speed/10
            state.Step = (int)Math.Max(1, Math.Round(9999.0 * speed / 20.0));
        }
    }

    // ── 振动指令 ───────────────────────────────────────────────────────
    // single/dual：忠实原桥直发 V0（本机未安装附件轴，固件忽略）。
    // 振动响应设为「关闭」时协议层直接丢弃 VibrateCmd，不会走到这里。
    private void SetVibrateState(string axis, double speed)
    {
        speed = Math.Clamp(speed, 0, 1);
        int raw = (int)Math.Round(speed * 20.0);
        SendTCode($"V0{raw:D2}");
    }

    /// <summary>游戏桥下发的最小插值时间：再小就等于不限速（游戏发 1ms 时尤其危险）。</summary>
    private const int BridgeMinInterpolationMs = 60;

    /// <summary>取某轴在设置里配置的限位（轴名可能大小写不一，或不在字典里）。</summary>
    private int AxisLimit(string axis, bool lower)
    {
        try
        {
            var table = lower ? _cfg.AxisMin : _cfg.AxisMax;
            if (table is not null && table.TryGetValue(axis, out int value)) return Math.Clamp(value, 0, 9999);
            foreach ((string key, int candidate) in table ?? [])
                if (string.Equals(key, axis, StringComparison.OrdinalIgnoreCase)) return Math.Clamp(candidate, 0, 9999);
        }
        catch (Exception ex) { AppLogger.Warn($"读取轴限位失败（{axis}）：{ex.Message}"); }

        // 读不到限位时**不能**悄悄放大到 0–9999 全行程 —— 用户特意收窄过行程的话，那等于把保护关掉
        //（审计里的 fail-open）。回中位＝这一帧等于"别动"，宁可不动也不要乱走到头。
        return 5000;
    }

    private void TickTimerElapsed()
    {
        // 重入守卫：System.Timers.Timer 的回调跑在线程池上，回调一旦超过 50ms 就可能并发再入
        //（两个 tick 同时出队/整形 = 重复帧、dt 抖动、线程堆积）。
        if (Interlocked.CompareExchange(ref _tickBusy, 1, 0) != 0) return;
        try
        {
            if (_disposed || _listener == null) return;
            // 「最近指令」不能只在收到指令时才刷新：空闲 / 输出锁定 / 还没连上时界面会一直写着"整形中"，
            // 与实际不符（用户据此判断"到底动没动"）。
            if (ClientCount == 0) StatusNote = "等游戏连接…";
            else if (!_transport.OutputEnabled) StatusNote = "⚠ 输出已锁定 —— 点侧栏「全部归中」解锁";
            else StatusNote = "—";

            // 让位检查必须在出队**之前**：否则 Hexa 自己在跑（脚本/自动/波形）时，
            // 延迟补偿队列里的游戏指令依旧会被出队并直接发到设备上，两边抢同一根轴。
            // 让位判据必须包含脚本播放：ScriptPlaying 不置 IsRunning（脚本走的是直接输入这条路），
            // 只查 IsRunning 会让「脚本在放 + 游戏在发」两边同时写同一根轴。
            if (_engine.IsRunning || _engine.ScriptPlaying)
            {
                StatusNote = "让位给 Hexa 播放（游戏指令已丢弃）";
                bool had;
                lock (_pendingLock)
                {
                    had = _pending.Count > 0;
                    _pending.Clear();
                }
                // 让位是对的（Hexa 自己在跑），但要留一句可见的说明，否则「游戏发的指令去哪了」完全查不出来。
                if (had && !_yieldNoted)
                {
                    _yieldNoted = true;
                    Note("让位给 Hexa 自己播放：这段期间的游戏指令已被丢弃（脚本或自动动作停下后会自动恢复接收）");
                }
                return;
            }
            if (_yieldNoted)
            {
                // 刚从"让位"里出来：整形状态还停在让位之前的记忆上，按设备当前真实姿态重新对齐，
                // 否则接管瞬间会先往一个旧位置走一下（用户能看到跳变）。
                _yieldNoted = false;
                ResetShaping();
            }
            DrainPending();

            ShapeAndSend();

            lock (_rotateLock)
            {
                // 先取一份 key 快照：循环里会把「已经撞到限位」的轴停掉，不能边遍历边改字典。
                var rotatingAxes = new List<string>(_rotateStates.Keys);
                foreach (string axis in rotatingAxes)
                {
                    if (!_rotateStates.TryGetValue(axis, out RotateState? state) || state.Speed <= 0.001) continue;
                    // 关键：旋转也夹在用户设置的轴限位里。
                    // 原来这里只夹 0–9999，等于把"轴限位"这道安全阀只装在了直线路径上：
                    // 用户把 R0 限到 2000–8000 保护机械，游戏一条 RotateCmd 就能把它转到 0。
                    int lower = AxisLimit(axis, lower: true);
                    int upper = AxisLimit(axis, lower: false);
                    (int next, bool clockwise, bool changed) =
                        IntifaceProtocol.RotateStep(state.Position, state.Step, state.Clockwise, lower, upper);
                    state.Clockwise = clockwise;
                    // 撞到限位就停下、并停止继续发。
                    // 以前这里照样每 50ms 发一条一模一样的帧（2026-10-09 实测把 R0 钉在 0000 刷了上千条 TX），
                    // 既白占串口，也把扭转轴长期别在极位上。
                    if (!changed) { state.Speed = 0; continue; }
                    state.Position = next;
                    SendTCode($"{axis}{state.Position:D4}I100");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("游戏桥定时输出异常", ex);
        }
        finally
        {
            Volatile.Write(ref _tickBusy, 0);
        }
    }

    /// <summary>
    /// 一个整形节拍（50ms）：把游戏的最新指令低通 + 限速地追过去，需要时带起次要轴，然后只发变化了的轴。
    /// 速度上限取「舒适档」，所以游戏再急也抽不出超过舒适档的动作（关掉这个开关就回到旧的 60ms 插值上限）。
    ///
    /// 为什么整形放在这里而不是收到指令就发：游戏指令是**事件**（20–50ms 一条，还带噪声），
    /// 而设备要的是**连续动作**。按固定节拍把指令流"熨平"再下发，才能既不抖又不丢响应。
    /// </summary>
    private void ShapeAndSend()
    {
        if (!ShapingActive) return;

        long now = Environment.TickCount64;
        double dt = _shapeLastTick <= 0 ? 0.05 : Math.Clamp((now - _shapeLastTick) / 1000.0, 0.005, 0.2);
        _shapeLastTick = now;

        // 一根轴被驱动过之后不能永远算「正在被驱动」：超过 500ms 没有新指令就标回未驱动。
        // 否则关掉联动时的回中释放对这根轴永远不生效，整形节拍也会一直空转。
        for (int i = 0; i < 6; i++)
        {
            if (_shapeDriven[i] && IntifaceProtocol.IsDriveStale(now, _shapeDrivenAt[i], ShapeDriveTimeoutMs))
            {
                _shapeDriven[i] = false;
                _shapeDrivenAt[i] = 0;
                _levels[i].Sustaining = false;
                _levels[i].Repeats = 0;
                _levels[i].LastValue = double.NaN;
            }
        }

        var cfg = _cfg;
        var profile = ComfortProfile.Resolve(cfg.ComfortProfile);
        double maxSpeed = cfg.BridgeUseComfortLimits
            ? profile.MaxAxisSpeedPerSecond
            : BridgeMotionShaper.LegacyMaxSpeedPerSecond;
        double smoothingMs = cfg.BridgeInputSmoothing ? cfg.BridgeSmoothingMs : 0;

        // 持续动作：把「一直发同一个值」渲染成正弦往复（目标每拍更新，交给下面的整形/限速与"变了才发"逻辑）。
        for (int i = 0; i < 6; i++)
        {
            LevelState level = _levels[i];
            if (!level.Sustaining) continue;
            if (now - _shapeDrivenAt[i] > SustainIdleStopMs)
            {
                // 游戏两秒没消息（进菜单/挂机）：停下持续动作，停在当前位置。
                level.Sustaining = false;
                continue;
            }
            level.Phase += dt * 1000.0 / Math.Max(200.0, level.PeriodMs);
            if (level.Phase >= 1.0) level.Phase -= Math.Floor(level.Phase);
            _shapeTarget[i] = IntifaceProtocol.SustainTarget(level.Mid, level.Amplitude, level.Phase);
            _shapeDriven[i] = true;
            _shapeDrivenAt[i] = now;
        }

        var values = new double[6];
        var mask = new bool[6];
        bool anyDriven;
        lock (_shapeLock)
        {
            anyDriven = _shapeDriven.Any(flag => flag);
            for (int i = 0; i < 6; i++)
            {
                _shapeValue[i] = BridgeMotionShaper.Step(_shapeValue[i], _shapeTarget[i], dt, smoothingMs, maxSpeed);
                values[i] = _shapeValue[i];
                // 正在被「旋转」驱动的轴不算整形可写的轴：否则联动会和旋转抢同一根轴。
                mask[i] = _shapeDriven[i] || IsRotating(ShaperAxes[i]);
            }
        }
        if (!anyDriven)
        {
            // 没有任何轴被驱动：把（可能还挂着的）联动派生量平滑收回中位，然后什么都不发。
            if (_shapeLinkActive) ShapeReleaseLink(values, dt, maxSpeed);
            else return;
        }

        // 多轴联动：游戏只驱动 L0 时，把其余轴按同一套规则带起来（旋转中的轴让给旋转）。
        if (cfg.BridgeAxisLink && mask[0])
        {
            _shapeLinker.Apply(values, mask, cfg.BridgeAxisLinkAmount, dt, 0, 100);
            _shapeLinkActive = true;
        }
        else if (_shapeLinkActive)
        {
            ShapeReleaseLink(values, dt, maxSpeed);
        }

        int interpolation = Math.Max(BridgeMinInterpolationMs, (int)Math.Round(dt * 1000) + 10);
        // 锁内只算"这一拍要发哪些轴"，串口写放到锁外：
        // SerialPort.Write 是阻塞调用，占着 _shapeLock 会把 WebSocket 收包线程一起卡住。
        var toSend = new List<string>(6);
        lock (_shapeLock)
        {
            for (int i = 0; i < 6; i++)
            {
                // 和串口路径用同一套映射（TCodeFrameFormatter.MapPosition）：
                // 0–100 → 该轴校准过的 [下限, 上限]。原来直接 ×99.99，两头会被夹成非线性，
                // 用户在设置页收窄过的行程也得不到线性映射。
                int raw = TCodeFrameFormatter.MapPosition(
                    ShaperAxes[i], Math.Clamp(values[i], 0, 100), _cfg.AxisMin, _cfg.AxisMax);
                if (Math.Abs(raw - _shapeSent[i]) < 1) continue;      // 没变化就不发，省串口带宽
                _shapeSent[i] = raw;
                _shapeValue[i] = values[i];
                toSend.Add($"{ShaperAxes[i]}{raw:D4}I{interpolation}");
            }
        }
        foreach (string command in toSend) SendTCode(command);
    }

    /// <summary>
    /// 把整形目标冻结在当前值（停车类指令用）。
    /// 不这么做的话，StopAllDevices 只发了 DSTOP，而整形节拍在随后几百毫秒内还会把轴推向
    /// 游戏最后给定的那个目标 —— 用户看到的就是「喊了停，机器还在走最后一段」。
    /// </summary>
    private void FreezeShaping()
    {
        lock (_shapeLock)
        {
            for (int i = 0; i < 6; i++)
            {
                _shapeTarget[i] = _shapeValue[i];
                _shapeDriven[i] = false;
                _shapeDrivenAt[i] = 0;
                _levels[i].Sustaining = false;
                _levels[i].Repeats = 0;
                _levels[i].LastValue = double.NaN;
            }
        }
    }

    /// <summary>把联动派生出来的次要轴平滑收回中位（关掉联动的瞬间不让人看到"啪"地跳回去）。</summary>
    private void ShapeReleaseLink(double[] values, double dt, double maxSpeed)
    {
        for (int i = 1; i < 6; i++)
        {
            if (_shapeDriven[i]) continue;
            values[i] = BridgeMotionShaper.Step(values[i], 50, dt, 120, maxSpeed);
            if (Math.Abs(values[i] - 50) < 0.3) values[i] = 50;
        }
        _shapeLinkActive = values.Skip(1).Any(value => Math.Abs(value - 50) >= 0.3);
    }

    /// <summary>这根轴当前是否由旋转指令驱动（旋转走的是另一条输出路径）。</summary>
    private bool IsRotating(string axis)
    {
        lock (_rotateLock)
            return _rotateStates.TryGetValue(axis, out RotateState? state) && state.Speed > 0.001;
    }

    /// <summary>整形状态复位：以设备当前姿态为起点，避免桥刚开就把设备猛地拽到别的姿态。</summary>
    private void ResetShaping()
    {
        double[] current = _engine.GetLastOutputSnapshot();
        lock (_shapeLock)
        {
            for (int i = 0; i < 6; i++)
            {
                _shapeValue[i] = i < current.Length && double.IsFinite(current[i]) ? current[i] : 50;
                _shapeTarget[i] = _shapeValue[i];
                _shapeSent[i] = -1;
                _shapeDriven[i] = false;
                _shapeDrivenAt[i] = 0;
            }
            _shapeLinker.Reset();
            _shapeLinkActive = false;
        }
        _shapeLastTick = 0;
    }

    /// <summary>
    /// 解析一条轴指令（形如 L01234I60 / R05000I100）并把位置回报给引擎。
    /// 放在唯一的下发汇聚点，所有路径（原样透传 / 整形 / 旋转 / 按住）都会自动覆盖到。
    /// </summary>
    private void ReportOutputFromFrame(string frame)
    {
        if (frame.Length < 7) return;                          // 至少 轴名(2) + 四位位置 + "I"
        if (frame[2] < '0' || frame[2] > '9') return;          // 不是 轴名+四位数字 的形式
        string axis = frame[..2];
        if (!int.TryParse(frame.AsSpan(2, 4), out int position)) return;
        if (frame[6] is not ('I' or 'S' or 'G')) return;
        try
        {
            double[] snapshot = _engine.GetLastOutputSnapshot();
            int index = Array.FindIndex(ShaperAxes,
                candidate => string.Equals(candidate, axis, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index >= snapshot.Length) return;
            snapshot[index] = Math.Clamp(position / 99.99, 0, 100);
            _engine.ReportExternalOutput(snapshot);
        }
        catch (Exception ex) { AppLogger.Warn("回报输出姿态失败（忽略）: " + ex.Message); }
    }

    private void SendTCode(string cmd)
    {
        try
        {
            if (_disposed || _listener == null) return;
            ReportOutputFromFrame(cmd);   // 把这一帧的真实位置回报给引擎（3D 预览与让位对齐都靠它）
            _transport.Send(cmd);
            // 记录真正发往设备的指令；设备未连接/输出锁定时标注出来，便于定位"机器没反应"。
            Log("TX", _transport.OutputEnabled ? cmd : cmd + "  ⚠ 输出已锁定");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("游戏桥串口输出失败: " + ex.Message);
        }
    }

    // ── WebSocket 帧（RFC 6455，服务端到客户端不加掩码） ────────────────
    private sealed class FrameInfo
    {
        public byte Opcode = 0;
        public byte[] Payload = [];
    }

    private static async Task<FrameInfo?> ReadFrameAsync(Stream stream, byte[] buffer)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(stream, header, 2).ConfigureAwait(false)) return null;
        byte opcode = (byte)(header[0] & 0xF);
        int length = header[1] & 0x7F;
        switch (length)
        {
            case 126:
            {
                var ext = new byte[2];
                if (!await ReadExactAsync(stream, ext, 2).ConfigureAwait(false)) return null;
                length = (ext[0] << 8) | ext[1];
                break;
            }
            case 127:
            {
                var ext = new byte[8];
                if (!await ReadExactAsync(stream, ext, 8).ConfigureAwait(false)) return null;
                ulong big = BitConverter.ToUInt64(ext, 0);
                if (big > int.MaxValue) return null;
                length = (int)big;
                break;
            }
        }
        if (length > buffer.Length) return null;

        bool masked = (header[1] & 0x80) != 0;
        byte[]? mask = null;
        if (masked)
        {
            mask = new byte[4];
            if (!await ReadExactAsync(stream, mask, 4).ConfigureAwait(false)) return null;
        }
        if (!await ReadExactAsync(stream, buffer, length).ConfigureAwait(false)) return null;
        if (masked && mask != null)
        {
            for (int i = 0; i < length; i++) buffer[i] ^= mask[i % 4];
        }
        var payload = new byte[length];
        Array.Copy(buffer, payload, length);
        return new FrameInfo { Opcode = opcode, Payload = payload };
    }

    private static async Task WriteRawFrameAsync(Stream stream, byte opcode, byte[] payload)
    {
        var header = new List<byte> { (byte)(0x80 | opcode) };
        AppendLength(header, payload.Length);
        await stream.WriteAsync(header.ToArray()).ConfigureAwait(false);
        if (payload.Length != 0) await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload) =>
        await WriteRawFrameAsync(stream, 0x1, payload).ConfigureAwait(false);

    private static void AppendLength(List<byte> header, int length)
    {
        if (length < 126)
        {
            header.Add((byte)length);
        }
        else if (length < 65536)
        {
            header.Add(126);
            header.Add((byte)(length >> 8));
            header.Add((byte)(length & 0xFF));
        }
        else
        {
            header.Add(127);
            for (int shift = 56; shift >= 0; shift -= 8) header.Add((byte)((long)length >> shift));
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset)).ConfigureAwait(false);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

/// <summary>只允许本机来源的浏览器连接（原生客户端通常不带 Origin 头）。</summary>
    private static bool IsLocalOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        // 以前是子串匹配：域名里含 127.0.0.1 / localhost 就能过（例如 http://127.0.0.1.evil.com），
        // 任意网页因此能连上桥并驱动设备。现在解析出主机名做精确比对。
        if (origin.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed)) return false;
        string host = parsed.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal);
    }

    private static string ComputeAccept(string key)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
        return Convert.ToBase64String(hash);
    }

    public void Dispose()
    {
        _disposed = true;
        _engine.StateChanged -= OnEngineStateChanged;
        Stop();
    }
}

/// <summary>
/// Intiface/Buttplug 协议处理（纯函数，可单测）。
/// 输入一条客户端 JSON 消息，输出响应 JSON 列表，并把设备指令翻译为 BridgeAction 回调。
/// </summary>
internal static class IntifaceProtocol
{
    public abstract record BridgeAction;
    public sealed record LinearAction(string Axis, int Position, int DurationMs) : BridgeAction;
    public sealed record RotateAction(string Axis, double Speed, bool Clockwise) : BridgeAction;
    public sealed record VibrateAction(string Axis, double Speed) : BridgeAction;
    public sealed record StopAction : BridgeAction;

    private static bool IsSingle(string mode) => mode == "single";

    /// <summary>WebSocket 握手应答（RFC 6455）。</summary>
    public static string ComputeAccept(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

    /// <summary>六轴直驱：LinearCmd 的 Vectors[].Index 就是轴序号（0..5 → L0/L1/L2/R0/R1/R2）；越界回 Error 并返回空串。</summary>
    private static string LinearAxisForFeature(JsonElement vector, uint id, List<string> responses)
    {
        uint index = vector.TryGetProperty("Index", out JsonElement idx) && idx.TryGetUInt32(out uint parsed) ? parsed : 0u;
        if (index >= SixAxisOrder.Length)
        {
            responses.Add($"[{{\"Error\":{{\"Id\":{id},\"ErrorMessage\":\"LinearCmd feature index out of range\",\"ErrorCode\":3}}}}]");
            return string.Empty;
        }
        return SixAxisOrder[index];
    }

    /// <summary>六轴直驱模式：一台设备里宣告 6 个位置轴（L0/L1/L2/R0/R1/R2），游戏可以按 Index 直接点名任意一根。</summary>
    internal static bool IsSixAxis(string mode) => string.Equals(mode, "six", StringComparison.OrdinalIgnoreCase);

    /// <summary>「一台设备」的两种呈现：single（旧：只 L0+R0）与 six（六轴直驱）。</summary>
    internal static bool IsOneDevice(string mode) => IsSingle(mode) || IsSixAxis(mode);

    /// <summary>六轴直驱时的轴序：LinearCmd 的 Vectors[].Index 就是这里的下标。</summary>
    private static readonly string[] SixAxisOrder = new[] { "L0", "L1", "L2", "R0", "R1", "R2" };

    /// <summary>六轴直驱时 RotateCmd 能点名的轴（R0 扭转 / R1 倾斜 / R2 俯仰 都当"角度位置"驱动）。</summary>
    private static readonly string[] RotateFeatures = new[] { "R0", "R1", "R2" };

    private static string SixAxisDeviceMessages()
    {
        var linear = new List<string>();
        foreach (string axis in SixAxisOrder)
            linear.Add("{\"FeatureDescriptor\":\"" + axis + "\",\"ActuatorType\":\"Position\",\"StepCount\":100}");
        var rotate = new List<string>();
        foreach (string axis in RotateFeatures)
            rotate.Add("{\"FeatureDescriptor\":\"" + axis + "\",\"ActuatorType\":\"Rotate\",\"StepCount\":100}");
        return "\"LinearCmd\":[" + string.Join(",", linear) + "],\"RotateCmd\":[" + string.Join(",", rotate) + "],\"StopDeviceCmd\":{}";
    }

    public static string DeviceListJson(uint id, string mode)
    {
        if (IsSixAxis(mode))
        {
            return "[{\"DeviceList\":{\"Id\":" + id + ",\"Devices\":[{\"DeviceIndex\":0,\"DeviceName\":\"Hexa OSR6 6-Axis\",\"DeviceMessages\":{" + SixAxisDeviceMessages() + "}}]}}]";
        }
        if (IsSingle(mode))
        {
            return "[{\"DeviceList\":{\"Id\":" + id + ",\"Devices\":[{\"DeviceIndex\":0,\"DeviceName\":\"Hexa OSR6 (L0+R0)\",\"DeviceMessages\":{\"LinearCmd\":[{\"FeatureDescriptor\":\"L0\",\"ActuatorType\":\"Position\",\"StepCount\":100}],\"RotateCmd\":[{\"FeatureDescriptor\":\"R0\",\"ActuatorType\":\"Rotate\",\"StepCount\":100}],\"StopDeviceCmd\":{}}}]}}]";
        }
        return "[{\"DeviceList\":{\"Id\":" + id + ",\"Devices\":[{\"DeviceIndex\":0,\"DeviceName\":\"Hexa Linear (L0)\",\"DeviceMessages\":{\"LinearCmd\":[{\"FeatureDescriptor\":\"L0\",\"ActuatorType\":\"Position\",\"StepCount\":100}],\"StopDeviceCmd\":{}}},{\"DeviceIndex\":1,\"DeviceName\":\"Hexa Rotary (R0)\",\"DeviceMessages\":{\"RotateCmd\":[{\"FeatureDescriptor\":\"R0\",\"ActuatorType\":\"Rotate\",\"StepCount\":100}],\"StopDeviceCmd\":{}}}]}}]";
    }

    public static string DeviceAddedJson(uint id, int deviceIndex, string mode)
    {
        if (IsSixAxis(mode))
        {
            return "[{\"DeviceAdded\":{\"Id\":" + id + ",\"DeviceIndex\":0,\"DeviceName\":\"Hexa OSR6 6-Axis\",\"DeviceMessages\":{" + SixAxisDeviceMessages() + "}}}]";
        }
        if (IsSingle(mode))
        {
            return "[{\"DeviceAdded\":{\"Id\":" + id + ",\"DeviceIndex\":0,\"DeviceName\":\"Hexa OSR6 (L0+R0)\",\"DeviceMessages\":{\"LinearCmd\":[{\"FeatureDescriptor\":\"L0\",\"ActuatorType\":\"Position\",\"StepCount\":100}],\"RotateCmd\":[{\"FeatureDescriptor\":\"R0\",\"ActuatorType\":\"Rotate\",\"StepCount\":100}],\"StopDeviceCmd\":{}}}}]";
        }
        if (deviceIndex == 0)
        {
            return "[{\"DeviceAdded\":{\"Id\":" + id + ",\"DeviceIndex\":0,\"DeviceName\":\"Hexa Linear (L0)\",\"DeviceMessages\":{\"LinearCmd\":[{\"FeatureDescriptor\":\"L0\",\"ActuatorType\":\"Position\",\"StepCount\":100}],\"StopDeviceCmd\":{}}}}]";
        }
        return "[{\"DeviceAdded\":{\"Id\":" + id + ",\"DeviceIndex\":1,\"DeviceName\":\"Hexa Rotary (R0)\",\"DeviceMessages\":{\"RotateCmd\":[{\"FeatureDescriptor\":\"R0\",\"ActuatorType\":\"Rotate\",\"StepCount\":100}],\"StopDeviceCmd\":{}}}}]";
    }

    public static List<string> Process(string json, string mode, Action<BridgeAction> onAction, string vibrateMode = "position",
        Action<string>? onNote = null)
    {
        var responses = new List<string>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    // 逐条兜住：以前一条坏消息会抛到外层 catch，把同一批里后面的指令（包括 StopAllDevices）一起丢掉。
                    try { ProcessOne(item, mode, vibrateMode, responses, onAction, onNote); }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("游戏桥单条消息处理失败（已跳过这一条）: " + ex.Message);
                        responses.Add("[{\"Error\":{\"Id\":0,\"ErrorCode\":0,\"ErrorMessage\":\"bad message\"}}]");
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                ProcessOne(root, mode, vibrateMode, responses, onAction, onNote);
            }
            else
            {
                // 合法 JSON但不是消息（5 / "x" / null / true）：以前两个分支都不进 → 静默丢弃，
                // 违反项目自己的不变量「每种意图要么处理、要么明确拒绝」。
                responses.Add("[{\"Error\":{\"Id\":0,\"ErrorMessage\":\"Unhandled message type\",\"ErrorCode\":3}}]");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("游戏桥消息解析失败: " + ex.Message);
            responses.Add("[{\"Error\":{\"Id\":0,\"ErrorCode\":0,\"ErrorMessage\":\"parse error\"}}]");
        }
        return responses;
    }

    private static void ProcessOne(JsonElement element, string mode, string vibrateMode, List<string> responses, Action<BridgeAction> onAction,
        Action<string>? onNote = null)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string name = property.Name;
            JsonElement value = property.Value;
            uint id = value.TryGetProperty("Id", out JsonElement idElement) && idElement.TryGetUInt32(out uint parsed) ? parsed : 1u;

            // 设备号越界一律拒绝：以前完全不校验 —— dual 下 DeviceIndex=99 会被静默当成 1 号设备驱动，
            // single/six 下也被忽略后照常下发，等于把指令打到客户端没点名的设备上。
            if (value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("DeviceIndex", out JsonElement diElement)
                && diElement.TryGetUInt32(out uint diValue)
                && !(IsOneDevice(mode) ? diValue == 0u : diValue <= 1u))
            {
                responses.Add($"[{{\"Error\":{{\"Id\":{id},\"ErrorMessage\":\"DeviceIndex out of range\",\"ErrorCode\":4}}}}]");
                continue;
            }

            switch (name)
            {
                case "RequestServerInfo":
                    responses.Add($"[{{\"ServerInfo\":{{\"Id\":{id},\"MessageVersion\":3,\"MaxPingTime\":0,\"ServerName\":\"Hexa OSR6 Bridge\",\"ServerMajorVersion\":3,\"ServerMinorVersion\":0,\"ServerBuildVersion\":0}}}}]");
                    break;
                case "RequestDeviceList":
                    responses.Add(DeviceListJson(id, mode));
                    break;
                case "StartScanning":
                    responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                    responses.Add(DeviceAddedJson(id, 0, mode));
                    // single 只呈现 1 台设备；dual 追加第 2 台。
                    if (!IsSingle(mode)) responses.Add(DeviceAddedJson(id, 1, mode));
                    // Buttplug v3 规定扫描结束要回 ScanningFinished（Id 用 StartScanning 的 Id）。
                    // 少这一条的后果：走"扫描"流程的客户端（Intiface Central 系、部分插件）会一直等扫描结束，永远不进驱动阶段。
                    responses.Add($"[{{\"ScanningFinished\":{{\"Id\":{id}}}}}]");
                    break;
                case "StopScanning":
                    responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                    break;
                case "LinearCmd":
                {
                    uint deviceIndex = value.TryGetProperty("DeviceIndex", out JsonElement di) && di.TryGetUInt32(out uint parsedDi) ? parsedDi : 0u;
                    foreach (JsonElement vector in value.GetProperty("Vectors").EnumerateArray())
                    {
                        if (!vector.TryGetProperty("Position", out JsonElement positionElement)
                            || !positionElement.TryGetDouble(out double positionRaw)
                            || !double.IsFinite(positionRaw))
                        {
                            // NaN/缺字段以前会被 (int) 转换夹到端点，看着像"静默跑到最下面"。
                            responses.Add($"[{{\"Error\":{{\"Id\":{id},\"ErrorMessage\":\"bad LinearCmd position\",\"ErrorCode\":3}}}}]");
                            continue;
                        }
                        double position = Math.Clamp(positionRaw, 0.0, 1.0);
                        int duration = vector.TryGetProperty("Duration", out JsonElement durationElement)
                            && durationElement.TryGetDouble(out double durationRaw)
                            && double.IsFinite(durationRaw)
                            ? Math.Clamp((int)Math.Round(durationRaw), 1, 9999)
                            : 200;
                        int raw = (int)Math.Round(position * 9999.0);
                        string axis = IsSixAxis(mode)
                            ? LinearAxisForFeature(vector, id, responses)
                            : LinearAxisFor(mode, deviceIndex);
                        if (axis.Length == 0) continue;   // 越界已经回过 Error
                        onAction(new LinearAction(axis, raw, duration));
                    }
                    responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                    break;
                }
                case "RotateCmd":
                {
                    uint deviceIndex = value.TryGetProperty("DeviceIndex", out JsonElement di) && di.TryGetUInt32(out uint parsedDi) ? parsedDi : 0u;
                    foreach (JsonElement rotation in value.GetProperty("Rotations").EnumerateArray())
                    {
                        if (!rotation.TryGetProperty("Speed", out JsonElement speedElement)
                            || !speedElement.TryGetDouble(out double speedRaw)
                            || !double.IsFinite(speedRaw))
                        {
                            responses.Add($"[{{\"Error\":{{\"Id\":{id},\"ErrorMessage\":\"bad RotateCmd speed\",\"ErrorCode\":3}}}}]");
                            continue;
                        }
                        double speed = Math.Clamp(speedRaw, 0.0, 1.0);
                        bool clockwise = rotation.GetProperty("Clockwise").GetBoolean();
                        uint featureIndex = rotation.TryGetProperty("Index", out JsonElement rotateIndex)
                            && rotateIndex.TryGetUInt32(out uint parsedRotateIndex) ? parsedRotateIndex : 0u;
                        if (IsSixAxis(mode) && featureIndex >= RotateFeatures.Length)
                        {
                            responses.Add($"[{{\"Error\":{{\"Id\":{id},\"ErrorMessage\":\"RotateCmd feature index out of range\",\"ErrorCode\":3}}}}]");
                            continue;
                        }
                        string axis = IsSixAxis(mode) ? RotateFeatures[featureIndex] : RotateAxisFor(mode, deviceIndex);
                        onAction(new RotateAction(axis, speed, clockwise));
                    }
                    responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                    break;
                }
                case "VibrateCmd":
                {
                    // 振动响应设为「关闭」：仍回 Ok（避免游戏判定设备掉线），但不下发任何动作，也不计入 Tx。
                    if (vibrateMode == "off")
                    {
                        // 显式说明「收到了但故意不执行」——静默丢弃会让人以为链路坏了（节流在服务侧做）。
                        onNote?.Invoke("已忽略 VibrateCmd：SR6 没有振动通道，振动永远动不了这台机器 —— 要让机器动，请让游戏发 LinearCmd（直线）或 RotateCmd（旋转）");
                        responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                        break;
                    }
                    uint deviceIndex = value.TryGetProperty("DeviceIndex", out JsonElement di) && di.TryGetUInt32(out uint parsedDi) ? parsedDi : 0u;
                    foreach (JsonElement speedItem in value.GetProperty("Speeds").EnumerateArray())
                    {
                        double speed = 0.0;
                        if (speedItem.TryGetProperty("Speed", out JsonElement speedValue))
                            speed = speedValue.TryGetDouble(out double speedRaw) && double.IsFinite(speedRaw)
                                ? Math.Clamp(speedRaw, 0.0, 1.0)
                                : 0.0;
                        uint index = speedItem.TryGetProperty("Index", out JsonElement idx) && idx.TryGetUInt32(out uint parsedIdx) ? parsedIdx : 0u;
                        onAction(new VibrateAction(VibrateAxisFor(mode, index), speed));
                    }
                    responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                    break;
                }
                case "StopAllDevices":
                    // 游戏要求「全部停止」必须真的停：以前这条没有分支，落到 default 只回一个 Ok，
                    // 实测 The Spiriting Away 一连上就发两次 StopAllDevices —— 也就是说"游戏喊停，机器不停"。
                    onAction(new StopAction());
                    responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                    break;
                case "StopDeviceCmd":
                {
                    // single：停整台（DSTOP）。dual：TCode 没有单轴急停，所以按设备号只把那一台的轴按住不动。
                    uint stopIndex = value.TryGetProperty("DeviceIndex", out JsonElement sdi) && sdi.TryGetUInt32(out uint parsedSdi)
                        ? parsedSdi : 0u;
                    onAction(IsOneDevice(mode) ? new StopAction() : new StopDeviceAction(stopIndex));
                    // 必须回 Ok：以前 break 写在前面，这一行永远执行不到（等 Ok 的客户端会超时）。
                    responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                    break;
                }
                case "Ping":
                    responses.Add($"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                    break;
                default:
                    // 以前未知消息也回 Ok，客户端会以为"支持但不做"；按 v3 回 Error（ErrorCode 3 = Message）。
                    responses.Add($"[{{\"Error\":{{\"Id\":{id},\"ErrorMessage\":\"Unhandled message type\",\"ErrorCode\":3}}}}]");
                    break;
            }
        }
    }

    // ── 轴映射表 ───────────────────────────────────────────────────────
    // 直线 → L0/L1（单桥 L0、双桥设备0=L0/设备1=L1），旋转 → R0/R1，振动 → V0 原样转发。
    // 曾经有「GTPB 兼容」和「六轴虚拟」两套映射可切换，均已按用户要求删除：
    // 桥不做任何振动轴映射，振动脉冲就是 V0。

    /// <summary>LinearCmd 轴映射：single→L0；dual→设备0=L0、设备1=L1。</summary>
    internal static string LinearAxisFor(string mode, uint deviceIndex) => mode switch
    {
        "single" => "L0",
        _ => deviceIndex == 0 ? "L0" : "L1",
    };

    /// <summary>RotateCmd 轴映射：single→R0；dual→设备1=R0、设备0=R1。</summary>
    internal static string RotateAxisFor(string mode, uint deviceIndex)
    {
        if (mode == "single") return "R0";
        return deviceIndex == 1 ? "R0" : "R1";
    }

    /// <summary>VibrateCmd → V0（振动通道原样转发，不做任何轴映射）。</summary>
    private static string VibrateAxisFor(string mode, uint featureIndex) => "V0";

    /// <summary>
    /// 设备被移除（串口掉线/拔插）时主动通知客户端：Buttplug v3 的 DeviceRemoved。
    /// 不发这条的后果：游戏一直以为设备还在，继续发指令（要么被丢、要么顶在锁定上），
    /// 而它自己完全不知道 —— 这正是"游戏在发、机器没动"最难查的一类。
    /// </summary>
    public static string DeviceRemovedJson(int deviceIndex) =>
        "[{\"DeviceRemoved\":{\"Id\":0,\"DeviceIndex\":" + deviceIndex + "}}]";

    /// <summary>
    /// 旋转走一步（纯函数，便于单测）。位置舵机做不到持续旋转，所以撞到限位就掉头 -> 往复摆动。
    /// 返回新位置、新方向，以及位置是否真的变了（没变就不该发帧，避免每 50ms 重发同一条）。
    /// </summary>
    /// <summary>dual 模式下只停某一台设备：TCode 没有单轴急停，服务侧会把那一台的轴按住并停掉它的旋转。</summary>
    public sealed record StopDeviceAction(uint DeviceIndex) : BridgeAction;

    /// <summary>把「电平」画成正弦往复（0–100 域），供持续动作渲染与单测用。</summary>
    internal static double SustainTarget(double mid, double amplitude, double phase) =>
        mid + amplitude * Math.Sin(phase * Math.PI * 2);

    /// <summary>同一个位置重复出现多少次之后，就把它当成「电平/油门」而不是「轨迹」。</summary>
    internal const int LevelRepeatThreshold = 4;

    /// <summary>该轴是否已经「不再被驱动」（超过 timeout 没有新指令；从未驱动过＝不算过期）。</summary>
    internal static bool IsDriveStale(long nowMs, long drivenAtMs, long timeoutMs) =>
        drivenAtMs > 0 && nowMs - drivenAtMs > timeoutMs;

    internal static (int Position, bool Clockwise, bool Changed) RotateStep(
        int position, int step, bool clockwise, int lower, int upper)
    {
        int min = Math.Min(lower, upper);
        int max = Math.Max(lower, upper);
        int signed = clockwise ? Math.Abs(step) : -Math.Abs(step);
        int target = position + signed;
        if (target > max) { target = max; clockwise = !clockwise; }
        else if (target < min) { target = min; clockwise = !clockwise; }
        target = Math.Clamp(target, min, max);
        return (target, clockwise, target != position);
    }
}
