using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Hexa.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Hexa.Services;

/// <summary>
/// 一次 letterbox 的坐标换算表：把 640 画布上的坐标换回「原窗口的 0–1 相对坐标」。
/// 为什么需要它：模型只吃 640×640 的正方形，而窗口是任意长宽比，缩放后要在两侧补灰边；
/// 关键点是在<b>带灰边的画布</b>上给出的，先减掉灰边、再除缩放比，才是窗口里的真实位置。
/// </summary>
/// <param name="Scale">等比缩放比（源图 → 画布）。</param>
/// <param name="PadX">左边灰边宽度（画布像素）。</param>
/// <param name="PadY">上边灰边高度（画布像素）。</param>
/// <param name="SourceWidth">源图宽（窗口被观察区域的实际宽度）。</param>
/// <param name="SourceHeight">源图高。</param>
public readonly record struct LetterboxMap(
    double Scale, double PadX, double PadY, int SourceWidth, int SourceHeight)
{
    /// <summary>画布横坐标 → 窗口内的 0–1 相对横坐标（越界一律夹回 0–1）。</summary>
    public double NormX(double canvasX) =>
        Math.Clamp((canvasX - PadX) / Math.Max(1e-6, Scale) / Math.Max(1, SourceWidth), 0, 1);

    /// <summary>画布纵坐标 → 窗口内的 0–1 相对纵坐标。</summary>
    public double NormY(double canvasY) =>
        Math.Clamp((canvasY - PadY) / Math.Max(1e-6, Scale) / Math.Max(1, SourceHeight), 0, 1);
}

/// <summary>
/// 一次姿态定位的结果：运动主体的包围盒，坐标是<b>窗口内的 0–1 相对值</b>（左上角为 0,0）。
/// 用相对坐标是刻意的：抓帧那边只认那张 160×90 的小图，相对坐标乘一下就是像素矩形，
/// 跟窗口有多大、缩放了多少都无关。
/// </summary>
/// <param name="Left">左边界 0–1。</param>
/// <param name="Top">上边界 0–1。</param>
/// <param name="Right">右边界 0–1。</param>
/// <param name="Bottom">下边界 0–1。</param>
/// <param name="Confidence">用到的关键点的平均置信度 0–1（界面显示的那个数）。</param>
/// <param name="Keypoints">用到的关键点个数（≥ <see cref="PoseTracker.MinValidKeypoints"/> 才算有效）。</param>
public readonly record struct PoseSubject(
    double Left, double Top, double Right, double Bottom, double Confidence, int Keypoints)
{
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
    /// <summary>框太小（主体几乎不可见 / 关键点挤在一起）就不值得拿它当 ROI。</summary>
    public bool IsUsable => Width >= 0.03 && Height >= 0.03;
}

/// <summary>一个人的一个关键点：<b>原图像素坐标</b>（左上角为原点）+ 置信度 0–1。</summary>
public readonly record struct PosePoint(double X, double Y, double Confidence);

/// <summary>
/// 一次检测到的人：分数、包围盒、17 个 COCO 关键点，坐标都是<b>原图像素</b>。
/// 分数是 sigmoid 之后的 0–1（原始头给的是 logit，<see cref="PoseTracker.Detect"/> 已经转换过）。
/// </summary>
public sealed record PosePerson(
    double Score,
    double X1, double Y1, double X2, double Y2,
    PosePoint[] Keypoints);

/// <summary>
/// 本地姿态定位：Xenova/yolov8n-pose 的 ONNX（YOLOv8n-pose，COCO 17 关键点，约 13MB，AGPL-3.0 上游 / 模型卡 MIT）。
///
/// <b>为什么需要它</b>：现在的运动量是在整幅画面上算的，镜头一晃、背景里的树叶一动，数字就上去了。
/// 先用姿态把「人在这幅画面的哪一块」找出来，再只在这一块里算运动量与节奏，镜头晃动的干扰就小得多。
///
/// <b>模型接口（2026-11 实测，别凭印象改）</b>：
///   来源 AXERA-TECH/YOLOv8-Pose 的 <c>yolov8n-pose_640x640.onnx</c>，是<b>未融合的原始头</b>
///   （和 Xenova 那个 56×8400 的融合头不是同一种导出，别照抄旧写法）：
///   输入名 <c>images</c>，形状 <b>[1,3,640,640] float32</b>；预处理 = letterbox 到 640×640（补 114 灰）
///   + <b>只除以 255</b>（RGB，NCHW）—— 这个导出<b>没有</b> ImageNet 标准化，别照抄分类模型那套 mean/std；
///   输出<b>9 个张量</b> = 三层（80/40/20 格）× 三个分支：
///     <c>(H,W,64)</c> 四边 × 16 bin 的 DFL 分布、<c>(H,W,1)</c> 置信度 logit、<c>(H,W,51)</c> 17 个关键点 × (x, y, 置信度 logit)。
///   所以解码要自己算 DFL 的 softmax 期望再乘 stride 还原到 640 坐标（<see cref="Decode"/>）；关键点的置信度
///   也<b>要自己过 sigmoid</b>。这套解码已和 python 参考实现对齐：bus.jpg 上 bbox 与 17 点误差 &lt; 0.4px，
///   逐项比对见 <c>tools/PoseCheck</c>。真人照片最高框分 0.88、纯灰 0.001、噪声 0.015 —— 0.35 当门槛是安全的。
///   <b>没有</b> NMS：只取分数最高的那一个框，单主体场景够用，也省掉一整套后处理。
///
/// <b>四条硬规矩（改这个类的人请一并守住）</b>：
/// ① <b>绝不偷偷下载</b>：<see cref="DownloadAsync"/> 只在用户点了界面上的「下载姿态模型」并确认之后才会被调用。
/// ② <b>画面不外传、不写盘</b>：只把内存里的那一帧缩到 640×640 喂给本机模型，没有上传、没有截图落盘。
/// ③ <b>同一时刻只允许一次推理</b>（<see cref="InferenceGate"/>）：会话与输入缓冲都是复用的。
/// ④ <b>绝不崩</b>：没装模型 / 模型坏了 / 推理异常一律被 <see cref="TryLocate"/> 消化成 false，
///    调用方据此退回「整幅画面」，不报错、不刷屏、不影响抓帧。
///
/// 为什么是 static：ONNX 会话是常驻内存，而界面/自检会 new 出好几个 ScreenWatchService；
/// 做成实例字段就会重复加载模型（和 <see cref="NsfwClassifier"/> 同一个理由）。
/// </summary>
public static class PoseTracker
{
    // ── 模型接口（数值全部来自实测，不要凭印象改）──────────────────────
    /// <summary>模型输入边长：640×640。</summary>
    public const int InputSize = 640;

