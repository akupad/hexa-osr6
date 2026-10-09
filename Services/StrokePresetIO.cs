using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// 动作预设（<see cref="StrokePreset"/>）的导入 / 导出实现。
///
/// 这段逻辑原来只长在 <c>StrokesViewModel</c> 里、入口只挂在脚本库页；现在抽到服务层，
/// <b>动作页与脚本库页共用同一套</b>：文件格式（<c>[{name,type,data}]</c>）、
/// 冲突自动改名（-2 / -3）、模型里没有的 noise / motion 附加字段的原样保留，
/// 三处行为完全一致（抽取时是原样搬运，没有顺手改逻辑 —— 脚本库页的既有调用路径
/// 仍走 <c>StrokesViewModel.ExportPresetsJson / ImportPresetsJson</c>，行为一字未变）。
///
/// 为什么做成"传入当前集合 → 返回合并结果"的纯函数：谁拥有动作库谁负责写回。
/// 服务层不持有任何 ViewModel 状态，自检 / 单测可以直接喂一段 JSON 验证合并与改名规则。
/// </summary>
public static class StrokePresetIO
{
    /// <summary>轴顺序（唯一真源：<see cref="Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxisKeys = Osr6DeviceProfile.InstalledAxes;

    /// <summary>导出用：不转义中文，方便用户直接阅读 / 手改。</summary>
    private static readonly JsonSerializerOptions _exportJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>导入结果：新增 / 改名统计 + 合并后的动作集合与附加字段（调用方据此落盘并刷新界面）。</summary>
    public sealed record ImportOutcome(
        int Added,
        int Renamed,
        List<string> RenamedNames,
        StrokePreset[] Presets,
        Dictionary<string, JsonObject> Extras);

    // ══════════════════════════════════════════════════════════════
    //  纯数据层
    // ══════════════════════════════════════════════════════════════

    /// <summary>把动作预设序列化为 JSON 文本（<c>[{name,type,data}]</c>）。</summary>
    /// <param name="extras">模型里没有、但要原样带回的附加字段（noise / motion），按动作 Id 索引。</param>
    public static string ExportToJson(
        IReadOnlyList<StrokePreset> presets,
        IReadOnlyDictionary<string, JsonObject>? extras)
    {
        var root = new JsonArray();
        foreach (var preset in presets)
        {
            var axisValues = preset.AllAxes;
            var axes = new JsonObject();
            for (int i = 0; i < AxisKeys.Length; i++)
            {
                var axis = axisValues[i];
                axes[AxisKeys[i]] = new JsonObject
                {
                    ["from"]         = axis[0],
                    ["to"]           = axis[1],
                    ["phase"]        = axis[2],
                    ["eccentricity"] = axis[3],
                };
            }

            var data = new JsonObject
            {
                ["id"]       = preset.Id,
                ["category"] = preset.Cat,
                ["axes"]     = axes,
            };

            // noise / motion 等模型里没有的字段：导入时存下来，导出原样带回
            if (extras is not null && extras.TryGetValue(preset.Id, out var extra))
            {
                foreach (var kv in extra)
                {
                    if (data.ContainsKey(kv.Key)) continue;
                    data[kv.Key] = kv.Value?.DeepClone();
                }
            }

            root.Add(new JsonObject
            {
                ["name"] = preset.Label,
                ["type"] = "tempeststroke",
                ["data"] = data,
            });
        }
        return root.ToJsonString(_exportJsonOpts);
    }

