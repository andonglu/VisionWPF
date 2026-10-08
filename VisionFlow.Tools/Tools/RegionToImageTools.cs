using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>区域转图像方式（按数字保存；Binary 在首位即默认）。</summary>
    public enum RegionToImageMethod
    {
        Binary,
        PaintOnImage
    }

    /// <summary>paint_region 的 Type（成员与顺序同 get_param_info 的 value_list，名称即 HALCON 参数值）。</summary>
    public enum RegionPaintType
    {
        fill,
        margin
    }

    /// <summary>
    /// 区域转图像（IP-07）：Binary 用 region_to_bin 生成二值掩膜图；PaintOnImage 用 paint_region 把区域画到参考图像上（结果图）。
    /// 行为按 IMAGE-TOOLS-PLAN 第 18 节的 22.11 实测：两个算子都绘制全部区域对象（margin 逐个对象画外轮廓 1 像素边）；
    /// paint_region 不受参考图定义域限制、输出保留参考图定义域；区域变量为 0 个对象时按空区域处理。
    /// </summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class RegionToImageTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>region_to_bin 允许的最大宽高（22.11 实测）。</summary>
        public const int MaxImageSize = 32768;

        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>参考图像（PaintOnImage 必填、作为绘制底图；Binary 可选、只取尺寸）。</summary>
        [InputRef("参考图像", typeof(HalconImage), Optional = true)]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>转换方式。</summary>
        public RegionToImageMethod Method { get; set; } = RegionToImageMethod.Binary;
        /// <summary>前景灰度（Binary，0 ~ 255）。</summary>
        public int ForegroundGray { get; set; } = 255;
        /// <summary>背景灰度（Binary，0 ~ 255）。</summary>
        public int BackgroundGray { get; set; } = 0;
        /// <summary>输出宽度（Binary 且未配置参考图像时使用，1 ~ 32768；配置了参考图像时取参考图尺寸）。</summary>
        public int Width { get; set; } = 512;
        /// <summary>输出高度（Binary 且未配置参考图像时使用，1 ~ 32768；配置了参考图像时取参考图尺寸）。</summary>
        public int Height { get; set; } = 512;
        /// <summary>绘制灰度（PaintOnImage；多通道图每个通道相同，运行时按参考图像素类型检查范围）。</summary>
        public double Gray { get; set; } = 255;
        /// <summary>绘制方式（PaintOnImage）：fill 填充区域，margin 画区域外轮廓 1 像素边。</summary>
        public RegionPaintType PaintType { get; set; } = RegionPaintType.fill;

        public RegionToImageTool(string moduleName) : base(moduleName)
        {
        }

        private bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (string.IsNullOrWhiteSpace(RegionPath))
            {
                yield return new ToolConfigurationIssue(nameof(RegionPath), "需要配置区域");
            }
            switch (Method)
            {
                case RegionToImageMethod.Binary:
                    if (ForegroundGray < 0 || ForegroundGray > 255)
                    {
                        yield return new ToolConfigurationIssue(nameof(ForegroundGray), $"前景灰度 ForegroundGray 必须在 0 ~ 255 之间（当前 {ForegroundGray}）");
                    }
                    if (BackgroundGray < 0 || BackgroundGray > 255)
                    {
                        yield return new ToolConfigurationIssue(nameof(BackgroundGray), $"背景灰度 BackgroundGray 必须在 0 ~ 255 之间（当前 {BackgroundGray}）");
                    }
                    if (!HasImage)
                    {
                        if (Width < 1 || Width > MaxImageSize)
                        {
                            yield return new ToolConfigurationIssue(nameof(Width), $"未配置参考图像时输出宽度 Width 必须在 1 ~ {MaxImageSize} 之间（当前 {Width}）");
                        }
                        if (Height < 1 || Height > MaxImageSize)
                        {
                            yield return new ToolConfigurationIssue(nameof(Height), $"未配置参考图像时输出高度 Height 必须在 1 ~ {MaxImageSize} 之间（当前 {Height}）");
                        }
                    }
                    break;
                case RegionToImageMethod.PaintOnImage:
                    if (!HasImage)
                    {
                        yield return new ToolConfigurationIssue(nameof(ImagePath), "PaintOnImage 方式需要配置参考图像");
                    }
                    if (!double.IsFinite(Gray))
                    {
                        yield return new ToolConfigurationIssue(nameof(Gray), $"绘制灰度 Gray 必须是有限数（当前 {Format(Gray)}）");
                    }
                    if (!Enum.IsDefined(typeof(RegionPaintType), PaintType))
                    {
                        yield return new ToolConfigurationIssue(nameof(PaintType), $"绘制方式 PaintType 只能是 fill / margin（当前 {(int)PaintType}）");
                    }
                    break;
                default:
                    yield return new ToolConfigurationIssue(nameof(Method), $"未知的区域转图像方式 {(int)Method}");
                    break;
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(ForegroundGray):
                case nameof(BackgroundGray):
                case nameof(Width):
                case nameof(Height):
                    return Method == RegionToImageMethod.Binary;
                case nameof(Gray):
                case nameof(PaintType):
                    return Method == RegionToImageMethod.PaintOnImage;
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

            HObject source = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject image = HasImage ? Input<HalconImage>(ctx, ImagePath).Object : null;
            HObject emptyRegion = null;
            HObject output = null;
            string detail = ParameterText();
            try
            {
                HOperatorSet.CountObj(source, out HTuple regionCount);
                HObject region = source;
                if (regionCount.I == 0)
                {
                    // 0 个对象时两个算子都不输出图像（第 18 节），按空区域处理
                    HOperatorSet.GenEmptyRegion(out emptyRegion);
                    region = emptyRegion;
                }
                if (image != null)
                {
                    HOperatorSet.CountObj(image, out HTuple imageCount);
                    if (imageCount.I != 1)
                    {
                        return NodeResult.Fail($"{ModuleName} ImagePath：参考图像只支持单幅图像（{ImagePath} 当前为 {imageCount.I} 幅）");
                    }
                }

                if (Method == RegionToImageMethod.Binary)
                {
                    int width = Width, height = Height;
                    if (image != null)
                    {
                        HOperatorSet.GetImageSize(image, out HTuple w, out HTuple h);
                        width = w.I;
                        height = h.I;
                    }
                    detail = $"{detail}，尺寸 {width}×{height}（{(image != null ? "取自参考图像 " + ImagePath : "Width / Height")}）";
                    HOperatorSet.RegionToBin(region, out output, ForegroundGray, BackgroundGray, width, height);
                }
                else
                {
                    HOperatorSet.GetImageType(image, out HTuple type);
                    HOperatorSet.CountChannels(image, out HTuple channels);
                    string rangeError = CheckGrayRange(type.S);
                    if (rangeError != null)
                    {
                        return NodeResult.Fail($"{ModuleName} Gray：{rangeError}");
                    }
                    detail = $"{detail}，参考图像 {ImagePath}（{type.S}，{channels.I} 通道）";
                    // paint_region 要求灰度个数等于通道数（第 18 节），同一灰度复制到每个通道
                    HTuple grays = new HTuple(Enumerable.Repeat(Gray, channels.I).ToArray());
                    HOperatorSet.PaintRegion(region, image, out output, grays, PaintType.ToString());
                }
                SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
                output = null;
                ctx.AddLog(FlowLogLevel.Info, $"[区域转图像] {detail}，区域对象 {regionCount.I} 个");
                return NodeResult.Ok;
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 区域转图像 {Method} 失败（{detail}）：{ex.Message}");
            }
            finally
            {
                output?.Dispose();
                emptyRegion?.Dispose();
            }
        }

        /// <summary>按参考图像素类型检查绘制灰度（第 18 节：算子对超出范围的值静默截断，工具改为报错）。</summary>
        private string CheckGrayRange(string pixelType)
        {
            double min, max;
            switch (pixelType)
            {
                case "byte": (min, max) = (0, 255); break;
                case "int1": (min, max) = (-128, 127); break;
                case "uint2": (min, max) = (0, 65535); break;
                case "int2": (min, max) = (-32768, 32767); break;
                default: return null;
            }
            if (Gray < min || Gray > max)
            {
                return $"绘制灰度 Gray = {Format(Gray)} 超出参考图像像素类型 {pixelType} 的取值范围 {Format(min)} ~ {Format(max)}";
            }
            return null;
        }

        private string ParameterText()
        {
            return Method == RegionToImageMethod.Binary
                ? $"Method = Binary，ForegroundGray = {ForegroundGray}，BackgroundGray = {BackgroundGray}"
                : $"Method = PaintOnImage，Gray = {Format(Gray)}，PaintType = {PaintType}";
        }

        private static string Format(double value)
        {
            return value.ToString("G", CultureInfo.InvariantCulture);
        }
    }
}
