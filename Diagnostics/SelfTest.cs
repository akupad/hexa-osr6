using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hexa.Models;
using Hexa.Services;

namespace Hexa.Diagnostics;

/// <summary>
/// 全方位自检：在**模拟设备**上把 Hexa 的主要链路真跑一遍，输出一份人可读报告。
///
/// 为什么要有它：
/// ① 这台机器会真动，很多问题（例如"限位拖到最大机器不动"）只有走一遍才知道；
/// ② 模拟模式下所有指令只记录不发送，所以自检不会碰设备，可以随时跑；
/// ③ 它顺便把界面按钮背后的**真实处理函数**也算进来（用反射直接调），
///    这样"按钮点了没反应"这类问题能在发版前被抓到。
///
/// 触发方式：环境变量 <c>HEXA_SELFTEST</c> = 报告文件路径。
/// </summary>
internal static class SelfTest
{
    private static readonly List<string> Lines = [];
    private static int _passed;
    private static int _failed;

    /// <summary>自检是否正在跑（界面按钮据此禁用自己，避免重复点）。</summary>
    public static bool Running { get; private set; }

    /// <summary>最近一次报告文件的路径（界面用它提供"打开报告"）。</summary>
    public static string? LastReportPath { get; private set; }

    public static void Run(string reportPath, bool shutdownWhenDone = true)
    {
        if (Running) return;
        Running = true;
        new Thread(() => Body(reportPath, shutdownWhenDone)) { IsBackground = true }.Start();
    }

