namespace Hexa.Models;

/// <summary>
/// 规则引擎单条规则。周期评估信号，命中后输出目标(自动模式 pattern, 强度)。
/// Type: audio（音频能量阈值）| gamepad（手柄活跃度阈值）| process（前台进程名匹配）。
/// Priority 越大越先判断，命中即返回。
/// </summary>
public class RuleConfig
{
    public bool   Enabled   { get; set; } = true;
    public string Id        { get; set; } = "";
    public string Type      { get; set; } = "audio";
    /// <summary>gallery（常驻）| reaction（短时覆盖）| filler（无命中时填充）。</summary>
    public string Role      { get; set; } = "reaction";
    /// <summary>audio/gamepad 规则触发阈值 [0,1]。</summary>
    public double Threshold { get; set; } = 0.6;
    public double ReleaseThreshold { get; set; } = 0.45;
    public int    MinHoldMs { get; set; } = 1800;
    /// <summary>process 规则匹配的前台进程名（不含 .exe，忽略大小写）。</summary>
    public string Match     { get; set; } = "";
    /// <summary>命中后输出的自动模式 pattern（见 MotionEngine.AutoTick）。</summary>
    public string Mode      { get; set; } = "intense_thrust";
    /// <summary>命中后输出的目标强度 [0.1, 2.0]。</summary>
    public double Intensity { get; set; } = 1.5;
    public int    Priority  { get; set; } = 0;
}
