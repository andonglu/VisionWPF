using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// TOOLS-REVIEW 回归：角度约定（TR-01/02）、线线交点与拟合直线（TR-03/04）。
/// 期望值以 HALCON 算子自身的结果为准，而不是工具内部公式。
/// </summary>
public class ToolGeometryTests
{
    private static double AngleModPi(double angle)
    {
        double value = angle % Math.PI;
        if (value < 0) value += Math.PI;
        return value;
    }

    private static void AssertSameOrientation(double expected, double actual, double tolerance = 0.02)
    {
        double diff = Math.Abs(AngleModPi(expected) - AngleModPi(actual));
        diff = Math.Min(diff, Math.PI - diff);
        Assert.True(diff < tolerance, $"方向不一致：期望 {expected:F4}，实际 {actual:F4}（模 π）");
    }

    /// <summary>
    /// 先建 300×300 图像再生成区域：HALCON 按已知最大图像尺寸裁剪新区域，
    /// 若进程中尚未出现过更大的图像，区域会被裁到默认尺寸内。
    /// </summary>
    private static HObject BinaryImage(Func<HObject> createRegion)
    {
        HOperatorSet.GenImageConst(out HObject sizeImage, "byte", 300, 300);
        using var sizeOwner = sizeImage;
        HObject region = createRegion();
        using var regionOwner = region;
        HOperatorSet.RegionToBin(region, out HObject image, 255, 0, 300, 300);
        return image;
    }

    private static HObject Disc(double row, double column, double radius)
    {
        return BinaryImage(() =>
        {
            HOperatorSet.GenCircle(out HObject disc, row, column, radius);
            return disc;
        });
    }

    [Fact]
    public void TransformPose_旋转方向与HALCON区域变换一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HomMat2D matrix = HomMat2D.FromPoses(100, 100, 0, 120, 80, 0.5);
        matrix.TransformPose(100, 100, 0.3, out double row, out double column, out double phi);

        HOperatorSet.GenRectangle2(out HObject rect, 100, 100, 0.3, 40, 10);
        using var rectOwner = rect;
        HOperatorSet.AffineTransRegion(rect, out HObject moved, matrix.Data, "nearest_neighbor");
        using var movedOwner = moved;
        HOperatorSet.AreaCenter(moved, out _, out HTuple expectedRow, out HTuple expectedColumn);
        HOperatorSet.OrientationRegion(moved, out HTuple expectedPhi);

