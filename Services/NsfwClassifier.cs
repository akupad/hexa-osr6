using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Hexa.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Hexa.Services;

/// <summary>
/// 一个可选档位的模型（同一套接口、同一套预处理，只有体积/精度/速度不同）。
/// 三档是三个<b>独立的文件</b>（文件名带档位后缀），换档不会互相覆盖。
/// </summary>
public sealed record NsfwModelVariant(
    string Id,              // "s" / "m" / "l"
    string RepoName,        // huggingface 仓库名
    string FileName,        // 本机文件名（带档位后缀）
    long ExpectedBytes,     // 已知的精确字节数（0 = 以服务器给的 Content-Length 为准）
    long MinCompleteBytes,  // 完整性的下限：小于它一律当成半截文件
    string SizeText,        // 界面上的体积（"44MB"）
    string Label,           // 下拉里的显示名
    string HintText)        // 准确率 + 实测耗时（白话）
{
    /// <summary>国内镜像（实测通）：huggingface 直连被墙，所以它排第一。</summary>
    public string Mirror => $"https://hf-mirror.com/{RepoName}/resolve/main/onnx/{FileName}";
    /// <summary>官方源：国内通常连不上，留着当兜底。</summary>
    public string OfficialMirror => $"https://huggingface.co/{RepoName}/resolve/main/onnx/{FileName}";
}

/// <summary>
/// 本地「画面是不是情色内容」分类模型：OwenElliott/image-safety-classifier-{s|m|l}（MIT，2026-03）。
///
/// 为什么用它（而不是老的那些 NSFW 检测器）：训练集约 32 万张，明确包含<b>真人照片 / 绘画 / Rule34 /
/// 截图 / AI 生成图 / 梗图</b> —— 成人工游戏与二次元场景正好落在它的覆盖范围里；
/// SwiftFormer 架构本来就是给边缘设备做的，比 ViT-base 快好几倍（中档实测 13–20ms）。
///
/// 三档（界面下拉选，设置里存 <see cref="AppSettings.NsfwModelTier"/>）：
///   s = 22MB，自报准确率 97.99%，最快；
///   m = 44MB（默认，推荐），自报 98.06%，实测每帧 13–20ms；
///   l = 106MB，自报 98.20%，最准，实测每帧 23–38ms。
///
/// 四条硬规矩（改这个类的人请一并守住）：
/// ① <b>绝不偷偷下载</b>：<see cref="DownloadAsync"/> 只在用户点了界面上的「下载模型」并确认之后才会被调用，
///    本类自己不发起任何网络请求。
/// ② <b>画面不外传、不写盘</b>：只把内存里那一帧缩到 224×224 喂给本机模型，
///    没有上传、没有截图落盘、没有临时图片文件。
/// ③ <b>同一时刻只允许一次推理</b>（<see cref="InferenceGate"/>）：ONNX 会话和输入张量都是复用的，
///    并发进去会把两块数据搅在一起。
/// ④ <b>绝不崩</b>：下载失败、文件损坏、推理异常一律被消化成 <see cref="Status"/> 里的白话字符串；
///    <see cref="Classify"/> 失败返回 −1，调用方据此退回手工特征。
///
/// 模型接口（照实测的写，别按老模型改）：
///   输入名 <c>image</c>（<b>不是</b> pixel_values），形状 [1,3,224,224] float32；
///   预处理 ImageNet 标准化 (x/255 − mean)/std，mean = [0.485,0.456,0.406]，std = [0.229,0.224,0.225]
///   （<b>不是</b> /127.5−1）；缩放先按短边缩到 236 再中心裁 224（crop_pct = 224/236 ≈ 0.95）；
///   输出 [-1,3] logits，标签顺序 <b>[NSFL, NSFW, SFW]</b> → <b>NSFW 是索引 1</b>。
///
/// 为什么整个类是 static：ONNX 会话是几十 MB 级别的常驻内存，而自检会 new 出好几个
/// <see cref="ScreenWatchService"/>（每个都持有一个分类器）。做成实例字段就会重复加载模型，
/// 所以「会话 + 输入缓冲 + 锁 + 档位」全进程一份。
/// </summary>
public static class NsfwClassifier
{
    // ── 模型接口与预处理（数值全部来自实测，不要凭印象改）──────────────
    /// <summary>模型输入边长：224×224。</summary>
    public const int InputSize = 224;

