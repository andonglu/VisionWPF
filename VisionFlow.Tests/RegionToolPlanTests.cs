using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>REGION-TOOLS-PLAN RG-01 ~ RG-07：现有区域工具增强、Region / XLD 互转、区域距离与配对规则。</summary>
public class RegionToolPlanTests
{
    // ---------------- 公共构造 ----------------

    private static HObject Rect(double row1, double column1, double row2, double column2)
    {
        HOperatorSet.GenRectangle1(out HObject rect, row1, column1, row2, column2);
        return rect;
    }

    /// <summary>多个矩形拼成一个区域对象组（每个矩形一个对象）。</summary>
    private static HObject Rects(params (double R1, double C1, double R2, double C2)[] rects)
    {
        HOperatorSet.GenEmptyObj(out HObject all);
        foreach (var r in rects)
        {
            using HObject rect = Rect(r.R1, r.C1, r.R2, r.C2);
            HOperatorSet.ConcatObj(all, rect, out HObject joined);
            all.Dispose();
            all = joined;
        }
        return all;
    }

    /// <summary>width×height 的灰度图：背景 background，各矩形区域填 gray。</summary>
    private static HObject Image(int width, int height, int background, params (HObject Region, int Gray)[] paints)
    {
        HOperatorSet.GenImageConst(out HObject image, "byte", width, height);
        HOperatorSet.PaintRegion(Rect(0, 0, height - 1, width - 1), image, out HObject filled, background, "fill");
        image.Dispose();
        image = filled;
        foreach (var paint in paints)
        {
            HOperatorSet.PaintRegion(paint.Region, image, out HObject painted, paint.Gray, "fill");
            image.Dispose();
            image = painted;
        }
        return image;
    }

    private static FlowContext Context(HObject? regions = null, HObject? image = null, HObject? regions2 = null)
    {
        var ctx = new FlowContext();
        if (regions != null)
        {
            HOperatorSet.CountObj(regions, out HTuple count);
            ctx.SetVariable(Variable.Object("上游", "Region", HalconRegion.Owned(regions), count.I));
        }
        if (regions2 != null)
        {
            HOperatorSet.CountObj(regions2, out HTuple count);
            ctx.SetVariable(Variable.Object("上游2", "Region", HalconRegion.Owned(regions2), count.I));
        }
        if (image != null)
        {
            ctx.SetVariable(Variable.Object("Input", "Image", HalconImage.Owned(image), 1));
        }
        return ctx;
    }

    private static int Area(HObject region)
    {
        HOperatorSet.Union1(region, out HObject union);
        using (union)
        {
            HOperatorSet.AreaCenter(union, out HTuple area, out _, out _);
            return area.I;
        }
    }

    private static HObject OutputRegion(FlowContext ctx, string module) => ((HalconRegion)ctx.GetVariable(module, "Region").Value).Object;

    private static double[] Doubles(FlowContext ctx, string module, string name) =>
        ((System.Collections.IEnumerable)ctx.GetVariable(module, name).Value).Cast<object>().Select(Convert.ToDouble).ToArray();

