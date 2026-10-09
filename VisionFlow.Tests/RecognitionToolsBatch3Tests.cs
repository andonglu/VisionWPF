using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// RC-03 颜色识别（ColorClassifyTool）与 RC-04 颜色分割（ColorSegmentTool）。
/// 合成色块图用 System.Drawing 渲染（红/绿/蓝/灰块 + 白色背景，仅测试用途）转 HALCON 三通道图。
/// 探测结论（RECOGNITION-TOOLS-PLAN.md 第 12 节 C / D 段）：
/// C：decompose3 → trans_from_rgb('hsv'/'cielab') → intensity 逐对象均值 → 欧氏距离 + 允许距离；
/// D：add_samples_image_class_* 区域对象数必须等于类数（#1502），Regions 顺序 = 训练类序，
/// RejectedRegion = 图像定义域 − union1(类区域)，拒识阈值 [0,1) 越大越严格。
/// </summary>
public class RecognitionToolsBatch3Tests
{
    // ======================= 合成色块图 =======================

    private const int ImageWidth = 320;
    private const int ImageHeight = 240;
    private const int BlockSide = 60;

    // 色块位置（行,列）：红、绿、蓝、灰
    private static readonly (int Row1, int Col1) RedAt = (20, 20);
    private static readonly (int Row1, int Col1) GreenAt = (20, 120);
    private static readonly (int Row1, int Col1) BlueAt = (20, 220);
    private static readonly (int Row1, int Col1) GrayAt = (130, 20);

    private static (int Row1, int Col1, int Row2, int Col2) BlockAt((int Row1, int Col1) at) =>
        (at.Row1, at.Col1, at.Row1 + BlockSide - 1, at.Col1 + BlockSide - 1);