    /// <summary>COCO 关键点个数（鼻、双眼、双耳、双肩、双肘、双腕、双髋、双膝、双踝）。</summary>
    public const int KeypointCount = 17;

    /// <summary>至少要有这么多关键点才认为「定位到了」——少于 6 个说明只露了个头或误检。</summary>
    public const int MinValidKeypoints = 6;

    /// <summary>三层检测头：(格子边长, stride)。80×8 + 40×16 + 20×32 覆盖 8–640 像素的人。</summary>
    private static readonly (int Size, int Stride)[] Levels = [(80, 8), (40, 16), (20, 32)];

    /// <summary>边框分支的通道数：四条边 × 16 个 DFL bin。</summary>
    private const int DflChannels = 16 * 4;

    /// <summary>关键点分支的通道数：17 × (x, y, 置信度 logit)。</summary>
    private const int KeypointChannels = KeypointCount * 3;

    /// <summary>「画面里有个人」的门槛（实测：纯灰 0.001、噪声 0.015、真人照片 0.88）。</summary>
    private const float PersonConfidence = 0.35f;

    /// <summary>letterbox 补边色 114 的浮点值（按字节预处理时没有 GDI 帮忙刷底，自己填）。</summary>
    private const float PadValue = 114f / 255f;

    /// <summary>单个关键点的置信度门槛：低于它的点不参与包围盒（遮挡/出画的手脚会被排掉）。</summary>
    private const float KeypointConfidence = 0.30f;

    /// <summary>包围盒往外扩的比例：关键点只到手腕脚踝，直接当 ROI 会把四肢末端切掉。</summary>
    private const double RoiExpand = 0.20;

    /// <summary>letterbox 的补边颜色（YOLO 官方预处理用的就是 114 灰）。</summary>
    private static readonly System.Drawing.Color PadColor = System.Drawing.Color.FromArgb(114, 114, 114);

    // ── 关键点下标（COCO 17 点顺序，模型就是这么排的）────────────────────
    // 0 鼻 / 1 左眼 / 2 右眼 / 3 左耳 / 4 右耳 —— 这五个是脸，不参与围主体（只拍到脸时框没有意义），
    // 所以下面只给躯干与四肢起名字。
    private const int LeftShoulder = 5, RightShoulder = 6, LeftElbow = 7, RightElbow = 8;
    private const int LeftWrist = 9, RightWrist = 10, LeftHip = 11, RightHip = 12;
    private const int LeftKnee = 13, RightKnee = 14, LeftAnkle = 15, RightAnkle = 16;

    /// <summary>躯干点（肩/髋/膝/踝/肘/腕）：围运动主体时优先用它们，脸的点不参与
    /// —— 只拍到脸的时候，用一个头的框去算运动量没有意义。</summary>
    private static readonly int[] BodyKeypoints =
    {
        LeftShoulder, RightShoulder, LeftElbow, RightElbow,
        LeftWrist, RightWrist, LeftHip, RightHip,
        LeftKnee, RightKnee, LeftAnkle, RightAnkle,
    };

    // ── 模型文件 ──────────────────────────────────────────────────────
    private const string ModelRepo = "AXERA-TECH/YOLOv8-Pose";
    private const string HubFileName = "yolov8n-pose_640x640.onnx";
    private const string LocalFileName = "yolov8n-pose_640.onnx";
    /// <summary>实测的精确字节数（下下来就是这么多），下载校验用它。</summary>
    private const long ExpectedBytes = 13_231_214L;
    /// <summary>完整性下限：小于它一律当成半截文件。</summary>
    private const long MinCompleteBytes = 12L * 1024 * 1024;

    /// <summary>界面上的体积文案。</summary>
    public const string SizeText = "13MB";
    /// <summary>界面上的一句话说明。</summary>
    public const string HintText = "13MB · 用来定位画面里的运动主体（定位到了就只在这一块算运动量，镜头晃也不容易乱）";

