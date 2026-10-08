using System.Text.Json.Nodes;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>MATCH-MEASURE-TOOLS-PLAN 第二批：匹配窗口“执行测试”回归修复、MS-02（卡尺边缘对宽度）、MS-07（单位换算，Fixed 当量）。</summary>
public class MatchMeasureBatch2Tests
{
    private static FlowContext ImageContext(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static double Single(FlowContext ctx, string module, string name) => Convert.ToDouble(ctx.GetVariable(module, name).Value);

    private static double[] Doubles(FlowContext ctx, string module, string name) =>
        ((System.Collections.IEnumerable)ctx.GetVariable(module, name).Value).Cast<object>().Select(Convert.ToDouble).ToArray();

    private static IReadOnlyList<string> ConfigIssues(ToolBase tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root).Issues.Select(i => i.Message).ToList();
    }

    private static Dictionary<string, object?> Snapshot(ToolBase tool) =>
        tool.GetType().GetProperties()
            .Where(p => p.CanRead && p.CanWrite && (p.PropertyType == typeof(string) || p.PropertyType == typeof(int)
                || p.PropertyType == typeof(double) || p.PropertyType == typeof(bool) || p.PropertyType == typeof(byte[]) || p.PropertyType.IsEnum))
            .ToDictionary(p => p.Name, p => p.GetValue(tool));

    private static void AssertSameConfiguration(Dictionary<string, object?> expected, ToolBase tool)
    {
        foreach (KeyValuePair<string, object?> pair in Snapshot(tool))
        {
            Assert.True(Equals(expected[pair.Key], pair.Value) || (expected[pair.Key] is byte[] a && pair.Value is byte[] b && a.SequenceEqual(b)),
                $"参数 {pair.Key} 被修改：{expected[pair.Key]} → {pair.Value}");
        }
    }

    // ======================= B2-0 回归修复：执行测试不修改原工具 =======================

