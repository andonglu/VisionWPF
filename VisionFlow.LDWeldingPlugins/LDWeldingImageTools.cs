using System;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.LDWeldingPlugins
{
    public enum ImageArithmeticOperation
    {
        Add,
        Sub
    }

    public enum ColorTransformSpace
    {
        hsv,
        hls,
        yuv,
        i1i2i3
    }

    [ToolboxTool("02 图像处理", "均值滤波", Id = "ldwelding.mean-image", DefaultModuleName = "均值滤波")]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class LDMeanImageTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public int Width { get; set; } = 9;
        public int Height { get; set; } = 9;

        public LDMeanImageTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (Width <= 0 || Height <= 0)
            {
                return NodeResult.Fail("均值滤波核宽高必须大于 0");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.MeanImage(image, out HObject output, Width, Height);
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 均值滤波] Width={Width}, Height={Height}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("02 图像处理", "图像加减", Id = "ldwelding.add-sub-image", DefaultModuleName = "图像加减")]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class LDAddSubImageTool : ToolBase
    {
        [InputRef("图像1", typeof(HalconImage))]
        public string ImagePath1 { get; set; } = "Input.Image";

        [InputRef("图像2", typeof(HalconImage))]
        public string ImagePath2 { get; set; }

        public ImageArithmeticOperation Operation { get; set; } = ImageArithmeticOperation.Add;
        public double Multi { get; set; } = 1;
        public int Add { get; set; } = 128;

        public LDAddSubImageTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image1 = Input<HalconImage>(ctx, ImagePath1).Object;
            HObject image2 = Input<HalconImage>(ctx, ImagePath2).Object;
            HObject output;
            if (Operation == ImageArithmeticOperation.Sub)
            {
                HOperatorSet.SubImage(image1, image2, out output, Multi, Add);
            }
            else
            {
                HOperatorSet.AddImage(image1, image2, out output, Multi, Add);
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 图像加减] {Operation}, Multi={Multi}, Add={Add}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("02 图像处理", "通道分解", Id = "ldwelding.decompose-channels", DefaultModuleName = "通道分解")]
    [ToolOutput("Channel1", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Channel2", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Channel3", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("SelectedImage", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("ChannelCount", VariableKind.Single, VariableType.Int)]
    public sealed class LDDecomposeChannelsTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public int Index { get; set; } = 1;

        public LDDecomposeChannelsTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.CountChannels(image, out HTuple countTuple);
            int count = countTuple.I;
            if (count < 1)
            {
                return NodeResult.Fail("图像通道数无效");
            }

            HObject selected = null;
            int selectedIndex = Math.Max(1, Math.Min(Index, count));
            for (int i = 1; i <= Math.Min(count, 3); i++)
            {
                HOperatorSet.AccessChannel(image, out HObject channel, i);
                SetOutput(ctx, Variable.Object(ModuleName, "Channel" + i, new HalconImage(channel), 1));
                if (i == selectedIndex)
                {
                    selected = channel;
                }
            }

            if (selected == null)
            {
                HOperatorSet.AccessChannel(image, out selected, selectedIndex);
            }

            SetOutput(ctx, Variable.Object(ModuleName, "SelectedImage", new HalconImage(selected), 1));
            SetOutput(ctx, Variable.Single(ModuleName, "ChannelCount", VariableType.Int, count));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 通道分解] 通道数={count}, 选择={selectedIndex}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("02 图像处理", "三通道合成", Id = "ldwelding.compose3", DefaultModuleName = "三通道合成")]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class LDCompose3ImageTool : ToolBase
    {
        [InputRef("通道1", typeof(HalconImage))]
        public string Channel1Path { get; set; }

        [InputRef("通道2", typeof(HalconImage))]
        public string Channel2Path { get; set; }

        [InputRef("通道3", typeof(HalconImage))]
        public string Channel3Path { get; set; }

        public LDCompose3ImageTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject channel1 = Input<HalconImage>(ctx, Channel1Path).Object;
            HObject channel2 = Input<HalconImage>(ctx, Channel2Path).Object;
            HObject channel3 = Input<HalconImage>(ctx, Channel3Path).Object;
            HOperatorSet.Compose3(channel1, channel2, channel3, out HObject output);
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, "[LDWelding 三通道合成] 完成");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("02 图像处理", "RGB 色彩空间转换", Id = "ldwelding.trans-color-space", DefaultModuleName = "色彩转换")]
    [ToolOutput("Channel1", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Channel2", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Channel3", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("SelectedImage", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class LDTransColorSpaceTool : ToolBase
    {
        [InputRef("R 通道", typeof(HalconImage))]
        public string Channel1Path { get; set; }

        [InputRef("G 通道", typeof(HalconImage))]
        public string Channel2Path { get; set; }

        [InputRef("B 通道", typeof(HalconImage))]
        public string Channel3Path { get; set; }

        public ColorTransformSpace ColorType { get; set; } = ColorTransformSpace.hsv;
        public int Index { get; set; } = 1;

        public LDTransColorSpaceTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject channel1 = Input<HalconImage>(ctx, Channel1Path).Object;
            HObject channel2 = Input<HalconImage>(ctx, Channel2Path).Object;
            HObject channel3 = Input<HalconImage>(ctx, Channel3Path).Object;
            HOperatorSet.TransFromRgb(channel1, channel2, channel3,
                out HObject out1, out HObject out2, out HObject out3, ColorType.ToString());

            SetOutput(ctx, Variable.Object(ModuleName, "Channel1", new HalconImage(out1), 1));
            SetOutput(ctx, Variable.Object(ModuleName, "Channel2", new HalconImage(out2), 1));
            SetOutput(ctx, Variable.Object(ModuleName, "Channel3", new HalconImage(out3), 1));

            HObject selected = Index == 2 ? out2 : Index == 3 ? out3 : out1;
            SetOutput(ctx, Variable.Object(ModuleName, "SelectedImage", new HalconImage(selected), 1));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 色彩转换] {ColorType}, 选择通道={Math.Max(1, Math.Min(Index, 3))}");
            return NodeResult.Ok;
        }
    }
}
