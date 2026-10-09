using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexa.Services;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Net.Http;
using System.Speech.Recognition;
using System.Windows;
using System.Windows.Threading;

namespace Hexa.ViewModels;

/// <summary>
/// 一条聊天记录。三种显示形态：用户气泡（IsUser=true，靠右）、AI 气泡（带头像）、
/// 居中的系统细条（IsNotice=true；IsError=true 时用警示配色并写明下一步怎么办）。
/// </summary>
public sealed partial class ChatMessage : ObservableObject
{
    public ChatMessage(string text, bool isUser, string sender = "", bool isNotice = false, bool isError = false)
    {
        _text = text;
        IsUser = isUser;
        Sender = sender;
        IsNotice = isNotice;
        IsError = isError;
    }

    /// <summary>
    /// 气泡文字。**可变 + 可通知**：流式回复是"边收边追加"的，
    /// 以前是只读属性，只能整段生成完再一次性上屏（首字要等很久）。
    /// </summary>
    [ObservableProperty] private string _text;

    public bool IsUser { get; }
    /// <summary>显示名：用户是「我」，AI 是当前人格名，系统提示是「设备 / 出错了」。</summary>
    public string Sender { get; }
    /// <summary>发出时间（HH:mm）。</summary>
    public string Time { get; } = DateTime.Now.ToString("HH:mm");
    public bool IsNotice { get; }
    public bool IsError { get; }

    /// <summary>正在逐字接收（气泡抬头显示「正在输入…」）。</summary>
    [ObservableProperty] private bool _isStreaming;

    /// <summary>这条回复中途断了 / 被取消：文字可能不完整，明确标出来，别让人以为是完整的一句。</summary>
    [ObservableProperty] private bool _interrupted;
}

public partial class AiAssistantViewModel : ObservableObject
{
    private readonly AiAssistantService _ai;

    [ObservableProperty] private ObservableCollection<ChatMessage> _messages = [];
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAiConfigured;
    [ObservableProperty] private string _connectionStatus = "未连接";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _safetyStatus = "";

    // ── 人格面板：新建 / 删除的状态 ────────────────────────────────────
    [ObservableProperty] private string _newPersonaName = "";
    [ObservableProperty] private string _newPersonaPrompt = "";
    /// <summary>做成功了的提示（新建 / 导入 / 删除）；出错信息走 ImportError。</summary>
    [ObservableProperty] private string _personaNotice = "";

    // ── TTS 播放状态（「停止说话」按钮的文案与灰显）──────────────────────
    [ObservableProperty] private bool _isAiSpeaking;

    // ── 语音识别（①，System.Speech zh-CN）──────────────────────────────
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private bool _isSpeechAvailable;
    [ObservableProperty] private bool _isSpeechInitDone;
    private SpeechRecognitionEngine? _recognizer;          // 降级：旧 SAPI
    private Windows.Media.SpeechRecognition.SpeechRecognizer? _winRtRecognizer;   // 首选：Windows 原生
    private bool _useWinRt;
    private readonly object _recLock = new();

    // ── 实时语音通话（第二阶段，默认关闭；配置在设置 → AI 助手）──────────
    private readonly RealtimeVoiceService _realtime;
    [ObservableProperty] private bool _realtimeActive;
    [ObservableProperty] private string _realtimeStatus = "";

    public IReadOnlyList<AiPersona> Personas => AiAssistantService.AllPersonas;

    [ObservableProperty] private AiPersona _selectedPersona = AiAssistantService.Personas[0];

    public AiAssistantViewModel(AiAssistantService ai)
    {
        _ai = ai;
        _isAiConfigured = _ai.IsConfigured;

        App.Serial.ConnectionChanged += _ => App.Dispatch(SyncDeviceState);
        App.Engine.StateChanged += () => App.Dispatch(SyncDeviceState);
        SyncDeviceState();

        // TTS 播放状态回推（Service 事件可能在 NAudio 线程触发，需调度回 UI 线程）
        _ai.SpeakingChanged += () => App.Dispatch(() => IsAiSpeaking = _ai.IsSpeaking);

        // 流式增量：Service 在后台线程回调，这里回 UI 线程往"当前这条气泡"上追加文字。
        _ai.StreamDelta += OnStreamDelta;
        // 高风险动作的执行前确认：Service 会 await 我们返回的布尔值（内部自己回到 UI 线程再动界面）。
        _ai.ConfirmToolAsync += OnConfirmToolAsync;

        // 实时语音通话：状态与转写都从后台线程回推，统一调度到 UI 线程。
        _realtime = new RealtimeVoiceService(App.Settings, _ai);
        _realtime.StatusChanged += msg => App.Dispatch(() => RealtimeStatus = msg);
        _realtime.UserTranscript += text =>
            App.Dispatch(() => Messages.Add(new ChatMessage(text, isUser: true, sender: "我（语音）")));
        _realtime.AssistantTranscript += text =>
            App.Dispatch(() => Messages.Add(new ChatMessage(text, isUser: false, sender: SelectedPersona.Id)));
        // 服务端断开/异常中断时，把按钮复位成「开始通话」，避免 UI 停在通话中。
        _realtime.Disconnected += () => App.Dispatch(() => RealtimeActive = false);
        // 退出时释放麦克风与 WebSocket，避免残留占用音频设备。
        if (System.Windows.Application.Current != null)
            System.Windows.Application.Current.Exit += (_, _) => _realtime.Stop();
        RefreshRealtimeState();

        _ = InitSpeechAsync();
    }

