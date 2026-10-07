using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>MATCH-MEASURE-TOOLS-PLAN 第四批：MS-06（找角）、MS-05（灰度投影），以及 metrology 基类“执行 + 按对象读取”的拆分回归。</summary>
public class MatchMeasureBatch4Tests
{
    private static FlowContext ImageContext(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static double Single(FlowContext ctx, string module, string name) => Convert.ToDouble(ctx.GetVariable(module, name).Value);

    private static T[] Array<T>(FlowContext ctx, string module, string name) =>
        ((System.Collections.IEnumerable)ctx.GetVariable(module, name).Value).Cast<object>().Select(v => (T)Convert.ChangeType(v, typeof(T))).ToArray();

    private static IReadOnlyList<string> ConfigIssues(ToolBase tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root).Issues.Select(i => i.Message).ToList();
    }

    private static HObject Blank(int width = 640, int height = 480, int gray = 30)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", width, height);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, height - 1, width - 1);
        HOperatorSet.PaintRegion(all, blank, out HObject image, gray, "fill");
        blank.Dispose();
        all.Dispose();
        return image;
    }

    /// <summary>亮矩形：行 200~399、列 150~449（上边缘 199.5、左边缘 149.5，左上角 (199.5, 149.5)）。</summary>
    private static HObject RectangleImage()
    {
        HObject image = Blank();
        HOperatorSet.GenRectangle1(out HObject rect, 200, 150, 399, 449);
        HOperatorSet.PaintRegion(rect, image, out HObject painted, 210, "fill");
        rect.Dispose();
        image.Dispose();
        return painted;
    }

    /// <summary>边 1 沿上边缘从左到右，边 2 沿左边缘从上到下。</summary>
    private static CornerFindTool Corner(string name = "找角1") => new(name)
    {
        TeachLine1Row1 = 200, TeachLine1Column1 = 200, TeachLine1Row2 = 200, TeachLine1Column2 = 400,
        TeachLine2Row1 = 250, TeachLine2Column1 = 150, TeachLine2Row2 = 380, TeachLine2Column2 = 150,
        MeasureLength1 = 20, MeasureLength2 = 3
    };

    // ======================= MS-06 找角 =======================

    [Fact]
    public void 找角_合成矩形角_交点误差在02像素内_夹角误差在01度内()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = RectangleImage();
        using FlowContext ctx = ImageContext(image);
        var tool = Corner();
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.True((bool)ctx.GetVariable("找角1", "Found").Value);
        Assert.InRange(Single(ctx, "找角1", "CornerRow"), 199.5 - 0.2, 199.5 + 0.2);
        Assert.InRange(Single(ctx, "找角1", "CornerColumn"), 149.5 - 0.2, 149.5 + 0.2);
        // angle_ll：边 1 向右、边 2 向下 → -90°
        Assert.InRange(Single(ctx, "找角1", "AngleDeg"), -90.1, -89.9);
        Assert.Equal(AngleMath.ToDegrees(Single(ctx, "找角1", "Angle")), Single(ctx, "找角1", "AngleDeg"));
        Assert.InRange(Single(ctx, "找角1", "Line1Row1"), 199.3, 199.7);
        Assert.InRange(Single(ctx, "找角1", "Line2Column1"), 149.3, 149.7);
        Assert.True(Single(ctx, "找角1", "Score1") > 0.9 && Single(ctx, "找角1", "Score2") > 0.9);
        var results = (List<CornerMeasureResult>)ctx.GetVariable("找角1", "Results").Value;
        CornerMeasureResult only = Assert.Single(results);
        Assert.Equal(Single(ctx, "找角1", "CornerRow"), only.CornerRow);
        Assert.False(only.Followed);
        // 显示轮廓：两条边的结果轮廓 + 交点十字
        HOperatorSet.CountObj(((HalconXld)ctx.GetVariable("找角1", "ResultContour").Value).Object, out HTuple contours);
        Assert.Equal(3, contours.I);
    }

    [Fact]
    public void 找角_旋转的矩形角_交点与夹角与真值一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject straight = RectangleImage();
        HOperatorSet.VectorAngleToRigid(240, 320, 0, 240, 320, AngleMath.ToRadians(10), out HTuple rotate);
        HOperatorSet.AffineTransImage(straight, out HObject rotated, rotate, "bilinear", "false");
        using HObject image = rotated;
        HOperatorSet.AffineTransPoint2d(rotate, 199.5, 149.5, out HTuple trueRow, out HTuple trueColumn);
        var tool = Corner();
        var teach = new HomMat2D(rotate);
        foreach ((Func<double> get, Action<double> set, Func<double> other, Action<double> setOther) in new (Func<double>, Action<double>, Func<double>, Action<double>)[]
                 {
                     (() => tool.TeachLine1Row1, v => tool.TeachLine1Row1 = v, () => tool.TeachLine1Column1, v => tool.TeachLine1Column1 = v),
                     (() => tool.TeachLine1Row2, v => tool.TeachLine1Row2 = v, () => tool.TeachLine1Column2, v => tool.TeachLine1Column2 = v),
                     (() => tool.TeachLine2Row1, v => tool.TeachLine2Row1 = v, () => tool.TeachLine2Column1, v => tool.TeachLine2Column1 = v),
                     (() => tool.TeachLine2Row2, v => tool.TeachLine2Row2 = v, () => tool.TeachLine2Column2, v => tool.TeachLine2Column2 = v)
                 })
        {
            teach.TransformPoint(get(), other(), out double r, out double c);
            set(r);
            setOther(c);
        }
        using FlowContext ctx = ImageContext(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.InRange(Single(ctx, "找角1", "CornerRow") - trueRow.D, -0.2, 0.2);
        Assert.InRange(Single(ctx, "找角1", "CornerColumn") - trueColumn.D, -0.2, 0.2);
        Assert.InRange(Math.Abs(Single(ctx, "找角1", "AngleDeg")), 89.9, 90.1);
    }

    [Fact]
    public void 找角_跟随矩阵_交点随定位结果移动_多矩阵逐个记录()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject straight = RectangleImage();
        HOperatorSet.HomMat2dIdentity(out HTuple id);
        HOperatorSet.HomMat2dTranslate(id, 10, 20, out HTuple shift);
        HOperatorSet.AffineTransImage(straight, out HObject moved, shift, "constant", "false");
        using HObject image = moved;
        using FlowContext ctx = ImageContext(image);
        ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object, new[] { new HomMat2D(shift), HomMat2D.Identity }));
        var tool = Corner();
        tool.MatrixPath = "定位.HomMats";
        tool.FailWhenNotFound = false;
        Assert.True(tool.Run(ctx).IsSuccess);
        var results = (List<CornerMeasureResult>)ctx.GetVariable("找角1", "Results").Value;
        // 第一个矩阵对准平移后的角；第二个（单位矩阵）在原位置找不到完整的两条边
        Assert.InRange(results[0].CornerRow, 209.3, 209.7);
        Assert.InRange(results[0].CornerColumn, 169.3, 169.7);
        Assert.True(results[0].Followed);
        Assert.InRange((int)ctx.GetVariable("找角1", "FailedCount").Value, 0, 1);
        Assert.Equal(results.Count + (int)ctx.GetVariable("找角1", "FailedCount").Value, 2);
    }

    [Fact]
    public void 找角_两边平行或单边未找到_按未找到处理_交点与角度为NaN()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = RectangleImage();
        // 两条边都放在水平边上（上边、下边）：平行
        var parallel = Corner();
        parallel.TeachLine2Row1 = 400;
        parallel.TeachLine2Column1 = 200;
        parallel.TeachLine2Row2 = 400;
        parallel.TeachLine2Column2 = 400;
        // 边 2 放在没有边缘的地方
        var missing = Corner();
        missing.TeachLine2Row1 = 60;
        missing.TeachLine2Column1 = 560;
        missing.TeachLine2Row2 = 160;
        missing.TeachLine2Column2 = 560;
        foreach ((CornerFindTool tool, string reason) in new[] { (parallel, "平行"), (missing, "边 2 未找到") })
        {
            foreach (bool fail in new[] { true, false })
            {
                tool.FailWhenNotFound = fail;
                using FlowContext ctx = ImageContext(image);
                NodeResult result = tool.Run(ctx);
                Assert.Equal(!fail, result.IsSuccess);
                if (fail)
                {
                    Assert.Contains(reason, result.Message);
                }
                Assert.False((bool)ctx.GetVariable("找角1", "Found").Value);
                foreach (string name in new[] { "CornerRow", "CornerColumn", "Angle", "AngleDeg" })
                {
                    Assert.True(double.IsNaN(Single(ctx, "找角1", name)), name);
                }
            }
        }
    }

    [Fact]
    public void 找角_两条边各取第一个实例_实例数不开放_基类输出按边展开()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = RectangleImage();
        var tool = Corner();
        tool.NumInstances = 3;
        Assert.False(tool.IsParameterVisible("NumInstances"));
        Assert.False(tool.ExposesNumInstances);
        tool.NumInstances = 0;
        Assert.DoesNotContain(tool.CheckConfiguration(), i => i.Parameter == "NumInstances");
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        double[] scores = Array<double>(ctx, "找角1", "Scores");
        Assert.Equal(new[] { Single(ctx, "找角1", "Score1"), Single(ctx, "找角1", "Score2") }, scores);
        Assert.Equal(Single(ctx, "找角1", "Score1"), Single(ctx, "找角1", "Score"));
        Assert.Equal(2, (int)ctx.GetVariable("找角1", "InstanceCount").Value);
        Assert.Equal(new[] { 0, 0 }, Array<int>(ctx, "找角1", "InstanceSeedIndices"));
        Assert.True(ctx.GetVariable("找角1", "MeasurePoints").Count > 10);
        Assert.True(new LineFollowMeasureTool("x").ExposesNumInstances);
        Assert.True(new LineFollowMeasureTool("x").IsParameterVisible("NumInstances"));
    }

    // ======================= metrology 拆分回归门禁 =======================

    private static HObject ShapeImage(Func<HObject> shape)
    {
        HObject image = Blank(600, 400);
        using HObject region = shape();
        HOperatorSet.PaintRegion(region, image, out HObject painted, 200, "fill");
        image.Dispose();
        return painted;
    }

    /// <summary>拆分前的实现：执行后按 "all" 读取轮廓与边缘点、按对象 0 读取参数与得分。</summary>
    private static (double[] Param, double[] Scores, int Points, double[] ContourRows) Legacy(HObject image, MetrologyMeasureToolBase tool, Func<HTuple, HTuple, HTuple, int> add)
    {
        HOperatorSet.CreateMetrologyModel(out HTuple model);
        try
        {
            HOperatorSet.SetMetrologyModelImageSize(model, 600, 400);
            HTuple names = new HTuple("measure_transition", "measure_select", "measure_distance", "min_score", "num_instances", "distance_threshold", "measure_interpolation");
            HTuple values = new HTuple(tool.MeasureTransition, tool.MeasureSelect).TupleConcat(tool.MeasureDistance).TupleConcat(tool.MinScore)
                .TupleConcat(tool.NumInstances).TupleConcat(tool.DistanceThreshold).TupleConcat(tool.MeasureInterpolation.ToString());
            add(model, names, values);
            HOperatorSet.ApplyMetrologyModel(image, model);
            HOperatorSet.GetMetrologyObjectResult(model, 0, "all", "result_type", "all_param", out HTuple param);
            HOperatorSet.GetMetrologyObjectResult(model, 0, "all", "result_type", "score", out HTuple scores);
            HOperatorSet.GetMetrologyObjectResultContour(out HObject contour, model, "all", "all", 1.5);
            HOperatorSet.GetMetrologyObjectMeasures(out HObject regions, model, "all", "all", out HTuple rows, out _);
            regions.Dispose();
            HOperatorSet.GetContourXld(contour.SelectObj(1), out HTuple contourRows, out _);
            contour.Dispose();
            return (param.DArr, scores.Length > 0 && param.Length > 0 ? scores.DArr : System.Array.Empty<double>(), rows.Length, contourRows.DArr);
        }
        finally
        {
            HOperatorSet.ClearMetrologyModel(model);
        }
    }

    private static void AssertSameAsLegacy(MetrologyMeasureToolBase tool, HObject image, string[] instanceNames, Func<HTuple, HTuple, HTuple, int> add)
    {
        var legacy = Legacy(image, tool, add);
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        string m = tool.ModuleName;
        int instances = (int)ctx.GetVariable(m, "InstanceCount").Value;
        var flattened = new List<double>();
        for (int i = 0; i < instances; i++)
        {
            foreach (string name in instanceNames)
            {
                flattened.Add(Array<double>(ctx, m, name)[i]);
            }
        }
        Assert.Equal(legacy.Param, flattened.ToArray());
        Assert.Equal(legacy.Scores, Array<double>(ctx, m, "Scores"));
        Assert.Equal(legacy.Points, ctx.GetVariable(m, "MeasurePoints").Count);
        HOperatorSet.GetContourXld(((HalconXld)ctx.GetVariable(m, "ResultContour").Value).Object.SelectObj(1), out HTuple rows, out _);
        Assert.Equal(legacy.ContourRows, rows.DArr);
        Assert.Equal(legacy.Scores.Length > 0 ? legacy.Scores[0] : double.NaN, Single(ctx, m, "Score"));
    }

    [Fact]
    public void 回归门禁_metrology拆分后四个工具全部输出与拆分前逐项一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        // 两条平行亮带 + 2 个实例的直线测量（覆盖多实例展开）
        using HObject bands = ShapeImage(() =>
        {
            HOperatorSet.GenRectangle1(out HObject a, 100, 100, 140, 500);
            HOperatorSet.GenRectangle1(out HObject b, 180, 100, 220, 500);
            HOperatorSet.Union2(a, b, out HObject u);
            a.Dispose();
            b.Dispose();
            return u;
        });
        var line = new LineFollowMeasureTool("直线1") { BaseRow1 = 160, BaseColumn1 = 150, BaseRow2 = 160, BaseColumn2 = 450, MeasureLength1 = 70, MeasureTransition = "positive", NumInstances = 2 };
        AssertSameAsLegacy(line, bands, new[] { "InstanceRows1", "InstanceColumns1", "InstanceRows2", "InstanceColumns2" }, (m, n, v) =>
        {
            HOperatorSet.AddMetrologyObjectLineMeasure(m, 160.0, 150.0, 160.0, 450.0, 70.0, 3.0, 1.0, 20.0, n, v, out HTuple i);
            return i.I;
        });

        using HObject rectImage = ShapeImage(() => { HOperatorSet.GenRectangle2(out HObject r, 200, 300, 0.2, 120, 60); return r; });
        var rect = new RectangleFollowMeasureTool("矩形1") { BaseRow = 200, BaseColumn = 300, BasePhi = 0.2, BaseLength1 = 120, BaseLength2 = 60 };
        AssertSameAsLegacy(rect, rectImage, new[] { "InstanceRows", "InstanceColumns", "InstancePhis", "InstanceLengths1", "InstanceLengths2" }, (m, n, v) =>
        {
            HOperatorSet.AddMetrologyObjectRectangle2Measure(m, 200.0, 300.0, 0.2, 120.0, 60.0, 10.0, 3.0, 1.0, 20.0, n, v, out HTuple i);
            return i.I;
        });

        using HObject circleImage = ShapeImage(() => { HOperatorSet.GenCircle(out HObject c, 200, 300, 80); return c; });
        var circle = new CircleFollowMeasureTool("圆1") { BaseRow = 200, BaseColumn = 300, BaseRadius = 80 };
        AssertSameAsLegacy(circle, circleImage, new[] { "InstanceRows", "InstanceColumns", "InstanceRadii" }, (m, n, v) =>
        {
            HOperatorSet.AddMetrologyObjectCircleMeasure(m, 200.0, 300.0, 80.0, 10.0, 3.0, 1.0, 20.0,
                n.TupleConcat("start_phi").TupleConcat("end_phi"), v.TupleConcat(0.0).TupleConcat(Math.PI * 2), out HTuple i);
            return i.I;
        });

        using HObject ellipseImage = ShapeImage(() => { HOperatorSet.GenEllipse(out HObject e, 200, 300, 0.3, 100, 50); return e; });
        var ellipse = new EllipseFollowMeasureTool("椭圆1") { EllipseRow = 200, EllipseColumn = 300, EllipseAngle = 0.3, EllipseLength1 = 100, EllipseLength2 = 50 };
        AssertSameAsLegacy(ellipse, ellipseImage, new[] { "InstanceRows", "InstanceColumns", "InstancePhis", "InstanceLengths1", "InstanceLengths2" }, (m, n, v) =>
        {
            HOperatorSet.AddMetrologyObjectEllipseMeasure(m, 200.0, 300.0, 0.3, 100.0, 50.0, 7.0, 2.0, 1.0, 1.0, n, v, out HTuple i);
            return i.I;
        });
    }

    // ======================= MS-05 灰度投影 =======================

    /// <summary>灰度 = 列号的渐变图（300×200）。</summary>
    private static HObject RampImage()
    {
        HOperatorSet.GenImageSurfaceFirstOrder(out HObject ramp, "byte", 0, 1, 0, 0, 0, 300, 200);
        return ramp;
    }

    private static GrayProjectionFollowTool Projection(double smooth = 0) => new("投影1")
    {
        BaseRow = 100, BaseColumn = 150, BasePhi = 0, BaseLength1 = 50, BaseLength2 = 5, Smooth = smooth
    };

    [Fact]
    public void 灰度投影_Profile与真值一致_导数_最值位置为长轴采样序号()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = RampImage();
        using FlowContext ctx = ImageContext(image);
        Assert.True(Projection().Run(ctx).IsSuccess);
        double[] profile = Array<double>(ctx, "投影1", "Profile");
        // 沿长轴从 中心−Length1 到 中心+Length1：列 100 ~ 200，共 2·50+1 点
        Assert.Equal(Enumerable.Range(100, 101).Select(v => (double)v), profile);
        Assert.All(Array<double>(ctx, "投影1", "Derivative"), d => Assert.Equal(1.0, d, 6));
        Assert.Equal(100, Single(ctx, "投影1", "MinGray"));
        Assert.Equal(200, Single(ctx, "投影1", "MaxGray"));
        Assert.Equal(150, Single(ctx, "投影1", "MeanGray"), 9);
        Assert.Equal(0, (int)ctx.GetVariable("投影1", "MinPosition").Value);
        Assert.Equal(100, (int)ctx.GetVariable("投影1", "MaxPosition").Value);
        var results = (List<OneDProjectionResult>)ctx.GetVariable("投影1", "Results").Value;
        OneDProjectionResult only = Assert.Single(results);
        Assert.Equal(profile, only.Profile);
        Assert.False(only.Followed);

        // 方向反过来（phi = π）：曲线从列 200 走到列 100
        var reversed = Projection();
        reversed.BasePhi = Math.PI;
        using FlowContext back = ImageContext(image);
        Assert.True(reversed.Run(back).IsSuccess);
        Assert.Equal(Enumerable.Range(100, 101).Select(v => (double)(300 - v)), Array<double>(back, "投影1", "Profile"));
        Assert.Equal(100, (int)back.GetVariable("投影1", "MinPosition").Value);
    }

    [Fact]
    public void 灰度投影_平滑_平坦曲线不产生NaN_平滑过大明确报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject ramp = RampImage();
        using (FlowContext ctx = ImageContext(ramp))
        {
            Assert.True(Projection(2).Run(ctx).IsSuccess);
            double[] profile = Array<double>(ctx, "投影1", "Profile");
            Assert.Equal(101, profile.Length);
            Assert.Equal(150, profile[50], 6);
            Assert.Equal(1.0, Array<double>(ctx, "投影1", "Derivative")[50], 6);
        }
        using HObject flat = Blank(300, 200, 0);
        using (FlowContext ctx = ImageContext(flat))
        {
            Assert.True(Projection(2).Run(ctx).IsSuccess);
            Assert.All(Array<double>(ctx, "投影1", "Profile"), v => Assert.Equal(0, v));
            Assert.All(Array<double>(ctx, "投影1", "Derivative"), v => Assert.Equal(0, v));
        }
        using (FlowContext ctx = ImageContext(ramp))
        {
            NodeResult result = Projection(20).Run(ctx);
            Assert.False(result.IsSuccess);
            Assert.Contains("平滑系数 20 相对曲线长度过大（101 个采样点", result.Message);
        }
    }

    [Fact]
    public void 灰度投影_多定位矩阵每个一条曲线_出界的矩阵按该项未找到_全部出界按策略()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = RampImage();
        HOperatorSet.HomMat2dIdentity(out HTuple id);
        HOperatorSet.HomMat2dTranslate(id, 0, 120, out HTuple outside);
        HOperatorSet.HomMat2dTranslate(id, 20, -30, out HTuple inside);
        var tool = Projection();
        tool.MatrixPath = "定位.HomMats";
        using (FlowContext ctx = ImageContext(image))
        {
            ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object, new[] { HomMat2D.Identity, new HomMat2D(outside), new HomMat2D(inside) }));
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(1, (int)ctx.GetVariable("投影1", "FailedCount").Value);
            var results = (List<OneDProjectionResult>)ctx.GetVariable("投影1", "Results").Value;
            // Index 沿用测量基类约定：未配置结果序号时为成功测量的序号（0、1…）
            Assert.Equal(new[] { 0, 1 }, results.Select(r => r.Index));
            Assert.Equal(new[] { 100.0, 70.0 }, results.Select(r => r.MinGray));
            // 单值与曲线取最后一次成功的测量（第三个矩阵：列 70 ~ 170）
            Assert.Equal(70, Single(ctx, "投影1", "MinGray"));
            Assert.Equal(results[1].Profile, Array<double>(ctx, "投影1", "Profile"));
            Assert.Contains(ctx.Log, l => l.Contains("测量矩形超出图像"));
        }
        foreach (bool fail in new[] { true, false })
        {
            tool.FailWhenNotFound = fail;
            using FlowContext ctx = ImageContext(image);
            ctx.SetVariable(Variable.Array("定位", "HomMats", VariableType.Object, new[] { new HomMat2D(outside) }));
            NodeResult result = tool.Run(ctx);
            Assert.Equal(!fail, result.IsSuccess);
            if (fail)
            {
                Assert.Contains("测量矩形超出图像", result.Message);
            }
            Assert.False((bool)ctx.GetVariable("投影1", "Found").Value);
            Assert.Empty(Array<double>(ctx, "投影1", "Profile"));
            Assert.True(double.IsNaN(Single(ctx, "投影1", "MeanGray")));
            Assert.Equal(-1, (int)ctx.GetVariable("投影1", "MinPosition").Value);
        }
    }

    // ======================= 元数据、保存加载、校验与登记 =======================

    [Fact]
    public void 新工具_保存加载_校验_显隐()
    {
        var corner = Corner("找角1");
        corner.TeachLine2Column2 = 155;
        var loadedCorner = (CornerFindTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(corner)))).Tool;
        Assert.Equal(155, loadedCorner.TeachLine2Column2);
        Assert.Equal(250, loadedCorner.TeachLine2Row1);

        var projection = Projection(1.5);
        projection.BasePhi = 0.3;
        var loadedProjection = (GrayProjectionFollowTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(projection)))).Tool;
        Assert.Equal(1.5, loadedProjection.Smooth);
        Assert.Equal(0.3, loadedProjection.BasePhi);

        Assert.Contains(ConfigIssues(new GrayProjectionFollowTool("投影1") { Smooth = -1 }), m => m.Contains("平滑系数不能小于 0"));
        Assert.Contains(ConfigIssues(new GrayProjectionFollowTool("投影1") { BaseLength1 = 0.5 }), m => m.Contains("测量矩形半长至少为 1"));
        Assert.Contains(ConfigIssues(new GrayProjectionFollowTool("投影1") { BaseLength2 = 0 }), m => m.Contains("测量矩形半宽必须大于 0"));
        Assert.Contains("平滑系数不能小于 0", new GrayProjectionFollowTool("投影1") { Smooth = -1 }.Run(new FlowContext()).Message);
        Assert.Contains(ConfigIssues(new CornerFindTool("找角1") { MinScore = 2 }), m => m.Contains("最低得分必须在 0 到 1 之间"));

        var visibility = new GrayProjectionFollowTool("投影1");
        foreach (string hidden in new[] { "MeasureLength1", "MeasureLength2", "MeasureSigma", "MeasureThreshold", "MeasureTransition", "MeasureSelect" })
        {
            Assert.False(visibility.IsParameterVisible(hidden), hidden);
        }
        Assert.True(visibility.IsParameterVisible("Smooth"));
        Assert.True(visibility.IsParameterVisible("BaseLength1"));
    }

    [Fact]
    public void 命名守卫_示教参数TeachLine与输出Line不冲突_输出声明完整()
    {
        foreach (ToolBase tool in new ToolBase[] { new CornerFindTool("找角1"), new GrayProjectionFollowTool("投影1") })
        {
            var properties = new HashSet<string>(tool.GetType().GetProperties().Select(p => p.Name));
            Assert.DoesNotContain(ToolMetadata.GetOutputs(tool), o => properties.Contains(o.Name));
        }
        IReadOnlyList<ToolOutputDef> corner = ToolMetadata.GetOutputs(typeof(CornerFindTool));
        foreach (string name in new[] { "CornerRow", "CornerColumn", "Angle", "AngleDeg", "Line1Row1", "Line2Column2", "Score1", "Score2", "Found", "Results", "Score", "Scores", "MeasurePoints" })
        {
            Assert.Contains(corner, o => o.Name == name);
        }
        Assert.Equal(typeof(List<CornerMeasureResult>), corner.Single(o => o.Name == "Results").ElementClrType);
        IReadOnlyList<ToolOutputDef> projection = ToolMetadata.GetOutputs(typeof(GrayProjectionFollowTool));
        Assert.Equal(typeof(List<OneDProjectionResult>), projection.Single(o => o.Name == "Results").ElementClrType);
        Assert.Contains(projection, o => o.Name == "MinPosition" && o.Type == VariableType.Int);
        Assert.Contains(projection, o => o.Name == "Profile" && o.Kind == VariableKind.Array);
    }

    [Fact]
    public void 新工具登记_固定ID_工具箱05几何测量()
    {
        ToolboxRegistry.RegisterDefaults();
        foreach ((string id, string display, Type type) in new[] { ("corner-find", "找角", typeof(CornerFindTool)), ("gray-projection", "灰度投影", typeof(GrayProjectionFollowTool)) })
        {
            ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == id);
            Assert.Equal("05 几何测量", item.Category);
            Assert.Equal(display, item.DisplayName);
            ToolNode node = Assert.IsType<ToolNode>(item.Factory());
            Assert.IsType(type, node.Tool);
            string json = FlowSerializer.SaveNode(node);
            Assert.Contains($"\"ToolId\": \"{id}\"", json);
            Assert.IsType(type, Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool);
        }
    }
}
