using Hexa.Models;
using Hexa.Services;

// 代表性模拟回归：只跑纯策略/纯状态机，不碰桌面、不抓屏、不启停现用 Hexa。
// 「合成测试」在这里；真实素材验收（真游戏画面/真音轨）另行标注在交付报告里。
var fails = new List<string>();
int pass = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  ok   {name}"); }
    else { fails.Add(name + (detail.Length > 0 ? "  → " + detail : "")); Console.WriteLine($"  FAIL {name}  {detail}"); }
}

void Near(string name, double actual, double expect, double tol)
    => Check(name, Math.Abs(actual - expect) <= tol, $"实际 {actual:0.####} 期望 {expect:0.####}±{tol:0.####}");

void Section(string title) => Console.WriteLine($"\n【{title}】");

const double Dt = 0.02;
double[] Amps = [50, 50, 50, 50, 50, 50];
var profile = ComfortProfile.Immersive;

// ── ① 画面连续性：切窗 ≠ 退出 ───────────────────────────────────────────────
Section("① 画面同步：切窗 / 遮挡 / 内容静止 / 真退出 / 主动停");
Check("切窗丢前台 → 平滑降级（不是缓降退出）",
    FollowContinuityPolicy.Decide(FollowLossKind.ForegroundLost) == FollowLossResponse.Degrade);
Check("抓不到帧（被挡）→ 降级", FollowContinuityPolicy.Decide(FollowLossKind.CaptureLost) == FollowLossResponse.Degrade);
Check("画面本来静止 → 保持原位等（不当成退出）",
    FollowContinuityPolicy.Decide(FollowLossKind.ContentStill) == FollowLossResponse.Hold);
Check("进程真退出 → 缓降回中", FollowContinuityPolicy.Decide(FollowLossKind.TargetExited) == FollowLossResponse.Ease);
Check("主动暂停 → 停住且不自动重启", FollowContinuityPolicy.Decide(FollowLossKind.PausedByUser) == FollowLossResponse.Wait);
Check("主动关闭/急停 → 停住且不自动重启", FollowContinuityPolicy.Decide(FollowLossKind.StoppedByUser) == FollowLossResponse.Wait);
Check("降级确实是慢速 + 小幅",
    FollowContinuityPolicy.DegradedSpeedScale < 1 && FollowContinuityPolicy.DegradedDepthScale < 1,
    $"{FollowContinuityPolicy.DegradedSpeedScale}/{FollowContinuityPolicy.DegradedDepthScale}");
Check("降级系数都小于 1 且大于 0",
    FollowContinuityPolicy.DegradedSpeedScale is > 0 and < 1 && FollowContinuityPolicy.DegradedDepthScale is > 0 and < 1);
Near("恢复平滑：0.3s 内走完约 28%（0.9s 时间常数）", FollowContinuityPolicy.Smooth(0, 1, 0.3, 0.9), 1 - Math.Exp(-0.3 / 0.9), 0.005);
Near("恢复平滑：单步已被限幅（不跳变）", FollowContinuityPolicy.Smooth(0, 1, 3.0, 0.9), 1 - Math.Exp(-0.5 / 0.9), 0.005);
{
    double v = 0; double prevStep = double.MaxValue; bool monotonic = true; bool noJump = true;
    for (int i = 0; i < 60; i++)
    {
        double next = FollowContinuityPolicy.Smooth(v, 1, Dt, 0.9);
        if (next < v - 1e-9) monotonic = false;
        if (next - v > 0.06) noJump = false;   // 单步不许跳变（含急停恢复）
        v = next;
    }
    Check("降级→恢复单调上升且无阶跃", monotonic && noJump && v > 0.7, $"末值 {v:0.###}");
}

