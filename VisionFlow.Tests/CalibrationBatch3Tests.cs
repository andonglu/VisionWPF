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
/// CALIBRATION-TOOLS-PLAN 第三批：CB-04（相机标定）与 CB-06（畸变校正）。
/// 标 [Trait("Requires", "HALCON")] 的为 HALCON 门禁用例：用 HALCONROOT / HALCONIMAGES 拼出示例图像与标定板描述文件的绝对路径，缺任一文件明确失败、不计通过；
/// 其余用例不调用 HALCON。
/// </summary>
public class CalibrationBatch3Tests : IDisposable
{
    private const string Module = "校正1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vf-calib3-" + Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public CalibrationBatch3Tests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
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

    // ======================= HALCON 示例数据（第 13 / 15 节） =======================

    private static string PlateDescription()
    {
        string? root = Environment.GetEnvironmentVariable("HALCONROOT");
        Assert.False(string.IsNullOrWhiteSpace(root), "缺少环境变量 HALCONROOT，HALCON 门禁用例不计通过");
        string plate = Path.Combine(root!, "calib", "calplate_80mm.cpd");
        Assert.True(File.Exists(plate), "缺少标定板描述文件：" + plate);
        return plate;
    }

    private static List<string> ExampleImagePaths()
    {
        string? images = Environment.GetEnvironmentVariable("HALCONIMAGES");
        Assert.False(string.IsNullOrWhiteSpace(images), "缺少环境变量 HALCONIMAGES，HALCON 门禁用例不计通过");
        List<string> paths = Enumerable.Range(1, 7).Select(i => Path.Combine(images!, "calib", $"calib_single_camera_{i:D2}.png")).ToList();
        foreach (string path in paths)
        {
            Assert.True(File.Exists(path), "缺少示例图像：" + path);
        }
        return paths;
    }

    private static List<HObject> ReadImages(IEnumerable<string> paths)
    {
        return paths.Select(p =>
        {
            HOperatorSet.ReadImage(out HObject image, p);
            return image;
        }).ToList();
    }

    private static HTuple ExampleStart() => CameraCalibrator.BuildStartParameters("area_scan_division", 0.008, 3.7e-6, 3.7e-6, 646, 482, 1292, 964);

