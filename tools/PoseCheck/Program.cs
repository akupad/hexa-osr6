#if POSECHECK
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Hexa.Services;
using Microsoft.ML.OnnxRuntime;

// 验收探针：默认读仓库里的 tools/PoseCheck/assets/bus.jpg → PoseTracker.Detect → 和 bus_pose_ref.json 逐项比对。
// 后端就是运行时那一个：PoseTracker（唯一引擎、唯一模型文件），模型路径也走 PoseTracker.ModelPath
// （= AppSettings.DataDirectory\models），所以探针量到的就是软件跑的那条路径。
// 阈值：bbox 每边 ≤8px、关键点最大 ≤10px、score 差 ≤0.05。
//
// 参数：
//   [图片] [参考 json]   默认 assets/bus.jpg + assets/bus_pose_ref.json
//   --dump-shapes        打印模型真实的输入/输出张量签名（排查输出布局时用）
//   --frame WxH          先把图缩到 W×H 再检测（模拟运行期 160×90 的抓帧）；缩放过就跳过参考值比对
//   --joint              打印 17 点置信度（重点髋 11/12、踝 15/16）与深度换算
//   --sweep              再扫一遍关键点置信度阈值（0.20–0.60），看深度还成不成立

const double MaxBoxErrorPx = 8.0;
const double MaxKptErrorPx = 10.0;
const double MaxScoreDiff = 0.05;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向到文件时没有控制台，忽略 */ }

string assets = Path.Combine(AppContext.BaseDirectory, "assets");
bool dumpShapes = args.Contains("--dump-shapes");
bool joint = args.Contains("--joint") || args.Contains("--sweep");
bool sweep = args.Contains("--sweep");
int frameW = 0, frameH = 0;

var plain = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--frame", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        string[] parts = args[++i].Split('x', 'X', '*');
        if (parts.Length == 2 && int.TryParse(parts[0], out int fw) && int.TryParse(parts[1], out int fh) && fw > 0 && fh > 0)
        {
            frameW = fw;
            frameH = fh;
        }
        else
        {
            Console.WriteLine($"--frame 参数看不懂：{args[i]}（要 WxH，例如 --frame 160x90）");
            return 2;
        }
        continue;
    }
    if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
    plain.Add(args[i]);
}

string jpgPath = plain.Count > 0 ? plain[0] : Path.Combine(assets, "bus.jpg");
string refPath = plain.Count > 1
    ? plain[1]
    : Path.Combine(Path.GetDirectoryName(jpgPath) ?? assets, "bus_pose_ref.json");

string F(double v) => double.IsFinite(v) ? v.ToString("F3", CultureInfo.InvariantCulture) : "NaN";
string P(double v) => double.IsFinite(v) ? v.ToString("F1", CultureInfo.InvariantCulture) : "NaN";

if (!File.Exists(jpgPath) && !Directory.Exists(jpgPath)) { Console.WriteLine($"找不到图片或目录：{jpgPath}"); return 2; }

Console.WriteLine($"模型：{PoseTracker.ModelPath}");
Console.WriteLine($"      已安装={PoseTracker.IsInstalled}（{PoseTracker.InstalledBytes:N0} B）  {PoseTracker.Status}");
if (!PoseTracker.IsInstalled) { Console.WriteLine("模型文件不存在或不足 12MB，无法验证。"); return 2; }

if (dumpShapes)
{
    using var diag = new InferenceSession(PoseTracker.ModelPath);
    foreach (var kv in diag.InputMetadata)
        Console.WriteLine($"输入  {kv.Key}: [{string.Join(", ", kv.Value.Dimensions)}] {kv.Value.ElementType}");
    foreach (var kv in diag.OutputMetadata)
        Console.WriteLine($"输出  {kv.Key}: [{string.Join(", ", kv.Value.Dimensions)}] {kv.Value.ElementType}");
    return 3;
}