    /// <summary>三个 L 形目标的图像与用中间目标示教的形状模型。</summary>
    private static HObject TargetsImage(out byte[] modelData)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", 400, 160);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, 159, 399);
        HOperatorSet.PaintRegion(all, blank, out HObject image, 30, "fill");
        blank.Dispose();
        all.Dispose();
        foreach (double c in new[] { 40.0, 170.0, 300.0 })
        {
            HOperatorSet.GenRectangle1(out HObject vertical, 50, c, 90, c + 8);
            HOperatorSet.GenRectangle1(out HObject horizontal, 82, c, 90, c + 30);
            HOperatorSet.Union2(vertical, horizontal, out HObject shape);
            HOperatorSet.PaintRegion(shape, image, out HObject painted, 220, "fill");
            foreach (HObject o in new[] { vertical, horizontal, shape, image }) o.Dispose();
            image = painted;
        }
        HOperatorSet.GenRectangle1(out HObject roi, 40, 160, 100, 210);
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        roi.Dispose();
        HOperatorSet.CreateShapeModel(template, "auto", -0.2, 0.4, "auto", "auto", "use_polarity", "auto", "auto", out HTuple modelId);
        template.Dispose();
        try
        {
            modelData = ShapeModelSerialization.Serialize(modelId);
        }
        finally
        {
            HOperatorSet.ClearShapeModel(modelId);
        }
        return image;
    }

    [Fact]
    public void 执行测试路径_在一次性副本上运行_不修改原工具任何参数()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage(out byte[] data);
        var tool = new HalconModelMatchTool("匹配1")
        {
            ImagePath = "Input.Image", ShapeModelData = data, NumMatches = 10, MinScore = 0.5,
            FindStartAngle = -0.2, FindExtentAngle = 0.4
        };
        Dictionary<string, object?> before = Snapshot(tool);
        using var lastRun = new FlowContext();

        using (ToolTestRun run = ToolTestRun.Run(tool, copy =>
               {
                   var edited = (HalconModelMatchTool)copy;
                   edited.ModuleName = "改名匹配";
                   edited.NumMatches = 2;
                   edited.MinScore = 0.6;
                   edited.SortBy = MatchSortBy.Column;
                   edited.RowTolerance = 3;
                   edited.FailWhenNotFound = false;
                   edited.Greediness = 0.8;
               }, lastRun, image))
        {
            Assert.True(run.Result.IsSuccess, run.Result.Message);
            Assert.Equal("改名匹配", run.ModuleName);
            // 运行用的是界面上的新参数：数量 2、按列排序
            Assert.Equal(2, (int)run.Context.GetVariable("改名匹配", "MatchCount").Value);
            double[] columns = ((System.Collections.IEnumerable)run.Context.GetVariable("改名匹配", "Items").Value)
                .Cast<MatchResultItem>().Select(i => i.Column).ToArray();
            Assert.True(columns[0] < columns[1]);
            Assert.False(run.Context.TryGetVariable("匹配1", "MatchCount", out _));
        }

        Assert.Equal("匹配1", tool.ModuleName);
        AssertSameConfiguration(before, tool);
        Assert.False(lastRun.TryGetVariable("改名匹配", "MatchCount", out _));
    }

    [Fact]
    public void 执行测试路径_界面参数解析失败时原工具不变()
    {
        var tool = new HalconModelMatchTool("匹配1") { NumMatches = 3 };
        Dictionary<string, object?> before = Snapshot(tool);
        Assert.Throws<InvalidOperationException>(() => ToolTestRun.Run(tool, copy =>
        {
            ((HalconModelMatchTool)copy).NumMatches = 7;
            throw new InvalidOperationException("最小分 不是有效数字。");
        }, null, null));
        AssertSameConfiguration(before, tool);
    }

    [Fact]
    public void 编辑事务_执行测试后取消_节点参数不变()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage(out byte[] data);
        var node = new ToolNode(new HalconModelMatchTool("匹配1") { ImagePath = "Input.Image", ShapeModelData = data, NumMatches = 10, MinScore = 0.5 });
        Dictionary<string, object?> before = Snapshot(node.Tool);
        using (var transaction = new ToolEditTransaction(node, new ToolEditContext { Node = node, InputImage = image }))
        {
            using ToolTestRun run = ToolTestRun.Run(transaction.WorkingCopy, copy => ((HalconModelMatchTool)copy).NumMatches = 1,
                null, image);
            Assert.Equal(1, (int)run.Context.GetVariable("匹配1", "MatchCount").Value);
            Assert.Equal(10, ((HalconModelMatchTool)transaction.WorkingCopy).NumMatches);
            // 不提交（取消）
        }
        AssertSameConfiguration(before, node.Tool);
    }

    // ======================= MS-02 卡尺边缘对 =======================

    /// <summary>竖直亮条（列 [c1, c2)，整幅高度），背景 30、亮条 220：边缘位于 c1 - 0.5 与 c2 - 0.5。</summary>
    private static HObject StripesImage(params (int C1, int C2)[] stripes)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", 400, 200);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, 199, 399);
        HOperatorSet.PaintRegion(all, blank, out HObject image, 30, "fill");
        blank.Dispose();
        all.Dispose();
        foreach (var s in stripes)
        {
            HOperatorSet.GenRectangle1(out HObject stripe, 0, s.C1, 199, s.C2 - 1);
            HOperatorSet.PaintRegion(stripe, image, out HObject painted, 220, "fill");
            stripe.Dispose();
            image.Dispose();
            image = painted;
        }
        return image;
    }

    private static readonly (int, int)[] Stripes = { (100, 120), (150, 170), (250, 290) };

    private static OneDCaliperFollowMeasureTool Caliper(EdgeMode mode = EdgeMode.Edge, string transition = "all") => new("卡尺1")
    {
        BaseRow = 100, BaseColumn = 200, BasePhi = 0, BaseLength1 = 150, BaseLength2 = 8,
        EdgeMode = mode, MeasureTransition = transition
    };

    private static void DirectMeasure(HObject image, Action<HTuple> measure, double row = 100, double col = 200, double phi = 0,
        double length1 = 150, double length2 = 8)
    {
        HOperatorSet.GenMeasureRectangle2(row, col, phi, length1, length2, 400, 200, "nearest_neighbor", out HTuple handle);
        try
        {
            measure(handle);
        }
        finally
        {
            HOperatorSet.CloseMeasure(handle);
        }
    }

    [Fact]
    public void 兼容门禁_一维卡尺单边缘模式与旧版measure_pos逐项一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = StripesImage(Stripes);
        var tool = Caliper();
        Assert.Equal(EdgeMode.Edge, new OneDCaliperFollowMeasureTool("x").EdgeMode);
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        DirectMeasure(image, handle =>
        {
            HOperatorSet.MeasurePos(image, handle, 1.0, 20.0, "all", "all", out HTuple rows, out HTuple columns, out HTuple amplitudes, out HTuple distances);
            Assert.Equal(rows.DArr, Doubles(ctx, "卡尺1", "Rows"));
            Assert.Equal(columns.DArr, Doubles(ctx, "卡尺1", "Columns"));
            Assert.Equal(amplitudes.DArr, Doubles(ctx, "卡尺1", "Amplitudes"));
            Assert.Equal(distances.DArr, Doubles(ctx, "卡尺1", "Distances"));
            Assert.Equal(rows.Length, (int)ctx.GetVariable("卡尺1", "Count").Value);
            Assert.Equal(columns[0].D, Single(ctx, "卡尺1", "FirstColumn"));
            Assert.Equal(distances[0].D, Single(ctx, "卡尺1", "FirstDistance"));
            var results = (List<OneDCaliperMeasureResult>)ctx.GetVariable("卡尺1", "Results").Value;
            Assert.Equal(columns.DArr, results.Select(r => r.Column).ToArray());
        });
        Assert.Equal(6, Doubles(ctx, "卡尺1", "Columns").Length);
        // 边缘对输出在单边缘模式写空
        Assert.Empty(Doubles(ctx, "卡尺1", "Widths"));
        Assert.Empty(Doubles(ctx, "卡尺1", "Gaps"));
        Assert.Empty(Doubles(ctx, "卡尺1", "PairCenterRows"));
        Assert.Empty(Doubles(ctx, "卡尺1", "PairCenterColumns"));
        Assert.True(double.IsNaN(Single(ctx, "卡尺1", "FirstWidth")));
        Assert.Equal(0, (int)ctx.GetVariable("卡尺1", "PairCount").Value);
        Assert.Empty((List<OneDCaliperPairResult>)ctx.GetVariable("卡尺1", "PairResults").Value);
    }

    [Fact]
    public void 兼容门禁_圆弧卡尺单边缘模式与旧版逐卡尺measure_pos一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = RingImage();
        var tool = new ArcCaliperFollowMeasureTool("圆弧1") { BaseRow = 100, BaseColumn = 200, BaseRadius = 47, CaliperCount = 8, MeasureLength1 = 20, MeasureLength2 = 3 };
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        var expectedColumns = new List<double>();
        var expectedDistances = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            double phi = ArcCaliperFollowMeasureTool.CaliperPhi(0, Math.PI * 2, 8, i);
            HomMat2D.PointOnCircle(100, 200, 47, phi, out double r, out double c);
            DirectMeasure(image, handle =>
            {
                HOperatorSet.MeasurePos(image, handle, 1.0, 20.0, "all", "all", out _, out HTuple columns, out _, out HTuple distances);
                expectedColumns.AddRange(columns.DArr);
                expectedDistances.AddRange(distances.Length > 0 ? distances.DArr : Array.Empty<double>());
            }, r, c, phi, 20, 3);
        }
        Assert.Equal(expectedColumns, Doubles(ctx, "圆弧1", "Columns"));
        Assert.Equal(expectedDistances, Doubles(ctx, "圆弧1", "Distances"));
        Assert.Equal(0, (int)ctx.GetVariable("圆弧1", "PairCount").Value);
    }

    [Fact]
    public void 边缘对_宽度间距中心与直接调用measure_pairs一致_边缘输出依次写两条边缘()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = StripesImage(Stripes);
        var tool = Caliper(EdgeMode.Pair, "positive");
        using FlowContext ctx = ImageContext(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);

        DirectMeasure(image, handle =>
        {
            HOperatorSet.MeasurePairs(image, handle, 1.0, 20.0, "positive", "all",
                out HTuple r1, out HTuple c1, out HTuple a1, out HTuple r2, out HTuple c2, out HTuple a2, out HTuple intra, out HTuple inter);
            Assert.Equal(intra.DArr, Doubles(ctx, "卡尺1", "Widths"));
            Assert.Equal(inter.DArr, Doubles(ctx, "卡尺1", "Gaps"));
            Assert.Equal(c1.DArr.Zip(c2.DArr, (x, y) => (x + y) / 2).ToArray(), Doubles(ctx, "卡尺1", "PairCenterColumns"));
            Assert.Equal(r1.DArr.Zip(r2.DArr, (x, y) => (x + y) / 2).ToArray(), Doubles(ctx, "卡尺1", "PairCenterRows"));
            Assert.Equal(c1.DArr.Zip(c2.DArr, (x, y) => new[] { x, y }).SelectMany(p => p).ToArray(), Doubles(ctx, "卡尺1", "Columns"));
            Assert.Equal(a1.DArr.Zip(a2.DArr, (x, y) => new[] { x, y }).SelectMany(p => p).ToArray(), Doubles(ctx, "卡尺1", "Amplitudes"));
        });

        // 三条亮带：宽度 20 / 20 / 40，间距 30 / 80
        Assert.Equal(new[] { 20.0, 20.0, 40.0 }, Doubles(ctx, "卡尺1", "Widths").Select(w => Math.Round(w, 1)));
        Assert.Equal(new[] { 30.0, 80.0 }, Doubles(ctx, "卡尺1", "Gaps").Select(w => Math.Round(w, 1)));
        Assert.Equal(new[] { 109.5, 159.5, 269.5 }, Doubles(ctx, "卡尺1", "PairCenterColumns").Select(w => Math.Round(w, 1)));
        Assert.Equal(3, (int)ctx.GetVariable("卡尺1", "PairCount").Value);
        Assert.Equal(20.0, Math.Round(Single(ctx, "卡尺1", "FirstWidth"), 1));
        // 边缘输出：2 × PairCount 个点，Distances 为宽度、间距交替
        Assert.Equal(6, (int)ctx.GetVariable("卡尺1", "Count").Value);
        Assert.Equal(new[] { 20.0, 30.0, 20.0, 80.0, 40.0 }, Doubles(ctx, "卡尺1", "Distances").Select(w => Math.Round(w, 1)));
        Assert.Equal(99.5, Math.Round(Single(ctx, "卡尺1", "FirstColumn"), 1));
        Assert.Equal(20.0, Math.Round(Single(ctx, "卡尺1", "FirstDistance"), 1));
        var edges = (List<OneDCaliperMeasureResult>)ctx.GetVariable("卡尺1", "Results").Value;
        Assert.Equal(Enumerable.Range(0, 6), edges.Select(e => e.EdgeIndex));
        Assert.True(double.IsNaN(edges[^1].Distance));

        var pairs = (List<OneDCaliperPairResult>)ctx.GetVariable("卡尺1", "PairResults").Value;
        Assert.Equal(3, pairs.Count);
        Assert.Equal(new[] { 0, 1, 2 }, pairs.Select(p => p.PairIndex));
        Assert.All(pairs, p => Assert.Equal(0, p.CaliperIndex));
        Assert.Equal(249.5, Math.Round(pairs[2].Column1, 1));
        Assert.Equal(289.5, Math.Round(pairs[2].Column2, 1));
        Assert.Equal(40.0, Math.Round(pairs[2].Width, 1));
        Assert.Contains("找到边缘对数=3", string.Join("\n", ctx.Log));
    }

    [Theory]
    [InlineData("all_strongest")]
    [InlineData("positive_strongest")]
    [InlineData("negative_strongest")]
    [InlineData("negative")]
    [InlineData("all")]
    public void 边缘对_各边缘极性与直接调用一致(string transition)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = StripesImage(Stripes);
        using FlowContext ctx = ImageContext(image);
        var tool = Caliper(EdgeMode.Pair, transition);
        tool.FailWhenNotFound = false;
        Assert.Empty(tool.CheckConfiguration());
        Assert.True(tool.Run(ctx).IsSuccess);
        DirectMeasure(image, handle =>
        {
            HOperatorSet.MeasurePairs(image, handle, 1.0, 20.0, transition, "all",
                out _, out HTuple c1, out _, out _, out HTuple c2, out _, out HTuple intra, out _);
            Assert.Equal(intra.DArr, Doubles(ctx, "卡尺1", "Widths"));
            Assert.Equal(c1.Length, (int)ctx.GetVariable("卡尺1", "PairCount").Value);
        });
    }

    [Fact]
    public void 边缘对_边缘选择first与last()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = StripesImage(Stripes);
        foreach ((string select, double width) in new[] { ("first", 20.0), ("last", 40.0) })
        {
            using FlowContext ctx = ImageContext(image);
            var tool = Caliper(EdgeMode.Pair, "positive");
            tool.MeasureSelect = select;
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(new[] { width }, Doubles(ctx, "卡尺1", "Widths").Select(w => Math.Round(w, 1)));
        }
    }

    /// <summary>圆环：圆心 (100, 200)，亮环内半径 40、外半径 55。</summary>
    private static HObject RingImage()
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", 400, 200);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, 199, 399);
        HOperatorSet.PaintRegion(all, blank, out HObject background, 30, "fill");
        HOperatorSet.GenCircle(out HObject outer, 100, 200, 55);
        HOperatorSet.GenCircle(out HObject inner, 100, 200, 40);
        HOperatorSet.Difference(outer, inner, out HObject ring);
        HOperatorSet.PaintRegion(ring, background, out HObject image, 220, "fill");
        foreach (HObject o in new[] { blank, all, background, outer, inner, ring }) o.Dispose();
        return image;
    }

    [Fact]
    public void 圆弧卡尺边缘对_每个径向卡尺测出环宽_与逐卡尺直接调用一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = RingImage();
        var tool = new ArcCaliperFollowMeasureTool("圆弧1")
        {
            BaseRow = 100, BaseColumn = 200, BaseRadius = 47, CaliperCount = 8, MeasureLength1 = 20, MeasureLength2 = 3,
            EdgeMode = EdgeMode.Pair
        };
        using FlowContext ctx = ImageContext(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);

        var expected = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            double phi = ArcCaliperFollowMeasureTool.CaliperPhi(0, Math.PI * 2, 8, i);
            HomMat2D.PointOnCircle(100, 200, 47, phi, out double r, out double c);
            DirectMeasure(image, handle =>
            {
                HOperatorSet.MeasurePairs(image, handle, 1.0, 20.0, "all", "all", out _, out _, out _, out _, out _, out _, out HTuple intra, out _);
                expected.AddRange(intra.DArr);
            }, r, c, phi, 20, 3);
        }
        double[] widths = Doubles(ctx, "圆弧1", "Widths");
        Assert.Equal(expected, widths);
        Assert.Equal(8, widths.Length);
        // 像素化圆环的径向宽度约 15（轴向精确，斜向受离散化影响）
        Assert.All(widths, w => Assert.InRange(w, 14.0, 16.5));
        var pairs = (List<OneDCaliperPairResult>)ctx.GetVariable("圆弧1", "PairResults").Value;
        Assert.Equal(Enumerable.Range(0, 8), pairs.Select(p => p.CaliperIndex));
        Assert.Equal(16, Doubles(ctx, "圆弧1", "Rows").Length);
        // 每对中心都在半径约 47.5 的圆上
        Assert.All(pairs, p => Assert.InRange(Math.Sqrt(Math.Pow(p.CenterRow - 100, 2) + Math.Pow(p.CenterColumn - 200, 2)), 46.5, 48.5));
        Assert.Contains("找到边缘对数=8", string.Join("\n", ctx.Log));
    }

    [Fact]
    public void 边缘对显示轮廓_在卡尺矩形和边缘十字之外加尺寸线()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = StripesImage(Stripes);
        using FlowContext pairCtx = ImageContext(image);
        Assert.True(Caliper(EdgeMode.Pair, "positive").Run(pairCtx).IsSuccess);
        HObject contour = ((HalconXld)pairCtx.GetVariable("卡尺1", "ResultContour").Value).Object;

        double[] rows = Doubles(pairCtx, "卡尺1", "Rows");
        double[] columns = Doubles(pairCtx, "卡尺1", "Columns");
        HOperatorSet.GenRectangle2ContourXld(out HObject rectangle, 100, 200, 0, 150, 8);
        HOperatorSet.GenCrossContourXld(out HObject crosses, new HTuple(rows), new HTuple(columns), 12, 0);
        HOperatorSet.CountObj(rectangle, out HTuple rectangleCount);
        HOperatorSet.CountObj(crosses, out HTuple crossCount);
        rectangle.Dispose();
        crosses.Dispose();
        HOperatorSet.CountObj(contour, out HTuple total);
        // 每对 3 段：连线 + 两端挡线
        Assert.Equal(rectangleCount.I + crossCount.I + 3 * 3, total.I);

        var segments = new List<(double R1, double C1, double R2, double C2)>();
        for (int i = rectangleCount.I + crossCount.I + 1; i <= total.I; i++)
        {
            HOperatorSet.SelectObj(contour, out HObject segment, i);
            HOperatorSet.GetContourXld(segment, out HTuple r, out HTuple c);
            segment.Dispose();
            Assert.Equal(2, r.Length);
            segments.Add((r[0].D, c[0].D, r[1].D, c[1].D));
        }
        // 第一对：连线 (100, 99.5) → (100, 119.5)；挡线为竖直方向（垂直于测量方向 phi = 0）
        Assert.Equal(100, segments[0].R1, 3);
        Assert.Equal(columns[0], segments[0].C1, 6);
        Assert.Equal(columns[1], segments[0].C2, 6);
        Assert.Equal(segments[1].C1, segments[1].C2, 6);
        Assert.Equal(columns[0], segments[1].C1, 6);
        // 挡线半长 = 卡尺半宽 8 + 十字大小 12
        Assert.Equal(40, Math.Abs(segments[1].R2 - segments[1].R1), 6);
    }

    [Fact]
    public void 边缘对未找到_按未找到策略_输出为空()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = StripesImage();
        foreach (bool failWhenNotFound in new[] { true, false })
        {
            using FlowContext ctx = ImageContext(image);
            var tool = Caliper(EdgeMode.Pair, "positive");
            tool.FailWhenNotFound = failWhenNotFound;
            NodeResult result = tool.Run(ctx);
            Assert.Equal(!failWhenNotFound, result.IsSuccess);
            if (failWhenNotFound)
            {
                Assert.Contains("未找到边缘对", result.Message);
            }
            Assert.False((bool)ctx.GetVariable("卡尺1", "Found").Value);
            Assert.Equal(0, (int)ctx.GetVariable("卡尺1", "PairCount").Value);
            Assert.Empty(Doubles(ctx, "卡尺1", "Widths"));
            Assert.True(double.IsNaN(Single(ctx, "卡尺1", "FirstWidth")));
        }
    }

    [Fact]
    public void 同一上下文先边缘对后单边缘_边缘对输出不残留()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = StripesImage(Stripes);
        using FlowContext ctx = ImageContext(image);
        var tool = Caliper(EdgeMode.Pair, "positive");
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(3, (int)ctx.GetVariable("卡尺1", "PairCount").Value);
        tool.EdgeMode = EdgeMode.Edge;
        tool.MeasureTransition = "all";
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(0, (int)ctx.GetVariable("卡尺1", "PairCount").Value);
        Assert.Empty(Doubles(ctx, "卡尺1", "Widths"));
        Assert.True(double.IsNaN(Single(ctx, "卡尺1", "FirstWidth")));
    }

    [Fact]
    public void 边缘模式校验_按模式收紧边缘极性_运行时同样拒绝()
    {
        Assert.Contains(ConfigIssues(Caliper(EdgeMode.Edge, "all_strongest")), m => m.Contains("*_strongest 只用于边缘对模式"));
        Assert.Contains(ConfigIssues(new ArcCaliperFollowMeasureTool("圆弧1") { MeasureTransition = "positive_strongest" }), m => m.Contains("单边缘模式的边缘极性"));
        Assert.Empty(ConfigIssues(Caliper(EdgeMode.Pair, "all_strongest")));
        Assert.Contains(ConfigIssues(Caliper(EdgeMode.Pair, "uniform")), m => m.Contains("边缘对模式的边缘极性"));
        var badSelect = Caliper(EdgeMode.Pair);
        badSelect.MeasureSelect = "middle";
        Assert.Contains(ConfigIssues(badSelect), m => m.Contains("边缘选择只能是"));
        foreach (string transition in CaliperMeasureToolBase.EdgeTransitions)
        {
            Assert.Empty(ConfigIssues(Caliper(EdgeMode.Edge, transition)));
        }

        NodeResult result = Caliper(EdgeMode.Edge, "all_strongest").Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Contains("MeasureTransition", result.Message);
        Assert.Equal(new[] { "all", "positive", "negative" }, CaliperMeasureToolBase.TransitionsFor(EdgeMode.Edge));
        Assert.Equal(6, CaliperMeasureToolBase.TransitionsFor(EdgeMode.Pair).Count);
    }

    [Fact]
    public void 边缘对输出只在边缘对模式进入引用候选()
    {
        var caliper = Caliper();
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(caliper));
        var consumer = new ToolNode(new AngleConvertTool("换算1"));
        root.Children.Add(consumer);

        List<string> edgePaths = RefCandidateService.ForNode(root, consumer).Select(c => c.Path).ToList();
        Assert.Contains("卡尺1.Rows", edgePaths);
        Assert.DoesNotContain(edgePaths, p => CaliperMeasureToolBase.PairOutputNames.Any(n => p == "卡尺1." + n));
        Assert.False(caliper.IsParameterVisible("Widths"));
        Assert.True(caliper.IsParameterVisible("EdgeMode"));

        caliper.EdgeMode = EdgeMode.Pair;
        List<string> pairPaths = RefCandidateService.ForNode(root, consumer).Select(c => c.Path).ToList();
        Assert.All(CaliperMeasureToolBase.PairOutputNames, n => Assert.Contains("卡尺1." + n, pairPaths));
        Assert.True(RefCandidateService.ForNode(root, consumer).Single(c => c.Path == "卡尺1.Widths").IsCollection);

        // 声明级校验与下拉共用作用域：单边缘模式引用边缘对输出时报错，边缘对模式通过
        ((AngleConvertTool)consumer.Tool).ValuePath = "卡尺1.Widths";
        Assert.DoesNotContain(FlowValidator.Validate(root).Issues, i => i.Message.Contains("卡尺1.Widths"));
        caliper.EdgeMode = EdgeMode.Edge;
        Assert.Contains(FlowValidator.Validate(root).Issues, i => i.Message.Contains("卡尺1.Widths"));
        caliper.EdgeMode = EdgeMode.Pair;
        foreach (string name in CaliperMeasureToolBase.PairOutputNames)
        {
            Assert.Contains(ToolMetadata.GetOutputs(typeof(ArcCaliperFollowMeasureTool)), o => o.Name == name);
        }
    }

    [Fact]
    public void 按输出名隐藏不误伤现有工具_任何工具的输出名都不与其参数名相同()
    {
        ToolboxRegistry.RegisterDefaults();
        foreach (ToolboxItem item in ToolboxRegistry.Items)
        {
            if (!(item.Factory() is ToolNode node) || !(node.Tool is IToolParameterVisibility))
            {
                continue;
            }
            var properties = new HashSet<string>(node.Tool.GetType().GetProperties().Select(p => p.Name));
            Assert.DoesNotContain(ToolMetadata.GetOutputs(node.Tool), o => properties.Contains(o.Name));
        }
    }

    [Fact]
    public void 边缘模式保存加载_按数字保存_历史文件缺省为单边缘()
    {
        string json = FlowSerializer.SaveNode(new ToolNode(Caliper(EdgeMode.Pair, "all_strongest")));
        Assert.Contains("\"EdgeMode\": 1", json);
        var loaded = (OneDCaliperFollowMeasureTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal(EdgeMode.Pair, loaded.EdgeMode);
        Assert.Equal("all_strongest", loaded.MeasureTransition);

        var node = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new ArcCaliperFollowMeasureTool("圆弧1"))))!.AsObject();
        Assert.True(node["Tool"]!["Properties"]!.AsObject().Remove("EdgeMode"));
        var legacy = (ArcCaliperFollowMeasureTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(node.ToJsonString())).Tool;
        Assert.Equal(EdgeMode.Edge, legacy.EdgeMode);
        Assert.Equal(1, (int)EdgeMode.Pair);
    }

    // ======================= MS-07 单位换算 =======================

    /// <summary>第一批之前的角度换算实现（逐字照抄），用作回归对照。</summary>
    private static double LegacyAngleConvert(double value, AngleUnit input, AngleUnit output, AngleRange range, double rangeMin, double rangeMax)
    {
        double degrees = input == AngleUnit.Radian ? value * 180.0 / Math.PI : value;
        if (AngleMath.TryGetBounds(range, rangeMin, rangeMax, out double min, out double max))
        {
            degrees = AngleMath.Fold(degrees, min, max);
        }
        return output == AngleUnit.Radian ? degrees * Math.PI / 180.0 : degrees;
    }

    [Fact]
    public void 兼容门禁_角度模式与旧版逐项一致()
    {
        double[] inputs = { -7.5, -3.2, -1.0, -0.25, 0, 0.3, 1.57, 3.14159, 6.5, 100, double.NaN };
        foreach (AngleUnit input in Enum.GetValues<AngleUnit>())
        foreach (AngleUnit output in Enum.GetValues<AngleUnit>())
        foreach (AngleRange range in Enum.GetValues<AngleRange>())
        {
            var tool = new AngleConvertTool("角度1") { InputUnit = input, OutputUnit = output, Range = range, RangeMin = -30, RangeMax = 150, PixelSize = 0.5, IsArea = true };
            Assert.Equal(ConvertKind.Angle, tool.ConvertKind);
            foreach (double value in inputs)
            {
                Assert.Equal(LegacyAngleConvert(value, input, output, range, -30, 150), tool.Convert(value));
            }
        }

        var angle = new AngleConvertTool("角度1") { ValuePath = "测量.Phis", Range = AngleRange.Minus90To90 };
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Array("测量", "Phis", VariableType.Double, new[] { 0.5, 2.0, double.NaN }));
        Assert.True(angle.Run(ctx).IsSuccess);
        Assert.Equal(new[] { 0.5, 2.0, double.NaN }.Select(v => LegacyAngleConvert(v, AngleUnit.Radian, AngleUnit.Degree, AngleRange.Minus90To90, -90, 90)),
            Doubles(ctx, "角度1", "Values"));
        Assert.Equal(3, (int)ctx.GetVariable("角度1", "Count").Value);
        Assert.Contains("[角度换算] 3 个值，Radian → Degree，范围 Minus90To90", ctx.Log);
        // 角度模式不新增任何流程校验（自定义范围仍只在运行时检查，与旧版相同）
        Assert.Empty(new AngleConvertTool("角度1") { Range = AngleRange.Custom, RangeMin = 10, RangeMax = 0, PixelSize = 0 }.CheckConfiguration());
        Assert.Contains("自定义角度范围无效", new AngleConvertTool("角度1") { ValuePath = "测量.Phis", Range = AngleRange.Custom, RangeMin = 10, RangeMax = 0 }.Run(ctx).Message);
    }

    [Fact]
    public void 长度模式_固定当量_毫米微米与面积()
    {
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Array("卡尺1", "Widths", VariableType.Double, new[] { 10.0, 25.5, double.NaN }));
        ctx.SetVariable(Variable.Single("区域1", "Area", VariableType.Double, 100.0));

        var length = new AngleConvertTool("换算1") { ConvertKind = ConvertKind.Length, ValuePath = "卡尺1.Widths", PixelSize = 0.02 };
        Assert.True(length.Run(ctx).IsSuccess);
        double[] mm = Doubles(ctx, "换算1", "Values");
        Assert.Equal(0.2, mm[0], 12);
        Assert.Equal(0.51, mm[1], 12);
        Assert.True(double.IsNaN(mm[2]));
        Assert.Equal(0.2, Single(ctx, "换算1", "Value"), 12);
        Assert.Equal(3, (int)ctx.GetVariable("换算1", "Count").Value);
        Assert.Contains(ctx.Log, l => l.StartsWith("[单位换算] 3 个值"));

        length.LengthUnit = LengthUnit.um;
        Assert.True(length.Run(ctx).IsSuccess);
        Assert.Equal(200.0, Doubles(ctx, "换算1", "Values")[0], 9);
        Assert.Equal(510.0, Doubles(ctx, "换算1", "Values")[1], 9);

        var area = new AngleConvertTool("面积1") { ConvertKind = ConvertKind.Length, ValuePath = "区域1.Area", PixelSize = 0.02, IsArea = true };
        Assert.True(area.Run(ctx).IsSuccess);
        Assert.Equal(0.04, Single(ctx, "面积1", "Value"), 12);
        Assert.Equal(1, (int)ctx.GetVariable("面积1", "Count").Value);
        area.LengthUnit = LengthUnit.um;
        Assert.True(area.Run(ctx).IsSuccess);
        Assert.Equal(40000.0, Single(ctx, "面积1", "Value"), 6);
        Assert.Contains(ctx.Log, l => l.Contains("um²"));
    }

    [Fact]
    public void 长度模式校验_像素当量必须大于0_参数按模式显隐()
    {
        Assert.Contains(ConfigIssues(new AngleConvertTool("换算1") { ConvertKind = ConvertKind.Length, ValuePath = "Input.X", PixelSize = 0 }), m => m.Contains("像素当量必须大于 0"));
        Assert.Contains(ConfigIssues(new AngleConvertTool("换算1") { ConvertKind = ConvertKind.Length, ValuePath = "Input.X", PixelSize = -0.1 }), m => m.Contains("像素当量必须大于 0"));
        Assert.Empty(new AngleConvertTool("换算1") { ConvertKind = ConvertKind.Length, PixelSize = 0.01 }.CheckConfiguration());
        NodeResult result = new AngleConvertTool("换算1") { ConvertKind = ConvertKind.Length, ValuePath = "Input.X", PixelSize = 0 }.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Contains("像素当量必须大于 0", result.Message);

        var tool = new AngleConvertTool("换算1");
        foreach (string name in new[] { "InputUnit", "OutputUnit", "Range", "RangeMin", "RangeMax", "ValuePath", "ConvertKind" })
        {
            Assert.True(tool.IsParameterVisible(name), name);
        }
        foreach (string name in new[] { "ScaleSource", "PixelSize", "LengthUnit", "IsArea" })
        {
            Assert.False(tool.IsParameterVisible(name), name);
        }
        tool.ConvertKind = ConvertKind.Length;
        foreach (string name in new[] { "ScaleSource", "PixelSize", "LengthUnit", "IsArea", "ValuePath", "ConvertKind" })
        {
            Assert.True(tool.IsParameterVisible(name), name);
        }
        foreach (string name in new[] { "InputUnit", "OutputUnit", "Range", "RangeMin", "RangeMax" })
        {
            Assert.False(tool.IsParameterVisible(name), name);
        }
        // 当量来源：固定当量（默认，0）在前，标定（1）追加在末尾
        Assert.Equal(new[] { ScaleSource.Fixed, ScaleSource.Calibration }, Enum.GetValues<ScaleSource>());
    }

    [Fact]
    public void 单位换算_工具箱改名_ID与类型不变_新参数按数字保存()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "angle-convert");
        Assert.Equal("单位换算", item.DisplayName);
        Assert.Equal("05 几何测量", item.Category);
        ToolNode node = Assert.IsType<ToolNode>(item.Factory());
        var created = Assert.IsType<AngleConvertTool>(node.Tool);
        Assert.Equal(ConvertKind.Angle, created.ConvertKind);

        var tool = new AngleConvertTool("换算1") { ConvertKind = ConvertKind.Length, PixelSize = 0.015, LengthUnit = LengthUnit.um, IsArea = true, ValuePath = "区域1.Area" };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"angle-convert\"", json);
        Assert.Contains("\"ConvertKind\": 1", json);
        Assert.Contains("\"LengthUnit\": 1", json);
        Assert.Contains("\"ScaleSource\": 0", json);
        var loaded = (AngleConvertTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal(ConvertKind.Length, loaded.ConvertKind);
        Assert.Equal(0.015, loaded.PixelSize);
        Assert.Equal(LengthUnit.um, loaded.LengthUnit);
        Assert.True(loaded.IsArea);

        var legacyNode = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new AngleConvertTool("角度1") { Range = AngleRange.ZeroTo180 })))!.AsObject();
        JsonObject properties = legacyNode["Tool"]!["Properties"]!.AsObject();
        foreach (string name in new[] { "ConvertKind", "ScaleSource", "PixelSize", "LengthUnit", "IsArea" })
        {
            Assert.True(properties.Remove(name), name);
        }
        var legacy = (AngleConvertTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacyNode.ToJsonString())).Tool;
        Assert.Equal(ConvertKind.Angle, legacy.ConvertKind);
        Assert.Equal(AngleRange.ZeroTo180, legacy.Range);
        Assert.Equal(1.0, legacy.PixelSize);
    }
}
