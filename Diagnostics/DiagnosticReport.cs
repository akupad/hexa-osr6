using System.Text;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Ports;
using Hexa.Services;
using Hexa.Models;

namespace Hexa.Diagnostics;

/// <summary>
/// 一键诊断报告：把运行日志、设置、系统信息、运行状态、黑匣子、时间线、串口占用线索
/// 打成一个 zip。用户出问题时第一时间把证据抓住。报告只落本地，绝不外发。
/// </summary>
public static class DiagnosticReport
{
    private static readonly string[] TimelineKeywords =
        { "断开", "连接", "重启", "急停", "看门狗", "失败", "异常", "限位" };

    /// <summary>生成诊断 zip。成功返回绝对路径，失败返回空字符串（并记日志）。</summary>
    public static string Create(string reason)
    {
        string? staging = null;
        try
        {
            string tag = SanitizeTag(reason);
            string zipPath = BuildZipPath(tag);
            staging = Path.Combine(Path.GetTempPath(), "hexa-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            WriteSystemTxt(Path.Combine(staging, "system.txt"));
            WriteStatusTxt(Path.Combine(staging, "status.txt"));
            WriteTimelineTxt(Path.Combine(staging, "timeline.txt"));
            WritePortsTxt(Path.Combine(staging, "ports.txt"));

            try { App.Serial.BlackBox.Export(Path.Combine(staging, "blackbox.txt")); }
            catch (Exception ex) { AppLogger.Warn("诊断：导出黑匣子失败 " + ex.Message); }

            // 异常断开的逐条现场：发送轨迹 / 接收轨迹 / 设备自报故障 / 链路判定。
            // 断开瞬间的快照优先（不被重连数据污染），没有快照就现拼一份当前状态。
            WriteSerialTraceTxt(Path.Combine(staging, "serial-trace.txt"), reason);

            CopyFileIfExists(AppLogger.LogFilePath, Path.Combine(staging, "app.log"));
            CopyFileIfExists(AppLogger.RotatedLogFilePath, Path.Combine(staging, "app.log.1"));
            CopyFileIfExists(AppSettings.SettingsFilePath, Path.Combine(staging, "settings.json"));

            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            // 串口现场同时落一份裸 txt 放在 zip 旁边：出问题时不用解压，直接双击就能看/发给我。
            try
            {
                string traceSrc = Path.Combine(staging, "serial-trace.txt");
                if (File.Exists(traceSrc))
                    File.Copy(traceSrc, Path.ChangeExtension(zipPath, ".serial.txt"), overwrite: true);
            }
            catch (Exception ex) { AppLogger.Warn("诊断：另存串口现场失败 " + ex.Message); }

            return zipPath;
        }
        catch (Exception ex)
        {
            AppLogger.Error("生成诊断报告失败", ex);
            return "";
        }
        finally
        {
            if (staging != null)
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }
    }

    private static string SanitizeTag(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) reason = "unknown";
        var sb = new StringBuilder();
        foreach (char c in reason)
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    private static string BuildZipPath(string tag)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string name = "HexaReport-" + stamp + "-" + tag + ".zip";
        string primary = Path.Combine("D:/用户文件/Desktop/工作交付/Hexa诊断", name);
        try
        {
            string dir = Path.GetDirectoryName(primary)!;
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return primary;
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hexa", "reports", name);
        }
    }