// --dir 目录模式：一口气跑完一串真实抓帧（运行期就是 160×90），统计髋/踝置信度与深度，
// 用来判断 MinKeypointConfidence=0.30 在真实画面下合适不合适 —— 看分布，不看单帧。
if (plain.Count > 0 && Directory.Exists(plain[0]))
{
    string[] files = Directory.GetFiles(plain[0], "*.png")
        .Concat(Directory.GetFiles(plain[0], "*.jpg"))
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0) { Console.WriteLine($"目录里没有 png/jpg：{plain[0]}"); return 2; }

    Console.WriteLine($"目录：{plain[0]}  {files.Length} 帧"
        + (frameW > 0 ? $"  统一缩放到 {frameW}×{frameH}" : "  按原尺寸"));
    Console.WriteLine("   帧    score   髋11   髋12   踝15   踝16     深度   耗时ms");

    double usableAt = 0.30; // 生产值 PoseMotionSource.MinKeypointConfidence
    var hips = new List<double>();
    var ankles = new List<double>();
    var depths = new List<double>();
    var cached = new List<(PosePoint[] Keypoints, int Height)>();
    int nulls = 0, ankleOut = 0;
    long totalMs = 0;

    for (int i = 0; i < files.Length; i++)
    {
        (byte[] frameBgra, int fw, int fh) = LoadFrame(files[i], frameW, frameH);
        var watch = Stopwatch.StartNew();
        PosePerson? detected = PoseTracker.Detect(frameBgra, fw, fh);
        watch.Stop();
        totalMs += watch.ElapsedMilliseconds;

        if (detected is null)
        {
            nulls++;
            Console.WriteLine($"  {i + 1,3}   null                                             {watch.ElapsedMilliseconds,5}");
            continue;
        }

        PosePoint[] k = detected.Keypoints;
        double depth = PoseMotionSource.DepthFromKeypoints(k, fh);
        hips.Add(k[11].Confidence);
        hips.Add(k[12].Confidence);
        ankles.Add(k[15].Confidence);
        ankles.Add(k[16].Confidence);
        if (double.IsFinite(depth)) depths.Add(depth);
        foreach (int a in new[] { 15, 16 })
        {
            // 生产函数会把越界的踝整点丢弃（检测幻觉），这里把丢弃次数也数出来
            if (PoseMotionSource.IsUsable(k[a]) && (k[a].Y < 1 || k[a].Y > fh - 1)) ankleOut++;
        }
        cached.Add((k, fh));

        Console.WriteLine($"  {i + 1,3}  {detected.Score,6:F3}  {k[11].Confidence,6:F3} {k[12].Confidence,6:F3}"
            + $" {k[15].Confidence,6:F3} {k[16].Confidence,6:F3}  {F(depth),7}  {watch.ElapsedMilliseconds,6}");
    }

    Console.WriteLine();
    Console.WriteLine($"帧数 {files.Length}（null {nulls}）  总耗时 {totalMs} ms  平均 {totalMs / (double)files.Length:F0} ms/帧");
    Console.WriteLine($"髋(11/12) 置信度 {Stat(hips)}  可用率 {Pct(hips, usableAt)}");
    Console.WriteLine($"踝(15/16) 置信度 {Stat(ankles)}  可用率 {Pct(ankles, usableAt)}  越界丢弃 {ankleOut} 次");
    Console.WriteLine(depths.Count > 0
        ? $"深度 有效帧 {depths.Count}/{files.Length - nulls}  min {depths.Min():F1}  中位 {Median(depths):F1}  max {depths.Max():F1}  幅度 {depths.Max() - depths.Min():F1}"
        : "深度 一帧都没算出来（髋点全被阈值挡掉）");

    Console.WriteLine("阈值扫描（同一批真实关键点，只改「低于多少算不可用」；结论看哪一档开始丢帧/改深度）：");
    Console.WriteLine("  阈值  可用髋点帧  出深度帧  深度中位  与 0.30 档的差");
    double baseline = double.NaN;
    foreach (double t in new[] { 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60 })
    {
        int withHip = 0;
        var scanned = new List<double>();
        foreach ((PosePoint[] keypoints, int height) in cached)
        {
            PosePoint[] masked = Mask(keypoints, t);
            if (PoseMotionSource.IsUsable(masked[11]) || PoseMotionSource.IsUsable(masked[12])) withHip++;
            double d = PoseMotionSource.DepthFromKeypoints(masked, height);
            if (double.IsFinite(d)) scanned.Add(d);
        }
        double median = Median(scanned);
        if (t == 0.30) baseline = median;
        Console.WriteLine($"  {t:F2}   {withHip,7}/{cached.Count}   {scanned.Count,6}/{cached.Count}   {P(median),7}   {P(median - baseline),7}");
    }
    return 0;
}

