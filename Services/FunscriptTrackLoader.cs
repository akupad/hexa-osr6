using System.Text.Json;

namespace Hexa.Services;

public sealed record FunscriptTrackSet(
    IReadOnlyDictionary<string, IReadOnlyList<WaveScriptCodec.ActionPoint>> Tracks,
    long DurationMs)
{
    /// <summary>
    /// 本次导入是否按 funscript 根节点的 <c>inverted</c> 做过上下颠倒（pos → 100 - pos）。
    /// <see cref="Tracks"/> 里的位置已经翻成标准朝向，这个标志只用于给用户提示。
    /// </summary>
    public bool Inverted { get; init; }
}

public static class FunscriptTrackLoader
{
    // 六轴 → 社区通用轴后缀（MultiFunPlayer 等播放器的约定）的唯一映射表。
    private static readonly IReadOnlyDictionary<string, string> AxisToCommunitySuffix =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["L0"] = "stroke", ["L1"] = "surge", ["L2"] = "sway",
            ["R0"] = "twist", ["R1"] = "roll", ["R2"] = "pitch",
        };

    // 社区后缀 → 六轴 id（只含社区名，不含 l0..r2），供 AxisFromCommunitySuffix 反向解析。
    private static readonly IReadOnlyDictionary<string, string> CommunitySuffixToAxis =
        AxisToCommunitySuffix.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    // 单一来源：由「社区映射表 + 轴 id 自身」构建，读取两种命名永远和写出（CompanionFilePath）一致。
    // 行为与旧的手写表完全相同：认得 stroke/surge/sway/twist/roll/pitch 与 l0..r2，且忽略大小写。
    private static readonly IReadOnlyDictionary<string, string> TrackSuffixes = BuildTrackSuffixes();

    private static IReadOnlyDictionary<string, string> BuildTrackSuffixes()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string axisId, string communitySuffix) in AxisToCommunitySuffix)
        {
            map[communitySuffix] = axisId;  // 社区约定：stroke → L0
            map[axisId] = axisId;           // Hexa 约定：轴 id 自身（L0 → L0）
        }
        return map;
    }

    public static FunscriptTrackSet LoadCompanionSet(string selectedPath)
    {
        string fullPath = Path.GetFullPath(selectedPath);
        string withoutExtension = Path.ChangeExtension(fullPath, null);
        string suffix = Path.GetExtension(withoutExtension).TrimStart('.');
        bool selectedHasAxisSuffix = TrackSuffixes.TryGetValue(suffix, out string? selectedAxis);
        string basePath = selectedHasAxisSuffix ? Path.ChangeExtension(withoutExtension, null) : withoutExtension;

        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(fullPath)) candidates[selectedHasAxisSuffix ? selectedAxis! : "L0"] = fullPath;

        foreach ((string suffixName, string axisId) in TrackSuffixes)
        {
            string candidate = $"{basePath}.{suffixName}.funscript";
            if (File.Exists(candidate) && !candidates.ContainsKey(axisId))
                candidates[axisId] = candidate;
        }

        var tracks = new Dictionary<string, IReadOnlyList<WaveScriptCodec.ActionPoint>>(StringComparer.OrdinalIgnoreCase);
        long duration = 0;
        bool inverted = false;
        foreach ((string axisId, string path) in candidates)
        {
            if (new System.IO.FileInfo(path).Length > 8 * 1024 * 1024)
                throw new FormatException($"脚本文件过大：{Path.GetFileName(path)}。");

            string json = File.ReadAllText(path);
            bool fileInverted = HasInvertedRoot(json);
            var parsedTracks = WaveScriptCodec.ParseFunscriptTracks(json);
            // 根节点 inverted=true 表示位置上下颠倒：这里统一翻成标准朝向，
            // 调用方看 FunscriptTrackSet.Inverted 就知道这次导入做过反转。
            if (fileInverted)
            {
                parsedTracks = parsedTracks.ToDictionary(
                    pair => pair.Key,
                    pair => InvertPositions(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
                inverted = true;
            }

            bool remapSingleRootTrack = parsedTracks.Count == 1
                && parsedTracks.ContainsKey("L0")
                && !string.Equals(axisId, "L0", StringComparison.OrdinalIgnoreCase);
            foreach ((string parsedAxis, IReadOnlyList<WaveScriptCodec.ActionPoint> actions) in parsedTracks)
            {
                string targetAxis = remapSingleRootTrack ? axisId : parsedAxis;
                tracks[targetAxis] = actions;
                duration = Math.Max(duration, actions[^1].At);
            }
        }

        if (tracks.Count == 0) throw new FormatException("没有找到可读取的 Funscript 轨道。");
        return new FunscriptTrackSet(tracks, duration) { Inverted = inverted };
    }

    /// <summary>读 funscript 根节点的 <c>inverted</c> 标志（true = 位置上下颠倒，需要把 pos 翻成 100 - pos）。</summary>
    /// <remarks>读不出来就返回 false：真正的错误交给 <see cref="WaveScriptCodec.ParseFunscriptTracks"/> 抛（那里给的是用户可读消息）。</remarks>
    private static bool HasInvertedRoot(string json)
    {
        // 绝大多数 funscript 根本没有这个字段：先做一次廉价的子串检查，
        // 免得每导入一个文件都为了读一个标志多解析一整遍 JSON（脚本库扫描会逐文件调用）。
        if (json.IndexOf("inverted", StringComparison.OrdinalIgnoreCase) < 0) return false;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("inverted", out JsonElement node))
                return false;
            return node.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => node.TryGetDouble(out double number) && number == 1,
                JsonValueKind.String => string.Equals(node.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
        catch { return false; }
    }

    /// <summary>位置上下颠倒：pos → 100 - pos（时间轴不变，仍按 at 升序）。</summary>
    private static IReadOnlyList<WaveScriptCodec.ActionPoint> InvertPositions(
        IReadOnlyList<WaveScriptCodec.ActionPoint> actions)
        => actions.Select(action => action with { Pos = 100 - action.Pos }).ToArray();

    public static string? AxisFromFileName(string path)
    {
        string withoutExtension = Path.ChangeExtension(path, null);
        string suffix = Path.GetExtension(withoutExtension).TrimStart('.');
        return TrackSuffixes.TryGetValue(suffix, out string? axis) ? axis : null;
    }

    public static string? NormalizeTrackId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string normalized = id.Trim();
        if (Osr6DeviceProfile.IsInstalledAxis(normalized)) return normalized.ToUpperInvariant();
        return TrackSuffixes.TryGetValue(normalized, out string? axis) ? axis : null;
    }

    /// <summary>六轴 → 社区通用的轴后缀名（MultiFunPlayer 等播放器的约定）。</summary>
    public static string? CommunitySuffix(string axisId)
    {
        if (string.IsNullOrWhiteSpace(axisId)) return null;
        return AxisToCommunitySuffix.TryGetValue(axisId.Trim(), out string? suffix) ? suffix : null;
    }

    /// <summary>社区后缀名（stroke/surge/...）→ 六轴 id；不认识返回 null。</summary>
    public static string? AxisFromCommunitySuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)) return null;
        string key = suffix.Trim().TrimStart('.');
        return CommunitySuffixToAxis.TryGetValue(key, out string? axis) ? axis : null;
    }

    /// <summary>写出某个轴的文件名：communityNaming=false → 名称.L0.funscript；true → 名称.stroke.funscript。</summary>
    public static string CompanionFilePath(string basePathWithoutExtension, string axisId, bool communityNaming)
    {
        if (string.IsNullOrWhiteSpace(basePathWithoutExtension))
            throw new ArgumentException("基础文件名不能为空。", nameof(basePathWithoutExtension));
        if (string.IsNullOrWhiteSpace(axisId))
            throw new ArgumentException("轴 id 不能为空。", nameof(axisId));

        string? normalizedAxis = NormalizeTrackId(axisId);
        if (normalizedAxis is null)
            throw new ArgumentException($"无法识别的轴 id：{axisId}。", nameof(axisId));

        // Hexa 约定沿用规范的大写轴 id（L0/R2），社区约定改用社区后缀；两种都不接受未知轴。
        string suffix = communityNaming
            ? (CommunitySuffix(normalizedAxis) ?? normalizedAxis)
            : normalizedAxis;
        return $"{basePathWithoutExtension}.{suffix}.funscript";
    }
}
