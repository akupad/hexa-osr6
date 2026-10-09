using System.Text.Json;

namespace Hexa.Models;

/// <summary>
/// TempestStroke 每轴运动函数（对应 ayva 的 motion 分支）。
/// </summary>
public enum StrokeMotion
{
    Sinusoidal = 0,
    Parabolic  = 1,
    Linear     = 2,
}

/// <summary>
/// TempestStroke preset — 每轴: [from, to, phase, eccentricity, noiseFrom, noiseTo]
/// 正弦: mid - amp * cos(t*2π + 0.5π*phase + ecc*sin(t*2π))
/// 抛物 / 线性: 见 MotionEngine.SampleTempestAxis
/// 旧 presets.json（每轴只有 4 个元素、没有 Motion 字段）由 Normalize() 补齐：
/// noise = 0、motion = Sinusoidal，采样结果与旧版完全一致。
/// </summary>
public class StrokePreset
{
    public string Id      { get; set; } = "";
    public string Label   { get; set; } = "";
    public string Cat     { get; set; } = "";
    public double[] L0    { get; set; } = [0, 1, 0, 0, 0, 0];
    public double[] L1    { get; set; } = [0.5, 0.5, 0, 0, 0, 0];
    public double[] L2    { get; set; } = [0.5, 0.5, 0, 0, 0, 0];
    public double[] R0    { get; set; } = [0.5, 0.5, 0, 0, 0, 0];
    public double[] R1    { get; set; } = [0.5, 0.5, 0, 0, 0, 0];
    public double[] R2    { get; set; } = [0.5, 0.5, 0, 0, 0, 0];

    /// <summary>
    /// 每轴运动函数名（Sinusoidal / Parabolic / Linear，与 L0..R2 同序）。
    /// 用字符串而不是枚举，是为了让 presets.json 可以手改；未知/缺省值一律回退正弦。
    /// </summary>
    public string[] Motion  { get; set; } =
        ["Sinusoidal", "Sinusoidal", "Sinusoidal", "Sinusoidal", "Sinusoidal", "Sinusoidal"];

    /// <summary>由动作编辑器创建的动作（UI 打「自」标记用）。</summary>
    public bool IsCustom { get; set; }

    public double[][] AllAxes => [L0, L1, L2, R0, R1, R2];

    /// <summary>第 axis 条轴的运动函数（0..5，顺序同 AllAxes）。</summary>
    public StrokeMotion MotionOf(int axis) =>
        ParseMotion(Motion is { Length: > 0 } && axis >= 0 && axis < Motion.Length ? Motion[axis] : null);

    public static StrokeMotion ParseMotion(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return StrokeMotion.Sinusoidal;
        if (name.Equals("Parabolic", StringComparison.OrdinalIgnoreCase)) return StrokeMotion.Parabolic;
        if (name.Equals("Linear", StringComparison.OrdinalIgnoreCase)) return StrokeMotion.Linear;
        return StrokeMotion.Sinusoidal;
    }

    /// <summary>枚举 → presets.json 里的规范字符串。</summary>
    public static string MotionName(StrokeMotion motion) => motion switch
    {
        StrokeMotion.Parabolic => "Parabolic",
        StrokeMotion.Linear    => "Linear",
        _                      => "Sinusoidal",
    };

    /// <summary>枚举 → 界面中文名。</summary>
    public static string MotionLabel(StrokeMotion motion) => motion switch
    {
        StrokeMotion.Parabolic => "抛物",
        StrokeMotion.Linear    => "线性",
        _                      => "正弦",
    };

    public bool Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) return false;
        Label = string.IsNullOrWhiteSpace(Label) ? Id : Label.Trim();
        Cat = string.IsNullOrWhiteSpace(Cat) ? "自" : Cat.Trim();
        L0 = NormalizeAxis(L0, [0, 1, 0, 0, 0, 0]);
        L1 = NormalizeAxis(L1, [0.5, 0.5, 0, 0, 0, 0]);
        L2 = NormalizeAxis(L2, [0.5, 0.5, 0, 0, 0, 0]);
        R0 = NormalizeAxis(R0, [0.5, 0.5, 0, 0, 0, 0]);
        R1 = NormalizeAxis(R1, [0.5, 0.5, 0, 0, 0, 0]);
        R2 = NormalizeAxis(R2, [0.5, 0.5, 0, 0, 0, 0]);
        Motion = NormalizeMotion(Motion);
        return true;
    }

    private static double[] NormalizeAxis(double[]? source, double[] fallback)
    {
        if (source is not { Length: >= 4 }) return fallback;
        return
        [
            ClampFinite(source[0], 0, 1, fallback[0]),
            ClampFinite(source[1], 0, 1, fallback[1]),
            ClampFinite(source[2], -4, 4, fallback[2]),
            ClampFinite(source[3], -1, 1, fallback[3]),
            // 旧文件没有这两个值 → 噪声默认 0（不扰动，行为与旧版一致）
            source.Length > 4 ? ClampFinite(source[4], 0, 1, 0) : 0,
            source.Length > 5 ? ClampFinite(source[5], 0, 1, 0) : 0,
        ];
    }

    private static string[] NormalizeMotion(string[]? source)
    {
        var result = new string[6];
        for (int i = 0; i < result.Length; i++)
        {
            string? name = source is { Length: > 0 } && i < source.Length ? source[i] : null;
            result[i] = MotionName(ParseMotion(name));
        }
        return result;
    }

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// presets.json 持久化（与 StrokesViewModel 读同一个文件：&lt;app_dir&gt;/presets.json）。
/// 这里只负责写；读失败时由调用方用内存里的预设全集兜底，避免把内置动作整批丢掉。
/// </summary>
public static class StrokePresetStore
{
    public static string FilePath { get; } =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presets.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>读取文件里的全部预设；文件缺失或解析失败返回空数组。</summary>
    public static StrokePreset[] LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var loaded = JsonSerializer.Deserialize<StrokePreset[]>(File.ReadAllText(FilePath), _jsonOpts);
            return loaded ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// 新增或按 Id（不区分大小写）覆盖写回 presets.json。
    /// fallback 只在文件缺失/损坏时作为基线，防止把内置动作整批丢掉。
    /// </summary>
    public static bool SaveOrAppend(StrokePreset preset, IReadOnlyList<StrokePreset>? fallback = null)
    {
        if (!preset.Normalize()) return false;
        var list = new List<StrokePreset>(LoadAll());
        if (list.Count == 0 && fallback != null) list.AddRange(fallback);
        int index = list.FindIndex(p => string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) list[index] = preset;
        else list.Add(preset);
        return SaveAll(list);
    }

    /// <summary>整体写回 presets.json；失败（目录只读等）返回 false。</summary>
    public static bool SaveAll(IEnumerable<StrokePreset> presets)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(presets.ToArray(), _jsonOpts));
            return true;
        }
        catch { return false; }
    }
}
