using System.Text.Json.Nodes;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Tools.Calibration;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>CALIBRATION-TOOLS-PLAN 第一批：CB-01（.vfcal.json 与 CalibrationService）、CB-02（N 点标定求解）、CB-05（坐标转换增强）。</summary>
public class CalibrationBatch1Tests : IDisposable
{
    private const string Module = "坐标1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vf-calib1-" + Guid.NewGuid().ToString("N"));

    public CalibrationBatch1Tests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, true);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static IReadOnlyList<string> ConfigIssues(ToolBase tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root).Issues.Select(i => i.Message).ToList();
    }

    private static FlowContext Points(double[] rows, double[] columns, double[]? angles = null)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Array("点", "Rows", VariableType.Double, rows));
        ctx.SetVariable(Variable.Array("点", "Columns", VariableType.Double, columns));
        if (angles != null)
        {
            ctx.SetVariable(Variable.Array("点", "Angles", VariableType.Double, angles));
        }
        return ctx;
    }

    private static double[] Array(FlowContext ctx, string name) => (double[])ctx.GetVariable(Module, name).Value;
    private static double Single(FlowContext ctx, string name) => Convert.ToDouble(ctx.GetVariable(Module, name).Value);

    /// <summary>真值：比例 0.05、旋转 3°、轻微剪切与两方向比例差、平移 (12, -7)。</summary>
    private static double[] TrueAffine()
    {
        HOperatorSet.HomMat2dIdentity(out HTuple h);
        HOperatorSet.HomMat2dScale(h, 0.05, 0.052, 0, 0, out h);
        HOperatorSet.HomMat2dSlant(h, 0.01, "x", 0, 0, out h);
        HOperatorSet.HomMat2dRotate(h, 3 * Math.PI / 180, 0, 0, out h);
        HOperatorSet.HomMat2dTranslate(h, 12, -7, out h);
        return h.ToDArr();
    }

    /// <summary>3×3 网格（行 100~400、列 150~450），图像坐标加 ±0.1 像素的确定性噪声。</summary>
    private static List<CalibrationPoint> GridPoints(double[] truth, double noise = 0.1, int seed = 42)
    {
        var random = new Random(seed);
        var points = new List<CalibrationPoint>();
        foreach (double row in new[] { 100.0, 250, 400 })
        {
            foreach (double column in new[] { 150.0, 300, 450 })
            {
                CalibrationService.TransformPoint(truth, row, column, out double x, out double y);
                points.Add(new CalibrationPoint
                {
                    Row = row + (random.NextDouble() * 2 - 1) * noise,
                    Column = column + (random.NextDouble() * 2 - 1) * noise,
                    X = x,
                    Y = y
                });
            }
        }
        return points;
    }

    private static CameraCalibration TestCamera()
    {
        // 焦距 16 mm、kappa -800、像元 5 um、主点 (330, 250)、640×480；测量平面在 0.5 m 处并略微倾斜
        var camParam = new HTuple("area_scan_division", 0.016, -800.0, 5e-6, 5e-6, 330.0, 250.0, 640, 480);
        var pose = new HTuple(0.01, -0.02, 0.5, 2.0, 358.5, 30.0, 0);
        return CameraCalibration.FromHalcon(camParam, pose);
    }

    // ======================= CB-01 文件格式 =======================

    [Fact]
    public void 标定文件_保存后读取_全部字段与数值一致_中文可读()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        Affine2DCalibration affine = CalibrationService.SolveAffine(GridPoints(TrueAffine()), CalibrationTransformType.affine);
        var result = new CalibrationResult
        {
            Kind = CalibrationFileKind.Affine2D,
            CreatedAt = new DateTimeOffset(2026, 10, 7, 20, 30, 15, TimeSpan.FromHours(8)).AddTicks(1234567),
            Description = "工位 1 九点标定",
            Unit = "mm",
            Affine2D = affine,
            RotationCenter = new RotationCenterCalibration
            {
                Row = 240.125, Column = 320.5, X = 1.25, Y = -3.5, Radius = 87.75, RmsError = 0.031,
                Points = { new RotationCenterPoint { Row = 1, Column = 2, Angle = 0.1 }, new RotationCenterPoint { Row = 3, Column = 4 } }
            },
            Camera = TestCamera()
        };
        result.Camera.PlaneThickness = 0.0025;
        result.Camera.RmsError = 0.12;
        result.Camera.PlateDescription = "calplate_160mm.cpd";
        result.Camera.ImageCount = 15;
        string file = PathOf("工位1.vfcal.json");
        CalibrationService.Save(file, result);

        string text = File.ReadAllText(file);
        Assert.Contains("工位 1 九点标定", text);
        Assert.Contains("\"Kind\": \"Affine2D\"", text);
        Assert.Contains("\"TransformType\": \"affine\"", text);

        CalibrationResult loaded = CalibrationService.Load(file);
        Assert.False(loaded.IsLegacyTuple);
        Assert.Equal(result.CreatedAt, loaded.CreatedAt);
        Assert.Equal(affine.HomMat2D, loaded.Affine2D.HomMat2D);
        Assert.Equal(affine.Points.Select(p => (p.Row, p.Column, p.X, p.Y, p.Residual)), loaded.Affine2D.Points.Select(p => (p.Row, p.Column, p.X, p.Y, p.Residual)));
        Assert.Equal((affine.RmsError, affine.MaxError), (loaded.Affine2D.RmsError, loaded.Affine2D.MaxError));
        Assert.Equal((240.125, 320.5, (double?)1.25, (double?)-3.5, 87.75, (double?)0.031), (loaded.RotationCenter.Row, loaded.RotationCenter.Column, loaded.RotationCenter.X, loaded.RotationCenter.Y, loaded.RotationCenter.Radius, loaded.RotationCenter.RmsError));
        Assert.Equal(0.1, loaded.RotationCenter.Points[0].Angle);
        Assert.Null(loaded.RotationCenter.Points[1].Angle);
        Assert.Equal(result.Camera.CamParam.Select(p => (p.Name, p.Value)), loaded.Camera.CamParam.Select(p => (p.Name, p.Value)));
        Assert.Equal(result.Camera.Pose, loaded.Camera.Pose);
        Assert.Equal((0.0025, (double?)0.12, "calplate_160mm.cpd", 15), (loaded.Camera.PlaneThickness, loaded.Camera.RmsError, loaded.Camera.PlateDescription, loaded.Camera.ImageCount));
        // 重新序列化逐字相同
        Assert.Equal(CalibrationService.ToJson(result), CalibrationService.ToJson(loaded));
        Assert.Equal(TestCamera().ToHalconCamParam().ToSArr(), loaded.Camera.ToHalconCamParam().ToSArr());
    }

    [Fact]
    public void 旧版write_tuple矩阵文件_按内容识别继续可读_JSON前导空白与BOM也能识别()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string legacy = PathOf("legacy.tup");
        HOperatorSet.WriteTuple(new HTuple(0.05, 0.001, 10.0, -0.002, 0.051, 20.0), legacy);
        CalibrationResult loaded = CalibrationService.Load(legacy);
        Assert.True(loaded.IsLegacyTuple);
        Assert.Equal(CalibrationFileKind.Affine2D, loaded.Kind);
        Assert.Equal(new[] { 0.05, 0.001, 10.0, -0.002, 0.051, 20.0 }, loaded.Affine2D.HomMat2D);
        Assert.Null(loaded.Affine2D.RmsError);

        string json = CalibrationService.ToJson(CalibrationService.FromAffine(new Affine2DCalibration { HomMat2D = new[] { 1.0, 0, 0, 0, 1, 0 } }, "mm", null));
        string withBom = PathOf("bom.vfcal.json");
        File.WriteAllText(withBom, "\r\n  " + json, new System.Text.UTF8Encoding(true));
        Assert.False(CalibrationService.Load(withBom).IsLegacyTuple);

        string wrongCount = PathOf("three.tup");
        HOperatorSet.WriteTuple(new HTuple(1.0, 2.0, 3.0), wrongCount);
        Assert.Contains("仿射矩阵应为 6 个数", Assert.Throws<InvalidDataException>(() => CalibrationService.Load(wrongCount)).Message);
        Assert.Throws<FileNotFoundException>(() => CalibrationService.Load(PathOf("missing.vfcal.json")));
    }

    [Theory]
    [InlineData("{ 不是 json", "格式错误")]
    [InlineData("{\"FormatVersion\": 2, \"Kind\": \"Affine2D\", \"Affine2D\": {\"HomMat2D\": [1,0,0,0,1,0]}}", "格式版本 2 不受支持")]
    [InlineData("{\"FormatVersion\": 1, \"Kind\": \"Camera\", \"Affine2D\": {\"HomMat2D\": [1,0,0,0,1,0]}}", "Kind 为 Camera，但缺少 Camera 数据")]
    [InlineData("{\"FormatVersion\": 1, \"Kind\": \"Affine2D\", \"Affine2D\": {\"HomMat2D\": [1,0,0,0,1]}}", "HomMat2D 应为 6 个有限数值")]
    [InlineData("{\"FormatVersion\": 1, \"Kind\": \"Camera\", \"Camera\": {\"CamParam\": [{\"Name\":\"camera_type\",\"Value\":\"area_scan_division\"},{\"Name\":\"focus\",\"Value\":0.016}], \"Pose\": [0,0,1]}}", "Pose 应为 7 个有限数值")]
    public void 标定数据格式错误_给出中文说明(string json, string expected)
    {
        Assert.Contains(expected, Assert.Throws<InvalidDataException>(() => CalibrationService.Parse(json, "内嵌标定数据")).Message);
    }

    [Fact]
    public void 相机参数_按名称保存_回到HALCON元组_不支持的类型明确报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var polynomial = new HTuple("area_scan_polynomial", 0.012, 10.0, -100.0, 1000.0, 0.1, -0.1, 4.4e-6, 4.4e-6, 640.0, 480.0, 1280, 960);
        CameraCalibration camera = CameraCalibration.FromHalcon(polynomial, new HTuple(0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0));
        Assert.Equal(new[] { "camera_type", "focus", "k1", "k2", "k3", "p1", "p2", "sx", "sy", "cx", "cy", "image_width", "image_height" }, camera.CamParam.Select(p => p.Name));
        HTuple back = camera.ToHalconCamParam();
        Assert.Equal(polynomial.ToSArr(), back.ToSArr());
        Assert.Equal(HTupleType.INTEGER, back[12].Type);
        Assert.Throws<NotSupportedException>(() => CameraCalibration.FromHalcon(new HTuple("area_scan_tilt_division", 1.0), new HTuple(0.0, 0, 1, 0, 0, 0, 0)));
    }

    // ======================= CB-02 N 点标定求解 =======================

    [Fact]
    public void N点标定_已知仿射加01像素噪声_矩阵误差在噪声量级内()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        double[] truth = TrueAffine();
        Affine2DCalibration solved = CalibrationService.SolveAffine(GridPoints(truth), CalibrationTransformType.affine);
        // 0.1 像素噪声 × 约 0.05 mm/像素 = 0.005 mm：在网格范围内求得的变换与真值之差应在此量级
        foreach (double row in new[] { 100.0, 175, 250, 325, 400 })
        {
            foreach (double column in new[] { 150.0, 225, 300, 375, 450 })
            {
                CalibrationService.TransformPoint(truth, row, column, out double tx, out double ty);
                CalibrationService.TransformPoint(solved.HomMat2D, row, column, out double sx, out double sy);
                Assert.True(Math.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy)) < 0.005, $"({row},{column}) 误差过大");
            }
        }
        Assert.InRange(solved.RmsError.GetValueOrDefault(), 1e-6, 0.005);
        Assert.Equal(solved.Points.Max(p => p.Residual.GetValueOrDefault()), solved.MaxError.GetValueOrDefault(), 12);
        Assert.Equal(9, solved.Points.Count);
        // 无噪声时残差为 0（数值精度内）
        Assert.True(CalibrationService.SolveAffine(GridPoints(truth, noise: 0), CalibrationTransformType.affine).MaxError < 1e-9);
    }

    [Fact]
    public void N点标定_相似与刚体变换_各按真值恢复()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.VectorAngleToRigid(0, 0, 0, 5, -3, 0.2, out HTuple rigid);
        Affine2DCalibration solvedRigid = CalibrationService.SolveAffine(GridPoints(rigid.ToDArr(), noise: 0), CalibrationTransformType.rigid);
        Assert.Equal(rigid.ToDArr(), solvedRigid.HomMat2D, new DoubleTolerance(1e-9));
        HOperatorSet.HomMat2dScale(rigid, 0.04, 0.04, 0, 0, out HTuple similar);
        Affine2DCalibration solvedSimilar = CalibrationService.SolveAffine(GridPoints(similar.ToDArr(), noise: 0), CalibrationTransformType.similarity);
        Assert.Equal(similar.ToDArr(), solvedSimilar.HomMat2D, new DoubleTolerance(1e-9));
        // 刚体变换不能拟合有比例的点对：HALCON 不报错，残差把问题暴露出来
        Assert.True(CalibrationService.SolveAffine(GridPoints(similar.ToDArr(), noise: 0), CalibrationTransformType.rigid).MaxError > 1);
    }

    public static IEnumerable<object[]> DegenerateCases()
    {
        foreach (CalibrationTransformType type in Enum.GetValues<CalibrationTransformType>())
        {
            yield return new object[] { type, new[] { (0.0, 0.0, 0.0, 0.0), (100.0, 0.0, 5.0, 0.0) }, "至少需要 3 个点对（当前 2 个）" };
            yield return new object[] { type, new[] { (0.0, 0.0, 0.0, 0.0), (50.0, 50.0, 2.5, 2.5), (100.0, 100.0, 5.0, 5.0) }, "图像点共线" };
            yield return new object[] { type, new[] { (0.0, 0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 0.0), (100.0, 100.0, 5.0, 5.0) }, "图像点中不重复的点只有 2 个" };
            yield return new object[] { type, new[] { (0.0, 0.0, 0.0, 0.0), (100.0, 0.0, 5.0, 5.0), (0.0, 100.0, 10.0, 10.0) }, "物理点共线" };
            yield return new object[] { type, new[] { (0.0, 0.0, 0.0, 0.0), (100.0, 0.0, double.NaN, 5.0), (0.0, 100.0, 10.0, 10.0) }, "第 2 个点对含无效数值" };
        }
    }

    [Theory]
    [MemberData(nameof(DegenerateCases))]
    public void N点标定_少于3点_共线_重复_无效数值_明确提示(CalibrationTransformType type, (double, double, double, double)[] raw, string expected)
    {
        List<CalibrationPoint> points = raw.Select(p => new CalibrationPoint { Row = p.Item1, Column = p.Item2, X = p.Item3, Y = p.Item4 }).ToList();
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => CalibrationService.SolveAffine(points, type)).Message);
    }

    // ======================= CB-05 坐标转换 =======================

    private AffinePointTool EmbeddedTool(double[] matrix, string unit = "mm")
    {
        var tool = new AffinePointTool(Module) { RowPath = "点.Rows", ColumnPath = "点.Columns" };
        tool.UseEmbeddedCalibration(CalibrationService.ToJson(CalibrationService.FromAffine(new Affine2DCalibration { HomMat2D = matrix }, unit, null)));
        return tool;
    }

    [Fact]
    public void 数组输入与逐个单值输入结果一致_一对多配对_个数不符明确失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        double[] matrix = TrueAffine();
        double[] rows = { 10, 220.5, 333.25 };
        double[] columns = { 40, 18.75, 500 };
        double[] angles = { 0.1, -1.2, 2.9 };
        AffinePointTool tool = EmbeddedTool(matrix);
        tool.AnglePath = "点.Angles";
        tool.OriginRow = 1.5;
        tool.OriginColumn = -2.5;
        using FlowContext all = Points(rows, columns, angles);
        Assert.True(tool.Run(all).IsSuccess);
        Assert.Equal(3, (int)all.GetVariable(Module, "Count").Value);
        for (int i = 0; i < rows.Length; i++)
        {
            using var single = new FlowContext();
            single.SetVariable(Variable.Single("点", "Rows", VariableType.Double, rows[i]));
            single.SetVariable(Variable.Single("点", "Columns", VariableType.Double, columns[i]));
            single.SetVariable(Variable.Single("点", "Angles", VariableType.Double, angles[i]));
            Assert.True(tool.Run(single).IsSuccess);
            Assert.Equal(Array(all, "WorldRows")[i], Single(single, "WorldRow"));
            Assert.Equal(Array(all, "WorldColumns")[i], Single(single, "WorldColumn"));
            Assert.Equal(Array(all, "WorldAngles")[i], Single(single, "WorldAngle"));
            Assert.Equal(AngleMath.ToDegrees(Array(all, "WorldAngles")[i]), Single(single, "WorldAngleDeg"));
            HOperatorSet.AffineTransPoint2d(new HTuple(matrix), rows[i], columns[i], out HTuple wr, out HTuple wc);
            Assert.Equal(wr.D - 1.5, Single(single, "WorldRow"));
            Assert.Equal(wc.D + 2.5, Single(single, "WorldColumn"));
        }
        Assert.Equal(Array(all, "WorldRows")[0], Single(all, "WorldRow"));

        // 一个点、多个角度：一对多
        using FlowContext broadcast = Points(new[] { 10.0 }, new[] { 40.0 }, angles);
        Assert.True(tool.Run(broadcast).IsSuccess);
        Assert.Equal(3, Array(broadcast, "WorldRows").Length);
        Assert.Equal(Array(all, "WorldAngles")[0], Array(broadcast, "WorldAngles")[0]);
        Assert.All(Array(broadcast, "WorldRows"), r => Assert.Equal(Array(all, "WorldRows")[0], r));

        tool.AnglePath = null;
        using FlowContext mismatch = Points(rows, new[] { 1.0, 2.0 });
        NodeResult failed = tool.Run(mismatch);
        Assert.False(failed.IsSuccess);
        Assert.Contains("3 对 2", failed.Message);

        // 空数组：数量 0，单值 NaN，照常成功
        using FlowContext empty = Points(new double[0], new double[0]);
        Assert.True(tool.Run(empty).IsSuccess);
        Assert.Equal(0, (int)empty.GetVariable(Module, "Count").Value);
        Assert.True(double.IsNaN(Single(empty, "WorldRow")));
        Assert.True(double.IsNaN(Single(empty, "WorldAngle")));
        Assert.Empty(Array(empty, "WorldAngles"));
    }

    [Fact]
    public void 角度换算_单位矩阵不变_旋转相加_镜像矩阵方向正确()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        double[] angles = { 0.3, -2.0, 1.4 };
        double Converted(double[] matrix, double angle)
        {
            AffinePointTool tool = EmbeddedTool(matrix);
            tool.AnglePath = "点.Angles";
            using FlowContext ctx = Points(new[] { 50.0 }, new[] { 60.0 }, new[] { angle });
            Assert.True(tool.Run(ctx).IsSuccess);
            return Single(ctx, "WorldAngle");
        }
        static double Wrap(double a) => Math.Atan2(Math.Sin(a), Math.Cos(a));
        HOperatorSet.HomMat2dIdentity(out HTuple identity);
        HOperatorSet.HomMat2dRotate(identity, 0.5, 0, 0, out HTuple rotate);
        HOperatorSet.HomMat2dScale(rotate, 0.05, 0.05, 0, 0, out HTuple scaledRotate);
        foreach (double angle in angles)
        {
            Assert.Equal(angle, Converted(identity.ToDArr(), angle), 12);
            Assert.Equal(Wrap(angle + 0.5), Converted(scaledRotate.ToDArr(), angle), 12);
            // 列方向镜像（WorldColumn = −列）：方向 (−sinφ, cosφ) → (−sinφ, −cosφ)，角度为 π − φ
            Assert.Equal(Wrap(Math.PI - angle), Converted(new[] { 1.0, 0, 0, 0, -1, 0 }, angle), 12);
            // 行方向镜像（WorldRow = −行）：角度为 −φ
            Assert.Equal(Wrap(-angle), Converted(new[] { -1.0, 0, 0, 0, 1, 0 }, angle), 12);
        }
        // 镜像矩阵的行列式为负：若误用“φ + 矩阵旋转量”会得到错误方向，这里与按方向向量直接变换的结果一致
        double[] mirrorRotate = { 0.0, 1, 0, 1, 0, 0 };
        Assert.True(mirrorRotate[0] * mirrorRotate[4] - mirrorRotate[1] * mirrorRotate[3] < 0);
        Assert.Equal(HomMat2D.DirectionToPhi(Math.Cos(0.3), -Math.Sin(0.3)), Converted(mirrorRotate, 0.3), 12);
    }

    [Fact]
    public void Camera方式_与直接调用image_points_to_world_plane一致_角度按两点换算()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        CameraCalibration camera = TestCamera();
        string file = PathOf("camera.vfcal.json");
        CalibrationService.Save(file, new CalibrationResult { Kind = CalibrationFileKind.Camera, Unit = "mm", Camera = camera, CreatedAt = DateTimeOffset.Now });
        var tool = new AffinePointTool(Module)
        {
            RowPath = "点.Rows", ColumnPath = "点.Columns", AnglePath = "点.Angles",
            CalibrationKind = CalibrationKind.Camera, CalibrationFile = file
        };
        double[] rows = { 12.5, 240, 470.25 };
        double[] columns = { 20, 330.5, 610 };
        double[] angles = { 0.0, 0.7, -2.4 };
        Assert.DoesNotContain(ConfigIssues(tool), m => !m.Contains("引用"));
        using FlowContext ctx = Points(rows, columns, angles);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        HOperatorSet.ImagePointsToWorldPlane(camera.ToHalconCamParam(), camera.ToHalconPose(), rows, columns, "mm", out HTuple x, out HTuple y);
        Assert.Equal(x.ToDArr(), Array(ctx, "WorldRows"));
        Assert.Equal(y.ToDArr(), Array(ctx, "WorldColumns"));
        for (int i = 0; i < rows.Length; i++)
        {
            HOperatorSet.ImagePointsToWorldPlane(camera.ToHalconCamParam(), camera.ToHalconPose(), rows[i] - Math.Sin(angles[i]), columns[i] + Math.Cos(angles[i]), "mm", out HTuple x2, out HTuple y2);
            Assert.Equal(Math.Atan2(-(x2.D - x[i].D), y2.D - y[i].D), Array(ctx, "WorldAngles")[i], 12);
        }
        // Camera 方式没有仿射矩阵：Matrix 输出不写、且在引用候选中隐藏
        Assert.False(ctx.TryGetVariable(Module, "Matrix", out _));
        Assert.False(tool.IsParameterVisible("Matrix"));
        Assert.False(tool.IsParameterVisible(nameof(AffinePointTool.MatrixPath)));

        // 单位不受支持
        CalibrationService.Save(file, new CalibrationResult { Kind = CalibrationFileKind.Camera, Unit = "inch", Camera = camera });
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        using FlowContext inch = Points(rows, columns, angles);
        Assert.Contains("单位“inch”不受支持", tool.Run(inch).Message);
    }

    [Fact]
    public void 标定类型不符_校验运行预热都给出文件名与实际期望类型()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string affineFile = PathOf("affine.vfcal.json");
        CalibrationService.Save(affineFile, CalibrationService.FromAffine(new Affine2DCalibration { HomMat2D = TrueAffine() }, "mm", null));
        string cameraFile = PathOf("camera.vfcal.json");
        CalibrationService.Save(cameraFile, new CalibrationResult { Kind = CalibrationFileKind.Camera, Unit = "mm", Camera = TestCamera() });
        string legacyFile = PathOf("legacy.tup");
        HOperatorSet.WriteTuple(new HTuple(TrueAffine()), legacyFile);

        void AssertMismatch(AffinePointTool tool, params string[] parts)
        {
            string issue = Assert.Single(ConfigIssues(tool), m => m.Contains("方式需要"));
            using FlowContext ctx = Points(new[] { 1.0 }, new[] { 2.0 });
            NodeResult run = tool.Run(ctx);
            Assert.False(run.IsSuccess);
            var root = new SequenceNode("根");
            root.Children.Add(new ToolNode(tool));
            string prepare = Assert.Single(FlowResources.Prepare(root).Issues).Message;
            foreach (string part in parts)
            {
                Assert.Contains(part, issue);
                Assert.Contains(part, run.Message);
                Assert.Contains(part, prepare);
            }
        }

        var camera = new AffinePointTool(Module) { RowPath = "点.Rows", ColumnPath = "点.Columns", CalibrationKind = CalibrationKind.Camera, CalibrationFile = affineFile };
        AssertMismatch(camera, affineFile, "类型为 Affine2D", "Camera 方式需要 Camera 数据");
        camera.CalibrationFile = legacyFile;
        AssertMismatch(camera, legacyFile, "旧版 write_tuple 矩阵文件", "Camera 方式需要");
        var affine = new AffinePointTool(Module) { RowPath = "点.Rows", ColumnPath = "点.Columns", CalibrationFile = cameraFile };
        AssertMismatch(affine, cameraFile, "类型为 Camera", "Affine2D 方式需要 Affine2D 数据");
        // 内嵌数据同样检查
        var embedded = new AffinePointTool(Module) { RowPath = "点.Rows", ColumnPath = "点.Columns", CalibrationKind = CalibrationKind.Camera };
        embedded.UseEmbeddedCalibration(File.ReadAllText(affineFile));
        AssertMismatch(embedded, "内嵌标定数据", "类型为 Affine2D", "Camera 方式需要");
        // Camera 方式不使用矩阵引用
        camera.CalibrationFile = cameraFile;
        camera.MatrixPath = "定位.HomMat";
        Assert.Contains(ConfigIssues(camera), m => m.Contains("Camera 方式使用标定结果中的相机参数，不使用变换矩阵引用"));
    }

    [Fact]
    public void 内嵌与文件互斥_切换清空另一侧_手工改出的冲突由校验与运行拦截()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string file = PathOf("affine.vfcal.json");
        CalibrationResult calibration = CalibrationService.FromAffine(new Affine2DCalibration { HomMat2D = TrueAffine() }, "mm", null);
        CalibrationService.Save(file, calibration);
        string json = CalibrationService.ToJson(calibration);

        var tool = new AffinePointTool(Module) { RowPath = "点.Rows", ColumnPath = "点.Columns" };
        tool.UseEmbeddedCalibration(json);
        Assert.Equal((CalibrationSource.Embedded, json, (string?)null), (tool.CalibrationSource, tool.CalibrationData, tool.CalibrationFile));
        Assert.False(tool.IsParameterVisible(nameof(AffinePointTool.CalibrationFile)));
        using FlowContext embeddedRun = Points(new[] { 10.0, 20 }, new[] { 30.0, 40 });
        Assert.True(tool.Run(embeddedRun).IsSuccess);

        tool.UseCalibrationFile(file);
        Assert.Equal((CalibrationSource.File, (string?)null, file), (tool.CalibrationSource, tool.CalibrationData, tool.CalibrationFile));
        Assert.True(tool.IsParameterVisible(nameof(AffinePointTool.CalibrationFile)));
        Assert.False(tool.IsParameterVisible(nameof(AffinePointTool.CalibrationData)));
        using FlowContext fileRun = Points(new[] { 10.0, 20 }, new[] { 30.0, 40 });
        Assert.True(tool.Run(fileRun).IsSuccess);
        Assert.Equal(Array(embeddedRun, "WorldRows"), Array(fileRun, "WorldRows"));
        Assert.Equal(Array(embeddedRun, "WorldColumns"), Array(fileRun, "WorldColumns"));

        // 手工改文件 / 侧栏只改来源造成的冲突
        tool.CalibrationData = json;
        Assert.Contains(ConfigIssues(tool), m => m.Contains("标定来源为文件，但仍保留内嵌标定数据"));
        using (FlowContext ctx = Points(new[] { 1.0 }, new[] { 2.0 }))
        {
            Assert.Contains("仍保留内嵌标定数据", tool.Run(ctx).Message);
        }
        tool.CalibrationSource = CalibrationSource.Embedded;
        Assert.Contains(ConfigIssues(tool), m => m.Contains("标定来源为内嵌，但仍配置了标定文件"));
        var empty = new AffinePointTool(Module) { RowPath = "点.Rows", ColumnPath = "点.Columns", CalibrationSource = CalibrationSource.Embedded };
        Assert.Contains(ConfigIssues(empty), m => m.Contains("标定来源为内嵌，但没有内嵌标定数据"));
        var corrupt = new AffinePointTool(Module) { RowPath = "点.Rows", ColumnPath = "点.Columns" };
        corrupt.UseEmbeddedCalibration("{ 坏数据");
        Assert.Contains(ConfigIssues(corrupt), m => m.Contains("内嵌标定数据 格式错误"));
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(corrupt));
        Assert.Contains("内嵌标定数据 格式错误", Assert.Single(FlowResources.Prepare(root).Issues).Message);
    }

    [Fact]
    public void 回归门禁_旧矩阵文件单值_引用矩阵优先_未配置与文件缺失提示不变_文件替换后重读()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string legacy = PathOf("legacy.tup");
        double[] matrix = { 0.05, 0.001, 10.0, -0.002, 0.051, 20.0 };
        HOperatorSet.WriteTuple(new HTuple(matrix), legacy);
        var tool = new AffinePointTool(Module) { RowPath = "点.Row", ColumnPath = "点.Column", CalibrationFile = legacy, OriginRow = 3, OriginColumn = 4 };
        Assert.DoesNotContain(ConfigIssues(tool), m => !m.Contains("引用"));
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("点", "Row", VariableType.Double, 123.5));
        ctx.SetVariable(Variable.Single("点", "Column", VariableType.Double, 456.25));
        Assert.True(tool.Run(ctx).IsSuccess);
        HOperatorSet.AffineTransPoint2d(new HTuple(matrix), 123.5, 456.25, out HTuple wr, out HTuple wc);
        Assert.Equal(wr.D - 3, Single(ctx, "WorldRow"));
        Assert.Equal(wc.D - 4, Single(ctx, "WorldColumn"));
        Assert.Equal(1, (int)ctx.GetVariable(Module, "Count").Value);
        Assert.Contains(ctx.Log, m => m.Contains("[坐标转换] (123.50,456.25) ->"));
        Assert.Equal(matrix, ((HomMat2D)ctx.GetVariable(Module, "Matrix").Value).ToArray());

        // 文件被替换（修改时间 / 大小变化）后重新读取
        double[] replaced = { 0.1, 0, 0, 0, 0.1, 0 };
        HOperatorSet.WriteTuple(new HTuple(replaced), legacy);
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddMinutes(1));
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(12.35 - 3, Single(ctx, "WorldRow"), 12);

        // 引用的矩阵优先于标定文件
        ctx.SetVariable(Variable.Object("定位", "HomMat", HomMat2D.Identity, 1));
        tool.MatrixPath = "定位.HomMat";
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(123.5 - 3, Single(ctx, "WorldRow"));

        var unconfigured = new AffinePointTool(Module) { RowPath = "点.Row", ColumnPath = "点.Column" };
        Assert.Equal("未配置变换矩阵引用或 CalibrationFile", unconfigured.Run(ctx).Message);
        unconfigured.CalibrationFile = legacy + ".missing";
        Assert.Equal($"标定文件不存在：{legacy}.missing", unconfigured.Run(ctx).Message);
        Assert.DoesNotContain(ConfigIssues(unconfigured), m => m.Contains("标定"));

        // 历史流程（没有新参数）按默认值加载：Affine2D、来源为文件
        string oldJson = FlowSerializer.SaveNode(new ToolNode(new AffinePointTool(Module) { RowPath = "点.Row", ColumnPath = "点.Column", CalibrationFile = legacy }));
        JsonObject node = JsonNode.Parse(oldJson)!.AsObject();
        JsonObject parameters = node["Tool"]!["Properties"]!.AsObject();
        foreach (string name in new[] { "CalibrationKind", "CalibrationSource", "CalibrationData", "AnglePath" })
        {
            parameters.Remove(name);
        }
        var loaded = (AffinePointTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(node.ToJsonString())).Tool;
        Assert.Equal((CalibrationKind.Affine2D, CalibrationSource.File, legacy), (loaded.CalibrationKind, loaded.CalibrationSource, loaded.CalibrationFile));
    }

    [Fact]
    public void 保存加载_新参数按数字保存_命名守卫_工具箱09标定()
    {
        var tool = new AffinePointTool(Module)
        {
            RowPath = "匹配1.Rows", ColumnPath = "匹配1.Columns", AnglePath = "匹配1.Angles",
            CalibrationKind = CalibrationKind.Camera, OriginRow = 1, OriginColumn = 2
        };
        tool.UseEmbeddedCalibration("{\"x\":1}");
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"CalibrationKind\": 1", json);
        Assert.Contains("\"CalibrationSource\": 1", json);
        var loaded = (AffinePointTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((tool.AnglePath, tool.CalibrationKind, tool.CalibrationSource, tool.CalibrationData, (string?)null),
            (loaded.AnglePath, loaded.CalibrationKind, loaded.CalibrationSource, loaded.CalibrationData, loaded.CalibrationFile));

        var properties = new HashSet<string>(typeof(AffinePointTool).GetProperties().Select(p => p.Name));
        IReadOnlyList<ToolOutputDef> outputs = ToolMetadata.GetOutputs(typeof(AffinePointTool));
        Assert.DoesNotContain(outputs, o => properties.Contains(o.Name));
        foreach (string name in new[] { "WorldRow", "WorldColumn", "WorldRows", "WorldColumns", "WorldAngle", "WorldAngles", "WorldAngleDeg", "Count", "Matrix" })
        {
            Assert.Contains(outputs, o => o.Name == name);
        }
        Assert.Contains(outputs, o => o.Name == "Matrix" && o.ElementClrType == typeof(HomMat2D));
        Assert.All(ToolMetadata.GetInputRefs(typeof(AffinePointTool)).Where(i => i.PropertyName != nameof(AffinePointTool.MatrixPath)), i => Assert.True(i.AcceptsCollection, i.PropertyName));

        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "affine-point");
        Assert.Equal(("09 标定", "图像坐标转世界坐标"), (item.Category, item.DisplayName));
        ToolNode created = Assert.IsType<ToolNode>(item.Factory());
        Assert.IsType<AffinePointTool>(created.Tool);
        Assert.Contains("\"ToolId\": \"affine-point\"", FlowSerializer.SaveNode(created));
    }

    private sealed class DoubleTolerance : IEqualityComparer<double>
    {
        private readonly double _tolerance;

        public DoubleTolerance(double tolerance)
        {
            _tolerance = tolerance;
        }

        public bool Equals(double x, double y) => Math.Abs(x - y) <= _tolerance;
        public int GetHashCode(double obj) => 0;
    }
}
