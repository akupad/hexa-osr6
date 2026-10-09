using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 外挂播放器时间码来源。
///
/// <b>为什么是「外挂从动」而不是内嵌播放器</b>（这一档软件的事实标准 MultiFunPlayer 也是这么做的）：
/// 内嵌一个能放 VR / 各种编码的播放器，要么拖进上百 MB 的原生依赖（LibVLC），
/// 要么根本没法把 VR 播放器塞进 WPF。所以这里反过来：**读外部播放器的时间码，让脚本跟着它走**。
/// 用户用自己的播放器看片（解码、字幕、VR 都是它的事），Hexa 只负责问一句「现在放到第几秒了」。
///
/// 所有实现都必须遵守两条：
/// ① <see cref="TryGetTime"/> 是**非阻塞**的，最坏几十毫秒内必须返回（同步循环不能被一个关掉的播放器拖住）；
/// ② 任何异常都在实现内部消化成「这次没拿到」，绝不往外抛。
/// </summary>
public interface IMediaSource
{
    /// <summary>给用户看的名字（「mpv」/「MPC-HC」/「VLC」）。</summary>
    string DisplayName { get; }

    /// <summary>上一次查询是否拿到过时间码（false = 播放器没开 / 没打开文件 / 超时）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 取一次时间码。拿不到返回 false（此时三个出参无意义）。
    /// </summary>
    /// <param name="seconds">媒体当前播放位置（秒）。</param>
    /// <param name="playing">媒体是否正在播放（暂停 / 停止都算 false）。</param>
    /// <param name="rate">媒体的播放倍速（1.0 = 正常；播放器不支持就返回 1.0）。</param>
    bool TryGetTime(out double seconds, out bool playing, out double rate);

    /// <summary>让媒体跳到指定秒（成功返回 true）。</summary>
    bool Seek(double seconds);

    /// <summary>
    /// 播放器当前打开的文件（完整路径或文件名，拿不到就是 null）。
    /// 「按文件名自动匹配脚本」用它 —— 没有它就只能靠用户手动选脚本。
    /// </summary>
    string? MediaPath { get; }
}

// ═══════════════════════════════════════════════════════════════════════════
//  mpv：命名管道 IPC（JSON / MPlayer 兼容协议）
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// mpv 时间码来源 —— 走 mpv 自己的 IPC 命名管道（Windows 上是 <c>\\.\pipe\&lt;名字&gt;</c>）。
///
/// 用户要在启动 mpv 时加一个参数才有的用：
/// <code>mpv.exe --input-ipc-server=\\.\pipe\mpvpipe 影片.mp4</code>
/// （也可以写进 mpv.conf：<c>input-ipc-server=\\.\pipe\mpvpipe</c>，这样以后双击打开也有。）
///
/// <b>每次查询开一条短连接</b>：mpv 的管道同一时刻只认真服务一个客户端，
/// 长连接一旦因为 mpv 卡住 / 被关掉而半死，后面每次都超时、还分不清是「mpv 关了」还是「连接废了」。
/// 短连接的代价只是本地一次 CreateFile + 握手（微秒级），换来的是每一次查询都是干净的状态。
/// </summary>
public sealed class MpvMediaSource : IMediaSource, IDisposable
{
    /// <summary>连接超时（毫秒）：管道不存在时 Windows 立刻返回「找不到」，这个值只在管道忙时用得上。</summary>
    private const int ConnectTimeoutMs = 18;
    /// <summary>读完这一轮回复的总预算（毫秒）。见类注释里的「非阻塞」约定。</summary>
    private const int ReadBudgetMs = 26;

    /// <summary>
    /// 候选管道名。mpvpipe 是社区最常见写法（MultiFunPlayer 的教程也用它），
    /// mpv / mpv-socket 是另两种常见起名。按「上次成功的排最前」试，避免每次都三次握手。
    /// </summary>
    private static readonly string[] DefaultPipeNames = ["mpvpipe", "mpv", "mpv-socket"];

    private readonly string[] _pipeNames;
    private readonly object _gate = new();
    private string? _knownGoodPipe;
    private string? _mediaPath;
    private volatile bool _available;

    public MpvMediaSource(string? pipeName = null)
    {
        _pipeNames = string.IsNullOrWhiteSpace(pipeName)
            ? DefaultPipeNames
            : [pipeName.Trim(), .. DefaultPipeNames];
    }

    public string DisplayName => "mpv";

    public bool IsAvailable => _available;

    public string? MediaPath
    {
        get { lock (_gate) return _mediaPath; }
    }

    /// <summary>候选管道名的顺序：上次成功的那条排第一（绝大多数时候第一次就连上了）。</summary>
    private IEnumerable<string> Candidates()
    {
        string? known;
        lock (_gate) known = _knownGoodPipe;
        if (known != null) yield return known;
        foreach (string name in _pipeNames)
        {
            if (!string.Equals(name, known, StringComparison.Ordinal)) yield return name;
        }
    }

    public bool TryGetTime(out double seconds, out bool playing, out double rate)
    {
        seconds = 0;
        playing = false;
        rate = 1.0;

        foreach (string pipeName in Candidates())
        {
            if (!Query(pipeName, out seconds, out playing, out rate)) continue;
            lock (_gate) _knownGoodPipe = pipeName;
            _available = true;
            return true;
        }

        _available = false;
        lock (_gate) _mediaPath = null;
        return false;
    }