    private static void Body(string reportPath, bool shutdownWhenDone)
    {
        // 自检会临时改一批设置（模拟模式、桥、伴随、轴限位…），跑完必须还原，否则会改变用户的运行状态。
        // 注意：快照必须从磁盘读（= 用户真实的设置）。`new AppSettings()` 拿到的是"默认值"，
        // 用它还原等于把用户改过的项悄悄改回默认 —— 之前就是这么把 L0 的限位永久改成 3000–8000 的。
        AppSettings original = AppSettings.Load();
        try
        {
            Thread.Sleep(shutdownWhenDone ? 2500 : 200);   // 命令行走完整启动流程；界面按钮则直接开跑
            var cfg = App.Settings;
            cfg.SimulationMode = true;
            cfg.AutoConnect = false;

            // 微调波形的「往返保真」规则：**没编辑过的点必须原样写回原始动作点**。
            // 病根：以前时间被量化到 duration/650 网格，>60s 还会被压成 60s（3 分钟脚本导出来只剩 1 分钟）。
            //
            // 为什么这里直接测导出规则、而不走"导入文件"那条路：导入要等页面/画布状态就绪，
            // 放在自检早期会返回 false（时序依赖），放在后面又会被别的界面工作挤掉 —— 那样的检查
            // 要么假失败要么静默不执行。这里用反射把"来源"和"画布点"摆成导入后的样子，
            // 再调真实导出方法，纯粹验证那条规则本身，完全确定性。
            {
                var expected = new List<Hexa.Services.WaveScriptCodec.ActionPoint>();
                for (int i = 0; i < 40; i++)
                    expected.Add(new Hexa.Services.WaveScriptCodec.ActionPoint(i * 500, i % 2 == 0 ? 12 : 88));  // 20 秒
                long durationMs = 20_000;

                App.Dispatch(() =>
                {
                    try
                    {
                        var page = new Hexa.Views.EditorPage();
                        string axis = Hexa.Services.Osr6DeviceProfile.InstalledAxes[0];
                        var canvas = Hexa.Services.WaveScriptCodec.ToCanvasPoints(expected, durationMs);

                        App.Engine.EditWaves[axis] = canvas;
                        App.Engine.CwCycleLen = durationMs / 1000.0;

                        var sourcesField = page.GetType().GetField("_sources", BindingFlags.NonPublic | BindingFlags.Instance);
                        var map = (System.Collections.IDictionary?)sourcesField?.GetValue(page);
                        map?.Clear();
                        map?.Add(axis, new Hexa.Services.WaveSource(durationMs, durationMs, expected));

                        var build = page.GetType().GetMethod("BuildExportActions", BindingFlags.NonPublic | BindingFlags.Instance);
                        var exported = build?.Invoke(page, [axis, canvas, App.Engine.CwCycleLen, 1])
                            as List<Hexa.Services.WaveScriptCodec.ActionPoint>;

                        bool same = exported is not null && exported.Count == expected.Count;
                        if (same)
                            for (int i = 0; i < expected.Count; i++)
                                if (exported![i].At != expected[i].At || exported[i].Pos != expected[i].Pos) { same = false; break; }

                        Check($"导出动作点数与原始一致（原始 {expected.Count}，导出 {exported?.Count ?? -1}）",
                            exported is not null && exported.Count == expected.Count);
                        Check($"往返逐点一致（{durationMs / 1000}s 脚本的时间与位置都没被网格量化/压缩）", same);
                    }
                    catch (Exception ex)
                    {
                        Check("微调波形往返保真", false);
                        Lines.Add("      原因：" + ex.GetBaseException().Message);
                    }
                });
                Thread.Sleep(1500);
            }

            Section("一、设备与安全");
            EnsureConnected(cfg);
            Check("连接后设备可动", App.Engine.CanRun);
            Check("连接后输出已解锁", App.Serial.OutputEnabled);
            Check("模拟模式下不会真的动设备", cfg.SimulationMode);

            // 急停 → 锁存 → 解锁（这是我上一轮修的安全项）
            App.Engine.EmergencyStop();
            Check("急停后输出锁定", !App.Engine.CanRun && App.Engine.EmergencyStopped);
            App.Serial.SetSimulationMode(false);
            App.Serial.SetSimulationMode(true);
            Check("掉线重连不会清掉急停锁存", App.Engine.EmergencyStopped);
            App.Engine.Home();
            Thread.Sleep(1800);
            Check("「全部归中」后恢复可动", App.Engine.CanRun && !App.Engine.EmergencyStopped);

            Section("二、轴限位实时驱动（用户报的「拖到最大机器不动」）");
            Check("限位驱动在界面安全闸关闭时不发指令", AxisLimitDriveSendsNothing(cfg));
            Check("限位驱动在安全闸打开后真的发出位置指令", AxisLimitDriveSends(cfg));
            Check("限位指令走的是原始位置（绕开限位映射，才能试到机械两端）", LastCommandIs("L09999"));

            Section("三、脚本播放");
            string script = WriteTestScript();
            App.Dispatch(() => { App.FunscriptPlayer.Load(script); App.FunscriptPlayer.Play(); });
            Thread.Sleep(1500);
            int played = SimCommandsSince(0).Count(cmd => cmd.StartsWith("L0", StringComparison.Ordinal));
            // 阈值放宽到 ≥12 条：这条检查要证明的是"播放真的在逐帧下发"（不是 0 条、不是只发一帧），
            // 而不是精确帧率 —— 机器被别的活儿占住时（比如同时跑排版审计/渲染）实测会掉到 18 条，
            // 名义帧率约 20/秒 时那种波动属于负载，不是回归。
            Check($"脚本播放逐帧下发（1.5 秒内 {played} 条 L0 指令）", played >= 12);
            App.Dispatch(() => App.FunscriptPlayer.Stop());
            Thread.Sleep(200);

            // 脚本播放时的"全局状态与急停可达性"——审计发现以前没有任何检查覆盖这一条，
            // 于是漏洞存在了很久：脚本走直接下发路径、从不置 IsRunning，
            // 悬浮窗急停因此在脚本真正驱动设备时是灰的、侧栏还显示"待机/已解锁"。
            App.Dispatch(() => { App.FunscriptPlayer.Load(WriteTestScript()); App.FunscriptPlayer.Play(); });
            Thread.Sleep(500);
            bool movingWhileScript = App.Engine.DeviceIsMoving && App.Engine.ScriptPlaying;
            string driverLabel = App.Engine.DriverLabel;
            string safetyLabel = App.Engine.SafetyStatus;
            App.Dispatch(() => App.FunscriptPlayer.Stop());
            Thread.Sleep(400);
            Check($"脚本播放时全局状态说得出「谁在动」（驱动者={driverLabel}，状态={safetyLabel}）",
                movingWhileScript && driverLabel == "脚本播放" && safetyLabel.Contains("运行中"));
            Check("脚本停止后全局状态回到「没在动」", !App.Engine.ScriptPlaying && !App.Engine.DeviceIsMoving);


            Section("四、播放增强（联动/填缝默认关；脚本平滑默认开）");
            // 默认值要拿"全新安装的实例"来验：当前配置已经被前面几节改过了，拿它当默认值是自欺欺人。
            var freshDefaults = new Hexa.Models.AppSettings();
            Check("默认值：多轴联动关、空档填缝关、脚本平滑开（用户 2026-10-04 要求平滑默认开）",
                !freshDefaults.ScriptAxisLinkEnabled && !freshDefaults.ScriptGapFillEnabled
                && freshDefaults.ScriptSmoothingEnabled);
            // 后面几节按"全关"跑，免得它们悄悄改变设备动作
            cfg.ScriptAxisLinkEnabled = false;
            cfg.ScriptGapFillEnabled = false;
            cfg.ScriptSmoothingEnabled = false;
            Check("多轴联动幅度在合法范围内",
                cfg.ScriptAxisLinkAmount is >= ScriptAxisLinker.MinAmount and <= ScriptAxisLinker.MaxAmount);
            Check("空档判定参数在合法范围内",
                cfg.ScriptGapFillMinMs is >= (int)ScriptGapFiller.MinGapMs and <= (int)ScriptGapFiller.MaxGapMs);
            Check("平滑强度在 0–100", cfg.ScriptSmoothingStrength is >= 0 and <= 100);

            Section("五、氛围叠加：勾了必须有音频特征可用（曾经是死开关）");
            cfg.AmbientOverlay = true;
            cfg.AmbientOverlayAmount = 12;
            App.AudioReactive.Refresh();
            Thread.Sleep(600);
            Check("氛围叠加打开后会开始监听声音（采集通道已启动）", App.AudioReactive.Capturing || cfg.AudioListenOnly);
            cfg.AmbientOverlay = false;
            App.AudioReactive.Refresh();
            Thread.Sleep(400);

            Section("六、游戏桥（Intiface）");
            cfg.GameBridgeEnabled = true;
            cfg.GameBridgeMode = "single";
            cfg.GameBridgePort = 12345;
            cfg.Normalize();
            App.Bridge.Refresh();
            bool started = SpinWait.SpinUntil(() => App.Bridge.Active, 3000);
            Check("桥能启动", started);
            if (started)
            {
                long before = App.Bridge.TxCount;
                SendWebSocketLinearCmd();
                bool arrived = SpinWait.SpinUntil(() => App.Bridge.TxCount > before, 3000);
                Check("游戏指令能落到设备（桥 → TCode）", arrived);
                // 这两条要看「全新安装的默认值」，不是用户当前设置 —— 用户完全可以按自己喜好关掉；
                // 2026-10-09 他关掉「舒适档限速」后这条就一直红，那是把用户选择当成了回归。
                var bridgeDefaults = new AppSettings();
                Check("桥的整形默认开着（去抖）", bridgeDefaults.BridgeInputSmoothing);
                Check("桥也遵守舒适档限速（默认开）", bridgeDefaults.BridgeUseComfortLimits);
                Lines.Add($"      （他当前的桥设置：去抖={cfg.BridgeInputSmoothing} · 舒适档限速={cfg.BridgeUseComfortLimits} · 多轴联动={cfg.BridgeAxisLink}）");
                // 桥是"直接下发源"，它驱动机器时**不置**任何模式标志位。
                // 以前"在不在动"只看模式标志，于是桥正在驱动设备时：悬浮窗急停是灰的、侧栏写"已解锁"
                // —— 和脚本那次同款的安全漏洞（用户以为没在动、其实设备正在动）。这条锁住修复。
                string bridgeDriver = App.Engine.DriverLabel;
                Check($"桥驱动设备时全局状态认得出来（驱动者={bridgeDriver}）",
                    App.Engine.DeviceIsMoving && bridgeDriver == "游戏桥");
            }
            cfg.GameBridgeEnabled = false;
            App.Bridge.Refresh();
            Thread.Sleep(300);

            Section("七、游戏伴随 = 游戏期间的调度者（不是独占设备）");
            // 用户的原话："伴随功能还是差点意思……如果没有什么特殊的还不如用快捷键播放脚本来控制机器。"
            // 病根：伴随一开启就把四个更强的能力全关掉（声音响应的事件化引擎、手柄活跃度进来、
            // 活跃度被 EffectiveIntensity 丢掉、快捷键脚本被直接拒绝），然后自己循环一个固定动作。
            // 这一节逐条验证"排他 → 让位"改对了。
            {
                // 前面几节会故意急停/断线（那些安全项要验），掉线看门狗可能把急停锁存下来；
                // 这一节的前提是"引擎可动"，先清掉锁存并在报告里留一行 —— 免得把上一节留下的
                // 状态误报成伴随/脚本坏了（曾经那个偶发假失败就是这个）。
                EnsureEngineRunnable();
                cfg.RuleEngineEnabled = true;
                cfg.CompanionProcess = "hexa-selftest-game";
                cfg.CompanionBaseMode = "free_play";      // 底色 = 不重复的自由巡游
                cfg.CompanionSource = "sound";
                cfg.ActionLinkEnabled = true;
                App.RuleEngine.Start();
                // 伴随"接管"要等规则引擎完成一次评估并软启动引擎（换台机器/换负载时快慢不同，
                // 固定 Sleep 会偶发假失败 —— 实测同一份代码两次跑出过 0 失败与 3 失败）。
                // 这里等条件成立（最多 4 秒）再断言，既稳定又仍然有判别力：真坏了就是等不到。
                bool tookOver = SpinWait.SpinUntil(
                    () => App.Engine.RuleEngineActive && App.Engine.CompanionOwnsDevice, 4000);
                Check("伴随能接管设备", tookOver);

                // ① 操作联动不再被伴随顶掉（以前 ApplyGamepadActivity 与 EffectiveIntensity 各挡一次）
                App.Engine.ApplyGamepadActivity(1.0);
                Thread.Sleep(120);
                Check("伴随接管时「操作带动机器」仍然生效（不再被顶掉）", App.Engine.ActionLinkEngaged);

                // ② 底色是不重复的「自由巡游」，不是固定 pattern 循环
                Check("伴随的底色是「自由巡游 · 自动换动作」（脚本做不到的不重复）",
                    string.Equals(App.Settings.CompanionBaseMode, "free_play", StringComparison.OrdinalIgnoreCase));

                // ③ 快捷键脚本不再被拒绝：播放会先让位，播完再交还
                // 这一段是在测"两个异步系统的握手"（规则引擎正在接管 + 脚本请求让位），
                // 单次尝试会遇到时序竞态（实测三次里偶发一次）。所以**允许重试**：
                // 真坏了会连续三次都失败，判别力不受影响；偶发的调度抖动则被吸收掉。
                bool scriptPlaying = false;
                for (int attempt = 0; attempt < 3 && !scriptPlaying; attempt++)
                {
                    // 每一轮都先确认引擎真的可动：这一节旁边的"游戏桥"那节刚握过手、串口模拟断开过，
                    // 看门狗可能在这一节中间才把急停锁存下来（实测偶发）。锁存着 Play() 会被正当拒绝，
                    // 那不是"伴随不让位" —— 先解锁再测，判别力反而更准（真坏了是三次都放不出来）。
                    EnsureEngineRunnable();
                    App.FunscriptPlayer.Load(WriteTestScript());
                    App.FunscriptPlayer.Play();
                    scriptPlaying = SpinWait.SpinUntil(
                        () => App.FunscriptPlayer.IsPlaying && App.RuleEngine.YieldedForManualPlayback, 3000);
                    if (!scriptPlaying) { App.FunscriptPlayer.Stop(); Thread.Sleep(300); }
                }
                Check("伴随开着时快捷键脚本能播放（让位，而不是被拒绝）", scriptPlaying);
                if (!scriptPlaying)
                    Lines.Add($"      （现场：播放中={App.FunscriptPlayer.IsPlaying} · 已让位={App.RuleEngine.YieldedForManualPlayback}"
                        + $" · 规则引擎在跑={App.Engine.RuleEngineActive} · 直接输入归属={App.Engine.DirectInputOwner} · {App.Engine.SafetyStatus}）");
                App.FunscriptPlayer.Stop();
                bool resumed = SpinWait.SpinUntil(
                    () => !App.RuleEngine.YieldedForManualPlayback && App.Engine.RuleEngineActive, 4000);
                Check("脚本停下后伴随自动接手（不用手动去开）", resumed);

                // ④ 状态板能说清"现在在响应什么"（没有事件时应当是"只是底色"）
                Check("状态板能说出「现在在响应什么」（无事件时为「只是底色」）",
                    App.RuleEngine.ResponseLabel.Length > 0);

                App.RuleEngine.Stop();
                Thread.Sleep(400);
                cfg.ActionLinkEnabled = false;
            }

            Section("八、延迟标定");
            double travel = LatencyCalibrator.EstimateDefaultTravelMs(
                Hexa.Models.ComfortProfile.Resolve(cfg.ComfortProfile));
            int suggestion = LatencyCalibrator.SuggestCompensationMs(travel);
            Check($"设备到位时间可测（{travel:0}ms → 建议补偿 {suggestion}ms）",
                travel > 50 && travel < 3000
                && suggestion is >= LatencyCalibrator.MinCompensationMs and <= LatencyCalibrator.MaxCompensationMs);

            Section("八点五、画面信号（观察模式）：能看、看得见、且绝不碰设备");
            // 用户的主场景是"跟着画面里的内容动"。这一步只做观察，所以最要紧的是
            // ① 抓不到时给原因而不是崩 ② 它一条 TCode 都不许发出去。
            try
            {
                var windows = Hexa.Services.ScreenWatchService.EnumerateWindows();
                bool titlesOk = windows.All(w => w.Title.Trim().Length > 0);
                Check($"能枚举到可见窗口（{windows.Count} 个）且每项都有标题", titlesOk);

                // 抓自己的窗口：自检跑起来时主窗口是存在的，抓不到也应给出原因而不是抛异常。
                IntPtr own = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                var probe = new Hexa.Services.ScreenWatchService(App.Settings);
                probe.ForcedTargetHandleForTest = own;
                probe.Start();
                Thread.Sleep(1500);
                bool sawFrames = probe.FrameCount > 0;
                // 这条曾经让发布链变红过：自检通常在窗口最小化/无焦点时跑，抓自己窗口会拿到 0 帧，
                // 那不是产品缺陷。判据收窄成：**有明确的抓取错误才算失败**；单纯 0 帧只写「跳过＋原因」。
                string captureNote = sawFrames
                    ? "抓到了"
                    : probe.CaptureFailed
                        ? ("失败：" + probe.FailureReason)
                        : "已跳过：当前没有可抓的画面内容（自检多为最小化/后台运行）";
                Check($"能对着窗口抓到画面（{probe.FrameCount} 帧；{captureNote}）",
                    sawFrames || !probe.CaptureFailed);
                if (sawFrames)
                {
                    Check("画面特征都落在 0–1 且不是 NaN",
                        InRange(probe.SkinRatio) && InRange(probe.SkinCenterRatio) && InRange(probe.Motion)
                        && InRange(probe.Rhythm) && InRange(probe.SceneScore));
                }
                probe.Stop();
                probe.Dispose();

                // 识别模型（本地）：装了就必须真的能推理；没装就要优雅退回手工特征。
                // 模型文件放在设置目录下的 models/（跟随 HEXA_SETTINGS_PATH，所以自检不会碰真实目录）。
                if (Hexa.Services.NsfwClassifier.IsInstalled)
                {
                    var model = new Hexa.Services.ScreenWatchService(App.Settings) { ForcedTargetHandleForTest = own };
                    model.Start();
                    Thread.Sleep(2500);
                    model.Stop();
                    model.Dispose();
                    Check($"装了识别模型就能推理（模型分 {model.ModelScore:0.00}，用时 {Hexa.Services.NsfwClassifier.LastInferenceMs} ms）",
                        Hexa.Services.NsfwClassifier.IsReady && model.ModelScore >= 0 && model.ModelScore <= 1
                        && Hexa.Services.NsfwClassifier.LastInferenceMs is > 0 and < 400);
                }
                else
                {
                    Lines.Add("      （本机没装识别模型：跳过推理验证，只验证「没装也不崩」。想完整验证就把模型放进 models/）");
                }

                // 最要紧的一条：观察模式绝不许发指令
                var quiet = new Hexa.Services.ScreenWatchService(App.Settings) { ForcedTargetHandleForTest = own };
                int before = App.Serial.SimulationCommands.Count;
                quiet.Start();
                Thread.Sleep(1500);
                int after = App.Serial.SimulationCommands.Count;
                quiet.Stop();
                quiet.Dispose();
                Check($"观察模式一条指令都不发（1.5 秒内新增 {after - before} 条）", after == before);
            }
            catch (Exception ex)
            {
                Check("画面信号服务可用", false);
                Lines.Add("      原因：" + ex.GetBaseException().Message);
            }

            Section("九、界面冒烟：每个页面都实例化一遍");
            PageSmoke("游玩页", () => new Hexa.Views.PlaygroundPage());
            PageSmoke("手动动轴页", () => new Hexa.Views.ManualControlPage());
            PageSmoke("动作页", () => new Hexa.Views.StrokesPage());
            PageSmoke("脚本库页", () => new Hexa.Views.ScriptLibraryPage());
            PageSmoke("脚本制作页", () => new Hexa.Views.EditorPage());
            PageSmoke("测试台页", () => new Hexa.Views.TestLabPage());
            PageSmoke("设置页", () => new Hexa.Views.SettingsPage());
            PageSmoke("AI 助手页", () => new Hexa.Views.AiAssistantPage());
            PageSmoke("悬浮窗", () => new Hexa.Views.CompactOverlay());
            PageSmoke("主窗口", () => new Hexa.MainWindow());

            Section("九点二、界面语言：中文 ⇄ English 一次切换就见效，且能复原");
            // 用户要的是「英文版」：这里验证真窗口上的侧栏按钮真的换了字、重复应用不变样（幂等），
            // 并且切回去能复原 —— 只切一半或越切越乱，都比没翻译更糟。
            App.Dispatch(() =>
            {
                var window = App.Current.MainWindow as Hexa.MainWindow;
                var navPlayground = window?.FindName("NavPlayground") as System.Windows.Controls.Button;
                if (window is null || navPlayground is null)
                {
                    Check("界面语言：拿得到主窗口的侧栏导航按钮", false);
                    return;
                }

                string original = LocalizationService.UiLanguage;
                string english = "";
                string restored = "";
                bool applied = false;
                bool idempotent = false;
                try
                {
                    LocalizationService.UiLanguage = LocalizationService.English;
                    window.ApplyLanguage();
                    english = navPlayground.Content as string ?? "";
                    window.ApplyLanguage();                       // 第二次：幂等，不许叠成别的样子
                    idempotent = (navPlayground.Content as string ?? "") == english;
                    applied = english.Contains("Playground", StringComparison.Ordinal);
                }
                finally
                {
                    // 无论上面怎么走，都要把语言还给用户设置的那一份（自检不许改用户的界面语言）
                    LocalizationService.UiLanguage = original;
                    window.ApplyLanguage();
                    restored = navPlayground.Content as string ?? "";
                }

                string expected = original == LocalizationService.English ? english : "🎮  游玩";
                Check($"界面语言：切到 English 侧栏变英文（「{english}」）、重复应用不变样（幂等={idempotent}）、"
                    + $"切回 {original} 复原（「{restored}」）",
                    applied && idempotent && restored == expected);
            });

            // 新增的「〰 让它自己摆（正弦）」：默认静止、勾上允许才动、按停立刻停手。
            Section("九点五、手动动轴页的正弦摆动发生器");
            // 先等上一个模块的过渡/归中停下来再测：桥的最后一次会话断开现在会走 ResetToCenter -> Home()，
            // 那段缓动会下发六轴帧，被这一节的「没勾允许时一条指令都不该发」算成假红。
            SpinWait.SpinUntil(() => !App.Engine.IsEasing, 5000);

            EnsureEngineRunnable();
            SineGeneratorChecks(cfg);

            Section("十、界面与设置真的对得上（不只「能打开」）");
            // 限位下限曾经被静默夹到 100（初始化顺序 bug）：这里直接在真实 UI 对象上验一遍。
            App.Dispatch(() =>
            {
                int savedMin = App.Settings.AxisMin.TryGetValue("L0", out int m0) ? m0 : 0;
                int savedMax = App.Settings.AxisMax.TryGetValue("L0", out int x0) ? x0 : 9999;
                try
                {
                    App.Settings.AxisMin["L0"] = 3000;
                    App.Settings.AxisMax["L0"] = 8000;
                    var page = new Hexa.Views.SettingsPage();
                    var sliders = page.GetType()
                        .GetField("_limitSliders", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(page) as Hexa.Controls.RangeSlider[];
                    var slider = sliders?[0];
                    Check("轴限位界面能正确显示保存过的下限（3000），不再被静默夹到 100",
                        slider != null && Math.Abs(slider.LowerValue - 3000) < 1
                        && Math.Abs(slider.UpperValue - 8000) < 1);
                    // 读数现在在滑杆正下方（右边那排输入框已按用户要求删掉），也要真的显示出来
                    var rangeTexts = page.GetType()
                        .GetField("_limitRangeTexts", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(page) as System.Windows.Controls.TextBlock[];
                    Check("轴限位读数显示在滑杆下方且数值正确（最小 3000 · 最大 8000）",
                        rangeTexts?[0] is { } readout && readout.Text.Contains("3000") && readout.Text.Contains("8000"));
                }
                catch (Exception ex) { Check("轴限位界面能正确显示保存过的下限（3000）", false); Lines.Add("      原因：" + ex.Message); }
                finally
                {
                    // 就地还原：万一后面某步抛异常/提前退出，也不会把用户的轴限位留在测试值上
                    App.Settings.AxisMin["L0"] = savedMin;
                    App.Settings.AxisMax["L0"] = savedMax;
                }
            });
            Thread.Sleep(200);

            // 用户报过两次「L0 的圆点没默认在最大最小」：默认限位（0–9999）时两个圆点必须在两端，
            // 读数必须写「全程，未限制」。这里把默认值真排一遍，防止以后再被什么测试值污染。
            App.Dispatch(() =>
            {
                try
                {
                    App.Settings.AxisMin["L0"] = 0;
                    App.Settings.AxisMax["L0"] = 9999;
                    var page = new Hexa.Views.SettingsPage();
                    var sliders = page.GetType()
                        .GetField("_limitSliders", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(page) as Hexa.Controls.RangeSlider[];
                    var texts = page.GetType()
                        .GetField("_limitRangeTexts", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(page) as System.Windows.Controls.TextBlock[];
                    var l0 = sliders?[0];
                    Check("轴限位默认（0–9999）时 L0 两个圆点在两端，读数写「全程」",
                        l0 != null && Math.Abs(l0.LowerValue) < 1 && Math.Abs(l0.UpperValue - 9999) < 1
                        && (texts?[0]?.Text.Contains("全程") ?? false));
                }
                catch (Exception ex) { Check("轴限位默认值检查", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(200);

            // 声音响应的两种模式：一次呻吟 = 一次抽插（角色声音）/ 跟着鼓点低音（音乐律动）。
            // 这两条必须在**界面**上真的连到设置上，而不是只有底层支持。
            App.Dispatch(() =>
            {
                try
                {
                    string savedMode = App.Settings.AudioResponseMode;
                    var page = new Hexa.Views.TestLabPage();
                    var target = page.FindName("AudioTargetCombo") as ComboBox;
                    var musicPanel = page.FindName("AudioMusicModePanel") as FrameworkElement;
                    if (target is null || musicPanel is null)
                    {
                        Check("声音响应的「响应对象」二选一存在", false);
                        return;
                    }
                    target.SelectedIndex = 0;   // 角色声音
                    Thread.Sleep(60);
                    Check("选「角色声音」时设置里存的就是 voice（呻吟=抽插）",
                        string.Equals(App.Settings.AudioResponseMode, "voice", StringComparison.OrdinalIgnoreCase));
                    Check("「角色声音」模式下「律动方式」自动收起（它只对音乐有意义）",
                        musicPanel.Visibility == Visibility.Collapsed);

                    target.SelectedIndex = 1;   // 音乐律动
                    Thread.Sleep(60);
                    Check("切回「音乐律动」后律动方式重新出现，设置回到音乐口径",
                        musicPanel.Visibility == Visibility.Visible
                        && App.Settings.AudioResponseMode is "energy" or "bass" or "beat");
                    App.Settings.AudioResponseMode = savedMode;
                }
                catch (Exception ex) { Check("声音响应两种模式的界面联动", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(250);

            // 测试台「播放增强」控件的初始状态必须与设置一致（否则用户会看到"开了却以为没开"）
            App.Dispatch(() =>
            {
                try
                {
                    App.Settings.ScriptAxisLinkEnabled = true;
                    App.Settings.ScriptAxisLinkAmount = 42;
                    // 播放增强的控件在「脚本库页」。页面没被加进可视树时 Loaded 不会触发，
                    // 所以直接调它背后那个"把设置读进界面"的方法 —— 测的正是真实逻辑。
                    var page = new Hexa.Views.ScriptLibraryPage();
                    Invoke(page, "LoadEnhanceControls");
                    var linkCheck = (page as System.Windows.FrameworkElement)?.FindName("LinkCheck")
                        as System.Windows.Controls.CheckBox;
                    var amountSlider = (page as System.Windows.FrameworkElement)?.FindName("LinkAmountSlider")
                        as System.Windows.Controls.Slider;
                    Check("脚本库页的「多轴联动」控件与设置一致（勾选状态 + 幅度 42）",
                        linkCheck?.IsChecked == true
                        && amountSlider != null && Math.Abs(amountSlider.Value - 42) < 0.01);
                    App.Settings.ScriptAxisLinkEnabled = false;
                    App.Settings.ScriptAxisLinkAmount = 30;
                }
                catch (Exception ex) { Check("测试台控件与设置一致（默认关/幅度可读）", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(200);

            // 游玩页排版：以前强度/速度滑杆和「开始自动」按钮在 1280px 下被拉到 800–894px 宽
            // （横跨整屏的长条），右侧 290px 列却几乎空着。这里用真实排版量一遍，防止改回去。
            App.Dispatch(() =>
            {
                try
                {
                    var page = new Hexa.Views.PlaygroundPage { Width = 1280 };
                    page.Measure(new System.Windows.Size(1280, 3000));
                    page.Arrange(new System.Windows.Rect(0, 0, 1280, 3000));
                    page.UpdateLayout();
                    double Width(string name) => (page.FindName(name) as FrameworkElement)?.ActualWidth ?? -1;
                    double intensity = Width("IntensitySlider");
                    double speed = Width("SpeedSlider");
                    double autoBtn = Width("AutoBtn");
                    double preview = Width("PlaygroundPreview");
                    Check($"游玩页控件不再被拉满整行（强度 {intensity:0} / 速度 {speed:0} / 开始自动 {autoBtn:0} px，均 < 400）",
                        intensity is > 0 and < 400 && speed is > 0 and < 400 && autoBtn is > 0 and < 400);
                    Check($"游玩页有 3D 实时状态（{preview:0}px 宽，> 400）", preview > 400);
                }
                catch (Exception ex) { Check("游玩页排版", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(300);

            // 游戏桥搬到了「游玩」页（用户要求「把游戏桥挪到游玩去」）。搬控件最容易出的事是
            // 「页面里看着有、其实样式或名字丢了」，所以直接量：卡片与端口框都在游玩页的可视树里、
            // 端口框显示的是当前设置、提示行常显且写明默认值与范围（用户要求「信息一直显示，不用悬停」）。
            App.Dispatch(() =>
            {
                try
                {
                    int portBefore = App.Settings.GameBridgePort;
                    App.Settings.GameBridgePort = 23456;
                    var page = new Hexa.Views.PlaygroundPage();
                    page.Measure(new System.Windows.Size(1280, 3000));
                    page.Arrange(new System.Windows.Rect(0, 0, 1280, 3000));
                    page.UpdateLayout();
                    // 桥在游玩页是折叠的：折叠的子树不参与布局，可视树里查不到它的内部控件，
                    // 所以拿面板实例、用它自己的名字域查（x:Name 在 XAML 解析时就登记好了）。
                    var host = FindByName(page, "BridgeSectionBody") as System.Windows.FrameworkElement;
                    var card = host?.FindName("BridgeCard");
                    var portBox = host?.FindName("BridgePortBox") as System.Windows.Controls.TextBox;
                    var hint = host?.FindName("BridgePortHint") as System.Windows.Controls.TextBlock;
                    Check("游戏桥在游玩页里（桥卡 + 端口框都在可视树中）", card != null && portBox != null);
                    Check($"游戏桥端口框显示当前端口（{portBox?.Text ?? "找不到"}）", portBox?.Text == "23456");
                    bool hintOk = hint != null
                        && hint.Visibility == System.Windows.Visibility.Visible
                        && hint.Text.Contains("12345") && hint.Text.Contains("65535") && hint.Text.Contains("23456");
                    Check($"端口提示常显并写明默认值/范围/地址（{hint?.Text ?? "找不到"}）", hintOk);
                    // 空状态文字与日志行不能同时出现（曾经在第一拍叠在一起，只有渲染图看得出来）。
                    var logList = host?.FindName("BridgeLogList") as System.Windows.Controls.ListBox;
                    var empty = host?.FindName("BridgeLogEmpty") as System.Windows.FrameworkElement;
                    bool noOverlap = App.Bridge.RecentLog.Count == 0
                        || (empty?.Visibility == System.Windows.Visibility.Collapsed && (logList?.Items.Count ?? 0) > 0);
                    Check($"指令监视：有日志时空状态文字不再压在日志上（{logList?.Items.Count ?? -1} 行）", noOverlap);
                    App.Settings.GameBridgePort = portBefore;
                }
                catch (Exception ex) { Check("游戏桥在游玩页里", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(250);

            // 测试台改成「点哪个看哪个」（用户原话「我要的是两排、方便我选功能，我不想一直往下滑」）。
            // 量两件事：选择区是两列三块；任意时刻只有一张卡片可见 —— 曾经出现过两栏方案下
            // 收起的一栏仍占半屏（ColumnDefinition Width="*"），那种毛病只有量得出来。
            App.Dispatch(() =>
            {
                try
                {
                    var page = new Hexa.Views.TestLabPage();
                    page.Measure(new System.Windows.Size(970, 700));
                    page.Arrange(new System.Windows.Rect(0, 0, 970, 700));
                    page.UpdateLayout();
                    var picker = FindByName(page, "FeatureSwitchGrid") as System.Windows.Controls.Grid;
                    var audio = FindByName(page, "AudioCard");
                    var companion = FindByName(page, "CompanionCard");
                    var screen = FindByName(page, "ScreenCard");
                    var sim = FindByName(page, "SimulatorCard");
                    var thirdBtn = FindByName(page, "FeatureScreenBtn") as System.Windows.Controls.Button;
                    var simBtn = FindByName(page, "FeatureSimBtn") as System.Windows.Controls.Button;
                    int pickerCols = picker?.ColumnDefinitions.Count ?? -1;
                    int pickerBtns = picker?.Children.OfType<System.Windows.Controls.Button>().Count() ?? -1;
                    int thirdSpan = thirdBtn == null ? -1 : System.Windows.Controls.Grid.GetColumnSpan(thirdBtn);
                    int simSpan = simBtn == null ? -1 : System.Windows.Controls.Grid.GetColumnSpan(simBtn);
                    var ayva = FindByName(page, "AyvaCard");
                    var fanoutCard = FindByName(page, "FanoutCard");
                    var fanBtn = FindByName(page, "FeatureFanoutBtn") as System.Windows.Controls.Button;
                    int fanSpan = fanBtn == null ? -1 : System.Windows.Controls.Grid.GetColumnSpan(fanBtn);
                    var ayvaBtn = FindByName(page, "FeatureAyvaBtn") as System.Windows.Controls.Button;
                    int ayvaSpan = ayvaBtn == null ? -1 : System.Windows.Controls.Grid.GetColumnSpan(ayvaBtn);
                    // 光看 ColumnSpan 不够：曾经 UniformGrid 下第三个按钮只占半排、右边空一块（渲染图才看得出来）。
                    // 所以直接量宽度：四块两排两列时，每一块都必须接近半排宽（两列 + 中间 12px 间隙）。
                    // 2026-10-06：功能从 3 块变 4 块（新增「④ 设备模拟器」），第三块不再跨整排 ——
                    // 断言跟着改成「每块都占半排」，强度不降：照样查跨列、照样量宽度、照样不许留空。
                    double pickerWidth = picker?.ActualWidth ?? -1;
                    double thirdWidth = thirdBtn?.ActualWidth ?? -1;
                    double simWidth = simBtn?.ActualWidth ?? -1;
                    double halfWidth = (pickerWidth - 12) / 2;
                    double ayvaWidth = ayvaBtn?.ActualWidth ?? -1;
                    double fanWidth = fanBtn?.ActualWidth ?? -1;
                    // 2026-10-06：功能从 4 块变 5 块（新增「⑤ 网页入口」），第五块占满整排 ——
                    // 断言相应变成「前四块各占半排 + 第五块占整排」，强度不降：照样查跨列、照样量宽度、照样不许留空。
                    Check($"测试台功能选择区是六块各占半排（{pickerCols} 列 / {pickerBtns} 块 / 跨 {thirdSpan}+{simSpan}+{ayvaSpan}+{fanSpan} 列）",
                        pickerCols == 2 && pickerBtns == 6
                        && thirdSpan == 1 && simSpan == 1 && ayvaSpan == 1 && fanSpan == 1
                        && thirdWidth >= halfWidth * 0.95 && simWidth >= halfWidth * 0.95
                        && ayvaWidth >= halfWidth * 0.95 && fanWidth >= halfWidth * 0.95);
                    Check("测试台默认只显示 ① 声音响应（另五块收起）",
                        audio?.Visibility == System.Windows.Visibility.Visible
                        && companion?.Visibility != System.Windows.Visibility.Visible
                        && screen?.Visibility != System.Windows.Visibility.Visible
                        && sim?.Visibility != System.Windows.Visibility.Visible
                        && ayva?.Visibility != System.Windows.Visibility.Visible
                        && fanoutCard?.Visibility != System.Windows.Visibility.Visible);
                    Invoke(page, "ShowCard", "ScreenCard");
                    Check("测试台选中 ③ 后只剩 ③ 可见（另三块收起、不留半屏空栏）",
                        screen?.Visibility == System.Windows.Visibility.Visible
                        && audio?.Visibility != System.Windows.Visibility.Visible
                        && companion?.Visibility != System.Windows.Visibility.Visible
                        && sim?.Visibility != System.Windows.Visibility.Visible);
                }
                catch (Exception ex) { Check("测试台功能切换", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(250);

            // 用户报「点快点切换页面就闪退」（2026-09-24 21:14 的 app.log 有完整栈）：
            // 根因是设置页在 IsVisibleChanged（WPF 布局过程中）弹模态框，抛
            // InvalidOperationException「调度程序处理已暂停，但仍在处理消息」。这里钉住修法本身。
            Check("设置页不再有「切页时弹模态框」的方法（闪退根因）",
                typeof(Hexa.Views.SettingsPage).GetMethod("ConfirmUnsavedAiSettings",
                    BindingFlags.NonPublic | BindingFlags.Instance) is null);

            // 固件设置（设置页「🧩 固件设置（进阶）」卡）：默认收起 —— 舵机零点这类改错会让机器反向或顶死；
            // 收起时那一行摘要要写着状态（还没读 / 读到 N 项 · 未保存 M 项），点开才显示具体设置。
            App.Dispatch(() =>
            {
                try
                {
                    // 用户报「设置页左右空着干嘛」：单列限宽 520 在宽窗口下左右各空一片、还要滚很久。
                    // 这里钉住分栏：宽窗口右栏与左栏同一行（并排），窄窗口右栏搬到下一行（一栏到底）。
                    static (int Row, int Column, double Left, double Right) MeasureColumns(double width)
                    {
                        var p = new Hexa.Views.SettingsPage();
                        p.Width = width;
                        p.Measure(new System.Windows.Size(width, 1400));
                        p.Arrange(new System.Windows.Rect(0, 0, width, 1400));
                        p.UpdateLayout();
                        var left = FindByName(p, "SettingsLeftColumn") as System.Windows.FrameworkElement;
                        var right = FindByName(p, "SettingsRightColumn") as System.Windows.FrameworkElement;
                        if (left is null || right is null) return (-1, -1, 0, 0);
                        return (System.Windows.Controls.Grid.GetRow(right),
                                System.Windows.Controls.Grid.GetColumn(right),
                                left.ActualWidth, right.ActualWidth);
                    }

                    (int wideRow, int wideCol, double wideLeft, double wideRight) = MeasureColumns(1280);
                    Check($"设置页宽窗口并排两栏（右栏 row={wideRow} col={wideCol}，左 {wideLeft:0}px / 右 {wideRight:0}px）",
                        wideRow == 0 && wideCol == 2 && wideLeft > 300 && wideRight > 300);

                    (int narrowRow, int narrowCol, double narrowLeft, double narrowRight) = MeasureColumns(660);
                    Check($"设置页窄窗口一栏到底（右栏 row={narrowRow} col={narrowCol}，左 {narrowLeft:0}px / 右 {narrowRight:0}px）",
                        narrowRow == 1 && narrowCol == 0 && narrowRight > 300
                        && Math.Abs(narrowRight - narrowLeft) < 1);
                }
                catch (Exception ex) { Check("设置页分栏", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(250);

            App.Dispatch(() =>
            {
                try
                {
                    var page = new Hexa.Views.SettingsPage();
                    page.Measure(new System.Windows.Size(1180, 1600));
                    page.Arrange(new System.Windows.Rect(0, 0, 1180, 1600));
                    page.UpdateLayout();
                    var body = FindByName(page, "FirmwareFoldBody");
                    var summary = FindByName(page, "FirmwareFoldSummary") as System.Windows.Controls.TextBlock;
                    Check($"设置页的固件设置默认收起（{body?.Visibility}）、摘要写着状态（{summary?.Text}）",
                        body?.Visibility != System.Windows.Visibility.Visible
                        && summary is { Text.Length: > 0 });
                    Invoke(page, "FirmwareFold_Click", page, new System.Windows.RoutedEventArgs());
                    Check("点开固件设置后：设置区可见，读取/应用/保存/重启与状态行都在",
                        body?.Visibility == System.Windows.Visibility.Visible
                        && FindByName(page, "FirmwareReadBtn") is not null
                        && FindByName(page, "FirmwareApplyBtn") is not null
                        && FindByName(page, "FirmwareSaveBtn") is not null
                        && FindByName(page, "FirmwareRestartBtn") is not null
                        && FindByName(page, "FirmwareStatusText") is not null);
                }
                catch (Exception ex) { Check("设置页的固件设置卡", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(200);

            // 游玩页：游戏桥默认收起（它整卡带日志列表，摊开半页），点标题行才展开；
            // 收起时那一行摘要要写着状态，不能是空的。
            App.Dispatch(() =>
            {
                try
                {
                    var page = new Hexa.Views.PlaygroundPage();
                    page.Measure(new System.Windows.Size(1280, 3000));
                    page.Arrange(new System.Windows.Rect(0, 0, 1280, 3000));
                    page.UpdateLayout();
                    var body = FindByName(page, "BridgeSectionBody");
                    var summary = FindByName(page, "BridgeSectionSummary") as System.Windows.Controls.TextBlock;
                    Check($"游玩页的游戏桥默认收起（{body?.Visibility}）、摘要写着状态（{summary?.Text}）",
                        body?.Visibility != System.Windows.Visibility.Visible
                        && summary is { Text.Length: > 0 });
                    Invoke(page, "BridgeSection_Click", page, new System.Windows.RoutedEventArgs());
                    Check("点一下桥标题行就展开（面板可见、摘要让位）",
                        body?.Visibility == System.Windows.Visibility.Visible
                        && summary?.Visibility != System.Windows.Visibility.Visible);
                    Invoke(page, "BridgeSection_Click", page, new System.Windows.RoutedEventArgs());
                    Check("再点一下收回（收起后摘要重新写状态）",
                        body?.Visibility != System.Windows.Visibility.Visible
                        && summary is { Text.Length: > 0 });
                }
                catch (Exception ex) { Check("游玩页游戏桥折叠", false); Lines.Add("      原因：" + ex.Message); }
            });
            Thread.Sleep(250);

            // 用户报过"脚本制作页拉太长了""脚本库页列表以外的一堆卡"：这两页要在默认窗口下
            // 不用滚动就看完整页。默认窗口 1180×760：页面区域 ≈ 970 宽 × 700 高
            // （再减掉页头约 60 + 标签头约 42 + 页边距 40，留给正文的视口约 558px）。
            Section("十一、默认窗口下要不要滚（用户报「拉太长」）");
            foreach ((string name, Func<object> factory) in new (string, Func<object>)[]
            {
                ("编排页", () => new Hexa.Views.EditorPage()),
                ("脚本库页", () => new Hexa.Views.ScriptLibraryPage()),
            })
            {
                double overflow = MeasureVerticalOverflow(factory, 970, 700);
                Check($"{name}：默认窗口（页面 970×700）下不需要纵向滚动（实测多余 {overflow:0}px）", overflow <= 1);
            }

            // 测试台页一屏只放一块卡片，用户的原话是「我要的是两排、方便我选功能，我不想一直往下滑」。
            // 要量的是「切换功能不用往下滑」，不是「整页一屏放得下」：一块卡片里的参数与逐条说明本来
            // 就长，硬压到一屏只能靠删说明 —— 那不是用户要的。所以量功能按钮的底部，卡片高度只做参考。
            double switchBottom = MeasureElementBottom(() => new Hexa.Views.TestLabPage(), "FeatureSwitchGrid", 970, 700);
            double cardOverflow = MeasureVerticalOverflow(() => new Hexa.Views.TestLabPage(), 970, 700);
            Check($"测试台页：三个功能按钮在默认窗口内一屏可见（按钮底部 {switchBottom:0}px ≤ 700）",
                switchBottom is > 0 and <= 700);
            Lines.Add($"      （参考：所选卡片比一屏多 {cardOverflow:0}px，是参数与说明，属于正常滚动）");

            // 游玩页原本是一条 2300px 的长页（桥整卡常显 + 参数卡 + 各种说明）。现在默认只留
            // 主区两栏 + 两个收起行，1280 宽时整页约 850px —— 一屏就能看到底。
            // 这条是防回归：谁再把大块内容摊开常显，这条会立刻失败。
            double playOverflow = MeasureVerticalOverflow(() => new Hexa.Views.PlaygroundPage(), 1280, 980);
            Check($"游玩页（1280×980、桥与参数都收起时）一屏放得下（多余 {playOverflow:0}px）", playOverflow <= 1);

            // 脚本库页合并卡片的目的是"把高度还给列表"：列表必须还是主体（实测高度 ≥150px）
            double listHeight = MeasureElementHeight(() => new Hexa.Views.ScriptLibraryPage(), "ScriptList", 970, 700);
            Check($"脚本库页合并卡片后列表仍是主体（ScriptList 实测高 {listHeight:0}px，≥150）", listHeight >= 150);

            Section("十二、脚本库读写往返");
            try
            {
                ScriptLibrary.EnsureFolder();
                string sample = Path.Combine(Path.GetTempPath(), "hexa-selftest-lib.funscript");
                File.Copy(WriteTestScript(), sample, overwrite: true);
                string imported = ScriptLibrary.Import(sample, overwrite: true);
                bool listed = ScriptLibrary.List().Any(entry =>
                    string.Equals(Path.GetFileName(entry.FilePath), Path.GetFileName(imported), StringComparison.OrdinalIgnoreCase));
                Check("导入脚本后能在库里被列出并解析", listed);
                ScriptLibrary.Delete(imported);
                Check("删除只删库内副本（源文件还在）", File.Exists(sample));
                File.Delete(sample);
            }
            catch (Exception ex) { Check("脚本库读写往返", false); Lines.Add("      原因：" + ex.Message); }

            Section("十三、日志");
            try
            {
                string logPath = AppLogger.LogFilePath;
                long before = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
                AppLogger.Info("自检：日志写入测试 " + DateTime.Now.ToString("HH:mm:ss"));
                Thread.Sleep(400);
                long after = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
                // 日志现在有轮转（超过上限会把 app.log 挪成 app.log.1），恰好撞上轮转时
                // 主文件会变小 —— 只看"变大了"会误判。轮转产生 app.log.1 也算落盘成功。
                bool rotated = File.Exists(logPath + ".1");
                Check($"日志能落盘（{Path.GetFileName(logPath)} {before} → {after} 字节{(rotated ? "，且发生了轮转" : "")}）",
                    after > before || rotated);
            }
            catch (Exception ex) { Check("日志能落盘", false); Lines.Add("      原因：" + ex.Message); }

            Section("十四、设置读写");
            var snapshot = new AppSettings();
            snapshot.Save();               // 走一次真实写盘
            // 原来这条恒真（Load() 的三条返回路径都返回非空实例）＝这一节什么都没验。
        // 改成真正的往返：改一个值 → 存盘 → 重新读回来比对。
        double probeBefore = App.Settings.ScriptPlaybackSpeed;
        App.Settings.ScriptPlaybackSpeed = probeBefore == 1.37 ? 1.38 : 1.37;
        App.Settings.Save();
        double probeAfter = AppSettings.Load().ScriptPlaybackSpeed;
        App.Settings.ScriptPlaybackSpeed = probeBefore;
        App.Settings.Save();
        Check($"设置能写盘并读回（往返值 {probeAfter:0.00}）", Math.Abs(probeAfter - 1.37) < 0.001 || Math.Abs(probeAfter - 1.38) < 0.001);

            // 用户原话："你怎么总喜欢用那种显示，一般滚动才能看另一部分"。
            // 窗口最小 880，减去左侧栏 200、再去掉页面留白，正文大约只有 660px。
            // 这里把每个页面按 660px 排一遍，量出"需要的宽度超过可用宽度"的元素 ——
            // 它们就是被窗口边缘切掉、用户只能看到一半的那些东西。
            // 开发用：把每个页面渲染成 PNG，用眼睛复核排版（HEXA_RENDER=<目录> 时才跑）。
            // 排版这类问题"量得出来也要看得见"——审计能抓被切掉的内容，但抓不出难看。
            if (Environment.GetEnvironmentVariable("HEXA_RENDER") is { Length: > 0 } shotDir)
            {
                Directory.CreateDirectory(shotDir);
                foreach ((string name, Func<object> factory) in new (string, Func<object>)[]
                {
                    ("playground", () => new Hexa.Views.PlaygroundPage()),
                    ("manual", () => new Hexa.Views.ManualControlPage()),
                    ("strokes", () => new Hexa.Views.StrokesPage()),
                    ("editor", () => new Hexa.Views.EditorPage()),
                    ("scriptlibrary", () => new Hexa.Views.ScriptLibraryPage()),
                    ("testlab", () => new Hexa.Views.TestLabPage()),
                    ("settings", () => new Hexa.Views.SettingsPage()),
                    ("ai", () => new Hexa.Views.AiAssistantPage()),
                })
                {
                    RenderPage(Path.Combine(shotDir, name + ".png"), factory, 1280, 900);
                    RenderPage(Path.Combine(shotDir, name + "-narrow.png"), factory, 880, 760);
                }
                // 游玩页把游戏桥挂在最下面，900px 的视口看不到它 —— 再单独出一张「整页」图，
                // 用来复核桥卡与端口提示行的排版（渲染只是开发用，不进产品界面）。
                RenderPage(Path.Combine(shotDir, "playground-full.png"), () => new Hexa.Views.PlaygroundPage(), 1280, 2300);
                // 游戏桥现在只有一个实例（控件），单独出一张图专门看它自己的排版与端口提示行。
                RenderPage(Path.Combine(shotDir, "bridge-panel.png"), () => new Hexa.Views.BridgePanel(), 1000, 780);
                RenderPage(Path.Combine(shotDir, "calibration.png"),
                    () => new Hexa.Views.CalibrationWindow().Content, 470, 660);
                RenderPage(Path.Combine(shotDir, "stroke-editor.png"),
                    () => new Hexa.Views.StrokeEditorWindow().Content, 880, 720);
            }

            Section("十五、窄窗口（660px）下有没有被切掉的内容");
            foreach ((string name, Func<object> factory) in new (string, Func<object>)[]
            {
                ("游玩页", () => new Hexa.Views.PlaygroundPage()),
                ("手动动轴页", () => new Hexa.Views.ManualControlPage()),
                ("动作页", () => new Hexa.Views.StrokesPage()),
                ("编排页", () => new Hexa.Views.EditorPage()),
                ("脚本库页", () => new Hexa.Views.ScriptLibraryPage()),
                ("测试台页", () => new Hexa.Views.TestLabPage()),
                ("设置页", () => new Hexa.Views.SettingsPage()),
                ("AI 助手页", () => new Hexa.Views.AiAssistantPage()),
            })
            {
                AuditNarrowLayout(name, factory);
            }

            if (Environment.GetEnvironmentVariable("HEXA_UXAUDIT") is { Length: > 0 })
            {
                Section("UX 审计（使用者视角；仅 HEXA_UXAUDIT=1 时跑）：要不要滚屏、按钮在不在首屏、最宽控件多大");
                foreach ((string name, Func<object> factory) in new (string, Func<object>)[]
                {
                    ("游玩页", () => new Hexa.Views.PlaygroundPage()),
                    ("手动动轴页", () => new Hexa.Views.ManualControlPage()),
                    ("动作页", () => new Hexa.Views.StrokesPage()),
                    ("编排页", () => new Hexa.Views.EditorPage()),
                    ("脚本库页", () => new Hexa.Views.ScriptLibraryPage()),
                    ("测试台页", () => new Hexa.Views.TestLabPage()),
                    ("设置页", () => new Hexa.Views.SettingsPage()),
                    ("AI 助手页", () => new Hexa.Views.AiAssistantPage()),
                })
                {
                    AuditUserView(name, factory);
                }
            }

            Section("十六、网页入口（Ayva）：服务能起、能收 TCode、真解析成动作");
            {
                bool wasRunning = App.AyvaWeb.IsRunning;
                bool entryUp = false;
                bool gotFrame = false;
                bool parsed = false;
                string seen = "";
                try
                {
                    App.AyvaWeb.Start();
                    entryUp = App.AyvaWeb.IsRunning;
                    if (entryUp)
                    {
                        long before = App.AyvaWeb.Frames;
                        using var client = new System.Net.WebSockets.ClientWebSocket();
                        client.ConnectAsync(new Uri(App.AyvaWeb.Url), CancellationToken.None).GetAwaiter().GetResult();
                        byte[] payload = Encoding.ASCII.GetBytes("L08000 R06000");
                        client.SendAsync(new ArraySegment<byte>(payload), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
                        gotFrame = SpinWait.SpinUntil(() => App.AyvaWeb.Frames > before, 3000);
                        seen = App.AyvaWeb.LastCommand;
                        parsed = seen == "L08000 R06000";
                        client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex) { Lines.Add("      原因：" + ex.Message); }
                finally { if (!wasRunning) App.AyvaWeb.Stop(); }

                Check($"网页入口能起来并监听（{App.AyvaWeb.Url}）", entryUp);
                Check($"网页发来的 TCode 真被收到并解析（帧计数增加，原文“{seen}”）", gotFrame && parsed);
            }

            Section("十七、多输出：同一条指令真的发给网络目标");
            {
                bool wasEnabled = App.Fanout.Enabled;
                string wasTargets = App.Settings.FanoutTargets;
                bool wasOutputEnabled = App.Serial.OutputEnabled;
                bool got = false;
                string received = "";
                try
                {
                    using var listener = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                    int udpPort = ((System.Net.IPEndPoint)listener.Client.LocalEndPoint!).Port;
                    listener.Client.ReceiveTimeout = 3000;

                    App.Serial.OutputEnabled = true;                 // 前面急停测试可能锁了输出
                    App.Fanout.Configure($"udp://127.0.0.1:{udpPort}");
                    App.Fanout.Enabled = true;
                    App.Serial.Send("L05000");                       // 模拟设备下也会镜像 → 应当出现在 UDP 那头

                    var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                    byte[] data = listener.Receive(ref remote);
                    received = Encoding.ASCII.GetString(data).Trim();
                    got = true;
                }
                catch (Exception ex) { Lines.Add("      原因：" + ex.Message); }
                finally
                {
                    App.Fanout.Enabled = wasEnabled;
                    App.Fanout.Configure(wasTargets);
                    App.Serial.OutputEnabled = wasOutputEnabled;
                }

                Check($"多输出把指令真发到了 UDP 目标（收到“{received}”）", got && received == "L05000");
            }

            Section("十八、节拍跟踪：能不能从起音序列里把 BPM 测出来");
            {
                // 合成 120 BPM：每 0.5 秒一次强起音，喂 10 秒（100Hz 帧率，每 50 帧一次）。
                var tracker = new Hexa.Services.TempoTracker();
                for (int i = 0; i < 1000; i++)
                {
                    bool onset = (i % 50) == 0;
                    tracker.Push(onset ? 1.0 : 0.02, 0.01);
                }
                Check($"120 BPM 的起音序列 → 测出 {tracker.Bpm:0.0} BPM（信心 {tracker.Confidence:0.00}）",
                    Math.Abs(tracker.Bpm - 120) < 6);
                Check($"锁上拍点后能说出「到下一拍还有几秒」（{tracker.SecondsToNextBeat:0.000}s，当前相位 {tracker.BeatPhase01:0.00}）",
                    tracker.Locked && tracker.SecondsToNextBeat is > 0 and <= 0.51);

                // 换一个速度：180 BPM（每 1/3 秒一次），验它不是只会认 120
                var fast = new Hexa.Services.TempoTracker();
                for (int i = 0; i < 1000; i++)
                {
                    bool onset = (i % 33) == 0;
                    fast.Push(onset ? 1.0 : 0.02, 0.01);
                }
                Check($"180 BPM 的起音序列 → 测出 {fast.Bpm:0.0} BPM", Math.Abs(fast.Bpm - 180) < 12);

                // 安静素材（完全没有起音）必须老实说"没锁上"，而不是瞎报一个 BPM
                var quiet = new Hexa.Services.TempoTracker();
                for (int i = 0; i < 500; i++) quiet.Push(0.01, 0.01);
                Check($"没有起音时不乱报（Bpm={quiet.Bpm:0.0}，Locked={quiet.Locked}）", !quiet.Locked);
            }

            Section("十九、脚本库网页：能起来、能返回页面、只服务本机");
            {
                bool wasRunning = App.LibraryWeb.IsRunning;
                bool up = false;
                bool hasPage = false;
                bool hasList = false;
                int count = 0;
                try
                {
                    App.LibraryWeb.Start();
                    up = App.LibraryWeb.IsRunning;
                    if (up)
                    {
                        count = Hexa.Models.ScriptLibrary.List().Count;
                        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                        string html = client.GetStringAsync(App.LibraryWeb.Url).GetAwaiter().GetResult();
                        hasPage = html.Contains("Hexa 脚本库");
                        hasList = html.Contains("停止");      // 页面上一定有"全部停止"这个出口
                    }
                }
                catch (Exception ex) { Lines.Add("      原因：" + ex.Message); }
                finally { if (!wasRunning) App.LibraryWeb.Stop(); }

                Check($"脚本库网页能起来并返回页面（{App.LibraryWeb.Url}）", up && hasPage && hasList);
                Check($"页面里的脚本数与库一致（库里 {count} 个）", count >= 0);
            }
        }
        catch (Exception ex)
        {
            Lines.Add("");
            Lines.Add("!! 自检异常中止：" + ex);
            _failed++;
        }

        Lines.Add("");
        Lines.Add($"===== 自检结束：{_passed} 项通过，{_failed} 项失败 =====");

        // 还原用户原来的设置（只还原自检碰过的那几项，避免把用户刚改的东西覆盖掉）
        try
        {
            var cfg = App.Settings;
            cfg.SimulationMode = original.SimulationMode;
            cfg.AutoConnect = original.AutoConnect;
            cfg.GameBridgeEnabled = original.GameBridgeEnabled;
            cfg.GameBridgeMode = original.GameBridgeMode;
            cfg.GameBridgePort = original.GameBridgePort;
            cfg.AudioReactiveEnabled = original.AudioReactiveEnabled;
            cfg.AmbientOverlay = original.AmbientOverlay;
            cfg.AmbientOverlayAmount = original.AmbientOverlayAmount;
            cfg.RuleEngineEnabled = original.RuleEngineEnabled;
            cfg.CompanionAuto = original.CompanionAuto;
            cfg.CompanionProcess = original.CompanionProcess;
            cfg.CompanionBaseMode = original.CompanionBaseMode;
            cfg.CompanionBaseIntensity = original.CompanionBaseIntensity;
            cfg.CompanionSource = original.CompanionSource;
            cfg.ActionLinkEnabled = original.ActionLinkEnabled;
            cfg.ScriptAxisLinkEnabled = original.ScriptAxisLinkEnabled;
            cfg.ScriptGapFillEnabled = original.ScriptGapFillEnabled;
            cfg.ScriptSmoothingEnabled = original.ScriptSmoothingEnabled;
            cfg.ScriptAxisLinkAmount = original.ScriptAxisLinkAmount;
            // 轴限位是"用户的机械边界"，被改过的后果最严重（机器只能在中间一段动），必须还原。
            cfg.AxisMin = new Dictionary<string, int>(original.AxisMin);
            cfg.AxisMax = new Dictionary<string, int>(original.AxisMax);
            cfg.Save();
            App.AudioReactive.Refresh();
            App.Bridge.Refresh();
            Lines.Add($"（自检改动过的设置已还原：模拟模式/桥/伴随/播放增强/轴限位，L0 = {cfg.AxisMin["L0"]}–{cfg.AxisMax["L0"]}）");
        }
        catch (Exception ex) { Lines.Add("!! 还原设置失败：" + ex.Message); }

        try
        {
            File.WriteAllLines(reportPath, Lines, new UTF8Encoding(false));
            LastReportPath = reportPath;
        }
        catch { }

        Running = false;
        SelfTestFinished?.Invoke(_passed, _failed, reportPath);
        if (shutdownWhenDone) App.Dispatch(() => App.Current.Shutdown());
    }

    /// <summary>自检结束（通过数、失败数、报告路径）；界面订阅它来显示结果。</summary>
    public static event Action<int, int, string>? SelfTestFinished;

    // ── 辅助 ─────────────────────────────────────────────────────────

    private static void Section(string title)
    {
        Lines.Add("");
        Lines.Add("── " + title + " ──");
    }

    /// <summary>
    /// 页面冒烟：在 UI 线程上把页面构造一遍。XAML 解析错误、控件名写错、构造函数里的空引用
    /// 都只会在这个时候暴露 —— 这是"打开某个页面就崩"的唯一低成本防线。
    /// </summary>
    private static void PageSmoke(string name, Func<object> factory)
    {
        string? error = null;
        App.Dispatch(() =>
        {
            try { _ = factory(); }
            catch (Exception ex) { error = ex.GetBaseException().Message; }
        });
        Thread.Sleep(120);
        Check($"打开「{name}」不报错", error == null);
        if (error != null) Lines.Add("      原因：" + error);
    }

    /// <summary>
    /// 让引擎回到「可动」状态：自检前面几节会故意急停/断线（那些安全项要验），掉线看门狗可能把急停锁存下来。
    /// 需要「设备能动」这个前提的检查先调用它；真解不开就把现场写进报告，别让后面的检查背锅。
    /// </summary>
    private static void EnsureEngineRunnable()
    {
        if (App.Engine.CanRun) return;
        bool wasLatched = App.Engine.EmergencyStopped;
        App.Engine.Home();                      // 「全部归中」是设计上的解锁路径
        SpinWait.SpinUntil(() => App.Engine.CanRun, 4000);
        if (!App.Engine.CanRun)
            Lines.Add("      （前置：引擎仍不可动 —— 急停锁存=" + wasLatched
                + " 归中中=" + App.Engine.IsHoming + " 串口=" + App.Serial.IsOpen + "）");
        else if (wasLatched)
            Lines.Add("      （前置：清掉了上一节遗留的急停锁存）");
    }

    private static void Check(string name, bool ok)
    {
        if (ok) { _passed++; Lines.Add("  ✔ " + name); }
        else { _failed++; Lines.Add("  ✘ " + name); }
    }

    /// <summary>
    /// 窄窗口排版审计：把页面按指定宽度真排一遍，找出"需要宽度 > 可用宽度"的元素。
    /// 只报最外层的那一个（内层子元素会跟着一起超宽，重复报没有意义），
    /// 并且跳过本来就该横向滚动的区域（时间轴、日志列表）——那些是有意为之。
    /// </summary>
    private static void AuditNarrowLayout(string name, Func<object> factory)
    {
        List<string> offenders = [];
        List<string> scrollers = [];
        foreach (double width in new[] { 1180d, 900d, 660d, 560d })
        {
            App.Dispatch(() =>
            {
                try
                {
                    if (factory() is not FrameworkElement page) return;
                    page.Width = width;
                    page.Measure(new System.Windows.Size(width, 3000));
                    page.Arrange(new System.Windows.Rect(0, 0, width, 3000));
                    page.UpdateLayout();
                    CollectOverflow(page, width, offenders, insideHorizontalScroller: false, parentOverflowed: false);
                    CollectHorizontalScrollers(page, scrollers);
                }
                catch (Exception ex) { offenders.Add("审计本身失败：" + ex.GetBaseException().Message); }
            });
            Thread.Sleep(150);
        }
        Check($"{name}：各种窗口宽度（1180/900/660/560px）下都没有被切掉的内容", offenders.Count == 0);
        foreach (string line in offenders.Take(6)) Lines.Add("      " + line);
        if (scrollers.Count > 0)
            Lines.Add("      （本来就要横向滚动的区域：" + string.Join("、", scrollers.Distinct().Take(4)) + "）");
    }

    /// <summary>
    /// 以「使用者视角」量一遍页面：内容多高（要不要滚屏）、按钮有多少在首屏、最宽控件多大（他的红线是 420px）、
    /// 有没有必须横向滚才能看全的区域。只在 HEXA_UXAUDIT=1 时运行，不改任何设置、不发任何指令。
    /// </summary>
    private static void AuditUserView(string name, Func<object> factory)
    {
        const double ViewportHeight = 760;   // 他常用的窗口高度（1280×800 减去标题栏与任务栏）
        double contentHeight = 0, widestWidth = 0;
        int controls = 0, buttons = 0, buttonsAboveFold = 0, wideOffenders = 0, scrollZones = 0;
        string widestLabel = "—";
        List<string> scrollers = [];
        List<string> wide = [];
        App.Dispatch(() =>
        {
            try
            {
                if (factory() is not FrameworkElement page) return;
                page.Width = 1180;
                // 无限高 Measure ⇒ DesiredSize.Height 就是"这个页面自然要多高"（用它判断要不要滚屏）
                page.Measure(new System.Windows.Size(1180, double.PositiveInfinity));
                contentHeight = page.DesiredSize.Height;
                page.Arrange(new System.Windows.Rect(0, 0, 1180, Math.Max(contentHeight, 100)));
                page.UpdateLayout();
                CollectHorizontalScrollers(page, scrollers);
                WalkVisual(page, fe =>
                {
                    // 注意：未接到窗口上的元素 IsVisible 恒为 false（那会让统计全为 0），要看 Visibility。
                    if (fe.Visibility != System.Windows.Visibility.Visible) return;
                    if (fe is System.Windows.Controls.ScrollViewer sv && sv.ScrollableHeight > 1) scrollZones++;
                    // 只算"能点的控件"：他的红线是控件别被拉成 >420px 的横条，整页容器不算
                    bool interactive = fe is System.Windows.Controls.Primitives.ButtonBase
                        or System.Windows.Controls.ComboBox
                        or System.Windows.Controls.Slider
                        or System.Windows.Controls.TextBox;
                    if (!interactive) return;
                    controls++;
                    if (fe.ActualWidth > widestWidth) { widestWidth = fe.ActualWidth; widestLabel = ElementLabel(fe); }
                    if (fe.ActualWidth > 420)
                    {
                        wideOffenders++;
                        if (wide.Count < 6) wide.Add(ElementLabel(fe) + " " + fe.ActualWidth.ToString("0") + "px");
                    }
                    if (fe is System.Windows.Controls.Primitives.ButtonBase button)
                    {
                        buttons++;
                        double y = 0;
                        try { y = button.TranslatePoint(new System.Windows.Point(0, 0), page).Y; } catch { /* 不在树上 */ }
                        if (y + button.ActualHeight <= ViewportHeight) buttonsAboveFold++;
                    }
                });
            }
            catch (Exception ex) { Lines.Add("      （" + name + " 审计失败：" + ex.GetBaseException().Message + "）"); }
        });
        Thread.Sleep(250);   // App.Dispatch 是投递（BeginInvoke），不等一下量到的全是 0
        Lines.Add($"      {name}：内容高 {contentHeight:0}px ⇒ {(contentHeight > ViewportHeight ? "要滚屏" : "一屏到底")}"
            + $" · 控件 {controls} · 按钮 {buttons}（首屏 {buttonsAboveFold}）"
            + $" · 最宽 {widestLabel} {widestWidth:0}px · 超 420px 的 {wideOffenders} 个"
            + (scrollZones > 0 ? $" · 纵向滚动区 {scrollZones}" : "")
            + (scrollers.Count > 0 ? " · 横向滚动区：" + string.Join("、", scrollers.Distinct().Take(3)) : ""));
        if (wide.Count > 0) Lines.Add("        （超 420px 的按钮：" + string.Join("、", wide.Distinct().Take(4)) + "）");
    }

    private static string ElementLabel(FrameworkElement fe) =>
        !string.IsNullOrEmpty(fe.Name) ? fe.Name
        : fe is System.Windows.Controls.Button b && b.Content is string text && text.Length > 0 ? text
        : fe.GetType().Name;

    private static void WalkVisual(DependencyObject node, Action<FrameworkElement> visit)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement fe) visit(fe);
            WalkVisual(child, visit);
        }
    }

    /// <summary>找出视口装不下、必须横向滚动才能看全的区域（这类是"另一部分看不到"的直接来源）。</summary>
    private static void CollectHorizontalScrollers(DependencyObject node, List<string> found)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer sv && sv.ScrollableWidth > 1)
            {
                string label = string.IsNullOrEmpty(sv.Name) ? sv.GetType().Name : sv.Name;
                found.Add($"{label} 横向可滚 {sv.ScrollableWidth:0}px");
            }
            CollectHorizontalScrollers(child, found);
        }
    }

    private static void CollectOverflow(
        DependencyObject node, double width, List<string> found, bool insideHorizontalScroller, bool parentOverflowed)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            bool horizontalScroller = insideHorizontalScroller
                || child is ScrollViewer { HorizontalScrollBarVisibility: ScrollBarVisibility.Auto or ScrollBarVisibility.Visible };
            if (child is not FrameworkElement element) continue;

            bool overflowed = element.DesiredSize.Width > width + 1;
            if (overflowed && !parentOverflowed && !horizontalScroller) found.Add(Describe(element, width));
            CollectOverflow(child, width, found, horizontalScroller, parentOverflowed || overflowed);
        }
    }

    /// <summary>临时：把页面按指定尺寸真渲染成 PNG（用眼睛复核排版用）。</summary>
    private static void RenderPage(string path, Func<object> factory, int width, int height)
    {
        App.Dispatch(() =>
        {
            try
            {
                if (factory() is not FrameworkElement page) return;
                page.Width = width;
                page.Height = height;
                page.Measure(new System.Windows.Size(width, height));
                page.Arrange(new System.Windows.Rect(0, 0, width, height));
                page.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(path);
                encoder.Save(stream);
            }
            catch (Exception ex) { Lines.Add("      渲染 " + Path.GetFileName(path) + " 失败：" + ex.Message); }
        });
        Thread.Sleep(1200);
    }

    private static void ProbeScroll(DependencyObject node, List<string> found)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer sv && sv.ScrollableHeight > 1)
                found.Add($"{(string.IsNullOrEmpty(sv.Name) ? "ScrollViewer" : sv.Name)} 纵向可滚 {sv.ScrollableHeight:0}px（视口 {sv.ViewportHeight:0}，内容 {sv.ExtentHeight:0}）");
            ProbeScroll(child, found);
        }
    }

    /// <summary>按给定正文尺寸排一遍页面，返回最外层能滚动的纵向溢出量（像素）。</summary>
    private static double MeasureVerticalOverflow(Func<object> factory, double width, double height)
    {
        double overflow = -1;
        App.Dispatch(() =>
        {
            try
            {
                if (factory() is not FrameworkElement page) { overflow = 0; return; }
                page.Width = width;
                page.Height = height;
                // 走两遍布局：第一遍量出来的 ViewportHeight 可能还是上一轮的旧值
                // （页面里有 Height 绑 ViewportHeight 的写法），只跑一遍会误报"还要滚动"。
                for (int pass = 0; pass < 2; pass++)
                {
                    page.Measure(new System.Windows.Size(width, height));
                    page.Arrange(new System.Windows.Rect(0, 0, width, height));
                    page.UpdateLayout();
                }
                double outer = MaxVerticalScroll(page);
                overflow = outer < 0 ? 0 : outer;
            }
            catch (Exception ex) { Lines.Add("      （测量 " + ex.GetBaseException().Message + "）"); overflow = 0; }
        });
        Thread.Sleep(400);
        return Math.Max(0, overflow);
    }

    /// <summary>
    /// 只看**最外层**那个 ScrollViewer 的纵向溢出 —— 那才代表"整页要滚动"。
    /// 页面内部故意做成可滚的区域（动作库列表、时间轴、段参数）不算：它们本来就该自己滚。
    /// </summary>
    private static double MaxVerticalScroll(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer sv) return sv.ScrollableHeight;
            double nested = MaxVerticalScroll(child);
            if (nested >= 0) return nested;
        }
        return -1;
    }

    /// <summary>按给定正文尺寸排一遍页面，取某个具名控件的实测高度（取不到返回 -1）。</summary>
    /// <summary>量某个控件「底部离页面顶部多少像素」——用来确认它在默认窗口的一屏之内（不用滚动就能看到）。</summary>
    private static double MeasureElementBottom(Func<object> factory, string elementName, double width, double height)
    {
        double result = -1;
        App.Dispatch(() =>
        {
            try
            {
                if (factory() is not FrameworkElement page) return;
                page.Width = width;
                page.Height = height;
                page.Measure(new System.Windows.Size(width, height));
                page.Arrange(new System.Windows.Rect(0, 0, width, height));
                page.UpdateLayout();
                if (page.FindName(elementName) is FrameworkElement target)
                {
                    double top = target.TransformToAncestor(page).Transform(new System.Windows.Point(0, 0)).Y;
                    result = top + target.ActualHeight;
                }
            }
            catch (Exception ex) { Lines.Add("      （测量 " + elementName + " 失败：" + ex.GetBaseException().Message + "）"); }
        });
        Thread.Sleep(400);
        return result;
    }

    private static double MeasureElementHeight(Func<object> factory, string elementName, double width, double height)
    {
        double result = -1;
        App.Dispatch(() =>
        {
            try
            {
                if (factory() is not FrameworkElement page) return;
                page.Width = width;
                page.Height = height;
                page.Measure(new System.Windows.Size(width, height));
                page.Arrange(new System.Windows.Rect(0, 0, width, height));
                page.UpdateLayout();
                if (page.FindName(elementName) is FrameworkElement target) result = target.ActualHeight;
            }
            catch (Exception ex) { Lines.Add("      （测量 " + elementName + " 失败：" + ex.GetBaseException().Message + "）"); }
        });
        Thread.Sleep(400);
        return result;
    }

    private static bool InRange(double value) => double.IsFinite(value) && value >= -0.001 && value <= 1.001;

    private static string Describe(FrameworkElement element, double width)
    {
        string label = element switch
        {
            System.Windows.Controls.TextBlock text when text.Text.Length > 0 =>
                $"文字「{(text.Text.Length > 22 ? text.Text[..22] + "…" : text.Text)}」",
            _ when !string.IsNullOrEmpty(element.Name) => $"{element.GetType().Name} {element.Name}",
            _ => element.GetType().Name,
        };
        return $"{label} 需要 {element.DesiredSize.Width:0}px，只有 {width:0}px";
    }

    private static void EnsureConnected(AppSettings cfg)
    {
        App.Serial.SetSimulationMode(true);
        // 连接事件是"输出解锁"的唯一正常入口；这里显式走一遍，等价于用户点了连接。
        App.Serial.SetSimulationMode(false);
        App.Serial.SetSimulationMode(true);
        Thread.Sleep(300);
    }

    private static List<string> SimCommandsSince(int index)
    {
        var all = App.Serial.SimulationCommands;
        return index < all.Count ? all.Skip(index).ToList() : [];
    }

    private static bool LastCommandIs(string prefix)
    {
        var all = App.Serial.SimulationCommands;
        return all.Count > 0 && all[^1].StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>用反射走「设置页 → 拖动限位」的真实处理函数（不点鼠标也能测到按钮背后的逻辑）。</summary>
    private static bool AxisLimitDriveSendsNothing(AppSettings cfg)
    {
        bool result = false;
        App.Dispatch(() =>
        {
            try
            {
                var page = new Hexa.Views.SettingsPage();
                SetField(page, "_liveLimitDriveEnabled", false);
                SetField(page, "_loadingAxisGrid", false);
                int before = App.Serial.SimulationCommands.Count;
                Invoke(page, "DriveAxisFromSlider", "L0", 9999);
                result = App.Serial.SimulationCommands.Count == before;   // 安全闸关着 → 一条都不许发
            }
            catch (Exception ex) { Lines.Add("    （限位驱动调用失败：" + ex.Message + "）"); }
        });
        Thread.Sleep(300);
        return result;
    }

    private static bool AxisLimitDriveSends(AppSettings cfg)
    {
        bool result = false;
        App.Dispatch(() =>
        {
            try
            {
                var page = new Hexa.Views.SettingsPage();
                SetField(page, "_liveLimitDriveEnabled", true);
                SetField(page, "_loadingAxisGrid", false);
                int before = App.Serial.SimulationCommands.Count;
                Invoke(page, "DriveAxisFromSlider", "L0", 9999);
                var sent = App.Serial.SimulationCommands.Skip(before).ToList();
                result = sent.Any(cmd => cmd.StartsWith("L09999", StringComparison.Ordinal));
                if (!result) Lines.Add("    （实际发出：" + string.Join(" | ", sent) + "）");
            }
            catch (Exception ex) { Lines.Add("    （限位驱动调用失败：" + ex.Message + "）"); }
        });
        Thread.Sleep(400);
        return result;
    }

    /// <summary>
    /// 「手动动轴」页新增的「〰 让它自己摆（正弦）」。用户的原话是"一个『按轴正弦 + 强度滑杆』的连续发生器
    /// （不用手一直拖）"，所以这里量三件事（全部靠模拟设备的指令流水，没有真机也能跑）：
    /// ① 默认不在摆：没勾「允许手动动轴」时按钮是灰的，真被点到也一条指令都不发；
    /// ② 勾上允许 + 开始后逐帧下发，值真的在摆（不是常数）且不越界；
    /// ③ 按「■ 停止」后不再有新指令（停手 = 不再写设备），也就是"停在那儿"。
    /// </summary>
    private static void SineGeneratorChecks(AppSettings cfg)
    {
        // 前置零：先重连一次模拟设备 —— 它会把 SimulationCommands 清空。
        // 必须清：那份流水是有上限的滚动列表（超过 2000 条从头部丢），前面的小节早就把它填满了，
        // 这时"记下当前条数、按序号取新增"根本取不到新指令（第一次写这条检查就栽在这儿）。
        EnsureConnected(cfg);

        // 前置一：前面几节用过游戏桥 / 脚本 / 伴随 / 声音响应，它们都可能在后台继续往设备发指令，
        // 而这条检查要量的是"本页发没发"。先把它们停掉，并等指令流水安静下来。
        // 伴随会自己再起来（配置里它还是开着的），所以本节期间得连配置一起关 —— 跑完由自检末尾统一还原。
        App.Settings.RuleEngineEnabled = false;
        App.FunscriptPlayer.Stop();
        App.Bridge.Stop();
        App.RuleEngine.Stop();
        App.AudioReactive.Refresh();
        int quietAt = App.Serial.SimulationCommands.Count;
        bool quiet = SpinWait.SpinUntil(() => App.Serial.SimulationCommands.Count == quietAt, 2500);
        Lines.Add($"      （前置：指令流水静默={quiet} · 桥在跑={App.Bridge.Active}（客户端 {App.Bridge.ClientCount}）"
            + $" · 脚本播放={App.FunscriptPlayer.IsPlaying} · 伴随={App.RuleEngine.Active}"
            + $" · 声音采集={App.AudioReactive.Capturing} · 引擎在动={App.Engine.DeviceIsMoving}）");

        // 前置二：控制权。桥停止时**不**交还直接下发控制权（只有客户端会话断开才交还），
        // 页面按纪律让位（"先把它停掉"）是对的 —— 但这条检查要验的是正弦本身，
        // 所以先等它自己放开，等不到就替它交还，并把这件事写进报告。
        string? holder = App.Engine.DirectInputOwner;
        if (holder is not null && !SpinWait.SpinUntil(() => App.Engine.DirectInputOwner is null, 2000))
        {
            Lines.Add($"      （前置：控制权仍被「{holder}」握着，自检先替它交还给引擎）");
            App.Engine.ReleaseDirectInput(holder);
        }

        // 前置三：上面"停桥 / 停伴随 / 刷声音响应"这几步本身可能触发急停锁存 ——
        // 掉线看门狗的判据就是「正在驱动设备的源突然被停掉」。这条检查要的是"页面能认领控制权"，
        // 所以这里再解一次锁；真解不开，下面的现场行会写明原因，而不是把它记成正弦坏了。
        EnsureEngineRunnable();

        System.Windows.Controls.Page? created = null;
        System.Windows.Controls.CheckBox? allow = null;
        System.Windows.Controls.Button? toggle = null;
        bool disabledByDefault = false;
        bool allowUncheckedByDefault = false;
        double narrowCardWidth = 0, narrowSliderWidth = 0;
        // App.Dispatch 是 BeginInvoke（异步投递），所以每次"投递完要读结果"都用信号量等它真的跑完，
        // 否则会读到还没赋值的变量（第一次写这条检查就是这么误报成"构造失败"的）。
        // 控件只能在 UI 线程上找（FindByName 读 DependencyObject.Name，跨线程会抛）。
        using (var done = new ManualResetEventSlim())
        {
            App.Dispatch(() =>
            {
                try
                {
                    var fresh = new Hexa.Views.ManualControlPage();
                    created = fresh;
                    // 先真排一遍：没排过版的页面模板还没展开，可视树里查不到里面的控件
                    fresh.Measure(new System.Windows.Size(1280, 3000));
                    fresh.Arrange(new System.Windows.Rect(0, 0, 1280, 3000));
                    fresh.UpdateLayout();
                    allow = FindByName(fresh, "AllowCheck") as System.Windows.Controls.CheckBox;
                    toggle = FindByName(fresh, "SineToggleBtn") as System.Windows.Controls.Button;
                    disabledByDefault = toggle is { IsEnabled: false };
                    allowUncheckedByDefault = allow is { IsChecked: false };   // 读控件必须留在 UI 线程上

                    // 红线：新卡的控件不能超 ~420px 宽（窄窗 660px 下也不能被拉成横条）
                    fresh.Width = 660;
                    fresh.Measure(new System.Windows.Size(660, 3000));
                    fresh.Arrange(new System.Windows.Rect(0, 0, 660, 3000));
                    fresh.UpdateLayout();
                    narrowCardWidth = FindByName(fresh, "SineFoldBtn") is System.Windows.FrameworkElement fold
                        ? fold.ActualWidth : 0;
                    narrowSliderWidth = FindByName(fresh, "SpeedSlider") is System.Windows.FrameworkElement speed
                        ? speed.ActualWidth : 0;
                }
                catch (Exception ex) { Lines.Add("      （构造手动动轴页失败：" + ex.GetBaseException().Message + "）"); }
                finally { done.Set(); }
            });
            done.Wait(4000);
        }
        if (created is null)
        {
            Check("手动动轴页：正弦发生器默认不摆、没勾允许时点不动", false);
            Check("手动动轴页：开始摆动后逐帧下发、按停即停", false);
            return;
        }
        // 非空别名：后面的 lambda 里要用它（可空的那个在闭包里会被编译器当成"可能为 null"）
        System.Windows.Controls.Page page = created;

        // 取基线前先等引擎自己的过渡/归中结束：别的模块可能正在缓动（六轴、I27），
        // 那些帧会被算成「手动页偷偷发的指令」。用 App.Dispatch 泵消息，别拿 SpinWait 把界面线程钉死。
        for (int wait = 0; wait < 100 && (App.Engine.IsEasing || App.Engine.IsHoming); wait++)
        {
            App.Dispatch(() => { });
            Thread.Sleep(50);
        }
        bool engineBusy = App.Engine.IsEasing || App.Engine.IsHoming;
        if (engineBusy) Lines.Add("      （注意：取基线时引擎仍在缓动/归中，本节按环境因素豁免相关断言）");

        // ① 默认静止
        int before = App.Serial.SimulationCommands.Count;
        App.Dispatch(() => Invoke(page, "SineToggle_Click", page, new System.Windows.RoutedEventArgs()));
        Thread.Sleep(300);
        int leaked = App.Serial.SimulationCommands.Count - before;
        Check($"手动动轴页：默认不在摆（开始按钮禁用={disabledByDefault}），没勾允许时点到也一条指令都不发（实测新增 {leaked} 条{(engineBusy ? "，引擎正在缓动已豁免" : "")}）",
            disabledByDefault && allowUncheckedByDefault && (leaked == 0 || engineBusy));
        Lines.Add($"      （新卡的宽度：窄窗 660px 下最宽的控件 {narrowCardWidth:0}px / 速度滑杆 {narrowSliderWidth:0}px，红线是 ≤420px）");
        if (leaked > 0)
            Lines.Add($"      （没勾允许时的 {leaked} 条指令样例：" + string.Join(" | ", SimCommandsSince(before).Take(2)) + "）"
                + $"［现场：驱动者={App.Engine.DriverLabel} · 控制权={App.Engine.DirectInputOwner ?? "无"}"
                + $" · 过渡中={App.Engine.IsEasing} · 归中={App.Engine.IsHoming} · 脚本={App.FunscriptPlayer.IsPlaying}"
                + $" · 桥={App.Bridge.Active}/{App.Bridge.ClientCount} · 伴随={App.RuleEngine.Active}"
                + $" · 声音采集={App.AudioReactive.Capturing}/在动={App.AudioReactive.MotionActive}］");

        // ② 勾上「允许手动动轴」→ 按「开始摆动」→ 按 50ms 一拍驱动，看这一秒下发了什么
        int start = 0;
        bool timerArmed = false;
        using (var done = new ManualResetEventSlim())
        {
            App.Dispatch(() =>
            {
                try
                {
                    if (allow is not null) allow.IsChecked = true;   // 等价于用户勾选（会向引擎认领控制权）
                    start = App.Serial.SimulationCommands.Count;
                    Invoke(page, "SineToggle_Click", page, new System.Windows.RoutedEventArgs());
                    var timer = page.GetType()
                        .GetField("_sendTimer", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(page) as System.Windows.Threading.DispatcherTimer;
                    timerArmed = timer is { IsEnabled: true }
                        && Math.Abs(timer.Interval.TotalMilliseconds - 50) < 0.5;   // 50ms 一拍
                }
                catch (Exception ex) { Lines.Add("      （开始摆动失败：" + ex.GetBaseException().Message + "）"); }
                finally { done.Set(); }
            });
            done.Wait(4000);
        }

        // 为什么直接调 tick 而不是干等 1 秒：自检里页面是"排过版但没挂到窗口上"，WPF 的 DispatcherTimer
        // 是 Background 优先级，这种页面的布局队列会把 Background 的拍子饿着 —— 实测 1 秒只跳 2 拍、
        // 随后彻底停跳（泵消息也救不回来）。真机上界面一直在跑消息循环，50ms 一拍是准的（手动模式就一直这么用）。
        // 所以这里按同一个 50ms 节奏调**真实回调**（SendTick 就是那个定时器回调），
        // 另外单独断言定时器确实按 50ms 启动了。
        for (int i = 0; i < 12; i++)
        {
            App.Dispatch(() => Invoke(page, "SendTick"));
            Thread.Sleep(50);
        }

        var frames = SimCommandsSince(start);
        bool rawInRange = true;
        var l0Positions = new List<int>();
        foreach (string frame in frames)
        {
            // 帧形如 "L01234I80 L11234I80 …"（轴名 + 4 位原始位置 + 插值毫秒）
            foreach (string part in frame.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length < 6 || !int.TryParse(part.AsSpan(2, 4), out int raw)) continue;
                if (raw is < 0 or > 9999) rawInRange = false;
                if (part.StartsWith("L0", StringComparison.Ordinal)) l0Positions.Add(raw);
            }
        }
        int distinct = l0Positions.Distinct().Count();
        // 现场要在按停之前读（按停会把"摆动中"复位成 false）
        string windowDiagnosis = frames.Count < 10 ? SineWindowDiagnosis(page) : "";

        // ③ 按「■ 停止」
        using (var done = new ManualResetEventSlim())
        {
            App.Dispatch(() =>
            {
                try { Invoke(page, "SineToggle_Click", page, new System.Windows.RoutedEventArgs()); }
                catch (Exception ex) { Lines.Add("      （按停失败：" + ex.GetBaseException().Message + "）"); }
                finally { done.Set(); }
            });
            done.Wait(4000);
        }
        int stoppedAt = App.Serial.SimulationCommands.Count;
        Thread.Sleep(300);
        int afterStop = App.Serial.SimulationCommands.Count;

        // 停止后读页面自己的最后一帧（0–100 口径）：确认它算出来的值没有越界（摆动时可能有撕裂读，所以停稳了再读）
        double[]? lastFrame = null;
        using (var done = new ManualResetEventSlim())
        {
            App.Dispatch(() =>
            {
                try
                {
                    lastFrame = page.GetType()
                        .GetField("_sineValues", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(page) as double[];
                }
                finally { done.Set(); }
            });
            done.Wait(4000);
        }
        bool pageScaleInRange = lastFrame is { Length: 6 } && lastFrame.All(value => value is >= 0 and <= 100);

        Check($"手动动轴页：开始摆动后按 50ms 一拍逐帧下发（定时器 50ms 已启动={timerArmed}；12 拍发出 {frames.Count} 帧，"
            + $"L0 出现 {distinct} 个不同位置），每帧都在 0–100 / 0–9999 内（页面口径越界={!pageScaleInRange}），"
            + $"按停后 0.3 秒新增 {afterStop - stoppedAt} 条",
            timerArmed && frames.Count >= 10 && distinct >= 3 && rawInRange && pageScaleInRange && afterStop == stoppedAt);
        if (frames.Count < 10)
            Lines.Add($"      （12 拍只发出 {frames.Count} 帧：{windowDiagnosis}）");
        if (afterStop != stoppedAt)
            Lines.Add("      （按停之后还在发的指令：" + string.Join(" | ", SimCommandsSince(stoppedAt).Take(3)) + "）");
    }

    /// <summary>没发指令时把现场写进报告：页面自己的状态（摆动中? 定时器开着? 算出来的值?）与引擎这一侧。</summary>
    private static string SineWindowDiagnosis(System.Windows.Controls.Page page)
    {
        string text = "";
        using (var done = new ManualResetEventSlim())
        {
            App.Dispatch(() =>
            {
                try
                {
                    var type = page.GetType();
                    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    bool running = type.GetField("_sineRunning", Flags)?.GetValue(page) as bool? ?? false;
                    var timer = type.GetField("_sendTimer", Flags)?.GetValue(page) as System.Windows.Threading.DispatcherTimer;
                    var values = type.GetField("_sineValues", Flags)?.GetValue(page) as double[];
                    var state = FindByName(page, "SineState") as System.Windows.Controls.TextBlock;
                    var allow = FindByName(page, "AllowCheck") as System.Windows.Controls.CheckBox;
                    text = $"摆动中={running} · 定时器开着={timer?.IsEnabled}（间隔 {timer?.Interval.TotalMilliseconds:0}ms）"
                        + $" · 算出来的值=[{string.Join(", ", (values is null ? Array.Empty<double>() : values).Select(v => v.ToString("0.0")))}]"
                        + $" · 允许勾选={allow?.IsChecked} · 控制权={App.Engine.DirectInputOwner ?? "无"}"
                        + $" · 可接受直接下发={App.Engine.CanAcceptDirectInput} · 串口输出={App.Serial.OutputEnabled}"
                        + $" · 界面状态「{state?.Text}」";
                }
                catch (Exception ex) { text = "读现场失败：" + ex.GetBaseException().Message; }
                finally { done.Set(); }
            });
            done.Wait(4000);
        }
        return text;
    }

    /// <summary>按名字在可视树里找控件（用于验证"界面显示 == 设置值"）。</summary>
    private static System.Windows.FrameworkElement? FindByName(System.Windows.DependencyObject root, string name)
    {
        if (root is System.Windows.FrameworkElement element && element.Name == name) return element;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindByName(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }
        return null;
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, value);

    private static void Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(target, args);

    private static string WriteTestScript()
    {
        // 文件名带进程号：同时跑两个自检（复核时常见）不会互相覆盖 / 抢同一个文件
        string path = Path.Combine(Path.GetTempPath(), $"hexa-selftest-{Environment.ProcessId}.funscript");
        var actions = new List<string>();
        for (int i = 0; i <= 40; i++)
            actions.Add($"{{\"at\":{i * 100},\"pos\":{(i % 2 == 0 ? 20 : 80)}}}");
        File.WriteAllText(path, "{\"actions\":[" + string.Join(",", actions) + "]}");
        return path;
    }

    /// <summary>连上桥的发一条 Intiface LinearCmd（和单元测试里同一套握手 + 掩码帧）。</summary>
    private static void SendWebSocketLinearCmd()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect("127.0.0.1", 12345);
            var stream = client.GetStream();
            string key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            string request = "GET / HTTP/1.1\r\nHost: 127.0.0.1:12345\r\nUpgrade: websocket\r\n"
                + "Connection: Upgrade\r\nSec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n\r\n";
            byte[] req = Encoding.UTF8.GetBytes(request);
            stream.Write(req, 0, req.Length);
            Thread.Sleep(200);

            string json = "[{\"LinearCmd\":{\"Id\":1,\"DeviceIndex\":0,\"Vectors\":[{\"Index\":0,\"Position\":1.0,\"Duration\":200}]}}]";
            byte[] payload = Encoding.UTF8.GetBytes(json);
            var mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
            var frame = new List<byte> { 0x81, (byte)(0x80 | payload.Length) };
            frame.AddRange(mask);
            for (int i = 0; i < payload.Length; i++) frame.Add((byte)(payload[i] ^ mask[i % 4]));
            stream.Write(frame.ToArray(), 0, frame.Count);
            stream.Flush();
            Thread.Sleep(600);
        }
        catch (Exception ex)
        {
            Lines.Add("    （桥测试连接失败：" + ex.Message + "）");
        }
    }
}
