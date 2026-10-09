using System.Text.RegularExpressions;

namespace Hexa.Services;

/// <summary>The physical hardware profile confirmed for this OSR6.</summary>
public static partial class Osr6DeviceProfile
{
    public static readonly string[] InstalledAxes = ["L0", "L1", "L2", "R0", "R1", "R2"];
    public static readonly string[] UninstalledAccessoryAxes = ["V0", "V1", "A0", "A1"];

    private static readonly HashSet<string> InstalledAxisSet =
        new(InstalledAxes, StringComparer.OrdinalIgnoreCase);

    public const string WiredUsbHardwareId = "VID_1A86&PID_7523";
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
        return response.Contains("TCode", StringComparison.OrdinalIgnoreCase)
            || response.Contains("SR6", StringComparison.OrdinalIgnoreCase)
            || response.Contains("OSR6", StringComparison.OrdinalIgnoreCase);
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
}
