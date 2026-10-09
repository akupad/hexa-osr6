using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Hexa.Services;

/// <summary>Funscript and Hexa wave data validation shared by the editor and tests.</summary>
public static class WaveScriptCodec
{
    public const int CanvasWidth = 650;
    public const int CanvasHeight = 240;
    public const int MaxActions = 5000;

    /// <summary>真正的硬上限：超过它就抽稀（不再像以前那样直接抛异常拒绝整份脚本）。</summary>
    public const int MaxActionsHardLimit = 20000;
    public const long MaxDurationMs = 3_600_000;

    public sealed record ActionPoint(long At, int Pos);

    // ══ metadata（章节 / 书签）：只读、纯装饰，绝不参与 actions 的解析 ══════════
    // 为什么单独一路解析：funscript 规范把 metadata 定义成「可选描述信息」，绝大多数脚本
    // 根本没有这一段，而写坏它的脚本多得是。所以这里的原则是**永不抛异常** ——
    // 装饰读不出来就当没有，绝不让一份本来能播的脚本因为 metadata 坏了而打不开。

    /// <summary>章节。时间单位是<b>秒</b>（funscript 规范里 metadata 的时间都是秒）。</summary>
    public sealed record ScriptChapter(string Name, double StartTime, double EndTime);

    /// <summary>书签。时间单位是<b>秒</b>。</summary>
    public sealed record ScriptBookmark(string Name, double Time);

    /// <summary>一份脚本的章节 / 书签（都没有时是两个空列表，不会返回 null）。</summary>
    public sealed record ScriptMetadata(
        IReadOnlyList<ScriptChapter> Chapters,
        IReadOnlyList<ScriptBookmark> Bookmarks)
    {
        public static ScriptMetadata Empty { get; } = new([], []);
        public bool IsEmpty => Chapters.Count == 0 && Bookmarks.Count == 0;
    }

    /// <summary>
    /// 廉价预检：整份文本里连 "chapters" / "bookmarks" 字样都没有时，调用方可以跳过完整 JSON 解析。
    /// 脚本库刷新要逐文件跑一遍，为了一个多半不存在的装饰多解析一次 2MB JSON 是白烧 CPU
    ///（和 <see cref="FunscriptTrackLoader"/> 里 HasInvertedRoot 同一套路）。
    /// </summary>
    public static bool MightHaveTimelineMetadata(string json) =>
        !string.IsNullOrEmpty(json)
        && (json.Contains("\"chapters\"", StringComparison.OrdinalIgnoreCase)
            || json.Contains("\"bookmarks\"", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 只读地取出 metadata 里的 chapters / bookmarks。<b>任何异常都吞掉并返回空</b> ——
    /// 这是装饰信息，不该影响脚本能不能播。找不到 metadata、或 metadata 里没有这两项，
    /// 都返回 <see cref="ScriptMetadata.Empty"/>。
    /// </summary>
    public static ScriptMetadata ParseFunscriptMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ScriptMetadata.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("metadata", out JsonElement meta)
                || meta.ValueKind != JsonValueKind.Object)
                return ScriptMetadata.Empty;

            var chapters = new List<ScriptChapter>();
            if (meta.TryGetProperty("chapters", out JsonElement chapterNode)
                && chapterNode.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement node in chapterNode.EnumerateArray())
                {
                    index++;
                    if (node.ValueKind != JsonValueKind.Object) continue;
                    // 起点读不出来就整条丢掉：没有起点的时间码没有意义，猜一个更糟。
                    if (!TryReadSeconds(node, ["startTime", "start"], out double start)) continue;
                    TryReadSeconds(node, ["endTime", "end"], out double end);
                    if (end < start) end = start;   // 终点早于起点（写坏了）→ 收成零长度，而不是负长度
                    chapters.Add(new ScriptChapter(ReadMetadataName(node, $"第 {index} 章"), start, end));
                }
                chapters.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            }