    /// <summary>缩放后短边的目标长度：224 / 0.95 = 235.8 → 236，然后再中心裁 224（就是 torchvision 的 Resize+CenterCrop）。</summary>
    public const int ShortSide = 236;

    /// <summary>输出 3 个 logits，顺序是 [NSFL（血腥暴力）, NSFW（情色）, SFW（安全）]。</summary>
    private const int NsflIndex = 0;
    private const int NsfwIndex = 1;
    private const int OutputCount = 3;

    /// <summary>ImageNet 标准化参数（照 preprocessor_config / timm 的默认值）。</summary>
    private const float MeanR = 0.485f;
    private const float MeanG = 0.456f;
    private const float MeanB = 0.406f;
    private const float InvStdR = 1f / 0.229f;
    private const float InvStdG = 1f / 0.224f;
    private const float InvStdB = 1f / 0.225f;

    /// <summary>小档没有精确字节数，完整性下限按实测要求取 20MB。</summary>
    private const long SmallMinBytes = 20L * 1024 * 1024;

    /// <summary>三档模型（顺序 = 界面下拉的顺序：小 → 中 → 大）。</summary>
    public static readonly NsfwModelVariant[] Variants =
    {
        new("s", "OwenElliott/image-safety-classifier-s", "image-safety-classifier-s.onnx",
            0L, SmallMinBytes, "22MB", "小（22MB，最快）",
            "自报准确率 97.99% · 22MB，三档里最快"),
        new("m", "OwenElliott/image-safety-classifier-m", "image-safety-classifier-m.onnx",
            46_308_944L, 40L * 1024 * 1024, "44MB", "中（44MB，推荐）",
            "自报准确率 98.06% · 实测每帧 13–20ms"),
        new("l", "OwenElliott/image-safety-classifier-l", "image-safety-classifier-l.onnx",
            111_121_598L, 100L * 1024 * 1024, "106MB", "大（106MB，最准）",
            "自报准确率 98.20% · 实测每帧 23–38ms"),
    };

    /// <summary>默认档位（中档：精度和速度都合适）。</summary>
    public const string DefaultTierId = "m";

    /// <summary>模型存放目录：跟随 <see cref="AppSettings.DataDirectory"/>（HEXA_SETTINGS_PATH 也会跟着走，
    /// 所以自检/渲染探针不会往真实用户目录里写东西）。</summary>
    public static string ModelFolder => Path.Combine(AppSettings.DataDirectory, "models");

    /// <summary>把档位 id 归一到三档之一（认不出来就用默认的中档）。</summary>
    public static NsfwModelVariant Resolve(string? tierId)
    {
        string id = (tierId ?? "").Trim().ToLowerInvariant();
        foreach (NsfwModelVariant variant in Variants)
            if (variant.Id == id) return variant;
        foreach (NsfwModelVariant variant in Variants)
            if (variant.Id == DefaultTierId) return variant;
        return Variants[0];
    }

    /// <summary>当前档位（默认中档；由 <see cref="SetTier"/> 改）。</summary>
    public static NsfwModelVariant Variant { get; private set; } = Resolve(DefaultTierId);

    /// <summary>当前档位的模型文件完整路径。</summary>
    public static string ModelPath => Path.Combine(ModelFolder, Variant.FileName);

    /// <summary>下到一半的临时文件名。只有整份下完并校验过大小，才会改名成正式文件。</summary>
    private static string PartPath => ModelPath + ".part";

    /// <summary>某一档的模型文件路径（界面/删除要看全部档位）。</summary>
    public static string PathOf(NsfwModelVariant variant) => Path.Combine(ModelFolder, variant.FileName);

    // ── 状态（抓取线程 / 界面线程 / 模型线程都会读，一律走 Volatile）─────
    private static readonly Lock Gate = new();            // 会话的创建 / 释放（慢操作）
    private static readonly Lock InferenceGate = new();   // 推理互斥（同一时刻只允许一次）
    private static readonly float[] InputBuffer = new float[3 * InputSize * InputSize];
    private static readonly byte[] RowBuffer = new byte[InputSize * 4];

