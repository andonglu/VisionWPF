using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        VarThreshold,
        /// <summary>动态阈值：与均值滤波后的参考图比较（mean_image + dyn_threshold）。</summary>
        DynThreshold
    }

    /// <summary>
    /// 提取亮区还是暗区（成员名即 HALCON 参数值）。原为字符串参数，按名称保存，文件内容与旧版一致、旧版程序仍可读取。
    /// 二值阈值只支持 light / dark；局部阈值、动态阈值支持全部四种。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ThresholdLightDark
    {
        light,
        dark,
        equal,
        not_equal
    }

    /// <summary>二值阈值自动确定阈值的方法（成员名即 HALCON 参数值）。原为字符串参数，按名称保存以保持文件兼容。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BinaryThresholdMethod
    {
        max_separability,
        smooth_histo
    }

    /// <summary>
    /// 阈值分割工具：按 SegmentMethod 选择执行哪种阈值分割（手动/自动/二值/快速/字符/局部/动态阈值），
    /// 可选立即做 connection 拆分连通域。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("UsedThreshold", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public class ThresholdTool : ToolBase, INotFoundPolicy, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>图像变量引用。</summary>
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; }

        /// <summary>分割结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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
        /// <summary>自动确定阈值的方法（二值阈值）。</summary>
        public BinaryThresholdMethod BinaryMethod { get; set; } = BinaryThresholdMethod.max_separability;
        /// <summary>提取亮区还是暗区（二值阈值只支持 light / dark；局部、动态阈值另支持 equal / not_equal）。</summary>
        public ThresholdLightDark LightDark { get; set; } = ThresholdLightDark.light;
        /// <summary>局部窗口宽（局部阈值）；动态阈值时为均值滤波掩膜宽。</summary>
        public int MaskWidth { get; set; } = 15;
        /// <summary>局部窗口高（局部阈值）；动态阈值时为均值滤波掩膜高。</summary>
        public int MaskHeight { get; set; } = 15;
        /// <summary>标准差缩放系数（局部阈值）。</summary>
        public double StdDevScale { get; set; } = 0.2;
        /// <summary>绝对阈值（局部阈值）。</summary>
        public double AbsThreshold { get; set; } = 15;
        /// <summary>与参考图的灰度差（动态阈值），作为 UsedThreshold 输出。</summary>
        public double Offset { get; set; } = 5;
        /// <summary>是否立即拆分连通域（connection）。</summary>
        public bool Connection { get; set; }

        public ThresholdTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (SegmentMethod == ThresholdSegmentMethod.BinaryThreshold
                && LightDark != ThresholdLightDark.light && LightDark != ThresholdLightDark.dark)
            {
                yield return new ToolConfigurationIssue(nameof(LightDark), "二值阈值只支持 light / dark");
            }
            if ((SegmentMethod == ThresholdSegmentMethod.VarThreshold || SegmentMethod == ThresholdSegmentMethod.DynThreshold)
                && (MaskWidth <= 0 || MaskHeight <= 0))
            {
                yield return new ToolConfigurationIssue("MaskWidth / MaskHeight", "掩膜宽高必须大于 0");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(MinGray):
                case nameof(MaxGray):
                    return SegmentMethod == ThresholdSegmentMethod.Threshold || SegmentMethod == ThresholdSegmentMethod.FastThreshold;
                case nameof(MinSize):
                    return SegmentMethod == ThresholdSegmentMethod.FastThreshold;
                case nameof(Sigma):
                    return SegmentMethod == ThresholdSegmentMethod.AutoThreshold || SegmentMethod == ThresholdSegmentMethod.CharThreshold;
                case nameof(Percent):
                    return SegmentMethod == ThresholdSegmentMethod.CharThreshold;
                case nameof(BinaryMethod):
                    return SegmentMethod == ThresholdSegmentMethod.BinaryThreshold;
                case nameof(LightDark):
                    return SegmentMethod == ThresholdSegmentMethod.BinaryThreshold || SegmentMethod == ThresholdSegmentMethod.VarThreshold
                        || SegmentMethod == ThresholdSegmentMethod.DynThreshold;
                case nameof(MaskWidth):
                case nameof(MaskHeight):
                    return SegmentMethod == ThresholdSegmentMethod.VarThreshold || SegmentMethod == ThresholdSegmentMethod.DynThreshold;
                case nameof(StdDevScale):
                case nameof(AbsThreshold):
                    return SegmentMethod == ThresholdSegmentMethod.VarThreshold;
                case nameof(Offset):
                    return SegmentMethod == ThresholdSegmentMethod.DynThreshold;
                case "Method":
                    // 旧版二值阈值工具的同义属性，与 BinaryMethod 重复，不再单独显示
                    return false;
                default:
                    return true;
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject region;
            HTuple usedThreshold = null;
            string label;
            string detail;
            switch (SegmentMethod)
            {
                case ThresholdSegmentMethod.DynThreshold:
                    HOperatorSet.MeanImage(image, out HObject reference, MaskWidth, MaskHeight);
                    try
                    {
                        HOperatorSet.DynThreshold(image, reference, out region, Offset, LightDark.ToString());
                    }
                    finally
                    {
                        reference.Dispose();
                    }
                    usedThreshold = new HTuple(Offset);
                    label = "DynThreshold";
                    detail = $"Mask={MaskWidth}x{MaskHeight}, Offset={Offset}, LightDark={LightDark}";
                    break;
                case ThresholdSegmentMethod.AutoThreshold:
                    HOperatorSet.AutoThreshold(image, out region, Sigma);
                    label = "AutoThreshold";
                    detail = $"Sigma={Sigma}";
                    break;
                case ThresholdSegmentMethod.BinaryThreshold:
                    HOperatorSet.BinaryThreshold(image, out region, BinaryMethod.ToString(), LightDark.ToString(), out usedThreshold);
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
                    HOperatorSet.VarThreshold(image, out region, MaskWidth, MaskHeight, StdDevScale, AbsThreshold, LightDark.ToString());
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

            NodeResult result = RegionOutput.Set(ctx, ModuleName, this, region, label, detail);
            if (usedThreshold != null)
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

        /// <summary>历史属性名，等价于 BinaryMethod（按名称解析，不区分大小写）。</summary>
        public string Method
        {
            get { return BinaryMethod.ToString(); }
            set { BinaryMethod = (BinaryThresholdMethod)Enum.Parse(typeof(BinaryThresholdMethod), value, ignoreCase: true); }
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

    /// <summary>
    /// 区域类工具的统一输出（TR-06）：写 Region / Count / Found；区域个数为 0 时按“未找到”策略处理。
    /// 与 ToolBase.SetOutput 同一约定：输出归本次运行所有（VF-04）。
    /// </summary>
    internal static class RegionOutput
    {
        public static NodeResult Set(FlowContext ctx, string moduleName, INotFoundPolicy policy, HObject region,
            string label, string detail)
        {
            HOperatorSet.CountObj(region, out HTuple count);
            Variable output = Variable.Object(moduleName, "Region", new HalconRegion(region), count.I);
            HalconOwnership.Adopt(output);
            ctx.SetVariable(output);
            ctx.SetVariable(Variable.Single(moduleName, "Count", VariableType.Int, count.I));
            ctx.SetVariable(Variable.Single(moduleName, "Found", VariableType.Bool, count.I > 0));
            if (count.I == 0)
            {
                return NotFoundOutcome.Resolve(ctx, policy,
                    string.IsNullOrEmpty(detail) ? $"{label}结果为空" : $"{label}结果为空（{detail}）");
            }
            ctx.AddLog(FlowLogLevel.Info, string.IsNullOrEmpty(detail)
                ? $"[{label}] 区域数 {count.I}"
                : $"[{label}] {detail}，区域数 {count.I}");
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
        ErosionCircle,
        /// <summary>矩形开运算。</summary>
        OpeningRectangle,
        /// <summary>矩形闭运算。</summary>
        ClosingRectangle,
        /// <summary>矩形膨胀。</summary>
        DilationRectangle,
        /// <summary>矩形腐蚀。</summary>
        ErosionRectangle,
        /// <summary>按形状特征填充孔洞（fill_up_shape）。</summary>
        FillUpShape,
        /// <summary>区域边界（boundary）。</summary>
        Boundary,
        /// <summary>按固定宽高切分（partition_rectangle）。</summary>
        PartitionRectangle,
        /// <summary>在细窄处动态切分（partition_dynamic）。</summary>
        PartitionDynamic,
        /// <summary>取反：裁剪图像范围减去区域。</summary>
        Complement
    }

    /// <summary>填充孔洞时判断的形状特征（成员名即 HALCON 参数值）。</summary>
    public enum RegionFillFeature
    {
        area,
        compactness,
        convexity,
        anisometry,
        phi,
        ra,
        rb,
        inner_circle,
        outer_circle
    }

    /// <summary>边界类型（成员名即 HALCON 参数值）。</summary>
    public enum RegionBoundaryType
    {
        inner,
        inner_filled,
        outer
    }

    /// <summary>
    /// 区域处理工具（参考 VisionTools.ProcessRegionTool）：对区域做连通拆分 / 填充 / 合并 / 骨架 /
    /// 圆形或矩形结构元素的形态学操作，以及按形状填充、边界、切分与取反（RG-02）。
    /// 工具箱中的区域形态学与 Union1 统一由本工具提供（TR-11）。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionProcessTool : ToolBase, INotFoundPolicy, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>区域变量引用。</summary>
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>取反时的裁剪范围（取该图像的宽高）；从工具箱新建时默认 Input.Image，其他方式不读取。</summary>
        [InputRef("裁剪图像", typeof(HalconImage), Optional = true)]
        public string ClipImagePath { get; set; }

        /// <summary>处理方式。</summary>
        public RegionProcessOp Method { get; set; } = RegionProcessOp.Connection;
        /// <summary>圆形结构元素半径（仅圆形形态学有效）。</summary>
        public double Radius { get; set; } = 3.5;
        /// <summary>矩形结构元素宽（矩形形态学）；切分时为切块宽度。</summary>
        public int Width { get; set; } = 5;
        /// <summary>矩形结构元素高（矩形形态学）；按固定宽高切分时为切块高度。</summary>
        public int Height { get; set; } = 5;
        /// <summary>按形状填充孔洞时判断的特征。</summary>
        public RegionFillFeature FillFeature { get; set; } = RegionFillFeature.area;
        /// <summary>特征下限：特征值在 [FillMin, FillMax] 内的孔洞被填充。</summary>
        public double FillMin { get; set; } = 1;
        /// <summary>特征上限。</summary>
        public double FillMax { get; set; } = 100;
        /// <summary>边界类型。</summary>
        public RegionBoundaryType BoundaryType { get; set; } = RegionBoundaryType.inner;
        /// <summary>动态切分时切点位置的最大偏移（占宽度的百分比，0 ~ 100）。</summary>
        public double Percent { get; set; } = 20;
        /// <summary>处理结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public RegionProcessTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (IsRectangleOp(Method) && (Width <= 0 || Height <= 0))
            {
                yield return new ToolConfigurationIssue("Width / Height", "矩形形态学宽高必须大于 0");
            }
            if (IsCircleOp(Method) && Radius <= 0)
            {
                yield return new ToolConfigurationIssue(nameof(Radius), "圆形形态学半径必须大于 0");
            }
            if (Method == RegionProcessOp.FillUpShape && FillMin > FillMax)
            {
                yield return new ToolConfigurationIssue("FillMin / FillMax", "特征下限不能大于上限");
            }
            if (Method == RegionProcessOp.PartitionRectangle && (Width <= 0 || Height <= 0))
            {
                yield return new ToolConfigurationIssue("Width / Height", "切块宽高必须大于 0");
            }
            if (Method == RegionProcessOp.PartitionDynamic && Width <= 0)
            {
                yield return new ToolConfigurationIssue(nameof(Width), "切块宽度必须大于 0");
            }
            if (Method == RegionProcessOp.PartitionDynamic && (Percent < 0 || Percent > 100))
            {
                yield return new ToolConfigurationIssue(nameof(Percent), "必须在 0 到 100 之间");
            }
            if (Method == RegionProcessOp.Complement && string.IsNullOrWhiteSpace(ClipImagePath))
            {
                yield return new ToolConfigurationIssue("裁剪图像", "取反需要指定裁剪图像");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(Radius):
                    return IsCircleOp(Method);
                case nameof(Width):
                    return IsRectangleOp(Method) || Method == RegionProcessOp.PartitionRectangle || Method == RegionProcessOp.PartitionDynamic;
                case nameof(Height):
                    return IsRectangleOp(Method) || Method == RegionProcessOp.PartitionRectangle;
                case nameof(FillFeature):
                case nameof(FillMin):
                case nameof(FillMax):
                    return Method == RegionProcessOp.FillUpShape;
                case nameof(BoundaryType):
                    return Method == RegionProcessOp.Boundary;
                case nameof(Percent):
                    return Method == RegionProcessOp.PartitionDynamic;
                case nameof(ClipImagePath):
                    return Method == RegionProcessOp.Complement;
                default:
                    return true;
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail(issue.Parameter == "裁剪图像" ? issue.Message : $"{issue.Parameter}：{issue.Message}");
            }

            HObject input = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject output;
            switch (Method)
            {
                case RegionProcessOp.FillUpShape:
                    HOperatorSet.FillUpShape(input, out output, FillFeature.ToString(), FillMin, FillMax);
                    break;
                case RegionProcessOp.Boundary:
                    HOperatorSet.Boundary(input, out output, BoundaryType.ToString());
                    break;
                case RegionProcessOp.PartitionRectangle:
                    HOperatorSet.PartitionRectangle(input, out output, Width, Height);
                    break;
                case RegionProcessOp.PartitionDynamic:
                    HOperatorSet.PartitionDynamic(input, out output, Width, Percent);
                    break;
                case RegionProcessOp.Complement:
                    output = Complement(input, Input<HalconImage>(ctx, ClipImagePath).Object);
                    break;
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
                case RegionProcessOp.OpeningRectangle:
                    HOperatorSet.OpeningRectangle1(input, out output, Width, Height);
                    break;
                case RegionProcessOp.ClosingRectangle:
                    HOperatorSet.ClosingRectangle1(input, out output, Width, Height);
                    break;
                case RegionProcessOp.DilationRectangle:
                    HOperatorSet.DilationRectangle1(input, out output, Width, Height);
                    break;
                case RegionProcessOp.ErosionRectangle:
                    HOperatorSet.ErosionRectangle1(input, out output, Width, Height);
                    break;
                default:
                    HOperatorSet.Connection(input, out output);
                    break;
            }

            return RegionOutput.Set(ctx, ModuleName, this, output, "区域处理", Method.ToString());
        }

        /// <summary>取反：裁剪图像的整幅范围减去输入区域（多个区域按合并后计算），结果为一个区域。</summary>
        private static HObject Complement(HObject region, HObject clipImage)
        {
            HOperatorSet.GetImageSize(clipImage, out HTuple width, out HTuple height);
            HOperatorSet.GenRectangle1(out HObject clip, 0, 0, height.I - 1, width.I - 1);
            try
            {
                HOperatorSet.Difference(clip, region, out HObject output);
                return output;
            }
            finally
            {
                clip.Dispose();
            }
        }

        private static bool IsRectangleOp(RegionProcessOp op)
        {
            return op == RegionProcessOp.OpeningRectangle || op == RegionProcessOp.ClosingRectangle
                || op == RegionProcessOp.DilationRectangle || op == RegionProcessOp.ErosionRectangle;
        }

        private static bool IsCircleOp(RegionProcessOp op)
        {
            return op == RegionProcessOp.OpeningCircle || op == RegionProcessOp.ClosingCircle
                || op == RegionProcessOp.DilationCircle || op == RegionProcessOp.ErosionCircle;
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
        Connection,
        /// <summary>每个包含 ROI 单独输出一个区域（扣除全部排除 ROI），顺序与绘制/阵列生成顺序一致，适合分区检测。</summary>
        PerShape
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

        /// <summary>复制并平移（行、列方向），形状与角度不变。</summary>
        public ManualRegionShape Offset(double deltaRow, double deltaColumn, string name)
        {
            var copy = (ManualRegionShape)MemberwiseClone();
            copy.Name = name;
            copy.Row1 += deltaRow;
            copy.Row2 += deltaRow;
            copy.Row += deltaRow;
            copy.Column1 += deltaColumn;
            copy.Column2 += deltaColumn;
            copy.Column += deltaColumn;
            return copy;
        }

        /// <summary>
        /// 按变换矩阵变换 ROI 形状（编辑器示教换算用；运行时直接对生成的区域做 affine_trans_region）。
        /// 轴对齐矩形在有旋转或缩放时转换为旋转矩形；变换后仍轴对齐且无缩放时保持轴对齐矩形。
        /// </summary>
        public ManualRegionShape Transform(HomMat2D matrix)
        {
            if (matrix == null)
            {
                throw new ArgumentNullException(nameof(matrix));
            }
            var copy = (ManualRegionShape)MemberwiseClone();
            double scale = matrix.ScaleFactor;
            switch (Kind)
            {
                case ManualRegionShapeKind.Rectangle1:
                {
                    double row = (Row1 + Row2) / 2.0;
                    double column = (Column1 + Column2) / 2.0;
                    double halfWidth = (Column2 - Column1) / 2.0;
                    double halfHeight = (Row2 - Row1) / 2.0;
                    matrix.TransformPose(row, column, 0, out double newRow, out double newColumn, out double phi);
                    if (Math.Abs(phi) < 1e-9 && Math.Abs(scale - 1) < 1e-9)
                    {
                        copy.Row1 = newRow - halfHeight;
                        copy.Row2 = newRow + halfHeight;
                        copy.Column1 = newColumn - halfWidth;
                        copy.Column2 = newColumn + halfWidth;
                        return copy;
                    }
                    copy.Kind = ManualRegionShapeKind.Rectangle2;
                    copy.Row = newRow;
                    copy.Column = newColumn;
                    copy.Phi = phi;
                    copy.Length1 = halfWidth * scale;
                    copy.Length2 = halfHeight * scale;
                    return copy;
                }
                case ManualRegionShapeKind.Rectangle2:
                    matrix.TransformPose(Row, Column, Phi, out double rectRow, out double rectColumn, out double rectPhi);
                    copy.Row = rectRow;
                    copy.Column = rectColumn;
                    copy.Phi = rectPhi;
                    copy.Length1 = Length1 * scale;
                    copy.Length2 = Length2 * scale;
                    return copy;
                case ManualRegionShapeKind.Circle:
                    matrix.TransformPoint(Row, Column, out double circleRow, out double circleColumn);
                    copy.Row = circleRow;
                    copy.Column = circleColumn;
                    copy.Radius = Radius * scale;
                    return copy;
                case ManualRegionShapeKind.Line:
                    matrix.TransformPoint(Row1, Column1, out double row1, out double column1);
                    matrix.TransformPoint(Row2, Column2, out double row2, out double column2);
                    copy.Row1 = row1;
                    copy.Column1 = column1;
                    copy.Row2 = row2;
                    copy.Column2 = column2;
                    return copy;
                default:
                    throw new InvalidOperationException("不支持的手动 ROI 类型：" + Kind);
            }
        }
    }

    /// <summary>手动 ROI 阵列生成：以一个 ROI 为左上角，按行列间距生成网格（先行后列，含原 ROI）。</summary>
    public static class ManualRegionArray
    {
        public static List<ManualRegionShape> Generate(ManualRegionShape seed, int rows, int columns,
            double rowPitch, double columnPitch)
        {
            if (seed == null)
            {
                throw new ArgumentNullException(nameof(seed));
            }
            if (rows < 1 || columns < 1)
            {
                throw new ArgumentException("阵列行数和列数必须大于 0");
            }
            string baseName = string.IsNullOrWhiteSpace(seed.Name) ? seed.Kind.ToString() : seed.Name;
            var shapes = new List<ManualRegionShape>(rows * columns);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    shapes.Add(seed.Offset(r * rowPitch, c * columnPitch, $"{baseName}_{r + 1}-{c + 1}"));
                }
            }
            return shapes;
        }
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

        /// <summary>
        /// 变换矩阵（可选）：ROI 按示教基准坐标保存，配置后运行时整体变换到当前位姿（affine_trans_region），
        /// 如引用“区域定位1.Matrix”或匹配的“BestHomMat”；多个工件请在循环中引用 Loop.Current.HomMat。
        /// 未配置时按基准坐标输出。
        /// </summary>
        [InputRef("变换矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        public ManualRegionTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (!FollowMatrixResolver.TryResolve(ctx, ModuleName, MatrixPath, out List<HomMat2D> matrices, out string matrixError))
            {
                return NodeResult.Fail(matrixError);
            }
            if (matrices.Count > 1)
            {
                return NodeResult.Fail($"{ModuleName} 的变换矩阵只支持单个矩阵（当前为 {matrices.Count} 个）；多个工件请放在 For 循环中引用 Loop.Current.HomMat");
            }
            HomMat2D matrix = matrices[0];

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
                HOperatorSet.GenEmptyObj(out HObject perShape);
                try
                {
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
                            HOperatorSet.ConcatObj(perShape, shapeRegion, out HObject mergedPerShape);
                            perShape.Dispose();
                            perShape = mergedPerShape;
                        }
                        shapeRegion.Dispose();
                    }

                    // PerShape：每个包含 ROI 各自扣除排除区域，保持绘制顺序
                    HOperatorSet.Difference(OutputMode == ManualRegionOutputMode.PerShape ? perShape : include, exclude, out output);
                }
                finally
                {
                    perShape.Dispose();
                }
                if (OutputMode == ManualRegionOutputMode.Connection)
                {
                    HOperatorSet.Connection(output, out HObject connected);
                    output.Dispose();
                    output = connected;
                }
                if (matrix != null)
                {
                    // 逐对象变换，保持对象顺序（PerShape 的格子序号不变）
                    HOperatorSet.AffineTransRegion(output, out HObject followed, matrix.Data, "nearest_neighbor");
                    output.Dispose();
                    output = followed;
                }

                HOperatorSet.CountObj(output, out HTuple count);
                if (count.I == 0)
                {
                    output.Dispose();
                    return NodeResult.Fail("手动 Region 输出为空");
                }

                SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
                SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
                ctx.AddLog(FlowLogLevel.Info, $"[手动 Region] ROI={definition.Items.Count}, 输出模式={OutputMode}, 区域数={count.I}"
                    + (matrix != null ? $"，已按 {MatrixPath} 跟随" : string.Empty));
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
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionDifferenceTool : ToolBase, INotFoundPolicy
    {
        [InputRef("区域1", typeof(HalconRegion))]
        public string RegionPath1 { get; set; }

        [InputRef("区域2", typeof(HalconRegion))]
        public string RegionPath2 { get; set; }

        public bool Connection { get; set; }
        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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

            return RegionOutput.Set(ctx, ModuleName, this, output, "Region 相减", null);
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionUnion2Tool : ToolBase, INotFoundPolicy
    {
        [InputRef("区域1", typeof(HalconRegion))]
        public string RegionPath1 { get; set; }

        [InputRef("区域2", typeof(HalconRegion))]
        public string RegionPath2 { get; set; }

        public bool Connection { get; set; }
        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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

            return RegionOutput.Set(ctx, ModuleName, this, output, "Region 合并", null);
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionIntersectionTool : ToolBase, INotFoundPolicy
    {
        [InputRef("区域1", typeof(HalconRegion))]
        public string RegionPath1 { get; set; }

        [InputRef("区域2", typeof(HalconRegion))]
        public string RegionPath2 { get; set; }

        public bool Connection { get; set; }
        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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

            return RegionOutput.Set(ctx, ModuleName, this, output, "Region 交集", null);
        }
    }

    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionShapeTransTool : ToolBase, INotFoundPolicy
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public RegionShapeTransformType Type { get; set; } = RegionShapeTransformType.convex;
        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public RegionShapeTransTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.ShapeTrans(region, out HObject output, Type.ToString());
            return RegionOutput.Set(ctx, ModuleName, this, output, "Region 形状转换", $"Type={Type}");
        }
    }

    /// <summary>
    /// Region Union1：已并入“区域处理”（Method=Union1），工具箱不再提供独立入口（TR-11），
    /// 类型保留用于加载历史流程文件。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionUnion1Tool : ToolBase, INotFoundPolicy
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public bool Connection { get; set; }
        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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

            return RegionOutput.Set(ctx, ModuleName, this, output, "Region Union1", null);
        }
    }

    /// <summary>形态学结构元素形状。</summary>
    public enum MorphologyShape
    {
        Rectangle,
        Circle
    }

    /// <summary>
    /// 形态学工具：按 Shape 选择矩形或圆形结构元素执行膨胀/腐蚀/开/闭运算，可选 connection 拆分连通域。
    /// 已并入“区域处理”（TR-11），工具箱不再提供独立入口；类型保留用于加载历史流程文件。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public class MorphologyTool : ToolBase, INotFoundPolicy
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
        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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

            return RegionOutput.Set(ctx, ModuleName, this, output, label, Operation.ToString());
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

    /// <summary>
    /// 区域灰度统计：逐区域输出最小 / 最大灰度与范围（min_max_gray），以及均值与标准差（intensity，RG-04）。
    /// 各数组与区域对象逐一对应。
    /// </summary>
    [ToolOutput("MinGrays", VariableKind.Array, VariableType.Double)]
    [ToolOutput("MaxGrays", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Ranges", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Means", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Deviations", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstMinGray", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstMaxGray", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstRange", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstMean", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstDeviation", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionMinMaxGrayTool : ToolBase, INotFoundPolicy
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public double Percent { get; set; }
        /// <summary>输入区域为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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
            var mins = new List<double>();
            var maxs = new List<double>();
            var ranges = new List<double>();
            var means = new List<double>();
            var deviations = new List<double>();
            if (regionCount.I > 0)
            {
                HOperatorSet.MinMaxGray(region, image, Percent, out HTuple minTuple, out HTuple maxTuple, out HTuple rangeTuple);
                mins.AddRange(HalconTupleConvert.ToDoubles(minTuple));
                maxs.AddRange(HalconTupleConvert.ToDoubles(maxTuple));
                ranges.AddRange(HalconTupleConvert.ToDoubles(rangeTuple));
                HOperatorSet.Intensity(region, image, out HTuple meanTuple, out HTuple deviationTuple);
                means.AddRange(HalconTupleConvert.ToDoubles(meanTuple));
                deviations.AddRange(HalconTupleConvert.ToDoubles(deviationTuple));
            }
            int count = Math.Max(mins.Count, Math.Max(maxs.Count, ranges.Count));

            SetOutput(ctx, Variable.Array(ModuleName, "MinGrays", VariableType.Double, mins));
            SetOutput(ctx, Variable.Array(ModuleName, "MaxGrays", VariableType.Double, maxs));
            SetOutput(ctx, Variable.Array(ModuleName, "Ranges", VariableType.Double, ranges));
            SetOutput(ctx, Variable.Array(ModuleName, "Means", VariableType.Double, means));
            SetOutput(ctx, Variable.Array(ModuleName, "Deviations", VariableType.Double, deviations));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstMinGray", VariableType.Double, mins.Count > 0 ? mins[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstMaxGray", VariableType.Double, maxs.Count > 0 ? maxs[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstRange", VariableType.Double, ranges.Count > 0 ? ranges[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstMean", VariableType.Double, means.Count > 0 ? means[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstDeviation", VariableType.Double, deviations.Count > 0 ? deviations[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, count > 0));
            if (count == 0)
            {
                return NotFoundOutcome.Resolve(ctx, this,
                    regionCount.I == 0 ? "Region 灰度统计输入区域为空" : "Region 灰度统计结果为空");
            }
            ctx.AddLog(FlowLogLevel.Info, $"[区域灰度统计] 区域数={regionCount.I}, 输出={count}, Percent={Percent}");
            return NodeResult.Ok;
        }
    }

    /// <summary>
    /// 特征值工具的统一输出（TR-09）：
    /// Values 按“特征优先”排列（特征 f 第 i 个对象 = Values[f * ObjectCount + i]），
    /// Features 按特征汇总（名称、数量、首值、最小/最大/平均值），供条件判断直接引用。
    /// </summary>
    public sealed class FeatureValues
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public double First { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }
        public double[] Values { get; set; }

        public static FeatureValues Create(string name, IList<double> values)
        {
            bool any = values.Count > 0;
            return new FeatureValues
            {
                Name = name,
                Count = values.Count,
                First = any ? values[0] : double.NaN,
                Min = any ? values.Min() : double.NaN,
                Max = any ? values.Max() : double.NaN,
                Mean = any ? values.Average() : double.NaN,
                Values = values.ToArray()
            };
        }

        public override string ToString()
        {
            return $"{Name}: Count={Count}, Min={Min:F3}, Max={Max:F3}, Mean={Mean:F3}";
        }
    }

    internal static class FeatureOutput
    {
        public static void Set(FlowContext ctx, string moduleName, List<FeatureValues> features, int objectCount)
        {
            var values = features.SelectMany(f => f.Values).ToList();
            var outputs = new[]
            {
                Variable.Array(moduleName, "Values", VariableType.Double, values),
                Variable.Single(moduleName, "FirstValue", VariableType.Double, values.Count > 0 ? values[0] : double.NaN),
                Variable.Single(moduleName, "Count", VariableType.Int, values.Count),
                Variable.Single(moduleName, "ObjectCount", VariableType.Int, objectCount),
                Variable.Array(moduleName, "Features", VariableType.Object, features)
            };
            foreach (Variable output in outputs)
            {
                ctx.SetVariable(output);
            }
        }
    }

    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstValue", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ObjectCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Features", VariableKind.Array, VariableType.Object, ElementClrType = typeof(FeatureValues),
        Members = new[] { "Name", "Count", "First", "Min", "Max", "Mean" })]
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
            HOperatorSet.CountObj(region, out HTuple objectCount);
            var features = new List<FeatureValues>();
            foreach (string feature in HalconTupleConvert.SplitCsv(Features))
            {
                HOperatorSet.RegionFeatures(region, feature, out HTuple tuple);
                features.Add(FeatureValues.Create(feature, HalconTupleConvert.ToDoubles(tuple).ToList()));
            }

            FeatureOutput.Set(ctx, ModuleName, features, objectCount.I);
            ctx.AddLog(FlowLogLevel.Info, $"[Region 特征值] {Features}，对象数 {objectCount.I}，输出 {features.Sum(f => f.Count)} 个数值");
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
        Leftmost,
        /// <summary>按序号取（TakeIndex，从 0 开始）。</summary>
        ByIndex
    }

    /// <summary>区域筛选依据：形状特征（select_shape）或灰度特征（select_gray）。</summary>
    public enum RegionFilterBy
    {
        Shape,
        Gray
    }

    /// <summary>多条件之间的关系（成员名即 HALCON 参数值）。</summary>
    public enum RegionSelectOperation
    {
        and,
        or
    }

    /// <summary>
    /// 区域筛选工具（参考 VisionTools.SelectShapeTool）：按特征区间过滤（形状 select_shape / 灰度 select_gray，
    /// 单条件或多条件），再按 TakeMode 取件（面积最大/最小/最左/按序号等）。输出筛选后的区域与数量。
    /// 多条件字段（Features / Mins / Maxs）任一非空即按多条件执行；都为空时按原单条件（Feature / Min / Max）执行，与旧版一致。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class SelectRegionTool : ToolBase, INotFoundPolicy, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>区域变量引用。</summary>
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>按灰度筛选时的图像；从工具箱新建时默认 Input.Image，按形状筛选时不读取。</summary>
        [InputRef("图像", typeof(HalconImage), Optional = true)]
        public string ImagePath { get; set; }

        /// <summary>筛选依据。</summary>
        public RegionFilterBy FilterBy { get; set; } = RegionFilterBy.Shape;
        /// <summary>筛选特征（形状如 area / width / circularity；灰度如 mean / deviation / max）。</summary>
        public string Feature { get; set; } = "area";
        /// <summary>特征下限。</summary>
        public double Min { get; set; } = 100;
        /// <summary>特征上限。</summary>
        public double Max { get; set; } = 99999999;
        /// <summary>多条件：特征名，逗号分隔（如 area,circularity）；非空时代替 Feature / Min / Max。</summary>
        public string Features { get; set; } = string.Empty;
        /// <summary>多条件：各特征下限，逗号分隔，个数与 Features 一致。</summary>
        public string Mins { get; set; } = string.Empty;
        /// <summary>多条件：各特征上限，逗号分隔，个数与 Features 一致。</summary>
        public string Maxs { get; set; } = string.Empty;
        /// <summary>多条件之间的关系：and 全部满足 / or 任一满足。</summary>
        public RegionSelectOperation Operation { get; set; } = RegionSelectOperation.and;
        /// <summary>多个满足条件时的取件方式。</summary>
        public RegionTakeMode TakeMode { get; set; } = RegionTakeMode.All;
        /// <summary>按序号取件时的序号（筛选结果中的第几个，从 0 开始）；越界按未找到处理。</summary>
        public int TakeIndex { get; set; }
        /// <summary>筛选结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public SelectRegionTool(string moduleName) : base(moduleName)
        {
        }

        private bool IsMultiCondition
        {
            get { return !string.IsNullOrWhiteSpace(Features) || !string.IsNullOrWhiteSpace(Mins) || !string.IsNullOrWhiteSpace(Maxs); }
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (FilterBy == RegionFilterBy.Gray && string.IsNullOrWhiteSpace(ImagePath))
            {
                yield return new ToolConfigurationIssue("图像", "按灰度筛选需要指定图像");
            }
            if (TakeMode == RegionTakeMode.ByIndex && TakeIndex < 0)
            {
                yield return new ToolConfigurationIssue(nameof(TakeIndex), "序号不能小于 0");
            }
            if (IsMultiCondition && !TryGetConditions(out _, out _, out _, out string error))
            {
                yield return new ToolConfigurationIssue("多条件", error);
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(ImagePath):
                    return FilterBy == RegionFilterBy.Gray;
                case nameof(TakeIndex):
                    return TakeMode == RegionTakeMode.ByIndex;
                default:
                    return true;
            }
        }

        /// <summary>多条件字段解析：个数必须一致且至少一个，上下限必须是数值。</summary>
        private bool TryGetConditions(out string[] features, out double[] mins, out double[] maxs, out string error)
        {
            features = HalconTupleConvert.SplitCsv(Features);
            string[] minTexts = HalconTupleConvert.SplitCsv(Mins);
            string[] maxTexts = HalconTupleConvert.SplitCsv(Maxs);
            mins = null;
            maxs = null;
            if (features.Length == 0 || features.Length != minTexts.Length || features.Length != maxTexts.Length)
            {
                error = $"特征、下限、上限的个数必须一致且不为 0（特征 {features.Length} 个、下限 {minTexts.Length} 个、上限 {maxTexts.Length} 个）";
                return false;
            }
            mins = new double[features.Length];
            maxs = new double[features.Length];
            for (int i = 0; i < features.Length; i++)
            {
                if (!double.TryParse(minTexts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out mins[i]))
                {
                    error = $"下限第 {i + 1} 项“{minTexts[i]}”不是数值";
                    return false;
                }
                if (!double.TryParse(maxTexts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out maxs[i]))
                {
                    error = $"上限第 {i + 1} 项“{maxTexts[i]}”不是数值";
                    return false;
                }
            }
            error = null;
            return true;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject input = Input<HalconRegion>(ctx, RegionPath).Object;
            HTuple features;
            HTuple operation;
            HTuple mins;
            HTuple maxs;
            string condition;
            if (IsMultiCondition)
            {
                TryGetConditions(out string[] names, out double[] minValues, out double[] maxValues, out _);
                features = new HTuple(names);
                operation = new HTuple(Operation.ToString());
                mins = new HTuple(minValues);
                maxs = new HTuple(maxValues);
                condition = string.Join($" {Operation} ", names.Select((n, i) => $"{n} ∈ [{minValues[i]}, {maxValues[i]}]"));
            }
            else
            {
                features = new HTuple(Feature);
                operation = new HTuple("and");
                mins = new HTuple(Min);
                maxs = new HTuple(Max);
                condition = $"{Feature} ∈ [{Min}, {Max}]";
            }

            HObject selected;
            if (FilterBy == RegionFilterBy.Gray)
            {
                HObject image = Input<HalconImage>(ctx, ImagePath).Object;
                HOperatorSet.SelectGray(input, image, out selected, features, operation, mins, maxs);
                condition = "灰度 " + condition;
            }
            else
            {
                HOperatorSet.SelectShape(input, out selected, features, operation, mins, maxs);
            }
            HOperatorSet.CountObj(selected, out HTuple count);

            HObject result = selected;
            string take = TakeMode.ToString();
            if (TakeMode == RegionTakeMode.ByIndex)
            {
                take = $"第 {TakeIndex} 个（共 {count.I} 个）";
                if (TakeIndex < count.I)
                {
                    HOperatorSet.SelectObj(selected, out result, TakeIndex + 1);
                }
                else
                {
                    HOperatorSet.GenEmptyObj(out result);
                }
                selected.Dispose();
            }
            else if (TakeMode != RegionTakeMode.All && count.I > 1)
            {
                int pick = PickIndex(selected, count.I);
                HOperatorSet.SelectObj(selected, out result, pick + 1);
                selected.Dispose();
            }

            return RegionOutput.Set(ctx, ModuleName, this, result, "区域筛选", $"{condition}，取 {take}");
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
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionPoseTool : ToolBase, INotFoundPolicy
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

        /// <summary>输入区域为空时是否失败（默认 true）；关闭后输出 Found=false、矩阵为 null 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public RegionPoseTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.CountObj(region, out HTuple regionCount);
            if (regionCount.I == 0)
            {
                // 未定位到区域：显式清空输出，避免循环中下游读到上一轮的位姿
                SetOutput(ctx, Variable.Single(ModuleName, "CenterRow", VariableType.Double, double.NaN));
                SetOutput(ctx, Variable.Single(ModuleName, "CenterColumn", VariableType.Double, double.NaN));
                SetOutput(ctx, Variable.Single(ModuleName, "Angle", VariableType.Double, double.NaN));
                SetOutput(ctx, Variable.Single(ModuleName, "Area", VariableType.Double, 0.0));
                SetOutput(ctx, Variable.Object<HomMat2D>(ModuleName, "Matrix", null, 0));
                SetOutput(ctx, Variable.Object<HomMat2D>(ModuleName, "BaseToCurrentMatrix", null, 0));
                SetOutput(ctx, Variable.Object<HomMat2D>(ModuleName, "CurrentToBaseMatrix", null, 0));
                SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, false));
                return NotFoundOutcome.Resolve(ctx, this, "区域定位输入区域为空（请检查上游分割/筛选）");
            }

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
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, true));

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