// 图片 → BGRA32（LockBits 读 Format32bppArgb，小端内存里就是 B,G,R,A，和 Detect 的入参一致）
byte[] bgra;
int w, h, sourceW, sourceH;
using (var original = new Bitmap(jpgPath))
{
    sourceW = original.Width;
    sourceH = original.Height;
    Bitmap? scaled = null;
    try
    {
        Bitmap current = original;
        if (frameW > 0 && (sourceW != frameW || sourceH != frameH))
        {
            // 缩图这一步只是模拟"抓帧拿到的是 160×90"：拉伸用 HighQualityBilinear（和运行时抓帧后的
            // 640 letterbox 双线性放大是两条独立的采样，这里只负责给出真实尺寸的输入）。
            scaled = new Bitmap(frameW, frameH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(original, new Rectangle(0, 0, frameW, frameH));
            }
            current = scaled;
        }
        (bgra, w, h) = ReadBgra(current);
    }
    finally { scaled?.Dispose(); }
}

bool resized = w != sourceW || h != sourceH;
Console.WriteLine($"图：{Path.GetFileName(jpgPath)}  原图 {sourceW}×{sourceH}"
    + (resized ? $"  → 缩放到 {w}×{h}（模拟运行期抓帧尺寸）" : ""));

var sw = Stopwatch.StartNew();
PosePerson? person = PoseTracker.Detect(bgra, w, h);
sw.Stop();

if (person is null)
{
    Console.WriteLine($"Detect 返回 null（耗时 {sw.ElapsedMilliseconds} ms）：画面里没人 / 分数低于 0.35 / 模型加载失败");
    Console.WriteLine($"状态：{PoseTracker.Status}");
    return 1;
}
Console.WriteLine($"推理耗时：{sw.ElapsedMilliseconds} ms（PoseTracker.LastInferenceMs={PoseTracker.LastInferenceMs}）");
Console.WriteLine($"score：{F(person.Score)}   bbox：[{F(person.X1)}, {F(person.Y1)}, {F(person.X2)}, {F(person.Y2)}]");

string[] kptNames =
[
    "鼻", "左眼", "右眼", "左耳", "右耳", "左肩", "右肩", "左肘", "右肘",
    "左腕", "右腕", "左髋", "右髋", "左膝", "右膝", "左踝", "右踝",
];