    private static InferenceSession? _session;
    private static DenseTensor<float>? _tensor;
    private static System.Drawing.Bitmap? _scratch;       // 只有"图比 224 还小"的兜底路径才用
    private static int _scratchWidth;
    private static int _scratchHeight;
    private static string _inputName = "image";
    private static string _outputName = "logits";

    private static string _loadError = "";        // 加载/推理失败原因（空 = 没失败过）
    private static bool _loadFailed;              // 试过且失败了：不再每帧重试
    private static string _statusOverride = "";   // 下载中 x% / 下载失败：原因 / 删除失败：原因
    private static bool _lastOperationFailed;     // 给界面决定用哪个颜色（警告 vs 普通）
    private static long _installedBytes = -1;     // 缓存的当前档位文件大小（−1 = 还没读盘）
    private static long _lastInferenceMs;
    private static bool _lastViolent;             // 最近一次推理：血腥暴力压过了情色

    // ── 对外状态 ──────────────────────────────────────────────────────

    /// <summary>当前档位的模型文件是否已下载且完整（够大）。半截文件一律当作没下载。</summary>
    public static bool IsInstalled => InstalledBytes >= Variant.MinCompleteBytes;

    /// <summary>当前档位模型文件的字节数（0 = 没下载）。读盘结果会缓存，界面每 150ms 问一次也不会打盘。</summary>
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

    /// <summary>本机一共占了多少磁盘（可能同时存在好几档）。删除按钮要如实告诉用户能腾出多少。</summary>
    public static long TotalInstalledBytes
    {
        get
        {
            long total = 0;
            foreach (NsfwModelVariant variant in Variants) total += FileBytes(PathOf(variant));
            return total;
        }
    }

    /// <summary>ONNX 会话是否已经加载（加载过一次就一直留在内存里，直到换档位或删除）。</summary>
    public static bool IsReady => Volatile.Read(ref _session) is not null;

    /// <summary>一次推理的耗时（毫秒，中档实测 13–20ms）。没跑过时为 0。</summary>
    public static long LastInferenceMs => Interlocked.Read(ref _lastInferenceMs);

    /// <summary>最近一次推理里「血腥暴力」压过了「情色」——界面据此说明为什么这一帧不算数。</summary>
    public static bool LastWasViolent => Volatile.Read(ref _lastViolent);

    /// <summary>下载 / 加载 / 删除有没有失败过（界面据此把状态文字标成警告色）。</summary>
    public static bool HasError => _loadError.Length > 0 || Volatile.Read(ref _lastOperationFailed);

    /// <summary>
    /// 白话状态字符串（直接给用户看）：
    /// 「没下载（中档 44MB）」/「下载中 42%」/「已下载（中档 44MB），开启观察后自动加载」/
    /// 「模型已加载」/「加载失败：…」/「下载失败：…」。
    /// </summary>
    public static string Status
    {
        get
        {
            string loadError = Volatile.Read(ref _loadError);
            if (loadError.Length > 0) return $"加载失败：{loadError}";
            string over = Volatile.Read(ref _statusOverride);
            if (over.Length > 0) return over;
            if (IsReady) return $"模型已加载（{VariantLabel()}）";
            if (IsInstalled) return $"已下载（{VariantLabel()}），开启观察后自动加载";
            return $"没下载（{VariantLabel()}，点下面的按钮下这一档）";
        }
    }

    private static string VariantLabel() => $"{TierName(Variant.Id)} {Variant.SizeText}";

    /// <summary>档位的中文名（"小" / "中" / "大"）。</summary>
    public static string TierName(string? tierId) => Resolve(tierId).Id switch
    {
        "s" => "小档",
        "l" => "大档",
        _ => "中档",
    };

    // ── 档位切换 ──────────────────────────────────────────────────────

