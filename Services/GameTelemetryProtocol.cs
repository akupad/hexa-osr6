using System.Text.Json;
using System.Text.RegularExpressions;
using Hexa.Models;

namespace Hexa.Services;

public static partial class GameTelemetryProtocol
{
    public const int MaxPacketBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out GameTelemetryFrame frame)
    {
        frame = new GameTelemetryFrame();
        if (utf8Json.Length is 0 or > MaxPacketBytes) return false;
        try
        {
            GameTelemetryFrame? parsed = JsonSerializer.Deserialize<GameTelemetryFrame>(utf8Json, JsonOptions);
            if (parsed == null) return false;
            frame = Normalize(parsed);
            return frame.Process.Length > 0
                && frame.Process != "*"
                && (frame.IsStop || frame.Confidence > 0);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseTCode(
        ReadOnlySpan<byte> packet,
        IReadOnlyList<double> previousAxes,
        out GameTelemetryFrame frame)
    {
        frame = new GameTelemetryFrame();
        if (packet.Length is 0 or > MaxPacketBytes || previousAxes.Count < 6) return false;
        string text;
        try { text = System.Text.Encoding.ASCII.GetString(packet).Trim(); }
        catch { return false; }
        string safe = Osr6DeviceProfile.SanitizeTCode(text);
        if (safe.Length == 0) return false;

        double[] axes = previousAxes.Take(6).ToArray();
        bool changed = false;
        bool stopRequested = false;
        int transitionMs = 20;
        int? commonTransitionMs = null;
        foreach (string token in safe.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "DSTOP")
            {
                stopRequested = true;
                continue;
            }
            Match match = TCodeAxisRegex().Match(token);
            if (!match.Success) continue;
            int index = Array.IndexOf(Osr6DeviceProfile.InstalledAxes, match.Groups["axis"].Value);
            if (index < 0 || !int.TryParse(match.Groups["value"].Value, out int raw)) continue;
            axes[index] = Math.Clamp(raw, 0, 9999) / 99.99;
            string timing = match.Groups["timing"].Value;
            if (match.Groups["gain"].Success) return false;
            if (timing.StartsWith("S", StringComparison.OrdinalIgnoreCase)) return false;
            if (timing.StartsWith("I", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(timing[1..], out int parsedTiming))
            {
                int clamped = Math.Clamp(parsedTiming, 1, 9999);
                if (commonTransitionMs.HasValue && commonTransitionMs.Value != clamped) return false;
                commonTransitionMs = clamped;
                transitionMs = clamped;
            }
            changed = true;
        }
        if (!changed && !stopRequested) return false;

        frame = new GameTelemetryFrame
        {
            Process = "*",
            MessageType = stopRequested ? "stop" : "motion",
            Engine = "TCode UDP",
            Active = !stopRequested,
            Confidence = 1,
            Intensity = 1,
            TransitionMs = transitionMs,
            Axes = axes,
        };
        return true;
    }

    public static GameTelemetryFrame Normalize(GameTelemetryFrame frame)
    {
        frame.ProtocolVersion = Math.Max(0, frame.ProtocolVersion);
        frame.Process = NormalizeProcessName(frame.Process);
        frame.MessageType = string.Equals(frame.MessageType?.Trim(), "stop", StringComparison.OrdinalIgnoreCase)
            || !frame.Active ? "stop" : "motion";
        frame.Token = CleanText(frame.Token, 128);
        frame.SessionId = CleanText(frame.SessionId, 128);
        frame.Sequence = Math.Max(0, frame.Sequence);
        frame.ProcessId = Math.Max(0, frame.ProcessId);
        frame.Engine = CleanText(frame.Engine, 64);
        frame.Scene = CleanText(frame.Scene, 160);
        frame.Pose = CleanText(frame.Pose, 160);
        frame.Phase = Unit(frame.Phase);
        frame.Speed = FiniteClamp(frame.Speed, 0, 8, 1);
        frame.Depth = Unit(frame.Depth);
        frame.Surge = Signed(frame.Surge);
        frame.Sway = Signed(frame.Sway);
        frame.Twist = Signed(frame.Twist);
        frame.Roll = Signed(frame.Roll);
        frame.Pitch = Signed(frame.Pitch);
        frame.Intensity = FiniteClamp(frame.Intensity, 0, 2, 1);
        frame.Confidence = Unit(frame.Confidence);
        frame.TransitionMs = Math.Clamp(frame.TransitionMs, 1, 9999);
        frame.Active = frame.MessageType != "stop";
        if (frame.Axes is { Length: >= 6 })
            frame.Axes = frame.Axes.Take(6).Select(value => FiniteClamp(value, 0, 100, 50)).ToArray();
        else
            frame.Axes = null;
        return frame;
    }

    public static double[] ToAxes(GameTelemetryFrame frame)
        => ToAxes(frame, applyIntensity: true);

    public static double[] ToAxes(GameTelemetryFrame frame, bool applyIntensity)
    {
        GameTelemetryFrame safe = Normalize(frame);
        double[] axes = safe.Axes?.ToArray() ??
        [
            safe.Depth * 100,
            50 + safe.Surge * 50,
            50 + safe.Sway * 50,
            50 + safe.Twist * 50,
            50 + safe.Roll * 50,
            50 + safe.Pitch * 50,
        ];
        double intensity = applyIntensity && safe.Active ? safe.Intensity : 1;
        for (int i = 0; i < axes.Length; i++)
            axes[i] = Math.Clamp(50 + (axes[i] - 50) * intensity, 0, 100);
        return axes;
    }

    public static string NormalizeProcessName(string? value)
    {
        string name = Path.GetFileName((value ?? "").Trim());
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return CleanText(name, 128);
    }

    private static string CleanText(string? value, int maxLength)
    {
        string clean = (value ?? "").Trim();
        if (clean.Length > maxLength) clean = clean[..maxLength];
        return clean;
    }

    private static double Unit(double value) => FiniteClamp(value, 0, 1, 0);
    private static double Signed(double value) => FiniteClamp(value, -1, 1, 0);

    private static double FiniteClamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    [GeneratedRegex(@"^(?<axis>L[012]|R[012])(?<value>\d{4})(?<timing>(?:I|S)\d{1,4})?(?<gain>G-?\d+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TCodeAxisRegex();
}
