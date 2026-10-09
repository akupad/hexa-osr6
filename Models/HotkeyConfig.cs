namespace Hexa.Models;

public sealed class HotkeyConfig
{
    public const string TogglePlayback = "toggle_playback";
    public const string EmergencyStop = "emergency_stop";
    public const string NextStroke = "next_stroke";
    public const string PreviousStroke = "previous_stroke";
    public const string Burst = "burst";
    public const string IntensityUp = "intensity_up";
    public const string IntensityDown = "intensity_down";

    public Dictionary<string, string> Bindings { get; set; } = CreateDefaults();

    public static IReadOnlyList<(string Action, string Label)> Actions { get; } =
    [
        (TogglePlayback, "播放 / 暂停"),
        (EmergencyStop, "锁定急停"),
        (NextStroke, "下一个动作"),
        (PreviousStroke, "上一个动作"),
        (Burst, "爆发动作"),
        (IntensityUp, "强度 +10%"),
        (IntensityDown, "强度 -10%"),
    ];

    public void Normalize()
    {
        Bindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string action, _) in Actions)
        {
            string value = Bindings.TryGetValue(action, out string? binding)
                ? binding?.Trim() ?? ""
                : CreateDefaults()[action];
            normalized[action] = value;
        }
        Bindings = normalized;
    }

    public string Get(string action) => Bindings.TryGetValue(action, out string? binding) ? binding : "";

    public void Set(string action, string binding)
    {
        if (Actions.Any(item => item.Action == action)) Bindings[action] = binding.Trim();
    }

    private static Dictionary<string, string> CreateDefaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        [TogglePlayback] = "Ctrl+Alt+Insert",
        [EmergencyStop] = "Ctrl+Alt+End",
        [NextStroke] = "Ctrl+Alt+Home",
        [PreviousStroke] = "Ctrl+Alt+Up",
        [Burst] = "Ctrl+Alt+D",
        [IntensityUp] = "Ctrl+Alt+PageUp",
        [IntensityDown] = "Ctrl+Alt+PageDown",
    };
}