    /// <summary>
    /// 换档位。<b>不同档位是不同文件</b>（文件名带后缀），所以换档只是"换一个要找的文件"，
    /// 会把已经加载的那一档会话释放掉（下一位要用的是另一个模型），并清掉加载失败的状态。
    /// 界面上原来的档位文件还在磁盘上（要腾空间用 <see cref="Delete"/>）。
    /// </summary>
    public static void SetTier(string? tierId)
    {
        NsfwModelVariant next = Resolve(tierId);
        if (next.Id == Variant.Id && IsReady) return;   // 已经是这一档而且已经加载好了

        lock (InferenceGate)
        {
            lock (Gate)
            {
                if (next.Id != Variant.Id) DisposeSessionLocked();
                Variant = next;
                InvalidateInstalledCache();
                _loadFailed = false;
                Volatile.Write(ref _loadError, "");
                Volatile.Write(ref _lastOperationFailed, false);
                Volatile.Write(ref _statusOverride, "");
                Volatile.Write(ref _lastViolent, false);
                Interlocked.Exchange(ref _lastInferenceMs, 0);
            }
        }
    }

    // ── 下载（必须先征得用户同意：本方法只被界面上的「下载模型」按钮调用）──

    /// <summary>
    /// 下载<b>当前档位</b>的模型：先试国内镜像，失败再试官方源。
    /// 全程写临时文件 <c>*.part</c>，**校验通过后才改名成正式文件**，所以中途断网/取消/磁盘满
    /// 都不会留下一个"看起来装了其实坏的"模型；每次失败都会把半截文件删掉。
    /// 这一档已经装好（够大）就直接返回 true，不重下。
    /// </summary>
    /// <returns>true = 现在这一档的模型是完整可用的。</returns>
    public static async Task<bool> DownloadAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        InvalidateInstalledCache();
        // 把路径钉在这一档上：万一用户下载中途在界面上换了档，也不能把这份数据写到另一个文件名去。
        NsfwModelVariant variant = Variant;
        string modelPath = PathOf(variant);
        string partPath = modelPath + ".part";

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
            string reason = Describe(ex);
            SetFailure($"下载失败：{reason}");
            AppLogger.Warn($"识别模型：准备下载目录失败 —— {ex.Message}");
            return false;
        }

        string lastError = "";
        string label = $"{TierName(variant.Id)} {variant.SizeText}";
        for (int i = 0; i < 2; i++)
        {
            string url = i == 0 ? variant.Mirror : variant.OfficialMirror;
            try
            {
                ct.ThrowIfCancellationRequested();
                Volatile.Write(ref _statusOverride, $"下载中 0%（{label}·线路 {i + 1}/2）");
                progress?.Report(0);

                long written = await DownloadOneAsync(variant, url, partPath, progress, ct).ConfigureAwait(false);

                // 校验：字节数必须够（各档的下限不同）。不够就不改名，当作这次线路失败。
                long size = FileBytes(partPath);
                if (size <= 0) size = written;
                if (size < variant.MinCompleteBytes)
                    throw new InvalidDataException($"下到的文件只有 {MegaBytes(size)}，不完整");

                DeleteQuietly(modelPath);
                File.Move(partPath, modelPath, overwrite: true);   // 原子改名：这一步之后才算"装好了"
                InvalidateInstalledCache();
                _loadFailed = false;         // 换了一份新文件：上次的加载失败不再拦着重试
                Volatile.Write(ref _loadError, "");
                Volatile.Write(ref _lastOperationFailed, false);
                ClearStatusOverride();
                progress?.Report(1);
                AppLogger.Info($"识别模型：下载完成 {modelPath}（{MegaBytes(size)}，线路 {i + 1}）");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                DeleteQuietly(partPath);
                ClearStatusOverride();
                progress?.Report(0);
                AppLogger.Info("识别模型：下载已取消（半截文件已清理）");
                return false;
            }
            catch (Exception ex)
            {
                // 注意：连接超时也是 TaskCanceledException，但它不该被当成"用户取消" ——
                // 走这个分支才会去试下一个线路（用户真取消的那条在上面拦住了）。
                DeleteQuietly(partPath);        // 失败必须清理，绝不留下半截文件
                lastError = Describe(ex);
                AppLogger.Warn($"识别模型：线路 {i + 1} 下载失败 —— {ex.Message}");
            }
        }

        SetFailure($"下载失败：{lastError}");
        progress?.Report(0);
        return false;
    }

    /// <summary>下一条线路。返回写到临时文件的字节数；任何失败都往外抛，由调用方统一收尾。</summary>
    private static async Task<long> DownloadOneAsync(
        NsfwModelVariant variant, string url, string partPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Hexa/1.0 (local model download)");

        using HttpResponseMessage response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // 总量优先用服务器给的 Content-Length；它缺失或明显不合理时按已知体积估算进度。
        long total = response.Content.Headers.ContentLength ?? 0;
        if (total < variant.MinCompleteBytes)
            total = variant.ExpectedBytes > 0 ? variant.ExpectedBytes : variant.MinCompleteBytes;

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

                // 只报到 99%：最后那 1% 留给「落盘 + 校验 + 改名」，用户看到 100% 就等于真的好了。
                double ratio = Math.Clamp(written / (double)total, 0, 1) * 0.99;
                Volatile.Write(ref _statusOverride, $"下载中 {ratio * 100:0}%");
                progress?.Report(ratio);
            }
            await file.FlushAsync(ct).ConfigureAwait(false);
        }
        return written;
    }

    /// <summary>
    /// 删掉本机下载过的<b>所有档位</b>的模型文件（腾空间）。会话会被一起释放，之后判定退回手工特征。
    /// </summary>
    /// <returns>true = 文件都已经不在了。</returns>
    public static bool Delete()
    {
        // 先拿推理锁再拿会话锁：和 Classify 的加锁顺序一致（推理锁 → 会话锁），不会互相等死。
        lock (InferenceGate)
        {
            lock (Gate)
            {
                DeleteQuietly(PartPath);
                foreach (NsfwModelVariant variant in Variants) DeleteQuietly(PathOf(variant) + ".part");

                DisposeSessionLocked();
                foreach (NsfwModelVariant variant in Variants)
                {
                    string path = PathOf(variant);
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        SetFailure($"删除失败：{Describe(ex)}");
                        InvalidateInstalledCache();
                        AppLogger.Warn($"识别模型：删除失败 {path} —— {ex.Message}");
                        return false;
                    }
                }

                InvalidateInstalledCache();
                Volatile.Write(ref _loadError, "");
                _loadFailed = false;
                Volatile.Write(ref _lastOperationFailed, false);
                ClearStatusOverride();
                Volatile.Write(ref _lastViolent, false);
                Interlocked.Exchange(ref _lastInferenceMs, 0);
                AppLogger.Info("识别模型：已删除本机所有档位的模型文件");
                return true;
            }
        }
    }

    // ── 推理 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 算一帧的 P(nsfw)（0–1）。<b>失败一律返回 −1</b>（没装模型、模型坏了、推理异常），
    /// 调用方看到负数就退回手工特征，绝不抛异常给抓帧/界面线程。
    ///
    /// 传进来的图随便多大：本类自己中心裁 224×224（调用方已经按短边 236 缩好的话就只做裁剪）。
    /// <b>血腥/暴力（NSFL）压过情色（NSFW）时按「不是情色」处理</b>：把情色概率按 NSFL 的占比打折
    /// —— 画面里全是血浆不该让设备动起来。
    /// </summary>
    public static double Classify(System.Drawing.Bitmap? frame)
    {
        if (frame is null) return -1;

        lock (InferenceGate)     // 同一时刻只允许一次推理：会话和张量都是复用的
        {
            try
            {
                InferenceSession? session = Volatile.Read(ref _session);
                if (session is null)
                {
                    session = LoadSessionLocked();
                    if (session is null) return -1;    // 原因已经写在 Status 里
                }

                DenseTensor<float> tensor = EnsureTensor();
                var watch = Stopwatch.StartNew();
                FillInput(frame);                       // 中心裁 → 归一化 → NCHW，全写进复用的 InputBuffer
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                    session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) }, new[] { _outputName });
                watch.Stop();

                float[] logits = ReadLogits(results);
                if (logits.Length < OutputCount) return -1;

                double[] probability = Softmax(logits);
                if (!double.IsFinite(probability[NsfwIndex])) return -1;

                bool violent = probability[NsflIndex] > probability[NsfwIndex];
                double score = probability[NsfwIndex];
                if (violent)
                {
                    // 血腥压过情色 → 按"不是情色"处理：情色概率再乘一个 NSFL 的占比（最多打到 1/4 不到）。
                    double denom = probability[NsflIndex] + probability[NsfwIndex];
                    score = denom > 0 ? score * (probability[NsfwIndex] / denom) : 0;
                }

                Volatile.Write(ref _lastViolent, violent);
                Interlocked.Exchange(ref _lastInferenceMs, watch.ElapsedMilliseconds);
                return Math.Clamp(score, 0, 1);
            }
            catch (Exception ex)
            {
                // 推理出错不能让抓帧线程死掉，也不能每帧刷屏：记一次状态就够，界面会显示原因。
                if (Volatile.Read(ref _loadError).Length == 0)
                {
                    Volatile.Write(ref _loadError, DescribeInference(ex));
                    AppLogger.Warn($"识别模型：推理失败 —— {ex.Message}");
                }
                return -1;
            }
        }
    }

    private static float[] ReadLogits(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (DisposableNamedOnnxValue result in results)
            return result.AsTensor<float>().ToArray();   // 我们只要了 logits 这一个输出
        return Array.Empty<float>();
    }

    /// <summary>对 logits 做 softmax，返回每个标签的概率（下标顺序 = [NSFL, NSFW, SFW]）。</summary>
    private static double[] Softmax(float[] logits)
    {
        double max = double.NegativeInfinity;
        for (int i = 0; i < OutputCount; i++) max = Math.Max(max, logits[i]);
        var probability = new double[OutputCount];
        double sum = 0;
        for (int i = 0; i < OutputCount; i++)
        {
            probability[i] = Math.Exp(logits[i] - max);
            sum += probability[i];
        }
        if (sum <= 0) return probability;
        for (int i = 0; i < OutputCount; i++) probability[i] /= sum;
        return probability;
    }

    /// <summary>懒加载 ONNX 会话（中档实测 204ms、大档 442ms）。失败后不再每帧重试，直到换档/重下/删除。</summary>
    private static InferenceSession? LoadSessionLocked()
    {
        InferenceSession? existing = Volatile.Read(ref _session);
        if (existing is not null) return existing;
        if (_loadFailed) return null;          // 已经失败过：别每 300ms 再抛一次
        if (!IsInstalled) return null;         // 没装模型：什么都不做

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
                    // 线程数故意不占满：一次推理才十几毫秒，留出核来跑抓帧（13fps）和界面。
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8),
                    InterOpNumThreads = 1,
                };
                var watch = Stopwatch.StartNew();
                var session = new InferenceSession(ModelPath, options);
                watch.Stop();

                // 输入/输出名字以模型元数据为准（实测是 image / logits），拿不到就用实测值兜底。
                _inputName = FirstKey(session.InputMetadata.Keys) ?? "image";
                _outputName = session.OutputMetadata.ContainsKey("logits")
                    ? "logits"
                    : FirstKey(session.OutputMetadata.Keys) ?? "logits";

                Volatile.Write(ref _session, session);
                Volatile.Write(ref _lastOperationFailed, false);
                AppLogger.Info($"识别模型：已加载 {Variant.Id} 档（{watch.ElapsedMilliseconds}ms，"
                    + $"输入 {_inputName}，输出 {_outputName}）");
                return session;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                Volatile.Write(ref _loadError, DescribeLoad(ex));
                Volatile.Write(ref _lastOperationFailed, true);
                AppLogger.Warn($"识别模型：加载失败 —— {ex.Message}");
                return null;
            }
        }
    }

    private static void DisposeSessionLocked()
    {
        InferenceSession? session = Volatile.Read(ref _session);
        Volatile.Write(ref _session, null);
        try { session?.Dispose(); }
        catch (Exception ex) { AppLogger.Warn($"识别模型：释放会话时出错 —— {ex.Message}"); }
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
    /// 把一帧裁成模型要的 224×224 并填进 NCHW 张量（RGB 顺序 = [R 平面, G 平面, B 平面]）。
    ///
    /// 两条路径：
    /// · 正常路径（画面观察交来的图短边已经是 236）：只做<b>中心裁 224</b>，一次 LockBits 搞定；
    /// · 兜底路径（图比 224 还小，比如别处直接塞了张小图）：先按短边缩到 236 再裁。
    /// 两种都写进同一块复用的 <see cref="InputBuffer"/>，中间不产生第二份大图。
    /// </summary>
    private static void FillInput(System.Drawing.Bitmap frame)
    {
        System.Drawing.Bitmap work = frame;
        if (frame.Width < InputSize || frame.Height < InputSize)
        {
            double scale = ShortSide / (double)Math.Min(frame.Width, frame.Height);
            int width = Math.Max(InputSize, (int)Math.Round(frame.Width * scale));
            int height = Math.Max(InputSize, (int)Math.Round(frame.Height * scale));
            work = EnsureScratch(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(work);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImage(frame,
                new System.Drawing.Rectangle(0, 0, width, height),
                0, 0, frame.Width, frame.Height, System.Drawing.GraphicsUnit.Pixel);
        }

        // 中心裁 224×224（等价于 Resize(短边 236) + CenterCrop(224)）
        int size = Math.Min(Math.Min(work.Width, work.Height), InputSize);
        int offsetX = (work.Width - size) / 2;
        int offsetY = (work.Height - size) / 2;

        const int plane = InputSize * InputSize;
        System.Drawing.Imaging.BitmapData data = work.LockBits(
            new System.Drawing.Rectangle(offsetX, offsetY, size, size),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), RowBuffer, 0, size * 4);
                int rowBase = y * InputSize;
                for (int x = 0; x < size; x++)
                {
                    int p = x * 4;                       // Format32bppArgb 的内存顺序是 B,G,R,A
                    byte b = RowBuffer[p], g = RowBuffer[p + 1], r = RowBuffer[p + 2];
                    int i = rowBase + x;
                    InputBuffer[i] = (r * (1f / 255f) - MeanR) * InvStdR;
                    InputBuffer[plane + i] = (g * (1f / 255f) - MeanG) * InvStdG;
                    InputBuffer[2 * plane + i] = (b * (1f / 255f) - MeanB) * InvStdB;
                }
            }
        }
        finally
        {
            work.UnlockBits(data);   // GDI+ 的锁必须还回去，否则这块位图以后再用就抛异常
        }
    }

    private static System.Drawing.Bitmap EnsureScratch(int width, int height)
    {
        if (_scratch is null || _scratchWidth != width || _scratchHeight != height)
        {
            _scratch?.Dispose();
            _scratch = new System.Drawing.Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            _scratchWidth = width;
            _scratchHeight = height;
        }
        return _scratch;
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
        catch (Exception ex) { AppLogger.Warn($"识别模型：删除文件失败 {path} —— {ex.Message}"); }
    }

    private static string MegaBytes(long bytes) => $"{bytes / 1048576.0:0.#}MB";

    /// <summary>下载相关的异常 → 白话原因（用户看到的就是这句）。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http when http.StatusCode is not null
            => $"镜像返回 {(int)http.StatusCode.Value}（链接可能变了，或镜像暂时不可用）",
        HttpRequestException => "连不上镜像（网络不通，或者镜像暂时不可用）",
        TaskCanceledException => "下载超时（网络太慢或镜像没响应）",
        OperationCanceledException => "已取消",
        UnauthorizedAccessException => $"没有写入权限（{ModelFolder}）",
        IOException io when IsDiskFull(io) => $"磁盘空间不足（{ModelFolder} 至少留 200MB）",
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
        OnnxRuntimeException => "模型文件读不了，可能没下完整（点「删除模型」再下一次）",
        _ => $"{ex.GetType().Name}：{Short(ex.Message)}",
    };

    private static string DescribeInference(Exception ex) => ex switch
    {
        OnnxRuntimeException => "推理出错，可能模型文件坏了（点「删除模型」再下一次）",
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