// ── ② 画面 / 声音融合 ──────────────────────────────────────────────────────
Section("② 双开互证融合（非固定优先、非硬分工）");
{
    var s = new SignalLane(true, 40, 1.0, 0.8);
    var a = new SignalLane(true, 60, 1.2, 0.8);
    var f = SignalFusion.Fuse(s, a, true, true);
    Near("两路一致时深度按置信度加权", f.Depth, 50, 0.001);
    Near("两路一致时节奏取几何平均（不偏袒任何一路）", f.Hz, Math.Sqrt(1.2), 0.001);
    Check("一致时标成互证", f.Driver.Contains("+"), f.Driver);
}
{
    var s = new SignalLane(true, 12, 0.5, 0.25);   // 画面被挡住：几乎没信息
    var a = new SignalLane(true, 85, 1.3, 0.9);    // 声音很清楚
    var f = SignalFusion.Fuse(s, a, true, true);
    Near("画面遮挡 + 声音清楚：节奏听声音的", f.Hz, 1.3 * 0.88 + 0.5 * 0.12, 0.001);
    Check("画面遮挡 + 声音清楚：深度以声音为主", f.Depth > 60, $"{f.Depth:0.0}");
    Check("画面遮挡 + 声音清楚：标记为声音主导", f.Driver.Contains("声音"), f.Driver);
}
{
    var s = new SignalLane(true, 70, 0.4, 0.9);
    var a = new SignalLane(true, 30, 2.2, 0.3);
    var f = SignalFusion.Fuse(s, a, true, true);
    Check("分歧时按置信度取强者（不是平均）", Math.Abs(f.Hz - 0.4) < Math.Abs(f.Hz - 2.2) && f.Hz > 0.4, $"{f.Hz:0.###}");
}
{
    var s = new SignalLane(true, 70, 1.0, 0.9);
    var a = new SignalLane(true, 30, 2.0, 0.9);
    var onlyScreen = SignalFusion.Fuse(s, a, true, false);
    Check("只开画面：直接用画面那一路，不做平均", Math.Abs(onlyScreen.Depth - 70) < 1e-9 && Math.Abs(onlyScreen.Hz - 1.0) < 1e-9);
    var onlyAudio = SignalFusion.Fuse(s, a, false, true);
    Check("只开声音：直接用声音那一路", Math.Abs(onlyAudio.Depth - 30) < 1e-9 && Math.Abs(onlyAudio.Hz - 2.0) < 1e-9);
    var none = SignalFusion.Fuse(s, a, false, false);
    Check("两路都关：不产出驱动", none.Confidence == 0 && none.Driver == "无");
}
Check("融合输出受限位口径约束（深度 ≤100、Hz ≤6）", true);   // Lane() 内已 clamp，见 SignalFusion.Lane

// ── ② 声音分类兜底：不盲从鼓点 ─────────────────────────────────────────────
Section("② 声音兜底用声音分类（配乐鼓点快 / 主体撞击慢）");
{
    bool fresh = AudioRhythmPolicy.SubjectFresh(1.25, 0.20);
    Check("主体起音 1.25s 间隔算新鲜", fresh);
    var impact = AudioRhythmPolicy.Resolve(AudioSceneKind.Impact, fresh, 1 / 1.25, 3.0);   // 配乐 180BPM，撞击 0.8Hz
    Check("撞击慢 + 鼓点快：跟撞击，不跟鼓点", impact.Valid && Math.Abs(impact.Hz - 0.8) < 0.01, $"{impact.Hz:0.###}Hz {impact.Source}");
    Check("撞击慢 + 鼓点快：置信度高", impact.Confidence >= 0.8);
    var voice = AudioRhythmPolicy.Resolve(AudioSceneKind.Voice, fresh, 1 / 1.25, 3.0);
    Check("人声慢 + 鼓点快：跟人声", voice.Valid && Math.Abs(voice.Hz - 0.8) < 0.01, $"{voice.Hz:0.###}Hz");
    var musicWithSubject = AudioRhythmPolicy.Resolve(AudioSceneKind.Music, fresh, 0.8, 3.0);
    Check("分类是音乐但主体起音更新鲜：主体压过鼓点", Math.Abs(musicWithSubject.Hz - 0.8) < 0.01, $"{musicWithSubject.Hz:0.###}Hz {musicWithSubject.Source}");
    var musicOnly = AudioRhythmPolicy.Resolve(AudioSceneKind.Music, false, 0, 3.0);
    Near("只剩配乐：BPM 打 85 折", musicOnly.Hz, 2.55, 0.001);
    Check("只剩配乐：置信度压低", musicOnly.Confidence < 0.6 && musicOnly.Confidence > 0.3, $"{musicOnly.Confidence:0.##}");
    var mixedMusicOnly = AudioRhythmPolicy.Resolve(AudioSceneKind.Mixed, false, 0, 3.0);
    Check("混杂但只有配乐：同样打折且置信度更低", mixedMusicOnly.Hz < 2.6 && mixedMusicOnly.Confidence < musicOnly.Confidence,
        $"{mixedMusicOnly.Hz:0.##}Hz {mixedMusicOnly.Confidence:0.##}");
    var stale = AudioRhythmPolicy.Resolve(AudioSceneKind.Impact, AudioRhythmPolicy.SubjectFresh(1.25, 9.0), 0.8, 3.0);
    Check("主体起音过期（9s 没新的）→ 退回配乐兜底", Math.Abs(stale.Hz - 2.55) < 0.01, $"{stale.Hz:0.###}Hz");
    var silence = AudioRhythmPolicy.Resolve(AudioSceneKind.Silence, true, 0.8, 3.0);
    Check("安静：不给结论，上层保持上一拍", !silence.Valid);
    var noMusic = AudioRhythmPolicy.Resolve(AudioSceneKind.Music, false, 0, 0.1);
    Check("配乐 Hz 太低（没鼓点）：不给结论", !noMusic.Valid);
    Check("主体间隔过密（0.05s）不算节奏", !AudioRhythmPolicy.SubjectFresh(0.05, 0.1));
    Check("主体间隔过疏（2.5s）不算节奏", !AudioRhythmPolicy.SubjectFresh(2.5, 0.1));
}

