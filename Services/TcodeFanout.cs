using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Hexa.Services;

/// <summary>
/// 多输出：把<b>同一条</b> TCode 同时发给别的目标 —— 第二台设备、VAM 里的 BusDriver、
/// 或任何听得懂 TCode 的软件。每条真正写到设备的指令都会镜像进来一份
/// （见 <see cref="SerialService.Mirror"/>），所以设备与网络目标始终同步，不会各走各的。
///
/// 目标一行一个，两种写法：
///   <c>udp://127.0.0.1:8000</c>   —— 裸 UDP，一行一条 TCode（Ayva 生态的惯例端口是 8000）
///   <c>ws://127.0.0.1:8581/ws</c> —— WebSocket 文本帧，一行一条 TCode（和 Ayva 的协议一致）
/// 连不上的目标会一直重试，不会拖累其它目标，也不会影响设备输出。
/// </summary>
public sealed class TcodeFanout : IDisposable
{
    private readonly object _lock = new();
    private readonly List<ITarget> _targets = [];

    /// <summary>总开关。关着时 <see cref="Publish"/> 直接返回，一条都不外发。
    /// 用 volatile 字段：UI 线程改、串口/音频线程读，普通属性可能读到旧值继续外发。</summary>
    private volatile bool _enabled;
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>累计外发的指令条数（界面上用来判断"到底有没有发出去"）。</summary>
    public long Sent;

    public IReadOnlyList<string> TargetStatuses
    {
        get { lock (_lock) return _targets.Select(t => t.Describe()).ToList(); }
    }

    /// <summary>上次 Configure 里"看不懂、被跳过"的目标行（给界面显示，别让用户以为它生效了）。</summary>
    public IReadOnlyList<string> ConfigurationWarnings { get; private set; } = Array.Empty<string>();

