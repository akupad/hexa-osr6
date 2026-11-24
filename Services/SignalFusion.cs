namespace Hexa.Services;

/// <summary>一路信号给设备的一组建议。Confidence 由新鲜度/判定置信度得到，0..1。</summary>
public readonly record struct SignalLane(bool Active, double Depth, double Hz, double Confidence);

/// <summary>
/// 画面 / 声音两路的融合。要点：
/// ① 双开不是「固定优先画面」也不是「画面管深度、声音管节奏」的硬分工 —— 两路各自都给出
///    深度和节奏，再按置信度互证加权；同一次输出，绝不重复驱动。
/// ② 两路都关返回空；只有一路开就用那一路，不做任何平均。
/// ③ 分歧大（快慢差 1.6 倍以上）时按置信度取强者，弱者只保留很轻的扰动，
///    避免「各说各话」被平均成一个谁都不像的中间节奏。
/// </summary>
public static class SignalFusion
{
    public const double AgreeRatio = 1.6;
    public const double MinConfidence = 0.05;

    public static (double Depth, double Hz, double Confidence, string Driver) Fuse(
        SignalLane screen, SignalLane audio, bool screenOn, bool audioOn)
    {
        if (screenOn && !audioOn) return Lane(screen.Depth, screen.Hz, screen.Confidence, "画面");
        if (audioOn && !screenOn) return Lane(audio.Depth, audio.Hz, audio.Confidence, "声音");
        if (!screenOn && !audioOn) return (0, 0, 0, "无");

        double sConf = screen.Active ? Math.Clamp(screen.Confidence, 0, 1) : 0;
        double aConf = audio.Active ? Math.Clamp(audio.Confidence, 0, 1) : 0;
        if (sConf <= MinConfidence && aConf <= MinConfidence) return (0, 0, 0, "无");
        if (sConf <= MinConfidence) return Lane(audio.Depth, audio.Hz, aConf, "声音");
        if (aConf <= MinConfidence) return Lane(screen.Depth, screen.Hz, sConf, "画面");

        double sHz = Math.Max(0.01, screen.Hz);
        double aHz = Math.Max(0.01, audio.Hz);
        double ratio = Math.Max(sHz, aHz) / Math.Min(sHz, aHz);
        double totalConf = sConf + aConf;

        // 深度：口径相同，加权平均即可（权重就是「这一路有多可信」）。
        double depth = (screen.Depth * sConf + audio.Depth * aConf) / totalConf;

        if (ratio <= AgreeRatio)
        {
            // 两路说的是同一件事：节奏取几何平均（对「谁快一点」不偏袒），置信度互相抬高。
            double hz = Math.Sqrt(sHz * aHz);
            double agreeBonus = Math.Clamp(1.0 - (ratio - 1.0) / (AgreeRatio - 1.0), 0, 1);
            return Lane(depth, hz, Math.Clamp(totalConf / 2 + 0.25 * agreeBonus, 0, 1), "画面+声音");
        }

        // 分歧：谁更可信听谁的，另一路只留 12% 的牵引（不是平均、也不是完全无视）。
        bool screenLead = sConf >= aConf;
        double leadHz = screenLead ? sHz : aHz;
        double otherHz = screenLead ? aHz : sHz;
        double hzMixed = leadHz * 0.88 + otherHz * 0.12;
        return Lane(depth, hzMixed, Math.Clamp(Math.Max(sConf, aConf) * 0.85, 0, 1),
            screenLead ? "画面（声音分歧，仅轻带）" : "声音（画面分歧，仅轻带）");
    }

    private static (double, double, double, string) Lane(double depth, double hz, double confidence, string driver)
        => (Math.Clamp(depth, 0, 100), Math.Clamp(hz, 0.01, 6.0), Math.Clamp(confidence, 0, 1), driver);
}
