using System.Text;
using System.Windows.Forms;
using Hexa.Services;

namespace Hexa.Diagnostics;

/// <summary>
/// 异常停顿检测：区分「功能主动停」和「该动却没动」。
/// 每 250ms 判一次；串口开着 + 输出闸门开着 + 有东西认为应该在动 + 距上次发帧 > 300ms = 停顿候选。
/// 候选再分功能性（急停/缓降/跟随未要求驱动）与异常（发帧线程卡住/串口写阻塞/定时器饿死）。
/// >1 秒的异常停顿自动留档（限频 2 分钟）。日志以「停顿：」开头，方便 grep。
/// </summary>
public static class StallDetector
{
    private static readonly object Gate = new();
    private static System.Threading.Timer? _timer;
    private static bool _started;

    private const long HostStallThresholdMs = 300;
    /// <summary>串口处于空闲降载（每秒一帧心跳）时的停顿阈值：必须大于心跳周期，否则每拍都误报。</summary>
    private const long IdleHostStallThresholdMs = 1800;
    private const long DeviceNoRespThresholdMs = 2000;
    private const long ArchiveIntervalMs = 2 * 60 * 1000L;
    private const long WindowMs = 10 * 60 * 1000L;

    // 当前停顿候选（-1 = 无）
    private static long _stallSinceTicks = -1;
    private static DateTime _stallStartUtc;
    private static string _stallDriver = "";
    private static int _stallStartFailures;
    private static long _stallLastLogMs;
    private static bool _stallFunctional;

    // 设备侧无回应
    private static long _deviceSilentSinceMs = -1;
    private static long _deviceSilentLastLogMs;

    // 滚动统计（最近 10 分钟已结束的停顿）
    private static readonly List<(long EndTicks, long DurationMs, bool Functional)> _recent = new();

    private static long _lastArchiveMs;

    public static bool IsRunning => _started;

