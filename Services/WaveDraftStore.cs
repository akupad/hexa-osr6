using System.Text.Json;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>波形草稿：六轴控制点 + 一圈时长 + 轴启用状态 + 平滑插值开关。</summary>
/// <param name="AxisEnabled">
/// 六轴启用状态，顺序与 <see cref="Osr6DeviceProfile.InstalledAxes"/> / MotionEngine.Axes /
/// EditorPage.AxIds 一致（L0 L1 L2 R0 R1 R2），长度固定 6。
/// </param>
/// <param name="CycleLen">一圈时长（秒）。</param>
/// <param name="UseSmooth">是否用平滑（贝塞尔）插值采样手画波形。</param>
/// <param name="Waves">各轴控制点，画布坐标（X 0..CanvasWidth，Y 0..CanvasHeight）。</param>
/// <param name="Sources">
/// 各轴的「这份画布数据是从哪份 funscript 导入的」（见 <see cref="WaveSource"/>）。
/// 可空且带默认值：老调用点（App.OnExit / 单元测试）只传前 4 个参数，照样编译。
/// </param>
public sealed record WaveDraft(
    bool[] AxisEnabled,
    double CycleLen,
    bool UseSmooth,
    Dictionary<string, List<(double X, double Y)>> Waves,
    Dictionary<string, WaveSource>? Sources = null);

/// <summary>
/// 一份导入来的 funscript 的原始数据，用来把「导入 → 微调 → 导出」做成**无损往返**。
///
/// 为什么必须留着它：画布只有 650 格宽，X 还会被取整（拖动一个点 = X 取整），
/// 30s 的脚本一格 ≈ 46ms —— 时间一旦经过画布就回不到原样。导出时如果这些点没人动过，
/// 就直接把 <see cref="Actions"/> 原样写回，完全不走画布那层网格。
/// </summary>
/// <param name="CanvasDurationMs">
/// 导入时**铺满画布**用的那段时间（秒）。它就是画布整幅宽度代表的时间，
/// 也是「X → At」反算和导出圈长的依据。超过一圈上限（60s）的脚本只取前 60 秒，所以这里是 60000。
/// </param>
/// <param name="OriginalDurationMs">
/// 脚本本来的总时长，可能大于 <paramref name="CanvasDurationMs"/>（>60s 被截取过）。
/// 导出前自检拿它对比「导出的时间轴会不会跟原脚本差太多」。
/// </param>
/// <param name="Actions">铺进画布的那部分原始动作点（被截取时只含前 60 秒），按时间有序。</param>
public sealed record WaveSource(
    long CanvasDurationMs,
    long OriginalDurationMs,
    List<WaveScriptCodec.ActionPoint> Actions);

/// <summary>
/// 「微调波形」页手画波形的自动草稿。
///
/// 背景：手画波形只存在于 <see cref="MotionEngine.EditWaves"/> 里，关掉程序就没了，
/// 只能靠工具栏「保存预设」手动导出成 .hwp 文件——用户很容易画完直接关窗口，全部白画。
/// 这里在退出时把 6 轴控制点 + 一圈时长 + 六轴启用状态 + 平滑插值开关写进
/// <c>%LOCALAPPDATA%\Hexa\wave-draft.json</c>，下次启动自动恢复，
/// 并在波形页提示「已恢复上次的波形草稿」。
///
/// 与「保存预设」的区别：草稿是自动的、单份的、随用随覆盖；预设是用户主动导出的文件。
/// 波形被清空时草稿也会删除，不会把已经不要的波形又恢复回来。
///
/// 只存控制点是不够的：波形恢复了、<see cref="MotionEngine.WfAxisEnabled"/> 却没恢复，
/// 界面显示「播放中」而六轴全部禁用，实际只输出中位 50——所以启用状态必须一起存。
/// </summary>
public static class WaveDraftStore
{
    /// <summary>
    /// 磁盘上的 JSON 结构（<c>Waves</c> 用 [x, y] 数组存点，比元组更稳，也和旧草稿文件格式一致）。
    /// 属性名沿用 PascalCase，老草稿文件才能继续读。
    /// </summary>
    private sealed class Draft
    {
        public double CycleLen { get; set; } = 2.0;

        /// <summary>旧草稿文件没有这个字段，用可空类型区分「没写」和「写了 false」。</summary>
        public bool? UseSmooth { get; set; }

        /// <summary>同上：null / 长度不对 = 旧文件，回退成「该轴点数 ≥ 2 则启用」。</summary>
        public bool[]? AxisEnabled { get; set; }

