using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Ui;
using VisionFlow.Variables;
using Xunit;

namespace VisionFlow.Tests
{
    /// <summary>
    /// 显示底图解析修复：流程用“图像加载”供图、没有 Input.Image 时，
    /// 其他模块的区域 / XLD 输出应能回退找到上下文中的图像作为叠加底图，
    /// 而不是画在黑底上（REGION 验收中发现的既有问题，独立小项修复）。
    /// </summary>
    public class DisplayOverlayBaseImageTests
    {
        [Fact]
        public void 图像加载供图_其他模块区域输出回退找到底图()
        {
            var ctx = new FlowContext();
            var loadedImage = new HObject();
            ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(loadedImage), 1));
            ctx.SetVariable(Variable.Object("处理1", "Region", new HalconRegion(new HObject()), 3));

            Variable region = ctx.GetVariable("处理1", "Region");
            Assert.Same(loadedImage, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, region));
        }

        [Fact]
        public void Input_Image存在时优先于扫描回退()
        {
            var ctx = new FlowContext();
            var loadedImage = new HObject();
            var inputImage = new HObject();
            ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(loadedImage), 1));
            ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(inputImage), 1));
            ctx.SetVariable(Variable.Object("处理1", "Region", new HalconRegion(new HObject()), 1));

            Variable region = ctx.GetVariable("处理1", "Region");
            Assert.Same(inputImage, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, region));
        }

        [Fact]
        public void 同模块Image输出仍优先于其他模块图像()
        {
            var ctx = new FlowContext();
            var otherImage = new HObject();
            var siblingImage = new HObject();
            ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(otherImage), 1));
            ctx.SetVariable(Variable.Object("处理1", "Image", new HalconImage(siblingImage), 1));
            ctx.SetVariable(Variable.Object("处理1", "Region", new HalconRegion(new HObject()), 1));

            Variable region = ctx.GetVariable("处理1", "Region");
            Assert.Same(siblingImage, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, region));
        }

        [Fact]
        public void 自身是图像变量时返回自身()
        {
            var ctx = new FlowContext();
            var ownImage = new HObject();
            ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(ownImage), 1));

            Variable imageVariable = ctx.GetVariable("图像1", "Image");
            Assert.Same(ownImage, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, imageVariable));
        }

        [Fact]
        public void 上下文中没有任何图像时返回null()
        {
            var ctx = new FlowContext();
            ctx.SetVariable(Variable.Object("处理1", "Region", new HalconRegion(new HObject()), 1));

            Variable region = ctx.GetVariable("处理1", "Region");
            Assert.Null(DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, region));
        }
    }
}
