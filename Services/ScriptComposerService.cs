using System.Text.Json;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 动作序列合成器：把 <see cref="ScriptComposition"/>（有序动作段）按时间轴采样成六轴波形，
/// 导出标准多轴 funscript，并给 UI 提供播放时的逐帧采样与总时长估算。
///
/// 采样口径与 MotionEngine.StrokeTick 完全一致（同一个 SampleTempestAxis 公式），
/// 所以「编排预览听到的」与「设备实际跑出来的」是同一条曲线。
/// </summary>
public static class ScriptComposerService
{
    /// <summary>时间轴 1 秒对应的像素宽度（与 EditorPage 的卡片宽度一致）。</summary>
    public const double PixelsPerSecond = 40.0;

    /// <summary>单段卡片最小宽度（秒数很短的段也要能点得到）。</summary>
    public const double MinSegmentWidth = 80.0;

    /// <summary>每段至少采样这么多点（保证短段也有足够的时间分辨率）。</summary>
    public const int MinSamplesPerSegment = 200;

    /// <summary>采样步长上限（秒）：一段越长，点数越多，但不会粗于 100ms。</summary>
    public const double MaxSampleStepSeconds = 0.1;

    /// <summary>BPM 合法区间。</summary>
    public const int MinBpm = 10;
    public const int MaxBpm = 240;

    // ══════════════════════════════════════════════════════════════
    //  基础换算
    // ══════════════════════════════════════════════════════════════

    /// <summary>一个 Tempest 循环占用的秒数：bpm=60 → 1 圈/秒，bpm=120 → 2 圈/秒。</summary>
    public static double CycleSeconds(int bpm)
    {
        int safe = Math.Clamp(bpm, MinBpm, MaxBpm);
        return 60.0 / safe;
    }

    /// <summary>时长 → 卡片像素宽度（40px/秒，最小 80px）。</summary>
    public static double SegmentWidth(double durationSeconds) =>
        Math.Max(MinSegmentWidth, Math.Max(0, durationSeconds) * PixelsPerSecond);

    /// <summary>像素宽度 → 时长（拖动右边缘改时长时用）。</summary>
    public static double DurationFromPixels(double pixels) =>
        Math.Clamp(pixels / PixelsPerSecond, ScriptSegment.MinDurationSeconds, ScriptSegment.MaxDurationSeconds);

