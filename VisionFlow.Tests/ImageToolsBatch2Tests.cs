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
/// IMAGE-TOOLS 第二批：IP-08 阈值分割颜色方式（ColorHsv / ColorRgb）+ IP-02 图像运算（原图像加减）。
/// 标注 Requires=HALCON 的用例与直接调用 HALCON 算子对照；其余用例（校验、显隐、序列化、登记）不调用 HALCON 算子。
/// </summary>
public class ImageToolsBatch2Tests
{
    // ======================= 合成色块图与对照工具 =======================

    /// <summary>色块之间留 10 列黑色间隔，便于 connection 分开。</summary>
    private static readonly (string Name, int R, int G, int B)[] PatchColors =
    {
        ("red", 255, 0, 0),
        ("green", 0, 255, 0),
        ("blue", 0, 0, 255),
        ("yellow", 255, 255, 0),
        ("wrapRed", 255, 0, 30),
        ("gray", 128, 128, 128),
        ("white", 255, 255, 255)
    };

    private static int PatchColumn(int index) => 10 + index * 30;

    private static HObject PatchImage()
    {
        int width = 10 + PatchColors.Length * 30;
        HOperatorSet.GenImageConst(out HObject r, "byte", width, 40);
        HOperatorSet.GenImageConst(out HObject g, "byte", width, 40);
        HOperatorSet.GenImageConst(out HObject b, "byte", width, 40);
        for (int i = 0; i < PatchColors.Length; i++)
        {
            HOperatorSet.GenRectangle1(out HObject rect, 10, PatchColumn(i), 29, PatchColumn(i) + 19);
            HOperatorSet.PaintRegion(rect, r, out HObject r2, PatchColors[i].R, "fill"); r.Dispose(); r = r2;
            HOperatorSet.PaintRegion(rect, g, out HObject g2, PatchColors[i].G, "fill"); g.Dispose(); g = g2;
            HOperatorSet.PaintRegion(rect, b, out HObject b2, PatchColors[i].B, "fill"); b.Dispose(); b = b2;
            rect.Dispose();
        }
        HOperatorSet.Compose3(r, g, b, out HObject rgb);
        return rgb;
    }

    private static HObject PatchRegion(params string[] names)
    {
        HOperatorSet.GenEmptyRegion(out HObject all);
        foreach (string name in names)
        {
            int i = Array.FindIndex(PatchColors, c => c.Name == name);
            HOperatorSet.GenRectangle1(out HObject rect, 10, PatchColumn(i), 29, PatchColumn(i) + 19);
            HOperatorSet.Union2(all, rect, out HObject merged);
            all.Dispose();
            rect.Dispose();
            all = merged;
        }
        return all;
    }

    private static void AssertSameRegion(HObject expected, HObject actual)
    {
        HOperatorSet.Union1(expected, out HObject e);
        HOperatorSet.Union1(actual, out HObject a);
        HOperatorSet.SymmDifference(e, a, out HObject diff);
        HOperatorSet.AreaCenter(diff, out HTuple diffArea, out _, out _);
        HOperatorSet.AreaCenter(e, out HTuple area, out _, out _);
        Assert.True(diffArea.I == 0, $"区域相差 {diffArea.I} 像素（期望面积 {area.I}）");
    }

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

    private static HObject Inverted(HObject image)
    {
        HOperatorSet.InvertImage(image, out HObject inverted);
        return inverted;
    }

    private static HObject Rgb(HObject a)
    {
        HOperatorSet.InvertImage(a, out HObject b);
        HOperatorSet.ScaleImage(a, out HObject c, 0.5, 30);
        HOperatorSet.Compose3(a, b, c, out HObject rgb);
        return rgb;
    }

    private static HObject Reduced(HObject image)
    {
        HOperatorSet.GenRectangle1(out HObject rect, 5, 6, 40, 50);
        HOperatorSet.ReduceDomain(image, rect, out HObject reduced);
        rect.Dispose();
        return reduced;
    }