    /// <summary>页面变为可见时刷新配置/状态（用户可能在设置页改了 API key）。</summary>
    public void Refresh()
    {
        SyncDeviceState();
        RefreshRealtimeState();
    }

    // ── 语音识别 UI 状态 ───────────────────────────────────────────────

    public string ListenButtonText => IsListening ? "🔴 正在听…" : "🎤 语音输入";

    // ── 停止说话（打断 TTS）：文案随播放状态变化，未在说话时按钮灰显 ──────
    public string StopSpeakingButtonText => IsAiSpeaking ? "⏹ 停止说话" : "🔈 未在说话";

    partial void OnIsAiSpeakingChanged(bool value)
    {
        OnPropertyChanged(nameof(StopSpeakingButtonText));
        StopSpeakingCommand.NotifyCanExecuteChanged();
    }

    // ── 实时语音通话 UI 状态 ────────────────────────────────────────────

    /// <summary>「📞 实时通话」按钮文案：未通话时开始、通话中变成结束。</summary>
    public string RealtimeButtonText => RealtimeActive ? "📞 结束通话" : "📞 实时通话";

    /// <summary>配置是否齐全（总开关 + URL + Key），决定按钮是否可用。</summary>
    public bool IsRealtimeConfigured => _realtime.IsConfigured;

