using System.Text.RegularExpressions;

namespace Hexa.Services;

/// <summary>固件里一个设置项的值类型（对应 <c>#list-settings</c> 打印的 <c>:&lt;int&gt;</c> 这类标注）。</summary>
public enum FirmwareSettingKind
{
    Number,
    Boolean,
    Text,
    Decimal,
    Unknown,
}

/// <summary>固件设置列表里的一项：名字 + 值类型。</summary>
public sealed record FirmwareSetting(string Name, FirmwareSettingKind Kind);

/// <summary>
/// 设备固件里的设置（TCodeESP32 的串口命令：<c>#list-settings</c> / <c>#setting:名字:值</c> /
/// <c>$save</c> / <c>#restart</c>）。
///
/// 为什么界面是「填要改成多少」而不是「显示当前值」：固件把**当前值**放在它自己的网页接口里，
/// 串口协议只给了「列出可用设置」和「改设置」两条。所以这里读不到现状，也不假装读得到 ——
/// 文案必须写清楚，用户才不会以为框里的是设备现状。
///
/// 只做「读写设置」这一件事：会动设备的命令（<c>#device-home</c>、<c>#motion-*</c>、<c>#pause</c>）
/// 一条都不放进来，运动只能走引擎那条被限速器和急停管着的路（白名单在 <see cref="SerialService.IsAllowedFirmwareCommand"/>）。
/// </summary>
public static partial class FirmwareSettingsService
{
    /// <summary>
    /// 解析 <c>#list-settings</c> 的输出。
    /// 固件那边的格式是：名字 + <c>:&lt;类型&gt;</c> 左对齐补到 40 字符（空格填成 '-'），后面跟英文描述，一条一行，
    /// 例如 <c>LeftServo_ZERO:&lt;int&gt;----------------------The zero calibration for the left servo</c>。
    /// </summary>
    public static IReadOnlyList<FirmwareSetting> ParseSettingList(string? raw)
    {
        var result = new List<FirmwareSetting>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            // 先切掉填充用的 '-'（描述里可能有 '-'，所以只在 '<类型>' 之后、连续段上切）
            Match match = SettingLineRegex().Match(line);
            if (!match.Success) continue;

            string name = match.Groups["name"].Value;
            if (name.Length is 0 or > 40) continue;
            if (!seen.Add(name)) continue;
            result.Add(new FirmwareSetting(name, KindOf(match.Groups["kind"].Value)));
        }
        return result;
    }

    private static FirmwareSettingKind KindOf(string token) => token.Trim().ToLowerInvariant() switch
    {
        "int" or "number" => FirmwareSettingKind.Number,
        "bool/bit" or "bool" or "bit" or "boolean" => FirmwareSettingKind.Boolean,
        "string" => FirmwareSettingKind.Text,
        "double" or "float" => FirmwareSettingKind.Decimal,
        _ => FirmwareSettingKind.Unknown,
    };

    /// <summary>这个值类型该填什么，给界面当提示用。</summary>
    public static string FillHint(FirmwareSettingKind kind) => kind switch
    {
        FirmwareSettingKind.Number => "整数（如 1500）",
        FirmwareSettingKind.Boolean => "开 / 关",
        FirmwareSettingKind.Text => "文字",
        FirmwareSettingKind.Decimal => "小数（如 1.5）",
        _ => "按固件要求填",
    };

    /// <summary>校验用户输入的值是否符合该类型；返回 null = 通过，否则是给用户看的原因。</summary>
    public static string? Validate(FirmwareSettingKind kind, string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0) return "还没填值";
        if (text.Length > 64) return "太长了（最多 64 字符）";
        return kind switch
        {
            FirmwareSettingKind.Number =>
                int.TryParse(text, out _) ? null : "要填整数",
            FirmwareSettingKind.Decimal =>
                double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _) ? null : "要填数字",
            FirmwareSettingKind.Boolean =>
                text is "0" or "1" or "true" or "false" or "on" or "off" ? null : "只能填 0 / 1",
            _ => null,
        };
    }

    /// <summary>
    /// SR6 上真正会用到的那几项：固件里的原名 → 中文名 + 提示。
    /// 名字来自固件源码（lib/settingConstants.h），提示里的默认值也来自源码（*_DEFAULT）。
    /// 只在设备真的列出了这一项时才会显示，所以不同固件版本都不会显示成假的。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string Hint)> CuratedSr6Settings =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["LeftServo_ZERO"] = ("左舵机零点（行程左臂）", "默认 1500 · 改完要重启"),
            ["RightServo_ZERO"] = ("右舵机零点（行程右臂）", "默认 1500 · 改完要重启"),
            ["LeftUpperServo_ZERO"] = ("左上舵机零点", "默认 1500 · 改完要重启"),
            ["RightUpperServo_ZERO"] = ("右上舵机零点", "默认 1500 · 改完要重启"),
            ["PitchLeftServo_ZERO"] = ("俯仰左舵机零点", "默认 1500 · 改完要重启"),
            ["PitchRightServo_ZERO"] = ("俯仰右舵机零点", "默认 1500 · 改完要重启"),
            ["TwistServo_ZERO"] = ("扭转舵机零点", "默认 1500 · 改完要重启"),
            ["maxServoRange"] = ("舵机最大角度", "默认 180"),
            ["inverseStroke"] = ("反转行程方向", "0 = 不反转，1 = 反转"),
            ["inversePitch"] = ("反转俯仰方向", "0 = 不反转，1 = 反转"),
            ["inverseTwist"] = ("反转扭转方向", "0 = 不反转，1 = 反转"),
            ["continuousTwist"] = ("连续扭转", "1 = 扭转轴可以一直转，0 = 限位内"),
            ["bluetoothEnabled"] = ("蓝牙串口（SPP）", "1 = 开（手机上那种蓝牙连接）"),
            ["bleEnabled"] = ("BLE 蓝牙", "1 = 开（低功耗蓝牙）"),
            ["friendlyName"] = ("设备名", "网页 / 蓝牙里显示的名字"),
            ["hostname"] = ("主机名", "连着 WiFi 时用 <名字>.local 访问"),
            ["vibTimeout"] = ("振动超时（毫秒）", "默认 2000 · 到点自动停振动"),
        };

    /// <summary>把一行行设置拆成「常用（SR6 相关）」和「其它」，界面按这个分组显示。</summary>
    public static (IReadOnlyList<FirmwareSetting> Common, IReadOnlyList<FirmwareSetting> Others) SplitByCurated(
        IReadOnlyList<FirmwareSetting> settings)
    {
        var common = new List<FirmwareSetting>();
        var others = new List<FirmwareSetting>();
        foreach (FirmwareSetting setting in settings)
        {
            if (CuratedSr6Settings.ContainsKey(setting.Name)) common.Add(setting);
            else others.Add(setting);
        }
        // 常用项按中文名排序，读起来顺；其它项按固件给的顺序（本来就是分文件分组的）。
        common.Sort((a, b) => string.CompareOrdinal(
            CuratedSr6Settings[a.Name].Label, CuratedSr6Settings[b.Name].Label));
        return (common, others);
    }

    /// <summary>
    /// 固件的设置行：<c>名字:&lt;类型&gt;</c>。类型里可能有 '/'（bool/bit），名字允许字母数字下划线点和横线。
    /// </summary>
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_.\-]+):<(?<kind>[a-zA-Z/]+)>",
        RegexOptions.CultureInvariant)]
    private static partial Regex SettingLineRegex();
}