    /// <summary>报告主目录（可写时）或本地回退目录，供「打开报告目录」按钮使用。</summary>
    public static string GetReportDirectory()
    {
        string primary = "D:/用户文件/Desktop/工作交付/Hexa诊断";
        try
        {
            Directory.CreateDirectory(primary);
            string probe = Path.Combine(primary, ".probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return primary;
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hexa", "reports");
        }
    }

    /// <summary>
    /// 写串口现场轨迹。serial 只在单元测试里显式传入；App.Serial 在极早期还没建好时也走这条路，
    /// 宁可只落一句"当时还没有串口实例"，也不要因为一个空引用让整包诊断生不出来。
    /// </summary>
    internal static void WriteSerialTraceTxt(string path, string reason, Services.SerialService? serial = null)
    {
        try
        {
            serial ??= App.Serial;
            string text = serial is null
                ? "# hexa-serial-trace-v1\n（生成这份诊断时还没有串口实例，没有轨迹可写）\n"
                : serial.TakeIncidentTrace(reason, "诊断包现场：" + reason);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception ex) { AppLogger.Warn("诊断：导出串口现场轨迹失败 " + ex.Message); }
    }

    private static void WriteSystemTxt(string path)
    {
        var sb = new StringBuilder();
        var proc = Process.GetCurrentProcess();
        sb.AppendLine("操作系统: " + RuntimeInformation.OSDescription);
        sb.AppendLine(".NET 运行时: " + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("进程位数: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") +
                      " / 系统 " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
        sb.AppendLine("机器名: " + Environment.MachineName);
        sb.AppendLine("CPU 核数: " + Environment.ProcessorCount);
        long totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        sb.AppendLine("内存(系统可用给进程GC): " + (totalMem / 1024 / 1024) + " MB");
        sb.AppendLine("Hexa 进程 PID: " + proc.Id);
        try { sb.AppendLine("Hexa 启动时间: " + proc.StartTime.ToString("yyyy-MM-dd HH:mm:ss")); }
        catch { sb.AppendLine("Hexa 启动时间: 不可用"); }
        sb.AppendLine("Hexa 工作集: " + (proc.WorkingSet64 / 1024 / 1024) + " MB");
        sb.AppendLine("当前时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.AppendLine("时区: " + TimeZoneInfo.Local.Id + " (" + TimeZoneInfo.Local.DisplayName + ")");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteStatusTxt(string path)
    {
        var sb = new StringBuilder();
        var serial = App.Serial;
        var engine = App.Engine;
        var rules = App.RuleEngine;
        var settings = App.Settings;

        try
        {
            sb.AppendLine("[串口]");
            sb.AppendLine("是否打开: " + serial.IsOpen);
            sb.AppendLine("端口名: " + (string.IsNullOrEmpty(serial.PortName) ? "(空)" : serial.PortName));
            sb.AppendLine("是否模拟: " + serial.IsSimulation);
            sb.AppendLine("已发指令数(链路监控帧数): " + serial.LinkMonitor.FramesWritten);
            sb.AppendLine("串口连续失败计数: " + serial.ConsecutiveFailures);
            sb.AppendLine("最近写帧时刻Ticks: " + serial.LastWriteAtTicks);
            var lastCmd = serial.BlackBox.Snapshot().LastOrDefault(e => e.Channel == "out");
            sb.AppendLine("最后一条指令: " + (lastCmd.Channel == null ? "(无)" : lastCmd.Text));
        }
        catch (Exception ex) { sb.AppendLine("[串口] 读取失败: " + ex.Message); }

        try
        {
            sb.AppendLine("[急停/输出]");
            sb.AppendLine("急停状态: " + engine.EmergencyStopped);
            sb.AppendLine("输出闸门 OutputEnabled: " + serial.OutputEnabled);
            sb.AppendLine("输出锁期间丢弃指令数: " + serial.DroppedWhileLocked);
            sb.AppendLine("当前驱动标签: " + engine.DriverLabel);
            sb.AppendLine("MotionEngine 在跑: " + engine.IsRunning);
        }
        catch (Exception ex) { sb.AppendLine("[急停/输出] 读取失败: " + ex.Message); }

        try
        {
            sb.AppendLine("[各轴位置与限位]");
            double[] amp = engine.GetAxisAmp();
            string[] axes = Osr6DeviceProfile.InstalledAxes;
            for (int i = 0; i < axes.Length; i++)
            {
                string ax = axes[i];
                settings.AxisMin.TryGetValue(ax, out int mn);
                settings.AxisMax.TryGetValue(ax, out int mx);
                settings.EffectiveAxisMin.TryGetValue(ax, out int emn);
                settings.EffectiveAxisMax.TryGetValue(ax, out int emx);
                double a = i < amp.Length ? amp[i] : 0;
                sb.AppendLine(ax + ": 限位[" + mn + ".." + mx + "] 生效限位[" + emn + ".." + emx + "] 当前幅度 " + a.ToString("F1") + "%");
            }
            sb.AppendLine("限位开关 AxisLimitsEnabled: " + settings.AxisLimitsEnabled);
        }
        catch (Exception ex) { sb.AppendLine("[各轴位置与限位] 读取失败: " + ex.Message); }

        try
        {
            var sw = ScreenWatchService.Current;
            sb.AppendLine("[画面同步]");
            sb.AppendLine("enabled(设置): " + settings.ScreenWatchEnabled);
            sb.AppendLine("running: " + (sw?.IsRunning ?? false));
            sb.AppendLine("active: " + (sw?.FollowActive ?? false));
            sb.AppendLine("depth: " + (sw?.FollowDepth ?? 0).ToString("F2"));
            sb.AppendLine("hz: " + (sw?.FollowHz ?? 0).ToString("F2"));
            sb.AppendLine("reason: " + (sw?.FollowReason ?? "(无)"));
            sb.AppendLine("姿态人数(PoseDiagnostics.Person): " + (sw?.PoseDiagnostics.Person ?? 0));
        }
        catch (Exception ex) { sb.AppendLine("[画面同步] 读取失败: " + ex.Message); }

        try
        {
            sb.AppendLine("[规则引擎]");
            sb.AppendLine("Active: " + rules.Active);
            sb.AppendLine("当前进程: " + (string.IsNullOrEmpty(rules.CurrentProcessName) ? "(空)" : rules.CurrentProcessName));
            sb.AppendLine("目标模式: " + rules.TargetMode);
            sb.AppendLine("目标激活: " + rules.TargetActive);
            sb.AppendLine("遥测激活: " + rules.TelemetryActive);
            sb.AppendLine("缓降中: " + rules.Transitioning);
            sb.AppendLine("最近切换: " + rules.LastTransition);
            sb.AppendLine("同步质量: " + rules.SyncQuality);
        }
        catch (Exception ex) { sb.AppendLine("[规则引擎] 读取失败: " + ex.Message); }

        try
        {
            sb.AppendLine("[AutoTwist]");
            sb.AppendLine("AutoTwistEnabled: " + settings.AutoTwistEnabled);
            sb.AppendLine("AutoTwistAmplitudePercent: " + settings.AutoTwistAmplitudePercent);
        }
        catch (Exception ex) { sb.AppendLine("[AutoTwist] 读取失败: " + ex.Message); }

        try
        {
            sb.AppendLine("[停顿检测]");
            sb.AppendLine(StallDetector.SnapshotText());
        }
        catch (Exception ex) { sb.AppendLine("[停顿检测] 读取失败: " + ex.Message); }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteTimelineTxt(string path)
    {
        var sb = new StringBuilder();
        try
        {
            var lines = ReadAllLinesShared(AppLogger.LogFilePath);
            var matches = new List<string>();
            foreach (var line in lines)
            {
                if (TimelineKeywords.Any(k => line.Contains(k, StringComparison.Ordinal)))
                    matches.Add(line);
            }
            int take = Math.Min(200, matches.Count);
            for (int i = matches.Count - take; i < matches.Count; i++)
                sb.AppendLine(matches[i]);
            if (take == 0)
                sb.AppendLine("(未在 app.log 中找到含关键词的行)");
        }
        catch (Exception ex) { sb.AppendLine("读取时间线失败: " + ex.Message); }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WritePortsTxt(string path)
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine("Hexa 当前端口: " + (string.IsNullOrEmpty(App.Serial.PortName) ? "(未连接)" : App.Serial.PortName));
            try
            {
                string[] ports = SerialPort.GetPortNames();
                sb.AppendLine("系统枚举串口: " + (ports.Length == 0 ? "(无)" : string.Join(", ", ports)));
            }
            catch (Exception ex) { sb.AppendLine("系统枚举串口失败: " + ex.Message); }
            try
            {
                var competing = SerialService.DetectCompetingApps();
                sb.AppendLine("疑似占用串口的竞争进程(白名单 MultiFunPlayer/intiface/osr6_bridge): " +
                              (competing.Count == 0 ? "(无)" : string.Join(", ", competing)));
            }
            catch (Exception ex) { sb.AppendLine("竞争进程检测失败: " + ex.Message); }
            sb.AppendLine("说明: 若设备端口被别的程序占用，Hexa 打不开串口。上面列表若有 MultiFunPlayer / Intiface / osr6_bridge，先退出它们再重连。");
        }
        catch (Exception ex) { sb.AppendLine("读取端口占用线索失败: " + ex.Message); }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void CopyFileIfExists(string src, string dst)
    {
        try
        {
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return;
            using var inFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var outFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
            inFs.CopyTo(outFs);
        }
        catch (Exception ex) { AppLogger.Warn("诊断：复制 " + src + " 失败 " + ex.Message); }
    }

    private static List<string> ReadAllLinesShared(string path)
    {
        var result = new List<string>();
        if (!File.Exists(path)) return result;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        while (sr.ReadLine() is { } line) result.Add(line);
        return result;
    }
}
