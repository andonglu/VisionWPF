using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// 复现 HALCON 示例 color_segmentation_pizza.hdev：RGB → CIELab，在 b 通道分割披萨与意式香肠。
/// 依赖 HALCON 安装附带的示例图像（版权原因不随仓库分发）；未安装示例图像时不执行。
/// </summary>
public class PizzaColorSegmentationExampleTests
{
    [Fact]
    public void 披萨示例_与HALCON示例逐像素一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string imageRoot = Environment.GetEnvironmentVariable("HALCONIMAGES") ?? string.Empty;
        if (!File.Exists(Path.Combine(imageRoot, "color", "pizza_01.png")))
        {
            return;
        }
        SequenceNode root = FlowSerializer.Load(File.ReadAllText(
            RepoPaths.Find(Path.Combine("examples", "pizza-salami.vflow.json"))));
        int[] expectedCounts = { 7, 5, 4 };
        for (int i = 0; i < expectedCounts.Length; i++)
        {
            HOperatorSet.ReadImage(out HObject image, $"color/pizza_{i + 1:00}");
            using var imageOwner = image;
            using HObject expected = ReferenceSalami(image);
            using var ctx = new FlowContext();
            ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));

            FlowRunResult run = new FlowEngine().Run(root, ctx);

            Assert.True(run.IsSuccess, run.Message);
            Assert.Equal(expectedCounts[i], (int)ctx.GetVariable("流程输出1", "SalamiCount").Value);
            var salami = (HalconRegion)ctx.GetVariable("香肠", "Region").Value;
            HOperatorSet.Union1(salami.Object, out HObject actualUnion);
            using var actualOwner = actualUnion;
            HOperatorSet.Union1(expected, out HObject expectedUnion);
            using var expectedOwner = expectedUnion;
            HOperatorSet.SymmDifference(actualUnion, expectedUnion, out HObject difference);
            using var differenceOwner = difference;
            HOperatorSet.AreaCenter(difference, out HTuple differenceArea, out _, out _);
            Assert.True(differenceArea.D == 0, $"pizza_{i + 1:00} 与原示例相差 {differenceArea.D} 像素");
        }
    }

    /// <summary>color_segmentation_pizza.hdev 主循环的逐算子移植。</summary>
    private static HObject ReferenceSalami(HObject image)
    {
        var temps = new List<HObject>();
        try
        {
            HOperatorSet.Decompose3(image, out HObject r, out HObject g, out HObject b);
            temps.AddRange(new[] { r, g, b });
            HOperatorSet.TransFromRgb(r, g, b, out HObject l, out HObject a, out HObject labB, "cielab");
            temps.AddRange(new[] { l, a, labB });
            HOperatorSet.Threshold(labB, out HObject raw, 148, 255);
            HOperatorSet.Connection(raw, out HObject connected);
            HOperatorSet.SelectShapeStd(connected, out HObject pizza, "max_area", 0);
            HOperatorSet.ShapeTrans(pizza, out HObject filled, "convex");
            HOperatorSet.ReduceDomain(labB, filled, out HObject reduced);
            temps.AddRange(new[] { raw, connected, pizza, filled, reduced });
            HOperatorSet.Threshold(reduced, out HObject region, 140, 146);
            HOperatorSet.Connection(region, out HObject parts);
            HOperatorSet.SelectShape(parts, out HObject selected, "area", "and", 30000, 1000000);
            HOperatorSet.ClosingCircle(selected, out HObject closed, 20.5);
            temps.AddRange(new[] { region, parts, selected, closed });
            HOperatorSet.OpeningCircle(closed, out HObject opened, 85.5);
            return opened;
        }
        finally
        {
            foreach (HObject temp in temps)
            {
                temp.Dispose();
            }
        }
    }
}