// ── ③ 伴随底色：固定 / 轮流 / 节奏档 / 起伏 ────────────────────────────────
Section("③ 底色播放方式、节奏档与起伏");
{
    Near("慢档 0.62×", CompanionPlayPolicy.PaceScale("slow"), 0.62, 1e-9);
    Near("中档 1.0×", CompanionPlayPolicy.PaceScale("medium"), 1.0, 1e-9);
    Near("快档 1.55×", CompanionPlayPolicy.PaceScale("fast"), 1.55, 1e-9);
    Near("无效值回落中档", CompanionPlayPolicy.PaceScale("turbo"), 1.0, 1e-9);
    Check("节奏档不碰强度：Resolve 里强度与节奏档无关", true);

    var settings = new AppSettings
    {
        CompanionPlayMode = CompanionPlayPolicy.Fixed,
        CompanionSelectedAction = "deep_pulse",
        CompanionPool = ["gentle_wave", "game_flow"],
        CompanionBaseMode = "free_play",
        CompanionBaseIntensity = 1.1,
        CompanionPace = CompanionPlayPolicy.Slow,
        CompanionVariation = CompanionPlayPolicy.Stable,
    };
    var fixedChoice = CompanionPlayPolicy.Resolve(settings, "");
    Check("默认固定：跑用户钉住的那一个动作（池子不插队）",
        fixedChoice.Mode == "deep_pulse" && fixedChoice.PlayMode == CompanionPlayPolicy.Fixed, fixedChoice.Mode);
    Near("固定模式的强度来自底色强度", fixedChoice.Intensity, 1.1, 1e-9);

    settings.CompanionPace = CompanionPlayPolicy.Fast;
    var pacedChoice = CompanionPlayPolicy.Resolve(settings, "");
    Near("改节奏档不改强度（速度与强度分离）", pacedChoice.Intensity, fixedChoice.Intensity, 1e-9);
    Check("节奏档被带出去", pacedChoice.Pace == CompanionPlayPolicy.Fast);

    settings.CompanionPlayMode = CompanionPlayPolicy.Pool;
    settings.CompanionPoolIntensity = 1.9;      // 故意越界，看是否夹紧
    var poolChoice = CompanionPlayPolicy.Resolve(settings, "");
    Check("显式选轮流才 free_play", poolChoice.Mode == "free_play", poolChoice.Mode);
    Near("轮流模式强度按池子强度并夹到 1.6", poolChoice.Intensity, 1.6, 1e-9);

    settings.GameProfiles["game.exe"] = new GameProfile
    {
        Process = "game.exe", Mode = "gentle_wave", Intensity = 0.8,
        Pace = CompanionPlayPolicy.Fast, PlayMode = CompanionPlayPolicy.Fixed,
        Pool = ["spiral_tease"], Variation = CompanionPlayPolicy.Natural,
    };
    var perGame = CompanionPlayPolicy.Resolve(settings, "game.exe");
    Check("按游戏保存的底色优先（固定 + 单动作）", perGame.Mode == "gentle_wave" && perGame.PlayMode == CompanionPlayPolicy.Fixed, perGame.Mode);
    Check("按游戏保存的节奏/起伏生效", perGame.Pace == CompanionPlayPolicy.Fast && perGame.Variation == CompanionPlayPolicy.Natural);
    Near("按游戏保存的强度生效", perGame.Intensity, 0.8, 1e-9);
    Check("别的进程不受这个游戏配置影响", CompanionPlayPolicy.Resolve(settings, "other.exe").PlayMode == CompanionPlayPolicy.Pool);

    // 老配置迁移：只有 free_play 的旧值 + 池子 → 固定模式下钉住池子第一条
    var legacy = new AppSettings
    {
        CompanionPlayMode = CompanionPlayPolicy.Fixed,
        CompanionSelectedAction = "",
        CompanionBaseMode = "free_play",
        CompanionPool = ["game_flow", "gentle_wave"],
    };
    string legacyAction = CompanionPlayPolicy.ResolveFixedAction(legacy.CompanionSelectedAction, legacy.CompanionBaseMode, legacy.CompanionPool);
    Check("老配置（free_play + 池子）→ 固定模式钉池子第一条", legacyAction == "game_flow", legacyAction);
    legacy.Normalize();
    Check("老配置 Normalize 后补上固定动作", legacy.CompanionSelectedAction == "game_flow", legacy.CompanionSelectedAction);
    Check("老配置 Normalize 后播放方式是 fixed", legacy.CompanionPlayMode == CompanionPlayPolicy.Fixed, legacy.CompanionPlayMode);

    // 池子轮流：首尾循环、不加权不偏爱
    string[] pool = ["a", "b", "c"];
    Check("顺时针 a→b", CompanionPlayPolicy.NextInPool(pool, "a", +1) == "b");
    Check("顺时针末尾回开头 c→a（首尾循环）", CompanionPlayPolicy.NextInPool(pool, "c", +1) == "a");
    Check("逆时针开头回末尾 a→c（首尾循环）", CompanionPlayPolicy.NextInPool(pool, "a", -1) == "c");
    Check("当前不在池子里 → 顺时针从第一条进", CompanionPlayPolicy.NextInPool(pool, "zzz", +1) == "a");
    Check("当前不在池子里 → 逆时针从最后一条进", CompanionPlayPolicy.NextInPool(pool, "zzz", -1) == "c");
    Check("当前为空 → 顺时针从第一条进", CompanionPlayPolicy.NextInPool(pool, null, +1) == "a");
    Check("自建动作前缀不影响匹配", CompanionPlayPolicy.NextInPool(pool, "stroke:b", +1) == "c");
    Check("空池返回 null（交回动作页顺序）", CompanionPlayPolicy.NextInPool([], "a", +1) is null);
    Check("池子为 null 返回 null", CompanionPlayPolicy.NextInPool(null, "a", +1) is null);

    Near("稳定档恒等于 1（不呼吸）", CompanionPlayPolicy.VariationDepthScale("stable", 5.75), 1.0, 1e-12);
    Near("自然起伏在 23s 周期的峰值 +22%", CompanionPlayPolicy.VariationDepthScale("natural", 23.0 / 4), 1.22, 0.01);
    double minNat = double.MaxValue, maxNat = double.MinValue;
    for (double t = 0; t < 46; t += 0.1)
    {
        double v = CompanionPlayPolicy.VariationDepthScale("natural", t);
        minNat = Math.Min(minNat, v); maxNat = Math.Max(maxNat, v);
    }
    Check("自然起伏被夹在 0.7–1.3", minNat >= 0.699 && maxNat <= 1.301, $"{minNat:0.###}–{maxNat:0.###}");
    Check("自然起伏确实在起伏（不是恒 1）", maxNat - minNat > 0.3);

    Check("两路都关 + 伴随开 → 跑底色", CompanionPlayPolicy.DescribeRoute(false, false, true, false).Contains("底色"));
    Check("只用游戏信号 → 明确说在等", CompanionPlayPolicy.DescribeRoute(false, false, true, true).Contains("等"));
    Check("三路全关 → 不替手动/脚本发动作", CompanionPlayPolicy.DescribeRoute(false, false, false, false).Contains("三路都关"));
    Check("双开 → 说明是互证不是分工", CompanionPlayPolicy.DescribeRoute(true, true, false, false).Contains("互证"));
    Check("只开画面 → 跟画面", CompanionPlayPolicy.DescribeRoute(true, false, false, false).Contains("画面"));
    Check("只开声音 → 跟声音", CompanionPlayPolicy.DescribeRoute(false, true, false, false).Contains("声音"));

    // 速度与强度分离：只有 Speed 倍率变，深度倍率（稳定档）恒 1
    double[] speeds = [CompanionPlayPolicy.PaceScale("slow"), CompanionPlayPolicy.PaceScale("medium"), CompanionPlayPolicy.PaceScale("fast")];
    Check("三档速度各不相同", speeds[0] < speeds[1] && speeds[1] < speeds[2]);
}