    /// <summary>红/绿/蓝/灰四块 + 白色背景的彩色图（byte 三通道）。</summary>
    private static HObject ColorPatch()
    {
        using var bitmap = new Bitmap(ImageWidth, ImageHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            foreach ((int row, int col, Color color) in new[]
            {
                (RedAt.Row1, RedAt.Col1, Color.Red),
                (GreenAt.Row1, GreenAt.Col1, Color.FromArgb(0, 255, 0)),
                (BlueAt.Row1, BlueAt.Col1, Color.Blue),
                (GrayAt.Row1, GrayAt.Col1, Color.FromArgb(128, 128, 128))
            })
            {
                using var brush = new SolidBrush(color);
                graphics.FillRectangle(brush, col, row, BlockSide, BlockSide);
            }
        }
        var bounds = new Rectangle(0, 0, ImageWidth, ImageHeight);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var red = new byte[ImageWidth * ImageHeight];
        var green = new byte[ImageWidth * ImageHeight];
        var blue = new byte[ImageWidth * ImageHeight];
        var rowBytes = new byte[Math.Abs(data.Stride)];
        try
        {
            for (int y = 0; y < ImageHeight; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, rowBytes, 0, data.Stride);
                for (int x = 0; x < ImageWidth; x++)
                {
                    // Format24bppRgb 像素序为 B、G、R
                    red[y * ImageWidth + x] = rowBytes[x * 3 + 2];
                    green[y * ImageWidth + x] = rowBytes[x * 3 + 1];
                    blue[y * ImageWidth + x] = rowBytes[x * 3];
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return ToColorImage(red, green, blue, ImageWidth, ImageHeight);
    }

    /// <summary>纯色图（byte 三通道）。</summary>
    private static HObject SolidImage(byte r, byte g, byte b)
    {
        return ToColorImage(
            Enumerable.Repeat(r, ImageWidth * ImageHeight).ToArray(),
            Enumerable.Repeat(g, ImageWidth * ImageHeight).ToArray(),
            Enumerable.Repeat(b, ImageWidth * ImageHeight).ToArray(),
            ImageWidth, ImageHeight);
    }

    private static HObject ToColorImage(byte[] red, byte[] green, byte[] blue, int width, int height)
    {
        GCHandle pinR = GCHandle.Alloc(red, GCHandleType.Pinned);
        GCHandle pinG = GCHandle.Alloc(green, GCHandleType.Pinned);
        GCHandle pinB = GCHandle.Alloc(blue, GCHandleType.Pinned);
        try
        {
            HOperatorSet.GenImage3(out HObject image, "byte", width, height,
                new HTuple(pinR.AddrOfPinnedObject().ToInt64()),
                new HTuple(pinG.AddrOfPinnedObject().ToInt64()),
                new HTuple(pinB.AddrOfPinnedObject().ToInt64()));
            return image;
        }
        finally
        {
            pinR.Free();
            pinG.Free();
            pinB.Free();
        }
    }

    private static HObject ToGrayImage(byte value, int width, int height)
    {
        var pixels = Enumerable.Repeat(value, width * height).ToArray();
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            HOperatorSet.GenImage1(out HObject image, "byte", width, height, new HTuple(pin.AddrOfPinnedObject().ToInt64()));
            return image;
        }
        finally
        {
            pin.Free();
        }
    }

    private static HObject Rectangle((int Row1, int Col1, int Row2, int Col2) block)
    {
        HOperatorSet.GenRectangle1(out HObject region, block.Row1, block.Col1, block.Row2, block.Col2);
        return region;
    }

    /// <summary>把若干矩形拼成多对象区域（gen_empty_obj 作种子，探测结论 B：空区域对象会让输出多 1 个对象）。</summary>
    private static HObject BlocksRegions(params (int Row1, int Col1, int Row2, int Col2)[] blocks)
    {
        HOperatorSet.GenEmptyObj(out HObject regions);
        foreach ((int row1, int col1, int row2, int col2) in blocks)
        {
            HOperatorSet.GenRectangle1(out HObject block, row1, col1, row2, col2);
            HOperatorSet.ConcatObj(regions, block, out HObject combined);
            regions.Dispose();
            block.Dispose();
            regions = combined;
        }
        return regions;
    }

    /// <summary>取区域在指定色彩空间的逐通道均值（与 ColorClassifyTool 算法一致，用于生成测试参考色）。</summary>
    private static double[] ChannelMeans(HObject image, HObject region, string colorSpace)
    {
        HOperatorSet.Decompose3(image, out HObject r, out HObject g, out HObject b);
        HObject converted1 = null;
        HObject converted2 = null;
        HObject converted3 = null;
        try
        {
            HObject channel1 = r;
            HObject channel2 = g;
            HObject channel3 = b;
            if (colorSpace != "rgb")
            {
                HOperatorSet.TransFromRgb(r, g, b, out converted1, out converted2, out converted3, colorSpace);
                channel1 = converted1;
                channel2 = converted2;
                channel3 = converted3;
            }
            var means = new double[3];
            HOperatorSet.Intensity(region, channel1, out HTuple mean1, out _);
            HOperatorSet.Intensity(region, channel2, out HTuple mean2, out _);
            HOperatorSet.Intensity(region, channel3, out HTuple mean3, out _);
            means[0] = mean1.D;
            means[1] = mean2.D;
            means[2] = mean3.D;
            return means;
        }
        finally
        {
            r.Dispose();
            g.Dispose();
            b.Dispose();
            converted1?.Dispose();
            converted2?.Dispose();
            converted3?.Dispose();
        }
    }

    private static int SymmetricDifferenceArea(HObject regions, int index, HObject truth)
    {
        HOperatorSet.SelectObj(regions, out HObject selected, index);
        try
        {
            HOperatorSet.Difference(selected, truth, out HObject onlyInSelected);
            HOperatorSet.Difference(truth, selected, out HObject onlyInTruth);
            try
            {
                HOperatorSet.AreaCenter(onlyInSelected, out HTuple area1, out _, out _);
                HOperatorSet.AreaCenter(onlyInTruth, out HTuple area2, out _, out _);
                return area1.I + area2.I;
            }
            finally
            {
                onlyInSelected.Dispose();
                onlyInTruth.Dispose();
            }
        }
        finally
        {
            selected.Dispose();
        }
    }

    private static int IntersectionArea(HObject region1, HObject region2)
    {
        HOperatorSet.Intersection(region1, region2, out HObject intersection);
        try
        {
            HOperatorSet.AreaCenter(intersection, out HTuple area, out _, out _);
            return area.I;
        }
        finally
        {
            intersection.Dispose();
        }
    }

    // ======================= 运行 helper =======================

    private static FlowContext ContextWith(HObject image, HObject? regions, int regionCount)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        if (regions != null)
        {
            ctx.SetVariable(Variable.Object("区域1", "Region", new HalconRegion(regions), regionCount));
        }
        return ctx;
    }

