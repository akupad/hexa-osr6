using System.Text;

namespace Hexa.Services;

/// <summary>
/// 链路存活判定（纯逻辑、无 IO、可单测）：把串口上读到的文本喂进来，回答"设备还活着吗"。
///
/// 为什么需要它：这台固件每写一帧都会回两行 uart 日志（收到 / 回显）、每秒还会吐两行 Stats，
/// 而 Hexa 稳态一次都不读串口。于是"设备已复位 / 线松了"只能表现为"机器不动了"，
/// 用户分不清是脚本没跑、输出被锁，还是链路已经断了。回显本来就是最省事的 ACK。
///
/// 判据刻意保守：<b>没写过帧就永远不判死</b>（设备可能只是安静），
/// 而且它只产出结论，绝不反过来去锁输出或断线 —— 见 <see cref="SerialService"/> 的红线。
/// 线程模型：<see cref="Feed"/> 只由串口读线程调用（单写者，行缓冲因此不需要锁），
/// 其余字段全是 Interlocked 原子量，任意线程调 <see cref="Snapshot"/> 都安全。
/// </summary>
public sealed class SerialLinkMonitor
{
    /// <summary>"活着"的时间窗。固件每帧回两行，50Hz 写帧时这个窗口里有几十次证据，1.5 秒已经很宽。</summary>
    public static readonly TimeSpan AliveWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// "死了"的时间窗。故意比 <see cref="AliveWindow"/> 大一倍：中间留一段灰区，
    /// 状态机在边界上来回翻（已确认 ↔ 已复位）比晚一秒报更烦人。3 秒也是界面文案里写的那个数。
    /// </summary>
    public static readonly TimeSpan DeadWindow = TimeSpan.FromSeconds(3);

    /// <summary>心跳窗口。Stats 是每秒两行，3 秒没见到就说明固件执行器已经不在跑了。</summary>
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// banner 去重窗口。一次上电的 banner 会有好几行、而且同时含两个标记（app_main / SR6Executor(），
    /// 不去重就会把"板子重启过 1 次"记成好几次。
    /// </summary>
    private static readonly TimeSpan ResetDedupeWindow = TimeSpan.FromSeconds(2);

    // 固件原文（大小写不敏感匹配）：改这里等于改判据，所以集中放在一处。
    private const string HeartbeatToken = "Executor: Stats";
    private const string BootTokenAppMain = "app_main";
    private const string BootTokenExecutor = "SR6Executor(";
    /// <summary>看门狗超时：固件里 executor_task 卡死、把 IDLE 饿死时每 5 秒刷这一行，而且心跳会彻底停。</summary>
    private const string WatchdogToken = "task_wdt";

    private const string EchoToken = "uart echoed";
    private const string ReceivedToken = "uart received";

    /// <summary>单行上限：真出现这么长的"行"说明没在按行输出，宁可丢头也不能让它无界增长。</summary>
    private const int MaxLineBuffer = 8192;

    private readonly Func<DateTime> _clock;
    /// <summary>没走完的行（串口读到的块边界与行边界无关，一行可能分两次读到）。</summary>
    private readonly StringBuilder _lineBuffer = new();

    private long _bytesSeen;
    private long _lastByteTicks;
    private long _lastEchoTicks;
    private long _lastHeartbeatTicks;
    private long _lastEvidenceTicks;
    private long _lastFrameTicks;
    private long _lastResetTicks;
    private long _lastWatchdogTicks;
    private long _expectedBytes;
    private int _framesWritten;
    private int _resetSeenCount;

    /// <param name="clock">时间源（单测注入假时钟；正式路径不传，用 <see cref="DateTime.UtcNow"/>）。</param>
    public SerialLinkMonitor(Func<DateTime>? clock = null)
        => _clock = clock ?? (static () => DateTime.UtcNow);

    /// <summary>设备吐来的字节数（含 banner 与纯日志，用来判断"串口到底通不通"）。</summary>
    public long BytesSeen => Interlocked.Read(ref _bytesSeen);

    /// <summary>最后一次收到任何字节的时刻（纯日志也算 —— 这是"线在传数据"的证据，不是"我们的话到了"的证据）。</summary>
    public DateTime? LastByteAtUtc => ToUtc(Interlocked.Read(ref _lastByteTicks));