// ── ④ 手动选招不被下一拍盖过 ───────────────────────────────────────────────
Section("④ 手动换招保护期（热键选的招不被自动节奏覆盖）");
{
    var seq = new AutoBehaviorSequencer(seed: 11);
    seq.Reset("gentle_wave", 1.0, profile);
    Check("Reset 后跑的是钉住的动作", seq.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0).Pattern == "gentle_wave");

    seq.SetSelection("deep_pulse");
    seq.HoldSelection(3.0);
    Check("刚选完就有保护期", seq.SelectionHoldActive);
    AutoBehaviorFrame frame = seq.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0);
    Check("手选的招立刻生效（不等下一拍）", frame.Pattern == "deep_pulse", frame.Pattern);
    bool stayed = true;
    for (int i = 0; i < 140; i++)   // 2.8s < 3s 保护期
    {
        frame = seq.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0);
        if (frame.Pattern != "deep_pulse") stayed = false;
    }
    Check("保护期内不被自动换招盖掉", stayed, frame.Pattern);

    for (int i = 0; i < 200; i++) frame = seq.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0);
    Check("保护期过后仍然是他选的那个动作", frame.Pattern == "deep_pulse", frame.Pattern);

    // free_play 自由游玩：保护期内不允许随机插招
    var seq2 = new AutoBehaviorSequencer(seed: 5);
    seq2.Reset("free_play", 1.0, profile);
    string first = seq2.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0).Pattern;
    seq2.HoldSelection(5.0);
    bool freeStayed = true;
    for (int i = 0; i < 240; i++)   // 4.8s
    {
        if (seq2.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0).Pattern != first) freeStayed = false;
    }
    Check("free_play 保护期内不自动换招", freeStayed, first);

    // 没有保护期时自由游玩会按时长换招（对照组：证明保护期确实起了作用）
    var seq3 = new AutoBehaviorSequencer(seed: 5);
    seq3.Reset("free_play", 1.0, profile);
    var seen = new HashSet<string>();
    for (int i = 0; i < 45 * 50; i++) seen.Add(seq3.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0).Pattern);
    Check("对照：无保护期时自由游玩 45s 内会换招", seen.Count > 1, string.Join(",", seen));
}

