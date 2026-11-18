namespace Hexa.Services;

/// <summary>
/// 逐帧位置跟随（画面同步）的信号跟踪器：吃 320×180 灰度帧，在抓帧线程上跑。
/// 构造时一次分配全部缓冲，之后每帧零分配，耗时毫秒级。
///
/// 算法（主代理离线实测验证过）：
///  ① 交合区 ROI —— 最近约 2 秒逐像素时间标准差前 10% 的像素，中位数定中心、80 分位半径定宽高；
///     约 1 秒更新一次并平滑；有姿态主体框时只在框内找。
///  ② 位置信号 —— ROI 像素向量（≤4096 像素）减时间均值后幂迭代（每帧 2 次、热启动）求第一主成分，
///     当前帧投影得带符号标量 p；符号锚定到画面几何（主成分·平均梯度）：p 增大 = 内容往上（竖直为主）/往右，
///     与锁定时刻的相位、频率无关；锚定不出时沿用与上一拍的连续性。
///  ③ L0 —— p 最近约 3 秒的 5/95 分位归一化到 0–1（可选方向反转），映射到行程范围。
///  ④ 深度 —— 一个周期内 ROI 帧差总量（∝ 位移幅度，EMA 平滑）÷ 场景历史最大（锁定 30 帧后才记，丢失/切场景重置），下限 25%。
///  ⑤ 倾斜 —— ROI 帧差加权运动质心在最近一个周期里的位移投影到主运动方向（近似，默认关）。
///  ⑥ 质量门 —— p 在一周期处自相关 &lt; 0.6 或 ROI 几乎没运动时 Ok=false，调用方退回旧锁相路径。
///     周期取自相关第一个够高的局部峰（基频，抛物线插值），不取全局最大（会落在 2/3 倍周期）。
/// </summary>
internal sealed class ScreenSyncTracker
{
    public const int Width = 320;
    public const int Height = 180;
    public const int WindowFrames = 60;          // 最近 60 帧（20fps ≈ 3 秒）
    public const int RoiStdFrames = 40;          // ROI 检测用最近 40 帧（≈ 2 秒）
    public const int RoiMaxPixels = 4096;        // ROI 向量最多采样的像素数
    public const int MinPcFrames = 8;            // 至少这么多帧才开始做主成分
    public const int MinQualityFrames = 40;      // 至少这么多帧才判质量
    public const double RoiUpdateSeconds = 1.0;  // ROI 更新间隔
    public const double AcGate = 0.6;            // 一个周期处自相关的合格线
    public const double MotionFloor = 0.35;      // ROI 平均帧差低于它算「几乎没运动」
    public const double DepthFloor = 0.25;       // 相对深度下限
    public const double TiltGain = 30.0;         // 倾斜轴最大 ±30（= ±30% 行程）
    public const double TiltSmoothing = 0.25;    // 倾斜 EMA 平滑系数
    public const int LossResetFrames = 10;
    public const double DepthSmoothing = 0.1;    // 行程量 EMA 系数（压掉单周期抖动，避免历史最大被尖峰抬高）
    public const int DepthWarmupFrames = 30;     // 锁定后这么多帧才开始记场景最大行程
    public const double AnchorMinCos = 0.05;     // 主成分与平均梯度夹角余弦低于它不锚定       // 连续失锁这么多帧才重置深度历史

    private const int PixelCount = Width * Height;