    public static void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
            _timer = new System.Threading.Timer(_ => Tick(), null, 250, 250);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (!_started) return;
            _started = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>最近 10 分钟停顿汇总，供 status.txt 与设置页显示。</summary>
    public static string SnapshotText()
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;
            _recent.RemoveAll(r => now - r.EndTicks > WindowMs);
            int functional = _recent.Count(r => r.Functional);
            int abnormal = _recent.Count(r => !r.Functional);
            long max = 0;
            foreach (var r in _recent) if (r.DurationMs > max) max = r.DurationMs;
            return "功能性 " + functional + " 次 / 异常 " + abnormal + " 次 / 最长 " + max + "ms";
        }
    }

    private static void Tick()
    {
        try { Evaluate(Environment.TickCount64); }
        catch { /* 诊断线程绝不让异常逃逸 */ }
    }

    private static void Evaluate(long now)
    {
        var serial = App.Serial;
        var engine = App.Engine;
        var rules = App.RuleEngine;
        if (serial == null || engine == null || rules == null) return;

        bool portOpen = serial.IsOpen;
        bool gateOpen = serial.OutputEnabled;
        long lastWrite = serial.LastWriteAtTicks;

        bool followActive = ScreenWatchService.Current?.FollowActive ?? false;
        bool shouldMove = engine.IsRunning || rules.Active || followActive;

        // 空闲降载时设备每秒才收到一帧心跳，用 300ms 判停顿必然误报 —— 这时改用 1.8 秒。
        long stallThreshold = serial.IsIdlePacing ? IdleHostStallThresholdMs : HostStallThresholdMs;
        bool hostStalled = portOpen && gateOpen && shouldMove && lastWrite != 0 &&
                           now - lastWrite > stallThreshold;

        if (hostStalled)
        {
            if (_stallSinceTicks < 0)
            {
                _stallSinceTicks = now;
                _stallStartUtc = DateTime.UtcNow;
                _stallDriver = DescribeDriver(engine, rules, followActive);
                _stallStartFailures = serial.ConsecutiveFailures;
                _stallLastLogMs = 0;
            }
            long duration = now - _stallSinceTicks;
            bool functional = IsFunctional(engine, rules, followActive);
            _stallFunctional = functional;

            if (duration >= stallThreshold && now - _stallLastLogMs >= 1000)
            {
                _stallLastLogMs = now;
                string type = functional ? "功能性" : "异常";
                string reason = functional ? FunctionalReason(engine, rules, followActive)
                                           : "发帧线程卡住/串口写阻塞/定时器饿死";
                string line = "停顿：类型=" + type + " 持续=" + duration + "ms 驱动=" + _stallDriver + " 原因=" + reason;
                if (!functional && duration > 1000) AppLogger.Error(line);
                else if (functional) AppLogger.Info(line);
                else AppLogger.Warn(line);
            }

            if (!functional && duration > 1000)
            {
                long since = now - _lastArchiveMs;
                if (_lastArchiveMs == 0 || since >= ArchiveIntervalMs)
                {
                    _lastArchiveMs = now;
                    _ = Task.Run(ArchiveStall);
                }
            }
        }
        else if (_stallSinceTicks >= 0)
        {
            // 停顿结束
            long total = now - _stallSinceTicks;
            if (total > (serial.IsIdlePacing ? IdleHostStallThresholdMs : HostStallThresholdMs))
            {
                RecordEnd(now, total, _stallFunctional);
                if (!_stallFunctional && total > 1000)
                    LogStallEnd(serial, engine, total);
            }
            _stallSinceTicks = -1;
        }

        EvaluateDeviceSilence(now, serial, portOpen, gateOpen, lastWrite);
    }

    private static bool IsFunctional(MotionEngine engine, RuleEngine rules, bool followActive)
    {
        if (engine.EmergencyStopped) return true;
        if (engine.IsEasing || rules.Transitioning) return true;
        if (followActive && ScreenWatchService.Current?.FollowWantsDevice == false) return true;
        return false;
    }

    private static string FunctionalReason(MotionEngine engine, RuleEngine rules, bool followActive)
    {
        if (engine.EmergencyStopped) return "急停按下";
        if (engine.IsEasing) return "动作引擎缓降";
        if (rules.Transitioning) return "规则引擎主动缓降";
        if (followActive && ScreenWatchService.Current?.FollowWantsDevice == false) return "跟随目标消失/切走";
        return "功能主动停";
    }

    private static string DescribeDriver(MotionEngine engine, RuleEngine rules, bool followActive)
    {
        if (followActive) return "画面同步";
        if (rules.Active) return "规则引擎(伴随)";
        string d = engine.DriverLabel;
        return string.IsNullOrEmpty(d) ? "未知" : d;
    }

    private static void RecordEnd(long endTicks, long duration, bool functional)
    {
        lock (Gate)
        {
            _recent.Add((endTicks, duration, functional));
            long now = Environment.TickCount64;
            _recent.RemoveAll(r => now - r.EndTicks > WindowMs);
        }
    }

    private static void LogStallEnd(SerialService serial, MotionEngine engine, long total)
    {
        var lastCmd = serial.BlackBox.Snapshot().LastOrDefault(e => e.Channel == "out");
        string last = lastCmd.Channel == null ? "(无)" : lastCmd.Text;
        bool writeFailed = serial.ConsecutiveFailures > _stallStartFailures || serial.LastError != null || !serial.OutputEnabled;
        string line = "停顿：类型=异常 持续=" + total + "ms 驱动=" + _stallDriver +
                      " 开始=" + _stallStartUtc.ToLocalTime().ToString("HH:mm:ss") +
                      " 结束=" + DateTime.Now.ToString("HH:mm:ss") +
                      " 最后指令=" + last +
                      " 闸门=" + (serial.OutputEnabled ? "开" : "关") +
                      " 急停=" + (engine.EmergencyStopped ? "是" : "否") +
                      " 串口错误计数=" + serial.ConsecutiveFailures +
                      " 期间写失败=" + (writeFailed ? "是" : "否");
        AppLogger.Error(line);
    }

    private static void EvaluateDeviceSilence(long now, SerialService serial, bool portOpen, bool gateOpen, long lastWrite)
    {
        bool recentlyWriting = lastWrite != 0 && now - lastWrite < 1000;
        long sinceRx = serial.LinkMonitor.LastByteAtUtc is DateTime lastRx
            ? (long)(DateTime.UtcNow - lastRx).TotalMilliseconds
            : long.MaxValue;
        bool silent = portOpen && gateOpen && recentlyWriting && sinceRx > DeviceNoRespThresholdMs;

        if (silent)
        {
            if (_deviceSilentSinceMs < 0) { _deviceSilentSinceMs = now; _deviceSilentLastLogMs = 0; }
            if (now - _deviceSilentLastLogMs >= 10000)
            {
                _deviceSilentLastLogMs = now;
                AppLogger.Warn("停顿：类型=设备无回应 持续=" + (now - _deviceSilentSinceMs) + "ms 驱动=设备侧 原因=串口在写但超2秒没收到设备数据");
            }
        }
        else
        {
            _deviceSilentSinceMs = -1;
        }
    }

    private static void ArchiveStall()
    {
        try
        {
            string path = DiagnosticReport.Create("stall");
            if (path.Length == 0) return;
            AppLogger.Info("诊断报告已留存：" + path);
            try { App.Dispatch(() => App.TrayIcon?.ShowBalloonTip(5000, "诊断报告已留存", path, ToolTipIcon.Info)); }
            catch { }
        }
        catch (Exception ex) { AppLogger.Warn("停顿自动留档失败：" + ex.Message); }
    }
}
