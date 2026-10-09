using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Hexa.Services.Sr6;

/// <summary>SR6 模型里的一个可动部件（对应 three.js 的一个 Object3D）。</summary>
internal sealed class Sr6Part
{
    public Sr6Part(string name)
    {
        Name = name;
        Transform = new MatrixTransform3D();
        Visual = new Model3DGroup { Transform = Transform };
    }

    /// <summary>部件名（与 osr-emu 的 objects 键一致，便于对照/单测）。</summary>
    public string Name { get; }

    /// <summary>局部坐标（模型坐标系：毫米，Z 轴向上，与 osr-emu 完全一致）。</summary>
    public Vector3 Position;

    /// <summary>局部旋转（对应 three.js 的 object.quaternion）。</summary>
    public Quaternion Rotation = Quaternion.Identity;

    /// <summary>lookAt 用的 up 向量（对应 three.js 的 object.up）。</summary>
    public Vector3 Up = new(0, 1, 0);

    /// <summary>每帧只改 Matrix，不重建视觉树。</summary>
    public MatrixTransform3D Transform { get; }

    /// <summary>承载该部件几何的视觉节点。</summary>
    public Model3DGroup Visual { get; }

    /// <summary>该部件包含的网格（可能多于一个：例如俯仰连杆含 3 个 OBJ 组）。</summary>
    public List<GeometryModel3D> Meshes { get; } = new();
}

/// <summary>
/// SR6 六轴设备的真实机械模型与运动学。
///
/// 这是 osr-emu（https://github.com/ayvasoftware/osr-emu，MIT License）里
/// <c>lib/models/sr6/sr6.js</c> 的 C# 移植版：网格取自 <see cref="Sr6Assets"/>，
/// 装配顺序、关节定位、正/逆运动学、伺服角度算法（直接照搬 SR6 固件）逐行对应，
/// 因此模型姿态与官网 3D 预览一致 —— 六轴各自都有真实的连杆机构在动，
/// 而不是把几个方块摆在一起。
///
/// 坐标与单位：模型自带坐标系（毫米、Z 轴向上），根节点统一绕 X 轴旋转 -90°
/// （osr-emu 的 orientation）后变成 Y 轴向上的世界坐标，正好符合 WPF 的默认相机。
///
/// 每帧只更新各部件的 <see cref="MatrixTransform3D.Matrix"/>（结构体赋值，无分配），
/// 几何与视觉树只在构造时建一次。
/// </summary>
internal sealed class Sr6Rig
{
    /// <summary>osr-emu 里模型整体绕 X 轴的旋转（-90°），把 Z 轴向上转成 Y 轴向上。</summary>
    public const double Orientation = -Math.PI / 2;

    private const float PitchRange = 50;              // mm
    private const float AngleLinkLength = 185;        // mm
    private const float ReceiverWidth = 145.5f;       // mm
    private const float UpperLinkOffset = 6;          // mm
    private const float SwayRange = 30;               // mm
    private const float MainLinkArmLength = 175;      // mm
    private const float PitcherLinkRadius = 75;       // mm

    private static readonly Vector3 UnitX = new(1, 0, 0);
    private static readonly Vector3 UnitY = new(0, 1, 0);
    private static readonly Vector3 UnitZ = new(0, 0, 1);

    private readonly Dictionary<string, Sr6Part> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<GeometryModel3D>> _axisMeshes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<GeometryModel3D, Material> _baseMaterials = new();
    private readonly Quaternion _parentRotation =
        Quaternion.CreateFromAxisAngle(new Vector3(1, 0, 0), (float)Orientation);

