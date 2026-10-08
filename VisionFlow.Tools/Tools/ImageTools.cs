using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>图像运算（按数字保存，新值追加在末尾；Add / Sub 为原“图像加减”）。</summary>
    public enum ImageArithmeticOperation
    {
        Add,
        Sub,
        /// <summary>mult_image：g1 × g2 × Multi + Add。</summary>
        Mult,
        /// <summary>div_image：g1 / g2 × Multi + Add（除数为 0 的像素为 0）。</summary>
        Div,
        /// <summary>abs_diff_image：|g1 − g2| × Multi。</summary>
        AbsDiff,
        /// <summary>max_image：逐像素取较大值。</summary>
        Max,
        /// <summary>min_image：逐像素取较小值。</summary>
        Min
    }

    /// <summary>trans_from_rgb 的目标色彩空间（名称即 HALCON 参数值，追加时保持已有值不变以兼容已保存流程）。</summary>
    public enum ColorTransformSpace
    {
        hsv,
        hls,
        yuv,
        i1i2i3,
        yiq,
        argyb,
        ciexyz,
        ihs,
        hsi,
        cielab,
        cieluv,
        cielchab,
        cielchuv,
        lms
    }

    public enum ImageInterpolationMode
    {
        constant,
        nearest_neighbor,
        bilinear
    }

    /// <summary>图像滤波方式（按数字保存；Mean 在首位即默认，等于旧版“均值滤波”）。</summary>
    public enum ImageFilterMethod
    {
        Mean,
        Gauss,
        Median,
        Smooth,
        Bilateral,
        Emphasize,
        SobelAmp,
        GrayErosion,
        GrayDilation,
        GrayOpening,
        GrayClosing
    }

    /// <summary>gauss_filter 的 Size（22.11 只接受 3 / 5 / 7 / 9 / 11），按数字保存即尺寸本身。</summary>
    public enum GaussFilterSize
    {
        Size3 = 3,
        Size5 = 5,
        Size7 = 7,
        Size9 = 9,
        Size11 = 11
    }

    /// <summary>median_image 的 MaskType（名称即 HALCON 参数值）。</summary>
    public enum MedianMaskType
    {
        circle,
        square
    }

    /// <summary>median_image 的 Margin 字符串取值（名称即 HALCON 参数值）；灰度常数边界暂不提供。</summary>
    public enum MedianMargin
    {
        mirrored,
        cyclic,
        continued
    }

    /// <summary>smooth_image 的 Filter（名称即 HALCON 参数值）。</summary>
    public enum SmoothFilterType
    {
        deriche1,
        deriche2,
        shen,
        gauss
    }

    /// <summary>sobel_amp 的 FilterType 全集（名称即 HALCON 参数值，顺序同 get_param_info 的 value_list）。</summary>
    public enum SobelFilterType
    {
        sum_abs,
        thin_sum_abs,
        thin_max_abs,
        sum_sqrt,
        x,
        y,
        sum_abs_binomial,
        thin_sum_abs_binomial,
        thin_max_abs_binomial,
        sum_sqrt_binomial,
        x_binomial,
        y_binomial
    }

    /// <summary>sobel_amp 的 Size（value_list 列到 39，22.11 实测只接受 3 ~ 13 的奇数），按数字保存即尺寸本身。</summary>
    public enum SobelFilterSize
    {
        Size3 = 3,
        Size5 = 5,
        Size7 = 7,
        Size9 = 9,
        Size11 = 11,
        Size13 = 13
    }

    /// <summary>
    /// 图像滤波（工具箱原名“均值滤波”，ID mean-image 与类型名不变）。Mean 方式与旧版逐字相同；
    /// 其余方式的参数约束、通道与像素类型要求见 IMAGE-TOOLS-PLAN 第 10 节（22.11 实测）。
    /// 灰度形态学 gray_*_rect 的参数顺序为（高, 宽），与 mean_image / emphasize（宽, 高）相反。
    /// </summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class MeanImageTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        private static readonly string[] AllTypes = { "byte", "uint2", "int2", "real" };
        private static readonly string[] NoInt2 = { "byte", "uint2", "real" };
        private static readonly string[] NoReal = { "byte", "uint2", "int2" };

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>滤波方式。</summary>
        public ImageFilterMethod Method { get; set; } = ImageFilterMethod.Mean;
        /// <summary>掩膜宽（Mean / Emphasize / 灰度形态学）。</summary>
        public int Width { get; set; } = 9;
        /// <summary>掩膜高（Mean / Emphasize / 灰度形态学）。</summary>
        public int Height { get; set; } = 9;
        /// <summary>高斯滤波尺寸。</summary>
        public GaussFilterSize GaussSize { get; set; } = GaussFilterSize.Size5;
        /// <summary>中值滤波掩膜形状。</summary>
        public MedianMaskType MaskType { get; set; } = MedianMaskType.circle;
        /// <summary>中值滤波半径（≥ 1，上限约为图像短边的一半）。</summary>
        public int Radius { get; set; } = 1;
        /// <summary>中值滤波边界处理。</summary>
        public MedianMargin Margin { get; set; } = MedianMargin.mirrored;
        /// <summary>平滑滤波器。</summary>
        public SmoothFilterType SmoothFilter { get; set; } = SmoothFilterType.deriche2;
        /// <summary>平滑系数（> 0）。</summary>
        public double Alpha { get; set; } = 0.5;
        /// <summary>双边滤波空间标准差（≥ 0.6）。</summary>
        public double SigmaSpatial { get; set; } = 3;
        /// <summary>双边滤波灰度标准差（> 0）。</summary>
        public double SigmaRange { get; set; } = 20;
        /// <summary>锐化系数（≥ 0）。</summary>
        public double Factor { get; set; } = 1;
        /// <summary>Sobel 滤波类型。</summary>
        public SobelFilterType SobelType { get; set; } = SobelFilterType.sum_abs;
        /// <summary>Sobel 滤波尺寸。</summary>
        public SobelFilterSize SobelSize { get; set; } = SobelFilterSize.Size3;

        public MeanImageTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            switch (Method)
            {
                case ImageFilterMethod.Mean:
                    if (Width <= 0 || Height <= 0)
                    {
                        yield return new ToolConfigurationIssue("Width / Height", "均值滤波核宽高必须大于 0");
                    }
                    break;
                case ImageFilterMethod.Gauss:
                    if (!Enum.IsDefined(typeof(GaussFilterSize), GaussSize))
                    {
                        yield return new ToolConfigurationIssue(nameof(GaussSize), "高斯滤波尺寸只能是 3、5、7、9、11");
                    }
                    break;
                case ImageFilterMethod.Median:
                    if (!Enum.IsDefined(typeof(MedianMaskType), MaskType))
                    {
                        yield return new ToolConfigurationIssue(nameof(MaskType), "中值滤波掩膜形状只能是 circle / square");
                    }
                    if (Radius < 1)
                    {
                        yield return new ToolConfigurationIssue(nameof(Radius), "中值滤波半径必须不小于 1");
                    }
                    if (!Enum.IsDefined(typeof(MedianMargin), Margin))
                    {
                        yield return new ToolConfigurationIssue(nameof(Margin), "中值滤波边界处理只能是 mirrored / cyclic / continued");
                    }
                    break;
                case ImageFilterMethod.Smooth:
                    if (!Enum.IsDefined(typeof(SmoothFilterType), SmoothFilter))
                    {
                        yield return new ToolConfigurationIssue(nameof(SmoothFilter), "平滑滤波器只能是 deriche1 / deriche2 / shen / gauss");
                    }
                    if (!(Alpha > 0) || double.IsInfinity(Alpha))
                    {
                        yield return new ToolConfigurationIssue(nameof(Alpha), "平滑系数 Alpha 必须大于 0");
                    }
                    break;
                case ImageFilterMethod.Bilateral:
                    if (!(SigmaSpatial >= 0.6) || double.IsInfinity(SigmaSpatial))
                    {
                        yield return new ToolConfigurationIssue(nameof(SigmaSpatial), "双边滤波空间标准差 SigmaSpatial 必须不小于 0.6");
                    }
                    if (!(SigmaRange > 0) || double.IsInfinity(SigmaRange))
                    {
                        yield return new ToolConfigurationIssue(nameof(SigmaRange), "双边滤波灰度标准差 SigmaRange 必须大于 0");
                    }
                    break;
                case ImageFilterMethod.Emphasize:
                    if (Width < 3 || Height < 3)
                    {
                        yield return new ToolConfigurationIssue("Width / Height", "锐化掩膜宽高必须不小于 3");
                    }
                    if (!(Factor >= 0) || double.IsInfinity(Factor))
                    {
                        yield return new ToolConfigurationIssue(nameof(Factor), "锐化系数 Factor 必须不小于 0");
                    }
                    break;
                case ImageFilterMethod.SobelAmp:
                    if (!Enum.IsDefined(typeof(SobelFilterType), SobelType))
                    {
                        yield return new ToolConfigurationIssue(nameof(SobelType), "Sobel 滤波类型无效");
                    }
                    if (!Enum.IsDefined(typeof(SobelFilterSize), SobelSize))
                    {
                        yield return new ToolConfigurationIssue(nameof(SobelSize), "Sobel 滤波尺寸只能是 3、5、7、9、11、13");
                    }
                    break;
                case ImageFilterMethod.GrayErosion:
                case ImageFilterMethod.GrayDilation:
                case ImageFilterMethod.GrayOpening:
                case ImageFilterMethod.GrayClosing:
                    if (Width < 1 || Height < 1)
                    {
                        yield return new ToolConfigurationIssue("Width / Height", "灰度形态学掩膜宽高必须大于 0");
                    }
                    break;
                default:
                    yield return new ToolConfigurationIssue(nameof(Method), $"未知的滤波方式 {(int)Method}");
                    break;
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(Width):
                case nameof(Height):
                    return Method == ImageFilterMethod.Mean || Method == ImageFilterMethod.Emphasize || IsGrayMorphology;
                case nameof(GaussSize):
                    return Method == ImageFilterMethod.Gauss;
                case nameof(MaskType):
                case nameof(Radius):
                case nameof(Margin):
                    return Method == ImageFilterMethod.Median;
                case nameof(SmoothFilter):
                case nameof(Alpha):
                    return Method == ImageFilterMethod.Smooth;
                case nameof(SigmaSpatial):
                case nameof(SigmaRange):
                    return Method == ImageFilterMethod.Bilateral;
                case nameof(Factor):
                    return Method == ImageFilterMethod.Emphasize;
                case nameof(SobelType):
                case nameof(SobelSize):
                    return Method == ImageFilterMethod.SobelAmp;
                default:
                    return true;
            }
        }

        private bool IsGrayMorphology => Method == ImageFilterMethod.GrayErosion || Method == ImageFilterMethod.GrayDilation
            || Method == ImageFilterMethod.GrayOpening || Method == ImageFilterMethod.GrayClosing;

        public override NodeResult Run(FlowContext ctx)
        {
            if (Method == ImageFilterMethod.Mean)
            {
                return RunMean(ctx);
            }
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            string typeError = ImageInputRequirements.CheckType(image, SupportedTypes(), $"图像滤波 {Method} 方式");
            if (typeError != null)
            {
                return NodeResult.Fail($"{ModuleName} {typeError}");
            }
            HObject output;
            string detail;
            try
            {
                output = Filter(image, out detail);
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 图像滤波 {Method} 失败：{DescribeError(image, ex)}");
            }
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, $"[图像滤波] Method={Method}, {detail}");
            return NodeResult.Ok;
        }

        /// <summary>旧版均值滤波，错误信息与日志逐字不变。</summary>
        private NodeResult RunMean(FlowContext ctx)
        {
            if (Width <= 0 || Height <= 0)
            {
                return NodeResult.Fail("均值滤波核宽高必须大于 0");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.MeanImage(image, out HObject output, Width, Height);
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, $"[均值滤波] Width={Width}, Height={Height}");
            return NodeResult.Ok;
        }

        private string[] SupportedTypes()
        {
            switch (Method)
            {
                case ImageFilterMethod.Smooth:
                case ImageFilterMethod.Bilateral:
                    return NoInt2;
                case ImageFilterMethod.Emphasize:
                    return NoReal;
                default:
                    return AllTypes;
            }
        }

        private HObject Filter(HObject image, out string detail)
        {
            HObject output;
            switch (Method)
            {
                case ImageFilterMethod.Gauss:
                    HOperatorSet.GaussFilter(image, out output, (int)GaussSize);
                    detail = $"Size={(int)GaussSize}";
                    break;
                case ImageFilterMethod.Median:
                    HOperatorSet.MedianImage(image, out output, MaskType.ToString(), Radius, Margin.ToString());
                    detail = $"MaskType={MaskType}, Radius={Radius}, Margin={Margin}";
                    break;
                case ImageFilterMethod.Smooth:
                    HOperatorSet.SmoothImage(image, out output, SmoothFilter.ToString(), Alpha);
                    detail = $"Filter={SmoothFilter}, Alpha={Alpha}";
                    break;
                case ImageFilterMethod.Bilateral:
                    HOperatorSet.BilateralFilter(image, image, out output, SigmaSpatial, SigmaRange, new HTuple(), new HTuple());
                    detail = $"SigmaSpatial={SigmaSpatial}, SigmaRange={SigmaRange}";
                    break;
                case ImageFilterMethod.Emphasize:
                    HOperatorSet.Emphasize(image, out output, Width, Height, Factor);
                    detail = $"Width={Width}, Height={Height}, Factor={Factor}";
                    break;
                case ImageFilterMethod.SobelAmp:
                    HOperatorSet.SobelAmp(image, out output, SobelType.ToString(), (int)SobelSize);
                    detail = $"FilterType={SobelType}, Size={(int)SobelSize}";
                    break;
                case ImageFilterMethod.GrayErosion:
                    HOperatorSet.GrayErosionRect(image, out output, Height, Width);
                    detail = $"Width={Width}, Height={Height}";
                    break;
                case ImageFilterMethod.GrayDilation:
                    HOperatorSet.GrayDilationRect(image, out output, Height, Width);
                    detail = $"Width={Width}, Height={Height}";
                    break;
                case ImageFilterMethod.GrayOpening:
                    HOperatorSet.GrayOpeningRect(image, out output, Height, Width);
                    detail = $"Width={Width}, Height={Height}";
                    break;
                case ImageFilterMethod.GrayClosing:
                    HOperatorSet.GrayClosingRect(image, out output, Height, Width);
                    detail = $"Width={Width}, Height={Height}";
                    break;
                default:
                    throw new InvalidOperationException($"未知的滤波方式 {(int)Method}");
            }
            return output;
        }

        /// <summary>随图像尺寸变化的上限只能在运行时发现，转成带图像尺寸的中文说明。</summary>
        private string DescribeError(HObject image, HalconException ex)
        {
            string size = ImageInputRequirements.SizeText(image);
            int code = ex.GetErrorCode();
            if (Method == ImageFilterMethod.Median && code == 1302)
            {
                return $"中值滤波半径 {Radius} 超出图像允许的范围（图像 {size}，半径须小于短边的一半左右）";
            }
            if (code == 3033)
            {
                return Method == ImageFilterMethod.Bilateral
                    ? $"空间标准差 SigmaSpatial={SigmaSpatial} 对应的滤波尺寸超过图像尺寸（图像 {size}）"
                    : $"滤波掩膜尺寸超过图像尺寸（图像 {size}）";
            }
            return ex.Message;
        }
    }

    /// <summary>图像工具对通道数与像素类型的前置检查（IMAGE-TOOLS-PLAN 第 10.2 节）。</summary>
    internal static class ImageInputRequirements
    {
        /// <summary>像素类型不在支持列表时返回中文错误（含支持的类型与转换建议），否则返回 null。</summary>
        public static string CheckType(HObject image, string[] supportedTypes, string what)
        {
            HOperatorSet.GetImageType(image, out HTuple type);
            string actual = type.Length > 0 ? type[0].S : string.Empty;
            return supportedTypes.Contains(actual)
                ? null
                : $"{what}不支持 {actual} 图像（支持 {string.Join(" / ", supportedTypes)}），可先接灰度增强的 ConvertType 转换像素类型";
        }

        public static int Channels(HObject image)
        {
            HOperatorSet.CountChannels(image, out HTuple channels);
            return channels.Length > 0 ? channels[0].I : 0;
        }

        public static string SizeText(HObject image)
        {
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            return width.Length > 0 ? $"{width[0].I}×{height[0].I}" : "未知";
        }
    }

    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class AffineTransformImageTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("变换矩阵", typeof(HomMat2D))]
        public string MatrixPath { get; set; }

        public ImageInterpolationMode Interpolation { get; set; } = ImageInterpolationMode.constant;
        public bool AdaptImageSize { get; set; }

        public AffineTransformImageTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HomMat2D matrix = VariableReference.Parse(MatrixPath).Resolve<HomMat2D>(ctx);
            HOperatorSet.AffineTransImage(image, out HObject output, matrix.Data, Interpolation.ToString(),
                AdaptImageSize ? "true" : "false");

            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, $"[图像仿射变换] Interpolation={Interpolation}, AdaptImageSize={AdaptImageSize}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class ReduceDomainTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public ReduceDomainTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.ReduceDomain(image, region, out HObject output);

            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, "[ReduceDomain] 已按 Region 限定图像域");
            return NodeResult.Ok;
        }
    }

    /// <summary>
    /// 图像运算（工具箱原名“图像加减”，ID add-sub-image 与类型名不变）。Add / Sub 的成功路径、输出与日志不变；
    /// 七种运算都在调用算子前检查两图宽、高、通道数（IMAGE-TOOLS-PLAN 第 12 节）。byte 与 byte 运算的结果仍为 byte，出界饱和截断。
    /// </summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class AddSubImageTool : ToolBase, IToolParameterVisibility
    {
        [InputRef("图像1", typeof(HalconImage))]
        public string ImagePath1 { get; set; } = "Input.Image";

        [InputRef("图像2", typeof(HalconImage))]
        public string ImagePath2 { get; set; }

        public ImageArithmeticOperation Operation { get; set; } = ImageArithmeticOperation.Add;
        /// <summary>乘数（Add / Sub / Mult / Div / AbsDiff）。</summary>
        public double Multi { get; set; } = 1;
        /// <summary>加数（Add / Sub / Mult / Div；AbsDiff 没有加数）。</summary>
        public int Add { get; set; } = 128;

        public AddSubImageTool(string moduleName) : base(moduleName)
        {
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(Multi):
                    return Operation != ImageArithmeticOperation.Max && Operation != ImageArithmeticOperation.Min;
                case nameof(Add):
                    return Operation != ImageArithmeticOperation.Max && Operation != ImageArithmeticOperation.Min
                        && Operation != ImageArithmeticOperation.AbsDiff;
                default:
                    return true;
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image1 = Input<HalconImage>(ctx, ImagePath1).Object;
            HObject image2 = Input<HalconImage>(ctx, ImagePath2).Object;
            string mismatch = DescribeMismatch(image1, image2);
            if (mismatch != null)
            {
                return NodeResult.Fail($"{ModuleName} {mismatch}");
            }
            HObject output;
            try
            {
                switch (Operation)
                {
                    case ImageArithmeticOperation.Add:
                        HOperatorSet.AddImage(image1, image2, out output, Multi, Add);
                        break;
                    case ImageArithmeticOperation.Sub:
                        HOperatorSet.SubImage(image1, image2, out output, Multi, Add);
                        break;
                    case ImageArithmeticOperation.Mult:
                        HOperatorSet.MultImage(image1, image2, out output, Multi, Add);
                        break;
                    case ImageArithmeticOperation.Div:
                        HOperatorSet.DivImage(image1, image2, out output, Multi, Add);
                        break;
                    case ImageArithmeticOperation.AbsDiff:
                        HOperatorSet.AbsDiffImage(image1, image2, out output, Multi);
                        break;
                    case ImageArithmeticOperation.Max:
                        HOperatorSet.MaxImage(image1, image2, out output);
                        break;
                    case ImageArithmeticOperation.Min:
                        HOperatorSet.MinImage(image1, image2, out output);
                        break;
                    default:
                        return NodeResult.Fail($"{ModuleName} 未知的图像运算 {(int)Operation}");
                }
            }
            catch (HalconException ex) when (ex.GetErrorCode() == 3117 || ex.GetErrorCode() == 3122 || ex.GetErrorCode() == 9001)
            {
                return NodeResult.Fail($"{ModuleName} {DescribeMismatch(image1, image2) ?? DescribeTypes(image1, image2)}");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            if (Operation == ImageArithmeticOperation.Add || Operation == ImageArithmeticOperation.Sub)
            {
                ctx.AddLog(FlowLogLevel.Info, $"[图像加减] {Operation}, Multi={Multi}, Add={Add}");
                return NodeResult.Ok;
            }
            string parameters = Operation == ImageArithmeticOperation.Max || Operation == ImageArithmeticOperation.Min ? string.Empty
                : Operation == ImageArithmeticOperation.AbsDiff ? $", Multi={Multi}" : $", Multi={Multi}, Add={Add}";
            HOperatorSet.GetImageType(output, out HTuple outputType);
            string type = outputType.Length > 0 ? outputType[0].S : string.Empty;
            string clipNote = type == "byte" && (Operation == ImageArithmeticOperation.Mult || Operation == ImageArithmeticOperation.Div || Operation == ImageArithmeticOperation.AbsDiff)
                ? "；输出 byte，超出 0 ~ 255 的部分被截断，需要完整范围时配合 Multi / Add，或先接灰度增强的 ConvertType 转成 real"
                : $"；输出 {type}";
            ctx.AddLog(FlowLogLevel.Info, $"[图像运算] {Operation}{parameters}{clipNote}");
            return NodeResult.Ok;
        }

        /// <summary>两图宽、高、通道数不一致时返回“宽×高×通道”对照的中文说明，否则返回 null。</summary>
        private static string DescribeMismatch(HObject image1, HObject image2)
        {
            string shape1 = Shape(image1), shape2 = Shape(image2);
            return shape1 == shape2 ? null : $"两张图像的尺寸或通道数不一致：图像1 {shape1}，图像2 {shape2}（宽×高×通道）";
        }

        private static string Shape(HObject image)
        {
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CountChannels(image, out HTuple channels);
            return width.Length > 0 && channels.Length > 0 ? $"{width[0].I}×{height[0].I}×{channels[0].I}" : "空图像";
        }

        private static string DescribeTypes(HObject image1, HObject image2)
        {
            HOperatorSet.GetImageType(image1, out HTuple type1);
            HOperatorSet.GetImageType(image2, out HTuple type2);
            return $"两张图像的像素类型不同或不受支持：图像1 {(type1.Length > 0 ? type1[0].S : "?")}，图像2 {(type2.Length > 0 ? type2[0].S : "?")}，可先接灰度增强的 ConvertType 统一类型";
        }
    }

    /// <summary>
    /// 通道分解：Channel1~3 始终写出——图像不足 3 通道时，缺少的通道输出为 null 并记录警告，
    /// 下游引用会得到明确的空值错误，而不是“找不到变量”或上一轮的旧值（TR-14）。
    /// Index 超出实际通道数时失败，不再静默截断。
    /// </summary>
    [ToolOutput("Channel1", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Channel2", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Channel3", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("SelectedImage", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("ChannelCount", VariableKind.Single, VariableType.Int)]
    public sealed class DecomposeChannelsTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>选择输出到 SelectedImage 的通道（1 起，须不超过实际通道数）。</summary>
        public int Index { get; set; } = 1;

        public DecomposeChannelsTool(string moduleName) : base(moduleName)
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
            if (Index < 1 || Index > count)
            {
                return NodeResult.Fail($"通道分解 Index={Index} 超出范围：图像共有 {count} 个通道");
            }

            HObject selected = null;
            for (int i = 1; i <= 3; i++)
            {
                if (i > count)
                {
                    SetOutput(ctx, Variable.Object<HalconImage>(ModuleName, "Channel" + i, null, 0));
                    continue;
                }
                HOperatorSet.AccessChannel(image, out HObject channel, i);
                SetOutput(ctx, Variable.Object(ModuleName, "Channel" + i, new HalconImage(channel), 1));
                if (i == Index)
                {
                    selected = channel;
                }
            }
            if (count < 3)
            {
                ctx.AddLog(FlowLogLevel.Warning, $"[通道分解] 图像只有 {count} 个通道，Channel{count + 1}~Channel3 输出为空");
            }

            if (selected == null)
            {
                HOperatorSet.AccessChannel(image, out selected, Index);
            }

            SetOutput(ctx, Variable.Object(ModuleName, "SelectedImage", new HalconImage(selected), 1));
            SetOutput(ctx, Variable.Single(ModuleName, "ChannelCount", VariableType.Int, count));
            ctx.AddLog(FlowLogLevel.Info, $"[通道分解] 通道数={count}, 选择={Index}");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class Compose3ImageTool : ToolBase
    {
        [InputRef("通道1", typeof(HalconImage))]
        public string Channel1Path { get; set; }

        [InputRef("通道2", typeof(HalconImage))]
        public string Channel2Path { get; set; }

        [InputRef("通道3", typeof(HalconImage))]
        public string Channel3Path { get; set; }

        public Compose3ImageTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject channel1 = Input<HalconImage>(ctx, Channel1Path).Object;
            HObject channel2 = Input<HalconImage>(ctx, Channel2Path).Object;
            HObject channel3 = Input<HalconImage>(ctx, Channel3Path).Object;
            HOperatorSet.Compose3(channel1, channel2, channel3, out HObject output);
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, "[三通道合成] 完成");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Channel1", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Channel2", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Channel3", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("SelectedImage", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class TransColorSpaceTool : ToolBase
    {
        [InputRef("R 通道", typeof(HalconImage))]
        public string Channel1Path { get; set; }

        [InputRef("G 通道", typeof(HalconImage))]
        public string Channel2Path { get; set; }

        [InputRef("B 通道", typeof(HalconImage))]
        public string Channel3Path { get; set; }

        public ColorTransformSpace ColorType { get; set; } = ColorTransformSpace.hsv;
        public int Index { get; set; } = 1;

        public TransColorSpaceTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (Index < 1 || Index > 3)
            {
                return NodeResult.Fail($"色彩转换 Index={Index} 超出范围（1~3）");
            }
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
            ctx.AddLog(FlowLogLevel.Info, $"[色彩转换] {ColorType}, 选择通道={Index}");
            return NodeResult.Ok;
        }
    }
}