            var bookmarks = new List<ScriptBookmark>();
            if (meta.TryGetProperty("bookmarks", out JsonElement bookmarkNode)
                && bookmarkNode.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement node in bookmarkNode.EnumerateArray())
                {
                    index++;
                    if (node.ValueKind != JsonValueKind.Object) continue;
                    if (!TryReadSeconds(node, ["time", "at"], out double time)) continue;
                    bookmarks.Add(new ScriptBookmark(ReadMetadataName(node, $"书签 {index}"), time));
                }
                bookmarks.Sort((a, b) => a.Time.CompareTo(b.Time));
            }

            return new ScriptMetadata(chapters, bookmarks);
        }
        catch
        {
            return ScriptMetadata.Empty;
        }
    }

    /// <summary>
    /// 读一个「秒」值。允许几个别名（不同导出工具写法不一），也容忍把数字写成字符串（"12.5"）——
    /// 宽容度和 actions 那边的 TryGetNumber 保持一致。非法值（负数 / NaN / 无穷）一律读不出来。
    /// </summary>
    private static bool TryReadSeconds(JsonElement node, string[] names, out double seconds)
    {
        seconds = 0;
        foreach (string name in names)
        {
            if (!node.TryGetProperty(name, out JsonElement value)) continue;
            double parsed;
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!value.TryGetDouble(out parsed)) continue;
                    break;
                case JsonValueKind.String:
                    if (!double.TryParse(value.GetString(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out parsed)) continue;
                    break;
                default:
                    continue;
            }
            if (!double.IsFinite(parsed) || parsed < 0) continue;
            seconds = parsed;
            return true;
        }
        return false;
    }

    /// <summary>章节/书签的名字：缺失或空白就给「第 N 章 / 书签 N」，列表里不会出现点不动的空行。</summary>
    private static string ReadMetadataName(JsonElement node, string fallback)
    {
        if (!node.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
            return fallback;
        string text = (name.GetString() ?? "").Trim();
        return text.Length > 0 ? text : fallback;
    }

    public static IReadOnlyList<ActionPoint> ParseFunscript(string json)
    {
        IReadOnlyDictionary<string, IReadOnlyList<ActionPoint>> tracks = ParseFunscriptTracks(json);
        if (tracks.TryGetValue("L0", out IReadOnlyList<ActionPoint>? actions)) return actions;
        if (tracks.Count == 1) return tracks.Values.First();
        throw new FormatException("多轴脚本没有 L0 主轨道。");
    }

    /// <summary>把解析异常翻译成用户能看懂的一句话。</summary>
    public static string DescribeParseError(Exception ex) => ex switch
    {
        FormatException => ex.Message,
        System.Text.Json.JsonException => "不是合法的 funscript JSON（缺少 actions 或格式错误）",
        UnauthorizedAccessException => "没有读取权限",
        IOException io => "读取失败：" + io.Message,
        _ => ex.Message,
    };

    public static IReadOnlyDictionary<string, IReadOnlyList<ActionPoint>> ParseFunscriptTracks(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("脚本内容为空。");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("脚本根节点必须是对象。");

        var result = new Dictionary<string, IReadOnlyList<ActionPoint>>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("actions", out JsonElement rootActions)
            && rootActions.ValueKind == JsonValueKind.Array)
            result["L0"] = ParseActions(rootActions);

        if (doc.RootElement.TryGetProperty("axes", out JsonElement axes)
            && axes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement axis in axes.EnumerateArray())
            {
                if (axis.ValueKind != JsonValueKind.Object
                    || !axis.TryGetProperty("id", out JsonElement idNode)
                    || idNode.ValueKind != JsonValueKind.String
                    || !axis.TryGetProperty("actions", out JsonElement axisActions)
                    || axisActions.ValueKind != JsonValueKind.Array)
                    continue;

                string? axisId = FunscriptTrackLoader.NormalizeTrackId(idNode.GetString());
                if (axisId != null && Osr6DeviceProfile.IsInstalledAxis(axisId))
                    result[axisId] = ParseActions(axisActions);
            }
        }

        if (result.Count == 0)
            throw new FormatException("脚本没有可用的六轴 actions 数据。");
        return result;
    }

    private static IReadOnlyList<ActionPoint> ParseActions(JsonElement actions)
    {
        var byTime = new SortedDictionary<long, ActionPoint>();
        foreach (var item in actions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("at", out var atNode)
                || !item.TryGetProperty("pos", out var posNode)
                || !TryGetNumber(atNode, out double rawAt)
                || !TryGetNumber(posNode, out double rawPos)
                || !double.IsFinite(rawAt)
                || !double.IsFinite(rawPos))
                throw new FormatException("每个动作必须包含数字 at 和 pos。");
            // 很多导出工具会把整数写成 123.0 / 50.0，所以先按 double 读再四舍五入成整数。
            // 范围先在 double 上校验（防止 (long) 转换溢出），再取整。
            if (rawAt < 0 || rawAt > MaxDurationMs) throw new FormatException($"动作时间超出范围：{rawAt} ms。");
            if (rawPos is < 0 or > 100) throw new FormatException($"动作位置超出范围：{rawPos}。");
            long at = (long)Math.Round(rawAt);
            int pos = (int)Math.Round(rawPos);
            // 同一时间点采用最后一个动作，这是大多数播放器的兼容行为。
            byTime[at] = new ActionPoint(at, Math.Clamp(pos, 0, 100));
            // 以前这里是"超过 4 倍上限就直接抛异常、整份脚本读不进来"。
            // 2026 年 FunGen 那类帧精确工具生成的整片脚本很容易过 2 万点 —— 直接拒绝等于
            // "人家的脚本在我们这儿打不开"。改成**接收后保极值抽稀**（见 Downsample），
            // 既不丢换向峰值，也不再有硬墙。
        }

        // 这条警告以前写在循环体内：一个 3 万点的脚本会从第 20001 点起**每个点打一条**，
        // 实测一晚刷出 93,596 条、占全部日志 96.6%（app.log 被撑到 6.7MB 并反复轮转），
        // 真出问题时最该看的错误全被淹。移到循环外，只报一次。
        if (byTime.Count > MaxActionsHardLimit)
            AppLogger.Warn($"脚本动作点过多（{byTime.Count}），将保极值抽稀到 {MaxActions} 个（硬上限 {MaxActionsHardLimit}）");

        if (byTime.Count < 2) throw new FormatException("脚本至少需要两个不同时间点。");
        return Downsample(byTime.Values.ToArray(), MaxActions);
    }

    /// <summary>读数字节点：兼容 funscript 里 <c>"at": 123</c> / <c>123.0</c> / <c>1.23e3</c> 这些常见写法。</summary>
    private static bool TryGetNumber(JsonElement node, out double value)
    {
        value = 0;
        return node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out value);
    }

    public static List<(double X, double Y)> ToCanvasPoints(
        IEnumerable<ActionPoint> actions,
        long? timelineDurationMs = null)
    {
        var list = actions.OrderBy(p => p.At).ToArray();
        if (list.Length < 2) throw new FormatException("脚本至少需要两个动作点。");
        long duration = Math.Max(1, timelineDurationMs ?? list[^1].At);
        duration = Math.Max(duration, list[^1].At);
        return list.Select(point => (
            X: Math.Clamp(point.At * (double)CanvasWidth / duration, 0, CanvasWidth),
            Y: Math.Clamp(CanvasHeight - point.Pos * CanvasHeight / 100.0, 0, CanvasHeight)
        )).ToList();
    }

    /// <summary>
    /// 取前 <paramref name="maxAtMs"/> 毫秒以内的动作点（「微调波形」把超过一圈上限的长脚本
    /// 截成前 60 秒时用）。
    ///
    /// 为什么只过滤、不插值：这里凭空补一个收尾点的话，「没改过就原样写回」就会多出一个
    /// 原脚本里根本没有的动作 —— 用户只是导入又导出，文件却变了。宁可最后一段短一点，
    /// 也不在用户的数据里造假点。（输入按时间有序，解析端用 SortedDictionary 保证；顺序原样保留。）
    /// </summary>
    public static List<ActionPoint> TakeUpTo(IReadOnlyList<ActionPoint> actions, long maxAtMs)
    {
        var kept = new List<ActionPoint>(actions.Count);
        foreach (ActionPoint point in actions)
        {
            if (point.At <= maxAtMs) kept.Add(point);
        }
        return kept;
    }

    public static List<(double X, double Y)> NormalizePoints(IEnumerable<(double X, double Y)> points)
    {
        var byX = new SortedDictionary<double, double>();
        foreach (var (x, y) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            byX[Math.Clamp(x, 0, CanvasWidth)] = Math.Clamp(y, 0, CanvasHeight);
        }
        return byX.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    public static string SerializeFunscript(IEnumerable<(double X, double Y)> points, double cycleLengthSeconds)
    {
        var normalized = NormalizePoints(points);
        if (normalized.Count == 0) throw new InvalidOperationException("没有可导出的波形点。");
        double duration = Math.Clamp(double.IsFinite(cycleLengthSeconds) ? cycleLengthSeconds : 2, 0.1, MaxDurationMs / 1000.0);
        double maxX = Math.Max(1, normalized.Max(point => point.X));
        var actions = normalized.Select(point => new ActionPoint(
            (long)Math.Round(point.X / maxX * duration * 1000),
            (int)Math.Round(Math.Clamp(100 - point.Y / CanvasHeight * 100, 0, 100)))).ToArray();
        return JsonSerializer.Serialize(new { actions }, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    /// <summary>
    /// 保极值抽稀：按时间分桶，每桶保留**最小值与最大值两个点**（按原时间顺序放回）。
    ///
    /// 为什么不用"等间隔下标取样"（以前的做法）：动作脚本的信息几乎全在**换向峰值**上，
    /// 按下标取样会以固定概率把峰值整片削平 —— 抽稀后的脚本幅度变小、节奏变糊，而且
    /// 抽出来的东西不可预测。分桶保极值是信号处理里的标准降采样做法（min/max decimation），
    /// 它在同样的点数下保住包络，代价只是最坏情况下点数略多于目标上限。
    /// </summary>
    private static IReadOnlyList<ActionPoint> Downsample(IReadOnlyList<ActionPoint> input, int maxCount)
    {
        if (input.Count <= maxCount) return input;

        int bucketCount = Math.Max(1, maxCount / 2);           // 每桶最多出 2 个点
        var picked = new List<ActionPoint>(maxCount + 4);
        int size = input.Count;
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            int from = (int)((long)bucket * size / bucketCount);
            int to = (int)((long)(bucket + 1) * size / bucketCount);
            if (to <= from) continue;

            int minIndex = from, maxIndex = from;
            for (int i = from + 1; i < to; i++)
            {
                if (input[i].Pos < input[minIndex].Pos) minIndex = i;
                if (input[i].Pos > input[maxIndex].Pos) maxIndex = i;
            }
            if (minIndex == maxIndex)
            {
                picked.Add(input[minIndex]);
            }
            else if (minIndex < maxIndex)
            {
                picked.Add(input[minIndex]);
                picked.Add(input[maxIndex]);
            }
            else
            {
                picked.Add(input[maxIndex]);
                picked.Add(input[minIndex]);
            }
        }

        // 首尾必须保留（很多播放器按首尾算时长/起始姿态）
        if (picked.Count == 0 || picked[0].At != input[0].At) picked.Insert(0, input[0]);
        if (picked[^1].At != input[^1].At) picked.Add(input[^1]);
        return picked.DistinctBy(point => point.At).OrderBy(point => point.At).ToArray();
    }
}
