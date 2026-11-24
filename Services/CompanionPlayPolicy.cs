using Hexa.Models;

namespace Hexa.Services;

/// <summary>底色这一路的最终选择：动作、强度、播放方式、节奏档、起伏档。</summary>
public readonly record struct CompanionBaseChoice(
    string Mode, double Intensity, string PlayMode, string Pace, string Variation, IReadOnlyList<string> Pool);

/// <summary>伴随底色的播放方式：默认钉住用户选的这一个动作；只有显式选「轮流」才在池子里循环。</summary>
public enum CompanionPlayMode
{
    Fixed = 0,
    Pool = 1,
}

/// <summary>伴随底色的节奏档（慢 / 中 / 快），和强度完全分开。</summary>
public enum CompanionPace
{
    Slow = 0,
    Medium = 1,
    Fast = 2,
}

public static class CompanionPlayPolicy
{
    public const string Fixed = "fixed";
    public const string Pool = "pool";
    public const string Slow = "slow";
    public const string Medium = "medium";
    public const string Fast = "fast";
    public const string Stable = "stable";
    public const string Natural = "natural";

    /// <summary>慢档 0.62×、中档 1.0×、快档 1.55×。只缩放节拍，不碰强度。</summary>
    public static double PaceScale(string? pace) => NormalizePace(pace) switch
    {
        Slow => 0.62,
        Fast => 1.55,
        _ => 1.0,
    };

    public static string NormalizePace(string? pace)
    {
        string value = (pace ?? "").Trim().ToLowerInvariant();
        return value is Slow or Fast ? value : Medium;
    }

    public static string NormalizePlayMode(string? mode)
    {
        string value = (mode ?? "").Trim().ToLowerInvariant();
        return value == Pool ? Pool : Fixed;
    }

    public static string NormalizeVariation(string? variation)
    {
        string value = (variation ?? "").Trim().ToLowerInvariant();
        return value == Natural ? Natural : Stable;
    }

    public static string NormalizeSensitivity(string? sensitivity)
    {
        string value = (sensitivity ?? "").Trim().ToLowerInvariant();
        return value is "low" or "high" ? value : "standard";
    }

    /// <summary>
    /// 按游戏覆盖挣扎参数：profile 里 null / 空 = 跟全局（老配置没有这些字段，不能当成「这个游戏关了」）。
    /// </summary>
    public static (bool Enabled, string Sensitivity, double Feedback) ResolveGameStruggle(
        GameProfile? profile, bool globalEnabled, string? globalSensitivity, double globalFeedback)
    {
        bool enabled = profile?.StruggleEnabled ?? globalEnabled;
        string sensitivity = profile is not null && !string.IsNullOrWhiteSpace(profile.StruggleSensitivity)
            ? NormalizeSensitivity(profile.StruggleSensitivity)
            : NormalizeSensitivity(globalSensitivity);
        double feedback = profile?.StruggleFeedback is { } fb && double.IsFinite(fb)
            ? Math.Clamp(fb, 0, 1.5)
            : Math.Clamp(double.IsFinite(globalFeedback) ? globalFeedback : 1.0, 0, 1.5);
        return (enabled, sensitivity, feedback);
    }

    /// <summary>「自然起伏」只作用在底色上：深度按 23 秒一个很缓的周期在 ±22% 内呼吸，稳定档恒等于 1。</summary>
    public static double VariationDepthScale(string? variation, double seconds)
    {
        if (NormalizeVariation(variation) != Natural) return 1.0;
        double wave = Math.Sin(seconds * 2 * Math.PI / 23.0);
        return Math.Clamp(1.0 + wave * 0.22, 0.7, 1.3);
    }

    /// <summary>
    /// 固定模式下「到底跑哪个动作」。选定值优先；老配置里只有 BaseMode（可能是 free_play）时，
    /// 用池子第一条兜底，再不行就 organic_flow —— 不再让 free_play 偷偷把「固定」变成加权随机。
    /// </summary>
    public static string ResolveFixedAction(string? selectedAction, string? baseMode, IReadOnlyList<string>? pool)
    {
        string selected = (selectedAction ?? "").Trim();
        if (selected.Length > 0) return selected;
        string legacy = (baseMode ?? "").Trim();
        if (legacy.Length > 0 && !string.Equals(legacy, "free_play", StringComparison.OrdinalIgnoreCase))
            return legacy;
        if (pool is { Count: > 0 }) return pool[0];
        return "organic_flow";
    }