double threshold = PoseMotionSource.MinKeypointConfidence;
bool jointOk = false;
if (joint)
{
    Console.WriteLine($"关键点（阈值 MinKeypointConfidence = {F(threshold)}）：");
    for (int i = 0; i < person.Keypoints.Length; i++)
    {
        PosePoint point = person.Keypoints[i];
        bool usable = PoseMotionSource.IsUsable(point);
        string role = i is 11 or 12 ? "← 髋（深度分子）" : i is 15 or 16 ? "← 踝（深度参考线）" : "";
        Console.WriteLine($"  {i,2} {kptNames[i],-3} {(usable ? "√" : "×")}  ({P(point.X)},{P(point.Y)})  conf {F(point.Confidence)}  {role}");
    }

    double depth = PoseMotionSource.DepthFromKeypoints(person.Keypoints, h);
    jointOk = double.IsFinite(depth);
    Console.WriteLine(jointOk
        ? $"深度：{F(depth)}（0–100，越大越蹲下）"
        : "深度：NaN —— 没有可用髋点（这一帧不产生信号）");

    if (sweep)
    {
        Console.WriteLine("阈值扫描（只升不降：IsUsable 本身硬下限 0.30，低于它的档位无法用生产函数模拟）：");
        Console.WriteLine("  阈值   髋点  踝点  深度     结论");
        foreach (double t in new[] { 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60 })
        {
            PosePoint[] masked = Mask(person.Keypoints, t);
            int hips = Count(masked, 11, 12), ankles = Count(masked, 15, 16);
            double d = PoseMotionSource.DepthFromKeypoints(masked, h);
            Console.WriteLine($"  {t:F2}   {hips,2}    {ankles,2}   {P(d),7}   "
                + (double.IsFinite(d) ? (hips == 2 && ankles == 2 ? "髋踝齐全" : "有缺口，深度会跳") : "无髋，不出信号"));
        }
    }
}

if (resized)
{
    Console.WriteLine("缩放过（--frame）：参考值是原图坐标，跳过逐项比对。");
    Console.WriteLine(jointOk ? "结论：PASS（缩放后仍解出可用髋点）" : "结论：FAIL（缩放后没有可用髋点）");
    return jointOk ? 0 : 1;
}

if (!File.Exists(refPath)) { Console.WriteLine($"找不到参考值：{refPath}"); return 2; }

using var doc = JsonDocument.Parse(File.ReadAllText(refPath));
var root = doc.RootElement;
double refScore = root.GetProperty("score").GetDouble();
double[] refBox = root.GetProperty("bbox").EnumerateArray().Select(e => e.GetDouble()).ToArray();
double[][] refKpts = root.GetProperty("kpts").EnumerateArray()
    .Select(p => p.EnumerateArray().Select(e => e.GetDouble()).ToArray()).ToArray();
int refW = root.TryGetProperty("w", out var wEl) ? wEl.GetInt32() : 0;
int refH = root.TryGetProperty("h", out var hEl) ? hEl.GetInt32() : 0;
Console.WriteLine($"参考：{Path.GetFileName(refPath)}  {refW}×{refH}"
    + (refW == w && refH == h ? "" : "   ← 尺寸不一致，参考值对不上"));

double scoreDiff = Math.Abs(person.Score - refScore);
Console.WriteLine($"score 差：{F(scoreDiff)}"
    + (scoreDiff <= MaxScoreDiff ? $"   OK（≤{F(MaxScoreDiff)}）" : $"   FAIL（>{F(MaxScoreDiff)}）"));

double[] got = [person.X1, person.Y1, person.X2, person.Y2];
string[] names = ["x1", "y1", "x2", "y2"];
Console.WriteLine($"bbox：C# [{F(got[0])}, {F(got[1])}, {F(got[2])}, {F(got[3])}]");
Console.WriteLine($"      ref [{F(refBox[0])}, {F(refBox[1])}, {F(refBox[2])}, {F(refBox[3])}]");
double maxBoxErr = 0;
var boxLine = new StringBuilder("      各边误差 ");
for (int i = 0; i < 4; i++)
{
    double e = Math.Abs(got[i] - refBox[i]);
    maxBoxErr = Math.Max(maxBoxErr, e);
    boxLine.Append($"{names[i]} {F(e)}  ");
}
boxLine.Append(maxBoxErr <= MaxBoxErrorPx ? $"OK（max {F(maxBoxErr)} ≤ {F(MaxBoxErrorPx)}px）"
                                         : $"FAIL（max {F(maxBoxErr)} > {F(MaxBoxErrorPx)}px）");
Console.WriteLine(boxLine.ToString());