    private readonly byte[][] _ring;             // 灰度帧环形缓冲（每帧 320×180）
    private readonly double[][] _roiRing;        // 每个窗口帧的 ROI 采样向量
    private readonly double[] _sum;              // ROI 像素时间累加和
    private readonly double[] _mean;             // ROI 像素时间均值
    private readonly double[] _pc;               // 第一主成分（单位向量）
    private readonly double[] _pcPrev;           // 上一拍主成分（防跳反对照）
    private readonly double[] _acc;              // 幂迭代累加器
    private readonly double[] _cent;             // 当前帧中心化向量
    private readonly double[][] _gradRing;       // 每个窗口帧 ROI 采样点的竖直梯度（方向锚定用）
    private readonly double[][] _gradXRing;      // 每个窗口帧 ROI 采样点的水平梯度
    private readonly double[] _gradSum, _gradXSum;
    private readonly double[] _pRing;            // 带符号位置 p 的环形缓冲
    private readonly double[] _pSort;
    private readonly double[] _acScratch = new double[WindowFrames];            // 归一化分位排序用
    private readonly double[] _diffSumRing = new double[WindowFrames]; // ROI 帧差总量（× 采样步长，近似全 ROI）
    private readonly double[] _cxRing, _cyRing;  // 帧差加权运动质心（像素坐标）
    private readonly double[] _stdMean, _stdM2;  // ROI 检测：逐像素时间均值 / M2（Welford）
    private readonly double[] _sortScratch;      // ROI 检测排序用（复用）
    private readonly int[] _activeXs, _activeYs; // ROI 检测：活跃像素坐标

    private int _ringIndex;                      // 下一帧写入槽
    private int _count;                          // 窗口内有效帧数（≤ WindowFrames）

    private int _roiX0, _roiY0, _roiX1, _roiY1;  // ROI 像素边界（含）
    private bool _roiValid;
    private double _roiUpdatedAt;                // 上次 ROI 更新的本地时钟

    private int _roiCount;                       // 采样后的 ROI 像素数
    private int _roiStep;                        // 采样步长（把 ROI 抽到 ≤ RoiMaxPixels）

    private double? _subjectL, _subjectT, _subjectR, _subjectB;   // 主体框（0–1）

    private bool _pcValid;
    private int _signFixes;

    private double _p;                           // 当前帧投影（带符号）
    private double _p2P;                         // 本周期峰峰
    private double _histMaxP2P;                  // 场景历史最大峰峰
    private int _lossStreak;
    private int _okStreak;
    private double _motionEma;                   // 帧差均值的短时平滑：运动折返点单帧帧差≈0 时不掉锁

    private bool _ok;
    private double _ac;
    private double _diffMean;
    private int _periodFrames;
    private double _hz;
    private double _relativeDepth = 1.0;
    private double _r1 = 50.0, _r2 = 50.0;
    private double _t;
    private double _dt;

    public ScreenSyncTracker()
    {
        _ring = new byte[WindowFrames][];
        for (int i = 0; i < WindowFrames; i++) _ring[i] = new byte[PixelCount];
        _roiRing = new double[WindowFrames][];
        for (int i = 0; i < WindowFrames; i++) _roiRing[i] = new double[RoiMaxPixels];
        _sum = new double[RoiMaxPixels];
        _mean = new double[RoiMaxPixels];
        _pc = new double[RoiMaxPixels];
        _pcPrev = new double[RoiMaxPixels];
        _acc = new double[RoiMaxPixels];
        _cent = new double[RoiMaxPixels];
        _gradRing = new double[WindowFrames][];
        _gradXRing = new double[WindowFrames][];
        for (int i = 0; i < WindowFrames; i++) { _gradRing[i] = new double[RoiMaxPixels]; _gradXRing[i] = new double[RoiMaxPixels]; }
        _gradSum = new double[RoiMaxPixels];
        _gradXSum = new double[RoiMaxPixels];
        _pRing = new double[WindowFrames];
        _pSort = new double[WindowFrames];
        _cxRing = new double[WindowFrames];
        _cyRing = new double[WindowFrames];
        _stdMean = new double[PixelCount];
        _stdM2 = new double[PixelCount];
        _sortScratch = new double[PixelCount];
        _activeXs = new int[PixelCount];
        _activeYs = new int[PixelCount];
    }

    /// <summary>当前质量门：周期性与运动量都够，逐帧位置可以驱动设备。</summary>
    public bool Ok => _ok;

    /// <summary>相对深度（本周期峰峰 ÷ 场景历史最大峰峰，0.25–1）。</summary>
    public double RelativeDepth => _relativeDepth;

    /// <summary>测到的频率（Hz；质量门不过线时为 0）。</summary>
    public double Hz => _hz;

    /// <summary>一个周期处的自相关峰（最近一次）。</summary>
    public double Ac => _ac;

