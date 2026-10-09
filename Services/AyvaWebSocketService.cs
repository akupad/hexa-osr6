using System.Net;
using System.Net.WebSockets;
using System.Text;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// Ayva 网页入口 —— 让 Ayva 系的网页（Ayva Stroker Lite、Ayva Remote 等）直接驱动设备。
///
/// 协议是从 Ayva 自己的源码读出来的，不是猜的：
///   · <c>ayvajs/src/devices/websocket-device.js</c>：连 <c>ws://host:port/ws</c>，<c>write()</c> 就是 <c>socket.send(字符串)</c>；
///   · <c>ayvajs/src/ayva.js</c>：把数值转成 <c>"L0500"</c> 这种 TCode 文本。
/// 所以连接后每一帧文本就是一条 TCode 指令 —— 没有握手、没有 JSON 包装。
///
/// 动作走 Hexa 既有的「直接下发」通道（<see cref="MotionEngine.TryClaimDirectInput"/> +
/// <see cref="MotionEngine.TrySendDirectAxes"/>），和游戏桥同一档：照样受急停、安全归中与
/// 让位关系的约束，不绕开安全闸。
/// </summary>
public sealed class AyvaWebSocketService : IDisposable
{
    /// <summary>
    /// 监听端口。网页里主机名<b>必须填 <c>localhost</c></b> —— Ayva 的源码里只有 localhost 才用
    /// <c>ws://</c>，填别的会尝试 <c>wss://</c> 而连不上。
    /// </summary>
    public const int FallbackPort = 8581;

    /// <summary>
    /// 真正监听的端口。默认先抢 <b>80</b>：Ayva 网页的默认端口就是 80
    /// （<c>ayva-stroker-lite/src/components/AyvaSettings.vue</c> 里 <c>port: storage.load('port') || 80</c>），
    /// 抢到 80 用户打开网页什么都不用改；抢不到就退到 <see cref="FallbackPort"/>，界面上会写清该填哪个。
    /// </summary>
    public int ActivePort { get; private set; } = FallbackPort;

    private const string Owner = "ayva";

    /// <summary>
    /// 允许连上来的网页来源白名单。**WebSocket 不受同源策略约束** —— 不查来源的话，
    /// 只要用户开着这个入口，任何网页都能连 ws://localhost:.../ws 直接驱动设备。
    /// 放行三类：本机页面、Ayva 官方站点、以及没有 Origin 头的请求（本机脚本/curl 之类 ——
    /// 本机程序本来就能直接控制设备，挡它没有意义）。
    /// 注意不能简单"只允许本机"：Ayva 那两个网页是外网站点，只放本机会把功能本身封掉。
    /// </summary>
    private static readonly string[] AllowedOriginHosts =
    {
        "localhost", "127.0.0.1", "::1",
        "ayva-stroker-lite.io", "www.ayva-stroker-lite.io",
        "ayvasoftware.io", "www.ayvasoftware.io", "remote.ayvasoftware.io",
    };

