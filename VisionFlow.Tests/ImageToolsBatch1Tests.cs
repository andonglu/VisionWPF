using System.Text.Json.Nodes;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;
using Xunit;

namespace VisionFlow.Tests;

/// <summary>
/// IMAGE-TOOLS 第一批：IP-01 图像滤波（原均值滤波）+ IP-03 灰度增强。
/// 标注 Requires=HALCON 的用例与直接调用 HALCON 算子逐像素对照；其余用例（校验、显隐、序列化、登记）不调用 HALCON 算子。
/// </summary>
public class ImageToolsBatch1Tests
{
    // ======================= 合成图像与对照工具 =======================

    /// <summary>64 × 48 字节图：水平渐变加可复现的小噪声（与第 10 节探测相同）。</summary>
    private static HObject ByteImage(int width = 64, int height = 48)
    {
        HOperatorSet.GenImageConst(out HObject image, "byte", width, height);
        var rnd = new Random(7);
        var rows = new List<int>();
        var cols = new List<int>();
        var values = new List<int>();
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++)
            {
                rows.Add(r);
                cols.Add(c);
                values.Add(Math.Clamp(20 + c * 3 + rnd.Next(-5, 6), 0, 255));
            }
        }
        HOperatorSet.SetGrayval(image, new HTuple(rows.ToArray()), new HTuple(cols.ToArray()), new HTuple(values.ToArray()));
        return image;
    }

    private static HObject Converted(HObject image, string type)
    {
        HOperatorSet.ConvertImageType(image, out HObject converted, type);
        return converted;
    }

    private static HObject RgbImage()
    {
        HObject a = ByteImage();
        HOperatorSet.InvertImage(a, out HObject b);
        HOperatorSet.ScaleImage(a, out HObject c, 0.5, 30);
        HOperatorSet.Compose3(a, b, c, out HObject rgb);
        return rgb;
    }

    private static HObject Reduced(HObject image, int r1, int c1, int r2, int c2)
    {
        HOperatorSet.GenRectangle1(out HObject rect, r1, c1, r2, c2);
        HOperatorSet.ReduceDomain(image, rect, out HObject reduced);
        rect.Dispose();
        return reduced;
    }

    /// <summary>类型、通道、尺寸、定义域相同，且定义域内每个通道的每个像素都相同。</summary>
    private static void AssertSameImage(HObject expected, HObject actual)
    {
        HOperatorSet.GetImageType(expected, out HTuple expectedType);
        HOperatorSet.GetImageType(actual, out HTuple actualType);
        Assert.Equal(expectedType.S, actualType.S);
        HOperatorSet.CountChannels(expected, out HTuple expectedChannels);
        HOperatorSet.CountChannels(actual, out HTuple actualChannels);
        Assert.Equal(expectedChannels.I, actualChannels.I);
        HOperatorSet.GetImageSize(expected, out HTuple ew, out HTuple eh);
        HOperatorSet.GetImageSize(actual, out HTuple aw, out HTuple ah);
        Assert.Equal((ew.I, eh.I), (aw.I, ah.I));
        HOperatorSet.GetDomain(expected, out HObject expectedDomain);
        HOperatorSet.GetDomain(actual, out HObject actualDomain);
        HOperatorSet.SymmDifference(expectedDomain, actualDomain, out HObject domainDiff);
        HOperatorSet.AreaCenter(domainDiff, out HTuple diffArea, out _, out _);
        Assert.Equal(0, diffArea.I);
        HOperatorSet.AreaCenter(expectedDomain, out HTuple domainArea, out _, out _);
        if (domainArea.I == 0)
        {
            return;
        }
        for (int channel = 1; channel <= expectedChannels.I; channel++)
        {
            HOperatorSet.AccessChannel(expected, out HObject e, channel);
            HOperatorSet.AccessChannel(actual, out HObject a, channel);
            HOperatorSet.SubImage(Converted(e, "real"), Converted(a, "real"), out HObject diff, 1, 0);
            HOperatorSet.MinMaxGray(expectedDomain, diff, 0, out HTuple min, out HTuple max, out _);
            Assert.True(min.D == 0 && max.D == 0, $"通道 {channel} 像素差 [{min.D}, {max.D}]");
        }
    }

    private static FlowContext ContextWith(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static HObject RunTool(ToolBase tool, HObject image, out FlowContext ctx)
    {
        ctx = ContextWith(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ((HalconImage)ctx.GetVariable(tool.ModuleName, "Image").Value).Object;
    }

    private static string RunFailure(ToolBase tool, HObject image)
    {
        NodeResult result = tool.Run(ContextWith(image));
        Assert.False(result.IsSuccess);
        return result.Message;
    }

    private static MeanImageTool Filter(ImageFilterMethod method) => new MeanImageTool("滤波1") { ImagePath = "图像1.Image", Method = method };

    private static GrayEnhanceTool Enhance(GrayEnhanceMethod method) => new GrayEnhanceTool("增强1") { ImagePath = "图像1.Image", Method = method };

    // ======================= IP-01 与 HALCON 逐像素一致（门禁） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(ImageFilterMethod.Mean)]
    [InlineData(ImageFilterMethod.Gauss)]
    [InlineData(ImageFilterMethod.Median)]
    [InlineData(ImageFilterMethod.Smooth)]
    [InlineData(ImageFilterMethod.Bilateral)]
    [InlineData(ImageFilterMethod.Emphasize)]
    [InlineData(ImageFilterMethod.SobelAmp)]
    [InlineData(ImageFilterMethod.GrayErosion)]
    [InlineData(ImageFilterMethod.GrayDilation)]
    [InlineData(ImageFilterMethod.GrayOpening)]
    [InlineData(ImageFilterMethod.GrayClosing)]
    public void 图像滤波_各方式与直接调用HALCON逐像素一致_含彩色图逐通道(ImageFilterMethod method)
    {
        // 宽高不同，固定灰度形态学的（高, 宽）参数顺序
        MeanImageTool tool = Filter(method);
        tool.Width = 7;
        tool.Height = 3;
        tool.GaussSize = GaussFilterSize.Size9;
        tool.MaskType = MedianMaskType.square;
        tool.Radius = 2;
        tool.Margin = MedianMargin.cyclic;
        tool.SmoothFilter = SmoothFilterType.shen;
        tool.Alpha = 1.5;
        tool.SigmaSpatial = 2.5;
        tool.SigmaRange = 30;
        tool.Factor = 1.4;
        tool.SobelType = SobelFilterType.thin_max_abs_binomial;
        tool.SobelSize = SobelFilterSize.Size5;
        foreach (HObject input in new[] { ByteImage(), RgbImage(), Reduced(ByteImage(), 5, 6, 40, 50) })
        {
            HObject expected;
            switch (method)
            {
                case ImageFilterMethod.Mean: HOperatorSet.MeanImage(input, out expected, 7, 3); break;
                case ImageFilterMethod.Gauss: HOperatorSet.GaussFilter(input, out expected, 9); break;
                case ImageFilterMethod.Median: HOperatorSet.MedianImage(input, out expected, "square", 2, "cyclic"); break;
                case ImageFilterMethod.Smooth: HOperatorSet.SmoothImage(input, out expected, "shen", 1.5); break;
                case ImageFilterMethod.Bilateral: HOperatorSet.BilateralFilter(input, input, out expected, 2.5, 30, new HTuple(), new HTuple()); break;
                case ImageFilterMethod.Emphasize: HOperatorSet.Emphasize(input, out expected, 7, 3, 1.4); break;
                case ImageFilterMethod.SobelAmp: HOperatorSet.SobelAmp(input, out expected, "thin_max_abs_binomial", 5); break;
                case ImageFilterMethod.GrayErosion: HOperatorSet.GrayErosionRect(input, out expected, 3, 7); break;
                case ImageFilterMethod.GrayDilation: HOperatorSet.GrayDilationRect(input, out expected, 3, 7); break;
                case ImageFilterMethod.GrayOpening: HOperatorSet.GrayOpeningRect(input, out expected, 3, 7); break;
                default: HOperatorSet.GrayClosingRect(input, out expected, 3, 7); break;
            }
            HObject actual = RunTool(tool, input, out FlowContext ctx);
            AssertSameImage(expected, actual);
            Assert.NotSame(input, actual);
            Assert.True(input.IsInitialized());
            ctx.Dispose();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 灰度形态学按高宽顺序调用_宽7高1只沿行方向扩展()
    {
        HOperatorSet.GenImageConst(out HObject flat, "byte", 21, 21);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, 20, 20);
        HOperatorSet.PaintRegion(all, flat, out HObject bright, 200, "fill");
        HOperatorSet.GenRectangle1(out HObject dot, 10, 10, 10, 10);
        HOperatorSet.PaintRegion(dot, bright, out HObject withDot, 0, "fill");
        MeanImageTool tool = Filter(ImageFilterMethod.GrayErosion);
        tool.Width = 7;
        tool.Height = 1;

        HObject eroded = RunTool(tool, withDot, out _);
        HOperatorSet.Threshold(eroded, out HObject dark, 0, 50);
        HOperatorSet.SmallestRectangle1(dark, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
        Assert.Equal((10, 7, 10, 13), (r1.I, c1.I, r2.I, c2.I));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 均值方式与旧版一致_日志格式不变()
    {
        HObject input = ByteImage();
        var tool = new MeanImageTool("均值1") { ImagePath = "图像1.Image" };
        Assert.Equal(ImageFilterMethod.Mean, tool.Method);
        HOperatorSet.MeanImage(input, out HObject expected, 9, 9);

        HObject actual = RunTool(tool, input, out FlowContext ctx);
        AssertSameImage(expected, actual);
        Assert.Contains(ctx.StructuredLogs, l => l.Message == "[均值滤波] Width=9, Height=9");
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(ImageFilterMethod.Smooth, "int2", "图像滤波 Smooth 方式不支持 int2 图像（支持 byte / uint2 / real）")]
    [InlineData(ImageFilterMethod.Bilateral, "int2", "图像滤波 Bilateral 方式不支持 int2 图像（支持 byte / uint2 / real）")]
    [InlineData(ImageFilterMethod.Emphasize, "real", "图像滤波 Emphasize 方式不支持 real 图像（支持 byte / uint2 / int2）")]
    [InlineData(ImageFilterMethod.Gauss, "int4", "图像滤波 Gauss 方式不支持 int4 图像（支持 byte / uint2 / int2 / real）")]
    public void 图像滤波_不支持的像素类型给出中文错误(ImageFilterMethod method, string type, string expected)
    {
        string message = RunFailure(Filter(method), Converted(ByteImage(), type));
        Assert.Equal($"滤波1 {expected}，可先接灰度增强的 ConvertType 转换像素类型", message);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 图像滤波_随图像尺寸变化的上限在运行时给出带尺寸的中文错误()
    {
        MeanImageTool median = Filter(ImageFilterMethod.Median);
        median.Radius = 30;
        Assert.Equal("滤波1 图像滤波 Median 失败：中值滤波半径 30 超出图像允许的范围（图像 64×48，半径须小于短边的一半左右）", RunFailure(median, ByteImage()));

        MeanImageTool closing = Filter(ImageFilterMethod.GrayClosing);
        closing.Width = 200;
        Assert.Equal("滤波1 图像滤波 GrayClosing 失败：滤波掩膜尺寸超过图像尺寸（图像 64×48）", RunFailure(closing, ByteImage()));

        MeanImageTool bilateral = Filter(ImageFilterMethod.Bilateral);
        bilateral.SigmaSpatial = 25;
        Assert.Equal("滤波1 图像滤波 Bilateral 失败：空间标准差 SigmaSpatial=25 对应的滤波尺寸超过图像尺寸（图像 64×48）", RunFailure(bilateral, ByteImage()));
    }

    // ======================= IP-03 与 HALCON 逐像素一致（门禁） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(GrayEnhanceMethod.Linear)]
    [InlineData(GrayEnhanceMethod.AutoStretch)]
    [InlineData(GrayEnhanceMethod.EquHisto)]
    [InlineData(GrayEnhanceMethod.Invert)]
    [InlineData(GrayEnhanceMethod.Gamma)]
    [InlineData(GrayEnhanceMethod.Illuminate)]
    [InlineData(GrayEnhanceMethod.ConvertType)]
    public void 灰度增强_各方式与直接调用HALCON逐像素一致_含彩色图逐通道(GrayEnhanceMethod method)
    {
        GrayEnhanceTool tool = Enhance(method);
        tool.Mult = 1.3;
        tool.Add = -12;
        tool.Gamma = 2.2;
        tool.Offset = 0;
        tool.Threshold = 0;
        tool.MaxGray = 255;
        tool.Encode = false;
        tool.MaskWidth = 31;
        tool.MaskHeight = 21;
        tool.Factor = 0.9;
        tool.NewType = ConvertImageNewType.real;
        foreach (HObject input in new[] { ByteImage(), RgbImage(), Reduced(ByteImage(), 5, 6, 40, 50) })
        {
            HObject expected;
            switch (method)
            {
                case GrayEnhanceMethod.Linear: HOperatorSet.ScaleImage(input, out expected, 1.3, -12); break;
                case GrayEnhanceMethod.AutoStretch: HOperatorSet.ScaleImageMax(input, out expected); break;
                case GrayEnhanceMethod.EquHisto: HOperatorSet.EquHistoImage(input, out expected); break;
                case GrayEnhanceMethod.Invert: HOperatorSet.InvertImage(input, out expected); break;
                case GrayEnhanceMethod.Gamma: HOperatorSet.GammaImage(input, out expected, 2.2, 0, 0, 255, "false"); break;
                case GrayEnhanceMethod.Illuminate: HOperatorSet.Illuminate(input, out expected, 31, 21, 0.9); break;
                default: HOperatorSet.ConvertImageType(input, out expected, "real"); break;
            }
            HObject actual = RunTool(tool, input, out FlowContext ctx);
            AssertSameImage(expected, actual);
            Assert.NotSame(input, actual);
            Assert.True(input.IsInitialized());
            ctx.Dispose();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void RgbToGray_三通道与rgb1_to_gray一致_单通道报错()
    {
        HObject rgb = RgbImage();
        HOperatorSet.Rgb1ToGray(rgb, out HObject expected);
        AssertSameImage(expected, RunTool(Enhance(GrayEnhanceMethod.RgbToGray), rgb, out _));

        Assert.Equal("增强1 灰度增强 RgbToGray 需要三通道彩色图像（当前 1 通道）", RunFailure(Enhance(GrayEnhanceMethod.RgbToGray), ByteImage()));
        HObject b = ByteImage();
        HOperatorSet.Compose2(b, b, out HObject two);
        Assert.Equal("增强1 灰度增强 RgbToGray 需要三通道彩色图像（当前 2 通道）", RunFailure(Enhance(GrayEnhanceMethod.RgbToGray), two));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void PercentStretch_已知直方图与按定义域手算一致()
    {
        // 灰度 50 ~ 150 线性分布（每列一个灰度，共 101 列）：Percent 0 时等价于 scale_image_max（与它逐像素一致），最大灰度映射为 255
        HOperatorSet.GenImageConst(out HObject image, "byte", 101, 10);
        var rows = new List<int>();
        var cols = new List<int>();
        var values = new List<int>();
        for (int r = 0; r < 10; r++)
        {
            for (int c = 0; c <= 100; c++)
            {
                rows.Add(r);
                cols.Add(c);
                values.Add(50 + c);
            }
        }
        HOperatorSet.SetGrayval(image, new HTuple(rows.ToArray()), new HTuple(cols.ToArray()), new HTuple(values.ToArray()));
        GrayEnhanceTool tool = Enhance(GrayEnhanceMethod.PercentStretch);
        tool.Percent = 0;
        HObject stretched = RunTool(tool, image, out _);
        HOperatorSet.ScaleImageMax(image, out HObject autoStretched);
        AssertSameImage(autoStretched, stretched);
        HOperatorSet.GetGrayval(stretched, new HTuple(0, 0, 0), new HTuple(0, 20, 100), out HTuple gray);
        // scale_image 对 Add = −50 × 2.55 的取整使最小灰度落在 1（scale_image_max 同样如此，第 10 节）
        Assert.Equal(new[] { 1, 52, 255 }, gray.ToIArr());

        // Percent 10：两端各舍弃 10% 后的范围映射到 0 ~ 255，与直接调用 min_max_gray + scale_image 一致
        tool.Percent = 10;
        HOperatorSet.GetDomain(image, out HObject domain);
        HOperatorSet.MinMaxGray(domain, image, 10, out HTuple min, out HTuple max, out _);
        Assert.True(min.D > 50 && max.D < 150);
        double mult = 255.0 / (max.D - min.D);
        HOperatorSet.ScaleImage(image, out HObject expected, mult, -min.D * mult);
        AssertSameImage(expected, RunTool(tool, image, out _));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void PercentStretch_只统计定义域_而不是全图()
    {
        HObject image = ByteImage();
        HObject reduced = Reduced(image, 10, 10, 30, 25);
        GrayEnhanceTool tool = Enhance(GrayEnhanceMethod.PercentStretch);
        tool.Percent = 1;

        HOperatorSet.GetDomain(reduced, out HObject domain);
        HOperatorSet.MinMaxGray(domain, reduced, 1, out HTuple min, out HTuple max, out _);
        HOperatorSet.GetDomain(image, out HObject fullDomain);
        HOperatorSet.MinMaxGray(fullDomain, image, 1, out HTuple fullMin, out HTuple fullMax, out _);
        Assert.NotEqual((fullMin.D, fullMax.D), (min.D, max.D));
        double mult = 255.0 / (max.D - min.D);
        HOperatorSet.ScaleImage(reduced, out HObject expected, mult, -min.D * mult);

        HObject actual = RunTool(tool, reduced, out FlowContext ctx);
        AssertSameImage(expected, actual);
        Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains($"Min={min.D}, Max={max.D}"));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void PercentStretch_uint2拉伸到65535_空定义域失败_灰度一致输出副本并警告_彩色图报错()
    {
        HObject uint2 = Converted(ByteImage(), "uint2");
        HOperatorSet.GetDomain(uint2, out HObject domain);
        HOperatorSet.MinMaxGray(domain, uint2, 1, out HTuple min, out HTuple max, out _);
        double mult = 65535.0 / (max.D - min.D);
        HOperatorSet.ScaleImage(uint2, out HObject expected, mult, -min.D * mult);
        AssertSameImage(expected, RunTool(Enhance(GrayEnhanceMethod.PercentStretch), uint2, out _));

        HOperatorSet.GenEmptyRegion(out HObject empty);
        HOperatorSet.ReduceDomain(ByteImage(), empty, out HObject emptyDomain);
        Assert.Equal("增强1 图像定义域为空，无法统计灰度", RunFailure(Enhance(GrayEnhanceMethod.PercentStretch), emptyDomain));

        HOperatorSet.GenImageConst(out HObject constant, "byte", 20, 10);
        HObject copy = RunTool(Enhance(GrayEnhanceMethod.PercentStretch), constant, out FlowContext ctx);
        AssertSameImage(constant, copy);
        Assert.NotSame(constant, copy);
        Assert.Contains(ctx.StructuredLogs, l => l.Level == FlowLogLevel.Warning && l.Message.Contains("无法拉伸，输出原图副本"));

        Assert.StartsWith("增强1 灰度增强 PercentStretch 只接受单通道图像（当前 3 通道", RunFailure(Enhance(GrayEnhanceMethod.PercentStretch), RgbImage()));
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(GrayEnhanceMethod.EquHisto, "real", "byte / uint2")]
    [InlineData(GrayEnhanceMethod.EquHisto, "int2", "byte / uint2")]
    [InlineData(GrayEnhanceMethod.Gamma, "int2", "byte / uint2 / real")]
    [InlineData(GrayEnhanceMethod.Illuminate, "real", "byte / uint2 / int2")]
    [InlineData(GrayEnhanceMethod.PercentStretch, "real", "byte / uint2")]
    public void 灰度增强_不支持的像素类型给出中文错误(GrayEnhanceMethod method, string type, string supported)
    {
        Assert.Equal($"增强1 灰度增强 {method} 方式不支持 {type} 图像（支持 {supported}），可先接灰度增强的 ConvertType 转换像素类型",
            RunFailure(Enhance(method), Converted(ByteImage(), type)));
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(ConvertImageNewType.@byte, "byte")]
    [InlineData(ConvertImageNewType.uint2, "uint2")]
    [InlineData(ConvertImageNewType.int2, "int2")]
    [InlineData(ConvertImageNewType.real, "real")]
    public void ConvertType_输出类型正确(ConvertImageNewType newType, string expectedType)
    {
        GrayEnhanceTool tool = Enhance(GrayEnhanceMethod.ConvertType);
        tool.NewType = newType;
        HObject output = RunTool(tool, Converted(ByteImage(), "real"), out _);
        HOperatorSet.GetImageType(output, out HTuple type);
        Assert.Equal(expectedType, type.S);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void Linear出界时警告_不出界时不警告()
    {
        GrayEnhanceTool tool = Enhance(GrayEnhanceMethod.Linear);
        tool.Mult = 2;
        tool.Add = 100;
        RunTool(tool, ByteImage(), out FlowContext clipped);
        Assert.Contains(clipped.StructuredLogs, l => l.Level == FlowLogLevel.Warning && l.Message.Contains("超出 byte 的 0 ~ 255，超出部分会被截断"));

        tool.Mult = 0.5;
        tool.Add = 10;
        RunTool(tool, ByteImage(), out FlowContext inside);
        Assert.DoesNotContain(inside.StructuredLogs, l => l.Level == FlowLogLevel.Warning);

        tool.Mult = 1000;
        RunTool(tool, Converted(ByteImage(), "real"), out FlowContext real);
        Assert.DoesNotContain(real.StructuredLogs, l => l.Level == FlowLogLevel.Warning);
    }

    // ======================= 参数校验：流程校验与运行措辞一致（非 HALCON） =======================

    public static IEnumerable<object[]> FilterRangeCases()
    {
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Gauss; t.GaussSize = (GaussFilterSize)4; }), "GaussSize", "高斯滤波尺寸只能是 3、5、7、9、11" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Median; t.Radius = 0; }), "Radius", "中值滤波半径必须不小于 1" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Median; t.MaskType = (MedianMaskType)5; }), "MaskType", "中值滤波掩膜形状只能是 circle / square" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Median; t.Margin = (MedianMargin)7; }), "Margin", "中值滤波边界处理只能是 mirrored / cyclic / continued" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Smooth; t.Alpha = 0; }), "Alpha", "平滑系数 Alpha 必须大于 0" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Smooth; t.SmoothFilter = (SmoothFilterType)9; }), "SmoothFilter", "平滑滤波器只能是 deriche1 / deriche2 / shen / gauss" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Bilateral; t.SigmaSpatial = 0.59; }), "SigmaSpatial", "双边滤波空间标准差 SigmaSpatial 必须不小于 0.6" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Bilateral; t.SigmaRange = 0; }), "SigmaRange", "双边滤波灰度标准差 SigmaRange 必须大于 0" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Emphasize; t.Width = 2; }), "Width / Height", "锐化掩膜宽高必须不小于 3" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.Emphasize; t.Factor = -0.1; }), "Factor", "锐化系数 Factor 必须不小于 0" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.SobelAmp; t.SobelType = (SobelFilterType)99; }), "SobelType", "Sobel 滤波类型无效" };
        yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = ImageFilterMethod.SobelAmp; t.SobelSize = (SobelFilterSize)15; }), "SobelSize", "Sobel 滤波尺寸只能是 3、5、7、9、11、13" };
        foreach (ImageFilterMethod gray in new[] { ImageFilterMethod.GrayErosion, ImageFilterMethod.GrayDilation, ImageFilterMethod.GrayOpening, ImageFilterMethod.GrayClosing })
        {
            yield return new object[] { (Action<MeanImageTool>)(t => { t.Method = gray; t.Height = 0; }), "Width / Height", "灰度形态学掩膜宽高必须大于 0" };
        }
        yield return new object[] { (Action<MeanImageTool>)(t => t.Method = (ImageFilterMethod)99), "Method", "未知的滤波方式 99" };
    }

    [Theory]
    [MemberData(nameof(FilterRangeCases))]
    public void 图像滤波_参数越界_流程校验与运行措辞一致(Action<MeanImageTool> configure, string parameter, string message)
    {
        MeanImageTool tool = Filter(ImageFilterMethod.Mean);
        configure(tool);
        ToolConfigurationIssue issue = Assert.Single(tool.CheckConfiguration());
        Assert.Equal((parameter, message), (issue.Parameter, issue.Message));
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"滤波1 {parameter}：{message}", result.Message);
    }

    [Fact]
    public void 均值方式错误信息与旧版逐字相同()
    {
        var tool = new MeanImageTool("均值1") { ImagePath = "图像1.Image", Width = 0 };
        ToolConfigurationIssue issue = Assert.Single(tool.CheckConfiguration());
        Assert.Equal("均值滤波核宽高必须大于 0", issue.Message);
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal("均值滤波核宽高必须大于 0", result.Message);
        Assert.Empty(new MeanImageTool("均值1").CheckConfiguration());
    }

    public static IEnumerable<object[]> EnhanceRangeCases()
    {
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.Linear; t.Mult = double.NaN; }), "Mult / Add", "线性变换的 Mult / Add 必须是有限数" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.Linear; t.Add = double.PositiveInfinity; }), "Mult / Add", "线性变换的 Mult / Add 必须是有限数" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.PercentStretch; t.Percent = 51; }), "Percent", "百分比拉伸的 Percent 必须在 0 ~ 50 之间" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.PercentStretch; t.Percent = -1; }), "Percent", "百分比拉伸的 Percent 必须在 0 ~ 50 之间" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.Gamma; t.Gamma = 0; }), "Gamma", "伽马指数 Gamma 必须大于 0" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.Gamma; t.Offset = -1; }), "Offset", "伽马偏移 Offset 必须不小于 0" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.Gamma; t.Threshold = -1; }), "Threshold", "伽马阈值 Threshold 必须不小于 0" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.Gamma; t.MaxGray = 0; }), "MaxGray", "伽马最大灰度 MaxGray 必须大于 0" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.Illuminate; t.MaskWidth = 0; }), "MaskWidth / MaskHeight", "光照校正掩膜宽高必须大于 0" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.Illuminate; t.Factor = -1; }), "Factor", "光照校正强度 Factor 必须不小于 0" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => { t.Method = GrayEnhanceMethod.ConvertType; t.NewType = (ConvertImageNewType)9; }), "NewType", "目标像素类型只能是 byte / uint2 / int2 / real" };
        yield return new object[] { (Action<GrayEnhanceTool>)(t => t.Method = (GrayEnhanceMethod)99), "Method", "未知的增强方式 99" };
    }

    [Theory]
    [MemberData(nameof(EnhanceRangeCases))]
    public void 灰度增强_参数越界_流程校验与运行措辞一致(Action<GrayEnhanceTool> configure, string parameter, string message)
    {
        GrayEnhanceTool tool = Enhance(GrayEnhanceMethod.Linear);
        configure(tool);
        ToolConfigurationIssue issue = Assert.Single(tool.CheckConfiguration());
        Assert.Equal((parameter, message), (issue.Parameter, issue.Message));
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"增强1 {parameter}：{message}", result.Message);
    }

    [Fact]
    public void 默认参数校验通过_且只校验当前方式的参数()
    {
        foreach (ImageFilterMethod method in Enum.GetValues<ImageFilterMethod>())
        {
            Assert.Empty(new MeanImageTool("滤波1") { Method = method }.CheckConfiguration());
        }
        foreach (GrayEnhanceMethod method in Enum.GetValues<GrayEnhanceMethod>())
        {
            Assert.Empty(new GrayEnhanceTool("增强1") { Method = method }.CheckConfiguration());
        }
        // 其他方式的参数越界不影响当前方式
        Assert.Empty(new MeanImageTool("滤波1") { Method = ImageFilterMethod.Gauss, Radius = 0, Alpha = 0, Width = 0 }.CheckConfiguration());
        Assert.Empty(new GrayEnhanceTool("增强1") { Method = GrayEnhanceMethod.Invert, Percent = 99, Gamma = 0, MaskWidth = 0 }.CheckConfiguration());
    }

    // ======================= 参数显隐（非 HALCON） =======================

    private static readonly string[] FilterParameters =
        { "Width", "Height", "GaussSize", "MaskType", "Radius", "Margin", "SmoothFilter", "Alpha", "SigmaSpatial", "SigmaRange", "Factor", "SobelType", "SobelSize" };

    [Theory]
    [InlineData(ImageFilterMethod.Mean, "Width,Height")]
    [InlineData(ImageFilterMethod.Gauss, "GaussSize")]
    [InlineData(ImageFilterMethod.Median, "MaskType,Radius,Margin")]
    [InlineData(ImageFilterMethod.Smooth, "SmoothFilter,Alpha")]
    [InlineData(ImageFilterMethod.Bilateral, "SigmaSpatial,SigmaRange")]
    [InlineData(ImageFilterMethod.Emphasize, "Width,Height,Factor")]
    [InlineData(ImageFilterMethod.SobelAmp, "SobelType,SobelSize")]
    [InlineData(ImageFilterMethod.GrayErosion, "Width,Height")]
    [InlineData(ImageFilterMethod.GrayDilation, "Width,Height")]
    [InlineData(ImageFilterMethod.GrayOpening, "Width,Height")]
    [InlineData(ImageFilterMethod.GrayClosing, "Width,Height")]
    public void 图像滤波_按方式精确显隐参数(ImageFilterMethod method, string visible)
    {
        var tool = new MeanImageTool("滤波1") { Method = method };
        string[] expected = visible.Split(',');
        Assert.Equal(expected, FilterParameters.Where(tool.IsParameterVisible).ToArray());
        Assert.True(tool.IsParameterVisible("Method"));
        Assert.True(tool.IsParameterVisible("ImagePath"));
    }

    private static readonly string[] EnhanceParameters =
        { "Mult", "Add", "Percent", "Gamma", "Offset", "Threshold", "MaxGray", "Encode", "MaskWidth", "MaskHeight", "Factor", "NewType" };

    [Theory]
    [InlineData(GrayEnhanceMethod.Linear, "Mult,Add")]
    [InlineData(GrayEnhanceMethod.AutoStretch, "")]
    [InlineData(GrayEnhanceMethod.PercentStretch, "Percent")]
    [InlineData(GrayEnhanceMethod.EquHisto, "")]
    [InlineData(GrayEnhanceMethod.Invert, "")]
    [InlineData(GrayEnhanceMethod.Gamma, "Gamma,Offset,Threshold,MaxGray,Encode")]
    [InlineData(GrayEnhanceMethod.Illuminate, "MaskWidth,MaskHeight,Factor")]
    [InlineData(GrayEnhanceMethod.RgbToGray, "")]
    [InlineData(GrayEnhanceMethod.ConvertType, "NewType")]
    public void 灰度增强_按方式精确显隐参数(GrayEnhanceMethod method, string visible)
    {
        var tool = new GrayEnhanceTool("增强1") { Method = method };
        string[] expected = visible.Length == 0 ? Array.Empty<string>() : visible.Split(',');
        Assert.Equal(expected, EnhanceParameters.Where(tool.IsParameterVisible).ToArray());
        Assert.True(tool.IsParameterVisible("Method"));
        Assert.True(tool.IsParameterVisible("ImagePath"));
    }

    // ======================= 枚举、序列化、命名守卫（非 HALCON） =======================

    [Fact]
    public void 枚举成员顺序与取值固定_成员名即HALCON参数值()
    {
        Assert.Equal(new[] { "Mean", "Gauss", "Median", "Smooth", "Bilateral", "Emphasize", "SobelAmp", "GrayErosion", "GrayDilation", "GrayOpening", "GrayClosing" },
            Enum.GetNames<ImageFilterMethod>());
        Assert.Equal(new[] { 3, 5, 7, 9, 11 }, Enum.GetValues<GaussFilterSize>().Select(v => (int)v));
        Assert.Equal(new[] { 3, 5, 7, 9, 11, 13 }, Enum.GetValues<SobelFilterSize>().Select(v => (int)v));
        Assert.Equal(new[] { "circle", "square" }, Enum.GetNames<MedianMaskType>());
        Assert.Equal(new[] { "mirrored", "cyclic", "continued" }, Enum.GetNames<MedianMargin>());
        Assert.Equal(new[] { "deriche1", "deriche2", "shen", "gauss" }, Enum.GetNames<SmoothFilterType>());
        Assert.Equal(new[] { "sum_abs", "thin_sum_abs", "thin_max_abs", "sum_sqrt", "x", "y", "sum_abs_binomial", "thin_sum_abs_binomial",
            "thin_max_abs_binomial", "sum_sqrt_binomial", "x_binomial", "y_binomial" }, Enum.GetNames<SobelFilterType>());
        Assert.Equal(new[] { "Linear", "AutoStretch", "PercentStretch", "EquHisto", "Invert", "Gamma", "Illuminate", "RgbToGray", "ConvertType" },
            Enum.GetNames<GrayEnhanceMethod>());
        Assert.Equal(new[] { "byte", "uint2", "int2", "real" }, Enum.GetNames<ConvertImageNewType>());
    }

    [Fact]
    public void 图像滤波按数字保存_历史文件缺省为Mean()
    {
        var tool = new MeanImageTool("滤波1")
        {
            Method = ImageFilterMethod.SobelAmp,
            GaussSize = GaussFilterSize.Size9,
            SobelType = SobelFilterType.x_binomial,
            SobelSize = SobelFilterSize.Size13,
            MaskType = MedianMaskType.square,
            Margin = MedianMargin.continued,
            SmoothFilter = SmoothFilterType.gauss
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"mean-image\"", json);
        Assert.Contains("\"Method\": 6", json);
        Assert.Contains("\"GaussSize\": 9", json);
        Assert.Contains("\"SobelSize\": 13", json);
        Assert.Contains("\"SobelType\": 10", json);
        Assert.Contains("\"MaskType\": 1", json);
        Assert.Contains("\"Margin\": 2", json);
        Assert.Contains("\"SmoothFilter\": 3", json);
        var loaded = (MeanImageTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((ImageFilterMethod.SobelAmp, GaussFilterSize.Size9, SobelFilterType.x_binomial, SobelFilterSize.Size13),
            (loaded.Method, loaded.GaussSize, loaded.SobelType, loaded.SobelSize));

        // 旧版流程只有 ImagePath / Width / Height
        JsonObject legacy = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new MeanImageTool("均值1") { Width = 5, Height = 7 })))!.AsObject();
        JsonObject properties = legacy["Tool"]!["Properties"]!.AsObject();
        foreach (string name in properties.Select(p => p.Key).Where(k => k != "ImagePath" && k != "Width" && k != "Height").ToList())
        {
            properties.Remove(name);
        }
        var legacyTool = (MeanImageTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy.ToJsonString())).Tool;
        Assert.Equal(ImageFilterMethod.Mean, legacyTool.Method);
        Assert.Equal((5, 7), (legacyTool.Width, legacyTool.Height));
        Assert.Empty(legacyTool.CheckConfiguration());
    }

    [Fact]
    public void 灰度增强按数字保存_缺省为Linear()
    {
        var tool = new GrayEnhanceTool("增强1") { Method = GrayEnhanceMethod.ConvertType, NewType = ConvertImageNewType.int2, Encode = false, Percent = 2.5 };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"gray-enhance\"", json);
        Assert.Contains("\"Method\": 8", json);
        Assert.Contains("\"NewType\": 2", json);
        Assert.Contains("\"Encode\": false", json);
        var loaded = (GrayEnhanceTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((GrayEnhanceMethod.ConvertType, ConvertImageNewType.int2, false, 2.5), (loaded.Method, loaded.NewType, loaded.Encode, loaded.Percent));

        JsonObject minimal = JsonNode.Parse(json)!.AsObject();
        JsonObject properties = minimal["Tool"]!["Properties"]!.AsObject();
        foreach (string name in properties.Select(p => p.Key).Where(k => k != "ImagePath").ToList())
        {
            properties.Remove(name);
        }
        var defaults = (GrayEnhanceTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(minimal.ToJsonString())).Tool;
        Assert.Equal(GrayEnhanceMethod.Linear, defaults.Method);
        Assert.Equal((1.0, 0.0), (defaults.Mult, defaults.Add));
    }

    [Fact]
    public void 输出只有Image_且不与任何参数同名()
    {
        foreach (Type type in new[] { typeof(MeanImageTool), typeof(GrayEnhanceTool) })
        {
            Assert.Equal(new[] { "Image" }, ToolMetadata.GetOutputs(type).Select(o => o.Name).ToArray());
            Assert.DoesNotContain(type.GetProperties(), p => p.Name == "Image");
            ToolInputRefDef image = Assert.Single(ToolMetadata.GetInputRefs(type));
            Assert.Equal(typeof(HalconImage), image.ExpectedType);
            Assert.Equal("ImagePath", image.PropertyName);
        }
    }

    // ======================= 登记（非 HALCON） =======================

    [Fact]
    public void 工具箱_图像滤波改名_灰度增强登记_模块名()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem filter = ToolboxRegistry.Items.Single(i => i.Id == "mean-image");
        Assert.Equal(("02 图像处理", "图像滤波"), (filter.Category, filter.DisplayName));
        var filterTool = Assert.IsType<MeanImageTool>(Assert.IsType<ToolNode>(filter.Factory()).Tool);
        Assert.StartsWith("图像滤波", filterTool.ModuleName);
        Assert.Equal(ImageFilterMethod.Mean, filterTool.Method);

        ToolboxItem enhance = ToolboxRegistry.Items.Single(i => i.Id == "gray-enhance");
        Assert.Equal(("02 图像处理", "灰度增强"), (enhance.Category, enhance.DisplayName));
        var enhanceTool = Assert.IsType<GrayEnhanceTool>(Assert.IsType<ToolNode>(enhance.Factory()).Tool);
        Assert.StartsWith("灰度增强", enhanceTool.ModuleName);
        Assert.Equal(GrayEnhanceMethod.Linear, enhanceTool.Method);
        Assert.Equal("Input.Image", enhanceTool.ImagePath);
    }

    [Fact]
    public void 图标键与视觉预览路由已登记()
    {
        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        Assert.Contains("x:Key=\"ToolIcon.gray-enhance\"", icons);
        Assert.Contains("x:Key=\"ToolIcon.mean-image\"", icons);

        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int start = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string body = router.Substring(start, router.IndexOf('}', start) - start);
        Assert.Contains("tool is MeanImageTool", body);
        Assert.Contains("tool is GrayEnhanceTool", body);
    }
}