    /// <summary>ROI 平均帧差（luma 0–255）。</summary>
    public double DiffMean => _diffMean;

    /// <summary>周期帧数（质量门不过线时为 0）。</summary>
    public int PeriodFrames => _periodFrames;

    /// <summary>主成分翻号次数（防跳反计数器）。</summary>
    public int SignFixes => _signFixes;

    /// <summary>倾斜 R1（左右，50±30）。</summary>
    public double R1 => _r1;

    /// <summary>倾斜 R2（前后，50±30）。</summary>
    public double R2 => _r2;

    /// <summary>当前帧的归一化位置（0–1，未反转）。仅供界面/诊断显示。</summary>
    public double Position
    {
        get
        {
            TryGetNormalizedPosition(false, out double norm);
            return norm;
        }
    }

    /// <summary>场景历史最大峰峰（测试/诊断用）。</summary>
    public double HistMaxP2P => _histMaxP2P;

    /// <summary>本周期峰峰（测试/诊断用）。</summary>
    public double P2P => _p2P;

    /// <summary>设定主体框（姿态框，0–1 坐标）。传 null 表示没有姿态、用整幅画面找 ROI。</summary>
    public void SetSubjectBox(double? left, double? top, double? right, double? bottom)
    {
        if (left is null || top is null || right is null || bottom is null)
        {
            _subjectL = _subjectT = _subjectR = _subjectB = null;
            return;
        }
        double w = right.Value - left.Value;
        double h = bottom.Value - top.Value;
        if (!(w > 0) || !(h > 0))
        {
            _subjectL = _subjectT = _subjectR = _subjectB = null;
            return;
        }
        _subjectL = Math.Clamp(left.Value, 0, 1);
        _subjectT = Math.Clamp(top.Value, 0, 1);
        _subjectR = Math.Clamp(right.Value, 0, 1);
        _subjectB = Math.Clamp(bottom.Value, 0, 1);
    }

    /// <summary>喂一帧 BGRA（320×180）。dt = 帧间隔（秒）。</summary>
    public void SubmitBgra(byte[] bgra, double dt)
    {
        byte[] dest = _ring[_ringIndex];
        int n = PixelCount;
        for (int i = 0; i < n; i++)
        {
            int b = bgra[i * 4];
            int g = bgra[i * 4 + 1];
            int r = bgra[i * 4 + 2];
            dest[i] = (byte)((r * 77 + g * 151 + b * 28) >> 8);
        }
        Advance(dt);
    }

    /// <summary>喂一帧灰度（320×180）。dt = 帧间隔（秒）。</summary>
    public void SubmitGray(byte[] gray, double dt)
    {
        Buffer.BlockCopy(gray, 0, _ring[_ringIndex], 0, PixelCount);
        Advance(dt);
    }

    /// <summary>取当前帧归一化位置（0–1）。质量门不过线时返回 false（norm=0.5）。</summary>
    public bool TryGetNormalizedPosition(bool invert, out double norm)
    {
        if (!_ok || _count < MinQualityFrames)
        {
            norm = 0.5;
            return false;
        }
        int n = _count;
        for (int f = 0; f < n; f++) _pSort[f] = _pRing[SlotOf(f)];
        Array.Sort(_pSort, 0, n);
        int idx5 = Math.Max(0, (int)Math.Round(0.05 * (n - 1)));
        int idx95 = Math.Min(n - 1, (int)Math.Round(0.95 * (n - 1)));
        double lo = _pSort[idx5];
        double hi = _pSort[idx95];
        double span = hi - lo;
        norm = span < 1e-9 ? 0.5 : Math.Clamp((_p - lo) / span, 0, 1);
        if (invert) norm = 1 - norm;
        return true;
    }

    private void Advance(double dt)
    {
        _dt = Math.Clamp(dt, 0.005, 0.25);
        _t += _dt;
        if (_count < WindowFrames) _count++;

        if (!_roiValid || _t - _roiUpdatedAt >= RoiUpdateSeconds) UpdateRoi();

        ExtractRoiVector();
        ComputeCentroid();
        UpdatePcAndProject();
        UpdateQuality();
        UpdateDepth();
        UpdateTilt();

        _ringIndex = (_ringIndex + 1) % WindowFrames;
    }

