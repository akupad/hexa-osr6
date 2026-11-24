using System.IO;
using System.Text;

namespace Hexa.Services;

/// <summary>
/// 串口现场记录（异常断开取证专用）：把「发过什么 / 收到什么 / 设备自己吐的故障原文」按毫秒时间戳留在内存里，
/// 出问题时整段写进诊断包。
///
/// 与 <see cref="BlackBoxRecorder"/> 的分工：黑匣子是**限流抽样**（每几十毫秒一条、每条截断 120 字），
/// 用来一眼看懂"发生了什么"；这里是**全量轨迹**（最近 600 帧 + 500 行回话 + 全部故障行），
/// 用来事后逐帧对账——「断开前最后发出去的是什么」、「设备是不是重启过」、「回显在第几帧断的」，
/// 这三件事只有全量轨迹答得上。
///
/// 热路径纪律：入队一次、超容量出队一次，不落盘、不打印、不取 <see cref="_lock"/>（只取私有 _traceLock）；
/// 真正的字符串拼接与文件 IO 全在诊断包里做，绝不在 50Hz 发帧线程或串口读线程上。
/// </summary>
public sealed partial class SerialService
{
    private const int IncidentTxCapacity = 600;
    private const int IncidentRxCapacity = 500;
    private const int IncidentFaultCapacity = 200;

    /// <summary>单行上限：固件真出现这么长的"行"说明它没按行输出，宁可截断也不让内存无界。</summary>
    private const int IncidentLineMax = 400;

    /// <summary>设备侧自报故障的关键字（panic / 看门狗 / 断言 / 内存访问异常）。大小写敏感匹配原始输出。</summary>
    private static readonly string[] DeviceFaultTokens =
    [
        "rst:", "Guru Meditation", "Backtrace:", "panic", "Panic", "abort(", "assert",
        "task_wdt", "watchdog", "interrupt_wdt", "Load access fault", "Store access fault",
        "Illegal instruction", "Stack canary", "stack overflow", "CORRUPT HEAP",
    ];

    private readonly Lock _traceLock = new();
    private readonly Queue<(long Ms, string Text)> _incidentTx = new();
    private readonly Queue<(long Ms, string Text)> _incidentRx = new();
    private readonly Queue<(long Ms, string Text)> _incidentFaults = new();
    private readonly StringBuilder _incidentLineBuffer = new();

    /// <summary>断开现场快照：断开发生那一刻拼好，诊断包直接用这一份，不会被随后重连的新数据冲掉。</summary>
    private string? _pendingIncidentTrace;

    /// <summary>记一帧写出去的原始文本（发帧线程调用，50Hz）。</summary>
    internal void NoteTxTrace(string frame)
    {
        long ms = Environment.TickCount64;
        string text = frame.Length <= IncidentLineMax ? frame : frame[..IncidentLineMax];
        lock (_traceLock)
        {
            _incidentTx.Enqueue((ms, text));
            while (_incidentTx.Count > IncidentTxCapacity) _incidentTx.Dequeue();
        }
    }

    /// <summary>
    /// 记一段串口收到的原文（读线程调用）。按行切：不完整的尾巴留在缓冲里等下一次，
    /// 这样"一行被分两次读到"不会在轨迹里变成两条半行。
    /// </summary>
    internal void NoteRxTrace(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        long ms = Environment.TickCount64;
        lock (_traceLock)
        {
            _incidentLineBuffer.Append(chunk);
            string pending = _incidentLineBuffer.ToString();
            int consumed = 0;
            while (true)
            {
                int newline = pending.IndexOf('\n', consumed);
                if (newline < 0) break;
                PushRxLineLocked(pending.AsSpan(consumed, newline - consumed), ms);
                consumed = newline + 1;
            }
            _incidentLineBuffer.Clear();
            if (consumed < pending.Length) _incidentLineBuffer.Append(pending, consumed, pending.Length - consumed);
            // 没有换行的长流（固件卡在打印半个行）也要能留下证据：超过上限就直接截断入队。
            if (_incidentLineBuffer.Length > IncidentLineMax * 4)
            {
                PushRxLineLocked(_incidentLineBuffer.ToString().AsSpan(), ms);
                _incidentLineBuffer.Clear();
            }
        }
    }

    private void PushRxLineLocked(ReadOnlySpan<char> line, long ms)
    {
        if (line.Length == 0) return;
        string text = line.Length <= IncidentLineMax ? line.ToString() : line[..IncidentLineMax].ToString();
        text = text.TrimEnd('\r');
        if (text.Length == 0) return;

        _incidentRx.Enqueue((ms, text));
        while (_incidentRx.Count > IncidentRxCapacity) _incidentRx.Dequeue();

        foreach (string token in DeviceFaultTokens)
        {
            if (text.IndexOf(token, StringComparison.Ordinal) < 0) continue;
            _incidentFaults.Enqueue((ms, text));
            while (_incidentFaults.Count > IncidentFaultCapacity) _incidentFaults.Dequeue();
            break;
        }
    }

