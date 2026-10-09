using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Hexa.Models;

namespace Hexa.Services;

public sealed record GameTelemetrySnapshot(GameTelemetryFrame Frame, long ReceivedAt);

public sealed class GameTelemetryService : IDisposable
{
    public const int DefaultPort = 26781;

    private static readonly TimeSpan SessionRetention = TimeSpan.FromMinutes(10);
    private const int MaxAuthenticatedSessions = 64;
    private const int MaxRawSenders = 8;

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _authToken;
    private readonly Func<bool> _allowRawTCode;
    private readonly Func<string> _rawTargetProcess;
    private readonly Dictionary<string, AuthSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameTelemetrySnapshot> _latestByProcess =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPEndPoint, RawTCodeState> _rawTCodeBySender = [];

    private UdpClient? _client;
    private long _acceptedPackets;
    private long _rejectedPackets;

    public int Port { get; private set; }
    public bool Listening => _client != null;
    public long AcceptedPackets => Interlocked.Read(ref _acceptedPackets);
    public long RejectedPackets => Interlocked.Read(ref _rejectedPackets);
    public event Action<GameTelemetrySnapshot>? FrameReceived;

    public GameTelemetryService(
        int port = DefaultPort,
        string? authToken = null,
        Func<bool>? allowRawTCode = null,
        Func<string>? rawTargetProcess = null)
    {
        _authToken = (authToken ?? "").Trim();
        _allowRawTCode = allowRawTCode ?? (() => false);
        _rawTargetProcess = rawTargetProcess ?? (() => "");
        Start(port);
    }

    public bool TryGetLatest(string? processName, TimeSpan maxAge, out GameTelemetrySnapshot snapshot)
    {
        snapshot = null!;
        string target = GameTelemetryProtocol.NormalizeProcessName(processName);
        if (target.Length == 0) return false;

        lock (_lock)
        {
            if (!_latestByProcess.TryGetValue(target, out GameTelemetrySnapshot? latest)) return false;
            if (Stopwatch.GetElapsedTime(latest.ReceivedAt) > maxAge) return false;
            snapshot = CloneSnapshot(latest);
            return true;
        }
    }

