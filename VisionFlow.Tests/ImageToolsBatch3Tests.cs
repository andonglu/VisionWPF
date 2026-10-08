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
/// IMAGE-TOOLS 第三批：IP-04 图像几何变换（ImageGeometryTool）。
/// 标注 Requires=HALCON 的用例与直接调用 HALCON 算子对照；其余用例（校验、显隐、序列化、登记）不调用 HALCON 算子。
/// </summary>
public class ImageToolsBatch3Tests
{
    // ======================= 合成图像与对照工具 =======================

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
                values.Add(Math.Clamp(20 + c * 3 + r + rnd.Next(-5, 6), 0, 255));
            }
        }
        HOperatorSet.SetGrayval(image, new HTuple(rows.ToArray()), new HTuple(cols.ToArray()), new HTuple(values.ToArray()));
        return image;
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

    private static HObject Converted(HObject image, string type)
    {
        HOperatorSet.ConvertImageType(image, out HObject converted, type);
        return converted;
    }

    /// <summary>101 × 71 字节图，两个以整数坐标为中心的对称圆盘（灰度加权重心精确等于圆心，第 14 节探测用图）。</summary>
    private static readonly (double R, double C)[] Blobs = { (20, 30), (50, 80) };

    private static HObject BlobImage()
    {
        HOperatorSet.GenImageConst(out HObject image, "byte", 101, 71);
        foreach (var (r, c) in Blobs)
        {
            HOperatorSet.GenCircle(out HObject disc, r, c, 4.5);
            HOperatorSet.PaintRegion(disc, image, out HObject painted, 255, "fill");
            image.Dispose();
            disc.Dispose();
            image = painted;
        }
        return image;
    }

    private static List<(double R, double C)> Centroids(HObject image)
    {
        HOperatorSet.Threshold(image, out HObject bright, 20, 255);
        HOperatorSet.Connection(bright, out HObject parts);
        HOperatorSet.CountObj(parts, out HTuple n);
        var found = new List<(double R, double C)>();
        for (int i = 1; i <= n.I; i++)
        {
            HOperatorSet.SelectObj(parts, out HObject one, i);
            HOperatorSet.AreaCenterGray(one, image, out HTuple area, out HTuple r, out HTuple c);
            if (area.D > 0)
            {
                found.Add((r.D, c.D));
            }
        }
        return found;
    }

    private static void AssertSameImage(HObject expected, HObject actual)
    {
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
        HOperatorSet.AreaCenter(ed, out HTuple domainArea, out _, out _);
        if (domainArea.I == 0)
        {
            return;
        }
        for (int channel = 1; channel <= ec.I; channel++)
        {
            HOperatorSet.AccessChannel(expected, out HObject e, channel);
            HOperatorSet.AccessChannel(actual, out HObject a, channel);
            HOperatorSet.SubImage(Converted(e, "real"), Converted(a, "real"), out HObject diff, 1, 0);
            HOperatorSet.MinMaxGray(ed, diff, 0, out HTuple min, out HTuple max, out _);
            Assert.True(min.D == 0 && max.D == 0, $"通道 {channel} 像素差 [{min.D}, {max.D}]");
        }
    }

    private static void AssertMatrix(double[] expected, HomMat2D actual)
    {
        double[] values = actual.Data.ToDArr();
        Assert.Equal(6, values.Length);
        for (int i = 0; i < 6; i++)
        {
            Assert.True(Math.Abs(expected[i] - values[i]) < 1e-9, $"矩阵第 {i} 项：期望 {expected[i]}，实际 {values[i]}");
        }
    }

    private static FlowContext ContextWith(HObject image, HObject? region = null)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        if (region != null)
        {
            HOperatorSet.CountObj(region, out HTuple count);
            ctx.SetVariable(Variable.Object("区域1", "Region", new HalconRegion(region), count.I));
        }
        return ctx;
    }

    private static ImageGeometryTool Tool(ImageGeometryMethod method) =>
        new ImageGeometryTool("几何1") { ImagePath = "图像1.Image", RegionPath = "区域1.Region", Method = method };

    private static HObject Run(ImageGeometryTool tool, FlowContext ctx, out HomMat2D homMat, out HomMat2D inverse)
    {
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        homMat = (HomMat2D)ctx.GetVariable(tool.ModuleName, "HomMat").Value;
        inverse = (HomMat2D)ctx.GetVariable(tool.ModuleName, "InverseHomMat").Value;
        return ((HalconImage)ctx.GetVariable(tool.ModuleName, "Image").Value).Object;
    }

    private static HObject TwoRegions()
    {
        HOperatorSet.GenCircle(out HObject circle, 20, 25, 9);
        HOperatorSet.GenRectangle1(out HObject rect, 30, 40, 44, 60);
        HOperatorSet.ConcatObj(circle, rect, out HObject both);
        return both;
    }

    private static double[] Inverted(double[] matrix)
    {
        HOperatorSet.HomMat2dInvert(new HTuple(matrix), out HTuple inverse);
        return inverse.ToDArr();
    }

    // ======================= 与 HALCON 直接调用逐像素一致（门禁） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(ImageGeometryMethod.ZoomFactor, ImageMirrorMode.row)]
    [InlineData(ImageGeometryMethod.ZoomSize, ImageMirrorMode.row)]
    [InlineData(ImageGeometryMethod.Rotate, ImageMirrorMode.row)]
    [InlineData(ImageGeometryMethod.Mirror, ImageMirrorMode.row)]
    [InlineData(ImageGeometryMethod.Mirror, ImageMirrorMode.column)]
    [InlineData(ImageGeometryMethod.Mirror, ImageMirrorMode.diagonal)]
    [InlineData(ImageGeometryMethod.CropRectangle, ImageMirrorMode.row)]
    [InlineData(ImageGeometryMethod.CropRegion, ImageMirrorMode.row)]
    public void 各方式输出与直接调用逐像素一致_HomMat按第14节定义_InverseHomMat为其逆(ImageGeometryMethod method, ImageMirrorMode mirror)
    {
        ImageGeometryTool tool = Tool(method);
        (tool.ScaleWidth, tool.ScaleHeight) = (0.3, 1.7);
        (tool.TargetWidth, tool.TargetHeight) = (90, 30);
        tool.AngleDeg = 30;
        tool.Interpolation = method == ImageGeometryMethod.ZoomSize ? GeometryInterpolation.bicubic : GeometryInterpolation.bilinear;
        tool.MirrorMode = mirror;
        (tool.Row1, tool.Column1, tool.Row2, tool.Column2) = (5, 6, 30, 50);
        HObject a = ByteImage();
        foreach (HObject input in new[] { a, Rgb(a), Reduced(a) })
        {
            HOperatorSet.GetImageSize(input, out HTuple w, out HTuple h);
            int width = w.I, height = h.I;
            HObject expected;
            double[] matrix;
            switch (method)
            {
                case ImageGeometryMethod.ZoomFactor:
                case ImageGeometryMethod.ZoomSize:
                {
                    if (method == ImageGeometryMethod.ZoomFactor)
                    {
                        HOperatorSet.ZoomImageFactor(input, out expected, 0.3, 1.7, "bilinear");
                    }
                    else
                    {
                        HOperatorSet.ZoomImageSize(input, out expected, 90, 30, "bicubic");
                    }
                    HOperatorSet.GetImageSize(expected, out HTuple zw, out HTuple zh);
                    double sr = (double)zh.I / height, sc = (double)zw.I / width;
                    matrix = new[] { sr, 0, 0.5 * sr - 0.5, 0, sc, 0.5 * sc - 0.5 };
                    break;
                }
                case ImageGeometryMethod.Rotate:
                {
                    HOperatorSet.RotateImage(input, out expected, 30, "bilinear");
                    HOperatorSet.HomMat2dIdentity(out HTuple m);
                    HOperatorSet.HomMat2dTranslate(m, -(height - 1) / 2.0, -(width - 1) / 2.0, out m);
                    HOperatorSet.HomMat2dRotate(m, 30 * Math.PI / 180, 0, 0, out m);
                    HOperatorSet.HomMat2dTranslate(m, (height - 1) / 2.0, (width - 1) / 2.0, out m);
                    matrix = m.ToDArr();
                    break;
                }
                case ImageGeometryMethod.Mirror:
                    HOperatorSet.MirrorImage(input, out expected, mirror.ToString());
                    matrix = mirror == ImageMirrorMode.row ? new double[] { -1, 0, height - 1, 0, 1, 0 }
                        : mirror == ImageMirrorMode.column ? new double[] { 1, 0, 0, 0, -1, width - 1 }
                        : new double[] { 0, 1, 0, 1, 0, 0 };
                    break;
                case ImageGeometryMethod.CropRectangle:
                    HOperatorSet.CropRectangle1(input, out expected, 5, 6, 30, 50);
                    matrix = new double[] { 1, 0, -5, 0, 1, -6 };
                    break;
                default:
                {
                    HOperatorSet.Union1(TwoRegions(), out HObject union);
                    HOperatorSet.ReduceDomain(input, union, out HObject reduced);
                    HOperatorSet.GetDomain(reduced, out HObject domain);
                    HOperatorSet.SmallestRectangle1(domain, out HTuple top, out HTuple left, out _, out _);
                    HOperatorSet.CropDomain(reduced, out expected);
                    matrix = new double[] { 1, 0, -top.I, 0, 1, -left.I };
                    break;
                }
            }
            var ctx = ContextWith(input, method == ImageGeometryMethod.CropRegion ? TwoRegions() : null);
            HObject actual = Run(tool, ctx, out HomMat2D homMat, out HomMat2D inverse);
            AssertSameImage(expected, actual);
            AssertMatrix(matrix, homMat);
            AssertMatrix(Inverted(matrix), inverse);
            Assert.NotSame(input, actual);
            Assert.True(input.IsInitialized());
            ctx.Dispose();
        }
    }

    // ======================= 第 8 节验收：新图上的点经 InverseHomMat 映射回原图（门禁） =======================

    public static IEnumerable<object[]> MappingCases()
    {
        yield return new object[] { "缩放 ZoomFactor 2 × 1.5", (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomFactor; (t.ScaleWidth, t.ScaleHeight) = (2, 1.5); t.Interpolation = GeometryInterpolation.bilinear; }) };
        yield return new object[] { "缩放 ZoomFactor 0.6 × 0.8", (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomFactor; (t.ScaleWidth, t.ScaleHeight) = (0.6, 0.8); t.Interpolation = GeometryInterpolation.bilinear; }) };
        yield return new object[] { "缩放 ZoomSize 150 × 50", (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomSize; (t.TargetWidth, t.TargetHeight) = (150, 50); t.Interpolation = GeometryInterpolation.bilinear; }) };
        yield return new object[] { "旋转 30°", (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.Rotate; t.AngleDeg = 30; t.Interpolation = GeometryInterpolation.bilinear; }) };
        yield return new object[] { "旋转 −90°（宽高交换）", (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.Rotate; t.AngleDeg = -90; t.Interpolation = GeometryInterpolation.bilinear; }) };
        yield return new object[] { "镜像 row", (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.Mirror; t.MirrorMode = ImageMirrorMode.row; }) };
        yield return new object[] { "镜像 diagonal（宽高交换）", (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.Mirror; t.MirrorMode = ImageMirrorMode.diagonal; }) };
        yield return new object[] { "矩形裁剪", (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.CropRectangle; (t.Row1, t.Column1, t.Row2, t.Column2) = (8, 12, 64, 95); }) };
        yield return new object[] { "按区域裁剪", (Action<ImageGeometryTool>)(t => t.Method = ImageGeometryMethod.CropRegion) };
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [MemberData(nameof(MappingCases))]
    public void 新图上取点经InverseHomMat映射回原图_误差小于半像素(string what, Action<ImageGeometryTool> configure)
    {
        ImageGeometryTool tool = Tool(ImageGeometryMethod.ZoomFactor);
        configure(tool);
        HOperatorSet.GenRectangle1(out HObject cropRegion, 10, 15, 60, 90);
        HObject output = Run(tool, ContextWith(BlobImage(), cropRegion), out HomMat2D homMat, out HomMat2D inverse);
        List<(double R, double C)> found = Centroids(output);
        Assert.Equal(2, found.Count);
        foreach ((double r, double c) in found)
        {
            inverse.TransformPoint(r, c, out double backRow, out double backColumn);
            double error = Blobs.Min(b => Math.Sqrt((b.R - backRow) * (b.R - backRow) + (b.C - backColumn) * (b.C - backColumn)));
            Assert.True(error < 0.5, $"{what}：新图 ({r:F3}, {c:F3}) 映射回 ({backRow:F3}, {backColumn:F3})，误差 {error:F3} 像素");
        }
        foreach ((double r, double c) in Blobs)
        {
            homMat.TransformPoint(r, c, out double newRow, out double newColumn);
            double error = found.Min(f => Math.Sqrt((f.R - newRow) * (f.R - newRow) + (f.C - newColumn) * (f.C - newColumn)));
            Assert.True(error < 0.5, $"{what}：原图 ({r}, {c}) 经 HomMat 为 ({newRow:F3}, {newColumn:F3})，与新图亮斑相差 {error:F3} 像素");
        }
    }

    // ======================= 随图像尺寸变化的约束与裁剪行为（门禁） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 随图像尺寸变化的约束在运行时给出含参数名与当前值的中文错误()
    {
        HObject image = ByteImage();
        ImageGeometryTool zoom = Tool(ImageGeometryMethod.ZoomFactor);
        (zoom.ScaleWidth, zoom.ScaleHeight) = (0.004, 1);
        Assert.Equal("几何1 ScaleWidth：宽度缩放系数 ScaleWidth = 0.004 使缩放后宽度为 0（原宽 64），须在 1 ~ 32768 之间", zoom.Run(ContextWith(image)).Message);
        (zoom.ScaleWidth, zoom.ScaleHeight) = (1, 700);
        Assert.Equal("几何1 ScaleHeight：高度缩放系数 ScaleHeight = 700 使缩放后高度为 33600（原高 48），须在 1 ~ 32768 之间", zoom.Run(ContextWith(image)).Message);
        (zoom.ScaleWidth, zoom.ScaleHeight) = (0.0079, 1);
        Assert.True(zoom.Run(ContextWith(image)).IsSuccess, "64 × 0.0079 + 0.5 → 1 列可用");

        ImageGeometryTool crop = Tool(ImageGeometryMethod.CropRectangle);
        (crop.Row1, crop.Column1, crop.Row2, crop.Column2) = (48, 0, 50, 10);
        Assert.Equal("几何1 Row1：裁剪起点 Row1 = 48 超出图像（高 48，行号 0 ~ 47）", crop.Run(ContextWith(image)).Message);
        (crop.Row1, crop.Column1) = (0, 64);
        crop.Column2 = 70;
        Assert.Equal("几何1 Column1：裁剪起点 Column1 = 64 超出图像（宽 64，列号 0 ~ 63）", crop.Run(ContextWith(image)).Message);

        ImageGeometryTool region = Tool(ImageGeometryMethod.CropRegion);
        HOperatorSet.GenRectangle1(out HObject outside, 100, 100, 120, 120);
        Assert.Equal("几何1 RegionPath：裁剪区域 区域1.Region 与图像（64×48）的定义域没有交集，无法裁剪", region.Run(ContextWith(image, outside)).Message);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 矩形裁剪超出图像时沿用算子行为_多个区域对象先合并再裁剪_旋转不沿用输入定义域()
    {
        HObject image = ByteImage();
        ImageGeometryTool crop = Tool(ImageGeometryMethod.CropRectangle);
        (crop.Row1, crop.Column1, crop.Row2, crop.Column2) = (40, 50, 60, 80);
        var ctx = ContextWith(image);
        HObject cropped = Run(crop, ctx, out _, out _);
        HOperatorSet.GetImageSize(cropped, out HTuple w, out HTuple h);
        HOperatorSet.GetDomain(cropped, out HObject domain);
        HOperatorSet.AreaCenter(domain, out HTuple area, out _, out _);
        Assert.Equal((31, 21, 8 * 14), (w.I, h.I, area.I));
        Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("（超出原图的部分不在定义域内）"));

        // HALCON 的 reduce_domain 对多个区域对象只用第一个；工具先 union1，裁剪范围覆盖两个区域的外接矩形
        ImageGeometryTool region = Tool(ImageGeometryMethod.CropRegion);
        HObject regionCropped = Run(region, ContextWith(image, TwoRegions()), out HomMat2D homMat, out _);
        HOperatorSet.Union1(TwoRegions(), out HObject union);
        HOperatorSet.SmallestRectangle1(union, out HTuple top, out HTuple left, out HTuple bottom, out HTuple right);
        HOperatorSet.SmallestRectangle1(TwoRegions(), out HTuple firstTop, out _, out HTuple firstBottom, out _);
        Assert.True(firstBottom[0].I < bottom.I, "两个区域对象的外接矩形大于第一个对象");
        HOperatorSet.GetImageSize(regionCropped, out HTuple rw, out HTuple rh);
        Assert.Equal((right.I - left.I + 1, bottom.I - top.I + 1), (rw.I, rh.I));
        AssertMatrix(new double[] { 1, 0, -top.I, 0, 1, -left.I }, homMat);

        ImageGeometryTool rotate = Tool(ImageGeometryMethod.Rotate);
        rotate.AngleDeg = 0;
        HObject rotated = Run(rotate, ContextWith(Reduced(image)), out _, out _);
        HOperatorSet.GetDomain(rotated, out HObject rotatedDomain);
        HOperatorSet.AreaCenter(rotatedDomain, out HTuple rotatedArea, out _, out _);
        Assert.Equal(64 * 48, rotatedArea.I);
    }

    // ======================= 校验、显隐、序列化、登记（非 HALCON） =======================

    public static IEnumerable<object[]> RangeCases()
    {
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomFactor; t.ScaleWidth = 0; }), "ScaleWidth", "宽度缩放系数 ScaleWidth 必须大于 0（当前 0）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomFactor; t.ScaleHeight = -1.5; }), "ScaleHeight", "高度缩放系数 ScaleHeight 必须大于 0（当前 -1.5）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomFactor; t.ScaleWidth = double.NaN; }), "ScaleWidth", "宽度缩放系数 ScaleWidth 必须大于 0（当前 NaN）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomFactor; t.ScaleHeight = double.PositiveInfinity; }), "ScaleHeight", "高度缩放系数 ScaleHeight 必须大于 0（当前 Infinity）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomSize; t.TargetWidth = 0; }), "TargetWidth", "目标宽度 TargetWidth 必须在 1 ~ 32768 之间（当前 0）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomSize; t.TargetHeight = 32769; }), "TargetHeight", "目标高度 TargetHeight 必须在 1 ~ 32768 之间（当前 32769）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.ZoomSize; t.Interpolation = (GeometryInterpolation)9; }), "Interpolation", "插值方式 Interpolation 只能是 nearest_neighbor / bilinear / bicubic / constant / weighted（当前 9）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.Rotate; t.AngleDeg = double.NaN; }), "AngleDeg", "旋转角度 AngleDeg 必须是有限数（当前 NaN）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.Mirror; t.MirrorMode = (ImageMirrorMode)5; }), "MirrorMode", "镜像方式 MirrorMode 只能是 row / column / diagonal（当前 5）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.CropRectangle; t.Row1 = -1; }), "Row1", "裁剪起点 Row1 不能小于 0（当前 -1）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.CropRectangle; t.Column1 = -3; }), "Column1", "裁剪起点 Column1 不能小于 0（当前 -3）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.CropRectangle; t.Row2 = 99; }), "Row2", "裁剪终点 Row2 不能小于 Row1（当前 Row1 = 100，Row2 = 99）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.CropRectangle; t.Column2 = 50; }), "Column2", "裁剪终点 Column2 不能小于 Column1（当前 Column1 = 100，Column2 = 50）" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => { t.Method = ImageGeometryMethod.CropRegion; t.RegionPath = " "; }), "RegionPath", "CropRegion 方式需要配置裁剪区域" };
        yield return new object[] { (Action<ImageGeometryTool>)(t => t.Method = (ImageGeometryMethod)99), "Method", "未知的几何变换方式 99" };
    }

    [Theory]
    [MemberData(nameof(RangeCases))]
    public void 参数越界_流程校验与运行措辞一致(Action<ImageGeometryTool> configure, string parameter, string message)
    {
        var tool = new ImageGeometryTool("几何1") { ImagePath = "图像1.Image" };
        configure(tool);
        ToolConfigurationIssue issue = Assert.Single(tool.CheckConfiguration());
        Assert.Equal((parameter, message), (issue.Parameter, issue.Message));
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"几何1 {parameter}：{message}", result.Message);
    }

    [Fact]
    public void 默认参数校验通过_且只校验当前方式的参数()
    {
        foreach (ImageGeometryMethod method in Enum.GetValues<ImageGeometryMethod>())
        {
            var tool = new ImageGeometryTool("几何1") { Method = method, RegionPath = "区域1.Region" };
            Assert.Empty(tool.CheckConfiguration());
        }
        var other = new ImageGeometryTool("几何1")
        {
            Method = ImageGeometryMethod.Mirror,
            ScaleWidth = 0,
            TargetWidth = 0,
            AngleDeg = double.NaN,
            Row1 = -1,
            Interpolation = (GeometryInterpolation)9
        };
        Assert.Empty(other.CheckConfiguration());
    }

    private static readonly string[] Parameters =
        { "ScaleWidth", "ScaleHeight", "TargetWidth", "TargetHeight", "AngleDeg", "Interpolation", "MirrorMode", "Row1", "Column1", "Row2", "Column2", "RegionPath" };

    [Theory]
    [InlineData(ImageGeometryMethod.ZoomFactor, "ScaleWidth,ScaleHeight,Interpolation")]
    [InlineData(ImageGeometryMethod.ZoomSize, "TargetWidth,TargetHeight,Interpolation")]
    [InlineData(ImageGeometryMethod.Rotate, "AngleDeg,Interpolation")]
    [InlineData(ImageGeometryMethod.Mirror, "MirrorMode")]
    [InlineData(ImageGeometryMethod.CropRectangle, "Row1,Column1,Row2,Column2")]
    [InlineData(ImageGeometryMethod.CropRegion, "RegionPath")]
    public void 按方式精确显隐参数(ImageGeometryMethod method, string visible)
    {
        var tool = new ImageGeometryTool("几何1") { Method = method };
        Assert.Equal(visible.Split(','), Parameters.Where(tool.IsParameterVisible).ToArray());
        Assert.True(tool.IsParameterVisible("Method"));
        Assert.True(tool.IsParameterVisible("ImagePath"));
    }

    [Fact]
    public void 枚举成员顺序固定_成员名即HALCON参数值_按数字保存_缺省值()
    {
        Assert.Equal(new[] { "ZoomFactor", "ZoomSize", "Rotate", "Mirror", "CropRectangle", "CropRegion" }, Enum.GetNames<ImageGeometryMethod>());
        Assert.Equal(new[] { "nearest_neighbor", "bilinear", "bicubic", "constant", "weighted" }, Enum.GetNames<GeometryInterpolation>());
        Assert.Equal(new[] { "row", "column", "diagonal" }, Enum.GetNames<ImageMirrorMode>());

        var tool = new ImageGeometryTool("几何1")
        {
            Method = ImageGeometryMethod.CropRectangle,
            Interpolation = GeometryInterpolation.bicubic,
            MirrorMode = ImageMirrorMode.diagonal,
            AngleDeg = -37.5,
            Row1 = 3,
            RegionPath = "区域1.Region"
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"image-geometry\"", json);
        Assert.Contains("\"Method\": 4", json);
        Assert.Contains("\"Interpolation\": 2", json);
        Assert.Contains("\"MirrorMode\": 2", json);
        var loaded = (ImageGeometryTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((ImageGeometryMethod.CropRectangle, GeometryInterpolation.bicubic, ImageMirrorMode.diagonal, -37.5, 3, "区域1.Region"),
            (loaded.Method, loaded.Interpolation, loaded.MirrorMode, loaded.AngleDeg, loaded.Row1, loaded.RegionPath));

        JsonObject minimal = JsonNode.Parse(json)!.AsObject();
        JsonObject properties = minimal["Tool"]!["Properties"]!.AsObject();
        foreach (string name in properties.Select(p => p.Key).Where(k => k != "ImagePath").ToList())
        {
            properties.Remove(name);
        }
        var defaults = (ImageGeometryTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(minimal.ToJsonString())).Tool;
        Assert.Equal((ImageGeometryMethod.ZoomFactor, GeometryInterpolation.constant, ImageMirrorMode.row), (defaults.Method, defaults.Interpolation, defaults.MirrorMode));
        Assert.Equal((0.5, 0.5, 512, 512, 90.0), (defaults.ScaleWidth, defaults.ScaleHeight, defaults.TargetWidth, defaults.TargetHeight, defaults.AngleDeg));
        Assert.Null(defaults.RegionPath);
    }

    [Fact]
    public void 输出为Image_HomMat_InverseHomMat_不与参数同名_沿用矩阵变量机制()
    {
        var outputs = ToolMetadata.GetOutputs(typeof(ImageGeometryTool)).ToList();
        Assert.Equal(new[] { "Image", "HomMat", "InverseHomMat" }, outputs.Select(o => o.Name).ToArray());
        Assert.Equal(typeof(HalconImage), outputs[0].ElementClrType);
        Assert.All(outputs.Skip(1), o => Assert.Equal((VariableKind.Object, typeof(HomMat2D)), (o.Kind, o.ElementClrType)));
        var parameters = typeof(ImageGeometryTool).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain(outputs, o => parameters.Contains(o.Name));
        IReadOnlyList<ToolInputRefDef> inputs = ToolMetadata.GetInputRefs(typeof(ImageGeometryTool));
        Assert.Equal(new[] { ("ImagePath", typeof(HalconImage), false), ("RegionPath", typeof(HalconRegion), true) },
            inputs.Select(i => (i.PropertyName, i.ExpectedType, i.Optional)).ToArray());
    }

    [Fact]
    public void 工具箱登记_图标键_视觉预览路由()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "image-geometry");
        Assert.Equal(("02 图像处理", "图像几何变换"), (item.Category, item.DisplayName));
        var created = Assert.IsType<ImageGeometryTool>(Assert.IsType<ToolNode>(item.Factory()).Tool);
        Assert.StartsWith("几何变换", created.ModuleName);
        Assert.Equal(("Input.Image", ImageGeometryMethod.ZoomFactor), (created.ImagePath, created.Method));

        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        Assert.Contains("x:Key=\"ToolIcon.image-geometry\"", icons);
        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int start = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        string preview = router.Substring(start, router.IndexOf('}', start) - start);
        Assert.Contains("tool is ImageGeometryTool", preview);
    }
}
