using System.Text.RegularExpressions;

namespace Hexa.Services;

/// <summary>The physical hardware profile confirmed for this OSR6.</summary>
public static partial class Osr6DeviceProfile
{
    public static readonly string[] InstalledAxes = ["L0", "L1", "L2", "R0", "R1", "R2"];
    public static readonly string[] UninstalledAccessoryAxes = ["V0", "V1", "A0", "A1"];

    /// <summary>
    /// 有线口的 USB 硬件 ID 白名单。两个都留：旧板是 CH340 桥（VID_1A86&PID_7523），
    /// 现用的板是 ESP32 原生 USB CDC（VID_303A&PID_1001）——只认 CH340 会让现役设备在
    /// 自动发现里拿不到「已知有线口」的加分。
    /// </summary>
    public static readonly string[] WiredUsbHardwareIds = ["VID_303A&PID_1001", "VID_1A86&PID_7523"];

    private static readonly HashSet<string> InstalledAxisSet =
        new(InstalledAxes, StringComparer.OrdinalIgnoreCase);

    public const string BluetoothDeviceName = "ESP32SPP";
    /// <summary>
    /// 蓝牙地址留空 = 只按设备名识别。
    /// 曾经把开发机那台的 MAC 写死在这里 —— 那是唯一的物理标识，**不该随代码公开**，
    /// 而且写死 MAC 也只对那一台机器有效。想按地址精确识别的话，填自己的（或用下面的设置项）。
    /// </summary>
    public const string BluetoothAddress = "";

    public static bool IsInstalledAxis(string? axisId) =>
        axisId is not null && InstalledAxisSet.Contains(axisId);

    public static bool IsCompatibleIdentityResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        // 固件把 ESP-IDF 的运行日志和 TCode 回话写在同一个 CDC 口上（实测稳态每秒两行 Stats/CPU，
        // 上电还有整段 banner）。判身份前先剔掉这些日志行 —— banner 里的 SR6Executor: 会让
        // 「回话里含 SR6」变成理所当然，等于把身份闸门弄丢。
        string cleaned = DeviceLogLineRegex().Replace(response, "");
        if (string.IsNullOrWhiteSpace(cleaned)) return false;
        return cleaned.Contains("TCode", StringComparison.OrdinalIgnoreCase)
            || cleaned.Contains("SR6", StringComparison.OrdinalIgnoreCase)
            || cleaned.Contains("OSR6", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Accept only known six-axis movement tokens and the read-only/stop device commands used by Hexa.
    /// Firmware capability reports may mention V/A channels, but this machine has no such accessories.
    /// </summary>
    public static string SanitizeTCode(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";

        var accepted = new List<string>();
        foreach (string rawToken in command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = rawToken.Trim();
            if (token is "D0" or "D1" or "D2" or "DSTOP")
            {
                accepted.Add(token);
                continue;
            }

            Match match = AxisTokenRegex().Match(token);
            if (!match.Success || !IsInstalledAxis(match.Groups["axis"].Value)) continue;

            if (!int.TryParse(match.Groups["value"].Value, out int rawPosition)) continue;
            rawPosition = Math.Clamp(rawPosition, 0, 9999);

            string timing = "";
            string timingGroup = match.Groups["timing"].Value;
            if (timingGroup.Length > 1
                && int.TryParse(timingGroup[1..], out int rawTiming))
            {
                timing = $"{timingGroup[0]}{Math.Clamp(rawTiming, 1, 9999)}";
            }

            accepted.Add($"{match.Groups["axis"].Value.ToUpperInvariant()}{rawPosition:D4}{timing}{match.Groups["gain"].Value}");
        }
        return string.Join(' ', accepted);
    }

    [GeneratedRegex("^(?<axis>[LVRAlvra][0-9])(?<value>[0-9]{1,5})(?<timing>(?:I|S)[0-9]{1,5})?(?<gain>G-?[0-9]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AxisTokenRegex();

    /// <summary>整行 ESP-IDF 日志，例如 <c>I (529688) uart: ...</c>（连行尾一起剔）。</summary>
    [GeneratedRegex(@"(?m)^[ \t]*[IEWD] \(\d+\) [^\r\n]*\r?\n?", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLogLineRegex();
}
