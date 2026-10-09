using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;

namespace Hexa.Services;

/// <summary>生成参数。</summary>
/// <param name="AmplitudePercent">行程：占全行程的百分比（10–100）。100 = 0↔100 全行程。</param>
/// <param name="Sensitivity">0–2，越灵敏越容易判成事件（只影响本服务的事件筛选门限，不动检测器阈值）。</param>
/// <param name="MinGapMs">相邻动作的最小间隔（防抖）：两个事件挨得比它还近，就丢掉较弱的那个。</param>
/// <param name="MultiAxis">true = 额外生成一条 R0 扭转轨道。</param>
public sealed record AudioScriptOptions(
    double AmplitudePercent = 55,
    double Sensitivity     = 1.0,
    int    MinGapMs        = 90,
    bool   MultiAxis       = false);

/// <summary>生成结果。</summary>
/// <param name="SourcePath">源媒体文件路径。</param>
/// <param name="DurationMs">媒体时长（毫秒）。</param>
/// <param name="StrokeCount">生成了多少次「来回」。</param>
/// <param name="ImpactCount">检出的冲击数（筛选后、上限裁剪前）。</param>
/// <param name="BeatCount">检出的节拍数（筛选后、上限裁剪前）。</param>
/// <param name="SwellCount">检出的渐强数（筛选后、上限裁剪前）。</param>
/// <param name="VoiceCount">检出的人声（呻吟 / 喘息）数（筛选后、上限裁剪前）。</param>
/// <param name="MainTrack">L0 主轨道，按时间严格升序、同一毫秒只有一个。</param>
/// <param name="TwistTrack">R0 扭转轨道（MultiAxis 时才有；时间点与主轴一一对应）。</param>
/// <param name="Summary">给界面直接显示的一句话。</param>
public sealed record AudioScriptResult(
    string SourcePath,
    long   DurationMs,
    int    StrokeCount,
    int    ImpactCount,
    int    BeatCount,
    int    SwellCount,
    int    VoiceCount,
    IReadOnlyList<(long At, int Pos)> MainTrack,
    IReadOnlyList<(long At, int Pos)>? TwistTrack,
    string Summary);

/// <summary>
/// 离线「听歌/看片自动生成脚本」：把一个音频（或含音轨的视频）文件整段分析一遍，
/// 把 <see cref="AudioEventDetector"/> 检出的事件翻译成一串 funscript 动作。
///
/// 与实时版（<see cref="AudioReactiveService"/>）的关系：同一个检测器、同一套动作语汇，
/// 区别只在于这里可以「看完全片再决定」——
///   ① 有了完整事件表，才能按强度从弱到强丢事件来守住动作数上限；
///   ② 实时版必须立刻下发，离线版可以在事件之间补 rest，让每个来回都「有起落」。
///
/// 分三层，每层都能单独读懂：
///   ① 解码 <see cref="DecodeAndAnalyze"/>：AudioFileReader 优先（wav/mp3/aiff），失败或格式不认
///      （视频容器 mp4/m4a/wma/avi…）就换 MediaFoundationReader —— 用户因此可以直接选视频文件。
///      两条路都降混成单声道，按块喂给检测器，时间戳用「已读样本数 ÷ 采样率」保证精确。
///   ② 选事件 <see cref="SelectStrokes"/>：灵敏度门限 → 与上一个动作「挨太近就丢弱的」→ 动作数上限。
///   ③ 排版 <see cref="BuildTracks"/>：事件 → 有起落的动作点，补 rest，生成单轴/多轴轨道。
///
/// 线程模型：<see cref="GenerateAsync"/> 在工作线程上跑（解码是同步阻塞的），进度回调也在该线程上触发。
/// 稳态分配：块缓冲与事件缓冲各一份、循环复用；每个事件额外分配一个 stroke 对象（每秒最多几个，可忽略）。
/// </summary>
public static class AudioScriptGenerator
{
    // ── 解码 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 分析块长度（每声道样本数）＝ 采样率 ÷ 10，即约 100ms 一块：44.1kHz 下正好 4410。
    /// 取 100ms 的理由：检测器是 10ms 一帧，一块 10 帧 —— 够密（判「上升够快」不会漏细节），
    /// 又不会让取消检查与进度上报变得过于频繁。上下限只是防止极端采样率把块撑爆或切碎。
    /// </summary>
    private const int BlockDivisor = 10;
    private const int MinBlockSamples = 1024;
    private const int MaxBlockSamples = 8192;