    private static void AssertSameImage(HObject expected, HObject actual)
    {
        HOperatorSet.GetImageType(expected, out HTuple et);
        HOperatorSet.GetImageType(actual, out HTuple at);
        Assert.Equal(et.S, at.S);
        HOperatorSet.CountChannels(expected, out HTuple ec);
        HOperatorSet.CountChannels(actual, out HTuple ac);
        Assert.Equal(ec.I, ac.I);
        HOperatorSet.GetDomain(expected, out HObject ed);
        HOperatorSet.GetDomain(actual, out HObject ad);
        HOperatorSet.SymmDifference(ed, ad, out HObject dd);
        HOperatorSet.AreaCenter(dd, out HTuple domainDiff, out _, out _);
        Assert.Equal(0, domainDiff.I);
        for (int channel = 1; channel <= ec.I; channel++)
        {
            HOperatorSet.AccessChannel(expected, out HObject e, channel);
            HOperatorSet.AccessChannel(actual, out HObject a, channel);
            HOperatorSet.SubImage(Converted(e, "real"), Converted(a, "real"), out HObject diff, 1, 0);
            HOperatorSet.MinMaxGray(ed, diff, 0, out HTuple min, out HTuple max, out _);
            Assert.True(min.D == 0 && max.D == 0, $"通道 {channel} 像素差 [{min.D}, {max.D}]");
        }
    }