    /// <summary>
    /// 拼一份完整的串口现场文本。断开时**在锁外、在事件线程上**调一次并缓存，
    /// 诊断包优先用缓存的那份（<see cref="TakeIncidentTrace"/>），否则现拼一份。
    /// </summary>
    public string BuildIncidentText(string kind, string detail, Exception? exception = null)
    {
        SerialLinkSnapshot link = LinkMonitor.Snapshot();
        var sb = new StringBuilder(24 * 1024);

        sb.AppendLine("# hexa-serial-trace-v1");
        sb.AppendLine("# 这是「异常断开」的逐条现场：发送轨迹 / 接收轨迹 / 设备自报故障 / 链路判定。");
        sb.AppendLine();
        sb.AppendLine("## 事件");
        sb.AppendLine("时间(本地): " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.AppendLine("时间(UTC): " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.AppendLine("类型: " + kind);
        sb.AppendLine("说明: " + (detail ?? "（无）"));
        sb.AppendLine("端口: " + (IsSimulation ? "（模拟设备）" : (string.IsNullOrEmpty(PortName) ? "（未连接）" : PortName)));
        sb.AppendLine("输出已解锁: " + (OutputEnabled ? "是" : "否"));
        sb.AppendLine("读线程: " + (IsReaderActive ? "运行中" : "未运行"));
        sb.AppendLine("连续失败: " + _consecutiveFailures);
        sb.AppendLine("最近错误: " + (LastError ?? "（无）"));
        sb.AppendLine("累计写出帧: " + link.FramesWritten + " · 收到字节: " + link.BytesSeen +
                      " · 回显/发出比: " + link.EchoRatio.ToString("0.00"));
        sb.AppendLine("见过重启标记: " + link.ResetSeenCount + " 次");
        sb.AppendLine("距最后收字节: " + link.SecondsSinceLastByte.ToString("0.000") + " 秒" +
                      " · 距最后回显: " + link.SecondsSinceLastEcho.ToString("0.000") + " 秒" +
                      " · 距最后心跳: " + link.SecondsSinceLastHeartbeat.ToString("0.000") + " 秒");
        sb.AppendLine("链路结论: " + SerialLinkMonitor.Describe(link));
        sb.AppendLine("链路标志: alive=" + link.IsAlive + " dead=" + link.IsDead +
                      " stalled=" + link.IsStalled + " wedged=" + link.IsWedged + " idle=" + link.IsIdle);
        if (exception != null)
        {
            sb.AppendLine();
            sb.AppendLine("## 异常原文（含调用栈）");
            sb.AppendLine(exception.ToString());
        }

        (long Ms, string Text)[] tx, rx, faults;
        lock (_traceLock)
        {
            tx = _incidentTx.ToArray();
            rx = _incidentRx.ToArray();
            faults = _incidentFaults.ToArray();
        }
        long now = Environment.TickCount64;

        sb.AppendLine();
        sb.AppendLine("## 发送轨迹（最近 " + tx.Length + " 帧，时间=距现在多少毫秒之前）");
        if (tx.Length == 0) sb.AppendLine("（无）");
        foreach ((long ms, string text) in tx)
            sb.AppendLine("-" + (now - ms).ToString("D7") + "ms  " + text);

        sb.AppendLine();
        sb.AppendLine("## 接收轨迹（最近 " + rx.Length + " 行原文）");
        if (rx.Length == 0) sb.AppendLine("（无）");
        foreach ((long ms, string text) in rx)
            sb.AppendLine("-" + (now - ms).ToString("D7") + "ms  " + text);

        sb.AppendLine();
        sb.AppendLine("## 设备自报故障行（" + faults.Length + " 行，panic / 看门狗 / 断言 / 内存异常）");
        if (faults.Length == 0) sb.AppendLine("（这一轮没有出现）");
        foreach ((long ms, string text) in faults)
            sb.AppendLine("-" + (now - ms).ToString("D7") + "ms  " + text);

        sb.AppendLine();
        sb.AppendLine("## 黑匣子（限流抽样，最近 " + BlackBox.Count + " 条）");
        foreach (BlackBoxEntry entry in BlackBox.Snapshot())
            sb.AppendLine(entry.AtUtc.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + entry.Channel + " " + entry.Text);

        return sb.ToString();
    }

    /// <summary>
    /// 断开瞬间把现场拼好存起来（在锁外、事件线程上调用一次，约 10–20 KB 字符串，不是热路径）。
    /// 拼失败也绝不能影响断开流程本身。
    /// </summary>
    internal void StashIncidentTrace(string kind, string detail)
    {
        try { Volatile.Write(ref _pendingIncidentTrace, BuildIncidentText(kind, detail)); }
        catch { /* 取证失败不影响断线处理 */ }
    }

    /// <summary>
    /// 诊断包取用现场：优先用断开那一刻缓存的快照（不被后续重连数据污染），没有就现拼一份。
    /// 取走即清空，避免同一份快照被反复写进多个包。
    /// </summary>
    public string TakeIncidentTrace(string kind, string detail)
        => Volatile.Read(ref _pendingIncidentTrace) is string pending && Interlocked.Exchange(ref _pendingIncidentTrace, null) is not null
            ? pending
            : BuildIncidentText(kind, detail);
}
