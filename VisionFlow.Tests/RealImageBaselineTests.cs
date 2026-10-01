using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// VF-11 真实图像效果基线：固定图像（src/Image/razors1.png）+ 固定参数（examples 示例流程）
/// + HALCON 20.11 下取得的参考结果和样例回归容差，不代表业务精度已标定。
/// 环境缺失时明确失败，调用方输入与运行输出均在异常路径释放。
/// </summary>
public class RealImageBaselineTests
{
    [Fact]
    public void 区域处理示例_结果符合基线()
    {
        using var baseline = new ExampleImageBaseline(ExampleImageBaseline.RegionExample);
        using var ctx = new FlowContext();
        baseline.AssertResult(baseline.Run(ctx), ctx);
    }

    [Fact]
    public void XLD直线示例_结果符合基线()
    {
        using var baseline = new ExampleImageBaseline(ExampleImageBaseline.LineExample);
        using var ctx = new FlowContext();
        baseline.AssertResult(baseline.Run(ctx), ctx);
    }

    [Fact]
    public void 区域测量示例_结果符合基线()
    {
        using var baseline = new ExampleImageBaseline(ExampleImageBaseline.RegionMeasureExample);
        using var ctx = new FlowContext();
        baseline.AssertResult(baseline.Run(ctx), ctx);
    }

    [Fact]
    public void 区域处理示例_输入图像在上下文释放后仍有效()
    {
        using var baseline = new ExampleImageBaseline(ExampleImageBaseline.RegionExample);
        using var ctx = new FlowContext();
        baseline.AssertResult(baseline.Run(ctx), ctx);

        var inputImage = (HalconImage)ctx.GetVariable("Input", "Image").Value;
        HObject raw = inputImage.Object;
        ctx.Dispose();

        // 调用方注入的借用输入不被上下文释放，由调用方自行处置
        Assert.True(raw.IsInitialized());
    }

    [Fact]
    public void 样例结果消费失败_调用方输入仍被释放()
    {
        HObject? input = null;
        Action consumeWithFailure = () =>
        {
            using var baseline = new ExampleImageBaseline(ExampleImageBaseline.RegionExample);
            input = baseline.InputImage;
            using var ctx = new FlowContext();
            baseline.AssertResult(baseline.Run(ctx), ctx);
            throw new InvalidOperationException("模拟结果消费失败");
        };

        Assert.Throws<InvalidOperationException>(consumeWithFailure);
        Assert.NotNull(input);
        Assert.False(input.IsInitialized());
    }
}
