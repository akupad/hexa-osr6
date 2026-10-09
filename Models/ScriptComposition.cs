using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hexa.Services;

namespace Hexa.Models;

/// <summary>
/// 编排里的一个动作段：引用动作库（presets.json / StrokesViewModel.AllPresets）里的一个动作，
/// 并指定这一段播放多久、强度多大、与上一段之间用多长做过渡。
/// </summary>
public class ScriptSegment
{
    public const double MinDurationSeconds   = 0.5;
    public const double MaxDurationSeconds   = 600.0;
    public const double MinIntensity         = 0.1;
    public const double MaxIntensity         = 2.0;
    public const double MinTransitionSeconds = 0.0;
    public const double MaxTransitionSeconds = 10.0;

    /// <summary>动作库里的 StrokePreset.Id。</summary>
    public string PresetId { get; set; } = "";

    /// <summary>动作显示名快照（动作库里的预设被删掉后仍能显示；导出时以 PresetId 为准）。</summary>
    public string Label { get; set; } = "";

    /// <summary>本段时长（秒），0.5–600。</summary>
    public double DurationSeconds { get; set; } = 2.0;

    /// <summary>强度倍率，乘在 Tempest 振幅上（1.0 = 动作库原始行程），0.1–2.0。</summary>
    public double Intensity { get; set; } = 1.0;

    /// <summary>
    /// 与上一段之间的过渡时长（秒），0–10。
    /// 语义：本段开头这段时间内，从上一段的末值线性过渡到本段自身的曲线值；
    /// 因此过渡不额外增加总时长（总时长恒等于各段时长之和）。
    /// </summary>
    public double TransitionSeconds { get; set; }

    public ScriptSegment Clone() => new()
    {
        PresetId          = PresetId,
        Label             = Label,
        DurationSeconds   = DurationSeconds,
        Intensity         = Intensity,
        TransitionSeconds = TransitionSeconds,
    };

    /// <summary>
    /// 夹紧到合法区间：时长 0.5–600s、强度 0.1–2.0、过渡 0–10s（且不超过本段时长）。
    /// PresetId 为空视为无效段，返回 false。
    /// </summary>
    public bool Normalize()
    {
        if (string.IsNullOrWhiteSpace(PresetId)) return false;
        PresetId          = PresetId.Trim();
        Label             = string.IsNullOrWhiteSpace(Label) ? PresetId : Label.Trim();
        DurationSeconds   = ClampFinite(DurationSeconds, MinDurationSeconds, MaxDurationSeconds, 2.0);
        Intensity         = ClampFinite(Intensity, MinIntensity, MaxIntensity, 1.0);
        TransitionSeconds = ClampFinite(TransitionSeconds, MinTransitionSeconds, MaxTransitionSeconds, 0.0);
        // 过渡长于本段时整段都会被插值覆盖，没有意义 —— 顺手夹到本段时长内
        TransitionSeconds = Math.Min(TransitionSeconds, DurationSeconds);
        return true;
    }

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// 一个「动作序列合成」：有序的动作段列表 + 名字。
/// 持久化为 %LOCALAPPDATA%/Hexa/compositions/&lt;Name&gt;.json（见 CompositionStore）。
/// </summary>
public class ScriptComposition
{
    public string Name { get; set; } = "未命名";

    /// <summary>试听/导出用的 Tempest 循环速度（BPM）。必须落盘，否则重新打开后导出结果与试听不一致。</summary>
    public int Bpm { get; set; } = 60;

    public List<ScriptSegment> Segments { get; set; } = new();

    /// <summary>各段时长之和（秒）。过渡不额外占时间，所以这就是导出总时长。</summary>
    [JsonIgnore]
    public double TotalSeconds => Segments.Sum(segment => segment.DurationSeconds);

    /// <summary>导出/序列化用：缩进 + 中文不转义（方便用户直接看/手改）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        Encoder                     = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 规范化：清洗名字、丢掉无效段（PresetId 为空 / null）、逐段夹紧。
    /// 返回自身，便于链式调用。
    /// </summary>
    public ScriptComposition Normalize()
    {
        Name = SanitizeName(Name);
        Bpm = Bpm is >= ScriptComposerService.MinBpm and <= ScriptComposerService.MaxBpm ? Bpm : 60;
        var valid = new List<ScriptSegment>(Segments?.Count ?? 0);
        if (Segments != null)
        {
            foreach (ScriptSegment? segment in Segments)
            {
                if (segment is null) continue;
                if (!segment.Normalize()) continue;
                valid.Add(segment);
            }
        }
        Segments = valid;
        return this;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>反序列化 + Normalize；JSON 语法/结构错误抛 FormatException（中文说明）。</summary>
    public static ScriptComposition FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("合成内容为空。");
        ScriptComposition? composition;
        try
        {
            composition = JsonSerializer.Deserialize<ScriptComposition>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"合成 JSON 语法错误：{ex.Message}", ex);
        }
        if (composition is null) throw new FormatException("合成 JSON 不是有效对象。");
        return composition.Normalize();
    }

    /// <summary>把名字里的非法文件名字符替换成下划线；空名回退「未命名」。</summary>
    public static string SanitizeName(string? name)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? "未命名" : name.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        var buffer = new System.Text.StringBuilder(trimmed.Length);
        foreach (char ch in trimmed)
            buffer.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        string result = buffer.ToString().Trim();
        return result.Length == 0 ? "未命名" : result;
    }
}

/// <summary>
/// 合成的本地持久化：%LOCALAPPDATA%/Hexa/compositions/&lt;Name&gt;.json，目录不存在时自动创建。
/// </summary>
public static class CompositionStore
{
    /// <summary>用户自定义目录（由设置页写入；空 = 用默认目录）。</summary>
    public static string? CustomDirectory { get; set; }

    private static string DefaultDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hexa",
        "compositions");

    public static string DirectoryPath =>
        string.IsNullOrWhiteSpace(CustomDirectory) ? DefaultDirectoryPath : CustomDirectory!;

    /// <summary>确保目录存在并返回它（UI 打开另存为对话框时用它做初始目录）。</summary>
    public static string EnsureDirectory()
    {
        Directory.CreateDirectory(DirectoryPath);
        return DirectoryPath;
    }

    /// <summary>某个合成名对应的完整文件路径（名字已做非法字符清洗）。</summary>
    public static string PathFor(string? name) =>
        Path.Combine(EnsureDirectory(), ScriptComposition.SanitizeName(name) + ".json");

    /// <summary>写入 &lt;Name&gt;.json；失败抛 IOException（中文说明）。</summary>
    public static void Save(ScriptComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        composition.Normalize();
        try
        {
            File.WriteAllText(PathFor(composition.Name), composition.ToJson());
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"写入合成文件失败：{ex.Message}", ex);
        }
    }

    /// <summary>按名字读取合成；文件不存在返回 null，解析失败抛 FormatException。</summary>
    public static ScriptComposition? Load(string? name)
    {
        string path = PathFor(name);
        if (!File.Exists(path)) return null;
        return ScriptComposition.FromJson(File.ReadAllText(path));
    }

    /// <summary>列出 compositions 目录下所有合成名（不含扩展名，按名称排序）。</summary>
    public static IReadOnlyList<string> ListNames()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return [];
            return Directory.EnumerateFiles(DirectoryPath, "*.json")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