    /// <summary>按 Id 找动作库里的预设（不区分大小写）；找不到返回 null。</summary>
    public static StrokePreset? FindPreset(IReadOnlyList<StrokePreset>? presets, string? presetId)
    {
        if (presets is null || string.IsNullOrWhiteSpace(presetId)) return null;
        for (int i = 0; i < presets.Count; i++)
        {
            StrokePreset preset = presets[i];
            if (preset != null && string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase))
                return preset;
        }
        return null;
    }

    /// <summary>时间点落在第几段（越界夹到首/末段；空合成返回 -1）。</summary>
    public static int SegmentIndexAt(ScriptComposition? composition, double seconds)
    {
        if (composition?.Segments is not { Count: > 0 } segments) return -1;
        double cursor = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            double end = cursor + Math.Max(0, segments[i].DurationSeconds);
            if (seconds < end || i == segments.Count - 1) return i;
            cursor = end;
        }
        return segments.Count - 1;
    }

    // ══════════════════════════════════════════════════════════════
    //  估算（UI 显示总时长 / 可导出段数）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 估算合成规模。
    /// segments：能在动作库里解析到对应动作的段数（导出时真正会写进 funscript 的段）；
    /// totalSeconds：全部段的时长合计（含解析不到动作的段，便于用户发现「漏动作」）。
    /// bpm 只影响采样密度里的循环数，不改变总时长。
    /// </summary>
    public static (int segments, double totalSeconds) Estimate(
        ScriptComposition? composition,
        IReadOnlyList<StrokePreset>? presets,
        int bpm = 60)
    {
        if (composition?.Segments is not { Count: > 0 } segments) return (0, 0);

        int resolvable = 0;
        double total = 0;
        foreach (ScriptSegment segment in segments)
        {
            if (segment is null) continue;
            double duration = Math.Clamp(
                segment.DurationSeconds, ScriptSegment.MinDurationSeconds, ScriptSegment.MaxDurationSeconds);
            total += duration;
            if (FindPreset(presets, segment.PresetId) != null) resolvable++;
        }
        return (resolvable, total);
    }

    /// <summary>不关心动作库时的便捷重载。</summary>
    public static (int segments, double totalSeconds) Estimate(ScriptComposition? composition) =>
        Estimate(composition, null);

    // ══════════════════════════════════════════════════════════════
    //  实时采样（播放 / 预览）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 在合成时间轴上取某一时刻的六轴值（0..100）。合成里没有可解析的段时返回全 50（中位）。
    /// </summary>
    public static double[] SampleComposition(
        ScriptComposition? composition,
        IReadOnlyList<StrokePreset>? presets,
        double seconds,
        int bpm = 60)
    {
        var output = new double[6];
        Array.Fill(output, 50.0);
        if (composition?.Segments is not { Count: > 0 } segments) return output;

        int index = SegmentIndexAt(composition, seconds);
        if (index < 0) return output;

        double start = 0;
        for (int i = 0; i < index; i++) start += Math.Max(0, segments[i].DurationSeconds);
        ScriptSegment segment = segments[index];
        double local = Math.Max(0, seconds - start);

        double cycleSeconds = CycleSeconds(bpm);
        StrokePreset? preset = FindPreset(presets, segment.PresetId);
        double[] value = SampleSegment(preset, segment, local, cycleSeconds);

        // 段间过渡：本段开头 TransitionSeconds 内，从上一段末值线性过渡到本段自然曲线
        double blend = Math.Clamp(
            segment.TransitionSeconds, ScriptSegment.MinTransitionSeconds, ScriptSegment.MaxTransitionSeconds);
        if (blend > 0 && index > 0 && local < blend)
        {
            ScriptSegment previous = segments[index - 1];
            StrokePreset? previousPreset = FindPreset(presets, previous.PresetId);
            // 上一段在动作库里已经不存在时不做过渡：与 ComposeFunscript 的导出行为保持一致
            // （导出那边解析不到动作会跳过该段并把 previousEnd 置空），否则预览与导出会不一致。
            if (previousPreset is null) return value;
            double[] previousEnd = SampleSegment(
                previousPreset, previous, previous.DurationSeconds, cycleSeconds);
            double factor = Math.Clamp(local / blend, 0, 1);
            for (int i = 0; i < output.Length; i++)
                output[i] = previousEnd[i] + (value[i] - previousEnd[i]) * factor;
            return output;
        }
        return value;
    }

    /// <summary>
    /// 单个段在「段内第 localSeconds 秒」处的六轴值（0..100），不含段间过渡。
    /// 与 MotionEngine.StrokeTick 同公式：角度 = 段内时间 / 循环时长 × 2π，强度乘到振幅上。
    /// </summary>
    public static double[] SampleSegment(
        StrokePreset? preset,
        ScriptSegment segment,
        double localSeconds,
        double cycleSeconds)
    {
        var values = new double[6];
        if (preset is null || segment is null)
        {
            Array.Fill(values, 50.0);
            return values;
        }

        double duration = Math.Max(1e-6, segment.DurationSeconds);
        double local = Math.Clamp(localSeconds, 0, duration);
        double safeCycle = Math.Max(1e-6, cycleSeconds);
        double angle = local / safeCycle * Math.Tau;
        double intensity = Math.Clamp(
            segment.Intensity, ScriptSegment.MinIntensity, ScriptSegment.MaxIntensity);
        long cycleIndex = (long)Math.Floor(local / safeCycle);

        double[][] axes = preset.AllAxes;
        for (int i = 0; i < values.Length && i < axes.Length; i++)
        {
            double[] axis = axes[i];
            double sampled = MotionEngine.SampleTempestAxis(
                axis.Length > 0 ? axis[0] : 0.5,
                axis.Length > 1 ? axis[1] : 0.5,
                axis.Length > 2 ? axis[2] : 0,
                axis.Length > 3 ? axis[3] : 0,
                preset.MotionOf(i),
                angle,
                intensity,
                axis.Length > 4 ? axis[4] : 0,
                axis.Length > 5 ? axis[5] : 0,
                i,
                cycleIndex);
            values[i] = Math.Clamp(sampled * 100, 0, 100);
        }
        return values;
    }

    // ══════════════════════════════════════════════════════════════
    //  导出 funscript
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 把合成导出成标准多轴 funscript JSON：
    /// <code>
    /// {
    ///   "actions": [ { "at": 0, "pos": 50 }, ... ],              // L0 主轨道（单轴播放器兼容）
    ///   "axes":    [ { "id": "L0", "actions": [...] }, ... ]     // L0..R2 六轴
    /// }
    /// </code>
    /// 与 WaveScriptCodec.ParseFunscriptTracks 的读取格式严格对齐（camelCase 的 at / pos）。
    /// </summary>
    /// <param name="bpm">每个 Tempest 循环的速度，60 = 1 圈/秒（只改速度，不改总时长）。</param>
    /// <exception cref="InvalidOperationException">合成为空 / 总时长超上限 / 没有任何可解析的动作。</exception>
    public static string ComposeFunscript(
        ScriptComposition composition,
        IReadOnlyList<StrokePreset> presets,
        int bpm = 60)
    {
        ArgumentNullException.ThrowIfNull(composition);
        if (composition.Segments is not { Count: > 0 } segments)
            throw new InvalidOperationException("合成为空，请先从动作库拖入至少一个动作段。");

        double cycleSeconds = CycleSeconds(bpm);
        double totalSeconds = segments.Sum(segment => segment.DurationSeconds);
        double maxSeconds = WaveScriptCodec.MaxDurationMs / 1000.0;
        if (totalSeconds > maxSeconds)
            throw new InvalidOperationException(
                $"合成总时长 {totalSeconds:0.##}s 超过 funscript 上限 {maxSeconds:0}s，请缩短某些段。");
        if (totalSeconds <= 0)
            throw new InvalidOperationException("合成总时长为 0，无法导出。");

        // 每个轴一张「时间 → 位置」表；同一毫秒后写覆盖，与解析端行为一致
        string[] axisIds = Osr6DeviceProfile.InstalledAxes;
        var tracks = new SortedDictionary<long, int>[axisIds.Length];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = new SortedDictionary<long, int>();

        double cursor = 0;
        double[]? previousEnd = null;
        int exportedSegments = 0;

        foreach (ScriptSegment segment in segments)
        {
            if (segment is null) continue;
            double duration = Math.Clamp(
                segment.DurationSeconds, ScriptSegment.MinDurationSeconds, ScriptSegment.MaxDurationSeconds);
            StrokePreset? preset = FindPreset(presets, segment.PresetId);
            if (preset is null)
            {
                // 动作库里已经没有这个 Id：跳过该段（保持时间轴连续，避免整体错位）
                cursor += duration;
                previousEnd = null;
                continue;
            }

            int steps = SampleCount(duration);
            double step = duration / steps;
            double blend = Math.Clamp(
                segment.TransitionSeconds, ScriptSegment.MinTransitionSeconds, ScriptSegment.MaxTransitionSeconds);

            for (int k = 0; k <= steps; k++)
            {
                double local = Math.Min(duration, k * step);
                double[] value = SampleSegment(preset, segment, local, cycleSeconds);

                // 段间过渡：本段开头 blend 秒内，从上一段末值线性过渡到本段自然曲线
                if (previousEnd != null && blend > 0 && local < blend)
                {
                    double factor = Math.Clamp(local / blend, 0, 1);
                    for (int i = 0; i < value.Length; i++)
                        value[i] = previousEnd[i] + (value[i] - previousEnd[i]) * factor;
                }

                long at = (long)Math.Round((cursor + local) * 1000.0);
                for (int i = 0; i < value.Length && i < tracks.Length; i++)
                    tracks[i][at] = (int)Math.Round(Math.Clamp(value[i], 0, 100));
            }

            previousEnd = SampleSegment(preset, segment, duration, cycleSeconds);
            cursor += duration;
            exportedSegments++;
        }

        if (exportedSegments == 0)
            throw new InvalidOperationException("合成里的动作在动作库中都不存在，无法导出。");

        var axes = new List<object>(axisIds.Length);
        for (int i = 0; i < axisIds.Length; i++)
            axes.Add(new { id = axisIds[i], actions = BuildActions(tracks[i]) });

        var payload = new
        {
            // L0 主轨道同时放在根上，兼容只读 actions 的单轴播放器
            actions = BuildActions(tracks[0]),
            axes,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>单段采样点数：max(200, 时长 / 100ms) —— 即步长 ≤ 100ms，且至少 200 点。</summary>
    public static int SampleCount(double durationSeconds) =>
        Math.Max(MinSamplesPerSegment,
            (int)Math.Ceiling(Math.Max(0, durationSeconds) / MaxSampleStepSeconds));

    private static List<object> BuildActions(SortedDictionary<long, int> byTime)
    {
        KeyValuePair<long, int>[] points = byTime.ToArray();
        if (points.Length > WaveScriptCodec.MaxActions)
        {
            var picked = new List<KeyValuePair<long, int>>(WaveScriptCodec.MaxActions) { points[0] };
            double stride = (points.Length - 1.0) / (WaveScriptCodec.MaxActions - 1);
            for (int i = 1; i < WaveScriptCodec.MaxActions - 1; i++)
                picked.Add(points[(int)Math.Round(i * stride)]);
            picked.Add(points[^1]);
            points = picked.DistinctBy(point => point.Key).ToArray();
        }

        var actions = new List<object>(points.Length);
        foreach (KeyValuePair<long, int> point in points)
            actions.Add(new { at = point.Key, pos = point.Value });
        return actions;
    }
}