    /// <summary>模型存放目录（和识别模型同一个目录，跟着 AppSettings.DataDirectory 走）。</summary>
    public static string ModelFolder => Path.Combine(AppSettings.DataDirectory, "models");

    /// <summary>模型文件完整路径。</summary>
    public static string ModelPath => Path.Combine(ModelFolder, LocalFileName);

    private static string PartPath => ModelPath + ".part";
    private static string Mirror => $"https://hf-mirror.com/{ModelRepo}/resolve/main/{HubFileName}";
    private static string Official => $"https://huggingface.co/{ModelRepo}/resolve/main/{HubFileName}";

    // ── 状态（抓帧线程 / 姿态线程 / 界面线程都读，一律走 Volatile）─────
    private static readonly Lock Gate = new();            // 会话的创建/释放（慢操作）
    private static readonly Lock InferenceGate = new();   // 推理互斥（会话与张量都是复用的）
    private static readonly float[] InputBuffer = new float[3 * InputSize * InputSize];
    private static readonly byte[] RowBuffer = new byte[InputSize * 4];

    private static InferenceSession? _session;
    private static DenseTensor<float>? _tensor;
    private static string _inputName = "images";

    private static string _loadError = "";        // 加载/推理失败原因（空 = 没失败过）
    private static bool _loadFailed;              // 试过且失败了：不再每次重试
    private static string _statusOverride = "";   // 下载中 x% / 下载失败：原因
    private static bool _lastOperationFailed;
    private static long _installedBytes = -1;     // 缓存的文件大小（−1 = 还没读盘）
    private static long _lastInferenceMs;

    /// <summary>模型文件是不是已经下载且完整（够大）。半截文件一律当作没下载。</summary>
    public static bool IsInstalled => InstalledBytes >= MinCompleteBytes;

    /// <summary>模型文件的字节数（0 = 没下载）。读盘结果会缓存，界面每 150ms 问一次也不会打盘。</summary>
    public static long InstalledBytes
    {
        get
        {
            long cached = Interlocked.Read(ref _installedBytes);
            if (cached >= 0) return cached;
            long size = FileBytes(ModelPath);
            Interlocked.Exchange(ref _installedBytes, size);
            return size;
        }
    }

    /// <summary>ONNX 会话是否已经加载。</summary>
    public static bool IsReady => Volatile.Read(ref _session) is not null;

    /// <summary>一次推理的耗时（毫秒）。没跑过时为 0。</summary>
    public static long LastInferenceMs => Interlocked.Read(ref _lastInferenceMs);

    /// <summary>下载 / 加载有没有失败过（界面据此把状态标成警告色）。</summary>
    public static bool HasError => _loadError.Length > 0 || Volatile.Read(ref _lastOperationFailed);

    /// <summary>白话状态（直接给用户看）：没下载 / 下载中 / 已下载 / 已加载 / 加载失败：原因。</summary>
    public static string Status
    {
        get
        {
            string loadError = Volatile.Read(ref _loadError);
            if (loadError.Length > 0) return $"加载失败：{loadError}";
            string over = Volatile.Read(ref _statusOverride);
            if (over.Length > 0) return over;
            if (IsReady) return $"姿态模型已加载（{SizeText}）";
            if (IsInstalled) return $"姿态模型已下载（{SizeText}），开启同步后自动加载";
            return $"没下载姿态模型（{SizeText}，点「下载姿态模型」）";
        }
    }

    // ── 下载（必须先征得用户同意：本方法只被界面上的按钮调用）──────────

    /// <summary>
    /// 下载姿态模型：先试国内镜像，失败再试官方源。全程写 <c>*.part</c>，
    /// 校验通过才改名成正式文件，所以中途断网/取消/磁盘满都不会留下「看起来装了其实坏了」的模型。
    /// </summary>
    /// <returns>true = 现在模型是完整可用的。</returns>
    public static async Task<bool> DownloadAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        InvalidateInstalledCache();
        string modelPath = ModelPath;
        string partPath = PartPath;

        if (IsInstalled)
        {
            progress?.Report(1);
            ClearStatusOverride();
            return true;
        }

        try
        {
            Directory.CreateDirectory(ModelFolder);
            DeleteQuietly(modelPath);     // 半截的旧正式文件（太小）：留着只会让人以为装好了
            DeleteQuietly(partPath);
        }
        catch (Exception ex)
        {
            SetFailure($"下载失败：{Describe(ex)}");
            AppLogger.Warn($"姿态模型：准备下载目录失败 —— {ex.Message}");
            return false;
        }