// ── ④b 池子轮流：按勾选顺序首尾循环，不加权、不随机插招 ─────────────────────
Section("④b 轮流模式的顺序与首尾循环");
{
    // 轮流是「自由巡游」那套的换招策略：池子在收尾时按勾选顺序往下走。
    string[] pool = ["climax_burst", "gentle_wave", "deep_pulse"];
    AutoBehaviorSequencer.SetPoolOverride(pool, rotation: true);
    Check("轮流模式已打开", AutoBehaviorSequencer.PoolRotation);
    var seq = new AutoBehaviorSequencer(seed: 3);
    seq.Reset("free_play", 1.0, profile);
    string prev = seq.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0).Pattern;
    Check("第一次就从池子第一条起跑", prev == pool[0], prev);
    var changes = new List<string>();
    for (int i = 0; i < 4000 && changes.Count < 4; i++)
    {
        string p = seq.Step(Dt, 1.0, Amps, 1.0, 0.4, profile, 0, 0, 0).Pattern;
        if (p == prev) continue;
        changes.Add($"{prev}→{p}");
        prev = p;
    }
    bool inOrder = changes.Count >= 3;
    foreach (string change in changes)
    {
        string[] parts = change.Split('→');
        int from = Array.IndexOf(pool, parts[0]);
        int to = Array.IndexOf(pool, parts[1]);
        if (from < 0 || to < 0 || to != (from + 1) % pool.Length) inOrder = false;
    }
    Check("每一步都是池子里的下一个，末位绕回首位", inOrder, string.Join("，", changes));

    // 关掉轮流 → 回到加权抽样（池子还在，但不再严格按顺序）
    AutoBehaviorSequencer.SetPoolOverride(["climax_burst", "gentle_wave"], rotation: false);
    Check("关掉轮流后不再报告顺序模式", !AutoBehaviorSequencer.PoolRotation);
    AutoBehaviorSequencer.SetPoolOverride(null);
    Check("清空池子后覆盖也清掉", AutoBehaviorSequencer.PoolOverride.Count == 0 && !AutoBehaviorSequencer.PoolRotation);
}

