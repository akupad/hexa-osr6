using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hexa.Models;
using NAudio.Wave;

namespace Hexa.Services;

/// <summary>
/// 「AI 助手」后端：封装 DeepSeek/硅基流动 OpenAI 兼容 chat.completions（function calling + SSE 流式），
/// 把 AI 的工具调用映射到 MotionEngine，并用 edge-tts + NAudio 做 TTS 语音回复。
/// 运动执行一律走 App.Engine；本类绝不直接 new SerialService / 发串口。
/// API key 从 AppSettings 读取（默认空 → 功能禁用），不硬编码；落盘时用 DPAPI 加密（见 SecretProtector）。
/// </summary>
public sealed class AiAssistantService
{
    // ── 超时 / 重试 / 断流：固定参数集中放这里，一眼看清"多久算卡住" ──────────
    /// <summary>网络类错误（超时 / 连接被重置 / 5xx）最多请求几次：首次 + 1 次重试。</summary>
    private const int MaxAttempts = 2;
    /// <summary>重试退避基数：500ms（指数退避，第 1 次重试等 500ms）。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    /// <summary>非流式整包请求的总超时（保持原来"60 秒还不回就别等"的语义）。</summary>
    private static readonly TimeSpan BufferedTimeout = TimeSpan.FromSeconds(60);
    /// <summary>流式请求的总超时：首字很快，但整段生成可能超过 60 秒，所以给得更宽。</summary>
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(150);
    /// <summary>流式"断流"判据：这么久没收到任何新分片就当流断了，立刻收尾报错（不让气泡永远转圈）。</summary>
    private static readonly TimeSpan StreamStallTimeout = TimeSpan.FromSeconds(45);

    // ── 高风险动作的判据阈值（条件式确认用，口径见 AssessToolRisk）──────────
    /// <summary>轴偏离中心（或振幅）≥ 40% ＝ 接近满行程。</summary>
    private const double RiskyAmplitude = 40;
    /// <summary>速度 ≥ 2.5 档（满档 3.0）。</summary>
    private const double RiskySpeed = 2.5;
    /// <summary>hold / tease / nudge 这类"持续动作"超过 20 秒就算长时间动作。</summary>
    private const double LongActionSeconds = 20;

    private readonly MotionEngine _engine;
    private readonly AppSettings _cfg;

    /// <summary>
    /// 不用 HttpClient.Timeout：它是"整个请求 + 读 body"一刀切，流式长回复会被腰斩。
    /// 改成每次请求自带 CTS（非流式 60s；流式 150s 总超时 + 45s 断流看门狗）。
    /// </summary>
    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>服务商明确拒收 reasoning_effort / thinking 字段后置 true：之后不再发这两个可选字段。</summary>
    private volatile bool _extraFieldsRejected;

    /// <summary>API Key 解密缓存：原始串没变就不重复调 DPAPI（Unprotect 每次都要过一次系统调用）。</summary>
    private string _cachedRawKey = "";
    private string _cachedPlainKey = "";

    /// <summary>
    /// 上一次回复里的思考内容（DeepSeek 的 reasoning_content）。
    /// 只有开了思考（AiReasoningEffort != none）时才非空；它不进界面、不进 TTS，
    /// 但多轮工具调用时要跟着 assistant 消息原样回填 —— 官方 thinking_mode 示例就是这么做的
    /// （服务端会忽略或校验它）。默认 none 下这个字段永远是空的，等于不多传任何字段。
    /// </summary>
    private string _lastReasoningContent = "";

    // 多轮对话缓存：role=system/user/assistant 及 role=tool（含 tool_calls/tool_call_id）。
    // 用 JsonObject 原样保存，便于精确回填 assistant 的 tool_calls 和 tool 返回值。
    private readonly List<JsonObject> _messages = new();

    private readonly object _ttsLock = new();
    private WaveOut? _waveOut;
    private AudioFileReader? _audioReader;
    private volatile bool _isSpeaking;
    private int _speakGeneration;                 // 每次打断 +1，用于丢弃尚未播出的过期 TTS
    private int _holdToken;                       // hold 的恢复令牌，期间有新指令则放弃恢复

    // AI 侧记录的每轴行程范围（百分比），仅用于 get_state 回读；实际生效的是 Engine 的每轴振幅。
    private readonly Dictionary<string, (double Min, double Max)> _axisRanges = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// 真正拿去发请求的 Key：settings.json 里存的是 DPAPI 密文，这里读出来解密。
    /// 老配置 / 手工填的明文原样返回（兼容）；解不开就当"没配 Key"，让人重填，
    /// 绝不把密文当 Key 发出去（那只会换来一个看不懂的 401）。
    /// </summary>
    public string ApiKey
    {
        get
        {
            string raw = _cfg.AiApiKey ?? "";
            if (!string.Equals(raw, _cachedRawKey, StringComparison.Ordinal))
            {
                _cachedRawKey = raw;
                _cachedPlainKey = SecretProtector.Unprotect(raw);
            }
            return _cachedPlainKey;
        }
    }

    /// <summary>是否正在播放 TTS 语音（播放开始置 true，PlaybackStopped / StopSpeaking 置 false）。</summary>
    public bool IsSpeaking => _isSpeaking;

    /// <summary>
    /// 实时语音通话进行中：暂停本地 edge-tts 播放，避免与实时通话抢麦克风/扬声器。
    /// 由 RealtimeVoiceService 在连接成功时置 true、断开时复位。
    /// </summary>
    public bool SuppressTts { get; set; }

    /// <summary>说话状态变化通知；可能在 NAudio 线程触发，订阅方需自行调度到 UI 线程。</summary>
    public event Action? SpeakingChanged;

    /// <summary>
    /// 流式增量文本（每收到一段就回调一次）。**在后台线程触发**，订阅方需自行调度到 UI 线程。
    /// 只在 AppSettings.AiStreaming=true 时触发；没人订阅也能正常工作（等于回到整包模式的表现）。
    /// </summary>
    public event Action<string>? StreamDelta;

    /// <summary>
    /// 高风险动作的**执行前**确认（可取消）。返回 true = 放行，false = 拒绝执行并把"被拒绝"回灌给模型。
    /// 只有 AssessToolRisk 按解析后的参数判成高风险的动作才会问，绝不是逐次弹框。
    /// **没有订阅方时高风险动作一律拒绝执行（fail-closed）**，绝不静默执行。
    /// </summary>
    public event Func<ToolConfirmRequest, Task<bool>>? ConfirmToolAsync;

    public AiAssistantService(MotionEngine engine, AppSettings cfg)
    {
        _engine = engine;
        _cfg = cfg;
        // 启动时把明文 Key 迁移成 DPAPI 密文（失败就保持明文可用，绝不让 AI 功能因为加密失败而坏掉）。
        TryMigrateApiKeyToProtected();
    }

