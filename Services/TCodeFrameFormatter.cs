namespace Hexa.Services;

public readonly record struct TCodeAxisTarget(string AxisId, double Position, int InterpolationMs);

/// <summary>Formats mapped, multi-axis TCode frames for the confirmed six-axis device.</summary>
public static class TCodeFrameFormatter
{
    public static string FormatAxes(
        double[] values,
        IReadOnlyDictionary<string, int> axisMin,
        IReadOnlyDictionary<string, int> axisMax,
        int interpolationMs,
        int[]? previousRaw = null,
        bool[]? previousValid = null,
        bool changedOnly = true)
    {
        if (values.Length < Osr6DeviceProfile.InstalledAxes.Length) return "";

        var targets = new List<TCodeAxisTarget>(Osr6DeviceProfile.InstalledAxes.Length);
        for (int i = 0; i < Osr6DeviceProfile.InstalledAxes.Length; i++)
            targets.Add(new TCodeAxisTarget(Osr6DeviceProfile.InstalledAxes[i], values[i], interpolationMs));

        return FormatTargets(targets, axisMin, axisMax, previousRaw, previousValid, changedOnly);
    }

    public static string FormatTargets(
        IEnumerable<TCodeAxisTarget> targets,
        IReadOnlyDictionary<string, int> axisMin,
        IReadOnlyDictionary<string, int> axisMax,
        int[]? previousRaw = null,
        bool[]? previousValid = null,
        bool changedOnly = true)
    {
        var parts = new List<string>(Osr6DeviceProfile.InstalledAxes.Length);
        foreach (TCodeAxisTarget target in targets)
        {
            int index = Array.IndexOf(Osr6DeviceProfile.InstalledAxes, target.AxisId.ToUpperInvariant());
            if (index < 0) continue;

            int raw = MapPosition(target.AxisId, target.Position, axisMin, axisMax);
            bool unchanged = previousRaw is { Length: >= 6 }
                && previousValid is { Length: >= 6 }
                && previousValid[index]
                && previousRaw[index] == raw;

            if (previousRaw is { Length: >= 6 }) previousRaw[index] = raw;
            if (previousValid is { Length: >= 6 }) previousValid[index] = true;
            if (changedOnly && unchanged) continue;

            int interpolation = Math.Clamp(target.InterpolationMs, 1, 9999);
            parts.Add($"{Osr6DeviceProfile.InstalledAxes[index]}{raw:D4}I{interpolation}");
        }
        return string.Join(' ', parts);
    }

    public static int MapPosition(
        string axisId,
        double position,
        IReadOnlyDictionary<string, int> axisMin,
        IReadOnlyDictionary<string, int> axisMax)
    {
        int low = Math.Clamp(axisMin.TryGetValue(axisId, out int min) ? min : 0, 0, 9999);
        int high = Math.Clamp(axisMax.TryGetValue(axisId, out int max) ? max : 9999, 0, 9999);
        if (low > high) (low, high) = (high, low);
        double normalized = Math.Clamp(double.IsFinite(position) ? position : 50, 0, 100);
        return (int)Math.Round(low + normalized / 100.0 * (high - low));
    }
}