    private static IReadOnlyList<string> ConfigIssues(ToolBase tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root).Issues.Select(i => i.Message).ToList();
    }

    // ---------------- 配对规则 ----------------

    [Theory]
    [InlineData(3, 3, 3)]
    [InlineData(1, 4, 4)]
    [InlineData(4, 1, 4)]
    [InlineData(1, 1, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    public void 配对规则_等长逐一_单个对多(int left, int right, int expected)
    {
        Assert.True(PairingHelper.TryGetPairCount(left, right, out int count, out string? error));
        Assert.Equal(expected, count);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(0, 5)]
    public void 配对规则_个数不等且都不为1_报错(int left, int right)
    {
        Assert.False(PairingHelper.TryGetPairCount(left, right, out _, out string? error));
        Assert.Equal($"两组对象个数不一致（{left} 对 {right}），无法逐一配对", error);
    }

    // ---------------- RG-01 动态阈值 ----------------

    [Fact]
    public void 动态阈值_与均值滤波加dyn_threshold结果一致_UsedThreshold为Offset()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Image(120, 100, 40, (Rect(30, 30, 60, 80), 200));
        using var ctx = Context(image: image);
        var tool = new ThresholdTool("动态1")
        {
            ImagePath = "Input.Image", SegmentMethod = ThresholdSegmentMethod.DynThreshold,
            MaskWidth = 15, MaskHeight = 15, Offset = 10, LightDark = ThresholdLightDark.light
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        HOperatorSet.MeanImage(image, out HObject mean, 15, 15);
        using (mean)
        {
            HOperatorSet.DynThreshold(image, mean, out HObject expected, 10, "light");
            using (expected)
            {
                Assert.Equal(Area(expected), Area(OutputRegion(ctx, "动态1")));
            }
        }
        Assert.True(Area(OutputRegion(ctx, "动态1")) > 0);
        Assert.Equal(10.0, ctx.GetVariable("动态1", "UsedThreshold").Value);
    }

    [Fact]
    public void 二值阈值不支持equal_校验与运行都报错()
    {
        var tool = new ThresholdTool("二值1") { ImagePath = "Input.Image", SegmentMethod = ThresholdSegmentMethod.BinaryThreshold, LightDark = ThresholdLightDark.equal };
        Assert.Contains("二值阈值只支持 light / dark", ConfigIssues(tool));
        using var ctx = new FlowContext();
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("二值阈值只支持 light / dark", result.Message);
    }

    [Fact]
    public void 阈值参数改为枚举_保存的文本与旧版字符串一致_历史文件可加载()
    {
        var tool = new ThresholdTool("阈值1") { LightDark = ThresholdLightDark.dark, BinaryMethod = BinaryThresholdMethod.smooth_histo };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        string compact = new string(json.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains("\"LightDark\":\"dark\"", compact);
        Assert.Contains("\"BinaryMethod\":\"smooth_histo\"", compact);

        // 历史文件中的字符串（含大小写差异）读入同名枚举成员
        string legacy = json.Replace("\"dark\"", "\"Dark\"");
        var loaded = (ThresholdTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy)).Tool;
        Assert.Equal(ThresholdLightDark.dark, loaded.LightDark);
        Assert.Equal(BinaryThresholdMethod.smooth_histo, loaded.BinaryMethod);

        var binary = new BinaryThresholdTool("二值旧") { Method = "smooth_histo" };
        Assert.Equal(BinaryThresholdMethod.smooth_histo, binary.BinaryMethod);
        Assert.Equal("smooth_histo", binary.Method);

        SequenceNode example = FlowSerializer.LoadFile(Path.Combine(RepoPaths.Find("examples/blister-check.vflow.json")));
        Assert.Contains(FlowResources.EnumerateToolNodes(example).Select(n => n.Tool).OfType<ThresholdTool>(),
            t => t.LightDark == ThresholdLightDark.dark);
    }

    [Fact]
    public void 阈值参数按方式显示()
    {
        var tool = new ThresholdTool("阈值1") { SegmentMethod = ThresholdSegmentMethod.DynThreshold };
        Assert.True(tool.IsParameterVisible(nameof(ThresholdTool.Offset)));
        Assert.True(tool.IsParameterVisible(nameof(ThresholdTool.MaskWidth)));
        Assert.True(tool.IsParameterVisible(nameof(ThresholdTool.LightDark)));
        Assert.False(tool.IsParameterVisible(nameof(ThresholdTool.MinGray)));
        Assert.False(tool.IsParameterVisible(nameof(ThresholdTool.StdDevScale)));
        tool.SegmentMethod = ThresholdSegmentMethod.Threshold;
        Assert.True(tool.IsParameterVisible(nameof(ThresholdTool.MinGray)));
        Assert.False(tool.IsParameterVisible(nameof(ThresholdTool.Offset)));
        Assert.True(tool.IsParameterVisible(nameof(ThresholdTool.ImagePath)));
    }

    // ---------------- RG-02 区域处理 ----------------

    [Theory]
    [InlineData(1, 500, true)]
    [InlineData(1, 10, false)]
    public void 按形状填充_只填特征在范围内的孔洞(double fillMin, double fillMax, bool filled)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        // 30×30 方块中间挖 10×10 的孔（面积 100）
        using HObject outer = Rect(10, 10, 39, 39);
        using HObject hole = Rect(20, 20, 29, 29);
        HOperatorSet.Difference(outer, hole, out HObject ring);
        using var ctx = Context(ring);
        var tool = new RegionProcessTool("处理1") { RegionPath = "上游.Region", Method = RegionProcessOp.FillUpShape, FillFeature = RegionFillFeature.area, FillMin = fillMin, FillMax = fillMax };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(filled ? 900 : 800, Area(OutputRegion(ctx, "处理1")));
    }

    [Fact]
    public void 边界_内边界为方块一圈像素()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rect(10, 10, 20, 20));
        var tool = new RegionProcessTool("处理1") { RegionPath = "上游.Region", Method = RegionProcessOp.Boundary, BoundaryType = RegionBoundaryType.inner };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(40, Area(OutputRegion(ctx, "处理1")));
    }

    [Fact]
    public void 按固定宽高切分_与动态切分_切块合起来等于原区域()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rect(0, 0, 19, 39));
        var rectangle = new RegionProcessTool("切分1") { RegionPath = "上游.Region", Method = RegionProcessOp.PartitionRectangle, Width = 10, Height = 10 };
        var dynamic = new RegionProcessTool("切分2") { RegionPath = "上游.Region", Method = RegionProcessOp.PartitionDynamic, Width = 10, Percent = 20 };

        Assert.True(rectangle.Run(ctx).IsSuccess);
        Assert.True(dynamic.Run(ctx).IsSuccess);

        Assert.Equal(8, (int)ctx.GetVariable("切分1", "Count").Value);
        Assert.Equal(800, Area(OutputRegion(ctx, "切分1")));
        Assert.True((int)ctx.GetVariable("切分2", "Count").Value > 1);
        Assert.Equal(800, Area(OutputRegion(ctx, "切分2")));
    }

    [Fact]
    public void 取反_等于裁剪图像范围减去区域()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rects((10, 10, 19, 19), (40, 40, 49, 59)), Image(100, 80, 0));
        var tool = new RegionProcessTool("取反1") { RegionPath = "上游.Region", ClipImagePath = "Input.Image", Method = RegionProcessOp.Complement };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(100 * 80 - 100 - 200, Area(OutputRegion(ctx, "取反1")));
        Assert.Equal(1, (int)ctx.GetVariable("取反1", "Count").Value);
    }

    [Fact]
    public void 取反未指定裁剪图像_校验与运行都报错_工具箱新建时默认Input图像()
    {
        var tool = new RegionProcessTool("取反1") { RegionPath = "上游.Region", Method = RegionProcessOp.Complement };
        Assert.Null(tool.ClipImagePath);
        Assert.Contains("取反需要指定裁剪图像", ConfigIssues(tool));
        using var ctx = new FlowContext();
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Equal("取反需要指定裁剪图像", result.Message);

        // 其他方式不需要裁剪图像
        tool.Method = RegionProcessOp.Boundary;
        Assert.DoesNotContain("取反需要指定裁剪图像", ConfigIssues(tool));
        Assert.False(tool.IsParameterVisible(nameof(RegionProcessTool.ClipImagePath)));

        ToolboxRegistry.RegisterDefaults();
        var created = (RegionProcessTool)((ToolNode)ToolboxRegistry.Items.Single(i => i.Id == "regionprocess").Factory()).Tool;
        Assert.Equal("Input.Image", created.ClipImagePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 区域处理新方式_输入为空按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEmptyObj(out HObject empty);
        using var ctx = Context(empty);
        var tool = new RegionProcessTool("处理1") { RegionPath = "上游.Region", Method = RegionProcessOp.Boundary, FailWhenNotFound = failWhenNotFound };

        NodeResult result = tool.Run(ctx);

        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.False((bool)ctx.GetVariable("处理1", "Found").Value);
    }

    // ---------------- RG-03 区域筛选 ----------------

    private static HObject SampleRegions() => Rects(
        (0, 0, 9, 9),       // 10×10，面积 100
        (20, 0, 29, 29),    // 10×30，面积 300
        (40, 0, 79, 9),     // 40×10，面积 400
        (100, 0, 104, 4));  // 5×5，面积 25

    [Fact]
    public void 多条件and_与串联两个单条件节点结果一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(SampleRegions());
        var multi = new SelectRegionTool("多条件") { RegionPath = "上游.Region", Features = "area, width", Mins = "50, 5", Maxs = "500, 20" };
        var first = new SelectRegionTool("单条件1") { RegionPath = "上游.Region", Feature = "area", Min = 50, Max = 500 };
        var second = new SelectRegionTool("单条件2") { RegionPath = "单条件1.Region", Feature = "width", Min = 5, Max = 20 };

        Assert.True(multi.Run(ctx).IsSuccess);
        Assert.True(first.Run(ctx).IsSuccess);
        Assert.True(second.Run(ctx).IsSuccess);

        Assert.Equal(2, (int)ctx.GetVariable("多条件", "Count").Value);
        Assert.Equal((int)ctx.GetVariable("单条件2", "Count").Value, (int)ctx.GetVariable("多条件", "Count").Value);
        Assert.Equal(Area(OutputRegion(ctx, "单条件2")), Area(OutputRegion(ctx, "多条件")));
    }

    [Fact]
    public void 多条件or_任一满足()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(SampleRegions());
        var tool = new SelectRegionTool("或") { RegionPath = "上游.Region", Features = "area,area", Mins = "0,350", Maxs = "50,1000", Operation = RegionSelectOperation.or };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, (int)ctx.GetVariable("或", "Count").Value);
        Assert.Equal(425, Area(OutputRegion(ctx, "或")));
    }

    [Fact]
    public void 新字段为空_与旧版单条件结果和日志一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject regions = SampleRegions();
        HOperatorSet.SelectShape(regions, out HObject expected, "area", "and", 100, 99999999);
        using var expectedOwner = expected;
        using var ctx = Context(regions);
        var tool = new SelectRegionTool("筛选1") { RegionPath = "上游.Region" };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(Area(expected), Area(OutputRegion(ctx, "筛选1")));
        Assert.Contains(ctx.Log, l => l.Contains("area ∈ [100, 99999999]，取 All"));
    }

    [Theory]
    [InlineData("area,width", "1,2", "10", "下限 2 个、上限 1 个")]
    [InlineData("", "1", "", "特征 0 个")]
    [InlineData("area", "x", "10", "下限第 1 项“x”不是数值")]
    public void 多条件字段不完整_校验与运行都报错(string features, string mins, string maxs, string messagePart)
    {
        var tool = new SelectRegionTool("筛选1") { RegionPath = "上游.Region", Features = features, Mins = mins, Maxs = maxs };
        Assert.Contains(ConfigIssues(tool), m => m.Contains(messagePart));
        using var ctx = new FlowContext();
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains(messagePart, result.Message);
    }

    [Fact]
    public void 按灰度筛选_用select_gray_未指定图像报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject regions = Rects((10, 10, 29, 29), (10, 50, 29, 69));
        HObject image = Image(100, 60, 0, (Rect(10, 10, 29, 29), 80), (Rect(10, 50, 29, 69), 200));
        using var ctx = Context(regions, image);
        var tool = new SelectRegionTool("灰度1") { RegionPath = "上游.Region", ImagePath = "Input.Image", FilterBy = RegionFilterBy.Gray, Feature = "mean", Min = 150, Max = 255 };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(1, (int)ctx.GetVariable("灰度1", "Count").Value);
        HOperatorSet.AreaCenter(OutputRegion(ctx, "灰度1"), out _, out _, out HTuple column);
        Assert.Equal(59.5, column.D, 3);

        tool.ImagePath = null;
        Assert.Contains("按灰度筛选需要指定图像", ConfigIssues(tool));
        Assert.Contains("按灰度筛选需要指定图像", tool.Run(ctx).Message);
        Assert.True(tool.IsParameterVisible(nameof(SelectRegionTool.ImagePath)));
        tool.FilterBy = RegionFilterBy.Shape;
        Assert.False(tool.IsParameterVisible(nameof(SelectRegionTool.ImagePath)));
        Assert.DoesNotContain("按灰度筛选需要指定图像", ConfigIssues(tool));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 按序号取件_先筛选后取_越界按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(SampleRegions());
        // 面积 ≥ 100 的有 3 个（100、300、400），取第 1 个 → 面积 300
        var tool = new SelectRegionTool("取件1") { RegionPath = "上游.Region", TakeMode = RegionTakeMode.ByIndex, TakeIndex = 1, FailWhenNotFound = failWhenNotFound };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(300, Area(OutputRegion(ctx, "取件1")));

        tool.TakeIndex = 3;
        NodeResult result = tool.Run(ctx);
        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.False((bool)ctx.GetVariable("取件1", "Found").Value);
        Assert.Contains("取 第 3 个（共 3 个）", failWhenNotFound ? result.Message : ctx.Log.Last());
    }

    // ---------------- RG-04 区域灰度统计 ----------------

    [Fact]
    public void 灰度统计_均值与标准差与区域逐一对应()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject left = Rect(0, 0, 9, 9);
        HObject image = Image(40, 10, 100, (Rect(0, 20, 9, 39), 200));
        // 第 2 个区域跨两种灰度：一半 100、一半 200 → 均值 150，标准差 50
        HObject regions = Rects((0, 0, 9, 9), (0, 10, 9, 29));
        left.Dispose();
        using var ctx = Context(regions, image);
        var tool = new RegionMinMaxGrayTool("统计1") { RegionPath = "上游.Region" };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(new[] { 100.0, 150.0 }, Doubles(ctx, "统计1", "Means"));
        Assert.Equal(new[] { 0.0, 50.0 }, Doubles(ctx, "统计1", "Deviations"));
        Assert.Equal(new[] { 100.0, 100.0 }, Doubles(ctx, "统计1", "MinGrays"));
        Assert.Equal(new[] { 100.0, 200.0 }, Doubles(ctx, "统计1", "MaxGrays"));
        Assert.Equal(100.0, ctx.GetVariable("统计1", "FirstMean").Value);
        Assert.Equal(0.0, ctx.GetVariable("统计1", "FirstDeviation").Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 灰度统计_输入为空时均值为NaN并按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEmptyObj(out HObject empty);
        using var ctx = Context(empty, Image(10, 10, 0));
        var tool = new RegionMinMaxGrayTool("统计1") { RegionPath = "上游.Region", FailWhenNotFound = failWhenNotFound };

        Assert.Equal(!failWhenNotFound, tool.Run(ctx).IsSuccess);
        Assert.Empty(Doubles(ctx, "统计1", "Means"));
        Assert.True(double.IsNaN((double)ctx.GetVariable("统计1", "FirstMean").Value));
        Assert.True(double.IsNaN((double)ctx.GetVariable("统计1", "FirstDeviation").Value));
        Assert.False((bool)ctx.GetVariable("统计1", "Found").Value);
    }

    // ---------------- RG-05 / RG-06 Region 与 XLD 互转 ----------------

    [Fact]
    public void Region转XLD再转回Region_center往返面积一致_border沿像素外沿_margin只取轮廓线()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rects((10, 10, 29, 39), (50, 50, 59, 59)));
        var toXld = new RegionToXldTool("转XLD") { RegionPath = "上游.Region", Mode = RegionContourMode.center };
        var filled = new XldToRegionTool("转区域") { XldPath = "转XLD.Xld", Mode = XldRegionMode.filled };
        var margin = new XldToRegionTool("轮廓线") { XldPath = "转XLD.Xld", Mode = XldRegionMode.margin };
        var borderXld = new RegionToXldTool("外沿XLD") { RegionPath = "上游.Region", Mode = RegionContourMode.border };
        var borderFilled = new XldToRegionTool("外沿区域") { XldPath = "外沿XLD.Xld", Mode = XldRegionMode.filled };

        foreach (ToolBase tool in new ToolBase[] { toXld, filled, margin, borderXld, borderFilled })
        {
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, tool.ModuleName + "：" + result.Message);
        }

        Assert.Equal(2, (int)ctx.GetVariable("转XLD", "Count").Value);
        Assert.Equal(2, (int)ctx.GetVariable("转区域", "Count").Value);
        HOperatorSet.AreaCenter(OutputRegion(ctx, "转区域"), out HTuple areas, out _, out _);
        Assert.Equal(new[] { 600.0, 100.0 }, areas.ToDArr());
        Assert.True(Area(OutputRegion(ctx, "轮廓线")) < 700);
        // border 轮廓沿像素外沿，填充后每边多出半个像素的范围
        HOperatorSet.AreaCenter(OutputRegion(ctx, "外沿区域"), out HTuple borderAreas, out _, out _);
        Assert.Equal(new[] { 21.0 * 31, 11.0 * 11 }, borderAreas.ToDArr());
        Assert.Equal(typeof(HalconXld), ToolMetadata.GetOutputs(typeof(RegionToXldTool)).Single(o => o.Name == "Xld").ElementClrType);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Region转XLD_输入为空按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEmptyObj(out HObject empty);
        using var ctx = Context(empty);
        var tool = new RegionToXldTool("转XLD") { RegionPath = "上游.Region", FailWhenNotFound = failWhenNotFound };

        Assert.Equal(!failWhenNotFound, tool.Run(ctx).IsSuccess);
        Assert.False((bool)ctx.GetVariable("转XLD", "Found").Value);
    }

    // ---------------- RG-07 区域距离 ----------------

    private static (double Distance, double Row1, double Column1, double Row2, double Column2) Direct(HObject a, HObject b)
    {
        HOperatorSet.DistanceRrMin(a, b, out HTuple d, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
        return (d.D, r1.D, c1.D, r2.D, c2.D);
    }

    private static HObject Pick(HObject regions, int index)
    {
        HOperatorSet.SelectObj(regions, out HObject one, index + 1);
        return one;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    public void 区域到区域_逐一配对与一对多_结果与distance_rr_min一致(int n1, int n2)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject left = Rects(Enumerable.Range(0, n1).Select(i => (i * 30.0, 0.0, i * 30.0 + 9, 9.0)).ToArray());
        HObject right = Rects(Enumerable.Range(0, n2).Select(i => (i * 30.0, 20.0 + i * 5, i * 30.0 + 9, 29.0 + i * 5)).ToArray());
        HOperatorSet.CopyObj(left, out HObject leftCopy, 1, -1);
        HOperatorSet.CopyObj(right, out HObject rightCopy, 1, -1);
        using var leftOwner = leftCopy;
        using var rightOwner = rightCopy;
        using var ctx = Context(left, regions2: right);
        var tool = new RegionDistanceTool("距离1") { RegionPath = "上游.Region", RegionPath2 = "上游2.Region", Mode = RegionDistanceMode.RegionToRegion };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        int pairs = Math.Max(n1, n2);
        Assert.Equal(pairs, (int)ctx.GetVariable("距离1", "Count").Value);
        double[] distances = Doubles(ctx, "距离1", "Distances");
        double[] rows2 = Doubles(ctx, "距离1", "Rows2");
        double[] columns1 = Doubles(ctx, "距离1", "Columns1");
        for (int i = 0; i < pairs; i++)
        {
            using HObject a = Pick(leftCopy, n1 == 1 ? 0 : i);
            using HObject b = Pick(rightCopy, n2 == 1 ? 0 : i);
            var expected = Direct(a, b);
            Assert.Equal(expected.Distance, distances[i], 6);
            Assert.Equal(expected.Row2, rows2[i], 6);
            Assert.Equal(expected.Column1, columns1[i], 6);
        }
        Assert.Equal(distances[0], (double)ctx.GetVariable("距离1", "Distance").Value);
        Assert.Equal(pairs, ctx.GetVariable("距离1", "Segments").Count);
        Assert.Empty(Doubles(ctx, "距离1", "MaxDistances"));
        Assert.True(double.IsNaN((double)ctx.GetVariable("距离1", "MaxDistance").Value));
    }

    [Fact]
    public void 区域到区域_个数不等且都大于1_报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rects((0, 0, 9, 9), (20, 0, 29, 9)), regions2: Rects((0, 20, 9, 29), (20, 20, 29, 29), (40, 20, 49, 29)));
        var tool = new RegionDistanceTool("距离1") { RegionPath = "上游.Region", RegionPath2 = "上游2.Region" };

        NodeResult result = tool.Run(ctx);

        Assert.False(result.IsSuccess);
        Assert.Contains("两组对象个数不一致（2 对 3），无法逐一配对", result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 区域到区域_配对中含空区域_按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEmptyRegion(out HObject emptyRegion);
        using HObject first = Rect(0, 0, 9, 9);
        HOperatorSet.ConcatObj(first, emptyRegion, out HObject left);
        emptyRegion.Dispose();
        using var ctx = Context(left, regions2: Rect(0, 20, 9, 29));
        var tool = new RegionDistanceTool("距离1") { RegionPath = "上游.Region", RegionPath2 = "上游2.Region", FailWhenNotFound = failWhenNotFound };

        NodeResult result = tool.Run(ctx);

        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.Contains("第 1 对含空区域（区域第 1 个）", failWhenNotFound ? result.Message : ctx.Log.Last());
        Assert.False((bool)ctx.GetVariable("距离1", "Found").Value);
        Assert.Empty(Doubles(ctx, "距离1", "Distances"));
    }

    [Fact]
    public void 点到区域_按distance_pr计算_单区域对多个点()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject region = Rect(10, 10, 19, 19);
        HOperatorSet.CopyObj(region, out HObject copy, 1, -1);
        using var copyOwner = copy;
        using var ctx = Context(region);
        ctx.SetVariable(Variable.Array("点", "Rows", VariableType.Double, new[] { 15.0, 15.0 }));
        ctx.SetVariable(Variable.Array("点", "Columns", VariableType.Double, new[] { 0.0, 40.0 }));
        var tool = new RegionDistanceTool("距离1") { RegionPath = "上游.Region", Mode = RegionDistanceMode.PointToRegion, RowPath = "点.Rows", ColumnPath = "点.Columns" };

        Assert.True(tool.Run(ctx).IsSuccess);

        double[] distances = Doubles(ctx, "距离1", "Distances");
        double[] maxDistances = Doubles(ctx, "距离1", "MaxDistances");
        Assert.Equal(2, distances.Length);
        for (int i = 0; i < 2; i++)
        {
            HOperatorSet.DistancePr(copy, 15.0, i == 0 ? 0.0 : 40.0, out HTuple min, out HTuple max);
            Assert.Equal(min.D, distances[i], 6);
            Assert.Equal(max.D, maxDistances[i], 6);
        }
        Assert.Empty(Doubles(ctx, "距离1", "Rows1"));
        Assert.Equal(0, ctx.GetVariable("距离1", "Segments").Count);
    }

    [Fact]
    public void 点到区域_行列个数不一致报错_未指定点时校验报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rect(10, 10, 19, 19));
        ctx.SetVariable(Variable.Array("点", "Rows", VariableType.Double, new[] { 1.0, 2.0, 3.0 }));
        ctx.SetVariable(Variable.Single("点", "Column", VariableType.Double, 5.0));
        var tool = new RegionDistanceTool("距离1") { RegionPath = "上游.Region", Mode = RegionDistanceMode.PointToRegion, RowPath = "点.Rows", ColumnPath = "点.Column" };

        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("点的行、列个数不一致（行 3 个，列 1 个）", result.Message);

        var unset = new RegionDistanceTool("距离2") { RegionPath = "上游.Region", Mode = RegionDistanceMode.PointToRegion };
        Assert.Contains("点到区域需要指定点的行、列坐标", ConfigIssues(unset));
        Assert.True(unset.IsParameterVisible(nameof(RegionDistanceTool.RowPath)));
        Assert.False(unset.IsParameterVisible(nameof(RegionDistanceTool.RegionPath2)));
        unset.Mode = RegionDistanceMode.RegionToRegion;
        Assert.Contains("区域到区域需要指定区域 2", ConfigIssues(unset));
    }

    // ---------------- 注册 ----------------

    [Fact]
    public void 新工具登记固定ID并进入工具箱_可保存加载()
    {
        ToolboxRegistry.RegisterDefaults();
        foreach (var (id, category, type) in new[]
                 {
                     ("region-to-xld", "04 XLD轮廓", typeof(RegionToXldTool)),
                     ("xld-to-region", "03 区域处理", typeof(XldToRegionTool)),
                     ("region-distance", "05 几何测量", typeof(RegionDistanceTool))
                 })
        {
            ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == id);
            Assert.Equal(category, item.Category);
            ToolNode node = Assert.IsType<ToolNode>(item.Factory());
            Assert.IsType(type, node.Tool);
            string json = FlowSerializer.SaveNode(node);
            Assert.Contains($"\"ToolId\": \"{id}\"", json);
            Assert.IsType(type, Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool);
        }
        Assert.Equal("区域灰度统计", ToolboxRegistry.Items.Single(i => i.Id == "region-min-max-gray").DisplayName);
        var select = (SelectRegionTool)((ToolNode)ToolboxRegistry.Items.Single(i => i.Id == "selectregion").Factory()).Tool;
        Assert.Equal("Input.Image", select.ImagePath);
        Assert.Null(new SelectRegionTool("历史").ImagePath);
    }
}
