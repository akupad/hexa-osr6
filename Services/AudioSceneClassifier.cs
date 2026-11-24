namespace Hexa.Services;

/// <summary>当前这段声音像什么。用 Auto 模式时由它决定设备怎么动。</summary>
public enum AudioSceneKind
{
    /// <summary>安静（低于 −58dBFS 超过 320ms）。</summary>
    Silence = 0,
    /// <summary>人声：呻吟 / 喘息 / 说话 —— 谐波成列、中频占主导、慢起慢落。</summary>
    Voice = 1,
    /// <summary>音乐：稳定的鼓点 + 持续低音，节奏可预测。</summary>
    Music = 2,
    /// <summary>碰撞 / 拍击：宽带瞬态，低频闷响 + 高频"啪"，一下就过去。</summary>
    Impact = 3,
    /// <summary>人声和音乐同时成立（唱歌、或视频里人声叠着背景乐）。</summary>
    Mixed = 4,
}

/// <summary>
/// 一帧（10ms）的听感特征。<see cref="SpectralFluxOnset"/> 已经从同一趟 FFT 里算好了，
/// 拿过来只是搬运，不额外算谱。
/// </summary>
public readonly record struct AudioSceneFeature(
    double TimeSeconds,
    double LevelDb,          // 全带 RMS 电平 dBFS
    double SubRatio,         // 20–80Hz 占比
    double LowRatio,         // 80–200Hz 占比
    double VoiceBandRatio,   // 100–1200Hz 占比
    double HighRatio,        // 2000–8000Hz 占比
    double Flatness,         // 谱平坦度 0–1
    double Harmonics,        // 80–1000Hz 谐波峰数
    double F0Hz,             // 估计基频
    bool   VoiceLike,        // 谐波成列 + 谱不平坦
    bool   Onset,            // 这一帧是不是一次起音
    double OnsetStrength);   // 起音强度 0–1

/// <summary>
/// 声音场景分类器：把逐帧谱特征判成「呻吟 / 音乐 / 碰撞 / 安静」。
///
/// <b>为什么需要它</b>：原来只有两个口径 —— 人声模式只看中频占比（音乐里有人唱也叫），
/// 音乐模式只看低频和通量（呻吟时的喘息、被单摩擦也会推）。用户要的是「它自己听出是什么」：
/// 呻吟 → 一次发声 = 一次抽插；音乐 → 跟着鼓点律动；碰撞 → 一下一下地顶。
///
/// <b>判据（全部来自谱形，不看文件名、不看窗口标题）</b>
///   呻吟：100–1200Hz 占比高 + 谱平坦度低（谐波性强）+ 80–1000Hz 有 ≥3 条等间距谐波 +
///         基频落在 80–700Hz；起音慢、持续几百毫秒到一两秒。
///   音乐：起音间隔的变异系数小（节拍稳）、持续超过 1 秒、80–200Hz 低音常驻、2–8kHz 有镲片/齿音；
///         谐波也丰富，但基频帧间跳动大（换和弦、换音），不像一句人声那样平滑。
///   碰撞：宽带瞬态（起音 <40ms、衰减 <300ms）+ 20–80Hz 冲击 + 谱平坦度高（噪声性）+
///         谐波不成列；间隔不规则（抽插的啪啪声不是均匀节拍）。
///
/// <b>切换策略</b>：呻吟 / 碰撞是"事件型"，一确认就切（延迟 ~20ms，不然那一下就错过）；
/// 音乐是"状态型"，要连续 260ms 都判成音乐才切，避免一句喘息把节拍跟法打断。
/// 分数都做 EMA 平滑，避免单帧抖动。
///
/// 这个类不读时钟、不分配内存，只在音频采集线程上被调用（10ms 一次）。
/// </summary>
public sealed class AudioSceneClassifier
{
    /// <summary>低于这条线（−58dBFS）算"没声音"。</summary>
    public const double SilenceDb = -58.0;

    /// <summary>静音判定要连续这么久，避免句尾的气口把场景切成安静。</summary>
    private const double SilenceHoldSeconds = 0.320;

    /// <summary>音乐要连续判成音乐这么久才切（状态型）。</summary>
    private const double MusicConfirmSeconds = 0.260;

    /// <summary>分数平滑系数（每帧 10ms，0.35 ≈ 60ms 时间常数）。</summary>
    private const double ScoreSmoothing = 0.35;

    // 起音历史：判"节拍稳不稳"和"间隔乱不乱"用
    private const int OnsetCapacity = 12;
    private readonly double[] _onsetAt = new double[OnsetCapacity];
    private readonly double[] _onsetSub = new double[OnsetCapacity];
    private readonly double[] _onsetFlat = new double[OnsetCapacity];
    private readonly double[] _onsetStrength = new double[OnsetCapacity];
    private int _onsetHead, _onsetCount;