    /// <summary>窗口内第 f 帧（0 = 最旧，_count-1 = 当前）在环形缓冲里的槽位。</summary>
    private int SlotOf(int f) =>
        (_ringIndex - (_count - 1 - f) + (WindowFrames << 2)) % WindowFrames;

    private void UpdateRoi()
    {
        _roiUpdatedAt = _t;

        // 搜索范围：主体框有效时只在框内找，否则整幅。
        int sx0 = 0, sy0 = 0, sx1 = Width, sy1 = Height;
        if (_subjectL is double l && _subjectT is double t && _subjectR is double r && _subjectB is double b)
        {
            int il = Math.Clamp((int)Math.Floor(l * Width), 0, Width - 2);
            int it = Math.Clamp((int)Math.Floor(t * Height), 0, Height - 2);
            int ir = Math.Clamp((int)Math.Ceiling(r * Width), il + 2, Width);
            int ib = Math.Clamp((int)Math.Ceiling(b * Height), it + 2, Height);
            // 太小的框（主体几乎看不见）退回整幅。
            if (ir - il >= 8 && ib - it >= 8) { sx0 = il; sy0 = it; sx1 = ir; sy1 = ib; }
        }

        int n = Math.Min(_count, RoiStdFrames);
        // Welford 单遍：搜索范围内逐像素时间均值 + M2。
        for (int py = sy0; py < sy1; py++)
        {
            int row = py * Width;
            for (int px = sx0; px < sx1; px++)
            {
                int i = row + px;
                double mean = 0, m2 = 0;
                int cnt = 0;
                for (int f = 0; f < n; f++)
                {
                    byte[] frame = _ring[SlotOf(_count - 1 - f)];
                    double v = frame[i];
                    cnt++;
                    double d = v - mean;
                    mean += d / cnt;
                    m2 += d * (v - mean);
                }
                _stdMean[i] = mean;
                _stdM2[i] = m2;
            }
        }

        // 前 10% 像素：把搜索范围内的标准差拷出来排序取 90 分位当门槛。
        int total = (sx1 - sx0) * (sy1 - sy0);
        if (total <= 0 || n < MinPcFrames)
        {
            if (!_roiValid) SetRoi(0, 0, Width - 1, Height - 1);
            return;
        }
        int m = 0;
        for (int py = sy0; py < sy1; py++)
        {
            int row = py * Width;
            for (int px = sx0; px < sx1; px++)
            {
                int i = row + px;
                _sortScratch[m++] = Math.Sqrt(Math.Max(0, _stdM2[i] / n));
            }
        }
        Array.Sort(_sortScratch, 0, m);
        double cutoff = _sortScratch[Math.Min(m - 1, (int)(m * 0.90))];

        // 活跃像素：坐标中位数定中心。
        int active = 0;
        for (int py = sy0; py < sy1; py++)
        {
            int row = py * Width;
            for (int px = sx0; px < sx1; px++)
            {
                int i = row + px;
                if (Math.Sqrt(Math.Max(0, _stdM2[i] / n)) >= cutoff && cutoff > 0)
                {
                    _activeXs[active] = px;
                    _activeYs[active] = py;
                    active++;
                }
            }
        }
        if (active < 16)
        {
            // 几乎全是静止画面：没有可用的交合区，沿用旧 ROI（没有就全幅）。
            if (!_roiValid) SetRoi(0, 0, Width - 1, Height - 1);
            return;
        }
        Array.Sort(_activeXs, 0, active);
        Array.Sort(_activeYs, 0, active);
        double cx = _activeXs[active / 2];
        double cy = _activeYs[active / 2];

        // 80 分位 Chebyshev 半径定宽高。
        for (int k = 0; k < active; k++)
        {
            double dx = Math.Abs(_activeXs[k] - cx);
            double dy = Math.Abs(_activeYs[k] - cy);
            _sortScratch[k] = Math.Max(dx, dy);
        }
        Array.Sort(_sortScratch, 0, active);
        int half = Math.Max(8, (int)Math.Ceiling(_sortScratch[(int)(active * 0.80)]));

        // 与旧 ROI 平滑（不跳变）；几乎没变化就沿用旧几何（避免每帧重建缓冲）。
        if (_roiValid)
        {
            double oldCx = (_roiX0 + _roiX1) / 2.0;
            double oldCy = (_roiY0 + _roiY1) / 2.0;
            double oldHalfW = Math.Max(1, (_roiX1 - _roiX0 + 1) / 2.0);
            double oldHalfH = Math.Max(1, (_roiY1 - _roiY0 + 1) / 2.0);
            if (Math.Abs(cx - oldCx) <= 4 && Math.Abs(cy - oldCy) <= 4
                && half >= oldHalfW * 0.8 && half <= oldHalfW * 1.25)
                return;

            // ROI 大幅移动 = 场景切换：重置主成分与深度历史。
            if (Math.Abs(cx - oldCx) > oldHalfW || Math.Abs(cy - oldCy) > oldHalfH)
                ResetScene();

            double smooth = 0.5;
            cx = oldCx + (cx - oldCx) * smooth;
            cy = oldCy + (cy - oldCy) * smooth;
            int oldHalf = (int)Math.Round((oldHalfW + oldHalfH) / 2.0);
            half = Math.Max(8, (int)Math.Round(oldHalf + (half - oldHalf) * smooth));
        }

        SetRoi(
            Math.Clamp((int)Math.Round(cx - half), 0, Width - 1),
            Math.Clamp((int)Math.Round(cy - half), 0, Height - 1),
            Math.Clamp((int)Math.Round(cx + half), 0, Width - 1),
            Math.Clamp((int)Math.Round(cy + half), 0, Height - 1));
    }

