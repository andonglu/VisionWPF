using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>MATCH-MEASURE-TOOLS-PLAN 第一批：MT-01（匹配搜索区域与结果排序）、MS-01（metrology 高级参数与输出）。</summary>
public class MatchMeasureBatch1Tests
{
    // ======================= MT-01 公共构造 =======================

    /// <summary>L 形目标（左上角 r, c）：竖条 40×8，底部横条 8×30；occluded 时横条右半被遮住（得分较低）。</summary>
    private static void PaintL(ref HObject image, double r, double c, bool occluded = false)
    {
        HOperatorSet.GenRectangle1(out HObject vertical, r, c, r + 40, c + 8);
        HOperatorSet.GenRectangle1(out HObject horizontal, r + 32, c, r + 40, occluded ? c + 14 : c + 30);
        HOperatorSet.Union2(vertical, horizontal, out HObject shape);
        HOperatorSet.PaintRegion(shape, image, out HObject painted, 220, "fill");
        foreach (HObject o in new[] { vertical, horizontal, shape, image }) o.Dispose();
        image = painted;
    }

    /// <summary>六个目标分两行（行坐标有 ±4 像素的抖动），最左上角的目标被部分遮挡、得分最低，按行 / 列排序时排在最前。</summary>
    private static readonly (double R, double C, bool Occluded)[] Targets =
    {
        (30, 20, true), (44, 160, false), (38, 280, false),
        (140, 40, false), (136, 160, false), (142, 280, false)
    };