    /// <summary>网页来源是不是我们认识的（或本机 / 非浏览器）。</summary>
    public static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;          // 非浏览器来源：本机脚本、curl 等
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)) return false;
        string host = uri.Host.Trim('[', ']');
        foreach (string allowed in AllowedOriginHosts)
            if (string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    private const int MaxFrameBytes = 8192;

    private readonly MotionEngine _engine;
    private readonly double[] _axes = [50, 50, 50, 50, 50, 50];
    private readonly object _lock = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>当前连着的网页数。</summary>
    public int Clients { get; private set; }

    /// <summary>累计收到的帧数（用于界面上判断「到底有没有东西发过来」）。</summary>
    public long Frames;

    /// <summary>最近一条 TCode 原文（界面上直接显示，方便对照）。</summary>
    public string LastCommand = "";

    public string LastError = "";

    /// <summary>
    /// 运行期提示（客户端断开、被别的源占着…）。**与 <see cref="LastError"/> 分开**：
    /// 以前两者写同一个字段，于是"网页正常断开一次"会让界面显示「没开（上次启动失败：远程主机强迫关闭…）」——
    /// 明明服务还在监听（子代理审计发现）。
    /// </summary>
    public string RuntimeNote = "";
    public bool IsRunning => _listener?.IsListening == true;
    public string Url => $"ws://localhost:{ActivePort}/ws";

    public AyvaWebSocketService(MotionEngine engine) => _engine = engine;

    public void Start()
    {
        if (IsRunning) return;
        Stop();

        var cts = new CancellationTokenSource();
        foreach (int port in new[] { 80, FallbackPort })
        {
            HttpListener? listener = TryListen(port);
            if (listener is null) continue;
            _listener = listener;
            _cts = cts;
            ActivePort = port;
            LastError = "";
        RuntimeNote = "";
            AppLogger.Info($"Ayva 网页入口已监听 {Url}（网页里主机填 localhost、端口 {port}）");
            _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
            return;
        }

        cts.Dispose();
        if (LastError.Length == 0) LastError = "80 和 8581 都监听不上";
        AppLogger.Warn("Ayva 网页入口启动失败：" + LastError);
    }

    /// <summary>
    /// 试绑一个端口：先 localhost 前缀（http.sys 会同时接受 127.0.0.1 与 ::1，而网页那边填的正是
    /// localhost），失败再退 127.0.0.1；两个都不行返回 null，交给调用方换下一个端口。
    /// </summary>
    private HttpListener? TryListen(int port)
    {
        var listener = new HttpListener();
        foreach (string prefix in new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" })
        {
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add(prefix);
                listener.Start();
                return listener;
            }
            catch (Exception ex)
            {
                LastError = $"端口 {port}：{ex.Message}";
            }
        }
        try { listener.Close(); } catch { }
        return null;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        lock (_lock) Clients = 0;
        if (string.Equals(_engine.DirectInputOwner, Owner, StringComparison.Ordinal))
            _engine.ReleaseDirectInput(Owner);
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }   // Stop() 会让它抛异常，直接退出循环

            if (!ctx.Request.IsWebSocketRequest)
            {
                try
                {
                    byte[] body = Encoding.UTF8.GetBytes("Hexa Ayva 入口：请用 WebSocket 连 " + Url + "\n");
                    ctx.Response.StatusCode = 426;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body, token).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch { }
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(ctx, token), token);
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx, CancellationToken token)
    {
        // 来源校验必须在升级成 WebSocket **之前**做：升级之后就没法再拒了。
        if (!IsAllowedOrigin(ctx.Request.Headers["Origin"]))
        {
            try
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.Close();
            }
            catch { }
            AppLogger.Warn("已拒绝一个不在白名单里的网页来源：" + (ctx.Request.Headers["Origin"] ?? "(无)"));
            return;
        }

        WebSocket? ws = null;
        try
        {
            HttpListenerWebSocketContext wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
            ws = wsCtx.WebSocket;
            lock (_lock) Clients++;
            AppLogger.Info("Ayva 网页已连上，开始接收 TCode");

            var buffer = new byte[MaxFrameBytes];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, r.Count));
                if (!r.EndOfMessage)
                {
                    if (sb.Length > MaxFrameBytes * 4) sb.Clear();   // 异常超长帧直接丢，别把内存吃了
                    continue;
                }
                string text = sb.ToString();
                sb.Clear();
                Apply(text);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) RuntimeNote = "客户端断开：" + ex.Message;
        }
        finally
        {
            lock (_lock) Clients = Math.Max(0, Clients - 1);
            try { if (ws is not null) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { }

            // 断开就把控制权交还（最后一个网页走了才算）：否则声音响应 / 画面跟随 / 手动动轴
            // 会被一个"已经不存在的网页"永久挡住，悬浮窗还会一直显示"运行中"。
            bool lastOne;
            lock (_lock) lastOne = Clients == 0;
            if (lastOne) ReleaseOwner();

            AppLogger.Info("Ayva 网页已断开");
        }
    }

    /// <summary>一帧文本 = 一条 TCode（可能是 "L05000" 单轴，也可能是 "L05000 R05000" 多轴）。</summary>
    private void Apply(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return;

        double[] values;
        int transitionMs;
        bool stop;
        lock (_lock)
        {
            if (!GameTelemetryProtocol.TryParseTCode(Encoding.ASCII.GetBytes(trimmed), _axes, out GameTelemetryFrame frame))
                return;
            if (frame.Axes is { Length: >= 6 })
                for (int i = 0; i < 6; i++) _axes[i] = frame.Axes[i];
            values = (double[])_axes.Clone();
            transitionMs = frame.TransitionMs;
            stop = frame.IsStop;
        }

        Interlocked.Increment(ref Frames);
        LastCommand = trimmed;

        if (stop)
        {
            ReleaseOwner();
            return;
        }

        // 别人正在写就让位（照声音响应那条硬规矩来）：否则两边交替覆盖同一批轴，设备会抖；
        // 更糟的是我们抢走 owner 之后，对方收尾时的 ReleaseDirectInput 会静默失效
        //（它只释放自己持有的），owner 就变成僵尸，界面会一直说"某某正在控制设备"。
        string? currentOwner = _engine.DirectInputOwner;
        if (currentOwner is not null && !string.Equals(currentOwner, Owner, StringComparison.Ordinal))
        {
            RuntimeNote = $"现在被「{currentOwner}」占着，网页指令暂时让位";
            return;
        }

        // 与游戏桥同一档：网页遥控器是用户主动打开的，有指令就接管；
        // 急停/安全归中时 TrySendDirectAxes 自己会拒绝，不会硬来。
        _engine.TryClaimDirectInput(Owner, force: true);
        _engine.TrySendDirectAxes(values, Math.Clamp(transitionMs / 1000.0, 0.02, 0.5));
    }

    private void ReleaseOwner()
    {
        if (string.Equals(_engine.DirectInputOwner, Owner, StringComparison.Ordinal))
            _engine.ReleaseDirectInput(Owner);
    }
}