        public Dictionary<string, List<double[]>> Waves { get; set; } = new();

        /// <summary>
        /// 导入来源分区（可空：**旧草稿文件没有这一段**，反序列化后是 null，
        /// 下面的读取逻辑一律按「没有来源」处理，不会崩）。
        /// </summary>
        public Dictionary<string, DraftSource>? Sources { get; set; }
    }

    /// <summary>草稿文件里的来源条目：和 <c>Waves</c> 一样用数组存点（[at, pos]），省得再定义一套结构。</summary>
    private sealed class DraftSource
    {
        public long CanvasDurationMs { get; set; }
        public long OriginalDurationMs { get; set; }
        public List<long[]> Actions { get; set; } = new();
    }

    private static string DraftPath => Path.Combine(AppSettings.DataDirectory, "wave-draft.json");

    /// <summary>
    /// 当前这份草稿各轴的导入来源。生命周期跟着草稿走：<see cref="Load"/> 时从草稿文件读出来，
    /// <c>EditorPage</c> 在导入 / 清空 / 加载预设时改写，<see cref="Save"/> 时再写回去。
    ///
    /// 为什么是静态的：草稿的保存发生在 <c>App.OnExit</c>，那里用 4 个参数的 <see cref="WaveDraft"/>
    /// 保存（那一处不归微调页改），页面只能通过这个入口把原始动作点搭上**同一份**草稿文件，
    /// 而不是另开一个文件——另开文件会出现「草稿被清了、来源还留着」这种对不上的状态。
    /// </summary>
    public static IReadOnlyDictionary<string, WaveSource> Sources { get; private set; } =
        new Dictionary<string, WaveSource>(StringComparer.OrdinalIgnoreCase);

    /// <summary>整体替换来源表（传 null = 清空）。EditorPage 每次改完都调它一次。</summary>
    public static void ReplaceSources(IReadOnlyDictionary<string, WaveSource>? sources)
    {
        var copy = new Dictionary<string, WaveSource>(StringComparer.OrdinalIgnoreCase);
        if (sources is not null)
        {
            foreach (KeyValuePair<string, WaveSource> pair in sources)
            {
                if (pair.Value?.Actions is { Count: > 0 }) copy[pair.Key] = pair.Value;
            }
        }
        Sources = copy;
    }

