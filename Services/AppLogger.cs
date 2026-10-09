using System.IO;

namespace Hexa.Services;

/// <summary>
/// 简单的线程安全文件日志系统，写到 %LocalAppData%\Hexa\app.log。
///
/// 磁盘占用有硬上限：写之前先看文件多大，超过 <c>AppSettings.LogMaxMegabytes</c> 就把
/// app.log 轮转成 app.log.1（只留一代）。以前只 append，长时间挂着软件日志会一直长，
/// 用户既不知道该删也不知道能删 —— 现在最多就是「上限 × 2」。
/// </summary>
public static class AppLogger
{
    /// <summary>设置读不到时用的兜底上限（MB；与 AppSettings.LogMaxMegabytes 的默认值一致）。</summary>
    private const int DefaultMaxMegabytes = 10;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hexa", "app.log");

    /// <summary>轮转目标：上一代日志。只留一代，简单可靠、不会越滚越多。</summary>
    private static readonly string RotatedPath = LogPath + ".1";

    private static readonly Lock _lock = new();

    /// <summary>
    /// 轮转失败只提示一次。**不能**在轮转失败时调用 Write()：那时正持有 _lock，
    /// 递归进来会自锁；所以只用标志位压住后续重复提示。
    /// </summary>
    private static bool _rotateWarned;

    static AppLogger()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); }
        catch { /* 目录创建失败时静默忽略，不能让日志系统崩溃主程序 */ }
    }

    /// <summary>日志文件路径（供内置日志查看器读取）。</summary>
    public static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hexa", "app.log");

    /// <summary>上一代日志路径（轮转后的 app.log.1）：设置页用它说明「旧内容去哪了」。</summary>
    public static string RotatedLogFilePath => RotatedPath;

    public static void Info(string msg)  => Write("INFO ", msg);
    public static void Warn(string msg)  => Write("WARN ", msg);
    /// <summary>
    /// 记一条错误。**带异常时必须记完整堆栈**（<c>ex.ToString()</c>）：
    /// 以前只记 `类型: 消息`，崩溃日志里连在哪一行挂的都不知道 —— 等于没有线索。
    /// 堆栈有多行：缩进后逐行写入，日志查看器照旧逐行显示。
    /// </summary>
    public static void Error(string msg, Exception? ex = null)
    {
        if (ex == null)
        {
            Write("ERROR", msg);
            return;
        }
        string indent = new string(' ', 7);
        string[] lines = ex.ToString().Split('\n');
        var builder = new System.Text.StringBuilder(msg);
        for (int i = 0; i < lines.Length; i++)
            builder.Append(i == 0 ? " | " : "\n" + indent + "  ").Append(lines[i].TrimEnd('\r'));
        Write("ERROR", builder.ToString());
    }

    /// <summary>当前日志文件的字节数（设置页显示「日志现在多大」用）；文件不存在时返回 0。</summary>
    public static long CurrentBytes()
    {
        try
        {
            var info = new FileInfo(LogPath);
            return info.Exists ? info.Length : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 清空日志（设置页「清空日志」按钮用）：当前 app.log 和上一代 app.log.1 一起删掉。
    /// 两个都删，是因为用户点「清空」的意思就是「这些记录我不要了」——
    /// 只删当前文件的话，上一代里同样的内容还留在磁盘上，等于没清干净。
    /// 返回 false = 至少有一个文件没删掉（被别的程序占着 / 没有写权限）。
    /// </summary>
    public static bool Clear()
    {
        bool ok = true;
        lock (_lock)
        {
            foreach (string path in new[] { LogPath, RotatedPath })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { ok = false; }
            }
        }
        return ok;
    }

    /// <summary>读取日志文件最近 count 行（不含空白行），供内置日志查看器使用。</summary>
    public static IReadOnlyList<string> ReadRecentLines(int count)
    {
        var result = new List<string>();
        try
        {
            if (!System.IO.File.Exists(LogPath)) return result;
            foreach (string line in System.IO.File.ReadLines(LogPath).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                result.Add(line);
                if (result.Count >= count) break;
            }
            result.Reverse();
        }
        catch { }
        return result;
    }

    private static void Write(string level, string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}";
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
                // 「先写、后轮转」而不是反过来：这一行一定会落进 app.log，
                // 于是任何按「写前 / 写后文件大小」判断日志有没有落盘的检查都不会因为
                // 正好赶上轮转而误判成失败（自检里就有这样一项）。上限最多被超出一行。
                RotateIfTooBig();
            }
        }
        catch { /* 日志写入失败绝不能抛出异常影响主逻辑 */ }
    }

    /// <summary>
    /// 超过上限就把 app.log 挪成 app.log.1（旧的 .1 直接被覆盖）。
    /// 调用方必须已持有 <see cref="_lock"/> —— 「量大小」和「搬文件」之间不能有别人插进来，
    /// 否则两个线程会同时判定该轮转、互相把对方刚写的日志搬走。
    /// </summary>
    private static void RotateIfTooBig()
    {
        long limit = MaxLogBytes();
        if (limit <= 0) return;                 // 0 = 用户显式关掉了上限，永远不轮转
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < limit) return;
            if (File.Exists(RotatedPath)) File.Delete(RotatedPath);
            File.Move(LogPath, RotatedPath);
            _rotateWarned = false;              // 恢复正常后，下次真出问题还能再提示一次
        }
        catch (Exception ex)
        {
            // 轮转失败（文件被占用 / 没权限）时不能让日志系统自己把程序拖死：
            // 这里只能直接追加一行说明，并且只提示一次；之后日志继续增长 ——
            // 少一个体积限制，总比丢日志或者递归写日志强。
            if (_rotateWarned) return;
            _rotateWarned = true;
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN ] 日志轮转失败（{ex.Message}）："
                    + $"日志会继续增长，可在设置页点「清空日志」。{Environment.NewLine}");
            }
            catch { }
        }
    }

    /// <summary>上限字节数；0 = 不限制。设置还没加载好（App.Settings 为 null）时退回默认 10 MB。</summary>
    private static long MaxLogBytes()
    {
        int megabytes = DefaultMaxMegabytes;
        try { megabytes = App.Settings?.LogMaxMegabytes ?? DefaultMaxMegabytes; }
        catch { /* 设置对象坏掉也照样要能记日志：用默认上限 */ }
        return megabytes <= 0 ? 0 : megabytes * 1024L * 1024L;
    }
}
