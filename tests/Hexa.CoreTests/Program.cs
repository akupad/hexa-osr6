using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Hexa.Models;
using Hexa.Services;

/// <summary>本机回环上这个 UDP 端口现在能不能绑上（遥测换端口建议用同一条判据）。</summary>
static bool LoopbackUdpPortFree(int port)
{
    try
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}

/// <summary>让联动器跑 12 帧「快上快下」，返回最终的扭转轴（R0）值。</summary>
static double RunLinkerTwist(bool[] scriptedAxes, double amount = 30)
{
    var link = new ScriptAxisLinker();
    double[] frame = [50, 50, 50, 50, 50, 50];
    for (int i = 0; i < 12; i++)
    {
        frame[0] = i % 2 == 0 ? 90 : 10;   // 每帧 0.05 秒走完 80% 行程 ≈ 一秒四个来回：典型快段落
        link.Apply(frame, scriptedAxes, amount, 0.05, 0, 100);
    }
    return frame[3];
}

/// <summary>快段落之后停住 2 秒，返回扭转轴的最终值（应该回到中位附近）。</summary>
static double RunLinkerThenStop()
{
    var link = new ScriptAxisLinker();
    bool[] onlyL0 = [true, false, false, false, false, false];
    double[] frame = [50, 50, 50, 50, 50, 50];
    for (int i = 0; i < 12; i++)
    {
        frame[0] = i % 2 == 0 ? 90 : 10;
        link.Apply(frame, onlyL0, 30, 0.05, 0, 100);
    }
    for (int i = 0; i < 40; i++)
    {
        frame[0] = 50;
        link.Apply(frame, onlyL0, 30, 0.05, 0, 100);
    }
    return frame[3];
}

/// <summary>主轴停在最顶端（92）时的填缝结果：必须等比缩小，不能撞到 100 被截平。</summary>
static double[] RunExtremeGapFill()
{
    var gaps = ScriptGapFiller.FindGaps([new WaveScriptCodec.ActionPoint(0, 92), new WaveScriptCodec.ActionPoint(4000, 92)], 1500);
    double[] frame = [92, 50, 50, 50, 50, 50];
    ScriptGapFiller.TryFill(frame, 2000, gaps, 60, true, 1.0, 0.25);
    return frame;
}


/// <summary>合成一段「呻吟」：基频 + 2/3 次谐波 + 颤音 + 呼吸噪声，包络慢起慢落。baseHz=0 时只留呼吸噪声。</summary>
static void WriteVoiceWav(string path, double seconds, double baseHz, bool withBreath)
{
    const int rate = 16000;
    int frames = (int)(rate * seconds);
    var random = new Random(11);
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    int dataBytes = frames * 2;
    writer.Write("RIFF"u8.ToArray());
    writer.Write(36 + dataBytes);
    writer.Write("WAVE"u8.ToArray());
    writer.Write("fmt "u8.ToArray());
    writer.Write(16); writer.Write((short)1); writer.Write((short)1);
    writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8.ToArray());
    writer.Write(dataBytes);
    for (int i = 0; i < frames; i++)
    {
        double t = i / (double)rate;
        // 每 1.4 秒一次呻吟：0.45 秒起音、0.55 秒保持、0.4 秒回落（慢起慢落）
        double phase = t % 1.4;
        double envelope = phase < 0.45 ? phase / 0.45 : (phase < 1.0 ? 1.0 : Math.Max(0, (1.4 - phase) / 0.4));
        double value = 0;
        if (baseHz > 0)
        {
            double vibrato = 1 + 0.03 * Math.Sin(2 * Math.PI * 5.5 * t);
            value += 0.42 * envelope * Math.Sin(2 * Math.PI * baseHz * vibrato * t);
            value += 0.16 * envelope * Math.Sin(2 * Math.PI * baseHz * 2 * vibrato * t);
            value += 0.08 * envelope * Math.Sin(2 * Math.PI * baseHz * 3 * vibrato * t);
        }
        if (withBreath) value += 0.05 * envelope * (random.NextDouble() * 2 - 1);
        writer.Write((short)(Math.Clamp(value, -1, 1) * 32767));
    }
}

/// <summary>把整段 WAV 喂给检测器，收集检出的事件。</summary>
static List<MotionEvent> DetectEvents(AudioEventDetector detector, string path)
{
    var events = new List<MotionEvent>();
    var scratch = new MotionEvent[AudioEventDetector.MaxEventsPerBlock];
    using var sourceReader = new NAudio.Wave.AudioFileReader(path);
    NAudio.Wave.ISampleProvider provider = sourceReader;
    var buffer = new float[sourceReader.WaveFormat.SampleRate / 10];
    int read;
    double at = 0;
    while ((read = provider.Read(buffer.AsSpan(0, buffer.Length))) > 0)
    {
        int count = detector.Process(buffer.AsSpan(0, read), sourceReader.WaveFormat.SampleRate, at, scratch);
        for (int i = 0; i < count; i++) events.Add(scratch[i]);
        at += read / (double)sourceReader.WaveFormat.SampleRate;
    }
    return events;
}

/// <summary>写一段纯净正弦（不带谐波）：用来验证"纯低音不会被人声模式当成呻吟"。</summary>
static void WriteSineWav(string path, double seconds, double hz)
{
    const int rate = 16000;
    int frames = (int)(rate * seconds);
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    int dataBytes = frames * 2;
    writer.Write("RIFF"u8.ToArray()); writer.Write(36 + dataBytes); writer.Write("WAVE"u8.ToArray());
    writer.Write("fmt "u8.ToArray());
    writer.Write(16); writer.Write((short)1); writer.Write((short)1);
    writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8.ToArray()); writer.Write(dataBytes);
    for (int i = 0; i < frames; i++)
    {
        double t = i / (double)rate;
        double envelope = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.7 * t);   // 慢起慢落，模仿音乐里的低音线条
        writer.Write((short)(envelope * 0.55 * Math.Sin(2 * Math.PI * hz * t) * short.MaxValue));
    }
}

/// <summary>喂一份素材，记录过程中「人声电平」的峰值（0–1）—— 角色声音模式的深度就看它。</summary>
static double PeakVoiceLevel(AudioEventDetector detector, string path)
{
    double peak = 0;
    var scratch = new MotionEvent[AudioEventDetector.MaxEventsPerBlock];
    using var sourceReader = new NAudio.Wave.AudioFileReader(path);
    NAudio.Wave.ISampleProvider provider = sourceReader;
    var buffer = new float[sourceReader.WaveFormat.SampleRate / 10];
    int read;
    double at = 0;
    while ((read = provider.Read(buffer.AsSpan(0, buffer.Length))) > 0)
    {
        detector.Process(buffer.AsSpan(0, read), sourceReader.WaveFormat.SampleRate, at, scratch);
        if (detector.VoiceLevel > peak) peak = detector.VoiceLevel;
        at += read / (double)sourceReader.WaveFormat.SampleRate;
    }
    return peak;
}

/// <summary>写一个 16bit 单声道测试 WAV：percussive=true 时每秒一次 60ms 冲击，否则是静音。</summary>
static void WriteTestWav(string path, double seconds, bool percussive)
{
    const int rate = 44100;
    int frames = (int)(rate * seconds);
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    int dataBytes = frames * 2;
    writer.Write("RIFF"u8.ToArray());
    writer.Write(36 + dataBytes);
    writer.Write("WAVE"u8.ToArray());
    writer.Write("fmt "u8.ToArray());
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(rate);
    writer.Write(rate * 2);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write("data"u8.ToArray());
    writer.Write(dataBytes);

    var random = new Random(7);
    for (int i = 0; i < frames; i++)
    {
        double t = i / (double)rate;
        double value = 0;
        if (percussive)
        {
            // 每秒一个 60ms 的低频冲击 + 一点噪声，模拟「重音」
            double phase = t % 1.0;
            if (phase < 0.06)
            {
                double envelope = Math.Exp(-phase / 0.02);
                value = 0.75 * envelope * (Math.Sin(2 * Math.PI * 70 * t) + 0.6 * (random.NextDouble() * 2 - 1));
            }
        }
        writer.Write((short)(Math.Clamp(value, -1, 1) * 32767));
    }
}

var passed = 0;
var failed = 0;

// 开发期探针：HEXA_SR6_RENDER=<dir> 时把 SR6 模型离屏渲染成 PNG（仅用于人工确认朝向）
if (Environment.GetEnvironmentVariable("HEXA_SR6_RENDER") is { Length: > 0 } renderDir)
{
    var probeThread = new System.Threading.Thread(() => Hexa.CoreTests.Sr6RenderProbe.Run(renderDir));
    probeThread.SetApartmentState(System.Threading.ApartmentState.STA);
    probeThread.Start();
    probeThread.Join();
    Console.WriteLine("render probe done");
    return 0;
}

void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS {name}"); passed++; }
    else { Console.WriteLine($"FAIL {name}"); failed++; }
}