    private double _voiceScore, _musicScore, _impactScore;
    private double _impactPulse;              // 碰撞脉冲：一次冲击 → 1，之后按帧衰减
    private double _impactUntil;
    private double _voicePulse;
    private double _voiceUntil;

    private double _activeSince = -1;         // 这一轮"有声音"是从什么时候开始的
    private double _quietSince = -1;
    private double _musicCandidateSince = -1;

    private double _lastF0;
    private double _f0Jitter;
    // 高频/低频占比用「快升慢降」的保持值：音乐的镲片只有几十毫秒，逐帧取值会让「高频有料」这条
    // 大部分帧读成 0，把整段音乐的音乐分压低；呻吟的频带则是常在的，保持值不会替它加分。
    private double _highHold;
    private double _lowHold;
    private double _lastUpdate;
    private double _levelPeak = -90;
    private double _duty;

    /// <summary>最近约 0.2 秒里「电平接近峰值」的帧占比（0–1）：持续音接近 1，一下一下的碰撞很低。</summary>
    public double DutyFraction => _duty;
    private double _musicMissSeconds;
    private double _mixedSince = -1;

    /// <summary>「混合」的确认时长：比音乐短，因为它是"两边都成立"的更保守结论。</summary>
    private const double MixedConfirmSeconds = 0.15;                 // 基频帧间相对跳动的 EMA
    private double _confidence;

    private int _kind = (int)AudioSceneKind.Silence;

    /// <summary>当前判定的场景（音频线程写、界面线程读，用 volatile 保证可见）。</summary>
    public AudioSceneKind Kind => (AudioSceneKind)Volatile.Read(ref _kind);

    /// <summary>主判分数 0–1（给界面显示"有多确定"）。</summary>
    public double Confidence => _confidence;

    /// <summary>三路分数，诊断用。</summary>
    public double VoiceScore => _voiceScore;
    public double MusicScore => _musicScore;
    public double ImpactScore => _impactScore;

    /// <summary>中文标签，直接显示在界面上。</summary>
    public string Label => Kind switch
    {
        AudioSceneKind.Voice  => "呻吟 / 喘息",
        AudioSceneKind.Music  => "音乐",
        AudioSceneKind.Impact => "碰撞 / 拍击",
        AudioSceneKind.Mixed  => "人声 + 音乐",
        _                     => "安静",
    };

    /// <summary>换成一句能看懂的话（带分数），给状态行用。</summary>
    public string Describe()
    {
        if (Kind == AudioSceneKind.Silence) return "安静";
        return $"{Label}（人声 {_voiceScore:0.00} / 音乐 {_musicScore:0.00} / 碰撞 {_impactScore:0.00}）";
    }

    public void Reset()
    {
        _voiceScore = _musicScore = _impactScore = 0;
        _lastUpdate = -1;
        _levelPeak = -90;
        _duty = 0;
        _musicMissSeconds = 0;
        _mixedSince = -1;
        _impactPulse = _voicePulse = 0;
        _impactUntil = _voiceUntil = double.NegativeInfinity;
        _activeSince = _quietSince = _musicCandidateSince = -1;
        _lastF0 = 0;
        _f0Jitter = 0;
        _highHold = _lowHold = 0;
        _confidence = 0;
        _onsetHead = _onsetCount = 0;
        Array.Clear(_onsetAt);
        Volatile.Write(ref _kind, (int)AudioSceneKind.Silence);
    }