    private void SetRoi(int x0, int y0, int x1, int y1)
    {
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);
        bool geometryChanged = !_roiValid || x0 != _roiX0 || y0 != _roiY0 || x1 != _roiX1 || y1 != _roiY1;
        _roiX0 = x0; _roiY0 = y0; _roiX1 = x1; _roiY1 = y1;
        int w = x1 - x0 + 1;
        int h = y1 - y0 + 1;
        _roiStep = Math.Max(1, (int)Math.Ceiling((double)(w * h) / RoiMaxPixels));
        _roiCount = (w * h + _roiStep - 1) / _roiStep;
        _roiValid = true;
        if (geometryChanged) RebuildRoiVectors();
    }

    /// <summary>ROI 几何变了：从灰度环形缓冲重建所有窗口帧的 ROI 向量与时间累加和。</summary>
    private void RebuildRoiVectors()
    {
        Array.Clear(_sum, 0, _roiCount);
        Array.Clear(_gradSum, 0, _roiCount);
        Array.Clear(_gradXSum, 0, _roiCount);
        for (int f = 0; f < _count; f++)
        {
            int slot = SlotOf(f);
            double[] vec = _roiRing[slot];
            FillRoiVector(_ring[slot], vec, _gradRing[slot], _gradXRing[slot]);
            for (int i = 0; i < _roiCount; i++)
            {
                _sum[i] += vec[i];
                _gradSum[i] += _gradRing[slot][i];
                _gradXSum[i] += _gradXRing[slot][i];
            }
        }
    }

    /// <summary>按当前 ROI 几何把一帧灰度抽成采样向量。</summary>
    private void FillRoiVector(byte[] gray, double[] vec, double[] gy, double[] gx)
    {
        int w = _roiX1 - _roiX0 + 1;
        int k = 0;
        int total = w * (_roiY1 - _roiY0 + 1);
        for (int idx = 0; idx < total && k < _roiCount; idx += _roiStep, k++)
        {
            int x = _roiX0 + idx % w;
            int y = _roiY0 + idx / w;
            int i = y * Width + x;
            vec[k] = gray[i];
            int up = y > 0 ? i - Width : i;
            int dn = y < Height - 1 ? i + Width : i;
            int lf = x > 0 ? i - 1 : i;
            int rt = x < Width - 1 ? i + 1 : i;
            gy[k] = gray[dn] - gray[up];
            gx[k] = gray[rt] - gray[lf];
        }
    }

    private void ExtractRoiVector()
    {
        double[] vec = _roiRing[_ringIndex];
        double[] gy = _gradRing[_ringIndex];
        double[] gx = _gradXRing[_ringIndex];
        if (_count >= WindowFrames)
        {
            // 满窗：当前槽里是即将被挤出的最旧帧，先把它从累加和里减掉。
            for (int i = 0; i < _roiCount; i++) { _sum[i] -= vec[i]; _gradSum[i] -= gy[i]; _gradXSum[i] -= gx[i]; }
        }
        FillRoiVector(_ring[_ringIndex], vec, gy, gx);
        for (int i = 0; i < _roiCount; i++) { _sum[i] += vec[i]; _gradSum[i] += gy[i]; _gradXSum[i] += gx[i]; }
    }

    private void ComputeCentroid()
    {
        byte[] cur = _ring[_ringIndex];
        byte[] prev = _count > 1 ? _ring[(_ringIndex - 1 + WindowFrames) % WindowFrames] : cur;
        double sumD = 0, sumX = 0, sumY = 0;
        int w = _roiX1 - _roiX0 + 1;
        int total = w * (_roiY1 - _roiY0 + 1);
        int k = 0;
        for (int idx = 0; idx < total && k < _roiCount; idx += _roiStep, k++)
        {
            int x = _roiX0 + idx % w;
            int y = _roiY0 + idx / w;
            int i = y * Width + x;
            double d = Math.Abs((int)cur[i] - (int)prev[i]);
            sumD += d;
            sumX += d * x;
            sumY += d * y;
        }
        _diffMean = sumD / Math.Max(1, _roiCount);
        _motionEma += 0.2 * (_diffMean - _motionEma);
        _diffSumRing[_ringIndex] = sumD * _roiStep;
        if (sumD > 1e-9)
        {
            _cxRing[_ringIndex] = sumX / sumD;
            _cyRing[_ringIndex] = sumY / sumD;
        }
        else
        {
            // 这一帧 ROI 里完全没动：沿用 ROI 中心，让倾斜往中位收。
            _cxRing[_ringIndex] = (_roiX0 + _roiX1) / 2.0;
            _cyRing[_ringIndex] = (_roiY0 + _roiY1) / 2.0;
        }
    }

    private void UpdatePcAndProject()
    {
        if (_count < MinPcFrames)
        {
            _p = 0;
            return;
        }

        for (int i = 0; i < _roiCount; i++) _mean[i] = _sum[i] / _count;
        double[] cur = _roiRing[_ringIndex];
        for (int i = 0; i < _roiCount; i++) _cent[i] = cur[i] - _mean[i];

        if (!_pcValid)
        {
            double norm = Norm(_cent, _roiCount);
            if (norm < 1e-9)
            {
                _p = 0;
                return;
            }
            for (int i = 0; i < _roiCount; i++)
            {
                _pc[i] = _cent[i] / norm;
                _pcPrev[i] = _pc[i];
            }
            _pcValid = true;
        }
        else
        {
            // 幂迭代 2 次 / 帧，热启动（上一次的向量就是初值）。
            for (int it = 0; it < 2; it++)
            {
                Array.Clear(_acc, 0, _roiCount);
                for (int f = 0; f < _count; f++)
                {
                    double[] xf = _roiRing[SlotOf(f)];
                    double dot = 0;
                    for (int i = 0; i < _roiCount; i++) dot += (xf[i] - _mean[i]) * _pc[i];
                    for (int i = 0; i < _roiCount; i++) _acc[i] += dot * (xf[i] - _mean[i]);
                }
                double norm = Norm(_acc, _roiCount);
                if (norm < 1e-12) break;
                for (int i = 0; i < _roiCount; i++) _pc[i] = _acc[i] / norm;
            }

            // 防跳反：新向量与上一拍点积为负就整体翻号。
            double dotOld = 0;
            for (int i = 0; i < _roiCount; i++) dotOld += _pc[i] * _pcPrev[i];
            if (dotOld < 0)
            {
                for (int i = 0; i < _roiCount; i++) _pc[i] = -_pc[i];
                _signFixes++;
            }
        }
        AnchorSign();
        for (int i = 0; i < _roiCount; i++) _pcPrev[i] = _pc[i];

        double p = 0;
        for (int i = 0; i < _roiCount; i++) p += _cent[i] * _pc[i];
        _p = p;
        _pRing[_ringIndex] = p;
    }

    /// <summary>
    /// 方向锚定到画面几何：画面内容位移 s 时帧差 ≈ -∇I·s，所以主成分与时间平均梯度的点积
    /// 给出「p 增大 = 画面往哪走」。约定 p 增大 = 内容往上（竖直为主时）/ 往右（水平为主时），
    /// 不再取决于锁定时刻的相位或频率。点积太弱（纯亮度闪烁等）时沿用连续性符号。
    /// </summary>
    private void AnchorSign()
    {
        double sy = 0, sx = 0, gn = 0;
        for (int i = 0; i < _roiCount; i++)
        {
            double gy = _gradSum[i] / _count;
            double gx = _gradXSum[i] / _count;
            sy += _pc[i] * gy;
            sx += _pc[i] * gx;
            gn += gy * gy + gx * gx;
        }
        if (gn < 1e-9) return;
        double anchor = Math.Abs(sy) >= Math.Abs(sx) ? sy : -sx;
        if (Math.Abs(anchor) / Math.Sqrt(gn) < AnchorMinCos) return;
        if (anchor < 0)
        {
            for (int i = 0; i < _roiCount; i++) _pc[i] = -_pc[i];
            _signFixes++;
        }
    }

    private static double Norm(double[] v, int n)
    {
        double s = 0;
        for (int i = 0; i < n; i++) s += v[i] * v[i];
        return Math.Sqrt(s);
    }

    private void UpdateQuality()
    {
        if (_count < MinQualityFrames)
        {
            _ok = false;
            _ac = 0;
            _hz = 0;
            _periodFrames = 0;
            return;
        }

        double fps = 1.0 / _dt;
        int lagMin = Math.Max(2, (int)Math.Round(fps / 4.0));
        int lagMax = Math.Min(_count - 4, (int)Math.Round(fps / 0.5));
        if (lagMin > lagMax)
        {
            _ok = false;
            _ac = 0;
            _hz = 0;
            _periodFrames = 0;
            return;
        }

        double meanP = 0;
        for (int f = 0; f < _count; f++) meanP += _pRing[SlotOf(f)];
        meanP /= _count;
        double var0 = 0;
        for (int f = 0; f < _count; f++)
        {
            double d = _pRing[SlotOf(f)] - meanP;
            var0 += d * d;
        }
        if (var0 < 1e-9)
        {
            _ok = false;
            _ac = 0;
            _hz = 0;
            _periodFrames = 0;
            return;
        }

        double bestAc = -2;
        int bestLag = 0;
        int nLags = lagMax - lagMin + 1;
        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            double num = 0, va = 0, vb = 0;
            for (int f = lag; f < _count; f++)
            {
                double a = _pRing[SlotOf(f)] - meanP;
                double b = _pRing[SlotOf(f - lag)] - meanP;
                num += a * b;
                va += a * a;
                vb += b * b;
            }
            double ac = num / Math.Sqrt(Math.Max(va * vb, 1e-18));
            _acScratch[lag - lagMin] = ac;
            if (ac > bestAc) { bestAc = ac; bestLag = lag; }
        }

        // 周期 = 第一个够高的局部峰（基频），而不是全局最大（常落在 2、3 倍周期上）。
        double periodLag = bestLag;
        for (int k = 0; k < nLags; k++)
        {
            double a = _acScratch[k];
            double left = k > 0 ? _acScratch[k - 1] : double.NegativeInfinity;
            double right = k < nLags - 1 ? _acScratch[k + 1] : double.NegativeInfinity;
            if (a >= left && a >= right && a >= bestAc * 0.85 && a >= AcGate)
            {
                periodLag = k + lagMin;
                if (k > 0 && k < nLags - 1)
                {
                    double denom = left - 2 * a + right;
                    if (Math.Abs(denom) > 1e-12) periodLag += Math.Clamp(0.5 * (left - right) / denom, -0.5, 0.5);
                }
                bestAc = Math.Max(a, bestAc * 0.85);
                break;
            }
        }

        _ac = bestAc;
        _periodFrames = (int)Math.Round(periodLag);
        _hz = bestAc >= AcGate ? fps / periodLag : 0;
        _ok = bestAc >= AcGate && _motionEma >= MotionFloor;
    }

    private void UpdateDepth()
    {
        if (_ok)
        {
            _lossStreak = 0;
            _okStreak++;
            // 行程量 ≈ 一个周期内帧差总量之和：画面位移 s 时 Σ|ΔI| ≈ |s|·Σ|∇I|，与位移成正比，
            // 不受主成分基底随幅度变化（投影饱和）的影响。
            int span = Math.Max(4, Math.Min(Math.Max(2, _periodFrames), _count - 1));
            double travel = 0;
            for (int f = _count - span; f < _count; f++) travel += _diffSumRing[SlotOf(f)];
            _p2P = _p2P > 0 ? _p2P + DepthSmoothing * (travel - _p2P) : travel;
            // 刚锁定时 ROI 还在收敛、行程量偏大，前一段不计入场景最大。
            if (_okStreak >= DepthWarmupFrames && _p2P > _histMaxP2P) _histMaxP2P = _p2P;
            _relativeDepth = _histMaxP2P > 0
                ? Math.Clamp(_p2P / _histMaxP2P, DepthFloor, 1.0)
                : 1.0;
        }
        else
        {
            _lossStreak++;
            if (_lossStreak >= LossResetFrames)
            {
                _okStreak = 0;
                _histMaxP2P = 0;
                _p2P = 0;
            }
            _relativeDepth = 1.0;
        }
    }

    private void UpdateTilt()
    {
        if (_count < 2)
        {
            _r1 += TiltSmoothing * (50 - _r1);
            _r2 += TiltSmoothing * (50 - _r2);
            return;
        }
        int span;
        if (_ok && _periodFrames > 0) span = Math.Clamp(_periodFrames, 1, _count - 1);
        else span = Math.Clamp(30, 1, _count - 1);
        double cx = _cxRing[_ringIndex];
        double cy = _cyRing[_ringIndex];
        double cx0 = _cxRing[SlotOf(_count - 1 - span)];
        double cy0 = _cyRing[SlotOf(_count - 1 - span)];
        double halfW = Math.Max(1.0, (_roiX1 - _roiX0 + 1) / 2.0);
        double halfH = Math.Max(1.0, (_roiY1 - _roiY0 + 1) / 2.0);
        double dirX = Math.Clamp((cx - cx0) / halfW, -1, 1);
        double dirY = Math.Clamp((cy - cy0) / halfH, -1, 1);
        double offX = Math.Clamp((cx - (_roiX0 + _roiX1) / 2.0) / halfW, -1, 1);

        double targetR1 = 50 + TiltGain * Math.Clamp(0.6 * dirX + 0.4 * offX, -1, 1);
        double targetR2 = 50 + TiltGain * Math.Clamp(dirY, -1, 1);
        if (_diffMean < MotionFloor || !_ok)
        {
            // 没动或质量不过线：倾斜往中位收，别把噪声当倾斜。
            targetR1 = 50;
            targetR2 = 50;
        }
        _r1 += TiltSmoothing * (targetR1 - _r1);
        _r2 += TiltSmoothing * (targetR2 - _r2);
    }

    private void ResetScene()
    {
        _pcValid = false;
        Array.Clear(_pc, 0, _roiCount);
        Array.Clear(_pcPrev, 0, _roiCount);
        _histMaxP2P = 0;
        _p2P = 0;
        _okStreak = 0;
        _signFixes = 0;
        _lossStreak = 0;
    }
}
