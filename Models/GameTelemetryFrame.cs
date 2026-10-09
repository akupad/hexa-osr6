namespace Hexa.Models;

/// <summary>
/// Engine bridges send normalized game motion here. Values are semantic and
/// device-independent; Hexa remains responsible for safety and TCode output.
/// </summary>
public sealed class GameTelemetryFrame
{
    public int ProtocolVersion { get; set; } = 1;
    public string Process { get; set; } = "";
    public string MessageType { get; set; } = "motion";
    public string Token { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long Sequence { get; set; }
    public int ProcessId { get; set; }
    public string Engine { get; set; } = "";
    public string Scene { get; set; } = "";
    public string Pose { get; set; } = "";
    public bool Active { get; set; } = true;
    public long Timestamp { get; set; }
    public double Phase { get; set; }
    public double Speed { get; set; } = 1;
    public double Depth { get; set; } = 0.5;
    public double Surge { get; set; }
    public double Sway { get; set; }
    public double Twist { get; set; }
    public double Roll { get; set; }
    public double Pitch { get; set; }
    public double Intensity { get; set; } = 1;
    public double Confidence { get; set; } = 1;
    public int TransitionMs { get; set; } = 20;
    public double[]? Axes { get; set; }

    public bool IsStop => string.Equals(MessageType, "stop", StringComparison.OrdinalIgnoreCase) || !Active;
}
