using Hexa.Models;

namespace Hexa.Services;

public static class GameCompanionRules
{
    /// <summary>同一 base 动作族内允许「只调强度」的强度差上限；|Δ强度| 小于它就当作只是强度变了。</summary>
    public const double SmoothIntensityDelta = 0.2;

    /// <summary>
    /// 动作的「base 动作族」：族内动作的运动形态相近（都是缓波 / 都是冲击），
    /// 换族才算真的换了动作，族内只算参数变化。未知动作按自身成族（保守：只有完全相同才算同族）。
    /// </summary>
    public static string BaseAction(string? mode)
    {
        string id = (mode ?? "").Trim().ToLowerInvariant();
        return id switch
        {
            "free_play" or "slow_sine" or "gentle_wave" or "organic_flow" or "game_flow" or "random_micro" => "flow",
            "deep_pulse" => "pulse",
            "spiral_tease" or "edge_swirl" => "spiral",
            "intense_thrust" or "climax_burst" => "impact",
            _ => id.Length == 0 ? "flow" : id,
        };
    }

    /// <summary>
    /// 规则切换能不能「只平滑调整、不重起模式」：同一 base 动作族 且 强度差 &lt; <see cref="SmoothIntensityDelta"/>。
    /// true → 调用侧只改 AutoPattern / IntensityScale，靠引擎的强度 ramp 与动作交叉淡入完成过渡；
    /// false → 动作形态真的换了（例如 缓波 → 冲击），是否重起由调用侧决定（重起要付一次模式重启的代价）。
    /// </summary>
    public static bool CanAdjustSmoothly(string? fromMode, double fromIntensity, string? toMode, double toIntensity)
        => string.Equals(BaseAction(fromMode), BaseAction(toMode), StringComparison.OrdinalIgnoreCase)
           && Math.Abs(SanitizeIntensity(toIntensity) - SanitizeIntensity(fromIntensity)) < SmoothIntensityDelta;

    private static double SanitizeIntensity(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>
    /// 把「游戏伴随」的设置编译成规则（companion-base / companion-reaction 两条）。
    ///
    /// 底色动作与强度取自 <see cref="AppSettings.CompanionBaseMode"/> / <see cref="AppSettings.CompanionBaseIntensity"/> ——
    /// 和 ForegroundWatcher 用的是同一套设置（界面只有一组「底色动作 / 强度」控件），
    /// 这个游戏有专属配置（<see cref="AppSettings.GameProfiles"/>）时由它覆盖，两条路的覆盖规则一致。
    ///
    /// 「激烈场面时 / 反应灵敏度」只属于声音兜底那条路：动作来源选「只用游戏信号」时 reaction 规则一律不启用，
    /// 免得环境声音把游戏画面切走。
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        string process = (settings.CompanionProcess ?? "").Trim();
        string source = settings.CompanionSource;
        string reaction = settings.CompanionReaction;
        (double threshold, double release) = settings.CompanionSensitivity switch
        {
            "low" => (0.70, 0.52),
            "high" => (0.32, 0.20),
            _ => (0.48, 0.32),
        };

        // 底色动作/强度：这个游戏有专属配置就整套换成它的（与 ForegroundWatcher 进游戏时同一套覆盖规则）。
        string baseMode = settings.CompanionBaseMode;
        double baseIntensity = settings.CompanionBaseIntensity;
        if (settings.GameProfiles is { } profiles
            && process.Length > 0
            && profiles.TryGetValue(process, out GameProfile? profile)
            && profile is not null)
        {
            if (!string.IsNullOrWhiteSpace(profile.Mode)) baseMode = profile.Mode;
            if (double.IsFinite(profile.Intensity)) baseIntensity = profile.Intensity;
        }
        baseIntensity = Math.Clamp(double.IsFinite(baseIntensity) ? baseIntensity : 1.0, 0.1, 2.0);

        lock (settings.Rules)
        {
            foreach (RuleConfig rule in settings.Rules)
                if (!rule.Id.StartsWith("companion-", StringComparison.OrdinalIgnoreCase)) rule.Enabled = false;

            RuleConfig gallery = FindOrCreate(settings.Rules, "companion-base");
            gallery.Enabled = process.Length > 0;
            gallery.Type = "process";
            gallery.Role = "gallery";
            gallery.Match = process;
            gallery.Mode = baseMode;
            gallery.Intensity = baseIntensity;
            gallery.Priority = 20;
            gallery.Threshold = 0;
            gallery.ReleaseThreshold = 0;

            RuleConfig reactionRule = FindOrCreate(settings.Rules, "companion-reaction");
            // 只用游戏信号时不听声音（auto / sound 保持原样：按「激烈场面时」是否关闭决定）。
            bool telemetryOnly = string.Equals(source, "telemetry", StringComparison.OrdinalIgnoreCase);
            reactionRule.Enabled = gallery.Enabled && reaction != "none" && !telemetryOnly;
            reactionRule.Type = "game";
            reactionRule.Role = "reaction";
            reactionRule.Match = process;
            reactionRule.Mode = reaction == "burst" ? "climax_burst" : "intense_thrust";
            reactionRule.Intensity = reaction == "burst" ? 1.55 : 1.28;
            reactionRule.Threshold = threshold;
            reactionRule.ReleaseThreshold = release;
            reactionRule.MinHoldMs = reaction == "burst" ? 1100 : 1800;
            reactionRule.Priority = 100;
        }
    }

    private static RuleConfig FindOrCreate(List<RuleConfig> rules, string id)
    {
        RuleConfig? rule = rules.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (rule != null) return rule;
        rule = new RuleConfig { Id = id };
        rules.Add(rule);
        return rule;
    }
}