    /// <summary>
    /// 解析 JSON 并合并进现有动作预设集合。Id / 名称冲突时自动追加 -2 / -3 后缀。
    /// <b>不写盘</b>：合并结果由调用方决定怎么落地（服务层不做 I/O，便于测试与复用）。
    /// </summary>
    /// <exception cref="FormatException">JSON 语法或结构不正确（中文说明）。</exception>
    public static ImportOutcome ImportFromJson(
        string json,
        IReadOnlyList<StrokePreset> current,
        IReadOnlyDictionary<string, JsonObject>? currentExtras)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("动作库 JSON 为空，无法导入。");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"动作库 JSON 语法错误：{ex.Message}", ex);
        }

        var items = ResolveImportItems(root);
        if (items.Count == 0)
            throw new FormatException("动作库 JSON 里没有任何动作（数组为空）。");

        var merged       = current.ToList();
        var usedIds      = new HashSet<string>(merged.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var usedLabels   = new HashSet<string>(merged.Select(p => p.Label), StringComparer.OrdinalIgnoreCase);
        var extras       = currentExtras is null
            ? new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonObject>(currentExtras, StringComparer.OrdinalIgnoreCase);
        var renamedNames = new List<string>();
        int added = 0, renamed = 0;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject item)
                throw new FormatException($"动作库 JSON 第 {i + 1} 项不是对象，无法解析。");

            var (preset, extra) = ParseImportItem(item, i + 1);

            string baseId     = preset.Id;
            string baseLabel  = preset.Label;
            string finalId    = baseId;
            string finalLabel = baseLabel;

            // 冲突自动改名：Id 或显示名任一被占用，就一起加 -2 / -3 后缀
            if (usedIds.Contains(finalId) || usedLabels.Contains(finalLabel))
            {
                int suffix = 2;
                while (usedIds.Contains($"{baseId}-{suffix}") || usedLabels.Contains($"{baseLabel}-{suffix}"))
                    suffix++;
                finalId    = $"{baseId}-{suffix}";
                finalLabel = $"{baseLabel}-{suffix}";
                renamed++;
                renamedNames.Add(finalLabel);
            }

            preset.Id    = finalId;
            preset.Label = finalLabel;
            usedIds.Add(finalId);
            usedLabels.Add(finalLabel);
            merged.Add(preset);
            if (extra != null) extras[finalId] = extra;
            added++;
        }

        return new ImportOutcome(added, renamed, renamedNames, merged.ToArray(), extras);
    }

    // ══════════════════════════════════════════════════════════════
    //  界面流程层（选文件 / 读写 / 提示）—— 动作页与脚本库页共用同一套文案与容错
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 「选文件 → 写盘 → 提示」一条龙。返回导出的动作个数；用户取消或失败返回 null（失败时已弹过提示）。
    /// </summary>
    public static int? ExportWithDialog(
        System.Windows.Window? owner,
        string title,
        IReadOnlyList<StrokePreset> presets,
        IReadOnlyDictionary<string, JsonObject>? extras)
    {
        var dialog = new SaveFileDialog
        {
            Title      = title,
            Filter     = "动作库 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            DefaultExt = ".json",
            FileName   = $"helix-presets-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (!ShowDialog(dialog, owner)) return null;

        try
        {
            File.WriteAllText(dialog.FileName, ExportToJson(presets, extras));
            System.Windows.MessageBox.Show(
                $"已导出 {presets.Count} 个动作到：\n{dialog.FileName}",
                title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return presets.Count;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导出失败：" + ex.Message, title,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>
    /// 「选文件 → 读盘 → 合并 → 提示」一条龙。<paramref name="merge"/> 由调用方提供
    /// （谁拥有动作库谁负责写回：动作页传 <c>StrokesViewModel.ImportPresets</c>）。
    /// 返回 null = 用户取消或失败（失败时已弹过提示）。
    /// </summary>
    public static ImportOutcome? ImportWithDialog(
        System.Windows.Window? owner,
        string title,
        Func<string, ImportOutcome> merge)
    {
        var dialog = new OpenFileDialog
        {
            Title           = title,
            Filter          = "动作库 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!ShowDialog(dialog, owner)) return null;

        try
        {
            var outcome = merge(File.ReadAllText(dialog.FileName));

            var message = $"导入完成：新增 {outcome.Added} 个动作";
            if (outcome.Renamed > 0)
                message += $"，重命名 {outcome.Renamed} 个：\n  " + string.Join("\n  ", outcome.RenamedNames);
            message += $"\n\n当前动作库共 {outcome.Presets.Length} 个动作。";

            System.Windows.MessageBox.Show(message, title,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return outcome;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导入失败：" + ex.Message, title,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>统一的对话框调用：有宿主窗口就挂上去（模态在页面窗口上），没有就独立弹出。</summary>
    private static bool ShowDialog(Microsoft.Win32.CommonDialog dialog, System.Windows.Window? owner) =>
        owner is null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;

    // ══════════════════════════════════════════════════════════════
    //  解析辅助（与抽取前逐行一致）
    // ══════════════════════════════════════════════════════════════

    /// <summary>兼容根数组与 { "presets": [...] } / { "data": [...] } 两种包装。</summary>
    private static JsonArray ResolveImportItems(JsonNode? root)
    {
        if (root is JsonArray array) return array;
        if (root is JsonObject obj)
        {
            if (GetMember(obj, "presets") is JsonArray presets) return presets;
            if (GetMember(obj, "data") is JsonArray data) return data;
            throw new FormatException("动作库 JSON 结构不正确：根对象里没有 presets / data 数组。");
        }
        throw new FormatException("动作库 JSON 结构不正确：根节点应为数组 [{name,type,data}]。");
    }

    /// <summary>解析单个 {name,type,data} 条目；同时取出需要原样保留的 noise / motion。</summary>
    private static (StrokePreset preset, JsonObject? extra) ParseImportItem(JsonObject item, int index)
    {
        var data = GetMember(item, "data") as JsonObject ?? item;

        string? name = ReadString(GetMember(item, "name"))
                       ?? ReadString(GetMember(data, "name"))
                       ?? ReadString(GetMember(data, "label"));
        string? id = ReadString(GetMember(data, "id")) ?? name;
        if (string.IsNullOrWhiteSpace(id))
            throw new FormatException($"动作库 JSON 第 {index} 项缺少 id / name，无法确定动作名称。");

        // axes 既可以是 data.axes 对象，也兼容直接平铺在 data 上
        var axes = GetMember(data, "axes") as JsonObject ?? data;
        var preset = new StrokePreset
        {
            Id    = id,
            Label = name ?? id,
            Cat   = ReadString(GetMember(data, "category"))
                    ?? ReadString(GetMember(data, "cat")) ?? "自",
            L0    = ReadAxis(GetMember(axes, "L0"), [0, 1, 0, 0]),
            L1    = ReadAxis(GetMember(axes, "L1"), [0.5, 0.5, 0, 0]),
            L2    = ReadAxis(GetMember(axes, "L2"), [0.5, 0.5, 0, 0]),
            R0    = ReadAxis(GetMember(axes, "R0"), [0.5, 0.5, 0, 0]),
            R1    = ReadAxis(GetMember(axes, "R1"), [0.5, 0.5, 0, 0]),
            R2    = ReadAxis(GetMember(axes, "R2"), [0.5, 0.5, 0, 0]),
        };
        if (!preset.Normalize())
            throw new FormatException($"动作库 JSON 第 {index} 项校验失败（id 为空）。");

        JsonObject? extra = null;
        foreach (var key in new[] { "noise", "motion" })
        {
            if (GetMember(data, key) is JsonNode node)
            {
                extra ??= new JsonObject();
                extra[key] = node.DeepClone();
            }
        }
        return (preset, extra);
    }

    /// <summary>读取单轴 [from,to,phase,eccentricity]；兼容对象写法与数组写法。</summary>
    private static double[] ReadAxis(JsonNode? node, double[] fallback)
    {
        if (node is JsonArray array)
        {
            if (array.Count < 4) return fallback;
            return
            [
                ReadDouble(array[0], fallback[0]),
                ReadDouble(array[1], fallback[1]),
                ReadDouble(array[2], fallback[2]),
                ReadDouble(array[3], fallback[3]),
            ];
        }
        if (node is JsonObject obj)
        {
            return
            [
                ReadDouble(GetMember(obj, "from"), fallback[0]),
                ReadDouble(GetMember(obj, "to"), fallback[1]),
                ReadDouble(GetMember(obj, "phase"), fallback[2]),
                ReadDouble(GetMember(obj, "eccentricity") ?? GetMember(obj, "ecc"), fallback[3]),
            ];
        }
        return fallback;
    }

    private static double ReadDouble(JsonNode? node, double fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                double.IsFinite(parsed)) return parsed;
        }
        return fallback;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            return text.Trim();
        return null;
    }

    /// <summary>大小写不敏感取成员（导出格式用 from/to，ayva 系文件可能是 From/To）。</summary>
    private static JsonNode? GetMember(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var direct)) return direct;
        foreach (var kv in obj)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }
}