    /// <summary>
    /// 启动时的一次性迁移：settings.json 里的明文 Key → DPAPI 密文，并置 AiKeyProtected=true。
    /// 判据看"内容像不像密文"而不是只看标记位：万一别的写入路径（比如设置页保存）又把明文写了回去，
    /// 下次启动照样会被重新加密，不会留下明文。
    /// 迁移失败（DPAPI 不可用 / 写盘失败）时保持明文可用，只在日志里说明。
    /// </summary>
    private void TryMigrateApiKeyToProtected()
    {
        try
        {
            string raw = (_cfg.AiApiKey ?? "").Trim();
            if (raw.Length == 0) return;

            if (SecretProtector.LooksProtected(raw))
            {
                // 已经是密文：只把内部标记补齐（老配置里可能还没有这个字段）。
                if (!_cfg.AiKeyProtected)
                {
                    _cfg.AiKeyProtected = true;
                    if (!_cfg.Save())
                        AppLogger.Warn("[AI] 已加密的 Key 标记未能写盘（下次启动会再试一次，不影响使用）。");
                }
                return;
            }

            string protectedValue = SecretProtector.Protect(raw);
            if (!SecretProtector.LooksProtected(protectedValue))
            {
                // DPAPI 不可用（Protect 已记原因）→ 保持明文，不阻塞使用。
                return;
            }

            _cfg.AiApiKey = protectedValue;
            _cfg.AiKeyProtected = true;
            bool saved = _cfg.Save();
            AppLogger.Info(saved
                ? "[AI] API Key 已用 DPAPI（当前用户）加密写回 settings.json；界面与接口读取时自动解密。"
                : "[AI] API Key 已在内存里加密，但 settings.json 写盘失败：本次启动照常可用，"
                  + "文件里可能仍是明文，下次启动会再试一次。");

            // 已经核对过：其它读 Key 的地方（Views/SettingsPage 的读写、Services/RealtimeVoiceService.ResolveKey）
            // 都接了 SecretProtector，不存在"拿到密文当 Key 用"的路径。留这条注释是为了下次有人新增
            // 读 cfg.AiApiKey 的代码时能看见这个约束 —— 读到密文不会报错，只会静默 401，很难查。
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AI] API Key 加密迁移失败（保持原样，不影响使用）：{ex.Message}");
        }
    }

    /// <summary>角色卡（人格）。Id 用于 UI 展示，Prompt 作为 system prompt。</summary>
    public static AiPersona[] Personas { get; } =
    [
        new("温柔陪伴",
            "你是用户的 AI 伴侣，名字叫小柔。语气温柔体贴、亲密自然，用简体中文回复，篇幅不要太长。"
            + "你可以调用工具控制用户的 OSR6 设备来贴合当前气氛，选择契合的运动节奏与强度，"
            + "但永远把安全放在第一位（用户喊停就立刻停止）。"),
        new("调皮调教",
            "你是用户的 AI 伴侣，性格俏皮、爱逗趣、带着一点调戏感。用简体中文回复，语气轻松自然。"
            + "根据用户的语气合适地调用工具，做出带挑逗感的动作控制，但绝不越过安全界限。"),
        new("冷静服务",
            "你是一个冷静、专业、简洁的 AI 助手。用简体中文回复。仅在用户明确要求时调用工具控制设备，"
            + "其余时间以陪伴和应答为主。"),
    ];

    /// <summary>清空当前对话（含 system 消息，下次发送重新生成）。</summary>
    public void ResetConversation() => _messages.Clear();

    /// <summary>
    /// 发送一条用户消息并完成 AI 多轮往返（含 function calling 执行）。
    /// 若命中高潮关键词则走 SendClimaxCoreAsync（停轴降位 + AI 收尾话）。
    /// 返回 AI 最终的文字回复；TTS 已在后台触发，无需额外调用。
    /// </summary>
    public async Task<string> SendUserAsync(string userText, string personaPrompt,
        CancellationToken cancellationToken = default)
    {
        // 用户发新消息 → 立刻打断上一句 TTS（含尚未播出的生成结果），避免旧语音盖过新回复。
        StopSpeaking();

        if (!IsConfigured)
            throw new InvalidOperationException("尚未配置 AI API，请到「设置 → AI 助手」填写 API Key。");

        // ② 高潮处理：用户喊“射了/高潮/1…”→ 停轴降位 + 只发收尾话
        if (IsClimaxKeyword(userText))
            return await SendClimaxCoreAsync(personaPrompt, cancellationToken).ConfigureAwait(false);

        string systemPrompt = BuildSystemPrompt(personaPrompt);
        if (_messages.Count == 0)
            _messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        else
            _messages[0]["content"] = systemPrompt;   // 切换人格时替换首条 system

        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText.Trim() });

        // 流式：只在开关打开时把增量抛给上层；关掉时 onDelta=null，走原来的整包路径。
        Action<string>? onDelta = _cfg.AiStreaming ? new Action<string>(EmitStreamDelta) : null;

        string reply = "";
        bool anyToolCalled = false;
        for (int turn = 0; turn < 12; turn++)   // 安全上限，防止失控循环
        {
            var (content, toolCalls) = await CallChatAsync(turn, _messages, sendTools: true,
                cancellationToken: cancellationToken, onDelta: onDelta).ConfigureAwait(false);

            var assistantMsg = new JsonObject { ["role"] = "assistant", ["content"] = content ?? "" };
            if (toolCalls.Count > 0)
                assistantMsg["tool_calls"] = BuildToolCallsArray(toolCalls);
            // 开了思考时把思考内容原样带上（官方示例的写法；不显示给用户，只是多轮工具调用的协议要求）。
            // 默认 none 下这一句不生效，请求体里不会多出任何字段。
            if (_lastReasoningContent.Length > 0)
                assistantMsg["reasoning_content"] = _lastReasoningContent;
            _messages.Add(assistantMsg);

            if (!string.IsNullOrWhiteSpace(content))
                reply = content;

            if (toolCalls.Count == 0)
                break;

            anyToolCalled = true;
            foreach (var call in toolCalls)
            {
                // 高风险动作会在这里先问一句（非模态 3 秒倒计时）；用户点「不要」就把拒绝结果回灌给模型。
                string result = await ExecuteToolAsync(call, cancellationToken).ConfigureAwait(false);
                _messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = result,
                });
            }
        }

        // ③ 敏感词运动补充：AI 描述了身体动作但本轮没调任何工具 → 再请求一次生成 set_motion
        if (!anyToolCalled && !string.IsNullOrWhiteSpace(reply) && ContainsContactKeyword(reply))
            await NudgeMotionAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(reply))
            _ = Task.Run(() => SpeakAsync(reply)).ConfigureAwait(false);

        return reply;
    }

    // ── ② 高潮处理（对齐 c.py force_climax_reply）───────────────────────────

    private static readonly string[] ClimaxKeywords =
        ["1", "一", "射了", "高潮", "要到了", "要出来了", "出来了", "憋不住了", "快到了"];

    /// <summary>命中高潮关键词：精确匹配“1/一”，其余按子串匹配（避免句子里的“1”误触发）。</summary>
    private static bool IsClimaxKeyword(string text)
    {
        string trimmed = text.Trim();
        if (trimmed is "1" or "一") return true;
        return ClimaxKeywords.Any(k => k.Length >= 2 && trimmed.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>高潮逻辑：停所有轴并降 L0 到最低位，让 AI 只回一句收尾话（不调工具、不控 OSR）。</summary>
    private async Task<string> SendClimaxCoreAsync(string personaPrompt,
        CancellationToken cancellationToken = default)
    {
        // 停所有轴 + 把 L0 降到最低位（中心 15），其余轴居中；不锁定输出（区别于急停）。
        _engine.StopAll();
        // AI 的直控也要认领控制权：不认领时界面会显示"待机"、悬浮窗急停是灰的，而设备其实被 AI 驱动着。
        _engine.TryClaimDirectInput("ai");
        _engine.TrySendDirectAxes([15, 50, 50, 50, 50, 50]);
        _engine.ReleaseDirectInput("ai");

        string systemPrompt = BuildSystemPrompt(personaPrompt);
        if (_messages.Count == 0)
            _messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        else
            _messages[0]["content"] = systemPrompt;

        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = "我射了" });

        string reply = "";
        try
        {
            // 不传 tools，让 AI 纯粹回收尾话，不再控制设备
            var (content, toolCalls) = await CallChatAsync(0, _messages, sendTools: false,
                cancellationToken: cancellationToken,
                onDelta: _cfg.AiStreaming ? new Action<string>(EmitStreamDelta) : null).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(content)) reply = content;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AI] 高潮收尾回复失败: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(reply)) reply = "呼…终于结束了…";

        _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply });
        if (!string.IsNullOrWhiteSpace(reply))
            _ = Task.Run(() => SpeakAsync(reply)).ConfigureAwait(false);
        return reply;
    }

    // ── ③ 敏感词运动补充（对齐 c.py contact_kw）──────────────────────────────

    private static readonly string[] ContactKeywords =
        ["蹭", "贴住", "含", "握", "揉", "捏", "套", "舔", "撸", "夹", "吸", "顶", "磨", "套弄", "吞吐",
         "包裹", "摩擦", "抽插", "骑", "龟头", "下面", "那里", "性器", "生殖器", "阴茎", "肉棒", "穴", "阴",
         "射", "高潮", "插"];

    private static bool ContainsContactKeyword(string text) =>
        ContactKeywords.Any(k => text.Contains(k, StringComparison.Ordinal));

    /// <summary>AI 描述了身体动作但没调工具时，追加一条指令再请求一次，若返回 set_motion 则执行（不改主对话历史）。</summary>
    private async Task NudgeMotionAsync()
    {
        try
        {
            var nudge = _messages.Select(m => (JsonObject)m.DeepClone()).ToList();
            nudge.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "你刚才描述了身体动作，请调用 set_motion 生成对应的运动参数来模拟这个动作，不要回复文字",
            });
            // 这是"幕后"的补充请求：onDelta 传 null，不把它的文字流到聊天区。
            var (_, toolCalls) = await CallChatAsync(_messages.Count, nudge, onDelta: null).ConfigureAwait(false);
            foreach (var call in toolCalls)
            {
                string result = await ExecuteToolAsync(call, CancellationToken.None).ConfigureAwait(false);
                AppLogger.Info($"[AI] 运动补充：执行工具 {call.Name} → {result}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AI] 运动补充请求失败: {ex.Message}");
        }
    }

    // ── OpenAI 兼容请求 ────────────────────────────────────────────────────

    /// <summary>
    /// 一次 chat.completions 往返（按开关自动选流式 / 整包），返回最终文本与工具调用。
    /// </summary>
    private async Task<(string? content, List<ToolCall> toolCalls)> CallChatAsync(
        int turn, List<JsonObject> messages, bool sendTools = true,
        CancellationToken cancellationToken = default, Action<string>? onDelta = null)
    {
        var payload = BuildPayload(messages, sendTools);
        try
        {
            return await SendOnceAsync(payload, onDelta, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (!_extraFieldsRejected && IsExtraFieldRejection(ex.Message))
        {
            // 个别 OpenAI 兼容服务商不认 reasoning_effort / thinking（回 400）。
            // 自愈：去掉这两个可选字段重试一次并记住，之后不再发 —— 不因为一个可选参数把功能打死。
            _extraFieldsRejected = true;
            AppLogger.Warn("[AI] 服务商不接受 reasoning_effort / thinking 字段，已自动去掉后重试。");
            return await SendOnceAsync(BuildPayload(messages, sendTools), onDelta, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task<(string? content, List<ToolCall> toolCalls)> SendOnceAsync(
        JsonObject payload, Action<string>? onDelta, CancellationToken ct) =>
        _cfg.AiStreaming
            ? PostStreamAsync(payload, onDelta, ct)
            : PostBufferedAsync(payload, ct);

    /// <summary>规范化后的思考强度：只可能是 none / low / medium / high。</summary>
    private string Effort
    {
        get
        {
            string raw = (_cfg.AiReasoningEffort ?? "").Trim().ToLowerInvariant();
            return raw is "low" or "medium" or "high" ? raw : "none";
        }
    }

    /// <summary>是否开着思考（开思考时思考 token 与正文共享 max_tokens 额度）。</summary>
    private bool IsReasoningOn => Effort != "none";

    /// <summary>请求体：模型 / 消息 / 温度 / max_tokens / 思考强度 /（可选）流式 /（可选）工具。</summary>
    private JsonObject BuildPayload(List<JsonObject> messages, bool sendTools)
    {
        var payload = new JsonObject
        {
            ["model"] = _cfg.AiModel,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["temperature"] = 0.8,
            // max_tokens：封住"无限长回复"。关思考时 2048 对一个"篇幅不要太长"的陪伴回复绰绰有余；
            // 开思考时思考 token 与正文共享这份额度，所以给一倍余量，免得正文被思考挤没。
            // （真被截断时 finish_reason=length，下面会写一条日志，不用猜。）
            ["max_tokens"] = IsReasoningOn ? 4096 : 2048,
        };

        // reasoning_effort —— 判断依据（api-docs.deepseek.com/guides/thinking_mode）：
        //   · DeepSeek V4 **默认 high（默认就开思考）**，对实时对话纯粹是首字延迟税，所以默认关掉；
        //   · Chat Completions 接受 low / medium / high（medium、xhigh 会被映射成 high），**并不接受 none**；
        //   · 官方给的"关思考"写法是 thinking:{type:"disabled"}，不是 reasoning_effort:"none"。
        // 因此：none → 不发 reasoning_effort，改发 thinking.disabled；low/medium/high → 原样发 reasoning_effort。
        if (!_extraFieldsRejected)
        {
            if (Effort is "low" or "medium" or "high")
                payload["reasoning_effort"] = Effort;
            else
                payload["thinking"] = new JsonObject { ["type"] = "disabled" };
        }

        if (_cfg.AiStreaming)
        {
            payload["stream"] = true;
            // 故意不发 stream_options.include_usage：我们不用 token 用量统计，
            // 少一个可选字段就少一次被兼容服务商拒收（400）的机会。
        }

        if (sendTools)
        {
            payload["tools"] = BuildTools();
            payload["tool_choice"] = "auto";
        }
        return payload;
    }

    private HttpRequestMessage BuildRequest(JsonObject payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.AiApiBase}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>服务端 5xx 才值得重试；4xx（Key 错 / 没权限 / 地址错 / 限流）重试只会再错一次。</summary>
    private static bool IsServerError(System.Net.HttpStatusCode status) => (int)status >= 500;

    /// <summary>
    /// 网络类错误：超时（我们自己的 CTS 触发）、连接被重置、DNS/代理失败、响应中途断 —— 重试一次有意义。
    /// 用户自己点「取消」不算（不能把取消当失败重试）；4xx 不算。
    /// </summary>
    private static bool IsTransient(Exception ex, CancellationToken userToken) =>
        !userToken.IsCancellationRequested
        && ex is OperationCanceledException or HttpRequestException or IOException;

    private static void LogRetry(int attempt, string reason) =>
        AppLogger.Info($"[AI] 第 {attempt} 次请求失败（{reason}），{RetryDelay.TotalMilliseconds:0}ms 后重试一次。");

    /// <summary>统一成"AI 接口返回 4xx：…"——上层 FriendlyError 就是按这个格式翻人话的。</summary>
    private static InvalidOperationException HttpError(System.Net.HttpStatusCode status, string body) =>
        new($"AI 接口返回 {(int)status}：{Truncate(body, 400)}");

    /// <summary>读错误响应体（读失败/超时就返回空串：别让"读错误信息"本身再炸一次）。</summary>
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return ""; }
    }

    /// <summary>400 且服务端抱怨的正是我们这两个可选思考字段 → 值得去掉它们重试。</summary>
    private static bool IsExtraFieldRejection(string message) =>
        message.Contains("400", StringComparison.Ordinal)
        && (message.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
            || message.Contains("thinking", StringComparison.OrdinalIgnoreCase));

    /// <summary>把增量抛给订阅方；订阅方抛异常绝不能带崩解析循环（少一段文字也比整条流断掉好）。</summary>
    private void EmitStreamDelta(string text)
    {
        try { StreamDelta?.Invoke(text); }
        catch (Exception ex) { AppLogger.Warn($"[AI] 流式文本回调异常（已忽略，继续接收）：{ex.Message}"); }
    }

    private static void EmitDelta(Action<string>? onDelta, string text)
    {
        if (onDelta == null) return;
        try { onDelta(text); }
        catch (Exception ex) { AppLogger.Warn($"[AI] 流式回调异常（已忽略，继续接收）：{ex.Message}"); }
    }

    // ── 非流式：整包等（AiStreaming=false 时的路径）────────────────────────

    private async Task<(string? content, List<ToolCall> toolCalls)> PostBufferedAsync(
        JsonObject payload, CancellationToken ct)
    {
        string body = await PostJsonAsync(payload, ct).ConfigureAwait(false);
        return ParseCompletion(body);
    }

    /// <summary>
    /// POST 一次 chat/completions 并拿完整响应体。
    /// 重试策略：网络类错误（超时 / 连接重置）与 5xx 各重试 1 次（500ms 退避）；4xx 直接抛。
    /// 每个尝试独立计时（第一次卡满 60 秒后重试要重新计时，否则重试会立刻被判超时）。
    /// </summary>
    private async Task<string> PostJsonAsync(JsonObject payload, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(BufferedTimeout);
            try
            {
                using var request = BuildRequest(payload);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (IsServerError(response.StatusCode) && attempt < MaxAttempts)
                    {
                        LogRetry(attempt, $"服务端 {(int)response.StatusCode}");
                        await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw HttpError(response.StatusCode, body);
                }
                return body;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex, ct))
            {
                LogRetry(attempt, $"{ex.GetType().Name}: {ex.Message}");
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>解析整包响应（非流式路径，以及"服务商忽略 stream:true"时的兜底）。</summary>
    private (string? content, List<ToolCall> toolCalls) ParseCompletion(string body)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(body)?.AsObject(); }
        catch (JsonException) { throw new InvalidOperationException($"AI 返回无法解析：{Truncate(body, 200)}"); }

        var message = root?["choices"]?[0]?["message"]?.AsObject();
        if (message == null)
            throw new InvalidOperationException($"AI 返回缺少 choices: {Truncate(body, 200)}");

        _lastReasoningContent = AsString(message["reasoning_content"]) ?? "";   // 开思考时才有
        string? content = AsString(message["content"]);
        var toolCalls = new List<ToolCall>();
        if (message["tool_calls"] is JsonArray array)
            foreach (var node in array)
                toolCalls.Add(ParseToolCall(node));

        return (content, toolCalls);
    }

    // ── 流式：SSE 逐行解析（data: {...} … data: [DONE]）────────────────────

    /// <summary>
    /// 流式请求：按行读 SSE，把 delta.content 增量回调给上层，并按 index 聚合工具调用分片。
    /// 收尾保证：总超时 150s、断流 45s 看门狗、用户取消都能立刻抛出去（上层据此收尾，不留"永远思考中"的气泡）。
    /// 重试：只在"一个字都还没抛给界面"时重试（否则界面上的文字会重复一遍）；且最多 1 次。
    /// </summary>
    private async Task<(string? content, List<ToolCall> toolCalls)> PostStreamAsync(
        JsonObject payload, Action<string>? onDelta, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
            total.CancelAfter(StreamTimeout);
            using var stall = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(total.Token, stall.Token);
            long[] lastDataTicks = [DateTime.UtcNow.Ticks];
            StartStallWatchdog(stall, lastDataTicks);
            int emitted = 0;   // 已经抛给界面的分片数：>0 就不能重试

            try
            {
                using var request = BuildRequest(payload);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        linked.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string err = await SafeReadBodyAsync(response, linked.Token).ConfigureAwait(false);
                    if (IsServerError(response.StatusCode) && attempt < MaxAttempts && emitted == 0)
                    {
                        LogRetry(attempt, $"服务端 {(int)response.StatusCode}");
                        await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw HttpError(response.StatusCode, err);
                }

                using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var content = new StringBuilder();
                var reasoning = new StringBuilder();     // 思考过程：不显示，但多轮工具调用要回填
                var rawFallback = new StringBuilder();   // 服务商忽略 stream:true 时回的整包 JSON
                var toolAcc = new Dictionary<int, ToolCallAccumulator>();
                string finishReason = "";

                string? line;
                while ((line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false)) != null)
                {
                    Interlocked.Exchange(ref lastDataTicks[0], DateTime.UtcNow.Ticks);
                    if (line.Length == 0) continue;

                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        // 忽略 event: / id: / ": " 心跳行；只有整条响应就是一个 JSON 对象时才留作兜底。
                        if (line[0] == '{') rawFallback.Append(line);
                        continue;
                    }

                    string data = line[5..].Trim();
                    if (data.Length == 0) continue;
                    if (data == "[DONE]") break;          // 官方流结束标记

                    JsonObject? chunk = null;
                    try { chunk = JsonNode.Parse(data)?.AsObject(); }
                    catch (JsonException) { /* 半行 / 非 JSON 分片：跳过这一片，不能让它把整条流带崩 */ }
                    if (chunk == null) continue;

                    JsonNode? choice = chunk["choices"]?[0];
                    JsonNode? delta = choice?["delta"];

                    // 思考增量：不显示给用户，单独攒着（回填用）。
                    string? think = AsString(delta?["reasoning_content"]);
                    if (!string.IsNullOrEmpty(think)) reasoning.Append(think);

                    // 正文增量。
                    string? piece = AsString(delta?["content"]);
                    if (!string.IsNullOrEmpty(piece))
                    {
                        content.Append(piece);
                        emitted++;
                        EmitDelta(onDelta, piece);
                    }

                    // 工具调用增量：按 index 聚合 id / name / arguments 三样碎片。
                    if (delta?["tool_calls"] is JsonArray toolDeltas)
                        foreach (var node in toolDeltas)
                            AccumulateToolDelta(toolAcc, node);

                    string? fr = AsString(choice?["finish_reason"]);
                    if (!string.IsNullOrEmpty(fr)) finishReason = fr;
                }

                if (content.Length == 0 && toolAcc.Count == 0 && rawFallback.Length > 0)
                {
                    // 兜底：服务商没实现流式，回了一整包 JSON（这时把整段文字一次性抛给界面）。
                    var (fallbackText, fallbackTools) = ParseCompletion(rawFallback.ToString());
                    if (!string.IsNullOrEmpty(fallbackText)) EmitDelta(onDelta, fallbackText);
                    return (fallbackText, fallbackTools);
                }

                if (finishReason == "length")
                    AppLogger.Warn("[AI] 这次回复被 max_tokens 截断了（想要更长就把它调大）。");

                _lastReasoningContent = reasoning.ToString();
                var toolCalls = toolAcc.OrderBy(kv => kv.Key).Select(kv => BuildToolCall(kv.Value)).ToList();
                return (content.Length > 0 ? content.ToString() : null, toolCalls);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && stall.IsCancellationRequested)
            {
                // 断流：这么久没有任何新分片。明确抛一条人话，让上层把气泡收尾（不永远转圈）。
                throw new InvalidOperationException(
                    "AI 回复中途断了（45 秒没有新数据）：网络可能不稳，或者服务商那边限流了，再发一次试试。");
            }
            catch (Exception ex) when (attempt < MaxAttempts && emitted == 0 && IsTransient(ex, ct))
            {
                // 只有"还没吐出任何文字"时才重试（已经显示了半截文字再重试 = 界面重复一遍）。
                LogRetry(attempt, $"{ex.GetType().Name}: {ex.Message}");
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
            finally
            {
                try { stall.Cancel(); } catch { }   // 停掉看门狗，别让它干扰下一次尝试
            }
        }
    }

    /// <summary>
    /// 断流看门狗：一个 5 秒心跳的后台任务，发现"距上次收到分片超过 StreamStallTimeout"就取消这条流。
    /// 整条流只用一个定时器（不是每读一行建一个 Task.Delay），代价可忽略。
    /// </summary>
    private static void StartStallWatchdog(CancellationTokenSource stall, long[] lastDataTicks)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stall.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (stall.IsCancellationRequested) return;
                    if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastDataTicks[0]) > StreamStallTimeout.Ticks)
                    {
                        try { stall.Cancel(); } catch (ObjectDisposedException) { }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AI] 断流看门狗异常（已忽略，不影响本次请求）：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 流式下的工具调用是**分片**下发的：同一个 index 会陆续送来 id、name 和 arguments 的碎片。
    /// 契约：arguments 必须**按顺序拼接**；id / name 只认第一次出现的
    /// （不少服务商每个分片都重复带一遍，无脑追加会拼成 "set_motionset_motion"）。
    /// </summary>
    private static void AccumulateToolDelta(Dictionary<int, ToolCallAccumulator> acc, JsonNode? node)
    {
        var obj = node?.AsObject();
        if (obj == null) return;

        int index = (int)GetNumber(obj["index"], acc.Count);
        if (!acc.TryGetValue(index, out var slot))
        {
            slot = new ToolCallAccumulator();
            acc[index] = slot;
        }

        string? id = AsString(obj["id"]);
        if (!string.IsNullOrEmpty(id) && slot.Id.Length == 0) slot.Id = id;

        var fn = obj["function"]?.AsObject();
        string? name = AsString(fn?["name"]);
        if (!string.IsNullOrEmpty(name) && slot.Name.Length == 0) slot.Name = name;

        string? args = AsString(fn?["arguments"]);
        if (!string.IsNullOrEmpty(args)) slot.Arguments.Append(args);
    }

    /// <summary>分片累加器定稿成一次工具调用。服务商没给 id 时补一个：否则回填给模型的 tool_call_id
    /// 会是空串（assistant 消息里的 tool_calls 与 tool 消息用的是同一个值，两边始终一致）。</summary>
    private static ToolCall BuildToolCall(ToolCallAccumulator slot) =>
        MakeToolCall(
            slot.Id.Length > 0 ? slot.Id : "call_" + Guid.NewGuid().ToString("N")[..8],
            slot.Name,
            slot.Arguments.ToString());

    /// <summary>
    /// 组装一次工具调用。参数解析失败**不吞掉**：标成 ArgsMalformed，让风险判据按 fail-closed 处理（也问一次）。
    /// </summary>
    private static ToolCall MakeToolCall(string id, string name, string rawArguments)
    {
        string raw = rawArguments ?? "";
        JsonObject? args = null;
        bool malformed = false;

        if (raw.Trim().Length == 0)
        {
            args = new JsonObject();          // 无参工具（stop / get_state / pause / resume）合法地就是空参数
        }
        else
        {
            try { args = JsonNode.Parse(raw)?.AsObject(); }
            catch { malformed = true; }
            if (args == null) malformed = true;   // 参数不是 JSON 对象（字符串 / 数组）也算畸形
        }
        return new ToolCall(id, name, args ?? new JsonObject(), malformed, raw);
    }

    // ── 工具定义（function calling schema）────────────────────────────────

    private static JsonArray BuildTools() => new JsonArray(
        FunctionTool("set_scene",
            "设置整体场景氛围，切换设备运动到对应的预设。scene 取值：idle 归中静止 / mild 轻柔 / "
            + "excited 兴奋 / climax 激烈 / riding 律动 / sitting_twist 扭动 / fisting 心跳式。",
            new JsonObject
            {
                ["scene"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "要切换到的场景",
                    ["enum"] = new JsonArray("idle", "mild", "excited", "climax", "riding", "sitting_twist", "fisting"),
                },
            },
            "scene"),
        FunctionTool("set_speed", "设置运动速度档位。",
            new JsonObject
            {
                ["speed"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "速度档位",
                    ["enum"] = new JsonArray("slow", "medium", "fast", "full"),
                },
            },
            "speed"),
        FunctionTool("stop", "立即停止所有运动（安全急停）。", new JsonObject()),
        FunctionTool("pulse",
            "让设备「来一下」，跟着当下气氛或你说的话做即时反应：impact 一次冲击（短促有力）/ "
            + "swell 一次渐强（慢而连贯）/ beat 一拍（轻而准）/ voice 一次呻吟式抽插（一进一出）。"
            + "动作形状由本地生成，受舒适档与急停约束。需要设备已经在自动运动中才有效果。",
            new JsonObject
            {
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "事件形态",
                    ["enum"] = new JsonArray("impact", "swell", "beat", "voice"),
                },
                ["strength"] = NumberProp("强度 0–1（0.5 = 中等，≥0.8 算猛）", 0, 1),
            },
            "kind"),
        FunctionTool("pause", "暂停当前自动行为（不归中，保留位置）。", new JsonObject()),
        FunctionTool("resume", "恢复自动行为。", new JsonObject()),
        FunctionTool("tease", "启动/调整精确的挑逗循环：动 move_sec 秒 → 停 stop_sec 秒 → 歇 pause_sec 秒，循环往复吊胃口。",
            new JsonObject
            {
                ["move_sec"] = NumberProp("动作秒数（动 N 秒）", 1, 10),
                ["stop_sec"] = NumberProp("停顿秒数（停 N 秒）", 1, 15),
                ["pause_sec"] = NumberProp("暂停秒数（歇 N 秒）", 1, 15),
            }),
        FunctionTool("set_motion", "精细控制六个轴（L0/L1/L2/R0/R1/R2）的中心位置、振幅与速度。",
            new JsonObject
            {
                ["L0"] = AxisProperty("左环升降"),
                ["L1"] = AxisProperty("左环前后"),
                ["L2"] = AxisProperty("左环左右"),
                ["R0"] = AxisProperty("右环扭转"),
                ["R1"] = AxisProperty("右环滚转"),
                ["R2"] = AxisProperty("右环俯仰"),
            }),
        FunctionTool("get_state",
            "读取设备当前状态（连接/模式/强度/速度/是否运行/当前预设/各轴幅度）。"
            + "不确定现在是什么状态时先调用它，再决定怎么动。",
            new JsonObject()),
        FunctionTool("set_axis",
            "实时设置单个轴的行程范围（min/max 百分比）。自动运动以 50 为中心振荡，"
            + "实际幅度 = (max-min)/2；用来让某一轴更突出、制造新的姿势感。",
            new JsonObject
            {
                ["axis"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "要调整的轴",
                    ["enum"] = new JsonArray("L0", "L1", "L2", "R0", "R1", "R2"),
                },
                ["min"] = NumberProp("行程下限百分比 (0-100)", 0, 100),
                ["max"] = NumberProp("行程上限百分比 (0-100)", 0, 100),
            },
            "axis", "min", "max"),
        FunctionTool("nudge",
            "一次性的轻微微调：临时加深/减轻强度或加快/放慢节奏，持续 duration_sec 秒后自动恢复。"
            + "适合「稍微……一点」这类要求。",
            new JsonObject
            {
                ["direction"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "微调方向",
                    ["enum"] = new JsonArray("deeper", "gentler", "faster", "slower"),
                },
                ["amount"] = NumberProp("微调幅度（强度或速度的增量）", 0.05, 0.5),
                ["duration_sec"] = NumberProp("持续秒数，之后自动恢复原值", 1, 30),
            },
            "direction"),
        FunctionTool("hold",
            "保持当前姿势/节奏静止 N 秒，之后自动恢复运动。适合停顿、吊胃口。",
            new JsonObject
            {
                ["seconds"] = NumberProp("保持秒数 (1-60)", 1, 60),
            },
            "seconds"));

    private static JsonObject FunctionTool(string name, string description, JsonObject properties, params string[] required)
    {
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Length > 0)
            parameters["required"] = new JsonArray(required.Select(s => JsonValue.Create(s)!).ToArray());
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters,
            },
        };
    }

    private static JsonObject NumberProp(string description, double min, double max) => new()
    {
        ["type"] = "number",
        ["description"] = description,
        ["minimum"] = min,
        ["maximum"] = max,
    };

    private static JsonObject AxisProperty(string description) => new()
    {
        ["center"] = new JsonObject
        {
            ["type"] = "number",
            ["description"] = $"{description} 中心位置 (0-100)",
            ["minimum"] = 0,
            ["maximum"] = 100,
        },
        ["amplitude"] = new JsonObject
        {
            ["type"] = "number",
            ["description"] = $"{description} 振幅 (0-50)",
            ["minimum"] = 0,
            ["maximum"] = 50,
        },
        ["speed"] = new JsonObject
        {
            ["type"] = "number",
            ["description"] = "速度 (0.1-3.0)",
            ["minimum"] = 0.1,
            ["maximum"] = 3.0,
        },
    };

    // ── 工具执行：全部映射到 MotionEngine ──────────────────────────────────

    /// <summary>
    /// 执行一次工具调用。**执行前**按解析后的参数判断风险：高风险动作先经过可取消的确认
    /// （ConfirmToolAsync），拿到"不要"就把拒绝原因回灌给模型；没有确认界面时一律拒绝（fail-closed）。
    /// 开关 AiConfirmRiskyTools=false 时完全跳过确认（用户明确关掉了）。
    /// </summary>
    private async Task<string> ExecuteToolAsync(ToolCall call, CancellationToken ct)
    {
        try
        {
            // ① 风险评估：要读引擎状态判断"是不是从静止启动"，所以放到 UI 线程上评估。
            if (_cfg.AiConfirmRiskyTools)
            {
                var (risky, reason) = await RunOnUiAsync(() => AssessToolRisk(call)).ConfigureAwait(false);
                if (risky)
                {
                    string? refused = await AskConfirmAsync(call, DescribeToolAction(call), reason)
                        .ConfigureAwait(false);
                    if (refused != null) return refused;   // 没放行：把明确原因交回给模型
                }
            }

            // ② 真正执行。运动状态与 UI 相关，统一调度到 UI 线程，避免与主界面竞争 read-modify-write。
            // 注意：App.Dispatch 现在走 BeginInvoke（异步，防止退出时同步 Invoke 挂死），
            // 所以这里必须等它真正执行完再返回，否则工具结果会永远是「工具执行失败」。
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool posted = App.Dispatch(() =>
            {
                try { completion.TrySetResult(ExecuteToolSync(call)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            if (!posted)
                return "工具执行失败：主窗口已关闭";

            try
            {
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return "工具执行失败：界面无响应";
            }
        }
        catch (OperationCanceledException)
        {
            throw;   // 用户取消：交给上层处理，别把它当成"工具失败"回灌给模型
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[AI] 工具 {call.Name} 执行失败", ex);
            return $"工具执行失败：{ex.Message}";
        }
    }

    /// <summary>把一段要在 UI 线程上做的工作变成可 await 的任务（App.Dispatch 是异步投递，必须等它真跑完）。</summary>
    private static Task<T> RunOnUiAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool posted = App.Dispatch(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        if (!posted) tcs.TrySetException(new InvalidOperationException("主窗口已关闭"));
        return tcs.Task;
    }

    // ── 高风险动作：条件式确认（不是逐次弹框）─────────────────────────────

    /// <summary>
    /// 高风险判据（**只看解析后的参数**，不是"某个工具名一律要问"）：
    ///   · 轴偏离中心 ≥ 40%（含振幅 ≥ 40%）—— 接近满行程；
    ///   · 速度 ≥ 2.5 档 —— 接近满档 3.0；
    ///   · hold / tease / nudge 这类"持续动作"超过 20 秒；
    ///   · 从静止启动运动（现在没有任何驱动源在动，这次却要让设备动起来）；
    ///   · 参数畸形 / 解析失败 —— 猜不出它要干什么，也问一次（fail-closed）。
    /// 反例（永不询问）：stop / pause / get_state 这些"停、暂停、只读"的方向。
    /// 必须在 UI 线程调用（要读引擎状态）。
    /// </summary>
    private (bool Risky, string Reason) AssessToolRisk(ToolCall call)
    {
        var args = call.Arguments;

        if (call.ArgsMalformed)
            return (true, "工具参数没解析出来（模型这次生成的参数是坏的）");

        switch (call.Name)
        {
            case "stop" or "pause" or "get_state":
                return (false, "");   // 安全方向的动作永远不用问

            case "pulse":
            {
                double strength = Math.Clamp(GetNumber(args["strength"], 0.5), 0, 1);
                if (strength >= 0.8) return (true, $"来一下的强度 {strength:0.0}（≥ 0.8，算猛）");
                break;
            }

            case "set_speed":
            {
                double speed = SpeedValueOf(AsString(args["speed"]) ?? "medium");
                if (speed >= RiskySpeed) return (true, $"速度 {speed:0.0} 档（≥ {RiskySpeed:0.0}，接近满档）");
                break;
            }

            case "set_axis":
            {
                double min = Math.Clamp(GetNumber(args["min"], 0), 0, 100);
                double max = Math.Clamp(GetNumber(args["max"], 100), 0, 100);
                if (min > max) (min, max) = (max, min);
                // 同一套口径：行程到边（振幅）或整体偏到一侧（偏离中心），取更大的那个。
                double extent = Math.Max((max - min) / 2, Math.Abs((max + min) / 2 - 50));
                if (extent >= RiskyAmplitude)
                    return (true, $"{AsString(args["axis"]) ?? "?"} 轴行程 {min:0}-{max:0}%（偏离中心 {extent:0}%，接近满行程）");
                break;
            }

            case "set_motion":
            {
                foreach (var name in AxisNames)
                {
                    if (args[name] is not JsonObject axis) continue;

                    double amp = GetNumber(axis["amplitude"], 0);
                    if (amp >= RiskyAmplitude)
                        return (true, $"{name} 轴振幅 {amp:0}%（≥ {RiskyAmplitude:0}%，接近满行程）");

                    double center = GetNumber(axis["center"], 50);
                    double off = Math.Abs(center - 50);
                    if (off >= RiskyAmplitude)
                        return (true, $"{name} 轴中心拉到 {center:0}%（偏离中心 {off:0}%）");

                    double axisSpeed = GetNumber(axis["speed"], 0);
                    if (axisSpeed >= RiskySpeed) return (true, $"速度 {axisSpeed:0.0} 档（≥ {RiskySpeed:0.0}）");
                }
                break;
            }

            case "tease":
            {
                double cycle = GetNumber(args["move_sec"], 3) + GetNumber(args["stop_sec"], 5)
                             + GetNumber(args["pause_sec"], 5);
                if (cycle > LongActionSeconds)
                    return (true, $"一轮挑逗循环 {cycle:0.#} 秒（超过 {LongActionSeconds:0} 秒）");
                break;
            }

            case "hold":
            {
                double seconds = GetNumber(args["seconds"], 5);
                if (seconds > LongActionSeconds)
                    return (true, $"保持静止 {seconds:0.#} 秒（超过 {LongActionSeconds:0} 秒）");
                break;
            }

            case "nudge":
            {
                double seconds = GetNumber(args["duration_sec"], 6);
                if (seconds > LongActionSeconds)
                    return (true, $"临时改变强度/节奏持续 {seconds:0.#} 秒（超过 {LongActionSeconds:0} 秒）");
                break;
            }
        }

        // 从静止启动运动：现在没有任何驱动源在动，而这次调用会让设备开始动。
        if (StartsMotion(call.Name, args) && !AnyDriverActive())
            return (true, "设备现在是静止的，而这次会让它直接动起来");

        return (false, "");
    }

    /// <summary>这次调用会不会让设备"开始动"（"从静止启动运动"判据只关心这一类）。</summary>
    private static bool StartsMotion(string tool, JsonObject args) => tool switch
    {
        // idle 是"归中静止"，不是启动；其余场景都会 ApplyPreset + StartAuto。
        "set_scene"  => (AsString(args["scene"]) ?? "") is not ("" or "idle"),
        "set_motion" => true,   // SetMotion 末尾会 StartAuto
        "set_axis"   => true,   // SetAxis 里没有运动就 StartAuto
        "tease"      => true,
        "resume"     => true,
        // nudge 只改强度/速度参数（不会凭空启动）、hold 要求本来就在动、stop/pause 是停 → 都不算。
        _ => false,
    };

    /// <summary>现在有没有任何"驱动源"正在动：自动行为/自定义/遥测/挑逗循环（IsRunning 已涵盖）+ 规则引擎 + 脚本播放。</summary>
    private static bool AnyDriverActive()
    {
        var engine = App.Engine;
        if (engine.IsRunning || engine.RuleEngineActive) return true;
        try { return App.FunscriptPlayer?.IsPlaying == true; }
        catch { return false; }
    }

    /// <summary>
    /// 高风险动作的确认。返回 null = 放行；返回字符串 = 拒绝，并把这句话当工具结果回灌给模型
    /// （模型因此知道"被用户拒了"，可以换个更温和的动作，而不是以为执行成功）。
    /// </summary>
    private async Task<string?> AskConfirmAsync(ToolCall call, string action, string reason)
    {
        var handler = ConfirmToolAsync;
        if (handler == null)
        {
            // fail-closed：没人能确认（别处直接 new 了这个服务、或界面还没起来）→ 不执行，并说清原因。
            AppLogger.Warn($"[AI] 高风险动作 {call.Name}（{reason}）没有确认界面，按安全策略拒绝执行。");
            return $"没有执行「{action}」：这属于高风险动作（{reason}），而当前没有可用的确认界面，"
                 + "按安全策略一律拒绝。请换一个幅度更小、速度更慢的动作，或者先问用户。";
        }

        bool approved;
        try
        {
            var request = new ToolConfirmRequest(call.Name, action, reason, call.RawArguments);
            // 兜底超时：确认界面万一卡住，30 秒后按"拒绝"收尾（同样 fail-closed）。
            approved = await handler(request).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AI] 高风险动作确认环节异常，按拒绝处理：{ex.Message}");
            approved = false;
        }

        if (approved) return null;
        return $"用户没有同意执行「{action}」（原因：{reason}），这次没有执行。"
             + "不要重复尝试同一个动作，先问用户想怎样，或者换成更温和的动作。";
    }

    private static readonly Dictionary<string, string> SceneLabels = new()
    {
        ["idle"] = "归中静止", ["mild"] = "轻柔", ["excited"] = "兴奋",
        ["climax"] = "激烈", ["riding"] = "律动", ["sitting_twist"] = "扭动", ["fisting"] = "心跳式",
    };

    /// <summary>把工具调用翻成一句人话（确认条上给用户看"到底要做什么"，不看工具名和参数）。</summary>
    private static string DescribeToolAction(ToolCall call)
    {
        var a = call.Arguments;
        switch (call.Name)
        {
            case "set_scene":
            {
                string scene = AsString(a["scene"]) ?? "?";
                return $"切到场景「{(SceneLabels.TryGetValue(scene, out var label) ? label : scene)}」";
            }
            case "set_speed":
                return $"把速度档位切到「{AsString(a["speed"]) ?? "medium"}」";
            case "set_motion":
            {
                string detail = string.Join("、", AxisNames
                    .Where(axis => a[axis] is JsonObject)
                    .Select(axis => $"{axis} 中心 {GetNumber(a[axis]!["center"], 50):0}%、振幅 {GetNumber(a[axis]!["amplitude"], 0):0}%"));
                return detail.Length > 0 ? $"调整各轴：{detail}" : "调整各轴运动参数";
            }
            case "set_axis":
                return $"{AsString(a["axis"]) ?? "?"} 轴行程 {GetNumber(a["min"], 0):0}-{GetNumber(a["max"], 100):0}%";
            case "tease":
                return $"挑逗循环：动 {GetNumber(a["move_sec"], 3):0.#} 秒 → 停 {GetNumber(a["stop_sec"], 5):0.#} 秒"
                     + $" → 歇 {GetNumber(a["pause_sec"], 5):0.#} 秒";
            case "hold":  return $"保持当前姿势 {GetNumber(a["seconds"], 5):0.#} 秒";
            case "nudge": return $"临时微调（{AsString(a["direction"]) ?? "deeper"}）{GetNumber(a["duration_sec"], 6):0.#} 秒";
            case "resume":    return "恢复自动运动";
            case "pause":     return "暂停自动运动";
            case "stop":      return "立刻急停";
            case "get_state": return "读取设备状态";
            default:          return call.Name;
        }
    }

    private string ExecuteToolSync(ToolCall call)
    {
        return call.Name switch
        {
            "set_scene" => SetScene(AsString(call.Arguments["scene"]) ?? "mild", call.Arguments),
            "set_speed" => SetSpeed(AsString(call.Arguments["speed"]) ?? "medium"),
            "stop"      => Stop(),
            "pause"     => Pause(),
            "resume"    => Resume(),
            "tease"     => Tease(call.Arguments),
            "set_motion" => SetMotion(call.Arguments),
            "get_state" => GetState(),
            "set_axis"  => SetAxis(call.Arguments),
            "pulse"     => Pulse(call.Arguments),
            "nudge"     => Nudge(call.Arguments),
            "hold"      => Hold(call.Arguments),
            _           => $"未知工具：{call.Name}",
        };
    }

    /// <summary>pulse：让设备"来一下"。只决定事件形态与强度，动作形状交给本地语汇。</summary>
    private string Pulse(JsonObject args)
    {
        string kindName = (AsString(args["kind"]) ?? "voice").Trim().ToLowerInvariant();
        MotionEventKind kind = kindName switch
        {
            "impact" => MotionEventKind.Impact,
            "swell"  => MotionEventKind.Swell,
            "beat"   => MotionEventKind.Beat,
            _        => MotionEventKind.Voice,
        };
        double strength = Math.Clamp(GetNumber(args["strength"], 0.5), 0.05, 1.0);
        if (!_engine.PulseVocab(kind, strength))
            return "设备现在没有在自动运动，来一下不会有反应。先用 set_scene（比如 excited）让它动起来，再用 pulse。";
        return $"已来一下：{kindName}（强度 {strength:0.0}）";
    }

    private static readonly Dictionary<string, string> SceneMap = new()
    {
        ["idle"] = "__home__",
        ["mild"] = "gentle",
        ["excited"] = "daily",
        ["climax"] = "ambush",
        ["riding"] = "wave",
        ["sitting_twist"] = "tease",
        ["fisting"] = "heartbeat",
    };

    private string SetScene(string scene, JsonObject args)
    {
        if (!SceneMap.TryGetValue(scene, out string? preset))
            return $"未知场景：{scene}";

        if (preset == "__home__")
        {
            // Home() 会解除急停并重新使能输出：AI 不能替用户做这个决定。
            if (_engine.EmergencyStopped)
                return "设备处于急停锁定，AI 不会自动解除急停。请到主界面点「全部归中」并确认。";

            _engine.Home();
            return $"场景已切到「{scene}（归中静止）」。";
        }

        ApplyPreset(preset);
        return $"场景已切到「{scene}」，对应预设「{preset}」。";
    }

    private void ApplyPreset(string id)
    {
        _engine.ApplyQuickPreset(id);
        if (!_engine.AutoRunning) _engine.StartAuto();
    }

    /// <summary>速度档位 → 数值（唯一真源：SetSpeed 与"高风险速度"判据都用它）。</summary>
    private static double SpeedValueOf(string speed) => speed switch
    {
        "slow"   => 0.5,
        "medium" => 1.0,
        "fast"   => 1.8,
        "full"   => 3.0,
        _        => 1.0,
    };

    private string SetSpeed(string speed)
    {
        double value = SpeedValueOf(speed);
        _engine.Speed = value;
        return $"速度已设为「{speed}」（{value:0.0}）。";
    }

    private string Stop()
    {
        _holdToken++;                       // 急停后不再自动恢复 hold
        _engine.EmergencyStop();
        return "已执行急停，所有运动停止。";
    }

    private string Pause()
    {
        _holdToken++;                       // 手动暂停优先，取消 hold 的自动恢复
        _engine.StopAuto();
        return "已暂停自动行为（保持当前位置）。";
    }

    private string Resume()
    {
        bool ok = _engine.StartAuto();
        return ok ? "已恢复自动行为。" : "无法恢复：设备未连接或输出锁定。";
    }

    private string Tease(JsonObject args)
    {
        double move = Math.Clamp(GetNumber(args["move_sec"], 3), 1, 10);
        double stop = Math.Clamp(GetNumber(args["stop_sec"], 5), 1, 15);
        double pause = Math.Clamp(GetNumber(args["pause_sec"], 5), 1, 15);
        bool ok = _engine.StartTease(move, stop, pause);
        if (!ok)
            return "无法启动挑逗循环：设备未连接、处于急停/归中，或规则引擎接管。";
        return $"已启动挑逗循环：动 {move:0.#} 秒 → 停 {stop:0.#} 秒 → 歇 {pause:0.#} 秒。";
    }

    /// <summary>轴顺序（唯一真源：<see cref="Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxisNames = Osr6DeviceProfile.InstalledAxes;

    private string SetMotion(JsonObject args)
    {
        var centers = new double[6];
        Array.Fill(centers, 50.0);
        bool anyAmp = false, anyCenter = false;

        for (int i = 0; i < AxisNames.Length; i++)
        {
            if (args[AxisNames[i]] is not JsonObject axis) continue;

            if (axis["amplitude"] != null)
            {
                double amp = GetNumber(axis["amplitude"], 0);
                _engine.SetAxisAmp(i, Math.Clamp(amp, 0, 50));
                anyAmp = true;
            }
            if (axis["center"] != null)
            {
                centers[i] = Math.Clamp(GetNumber(axis["center"], 50), 0, 100);
                anyCenter = true;
            }
            if (axis["speed"] != null)
                _engine.Speed = Math.Clamp(GetNumber(axis["speed"], 1.0), 0.1, 3.0);
        }

        if (anyCenter && _engine.TrySendDirectAxes(centers))
            return $"已发送轴位置（直接送轴），并{(anyAmp ? "更新振幅。" : "保持原振幅。")}";

        if (!_engine.AutoRunning) _engine.StartAuto();
        return $"已更新各轴{(anyAmp ? "振幅" : "参数")}，{(anyCenter ? "请等待归位" : "")}已启动自动。";
    }

    // ── 遥控者：状态感知 + 实时改变 ───────────────────────────────────────

    /// <summary>get_state：读取当前设备状态，返回 JSON 字符串给模型（让 AI 对设备有感知）。</summary>
    private string GetState()
    {
        var axisAmp = new JsonObject();
        double[] amp = _engine.GetAxisAmp();
        for (int i = 0; i < AxisNames.Length; i++)
            axisAmp[AxisNames[i]] = Math.Round(amp[i], 1);

        var axisRange = new JsonObject();
        foreach (var (axis, limits) in _axisRanges)
            axisRange[axis] = new JsonArray(Math.Round(limits.Min, 1), Math.Round(limits.Max, 1));

        string mode = _engine.ActiveMode switch
        {
            MotionMode.Auto      => "auto",
            MotionMode.Stroke    => "stroke",
            MotionMode.Custom    => "custom",
            MotionMode.Telemetry => "telemetry",
            _                    => "idle",
        };

        var state = new JsonObject
        {
            ["connected"] = App.Serial.IsOpen,
            ["port"] = App.Serial.PortName,
            ["simulation"] = App.Serial.IsSimulation,
            ["safety"] = _engine.SafetyStatus,
            ["emergency_stopped"] = _engine.EmergencyStopped,
            ["homing"] = _engine.IsHoming,
            ["mode"] = mode,
            ["running"] = _engine.IsRunning,
            ["auto_running"] = _engine.AutoRunning,
            ["preset"] = _engine.AutoPattern,
            ["current_preset"] = _engine.CurrentAutoPattern,
            ["current_preset_label"] = _engine.CurrentAutoPatternLabel,
            ["bpm"] = Math.Round(_engine.CurrentAutoBpm, 1),
            ["speed"] = Math.Round(_engine.Speed, 2),
            ["intensity"] = Math.Round(_engine.IntensityScale, 2),
            ["teasing"] = _engine.TeasingMode,
            ["behavior_held"] = _engine.AutoBehaviorHeld,
            ["rule_engine_active"] = _engine.RuleEngineActive,
            ["axis_amplitude"] = axisAmp,
        };
        if (axisRange.Count > 0) state["axis_range"] = axisRange;
        return state.ToJsonString();
    }

    /// <summary>
    /// set_axis：设置单个轴的行程范围。落到 MotionEngine 现成的每轴机制 SetAxisAmp（0-100，实际生效 0-50）：
    /// 行程宽度的一半即振幅；自动运动固定以 50 为中心，故 min/max 只决定幅度。
    /// </summary>
    private string SetAxis(JsonObject args)
    {
        string axis = (AsString(args["axis"]) ?? "").Trim().ToUpperInvariant();
        int index = Array.IndexOf(AxisNames, axis);
        if (index < 0)
            return $"未知轴「{axis}」：只支持 L0/L1/L2/R0/R1/R2。";

        double min = Math.Clamp(GetNumber(args["min"], 0), 0, 100);
        double max = Math.Clamp(GetNumber(args["max"], 100), 0, 100);
        if (min > max) (min, max) = (max, min);

        double amplitude = Math.Clamp((max - min) / 2, 0, 50);
        double center = (min + max) / 2;
        _engine.SetAxisAmp(index, amplitude);
        _axisRanges[axis] = (min, max);

        if (!_engine.AutoRunning && !_engine.IsRunning) _engine.StartAuto();

        string note = Math.Abs(center - 50) > 2
            ? $"；注意自动运动固定以 50 为中心，实际行程约 50±{amplitude:0.#}%（要偏移中心请用 set_motion 的 center 直接送轴）"
            : "";
        string tail = _engine.CanRun ? "" : "（设备未连接或急停锁定，参数已记录，恢复后生效）";
        return $"{axis} 轴行程已设为 {min:0.#}-{max:0.#}%（幅度 {amplitude:0.#}）{note}{tail}。";
    }

    /// <summary>
    /// nudge：一次性微调。落到现成机制——临时改 IntensityScale / Speed，到点自动回退。
    /// MotionEngine 没有公开的「当前轴位置 / 相对位移」API，故用强度、节奏作为微调代理。
    /// </summary>
    private string Nudge(JsonObject args)
    {
        string direction = (AsString(args["direction"]) ?? "deeper").Trim().ToLowerInvariant();
        if (direction is not ("deeper" or "gentler" or "faster" or "slower"))
            return $"未知微调方向「{direction}」：只支持 deeper/gentler/faster/slower。";
        if (!_engine.CanRun)
            return "设备未连接或处于急停锁定，无法微调。";

        double amount = Math.Clamp(GetNumber(args["amount"], 0.2), 0.05, 0.5);
        double duration = Math.Clamp(GetNumber(args["duration_sec"], 6), 1, 30);
        bool increase = direction is "deeper" or "faster";

        if (direction is "faster" or "slower")
        {
            double beforeSpeed = _engine.Speed;
            double targetSpeed = Math.Clamp(beforeSpeed + (increase ? amount : -amount), 0.1, 3.0);
            _engine.Speed = targetSpeed;
            ScheduleRestore(duration, () =>
            {
                if (Math.Abs(_engine.Speed - targetSpeed) < 0.001) _engine.Speed = beforeSpeed;
            });
            return $"已临时{(increase ? "加快" : "放慢")}节奏到 {targetSpeed:0.0} 档，{duration:0.#} 秒后恢复 {beforeSpeed:0.0} 档。";
        }

        double beforeIntensity = _engine.IntensityScale;
        double targetIntensity = Math.Clamp(beforeIntensity + (increase ? amount : -amount), 0.1, 2.0);
        _engine.IntensityScale = targetIntensity;
        ScheduleRestore(duration, () =>
        {
            if (Math.Abs(_engine.IntensityScale - targetIntensity) < 0.001) _engine.IntensityScale = beforeIntensity;
        });
        return $"已临时{(increase ? "加深" : "减轻")}强度到 {targetIntensity:0.00}，{duration:0.#} 秒后恢复 {beforeIntensity:0.00}。";
    }

    /// <summary>hold：保持当前姿势 N 秒（停自动 → 到点恢复）；期间若有新指令则放弃自动恢复。</summary>
    private string Hold(JsonObject args)
    {
        if (!_engine.CanRun)
            return "设备未连接或处于急停锁定，无法保持。";
        if (!_engine.AutoRunning)
            return "当前没有正在运行的自动行为，无需保持。";

        double seconds = Math.Clamp(GetNumber(args["seconds"], 5), 1, 60);
        int token = ++_holdToken;
        _engine.StopAuto();

        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
        {
            try
            {
                App.Dispatch(() =>
                {
                    if (token != _holdToken) return;                    // 期间有新指令 → 不恢复
                    if (_engine.AutoRunning || !_engine.CanRun) return;
                    _engine.StartAuto();
                });
            }
            catch (Exception ex) { AppLogger.Warn($"[AI] 保持恢复失败: {ex.Message}"); }
        }, TaskScheduler.Default);

        return $"已保持当前姿势 {seconds:0.#} 秒，之后自动恢复运动。";
    }

    /// <summary>延迟若干秒后在 UI 线程执行恢复动作（nudge 的自动回退）。</summary>
    private static void ScheduleRestore(double seconds, Action restore)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
        {
            try { App.Dispatch(restore); }
            catch (Exception ex) { AppLogger.Warn($"[AI] 微调恢复失败: {ex.Message}"); }
        }, TaskScheduler.Default);
    }

    // ── TTS：edge-tts 生成 mp3 + NAudio 播放 ──────────────────────────────

    /// <summary>打断当前 TTS：停掉播放 + 作废尚未播出的生成结果。</summary>
    public void StopSpeaking()
    {
        Interlocked.Increment(ref _speakGeneration);
        StopPlayback();
    }

    /// <summary>停止 NAudio 播放并复位句柄（不改动 generation，供 PlayFile 内部调用）。</summary>
    private void StopPlayback()
    {
        lock (_ttsLock)
        {
            try { _waveOut?.Stop(); } catch { }
            try { _audioReader?.Dispose(); } catch { }
            _waveOut = null;
            _audioReader = null;
        }
        SetSpeaking(false);
    }

    /// <summary>更新说话状态并通知订阅方（UI 按钮文案/灰显）。</summary>
    private void SetSpeaking(bool value)
    {
        if (_isSpeaking == value) return;
        _isSpeaking = value;
        try { SpeakingChanged?.Invoke(); } catch { }
    }

    private async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // 实时通话进行中：本地 TTS 让位（音频通道被 RealtimeVoiceService 占用）
        if (SuppressTts)
        {
            AppLogger.Info("[AI] 实时通话进行中，跳过本地 TTS 播放。");
            return;
        }
        int generation = Volatile.Read(ref _speakGeneration);
        string clean = CleanForTts(text);
        string tmp = Path.Combine(Path.GetTempPath(), $"hexa_tts_{Guid.NewGuid():N}.mp3");
        try
        {
            if (await GenerateEdgeTtsAsync(clean, tmp).ConfigureAwait(false) && File.Exists(tmp))
            {
                // 生成期间用户已发新消息 / 点了停止 → 丢弃这段语音，不播放
                if (generation != Volatile.Read(ref _speakGeneration)) { TryDeleteFile(tmp); return; }
                PlayFile(tmp);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AI] edge-tts 生成失败，尝试降级: {ex.Message}");
        }
        if (generation != Volatile.Read(ref _speakGeneration)) return;
        TryFallbackSpeech(clean);
    }

    /// <summary>删除 TTS 临时 mp3（失败忽略）。</summary>
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private async Task<bool> GenerateEdgeTtsAsync(string text, string outPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "edge-tts",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--voice");
        psi.ArgumentList.Add("zh-CN-XiaoyiNeural");
        psi.ArgumentList.Add("--rate=+30%");
        psi.ArgumentList.Add("--text");
        psi.ArgumentList.Add(text);
        psi.ArgumentList.Add("--write-media");
        psi.ArgumentList.Add(outPath);

        using Process? proc = Process.Start(psi);
        if (proc == null) return false;
        // edge-tts 是外部进程：必须加超时，否则它挂起会让整条 TTS 链路永久卡住。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            AppLogger.Warn("[AI] edge-tts 超时（20s），已结束进程并降级");
            return false;
        }
        return proc.ExitCode == 0 && File.Exists(outPath);
    }

    private void PlayFile(string path)
    {
        StopPlayback();
        var reader = new AudioFileReader(path);
        var waveOut = new WaveOut();
        waveOut.Init(reader);
        waveOut.PlaybackStopped += (_, _) =>
        {
            lock (_ttsLock)
            {
                try { reader.Dispose(); } catch { }
                try { waveOut.Dispose(); } catch { }
                if (ReferenceEquals(_waveOut, waveOut)) _waveOut = null;
            }
            SetSpeaking(false);
            TryDeleteFile(path);
        };
        lock (_ttsLock)
        {
            _waveOut = waveOut;
            _audioReader = reader;
        }
        waveOut.Play();
        SetSpeaking(true);
    }

    /// <summary>降级方案：若系统存在 System.Speech 则用它朗读（音质较差）。无需新增包，反射加载。</summary>
    private static void TryFallbackSpeech(string text)
    {
        try
        {
            Type? type = Type.GetType("System.Speech.Synthesis.SpeechSynthesizer, System.Speech");
            if (type == null) { AppLogger.Info("[AI] 未找到 System.Speech，TTS 降级不可用。"); return; }
            object? synth = Activator.CreateInstance(type);
            var speak = type.GetMethod("Speak", new[] { typeof(string) });
            Task.Run(() =>
            {
                try { speak?.Invoke(synth, new object[] { text }); }
                catch { }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AI] TTS 降级失败: {ex.Message}");
        }
    }

    private static readonly Regex TtsCodeBlock = new(@"```[\s\S]*?```", RegexOptions.Compiled);
    private static readonly Regex TtsTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex TtsMarkdown = new(@"[*_`#>|~^=\-]", RegexOptions.Compiled);
    private static readonly Regex TtsKeep = new(@"[^\p{IsCJKUnifiedIdeographs}a-zA-Z0-9，。！？、；：""''\s]", RegexOptions.Compiled);

    /// <summary>clean_text_for_tts：去掉 think/标签/括号/markdown 等，只保留中英文与常见标点。</summary>
    private static string CleanForTts(string text)
    {
        text = TtsCodeBlock.Replace(text, " ");
        text = TtsTag.Replace(text, " ");
        text = TtsMarkdown.Replace(text, " ");
        text = TtsKeep.Replace(text, " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > 500) text = text[..500];
        return text;
    }

    // ── 帮助函数 ──────────────────────────────────────────────────────────

    private static string BuildSystemPrompt(string personaPrompt)
    {
        string baseText = string.IsNullOrWhiteSpace(personaPrompt) ? Personas[0].Prompt : personaPrompt;
        return baseText + "\n\n" +
            "【你的角色：节奏导演 / 遥控者】\n" +
            "你手里握着用户 OSR6 六轴设备（L0/L1/L2/R0/R1/R2）的实时控制权，不是被动等指令的执行器。\n" +
            "你要像导演一样主动设计节奏，像两个人互动换体位那样自然：自己决定什么时候动、怎么动、动多久。\n\n" +
            "【怎么动】\n" +
            "1. 用「意图」而不是「参数」思考：先想清楚要的效果（例如「先慢后快，然后突然停住吊一下」），再挑工具去实现。\n" +
            "2. 主动变化，不要每次都用同一个预设：同一场对话里至少变换一次场景、幅度或节奏，不要连续两轮用同样的 set_scene。\n" +
            "3. 节奏的时间尺度要跳动：短促变化用 2~6 秒，长段落用 20~40 秒，像换姿势一样交替，不要一直匀速。\n" +
            "4. 一轮里通常调用 1~3 个工具就够；也可以只说话不动设备——有时「不动」本身就是节奏。\n\n" +
            "【你的工具】\n" +
            "- get_state：先感知再动作。不确定当前连接/模式/强度/预设/各轴幅度时，先调用它。\n" +
            "- set_scene：切换大段落氛围（idle/mild/excited/climax/riding/sitting_twist/fisting）。\n" +
            "- set_speed：整体速度档位（slow/medium/fast/full）。\n" +
            "- set_axis：实时改单个轴的行程范围（min/max 百分比），用来制造偏重某一轴的新姿势。\n" +
            "- set_motion：精细设定各轴中心/振幅/速度，用来做一次明确的姿态。\n" +
            "- nudge：一次性轻微微调（加深/减轻/加快/放慢），到点自动恢复，适合「稍微……一点」。\n" +
            "- hold：保持当前姿势 N 秒，之后自动恢复，适合停顿、吊胃口。\n" +
            "- tease：精确的「动 N 秒 → 停 N 秒 → 歇 N 秒」循环。\n" +
            "- pause / resume：暂停或恢复自动行为；stop：立刻急停。\n\n" +
            "【安全底线】\n" +
            "用户说停、喊停、表示要停，或出现任何不适信号，立刻调用 stop（急停），不要犹豫。\n" +
            "所有动作都在设备安全限制内，绝不尝试绕过。\n\n" +
            "【说话方式】\n" +
            "措辞自然、贴合气氛，用简体中文；不要罗列工具名或参数，不要解释你在调用什么工具，就当一个真人在主导节奏。";
    }

    private static JsonArray BuildToolCallsArray(List<ToolCall> calls)
    {
        var array = new JsonArray();
        foreach (var call in calls)
        {
            array.Add(new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments.ToJsonString(),
                },
            });
        }
        return array;
    }

    private static string? AsString(JsonNode? node)
    {
        if (node == null) return null;
        try { return node.GetValue<string>(); }
        catch { return node.ToString(); }
    }

    private static double GetNumber(JsonNode? node, double fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out double d)) return d;
            if (value.TryGetValue<int>(out int i)) return i;
            if (value.TryGetValue<long>(out long l)) return l;
        }
        return double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
            ? r
            : fallback;
    }

    private static ToolCall ParseToolCall(JsonNode? node)
    {
        var obj = node?.AsObject();
        string id = AsString(obj?["id"]) ?? "";
        var fn = obj?["function"]?.AsObject();
        string name = AsString(fn?["name"]) ?? "";
        return MakeToolCall(id, name, AsString(fn?["arguments"]) ?? "");
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    // ── 类型定义 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 一次工具调用。ArgsMalformed=true 表示模型给的参数没解析成 JSON 对象
    /// （风险判据据此 fail-closed：猜不出要干什么也得先问一句）；RawArguments 留着给确认条/日志看原文。
    /// </summary>
    private sealed record ToolCall(string Id, string Name, JsonObject Arguments, bool ArgsMalformed, string RawArguments);

    /// <summary>流式工具调用的分片累加器（一个 index 一路）。</summary>
    private sealed class ToolCallAccumulator
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments { get; } = new();
    }

    // ── 角色卡：导入/导出自定义人格（对齐 c.py parse_character_card / build_prompt_from_card）──
    private static readonly List<AiPersona> _custom = new();
    private static readonly string CustomFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexa", "custom_personas.json");

    public static IReadOnlyList<AiPersona> AllPersonas => Personas.Concat(_custom).ToArray();
    public static IReadOnlyList<AiPersona> CustomPersonas => _custom;
    public static bool HasCustomPersonas => _custom.Count > 0;

    // 解析角色卡 JSON（兼容 chara_card_v2；含 data 嵌套）→ 构建系统提示 → AiPersona
    public static AiPersona ParseCardJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            root = d;
        string Get(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string name = Get("name");
        var persona = new AiPersona(
            string.IsNullOrWhiteSpace(name) ? "自定义角色" : name,
            BuildCardPrompt(Get("name"), Get("description"), Get("personality"), Get("scenario"), Get("first_mes"), Get("system_prompt")));
        return persona;
    }

    // 导出当前人格为 JSON 角色卡
    public static string ExportPersonaJson(AiPersona p) =>
        new JsonObject { ["name"] = p.Id, ["prompt"] = p.Prompt }
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public static bool AddCustomPersona(AiPersona p)
    {
        _custom.RemoveAll(x => x.Id == p.Id);
        _custom.Add(p); SaveCustom(); return true;
    }
    public static void RemoveCustomPersona(string id) { _custom.RemoveAll(x => x.Id == id); SaveCustom(); }

    // 构建系统提示（对齐 c.py build_prompt_from_card：中文必须 + 卡字段 + OSR 控制指令）
    private static string BuildCardPrompt(string name, string desc, string personality, string scenario, string firstMes, string systemPrompt)
    {
        var parts = new List<string> { "无论角色卡使用什么语言，你必须始终用中文回复。" };
        if (!string.IsNullOrEmpty(systemPrompt)) parts.Add(systemPrompt);
        if (!string.IsNullOrEmpty(name)) parts.Add("你的名字是" + name + "。");
        if (!string.IsNullOrEmpty(desc)) parts.Add("【角色设定】\n" + desc);
        if (!string.IsNullOrEmpty(personality)) parts.Add("【性格】\n" + personality);
        if (!string.IsNullOrEmpty(scenario)) parts.Add("【场景】\n" + scenario);
        parts.Add("你可以调用工具控制用户的 OSR6 设备，安全第一，用户喊停就立刻停。");
        return string.Join("\n\n", parts);
    }

    static AiAssistantService()
    {
        try
        {
            if (File.Exists(CustomFile))
            {
                var arr = JsonNode.Parse(File.ReadAllText(CustomFile))?.AsArray();
                if (arr != null)
                    foreach (var n in arr)
                    {
                        var id = n?["name"]?.GetValue<string>();
                        var pr = n?["prompt"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(id)) _custom.Add(new AiPersona(id, pr ?? ""));
                    }
            }
        }
        catch { }
    }
    private static void SaveCustom()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CustomFile)!);
            File.WriteAllText(CustomFile,
                new JsonArray(_custom.Select(p => (JsonNode)new JsonObject { ["name"] = p.Id, ["prompt"] = p.Prompt }).ToArray()).ToJsonString());
        }
        catch (Exception ex) { AppLogger.Warn("[AI] 保存角色卡失败: " + ex.Message); }
    }
}

/// <summary>人工智能角色卡：Id 用于 UI 展示，Prompt 作为 system prompt。</summary>
public sealed record AiPersona(string Id, string Prompt);

/// <summary>
/// 高风险工具调用的"执行前确认"请求（给「AI 助手」页做非模态 3 秒倒计时条用）。
/// ActionText = 一句人话的动作描述；Reason = 为什么判成高风险；ArgumentsJson = 模型给的原参数（排查/展示用）。
/// </summary>
public sealed record ToolConfirmRequest(string ToolName, string ActionText, string Reason, string ArgumentsJson);
