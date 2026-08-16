using System;
using System.Collections.Generic;
using System.IO;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>图像加载工具：从文件读取图像，作为流程的图像源。</summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class LoadImageTool : ToolBase
    {
        /// <summary>图像文件路径。</summary>
        public string FilePath { get; set; }

        public LoadImageTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                return NodeResult.Fail($"图像文件不存在：{FilePath}");
            }

            HOperatorSet.ReadImage(out HObject image, FilePath);
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(image), 1));
            ctx.AddLog(FlowLogLevel.Info, $"[图像加载] {Path.GetFileName(FilePath)}");
            return NodeResult.Ok;
        }
    }

    /// <summary>
    /// 二值化工具（参考 VisionTools.ThresholdTool）：按灰度区间 threshold 分割出区域，
    /// 可选立即做 connection 拆分连通域。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class ThresholdTool : ToolBase
    {
        /// <summary>图像变量引用。</summary>
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; }

        /// <summary>灰度下限。</summary>
        public double MinGray { get; set; } = 128;
        /// <summary>灰度上限。</summary>
        public double MaxGray { get; set; } = 255;
        /// <summary>是否立即拆分连通域（connection）。</summary>
        public bool Connection { get; set; }

        public ThresholdTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;

            HOperatorSet.Threshold(image, out HObject region, MinGray, MaxGray);
            if (Connection)
            {
                HOperatorSet.Connection(region, out HObject connected);
                region.Dispose();
                region = connected;
            }

            HOperatorSet.CountObj(region, out HTuple count);
            if (count.I == 0)
            {
                region.Dispose();
                return NodeResult.Fail($"二值化结果为空（灰度区间 [{MinGray}, {MaxGray}]）");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(region), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[二值化] 灰度 [{MinGray}, {MaxGray}]，区域数 {count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class AutoThresholdTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public double Sigma { get; set; } = 2.0;
        public bool Connection { get; set; }

        public AutoThresholdTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.AutoThreshold(image, out HObject region, Sigma);
            return SetRegionOutput(ctx, region, "AutoThreshold", $"Sigma={Sigma}");
        }

        private NodeResult SetRegionOutput(FlowContext ctx, HObject region, string label, string detail)
        {
            if (Connection)
            {
                HOperatorSet.Connection(region, out HObject connected);
                region.Dispose();
                region = connected;
            }
            return RegionThresholdOutput.Set(ctx, ModuleName, region, label, detail);
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("UsedThreshold", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class BinaryThresholdTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public string Method { get; set; } = "max_separability";
        public string LightDark { get; set; } = "light";
        public bool Connection { get; set; }

        public BinaryThresholdTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.BinaryThreshold(image, out HObject region, Method, LightDark, out HTuple usedThreshold);
            NodeResult result = RegionThresholdOutput.Set(ctx, ModuleName, ApplyConnection(region), "BinaryThreshold",
                $"Method={Method}, LightDark={LightDark}, UsedThreshold={usedThreshold.D}");
            if (result.IsSuccess)
            {
                SetOutput(ctx, Variable.Single(ModuleName, "UsedThreshold", VariableType.Double, usedThreshold.D));
            }
            return result;
        }

        private HObject ApplyConnection(HObject region)
        {
            if (!Connection)
            {
                return region;
            }
            HOperatorSet.Connection(region, out HObject connected);
            region.Dispose();
            return connected;
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class FastThresholdTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public double MinGray { get; set; } = 128;
        public double MaxGray { get; set; } = 255;
        public int MinSize { get; set; } = 20;
        public bool Connection { get; set; }

        public FastThresholdTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.FastThreshold(image, out HObject region, MinGray, MaxGray, MinSize);
            if (Connection)
            {
                HOperatorSet.Connection(region, out HObject connected);
                region.Dispose();
                region = connected;
            }
            return RegionThresholdOutput.Set(ctx, ModuleName, region, "FastThreshold",
                $"Gray=[{MinGray}, {MaxGray}], MinSize={MinSize}");
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("UsedThreshold", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class CharThresholdTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public double Sigma { get; set; } = 1.0;
        public double Percent { get; set; } = 5.0;
        public bool Connection { get; set; }

        public CharThresholdTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.GetDomain(image, out HObject domain);
            try
            {
                HOperatorSet.CharThreshold(image, domain, out HObject region, Sigma, Percent, out HTuple threshold);
                if (Connection)
                {
                    HOperatorSet.Connection(region, out HObject connected);
                    region.Dispose();
                    region = connected;
                }
                NodeResult result = RegionThresholdOutput.Set(ctx, ModuleName, region, "CharThreshold",
                    $"Sigma={Sigma}, Percent={Percent}, Threshold={threshold.D}");
                if (result.IsSuccess)
                {
                    SetOutput(ctx, Variable.Single(ModuleName, "UsedThreshold", VariableType.Double, threshold.D));
                }
                return result;
            }
            finally
            {
                domain.Dispose();
            }
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class VarThresholdTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public int MaskWidth { get; set; } = 15;
        public int MaskHeight { get; set; } = 15;
        public double StdDevScale { get; set; } = 0.2;
        public double AbsThreshold { get; set; } = 15;
        public string LightDark { get; set; } = "light";
        public bool Connection { get; set; }

        public VarThresholdTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.VarThreshold(image, out HObject region, MaskWidth, MaskHeight, StdDevScale, AbsThreshold, LightDark);
            if (Connection)
            {
                HOperatorSet.Connection(region, out HObject connected);
                region.Dispose();
                region = connected;
            }
            return RegionThresholdOutput.Set(ctx, ModuleName, region, "VarThreshold",
                $"Mask={MaskWidth}x{MaskHeight}, StdDevScale={StdDevScale}, AbsThreshold={AbsThreshold}, LightDark={LightDark}");
        }
    }

    internal static class RegionThresholdOutput
    {
        public static NodeResult Set(FlowContext ctx, string moduleName, HObject region, string label, string detail)
        {
            HOperatorSet.CountObj(region, out HTuple count);
            if (count.I == 0)
            {
                region.Dispose();
                return NodeResult.Fail($"{label} 结果为空（{detail}）");
            }

            ctx.SetVariable(Variable.Object(moduleName, "Region", new HalconRegion(region), count.I));
            ctx.SetVariable(Variable.Single(moduleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[{label}] {detail}，区域数 {count.I}");
            return NodeResult.Ok;
        }
    }

    /// <summary>区域处理方式（参考 VisionTools.ProcessRegionTool 的 Method，并补充圆形形态学）。</summary>
    public enum RegionProcessOp
    {
        /// <summary>拆分连通域。</summary>
        Connection,
        /// <summary>填充孔洞。</summary>
        FillUp,
        /// <summary>合并为一个区域。</summary>
        Union1,
        /// <summary>骨架。</summary>
        Skeleton,
        /// <summary>圆形开运算（去小噪点）。</summary>
        OpeningCircle,
        /// <summary>圆形闭运算（补小缺口）。</summary>
        ClosingCircle,
        /// <summary>圆形膨胀。</summary>
        DilationCircle,
        /// <summary>圆形腐蚀。</summary>
        ErosionCircle
    }

    /// <summary>
    /// 区域处理工具（参考 VisionTools.ProcessRegionTool）：对区域做连通拆分 / 填充 / 合并 / 形态学操作。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class RegionProcessTool : ToolBase
    {
        /// <summary>区域变量引用。</summary>
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>处理方式。</summary>
        public RegionProcessOp Method { get; set; } = RegionProcessOp.Connection;
        /// <summary>圆形结构元素半径（仅形态学操作有效）。</summary>
        public double Radius { get; set; } = 3.5;

        public RegionProcessTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject input = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject output;
            switch (Method)
            {
                case RegionProcessOp.FillUp:
                    HOperatorSet.FillUp(input, out output);
                    break;
                case RegionProcessOp.Union1:
                    HOperatorSet.Union1(input, out output);
                    break;
                case RegionProcessOp.Skeleton:
                    HOperatorSet.Skeleton(input, out output);
                    break;
                case RegionProcessOp.OpeningCircle:
                    HOperatorSet.OpeningCircle(input, out output, Radius);
                    break;
                case RegionProcessOp.ClosingCircle:
                    HOperatorSet.ClosingCircle(input, out output, Radius);
                    break;
                case RegionProcessOp.DilationCircle:
                    HOperatorSet.DilationCircle(input, out output, Radius);
                    break;
                case RegionProcessOp.ErosionCircle:
                    HOperatorSet.ErosionCircle(input, out output, Radius);
                    break;
                default:
                    HOperatorSet.Connection(input, out output);
                    break;
            }

            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail($"区域处理（{Method}）结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[区域处理] {Method}，区域数 {count.I}");
            return NodeResult.Ok;
        }
    }

    public enum MorphologyOperation
    {
        closing,
        opening,
        dilation,
        erosion
    }

    public enum RegionShapeTransformType
    {
        convex,
        ellipse,
        inner_circle,
        outer_circle,
        rectangle1,
        rectangle2,
        inner_rectangle1,
        inner_center,
        outer_rectangle1
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class RegionDifferenceTool : ToolBase
    {
        [InputRef("区域1", typeof(HalconRegion))]
        public string RegionPath1 { get; set; }

        [InputRef("区域2", typeof(HalconRegion))]
        public string RegionPath2 { get; set; }

        public bool Connection { get; set; }

        public RegionDifferenceTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region1 = Input<HalconRegion>(ctx, RegionPath1).Object;
            HObject region2 = Input<HalconRegion>(ctx, RegionPath2).Object;
            HOperatorSet.Difference(region1, region2, out HObject output);
            if (Connection)
            {
                HOperatorSet.Union1(output, out HObject union);
                output.Dispose();
                HOperatorSet.Connection(union, out output);
                union.Dispose();
            }

            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("Region 相减结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[Region 相减] 输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class RegionUnion2Tool : ToolBase
    {
        [InputRef("区域1", typeof(HalconRegion))]
        public string RegionPath1 { get; set; }

        [InputRef("区域2", typeof(HalconRegion))]
        public string RegionPath2 { get; set; }

        public bool Connection { get; set; }

        public RegionUnion2Tool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region1 = Input<HalconRegion>(ctx, RegionPath1).Object;
            HObject region2 = Input<HalconRegion>(ctx, RegionPath2).Object;
            HOperatorSet.Union2(region1, region2, out HObject output);
            if (Connection)
            {
                HOperatorSet.Connection(output, out HObject connected);
                output.Dispose();
                output = connected;
            }

            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("Region 合并结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[Region 合并] 输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class RegionShapeTransTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public RegionShapeTransformType Type { get; set; } = RegionShapeTransformType.convex;

        public RegionShapeTransTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.ShapeTrans(region, out HObject output, Type.ToString());
            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("Region 形状转换结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[Region 形状转换] Type={Type}, 输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class RegionUnion1Tool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public bool Connection { get; set; }

        public RegionUnion1Tool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.Union1(region, out HObject output);
            if (Connection)
            {
                HOperatorSet.Connection(output, out HObject connected);
                output.Dispose();
                output = connected;
            }

            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("Region Union1 结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[Region Union1] 输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class MorphologyRectTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public int Width { get; set; } = 5;
        public int Height { get; set; } = 5;
        public bool Connection { get; set; }
        public MorphologyOperation Operation { get; set; } = MorphologyOperation.closing;

        public MorphologyRectTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (Width <= 0 || Height <= 0)
            {
                return NodeResult.Fail("矩形形态学宽高必须大于 0");
            }

            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject output;
            switch (Operation)
            {
                case MorphologyOperation.dilation:
                    HOperatorSet.DilationRectangle1(region, out output, Width, Height);
                    break;
                case MorphologyOperation.erosion:
                    HOperatorSet.ErosionRectangle1(region, out output, Width, Height);
                    break;
                case MorphologyOperation.opening:
                    HOperatorSet.OpeningRectangle1(region, out output, Width, Height);
                    break;
                default:
                    HOperatorSet.ClosingRectangle1(region, out output, Width, Height);
                    break;
            }

            if (Connection)
            {
                HOperatorSet.Union1(output, out HObject union);
                output.Dispose();
                HOperatorSet.Connection(union, out output);
                union.Dispose();
            }

            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("矩形形态学结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[矩形形态学] {Operation}，输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class MorphologyCircleTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public double Radius { get; set; } = 3.5;
        public bool Connection { get; set; }
        public MorphologyOperation Operation { get; set; } = MorphologyOperation.closing;

        public MorphologyCircleTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (Radius <= 0)
            {
                return NodeResult.Fail("圆形形态学半径必须大于 0");
            }

            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject output;
            switch (Operation)
            {
                case MorphologyOperation.dilation:
                    HOperatorSet.DilationCircle(region, out output, Radius);
                    break;
                case MorphologyOperation.erosion:
                    HOperatorSet.ErosionCircle(region, out output, Radius);
                    break;
                case MorphologyOperation.opening:
                    HOperatorSet.OpeningCircle(region, out output, Radius);
                    break;
                default:
                    HOperatorSet.ClosingCircle(region, out output, Radius);
                    break;
            }

            if (Connection)
            {
                HOperatorSet.Union1(output, out HObject union);
                output.Dispose();
                HOperatorSet.Connection(union, out output);
                union.Dispose();
            }

            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("圆形形态学结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[圆形形态学] {Operation}，输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstValue", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class RegionFeaturesTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public string Features { get; set; } = "area,row,column";

        public RegionFeaturesTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            var values = new List<double>();
            foreach (string feature in HalconTupleConvert.SplitCsv(Features))
            {
                HOperatorSet.RegionFeatures(region, feature, out HTuple tuple);
                values.AddRange(HalconTupleConvert.ToDoubles(tuple));
            }

            SetOutput(ctx, Variable.Array(ModuleName, "Values", VariableType.Double, values));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstValue", VariableType.Double, values.Count > 0 ? values[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, values.Count));
            ctx.AddLog(FlowLogLevel.Info, $"[Region 特征值] {Features}，输出 {values.Count} 个数值");
            return NodeResult.Ok;
        }
    }

    internal static class HalconTupleConvert
    {
        public static string[] SplitCsv(string text)
        {
            string[] parts = (text ?? string.Empty).Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }
            return parts;
        }

        public static IEnumerable<double> ToDoubles(HTuple tuple)
        {
            if (tuple == null || tuple.Length == 0)
            {
                yield break;
            }
            for (int i = 0; i < tuple.Length; i++)
            {
                if (tuple[i].Type == HTupleType.INTEGER || tuple[i].Type == HTupleType.LONG || tuple[i].Type == HTupleType.DOUBLE)
                {
                    yield return tuple[i].D;
                }
            }
        }
    }

    /// <summary>筛选后的取件方式。</summary>
    public enum RegionTakeMode
    {
        /// <summary>全部保留。</summary>
        All,
        /// <summary>面积最大。</summary>
        Largest,
        /// <summary>面积最小。</summary>
        Smallest,
        /// <summary>第一个。</summary>
        First,
        /// <summary>最左侧（中心列最小）。</summary>
        Leftmost
    }

    /// <summary>
    /// 区域筛选工具（参考 VisionTools.SelectShapeTool）：按特征区间 select_shape 过滤，
    /// 再按 TakeMode 取件（面积最大/最小/最左等）。输出筛选后的区域与数量。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class SelectRegionTool : ToolBase
    {
        /// <summary>区域变量引用。</summary>
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>筛选特征（select_shape 特征名，如 area / width / height / circularity / rectangularity）。</summary>
        public string Feature { get; set; } = "area";
        /// <summary>特征下限。</summary>
        public double Min { get; set; } = 100;
        /// <summary>特征上限。</summary>
        public double Max { get; set; } = 99999999;
        /// <summary>多个满足条件时的取件方式。</summary>
        public RegionTakeMode TakeMode { get; set; } = RegionTakeMode.All;

        public SelectRegionTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject input = Input<HalconRegion>(ctx, RegionPath).Object;

            HOperatorSet.SelectShape(input, out HObject selected,
                new HTuple(Feature), new HTuple("and"), new HTuple(Min), new HTuple(Max));
            HOperatorSet.CountObj(selected, out HTuple count);
            if (count.I == 0)
            {
                selected.Dispose();
                return NodeResult.Fail($"区域筛选结果为空（特征 {Feature} ∈ [{Min}, {Max}]）");
            }

            HObject result = selected;
            if (TakeMode != RegionTakeMode.All && count.I > 1)
            {
                int pick = PickIndex(selected, count.I);
                HOperatorSet.SelectObj(selected, out result, pick + 1);
                selected.Dispose();
                count = 1;
            }
            else if (TakeMode != RegionTakeMode.All && count.I == 1)
            {
                // 只有一个时无需再取
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(result), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[区域筛选] {Feature} ∈ [{Min}, {Max}]，取 {TakeMode}，输出 {count.I} 个区域");
            return NodeResult.Ok;
        }

        /// <summary>按取件方式返回要选中的下标（0 基）。</summary>
        private int PickIndex(HObject regions, int count)
        {
            if (TakeMode == RegionTakeMode.First)
            {
                return 0;
            }

            HOperatorSet.AreaCenter(regions, out HTuple areas, out _, out HTuple cols);
            int best = 0;
            for (int i = 1; i < count; i++)
            {
                bool better;
                switch (TakeMode)
                {
                    case RegionTakeMode.Smallest:
                        better = areas[i].D < areas[best].D;
                        break;
                    case RegionTakeMode.Leftmost:
                        better = cols[i].D < cols[best].D;
                        break;
                    default: // Largest
                        better = areas[i].D > areas[best].D;
                        break;
                }
                if (better)
                {
                    best = i;
                }
            }
            return best;
        }
    }

    /// <summary>区域方向角的处理方式（参考 VisionTools RegionToHom2dTool 的 HovVec）。</summary>
    public enum RegionAngleMode
    {
        /// <summary>直接使用 orientation_region 的角度。</summary>
        Raw,
        /// <summary>归一化到水平方向（[-π/2, π/2)，对应 HovVec=0）。</summary>
        Horizontal,
        /// <summary>归一化到垂直方向（[0, π)，对应 HovVec=1）。</summary>
        Vertical
    }

    /// <summary>
    /// 区域定位工具（参考 VisionTools.仿射变换.RegionToHom2dTool）：
    /// 把 region 的位姿（中心 + 方向角）转换为变换矩阵，实现"region 信息 → HomMat2D"。
    /// 基准位姿（BaseRow/BaseColumn/BaseAngle）是示教时确定的固定值：
    /// 示教时把本工具的 CenterRow/CenterColumn/Angle 输出写回 Base*，之后不再改变；
    /// 运行时对新图像同样求 region 位姿，生成 "基准位姿 → 当前位姿" 的刚体变换矩阵 Matrix。
    /// 下游测量工具把 MatrixPath 指到 "本工具.Matrix"，即可让固定示教位置跟随 region 变化。
    /// 要求输入恰好 1 个区域（请先经区域筛选工具取件）。
    /// </summary>
    [ToolOutput("CenterRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("CenterColumn", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Angle", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Area", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Matrix", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    public sealed class RegionPoseTool : ToolBase
    {
        /// <summary>区域变量引用（须恰好 1 个区域）。</summary>
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>基准位姿：示教时设定的固定行坐标（不随运行改变）。</summary>
        public double BaseRow { get; set; }
        /// <summary>基准位姿：示教时设定的固定列坐标。</summary>
        public double BaseColumn { get; set; }
        /// <summary>基准位姿：示教时设定的固定角度（弧度）。</summary>
        public double BaseAngle { get; set; }

        /// <summary>方向角处理方式（水平 / 垂直 / 不约束）。</summary>
        public RegionAngleMode AngleMode { get; set; } = RegionAngleMode.Horizontal;

        public RegionPoseTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;

            HOperatorSet.CountObj(region, out HTuple count);
            if (count.I != 1)
            {
                return NodeResult.Fail($"区域定位要求输入恰好 1 个区域，实际 {count.I} 个（请先用区域筛选工具取件）");
            }

            HOperatorSet.AreaCenter(region, out HTuple area, out HTuple row, out HTuple col);
            HOperatorSet.OrientationRegion(region, out HTuple phi);
            double angle = NormalizeAngle(phi.D);

            // region 位姿 → 变换矩阵：固定基准位姿 → 当前 region 位姿
            HomMat2D matrix = HomMat2D.FromPoses(BaseRow, BaseColumn, BaseAngle, row.D, col.D, angle);

            SetOutput(ctx, Variable.Single(ModuleName, "CenterRow", VariableType.Double, row.D));
            SetOutput(ctx, Variable.Single(ModuleName, "CenterColumn", VariableType.Double, col.D));
            SetOutput(ctx, Variable.Single(ModuleName, "Angle", VariableType.Double, angle));
            SetOutput(ctx, Variable.Single(ModuleName, "Area", VariableType.Double, area.D));
            SetOutput(ctx, Variable.Object(ModuleName, "Matrix", matrix, 1));

            ctx.AddLog(FlowLogLevel.Info,
                $"[区域定位] 中心=({row.D:F2}, {col.D:F2}), 角度={angle:F4}, 面积={area.D:F0}，矩阵 {matrix}");
            return NodeResult.Ok;
        }

        /// <summary>按 AngleMode 归一化方向角（与 VisionTools GetAngleHor / GetAngleVec 一致）。</summary>
        private double NormalizeAngle(double phi)
        {
            switch (AngleMode)
            {
                case RegionAngleMode.Horizontal:
                    if (phi >= Math.PI / 2)
                    {
                        return phi - Math.PI;
                    }
                    if (phi < -Math.PI / 2)
                    {
                        return phi + Math.PI;
                    }
                    return phi;
                case RegionAngleMode.Vertical:
                    return phi < 0 ? phi + Math.PI : phi;
                default:
                    return phi;
            }
        }
    }
}