    /// <summary>单块事件的预备缓冲：检测器单块上限只有 4，留 16 是给「调用方缓冲写满也不丢计数」留余地。</summary>
    private const int EventScratchSize = AudioEventDetector.MaxEventsPerBlock * 4;

    /// <summary>本服务能接受的媒体扩展名。只是为了在解码前给一句更清楚的中文提示，不是功能边界。</summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 音频
        ".wav", ".wave", ".mp3", ".aiff", ".aif", ".aifc",
        // 视频 / 容器（走 MediaFoundation：WMA 与 AAC 音频，mp4/m4a/wmv/avi 音轨）
        ".mp4", ".m4a", ".m4v", ".mov", ".aac", ".wma", ".asf", ".avi", ".wmv", ".mkv",
    };

    // ── 事件 → 动作 ─────────────────────────────────────────────────────

    /// <summary>Impulse 上升时间取事件时长的比例。</summary>
    private const double ImpactRiseRatio = 0.15;

    /// <summary>上升时间下限（毫秒）：再快就只是一个尖峰，机器跟不出「快进」的形。</summary>
    private const double ImpactMinRiseMs = 45.0;

    /// <summary>节拍峰值 = rest + 满行程 × 该比例（浅而准，不抢主冲击的风头）。</summary>
    private const double BeatPeakRatio = 0.70;

    /// <summary>多轴时 R0 的幅度相对主轴的比例。</summary>
    private const double TwistAmplitudeRatio = 0.35;

    /// <summary>R0 的静态偏置（点）：扭转发生在「偏离中位一点」的区间里，看起来才像拧而不是同时推拉。</summary>
    private const double TwistBiasPoints = 6.0;

    /// <summary>R0 的错相抖动（点）：±该值循环，形成相位错开；取 4 很小，不会盖过 35% 的主扭转。</summary>
    private const double TwistPhaseJitterPoints = 4.0;

    /// <summary>动作数上限：与 <see cref="WaveScriptCodec.MaxActions"/> 一致，导出的脚本一定还能被脚本库读回来。</summary>
    private const int MaxActions = WaveScriptCodec.MaxActions;

    /// <summary>
    /// 弱事件的行程下限（相对全局行程）：强度 0 的事件也走 35% 的行程 ——
    /// 再小就"看不出在动"，而完全不动等于把这个事件吞掉了。
    /// </summary>
    private const double StrokeStrengthFloor = 0.35;

    /// <summary>每个 stroke 固定 3 个动作点：起点(rest) → 峰值 → 终点(rest)。</summary>
    private const int ActionsPerStroke = 3;

    /// <summary>可以保留的最大事件数。检测器自身的最小间隔把事件率压得很低，这个数只是内存上界。</summary>
    private const int MaxEvents = 4000;

    /// <summary>进度上报的最小间隔：每块都报会白刷界面。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>离线分析音频/视频文件并生成动作。progress 是 0–1；支持取消。</summary>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    /// <exception cref="InvalidOperationException">
    /// 文件不存在 / 格式不支持 / 解码失败 / 时长不足 1 秒 / 没有任何事件 —— 消息是中文，可直接显示。
    /// </exception>
    public static Task<AudioScriptResult> GenerateAsync(
        string mediaPath, AudioScriptOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(mediaPath))
            throw new InvalidOperationException("请先选择一个音频或视频文件。");

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return Generate(mediaPath, options, progress, ct);
            }
            catch
            {
                // 取消的原因也可能是「内部抛了自己的异常」：只要令牌已经取消，对外就统一是
                // OperationCanceledException，调用方才有唯一的取消判据（不必去认底层异常类型）。
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw;
            }
        }, ct);
    }

    // ── 主流程 ──────────────────────────────────────────────────────────

    private static AudioScriptResult Generate(
        string mediaPath, AudioScriptOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        string fullPath = ResolvePath(mediaPath);
        EnsureSupportedExtension(fullPath);

        // 行程夹到 10–100：10 以下几乎看不出在动，100 以上会撞到机械行程两端。
        double amplitude = Math.Clamp(options.AmplitudePercent, 10.0, 100.0);
        // 间隔至少 1ms，否则「严格升序」这条不变量就没法维持。
        int minGapMs = Math.Max(1, options.MinGapMs);

        var stopwatch = Stopwatch.StartNew();
        Detected detections = DecodeAndAnalyze(fullPath, options.Sensitivity, progress, ct);

        if (detections.Events.Count == 0)
            throw new InvalidOperationException(
                "整段音频里没有检测到明显的事件（冲击 / 节拍 / 渐强 / 人声）。请确认音量不是过小，或把灵敏度调高一些。");

        // 先算「候选 stroke」，再走「选事件」，最后才排版 —— 上限裁剪会改变动作数，
        // 补 rest 又会反过来影响动作数，所以两者必须一起迭代（见 TrimToActionLimit）。
        List<Stroke> candidates = ToStrokes(detections.Events, amplitude);
        int droppedByConflict = 0;
        List<Stroke> kept = SelectStrokes(candidates, minGapMs, ref droppedByConflict);
        int droppedByLimit = TrimToActionLimit(kept, minGapMs);

        int impact = 0, beat = 0, swell = 0, voice = 0;
        foreach (Stroke stroke in kept)
        {
            switch (stroke.Kind)
            {
                case MotionEventKind.Impact: impact++; break;
                case MotionEventKind.Beat:   beat++;   break;
                case MotionEventKind.Swell:  swell++;  break;
                // 人声（呻吟）以前被算进"共 N 次来回"里，却不出现在分类统计中 —— 显示上是漏的。
                case MotionEventKind.Voice:  voice++;  break;
            }
        }

        long durationMs = Math.Max(1, (long)Math.Round(detections.DurationSeconds * 1000.0));
        BuildTracks(kept, minGapMs, amplitude, options.MultiAxis,
            out List<(long At, int Pos)> main, out List<(long At, int Pos)>? twist);

        int dropped = droppedByConflict + droppedByLimit;
        string summary = BuildSummary(durationMs, impact, beat, swell, voice, kept.Count,
            droppedByConflict, droppedByLimit, options.MultiAxis, stopwatch.Elapsed);

        progress?.Report(1.0);
        AppLogger.Info($"[音频脚本] {System.IO.Path.GetFileName(fullPath)}：{summary}（{stopwatch.Elapsed.TotalSeconds:0.0}s）");

        return new AudioScriptResult(
            fullPath, durationMs, kept.Count, impact, beat, swell, voice,
            main,
            options.MultiAxis ? twist : null,
            summary);

    }

    // ── ① 解码 + 分析 ───────────────────────────────────────────────────

    /// <summary>一次离线分析的解码产物。</summary>
    private readonly record struct Detected(List<MotionEvent> Events, double DurationSeconds);

    /// <summary>「时长不足」不是解码失败：不该触发 MediaFoundation 回退，也不该被包进「解码失败」提示里。</summary>
    private sealed class TooShortException(string message) : Exception(message);

    /// <summary>
    /// 打开文件、降混成单声道、按块喂给检测器，收集全部事件。
    ///
    /// 两条解码路径都只经过 <see cref="ISampleProvider"/>，所以后面的分析逻辑完全共用：
    ///   AudioFileReader：wav/mp3/aiff，本身实现 ISampleProvider；
    ///   MediaFoundationReader：mp4/m4a/wma/avi（含音轨的视频）—— 它是 WaveStream，
    ///   用 <c>ToSampleProvider()</c> 转一次，拿到的就是浮点采样源。
    /// </summary>
    private static Detected DecodeAndAnalyze(
        string fullPath, double sensitivity, IProgress<double>? progress, CancellationToken ct)
    {
        Exception? firstError = null;
        try
        {
            return AnalyzeWith(() => OpenAudioFileReader(fullPath), sensitivity, progress, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (TooShortException) { throw; }      // 时长问题是文件本身的性质，换解码器也一样
        catch (Exception ex) { firstError = ex; }

        ct.ThrowIfCancellationRequested();

        try
        {
            return AnalyzeWith(() => OpenMediaFoundationReader(fullPath), sensitivity, progress, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (TooShortException) { throw; }
        catch (Exception ex)
        {
            // 两句都留在中文提示里：用户看到「AudioFileReader 也失败了」才知道不是自己的文件坏了。
            throw new InvalidOperationException(
                $"无法解码这个文件：{Explain(firstError)}；换用系统解码器（Media Foundation）也失败：{Explain(ex)}", ex);
        }
    }

    private static Detected AnalyzeWith(
        Func<ISampleProvider> open, double sensitivity, IProgress<double>? progress, CancellationToken ct)
    {
        ISampleProvider? provider = null;
        try
        {
            provider = open();
            WaveFormat format = provider.WaveFormat;
            int sampleRate = format.SampleRate;
            if (sampleRate <= 0) throw new FormatException("采样率无效（0 Hz）。");

            int channels = Math.Max(1, format.Channels);
            double totalSeconds = TotalSecondsOf(provider);

            int framesPerBlock = Math.Clamp(sampleRate / BlockDivisor, MinBlockSamples, MaxBlockSamples);
            // 稳态不再分配：这两个缓冲在整段分析里反复使用。
            // 缓冲按「一帧 = 全部声道」申请，所以是 framesPerBlock × channels 个 float；降混就在检测器里按帧做。
            var blockBuffer = new float[framesPerBlock * channels];
            var scratch = new MotionEvent[EventScratchSize];

            var detector = new AudioEventDetector();
            detector.Reset();

            var events = new List<MotionEvent>(256);
            long readSamples = 0;                       // 已读「帧」数（每帧 = 一个采样点 × 全部声道）
            double reportedProgress = -1;
            var lastReport = TimeSpan.Zero;
            var clock = Stopwatch.StartNew();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // NAudio 3.x 的 ISampleProvider.Read 收 Span<float>（2.x 才是 (float[],int,int)）
                int read = provider.Read(blockBuffer.AsSpan(0, blockBuffer.Length));
                if (read <= 0) break;

                int frames = read / channels;
                if (frames <= 0) break;   // 连一帧都不满：再读下去也凑不齐，直接收尾（绝不 continue，否则会死循环）

                double blockStart = readSamples / (double)sampleRate;
                int detected = detector.Process(blockBuffer.AsSpan(0, frames * channels), sampleRate, blockStart, scratch);
                for (int i = 0; i < detected; i++)
                {
                    if (events.Count >= MaxEvents) break;   // 上限：后面的整段仍在跑，但不再记（见 MaxEvents）
                    events.Add(scratch[i]);
                }

                readSamples += frames;

                if (progress != null)
                {
                    double value = totalSeconds > 0
                        ? Math.Clamp(readSamples / (double)sampleRate / totalSeconds, 0, 1)
                        : 0;
                    if (value - reportedProgress >= 0.002 || clock.Elapsed - lastReport >= ProgressInterval)
                    {
                        reportedProgress = value;
                        lastReport = clock.Elapsed;
                        progress.Report(value);
                    }
                }
            }

            if (readSamples <= 0) throw new FormatException("文件里没有可读的音频采样。");

            double durationSeconds = readSamples / (double)sampleRate;
            if (durationSeconds < 1.0) throw new TooShortException("音频时长不足 1 秒，生成不出有意义的脚本。");

            FilterBySensitivity(events, sensitivity);
            return new Detected(events, durationSeconds);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    /// <summary>wav/mp3/aiff 直读。本身实现 ISampleProvider，WaveFormat 已经是浮点格式。</summary>
    private static ISampleProvider OpenAudioFileReader(string fullPath)
    {
        var reader = new AudioFileReader(fullPath);   // 打开失败会在这里抛，调用方据此换 MediaFoundation
        try
        {
            if (reader.WaveFormat.SampleRate <= 0) throw new FormatException("采样率无效（0 Hz）。");
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>mp4/m4a/wma/avi 等（含音轨的视频）。MediaFoundationReader 是 WaveStream，转一次就是浮点采样源。</summary>
    private static ISampleProvider OpenMediaFoundationReader(string fullPath)
    {
        var reader = new MediaFoundationReader(fullPath);
        try
        {
            if (reader.WaveFormat.SampleRate <= 0) throw new FormatException("采样率无效（0 Hz）。");
            return reader.ToSampleProvider();
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>能提前拿到的媒体总时长（秒）；拿不到就返回 0（进度条退化成「已读了多少」）。</summary>
    private static double TotalSecondsOf(ISampleProvider provider) => provider switch
    {
        AudioFileReader file => file.TotalTime.TotalSeconds,
        WaveStream stream when stream.WaveFormat.AverageBytesPerSecond > 0 =>
            stream.Length / (double)stream.WaveFormat.AverageBytesPerSecond,
        _ => 0,
    };

    /// <summary>
    /// 灵敏度门限：越灵敏留下的弱事件越多。**只筛本服务收集到的事件，不动检测器自己的阈值**
    /// —— 检测器是实时版共用的，改它会连带改变实时响应的手感。
    /// </summary>
    private static void FilterBySensitivity(List<MotionEvent> events, double sensitivity)
    {
        double threshold = 0.5 - 0.25 * Math.Clamp(sensitivity, 0.0, 2.0);   // 0 → 0.50；1 → 0.25；2 → 0.00
        events.RemoveAll(item => item.Strength < threshold);
    }

    // ── ② 选事件 ────────────────────────────────────────────────────────

    /// <summary>一个事件 → 一次「来回」的排版结果。位置已经算好，只等时间落点。</summary>
    private struct Stroke
    {
        /// <summary>事件种类。</summary>
        public MotionEventKind Kind;

        /// <summary>事件的开始时刻（毫秒）。</summary>
        public long StartMs;

        /// <summary>事件的持续时长（毫秒）。</summary>
        public long DurationMs;

        /// <summary>事件强度 0–1（冲突与上限裁剪时按它从弱到强丢）。</summary>
        public double Strength;

        /// <summary>静止位（= 50 − 半行程）。</summary>
        public int Rest;

        /// <summary>峰值位（= 50 + 半行程）。</summary>
        public int Peak;
    }

    /// <summary>
    /// 事件 → 候选 stroke：算好各自的静止位 / 峰值位与时间落点（不做增删）。
    ///
    /// **行程按各自的强度缩放**：以前 rest / peak 在循环**外**由全局 AmplitudePercent 算一次，
    /// 于是每个事件的行程完全一样 —— 输出是等幅抽插串，没有强弱、也没有段落起伏。
    /// 现在每个事件用自己的 strength 折算行程，全局 AmplitudePercent 仍然当**上限 / 基准**
    ///（缩放系数 ∈ [0.35, 1]，所以永远不会超过用户设的行程）。
    /// </summary>
    private static List<Stroke> ToStrokes(List<MotionEvent> events, double amplitude)
    {
        double durationScale = Math.Clamp(amplitude / 55.0, 0.6, 1.6);

        var result = new List<Stroke>(events.Count);
        foreach (MotionEvent item in events)
        {
            double strength = Math.Clamp(item.Strength, 0, 1);
            double seconds = MotionVocabulary.DurationSeconds(item.Kind, strength);
            if (!(seconds > 0)) seconds = 0.3;                      // None 之类：给个保守时长，不至于生成 0ms 的 stroke

            // 行程 = 全局行程 × （下限 + (1 − 下限) × 强度）：强度 0 → 35%，强度 1 → 100%。
            // 于是"响的那一下插得深、轻的那一下插得浅"，段落起伏自然就有了。
            double half = amplitude / 2.0 * (StrokeStrengthFloor + (1.0 - StrokeStrengthFloor) * strength);

            long durationMs = Math.Max(1, (long)Math.Round(seconds * 1000.0 * durationScale));
            long startMs = Math.Max(0, (long)Math.Round(item.AtSeconds * 1000.0));
            result.Add(new Stroke
            {
                Kind = item.Kind,
                StartMs = startMs,
                DurationMs = durationMs,
                Strength = strength,
                Rest = Clamp(50.0 - half),
                Peak = Clamp(50.0 + half),
            });
        }
        return result;
    }

    /// <summary>
    /// 挑出真正要下发的 stroke：走一遍时间轴，凡是「与上一次动作挨得太近」的冲突，
    /// 都丢掉较弱的那一个（而不是把时间点截断 —— 截断会把动作挪到不该发生的位置上）。
    ///
    /// 冲突判据：下一个 stroke 的起点早于「上一个 stroke 的终点 + MinGapMs」。
    /// 说明：stroke 内部三个动作点（起/峰/终）之间的间隔由事件时长决定，短事件可能小于 MinGapMs，
    /// 这是「一次来回」本身的形状，必须保留；MinGapMs 只用来管**两个事件之间**的防抖。
    /// </summary>
    private static List<Stroke> SelectStrokes(List<Stroke> events, int minGapMs, ref int dropped)
    {
        var kept = new List<Stroke>(Math.Min(events.Count, MaxActions / ActionsPerStroke));
        foreach (Stroke next in events)
        {
            while (kept.Count > 0 && Conflicts(kept[^1], next, minGapMs))
            {
                Stroke previous = kept[^1];
                if (next.Strength <= previous.Strength)
                {
                    // 后来的更弱：直接丢它。`next` 是 foreach 的副本，跳过不需要任何清理。
                    dropped++;
                    goto NextEvent;
                }
                // 后来的更强：先撤掉上一个，再跟再上一个比 —— 强事件值得占掉它前面那个弱事件的位置。
                kept.RemoveAt(kept.Count - 1);
                dropped++;
            }
            kept.Add(next);
        NextEvent:;
        }
        return kept;
    }

    /// <summary>两个 stroke 是否挨得太近（下一个的起点早于上一个的终点 + 最小间隔）。</summary>
    private static bool Conflicts(in Stroke previous, in Stroke next, int minGapMs) =>
        next.StartMs < previous.StartMs + previous.DurationMs + minGapMs;

    /// <summary>
    /// 动作数上限：超了就按强度从弱到强丢事件，直到放得下。返回丢掉的个数。
    ///
    /// 为什么是「丢事件」而不是「抽稀动作点」：抽稀会把某一次来回的起点或终点抽掉，
    /// 机器就会停在半路上等下一个点 —— 听感上比少做一个来回糟得多。丢就整个丢掉，剩下的来回都完整。
    /// </summary>
    private static int TrimToActionLimit(List<Stroke> strokes, int minGapMs)
    {
        int dropped = 0;
        while (strokes.Count > 0 && ActionCount(strokes, minGapMs) > MaxActions)
        {
            int weakest = IndexOfWeakest(strokes);
            if (weakest < 0) break;
            strokes.RemoveAt(weakest);
            dropped++;
        }
        return dropped;
    }

    /// <summary>
    /// 估算排版后的动作数：每个 stroke 固定 3 个，另外「上一次动作结束时不在 rest」就还要补一个 rest。
    /// 估算必须和 <see cref="BuildTracks"/> 用同一套规则，否则上限会算错。
    /// </summary>
    private static int ActionCount(List<Stroke> strokes, int minGapMs)
    {
        int total = 0;
        int previousPosition = -1;   // −1 = 还没有过动作
        foreach (Stroke stroke in strokes)
        {
            if (previousPosition >= 0 && previousPosition != stroke.Rest) total++;
            total += ActionsPerStroke;
            previousPosition = stroke.Rest;   // 每个 stroke 都收在 rest
        }
        return total;
    }

    /// <summary>最弱的那个 stroke；同强度取时间靠后的（先丢晚发生的，保住开头的印象）。</summary>
    private static int IndexOfWeakest(List<Stroke> strokes)
    {
        int index = -1;
        double weakest = double.MaxValue;
        for (int i = 0; i < strokes.Count; i++)
        {
            if (strokes[i].Strength <= weakest)
            {
                weakest = strokes[i].Strength;
                index = i;
            }
        }
        return index;
    }

    // ── ③ 排版：事件 → 动作点 ────────────────────────────────────────────

    /// <summary>
    /// 把 stroke 排成两条轨道。每个 stroke 都是「有起落」的一个来回：
    ///   Impact：快进快出 —— rest →（15% 时长，不小于 45ms）→ peak →（结束）→ rest
    ///   Beat：rest →（中点）peak×0.7 →（结束）→ rest
    ///   Swell：慢而连贯 —— rest →（中点）peak →（结束）→ rest
    /// 事件之间只补 rest，不自己造动作：安静就该停住。
    /// </summary>
    private static void BuildTracks(
        List<Stroke> strokes, int minGapMs, double amplitude, bool multiAxis,
        out List<(long At, int Pos)> main,
        out List<(long At, int Pos)>? twist)
    {
        main = new List<(long At, int Pos)>(strokes.Count * ActionsPerStroke + 8);
        int previousPosition = -1;

        foreach (Stroke stroke in strokes)
        {
            // 上一个动作停在非 rest 上（理论上不会：每个 stroke 都收在 rest），先回到 rest 再开始下一个来回。
            if (previousPosition >= 0 && previousPosition != stroke.Rest)
            {
                long restAt = stroke.StartMs - Math.Max(1, minGapMs);
                Add(main, restAt, stroke.Rest);
                previousPosition = stroke.Rest;
            }

            (long At, int Pos)[] points = StrokeActions(stroke);
            foreach ((long at, int pos) in points) Add(main, at, pos);
            previousPosition = points[^1].Pos;
        }

        twist = null;
        if (!multiAxis) return;

        // R0 与主轴共用同一批时间点（多轴 funscript 的惯例，也让两轴天然同步），
        // 但幅度只有主轴的 35%、并叠加一个「偏离中位 6 点 + ±4 点错相」的偏置：
        // 扭转发生在偏离中位的区间里，看起来才像拧，而不是两条轴同时在推拉。
        twist = new List<(long At, int Pos)>(main.Count);
        for (int i = 0; i < main.Count; i++)
        {
            double scaled = 50.0 + (main[i].Pos - 50.0) * TwistAmplitudeRatio;
            double jitter = TwistPhaseJitterPoints * (i % 3 - 1);   // i = 0,1,2,3… → 0, +4, −4, 0, +4, −4…
            Add(twist, main[i].At, Clamp(scaled + TwistBiasPoints + jitter));
        }
    }

    /// <summary>一个 stroke 的 3 个动作点，时间落在 stroke 内部的 0% / 峰值相位 / 100%。</summary>
    private static (long At, int Pos)[] StrokeActions(in Stroke stroke)
    {
        switch (stroke.Kind)
        {
            case MotionEventKind.Impact:
            {
                // 快进快出：上升只用 15% 的时长（但不小于 45ms，太尖的形机器跟不出来），
                // 剩下 85% 走完回落 —— 有 attack 也有余韵，跟 MotionVocabulary 的口味一致。
                long rise = Math.Max((long)Math.Round(stroke.DurationMs * ImpactRiseRatio), (long)ImpactMinRiseMs);
                rise = Math.Min(rise, stroke.DurationMs);
                return
                [
                    (stroke.StartMs, stroke.Rest),
                    (stroke.StartMs + rise, stroke.Peak),
                    (stroke.StartMs + stroke.DurationMs, stroke.Rest),
                ];
            }

            case MotionEventKind.Beat:
            {
                // 节拍浅而准：峰值只有满行程的 70%，中点出峰，方便连上下一拍。
                int beatPeak = Clamp(stroke.Rest + (stroke.Peak - stroke.Rest) * BeatPeakRatio);
                long at = stroke.StartMs + stroke.DurationMs / 2;
                return
                [
                    (stroke.StartMs, stroke.Rest),
                    (at, beatPeak),
                    (stroke.StartMs + stroke.DurationMs, stroke.Rest),
                ];
            }

            default:
            {
                // 渐强：慢而连贯的一个来回，中点出峰。
                long at = stroke.StartMs + stroke.DurationMs / 2;
                return
                [
                    (stroke.StartMs, stroke.Rest),
                    (at, stroke.Peak),
                    (stroke.StartMs + stroke.DurationMs, stroke.Rest),
                ];
            }
        }
    }

    /// <summary>
    /// 写入一个动作点：时间取整到毫秒、位置夹 0–100，并保证时间**严格升序**
    /// （与 <see cref="WaveScriptCodec.ParseActions"/> 的「同一毫秒只留最后一个」语义一致：
    /// 这里干脆不产生同一毫秒上的第二个点，时间不递增就顺延 1ms —— 挪的是 1ms，不是动作本身）。
    /// </summary>
    private static void Add(List<(long At, int Pos)> track, long atMs, int pos)
    {
        long at = Math.Max(0, atMs);
        if (track.Count > 0)
        {
            long previous = track[^1].At;
            if (at <= previous) at = previous + 1;
        }
        track.Add((at, Clamp(pos)));
    }

    private static int Clamp(double position) => (int)Math.Round(Math.Clamp(position, 0.0, 100.0));

    // ── 序列化 ──────────────────────────────────────────────────────────

    /// <summary>funscript 动作点（字段名固定是 at / pos，不能跟着 naming policy 变）。</summary>
    private sealed record FunscriptAction(
        [property: JsonPropertyName("at")]  long At,
        [property: JsonPropertyName("pos")] int  Pos);

    private sealed record FunscriptAxis(
        [property: JsonPropertyName("id")]      string       Id,
        [property: JsonPropertyName("actions")] List<FunscriptAction> Actions);

    /// <summary>
    /// 根节点：<c>actions</c> = L0，多轴时 <c>axes</c> 里再放 R0。
    /// 这正是 <see cref="WaveScriptCodec.ParseFunscriptTracks"/> 认的结构，也是
    /// <see cref="ScriptComposerService"/> 导出时用的结构，所以生成的脚本能直接被脚本库 / 播放器接受。
    /// </summary>
    private sealed record FunscriptDocument(
        [property: JsonPropertyName("actions")]  List<FunscriptAction>  Actions,
        [property: JsonPropertyName("axes")]     List<FunscriptAxis>?   Axes,
        [property: JsonPropertyName("source")]   string?                Source,
        [property: JsonPropertyName("duration")] long                   Duration,
        [property: JsonPropertyName("inverted")] bool                   Inverted,
        [property: JsonPropertyName("version")]  string?                Version);

    /// <summary>把结果序列化成标准 funscript JSON（单轴只有 actions；多轴带 axes 数组）。</summary>
    public static string ToFunscriptJson(AudioScriptResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // 导出前再收一次：时间升序去重、位置夹 0–100。解析端「同一毫秒后写覆盖前写」，
        // 这里是同一套语义的写入侧，保证自己写出去的脚本一定还能被 ParseFunscriptTracks 读回来。
        List<FunscriptAction> main = ToActions(result.MainTrack);
        if (main.Count < 2)
            throw new InvalidOperationException("动作点不足两个不同时间点，无法生成脚本。");

        List<FunscriptAxis>? axes = null;
        if (result.TwistTrack is { Count: > 0 } twistSource)
        {
            List<FunscriptAction> twist = ToActions(twistSource);
            if (twist.Count >= 2) axes = [new FunscriptAxis("R0", twist)];
        }

        var document = new FunscriptDocument(
            main,
            axes,
            string.IsNullOrWhiteSpace(result.SourcePath) ? null : System.IO.Path.GetFileName(result.SourcePath),
            Math.Max(0, result.DurationMs),
            // 位置本来就是标准朝向（rest 在下、peak 在上），显式写 false，
            // 免得导入时被 FunscriptTrackLoader 当成需要上下颠倒的脚本。
            Inverted: false,
            "1.0");

        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            // 已知字段的 JsonPropertyName 优先，所以 at/pos/id/actions 不会被改写成 atMs/posPercent 之类；
            // 这个策略只用于将来加字段时的默认风格。
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static List<FunscriptAction> ToActions(IReadOnlyList<(long At, int Pos)> source)
    {
        var ordered = new SortedDictionary<long, int>();
        foreach ((long at, int pos) in source)
            ordered[Math.Clamp(at, 0, WaveScriptCodec.MaxDurationMs)] = Math.Clamp(pos, 0, 100);

        var actions = new List<FunscriptAction>(ordered.Count);
        foreach ((long at, int pos) in ordered) actions.Add(new FunscriptAction(at, pos));
        return actions;
    }

    // ── 文案与小工具 ────────────────────────────────────────────────────

    /// <summary>给界面直接显示的一句话：时长 · 四类事件数 · 动作数，必要时补上限/防抖/多轴说明。</summary>
    private static string BuildSummary(
        long durationMs, int impact, int beat, int swell, int voice,
        int strokes, int droppedByConflict, int droppedByLimit, bool multiAxis, TimeSpan elapsed)
    {
        var parts = new List<string>(7)
        {
            FormatDuration(durationMs),
            $"冲击 {impact} / 节拍 {beat} / 渐强 {swell} / 人声 {voice}",
            $"共 {strokes * ActionsPerStroke} 个动作（{strokes} 次来回）",
        };
        if (droppedByLimit > 0) parts.Add($"因上限丢弃了 {droppedByLimit} 个弱事件");
        if (droppedByConflict > 0) parts.Add($"因间隔过近丢弃了 {droppedByConflict} 个弱事件");
        if (multiAxis) parts.Add("含 R0 扭转轴");
        parts.Add($"耗时 {elapsed.TotalSeconds:0.0} 秒");
        return string.Join(" · ", parts);
    }

    /// <summary>「3 分 12 秒」这种给人看的时长。</summary>
    private static string FormatDuration(long durationMs)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, durationMs));
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours} 小时 {time.Minutes} 分 {time.Seconds} 秒";
        if (time.TotalMinutes >= 1)
            return $"{time.Minutes} 分 {time.Seconds} 秒";
        return $"{time.TotalSeconds:0.0} 秒";
    }

    private static string ResolvePath(string mediaPath)
    {
        try { return System.IO.Path.GetFullPath(mediaPath.Trim().Trim('"')); }
        catch (Exception ex) { throw new InvalidOperationException($"这个路径不可用：{ex.Message}", ex); }
    }

    private static void EnsureSupportedExtension(string fullPath)
    {
        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"找不到文件：{fullPath}");
        string extension = System.IO.Path.GetExtension(fullPath);
        if (extension.Length == 0 || !SupportedExtensions.Contains(extension))
            throw new InvalidOperationException(
                $"不支持这种文件类型（{extension}）。请选音频（wav / mp3 / aiff）或含音轨的视频（mp4 / m4a / wma / avi …）。");
    }

    /// <summary>把底层异常翻成一句能看的中文（界面会直接显示）。</summary>
    private static string Explain(Exception? ex) => ex switch
    {
        null => "原因未知",
        InvalidOperationException => ex.Message,
        FormatException => ex.Message,
        UnauthorizedAccessException => "没有读取权限",
        FileNotFoundException => "文件不存在",
        IOException io => "读取失败：" + io.Message,
        _ => ex.GetType().Name + "：" + ex.Message,
    };
}
