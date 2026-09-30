using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// VF-03 回归：已配置定位跟随但矩阵解析失败时明确失败，不再隐式退回固定位置测量。
/// </summary>
public class FollowMeasureMatrixTests
{
    private sealed class TestMeasureTool : VisionFlow.Tools.FollowMeasureToolBase
    {
        public TestMeasureTool(string moduleName) : base(moduleName) { }
        public List<HomMat2D> MeasuredMatrices { get; } = new();
        public List<int> Indices { get; } = new();

        public override NodeResult Run(FlowContext ctx)
        {
            if (!TryGetMatrices(ctx, out var matrices, out string error))
                return NodeResult.Fail(error);
            foreach (HomMat2D matrix in matrices)
            {
                Indices.Add(GetIndex(ctx));
                MeasuredMatrices.Add(matrix);
            }
            return NodeResult.Ok;
        }

        public bool TryResolve(FlowContext ctx, out List<VisionFlow.Variables.HomMat2D> matrices, out string? error)
            => TryGetMatrices(ctx, out matrices, out error);
    }

    private static FlowContext NewContext() => new FlowContext();

    public static IEnumerable<object[]> ToolTypes()
    {
        yield return new object[] { typeof(LineFollowMeasureTool) };
        yield return new object[] { typeof(RectangleFollowMeasureTool) };
        yield return new object[] { typeof(CircleFollowMeasureTool) };
        yield return new object[] { typeof(OneDCaliperFollowMeasureTool) };
        yield return new object[] { typeof(ArcCaliperFollowMeasureTool) };
        yield return new object[] { typeof(EllipseFollowMeasureTool) };
    }

    private static ToolBase CreateTool(Type type, string matrixPath)
    {
        var tool = (ToolBase)Activator.CreateInstance(type, "测量1")!;
        type.GetProperty("MatrixPath")!.SetValue(tool, matrixPath);
        type.GetProperty("ImagePath")!.SetValue(tool, "Input.Image");
        return tool;
    }