    /// <summary>最后一次看到 <c>Executor: Stats</c> 的时刻。</summary>
    public DateTime? LastHeartbeatAtUtc => ToUtc(Interlocked.Read(ref _lastHeartbeatTicks));

    /// <summary>最后一次看到回显（`UART received` / `UART echoed` / 设备应答行）的时刻。</summary>
    public DateTime? LastEchoAtUtc => ToUtc(Interlocked.Read(ref _lastEchoTicks));

    /// <summary>见过几次上电 banner（app_main / SR6Executor(）。非 0 说明板子在我们眼皮底下重启过。</summary>
    public int ResetSeenCount => Volatile.Read(ref _resetSeenCount);

    /// <summary>成功写出去的帧数。</summary>
    public int FramesWritten => Volatile.Read(ref _framesWritten);

    /// <summary>
    /// 消费设备吐出的字节文本。只做行切分 + 关键字识别，不解析、不保存原文（原文归黑匣子管）。
    /// </summary>
    public void Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        long now = NowTicks();
        Interlocked.Add(ref _bytesSeen, chunk.Length);
        Interlocked.Exchange(ref _lastByteTicks, now);

        // 快速路径：整块里没有行结束符（串口分块边界与行边界无关）就只缓存，不摊平字符串。
        if (chunk.IndexOf('\n') < 0)
        {
            AppendToLineBuffer(chunk);
            return;
        }

        AppendToLineBuffer(chunk);

