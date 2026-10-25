using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Hexa.Services;

/// <summary>
/// 冒充 Lovense Remote 的「游戏模式」本地服务：游戏那一栏填本机 IP + 30010 就能连上，
/// 它发来的振动/转动强度被翻成 OSR6 的抽插动作。
///
/// 为什么不用 HttpListener：它绑 https 到非 localhost 要提前 netsh 加 URL ACL（要管理员），
/// 这里自己起 TcpListener + SslStream，证书现生成，不依赖系统状态也不弹提权。
/// </summary>
public sealed class LovenseBridgeService : IDisposable
{
    /// <summary>Lovense 的明文端口，老式 /Vibrate?v= 接口走这个；和 SSL 端口成对出现。</summary>
    public const int DefaultHttpPort = 20010;

    private const int TickMs = 33;

    private readonly MotionEngine _engine;
    private readonly object _gate = new();

    private TcpListener? _httpsListener;
    private TcpListener? _httpListener;
    private X509Certificate2? _certificate;
    private CancellationTokenSource? _cancellation;
    private System.Threading.Timer? _ticker;

    private string _action = "";
    private double _amplitude;
    private double _hz;
    private double _phase;
    private DateTime _lastCommandUtc = DateTime.MinValue;
    /// <summary>这条指令跑到什么时候为止（Lovense 一条 Function 会带 timeSec，游戏不会再发第二遍）。</summary>
    private DateTime _runUntilUtc = DateTime.MinValue;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private volatile bool _sawClient;

    public LovenseBridgeService(MotionEngine engine) => _engine = engine;

    public bool IsRunning { get; private set; }
    public int HttpsPort { get; private set; }
    public int HttpPort { get; private set; }
    public string LastError { get; private set; } = "";

