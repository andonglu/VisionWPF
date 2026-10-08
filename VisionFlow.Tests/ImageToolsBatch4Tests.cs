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
/// IMAGE-TOOLS 第四批：IP-05 极坐标展开（PolarUnwrapTool）+ IP-06 极坐标逆变换（PolarInverseTool）。
/// 标注 Requires=HALCON 的用例与直接调用 HALCON 算子对照；其余用例（校验、显隐、序列化、登记、PolarParams）不调用 HALCON 算子。
/// </summary>
public class ImageToolsBatch4Tests
{
    // ======================= 合成图像与对照工具 =======================

    private const int W = 260, H = 200;
    private const double CR = 100, CC = 130;

    /// <summary>以 (100, 130) 为圆心的圆环（半径 20 ~ 70，灰度 100）上画一块扇环缺陷（半径 35 ~ 50、角度 0.8 ~ 1.3 弧度，灰度 230）。</summary>
    private static HObject RingWithDefect(out HObject defect)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", W, H);
        HOperatorSet.GenCircle(out HObject outer, CR, CC, 70);
        HOperatorSet.GenCircle(out HObject inner, CR, CC, 20);
        HOperatorSet.Difference(outer, inner, out HObject ring);
        HOperatorSet.PaintRegion(ring, blank, out HObject withRing, 100, "fill");
        var rows = new List<double>();
        var cols = new List<double>();
        for (double a = 0.8; a <= 1.3001; a += 0.01) { rows.Add(CR - 50 * Math.Sin(a)); cols.Add(CC + 50 * Math.Cos(a)); }
        for (double a = 1.3; a >= 0.7999; a -= 0.01) { rows.Add(CR - 35 * Math.Sin(a)); cols.Add(CC + 35 * Math.Cos(a)); }
        HOperatorSet.GenRegionPolygonFilled(out defect, new HTuple(rows.ToArray()), new HTuple(cols.ToArray()));
        HOperatorSet.PaintRegion(defect, withRing, out HObject image, 230, "fill");
        blank.Dispose(); outer.Dispose(); inner.Dispose(); ring.Dispose(); withRing.Dispose();
        return image;
    }

    private static HObject Gradient()
    {
        HOperatorSet.GenImageGrayRamp(out HObject image, 0.4, 0.6, 120, H / 2, W / 2, W, H);
        return image;
    }

    private static HObject Converted(HObject image, string type)
    {
        HOperatorSet.ConvertImageType(image, out HObject converted, type);
        return converted;
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
        for (int channel = 1; channel <= ec.I; channel++)
        {
            HOperatorSet.AccessChannel(expected, out HObject e, channel);
            HOperatorSet.AccessChannel(actual, out HObject a, channel);
            HOperatorSet.SubImage(Converted(e, "real"), Converted(a, "real"), out HObject diff, 1, 0);
            HOperatorSet.MinMaxGray(ed, diff, 0, out HTuple min, out HTuple max, out _);
            Assert.True(min.D == 0 && max.D == 0, $"通道 {channel} 像素差 [{min.D}, {max.D}]");
        }
    }

    private static FlowContext ContextWith(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static PolarUnwrapTool Unwrap() => new PolarUnwrapTool("展开1")
    {
        ImagePath = "图像1.Image",
        CenterRow = CR,
        CenterColumn = CC,
        AngleStartDeg = 0,
        AngleEndDeg = 360,
        RadiusStart = 20,
        RadiusEnd = 70
    };

    private static HObject RunUnwrap(PolarUnwrapTool tool, FlowContext ctx, out PolarParams parameters)
    {
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        parameters = (PolarParams)ctx.GetVariable(tool.ModuleName, "PolarParams").Value;
        return ((HalconImage)ctx.GetVariable(tool.ModuleName, "Image").Value).Object;
    }

    private static double Area(HObject region)
    {
        HOperatorSet.Union1(region, out HObject all);
        HOperatorSet.AreaCenter(all, out HTuple area, out _, out _);
        return area.D;
    }

    // ======================= IP-05 与直接调用逐像素一致（门禁） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(PolarInterpolation.nearest_neighbor, 0, 0)]
    [InlineData(PolarInterpolation.bilinear, 0, 0)]
    [InlineData(PolarInterpolation.bilinear, 500, 64)]
    public void 展开结果与直接调用polar_trans_image_ext逐像素一致_PolarParams为实际值(PolarInterpolation interpolation, int width, int height)
    {
        PolarUnwrapTool tool = Unwrap();
        (tool.AngleStartDeg, tool.AngleEndDeg, tool.RadiusStart, tool.RadiusEnd) = (30, -150, 70, 25);
        (tool.OutputWidth, tool.OutputHeight, tool.Interpolation) = (width, height, interpolation);
        double a0 = 30 * Math.PI / 180, a1 = -150 * Math.PI / 180;
        int expectedWidth = width > 0 ? width : (int)Math.Round(Math.Abs(a1 - a0) * 70);
        int expectedHeight = height > 0 ? height : 45;
        HObject gradient = Gradient();
        HOperatorSet.Compose3(gradient, Converted(gradient, "byte"), gradient, out HObject rgb);
        HOperatorSet.GenRectangle1(out HObject part, 30, 40, 160, 220);
        HOperatorSet.ReduceDomain(gradient, part, out HObject reduced);
        foreach (HObject input in new[] { gradient, rgb, reduced })
        {
            HOperatorSet.PolarTransImageExt(input, out HObject expected, CR, CC, a0, a1, 70, 25, expectedWidth, expectedHeight, interpolation.ToString());
            var ctx = ContextWith(input);
            HObject actual = RunUnwrap(tool, ctx, out PolarParams p);
            AssertSameImage(expected, actual);
            Assert.Equal((CR, CC, a0, a1, 70.0, 25.0, expectedWidth, expectedHeight, W, H, interpolation),
                (p.CenterRow, p.CenterColumn, p.AngleStart, p.AngleEnd, p.RadiusStart, p.RadiusEnd, p.Width, p.Height, p.ImageWidth, p.ImageHeight, p.Interpolation));
            Assert.NotSame(input, actual);
            Assert.True(input.IsInitialized());
            ctx.Dispose();
        }
    }

    // ======================= 第 8 节验收：缺陷展开、检测、逆变换回原图（门禁） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 合成圆环缺陷_展开后检测_区域经逆变换与原缺陷重合_面积误差小于5百分比()
    {
        HObject image = RingWithDefect(out HObject defect);
        var ctx = ContextWith(image);
        HObject polar = RunUnwrap(Unwrap(), ctx, out PolarParams p);
        Assert.Equal((440, 50), (p.Width, p.Height));

        var detect = new ThresholdTool("缺陷") { ImagePath = "展开1.Image", MinGray = 165, MaxGray = 255 };
        Assert.True(detect.Run(ctx).IsSuccess);
        var inverse = new PolarInverseTool("回原图") { PolarParamsPath = "展开1.PolarParams", RegionPath = "缺陷.Region" };
        Assert.True(inverse.Run(ctx).IsSuccess);
        HObject back = ((HalconRegion)ctx.GetVariable("回原图", "Region").Value).Object;

        double defectArea = Area(defect), backArea = Area(back);
        HOperatorSet.Intersection(defect, back, out HObject overlap);
        double overlapArea = Area(overlap);
        Assert.True(Math.Abs(backArea - defectArea) / defectArea < 0.05, $"逆变换面积 {backArea}，原缺陷 {defectArea}");
        Assert.True(overlapArea / defectArea > 0.95, $"重叠 {overlapArea}，原缺陷 {defectArea}");
        Assert.Equal(1, ctx.GetVariable("回原图", "Count").Value);
        Assert.Equal(true, ctx.GetVariable("回原图", "Found").Value);
        HOperatorSet.CountObj(((HalconXld)ctx.GetVariable("回原图", "Xld").Value).Object, out HTuple xldCount);
        Assert.Equal(0, xldCount.I);

        // 与直接调用 polar_trans_region_inv 一致
        HOperatorSet.PolarTransRegionInv(((HalconRegion)ctx.GetVariable("缺陷", "Region").Value).Object, out HObject direct,
            CR, CC, 0, 2 * Math.PI, 20, 70, 440, 50, W, H, "nearest_neighbor");
        HOperatorSet.SymmDifference(direct, back, out HObject diff);
        Assert.Equal(0.0, Area(diff));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void XLD经逆变换回原图_只配置XLD时Count与Found取轮廓数_区域输出为空对象()
    {
        HObject image = RingWithDefect(out HObject defect);
        var ctx = ContextWith(image);
        RunUnwrap(Unwrap(), ctx, out PolarParams p);
        var detect = new ThresholdTool("缺陷") { ImagePath = "展开1.Image", MinGray = 165, MaxGray = 255 };
        Assert.True(detect.Run(ctx).IsSuccess);
        HOperatorSet.GenContourRegionXld(((HalconRegion)ctx.GetVariable("缺陷", "Region").Value).Object, out HObject contour, "border");
        HOperatorSet.CountObj(contour, out HTuple contourCount);
        Assert.True(contourCount.I >= 1);
        ctx.SetVariable(Variable.Object("轮廓", "Xld", new HalconXld(contour), contourCount.I));

        var inverse = new PolarInverseTool("回原图") { PolarParamsPath = "展开1.PolarParams", XldPath = "轮廓.Xld" };
        Assert.True(inverse.Run(ctx).IsSuccess);
        HObject backXld = ((HalconXld)ctx.GetVariable("回原图", "Xld").Value).Object;
        HOperatorSet.PolarTransContourXldInv(contour, out HObject direct, CR, CC, 0, 2 * Math.PI, 20, 70, p.Width, p.Height, W, H);
        HOperatorSet.GetContourXld(backXld, out HTuple rows, out HTuple cols);
        HOperatorSet.GetContourXld(direct, out HTuple directRows, out HTuple directCols);
        Assert.Equal(directRows.ToDArr(), rows.ToDArr());
        Assert.Equal(directCols.ToDArr(), cols.ToDArr());
        HOperatorSet.GenRegionContourXld(backXld, out HObject filled, "filled");
        // border 轮廓沿像素外沿，小缺陷围成的面积偏大，这里核对位置：轮廓围成区域的中心与原缺陷中心相差 < 1 像素
        HOperatorSet.AreaCenter(filled, out _, out HTuple filledRow, out HTuple filledColumn);
        HOperatorSet.AreaCenter(defect, out _, out HTuple defectRow, out HTuple defectColumn);
        double shift = Math.Sqrt(Math.Pow(filledRow.D - defectRow.D, 2) + Math.Pow(filledColumn.D - defectColumn.D, 2));
        Assert.True(shift < 1.0, $"轮廓围成区域中心 ({filledRow.D:F2}, {filledColumn.D:F2})，原缺陷中心 ({defectRow.D:F2}, {defectColumn.D:F2})");
        Assert.Equal((contourCount.I, true), ((int)ctx.GetVariable("回原图", "Count").Value, (bool)ctx.GetVariable("回原图", "Found").Value));
        HOperatorSet.CountObj(((HalconRegion)ctx.GetVariable("回原图", "Region").Value).Object, out HTuple regionCount);
        Assert.Equal(0, regionCount.I);

        // 两者都配置：Count / Found 取区域（RegionOutput 既有机制）
        var both = new PolarInverseTool("两者") { PolarParamsPath = "展开1.PolarParams", RegionPath = "缺陷.Region", XldPath = "轮廓.Xld" };
        Assert.True(both.Run(ctx).IsSuccess);
        Assert.Equal(1, ctx.GetVariable("两者", "Count").Value);
        HOperatorSet.CountObj(((HalconXld)ctx.GetVariable("两者", "Xld").Value).Object, out HTuple bothXld);
        Assert.Equal(contourCount.I, bothXld.I);
    }

    // ======================= 跟随：圆心引用接圆形测量、定位矩阵（门禁） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 圆心引用接圆形测量输出时跟随实测圆心()
    {
        // 实际圆盘圆心 (110, 145)、半径 80；圆形测量的示教基准故意偏到 (106, 141)（在默认搜索长度内）
        HOperatorSet.GenImageConst(out HObject blank, "byte", W, H);
        HOperatorSet.GenCircle(out HObject disc, 110, 145, 80);
        HOperatorSet.PaintRegion(disc, blank, out HObject image, 200, "fill");
        var ctx = ContextWith(image);
        var circle = new CircleFollowMeasureTool("圆1") { ImagePath = "图像1.Image", BaseRow = 106, BaseColumn = 141, BaseRadius = 80 };
        Assert.True(circle.Run(ctx).IsSuccess);
        double row = (double)ctx.GetVariable("圆1", "Row").Value, column = (double)ctx.GetVariable("圆1", "Column").Value;
        Assert.True(Math.Abs(row - 110) < 1.0 && Math.Abs(column - 145) < 1.0, $"圆形测量 ({row}, {column})");

        PolarUnwrapTool tool = Unwrap();
        (tool.CenterRowPath, tool.CenterColumnPath) = ("圆1.Row", "圆1.Column");
        (tool.CenterRow, tool.CenterColumn) = (1, 1);
        (tool.RadiusStart, tool.RadiusEnd, tool.OutputWidth, tool.OutputHeight) = (60, 100, 360, 40);
        HObject actual = RunUnwrap(tool, ctx, out PolarParams p);
        Assert.Equal((row, column), (p.CenterRow, p.CenterColumn));
        HOperatorSet.PolarTransImageExt(image, out HObject expected, row, column, 0, 2 * Math.PI, 60, 100, 360, 40, "nearest_neighbor");
        AssertSameImage(expected, actual);
        // 圆盘边缘（半径 80）在展开图中应为一条水平边：第 17 行（半径约 77.4）为亮、第 23 行（半径约 83.6）为暗，沿整圈都如此
        HOperatorSet.GetGrayval(actual, new HTuple(17, 23, 17, 23), new HTuple(10, 10, 300, 300), out HTuple gray);
        Assert.Equal(new[] { 200, 0, 200, 0 }, gray.ToIArr());

        ctx.SetVariable(Variable.Single("圆1", "Row", VariableType.Double, double.NaN));
        Assert.Equal("展开1 圆心引用的值无效（圆1.Row = NaN，圆1.Column = " + column.ToString("G", System.Globalization.CultureInfo.InvariantCulture) + "），上游可能未找到圆", tool.Run(ctx).Message);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 定位矩阵跟随_圆心变换_起止角加旋转量_半径乘缩放_镜像与多矩阵报错()
    {
        HObject image = Gradient();
        var ctx = ContextWith(image);
        HOperatorSet.VectorAngleToRigid(0, 0, 0, 12, -8, 0.4, out HTuple rigid);
        HOperatorSet.HomMat2dScale(rigid, 1.2, 1.2, 12, -8, out HTuple similarity);
        ctx.SetVariable(Variable.Object("定位", "HomMat", new HomMat2D(similarity), 1));

        PolarUnwrapTool tool = Unwrap();
        (tool.MatrixPath, tool.AngleStartDeg, tool.AngleEndDeg, tool.OutputWidth, tool.OutputHeight) = ("定位.HomMat", 10, 100, 200, 50);
        HObject actual = RunUnwrap(tool, ctx, out PolarParams p);
        HOperatorSet.AffineTransPoint2d(similarity, CR, CC, out HTuple row, out HTuple column);
        Assert.Equal(row.D, p.CenterRow, 9);
        Assert.Equal(column.D, p.CenterColumn, 9);
        Assert.Equal(10 * Math.PI / 180 + 0.4, p.AngleStart, 9);
        Assert.Equal(100 * Math.PI / 180 + 0.4, p.AngleEnd, 9);
        Assert.Equal((24.0, 84.0), (Math.Round(p.RadiusStart, 9), Math.Round(p.RadiusEnd, 9)));
        HOperatorSet.PolarTransImageExt(image, out HObject expected, p.CenterRow, p.CenterColumn, p.AngleStart, p.AngleEnd, p.RadiusStart, p.RadiusEnd, 200, 50, "nearest_neighbor");
        AssertSameImage(expected, actual);

        HOperatorSet.HomMat2dReflect(new HTuple(1.0, 0, 0, 0, 1, 0), 0, 0, 10, 0, out HTuple mirror);
        ctx.SetVariable(Variable.Object("镜像", "HomMat", new HomMat2D(mirror), 1));
        tool.MatrixPath = "镜像.HomMat";
        Assert.StartsWith("展开1 定位矩阵 镜像.HomMat 含镜像或退化（行列式 ", tool.Run(ctx).Message);

        ctx.SetVariable(Variable.Array("多个", "HomMats", VariableType.Object, new[] { new HomMat2D(rigid), new HomMat2D(rigid) }));
        tool.MatrixPath = "多个.HomMats";
        Assert.Equal("展开1 的定位矩阵只支持单个矩阵（当前为 2 个）；多个工件请放在 For 循环中引用 Loop.Current.HomMat", tool.Run(ctx).Message);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 自动展开尺寸超过上限时给出中文错误()
    {
        PolarUnwrapTool tool = Unwrap();
        (tool.AngleStartDeg, tool.AngleEndDeg, tool.RadiusEnd) = (0, 36000, 70);
        Assert.Equal("展开1 OutputWidth：自动计算的展开宽度 43982（角度跨度 36000° × 外半径 70）超过 32768，请填写 OutputWidth", tool.Run(ContextWith(Gradient())).Message);
    }

    // ======================= 校验、显隐、序列化、登记（非 HALCON） =======================

    public static IEnumerable<object[]> UnwrapRangeCases()
    {
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.CenterRowPath = "圆1.Row"), "CenterRowPath / CenterColumnPath", "圆心行、列引用须同时配置（当前只配置了圆心行）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.CenterColumnPath = "圆1.Column"), "CenterRowPath / CenterColumnPath", "圆心行、列引用须同时配置（当前只配置了圆心列）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => { (t.CenterRowPath, t.CenterColumnPath, t.MatrixPath) = ("圆1.Row", "圆1.Column", "定位.HomMat"); }), "MatrixPath", "已配置圆心引用，不能同时配置定位矩阵（引用已给出当前圆心）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.CenterRow = double.NaN), "CenterRow", "圆心行 CenterRow 必须是有限数（当前 NaN）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.CenterColumn = double.PositiveInfinity), "CenterColumn", "圆心列 CenterColumn 必须是有限数（当前 Infinity）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.AngleStartDeg = double.NaN), "AngleStartDeg", "起始角 AngleStartDeg 必须是有限数（当前 NaN）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.AngleEndDeg = 0), "AngleEndDeg", "起止角 AngleStartDeg 与 AngleEndDeg 不能相同（当前都为 0）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.RadiusStart = -1), "RadiusStart", "起始半径 RadiusStart 不能小于 0（当前 -1）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.RadiusEnd = -2.5), "RadiusEnd", "终止半径 RadiusEnd 不能小于 0（当前 -2.5）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.RadiusEnd = 0), "RadiusEnd", "起止半径 RadiusStart 与 RadiusEnd 不能相同（当前都为 0）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.OutputWidth = -1), "OutputWidth", "展开宽度 OutputWidth 必须为 0（自动）或 1 ~ 32768（当前 -1）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.OutputHeight = 32769), "OutputHeight", "展开高度 OutputHeight 必须为 0（自动）或 1 ~ 32768（当前 32769）" };
        yield return new object[] { (Action<PolarUnwrapTool>)(t => t.Interpolation = (PolarInterpolation)5), "Interpolation", "插值方式 Interpolation 只能是 nearest_neighbor / bilinear（当前 5）" };
    }

    [Theory]
    [MemberData(nameof(UnwrapRangeCases))]
    public void 极坐标展开_参数越界_流程校验与运行措辞一致(Action<PolarUnwrapTool> configure, string parameter, string message)
    {
        var tool = new PolarUnwrapTool("展开1") { ImagePath = "图像1.Image" };
        configure(tool);
        ToolConfigurationIssue issue = Assert.Single(tool.CheckConfiguration());
        Assert.Equal((parameter, message), (issue.Parameter, issue.Message));
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"展开1 {parameter}：{message}", result.Message);
    }

    [Fact]
    public void 极坐标逆变换_极坐标参数必填_区域与XLD至少配置一个_校验与运行措辞一致()
    {
        var none = new PolarInverseTool("回原图");
        Assert.Equal(new[] { ("PolarParamsPath", "需要配置极坐标参数（引用“极坐标展开”输出的 PolarParams）"), ("RegionPath / XldPath", "区域与 XLD 至少配置一个") },
            none.CheckConfiguration().Select(i => (i.Parameter, i.Message)).ToArray());
        Assert.Equal("回原图 PolarParamsPath：需要配置极坐标参数（引用“极坐标展开”输出的 PolarParams）", none.Run(new FlowContext()).Message);

        var noTarget = new PolarInverseTool("回原图") { PolarParamsPath = "展开1.PolarParams" };
        Assert.Equal("回原图 RegionPath / XldPath：区域与 XLD 至少配置一个", noTarget.Run(new FlowContext()).Message);
        Assert.Empty(new PolarInverseTool("回原图") { PolarParamsPath = "展开1.PolarParams", RegionPath = "缺陷.Region" }.CheckConfiguration());
        Assert.Empty(new PolarInverseTool("回原图") { PolarParamsPath = "展开1.PolarParams", XldPath = "轮廓.Xld" }.CheckConfiguration());

        // 极坐标参数无效（如手工构造的 0 尺寸）在调用算子前报错
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("展开1", "PolarParams", new PolarParams { Width = 0, Height = 10, ImageWidth = 100, ImageHeight = 80 }, 1));
        Assert.StartsWith("回原图 极坐标参数 展开1.PolarParams 无效（PolarParams[圆心 (0, 0)", new PolarInverseTool("回原图") { PolarParamsPath = "展开1.PolarParams", RegionPath = "缺陷.Region" }.Run(ctx).Message);
    }

    [Fact]
    public void 默认参数校验通过()
    {
        Assert.Empty(new PolarUnwrapTool("展开1").CheckConfiguration());
        Assert.Empty(new PolarUnwrapTool("展开1") { CenterRowPath = "圆1.Row", CenterColumnPath = "圆1.Column", CenterRow = double.NaN }.CheckConfiguration());
        Assert.Empty(new PolarUnwrapTool("展开1") { MatrixPath = "定位.HomMat", AngleStartDeg = 350, AngleEndDeg = -10, RadiusStart = 80, RadiusEnd = 0, OutputWidth = 32768 }.CheckConfiguration());
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("圆1.Row", null, true)]
    [InlineData(null, "圆1.Column", true)]
    [InlineData("圆1.Row", "圆1.Column", false)]
    public void 固定圆心按圆心引用显隐(string? rowPath, string? columnPath, bool fixedVisible)
    {
        var tool = new PolarUnwrapTool("展开1") { CenterRowPath = rowPath, CenterColumnPath = columnPath };
        Assert.Equal((fixedVisible, fixedVisible), (tool.IsParameterVisible("CenterRow"), tool.IsParameterVisible("CenterColumn")));
        foreach (string name in new[] { "ImagePath", "CenterRowPath", "CenterColumnPath", "MatrixPath", "AngleStartDeg", "AngleEndDeg", "RadiusStart", "RadiusEnd", "OutputWidth", "OutputHeight", "Interpolation" })
        {
            Assert.True(tool.IsParameterVisible(name), name);
        }
    }

    [Fact]
    public void PolarParams记录实际值_度与弧度换算_文本摘要()
    {
        var p = new PolarParams
        {
            CenterRow = 100.5,
            CenterColumn = 130.25,
            AngleStart = Math.PI / 6,
            AngleEnd = -Math.PI / 2,
            RadiusStart = 20,
            RadiusEnd = 70,
            Width = 440,
            Height = 50,
            ImageWidth = 260,
            ImageHeight = 200
        };
        Assert.Equal((30.0, -90.0), (Math.Round(p.AngleStartDeg, 9), Math.Round(p.AngleEndDeg, 9)));
        Assert.Equal("PolarParams[圆心 (100.5, 130.25)，角度 30° ~ -90°，半径 20 ~ 70，展开图 440×50，原图 260×200]", p.ToString());
    }

    [Fact]
    public void 枚举按数字保存_成员名即HALCON参数值_缺省值()
    {
        Assert.Equal(new[] { "nearest_neighbor", "bilinear" }, Enum.GetNames<PolarInterpolation>());
        var tool = new PolarUnwrapTool("展开1")
        {
            Interpolation = PolarInterpolation.bilinear,
            CenterRowPath = "圆1.Row",
            CenterColumnPath = "圆1.Column",
            AngleStartDeg = -45,
            OutputWidth = 720
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"polar-unwrap\"", json);
        Assert.Contains("\"Interpolation\": 1", json);
        var loaded = (PolarUnwrapTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((PolarInterpolation.bilinear, "圆1.Row", "圆1.Column", -45.0, 720), (loaded.Interpolation, loaded.CenterRowPath, loaded.CenterColumnPath, loaded.AngleStartDeg, loaded.OutputWidth));

        JsonObject minimal = JsonNode.Parse(json)!.AsObject();
        JsonObject properties = minimal["Tool"]!["Properties"]!.AsObject();
        foreach (string name in properties.Select(p => p.Key).Where(k => k != "ImagePath").ToList())
        {
            properties.Remove(name);
        }
        var defaults = (PolarUnwrapTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(minimal.ToJsonString())).Tool;
        Assert.Equal((PolarInterpolation.nearest_neighbor, 256.0, 256.0, 0.0, 360.0, 0.0, 100.0, 0, 0),
            (defaults.Interpolation, defaults.CenterRow, defaults.CenterColumn, defaults.AngleStartDeg, defaults.AngleEndDeg, defaults.RadiusStart, defaults.RadiusEnd, defaults.OutputWidth, defaults.OutputHeight));

        string inverseJson = FlowSerializer.SaveNode(new ToolNode(new PolarInverseTool("回原图") { PolarParamsPath = "展开1.PolarParams", XldPath = "轮廓.Xld", FailWhenNotFound = false }));
        Assert.Contains("\"ToolId\": \"polar-inverse\"", inverseJson);
        var inverse = (PolarInverseTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(inverseJson)).Tool;
        Assert.Equal(("展开1.PolarParams", null, "轮廓.Xld", false), (inverse.PolarParamsPath, inverse.RegionPath, inverse.XldPath, inverse.FailWhenNotFound));
    }

    [Fact]
    public void 输出名与类型_不与参数同名_输入元数据()
    {
        var unwrapOutputs = ToolMetadata.GetOutputs(typeof(PolarUnwrapTool)).ToList();
        Assert.Equal(new[] { ("Image", typeof(HalconImage)), ("PolarParams", typeof(PolarParams)) }, unwrapOutputs.Select(o => (o.Name, o.ElementClrType)).ToArray());
        var inverseOutputs = ToolMetadata.GetOutputs(typeof(PolarInverseTool)).ToList();
        Assert.Equal(new[] { "Region", "Xld", "Count", "Found" }, inverseOutputs.Select(o => o.Name).ToArray());
        foreach (Type type in new[] { typeof(PolarUnwrapTool), typeof(PolarInverseTool) })
        {
            var parameters = type.GetProperties().Select(p => p.Name).ToHashSet();
            Assert.DoesNotContain(ToolMetadata.GetOutputs(type), o => parameters.Contains(o.Name));
        }
        Assert.Equal(new[] { ("ImagePath", typeof(HalconImage), false), ("CenterRowPath", typeof(double), true), ("CenterColumnPath", typeof(double), true), ("MatrixPath", typeof(HomMat2D), true) },
            ToolMetadata.GetInputRefs(typeof(PolarUnwrapTool)).Select(i => (i.PropertyName, i.ExpectedType, i.Optional)).ToArray());
        Assert.Equal(new[] { ("PolarParamsPath", typeof(PolarParams), false), ("RegionPath", typeof(HalconRegion), true), ("XldPath", typeof(HalconXld), true) },
            ToolMetadata.GetInputRefs(typeof(PolarInverseTool)).Select(i => (i.PropertyName, i.ExpectedType, i.Optional)).ToArray());
    }

    [Fact]
    public void 工具箱登记_图标键_编辑器路由()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem unwrap = ToolboxRegistry.Items.Single(i => i.Id == "polar-unwrap");
        ToolboxItem inverse = ToolboxRegistry.Items.Single(i => i.Id == "polar-inverse");
        Assert.Equal(("02 图像处理", "极坐标展开"), (unwrap.Category, unwrap.DisplayName));
        Assert.Equal(("02 图像处理", "极坐标逆变换"), (inverse.Category, inverse.DisplayName));
        Assert.StartsWith("极坐标展开", Assert.IsType<PolarUnwrapTool>(Assert.IsType<ToolNode>(unwrap.Factory()).Tool).ModuleName);
        Assert.StartsWith("极坐标逆变换", Assert.IsType<PolarInverseTool>(Assert.IsType<ToolNode>(inverse.Factory()).Tool).ModuleName);

        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        Assert.Contains("x:Key=\"ToolIcon.polar-unwrap\"", icons);
        Assert.Contains("x:Key=\"ToolIcon.polar-inverse\"", icons);
        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int start = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        string preview = router.Substring(start, router.IndexOf('}', start) - start);
        Assert.Contains("tool is PolarInverseTool", preview);
        Assert.DoesNotContain("tool is PolarUnwrapTool", preview);
        Assert.Contains("new WpfPolarUnwrapToolEditWindow(polarUnwrapTool, context)", router.Substring(0, start));
    }
}