    /// <summary>
    /// 把「全局设置 + 这个游戏的专属配置」合成底色最终选择。
    /// 播放方式默认固定：只有显式选「轮流」且池子非空，才在池子里换动作（free_play）；
    /// 固定模式即便留着旧池子也只跑钉住的那一个，不会被随机换招盖掉。
    /// 强度与节奏分开：节奏档只缩放节拍，池子强度只作用于轮流模式。
    /// </summary>
    public static CompanionBaseChoice Resolve(AppSettings settings, string? process)
    {
        string key = (process ?? "").Trim();
        GameProfile? profile = null;
        if (key.Length > 0 && settings.GameProfiles is { } profiles) profiles.TryGetValue(key, out profile);

        string playMode = NormalizePlayMode(profile?.PlayMode is { Length: > 0 } pm ? pm : settings.CompanionPlayMode);
        IReadOnlyList<string> pool = profile?.Pool is { Count: > 0 } own ? own : settings.CompanionPool;
        string pace = NormalizePace(profile?.Pace is { Length: > 0 } p ? p : settings.CompanionPace);
        string variation = NormalizeVariation(profile?.Variation is { Length: > 0 } v ? v : settings.CompanionVariation);

        double intensity = double.IsFinite(profile?.Intensity ?? double.NaN)
            ? profile!.Intensity
            : settings.CompanionBaseIntensity;

        if (playMode == Pool && pool is { Count: > 0 })
        {
            return new CompanionBaseChoice("free_play",
                Math.Clamp(double.IsFinite(settings.CompanionPoolIntensity) ? settings.CompanionPoolIntensity : 1.0, 0.5, 1.6),
                playMode, pace, variation, pool);
        }

        // 固定模式下跑哪个：这个游戏自己存过动作就用它（用户手动保存的底色优先），否则用全局钉住的那一个。
        string legacyOrSelected = profile?.Mode is { Length: > 0 } ownMode ? ownMode : settings.CompanionSelectedAction;
        string mode = ResolveFixedAction(legacyOrSelected,
            profile?.Mode is { Length: > 0 } m ? m : settings.CompanionBaseMode, pool);
        return new CompanionBaseChoice(mode, Math.Clamp(intensity, 0.1, 2.0), playMode, pace, variation, pool);
    }

    /// <summary>池子里按用户勾选顺序循环；当前不在池子里时从第一条（或最后一条）进入。空池返回 null。</summary>
    public static string? NextInPool(IReadOnlyList<string>? pool, string? current, int direction)
    {
        if (pool is null || pool.Count == 0) return null;
        string cur = NormalizeId(current);
        int index = -1;
        for (int i = 0; i < pool.Count; i++)
        {
            if (NormalizeId(pool[i]) == cur) { index = i; break; }
        }
        int step = direction < 0 ? -1 : 1;
        int next = index < 0
            ? (step > 0 ? 0 : pool.Count - 1)
            : (index + step + pool.Count) % pool.Count;
        return pool[next];
    }

    private static string NormalizeId(string? id)
    {
        string value = (id ?? "").Trim().ToLowerInvariant();
        return value.StartsWith("stroke:", StringComparison.Ordinal) ? value["stroke:".Length..] : value;
    }

    /// <summary>状态板一句话：现在跟谁 / 在等什么 / 是不是只剩底色。</summary>
    public static string DescribeRoute(bool followScreen, bool followAudio, bool companionOn, bool waitingForGameSignal)
    {
        if (followScreen && followAudio) return "跟画面 + 声音（两路互证，同一次输出）";
        if (followScreen) return "跟画面";
        if (followAudio) return "跟声音";
        if (waitingForGameSignal) return "只用游戏信号：正在等游戏对接（不跑底色）";
        if (companionOn) return "跟底色（没接游戏也不用等）";
        return "三路都关：不替手动或脚本发动作";
    }
}