    /// <summary>
    /// 界面用：本机能被局域网访问的 IPv4 地址，游戏里那一栏「Lovense IP 地址」要填的就是它。
    /// 回环地址不列（游戏在另一台机器/头显上，填 127.0.0.1 连不回来）。
    /// </summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var found = new List<string>();
        try
        {
            foreach (System.Net.NetworkInformation.NetworkInterface nic in
                     System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation info in
                         nic.GetIPProperties().UnicastAddresses)
                {
                    System.Net.IPAddress address = info.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (System.Net.IPAddress.IsLoopback(address)) continue;
                    string text = address.ToString();
                    if (!found.Contains(text)) found.Add(text);
                }
            }
        }
        catch (Exception ex)
        {
            LastAddressError = ex.Message;
        }
        return found;
    }

    private static string LastAddressError = "";

    /// <summary>界面用：游戏连上过没有、现在在跑什么强度。</summary>
    public string Status
    {
        get
        {
            if (!IsRunning) return LastError.Length > 0 ? "启动失败：" + LastError : "未开启";
            lock (_gate)
            {
                string seen = _sawClient ? "游戏已连上" : "等游戏连接";
                if (_action.Length == 0 || _action == "Stop") return seen + " · 待机";
                return seen + " · " + _action + " " + _amplitude.ToString("0") + "% / " + _hz.ToString("0.0") + "Hz";
            }
        }
    }

    public void Start(int httpsPort, int httpPort)
    {
        Stop();
        HttpsPort = httpsPort;
        HttpPort = httpPort;
        LastError = "";
        try
        {
            _certificate = CreateCertificate();
            _cancellation = new CancellationTokenSource();

            _httpsListener = new TcpListener(IPAddress.Any, httpsPort);
            _httpsListener.Start();
            _ = AcceptLoopAsync(_httpsListener, true, _cancellation.Token);

            if (httpPort > 0 && httpPort != httpsPort)
            {
                _httpListener = new TcpListener(IPAddress.Any, httpPort);
                _httpListener.Start();
                _ = AcceptLoopAsync(_httpListener, false, _cancellation.Token);
            }

            _lastTickUtc = DateTime.UtcNow;
            _ticker = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
            IsRunning = true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
        }
    }

    public void Stop()
    {
        _ticker?.Dispose();
        _ticker = null;
        _cancellation?.Cancel();
        try { _httpsListener?.Stop(); } catch { }
        try { _httpListener?.Stop(); } catch { }
        _httpsListener = null;
        _httpListener = null;
        _cancellation?.Dispose();
        _cancellation = null;
        _certificate?.Dispose();
        _certificate = null;
        IsRunning = false;
        lock (_gate)
        {
            _action = "";
            _sawClient = false;
        }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, bool tls, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client, tls, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, bool tls, CancellationToken token)
    {
        using (client)
        {
            client.NoDelay = true;
            Stream stream = client.GetStream();
            SslStream? ssl = null;
            try
            {
                if (tls && _certificate is not null)
                {
                    ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsServerAsync(_certificate, false, false).ConfigureAwait(false);
                    stream = ssl;
                }

                (string method, string path, string? body) = await ReadRequestAsync(stream, token).ConfigureAwait(false);
                if (path.Length == 0) return;

                LovenseProtocol.Outcome outcome = LovenseProtocol.Handle(method, path, body);
                (string status, byte[] payload) = await BuildResponseAsync(method, path, outcome).ConfigureAwait(false);
                await WriteResponseAsync(stream, status, "application/json; charset=utf-8", payload).ConfigureAwait(false);
                Apply(outcome);
            }
            catch
            {
                // 游戏半路断开、证书被拒都走这儿：连接是短连接，坏一条不影响服务。
            }
            finally
            {
                ssl?.Dispose();
            }
        }
    }

    private async Task<(string Status, byte[] Payload)> BuildResponseAsync(string method, string path, LovenseProtocol.Outcome outcome)
    {
        // Lovense 的 web 控制页会先 GET 一个页面；游戏一般直接 POST /command。
        string verb = path.Trim('/').ToLowerInvariant();
        if (method == "GET" && verb.Length == 0)
            return ("200 OK", Encoding.UTF8.GetBytes("<html><body>Hexa · Lovense bridge</body></html>"));

        if (method == "OPTIONS") return ("200 OK", []);
        await Task.CompletedTask;
        return (outcome.Ok ? "200 OK" : "200 OK", Encoding.UTF8.GetBytes(outcome.Body));
    }

    private void Apply(LovenseProtocol.Outcome outcome)
    {
        lock (_gate)
        {
            _sawClient = true;
            _lastCommandUtc = DateTime.UtcNow;
            if (!outcome.Ok) return;
            if (outcome.Action.Length == 0) return;          // GetToys 这类查询不动设备
            if (outcome.Action == "Stop" || outcome.Amplitude <= 0)
            {
                _action = "";
                _engine.TrySendDirectAxes([50, 50, 50, 50, 50, 50], 0.45);
                return;
            }
            _action = outcome.Action;
            _amplitude = outcome.Amplitude;
            _hz = Math.Clamp(outcome.Hz, 0.1, 3.0);
            // 游戏一条命令只发一次，时长靠 timeSec 说明（缺省 20 秒，和 Lovense 官方默认一致）。
            double seconds = outcome.Seconds > 0 ? Math.Clamp(outcome.Seconds, 0.2, 3600) : 20;
            _runUntilUtc = _lastCommandUtc.AddSeconds(seconds);
        }
    }

    private void Tick()
    {
        if (!IsRunning) return;
        DateTime now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0.005, 0.2);
        _lastTickUtc = now;

        string action;
        double amplitude, hz;
        DateTime last, until;
        lock (_gate)
        {
            action = _action;
            amplitude = _amplitude;
            hz = _hz;
            last = _lastCommandUtc;
            until = _runUntilUtc;
        }
        if (action.Length == 0) return;

        // 到时收尾：timeSec 用完（游戏关了、暂停了也一样），别让设备一直抽。
        if (now > until)
        {
            lock (_gate) { _action = ""; }
            _engine.TrySendDirectAxes([50, 50, 50, 50, 50, 50], 0.45);
            return;
        }

        double half = amplitude / 2.0;
        _phase += dt * hz;
        if (_phase >= 1.0) _phase -= Math.Floor(_phase);
        double main = 50 + half * Math.Sin(2 * Math.PI * _phase);
        if (action.Equals("Rotate", StringComparison.OrdinalIgnoreCase))
            _engine.TrySendDirectAxes([50, 50, 50, main, 50, 50], 0.06);
        else
            _engine.TrySendDirectAxes([main, 50 + (main - 50) * 0.10, 50 + (main - 50) * 0.08, 50, 50, 50], 0.06);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=lovense-bridge.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("127-0-0-1.lovense.club");
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.Any);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using X509Certificate2 selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        // SslStream 在 Windows 上要用带私钥的 PFX 往返一次，否则握手会报「凭据没有私钥」。
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
    }

    private static async Task<(string Method, string Path, string? Body)> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[2048];
        int headerEnd = -1;
        while (headerEnd < 0 && buffer.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read <= 0) return ("", "", null);
            buffer.Write(chunk, 0, read);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
        }
        if (headerEnd < 0) return ("", "", null);

        byte[] bytes = buffer.GetBuffer();
        string head = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        string[] lines = head.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return ("", "", null);
        string method = requestLine[0].ToUpperInvariant();
        string path = requestLine[1];

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0 && lines[i][..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(lines[i][(colon + 1)..].Trim(), out contentLength);
        }

        int bodyStart = headerEnd + 4;
        int already = (int)buffer.Length - bodyStart;
        while (already < contentLength)
        {
            int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read <= 0) break;
            buffer.Write(chunk, 0, read);
            already += read;
        }
        string body = contentLength > 0 && already > 0
            ? Encoding.UTF8.GetString(bytes, bodyStart, Math.Min(contentLength, (int)buffer.Length - bodyStart))
            : "";
        return (method, path, body.Length > 0 ? body : null);
    }

    private static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= length; i++)
        {
            bool hit = true;
            for (int k = 0; k < needle.Length; k++)
                if (haystack[i + k] != needle[k]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    private static async Task WriteResponseAsync(Stream stream, string status, string contentType, byte[] payload)
    {
        string head = "HTTP/1.1 " + status + "\r\n"
            + "Content-Type: " + contentType + "\r\n"
            + "Content-Length: " + payload.Length.ToString() + "\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
