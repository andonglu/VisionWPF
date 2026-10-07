using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>MATCH-MEASURE-TOOLS-PLAN 第五批：MT-06 差分检测（VariationInspectTool）。</summary>
public class MatchMeasureBatch5Tests
{
    private const string Module = "差分1";

    private static FlowContext ImageContext(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static IReadOnlyList<string> ConfigIssues(ToolBase tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root).Issues.Select(i => i.Message).ToList();
    }

    private static int Count(FlowContext ctx) => (int)ctx.GetVariable(Module, "DefectCount").Value;
    private static double[] Areas(FlowContext ctx) => (double[])ctx.GetVariable(Module, "DefectAreas").Value;
    private static int Loads(FlowContext ctx) => ctx.Log.Count(m => m.Contains("模型加载耗时"));
    private static int Prepares(FlowContext ctx) => ctx.Log.Count(m => m.Contains("准备模型"));

    private static HObject Paint(HObject image, HObject region, double gray)
    {
        HOperatorSet.PaintRegion(region, image, out HObject painted, gray, "fill");
        region.Dispose();
        image.Dispose();
        return painted;
    }

    /// <summary>良品图：背景 30+offset，亮矩形（行 150~300、列 250~450）210+offset；可整体平移 (dr, dc)。</summary>
    private static HObject Good(int offset = 0, int width = 640, int height = 480, double dr = 0, double dc = 0)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", width, height);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, height - 1, width - 1);
        HObject image = Paint(blank, all, 30 + offset);
        HOperatorSet.GenRectangle1(out HObject pattern, 150 + dr, 250 + dc, 300 + dr, 450 + dc);
        return Paint(image, pattern, 210 + offset);
    }

    /// <summary>
    /// 待检图（良品 offset 2）：亮圆斑 (80,100) r6 灰度 120（约 113 像素）、亮矩形块 行 380~389 列 60~79 灰度 150（200 像素）、
    /// 暗点 行 420~421 列 560~561 灰度 0（4 像素，被最小面积 10 滤掉）；可选暗方块 行 60~71 列 500~511 灰度 0（144 像素）。
    /// </summary>
    private static HObject Defective(bool darkSquare = false, double dr = 0, double dc = 0)
    {
        HObject image = Good(2, dr: dr, dc: dc);
        HOperatorSet.GenCircle(out HObject spot, 80 + dr, 100 + dc, 6);
        image = Paint(image, spot, 120);
        HOperatorSet.GenRectangle1(out HObject block, 380 + dr, 60 + dc, 389 + dr, 79 + dc);
        image = Paint(image, block, 150);
        HOperatorSet.GenRectangle1(out HObject speck, 420 + dr, 560 + dc, 421 + dr, 561 + dc);
        image = Paint(image, speck, 0);
        if (darkSquare)
        {
            HOperatorSet.GenRectangle1(out HObject square, 60 + dr, 500 + dc, 71 + dr, 511 + dc);
            image = Paint(image, square, 0);
        }
        return image;
    }

    private static List<HObject> Samples(int count = 5)
    {
        return Enumerable.Range(0, count).Select(k => Good(k)).ToList();
    }

    private static byte[] Train(VariationModelMode mode, int count = 5)
    {
        List<HObject> samples = Samples(mode == VariationModelMode.direct ? 1 : count);
        try
        {
            return VariationModelTraining.Train(samples, mode);
        }
        finally
        {
            samples.ForEach(s => s.Dispose());
        }
    }

    private static VariationInspectTool Tool(byte[] data, VariationModelMode mode = VariationModelMode.standard) => new(Module)
    {
        VariationModelData = data,
        ModelMode = mode,
        AbsThreshold = "20",
        VarThreshold = "3",
        MinDefectArea = 10
    };

    private static double SpotArea()
    {
        HOperatorSet.GenCircle(out HObject spot, 80, 100, 6);
        HOperatorSet.AreaCenter(spot, out HTuple area, out _, out _);
        spot.Dispose();
        return area.D;
    }

    // ======================= 检测与后处理（B5-2-1） =======================

    [Fact]
    public void 标准模型_连通域拆分_最小面积筛选_输出一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        byte[] data = Train(VariationModelMode.standard);
        var tool = Tool(data);
        using HObject image = Defective();
        using (FlowContext ctx = ImageContext(image))
        {
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(2, Count(ctx));
            double[] areas = Areas(ctx);
            Assert.Equal(new[] { SpotArea(), 200.0 }.OrderBy(a => a), areas.OrderBy(a => a));
            Assert.Equal(200.0, (double)ctx.GetVariable(Module, "MaxDefectArea").Value);
            Assert.True((bool)ctx.GetVariable(Module, "HasDefect").Value);
            var region = (HalconRegion)ctx.GetVariable(Module, "DefectRegion").Value;
            HOperatorSet.CountObj(region.Object, out HTuple count);
            Assert.Equal(2, count.I);
            Assert.Same(image, ((HalconImage)ctx.GetVariable(Module, "Image").Value).Object);
        }

        // 最小面积 0：4 像素暗点也保留，三个连通域各自独立
        tool.MinDefectArea = 0;
        using (FlowContext ctx = ImageContext(image))
        {
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(3, Count(ctx));
            Assert.Contains(4.0, Areas(ctx));
        }
        FlowResources.Release(tool);
    }

    [Fact]
    public void 良品图_无缺陷是正常结果_不失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var tool = Tool(Train(VariationModelMode.standard));
        using HObject image = Good(3);
        using FlowContext ctx = ImageContext(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0, Count(ctx));
        Assert.Empty(Areas(ctx));
        Assert.Equal(0.0, (double)ctx.GetVariable(Module, "MaxDefectArea").Value);
        Assert.False((bool)ctx.GetVariable(Module, "HasDefect").Value);
        Assert.Contains(ctx.Log, m => m.Contains("未发现缺陷"));
        FlowResources.Release(tool);
    }

    [Fact]
    public void Robust模型_同样检出()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var tool = Tool(Train(VariationModelMode.robust), VariationModelMode.robust);
        using HObject image = Defective();
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, Count(ctx));
        FlowResources.Release(tool);
    }

    [Fact]
    public void 比较方式_亮暗分开_两值阈值依次为亮暗()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var tool = Tool(Train(VariationModelMode.standard));
        using HObject image = Defective(darkSquare: true);
        int Run(VariationCompareMode mode, string abs)
        {
            tool.CompareMode = mode;
            tool.AbsThreshold = abs;
            using FlowContext ctx = ImageContext(image);
            Assert.True(tool.Run(ctx).IsSuccess);
            return Count(ctx);
        }
        Assert.Equal(3, Run(VariationCompareMode.absolute, "20"));
        Assert.Equal(2, Run(VariationCompareMode.light, "20"));
        Assert.Equal(1, Run(VariationCompareMode.dark, "20"));
        Assert.Equal(3, Run(VariationCompareMode.light_dark, "20"));
        // 亮 20、暗 200：暗方块（差 32）不再超出
        Assert.Equal(2, Run(VariationCompareMode.light_dark, "20,200"));
        // 亮 200、暗 20：只剩暗方块
        Assert.Equal(1, Run(VariationCompareMode.light_dark, "200,20"));
        FlowResources.Release(tool);
    }

    [Fact]
    public void 允许偏差为绝对阈值与相对阈值乘偏差图的较大者_HALCON实测()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject Halves(int left, int right)
        {
            HOperatorSet.GenImageConst(out HObject blank, "byte", 100, 100);
            HOperatorSet.GenRectangle1(out HObject l, 0, 0, 99, 49);
            HObject image = Paint(blank, l, left);
            HOperatorSet.GenRectangle1(out HObject r, 0, 50, 99, 99);
            return Paint(image, r, right);
        }
        // 左半恒为 100（偏差 0），右半交替 90 / 110（standard 偏差约 11.55）
        List<HObject> samples = new[] { 90, 110, 90, 110 }.Select(b => Halves(100, b)).ToList();
        var tool = new VariationInspectTool(Module) { VariationModelData = VariationModelTraining.Train(samples, VariationModelMode.standard), AbsThreshold = "5", VarThreshold = "2", MinDefectArea = 0 };
        samples.ForEach(s => s.Dispose());
        using HObject image = Halves(125, 125);
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        // 偏离 25：max(5, 2 × 11.55) = 23.1 → 右半也检出；若为 5 + 23.1 = 28.1 则右半不会检出
        Assert.Equal(new[] { 10000.0 }, Areas(ctx));
        FlowResources.Release(tool);
    }

    // ======================= 缓存键与阈值（B5-1-1） =======================

    [Fact]
    public void Direct模式_改阈值结果随之变化_不重新训练不重新加载()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        byte[] data = Train(VariationModelMode.direct);
        var tool = Tool(data, VariationModelMode.direct);
        using HObject image = Defective();
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, Count(ctx));
        // 圆斑与背景差 88、矩形块差 118：绝对阈值 100 只剩矩形块
        tool.AbsThreshold = "100";
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(new[] { 200.0 }, Areas(ctx));
        tool.AbsThreshold = "20";
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, Count(ctx));
        // 阈值不变时不再 prepare
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Same(data, tool.VariationModelData);
        Assert.Equal(1, Loads(ctx));
        Assert.Equal(3, Prepares(ctx));
        FlowResources.Release(tool);
    }

    [Fact]
    public void 标准模式_改阈值免重训_HALCON实测阈值在prepare时烘焙所以同样重新prepare()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        byte[] data = Train(VariationModelMode.standard);
        var tool = Tool(data);
        using HObject image = Defective();
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, Count(ctx));
        tool.AbsThreshold = "200";
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(0, Count(ctx));
        tool.AbsThreshold = "100";
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(1, Count(ctx));
        Assert.Same(data, tool.VariationModelData);
        Assert.Equal(1, Loads(ctx));
        Assert.Equal(3, Prepares(ctx));
        FlowResources.Release(tool);
    }

    [Fact]
    public void 缓存键_替换数组重新加载_改建模方式要求重训_释放后重新加载()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var tool = Tool(Train(VariationModelMode.standard));
        using HObject image = Defective();
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        tool.UseEmbeddedModel(Train(VariationModelMode.standard));
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, Loads(ctx));

        tool.ModelMode = VariationModelMode.robust;
        Assert.Contains(ConfigIssues(tool), m => m.Contains("建模方式已改为 robust，需要重新训练"));
        NodeResult mismatch = tool.Run(ctx);
        Assert.False(mismatch.IsSuccess);
        Assert.Contains("需要重新训练", mismatch.Message);

        tool.ModelMode = VariationModelMode.standard;
        FlowResources.Release(tool);
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(3, Loads(ctx));
        FlowResources.Release(tool);
    }

    [Fact]
    public void 资源生命周期_Prepare预热_损坏数据抛出_未训练直接返回()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var tool = Tool(Train(VariationModelMode.standard));
        tool.Prepare();
        using HObject image = Defective();
        using (FlowContext ctx = ImageContext(image))
        {
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(0, Loads(ctx));
            Assert.Equal(0, Prepares(ctx));
        }
        tool.ReleaseResources();
        using (FlowContext ctx = ImageContext(image))
        {
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(1, Loads(ctx));
        }
        tool.ReleaseResources();

        new VariationInspectTool(Module).Prepare();
        Assert.Throws<InvalidOperationException>(() => Tool(new byte[] { 1, 2, 3 }).Prepare());
        using (FlowContext ctx = ImageContext(image))
        {
            Assert.Contains("差分模型未训练", new VariationInspectTool(Module).Run(ctx).Message);
        }
    }

    // ======================= 阈值 CSV（B5-1-3） =======================

    [Theory]
    [InlineData("20", new[] { 20.0 })]
    [InlineData("20,30", new[] { 20.0, 30.0 })]
    [InlineData(" 20 ， 30 ", new[] { 20.0, 30.0 })]
    [InlineData("0.5", new[] { 0.5 })]
    public void 阈值CSV_一个值共用_两个值亮暗分开(string text, double[] expected)
    {
        Assert.True(VariationThresholds.TryParse(text, "绝对阈值", out double[] values, out string error), error);
        Assert.Equal(expected, values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("a")]
    [InlineData("10,")]
    [InlineData("1,2,3")]
    [InlineData("NaN")]
    public void 阈值CSV_非法_校验与运行都报中文示例(string text)
    {
        Assert.False(VariationThresholds.TryParse(text, "绝对阈值", out _, out string error));
        Assert.Contains("如 20", error);
        Assert.Contains("如 20,30", error);
        var tool = new VariationInspectTool(Module) { AbsThreshold = text };
        Assert.Contains(ConfigIssues(tool), m => m.Contains("绝对阈值格式错误"));
        var variance = new VariationInspectTool(Module) { VarThreshold = text };
        Assert.Contains(ConfigIssues(variance), m => m.Contains("相对阈值格式错误"));
        using FlowContext ctx = new();
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("绝对阈值格式错误", result.Message);
    }

    // ======================= ModelFile 互斥（B5-1-2） =======================

    [Fact]
    public void 外部模型文件_与内嵌互斥_从文件加载_文件变化重新加载_重训清空外部文件()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        byte[] data = Train(VariationModelMode.standard);
        string file = Path.Combine(Path.GetTempPath(), "vf-variation-" + Guid.NewGuid().ToString("N") + ".vfvm");
        try
        {
            File.WriteAllBytes(file, data);
            var both = Tool(data);
            both.ModelFile = file;
            Assert.Contains(ConfigIssues(both), m => m.Contains("外部模型文件与内嵌模型数据不能同时存在"));
            using HObject image = Defective();
            using (FlowContext ctx = ImageContext(image))
            {
                Assert.False(both.Run(ctx).IsSuccess);
            }

            var external = Tool(data);
            external.UseModelFile(file);
            Assert.Null(external.VariationModelData);
            Assert.Empty(ConfigIssues(external));
            using (FlowContext ctx = ImageContext(image))
            {
                Assert.True(external.Run(ctx).IsSuccess);
                Assert.True(external.Run(ctx).IsSuccess);
                Assert.Equal(2, Count(ctx));
                Assert.Equal(1, Loads(ctx));
                // 文件被替换（大小 / 修改时间变化）→ 重新加载
                File.WriteAllBytes(file, Train(VariationModelMode.standard, count: 3));
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
                Assert.True(external.Run(ctx).IsSuccess);
                Assert.Equal(2, Loads(ctx));
                Assert.Contains(ctx.Log, m => m.Contains("样本 3 张"));
            }
            FlowResources.Release(external);

            // 重新训练：整体替换内嵌数组并清空外部文件
            byte[] retrained = Train(VariationModelMode.standard);
            external.UseEmbeddedModel(retrained);
            Assert.Null(external.ModelFile);
            Assert.Same(retrained, external.VariationModelData);

            var missing = new VariationInspectTool(Module) { ModelFile = file + ".missing" };
            using (FlowContext ctx = ImageContext(image))
            {
                NodeResult result = missing.Run(ctx);
                Assert.False(result.IsSuccess);
                Assert.Contains("外部模型文件不存在", result.Message);
            }
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
            var corrupt = new VariationInspectTool(Module) { ModelFile = file };
            using (FlowContext ctx = ImageContext(image))
            {
                NodeResult result = corrupt.Run(ctx);
                Assert.False(result.IsSuccess);
                Assert.Contains("差分模型数据格式错误", result.Message);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void 模型数据格式_损坏与版本不符明确报错()
    {
        byte[] payload = { 9, 8, 7 };
        byte[] good = new VariationModelPackage { Mode = VariationModelMode.robust, Width = 64, Height = 32, SampleCount = 4, Payload = payload }.ToBytes();
        VariationModelPackage parsed = VariationModelPackage.FromBytes(good);
        Assert.Equal((VariationModelMode.robust, 64, 32, 4), (parsed.Mode, parsed.Width, parsed.Height, parsed.SampleCount));
        Assert.Equal(payload, parsed.Payload);

        Assert.Contains("缺少 VFVM 标识", Assert.Throws<InvalidDataException>(() => VariationModelPackage.FromBytes(new byte[40])).Message);
        byte[] version = (byte[])good.Clone();
        version[4] = 2;
        Assert.Contains("版本 2 不受支持", Assert.Throws<InvalidDataException>(() => VariationModelPackage.FromBytes(version)).Message);
        byte[] mode = (byte[])good.Clone();
        mode[8] = 7;
        Assert.Contains("建模方式 7 无效", Assert.Throws<InvalidDataException>(() => VariationModelPackage.FromBytes(mode)).Message);
        Assert.Contains("载荷长度与数据不符", Assert.Throws<InvalidDataException>(() => VariationModelPackage.FromBytes(good.Take(good.Length - 1).ToArray())).Message);

        Assert.Contains(ConfigIssues(new VariationInspectTool(Module) { VariationModelData = version }), m => m.Contains("版本 2 不受支持"));
    }

    // ======================= 尺寸校验（B5-1-4）、对齐与检测区域（B5-2-2） =======================

    [Fact]
    public void 尺寸不符_明确失败_错误信息带两个尺寸()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var tool = Tool(Train(VariationModelMode.standard));
        using HObject small = Good(0, 320, 240);
        using (FlowContext ctx = ImageContext(small))
        {
            NodeResult result = tool.Run(ctx);
            Assert.False(result.IsSuccess);
            Assert.Contains("320×240", result.Message);
            Assert.Contains("640×480", result.Message);
            Assert.Contains("输入", result.Message);
        }
        using (FlowContext ctx = ImageContext(small))
        {
            ctx.SetVariable(Variable.Object("定位", "HomMat", HomMat2D.Identity, 1));
            tool.MatrixPath = "定位.HomMat";
            NodeResult result = tool.Run(ctx);
            Assert.False(result.IsSuccess);
            Assert.Contains("对齐后", result.Message);
            Assert.Contains("640×480", result.Message);
        }
        FlowResources.Release(tool);
    }

    [Fact]
    public void 定位矩阵对齐_加检测区域_缺陷输出回到当前图像坐标()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var tool = Tool(Train(VariationModelMode.standard));
        // 工件整体平移 (12, -8)：矩阵为“示教位姿 → 当前位姿”
        using HObject shifted = Defective(dr: 12, dc: -8);
        HOperatorSet.HomMat2dIdentity(out HTuple identity);
        HOperatorSet.HomMat2dTranslate(identity, 12, -8, out HTuple translate);

        using (FlowContext ctx = ImageContext(shifted))
        {
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.True(Count(ctx) > 2, "不对齐时亮矩形边缘应被误检");
        }

        tool.MatrixPath = "定位.HomMat";
        using (FlowContext ctx = ImageContext(shifted))
        {
            ctx.SetVariable(Variable.Object("定位", "HomMat", new HomMat2D(translate), 1));
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(2, Count(ctx));
            var region = (HalconRegion)ctx.GetVariable(Module, "DefectRegion").Value;
            HOperatorSet.AreaCenter(region.Object, out HTuple areas, out HTuple rows, out HTuple cols);
            int block = areas.ToDArr().ToList().IndexOf(200);
            Assert.InRange(rows[block].D, 384.5 + 12 - 0.01, 384.5 + 12 + 0.01);
            Assert.InRange(cols[block].D, 69.5 - 8 - 0.01, 69.5 - 8 + 0.01);
        }

        // 检测区域在模型坐标中：只框住矩形块
        HOperatorSet.GenRectangle1(out HObject inspect, 370, 50, 400, 90);
        tool.RegionPath = "区域.Region";
        using (FlowContext ctx = ImageContext(shifted))
        {
            ctx.SetVariable(Variable.Object("定位", "HomMat", new HomMat2D(translate), 1));
            ctx.SetVariable(Variable.Object("区域", "Region", new HalconRegion(inspect), 1));
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(new[] { 200.0 }, Areas(ctx));
        }
        inspect.Dispose();
        FlowResources.Release(tool);
    }

    [Fact]
    public void 检测区域为空按失败_引用无效明确失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var tool = Tool(Train(VariationModelMode.standard));
        tool.RegionPath = "区域.Region";
        using HObject image = Defective();
        HOperatorSet.GenEmptyRegion(out HObject empty);
        using (FlowContext ctx = ImageContext(image))
        {
            ctx.SetVariable(Variable.Object("区域", "Region", new HalconRegion(empty), 0));
            NodeResult result = tool.Run(ctx);
            Assert.False(result.IsSuccess);
            Assert.Contains("检测区域为空", result.Message);
        }
        using (FlowContext ctx = ImageContext(image))
        {
            NodeResult result = tool.Run(ctx);
            Assert.False(result.IsSuccess);
            Assert.Contains("检测区域引用无效", result.Message);
        }
        empty.Dispose();
        FlowResources.Release(tool);
    }

    // ======================= 训练、序列化往返与预览 =======================

    [Fact]
    public void 训练_序列化_反序列化_检测_与直接句柄逐项一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = Defective();
        foreach (VariationModelMode mode in new[] { VariationModelMode.standard, VariationModelMode.robust, VariationModelMode.direct })
        {
            List<HObject> samples = Samples(mode == VariationModelMode.direct ? 1 : 5);
            HOperatorSet.CreateVariationModel(640, 480, "byte", mode.ToString(), out HTuple model);
            if (mode == VariationModelMode.direct)
            {
                HOperatorSet.SobelAmp(samples[0], out HObject edges, "sum_abs", 3);
                HOperatorSet.PrepareDirectVariationModel(samples[0], edges, model, 20, 3);
                edges.Dispose();
            }
            else
            {
                samples.ForEach(s => HOperatorSet.TrainVariationModel(s, model));
                HOperatorSet.PrepareVariationModel(model, 20, 3);
            }
            HOperatorSet.CompareExtVariationModel(image, out HObject diff, model, "absolute");
            HOperatorSet.Connection(diff, out HObject parts);
            HOperatorSet.SelectShape(parts, out HObject kept, "area", "and", 10, int.MaxValue);
            HOperatorSet.AreaCenter(kept, out HTuple expectedAreas, out HTuple expectedRows, out HTuple expectedCols);
            HOperatorSet.ClearVariationModel(model);
            diff.Dispose();
            parts.Dispose();
            kept.Dispose();

            // 训练数据经流程文件保存 / 加载往返
            var tool = Tool(VariationModelTraining.Train(samples, mode), mode);
            samples.ForEach(s => s.Dispose());
            var loaded = (VariationInspectTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(tool)))).Tool;
            Assert.Equal(tool.VariationModelData, loaded.VariationModelData);
            using FlowContext ctx = ImageContext(image);
            Assert.True(loaded.Run(ctx).IsSuccess);
            var region = (HalconRegion)ctx.GetVariable(Module, "DefectRegion").Value;
            HOperatorSet.AreaCenter(region.Object, out HTuple areas, out HTuple rows, out HTuple cols);
            Assert.Equal(expectedAreas.ToDArr(), areas.ToDArr());
            Assert.Equal(expectedRows.ToDArr(), rows.ToDArr());
            Assert.Equal(expectedCols.ToDArr(), cols.ToDArr());
            FlowResources.Release(loaded);
        }
    }

    [Fact]
    public void 训练校验_样本为空_direct只用一张_尺寸不一致报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        Assert.Contains("没有训练样本", Assert.Throws<InvalidOperationException>(() => VariationModelTraining.Train(new List<HObject>(), VariationModelMode.standard)).Message);
        List<HObject> two = Samples(2);
        Assert.Contains("direct 建模只用一张良品图", Assert.Throws<InvalidOperationException>(() => VariationModelTraining.Train(two, VariationModelMode.direct)).Message);
        two.Add(Good(0, 320, 240));
        Assert.Contains("第 3 张样本为 320×240", Assert.Throws<InvalidOperationException>(() => VariationModelTraining.Train(two, VariationModelMode.standard)).Message);
        two.ForEach(s => s.Dispose());
    }

    [Fact]
    public void 预览_标准图为样本均值_偏差图为real_direct标准图即良品图()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        VariationModelTraining.GetPreview(Train(VariationModelMode.standard), out HObject ideal, out HObject variation);
        HOperatorSet.GetGrayval(ideal, 10, 10, out HTuple background);
        HOperatorSet.GetGrayval(ideal, 200, 300, out HTuple pattern);
        Assert.Equal(32, background.I);
        Assert.Equal(212, pattern.I);
        HOperatorSet.GetImageType(variation, out HTuple type);
        Assert.Equal("real", type.S);
        ideal.Dispose();
        variation.Dispose();

        VariationModelTraining.GetPreview(Train(VariationModelMode.direct), out ideal, out variation);
        HOperatorSet.GetGrayval(ideal, 10, 10, out background);
        Assert.Equal(30, background.I);
        ideal.Dispose();
        variation.Dispose();
    }

    [Fact]
    public void 模型体积_标准模型约12字节每像素_10MB提示逻辑()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        byte[] data = Train(VariationModelMode.standard, count: 2);
        Assert.InRange(data.Length, 640 * 480 * 12, 640 * 480 * 12 + 4096);
        Assert.InRange(Train(VariationModelMode.direct).Length, 640 * 480, 640 * 480 + 4096);

        Assert.False(VariationModelTraining.ShouldSuggestModelFile(10L * 1024 * 1024));
        Assert.True(VariationModelTraining.ShouldSuggestModelFile(10L * 1024 * 1024 + 1));
        Assert.Null(VariationModelTraining.LargeModelHint(data.Length));
        string hint = VariationModelTraining.LargeModelHint(12_582_952);
        Assert.Contains("12.00 MB", hint);
        Assert.Contains("外部模型文件", hint);
    }

    [Fact]
    public void 超过10MB的内嵌模型_运行日志提示改用外部文件()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject big = Good(0, 1024, 1024);
        byte[] data = VariationModelTraining.Train(new List<HObject> { big }, VariationModelMode.standard);
        Assert.True(VariationModelTraining.ShouldSuggestModelFile(data.Length));
        var tool = Tool(data);
        using FlowContext ctx = ImageContext(big);
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Contains(ctx.Log, m => m.Contains("超过 10 MB") && m.Contains("外部模型文件"));
        FlowResources.Release(tool);
    }

    // ======================= 登记、持久化、守卫（B5-2-5/7） =======================

    [Fact]
    public void 保存加载_全部参数_校验其余项()
    {
        var tool = new VariationInspectTool(Module)
        {
            ImagePath = "图像1.Image",
            MatrixPath = "匹配1.BestHomMat",
            RegionPath = "区域1.Region",
            ModelMode = VariationModelMode.direct,
            CompareMode = VariationCompareMode.light_dark,
            AbsThreshold = "15,25",
            VarThreshold = "2.5",
            MinDefectArea = 33,
            ModelFile = @"D:\models\a.vfvm"
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ModelMode\": 2", json);
        Assert.Contains("\"CompareMode\": 3", json);
        var loaded = (VariationInspectTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((tool.ImagePath, tool.MatrixPath, tool.RegionPath, tool.ModelMode, tool.CompareMode, tool.AbsThreshold, tool.VarThreshold, tool.MinDefectArea, tool.ModelFile),
            (loaded.ImagePath, loaded.MatrixPath, loaded.RegionPath, loaded.ModelMode, loaded.CompareMode, loaded.AbsThreshold, loaded.VarThreshold, loaded.MinDefectArea, loaded.ModelFile));

        Assert.Contains(ConfigIssues(new VariationInspectTool(Module) { MinDefectArea = -1 }), m => m.Contains("最小缺陷面积不能小于 0"));
    }

    [Fact]
    public void 命名守卫_输出名不与参数名相同_输出声明完整()
    {
        var tool = new VariationInspectTool(Module);
        var properties = new HashSet<string>(tool.GetType().GetProperties().Select(p => p.Name));
        IReadOnlyList<ToolOutputDef> outputs = ToolMetadata.GetOutputs(tool);
        Assert.DoesNotContain(outputs, o => properties.Contains(o.Name));
        Assert.Contains(outputs, o => o.Name == "DefectRegion" && o.ElementClrType == typeof(HalconRegion));
        Assert.Contains(outputs, o => o.Name == "DefectCount" && o.Type == VariableType.Int);
        Assert.Contains(outputs, o => o.Name == "DefectAreas" && o.Kind == VariableKind.Array && o.Type == VariableType.Double);
        Assert.Contains(outputs, o => o.Name == "MaxDefectArea" && o.Type == VariableType.Double);
        Assert.Contains(outputs, o => o.Name == "HasDefect" && o.Type == VariableType.Bool);
        Assert.DoesNotContain(outputs, o => o.Name == "Found");
        Assert.False(typeof(INotFoundPolicy).IsAssignableFrom(typeof(VariationInspectTool)));
    }

    [Fact]
    public void 新工具登记_固定ID_工具箱08缺陷检测()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "variation-inspect");
        Assert.Equal("08 缺陷检测", item.Category);
        Assert.Equal("差分检测", item.DisplayName);
        ToolNode node = Assert.IsType<ToolNode>(item.Factory());
        Assert.IsType<VariationInspectTool>(node.Tool);
        string json = FlowSerializer.SaveNode(node);
        Assert.Contains("\"ToolId\": \"variation-inspect\"", json);
        Assert.IsType<VariationInspectTool>(Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool);
    }
}