    /// <summary>用一段文本重建目标列表（每行一个目标）。UI 上改一次文本就调一次。</summary>
    public void Configure(string? spec)
    {
        var fresh = new List<ITarget>();
        var bad = new List<string>();
        foreach (string line in (spec ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ITarget? t = Create(line);
            if (t is null)
            {
                // 以前写错一行就被静默丢掉，用户改了目标框却"没反应也没报错"（子代理审计发现）。
                bad.Add(line);
                AppLogger.Warn($"多输出目标看不懂，已跳过：{line}（要写成 udp://主机:端口 或 ws://主机:端口/路径）");
                continue;
            }
            t.Start();
            fresh.Add(t);
        }
        ConfigurationWarnings = bad;

        List<ITarget> old;
        lock (_lock)
        {
            old = [.. _targets];
            _targets.Clear();
            _targets.AddRange(fresh);
        }
        foreach (ITarget t in old) t.Dispose();
    }

    /// <summary>把一条 TCode 发给所有目标。任何目标出错都只影响它自己。</summary>
    public void Publish(string tcode)
    {
        if (!_enabled || tcode.Length == 0) return;

        ITarget[] snapshot;
        lock (_lock)
        {
            if (_targets.Count == 0) return;
            snapshot = [.. _targets];
        }
        // 只有一个目标真的发出去了才 +1：否则界面会一边写"没连上（重试中）· 已发出 0 条"、
        // 一边写"已外发 1200 条"，用户会以为对面收到了。
        bool anySent = false;
        foreach (ITarget t in snapshot) if (t.Send(tcode)) anySent = true;
        if (anySent) Interlocked.Increment(ref Sent);
    }

    public void Dispose()
    {
        List<ITarget> old;
        lock (_lock) { old = [.. _targets]; _targets.Clear(); }
        foreach (ITarget t in old) t.Dispose();
    }

    private static ITarget? Create(string spec)
    {
        string s = spec.Trim();
        int schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return null;
        string scheme = s[..schemeEnd].ToLowerInvariant();
        string rest = s[(schemeEnd + 3)..];

        if (scheme == "udp")
        {
            int colon = rest.LastIndexOf(':');
            if (colon <= 0) return null;
            string host = rest[..colon];
            if (!int.TryParse(rest[(colon + 1)..], out int port) || port is < 1 or > 65535) return null;
            return new UdpTarget(s, host, port);
        }

        if (scheme is "ws" or "wss")
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out Uri? uri)) return null;
            return new WsTarget(s, uri);
        }

        return null;
    }

    private interface ITarget : IDisposable
    {
        void Start();

        /// <summary>发一条；返回 false ＝ 这一条没真发出去（连不上/已释放），用于让界面上的计数说实话。</summary>
        bool Send(string tcode);

        string Describe();
    }

    private sealed class UdpTarget : ITarget
    {
        private readonly string _spec;
        private readonly UdpClient _client = new();
        private readonly IPEndPoint _end;
        private long _sent;

        private readonly string? _resolveError;

        public UdpTarget(string spec, string host, int port)
        {
            _spec = spec;
            IPAddress? resolved = Resolve(host);
            // 解析不了域名时**绝不回落到 127.0.0.1**：那会让用户以为发给了家里那台机器，
            // 其实全发回了本机（子代理审计发现）。现在如实记下原因、这个目标算不可用。
            if (resolved is null)
            {
                _resolveError = $"域名解析不了：{host}（检查拼写，或直接填 IP）";
                resolved = IPAddress.Loopback;
            }
            _end = new IPEndPoint(resolved, port);
        }

        public void Start() { }

        public bool Send(string tcode)
        {
            if (_resolveError is not null) return false;
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(tcode + "\n");
                _client.Send(bytes, bytes.Length, _end);
                Interlocked.Increment(ref _sent);
                return true;
            }
            catch { return false; /* 发不出去就算了，不能拖累设备输出 */ }
        }

        public string Describe() => _resolveError is not null
            ? $"{_spec} · UDP · 不可用（{_resolveError}）"
            : $"{_spec} · UDP · 已发出 {Interlocked.Read(ref _sent)} 条（UDP 只是本地发出，对面收没收到这里看不到）";

        public void Dispose() { try { _client.Dispose(); } catch { } }
    }

    private sealed class WsTarget : ITarget
    {
        private readonly string _spec;
        private readonly Uri _uri;
        private readonly CancellationTokenSource _cts = new();
        private ClientWebSocket? _socket;
        private volatile bool _alive;
        private long _sent;
        private long _failed;
        private volatile string _lastError = "";

        public WsTarget(string spec, Uri uri) { _spec = spec; _uri = uri; }

        public void Start() => _ = Task.Run(KeepAliveAsync);

        private async Task KeepAliveAsync()
        {
            var buffer = new byte[256];
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var ws = new ClientWebSocket();
                    await ws.ConnectAsync(_uri, _cts.Token).ConfigureAwait(false);
                    _socket = ws;
                    _alive = true;
                    // 连上以后就等它断开（对方一般不发东西回来）
                    while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch (Exception ex)
                {
                    // 记下原因：以前把异常吞掉，界面上三种完全不同的失败（没监听/不是 WS 端点/域名解析不了）
                    // 都显示成同一句"没连上（每 2 秒重试）"，用户无从下手。
                    try { _lastError = ex.Message; } catch { }
                }
                _alive = false;
                _socket = null;
                try { await Task.Delay(2000, _cts.Token).ConfigureAwait(false); }
                catch { break; }
            }
        }

        public bool Send(string tcode)
        {
            ClientWebSocket? ws = _socket;
            if (ws is not { State: WebSocketState.Open }) return false;
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(tcode);
                // 计数必须在**发送真的完成之后**：以前先 +1 再 fire-and-forget，界面上"已发出 N 条"
                // 证明不了对面收到（异步失败只在 GC 时被观测到）。现在成功才 +1、失败记到 _failed。
                _ = SendAndCountAsync(ws, bytes);
                return true;
            }
            catch { Interlocked.Increment(ref _failed); return false; }
        }

        /// <summary>串行化发送：同一个 ClientWebSocket 上重叠 SendAsync 会抛 InvalidOperationException，
        /// 而引擎的连续输出（16–50ms 一帧）正好会让它重叠——不串行化就会大量丢帧，界面只显示「失败 N 条」。</summary>
        private readonly SemaphoreSlim _sendGate = new(1, 1);

        private async Task SendAndCountAsync(ClientWebSocket ws, byte[] bytes)
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
            catch { Interlocked.Increment(ref _failed); }
            finally { try { _sendGate.Release(); } catch { } }
        }

        public string Describe() =>
            $"{_spec} · WebSocket · {(_alive ? "已连接" : "没连上（每 2 秒重试）")} · 已发出 {Interlocked.Read(ref _sent)} 条"
            + (_lastError is { Length: > 0 } err ? $" · 原因：{err}" : "")
            + (Interlocked.Read(ref _failed) > 0 ? $" · 失败 {Interlocked.Read(ref _failed)} 条" : "");

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _socket?.Dispose(); } catch { }
            try { _lastError = ""; } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }

    /// <summary>解析目标主机；解析不了返回 null（调用方据此把目标标成不可用，绝不静默回落到本机）。</summary>
    private static IPAddress? Resolve(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed)) return parsed;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        try
        {
            return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch { return null; }
    }
}
