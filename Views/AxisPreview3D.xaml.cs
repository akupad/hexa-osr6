using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Hexa.Services;
using Hexa.Services.Sr6;
using HelixToolkit.Wpf;

namespace Hexa.Views;

/// <summary>
/// 实时六轴 3D 预览。
///
/// 模型是 osr-emu（MIT）那台带完整逆运动学的真 SR6：底座 + 盖板 + 主臂 + 连杆 +
/// 球头轴承 + 左右俯仰臂 + 俯仰连杆 + 接收器 + 旋转机头，由 <see cref="Sr6Rig"/> 装配，
/// 六轴各自驱动真实机构（伺服角算法照搬 SR6 固件），因此“看到的姿态”就是设备真实的姿态。
///
/// 视口是 <see cref="HelixViewport3D"/>，相机与鼠标交互全部交给它内置的 CameraController；
/// 机位在首次布局完成时按模型包围盒自动取景（模型是 Z 轴向上，所以 UpDirection=(0,0,1)）。
/// 每帧只更新各部件的 MatrixTransform3D.Matrix（结构体赋值，无分配），几何跨控件共享且已冻结。
///
/// 对外契约（改签名会连带改调用方）：
///   UpdateAxes / UpdateAmp / RefreshPreview / HighlightAxis / HighlightedAxis / ResetView
///   AxisValues / AmpValues / AxisOrder / AutoRefreshEnabled，以及名为 _timer 的 120ms
///   DispatcherTimer 字段（宿主要自己按帧喂数据时，用 AutoRefreshEnabled=false 停掉它）。
///   目前外部调用：StrokesPage（悬停试看动作）、EditorPage（编排预览）、StrokesViewModel 刷新。
/// </summary>
public partial class AxisPreview3D : System.Windows.Controls.UserControl
{
    private static readonly double[] AxisHome = { 50, 50, 50, 50, 50, 50 };
    /// <summary>轴顺序（唯一真源：<see cref="Hexa.Services.Osr6DeviceProfile.InstalledAxes"/>）。</summary>
    private static readonly string[] AxisNames = Hexa.Services.Osr6DeviceProfile.InstalledAxes;

    // ── 场景根节点（挂到 HelixViewport3D.Children 下）──────────────
    private readonly ModelVisual3D _sceneRoot = new();

    // ── SR6 模型 + 振幅指示环 ────────────────────────────────────
    private readonly Sr6Rig _rig;
    private readonly ScaleTransform3D _ampRingScale = new(1, 1, 1);

    // ── 视角：左键拖动 = 以你的视角绕设备自由旋转（360°，没有上下限位）──
    // 用四元数累积旋转，不夹取角度、也不固定“上方向”，所以可以一路转到正上方、
    // 正下方甚至翻过来看——设备本身不动，动的是相机（含相机的上方向）。
    private const double OrbitYawPerPixel = 0.42;
    private const double OrbitPitchPerPixel = 0.32;
    private System.Numerics.Quaternion _orbitRotation = System.Numerics.Quaternion.Identity;
    private Point3D _orbitTarget;          // 旋转中心 = 设备包围盒中心
    private Vector3D _orbitBaseOffset;     // 默认机位下“相机 − 中心”的向量
    private Vector3D _orbitBaseUp = new(0, 0, 1);
    private Point _lastDragPoint;
    private bool _dragging;
    private bool _autoRefresh = true;


    // ── 实时值（0~100）───────────────────────────────────────────
    private readonly double[] _axis = { 50, 50, 50, 50, 50, 50 };
    private readonly double[] _amp  = { 0, 0, 0, 0, 0, 0 };

