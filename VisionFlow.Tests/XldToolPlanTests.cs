using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>XLD-TOOLS-PLAN XG-01 ~ XG-09：边缘提取 / 选择 / 分割 / 特征增强、交点计算、XLD 处理、拟合椭圆矩形、轮廓距离、几何关系测量。</summary>
public class XldToolPlanTests
{
    // ---------------- 公共构造 ----------------

    private static HObject Polyline(params (double Row, double Column)[] points)
    {
        HOperatorSet.GenContourPolygonXld(out HObject xld,
            new HTuple(points.Select(p => p.Row).ToArray()), new HTuple(points.Select(p => p.Column).ToArray()));
        return xld;
    }

    private static HObject Concat(params HObject[] objects)
    {
        HOperatorSet.GenEmptyObj(out HObject all);
        foreach (HObject o in objects)
        {
            HOperatorSet.ConcatObj(all, o, out HObject joined);
            all.Dispose();
            all = joined;
            o.Dispose();
        }
        return all;
    }

    private static HObject Rect2(double row, double column, double phi, double l1, double l2)
    {
        HOperatorSet.GenRectangle2ContourXld(out HObject rect, row, column, phi, l1, l2);
        return rect;
    }

    /// <summary>按 0.5 像素在矩形角点之间插值的致密理想矩形轮廓（矩形拟合需要致密轮廓，见计划第 10 节）。</summary>
    private static HObject DenseRect2(double row, double column, double phi, double l1, double l2)
    {
        using HObject corners = Rect2(row, column, phi, l1, l2);
        HOperatorSet.GetContourXld(corners, out HTuple cr, out HTuple cc);
        var rows = new List<double>();
        var cols = new List<double>();
        for (int k = 0; k < cr.Length - 1; k++)
        {
            double r0 = cr[k].D, c0 = cc[k].D, r1 = cr[k + 1].D, c1 = cc[k + 1].D;
            int steps = (int)Math.Ceiling(Math.Sqrt((r1 - r0) * (r1 - r0) + (c1 - c0) * (c1 - c0)) / 0.5);
            for (int s = 0; s < steps; s++)
            {
                rows.Add(r0 + (r1 - r0) * s / steps);
                cols.Add(c0 + (c1 - c0) * s / steps);
            }
        }
        rows.Add(cr[0].D);
        cols.Add(cc[0].D);
        HOperatorSet.GenContourPolygonXld(out HObject dense, new HTuple(rows.ToArray()), new HTuple(cols.ToArray()));
        return dense;
    }

    private static HObject Circle(double row, double column, double radius, double start = 0, double end = 2 * Math.PI)
    {
        HOperatorSet.GenCircleContourXld(out HObject circle, row, column, radius, start, end, "positive", 1.0);
        return circle;
    }

