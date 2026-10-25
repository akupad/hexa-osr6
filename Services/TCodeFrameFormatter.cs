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
        bool changedOnly = true,
        int[]? previousInterval = null)
    {
        if (values.Length < Osr6DeviceProfile.InstalledAxes.Length) return "";

        var targets = new List<TCodeAxisTarget>(Osr6DeviceProfile.InstalledAxes.Length);
        for (int i = 0; i < Osr6DeviceProfile.InstalledAxes.Length; i++)
            targets.Add(new TCodeAxisTarget(Osr6DeviceProfile.InstalledAxes[i], values[i], interpolationMs));

        return FormatTargets(targets, axisMin, axisMax, previousRaw, previousValid, changedOnly, previousInterval);
    }

    public static string FormatTargets(
        IEnumerable<TCodeAxisTarget> targets,
        IReadOnlyDictionary<string, int> axisMin,
        IReadOnlyDictionary<string, int> axisMax,
        int[]? previousRaw = null,
        bool[]? previousValid = null,
        bool changedOnly = true,
        int[]? previousInterval = null)
    {
        var updates = new List<(int Index, int Raw, int IntervalMs)>(Osr6DeviceProfile.InstalledAxes.Length);
        foreach (TCodeAxisTarget target in targets)
        {
            int index = Array.IndexOf(Osr6DeviceProfile.InstalledAxes, target.AxisId.ToUpperInvariant());
            if (index < 0) continue;
            updates.Add((index, MapPosition(target.AxisId, target.Position, axisMin, axisMax),
                Math.Clamp(target.InterpolationMs, 1, 9999)));
        }

        // changedOnly 现在只表示「顺带刷新一次插值参数」，不再决定发几根轴：
        // 真机固件收到少轴的帧会在 20 秒内 panic 重启（灌流量实验复现），所以一律发完整帧。
        return FormatRaw(updates, previousRaw, previousValid, previousInterval,
            forceInterpolation: !changedOnly);
    }

    /// <summary>
    /// 把若干轴的原始位置并进帧状态，输出**完整帧**（已知的轴一根不少）。
    /// 插值参数只在变化时带，省掉每轴 3 个字节（30 Hz 下一帧 84 → 55 字符）。
    /// </summary>
    public static string FormatRaw(
        IReadOnlyList<(int Index, int Raw, int IntervalMs)> updates,
        int[]? previousRaw = null,
        bool[]? previousValid = null,
        int[]? previousInterval = null,
        bool forceInterpolation = false)
    {
        int axes = Osr6DeviceProfile.InstalledAxes.Length;
        var raw = new int[axes];
        var interp = new int[axes];
        var valid = new bool[axes];
        var intervalChanged = new bool[axes];
        bool tracked = previousRaw is { Length: >= 6 } && previousValid is { Length: >= 6 };
        bool trackedInterval = previousInterval is { Length: >= 6 };

        if (tracked)
        {
            for (int i = 0; i < axes; i++)
            {
                raw[i] = previousRaw![i];
                valid[i] = previousValid![i];
            }
            if (trackedInterval) Array.Copy(previousInterval!, interp, axes);
        }

        foreach ((int index, int value, int ms) in updates)
        {
            if (index < 0 || index >= axes) continue;
            raw[index] = Math.Clamp(value, 0, 9999);
            valid[index] = true;
            if (ms > 0)
            {
                int clamped = Math.Clamp(ms, 1, 9999);
                intervalChanged[index] = !trackedInterval || interp[index] != clamped;
                interp[index] = clamped;
            }
        }

        if (tracked)
        {
            for (int i = 0; i < axes; i++)
            {
                previousRaw![i] = raw[i];
                previousValid![i] = valid[i];
                if (trackedInterval) previousInterval![i] = interp[i];
            }
        }

        var parts = new List<string>(axes);
        for (int i = 0; i < axes; i++)
        {
            if (!valid[i]) continue;
            string token = Osr6DeviceProfile.InstalledAxes[i] + raw[i].ToString("D4");
            bool needInterval = forceInterpolation || !trackedInterval || intervalChanged[i] || interp[i] <= 0;
            if (needInterval) token += "I" + Math.Max(1, interp[i]).ToString();
            parts.Add(token);
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 从帧状态直接渲染一整帧：六根轴一根不少，每根都带同一个 I。
    /// 节拍器只发这种帧——真机上唯一连跑 5 分钟不崩的形态就是"固定节拍 + I 恒等于节拍"。
    /// 未知轴按中位（5000）兜底，宁可多发一根也不要发残帧。
    /// </summary>
    public static string FormatState(int[] raw, bool[] valid, int intervalMs)
    {
        int axes = Osr6DeviceProfile.InstalledAxes.Length;
        intervalMs = Math.Clamp(intervalMs, 10, 500);
        var parts = new List<string>(axes);
        for (int i = 0; i < axes; i++)
        {
            int value = i < raw.Length ? Math.Clamp(raw[i], 0, 9999) : 5000;
            bool known = i < valid.Length && valid[i];
            if (!known) value = 5000;
            parts.Add(Osr6DeviceProfile.InstalledAxes[i] + value.ToString("D4") + "I" + intervalMs);
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
