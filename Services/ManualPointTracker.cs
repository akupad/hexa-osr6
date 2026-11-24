namespace Hexa.Services;

public readonly record struct ManualTrackPoint(double X, double Y);
public readonly record struct ManualTrackSignal(bool Bound, bool Located, bool Lost,
    double X, double Y, double Position, double Confidence, string Status);

/// <summary>只匹配用户指定的小块纹理；固定模板，丢失后锁停，直到用户重新绑定。</summary>
public sealed class ManualPointTracker(int width = 320, int height = 180)
{
    private const int Radius = 6;
    private const int Side = Radius * 2 + 1;
    private const int Count = Side * Side;
    private const int CoarseSide = Radius + 1;
    private const int CoarseCount = CoarseSide * CoarseSide;
    private readonly byte[] _gray = new byte[width * height];
    private readonly double[] _template = new double[Count];
    private readonly double[] _coarseTemplate = new double[CoarseCount];
    private double _templateNorm, _coarseTemplateNorm;
    private double _ax, _ay, _bx, _by, _lengthSquared;
    private int _x, _y;
    private bool _first;
    public ManualTrackSignal Signal { get; private set; } = new(false, false, false, 0, 0, 0, 0, "自动识别");

    public bool TryBind(byte[] bgra, ManualTrackPoint target, ManualTrackPoint shallow,
        ManualTrackPoint deep, out string message)
    {
        message = "";
        if (bgra.Length != width * height * 4 || !Valid(target) || !Valid(shallow) || !Valid(deep))
        { message = "画面或坐标已失效，请重新取图"; return false; }
        int x = (int)Math.Round(target.X * (width - 1)), y = (int)Math.Round(target.Y * (height - 1));
        if (x < Radius || x >= width - Radius || y < Radius || y >= height - Radius)
        { message = "目标太靠边，请选离边缘稍远的轮廓或细节"; return false; }
        double ax = shallow.X * (width - 1), ay = shallow.Y * (height - 1);
        double bx = deep.X * (width - 1), by = deep.Y * (height - 1);
        double lengthSquared = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
        if (lengthSquared < 64)
        { message = "两端太近，请把来回运动的两端标开一点"; return false; }
        ConvertGray(bgra);
        double sum = 0, sum2 = 0;
        for (int dy = -Radius; dy <= Radius; dy++)
            for (int dx = -Radius; dx <= Radius; dx++)
            { double value = _gray[(y + dy) * width + x + dx]; sum += value; sum2 += value * value; }
        double norm = Math.Sqrt(Math.Max(0, sum2 - sum * sum / Count));
        if (norm / Math.Sqrt(Count) < 10)
        { message = "这块太平，换个有轮廓、花纹或明暗交界的位置"; return false; }
        double corridor = Math.Max(14, Math.Min(28, Math.Sqrt(lengthSquared) * 0.25));
        if (DistanceToSegment(x, y, ax, ay, bx, by, lengthSquared) > corridor)
        { message = "目标不在标出的运动路线附近，请重标两端"; return false; }
        _ax = ax; _ay = ay; _bx = bx; _by = by; _lengthSquared = lengthSquared;
        int index = 0;
        for (int dy = -Radius; dy <= Radius; dy++)
            for (int dx = -Radius; dx <= Radius; dx++)
                _template[index++] = _gray[(y + dy) * width + x + dx] - sum / Count;
        _templateNorm = norm;
        double coarseSum = 0, coarseSum2 = 0;
        for (int dy = -Radius; dy <= Radius; dy += 2)
            for (int dx = -Radius; dx <= Radius; dx += 2)
            { double value = _gray[(y + dy) * width + x + dx]; coarseSum += value; coarseSum2 += value * value; }
        double coarseMean = coarseSum / CoarseCount;
        _coarseTemplateNorm = Math.Sqrt(Math.Max(0, coarseSum2 - coarseSum * coarseSum / CoarseCount));
        index = 0;
        for (int dy = -Radius; dy <= Radius; dy += 2)
            for (int dx = -Radius; dx <= Radius; dx += 2)
                _coarseTemplate[index++] = _gray[(y + dy) * width + x + dx] - coarseMean;
        _x = x; _y = y; _first = true;
        Signal = new(true, false, false, target.X, target.Y, Projection(x, y), 1, "已绑定，等下一帧确认");
        return true;
    }

