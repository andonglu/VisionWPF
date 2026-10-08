using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>灰度增强方式（按数字保存；Linear 在首位即默认）。</summary>
    public enum GrayEnhanceMethod
    {
        Linear,
        AutoStretch,
        PercentStretch,
        EquHisto,
        Invert,
        Gamma,
        Illuminate,
        RgbToGray,
        ConvertType
    }

    /// <summary>convert_image_type 的目标类型（名称即 HALCON 参数值；其余类型需要时追加在末尾）。</summary>
    public enum ConvertImageNewType
    {
        @byte,
        uint2,
        int2,
        real
    }

    /// <summary>
    /// 灰度增强（IP-03）：一张图进、一张图出，只改变灰度映射。各方式的参数约束、通道与像素类型要求见
    /// IMAGE-TOOLS-PLAN 第 10 节（22.11 实测）：PercentStretch 只接受单通道（min_max_gray 对彩色图只统计第一通道），
    /// RgbToGray 只接受三通道（rgb1_to_gray 对单通道不报错、原样输出），其余方式逐通道处理。输出均为新生成图像。
    /// </summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class GrayEnhanceTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        private static readonly string[] AllTypes = { "byte", "uint2", "int2", "real" };

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>增强方式。</summary>
        public GrayEnhanceMethod Method { get; set; } = GrayEnhanceMethod.Linear;
        /// <summary>线性变换乘数（g' = g × Mult + Add）。</summary>
        public double Mult { get; set; } = 1;
        /// <summary>线性变换加数。</summary>
        public double Add { get; set; }
        /// <summary>百分比拉伸时两端各舍弃的百分比（0 ~ 50）。</summary>
        public double Percent { get; set; } = 1;
        /// <summary>伽马指数（> 0）；默认值与 gamma_image 相同（sRGB 编码）。</summary>
        public double Gamma { get; set; } = 0.416666666667;
        /// <summary>伽马曲线的偏移（≥ 0）。</summary>
        public double Offset { get; set; } = 0.055;
        /// <summary>伽马曲线线性段的阈值（≥ 0）。</summary>
        public double Threshold { get; set; } = 0.0031308;
        /// <summary>伽马计算的最大灰度（> 0）。</summary>
        public double MaxGray { get; set; } = 255;
        /// <summary>伽马编码（true）或解码（false），对应 gamma_image 的 'true' / 'false'。</summary>
        public bool Encode { get; set; } = true;
        /// <summary>光照校正掩膜宽（≥ 1）。</summary>
        public int MaskWidth { get; set; } = 101;
        /// <summary>光照校正掩膜高（≥ 1）。</summary>
        public int MaskHeight { get; set; } = 101;
        /// <summary>光照校正强度（≥ 0）。</summary>
        public double Factor { get; set; } = 0.7;
        /// <summary>像素类型转换的目标类型。</summary>
        public ConvertImageNewType NewType { get; set; } = ConvertImageNewType.@byte;

        public GrayEnhanceTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            switch (Method)
            {
                case GrayEnhanceMethod.Linear:
                    if (!double.IsFinite(Mult) || !double.IsFinite(Add))
                    {
                        yield return new ToolConfigurationIssue("Mult / Add", "线性变换的 Mult / Add 必须是有限数");
                    }
                    break;
                case GrayEnhanceMethod.PercentStretch:
                    if (!(Percent >= 0 && Percent <= 50))
                    {
                        yield return new ToolConfigurationIssue(nameof(Percent), "百分比拉伸的 Percent 必须在 0 ~ 50 之间");
                    }
                    break;
                case GrayEnhanceMethod.Gamma:
                    if (!(Gamma > 0) || double.IsInfinity(Gamma))
                    {
                        yield return new ToolConfigurationIssue(nameof(Gamma), "伽马指数 Gamma 必须大于 0");
                    }
                    if (!(Offset >= 0) || double.IsInfinity(Offset))
                    {
                        yield return new ToolConfigurationIssue(nameof(Offset), "伽马偏移 Offset 必须不小于 0");
                    }
                    if (!(Threshold >= 0) || double.IsInfinity(Threshold))
                    {
                        yield return new ToolConfigurationIssue(nameof(Threshold), "伽马阈值 Threshold 必须不小于 0");
                    }
                    if (!(MaxGray > 0) || double.IsInfinity(MaxGray))
                    {
                        yield return new ToolConfigurationIssue(nameof(MaxGray), "伽马最大灰度 MaxGray 必须大于 0");
                    }
                    break;
                case GrayEnhanceMethod.Illuminate:
                    if (MaskWidth < 1 || MaskHeight < 1)
                    {
                        yield return new ToolConfigurationIssue("MaskWidth / MaskHeight", "光照校正掩膜宽高必须大于 0");
                    }
                    if (!(Factor >= 0) || double.IsInfinity(Factor))
                    {
                        yield return new ToolConfigurationIssue(nameof(Factor), "光照校正强度 Factor 必须不小于 0");
                    }
                    break;
                case GrayEnhanceMethod.ConvertType:
                    if (!Enum.IsDefined(typeof(ConvertImageNewType), NewType))
                    {
                        yield return new ToolConfigurationIssue(nameof(NewType), "目标像素类型只能是 byte / uint2 / int2 / real");
                    }
                    break;
                case GrayEnhanceMethod.AutoStretch:
                case GrayEnhanceMethod.EquHisto:
                case GrayEnhanceMethod.Invert:
                case GrayEnhanceMethod.RgbToGray:
                    break;
                default:
                    yield return new ToolConfigurationIssue(nameof(Method), $"未知的增强方式 {(int)Method}");
                    break;
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(Mult):
                case nameof(Add):
                    return Method == GrayEnhanceMethod.Linear;
                case nameof(Percent):
                    return Method == GrayEnhanceMethod.PercentStretch;
                case nameof(Gamma):
                case nameof(Offset):
                case nameof(Threshold):
                case nameof(MaxGray):
                case nameof(Encode):
                    return Method == GrayEnhanceMethod.Gamma;
                case nameof(MaskWidth):
                case nameof(MaskHeight):
                case nameof(Factor):
                    return Method == GrayEnhanceMethod.Illuminate;
                case nameof(NewType):
                    return Method == GrayEnhanceMethod.ConvertType;
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
            string inputError = CheckInput(image);
            if (inputError != null)
            {
                return NodeResult.Fail($"{ModuleName} {inputError}");
            }
            HObject output;
            string detail;
            try
            {
                output = Enhance(ctx, image, out detail, out string error);
                if (output == null)
                {
                    return NodeResult.Fail($"{ModuleName} {error}");
                }
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 灰度增强 {Method} 失败：{DescribeError(image, ex)}");
            }
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
            ctx.AddLog(FlowLogLevel.Info, $"[灰度增强] Method={Method}{(detail.Length == 0 ? string.Empty : ", " + detail)}");
            return NodeResult.Ok;
        }

        /// <summary>通道数与像素类型的前置要求（第 10.2 节）。</summary>
        private string CheckInput(HObject image)
        {
            if (Method == GrayEnhanceMethod.RgbToGray)
            {
                int channels = ImageInputRequirements.Channels(image);
                if (channels != 3)
                {
                    return $"灰度增强 RgbToGray 需要三通道彩色图像（当前 {channels} 通道）";
                }
            }
            if (Method == GrayEnhanceMethod.PercentStretch)
            {
                int channels = ImageInputRequirements.Channels(image);
                if (channels != 1)
                {
                    return $"灰度增强 PercentStretch 只接受单通道图像（当前 {channels} 通道；min_max_gray 对彩色图只统计第一通道），请先接灰度增强的 RgbToGray 或通道分解";
                }
            }
            return ImageInputRequirements.CheckType(image, SupportedTypes(), $"灰度增强 {Method} 方式");
        }

        private string[] SupportedTypes()
        {
            switch (Method)
            {
                case GrayEnhanceMethod.PercentStretch:
                case GrayEnhanceMethod.EquHisto:
                    return new[] { "byte", "uint2" };
                case GrayEnhanceMethod.Gamma:
                case GrayEnhanceMethod.RgbToGray:
                    return new[] { "byte", "uint2", "real" };
                case GrayEnhanceMethod.Illuminate:
                    return new[] { "byte", "uint2", "int2" };
                default:
                    return AllTypes;
            }
        }

        /// <summary>按方式生成新图像；返回 null 时 error 为失败说明。</summary>
        private HObject Enhance(FlowContext ctx, HObject image, out string detail, out string error)
        {
            HObject output;
            error = null;
            switch (Method)
            {
                case GrayEnhanceMethod.Linear:
                    WarnIfLinearClips(ctx, image);
                    HOperatorSet.ScaleImage(image, out output, Mult, Add);
                    detail = $"Mult={Mult}, Add={Add}";
                    return output;
                case GrayEnhanceMethod.AutoStretch:
                    HOperatorSet.ScaleImageMax(image, out output);
                    detail = string.Empty;
                    return output;
                case GrayEnhanceMethod.PercentStretch:
                    return PercentStretch(ctx, image, out detail, out error);
                case GrayEnhanceMethod.EquHisto:
                    HOperatorSet.EquHistoImage(image, out output);
                    detail = string.Empty;
                    return output;
                case GrayEnhanceMethod.Invert:
                    HOperatorSet.InvertImage(image, out output);
                    detail = string.Empty;
                    return output;
                case GrayEnhanceMethod.Gamma:
                    HOperatorSet.GammaImage(image, out output, Gamma, Offset, Threshold, MaxGray, Encode ? "true" : "false");
                    detail = $"Gamma={Gamma}, Offset={Offset}, Threshold={Threshold}, MaxGray={MaxGray}, Encode={(Encode ? "true" : "false")}";
                    return output;
                case GrayEnhanceMethod.Illuminate:
                    HOperatorSet.Illuminate(image, out output, MaskWidth, MaskHeight, Factor);
                    detail = $"MaskWidth={MaskWidth}, MaskHeight={MaskHeight}, Factor={Factor}";
                    return output;
                case GrayEnhanceMethod.RgbToGray:
                    HOperatorSet.Rgb1ToGray(image, out output);
                    detail = string.Empty;
                    return output;
                case GrayEnhanceMethod.ConvertType:
                    HOperatorSet.ConvertImageType(image, out output, NewType.ToString());
                    detail = $"NewType={NewType}";
                    return output;
                default:
                    throw new InvalidOperationException($"未知的增强方式 {(int)Method}");
            }
        }

        /// <summary>
        /// 百分比拉伸：在图像定义域（get_domain，不是全图矩形）内用 min_max_gray 取两端各舍弃 Percent% 后的灰度范围，
        /// 线性映射到 byte 的 0 ~ 255 或 uint2 的 0 ~ 65535。空定义域失败；范围为 0 时输出副本并警告（使用方确认）。
        /// </summary>
        private HObject PercentStretch(FlowContext ctx, HObject image, out string detail, out string error)
        {
            detail = $"Percent={Percent}";
            error = null;
            HOperatorSet.GetDomain(image, out HObject domain);
            try
            {
                HOperatorSet.AreaCenter(domain, out HTuple area, out _, out _);
                if (area.Length == 0 || area[0].D <= 0)
                {
                    error = "图像定义域为空，无法统计灰度";
                    return null;
                }
                HOperatorSet.MinMaxGray(domain, image, Percent, out HTuple min, out HTuple max, out _);
                HOperatorSet.GetImageType(image, out HTuple type);
                double top = type[0].S == "uint2" ? 65535.0 : 255.0;
                detail = $"Percent={Percent}, Min={min.D}, Max={max.D}";
                if (!(max.D > min.D))
                {
                    ctx.AddLog(FlowLogLevel.Warning, $"[灰度增强] PercentStretch 定义域内灰度全部为 {min.D}（Percent={Percent}），无法拉伸，输出原图副本");
                    HOperatorSet.CopyImage(image, out HObject copy);
                    return copy;
                }
                double mult = top / (max.D - min.D);
                HOperatorSet.ScaleImage(image, out HObject output, mult, -min.D * mult);
                return output;
            }
            finally
            {
                domain.Dispose();
            }
        }

        /// <summary>scale_image 出界时饱和截断、不报错：按定义域内各通道的灰度范围估算结果，超出像素类型范围时写警告日志。</summary>
        private void WarnIfLinearClips(FlowContext ctx, HObject image)
        {
            HOperatorSet.GetImageType(image, out HTuple typeTuple);
            string type = typeTuple[0].S;
            double low, high;
            switch (type)
            {
                case "byte": low = 0; high = 255; break;
                case "uint2": low = 0; high = 65535; break;
                case "int2": low = -32768; high = 32767; break;
                default: return;
            }
            HOperatorSet.GetDomain(image, out HObject domain);
            try
            {
                HOperatorSet.AreaCenter(domain, out HTuple area, out _, out _);
                if (area.Length == 0 || area[0].D <= 0)
                {
                    return;
                }
                double resultMin = double.MaxValue, resultMax = double.MinValue;
                int channels = ImageInputRequirements.Channels(image);
                for (int channel = 1; channel <= channels; channel++)
                {
                    HOperatorSet.AccessChannel(image, out HObject single, channel);
                    try
                    {
                        HOperatorSet.MinMaxGray(domain, single, 0, out HTuple min, out HTuple max, out _);
                        double a = min.D * Mult + Add, b = max.D * Mult + Add;
                        resultMin = Math.Min(resultMin, Math.Min(a, b));
                        resultMax = Math.Max(resultMax, Math.Max(a, b));
                    }
                    finally
                    {
                        single.Dispose();
                    }
                }
                if (resultMin < low || resultMax > high)
                {
                    ctx.AddLog(FlowLogLevel.Warning, $"[灰度增强] Linear 结果范围约 [{resultMin:G6}, {resultMax:G6}] 超出 {type} 的 {low:G6} ~ {high:G6}，超出部分会被截断；可调整 Mult / Add，或先 ConvertType 到 real");
                }
            }
            finally
            {
                domain.Dispose();
            }
        }

        private string DescribeError(HObject image, HalconException ex)
        {
            return ex.GetErrorCode() == 3033
                ? $"光照校正掩膜尺寸超过图像尺寸（图像 {ImageInputRequirements.SizeText(image)}）"
                : ex.Message;
        }
    }
}