    /// <summary>喂一帧特征（每 10ms 一次）。</summary>
    public void Update(in AudioSceneFeature f)
    {
        double now = f.TimeSeconds;
        _lastUpdate = now;

        // ── 静音：连续 320ms 低于门槛才认，避免气口把状态打散 ──
        if (f.LevelDb < SilenceDb)
        {
            if (_quietSince < 0) _quietSince = now;
            _impactPulse *= 0.90;
            _voiceScore    += (0 - _voiceScore)    * ScoreSmoothing;
            _musicScore    += (0 - _musicScore)    * ScoreSmoothing;
            _impactScore   += (0 - _impactScore)   * ScoreSmoothing;
            if (now - _quietSince >= SilenceHoldSeconds)
            {
                _activeSince = -1;
                _musicCandidateSince = -1;
                _confidence = 1.0;
                Volatile.Write(ref _kind, (int)AudioSceneKind.Silence);
            }
            return;
        }
        _quietSince = -1;
        if (_activeSince < 0) _activeSince = now;

        // ── 基频稳定性：人声一句里 F0 是慢慢滑的；和弦/换音是跳的 ──
        if (f.F0Hz >= 70 && f.F0Hz <= 700)
        {
            if (_lastF0 > 0)
            {
                double jump = Math.Abs(f.F0Hz - _lastF0) / Math.Max(70.0, _lastF0);
                _f0Jitter += (jump - _f0Jitter) * 0.12;
            }
            _lastF0 = f.F0Hz;
        }
        else
        {
            _lastF0 = 0;
        }

        // ── 起音事件：记历史 + 触发碰撞 / 人声脉冲 ──
        double pulseDecay = 0.94;
        _impactPulse *= pulseDecay;
        _voicePulse *= pulseDecay;

        if (f.Onset)
        {
            PushOnset(f);
            bool impactish = f.OnsetStrength >= 0.42
                && !f.VoiceLike
                && (f.SubRatio >= 0.06 || f.HighRatio >= 0.04)
                && (f.Flatness >= 0.22 || f.Harmonics <= 2);
            if (impactish)
            {
                _impactPulse = Math.Max(_impactPulse, 0.6 + 0.4 * Math.Clamp(f.OnsetStrength, 0, 1));
                _impactUntil = now + 0.55;
            }
            if (f.VoiceLike && f.Harmonics >= 3)
            {
                _voicePulse = 1.0;
                _voiceUntil = now + 0.80;
            }
        }

        double cv = OnsetIntervalCv();
        bool steadyBeat = _onsetCount >= 5 && cv <= 0.20;

        // ── 三路分数 ──
        // 呻吟：中频占比 + 谐波成列 + 谱不平坦 + 基频在人声区
        double band = Saturate((f.VoiceBandRatio - 0.26) / 0.40);
        double harm = Saturate((f.Harmonics - 1.0) / 3.0);
        double flat = Saturate((0.40 - f.Flatness) / 0.26);
        double f0ok = f.F0Hz >= 80 && f.F0Hz <= 700 ? 1.0 : 0.30;
        double v = 0.34 * band + 0.26 * harm + 0.24 * flat + 0.16 * f0ok;
        if (f.VoiceLike) v += 0.08;
        if (now < _voiceUntil) v += 0.10 * _voicePulse;
        // 稳态节拍很强的时候压一点人声分：唱歌也会中频高、有谐波
        if (steadyBeat) v -= 0.10;
        // 低频越足越不像呻吟：鼓、贝斯、闷响都在下面，真人声的能量几乎不会压到 200Hz 以下
        v -= 0.15 * Saturate((f.LowRatio + f.SubRatio - 0.26) / 0.25);
        // 基频跳得厉害也不是呻吟：一句呻吟里基频是慢慢滑的，和弦/换音是整段跳（multitone 才有的行为）
        v -= 0.12 * Saturate((_f0Jitter - 0.03) / 0.07);
        v = Math.Clamp(v, 0, 1);

        // 占空比：音乐在几百毫秒里一直有电平；碰撞是一下一下，中间回到噪声底。
        // 没有这一条，「每 0.5 秒撞一下」会被节拍规则算成 120BPM 的音乐。
        _levelPeak = Math.Max(f.LevelDb, _levelPeak - 0.05);
        bool loud = f.LevelDb >= _levelPeak - 14;
        _duty += ((loud ? 1.0 : 0.0) - _duty) * 0.06;

        // 音乐：节拍稳 + 持续够久 + 低音常驻 + 高频有料 + 基频跳动
        _highHold = Math.Max(f.HighRatio, _highHold * 0.97);
        _lowHold = Math.Max(f.LowRatio + f.SubRatio, _lowHold * 0.97);
        double beatScore = steadyBeat ? Saturate((0.30 - cv) / 0.16) : 0.0;
        double sustain = Saturate((now - _activeSince - 0.6) / 1.2);
        double low = Saturate((_lowHold - 0.10) / 0.28);
        double high = Saturate((_highHold - 0.02) / 0.10);
        double f0Jump = Saturate((_f0Jitter - 0.02) / 0.06);
        double m = 0.34 * beatScore + 0.22 * sustain + 0.20 * low + 0.14 * high + 0.10 * f0Jump;
        m *= Saturate(0.20 + 1.10 * _duty);
        m = Math.Clamp(m, 0, 1);

        // 碰撞：脉冲 + 低频冲击 + 噪声性 + 间隔不规则
        double lowHit = Saturate((f.SubRatio - 0.05) / 0.18);
        double harsh = Saturate((f.Flatness - 0.20) / 0.40);
        double irregular = steadyBeat ? 0.20 : 1.0;
        double i = 0.55 * Math.Clamp(_impactPulse, 0, 1) + 0.20 * lowHit + 0.15 * harsh + 0.10 * irregular;
        i = Math.Clamp(i, 0, 1);

        _voiceScore  += (v - _voiceScore)  * ScoreSmoothing;
        _musicScore  += (m - _musicScore)  * ScoreSmoothing;
        _impactScore += (i - _impactScore) * ScoreSmoothing;

        // ── 判定：事件型立即切，状态型要稳定 ──
        double vs = _voiceScore, ms = _musicScore, isc = _impactScore;

        // 纯粹的噪声性瞬态（碰撞）优先：它不是音乐，也不是人声
        if (isc >= 0.58 && isc - Math.Max(vs, ms) >= 0.05 && now < _impactUntil)
        {
            SetKind(AudioSceneKind.Impact, isc);
            _musicCandidateSince = -1;
            return;
        }

        if (vs >= 0.56 && now < _voiceUntil + 0.6 && vs - ms >= 0.02)
        {
            SetKind(AudioSceneKind.Voice, vs);
            _musicCandidateSince = -1;
            return;
        }

        // 音乐先于「混合」判：ms 明显领先时就是音乐，哪怕中间几帧被和弦/鼓点顶得人声分也高。
        // 连续 0.26 秒确认，中间容许 0.12 秒的漏判（鼓点瞬间分数会抖）。
        if (ms >= 0.50 && ms - vs >= 0.06)
        {
            if (_musicCandidateSince < 0) _musicCandidateSince = now;
            _musicMissSeconds = 0;
            if (now - _musicCandidateSince >= MusicConfirmSeconds)
            {
                SetKind(AudioSceneKind.Music, ms);
                return;
            }
            _mixedSince = -1;
        }
        else
        {
            _musicMissSeconds += Math.Max(0, now - _lastUpdate);
            if (_musicMissSeconds > 0.12) _musicCandidateSince = -1;
        }

        // 混合：两边都成立、且分不开 —— 同样要连续看清一会儿，别在鼓点与人声之间来回跳
        if (vs >= 0.44 && ms >= 0.44 && Math.Abs(vs - ms) < 0.12)
        {
            if (_mixedSince < 0) _mixedSince = now;
            if (now - _mixedSince >= MixedConfirmSeconds)
            {
                SetKind(AudioSceneKind.Mixed, Math.Max(vs, ms));
                return;
            }
        }
        else
        {
            _mixedSince = -1;
        }

        // 都不够确定：保持上一次的判定（不要来回跳），分数掉光才回安静
        if (Kind != AudioSceneKind.Silence)
        {
            _confidence = Math.Max(vs, Math.Max(ms, isc));
            return;
        }
        _confidence = Math.Max(vs, Math.Max(ms, isc));
    }

