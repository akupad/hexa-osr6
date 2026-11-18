using System;

namespace Hexa.Services;

/// <summary>
/// Lovense 双通道状态：主玩具（osr6hexa → 抽插 L0）和转动玩具（osr6rotate/nora → R0）各跑各的。
/// LovenseBridgeService.Apply 与单元测试共用这份纯合并逻辑：定向命令只动自己那一路，绝不互相清零。
/// </summary>
public readonly record struct LovenseChannels(
    string StrokeAction,
    double StrokeAmplitude,
    double StrokeHz,
    double StrokeSeconds,
    double StrokeLoopRunSec,
    double StrokeLoopPauseSec,
    int StrokeTopOverride,
    int StrokeDeepOverride,
    string RotateAction,
    double RotateAmplitude,
    double RotateHz,
    double RotateSeconds)
{
    public static LovenseChannels Empty => new("", 0, 0, 0, 0, 0, -1, -1, "", 0, 0, 0);

    public bool StrokeActive => StrokeAction.Length > 0 && StrokeAction != "Stop";
    public bool RotateActive => RotateAction.Length > 0 && RotateAction != "Stop";

    public static LovenseChannels Merge(LovenseChannels current, LovenseProtocol.Outcome o)
    {
        if (!o.Ok || o.Action.Length == 0) return current;
        return o.Target switch
        {
            LovenseProtocol.ToyTarget.Main => MergeMain(current, o),
            LovenseProtocol.ToyTarget.Rotate => MergeRotate(current, o),
            _ => MergeAll(current, o)
        };
    }

    private static LovenseChannels MergeAll(LovenseChannels c, LovenseProtocol.Outcome o)
    {
        // 全部玩具：保持旧版整机语义——停就全停，stopPrevious:0 不打断当前动作。
        if (o.Action == "Stop" || (o.Amplitude <= 0 && o.RotateAmplitude <= 0))
            return ClearStroke(ClearRotate(c));
        if (!o.StopPrevious && (c.StrokeActive || c.RotateActive)) return c;
        return c with
        {
            StrokeAction = o.Action,
            StrokeAmplitude = o.Amplitude,
            StrokeHz = o.Hz,
            StrokeSeconds = o.Seconds,
            StrokeLoopRunSec = o.LoopRunningSec,
            StrokeLoopPauseSec = o.LoopPauseSec,
            StrokeTopOverride = o.StrokeMin >= 0 ? (int)Math.Round(o.StrokeMin) : -1,
            StrokeDeepOverride = o.StrokeMax >= 0 ? (int)Math.Round(o.StrokeMax) : -1,
            RotateAction = o.Action,
            RotateAmplitude = o.RotateAmplitude,
            RotateHz = o.RotateHz,
            RotateSeconds = o.Seconds
        };
    }

    private static LovenseChannels MergeMain(LovenseChannels c, LovenseProtocol.Outcome o)
    {
        // 主玩具：只动抽插；命令自己带 Rotate 时也写转动通道（主玩具自带的 Rotate）。
        // Stop / 纯转动（stopPrevious:1）只清抽插那一路，绝不碰 nora 正在跑的转动。
        if (o.Action == "Stop") return ClearStroke(c);
        if (!o.StopPrevious && c.StrokeActive) return c;
        LovenseChannels n = o.Amplitude > 0
            ? c with
            {
                StrokeAction = o.Action,
                StrokeAmplitude = o.Amplitude,
                StrokeHz = o.Hz,
                StrokeSeconds = o.Seconds,
                StrokeLoopRunSec = o.LoopRunningSec,
                StrokeLoopPauseSec = o.LoopPauseSec,
                StrokeTopOverride = o.StrokeMin >= 0 ? (int)Math.Round(o.StrokeMin) : -1,
                StrokeDeepOverride = o.StrokeMax >= 0 ? (int)Math.Round(o.StrokeMax) : -1
            }
            : ClearStroke(c);
        if (o.RotateAmplitude > 0)
            n = n with { RotateAction = o.Action, RotateAmplitude = o.RotateAmplitude, RotateHz = o.RotateHz, RotateSeconds = o.Seconds };
        return n;
    }

    private static LovenseChannels MergeRotate(LovenseChannels c, LovenseProtocol.Outcome o)
    {
        // 转动玩具（nora）：只动 R0，Vibrate/Rotate 都只当转动强度；Stop 只停转动。
        if (o.Action == "Stop") return ClearRotate(c);
        if (!o.StopPrevious && c.RotateActive) return c;
        return c with
        {
            RotateAction = o.Action,
            RotateAmplitude = o.RotateAmplitude,
            RotateHz = o.RotateHz,
            RotateSeconds = o.Seconds
        };
    }

    private static LovenseChannels ClearStroke(LovenseChannels c) => c with
    {
        StrokeAction = "", StrokeAmplitude = 0, StrokeHz = 0, StrokeSeconds = 0,
        StrokeLoopRunSec = 0, StrokeLoopPauseSec = 0, StrokeTopOverride = -1, StrokeDeepOverride = -1
    };

    private static LovenseChannels ClearRotate(LovenseChannels c) => c with
    {
        RotateAction = "", RotateAmplitude = 0, RotateHz = 0, RotateSeconds = 0
    };
}
