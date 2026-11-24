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

    /// <summary>这个游戏的底色节奏档：slow / medium / fast（空 = 跟全局）。</summary>
    public string Pace { get; set; } = "";

    /// <summary>这个游戏的播放方式：fixed（钉住一个）/ pool（在池子里轮流，空 = 跟全局）。</summary>
    public string PlayMode { get; set; } = "";

    /// <summary>这个游戏自己的动作池（空 = 跟全局）。</summary>
    public List<string> Pool { get; set; } = new();

    /// <summary>稳定 / 自然起伏（只作用在底色；空 = 跟全局）。</summary>
    public string Variation { get; set; } = "";

    /// <summary>
    /// 这个游戏是否启用「挣扎连点」；null = 跟全局（老配置没有这个字段，不能当成「关」）。
    /// </summary>
    public bool? StruggleEnabled { get; set; }

    /// <summary>这个游戏的挣扎灵敏度：low / standard / high；空 = 跟全局。</summary>
    public string StruggleSensitivity { get; set; } = "";

    /// <summary>挣扎反馈强度倍数（0 = 只加节奏不加深；独立于灵敏度）；null = 跟全局。</summary>
    public double? StruggleFeedback { get; set; }
}
