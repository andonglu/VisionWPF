using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>TOOLS-REVIEW 回归：未找到策略、输出类型、部分失败、特征输出、工具箱收敛、模型缓存、通道契约。</summary>
public class ToolBehaviorTests
{
    private static HObject BinaryImage(Func<HObject> createRegion)
    {
        HOperatorSet.GenImageConst(out HObject sizeImage, "byte", 300, 300);
        using var sizeOwner = sizeImage;
        HObject region = createRegion();
        using var regionOwner = region;
        HOperatorSet.RegionToBin(region, out HObject image, 255, 0, 300, 300);
        return image;
    }

    private static HObject Rectangle(double row1, double column1, double row2, double column2)
    {
        return BinaryImage(() =>
        {
            HOperatorSet.GenRectangle1(out HObject rect, row1, column1, row2, column2);
            return rect;
        });
    }

    private static FlowContext ContextWithImage(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        return ctx;
    }

    [Fact]
    public void 未找到策略_所有相关内置工具默认失败()
    {
        Type[] policyTypes = typeof(LoadImageTool).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(INotFoundPolicy).IsAssignableFrom(t))
            .ToArray();
        Assert.Contains(typeof(HalconModelMatchTool), policyTypes);
        Assert.Contains(typeof(LineFollowMeasureTool), policyTypes);
        Assert.Contains(typeof(SelectRegionTool), policyTypes);
        Assert.Contains(typeof(Barcode1DTool), policyTypes);
        Assert.Contains(typeof(ColorClassifyTool), policyTypes);
        Assert.Contains(typeof(ColorSegmentTool), policyTypes);
        foreach (Type type in policyTypes)
        {
            var tool = (INotFoundPolicy)Activator.CreateInstance(type, "工具")!;
            Assert.True(tool.FailWhenNotFound, $"{type.Name} 默认应在未找到时失败");
            // 颜色识别的“未找到”信号是 Count=0（输出无 Found，见 RECOGNITION-TOOLS-PLAN RC-03），其余工具都有 Found 输出
            if (type != typeof(ColorClassifyTool))
            {
                Assert.Contains(ToolMetadata.GetOutputs(type), o => o.Name == "Found" && o.Type == VariableType.Bool);
            }
        }
    }

    [Fact]
    public void 未找到策略_参数可保存加载()
    {
        var tool = new SelectRegionTool("筛选1") { FailWhenNotFound = false };
        var loaded = Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(tool))));
        Assert.False(Assert.IsType<SelectRegionTool>(loaded.Tool).FailWhenNotFound);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 区域筛选为空_按策略失败或输出Found为false继续(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenRectangle1(out HObject region, 10, 10, 20, 20);
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("上游", "Region", HalconRegion.Owned(region), 1));
        var tool = new SelectRegionTool("筛选1") { RegionPath = "上游.Region", Min = 1000, FailWhenNotFound = failWhenNotFound };

        NodeResult result = tool.Run(ctx);

        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.Contains("区域筛选结果为空", failWhenNotFound ? result.Message : ctx.Log.Last());
        Assert.False((bool)ctx.GetVariable("筛选1", "Found").Value);
        Assert.Equal(0, (int)ctx.GetVariable("筛选1", "Count").Value);
    }

    [Fact]
    public void 流程中未找到继续_下游IfElse可按Found分支()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Rectangle(100, 100, 200, 200);
        using var imageOwner = image;
        using var ctx = ContextWithImage(image);
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new LineFollowMeasureTool("直线1")
        {
            BaseRow1 = 20, BaseColumn1 = 20, BaseRow2 = 20, BaseColumn2 = 60,
            FailWhenNotFound = false
        }));
        root.Children.Add(new ToolNode(new DelegateTool("后续", c => c.AddLog(FlowLogLevel.Info, "后续节点已执行"))));

        var run = new VisionFlow.Engine.FlowEngine().Run(root, ctx);

        Assert.True(run.IsSuccess, run.Message);
        Assert.False((bool)ctx.GetVariable("直线1", "Found").Value);
        Assert.True(double.IsNaN((double)ctx.GetVariable("直线1", "Row1").Value));
        Assert.Contains(ctx.Log, l => l.Contains("后续节点已执行"));
    }

    [Fact]
    public void 轮廓输出为XLD类型_匹配与测量结果可被XLD工具引用()
    {
        foreach (Type type in new[] { typeof(HalconModelMatchTool), typeof(HalconGrayMatchTool),
                     typeof(HalconScaledShapeMatchTool), typeof(HalconLocalDeformableMatchTool) })
        {
            IReadOnlyList<ToolOutputDef> outputs = ToolMetadata.GetOutputs(type);
            Assert.Equal(typeof(HalconXld), outputs.Single(o => o.Name == "ResultContour").ElementClrType);
            ToolOutputDef contours = outputs.Single(o => o.Name == "Contours");
            Assert.Equal(VariableKind.Array, contours.Kind);
            Assert.Equal(typeof(HalconXld), contours.ElementClrType);
        }
        foreach (Type type in new[] { typeof(LineFollowMeasureTool), typeof(RectangleFollowMeasureTool),
                     typeof(CircleFollowMeasureTool), typeof(EllipseFollowMeasureTool),
                     typeof(OneDCaliperFollowMeasureTool), typeof(ArcCaliperFollowMeasureTool) })
        {
            Assert.Equal(typeof(HalconXld), ToolMetadata.GetOutputs(type).Single(o => o.Name == "ResultContour").ElementClrType);
        }
    }

    [Fact]
    public void 直线测量轮廓_直接接入线线交点得到矩形角点()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Rectangle(100, 100, 200, 220);
        using var imageOwner = image;
        using var ctx = ContextWithImage(image);
        var top = new LineFollowMeasureTool("上边") { BaseRow1 = 100, BaseColumn1 = 130, BaseRow2 = 100, BaseColumn2 = 190 };
        var left = new LineFollowMeasureTool("左边") { BaseRow1 = 130, BaseColumn1 = 100, BaseRow2 = 170, BaseColumn2 = 100 };
        var corner = new IntersectionLinesTool("角点") { Line1Path = "上边.ResultContour", Line2Path = "左边.ResultContour" };

        Assert.True(top.Run(ctx).IsSuccess);
        Assert.True(left.Run(ctx).IsSuccess);
        NodeResult result = corner.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.InRange((double)ctx.GetVariable("角点", "Row").Value, 98.5, 100.5);
        Assert.InRange((double)ctx.GetVariable("角点", "Column").Value, 98.5, 100.5);
    }

    [Fact]
    public void 多矩阵测量部分失败_记录警告与失败数且结果数量同步()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Rectangle(100, 100, 200, 200);
        using var imageOwner = image;
        using var ctx = ContextWithImage(image);
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object, new[]
        {
            HomMat2D.Identity,
            HomMat2D.FromPoses(0, 0, 0, -80, 0, 0)
        }));
        var tool = new LineFollowMeasureTool("直线1")
        {
            MatrixPath = "定位.HomMats",
            BaseRow1 = 100, BaseColumn1 = 130, BaseRow2 = 100, BaseColumn2 = 170
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, (int)ctx.GetVariable("直线1", "Count").Value);
        Assert.Equal(1, (int)ctx.GetVariable("直线1", "FailedCount").Value);
        Assert.Contains(ctx.StructuredLogs, l => l.Level == FlowLogLevel.Warning && l.Message.Contains("第 1 个定位结果测量失败"));
        Variable results = ctx.GetVariable("直线1", "Results");
        Assert.Equal(results.GetValue<List<LineMeasureResult>>().Count, results.Count);
    }

    [Fact]
    public void 一维卡尺距离_按measure_pos语义输出相邻边缘距离()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Rectangle(100, 120, 200, 180);
        using var imageOwner = image;
        using var ctx = ContextWithImage(image);
        var tool = new OneDCaliperFollowMeasureTool("卡尺1") { BaseRow = 150, BaseColumn = 150, BasePhi = 0, BaseLength1 = 50, BaseLength2 = 5 };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, (int)ctx.GetVariable("卡尺1", "Count").Value);
        var distances = (double[])ctx.GetVariable("卡尺1", "Distances").Value;
        Assert.Single(distances);
        Assert.InRange(distances[0], 58, 62);
        var edges = ctx.GetVariable("卡尺1", "Results").GetValue<List<OneDCaliperMeasureResult>>();
        Assert.True(double.IsNaN(edges.Last().Distance));
    }

    [Fact]
    public void 区域特征_按特征汇总输出()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenRectangle1(out HObject small, 10, 10, 19, 19);
        HOperatorSet.GenRectangle1(out HObject large, 50, 50, 69, 69);
        HOperatorSet.ConcatObj(small, large, out HObject both);
        small.Dispose();
        large.Dispose();
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("上游", "Region", HalconRegion.Owned(both), 2));
        var tool = new RegionFeaturesTool("特征1") { RegionPath = "上游.Region", Features = "area,row" };

        Assert.True(tool.Run(ctx).IsSuccess);

        Assert.Equal(2, (int)ctx.GetVariable("特征1", "ObjectCount").Value);
        Assert.Equal(new[] { 100.0, 400.0, 14.5, 59.5 }, (double[])ctx.GetVariable("特征1", "Values").Value);
        var features = (FeatureValues[])ctx.GetVariable("特征1", "Features").Value;
        Assert.Equal("area", features[0].Name);
        Assert.Equal(100, features[0].Min);
        Assert.Equal(400, features[0].Max);
        Assert.Equal(59.5, features[1].Max);

        // 成员链引用（特征1.Features[0].Max）可以通过校验，供条件判断直接使用
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new RegionFeaturesTool("特征1") { RegionPath = "Input.Image" }));
        root.Children.Add(new ToolNode(new AffinePointTool("坐标1")
        {
            RowPath = "特征1.Features[0].Max",
            ColumnPath = "特征1.Features[1].Mean",
            CalibrationFile = "calib.tup"
        }));
        FlowValidationResult validation = FlowValidator.Validate(root);
        Assert.DoesNotContain(validation.Issues, i => i.Message.Contains("Features"));
    }

    [Fact]
    public void 边缘选择_按特征分别设置范围且数量不一致时明确失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenContourPolygonXld(out HObject shortLine, new HTuple(0.0, 10.0), new HTuple(0.0, 0.0));
        HOperatorSet.GenContourPolygonXld(out HObject longLine, new HTuple(0.0, 100.0), new HTuple(50.0, 50.0));
        HOperatorSet.ConcatObj(shortLine, longLine, out HObject both);
        shortLine.Dispose();
        longLine.Dispose();
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("上游", "Xld", HalconXld.Owned(both), 2));
        var tool = new SelectContourTool("选择1")
        {
            XldPath = "上游.Xld",
            Features = "contlength,column1",
            MinValues = "50,40",
            MaxValues = "200,60"
        };

        NodeResult selected = tool.Run(ctx);
        Assert.True(selected.IsSuccess, selected.Message);
        Assert.Equal(1, (int)ctx.GetVariable("选择1", "Count").Value);

        tool.MaxValues = "200";
        NodeResult mismatch = tool.Run(ctx);
        Assert.False(mismatch.IsSuccess);
        Assert.Contains("MaxValues", mismatch.Message);
    }

    [Fact]
    public void 工具箱_形态学与Union1入口并入区域处理_历史类型仍可加载()
    {
        ToolboxRegistry.RegisterDefaults();
        Assert.DoesNotContain(ToolboxRegistry.Items, i => i.Id == "morphology" || i.Id == "region-union1");
        foreach (ToolBase legacy in new ToolBase[] { new MorphologyTool("形态学1"), new RegionUnion1Tool("Union1") })
        {
            var loaded = Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(legacy))));
            Assert.IsType(legacy.GetType(), loaded.Tool);
        }
    }

    [Theory]
    [InlineData(RegionProcessOp.DilationRectangle, 11, 21 * 21)]
    [InlineData(RegionProcessOp.ErosionRectangle, 5, 7 * 7)]
    public void 区域处理_矩形形态学(RegionProcessOp op, int size, int expectedArea)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenRectangle1(out HObject region, 10, 10, 20, 20);
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("上游", "Region", HalconRegion.Owned(region), 1));
        var tool = new RegionProcessTool("处理1") { RegionPath = "上游.Region", Method = op, Width = size, Height = size };

        Assert.True(tool.Run(ctx).IsSuccess);

        var output = (HalconRegion)ctx.GetVariable("处理1", "Region").Value;
        HOperatorSet.AreaCenter(output.Object, out HTuple area, out _, out _);
        Assert.Equal(expectedArea, area.I);
    }

    [Fact]
    public void 匹配模型_同一实例复用_模板数据替换后重新加载()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Rectangle(100, 100, 160, 200);
        using var imageOwner = image;
        HOperatorSet.GenRectangle1(out HObject roi, 80, 80, 180, 220);
        using var roiOwner = roi;
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        using var templateOwner = template;
        HOperatorSet.CreateShapeModel(template, "auto", -0.2, 0.4, "auto", "auto", "use_polarity", "auto", "auto", out HTuple modelId);
        byte[] data;
        try
        {
            data = ShapeModelSerialization.Serialize(modelId);
        }
        finally
        {
            HOperatorSet.ClearShapeModel(modelId);
        }
        var tool = new HalconModelMatchTool("匹配1") { ImagePath = "Input.Image", ShapeModelData = data, NumMatches = 1, FindStartAngle = -0.2, FindExtentAngle = 0.4 };

        int LoadLogs(FlowContext c) => c.Log.Count(l => l.Contains("模型加载耗时"));
        using (var first = ContextWithImage(image))
        {
            Assert.True(tool.Run(first).IsSuccess);
            Assert.Equal(1, LoadLogs(first));
        }
        using (var second = ContextWithImage(image))
        {
            Assert.True(tool.Run(second).IsSuccess);
            Assert.Equal(0, LoadLogs(second));
        }
        tool.ShapeModelData = (byte[])data.Clone();
        using (var third = ContextWithImage(image))
        {
            Assert.True(tool.Run(third).IsSuccess);
            Assert.Equal(1, LoadLogs(third));
            Assert.True((bool)third.GetVariable("匹配1", "Found").Value);
        }
    }

    [Fact]
    public void 匹配未找到继续_最佳结果显式为空()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject teach = Rectangle(100, 100, 160, 200);
        using var teachOwner = teach;
        HOperatorSet.GenRectangle1(out HObject roi, 80, 80, 180, 220);
        using var roiOwner = roi;
        HOperatorSet.ReduceDomain(teach, roi, out HObject template);
        using var templateOwner = template;
        HOperatorSet.CreateShapeModel(template, "auto", -0.2, 0.4, "auto", "auto", "use_polarity", "auto", "auto", out HTuple modelId);
        byte[] data;
        try
        {
            data = ShapeModelSerialization.Serialize(modelId);
        }
        finally
        {
            HOperatorSet.ClearShapeModel(modelId);
        }
        HOperatorSet.GenImageConst(out HObject blank, "byte", 300, 300);
        HOperatorSet.GenImageProto(blank, out HObject empty, 0);
        blank.Dispose();
        using var emptyOwner = empty;
        using var ctx = ContextWithImage(empty);
        var tool = new HalconModelMatchTool("匹配1") { ImagePath = "Input.Image", ShapeModelData = data, FailWhenNotFound = false };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0, (int)ctx.GetVariable("匹配1", "MatchCount").Value);
        Assert.False((bool)ctx.GetVariable("匹配1", "Found").Value);
        Assert.Null(ctx.GetVariable("匹配1", "BestHomMat").Value);
        Assert.Empty((HalconXld[])ctx.GetVariable("匹配1", "Contours").Value);
    }

    [Fact]
    public void 通道分解_灰度图缺少的通道输出为空且越界序号失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Rectangle(10, 10, 20, 20);
        using var imageOwner = image;
        using var ctx = ContextWithImage(image);
        var tool = new DecomposeChannelsTool("分解1");

        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.NotNull(ctx.GetVariable("分解1", "Channel1").Value);
        Assert.Null(ctx.GetVariable("分解1", "Channel2").Value);
        Assert.Null(ctx.GetVariable("分解1", "Channel3").Value);

        tool.Index = 2;
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("超出范围", result.Message);
    }
}
