using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Hexa.Models;
using NAudio.Wave;

namespace Hexa.Services;

/// <summary>
/// 「实时语音通话」（第二阶段）：直连 OpenAI Realtime 兼容 WebSocket，做全双工语音对话。
/// 上行：NAudio WaveInEvent 采集 16bit 单声道 PCM → base64 → input_audio_buffer.append；
/// 下行：response.audio.delta 的 base64 PCM16 → NAudio BufferedWaveProvider + WaveOut 播放；
/// 文字：conversation.item.create + response.create（把打字内容也送进同一个会话）。
/// 默认关闭（AppSettings.AiRealtimeEnabled=false）；URL/Key 未配置时 StartAsync 直接返回 false。
/// 所有异常一律捕获并写 AppLogger，绝不把异常抛到 UI 线程。
/// </summary>
public sealed class RealtimeVoiceService : IDisposable
{
    /// <summary>Realtime 协议的 PCM16 基准采样率（OpenAI 的 pcm16 输入/输出都是 24kHz）。</summary>
    private const int ProtocolSampleRate = 24000;

    /// <summary>上行分片时长（毫秒）：把采集回调的小块攒成约 100ms 再发，减少 WebSocket 帧数量。</summary>
    private const int UplinkChunkMs = 100;

    private readonly AppSettings _cfg;
    private readonly AiAssistantService? _ai;   // 通话期间用于暂停本地 TTS

    private readonly object _stateLock = new();
    private readonly object _captureLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private WaveIn? _capture;
    private WaveOut? _playback;
    private BufferedWaveProvider? _playBuffer;
    private Channel<byte[]>? _uplink;

    private readonly List<byte> _captureCarry = new();
    private readonly StringBuilder _assistantDelta = new();

    private int _playbackRate = ProtocolSampleRate;   // 扬声器播放采样率（= 配置采样率）
    private int _captureRate = ProtocolSampleRate;    // 实际采集采样率（设备不支持配置值时降级）
    private volatile bool _isConnected;
    private bool _restoreTtsOnStop;

    public RealtimeVoiceService(AppSettings cfg, AiAssistantService? ai = null)
    {
        _cfg = cfg;
        _ai = ai;
    }

    // ── 公开 API ──────────────────────────────────────────────────────────

    /// <summary>是否已连接（会话可用）。</summary>
    public bool IsConnected => _isConnected;

    /// <summary>状态文字变化（连接中/已连接/错误/已结束）；可能在后台线程触发，订阅方需自行调度到 UI 线程。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>用户语音转写结果（conversation.item.input_audio_transcription.completed）。</summary>
    public event Action<string>? UserTranscript;

    /// <summary>AI 语音回复的转写结果（response.audio_transcript.done）。</summary>
    public event Action<string>? AssistantTranscript;

    /// <summary>通话结束（用户挂断、服务端关闭或异常中断）时触发；供 UI 复位按钮状态。</summary>
    public event Action? Disconnected;

    /// <summary>配置是否齐全（总开关 + URL + Key）。</summary>
    public bool IsConfigured => ConfigProblem() == null;

    /// <summary>
    /// 返回当前配置缺失说明；配置齐全返回 null。
    /// 供 UI 直接提示「需要用户填写 X」，例如「请在设置中把 AiRealtimeEnabled 设为 true」。
    /// </summary>
    public string? ConfigProblem()
    {
        if (!_cfg.AiRealtimeEnabled)
            return "实时通话已关闭：请在设置中把 AiRealtimeEnabled 设为 true（默认关闭）。";
        if (string.IsNullOrWhiteSpace(_cfg.AiRealtimeUrl))
            return "尚未配置实时接口地址：请填写 AiRealtimeUrl（例如 wss://api.openai.com/v1/realtime）。";
        if (string.IsNullOrWhiteSpace(ResolveKey()))
            return "尚未配置实时接口 Key：请填写 AiRealtimeKey，或复用 AiApiKey。";
        return null;
    }

    /// <summary>
    /// 实时通话专用 Key，留空时复用 AiApiKey。
    /// 两个字段都可能存的是 DPAPI 密文（见 Services/SecretProtector.cs），
    /// 送出去之前必须解密 —— 否则会拿密文当 Key 发请求，直接 401。
    /// 明文（老配置）会被 Unprotect 原样返回，所以这里不需要判断格式。
    /// </summary>
    private string ResolveKey()
    {
        string raw = string.IsNullOrWhiteSpace(_cfg.AiRealtimeKey) ? _cfg.AiApiKey : _cfg.AiRealtimeKey;
        return Hexa.Services.SecretProtector.Unprotect(raw);
    }