    private static ColorClassifyTool Classify(string references, ColorClassifySpace space = ColorClassifySpace.Cielab) =>
        new ColorClassifyTool("颜色识别1") { ImagePath = "图像1.Image", RegionPath = "区域1.Region", References = references, ColorSpace = space };

    private static FlowContext RunOk(ToolBase tool, FlowContext ctx)
    {
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ctx;
    }

    private static string ReferenceLine(string name, double[] means, double tolerance) =>
        $"{name}|{means[0]:0.###}|{means[1]:0.###}|{means[2]:0.###}|{tolerance}";

    // ======================= RC-03 颜色识别（HALCON） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色识别_Cielab默认_四块判为红绿蓝_灰色超出允许距离为未知()
    {
        HObject image = ColorPatch();
        HObject regions = BlocksRegions(BlockAt(RedAt), BlockAt(GreenAt), BlockAt(BlueAt), BlockAt(GrayAt));

        // 参考色直接从图上取各纯色块的 CIELAB 均值（与工具同一算法），灰块与三者距离远超允许距离
        string references = string.Join("\n",
            ReferenceLine("红", ChannelMeans(image, Rectangle(BlockAt(RedAt)), "cielab"), 30),
            ReferenceLine("绿", ChannelMeans(image, Rectangle(BlockAt(GreenAt)), "cielab"), 30),
            ReferenceLine("蓝", ChannelMeans(image, Rectangle(BlockAt(BlueAt)), "cielab"), 30));
        ColorClassifyTool tool = Classify(references);

        FlowContext ctx = RunOk(tool, ContextWith(image, regions, 4));

        Assert.Equal(new[] { "红", "绿", "蓝", "未知" }, ctx.GetVariable("颜色识别1", "Labels").GetValue<string[]>());
        Assert.Equal("红", ctx.GetVariable("颜色识别1", "FirstLabel").Value);
        Assert.Equal(4, ctx.GetVariable("颜色识别1", "Count").Value);
        Assert.Equal(false, ctx.GetVariable("颜色识别1", "AllKnown").Value);
        double[] distances = ctx.GetVariable("颜色识别1", "Distances").GetValue<double[]>();
        Assert.Equal(4, distances.Length);
        Assert.All(distances.Take(3), d => Assert.True(d >= 0 && d <= 30, $"已知颜色距离 {d} 应在允许距离内"));
        Assert.True(double.IsNaN(distances[3]), "未知颜色的距离应为 NaN");

        // MeanColors：每区域一个 1×1 三通道图像（所选色彩空间的均值）
        HalconImage[] meanColors = ctx.GetVariable("颜色识别1", "MeanColors").GetValue<HalconImage[]>();
        Assert.Equal(4, meanColors.Length);
        foreach (HalconImage meanColor in meanColors)
        {
            HOperatorSet.CountChannels(meanColor.Object, out HTuple channels);
            HOperatorSet.GetImageSize(meanColor.Object, out HTuple width, out HTuple height);
            Assert.Equal(3, channels.I);
            Assert.Equal(1, width.I);
            Assert.Equal(1, height.I);
        }
        Assert.Contains(ctx.StructuredLogs, l => l.Message.StartsWith("[颜色识别] Cielab 区域数=4 已知颜色=3"));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色识别_Rgb空间_显式参考值_标签正确()
    {
        HObject image = ColorPatch();
        HObject regions = BlocksRegions(BlockAt(RedAt), BlockAt(GreenAt), BlockAt(BlueAt), BlockAt(GrayAt));
        ColorClassifyTool tool = Classify("红|255|0|0|60\n绿|0|255|0|60\n蓝|0|0|255|60", ColorClassifySpace.Rgb);

        FlowContext ctx = RunOk(tool, ContextWith(image, regions, 4));

        Assert.Equal(new[] { "红", "绿", "蓝", "未知" }, ctx.GetVariable("颜色识别1", "Labels").GetValue<string[]>());
        Assert.Equal(false, ctx.GetVariable("颜色识别1", "AllKnown").Value);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色识别_非三通道输入_中文错误()
    {
        HObject gray = ToGrayImage(128, 200, 100);
        HObject regions = BlocksRegions(BlockAt(RedAt));
        ColorClassifyTool tool = Classify("红|1|2|3|60");

        NodeResult result = tool.Run(ContextWith(gray, regions, 1));

        Assert.False(result.IsSuccess);
        Assert.Equal("颜色识别1 颜色识别需要三通道彩色图像（当前 1 通道）", result.Message);
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(true)]
    [InlineData(false)]
    public void 颜色识别_空区域元组_未找到策略两态(bool failWhenNotFound)
    {
        HObject image = ColorPatch();
        HOperatorSet.GenEmptyObj(out HObject emptyRegions);
        ColorClassifyTool tool = Classify("红|1|2|3|60");
        tool.FailWhenNotFound = failWhenNotFound;

        FlowContext ctx = ContextWith(image, emptyRegions, 0);
        NodeResult result = tool.Run(ctx);

        if (failWhenNotFound)
        {
            Assert.False(result.IsSuccess);
            Assert.Equal("未找到待识别颜色的区域", result.Message);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0, ctx.GetVariable("颜色识别1", "Count").Value);
            Assert.Empty(ctx.GetVariable("颜色识别1", "Labels").GetValue<string[]>());
            Assert.Equal(string.Empty, ctx.GetVariable("颜色识别1", "FirstLabel").Value);
            Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("未找到待识别颜色的区域（已设置未找到时继续：Found=false）"));
        }
    }