try
{
    // 3D 预览逐帧采样轴位置（选中脚本 → 模型演动作）：口径必须与播放器一致（线性插值 + 首尾夹住）。
var sampleTrack = new List<WaveScriptCodec.ActionPoint> { new(0, 0), new(1000, 100) };
Check("轴采样：两个点之间按线性插值", Math.Abs(WaveScriptCodec.SamplePos(sampleTrack, 500) - 50) < 0.01);
Check("轴采样：首尾之外夹住、不越 0–100",
    WaveScriptCodec.SamplePos(sampleTrack, -500) == 0 && WaveScriptCodec.SamplePos(sampleTrack, 9000) == 100);
Check("轴采样：空轨道摆中位 50", WaveScriptCodec.SamplePos([], 1234) == 50);
Check("轴采样：位置字段脏值也被夹住",
    WaveScriptCodec.SamplePos([new WaveScriptCodec.ActionPoint(0, -20), new WaveScriptCodec.ActionPoint(1000, 300)], 500) == 100);
var parsed = WaveScriptCodec.ParseFunscript("{\"actions\":[{\"at\":1000,\"pos\":30},{\"at\":0,\"pos\":10},{\"at\":1000,\"pos\":60}]}");
    Check("funscript sort and duplicate policy", parsed.Count == 2 && parsed[0].At == 0 && parsed[1].Pos == 60);
    Check("canvas normalization", WaveScriptCodec.ToCanvasPoints(parsed)[^1].X == WaveScriptCodec.CanvasWidth);
    var shortTrackCanvas = WaveScriptCodec.ToCanvasPoints(parsed, timelineDurationMs: 2000);
    Check("short companion track keeps global timing", shortTrackCanvas[^1].X == WaveScriptCodec.CanvasWidth / 2.0);
    string exported = WaveScriptCodec.SerializeFunscript([(0, 240), (650, 0)], 2);
    Check("funscript export uses standard property names", exported.Contains("\"at\"") && exported.Contains("\"pos\""));

    bool rejected = false;
    try { WaveScriptCodec.ParseFunscript("{\"actions\":[{\"at\":0,\"pos\":101},{\"at\":1,\"pos\":0}]}"); }
    catch (FormatException) { rejected = true; }
    Check("funscript invalid position rejected", rejected);

    // 2026-11-25：整片的 Cock Hero 类脚本常见 60–95 分钟，原来的 60 分钟上限把它们全判成「无法解析」
    long longMs = 5_538_000;   // Cock Hero - Optikon Alpha 的真实长度（92 分钟）
    var longScript = WaveScriptCodec.ParseFunscript(
        $"{{\"actions\":[{{\"at\":0,\"pos\":0}},{{\"at\":{longMs},\"pos\":100}}]}}");
    Check($"92 分钟的整片脚本能读进来（旧上限 60 分钟，实测 {longScript[^1].At} ms）",
        longScript.Count == 2 && longScript[^1].At == longMs);
    bool tooLongRejected = false;
    try { WaveScriptCodec.ParseFunscript("{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":21600001,\"pos\":1}]}"); }
    catch (FormatException) { tooLongRejected = true; }
    Check("超过 6 小时的脚本仍然拒绝（上限还在，只是放宽）", tooLongRejected);

    var defaults = new AppSettings();
    defaults.Normalize();
    string testProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
    string testSession = "test-session";
    byte[] telemetryJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new GameTelemetryFrame
    {
        Process = testProcess,
        Token = defaults.GameTelemetryToken,
        SessionId = testSession,
        Sequence = 1,
        ProcessId = Environment.ProcessId,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Engine = "Unity",
        Scene = "LoopA",
        Active = true,
        Axes = [100, -20, 50, 75, 25, 200],
        Intensity = 0.5,
        Confidence = 0.9,
    });
    Check("game telemetry JSON is accepted",
        GameTelemetryProtocol.TryParse(telemetryJson, out GameTelemetryFrame telemetryFrame));
    Check("game telemetry process and axes are normalized",
        telemetryFrame.Process == testProcess
        && telemetryFrame.Axes is { Length: 6 }
        && telemetryFrame.Axes[1] == 0 && telemetryFrame.Axes[5] == 100);
    double[] telemetryAxes = GameTelemetryProtocol.ToAxes(telemetryFrame);
    Check("game telemetry intensity is applied around center",
        telemetryAxes.SequenceEqual([75, 25, 50, 62.5, 37.5, 75]));
    Check("game telemetry without a process is rejected",
        !GameTelemetryProtocol.TryParse(System.Text.Encoding.UTF8.GetBytes("{\"depth\":0.5}"), out _));
    Check("JSON telemetry cannot claim the raw TCode wildcard",
        !GameTelemetryProtocol.TryParse(
            System.Text.Encoding.UTF8.GetBytes("{\"process\":\"*\",\"confidence\":1}"), out _));

    double[] tcodeState = [50, 50, 50, 50, 50, 50];
    Check("standard multi-axis TCode is accepted",
        GameTelemetryProtocol.TryParseTCode(
            System.Text.Encoding.ASCII.GetBytes("L09999 R00000"), tcodeState, out GameTelemetryFrame tcodeFrame)
        && tcodeFrame.Process == "*"
        && tcodeFrame.Axes is { Length: 6 }
        && tcodeFrame.Axes[0] == 100
        && tcodeFrame.Axes[3] == 0);
    Check("changed-only TCode keeps the other axes",
        GameTelemetryProtocol.TryParseTCode(
            System.Text.Encoding.ASCII.GetBytes("R05000I200"), tcodeFrame.Axes!, out GameTelemetryFrame changedTcodeFrame)
        && changedTcodeFrame.Axes![0] == 100
        && Math.Abs(changedTcodeFrame.Axes[3] - 50.005) < 0.001
        && changedTcodeFrame.TransitionMs == 200);
    Check("raw TCode accessories and non-stop device commands are ignored",
        !GameTelemetryProtocol.TryParseTCode(
            System.Text.Encoding.ASCII.GetBytes("V09999 A09999 D1"), changedTcodeFrame.Axes!, out _));
    Check("DSTOP is preserved as an inactive safety frame",
        GameTelemetryProtocol.TryParseTCode(
            System.Text.Encoding.ASCII.GetBytes("DSTOP"), changedTcodeFrame.Axes!, out GameTelemetryFrame stopFrame)
        && stopFrame.IsStop && stopFrame.MessageType == "stop" && stopFrame.Process == "*");
    Check("unsupported TCode speed timing is rejected",
        !GameTelemetryProtocol.TryParseTCode(
            System.Text.Encoding.ASCII.GetBytes("L09999S100"), changedTcodeFrame.Axes!, out _));
    Check("unsupported TCode gain is rejected",
        !GameTelemetryProtocol.TryParseTCode(
            System.Text.Encoding.ASCII.GetBytes("L09999I100G5"), changedTcodeFrame.Axes!, out _));
    Check("mixed per-axis TCode interpolation is rejected",
        !GameTelemetryProtocol.TryParseTCode(
            System.Text.Encoding.ASCII.GetBytes("L09999I100 R00000I200"), changedTcodeFrame.Axes!, out _));

    string detectorRoot = Path.Combine(Path.GetTempPath(), "hexa-engine-detector-" + Guid.NewGuid().ToString("N"));
    try
    {
        string gameRoot = Path.Combine(detectorRoot, "RpgGame");
        Directory.CreateDirectory(Path.Combine(gameRoot, "www", "js"));
        File.WriteAllText(Path.Combine(gameRoot, "www", "js", "rpg_core.js"), "");
        GameEngineInfo detected = GameEngineDetector.DetectExecutable(Path.Combine(gameRoot, "Game.exe"));
        Check("RPG Maker MV is detected from engine files", detected.Kind == GameEngineKind.RpgMakerMv);

        string unityRoot = Path.Combine(detectorRoot, "UnityGame");
        Directory.CreateDirectory(unityRoot);
        File.WriteAllText(Path.Combine(unityRoot, "UnityPlayer.dll"), "");
        File.WriteAllText(Path.Combine(unityRoot, "GameAssembly.dll"), "");
        detected = GameEngineDetector.DetectExecutable(Path.Combine(unityRoot, "Example.exe"));
        Check("Unity IL2CPP is distinguished from Mono", detected.Kind == GameEngineKind.UnityIl2Cpp);
    }
    finally
    {
        if (Directory.Exists(detectorRoot)) Directory.Delete(detectorRoot, recursive: true);
    }

    using (var telemetryService = new GameTelemetryService(
        port: 0,
        authToken: defaults.GameTelemetryToken,
        allowRawTCode: () => true,
        rawTargetProcess: () => testProcess))
    using (var sender = new System.Net.Sockets.UdpClient())
    {
        int tcodeFrames = 0;
        telemetryService.FrameReceived += snapshot =>
        {
            if (snapshot.Frame.Engine == "TCode UDP") Interlocked.Increment(ref tcodeFrames);
        };
        sender.Send(telemetryJson, telemetryJson.Length, "127.0.0.1", telemetryService.Port);
        bool received = SpinWait.SpinUntil(
            () => telemetryService.TryGetLatest(testProcess, TimeSpan.FromSeconds(1), out _),
            1000);
        Check("telemetry gateway receives loopback frames", telemetryService.Listening && received);
        telemetryService.TryGetLatest(testProcess, TimeSpan.FromSeconds(1), out GameTelemetrySnapshot immutableOne);
        immutableOne.Frame.Axes![0] = 0;
        Check("telemetry snapshots cannot mutate the stored frame",
            telemetryService.TryGetLatest(testProcess, TimeSpan.FromSeconds(1), out GameTelemetrySnapshot immutableTwo)
            && immutableTwo.Frame.Axes![0] == 100);

        byte[] wrongToken = telemetryJson.ToArray();
        var wrongFrame = System.Text.Json.JsonSerializer.Deserialize<GameTelemetryFrame>(wrongToken)!;
        wrongFrame.Token = "invalid-token";
        byte[] wrongPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(wrongFrame);
        sender.Send(wrongPayload, wrongPayload.Length, "127.0.0.1", telemetryService.Port);
        Thread.Sleep(100);
        Check("telemetry gateway rejects an invalid token", telemetryService.RejectedPackets > 0);

        byte[] duplicate = telemetryJson.ToArray();
        sender.Send(duplicate, duplicate.Length, "127.0.0.1", telemetryService.Port);
        Thread.Sleep(100);
        Check("telemetry gateway rejects a duplicate sequence", telemetryService.RejectedPackets > 1);

        long rejectedBeforeVersion = telemetryService.RejectedPackets;
        var futureVersion = System.Text.Json.JsonSerializer.Deserialize<GameTelemetryFrame>(telemetryJson)!;
        futureVersion.ProtocolVersion = 2;
        futureVersion.Sequence = 2;
        futureVersion.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] futurePayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(futureVersion);
        sender.Send(futurePayload, futurePayload.Length, "127.0.0.1", telemetryService.Port);
        Thread.Sleep(100);
        Check("telemetry gateway rejects unsupported protocol versions",
            telemetryService.RejectedPackets > rejectedBeforeVersion);

        long rejectedBeforeSenderSwap = telemetryService.RejectedPackets;
        using (var secondSender = new System.Net.Sockets.UdpClient())
        {
            var movedSession = System.Text.Json.JsonSerializer.Deserialize<GameTelemetryFrame>(telemetryJson)!;
            movedSession.Sequence = 2;
            movedSession.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            byte[] movedPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(movedSession);
            secondSender.Send(movedPayload, movedPayload.Length, "127.0.0.1", telemetryService.Port);
        }
        Thread.Sleep(100);
        Check("telemetry session is bound to its UDP sender",
            telemetryService.RejectedPackets > rejectedBeforeSenderSwap);

        long rejectedBeforeStale = telemetryService.RejectedPackets;
        var staleFrame = System.Text.Json.JsonSerializer.Deserialize<GameTelemetryFrame>(telemetryJson)!;
        staleFrame.Sequence = 2;
        staleFrame.Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        byte[] stalePayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(staleFrame);
        sender.Send(stalePayload, stalePayload.Length, "127.0.0.1", telemetryService.Port);
        Thread.Sleep(100);
        Check("telemetry gateway rejects stale timestamps",
            telemetryService.RejectedPackets > rejectedBeforeStale);

        byte[] fullTcode = System.Text.Encoding.ASCII.GetBytes("L09999 R00000");
        sender.Send(fullTcode, fullTcode.Length, "127.0.0.1", telemetryService.Port);
        bool fullTcodeReceived = SpinWait.SpinUntil(() => Volatile.Read(ref tcodeFrames) >= 1, 1000);
        Check("telemetry gateway accepts standard TCode UDP", fullTcodeReceived
            && telemetryService.TryGetLatest(testProcess, TimeSpan.FromSeconds(1), out GameTelemetrySnapshot fullSnapshot)
            && fullSnapshot.Frame.Axes![0] == 100
            && fullSnapshot.Frame.Axes[3] == 0);

        byte[] changedTcode = System.Text.Encoding.ASCII.GetBytes("R05000");
        sender.Send(changedTcode, changedTcode.Length, "127.0.0.1", telemetryService.Port);
        bool changedTcodeReceived = SpinWait.SpinUntil(() => Volatile.Read(ref tcodeFrames) >= 2, 1000);
        Check("telemetry gateway preserves changed-only TCode axes", changedTcodeReceived
            && telemetryService.TryGetLatest(testProcess, TimeSpan.FromSeconds(1), out GameTelemetrySnapshot changedSnapshot)
            && changedSnapshot.Frame.Axes![0] == 100
            && Math.Abs(changedSnapshot.Frame.Axes[3] - 50.005) < 0.001);

        int beforeAccessories = Volatile.Read(ref tcodeFrames);
        byte[] accessories = System.Text.Encoding.ASCII.GetBytes("V09999 A09999");
        sender.Send(accessories, accessories.Length, "127.0.0.1", telemetryService.Port);
        Thread.Sleep(100);
        Check("telemetry gateway rejects absent accessory axes",
            Volatile.Read(ref tcodeFrames) == beforeAccessories);

        byte[] stop = System.Text.Encoding.ASCII.GetBytes("DSTOP");
        sender.Send(stop, stop.Length, "127.0.0.1", telemetryService.Port);
        bool stopReceived = SpinWait.SpinUntil(() => Volatile.Read(ref tcodeFrames) >= beforeAccessories + 1, 1000);
        Check("telemetry gateway forwards DSTOP as a stop frame", stopReceived
            && telemetryService.TryGetLatest(testProcess, TimeSpan.FromSeconds(1), out GameTelemetrySnapshot stopSnapshot)
            && stopSnapshot.Frame.IsStop
            && stopSnapshot.Frame.MessageType == "stop");
    }

    using (var rawDisabled = new GameTelemetryService(
        port: 0,
        authToken: defaults.GameTelemetryToken,
        allowRawTCode: () => false,
        rawTargetProcess: () => testProcess))
    using (var rawSender = new System.Net.Sockets.UdpClient())
    {
        byte[] raw = System.Text.Encoding.ASCII.GetBytes("L09999");
        rawSender.Send(raw, raw.Length, "127.0.0.1", rawDisabled.Port);
        Thread.Sleep(100);
        Check("raw TCode UDP is disabled by default",
            rawDisabled.RejectedPackets == 1
            && !rawDisabled.TryGetLatest(testProcess, TimeSpan.FromSeconds(1), out _));
    }

    string timedFrame = TCodeFrameFormatter.FormatTargets(
        [new("L0", 100, 100), new("R0", 0, 200)],
        defaults.AxisMin,
        defaults.AxisMax,
        changedOnly: false);
    Check("script segment durations stay exact", timedFrame == "L09999I100 R00000I200");
    Check("multi-axis targets share one frame", timedFrame.Split(' ').Length == 2);
    Check("uninstalled accessory axes are rejected",
        Osr6DeviceProfile.SanitizeTCode("L05000I20 V09999I20 A01234I20 R05000I20")
            == "L05000I20 R05000I20");
    Check("device commands are restricted",
        Osr6DeviceProfile.SanitizeTCode("D1 DSTOP DHOME") == "D1 DSTOP");
    Check("SR6 identity response is recognized",
        Osr6DeviceProfile.IsCompatibleIdentityResponse("SR6-Alpha4-ESP32.ino\r\nTCode v0.3"));
    Check("silent bluetooth placeholder is not a device",
        !Osr6DeviceProfile.IsCompatibleIdentityResponse(""));
    // 真机上电时固件会把 ESP-IDF banner 一起吐在这个口上，banner 里就有 SR6Executor 字样；
    // 只有日志、没有 TCode 回话时必须判成「不是设备」，否则自动发现会去开 COM4/COM5。
    Check("firmware boot log alone is not an identity response",
        !Osr6DeviceProfile.IsCompatibleIdentityResponse(
            "I (998) SR6Executor: Starting SR6 executor\r\nI (1002) app_main: OSR6 boot done\r\n"));
    Check("新固件的开机横幅也算身份（它不回 D1）",
        Osr6DeviceProfile.IsCompatibleIdentityResponse(
            "I (241) app_init: Project name:     tcode\r\nI (242) app_init: App version:      v1.0.3-209-g0a9ea27"));
    Check("别的 ESP 项目的横幅不算",
        !Osr6DeviceProfile.IsCompatibleIdentityResponse(
            "I (241) app_init: Project name:     esp32-cam\r\nI (242) app_init: App version: x"));
    Check("identity response is recognized past firmware log lines",
        Osr6DeviceProfile.IsCompatibleIdentityResponse(
            "I (529688) uart: UART echoed 4 bytes\r\nTCode v0.3\r\n"));
    Check("esp32 native usb cdc port counts as known wired device",
        new SerialPortOption("COM3", false, "USB 串行设备",
            @"USB\VID_303A&PID_1001&MI_00\7&1A2B3C&0&0000").IsKnownWiredDevice
        && new SerialPortOption("COM7", false, "USB-SERIAL CH340", @"USB\VID_1A86&PID_7523").IsKnownWiredDevice
        && !new SerialPortOption("COM4", true, "ESP32SPP", @"BTHENUM\{0000}").IsKnownWiredDevice);
    var wiredAndBluetooth = new[]
    {
        new SerialPortOption("COM3", false, "USB 串行设备", @"USB\VID_303A&PID_1001&MI_00"),
        new SerialPortOption("COM4", true, "ESP32SPP", @"BTHENUM\{0000}"),
    };
    Check("auto discovery skips bluetooth ports when a wired one exists",
        SerialService.WithoutBluetoothWhenWired(wiredAndBluetooth, preferWired: true)
            .Select(option => option.PortName).SequenceEqual(new[] { "COM3" }));
    Check("auto discovery keeps bluetooth ports when bluetooth is all we have",
        SerialService.WithoutBluetoothWhenWired(new[] { wiredAndBluetooth[1] }, preferWired: true)
            .Select(option => option.PortName).SequenceEqual(new[] { "COM4" })
        && SerialService.WithoutBluetoothWhenWired(wiredAndBluetooth, preferWired: false).Count == 2);
    Check("standard companion suffix maps to OSR6 axis",
        FunscriptTrackLoader.AxisFromFileName("scene.twist.funscript") == "R0"
        && FunscriptTrackLoader.AxisFromFileName("scene.pitch.funscript") == "R2");
    var embeddedTracks = WaveScriptCodec.ParseFunscriptTracks("""
        {"axes":[
          {"id":"L0","actions":[{"at":0,"pos":0},{"at":100,"pos":100}]},
          {"id":"twist","actions":[{"at":0,"pos":50},{"at":200,"pos":75}]},
          {"id":"V0","actions":[{"at":0,"pos":0},{"at":100,"pos":100}]}
        ]}
        """);
    Check("embedded multi-axis funscript maps installed tracks", embeddedTracks.Count == 2
        && embeddedTracks.ContainsKey("L0") && embeddedTracks.ContainsKey("R0"));
    Check("embedded optional accessory track is ignored", !embeddedTracks.ContainsKey("V0"));

    // ── 脚本播放增强：社区命名 / 多轴联动 / 空档填缝 ─────────────────────
    Check("community axis naming round-trips",
        FunscriptTrackLoader.CommunitySuffix("R0") == "twist"
        && FunscriptTrackLoader.CommunitySuffix("l2") == "sway"
        && FunscriptTrackLoader.AxisFromCommunitySuffix(".surge") == "L1"
        && FunscriptTrackLoader.AxisFromCommunitySuffix("PITCH") == "R2"
        && FunscriptTrackLoader.AxisFromCommunitySuffix("L0") == null);
    Check("companion file naming keeps both conventions",
        FunscriptTrackLoader.CompanionFilePath(Path.Combine("C:", "x", "scene"), "L1", false)
            == Path.Combine("C:", "x", "scene.L1.funscript")
        && FunscriptTrackLoader.CompanionFilePath(Path.Combine("C:", "x", "scene"), "L1", true)
            == Path.Combine("C:", "x", "scene.surge.funscript"));
    bool rejectedUnknownAxis = false;
    try { FunscriptTrackLoader.CompanionFilePath(Path.Combine("C:", "x", "scene"), "V0", true); }
    catch (ArgumentException) { rejectedUnknownAxis = true; }
    Check("companion file naming rejects accessory axes", rejectedUnknownAxis);

    // 多轴联动：脚本没写的轴跟着主轴动，写了的轴一个字节都不能改。
    bool[] onlyL0 = [true, false, false, false, false, false];
    bool[] withTwist = [true, false, false, true, false, false];

    var linker = new ScriptAxisLinker();
    double[] still = [10, 50, 50, 50, 50, 50];
    linker.Apply(still, onlyL0, 30, 0, 0, 100);
    Check("linker tilts with the stroke position",
        Math.Abs(still[2] - 29.6) < 0.01 && Math.Abs(still[4] - 28.4) < 0.01);
    Check("linker does not twist without movement", Math.Abs(still[3] - 50) < 0.01);
    Check("linker never touches a scripted axis", Math.Abs(RunLinkerTwist(withTwist) - 50) < 0.01);

    double fastTwist = RunLinkerTwist(onlyL0);
    Check("linker twists on fast strokes", Math.Abs(fastTwist - 50) > 5);
    Check("linker returns to center once the script stops", Math.Abs(RunLinkerThenStop() - 50) < 1.0);
    Check("linker follows the amount setting",
        Math.Abs(RunLinkerTwist(onlyL0, 8) - 50) < Math.Abs(fastTwist - 50));

    double[] noMainAxis = [50, 50, 50, 50, 50, 50];
    linker.Apply(noMainAxis, [false, false, true, false, false, false], 40, 0.02, 0, 100);
    Check("linker stays out when the script has no main axis", noMainAxis[2] == 50);

    // 空档填缝：只填「作者没写、且两端几乎不动」的段落。
    var gappedScript = new List<WaveScriptCodec.ActionPoint>
    {
        new(0, 20), new(500, 20), new(1000, 80), new(1200, 80),
        new(4200, 80), new(4600, 20),
    };
    var gaps = ScriptGapFiller.FindGaps(gappedScript, 1500);
    Check("gap filler finds the flat hold between actions",
        gaps.Count == 1 && gaps[0].StartMs == 1200 && gaps[0].EndMs == 4200
        && Math.Abs(gaps[0].HoldPosition - 80) < 0.001);
    Check("gap filler ignores a slow but continuous move",
        ScriptGapFiller.FindGaps([new(0, 0), new(3000, 100)], 1500).Count == 0);
    Check("gap filler treats a tiny wobble as a hold",
        ScriptGapFiller.FindGaps([new(0, 50), new(3000, 53)], 1500).Count == 1);
    Check("gap filler clamps the minimum gap length",
        ScriptGapFiller.FindGaps(gappedScript, 10).Count == 1);

    double[] heldFrame = [80, 50, 50, 50, 50, 50];
    Check("gap filler keeps still when it is quiet",
        !ScriptGapFiller.TryFill(heldFrame, 2700, gaps, 30, true, 0, 0.25) && heldFrame[0] == 80);
    Check("gap filler eases in at the gap boundary",
        !ScriptGapFiller.TryFill(heldFrame, 1200, gaps, 30, true, 0.8, 0.25));
    Check("gap filler ignores a position outside every gap",
        !ScriptGapFiller.TryFill(heldFrame, 4500, gaps, 30, true, 0.8, 0.25));

    // 渐强语汇在相位 0.25 处取峰值：越响摆得越大（响度直接当强度用）
    double[] loudFrame = [80, 50, 50, 50, 50, 50];
    bool filled = ScriptGapFiller.TryFill(loudFrame, 2700, gaps, 20, true, 1.0, 0.25);
    Check("gap filler adds motion in the middle of a gap",
        filled && loudFrame[0] > 80 && loudFrame[3] > 50);
    Check("gap filler keeps every axis inside its travel",
        loudFrame.All(value => value is >= 0 and <= 100));

    double[] quietFrame = [80, 50, 50, 50, 50, 50];
    ScriptGapFiller.TryFill(quietFrame, 2700, gaps, 20, true, 0.25, 0.25);
    Check("gap filler swings less when the sound is quieter",
        quietFrame[0] - 80 < loudFrame[0] - 80);

    double[] linkOneAxisFrame = [80, 50, 50, 50, 50, 50];
    ScriptGapFiller.TryFill(linkOneAxisFrame, 2700, gaps, 20, false, 1.0, 0.25);
    Check("gap filler leaves the other axes alone when multi-axis is off",
        linkOneAxisFrame[0] > 80 && linkOneAxisFrame.Skip(1).All(value => value == 50));

    double[] extremeFrame = RunExtremeGapFill();
    Check("gap filler scales down at an extreme hold position",
        extremeFrame.All(value => value is >= 0 and <= 100)
        && Math.Abs(extremeFrame[0] - 100) < 0.001);

    int[] previousRaw = new int[6];
    bool[] previousValid = new bool[6];
    int[] previousInterval = new int[6];
    string firstFrame = TCodeFrameFormatter.FormatAxes(
        [50, 50, 50, 50, 50, 50], defaults.AxisMin, defaults.AxisMax, 10,
        previousRaw, previousValid, changedOnly: true, previousInterval);
    string unchangedFrame = TCodeFrameFormatter.FormatAxes(
        [50, 50, 50, 50, 50, 50], defaults.AxisMin, defaults.AxisMax, 10,
        previousRaw, previousValid, changedOnly: true, previousInterval);
    string oneAxisFrame = TCodeFrameFormatter.FormatAxes(
        [51, 50, 50, 50, 50, 50], defaults.AxisMin, defaults.AxisMax, 10,
        previousRaw, previousValid, changedOnly: true, previousInterval);
    Check("first frame is complete and carries the interpolation",
        firstFrame.Split(' ').Length == 6
        && firstFrame.Split(' ').All(token => token.EndsWith("I10", StringComparison.Ordinal)));
    // 真机固件收到只有一根轴的残帧会 panic 重启（灌流量实验 20 秒复现），
    // 所以「没变化」也只省掉 I 参数，帧本身永远是完整的六轴。
    Check("unchanged pose still ships a complete frame but drops the I parameter",
        unchangedFrame.Split(' ').Length == 6 && !unchangedFrame.Contains('I'));
    Check("one changed axis still ships a complete frame",
        oneAxisFrame.Split(' ').Length == 6
        && oneAxisFrame.StartsWith("L0", StringComparison.Ordinal)
        && oneAxisFrame.Split(' ')[0] != unchangedFrame.Split(' ')[0]);
    string completedFrame = TCodeFrameFormatter.FormatRaw(
        [(3, 7777, 0)], previousRaw, previousValid, previousInterval);
    Check("partial raw line is completed into a six-axis frame",
        completedFrame.Split(' ').Length == 6
        && completedFrame.Split(' ')[3].StartsWith("R0", StringComparison.Ordinal)
        && completedFrame.Split(' ')[3].EndsWith("7777", StringComparison.Ordinal));

    var pacerRaw = new int[6];
    var pacerValid = new bool[6];
    pacerRaw[0] = 8123;
    pacerValid[0] = true;
    string pacedFrame = TCodeFrameFormatter.FormatState(pacerRaw, pacerValid, SerialService.FrameIntervalMs);
    Check("pacer frame always carries all six axes",
        pacedFrame.Split(' ').Length == 6);
    Check("pacer frame uses one constant interval on every axis",
        pacedFrame.Split(' ').All(token => token.EndsWith("I" + SerialService.FrameIntervalMs, StringComparison.Ordinal)));
    Check("pacer keeps a known axis position",
        pacedFrame.Split(' ')[0] == "L08123I" + SerialService.FrameIntervalMs);
    Check("unknown axes fall back to the middle position",
        pacedFrame.Split(' ').Skip(1).All(token => token[2..6] == "5000"));

    // 相位对齐：游戏一抽发一条指令，动作必须从这条指令到达的时刻开始。
    Check("phase error points to the shortest way back to a stroke start",
        Math.Abs(LovenseProtocol.PhaseError(0.1) - 0.1) < 1e-9
        && Math.Abs(LovenseProtocol.PhaseError(0.9) + 0.1) < 1e-9);
    Check("correcting by the reported error lands on a stroke start",
        Math.Abs(LovenseProtocol.Frac(0.1 - LovenseProtocol.PhaseError(0.1))) < 1e-9
        && Math.Abs(LovenseProtocol.Frac(0.9 - LovenseProtocol.PhaseError(0.9))) < 1e-9);
    Check("phase wrap keeps the oscillator inside one stroke",
        Math.Abs(LovenseProtocol.Frac(1.25) - 0.25) < 1e-9
        && Math.Abs(LovenseProtocol.Frac(-0.25) - 0.75) < 1e-9);

    double[] coordinated = Osr6MotionComposer.Compose(
        "game_flow", 0.37, [35, 13, 11, 14, 10, 8], 1, 30, ComfortProfile.Immersive);
    Check("automatic pose contains six installed axes", coordinated.Length == 6
        && coordinated.All(value => value is >= 0 and <= 100));
    Check("automatic axes are coordinated but distinct",
        coordinated.DistinctBy(value => Math.Round(value, 3)).Count() >= 4);

    var rankedPorts = SerialService.RankPortOptions(
        [
            new("COM5", true, "Bluetooth Incoming Port", "BTHENUM", IsBluetoothInbound: true),
            new("COM4", true, "ESP32SPP", "BTHENUM"),
            new("COM7", false, "USB-SERIAL CH340", "USB\\VID_1A86&PID_7523"),
        ],
        preferWired: true,
        primaryPortName: "",
        fallbackPortName: "");
    Check("auto-connect prefers confirmed COM7 wired device", rankedPorts[0].PortName == "COM7");
    Check("auto-connect keeps confirmed COM4 bluetooth fallback", rankedPorts[1].PortName == "COM4");
    Check("auto-connect excludes inbound COM5", rankedPorts.All(option => option.PortName != "COM5"));

    var limiter = new MotionSafetyLimiter();
    limiter.Reset([50, 50, 50, 50, 50, 50]);
    double previousPosition = 50;
    double previousVelocity = 0;
    double maxObservedAcceleration = 0;
    bool speedBounded = true, accelerationBounded = true;
    for (int i = 0; i < 80; i++)
    {
        double next = limiter.Step([100, 100, 100, 100, 100, 100], 0.02, ComfortProfile.Dynamic)[0];
        double velocity = (next - previousPosition) / 0.02;
        speedBounded &= Math.Abs(velocity) <= ComfortProfile.Dynamic.MaxAxisSpeedPerSecond + 0.001;
        double observedAcceleration = Math.Abs((velocity - previousVelocity) / 0.02);
        maxObservedAcceleration = Math.Max(maxObservedAcceleration, observedAcceleration);
        accelerationBounded &= observedAcceleration <= ComfortProfile.Dynamic.MaxAxisAccelerationPerSecond2 + 0.01;
        previousPosition = next;
        previousVelocity = velocity;
    }
    Check("final limiter bounds velocity", speedBounded);
    Check($"final limiter bounds acceleration (max {maxObservedAcceleration:0})", accelerationBounded);
    Check("final limiter reaches a fast target", previousPosition > 95);

    var interpolationPoints = new List<(double X, double Y)> { (0, 0), (1, 0.4), (2, 1) };
    double pchipMid = MotionInterpolation.SamplePchip(interpolationPoints, 1.5);
    Check("PCHIP interpolation stays monotonic", pchipMid is >= 0.4 and <= 1.0);
    Check("linear interpolation remains available",
        Math.Abs(MotionInterpolation.SampleLinear(interpolationPoints, 1.5) - 0.7) < 0.0001);

    var sequencer = new AutoBehaviorSequencer(seed: 42);
    sequencer.Reset("free_play", 1.4, ComfortProfile.Dynamic);
    var seenPatterns = new HashSet<string>();
    AutoBehaviorFrame behaviorFrame = null!;
    for (int i = 0; i < 2200; i++)
    {
        behaviorFrame = sequencer.Step(
            0.02, 1.4, [35, 13, 11, 14, 10, 8], 1, 30,
            ComfortProfile.Dynamic, 0.2, 0.3, 0);
        seenPatterns.Add(behaviorFrame.Pattern);
    }
    Check("free play rotates through behaviors", seenPatterns.Count >= 2);
    Check("free play reports useful tempo", behaviorFrame.Bpm is > 25 and < 300);

    using var serial = new SerialService();
    serial.SetSimulationMode(true);
    var cfg = new AppSettings();
    using var engine = new MotionEngine(serial, cfg);
    Check("simulator is connected", serial.IsOpen && serial.IsSimulation);
    // CanRun 现在还要求输出闸门解锁（真实设备连上时由 OnConnectionChanged 解锁，
    // 这里是先建引擎后连的模拟，得手动补上同样的那一步）。
    serial.OutputEnabled = true;
    Check("device is armed and can run once output is unlocked", engine.IsArmed && engine.CanRun);
    Check("arm simulator", engine.TryArm(out _));
    Check("arm first enters homing state", engine.IsHoming && !engine.CanRun);
    Check("homing emits DSTOP then a full center frame",
        serial.SimulationCommands.Count >= 2
        && serial.SimulationCommands[0] == "DSTOP"
        && serial.SimulationCommands[1].Split(' ').Length == 6);
    Check("homing blocks direct motion", !engine.TrySendDirectAxes([10, 20, 30, 40, 50, 60]));
    Thread.Sleep(1350);
    Check("arm completes only after homing", !engine.IsHoming && engine.CanRun);
    int beforeDirect = serial.SimulationCommands.Count;
    Check("direct output records command", engine.TrySendDirectAxes([10, 20, 30, 40, 50, 60])
        && serial.SimulationCommands.Count > beforeDirect);
    cfg.AxisMin["L0"] = 2000; cfg.AxisMax["L0"] = 8000;
    engine.TrySendDirectAxes([50, 50, 50, 50, 50, 50]);
    string mappedL0 = serial.SimulationCommands[^1].Split(' ').First(token => token.StartsWith("L0", StringComparison.Ordinal));
    int mappedL0Raw = int.Parse(mappedL0.Substring(2, 4));
    Check("axis range contains final-limited direct output", mappedL0Raw is >= 2000 and <= 8000);
    Check("calibration uses explicit raw axis command", engine.TryMoveCalibrationAxis("L0", 1234)
        && serial.SimulationCommands.Any(command => command.StartsWith("L01234I", StringComparison.Ordinal)));
    int beforeAccessory = serial.SimulationCommands.Count;
    serial.Send("V09999I10 A09999I10");
    Check("transport never emits absent accessories", serial.SimulationCommands.Count == beforeAccessory);
    serial.Send("L099999I99999");
    string[] normalizedTokens = serial.SimulationCommands[^1].Split(' ');
    Check("raw axis token is normalized to safe bounds and sent as-is (no six-axis padding)",
        normalizedTokens[0] == "L09999I9999" && normalizedTokens.Length == 1);
    Check("calibration returns to center before playback", engine.TryMoveCalibrationAxis("L0", 5000));

    // ── 暂停 / 继续：停住不动、不回中、不锁输出，继续时接着走 ──────────
    Check("pause refuses when nothing is running", !engine.Pause() && !engine.IsPaused);
    Check("auto starts for the pause test", engine.StartAuto());
    Thread.Sleep(120);
    Check("pause accepted while playing", engine.Pause() && engine.IsPaused);
    Thread.Sleep(250);
    int pausedFrameCount = serial.SimulationCommands.Count;
    Thread.Sleep(250);
    Check("paused engine stops emitting motion frames",
        serial.SimulationCommands.Count == pausedFrameCount);
    Check("pause neither locks output nor trips the latch",
        engine.IsArmed && engine.CanRun && !engine.EmergencyStopped);
    Thread.Sleep(200);   // 撑过看门狗的 750ms 判据余量：暂停不该被误判成卡死
    Check("pause survives past the watchdog window", engine.IsPaused && !engine.EmergencyStopped);
    Check("resume accepted", engine.Resume() && !engine.IsPaused);
    Thread.Sleep(350);
    Check("resumed engine emits motion frames again",
        serial.SimulationCommands.Count > pausedFrameCount);
    engine.StopAll();
    Check("stop clears the paused flag", !engine.IsPaused);

    // 外部控制接口鉴权：token 定长比较，错一位／前缀／空值都不许过
    Check("external api token accepts the exact value", WebApiService.TokenEquals("0123456789abcdef", "0123456789abcdef"));
    Check("external api token rejects wrong value", !WebApiService.TokenEquals("0123456789abcdee", "0123456789abcdef"));
    Check("external api token rejects a prefix", !WebApiService.TokenEquals("0123456789abcde", "0123456789abcdef"));
    Check("external api token rejects empty on either side",
        !WebApiService.TokenEquals("", "0123456789abcdef") && !WebApiService.TokenEquals("0123456789abcdef", ""));

    engine.EditWaves["L0"].Clear();
    engine.EditWaves["L0"].AddRange([(0, WaveScriptCodec.CanvasHeight), (WaveScriptCodec.CanvasWidth, 0)]);
    engine.WfAxisEnabled[0] = true;
    engine.CwCycleLen = 0.35;
    engine.Speed = 0.5;
    engine.ScriptSpeed = 1;
    cfg.ComfortProfile = "dynamic";
    engine.MarkWaveDirty("L0");
    int beforeCustom = serial.SimulationCommands.Count;
    Check("faithful custom script starts", engine.StartCustom());
    Thread.Sleep(2500);
    engine.StopCustom();
    string[] customTokens = serial.SimulationCommands.Skip(beforeCustom)
        .SelectMany(command => command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(token => token.StartsWith("L0", StringComparison.Ordinal) && token.Length >= 6)
        .ToArray();
    int customPeak = customTokens.Length == 0 ? 0 : customTokens.Max(token => int.Parse(token.Substring(2, 4)));
    Check($"fast custom motion remains responsive (peak {customPeak})", customPeak > 6800);
    int[] customPositions = customTokens.Select(token => int.Parse(token.Substring(2, 4))).ToArray();
    Check("fast custom motion stays inside 0-9999", customPositions.All(p => p is >= 0 and <= 9999));
    Check("custom scheduler uses measured short interpolation", customTokens.Any(token =>
        token.Contains('I') && int.Parse(token[(token.IndexOf('I') + 1)..]) <= 30));
    Check("automatic speed does not slow imported scripts", engine.Speed == 0.5 && engine.ScriptSpeed == 1);

    int beforeAuto = serial.SimulationCommands.Count;
    Check("automatic mode starts", engine.StartAuto());
    Thread.Sleep(100);
    engine.StopAuto();
    Check("automatic scheduler emits bounded output", serial.SimulationCommands.Count > beforeAuto);

    engine.RuleEngineActive = true;
    int beforeTelemetry = serial.SimulationCommands.Count;
    Check("telemetry frame atomically starts a session",
        engine.ApplyTelemetryFrame(
            "session-a", 1, [100, 0, 60, 70, 40, 55], "simulation", 20, 1));
    Check("telemetry rejects duplicate and backwards sequences",
        !engine.ApplyTelemetryFrame(
            "session-a", 1, [0, 100, 40, 30, 60, 45], "simulation", 20, 1));
    Thread.Sleep(220);
    Check("telemetry scheduler emits six-axis output", serial.SimulationCommands.Count > beforeTelemetry
        && serial.SimulationCommands.Skip(beforeTelemetry).Any(command => command.Contains("L0") && command.Contains("R2")));
    // 注意：遥测调度器在后台持续输出，这里不能比较「指令总数不变」——
    // 那会和调度器的正常输出竞争（偶发失败）。只断言「没有发出 DSTOP」。
    int beforeWrongStop = serial.SimulationCommands.Count;
    Check("a different session cannot stop active telemetry",
        !engine.StopTelemetrySession("session-b")
        && engine.TelemetryRunning
        && !serial.SimulationCommands.Skip(beforeWrongStop).Any(command => command == "DSTOP"));
    Check("the active session can stop immediately",
        engine.StopTelemetrySession("session-a")
        && !engine.TelemetryRunning
        && serial.SimulationCommands.Skip(beforeWrongStop).Contains("DSTOP"));

    int beforeStaleTelemetry = serial.SimulationCommands.Count;
    Check("a new telemetry session does not inherit the old session id",
        engine.ApplyTelemetryFrame(
            "session-stale", 1, [15, 85, 50, 45, 55, 50], "simulation", 20, 0.8)
        && engine.ActiveTelemetrySessionId == "session-stale");
    Thread.Sleep(450);
    Check("stale telemetry immediately stops without a recenter phase",
        !engine.TelemetryRunning
        && serial.SimulationCommands.Count > beforeStaleTelemetry
        && serial.SimulationCommands[^1] == "DSTOP");
    engine.RuleEngineActive = false;

    engine.EmergencyStop();
    int count = serial.SimulationCommands.Count;
    Check("emergency stop sends DSTOP", serial.SimulationCommands.Contains("DSTOP"));

    Check("emergency stop is the final write", serial.SimulationCommands[^1] == "DSTOP");
    Check("emergency stop locks output", !engine.TrySendDirectAxes([50, 50, 50, 50, 50, 50]) && serial.SimulationCommands.Count == count);

    cfg.AxisMin["L0"] = 12000;
    cfg.AxisMax["L0"] = -2;
    cfg.Normalize();
    Check("settings normalization clamps limits", cfg.AxisMin["L0"] == 0 && cfg.AxisMax["L0"] == 9999);
    cfg.ScriptPlaybackSpeed = 9;
    cfg.Normalize();
    Check("script speed setting is bounded independently", cfg.ScriptPlaybackSpeed == 2.0);
    cfg.ScriptAxisLinkAmount = 999;
    cfg.ScriptGapFillMinMs = 5;
    cfg.ScriptGapFillRange = 999;
    cfg.ScriptSmoothingStrength = 999;
    cfg.BridgeSmoothingMs = 9999;
    cfg.BridgeAxisLinkAmount = 999;
    cfg.AmbientOverlayAmount = 999;
    cfg.ScriptAxisLinkEnabled = true;      // 默认必须是关的，只有用户自己打开才会是 true
    cfg.Normalize();
    Check("playback enhancement settings are bounded",
        cfg.ScriptAxisLinkAmount == ScriptAxisLinker.MaxAmount
        && cfg.ScriptGapFillMinMs == (int)ScriptGapFiller.MinGapMs
        && Math.Abs(cfg.ScriptGapFillRange - ScriptGapFiller.MaxFillRange) < 1e-9
        && Math.Abs(cfg.ScriptSmoothingStrength - ScriptSmoothing.MaxStrength) < 1e-9
        && cfg.BridgeSmoothingMs == (int)BridgeMotionShaper.MaxSmoothingMs
        && Math.Abs(cfg.BridgeAxisLinkAmount - ScriptAxisLinker.MaxAmount) < 1e-9
        && Math.Abs(cfg.AmbientOverlayAmount - 30) < 1e-9);
    // 用户 2026-10-04 要求「脚本平滑默认开」；联动 / 填缝 / 只听不驱动仍然默认关。
    Check("playback enhancement defaults", new AppSettings() is
        { ScriptAxisLinkEnabled: false, ScriptGapFillEnabled: false, AudioListenOnly: false, ScriptSmoothingEnabled: true, BridgeAxisLink: false }
        && Math.Abs(new AppSettings().ScriptAxisLinkAmount - ScriptAxisLinker.DefaultAmount) < 0.001);
    cfg.ScriptAxisLinkEnabled = false;
    cfg.Rules.Add(new RuleConfig { Id = "bad-role", Role = "unknown", Mode = "free_play" });
    cfg.Normalize();
    Check("rule layer is normalized", cfg.Rules[^1].Role == "reaction");
    cfg.CompanionProcess = "ExampleGame";
    cfg.CompanionBaseMode = "organic_flow";
    cfg.CompanionReaction = "sound";
    cfg.CompanionSensitivity = "standard";
    GameCompanionRules.Apply(cfg);
    RuleConfig companionBase = cfg.Rules.Single(rule => rule.Id == "companion-base");
    RuleConfig companionReaction = cfg.Rules.Single(rule => rule.Id == "companion-reaction");
    Check("simple companion creates one foreground gallery rule",
        companionBase.Enabled && companionBase.Type == "process" && companionBase.Role == "gallery"
        && companionBase.Match == "ExampleGame" && companionBase.Mode == "organic_flow");
    Check("simple companion creates one audio reaction rule",
        companionReaction.Enabled && companionReaction.Type == "game" && companionReaction.Role == "reaction"
        && companionReaction.Threshold == 0.48 && companionReaction.ReleaseThreshold == 0.32);

    // ── 游戏桥（Intiface WebSocket → TCode）协议 ─────────────────────────
    Check("game bridge websocket accept follows RFC 6455",
        IntifaceProtocol.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ==") == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");

    Check("game bridge single mode presents one full device",
        IntifaceProtocol.DeviceListJson(7, "single").Contains("\"DeviceName\":\"Hexa OSR6 (L0+R0)\"")
        && !IntifaceProtocol.DeviceListJson(7, "single").Contains("Hexa Rotary"));
    Check("game bridge dual mode presents linear and rotary devices",
        IntifaceProtocol.DeviceListJson(7, "dual").Contains("Hexa Linear (L0)")
        && IntifaceProtocol.DeviceListJson(7, "dual").Contains("Hexa Rotary (R0)"));
    // 本机六轴没有振动附件，桥不向游戏宣告 VibrateCmd（否则游戏会以为有振动器并一直发指令）
    Check("game bridge does not advertise VibrateCmd",
        !IntifaceProtocol.DeviceListJson(7, "single").Contains("VibrateCmd")
        && !IntifaceProtocol.DeviceListJson(7, "dual").Contains("VibrateCmd")
        && !IntifaceProtocol.DeviceAddedJson(7, 0, "single").Contains("VibrateCmd"));

    var bridgeActions = new List<IntifaceProtocol.BridgeAction>();
    var bridgeResponses = IntifaceProtocol.Process(
        "[{\"LinearCmd\":{\"Id\":1,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":0.5,\"Duration\":300}]}}]",
        "single", bridgeActions.Add);
    Check("game bridge linear maps to L0 with clamped duration",
        bridgeResponses.Count == 1 && bridgeResponses[0].Contains("\"Ok\"")
        && bridgeActions.SequenceEqual(
            new IntifaceProtocol.BridgeAction[] { new IntifaceProtocol.LinearAction("L0", 5000, 300) }));

    bridgeActions.Clear();
    IntifaceProtocol.Process(
        "[{\"LinearCmd\":{\"Id\":2,\"DeviceIndex\":1,\"Vectors\":[{\"Index\":0,\"Position\":0.25,\"Duration\":100}]}}]",
        "dual", bridgeActions.Add);
    Check("game bridge dual mode maps linear device 1 to L1",
        bridgeActions.SequenceEqual(
            new IntifaceProtocol.BridgeAction[] { new IntifaceProtocol.LinearAction("L1", 2500, 100) }));

    bridgeActions.Clear();
    IntifaceProtocol.Process(
        "[{\"RotateCmd\":{\"Id\":3,\"DeviceIndex\":0,\"Rotations\":[{\"Index\":0,\"Speed\":0.8,\"Clockwise\":true}]}}]",
        "dual", bridgeActions.Add);
    Check("game bridge dual mode maps rotate device 0 to R1",
        bridgeActions.SequenceEqual(
            new IntifaceProtocol.BridgeAction[] { new IntifaceProtocol.RotateAction("R1", 0.8, true) }));

    // 振动通道不映射到任何真实轴：桥只把 V0 转发给传输层（设备自己会忽略它）。
    // 曾经把振动映射到 L0/R2（GTPB 兼容），但「游戏震动 = 主轴抽动」对用户是惊吓而不是沉浸，已废弃。
    bridgeActions.Clear();
    IntifaceProtocol.Process(
        "[{\"VibrateCmd\":{\"Id\":4,\"DeviceIndex\":0,\"Speeds\":[{\"Index\":1,\"Speed\":0.6}]}}]",
        "single", bridgeActions.Add);
    Check("game bridge vibrate maps to V0 in single mode",
        bridgeActions.SequenceEqual(
            new IntifaceProtocol.BridgeAction[] { new IntifaceProtocol.VibrateAction("V0", 0.6) }));

    bridgeActions.Clear();
    bridgeResponses = IntifaceProtocol.Process(
        "[{\"VibrateCmd\":{\"Id\":12,\"DeviceIndex\":0,\"Speeds\":[{\"Index\":0,\"Speed\":0.6}]}}]",
        "single", bridgeActions.Add, "off");
    Check("game bridge vibrate off mode ignores vibrate commands",
        bridgeActions.Count == 0 && bridgeResponses.Count == 1 && bridgeResponses[0].Contains("\"Ok\""));

    bridgeActions.Clear();
    IntifaceProtocol.Process(
        "[{\"StopDeviceCmd\":{\"Id\":5}}]", "single", bridgeActions.Add);
    Check("game bridge stop device emits DSTOP action",
        bridgeActions.SequenceEqual(new IntifaceProtocol.BridgeAction[] { new IntifaceProtocol.StopAction() }));

    bridgeActions.Clear();
    bridgeResponses = IntifaceProtocol.Process(
        "[{\"StartScanning\":{\"Id\":6}}]", "dual", bridgeActions.Add);
    Check("game bridge scanning adds both dual devices",
        bridgeResponses.Count == 4 && bridgeResponses[1].Contains("Hexa Linear")
        && bridgeResponses[2].Contains("Hexa Rotary"));
    // v3 规定扫描结束要回 ScanningFinished（少这一条会卡住走扫描流程的客户端）。
    Check("game bridge scanning finishes with ScanningFinished",
        bridgeResponses.Count == 4 && bridgeResponses[3].Contains("ScanningFinished"));

    // 旋转＝位置舵机做不到持续旋转 ⇒ 撞限位掉头（往复摆动）；位置没变就不发帧。
    // 游戏喊「全部停止」必须真的停；未知消息必须回 Error（不是 Ok）。
    bridgeActions.Clear();
    IntifaceProtocol.Process("[{\"StopAllDevices\":{\"Id\":7}}]", "single", bridgeActions.Add);
    Check("game bridge StopAllDevices really stops",
        bridgeActions.Count == 1 && bridgeActions[0] is IntifaceProtocol.StopAction);
    // 六轴直驱：一台设备宣告 6 个位置轴；LinearCmd 的 Index 直接点名轴；越界回 Error。
    string sixList = IntifaceProtocol.DeviceListJson(3, "six");
    int featureCount = System.Text.RegularExpressions.Regex.Matches(sixList, "FeatureDescriptor").Count;
    // 六轴设备现在有三组能力：LinearCmd 6 轴 + RotateCmd 3 轴 + ScalarCmd 6 轴 = 15 条特征。
    Check("game bridge six-axis mode declares every installed axis",
        featureCount == 15 && sixList.Contains("\"L2\"") && sixList.Contains("\"R2\"")
        && sixList.Contains("Hexa OSR6 6-Axis") && sixList.Contains("\"ScalarCmd\":["));
    bridgeActions.Clear();
    IntifaceProtocol.Process("[{\"LinearCmd\":{\"Id\":20,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":3,\"Position\":0.5,\"Duration\":200}]}}]",
        "six", bridgeActions.Add);
    Check("game bridge six-axis mode routes Index 3 to R0",
        bridgeActions.Count == 1 && bridgeActions[0] is IntifaceProtocol.LinearAction sixLinear && sixLinear.Axis == "R0");
    var outOfRange = IntifaceProtocol.Process(
        "[{\"LinearCmd\":{\"Id\":21,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":9,\"Position\":0.5,\"Duration\":200}]}}]",
        "six", _ => { });
    Check("game bridge six-axis mode rejects an out-of-range feature index",
        outOfRange.Any(response => response.Contains("\"Error\"")));
    var legacyList = IntifaceProtocol.DeviceListJson(4, "single");
    // 单设备模式只有 L0+R0 两条 LinearCmd 特征；ScalarCmd 的索引是六轴轴序，不能塞进来。
    Check("game bridge legacy single mode still declares only L0+R0",
        System.Text.RegularExpressions.Regex.Matches(legacyList, "FeatureDescriptor").Count == 2
        && !legacyList.Contains("ScalarCmd"));

    // ── Buttplug v4：MessageVersion 协商 + ScalarCmd 能力宣告 ─────────────
    // 报 3 的客户端必须看到和加 ScalarCmd 之前**逐字节一致**的 DeviceMessages（老客户端不许被动到）。
    var v3Session = new IntifaceProtocol.SessionState();
    var v3ServerInfo = IntifaceProtocol.Process(
        "[{\"RequestServerInfo\":{\"Id\":11,\"ClientName\":\"t\",\"MessageVersion\":3}}]",
        "six", _ => { }, "off", null, v3Session);
    Check("game bridge v3 client is answered with v2+ ServerInfo (no legacy version fields)",
        v3ServerInfo.Count == 1 && v3ServerInfo[0].Contains("\"MessageVersion\":3")
        && !v3ServerInfo[0].Contains("ServerMajorVersion") && v3Session.MessageVersion == 3);
    string v3DeviceList = IntifaceProtocol.Process("[{\"RequestDeviceList\":{\"Id\":12}}]", "six", _ => { }, "off", null, v3Session)[0];
    // ScalarCmd 本来就是 v3 能力（以前错当成 v4 才宣告，导致 v3 客户端少一条通道）。
    Check("game bridge v3 device list declares ScalarCmd without StepRange",
        v3DeviceList.Contains("\"ScalarCmd\":[") && !v3DeviceList.Contains("StepRange")
        && v3DeviceList == IntifaceProtocol.DeviceListJson(12, "six")
        && System.Text.RegularExpressions.Regex.Matches(v3DeviceList, "FeatureDescriptor").Count == 15);

    var v4Session = new IntifaceProtocol.SessionState();
    var v4ServerInfo = IntifaceProtocol.Process(
        "[{\"RequestServerInfo\":{\"Id\":21,\"ClientName\":\"t\",\"MessageVersion\":4}}]",
        "six", _ => { }, "off", null, v4Session);
    // 客户端报 4：v4 客户端把「服务端 major 低于自己」视为无效连接，会自己断线；按 v3 Patch1 回 Error 让客户端明确知道上限。
    Check("game bridge v4 client is rejected per spec (server supports up to v3)",
        v4ServerInfo.Count == 1 && v4ServerInfo[0].Contains("\"Error\"")
        && v4ServerInfo[0].Contains("\"ErrorCode\":1")
        && v4ServerInfo[0].Contains("spec version mismatch")
        && v4Session.MessageVersion == 3);
    string v4DeviceList = IntifaceProtocol.Process("[{\"RequestDeviceList\":{\"Id\":22}}]", "six", _ => { }, "off", null, v4Session)[0];
    int scalarStart = v4DeviceList.IndexOf("\"ScalarCmd\":[", StringComparison.Ordinal);
    int scalarEnd = scalarStart < 0 ? -1 : v4DeviceList.IndexOf("}],", scalarStart);
    string scalarBlock = scalarEnd < 0 ? "" : v4DeviceList[scalarStart..(scalarEnd + 1)];
    // 六轴全用 Position：Rotate 类型在客户端看来是「无方向转速」，游戏会把角度当转速推而不是按位置摆。
    Check("game bridge ScalarCmd declares all six axes as Position",
        scalarBlock.Length > 0
        && System.Text.RegularExpressions.Regex.Matches(scalarBlock, "FeatureDescriptor").Count == 6
        && System.Text.RegularExpressions.Regex.Matches(scalarBlock, "\"ActuatorType\":\"Position\"").Count == 6
        && !scalarBlock.Contains("\"ActuatorType\":\"Rotate\""));
    Check("game bridge ScalarCmd declares StepCount 100 without StepRange",
        System.Text.RegularExpressions.Regex.Matches(scalarBlock, "\"StepCount\":100").Count == 6
        && !scalarBlock.Contains("StepRange"));
    // 单设备/双设备模式的 Scalars[].Index 语义不是六轴轴序，不能宣告六轴 ScalarCmd。
    Check("game bridge split devices do not declare the six-axis ScalarCmd",
        !IntifaceProtocol.DeviceListJson(5, "single", "off", 4).Contains("ScalarCmd")
        && !IntifaceProtocol.DeviceListJson(5, "dual", "off", 4).Contains("ScalarCmd")
        && !IntifaceProtocol.DeviceAddedJson(5, 0, "dual", "off", 4).Contains("ScalarCmd"));

    // 客户端没报 MessageVersion（有的插件只给 ClientName）：一律落回 v3。
    var missingVersionSession = new IntifaceProtocol.SessionState();
    var missingVersionServerInfo = IntifaceProtocol.Process(
        "[{\"RequestServerInfo\":{\"Id\":31,\"ClientName\":\"t\"}}]", "six", _ => { }, "off", null, missingVersionSession);
    Check("game bridge RequestServerInfo without MessageVersion falls back to v3",
        missingVersionServerInfo.Count == 1 && missingVersionServerInfo[0].Contains("\"MessageVersion\":3")
        && missingVersionSession.MessageVersion == 3
        && IntifaceProtocol.Process("[{\"RequestDeviceList\":{\"Id\":32}}]", "six", _ => { }, "off", null, missingVersionSession)[0]
            .Contains("\"ScalarCmd\":["));

    // VibrateCmd 只在振动模式非 off 时宣告；off 模式下桥会明确丢弃振动，宣告出去等于骗客户端。
    Check("game bridge advertises VibrateCmd only when vibrate mode is not off",
        !IntifaceProtocol.DeviceListJson(6, "single", "off", 3).Contains("VibrateCmd")
        && !IntifaceProtocol.DeviceAddedJson(6, 0, "single", "off", 3).Contains("VibrateCmd")
        && IntifaceProtocol.DeviceListJson(6, "single", "position", 3).Contains("\"VibrateCmd\":[")
        && IntifaceProtocol.DeviceAddedJson(6, 1, "dual", "rotate", 3).Contains("\"VibrateCmd\":["));

    // ScalarCmd 是「立即置位」：只有轴序映射 + 位置，没有 Duration（不补插值）。
    var scalarActions = new List<IntifaceProtocol.BridgeAction>();
    var scalarResponses = IntifaceProtocol.Process(
        "[{\"ScalarCmd\":{\"Id\":41,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":1.0},{\"Index\":5,\"Scalar\":0.25}]}}]",
        "six", scalarActions.Add);
    Check("game bridge ScalarCmd routes Index 5 to R2 (and 0 to L0) with no duration",
        scalarActions.SequenceEqual(new IntifaceProtocol.BridgeAction[]
        {
            new IntifaceProtocol.ScalarAction("L0", 9999),
            new IntifaceProtocol.ScalarAction("R2", 2500),
        })
        && scalarResponses.Count == 1 && scalarResponses[0].Contains("\"Ok\""));
    var scalarOutOfRange = IntifaceProtocol.Process(
        "[{\"ScalarCmd\":{\"Id\":42,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":6,\"Scalar\":0.5}]}}]", "six", _ => { });
    Check("game bridge ScalarCmd rejects an out-of-range feature index with ErrorCode 3",
        scalarOutOfRange.Any(response => response.Contains("\"ErrorCode\":3")
            && response.Contains("ScalarCmd feature index out of range")));

    // 一条消息（一个 Id）只回一个响应，且要么全执行、要么一条都不执行：
    // 以前是一条条处理，坏的那条回 Error、好的那条已经发出去，最后还补一个 Ok。
    var partialActions = new List<IntifaceProtocol.BridgeAction>();
    var partialResponses = IntifaceProtocol.Process(
        "[{\"LinearCmd\":{\"Id\":43,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":0.5,\"Duration\":200},{\"Index\":9,\"Position\":0.5,\"Duration\":200}]}}]",
        "six", partialActions.Add);
    Check("game bridge LinearCmd with one bad vector executes nothing and answers once",
        partialActions.Count == 0 && partialResponses.Count == 1
        && partialResponses[0].Contains("\"Error\"") && !partialResponses[0].Contains("\"Ok\""));
    var scalarPartial = new List<IntifaceProtocol.BridgeAction>();
    var scalarPartialResponses = IntifaceProtocol.Process(
        "[{\"ScalarCmd\":{\"Id\":44,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.5},{\"Index\":6,\"Scalar\":0.5}]}}]",
        "six", scalarPartial.Add);
    Check("game bridge ScalarCmd with one bad scalar executes nothing and answers once",
        scalarPartial.Count == 0 && scalarPartialResponses.Count == 1
        && scalarPartialResponses[0].Contains("\"Error\"") && !scalarPartialResponses[0].Contains("\"Ok\""));
    var goodResponses = IntifaceProtocol.Process(
        "[{\"LinearCmd\":{\"Id\":45,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":0.5,\"Duration\":200},{\"Index\":1,\"Position\":0.6,\"Duration\":150}]}}]",
        "six", _ => { });
    Check("game bridge LinearCmd with all-good vectors answers exactly one Ok",
        goodResponses.Count == 1 && goodResponses[0].Contains("\"Ok\"") && !goodResponses[0].Contains("\"Error\""));

    // B2：多客户端仲裁必须解析后逐条拦；混在同一帧里的停止类指令永远不能被丢。
    var gateActions = new List<IntifaceProtocol.BridgeAction>();
    bool deniedAny = false;
    var gateResponses = IntifaceProtocol.Process(
        "[{\"LinearCmd\":{\"Id\":51,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":0.5,\"Duration\":200}]}},{\"StopAllDevices\":{\"Id\":52}}]",
        "single", gateActions.Add, "off", null, null,
        element => { if (!IntifaceProtocol.IsDriveMessage(element)) return true; deniedAny = true; return false; });
    Check("game bridge arbitration parses per message and never drops stops",
        gateActions.Count == 1 && gateActions[0] is IntifaceProtocol.StopAction
        && gateResponses.Count == 2
        && gateResponses.Any(r => r.Contains("\"ErrorCode\":4"))
        && gateResponses.Any(r => r.Contains("\"Ok\""))
        && deniedAny);
    // B3：整形全关时 LinearCmd Duration 下限放宽到 10ms；任一整形开关打开时仍按 60ms 整形阀。
    Check("game bridge min linear duration drops to 10 ms only when shaping is off",
        IntifaceProtocol.MinLinearDurationMs(false) == 10
        && IntifaceProtocol.MinLinearDurationMs(true) == 60);
    // B1：ServerInfo 只含 v2+ 字段（Id/ServerName/MessageVersion/MaxPingTime）。
    Check("game bridge ServerInfo carries only v2+ fields",
        v3ServerInfo[0].Contains("\"Id\"")
        && v3ServerInfo[0].Contains("\"ServerName\"")
        && v3ServerInfo[0].Contains("\"MessageVersion\":3")
        && v3ServerInfo[0].Contains("\"MaxPingTime\"")
        && !v3ServerInfo[0].Contains("ServerMajorVersion")
        && !v3ServerInfo[0].Contains("ServerMinorVersion")
        && !v3ServerInfo[0].Contains("BuildVersion"));

    // ── 覆盖机制：每一种"游戏能表达的意图"都必须有明确处置（要么产生动作、要么明确回一条应答），
    // 不许出现"既没动作也没应答"的静默丢弃 —— 这条锁住"漏掉一类意图"这类问题不会再悄悄发生。
    var covered = new (string Intent, string Payload)[]
    {
        ("RequestServerInfo", "[{\"RequestServerInfo\":{\"Id\":1,\"ClientName\":\"t\",\"MessageVersion\":3}}]"),
        ("RequestDeviceList", "[{\"RequestDeviceList\":{\"Id\":2}}]"),
        ("StartScanning",     "[{\"StartScanning\":{\"Id\":3}}]"),
        ("StopScanning",      "[{\"StopScanning\":{\"Id\":4}}]"),
        ("Ping",              "[{\"Ping\":{\"Id\":5}}]"),
        ("LinearCmd",         "[{\"LinearCmd\":{\"Id\":6,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":0.5,\"Duration\":200}]}}]"),
        ("RotateCmd",         "[{\"RotateCmd\":{\"Id\":7,\"DeviceIndex\":0,\"Rotations\":[{\"Index\":0,\"Speed\":0.3,\"Clockwise\":true}]}}]"),
        ("VibrateCmd",        "[{\"VibrateCmd\":{\"Id\":8,\"DeviceIndex\":0,\"Speeds\":[{\"Index\":0,\"Speed\":0.5}]}}]"),
        ("ScalarCmd",         "[{\"ScalarCmd\":{\"Id\":12,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.5}]}}]"),
        ("StopDeviceCmd",     "[{\"StopDeviceCmd\":{\"Id\":9,\"DeviceIndex\":0}}]"),
        ("StopAllDevices",    "[{\"StopAllDevices\":{\"Id\":10}}]"),
        ("未知消息",           "[{\"NoSuchCmd\":{\"Id\":11}}]"),
    };
    var silent = new List<string>();
    foreach ((string intent, string payload) in covered)
    {
        bridgeActions.Clear();
        var responses = IntifaceProtocol.Process(payload, "six", bridgeActions.Add);
        if (bridgeActions.Count == 0 && responses.Count == 0) silent.Add(intent);
    }
    Check("game bridge gives every intent a disposition (no silent drops)",
        silent.Count == 0);

    // ── 服务层：桥自己有没有真的把「停」「动」「掉线」送出去 ────────────────
    // 上面那些全在纯函数层（IntifaceProtocol）；这一节第一次落到 IntifaceBridgeService 本体：
    // 起监听 → 真 WebSocket 客户端连进来 → 看设备侧到底收到了什么。
    var fakeTransport = new FakeTransport();
    var svcCfg = new AppSettings
    {
        GameBridgePort = 34999,
        GameBridgeMode = "six",
        GameBridgeVibrateMode = "off",
        GameBridgeEnabled = true,
    };
    using var svcEngine = new MotionEngine(fakeTransport, svcCfg);
    using var bridgeSvc = new IntifaceBridgeService(fakeTransport, svcEngine, svcCfg);
    Check("bridge service starts listening on the configured port", bridgeSvc.Start() && bridgeSvc.Active);
    Check("bridge service takes over output at startup (DSTOP first)",
        fakeTransport.Sent.Count > 0 && fakeTransport.Sent[0] == "DSTOP");

    using var bridgeClient = new System.Net.WebSockets.ClientWebSocket();
    bool connected = bridgeClient.ConnectAsync(new Uri("ws://127.0.0.1:34999/"), CancellationToken.None)
        .Wait(TimeSpan.FromSeconds(5));
    Check("bridge service accepts a real Intiface websocket client",
        connected && bridgeClient.State == System.Net.WebSockets.WebSocketState.Open);

    if (connected)
    {
        // 第一件事：像真客户端那样自报身份。追踪文件里必须留下「谁、按哪个版本、什么模式、哪个端口」。
        byte[] hello = System.Text.Encoding.UTF8.GetBytes(
            "[{\"RequestServerInfo\":{\"Id\":0,\"ClientName\":\"HexaTest\",\"MessageVersion\":3}}]");
        bridgeClient.SendAsync(new ArraySegment<byte>(hello),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None)
            .Wait(TimeSpan.FromSeconds(3));
        string sessionLine = string.Empty;
        for (int i = 0; i < 40 && sessionLine.Length == 0; i++)
        {
            Thread.Sleep(50);
            sessionLine = bridgeSvc.RecentLog.Select(entry => entry.Text)
                .FirstOrDefault(text => text.Contains("已接入")) ?? string.Empty;
        }
        Check("bridge trace records who connected, the negotiated version, mode and port",
            sessionLine.Contains("HexaTest") && sessionLine.Contains("MessageVersion=3")
            && sessionLine.Contains("模式 six") && sessionLine.Contains("端口 34999"));

        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            "[{\"LinearCmd\":{\"Id\":1,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":0.9,\"Duration\":300}]}}]");
        bridgeClient.SendAsync(new ArraySegment<byte>(payload),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None)
            .Wait(TimeSpan.FromSeconds(3));
        bool gotLinearFrame = false;
        for (int i = 0; i < 40 && !gotLinearFrame; i++)
        {
            Thread.Sleep(50);
            gotLinearFrame = fakeTransport.Sent.Any(command => command.StartsWith("L0", StringComparison.Ordinal));
        }
        Check("bridge service turns a LinearCmd into a TCode frame on the device side", gotLinearFrame);

        // 设备掉线（串口拔掉/断电）必须主动告诉客户端 DeviceRemoved，否则游戏一直以为设备还在。
        // 客户端还欠一条 LinearCmd 的 Ok 应答，所以这里要一直读到 DeviceRemoved 为止，不能只看第一帧。
        string dropNotice = string.Empty;
        var receiveBuffer = new byte[65536];
        fakeTransport.RaiseConnectionChanged(false);
        for (int i = 0; i < 40 && !dropNotice.Contains("DeviceRemoved"); i++)
        {
            var receiveTask = bridgeClient.ReceiveAsync(new ArraySegment<byte>(receiveBuffer),
                CancellationToken.None);
            if (!receiveTask.Wait(TimeSpan.FromSeconds(1))) break;
            if (receiveTask.Result.MessageType != System.Net.WebSockets.WebSocketMessageType.Text) continue;
            dropNotice += System.Text.Encoding.UTF8.GetString(receiveBuffer, 0, receiveTask.Result.Count);
        }
        Check("bridge service pushes DeviceRemoved to the game when the device drops",
            dropNotice.Contains("DeviceRemoved"));

        int beforeStop = fakeTransport.Sent.Count;
        bridgeSvc.Stop();
        Check("bridge service stops the device on shutdown (DSTOP)",
            !bridgeSvc.Active && fakeTransport.Sent.Skip(beforeStop).Any(command => command == "DSTOP"));
    }



    // 持续动作渲染：把「一直发同一个值」画成正弦往复；电平离中位越远，幅度越大。
    Check("game bridge sustain target oscillates around the level",
        Math.Abs(IntifaceProtocol.SustainTarget(80, 20, 0.0) - 80) < 0.001
        && Math.Abs(IntifaceProtocol.SustainTarget(80, 20, 0.25) - 100) < 0.001
        && Math.Abs(IntifaceProtocol.SustainTarget(80, 20, 0.75) - 60) < 0.001);
    Check("game bridge level repeat threshold is sane", IntifaceProtocol.LevelRepeatThreshold >= 3);

    // 振动模式现在允许 rotate（映射到旋转），其余值一律回 off。
    var vibRotate = new AppSettings { GameBridgeVibrateMode = "ROTATE" };
    vibRotate.Normalize();
    var vibJunk = new AppSettings { GameBridgeVibrateMode = "nonsense" };
    vibJunk.Normalize();
    var vibStroke = new AppSettings { GameBridgeVibrateMode = "stroke" };
    vibStroke.Normalize();
    Check("game bridge vibrate mode accepts rotate and rejects junk",
        vibRotate.GameBridgeVibrateMode == "rotate" && vibStroke.GameBridgeVibrateMode == "stroke"
        && vibJunk.GameBridgeVibrateMode == "off");

    // dual 模式下 StopDeviceCmd 只停被点名的设备；single 模式停整台。
    bridgeActions.Clear();
    IntifaceProtocol.Process("[{\"StopDeviceCmd\":{\"Id\":9,\"DeviceIndex\":1}}]", "dual", bridgeActions.Add);
    Check("game bridge dual mode stops only the requested device",
        bridgeActions.Count == 1 && bridgeActions[0] is IntifaceProtocol.StopDeviceAction only
        && only.DeviceIndex == 1);
    bridgeActions.Clear();
    IntifaceProtocol.Process("[{\"StopDeviceCmd\":{\"Id\":10,\"DeviceIndex\":0}}]", "single", bridgeActions.Add);
    Check("game bridge single mode StopDeviceCmd stops everything",
        bridgeActions.Count == 1 && bridgeActions[0] is IntifaceProtocol.StopAction);

    // 被驱动过的轴不能永久粘住：超过 500ms 没有新指令就算「不再被驱动」。
    Check("game bridge marks an idle axis as no longer driven",
        !IntifaceProtocol.IsDriveStale(nowMs: 10_200, drivenAtMs: 10_000, timeoutMs: 500)
        && IntifaceProtocol.IsDriveStale(nowMs: 10_600, drivenAtMs: 10_000, timeoutMs: 500)
        && !IntifaceProtocol.IsDriveStale(nowMs: 99_999, drivenAtMs: 0, timeoutMs: 500));

    // 设备掉线时要主动告诉客户端（否则它一直以为设备还在）。
    Check("game bridge announces DeviceRemoved when the device drops",
        IntifaceProtocol.DeviceRemovedJson(0).Contains("DeviceRemoved")
        && IntifaceProtocol.DeviceRemovedJson(0).Contains("\"DeviceIndex\":0"));

    var unknown = IntifaceProtocol.Process("[{\"NoSuchCmd\":{\"Id\":8}}]", "single", _ => { });
    Check("game bridge answers unknown messages with Error",
        unknown.Count == 1 && unknown[0].Contains("\"Error\"") && unknown[0].Contains("ErrorCode"));

    var bounceUp = IntifaceProtocol.RotateStep(9990, 100, clockwise: true, 0, 9999);
    Check("game bridge rotate bounces at the upper limit",
        bounceUp.Position == 9999 && !bounceUp.Clockwise && bounceUp.Changed);
    var bounceDown = IntifaceProtocol.RotateStep(10, 100, clockwise: false, 0, 9999);
    Check("game bridge rotate bounces at the lower limit",
        bounceDown.Position == 0 && bounceDown.Clockwise && bounceDown.Changed);
    var degenerate = IntifaceProtocol.RotateStep(5000, 100, clockwise: true, 5000, 5000);
    Check("game bridge rotate stops sending when the range is a single point", !degenerate.Changed);
    var normal = IntifaceProtocol.RotateStep(5000, 120, clockwise: false, 0, 9999);
    Check("game bridge rotate keeps stepping in the requested direction",
        normal.Position == 4880 && !normal.Clockwise && normal.Changed);
    Check("game bridge replies server info with message version 3",
        IntifaceProtocol.Process("[{\"RequestServerInfo\":{\"Id\":9}}]", "single", _ => { })
            .Single().Contains("\"MessageVersion\":3"));

    // 服务级冒烟：模拟模式下开启单桥，验证游戏指令真的落到串口
    serial.OutputEnabled = true;   // 前面急停测试锁定了输出，这里重新解锁（模拟设备）
    cfg.GameBridgeMode = "single";
    cfg.GameBridgePort = 0;  // Normalize 会回退到 12345
    cfg.Normalize();
    Check("game bridge port is normalized to default", cfg.GameBridgePort == 12345);
    using (var bridge = new IntifaceBridgeService(serial, engine, cfg))
    {
        cfg.GameBridgeEnabled = true;
        // 端口刚被上个用例占着时第一次 Start 可能失败：等 700ms 再试一次（和真实启动路径一致）
        bool bridgeStarted = bridge.Start();
        if (!bridgeStarted)
        {
            Thread.Sleep(700);
            bridgeStarted = bridge.Start();
        }
        Check("game bridge starts on simulation transport", bridgeStarted && bridge.Active);
        int beforeBridge = serial.SimulationCommands.Count;

        // 局部辅助：连 12345 完成握手并发一帧带掩码的文本帧
        string WsSend(string json)
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect("127.0.0.1", 12345);
            var ws = client.GetStream();
            string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            string request =
                "GET / HTTP/1.1\r\nHost: 127.0.0.1:12345\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: " +
                key + "\r\nSec-WebSocket-Version: 13\r\n\r\n";
            byte[] reqBytes = System.Text.Encoding.UTF8.GetBytes(request);
            ws.Write(reqBytes, 0, reqBytes.Length);
            byte[] respBuf = new byte[1024];
            int read = 0;
            int attempts = 0;
            while (read < 100 && attempts++ < 50)
            {
                Thread.Sleep(20);
                if (ws.DataAvailable) read += ws.Read(respBuf, read, respBuf.Length - read);
            }
            string handshakeText = System.Text.Encoding.UTF8.GetString(respBuf, 0, read);
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
            var mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
            var frameBytes = new List<byte> { 0x81, (byte)(0x80 | payload.Length) };
            frameBytes.AddRange(mask);
            for (int i = 0; i < payload.Length; i++) frameBytes.Add((byte)(payload[i] ^ mask[i % 4]));
            ws.Write(frameBytes.ToArray(), 0, frameBytes.Count);
            ws.Flush();
            Thread.Sleep(200);
            return handshakeText;
        }

        // 整形全关 = 旧行为：游戏指令原样透传（这条保证"随时能退回"）
        cfg.BridgeInputSmoothing = false;
        cfg.BridgeAxisLink = false;
        string handshake = WsSend("[{\"LinearCmd\":{\"Id\":10,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":1.0,\"Duration\":200}]}}]");
        Check("game bridge completes websocket handshake",
            handshake.Contains("101 Switching Protocols") && handshake.Contains("Sec-WebSocket-Accept"));
        Check("game bridge linear reaches serial as L09999I200",
            serial.SimulationCommands.Count > beforeBridge && serial.SimulationCommands.Contains("L09999I200"));

        // ScalarCmd（Buttplug v4）是「立即置位」：落到串口的帧只带固定最小 ramp，不带游戏给的 Duration。
        int beforeScalarLive = serial.SimulationCommands.Count;
        WsSend("[{\"ScalarCmd\":{\"Id\":15,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.5}]}}]");
        Check("game bridge ScalarCmd reaches serial as an immediate set with the fixed minimal ramp",
            SpinWait.SpinUntil(() => serial.SimulationCommands.Skip(beforeScalarLive).Contains("L05000I60"), 3000));

        // 整形打开（默认）：同一条指令不再一步到底，而是按 50ms 节拍分步爬上去，
        // 且插值时间用节拍长度（60ms），设备才不会一次跳满行程。
        cfg.BridgeInputSmoothing = true;
        cfg.BridgeSmoothingMs = 90;
        int beforeShape = serial.SimulationCommands.Count;
        WsSend("[{\"LinearCmd\":{\"Id\":14,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":1.0,\"Duration\":200}]}}]");
        bool shapedReached = SpinWait.SpinUntil(
            () => serial.SimulationCommands.Skip(beforeShape).Any(cmd => cmd.StartsWith("L099", StringComparison.Ordinal)), 3000);
        var shapedSteps = serial.SimulationCommands.Skip(beforeShape)
            .Where(cmd => cmd.StartsWith("L0", StringComparison.Ordinal)).ToList();
        Check("game bridge shaped output ramps toward the target instead of jumping",
            shapedReached && shapedSteps.Count >= 3 && !shapedSteps[0].StartsWith("L099", StringComparison.Ordinal));

        // ScalarCmd 在整形开着时也必须过整形（低通 + 限速）：否则游戏 60fps 发 0↔1 方波时，
        // 轴永远跑不完一段就被反向重指，端点反复硬反转。这里断言「不是一步到位的 I60 直落」。
        int beforeScalarShape = serial.SimulationCommands.Count;
        WsSend("[{\"ScalarCmd\":{\"Id\":16,\"DeviceIndex\":0,\"Scalars\":[{\"Index\":0,\"Scalar\":0.0}]}}]");
        SpinWait.SpinUntil(() => serial.SimulationCommands.Skip(beforeScalarShape)
            .Any(cmd => cmd.StartsWith("L0", StringComparison.Ordinal) && cmd != "L05000I60"), 3000);
        var scalarShapeSteps = serial.SimulationCommands.Skip(beforeScalarShape)
            .Where(cmd => cmd.StartsWith("L0", StringComparison.Ordinal)).ToList();
        Check("game bridge ScalarCmd is shaped (several steps, never a single immediate jump to the target)",
            scalarShapeSteps.Count >= 3 && !serial.SimulationCommands.Skip(beforeScalarShape).Contains("L00000I60"));
        Check("game bridge records rx/tx instruction log",
            bridge.RxCount >= 1 && bridge.TxCount >= 1
            && bridge.RecentLog.Any(entry => entry.Direction == "RX" && entry.Text.Contains("LinearCmd"))
            && bridge.RecentLog.Any(entry => entry.Direction == "TX" && entry.Text.StartsWith("L0")));

        // 振动不映射到任何真实轴：桥把 V0 原样转发给传输层（设备侧忽略它）。
        // 曾经的「振动 → L0 抽一下」对用户是惊吓不是沉浸，已废弃（见 IntifaceProtocol 的注释）。
        cfg.GameBridgeMode = "single";
        cfg.GameBridgeVibrateMode = "position";
        cfg.Normalize();
        WsSend("[{\"VibrateCmd\":{\"Id\":11,\"DeviceIndex\":0,\"Speeds\":[{\"Index\":0,\"Speed\":0.72}]}}]");
        Check("game bridge forwards vibrate as V0 without any axis mapping",
            SpinWait.SpinUntil(() => bridge.RecentLog.Any(entry => entry.Direction == "TX"
                && entry.Text.StartsWith("V0")), 2000));

        cfg.GameBridgeVibrateMode = "off";
        cfg.Normalize();
        int vibrateTxBefore = bridge.RecentLog.Count(entry => entry.Direction == "TX" && entry.Text.StartsWith("V0"));
        WsSend("[{\"VibrateCmd\":{\"Id\":13,\"DeviceIndex\":0,\"Speeds\":[{\"Index\":0,\"Speed\":0.72}]}}]");
        bool offModeRxArrived = SpinWait.SpinUntil(() => bridge.RecentLog.Any(entry => entry.Direction == "RX"
            && entry.Text.Contains("\"Id\":13")), 2000);
        Check("game bridge vibrate off mode sends no V0 output",
            offModeRxArrived
            && bridge.RecentLog.Count(entry => entry.Direction == "TX" && entry.Text.StartsWith("V0")) == vibrateTxBefore);

        // ── 硬门槛：两个客户端同时连着时的仲裁（最后连上的有发言权；停车指令谁都能喊） ──
        // 背景：一边开着游戏、一边开着 VAM 插件是很常见的；两边都以为自己能驱动设备就是设备乱抖。
        Check("多客户端仲裁：开始前桥上没有残留会话", SpinWait.SpinUntil(() => bridge.ClientCount == 0, 3000));

        cfg.GameBridgeMode = "single";
        cfg.BridgeInputSmoothing = false;    // 整形三件套全关＝原样透传，指令有没有落地一看便知
        cfg.BridgeAxisLink = false;
        cfg.Normalize();

        var clientCounts = new List<int>();
        using (var firstClient = new WsProbe(12345))
        {
            Check("多客户端仲裁：第一个客户端握手成功",
                firstClient.Handshake.Contains("101 Switching Protocols")
                && SpinWait.SpinUntil(() => bridge.ClientCount == 1, 3000));
            clientCounts.Add(bridge.ClientCount);

            using (var secondClient = new WsProbe(12345))
            {
                Check("多客户端仲裁：第二个客户端握手成功",
                    secondClient.Handshake.Contains("101 Switching Protocols")
                    && SpinWait.SpinUntil(() => bridge.ClientCount == 2, 3000));
                clientCounts.Add(bridge.ClientCount);

                // 先连上的失去了发言权：LinearCmd 只回 ErrorCode 4，一个字节都不许落到串口。
                int beforeDenied = serial.SimulationCommands.Count;
                firstClient.Send("[{\"LinearCmd\":{\"Id\":21,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":0.031,\"Duration\":200}]}}]");
                string denial = firstClient.WaitFor("another client owns the device", 2000);
                Check("多客户端仲裁：先连上的发 LinearCmd 收到 ErrorCode 4（另一个客户端占有设备）",
                    denial.Contains("\"ErrorCode\":4") && !denial.Contains("\"Ok\"")
                    && serial.SimulationCommands.Count == beforeDenied);

                // 后连上的有发言权：同一条指令真的驱动了设备。
                int beforeOwner = serial.SimulationCommands.Count;
                secondClient.Send("[{\"LinearCmd\":{\"Id\":22,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":0.42,\"Duration\":200}]}}]");
                Check("多客户端仲裁：后连上的有发言权（指令真的落到串口）",
                    SpinWait.SpinUntil(() => serial.SimulationCommands.Count > beforeOwner, 2000)
                    && serial.SimulationCommands.Skip(beforeOwner).Any(cmd => cmd.StartsWith("L0", StringComparison.Ordinal)));

                // 停车类指令任何客户端都放行：非发言方喊停必须真的停，而且回 Ok（不是 ErrorCode 4）。
                int beforeStop = serial.SimulationCommands.Count;
                firstClient.Send("[{\"StopDeviceCmd\":{\"Id\":23,\"DeviceIndex\":0}}]");
                string stopResponse = firstClient.WaitFor("\"Id\":23", 2000);
                Check("多客户端仲裁：非发言方的 StopDeviceCmd 被放行（回 Ok 且真的发出 DSTOP）",
                    stopResponse.Contains("\"Ok\"") && !stopResponse.Contains("another client owns")
                    && SpinWait.SpinUntil(() => serial.SimulationCommands.Skip(beforeStop).Contains("DSTOP"), 2000));

                int beforeStopAll = serial.SimulationCommands.Count;
                firstClient.Send("[{\"StopAllDevices\":{\"Id\":24}}]");
                string stopAllResponse = firstClient.WaitFor("\"Id\":24", 2000);
                Check("多客户端仲裁：非发言方的 StopAllDevices 也被放行（回 Ok 且真的发出 DSTOP）",
                    stopAllResponse.Contains("\"Ok\"") && !stopAllResponse.Contains("another client owns")
                    && SpinWait.SpinUntil(() => serial.SimulationCommands.Skip(beforeStopAll).Contains("DSTOP"), 2000));
            }

            Check("多客户端仲裁：一个客户端断开后计数回到 1",
                SpinWait.SpinUntil(() => bridge.ClientCount == 1, 3000));
            clientCounts.Add(bridge.ClientCount);
        }
        Check("多客户端仲裁：最后一个客户端断开后计数回到 0",
            SpinWait.SpinUntil(() => bridge.ClientCount == 0, 3000));
        clientCounts.Add(bridge.ClientCount);
        Check($"多客户端仲裁：ClientCount 依次为 1/2/1/0（实测 {string.Join("/", clientCounts)}）",
            string.Join("/", clientCounts) == "1/2/1/0");
        Check("多客户端仲裁：最后一个客户端断开后引擎交还直接输入控制权", engine.DirectInputOwner is null);

        cfg.GameBridgeEnabled = false;
        bridge.Stop();
        Check("game bridge stops cleanly", !bridge.Active);
        // 硬门槛：Stop() 的承诺不只是"不再监听"——它必须真的让设备停下（DSTOP）
        // 并把直接输入控制权交还给引擎，否则声音响应/画面同步会被一个已关闭的桥永久挡住
        // （2026-10-09 审计里正是这个 bug：关了桥之后其它模块全都动不了）。
        var simCmds = serial.SimulationCommands;
        bool sentStop = simCmds.Any(c => c.StartsWith("DSTOP", StringComparison.Ordinal));
        Check("game bridge stop actually stops the device (DSTOP sent)", sentStop);
        Check("game bridge stop releases direct-input ownership", engine.DirectInputOwner is null);
    }

    // ══════════════════════════════════════════════════════════════
    //  硬门槛：串口写失败 → 锁输出 → 恢复
    //  契约（对齐 Services/SerialService.cs）：
    //   ① 写失败不抛给调用方，而是传输层自己标记掉线：LastError 非空 + OutputEnabled=false +
    //      ConnectionChanged(false)（SerialService.Send 的失败分支 → MarkDisconnectedLocked）；
    //   ② 端口没真的打开时，谁把 OutputEnabled 置 true 都必须被拒（SerialService 的
    //      `value && IsOpen` 闸门）——「拔插后还没重连上就点解锁」不能静默地"看着已解锁"；
    //   ③ 端口重连（ConnectionChanged(true)）后引擎必须能重新驱动。
    // ══════════════════════════════════════════════════════════════
    // 真实传输层的那道闸门（不是只有测试桩才这样）：端口没开时置 true 一律被拒。
    using (var closedSerial = new SerialService())
    {
        closedSerial.OutputEnabled = true;
        Check("真实传输层：端口没开时置 OutputEnabled=true 被拒绝（拔插没完成就点解锁不会静默放行）",
            !closedSerial.IsOpen && !closedSerial.OutputEnabled);
        closedSerial.SetSimulationMode(true);
        closedSerial.OutputEnabled = true;
        Check("真实传输层：端口真的打开后置 OutputEnabled=true 才生效",
            closedSerial.IsOpen && closedSerial.OutputEnabled);
    }

    var flakyCfg = new AppSettings();
    var flaky = new FlakyTransport();
    using (var flakyEngine = new MotionEngine(flaky, flakyCfg))
    {
        flakyEngine.Home();                 // 用户点「全部归中」＝解锁输出
        Check("写失败前：桩可动、输出已解锁且真的收到帧",
            flakyEngine.CanRun && flaky.OutputEnabled && flaky.Writes.Count > 0);

        flaky.FailWrites = true;            // 模拟写入抛 IOException（拔线 / 串口卡死）
        int writesBeforeFailure = flaky.Writes.Count;
        flakyEngine.TrySendDirectAxes([10, 20, 30, 40, 50, 60]);
        Check("串口写失败：传输层标记掉线（LastError 非空 + 锁输出 + 端口关闭），这一帧没有发出去",
            !flaky.OutputEnabled && !string.IsNullOrEmpty(flaky.LastError) && !flaky.IsOpen
            && flaky.Writes.Count == writesBeforeFailure);
        Check("串口写失败：引擎收到掉线事件后立刻不可动（CanRun 假）", !flakyEngine.CanRun);
        Check("串口写失败：界面状态说得清发生了什么（不再只是「未连接」三个字）",
            flakyEngine.SafetyStatus.StartsWith("⚠", StringComparison.Ordinal));

        // 端口还没重连上，却来了一个 connected=true（拔插中的竞态）：解锁尝试必须被传输层拒掉。
        int attemptsBefore = flaky.UnlockAttempts;
        flaky.RaiseConnectionChangedWithoutPort();
        Check("端口没重连上就解锁：引擎发了解锁请求，但传输层拒绝（不假装已解锁）",
            flaky.UnlockAttempts > attemptsBefore && flaky.RefusedUnlocks > 0
            && !flaky.OutputEnabled && !flakyEngine.CanRun);

        flakyEngine.Home();                 // 用户此刻点「全部归中」也不该把设备点亮
        Check("端口没重连上时点「全部归中」也不会解锁输出", !flaky.OutputEnabled && !flakyEngine.CanRun);
        Check("端口没重连上时动轴被明确拒绝（返回 false，而不是静默发不出去）",
            !flakyEngine.TryMoveCalibrationAxis("L0", 5000));

        flaky.FailWrites = false;
        flaky.Reconnect();                  // 拔插后端口真的回来了
        Check("端口重连后：引擎重新解锁输出、恢复可动、状态回到「已解锁」",
            flaky.OutputEnabled && flakyEngine.CanRun && flakyEngine.SafetyStatus == "已解锁");
        int writesBeforeRecovery = flaky.Writes.Count;
        Check("端口重连后：引擎能重新驱动设备（真的又发出帧）",
            flakyEngine.TrySendDirectAxes([60, 40, 50, 50, 50, 50])
            && flaky.Writes.Count > writesBeforeRecovery);
    }

    // ══════════════════════════════════════════════════════════════
    //  连接错误的生命周期（Services/SerialService.cs 的 LastError）
    //  并发上引用赋值是原子的，真正的坑是「语义过期」：成功重连、切模拟、用户主动断开
    //  之后仍挂着上一次的失败原因，面板会一直指着一个早就不存在的毛病。
    // ══════════════════════════════════════════════════════════════
    using (var staleSerial = new SerialService())
    {
        staleSerial.ConnectAsync("").GetAwaiter().GetResult();
        Check($"连不上时 LastError 有内容（面板要能说清原因）：{staleSerial.LastError}",
            staleSerial.LastError == "主端口为空");

        staleSerial.SetSimulationMode(true);
        Check("切到模拟设备后旧的连接错误被清掉（不再挂着过期原因）", staleSerial.LastError is null);

        staleSerial.SetSimulationMode(false);
        staleSerial.ConnectAsync("").GetAwaiter().GetResult();
        Check("断开前的错误先被记下来（否则下面那条断言会假通过）", staleSerial.LastError is not null);

        staleSerial.Disconnect();
        Check("用户主动断开后不再显示旧的失败原因（断开不是错误）", staleSerial.LastError is null);
    }

    // ══════════════════════════════════════════════════════════════
    //  遥测端口绑定失败必须对界面可见（Services/GameTelemetryService.cs + BridgePanel 的提示行）
    //  以前绑不上只写日志：界面照旧显示配置端口，用户按提示在 VAM 里填好却一条都收不到。
    //  这里用「本机回环上先占住一个 UDP 端口」制造真实的绑定失败，不碰串口也不碰真设备。
    // ══════════════════════════════════════════════════════════════
    using (var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
    {
        int busyPort = ((IPEndPoint)squatter.Client.LocalEndPoint!).Port;
        using var failedTelemetry = new GameTelemetryService(busyPort, null, () => false, () => "");
        Check($"遥测绑不上端口时：Listening=false、Port=0、失败原因非空（实测原因：{failedTelemetry.ListenError}）",
            !failedTelemetry.Listening && failedTelemetry.Port == 0
            && !string.IsNullOrWhiteSpace(failedTelemetry.ListenError));
        int suggestion = GameTelemetryService.SuggestFreePort(busyPort);
        Check($"遥测绑不上端口时：换端口建议是一个真的能绑上的端口（{busyPort} → {suggestion}）",
            suggestion != busyPort && LoopbackUdpPortFree(suggestion));
        Check($"遥测换端口建议落在合法区间（65535 → {GameTelemetryService.SuggestFreePort(65535)}，绕回 1024 起）",
            GameTelemetryService.SuggestFreePort(65535) is >= 1024 and < 65536);
    }

    int freePort;
    using (var picker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        freePort = ((IPEndPoint)picker.Client.LocalEndPoint!).Port;
    using (var liveTelemetry = new GameTelemetryService(freePort, null, () => false, () => ""))
    {
        Check($"遥测端口没被占时：Listening=true 且对外报的是实际端口 {freePort}",
            liveTelemetry.Listening && liveTelemetry.Port == freePort && liveTelemetry.ListenError is null);
    }

    // ══════════════════════════════════════════════════════════════
    //  硬门槛：急停 × 归中的竞态（自动路径绝不能替用户解除急停）
    // ══════════════════════════════════════════════════════════════
    using (var raceEngine = new MotionEngine(serial, cfg))
    {
        raceEngine.EmergencyStop();
        bool refused = !raceEngine.HomeUnlessEstopped();
        Check("急停锁存时 HomeUnlessEstopped 拒绝归中（不替用户解锁）",
            refused && raceEngine.EmergencyStopped);
        raceEngine.Home();
        Check("用户显式点「全部归中」后才解除急停", !raceEngine.EmergencyStopped);
    }

    // ══════════════════════════════════════════════════════════════
    //  脚本编排器（时间轴换算 / 采样 / 导出）
    // ══════════════════════════════════════════════════════════════
    Check("composer cycle seconds follow bpm",
        Math.Abs(ScriptComposerService.CycleSeconds(60) - 1.0) < 1e-9
        && Math.Abs(ScriptComposerService.CycleSeconds(120) - 0.5) < 1e-9
        && Math.Abs(ScriptComposerService.CycleSeconds(0) - 60.0 / ScriptComposerService.MinBpm) < 1e-9
        && Math.Abs(ScriptComposerService.CycleSeconds(9999) - 60.0 / ScriptComposerService.MaxBpm) < 1e-9);

    Check("composer card width and duration conversions round trip",
        Math.Abs(ScriptComposerService.SegmentWidth(2.0) - 80.0) < 1e-9
        && Math.Abs(ScriptComposerService.SegmentWidth(0.5) - ScriptComposerService.MinSegmentWidth) < 1e-9
        && Math.Abs(ScriptComposerService.DurationFromPixels(200.0) - 5.0) < 1e-9
        && Math.Abs(ScriptComposerService.DurationFromPixels(1.0) - ScriptSegment.MinDurationSeconds) < 1e-9);

    var composerPreset = new StrokePreset
    {
        Id = "unit_test_preset",
        Label = "单测动作",
        L0 = [0.1, 0.9, 0.25, 0.5, 0, 0],
        R2 = [0.2, 0.8, 0, 0, 0, 0],
    };
    composerPreset.Motion[5] = "Parabolic";
    var composerPresets = new List<StrokePreset> { composerPreset };

    var composition = new ScriptComposition
    {
        Name = "单测合成",
        Segments =
        [
            new ScriptSegment { PresetId = "unit_test_preset", Label = "A", DurationSeconds = 2.0 },
            new ScriptSegment { PresetId = "missing_id", Label = "B", DurationSeconds = 1.0 },
            new ScriptSegment { PresetId = "unit_test_preset", Label = "C", DurationSeconds = 4.0, Intensity = 0.5, TransitionSeconds = 1.0 },
            new ScriptSegment { PresetId = "unit_test_preset", Label = "D", DurationSeconds = 2.0, Intensity = 1.5, TransitionSeconds = 1.0 },
        ],
    };
    composition.Normalize();

    Check("composer segment index maps time to segment",
        ScriptComposerService.SegmentIndexAt(composition, 0) == 0
        && ScriptComposerService.SegmentIndexAt(composition, 1.999) == 0
        && ScriptComposerService.SegmentIndexAt(composition, 2.0) == 1
        && ScriptComposerService.SegmentIndexAt(composition, 3.0) == 2
        && ScriptComposerService.SegmentIndexAt(composition, 99) == 3
        && ScriptComposerService.SegmentIndexAt(null, 0) == -1);

    (int composerSegments, double composerSeconds) = ScriptComposerService.Estimate(composition, composerPresets);
    Check("composer estimate counts resolvable segments and total duration",
        composerSegments == 3 && Math.Abs(composerSeconds - 9.0) < 1e-9);

    double[] composerSampled = ScriptComposerService.SampleComposition(composition, composerPresets, 0.75, 60);
    double[] composerExpected = ScriptComposerService.SampleSegment(composerPreset, composition.Segments[0], 0.75, 1.0);
    Check("composer sampling inside a segment equals segment sampling",
        composerSampled.Zip(composerExpected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9));

    double[] composerEngine = ScriptComposerService.SampleSegment(composerPreset, composition.Segments[0], 0.75, 1.0);
    double composerEngineL0 = MotionEngine.SampleTempestAxis(
        0.1, 0.9, 0.25, 0.5, StrokeMotion.Sinusoidal, 0.75 * Math.Tau, 1.0, 0, 0, 0, 0);
    Check("composer sample uses the engine tempest formula",
        Math.Abs(composerEngine[0] - Math.Clamp(composerEngineL0 * 100, 0, 100)) < 1e-9);

    // 段 D 从 7.0s 开始、过渡 1s：t = 7.5s 应为「C 段末值 → D 段曲线」的中点
    double[] composerPrevEnd = ScriptComposerService.SampleSegment(
        composerPreset, composition.Segments[2], 4.0, 1.0);
    double[] composerCurve = ScriptComposerService.SampleSegment(
        composerPreset, composition.Segments[3], 0.5, 1.0);
    double[] composerBlended = ScriptComposerService.SampleComposition(composition, composerPresets, 7.5, 60);
    Check("composer transition blends from previous segment end",
        Enumerable.Range(0, 6).All(i =>
            Math.Abs(composerBlended[i] - (composerPrevEnd[i] + (composerCurve[i] - composerPrevEnd[i]) * 0.5)) < 1e-9));

    double[] composerAfter = ScriptComposerService.SampleComposition(composition, composerPresets, 8.5, 60);
    double[] composerAfterCurve = ScriptComposerService.SampleSegment(
        composerPreset, composition.Segments[3], 1.5, 1.0);
    Check("composer stops blending after the transition window",
        composerAfter.Zip(composerAfterCurve).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9));

    string composed = ScriptComposerService.ComposeFunscript(composition, composerPresets, 60);
    IReadOnlyDictionary<string, IReadOnlyList<WaveScriptCodec.ActionPoint>> composedTracks = WaveScriptCodec.ParseFunscriptTracks(composed);
    Check("composer export writes every installed axis",
        composedTracks.Count == Osr6DeviceProfile.InstalledAxes.Length
        && composedTracks.ContainsKey("L0") && composedTracks.ContainsKey("R2"));
    Check("composer export keeps the full timeline",
        composedTracks["L0"][0].At == 0 && composedTracks["L0"][^1].At == 9000);
    Check("composer export positions stay inside 0..100",
        composedTracks.Values.All(track => track.All(point => point.Pos is >= 0 and <= 100)));

    int composedAtTransition = composedTracks["L0"].First(point => point.At >= 7500).Pos;
    Check("composer export matches the live preview at the transition",
        Math.Abs(composedAtTransition - composerBlended[0]) <= 0.5);

    bool composerRejectedEmpty = false;
    try { ScriptComposerService.ComposeFunscript(new ScriptComposition(), composerPresets, 60); }
    catch (InvalidOperationException) { composerRejectedEmpty = true; }
    Check("composer export rejects an empty composition", composerRejectedEmpty);

    var orphanComposition = new ScriptComposition
    {
        Name = "孤儿段",
        Segments = [new ScriptSegment { PresetId = "missing_id", DurationSeconds = 3.0 }],
    };
    orphanComposition.Normalize();
    bool composerRejectedOrphan = false;
    try { ScriptComposerService.ComposeFunscript(orphanComposition, composerPresets, 60); }
    catch (InvalidOperationException) { composerRejectedOrphan = true; }
    Check("composer export rejects a composition with no resolvable action", composerRejectedOrphan);

    ScriptComposition composerRoundTrip = ScriptComposition.FromJson(composition.ToJson());
    Check("composer JSON round trip keeps segments",
        composerRoundTrip.Segments.Count == 4
        && Math.Abs(composerRoundTrip.Segments[2].TransitionSeconds - 1.0) < 1e-9
        && Math.Abs(composerRoundTrip.Segments[3].Intensity - 1.5) < 1e-9);
    // 合成落盘往返：CompositionStore.Save → Load → ListNames（用户反馈「保存的合成载不回来」）
    var storedComposition = new ScriptComposition
    {
        Name = "单测_临时合成",
        Bpm = 123,
        Segments = [new ScriptSegment { PresetId = "unit_test_preset", DurationSeconds = 3.0 }],
    };
    CompositionStore.Save(storedComposition);
    ScriptComposition? storedBack = CompositionStore.Load("单测_临时合成");
    Check("composition store round trip keeps bpm and segments",
        storedBack is { Bpm: 123 }
        && storedBack.Segments.Count == 1
        && Math.Abs(storedBack.Segments[0].DurationSeconds - 3.0) < 1e-9
        && CompositionStore.ListNames().Contains("单测_临时合成"));
    File.Delete(CompositionStore.PathFor("单测_临时合成"));

    Check("composer name sanitizing strips invalid characters",
        ScriptComposition.SanitizeName("a/b:c*?") == "a_b_c__" && ScriptComposition.SanitizeName("  ") == "未命名");

    // ── 开发用：生成「平滑对比」演示素材（HEXA_SMOOTH_DEMO=<dir> 时写数据 + 两份 funscript）──
    if (Environment.GetEnvironmentVariable("HEXA_SMOOTH_DEMO") is { Length: > 0 } demoDir)
    {
        Directory.CreateDirectory(demoDir);
        // 一份"典型"的演示脚本：慢段 → 快段 → 长静止 → 变速 → 高频抖动
        var demoActions = new List<WaveScriptCodec.ActionPoint>();
        long clock = 0;
        void Add(long at, int pos) => demoActions.Add(new WaveScriptCodec.ActionPoint(at, pos));
        // ① 慢段：每 800ms 一个来回
        for (int i = 0; i < 3; i++) { Add(clock, 10); Add(clock + 400, 90); clock += 800; }
        // ② 快段：每 160ms 一个来回
        for (int i = 0; i < 10; i++) { Add(clock, 10); Add(clock + 80, 90); clock += 160; }
        // ③ 长静止 2 秒（空档）
        Add(clock, 90); clock += 2000; Add(clock, 90);
        // ④ 变速：间隔逐次变短（先空 100ms，避免和上一段的收尾动作落在同一毫秒上）
        clock += 100;
        for (int i = 0; i < 6; i++) { Add(clock, i % 2 == 0 ? 15 : 85); clock += 300 - i * 40; }
        // ⑤ 高频抖动：每 60ms 一次，行程 35
        for (int i = 0; i < 20; i++) { Add(clock, i % 2 == 0 ? 32 : 68); clock += 60; }
        Add(clock + 400, 50);

        var curve = ScriptSmoothing.Prepare(demoActions);
        const double strength = 100;

        // 采样成对比数据（位置 + 速度），交给 HTML 演示页画图
        var rows = new List<string>();
        double previousLinear = curve.SampleLinear(0), previousSmooth = curve.SampleCurved(0);
        const double dt = 0.01;   // 10ms 采样
        for (long ms = 0; ms <= clock + 400; ms += 10)
        {
            double linear = curve.Sample(ms, 0);
            double smooth = curve.Sample(ms, strength);
            double linearVelocity = (linear - previousLinear) / dt;
            double smoothVelocity = (smooth - previousSmooth) / dt;
            rows.Add($"{{\"t\":{ms},\"linear\":{linear:F4},\"smooth\":{smooth:F4}," +
                     $"\"linearVelocity\":{linearVelocity:F1},\"smoothVelocity\":{smoothVelocity:F1}}}");
            previousLinear = linear; previousSmooth = smooth;
        }
        File.WriteAllText(Path.Combine(demoDir, "samples.json"),
            "{\"durationMs\":" + (clock + 400) + ",\"samples\":[" + string.Join(",", rows) + "]}");

        // ② 专门用来「听差别」的一对：动作点稀疏 + 慢速大行程（平滑在这里差别最大），
        //    末尾再跟一段高频抖动（平滑把"嗡嗡"抹掉的地方）
        var abActions = new List<WaveScriptCodec.ActionPoint>();
        long ab = 0;
        for (int i = 0; i < 6; i++)   // 慢速：每 600ms 走满行程
        {
            abActions.Add(new(ab, i % 2 == 0 ? 0 : 100));
            abActions.Add(new(ab + 600, i % 2 == 0 ? 100 : 0));
            ab += 600;
        }
        for (int i = 0; i < 6; i++)   // 中速：每 300ms
        {
            abActions.Add(new(ab, i % 2 == 0 ? 10 : 90));
            abActions.Add(new(ab + 300, i % 2 == 0 ? 90 : 10));
            ab += 300;
        }
        for (int i = 0; i < 16; i++)  // 高频抖动：每 70ms
        {
            abActions.Add(new(ab, i % 2 == 0 ? 35 : 65));
            ab += 70;
        }
        abActions.Add(new(ab + 300, 50));
        var abCurve = ScriptSmoothing.Prepare(abActions);
        File.WriteAllText(Path.Combine(demoDir, "平滑对比-原始.funscript"),
            JsonSerializer.Serialize(new { actions = abActions.Select(a => new { at = a.At, pos = a.Pos }) },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var abResampled = new List<object>();
        for (long ms = 0; ms <= ab + 300; ms += 20)
            abResampled.Add(new { at = ms, pos = (int)Math.Round(abCurve.Sample(ms, 100)) });
        File.WriteAllText(Path.Combine(demoDir, "平滑对比-平滑后.funscript"),
            JsonSerializer.Serialize(new { actions = abResampled },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        // 这一对在慢段上的形状差（直线 = 匀速三角波；平滑 = S 型，两端减速、到位停顿）
        double abMaxDiff = 0;
        for (long ms = 0; ms <= 3600; ms += 10)
            abMaxDiff = Math.Max(abMaxDiff, Math.Abs(abCurve.Sample(ms, 100) - abCurve.Sample(ms, 0)));
        Console.WriteLine($"     A/B 脚本: 慢段/中段上「平滑 vs 直线」的最大位置差={abMaxDiff:F1} 个行程点");

        // 两份 funscript：原样 / 平滑后重采样（都用产品代码，方便拿去实机对比手感）
        File.WriteAllText(Path.Combine(demoDir, "演示-原样.funscript"),
            JsonSerializer.Serialize(new { actions = demoActions.Select(a => new { at = a.At, pos = a.Pos }) },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var resampled = new List<object>();
        for (long ms = 0; ms <= clock + 400; ms += 20)
            resampled.Add(new { at = ms, pos = (int)Math.Round(curve.Sample(ms, strength)) });
        File.WriteAllText(Path.Combine(demoDir, "演示-平滑后.funscript"),
            JsonSerializer.Serialize(new { actions = resampled },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        // 关键指标：每个动作点「左右两侧速度差」（= 拐角有多硬）。
        // 直线插值时速度是分段常数，拐角处直接从一个速度跳到另一个；平滑后拐角变成圆角，速度连上。
        double worstLinear = 0, worstSmooth = 0, worstAt = 0;
        for (int i = 1; i < demoActions.Count - 1; i++)
        {
            long at = demoActions[i].At;
            double leftLinear = curve.SampleLinear(at - 2), rightLinear = curve.SampleLinear(at + 2);
            double leftSmooth = curve.SampleCurved(at - 2), rightSmooth = curve.SampleCurved(at + 2);
            // 直线插值的拐角跳变是精确值：左右两段的割线速度直接跳
            double leftSecant = (demoActions[i].Pos - demoActions[i - 1].Pos)
                / (double)Math.Max(1, demoActions[i].At - demoActions[i - 1].At);
            double rightSecant = (demoActions[i + 1].Pos - demoActions[i].Pos)
                / (double)Math.Max(1, demoActions[i + 1].At - demoActions[i].At);
            double jumpLinear = Math.Abs(rightSecant - leftSecant) * 1000;   // 位置/毫秒 → 位置/秒
            double jumpSmooth = Math.Abs((rightSmooth - leftSmooth) / 0.004);
            if (jumpLinear > worstLinear) { worstLinear = jumpLinear; worstAt = at; }
            worstSmooth = Math.Max(worstSmooth, jumpSmooth);
        }
        // 是否严格经过作者写的每个动作点（端点精确）
        double worstPointError = 0;
        foreach (var point in demoActions)
            worstPointError = Math.Max(worstPointError, Math.Abs(curve.SampleCurved(point.At) - point.Pos));
        Console.WriteLine($"     smooth demo: 拐角速度跳变 直线最大={worstLinear:F0}/秒（t={worstAt}ms） 平滑后最大={worstSmooth:F0}/秒");
        Console.WriteLine($"     smooth demo: 经过动作点的最大误差={worstPointError:F4} 个位置单位");
    }

    // ── 播放时脚本平滑（单调三次曲线）─────────────────────────────────
    {
        var zigzag = new List<WaveScriptCodec.ActionPoint>
        {
            new(0, 10), new(200, 90), new(400, 10), new(600, 90), new(800, 10),
        };
        var zigzagCurve = ScriptSmoothing.Prepare(zigzag);
        bool throughPoints = true;
        foreach (var point in zigzag)
            throughPoints &= Math.Abs(zigzagCurve.SampleCurved(point.At) - point.Pos) < 1e-9;
        Check("script smoothing passes through every action point exactly", throughPoints);

        // 单调：两段之间不许冲过作者写的极值（普通三次样条会过冲，那就是"设备跑到脚本没写的位置"）
        bool insideRange = true;
        for (int i = 0; i < zigzag.Count - 1; i++)
        {
            double low = Math.Min(zigzag[i].Pos, zigzag[i + 1].Pos);
            double high = Math.Max(zigzag[i].Pos, zigzag[i + 1].Pos);
            for (long t = zigzag[i].At; t <= zigzag[i + 1].At; t += 5)
            {
                double value = zigzagCurve.SampleCurved(t);
                insideRange &= value >= low - 1e-9 && value <= high + 1e-9;
            }
        }
        Check("script smoothing never overshoots the segment it belongs to", insideRange);

        // 上升段必须单调不回头
        var rampCurve = ScriptSmoothing.Prepare([new(0, 0), new(500, 100)]);
        bool monotone = true;
        double previousRamp = rampCurve.SampleCurved(0);
        for (long t = 5; t <= 500; t += 5)
        {
            double value = rampCurve.SampleCurved(t);
            monotone &= value >= previousRamp - 1e-9;
            previousRamp = value;
        }
        Check("script smoothing keeps a rising segment monotone", monotone);

        // 强度 0 = 现在的手感（逐点等于直线插值）
        bool identicalToLinear = true;
        for (long t = 0; t <= 800; t += 7)
            identicalToLinear &= Math.Abs(zigzagCurve.Sample(t, 0) - zigzagCurve.SampleLinear(t)) < 1e-12;
        Check("script smoothing at 0% is exactly the current linear playback", identicalToLinear);

        // 拐角处速度跳变：直线是分段常数（硬折返），平滑后应该小一个数量级
        double worstLinearJump = 0, worstSmoothJump = 0;
        for (int i = 1; i < zigzag.Count - 1; i++)
        {
            long at = zigzag[i].At;
            double leftSecant = (zigzag[i].Pos - zigzag[i - 1].Pos)
                / (double)(zigzag[i].At - zigzag[i - 1].At);
            double rightSecant = (zigzag[i + 1].Pos - zigzag[i].Pos)
                / (double)(zigzag[i + 1].At - zigzag[i].At);
            worstLinearJump = Math.Max(worstLinearJump, Math.Abs(rightSecant - leftSecant) * 1000);
            double leftSmooth = zigzagCurve.SampleCurved(at - 2);
            double rightSmooth = zigzagCurve.SampleCurved(at + 2);
            worstSmoothJump = Math.Max(worstSmoothJump, Math.Abs((rightSmooth - leftSmooth) / 0.004));
        }
        Check("script smoothing rounds the velocity jump at a corner",
            worstLinearJump > 300 && worstSmoothJump < worstLinearJump / 5);

        // 坏输入不能崩：空轨道 / 单点 / 同一毫秒两个动作
        Check("script smoothing survives empty and single-point tracks",
            ScriptSmoothing.Prepare(null).Sample(100, 100) == 50
            && ScriptSmoothing.Prepare([]).Sample(100, 100) == 50
            && ScriptSmoothing.Prepare([new(0, 42)]).Sample(100, 100) == 42);
        var duplicateTimeCurve = ScriptSmoothing.Prepare([new(0, 20), new(100, 20), new(100, 80), new(200, 80)]);
        Check("script smoothing never returns NaN for duplicate timestamps",
            duplicateTimeCurve.Sample(100, 100) is >= 0 and <= 100
            && duplicateTimeCurve.Sample(150, 100) is >= 0 and <= 100);
    }

    // ── 游戏桥动作整形（游戏指令 → 连续、不超速的动作）──────────────────
    {
        // ① 关闭平滑 + 放宽限速 = 一步到位（旧行为：原样透传）
        double instant = BridgeMotionShaper.Step(50, 100, 0.05, 0,
            BridgeMotionShaper.LegacyMaxSpeedPerSecond);
        Check("bridge shaper can reproduce the old pass-through behaviour",
            instant >= 99.99);

        // ② 平滑打开：同样的 dt 只走一部分，越大的时间常数走得越少
        double soft = BridgeMotionShaper.Step(50, 100, 0.05, 90, BridgeMotionShaper.LegacyMaxSpeedPerSecond);
        double softer = BridgeMotionShaper.Step(50, 100, 0.05, 300, BridgeMotionShaper.LegacyMaxSpeedPerSecond);
        Check("bridge shaper smooths step commands", soft > 50 && soft < 100 && softer < soft);

        // ③ 舒适档限速：一个 tick 最多走「速度上限 × dt」
        double capped = BridgeMotionShaper.Step(50, 100, 0.05, 0, 360);
        Check("bridge shaper honours the comfort speed limit",
            Math.Abs(capped - (50 + 360 * 0.05)) < 0.01);

        // ④ 稳态跟随：目标不变时最终会到位，不会永远差一点
        double value = 50;
        for (int i = 0; i < 200; i++)
            value = BridgeMotionShaper.Step(value, 100, 0.05, 90, 360);
        Check("bridge shaper converges to a steady target", Math.Abs(value - 100) < 0.5);

        // ⑤ 抖动抑制：目标在 40/60 之间来回跳时，整形后的输出摆幅明显更小
        double shakyRaw = 50, shakySmooth = 50;
        double rawMin = 50, rawMax = 50, smoothMin = 50, smoothMax = 50;
        for (int i = 0; i < 40; i++)
        {
            double target = i % 2 == 0 ? 40 : 60;
            shakyRaw = BridgeMotionShaper.Step(shakyRaw, target, 0.02, 0, BridgeMotionShaper.LegacyMaxSpeedPerSecond);
            shakySmooth = BridgeMotionShaper.Step(shakySmooth, target, 0.02, 150, 360);
            rawMin = Math.Min(rawMin, shakyRaw); rawMax = Math.Max(rawMax, shakyRaw);
            smoothMin = Math.Min(smoothMin, shakySmooth); smoothMax = Math.Max(smoothMax, shakySmooth);
        }
        Check("bridge shaper damps command jitter",
            (smoothMax - smoothMin) < (rawMax - rawMin) * 0.6);

        // ⑥ 坏输入不产生 NaN / 越界
        Check("bridge shaper survives bad input",
            BridgeMotionShaper.Step(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN) is >= 0 and <= 100
            && BridgeMotionShaper.Step(50, 100, -1, 90, 360) == 50
            && BridgeMotionShaper.Step(50, 999, 10, 90, 360) <= 100);
    }

    // 游戏伴随的「动作来源」：只认 auto / sound / telemetry，坏值回落 auto；
    // 旧版本只有「做法：简易/进阶」，要按旧语义迁移过来（advanced→auto，simple→sound）。
    {
        var badSource = new AppSettings { CompanionSource = "随便写的" };
        badSource.Normalize();
        Check("companion source only accepts auto/sound/telemetry",
            badSource.CompanionSource == "auto" && new AppSettings().CompanionSource.Length > 0);
        // 全新安装（没设过「做法」）要落到推荐的 auto；老用户的「简易」保持原来的 sound 语义
        Check("全新安装默认「自动」，老配置的「简易」仍是「只用声音」",
            new AppSettings().CompanionSource == "auto"
            && new AppSettings { CompanionKind = "simple" }.CompanionSource == "sound");

        var fromAdvanced = new AppSettings { CompanionKind = "advanced", CompanionSource = "" };
        fromAdvanced.Normalize();
        var fromSimple = new AppSettings { CompanionKind = "simple", CompanionSource = "" };
        fromSimple.Normalize();
        Check("旧的「做法」按语义迁移成动作来源（进阶→自动、简易→只用声音）",
            fromAdvanced.CompanionSource == "auto" && fromSimple.CompanionSource == "sound");

        var explicitSource = new AppSettings { CompanionKind = "simple", CompanionSource = "telemetry" };
        explicitSource.Normalize();
        Check("已经存了动作来源时不会被旧字段覆盖", explicitSource.CompanionSource == "telemetry");
    }

    // 伴随的「底色动作」必须是**一套**设置、两条路径都读它。
    // 病根：以前简易面板写 CompanionMode/CompanionIntensity（只有 ForegroundWatcher 用，而它在伴随开启时
    // 直接 return），规则引擎读的却是 CompanionBaseMode —— 于是"用户设的动作"根本不生效。
    {
        var merged = new AppSettings
        {
            CompanionProcess = "game-a",
            CompanionMode = "gentle_wave",
            CompanionIntensity = 1.4,
            CompanionBaseMode = "",
            CompanionBaseIntensity = 1.0,
        };
        merged.Normalize();
        Check("旧的「服务动作/服务强度」会被迁移进合并后的底色动作",
            merged.CompanionBaseMode == "gentle_wave" && Math.Abs(merged.CompanionBaseIntensity - 1.4) < 1e-9);

        var rules = new AppSettings
        {
            CompanionProcess = "game-a",
            CompanionBaseMode = "game_flow",
            CompanionBaseIntensity = 1.2,
            CompanionSource = "sound",
        };
        rules.Normalize();
        GameCompanionRules.Apply(rules);
        var gallery = rules.Rules.First(rule => rule.Id == "companion-base");
        Check("规则引擎播放的就是用户设的那套底色动作/强度（不再是另一套字段）",
            gallery.Mode == "game_flow" && Math.Abs(gallery.Intensity - 1.2) < 1e-9);

        // 「每个游戏一套配置」以前只对 ForegroundWatcher 生效；现在规则引擎路径也必须吃到它
        rules.GameProfiles["game-a"] = new GameProfile { Process = "game-a", Mode = "gentle_wave", Intensity = 0.7 };
        GameCompanionRules.Apply(rules);
        var profiled = rules.Rules.First(rule => rule.Id == "companion-base");
        Check("专属配置（每个游戏一套）对规则引擎路径同样生效",
            profiled.Mode == "gentle_wave" && Math.Abs(profiled.Intensity - 0.7) < 1e-9);
    }

    // 长脚本 / 密集脚本必须能读进来，而且抽稀不能削平换向峰值。
    // 病根：以前 (a) 动作点超过 2 万**直接抛异常拒绝整份脚本**（2026 年 FunGen 那类帧精确工具
    // 生成的整片脚本很容易过线）；(b) 抽稀按等间隔下标取样，会把峰值整片削掉。
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"actions\":[");
        const int total = 30000;                     // 远超旧硬墙（20000）
        for (int i = 0; i < total; i++)
        {
            // 位置在 0..100 之间来回，形成密集的换向峰值；每 100 点插一个极值尖峰
            // 注意别写错：200 的倍数给 0、+100 给 100（两个极值都必须真的出现，否则测不出抽稀是否保峰）
            int pos = (i % 200 == 0) ? 0 : (i % 200 == 100) ? 100 : (i % 2 == 0 ? 45 : 55);
            if (i > 0) sb.Append(',');
            sb.Append("{\"at\":").Append(i * 10).Append(",\"pos\":").Append(pos).Append('}');
        }
        sb.Append("]}");

        bool denseParsed = true;
        IReadOnlyList<WaveScriptCodec.ActionPoint> points = [];
        try { points = WaveScriptCodec.ParseFunscript(sb.ToString()); }
        catch (Exception ex) { denseParsed = false; Console.WriteLine("     解析抛异常：" + ex.Message); }

        Check($"3 万个动作点的长脚本能读进来（不再直接拒绝），得到 {points.Count} 点", denseParsed && points.Count > 1000);
        if (denseParsed && points.Count > 0)
        {
            int minPos = points.Min(p => p.Pos), maxPos = points.Max(p => p.Pos);
            Check($"抽稀保住了换向峰值（原始 0–100，抽稀后 {minPos}–{maxPos}）", minPos == 0 && maxPos == 100);
            Check("抽稀后仍按时间升序且首尾保留",
                points[0].At == 0 && points[^1].At == (total - 1) * 10
                && points.Zip(points.Skip(1)).All(pair => pair.First.At < pair.Second.At));
        }
    }

    // 2026-09 新增的三件事：动作来源多一个 screen、媒体同步默认关、自动池认自建动作 id。
    {
        var screenSource = new AppSettings { CompanionSource = "screen" };
        screenSource.Normalize();
        Check("伴随的动作来源认得「画面内容」（screen 在白名单里能存活过夜）",
            screenSource.CompanionSource == "screen");

        var mediaDefault = new AppSettings();
        mediaDefault.Normalize();
        Check($"媒体同步默认关闭、端口默认自动探测（enabled={mediaDefault.MediaSyncEnabled}，port={mediaDefault.MediaSyncPort}）",
            !mediaDefault.MediaSyncEnabled && mediaDefault.MediaSyncPort == 0);

        var mediaBad = new AppSettings { MediaSyncPlayer = "随便写的播放器", MediaSyncOffsetMs = 99999, MediaSyncPort = 123456 };
        mediaBad.Normalize();
        Check("媒体同步的坏配置会被清洗（播放器回落 mpv、偏移夹到 ±5s、端口夹到 65535）",
            mediaBad.MediaSyncPlayer == "mpv" && mediaBad.MediaSyncOffsetMs == 5000 && mediaBad.MediaSyncPort == 65535);

        // 自建动作在随机池里的 id 形如 stroke:<presetId>：必须能原样存下来（不能被白名单过滤掉）
        var pool = new AppSettings { EnabledAutoPatterns = ["organic_flow", "stroke:my-custom"] };
        pool.Normalize();
        Check("自建动作 id（stroke:xxx）能存进「参与随机的动作」并存活",
            pool.EnabledAutoPatterns.Contains("stroke:my-custom"));

        var effort = new AppSettings { AiReasoningEffort = "随便写的" };
        effort.Normalize();
        Check("AI 思考强度只认 none/low/medium/high，坏值回落 none", effort.AiReasoningEffort == "none");

        var logs = new AppSettings { LogMaxMegabytes = 9999 };
        logs.Normalize();
        Check("日志上限被夹到 500MB 以内（防止手改配置写崩磁盘）", logs.LogMaxMegabytes == 500);
        // 深度/节奏默认全自动：不勾「自定义」时永远按 50 / 1.0 基准走，勾上才用他拖的值。
        var autoDepth = new AppSettings { ScreenSyncDepth = 88, ScreenSyncSpeed = 2.2, AudioDepth = 12, AudioSpeed = 0.4 };
        Check("深度节奏默认全自动（不勾自定义就按 50 / 1.0 基准走画面和声音）",
            !autoDepth.ScreenSyncCustom && !autoDepth.AudioCustom
            && autoDepth.ScreenDepthEffective == 50 && autoDepth.ScreenSpeedEffective == 1.0
            && autoDepth.AudioDepthEffective == 50 && autoDepth.AudioSpeedEffective == 1.0);
        autoDepth.ScreenSyncCustom = true;
        autoDepth.AudioCustom = true;
        Check("勾上自定义后深度和节奏改回用户设的值",
            autoDepth.ScreenDepthEffective == 88 && Math.Abs(autoDepth.ScreenSpeedEffective - 2.2) < 1e-9
            && autoDepth.AudioDepthEffective == 12 && Math.Abs(autoDepth.AudioSpeedEffective - 0.4) < 1e-9);
        AppSettings? customRoundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(
            new AppSettings { ScreenSyncCustom = true, AudioCustom = true, ScreenSyncDepth = 77, AudioSpeed = 1.7 }));
        Check("自定义开关和他设的深度节奏能存进配置再读回来",
            customRoundTrip is { ScreenSyncCustom: true, AudioCustom: true }
            && customRoundTrip.ScreenDepthEffective == 77
            && Math.Abs(customRoundTrip.AudioSpeedEffective - 1.7) < 1e-9);
        Check("生效值只是算出来的，不会写进配置文件",
            !JsonSerializer.Serialize(new AppSettings()).Contains("Effective"));
    }

    // ── 人声（呻吟）响应：用户报"对女性呻吟没反应" ─────────────────────
    {
        // 合成两段「呻吟」：女性（基频 ~300Hz）与男性（~120Hz），都带颤音 + 呼吸噪声，
        // 包络是慢起慢落（0.7Hz 起伏、每段 300ms 起音）—— 这正是老逻辑抓不到的那类声音。
        string femaleMoan = Path.Combine(Path.GetTempPath(), "hexa-moan-female.wav");
        string maleMoan = Path.Combine(Path.GetTempPath(), "hexa-moan-male.wav");
        string roomTone = Path.Combine(Path.GetTempPath(), "hexa-roomtone.wav");
        WriteVoiceWav(femaleMoan, 8.0, 300, withBreath: true);
        WriteVoiceWav(maleMoan, 8.0, 120, withBreath: true);
        WriteVoiceWav(roomTone, 3.0, 0, withBreath: true);   // 只有呼吸噪声、没有人声基频

        var femaleDetector = new AudioEventDetector();
        var maleDetector = new AudioEventDetector();
        var roomDetector = new AudioEventDetector();
        var femaleEvents = DetectEvents(femaleDetector, femaleMoan);
        var maleEvents = DetectEvents(maleDetector, maleMoan);
        var roomEvents = DetectEvents(roomDetector, roomTone);

        // 呻吟现在归属于**人声事件**（用户要的语义是"呻吟 = 抽插"），不再伪装成"渐强"。
        // 两类事件的分工：Voice = 中频（150–1200Hz）跃升；Swell = 宽带跃升 / 低频爬升。
        int femaleVoice = femaleEvents.Count(e => e.Kind == MotionEventKind.Voice);
        int maleVoice = maleEvents.Count(e => e.Kind == MotionEventKind.Voice);
        int roomVoice = roomEvents.Count(e => e.Kind == MotionEventKind.Voice);
        Check($"女性呻吟被认成「人声」事件（{femaleVoice} 次）", femaleVoice >= 2);
        Check($"男性呻吟也被认成「人声」事件（{maleVoice} 次）", maleVoice >= 2);
        Check($"只有呼吸噪声时不乱报人声（{roomVoice} 次）", roomVoice == 0);
        Check("呻吟不再被当成「渐强」（人声从渐强里分出来了）",
            femaleEvents.Count(e => e.Kind == MotionEventKind.Swell) < femaleVoice
            && maleEvents.Count(e => e.Kind == MotionEventKind.Swell) < maleVoice);
        Console.WriteLine($"     voice: 女声 {femaleEvents.Count} 事件（人声 {femaleVoice}）· " +
                          $"男声 {maleEvents.Count}（人声 {maleVoice}）· 环境噪声 {roomEvents.Count}（人声 {roomVoice}）");

        // ── 两个模式互不串味：「角色声音」该只认人声频段，「音乐律动」该只认鼓点/低音 ──
        // 用同一段合成素材验证：300Hz 的女声 vs 60Hz 的纯低音。
        // （60Hz 落在 150Hz 高通之下，人声频段几乎拿不到能量；这正是"放音乐时不该乱插"的基础。）
        string bassyMusic = Path.Combine(Path.GetTempPath(), "hexa-bass-music.wav");
        WriteSineWav(bassyMusic, 8.0, 55);   // 纯 55Hz 低音：不给人声频段留任何能量
        var voiceDetector = new AudioEventDetector();
        var musicDetector = new AudioEventDetector();
        double moanVoiceLevel = PeakVoiceLevel(voiceDetector, femaleMoan);
        double bassVoiceLevel = PeakVoiceLevel(musicDetector, bassyMusic);
        Check($"呻吟素材把「人声电平」推得很高（{moanVoiceLevel:0.00}）", moanVoiceLevel > 0.30);
        Check($"纯低音（55Hz）不会推高「人声电平」（{bassVoiceLevel:0.00}，远低于呻吟）",
            bassVoiceLevel < moanVoiceLevel * 0.3);
        File.Delete(bassyMusic);

        File.Delete(femaleMoan); File.Delete(maleMoan); File.Delete(roomTone);
    }

    // ── 延迟标定：估算"设备到位要多久"并给出建议补偿 ─────────────────────
    {
        double fastTravel = LatencyCalibrator.EstimateDefaultTravelMs(ComfortProfile.Immersive);
        double slowTravel = LatencyCalibrator.EstimateDefaultTravelMs(ComfortProfile.Gentle);
        double shortTravel = LatencyCalibrator.EstimateTravelMs(50, 60, ComfortProfile.Immersive);   // 只走 10 个点
        Check("latency estimate grows when the comfort profile is slower",
            slowTravel > fastTravel && fastTravel > 0);
        Check("latency estimate grows with distance", shortTravel < fastTravel);
        Check("latency estimate includes the device interpolation time",
            LatencyCalibrator.EstimateDefaultTravelMs(ComfortProfile.Immersive, 0, 60) - fastTravel is > 59 and < 61);
        Check("latency estimate is never faster on secondary axes",
            LatencyCalibrator.EstimateDefaultTravelMs(ComfortProfile.Immersive, 2) > fastTravel
            && LatencyCalibrator.EstimateDefaultTravelMs(ComfortProfile.Immersive, 5) >= fastTravel);
        Check("latency suggestion stays inside the setting range",
            LatencyCalibrator.SuggestCompensationMs(fastTravel) is >= LatencyCalibrator.MinCompensationMs
                and <= LatencyCalibrator.MaxCompensationMs
            && LatencyCalibrator.SuggestCompensationMs(0) == 0);

        // 估算必须和「手工用限速器跑一遍」一致（误差 1 帧以内）：证明它用的就是设备真正走的曲线
        for (int axis = 0; axis < 6; axis++)
        {
            var manual = new MotionSafetyLimiter();
            var from = new double[6];
            var to = new double[6];
            for (int i = 0; i < 6; i++) { from[i] = 50; to[i] = 50; }
            from[axis] = LatencyCalibrator.DefaultTravelFrom;
            to[axis] = LatencyCalibrator.DefaultTravelTo;
            manual.Reset(from);
            double threshold = LatencyCalibrator.DefaultTravelFrom
                + (LatencyCalibrator.DefaultTravelTo - LatencyCalibrator.DefaultTravelFrom) * 0.9;
            double elapsed = 0;
            for (int i = 0; i < 2000; i++)
            {
                double value = manual.Step(to, 0.016, ComfortProfile.Immersive)[axis];
                elapsed += 16;
                if (value >= threshold) break;
            }
            double estimate = LatencyCalibrator.EstimateDefaultTravelMs(ComfortProfile.Immersive, axis);
            Check($"latency estimate matches the real limiter (axis {axis})", Math.Abs(estimate - elapsed) <= 16.01);
        }

        var taps = new List<double> { 118, 122, 95, 121, 119 };
        var (offset, spread) = LatencyCalibrator.EstimateFromTaps(taps);
        Check("tap calibration takes the median and reports spread",
            offset is >= 118 and <= 122 && spread is > 25 and < 30);
        Check("tap calibration survives empty input",
            LatencyCalibrator.EstimateFromTaps([]) == (0, 0));
    }

    // 急停锁存不能被"连接事件"清掉：用户按了急停之后设备掉线又自动重连（或一次串口写失败触发重连），
    // 如果重连把锁存清了并解锁输出，自动动作会自己开跑 —— 用户没碰任何按钮，机器却动了。
    // （用独立的一对 service/engine：切换模拟模式会清空指令历史，不能污染上面的用例。）
    {
        var latchCfg = new AppSettings { SimulationMode = true };
        using var latchSerial = new SerialService();
        latchSerial.SetSimulationMode(true);
        using var latchEngine = new MotionEngine(latchSerial, latchCfg);
        latchEngine.EmergencyStop();
        latchSerial.SetSimulationMode(false);
        latchSerial.SetSimulationMode(true);
        Check("reconnect does not clear the emergency latch",
            latchEngine.EmergencyStopped
            && !latchEngine.TrySendDirectAxes([50, 50, 50, 50, 50, 50]));
    }

    // 轴限位总开关：关着走全程，开着才按存的范围；存的数值不因开关丢失。
    {
        var limCfg = new AppSettings();
        limCfg.AxisMin["L0"] = 3000; limCfg.AxisMax["L0"] = 8000;
        Check("axis limits are off by default and output uses full range",
            !limCfg.AxisLimitsEnabled
            && TCodeFrameFormatter.MapPosition("L0", 100, limCfg.EffectiveAxisMin, limCfg.EffectiveAxisMax) == 9999
            && TCodeFrameFormatter.MapPosition("L0", 0, limCfg.EffectiveAxisMin, limCfg.EffectiveAxisMax) == 0);
        limCfg.AxisLimitsEnabled = true;
        Check("axis limits switch on applies the saved range",
            TCodeFrameFormatter.MapPosition("L0", 100, limCfg.EffectiveAxisMin, limCfg.EffectiveAxisMax) == 8000
            && TCodeFrameFormatter.MapPosition("L0", 0, limCfg.EffectiveAxisMin, limCfg.EffectiveAxisMax) == 3000);
        limCfg.AxisLimitsEnabled = false;
        Check("axis limits switch off keeps the saved values",
            limCfg.AxisMin["L0"] == 3000 && limCfg.AxisMax["L0"] == 8000);
        string limJson = System.Text.Json.JsonSerializer.Serialize(limCfg);
        Check("axis limits switch persists and effective tables are not serialized",
            limJson.Contains("\"AxisLimitsEnabled\":false") && !limJson.Contains("EffectiveAxis"));
    }

    // 急停不能被调度锁卡住：别的线程正占着 _dispatchLock 时按急停，必须立刻锁存并发出 DSTOP。
    {
        var esCfg = new AppSettings { SimulationMode = true };
        using var esSerial = new SerialService();
        esSerial.SetSimulationMode(true);
        using var esEngine = new MotionEngine(esSerial, esCfg);
        esSerial.SetSimulationMode(false);
        esSerial.SetSimulationMode(true);
        var dispatchLock = typeof(MotionEngine)
            .GetField("_dispatchLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(esEngine)!;
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() => { lock (dispatchLock) { held.Set(); release.Wait(5000); } }) { IsBackground = true };
        holder.Start();
        held.Wait(2000);
        int esBefore = esSerial.SimulationCommands.Count;
        var esTask = Task.Run(esEngine.EmergencyStop);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000
               && !(esEngine.EmergencyStopped && esSerial.SimulationCommands.Skip(esBefore).Contains("DSTOP")))
            Thread.Sleep(5);
        bool fast = esEngine.EmergencyStopped && esSerial.SimulationCommands.Skip(esBefore).Contains("DSTOP");
        Console.WriteLine($"     estop under held lock: {sw.ElapsedMilliseconds}ms latched={esEngine.EmergencyStopped}");
        Check("emergency stop latches and sends DSTOP while the dispatch lock is held elsewhere",
            fast && sw.ElapsedMilliseconds < 200 && !esEngine.TrySendDirectAxes([50, 50, 50, 50, 50, 50]));
        release.Set();
        Check("emergency stop finishes its cleanup once the lock frees", esTask.Wait(3000));
    }

    // ── 设置页「轴限位实时驱动」这条链路（用户报"拖到最大机器居中不动"）──
    {
        var calCfg = new AppSettings { SimulationMode = true };
        using var calSerial = new SerialService();
        calSerial.SetSimulationMode(true);
        using var calEngine = new MotionEngine(calSerial, calCfg);

        // 输出锁定（刚构造完就是这个状态）时不能报"已发送"：必须如实返回 false。
        Check("axis-limit live drive reports failure while the output is locked",
            !calEngine.TryMoveCalibrationAxis("L0", 9999, 150));

        // 解锁输出（等价于"设备已连接"）：走一遍真实的连接事件
        calSerial.SetSimulationMode(false);
        calSerial.SetSimulationMode(true);
        int before = calSerial.SimulationCommands.Count;
        bool ok = calEngine.TryMoveCalibrationAxis("L0", 9999, 150);
        string last = calSerial.SimulationCommands.Count > before ? calSerial.SimulationCommands[^1] : "(没发出任何指令)";
        Console.WriteLine($"     calib: ok={ok} 指令={last}");
        Check("axis-limit live drive sends the raw position to the device",
            ok && string.Equals(last, "L09999I250", StringComparison.Ordinal));
        Check("axis-limit live drive updates the engine output state so nothing pulls it back",
            Math.Abs(calEngine.GetLastOutputSnapshot()[0] - 100) < 0.5);

        // 规则引擎接管时必须拒绝（设置页会先把它停掉），不能"看起来发了其实没发"
        calEngine.RuleEngineActive = true;
        bool refused = calEngine.TryMoveCalibrationAxis("L0", 0, 150);
        Check("axis-limit live drive is refused while the rule engine owns the output", !refused);
        calEngine.RuleEngineActive = false;

        // 急停锁定期间必须拒绝
        calEngine.EmergencyStop();
        Check("axis-limit live drive is refused while emergency-stopped",
            !calEngine.TryMoveCalibrationAxis("L0", 9999, 150));
    }

    // ── 输入融合：氛围叠加层（精确动作 + 轻微动）─────────────────────────
    {
        var ambientCfg = new AppSettings { SimulationMode = true, ComfortProfile = "immersive" };
        using var ambientSerial = new SerialService();
        ambientSerial.SetSimulationMode(true);
        using var ambientEngine = new MotionEngine(ambientSerial, ambientCfg);
        ambientSerial.OutputEnabled = true;   // 同上一节：解锁输出闸门，等价于真机连上后的状态
        double[] direct = [70, 50, 50, 50, 50, 50];

        // 用「次要轴偏离 50 的均方根」当指标：限速器本身在目标附近有余摆，
        // 单看某一帧会被余摆带偏，均方根才能稳定反映"有没有在叠微动"。
        double SecondaryRms(int frames)
        {
            double sum = 0;
            for (int i = 0; i < frames; i++)
            {
                ambientEngine.TrySendDirectAxes(direct);
                double[] output = ambientEngine.GetLastOutputSnapshot();
                for (int axis = 1; axis < 6; axis++) sum += (output[axis] - 50) * (output[axis] - 50);
            }
            return Math.Sqrt(sum / (frames * 5.0));
        }

        // ① 关掉叠加：基线（把主轴先走到位，余摆先摇完）
        ambientCfg.AmbientOverlay = false;
        SecondaryRms(120);
        double baselineRms = SecondaryRms(120);
        double[] calm = ambientEngine.GetLastOutputSnapshot();
        Check("direct input keeps the main axis exactly where it was asked", Math.Abs(calm[0] - 70) < 0.5);

        // ② 打开叠加 + 有声音：次要轴明显动起来（主轴一动不动）
        ambientCfg.AmbientOverlay = true;
        ambientCfg.AmbientOverlayAmount = 30;
        ambientEngine.SetAudioFeatures(1.0, 1.0, 1.0);
        double overlayRms = SecondaryRms(120);
        double[] active = ambientEngine.GetLastOutputSnapshot();
        Check("ambient overlay never touches the main axis", Math.Abs(active[0] - 70) < 0.5);
        Check("ambient overlay adds motion on the secondary axes",
            overlayRms > Math.Max(baselineRms * 3, 3.0));
        Check("ambient overlay stays inside 0-100", active.All(value => value is >= 0 and <= 100));

        // ③ 声音停了：叠加必须停（回到基线水平），不能"声音早停了设备还在呼吸"
        ambientEngine.SetAudioFeatures(0, 0, 0);
        SecondaryRms(120);
        double quietRms = SecondaryRms(120);
        // 用"相对叠加开着时"的下降幅度判断，而不是跟基线绝对值比：
        // 限速器在目标附近本身会有余摆（既有特性），绝对 RMS 归不了零。
        Check("ambient overlay stops when the sound stops", quietRms < overlayRms * 0.6);

        // ④ 关掉「多轴动作」总开关：门控说了算，氛围层不能把它顶开
        ambientEngine.SetAudioFeatures(1.0, 1.0, 1.0);
        ambientCfg.MotionMultiAxis = false;
        SecondaryRms(120);
        double gatedRms = SecondaryRms(120);
        Check("ambient overlay cannot bypass the multi-axis gate", gatedRms < baselineRms * 1.5 + 1.0);
        ambientCfg.MotionMultiAxis = true;
    }

    // ── 声音事件语汇（动作翻译的纯函数）───────────────────────────────
    double[] noEventPose = MotionVocabulary.Render(MotionEventKind.None, 1.0, 0.5, 40, true);
    Check("motion vocabulary: no event means all axes at center",
        noEventPose.All(value => Math.Abs(value - 50) < 1e-9));

    double[] impactRise = MotionVocabulary.Render(MotionEventKind.Impact, 1.0, 0.15, 40, true);
    double[] impactEnd = MotionVocabulary.Render(MotionEventKind.Impact, 1.0, 1.0, 40, true);
    Check("motion vocabulary: impact has attack and decays back to center",
        impactRise[0] > 70 && Math.Abs(impactEnd[0] - 50) < 0.01);

    double[] singleAxisImpact = MotionVocabulary.Render(MotionEventKind.Impact, 1.0, 0.15, 40, false);
    Check("motion vocabulary: multi-axis off keeps every other axis at center",
        Math.Abs(singleAxisImpact[0] - impactRise[0]) < 1e-9
        && singleAxisImpact.Skip(1).All(value => Math.Abs(value - 50) < 1e-9));

    Check("motion vocabulary: multi-axis adds twist/tilt on secondary axes",
        impactRise.Skip(1).Any(value => Math.Abs(value - 50) > 1.0));

    double[] swellPeak = MotionVocabulary.Render(MotionEventKind.Swell, 1.0, 0.25, 40, true);
    Check("motion vocabulary: swell peaks a quarter into the cycle", swellPeak[0] > 80);

    // 强度/相位/幅度都可能被界面传成越界值：落点必须永远在 0–100 内
    bool vocabularyInRange = true;
    foreach (MotionEventKind kind in Enum.GetValues<MotionEventKind>())
    {
        for (double phase = -0.5; phase <= 1.5; phase += 0.05)
        {
            double[] pose = MotionVocabulary.Render(kind, 2.0, phase, 200, true);
            if (pose.Length != 6 || pose.Any(value => value < 0 || value > 100)) vocabularyInRange = false;
        }
    }
    Check("motion vocabulary: clamps extreme input into 0-100", vocabularyInRange);
    Check("motion vocabulary: durations feel right (beat < impact < swell)",
        MotionVocabulary.DurationSeconds(MotionEventKind.Beat, 1.0) < MotionVocabulary.DurationSeconds(MotionEventKind.Impact, 1.0)
        && MotionVocabulary.DurationSeconds(MotionEventKind.Impact, 1.0) < MotionVocabulary.DurationSeconds(MotionEventKind.Swell, 1.0));

    // ── 每个游戏一套配置（进游戏按进程名套用）─────────────────────────
    var profileSettings = new AppSettings();
    profileSettings.GameProfiles["game-a"] = new GameProfile { Process = "game-a", Mode = "game_flow", Intensity = 1.4 };
    profileSettings.GameProfiles["game-b"] = new GameProfile { Process = "game-b", Mode = "gentle_wave", Intensity = 0.7 };
    profileSettings.Normalize();
    AppSettings? profileRoundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(profileSettings));
    profileRoundTrip!.Normalize();
    Check("per-game profiles survive a settings round trip",
        profileRoundTrip.GameProfiles.Count == 2
        && Math.Abs(profileRoundTrip.GameProfiles["game-a"].Intensity - 1.4) < 1e-9
        && profileRoundTrip.GameProfiles["game-a"].Mode == "game_flow"
        && profileRoundTrip.GameProfiles["game-b"].Mode == "gentle_wave");

    var messyProfiles = new AppSettings();
    messyProfiles.GameProfiles["big"] = new GameProfile { Intensity = 9.0, Mode = "" };
    messyProfiles.GameProfiles["  "] = new GameProfile { Intensity = 1.0 };
    messyProfiles.Normalize();
    Check("per-game profile is normalized (clamped intensity, default mode, blank key dropped)",
        messyProfiles.GameProfiles.Count == 1
        && Math.Abs(messyProfiles.GameProfiles["big"].Intensity - 2.0) < 1e-9
        && messyProfiles.GameProfiles["big"].Mode == "organic_flow");

    // ── 从音频/视频离线生成脚本（听歌写脚本）──────────────────────────
    string audioScriptDir = Path.Combine(Path.GetTempPath(), "hexa_audioscript_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(audioScriptDir);
    try
    {
        string beatsWav = Path.Combine(audioScriptDir, "beats.wav");
        WriteTestWav(beatsWav, 6.0, percussive: true);
        string silentWav = Path.Combine(audioScriptDir, "silent.wav");
        WriteTestWav(silentWav, 2.0, percussive: false);

        AudioScriptResult script = AudioScriptGenerator
            .GenerateAsync(beatsWav, new AudioScriptOptions(70, 1.0, 90, MultiAxis: true))
            .GetAwaiter().GetResult();

        bool sorted = true, inRange = true;
        for (int i = 0; i < script.MainTrack.Count; i++)
        {
            (long at, int pos) = script.MainTrack[i];
            if (pos is < 0 or > 100 || at < 0 || at > script.DurationMs) inRange = false;
            if (i > 0 && at <= script.MainTrack[i - 1].At) sorted = false;
        }
        Check("audio script: produces sorted, in-range actions",
            script.MainTrack.Count >= 4 && sorted && inRange && script.StrokeCount > 0);
        Check("audio script: multi-axis adds a twist track on the same timeline",
            script.TwistTrack is { Count: > 0 } && script.TwistTrack.Count == script.MainTrack.Count);
        Check("audio script: summary mentions what was found",
            script.Summary.Contains("动作") && script.DurationMs > 4000);

        var exportedTracks = WaveScriptCodec.ParseFunscriptTracks(AudioScriptGenerator.ToFunscriptJson(script));
        Check("audio script: exported json round-trips through the funscript parser",
            exportedTracks.ContainsKey("L0") && exportedTracks["L0"].Count >= 4 && exportedTracks.ContainsKey("R0"));

        string silentError = "";
        try
        {
            AudioScriptGenerator.GenerateAsync(silentWav, new AudioScriptOptions(55, 1.0, 90, false)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            silentError = ex.Message;
        }
        Check("audio script: silent input explains itself instead of writing an empty script",
            silentError.Contains("没有检测到") || silentError.Contains("事件"));
    }
    finally
    {
        try { Directory.Delete(audioScriptDir, true); } catch { }
    }

    // ── 手画波形的自动草稿（退出保存 / 启动恢复）─────────────────────
    {
        string draftDir = Path.Combine(Path.GetTempPath(), "hexa_wavedraft_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(draftDir);
        string? previousSettings = Environment.GetEnvironmentVariable("HEXA_SETTINGS_PATH");
        Environment.SetEnvironmentVariable("HEXA_SETTINGS_PATH", Path.Combine(draftDir, "settings.json"));
        try
        {
            var draftWaves = new Dictionary<string, List<(double X, double Y)>>
            {
                ["L0"] = [(10, 20), (300, 100), (640, 30)],
                ["R1"] = [(0, 240), (650, 0)],
            };
            // 启用状态/平滑插值也要一起存：只存波形的话重启后六轴全禁用，播放只输出中位 50
            bool[] enabled = [true, false, false, false, true, false];
            WaveDraftStore.Save(new WaveDraft(enabled, 3.5, false, draftWaves));
            var loadedDraft = WaveDraftStore.Load();
            Check("wave draft round trip keeps points, cycle, axis enabled and smooth flag",
                loadedDraft is { } d
                && Math.Abs(d.CycleLen - 3.5) < 1e-9
                && !d.UseSmooth
                && d.AxisEnabled.Length == 6
                && d.AxisEnabled[0] && !d.AxisEnabled[1] && !d.AxisEnabled[2]
                && !d.AxisEnabled[3] && d.AxisEnabled[4] && !d.AxisEnabled[5]
                && d.Waves["L0"].Count == 3
                && d.Waves["R1"].Count == 2);

            // 六轴都空 → 草稿必须删掉，否则清空过的波形会在下次启动「复活」
            WaveDraftStore.Save(new WaveDraft(new bool[6], 2.0, true,
                new Dictionary<string, List<(double X, double Y)>> { ["L0"] = [] }));
            Check("wave draft is removed when every axis is empty", WaveDraftStore.Load() is null);

            // 损坏的草稿不能让启动失败，也不该每次启动都报错
            File.WriteAllText(Path.Combine(draftDir, "wave-draft.json"), "{ not json");
            Check("broken wave draft is discarded safely", WaveDraftStore.Load() is null
                && !File.Exists(Path.Combine(draftDir, "wave-draft.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEXA_SETTINGS_PATH", previousSettings);
            try { Directory.Delete(draftDir, true); } catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  SR6 机械模型运动学（与 osr-emu 官方 JS 实现逐部件比对）
    //  黄金数据由 node 跑 osr-emu 源码生成：10 组轴值 × 25 个部件的
    //  局部位置 + 四元数。移植版必须逐部件复现，否则模型姿态就“不像机器”。
    // ══════════════════════════════════════════════════════════════
    {
        Exception? sr6Failure = null;
        double maxPositionError = 0;
        double maxRotationError = 0;
        string worstPosition = "";
        string worstRotation = "";
        double perUpdateMs = 0;
        int triangleCount = 0;
        int comparedParts = 0;
        bool allPartsWithinTolerance = true;
        double receiverTravel = 0;

        var sr6Thread = new System.Threading.Thread(() =>
        {
            try
            {
                var cases = new (string Name, double[] Axes)[]
                {
                    ("home",     [0.5,  0.5,  0.5,  0.5,  0.5,  0.5]),
                    ("zero",     [0.0,  0.0,  0.0,  0.0,  0.0,  0.0]),
                    ("one",      [1.0,  1.0,  1.0,  1.0,  1.0,  1.0]),
                    ("stroke",   [0.85, 0.5,  0.5,  0.5,  0.5,  0.5]),
                    ("mixed",    [0.25, 0.7,  0.3,  0.9,  0.2,  0.65]),
                    ("pitchMax", [0.5,  0.5,  0.5,  0.5,  0.5,  1.0]),
                    ("twistMax", [0.5,  0.5,  0.5,  1.0,  0.5,  0.5]),
                    ("swayMax",  [0.5,  0.5,  1.0,  0.5,  0.5,  0.5]),
                    ("rollMax",  [0.5,  0.5,  0.5,  0.5,  0.9,  0.5]),
                    ("fwdMax",   [0.5,  1.0,  0.5,  0.5,  0.5,  0.5]),
                };
                var scale = new double[] { 1, 1, 1, 1, 1, 1 };

                string json;
                using (Stream? stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Hexa.CoreTests.sr6_golden.json"))
                {
                    if (stream is null) throw new InvalidOperationException("缺少 sr6_golden.json 嵌入资源");
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
                using var doc = JsonDocument.Parse(json);
                JsonElement goldenCases = doc.RootElement.GetProperty("cases");

                var rig = new Hexa.Services.Sr6.Sr6Rig();
                System.Windows.Media.Media3D.Vector3D? receiverAtZero = null;

                foreach ((string caseName, double[] axes) in cases)
                {
                    rig.Update(axes, scale);
                    JsonElement expected = goldenCases.GetProperty(caseName);

                    foreach (JsonProperty part in expected.EnumerateObject())
                    {
                        if (!rig.Parts.TryGetValue(part.Name, out Hexa.Services.Sr6.Sr6Part? actual))
                        {
                            allPartsWithinTolerance = false;
                            continue;
                        }

                        JsonElement p = part.Value.GetProperty("p");
                        double dx = actual.Position.X - p[0].GetDouble();
                        double dy = actual.Position.Y - p[1].GetDouble();
                        double dz = actual.Position.Z - p[2].GetDouble();
                        double positionError = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (positionError > maxPositionError) { maxPositionError = positionError; worstPosition = $"{caseName}/{part.Name}"; }

                        JsonElement q = part.Value.GetProperty("q");
                        double dot = Math.Abs(
                            actual.Rotation.X * q[0].GetDouble() +
                            actual.Rotation.Y * q[1].GetDouble() +
                            actual.Rotation.Z * q[2].GetDouble() +
                            actual.Rotation.W * q[3].GetDouble());
                        if (1.0 - dot > maxRotationError) { maxRotationError = 1.0 - dot; worstRotation = $"{caseName}/{part.Name}"; }
                        comparedParts++;
                    }

                    if (caseName == "zero")
                    {
                        receiverAtZero = new System.Windows.Media.Media3D.Vector3D(
                            rig.Parts["receiver"].Position.X,
                            rig.Parts["receiver"].Position.Y,
                            rig.Parts["receiver"].Position.Z);
                    }
                }

                // 位置误差 < 0.05mm、四元数 1-|dot| < 1e-4（约 0.03°），远超肉眼可见范围
                allPartsWithinTolerance = allPartsWithinTolerance
                    && comparedParts >= 250
                    && maxPositionError < 0.05
                    && maxRotationError < 1e-4;

                // 性能护栏：预览每 120ms 刷一次，运动学必须远快于这个节奏
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++)
                    rig.Update(cases[i % cases.Length].Axes, scale);
                stopwatch.Stop();
                perUpdateMs = stopwatch.Elapsed.TotalMilliseconds / 1000.0;
                foreach (Hexa.Services.Sr6.Sr6Part part in rig.Parts.Values)
                {
                    foreach (System.Windows.Media.Media3D.GeometryModel3D mesh in part.Meshes)
                    {
                        if (mesh.Geometry is System.Windows.Media.Media3D.MeshGeometry3D geometry)
                            triangleCount += geometry.TriangleIndices.Count / 3;
                    }
                }

                // 反向自检：轴值确实驱动了机构（否则上面可能是在比对两个静止模型）
                // L0 从 0 走到 1（其余轴中位）时接收器走过的距离（模型里行程方向是 Y 轴）
                rig.Update([0, 0.5, 0.5, 0.5, 0.5, 0.5], scale);
                var low = rig.Parts["receiver"].Position;
                rig.Update([1, 0.5, 0.5, 0.5, 0.5, 0.5], scale);
                var high = rig.Parts["receiver"].Position;
                receiverTravel = (high - low).Length();
            }
            catch (Exception ex)
            {
                sr6Failure = ex;
            }
        });
        sr6Thread.SetApartmentState(System.Threading.ApartmentState.STA);
        sr6Thread.Start();
        sr6Thread.Join();

        if (sr6Failure != null)
        {
            Console.WriteLine($"FAIL sr6 kinematics: {sr6Failure.Message}");
            failed++;
        }
        else
        {
            Check("sr6 model reproduces osr-emu kinematics for every part",
                allPartsWithinTolerance);
            Check("sr6 kinematics update is fast enough for a 120ms refresh", perUpdateMs < 1.0);
            Check("sr6 model parts are actually driven by the axes",
                receiverTravel > 50);
            Console.WriteLine(
                $"     sr6: {comparedParts} part poses compared, max position error {maxPositionError:F5}mm ({worstPosition}), " +
                $"max rotation error {maxRotationError:E2} ({worstRotation}), L0 travel {receiverTravel:F1}mm");
            Console.WriteLine(
                $"     sr6: 每次姿态更新 {perUpdateMs:F3} ms，模型共 {triangleCount} 个三角形");
        }
    }

    // ── 固件设置（TCodeESP32 的 #list-settings / #setting / $save / #restart）──────
    // 这条串口通道有副作用（会写设备 Flash），所以「哪些命令能发」必须钉死；
    // 解析用的样本照固件源码 SystemCommandHandler::formatCommand 的输出样子造。
    Check("固件设置：白名单只放行 读列表/改设置/保存/重启",
        SerialService.IsAllowedFirmwareCommand("#list-settings")
        && SerialService.IsAllowedFirmwareCommand("#help")
        && SerialService.IsAllowedFirmwareCommand("$save")
        && SerialService.IsAllowedFirmwareCommand("#restart")
        && SerialService.IsAllowedFirmwareCommand("#setting:LeftServo_ZERO:1520"));
    Check("固件设置：会动设备/会抢控制权的命令一律不放行",
        !SerialService.IsAllowedFirmwareCommand("#device-home")
        && !SerialService.IsAllowedFirmwareCommand("#motion-enable")
        && !SerialService.IsAllowedFirmwareCommand("#pause")
        && !SerialService.IsAllowedFirmwareCommand("L05000")
        && !SerialService.IsAllowedFirmwareCommand("DSTOP"));
    Check("固件设置：塞第二条命令/空值/带空格/超长值都被挡住",
        !SerialService.IsAllowedFirmwareCommand("#setting:LeftServo_ZERO")
        && !SerialService.IsAllowedFirmwareCommand("#setting::1520")
        && !SerialService.IsAllowedFirmwareCommand("#setting:LeftServo_ZERO:15 20")
        && !SerialService.IsAllowedFirmwareCommand("#setting:LeftServo_ZERO:1\nL05000")
        && !SerialService.IsAllowedFirmwareCommand("#setting:LeftServo_ZERO:" + new string('9', 65)));

    string firmwareListSample =
        "\n\n\nAvailable settings:\n\n"
        + "LeftServo_ZERO:<int>----------------------The zero calibration for the left servo\n"
        + "RightServo_ZERO:<int>---------------------The zero calibration for the right servo\n"
        + "inverseStroke:<bool/bit>------------------Inverts the stroke direction\n"
        + "friendlyName:<string>---------------------The friendly name shown on the web ui\n"
        + "maxServoRange:<int>-----------------------Max servo range\n"
        + "someDecimals:<double>---------------------A decimal setting\n"
        + "this line has no type token\n"
        + "LeftServo_ZERO:<int>----------------------duplicate, must not show up twice\n";
    var firmwareSettings = FirmwareSettingsService.ParseSettingList(firmwareListSample);
    Check($"固件设置：解析 #list-settings 输出（{firmwareSettings.Count} 项）",
        firmwareSettings.Count == 6
        && firmwareSettings[0].Name == "LeftServo_ZERO"
        && firmwareSettings[0].Kind == FirmwareSettingKind.Number
        && firmwareSettings.Any(s => s.Name == "inverseStroke" && s.Kind == FirmwareSettingKind.Boolean)
        && firmwareSettings.Any(s => s.Name == "friendlyName" && s.Kind == FirmwareSettingKind.Text)
        && firmwareSettings.Any(s => s.Name == "someDecimals" && s.Kind == FirmwareSettingKind.Decimal));

    var (firmwareCommon, firmwareOthers) = FirmwareSettingsService.SplitByCurated(firmwareSettings);
    Check("固件设置：SR6 常用项与其它项分开，常用项都带中文名",
        firmwareCommon.Count == 5 && firmwareOthers.Count == 1
        && firmwareCommon.All(s => FirmwareSettingsService.CuratedSr6Settings.ContainsKey(s.Name))
        && firmwareOthers.All(s => !FirmwareSettingsService.CuratedSr6Settings.ContainsKey(s.Name)));
    Check("固件设置：值类型校验挡住明显填错",
        FirmwareSettingsService.Validate(FirmwareSettingKind.Number, "1520") == null
        && FirmwareSettingsService.Validate(FirmwareSettingKind.Number, "15.2") != null
        && FirmwareSettingsService.Validate(FirmwareSettingKind.Boolean, "1") == null
        && FirmwareSettingsService.Validate(FirmwareSettingKind.Boolean, "开") != null
        && FirmwareSettingsService.Validate(FirmwareSettingKind.Text, "") != null);
    Check("固件设置：解析器对空/垃圾输入不炸也不瞎编",
        FirmwareSettingsService.ParseSettingList("").Count == 0
        && FirmwareSettingsService.ParseSettingList(null).Count == 0
        && FirmwareSettingsService.ParseSettingList("hello world\n\n").Count == 0);

    // ── 界面语言的词表（中文原文 → English）────────────────────────────────
    // 词表是运行时翻译（LocalizationService.Apply）的唯一真源：键必须逐字符等于界面上的原文，
    // 写错一条（多一个空格、抄错一个字）只会「静默不生效」，所以在单测里把结构和最关键的几条钉死。
    var uiTable = Hexa.Services.LocalizationTable.Entries;
    Check($"界面词表：键不重复（{uiTable.Length} 条）",
        uiTable.Select(entry => entry.Zh).Distinct(StringComparer.Ordinal).Count() == uiTable.Length);
    Check("界面词表：没有空键/空值",
        uiTable.All(entry => !string.IsNullOrWhiteSpace(entry.Zh) && !string.IsNullOrWhiteSpace(entry.En)));
    Check("界面词表：每个键都是界面上的中文原文（含汉字）",
        uiTable.All(entry => entry.Zh.Any(c => c is >= '\u4e00' and <= '\u9fff')));
    Check($"界面词表：第一版按页面覆盖到 200 条以上（实际 {uiTable.Length} 条）", uiTable.Length >= 200);
    Check("界面词表：查表用字典与词条一一对应",
        Hexa.Services.LocalizationTable.Map.Count == uiTable.Length);
    // T 是「动态文字」的唯一出口，从 2026-11-24 起跟随界面语言：中文界面必须原样返回中文，
    // 所以下面这条断言只在英文界面下成立 —— 先把语言切到英文，测完由后面的断言切回中文。
    Hexa.Services.LocalizationService.UiLanguage = Hexa.Services.LocalizationService.English;
    Check("界面词表：英文界面下 T(\"全部归中\") 返回英文",
        Hexa.Services.LocalizationService.T("全部归中") == "Center all");
    Check("界面词表：查不到的中文原样返回（绝不半截替换）",
        Hexa.Services.LocalizationService.T("这句话不在词表里") == "这句话不在词表里");
    Check("界面词表：英文侧没有漏翻的汉字",
        uiTable.All(entry => !entry.En.Any(c => c is >= '\u4e00' and <= '\u9fff')));
    Hexa.Services.LocalizationService.UiLanguage = Hexa.Services.LocalizationService.Chinese;
    Check("中文界面下 T() 原样返回中文（侧栏状态/弹窗不再变英文）",
        Hexa.Services.LocalizationService.T("全部归中") == "全部归中"
        && Hexa.Services.LocalizationService.T("待机") == "待机"
        && Hexa.Services.LocalizationService.T("已解锁") == "已解锁");

    Hexa.Services.LocalizationService.UiLanguage = "fr";
    bool languageFallback = Hexa.Services.LocalizationService.UiLanguage == Hexa.Services.LocalizationService.Chinese;
    Hexa.Services.LocalizationService.UiLanguage = Hexa.Services.LocalizationService.Chinese;
    Check("界面语言：运行时只认 zh / en，其它值一律当中文", languageFallback);

    var languageSettings = new Hexa.Models.AppSettings { UiLanguage = "EN" };
    languageSettings.Normalize();
    var brokenLanguageSettings = new Hexa.Models.AppSettings { UiLanguage = "fr" };
    brokenLanguageSettings.Normalize();
    Check("设置：界面语言白名单（\"EN\" → en，写坏的 → zh）",
        languageSettings.UiLanguage == "en" && brokenLanguageSettings.UiLanguage == "zh");

    // ── 链路存活监控（拿固件回显当 ACK）──────────────────────────────────
    // 这些判据的价值全在"保守"：把方向写反的代价是误锁输出 / 误断连，比漏报贵得多，
    // 所以边界（纯日志不算活、没写过帧不算死、banner 去重）逐条钉死在单测里。
    {
        var linkNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var monitor = new SerialLinkMonitor(() => linkNow);

        // ① 写过帧，但设备只吐纯日志（banner 里没有 Stats、也没有 uart 回显）：不能算活。
        monitor.NoteFrameWritten(6);
        monitor.Feed("I (123) uart: boot stage 3 done\r\nI (124) system: ready without executor\r\n");
        var logOnly = monitor.Snapshot();
        Check("链路：纯日志行不能判活（日志里没有 Stats 也没有 uart 回显）",
            !logOnly.IsAlive && !logOnly.HasEcho && logOnly.LastHeartbeatAtUtc is null);

        // ② 一行 Executor: Stats 就是心跳（固件每秒吐两行）。注意：刚写完帧、只有心跳没有回显时
        //    不算"已确认"，而是"有去无回"——心跳证明设备在跑，恰恰说明问题在我们这一侧。
        monitor.Feed("I (456) Executor: Stats [1.0s window] - Compute: 12.3us freq=50.00 Hz\n");
        var heartbeat = monitor.Snapshot();
        Check("链路：看到 Executor: Stats 判定心跳",
            heartbeat.HasHeartbeat && heartbeat.LastHeartbeatAtUtc == linkNow);
        Check("链路：写完帧只有心跳没有回显 → 判\"有去无回\"，不是已确认",
            !heartbeat.IsAlive && heartbeat.IsStalled
            && SerialLinkMonitor.Describe(heartbeat).Contains("先别拔线", StringComparison.Ordinal));

        // ③ 上电 banner 出现即说明板子重启过；同一次上电的多行（含两个标记）只算一次。
        monitor.Feed("I (1) main_task: app_main starting\r\nI (2) SR6Executor(0x3f) begin\r\n");
        var booted = monitor.Snapshot();
        linkNow = linkNow.AddSeconds(5);
        monitor.Feed("SR6Executor(0x3f) restart by watchdog\n");
        Check("链路：banner（app_main / SR6Executor(）出现即 ResetSeenCount+1，同一波只算一次",
            booted.ResetSeenCount == 1 && monitor.Snapshot().ResetSeenCount == 2);

        // ④ 从来没写过帧：无论过多久都不判死 —— 设备开机后安静是正常状态，不是故障。
        var quietNow = linkNow;
        var wedgedNow = new DateTime(2026, 10, 25, 23, 0, 0, DateTimeKind.Utc);
    var wedgedMonitor = new SerialLinkMonitor(() => wedgedNow);
    wedgedMonitor.NoteFrameWritten(40);
    wedgedMonitor.Feed("E (96167) task_wdt: Task watchdog got triggered. The following tasks/users did not reset the watchdog in time:\r\n");
    SerialLinkSnapshot wedgedSnapshot = wedgedMonitor.Snapshot();
    Check("watchdog spam with no heartbeat is reported as wedged",
        wedgedSnapshot.IsWedged && !wedgedSnapshot.HasHeartbeat);
    var healthyMonitor = new SerialLinkMonitor(() => wedgedNow);
    healthyMonitor.NoteFrameWritten(40);
    healthyMonitor.Feed("I (96167) Executor: Stats [1.0s window] - Compute: avg=0.6 ms\r\n");
    Check("heartbeat keeps a board out of the wedged state", !healthyMonitor.Snapshot().IsWedged);

    var quiet = new SerialLinkMonitor(() => quietNow);
        quietNow = quietNow.AddSeconds(30);
        quiet.Feed("I (9) just a log line\r\n");
        var quietSnapshot = quiet.Snapshot();
        Check("链路：从未写过帧时绝不判死（设备可能只是安静）",
            !quietSnapshot.IsDead && !quietSnapshot.IsAlive && quietSnapshot.FramesWritten == 0);
        Check("链路：文案对\"没写过帧\"直说待发帧，不吓唬人",
            SerialLinkMonitor.Describe(quietSnapshot).Contains("待发帧", StringComparison.Ordinal));

        // ⑦ 写完帧 + 收到 uart 回显 → 已确认，且回显比有值。
        var live = new SerialLinkMonitor(() => linkNow);
        live.NoteFrameWritten(6);
        live.Feed("I (7) uart: UART received 4 bytes: D1\r\nI (8) uart: UART echoed 6 bytes\r\n");
        var aliveSnapshot = live.Snapshot();
        Check("链路：写完帧并收到 uart 回显 → 已确认（EchoRatio 有值）",
            aliveSnapshot.IsAlive && aliveSnapshot.HasEcho && aliveSnapshot.EchoRatio > 0);

        // ⑧ 同一份现场往后推 5 秒仍无任何回话 → 判死；文案必须劝人「先别拔」，
        //    因为程序现在会自己硬复位（2026-11 改：以前叫用户拔线，拔了才恢复）。
        linkNow = linkNow.AddSeconds(5);
        var deadSnapshot = live.Snapshot();
        Check("链路：写完帧后 5 秒没有任何回话 → 判死（文案劝「先别拔」并说明会自动复位）",
            deadSnapshot.IsDead && !deadSnapshot.IsAlive
            && SerialLinkMonitor.Describe(deadSnapshot).Contains("先别拔线", StringComparison.Ordinal));

        // ⑨ 空闲：写过帧之后停手不动（超过写字窗口），心跳仍在 ⇒ 正常状态，绝不能提示拔线。
        var idleNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var idle = new SerialLinkMonitor(() => idleNow);
        idle.NoteFrameWritten(6);
        idle.Feed("I (1) uart: UART echoed 6 bytes\n");
        idleNow = idleNow.AddSeconds(2);
        idle.Feed("I (2) Executor: Stats [1.0s window] - Compute: 12.3us freq=50.00 Hz\n");
        var idleSnapshot2 = idle.Snapshot();
        Check("链路：停手不动但心跳还在 → 空闲正常，文案里不许出现\"拔插\"",
            idleSnapshot2.IsIdle && !idleSnapshot2.IsDead && !idleSnapshot2.IsAlive
            && SerialLinkMonitor.Describe(idleSnapshot2).Contains("空闲", StringComparison.Ordinal)
            && !SerialLinkMonitor.Describe(idleSnapshot2).Contains("拔插", StringComparison.Ordinal));

        // ⑩ 三个状态互斥：空闲 / 有去无回 / 已确认 不可能同时成立。
        Check("链路：空闲与有去无回、已确认三者互斥",
            !(idleSnapshot2.IsIdle && idleSnapshot2.IsStalled) && !(idleSnapshot2.IsIdle && idleSnapshot2.IsAlive)
            && !(aliveSnapshot.IsAlive && aliveSnapshot.IsStalled) && !(heartbeat.IsStalled && heartbeat.IsAlive));
    }

    // ── 黑匣子（定容环形 + 故障包导出）───────────────────────────────────
    // 环形的意义是"上限即内存上限"：写 1000 条之后必须只剩最新 800 条，且每条都被截断。
    {
        var recordClock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var blackBox = new BlackBoxRecorder(() => recordClock);
        for (int i = 0; i < 1000; i++)
        {
            recordClock = recordClock.AddMilliseconds(10);
            // 每条塞 200 个字符：验证"截断到 120"，否则一条 banner 就能挤掉整圈有效信息。
            blackBox.Record(i % 2 == 0 ? "out" : "in", $"line-{i} {new string('x', 200)}");
        }

        var kept = blackBox.Snapshot();
        Check($"黑匣子：超容量后只保留最新 {BlackBoxRecorder.Capacity} 条、总条数不超上限",
            kept.Count == BlackBoxRecorder.Capacity
            && blackBox.TotalWritten == 1000
            && kept[0].Text.StartsWith("line-200 ", StringComparison.Ordinal)
            && kept[^1].Text.StartsWith("line-999 ", StringComparison.Ordinal)
            && kept.All(entry => entry.Text.Length == BlackBoxRecorder.MaxTextLength));

        string exportPath = Path.Combine(Path.GetTempPath(),
            "hexa-blackbox-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            string written = blackBox.Export(exportPath,
                [("端口", "COM7"), ("ResetSeenCount", "1"), ("EchoRatio", "1.87")]);
            string[] blackBoxLines = File.ReadAllLines(written);
            // 时间戳前缀形如 "2026-01-01 12:00:00.000"：长度与分隔符位置都钉住，避免导出一堆没有时间的行。
            static bool HasTimestampPrefix(string line)
                => line.Length > 24 && line[4] == '-' && line[7] == '-' && line[10] == ' '
                   && line[13] == ':' && line[16] == ':' && line[19] == '.';

            Check("黑匣子：导出文本含版本头 / 摘要 / 时间戳 / channel",
                blackBoxLines.Length > BlackBoxRecorder.Capacity
                && blackBoxLines[0] == "# " + BlackBoxRecorder.ExportVersion
                && blackBoxLines.Any(line => line.Contains("端口: COM7", StringComparison.Ordinal))
                && blackBoxLines.Any(line => line.Contains("EchoRatio: 1.87", StringComparison.Ordinal))
                && blackBoxLines.Any(line => line.Contains(" out   ", StringComparison.Ordinal))
                && blackBoxLines.Any(line => line.Contains(" in    ", StringComparison.Ordinal))
                && blackBoxLines.Count(HasTimestampPrefix) == BlackBoxRecorder.Capacity);
        }
        finally
        {
            try { if (File.Exists(exportPath)) File.Delete(exportPath); } catch { }
        }
    }

    // ── 方向感知：行剖面互相关的位移估计 ─────────────────────────────
    {
        static double[] Shifted(double[] src, double shift)
        {
            var dst = new double[src.Length];
            for (int y = 0; y < src.Length; y++)
            {
                double q = y - shift;
                int i0 = (int)Math.Floor(q);
                double frac = q - i0;
                double a = i0 >= 0 && i0 < src.Length ? src[i0] : 0;
                double b = i0 + 1 >= 0 && i0 + 1 < src.Length ? src[i0 + 1] : 0;
                dst[y] = a * (1 - frac) + b * frac;
            }
            return dst;
        }

        var profile = new double[90];
        for (int y = 0; y < profile.Length; y++)
            profile[y] = 1000 + 400 * Math.Sin(y * 0.37) + 150 * Math.Sin(y * 1.9 + 0.4);

        double down3 = FlowEstimator.VerticalShift(Shifted(profile, 3), profile);
        Check("方向：内容下移 3 像素 → +3（±0.5）", Math.Abs(down3 - 3) < 0.5);
        double up2 = FlowEstimator.VerticalShift(Shifted(profile, -2), profile);
        Check("方向：内容上移 2 像素 → -2（±0.5）", Math.Abs(up2 + 2) < 0.5);
        double sub = FlowEstimator.VerticalShift(Shifted(profile, 0.6), profile);
        Check("方向：亚像素位移也要报出来（0.6 → 0.6±0.4）", Math.Abs(sub - 0.6) < 0.4);
        var flat = new double[90];
        Array.Fill(flat, 512);
        Check("方向：纯色画面不瞎猜（判 0）", FlowEstimator.VerticalShift(flat, flat) == 0);
        double far = FlowEstimator.VerticalShift(Shifted(profile, 20), profile, maxShift: 6);
        Check("方向：超出搜索范围时夹在边界内", Math.Abs(far) <= 6.0 + 1e-9);

        // 方向量来自「主体在画面里变大还是变小」，不是整幅位移：人在固定镜头里动也算数。
        Check("方向：主体每秒涨 50% → 满幅靠近", Math.Abs(FlowEstimator.ApproachFromGrowth(0.5) - 1.0) < 1e-9);
        Check("方向：主体每秒涨 25% → 约四成靠近（扣掉死区）", Math.Abs(FlowEstimator.ApproachFromGrowth(0.25) - 0.43) < 0.03);
        Check("方向：主体在缩小 → 负数（退开）", FlowEstimator.ApproachFromGrowth(-0.3) < -0.4);
        Check("方向：包围盒抖出来的小变化走死区（不瞎偏）",
            FlowEstimator.ApproachFromGrowth(0.03) == 0 && FlowEstimator.ApproachFromGrowth(0.06) == 0);
        Check("方向：增长率离谱时夹在 ±1", FlowEstimator.ApproachFromGrowth(9.0) == 1.0
            && FlowEstimator.ApproachFromGrowth(-9.0) == -1.0);

        // 三档手感：同一条相位，形状参数不同，落点就该不同（光调频率分不开档位）。
        Check("抽插形状：相位 0 在最外面（0）", MotionVocabulary.ThrustWave(0, 0.45, 0.03) == 0);
        Check("抽插形状：推进结束落在最里面（1）", Math.Abs(MotionVocabulary.ThrustWave(0.45, 0.45, 0.03) - 1) < 1e-9);
        Check("抽插形状：到底停顿时保持满位", Math.Abs(MotionVocabulary.ThrustWave(0.465, 0.45, 0.03) - 1) < 1e-9);
        Check("抽插形状：到相位 1 回到最外面", Math.Abs(MotionVocabulary.ThrustWave(0.999999, 0.45, 0.03)) < 1e-3);
        Check("抽插形状：推进是单调的（不会来回抖）",
            MotionVocabulary.ThrustWave(0.1, 0.45, 0.03) < MotionVocabulary.ThrustWave(0.25, 0.45, 0.03)
            && MotionVocabulary.ThrustWave(0.25, 0.45, 0.03) < MotionVocabulary.ThrustWave(0.4, 0.45, 0.03));
        Check("抽插形状：同一相位下 动感档比舒缓档进得更深",
            MotionVocabulary.ThrustWave(0.12, 0.30, 0.0) > MotionVocabulary.ThrustWave(0.12, 0.52, 0.06));

        var gentleStroke = MotionVocabulary.FollowStroke(0.10, 40, 0.52, 0.06);
        var dynamicStroke = MotionVocabulary.FollowStroke(0.10, 40, 0.30, 0.0);
        Check("跟随落点：六轴齐全且都在 0–100", gentleStroke.Length == 6 && gentleStroke.All(v => v >= 0 && v <= 100));
        Check("跟随落点：副轴只轻微跟随（比主轴小）",
            Math.Abs(gentleStroke[3] - MotionVocabulary.Center) < Math.Abs(gentleStroke[0] - MotionVocabulary.Center));
        Check("跟随落点：关掉多轴就只有 L0 动",
            MotionVocabulary.FollowStroke(0.6, 40, 0.45, 0.03, multiAxis: false).Skip(1).All(v => v == MotionVocabulary.Center));
        Check("跟随落点：这一相位下动感档比舒缓档更深", dynamicStroke[0] > gentleStroke[0] + 2);

        // ── 姿态运动源：只有纯函数在这里被测（不跑模型、不起线程、不碰设备）──
        // 深度公式：髋中点 y 与参考线（踝中点，踝丢了用画面下缘）的比例 → 0..100 并夹取。
        PosePoint[] Pose(double hipY, double ankleY, int frameHeight = 90)
        {
            var k = new PosePoint[17];
            for (int i = 0; i < k.Length; i++) k[i] = new PosePoint(80, frameHeight * 0.4, 0.9);
            k[11] = new PosePoint(70, hipY, 0.9);
            k[12] = new PosePoint(90, hipY, 0.9);
            k[15] = new PosePoint(70, ankleY, 0.9);
            k[16] = new PosePoint(90, ankleY, 0.9);
            return k;
        }

        Check("姿态深度：站着（髋在踝上方一半）→ 50",
            Math.Abs(PoseMotionSource.DepthFromKeypoints(Pose(44.5, 89), 90) - 50) < 1e-9);
        Check("姿态深度：往下蹲（髋贴近踝）→ 明显更深（≈89.9）",
            Math.Abs(PoseMotionSource.DepthFromKeypoints(Pose(80, 89), 90) - 100.0 * (1 - 9.0 / 89)) < 1e-9);

        // 低置信度：踝丢掉 → 退回画面下缘当参考线（frameHeight-1 = 89）；髋全丢 → 不输出（NaN）。
        var lowAnkle = Pose(45, 90);
        lowAnkle[15] = new PosePoint(70, 90, 0.1);
        lowAnkle[16] = new PosePoint(90, 90, 0.1);
        var lowHip = Pose(45, 90);
        lowHip[11] = new PosePoint(70, 45, 0.29);
        lowHip[12] = new PosePoint(90, 45, 0.29);
        var oneHip = Pose(45, 90);
        oneHip[12] = new PosePoint(90, 10, 0.1);                  // 右髋不可信：必须只信左髋
        oneHip[11] = new PosePoint(70, 90, 0.9);
        Check("姿态深度：置信度 <0.3 的点一律丢掉（踝丢→用画面下缘 50.56；髋丢→NaN 不输出；只剩一边髋也照算）",
            Math.Abs(PoseMotionSource.DepthFromKeypoints(lowAnkle, 90) - 100.0 * (1 - 44.0 / 89)) < 1e-9
            && double.IsNaN(PoseMotionSource.DepthFromKeypoints(lowHip, 90))
            && !PoseMotionSource.TryDepthFromKeypoints(lowHip, 90, out _)
            && Math.Abs(PoseMotionSource.DepthFromKeypoints(oneHip, 90) - 100) < 1e-9);

        // 越界：髋沉到画面下方 → 100（不是 222），髋高到画面上方 → 0（不是负数）；数组不足 17 点 → 不输出。
        Check("姿态深度：越界一律夹在 0–100（髋在画面下 → 100，髋在画面上 → 0，关键点不齐 → NaN）",
            PoseMotionSource.DepthFromKeypoints(Pose(200, 90), 90) == 100
            && PoseMotionSource.DepthFromKeypoints(Pose(-50, 90), 90) == 0
            && double.IsNaN(PoseMotionSource.DepthFromKeypoints(new PosePoint[5], 90)));

        Check("姿态速度：位移 12 / 0.08 秒 = 150（双向同值），异常帧间隔（<0.02 或 >0.5）不冒充速度",
            Math.Abs(PoseMotionSource.SpeedFromDepths(40, 52, 0.08) - 150) < 1e-9
            && Math.Abs(PoseMotionSource.SpeedFromDepths(52, 40, 0.08) - 150) < 1e-9
            && PoseMotionSource.SpeedFromDepths(40, 52, 0.01) == 0
            && PoseMotionSource.SpeedFromDepths(40, 52, 1.0) == 0
            && PoseMotionSource.SpeedFromDepths(double.NaN, 52, 0.08) == 0
            && PoseMotionSource.SpeedToHz(150, 30) == 2.5);   // 5.2×150/(2π×30)≈4.14 → 封顶（=FollowHzCap）

        // 速度→频率（标定版）：hz = clamp(FollowHzGain × v / (2π×半幅), 0, FollowHzCap) = clamp(5.2·v/(2π·A), 0, 2.5)。
        // 增益 5.2 的标定依据：398 帧真实深蹲视频（pexels_4267356，160×90 分析图）实测 speed 中位 ≈43.2 深度单位/秒、
        // 该段半幅 ≈36 → 目标常规幅度动作落在 1Hz 中段：2π×36/43.2 ≈ 5.24 → 5.2（修前 hz 恒钉 0.5，std=0）。
        bool monotonic = true;
        double prevHz = -1;
        for (int v = 0; v <= 300; v += 5)
        {
            double hz = PoseMotionSource.SpeedToHz(v, 36);
            if (hz < prevHz) { monotonic = false; break; }
            prevHz = hz;
        }
        Check("姿态频率：speed→hz 单调不减、异常输入回 0、半幅越大频率越低",
            monotonic
            && PoseMotionSource.SpeedToHz(0, 36) == 0
            && PoseMotionSource.SpeedToHz(-5, 36) == 0
            && PoseMotionSource.SpeedToHz(double.NaN, 36) == 0
            && PoseMotionSource.SpeedToHz(87, 60) < PoseMotionSource.SpeedToHz(87, 36));
        double anchorHz = PoseMotionSource.SpeedToHz(43.2, 36);
        Check("姿态频率：标定锚点——实测中速 43.2 u/s@半幅36 ≈ 0.99Hz，落在常规动作 0.8–1.6 中段",
            Math.Abs(anchorHz - 5.2 * 43.2 / (Math.Tau * 36)) < 1e-9 && anchorHz > 0.8 && anchorHz < 1.6);
        Check("姿态频率：慢速贴 0.5 由调用方地板兜住、87 u/s@36≈2.0、110+ 封顶 FollowHzCap=2.5（不再全程钉 0.5）",
            PoseMotionSource.SpeedToHz(20, 36) < 0.5
            && PoseMotionSource.SpeedToHz(87, 36) >= 1.98 && PoseMotionSource.SpeedToHz(87, 36) <= 2.05
            && PoseMotionSource.SpeedToHz(110, 36) == 2.5    // 封顶 = FollowHzCap
            && PoseMotionSource.SpeedToHz(500, 36) == 2.5);

        // 幻觉踝防护：y 越出 [1, frameHeight-1] 的踝点 = 画面边缘/画面外的检测幻觉，整点丢弃、不进均值
        // （修前会把参考线拽到髋以上 → 深度假满 100；真实视频上实测约 0.4 秒）。
        var ghostAnkles = Pose(45, -20, 100);
        ghostAnkles[15] = new PosePoint(70, -20, 0.9);
        ghostAnkles[16] = new PosePoint(90, -101, 0.9);           // 两只全在画面外 → 参考线退回下缘 99
        var highEdge = Pose(45, 94, 100);
        highEdge[15] = new PosePoint(70, 0.5, 0.9);               // y<1：丢弃，只剩 y=94 的那只踝
        var lowEdge = Pose(45, -20, 100);
        lowEdge[15] = new PosePoint(70, -20, 0.9);
        lowEdge[16] = new PosePoint(90, 101, 0.9);                // 一上一下全越界 → 下缘 99（修前这个输入会假满 100）
        var bothIn = Pose(45, 94, 100);
        bothIn[16] = new PosePoint(90, 99, 0.9);                  // 94 与 99(=frameHeight-1) 都合法 → 均值 96.5
        var atFrameBottom = Pose(44, 90);                         // y=90==frameHeight：贴底幻觉 → 退回 89
        Check("姿态深度：画面外/贴边的幻觉踝整点丢弃，参考线只由画面内踝决定（边界 −20/−101/0.5/101/99/90==h 全覆盖）",
            Math.Abs(PoseMotionSource.DepthFromKeypoints(ghostAnkles, 100) - 100.0 * (1 - 54.0 / 99)) < 1e-9
            && Math.Abs(PoseMotionSource.DepthFromKeypoints(highEdge, 100) - 100.0 * (1 - 49.0 / 94)) < 1e-9
            && Math.Abs(PoseMotionSource.DepthFromKeypoints(lowEdge, 100) - 100.0 * (1 - 54.0 / 99)) < 1e-9
            && Math.Abs(PoseMotionSource.DepthFromKeypoints(bothIn, 100) - 100.0 * (1 - 51.5 / 96.5)) < 1e-9
            && Math.Abs(PoseMotionSource.DepthFromKeypoints(atFrameBottom, 90) - 100.0 * (1 - 45.0 / 89)) < 1e-9);

        // 关闭竞态：Submit 后立刻 Dispose（worker 可能正卡在推理/等待里），8 轮压力。
        // 修前缺陷：Join(500) 超时照样 _wake.Dispose()，worker 回到 _wake.WaitOne(200)（try 外）→
        // 未捕获 ObjectDisposedException 直接崩进程；修后循环顶查 _stop + WaitOne 挪进 try，确定性退出。
        bool disposeSafe = true;
        for (int cycle = 0; cycle < 8 && disposeSafe; cycle++)
        {
            var source = new PoseMotionSource();
            var noise = new byte[160 * 90 * 4];
            new Random(cycle).NextBytes(noise);
            source.Submit(noise, 160, 90);
            source.Dispose();
            for (int waited = 0; waited < 200 && source.IsRunning; waited++) Thread.Sleep(10);
            disposeSafe = !source.IsRunning
                && !source.Submit(noise, 160, 90)
                && (source.SampleCount > 0 || !source.TryRead(out _, out _));
        }
        Check("姿态运动：推理中途关闭不崩、线程确定性退出（8 轮 Submit→立即 Dispose 压力）", disposeSafe);

        // ── 姿态门控（画面同步的唯一回落点）：姿态有效 → 深度/速度 100% 来自姿态；──
        //    断掉但没超过保持期 → 原样保持最后一次有效值（不跳、不混画面分）；断超过保持期 → 才回落画面分。
        const long gateHoldMs = 1500;
        var gatePoseOk = PoseMotionSource.ResolveFollowSignal(true, 42.5, 13.7, 1000, 900, 80, 3, gateHoldMs);
        Check("姿态门控：姿态有效时深度与速度 100% 来自姿态（缓存里的画面值 80/3 一个都不掺）",
            gatePoseOk.Depth == 42.5 && gatePoseOk.Speed == 13.7 && !gatePoseOk.Fallback);

        // 保持窗口边界（空档 0 / 600 / 1499 毫秒）：输出必须与缓存值逐位相等（误差 0）。
        var hold0 = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 1000, 1000, 42.5, 13.7, gateHoldMs);
        var hold600 = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 1600, 1000, 42.5, 13.7, gateHoldMs);
        var hold1499 = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 2499, 1000, 42.5, 13.7, gateHoldMs);
        Check("姿态门控：空档 0 / 600 / 1499 毫秒都保持最后一次有效姿态值（深度 42.5、速度 13.7 逐位相等）",
            hold0.Depth == 42.5 && hold0.Speed == 13.7 && !hold0.Fallback
            && hold600.Depth == 42.5 && hold600.Speed == 13.7 && !hold600.Fallback
            && hold1499.Depth == 42.5 && hold1499.Speed == 13.7 && !hold1499.Fallback);

        // 到达/超过保持期（1500 毫秒整点与更久）：唯一的回落点。
        var fall1500 = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 2500, 1000, 42.5, 13.7, gateHoldMs);
        var fallHuge = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 100000, 1000, 42.5, 13.7, gateHoldMs);
        Check("姿态门控：空档 1500 毫秒整点与更久都回落画面分（Fallback=true，姿态值不再参与）",
            fall1500.Fallback && fallHuge.Fallback);

        // 保持不续期：断掉期间逐拍逼近（499/1099/1499 毫秒），窗口始终从最后一次有效时刻起算，1500 整点回落。
        var step499 = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 1499, 1000, 42.5, 13.7, gateHoldMs);
        var step1099 = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 2099, 1000, 42.5, 13.7, gateHoldMs);
        var step1499 = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 2499, 1000, 42.5, 13.7, gateHoldMs);
        var step1500 = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 2500, 1000, 42.5, 13.7, gateHoldMs);
        Check("姿态门控：保持期从最后一次有效时刻起算、不因保持而续期（1499 内保持、1500 整点回落）",
            !step499.Fallback && !step1099.Fallback && !step1499.Fallback && step1500.Fallback);

        // 从没出现过有效姿态（lastValidMs=0）：没有旧值可保持，直接回落，绝不拿 0 当深度。
        var neverHad = PoseMotionSource.ResolveFollowSignal(false, 0, 0, 150, 0, 0, 0, gateHoldMs);
        Check("姿态门控：本会话还没出现过有效姿态时直接回落画面分（没有旧值可保持）",
            neverHad.Fallback);

        // 频率路径：保持期里的频率由最后一次有效速度推出（与姿态有效时同一公式、同一上限）。
        double heldHz = PoseMotionSource.SpeedToHz(hold1499.Speed, 36);
        Check("姿态门控：保持期里的频率由最后一次有效速度推出（SpeedToHz 公式不变、仍夹在 FollowHzCap 内）",
            heldHz == PoseMotionSource.SpeedToHz(13.7, 36) && heldHz >= 0 && heldHz <= PoseMotionSource.FollowHzCap);

        // ── Lovense 兼容：游戏问「有什么玩具」必须看见一台 nora，命令强度要翻成动作 ──
        LovenseProtocol.Outcome toys = LovenseProtocol.Handle("POST", "/command", "{\"command\":\"GetToys\"}");
        Check("Lovense：GetToys 回了 200", toys.Ok);
        using (System.Text.Json.JsonDocument outer = System.Text.Json.JsonDocument.Parse(toys.Body))
        {
            string inner = outer.RootElement.GetProperty("data").GetProperty("toys").GetString() ?? "";
            using System.Text.Json.JsonDocument toyDoc = System.Text.Json.JsonDocument.Parse(inner);
            System.Text.Json.JsonElement toy = toyDoc.RootElement.GetProperty(LovenseProtocol.ToyId);
            Check("Lovense：GetToys 把玩具名报成 Solace Pro（sanitize 后小写）",
                toy.GetProperty("name").GetString() == "solace pro" && toy.GetProperty("nickName").GetString() == "OSR6");
            Check("Lovense：toys 是二次转义的 JSON 字符串（游戏要再解一次才拿到 name）",
                inner.Contains("\"name\":\"solace pro\""));
        }
        Check("Lovense：Function Vibrate:16 解出动作与强度",
            LovenseProtocol.ParseAction("Vibrate:16") == ("Vibrate", 16));
        Check("Lovense：Function 里 Rotate 也认（nora 有转动，游戏会给两根滑杆）",
            LovenseProtocol.ParseAction("Vibrate:2,Rotate:3") == ("Vibrate", 2));
        Check("Lovense：Pump 上限是 3 不是 20",
            LovenseProtocol.ParseAction("Pump:9") == ("Pump", 3));
        Check("Lovense：Stop 直接给停止",
            LovenseProtocol.ParseAction("Stop") == ("Stop", 0)
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Stop\"}").Action == "Stop");
        Check("Lovense：强度 0 也要真的停下来（不然游戏以为振了）",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Vibrate:0\"}").Action == "Stop");
        LovenseProtocol.Outcome vib = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Vibrate:16\",\"timeSec\":20,\"apiVer\":1}");
        Check("Lovense：Vibrate:16 给 16/20 的强度、幅度已接近满程",
            vib.Ok && vib.Strength == 16 && vib.Amplitude > 79 && vib.Amplitude < 84 &&
            Math.Abs(vib.Amplitude - LovenseProtocol.StrokeAmplitude(16)) < 1e-9);
        Check("Lovense：强度越大越深越快，拉满时用整段行程",
            LovenseProtocol.StrokeAmplitude(20) > LovenseProtocol.StrokeAmplitude(5)
            && LovenseProtocol.StrokeHz(20) > LovenseProtocol.StrokeHz(5)
            && Math.Abs(LovenseProtocol.StrokeAmplitude(20) - 100) < 1e-9);
        Check("Lovense：行程区间被夹住、倒着的两端自动换回来、太窄的往上撑到 10%",
            LovenseProtocol.StrokeWindow(20, 70) == (20.0, 70.0)
            && LovenseProtocol.StrokeWindow(70, 20) == (20.0, 70.0)
            && LovenseProtocol.StrokeWindow(-30, 999) == (0.0, 100.0)
            && LovenseProtocol.StrokeWindow(50, 52) == (50.0, 60.0)
            && LovenseProtocol.StrokeWindow(95, 100) == (90.0, 100.0));
        Check("Lovense：强度再大也走不出设的最高/最低点",
            Math.Abs(LovenseProtocol.StrokePosition(20, 70, 100, 1) - 70) < 1e-9
            && Math.Abs(LovenseProtocol.StrokePosition(20, 70, 100, -1) - 20) < 1e-9
            && Math.Abs(LovenseProtocol.StrokePosition(20, 70, 100, 0) - 45) < 1e-9
            && Math.Abs(LovenseProtocol.StrokePosition(20, 70, 50, 1) - 57.5) < 1e-9
            && Math.Abs(LovenseProtocol.StrokePosition(20, 70, 0, 1) - 45) < 1e-9
            && Math.Abs(LovenseProtocol.StrokePosition(20, 70, 500, 5) - 70) < 1e-9);
        Check("Lovense：抽插走「进快—到底停—退慢」，不再是纯正弦",
            Math.Abs(MotionVocabulary.ThrustWave(0.0, 0.45, 0.03)) < 1e-9
            && Math.Abs(MotionVocabulary.ThrustWave(0.46, 0.45, 0.03) - 1.0) < 1e-9
            && Math.Abs(MotionVocabulary.ThrustWave(0.99, 0.45, 0.03)) < 0.01
            // 推进段的中点比回程段的中点更靠深端：说明「进得快」
            && Math.Abs(MotionVocabulary.ThrustWave(0.225, 0.45, 0.03) - 0.75) < 0.01
            && MotionVocabulary.ThrustWave(0.8, 0.45, 0.03) > 0.05);
        Check("Lovense：条的百分比要翻成轴值（条上小=抬得高，轴值小=往下）",
            Math.Abs(LovenseProtocol.StrokeAxisValue(20) - 80) < 1e-9
            && Math.Abs(LovenseProtocol.StrokeAxisValue(70) - 30) < 1e-9
            && Math.Abs(LovenseProtocol.StrokeAxisValue(0) - 100) < 1e-9
            && Math.Abs(LovenseProtocol.StrokeAxisValue(100) - 0) < 1e-9
            && Math.Abs(LovenseProtocol.StrokeAxisValue(500) - 0) < 1e-9);
        Check("Lovense：深端留量让满强度也不顶到用户画的最深点",
            Math.Abs(LovenseProtocol.StrokeWavePosition(20, 70, 100, 0.0, 8) - 20) < 1e-9
            && Math.Abs(LovenseProtocol.StrokeWavePosition(20, 70, 100, 1.0, 8) - (20 + 50 * 0.92)) < 1e-9
            && Math.Abs(LovenseProtocol.StrokeWavePosition(20, 70, 100, 1.0, 0) - 70) < 1e-9
            && Math.Abs(LovenseProtocol.StrokeWavePosition(20, 70, 50, 1.0, 0) - 45) < 1e-9
            && LovenseProtocol.StrokeWavePosition(20, 70, 100, 5.0, 8) <= 66.0000001);
        Check("Lovense：快慢跟指令间隔走，间隔离谱时退回强度频率",
            Math.Abs(LovenseProtocol.TempoHz(0.5, 1.0) - 2.0) < 1e-9
            && Math.Abs(LovenseProtocol.TempoHz(0.2, 1.0) - 2.6) < 1e-9
            && Math.Abs(LovenseProtocol.TempoHz(0.05, 1.0) - 1.0) < 1e-9
            && Math.Abs(LovenseProtocol.TempoHz(9.0, 1.0) - 1.0) < 1e-9
            && Math.Abs(LovenseProtocol.TempoHz(double.NaN, 1.3) - 1.3) < 1e-9);
        Check("Lovense：旋转也跟着指令间隔走，一条指令拧一个来回",
            Math.Abs(LovenseProtocol.TwistHz(0.5, 1.0) - 2.0) < 1e-9
            && Math.Abs(LovenseProtocol.TwistHz(0.2, 1.0) - 2.6) < 1e-9
            && Math.Abs(LovenseProtocol.TwistHz(2.9, 1.0) - 0.35) < 1e-9
            && Math.Abs(LovenseProtocol.TwistHz(0.05, 0.6) - 0.6) < 1e-9
            && Math.Abs(LovenseProtocol.TwistHz(9.0, 0.6) - 0.6) < 1e-9);
        Check("Lovense：扭转条与行程条同一套区间规则（拖反了自动换、跨度至少 10）",
            LovenseProtocol.StrokeWindow(LovenseProtocol.DefaultRotateTopPercent, LovenseProtocol.DefaultRotateDeepPercent)
                == (LovenseProtocol.DefaultRotateTopPercent, LovenseProtocol.DefaultRotateDeepPercent)
            && LovenseProtocol.StrokeWindow(80, 20) == (20, 80)
            && LovenseProtocol.StrokePosition(20, 80, 25, 1) == 20 + (80 - 20) / 2.0 + (80 - 20) / 2.0 * 0.25);
        Check("游戏不限速档：只守轴限位，比动感档宽得多且不进界面三档",
            ComfortProfile.All.Count == 3
            && !ComfortProfile.All.Any(p => p.Id == "game")
            && ComfortProfile.Resolve("game").Id == "game"
            && ComfortProfile.Resolve("game").MaxAxisSpeedPerSecond > ComfortProfile.Dynamic.MaxAxisSpeedPerSecond
            && ComfortProfile.Resolve("game").MaxIntensity >= 3.0
            && ComfortProfile.Resolve("game").SoftStartSeconds <= 0.01);
        Check("Lovense：行程与快慢相冲时压低频率，避免被限速器削平",
            // 170%/s 的舒适档，走满 100% 行程最多 π×100 的速度 ⇒ 频率约 0.54Hz
            Math.Abs(LovenseProtocol.FitHz(2.0, 100, 170) - 170 / (Math.PI * 100)) < 1e-9
            && Math.Abs(LovenseProtocol.FitHz(0.5, 100, 170) - 0.5) < 1e-9
            && Math.Abs(LovenseProtocol.FitHz(2.0, 20, 170) - 2.0) < 1e-9
            && Math.Abs(LovenseProtocol.FitHz(1.5, 60, 620) - 1.5) < 1e-9);
        Check("Lovense：Preset earthquake 最强、wave 最轻",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Preset\",\"name\":\"earthquake\"}").Strength
                > LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Preset\",\"name\":\"wave\"}").Strength);
        Check("Lovense：Pattern 取最强的一格当持续强度",
            LovenseProtocol.Handle("POST", "/command",
                "{\"command\":\"Pattern\",\"strength\":\"5;12;18;3\",\"rule\":\"V:1;F:v;S:1000#\"}").Strength == 18);
        Check("Lovense：老式 GET /Vibrate?v=10 也认",
            LovenseProtocol.Handle("GET", "/Vibrate?v=10", null).Strength == 10
            && LovenseProtocol.Handle("GET", "/Stop", null).Action == "Stop");
        LovenseProtocol.Outcome allAction = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"All:13\",\"timeSec\":1,\"toy\":\"osr6hexa\",\"stopPrevious\":1,\"apiVer\":1}");
        Check("Lovense：All:13（Pure Onyx 用的写法）抽插和转动一起动",
            allAction.Ok && allAction.Amplitude > 0 && allAction.RotateAmplitude > 0);
        Check("Lovense：一条消息里同时有抽和振动时，振动接到转轴上",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Thrusting:10,Vibrate:14\"}") is { Ok: true, Amplitude: > 0, RotateAmplitude: > 0 });
        Check("Lovense：只有振动（没有抽）时仍然驱动直线，别只剩转",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Vibrate:14\"}") is { Ok: true, Amplitude: > 0, RotateAmplitude: 0 });
        Check("Lovense：官方写的 Thrusting / Fingering / Suction / Depth 都认",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Thrusting:12\"}").Ok
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Fingering:12\"}").Amplitude > 0
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Suction:12\"}").Amplitude > 0
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Depth:12\"}").Amplitude > 0);
        Check("Lovense：探活 ping 回 200 不当成坏命令",
            LovenseProtocol.Handle("POST", "/command", "{\"type\":\"ping\"}").Ok);
        Check("Lovense：乱发的命令回 400 而不是装作成功",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Nope\"}").Body.Contains("\"code\":400")
            && LovenseProtocol.Handle("POST", "/command", "{oops").Body.Contains("\"code\":400"));
        Check("Lovense：命令名大小写不敏感（不同游戏写法不一样）",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"gettoys\"}").Ok);
        Check("Lovense：timeSec 被读出来（游戏一条命令只发一次，靠它说跑多久）",
            Math.Abs(vib.Seconds - 20) < 1e-9
            && Math.Abs(LovenseProtocol.Handle("POST", "/command",
                "{\"command\":\"Function\",\"action\":\"Vibrate:9\",\"timeSec\":7.5}").Seconds - 7.5) < 1e-9);
        Check("Lovense：没写 timeSec 输出 0（官方 0/缺省 = 无限直到 Stop，由服务端处理）",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Vibrate:9\"}").Seconds == 0);
        Check("Lovense：玩具名可以换（游戏按名字推它能做哪些动作）",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"GetToys\"}", "max").Body.Contains("max", StringComparison.Ordinal)
            && !LovenseProtocol.Handle("POST", "/command", "{\"command\":\"GetToys\"}", "max").Body.Contains("nora", StringComparison.Ordinal));
        Check("Lovense：玩具名转小写且保留空格（Solace Pro → solace pro，与真实 GetToys 一致），手输引号进不了报文",
            LovenseProtocol.Sanitize("Solace Pro") == "solace pro" && LovenseProtocol.Sanitize("  Solace   Pro  ") == "solace pro"
            && LovenseProtocol.Sanitize(" \"ma\"x\\ ") == "max" && LovenseProtocol.Sanitize("") == "solace pro"
            && LovenseProtocol.Sanitize("Nora") == "nora" && LovenseProtocol.Sanitize("Max 2") == "max 2");
        LovenseProtocol.Outcome both = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Vibrate:16,Rotate:10\",\"timeSec\":5}");
        Check("Lovense：一条消息同时带抽插和转动时两路都保留", both.Ok && both.Amplitude > 0 && both.RotateAmplitude > 0);
        Check("Lovense：两路动作名标成 Vibrate+Rotate", both.Action == "Vibrate+Rotate");
        LovenseProtocol.Outcome twistOnly = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Rotate:20\"}");
        Check("Lovense：只发转动时直线不动、扭转动", twistOnly.Amplitude == 0 && twistOnly.RotateAmplitude > 0);
        Check("Lovense：Thrust 也认（抽插机用的动作，最贴近 OSR6 直线）",
            LovenseProtocol.ParseAction("Thrust:10") == ("Thrust", 10)
            && LovenseProtocol.Handle("POST", "/command",
                "{\"command\":\"Function\",\"action\":\"Thrust:20\"}").Amplitude > 40);
        Check("Lovense：玩具名换成 max 也不影响强度解析",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Vibrate:16\"}", "max").Strength == 16);
        Check("Lovense：timeSec 0/缺省 = 无限直到 Stop（服务端语义）",
            double.IsPositiveInfinity(LovenseProtocol.EffectiveSeconds(0))
            && Math.Abs(LovenseProtocol.EffectiveSeconds(20) - 20) < 1e-9
            && Math.Abs(LovenseProtocol.EffectiveSeconds(0.05) - 0.2) < 1e-9);
        LovenseProtocol.Outcome loopOutcome = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Vibrate:9\",\"timeSec\":20,\"loopRunningSec\":9,\"loopPauseSec\":4}");
        Check("Lovense：loopRunningSec/loopPauseSec 解析出来并算「动 N 停 M」",
            Math.Abs(loopOutcome.LoopRunningSec - 9) < 1e-9 && Math.Abs(loopOutcome.LoopPauseSec - 4) < 1e-9
            && !LovenseProtocol.LoopPaused(9, 4, 8) && LovenseProtocol.LoopPaused(9, 4, 10)
            && !LovenseProtocol.LoopPaused(9, 4, 13) && !LovenseProtocol.LoopPaused(0, 0, 100));
        LovenseProtocol.Outcome posOutcome = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Position\",\"value\":\"38\",\"apiVer\":1}");
        Check("Lovense：Position 值翻成 L0 直接定位（38 → 轴值 62）",
            posOutcome.Ok && posOutcome.Action == "Position"
            && Math.Abs(posOutcome.L0Target - 62) < 1e-9);
        LovenseProtocol.Outcome pvSetup = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"PatternV2\",\"type\":\"Setup\",\"actions\":[{\"ts\":500,\"pos\":20},{\"ts\":0,\"pos\":80}],\"apiVer\":1}");
        LovenseProtocol.Outcome pvPlay = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"PatternV2\",\"type\":\"Play\",\"startTime\":0,\"offsetTime\":100,\"timeMs\":1000,\"apiVer\":1}");
        Check("Lovense：PatternV2 Setup 存时间戳位置序列（按 ts 排序）",
            pvSetup.Ok && pvSetup.Action == "PatternV2Setup" && pvSetup.PatternPoints is { Count: 2 }
            && Math.Abs(pvSetup.PatternPoints[0].Ts) < 1e-9 && Math.Abs(pvSetup.PatternPoints[0].Pos - 80) < 1e-9);
        Check("Lovense：PatternV2 Play/Stop/SyncTime 都认，timeMs≤100 回 404",
            pvPlay.Ok && pvPlay.Action == "PatternV2Play" && Math.Abs(pvPlay.PatternOffsetMs - 100) < 1e-9
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"PatternV2\",\"type\":\"Stop\"}").Action == "Stop"
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"PatternV2\",\"type\":\"SyncTime\"}").Ok
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"PatternV2\",\"type\":\"Play\",\"timeMs\":100}").Body.Contains("\"code\":404"));
        Check("Lovense：PatternV2 按时间戳线性插值",
            Math.Abs(LovenseProtocol.PatternV2PositionAt(new[] { (0.0, 80.0), (500.0, 20.0) }, 250) - 50) < 1e-9
            && Math.Abs(LovenseProtocol.PatternV2PositionAt(new[] { (0.0, 80.0) }, 999) - 80) < 1e-9);
        Check("Lovense：Function Stroke:min-max 差≥20 且与 Thrusting 联用才生效",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Thrusting:12,Stroke:30-80\"}") is { StrokeMin: 30, StrokeMax: 80 }
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Thrusting:12,Stroke:40-50\"}").StrokeMin < 0
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Stroke:30-80\"}").StrokeMin < 0);
        Check("Lovense：Function Oscillate 归到旋转那一路",
            LovenseProtocol.Handle("POST", "/command", "{\"command\":\"Function\",\"action\":\"Oscillate:14\"}") is { Ok: true, RotateAmplitude: > 0 });
        Check("Lovense：Depth 上限 20→3（官方 0–3）",
            LovenseProtocol.ParseAction("Depth:9") == ("Depth", 3)
            && LovenseProtocol.ParseAction("Depth:2") == ("Depth", 2));
        Check("Lovense：默认伪装玩具换成 Solace Pro，nora 只在额外旋转玩具开启时出现",
            LovenseProtocol.ToysJson().Contains("solace pro", StringComparison.Ordinal)
            && !LovenseProtocol.ToysJson().Contains("nora", StringComparison.Ordinal)
            && LovenseProtocol.ToysJson(includeRotateToy: true).Contains("nora", StringComparison.Ordinal)
            && LovenseProtocol.Handle("POST", "/command", "{\"command\":\"GetToys\"}", "max", true).Body.Contains("nora", StringComparison.Ordinal));
        Check("Lovense：自动扭转联动公式（R0=50±amp×L0 偏离，夹进已设 R0 端点）",
            Math.Abs(AutoTwistLink.ComputeLinked(50, 25, 20, 80) - 50) < 1e-9
            && Math.Abs(AutoTwistLink.ComputeLinked(100, 25, 20, 80) - 75) < 1e-9
            && Math.Abs(AutoTwistLink.ComputeLinked(0, 25, 20, 80) - 25) < 1e-9
            && Math.Abs(AutoTwistLink.ComputeLinked(100, 25, 40, 60) - 60) < 1e-9
            && Math.Abs(AutoTwistLink.ComputeLinked(0, 25, 40, 60) - 40) < 1e-9);
        var twistLink = new AutoTwistLink();
        var twistNow = DateTime.UtcNow;
        twistLink.MarkExplicitRotate(twistNow);
        Check("Lovense：显式旋转 2 秒窗口（到时失效，让联动接回来）",
            twistLink.ExplicitRotateActive(twistNow)
            && !twistLink.ExplicitRotateActive(twistNow.AddSeconds(2.5)));
        var freshLink = new AutoTwistLink();
        Check("Lovense：没收到过显式旋转时联动不避让",
            !freshLink.ExplicitRotateActive(DateTime.UtcNow));

        // ── 双玩具定向：ExtraRotateToy 开启后 toy 参数把命令分到抽插/转动两路 ──
        Check("Lovense：ResolveToyTarget 单主/单转/空/双 id/未知都归到正确一路",
            LovenseProtocol.ResolveToyTarget("osr6hexa") == LovenseProtocol.ToyTarget.Main
            && LovenseProtocol.ResolveToyTarget("OSR6ROTATE") == LovenseProtocol.ToyTarget.Rotate
            && LovenseProtocol.ResolveToyTarget(null) == LovenseProtocol.ToyTarget.All
            && LovenseProtocol.ResolveToyTarget("") == LovenseProtocol.ToyTarget.All
            && LovenseProtocol.ResolveToyTarget("osr6hexa,osr6rotate") == LovenseProtocol.ToyTarget.All
            && LovenseProtocol.ResolveToyTarget("nonsense") == LovenseProtocol.ToyTarget.All);
        LovenseProtocol.Outcome toMain = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Vibrate:15\",\"toy\":\"osr6hexa\"}", includeRotateToy: true);
        LovenseProtocol.Outcome toRotate = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Rotate:10\",\"toy\":\"osr6rotate\"}", includeRotateToy: true);
        LovenseProtocol.Outcome toRotateArr = LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Rotate:10\",\"toy\":[\"osr6rotate\"]}", includeRotateToy: true);
        Check("Lovense：开额外玩具时 toy 定向到主/转玩具（数组格式也认）",
            toMain.Target == LovenseProtocol.ToyTarget.Main
            && toRotate.Target == LovenseProtocol.ToyTarget.Rotate
            && toRotateArr.Target == LovenseProtocol.ToyTarget.Rotate
            && LovenseProtocol.Handle("POST", "/command",
                "{\"command\":\"Function\",\"action\":\"Rotate:10\"}", includeRotateToy: true).Target == LovenseProtocol.ToyTarget.All
            && LovenseProtocol.Handle("POST", "/command",
                "{\"command\":\"Function\",\"action\":\"Rotate:10\",\"toy\":\"osr6hexa,osr6rotate\"}", includeRotateToy: true).Target == LovenseProtocol.ToyTarget.All);
        Check("Lovense：不开额外玩具时 toy 定向被忽略（与旧行为一致）",
            LovenseProtocol.Handle("POST", "/command",
                "{\"command\":\"Function\",\"action\":\"Rotate:10\",\"toy\":\"osr6rotate\"}").Target == LovenseProtocol.ToyTarget.All);
        Check("Lovense：toy=osr6rotate 的 Vibrate 只当转动强度、绝不碰抽插",
            LovenseProtocol.Handle("POST", "/command",
                "{\"command\":\"Function\",\"action\":\"Vibrate:15\",\"toy\":\"osr6rotate\"}", includeRotateToy: true)
                is { Amplitude: 0, RotateAmplitude: > 0, Action: "Vibrate>转" });

        LovenseChannels ch = LovenseChannels.Empty;
        ch = LovenseChannels.Merge(ch, toMain);
        Check("Lovense：toy=osr6hexa Vibrate:15 只写抽插路", ch.StrokeActive && ch.StrokeAmplitude > 0 && !ch.RotateActive);
        ch = LovenseChannels.Merge(ch, toRotate);
        Check("Lovense：toy=osr6rotate Rotate:10 不覆盖抽插，两路同时有效",
            ch.StrokeActive && ch.StrokeAmplitude > 0 && ch.RotateActive && ch.RotateAmplitude > 0);
        LovenseChannels sp0 = LovenseChannels.Merge(ch, LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Vibrate:15\",\"toy\":\"osr6hexa\",\"stopPrevious\":0}", includeRotateToy: true));
        Check("Lovense：主玩具 stopPrevious:0 不打断自己、也不碰 nora 转动",
            sp0.StrokeActive && sp0.StrokeAmplitude > 0 && sp0.RotateActive && sp0.RotateAmplitude > 0);
        ch = LovenseChannels.Merge(ch, LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Stop\",\"toy\":\"osr6rotate\"}", includeRotateToy: true));
        Check("Lovense：toy=osr6rotate Stop 只停转动，抽插继续",
            ch.StrokeActive && ch.StrokeAmplitude > 0 && !ch.RotateActive && ch.RotateAmplitude == 0);
        LovenseChannels bothCh = LovenseChannels.Merge(ch, LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Vibrate:12,Rotate:8\"}"));
        Check("Lovense：toy 缺省（全部玩具）命令两路一起写",
            bothCh.StrokeActive && bothCh.StrokeAmplitude > 0 && bothCh.RotateActive && bothCh.RotateAmplitude > 0);
        bothCh = LovenseChannels.Merge(bothCh, LovenseProtocol.Handle("POST", "/command",
            "{\"command\":\"Function\",\"action\":\"Stop\"}"));
        Check("Lovense：toy 缺省 Stop 两路一起停",
            !bothCh.StrokeActive && !bothCh.RotateActive && bothCh.StrokeAmplitude == 0 && bothCh.RotateAmplitude == 0);
    }

    // ── 画面同步：逐帧位置跟随（ScreenSyncTracker）────────────────────────
    static byte[] SyncFrame(double centerY, double halfBlock = 20, byte fg = 220, byte bg = 20)
    {
        const int w = ScreenSyncTracker.Width;
        const int h = ScreenSyncTracker.Height;
        var gray = new byte[w * h];
        double sigma = Math.Max(4.0, halfBlock * 3.0);
        double inv2s2 = 1.0 / (2.0 * sigma * sigma);
        int y0 = Math.Max(0, (int)Math.Round(centerY - halfBlock * 3));
        int y1 = Math.Min(h, (int)Math.Round(centerY + halfBlock * 3) + 1);
        for (int i = 0; i < gray.Length; i++) gray[i] = bg;
        for (int y = y0; y < y1; y++)
        {
            double dy = y - centerY;
            for (int x = 0; x < w; x++)
            {
                double dx = x - w / 2.0;
                double v = bg + (fg - bg) * Math.Exp(-(dx * dx + dy * dy) * inv2s2);
                gray[y * w + x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }

        }
        return gray;
    }

    /// <summary>整幅纹理按 panX 横移（摇镜），主体固定贴在纹理上 —— 用来复现「镜头一动整幅都在动」。</summary>
    static byte[] PanFrame(int panX, int panY)
    {
        const int w = ScreenSyncTracker.Width;
        const int h = ScreenSyncTracker.Height;
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = ((x - panX) % 40 + 40) % 40;
                int sy = ((y - panY) % 40 + 40) % 40;
                gray[y * w + x] = (byte)(sx < 8 || sy < 8 ? 210 : 40 + (sx * 3 + sy * 5) % 60);
            }
        // 主体：贴在纹理坐标上的亮块，随摇镜一起移动（相对场景静止）。
        int cx = 160 + panX, cy = 90 + panY;
        for (int y = Math.Max(0, cy - 14); y < Math.Min(h, cy + 14); y++)
            for (int x = Math.Max(0, cx - 14); x < Math.Min(w, cx + 14); x++)
                gray[y * w + x] = 250;
        return gray;
    }

    /// <summary>固定的场景纹理（背景不动）+ 主体上下移动 —— 用来验证「主体自己在动」不会被当成摇镜。</summary>
    static byte[] MotionFrame(double centerY)
    {
        const int w = ScreenSyncTracker.Width;
        const int h = ScreenSyncTracker.Height;
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                gray[y * w + x] = (byte)(26 + (x * 7 + y * 13) % 61);
        int cy = (int)Math.Round(centerY);
        for (int y = Math.Max(0, cy - 18); y < Math.Min(h, cy + 18); y++)
            for (int x = w / 2 - 26; x < w / 2 + 26; x++)
                gray[y * w + x] = 235;
        return gray;
    }

    static double Pearson(double[] a, double[] b)
    {
        double ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        double num = 0, va = 0, vb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            num += da * db; va += da * da; vb += db * db;
        }
        return num / Math.Sqrt(va * vb);
    }

    // ① 亮块按 1Hz 正弦上下移动：位置信号跟上、频率准、方向不跳反。
    {
        var tracker = new ScreenSyncTracker();
        const double dt = 1.0 / 20.0;
        var pos = new double[60];
        var trueY = new double[60];
        for (int i = 0; i < 120; i++)
        {
            double t = i * dt;
            double y = 90 + 40 * Math.Sin(2 * Math.PI * t);
            tracker.SubmitGray(SyncFrame(y), dt);
            if (i >= 60)
            {
                pos[i - 60] = tracker.Position;
                trueY[i - 60] = y;
            }
        }
        double corr = Pearson(pos, trueY);
        Check("画面同步：亮块正弦移动，L0 位置信号与正弦相关 > 0.9 且方向不跳反",
            tracker.Ok && Math.Abs(corr) > 0.9 && tracker.SignFixes <= 1);
        Check("画面同步：正弦移动测频误差 < 5%",
            tracker.Ok && Math.Abs(tracker.Hz - 1.0) / 1.0 < 0.05);
    }

    // ② 静止画面：质量门不过线，退回原锁相预设波形路径。
    {
        var tracker = new ScreenSyncTracker();
        for (int i = 0; i < 60; i++)
            tracker.SubmitGray(SyncFrame(90, 20, 20, 20), 1.0 / 20.0);
        Check("画面同步：静止画面 Ok=false（调用方退回兜底路径）",
            !tracker.Ok && !tracker.TryGetNormalizedPosition(false, out _));
    }

    // ③ 幅度减半：第二级相对深度随之减小。
    {
        var tracker = new ScreenSyncTracker();
        const double dt = 1.0 / 20.0;
        for (int i = 0; i < 120; i++)
            tracker.SubmitGray(SyncFrame(90 + 40 * Math.Sin(2 * Math.PI * i * dt)), dt);
        double depthFull = tracker.RelativeDepth;
        for (int i = 120; i < 240; i++)
            tracker.SubmitGray(SyncFrame(90 + 20 * Math.Sin(2 * Math.PI * i * dt)), dt);
        double depthHalf = tracker.RelativeDepth;
        Check("画面同步：幅度减半后相对深度减小",
            tracker.Ok && depthFull > ScreenSyncTracker.DepthFloor
            && depthHalf < depthFull * 0.8);
    }

    static double SyncCorr(double freq, double phase, double amp = 40, int frames = 240)
    {
        var tracker = new ScreenSyncTracker();
        const double dt = 1.0 / 20.0;
        var pos = new List<double>();
        var up = new List<double>();
        for (int i = 0; i < frames; i++)
        {
            double off = amp * Math.Sin(2 * Math.PI * freq * i * dt + phase);
            tracker.SubmitGray(SyncFrame(90 + off), dt);
            if (i >= frames / 2 && tracker.TryGetNormalizedPosition(false, out double n))
            {
                pos.Add(n);
                up.Add(-off);
            }
        }
        return pos.Count < 20 ? 0 : Pearson(pos.ToArray(), up.ToArray());
    }

    // ④ 方向锚定画面几何：不同相位 / 频率下都是「内容往上 = 位置变大」，反转勾选才能固定生效。
    {
        double[] cs =
        {
            SyncCorr(1.0, 0), SyncCorr(1.0, Math.PI), SyncCorr(1.0, Math.PI / 2),
            SyncCorr(1.5, 0), SyncCorr(2.0, 0), SyncCorr(0.5, 0, 40, 320),
        };
        Check("画面同步：方向与锁定相位/频率无关（内容上移 = 位置变大）",
            cs.All(c => c > 0.9));
    }

    // ⑤ 测频取基频：1.5 / 2 / 3 Hz 不再报成 0.5 / 1 Hz。
    foreach (double f in new[] { 1.5, 2.0, 3.0 })
    {
        var tracker = new ScreenSyncTracker();
        const double dt = 1.0 / 20.0;
        for (int i = 0; i < 160; i++)
            tracker.SubmitGray(SyncFrame(90 + 40 * Math.Sin(2 * Math.PI * f * i * dt)), dt);
        Check($"画面同步：{f}Hz 测频误差 < 5%",
            tracker.Ok && Math.Abs(tracker.Hz - f) / f < 0.05);
    }

    // ⑥ 深度按比例：幅度 40→16 后相对深度约 0.4（不是旧算法的 0.76~0.86）。
    {
        var tracker = new ScreenSyncTracker();
        const double dt = 1.0 / 20.0;
        for (int i = 0; i < 200; i++)
            tracker.SubmitGray(SyncFrame(90 + 40 * Math.Sin(2 * Math.PI * 1.2 * i * dt)), dt);
        double d1 = tracker.RelativeDepth;
        for (int i = 200; i < 400; i++)
            tracker.SubmitGray(SyncFrame(90 + 16 * Math.Sin(2 * Math.PI * 1.2 * i * dt)), dt);
        double d2 = tracker.RelativeDepth;
        Check($"画面同步：幅度 40→16 相对深度按比例下降（{d1:F2}→{d2:F2}）",
            tracker.Ok && d1 > 0.8 && d2 / d1 > 0.25 && d2 / d1 < 0.6);
    }


    // ⑦ 镜头平移 ≠ 角色动作：整幅画面在平移时标记为 PanDominant，静止主体 + 摇镜不再被当成来回动作。
    {
        var tracker = new ScreenSyncTracker();
        const double dt = 1.0 / 20.0;
        var panFrames = new byte[100][];
        for (int i = 0; i < 100; i++)
            panFrames[i] = PanFrame((int)Math.Round(14 * Math.Sin(2 * Math.PI * 1.0 * i * dt)), 0);
        var swPan = System.Diagnostics.Stopwatch.StartNew();
        foreach (byte[] frame in panFrames) tracker.SubmitGray(frame, dt);
        swPan.Stop();
        Check($"画面同步：整幅摇镜被判成 PanDominant（{tracker.PanPixels:0}px/帧，{tracker.PanFraction:P0} 可由平移解释，采样 {tracker.PanSamples}，块 {tracker.PanBlockFraction:P0}）",
            tracker.PanDominant);
        Console.WriteLine($"    [测速] 整幅平移估计 {swPan.Elapsed.TotalMilliseconds / 100:F3} ms/帧（{ScreenSyncTracker.Width}×{ScreenSyncTracker.Height}，20fps 预算 50ms）");
    }


    // ⑧ 主体自己在动、背景不动：不能被误判成摇镜。
    {
        var tracker = new ScreenSyncTracker();
        const double dt = 1.0 / 20.0;
        for (int i = 0; i < 100; i++)
            tracker.SubmitGray(MotionFrame(90 + 40 * Math.Sin(2 * Math.PI * 1.0 * i * dt)), dt);
        Check($"画面同步：主体局部运动不会被误判成整幅平移（{tracker.PanPixels:0}px/帧，{tracker.PanFraction:P0}，采样 {tracker.PanSamples}，块 {tracker.PanBlockFraction:P0}）",
            !tracker.PanDominant);

    // ⑨ 声音场景分类：合成呻吟 / 音乐 / 撞击 / 安静四种声音，看它各自听成什么。
    {
        const int rate = 48000;
        var rng = new Random(7);

        // 呻吟：0.9 秒一声、每声 0.62 秒，基频 210→265Hz 滑上去再滑回来，谐波按 1/h 衰减，底噪压到 −70dB。
        double[] moan = new double[rate * 6];
        {
            int period = rate * 9 / 10;
            for (int pos = 0; pos + period <= moan.Length; pos += period)
            {
                int len = (int)(0.62 * rate);
                double phase = 0;
                for (int i = 0; i < len; i++)
                {
                    double t = i / (double)rate;
                    double env = Math.Sin(Math.PI * t / 0.62);
                    env *= env;
                    double f0 = 210 + 55 * Math.Sin(Math.PI * t / 0.62);
                    phase += 2 * Math.PI * f0 / rate;
                    double v = 0;
                    for (int h = 1; h <= 6; h++) v += Math.Sin(h * phase) / h;
                    moan[pos + i] += 0.22 * env * v;
                }
            }
            for (int i = 0; i < moan.Length; i++) moan[i] += 0.0003 * (rng.NextDouble() * 2 - 1);
        }

        // 音乐：120BPM 底鼓 + 每拍长贝斯 + 反拍 hi-hat + 一直铺着的三音和弦。
        double[] music = new double[rate * 8];
        {
            double[] chord = [220.0, 277.18, 329.63];
            for (int i = 0; i < music.Length; i++)
            {
                double t = i / (double)rate;
                foreach (double f in chord) music[i] += 0.05 * Math.Sin(2 * Math.PI * f * t);
            }
            int beat = rate / 2;
            for (int pos = 0; pos + beat <= music.Length; pos += beat)
            {
                int k = pos / beat;
                for (int i = 0; i < rate / 4; i++)
                {
                    double t = i / (double)rate;
                    music[pos + i] += 0.30 * Math.Exp(-t * 26) * Math.Sin(2 * Math.PI * 55 * t);
                }
                double bassHz = (k % 4) switch { 0 => 82.41, 1 => 82.41, 2 => 110.0, _ => 98.0 };
                for (int i = 0; i < (int)(0.42 * rate); i++)
                {
                    double t = i / (double)rate;
                    music[pos + i] += 0.16 * Math.Exp(-t * 3.2) * Math.Sin(2 * Math.PI * bassHz * t);
                }
                int hp = pos + beat / 2;
                if (hp + 2400 <= music.Length)
                    for (int i = 0; i < 2400; i++)
                        music[hp + i] += 0.12 * Math.Exp(-i / (double)rate * 60) * (rng.NextDouble() * 2 - 1);
            }
        }

        // 撞击：40–60ms 的噪声性拍击 + 68Hz 闷响，间隔 0.40–0.60 秒随机（不踩拍子）。
        double[] slam = new double[rate * 6];
        {
            int pos = rate / 4;
            while (pos < slam.Length - rate / 3)
            {
                for (int i = 0; i < (int)(0.05 * rate); i++)
                    slam[pos + i] += 0.40 * Math.Exp(-i / (double)rate * 90) * (rng.NextDouble() * 2 - 1);
                for (int i = 0; i < (int)(0.16 * rate) && pos + i < slam.Length; i++)
                    slam[pos + i] += 0.26 * Math.Exp(-i / (double)rate * 30) * Math.Sin(2 * Math.PI * 68 * i / rate);
                pos += (int)((0.40 + 0.20 * rng.NextDouble()) * rate);
            }
            // 真实录音不会在两下撞击之间绝对静音：带一点房间底噪（−54dB），否则间隔全被判成安静。
            for (int i = 0; i < slam.Length; i++) slam[i] += 0.004 * (rng.NextDouble() * 2 - 1);
        }

        double[] quiet = new double[rate * 3];
        for (int i = 0; i < quiet.Length; i++) quiet[i] = 0.00008 * (rng.NextDouble() * 2 - 1);

        AudioSceneKind Classify(double[] pcm, out string detail)
        {
            var det = new AudioEventDetector();
            var scene = new AudioSceneClassifier();
            var events = new MotionEvent[AudioEventDetector.MaxEventsPerBlock];
            var tally = new int[8];
            int total = 0;
            for (int pos = 0; pos + 480 <= pcm.Length; pos += 480)
            {
                double at = pos / (double)rate;
                det.Process(pcm.AsSpan(pos, 480), rate, at, events);
                scene.Update(det.SceneFeature(at));
                if (at < 0.8) continue;                       // 前 0.8 秒当预热，不记账
                tally[(int)scene.Kind]++;
                total++;
            }
            int best = 0;
            for (int i = 1; i < tally.Length; i++) if (tally[i] > tally[best]) best = i;
            detail = $"{(AudioSceneKind)best} {tally[best]}/{total} 帧（呻吟 {tally[(int)AudioSceneKind.Voice]} / 音乐 {tally[(int)AudioSceneKind.Music]} / 撞击 {tally[(int)AudioSceneKind.Impact]} / 混合 {tally[(int)AudioSceneKind.Mixed]} / 安静 {tally[(int)AudioSceneKind.Silence]}），{scene.Describe()}";
            return (AudioSceneKind)best;
        }

        var moanKind = Classify(moan, out string moanNote);
        Check($"声音分类：呻吟听成人声（{moanNote}）", moanKind == AudioSceneKind.Voice);

        var musicKind = Classify(music, out string musicNote);
        Check($"声音分类：音乐听成音乐（{musicNote}）", musicKind == AudioSceneKind.Music);

        var slamKind = Classify(slam, out string slamNote);
        Check($"声音分类：撞击听成撞击（{slamNote}）", slamKind == AudioSceneKind.Impact);

        var quietKind = Classify(quiet, out string quietNote);
        Check($"声音分类：安静听成安静（{quietNote}）", quietKind == AudioSceneKind.Silence);
    }

        // ── 串口异常断开现场记录（SerialService.Incident.cs） ─────────────────
        // 出问题时要能回答三件事：最后发出去的是什么、设备自己吐了什么、断在第几帧。
        {
            var incidentSvc = new SerialService();
            incidentSvc.NoteTxTrace("L09999I100");
            incidentSvc.NoteTxTrace("L00000I050 R00000I020");
            incidentSvc.NoteRxTrace("SR6 TCode v1\r\nExecutor: Stats F50.0");
            incidentSvc.NoteRxTrace("0 frames=100\nGuru Medit");
            incidentSvc.NoteRxTrace("ation Error: Core 0 panic'ed (Load access fault)\n");
            string trace = incidentSvc.BuildIncidentText("disconnect", "测试断开", new IOException("信号灯超时时间已到"));

            Check("断开现场：带版本头", trace.Contains("# hexa-serial-trace-v1"));
            Check("断开现场：事件头有类型与说明", trace.Contains("类型: disconnect") && trace.Contains("说明: 测试断开"));
            Check("断开现场：发送轨迹在", trace.Contains("L09999I100") && trace.Contains("L00000I050 R00000I020"));
            Check("断开现场：跨两次读的半行被拼回一行", trace.Contains("Executor: Stats F50.00 frames=100"));
            Check("断开现场：设备故障行单独摘出",
                trace.Contains("## 设备自报故障行（1 行") && trace.Contains("Guru Meditation Error: Core 0 panic'ed (Load access fault)"));
            Check("断开现场：异常原文带上", trace.Contains("信号灯超时时间已到"));

            incidentSvc.StashIncidentTrace("disconnect", "断开那一刻的快照");
            string cached = incidentSvc.TakeIncidentTrace("archive", "诊断包现场");
            Check("断开现场：诊断包优先用断开瞬间的快照",
                cached.Contains("类型: disconnect") && cached.Contains("说明: 断开那一刻的快照"));
            Check("断开现场：快照只被取走一次",
                incidentSvc.TakeIncidentTrace("archive", "诊断包现场").Contains("类型: archive"));
            // 落盘这一段单独验：诊断包里那一份就是从这儿出去的。
            string traceFile = Path.Combine(Path.GetTempPath(), "hexa-trace-test-" + Guid.NewGuid().ToString("N") + ".txt");
            Hexa.Diagnostics.DiagnosticReport.WriteSerialTraceTxt(traceFile, "disconnect", incidentSvc);
            string written = File.Exists(traceFile) ? File.ReadAllText(traceFile) : "";
            Check("断开现场：能落成可以直接打开的 txt",
                written.Contains("# hexa-serial-trace-v1") && written.Contains("L09999I100")
                && written.Contains("Guru Meditation Error"));
            try { File.Delete(traceFile); } catch (IOException) { }

            incidentSvc.Dispose();
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL unexpected exception: {ex}");
    failed++;
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

/// <summary>
/// 最小传输桩，行为对齐 <see cref="SerialService"/>（测试要复现的是"传输层怎么把写失败变成状态"）：
/// ① 写失败不抛给调用方，而是自己标记掉线：LastError 非空 + OutputEnabled=false + ConnectionChanged(false)；
/// ② 端口没打开时，把 OutputEnabled 置 true 一律拒绝（复刻 SerialService.OutputEnabled 的
///    <c>value &amp;&amp; IsOpen</c> 闸门，并记下"有人想解锁"这件事，让断言不是空转）；
/// ③ 成功写入的指令记在 <see cref="Writes"/> 里，"引擎能重新驱动"才有可观测证据。
/// </summary>
sealed class FlakyTransport : ICommandTransport
{
    private readonly object _gate = new();
    private readonly List<string> _writes = [];
    private bool _open = true;
    private bool _outputEnabled;
    private string? _lastError;

    /// <summary>true = 之后的写操作都失败（模拟拔线 / 串口卡死）。</summary>
    public bool FailWrites { get; set; }

    public bool IsOpen { get { lock (_gate) return _open; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public IReadOnlyList<string> Writes { get { lock (_gate) return _writes.ToArray(); } }

    /// <summary>有人把 OutputEnabled 置 true 的次数（含被拒绝的）。</summary>
    public int UnlockAttempts { get; private set; }

    /// <summary>因为端口没开而被拒绝的解锁次数。</summary>
    public int RefusedUnlocks { get; private set; }

    public bool OutputEnabled
    {
        get { lock (_gate) return _outputEnabled; }
        set
        {
            lock (_gate)
            {
                if (value) UnlockAttempts++;
                bool enabled = value && _open;
                if (value && !enabled) RefusedUnlocks++;
                _outputEnabled = enabled;
            }
        }
    }

    public string PortName => "TEST-FLAKY";
    public string? UserAdvice => LastError;
    public event Action<bool>? ConnectionChanged;

    public void Send(string command) => Write(command);
    public void StopMotion() => Write("DSTOP");

    public void SendAxes(
        double[] values,
        Dictionary<string, int> axisMin,
        Dictionary<string, int> axisMax,
        int interpolationMs = 16,
        bool changedOnly = true) =>
        Write(string.Join(" ", values.Select(value => value.ToString("F1"))) + $" I{interpolationMs}");

    public void SendFrame(
        IEnumerable<TCodeAxisTarget> targets,
        Dictionary<string, int> axisMin,
        Dictionary<string, int> axisMax,
        bool changedOnly = true) =>
        Write(string.Join(" ", targets.Select(target => target.ToString())));

    /// <summary>模拟「拔插完成、端口真的回来了」：重新打开并广播 connected=true。</summary>
    public void Reconnect()
    {
        lock (_gate)
        {
            _open = true;
            _lastError = null;
        }
        ConnectionChanged?.Invoke(true);
    }

    /// <summary>模拟竞态：端口还没回来，引擎却收到一个 connected=true。</summary>
    public void RaiseConnectionChangedWithoutPort() => ConnectionChanged?.Invoke(true);

    private void Write(string command)
    {
        bool disconnected = false;
        lock (_gate)
        {
            if (FailWrites)
            {
                // 与 SerialService.MarkDisconnectedLocked 同一组动作；只在状态真的变化时广播，
                // 避免引擎处理掉线事件时又发一帧（DSTOP）造成回调递归。
                if (_open || _outputEnabled || _lastError is null)
                {
                    _open = false;
                    _outputEnabled = false;
                    _lastError = "IOException：串口写入失败（设备可能已拔出）";
                    disconnected = true;
                }
            }
            else
            {
                _lastError = null;
                _writes.Add(command);
            }
        }
        if (disconnected) ConnectionChanged?.Invoke(false);
    }
}

/// <summary>
/// 桥测试用的最小 WebSocket 客户端：握手 + 发一个带掩码的文本帧 + 读服务端文本帧。
/// 帧格式与测试里既有的 WsSend 局部函数同一套（服务端帧不加掩码，见 IntifaceBridgeService
/// 的「WebSocket 帧（RFC 6455，服务端到客户端不加掩码）」）。比 WsSend 多的是"连接可以保持住"，
/// 多客户端仲裁必须两个客户端同时在线上才测得到。
/// </summary>
sealed class WsProbe : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    public string Handshake { get; }

    public WsProbe(int port)
    {
        _tcp = new TcpClient();
        _tcp.Connect("127.0.0.1", port);
        _stream = _tcp.GetStream();
        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        byte[] request = System.Text.Encoding.UTF8.GetBytes(
            "GET / HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n\r\n");
        _stream.Write(request, 0, request.Length);
        _stream.Flush();
        byte[] buffer = new byte[1024];
        int read = 0;
        int attempts = 0;
        while (read < 100 && attempts++ < 50)
        {
            Thread.Sleep(20);
            if (_stream.DataAvailable) read += _stream.Read(buffer, read, buffer.Length - read);
        }
        Handshake = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    public void Send(string json)
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
        byte[] mask = [0x12, 0x34, 0x56, 0x78];
        var frame = new List<byte> { 0x81, (byte)(0x80 | payload.Length) };
        frame.AddRange(mask);
        for (int i = 0; i < payload.Length; i++) frame.Add((byte)(payload[i] ^ mask[i % 4]));
        _stream.Write(frame.ToArray(), 0, frame.Count);
        _stream.Flush();
    }

    /// <summary>等一个含 needle 的服务端文本帧（跳过 DeviceAdded 这类广播帧）；超时返回 ""。</summary>
    public string WaitFor(string needle, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            string? frame = ReadTextFrame((int)Math.Max(1, deadline - Environment.TickCount64));
            if (frame is null) return "";
            if (frame.Contains(needle, StringComparison.Ordinal)) return frame;
        }
        return "";
    }

    private string? ReadTextFrame(int timeoutMs)
    {
        try
        {
            _stream.ReadTimeout = Math.Clamp(timeoutMs, 1, 30_000);
            byte[] header = new byte[2];
            if (!ReadExact(header)) return null;
            int length = header[1] & 0x7F;
            if (length == 126)
            {
                byte[] ext = new byte[2];
                if (!ReadExact(ext)) return null;
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                byte[] ext = new byte[8];
                if (!ReadExact(ext)) return null;
                length = (int)BitConverter.ToUInt64(ext, 0);
            }
            byte[] payload = new byte[length];
            if (!ReadExact(payload)) return null;
            return System.Text.Encoding.UTF8.GetString(payload);
        }
        catch (IOException) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    private bool ReadExact(byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = _stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch (IOException) { }
        try { _tcp.Dispose(); } catch (IOException) { }
    }
}
/// <summary>桥的服务层测试用：只记下发出去的指令，不碰真串口；可手动模拟设备掉线。</summary>
sealed class FakeTransport : Hexa.Services.ICommandTransport
{
    private readonly List<string> _sent = new();
    public bool IsOpen => true;
    public bool OutputEnabled { get; set; } = true;
    public string PortName => "FAKE";
    public string? UserAdvice => null;
    public event Action<bool>? ConnectionChanged;
    public IReadOnlyList<string> Sent { get { lock (_sent) return _sent.ToArray(); } }
    public void RaiseConnectionChanged(bool connected) => ConnectionChanged?.Invoke(connected);
    public void Send(string command) { lock (_sent) _sent.Add(command); }
    public void StopMotion() { }
    public void SendAxes(double[] values, Dictionary<string, int> axisMin, Dictionary<string, int> axisMax,
        int interpolationMs = 16, bool changedOnly = true) { }
    public void SendFrame(IEnumerable<Hexa.Services.TCodeAxisTarget> targets,
        Dictionary<string, int> axisMin, Dictionary<string, int> axisMax, bool changedOnly = true) { }
}