    [Fact]
    public void 未配置矩阵_固定位置测量()
    {
        var tool = new TestMeasureTool("测量1");
        bool ok = tool.TryResolve(NewContext(), out var matrices, out string? error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(matrices);
        Assert.Null(matrices[0]);
    }

    [Fact]
    public void 单矩阵引用_正常解析()
    {
        var ctx = NewContext();
        ctx.SetVariable(Variable.Object("定位", "HomMat", VisionFlow.Variables.HomMat2D.Identity, 1));
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMat" };
        bool ok = tool.TryResolve(ctx, out var matrices, out string? error);
        Assert.True(ok);
        Assert.Single(matrices);
        Assert.NotNull(matrices[0]);
    }

    [Fact]
    public void 矩阵集合引用_正常解析()
    {
        var ctx = NewContext();
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object,
            new[] { VisionFlow.Variables.HomMat2D.Identity, VisionFlow.Variables.HomMat2D.Identity }));
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMats" };
        bool ok = tool.TryResolve(ctx, out var matrices, out _);
        Assert.True(ok);
        Assert.Equal(2, matrices.Count);
    }

    [Fact]
    public void 公共数组输出的装箱矩阵_逐项检查类型()
    {
        using var ctx = NewContext();
        var tool = new TestMeasureTool("测量1") { MatrixPath = "分支.HomMats" };
        ctx.SetVariable(Variable.Array<object>("分支", "HomMats", VariableType.Object,
            new object[] { HomMat2D.Identity, HomMat2D.Identity }));
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, tool.MeasuredMatrices.Count);
        tool.MeasuredMatrices.Clear();
        ctx.SetVariable(Variable.Array<object>("分支", "HomMats", VariableType.Object,
            new object[] { HomMat2D.Identity, 42 }));
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("第 1 项类型", result.Message);
        Assert.Empty(tool.MeasuredMatrices);
    }

    [Fact]
    public void 已配置但引用不存在_明确失败()
    {
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMat" };
        bool ok = tool.TryResolve(NewContext(), out _, out string? error);
        Assert.False(ok);
        Assert.Contains("测量1", error);
        Assert.Contains("定位.HomMat", error);
    }

    [Fact]
    public void 已配置但类型错误_明确失败()
    {
        var ctx = NewContext();
        ctx.SetVariable(Variable.Single("定位", "Score", VariableType.Double, 0.5));
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.Score" };
        bool ok = tool.TryResolve(ctx, out _, out string? error);
        Assert.False(ok);
        Assert.Contains("不是 HomMat2D", error);
    }

    [Fact]
    public void 已配置但矩阵集合为空_明确失败()
    {
        var ctx = NewContext();
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object,
            Array.Empty<VisionFlow.Variables.HomMat2D>()));
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMats" };
        bool ok = tool.TryResolve(ctx, out _, out string? error);
        Assert.False(ok);
        Assert.Contains("为空", error);
    }

    [Fact]
    public void 预览显式允许降级时_退回固定位置并记录警告()
    {
        using var source = NewContext();
        using var ctx = source.CreatePreviewContext(allowMatrixFallback: true);
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMat" };
        bool ok = tool.TryResolve(ctx, out var matrices, out string? error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(matrices);
        Assert.Null(matrices[0]);
        Assert.Contains(ctx.StructuredLogs, l => l.Level == FlowLogLevel.Warning);
        Assert.False(source.IsPreview);
        Assert.False(source.AllowMatrixFallback);
        Assert.False(tool.TryResolve(source, out _, out _));
    }

    [Fact]
    public void 默认预览_不自动允许降级()
    {
        using var source = NewContext();
        using var preview = source.CreatePreviewContext();
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMat" };
        Assert.True(preview.IsPreview);
        Assert.False(preview.AllowMatrixFallback);
        Assert.False(tool.TryResolve(preview, out _, out string? error));
        Assert.Contains("不会退回", error);
        Assert.DoesNotContain(preview.StructuredLogs, l => l.Level == FlowLogLevel.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 矩阵集合含null_整个解析失败且不测量任何项(bool validFirst)
    {
        using var ctx = NewContext();
        HomMat2D[] values = validFirst ? new[] { HomMat2D.Identity, null! } : new HomMat2D[] { null! };
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object, values));
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMats" };
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("null", result.Message);
        Assert.Empty(tool.MeasuredMatrices);
        Assert.Empty(tool.Indices);
        Assert.False(tool.TryResolve(ctx, out var matrices, out _));
        Assert.Null(matrices);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void 非有限矩阵值_在开始任何测量前失败(double invalid)
    {
        using var ctx = NewContext();
        using var data = new HTuple(1.0, 0.0, invalid, 0.0, 1.0, 0.0);
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object,
            new[] { HomMat2D.Identity, new HomMat2D(data) }));
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMats" };
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("第 1 项无效", result.Message);
        Assert.Contains("非有限", result.Message);
        Assert.Empty(tool.MeasuredMatrices);
    }

    [Fact]
    public void 非数值矩阵或不可逆矩阵_明确失败()
    {
        using var ctx = NewContext();
        using var textData = new HTuple("1", "0", "0", "0", "1", "0");
        using var singularData = new HTuple(0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
        using var overflowData = new HTuple(double.MaxValue, 0.0, 0.0, 0.0, double.MaxValue, 0.0);
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMat" };
        foreach (HTuple data in new[] { textData, singularData, overflowData })
        {
            ctx.SetVariable(Variable.Object("定位", "HomMat", new HomMat2D(data), 1));
            Assert.False(tool.TryResolve(ctx, out _, out string? error));
            Assert.Contains("无效", error);
        }
    }

    [Fact]
    public void 整数矩阵有效_序号输入实际执行标量转换()
    {
        using var ctx = NewContext();
        using var data = new HTuple(1, 0, 0, 0, 1, 0);
        ctx.SetVariable(Variable.Object("定位", "HomMat", new HomMat2D(data), 1));
        ctx.SetVariable(Variable.Array("定位", "Indices", VariableType.Int, new[] { 4, 5 }));
        var tool = new TestMeasureTool("测量1") { MatrixPath = "定位.HomMat", IndexPath = "定位.Indices" };
        Assert.Throws<InvalidCastException>(() => tool.Run(ctx));
        tool.IndexPath = "定位.Indices[0]";
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(new[] { 4 }, tool.Indices);
        Assert.Single(tool.MeasuredMatrices);
    }

    [Theory]
    [MemberData(nameof(ToolTypes))]
    public void 所有跟随工具_生产及默认预览拒绝无效矩阵_显式预览才能进入图像转换(Type type)
    {
        using var source = NewContext();
        source.SetVariable(Variable.Array<HomMat2D?>("定位", "HomMats", VariableType.Object,
            new[] { HomMat2D.Identity, null }));
        source.SetVariable(Variable.Single("Input", "Image", VariableType.Int, 42));
        using var preview = source.CreatePreviewContext();
        using var fallback = source.CreatePreviewContext(allowMatrixFallback: true);
        ToolBase tool = CreateTool(type, "定位.HomMats");
        foreach (FlowContext ctx in new[] { source, preview })
        {
            NodeResult result = tool.Run(ctx);
            Assert.False(result.IsSuccess);
            Assert.Contains("第 1 项为 null", result.Message);
            Assert.False(ctx.TryGetVariable("测量1", "Results", out _));
            Assert.False(ctx.TryGetVariable("测量1", "ResultContour", out _));
            Assert.DoesNotContain(ctx.StructuredLogs, l => l.Level == FlowLogLevel.Warning);
        }
        // 矩阵降级通过后必须真的读取图像；错误类型到达 Input<HalconImage> 的转换处。
        Assert.Throws<InvalidCastException>(() => tool.Run(fallback));
        Assert.Contains(fallback.StructuredLogs,
            l => l.Level == FlowLogLevel.Warning && l.Message.Contains("预览已显式允许降级"));
        Assert.Null(type.GetProperty("AllowMatrixFallback"));
    }

    [Theory]
    [MemberData(nameof(ToolTypes))]
    public void 所有跟随工具_合法矩阵集合通过解析并实际读取图像(Type type)
    {
        using var ctx = NewContext();
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object,
            new[] { HomMat2D.Identity, HomMat2D.Identity }));
        ctx.SetVariable(Variable.Single("Input", "Image", VariableType.Int, 42));
        ToolBase tool = CreateTool(type, "定位.HomMats");
        Assert.Throws<InvalidCastException>(() => tool.Run(ctx));
        Assert.DoesNotContain(ctx.StructuredLogs, l => l.Level == FlowLogLevel.Warning);
    }

    [Theory]
    [InlineData("fixed", 1, false)]
    [InlineData("single", 1, true)]
    [InlineData("array", 2, true)]
    [InlineData("fallback", 1, false)]
    public void 椭圆真实测量_固定单矩阵集合及显式预览结果一致(string mode, int count, bool followed)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject background, "byte", 200, 200);
        using var backgroundOwner = background;
        HOperatorSet.GenEllipse(out HObject region, 100, 100, 0, 30, 20);
        using var regionOwner = region;
        HOperatorSet.PaintRegion(region, background, out HObject image, 255, "fill");
        using var imageOwner = image;
        using var source = NewContext();
        source.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        source.SetVariable(Variable.Object("定位", "HomMat", HomMat2D.Identity, 1));
        source.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object,
            new[] { HomMat2D.Identity, HomMat2D.Identity }));
        using var preview = source.CreatePreviewContext(allowMatrixFallback: mode == "fallback");
        FlowContext ctx = mode == "fallback" ? preview : source;
        var tool = new EllipseFollowMeasureTool("测量1")
        {
            ImagePath = "Input.Image",
            MatrixPath = mode switch
            {
                "single" => "定位.HomMat",
                "array" => "定位.HomMats",
                "fallback" => "定位.Missing",
                _ => null
            },
            EllipseRow = 100,
            EllipseColumn = 100,
            EllipseAngle = 0,
            EllipseLength1 = 30,
            EllipseLength2 = 20
        };
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        var results = ctx.GetVariable("测量1", "Results").GetValue<List<EllipseMeasureResult>>();
        Assert.Equal(count, results.Count);
        Assert.All(results, item =>
        {
            Assert.Equal(followed, item.Followed);
            Assert.InRange(item.Row, 99, 101);
            Assert.InRange(item.Column, 99, 101);
        });
        Assert.Equal(count == 1 ? new[] { -1 } : new[] { 0, 1 }, results.Select(item => item.Index));
    }

    [Fact]
    public void 保存流程文件_所有跟随工具均不持久化预览降级开关()
    {
        var root = new SequenceNode("根");
        int index = 0;
        foreach (object[] entry in ToolTypes())
        {
            ToolBase tool = CreateTool((Type)entry[0], "定位.HomMats");
            tool.ModuleName += index++;
            root.Children.Add(new ToolNode(tool));
        }
        string path = Path.Combine(Directory.GetCurrentDirectory(), $"matrix-policy-{Guid.NewGuid():N}.vflow.json");
        try
        {
            File.WriteAllText(path, FlowSerializer.Save(root));
            string saved = File.ReadAllText(path);
            Assert.DoesNotContain("AllowMatrixFallback", saved);
            Assert.Contains("MatrixPath", saved);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
