using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;
using Xunit;

namespace VisionFlow.Tests
{
    /// <summary>
    /// 叠加底图按区域来源溯源（2026-10-08，`REVIEW-FIX-PROGRESS.md` 已知边界项）：
    /// 多图像流程中，结果显示直接选第二幅图像上产生的区域时，底图应为该区域来源工具的图像输入，
    /// 而不是上下文中第一个图像变量。只引用未初始化的 HObject，不依赖 HALCON 原生库。
    /// </summary>
    public class DisplayOverlaySourceImageTests
    {
        /// <summary>历史 / 插件工具把图像输入声明为 HObject 的情形：能解析出 HalconImage 才作底图。</summary>
        private sealed class HObjectInputTool : ToolBase
        {
            [InputRef("图像", typeof(HObject), Optional = true)]
            public string? ImagePath { get; set; }

            public HObjectInputTool(string moduleName) : base(moduleName) { }

            public override NodeResult Run(FlowContext ctx)
            {
                return NodeResult.Ok;
            }
        }

        private readonly HObject _image1 = new HObject();
        private readonly HObject _image2 = new HObject();

        /// <summary>两个供图工具（图像1 先写入上下文）+ 两个阈值工具，阈值2 的图像输入指向图像2。</summary>
        private FlowContext TwoImageContext()
        {
            var ctx = new FlowContext();
            ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(_image1), 1));
            ctx.SetVariable(Variable.Object("图像2", "Image", new HalconImage(_image2), 1));
            ctx.SetVariable(Variable.Object("阈值1", "Region", new HalconRegion(new HObject()), 1));
            ctx.SetVariable(Variable.Object("阈值2", "Region", new HalconRegion(new HObject()), 1));
            return ctx;
        }

        private static SequenceNode TwoImageFlow(params FlowNode[] extra)
        {
            var root = new SequenceNode("流程");
            root.Children.Add(new ToolNode(new LoadImageTool("图像1")));
            root.Children.Add(new ToolNode(new LoadImageTool("图像2")));
            root.Children.Add(new ToolNode(new ThresholdTool("阈值1") { ImagePath = "图像1.Image" }));
            root.Children.Add(new ToolNode(new ThresholdTool("阈值2") { ImagePath = "图像2.Image" }));
            root.Children.AddRange(extra);
            return root;
        }

        [Fact]
        public void 双图像流程_第二幅图上的区域底图为第二幅图像()
        {
            FlowContext ctx = TwoImageContext();
            SequenceNode root = TwoImageFlow();

            Assert.Same(_image2, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("阈值2", "Region"), root));
            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("阈值1", "Region"), root));
        }

        [Fact]
        public void 不传流程根节点时保持原四级查找_取第一个图像变量()
        {
            FlowContext ctx = TwoImageContext();

            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("阈值2", "Region")));
            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("阈值2", "Region"), null));
        }

        [Fact]
        public void 图像输入指向Input_Image时底图为输入图像()
        {
            var inputImage = new HObject();
            var ctx = new FlowContext();
            ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(_image1), 1));
            ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(inputImage), 1));
            ctx.SetVariable(Variable.Object("阈值1", "Region", new HalconRegion(new HObject()), 1));
            var root = new SequenceNode("流程");
            root.Children.Add(new ToolNode(new LoadImageTool("图像1")));
            root.Children.Add(new ToolNode(new ThresholdTool("阈值1") { ImagePath = "Input.Image" }));

            Assert.Same(inputImage, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("阈值1", "Region"), root));
        }

        [Fact]
        public void 来源工具不在流程中_落到Input_Image或第一个图像变量()
        {
            FlowContext ctx = TwoImageContext();
            ctx.SetVariable(Variable.Object("自定义1", "Region", new HalconRegion(new HObject()), 1));
            SequenceNode root = TwoImageFlow();

            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("自定义1", "Region"), root));

            var inputImage = new HObject();
            ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(inputImage), 1));
            Assert.Same(inputImage, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("自定义1", "Region"), root));
        }

        [Fact]
        public void 自身图像与同模块Image输出仍优先于溯源()
        {
            FlowContext ctx = TwoImageContext();
            var siblingImage = new HObject();
            ctx.SetVariable(Variable.Object("阈值2", "Image", new HalconImage(siblingImage), 1));
            SequenceNode root = TwoImageFlow();

            Assert.Same(siblingImage, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("阈值2", "Region"), root));
            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("图像1", "Image"), root));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("图像9.Image")]
        [InlineData("没有点号")]
        [InlineData("图像2.Image[x]")]
        [InlineData("阈值1.Region")]
        [InlineData("Loop.Current")]
        public void 图像输入为空_不存在_格式错误或不是图像时不抛异常_落到下一级(string imagePath)
        {
            FlowContext ctx = TwoImageContext();
            var root = new SequenceNode("流程");
            root.Children.Add(new ToolNode(new ThresholdTool("阈值2") { ImagePath = imagePath }));

            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("阈值2", "Region"), root));
        }

        [Fact]
        public void 没有图像输入的下游区域工具沿区域引用继续溯源()
        {
            FlowContext ctx = TwoImageContext();
            ctx.SetVariable(Variable.Object("排序2", "Region", new HalconRegion(new HObject()), 1));
            SequenceNode root = TwoImageFlow(new ToolNode(new RegionSortTool("排序2") { RegionPath = "阈值2.Region" }));

            Assert.Same(_image2, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("排序2", "Region"), root));
        }

        [Fact]
        public void 溯源途中上游模块有Image输出时用该输出()
        {
            FlowContext ctx = TwoImageContext();
            var processedImage = new HObject();
            ctx.SetVariable(Variable.Object("处理2", "Image", new HalconImage(processedImage), 1));
            ctx.SetVariable(Variable.Object("处理2", "Region", new HalconRegion(new HObject()), 1));
            ctx.SetVariable(Variable.Object("排序2", "Region", new HalconRegion(new HObject()), 1));
            SequenceNode root = TwoImageFlow(new ToolNode(new RegionSortTool("排序2") { RegionPath = "处理2.Region" }));

            Assert.Same(processedImage, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("排序2", "Region"), root));
        }

        [Fact]
        public void 循环体内的来源工具也能找到()
        {
            FlowContext ctx = TwoImageContext();
            ctx.SetVariable(Variable.Object("排序2", "Region", new HalconRegion(new HObject()), 1));
            ForLoopNode loop = ForLoopNode.Each("循环1", "阈值2.Region");
            loop.Body.Add(new ToolNode(new RegionSortTool("排序2") { RegionPath = "阈值2.Region" }));
            SequenceNode root = TwoImageFlow(loop);

            Assert.Same(_image2, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("排序2", "Region"), root));
        }

        [Fact]
        public void 引用成环时不死循环_落到下一级()
        {
            FlowContext ctx = TwoImageContext();
            ctx.SetVariable(Variable.Object("排序A", "Region", new HalconRegion(new HObject()), 1));
            ctx.SetVariable(Variable.Object("排序B", "Region", new HalconRegion(new HObject()), 1));
            SequenceNode root = TwoImageFlow(
                new ToolNode(new RegionSortTool("排序A") { RegionPath = "排序B.Region" }),
                new ToolNode(new RegionSortTool("排序B") { RegionPath = "排序A.Region" }));

            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("排序A", "Region"), root));
        }

        [Fact]
        public void 超过追溯深度时落到下一级()
        {
            FlowContext ctx = TwoImageContext();
            var extra = new List<FlowNode>();
            string previous = "阈值2.Region";
            for (int i = 1; i <= 40; i++)
            {
                extra.Add(new ToolNode(new RegionSortTool("排序" + i) { RegionPath = previous }));
                ctx.SetVariable(Variable.Object("排序" + i, "Region", new HalconRegion(new HObject()), 1));
                previous = "排序" + i + ".Region";
            }
            SequenceNode root = TwoImageFlow(extra.ToArray());

            Assert.Same(_image2, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("排序20", "Region"), root));
            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("排序40", "Region"), root));
        }

        [Fact]
        public void 图像输入声明为HObject时_解析出HalconImage才作底图_否则继续溯源()
        {
            FlowContext ctx = TwoImageContext();
            ctx.SetVariable(Variable.Object("旧工具1", "Region", new HalconRegion(new HObject()), 1));
            var tool = new HObjectInputTool("旧工具1") { ImagePath = "图像2.Image" };
            SequenceNode root = TwoImageFlow(new ToolNode(tool));

            Assert.Same(_image2, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("旧工具1", "Region"), root));

            tool.ImagePath = "阈值2.Region";
            Assert.Same(_image2, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("旧工具1", "Region"), root));

            tool.ImagePath = "图像9.Region";
            Assert.Same(_image1, DisplayOverlayBuilder.ResolveDisplayBaseImage(ctx, ctx.GetVariable("旧工具1", "Region"), root));
        }
    }
}