        Assert.InRange(row, expectedRow.D - 0.5, expectedRow.D + 0.5);
        Assert.InRange(column, expectedColumn.D - 0.5, expectedColumn.D + 0.5);
        AssertSameOrientation(expectedPhi.D, phi);
        Assert.InRange(phi, 0.79, 0.81);
        Assert.InRange(matrix.RotationAngle, 0.49, 0.51);
    }

    [Fact]
    public void 矩形测量_旋转定位后跟随到目标矩形()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = BinaryImage(() =>
        {
            HOperatorSet.GenRectangle2(out HObject target, 160, 140, 0.4, 60, 30);
            return target;
        });
        using var imageOwner = image;
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        ctx.SetVariable(Variable.Object("定位", "HomMat", HomMat2D.FromPoses(150, 150, 0, 160, 140, 0.4), 1));
        var tool = new RectangleFollowMeasureTool("矩形测量1")
        {
            ImagePath = "Input.Image",
            MatrixPath = "定位.HomMat",
            BaseRow = 150,
            BaseColumn = 150,
            BasePhi = 0,
            BaseLength1 = 60,
            BaseLength2 = 30,
            MeasureLength1 = 8,
            MeasureLength2 = 3
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.InRange((double)ctx.GetVariable("矩形测量1", "Row").Value, 159, 161);
        Assert.InRange((double)ctx.GetVariable("矩形测量1", "Column").Value, 139, 141);
        AssertSameOrientation(0.4, (double)ctx.GetVariable("矩形测量1", "Phi").Value);
    }

    [Fact]
    public void 圆弧卡尺_整圆每个卡尺沿半径方向只找到一个边缘()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Disc(150, 150, 50);
        using var imageOwner = image;
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        var tool = new ArcCaliperFollowMeasureTool("圆弧卡尺1")
        {
            ImagePath = "Input.Image",
            BaseRow = 150,
            BaseColumn = 150,
            BaseRadius = 50,
            StartPhi = 0,
            EndPhi = Math.PI * 2,
            CaliperCount = 8,
            MeasureLength1 = 15,
            MeasureLength2 = 3
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        var rows = (double[])ctx.GetVariable("圆弧卡尺1", "Rows").Value;
        var columns = (double[])ctx.GetVariable("圆弧卡尺1", "Columns").Value;
        Assert.Equal(8, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            double distance = Math.Sqrt(Math.Pow(rows[i] - 150, 2) + Math.Pow(columns[i] - 150, 2));
            Assert.InRange(distance, 48, 52);
            double expectedPhi = ArcCaliperFollowMeasureTool.CaliperPhi(0, Math.PI * 2, 8, i);
            HomMat2D.PointOnCircle(150, 150, 50, expectedPhi, out double expectedRow, out double expectedColumn);
            Assert.InRange(rows[i], expectedRow - 1, expectedRow + 1);
            Assert.InRange(columns[i], expectedColumn - 1, expectedColumn + 1);
        }
    }

    [Fact]
    public void 圆弧卡尺_旋转跟随整圆不塌缩()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = Disc(160, 140, 50);
        using var imageOwner = image;
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        ctx.SetVariable(Variable.Object("定位", "HomMat", HomMat2D.FromPoses(150, 150, 0, 160, 140, 0.7), 1));
        var tool = new ArcCaliperFollowMeasureTool("圆弧卡尺1")
        {
            ImagePath = "Input.Image",
            MatrixPath = "定位.HomMat",
            BaseRow = 150,
            BaseColumn = 150,
            BaseRadius = 50,
            StartPhi = 0,
            EndPhi = Math.PI * 2,
            CaliperCount = 8,
            MeasureLength1 = 15,
            MeasureLength2 = 3
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        var rows = (double[])ctx.GetVariable("圆弧卡尺1", "Rows").Value;
        var columns = (double[])ctx.GetVariable("圆弧卡尺1", "Columns").Value;
        Assert.Equal(8, rows.Length);
        var angles = new HashSet<int>();
        for (int i = 0; i < rows.Length; i++)
        {
            double distance = Math.Sqrt(Math.Pow(rows[i] - 160, 2) + Math.Pow(columns[i] - 140, 2));
            Assert.InRange(distance, 48, 52);
            angles.Add((int)Math.Round(HomMat2D.DirectionToPhi(rows[i] - 160, columns[i] - 140) * 10));
        }
        Assert.Equal(8, angles.Count);
    }

    [Fact]
    public void 缩放形状匹配_变换矩阵把示教位姿映射到匹配位姿()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        static HObject LShape()
        {
            HOperatorSet.GenRectangle1(out HObject bar, 110, 120, 180, 140);
            HOperatorSet.GenRectangle1(out HObject foot, 160, 120, 180, 195);
            HOperatorSet.Union2(bar, foot, out HObject shape);
            bar.Dispose();
            foot.Dispose();
            return shape;
        }

        HObject teachImage = BinaryImage(LShape);
        using var teachOwner = teachImage;
        HOperatorSet.GenRectangle1(out HObject roi, 95, 105, 195, 210);
        using var roiOwner = roi;
        HOperatorSet.ReduceDomain(teachImage, roi, out HObject template);
        using var templateOwner = template;
        HOperatorSet.CreateScaledShapeModel(template, "auto", -0.5, 1.0, "auto", 0.8, 1.2, "auto", "auto",
            "use_polarity", "auto", "auto", out HTuple modelId);
        byte[] modelData;
        double baseRow, baseColumn;
        try
        {
            modelData = ShapeModelSerialization.Serialize(modelId);
            HOperatorSet.FindScaledShapeModel(teachImage, modelId, -0.5, 1.0, 0.8, 1.2, 0.8, 1, 0.5,
                "least_squares", 0, 0.9, out HTuple r0, out HTuple c0, out _, out _, out _);
            baseRow = r0.D;
            baseColumn = c0.D;
        }
        finally
        {
            HOperatorSet.ClearShapeModel(modelId);
        }

        HomMat2D truth = HomMat2D.FromPosesScaled(baseRow, baseColumn, 0, 160, 140, 0.3, 1.1);
        HObject targetImage = BinaryImage(() =>
        {
            HObject shape = LShape();
            HOperatorSet.AffineTransRegion(shape, out HObject moved, truth.Data, "nearest_neighbor");
            shape.Dispose();
            return moved;
        });
        using var targetOwner = targetImage;
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(targetImage), 1));
        var tool = new HalconScaledShapeMatchTool("缩放匹配1")
        {
            ImagePath = "Input.Image",
            ShapeModelData = modelData,
            FindStartAngle = -0.5,
            FindExtentAngle = 1.0,
            ScaleMin = 0.8,
            ScaleMax = 1.2,
            MinScore = 0.7,
            NumMatches = 1,
            BaseRow = baseRow,
            BaseColumn = baseColumn,
            BaseAngle = 0
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        var matrix = (HomMat2D)ctx.GetVariable("缩放匹配1", "BestHomMat").Value;
        Assert.InRange(matrix.ScaleFactor, 1.08, 1.12);
        // 示教图像中的 L 形拐角应跟随到目标图像中的对应位置
        truth.TransformPoint(180, 195, out double expectedRow, out double expectedColumn);
        matrix.TransformPoint(180, 195, out double row, out double column);
        Assert.InRange(row, expectedRow - 1.5, expectedRow + 1.5);
        Assert.InRange(column, expectedColumn - 1.5, expectedColumn + 1.5);
    }

    private static FlowContext LineContext(params (string Name, double[] Rows, double[] Columns)[] lines)
    {
        var ctx = new FlowContext();
        foreach (var line in lines)
        {
            HOperatorSet.GenContourPolygonXld(out HObject xld, new HTuple(line.Rows), new HTuple(line.Columns));
            ctx.SetVariable(Variable.Object("线", line.Name, HalconXld.Owned(xld), 1));
        }
        return ctx;
    }

    [Theory]
    [InlineData(new[] { 0.0, 100.0 }, new[] { 100.0, 0.0 })]
    [InlineData(new[] { 100.0, 0.0 }, new[] { 0.0, 100.0 })]
    [InlineData(new[] { 0.0, 25.0, 50.0, 75.0, 100.0 }, new[] { 100.0, 75.0, 50.0, 25.0, 0.0 })]
    public void 线线交点_反斜线与多点轮廓均得到正确交点(double[] rows, double[] columns)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = LineContext(("A", rows, columns), ("B", new[] { 0.0, 100.0 }, new[] { 20.0, 20.0 }));
        var tool = new IntersectionLinesTool("交点1") { Line1Path = "线.A", Line2Path = "线.B" };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.InRange((double)ctx.GetVariable("交点1", "Row").Value, 79.9, 80.1);
        Assert.InRange((double)ctx.GetVariable("交点1", "Column").Value, 19.9, 20.1);
    }

    [Fact]
    public void 线线交点_平行线明确失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = LineContext(("A", new[] { 0.0, 100.0 }, new[] { 20.0, 20.0 }),
            ("B", new[] { 0.0, 100.0 }, new[] { 50.0, 50.0 }));
        var tool = new IntersectionLinesTool("交点1") { Line1Path = "线.A", Line2Path = "线.B" };

        NodeResult result = tool.Run(ctx);

        Assert.False(result.IsSuccess);
        Assert.Contains("平行", result.Message);
    }

    [Fact]
    public void 拟合直线_多条轮廓输出多条独立直线()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenContourPolygonXld(out HObject first, new HTuple(0.0, 100.0), new HTuple(0.0, 100.0));
        HOperatorSet.GenContourPolygonXld(out HObject second, new HTuple(0.0, 100.0), new HTuple(20.0, 20.0));
        HOperatorSet.ConcatObj(first, second, out HObject both);
        first.Dispose();
        second.Dispose();
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("线", "Two", HalconXld.Owned(both), 2));
        var tool = new FitLineTool("拟合直线1") { XldPath = "线.Two" };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        var xld = (HalconXld)ctx.GetVariable("拟合直线1", "Xld").Value;
        HOperatorSet.CountObj(xld.Object, out HTuple count);
        Assert.Equal(2, count.I);
        for (int i = 1; i <= 2; i++)
        {
            HOperatorSet.SelectObj(xld.Object, out HObject single, i);
            HOperatorSet.GetContourXld(single, out HTuple lineRows, out _);
            single.Dispose();
            Assert.Equal(2, lineRows.Length);
        }
    }
}
