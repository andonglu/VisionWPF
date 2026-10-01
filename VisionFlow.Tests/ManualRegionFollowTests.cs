using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>手动 Region 跟随变换矩阵（运行时）与 ROI 形状换算（编辑器示教）。</summary>
public class ManualRegionFollowTests
{
    private static string Grid()
    {
        var seed = new ManualRegionShape { Name = "格", Kind = ManualRegionShapeKind.Rectangle2, Row = 50, Column = 50, Length1 = 20, Length2 = 10 };
        return ManualRegionSerializer.Serialize(new ManualRegionDefinition { Items = ManualRegionArray.Generate(seed, 1, 3, 0, 60) });
    }

    [Fact]
    public void 跟随矩阵_每个检测格按位姿变换且保持顺序()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        // HALCON 按已知最大图像尺寸裁剪新建区域；实际流程中输入图像先于手动 Region 读入，测试里显式建立尺寸
        HOperatorSet.GenImageConst(out HObject sizeImage, "byte", 400, 400);
        using var sizeOwner = sizeImage;
        HomMat2D pose = HomMat2D.FromPoses(100, 100, 0, 150, 130, 0.5);
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("定位", "Matrix", pose, 1));
        var tool = new ManualRegionTool("格") { RoiJson = Grid(), OutputMode = ManualRegionOutputMode.PerShape, MatrixPath = "定位.Matrix" };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        var region = (HalconRegion)ctx.GetVariable("格", "Region").Value;
        HOperatorSet.AreaCenter(region.Object, out HTuple areas, out HTuple rows, out HTuple columns);
        Assert.Equal(3, areas.Length);
        foreach (int i in new[] { 0, 1, 2 })
        {
            pose.TransformPoint(50, 50 + i * 60, out double expectedRow, out double expectedColumn);
            Assert.InRange(rows[i].D, expectedRow - 1, expectedRow + 1);
            Assert.InRange(columns[i].D, expectedColumn - 1, expectedColumn + 1);
        }
        HOperatorSet.SelectObj(region.Object, out HObject first, 1);
        HOperatorSet.OrientationRegion(first, out HTuple phi);
        first.Dispose();
        double diff = Math.Abs((phi.D - 0.5) % Math.PI);
        Assert.True(Math.Min(diff, Math.PI - diff) < 0.05, $"phi={phi.D}");
    }

    [Fact]
    public void 跟随矩阵_集合或无法解析时明确失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Array("匹配", "HomMats", VariableType.Object, new[] { HomMat2D.Identity, HomMat2D.Identity }));
        var tool = new ManualRegionTool("格") { RoiJson = Grid(), MatrixPath = "匹配.HomMats" };

        NodeResult collection = tool.Run(ctx);
        Assert.False(collection.IsSuccess);
        Assert.Contains("只支持单个矩阵", collection.Message);

        tool.MatrixPath = "匹配.Missing";
        NodeResult missing = tool.Run(ctx);
        Assert.False(missing.IsSuccess);
        Assert.Contains("匹配.Missing", missing.Message);
    }

    [Fact]
    public void 形状换算_平移保持轴对齐矩形_旋转转为旋转矩形_逆变换可还原()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var rect = new ManualRegionShape { Kind = ManualRegionShapeKind.Rectangle1, Row1 = 10, Column1 = 20, Row2 = 30, Column2 = 60 };

        ManualRegionShape moved = rect.Transform(HomMat2D.FromPoses(0, 0, 0, 5, 7, 0));
        Assert.Equal(ManualRegionShapeKind.Rectangle1, moved.Kind);
        Assert.Equal(new[] { 15.0, 27.0, 35.0, 67.0 }, new[] { moved.Row1, moved.Column1, moved.Row2, moved.Column2 });

        HomMat2D pose = HomMat2D.FromPoses(20, 40, 0, 120, 90, 0.4);
        ManualRegionShape rotated = rect.Transform(pose);
        Assert.Equal(ManualRegionShapeKind.Rectangle2, rotated.Kind);
        Assert.InRange(rotated.Row, 119.99, 120.01);
        Assert.InRange(rotated.Column, 89.99, 90.01);
        Assert.InRange(rotated.Phi, 0.399, 0.401);
        Assert.Equal(20, rotated.Length1, 6);
        Assert.Equal(10, rotated.Length2, 6);

        ManualRegionShape back = rotated.Transform(pose.Inverted());
        Assert.InRange(back.Row, 19.99, 20.01);
        Assert.InRange(back.Column, 39.99, 40.01);
        Assert.InRange(back.Phi, -0.001, 0.001);

        var circle = new ManualRegionShape { Kind = ManualRegionShapeKind.Circle, Row = 10, Column = 10, Radius = 5 };
        ManualRegionShape scaled = circle.Transform(HomMat2D.FromPosesScaled(10, 10, 0, 50, 60, 0, 2));
        Assert.Equal(50, scaled.Row, 6);
        Assert.Equal(60, scaled.Column, 6);
        Assert.Equal(10, scaled.Radius, 6);
    }
}
