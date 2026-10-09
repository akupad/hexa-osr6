namespace Hexa.Models;

/// <summary>
/// 某个游戏专用的一套设置：进入该游戏时应用（动作风格 + 强度）。
/// 存在 <see cref="AppSettings.GameProfiles"/> 里，键是游戏进程名。
/// </summary>
public sealed class GameProfile
{
    /// <summary>游戏进程名（不含 .exe）。</summary>
    public string Process { get; set; } = "";

    /// <summary>进入该游戏时使用的动作风格（与 CompanionMode 同一套 Tag）。</summary>
    public string Mode { get; set; } = "organic_flow";

    /// <summary>进入该游戏时使用的强度（0.1–2.0）。</summary>
    public double Intensity { get; set; } = 1.0;
}