        string lastError = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string url = attempt == 0 ? Mirror : Official;
            try
            {
                ct.ThrowIfCancellationRequested();
                Volatile.Write(ref _statusOverride, $"下载中 0%（姿态模型 · 线路 {attempt + 1}/2）");
                progress?.Report(0);

                long written = await DownloadOneAsync(url, partPath, progress, ct).ConfigureAwait(false);

                long size = FileBytes(partPath);
                if (size <= 0) size = written;
                if (size < MinCompleteBytes)
                    throw new InvalidDataException($"下到的文件只有 {size / 1048576.0:0.#}MB，不完整");

                DeleteQuietly(modelPath);
                File.Move(partPath, modelPath, overwrite: true);   // 原子改名：这一步之后才算「装好了」
                InvalidateInstalledCache();
                _loadFailed = false;
                Volatile.Write(ref _loadError, "");
                Volatile.Write(ref _lastOperationFailed, false);
                ClearStatusOverride();
                progress?.Report(1);
                AppLogger.Info($"姿态模型：下载完成 {modelPath}（{size / 1048576.0:0.#}MB，线路 {attempt + 1}）");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                DeleteQuietly(partPath);
                ClearStatusOverride();
                progress?.Report(0);
                AppLogger.Info("姿态模型：下载已取消（半截文件已清理）");
                return false;
            }
            catch (Exception ex)
            {
                // 连接超时也是 TaskCanceledException，但它不该被当成「用户取消」——走这条分支才会试下一条线路。
                DeleteQuietly(partPath);
                lastError = Describe(ex);
                AppLogger.Warn($"姿态模型：线路 {attempt + 1} 下载失败 —— {ex.Message}");
            }
        }

        SetFailure($"下载失败：{lastError}");
        progress?.Report(0);
        return false;
    }

    private static async Task<long> DownloadOneAsync(
        string url, string partPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Hexa/1.0 (local model download)");

        using HttpResponseMessage response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? 0;
        if (total < MinCompleteBytes) total = ExpectedBytes;

        var buffer = new byte[1 << 20];
        long written = 0;
        using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    buffer.Length, useAsync: true))
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;

                // 只报到 99%：最后 1% 留给「落盘 + 校验 + 改名」。
                double ratio = Math.Clamp(written / (double)total, 0, 1) * 0.99;
                Volatile.Write(ref _statusOverride, $"下载中 {ratio * 100:0}%");
                progress?.Report(ratio);
            }
            await file.FlushAsync(ct).ConfigureAwait(false);
        }
        return written;
    }

    /// <summary>删掉本机的姿态模型（腾空间）。会话一起释放，之后自动退回「整幅画面」。</summary>
    public static bool Delete()
    {
        lock (InferenceGate)
        {
            lock (Gate)
            {
                DeleteQuietly(PartPath);
                DisposeSessionLocked();
                try
                {
                    if (File.Exists(ModelPath)) File.Delete(ModelPath);
                }
                catch (Exception ex)
                {
                    SetFailure($"删除失败：{Describe(ex)}");
                    InvalidateInstalledCache();
                    AppLogger.Warn($"姿态模型：删除失败 {ModelPath} —— {ex.Message}");
                    return false;
                }
                InvalidateInstalledCache();
                Volatile.Write(ref _loadError, "");
                _loadFailed = false;
                Volatile.Write(ref _lastOperationFailed, false);
                ClearStatusOverride();
                Interlocked.Exchange(ref _lastInferenceMs, 0);
                AppLogger.Info("姿态模型：已删除本机的姿态模型文件");
                return true;
            }
        }
    }

    // ── 预处理（由抓帧线程调用：GDI 的活儿留在抓帧线程，姿态线程只做纯推理）──

    /// <summary>
    /// 把一帧源图按比例缩进 640×640 的灰底画布（letterbox：保持长宽比、居中、两侧补 114 灰），
    /// 返回坐标换算表。源图不会被改动，画布尺寸必须正好是 <see cref="InputSize"/>。
    /// </summary>
    public static LetterboxMap DrawLetterbox(System.Drawing.Bitmap source, System.Drawing.Bitmap canvas)
    {
        int sourceWidth = Math.Max(1, source.Width);
        int sourceHeight = Math.Max(1, source.Height);
        double scale = Math.Min(InputSize / (double)sourceWidth, InputSize / (double)sourceHeight);
        int width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, InputSize);
        int height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, InputSize);
        double padX = (InputSize - width) / 2.0;
        double padY = (InputSize - height) / 2.0;

        using var graphics = System.Drawing.Graphics.FromImage(canvas);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.Clear(PadColor);
        graphics.DrawImage(source,
            new System.Drawing.Rectangle((int)Math.Round(padX), (int)Math.Round(padY), width, height),
            0, 0, sourceWidth, sourceHeight, System.Drawing.GraphicsUnit.Pixel);

        return new LetterboxMap(scale, padX, padY, sourceWidth, sourceHeight);
    }

    // ── 推理 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 在 640×640 的画布上跑一次姿态推理，算出运动主体的包围盒。
    ///
    /// <b>失败一律返回 false</b>（没装模型、模型坏了、画面里没人、关键点太少、推理异常），
    /// 调用方看到 false 就退回「整幅画面」，绝不抛异常给抓帧/界面线程，也不刷日志。
    /// </summary>
    /// <param name="canvas">必须是 <see cref="DrawLetterbox"/> 画好的 640×640 画布。</param>
    /// <param name="map">画这张画布时得到的坐标换算表。</param>
    /// <param name="subject">定位到的主体（返回 true 时有效）。</param>
    public static bool TryLocate(System.Drawing.Bitmap canvas, in LetterboxMap map, out PoseSubject subject)
    {
        subject = default;
        if (canvas is null || canvas.Width != InputSize || canvas.Height != InputSize) return false;

        LetterboxMap local = map;                       // in 参数不能被 lambda 捕获，先拷一份
        PosePerson? person = InferWithGuard(() => FillInputFromCanvas(canvas), PersonConfidence);
        return person is not null && TryBuildSubject(person.Keypoints, local, out subject);
    }

    /// <summary>
    /// 直接在 BGRA32 像素上跑一帧，返回分数最高的那个人（坐标是<b>原图像素</b>）；画面里没人则 null。
    ///
    /// 和 <see cref="TryLocate"/> 的差别只有预处理：这边吃 raw 像素、自己做双线性 letterbox
    /// （抓帧线程手上已经有一块 160×90 的 BGRA，不必再倒一手 GDI）；会话、解码、失败处理完全是同一套，
    /// 所以两个入口永远不可能各跑一份模型。
    /// </summary>
    /// <param name="bgra">BGRA32 像素（屏幕抓帧直接就是这个格式）。</param>
    /// <param name="minScore">「画面里有个人」的门槛，默认 <see cref="PersonConfidence"/>。</param>
    public static PosePerson? Detect(byte[] bgra, int width, int height, float minScore = PersonConfidence)
    {
        if (bgra is null || width <= 0 || height <= 0 || bgra.LongLength < (long)width * height * 4) return null;

        FitBox box = FitBox.Fit(width, height);
        PosePerson? person = InferWithGuard(() => FillInputFromBytes(bgra, width, height, box), minScore);
        if (person is null) return null;

        var points = new PosePoint[person.Keypoints.Length];
        for (int i = 0; i < points.Length; i++) points[i] = Unletterbox(person.Keypoints[i], box);
        return new PosePerson(person.Score,
            UnletterboxX(person.X1, box), UnletterboxY(person.Y1, box),
            UnletterboxX(person.X2, box), UnletterboxY(person.Y2, box), points);
    }

    /// <summary>
    /// 所有推理的唯一入口：拿会话 → 填输入 → 跑 9 个输出 → 解出分数最高的那个框。
    /// <b>失败一律返回 null</b>（没装模型 / 模型坏了 / 推理异常），原因只记一次，绝不每帧刷日志、绝不抛给调用方。
    /// </summary>
    /// <param name="fillInput">把像素写进复用的 <see cref="InputBuffer"/>（两种预处理共用的唯一差别）。</param>
    private static PosePerson? InferWithGuard(Action fillInput, float minScore)
    {
        lock (InferenceGate)     // 同一时刻只允许一次推理：会话和输入缓冲都是复用的
        {
            try
            {
                InferenceSession? session = Volatile.Read(ref _session);
                if (session is null)
                {
                    session = LoadSessionLocked();
                    if (session is null) return null;    // 原因已经写在 Status 里
                }

                var watch = Stopwatch.StartNew();
                fillInput();                             // 只除以 255 → NCHW，全写进复用的 InputBuffer
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                    session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, EnsureTensor()) });
                watch.Stop();
                Interlocked.Exchange(ref _lastInferenceMs, watch.ElapsedMilliseconds);
                return Decode(results, minScore);
            }
            catch (Exception ex)
            {
                // 推理出错不能让抓帧线程死掉，也不能每帧刷屏：记一次状态就够，界面会显示原因。
                if (Volatile.Read(ref _loadError).Length == 0)
                {
                    Volatile.Write(ref _loadError, DescribeInference(ex));
                    AppLogger.Warn($"姿态模型：推理失败（退回整幅画面）—— {ex.Message}");
                }
                return null;
            }
        }
    }

    /// <summary>
    /// 从 9 个输出张量里解出<b>分数最高的那一个人</b>（坐标还在 640 画布上）。
    ///
    /// <b>单目标语义</b>：这个模型的输出没做 NMS，同一个人会落在好几个相邻格点上；但这里只返回一个框，
    /// 抑制别的框对返回值本来就没有任何影响（旧实现那圈 IoU 抑制是白算的）。所以这里只保留
    /// 「分数比当前最高分高才解」——解出来的框唯一，语义也就唯一，还省掉了整张候选表。
    /// 画面里有两个人时取分数高的那个：至少帧与帧之间是稳定的，而多主体跟踪是另一个量级的工程。
    /// </summary>
    private static PosePerson? Decode(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, float minScore)
    {
        PosePerson? best = null;
        foreach ((int size, int stride) in Levels)
        {
            // 原始头的三层各出三个张量：按（格子边长, 通道数）认出这一组。
            Tensor<float>? dfl = null, cls = null, kpts = null;
            foreach (DisposableNamedOnnxValue value in results)
            {
                if (value.Value is not Tensor<float> tensor || tensor.Dimensions.Length != 4) continue;
                if (tensor.Dimensions[1] != size || tensor.Dimensions[2] != size) continue;
                switch (tensor.Dimensions[3])
                {
                    case DflChannels: dfl ??= tensor; break;
                    case 1: cls ??= tensor; break;
                    case KeypointChannels: kpts ??= tensor; break;
                }
            }
            if (dfl is null || cls is null || kpts is null) continue;   // 缺分支就跳过这一层，绝不猜布局

            for (int gy = 0; gy < size; gy++)
            {
                for (int gx = 0; gx < size; gx++)
                {
                    float score = (float)Sigmoid(cls[0, gy, gx, 0]);
                    if (score < minScore) continue;                  // 这一格不是人
                    if (best is not null && score <= best.Score) continue;   // 不如手上那个：连 DFL 都不算
                    best = DecodeOne(dfl, kpts, gx, gy, stride, score);
                }
            }
        }
        return best;
    }

    /// <summary>解一个格点：DFL 期望 × stride 还原四条边，关键点 = (2 × 偏移 + 格点) × stride。</summary>
    private static PosePerson DecodeOne(
        Tensor<float> dfl, Tensor<float> kpts, int gx, int gy, int stride, double score)
    {
        double cx = (gx + 0.5) * stride, cy = (gy + 0.5) * stride;
        double left = Dfl(dfl, gy, gx, 0) * stride;
        double top = Dfl(dfl, gy, gx, 16) * stride;
        double right = Dfl(dfl, gy, gx, 32) * stride;
        double bottom = Dfl(dfl, gy, gx, 48) * stride;

        var points = new PosePoint[KeypointCount];
        for (int i = 0; i < KeypointCount; i++)
        {
            points[i] = new PosePoint(
                (kpts[0, gy, gx, i * 3 + 0] * 2 + gx) * stride,
                (kpts[0, gy, gx, i * 3 + 1] * 2 + gy) * stride,
                Sigmoid(kpts[0, gy, gx, i * 3 + 2]));
        }
        return new PosePerson(score, cx - left, cy - top, cx + right, cy + bottom, points);
    }

    /// <summary>DFL：16 个 bin 做 softmax 后取期望（× stride 就是像素距离）。</summary>
    private static double Dfl(Tensor<float> tensor, int gy, int gx, int offset)
    {
        double max = double.MinValue;
        for (int b = 0; b < 16; b++) max = Math.Max(max, tensor[0, gy, gx, offset + b]);
        double sum = 0, weighted = 0;
        for (int b = 0; b < 16; b++)
        {
            double e = Math.Exp(tensor[0, gy, gx, offset + b] - max);
            sum += e;
            weighted += e * b;
        }
        return sum <= 0 ? 0 : weighted / sum;
    }

    private static double Sigmoid(double value) => 1.0 / (1.0 + Math.Exp(-value));

    /// <summary>
    /// 关键点（640 画布像素）→ 主体包围盒（窗口内 0–1）。只认躯干/四肢点（脸不参与，只拍到脸时框没意义），
    /// 外扩 20% 补上手腕脚踝之外的部分，有效点少于 <see cref="MinValidKeypoints"/> 就当没定位到。
    /// </summary>
    private static bool TryBuildSubject(PosePoint[] keypoints, in LetterboxMap map, out PoseSubject subject)
    {
        subject = default;
        if (keypoints is null || keypoints.Length < KeypointCount) return false;

        int valid = 0;
        double sumConfidence = 0;
        double bodyMinX = double.MaxValue, bodyMinY = double.MaxValue;
        double bodyMaxX = double.MinValue, bodyMaxY = double.MinValue;
        double anyMinX = double.MaxValue, anyMinY = double.MaxValue;
        double anyMaxX = double.MinValue, anyMaxY = double.MinValue;

        for (int k = 0; k < KeypointCount; k++)
        {
            PosePoint point = keypoints[k];
            if (point.Confidence < KeypointConfidence) continue;
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;

            valid++;
            sumConfidence += point.Confidence;
            anyMinX = Math.Min(anyMinX, point.X); anyMaxX = Math.Max(anyMaxX, point.X);
            anyMinY = Math.Min(anyMinY, point.Y); anyMaxY = Math.Max(anyMaxY, point.Y);
            if (!IsBodyKeypoint(k)) continue;
            bodyMinX = Math.Min(bodyMinX, point.X); bodyMaxX = Math.Max(bodyMaxX, point.X);
            bodyMinY = Math.Min(bodyMinY, point.Y); bodyMaxY = Math.Max(bodyMaxY, point.Y);
        }

        if (valid < MinValidKeypoints) return false;     // 只露了个头 / 误检：不给 ROI
        // 一个躯干点都没有（只有脸）时退回「所有关键点」的框，聊胜于无。
        if (bodyMinX > bodyMaxX) { bodyMinX = anyMinX; bodyMaxX = anyMaxX; bodyMinY = anyMinY; bodyMaxY = anyMaxY; }

        // 外扩 20%：关键点只到手腕脚踝，紧贴它们会把四肢末端切在框外。
        double width = Math.Max(0, bodyMaxX - bodyMinX);
        double height = Math.Max(0, bodyMaxY - bodyMinY);
        double left = bodyMinX - width * RoiExpand;
        double right = bodyMaxX + width * RoiExpand;
        double top = bodyMinY - height * RoiExpand;
        double bottom = bodyMaxY + height * RoiExpand;

        subject = new PoseSubject(
            map.NormX(left), map.NormY(top), map.NormX(right), map.NormY(bottom),
            sumConfidence / valid, valid);
        return subject.IsUsable;
    }

    private static bool IsBodyKeypoint(int index)
    {
        foreach (int candidate in BodyKeypoints)
            if (candidate == index) return true;
        return false;
    }

    /// <summary>懒加载 ONNX 会话。失败后不再每次重试，直到重新下载 / 删除。</summary>
    private static InferenceSession? LoadSessionLocked()
    {
        InferenceSession? existing = Volatile.Read(ref _session);
        if (existing is not null) return existing;
        if (_loadFailed) return null;
        if (!IsInstalled) return null;

        lock (Gate)
        {
            existing = Volatile.Read(ref _session);
            if (existing is not null) return existing;
            Volatile.Write(ref _loadError, "");
            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    // 640×640 比分类模型大得多，多给几个核；但**不占满** ——
                    // 抓帧（13fps）、识别模型、串口通信都要留核，画面同步绝不能把设备通信饿死。
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 2, 1, 6),
                    InterOpNumThreads = 1,
                };
                var watch = Stopwatch.StartNew();
                var session = new InferenceSession(ModelPath, options);
                watch.Stop();

                // 输入/输出名字以模型元数据为准（实测是 images / output0），拿不到就用实测值兜底。
                _inputName = FirstKey(session.InputMetadata.Keys) ?? "images";   // 输出按形状配对，不记名字

                Volatile.Write(ref _session, session);
                Volatile.Write(ref _lastOperationFailed, false);
                AppLogger.Info($"姿态模型：已加载（{watch.ElapsedMilliseconds}ms，输入 {_inputName}，输出 {session.OutputMetadata.Count} 个）");
                return session;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                Volatile.Write(ref _loadError, DescribeLoad(ex));
                Volatile.Write(ref _lastOperationFailed, true);
                AppLogger.Warn($"姿态模型：加载失败（退回整幅画面）—— {ex.Message}");
                return null;
            }
        }
    }

    private static void DisposeSessionLocked()
    {
        InferenceSession? session = Volatile.Read(ref _session);
        Volatile.Write(ref _session, null);
        try { session?.Dispose(); }
        catch (Exception ex) { AppLogger.Warn($"姿态模型：释放会话时出错 —— {ex.Message}"); }
    }

    /// <summary>输入张量只建一次，包在复用的 <see cref="InputBuffer"/> 上 —— 绝不每帧新分配大数组。</summary>
    private static DenseTensor<float> EnsureTensor()
    {
        DenseTensor<float>? tensor = _tensor;
        if (tensor is not null) return tensor;
        Memory<float> memory = InputBuffer;   // 显式转 Memory<T>：避免和别的重载歧义
        tensor = new DenseTensor<float>(memory, new[] { 1, 3, InputSize, InputSize });
        _tensor = tensor;
        return tensor;
    }

    /// <summary>
    /// 把 640×640 画布的像素填进 NCHW 张量（RGB 三平面）。
    /// 归一化只有 /255 —— 这个导出<b>没有</b> ImageNet 的 mean/std，加了反而全错。
    /// 一次 LockBits + 逐行 Marshal.Copy，绝不在循环里 GetPixel。
    /// </summary>
    private static void FillInputFromCanvas(System.Drawing.Bitmap canvas)
    {
        const int plane = InputSize * InputSize;
        System.Drawing.Imaging.BitmapData data = canvas.LockBits(
            new System.Drawing.Rectangle(0, 0, InputSize, InputSize),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < InputSize; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), RowBuffer, 0, InputSize * 4);
                int rowBase = y * InputSize;
                for (int x = 0; x < InputSize; x++)
                {
                    int p = x * 4;                       // Format32bppArgb 的内存顺序是 B,G,R,A
                    byte b = RowBuffer[p], g = RowBuffer[p + 1], r = RowBuffer[p + 2];
                    int i = rowBase + x;
                    InputBuffer[i] = r * (1f / 255f);
                    InputBuffer[plane + i] = g * (1f / 255f);
                    InputBuffer[2 * plane + i] = b * (1f / 255f);
                }
            }
        }
        finally
        {
            canvas.UnlockBits(data);   // GDI+ 的锁必须还回去，否则这块位图以后再用就抛异常
        }
    }

    /// <summary>
    /// 把一块 BGRA32 像素按 letterbox 几何填进张量：画内用<b>双线性</b>采样（cv2.INTER_LINEAR 的
    /// half-pixel 约定：目标像素中心 → 源像素中心），画外一律填 114 灰。
    ///
    /// 为什么不能只用最近邻：160×90 → 640×360 是 4 倍放大，最近邻会带来 1–4px 的系统性关键点偏移，
    /// 髋/踝这种要算深度的点直接被这个偏移吃掉。
    /// </summary>
    private static void FillInputFromBytes(byte[] bgra, int width, int height, in FitBox box)
    {
        const int plane = InputSize * InputSize;
        // 采样用「源/贴入尺寸」的比例（和贴图那一步严格互逆）；换算回原图时才用 box.Scale，
        // 两者差的是取整误差（<0.5%），和 python 参考实现保持一致。
        double stepX = width / (double)box.Width;
        double stepY = height / (double)box.Height;

        for (int y = 0; y < InputSize; y++)
        {
            int rowBase = y * InputSize;
            bool rowInside = y >= box.PadY && y < box.PadY + box.Height;
            for (int x = 0; x < InputSize; x++)
            {
                float r = PadValue, g = PadValue, b = PadValue;
                if (rowInside && x >= box.PadX && x < box.PadX + box.Width)
                {
                    double fx = (x - box.PadX + 0.5) * stepX - 0.5;
                    double fy = (y - box.PadY + 0.5) * stepY - 0.5;
                    b = SampleBilinear(bgra, width, height, fx, fy, 0);   // BGRA 内存序
                    g = SampleBilinear(bgra, width, height, fx, fy, 1);
                    r = SampleBilinear(bgra, width, height, fx, fy, 2);
                }
                int i = rowBase + x;
                InputBuffer[i] = r;
                InputBuffer[plane + i] = g;
                InputBuffer[2 * plane + i] = b;
            }
        }
    }

    /// <summary>双线性采样一个通道（channel = BGRA 里的字节偏移），越界夹到边缘。</summary>
    private static float SampleBilinear(byte[] bgra, int width, int height, double fx, double fy, int channel)
    {
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double ax = fx - x0, ay = fy - y0;
        x0 = Math.Clamp(x0, 0, width - 1);
        y0 = Math.Clamp(y0, 0, height - 1);
        int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        double v00 = bgra[(y0 * width + x0) * 4 + channel];
        double v01 = bgra[(y0 * width + x1) * 4 + channel];
        double v10 = bgra[(y1 * width + x0) * 4 + channel];
        double v11 = bgra[(y1 * width + x1) * 4 + channel];
        double top = v00 + (v01 - v00) * ax;
        double bottom = v10 + (v11 - v10) * ax;
        return (float)((top + (bottom - top) * ay) / 255.0);
    }

    /// <summary>一次 letterbox 的像素几何（字节路径用）：缩放比、贴进去的宽高、左上角灰边。</summary>
    private readonly record struct FitBox(double Scale, int Width, int Height, int PadX, int PadY)
    {
        /// <summary>等比缩放并居中，和 <see cref="DrawLetterbox"/> 同一套算法（那边走 GDI，这边只算数）。</summary>
        public static FitBox Fit(int sourceWidth, int sourceHeight)
        {
            double scale = Math.Min(
                InputSize / (double)Math.Max(1, sourceWidth),
                InputSize / (double)Math.Max(1, sourceHeight));
            int width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, InputSize);
            int height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, InputSize);
            return new FitBox(scale, width, height, (InputSize - width) / 2, (InputSize - height) / 2);
        }
    }

    private static double UnletterboxX(double canvasX, in FitBox box) => (canvasX - box.PadX) / box.Scale;
    private static double UnletterboxY(double canvasY, in FitBox box) => (canvasY - box.PadY) / box.Scale;

    private static PosePoint Unletterbox(in PosePoint point, in FitBox box) =>
        new(UnletterboxX(point.X, box), UnletterboxY(point.Y, box), point.Confidence);

    // ── 小工具 ────────────────────────────────────────────────────────

    private static string? FirstKey(IEnumerable<string> keys)
    {
        foreach (string key in keys) return key;
        return null;
    }

    private static long FileBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;   // 读不到就当没有：绝不因为一次 IO 把界面搞崩
        }
    }

    private static void InvalidateInstalledCache() => Interlocked.Exchange(ref _installedBytes, -1);

    private static void SetFailure(string text)
    {
        Volatile.Write(ref _statusOverride, text);
        Volatile.Write(ref _lastOperationFailed, true);
    }

    private static void ClearStatusOverride()
    {
        Volatile.Write(ref _statusOverride, "");
        Volatile.Write(ref _lastOperationFailed, false);
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLogger.Warn($"姿态模型：删除文件失败 {path} —— {ex.Message}"); }
    }

    /// <summary>下载相关的异常 → 白话原因。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http when http.StatusCode is not null
            => $"镜像返回 {(int)http.StatusCode.Value}（链接可能变了，或镜像暂时不可用）",
        HttpRequestException => "连不上镜像（网络不通，或者镜像暂时不可用）",
        TaskCanceledException => "下载超时（网络太慢或镜像没响应）",
        OperationCanceledException => "已取消",
        UnauthorizedAccessException => $"没有写入权限（{ModelFolder}）",
        IOException io when IsDiskFull(io) => $"磁盘空间不足（{ModelFolder} 至少留 50MB）",
        InvalidDataException invalid => invalid.Message,
        IOException => "写文件出错（磁盘满、或文件被杀毒软件占用）",
        _ => $"{ex.GetType().Name}：{Short(ex.Message)}",
    };

    /// <summary>加载/推理相关的异常 → 白话原因。</summary>
    private static string DescribeLoad(Exception ex) => ex switch
    {
        DllNotFoundException => "缺少推理运行库（onnxruntime.dll 没跟着程序一起装出来）",
        BadImageFormatException => "推理运行库位数不对（需要 64 位）",
        FileNotFoundException => "模型文件不在了（被删掉或被安全软件隔离）",
        OnnxRuntimeException => "模型文件读不了，可能没下完整（点「删除姿态模型」再下一次）",
        _ => $"{ex.GetType().Name}：{Short(ex.Message)}",
    };

    private static string DescribeInference(Exception ex) => ex switch
    {
        OnnxRuntimeException => "推理出错，可能模型文件坏了（点「删除姿态模型」再下一次）",
        _ => $"{ex.GetType().Name}：{Short(ex.Message)}",
    };

    private static bool IsDiskFull(IOException ex)
    {
        const int DiskFull = unchecked((int)0x80070070);         // ERROR_DISK_FULL
        const int HandleDiskFull = unchecked((int)0x8007006F);   // ERROR_HANDLE_DISK_FULL
        return ex.HResult == DiskFull || ex.HResult == HandleDiskFull
            || ex.Message.Contains("space", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("磁盘", StringComparison.Ordinal);
    }

    /// <summary>异常消息截断：ONNX 的报错可能几百字符，界面上只要一眼能看懂的一小段。</summary>
    private static string Short(string message)
    {
        string oneLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 90 ? oneLine : oneLine[..90] + "…";
    }
}
