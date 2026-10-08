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
/// IMAGE-TOOLS 第五批：IP-07 区域转图像（RegionToImageTool）。
/// 标注 Requires=HALCON 的用例与直接调用 region_to_bin / paint_region 对照；其余用例（校验、显隐、序列化、登记）不调用 HALCON 算子。
/// </summary>
public class ImageToolsBatch5Tests
{
    // ======================= 合成图像与对照工具 =======================

    private const int W = 120, H = 90;

    /// <summary>矩形 (10, 10) ~ (29, 39)（面积 600）与圆 (60, 80) 半径 15。</summary>
    private static HObject TwoRegions(out HObject rect, out HObject disc)
    {
        HOperatorSet.GenRectangle1(out rect, 10, 10, 29, 39);
        HOperatorSet.GenCircle(out disc, 60, 80, 15);
        HOperatorSet.ConcatObj(rect, disc, out HObject both);
        return both;
    }

    private static HObject Gradient()
    {
        HOperatorSet.GenImageGrayRamp(out HObject image, 0.5, 0.8, 60, H / 2, W / 2, W, H);
        return image;
    }

    private static HObject Rgb()
    {
        HObject a = Gradient();
        HOperatorSet.InvertImage(a, out HObject b);
        HOperatorSet.ScaleImage(a, out HObject c, 0.5, 30);
        HOperatorSet.Compose3(a, b, c, out HObject rgb);
        return rgb;
    }

    private static HObject Converted(HObject image, string type)
    {
        HOperatorSet.ConvertImageType(image, out HObject converted, type);
        return converted;
    }

    private static void AssertSameImage(HObject expected, HObject actual)
    {
        HOperatorSet.CountObj(expected, out HTuple en);
        HOperatorSet.CountObj(actual, out HTuple an);
        Assert.Equal((1, 1), (en.I, an.I));
        HOperatorSet.GetImageType(expected, out HTuple et);
        HOperatorSet.GetImageType(actual, out HTuple at);
        Assert.Equal(et.S, at.S);
        HOperatorSet.CountChannels(expected, out HTuple ec);
        HOperatorSet.CountChannels(actual, out HTuple ac);
        Assert.Equal(ec.I, ac.I);
        HOperatorSet.GetImageSize(expected, out HTuple ew, out HTuple eh);
        HOperatorSet.GetImageSize(actual, out HTuple aw, out HTuple ah);
        Assert.Equal((ew.I, eh.I), (aw.I, ah.I));
        HOperatorSet.GetDomain(expected, out HObject ed);
        HOperatorSet.GetDomain(actual, out HObject ad);
        HOperatorSet.SymmDifference(ed, ad, out HObject dd);
        HOperatorSet.AreaCenter(dd, out HTuple domainDiff, out _, out _);
        Assert.Equal(0, domainDiff.I);
        // 按整幅矩阵比较（paint_region 会改动定义域外的像素，第 18 节）
        HOperatorSet.FullDomain(expected, out HObject ef);
        HOperatorSet.FullDomain(actual, out HObject af);
        HOperatorSet.GetDomain(ef, out HObject full);
        for (int channel = 1; channel <= ec.I; channel++)
        {
            HOperatorSet.AccessChannel(ef, out HObject e, channel);
            HOperatorSet.AccessChannel(af, out HObject a, channel);
            HOperatorSet.SubImage(Converted(e, "real"), Converted(a, "real"), out HObject diff, 1, 0);
            HOperatorSet.MinMaxGray(full, diff, 0, out HTuple min, out HTuple max, out _);
            Assert.True(min.D == 0 && max.D == 0, $"通道 {channel} 像素差 [{min.D}, {max.D}]");
        }
    }

    /// <summary>after 与 before 第一通道不同的像素（整幅矩阵）。</summary>
    private static HObject Changed(HObject before, HObject after)
    {
        HOperatorSet.FullDomain(before, out HObject b);
        HOperatorSet.FullDomain(after, out HObject a);
        HOperatorSet.AccessChannel(b, out HObject b1, 1);
        HOperatorSet.AccessChannel(a, out HObject a1, 1);
        HOperatorSet.AbsDiffImage(Converted(b1, "real"), Converted(a1, "real"), out HObject diff, 1);
        HOperatorSet.Threshold(diff, out HObject changed, 0.5, 1e9);
        return changed;
    }