    private static CameraCalibrationRun CalibrateExample(double thickness = 0, int reference = 0, List<HObject>? extra = null)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        List<string> paths = ExampleImagePaths();
        List<HObject> images = ReadImages(paths);
        var names = paths.Select(Path.GetFileName).Cast<string>().ToList();
        if (extra != null)
        {
            images.AddRange(extra);
            names.AddRange(extra.Select((_, i) => $"额外 {i + 1}"));
        }
        try
        {
            return CameraCalibrator.Calibrate(images, names, PlateDescription(), ExampleStart(), reference, thickness);
        }
        finally
        {
            images.ForEach(i => i.Dispose());
        }
    }

    private string SaveCamera(CameraCalibration camera, string unit = "mm", string name = "camera.vfcal.json")
    {
        string file = PathOf(name);
        CalibrationService.Save(file, CalibrationService.MergeCamera(null, camera, unit, null));
        return file;
    }

    // ======================= CB-04 相机标定（HALCON 门禁） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 相机标定_7张示例_反投影误差不超过01像素_逐张误差与参考位姿()
    {
        CameraCalibrationRun run = CalibrateExample();
        _output.WriteLine($"calibrate_cameras 误差 {run.Error:F4}，全部点 RMS {run.PointRmsError:F4}，逐张 " + string.Join(" / ", run.Images.Select(i => i.Error.GetValueOrDefault().ToString("F4"))));
        Assert.All(run.Images, i => Assert.True(i.Found, i.Name + " " + i.Message));
        Assert.InRange(run.Error, 0.0, 0.1);
        Assert.InRange(run.PointRmsError, 0.0, 0.1);
        Assert.All(run.Images, i => Assert.InRange(i.Error.GetValueOrDefault(double.NaN), 0.0, 0.2));
        Assert.All(run.Images, i => Assert.True(i.PointCount > 400));
        CameraCalibration camera = run.Camera;
        Assert.Equal("area_scan_division", camera.CameraType);
        Assert.Equal(new[] { "camera_type", "focus", "kappa", "sx", "sy", "cx", "cy", "image_width", "image_height" }, camera.CamParam.Select(p => p.Name));
        Assert.InRange(camera.GetValue("focus"), 0.0080, 0.0087);
        Assert.Equal(1292, camera.GetValue("image_width"));
        Assert.Equal((7, "calplate_80mm.cpd", 0.0, (double?)run.Error), (camera.ImageCount, camera.PlateDescription, camera.PlaneThickness, camera.RmsError));
        Assert.Equal(7, camera.Pose.Length);
        Assert.InRange(camera.Pose[2], 0.13, 0.15);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 相机标定_厚度把参考位姿沿标定板z轴移向远离相机一侧()
    {
        CameraCalibration flat = CalibrateExample().Camera;
        CameraCalibration thick = CalibrateExample(thickness: 0.002).Camera;
        Assert.Equal(0.002, thick.PlaneThickness);
        Assert.InRange(thick.Pose[2] - flat.Pose[2], 0.0019, 0.0021);
        CameraCalibration other = CalibrateExample(reference: 3).Camera;
        Assert.NotEqual(flat.Pose[2], other.Pose[2]);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 相机标定_找不到标定板与尺寸不符的图像标出_其余照常标定_参考图像未找到明确报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject blank, "byte", 1292, 964);
        HOperatorSet.GenImageConst(out HObject small, "byte", 640, 480);
        CameraCalibrationRun run = CalibrateExample(extra: new List<HObject> { blank, small });
        Assert.Equal(9, run.Images.Count);
        Assert.Equal(7, run.Camera.ImageCount);
        Assert.False(run.Images[7].Found);
        Assert.Equal("未找到标定板（标记分割失败）", run.Images[7].Message);
        Assert.False(run.Images[8].Found);
        Assert.Equal("图像尺寸 640×480 与初始参数 1292×964 不一致", run.Images[8].Message);
        Assert.InRange(run.Error, 0.0, 0.1);

        HOperatorSet.GenImageConst(out HObject blank2, "byte", 1292, 964);
        var ex = Assert.Throws<InvalidOperationException>(() => CalibrateExample(reference: 7, extra: new List<HObject> { blank2 }));
        Assert.Contains("参考图像（第 8 张）没有找到标定板", ex.Message);
        Assert.Contains("没有标定图像", Assert.Throws<InvalidOperationException>(() => CameraCalibrator.Calibrate(new List<HObject>(), null, PlateDescription(), ExampleStart(), 0, 0)).Message);
        HOperatorSet.GenImageConst(out HObject only, "byte", 1292, 964);
        Assert.Contains("没有图像找到标定板", Assert.Throws<InvalidOperationException>(() => CameraCalibrator.Calibrate(new List<HObject> { only }, null, PlateDescription(), ExampleStart(), 0, 0)).Message);
        only.Dispose();
        Assert.Throws<FileNotFoundException>(() => CameraCalibrator.Calibrate(new List<HObject> { blank }, null, PathOf("missing.cpd"), ExampleStart(), 0, 0));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 相机标定_初始参数按名称表手拼_只支持两种面阵模型()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HTuple division = ExampleStart();
        Assert.Equal(9, division.Length);
        Assert.Equal("area_scan_division", division[0].S);
        Assert.Equal(new[] { 0.008, 0.0, 3.7e-6, 3.7e-6, 646.0, 482.0 }, Enumerable.Range(1, 6).Select(i => division[i].D));
        Assert.Equal((HTupleType.INTEGER, 1292, HTupleType.INTEGER, 964), (division[7].Type, division[7].I, division[8].Type, division[8].I));
        HTuple polynomial = CameraCalibrator.BuildStartParameters("area_scan_polynomial", 0.012, 4.4e-6, 4.4e-6, 640, 480, 1280, 960);
        Assert.Equal(13, polynomial.Length);
        Assert.Equal(new[] { 0.0, 0, 0, 0, 0 }, Enumerable.Range(2, 5).Select(i => polynomial[i].D));
        Assert.Contains("暂不支持的相机模型：area_scan_telecentric_division", Assert.Throws<NotSupportedException>(() =>
            CameraCalibrator.BuildStartParameters("area_scan_telecentric_division", 0.01, 1e-6, 1e-6, 1, 1, 2, 2)).Message);
        Assert.Throws<ArgumentException>(() => CameraCalibrator.BuildStartParameters("area_scan_division", 0, 1e-6, 1e-6, 1, 1, 2, 2));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 相机标定结果_与已有Affine2D和旋转中心共存_坐标转换Camera方式可用()
    {
        CameraCalibration camera = CalibrateExample().Camera;
        CalibrationResult existing = CalibrationService.FromAffine(new Affine2DCalibration { HomMat2D = new[] { 0.05, 0, 1, 0, 0.05, 2 } }, "mm", "工位 3");
        existing = CalibrationService.MergeRotationCenter(existing, new RotationCenterCalibration { Row = 10, Column = 20, Radius = 5 }, null, null);
        CalibrationResult merged = CalibrationService.MergeCamera(existing, camera, "m", null);
        Assert.Equal((CalibrationFileKind.Affine2D, "mm", "Affine2D + RotationCenter + Camera"), (merged.Kind, merged.Unit, merged.SectionsText()));
        string file = PathOf("all.vfcal.json");
        CalibrationService.Save(file, merged);
        CalibrationResult loaded = CalibrationService.Load(file);
        Assert.Equal(camera.Pose, loaded.Camera.Pose);
        // 旋转中心重新保存时保留相机段
        CalibrationResult again = CalibrationService.MergeRotationCenter(loaded, new RotationCenterCalibration { Row = 11, Column = 21, Radius = 6 }, null, null);
        Assert.Same(loaded.Camera, again.Camera);

        var coordinate = new AffinePointTool("坐标1") { RowPath = "点.Row", ColumnPath = "点.Column", CalibrationKind = CalibrationKind.Camera, CalibrationFile = file };
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("点", "Row", VariableType.Double, 482.0));
        ctx.SetVariable(Variable.Single("点", "Column", VariableType.Double, 646.0));
        Assert.True(coordinate.Run(ctx).IsSuccess);
        HOperatorSet.ImagePointsToWorldPlane(camera.ToHalconCamParam(), camera.ToHalconPose(), 482.0, 646.0, "mm", out HTuple x, out HTuple y);
        Assert.Equal(x.D, Convert.ToDouble(ctx.GetVariable("坐标1", "WorldRow").Value));
        FlowResources.Release(coordinate);
    }

    // ======================= CB-06 畸变校正（HALCON 门禁） =======================

    private static FlowContext ImageContext(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        return ctx;
    }

    /// <summary>标定板标记的真实坐标（米，标定板坐标系）。</summary>
    private static (double[] X, double[] Y) TrueMarks()
    {
        HOperatorSet.CreateCalibData("calibration_object", 1, 1, out HTuple calib);
        try
        {
            HOperatorSet.SetCalibDataCalibObject(calib, 0, PlateDescription());
            HOperatorSet.GetCalibData(calib, "calib_obj", 0, "x", out HTuple x);
            HOperatorSet.GetCalibData(calib, "calib_obj", 0, "y", out HTuple y);
            return (x.ToDArr(), y.ToDArr());
        }
        finally
        {
            HOperatorSet.ClearCalibData(calib);
        }
    }

    /// <summary>校正图上的亮色标记（第 15 节：binary_threshold max_separability light → 圆形筛选 → 膨胀后灰度加权重心）。</summary>
    private static (double[] Rows, double[] Columns) DetectMarks(HObject rectified)
    {
        HOperatorSet.BinaryThreshold(rectified, out HObject light, "max_separability", "light", out _);
        HOperatorSet.Connection(light, out HObject parts);
        HOperatorSet.SelectShape(parts, out HObject marks, new HTuple("area", "circularity"), "and", new HTuple(30, 0.7), new HTuple(5000, 1.0));
        HOperatorSet.DilationCircle(marks, out HObject grown, 3.5);
        HOperatorSet.AreaCenterGray(grown, rectified, out _, out HTuple rows, out HTuple columns);
        foreach (HObject o in new[] { light, parts, marks, grown })
        {
            o.Dispose();
        }
        return (rows.ToDArr(), columns.ToDArr());
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 畸变校正_标定板相邻标记平均间距误差小于05百分比_Matrix把校正像素换算到标定板坐标()
    {
        CameraCalibration camera = CalibrateExample().Camera;
        string file = SaveCamera(camera);
        var tool = new ImageRectifyTool(Module) { CalibrationFile = file };
        HOperatorSet.ReadImage(out HObject image, ExampleImagePaths()[0]);
        using FlowContext ctx = ImageContext(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        HObject rectified = ((HalconImage)ctx.GetVariable(Module, "Image").Value).Object;
        double pixelSize = (double)ctx.GetVariable(Module, "ActualPixelSize").Value;
        double[] matrix = ((HomMat2D)ctx.GetVariable(Module, "Matrix").Value).ToArray();
        Assert.InRange(pixelSize, 0.0612, 0.0620);
        Assert.Equal(new[] { 0.0, pixelSize, pixelSize, 0.0 }, new[] { matrix[0], matrix[1], matrix[3], matrix[4] });

        (double[] rows, double[] columns) = DetectMarks(rectified);
        (double[] trueX, double[] trueY) = TrueMarks();
        Assert.True(rows.Length >= 800, $"只找到 {rows.Length} 个标记");
        double trueSpacing = Enumerable.Range(0, trueX.Length).Average(i => Enumerable.Range(0, trueX.Length).Where(j => j != i)
            .Min(j => Math.Sqrt(Math.Pow(trueX[i] - trueX[j], 2) + Math.Pow(trueY[i] - trueY[j], 2)))) * 1000;
        double[] spacing = Enumerable.Range(0, rows.Length).Select(i => Enumerable.Range(0, rows.Length).Where(j => j != i)
            .Min(j => Math.Sqrt(Math.Pow(rows[i] - rows[j], 2) + Math.Pow(columns[i] - columns[j], 2))) * pixelSize).ToArray();
        double meanError = Math.Abs(spacing.Average() - trueSpacing) / trueSpacing;
        double worstPair = spacing.Max(s => Math.Abs(s - trueSpacing) / trueSpacing);
        _output.WriteLine($"标记 {rows.Length} 个；当量 {pixelSize:F6} mm/像素；平均间距 {spacing.Average():F4} mm，真实 {trueSpacing:F4} mm，平均误差 {meanError * 100:F3}%，单对最大偏差 {worstPair * 100:F3}%（信息）");
        Assert.True(meanError < 0.005, $"平均间距 {spacing.Average():F4} mm，真实 {trueSpacing:F4} mm，误差 {meanError * 100:F3}%");

        // Matrix：校正图 (行, 列) → (X, Y) mm 应落在标定板的真实标记上（测量平面即参考标定板平面，厚度 0）
        double worst = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            CalibrationService.TransformPoint(matrix, rows[i], columns[i], out double x, out double y);
            worst = Math.Max(worst, Enumerable.Range(0, trueX.Length).Min(j => Math.Sqrt(Math.Pow(trueX[j] * 1000 - x, 2) + Math.Pow(trueY[j] * 1000 - y, 2))));
        }
        _output.WriteLine($"标记经 Matrix 换算后与真实位置最大偏差 {worst:F4} mm");
        Assert.True(worst < 0.1, $"标记经 Matrix 换算后与真实位置最大偏差 {worst:F4} mm");
        FlowResources.Release(tool);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 畸变校正_指定当量与区域_映射按缓存键重建_预热生成_尺寸不符失败()
    {
        CameraCalibration camera = CalibrateExample().Camera;
        string file = SaveCamera(camera);
        var tool = new ImageRectifyTool(Module) { CalibrationFile = file, PixelSize = 0.1, RegionX = -10, RegionY = -5, RegionWidth = 20, RegionHeight = 12 };
        HOperatorSet.ReadImage(out HObject image, ExampleImagePaths()[1]);
        using FlowContext ctx = ImageContext(image);
        int Generated() => ctx.Log.Count(m => m.Contains("生成校正映射"));

        Assert.True(tool.Run(ctx).IsSuccess);
        HOperatorSet.GetImageSize(((HalconImage)ctx.GetVariable(Module, "Image").Value).Object, out HTuple w, out HTuple h);
        Assert.Equal((200, 120), (w.I, h.I));
        Assert.Equal(0.1, (double)ctx.GetVariable(Module, "ActualPixelSize").Value, 12);
        Assert.Equal(new[] { 0.0, 0.1, -10.0, 0.1, 0.0, -5.0 }, ((HomMat2D)ctx.GetVariable(Module, "Matrix").Value).ToArray(), new Tolerance(1e-12));
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(1, Generated());
        tool.Interpolation = RectifyInterpolation.nearest_neighbor;
        Assert.True(tool.Run(ctx).IsSuccess);
        tool.PixelSize = 0.2;
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(3, Generated());
        HOperatorSet.GetImageSize(((HalconImage)ctx.GetVariable(Module, "Image").Value).Object, out w, out h);
        Assert.Equal((100, 60), (w.I, h.I));

        // 预热生成映射后运行不再生成；释放后重新生成
        tool.ReleaseResources();
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        Assert.True(FlowResources.Prepare(root).IsSuccess);
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(3, Generated());
        FlowResources.Release(root);
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(4, Generated());

        HOperatorSet.GenImageConst(out HObject small, "byte", 640, 480);
        using FlowContext wrong = ImageContext(small);
        NodeResult failed = tool.Run(wrong);
        Assert.False(failed.IsSuccess);
        Assert.Contains("图像尺寸 640×480 与相机标定的 1292×964 不一致", failed.Message);

        tool.PixelSize = 1e-6;
        tool.RegionWidth = tool.RegionHeight = 0;
        Assert.Contains("超出范围", tool.Run(ctx).Message);
        FlowResources.Release(tool);
    }

    // ======================= 非 HALCON 用例 =======================

    /// <summary>不经 HALCON 构造的相机段（只用于格式与校验）。</summary>
    private static CameraCalibration FakeCamera() => new()
    {
        CamParam =
        {
            new CameraParameter { Name = "camera_type", Value = "area_scan_division" },
            new CameraParameter { Name = "focus", Value = 0.008 }, new CameraParameter { Name = "kappa", Value = -1500.0 },
            new CameraParameter { Name = "sx", Value = 3.7e-6 }, new CameraParameter { Name = "sy", Value = 3.7e-6 },
            new CameraParameter { Name = "cx", Value = 640.0 }, new CameraParameter { Name = "cy", Value = 480.0 },
            new CameraParameter { Name = "image_width", Value = 1292.0 }, new CameraParameter { Name = "image_height", Value = 964.0 }
        },
        Pose = new[] { 0.0, 0, 0.14, 0, 0, 0, 0 },
        RmsError = 0.08,
        PlateDescription = "calplate_80mm.cpd",
        ImageCount = 7
    };

    [Fact]
    public void 合并规则_相机段与已有段共存_没有已有标定时Kind为Camera()
    {
        CameraCalibration camera = FakeCamera();
        CalibrationResult alone = CalibrationService.MergeCamera(null, camera, "mm", "相机");
        Assert.Equal((CalibrationFileKind.Camera, "mm", "Camera", "相机"), (alone.Kind, alone.Unit, alone.SectionsText(), alone.Description));
        CalibrationResult rotationOnly = CalibrationService.MergeRotationCenter(null, new RotationCenterCalibration { Row = 1, Column = 2, Radius = 3 }, "um", null);
        CalibrationResult withRotation = CalibrationService.MergeCamera(rotationOnly, camera, "mm", null);
        Assert.Equal((CalibrationFileKind.Camera, "um", "RotationCenter + Camera"), (withRotation.Kind, withRotation.Unit, withRotation.SectionsText()));
        CalibrationResult legacy = new() { Kind = CalibrationFileKind.Affine2D, IsLegacyTuple = true, Affine2D = new Affine2DCalibration { HomMat2D = new[] { 1.0, 0, 0, 0, 1, 0 } } };
        Assert.Equal(CalibrationFileKind.Affine2D, CalibrationService.MergeCamera(legacy, camera, "mm", null).Kind);
        Assert.Equal("mm", CalibrationService.MergeCamera(legacy, camera, "mm", null).Unit);
        CalibrationResult loaded = CalibrationService.Parse(CalibrationService.ToJson(alone), "内嵌标定数据");
        Assert.Equal(camera.CamParam.Select(p => (p.Name, p.Value)), loaded.Camera.CamParam.Select(p => (p.Name, p.Value)));
        Assert.Equal((0.08, "calplate_80mm.cpd", 7), (loaded.Camera.RmsError.GetValueOrDefault(), loaded.Camera.PlateDescription, loaded.Camera.ImageCount));
    }

    [Fact]
    public void 畸变校正_缺Camera段时校验运行预热措辞一致()
    {
        string affineOnly = PathOf("affine.vfcal.json");
        CalibrationService.Save(affineOnly, CalibrationService.FromAffine(new Affine2DCalibration { HomMat2D = new[] { 1.0, 0, 0, 0, 1, 0 } }, "mm", null));

        void AssertSame(ImageRectifyTool tool, params string[] parts)
        {
            string issue = Assert.Single(ConfigIssues(tool), m => m.Contains("畸变校正需要"));
            using var ctx = new FlowContext();
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

        AssertSame(new ImageRectifyTool(Module) { CalibrationFile = affineOnly }, affineOnly, "类型为 Affine2D（包含 Affine2D），畸变校正需要 Camera 数据");
        var embedded = new ImageRectifyTool(Module);
        embedded.UseEmbeddedCalibration(File.ReadAllText(affineOnly));
        AssertSame(embedded, "内嵌标定数据", "畸变校正需要 Camera 数据");

        string inch = PathOf("inch.vfcal.json");
        CalibrationService.Save(inch, CalibrationService.MergeCamera(null, FakeCamera(), "inch", null));
        var inchTool = new ImageRectifyTool(Module) { CalibrationFile = inch };
        Assert.Contains(ConfigIssues(inchTool), m => m.Contains("单位“inch”不受支持，畸变校正只支持 m / cm / mm / um"));
    }

    [Fact]
    public void 畸变校正_其他校验_互斥_文件缺失_参数范围()
    {
        string file = PathOf("camera.vfcal.json");
        CalibrationService.Save(file, CalibrationService.MergeCamera(null, FakeCamera(), "mm", null));
        var tool = new ImageRectifyTool(Module) { CalibrationFile = file };
        Assert.DoesNotContain(ConfigIssues(tool), m => m.Contains("标定") || m.Contains("当量") || m.Contains("区域"));

        tool.PixelSize = -1;
        Assert.Contains(ConfigIssues(tool), m => m.Contains("像素当量不能为负数"));
        tool.PixelSize = 0;
        tool.RegionWidth = 10;
        Assert.Contains(ConfigIssues(tool), m => m.Contains("输出区域的宽和高须同时大于 0"));
        using (var ctx = new FlowContext())
        {
            Assert.Contains("输出区域的宽和高须同时大于 0", tool.Run(ctx).Message);
        }
        tool.RegionWidth = 0;

        string json = File.ReadAllText(file);
        tool.UseEmbeddedCalibration(json);
        Assert.Equal((CalibrationSource.Embedded, (string?)null), (tool.CalibrationSource, tool.CalibrationFile));
        Assert.False(tool.IsParameterVisible(nameof(ImageRectifyTool.CalibrationFile)));
        Assert.False(tool.IsParameterVisible(nameof(ImageRectifyTool.CalibrationData)));
        tool.UseCalibrationFile(file);
        Assert.Equal((CalibrationSource.File, (string?)null), (tool.CalibrationSource, tool.CalibrationData));
        tool.CalibrationData = json;
        Assert.Contains(ConfigIssues(tool), m => m.Contains("标定来源为文件，但仍保留内嵌标定数据"));
        var empty = new ImageRectifyTool(Module) { CalibrationSource = CalibrationSource.Embedded };
        Assert.Contains(ConfigIssues(empty), m => m.Contains("标定来源为内嵌，但没有内嵌标定数据"));

        var missing = new ImageRectifyTool(Module) { CalibrationFile = file + ".missing" };
        Assert.DoesNotContain(ConfigIssues(missing), m => m.Contains("标定文件不存在"));
        using (var ctx = new FlowContext())
        {
            Assert.Contains("标定文件不存在", missing.Run(ctx).Message);
        }
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(missing));
        Assert.Contains("标定文件不存在", Assert.Single(FlowResources.Prepare(root).Issues).Message);
        var unconfigured = new ImageRectifyTool(Module);
        using (var ctx = new FlowContext())
        {
            Assert.Contains("未配置 CalibrationFile", unconfigured.Run(ctx).Message);
        }
    }

    [Fact]
    public void 畸变校正_保存加载_默认值_命名守卫_工具箱09标定()
    {
        var defaults = new ImageRectifyTool(Module);
        Assert.Equal(("Input.Image", CalibrationSource.File, 0.0, RectifyInterpolation.bilinear), (defaults.ImagePath, defaults.CalibrationSource, defaults.PixelSize, defaults.Interpolation));
        var tool = new ImageRectifyTool(Module)
        {
            ImagePath = "图像1.Image", PixelSize = 0.05, RegionX = -1.5, RegionY = 2.5, RegionWidth = 30, RegionHeight = 20,
            Interpolation = RectifyInterpolation.nearest_neighbor
        };
        tool.UseEmbeddedCalibration("{\"x\":1}");
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"image-rectify\"", json);
        Assert.Contains("\"Interpolation\": 1", json);
        Assert.Contains("\"CalibrationSource\": 1", json);
        var loaded = (ImageRectifyTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((tool.ImagePath, tool.PixelSize, tool.RegionX, tool.RegionY, tool.RegionWidth, tool.RegionHeight, tool.Interpolation, tool.CalibrationData),
            (loaded.ImagePath, loaded.PixelSize, loaded.RegionX, loaded.RegionY, loaded.RegionWidth, loaded.RegionHeight, loaded.Interpolation, loaded.CalibrationData));

        var properties = new HashSet<string>(typeof(ImageRectifyTool).GetProperties().Select(p => p.Name));
        IReadOnlyList<ToolOutputDef> outputs = ToolMetadata.GetOutputs(typeof(ImageRectifyTool));
        Assert.DoesNotContain(outputs, o => properties.Contains(o.Name));
        Assert.Equal(new[] { "Image", "ActualPixelSize", "Matrix" }, outputs.Select(o => o.Name));
        Assert.Contains(nameof(ImageRectifyTool.PixelSize), properties);
        Assert.Contains(outputs, o => o.Name == "Matrix" && o.ElementClrType == typeof(HomMat2D));

        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "image-rectify");
        Assert.Equal(("09 标定", "畸变校正"), (item.Category, item.DisplayName));
        ToolNode created = Assert.IsType<ToolNode>(item.Factory());
        Assert.IsType<ImageRectifyTool>(created.Tool);
        Assert.Contains("\"ToolId\": \"image-rectify\"", FlowSerializer.SaveNode(created));
    }

    private sealed class Tolerance : IEqualityComparer<double>
    {
        private readonly double _tolerance;

        public Tolerance(double tolerance)
        {
            _tolerance = tolerance;
        }

        public bool Equals(double x, double y) => Math.Abs(x - y) <= _tolerance;
        public int GetHashCode(double obj) => 0;
    }
}