    // 内置自动刷新计时器（读取引擎实时输出）。宿主可用 AutoRefreshEnabled=false 停掉它，
    // 自己按帧调 UpdateAxes，避免两路数据互相覆盖造成闪动。
    private readonly DispatcherTimer _timer;
    private bool _timerRunning;
    private bool _statusShown;
    private bool _framed;             // 机位是否已经按包围盒取过景
    private System.Windows.Size _framedViewSize;     // 上次取景时的视口尺寸
    private bool _userAdjustedCamera; // 用户手动转过/缩放过视角后，不再自动重新取景
    private long _lastRefreshTicks;   // 外部驱动与内置定时器去重（见 TimerTick）

    // ── 当前轴高亮（只换材质引用，几何与变换不动）──────────────────
    private static readonly Material HighlightMaterial = MakeHighlightMaterial();
    private readonly Dictionary<GeometryModel3D, Material> _baseMaterials = new();
    private readonly Dictionary<GeometryModel3D, Material> _baseBackMaterials = new();
    private readonly List<GeometryModel3D> _highlighted = new();
    private string _highlightAxis = string.Empty;

    /// <summary>构造六轴 3D 预览控件。</summary>
    public AxisPreview3D()
    {
        InitializeComponent();

        long started = Stopwatch.GetTimestamp();
        _rig = new Sr6Rig();                      // 原生坐标：Z 轴向上
        foreach ((GeometryModel3D mesh, Material material) in _rig.BaseMaterials)
        {
            _baseMaterials[mesh] = material;
            _baseBackMaterials[mesh] = mesh.BackMaterial ?? material;
        }
        AppLogger.Info($"3D 预览模型就绪：{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");

        var scene = new Model3DGroup();
        scene.Children.Add(_rig.Root);
        scene.Children.Add(BuildAmplitudeRing());
        _sceneRoot.Content = scene;

        // 模型挂到 HelixViewport3D 的 Children（其第 0 项是 HelixToolkit 自带的灯光视觉节点）
        View3D.Children.Add(_sceneRoot);
        BuildLights();

        UpdateAxes(AxisHome);
        UpdateAmp(_amp);

        // 左键拖动 = 转动设备（相机旋转已关闭；Shift+左键留给平移）
        View3D.PreviewMouseLeftButtonDown += OnViewMouseDown;
        View3D.PreviewMouseMove += OnViewMouseMove;
        View3D.PreviewMouseLeftButtonUp += OnViewMouseUp;
        // 鼠标捕获被系统抢走（窗口失焦等）时复位，避免 _dragging 卡住导致设备一直跟着鼠标转
        View3D.LostMouseCapture += (_, _) => _dragging = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => TimerTick();
        // 注意：这里必须尊重 AutoRefreshEnabled。TabControl 切换标签会卸载/重载内容，
        // 宿主（编排页）把自动刷新关掉后，重新 Loaded 不能又把它打开、把引擎值盖到预览上。
        Loaded   += (_, _) =>
        {
            if (_autoRefresh) StartPreview();
            FrameCameraIfNeeded();
        };
        Unloaded += (_, _) =>
        {
            StopPreview();
            // 卸载时清掉拖动状态，避免回来之后鼠标不动也跟着转
            _dragging = false;
            View3D.ReleaseMouseCapture();
        };
        SizeChanged += (_, _) => FrameCameraIfNeeded();
    }

    // ══════════════════════════════════════════════════════════════
    //  公开 API（对外契约，勿改签名）
    // ══════════════════════════════════════════════════════════════

    /// <summary>刷新六轴姿态，values 为 6 个 0~100 的轴值（越界自动夹取）。</summary>
    public void UpdateAxes(double[] values)
    {
        if (values != null)
        {
            for (int i = 0; i < 6 && i < values.Length; i++)
                _axis[i] = double.IsFinite(values[i]) ? Math.Clamp(values[i], 0, 100) : 50;
        }

        // 轴值 0~100 → 模型 0~1；scale 恒为 1（振幅已经体现在实时输出里，再乘一次会偏移中位）
        var axes01 = new double[6];
        var scale01 = new double[6];
        for (int i = 0; i < 6; i++)
        {
            axes01[i] = _axis[i] / 100.0;
            scale01[i] = 1.0;
        }
        _rig.Update(axes01, scale01);

        RefreshReadout();
    }