        // 一次 ToString 把缓冲摊平成字符串再切行：每块只分配一次，比逐行 ToString 干净。
        string pending = _lineBuffer.ToString();
        int consumed = 0;
        while (true)
        {
            int newline = pending.IndexOf('\n', consumed);
            if (newline < 0) break;
            ScanLine(pending.AsSpan(consumed, newline - consumed), now);
            consumed = newline + 1;
        }
        if (consumed == 0) return;
        _lineBuffer.Clear();
        _lineBuffer.Append(pending, consumed, pending.Length - consumed);
    }

    private void AppendToLineBuffer(string chunk)
    {
        _lineBuffer.Append(chunk);
        // 真出现这么长的"行"说明它没在按行输出：丢头保尾，绝不让缓冲无界增长。
        if (_lineBuffer.Length > MaxLineBuffer)
            _lineBuffer.Remove(0, _lineBuffer.Length - MaxLineBuffer / 2);
    }

    /// <summary>
    /// 每成功写出一帧后调用（50Hz 热路径）：必须是原子的、无锁、无大对象分配。
    /// 帧数与"期望字节数"只做自增，所以 <see cref="SerialLinkSnapshot.EchoRatio"/> 是粗粒度比值
    /// （分子含 banner 之类噪声）—— 它只用来看"回显还在不在"，不当精确计量。
    /// </summary>
    /// <param name="expectedBytes">这次写出去的字节数（含换行）。回显是逐字节回的。</param>
    public void NoteFrameWritten(int expectedBytes = 1)
    {
        long now = NowTicks();
        Interlocked.Increment(ref _framesWritten);
        Interlocked.Exchange(ref _lastFrameTicks, now);
        if (expectedBytes > 0) Interlocked.Add(ref _expectedBytes, expectedBytes);
    }

    /// <summary>当前结论（界面每秒取一次，导出故障包时也取一次）。</summary>
    public SerialLinkSnapshot Snapshot()
    {
        long now = NowTicks();
        int frames = Volatile.Read(ref _framesWritten);
        long lastByte = Interlocked.Read(ref _lastByteTicks);
        long lastEcho = Interlocked.Read(ref _lastEchoTicks);
        long lastHeartbeat = Interlocked.Read(ref _lastHeartbeatTicks);
        long lastEvidence = Interlocked.Read(ref _lastEvidenceTicks);
        long lastFrame = Interlocked.Read(ref _lastFrameTicks);
        long lastWatchdog = Interlocked.Read(ref _lastWatchdogTicks);
        long bytesSeen = Interlocked.Read(ref _bytesSeen);
        long expected = Interlocked.Read(ref _expectedBytes);

        // 三个时间基准各有各的意思，别混用（混用是上一版的真 bug，见下面注释）：
        //   sFrame = 我们最近有没有在发；sEcho = 我们的帧有没有被回；sHb = 固件执行器还活不活；sByte = 线上还有没有字节。
        double sFrame = SecondsBetween(now, lastFrame);
        double sEcho = SecondsBetween(now, lastEcho);
        double sHb = SecondsBetween(now, lastHeartbeat);
        double sByte = SecondsBetween(now, lastByte);

        bool hasEcho = lastEcho != 0 && sEcho < AliveWindow.TotalSeconds;
        bool hasHeartbeat = lastHeartbeat != 0 && sHb < HeartbeatWindow.TotalSeconds;

        bool writing = frames > 0 && sFrame < AliveWindow.TotalSeconds;
        bool beating = sHb < HeartbeatWindow.TotalSeconds;

        // ③ 线上彻底没字节了：这是唯一该让人拔插 USB 的情形（复位后的 ESP32 只能拔插）。
        bool dead = frames > 0 && sByte >= DeadWindow.TotalSeconds;
        // ② 的反面：正在发、而且真收到了回显。回显只可能由我们的帧触发，所以必须同时要求 writing——
        // 之前用「任何证据（含每秒心跳）在窗口内」判活，会把"发了没人回"也说成已确认，正好把故障盖住。
        bool alive = writing && sEcho < AliveWindow.TotalSeconds;
        // ① 没在发、但心跳还在：设备好好的，不是故障。用户停手不动不是毛病。
        bool idleOk = frames > 0 && !writing && beating;
        // ② 正在发、心跳还在（执行器跑得好好的）、却没有回显：字节没到设备或被别人占了端口。
        bool stalled = writing && !alive && beating;

        // 卡死：固件在刷看门狗告警、可执行器的心跳已经停了 —— 这不是「安静」，它是转不动了，
        // 只能硬复位（RTS 拉一下 EN），否则会一直刷告警刷到人拔线。
        double sWdt = SecondsBetween(now, lastWatchdog);
        bool wedged = lastWatchdog != 0 && sWdt < 10 && !beating;

        return new SerialLinkSnapshot(
            IsAlive: alive,
            IsDead: dead,
            HasHeartbeat: hasHeartbeat,
            HasEcho: hasEcho,
            BytesSeen: bytesSeen,
            ExpectedBytes: expected,
            EchoRatio: expected > 0 ? Math.Round(bytesSeen / (double)expected, 2) : 0,
            FramesWritten: frames,
            ResetSeenCount: Volatile.Read(ref _resetSeenCount),
            LastByteAtUtc: ToUtc(lastByte),
            LastHeartbeatAtUtc: ToUtc(lastHeartbeat),
            LastEchoAtUtc: ToUtc(lastEcho),
            SecondsSinceLastByte: sByte,
            SecondsSinceLastEcho: sEcho,
            SecondsSinceLastHeartbeat: sHb,
            SecondsSinceEvidence: SecondsBetween(now, lastEvidence),
            SecondsSinceSilence: Math.Min(sFrame, sByte),
            IsIdle: idleOk,
            IsStalled: stalled,
            IsWedged: wedged);
    }

    /// <summary>
    /// 给界面与故障包共用的一句话结论（只有一处文案，两边不会说岔）。
    /// 只有「线上一个字节都没有」才让人拔插 USB —— 空闲正常和有去无回都明确说出"先别拔"，
    /// 否则用户会把线拔在没问题的时候（那是比晚一秒报更贵的误伤）。
    /// </summary>
    public static string Describe(SerialLinkSnapshot snapshot)
    {
        if (snapshot.FramesWritten == 0)
            return "链路：待发帧 · 还没写过命令，设备安静属正常";

        if (snapshot.IsDead)
            return $"链路：{snapshot.SecondsSinceLastByte:0.0} 秒没有任何数据 · 设备可能已复位或线松，拔插 USB 后重连";

        if (snapshot.IsAlive)
            return $"链路：已确认 · {snapshot.SecondsSinceLastEcho:0.0} 秒前收到过回显";

        if (snapshot.IsIdle)
            return $"链路：空闲 · 设备心跳 {snapshot.SecondsSinceLastHeartbeat:0} 秒前还在 · 没在发命令，不算故障";

        if (snapshot.IsStalled)
            return $"链路：有去无回 · 设备还在跑（心跳 {snapshot.SecondsSinceLastHeartbeat:0} 秒前），但我们发出的帧没有回显 · 先别拔线，看看脚本或端口是否被占用";

        return "链路：待确认 · " + AgoText(snapshot.SecondsSinceLastByte, "还没收到过任何数据");
    }

    /// <summary>把"距今多少秒"变成一句人话；从没发生过时用调用方给的说法（绝不把 ∞ 打给用户看）。</summary>
    private static string AgoText(double seconds, string neverText)
        => double.IsFinite(seconds) ? $"{seconds:0.0} 秒无回话" : neverText;

    private void ScanLine(ReadOnlySpan<char> line, long nowTicks)
    {
        if (line.Length > 0 && line[^1] == '\r') line = line[..^1];   // ESP-IDF 的日志行是 CRLF 结尾
        if (line.Length == 0) return;

        if (Contains(line, WatchdogToken))
            Interlocked.Exchange(ref _lastWatchdogTicks, nowTicks);

        if (Contains(line, HeartbeatToken))
        {
            Interlocked.Exchange(ref _lastHeartbeatTicks, nowTicks);
            Interlocked.Exchange(ref _lastEvidenceTicks, nowTicks);
        }

        if (Contains(line, EchoToken) || Contains(line, ReceivedToken) || IsTCodeAcknowledgement(line))
        {
            Interlocked.Exchange(ref _lastEchoTicks, nowTicks);
            Interlocked.Exchange(ref _lastEvidenceTicks, nowTicks);
        }

        if (Contains(line, BootTokenAppMain) || Contains(line, BootTokenExecutor))
        {
            long lastReset = Interlocked.Read(ref _lastResetTicks);
            if (lastReset == 0 || new TimeSpan(nowTicks - lastReset) >= ResetDedupeWindow)
            {
                Interlocked.Increment(ref _resetSeenCount);
                Interlocked.Exchange(ref _lastResetTicks, nowTicks);
            }
            Interlocked.Exchange(ref _lastEvidenceTicks, nowTicks);
        }
    }

    /// <summary>设备对我们命令的直接应答（<c>D1</c> 这类）：身份探测收的就是它，同样算回显证据。</summary>
    private static bool IsTCodeAcknowledgement(ReadOnlySpan<char> line)
    {
        if (line.Length is < 2 or > 3) return false;
        if (line[0] is not ('D' or 'd')) return false;
        for (int i = 1; i < line.Length; i++)
            if (!char.IsAsciiDigit(line[i])) return false;
        return true;
    }

    private static bool Contains(ReadOnlySpan<char> line, string token)
        => line.IndexOf(token.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;

    private long NowTicks() => _clock().Ticks;

    /// <summary>距今多少秒；<paramref name="thenTicks"/> 为 0（还没发生过）时返回 +∞，由调用方决定怎么措辞。</summary>
    private static double SecondsBetween(long nowTicks, long thenTicks)
        => thenTicks == 0 ? double.PositiveInfinity : Math.Max(0, (nowTicks - thenTicks) / (double)TimeSpan.TicksPerSecond);

    private static DateTime? ToUtc(long ticks)
        => ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
}