    private static HObject TargetsImage()
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", 400, 240);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, 239, 399);
        HOperatorSet.PaintRegion(all, blank, out HObject image, 30, "fill");
        blank.Dispose();
        all.Dispose();
        foreach (var t in Targets)
        {
            PaintL(ref image, t.R, t.C, t.Occluded);
        }
        return image;
    }

    /// <summary>用第 2 个目标（完整）示教形状模板。</summary>
    private static byte[] ShapeModelData(HObject image)
    {
        HOperatorSet.GenRectangle1(out HObject roi, 34, 150, 94, 200);
        using var roiOwner = roi;
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        using var templateOwner = template;
        HOperatorSet.CreateShapeModel(template, "auto", -0.2, 0.4, "auto", "auto", "use_polarity", "auto", "auto", out HTuple modelId);
        try
        {
            return ShapeModelSerialization.Serialize(modelId);
        }
        finally
        {
            HOperatorSet.ClearShapeModel(modelId);
        }
    }

    private static HalconModelMatchTool ShapeTool(byte[] data) => new("匹配1")
    {
        ImagePath = "Input.Image", ShapeModelData = data, NumMatches = 10, MinScore = 0.5,
        FindStartAngle = -0.2, FindExtentAngle = 0.4
    };

    private static FlowContext ImageContext(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static List<MatchResultItem> Items(FlowContext ctx, string module = "匹配1") =>
        ((System.Collections.IEnumerable)ctx.GetVariable(module, "Items").Value).Cast<MatchResultItem>().ToList();

    private static double Single(FlowContext ctx, string module, string name) => Convert.ToDouble(ctx.GetVariable(module, name).Value);

    private static double[] Doubles(FlowContext ctx, string module, string name) =>
        ((System.Collections.IEnumerable)ctx.GetVariable(module, name).Value).Cast<object>().Select(Convert.ToDouble).ToArray();

    private static IReadOnlyList<string> ConfigIssues(ToolBase tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root).Issues.Select(i => i.Message).ToList();
    }

    // ======================= MT-01 兼容性硬门禁 =======================

    [Fact]
    public void 兼容门禁_形状匹配默认参数与旧版逐项一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage();
        byte[] data = ShapeModelData(image);
        HalconModelMatchTool tool = ShapeTool(data);
        Assert.Null(tool.SearchRegionPath);
        Assert.Equal(MatchSortBy.Score, tool.SortBy);
        Assert.Equal(0, tool.RowTolerance);

        HTuple model = ShapeModelSerialization.Deserialize(data);
        HTuple rows, columns, angles, scores;
        try
        {
            HOperatorSet.FindShapeModel(image, model, -0.2, 0.4, 0.5, 10, 0.5, "least_squares", 0, 0.9,
                out rows, out columns, out angles, out scores);
        }
        finally
        {
            HOperatorSet.ClearShapeModel(model);
        }
        using var ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);

        // 旧版：数组按算子返回顺序、Index 为 0 起序号、最佳结果为 items[0]
        List<MatchResultItem> items = Items(ctx);
        Assert.Equal(6, items.Count);
        Assert.Equal(rows.DArr, items.Select(i => i.Row));
        Assert.Equal(columns.DArr, items.Select(i => i.Column));
        Assert.Equal(angles.DArr, items.Select(i => i.Angle));
        Assert.Equal(scores.DArr, Doubles(ctx, "匹配1", "Scores"));
        Assert.Equal(Enumerable.Range(0, 6), items.Select(i => i.Index));
        Assert.Same(items[0], ctx.GetVariable("匹配1", "BestMatch").Value);
        Assert.Equal(rows[0].D, Single(ctx, "匹配1", "Row"));
        Assert.Equal(scores[0].D, Single(ctx, "匹配1", "Score"));
        tool.ReleaseResources();
    }

    [Fact]
    public void 兼容门禁_灰度匹配默认参数与旧版逐项一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage();
        HOperatorSet.GenRectangle1(out HObject roi, 34, 150, 94, 200);
        using var roiOwner = roi;
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        using var templateOwner = template;
        HOperatorSet.CreateNccModel(template, "auto", -0.2, 0.4, "auto", "use_polarity", out HTuple model);
        byte[] data = ShapeModelSerialization.SerializeNcc(model);
        HOperatorSet.FindNccModel(image, model, -0.2, 0.4, 0.5, 10, 0.5, "true", 0,
            out HTuple rows, out HTuple columns, out _, out HTuple scores);
        HOperatorSet.ClearNccModel(model);
        var tool = new HalconGrayMatchTool("灰度1") { ImagePath = "Input.Image", ShapeModelData = data, NumMatches = 10, MinScore = 0.5, FindStartAngle = -0.2, FindExtentAngle = 0.4 };
        using var ctx = ImageContext(image);

        Assert.True(tool.Run(ctx).IsSuccess);
        List<MatchResultItem> items = Items(ctx, "灰度1");
        Assert.True(items.Count >= 5);
        Assert.Equal(rows.DArr, items.Select(i => i.Row));
        Assert.Equal(columns.DArr, items.Select(i => i.Column));
        Assert.Equal(scores.DArr, Doubles(ctx, "灰度1", "Scores"));
        Assert.Same(items[0], ctx.GetVariable("灰度1", "BestMatch").Value);
        tool.ReleaseResources();
    }

    // ======================= MT-01 排序 =======================

    private static List<(double Row, double Column)> Expected(IEnumerable<MatchResultItem> items, MatchSortBy sortBy, double tolerance)
    {
        List<MatchResultItem> list = items.ToList();
        IEnumerable<MatchResultItem> ordered = sortBy switch
        {
            MatchSortBy.Row => list.OrderBy(i => i.Row),
            MatchSortBy.Column => list.OrderBy(i => i.Column),
            MatchSortBy.ColumnThenRow => list.OrderBy(i => i.Column).ThenBy(i => i.Row),
            // 两行目标的参考点行坐标：第一行 50 ~ 64（含遮挡目标），第二行 156 ~ 160
            MatchSortBy.RowThenColumn when tolerance >= 15 => list.OrderBy(i => i.Row < 110 ? 0 : 1).ThenBy(i => i.Column),
            MatchSortBy.RowThenColumn => list.OrderBy(i => i.Row).ThenBy(i => i.Column),
            _ => list
        };
        return ordered.Select(i => (i.Row, i.Column)).ToList();
    }

    [Theory]
    [InlineData(MatchSortBy.Score, 0.0)]
    [InlineData(MatchSortBy.Score, 16.0)]
    [InlineData(MatchSortBy.Row, 0.0)]
    [InlineData(MatchSortBy.Row, 16.0)]
    [InlineData(MatchSortBy.Column, 0.0)]
    [InlineData(MatchSortBy.Column, 16.0)]
    [InlineData(MatchSortBy.RowThenColumn, 0.0)]
    [InlineData(MatchSortBy.RowThenColumn, 16.0)]
    [InlineData(MatchSortBy.ColumnThenRow, 0.0)]
    [InlineData(MatchSortBy.ColumnThenRow, 16.0)]
    public void 排序_五种方式乘行容差_数组随序输出_单值固定为最高分(MatchSortBy sortBy, double tolerance)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage();
        byte[] data = ShapeModelData(image);
        HalconModelMatchTool baseline = ShapeTool(data);
        HalconModelMatchTool sorted = ShapeTool(data);
        sorted.SortBy = sortBy;
        sorted.RowTolerance = tolerance;
        using var baseCtx = ImageContext(image);
        using var ctx = ImageContext(image);

        Assert.True(baseline.Run(baseCtx).IsSuccess);
        Assert.True(sorted.Run(ctx).IsSuccess);

        List<MatchResultItem> original = Items(baseCtx);
        List<MatchResultItem> items = Items(ctx);
        Assert.Equal(Expected(original, sortBy, tolerance), items.Select(i => (i.Row, i.Column)).ToList());
        Assert.Equal(Enumerable.Range(0, items.Count), items.Select(i => i.Index));
        // 数组随新顺序输出
        Assert.Equal(items.Select(i => i.Score), Doubles(ctx, "匹配1", "Scores"));
        var homMats = ((System.Collections.IEnumerable)ctx.GetVariable("匹配1", "HomMats").Value).Cast<HomMat2D>().ToList();
        Assert.Equal(items.Select(i => i.HomMat), homMats);
        var contours = ((System.Collections.IEnumerable)ctx.GetVariable("匹配1", "Contours").Value).Cast<HalconXld>().ToList();
        Assert.Equal(items.Select(i => i.Contour), contours.Select(c => c.Object));
        // 单值与最佳结果固定为最高分，不随排序变化（遮挡目标在左上角，按行 / 列排序时排在最前）
        double maxScore = items.Max(i => i.Score);
        var best = (MatchResultItem)ctx.GetVariable("匹配1", "BestMatch").Value;
        Assert.Equal(maxScore, best.Score);
        Assert.Equal(Single(baseCtx, "匹配1", "Row"), Single(ctx, "匹配1", "Row"));
        Assert.Equal(Single(baseCtx, "匹配1", "Column"), Single(ctx, "匹配1", "Column"));
        Assert.Equal(Single(baseCtx, "匹配1", "Angle"), Single(ctx, "匹配1", "Angle"));
        Assert.Equal(maxScore, Single(ctx, "匹配1", "Score"));
        Assert.Same(best.HomMat, ctx.GetVariable("匹配1", "BestHomMat").Value);
        if (sortBy is MatchSortBy.Row or MatchSortBy.Column or MatchSortBy.RowThenColumn or MatchSortBy.ColumnThenRow)
        {
            Assert.True(items[0].Score < maxScore, "左上角的遮挡目标排在最前且不是最高分");
        }
        baseline.ReleaseResources();
        sorted.ReleaseResources();
    }

    [Fact]
    public void 排序_先行后列的行容差把同一行的抖动目标归为一行()
    {
        var items = new[]
        {
            new MatchResultItem { Row = 64, Column = 300 }, new MatchResultItem { Row = 58, Column = 200 },
            new MatchResultItem { Row = 61, Column = 100 }, new MatchResultItem { Row = 160, Column = 50 }
        };
        Assert.Equal(new[] { 200.0, 100.0, 300.0, 50.0 }, HalconMatchToolBase.SortMatches(items, MatchSortBy.RowThenColumn, 0).Select(i => i.Column));
        Assert.Equal(new[] { 100.0, 200.0, 300.0, 50.0 }, HalconMatchToolBase.SortMatches(items, MatchSortBy.RowThenColumn, 8).Select(i => i.Column));
        // 行容差按“当前行第一项”计算，不会把逐步漂移的整列连成一行
        var drifting = new[] { new MatchResultItem { Row = 0, Column = 3 }, new MatchResultItem { Row = 6, Column = 2 }, new MatchResultItem { Row = 12, Column = 1 } };
        Assert.Equal(new[] { 2.0, 3.0, 1.0 }, HalconMatchToolBase.SortMatches(drifting, MatchSortBy.RowThenColumn, 8).Select(i => i.Column));
    }

    [Fact]
    public void 排序参数_行容差只在先行后列时显示_负数校验报错()
    {
        var tool = new HalconModelMatchTool("匹配1");
        Assert.False(tool.IsParameterVisible(nameof(HalconMatchToolBase.RowTolerance)));
        tool.SortBy = MatchSortBy.RowThenColumn;
        Assert.True(tool.IsParameterVisible(nameof(HalconMatchToolBase.RowTolerance)));
        tool.RowTolerance = -1;
        Assert.Contains("行容差不能小于 0", ConfigIssues(tool));
    }

    // ======================= MT-01 搜索区域 =======================

    [Fact]
    public void 搜索区域_只找到区域内的目标_Image输出仍为原图()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage();
        HalconModelMatchTool tool = ShapeTool(ShapeModelData(image));
        tool.SearchRegionPath = "区域.Right";
        using var ctx = ImageContext(image);
        HOperatorSet.GenRectangle1(out HObject right, 0, 250, 239, 399);
        ctx.SetVariable(Variable.Object("区域", "Right", HalconRegion.Owned(right), 1));

        Assert.True(tool.Run(ctx).IsSuccess);
        List<MatchResultItem> items = Items(ctx);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.Column > 250));
        var output = (HalconImage)ctx.GetVariable("匹配1", "Image").Value;
        Assert.Same(((HalconImage)ctx.GetVariable("Input", "Image").Value).Object, output.Object);
        HOperatorSet.GetDomain(output.Object, out HObject domain);
        using (domain)
        {
            HOperatorSet.AreaCenter(domain, out HTuple area, out _, out _);
            Assert.Equal(400 * 240, area.I);
        }
        Assert.Contains(ctx.Log, l => l.Contains("只在搜索区域 区域.Right 内查找"));
        tool.ReleaseResources();
    }

    [Fact]
    public void 搜索区域_限制的是模型参考点_很小的区域只要含参考点即可找到整个目标()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage();
        HalconModelMatchTool tool = ShapeTool(ShapeModelData(image));
        using var full = ImageContext(image);
        Assert.True(ShapeTool(tool.ShapeModelData).Run(full).IsSuccess);
        MatchResultItem target = Items(full).OrderBy(i => Math.Abs(i.Row - 160) + Math.Abs(i.Column - 175)).First();

        tool.SearchRegionPath = "区域.Point";
        using var ctx = ImageContext(image);
        HOperatorSet.GenRectangle1(out HObject tiny, target.Row - 2, target.Column - 2, target.Row + 2, target.Column + 2);
        ctx.SetVariable(Variable.Object("区域", "Point", HalconRegion.Owned(tiny), 1));

        Assert.True(tool.Run(ctx).IsSuccess);
        MatchResultItem found = Assert.Single(Items(ctx));
        Assert.Equal(target.Row, found.Row, 3);
        Assert.Equal(target.Column, found.Column, 3);
        tool.ReleaseResources();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 搜索区域为空_按未找到策略(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage();
        HalconModelMatchTool tool = ShapeTool(ShapeModelData(image));
        tool.SearchRegionPath = "区域.Empty";
        tool.FailWhenNotFound = failWhenNotFound;
        using var ctx = ImageContext(image);
        HOperatorSet.GenEmptyRegion(out HObject empty);
        ctx.SetVariable(Variable.Object("区域", "Empty", HalconRegion.Owned(empty), 1));

        NodeResult result = tool.Run(ctx);
        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.Contains("搜索区域为空", failWhenNotFound ? result.Message : ctx.Log.Last());
        Assert.False((bool)ctx.GetVariable("匹配1", "Found").Value);
        Assert.Equal(0, (int)ctx.GetVariable("匹配1", "MatchCount").Value);
        Assert.True(double.IsNaN(Single(ctx, "匹配1", "Row")));
        tool.ReleaseResources();
    }

    [Fact]
    public void 搜索区域引用无效_明确失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = TargetsImage();
        HalconModelMatchTool tool = ShapeTool(ShapeModelData(image));
        tool.SearchRegionPath = "不存在.Region";
        using var ctx = ImageContext(image);

        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("搜索区域引用无效：不存在.Region", result.Message);
        tool.ReleaseResources();
    }

    [Fact]
    public void 搜索区域对描述子匹配同样生效()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.SetSystem("seed_rand", 42);
        HOperatorSet.GenImageConst(out HObject blank, "byte", 400, 400);
        HOperatorSet.GenImageProto(blank, out HObject gray, 128);
        HOperatorSet.AddNoiseWhite(gray, out HObject noisy, 120);
        HOperatorSet.MeanImage(noisy, out HObject texture, 5, 5);
        blank.Dispose(); gray.Dispose(); noisy.Dispose();
        using var textureOwner = texture;
        var tool = new DescriptorMatchTool("描述子1")
        {
            ImagePath = "Input.Image", Depth = 7, NumberFerns = 10, PatchSize = 17, MinScale = 0.8, MaxScale = 1.2,
            UseModelFileCache = false, SearchRegionPath = "区域.Search"
        };
        tool.CreateTemplate(texture, 120, 120, 280, 280);

        using var inside = ImageContext(texture);
        HOperatorSet.GenRectangle1(out HObject around, 100, 100, 300, 300);
        inside.SetVariable(Variable.Object("区域", "Search", HalconRegion.Owned(around), 1));
        Assert.True(tool.Run(inside).IsSuccess);
        Assert.InRange(Single(inside, "描述子1", "Row"), 198, 202);

        using var outside = ImageContext(texture);
        HOperatorSet.GenRectangle1(out HObject corner, 0, 0, 40, 40);
        outside.SetVariable(Variable.Object("区域", "Search", HalconRegion.Owned(corner), 1));
        tool.FailWhenNotFound = false;
        Assert.True(tool.Run(outside).IsSuccess);
        Assert.False((bool)outside.GetVariable("描述子1", "Found").Value);
        tool.ReleaseResources();
    }

    [Fact]
    public void 新参数保存加载_历史文件缺省时取默认值()
    {
        var tool = new HalconModelMatchTool("匹配1") { SearchRegionPath = "区域.R", SortBy = MatchSortBy.ColumnThenRow, RowTolerance = 6 };
        var loaded = (HalconModelMatchTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(tool)))).Tool;
        Assert.Equal("区域.R", loaded.SearchRegionPath);
        Assert.Equal(MatchSortBy.ColumnThenRow, loaded.SortBy);
        Assert.Equal(6, loaded.RowTolerance);

        var node = System.Text.Json.Nodes.JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new HalconModelMatchTool("匹配2"))))!.AsObject();
        var properties = node["Tool"]!["Properties"]!.AsObject();
        Assert.True(properties.Remove("SearchRegionPath") && properties.Remove("SortBy") && properties.Remove("RowTolerance"));
        var legacy = (HalconModelMatchTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(node.ToJsonString())).Tool;
        Assert.Null(legacy.SearchRegionPath);
        Assert.Equal(MatchSortBy.Score, legacy.SortBy);
        Assert.Equal(0, legacy.RowTolerance);
        Assert.Equal(4, (int)MatchSortBy.ColumnThenRow);
    }

    // ======================= MS-01 公共构造 =======================

    /// <summary>亮带：每条为行 r..r+40、列 c1..c2，背景 30。</summary>
    private static HObject BandsImage(params (double R, double C1, double C2)[] bands)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", 600, 400);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, 399, 599);
        HOperatorSet.PaintRegion(all, blank, out HObject image, 30, "fill");
        blank.Dispose();
        all.Dispose();
        foreach (var b in bands)
        {
            HOperatorSet.GenRectangle1(out HObject band, b.R, b.C1, b.R + 40, b.C2);
            HOperatorSet.PaintRegion(band, image, out HObject painted, 200, "fill");
            band.Dispose();
            image.Dispose();
            image = painted;
        }
        return image;
    }

    private static HObject ShapeImage(Func<HObject> shape)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", 600, 400);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, 399, 599);
        HOperatorSet.PaintRegion(all, blank, out HObject background, 30, "fill");
        using HObject region = shape();
        HOperatorSet.PaintRegion(region, background, out HObject image, 200, "fill");
        blank.Dispose(); all.Dispose(); background.Dispose();
        return image;
    }

    /// <summary>旧版 metrology 调用：只传 measure_transition / measure_select。</summary>
    private static double[] LegacyMetrology(HObject image, Func<HTuple, HTuple, HTuple, int> addObject)
    {
        HOperatorSet.CreateMetrologyModel(out HTuple model);
        try
        {
            HOperatorSet.SetMetrologyModelImageSize(model, 600, 400);
            addObject(model, new HTuple("measure_transition", "measure_select"), new HTuple("all", "all"));
            HOperatorSet.ApplyMetrologyModel(image, model);
            HOperatorSet.GetMetrologyObjectResult(model, 0, "all", "result_type", "all_param", out HTuple param);
            return param.DArr;
        }
        finally
        {
            HOperatorSet.ClearMetrologyModel(model);
        }
    }

    private static LineFollowMeasureTool LineTool(double row = 100) => new("直线1")
    {
        BaseRow1 = row, BaseColumn1 = 150, BaseRow2 = row, BaseColumn2 = 350
    };

    // ======================= MS-01 兼容性硬门禁 =======================

    [Fact]
    public void 兼容门禁_四个metrology工具默认参数与旧版调用结果完全相同()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject bands = BandsImage((100, 100, 500));
        var line = LineTool();
        using (var ctx = ImageContext(bands))
        {
            Assert.True(line.Run(ctx).IsSuccess);
            double[] legacy = LegacyMetrology(bands, (m, n, v) =>
            {
                HOperatorSet.AddMetrologyObjectLineMeasure(m, 100.0, 150.0, 100.0, 350.0, 10.0, 3.0, 1.0, 20.0, n, v, out HTuple index);
                return index.I;
            });
            Assert.Equal(legacy, new[] { Single(ctx, "直线1", "Row1"), Single(ctx, "直线1", "Column1"), Single(ctx, "直线1", "Row2"), Single(ctx, "直线1", "Column2") });
            Assert.Equal(1, (int)ctx.GetVariable("直线1", "InstanceCount").Value);
        }

        using HObject rectImage = ShapeImage(() => { HOperatorSet.GenRectangle2(out HObject r, 200, 300, 0.2, 120, 60); return r; });
        var rect = new RectangleFollowMeasureTool("矩形1") { BaseRow = 200, BaseColumn = 300, BasePhi = 0.2, BaseLength1 = 120, BaseLength2 = 60 };
        using (var ctx = ImageContext(rectImage))
        {
            Assert.True(rect.Run(ctx).IsSuccess);
            double[] legacy = LegacyMetrology(rectImage, (m, n, v) =>
            {
                HOperatorSet.AddMetrologyObjectRectangle2Measure(m, 200.0, 300.0, 0.2, 120.0, 60.0, 10.0, 3.0, 1.0, 20.0, n, v, out HTuple index);
                return index.I;
            });
            Assert.Equal(legacy, new[] { Single(ctx, "矩形1", "Row"), Single(ctx, "矩形1", "Column"), Single(ctx, "矩形1", "Phi"), Single(ctx, "矩形1", "Length1"), Single(ctx, "矩形1", "Length2") });
        }

        using HObject circleImage = ShapeImage(() => { HOperatorSet.GenCircle(out HObject c, 200, 300, 80); return c; });
        var circle = new CircleFollowMeasureTool("圆1") { BaseRow = 200, BaseColumn = 300, BaseRadius = 80 };
        using (var ctx = ImageContext(circleImage))
        {
            Assert.True(circle.Run(ctx).IsSuccess);
            double[] legacy = LegacyMetrology(circleImage, (m, n, v) =>
            {
                HOperatorSet.AddMetrologyObjectCircleMeasure(m, 200.0, 300.0, 80.0, 10.0, 3.0, 1.0, 20.0,
                    n.TupleConcat("start_phi").TupleConcat("end_phi"), v.TupleConcat(0.0).TupleConcat(Math.PI * 2), out HTuple index);
                return index.I;
            });
            Assert.Equal(legacy, new[] { Single(ctx, "圆1", "Row"), Single(ctx, "圆1", "Column"), Single(ctx, "圆1", "Radius") });
        }

        using HObject ellipseImage = ShapeImage(() => { HOperatorSet.GenEllipse(out HObject e, 200, 300, 0.3, 100, 50); return e; });
        var ellipse = new EllipseFollowMeasureTool("椭圆1") { EllipseRow = 200, EllipseColumn = 300, EllipseAngle = 0.3, EllipseLength1 = 100, EllipseLength2 = 50 };
        using (var ctx = ImageContext(ellipseImage))
        {
            Assert.True(ellipse.Run(ctx).IsSuccess);
            double[] legacy = LegacyMetrology(ellipseImage, (m, n, v) =>
            {
                HOperatorSet.AddMetrologyObjectEllipseMeasure(m, 200.0, 300.0, 0.3, 100.0, 50.0, 7.0, 2.0, 1.0, 1.0, n, v, out HTuple index);
                return index.I;
            });
            Assert.Equal(legacy, new[] { Single(ctx, "椭圆1", "Row"), Single(ctx, "椭圆1", "Column"), Single(ctx, "椭圆1", "Phi"), Single(ctx, "椭圆1", "Length1"), Single(ctx, "椭圆1", "Length2") });
        }
    }

    [Fact]
    public void 默认值等于HALCON实测默认_一维卡尺工具不受影响()
    {
        var line = new LineFollowMeasureTool("直线1");
        Assert.Equal(0, line.NumMeasures);
        Assert.Equal(10, line.MeasureDistance);
        Assert.Equal(0.7, line.MinScore);
        Assert.Equal(1, line.NumInstances);
        Assert.Equal(3.5, line.DistanceThreshold);
        Assert.Equal(MetrologyInterpolation.nearest_neighbor, line.MeasureInterpolation);
        foreach (Type type in new[] { typeof(LineFollowMeasureTool), typeof(RectangleFollowMeasureTool), typeof(CircleFollowMeasureTool), typeof(EllipseFollowMeasureTool) })
        {
            Assert.True(typeof(MetrologyMeasureToolBase).IsAssignableFrom(type), type.Name);
            Assert.Contains(ToolMetadata.GetOutputs(type), o => o.Name == "MeasurePoints" && o.ElementClrType == typeof(HalconXld));
        }
        foreach (Type type in new[] { typeof(OneDCaliperFollowMeasureTool), typeof(ArcCaliperFollowMeasureTool) })
        {
            Assert.False(typeof(MetrologyMeasureToolBase).IsAssignableFrom(type), type.Name);
            Assert.DoesNotContain(ToolMetadata.GetOutputs(type), o => o.Name == "Score" || o.Name == "MeasurePoints");
            Assert.Null(type.GetProperty("NumInstances"));
        }
    }

    // ======================= MS-01 参数生效 =======================

    [Theory]
    [InlineData(9, 10.0, 9)]
    [InlineData(0, 40.0, 6)]
    [InlineData(0, 10.0, 21)]
    public void 卡尺数与卡尺间距二选一_边缘点数与卡尺数一致(int numMeasures, double distance, int expectedPoints)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = BandsImage((100, 100, 500));
        var tool = LineTool();
        tool.NumMeasures = numMeasures;
        tool.MeasureDistance = distance;
        using var ctx = ImageContext(image);

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(expectedPoints, ctx.GetVariable("直线1", "MeasurePoints").Count);
        Assert.Equal(1.0, Single(ctx, "直线1", "Score"));
        Assert.Equal(numMeasures > 0, !tool.IsParameterVisible(nameof(MetrologyMeasureToolBase.MeasureDistance)));
    }

    [Fact]
    public void 最低得分生效_边缘只覆盖部分卡尺时默认失败_降低后成功并给出得分()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        // 亮带只覆盖测量范围（列 150 ~ 350）的前 40%
        using HObject image = BandsImage((100, 100, 230));
        var strict = LineTool();
        var relaxed = LineTool();
        relaxed.MinScore = 0.3;
        using var ctx = ImageContext(image);

        Assert.False(strict.Run(ctx).IsSuccess);
        Assert.True(double.IsNaN(Single(ctx, "直线1", "Score")));
        Assert.Equal(0, (int)ctx.GetVariable("直线1", "InstanceCount").Value);
        Assert.True(ctx.GetVariable("直线1", "MeasurePoints").Count > 0, "失败时边缘点仍输出，便于判断卡尺是否找到边缘");
        Assert.True(relaxed.Run(ctx).IsSuccess);
        Assert.InRange(Single(ctx, "直线1", "Score"), 0.3, 0.7);
    }

    [Fact]
    public void 多实例_两条平行直线_单值取第一个实例_数组按实例展开()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = BandsImage((100, 50, 550), (180, 50, 550));
        var tool = new LineFollowMeasureTool("直线1")
        {
            BaseRow1 = 160, BaseColumn1 = 100, BaseRow2 = 160, BaseColumn2 = 500, MeasureLength1 = 70,
            MeasureTransition = "positive", NumInstances = 2
        };
        using var ctx = ImageContext(image);

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(2, (int)ctx.GetVariable("直线1", "InstanceCount").Value);
        double[] rows1 = Doubles(ctx, "直线1", "InstanceRows1");
        Assert.Equal(2, rows1.Length);
        Assert.Contains(rows1, r => Math.Abs(r - 99.5) < 0.6);
        Assert.Contains(rows1, r => Math.Abs(r - 179.5) < 0.6);
        Assert.Equal(rows1[0], Single(ctx, "直线1", "Row1"));
        Assert.Equal(2, Doubles(ctx, "直线1", "Scores").Length);
        Assert.Equal(new[] { 0, 0 }, ((System.Collections.IEnumerable)ctx.GetVariable("直线1", "InstanceSeedIndices").Value).Cast<int>());
        Assert.Equal(Doubles(ctx, "直线1", "Scores")[0], Single(ctx, "直线1", "Score"));
    }

    [Fact]
    public void 多定位矩阵乘多实例_按定位结果到实例展开_InstanceSeedIndices对齐()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = BandsImage((100, 50, 550), (180, 50, 550));
        var tool = new LineFollowMeasureTool("直线1")
        {
            BaseRow1 = 160, BaseColumn1 = 100, BaseRow2 = 160, BaseColumn2 = 300, MeasureLength1 = 70,
            MeasureTransition = "positive", NumInstances = 2, MatrixPath = "定位.HomMats"
        };
        using var ctx = ImageContext(image);
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object, new[] { HomMat2D.Identity, HomMat2D.FromPoses(0, 0, 0, 0, 200, 0) }));

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(4, (int)ctx.GetVariable("直线1", "InstanceCount").Value);
        Assert.Equal(new[] { 0, 0, 1, 1 }, ((System.Collections.IEnumerable)ctx.GetVariable("直线1", "InstanceSeedIndices").Value).Cast<int>());
        double[] columns1 = Doubles(ctx, "直线1", "InstanceColumns1");
        Assert.True(columns1[2] > columns1[0] + 150, "第二个定位结果的实例在右侧");
        Assert.Equal(4, Doubles(ctx, "直线1", "Scores").Length);
    }

    [Fact]
    public void 圆与矩形的实例数组名_插值方式生效()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject circleImage = ShapeImage(() => { HOperatorSet.GenCircle(out HObject c, 200, 300, 80); return c; });
        var circle = new CircleFollowMeasureTool("圆1") { BaseRow = 200, BaseColumn = 300, BaseRadius = 80, MeasureInterpolation = MetrologyInterpolation.bilinear };
        using var ctx = ImageContext(circleImage);

        Assert.True(circle.Run(ctx).IsSuccess);
        Assert.Equal(new[] { Single(ctx, "圆1", "Row") }, Doubles(ctx, "圆1", "InstanceRows"));
        Assert.Equal(new[] { Single(ctx, "圆1", "Radius") }, Doubles(ctx, "圆1", "InstanceRadii"));
        Assert.InRange(Single(ctx, "圆1", "Radius"), 79, 81);
        Assert.Contains(ToolMetadata.GetOutputs(typeof(RectangleFollowMeasureTool)), o => o.Name == "InstanceLengths2");
        Assert.Contains(ToolMetadata.GetOutputs(typeof(EllipseFollowMeasureTool)), o => o.Name == "InstancePhis");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 未找到边缘_按未找到策略_新输出为空(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject blank = BandsImage();
        var tool = LineTool();
        tool.FailWhenNotFound = failWhenNotFound;
        using var ctx = ImageContext(blank);

        Assert.Equal(!failWhenNotFound, tool.Run(ctx).IsSuccess);
        Assert.False((bool)ctx.GetVariable("直线1", "Found").Value);
        Assert.True(double.IsNaN(Single(ctx, "直线1", "Score")));
        Assert.Equal(0, (int)ctx.GetVariable("直线1", "InstanceCount").Value);
        Assert.Empty(Doubles(ctx, "直线1", "Scores"));
        Assert.Equal(0, ctx.GetVariable("直线1", "MeasurePoints").Count);
    }

    [Fact]
    public void 高级参数校验_保存加载()
    {
        var tool = LineTool();
        tool.NumInstances = 0;
        tool.MinScore = 1.5;
        tool.MeasureDistance = 0;
        IReadOnlyList<string> issues = ConfigIssues(tool);
        Assert.Contains("实例数必须大于 0", issues);
        Assert.Contains("最低得分必须在 0 到 1 之间", issues);
        Assert.Contains("卡尺间距必须大于 0", issues);
        using (var ctx = new FlowContext())
        {
            // 运行时报告第一个配置问题
            Assert.Equal("直线1 MeasureDistance：卡尺间距必须大于 0", tool.Run(ctx).Message);
        }
        tool.NumMeasures = 5;
        Assert.DoesNotContain("卡尺间距必须大于 0", ConfigIssues(tool));

        var saved = new CircleFollowMeasureTool("圆1") { NumMeasures = 12, MinScore = 0.5, NumInstances = 3, DistanceThreshold = 2, MeasureInterpolation = MetrologyInterpolation.bicubic };
        var loaded = (CircleFollowMeasureTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(saved)))).Tool;
        Assert.Equal((12, 0.5, 3, 2.0, MetrologyInterpolation.bicubic), (loaded.NumMeasures, loaded.MinScore, loaded.NumInstances, loaded.DistanceThreshold, loaded.MeasureInterpolation));
    }
}
