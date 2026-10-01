using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>分区检测（ZoneInspectTool）、手动 Region 阵列与 PerShape 输出、泡罩检测示例。</summary>
public class ZoneInspectTests
{
    private static HalconRegion Rectangles(params (double R1, double C1, double R2, double C2)[] rects)
    {
        HOperatorSet.GenEmptyObj(out HObject all);
        foreach (var r in rects)
        {
            HOperatorSet.GenRectangle1(out HObject rect, r.R1, r.C1, r.R2, r.C2);
            HOperatorSet.ConcatObj(all, rect, out HObject combined);
            all.Dispose();
            rect.Dispose();
            all = combined;
        }
        return HalconRegion.Owned(all);
    }

    [Fact]
    public void 分区检测_逐格判定缺失面积错误与灰度错误()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject blank, "byte", 300, 300);
        HOperatorSet.GenImageProto(blank, out HObject image, 200);
        blank.Dispose();
        // 第 3 格目标偏暗
        HOperatorSet.GenRectangle1(out HObject darkSpot, 20, 220, 30, 230);
        HOperatorSet.PaintRegion(darkSpot, image, out HObject painted, 30, "fill");
        darkSpot.Dispose();
        image.Dispose();
        using var imageOwner = painted;
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(painted), 1));
        ctx.SetVariable(Variable.Object("格", "Region",
            Rectangles((0, 0, 59, 59), (0, 80, 59, 139), (0, 160, 59, 219 + 20), (0, 260, 59, 299)), 4));
        ctx.SetVariable(Variable.Object("目标", "Region",
            Rectangles((10, 10, 49, 49), (25, 100, 34, 109), (10, 180, 49, 235)), 3));
        var tool = new ZoneInspectTool("分区1")
        {
            ZonePath = "格.Region",
            TargetPath = "目标.Region",
            ImagePath = "Input.Image",
            MinArea = 500,
            MinGrayLimit = 60
        };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        var zones = (ZoneResult[])ctx.GetVariable("分区1", "Zones").Value;
        Assert.Equal(new[] { ZoneState.OK, ZoneState.Wrong, ZoneState.Wrong, ZoneState.Missing }, zones.Select(z => z.State));
        Assert.Contains("面积", zones[1].Reason);
        Assert.Contains("最小灰度", zones[2].Reason);
        Assert.Equal(1600, zones[0].Area);
        Assert.Equal(1, (int)ctx.GetVariable("分区1", "OkCount").Value);
        Assert.Equal(2, (int)ctx.GetVariable("分区1", "WrongCount").Value);
        Assert.Equal(1, (int)ctx.GetVariable("分区1", "MissingCount").Value);
        Assert.False((bool)ctx.GetVariable("分区1", "AllOk").Value);
        var missing = (HalconRegion)ctx.GetVariable("分区1", "MissingRegion").Value;
        HOperatorSet.CountObj(missing.Object, out HTuple missingCount);
        Assert.Equal(1, missingCount.I);
    }

    [Fact]
    public void 分区检测_配置灰度判定但未指定图像时明确失败()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("格", "Region", Rectangles((0, 0, 9, 9)), 1));
        ctx.SetVariable(Variable.Object("目标", "Region", Rectangles((0, 0, 9, 9)), 1));
        var tool = new ZoneInspectTool("分区1") { ZonePath = "格.Region", TargetPath = "目标.Region", MinGrayLimit = 60 };

        NodeResult result = tool.Run(ctx);

        Assert.False(result.IsSuccess);
        Assert.Contains("灰度图像", result.Message);
    }

    [Fact]
    public void 手动Region阵列_先行后列生成并按序命名()
    {
        var seed = new ManualRegionShape { Name = "格", Kind = ManualRegionShapeKind.Rectangle2, Row = 88, Column = 163, Length1 = 64, Length2 = 30 };

        List<ManualRegionShape> shapes = ManualRegionArray.Generate(seed, 2, 3, 70, 150);

        Assert.Equal(6, shapes.Count);
        Assert.Equal(new[] { 163.0, 313.0, 463.0, 163.0, 313.0, 463.0 }, shapes.Select(s => s.Column));
        Assert.Equal(new[] { 88.0, 88.0, 88.0, 158.0, 158.0, 158.0 }, shapes.Select(s => s.Row));
        Assert.Equal("格_2-3", shapes[5].Name);
        Assert.All(shapes, s => Assert.Equal(64, s.Length1));
        Assert.Equal(88, seed.Row);
    }

    [Fact]
    public void 手动Region_PerShape按绘制顺序逐个输出且各自扣除排除区域()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        var definition = new ManualRegionDefinition
        {
            Items =
            {
                new ManualRegionShape { Kind = ManualRegionShapeKind.Rectangle1, Row1 = 0, Column1 = 100, Row2 = 9, Column2 = 109 },
                new ManualRegionShape { Kind = ManualRegionShapeKind.Rectangle1, Row1 = 0, Column1 = 0, Row2 = 9, Column2 = 9 },
                new ManualRegionShape { Kind = ManualRegionShapeKind.Rectangle1, Row1 = 0, Column1 = 5, Row2 = 9, Column2 = 14 },
                new ManualRegionShape { Kind = ManualRegionShapeKind.Rectangle1, Polarity = ManualRegionPolarity.Exclude, Row1 = 0, Column1 = 0, Row2 = 9, Column2 = 1 }
            }
        };
        using var ctx = new FlowContext();
        var tool = new ManualRegionTool("格") { RoiJson = ManualRegionSerializer.Serialize(definition), OutputMode = ManualRegionOutputMode.PerShape };

        Assert.True(tool.Run(ctx).IsSuccess);

        var region = (HalconRegion)ctx.GetVariable("格", "Region").Value;
        HOperatorSet.AreaCenter(region.Object, out HTuple areas, out _, out HTuple columns);
        Assert.Equal(3, areas.Length);
        Assert.Equal(new[] { 100.0, 80.0, 100.0 }, Enumerable.Range(0, areas.Length).Select(i => areas[i].D));
        Assert.True(columns[0].D > columns[1].D, "第 1 个输出应是第 1 个绘制的 ROI（右侧）");
    }

    [Fact]
    public void 泡罩检测示例_与HALCON示例check_blister逐图结果一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string imageRoot = Environment.GetEnvironmentVariable("HALCONIMAGES") ?? string.Empty;
        if (!File.Exists(Path.Combine(imageRoot, "blister", "blister_01.png")))
        {
            // 依赖 HALCON 安装附带的示例图像（版权原因不随仓库分发）；未安装示例图像时不执行
            return;
        }
        string flowPath = RepoPaths.Find(Path.Combine("examples", "blister-check.vflow.json"));
        SequenceNode root = FlowSerializer.Load(File.ReadAllText(flowPath));
        // 期望值来自对 check_blister.hdev 的逐算子移植：(OK, 错误, 缺失)
        var expected = new[] { (15, 0, 0), (13, 2, 0), (13, 2, 0), (13, 1, 1), (14, 0, 1), (14, 1, 0) };
        for (int i = 0; i < expected.Length; i++)
        {
            HOperatorSet.ReadImage(out HObject image, $"blister/blister_{i + 1:00}");
            using var imageOwner = image;
            using var ctx = new FlowContext();
            ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));

            FlowRunResult run = new FlowEngine().Run(root, ctx);

            Assert.True(run.IsSuccess, run.Message);
            var actual = ((int)ctx.GetVariable("流程输出1", "OkCount").Value,
                (int)ctx.GetVariable("流程输出1", "WrongCount").Value,
                (int)ctx.GetVariable("流程输出1", "MissingCount").Value);
            Assert.True(expected[i] == actual, $"blister_{i + 1:00}：期望 {expected[i]}，实际 {actual}");
            Assert.Equal(i == 0, (bool)ctx.GetVariable("流程输出1", "Ok").Value);
        }
    }
}