    partial void OnRealtimeActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(RealtimeButtonText));
        OnPropertyChanged(nameof(SpeechStatus));
        ToggleRealtimeCommand.NotifyCanExecuteChanged();
        ToggleListeningCommand.NotifyCanExecuteChanged();
    }

    /// <summary>刷新实时通话的可用性与提示文字（未配置时给出「去设置页填什么」的说明）。</summary>
    private void RefreshRealtimeState()
    {
        OnPropertyChanged(nameof(IsRealtimeConfigured));
        ToggleRealtimeCommand.NotifyCanExecuteChanged();
        if (!RealtimeActive)
            RealtimeStatus = _realtime.ConfigProblem() ?? "实时通话已就绪：点击「📞 实时通话」开始。";
    }

    /// <summary>未配置时按钮灰显；通话中始终允许点击（用于挂断）。</summary>
    private bool CanToggleRealtime() => RealtimeActive || _realtime.IsConfigured;

    [RelayCommand(CanExecute = nameof(CanToggleRealtime))]
    private async Task ToggleRealtimeAsync()
    {
        if (RealtimeActive)
        {
            _realtime.Stop();
            RealtimeActive = false;
            return;
        }

        // 实时通话自带语音识别，先停掉 System.Speech，避免两个引擎抢麦克风。
        if (IsListening) StopListening();

        RealtimeActive = true;
        RealtimeStatus = "正在连接实时语音服务…";
        ToggleRealtimeCommand.NotifyCanExecuteChanged();

        bool ok = await _realtime.StartAsync();
        if (!ok)
        {
            RealtimeActive = false;
            // 失败原因已由 RealtimeVoiceService 通过 StatusChanged 写入 RealtimeStatus。
            if (string.IsNullOrWhiteSpace(RealtimeStatus))
                RealtimeStatus = "实时通话连接失败，请检查配置与网络。";
        }
        ToggleRealtimeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 输入框下面那一行小字：把「现在能做什么 / 为什么点不动」一次说清，
    /// 不再只讲语音识别（没配 Key、AI 正在回的时候这里也要有话说）。
    /// </summary>
    public string SpeechStatus =>
        IsBusy ? "AI 正在回你…要中止就点「取消」。"
        : !IsAiConfigured ? "还没填 API Key：点上方的「去设置填 Key」，填好就能对话、也能让 AI 控制设备。"
        : RealtimeActive ? "实时通话进行中：麦克风被通话占用，语音输入暂时停用。"
        : !IsSpeechInitDone ? "正在检测语音输入…"
        : IsSpeechAvailable
            ? (IsListening ? "正在听你说，识别到一句就自动发送；再点一次按钮停止。"
                           : "点「🎤 语音输入」可以直接说话，识别到一句自动发送，不用打字。")
            : "这台电脑的语音识别用不了（没麦克风，或没装中文语音包）：直接打字一样聊。";

    partial void OnIsSpeechInitDoneChanged(bool value) => OnPropertyChanged(nameof(SpeechStatus));
    partial void OnIsSpeechAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(SpeechStatus));
        ToggleListeningCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(ListenButtonText));
        OnPropertyChanged(nameof(SpeechStatus));
        ToggleListeningCommand.NotifyCanExecuteChanged();
    }

    private void SyncDeviceState()
    {
        IsConnected = App.Serial.IsOpen;
        ConnectionStatus = App.Serial.IsOpen ? $"已连接 {App.Serial.PortName}" : "未连接";
        SafetyStatus = App.Engine.SafetyStatus;
        IsAiConfigured = _ai.IsConfigured;
        IsAiSpeaking = _ai.IsSpeaking;
    }

    partial void OnIsBusyChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        ToggleListeningCommand.NotifyCanExecuteChanged();
        CancelRequestCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SendButtonText));
        OnPropertyChanged(nameof(SpeechStatus));
    }

    /// <summary>发送按钮文案：等待回复时变成「发送中…」，一眼看出正在忙。</summary>
    public string SendButtonText => IsBusy ? "发送中…" : "发送";

    // 空内容、正在忙、没配 Key 都不给发（没配 Key 时按钮上还有 tooltip 指路）。
    private bool CanSend() => !IsBusy && IsAiConfigured && InputText.Trim().Length > 0;

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnIsAiConfiguredChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SpeechStatus));
    }

    // 取消：token 会一路透传到 HttpClient.SendAsync，取消后底层请求立即中断（不再白白跑完）。
    private CancellationTokenSource? _cts;
    private int _sendGeneration;

    // ── 流式回复：边收边追加 ──────────────────────────────────────────────
    /// <summary>这一路流式是否还在收。发送结束后置 false：晚到的分片直接丢掉，不污染下一条消息。</summary>
    private bool _streamActive;
    /// <summary>这一路流式属于哪次发送（用 generation 认出"上一轮残留的分片"）。</summary>
    private int _streamGeneration;
    /// <summary>正在追加文字的气泡；null = 还没有收到任何文字。</summary>
    private ChatMessage? _streamBubble;

    /// <summary>还没收到任何文字时，聊天区显示"三个点正在思考"（收到第一个字就收起来）。</summary>
    [ObservableProperty] private bool _isThinking;

    /// <summary>Service 的流式增量回调（**后台线程**）→ 回 UI 线程追加到当前气泡。</summary>
    private void OnStreamDelta(string text)
    {
        App.Dispatch(() =>
        {
            // 过期分片（上一轮 / 已取消）直接丢：否则文字会串到新的气泡里。
            if (!_streamActive || _streamGeneration != _sendGeneration) return;

            if (_streamBubble == null)
            {
                _streamBubble = new ChatMessage("", isUser: false, sender: SelectedPersona.Id) { IsStreaming = true };
                Messages.Add(_streamBubble);
                IsThinking = false;   // 已经有字回来了，收起"正在思考"
            }
            _streamBubble.Text += text;
        });
    }

    /// <summary>流式收尾（成功）：气泡定稿；一个字都没流过来时，按老逻辑补一条完整消息。</summary>
    private void FinishStreaming(string reply)
    {
        var bubble = _streamBubble;
        _streamBubble = null;
        _streamActive = false;

        if (bubble != null && !string.IsNullOrWhiteSpace(bubble.Text))
        {
            bubble.IsStreaming = false;
            // 以"边收边显示"的文本为准：多轮（带工具调用）时它可能比最终 reply 更全，覆盖会丢字。
            // 唯一的例外：流式文本正好是最终文本的前缀 → 说明最后几个分片还没派发过来（UI 队列），
            // 这种情况用最终文本补齐，免得尾巴少一个字。
            if (!string.IsNullOrEmpty(reply)
                && reply.Length > bubble.Text.Length
                && reply.StartsWith(bubble.Text, StringComparison.Ordinal))
            {
                bubble.Text = reply;
            }
            return;
        }
        if (bubble != null) Messages.Remove(bubble);   // 只调了工具、一个字都没说的空气泡不留

        if (!string.IsNullOrWhiteSpace(reply))
            Messages.Add(new ChatMessage(reply, isUser: false, sender: SelectedPersona.Id));
        else
            Messages.Add(new ChatMessage("AI 这次没回文字。", isUser: false, sender: "系统", isNotice: true));
    }

    /// <summary>流式中断收尾（取消 / 超时 / 断流 / 出错）：半截文字标「已中断」，空气泡删掉。</summary>
    private void AbortStreaming()
    {
        var bubble = _streamBubble;
        _streamBubble = null;
        _streamActive = false;
        IsThinking = false;

        if (bubble == null) return;
        bubble.IsStreaming = false;
        if (string.IsNullOrWhiteSpace(bubble.Text)) Messages.Remove(bubble);
        else bubble.Interrupted = true;
    }

    private bool CanCancelRequest() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancelRequest))]
    private void CancelRequest()
    {
        _cts?.Cancel();
        // 正在等用户点确认的高风险动作：这次发送都取消了，确认条不能还挂着倒计时。
        FinishConfirm(false);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        string text = InputText.Trim();
        if (text.Length == 0) return;

        InputText = "";
        Messages.Add(new ChatMessage(text, isUser: true, sender: "我"));

        // 实时通话中：文字走 realtime 会话（conversation.item.create + response.create），
        // 不再走 chat.completions，否则两套上下文会互相打架。
        if (RealtimeActive)
        {
            bool sent = await _realtime.SendTextAsync(text);
            if (!sent)
                Messages.Add(new ChatMessage("实时通话没连上，这条文字没发出去。",
                    isUser: false, sender: "出错了", isError: true));
            return;
        }

        IsBusy = true;
        IsThinking = true;            // 还没收到第一个字：聊天区显示"正在思考"
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        int generation = ++_sendGeneration;
        _streamActive = true;         // 开始接收流式增量（旧一轮的残留分片由 generation 挡掉）
        _streamGeneration = generation;
        _streamBubble = null;

        // 发送前先记下设备状态：AI 回完对比一次，把它实际做了什么摊开写在聊天里。
        DeviceSnapshot before = DeviceSnapshot.Capture();

        try
        {
            var sendTask = _ai.SendUserAsync(text, SelectedPersona.Prompt, _cts.Token);

            // 把「取消」变成一个可 await 的任务，与 AI 回复赛跑；取消后 UI 立即恢复。
            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = _cts.Token.Register(() => cancelTcs.TrySetResult(true));
            var finished = await Task.WhenAny(sendTask, cancelTcs.Task);

            if (generation != _sendGeneration) return;   // 已被新一轮发送取代，UI 交给它处理

            if (finished == cancelTcs.Task)
            {
                // HTTP 请求已被 token 中断：观察异常避免「未观察任务异常」，回复直接丢弃。
                _ = sendTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                AbortStreaming();          // 半截文字标「已中断」，空泡删掉（不留永远转圈的气泡）
                Messages.Add(CancelledNotice());
                AppendDeviceActions(before);
                return;
            }

            string reply = await sendTask;
            FinishStreaming(reply);        // 流式已上屏的以它为准；没流式的按老逻辑补一条完整消息

            AppendDeviceActions(before);   // 真动了设备就补一条「设备动作」提示
        }
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            if (generation == _sendGeneration)
            {
                AbortStreaming();
                Messages.Add(CancelledNotice());
                AppendDeviceActions(before);
            }
        }
        catch (OperationCanceledException)
        {
            // 不是用户点的取消 —— 是我们自己的超时（整包 60 秒 / 流式总时长到点）。
            if (generation == _sendGeneration)
            {
                AbortStreaming();
                Messages.Add(new ChatMessage("等太久了还没回复，先不等了：网络可能不稳，或者服务商那边卡住，过会儿再发一次。",
                    isUser: false, sender: "出错了", isError: true));
                AppendDeviceActions(before);
            }
        }
        catch (Exception ex)
        {
            if (generation == _sendGeneration)
            {
                AbortStreaming();
                Messages.Add(new ChatMessage(FriendlyError(ex), isUser: false, sender: "出错了", isError: true));
                AppendDeviceActions(before);
            }
        }
        finally
        {
            if (generation == _sendGeneration)
            {
                _streamActive = false;   // 之后晚到的分片一律丢弃
                _streamBubble = null;
                IsThinking = false;      // 无论走哪条路，都把"正在思考"收掉
                IsBusy = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    /// <summary>取消等待时的提示：说清「AI 那边可能已经把动作做完了」，避免用户以为设备没动。</summary>
    private static ChatMessage CancelledNotice() =>
        new("已取消这次等待（AI 可能已经把动作做完了，看下面的设备动作）。",
            isUser: false, sender: "系统", isNotice: true);

    // ── 设备动作回执：AI 到底动了什么，摊开写清楚 ────────────────────────

    /// <summary>发送前后的设备关键状态快照，用来告诉用户「AI 刚才真的做了什么」。</summary>
    private readonly record struct DeviceSnapshot(
        bool AutoRunning, bool Teasing, bool Held, bool Stopped,
        string Preset, double Speed, double Intensity, string Amps)
    {
        public static DeviceSnapshot Capture()
        {
            var engine = App.Engine;
            return new DeviceSnapshot(
                engine.AutoRunning, engine.TeasingMode, engine.AutoBehaviorHeld, engine.EmergencyStopped,
                engine.CurrentAutoPatternLabel, Math.Round(engine.Speed, 2), Math.Round(engine.IntensityScale, 2),
                string.Join("/", engine.GetAxisAmp().Select(a => Math.Round(a).ToString("0"))));
        }
    }

    /// <summary>对比前后状态，有变化就补一条居中提示；没变化什么都不写（免得刷屏）。</summary>
    private void AppendDeviceActions(DeviceSnapshot before)
    {
        DeviceSnapshot after = DeviceSnapshot.Capture();
        var parts = new List<string>();

        if (!before.Stopped && after.Stopped) parts.Add("执行了急停，所有运动已停");
        else if (before.Stopped && !after.Stopped) parts.Add("解除了急停");
        if (!string.Equals(before.Preset, after.Preset, StringComparison.Ordinal))
            parts.Add($"场景换成「{after.Preset}」");
        if (Math.Abs(before.Speed - after.Speed) > 0.01)
            parts.Add($"速度 {before.Speed:0.0} → {after.Speed:0.0}");
        if (Math.Abs(before.Intensity - after.Intensity) > 0.005)
            parts.Add($"强度 {before.Intensity:0.00} → {after.Intensity:0.00}");
        if (!before.Teasing && after.Teasing) parts.Add("进入挑逗循环（动—停—歇）");
        else if (before.Teasing && !after.Teasing) parts.Add("挑逗循环已结束");
        if (!before.Held && after.Held) parts.Add("正保持当前姿势");
        if (!string.Equals(before.Amps, after.Amps, StringComparison.Ordinal)) parts.Add("各轴幅度已调整");
        if (parts.Count == 0 && !before.AutoRunning && after.AutoRunning) parts.Add("开始运动");
        else if (parts.Count == 0 && before.AutoRunning && !after.AutoRunning && !after.Stopped) parts.Add("暂停了自动运动");

        if (parts.Count == 0) return;
        Messages.Add(new ChatMessage("🎛 设备动作 · " + string.Join(" · ", parts),
            isUser: false, sender: "设备", isNotice: true));
    }

    /// <summary>把接口/网络的原始报错翻成一句人话，并说清下一步该去哪儿改。</summary>
    private static string FriendlyError(Exception ex)
    {
        string msg = ex.Message ?? "";
        if (msg.Contains("尚未配置 AI API"))
            return "还没填 API Key：点上方的「去设置填 Key」，填好再发（Key 只存在本机）。";
        if (msg.Contains("中途断了"))
            return msg;   // 服务侧写的断流提示本身就是人话，别再包一层"出错了：…"
        if (msg.Contains("401")) return "API Key 不对或已失效（401）：去「设置 → AI 助手」重新填一次。";
        if (msg.Contains("402") || msg.Contains("余额") || msg.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
            return "账户余额不足，或没开通这个模型（402）：去服务商后台看看。";
        if (msg.Contains("403")) return "这个 Key 没有调用权限（403）：换一个 Key，或换成你有权限的模型。";
        if (msg.Contains("404")) return "接口地址或模型名不对（404）：检查「设置 → AI 助手」里的接口地址与模型名。";
        if (msg.Contains("429")) return "发得太快了，被限流了（429）：等几秒再发。";
        if (msg.Contains("500") || msg.Contains("502") || msg.Contains("503") || msg.Contains("504"))
            return "AI 服务端出问题了：" + msg + "（过一会儿再试）";
        if (ex is HttpRequestException)
            return "连不上 AI 服务：检查网络或代理，以及「设置 → AI 助手」里的接口地址。";
        return "出错了：" + msg + "（如果一直这样，去「设置 → AI 助手」检查 Key、接口地址和模型名）";
    }

    // ── 语音识别：开始/停止 ────────────────────────────────────────────

    private bool CanToggleListening() => IsSpeechAvailable && !IsBusy && !RealtimeActive;

    [RelayCommand(CanExecute = nameof(CanToggleListening))]
    private void ToggleListening()
    {
        if (IsListening) StopListening();
        else StartListening();
    }

    private async Task InitSpeechAsync()
    {
        bool ok = false;
        // 首选 Windows 原生语音识别（WinRT）：中文听写质量明显好于旧 SAPI。
        try
        {
            bool winRt = await Task.Run(async () =>
            {
                try
                {
                    bool zhSupported = Windows.Media.SpeechRecognition.SpeechRecognizer.SupportedTopicLanguages
                        .Any(language => language.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                    if (!zhSupported) return false;
                    using var probe = new Windows.Media.SpeechRecognition.SpeechRecognizer(
                        new Windows.Globalization.Language("zh-CN"));
                    probe.Constraints.Add(new Windows.Media.SpeechRecognition.SpeechRecognitionTopicConstraint(
                        Windows.Media.SpeechRecognition.SpeechRecognitionScenario.Dictation, "dictation"));
                    var compiled = await probe.CompileConstraintsAsync();
                    return compiled.Status == Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[AI] Windows 原生语音识别不可用，将降级: {ex.Message}");
                    return false;
                }
            });
            if (winRt) { _useWinRt = true; ok = true; }
        }
        catch { ok = false; }

        // 降级：旧 SAPI（System.Speech）
        if (!ok)
        {
            try
            {
                ok = await Task.Run(() =>
                {
                    try
                    {
                        using var probe = new SpeechRecognitionEngine(new CultureInfo("zh-CN"));
                        probe.LoadGrammar(new DictationGrammar());
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"[AI] 语音识别初始化失败: {ex.Message}");
                        return false;
                    }
                });
            }
            catch { ok = false; }
        }

        // await 回到 UI 线程构造上下文，设置可安全绑定
        IsSpeechInitDone = true;
        IsSpeechAvailable = ok;
    }

    private void StartListening()
    {
        if (_useWinRt)
        {
            StartWinRtListening();
            return;
        }
        try
        {
            var rec = new SpeechRecognitionEngine(new CultureInfo("zh-CN"));
            rec.LoadGrammar(new DictationGrammar());
            rec.SpeechRecognized += OnSpeechRecognized;
            rec.SetInputToDefaultAudioDevice();
            lock (_recLock) _recognizer = rec;
            rec.RecognizeAsync(RecognizeMode.Multiple);
            IsListening = true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AI] 语音识别启动失败: {ex.Message}");
            IsSpeechAvailable = false;
            IsListening = false;
            lock (_recLock) { _recognizer?.Dispose(); _recognizer = null; }
        }
    }

    private void StopListening()
    {
        if (_useWinRt)
        {
            Windows.Media.SpeechRecognition.SpeechRecognizer? winRt;
            lock (_recLock) { winRt = _winRtRecognizer; _winRtRecognizer = null; }
            if (winRt != null)
            {
                try { winRt.ContinuousRecognitionSession.ResultGenerated -= OnWinRtRecognized; } catch { }
                try { _ = winRt.ContinuousRecognitionSession.StopAsync(); } catch { }
                try { winRt.Dispose(); } catch { }
            }
            IsListening = false;
            return;
        }
        SpeechRecognitionEngine? rec;
        lock (_recLock) { rec = _recognizer; _recognizer = null; }
        if (rec != null)
        {
            try { rec.RecognizeAsyncCancel(); } catch { }
            try { rec.SpeechRecognized -= OnSpeechRecognized; } catch { }
            try { rec.Dispose(); } catch { }
        }
        IsListening = false;
    }

    // ── Windows 原生语音识别（WinRT）──────────────────────────────────
    private async void StartWinRtListening()
    {
        try
        {
            var rec = new Windows.Media.SpeechRecognition.SpeechRecognizer(
                new Windows.Globalization.Language("zh-CN"));
            rec.Constraints.Add(new Windows.Media.SpeechRecognition.SpeechRecognitionTopicConstraint(
                Windows.Media.SpeechRecognition.SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await rec.CompileConstraintsAsync();
            if (compiled.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success)
            {
                AppLogger.Warn($"[AI] WinRT 语音识别编译语法失败: {compiled.Status}");
                rec.Dispose();
                IsSpeechAvailable = false;
                return;
            }
            rec.ContinuousRecognitionSession.ResultGenerated += OnWinRtRecognized;
            lock (_recLock) _winRtRecognizer = rec;
            await rec.ContinuousRecognitionSession.StartAsync();
            IsListening = true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AI] WinRT 语音识别启动失败: {ex.Message}");
            IsSpeechAvailable = false;
            IsListening = false;
            lock (_recLock) { _winRtRecognizer?.Dispose(); _winRtRecognizer = null; }
        }
    }

    private void OnWinRtRecognized(
        Windows.Media.SpeechRecognition.SpeechContinuousRecognitionSession sender,
        Windows.Media.SpeechRecognition.SpeechContinuousRecognitionResultGeneratedEventArgs e)
    {
        if (e.Result?.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success) return;
        string text = e.Result.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        App.Dispatch(() =>
        {
            InputText = text;
            if (!IsBusy) SendCommand.Execute(null);
        });
        StopListening();
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result == null || string.IsNullOrWhiteSpace(e.Result.Text)) return;
        string text = e.Result.Text.Trim();

        // 识别到一句话 → 回 UI 线程填输入框并自动发送。
        App.Dispatch(() =>
        {
            InputText = text;
            if (!string.IsNullOrWhiteSpace(text) && !IsBusy)
                SendCommand.Execute(null);
        });
        StopListening();
    }

    private bool CanStopSpeaking() => IsAiSpeaking;

    /// <summary>打断 AI 正在朗读的 TTS；未在说话时按钮灰显不可点。</summary>
    [RelayCommand(CanExecute = nameof(CanStopSpeaking))]
    private void StopSpeaking() => _ai.StopSpeaking();

    // ── 高风险动作确认：非模态 3 秒倒计时（不弹模态框、不占用户的双手）────────
    /// <summary>倒计时长度（秒）：够看清"要做什么"，又不至于耽误事。要和 XAML 里进度条的 Maximum 对上。</summary>
    private const double ConfirmSeconds = 3;

    /// <summary>确认条是否显示。</summary>
    [ObservableProperty] private bool _confirmVisible;
    /// <summary>确认条上"即将执行"的动作描述（人话，不是工具名）。</summary>
    [ObservableProperty] private string _confirmActionText = "";
    /// <summary>为什么判成高风险（让用户能判断该不该拦）。</summary>
    [ObservableProperty] private string _confirmReasonText = "";
    /// <summary>剩余秒数（进度条用，从 3 走到 0）。</summary>
    [ObservableProperty] private double _confirmRemaining = ConfirmSeconds;

    private DispatcherTimer? _confirmTimer;
    private DateTime _confirmStartAt;
    private TaskCompletionSource<bool>? _confirmTcs;

    /// <summary>剩余秒数的文字。</summary>
    public string ConfirmCountdownText => $"{(int)Math.Ceiling(Math.Max(0, ConfirmRemaining))} 秒后开始";

    partial void OnConfirmRemainingChanged(double value) => OnPropertyChanged(nameof(ConfirmCountdownText));

    /// <summary>
    /// Service 的确认回调（**后台线程**）：必须回 UI 线程再改界面状态；
    /// 拿不到 UI 线程（主窗口已关）就返回 false = 不执行（fail-closed，与 Service 的安全策略一致）。
    /// </summary>
    private Task<bool> OnConfirmToolAsync(ToolConfirmRequest req)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.Dispatch(() => BeginConfirm(req, tcs)))
            tcs.TrySetResult(false);
        return tcs.Task;
    }

    private void BeginConfirm(ToolConfirmRequest req, TaskCompletionSource<bool> tcs)
    {
        // Service 是串行询问的；万一有第二条挤进来，先把上一条按"不要"收掉，绝不叠加两个倒计时。
        _confirmTcs?.TrySetResult(false);

        _confirmTcs = tcs;
        ConfirmActionText = req.ActionText;
        ConfirmReasonText = req.Reason;
        _confirmStartAt = DateTime.UtcNow;
        ConfirmRemaining = ConfirmSeconds;
        ConfirmVisible = true;

        if (_confirmTimer == null)
        {
            _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _confirmTimer.Tick += (_, _) => TickConfirm();
        }
        _confirmTimer.Start();
    }

    /// <summary>用真实时间算剩余：UI 卡一下也不会把 3 秒拖成 10 秒。</summary>
    private void TickConfirm()
    {
        double left = ConfirmSeconds - (DateTime.UtcNow - _confirmStartAt).TotalSeconds;
        ConfirmRemaining = Math.Max(0, left);
        if (left <= 0) FinishConfirm(true);   // 到点自动放行（默许，用户不点就是同意）
    }

    /// <summary>收尾：停表、隐藏确认条、把结果交给等待中的 Service（只能结束一次）。</summary>
    private void FinishConfirm(bool approved)
    {
        _confirmTimer?.Stop();
        ConfirmVisible = false;
        var tcs = _confirmTcs;
        _confirmTcs = null;
        tcs?.TrySetResult(approved);
    }

    /// <summary>「✋ 不要」：取消这个动作，并把"被拒绝"回灌给 AI（它会换个更温和的做法）。</summary>
    [RelayCommand]
    private void ConfirmReject() => FinishConfirm(false);

    /// <summary>「✅ 现在执行」：不用等倒计时，立刻放行。</summary>
    [RelayCommand]
    private void ConfirmApprove() => FinishConfirm(true);

    // 保留命令：急停已由侧边栏常驻按钮提供，本页不再绑定（避免重复入口）。
    [RelayCommand]
    private void EmergencyStop() => App.Engine.EmergencyStop();

    [RelayCommand]
    private void ClearChat() => App.Dispatch(ClearChatCore);

    /// <summary>清空对话前必须确认；弹框与集合修改都固定发生在 UI 线程。</summary>
    private void ClearChatCore()
    {
        var confirm = System.Windows.MessageBox.Show(
            "清空这里的聊天记录？AI 也会忘掉之前聊过的内容。\n设备保持现在的状态，不会停（要停就点左侧「⛔ 锁定急停」）。",
            "清空对话", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;
        FinishConfirm(false);   // 正在等确认的动作按"不要"收掉，别留一个挂着的倒计时条
        Messages.Clear();
        _ai.ResetConversation();
    }

    // ── 人格：新建 / 删除 / 导入 / 导出 ────────────────────────────────────
    [ObservableProperty] private string _importJson = "";
    // 导入失败提示单独显示，绝不覆盖用户粘贴的 JSON。
    [ObservableProperty] private string _importError = "";
    [ObservableProperty] private string _exportJson = "";
    /// <summary>人格管理面板是否展开（默认收起，不占聊天区）。</summary>
    [ObservableProperty] private bool _isImportOpen;

    /// <summary>当前人格是不是自己新建/导入的：只有自定义人格能删。</summary>
    public bool CanDeletePersona =>
        SelectedPersona != null && AiAssistantService.CustomPersonas.Any(p => p.Id == SelectedPersona.Id);

    /// <summary>「内置 / 自定义」标记：一眼知道这个人格能不能删。</summary>
    public string SelectedPersonaKindText =>
        CanDeletePersona ? "· 自定义人格（可删除）" : "· 内置人格（删不掉，可导出后改）";

    partial void OnSelectedPersonaChanged(AiPersona value)
    {
        OnPropertyChanged(nameof(CanDeletePersona));
        OnPropertyChanged(nameof(SelectedPersonaKindText));
        DeletePersonaCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 删除当前人格：只允许删自定义人格（内置人格不在自定义列表里），并且必须确认一次 ——
    /// 点错就把人设弄丢了。删完自动切回第一个人格，并留一句回执。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeletePersona))]
    private void DeletePersona()
    {
        if (SelectedPersona is null || !CanDeletePersona) return;

        var confirm = System.Windows.MessageBox.Show(
            $"删除人格「{SelectedPersona.Id}」？\n删掉之后要重新导入 JSON 才能找回来。",
            "删除人格", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        string removed = SelectedPersona.Id;
        AiAssistantService.RemoveCustomPersona(removed);
        OnPropertyChanged(nameof(Personas));
        ImportError = "";
        SelectedPersona = Personas.Count > 0 ? Personas[0] : AiAssistantService.Personas[0];
        PersonaNotice = $"已删除人格「{removed}」，现在用的是「{SelectedPersona.Id}」。";
    }

    /// <summary>用「名字 + 设定」新建一个人格并立刻切过去（不用写 JSON）。</summary>
    [RelayCommand]
    private void SaveNewPersona()
    {
        ImportError = "";
        PersonaNotice = "";
        string name = NewPersonaName.Trim();
        string prompt = NewPersonaPrompt.Trim();
        if (name.Length == 0) { ImportError = "先给这个新人格起个名字。"; return; }
        if (prompt.Length == 0) { ImportError = "再写一句设定：它该用什么语气、什么性格（这段就是给 AI 的人设）。"; return; }

        var persona = new AiPersona(name, prompt);
        AiAssistantService.AddCustomPersona(persona);
        OnPropertyChanged(nameof(Personas));
        SelectedPersona = persona;
        NewPersonaName = "";
        NewPersonaPrompt = "";
        PersonaNotice = $"已新建人格「{name}」，并切换过去。";
    }

    /// <summary>粘贴角色卡 JSON 建人格。失败保留原文，只把错误单独显示。</summary>
    [RelayCommand]
    private void ImportPersona()
    {
        ImportError = "";
        PersonaNotice = "";
        try
        {
            var p = AiAssistantService.ParseCardJson(ImportJson);
            AiAssistantService.AddCustomPersona(p);
            OnPropertyChanged(nameof(Personas));
            SelectedPersona = p;
            ImportJson = "";
            PersonaNotice = $"已导入人格「{p.Id}」，并切换过去。";
        }
        catch (Exception ex)
        {
            // 保留用户粘贴的原文，错误单独提示（原先会把 JSON 覆盖成错误信息）。
            ImportError = "导入失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void ExportPersona()
    {
        ExportJson = AiAssistantService.ExportPersonaJson(SelectedPersona);
    }

    /// <summary>展开/收起人格管理面板；刚展开时清掉上一次的提示，免得看着像刚发生的。</summary>
    [RelayCommand]
    private void ToggleImport()
    {
        IsImportOpen = !IsImportOpen;
        if (IsImportOpen)
        {
            ImportError = "";
            PersonaNotice = "";
        }
    }
}
