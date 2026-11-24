namespace Hexa.Services;

/// <summary>一帧节奏提示：<see cref="Valid"/> 为假时上层保持上一拍，不要突然改节奏。</summary>
public readonly record struct RhythmHint(bool Valid, double Hz, double Confidence, string Source)
{
    public static RhythmHint None => default;
}

/// <summary>
/// 声音兜底「该跟哪条节奏」的判定：先看分类，再决定用主体起音还是配乐 BPM。
/// 主体（人声 / 撞击）新鲜时用主体节奏，配乐鼓点再快也不跟；只听得出音乐时用音乐 BPM 但打八五折并压低置信度。
/// 纯函数，方便回归。
/// </summary>
public static class AudioRhythmPolicy
{
    /// <summary>主体起音间隔的可用区间（秒）——太密是噪声，太疏就不是「节奏」了。</summary>
    public const double SubjectMinIntervalSeconds = 0.18;
    public const double SubjectMaxIntervalSeconds = 1.6;
    /// <summary>多久没有新的主体起音就认为它过期。</summary>
    public const double SubjectFreshSeconds = 2.5;
    /// <summary>音乐 BPM 折算出的 Hz 至少这么高才认为「有鼓点」。</summary>
    public const double MinMusicHz = 0.25;
    /// <summary>只拿得到配乐时的打折系数。</summary>
    public const double MusicOnlyScale = 0.85;
    /// <summary>主体节奏达到配乐鼓点的这个比例就算一致，不用压。</summary>
    public const double AgreeTempoRatio = 0.75;
    /// <summary>压制下限：再不一致也留一点鼓点推着，完全清零会显得断气。</summary>
    public const double MinBeatWeight = 0.20;

    /// <summary>
    /// 配乐鼓点快、主体（人声 / 撞击）慢的时候，把鼓点推动压下来：
    /// 底色的节奏该跟着戏走，不是跟着 BGM 的拍子乱冲。置信度越高压得越狠。
    /// </summary>
    public static double BeatDamping(RhythmHint hint, double musicHz)
    {
        if (musicHz < MinMusicHz || !double.IsFinite(musicHz)) return 1;
        if (!hint.Valid || hint.Hz <= 0) return 1;
        double ratio = hint.Hz / musicHz;
        if (ratio >= AgreeTempoRatio) return 1;
        double weight = Math.Clamp(ratio, MinBeatWeight, 1.0);
        double confidence = Math.Clamp(hint.Confidence, 0, 1);
        return 1 - (1 - weight) * confidence;
    }

    public static bool SubjectFresh(double intervalSeconds, double secondsSinceSubject)
        => intervalSeconds >= SubjectMinIntervalSeconds
           && intervalSeconds <= SubjectMaxIntervalSeconds
           && secondsSinceSubject <= SubjectFreshSeconds;

    public static RhythmHint Resolve(AudioSceneKind kind, bool subjectFresh, double subjectHz, double musicHz)
    {
        bool music = musicHz >= MinMusicHz && double.IsFinite(musicHz);
        bool subject = subjectFresh && subjectHz > 0 && double.IsFinite(subjectHz);
        return kind switch
        {
            AudioSceneKind.Voice or AudioSceneKind.Impact when subject =>
                new RhythmHint(true, subjectHz, 0.85, "主体（人声 / 撞击）"),
            AudioSceneKind.Voice or AudioSceneKind.Impact when music =>
                new RhythmHint(true, musicHz * MusicOnlyScale, 0.35, "只有配乐可用（已放缓）"),
            AudioSceneKind.Music when subject =>
                new RhythmHint(true, subjectHz, 0.80, "主体压过配乐鼓点"),
            AudioSceneKind.Music when music =>
                new RhythmHint(true, musicHz * MusicOnlyScale, 0.55, "配乐鼓点（已放缓）"),
            AudioSceneKind.Mixed when subject =>
                new RhythmHint(true, subjectHz, 0.75, "主体 + 配乐，取主体"),
            AudioSceneKind.Mixed when music =>
                new RhythmHint(true, musicHz * MusicOnlyScale, 0.45, "配乐鼓点（已放缓）"),
            _ => RhythmHint.None,
        };
    }
}