    /// <summary>
    /// 刷新六轴振幅（0~100），驱动地面上振幅指示环的缩放。
    /// 只使用最大振幅，避免与 UpdateAxes 的模型姿态互相覆盖。
    /// </summary>
    public void UpdateAmp(double[] amps)
    {
        if (amps != null)
        {
            for (int i = 0; i < 6 && i < amps.Length; i++)
                _amp[i] = double.IsFinite(amps[i]) ? Math.Clamp(amps[i], 0, 100) : 0;
        }

        double maxAmp = 0;
        for (int i = 0; i < 6; i++)
            if (_amp[i] > maxAmp) maxAmp = _amp[i];

        double scale = 0.72 + maxAmp / 100.0 * 0.55;
        _ampRingScale.ScaleX = _ampRingScale.ScaleY = _ampRingScale.ScaleZ = scale;
    }

    /// <summary>
    /// 高亮指定轴对应的机构：axisId 取 "L0".."R2"，传空串或 null 清除高亮。
    /// 只替换材质引用（几何、变换都不动），因此可在 120ms 实时刷新期间安全调用；
    /// 部件映射见 <see cref="Sr6Rig.MeshesForAxis"/>：
    /// L0 接收器 / L1 四根主臂 / L2 四根连杆 / R0 机头外壳 / R1 左右俯仰臂 / R2 俯仰连杆+前轴承。
    /// </summary>
    public void HighlightAxis(string? axisId)
    {
        string key = NormalizeAxisId(axisId);
        if (key == _highlightAxis) return;   // 重复设置同一轴：不必反复改写材质

        // 先还原上一次高亮的部件
        foreach (GeometryModel3D model in _highlighted)
        {
            if (_baseMaterials.TryGetValue(model, out Material? original)) model.Material = original;
            if (_baseBackMaterials.TryGetValue(model, out Material? back)) model.BackMaterial = back;
        }
        _highlighted.Clear();
        _highlightAxis = key;

        if (key.Length == 0) return;
        foreach (GeometryModel3D model in _rig.MeshesForAxis(key))
        {
            model.Material = HighlightMaterial;
            model.BackMaterial = HighlightMaterial;
            _highlighted.Add(model);
        }
    }

    /// <summary>当前高亮的轴（无高亮时为空串）。</summary>
    public string HighlightedAxis => _highlightAxis;

    /// <summary>
    /// 复位到默认机位（与中键双击同效）。按**当前**视口重新取景，
    /// 这样最大化/还原之后点「重置视角」都能正好把设备框满，而不是回到首次取景时的机位。
    /// </summary>
    public void ResetView()
    {
        _orbitRotation = System.Numerics.Quaternion.Identity;
        _userAdjustedCamera = false;
        FrameCamera(force: true);
    }

    // ══════════════════════════════════════════════════════════════
    //  机位 / 灯光 / 地面
    // ══════════════════════════════════════════════════════════════

    private void FrameCameraIfNeeded() => FrameCamera(force: false);