    public ManualTrackSignal SubmitBgra(byte[] bgra)
    {
        if (!Signal.Bound || Signal.Lost) return Signal;
        if (bgra.Length != width * height * 4) { Invalidate("画面尺寸变了，请重绑"); return Signal; }
        ConvertGray(bgra);
        double corridor = Math.Max(14, Math.Min(28, Math.Sqrt(_lengthSquared) * 0.25));
        int minX = Math.Max(Radius, (int)Math.Floor(Math.Min(_ax, _bx) - corridor));
        int maxX = Math.Min(width - Radius - 1, (int)Math.Ceiling(Math.Max(_ax, _bx) + corridor));
        int minY = Math.Max(Radius, (int)Math.Floor(Math.Min(_ay, _by) - corridor));
        int maxY = Math.Min(height - Radius - 1, (int)Math.Ceiling(Math.Max(_ay, _by) + corridor));
        if (!_first)
        {
            minX = Math.Max(minX, _x - 24); maxX = Math.Min(maxX, _x + 24);
            minY = Math.Max(minY, _y - 24); maxY = Math.Min(maxY, _y + 24);
        }
        double best = -1;
        int bestX = _x, bestY = _y;
        // 先用稀疏模板找若干高分候选，再以完整模板逐像素精化；奇数像素位移也会被覆盖。
        Span<int> candidateX = stackalloc int[8];
        Span<int> candidateY = stackalloc int[8];
        Span<double> candidateScore = stackalloc double[8];
        candidateScore.Fill(-1);
        for (int y = minY; y <= maxY; y += 2)
            for (int x = minX; x <= maxX; x += 2)
            {
                if (DistanceToSegment(x, y, _ax, _ay, _bx, _by, _lengthSquared) > corridor) continue;
                double score = CoarseScore(x, y);
                if (score <= candidateScore[7]) continue;
                int at = 7;
                while (at > 0 && score > candidateScore[at - 1])
                {
                    candidateScore[at] = candidateScore[at - 1];
                    candidateX[at] = candidateX[at - 1];
                    candidateY[at] = candidateY[at - 1];
                    at--;
                }
                candidateScore[at] = score; candidateX[at] = x; candidateY[at] = y;
            }
        for (int i = 0; i < candidateScore.Length; i++)
        {
            if (candidateScore[i] < 0) break;
            int cx = candidateX[i], cy = candidateY[i];
            for (int y = Math.Max(minY, cy - 2); y <= Math.Min(maxY, cy + 2); y++)
                for (int x = Math.Max(minX, cx - 2); x <= Math.Min(maxX, cx + 2); x++)
                {
                    if (DistanceToSegment(x, y, _ax, _ay, _bx, _by, _lengthSquared) > corridor) continue;
                    double score = Score(x, y);
                    if (score > best) { best = score; bestX = x; bestY = y; }
                }
        }
        // 次峰在粗搜网格上估计，并排除同一目标周围的响应瓣。
        double runnerUp = -1;
        for (int y = minY; y <= maxY; y += 2)
            for (int x = minX; x <= maxX; x += 2)
            {
                if ((x - bestX) * (x - bestX) + (y - bestY) * (y - bestY) <= 64) continue;
                if (DistanceToSegment(x, y, _ax, _ay, _bx, _by, _lengthSquared) > corridor) continue;
                runnerUp = Math.Max(runnerUp, CoarseScore(x, y));
            }
        if (best < 0.78 || best - runnerUp < 0.06)
        { Invalidate(best < 0.78 ? "目标跟丢了，请重绑" : "出现相似目标，请重绑"); return Signal; }
        _first = false; _x = bestX; _y = bestY;
        Signal = new(true, true, false, bestX / (double)(width - 1), bestY / (double)(height - 1),
            Projection(bestX, bestY), best, "跟着手动目标");
        return Signal;
    }

    public void Invalidate(string reason)
    { if (Signal.Bound) Signal = Signal with { Located = false, Lost = true, Status = reason }; }
    public void Clear() => Signal = new(false, false, false, 0, 0, 0, 0, "自动识别");
    private static bool Valid(ManualTrackPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y)
        && p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1;
    private void ConvertGray(byte[] bgra)
    {
        for (int i = 0; i < _gray.Length; i++)
            _gray[i] = (byte)((bgra[4 * i + 2] * 77 + bgra[4 * i + 1] * 151 + bgra[4 * i] * 28) >> 8);
    }
    private double Projection(double x, double y) => Math.Clamp(((x - _ax) * (_bx - _ax)
        + (y - _ay) * (_by - _ay)) / _lengthSquared, 0, 1);
    private static double DistanceToSegment(double x, double y, double ax, double ay, double bx, double by, double lengthSquared)
    {
        double t = Math.Clamp(((x - ax) * (bx - ax) + (y - ay) * (by - ay)) / lengthSquared, 0, 1);
        return Math.Sqrt(Math.Pow(x - ax - t * (bx - ax), 2) + Math.Pow(y - ay - t * (by - ay), 2));
    }
    private double CoarseScore(int x, int y)
    {
        double sum = 0, sum2 = 0, dot = 0;
        int index = 0;
        for (int dy = -Radius; dy <= Radius; dy += 2)
            for (int dx = -Radius; dx <= Radius; dx += 2)
            { double value = _gray[(y + dy) * width + x + dx]; sum += value; sum2 += value * value; dot += value * _coarseTemplate[index++]; }
        double norm = Math.Sqrt(Math.Max(0, sum2 - sum * sum / CoarseCount));
        return norm < 1 ? -1 : dot / (_coarseTemplateNorm * norm);
    }
    private double Score(int x, int y)
    {
        double sum = 0, sum2 = 0, dot = 0;
        int index = 0;
        for (int dy = -Radius; dy <= Radius; dy++)
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                double value = _gray[(y + dy) * width + x + dx];
                sum += value; sum2 += value * value; dot += value * _template[index++];
            }
        double norm = Math.Sqrt(Math.Max(0, sum2 - sum * sum / Count));
        return norm < 1 ? -1 : dot / (_templateNorm * norm);
    }
}