    private void SetKind(AudioSceneKind kind, double confidence)
    {
        _confidence = Math.Clamp(confidence, 0, 1);
        Volatile.Write(ref _kind, (int)kind);
    }

    private static double Saturate(double x) => x <= 0 ? 0 : (x >= 1 ? 1 : x);

    private void PushOnset(in AudioSceneFeature f)
    {
        int slot = _onsetHead;
        _onsetAt[slot] = f.TimeSeconds;
        _onsetSub[slot] = f.SubRatio;
        _onsetFlat[slot] = f.Flatness;
        _onsetStrength[slot] = f.OnsetStrength;
        _onsetHead = (_onsetHead + 1) % OnsetCapacity;
        if (_onsetCount < OnsetCapacity) _onsetCount++;
    }

    /// <summary>最近几次起音间隔的变异系数（标准差 ÷ 均值）；样本不够返回 1 = 很不规则。</summary>
    private double OnsetIntervalCv()
    {
        if (_onsetCount < 4) return 1.0;
        int n = Math.Min(_onsetCount, 8);
        // 环里从旧到新取 n 个时刻
        double sum = 0, sumSq = 0;
        int gaps = 0;
        double prev = 0;
        for (int idx = 0; idx < n; idx++)
        {
            int slot = ((_onsetHead - n + idx) % OnsetCapacity + OnsetCapacity) % OnsetCapacity;
            double t = _onsetAt[slot];
            if (idx > 0)
            {
                double gap = t - prev;
                if (gap > 0.05 && gap < 4.0) { sum += gap; sumSq += gap * gap; gaps++; }
            }
            prev = t;
        }
        if (gaps < 3) return 1.0;
        double mean = sum / gaps;
        double variance = Math.Max(0, sumSq / gaps - mean * mean);
        double std = Math.Sqrt(variance);
        return mean > 1e-6 ? Math.Clamp(std / mean, 0, 3) : 1.0;
    }
}