    public bool Seek(double seconds)
    {
        if (!double.IsFinite(seconds)) return false;
        if (CurrentPipe() is not { } pipeName) return false;

        string command = "{\"command\":[\"set_property\",\"time-pos\","
            + Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture)
            + "],\"request_id\":9}\n";
        return SendCommand(pipeName, command, expectReply: true);
    }

    /// <summary>已知能用的管道名；还没成功过就先探一次（免得刚「连接」就拖进度条没反应）。</summary>
    private string? CurrentPipe()
    {
        lock (_gate) { if (_knownGoodPipe is { } known) return known; }
        if (!TryGetTime(out _, out _, out _)) return null;
        lock (_gate) return _knownGoodPipe;
    }

    /// <summary>
    /// 一次查询：连上 → 发 4 条 get_property → 在一个硬期限内把回复读回来。
    /// 任何一步失败都返回 false（调用方据此切下一条候选管道）。
    /// </summary>
    private bool Query(string pipeName, out double seconds, out bool playing, out double rate)
    {
        seconds = 0;
        playing = false;
        rate = 1.0;

        const string request =
            "{\"command\":[\"get_property\",\"time-pos\"],\"request_id\":1}\n" +
            "{\"command\":[\"get_property\",\"pause\"],\"request_id\":2}\n" +
            "{\"command\":[\"get_property\",\"speed\"],\"request_id\":3}\n" +
            "{\"command\":[\"get_property\",\"path\"],\"request_id\":4}\n";

        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(ConnectTimeoutMs);
            byte[] payload = Encoding.UTF8.GetBytes(request);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();

            // 只等前 3 条（时间/暂停/倍速）；path 通常和前 3 条一起回来，顺带解析。
            // 这样即使 path 这条属性在某些状态下拿不到，也不会把每次查询都拖满预算。
            string reply = ReadReplies(pipe, expectedReplies: 3);

            bool gotTime = false;
            foreach (string line in reply.Split('\n'))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("request_id", out JsonElement idNode)
                        || !idNode.TryGetInt32(out int id)) continue;
                    // mpv 在很多状态下会回 error="property unavailable"（例如还没打开文件），
                    // 这种回复里的 data 是 null，不能当成有效时间码。
                    bool ok = root.TryGetProperty("error", out JsonElement errorNode)
                              && errorNode.ValueKind == JsonValueKind.String
                              && string.Equals(errorNode.GetString(), "success", StringComparison.Ordinal);
                    if (!ok || !root.TryGetProperty("data", out JsonElement data)) continue;

                    switch (id)
                    {
                        case 1:
                            if (TryReadDouble(data, out double position)) { seconds = position; gotTime = true; }
                            break;
                        case 2:
                            if (data.ValueKind is JsonValueKind.True or JsonValueKind.False)
                                playing = data.ValueKind == JsonValueKind.False;   // pause=false 就是在播
                            break;
                        case 3:
                            if (TryReadDouble(data, out double speed) && speed > 0.05) rate = speed;
                            break;
                        case 4:
                            if (data.ValueKind == JsonValueKind.String)
                            {
                                string? path = data.GetString();
                                if (!string.IsNullOrWhiteSpace(path))
                                {
                                    lock (_gate) _mediaPath = path;
                                }
                            }
                            break;
                    }
                }
                catch (JsonException) { /* 单行解析不了就跳过：mpv 也会往管道里推事件消息 */ }
            }

            return gotTime;
        }
        catch
        {
            return false;   // 管道不存在 / 忙 / 断开 / 超时 —— 对调用方都是「这次没拿到」
        }
        finally
        {
            try { pipe?.Dispose(); } catch { /* 关连接失败无所谓，下次会开新的 */ }
        }
    }

    private static bool SendCommand(string pipeName, string command, bool expectReply)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(ConnectTimeoutMs);
            byte[] payload = Encoding.UTF8.GetBytes(command);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
            if (expectReply) _ = ReadReplies(pipe, expectedReplies: 1);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { pipe?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 在一个硬期限内把回复读回来（读到 <paramref name="expectedReplies"/> 条回复或超时就返回）。
    ///
    /// <b>为什么不用 ReadTimeout</b>：那是靠底层句柄超时实现的，超时后这条连接的状态是「不确定」的；
    /// 这里用「带期限的异步读 + 超时就整条连接作废」，超时与否都是干净的状态。
    /// 超时那条读操作不会一直挂着 —— finally 里 Dispose 掉流会把它踢出来，异常也被观察掉了。
    /// </summary>
    private static string ReadReplies(Stream pipe, int expectedReplies)
    {
        var text = new StringBuilder(512);
        byte[] buffer = new byte[4096];
        long deadline = Stopwatch.GetTimestamp()
                        + (long)(ReadBudgetMs / 1000.0 * Stopwatch.Frequency);
        while (true)
        {
            if (CountReplies(text) >= expectedReplies) return text.ToString();
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return text.ToString();

            int remainingMs = (int)Math.Max(1, remainingTicks * 1000.0 / Stopwatch.Frequency);
            Task<int> read = pipe.ReadAsync(buffer.AsMemory(0, buffer.Length)).AsTask();
            if (!read.Wait(remainingMs))
            {
                AbandonRead(read);
                return text.ToString();
            }
            int count = read.Result;
            if (count <= 0) return text.ToString();
            text.Append(Encoding.UTF8.GetString(buffer, 0, count));
        }
    }

    /// <summary>数一数已经收到几条回复（每条回复都带 request_id）。</summary>
    private static int CountReplies(StringBuilder text)
    {
        string s = text.ToString();
        int count = 0, index = 0;
        while ((index = s.IndexOf("\"request_id\"", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 12;
        }
        return count;
    }

    /// <summary>
    /// 放弃一条没读完的读操作：只观察它的异常，不让它变成「未观察的任务异常」。
    /// （超时后我们立刻弃用整条短连接，所以不需要等它回来。）
    /// </summary>
    private static void AbandonRead(Task read)
    {
        _ = read.ContinueWith(
            static finished => { _ = finished.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool TryReadDouble(JsonElement node, out double value)
    {
        value = 0;
        return node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out value) && double.IsFinite(value);
    }

    public void Dispose() { /* 没有长连接要关：每次查询都是短连接，见类注释 */ }
}

// ═══════════════════════════════════════════════════════════════════════════
//  MPC-HC / MPC-BE：Web 接口
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// MPC-HC / MPC-BE 的时间码来源 —— 走播放器自带的 Web 接口（默认端口 13579）。
///
/// 用户要在播放器里开一次：<c>选项 → 播放器 → Web 界面 → 勾上「监听端口」</c>（默认 13579）。
/// <c>variables.html</c> 是一个定时刷新的小页面，里面的 JS 变量直接就是当前状态（position 单位是秒）。
///
/// 端口可配，所以这里按候选端口逐个试（默认 13579 优先）——
/// 想改端口的话，目前要在播放器那边改回 13579（Hexa 这边没有存自定义端口的设置项）。
/// </summary>
public sealed class MpcMediaSource : IMediaSource, IDisposable
{
    /// <summary>单次请求的硬预算（毫秒）。本地回环正常只要 1–3ms，45ms 是十几倍余量。</summary>
    private const int RequestBudgetMs = 45;

    /// <summary>候选端口：13579 是 MPC-HC/BE 的默认值，另两个是常见的自定义值。</summary>
    private static readonly int[] PortCandidates = [13579, 13580, 13581];

    /// <summary>
    /// 实际要试的端口：用户在设置里填了就**优先试它**（放最前），否则自动探测常见端口。
    /// 播放器的 Web 端口是可以改的；不给入口的话用户只能反过来改播放器，体验上说不过去。
    /// </summary>
    private static int[] PortsFor(AppSettings cfg)
    {
        int custom = cfg.MediaSyncPort;
        if (custom is <= 0 or > 65535) return PortCandidates;
        return PortCandidates.Contains(custom) ? PortCandidates : [custom, .. PortCandidates];
    }

    /// <summary>这个源实际使用的候选端口（自定义端口优先）。</summary>
    private int[] _ports = PortCandidates;

    private static readonly Regex PositionRegex = new(
        @"var\s+position\s*=\s*(-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StateRegex = new(
        @"var\s+state\s*=\s*(-?[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlayRateRegex = new(
        @"var\s+playrate\s*=\s*(-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DurationRegex = new(
        @"var\s+fileduration\s*=\s*(-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilePathRegex = new(
        "var\\s+filepath\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private int? _knownGoodPort;
    private string? _mediaPath;
    private volatile bool _available;

    public MpcMediaSource(AppSettings? cfg = null)
    {
        // 超时设死：本地回环正常 1–3ms，超时值只是「播放器关掉 / 端口不是这个」时的兜底，
        // 不能让同步循环在这里挂住（见 IMediaSource 的非阻塞约定）。
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(RequestBudgetMs) };
        if (cfg is not null) _ports = PortsFor(cfg);
    }

    public string DisplayName => "MPC-HC";

    public bool IsAvailable => _available;

    public string? MediaPath
    {
        get { lock (_gate) return _mediaPath; }
    }

    private IEnumerable<int> Candidates()
    {
        int? known;
        lock (_gate) known = _knownGoodPort;
        if (known is { } port) yield return port;
        foreach (int candidate in _ports)
        {
            if (candidate != known) yield return candidate;
        }
    }

    public bool TryGetTime(out double seconds, out bool playing, out double rate)
    {
        seconds = 0;
        playing = false;
        rate = 1.0;

        foreach (int port in Candidates())
        {
            if (!Query(port, out seconds, out playing, out rate)) continue;
            lock (_gate) _knownGoodPort = port;
            _available = true;
            return true;
        }

        _available = false;
        lock (_gate) _mediaPath = null;
        return false;
    }

    public bool Seek(double seconds)
    {
        if (!double.IsFinite(seconds)) return false;
        if (CurrentPort() is not { } port) return false;

        // MPC 的 Web 接口里 wm_command=-1 是「跳到 position」，单位是毫秒。
        long milliseconds = (long)Math.Round(Math.Max(0, seconds) * 1000.0);
        string url = $"http://127.0.0.1:{port}/command.html?wm_command=-1&position={milliseconds}";
        return Get(url) != null;
    }

    /// <summary>已知能用的端口；还没成功过就先探一次。</summary>
    private int? CurrentPort()
    {
        lock (_gate) { if (_knownGoodPort is { } known) return known; }
        if (!TryGetTime(out _, out _, out _)) return null;
        lock (_gate) return _knownGoodPort;
    }

    private bool Query(int port, out double seconds, out bool playing, out double rate)
    {
        seconds = 0;
        playing = false;
        rate = 1.0;

        string? html = Get($"http://127.0.0.1:{port}/variables.html");
        if (html is null) return false;

        if (!TryMatchDouble(PositionRegex, html, out double position)) return false;
        // 没打开文件时 position 是 0 / fileduration 是 0：这时候「停在 0 秒」不是有效时间码，
        // 报「未找到播放器（没在放东西）」比把脚本拽到 0 秒更好。
        if (TryMatchDouble(DurationRegex, html, out double duration) && duration <= 0.05) return false;

        seconds = position;
        // MPC 的 state：0 = 停止 / 1 = 暂停 / 2 = 播放。
        playing = TryMatchInt(StateRegex, html, out int state) && state == 2;
        if (TryMatchDouble(PlayRateRegex, html, out double playRate) && playRate > 0.05) rate = playRate;

        if (FilePathRegex.Match(html) is { Success: true } match)
        {
            string path = UnescapeJsString(match.Groups[1].Value);
            lock (_gate) _mediaPath = path.Length > 0 ? path : null;
        }
        return true;
    }

    private string? Get(string url)
    {
        try
        {
            using HttpResponseMessage response = _http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return null;   // 端口不通 / 超时 / 返回不是文本 —— 对调用方都是「这次没拿到」
        }
    }

    private static bool TryMatchDouble(Regex regex, string text, out double value)
    {
        value = 0;
        Match match = regex.Match(text);
        return match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static bool TryMatchInt(Regex regex, string text, out int value)
    {
        value = 0;
        Match match = regex.Match(text);
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 还原 JS 字符串里的转义：MPC 写出来的是 <c>"C:\\video\\片名.mkv"</c>，
    /// 非 ASCII 还会写成 <c>\uXXXX</c>。不还原的话按文件名匹配永远对不上。
    /// </summary>
    private static string UnescapeJsString(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char current = raw[i];
            if (current != '\\' || i + 1 >= raw.Length) { builder.Append(current); continue; }
            char next = raw[++i];
            switch (next)
            {
                case 'u':
                    if (i + 4 < raw.Length
                        && ushort.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out ushort code))
                    {
                        builder.Append((char)code);
                        i += 4;
                    }
                    else builder.Append("\\u");
                    break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                default:  builder.Append(next); break;   // \\ \" \/ 以及其它未转义字符都是它本身
            }
        }
        return builder.ToString();
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  VLC：--extraintf http 的 Web 接口
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// VLC 的时间码来源 —— 走 VLC 自带的 HTTP 接口（默认端口 8080）。
///
/// 用户要这样启动 VLC：<c>vlc.exe --extraintf http --http-password=xxx 影片.mp4</c>。
/// <c>status.json</c> 里有 time（秒）/ state / rate（倍速）。
///
/// 注意：VLC 的 HTTP 接口**默认要求密码**。Hexa 这边没有存密码的设置项，
/// 所以只支持「没设密码」或「密码为空」的启动方式；设了密码就会如实报错并建议改用 mpv / MPC-HC。
/// </summary>
public sealed class VlcMediaSource : IMediaSource, IDisposable
{
    private const int RequestBudgetMs = 45;
    private static readonly int[] PortCandidates = [8080, 8081, 8082];

    /// <summary>这个源实际使用的候选端口（自定义端口优先，同 MpcMediaSource）。</summary>
    private int[] _ports = PortCandidates;

    /// <summary>用户在设置里填了端口就优先试它（同 MpcMediaSource.PortsFor）。</summary>
    private static int[] PortsFor(AppSettings cfg)
    {
        int custom = cfg.MediaSyncPort;
        if (custom is <= 0 or > 65535) return PortCandidates;
        return PortCandidates.Contains(custom) ? PortCandidates : [custom, .. PortCandidates];
    }

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private int? _knownGoodPort;
    private string? _mediaPath;
    private volatile bool _available;

    public VlcMediaSource(AppSettings? cfg = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(RequestBudgetMs) };
        if (cfg is not null) _ports = PortsFor(cfg);
    }

    public string DisplayName => "VLC";

    public bool IsAvailable => _available;

    public string? MediaPath
    {
        get { lock (_gate) return _mediaPath; }
    }

    private IEnumerable<int> Candidates()
    {
        int? known;
        lock (_gate) known = _knownGoodPort;
        if (known is { } port) yield return port;
        foreach (int candidate in _ports)
        {
            if (candidate != known) yield return candidate;
        }
    }

    public bool TryGetTime(out double seconds, out bool playing, out double rate)
    {
        seconds = 0;
        playing = false;
        rate = 1.0;

        foreach (int port in Candidates())
        {
            if (!Query(port, out seconds, out playing, out rate)) continue;
            lock (_gate) _knownGoodPort = port;
            _available = true;
            return true;
        }

        _available = false;
        lock (_gate) _mediaPath = null;
        return false;
    }

    public bool Seek(double seconds)
    {
        if (!double.IsFinite(seconds)) return false;
        if (CurrentPort() is not { } port) return false;

        // VLC 的 seek 命令 val 是「秒」。
        string value = Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
        return Get($"http://127.0.0.1:{port}/requests/status.json?command=seek&val={value}") != null;
    }

    /// <summary>已知能用的端口；还没成功过就先探一次。</summary>
    private int? CurrentPort()
    {
        lock (_gate) { if (_knownGoodPort is { } known) return known; }
        if (!TryGetTime(out _, out _, out _)) return null;
        lock (_gate) return _knownGoodPort;
    }

    private bool Query(int port, out double seconds, out bool playing, out double rate)
    {
        seconds = 0;
        playing = false;
        rate = 1.0;

        string? json = Get($"http://127.0.0.1:{port}/requests/status.json");
        if (json is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("time", out JsonElement timeNode)
                || !timeNode.TryGetDouble(out double time)
                || !double.IsFinite(time))
                return false;

            seconds = Math.Max(0, time);
            string state = root.TryGetProperty("state", out JsonElement stateNode)
                           && stateNode.ValueKind == JsonValueKind.String
                ? stateNode.GetString() ?? ""
                : "";
            playing = string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase);
            if (root.TryGetProperty("rate", out JsonElement rateNode)
                && rateNode.TryGetDouble(out double parsedRate)
                && double.IsFinite(parsedRate) && parsedRate > 0.05)
                rate = parsedRate;

            // 文件名藏在 information.category.meta 里（VLC 给的是文件名，不是完整路径，够匹配用了）。
            if (root.TryGetProperty("information", out JsonElement info)
                && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("category", out JsonElement category)
                && category.ValueKind == JsonValueKind.Object
                && category.TryGetProperty("meta", out JsonElement meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("filename", out JsonElement filename)
                && filename.ValueKind == JsonValueKind.String)
            {
                string? name = filename.GetString();
                lock (_gate) _mediaPath = string.IsNullOrWhiteSpace(name) ? null : name;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string? Get(string url)
    {
        try
        {
            using HttpResponseMessage response = _http.GetAsync(url).GetAwaiter().GetResult();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // VLC 的 HTTP 接口默认要密码。再试一次「空密码」的 Basic 认证
                //（--http-password= 或没配时就是这个情况），还不行就交给上层报错。
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":")));
                using HttpResponseMessage retry = _http.SendAsync(request).GetAwaiter().GetResult();
                if (!retry.IsSuccessStatusCode) return null;
                return retry.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            if (!response.IsSuccessStatusCode) return null;
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  同步循环
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>媒体同步的一帧状态快照（只读，界面直接拿去显示）。</summary>
/// <param name="Connected">播放器连上了（能看到有效时间码）。</param>
/// <param name="PlayerName">播放器显示名。</param>
/// <param name="Message">一行中文状态（界面直接用）。</param>
/// <param name="MediaSeconds">媒体位置（秒）。</param>
/// <param name="ScriptSeconds">脚本位置（秒）。</param>
/// <param name="ErrorSeconds">误差 = 脚本位置 − 媒体位置（含偏移，秒）。</param>
/// <param name="MediaPlaying">媒体是否在播。</param>
/// <param name="EffectiveRate">脚本当前实际速度（含播放器倍速，1.0 = 正常）。</param>
public sealed record MediaSyncStatus(
    bool Connected,
    string PlayerName,
    string Message,
    double MediaSeconds,
    double ScriptSeconds,
    double ErrorSeconds,
    bool MediaPlaying,
    double EffectiveRate)
{
    public static MediaSyncStatus Idle { get; } = new(
        false, "", "未连接 · 点「连接」开始跟随（需要播放器开着并允许远程控制）",
        0, 0, 0, false, 1.0);
}

/// <summary>
/// 媒体同步 —— 让脚本跟着外挂播放器的时间码走。
///
/// <b>一条铁律：这个服务永远不决定「脚本放不放」，只决定「脚本放多快、放到哪」。</b>
/// 播放器关掉、端口写错、查询超时，结果都只是「这一轮没对上」：同步循环复位倍率、
/// 在状态里如实说明，**脚本继续按用户自己的速度播下去**。
/// 所以同步循环跑在自己的后台线程上（<see cref="ThreadPriority.BelowNormal"/>，80ms 一拍），
/// 就算它被一个卡死的播放器拖住几百毫秒，脚本的时钟（在引擎输出节拍线程上）也一点都不受影响。
///
/// 对齐策略（阈值与公式见 <see cref="SeekThresholdSeconds"/> / <see cref="CatchUpCoefficient"/>）：
/// ① 误差 &gt; 1 秒 → 直接 Seek 脚本（差得太多，用 ±10% 的微调要追十几秒，用户只会觉得「一直对不上」）；
/// ② 误差 ≤ 1 秒 → 只微调倍率在 0.9–1.1× 之间追，画面不会因为一次小抖动就跳。
/// </summary>
public sealed class MediaSyncService : IDisposable
{
    /// <summary>同步循环的节拍（毫秒）。50–100 是「跟得上画面」和「别太费 CPU」之间的常用折中。</summary>
    public const int TickIntervalMs = 80;

    /// <summary>误差超过这个秒数就直接跳，不做微调。</summary>
    public const double SeekThresholdSeconds = 1.0;

    /// <summary>
    /// 微调倍率的比例系数（误差 × 这个系数 = 倍率修正量）。
    /// 0.33 取自 MultiFunPlayer 的跟随实现：误差 0.3 秒刚好用满 ±10% 的修正量，
    /// 再大就夹住 —— 既能追上，又不会因为一点点抖动就开始忽快忽慢。
    /// </summary>
    public const double CatchUpCoefficient = 0.33;

    /// <summary>微调倍率的下限 / 上限（1.0 是「不修正」）。</summary>
    public const double TrimMin = 0.9;
    public const double TrimMax = 1.1;

    private readonly FunscriptPlayerService _player;
    private readonly AppSettings _cfg;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _gate = new();

    private Thread? _thread;
    private IMediaSource? _source;
    private volatile bool _stop;
    private volatile bool _disposed;
    /// <summary>位置同步的暂停原因（拖动进度条 / A-B 循环）；null = 正常跟随。</summary>
    private volatile string? _holdReason;
    private volatile MediaSyncStatus _status = MediaSyncStatus.Idle;
    /// <summary>只有循环线程碰它：上一次暂停是同步自己按的（而不是用户手动按的）。</summary>
    private bool _pausedBySync;
    /// <summary>上一次自动匹配过的媒体文件（换文件才再匹配一次）。</summary>
    private string? _autoMatchedPath;
    /// <summary>自动匹配同一时刻只跑一次（按钮和自动两条路都会调）。</summary>
    private int _autoMatchBusy;

    public MediaSyncService(FunscriptPlayerService player, AppSettings cfg)
    {
        _player = player;
        _cfg = cfg;
    }

    /// <summary>最新一帧状态（界面轮询它，不需要事件，也就没有跨线程回调的坑）。</summary>
    public MediaSyncStatus Status => _status;

    /// <summary>同步循环是否在跑（注意：在跑 ≠ 已经连上播放器；连不上会在状态里说明）。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _thread != null; }
    }

    /// <summary>当前播放器 id（mpv / mpc / vlc）。</summary>
    public string PlayerId { get; private set; } = "";

    /// <summary>
    /// 开始跟随某个播放器。<b>必须由用户点「连接」触发</b>（本类不会自己启动）。
    /// 播放器此刻没开也没关系：循环会一直找，用户后开播放器也能自动接上。
    /// </summary>
    public bool Connect(string playerId)
    {
        Disconnect();
        string id = (playerId ?? "").Trim().ToLowerInvariant();
        IMediaSource? source = CreateSource(id);
        if (source is null)
        {
            PlayerId = "";
            _status = new MediaSyncStatus(false, "", $"这个播放器还没有支持：{id}（目前支持 mpv / MPC-HC / VLC）", 0, 0, 0, false, 1.0);
            return false;
        }

        PlayerId = id;
        lock (_gate)
        {
            _source = source;
            _pausedBySync = false;
            _autoMatchedPath = null;
            _wake.Reset();
            _stop = false;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,   // 播放和串口都比它重要
                Name = "HexaMediaSync",
            };
            _thread.Start();
        }

        _status = new MediaSyncStatus(false, source.DisplayName,
            $"正在找 {source.DisplayName}…（请确认播放器开着，并允许远程控制）", 0, 0, 0, false, 1.0);
        AppLogger.Info($"[媒体同步] 开始跟随 {source.DisplayName}（{DescribePlayer(id)}）");
        return true;
    }

    /// <summary>
    /// 停止跟随。<b>不碰脚本的播放状态</b>：把倍率还给用户（1.0），脚本继续按原速播。
    /// </summary>
    public void Disconnect(string? message = null)
    {
        Thread? thread;
        lock (_gate)
        {
            _stop = true;
            thread = _thread;
            _thread = null;
        }
        _wake.Set();
        if (thread != null && thread != Thread.CurrentThread)
        {
            try { thread.Join(400); } catch { /* 线程已经退出 / 正在退，都不影响后续 */ }
        }
        _wake.Reset();

        IMediaSource? source;
        lock (_gate)
        {
            source = _source;
            _source = null;
        }
        (source as IDisposable)?.Dispose();

        _player.MediaSyncRate = 1.0;
        _pausedBySync = false;
        _autoMatchedPath = null;
        _holdReason = null;
        PlayerId = "";
        _status = string.IsNullOrWhiteSpace(message)
            ? MediaSyncStatus.Idle
            : MediaSyncStatus.Idle with { Message = message };
    }

    /// <summary>
    /// 暂停位置同步并给出原因（拖动进度条 / 开着 A-B 循环时用）。
    /// 传 null 恢复跟随。为什么要有这个开关：拖进度条时同步循环每 80ms 就把位置抢回去一次，
    /// 用户根本拖不动；A-B 循环和位置同步更是会互相打架（一个往回跳、一个往前拉 → 设备来回抖）。
    /// 暂停期间仍然跟随**倍速**，只是不做位置纠正。
    /// </summary>
    public void SetPositionHold(string? reason) =>
        _holdReason = string.IsNullOrWhiteSpace(reason) ? null : reason;

    /// <summary>
    /// 按播放器当前打开的文件名，在脚本库里找同名的 .funscript 并**载入**（不播放）。
    /// 返回一句中文结果，界面直接显示。按钮和「自动匹配」两条路都会调它，所以同一时刻只跑一次。
    /// </summary>
    public string TryAutoLoadMatchingScript(string? mediaPath = null)
    {
        if (Interlocked.Exchange(ref _autoMatchBusy, 1) == 1) return "正在匹配…";
        try
        {
            mediaPath ??= _source?.MediaPath;
            const string noName = "还读不到播放器打开的文件名：先点「连接」，并让播放器真的打开一个文件。";
            if (string.IsNullOrWhiteSpace(mediaPath)) return noName;

            string stem = Path.GetFileNameWithoutExtension(mediaPath.Trim());
            if (stem.Length == 0) return noName;

            string? match = FindScriptByStem(ScriptLibrary.Folder, stem);
            if (match is null)
                return $"脚本库里没有和「{stem}」同名的脚本（找的是 {stem}.funscript）。";

            string? loaded = _player.LoadedFile;
            if (!string.IsNullOrEmpty(loaded)
                && string.Equals(Path.GetFullPath(match), Path.GetFullPath(loaded), StringComparison.OrdinalIgnoreCase))
                return $"已经载入的就是「{Path.GetFileName(match)}」。";

            _player.Load(match);
            return $"已按文件名载入「{Path.GetFileName(match)}」（没有开始播放，要点「▶ 载入并播放」设备才动）。";
        }
        catch (Exception ex)
        {
            return "匹配脚本失败：" + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _autoMatchBusy, 0);
        }
    }

    /// <summary>在库里找同名的 .funscript（先试精确名，再按大小写不敏感找一遍）。</summary>
    private static string? FindScriptByStem(string folder, string stem)
    {
        try
        {
            string direct = Path.Combine(folder, stem + ".funscript");
            if (File.Exists(direct)) return direct;
            if (!Directory.Exists(folder)) return null;
            string wanted = stem + ".funscript";
            foreach (string candidate in Directory.EnumerateFiles(folder, "*.funscript"))
            {
                if (string.Equals(Path.GetFileName(candidate), wanted, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("[媒体同步] 在脚本库里找同名脚本失败：" + ex.Message);
        }
        return null;
    }

    private IMediaSource? CreateSource(string playerId) => playerId switch
    {
        "mpv" => new MpvMediaSource(),
        "mpc" => new MpcMediaSource(_cfg),
        "vlc" => new VlcMediaSource(_cfg),
        _ => null,
    };

    /// <summary>「这个播放器要怎么开才能被控制」——按错了要知道去哪儿改，所以写进日志与界面提示。</summary>
    private static string DescribePlayer(string playerId) => playerId switch
    {
        "mpv" => "需要 mpv 以 --input-ipc-server=\\\\.\\pipe\\mpvpipe 启动",
        "mpc" => "需要在 MPC-HC/BE 选项里打开 Web 界面（默认端口 13579）",
        "vlc" => "需要 VLC 以 --extraintf http 启动（默认端口 8080，且不能设密码）",
        _ => "未知播放器",
    };

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                Step();
            }
            catch (Exception ex)
            {
                // 意外也不能把后台线程炸掉（未处理异常会直接结束进程），更不能让脚本停下：
                // 复位倍率 + 写一行状态就够了。
                ResetRate();
                _status = new MediaSyncStatus(false, _source?.DisplayName ?? "播放器",
                    $"同步出错（脚本继续按原速播放）：{ex.Message}", 0, _player.PositionSeconds, 0, false, 1.0);
            }
            try { _wake.Wait(TickIntervalMs); }
            catch { try { Thread.Sleep(TickIntervalMs); } catch { } }
        }
    }

    private void Step()
    {
        if (_disposed || _stop || _source is not { } source) return;

        if (!source.TryGetTime(out double media, out bool playing, out double rate))
        {
            // 播放器没开 / 端口不对 / 查询超时：**不碰脚本的播放状态**，只把倍率还回去，
            // 让它按用户自己的速度继续播完。用户关播放器不等于要停脚本。
            // （_pausedBySync 刻意不清：那是一次「瞬时抖动」而不是用户改主意，
            //   播放器回来继续播时还应该把脚本接回去。）
            if (_stop) return;   // 查询期间用户点了「断开」：别再往播放器写任何状态
            ResetRate();
            _status = new MediaSyncStatus(false, source.DisplayName,
                $"未找到 {source.DisplayName} · 请先打开播放器并允许远程控制（脚本继续按原速播放）",
                0, _player.PositionSeconds, 0, false, 1.0);
            return;
        }

        // TryGetTime 是最慢的一步（管道 / HTTP 几十毫秒），断开可能就发生在这中间。
        // 这里再确认一次，避免「已经断开」之后还被写一次倍率 —— 那会留下一个改过速的脚本。
        if (_stop) return;

        // 自动匹配同名脚本：只在「换了一个媒体文件」时试一次。绝不自动播放 ——
        // 载入脚本不动设备，真正让设备动起来必须用户自己点。
        if (_cfg.MediaSyncAutoLoad && source.MediaPath is { Length: > 0 } mediaPath
            && !string.Equals(mediaPath, _autoMatchedPath, StringComparison.OrdinalIgnoreCase))
        {
            _autoMatchedPath = mediaPath;
            if (_player.IsPlaying)
            {
                // 正放着别的脚本：不抢。Load 会把正在播的脚本整个换掉、设备当场停住，
                // 用户开了个新视频就打断他正在看的东西，比「没自动载入」糟得多。
                AppLogger.Info($"[媒体同步] 媒体换成了「{Path.GetFileName(mediaPath)}」，但当前有脚本正在播放，跳过自动载入");
            }
            else
            {
                AppLogger.Info("[媒体同步] 自动匹配脚本：" + TryAutoLoadMatchingScript(mediaPath));
            }
        }

        string clock = $"媒体 {FormatClock(media)}";
        if (!_player.HasTrack)
        {
            ResetRate();
            _pausedBySync = false;
            _status = new MediaSyncStatus(true, source.DisplayName,
                $"已连接 {source.DisplayName} · {clock} · 还没有载入脚本", media, 0, 0, playing, 1.0);
            return;
        }

        double offsetSeconds = Math.Clamp(_cfg.MediaSyncOffsetMs, -5000, 5000) / 1000.0;
        double target = Math.Max(0, media - offsetSeconds);
        double script = _player.PositionSeconds;
        double error = script - target;
        if (!double.IsFinite(error)) error = 0;

        double mediaRate = double.IsFinite(rate) && rate > 0.05 ? Math.Clamp(rate, 0.1, 8.0) : 1.0;
        string? hold = _holdReason;

        // 媒体暂停 → 脚本暂停；媒体继续 → 脚本继续。
        // 只恢复「同步自己暂停的」：用户在本软件里手动按的 ⏸ 不该被外挂播放器掀掉
        //（那会变成「我明明暂停了，机器又动起来」）。
        if (!playing)
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                _pausedBySync = true;
            }
        }
        else if (_player.IsPaused && _pausedBySync)
        {
            _player.Play();
            _pausedBySync = false;
        }

        string state;
        if (!_player.IsPlaying)
        {
            // 脚本没在播（用户手动暂停 / 还没点播放）—— **一点都不动它的位置**。
            // 为什么必须特判：Seek 在暂停时会渲染一帧，而设备是「按位置走」的。
            // 拖着一个「暂停」的脚本往画面位置跑，结果是设备每隔一秒跳到新位置，
            // 而界面上明明写着「已暂停」—— 用户完全看不懂设备为什么在动。
            // 等它真的播起来，下面那一跳会把位置一次性对齐（≤80ms）。
            _player.MediaSyncRate = mediaRate;
            state = playing ? "脚本未播放 · 点「▶ 继续」开始跟随" : "媒体已暂停";
        }
        else if (hold != null)
        {
            // 用户在做主（拖进度条 / A-B 循环）：只跟倍速，位置一点都不动。
            _player.MediaSyncRate = mediaRate;
            state = hold;
        }
        else if (Math.Abs(error) > SeekThresholdSeconds)
        {
            _player.SeekSeconds(target);
            _player.MediaSyncRate = mediaRate;
            state = "已跳到画面位置";
        }
        else
        {
            double trim = Math.Clamp(1.0 - error * CatchUpCoefficient, TrimMin, TrimMax);
            _player.MediaSyncRate = mediaRate * trim;
            state = Math.Abs(trim - 1.0) < 0.002 ? "跟随中" : $"追赶中 {trim:0.00}x";
        }

        double effective = _player.MediaSyncRate * _cfg.ScriptPlaybackSpeed;
        string speedNote = Math.Abs(effective - 1.0) > 0.02 ? $" · 脚本 {effective:0.00}x" : "";
        _status = new MediaSyncStatus(true, source.DisplayName,
            $"已连接 {source.DisplayName} · {clock} / 脚本 {FormatClock(script)}{speedNote} · {state}",
            media, script, error, playing, effective);
    }

    /// <summary>把倍率还给用户（1.0）。失败路径 / 断开都要调，免得留下一个改过速的脚本在跑。</summary>
    private void ResetRate() => _player.MediaSyncRate = 1.0;

    /// <summary>秒 → mm:ss（和脚本库页的读数一致，用户不用在两套格式之间换算）。</summary>
    private static string FormatClock(double seconds)
    {
        long total = (long)Math.Max(0, Math.Round(seconds));
        return $"{total / 60:00}:{total % 60:00}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect("已断开");
        // 刻意不 Dispose _wake：万一线程没能及时退出，Dispose 会让它 Wait 时抛异常，
        // 那比留一个几字节的句柄糟糕得多（ManualResetEventSlim 没有内核对象时几乎不占资源）。
    }
}