    /// <summary>
    /// 按模型包围盒取景。
    /// force=false 时：首次取景一定执行；之后只有「视口尺寸变化超过 25% 且用户没手动调过视角」才重新取景，
    /// 这样最大化/还原窗口时模型不会缩在角落或忽大忽小，而用户自己转过的视角也不会被重置。
    /// </summary>
    private void FrameCamera(bool force)
    {
        double width = View3D.ActualWidth;
        double height = View3D.ActualHeight;
        if (width < 40 || height < 40) return;

        if (_framed && !force)
        {
            if (_userAdjustedCamera) return;
            double dw = Math.Abs(width - _framedViewSize.Width) / Math.Max(1, _framedViewSize.Width);
            double dh = Math.Abs(height - _framedViewSize.Height) / Math.Max(1, _framedViewSize.Height);
            if (dw < 0.25 && dh < 0.25) return;
        }

        Rect3D bounds = _rig.Root.Bounds;
        if (bounds.IsEmpty) return;   // 还没建好：不置 _framed，等下一次尺寸变化再取景
        _framed = true;

        var center = new Point3D(
            bounds.X + bounds.SizeX / 2,
            bounds.Y + bounds.SizeY / 2,
            bounds.Z + bounds.SizeZ / 2);
        double radius = 0.5 * Math.Sqrt(
            bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ);

        // 从右前上方看（模型坐标：+X 右、+Y 后、+Z 上；套筒朝 -Y 即“前方”）。
        // 注意 LookDirection 是相机看出去的方向，相机位于 center - direction*distance，
        // 所以想让相机在“上方”，direction 的 Z 分量必须为负。
        var direction = new Vector3D(0.56, 0.56, -0.61);
        direction.Normalize();

        const double fieldOfViewX = 45;                 // WPF 的 FieldOfView 是水平视角
        double aspect = width / height;
        double fieldOfViewY = 2 * Math.Atan(Math.Tan(fieldOfViewX * Math.PI / 360) / aspect) * 180 / Math.PI;

        // 取景用「把包围盒 8 个角投到相机基向量上」算，而不是用外接球半径：
        // 外接球在竖直方向明显偏大/偏小，最大化之后机器会被裁掉一截或者缩成一小团。
        var rightAxis = Vector3D.CrossProduct(direction, new Vector3D(0, 0, 1));
        if (rightAxis.LengthSquared < 1e-9) rightAxis = new Vector3D(1, 0, 0);
        rightAxis.Normalize();
        Vector3D upAxis = Vector3D.CrossProduct(rightAxis, direction);
        upAxis.Normalize();

        double halfWidth = 0, halfHeight = 0, halfDepth = 0;
        for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
        {
            var corner = new Point3D(
                (cornerIndex & 1) == 0 ? bounds.X : bounds.X + bounds.SizeX,
                (cornerIndex & 2) == 0 ? bounds.Y : bounds.Y + bounds.SizeY,
                (cornerIndex & 4) == 0 ? bounds.Z : bounds.Z + bounds.SizeZ);
            Vector3D offset = corner - center;
            halfWidth = Math.Max(halfWidth, Math.Abs(Vector3D.DotProduct(offset, rightAxis)));
            halfHeight = Math.Max(halfHeight, Math.Abs(Vector3D.DotProduct(offset, upAxis)));
            halfDepth = Math.Max(halfDepth, Math.Abs(Vector3D.DotProduct(offset, direction)));
        }

        // 让包围盒正好落在视锥里：横竖各算一次所需距离，取大的，再加上沿视线的深度，
        // 最后留 6% 边距（太大则留白，太小会贴边被裁）。
        double tanX = Math.Tan(fieldOfViewX * Math.PI / 360);
        double tanY = Math.Tan(fieldOfViewY * Math.PI / 360);
        double distance = Math.Max(halfWidth / Math.Max(1e-6, tanX), halfHeight / Math.Max(1e-6, tanY)) * 1.06
                          + halfDepth;

        var camera = new PerspectiveCamera
        {
            Position = center - direction * distance,
            // 关键：LookDirection 的长度 = 相机到设备的距离。
            // HelixToolkit 的 CameraController.CameraTarget 就是 Position + LookDirection，
            // 若这里写单位向量，隐式目标会落在相机自己附近，绕它转机器会直接甩出画面；
            // 写成 direction * distance 后 Position + LookDirection 恒等于设备中心。
            LookDirection = direction * distance,
            UpDirection = new Vector3D(0, 0, 1),
            FieldOfView = fieldOfViewX,
            NearPlaneDistance = 20,
            FarPlaneDistance = Math.Max(4000, distance + radius * 6),
        };

        // 先设相机、再设 DefaultCamera：复位目标必须和“已修正过 LookDirection 的相机”一致，
        // 否则点「重置视角」/中键双击后旋转中心又跑偏（CameraController.ResetCamera 只做
        // CameraHelper.CopyTo(DefaultCamera, Camera)，不会重新推导目标）。
        View3D.Camera = camera;
        View3D.DefaultCamera = camera.Clone();

        // 兜底：显式把旋转中心钉在设备包围盒中心（正常应已是这个值）
        if (View3D.CameraController is { } controller) controller.CameraTarget = center;

        // 记录旋转基准：之后左键拖动就在这台相机的基础上绕 center 自由转
        _orbitTarget = center;
        _orbitBaseOffset = camera.Position - center;
        _orbitBaseUp = camera.UpDirection;
        _orbitRotation = System.Numerics.Quaternion.Identity;
        _framedViewSize = new System.Windows.Size(width, height);
        _userAdjustedCamera = false;
    }

