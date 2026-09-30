using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>阈值分割方式。</summary>
    public enum ThresholdSegmentMethod
    {
        Threshold,
        AutoThreshold,
        BinaryThreshold,
        FastThreshold,
        CharThreshold,
        VarThreshold
    }

    /// <summary>
    /// 阈值分割工具：按 SegmentMethod 选择执行哪种阈值分割（手动/自动/二值/快速/字符/局部阈值），
    /// 可选立即做 connection 拆分连通域。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("UsedThreshold", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public class ThresholdTool : ToolBase
    {
        /// <summary>图像变量引用。</summary>
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; }

        /// <summary>阈值分割方式。</summary>
        public ThresholdSegmentMethod SegmentMethod { get; set; } = ThresholdSegmentMethod.Threshold;

        /// <summary>灰度下限（手动/快速阈值）。</summary>
        public double MinGray { get; set; } = 128;
        /// <summary>灰度上限（手动/快速阈值）。</summary>
        public double MaxGray { get; set; } = 255;
        /// <summary>最小区域尺寸（快速阈值）。</summary>
        public int MinSize { get; set; } = 20;
        /// <summary>平滑系数 Sigma（自动/字符阈值）。</summary>
        public double Sigma { get; set; } = 2.0;
        /// <summary>灰度差异百分比（字符阈值）。</summary>
        public double Percent { get; set; } = 5.0;
        /// <summary>自动确定阈值的方法（二值阈值）：max_separability / smooth_histo。</summary>
        public string BinaryMethod { get; set; } = "max_separability";
        /// <summary>提取亮区还是暗区（二值/局部阈值）：light / dark。</summary>
        public string LightDark { get; set; } = "light";
        /// <summary>局部窗口宽（局部阈值）。</summary>
        public int MaskWidth { get; set; } = 15;
        /// <summary>局部窗口高（局部阈值）。</summary>
        public int MaskHeight { get; set; } = 15;
        /// <summary>标准差缩放系数（局部阈值）。</summary>
        public double StdDevScale { get; set; } = 0.2;
        /// <summary>绝对阈值（局部阈值）。</summary>
        public double AbsThreshold { get; set; } = 15;
        /// <summary>是否立即拆分连通域（connection）。</summary>
        public bool Connection { get; set; }

        public ThresholdTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject region;
            HTuple usedThreshold = null;
            string label;
            string detail;
            switch (SegmentMethod)
            {
                case ThresholdSegmentMethod.AutoThreshold:
                    HOperatorSet.AutoThreshold(image, out region, Sigma);
                    label = "AutoThreshold";
                    detail = $"Sigma={Sigma}";
                    break;
                case ThresholdSegmentMethod.BinaryThreshold:
                    HOperatorSet.BinaryThreshold(image, out region, BinaryMethod, LightDark, out usedThreshold);
                    label = "BinaryThreshold";
                    detail = $"Method={BinaryMethod}, LightDark={LightDark}, UsedThreshold={usedThreshold.D}";
                    break;
                case ThresholdSegmentMethod.FastThreshold:
                    HOperatorSet.FastThreshold(image, out region, MinGray, MaxGray, MinSize);
                    label = "FastThreshold";
                    detail = $"Gray=[{MinGray}, {MaxGray}], MinSize={MinSize}";
                    break;
                case ThresholdSegmentMethod.CharThreshold:
                    HOperatorSet.GetDomain(image, out HObject domain);
                    try
                    {
                        HOperatorSet.CharThreshold(image, domain, out region, Sigma, Percent, out usedThreshold);
                    }
                    finally
                    {
                        domain.Dispose();
                    }
                    label = "CharThreshold";
                    detail = $"Sigma={Sigma}, Percent={Percent}, Threshold={usedThreshold.D}";
                    break;
                case ThresholdSegmentMethod.VarThreshold:
                    HOperatorSet.VarThreshold(image, out region, MaskWidth, MaskHeight, StdDevScale, AbsThreshold, LightDark);
                    label = "VarThreshold";
                    detail = $"Mask={MaskWidth}x{MaskHeight}, StdDevScale={StdDevScale}, AbsThreshold={AbsThreshold}, LightDark={LightDark}";
                    break;
                default:
                    HOperatorSet.Threshold(image, out region, MinGray, MaxGray);
                    label = "二值化";
                    detail = $"灰度 [{MinGray}, {MaxGray}]";
                    break;
            }

            if (Connection)
            {
                HOperatorSet.Connection(region, out HObject connected);
                region.Dispose();
                region = connected;
            }

            NodeResult result = RegionThresholdOutput.Set(ctx, ModuleName, region, label, detail);
            if (result.IsSuccess && usedThreshold != null)
            {
                SetOutput(ctx, Variable.Single(ModuleName, "UsedThreshold", VariableType.Double, usedThreshold.D));
            }
            return result;
        }
    }

    // 以下为旧版独立阈值工具的持久化兼容壳：不进工具箱，仅保证历史流程文件按原方式加载运行，
    // 行为等价于 ThresholdTool 选择对应 SegmentMethod。
    public sealed class AutoThresholdTool : ThresholdTool
    {
        public AutoThresholdTool(string moduleName) : base(moduleName)
        {
            SegmentMethod = ThresholdSegmentMethod.AutoThreshold;
            ImagePath = "Input.Image";
        }
    }

    public sealed class BinaryThresholdTool : ThresholdTool
    {
        public BinaryThresholdTool(string moduleName) : base(moduleName)
        {
            SegmentMethod = ThresholdSegmentMethod.BinaryThreshold;
            ImagePath = "Input.Image";
        }

        /// <summary>历史属性名，等价于 BinaryMethod。</summary>
        public string Method
        {
            get { return BinaryMethod; }
            set { BinaryMethod = value; }
        }
    }

    public sealed class FastThresholdTool : ThresholdTool
    {
        public FastThresholdTool(string moduleName) : base(moduleName)
        {
            SegmentMethod = ThresholdSegmentMethod.FastThreshold;
            ImagePath = "Input.Image";
        }
    }

    public sealed class CharThresholdTool : ThresholdTool
    {
        public CharThresholdTool(string moduleName) : base(moduleName)
        {
            SegmentMethod = ThresholdSegmentMethod.CharThreshold;
            ImagePath = "Input.Image";
            Sigma = 1.0;
        }
    }

    public sealed class VarThresholdTool : ThresholdTool
    {
        public VarThresholdTool(string moduleName) : base(moduleName)
        {
            SegmentMethod = ThresholdSegmentMethod.VarThreshold;
            ImagePath = "Input.Image";
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

            // 与 ToolBase.SetOutput 同一约定：输出归本次运行所有（VF-04）
            Variable output = Variable.Object(moduleName, "Region", new HalconRegion(region), count.I);
            HalconOwnership.Adopt(output);
            ctx.SetVariable(output);
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

    public enum ManualRegionOutputMode
    {
        UnionOne,
        Connection
    }

    public enum ManualRegionShapeKind
    {
        Rectangle1,
        Rectangle2,
        Circle,
        Line
    }

    public enum ManualRegionPolarity
    {
        Include,
        Exclude
    }

    public sealed class ManualRegionDefinition
    {
        public List<ManualRegionShape> Items { get; set; } = new List<ManualRegionShape>();
    }

    public sealed class ManualRegionShape
    {
        public string Name { get; set; }
        public ManualRegionShapeKind Kind { get; set; }
        public ManualRegionPolarity Polarity { get; set; }
        public double Row1 { get; set; }
        public double Column1 { get; set; }
        public double Row2 { get; set; }
        public double Column2 { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Phi { get; set; }
        public double Length1 { get; set; }
        public double Length2 { get; set; }
        public double Radius { get; set; }
    }

    public static class ManualRegionSerializer
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string Serialize(ManualRegionDefinition definition)
        {
            return JsonSerializer.Serialize(definition ?? new ManualRegionDefinition(), Options);
        }

        public static ManualRegionDefinition Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ManualRegionDefinition();
            }

            ManualRegionDefinition definition = JsonSerializer.Deserialize<ManualRegionDefinition>(json, Options);
            return definition ?? new ManualRegionDefinition();
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class ManualRegionTool : ToolBase
    {
        public string RoiJson { get; set; } = ManualRegionSerializer.Serialize(new ManualRegionDefinition());
        public ManualRegionOutputMode OutputMode { get; set; } = ManualRegionOutputMode.UnionOne;

        public ManualRegionTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ManualRegionDefinition definition;
            try
            {
                definition = ManualRegionSerializer.Deserialize(RoiJson);
            }
            catch (Exception ex)
            {
                return NodeResult.Fail("手动 Region 数据解析失败：" + ex.Message);
            }

            if (definition.Items == null || definition.Items.Count == 0)
            {
                return NodeResult.Fail("手动 Region 未绘制任何 ROI");
            }

            HObject include = null;
            HObject exclude = null;
            HObject output = null;
            try
            {
                HOperatorSet.GenEmptyRegion(out include);
                HOperatorSet.GenEmptyRegion(out exclude);
                foreach (ManualRegionShape shape in definition.Items)
                {
                    HObject shapeRegion = CreateShapeRegion(shape);
                    if (shape.Polarity == ManualRegionPolarity.Exclude)
                    {
                        HOperatorSet.Union2(exclude, shapeRegion, out HObject mergedExclude);
                        exclude.Dispose();
                        exclude = mergedExclude;
                    }
                    else
                    {
                        HOperatorSet.Union2(include, shapeRegion, out HObject mergedInclude);
                        include.Dispose();
                        include = mergedInclude;
                    }
                    shapeRegion.Dispose();
                }

                HOperatorSet.Difference(include, exclude, out output);
                if (OutputMode == ManualRegionOutputMode.Connection)
                {
                    HOperatorSet.Connection(output, out HObject connected);
                    output.Dispose();
                    output = connected;
                }

                HOperatorSet.CountObj(output, out HTuple count);
                if (count.I == 0)
                {
                    output.Dispose();
                    return NodeResult.Fail("手动 Region 输出为空");
                }

                SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
                SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
                ctx.AddLog(FlowLogLevel.Info, $"[手动 Region] ROI={definition.Items.Count}, 输出模式={OutputMode}, 区域数={count.I}");
                return NodeResult.Ok;
            }
            finally
            {
                include?.Dispose();
                exclude?.Dispose();
            }
        }

        private static HObject CreateShapeRegion(ManualRegionShape shape)
        {
            switch (shape.Kind)
            {
                case ManualRegionShapeKind.Rectangle1:
                    HOperatorSet.GenRectangle1(out HObject rectangle1, shape.Row1, shape.Column1, shape.Row2, shape.Column2);
                    return rectangle1;
                case ManualRegionShapeKind.Rectangle2:
                    HOperatorSet.GenRectangle2(out HObject rectangle2, shape.Row, shape.Column, shape.Phi,
                        Math.Max(1, shape.Length1), Math.Max(1, shape.Length2));
                    return rectangle2;
                case ManualRegionShapeKind.Circle:
                    HOperatorSet.GenCircle(out HObject circle, shape.Row, shape.Column, Math.Max(1, shape.Radius));
                    return circle;
                case ManualRegionShapeKind.Line:
                    HOperatorSet.GenRegionLine(out HObject line, shape.Row1, shape.Column1, shape.Row2, shape.Column2);
                    return line;
                default:
                    throw new InvalidOperationException("不支持的手动 ROI 类型：" + shape.Kind);
            }
        }
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
    public sealed class RegionIntersectionTool : ToolBase
    {
        [InputRef("区域1", typeof(HalconRegion))]
        public string RegionPath1 { get; set; }

        [InputRef("区域2", typeof(HalconRegion))]
        public string RegionPath2 { get; set; }

        public bool Connection { get; set; }

        public RegionIntersectionTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region1 = Input<HalconRegion>(ctx, RegionPath1).Object;
            HObject region2 = Input<HalconRegion>(ctx, RegionPath2).Object;
            HOperatorSet.Intersection(region1, region2, out HObject output);
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
                return NodeResult.Fail("Region 交集结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[Region 交集] 输出区域数={count.I}");
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

    /// <summary>形态学结构元素形状。</summary>
    public enum MorphologyShape
    {
        Rectangle,
        Circle
    }

    /// <summary>
    /// 形态学工具：按 Shape 选择矩形或圆形结构元素执行膨胀/腐蚀/开/闭运算，
    /// 可选 connection 拆分连通域。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public class MorphologyTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>结构元素形状：矩形 / 圆形。</summary>
        public MorphologyShape Shape { get; set; } = MorphologyShape.Rectangle;

        /// <summary>结构元素宽（矩形）。</summary>
        public int Width { get; set; } = 5;
        /// <summary>结构元素高（矩形）。</summary>
        public int Height { get; set; } = 5;
        /// <summary>结构元素半径（圆形）。</summary>
        public double Radius { get; set; } = 3.5;
        public bool Connection { get; set; }
        public MorphologyOperation Operation { get; set; } = MorphologyOperation.closing;

        public MorphologyTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            string label = Shape == MorphologyShape.Rectangle ? "矩形形态学" : "圆形形态学";
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject output;
            if (Shape == MorphologyShape.Rectangle)
            {
                if (Width <= 0 || Height <= 0)
                {
                    return NodeResult.Fail("矩形形态学宽高必须大于 0");
                }
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
            }
            else
            {
                if (Radius <= 0)
                {
                    return NodeResult.Fail("圆形形态学半径必须大于 0");
                }
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
                return NodeResult.Fail($"{label}结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[{label}] {Operation}，输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    // 旧版形态学工具的持久化兼容壳：不进工具箱，仅保证历史流程文件按原方式加载运行。
    public sealed class MorphologyRectTool : MorphologyTool
    {
        public MorphologyRectTool(string moduleName) : base(moduleName)
        {
            Shape = MorphologyShape.Rectangle;
        }
    }

    public sealed class MorphologyCircleTool : MorphologyTool
    {
        public MorphologyCircleTool(string moduleName) : base(moduleName)
        {
            Shape = MorphologyShape.Circle;
        }
    }

    [ToolOutput("MinGrays", VariableKind.Array, VariableType.Double)]
    [ToolOutput("MaxGrays", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Ranges", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstMinGray", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstMaxGray", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstRange", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class RegionMinMaxGrayTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public double Percent { get; set; }

        public RegionMinMaxGrayTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (Percent < 0 || Percent > 100)
            {
                return NodeResult.Fail("Percent 必须在 0 到 100 之间");
            }

            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.CountObj(region, out HTuple regionCount);
            if (regionCount.I == 0)
            {
                return NodeResult.Fail("Region 灰度统计输入区域为空");
            }

            HOperatorSet.MinMaxGray(region, image, Percent, out HTuple minTuple, out HTuple maxTuple, out HTuple rangeTuple);
            var mins = new List<double>(HalconTupleConvert.ToDoubles(minTuple));
            var maxs = new List<double>(HalconTupleConvert.ToDoubles(maxTuple));
            var ranges = new List<double>(HalconTupleConvert.ToDoubles(rangeTuple));
            int count = Math.Max(mins.Count, Math.Max(maxs.Count, ranges.Count));
            if (count == 0)
            {
                return NodeResult.Fail("Region 灰度统计结果为空");
            }

            SetOutput(ctx, Variable.Array(ModuleName, "MinGrays", VariableType.Double, mins));
            SetOutput(ctx, Variable.Array(ModuleName, "MaxGrays", VariableType.Double, maxs));
            SetOutput(ctx, Variable.Array(ModuleName, "Ranges", VariableType.Double, ranges));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstMinGray", VariableType.Double, mins.Count > 0 ? mins[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstMaxGray", VariableType.Double, maxs.Count > 0 ? maxs[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstRange", VariableType.Double, ranges.Count > 0 ? ranges[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count));
            ctx.AddLog(FlowLogLevel.Info, $"[MinMaxGray] 区域数={regionCount.I}, 输出={count}, Percent={Percent}");
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
    /// 运行时对新图像同样求 region 位姿，同时生成 "基准位姿 → 当前位姿" 和
    /// "当前位姿 → 基准位姿" 两个刚体变换矩阵。
    /// 下游测量工具把 MatrixPath 指到 "本工具.Matrix" 或 "本工具.BaseToCurrentMatrix"，
    /// 即可让固定示教位置跟随 region 变化；图像对齐到标准检测位置时使用 CurrentToBaseMatrix。
    /// 要求输入恰好 1 个区域（请先经区域筛选工具取件）。
    /// </summary>
    [ToolOutput("CenterRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("CenterColumn", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Angle", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Area", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Matrix", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("BaseToCurrentMatrix", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("CurrentToBaseMatrix", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    public sealed class RegionPoseTool : ToolBase
    {
        private sealed class PoseInfo
        {
            public double Row { get; set; }
            public double Column { get; set; }
            public double Angle { get; set; }
            public double Area { get; set; }
        }

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
            PoseInfo pose;
            try
            {
                pose = ReadSingleRegionPose(region);
            }
            catch (InvalidOperationException ex)
            {
                return NodeResult.Fail(ex.Message);
            }

            HomMat2D baseToCurrent = HomMat2D.FromPoses(BaseRow, BaseColumn, BaseAngle, pose.Row, pose.Column, pose.Angle);
            HomMat2D currentToBase = HomMat2D.FromPoses(pose.Row, pose.Column, pose.Angle, BaseRow, BaseColumn, BaseAngle);

            SetOutput(ctx, Variable.Single(ModuleName, "CenterRow", VariableType.Double, pose.Row));
            SetOutput(ctx, Variable.Single(ModuleName, "CenterColumn", VariableType.Double, pose.Column));
            SetOutput(ctx, Variable.Single(ModuleName, "Angle", VariableType.Double, pose.Angle));
            SetOutput(ctx, Variable.Single(ModuleName, "Area", VariableType.Double, pose.Area));
            SetOutput(ctx, Variable.Object(ModuleName, "Matrix", baseToCurrent, 1));
            SetOutput(ctx, Variable.Object(ModuleName, "BaseToCurrentMatrix", baseToCurrent, 1));
            SetOutput(ctx, Variable.Object(ModuleName, "CurrentToBaseMatrix", currentToBase, 1));

            ctx.AddLog(FlowLogLevel.Info,
                $"[区域定位] 中心=({pose.Row:F2}, {pose.Column:F2}), 角度={pose.Angle:F4}, 面积={pose.Area:F0}，BaseToCurrent={baseToCurrent}, CurrentToBase={currentToBase}");
            return NodeResult.Ok;
        }

        public void SetBaseFromRegion(HObject region)
        {
            PoseInfo pose = ReadSingleRegionPose(region);
            BaseRow = pose.Row;
            BaseColumn = pose.Column;
            BaseAngle = pose.Angle;
        }

        private PoseInfo ReadSingleRegionPose(HObject region)
        {
            HOperatorSet.CountObj(region, out HTuple count);
            if (count.I != 1)
            {
                throw new InvalidOperationException($"区域定位要求输入恰好 1 个区域，实际 {count.I} 个（请先用区域筛选工具取件或合并）");
            }

            HOperatorSet.AreaCenter(region, out HTuple area, out HTuple row, out HTuple col);
            HOperatorSet.OrientationRegion(region, out HTuple phi);
            return new PoseInfo
            {
                Row = row.D,
                Column = col.D,
                Angle = NormalizeAngle(phi.D),
                Area = area.D
            };
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