Console.WriteLine("关键点（相对参考值的像素误差）：");
double maxKpt = 0, sumKpt = 0;
int counted = 0;
for (int i = 0; i < person.Keypoints.Length && i < refKpts.Length; i++)
{
    double dx = person.Keypoints[i].X - refKpts[i][0];
    double dy = person.Keypoints[i].Y - refKpts[i][1];
    double dist = Math.Sqrt(dx * dx + dy * dy);
    maxKpt = Math.Max(maxKpt, dist);
    sumKpt += dist;
    counted++;
    Console.WriteLine($"  {i,2} {kptNames[i],-3} C#({P(person.Keypoints[i].X)},{P(person.Keypoints[i].Y)}) "
        + $"ref({P(refKpts[i][0])},{P(refKpts[i][1])})  dx {dx,7:F2} dy {dy,7:F2}  dist {dist,6:F2}");
}
double avgKpt = counted == 0 ? double.NaN : sumKpt / counted;
Console.WriteLine($"关键点最大误差 {F(maxKpt)} px（≤{F(MaxKptErrorPx)}）"
    + $"  平均误差 {F(avgKpt)} px  点数 {counted}");

bool pass = maxBoxErr <= MaxBoxErrorPx && maxKpt <= MaxKptErrorPx && scoreDiff <= MaxScoreDiff;
Console.WriteLine(pass ? "结论：PASS" : "结论：FAIL");
return pass ? 0 : 1;

// ── 小工具 ────────────────────────────────────────────────────────────

static (byte[] Bgra, int Width, int Height) ReadBgra(Bitmap bmp)
{
    int width = bmp.Width, height = bmp.Height;
    BitmapData data = bmp.LockBits(
        new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        int stride = data.Stride;
        byte[] raw = new byte[stride * height];
        Marshal.Copy(data.Scan0, raw, 0, raw.Length);
        if (stride == width * 4) return (raw, width, height);
        byte[] tight = new byte[width * height * 4];
        for (int y = 0; y < height; y++) Buffer.BlockCopy(raw, y * stride, tight, y * width * 4, width * 4);
        return (tight, width, height);
    }
    finally { bmp.UnlockBits(data); }
}

/// <summary>把低于阈值的点压成 confidence 0（IsUsable 会因为 &lt; 0.30 丢掉它），用来模拟更高的阈值。</summary>
static PosePoint[] Mask(PosePoint[] source, double minConfidence)
{
    var copy = new PosePoint[source.Length];
    for (int i = 0; i < copy.Length; i++)
        copy[i] = source[i].Confidence >= minConfidence ? source[i] : new PosePoint(source[i].X, source[i].Y, 0);
    return copy;
}

static int Count(PosePoint[] points, int first, int second)
{
    int n = 0;
    if (PoseMotionSource.IsUsable(points[first])) n++;
    if (PoseMotionSource.IsUsable(points[second])) n++;
    return n;
}
static (byte[] Bgra, int Width, int Height) LoadFrame(string path, int targetWidth, int targetHeight)
{
    using var bitmap = new Bitmap(path);
    if (targetWidth <= 0 || targetHeight <= 0 || (bitmap.Width == targetWidth && bitmap.Height == targetHeight))
        return ReadBgra(bitmap);
    using var scaled = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(scaled))
    {
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(bitmap, new Rectangle(0, 0, targetWidth, targetHeight));
    }
    return ReadBgra(scaled);
}

static double Median(IEnumerable<double> values)
{
    double[] sorted = values.OrderBy(v => v).ToArray();
    if (sorted.Length == 0) return double.NaN;
    int mid = sorted.Length / 2;
    return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

static string Stat(IEnumerable<double> values)
{
    double[] arr = values.ToArray();
    return arr.Length == 0 ? "—" : $"min {arr.Min():F3}  中位 {Median(arr):F3}  均 {arr.Average():F3}";
}

static string Pct(IEnumerable<double> values, double threshold)
{
    double[] arr = values.ToArray();
    return arr.Length == 0 ? "—" : $"{arr.Count(v => v >= threshold) * 100.0 / arr.Length:F1}%（≥{threshold:F2}）";
}
#endif
