using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Tools.Calibration;
using VisionFlow.Variables;
using Xunit;

namespace VisionFlow.Tests;

/// <summary>
/// MS-07 收尾：单位换算的 Calibration 当量来源（标定文件 / 引用标定矩阵）。
/// 当量为矩阵两列范数的平均（纯计算），按标定单位折算为毫米/像素。
/// 除标注 Requires=HALCON 的用例（旧版 write_tuple 文件读写、与 hom_mat2d_to_affine_par 对照）外不调用 HALCON 算子。
/// </summary>
public class Ms07CalibrationScaleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vf-ms07-" + Guid.NewGuid().ToString("N"));

    public Ms07CalibrationScaleTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>缩放 s、旋转 θ、平移 (tx, ty) 的相似变换（图像 (行, 列) → 物理 (X, Y)）。</summary>
    private static double[] Similarity(double scale, double theta, double tx = 12.5, double ty = -7.25)
    {
        return new[] { scale * Math.Cos(theta), -scale * Math.Sin(theta), tx, scale * Math.Sin(theta), scale * Math.Cos(theta), ty };
    }

    private string SaveAffine(string name, double[] matrix, string? unit)
    {
        string path = Path.Combine(_dir, name);
        CalibrationService.Save(path, CalibrationService.FromAffine(
            new Affine2DCalibration { HomMat2D = matrix, TransformType = CalibrationTransformType.similarity }, unit, "MS-07 用例"));
        return path;
    }

    private static FlowContext ContextWith(double value)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("卡尺1", "Width", VariableType.Double, value));
        ctx.SetVariable(Variable.Array("卡尺1", "Widths", VariableType.Double, new[] { value, 2 * value, double.NaN }));
        return ctx;
    }

    private static AngleConvertTool LengthTool(string? file = null, string? matrixPath = null)
    {
        return new AngleConvertTool("换算1")
        {
            ConvertKind = ConvertKind.Length,
            ScaleSource = ScaleSource.Calibration,
            ValuePath = "卡尺1.Width",
            CalibrationFile = file,
            MatrixPath = matrixPath
        };
    }

    private static double Run(AngleConvertTool tool, FlowContext ctx)
    {
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return (double)ctx.GetVariable(tool.ModuleName, "Value").Value;
    }

    private static FlowLogEntry ScaleLog(FlowContext ctx)
    {
        return Assert.Single(ctx.StructuredLogs, l => l.Message.StartsWith("[单位换算] 标定当量来自", StringComparison.Ordinal));
    }

    private static List<string> Messages(AngleConvertTool tool)
    {
        return tool.CheckConfiguration().Select(i => i.Message).ToList();
    }

    [Fact]
    public void 已知缩放与旋转角的标定文件_当量与真值一致()
    {
        const double scale = 0.0234;
        string path = SaveAffine("rot.vfcal.json", Similarity(scale, 0.5236), "mm");
        AngleConvertTool tool = LengthTool(path);
        FlowContext ctx = ContextWith(100);

        Assert.Equal(100 * scale, Run(tool, ctx), 12);
        Assert.Empty(tool.CheckConfiguration());
        FlowLogEntry log = ScaleLog(ctx);
        Assert.Equal(FlowLogLevel.Info, log.Level);
        Assert.Contains("单位 mm", log.Message);
        Assert.Contains("相差 0.00%", log.Message);

        tool.ValuePath = "卡尺1.Widths";
        tool.LengthUnit = LengthUnit.um;
        FlowContext arrayCtx = ContextWith(100);
        Assert.True(tool.Run(arrayCtx).IsSuccess);
        double[] values = (double[])arrayCtx.GetVariable("换算1", "Values").Value;
        Assert.Equal(100 * scale * 1000, values[0], 9);
        Assert.Equal(200 * scale * 1000, values[1], 9);
        Assert.True(double.IsNaN(values[2]));

        tool.ValuePath = "卡尺1.Width";
        tool.LengthUnit = LengthUnit.mm;
        tool.IsArea = true;
        Assert.Equal(100 * scale * scale, Run(tool, ContextWith(100)), 12);
    }

    [Theory]
    [InlineData("m", 0.00002, 0.02)]
    [InlineData("cm", 0.002, 0.02)]
    [InlineData("mm", 0.02, 0.02)]
    [InlineData("um", 20.0, 0.02)]
    [InlineData("μm", 20.0, 0.02)]
    public void 标定单位折算为毫米(string unit, double scaleInUnit, double expectedMillimetersPerPixel)
    {
        string path = SaveAffine("unit-" + Guid.NewGuid().ToString("N") + ".vfcal.json", Similarity(scaleInUnit, -0.3), unit);
        FlowContext ctx = ContextWith(50);

        Assert.Equal(50 * expectedMillimetersPerPixel, Run(LengthTool(path), ctx), 12);
        Assert.Contains("取平均 0.02 mm/像素", ScaleLog(ctx).Message);
    }

    [Fact]
    public void 未注明单位的标定文件按mm_不支持的单位校验与运行措辞一致()
    {
        string noUnit = SaveAffine("no-unit.vfcal.json", Similarity(0.05, 0), null);
        FlowContext ctx = ContextWith(10);
        Assert.Equal(0.5, Run(LengthTool(noUnit), ctx), 12);
        Assert.Contains("未注明单位，按 mm", ScaleLog(ctx).Message);

        string inch = SaveAffine("inch.vfcal.json", Similarity(0.05, 0), "inch");
        AngleConvertTool tool = LengthTool(inch);
        string issue = Assert.Single(Messages(tool));
        Assert.Equal($"标定文件 {inch} 的单位“inch”不受支持，单位换算只支持 m / cm / mm / um", issue);
        NodeResult result = tool.Run(ContextWith(10));
        Assert.False(result.IsSuccess);
        Assert.Equal("换算1 " + issue, result.Message);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 旧版write_tuple矩阵文件按mm()
    {
        string legacy = Path.Combine(_dir, "legacy.tup");
        HOperatorSet.WriteTuple(new HTuple(Similarity(0.04, 1.1)), legacy);
        AngleConvertTool tool = LengthTool(legacy);
        FlowContext ctx = ContextWith(25);

        Assert.Empty(tool.CheckConfiguration());
        Assert.Equal(1.0, Run(tool, ctx), 12);
        Assert.Contains("旧版矩阵文件无单位，按 mm", ScaleLog(ctx).Message);
    }

    [Fact]
    public void 引用非均匀缩放矩阵_取平均并警告给出两个方向的值()
    {
        // 行方向 0.02、列方向 0.025（相对差 22.2%），含旋转
        double c = Math.Cos(0.2), s = Math.Sin(0.2);
        var matrix = new HomMat2D(new HTuple(new[] { 0.02 * c, -0.025 * s, 3.0, 0.02 * s, 0.025 * c, 4.0 }));
        FlowContext ctx = ContextWith(100);
        ctx.SetVariable(Variable.Object("坐标转换1", "Matrix", matrix, 1));
        AngleConvertTool tool = LengthTool(matrixPath: "坐标转换1.Matrix");

        Assert.Empty(tool.CheckConfiguration());
        Assert.Equal(100 * 0.0225, Run(tool, ctx), 12);
        FlowLogEntry log = ScaleLog(ctx);
        Assert.Equal(FlowLogLevel.Warning, log.Level);
        Assert.Contains("行方向 0.02、列方向 0.025 mm/像素，相差 22.22%（超过 1%，行列缩放不一致），取平均 0.0225 mm/像素", log.Message);
        Assert.Contains("引用的标定矩阵 坐标转换1.Matrix（不带单位，按 mm）", log.Message);
    }

    [Fact]
    public void 行列缩放相差不超过1百分比时_信息日志_仍取平均()
    {
        // 行方向 0.02、列方向 0.02016（相对差约 0.80%）
        var matrix = new HomMat2D(new HTuple(new[] { 0.02, 0.0, 0.0, 0.0, 0.02016, 0.0 }));
        FlowContext ctx = ContextWith(100);
        ctx.SetVariable(Variable.Object("畸变校正1", "Matrix", matrix, 1));

        Assert.Equal(100 * 0.02008, Run(LengthTool(matrixPath: "畸变校正1.Matrix"), ctx), 12);
        FlowLogEntry log = ScaleLog(ctx);
        Assert.Equal(FlowLogLevel.Info, log.Level);
        Assert.Contains("相差 0.80%，取平均 0.02008 mm/像素", log.Message);
        Assert.DoesNotContain("超过", log.Message);
    }

    [Fact]
    public void 镜像矩阵行列式为负时当量取正()
    {
        // 畸变校正输出的 Matrix 形如 [0, s, x0, s, 0, y0]（行列式 −s²）
        var matrix = new HomMat2D(new HTuple(new[] { 0.0, 0.0616, -39.87, 0.0616, 0.0, -29.89 }));
        FlowContext ctx = ContextWith(10);
        ctx.SetVariable(Variable.Object("畸变校正1", "Matrix", matrix, 1));

        Assert.Equal(0.616, Run(LengthTool(matrixPath: "畸变校正1.Matrix"), ctx), 12);
    }

    [Fact]
    public void 引用矩阵优先于标定文件_文件不参与并在日志注明()
    {
        string file = SaveAffine("other.vfcal.json", Similarity(0.5, 0), "mm");
        string onlyCenter = Path.Combine(_dir, "center.vfcal.json");
        CalibrationService.Save(onlyCenter, CalibrationService.MergeRotationCenter(null, new RotationCenterCalibration { Row = 1, Column = 2, Radius = 3 }, "mm", null));
        var matrix = new HomMat2D(new HTuple(Similarity(0.01, 0.7)));

        foreach (string configured in new[] { file, onlyCenter, Path.Combine(_dir, "missing.vfcal.json") })
        {
            FlowContext ctx = ContextWith(100);
            ctx.SetVariable(Variable.Object("坐标转换1", "Matrix", matrix, 1));
            AngleConvertTool tool = LengthTool(configured, "坐标转换1.Matrix");
            Assert.Empty(tool.CheckConfiguration());
            Assert.Equal(1.0, Run(tool, ctx), 12);
            Assert.Contains($"已引用矩阵，标定文件 {configured} 不参与", ScaleLog(ctx).Message);
        }
    }

    [Fact]
    public void 标定文件缺Affine2D段_校验与运行措辞一致()
    {
        string onlyCenter = Path.Combine(_dir, "center.vfcal.json");
        CalibrationService.Save(onlyCenter, CalibrationService.MergeRotationCenter(null, new RotationCenterCalibration { Row = 1, Column = 2, Radius = 3 }, "mm", null));
        AngleConvertTool tool = LengthTool(onlyCenter);

        string issue = Assert.Single(Messages(tool));
        Assert.Equal($"标定文件 {onlyCenter} 的类型为 RotationCenter（包含 RotationCenter），单位换算需要 Affine2D 数据", issue);
        Assert.Equal(nameof(AngleConvertTool.CalibrationFile), Assert.Single(tool.CheckConfiguration()).Parameter);
        NodeResult result = tool.Run(ContextWith(10));
        Assert.False(result.IsSuccess);
        Assert.Equal("换算1 " + issue, result.Message);
    }

    [Fact]
    public void 标定来源未配置文件也未引用矩阵时校验报错_运行同样拒绝()
    {
        AngleConvertTool tool = LengthTool();
        string issue = Assert.Single(Messages(tool));
        Assert.Contains("请配置标定文件或引用标定矩阵", issue);
        NodeResult result = tool.Run(ContextWith(10));
        Assert.False(result.IsSuccess);
        Assert.Equal("换算1 " + issue, result.Message);

        tool.CalibrationFile = "   ";
        Assert.Contains("请配置标定文件或引用标定矩阵", Assert.Single(Messages(tool)));
        // 标定来源不检查 PixelSize（侧栏已隐藏）
        tool.PixelSize = 0;
        tool.MatrixPath = "坐标转换1.Matrix";
        Assert.Empty(tool.CheckConfiguration());
    }

    [Fact]
    public void 标定文件缺失不在校验阶段报_运行报告()
    {
        string missing = Path.Combine(_dir, "missing.vfcal.json");
        AngleConvertTool tool = LengthTool(missing);

        Assert.Empty(tool.CheckConfiguration());
        NodeResult result = tool.Run(ContextWith(10));
        Assert.False(result.IsSuccess);
        Assert.Equal($"换算1 标定文件不存在：{missing}", result.Message);
    }

    [Fact]
    public void 标定文件替换后重新读取()
    {
        string path = SaveAffine("swap.vfcal.json", Similarity(0.02, 0), "mm");
        AngleConvertTool tool = LengthTool(path);
        Assert.Equal(2.0, Run(tool, ContextWith(100)), 12);

        SaveAffine("swap.vfcal.json", Similarity(0.03, 0.4), "mm");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(3.0, Run(tool, ContextWith(100)), 12);
    }

    [Fact]
    public void 引用的标定矩阵无效时运行失败()
    {
        NodeResult missing = LengthTool(matrixPath: "坐标转换1.Matrix").Run(ContextWith(10));
        Assert.False(missing.IsSuccess);
        Assert.StartsWith("换算1 标定矩阵引用无效：坐标转换1.Matrix，", missing.Message);

        FlowContext ctx = ContextWith(10);
        ctx.SetVariable(Variable.Object("坐标转换1", "Matrix", new HomMat2D(new HTuple(new[] { 0.0, 0.0, 1.0, 0.0, 0.0, 2.0 })), 1));
        NodeResult degenerate = LengthTool(matrixPath: "坐标转换1.Matrix").Run(ctx);
        Assert.False(degenerate.IsSuccess);
        Assert.Contains("标定矩阵的缩放无效", degenerate.Message);
    }

    [Fact]
    public void 固定当量与角度模式行为不变()
    {
        string path = SaveAffine("ignored.vfcal.json", Similarity(0.5, 0), "mm");
        var fixedTool = new AngleConvertTool("换算1")
        {
            ConvertKind = ConvertKind.Length,
            ValuePath = "卡尺1.Width",
            PixelSize = 0.02,
            CalibrationFile = path,
            MatrixPath = "坐标转换1.Matrix"
        };
        FlowContext ctx = ContextWith(100);
        Assert.Equal(2.0, Run(fixedTool, ctx), 12);
        Assert.DoesNotContain(ctx.StructuredLogs, l => l.Message.Contains("标定当量"));
        Assert.Equal(2.0, fixedTool.Convert(100), 12);
        Assert.Equal(5.0, fixedTool.Convert(100, 0.05), 12);

        var angle = new AngleConvertTool("角度1") { ValuePath = "卡尺1.Width", ScaleSource = ScaleSource.Calibration };
        Assert.Empty(angle.CheckConfiguration());
        Assert.Equal(AngleMath.ToDegrees(100), Run(angle, ContextWith(100)), 9);
    }

    [Fact]
    public void 参数显隐按换算类别与当量来源()
    {
        var tool = new AngleConvertTool("换算1");
        foreach (string name in new[] { "ScaleSource", "PixelSize", "LengthUnit", "IsArea", "MatrixPath", "CalibrationFile" })
        {
            Assert.False(tool.IsParameterVisible(name), "Angle: " + name);
        }

        tool.ConvertKind = ConvertKind.Length;
        foreach (string name in new[] { "ScaleSource", "PixelSize", "LengthUnit", "IsArea", "ValuePath" })
        {
            Assert.True(tool.IsParameterVisible(name), "Fixed: " + name);
        }
        Assert.False(tool.IsParameterVisible("MatrixPath"));
        Assert.False(tool.IsParameterVisible("CalibrationFile"));

        tool.ScaleSource = ScaleSource.Calibration;
        foreach (string name in new[] { "ScaleSource", "MatrixPath", "CalibrationFile", "LengthUnit", "IsArea", "ValuePath" })
        {
            Assert.True(tool.IsParameterVisible(name), "Calibration: " + name);
        }
        Assert.False(tool.IsParameterVisible("PixelSize"));
        Assert.False(tool.IsParameterVisible("InputUnit"));
    }

    [Fact]
    public void 序列化按数字保存_历史文件默认固定当量()
    {
        AngleConvertTool tool = LengthTool(Path.Combine(_dir, "a.vfcal.json"), "坐标转换1.Matrix");
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"angle-convert\"", json);
        Assert.Contains("\"ScaleSource\": 1", json);
        var loaded = (AngleConvertTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal(ScaleSource.Calibration, loaded.ScaleSource);
        Assert.Equal(tool.CalibrationFile, loaded.CalibrationFile);
        Assert.Equal("坐标转换1.Matrix", loaded.MatrixPath);

        var legacyNode = System.Text.Json.Nodes.JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new AngleConvertTool("换算1") { ConvertKind = ConvertKind.Length, PixelSize = 0.02 })))!.AsObject();
        System.Text.Json.Nodes.JsonObject properties = legacyNode["Tool"]!["Properties"]!.AsObject();
        foreach (string name in new[] { "ScaleSource", "CalibrationFile", "MatrixPath" })
        {
            properties.Remove(name);
        }
        var legacy = (AngleConvertTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacyNode.ToJsonString())).Tool;
        Assert.Equal(ScaleSource.Fixed, legacy.ScaleSource);
        Assert.Null(legacy.CalibrationFile);
        Assert.Null(legacy.MatrixPath);
        legacy.ValuePath = "卡尺1.Width";
        Assert.Equal(2.0, Run(legacy, ContextWith(100)), 12);

        var metadata = Assert.Single(ToolMetadata.GetInputRefs(typeof(AngleConvertTool)), d => d.PropertyName == "MatrixPath");
        Assert.Equal(typeof(HomMat2D), metadata.ExpectedType);
        Assert.True(metadata.Optional);
        Assert.Equal("标定矩阵", metadata.DisplayName);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 列范数当量与hom_mat2d_to_affine_par的缩放一致()
    {
        double c = Math.Cos(0.35), s = Math.Sin(0.35);
        foreach (double[] matrix in new[]
        {
            Similarity(0.0234, 0.5236),
            new[] { 0.02 * c, -0.025 * s, 3.0, 0.02 * s, 0.025 * c, 4.0 },
            new[] { 0.02, 0.005, 1.0, 0.001, 0.03, 2.0 },
            new[] { 0.0, 0.0616, -39.87, 0.0616, 0.0, -29.89 }
        })
        {
            AngleConvertTool.GetAxisScales(matrix, out double rowScale, out double columnScale);
            HOperatorSet.HomMat2dToAffinePar(new HTuple(matrix), out HTuple sx, out HTuple sy, out _, out _, out _, out _);
            Assert.Equal(Math.Abs(sx.D), rowScale, 12);
            Assert.Equal(Math.Abs(sy.D), columnScale, 12);
        }
    }
}
