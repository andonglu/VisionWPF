using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>区域初始化测量：粗区域 → smallest_rectangle2 / smallest_circle → metrology 亚像素测量。</summary>
public class RegionSeededMeasureTests
{
    /// <summary>生成二值图像并把连通区域写入 分割.Region。</summary>
    private static FlowContext SegmentedContext(Func<HObject> createShapes, out HObject image)
    {
        HOperatorSet.GenImageConst(out HObject sizeImage, "byte", 400, 400);
        sizeImage.Dispose();
        HObject shapes = createShapes();
        HOperatorSet.RegionToBin(shapes, out image, 255, 0, 400, 400);
        shapes.Dispose();
        HOperatorSet.Threshold(image, out HObject region, 128, 255);
        HOperatorSet.Connection(region, out HObject connected);
        region.Dispose();
        HOperatorSet.SortRegion(connected, out HObject sorted, "first_point", "true", "column");
        connected.Dispose();
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        ctx.SetVariable(Variable.Object("分割", "Region", HalconRegion.Owned(sorted), 2));
        return ctx;
    }

    private static double AngleDiffModPi(double a, double b)
    {
        double diff = Math.Abs((a - b) % Math.PI);
        return Math.Min(diff, Math.PI - diff);
    }

    [Fact]
    public void 矩形测量_按区域形状逐个测出亚像素中心角度与尺寸()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = SegmentedContext(() =>
        {
            HOperatorSet.GenRectangle2(out HObject rotated, 120, 100, 0.35, 50, 25);
            HOperatorSet.GenRectangle2(out HObject straight, 250, 280, 0, 40, 30);
            HOperatorSet.Union2(rotated, straight, out HObject both);
            rotated.Dispose();
            straight.Dispose();
            return both;
        }, out HObject image);
        using var imageOwner = image;
        var tool = new RectangleFollowMeasureTool("矩形测量1")
        {
            ImagePath = "Input.Image",
            InitRegionPath = "分割.Region",
            MeasureLength1 = 8,
            MeasureLength2 = 3
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, (int)ctx.GetVariable("矩形测量1", "Count").Value);
        var results = ctx.GetVariable("矩形测量1", "Results").GetValue<List<RectangleMeasureResult>>();
        Assert.Equal(new[] { 0, 1 }, results.Select(r => r.Index));
        RectangleMeasureResult rotatedResult = results[0];
        Assert.InRange(rotatedResult.Row, 119, 121);
        Assert.InRange(rotatedResult.Column, 99, 101);
        Assert.True(AngleDiffModPi(rotatedResult.Phi, 0.35) < 0.02, $"Phi={rotatedResult.Phi}");
        double[] lengths = { rotatedResult.Length1, rotatedResult.Length2 };
        Assert.InRange(lengths.Max(), 49, 51.5);
        Assert.InRange(lengths.Min(), 24, 26.5);
        Assert.InRange(results[1].Row, 249, 251);
        Assert.InRange(results[1].Column, 279, 281);
        Assert.False(results[0].Followed);
    }

    [Fact]
    public void 圆形测量_按区域形状逐个测出亚像素圆心与半径()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = SegmentedContext(() =>
        {
            HOperatorSet.GenCircle(out HObject small, 100, 100, 30);
            HOperatorSet.GenCircle(out HObject large, 250, 260, 45);
            HOperatorSet.Union2(small, large, out HObject both);
            small.Dispose();
            large.Dispose();
            return both;
        }, out HObject image);
        using var imageOwner = image;
        var tool = new CircleFollowMeasureTool("圆形测量1")
        {
            ImagePath = "Input.Image",
            InitRegionPath = "分割.Region",
            MeasureLength1 = 8,
            MeasureLength2 = 3
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        var results = ctx.GetVariable("圆形测量1", "Results").GetValue<List<CircleMeasureResult>>();
        Assert.Equal(2, results.Count);
        Assert.InRange(results[0].Row, 99, 101);
        Assert.InRange(results[0].Column, 99, 101);
        Assert.InRange(results[0].Radius, 29, 31);
        Assert.InRange(results[1].Radius, 44, 46);
        var contour = (HalconXld)ctx.GetVariable("圆形测量1", "ResultContour").Value;
        HOperatorSet.CountObj(contour.Object, out HTuple contourCount);
        Assert.True(contourCount.I >= 2);
    }

    [Fact]
    public void 区域初始化与变换矩阵同时配置_明确失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = SegmentedContext(() =>
        {
            HOperatorSet.GenCircle(out HObject disc, 100, 100, 30);
            return disc;
        }, out HObject image);
        using var imageOwner = image;
        ctx.SetVariable(Variable.Object("定位", "HomMat", HomMat2D.Identity, 1));
        var tool = new CircleFollowMeasureTool("圆形测量1")
        {
            ImagePath = "Input.Image",
            InitRegionPath = "分割.Region",
            MatrixPath = "定位.HomMat"
        };

        NodeResult result = tool.Run(ctx);

        Assert.False(result.IsSuccess);
        Assert.Contains("不能同时配置变换矩阵", result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 初始区域为空_按未找到策略处理(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = SegmentedContext(() =>
        {
            HOperatorSet.GenCircle(out HObject disc, 100, 100, 30);
            return disc;
        }, out HObject image);
        using var imageOwner = image;
        HOperatorSet.GenEmptyObj(out HObject empty);
        ctx.SetVariable(Variable.Object("筛选", "Region", HalconRegion.Owned(empty), 0));
        var tool = new RectangleFollowMeasureTool("矩形测量1")
        {
            ImagePath = "Input.Image",
            InitRegionPath = "筛选.Region",
            FailWhenNotFound = failWhenNotFound
        };

        NodeResult result = tool.Run(ctx);

        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.False((bool)ctx.GetVariable("矩形测量1", "Found").Value);
        Assert.Equal(0, (int)ctx.GetVariable("矩形测量1", "Count").Value);
        Assert.True(double.IsNaN((double)ctx.GetVariable("矩形测量1", "Row").Value));
        if (failWhenNotFound)
        {
            Assert.Contains("没有区域对象", result.Message);
        }
    }
}
