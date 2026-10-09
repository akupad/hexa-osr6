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

/// <summary>
/// 本地姿态定位：Xenova/yolov8n-pose 的 ONNX（YOLOv8n-pose，COCO 17 关键点，约 13MB，AGPL-3.0 上游 / 模型卡 MIT）。
///
/// <b>为什么需要它</b>：现在的运动量是在整幅画面上算的，镜头一晃、背景里的树叶一动，数字就上去了。
/// 先用姿态把「人在这幅画面的哪一块」找出来，再只在这一块里算运动量与节奏，镜头晃动的干扰就小得多。
///
/// <b>模型接口（2026-09 实测，别凭印象改）</b>：
///   输入名 <c>images</c>，形状 <b>[1,3,640,640] float32</b>；预处理 = letterbox 到 640×640（补 114 灰）
///   + <b>只除以 255</b>（RGB，NCHW）—— 这个导出<b>没有</b> ImageNet 标准化，别照抄分类模型那套 mean/std；
///   输出名 <c>output0</c>，形状 <b>[1,56,8400]</b>，即 8400 个候选框，每个 56 个数 =
///   <c>[cx, cy, w, h, 置信度, 17 × (x, y, 置信度)]</c>，坐标都在 640 画布像素上、置信度<b>已经过 sigmoid</b>
///   （实测：给一张有人的照片，最高置信度 0.88，关键点全部落在人物框内且横纵范围与框一致；
///    给纯灰图最高 0.001、给噪声图 0.015 —— 所以 0.35 当「有没有人」的门槛是安全的）。
///   <b>没有</b> NMS：这里只取置信度最高的那一个框，单主体场景够用，也省掉一整套后处理。
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

    /// <summary>每个候选框的长度：4 框 + 1 置信度 + 17 × 3 关键点。</summary>
    private const int Attributes = 5 + KeypointCount * 3;      // = 56

    /// <summary>「画面里有个人」的门槛（实测：纯灰 0.001、噪声 0.015、真人照片 0.88）。</summary>
    private const float PersonConfidence = 0.35f;

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
    private const string HubRepo = "Xenova/yolov8n-pose";
    private const string HubPath = "onnx/model.onnx";
    private const string LocalFileName = "yolov8n-pose.onnx";
    /// <summary>实测的精确字节数（curl 下下来就是这么多），下载校验用它。</summary>
    private const long ExpectedBytes = 13_484_153L;
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
    private static string Mirror => $"https://hf-mirror.com/{HubRepo}/resolve/main/{HubPath}";
    private static string Official => $"https://huggingface.co/{HubRepo}/resolve/main/{HubPath}";

    // ── 状态（抓帧线程 / 姿态线程 / 界面线程都读，一律走 Volatile）─────
    private static readonly Lock Gate = new();            // 会话的创建/释放（慢操作）
    private static readonly Lock InferenceGate = new();   // 推理互斥（会话与张量都是复用的）
    private static readonly float[] InputBuffer = new float[3 * InputSize * InputSize];
    private static readonly byte[] RowBuffer = new byte[InputSize * 4];

    private static InferenceSession? _session;
    private static DenseTensor<float>? _tensor;
    private static string _inputName = "images";
    private static string _outputName = "output0";

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
            if (IsInstalled) return $"姿态模型已下载（{SizeText}），开启观察后自动加载";
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

        lock (InferenceGate)     // 同一时刻只允许一次推理：会话和张量都是复用的
        {
            try
            {
                InferenceSession? session = Volatile.Read(ref _session);
                if (session is null)
                {
                    session = LoadSessionLocked();
                    if (session is null) return false;    // 原因已经写在 Status 里
                }

                DenseTensor<float> tensor = EnsureTensor();
                var watch = Stopwatch.StartNew();
                FillInput(canvas);                        // 只除以 255 → NCHW，全写进复用的 InputBuffer
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                    session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) }, new[] { _outputName });
                watch.Stop();
                Interlocked.Exchange(ref _lastInferenceMs, watch.ElapsedMilliseconds);

                foreach (DisposableNamedOnnxValue value in results)
                {
                    // ToArray() 会分配一块 1.8MB 的输出数组 —— 4 次/秒，换来的是「绝不猜 API」。
                    // （想省掉它要先确认 DenseTensor.Buffer 这个 API，不能编译的场合不值得赌。）
                    return Parse(value.AsTensor<float>().ToArray(), map, out subject);
                }
                return false;
            }
            catch (Exception ex)
            {
                // 推理出错不能让抓帧线程死掉，也不能每帧刷屏：记一次状态就够，界面会显示原因。
                if (Volatile.Read(ref _loadError).Length == 0)
                {
                    Volatile.Write(ref _loadError, DescribeInference(ex));
                    AppLogger.Warn($"姿态模型：推理失败（退回整幅画面）—— {ex.Message}");
                }
                return false;
            }
        }
    }

    /// <summary>
    /// 从输出张量里挑出「最像人的那一个候选框」，把关键点换算成窗口内的相对坐标，再围出主体框。
    ///
    /// 为什么只取置信度最高的一个：模型没做 NMS，同一个人会有好几个高分框；画面里同时有两个人时，
    /// 取最高的那个至少是稳定的（不会在两帧之间来回跳），而且这个信号的用途是「别让镜头晃动污染运动量」，
    /// 多主体跟踪是另一个量级的工程。
    /// </summary>
    private static bool Parse(ReadOnlySpan<float> data, in LetterboxMap map, out PoseSubject subject)
    {
        subject = default;
        if (data.Length < Attributes) return false;
        int anchors = data.Length / Attributes;          // 实测 8400
        if (anchors <= 0) return false;

        int best = -1;
        float bestConfidence = PersonConfidence;
        for (int i = 0; i < anchors; i++)
        {
            float confidence = data[4 * anchors + i];
            if (confidence > bestConfidence) { bestConfidence = confidence; best = i; }
        }
        if (best < 0) return false;                      // 这一帧里没有人

        // 关键点：通道下标 = 5 + 3k + {0=x,1=y,2=置信度}，每个通道长 anchors。
        int offset = 5 * anchors + best;
        int valid = 0;
        double sumConfidence = 0;
        double bodyMinX = double.MaxValue, bodyMinY = double.MaxValue;
        double bodyMaxX = double.MinValue, bodyMaxY = double.MinValue;
        double anyMinX = double.MaxValue, anyMinY = double.MaxValue;
        double anyMaxX = double.MinValue, anyMaxY = double.MinValue;

        for (int k = 0; k < KeypointCount; k++)
        {
            int at = offset + k * anchors;
            if (at + 2 * anchors >= data.Length) break;
            float confidence = data[at + 2 * anchors];
            if (confidence < KeypointConfidence) continue;
            float x = data[at];
            float y = data[at + anchors];
            if (!float.IsFinite(x) || !float.IsFinite(y)) continue;

            valid++;
            sumConfidence += confidence;
            anyMinX = Math.Min(anyMinX, x); anyMaxX = Math.Max(anyMaxX, x);
            anyMinY = Math.Min(anyMinY, y); anyMaxY = Math.Max(anyMaxY, y);
            if (!IsBodyKeypoint(k)) continue;
            bodyMinX = Math.Min(bodyMinX, x); bodyMaxX = Math.Max(bodyMaxX, x);
            bodyMinY = Math.Min(bodyMinY, y); bodyMaxY = Math.Max(bodyMaxY, y);
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
                    // 抓帧（13fps）、识别模型、串口通信都要留核，画面跟随绝不能把设备通信饿死。
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 2, 1, 6),
                    InterOpNumThreads = 1,
                };
                var watch = Stopwatch.StartNew();
                var session = new InferenceSession(ModelPath, options);
                watch.Stop();

                // 输入/输出名字以模型元数据为准（实测是 images / output0），拿不到就用实测值兜底。
                _inputName = FirstKey(session.InputMetadata.Keys) ?? "images";
                _outputName = FirstKey(session.OutputMetadata.Keys) ?? "output0";

                Volatile.Write(ref _session, session);
                Volatile.Write(ref _lastOperationFailed, false);
                AppLogger.Info($"姿态模型：已加载（{watch.ElapsedMilliseconds}ms，输入 {_inputName}，输出 {_outputName}）");
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
    private static void FillInput(System.Drawing.Bitmap canvas)
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