    private static FlowContext ContextWith(HObject image1, HObject? image2 = null)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image1), 1));
        if (image2 != null)
        {
            ctx.SetVariable(Variable.Object("图像2", "Image", new HalconImage(image2), 1));
        }
        return ctx;
    }

    private static ThresholdTool Color(ThresholdSegmentMethod method) =>
        new ThresholdTool("颜色1") { ImagePath = "图像1.Image", SegmentMethod = method };

    private static AddSubImageTool Arithmetic(ImageArithmeticOperation operation) =>
        new AddSubImageTool("运算1") { ImagePath1 = "图像1.Image", ImagePath2 = "图像2.Image", Operation = operation };

    // ======================= IP-08 颜色方式（门禁） =======================

    /// <summary>直接调用算子的对照实现：decompose3 →（HSV 时 trans_from_rgb）→ 各通道 threshold（色相跨 0 分两段并集）→ intersection。</summary>
    private static HObject DirectColorRegion(HObject image, bool hsv, int[] mins, int[] maxs)
    {
        HOperatorSet.Decompose3(image, out HObject c1, out HObject c2, out HObject c3);
        HObject[] channels = { c1, c2, c3 };
        if (hsv)
        {
            HOperatorSet.TransFromRgb(c1, c2, c3, out HObject h, out HObject s, out HObject v, "hsv");
            channels = new[] { h, s, v };
        }
        HObject? result = null;
        for (int i = 0; i < 3; i++)
        {
            HObject region;
            if (mins[i] <= maxs[i])
            {
                HOperatorSet.Threshold(channels[i], out region, mins[i], maxs[i]);
            }
            else
            {
                HOperatorSet.Threshold(channels[i], out HObject high, mins[i], 255);
                HOperatorSet.Threshold(channels[i], out HObject low, 0, maxs[i]);
                HOperatorSet.Union2(high, low, out region);
            }
            if (result == null)
            {
                result = region;
            }
            else
            {
                HOperatorSet.Intersection(result, region, out HObject next);
                result = next;
            }
        }
        return result!;
    }

    private static HObject RunColor(ThresholdTool tool, HObject image, out FlowContext ctx)
    {
        ctx = ContextWith(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ((HalconRegion)ctx.GetVariable(tool.ModuleName, "Region").Value).Object;
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void ColorHsv_黄色块_只提取黄色块_与直接调用一致_UsedThreshold为NaN()
    {
        HObject image = PatchImage();
        ThresholdTool tool = Color(ThresholdSegmentMethod.ColorHsv);
        (tool.HueMin, tool.HueMax, tool.SaturationMin, tool.SaturationMax, tool.ValueMin, tool.ValueMax) = (30, 60, 100, 255, 100, 255);

        HObject region = RunColor(tool, image, out FlowContext ctx);
        AssertSameRegion(PatchRegion("yellow"), region);
        AssertSameRegion(DirectColorRegion(image, true, new[] { 30, 100, 100 }, new[] { 60, 255, 255 }), region);
        Assert.True(double.IsNaN((double)ctx.GetVariable("颜色1", "UsedThreshold").Value));
        Assert.Equal(1, ctx.GetVariable("颜色1", "Count").Value);
        Assert.Equal(true, ctx.GetVariable("颜色1", "Found").Value);
        Assert.Contains(ctx.StructuredLogs, l => l.Message.StartsWith("[颜色阈值 HSV] 色相 [30, 60], 饱和度 [100, 255], 明度 [100, 255]"));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void ColorHsv_色相跨0_红色与偏蓝红色块一起提取_Connection拆成两块()
    {
        HObject image = PatchImage();
        ThresholdTool tool = Color(ThresholdSegmentMethod.ColorHsv);
        (tool.HueMin, tool.HueMax, tool.SaturationMin, tool.SaturationMax, tool.ValueMin, tool.ValueMax) = (230, 20, 100, 255, 100, 255);

        HObject merged = RunColor(tool, image, out FlowContext ctx);
        AssertSameRegion(PatchRegion("red", "wrapRed"), merged);
        AssertSameRegion(DirectColorRegion(image, true, new[] { 230, 100, 100 }, new[] { 20, 255, 255 }), merged);
        Assert.Equal(1, ctx.GetVariable("颜色1", "Count").Value);
        Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("色相 [230, 20]（跨 0）"));

        tool.Connection = true;
        HObject split = RunColor(tool, image, out FlowContext splitCtx);
        Assert.Equal(2, splitCtx.GetVariable("颜色1", "Count").Value);
        AssertSameRegion(PatchRegion("red", "wrapRed"), split);

        // 不跨 0 的同一色相上限只得到纯红
        tool.Connection = false;
        tool.HueMin = 0;
        AssertSameRegion(PatchRegion("red"), RunColor(tool, image, out _));
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData("green", 0, 50, 200, 255, 0, 50)]
    [InlineData("blue", 0, 50, 0, 50, 200, 255)]
    [InlineData("yellow", 200, 255, 200, 255, 0, 50)]
    public void ColorRgb_只提取对应色块_与直接调用一致(string patch, int rMin, int rMax, int gMin, int gMax, int bMin, int bMax)
    {
        HObject image = PatchImage();
        ThresholdTool tool = Color(ThresholdSegmentMethod.ColorRgb);
        (tool.RedMin, tool.RedMax, tool.GreenMin, tool.GreenMax, tool.BlueMin, tool.BlueMax) = (rMin, rMax, gMin, gMax, bMin, bMax);

        HObject region = RunColor(tool, image, out FlowContext ctx);
        AssertSameRegion(PatchRegion(patch), region);
        AssertSameRegion(DirectColorRegion(image, false, new[] { rMin, gMin, bMin }, new[] { rMax, gMax, bMax }), region);
        Assert.True(double.IsNaN((double)ctx.GetVariable("颜色1", "UsedThreshold").Value));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色方式_非三通道或非byte输入给出中文错误_空结果语义与灰度阈值相同()
    {
        HObject gray = ByteImage();
        HOperatorSet.Compose2(gray, gray, out HObject two);
        HOperatorSet.Compose4(gray, gray, gray, gray, out HObject four);
        foreach (ThresholdSegmentMethod method in new[] { ThresholdSegmentMethod.ColorHsv, ThresholdSegmentMethod.ColorRgb })
        {
            Assert.Equal("颜色1 颜色阈值需要三通道彩色图像（当前 1 通道）", Color(method).Run(ContextWith(gray)).Message);
            Assert.Equal("颜色1 颜色阈值需要三通道彩色图像（当前 2 通道）", Color(method).Run(ContextWith(two)).Message);
            Assert.Equal("颜色1 颜色阈值需要三通道彩色图像（当前 4 通道）", Color(method).Run(ContextWith(four)).Message);
            Assert.Equal("颜色1 颜色阈值需要 byte 彩色图像（当前 uint2），可先接灰度增强的 ConvertType 转成 byte",
                Color(method).Run(ContextWith(Converted(PatchImage(), "uint2"))).Message);
        }

        ThresholdTool none = Color(ThresholdSegmentMethod.ColorHsv);
        (none.HueMin, none.HueMax, none.SaturationMin) = (100, 110, 200);
        // 既有语义（与灰度阈值相同）：不拆连通域时空结果仍是 1 个空区域对象（HALCON store_empty_region 默认 true），Found 为 true
        var emptyCtx = ContextWith(PatchImage());
        Assert.True(none.Run(emptyCtx).IsSuccess);
        HOperatorSet.AreaCenter(((HalconRegion)emptyCtx.GetVariable("颜色1", "Region").Value).Object, out HTuple emptyArea, out _, out _);
        Assert.Equal((1, 0), ((int)emptyCtx.GetVariable("颜色1", "Count").Value, emptyArea.I));
        var grayTool = new ThresholdTool("阈值1") { ImagePath = "图像1.Image", MinGray = 255, MaxGray = 255 };
        var grayCtx = ContextWith(ByteImage());
        Assert.True(grayTool.Run(grayCtx).IsSuccess);
        Assert.Equal(1, grayCtx.GetVariable("阈值1", "Count").Value);

        // 拆连通域后同样是 1 个空区域（22.11 的 connection 对空区域也返回 1 个空对象）：
        // RegionOutput 按对象数判断未找到的既有问题已另行记录（REVIEW-FIX-PROGRESS 2026-10-08 已知边界），本批不改
        none.Connection = true;
        var connectedCtx = ContextWith(PatchImage());
        Assert.True(none.Run(connectedCtx).IsSuccess);
        Assert.Equal(1, connectedCtx.GetVariable("颜色1", "Count").Value);
        Assert.Equal(true, connectedCtx.GetVariable("颜色1", "Found").Value);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 原有阈值方式不受影响()
    {
        HObject image = ByteImage();
        var tool = new ThresholdTool("阈值1") { ImagePath = "图像1.Image", MinGray = 100, MaxGray = 200 };
        HOperatorSet.Threshold(image, out HObject expected, 100, 200);
        var ctx = ContextWith(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        AssertSameRegion(expected, ((HalconRegion)ctx.GetVariable("阈值1", "Region").Value).Object);
        Assert.False(ctx.TryGetVariable("阈值1", "UsedThreshold", out _));
    }

    // ======================= IP-08 校验与显隐（非 HALCON） =======================

    public static IEnumerable<object[]> ColorRangeCases()
    {
        foreach ((string name, Action<ThresholdTool, int> set, ThresholdSegmentMethod method, string label, bool isMin) in new (string, Action<ThresholdTool, int>, ThresholdSegmentMethod, string, bool)[]
        {
            ("HueMin", (t, v) => t.HueMin = v, ThresholdSegmentMethod.ColorHsv, "色相下限", true),
            ("HueMax", (t, v) => t.HueMax = v, ThresholdSegmentMethod.ColorHsv, "色相上限", false),
            ("SaturationMin", (t, v) => t.SaturationMin = v, ThresholdSegmentMethod.ColorHsv, "饱和度下限", true),
            ("SaturationMax", (t, v) => t.SaturationMax = v, ThresholdSegmentMethod.ColorHsv, "饱和度上限", false),
            ("ValueMin", (t, v) => t.ValueMin = v, ThresholdSegmentMethod.ColorHsv, "明度下限", true),
            ("ValueMax", (t, v) => t.ValueMax = v, ThresholdSegmentMethod.ColorHsv, "明度上限", false),
            ("RedMin", (t, v) => t.RedMin = v, ThresholdSegmentMethod.ColorRgb, "红色下限", true),
            ("RedMax", (t, v) => t.RedMax = v, ThresholdSegmentMethod.ColorRgb, "红色上限", false),
            ("GreenMin", (t, v) => t.GreenMin = v, ThresholdSegmentMethod.ColorRgb, "绿色下限", true),
            ("GreenMax", (t, v) => t.GreenMax = v, ThresholdSegmentMethod.ColorRgb, "绿色上限", false),
            ("BlueMin", (t, v) => t.BlueMin = v, ThresholdSegmentMethod.ColorRgb, "蓝色下限", true),
            ("BlueMax", (t, v) => t.BlueMax = v, ThresholdSegmentMethod.ColorRgb, "蓝色上限", false)
        })
        {
            // 下限取 256、上限取 −1 时还会同时违反“下限不能大于上限”，因此下限用 −1、上限用 256
            yield return new object[] { method, (Action<ThresholdTool>)(t => set(t, isMin ? -1 : 256)), name, $"{label} {name} 必须在 0 ~ 255 之间" };
        }
    }

    [Theory]
    [MemberData(nameof(ColorRangeCases))]
    public void 颜色通道范围越界_流程校验与运行措辞一致(ThresholdSegmentMethod method, Action<ThresholdTool> configure, string parameter, string message)
    {
        ThresholdTool tool = Color(method);
        configure(tool);
        ToolConfigurationIssue issue = Assert.Single(tool.CheckConfiguration());
        Assert.Equal((parameter, message), (issue.Parameter, issue.Message));
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"颜色1 {parameter}：{message}", result.Message);
    }

    [Theory]
    [InlineData(ThresholdSegmentMethod.ColorHsv, "Saturation", "饱和度")]
    [InlineData(ThresholdSegmentMethod.ColorHsv, "Value", "明度")]
    [InlineData(ThresholdSegmentMethod.ColorRgb, "Red", "红色")]
    [InlineData(ThresholdSegmentMethod.ColorRgb, "Green", "绿色")]
    [InlineData(ThresholdSegmentMethod.ColorRgb, "Blue", "蓝色")]
    public void 下限大于上限_色相以外报错_流程校验与运行措辞一致(ThresholdSegmentMethod method, string channel, string label)
    {
        ThresholdTool tool = Color(method);
        typeof(ThresholdTool).GetProperty(channel + "Min")!.SetValue(tool, 200);
        typeof(ThresholdTool).GetProperty(channel + "Max")!.SetValue(tool, 100);
        ToolConfigurationIssue issue = Assert.Single(tool.CheckConfiguration());
        Assert.Equal(($"{channel}Min / {channel}Max", $"{label}下限不能大于上限"), (issue.Parameter, issue.Message));
        Assert.Equal($"颜色1 {channel}Min / {channel}Max：{label}下限不能大于上限", tool.Run(new FlowContext()).Message);

        ThresholdTool hue = Color(ThresholdSegmentMethod.ColorHsv);
        (hue.HueMin, hue.HueMax) = (230, 20);
        Assert.Empty(hue.CheckConfiguration());
    }

    [Fact]
    public void 颜色参数只在对应方式校验与显示()
    {
        var hsvNames = new[] { "HueMin", "HueMax", "SaturationMin", "SaturationMax", "ValueMin", "ValueMax" };
        var rgbNames = new[] { "RedMin", "RedMax", "GreenMin", "GreenMax", "BlueMin", "BlueMax" };
        foreach (ThresholdSegmentMethod method in Enum.GetValues<ThresholdSegmentMethod>())
        {
            var tool = new ThresholdTool("阈值1") { SegmentMethod = method, RedMin = 300, HueMax = -5, SaturationMin = 200, SaturationMax = 10 };
            Assert.All(hsvNames, n => Assert.Equal(method == ThresholdSegmentMethod.ColorHsv, tool.IsParameterVisible(n)));
            Assert.All(rgbNames, n => Assert.Equal(method == ThresholdSegmentMethod.ColorRgb, tool.IsParameterVisible(n)));
            bool colorIssues = tool.CheckConfiguration().Any(i => hsvNames.Concat(rgbNames).Any(n => i.Parameter.Contains(n)));
            Assert.Equal(method == ThresholdSegmentMethod.ColorHsv || method == ThresholdSegmentMethod.ColorRgb, colorIssues);
            if (method == ThresholdSegmentMethod.ColorHsv || method == ThresholdSegmentMethod.ColorRgb)
            {
                foreach (string hidden in new[] { "MinGray", "MaxGray", "MinSize", "Sigma", "Percent", "BinaryMethod", "LightDark", "MaskWidth", "MaskHeight", "StdDevScale", "AbsThreshold", "Offset" })
                {
                    Assert.False(tool.IsParameterVisible(hidden), hidden);
                }
                Assert.True(tool.IsParameterVisible("Connection"));
                Assert.True(tool.IsParameterVisible("FailWhenNotFound"));
            }
        }
    }

    [Fact]
    public void 阈值方式枚举追加在末尾_按数字保存_历史文件缺省全范围()
    {
        Assert.Equal(new[] { "Threshold", "AutoThreshold", "BinaryThreshold", "FastThreshold", "CharThreshold", "VarThreshold", "DynThreshold", "ColorHsv", "ColorRgb" },
            Enum.GetNames<ThresholdSegmentMethod>());
        var tool = new ThresholdTool("颜色1") { SegmentMethod = ThresholdSegmentMethod.ColorHsv, HueMin = 230, HueMax = 20, Connection = true };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"threshold\"", json);
        Assert.Contains("\"SegmentMethod\": 7", json);
        Assert.Contains("\"HueMin\": 230", json);
        var loaded = (ThresholdTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((ThresholdSegmentMethod.ColorHsv, 230, 20, true), (loaded.SegmentMethod, loaded.HueMin, loaded.HueMax, loaded.Connection));

        JsonObject legacy = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new ThresholdTool("阈值1") { MinGray = 90 })))!.AsObject();
        JsonObject properties = legacy["Tool"]!["Properties"]!.AsObject();
        foreach (string name in new[] { "HueMin", "HueMax", "SaturationMin", "SaturationMax", "ValueMin", "ValueMax", "RedMin", "RedMax", "GreenMin", "GreenMax", "BlueMin", "BlueMax" })
        {
            Assert.True(properties.Remove(name), name);
        }
        var old = (ThresholdTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy.ToJsonString())).Tool;
        Assert.Equal((ThresholdSegmentMethod.Threshold, 90.0), (old.SegmentMethod, old.MinGray));
        Assert.Equal((0, 255, 0, 255, 0, 255), (old.HueMin, old.HueMax, old.SaturationMin, old.SaturationMax, old.ValueMin, old.ValueMax));
        Assert.Equal((0, 255, 0, 255, 0, 255), (old.RedMin, old.RedMax, old.GreenMin, old.GreenMax, old.BlueMin, old.BlueMax));
    }

    // ======================= IP-02 图像运算（门禁） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(ImageArithmeticOperation.Mult)]
    [InlineData(ImageArithmeticOperation.Div)]
    [InlineData(ImageArithmeticOperation.AbsDiff)]
    [InlineData(ImageArithmeticOperation.Max)]
    [InlineData(ImageArithmeticOperation.Min)]
    public void 新增五种运算与直接调用逐像素一致_含彩色图与缩小定义域(ImageArithmeticOperation operation)
    {
        AddSubImageTool tool = Arithmetic(operation);
        tool.Multi = operation == ImageArithmeticOperation.Mult ? 0.01 : operation == ImageArithmeticOperation.Div ? 50 : 2;
        tool.Add = 3;
        HObject a = ByteImage();
        foreach ((HObject first, HObject second) in new[] { (a, Inverted(a)), (Rgb(a), Rgb(Inverted(a))), (Reduced(a), Inverted(a)), (Converted(a, "real"), Converted(Inverted(a), "real")) })
        {
            HObject expected;
            switch (operation)
            {
                case ImageArithmeticOperation.Mult: HOperatorSet.MultImage(first, second, out expected, 0.01, 3); break;
                case ImageArithmeticOperation.Div: HOperatorSet.DivImage(first, second, out expected, 50, 3); break;
                case ImageArithmeticOperation.AbsDiff: HOperatorSet.AbsDiffImage(first, second, out expected, 2); break;
                case ImageArithmeticOperation.Max: HOperatorSet.MaxImage(first, second, out expected); break;
                default: HOperatorSet.MinImage(first, second, out expected); break;
            }
            var ctx = ContextWith(first, second);
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, result.Message);
            HObject actual = ((HalconImage)ctx.GetVariable("运算1", "Image").Value).Object;
            AssertSameImage(expected, actual);
            Assert.NotSame(first, actual);
            Assert.True(first.IsInitialized() && second.IsInitialized());
        }
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(ImageArithmeticOperation.Add)]
    [InlineData(ImageArithmeticOperation.Sub)]
    public void 加减运算行为与日志不变(ImageArithmeticOperation operation)
    {
        HObject a = ByteImage();
        HObject b = Inverted(a);
        var tool = new AddSubImageTool("加减1") { ImagePath1 = "图像1.Image", ImagePath2 = "图像2.Image", Operation = operation };
        HObject expected;
        if (operation == ImageArithmeticOperation.Add)
        {
            HOperatorSet.AddImage(a, b, out expected, 1, 128);
        }
        else
        {
            HOperatorSet.SubImage(a, b, out expected, 1, 128);
        }
        var ctx = ContextWith(a, b);
        Assert.True(tool.Run(ctx).IsSuccess);
        AssertSameImage(expected, ((HalconImage)ctx.GetVariable("加减1", "Image").Value).Object);
        Assert.Contains(ctx.StructuredLogs, l => l.Message == $"[图像加减] {operation}, Multi=1, Add=128");

        // byte + uint2：Add 沿用 HALCON 的自动转换（输出 uint2），Sub 报两种类型
        var mixed = ContextWith(a, Converted(b, "uint2"));
        NodeResult mixedResult = tool.Run(mixed);
        if (operation == ImageArithmeticOperation.Add)
        {
            Assert.True(mixedResult.IsSuccess);
            HOperatorSet.GetImageType(((HalconImage)mixed.GetVariable("加减1", "Image").Value).Object, out HTuple type);
            Assert.Equal("uint2", type.S);
        }
        else
        {
            Assert.Equal("加减1 两张图像的像素类型不同或不受支持：图像1 byte，图像2 uint2，可先接灰度增强的 ConvertType 统一类型", mixedResult.Message);
        }
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(ImageArithmeticOperation.Add)]
    [InlineData(ImageArithmeticOperation.Sub)]
    [InlineData(ImageArithmeticOperation.Mult)]
    [InlineData(ImageArithmeticOperation.Div)]
    [InlineData(ImageArithmeticOperation.AbsDiff)]
    [InlineData(ImageArithmeticOperation.Max)]
    [InlineData(ImageArithmeticOperation.Min)]
    public void 尺寸或通道数不一致时给出宽高通道对照的中文错误(ImageArithmeticOperation operation)
    {
        AddSubImageTool tool = Arithmetic(operation);
        HObject a = ByteImage();
        Assert.Equal("运算1 两张图像的尺寸或通道数不一致：图像1 64×48×1，图像2 32×24×1（宽×高×通道）",
            tool.Run(ContextWith(a, ByteImage(32, 24))).Message);
        Assert.Equal("运算1 两张图像的尺寸或通道数不一致：图像1 64×48×3，图像2 64×48×1（宽×高×通道）",
            tool.Run(ContextWith(Rgb(a), Inverted(a))).Message);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void byte结果出界时日志注明输出类型与建议()
    {
        HObject a = ByteImage();
        AddSubImageTool mult = Arithmetic(ImageArithmeticOperation.Mult);
        mult.Multi = 1;
        mult.Add = 0;
        var ctx = ContextWith(a, Inverted(a));
        Assert.True(mult.Run(ctx).IsSuccess);
        Assert.Contains(ctx.StructuredLogs, l => l.Message == "[图像运算] Mult, Multi=1, Add=0；输出 byte，超出 0 ~ 255 的部分被截断，需要完整范围时配合 Multi / Add，或先接灰度增强的 ConvertType 转成 real");

        var maxCtx = ContextWith(a, Inverted(a));
        Assert.True(Arithmetic(ImageArithmeticOperation.Max).Run(maxCtx).IsSuccess);
        Assert.Contains(maxCtx.StructuredLogs, l => l.Message == "[图像运算] Max；输出 byte");

        AddSubImageTool absDiff = Arithmetic(ImageArithmeticOperation.AbsDiff);
        var realCtx = ContextWith(Converted(a, "real"), Converted(Inverted(a), "real"));
        Assert.True(absDiff.Run(realCtx).IsSuccess);
        Assert.Contains(realCtx.StructuredLogs, l => l.Message == "[图像运算] AbsDiff, Multi=1；输出 real");
    }

    // ======================= IP-02 显隐、序列化、登记（非 HALCON） =======================

    [Theory]
    [InlineData(ImageArithmeticOperation.Add, true, true)]
    [InlineData(ImageArithmeticOperation.Sub, true, true)]
    [InlineData(ImageArithmeticOperation.Mult, true, true)]
    [InlineData(ImageArithmeticOperation.Div, true, true)]
    [InlineData(ImageArithmeticOperation.AbsDiff, true, false)]
    [InlineData(ImageArithmeticOperation.Max, false, false)]
    [InlineData(ImageArithmeticOperation.Min, false, false)]
    public void 图像运算按运算显隐参数(ImageArithmeticOperation operation, bool multi, bool add)
    {
        var tool = new AddSubImageTool("运算1") { Operation = operation };
        Assert.Equal((multi, add), (tool.IsParameterVisible("Multi"), tool.IsParameterVisible("Add")));
        Assert.True(tool.IsParameterVisible("Operation"));
        Assert.True(tool.IsParameterVisible("ImagePath1") && tool.IsParameterVisible("ImagePath2"));
    }

    [Fact]
    public void 图像运算枚举追加在末尾_按数字保存_历史文件缺省Add()
    {
        Assert.Equal(new[] { "Add", "Sub", "Mult", "Div", "AbsDiff", "Max", "Min" }, Enum.GetNames<ImageArithmeticOperation>());
        string json = FlowSerializer.SaveNode(new ToolNode(new AddSubImageTool("运算1") { Operation = ImageArithmeticOperation.AbsDiff, Multi = 2 }));
        Assert.Contains("\"ToolId\": \"add-sub-image\"", json);
        Assert.Contains("\"Operation\": 4", json);
        var loaded = (AddSubImageTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((ImageArithmeticOperation.AbsDiff, 2.0), (loaded.Operation, loaded.Multi));

        JsonObject legacy = JsonNode.Parse(json)!.AsObject();
        Assert.True(legacy["Tool"]!["Properties"]!.AsObject().Remove("Operation"));
        Assert.Equal(ImageArithmeticOperation.Add, ((AddSubImageTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy.ToJsonString())).Tool).Operation);
    }

    [Fact]
    public void 输出不与参数同名()
    {
        foreach (Type type in new[] { typeof(AddSubImageTool), typeof(ThresholdTool) })
        {
            var parameters = type.GetProperties().Select(p => p.Name).ToHashSet();
            Assert.DoesNotContain(ToolMetadata.GetOutputs(type), o => parameters.Contains(o.Name));
        }
        Assert.Equal(new[] { "Image" }, ToolMetadata.GetOutputs(typeof(AddSubImageTool)).Select(o => o.Name).ToArray());
        Assert.Equal(new[] { "Region", "UsedThreshold", "Count", "Found" }, ToolMetadata.GetOutputs(typeof(ThresholdTool)).Select(o => o.Name).ToArray());
    }

    [Fact]
    public void 工具箱_图像运算改名_阈值分割路由到专用窗口()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem arithmetic = ToolboxRegistry.Items.Single(i => i.Id == "add-sub-image");
        Assert.Equal(("02 图像处理", "图像运算"), (arithmetic.Category, arithmetic.DisplayName));
        var created = Assert.IsType<AddSubImageTool>(Assert.IsType<ToolNode>(arithmetic.Factory()).Tool);
        Assert.StartsWith("图像运算", created.ModuleName);
        Assert.Equal(ImageArithmeticOperation.Add, created.Operation);
        ToolboxItem threshold = ToolboxRegistry.Items.Single(i => i.Id == "threshold");
        Assert.Equal(("03 区域处理", "阈值分割"), (threshold.Category, threshold.DisplayName));

        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int start = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        string preview = router.Substring(start, router.IndexOf('}', start) - start);
        Assert.DoesNotContain("tool is ThresholdTool", preview);
        Assert.Contains("tool is AddSubImageTool", preview);
        Assert.Contains("if (tool is ThresholdTool thresholdTool)", router.Substring(0, start));
        Assert.Contains("new WpfThresholdToolEditWindow(thresholdTool, context)", router);

        string generic = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/Editors/WpfGenericToolEditWindow.xaml.cs"));
        Assert.Contains("if (_tool is AddSubImageTool)", generic);
        Assert.Contains("超出 0 ~ 255 的部分被截断", generic);
    }
}
