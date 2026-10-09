namespace Hexa.Models;

/// <summary>
/// 手柄→轴映射配置。每个轴可选来源输入、独立反向和共享死区。
/// </summary>
public class GamepadConfig
{
    // ── 可用来源名称常量（方便 UI 枚举） ──
    public static readonly string[] Sources =
        ["RightTrigger", "LeftTrigger", "LeftStickY", "LeftStickX", "RightStickY", "RightStickX"];

    /// <summary>6 轴来源映射，索引对应 [L0, L1, L2, R0, R1, R2]</summary>
    public string[] AxisSources { get; set; } =
        ["RightTrigger", "LeftStickY", "LeftStickX", "LeftTrigger", "RightStickY", "RightStickX"];

    /// <summary>每轴独立反向（true = 翻转 0↔100）</summary>
    public bool[] AxisInvert { get; set; } = new bool[6];

    /// <summary>死区比例 0–1（对摇杆/扳机统一生效）</summary>
    public double Deadzone { get; set; } = 0.12;

    /// <summary>
    /// 从原始 XInput 值字典中取出指定轴的归一化值 [0, 100]，已应用死区与反向。
    /// raw 键：LeftTrigger/RightTrigger 范围 0–255；摇杆范围 -32767–32767。
    /// </summary>
    public double ReadAxis(int axisIndex, Dictionary<string, double> raw)
    {
        if (axisIndex < 0 || axisIndex >= AxisSources.Length) return 50;
        string src = AxisSources[axisIndex];
        if (!raw.TryGetValue(src, out double v)) return 50;

        double norm; // 归一化至 0–1
        if (src is "LeftTrigger" or "RightTrigger")
        {
            norm = v / 255.0;
            norm = norm < Deadzone ? 0 : (norm - Deadzone) / (1.0 - Deadzone);
        }
        else
        {
            // 摇杆：-1 到 +1 → 映射到 0–1（中心 = 0.5）
            double n = v / 32767.0;
            double sign = Math.Sign(n);
            double abs  = Math.Abs(n);
            abs = abs < Deadzone ? 0 : (abs - Deadzone) / (1.0 - Deadzone);
            norm = (sign * abs + 1.0) / 2.0;
        }

        if (AxisInvert[axisIndex]) norm = 1.0 - norm;
        return Math.Clamp(norm * 100.0, 0, 100);
    }
}