    // ======================= RC-04 颜色分割（HALCON） =======================

    private static ColorSegmentTool Segment(string classNames = "红,绿", ColorSegmentClassifier classifier = ColorSegmentClassifier.Mlp) =>
        new ColorSegmentTool("颜色分割1") { ImagePath = "图像1.Image", ClassNames = classNames, Classifier = classifier };

    private static HObject RedGreenSamples() => BlocksRegions(BlockAt(RedAt), BlockAt(GreenAt));

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色分割_Mlp训练与分类_类区域与真值重合率大于95percent()
    {
        HObject image = ColorPatch();
        ColorSegmentTool tool = Segment();
        // MLP 对远离训练样本的像素外推置信度偏高（白色背景在 0.5 下会被归入最近类），
        // 0.9 时仅高置信像素归类（探测结论 D：阈值越大越严格）
        tool.RejectionThreshold = 0.9;
        try
        {
            tool.Train(null, image, RedGreenSamples());
            Assert.NotNull(tool.ClassifierData);
            Assert.True(tool.ClassifierData.Length > 0, "训练后的分类器应有序列化数据");

            FlowContext ctx = RunOk(tool, ContextWith(image, null, 0));

            var regions = (HalconRegion)ctx.GetVariable("颜色分割1", "Regions").Value;
            HOperatorSet.CountObj(regions.Object, out HTuple count);
            Assert.Equal(2, count.I);
            int[] areas = ctx.GetVariable("颜色分割1", "ClassAreas").GetValue<int[]>();
            Assert.Equal(2, areas.Length);

            const int blockArea = BlockSide * BlockSide;
            using HObject truthRed = Rectangle(BlockAt(RedAt));
            using HObject truthGreen = Rectangle(BlockAt(GreenAt));
            int symDiffRed = SymmetricDifferenceArea(regions.Object, 1, truthRed);
            int symDiffGreen = SymmetricDifferenceArea(regions.Object, 2, truthGreen);
            Assert.True(symDiffRed < blockArea * 0.05, $"红块对称差面积 {symDiffRed} 应小于块面积的 5%");
            Assert.True(symDiffGreen < blockArea * 0.05, $"绿块对称差面积 {symDiffGreen} 应小于块面积的 5%");
            Assert.InRange(areas[0], blockArea * 0.95, blockArea * 1.05);
            Assert.InRange(areas[1], blockArea * 0.95, blockArea * 1.05);
            Assert.Equal(true, ctx.GetVariable("颜色分割1", "Found").Value);
            Assert.Equal(2, ctx.GetVariable("颜色分割1", "Count").Value);
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色分割_训练数据序列化重载_新实例分类结果一致()
    {
        HObject image = ColorPatch();
        ColorSegmentTool trained = Segment();
        try
        {
            trained.Train(null, image, RedGreenSamples());
            FlowContext expected = RunOk(trained, ContextWith(image, null, 0));

            var reloaded = Segment();
            reloaded.ClassifierData = trained.ClassifierData;
            try
            {
                FlowContext actual = RunOk(reloaded, ContextWith(image, null, 0));
                Assert.Equal(expected.GetVariable("颜色分割1", "ClassAreas").GetValue<int[]>(),
                    actual.GetVariable("颜色分割1", "ClassAreas").GetValue<int[]>());
            }
            finally
            {
                reloaded.ReleaseResources();
            }
        }
        finally
        {
            trained.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色分割_拒识阈值0点9_背景进入RejectedRegion()
    {
        HObject image = ColorPatch();
        ColorSegmentTool tool = Segment();
        tool.RejectionThreshold = 0.9;
        try
        {
            tool.Train(null, image, RedGreenSamples());
            FlowContext ctx = RunOk(tool, ContextWith(image, null, 0));

            var rejected = (HalconRegion)ctx.GetVariable("颜色分割1", "RejectedRegion").Value;
            using HObject backgroundCorner = Rectangle((200, 300, 230, 310));
            Assert.True(IntersectionArea(rejected.Object, backgroundCorner) == 31 * 11,
                "拒识阈值 0.9 时白色背景应整体进入 RejectedRegion");
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色分割_Gmm训练与分类_两类区域覆盖对应色块()
    {
        HObject image = ColorPatch();
        ColorSegmentTool tool = Segment(classifier: ColorSegmentClassifier.Gmm);
        try
        {
            tool.Train(null, image, RedGreenSamples());
            FlowContext ctx = RunOk(tool, ContextWith(image, null, 0));

            var regions = (HalconRegion)ctx.GetVariable("颜色分割1", "Regions").Value;
            HOperatorSet.CountObj(regions.Object, out HTuple count);
            Assert.Equal(2, count.I);
            int[] areas = ctx.GetVariable("颜色分割1", "ClassAreas").GetValue<int[]>();
            const int blockArea = BlockSide * BlockSide;
            Assert.InRange(areas[0], blockArea * 0.9, blockArea * 1.1);
            Assert.InRange(areas[1], blockArea * 0.9, blockArea * 1.1);
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色分割_全拒识_RejectedRegion为整图定义域_走未找到策略()
    {
        HObject image = ColorPatch();
        ColorSegmentTool tool = Segment(classifier: ColorSegmentClassifier.Gmm);
        try
        {
            tool.Train(null, image, RedGreenSamples());

            // 与红/绿训练样本完全不同的纯色图：全部像素拒识（GMM 概率密度低于阈值）
            HObject foreign = SolidImage(128, 128, 128);
            tool.FailWhenNotFound = false;
            FlowContext ctx = RunOk(tool, ContextWith(foreign, null, 0));

            Assert.Equal(false, ctx.GetVariable("颜色分割1", "Found").Value);
            Assert.Equal(0, ctx.GetVariable("颜色分割1", "Count").Value);
            Assert.All(ctx.GetVariable("颜色分割1", "ClassAreas").GetValue<int[]>(), a => Assert.Equal(0, a));
            var rejected = (HalconRegion)ctx.GetVariable("颜色分割1", "RejectedRegion").Value;
            HOperatorSet.AreaCenter(rejected.Object, out HTuple area, out _, out _);
            Assert.Equal(ImageWidth * ImageHeight, area.I);
            Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("颜色分割结果为空：所有颜色类的区域均为空（已设置未找到时继续：Found=false）"));
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色分割_训练样本数与类名数不符_中文失败()
    {
        HObject image = ColorPatch();
        ColorSegmentTool tool = Segment();
        try
        {
            HObject threeRegions = BlocksRegions(BlockAt(RedAt), BlockAt(GreenAt), BlockAt(BlueAt));
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => tool.Train(null, image, threeRegions));
            Assert.Contains("训练样本数（3）与类名数（2）不一致", ex.Message);
            Assert.Null(tool.ClassifierData);
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 颜色分割_分类器按实例缓存_预热与释放幂等()
    {
        HObject image = ColorPatch();
        ColorSegmentTool tool = Segment();
        try
        {
            tool.Train(null, image, RedGreenSamples());
            tool.Prepare();
            tool.Prepare();
            FlowContext ctx = RunOk(tool, ContextWith(image, null, 0));
            Assert.Equal(2, ctx.GetVariable("颜色分割1", "Count").Value);
            tool.ReleaseResources();
            tool.ReleaseResources();
            tool.Prepare();
            FlowContext again = RunOk(tool, ContextWith(image, null, 0));
            Assert.Equal(2, again.GetVariable("颜色分割1", "Count").Value);
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    // ======================= 校验、枚举、序列化、登记（非 HALCON） =======================

    [Fact]
    public void 颜色识别参数校验_流程校验与运行措辞一致()
    {
        var empty = new ColorClassifyTool("颜色识别1");
        ToolConfigurationIssue issue = Assert.Single(empty.CheckConfiguration());
        Assert.Equal(nameof(ColorClassifyTool.References), issue.Parameter);
        Assert.Equal("参考颜色不能为空：每行一个 名称|通道1|通道2|通道3|允许距离（允许距离可省略，默认 60）", issue.Message);
        NodeResult result = empty.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"颜色识别1 {nameof(ColorClassifyTool.References)}：{issue.Message}", result.Message);

        var badFormat = new ColorClassifyTool("颜色识别1") { References = "红|1|2" };
        issue = Assert.Single(badFormat.CheckConfiguration());
        Assert.Equal("第 1 行格式错误：应为 名称|通道1|通道2|通道3|允许距离（允许距离可省略，默认 60），实际为「红|1|2」", issue.Message);

        var badChannel = new ColorClassifyTool("颜色识别1") { References = "红|a|2|3" };
        issue = Assert.Single(badChannel.CheckConfiguration());
        Assert.Equal("第 1 行通道值「a」不是有效数字", issue.Message);

        var negative = new ColorClassifyTool("颜色识别1") { References = "红|1|2|3|-5" };
        issue = Assert.Single(negative.CheckConfiguration());
        Assert.Equal("第 1 行允许距离不能为负数（当前 -5）", issue.Message);

        var good = new ColorClassifyTool("颜色识别1") { References = "红|1|2|3" };
        Assert.Empty(good.CheckConfiguration());
    }

    [Fact]
    public void ColorClassifySpace枚举_默认值Cielab()
    {
        Assert.Equal(new[] { "Rgb", "Hsv", "Cielab" }, Enum.GetNames<ColorClassifySpace>());
        Assert.Equal(0, (int)ColorClassifySpace.Rgb);
        ColorClassifyTool tool = new ColorClassifyTool("颜色识别1");
        Assert.Equal(ColorClassifySpace.Cielab, tool.ColorSpace);
        Assert.Equal("未知", tool.UnknownLabel);
        Assert.True(tool.FailWhenNotFound);
    }

    [Fact]
    public void 颜色识别序列化往返_JSON含color_classify_枚举按数字保存()
    {
        var tool = new ColorClassifyTool("颜色识别1")
        {
            ImagePath = "Input.Image",
            RegionPath = "区域1.Region",
            ColorSpace = ColorClassifySpace.Hsv,
            References = "红|1|2|3|30\n绿|4|5|6",
            UnknownLabel = "其它"
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"color-classify\"", json);
        Assert.Contains("\"ColorSpace\": 1", json);
        Assert.Contains("\"UnknownLabel\":", json);

        var loaded = (ColorClassifyTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((ColorClassifySpace.Hsv, "红|1|2|3|30\n绿|4|5|6", "其它"),
            (loaded.ColorSpace, loaded.References, loaded.UnknownLabel));
        Assert.True(loaded.FailWhenNotFound);

        // 历史文件缺省新属性：默认值与语义一致
        JsonObject legacy = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new ColorClassifyTool("颜色识别1") { References = "红|1|2|3" })))!.AsObject();
        JsonObject properties = legacy["Tool"]!["Properties"]!.AsObject();
        foreach (string name in new[] { "ColorSpace", "UnknownLabel" })
        {
            Assert.True(properties.Remove(name), name);
        }
        var old = (ColorClassifyTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy.ToJsonString())).Tool;
        Assert.Equal(ColorClassifySpace.Cielab, old.ColorSpace);
        Assert.Equal("未知", old.UnknownLabel);
        Assert.True(old.FailWhenNotFound);
    }

    [Fact]
    public void 颜色分割参数校验_流程校验与运行措辞一致()
    {
        var empty = new ColorSegmentTool("颜色分割1");
        ToolConfigurationIssue issue = Assert.Single(empty.CheckConfiguration());
        Assert.Equal((nameof(ColorSegmentTool.ClassNames), "类名不能为空：请用逗号分隔填写，如 红,绿,蓝"), (issue.Parameter, issue.Message));

        var duplicated = new ColorSegmentTool("颜色分割1") { ClassNames = "红,绿,红" };
        issue = Assert.Single(duplicated.CheckConfiguration());
        Assert.Equal("类名重复：红", issue.Message);

        var negative = new ColorSegmentTool("颜色分割1") { ClassNames = "红,绿", RejectionThreshold = -0.1 };
        issue = Assert.Single(negative.CheckConfiguration());
        Assert.Equal((nameof(ColorSegmentTool.RejectionThreshold), "拒识阈值必须在 0~1 之间（含 0、不含 1；越大越严格）"), (issue.Parameter, issue.Message));

        var tooLarge = new ColorSegmentTool("颜色分割1") { ClassNames = "红,绿", RejectionThreshold = 1.0 };
        issue = Assert.Single(tooLarge.CheckConfiguration());
        Assert.Equal(nameof(ColorSegmentTool.RejectionThreshold), issue.Parameter);

        NodeResult result = tooLarge.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"颜色分割1 {nameof(ColorSegmentTool.RejectionThreshold)}：拒识阈值必须在 0~1 之间（含 0、不含 1；越大越严格）", result.Message);

        var good = new ColorSegmentTool("颜色分割1") { ClassNames = "红,绿", RejectionThreshold = 0 };
        Assert.Empty(good.CheckConfiguration());
    }

    [Fact]
    public void 颜色分割_未训练时运行_中文失败()
    {
        ColorSegmentTool tool = Segment();
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal("颜色分割1 颜色分割分类器尚未训练：请在编辑窗口为每个类框选样本区域并执行训练", result.Message);
    }

    [Fact]
    public void ColorSegmentClassifier枚举_默认值Mlp()
    {
        Assert.Equal(new[] { "Mlp", "Gmm" }, Enum.GetNames<ColorSegmentClassifier>());
        Assert.Equal(0, (int)ColorSegmentClassifier.Mlp);
        ColorSegmentTool tool = new ColorSegmentTool("颜色分割1");
        Assert.Equal(ColorSegmentClassifier.Mlp, tool.Classifier);
        Assert.Equal(0.5, tool.RejectionThreshold);
        Assert.True(tool.FailWhenNotFound);
    }

    [Fact]
    public void 颜色分割序列化往返_JSON含color_segment_训练数据base64保存()
    {
        var tool = new ColorSegmentTool("颜色分割1")
        {
            ImagePath = "Input.Image",
            Classifier = ColorSegmentClassifier.Gmm,
            ClassNames = "红,绿",
            RejectionThreshold = 0.7,
            ClassifierData = new byte[] { 1, 2, 3, 250, 251 }
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"color-segment\"", json);
        Assert.Contains("\"Classifier\": 1", json);
        Assert.Contains("\"RejectionThreshold\": 0.7", json);

        var loaded = (ColorSegmentTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((ColorSegmentClassifier.Gmm, "红,绿", 0.7), (loaded.Classifier, loaded.ClassNames, loaded.RejectionThreshold));
        Assert.Equal(new byte[] { 1, 2, 3, 250, 251 }, loaded.ClassifierData);
        Assert.True(loaded.FailWhenNotFound);

        // 历史文件缺省新属性：MLP / 0.5 / 未训练
        JsonObject legacy = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new ColorSegmentTool("颜色分割1") { ClassNames = "红,绿" })))!.AsObject();
        JsonObject properties = legacy["Tool"]!["Properties"]!.AsObject();
        foreach (string name in new[] { "Classifier", "RejectionThreshold", "ClassifierData" })
        {
            Assert.True(properties.Remove(name), name);
        }
        var old = (ColorSegmentTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy.ToJsonString())).Tool;
        Assert.Equal(ColorSegmentClassifier.Mlp, old.Classifier);
        Assert.Equal(0.5, old.RejectionThreshold);
        Assert.Null(old.ClassifierData);
    }

    [Fact]
    public void 颜色工具输出不与参数同名()
    {
        var classifyParameters = typeof(ColorClassifyTool).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain(ToolMetadata.GetOutputs(typeof(ColorClassifyTool)), o => classifyParameters.Contains(o.Name));
        Assert.Equal(new[] { "Labels", "FirstLabel", "Distances", "MeanColors", "Count", "AllKnown" },
            ToolMetadata.GetOutputs(typeof(ColorClassifyTool)).Select(o => o.Name).ToArray());

        var segmentParameters = typeof(ColorSegmentTool).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain(ToolMetadata.GetOutputs(typeof(ColorSegmentTool)), o => segmentParameters.Contains(o.Name));
        Assert.Equal(new[] { "Regions", "ClassAreas", "RejectedRegion", "Found", "Count" },
            ToolMetadata.GetOutputs(typeof(ColorSegmentTool)).Select(o => o.Name).ToArray());
    }

    [Fact]
    public void 工具箱_颜色识别与颜色分割_登记与顺序()
    {
        ToolboxRegistry.RegisterDefaults();
        List<string> ids = ToolboxRegistry.Items.Select(i => i.Id).ToList();
        Assert.True(ids.IndexOf("barcode1d") < ids.IndexOf("ocr")
            && ids.IndexOf("ocr") < ids.IndexOf("color-classify")
            && ids.IndexOf("color-classify") < ids.IndexOf("color-segment"),
            "工具箱顺序应为 读码 → 字符识别 → 颜色识别 → 颜色分割");

        ToolboxItem classifyItem = ToolboxRegistry.Items.Single(i => i.Id == "color-classify");
        Assert.Equal(("06 识别工具", "颜色识别"), (classifyItem.Category, classifyItem.DisplayName));
        var classifyTool = Assert.IsType<ColorClassifyTool>(Assert.IsType<ToolNode>(classifyItem.Factory()).Tool);
        Assert.StartsWith("颜色识别", classifyTool.ModuleName);
        Assert.Equal("Input.Image", classifyTool.ImagePath);

        ToolboxItem segmentItem = ToolboxRegistry.Items.Single(i => i.Id == "color-segment");
        Assert.Equal(("06 识别工具", "颜色分割"), (segmentItem.Category, segmentItem.DisplayName));
        var segmentTool = Assert.IsType<ColorSegmentTool>(Assert.IsType<ToolNode>(segmentItem.Factory()).Tool);
        Assert.StartsWith("颜色分割", segmentTool.ModuleName);
        Assert.Equal("Input.Image", segmentTool.ImagePath);
    }
}