/// <summary>某一时刻的链路结论（一次快照，值语义，可以随便传）。</summary>
public readonly record struct SerialLinkSnapshot(
    bool IsAlive,
    bool IsDead,
    bool HasHeartbeat,
    bool HasEcho,
    long BytesSeen,
    long ExpectedBytes,
    double EchoRatio,
    int FramesWritten,
    int ResetSeenCount,
    DateTime? LastByteAtUtc,
    DateTime? LastHeartbeatAtUtc,
    DateTime? LastEchoAtUtc,
    double SecondsSinceLastByte,
    double SecondsSinceLastEcho,
    /// <summary>距最后一次看到 <c>Executor: Stats</c> 多少秒（从没见过为 +∞）；界面文案要"心跳 N 秒前还在"。</summary>
    double SecondsSinceLastHeartbeat,
    double SecondsSinceEvidence,
    /// <summary>距"最后一次证据或最后一次写帧"里更近的那一个（判死与文案都用它，写过帧就一定有值）。</summary>
    double SecondsSinceSilence,
    /// <summary>① 空闲正常：没在发命令、心跳还在。不是故障。</summary>
    bool IsIdle,
    /// <summary>② 有去无回：在发、心跳还在、却没有回显 —— 字节没到设备，或被别的程序占了端口。</summary>
    bool IsStalled,
    /// <summary>③ 固件卡死：在刷看门狗告警、执行器心跳已停。只能硬复位。</summary>
    bool IsWedged = false);