    private static int Area(HObject region)
    {
        HOperatorSet.Union1(region, out HObject all);
        HOperatorSet.AreaCenter(all, out HTuple area, out _, out _);
        return area.I;
    }

    private static int SymmArea(HObject a, HObject b)
    {
        HOperatorSet.Union1(a, out HObject ua);
        HOperatorSet.Union1(b, out HObject ub);
        HOperatorSet.SymmDifference(ua, ub, out HObject d);
        return Area(d);
    }

    private static FlowContext Context(HObject region, HObject? image = null)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("区域1", "Region", new HalconRegion(region), 1));
        if (image != null)
        {
            ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        }
        return ctx;
    }

    private static RegionToImageTool Tool(RegionToImageMethod method, string? imagePath = "图像1.Image") =>
        new RegionToImageTool("转图1") { RegionPath = "区域1.Region", ImagePath = imagePath, Method = method };

    private static HObject Run(RegionToImageTool tool, FlowContext ctx)
    {
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ((HalconImage)ctx.GetVariable(tool.ModuleName, "Image").Value).Object;
    }

    // ======================= Binary（门禁） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(255, 0)]
    [InlineData(0, 255)]
    [InlineData(200, 30)]
    public void Binary_取参考图尺寸_前景背景灰度正确_与直接调用region_to_bin逐像素一致(int foreground, int background)
    {
        HObject regions = TwoRegions(out HObject rect, out HObject disc);
        HObject image = Rgb();
        RegionToImageTool tool = Tool(RegionToImageMethod.Binary);
        (tool.ForegroundGray, tool.BackgroundGray, tool.Width, tool.Height) = (foreground, background, 7, 7);
        HObject output = Run(tool, Context(regions, image));

        HOperatorSet.RegionToBin(regions, out HObject expected, foreground, background, W, H);
        AssertSameImage(expected, output);
        HOperatorSet.GetImageType(output, out HTuple type);
        HOperatorSet.CountChannels(output, out HTuple channels);
        Assert.Equal(("byte", 1), (type.S, channels.I));
        // 前景恰为两个区域对象的并集（多对象全部绘制），其余为背景
        HOperatorSet.Threshold(output, out HObject fg, foreground, foreground);
        HOperatorSet.Threshold(output, out HObject bg, background, background);
        Assert.Equal(0, SymmArea(fg, regions));
        Assert.Equal(W * H - Area(regions), Area(bg));
        Assert.True(Area(rect) > 0 && Area(disc) > 0);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void Binary_未配置参考图像时用Width与Height_区域超出截断()
    {
        HOperatorSet.GenRectangle1(out HObject region, -5, 55, 5, 70);
        RegionToImageTool tool = Tool(RegionToImageMethod.Binary, imagePath: null);
        (tool.Width, tool.Height) = (64, 48);
        HObject output = Run(tool, Context(region));
        HOperatorSet.RegionToBin(region, out HObject expected, 255, 0, 64, 48);
        AssertSameImage(expected, output);
        HOperatorSet.Threshold(output, out HObject fg, 255, 255);
        Assert.Equal(6 * 9, Area(fg));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void Binary_空区域与0个对象_输出全背景图()
    {
        HOperatorSet.GenEmptyRegion(out HObject empty);
        HOperatorSet.GenEmptyObj(out HObject none);
        foreach (HObject region in new[] { empty, none })
        {
            RegionToImageTool tool = Tool(RegionToImageMethod.Binary, imagePath: null);
            (tool.BackgroundGray, tool.Width, tool.Height) = (40, 50, 30);
            HObject output = Run(tool, Context(region));
            HOperatorSet.GetImageSize(output, out HTuple w, out HTuple h);
            HOperatorSet.MinMaxGray(output, output, 0, out HTuple min, out HTuple max, out _);
            Assert.Equal((50, 30, 40.0, 40.0), (w.I, h.I, min.D, max.D));
        }
        // 对照：算子对 0 个对象不输出图像，工具按空区域处理
        HOperatorSet.RegionToBin(none, out HObject direct, 255, 40, 50, 30);
        HOperatorSet.CountObj(direct, out HTuple count);
        Assert.Equal(0, count.I);
    }

    // ======================= PaintOnImage（门禁） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(RegionPaintType.fill, "byte", 230.0)]
    [InlineData(RegionPaintType.margin, "byte", 0.0)]
    [InlineData(RegionPaintType.fill, "uint2", 1000.0)]
    [InlineData(RegionPaintType.margin, "real", -12.5)]
    public void PaintOnImage_与直接调用paint_region逐像素一致_只改动区域内或边缘像素(RegionPaintType paintType, string pixelType, double gray)
    {
        HObject regions = TwoRegions(out _, out _);
        HObject image = Converted(Gradient(), pixelType);
        RegionToImageTool tool = Tool(RegionToImageMethod.PaintOnImage);
        (tool.Gray, tool.PaintType) = (gray, paintType);
        HObject output = Run(tool, Context(regions, image));

        HOperatorSet.PaintRegion(regions, image, out HObject expected, gray, paintType.ToString());
        AssertSameImage(expected, output);
        HObject changed = Changed(image, output);
        HOperatorSet.Union1(regions, out HObject union);
        HOperatorSet.Difference(changed, union, out HObject outside);
        Assert.Equal(0, Area(outside));
        if (paintType == RegionPaintType.fill)
        {
            Assert.Equal(0, SymmArea(changed, regions));
        }
        else
        {
            // margin 逐个对象画外轮廓 1 像素边：在每个对象的内边界之内，远小于区域
            HOperatorSet.Boundary(regions, out HObject inner, "inner");
            HOperatorSet.Union1(inner, out HObject innerUnion);
            HOperatorSet.Difference(changed, innerUnion, out HObject notOnBoundary);
            Assert.Equal(0, Area(notOnBoundary));
            Assert.InRange(Area(changed), 1, Area(innerUnion));
        }
        // 参考图不被修改
        HOperatorSet.GetGrayval(image, 15, 20, out HTuple before);
        HOperatorSet.GetGrayval(Converted(Gradient(), pixelType), 15, 20, out HTuple original);
        Assert.Equal(original.D, before.D);
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(RegionPaintType.fill)]
    [InlineData(RegionPaintType.margin)]
    public void PaintOnImage_三通道参考图_同一灰度复制到每个通道(RegionPaintType paintType)
    {
        HObject regions = TwoRegions(out HObject rect, out _);
        HObject image = Rgb();
        RegionToImageTool tool = Tool(RegionToImageMethod.PaintOnImage);
        (tool.Gray, tool.PaintType) = (17, paintType);
        HObject output = Run(tool, Context(regions, image));

        HOperatorSet.PaintRegion(regions, image, out HObject expected, new HTuple(17.0, 17.0, 17.0), paintType.ToString());
        AssertSameImage(expected, output);
        HOperatorSet.CountChannels(output, out HTuple channels);
        Assert.Equal(3, channels.I);
        for (int channel = 1; channel <= 3; channel++)
        {
            HOperatorSet.AccessChannel(output, out HObject one, channel);
            HOperatorSet.GetGrayval(one, 10, 10, out HTuple corner);
            Assert.Equal(17.0, corner.D);
        }
        // 对照：算子对三通道图只传一个灰度报 #1401
        HalconException ex = Assert.ThrowsAny<HalconException>(() => HOperatorSet.PaintRegion(rect, image, out _, 17, paintType.ToString()));
        Assert.Equal(1401, ex.GetErrorCode());
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void PaintOnImage_多个区域对象逐个画边_空区域与0个对象输出参考图副本()
    {
        HOperatorSet.GenRectangle1(out HObject a, 5, 5, 25, 25);
        HOperatorSet.GenRectangle1(out HObject b, 15, 15, 35, 35);
        HOperatorSet.ConcatObj(a, b, out HObject overlapping);
        HObject image = Gradient();
        RegionToImageTool tool = Tool(RegionToImageMethod.PaintOnImage);
        (tool.Gray, tool.PaintType) = (255, RegionPaintType.margin);
        HObject output = Run(tool, Context(overlapping, image));
        HOperatorSet.PaintRegion(overlapping, image, out HObject expected, 255, "margin");
        AssertSameImage(expected, output);
        HOperatorSet.Union1(overlapping, out HObject union);
        HOperatorSet.PaintRegion(union, image, out HObject unionMargin, 255, "margin");
        Assert.True(Area(Changed(image, output)) > Area(Changed(image, unionMargin)), "两个对象应逐个画边，而不是只画并集外轮廓");

        HOperatorSet.GenEmptyRegion(out HObject empty);
        HOperatorSet.GenEmptyObj(out HObject none);
        foreach (HObject region in new[] { empty, none })
        {
            RegionToImageTool paint = Tool(RegionToImageMethod.PaintOnImage);
            HObject copy = Run(paint, Context(region, image));
            AssertSameImage(image, copy);
            Assert.NotSame(image, copy);
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void PaintOnImage_缩小定义域的参考图_输出保留定义域且定义域外像素也绘制()
    {
        HObject regions = TwoRegions(out _, out _);
        HOperatorSet.GenRectangle1(out HObject part, 0, 0, 40, 60);
        HOperatorSet.ReduceDomain(Gradient(), part, out HObject image);
        RegionToImageTool tool = Tool(RegionToImageMethod.PaintOnImage);
        tool.Gray = 250;
        HObject output = Run(tool, Context(regions, image));
        HOperatorSet.PaintRegion(regions, image, out HObject expected, 250, "fill");
        AssertSameImage(expected, output);
        HOperatorSet.GetDomain(output, out HObject domain);
        Assert.Equal(0, SymmArea(domain, part));
        // 圆 (60, 80) 完全在定义域外，仍被绘制
        HOperatorSet.FullDomain(output, out HObject full);
        HOperatorSet.GetGrayval(full, 60, 80, out HTuple centre);
        Assert.Equal(250.0, centre.D);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 运行时检查_灰度超出像素类型范围_多幅参考图()
    {
        HObject regions = TwoRegions(out _, out _);
        HObject image = Gradient();
        RegionToImageTool tool = Tool(RegionToImageMethod.PaintOnImage);
        tool.Gray = 300;
        NodeResult result = tool.Run(Context(regions, image));
        Assert.False(result.IsSuccess);
        Assert.Equal("转图1 Gray：绘制灰度 Gray = 300 超出参考图像像素类型 byte 的取值范围 0 ~ 255", result.Message);

        tool.Gray = -1;
        Assert.Equal("转图1 Gray：绘制灰度 Gray = -1 超出参考图像像素类型 byte 的取值范围 0 ~ 255", tool.Run(Context(regions, image)).Message);

        tool.Gray = 70000;
        Assert.Equal("转图1 Gray：绘制灰度 Gray = 70000 超出参考图像像素类型 uint2 的取值范围 0 ~ 65535", tool.Run(Context(regions, Converted(image, "uint2"))).Message);
        Assert.True(tool.Run(Context(regions, Converted(image, "real"))).IsSuccess);

        HOperatorSet.ConcatObj(image, image, out HObject two);
        foreach (RegionToImageMethod method in Enum.GetValues<RegionToImageMethod>())
        {
            NodeResult multi = Tool(method).Run(Context(regions, two));
            Assert.Equal("转图1 ImagePath：参考图像只支持单幅图像（图像1.Image 当前为 2 幅）", multi.Message);
        }
    }

    // ======================= 校验、显隐、序列化、登记（非 HALCON） =======================

    public static IEnumerable<object[]> RangeCases()
    {
        yield return new object[] { (Action<RegionToImageTool>)(t => t.RegionPath = " "), "RegionPath", "需要配置区域" };
        yield return new object[] { (Action<RegionToImageTool>)(t => t.ForegroundGray = 256), "ForegroundGray", "前景灰度 ForegroundGray 必须在 0 ~ 255 之间（当前 256）" };
        yield return new object[] { (Action<RegionToImageTool>)(t => t.ForegroundGray = -1), "ForegroundGray", "前景灰度 ForegroundGray 必须在 0 ~ 255 之间（当前 -1）" };
        yield return new object[] { (Action<RegionToImageTool>)(t => t.BackgroundGray = 300), "BackgroundGray", "背景灰度 BackgroundGray 必须在 0 ~ 255 之间（当前 300）" };
        yield return new object[] { (Action<RegionToImageTool>)(t => { t.ImagePath = null; t.Width = 0; }), "Width", "未配置参考图像时输出宽度 Width 必须在 1 ~ 32768 之间（当前 0）" };
        yield return new object[] { (Action<RegionToImageTool>)(t => { t.ImagePath = ""; t.Height = 32769; }), "Height", "未配置参考图像时输出高度 Height 必须在 1 ~ 32768 之间（当前 32769）" };
        yield return new object[] { (Action<RegionToImageTool>)(t => { t.Method = RegionToImageMethod.PaintOnImage; t.ImagePath = null; }), "ImagePath", "PaintOnImage 方式需要配置参考图像" };
        yield return new object[] { (Action<RegionToImageTool>)(t => { t.Method = RegionToImageMethod.PaintOnImage; t.Gray = double.NaN; }), "Gray", "绘制灰度 Gray 必须是有限数（当前 NaN）" };
        yield return new object[] { (Action<RegionToImageTool>)(t => { t.Method = RegionToImageMethod.PaintOnImage; t.Gray = double.NegativeInfinity; }), "Gray", "绘制灰度 Gray 必须是有限数（当前 -Infinity）" };
        yield return new object[] { (Action<RegionToImageTool>)(t => { t.Method = RegionToImageMethod.PaintOnImage; t.PaintType = (RegionPaintType)5; }), "PaintType", "绘制方式 PaintType 只能是 fill / margin（当前 5）" };
        yield return new object[] { (Action<RegionToImageTool>)(t => t.Method = (RegionToImageMethod)9), "Method", "未知的区域转图像方式 9" };
    }

    [Theory]
    [MemberData(nameof(RangeCases))]
    public void 参数越界_流程校验与运行措辞一致(Action<RegionToImageTool> configure, string parameter, string message)
    {
        RegionToImageTool tool = Tool(RegionToImageMethod.Binary);
        configure(tool);
        ToolConfigurationIssue issue = Assert.Single(tool.CheckConfiguration());
        Assert.Equal((parameter, message), (issue.Parameter, issue.Message));
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"转图1 {parameter}：{message}", result.Message);
    }

    [Fact]
    public void 参考图像与Width_Height互斥_只校验当前方式()
    {
        // Binary 配置了参考图像时尺寸取参考图，Width / Height 不使用、不校验
        Assert.Empty(new RegionToImageTool("转图1") { RegionPath = "区域1.Region", Width = 0, Height = -3 }.CheckConfiguration());
        Assert.Equal(new[] { "Width", "Height" },
            new RegionToImageTool("转图1") { RegionPath = "区域1.Region", ImagePath = null, Width = 0, Height = -3 }.CheckConfiguration().Select(i => i.Parameter).ToArray());
        // Binary 不校验绘制参数；PaintOnImage 不校验二值参数与尺寸
        Assert.Empty(new RegionToImageTool("转图1") { RegionPath = "区域1.Region", Gray = double.NaN, PaintType = (RegionPaintType)7 }.CheckConfiguration());
        Assert.Empty(new RegionToImageTool("转图1")
        {
            RegionPath = "区域1.Region",
            Method = RegionToImageMethod.PaintOnImage,
            ForegroundGray = 999,
            BackgroundGray = -9,
            Width = 0,
            Gray = -12.5
        }.CheckConfiguration());
        // 默认参数：只缺区域
        Assert.Equal(new[] { ("RegionPath", "需要配置区域") }, new RegionToImageTool("转图1").CheckConfiguration().Select(i => (i.Parameter, i.Message)).ToArray());
        Assert.Equal("转图1 RegionPath：需要配置区域", new RegionToImageTool("转图1").Run(new FlowContext()).Message);
    }

    [Theory]
    [InlineData(RegionToImageMethod.Binary, "图像1.Image", new[] { "ForegroundGray", "BackgroundGray", "Width", "Height" }, new[] { "Gray", "PaintType" })]
    [InlineData(RegionToImageMethod.Binary, null, new[] { "ForegroundGray", "BackgroundGray", "Width", "Height" }, new[] { "Gray", "PaintType" })]
    [InlineData(RegionToImageMethod.PaintOnImage, "图像1.Image", new[] { "Gray", "PaintType" }, new[] { "ForegroundGray", "BackgroundGray", "Width", "Height" })]
    [InlineData(RegionToImageMethod.PaintOnImage, null, new[] { "Gray", "PaintType" }, new[] { "ForegroundGray", "BackgroundGray", "Width", "Height" })]
    public void 按方式与参考图像精确显隐(RegionToImageMethod method, string? imagePath, string[] visible, string[] hidden)
    {
        var tool = new RegionToImageTool("转图1") { Method = method, ImagePath = imagePath };
        foreach (string name in visible.Concat(new[] { "RegionPath", "ImagePath", "Method" }))
        {
            Assert.True(tool.IsParameterVisible(name), name);
        }
        foreach (string name in hidden)
        {
            Assert.False(tool.IsParameterVisible(name), name);
        }
    }

    [Fact]
    public void 枚举按数字保存_成员顺序_缺省值()
    {
        Assert.Equal(new[] { "Binary", "PaintOnImage" }, Enum.GetNames<RegionToImageMethod>());
        Assert.Equal(new[] { "fill", "margin" }, Enum.GetNames<RegionPaintType>());
        Assert.Equal((0, 1), ((int)RegionPaintType.fill, (int)RegionPaintType.margin));
        var tool = new RegionToImageTool("转图1")
        {
            RegionPath = "区域1.Region",
            Method = RegionToImageMethod.PaintOnImage,
            PaintType = RegionPaintType.margin,
            Gray = 12.5,
            ForegroundGray = 200,
            Width = 640
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"region-to-image\"", json);
        Assert.Contains("\"Method\": 1", json);
        Assert.Contains("\"PaintType\": 1", json);
        var loaded = (RegionToImageTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((RegionToImageMethod.PaintOnImage, RegionPaintType.margin, 12.5, 200, 640, "区域1.Region", "Input.Image"),
            (loaded.Method, loaded.PaintType, loaded.Gray, loaded.ForegroundGray, loaded.Width, loaded.RegionPath, loaded.ImagePath));

        JsonObject minimal = JsonNode.Parse(json)!.AsObject();
        JsonObject properties = minimal["Tool"]!["Properties"]!.AsObject();
        foreach (string name in properties.Select(p => p.Key).Where(k => k != "RegionPath").ToList())
        {
            properties.Remove(name);
        }
        var defaults = (RegionToImageTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(minimal.ToJsonString())).Tool;
        Assert.Equal((RegionToImageMethod.Binary, RegionPaintType.fill, 255, 0, 512, 512, 255.0, "Input.Image"),
            (defaults.Method, defaults.PaintType, defaults.ForegroundGray, defaults.BackgroundGray, defaults.Width, defaults.Height, defaults.Gray, defaults.ImagePath));
    }

    [Fact]
    public void 输出名与类型_不与参数同名_输入元数据()
    {
        var outputs = ToolMetadata.GetOutputs(typeof(RegionToImageTool)).ToList();
        Assert.Equal(new[] { ("Image", typeof(HalconImage)) }, outputs.Select(o => (o.Name, o.ElementClrType)).ToArray());
        var parameters = typeof(RegionToImageTool).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain(outputs, o => parameters.Contains(o.Name));
        Assert.Equal(new[] { ("RegionPath", typeof(HalconRegion), false), ("ImagePath", typeof(HalconImage), true) },
            ToolMetadata.GetInputRefs(typeof(RegionToImageTool)).Select(i => (i.PropertyName, i.ExpectedType, i.Optional)).ToArray());
    }

    [Fact]
    public void 工具箱登记_图标键_视觉预览路由()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "region-to-image");
        Assert.Equal(("02 图像处理", "区域转图像"), (item.Category, item.DisplayName));
        Assert.StartsWith("区域转图像", Assert.IsType<RegionToImageTool>(Assert.IsType<ToolNode>(item.Factory()).Tool).ModuleName);

        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        Assert.Contains("x:Key=\"ToolIcon.region-to-image\"", icons);
        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int start = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        string preview = router.Substring(start, router.IndexOf('}', start) - start);
        Assert.Contains("tool is RegionToImageTool", preview);
    }
}