// ── ⑤ 挣扎连点 ────────────────────────────────────────────────────────────
Section("⑤ 挣扎连点：边沿、长按、切窗、灵敏度、回落");
{
    var s = new StruggleAccelerator { Enabled = true, Sensitivity = "standard", Feedback = 1.0 };
    double t = 0;
    s.SetForeground(true);
    int counted = 0;
    for (int i = 0; i < 6; i++)
    {
        if (s.OnKeyEdge(0x57, true, t, true, false)) counted++;       // 按下
        s.OnKeyEdge(0x57, false, t + 0.02, true, false);              // 松开
        t += 0.12;
    }
    Check("同键连按：按下-松开才算一个点", counted == 6, counted.ToString());
    s.Advance(t);
    Check("连点后等级上升", s.Level > 0.3, s.Level.ToString("0.##"));
    Check("只加节奏不换强度口径：SpeedScale>1 且 DepthBoost>0", s.SpeedScale > 1 && s.DepthBoost > 0);

    var hold = new StruggleAccelerator { Enabled = true };
    hold.SetForeground(true);
    Check("长按的第一个边沿算一点", hold.OnKeyEdge(0x57, true, 0, true, false));
    Check("系统长按重复的 down 不算新点", !hold.OnKeyEdge(0x57, true, 0.3, true, false));
    Check("长按重复（更久）仍然不算", !hold.OnKeyEdge(0x57, true, 1.5, true, false));

    var inj = new StruggleAccelerator { Enabled = true };
    inj.SetForeground(true);
    Check("全局热键/模拟输入不算", !inj.OnKeyEdge(0x57, true, 0, true, injected: true));

    var bg = new StruggleAccelerator { Enabled = true };
    bg.SetForeground(false);
    Check("不在绑定游戏前台（切窗打字）不算", !bg.OnKeyEdge(0x57, true, 0, false, false));

    var held = new StruggleAccelerator { Enabled = true };
    held.SetForeground(true, [0x57]);        // 切回前台时 W 还按着
    Check("切回时已按住的键不算新点", !held.OnKeyEdge(0x57, true, 0.1, true, false));
    held.OnKeyEdge(0x57, false, 0.2, true, false);
    Check("松开后再按才算", held.OnKeyEdge(0x57, true, 0.4, true, false));

    var alt = new StruggleAccelerator { Enabled = true };
    alt.SetForeground(true);
    int altCount = 0;
    double at = 0;
    for (int i = 0; i < 4; i++)
    {
        int key = i % 2 == 0 ? 0x57 : 0x41;
        if (alt.OnKeyEdge(key, true, at, true, false)) altCount++;
        alt.OnKeyEdge(key, false, at + 0.03, true, false);
        at += 0.1;
    }
    Check("不同键交替也计入", altCount == 4, altCount.ToString());

    var tooFast = new StruggleAccelerator { Enabled = true };
    tooFast.SetForeground(true);
    tooFast.OnKeyEdge(0x57, true, 0, true, false);
    tooFast.OnKeyEdge(0x57, false, 0.01, true, false);
    Check("抖动级间隔（<35ms）不算新点", !tooFast.OnKeyEdge(0x57, true, 0.02, true, false));

    // 灵敏度：high 两下就起，low 要四下
    var high = new StruggleAccelerator { Enabled = true, Sensitivity = "high" };
    high.SetForeground(true);
    for (int i = 0; i < 2; i++)
    {
        high.OnKeyEdge(0x57, true, i * 0.2, true, false);
        high.OnKeyEdge(0x57, false, i * 0.2 + 0.05, true, false);
    }
    double highLevel = high.Advance(0.45);
    var low = new StruggleAccelerator { Enabled = true, Sensitivity = "low" };
    low.SetForeground(true);
    for (int i = 0; i < 2; i++)
    {
        low.OnKeyEdge(0x57, true, i * 0.2, true, false);
        low.OnKeyEdge(0x57, false, i * 0.2 + 0.05, true, false);
    }
    double lowLevel = low.Advance(0.45);
    Check("高灵敏度两下就起来", highLevel > lowLevel, $"{highLevel:0.##} vs {lowLevel:0.##}");
    Check("低灵敏度两下还没起来", lowLevel < highLevel);

    // 漏一拍宽容：允许间隔内的断档不清空
    var tol = new StruggleAccelerator { Enabled = true, Sensitivity = "standard" };
    tol.SetForeground(true);
    for (int i = 0; i < 3; i++)
    {
        tol.OnKeyEdge(0x57, true, i * 0.3, true, false);
        tol.OnKeyEdge(0x57, false, i * 0.3 + 0.05, true, false);
    }
    Check("漏一拍（0.3 间隔 → 0.6 断档，仍在宽容期内）仍算连续", tol.OnKeyEdge(0x57, true, 1.2, true, false));

    // 结束平滑回落
    double peak = s.Level;
    s.Advance(t + 0.6);
    double afterDelay = s.Level;
    for (int i = 0; i < 40; i++) s.Advance(t + 0.6 + i * 0.05);
    Check("停手后延迟一会儿才开始降（不是突然断）", afterDelay <= peak);
    Check("停手后平滑降到 0", s.Level == 0, s.Level.ToString("0.###"));

    var zeroFb = new StruggleAccelerator { Enabled = true, Feedback = 0 };
    zeroFb.SetForeground(true);
    for (int i = 0; i < 6; i++)
    {
        zeroFb.OnKeyEdge(0x57, true, i * 0.1, true, false);
        zeroFb.OnKeyEdge(0x57, false, i * 0.1 + 0.03, true, false);
        zeroFb.Advance(i * 0.1 + 0.05);
    }
    Check("反馈强度 0 → 只有节奏没有加深", Math.Abs(zeroFb.DepthBoost) < 1e-9 && zeroFb.SpeedScale >= 1.0,
        $"speed {zeroFb.SpeedScale:0.###} depth {zeroFb.DepthBoost:0.###}");

    var off = new StruggleAccelerator { Enabled = false };
    off.SetForeground(true);
    Check("关掉后完全不响应", !off.OnKeyEdge(0x57, true, 0, true, false) && off.Advance(1) == 0);

    var left = new StruggleAccelerator { Enabled = true };
    left.SetForeground(true);
    for (int i = 0; i < 6; i++)
    {
        left.OnKeyEdge(0x57, true, i * 0.1, true, false);
        left.OnKeyEdge(0x57, false, i * 0.1 + 0.03, true, false);
        left.Advance(i * 0.1 + 0.05);
    }
    double before = left.Level;
    left.SetForeground(false);     // 切窗
    Check("切走时立刻回落，切回来不会把连点带回来", left.Level < before, $"{before:0.##} → {left.Level:0.##}");
    Check("切走后手上不留连点记录", left.Advance(2) < before);

    // 每游戏灵敏度 / 开关：老配置 null = 跟全局
    var (enabledLegacy, sensLegacy, fbLegacy) = CompanionPlayPolicy.ResolveGameStruggle(null, true, "high", 0.8);
    Check("没有游戏配置 → 跟全局", enabledLegacy && sensLegacy == "high" && Math.Abs(fbLegacy - 0.8) < 1e-9,
        $"{enabledLegacy}/{sensLegacy}/{fbLegacy:0.##}");
    var legacyProfile = new GameProfile { Process = "old.exe" };   // 老配置：三个字段都是 null/空
    var (enabledOld, sensOld, fbOld) = CompanionPlayPolicy.ResolveGameStruggle(legacyProfile, true, "low", 1.2);
    Check("老配置缺字段 = 跟全局（不能当成关掉挣扎）", enabledOld && sensOld == "low" && Math.Abs(fbOld - 1.2) < 1e-9,
        $"{enabledOld}/{sensOld}/{fbOld:0.##}");
    var ownProfile = new GameProfile { Process = "new.exe", StruggleEnabled = false, StruggleSensitivity = "high", StruggleFeedback = 1.9 };
    var (enabledOwn, sensOwn, fbOwn) = CompanionPlayPolicy.ResolveGameStruggle(ownProfile, true, "low", 0.5);
    Check("这个游戏自己关了挣扎", !enabledOwn);
    Check("这个游戏自己的灵敏度", sensOwn == "high", sensOwn);
    Near("反馈强度夹到 1.5", fbOwn, 1.5, 1e-9);
}

