using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hexa.Services;

namespace Hexa.Views;

/// <summary>
/// 💞 伴随：Lovense 游戏联动、🌉 游戏桥、🕹 游戏手柄这些「附加玩法」从「游玩」页搬出来，
/// 单独一页。它们本质上都是「让别的东西（游戏 / 手柄）来指挥设备」，跟游玩页
/// （自己调档位、点预设、看 3D）不是一件事，混在一页里又长又要折叠，找起来费劲。
/// 这些开关都写在 App.Settings 里、服务在 App.*，所以换页不影响它们继续跑。
/// </summary>
public partial class CompanionPage : Page
{

    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    /// <summary>当前在看的卡片（LovenseCard / BridgeCard / GamepadCard）。只影响显示，不影响功能。</summary>
    private string _activeCardName = "LovenseCard";

    public CompanionPage()
    {
        InitializeComponent();

        InitLovenseBar();
        RefreshBridgeSectionSummary();
        ShowCard(_activeCardName);   // 默认只看「游戏联动」那一块，和顶部高亮对齐

        // 250ms 刷一遍：桥的收发条数、Lovense 连接状态、实时轴读数。看不见就停，不白烧 CPU。
        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _statusTimer.Tick += (_, _) =>
        {
            RefreshBridgeSectionSummary();
            RefreshLovenseStatus();
            UpdateLovenseLive();
        };
        _statusTimer.Start();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                if (!_statusTimer.IsEnabled) _statusTimer.Start();
                RefreshBridgeSectionSummary();
                RefreshLovenseStatus();
                UpdateLovenseLive();
            }
            else
            {
                _statusTimer.Stop();
            }
        };

        // 页面被切走 / 关窗口：手柄线程必须停，否则它一直占着「直控」标记，设备看起来被占着。
        Unloaded += (_, _) => StopGamepad();
    }

    /// <summary>停手柄的共用收尾：线程标记、直控占用、界面文案一次清干净。</summary>
    private void StopGamepad()
    {
        if (!_gpActive) return;
        _gpActive = false;
        _gpActivity = 0;
        App.Engine.ReleaseDirectInput("gamepad");
        App.RuleEngine?.SetGamepadActivity(0);
        App.Engine.ApplyGamepadActivity(0);
    }

    // ── Gamepad ──
    private volatile bool _gpActive;
    private Thread? _gpThread;
    private readonly double[] _prevGpVals = new double[6];
    private bool   _gpPrevInit;      // 首次采样先填充基线，避免误报活跃度
    private double _gpActivity;      // 手柄活跃度 0..1（摇杆/扳机变化率）

    // ── XInput P/Invoke ──
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);
    /// <summary>
    /// Lovense 那一行：开关与端口直接写配置并即时重开服务。
    /// 端口只改了没重开的话，游戏连的还是旧端口，所以永远走一次 Stop/Start。
    /// </summary>
    private void InitLovenseBar()
    {
        if (LovenseEnabledCheck is null) return;
        LovenseEnabledCheck.IsChecked = App.Settings.LovenseBridgeEnabled;
        LovensePortBox.Text = App.Settings.LovenseBridgePort.ToString();
        LovenseRange.LowerValue = App.Settings.LovenseStrokeTopPercent;
        LovenseRange.UpperValue = App.Settings.LovenseStrokeDeepPercent;
        LovenseRotateRange.LowerValue = App.Settings.LovenseRotateTopPercent;
        LovenseRotateRange.UpperValue = App.Settings.LovenseRotateDeepPercent;
        ApplyLovenseRotate();
        LovenseLocalOnlyCheck.IsChecked = App.Settings.LovenseLocalOnly;
        LovenseExtraRotateToyCheck.IsChecked = App.Settings.LovenseExtraRotateToy;
        AutoTwistCheck.IsChecked = App.Settings.AutoTwistEnabled;
        AutoTwistSlider.Value = App.Settings.AutoTwistAmplitudePercent;
        AutoTwistCheck.Checked += (_, _) => ApplyAutoTwist();
        AutoTwistCheck.Unchecked += (_, _) => ApplyAutoTwist();
        AutoTwistSlider.ValueChanged += (_, _) => ApplyAutoTwist();
        ApplyAutoTwist();
        // 拖条时让机器立刻走到那个位置（手感预览）。订阅放在初值赋值之后，免得打开页面就把设备拽走。
        LovenseRange.LowerValueChanged += (_, _) => PreviewLovenseAxis(0, LovenseRange.LowerValue);
        LovenseRange.UpperValueChanged += (_, _) => PreviewLovenseAxis(0, LovenseRange.UpperValue);
        LovenseRotateRange.LowerValueChanged += (_, _) => PreviewLovenseAxis(3, LovenseRotateRange.LowerValue);
        LovenseRotateRange.UpperValueChanged += (_, _) => PreviewLovenseAxis(3, LovenseRotateRange.UpperValue);
        UpdateLovenseAddressText();
        RefreshLovenseStatus();
    }

    private void UpdateLovenseAddressText()
    {
        if (LovenseAddressText is null) return;
        var addresses = LovenseBridgeService.LocalAddresses();
        string where = addresses.Count == 0
            ? "本机没找到局域网地址，先连 Wi-Fi 或网线"
            : "IP 填 " + string.Join(" 或 ", addresses) + "（同一台电脑上填 127.0.0.1 也行）";
        LovenseAddressText.Text = "游戏设置里这样填：" + where + "；SSL 端口填上面的数字；「Lovense 网页」选「是」。";
    }

    private void RefreshLovenseStatus()
    {
        if (LovenseStatusText is null) return;
        LovenseBridgeService? lovense = App.Lovense;
        if (lovense is null || !lovense.IsRunning)
        {
            LovenseStatusText.Text = lovense is null ? "未开启" : lovense.Status;
            LovenseStatusText.Foreground = (Brush)FindResource("Muted");
            return;
        }
        string trust = lovense.CertificateTrusted ? "" : " · 证书还没被信任（游戏会拒连，点右边「信任证书」）";
        LovenseStatusText.Text = lovense.Status + trust;
        if (LovenseTrustBtn is not null)
            LovenseTrustBtn.Visibility = lovense.CertificateTrusted ? Visibility.Collapsed : Visibility.Visible;
        LovenseStatusText.Foreground = lovense.Status.Contains("已连上")
            ? (Brush)FindResource("Accent")
            : lovense.CertificateTrusted ? (Brush)FindResource("Muted") : (Brush)FindResource("Warning");
    }

    /// <summary>
    /// 装证书这件事必须走独立进程：certutil 会弹 Windows 确认框，在界面线程里等就等于卡死。
    /// </summary>
    private async void LovenseTrust_Click(object sender, RoutedEventArgs e)
    {
        LovenseBridgeService? lovense = App.Lovense;
        if (lovense is null || !lovense.IsRunning) return;
        string cer = lovense.ExportTrustFile();
        if (string.IsNullOrEmpty(cer))
        {
            LovenseStatusText.Text = "证书导出失败，看日志";
            return;
        }
        LovenseTrustBtn.IsEnabled = false;
        LovenseStatusText.Text = "等 Windows 弹框，点「是」";
        try
        {
            var start = new ProcessStartInfo("certutil.exe", "-addstore -user Root " + "\"" + cer + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? process = Process.Start(start);
            if (process is not null) await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            LovenseStatusText.Text = "装证书失败：" + ex.Message;
            LovenseTrustBtn.IsEnabled = true;
            return;
        }
        lovense.RefreshTrustState();
        LovenseTrustBtn.IsEnabled = true;
        RefreshLovenseStatus();
    }

    private void RestartLovenseBridge()
    {
        App.Settings.Save();
        LovenseBridgeService? lovense = App.Lovense;
        if (lovense is not null)
        {
            lovense.LocalOnly = App.Settings.LovenseLocalOnly;
            lovense.ExtraRotateToy = App.Settings.LovenseExtraRotateToy;
            lovense.AutoTwistEnabled = App.Settings.AutoTwistEnabled;
            lovense.AutoTwistAmplitudePercent = App.Settings.AutoTwistAmplitudePercent;
        }
        lovense?.Stop();
        if (!App.Settings.LovenseBridgeEnabled) { RefreshLovenseStatus(); return; }
        lovense?.Start(App.Settings.LovenseBridgePort, LovenseBridgeService.DefaultHttpPort);
        RefreshLovenseStatus();
    }

    private void LovenseEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (LovenseEnabledCheck is null) return;
        App.Settings.LovenseBridgeEnabled = LovenseEnabledCheck.IsChecked == true;
        RestartLovenseBridge();
    }

    /// <summary>
    /// 一根条子两个圆点：左边那个是「最高」（抬到这儿就停），右边那个是「最深」（插到这儿就停）。
    /// 拖动时立刻改区间（不用等游戏再发指令），松手才落盘。
    /// </summary>
    private void LovenseRange_Changed(object sender, EventArgs e) => ApplyLovenseStroke();

    private void ApplyLovenseStroke()
    {
        if (LovenseRange is null || LovenseRangeText is null) return;
        (double top, double deep) = LovenseProtocol.StrokeWindow(
            (int)Math.Round(LovenseRange.LowerValue), (int)Math.Round(LovenseRange.UpperValue));
        int topPercent = (int)Math.Round(top);
        int deepPercent = (int)Math.Round(deep);
        LovenseRangeText.Text = "最高 " + topPercent + "%　最深 " + deepPercent + "%";
        if (App.Lovense is not null)
        {
            App.Lovense.StrokeTopPercent = topPercent;
            App.Lovense.StrokeDeepPercent = deepPercent;
        }
        App.Settings.LovenseStrokeTopPercent = topPercent;
        App.Settings.LovenseStrokeDeepPercent = deepPercent;
    }


    private void ApplyAutoTwist()
    {
        if (AutoTwistCheck is null || AutoTwistSlider is null || AutoTwistText is null) return;
        bool enabled = AutoTwistCheck.IsChecked == true;
        double amp = Math.Clamp(AutoTwistSlider.Value, 0, 60);
        AutoTwistText.Text = (int)Math.Round(amp) + "%";
        App.Settings.AutoTwistEnabled = enabled;
        App.Settings.AutoTwistAmplitudePercent = amp;
        if (App.Lovense is not null)
        {
            App.Lovense.AutoTwistEnabled = enabled;
            App.Lovense.AutoTwistAmplitudePercent = amp;
        }
        App.Settings.Save();
    }

    private void LovenseLocalOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (LovenseLocalOnlyCheck is null) return;
        App.Settings.LovenseLocalOnly = LovenseLocalOnlyCheck.IsChecked == true;
        RestartLovenseBridge();
    }

    private void LovenseExtraRotateToy_Changed(object sender, RoutedEventArgs e)
    {
        if (LovenseExtraRotateToyCheck is null) return;
        bool extra = LovenseExtraRotateToyCheck.IsChecked == true;
        App.Settings.LovenseExtraRotateToy = extra;
        if (App.Lovense is not null) App.Lovense.ExtraRotateToy = extra;
        App.Settings.Save();
    }

    private void LovenseRotate_Changed(object sender, EventArgs e) => ApplyLovenseRotate();

    /// <summary>扭转条：两个圆点就是 R0 的两头，游戏强度只决定这段里摆多大。</summary>
    private void ApplyLovenseRotate()
    {
        if (LovenseRotateRange is null || LovenseRotateText is null) return;
        (double top, double deep) = LovenseProtocol.StrokeWindow(
            (int)Math.Round(LovenseRotateRange.LowerValue), (int)Math.Round(LovenseRotateRange.UpperValue));
        int topPercent = (int)Math.Round(top);
        int deepPercent = (int)Math.Round(deep);
        LovenseRotateText.Text = topPercent + "% – " + deepPercent + "%";
        if (App.Lovense is not null)
        {
            App.Lovense.RotateTopPercent = topPercent;
            App.Lovense.RotateDeepPercent = deepPercent;
        }
        App.Settings.LovenseRotateTopPercent = topPercent;
        App.Settings.LovenseRotateDeepPercent = deepPercent;
    }

    private DateTime _lovensePreviewUtc = DateTime.MinValue;

    /// <summary>
    /// 「看着机器走到我拉的那一格」：拖动行程条/扭转条时，立刻把对应那根轴送到该百分比。
    /// 只动那一根轴，其余保持 50（中位）；不改区间设置，也不影响游戏真要动作时的算法。
    /// 30ms 一把：拖动时事件非常密，逐帧发会把串口写满。
    /// </summary>
    private void PreviewLovenseAxis(int axis, double percent)
    {
        if (App.Engine is null || axis is < 0 or > 5) return;
        DateTime now = DateTime.UtcNow;
        if ((now - _lovensePreviewUtc).TotalMilliseconds < 30) return;
        _lovensePreviewUtc = now;
        double[] values = [50, 50, 50, 50, 50, 50];
        // 行程轴（L0）要翻一次：条上小的那端才抬得高，而轴值小了反而往下。
        values[axis] = axis == 0
            ? LovenseProtocol.StrokeAxisValue(percent)
            : Math.Clamp(percent, 0, 100);
        // 0.3 秒插值：拖得再快也不会一帧甩过去，看起来是「跟着手走」而不是弹过去。
        App.Engine.TrySendDirectAxes(values, 0.3);
    }

    private void LovenseRotate_LostMouseCapture(object sender, MouseEventArgs e)
    {
        ApplyLovenseRotate();
        App.Settings.Save();
    }

    /// <summary>实时读数：直接读引擎最后一批输出（过完限速器、真正发给机器的位置），不是我们想发什么。</summary>
    private void UpdateLovenseLive()
    {
        if (LovenseLiveText is null) return;
        double[] output = App.Engine.GetLastOutputSnapshot();
        LovenseLiveText.Text = "现在 L0 " + (int)Math.Round(output[0]) + "% · R0 "
            + (int)Math.Round(output[3]) + "%";
    }

    private void LovenseRange_LostMouseCapture(object sender, MouseEventArgs e)
    {
        ApplyLovenseStroke();
        App.Settings.Save();
    }

    private void LovensePort_LostFocus(object sender, RoutedEventArgs e)
    {
        if (LovensePortBox is null) return;
        if (!int.TryParse(LovensePortBox.Text, out int port) || port is < 1024 or > 65535)
        {
            LovensePortBox.Text = App.Settings.LovenseBridgePort.ToString();
            return;
        }
        if (port == App.Settings.LovenseBridgePort) return;
        App.Settings.LovenseBridgePort = port;
        RestartLovenseBridge();
    }

    /// <summary>顶部功能按钮：一次只显示选中的那一块卡片（和「测试台」同一套用法）。</summary>
    private void FeatureSwitch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name) ShowCard(name);
    }

    /// <summary>只显示一张卡片，并把对应按钮高亮；名字不认识时回到 Lovense —— 绝不出现「三块全收、满屏空白」。</summary>
    private void ShowCard(string cardName)
    {
        (string Name, FrameworkElement Card)[] cards =
        {
            ("LovenseCard", LovenseCard),
            ("BridgeCard", BridgeCard),
            ("GamepadCard", GamepadCard),
        };
        if (!cards.Any(card => card.Name == cardName)) cardName = cards[0].Name;
        _activeCardName = cardName;

        foreach ((string name, FrameworkElement card) in cards)
            card.Visibility = name == cardName ? Visibility.Visible : Visibility.Collapsed;

        foreach (Button btn in FeatureSwitchGrid.Children.OfType<Button>())
            btn.Style = (Style)FindResource((btn.Tag as string) == cardName ? "FeatureBtnOn" : "FeatureBtnOff");

        // 切过来立刻刷一次，别等 250ms 定时器把上一张卡片的旧数字留在这儿。
        RefreshLovenseStatus();
        RefreshBridgeSectionSummary();
        RefreshGamepadSummary();
        UpdateLovenseLive();
    }
    /// <summary>桥那一行摘要：开没开 / 游戏里该填什么 / 收发计数（一张卡片一句话说清状态）。</summary>
    private void RefreshBridgeSectionSummary()
    {
        if (BridgeSectionSummary is null) return;
        if (!App.Settings.GameBridgeEnabled)
        {
            BridgeSectionSummary.Text = "没开：不占端口、游戏连不上；要用时点开";
            return;
        }
        var bridge = App.Bridge;
        if (!bridge.Active)
        {
            // 以前只看"设置里开着没"，端口被占导致桥没起来时这一行仍写"已开启"，与桥面板的"启动失败"互相打脸。
            BridgeSectionSummary.Text = string.IsNullOrWhiteSpace(bridge.LastError)
                ? "没开起来：点开这一栏看原因"
                : $"启动失败：{bridge.LastError} —— 点开换一个「监听端口」再开";
            return;
        }
        BridgeSectionSummary.Text =
            $"已开启 · 游戏里填 ws://127.0.0.1:{bridge.Port} · 收到 {bridge.RxCount} / 发出 {bridge.TxCount}"
            + (bridge.ClientCount > 0 ? $" · 游戏已连（{bridge.ClientCount}）" : " · 等游戏连");
    }
    /// <summary>把当前手柄状态写成一行摘要（不用点按钮就知道插没插、能不能用）。</summary>
    private void RefreshGamepadSummary()
    {
        if (GamepadFoldSummary is null) return;
        string status = GamepadStatus?.Text ?? "";
        GamepadFoldSummary.Text = string.IsNullOrWhiteSpace(status) || status.StartsWith("还没检测", StringComparison.Ordinal)
            ? "没启用：手柄不影响设备"
            : status;
    }
    // ══════════════════════════════════════════════════════════════
    //  GAMEPAD — XInput polling on a background thread
    // ══════════════════════════════════════════════════════════════
    private void GamepadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_gpActive)
        {
            StopGamepad();   // 交还控制权 + 清活跃度：Unloaded / 切页也走这一条，别维护两份
            GamepadBtn.Content = "启用手柄";
            GamepadBtn.Style   = (Style)FindResource("BtnSecondary");
            GamepadStatus.Text = "手柄已停用（设备不会再跟着手柄动）";
            GamepadStatus.Foreground = (Brush)FindResource("Muted");
        }
        else
        {
            _gpActive = true;
            GamepadBtn.Content = "停用手柄";
            GamepadBtn.Style   = (Style)FindResource("BtnDanger");
            GamepadStatus.Text = "正在找手柄…";
            GamepadStatus.Foreground = (Brush)FindResource("Muted");
            _gpThread = new Thread(GamepadLoop) { IsBackground = true };
            _gpThread.Start();
        }
    }

    /// <summary>手柄连上了、但推不动设备时的原因（白话，跟页面顶部的写法一致）。</summary>
    private static string GamepadBusyReason()
    {
        if (!App.Engine.CanRun) return "设备现在不能动（原因见页面顶部提示）";
        if (App.Engine.RuleEngineActive) return "游戏伴随正在接管设备";
        if (App.Engine.ScriptPlaying) return "脚本正在播放（它会一直占着设备，先停脚本）";
        if (App.Engine.IsRunning) return "设备正在跑自动动作";
        if (App.Engine.IsEasing) return "设备正在过渡中";
        return "设备暂时不接受手柄直控";
    }

    private void GamepadLoop()
    {
        const uint ERROR_DEVICE_NOT_CONNECTED = 1167;
        var state  = new XINPUT_STATE();
        var cfg    = App.Settings.GamepadMap;   // 可配置映射

        while (_gpActive)
        {
            uint result = XInputGetState(0, ref state);
            if (result == ERROR_DEVICE_NOT_CONNECTED)
            {
                App.Dispatch(() =>
                {
                    GamepadStatus.Text       = "没检测到手柄：插上 USB 手柄，这里会自动认出来";
                    GamepadStatus.Foreground = (Brush)FindResource("Muted");
                });
                Thread.Sleep(1000);
                continue;
            }

            // 构造原始值字典（供 GamepadConfig.ReadAxis 解析）
            var gp = state.Gamepad;
            var raw = new Dictionary<string, double>
            {
                ["LeftTrigger"]   = gp.bLeftTrigger,
                ["RightTrigger"]  = gp.bRightTrigger,
                ["LeftStickX"]    = gp.sThumbLX,
                ["LeftStickY"]    = gp.sThumbLY,
                ["RightStickX"]   = gp.sThumbRX,
                ["RightStickY"]   = gp.sThumbRY,
            };

            var vals = new double[6];
            for (int i = 0; i < 6; i++)
                vals[i] = cfg.ReadAxis(i, raw);

            // 计算手柄活跃度（摇杆/扳机变化率）供规则引擎使用
            if (_gpPrevInit)
            {
                double deltaSum = 0;
                for (int i = 0; i < 6; i++)
                {
                    deltaSum += Math.Abs(vals[i] - _prevGpVals[i]);
                    _prevGpVals[i] = vals[i];
                }
                double inst = Math.Clamp(deltaSum / 6.0 / 100.0, 0, 1);
                _gpActivity += (inst - _gpActivity) * 0.2;   // EMA 平滑
            }
            else
            {
                for (int i = 0; i < 6; i++) _prevGpVals[i] = vals[i];
                _gpPrevInit = true;
            }
            App.RuleEngine?.SetGamepadActivity(_gpActivity);
            App.Engine.ApplyGamepadActivity(_gpActivity);

            App.Dispatch(() =>
            {
                // 手柄同样属于「直接下发源」：必须认领控制权，否则
                //  ① 悬浮窗/侧栏看不到"真在动"（急停按钮会变灰、ToolTip 还写"现在没有东西在动"）；
                //  ② 脚本正在播时两路交替写同一根轴（机器抖）。
                // 摇杆回到中位就交还，避免"只是插着手柄"也一直占着控制权。
                bool engaged = _gpActivity > 0.02;
                bool claimed = false;
                if (engaged)
                {
                    claimed = App.Engine.TryClaimDirectInput("gamepad");
                    if (claimed && App.Engine.CanAcceptDirectInput) App.Engine.TrySendDirectAxes(vals);
                }
                else
                {
                    App.Engine.ReleaseDirectInput("gamepad");
                }

                // 连上了不等于推得动：自动 / 脚本 / 游戏伴随跑着的时候，手柄下发会被引擎拒绝。
                // 这里把"为什么推不动"直接写出来，省得用户以为手柄坏了。
                // engaged=摇杆在中位。空闲不是故障：此时 claimed 为 false 是正常的，
                // 以前照抄成"手柄暂时无效"，用户会以为手柄坏了（摇杆一动文字就翻回来）。
                bool usable = engaged ? claimed : App.Engine.CanAcceptDirectInput;
                string text = usable
                    ? "✓ 手柄已连接，动摇杆设备就跟着动"
                    : $"✓ 手柄已连接，但{GamepadBusyReason()}，手柄暂时无效";
                if (GamepadStatus.Text != text)
                {
                    GamepadStatus.Text = text;
                    GamepadStatus.Foreground = (Brush)FindResource(usable ? "Success" : "Warning");
                }
                RefreshGamepadSummary();
            });
            Thread.Sleep(50);
        }
    }
}
