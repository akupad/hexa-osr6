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
    private double _rotateAmplitude;
    private double _rotateHz;
    private double _rotatePhase;
    private DateTime _lastCommandUtc = DateTime.MinValue;
    /// <summary>这条指令跑到什么时候为止（Lovense 一条 Function 会带 timeSec，游戏不会再发第二遍）。</summary>
    private DateTime _runUntilUtc = DateTime.MinValue;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    /// <summary>上一条动作指令到达的时刻：游戏一抽发一条，间隔就是动作周期。</summary>
    private DateTime _lastArrivalUtc = DateTime.MinValue;
    private double _intervalSeconds;
    /// <summary>相位修正量：每条指令到达时把相位误差记进来，几百毫秒内衰减掉，相当于轻轻加/减速而不是一下子跳过去。</summary>
    private double _phaseOffset;
    private bool _phaseLockPending;
    private double _smoothedAmplitude = -1;
    private double _smoothedRotateAmplitude = -1;
    /// <summary>timeSec 用完但这一抽还没走完时置位：等它走完再回中位，别在半路上突然停。</summary>
    private bool _finishing;
    private double _lastPhaseSeen;
    private DateTime _finishStartedUtc = DateTime.MinValue;
    private DateTime _blockedSinceUtc = DateTime.MinValue;
    private volatile int _depthPercent = LovenseProtocol.DefaultDepthPercent;
    private volatile bool _sawClient;
    private volatile string _toyName = LovenseProtocol.DefaultToyName;

    public LovenseBridgeService(MotionEngine engine) => _engine = engine;

    /// <summary>报给游戏的那台玩具名（nora / max / …）。游戏按名字推它能做什么动作，所以这个名字决定它发 Vibrate 还是 Rotate。</summary>
    public string ToyName
    {
        get => _toyName;
        set => _toyName = LovenseProtocol.Sanitize(value);
    }

    /// <summary>深度滑杆：抽插行程按这个百分比缩（10–100），改了立刻生效，不用等游戏再发指令。</summary>
    public int DepthPercent
    {
        get => _depthPercent;
        set => _depthPercent = Math.Clamp(value, LovenseProtocol.MinDepthPercent, 100);
    }

    public bool IsRunning { get; private set; }
    public int HttpsPort { get; private set; }
    public int HttpPort { get; private set; }
    public string LastError { get; private set; } = "";

    /// <summary>
    /// 证书有没有进「当前用户 → 受信任的根」。游戏（Unity）连的是 https，证书不被信任时它会直接报
    /// Cert verify failed 然后当没这台玩具 —— 所以这个值决定界面上那句"游戏会拒绝连接"的提示。
    /// </summary>
    public bool CertificateTrusted { get; private set; }

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
                string text = seen + " · " + _action + " " + _amplitude.ToString("0") + "% / " + _hz.ToString("0.0") + "Hz";
                if (_rotateAmplitude > 0) text += " + 扭 " + _rotateAmplitude.ToString("0") + "%";
                return text;
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
            _certificate = LoadOrCreateCertificate();
            // 只查不改：往「受信任的根」里塞证书会让 Windows 弹一次安全警告并卡住调用线程，
            // 所以这件事交给界面按钮用 certutil 在独立进程里做，服务本身保持无阻塞。
            CertificateTrusted = IsTrusted(_certificate);
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
            Trace($"accept tls={tls} from {client.Client?.RemoteEndPoint}");
            try
            {
                if (tls && _certificate is not null)
                {
                    ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsServerAsync(_certificate, false, false).ConfigureAwait(false);
                    stream = ssl;
                    Trace($"handshake ok {ssl.SslProtocol} cipher={ssl.NegotiatedCipherSuite}");
                }

                // 支持长连接：游戏每条动作都新建一条 TCP+TLS（实测 1775 次握手 / 1773 条请求），
                // 每多一次握手就多几毫秒不确定延迟，相位也就跟着飘。
                while (!token.IsCancellationRequested)
                {
                    bool keepAlive;
                    string method, path;
                    string? body;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        idle.CancelAfter(15000);
                        try
                        {
                            (method, path, body, keepAlive) = await ReadRequestAsync(stream, idle.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;   // 客户端闲着不发了，安静地关掉
                        }
                    }
                    Trace($"request {method} {path} body={body?.Length ?? 0}");
                    if (path.Length == 0) return;

                    if (body is { Length: > 0 }) Trace("body " + (body.Length > 200 ? body[..200] + "…" : body));
                    LovenseProtocol.Outcome outcome = LovenseProtocol.Handle(method, path, body, _toyName);
                    (string status, byte[] payload) = await BuildResponseAsync(method, path, outcome).ConfigureAwait(false);
                    await WriteResponseAsync(stream, status, "application/json; charset=utf-8", payload, keepAlive).ConfigureAwait(false);
                    Apply(outcome);
                    if (!keepAlive) return;
                }
            }
            catch (Exception ex)
            {
                // 游戏半路断开、证书被拒都走这儿：连接是短连接，坏一条不影响服务。
                Trace("error " + ex.GetType().Name + ": " + ex.Message);
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
            if (outcome.Action == "Stop" || (outcome.Amplitude <= 0 && outcome.RotateAmplitude <= 0))
            {
                _action = "";
                _amplitude = 0;
                _rotateAmplitude = 0;
                _engine.TrySendDirectAxes([50, 50, 50, 50, 50, 50], 0.45);
                return;
            }
            // 游戏基本是一抽发一条指令，间隔就是这个动作的真实周期；断流、卡顿时退回强度算出来的频率。
            DateTime arrival = DateTime.UtcNow;
            if (_lastArrivalUtc != DateTime.MinValue)
            {
                double interval = (arrival - _lastArrivalUtc).TotalSeconds;
                if (double.IsFinite(interval) && interval is >= 0.15 and <= 3.0)
                    _intervalSeconds = _intervalSeconds <= 0
                        ? interval
                        : _intervalSeconds * 0.7 + interval * 0.3;
            }
            _lastArrivalUtc = arrival;

            _action = outcome.Action;
            _amplitude = outcome.Amplitude;
            // 游戏一抽发一条：动作要从这条指令到达的瞬间开始（真正对齐在 Tick 里做，
            // 因为被限速器压慢时不该对齐——那样它永远追不上，反而一顿一顿）。
            _phaseLockPending = true;
            _finishing = false;
            _finishStartedUtc = DateTime.MinValue;
            _hz = LovenseProtocol.TempoHz(_intervalSeconds, Math.Clamp(outcome.Hz, 0.1, 3.0));
            _rotateAmplitude = outcome.RotateAmplitude;
            _rotateHz = Math.Clamp(outcome.RotateHz, 0.1, 2.0);
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
        double amplitude, hz, rotateAmplitude, rotateHz;
        int depth;
        DateTime last, until;
        lock (_gate)
        {
            action = _action;
            amplitude = _amplitude;
            hz = _hz;
            rotateAmplitude = _rotateAmplitude;
            rotateHz = _rotateHz;
            depth = _depthPercent;
            last = _lastCommandUtc;
            until = _runUntilUtc;
        }
        if (action.Length == 0) return;

        // timeSec 用完（游戏关了、暂停了也一样）：先把这一抽走完再回中位，中途掐断会明显一顿。
        if (now > until && !_finishing)
        {
            _finishing = true;
            _finishStartedUtc = now;
            _lastPhaseSeen = _phase;
        }

        // 深度滑杆决定走多长，舒适档的速度上限决定能跑多快；两者打架时压频率，
        // 否则限速器会把整段波形削圆（行程到不了、还滞后几十上百毫秒）。
        double speedLimit = _engine.ActiveComfortProfile.MaxAxisSpeedPerSecond;
        amplitude = LovenseProtocol.ScaleAmplitude(amplitude, depth);
        rotateAmplitude = LovenseProtocol.ScaleAmplitude(rotateAmplitude, depth);
        // 游戏强度是一档一档跳的（13→19）：直接跟会让行程一步甩出去，这里用 120ms 平滑过去。
        double blend = Math.Clamp(dt / 0.12, 0, 1);
        _smoothedAmplitude = _smoothedAmplitude < 0 ? amplitude : _smoothedAmplitude + (amplitude - _smoothedAmplitude) * blend;
        _smoothedRotateAmplitude = _smoothedRotateAmplitude < 0 ? rotateAmplitude : _smoothedRotateAmplitude + (rotateAmplitude - _smoothedRotateAmplitude) * blend;
        amplitude = _smoothedAmplitude;
        rotateAmplitude = _smoothedRotateAmplitude;
        double commandedHz = hz;
        hz = LovenseProtocol.FitHz(hz, amplitude, speedLimit);
        rotateHz = LovenseProtocol.FitHz(rotateHz, rotateAmplitude, speedLimit);
        if (_phaseLockPending)
        {
            _phaseLockPending = false;
            // 只有真能按游戏的节拍跑时才对齐；被物理限速压慢时对齐没有意义（它根本追不上）。
            if (hz >= commandedHz * 0.95)
                _phaseOffset = Math.Clamp(-LovenseProtocol.PhaseError(_phase), -0.25, 0.25);
        }

        // 抽插和转动各走各的相位：一条命令里同时有 Vibrate 和 Rotate 时两根轴一起动。
        double half = amplitude / 2.0;
        double main = 50;
        if (amplitude > 0)
        {
            _phase = LovenseProtocol.Frac(_phase + dt * hz);
            // 用「相位 + 修正量」出波形：修正量在 150ms 内衰减到 0，看起来只是这一抽略快或略慢，不会跳。
            double shown = LovenseProtocol.Frac(_phase + _phaseOffset);
            _phaseOffset *= Math.Exp(-dt / 0.15);
            main = 50 + half * Math.Sin(2 * Math.PI * shown);
        }
        double twist = 50;
        if (rotateAmplitude > 0)
        {
            _rotatePhase += dt * rotateHz;
            if (_rotatePhase >= 1.0) _rotatePhase -= Math.Floor(_rotatePhase);
            twist = 50 + rotateAmplitude / 2.0 * Math.Sin(2 * Math.PI * _rotatePhase);
        }
        // 收尾：这一抽绕回起点（或等满 1.5 秒）就停在中位。
        if (_finishing)
        {
            bool wrapped = _phase < _lastPhaseSeen;
            _lastPhaseSeen = _phase;
            if (wrapped || (now - _finishStartedUtc).TotalSeconds > 1.5)
            {
                lock (_gate)
                {
                    _action = "";
                    _finishing = false;
                    _finishStartedUtc = DateTime.MinValue;
                }
                _engine.TrySendDirectAxes([50, 50, 50, 50, 50, 50], 0.45);
                return;
            }
        }

        bool sent = _engine.TrySendDirectAxes(
            [main, 50 + (main - 50) * 0.10, 50 + (main - 50) * 0.08, twist, 50, 50], 0.06);

        // 帧被拒＝设备当场停住，而这条路径以前完全静默。连续被拒超过 0.4 秒记一条，测试完能直接看出卡在哪。
        if (sent)
        {
            _blockedSinceUtc = DateTime.MinValue;
            return;
        }
        if (_blockedSinceUtc == DateTime.MinValue)
        {
            _blockedSinceUtc = now;
            return;
        }
        if ((now - _blockedSinceUtc).TotalMilliseconds >= 400)
        {
            Trace($"frame blocked {(now - _blockedSinceUtc).TotalMilliseconds:0}ms: {_engine.DirectInputBlockReason}");
            _blockedSinceUtc = now;
        }
    }

    /// <summary>证书放这儿：每次启动都重生成的话，游戏那边每次都得重新信任一次，等于永远连不上。</summary>
    private static string CertificateFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexa");

    private static string CertificateFile => Path.Combine(CertificateFolder, "lovense-bridge.pfx");
    private static string CertificateSanFile => Path.Combine(CertificateFolder, "lovense-bridge.san.txt");

    /// <summary>这台机器现在该被证书认得的所有名字：回环、0.0.0.0、以及本机每个局域网 IP。</summary>
    private static List<string> DesiredNames()
    {
        var names = new List<string> { "127-0-0-1.lovense.club", "localhost", "127.0.0.1", "0.0.0.0" };
        foreach (string address in LocalAddresses())
            if (!names.Contains(address)) names.Add(address);
        return names;
    }

    /// <summary>复用旧证书（名字对得上、没过期）；换网络导致 IP 变了就重生成，否则游戏连新地址会证书不匹配。</summary>
    private static X509Certificate2 LoadOrCreateCertificate()
    {
        List<string> wanted = DesiredNames();
        try
        {
            if (File.Exists(CertificateFile) && File.Exists(CertificateSanFile))
            {
                string[] saved = File.ReadAllLines(CertificateSanFile);
                bool sameNames = saved.Length == wanted.Count && wanted.All(saved.Contains);
                X509Certificate2 cached = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(CertificateFile), null);
                if (sameNames && cached.NotAfter > DateTime.Now.AddDays(30) && cached.HasPrivateKey)
                    return cached;
                cached.Dispose();
            }
        }
        catch
        {
            // 文件坏了就当没有，下面重生成一份。
        }

        X509Certificate2 fresh = CreateCertificate(wanted, out byte[] freshPfx);
        try
        {
            Directory.CreateDirectory(CertificateFolder);
            // 必须写「刚生成那份」的字节：从 LoadPkcs12 拿回来的证书再 Export(Pfx) 会抛
            // CryptographicException「该项不适于在指定状态下使用」（临时密钥句柄不可再导出）。
            File.WriteAllBytes(CertificateFile, freshPfx);
            File.WriteAllLines(CertificateSanFile, wanted);
        }
        catch (Exception ex)
        {
            // 存不下去也不影响本次运行，只是下次启动又生成一份。
            try { File.AppendAllText(Path.Combine(CertificateFolder, "lovense-cert.log"), DateTime.Now + " save failed: " + ex + Environment.NewLine); } catch { }
        }
        return fresh;
    }

    /// <summary>证书现在有没有被系统信任（只读检查，不会弹任何窗口）。</summary>
    public static bool IsTrusted(X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把证书的公钥部分导出成 .cer，放在证书旁边。界面用 certutil -addstore -user Root 装它，
    /// 这样 Windows 那次确认框弹在独立进程里，不会把界面线程卡住。
    /// </summary>
    public string ExportTrustFile()
    {
        if (_certificate is null) return "";
        string path = Path.Combine(CertificateFolder, "lovense-bridge.cer");
        try
        {
            Directory.CreateDirectory(CertificateFolder);
            File.WriteAllBytes(path, _certificate.Export(X509ContentType.Cert));
            return path;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>装完证书后由界面回调，刷新这个标记。</summary>
    public void RefreshTrustState()
    {
        X509Certificate2? certificate = _certificate;
        if (certificate is not null) CertificateTrusted = IsTrusted(certificate);
    }

    private static X509Certificate2 CreateCertificate(List<string> names, out byte[] pfxBytes)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=lovense-bridge.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (string name in names)
        {
            if (IPAddress.TryParse(name, out IPAddress? address)) san.AddIpAddress(address);
            else san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        // 故意不写成 CA:true：CA 证书装进受信任根时 Windows 会弹一次"安全警告"并卡住调用线程，
        // 而 CA:false 的叶子证书是静默安装的，curl 实测（不带 -k）能通过校验，够用。
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using X509Certificate2 selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        // SslStream 在 Windows 上要用带私钥的 PFX 往返一次，否则握手会报「凭据没有私钥」。
        pfxBytes = selfSigned.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, null);
    }

    private static async Task<(string Method, string Path, string? Body, bool KeepAlive)> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[2048];
        int headerEnd = -1;
        while (headerEnd < 0 && buffer.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read <= 0) return ("", "", null, false);
            buffer.Write(chunk, 0, read);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
        }
        if (headerEnd < 0) return ("", "", null, false);

        byte[] bytes = buffer.GetBuffer();
        string head = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        string[] lines = head.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return ("", "", null, false);
        string method = requestLine[0].ToUpperInvariant();
        string path = requestLine[1];

        int contentLength = 0;
        bool keepAlive = true;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string name = lines[i][..colon].Trim();
            string value = lines[i][(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, out contentLength);
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                keepAlive = !value.Contains("close", StringComparison.OrdinalIgnoreCase);
        }

        // 有些 HTTP 客户端（Unity 的 curl）会先发 Expect: 100-continue 等响应再发 body，不回它就一直等到超时。
        if (head.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
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
        return (method, path, body.Length > 0 ? body : null, keepAlive);
    }

    /// <summary>排查用：把每条连接的关键动作写进 lovense-trace.log（游戏那边只报超时，看不到细节）。</summary>
    private static void Trace(string line)
    {
        try
        {
            string path = Path.Combine(CertificateFolder, "lovense-trace.log");
            FileInfo info = new(path);
            if (info.Exists && info.Length > 2 * 1024 * 1024) File.Delete(path);
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
        }
        catch
        {
        }
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

    private static async Task WriteResponseAsync(Stream stream, string status, string contentType, byte[] payload, bool keepAlive)
    {
        string head = "HTTP/1.1 " + status + "\r\n"
            + "Content-Type: " + contentType + "\r\n"
            + "Content-Length: " + payload.Length.ToString() + "\r\n"
            + (keepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