    /// <summary>灯光挂在 HelixViewport3D 自带的 Lights（Model3DGroup）上，其视觉节点已由控件放在 Children 里。</summary>
    private void BuildLights()
    {
        // 主光大致从相机方向打过来（相机在 +X/-Y/+Z 一侧，故光线朝 -X/+Y/-Z 走），
        // 保证朝向观众的面有亮部；补光从另一侧压出体积感，背光勾轮廓。
        Model3DGroup lights = View3D.Lights;
        // 只用「环境光 + 主光 + 补光」三盏：灯越多、环境光越亮，明暗关系越平，
        // 整台机器就会像蒙了一层光膜。这里压低环境光、让主光主导，拉开明暗对比。
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x44, 0x4E, 0x5A)));
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(0xF0, 0xF6, 0xFF), new Vector3D(-0.55, 0.58, -0.60)));
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(0x3C, 0x48, 0x56), new Vector3D(0.75, 0.20, -0.35)));
    }

    /// <summary>
    /// 地面上的振幅指示环（半径随最大振幅缩放）：一个扁平的圆环网格，
    /// 贴在 z≈0.5mm 的地面网格上，给静止的机器一个“工作范围”参照。
    /// </summary>
    private Model3DGroup BuildAmplitudeRing()
    {
        const int segments = 72;
        const double inner = 104;
        const double outer = 114;
        const double z = 0.6;

        var positions = new Point3DCollection(segments * 2);
        var normals = new Vector3DCollection(segments * 2);
        var indices = new Int32Collection(segments * 6);
        for (int i = 0; i < segments; i++)
        {
            double angle = i * 2 * Math.PI / segments;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            positions.Add(new Point3D(inner * cos, inner * sin, z));
            positions.Add(new Point3D(outer * cos, outer * sin, z));
            normals.Add(new Vector3D(0, 0, 1));
            normals.Add(new Vector3D(0, 0, 1));
        }
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            int a = i * 2, b = i * 2 + 1, c = next * 2, d = next * 2 + 1;
            indices.Add(a); indices.Add(b); indices.Add(d);
            indices.Add(a); indices.Add(d); indices.Add(c);
        }

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = indices,
        };
        mesh.Freeze();

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0x66, 0x17, 0xB8, 0x90))));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(0x28, 0x0F, 0x7A, 0x5F))));
        material.Freeze();

        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Transform = new Transform3DGroup { Children = { _ampRingScale } };
        return new Model3DGroup { Children = { model } };
    }

    // ══════════════════════════════════════════════════════════════
    //  相机公转（左键拖动 = 绕着设备走一圈）
    // ══════════════════════════════════════════════════════════════

    private void OnViewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Shift+左键留给 HelixViewport3D 的平移手势
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;
        _lastDragPoint = e.GetPosition(View3D);
        if (!View3D.CaptureMouse())
        {
            _dragging = false;
            return;
        }
        _dragging = true;
    }

    private void OnViewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _dragging = false;
            View3D.ReleaseMouseCapture();
            return;
        }

        Point current = e.GetPosition(View3D);
        double dx = current.X - _lastDragPoint.X;
        double dy = current.Y - _lastDragPoint.Y;
        _lastDragPoint = current;
        if (dx == 0 && dy == 0) return;

        // 以“我的视角”转：往右拖 = 视线往右转，往下拖 = 视线往下压（相机抬高）。
        // 旋转按当前屏幕轴累积，所以能一直转下去，不受“地面/上方向”限制。
        if (View3D.Camera is not PerspectiveCamera camera) return;
        Vector3D look = camera.LookDirection;
        if (look.LengthSquared < 1e-9) return;
        look.Normalize();
        Vector3D up = camera.UpDirection;
        if (up.LengthSquared < 1e-9) up = new Vector3D(0, 0, 1);
        up.Normalize();

        Vector3D screenRight = Vector3D.CrossProduct(look, up);
        if (screenRight.LengthSquared < 1e-9) screenRight = new Vector3D(1, 0, 0);
        screenRight.Normalize();
        Vector3D screenUp = Vector3D.CrossProduct(screenRight, look);
        screenUp.Normalize();

        var qYaw = System.Numerics.Quaternion.CreateFromAxisAngle(
            new System.Numerics.Vector3((float)screenUp.X, (float)screenUp.Y, (float)screenUp.Z),
            (float)(-dx * OrbitYawPerPixel * Math.PI / 180));
        var qPitch = System.Numerics.Quaternion.CreateFromAxisAngle(
            new System.Numerics.Vector3((float)screenRight.X, (float)screenRight.Y, (float)screenRight.Z),
            (float)(-dy * OrbitPitchPerPixel * Math.PI / 180));
        _userAdjustedCamera = true;   // 用户自己转过视角，之后窗口尺寸变化不再自动重新取景
        System.Numerics.Quaternion delta = System.Numerics.Quaternion.Normalize(
            System.Numerics.Quaternion.Multiply(qYaw, qPitch));
        _orbitRotation = System.Numerics.Quaternion.Normalize(
            System.Numerics.Quaternion.Multiply(delta, _orbitRotation));

        // 用「增量」旋转当前机位，而不是把累计旋转套回固定基准机位：
        // 这样滚轮缩放、右键平移之后的距离/位置不会被下一次拖动弹回默认值。
        ApplyOrbitDelta(delta);
        e.Handled = true;
    }

    private void OnViewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        View3D.ReleaseMouseCapture();
    }

    /// <summary>
    /// 直接设置视角旋转（度）。yaw = 绕屏幕竖直轴，pitch = 绕屏幕水平轴，
    /// 不做任何夹取，用于自检 / 将来的视角预设。
    /// </summary>
    internal void SetOrbit(double yawDegrees, double pitchDegrees)
    {
        Vector3D look = -_orbitBaseOffset;
        if (look.LengthSquared < 1e-9) look = new Vector3D(-1, -1, -1);
        look.Normalize();
        Vector3D right = Vector3D.CrossProduct(look, _orbitBaseUp);
        if (right.LengthSquared < 1e-9) right = new Vector3D(1, 0, 0);
        right.Normalize();
        Vector3D up = Vector3D.CrossProduct(right, look);
        up.Normalize();

        var qYaw = System.Numerics.Quaternion.CreateFromAxisAngle(
            new System.Numerics.Vector3((float)up.X, (float)up.Y, (float)up.Z),
            (float)(yawDegrees * Math.PI / 180));
        var qPitch = System.Numerics.Quaternion.CreateFromAxisAngle(
            new System.Numerics.Vector3((float)right.X, (float)right.Y, (float)right.Z),
            (float)(-pitchDegrees * Math.PI / 180));
        _orbitRotation = System.Numerics.Quaternion.Normalize(
            System.Numerics.Quaternion.Multiply(qYaw, qPitch));
        _userAdjustedCamera = true;
        ApplyOrbit();
    }

    /// <summary>用 System.Numerics 的四元数旋转 WPF 的 Vector3D（WPF 自己没有这个重载）。</summary>
    private static Vector3D RotateByQuaternion(Vector3D vector, System.Numerics.Quaternion rotation)
    {
        var source = new System.Numerics.Vector3((float)vector.X, (float)vector.Y, (float)vector.Z);
        System.Numerics.Vector3 result = System.Numerics.Vector3.Transform(source, rotation);
        return new Vector3D(result.X, result.Y, result.Z);
    }

    /// <summary>
    /// 按「增量」旋转当前相机：位置偏移与上方向一起转，所以能 360° 自由翻转，
    /// 同时保留用户当前的缩放距离与平移量（HelixToolkit 的滚轮缩放/右键平移直接改相机）。
    /// </summary>
    private void ApplyOrbitDelta(System.Numerics.Quaternion delta)
    {
        if (View3D.Camera is not PerspectiveCamera camera) return;
        Vector3D current = camera.Position - _orbitTarget;
        if (current.LengthSquared < 1e-9) return;

        Vector3D offset = RotateByQuaternion(current, delta);
        Vector3D up = RotateByQuaternion(camera.UpDirection, delta);
        if (up.LengthSquared < 1e-9) up = new Vector3D(0, 0, 1);

        camera.Position = _orbitTarget + offset;
        camera.LookDirection = -offset;
        camera.UpDirection = up;
    }

    /// <summary>把累积旋转套用到相机（绝对机位：复位 / 视角预设用）。</summary>
    private void ApplyOrbit()
    {
        if (View3D.Camera is not PerspectiveCamera camera) return;
        if (_orbitBaseOffset.LengthSquared < 1e-9) return;

        Vector3D offset = RotateByQuaternion(_orbitBaseOffset, _orbitRotation);
        Vector3D up = RotateByQuaternion(_orbitBaseUp, _orbitRotation);
        if (up.LengthSquared < 1e-9) up = new Vector3D(0, 0, 1);

        camera.Position = _orbitTarget + offset;
        camera.LookDirection = -offset;
        camera.UpDirection = up;
    }

    /// <summary>
    /// 是否允许内置的 120ms 自动刷新（读取引擎实时输出）。
    /// 宿主自己按帧喂 UpdateAxes 时应设为 false，否则两路数据互相覆盖会闪动
    /// （编排页的实时预览就是这种用法）。
    /// </summary>
    public bool AutoRefreshEnabled
    {
        get => _autoRefresh;
        set
        {
            if (_autoRefresh == value) return;
            _autoRefresh = value;
            if (value)
            {
                if (IsLoaded) StartPreview();
            }
            else
            {
                StopPreview();
            }
        }
    }

    /// <summary>轴名归一化：容错大小写与空白，非法轴名按“清除高亮”处理。</summary>
    private static string NormalizeAxisId(string? axisId)
    {
        if (string.IsNullOrWhiteSpace(axisId)) return string.Empty;
        string key = axisId.Trim().ToUpperInvariant();
        foreach (string name in AxisNames)
            if (name == key) return key;
        return string.Empty;
    }

    /// <summary>
    /// 高亮材质：#2BFFB8 漫反射 + 同色系自发光（WPF 没有 emissive 属性，用
    /// EmissiveMaterial 模拟自发光辉光，压暗到 ~40% 以免整块过曝成白色）+ 冷色高光。
    /// </summary>
    private static Material MakeHighlightMaterial()
    {
        var group = new MaterialGroup
        {
            Children =
            {
                new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x2B, 0xFF, 0xB8))),
                new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x12, 0x66, 0x4A))),
                new SpecularMaterial(new SolidColorBrush(Color.FromRgb(0x9F, 0xFF, 0xE2)), 45.0),
            },
        };
        group.Freeze();   // 共享材质：冻结后多处引用无额外开销
        return group;
    }

    // ══════════════════════════════════════════════════════════════
    //  刷新（DispatcherTimer 120ms）
    // ══════════════════════════════════════════════════════════════

    private void StartPreview()
    {
        if (!_timerRunning)
        {
            _timer.Start();
            _timerRunning = true;
        }
        RefreshPreview();
    }

    private void StopPreview()
    {
        if (!_timerRunning) return;
        _timer.Stop();
        _timerRunning = false;
    }

    /// <summary>
    /// 内置定时器回调：若宿主（如 StrokesPage）在最近一个刷新周期内已主动调用过
    /// RefreshPreview，则本次跳过，避免两处定时器把刷新频率翻倍。
    /// </summary>
    private void TimerTick()
    {
        if (!_autoRefresh) return;
        long now = Stopwatch.GetTimestamp();
        if (now - _lastRefreshTicks < Stopwatch.Frequency / 20) return;   // 50ms 去重窗口
        RefreshPreview();
    }

    /// <summary>
    /// 拉取引擎实时轴值并刷新模型（可由外部定时器主动调用）。
    /// 引擎没有公开的“当前输出”读取接口时降级为静止中位 + GetAxisAmp() 的振幅环，
    /// 并在顶部角标标注数据来源。
    /// </summary>
    public void RefreshPreview()
    {
        if (!IsLoaded) return;
        _lastRefreshTicks = Stopwatch.GetTimestamp();
        try
        {
            double[]? live = ReadEngineOutput();
            if (live != null)
            {
                UpdateAxes(live);
                SetStatus("实时输出");
            }
            else
            {
                UpdateAxes(AxisHome);
                SetStatus("振幅估算");
            }
            UpdateAmp(App.Engine.GetAxisAmp());
        }
        catch (Exception)
        {
            // 预览属于纯可视化，任何异常都不允许影响动作页主流程
            SetStatus("预览异常");
        }
    }

    private static double[]? ReadEngineOutput()
    {
        try
        {
            double[] values = App.Engine.GetLastOutputSnapshot();
            return values.Length >= 6 ? values : null;
        }
        catch (Exception)
        {
            return null;   // 引擎未就绪时走降级路径（用振幅数据）
        }
    }

    private void RefreshReadout()
    {
        if (LiveLabel == null) return;
        LiveLabel.Text =
            $"L0 {_axis[0]:000}  L1 {_axis[1]:000}  L2 {_axis[2]:000}\n" +
            $"R0 {_axis[3]:000}  R1 {_axis[4]:000}  R2 {_axis[5]:000}";
    }

    /// <summary>
    /// 外部按帧喂数据时用它改写左上角角标（例如"试看中 / 编排预览"）。
    /// 不开放的话，宿主明明在喂试看数据，角标却还写着"实时输出"——等于在骗用户。
    /// 控件自己的定时器在 AutoRefreshEnabled=false 时不会覆盖它。
    /// </summary>
    public void SetStatusText(string text) => SetStatus(text);

    private void SetStatus(string text)
    {
        if (StatusLabel == null || _statusShown && StatusLabel.Text == text) return;
        StatusLabel.Text = text;
        _statusShown = true;
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        ResetView();
        e.Handled = true;
    }

    /// <summary>轴值 / 振幅的只读快照，供调试或外部读取。</summary>
    public double[] AxisValues => (double[])_axis.Clone();
    public double[] AmpValues => (double[])_amp.Clone();

    /// <summary>轴名顺序（与引擎一致）：L0 / L1 / L2 / R0 / R1 / R2。</summary>
    public static IReadOnlyList<string> AxisOrder => AxisNames;
}