    /// <summary>
    /// 退出时保存草稿：控制点 + 一圈时长 + 六轴启用状态 + 平滑插值开关。
    /// 六轴都空则删除已有草稿并返回 true（这不是失败）；真正写盘失败时记日志并返回 false。
    /// </summary>
    public static bool Save(WaveDraft draft)
    {
        try
        {
            var store = new Draft
            {
                CycleLen = double.IsFinite(draft.CycleLen) ? draft.CycleLen : 2.0,
                UseSmooth = draft.UseSmooth,
                AxisEnabled = NormalizeAxisEnabled(draft.AxisEnabled),
            };

            if (draft.Waves is not null)
            {
                foreach ((string id, List<(double X, double Y)> points) in draft.Waves)
                {
                    if (points is null || points.Count == 0) continue;
                    store.Waves[id] = points.Select(point => new[] { point.X, point.Y }).ToList();
                }
            }

            if (store.Waves.Count == 0)
            {
                Clear();
                return true;
            }

            // 导入来源：只写「这一轴确实有画布点」的那些。某条例清空了还留着来源的话，
            // 下次启动会把一份孤儿来源的原始时长带回来，导出时弹出莫名其妙的时长警告。
            IReadOnlyDictionary<string, WaveSource> sources = draft.Sources ?? Sources;
            foreach (KeyValuePair<string, WaveSource> pair in sources)
            {
                WaveSource source = pair.Value;
                if (source?.Actions is not { Count: > 0 }) continue;
                if (!store.Waves.ContainsKey(pair.Key)) continue;
                store.Sources ??= new Dictionary<string, DraftSource>();
                store.Sources[pair.Key] = new DraftSource
                {
                    CanvasDurationMs = Math.Max(1, source.CanvasDurationMs),
                    OriginalDurationMs = Math.Max(source.CanvasDurationMs, source.OriginalDurationMs),
                    Actions = source.Actions.Select(point => new[] { point.At, (long)point.Pos }).ToList(),
                };
            }

            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.WriteAllText(DraftPath, JsonSerializer.Serialize(store,
                new JsonSerializerOptions { WriteIndented = false }));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"波形草稿保存失败：{ex.Message}");
            return false;
        }
    }

    public static void Clear()
    {
        // 草稿没了，跟它绑在一起的导入来源也必须一起没：否则下次启动会拿一份「孤儿来源」
        // 去比对一份毫不相干的波形，导出时弹出莫名其妙的时长警告。
        ReplaceSources(null);
        try { if (File.Exists(DraftPath)) File.Delete(DraftPath); }
        catch (Exception ex) { AppLogger.Warn($"波形草稿删除失败：{ex.Message}"); }
    }

    /// <summary>读回草稿；没有草稿或文件损坏时返回 null（损坏的文件会被删除，避免每次启动都失败）。</summary>
    public static WaveDraft? Load()
    {
        try
        {
            // 读之前先清空来源：没有草稿 / 草稿损坏 / 这条轴没有画布点，都不该留着上一份来源
            ReplaceSources(null);
            if (!File.Exists(DraftPath)) return null;

            Draft? draft = JsonSerializer.Deserialize<Draft>(File.ReadAllText(DraftPath));
            if (draft?.Waves is null || draft.Waves.Count == 0)
            {
                Clear();
                return null;
            }

            var waves = new Dictionary<string, List<(double X, double Y)>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, List<double[]> raw) in draft.Waves)
            {
                if (!Osr6DeviceProfile.IsInstalledAxis(id) || raw is null) continue;
                var points = raw
                    .Where(item => item is { Length: >= 2 })
                    .Select(item => (item[0], item[1]))
                    .ToList();
                if (points.Count == 0) continue;
                waves[id] = new List<(double X, double Y)>(WaveScriptCodec.NormalizePoints(points));
            }

            if (waves.Count == 0)
            {
                Clear();
                return null;
            }

            double cycle = double.IsFinite(draft.CycleLen) ? draft.CycleLen : 2.0;
            // 旧草稿文件没有 AxisEnabled / UseSmooth：启用状态按点数推断，平滑插值默认开。
            bool[] axisEnabled = draft.AxisEnabled is { Length: > 0 }
                ? NormalizeAxisEnabled(draft.AxisEnabled)
                : InferAxisEnabled(waves);

            // 导入来源：旧草稿文件没有 Sources 这一段 → null → 空表，一切按「没有来源」的老路走
            // （导出一律走老算法，绝不因为读不出来源就报错或改数据）。
            var sources = new Dictionary<string, WaveSource>(StringComparer.OrdinalIgnoreCase);
            if (draft.Sources is not null)
            {
                foreach (KeyValuePair<string, DraftSource> pair in draft.Sources)
                {
                    DraftSource? raw = pair.Value;
                    // 只认「这一轴确实有画布点」的来源：画布点没了（用户清空过），来源也就没有意义了
                    if (raw?.Actions is null || !waves.ContainsKey(pair.Key)) continue;

                    var actions = raw.Actions
                        .Where(item => item is { Length: >= 2 } && item[0] >= 0)
                        .Select(item => new WaveScriptCodec.ActionPoint(item[0], (int)Math.Clamp(item[1], 0, 100)))
                        .OrderBy(point => point.At)
                        .ToList();
                    if (actions.Count < 2) continue;

                    long canvasMs = raw.CanvasDurationMs > 0 ? raw.CanvasDurationMs : actions[^1].At;
                    sources[pair.Key] = new WaveSource(canvasMs, Math.Max(canvasMs, raw.OriginalDurationMs), actions);
                }
            }
            Sources = sources;

            return new WaveDraft(axisEnabled, cycle, draft.UseSmooth ?? true, waves, sources);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"波形草稿读取失败（将丢弃）：{ex.Message}");
            Clear();
            return null;
        }
    }

    /// <summary>把启用状态补齐/截断成 6 个（顺序同 <see cref="Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static bool[] NormalizeAxisEnabled(bool[]? source)
    {
        var enabled = new bool[Osr6DeviceProfile.InstalledAxes.Length];
        if (source is null) return enabled;
        for (int i = 0; i < enabled.Length && i < source.Length; i++) enabled[i] = source[i];
        return enabled;
    }

    /// <summary>旧草稿缺 AxisEnabled 时的回退规则：该轴控制点数 ≥ 2 就算启用（一个点画不出波形）。</summary>
    private static bool[] InferAxisEnabled(IReadOnlyDictionary<string, List<(double X, double Y)>> waves)
    {
        var enabled = new bool[Osr6DeviceProfile.InstalledAxes.Length];
        for (int i = 0; i < enabled.Length; i++)
        {
            enabled[i] = waves.TryGetValue(Osr6DeviceProfile.InstalledAxes[i], out var points)
                         && points is { Count: >= 2 };
        }
        return enabled;
    }
}