    private void Start(int port)
    {
        try
        {
            _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, Math.Clamp(port, 0, 65535)));
            Port = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;
            _ = ReceiveLoopAsync(_client, _cts.Token);
            AppLogger.Info($"Game telemetry listening on 127.0.0.1:{Port}");
        }
        catch (Exception ex)
        {
            _client?.Dispose();
            _client = null;
            AppLogger.Error("Game telemetry listener failed to start", ex);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult packet = await client.ReceiveAsync(token).ConfigureAwait(false);
                if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address))
                {
                    Interlocked.Increment(ref _rejectedPackets);
                    continue;
                }

                GameTelemetrySnapshot? snapshot = null;
                if (GameTelemetryProtocol.TryParse(packet.Buffer, out GameTelemetryFrame authenticatedFrame))
                    TryAcceptAuthenticated(packet.RemoteEndPoint, authenticatedFrame, out snapshot);
                else if (_allowRawTCode())
                    TryAcceptRawTCode(packet.RemoteEndPoint, packet.Buffer, out snapshot);

                if (snapshot == null)
                {
                    Interlocked.Increment(ref _rejectedPackets);
                    continue;
                }

                Interlocked.Increment(ref _acceptedPackets);
                RaiseFrameReceived(snapshot);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                AppLogger.Error("Game telemetry receive failed", ex);
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
    }

    private bool TryAcceptAuthenticated(
        IPEndPoint sender,
        GameTelemetryFrame frame,
        out GameTelemetrySnapshot? snapshot)
    {
        snapshot = null;
        if (frame.ProtocolVersion != 1
            || !TokenMatches(frame.Token)
            || frame.SessionId.Length == 0
            || frame.Sequence <= 0
            || frame.ProcessId <= 0)
            return false;

        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (frame.Timestamp < unixMs - 10_000 || frame.Timestamp > unixMs + 2_000) return false;

        long receivedAt = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            PruneSessionsLocked(receivedAt);
            if (!_sessions.TryGetValue(frame.SessionId, out AuthSessionState? session))
            {
                if (_sessions.Count >= MaxAuthenticatedSessions) return false;
                if (!ProcessIdentityMatches(frame.ProcessId, frame.Process)) return false;
                session = new AuthSessionState(
                    CopyEndpoint(sender),
                    frame.Process,
                    frame.ProcessId,
                    frame.Sequence,
                    frame.Timestamp,
                    receivedAt);
                _sessions.Add(frame.SessionId, session);
            }
            else
            {
                if (!session.Sender.Equals(sender)
                    || session.ProcessId != frame.ProcessId
                    || !string.Equals(session.Process, frame.Process, StringComparison.OrdinalIgnoreCase)
                    || frame.Sequence <= session.LastSequence
                    || frame.Timestamp < session.LastTimestamp)
                    return false;

                session.LastSequence = frame.Sequence;
                session.LastTimestamp = frame.Timestamp;
                session.LastReceivedAt = receivedAt;
            }

            frame.Token = "";
            GameTelemetrySnapshot stored = new(CloneFrame(frame), receivedAt);
            _latestByProcess[frame.Process] = stored;
            snapshot = CloneSnapshot(stored);
            return true;
        }
    }

    private bool TryAcceptRawTCode(
        IPEndPoint sender,
        ReadOnlySpan<byte> packet,
        out GameTelemetrySnapshot? snapshot)
    {
        snapshot = null;
        string target = GameTelemetryProtocol.NormalizeProcessName(_rawTargetProcess());
        if (target.Length == 0) return false;

        long receivedAt = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            PruneSessionsLocked(receivedAt);
            if (!_rawTCodeBySender.TryGetValue(sender, out RawTCodeState? state))
            {
                if (_rawTCodeBySender.Count >= MaxRawSenders) return false;
                state = new RawTCodeState(
                    [50, 50, 50, 50, 50, 50],
                    $"raw-tcode-{Guid.NewGuid():N}",
                    receivedAt);
                _rawTCodeBySender.Add(CopyEndpoint(sender), state);
            }

            if (!GameTelemetryProtocol.TryParseTCode(packet, state.Axes, out GameTelemetryFrame frame))
                return false;

            if (!frame.IsStop) Array.Copy(frame.Axes!, state.Axes, 6);
            state.Sequence++;
            state.LastReceivedAt = receivedAt;
            frame.Process = target;
            frame.SessionId = state.SessionId;
            frame.Sequence = state.Sequence;
            frame.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            GameTelemetrySnapshot stored = new(CloneFrame(frame), receivedAt);
            _latestByProcess[target] = stored;
            snapshot = CloneSnapshot(stored);
            return true;
        }
    }

    private void PruneSessionsLocked(long now)
    {
        if (_sessions.Count == 0) return;
        string[] stale = _sessions
            .Where(pair => Stopwatch.GetElapsedTime(pair.Value.LastReceivedAt, now) > SessionRetention)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string key in stale) _sessions.Remove(key);

        IPEndPoint[] staleRaw = _rawTCodeBySender
            .Where(pair => Stopwatch.GetElapsedTime(pair.Value.LastReceivedAt, now) > SessionRetention)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (IPEndPoint key in staleRaw) _rawTCodeBySender.Remove(key);
    }

    private bool TokenMatches(string supplied)
    {
        if (_authToken.Length == 0 || supplied.Length != _authToken.Length) return false;
        byte[] expected = Encoding.UTF8.GetBytes(_authToken);
        byte[] actual = Encoding.UTF8.GetBytes(supplied);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool ProcessIdentityMatches(int processId, string expectedProcess)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return string.Equals(
                GameTelemetryProtocol.NormalizeProcessName(process.ProcessName),
                expectedProcess,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void RaiseFrameReceived(GameTelemetrySnapshot snapshot)
    {
        Delegate[] handlers = FrameReceived?.GetInvocationList() ?? [];
        foreach (Delegate handler in handlers)
        {
            try { ((Action<GameTelemetrySnapshot>)handler)(CloneSnapshot(snapshot)); }
            catch (Exception ex) { AppLogger.Error("Game telemetry subscriber failed", ex); }
        }
    }

    private static GameTelemetrySnapshot CloneSnapshot(GameTelemetrySnapshot snapshot) =>
        new(CloneFrame(snapshot.Frame), snapshot.ReceivedAt);

    private static GameTelemetryFrame CloneFrame(GameTelemetryFrame frame) => new()
    {
        ProtocolVersion = frame.ProtocolVersion,
        Process = frame.Process,
        MessageType = frame.MessageType,
        Token = frame.Token,
        SessionId = frame.SessionId,
        Sequence = frame.Sequence,
        ProcessId = frame.ProcessId,
        Engine = frame.Engine,
        Scene = frame.Scene,
        Pose = frame.Pose,
        Active = frame.Active,
        Timestamp = frame.Timestamp,
        Phase = frame.Phase,
        Speed = frame.Speed,
        Depth = frame.Depth,
        Surge = frame.Surge,
        Sway = frame.Sway,
        Twist = frame.Twist,
        Roll = frame.Roll,
        Pitch = frame.Pitch,
        Intensity = frame.Intensity,
        Confidence = frame.Confidence,
        TransitionMs = frame.TransitionMs,
        Axes = frame.Axes?.ToArray(),
    };

    private static IPEndPoint CopyEndpoint(IPEndPoint endpoint) =>
        new(endpoint.Address, endpoint.Port);

    public void Dispose()
    {
        _cts.Cancel();
        _client?.Dispose();
        _client = null;
        _cts.Dispose();
    }

    private sealed class AuthSessionState(
        IPEndPoint sender,
        string process,
        int processId,
        long lastSequence,
        long lastTimestamp,
        long lastReceivedAt)
    {
        public IPEndPoint Sender { get; } = sender;
        public string Process { get; } = process;
        public int ProcessId { get; } = processId;
        public long LastSequence { get; set; } = lastSequence;
        public long LastTimestamp { get; set; } = lastTimestamp;
        public long LastReceivedAt { get; set; } = lastReceivedAt;
    }

    private sealed class RawTCodeState(double[] axes, string sessionId, long lastReceivedAt)
    {
        public double[] Axes { get; } = axes;
        public string SessionId { get; } = sessionId;
        public long Sequence { get; set; }
        public long LastReceivedAt { get; set; } = lastReceivedAt;
    }
}