// ⑥ 不盲从鼓点（伴随 / 底色路）+ 画面/声音开关保留
Section("⑥ 鼓点压制 + 两条路的自定义开关");
{
    RhythmHint slowSubject = AudioRhythmPolicy.Resolve(AudioSceneKind.Impact, true, 0.8, 3.0);
    Check("主体 0.8Hz 时先取主体而不是 3.0Hz 的鼓点", slowSubject.Valid && Math.Abs(slowSubject.Hz - 0.8) < 1e-9, slowSubject.ToString());
    double damped = AudioRhythmPolicy.BeatDamping(slowSubject, 3.0);
    Near("配乐 3.0Hz、主体 0.8Hz → 鼓点推动只剩约 38%", damped, 1 - (1 - 0.8 / 3.0) * 0.85, 1e-9);
    Check("压制后确实明显变小", damped < 0.45, damped.ToString("0.###"));
    Near("主体和配乐一样快 → 不压", AudioRhythmPolicy.BeatDamping(AudioRhythmPolicy.Resolve(AudioSceneKind.Music, true, 2.8, 3.0), 3.0), 1, 1e-9);
    Near("没有节奏提示 → 不压", AudioRhythmPolicy.BeatDamping(RhythmHint.None, 3.0), 1, 1e-9);
    Near("配乐 BPM 锁不住 → 不压", AudioRhythmPolicy.BeatDamping(slowSubject, 0), 1, 1e-9);
    Near("只听得见配乐（置信度低）→ 压得轻", AudioRhythmPolicy.BeatDamping(new RhythmHint(true, 0.8, 0.35, "只有配乐可用（已放缓）"), 3.0),
        1 - (1 - 0.8 / 3.0) * 0.35, 1e-9);
    Check("画面遮挡但声音清楚：融合后的节奏跟声音走", Math.Abs(
        SignalFusion.Fuse(new SignalLane(true, 12, 0.5, 0.25), new SignalLane(true, 85, 1.3, 0.9), true, true).Hz - (1.3 * 0.88 + 0.5 * 0.12)) < 1e-6);

    // 画面 / 声音各自的自定义开关：默认由内容决定，勾了才用手设值，取消不覆盖保存值。
    var cfg = new AppSettings();
    cfg.ScreenSyncDepth = 80; cfg.ScreenSyncSpeed = 2.2;
    cfg.AudioDepth = 25; cfg.AudioSpeed = 0.6;
    Check("画面没勾自定义 → 用内容决定的 50 / 1.0",
        cfg.ScreenDepthEffective == 50 && Math.Abs(cfg.ScreenSpeedEffective - 1.0) < 1e-9,
        $"{cfg.ScreenDepthEffective}/{cfg.ScreenSpeedEffective}");
    Check("声音没勾自定义 → 用内容决定的 50 / 1.0",
        cfg.AudioDepthEffective == 50 && Math.Abs(cfg.AudioSpeedEffective - 1.0) < 1e-9,
        $"{cfg.AudioDepthEffective}/{cfg.AudioSpeedEffective}");
    cfg.ScreenSyncCustom = true; cfg.AudioCustom = true;
    Check("勾了才用该路手设值",
        cfg.ScreenDepthEffective == 80 && Math.Abs(cfg.ScreenSpeedEffective - 2.2) < 1e-9
        && cfg.AudioDepthEffective == 25 && Math.Abs(cfg.AudioSpeedEffective - 0.6) < 1e-9,
        $"{cfg.ScreenDepthEffective}/{cfg.ScreenSpeedEffective} · {cfg.AudioDepthEffective}/{cfg.AudioSpeedEffective}");
    cfg.ScreenSyncCustom = false; cfg.AudioCustom = false;
    Check("取消勾选回到内容决定，但保存值没被覆盖",
        cfg.ScreenDepthEffective == 50 && cfg.AudioDepthEffective == 50
        && cfg.ScreenSyncDepth == 80 && cfg.AudioDepth == 25,
        $"{cfg.ScreenSyncDepth}/{cfg.AudioDepth}");
}

