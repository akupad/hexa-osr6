using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Hexa.Models;
using Hexa.Services;

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

// 开发期探针：HELIX_SR6_RENDER=<dir> 时把 SR6 模型离屏渲染成 PNG（仅用于人工确认朝向）
if (Environment.GetEnvironmentVariable("HELIX_SR6_RENDER") is { Length: > 0 } renderDir)
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

    string detectorRoot = Path.Combine(Path.GetTempPath(), "helix-engine-detector-" + Guid.NewGuid().ToString("N"));
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
    string firstFrame = TCodeFrameFormatter.FormatAxes(
        [50, 50, 50, 50, 50, 50], defaults.AxisMin, defaults.AxisMax, 10,
        previousRaw, previousValid, changedOnly: true);
    string unchangedFrame = TCodeFrameFormatter.FormatAxes(
        [50, 50, 50, 50, 50, 50], defaults.AxisMin, defaults.AxisMax, 10,
        previousRaw, previousValid, changedOnly: true);
    string oneAxisFrame = TCodeFrameFormatter.FormatAxes(
        [51, 50, 50, 50, 50, 50], defaults.AxisMin, defaults.AxisMax, 10,
        previousRaw, previousValid, changedOnly: true);
    Check("dirty-only sends initial full pose", firstFrame.Split(' ').Length == 6);
    Check("dirty-only suppresses unchanged frame", unchangedFrame.Length == 0);
    Check("dirty-only sends only changed axis", oneAxisFrame.StartsWith("L0", StringComparison.Ordinal)
        && !oneAxisFrame.Contains(' '));

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
    Check("device is armed and can run by default", engine.IsArmed && engine.CanRun);
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
    Check("raw axis token is normalized to safe bounds", serial.SimulationCommands[^1] == "L09999I9999");
    Check("calibration returns to center before playback", engine.TryMoveCalibrationAxis("L0", 5000));

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
    Check("fast custom motion is still bounded", customPositions.Zip(customPositions.Skip(1), (a, b) => Math.Abs(b - a)).All(delta => delta < 3500));
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
    Check("game bridge six-axis mode declares every installed axis",
        featureCount == 9 && sixList.Contains("\"L2\"") && sixList.Contains("\"R2\"")
        && sixList.Contains("Hexa OSR6 6-Axis"));
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
    Check("game bridge legacy single mode still declares only L0+R0",
        System.Text.RegularExpressions.Regex.Matches(legacyList, "FeatureDescriptor").Count == 2);

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
        cfg.BridgeUseComfortLimits = false;
        string handshake = WsSend("[{\"LinearCmd\":{\"Id\":10,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":1.0,\"Duration\":200}]}}]");
        Check("game bridge completes websocket handshake",
            handshake.Contains("101 Switching Protocols") && handshake.Contains("Sec-WebSocket-Accept"));
        Check("game bridge linear reaches serial as L09999I200",
            serial.SimulationCommands.Count > beforeBridge && serial.SimulationCommands.Contains("L09999I200"));

        // 整形打开（默认）：同一条指令不再一步到底，而是按 50ms 节拍分步爬上去，
        // 且插值时间用节拍长度（60ms），设备才不会一次跳满行程。
        cfg.BridgeInputSmoothing = true;
        cfg.BridgeSmoothingMs = 90;
        cfg.BridgeUseComfortLimits = true;
        int beforeShape = serial.SimulationCommands.Count;
        WsSend("[{\"LinearCmd\":{\"Id\":14,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":1.0,\"Duration\":200}]}}]");
        bool shapedReached = SpinWait.SpinUntil(
            () => serial.SimulationCommands.Skip(beforeShape).Any(cmd => cmd.StartsWith("L099", StringComparison.Ordinal)), 3000);
        var shapedSteps = serial.SimulationCommands.Skip(beforeShape)
            .Where(cmd => cmd.StartsWith("L0", StringComparison.Ordinal)).ToList();
        Check("game bridge shaped output ramps toward the target instead of jumping",
            shapedReached && shapedSteps.Count >= 3 && !shapedSteps[0].StartsWith("L099", StringComparison.Ordinal));
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

        cfg.GameBridgeEnabled = false;
        bridge.Stop();
        Check("game bridge stops cleanly", !bridge.Active);
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
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL unexpected exception: {ex}");
    failed++;
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