    private static HObject BoxImage(int gray = 200)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", 200, 200);
        HOperatorSet.GenRectangle1(out HObject box, 50, 60, 150, 140);
        HOperatorSet.PaintRegion(box, blank, out HObject image, gray, "fill");
        blank.Dispose();
        box.Dispose();
        return image;
    }

    private static FlowContext Context(HObject? xld = null, HObject? xld2 = null, HObject? image = null)
    {
        var ctx = new FlowContext();
        if (xld != null)
        {
            ctx.SetVariable(Variable.Object("上游", "Xld", HalconXld.Owned(xld), Count(xld)));
        }
        if (xld2 != null)
        {
            ctx.SetVariable(Variable.Object("上游2", "Xld", HalconXld.Owned(xld2), Count(xld2)));
        }
        if (image != null)
        {
            ctx.SetVariable(Variable.Object("Input", "Image", HalconImage.Owned(image), 1));
        }
        return ctx;
    }

    private static int Count(HObject o)
    {
        HOperatorSet.CountObj(o, out HTuple n);
        return n.I;
    }

    private static HObject OutXld(FlowContext ctx, string module, string name = "Xld") => ((HalconXld)ctx.GetVariable(module, name).Value).Object;

    private static double[] Doubles(FlowContext ctx, string module, string name) =>
        ((System.Collections.IEnumerable)ctx.GetVariable(module, name).Value).Cast<object>().Select(Convert.ToDouble).ToArray();

    private static double Single(FlowContext ctx, string module, string name) => Convert.ToDouble(ctx.GetVariable(module, name).Value);

    private static double TotalLength(HObject xld)
    {
        HOperatorSet.LengthXld(xld, out HTuple lengths);
        return lengths.Length == 0 ? 0 : lengths.DArr.Sum();
    }

    private static IReadOnlyList<string> ConfigIssues(ToolBase tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root).Issues.Select(i => i.Message).ToList();
    }

    private static ToolBase Reload(ToolBase tool) =>
        Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(tool)))).Tool;

    /// <summary>按当前格式保存后改写成旧版文件：去掉新参数，把指定参数改成旧版的字符串值。</summary>
    private static string LegacyJson(ToolBase tool, string[] removed, params (string Name, string Value)[] strings)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(tool)))!.AsObject();
        var properties = node["Tool"]!["Properties"]!.AsObject();
        foreach (string name in removed)
        {
            Assert.True(properties.Remove(name), "新参数不存在：" + name);
        }
        foreach (var (name, value) in strings)
        {
            properties[name] = value;
        }
        return node.ToJsonString();
    }

    // ---------------- XG-01 边缘提取 ----------------

    [Fact]
    public void 边缘提取默认仍为彩色边缘_与edges_color_sub_pix一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = BoxImage();
        HOperatorSet.EdgesColorSubPix(image, out HObject expected, "canny", 1.0, 20, 40);
        using var expectedOwner = expected;
        using var ctx = Context(image: image);
        var tool = new ContourCreateTool("边缘1");

        Assert.Equal(ContourExtractMethod.ColorEdges, tool.ExtractMethod);
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(Count(expected), Count(OutXld(ctx, "边缘1")));
        Assert.Equal(TotalLength(expected), TotalLength(OutXld(ctx, "边缘1")), 6);
        Assert.Contains(ctx.Log, l => l.Contains("[边缘提取] canny, Sigma=1, Low=20, High=40"));
    }

    [Fact]
    public void 灰度边缘与彩色边缘同一滤波器结果一致_灰度边缘支持lanser等滤波器()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(image: BoxImage());
        var color = new ContourCreateTool("彩色") { ExtractMethod = ContourExtractMethod.ColorEdges, Filter = EdgeFilter.canny };
        var gray = new ContourCreateTool("灰度") { ExtractMethod = ContourExtractMethod.Edges, Filter = EdgeFilter.canny };
        var lanser = new ContourCreateTool("lanser") { ExtractMethod = ContourExtractMethod.Edges, Filter = EdgeFilter.lanser2 };

        Assert.True(color.Run(ctx).IsSuccess);
        Assert.True(gray.Run(ctx).IsSuccess);
        Assert.True(lanser.Run(ctx).IsSuccess);

        // 单通道图像上两个算子的 canny 结果相同
        Assert.Equal(Count(OutXld(ctx, "彩色")), Count(OutXld(ctx, "灰度")));
        Assert.Equal(TotalLength(OutXld(ctx, "彩色")), TotalLength(OutXld(ctx, "灰度")), 3);
        HOperatorSet.EdgesSubPix(((HalconImage)ctx.GetVariable("Input", "Image").Value).Object, out HObject expected, "lanser2", 1.0, 20, 40);
        using (expected)
        {
            Assert.Equal(TotalLength(expected), TotalLength(OutXld(ctx, "lanser")), 6);
        }
        // 方框周长 2×(100+80) 左右
        Assert.InRange(TotalLength(OutXld(ctx, "灰度")), 340, 380);
    }

    [Fact]
    public void 彩色边缘不支持的滤波器_校验与运行都报错()
    {
        var tool = new ContourCreateTool("边缘1") { Filter = EdgeFilter.lanser1 };
        Assert.Contains(ConfigIssues(tool), m => m.Contains("彩色边缘提取不支持滤波器 lanser1"));
        using var ctx = new FlowContext();
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("彩色边缘提取不支持滤波器 lanser1", result.Message);
        tool.ExtractMethod = ContourExtractMethod.Edges;
        Assert.Empty(ConfigIssues(tool));
    }

    [Fact]
    public void 亚像素阈值与线条提取_与HALCON直接调用一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject blank, "byte", 200, 200);
        HOperatorSet.GenRegionLine(out HObject line, 100, 20, 100, 180);
        HOperatorSet.DilationCircle(line, out HObject thick, 2.5);
        HOperatorSet.PaintRegion(thick, blank, out HObject lineImage, 200, "fill");
        blank.Dispose(); line.Dispose(); thick.Dispose();
        HOperatorSet.LinesGauss(lineImage, out HObject expectedLines, 1.5, 3, 8, "light", "true", "bar-shaped", "true");
        HOperatorSet.ThresholdSubPix(lineImage, out HObject expectedThreshold, 128);
        using var expectedLinesOwner = expectedLines;
        using var expectedThresholdOwner = expectedThreshold;
        using var ctx = Context(image: lineImage);
        var lines = new ContourCreateTool("线条") { ExtractMethod = ContourExtractMethod.LinesGauss };
        var threshold = new ContourCreateTool("阈值") { ExtractMethod = ContourExtractMethod.ThresholdSubPix };

        Assert.True(lines.Run(ctx).IsSuccess);
        Assert.True(threshold.Run(ctx).IsSuccess);

        Assert.True(Count(OutXld(ctx, "线条")) > 0);
        Assert.Equal(TotalLength(expectedLines), TotalLength(OutXld(ctx, "线条")), 6);
        Assert.Equal(TotalLength(expectedThreshold), TotalLength(OutXld(ctx, "阈值")), 6);
        // 最长的线条沿中心线第 100 行（取各点行坐标的均值；端部可能另有短的分支轮廓）
        HOperatorSet.LengthXld(OutXld(ctx, "线条"), out HTuple lineLengths);
        int longest = Array.IndexOf(lineLengths.DArr, lineLengths.DArr.Max());
        HOperatorSet.SelectObj(OutXld(ctx, "线条"), out HObject firstLine, longest + 1);
        using var firstLineOwner = firstLine;
        HOperatorSet.GetContourXld(firstLine, out HTuple lineRows, out _);
        Assert.InRange(lineRows.DArr.Average(), 99.5, 100.5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 边缘提取空结果_按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject blank, "byte", 100, 100);
        using var ctx = Context(image: blank);
        var tool = new ContourCreateTool("边缘1") { ExtractMethod = ContourExtractMethod.Edges, FailWhenNotFound = failWhenNotFound };

        Assert.Equal(!failWhenNotFound, tool.Run(ctx).IsSuccess);
        Assert.False((bool)ctx.GetVariable("边缘1", "Found").Value);
    }

    [Fact]
    public void 边缘提取参数按方式显示_旧文件Filter字符串读入枚举并按名称保存()
    {
        var tool = new ContourCreateTool("边缘1");
        Assert.True(tool.IsParameterVisible(nameof(ContourCreateTool.Filter)));
        Assert.False(tool.IsParameterVisible(nameof(ContourCreateTool.LineSigma)));
        tool.ExtractMethod = ContourExtractMethod.LinesGauss;
        Assert.True(tool.IsParameterVisible(nameof(ContourCreateTool.LineModel)));
        Assert.False(tool.IsParameterVisible(nameof(ContourCreateTool.Filter)));
        tool.ExtractMethod = ContourExtractMethod.ThresholdSubPix;
        Assert.True(tool.IsParameterVisible(nameof(ContourCreateTool.Threshold)));
        Assert.False(tool.IsParameterVisible(nameof(ContourCreateTool.Sigma)));

        // 旧版文件：Filter 为字符串（大小写不同），且没有 ExtractMethod 等新参数
        string legacy = LegacyJson(new ContourCreateTool("边缘1") { Sigma = 1.5, Low = 10, High = 30 },
            new[] { "ExtractMethod", "Threshold", "LineSigma", "LineLow", "LineHigh", "LightDark", "ExtractWidth", "LineModel", "CompleteJunctions" },
            ("Filter", "Deriche2"));
        Assert.DoesNotContain("ExtractMethod", legacy);
        var loaded = (ContourCreateTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy)).Tool;
        Assert.Equal(EdgeFilter.deriche2, loaded.Filter);
        Assert.Equal(ContourExtractMethod.ColorEdges, loaded.ExtractMethod);
        Assert.Equal(1.5, loaded.Sigma);
        string saved = new string(FlowSerializer.SaveNode(new ToolNode(loaded)).Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains("\"Filter\":\"deriche2\"", saved);
    }

    // ---------------- XG-02 边缘选择 ----------------

    private static HObject ClosedAndOpen() => Concat(Rect2(100, 100, 0.3, 40, 20), Polyline((10, 10), (10, 80), (60, 80)));

    [Fact]
    public void 边缘选择默认仍为形状特征_与select_shape_xld一致_旧文件Operation字符串读入枚举()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject xld = ClosedAndOpen();
        HOperatorSet.SelectShapeXld(xld, out HObject expected, new HTuple("contlength"), "and", new HTuple(10.0), new HTuple(999999.0));
        using var expectedOwner = expected;
        using var ctx = Context(xld);
        var tool = new SelectContourTool("选择1") { XldPath = "上游.Xld" };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(Count(expected), Count(OutXld(ctx, "选择1")));

        string legacy = LegacyJson(new SelectContourTool("选择1") { XldPath = "上游.Xld", Features = "contlength,area", Min = 1, Max = 100 },
            new[] { "SelectBy", "ContourFeature", "Min1", "Max1", "Min2", "Max2", "TakeMode", "TakeIndex" },
            ("Operation", "or"));
        var loaded = (SelectContourTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy)).Tool;
        Assert.Equal(XldSelectOperation.or, loaded.Operation);
        Assert.Equal(ContourSelectBy.Shape, loaded.SelectBy);
        Assert.Equal(ContourTakeMode.All, loaded.TakeMode);
        string saved = new string(FlowSerializer.SaveNode(new ToolNode(loaded)).Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains("\"Operation\":\"or\"", saved);
    }

    [Theory]
    [InlineData(ContourFeature.closed, 0.5, 1.0, 1)]
    [InlineData(ContourFeature.open, 1.0, 200.0, 1)]
    [InlineData(ContourFeature.contour_length, 150.0, 1000.0, 1)]
    public void 按轮廓特征选择_与select_contours_xld一致(ContourFeature feature, double min1, double max1, int expectedCount)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject xld = ClosedAndOpen();
        HOperatorSet.SelectContoursXld(xld, out HObject expected, feature.ToString(), min1, max1, -0.5, 0.5);
        using var expectedOwner = expected;
        using var ctx = Context(xld);
        var tool = new SelectContourTool("选择1") { XldPath = "上游.Xld", SelectBy = ContourSelectBy.Contour, ContourFeature = feature, Min1 = min1, Max1 = max1 };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(expectedCount, Count(OutXld(ctx, "选择1")));
        Assert.Equal(Count(expected), Count(OutXld(ctx, "选择1")));
        HOperatorSet.TestClosedXld(OutXld(ctx, "选择1"), out HTuple closed);
        if (feature == ContourFeature.closed) Assert.Equal(1, closed.I);
        if (feature == ContourFeature.open) Assert.Equal(0, closed.I);
    }

    [Theory]
    [InlineData(ContourTakeMode.Longest, 3, 300.0)]
    [InlineData(ContourTakeMode.Shortest, 3, 50.0)]
    [InlineData(ContourTakeMode.First, 3, 100.0)]
    [InlineData(ContourTakeMode.ByIndex, 3, 300.0)]
    public void 边缘选择取件方式(ContourTakeMode mode, int index, double expectedLength)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject xld = Concat(Polyline((0, 0), (0, 100)), Polyline((10, 0), (10, 50)), Polyline((20, 0), (20, 300)));
        using var ctx = Context(xld);
        var tool = new SelectContourTool("取件1") { XldPath = "上游.Xld", Min = 1, TakeMode = mode, TakeIndex = index - 1 };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(1, Count(OutXld(ctx, "取件1")));
        Assert.Equal(expectedLength, TotalLength(OutXld(ctx, "取件1")), 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 按序号取件越界_按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(ClosedAndOpen());
        var tool = new SelectContourTool("取件1") { XldPath = "上游.Xld", Min = 1, TakeMode = ContourTakeMode.ByIndex, TakeIndex = 5, FailWhenNotFound = failWhenNotFound };

        NodeResult result = tool.Run(ctx);
        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.False((bool)ctx.GetVariable("取件1", "Found").Value);
        Assert.Contains("取 第 5 条（共 2 条）", failWhenNotFound ? result.Message : ctx.Log.Last());
    }

    [Fact]
    public void 边缘选择参数按依据与特征显示()
    {
        var tool = new SelectContourTool("选择1");
        Assert.True(tool.IsParameterVisible(nameof(SelectContourTool.Features)));
        Assert.False(tool.IsParameterVisible(nameof(SelectContourTool.ContourFeature)));
        Assert.False(tool.IsParameterVisible(nameof(SelectContourTool.TakeIndex)));
        tool.SelectBy = ContourSelectBy.Contour;
        Assert.False(tool.IsParameterVisible(nameof(SelectContourTool.Features)));
        Assert.True(tool.IsParameterVisible(nameof(SelectContourTool.Min1)));
        Assert.False(tool.IsParameterVisible(nameof(SelectContourTool.Min2)));
        tool.ContourFeature = ContourFeature.curvature;
        Assert.True(tool.IsParameterVisible(nameof(SelectContourTool.Max2)));
        tool.ContourFeature = ContourFeature.closed;
        Assert.False(tool.IsParameterVisible(nameof(SelectContourTool.Min1)));
        Assert.True(tool.IsParameterVisible(nameof(SelectContourTool.Max1)));
        tool.TakeMode = ContourTakeMode.ByIndex;
        Assert.True(tool.IsParameterVisible(nameof(SelectContourTool.TakeIndex)));
    }

    // ---------------- XG-03 XLD 分割 ----------------

    [Fact]
    public void XLD分割椭圆模式_与segment_contours_xld一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEllipseContourXld(out HObject arc, 100, 100, 0.0, 60, 30, 0, Math.PI, "positive", 1.0);
        HObject xld = Concat(arc, Polyline((100, 40), (200, 40)));
        HOperatorSet.SegmentContoursXld(xld, out HObject expected, "lines_ellipses", 5, 4, 2);
        using var expectedOwner = expected;
        using var ctx = Context(xld);
        var tool = new SegmentXldTool("分割1") { XldPath = "上游.Xld", Mode = XldSegmentMode.lines_ellipses };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(Count(expected), Count(OutXld(ctx, "分割1")));
        Assert.Equal(TotalLength(expected), TotalLength(OutXld(ctx, "分割1")), 6);
        Assert.Equal(2, (int)XldSegmentMode.lines_ellipses);
    }

    // ---------------- XG-04 XLD 特征值 ----------------

    [Theory]
    [InlineData("circularity")]
    [InlineData("compactness")]
    [InlineData("convexity")]
    [InlineData("anisometry")]
    [InlineData("bulkiness")]
    [InlineData("struct_factor")]
    [InlineData("orientation")]
    [InlineData("max_diameter")]
    [InlineData("rect2_phi")]
    [InlineData("rect2_len1")]
    [InlineData("rect2_len2")]
    [InlineData("outer_radius")]
    [InlineData("ra")]
    [InlineData("rb")]
    [InlineData("phi")]
    public void 新增特征值_与select_shape_xld同名特征筛选一致(string feature)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEllipseContourXld(out HObject ellipse, 200, 200, 0.6, 50, 20, 0, 2 * Math.PI, "positive", 1.0);
        HObject xld = Concat(Rect2(100, 100, 0.3, 40, 20), ellipse);
        using var ctx = Context(xld);
        var tool = new XldFeaturesTool("特征1") { XldPath = "上游.Xld", Features = feature };

        Assert.True(tool.Run(ctx).IsSuccess);
        double[] values = Doubles(ctx, "特征1", "Values");
        Assert.Equal(2, values.Length);
        HObject input = ((HalconXld)ctx.GetVariable("上游", "Xld").Value).Object;
        for (int i = 0; i < 2; i++)
        {
            double tolerance = Math.Max(1e-6, Math.Abs(values[i]) * 1e-6);
            HOperatorSet.SelectShapeXld(input, out HObject selected, feature, "and", values[i] - tolerance, values[i] + tolerance);
            using (selected)
            {
                HOperatorSet.SelectObj(input, out HObject expectedObject, i + 1);
                using (expectedObject)
                {
                    // 第 i 个轮廓按自身特征值筛选时被选中
                    HOperatorSet.AreaCenterXld(expectedObject, out _, out HTuple er, out HTuple ec, out _);
                    HOperatorSet.AreaCenterXld(selected, out _, out HTuple sr, out HTuple sc, out _);
                    Assert.Contains(Enumerable.Range(0, sr.Length), j => Math.Abs(sr[j].D - er.D) < 1e-6 && Math.Abs(sc[j].D - ec.D) < 1e-6);
                }
            }
        }
    }

    [Fact]
    public void 特征值is_closed_闭合为1开口为0()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(ClosedAndOpen());
        var tool = new XldFeaturesTool("特征1") { XldPath = "上游.Xld", Features = "is_closed,contlength" };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(new[] { 1.0, 0.0 }, Doubles(ctx, "特征1", "Values").Take(2));
    }

    // ---------------- XG-05 交点计算 ----------------

    [Fact]
    public void 直线与轮廓交点_与intersection_line_contour_xld一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject rect = Rect2(100, 100, 0.3, 40, 20);
        HOperatorSet.IntersectionLineContourXld(rect, 100.0, 0.0, 100.0, 200.0, out HTuple rows, out HTuple columns, out _);
        using var ctx = Context(Polyline((100, 0), (100, 200)), rect);
        var tool = new IntersectionLinesTool("交点1") { Line1Path = "上游.Xld", Line2Path = "上游2.Xld", Mode = IntersectionMode.LineContour };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, (int)ctx.GetVariable("交点1", "Count").Value);
        Assert.Equal(rows.DArr, Doubles(ctx, "交点1", "Rows"));
        Assert.Equal(columns.DArr, Doubles(ctx, "交点1", "Columns"));
        Assert.Equal(columns[0].D, Single(ctx, "交点1", "Column"));
        Assert.Equal(2, ctx.GetVariable("交点1", "Xld").Count);
    }

    [Fact]
    public void 轮廓与轮廓交点_与intersection_contours_xld一致_直线模式输出数组()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject rect = Rect2(100, 100, 0.3, 40, 20);
        HObject circle = Circle(100, 140, 30);
        HOperatorSet.IntersectionContoursXld(rect, circle, "mutual", out HTuple rows, out HTuple columns, out _);
        using var ctx = Context(rect, circle);
        ctx.SetVariable(Variable.Object("线", "A", HalconXld.Owned(Polyline((0, 0), (100, 100))), 1));
        ctx.SetVariable(Variable.Object("线", "B", HalconXld.Owned(Polyline((0, 100), (100, 0))), 1));
        var contours = new IntersectionLinesTool("交点1") { Line1Path = "上游.Xld", Line2Path = "上游2.Xld", Mode = IntersectionMode.ContourContour };
        var lines = new IntersectionLinesTool("交点2") { Line1Path = "线.A", Line2Path = "线.B" };

        Assert.True(contours.Run(ctx).IsSuccess);
        Assert.True(lines.Run(ctx).IsSuccess);

        Assert.Equal(rows.DArr, Doubles(ctx, "交点1", "Rows"));
        Assert.Equal(columns.DArr, Doubles(ctx, "交点1", "Columns"));
        Assert.Equal(new[] { 50.0 }, Doubles(ctx, "交点2", "Rows"));
        Assert.Equal(1, (int)ctx.GetVariable("交点2", "Count").Value);
        Assert.Equal(50.0, Single(ctx, "交点2", "Row"), 6);
        Assert.True(contours.IsParameterVisible(nameof(IntersectionLinesTool.IntersectionType)));
        Assert.False(lines.IsParameterVisible(nameof(IntersectionLinesTool.IntersectionType)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 交点计算无交点_按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Polyline((500, 0), (500, 10)), Rect2(100, 100, 0.3, 40, 20));
        var tool = new IntersectionLinesTool("交点1") { Line1Path = "上游.Xld", Line2Path = "上游2.Xld", Mode = IntersectionMode.LineContour, FailWhenNotFound = failWhenNotFound };

        NodeResult result = tool.Run(ctx);
        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.False((bool)ctx.GetVariable("交点1", "Found").Value);
        Assert.Empty(Doubles(ctx, "交点1", "Rows"));
        Assert.True(double.IsNaN(Single(ctx, "交点1", "Row")));
    }

    // ---------------- XG-06 XLD 处理 ----------------

    private static (double Row, double Column) Center(HObject xld)
    {
        HOperatorSet.AreaCenterXld(xld, out _, out HTuple r, out HTuple c, out _);
        return (r[0].D, c[0].D);
    }

    [Fact]
    public void XLD处理_平滑闭合裁剪形状转换排序_与HALCON直接调用一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject wavy = Polyline((50, 0), (55, 10), (50, 20), (55, 30), (50, 40), (55, 50), (50, 60));
        HOperatorSet.SmoothContoursXld(wavy, out HObject smoothExpected, 5);
        HOperatorSet.ClipContoursXld(wavy, out HObject clipExpected, 0, 0, 100, 30);
        HOperatorSet.ShapeTransXld(wavy, out HObject rectExpected, "rectangle1");
        using var o1 = smoothExpected; using var o2 = clipExpected; using var o3 = rectExpected;
        using var ctx = Context(wavy, Concat(Polyline((80, 80), (80, 90)), Polyline((10, 10), (10, 20)), Polyline((40, 5), (40, 15))));
        var smooth = new XldProcessTool("平滑") { XldPath = "上游.Xld", Method = XldProcessOp.Smooth };
        var close = new XldProcessTool("闭合") { XldPath = "上游.Xld", Method = XldProcessOp.Close };
        var clip = new XldProcessTool("裁剪") { XldPath = "上游.Xld", Method = XldProcessOp.Clip, ClipRow2 = 100, ClipColumn2 = 30 };
        var shape = new XldProcessTool("形状") { XldPath = "上游.Xld", Method = XldProcessOp.ShapeTrans, ShapeType = XldShapeType.rectangle1 };
        var sort = new XldProcessTool("排序") { XldPath = "上游2.Xld", Method = XldProcessOp.Sort, SortMode = XldSortMode.upper_left };

        foreach (XldProcessTool tool in new[] { smooth, close, clip, shape, sort })
        {
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, tool.ModuleName + "：" + result.Message);
        }

        Assert.Equal(TotalLength(smoothExpected), TotalLength(OutXld(ctx, "平滑")), 6);
        HOperatorSet.TestClosedXld(OutXld(ctx, "闭合"), out HTuple closed);
        Assert.Equal(1, closed.I);
        Assert.Equal(TotalLength(clipExpected), TotalLength(OutXld(ctx, "裁剪")), 6);
        Assert.Equal(Center(rectExpected), Center(OutXld(ctx, "形状")));
        var firstRows = new List<double>();
        HObject sorted = OutXld(ctx, "排序");
        for (int i = 1; i <= Count(sorted); i++)
        {
            HOperatorSet.SelectObj(sorted, out HObject one, i);
            using (one)
            {
                HOperatorSet.GetContourXld(one, out HTuple contourRows, out _);
                firstRows.Add(contourRows[0].D);
            }
        }
        Assert.Equal(new[] { 10.0, 40.0, 80.0 }, firstRows);
    }

    [Fact]
    public void XLD处理_连接相邻共线共圆轮廓_断开的轮廓连成一条()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(
            Concat(Polyline((50, 0), (50, 40)), Polyline((50, 43), (50, 90))),
            Concat(Circle(200, 200, 50, 0, 1.2), Circle(200, 200, 50, 1.4, 2.6)));
        var adjacent = new XldProcessTool("相邻") { XldPath = "上游.Xld", Method = XldProcessOp.UnionAdjacent };
        var collinear = new XldProcessTool("共线") { XldPath = "上游.Xld", Method = XldProcessOp.UnionCollinear };
        var cocircular = new XldProcessTool("共圆") { XldPath = "上游2.Xld", Method = XldProcessOp.UnionCocircular };

        foreach (XldProcessTool tool in new[] { adjacent, collinear, cocircular })
        {
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, tool.ModuleName + "：" + result.Message);
            Assert.Equal(1, (int)ctx.GetVariable(tool.ModuleName, "Count").Value);
        }
        // 角度参数按度填写，默认值等于 HALCON 默认的弧度值
        Assert.Equal(0.1, AngleMath.ToRadians(collinear.MaxAngleDeg), 12);
        Assert.Equal(0.5, AngleMath.ToRadians(cocircular.MaxArcAngleDiffDeg), 12);
    }

    [Fact]
    public void XLD处理_仿射跟随_平移到矩阵位置_未配置或无效时失败不退回原位()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rect2(100, 100, 0.3, 40, 20));
        HOperatorSet.HomMat2dIdentity(out HTuple identity);
        HOperatorSet.HomMat2dTranslate(identity, 10, 20, out HTuple translate);
        ctx.SetVariable(Variable.Object("定位", "HomMat", new HomMat2D(translate), 1));
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object, new[] { new HomMat2D(translate), HomMat2D.Identity }));
        ctx.SetVariable(Variable.Single("定位", "NotMatrix", VariableType.Double, 1.0));
        var tool = new XldProcessTool("跟随") { XldPath = "上游.Xld", Method = XldProcessOp.AffineTrans, MatrixPath = "定位.HomMat" };

        Assert.True(tool.Run(ctx).IsSuccess);
        (double row, double column) = Center(OutXld(ctx, "跟随"));
        Assert.Equal(110.0, row, 3);
        Assert.Equal(120.0, column, 3);

        tool.MatrixPath = null;
        Assert.Contains("仿射跟随需要指定定位矩阵", ConfigIssues(tool));
        Assert.Equal("仿射跟随需要指定定位矩阵", tool.Run(ctx).Message);

        tool.MatrixPath = "定位.NotMatrix";
        NodeResult invalid = tool.Run(ctx);
        Assert.False(invalid.IsSuccess);
        Assert.Contains("不会退回固定位置", invalid.Message);

        tool.MatrixPath = "定位.HomMats";
        NodeResult many = tool.Run(ctx);
        Assert.False(many.IsSuccess);
        Assert.Contains("只支持单个矩阵（当前为 2 个）", many.Message);

        tool.Method = XldProcessOp.Smooth;
        Assert.False(tool.IsParameterVisible(nameof(XldProcessTool.MatrixPath)));
        tool.NumRegrPoints = 4;
        Assert.Contains("平滑点数必须是不小于 3 的奇数", ConfigIssues(tool));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void XLD处理空结果_按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rect2(100, 100, 0.3, 40, 20));
        var tool = new XldProcessTool("裁剪") { XldPath = "上游.Xld", Method = XldProcessOp.Clip, ClipRow1 = 500, ClipColumn1 = 500, ClipRow2 = 600, ClipColumn2 = 600, FailWhenNotFound = failWhenNotFound };

        Assert.Equal(!failWhenNotFound, tool.Run(ctx).IsSuccess);
        Assert.False((bool)ctx.GetVariable("裁剪", "Found").Value);
    }

    // ---------------- XG-07 拟合椭圆/矩形 ----------------

    [Theory]
    [InlineData(EllipseFitAlgorithm.fitzgibbon)]
    [InlineData(EllipseFitAlgorithm.fhuber)]
    [InlineData(EllipseFitAlgorithm.ftukey)]
    [InlineData(EllipseFitAlgorithm.geometric)]
    [InlineData(EllipseFitAlgorithm.geohuber)]
    [InlineData(EllipseFitAlgorithm.geotukey)]
    [InlineData(EllipseFitAlgorithm.voss)]
    public void 拟合椭圆_闭合理想轮廓误差在0点1像素和0点1度内(EllipseFitAlgorithm algorithm)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEllipseContourXld(out HObject ellipse, 200.0, 300.0, 0.4, 80.0, 40.0, 0.0, 2 * Math.PI, "positive", 1.5);
        AssertEllipseFit(ellipse, algorithm);
    }

    [Theory]
    [InlineData(EllipseFitAlgorithm.focpoints)]
    [InlineData(EllipseFitAlgorithm.fphuber)]
    [InlineData(EllipseFitAlgorithm.fptukey)]
    public void 焦点类椭圆算法_开口理想椭圆弧误差在0点1像素和0点1度内_退化的理想闭合椭圆明确失败(EllipseFitAlgorithm algorithm)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        // 焦点类算法返回的角度比其他算法多 π，工具折算后与真值一致
        HOperatorSet.GenEllipseContourXld(out HObject arc, 200.0, 300.0, 0.4, 80.0, 40.0, 0.0, 6.0, "positive", 1.5);
        AssertEllipseFit(arc, algorithm);

        // HALCON 22.11 实测：gen_ellipse_contour_xld 生成的完整闭合椭圆上短半轴返回 0（阈值得到的闭合椭圆轮廓则正常），工具不输出退化结果
        HOperatorSet.GenEllipseContourXld(out HObject closed, 200.0, 300.0, 0.4, 80.0, 40.0, 0.0, 2 * Math.PI, "positive", 1.5);
        using var ctx = Context(closed);
        var tool = new FitEllipseRectTool("椭圆") { XldPath = "上游.Xld", EllipseAlgorithm = algorithm };
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("结果退化", result.Message);
        Assert.Contains("焦点类算法在部分闭合轮廓上会返回短半轴 0", result.Message);
    }

    private static void AssertEllipseFit(HObject ellipse, EllipseFitAlgorithm algorithm)
    {
        using var ctx = Context(ellipse);
        var tool = new FitEllipseRectTool("椭圆") { XldPath = "上游.Xld", EllipseAlgorithm = algorithm };

        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(200.0, Single(ctx, "椭圆", "Row"), 0.1);
        Assert.Equal(300.0, Single(ctx, "椭圆", "Column"), 0.1);
        Assert.Equal(80.0, Single(ctx, "椭圆", "Length1"), 0.1);
        Assert.Equal(40.0, Single(ctx, "椭圆", "Length2"), 0.1);
        Assert.Equal(AngleMath.ToDegrees(0.4), AngleMath.ToDegrees(Single(ctx, "椭圆", "Phi")), 0.1);
        Assert.Equal(1, ctx.GetVariable("椭圆", "Xld").Count);
    }

    [Theory]
    [InlineData(RectangleFitAlgorithm.regression)]
    [InlineData(RectangleFitAlgorithm.huber)]
    [InlineData(RectangleFitAlgorithm.tukey)]
    public void 拟合矩形_致密理想轮廓误差在0点1像素和0点1度内(RectangleFitAlgorithm algorithm)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(DenseRect2(150, 250, 0.5, 60, 25));
        var tool = new FitEllipseRectTool("矩形") { XldPath = "上游.Xld", FitShape = FitShapeKind.Rectangle2, RectangleAlgorithm = algorithm };

        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(150.0, Single(ctx, "矩形", "Row"), 0.1);
        Assert.Equal(250.0, Single(ctx, "矩形", "Column"), 0.1);
        Assert.Equal(60.0, Single(ctx, "矩形", "Length1"), 0.1);
        Assert.Equal(25.0, Single(ctx, "矩形", "Length2"), 0.1);
        Assert.Equal(AngleMath.ToDegrees(0.5), AngleMath.ToDegrees(Single(ctx, "矩形", "Phi")), 0.1);
    }

    [Fact]
    public void 拟合矩形_只有角点的轮廓明确失败_参数按形状显示()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Rect2(150, 250, 0.5, 60, 25));
        var tool = new FitEllipseRectTool("矩形") { XldPath = "上游.Xld", FitShape = FitShapeKind.Rectangle2 };

        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("拟合矩形失败", result.Message);
        Assert.Contains("只有角点的多边形无法拟合", result.Message);

        Assert.True(tool.IsParameterVisible(nameof(FitEllipseRectTool.RectangleAlgorithm)));
        Assert.False(tool.IsParameterVisible(nameof(FitEllipseRectTool.EllipseAlgorithm)));
        tool.FitShape = FitShapeKind.Ellipse;
        Assert.False(tool.IsParameterVisible(nameof(FitEllipseRectTool.VossTabSize)));
        tool.EllipseAlgorithm = EllipseFitAlgorithm.voss;
        Assert.True(tool.IsParameterVisible(nameof(FitEllipseRectTool.VossTabSize)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 拟合椭圆输入为空_按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEmptyObj(out HObject empty);
        using var ctx = Context(empty);
        var tool = new FitEllipseRectTool("椭圆") { XldPath = "上游.Xld", FailWhenNotFound = failWhenNotFound };

        Assert.Equal(!failWhenNotFound, tool.Run(ctx).IsSuccess);
        Assert.False((bool)ctx.GetVariable("椭圆", "Found").Value);
        Assert.True(double.IsNaN(Single(ctx, "椭圆", "Phi")));
    }

    // ---------------- XG-08 轮廓距离 ----------------

    private static HObject Lines(int count, double column0, double shift) => Concat(Enumerable.Range(0, count)
        .Select(i => Polyline((i * 40.0, column0 + i * shift), (i * 40.0 + 20, column0 + i * shift))).ToArray());

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    public void 轮廓到轮廓_逐一配对与一对多_结果与distance_cc_min_points一致(int n1, int n2)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject left = Lines(n1, 0, 0);
        HObject right = Lines(n2, 30, 7);
        HOperatorSet.CopyObj(left, out HObject leftCopy, 1, -1);
        HOperatorSet.CopyObj(right, out HObject rightCopy, 1, -1);
        using var leftOwner = leftCopy;
        using var rightOwner = rightCopy;
        using var ctx = Context(left, right);
        var tool = new ContourDistanceTool("距离1") { XldPath = "上游.Xld", XldPath2 = "上游2.Xld" };

        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        int pairs = Math.Max(n1, n2);
        double[] distances = Doubles(ctx, "距离1", "Distances");
        double[] rows2 = Doubles(ctx, "距离1", "Rows2");
        double[] columns1 = Doubles(ctx, "距离1", "Columns1");
        Assert.Equal(pairs, distances.Length);
        for (int i = 0; i < pairs; i++)
        {
            HOperatorSet.SelectObj(leftCopy, out HObject a, (n1 == 1 ? 0 : i) + 1);
            HOperatorSet.SelectObj(rightCopy, out HObject b, (n2 == 1 ? 0 : i) + 1);
            using (a)
            using (b)
            {
                HOperatorSet.DistanceCcMinPoints(a, b, "fast_point_to_segment", out HTuple d, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                Assert.Equal(d.D, distances[i], 6);
                Assert.Equal(r2.D, rows2[i], 6);
                Assert.Equal(c1.D, columns1[i], 6);
            }
        }
        Assert.Equal(distances[0], Single(ctx, "距离1", "Distance"));
        Assert.Equal(pairs, ctx.GetVariable("距离1", "Segments").Count);
        Assert.Empty(Doubles(ctx, "距离1", "MaxDistances"));
    }

    [Fact]
    public void 轮廓到轮廓_point_to_segment模式与直接调用一致_个数不等且都大于1报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject left = Lines(2, 0, 0);
        HOperatorSet.CopyObj(left, out HObject first, 1, 1);
        using var firstOwner = first;
        using HObject circle = Circle(10, 40, 8);
        HOperatorSet.DistanceCcMinPoints(first, circle, "point_to_segment", out HTuple expected, out _, out _, out _, out _);
        using var ctx = Context(left, Lines(3, 30, 7));
        ctx.SetVariable(Variable.Object("圆", "Xld", HalconXld.Owned(Circle(10, 40, 8)), 1));
        var mismatch = new ContourDistanceTool("距离1") { XldPath = "上游.Xld", XldPath2 = "上游2.Xld" };
        var segment = new ContourDistanceTool("距离2") { XldPath = "上游.Xld", XldPath2 = "圆.Xld", CcMode = ContourDistanceCcMode.point_to_segment };

        NodeResult result = mismatch.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("两组对象个数不一致（2 对 3），无法逐一配对", result.Message);
        Assert.True(segment.Run(ctx).IsSuccess);
        Assert.Equal(expected.D, Doubles(ctx, "距离2", "Distances")[0], 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 轮廓距离配对中含空轮廓_按未找到策略(bool failWhenNotFound)
    {
        // HALCON 不产生 0 点轮廓（计划第 10 节），空轮廓在实际中表现为一侧 XLD 没有对象
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEmptyObj(out HObject empty);
        using var ctx = Context(Lines(1, 0, 0), empty);
        var tool = new ContourDistanceTool("距离1") { XldPath = "上游.Xld", XldPath2 = "上游2.Xld", FailWhenNotFound = failWhenNotFound };

        NodeResult result = tool.Run(ctx);
        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.Contains("轮廓距离没有可计算的配对（轮廓 1 个，另一侧 0 个）", failWhenNotFound ? result.Message : ctx.Log.Last());
        Assert.False((bool)ctx.GetVariable("距离1", "Found").Value);
        Assert.Equal(0, ctx.GetVariable("距离1", "Segments").Count);
    }

    [Fact]
    public void 点到轮廓_按distance_pc计算_行列个数不一致报错_模式必填校验()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject line = Polyline((0, 0), (0, 100));
        HOperatorSet.DistancePc(line, new HTuple(10.0, 20.0), new HTuple(50.0, 50.0), out HTuple min, out HTuple max);
        using var ctx = Context(Polyline((0, 0), (0, 100)));
        ctx.SetVariable(Variable.Array("点", "Rows", VariableType.Double, new[] { 10.0, 20.0 }));
        ctx.SetVariable(Variable.Array("点", "Columns", VariableType.Double, new[] { 50.0, 50.0 }));
        ctx.SetVariable(Variable.Single("点", "Column", VariableType.Double, 5.0));
        var tool = new ContourDistanceTool("距离1") { XldPath = "上游.Xld", Mode = ContourDistanceMode.PointToContour, RowPath = "点.Rows", ColumnPath = "点.Columns" };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(min.DArr, Doubles(ctx, "距离1", "Distances"));
        Assert.Equal(max.DArr, Doubles(ctx, "距离1", "MaxDistances"));
        Assert.Empty(Doubles(ctx, "距离1", "Rows1"));

        tool.ColumnPath = "点.Column";
        tool.RowPath = "点.Rows";
        Assert.Contains("点的行、列个数不一致（行 2 个，列 1 个）", tool.Run(ctx).Message);

        var unset = new ContourDistanceTool("距离2") { XldPath = "上游.Xld", Mode = ContourDistanceMode.PointToContour };
        Assert.Contains("点到轮廓需要指定点的行、列坐标", ConfigIssues(unset));
        Assert.False(unset.IsParameterVisible(nameof(ContourDistanceTool.XldPath2)));
        Assert.False(unset.IsParameterVisible(nameof(ContourDistanceTool.CcMode)));
        unset.Mode = ContourDistanceMode.ContourToContour;
        Assert.Contains("轮廓到轮廓需要指定轮廓 2", ConfigIssues(unset));
        Assert.False(unset.IsParameterVisible(nameof(ContourDistanceTool.RowPath)));
    }

    // ---------------- XG-09 几何关系测量 ----------------

    [Fact]
    public void 点到直线_距离与垂足与解析值一致_输出垂线与十字()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Polyline((0, 0), (0, 100)));
        ctx.SetVariable(Variable.Array("点", "Rows", VariableType.Double, new[] { 10.0, -30.0 }));
        ctx.SetVariable(Variable.Array("点", "Columns", VariableType.Double, new[] { 50.0, 20.0 }));
        var tool = new GeometryRelationTool("关系1") { Line1Path = "上游.Xld", RowPath = "点.Rows", ColumnPath = "点.Columns" };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(new[] { 10.0, 30.0 }, Doubles(ctx, "关系1", "Distances"));
        Assert.Equal(new[] { 0.0, 0.0 }, Doubles(ctx, "关系1", "FootRows"));
        Assert.Equal(new[] { 50.0, 20.0 }, Doubles(ctx, "关系1", "FootColumns"));
        Assert.Equal(10.0, Single(ctx, "关系1", "Distance"));
        // 每对一条垂线 + 一个垂足十字
        Assert.Equal(4, ctx.GetVariable("关系1", "Xld").Count);
        Assert.Equal(2, (int)ctx.GetVariable("关系1", "Count").Value);
    }

    [Theory]
    [InlineData(AngleRange.None, -90.0)]
    [InlineData(AngleRange.ZeroTo180, 90.0)]
    [InlineData(AngleRange.ZeroTo360, 270.0)]
    [InlineData(AngleRange.Minus45To45, 0.0)]
    public void 两直线夹角_与angle_ll一致_按角度范围折算(AngleRange range, double expectedDegrees)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        // 水平线（向右）转到竖直线（向下）：angle_ll = -π/2
        using var ctx = Context(Polyline((0, 0), (0, 100)), Polyline((0, 0), (100, 0)));
        var tool = new GeometryRelationTool("夹角") { Line1Path = "上游.Xld", Line2Path = "上游2.Xld", Mode = GeometryRelationMode.LineToLineAngle, Range = range };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(expectedDegrees, Single(ctx, "夹角", "AngleDeg"), 9);
        Assert.Equal(AngleMath.ToRadians(expectedDegrees), Single(ctx, "夹角", "Angle"), 9);
        Assert.Equal(expectedDegrees, new AngleConvertTool("换算") { Range = range, InputUnit = AngleUnit.Degree, OutputUnit = AngleUnit.Degree }.Convert(-90.0), 9);
    }

    [Theory]
    [InlineData(GeometryRelationMode.SegmentToSegment, 30.0, 143.17821063276352)]
    [InlineData(GeometryRelationMode.SegmentToLine, 30.0, 50.0)]
    public void 线段距离_与解析值一致(GeometryRelationMode mode, double expectedMin, double expectedMax)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject first = mode == GeometryRelationMode.SegmentToSegment ? Polyline((0, 0), (0, 100)) : Polyline((30, 40), (50, 140));
        HObject second = mode == GeometryRelationMode.SegmentToSegment ? Polyline((30, 40), (30, 140)) : Polyline((0, 0), (0, 100));
        using var ctx = Context(first, second);
        var tool = new GeometryRelationTool("距离") { Line1Path = "上游.Xld", Line2Path = "上游2.Xld", Mode = mode };

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(expectedMin, Single(ctx, "距离", "Distance"), 6);
        Assert.Equal(expectedMax, Single(ctx, "距离", "MaxDistance"), 6);
    }

    [Fact]
    public void 几何关系_个数不一致报错_模式必填校验_参数按模式显示()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = Context(Lines(2, 0, 0), Lines(3, 30, 0));
        var tool = new GeometryRelationTool("关系1") { Line1Path = "上游.Xld", Line2Path = "上游2.Xld", Mode = GeometryRelationMode.SegmentToSegment };
        Assert.Contains("两组对象个数不一致（2 对 3），无法逐一配对", tool.Run(ctx).Message);

        var unset = new GeometryRelationTool("关系2") { Line1Path = "上游.Xld" };
        Assert.Contains("点到直线需要指定点的行、列坐标", ConfigIssues(unset));
        Assert.False(unset.IsParameterVisible(nameof(GeometryRelationTool.Line2Path)));
        Assert.False(unset.IsParameterVisible(nameof(GeometryRelationTool.Range)));
        unset.Mode = GeometryRelationMode.LineToLineAngle;
        Assert.Contains("该模式需要指定第二条直线或线段", ConfigIssues(unset));
        Assert.True(unset.IsParameterVisible(nameof(GeometryRelationTool.Range)));
        Assert.False(unset.IsParameterVisible(nameof(GeometryRelationTool.RangeMin)));
        unset.Range = AngleRange.Custom;
        Assert.True(unset.IsParameterVisible(nameof(GeometryRelationTool.RangeMin)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 几何关系输入为空_按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEmptyObj(out HObject empty);
        using var ctx = Context(empty, Polyline((0, 0), (0, 100)));
        var tool = new GeometryRelationTool("关系1") { Line1Path = "上游.Xld", Line2Path = "上游2.Xld", Mode = GeometryRelationMode.LineToLineAngle, FailWhenNotFound = failWhenNotFound };

        Assert.Equal(!failWhenNotFound, tool.Run(ctx).IsSuccess);
        Assert.False((bool)ctx.GetVariable("关系1", "Found").Value);
        Assert.True(double.IsNaN(Single(ctx, "关系1", "AngleDeg")));
    }

    // ---------------- 注册 ----------------

    [Fact]
    public void 新工具登记固定ID并进入工具箱_可保存加载_交点计算改名但ID不变()
    {
        ToolboxRegistry.RegisterDefaults();
        foreach (var (id, category, name, type) in new[]
                 {
                     ("xld-process", "04 XLD轮廓", "XLD 处理", typeof(XldProcessTool)),
                     ("fit-ellipse-rect", "05 几何测量", "拟合椭圆/矩形", typeof(FitEllipseRectTool)),
                     ("contour-distance", "05 几何测量", "轮廓距离", typeof(ContourDistanceTool)),
                     ("geometry-relation", "05 几何测量", "几何关系测量", typeof(GeometryRelationTool)),
                     ("intersection-lines", "05 几何测量", "交点计算", typeof(IntersectionLinesTool))
                 })
        {
            ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == id);
            Assert.Equal(category, item.Category);
            Assert.Equal(name, item.DisplayName);
            ToolNode node = Assert.IsType<ToolNode>(item.Factory());
            Assert.IsType(type, node.Tool);
            string json = FlowSerializer.SaveNode(node);
            Assert.Contains($"\"ToolId\": \"{id}\"", json);
            Assert.IsType(type, Reload(node.Tool));
        }
    }
}
