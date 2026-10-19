using System.IO;
using System.Text;

namespace Hexa.Services;

/// <summary>
/// 黑匣子：定容环形缓冲 + 纯文本导出。用途是"出问题了把最近一分钟的现场交出来"——
/// 之前排查串口故障只能靠 app.log，而热路径的帧、设备回话、状态切换全都不在里面。
///
/// 为什么是环形缓冲而不是 List：写帧是 50Hz 的常驻热路径，无界集合就是内存泄漏。
/// 为什么 <see cref="Record"/> 不拿锁：它被 50Hz 写帧线程、串口读线程、UI 线程同时调用，
/// 一把共享锁会把设备输出和界面都拖到一起（急停的教训）；这里改成"每格存一个不可变对象，
/// 用 Interlocked 推进写指针"，数组槽位写引用是原子的，所以读者最多看到略旧的混合视图，
/// 绝不会看到半条记录。黑匣子要的是"大致完整的现场"，不是事务一致性。
/// </summary>
public sealed class BlackBoxRecorder
{
    /// <summary>容量。800 条 × 120 字符 ≈ 200 KB 上限，开软件跑一整天也不涨。</summary>
    public const int Capacity = 800;

    /// <summary>单条文本上限。超过就截断 —— 一条几十 KB 的 banner 会把整圈有效信息挤掉。</summary>
    public const int MaxTextLength = 120;

    /// <summary>文件头版本号：以后改格式（比如加字段）时，至少能认出旧包。</summary>
    public const string ExportVersion = "hexa-blackbox-v1";

    /// <summary>一条记录的不可变载体（引用类型=数组槽位写入原子，读者不会读到半条）。</summary>
    private sealed record Entry(long Ticks, string Channel, string Text);

    private readonly Entry?[] _ring = new Entry?[Capacity];
    private readonly Func<DateTime> _clock;
    private long _written;

    public BlackBoxRecorder(Func<DateTime>? clock = null)
        => _clock = clock ?? (static () => DateTime.UtcNow);

    /// <summary>累计写入条数（含已经被覆盖掉的），用来算"最旧一条"的绝对序号。</summary>
    public long TotalWritten => Interlocked.Read(ref _written);

    /// <summary>当前有效条数（≤ <see cref="Capacity"/>）。</summary>
    public int Count => (int)Math.Min(Capacity, TotalWritten);

    /// <summary>
    /// 记一条。<paramref name="channel"/>：<c>out</c>=写出的帧、<c>in</c>=设备回话/心跳、
    /// <c>state</c>=连接/断开/急停/归中/写失败这类状态切换。
    /// 热路径安全：一次定容数组写 + 一个 ≤120 字符的字符串，不拿锁、不碰 IO。
    /// </summary>
    public void Record(string channel, string text)
    {
        string safeChannel = string.IsNullOrWhiteSpace(channel) ? "misc" : channel.Trim();
        Entry entry = new(_clock().Ticks, safeChannel, Shorten(text));

        // 先占序号再写格子：读者拿到的 Count 可能领先一格，最多漏掉刚写进去的那条，不会读到空引用。
        long index = Interlocked.Increment(ref _written) - 1;
        Volatile.Write(ref _ring[(int)(index % Capacity)], entry);
    }

    /// <summary>按时间顺序（旧 → 新）取全部有效记录。</summary>
    public IReadOnlyList<BlackBoxEntry> Snapshot()
    {
        long written = Interlocked.Read(ref _written);
        int count = (int)Math.Min(Capacity, written);
        if (count == 0) return Array.Empty<BlackBoxEntry>();

        var result = new List<BlackBoxEntry>(count);
        long start = written - count;
        for (long index = start; index < written; index++)
        {
            Entry? entry = Volatile.Read(ref _ring[(int)(index % Capacity)]);
            if (entry is null) continue;
            result.Add(new BlackBoxEntry(new DateTime(entry.Ticks, DateTimeKind.Utc), entry.Channel, entry.Text));
        }
        return result;
    }

    /// <summary>
    /// 导出成纯文本故障包，返回实际写出的绝对路径。不在热路径上：会建目录、分配整段文本、落盘。
    /// </summary>
    /// <param name="path">目标文件（父目录不存在会自动建）。</param>
    /// <param name="summary">文件头摘要（端口 / ResetSeenCount / EchoRatio 之类），由调用方决定内容。</param>
    public string Export(string path, IReadOnlyList<(string Name, string Value)>? summary = null)
    {
        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        IReadOnlyList<BlackBoxEntry> entries = Snapshot();
        var lines = new List<string>(entries.Count + 12)
        {
            "# " + ExportVersion,
            "# 导出时间(UTC): " + _clock().ToString("yyyy-MM-dd HH:mm:ss"),
            $"# 条数: {entries.Count}（上限 {Capacity}，每条截断到 {MaxTextLength} 字符）",
            "# 格式: <时间戳(UTC)> <channel> <text>",
        };
        if (summary != null)
            foreach ((string name, string value) in summary) lines.Add("# " + name + ": " + value);
        lines.Add("# ---- 记录开始 ----");
        foreach (BlackBoxEntry entry in entries)
            lines.Add($"{entry.AtUtc:yyyy-MM-dd HH:mm:ss.fff} {entry.Channel,-5} {entry.Text}");

        // 不带 BOM 的 UTF-8：记事本、grep、用户手动打开都不会被三个不可见字节绊到。
        File.WriteAllLines(full, lines, new UTF8Encoding(false));
        return full;
    }

    /// <summary>截断到 <see cref="MaxTextLength"/>，顺带把换行压成空格（一条记录必须占一行，否则导出文件没法读）。</summary>
    private static string Shorten(string? text)
    {
        string value = text ?? "";
        bool hasBreak = value.Contains('\n') || value.Contains('\r');
        if (hasBreak) value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= MaxTextLength ? value : value[..MaxTextLength];
    }
}

/// <summary>黑匣子里的一条记录（导出与单测用；时间戳是 UTC）。</summary>
public readonly record struct BlackBoxEntry(DateTime AtUtc, string Channel, string Text);
