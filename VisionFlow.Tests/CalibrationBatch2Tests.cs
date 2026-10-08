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

/// <summary>
/// CALIBRATION-TOOLS-PLAN 第二批：CB-03（旋转中心标定，纯 C#）与 CB-07（纠偏计算）。
/// CB-07 用例按计划第 6 节“符号约定”反推真值：用例即约定的可执行文档。
/// </summary>
public class CalibrationBatch2Tests : IDisposable
{
    private const string Module = "纠偏1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vf-calib2-" + Guid.NewGuid().ToString("N"));

    public CalibrationBatch2Tests()
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

    // ======================= CB-03 旋转中心 =======================

    /// <summary>真值圆心 (240.5, 320.25)、半径 150；特征点从方向 β0 起按图像角度约定（屏幕上逆时针）转 angles 度，加 ±noise 像素确定性噪声。</summary>
    private static List<RotationCenterPoint> RotationPoints(double[] angleDegrees, double noise, bool withAngles, int seed = 7)
    {
        const double centerRow = 240.5, centerColumn = 320.25, radius = 150, beta0 = 0.4;
        var random = new Random(seed);
        double startRow = centerRow - radius * Math.Sin(beta0);
        double startColumn = centerColumn + radius * Math.Cos(beta0);
        return angleDegrees.Select(a =>
        {
            CalibrationService.RotateAbout(centerRow, centerColumn, a * Math.PI / 180, startRow, startColumn, out double r, out double c);
            return new RotationCenterPoint
            {
                Row = r + (random.NextDouble() * 2 - 1) * noise,
                Column = c + (random.NextDouble() * 2 - 1) * noise,
                Angle = withAngles ? a * Math.PI / 180 : (double?)null
            };
        }).ToList();
    }

    private static double CenterError(RotationCenterSolution s) =>
        Math.Sqrt(Math.Pow(s.Calibration.Row - 240.5, 2) + Math.Pow(s.Calibration.Column - 320.25, 2));

    [Fact]
    public void 旋转中心_有角度路径_加噪声圆心误差小于02像素_无噪声精确()
    {
        double[] angles = { 0, 10, 20, 30, 40, 50, 60 };
        RotationCenterSolution noisy = CalibrationService.SolveRotationCenter(RotationPoints(angles, 0.2, true));
        Assert.True(noisy.UsedAngles);
        Assert.True(CenterError(noisy) < 0.2, $"圆心误差 {CenterError(noisy):F4}");
        Assert.InRange(noisy.Calibration.Radius, 149.7, 150.3);
        Assert.Equal(60, noisy.CoverageDegrees, 9);
        Assert.Empty(noisy.Warnings);
        Assert.Equal(noisy.Calibration.Points.Max(p => p.Residual.GetValueOrDefault()), noisy.Calibration.MaxError.GetValueOrDefault(), 12);
        Assert.InRange(noisy.Calibration.RmsError.GetValueOrDefault(), 1e-6, 0.3);

        RotationCenterSolution exact = CalibrationService.SolveRotationCenter(RotationPoints(angles, 0, true));
        Assert.True(CenterError(exact) < 1e-9);
        Assert.True(exact.Calibration.MaxError < 1e-9);
    }

    [Fact]
    public void 旋转中心_无角度路径_Kasa圆拟合_加噪声圆心误差小于02像素()
    {
        double[] angles = Enumerable.Range(0, 10).Select(i => i * 20.0).ToArray();
        RotationCenterSolution noisy = CalibrationService.SolveRotationCenter(RotationPoints(angles, 0.2, false));
        Assert.False(noisy.UsedAngles);
        Assert.True(CenterError(noisy) < 0.2, $"圆心误差 {CenterError(noisy):F4}");
        Assert.InRange(noisy.Calibration.Radius, 149.7, 150.3);
        // 覆盖弧度按拟合出的圆心计算，加噪声后接近 180°
        Assert.InRange(noisy.CoverageDegrees, 179, 181);

        RotationCenterSolution exact = CalibrationService.SolveRotationCenter(RotationPoints(new[] { 0.0, 30, 70 }, 0, false));
        Assert.True(CenterError(exact) < 1e-7);
        Assert.Equal(150, exact.Calibration.Radius, 7);
    }

    [Fact]
    public void 旋转中心_角度约定与HALCON旋转一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        // 用 hom_mat2d_rotate（屏幕上逆时针为正，第 13 节实测）生成旋转点，按同一角度求解应恢复真值圆心
        var points = new List<RotationCenterPoint>();
        foreach (double deg in new[] { -20.0, 5, 25, 45 })
        {
            HOperatorSet.HomMat2dIdentity(out HTuple h);
            HOperatorSet.HomMat2dRotate(h, deg * Math.PI / 180, 100.0, 200.0, out h);
            HOperatorSet.AffineTransPoint2d(h, 30.0, 260.0, out HTuple r, out HTuple c);
            points.Add(new RotationCenterPoint { Row = r.D, Column = c.D, Angle = deg * Math.PI / 180 });
        }
        RotationCenterSolution solution = CalibrationService.SolveRotationCenter(points);
        Assert.Equal(100.0, solution.Calibration.Row, 9);
        Assert.Equal(200.0, solution.Calibration.Column, 9);
        Assert.DoesNotContain(solution.Warnings, w => w.Contains("相反"));
    }

    [Fact]
    public void 旋转中心_提示不阻止求解_覆盖过小_部分无角度_角度方向相反()
    {
        RotationCenterSolution small = CalibrationService.SolveRotationCenter(RotationPoints(new[] { 0.0, 10, 20 }, 0, true));
        Assert.Contains(small.Warnings, w => w.Contains("角度覆盖只有 20.0°，建议覆盖 30° 以上"));

        List<RotationCenterPoint> mixed = RotationPoints(new[] { 0.0, 20, 40, 60 }, 0, true);
        mixed[3].Angle = null;
        RotationCenterSolution partial = CalibrationService.SolveRotationCenter(mixed);
        Assert.True(partial.UsedAngles);
        Assert.Contains(partial.Warnings, w => w.Contains("1 个点没有角度，未参与求圆心"));
        Assert.True(CenterError(partial) < 1e-9);
        Assert.Equal(4, partial.Calibration.Points.Count);

        List<RotationCenterPoint> flipped = RotationPoints(new[] { 0.0, 20, 40, 60 }, 0, true);
        flipped.ForEach(p => p.Angle = -p.Angle);
        Assert.Contains(CalibrationService.SolveRotationCenter(flipped).Warnings, w => w.Contains("角度方向可能与图像约定（屏幕上逆时针为正）相反"));

        List<RotationCenterPoint> oneAngle = RotationPoints(new[] { 0.0, 60, 120 }, 0, false);
        oneAngle[0].Angle = 0;
        RotationCenterSolution circle = CalibrationService.SolveRotationCenter(oneAngle);
        Assert.False(circle.UsedAngles);
        Assert.Contains(circle.Warnings, w => w.Contains("只有 1 个点带角度，按无角度的圆拟合求解"));
    }

    public static IEnumerable<object[]> RotationDegenerateCases()
    {
        yield return new object[] { new (double, double, double?)[0], "没有采集点" };
        yield return new object[] { new (double, double, double?)[] { (0, 0, null), (10, 10, null) }, "至少需要 3 个点（当前 2 个），或至少 2 个带角度的点" };
        yield return new object[] { new (double, double, double?)[] { (0, 0, null), (double.NaN, 10, null), (5, 5, null) }, "第 2 个点含无效数值" };
        yield return new object[] { new (double, double, double?)[] { (0, 0, 0.1), (10, 10, 0.1), (20, 5, 0.1) }, "各点的角度全部相同" };
        yield return new object[] { new (double, double, double?)[] { (5, 5, 0.0), (5, 5, 0.5), (5, 5, 1.0) }, "带角度的点全部重合" };
        yield return new object[] { new (double, double, double?)[] { (0, 0, null), (10, 10, null), (20, 20, null) }, "旋转点共线" };
        yield return new object[] { new (double, double, double?)[] { (0, 0, null), (0, 0, null), (20, 20, null) }, "旋转点中不重复的点只有 2 个" };
    }

    [Theory]
    [MemberData(nameof(RotationDegenerateCases))]
    public void 旋转中心_退化输入给出中文提示(IEnumerable<(double, double, double?)> raw, string expected)
    {
        List<RotationCenterPoint> points = raw.Select(p => new RotationCenterPoint { Row = p.Item1, Column = p.Item2, Angle = p.Item3 }).ToList();
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => CalibrationService.SolveRotationCenter(points)).Message);
    }

    [Fact]
    public void 旋转中心段_保存读取往返一致_非法载荷明确报错()
    {
        RotationCenterSolution solution = CalibrationService.SolveRotationCenter(RotationPoints(new[] { 0.0, 15, 30, 45 }, 0.1, true));
        CalibrationResult result = CalibrationService.MergeRotationCenter(null, solution.Calibration, "mm", "吸嘴 1");
        Assert.Equal(CalibrationFileKind.RotationCenter, result.Kind);
        Assert.Null(result.Affine2D);
        Assert.Null(result.RotationCenter.X);
        string file = PathOf("rotation.vfcal.json");
        CalibrationService.Save(file, result);
        CalibrationResult loaded = CalibrationService.Load(file);
        RotationCenterCalibration a = result.RotationCenter, b = loaded.RotationCenter;
        Assert.Equal((a.Row, a.Column, a.Radius, a.RmsError, a.MaxError), (b.Row, b.Column, b.Radius, b.RmsError, b.MaxError));
        Assert.Equal(a.Points.Select(p => (p.Row, p.Column, p.Angle, p.Residual)), b.Points.Select(p => (p.Row, p.Column, p.Angle, p.Residual)));
        Assert.Equal(CalibrationService.ToJson(result), CalibrationService.ToJson(loaded));

        string bad = CalibrationService.ToJson(result).Replace("\"Radius\": " + a.Radius.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "\"Radius\": -1");
        Assert.Contains("RotationCenter.Radius 应为不小于 0 的有限数值", Assert.Throws<InvalidDataException>(() => CalibrationService.Parse(bad, "内嵌标定数据")).Message);
        string badPoint = "{\"FormatVersion\":1,\"Kind\":\"RotationCenter\",\"RotationCenter\":{\"Row\":1,\"Column\":2,\"Radius\":3,\"Points\":[{\"Row\":1,\"Column\":2},{\"Row\":\"NaN\",\"Column\":2}]}}";
        Assert.Throws<InvalidDataException>(() => CalibrationService.Parse(badPoint, "内嵌标定数据"));
    }

    [Fact]
    public void 合并保存_带Affine2D时同一文件两段可读_坐标转换与纠偏都能用()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        Affine2DCalibration affine = new() { HomMat2D = NormalMatrix(), TransformType = CalibrationTransformType.affine };
        CalibrationResult existing = CalibrationService.FromAffine(affine, "mm", "工位 1");
        RotationCenterSolution solution = CalibrationService.SolveRotationCenter(RotationPoints(new[] { 0.0, 20, 40 }, 0, true));
        CalibrationResult merged = CalibrationService.MergeRotationCenter(existing, solution.Calibration, null, null);
        Assert.Equal(CalibrationFileKind.Affine2D, merged.Kind);
        Assert.Same(affine, merged.Affine2D);
        Assert.Equal(("mm", "工位 1"), (merged.Unit, merged.Description));
        CalibrationService.TransformPoint(affine.HomMat2D, merged.RotationCenter.Row, merged.RotationCenter.Column, out double x, out double y);
        Assert.Equal(((double?)x, (double?)y), (merged.RotationCenter.X, merged.RotationCenter.Y));

        string file = PathOf("merged.vfcal.json");
        CalibrationService.Save(file, merged);
        CalibrationResult loaded = CalibrationService.Load(file);
        Assert.Equal("Affine2D + RotationCenter", loaded.SectionsText());

        var coordinate = new AffinePointTool("坐标1") { RowPath = "当前.Row", ColumnPath = "当前.Column", CalibrationFile = file };
        Assert.DoesNotContain(ConfigIssues(coordinate), m => m.Contains("标定"));
        AlignmentOffsetTool alignment = Alignment(file, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed);
        Assert.DoesNotContain(ConfigIssues(alignment), m => m.Contains("标定") || m.Contains("需要"));
        using FlowContext ctx = Pose(100, 200, 0.1);
        Assert.True(coordinate.Run(ctx).IsSuccess);
        Assert.True(alignment.Run(ctx).IsSuccess);
        FlowResources.Release(coordinate);
        FlowResources.Release(alignment);
    }

    // ======================= CB-07 纠偏计算（符号约定反推真值） =======================

    /// <summary>一般仿射：比例 0.05 / 0.052、剪切、旋转 3°、平移 (10, −20)（行列式为正）。</summary>
    private static double[] NormalMatrix()
    {
        double s = Math.Sin(3 * Math.PI / 180), c = Math.Cos(3 * Math.PI / 180);
        return new[] { 0.05 * c, -0.052 * s + 0.001, 10.0, 0.05 * s, 0.052 * c, -20.0 };
    }

    /// <summary>镜像矩阵：列方向取反再旋转（行列式为负）。</summary>
    private static double[] MirrorMatrix()
    {
        double s = Math.Sin(0.2), c = Math.Cos(0.2);
        return new[] { 0.04 * c, 0.04 * s, 5.0, 0.04 * s, -0.04 * c, 7.0 };
    }

    /// <summary>物理 → 图像（仿射逆变换，纯计算）。</summary>
    private static void ToImage(double[] m, double x, double y, out double row, out double column)
    {
        double det = m[0] * m[4] - m[1] * m[3];
        double dx = x - m[2], dy = y - m[5];
        row = (m[4] * dx - m[1] * dy) / det;
        column = (-m[3] * dx + m[0] * dy) / det;
    }

    /// <summary>物理角 θ（WorldAngle 约定：方向向量 (ΔX, ΔY) = (−sin θ, cos θ)）→ 图像角 φ（方向 (Δ行, Δ列) = (−sin φ, cos φ)）。</summary>
    private static double ToImageAngle(double[] m, double theta)
    {
        double det = m[0] * m[4] - m[1] * m[3];
        double dx = -Math.Sin(theta), dy = Math.Cos(theta);
        double dr = (m[4] * dx - m[1] * dy) / det;
        double dc = (-m[3] * dx + m[0] * dy) / det;
        return Math.Atan2(-dr, dc);
    }

    private string WriteCalibration(double[] matrix, double cx, double cy, bool withCenter = true, string name = "station.vfcal.json")
    {
        CalibrationResult result = CalibrationService.FromAffine(new Affine2DCalibration { HomMat2D = matrix }, "mm", null);
        if (withCenter)
        {
            ToImage(matrix, cx, cy, out double row, out double column);
            result.RotationCenter = new RotationCenterCalibration { Row = row, Column = column, X = cx, Y = cy, Radius = 10 };
        }
        string file = PathOf(name);
        CalibrationService.Save(file, result);
        return file;
    }

    private static AlignmentOffsetTool Alignment(string? file, AlignmentMode mode, CameraMounting mounting) => new(Module)
    {
        RowPath = "当前.Row", ColumnPath = "当前.Column", AnglePath = "当前.Angle",
        CalibrationFile = file, Mode = mode, CameraMounting = mounting
    };

    private static FlowContext Pose(double row, double column, double angle)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("当前", "Row", VariableType.Double, row));
        ctx.SetVariable(Variable.Single("当前", "Column", VariableType.Double, column));
        ctx.SetVariable(Variable.Single("当前", "Angle", VariableType.Double, angle));
        return ctx;
    }

    private static double Out(FlowContext ctx, string name) => Convert.ToDouble(ctx.GetVariable(Module, name).Value);

    private sealed record Case(double[] Matrix, double Cx, double Cy, double BaseRow, double BaseColumn, double BaseAngle, double DeltaX, double DeltaY, double DTheta);

    /// <summary>
    /// 按计划第 6 节反推当前位姿：B / θb 由基准图像位姿经标定得到；期望补偿 (Δ, dθ) 给定后，
    /// Fixed + RotateAroundCenter：P = R(C, dθ)·(B − Δ)；Fixed + TranslationOnly：P = B − Δ；OnAxis + TranslationOnly：P = B + Δ；θ = θb + dθ；
    /// 再经标定逆变换得到当前图像位姿。返回工具运行后的上下文与当前物理坐标。
    /// </summary>
    private (FlowContext Ctx, double Px, double Py) RunCase(Case c, AlignmentMode mode, CameraMounting mounting, string file)
    {
        CalibrationService.TransformPose(c.Matrix, c.BaseRow, c.BaseColumn, c.BaseAngle, out double bx, out double by, out double thetaBase);
        double px, py;
        if (mode == AlignmentMode.RotateAroundCenter)
        {
            CalibrationService.RotateAbout(c.Cx, c.Cy, c.DTheta, bx - c.DeltaX, by - c.DeltaY, out px, out py);
        }
        else if (mounting == CameraMounting.Fixed)
        {
            px = bx - c.DeltaX;
            py = by - c.DeltaY;
        }
        else
        {
            px = bx + c.DeltaX;
            py = by + c.DeltaY;
        }
        ToImage(c.Matrix, px, py, out double row, out double column);
        double angle = ToImageAngle(c.Matrix, thetaBase + c.DTheta);
        AlignmentOffsetTool tool = Alignment(file, mode, mounting);
        tool.BaseRow = c.BaseRow;
        tool.BaseColumn = c.BaseColumn;
        tool.BaseAngle = c.BaseAngle;
        FlowContext ctx = Pose(row, column, angle);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return (ctx, px, py);
    }

    public static IEnumerable<object[]> FixedRotateCases()
    {
        yield return new object[] { "一般仿射", false };
        yield return new object[] { "镜像标定", true };
    }

    [Theory]
    [MemberData(nameof(FixedRotateCases))]
    public void 纠偏_Fixed_RotateAroundCenter_按约定反推真值(string name, bool mirror)
    {
        double[] m = mirror ? MirrorMatrix() : NormalMatrix();
        var c = new Case(m, 12.5, -4.0, 210.0, 330.0, 0.35, 1.25, -0.75, 0.12);
        string file = WriteCalibration(m, c.Cx, c.Cy, name: name + ".vfcal.json");
        (FlowContext ctx, double px, double py) = RunCase(c, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed, file);
        using (ctx)
        {
            Assert.Equal(1.25, Out(ctx, "DeltaX"), 9);
            Assert.Equal(-0.75, Out(ctx, "DeltaY"), 9);
            Assert.Equal(-0.12, Out(ctx, "DeltaAngle"), 12);
            Assert.Equal(0.12, Out(ctx, "AngleDifference"), 12);
            Assert.Equal(px, Out(ctx, "WorldX"), 9);
            Assert.Equal(py, Out(ctx, "WorldY"), 9);
            Assert.True((bool)ctx.GetVariable(Module, "Valid").Value);
        }
    }

    [Fact]
    public void 纠偏_镜像标定下物理系角度差与图像角度差方向相反_按物理系计算()
    {
        double[] m = MirrorMatrix();
        Assert.True(m[0] * m[4] - m[1] * m[3] < 0);
        var c = new Case(m, 12.5, -4.0, 210.0, 330.0, 0.35, 0, 0, 0.12);
        string file = WriteCalibration(m, c.Cx, c.Cy);
        (FlowContext ctx, _, _) = RunCase(c, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed, file);
        using (ctx)
        {
            double imageDifference = CalibrationService.WrapAngle(Convert.ToDouble(ctx.GetVariable("当前", "Angle").Value) - c.BaseAngle);
            // 镜像时图像中的转角与物理转角符号相反：直接用图像角相减会得到错误的方向
            Assert.True(imageDifference < 0);
            Assert.Equal(0.12, Out(ctx, "AngleDifference"), 12);
            Assert.Equal(-0.12, Out(ctx, "DeltaAngle"), 12);
        }
    }

    [Fact]
    public void 纠偏_Fixed_TranslationOnly_DeltaAngle为0_AngleDifference仍为dθ()
    {
        double[] m = NormalMatrix();
        var c = new Case(m, 0, 0, 150.0, 250.0, -0.2, -2.5, 3.0, 0.08);
        string file = WriteCalibration(m, 0, 0, withCenter: false);
        (FlowContext ctx, double px, double py) = RunCase(c, AlignmentMode.TranslationOnly, CameraMounting.Fixed, file);
        using (ctx)
        {
            Assert.Equal(-2.5, Out(ctx, "DeltaX"), 9);
            Assert.Equal(3.0, Out(ctx, "DeltaY"), 9);
            Assert.Equal(0.0, Out(ctx, "DeltaAngle"));
            Assert.Equal(0.08, Out(ctx, "AngleDifference"), 12);
            Assert.Equal((px, py), (Out(ctx, "WorldX"), Out(ctx, "WorldY")), new PairTolerance(1e-9));
        }
    }

    [Fact]
    public void 纠偏_OnAxis_TranslationOnly_与Fixed互为相反数()
    {
        double[] m = MirrorMatrix();
        var c = new Case(m, 0, 0, 150.0, 250.0, 0.4, 0.6, -1.1, -0.05);
        string file = WriteCalibration(m, 0, 0, withCenter: false);
        (FlowContext onAxis, _, _) = RunCase(c, AlignmentMode.TranslationOnly, CameraMounting.OnAxis, file);
        using (onAxis)
        {
            Assert.Equal(0.6, Out(onAxis, "DeltaX"), 9);
            Assert.Equal(-1.1, Out(onAxis, "DeltaY"), 9);
            Assert.Equal(0.0, Out(onAxis, "DeltaAngle"));
            Assert.Equal(-0.05, Out(onAxis, "AngleDifference"), 12);
            // 同一组输入换成 Fixed：补偿互为相反数
            var fixedTool = Alignment(file, AlignmentMode.TranslationOnly, CameraMounting.Fixed);
            fixedTool.BaseRow = c.BaseRow;
            fixedTool.BaseColumn = c.BaseColumn;
            fixedTool.BaseAngle = c.BaseAngle;
            using FlowContext fixedCtx = Pose(Convert.ToDouble(onAxis.GetVariable("当前", "Row").Value), Convert.ToDouble(onAxis.GetVariable("当前", "Column").Value),
                Convert.ToDouble(onAxis.GetVariable("当前", "Angle").Value));
            Assert.True(fixedTool.Run(fixedCtx).IsSuccess);
            Assert.Equal(-Out(onAxis, "DeltaX"), Out(fixedCtx, "DeltaX"), 12);
            Assert.Equal(-Out(onAxis, "DeltaY"), Out(fixedCtx, "DeltaY"), 12);
        }
    }

    [Fact]
    public void 纠偏_OnAxis_RotateAroundCenter_校验与运行都拒绝()
    {
        string file = WriteCalibration(NormalMatrix(), 1, 2);
        AlignmentOffsetTool tool = Alignment(file, AlignmentMode.RotateAroundCenter, CameraMounting.OnAxis);
        Assert.Contains(ConfigIssues(tool), m => m.Contains(AlignmentOffsetTool.OnAxisRotationMessage));
        Assert.Equal("相机随轴旋转后平移补偿取决于轴叠放方式，第二批不支持，请改用 TranslationOnly 或由上层按机构计算", AlignmentOffsetTool.OnAxisRotationMessage);
        using FlowContext ctx = Pose(100, 200, 0.1);
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains(AlignmentOffsetTool.OnAxisRotationMessage, result.Message);
    }

    [Fact]
    public void 纠偏_角度单位为度时DeltaAngle与AngleDifference换算()
    {
        double[] m = NormalMatrix();
        var c = new Case(m, 3, 4, 200.0, 300.0, 0.0, 0.5, 0.5, 0.2);
        string file = WriteCalibration(m, c.Cx, c.Cy);
        CalibrationService.TransformPose(m, c.BaseRow, c.BaseColumn, c.BaseAngle, out double bx, out double by, out double thetaBase);
        CalibrationService.RotateAbout(c.Cx, c.Cy, c.DTheta, bx - c.DeltaX, by - c.DeltaY, out double px, out double py);
        ToImage(m, px, py, out double row, out double column);
        AlignmentOffsetTool tool = Alignment(file, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed);
        (tool.BaseRow, tool.BaseColumn, tool.BaseAngle, tool.AngleUnit) = (c.BaseRow, c.BaseColumn, c.BaseAngle, AngleUnit.Degree);
        using FlowContext ctx = Pose(row, column, ToImageAngle(m, thetaBase + c.DTheta));
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(-AngleMath.ToDegrees(0.2), Out(ctx, "DeltaAngle"), 9);
        Assert.Equal(AngleMath.ToDegrees(0.2), Out(ctx, "AngleDifference"), 9);
        Assert.Equal(0.5, Out(ctx, "DeltaX"), 9);
    }

    [Fact]
    public void 纠偏_物理角与坐标转换CB05的WorldAngle同一换算()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        foreach (double[] m in new[] { NormalMatrix(), MirrorMatrix() })
        {
            var coordinate = new AffinePointTool("坐标1") { RowPath = "当前.Row", ColumnPath = "当前.Column", AnglePath = "当前.Angle" };
            coordinate.UseEmbeddedCalibration(CalibrationService.ToJson(CalibrationService.FromAffine(new Affine2DCalibration { HomMat2D = m }, "mm", null)));
            using FlowContext ctx = Pose(123.4, 456.7, -1.3);
            Assert.True(coordinate.Run(ctx).IsSuccess);
            CalibrationService.TransformPose(m, 123.4, 456.7, -1.3, out double x, out double y, out double angle);
            Assert.Equal(Convert.ToDouble(ctx.GetVariable("坐标1", "WorldRow").Value), x, 9);
            Assert.Equal(Convert.ToDouble(ctx.GetVariable("坐标1", "WorldColumn").Value), y, 9);
            Assert.Equal(Convert.ToDouble(ctx.GetVariable("坐标1", "WorldAngle").Value), angle, 9);
        }
    }

    [Theory]
    [InlineData("Row")]
    [InlineData("Angle")]
    public void 纠偏_当前位姿NaN_全部NaN且Valid为false_节点不失败(string nanInput)
    {
        string file = WriteCalibration(NormalMatrix(), 1, 2);
        AlignmentOffsetTool tool = Alignment(file, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed);
        using FlowContext ctx = Pose(nanInput == "Row" ? double.NaN : 100, 200, nanInput == "Angle" ? double.NaN : 0.1);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        foreach (string name in new[] { "DeltaX", "DeltaY", "DeltaAngle", "AngleDifference", "WorldX", "WorldY" })
        {
            Assert.True(double.IsNaN(Out(ctx, name)), name);
        }
        Assert.False((bool)ctx.GetVariable(Module, "Valid").Value);
        Assert.Contains(ctx.Log, m => m.Contains("当前位姿含 NaN"));
    }

    [Fact]
    public void 纠偏_缺段时校验运行预热措辞一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string rotationOnly = PathOf("rotation-only.vfcal.json");
        CalibrationService.Save(rotationOnly, CalibrationService.MergeRotationCenter(null,
            new RotationCenterCalibration { Row = 1, Column = 2, Radius = 3 }, "mm", null));
        string affineOnly = WriteCalibration(NormalMatrix(), 0, 0, withCenter: false, name: "affine-only.vfcal.json");
        string legacy = PathOf("legacy.tup");
        HOperatorSet.WriteTuple(new HTuple(NormalMatrix()), legacy);

        void AssertSame(AlignmentOffsetTool tool, params string[] parts)
        {
            string issue = Assert.Single(ConfigIssues(tool), m => m.Contains("需要"));
            using FlowContext ctx = Pose(100, 200, 0.1);
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

        AssertSame(Alignment(rotationOnly, AlignmentMode.TranslationOnly, CameraMounting.Fixed), rotationOnly, "类型为 RotationCenter（包含 RotationCenter），纠偏计算需要 Affine2D 数据");
        AssertSame(Alignment(affineOnly, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed), affineOnly, "类型为 Affine2D（包含 Affine2D），RotateAroundCenter 方式需要 Affine2D + RotationCenter 数据");
        AssertSame(Alignment(legacy, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed), legacy, "旧版 write_tuple 矩阵文件", "RotateAroundCenter 方式需要 Affine2D + RotationCenter 数据");
        var embedded = Alignment(null, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed);
        embedded.UseEmbeddedCalibration(File.ReadAllText(affineOnly));
        AssertSame(embedded, "内嵌标定数据", "RotateAroundCenter 方式需要 Affine2D + RotationCenter 数据");

        // TranslationOnly 只需要 Affine2D：旧版矩阵文件与 Affine2D 文件都可用
        foreach (string file in new[] { affineOnly, legacy })
        {
            AlignmentOffsetTool tool = Alignment(file, AlignmentMode.TranslationOnly, CameraMounting.Fixed);
            Assert.DoesNotContain(ConfigIssues(tool), m => m.Contains("需要"));
            using FlowContext ctx = Pose(100, 200, 0.1);
            Assert.True(tool.Run(ctx).IsSuccess);
        }
    }

    [Fact]
    public void 纠偏_其他校验_互斥_缺角度_文件缺失_数组输入()
    {
        string file = WriteCalibration(NormalMatrix(), 1, 2);
        AlignmentOffsetTool noAngle = Alignment(file, AlignmentMode.RotateAroundCenter, CameraMounting.Fixed);
        noAngle.AnglePath = null;
        Assert.Contains(ConfigIssues(noAngle), m => m.Contains("RotateAroundCenter 方式需要当前位姿的角度输入"));
        noAngle.Mode = AlignmentMode.TranslationOnly;
        Assert.DoesNotContain(ConfigIssues(noAngle), m => m.Contains("角度输入"));
        using (FlowContext ctx = Pose(100, 200, 0))
        {
            Assert.True(noAngle.Run(ctx).IsSuccess);
            Assert.True(double.IsNaN(Out(ctx, "AngleDifference")));
            Assert.Equal(0.0, Out(ctx, "DeltaAngle"));
            Assert.True((bool)ctx.GetVariable(Module, "Valid").Value);
        }

        AlignmentOffsetTool tool = Alignment(file, AlignmentMode.TranslationOnly, CameraMounting.Fixed);
        string json = File.ReadAllText(file);
        tool.UseEmbeddedCalibration(json);
        Assert.Equal((CalibrationSource.Embedded, (string?)null), (tool.CalibrationSource, tool.CalibrationFile));
        Assert.False(tool.IsParameterVisible(nameof(AlignmentOffsetTool.CalibrationFile)));
        tool.UseCalibrationFile(file);
        Assert.Equal((CalibrationSource.File, (string?)null), (tool.CalibrationSource, tool.CalibrationData));
        tool.CalibrationData = json;
        Assert.Contains(ConfigIssues(tool), m => m.Contains("标定来源为文件，但仍保留内嵌标定数据"));
        using (FlowContext ctx = Pose(100, 200, 0))
        {
            Assert.Contains("仍保留内嵌标定数据", tool.Run(ctx).Message);
        }

        AlignmentOffsetTool missing = Alignment(file + ".missing", AlignmentMode.TranslationOnly, CameraMounting.Fixed);
        Assert.DoesNotContain(ConfigIssues(missing), m => m.Contains("标定文件不存在"));
        using (FlowContext ctx = Pose(100, 200, 0))
        {
            Assert.Contains("标定文件不存在", missing.Run(ctx).Message);
        }
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(missing));
        Assert.Contains("标定文件不存在", Assert.Single(FlowResources.Prepare(root).Issues).Message);

        AlignmentOffsetTool arrayInput = Alignment(file, AlignmentMode.TranslationOnly, CameraMounting.Fixed);
        using var arrays = new FlowContext();
        arrays.SetVariable(Variable.Array("当前", "Row", VariableType.Double, new[] { 1.0, 2.0 }));
        arrays.SetVariable(Variable.Single("当前", "Column", VariableType.Double, 1.0));
        arrays.SetVariable(Variable.Single("当前", "Angle", VariableType.Double, 0.0));
        NodeResult failed = arrayInput.Run(arrays);
        Assert.False(failed.IsSuccess);
        Assert.Contains("需要单值", failed.Message);
    }

    [Fact]
    public void 纠偏_保存加载_默认值_命名守卫_工具箱09标定()
    {
        var defaults = new AlignmentOffsetTool(Module);
        Assert.Equal((AlignmentMode.RotateAroundCenter, CameraMounting.Fixed, AngleUnit.Radian, CalibrationSource.File),
            (defaults.Mode, defaults.CameraMounting, defaults.AngleUnit, defaults.CalibrationSource));

        var tool = new AlignmentOffsetTool(Module)
        {
            RowPath = "匹配1.Row", ColumnPath = "匹配1.Column", AnglePath = "匹配1.Angle",
            BaseRow = 1.5, BaseColumn = 2.5, BaseAngle = -0.25,
            Mode = AlignmentMode.TranslationOnly, CameraMounting = CameraMounting.OnAxis, AngleUnit = AngleUnit.Degree
        };
        tool.UseEmbeddedCalibration("{\"x\":1}");
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"alignment-offset\"", json);
        Assert.Contains("\"Mode\": 0", json);
        Assert.Contains("\"CameraMounting\": 1", json);
        Assert.Contains("\"AngleUnit\": 1", json);
        Assert.Contains("\"CalibrationSource\": 1", json);
        var loaded = (AlignmentOffsetTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((tool.RowPath, tool.ColumnPath, tool.AnglePath, tool.BaseRow, tool.BaseColumn, tool.BaseAngle, tool.Mode, tool.CameraMounting, tool.AngleUnit, tool.CalibrationData, (string?)null),
            (loaded.RowPath, loaded.ColumnPath, loaded.AnglePath, loaded.BaseRow, loaded.BaseColumn, loaded.BaseAngle, loaded.Mode, loaded.CameraMounting, loaded.AngleUnit, loaded.CalibrationData, loaded.CalibrationFile));

        var properties = new HashSet<string>(typeof(AlignmentOffsetTool).GetProperties().Select(p => p.Name));
        IReadOnlyList<ToolOutputDef> outputs = ToolMetadata.GetOutputs(typeof(AlignmentOffsetTool));
        Assert.DoesNotContain(outputs, o => properties.Contains(o.Name));
        Assert.Equal(new[] { "DeltaX", "DeltaY", "DeltaAngle", "AngleDifference", "WorldX", "WorldY", "Valid" }, outputs.Select(o => o.Name));
        Assert.Contains(outputs, o => o.Name == "Valid" && o.Type == VariableType.Bool);

        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "alignment-offset");
        Assert.Equal(("09 标定", "纠偏计算"), (item.Category, item.DisplayName));
        ToolNode created = Assert.IsType<ToolNode>(item.Factory());
        Assert.IsType<AlignmentOffsetTool>(created.Tool);
        Assert.Contains("\"ToolId\": \"alignment-offset\"", FlowSerializer.SaveNode(created));
    }

    private sealed class PairTolerance : IEqualityComparer<(double, double)>
    {
        private readonly double _tolerance;

        public PairTolerance(double tolerance)
        {
            _tolerance = tolerance;
        }

        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) <= _tolerance && Math.Abs(x.Item2 - y.Item2) <= _tolerance;
        public int GetHashCode((double, double) obj) => 0;
    }
}
