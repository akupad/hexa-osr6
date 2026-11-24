using System.Reflection;
using System.Text.Json;
using Hexa.Models;
using Hexa.Services;

static object? Call(object target, string name, params object?[] args)
{
    // 同名重载（例如 TryReadAudioRhythm 的 2 出参与 4 出参两个版本）按实参个数挑，
    // 免得 GetMethod(name) 抛 AmbiguousMatchException。
    MethodInfo? method = target.GetType()
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length);
    if (method is null) throw new MissingMethodException(target.GetType().FullName, $"{name}/{args.Length}");
    return method.Invoke(target, args);
}
static double Get(object target, string name) =>
    (double)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
static void Set(object target, string name, object value) =>
    target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
var evidence = new Dictionary<string, object>();
var cfg = new AppSettings {
    AudioReactiveEnabled = false, AudioListenOnly = false, RuleEngineEnabled = false,
    CompanionAuto = false, AmbientOverlay = false, GateCompanionOnSilence = false,
    ScreenWatchEnabled = false, ScreenSyncMirrorPose = true, ScreenSyncAudioRhythm = false,
    ScreenSyncDepth = 50, ScreenSyncMinPosition = 0, ScreenSyncMaxPosition = 100,
    MotionMultiAxis = false, AutoTwistEnabled = false, ComfortProfile = "immersive"
};
using var serial = new SerialService();
serial.SetSimulationMode(true);
using var engine = new MotionEngine(serial, cfg);
using var screen = new ScreenWatchService(cfg);
using var audio = new AudioReactiveService(engine, cfg);
void ResetMirror() { Set(screen, "_mirrorPosition", double.NaN); Set(screen, "_mirrorSent", double.NaN); }
double Mirror(double depth) {
    object?[] args = [engine, new PoseMotionSource.PoseFollowSignal(depth, 0, false), ""];
    return ((double[])Call(screen, "TryBuildMirrorAxes", args)!)[0];
}
ResetMirror();
double first = Mirror(50);
for (int i = 0; i < 200; i++) Mirror(51.5);
evidence["mirror_small_sustained_move"] = new { initialDepth=50, requestedDepth=51.5, frames=200, initialOutput=first, finalOutput=Mirror(51.5), filteredPosition=Get(screen,"_mirrorPosition") };
ResetMirror(); double top = Mirror(0);
ResetMirror(); double center = Mirror(50);
ResetMirror(); double bottom = Mirror(100);
evidence["mirror_asymmetric_mapping"] = new { top, center, bottom, upperTravel=center-top, lowerTravel=bottom-center };
var outputs = new List<double[]>();
foreach(double speed in new[]{0.3, 3.0}) {
    cfg.ScreenSyncSpeed=speed; ResetMirror();
    outputs.Add(new[]{20.0,40,60,80,20}.Select(Mirror).ToArray());
}
evidence["mirror_speed_slider"] = new { speedSlow=0.3, speedFast=3.0, equalOutputs=outputs[0].SequenceEqual(outputs[1]), slow=outputs[0], fast=outputs[1] };
var durations = new List<double>();
foreach(double speed in new[]{0.5,2.0}) {
    cfg.AudioSpeed=speed;
    Call(audio,"ActivateEvent",new MotionEvent(MotionEventKind.Beat,0.7,0),0.0,false);
    durations.Add(Get(audio,"_activeDuration"));
}
evidence["event_speed_slider"] = new { speedSlow=0.5, speedFast=2.0, durationSlow=durations[0], durationFast=durations[1] };
cfg.AudioSpeed=1;
var hz = new List<double>();
foreach(double level in new[]{0.1,0.9}) {
    Set(audio,"_phase",0.0); Call(audio,"ApplyVolumeTick",level);
    hz.Add(Get(audio,"_phase")/(Math.Tau*0.05));
}
evidence["volume_loudness_changes_rate_without_tempo"] = new { quietHz=hz[0], loudHz=hz[1] };
var voiceHz = new List<double>();
foreach(double level in new[]{0.1,0.9}) {
    Set(audio,"_voiceRate",0.0); Call(audio,"ApplyVoiceThrustTick",level);
    voiceHz.Add(Get(audio,"_voiceRate"));
}
evidence["voice_loudness_changes_rate_without_tempo"] = new { quietHz=voiceHz[0], loudHz=voiceHz[1] };
AutoBehaviorSequencer.SetPoolOverride(["deep_pulse"]);
try {
    var picks = new List<string>();
    for(int i=0;i<8;i++) {
        var seq=new AutoBehaviorSequencer(i);seq.Reset("free_play",1,ComfortProfile.Dynamic);
        picks.Add(seq.Step(0.02,1,[35,13,11,14,10,8],1,30,ComfortProfile.Dynamic,0,0,0).Pattern);
    }
    evidence["pool_override_affects_every_sequencer"] = new { independentSequencers=8, picks };
} finally { AutoBehaviorSequencer.SetPoolOverride(null); }

// Synthetic feature snapshots; capture stays stopped and output stays disabled.
var audioProperty = typeof(Hexa.App).GetProperty("AudioReactive", BindingFlags.Public | BindingFlags.Static)!;
object? previousAudio = audioProperty.GetValue(null);
audioProperty.SetValue(null, audio);
try {
    cfg.AudioReactiveEnabled = true;
    cfg.ScreenSyncAudioRhythm = true;
    cfg.ScreenSyncSpeed = 1;
    Set(audio, "_energy", 0.8);
    Set(audio, "_beatPulse", 0.0);
    var tempo = audio.Tempo!;
    Set(tempo, "_phaseValid", true);
    typeof(TempoTracker).GetProperty("Confidence")!.SetValue(tempo, 1.0);
    var pairedOutputs = new List<double[]>();
    var labels = new List<string>();
    foreach (double bpm in new[]{60.0, 180.0}) {
        typeof(TempoTracker).GetProperty("Bpm")!.SetValue(tempo, bpm);
        ResetMirror();
        pairedOutputs.Add(new[]{20.0,40,60,80,20}.Select(Mirror).ToArray());
        object?[] mirrorArgs = [engine, new PoseMotionSource.PoseFollowSignal(20,0,false), ""];
        Call(screen, "TryBuildMirrorAxes", mirrorArgs);
        labels.Add((string)mirrorArgs[2]!);
    }
    evidence["mirror_audio_bpm_changes_label_only"] = new {
        syntheticSnapshot = true, lowBpm = 60, highBpm = 180,
        equalOutputs = pairedOutputs[0].SequenceEqual(pairedOutputs[1]),
        slow = pairedOutputs[0], fast = pairedOutputs[1], labels
    };
    cfg.AudioNoiseGate = 0.95;
    Set(audio, "_energy", 0.2);
    Set(audio, "_envelope", 0.0);
    object?[] rhythmArgs = [0.0,0.0];
    bool accepted = (bool)Call(screen,"TryReadAudioRhythm",rhythmArgs)!;
    evidence["audio_snapshot_validity_guard"] = new {
        syntheticSnapshot = true, capturing = audio.Capturing,
        wantsDevice = audio.WantsDevice, gate = cfg.AudioNoiseGate,
        rawEnergy = audio.Energy, effectiveEnvelope = 0,
        accepted, bump = (double)rhythmArgs[0]!
    };
} finally {
    cfg.AudioReactiveEnabled = false;
    cfg.ScreenSyncAudioRhythm = false;
    audioProperty.SetValue(null, previousAudio);
}

Console.WriteLine(JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