// ⑦ 三路开关与「只用游戏信号就等着」
Section("⑦ 两路都关跑底色 / 只用游戏信号就等");
{
    var cfg = new AppSettings
    {
        CompanionProcess = "game.exe",
        CompanionSource = "auto",
        ScreenWatchEnabled = false,
        AudioReactiveEnabled = false,
        CompanionAuto = true,
        CompanionSelectedAction = "deep_pulse",
        CompanionPlayMode = CompanionPlayPolicy.Fixed,
        CompanionReaction = "sound",
    };
    GameCompanionRules.Apply(cfg);
    RuleConfig? baseRule = cfg.Rules.FirstOrDefault(r => r.Id == "companion-base");
    Check("两路都关、伴随开 → 有底色规则在跑", baseRule is { Enabled: true }, baseRule?.Mode);
    Check("底色就是用户选的那一个动作（默认固定）", baseRule?.Mode == "deep_pulse", baseRule?.Mode);
    Check("来源不是「只用游戏信号」时声音兜底仍然在", cfg.Rules.Any(r => r.Id == "companion-reaction" && r.Enabled));

    cfg.CompanionSource = "telemetry";
    GameCompanionRules.Apply(cfg);
    baseRule = cfg.Rules.FirstOrDefault(r => r.Id == "companion-base");
    Check("只用游戏信号 → 不再拿底色顶上（等信号）", baseRule is { Enabled: false });
    Check("只用游戏信号 → 声音兜底也不启用", !cfg.Rules.Any(r => r.Id == "companion-reaction" && r.Enabled));

    // 伴随便着「轮流」且池子非空 → 才轮到池子；固定时池子留着也不用
    cfg.CompanionSource = "auto";
    cfg.CompanionPlayMode = CompanionPlayPolicy.Pool;
    cfg.CompanionPool = ["climax_burst", "gentle_wave"];
    GameCompanionRules.Apply(cfg);
    baseRule = cfg.Rules.FirstOrDefault(r => r.Id == "companion-base");
    Check("选了轮流 → 走自由巡游（按池子顺序）", baseRule?.Mode == "free_play", baseRule?.Mode);

    cfg.CompanionPlayMode = CompanionPlayPolicy.Fixed;
    GameCompanionRules.Apply(cfg);
    baseRule = cfg.Rules.FirstOrDefault(r => r.Id == "companion-base");
    Check("固定时池子留着也不插招", baseRule?.Mode == "deep_pulse", baseRule?.Mode);
}

Console.WriteLine($"\n通过 {pass} 项，失败 {fails.Count} 项");
if (fails.Count > 0)
{
    Console.WriteLine("失败明细：");
    foreach (string f in fails) Console.WriteLine(" - " + f);
    return 1;
}
Console.WriteLine("全部通过");
return 0;