    /// <param name="applyOrientation">
    /// true = 套上 osr-emu 那套「整组绕 X 轴转 −90°」的朝向（配合 three.js 的 camera.up=(0,0,1)）；
    /// false = 保持模型原生坐标（Z 轴向上）—— 这是 Hexa 预览采用的方式：
    /// 实测官网那套组合会把机器摆成侧躺，原生坐标 + 相机 up=(0,0,1) 才是正常站姿。
    /// </param>
    public Sr6Rig(bool applyOrientation = false)
    {
        Root = new Model3DGroup();
        if (applyOrientation)
        {
            Root.Transform = new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(1, 0, 0), Orientation * 180.0 / Math.PI));
        }

        BuildModel();
        BuildAxisHighlightMap();
        Update(new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 }, new double[] { 1, 1, 1, 1, 1, 1 });
    }

    /// <summary>根节点（已应用 orientation 旋转），挂到预览控件即可。</summary>
    public Model3DGroup Root { get; }

    /// <summary>全部部件（按装配名）。</summary>
    public IReadOnlyDictionary<string, Sr6Part> Parts => _parts;

    /// <summary>某个轴对应的高亮部件（用于轴高亮）。</summary>
    public IReadOnlyList<GeometryModel3D> MeshesForAxis(string axisId) =>
        _axisMeshes.TryGetValue(axisId, out IReadOnlyList<GeometryModel3D>? meshes)
            ? meshes
            : Array.Empty<GeometryModel3D>();

    /// <summary>部件原始材质（高亮后还原用）。</summary>
    public IReadOnlyDictionary<GeometryModel3D, Material> BaseMaterials => _baseMaterials;

    /// <summary>模型在“世界坐标”（根节点旋转之后）下的包围盒，用于自动取景。</summary>
    public Rect3D WorldBounds => Root.Bounds;

    // ══════════════════════════════════════════════════════════════
    //  装配
    // ══════════════════════════════════════════════════════════════

    private void BuildModel()
    {
        // 材质比 osr-emu 原版整体提亮一档：原色（#1E1E1E / #131313）在深色 UI 背景上
        // 几乎和背景融成一片，看不出结构。蓝色件保持 ayva 的品牌色但略微提亮。
        // 漫反射色压到中低亮度、高光色压到很暗：环境光 + 主光 + 补光叠加后不会过曝，
        // 否则大面积平坦表面会泛白（WPF 的光照是加法，超 1 就夹到白）。
        // 纯色但**够深够饱和**：太淡会显得没质感（用户反馈「颜色太淡」）。
        Material ayvaBlue = MakeMaterial(0x2E, 0x54, 0xA6);
        Material dark = MakeMaterial(0x25, 0x2C, 0x35);
        Material servo = MakeMaterial(0x1A, 0x1F, 0x25);
        Material bearing = MakeMaterial(0x93, 0x9E, 0xAB);

        AddPart("base", "sr6_base.obj", dark);
        AddPart("mainArmServos", "sr6_servos.obj", servo);
        AddPart("lid", "sr6_lid.obj", ayvaBlue);
        AddPart("receiver", "sr6_receiver.obj", ayvaBlue);
        AddPart("modCase", "sr6_modcase.obj", dark);

        foreach (string name in new[] { "upperLeftArm", "upperRightArm", "lowerLeftArm", "lowerRightArm" })
            AddPart(name, "sr6_arm.obj", ayvaBlue);

        foreach (string name in new[] { "upperLeftLink", "upperRightLink", "lowerLeftLink", "lowerRightLink" })
            AddPart(name, "sr6_link.obj", dark);

        foreach (string name in new[]
                 {
                     "upperLeftFrontBearing", "upperLeftBackBearing",
                     "upperRightFrontBearing", "upperRightBackBearing",
                     "lowerRightFrontBearing", "lowerRightBackBearing",
                     "lowerLeftFrontBearing", "lowerLeftBackBearing",
                 })
        {
            AddPart(name, "sr6_bearing.obj", bearing, smoothNormals: true);
        }

        AddPart("leftPitcher", "sr6_pitcher_left.obj", ayvaBlue);
        AddPart("rightPitcher", "sr6_pitcher_right.obj", ayvaBlue);
        AddPart("leftPitcherLink", "sr6_pitcher_link_left.obj", dark, bearingMaterial: bearing);
        AddPart("rightPitcherLink", "sr6_pitcher_link_right.obj", dark, bearingMaterial: bearing);
    }

    private void AddPart(
        string name, string resource, Material material,
        bool smoothNormals = false, Material? bearingMaterial = null)
    {
        var part = new Sr6Part(name);
        foreach (Sr6MeshGroup group in Sr6Assets.Get(resource, smoothNormals))
        {
            // 俯仰连杆的 OBJ 里混了两种材质：pitcherLink 用深色，rodEndBearing 用金属色。
            Material groupMaterial =
                bearingMaterial != null && group.Name.Contains("rodEndBearing", StringComparison.Ordinal)
                    ? bearingMaterial
                    : material;

            var model = new GeometryModel3D(group.Mesh, groupMaterial) { BackMaterial = groupMaterial };
            part.Meshes.Add(model);
            part.Visual.Children.Add(model);
            _baseMaterials[model] = groupMaterial;
        }
        _parts[name] = part;
        Root.Children.Add(part.Visual);
    }

    /// <summary>
    /// 轴 → 部件映射（高亮用）。按机构学语义对应：
    /// L0 上下 = 接收器；L1 前后 = 四根主臂；L2 左右 = 四根连杆；
    /// R0 扭转 = 机头外壳；R1 滚转 = 左右俯仰臂；R2 俯仰 = 俯仰连杆 + 前轴承。
    /// </summary>
    private void BuildAxisHighlightMap()
    {
        AddAxisMeshes("L0", "receiver");
        AddAxisMeshes("L1", "upperLeftArm", "upperRightArm", "lowerLeftArm", "lowerRightArm");
        AddAxisMeshes("L2", "upperLeftLink", "upperRightLink", "lowerLeftLink", "lowerRightLink");
        AddAxisMeshes("R0", "modCase");
        AddAxisMeshes("R1", "leftPitcher", "rightPitcher");
        AddAxisMeshes("R2",
            "leftPitcherLink", "rightPitcherLink",
            "upperLeftFrontBearing", "upperRightFrontBearing",
            "lowerLeftFrontBearing", "lowerRightFrontBearing");
    }

    private void AddAxisMeshes(string axisId, params string[] partNames)
    {
        var meshes = new List<GeometryModel3D>();
        foreach (string partName in partNames)
            if (_parts.TryGetValue(partName, out Sr6Part? part)) meshes.AddRange(part.Meshes);
        _axisMeshes[axisId] = meshes;
    }

    /// <summary>
    /// 纯色材质：只有漫反射。两条规矩都是用户反馈后定下来的：
    ///   ① 不要自发光（EmissiveMaterial）——它加一层与光照无关的恒定颜色，机器像蒙了光膜；
    ///   ② 不要高光（SpecularMaterial）——大面积平面上的高光会泛白、糊掉明暗关系；
    /// 用户要的是「纯色质感」：干净的一块色，只靠光照明暗区分面。
    /// </summary>
    private static Material MakeMaterial(byte r, byte g, byte b)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(r, g, b)));
        material.Freeze();
        return material;
    }

    // ══════════════════════════════════════════════════════════════
    //  运动学（osr-emu sr6.js 的逐行移植）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 按六轴值更新模型姿态。
    /// </summary>
    /// <param name="axes01">六轴当前位置，0..1，顺序 L0/L1/L2/R0/R1/R2（0.5 = 中位）。</param>
    /// <param name="scale01">六轴行程缩放，0..1（osr-emu 的 scale，默认 1）。</param>
    public void Update(double[] axes01, double[] scale01)
    {
        ArgumentNullException.ThrowIfNull(axes01);
        var axes = new float[6];
        var scale = new float[6];
        for (int i = 0; i < 6; i++)
        {
            axes[i] = (float)Math.Clamp(i < axes01.Length && double.IsFinite(axes01[i]) ? axes01[i] : 0.5, 0, 1);
            scale[i] = (float)Math.Clamp(i < scale01.Length && double.IsFinite(scale01[i]) ? scale01[i] : 1, 0, 1);
        }

        SetBaseArmPositions();
        PerformKinematics(axes, scale);
        ApplyTransforms();
    }

    private Sr6Part P(string name) => _parts[name];

    /// <summary>主臂/俯仰臂在底座上的安装位置（osr-emu #setBaseArmPositions）。</summary>
    private void SetBaseArmPositions()
    {
        const float armX = 58.5f;
        const float upperArmY = 0f;
        const float lowerArmY = 30f;
        const float armZ = 59.92f - 10f;   // 舵机轴离舵机边缘约 10mm
        const float pitcherArmX = 14.318f;
        const float pitcherArmY = -29.72f;
        const float pitcherArmZ = 49.325f;

        P("upperLeftArm").Position = new Vector3(armX, upperArmY, armZ);
        P("upperLeftArm").Rotation = FromEulerXyz(0, MathF.PI, 0);

        P("upperRightArm").Position = new Vector3(-armX, upperArmY, armZ);
        P("upperRightArm").Rotation = Quaternion.Identity;

        P("lowerLeftArm").Position = new Vector3(armX, lowerArmY, armZ);
        P("lowerLeftArm").Rotation = FromEulerXyz(MathF.PI, MathF.PI, 0);

        P("lowerRightArm").Position = new Vector3(-armX, lowerArmY, armZ);
        P("lowerRightArm").Rotation = FromEulerXyz(MathF.PI, 0, 0);

        P("leftPitcher").Position = new Vector3(pitcherArmX, pitcherArmY, pitcherArmZ);
        P("rightPitcher").Position = new Vector3(-pitcherArmX, pitcherArmY, pitcherArmZ);
    }

    private void PerformKinematics(float[] axes, float[] scale)
    {
        float pitchAngle = scale[5] * axes[5] * DegToRad(PitchRange) - DegToRad(PitchRange / 2f);
        float pitchBearingAngle = pitchAngle - DegToRad(14.763202965644615f);
        var receiverDirection = new Vector3(-1, 0, 0);
        var pitcherBearingVector = new Vector3(0, -75, 0);

        Quaternion pitchQuaternion = Quaternion.CreateFromAxisAngle(UnitX, pitchAngle);
        Quaternion pitchBearingQuaternion = Quaternion.CreateFromAxisAngle(UnitX, pitchBearingAngle);
        Quaternion twistQuaternion = Quaternion.CreateFromAxisAngle(
            new Vector3(0, -1, 0), scale[3] * axes[3] * DegToRad(240f) - DegToRad(120f));

        FirmwareServoAngles servoAngles = ComputeFirmwareServoAngles(axes);

        // 主臂与俯仰臂的旋转（覆盖 Euler.x，保留 y —— 与 three.js 的 rotation.x 赋值一致）
        P("leftPitcher").Rotation = FromEulerXyz(servoAngles.LeftPitch, 0, 0);
        P("rightPitcher").Rotation = FromEulerXyz(servoAngles.RightPitch, 0, 0);
        P("upperLeftArm").Rotation = FromEulerXyz(servoAngles.UpperLeft, MathF.PI, 0);
        P("upperRightArm").Rotation = FromEulerXyz(servoAngles.UpperRight, 0, 0);
        P("lowerLeftArm").Rotation = FromEulerXyz(servoAngles.LowerLeft, MathF.PI, 0);
        P("lowerRightArm").Rotation = FromEulerXyz(servoAngles.LowerRight, 0, 0);

        // 连杆（轴承臂）位置：由主臂位置 + 舵机角算出
        P("upperLeftLink").Position = ComputeLinkPosition(P("upperLeftArm").Position, servoAngles.UpperLeft);
        P("upperRightLink").Position = ComputeLinkPosition(P("upperRightArm").Position, servoAngles.UpperRight, -1);
        P("lowerLeftLink").Position = ComputeLinkPosition(P("lowerLeftArm").Position, servoAngles.LowerLeft);
        P("lowerRightLink").Position = ComputeLinkPosition(P("lowerRightArm").Position, servoAngles.LowerRight, -1);

        P("upperLeftBackBearing").Position = P("upperLeftLink").Position;
        P("upperRightBackBearing").Position = P("upperRightLink").Position;
        P("lowerLeftBackBearing").Position = P("lowerLeftLink").Position;
        P("lowerRightBackBearing").Position = P("lowerRightLink").Position;

        bool leftOk = ComputeMainLinkIntersectionPoint(
            P("upperLeftLink").Position, P("lowerLeftLink").Position,
            out Vector3 leftIntersectionPoint, out Vector3 leftIntersectionCenter, out float leftIntersectionRadius);
        bool rightOk = ComputeMainLinkIntersectionPoint(
            P("upperRightLink").Position, P("lowerRightLink").Position,
            out Vector3 rightIntersectionPoint, out Vector3 rightIntersectionCenter, out float rightIntersectionRadius);

        if (!leftOk || !rightOk)
        {
            // 与 osr-emu 相同：无解时保持上一帧姿态（官网此处只打 console.warn）
            return;
        }

        // 两侧交点距离过大时向内收拢，保证主臂仍然“挂”在接收器上
        float actualWidth = (rightIntersectionPoint - leftIntersectionPoint).Length();
        if (actualWidth > ReceiverWidth)
        {
            float offset = (actualWidth - ReceiverWidth) / 2f;
            Vector3 rightCopy = rightIntersectionPoint;
            Vector3 leftCopy = leftIntersectionPoint;
            rightIntersectionPoint += Vector3.Normalize(leftCopy - rightCopy) * offset;
            leftIntersectionPoint += Vector3.Normalize(rightCopy - leftCopy) * offset;
        }

        // L2（左右）：绕 Y 轴在各自交点上做小角度摆动
        float swayArc = scale[2] * SwayRange * ((axes[2] - 0.5f) / 0.5f);
        float rightArcAngle = rightIntersectionRadius > 0 ? swayArc / rightIntersectionRadius : 0;
        float leftArcAngle = leftIntersectionRadius > 0 ? swayArc / leftIntersectionRadius : 0;
        if (rightArcAngle != 0 || leftArcAngle != 0)
        {
            rightIntersectionPoint = Rotate(rightIntersectionPoint - rightIntersectionCenter, UnitY, rightArcAngle)
                                     + rightIntersectionCenter;
            leftIntersectionPoint = Rotate(leftIntersectionPoint - leftIntersectionCenter, UnitY, leftArcAngle)
                                    + leftIntersectionCenter;
        }

        Vector3 upperLeftVector = Vector3.Normalize(leftIntersectionPoint - rightIntersectionPoint);
        Vector3 upperRightVector = Vector3.Normalize(rightIntersectionPoint - leftIntersectionPoint);
        Vector3 leftIntersectionPointShifted = leftIntersectionPoint + upperLeftVector * UpperLinkOffset;
        Vector3 rightIntersectionPointShifted = rightIntersectionPoint + upperRightVector * UpperLinkOffset;

        // 主连杆指向交点；up 用 (0,0,1) 或 (0,1,0)，与 osr-emu 一致
        LookAtLocal(P("upperLeftLink"), ToWorldCoordinates(leftIntersectionPointShifted), UnitZ);
        LookAtLocal(P("upperLeftBackBearing"), ToWorldCoordinates(
            P("upperLeftBackBearing").Position - leftIntersectionPointShifted + P("upperLeftBackBearing").Position),
            UnitZ);

        LookAtLocal(P("upperRightLink"), ToWorldCoordinates(rightIntersectionPointShifted), UnitZ);
        LookAtLocal(P("upperRightBackBearing"), ToWorldCoordinates(
            P("upperRightBackBearing").Position - rightIntersectionPointShifted + P("upperRightBackBearing").Position),
            UnitZ);

        LookAtLocal(P("lowerLeftLink"), ToWorldCoordinates(leftIntersectionPoint), UnitZ);
        LookAtLocal(P("lowerLeftBackBearing"), ToWorldCoordinates(
            P("lowerLeftBackBearing").Position - leftIntersectionPoint + P("lowerLeftBackBearing").Position),
            UnitZ);

        LookAtLocal(P("lowerRightLink"), ToWorldCoordinates(rightIntersectionPoint), UnitZ);
        LookAtLocal(P("lowerRightBackBearing"), ToWorldCoordinates(
            P("lowerRightBackBearing").Position - rightIntersectionPoint + P("lowerRightBackBearing").Position),
            UnitZ);

        // 前轴承（挂在接收器侧）
        P("upperLeftFrontBearing").Position = leftIntersectionPointShifted;
        LookAtLocal(P("upperLeftFrontBearing"), ToWorldCoordinates(
            leftIntersectionPointShifted - P("upperLeftLink").Position + leftIntersectionPointShifted), UnitZ);

        P("upperRightFrontBearing").Position = rightIntersectionPointShifted;
        LookAtLocal(P("upperRightFrontBearing"), ToWorldCoordinates(
            rightIntersectionPointShifted - P("upperRightLink").Position + rightIntersectionPointShifted), UnitZ);

        P("lowerLeftFrontBearing").Position = leftIntersectionPoint;
        LookAtLocal(P("lowerLeftFrontBearing"), ToWorldCoordinates(
            leftIntersectionPoint - P("lowerLeftLink").Position + leftIntersectionPoint), UnitZ);

        P("lowerRightFrontBearing").Position = rightIntersectionPoint;
        LookAtLocal(P("lowerRightFrontBearing"), ToWorldCoordinates(
            rightIntersectionPoint - P("lowerRightLink").Position + rightIntersectionPoint), UnitZ);

        // 接收器姿态：先由两个交点定出滚转，再叠加俯仰
        Vector3 receiverMainBearingAxis = Vector3.Normalize(rightIntersectionPoint - leftIntersectionPoint);
        Vector3 rotationAxis = Vector3.Normalize(Vector3.Cross(receiverDirection, receiverMainBearingAxis));
        float rotationAngle = MathF.Acos(Math.Clamp(Vector3.Dot(receiverDirection, receiverMainBearingAxis), -1f, 1f));
        Quaternion rollQuaternion = Quaternion.CreateFromAxisAngle(rotationAxis, rotationAngle);

        Vector3 leftPitcherEndBearingPosition = Vector3.Transform(new Vector3(0, -55, 0),
            Quaternion.Multiply(rollQuaternion, pitchBearingQuaternion)) + leftIntersectionPoint;

        // 左俯仰连杆：圆（俯仰臂孔）与球（接收器孔）求交
        Vector3[]? leftPoints = CircleSphereIntersection(
            P("leftPitcher").Position, PitcherLinkRadius, receiverDirection,
            leftPitcherEndBearingPosition, AngleLinkLength);

        if (leftPoints != null)
        {
            P("leftPitcherLink").Position = IntersectionPointsToPosition(leftPoints);

            // 用逆运动学修正左舵机角
            Vector3 correctLeft = P("leftPitcherLink").Position - P("leftPitcher").Position;
            Vector3 currentLeft = Rotate(pitcherBearingVector, receiverDirection, -servoAngles.LeftPitch);
            float correctionLeft = AngleBetween(correctLeft, currentLeft);
            float directionLeft = VectorDirection(correctLeft, currentLeft, UnitX);
            P("leftPitcher").Rotation = FromEulerXyz(
                EulerX(P("leftPitcher").Rotation) - correctionLeft * directionLeft, 0, 0);

            LookAtLocal(P("leftPitcherLink"), ToWorldCoordinates(leftPitcherEndBearingPosition), UnitZ);

            Vector3 rightPitcherEndBearingPosition =
                leftPitcherEndBearingPosition + (rightIntersectionPoint - leftIntersectionPoint);

            Vector3[]? rightPoints = CircleSphereIntersection(
                P("rightPitcher").Position, PitcherLinkRadius, receiverDirection,
                rightPitcherEndBearingPosition, AngleLinkLength);

            if (rightPoints != null)
            {
                P("rightPitcherLink").Position = IntersectionPointsToPosition(rightPoints);

                Vector3 correctRight = P("rightPitcherLink").Position - P("rightPitcher").Position;
                Vector3 currentRight = Rotate(pitcherBearingVector, receiverDirection, -servoAngles.RightPitch);
                float correctionRight = AngleBetween(correctRight, currentRight);
                float directionRight = VectorDirection(correctRight, currentRight, UnitX);
                P("rightPitcher").Rotation = FromEulerXyz(
                    EulerX(P("rightPitcher").Rotation) - correctionRight * directionRight, 0, 0);
            }

            LookAtLocal(P("rightPitcherLink"), ToWorldCoordinates(rightPitcherEndBearingPosition), UnitZ);
        }

        Quaternion receiverQuaternion = Quaternion.Multiply(rollQuaternion, pitchQuaternion);
        P("receiver").Position = Vector3.Lerp(leftIntersectionPoint, rightIntersectionPoint, 0.5f);
        P("receiver").Rotation = receiverQuaternion;
        P("modCase").Position = P("receiver").Position;
        P("modCase").Rotation = Quaternion.Multiply(receiverQuaternion, twistQuaternion);
    }

    private static float EulerX(Quaternion q)
    {
        // 只用于“把当前 x 角改掉”的场景：本模型里这些部件的 Euler.y 恒为 0，
        // 因此从四元数反解 x 角足够精确（等价于 three.js 直接改写 rotation.x）。
        return MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
    }

    private static Vector3 ComputeLinkPosition(Vector3 armPosition, float angle, float scale = 1f)
        => Rotate(new Vector3(0, -50, 0), UnitX, angle) + armPosition + UnitX * (scale * 14.5f);

    private static bool ComputeMainLinkIntersectionPoint(
        Vector3 upperLinkPosition, Vector3 lowerLinkPosition,
        out Vector3 intersection, out Vector3 center, out float radius)
    {
        intersection = center = default;
        radius = 0;
        if (!CircleCircleIntersection(
                upperLinkPosition, MainLinkArmLength, lowerLinkPosition, MainLinkArmLength,
                out center, out radius))
        {
            return false;
        }
        if (radius == 0) return false;

        Vector3 tangentVector = Vector3.Normalize(Vector3.Cross(upperLinkPosition - lowerLinkPosition, UnitX));
        intersection = center + tangentVector * radius;
        return true;
    }

    private static Vector3 IntersectionPointsToPosition(Vector3[] points) =>
        points.Length == 1 ? points[0]
        : points[0].Y < points[1].Y ? points[0] : points[1];

    private Vector3 ToWorldCoordinates(Vector3 vector) => Vector3.Transform(vector, _parentRotation);

    /// <summary>
    /// 对应 three.js 的 Object3D.lookAt：局部 +Z 指向目标，用 up 定滚转。
    /// osr-emu 传入的是世界坐标目标（已乘上根节点旋转），所以这里同样按世界坐标处理，
    /// 最后再折算回父节点的局部旋转。
    /// </summary>
    private void LookAtLocal(Sr6Part part, Vector3 worldTarget, Vector3 up)
    {
        Vector3 worldPosition = Vector3.Transform(part.Position, _parentRotation);
        Vector3 z = worldTarget - worldPosition;
        if (z.LengthSquared() < 1e-12f) z = UnitZ;
        z = Vector3.Normalize(z);

        Vector3 x = Vector3.Cross(up, z);
        if (x.LengthSquared() < 1e-12f)
        {
            z = Vector3.Normalize(new Vector3(z.X + 1e-4f, z.Y, z.Z));
            x = Vector3.Cross(up, z);
        }
        x = Vector3.Normalize(x);
        Vector3 y = Vector3.Cross(z, x);

        Quaternion worldRotation = QuaternionFromBasis(x, y, z);
        part.Rotation = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Conjugate(_parentRotation), worldRotation));
    }

    /// <summary>
    /// 伺服角度算法 —— 直接照搬 SR6 固件（osr-emu #computeFirmwareServoAngles）。
    /// 输入是 0..1 的轴值，输出是各臂绕 X 轴的旋转角（弧度）。
    /// </summary>
    private static FirmwareServoAngles ComputeFirmwareServoAngles(float[] axes)
    {
        const float pitchServoZero = 1580;
        const float rightPitchServoZero = (1515.105f - pitchServoZero) + 1515.105f;
        const float servoZero = 1515.105f;
        const float servoFrequency = 330;
        const float servoInterval = 1000000f / servoFrequency;
        const float msPerRad = 637;

        static float SetMainServo(float x, float y)
        {
            // 目标点是接收器枢轴在 1/100 毫米下的 x,y 坐标
            x /= 100f; y /= 100f;
            float gamma = MathF.Atan2(x, y);
            float csq = x * x + y * y;
            float c = MathF.Sqrt(csq);
            float betaCos = (csq - 28125f) / (100f * c);
            betaCos = betaCos < -1 ? -1 : betaCos > 1 ? 1 : betaCos;
            float beta = MathF.Acos(betaCos);
            return msPerRad * (gamma + beta - 3.14159f);
        }

        static float SetPitchServo(float x, float y, float z, float pitch)
        {
            pitch *= 0.0001745f;                       // 1/100 度 → 弧度
            x += 5500f * MathF.Sin(0.2618f + pitch);
            y -= 5500f * MathF.Cos(0.2618f + pitch);
            x /= 100f; y /= 100f; z /= 100f;
            float bsq = 36250f - (75f + z) * (75f + z);
            float gamma = MathF.Atan2(x, y);
            float csq = x * x + y * y;
            float c = MathF.Sqrt(csq);
            float betaCos = (csq + 5625f - bsq) / (150f * c);
            betaCos = betaCos < -1 ? -1 : betaCos > 1 ? 1 : betaCos;
            float beta = MathF.Acos(betaCos);
            return msPerRad * (gamma + beta - 3.14159f);
        }

        float roll = MapDecimal(axes[4], 0, 0.9999f, -3000, 3000);
        float pitch = MapDecimal(axes[5], 0, 0.9999f, -2500, 2500);
        float fwd = MapDecimal(axes[1], 0, 0.9999f, -3000, 3000);      // 60mm
        float thrust = MapDecimal(axes[0], 0, 0.9999f, -6000, 6000);   // 120mm 行程
        float side = MapDecimal(axes[2], 0, 0.9999f, -3000, 3000);     // 60mm

        float out1 = SetMainServo(16248 - fwd, 1500 + thrust + roll);   // 左下
        float out2 = SetMainServo(16248 - fwd, 1500 - thrust - roll);   // 左上
        float out5 = SetMainServo(16248 - fwd, 1500 - thrust + roll);   // 右上
        float out6 = SetMainServo(16248 - fwd, 1500 + thrust - roll);   // 右下

        float out3 = SetPitchServo(16248 - fwd, 4500 - thrust, side - 1.5f * roll, -pitch);
        float out4 = SetPitchServo(16248 - fwd, 4500 - thrust, -side + 1.5f * roll, -pitch);

        float lowerLeftServo = MapDecimal(servoZero - out1, 0, servoInterval, 0, 65535);
        float upperLeftServo = MapDecimal(servoZero + out2, 0, servoInterval, 0, 65535);
        float leftPitchServo = MapDecimal(
            Constrain(pitchServoZero - out3, pitchServoZero - 600, pitchServoZero + 1000), 0, servoInterval, 0, 65535);
        float rightPitchServo = MapDecimal(
            Constrain(rightPitchServoZero + out4, rightPitchServoZero - 1000, rightPitchServoZero + 600),
            0, servoInterval, 0, 65535);
        float upperRightServo = MapDecimal(servoZero - out5, 0, servoInterval, 0, 65535);
        float lowerRightServo = MapDecimal(servoZero + out6, 0, servoInterval, 0, 65535);

        const float servoScale = 0.5f;   // 180° 旋转

        return new FirmwareServoAngles(
            LowerLeft: ServoToRotation(lowerLeftServo, servoScale) - MathF.PI,
            UpperLeft: ServoToRotation(upperLeftServo, servoScale),
            LowerRight: ServoToRotation(lowerRightServo, -servoScale) - MathF.PI,
            UpperRight: ServoToRotation(upperRightServo, -servoScale),
            LeftPitch: ServoToRotation(leftPitchServo, -servoScale),
            RightPitch: ServoToRotation(rightPitchServo, servoScale));
    }

    private readonly record struct FirmwareServoAngles(
        float LowerLeft, float UpperLeft, float LowerRight, float UpperRight,
        float LeftPitch, float RightPitch);

    // ══════════════════════════════════════════════════════════════
    //  变换写回
    // ══════════════════════════════════════════════════════════════

    private void ApplyTransforms()
    {
        foreach (Sr6Part part in _parts.Values)
            part.Transform.Matrix = BuildMatrix(part.Position, part.Rotation);
    }

    /// <summary>
    /// (位置, 四元数) → WPF Matrix3D。
    /// three.js 用列向量约定（v' = R·v + t），WPF 用行向量约定（v' = v·M + offset），
    /// 所以旋转块取转置、平移写进 Offset。
    /// </summary>
    private static Matrix3D BuildMatrix(Vector3 position, Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;

        // 列向量约定下的旋转矩阵元素
        float r11 = 1 - 2 * (yy + zz), r12 = 2 * (xy - wz), r13 = 2 * (xz + wy);
        float r21 = 2 * (xy + wz), r22 = 1 - 2 * (xx + zz), r23 = 2 * (yz - wx);
        float r31 = 2 * (xz - wy), r32 = 2 * (yz + wx), r33 = 1 - 2 * (xx + yy);

        return new Matrix3D(
            r11, r21, r31, 0,
            r12, r22, r32, 0,
            r13, r23, r33, 0,
            position.X, position.Y, position.Z, 1);
    }

    // ══════════════════════════════════════════════════════════════
    //  数学工具（osr-emu lib/util.js 的对应实现）
    // ══════════════════════════════════════════════════════════════

    private static float DegToRad(float degrees) => degrees * MathF.PI / 180f;

    private static float MapDecimal(float value, float inMin, float inMax, float outMin = 0, float outMax = 1)
        => (value - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;

    private static float Constrain(float value, float min, float max) => MathF.Max(min, MathF.Min(value, max));

    private static float ServoToRotation(float servoValue, float scale = 1)
        => MapDecimal(servoValue, 0, 65535, DegToRad(-180 * scale), DegToRad(180 * scale));

    private static Vector3 Rotate(Vector3 v, Vector3 axis, float angle)
        => Vector3.Transform(v, Quaternion.CreateFromAxisAngle(axis, angle));

    private static float AngleBetween(Vector3 a, Vector3 b)
    {
        float lengths = a.Length() * b.Length();
        if (lengths < 1e-9f) return 0;
        return MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / lengths, -1f, 1f));
    }

    private static float VectorDirection(Vector3 a, Vector3 b, Vector3 up)
    {
        Vector3 right = Vector3.Cross(up, a);
        float dir = Vector3.Dot(right, b);
        return dir > 0 ? 1 : dir < 0 ? -1 : 0;
    }

    /// <summary>three.js Euler('XYZ') → 四元数。</summary>
    private static Quaternion FromEulerXyz(float x, float y, float z)
    {
        float c1 = MathF.Cos(x / 2), s1 = MathF.Sin(x / 2);
        float c2 = MathF.Cos(y / 2), s2 = MathF.Sin(y / 2);
        float c3 = MathF.Cos(z / 2), s3 = MathF.Sin(z / 2);
        return new Quaternion(
            s1 * c2 * c3 + c1 * s2 * s3,
            c1 * s2 * c3 - s1 * c2 * s3,
            c1 * c2 * s3 + s1 * s2 * c3,
            c1 * c2 * c3 - s1 * s2 * s3);
    }

    /// <summary>由正交基（列向量 = x/y/z 轴）构造四元数。</summary>
    private static Quaternion QuaternionFromBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        float m11 = x.X, m12 = y.X, m13 = z.X;
        float m21 = x.Y, m22 = y.Y, m23 = z.Y;
        float m31 = x.Z, m32 = y.Z, m33 = z.Z;
        float trace = m11 + m22 + m33;

        if (trace > 0)
        {
            float s = MathF.Sqrt(trace + 1f) * 2f;
            return new Quaternion((m32 - m23) / s, (m13 - m31) / s, (m21 - m12) / s, 0.25f * s);
        }
        if (m11 > m22 && m11 > m33)
        {
            float s = MathF.Sqrt(1f + m11 - m22 - m33) * 2f;
            return new Quaternion(0.25f * s, (m12 + m21) / s, (m13 + m31) / s, (m32 - m23) / s);
        }
        if (m22 > m33)
        {
            float s = MathF.Sqrt(1f + m22 - m11 - m33) * 2f;
            return new Quaternion((m12 + m21) / s, 0.25f * s, (m23 + m32) / s, (m13 - m31) / s);
        }
        else
        {
            float s = MathF.Sqrt(1f + m33 - m11 - m22) * 2f;
            return new Quaternion((m13 + m31) / s, (m23 + m32) / s, 0.25f * s, (m21 - m12) / s);
        }
    }

    /// <summary>两圆交点（osr-emu circleCircleIntersection）。</summary>
    private static bool CircleCircleIntersection(
        Vector3 c1, float r1, Vector3 c2, float r2, out Vector3 center, out float radius)
    {
        center = default;
        radius = 0;
        Vector3 difference = c2 - c1;
        float distance = difference.Length();

        if (distance == 0 || r1 + r2 < distance || distance + MathF.Min(r1, r2) < MathF.Max(r1, r2))
            return false;

        if (r1 + r2 == distance)
        {
            center = (c1 + difference) * (r1 / distance);
            return true;
        }
        if (distance + MathF.Min(r1, r2) == MathF.Max(r1, r2))
        {
            Vector3 larger = r1 > r2 ? c1 : c2;
            Vector3 smaller = r1 < r2 ? c1 : c2;
            float largerRadius = MathF.Max(r1, r2);
            center = larger + (smaller - larger) * (largerRadius / distance);
            return true;
        }

        float h = 0.5f + (r1 * r1 - r2 * r2) / (2f * distance * distance);
        center = c1 + difference * h;
        float radicand = r1 * r1 - h * h * distance * distance;
        radius = radicand > 0 ? MathF.Sqrt(radicand) : 0;
        return true;
    }

    /// <summary>圆与球求交（osr-emu circleSphereIntersection）。</summary>
    private static Vector3[]? CircleSphereIntersection(
        Vector3 circleCenter, float circleRadius, Vector3 circlePlaneDirection,
        Vector3 sphereCenter, float sphereRadius)
    {
        Vector3 circlePlaneNormal = Vector3.Normalize(circlePlaneDirection);
        float distanceToPlane = Vector3.Dot(circlePlaneNormal, circleCenter - sphereCenter);
        if (MathF.Abs(distanceToPlane) > sphereRadius) return null;

        Vector3 sphereCircleCenter = sphereCenter + circlePlaneNormal * distanceToPlane;
        if (MathF.Abs(distanceToPlane) == sphereRadius)
        {
            return (sphereCircleCenter - circleCenter).Length() == circleRadius
                ? new[] { sphereCircleCenter }
                : null;
        }

        float sphereCircleRadius = MathF.Sqrt(sphereRadius * sphereRadius - distanceToPlane * distanceToPlane);
        if (!CircleCircleIntersection(
                sphereCircleCenter, sphereCircleRadius, circleCenter, circleRadius,
                out Vector3 center, out float radius))
        {
            return null;
        }
        if (radius == 0) return new[] { center };

        Vector3 tangentVector = Vector3.Normalize(Vector3.Cross(sphereCircleCenter - circleCenter, circlePlaneNormal));
        return new[]
        {
            center - tangentVector * radius,
            center + tangentVector * radius,
        };
    }
}