    /// <summary>
    /// 建立连接并启动采集/播放。返回 true 表示会话已就绪；
    /// 配置缺失或连接失败时返回 false（原因已通过 StatusChanged 与 AppLogger 上报）。
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_isConnected) return true;

        string? problem = ConfigProblem();
        if (problem != null)
        {
            Report(problem);
            return false;
        }

        try
        {
            lock (_stateLock)
            {
                _cts = new CancellationTokenSource();
                _socket = new ClientWebSocket();
                _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _uplink = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                });
            }
            lock (_captureLock) _captureCarry.Clear();
            _assistantDelta.Clear();

            // 认证头：Realtime 用 Bearer；部分网关还需要 OpenAI-Beta 标记（不认识的头会被忽略）。
            var socket = _socket!;
            socket.Options.SetRequestHeader("Authorization", $"Bearer {ResolveKey()}");
            try { socket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1"); } catch { }

            _playbackRate = _cfg.AiRealtimeSampleRate is >= 8000 and <= 48000
                ? _cfg.AiRealtimeSampleRate
                : ProtocolSampleRate;

            Report("正在连接实时语音服务…");
            await socket.ConnectAsync(new Uri(_cfg.AiRealtimeUrl), _cts!.Token).ConfigureAwait(false);
            _isConnected = true;

            // 通话期间让本地 TTS 让位，避免两套音频抢设备。
            if (_ai != null)
            {
                _ai.StopSpeaking();
                _ai.SuppressTts = true;
                _restoreTtsOnStop = true;
            }

            StartPlayback();
            StartCapture();

            var token = _cts.Token;
            // 两个循环内部各自 try/catch，异常不会冒泡；结束由 Stop() 统一收尾。
            _ = Task.Run(() => SendLoopAsync(token), CancellationToken.None);
            _ = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);

            await SendJsonAsync(BuildSessionUpdate(), token).ConfigureAwait(false);
            Report("实时通话已连接，可以直接说话了。");
            AppLogger.Info($"[实时通话] 已连接 {_cfg.AiRealtimeUrl}（采样率 {_playbackRate}Hz）");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[实时通话] 连接失败", ex);
            Stop();                       // 先收尾，保证最后的提示文字是错误原因
            Report("连接失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>结束通话：停采集/播放、关闭 WebSocket、恢复本地 TTS。可重复调用。</summary>
    public void Stop()
    {
        bool wasRunning;
        WaveIn? capture;
        WaveOut? playback;
        ClientWebSocket? socket;
        CancellationTokenSource? cts;

        lock (_stateLock)
        {
            wasRunning = _isConnected || _capture != null || _socket != null;
            _isConnected = false;
            capture = _capture;   _capture = null;
            playback = _playback; _playback = null;
            _playBuffer = null;
            socket = _socket;     _socket = null;
            cts = _cts;           _cts = null;
            _uplink?.Writer.TryComplete();
            _uplink = null;
        }

        if (capture != null)
        {
            try { capture.DataAvailable -= OnDataAvailable; } catch { }
            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
        }
        if (playback != null)
        {
            try { playback.Stop(); } catch { }
            try { playback.Dispose(); } catch { }
        }
        try { cts?.Cancel(); } catch { }
        try { socket?.Abort(); } catch { }
        try { socket?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }

        lock (_captureLock) _captureCarry.Clear();
        _assistantDelta.Clear();

        if (_ai != null && _restoreTtsOnStop)
        {
            _ai.SuppressTts = false;
            _restoreTtsOnStop = false;
        }

        if (wasRunning)
        {
            AppLogger.Info("[实时通话] 已结束");
            Report("实时通话已结束。");
            try { Disconnected?.Invoke(); }
            catch (Exception ex) { AppLogger.Warn($"[实时通话] 断开回调异常: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 把一段文字送进实时会话（等价于「说了一句话」）：
    /// conversation.item.create 建用户消息 → response.create 请求 AI 回复。
    /// </summary>
    public async Task<bool> SendTextAsync(string text)
    {
        if (!_isConnected || string.IsNullOrWhiteSpace(text)) return false;

        var createItem = new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = text.Trim(),
                }),
            },
        };

        var token = CurrentToken();
        if (!await SendJsonAsync(createItem, token).ConfigureAwait(false)) return false;
        // server_vad 只对音频生效，文字输入需要手动触发一次回复生成。
        return await SendJsonAsync(new JsonObject { ["type"] = "response.create" }, token)
            .ConfigureAwait(false);
    }

    public void Dispose() => Stop();

    // ── 会话配置 ──────────────────────────────────────────────────────────

    /// <summary>session.update：配置模态、语音、PCM16 格式、VAD 与输入转写。</summary>
    private JsonObject BuildSessionUpdate()
    {
        var session = new JsonObject
        {
            ["modalities"] = new JsonArray("audio", "text"),
            ["instructions"] =
                "你是用户的 AI 伴侣，用简体中文口语化地自然对话。"
                + "回复要短，像真人说话一样有停顿感；不要念出 Markdown、括号或表情符号。"
                + "涉及设备动作时只做口头回应，不要输出任何指令。",
            ["voice"] = "alloy",
            ["input_audio_format"] = "pcm16",
            ["output_audio_format"] = "pcm16",
            ["input_audio_transcription"] = new JsonObject { ["model"] = "whisper-1" },
            ["turn_detection"] = new JsonObject
            {
                ["type"] = "server_vad",
                ["threshold"] = 0.5,
                ["prefix_padding_ms"] = 300,
                ["silence_duration_ms"] = 700,
            },
            ["temperature"] = 0.8,
        };

        // 模型名可放在 session 里（新版本协议），也可留空让服务端用 URL 上的默认模型。
        if (!string.IsNullOrWhiteSpace(_cfg.AiRealtimeModel))
            session["model"] = _cfg.AiRealtimeModel;

        return new JsonObject { ["type"] = "session.update", ["session"] = session };
    }

    // ── 音频采集（上行）──────────────────────────────────────────────────

    /// <summary>按配置采样率打开麦克风；设备不支持时依次降级到 48000 / 44100 并自动重采样。</summary>
    private void StartCapture()
    {
        int[] candidates = _playbackRate == 48000
            ? new[] { 48000 }
            : new[] { _playbackRate, 48000, 44100 };

        Exception? last = null;
        foreach (int rate in candidates)
        {
            try
            {
                StartCaptureAt(rate);
                if (rate != ProtocolSampleRate)
                    AppLogger.Info($"[实时通话] 麦克风采集 {rate}Hz，上行重采样到 {ProtocolSampleRate}Hz。");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                AppLogger.Warn($"[实时通话] 麦克风 {rate}Hz 打开失败，尝试下一个采样率: {ex.Message}");
            }
        }
        throw new InvalidOperationException("麦克风打开失败：" + (last?.Message ?? "无可用采样率"));
    }

    private void StartCaptureAt(int rate)
    {
        int previousRate = _captureRate;
        _captureRate = rate;   // 先置位，避免第一块采集数据用错采样率
        var capture = new WaveIn
        {
            WaveFormat = new WaveFormat(rate, 16, 1),   // 16bit 单声道
            BufferMilliseconds = 50,
            NumberOfBuffers = 3,
        };
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) AppLogger.Error("[实时通话] 采集异常停止", e.Exception);
        };
        try
        {
            capture.StartRecording();
        }
        catch
        {
            // 该采样率不被设备支持：先释放再抛，交给上层降级重试。
            _captureRate = previousRate;
            try { capture.DataAvailable -= OnDataAvailable; } catch { }
            try { capture.Dispose(); } catch { }
            throw;
        }
        lock (_stateLock) _capture = capture;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isConnected || e.BytesRecorded <= 0) return;
        try
        {
            // 设备采样率与协议 24kHz 不一致时用线性插值重采样。
            byte[] pcm = _captureRate == ProtocolSampleRate
                ? e.Buffer.AsSpan(0, e.BytesRecorded).ToArray()
                : Resample16(e.Buffer, e.BytesRecorded, _captureRate, ProtocolSampleRate);

            int chunkBytes = ProtocolSampleRate * 2 * UplinkChunkMs / 1000;   // 约 100ms
            lock (_captureLock)
            {
                _captureCarry.AddRange(pcm);
                while (_captureCarry.Count >= chunkBytes)
                {
                    var slice = new byte[chunkBytes];
                    _captureCarry.CopyTo(0, slice, 0, chunkBytes);
                    _captureCarry.RemoveRange(0, chunkBytes);
                    _uplink?.Writer.TryWrite(slice);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[实时通话] 采集数据处理失败: {ex.Message}");
        }
    }

    /// <summary>上行发送循环：串行发送 input_audio_buffer.append，保证 WebSocket 帧顺序。</summary>
    private async Task SendLoopAsync(CancellationToken token)
    {
        Channel<byte[]>? channel;
        lock (_stateLock) channel = _uplink;
        if (channel == null) return;

        try
        {
            await foreach (byte[] chunk in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var payload = new JsonObject
                {
                    ["type"] = "input_audio_buffer.append",
                    ["audio"] = Convert.ToBase64String(chunk),
                };
                if (!await SendJsonAsync(payload, token).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("[实时通话] 上行发送循环异常", ex);
        }
    }

    // ── 音频播放（下行）──────────────────────────────────────────────────

    private void StartPlayback()
    {
        var format = new WaveFormat(_playbackRate, 16, 1);
        // NAudio 3.x：BufferDuration 变只读（用默认 5s 缓冲），DesiredLatency 已移除（用默认延迟）。
        var buffer = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,     // 缓冲为空时输出静音，避免 WaveOut 反复起停
        };

        var output = new WaveOut { NumberOfBuffers = 3 };
        output.Init(buffer);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) AppLogger.Error("[实时通话] 播放异常停止", e.Exception);
        };
        output.Play();

        lock (_stateLock)
        {
            _playBuffer = buffer;
            _playback = output;
        }
    }

    /// <summary>把服务端音频增量（base64 PCM16 @24kHz）解码并写入播放缓冲。</summary>
    private void PlayAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        byte[] pcm;
        try { pcm = Convert.FromBase64String(base64); }
        catch (FormatException) { return; }
        if (pcm.Length < 2) return;

        byte[] output = _playbackRate == ProtocolSampleRate
            ? pcm
            : Resample16(pcm, pcm.Length, ProtocolSampleRate, _playbackRate);

        BufferedWaveProvider? buffer;
        lock (_stateLock) buffer = _playBuffer;
        if (buffer == null) return;

        try { buffer.AddSamples(output, 0, output.Length); }
        catch (Exception ex) { AppLogger.Warn($"[实时通话] 写入播放缓冲失败: {ex.Message}"); }
    }

    /// <summary>用户开始说话（barge-in）：清空播放缓冲，让 AI 的声音立刻让位。</summary>
    private void ClearPlaybackBuffer()
    {
        BufferedWaveProvider? buffer;
        lock (_stateLock) buffer = _playBuffer;
        try { buffer?.ClearBuffer(); } catch { }
    }

    // ── 下行事件循环 ──────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var frame = new byte[16 * 1024];
        var pending = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                ClientWebSocket? socket;
                lock (_stateLock) socket = _socket;
                if (socket is not { State: WebSocketState.Open }) break;

                var result = await socket.ReceiveAsync(frame, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Report("实时语音服务已关闭连接。");
                    break;
                }
                if (result.Count > 0) pending.Write(frame, 0, result.Count);
                if (!result.EndOfMessage) continue;   // 一条 JSON 可能跨多帧

                string json = Encoding.UTF8.GetString(pending.ToArray());
                pending.SetLength(0);
                if (json.Length > 0) HandleServerEvent(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("[实时通话] 接收循环异常", ex);
            Report("实时通话中断：" + ex.Message);
        }
        finally
        {
            Stop();
        }
    }

    /// <summary>分发单个服务端事件；未知类型静默忽略，解析失败只记日志。</summary>
    private void HandleServerEvent(string json)
    {
        JsonObject? evt;
        try { evt = JsonNode.Parse(json)?.AsObject(); }
        catch (JsonException ex)
        {
            AppLogger.Warn($"[实时通话] 事件解析失败: {ex.Message}");
            return;
        }
        if (evt == null) return;

        string type = AsString(evt["type"]) ?? "";
        switch (type)
        {
            case "session.created":
            case "session.updated":
                Report("实时通话已连接，可以直接说话了。");
                break;

            case "input_audio_buffer.speech_started":
                ClearPlaybackBuffer();   // 用户插话 → 立刻停掉正在播放的 AI 声音
                break;

            case "conversation.item.input_audio_transcription.completed":
                {
                    string text = (AsString(evt["transcript"]) ?? "").Trim();
                    if (text.Length > 0) Raise(UserTranscript, text);
                }
                break;

            case "conversation.item.input_audio_transcription.failed":
                AppLogger.Warn($"[实时通话] 语音转写失败: {evt["error"]?.ToJsonString()}");
                break;

            // 转写增量：beta 命名 response.audio_transcript.*，GA 命名 response.output_audio_transcript.*
            case "response.audio_transcript.delta":
            case "response.output_audio_transcript.delta":
            case "response.text.delta":
            case "response.output_text.delta":
                _assistantDelta.Append(AsString(evt["delta"]) ?? "");
                break;

            case "response.audio_transcript.done":
            case "response.output_audio_transcript.done":
            case "response.text.done":
            case "response.output_text.done":
                {
                    string text = (_assistantDelta.Length > 0
                        ? _assistantDelta.ToString()
                        : AsString(evt["transcript"]) ?? AsString(evt["text"]) ?? "").Trim();
                    _assistantDelta.Clear();
                    if (text.Length > 0) Raise(AssistantTranscript, text);
                }
                break;

            // 音频增量：beta 命名 response.audio.delta，GA 命名 response.output_audio.delta
            case "response.audio.delta":
            case "response.output_audio.delta":
                PlayAudioDelta(AsString(evt["delta"]) ?? "");
                break;

            case "response.done":
                // 兜底：服务端没发 transcript.done 时，用累计的 delta 收尾，避免消息丢失。
                if (_assistantDelta.Length > 0)
                {
                    string text = _assistantDelta.ToString().Trim();
                    _assistantDelta.Clear();
                    if (text.Length > 0) Raise(AssistantTranscript, text);
                }
                break;

            case "error":
                {
                    string detail = evt["error"]?.ToJsonString() ?? "(无详情)";
                    AppLogger.Warn($"[实时通话] 服务端错误: {detail}");
                    Report("实时语音服务报错：" + Truncate(detail, 160));
                }
                break;
        }
    }

    // ── 发送/工具 ─────────────────────────────────────────────────────────

    private async Task<bool> SendJsonAsync(JsonNode payload, CancellationToken token)
    {
        ClientWebSocket? socket;
        lock (_stateLock) socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return false;

        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        try
        {
            await _sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text,
                        endOfMessage: true, cancellationToken: token)
                    .ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            AppLogger.Warn($"[实时通话] 发送失败: {ex.Message}");
            return false;
        }
    }

    private CancellationToken CurrentToken()
    {
        try { return _cts?.Token ?? CancellationToken.None; }
        catch (ObjectDisposedException) { return CancellationToken.None; }
    }

    /// <summary>16bit 单声道 PCM 线性插值重采样（不引第三方库）。</summary>
    private static byte[] Resample16(byte[] source, int bytes, int fromRate, int toRate)
    {
        int inSamples = bytes / 2;
        if (inSamples <= 0 || fromRate <= 0 || toRate <= 0) return [];

        int outSamples = (int)Math.Round(inSamples * (double)toRate / fromRate);
        if (outSamples <= 0) return [];
        if (outSamples == inSamples) return source.AsSpan(0, bytes).ToArray();

        var result = new byte[outSamples * 2];
        double step = inSamples > 1 ? (double)(inSamples - 1) / Math.Max(1, outSamples - 1) : 0;
        for (int i = 0; i < outSamples; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            if (i0 >= inSamples) i0 = inSamples - 1;
            int i1 = Math.Min(i0 + 1, inSamples - 1);
            double frac = pos - i0;

            int s0 = source[i0 * 2] | (source[i0 * 2 + 1] << 8);
            int s1 = source[i1 * 2] | (source[i1 * 2 + 1] << 8);
            if (s0 > short.MaxValue) s0 -= 65536;   // 小端有符号还原
            if (s1 > short.MaxValue) s1 -= 65536;

            int value = (int)Math.Round(s0 + (s1 - s0) * frac);
            value = Math.Clamp(value, short.MinValue, short.MaxValue);
            result[i * 2] = (byte)(value & 0xFF);
            result[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return result;
    }

    private static string? AsString(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>上报状态；回调异常只记日志，绝不影响通话线程。</summary>
    private void Report(string message)
    {
        try { StatusChanged?.Invoke(message); }
        catch (Exception ex) { AppLogger.Warn($"[实时通话] 状态回调异常: {ex.Message}"); }
    }

    private void Raise(Action<string>? handler, string text)
    {
        try { handler?.Invoke(text); }
        catch (Exception ex) { AppLogger.Warn($"[实时通话] 转写回调异常: {ex.Message}"); }
    }
}
