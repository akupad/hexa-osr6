using Hexa.Models;
using Hexa.Services;

static class Audit
{
    const int W = 320, H = 180;
    static byte[] Frame(int centerX, bool textured = true, bool duplicate = false)
    {
        var frame = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { frame[i * 4] = 12; frame[i * 4 + 1] = 12; frame[i * 4 + 2] = 12; frame[i * 4 + 3] = 255; }
        if (textured) { Stamp(frame, centerX, 90); if (duplicate) Stamp(frame, Math.Min(W - 25, centerX + 48), 90); }
        return frame;
    }
    static void Stamp(byte[] f, int cx, int cy)
    {
        for (int y = -15; y <= 15; y++) for (int x = -15; x <= 15; x++)
        {
            uint v = unchecked((uint)((x + 17) * 73856093 ^ (y + 19) * 19349663));
            v ^= v >> 13; v *= 1274126177; v ^= v >> 16;
            int i = ((cy + y) * W + cx + x) * 4;
            byte c = (byte)(24 + v % 224); f[i] = c; f[i + 1] = (byte)(c ^ 0x57); f[i + 2] = (byte)(255 - c); f[i + 3] = 255;
        }
    }
    static void Check(bool ok, string msg) { if (!ok) throw new Exception(msg); Console.WriteLine("PASS " + msg); }
    static int Main()
    {
        var tracker = new ManualPointTracker();
        byte[] first = Frame(160);
        bool ok = tracker.TryBind(first,
            new(160.0 / 319, 90.0 / 179), new(100.0 / 319, 90.0 / 179), new(220.0 / 319, 90.0 / 179), out string reason);
        Check(ok, "bind textured target: " + reason);
        var match = tracker.SubmitBgra(Frame(172));
        Check(match.Located && !match.Lost && Math.Abs(match.X * 319 - 172) <= 2, $"track +12px to x={match.X * 319:0.0}, score={match.Confidence:0.000}");
        Check(match.Position is > 0.55 and < 0.66, $"projected range position={match.Position:0.000}");
        var next = tracker.SubmitBgra(Frame(184));
        Check(next.Located && Math.Abs(next.X * 319 - 184) <= 2, $"track +24px to x={next.X * 319:0.0}");
        var lost = tracker.SubmitBgra(Frame(184, textured:false));
        Check(lost.Lost && !lost.Located, "lost target freezes manual binding instead of reacquiring automatically");
        tracker.Clear(); Check(!tracker.Signal.Bound, "one click returns to automatic");
        var ambiguous = new ManualPointTracker();
        Check(ambiguous.TryBind(Frame(160), new(160.0/319,90.0/179),new(100.0/319,90.0/179),new(220.0/319,90.0/179),out reason), "bind before ambiguity test");
        var ambiguousResult=ambiguous.SubmitBgra(Frame(160,duplicate:true));
        Check(ambiguousResult.Lost, "similar duplicate targets trigger explicit rebind prompt");
        // 服务层：绑定只认“手里这一帧够不够新、还是不是同一个窗口”，不再要求等于后台最新帧的时间戳。
        var cfg = new AppSettings { ScreenWatchTarget = "fixture.exe", ScreenWatchEnabled = true };
        using var service = new ScreenWatchService(cfg);
        long now = Environment.TickCount64;
        ScreenWatchService.ManualBindingFrame Snapshot(byte[] pixels, long atMs, string target = "fixture.exe")
            => new(null!, pixels, (IntPtr)0x1234, target, "fixture", W, H, atMs);
        var pick = new ManualTrackPoint(160.0 / 319, 90.0 / 179);
        var near = new ManualTrackPoint(100.0 / 319, 90.0 / 179);
        var far = new ManualTrackPoint(220.0 / 319, 90.0 / 179);
        Check(service.TryBindManual(Snapshot(Frame(160), now - 900), pick, near, far, out string bindMessage)
            && service.ManualBinding.Bound, "fresh frozen frame binds without a newer preview: " + bindMessage);
        service.ClearManualBinding();
        Check(!service.TryBindManual(Snapshot(Frame(160), now - 5000), pick, near, far, out string staleMessage)
            && staleMessage.Contains("太旧"), "stale frozen frame is refused: " + staleMessage);
        Check(!service.TryBindManual(Snapshot(Frame(160), now - 400, "other.exe"), pick, near, far, out string otherTarget)
            && otherTarget.Contains("目标已经换了"), "frame from another target is refused: " + otherTarget);
        service.ClearManualBinding();
        Console.WriteLine("manual tracker synthetic audit passed"); return 0;
    }
}
