using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;
using Xunit.Abstractions;

namespace VisionFlow.Tests;

/// <summary>合成晶圆图像：圆形晶圆上按（可旋转的）网格排布的矩形 chip，带抗锯齿边缘与白噪声，附真值。</summary>
internal static class SyntheticWafer
{
    internal sealed class Chip
    {
        public int GridRow;
        public int GridColumn;
        public double Row;
        public double Column;
        public double Phi;
    }

    public const int Size = 2400;
    public const double RowPitch = 75;
    public const double ColumnPitch = 90;
    public const double HalfWidth = 35;
    public const double HalfHeight = 23;

    /// <summary>生成晶圆图像；missing 中的 (行, 列)（相对晶圆中心的网格坐标）不放 chip。</summary>
    public static HObject Create(double gridAngle, int seed, IEnumerable<(int Row, int Column)> missing, out List<Chip> chips,
        double waferRadius = 1100, double noise = 16)
    {
        const int factor = 4;
        var skip = new HashSet<(int, int)>(missing);
        var random = new Random(seed);
        double center = Size / 2.0;
        double cos = Math.Cos(gridAngle);
        double sin = Math.Sin(gridAngle);
        chips = new List<Chip>();
        int range = (int)(waferRadius / Math.Min(RowPitch, ColumnPitch)) + 1;
        for (int i = -range; i <= range; i++)
        {
            for (int j = -range; j <= range; j++)
            {
                double gridRow = i * RowPitch;
                double gridColumn = j * ColumnPitch;
                // 行方向单位向量 (cos, sin)，列方向单位向量 (-sin, cos)
                double row = center + gridRow * cos - gridColumn * sin + (random.NextDouble() - 0.5) * 4;
                double column = center + gridRow * sin + gridColumn * cos + (random.NextDouble() - 0.5) * 4;
                double phi = gridAngle + (random.NextDouble() - 0.5) * 0.01;
                if (Math.Sqrt((row - center) * (row - center) + (column - center) * (column - center)) + 60 > waferRadius
                    || skip.Contains((i, j)))
                {
                    continue;
                }
                chips.Add(new Chip { GridRow = i, GridColumn = j, Row = row, Column = column, Phi = phi });
            }
        }

        int big = Size * factor;
        HOperatorSet.GenImageConst(out HObject canvas, "byte", big, big);
        HObject? painted = null;
        HObject? disc = null;
        HObject? chipRegions = null;
        try
        {
            HOperatorSet.GenCircle(out disc, center * factor + 1.5, center * factor + 1.5, waferRadius * factor);
            HOperatorSet.GenRectangle2(out chipRegions,
                new HTuple(chips.Select(c => c.Row * factor + 1.5).ToArray()),
                new HTuple(chips.Select(c => c.Column * factor + 1.5).ToArray()),
                new HTuple(chips.Select(c => c.Phi).ToArray()),
                new HTuple(Enumerable.Repeat(HalfWidth * factor, chips.Count).ToArray()),
                new HTuple(Enumerable.Repeat(HalfHeight * factor, chips.Count).ToArray()));
            HOperatorSet.GenImageProto(canvas, out HObject background, 30);
            HOperatorSet.PaintRegion(disc, background, out HObject withDisc, 90, "fill");
            background.Dispose();
            HOperatorSet.PaintRegion(chipRegions, withDisc, out HObject withChips, 210, "fill");
            withDisc.Dispose();
            // chip 内左上角的暗色方向标记（距边缘 ≥ 9 像素）：灰度分割得到带孔区域，重心偏离几何中心
            HOperatorSet.GenRectangle2(out HObject marks,
                new HTuple(chips.Select(c => (c.Row - 11 * Math.Cos(c.Phi) + 20 * Math.Sin(c.Phi)) * factor + 1.5).ToArray()),
                new HTuple(chips.Select(c => (c.Column - 11 * Math.Sin(c.Phi) - 20 * Math.Cos(c.Phi)) * factor + 1.5).ToArray()),
                new HTuple(chips.Select(c => c.Phi).ToArray()),
                new HTuple(Enumerable.Repeat(5.0 * factor, chips.Count).ToArray()),
                new HTuple(Enumerable.Repeat(3.0 * factor, chips.Count).ToArray()));
            HOperatorSet.PaintRegion(marks, withChips, out painted, 100, "fill");
            withChips.Dispose();
            marks.Dispose();
            HOperatorSet.ZoomImageFactor(painted, out HObject small, 1.0 / factor, 1.0 / factor, "constant");
            if (noise <= 0)
            {
                return small;
            }
            HOperatorSet.SetSystem("seed_rand", seed);
            HOperatorSet.AddNoiseWhite(small, out HObject noisy, noise);
            small.Dispose();
            return noisy;
        }
        finally
        {
            canvas.Dispose();
            painted?.Dispose();
            disc?.Dispose();
            chipRegions?.Dispose();
        }
    }
}

/// <summary>区域排序（行列编号）与区域初始化测量的对齐数组；晶圆 chip 定位示例。</summary>
public class RegionSortTests
{
    private readonly ITestOutputHelper _output;

    public RegionSortTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static FlowContext Segment(HObject image)
    {
        HOperatorSet.Threshold(image, out HObject region, 150, 255);
        HOperatorSet.Connection(region, out HObject connected);
        region.Dispose();
        HOperatorSet.SelectShape(connected, out HObject chips, "area", "and", 2000, 5000);
        connected.Dispose();
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        HOperatorSet.CountObj(chips, out HTuple count);
        ctx.SetVariable(Variable.Object("分割", "Region", HalconRegion.Owned(chips), count.I));
        return ctx;
    }

    /// <summary>把真值网格坐标换成从 0 开始的行列号。</summary>
    private static Dictionary<(int, int), SyntheticWafer.Chip> TruthByIndex(List<SyntheticWafer.Chip> chips)
    {
        int minRow = chips.Min(c => c.GridRow);
        int minColumn = chips.Min(c => c.GridColumn);
        return chips.ToDictionary(c => (c.GridRow - minRow, c.GridColumn - minColumn));
    }

    private static void AssertIndicesMatchTruth(FlowContext ctx, string module, List<SyntheticWafer.Chip> chips)
    {
        Dictionary<(int, int), SyntheticWafer.Chip> truth = TruthByIndex(chips);
        var items = ctx.GetVariable(module, "Items").GetValue<RegionGridItem[]>();
        Assert.Equal(chips.Count, items.Length);
        Assert.Equal(0, (int)ctx.GetVariable(module, "DuplicateCount").Value);
        foreach (RegionGridItem item in items)
        {
            Assert.True(truth.TryGetValue((item.RowIndex, item.ColumnIndex), out SyntheticWafer.Chip? chip),
                $"多出的行列号 [{item.RowIndex},{item.ColumnIndex}]");
            Assert.True(Math.Abs(item.Row - chip.Row) < 1.5 && Math.Abs(item.Column - chip.Column) < 1.5,
                $"[{item.RowIndex},{item.ColumnIndex}] 中心 ({item.Row:F1},{item.Column:F1}) 与真值 ({chip.Row:F1},{chip.Column:F1}) 不符");
        }
    }

    [Fact]
    public void 晶圆网格_行列号与真值一致_行优先输出且缺料不影响列号()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = SyntheticWafer.Create(0, 7, new[] { (0, 0), (2, -3), (-5, 4) }, out List<SyntheticWafer.Chip> chips);
        using var imageOwner = image;
        using FlowContext ctx = Segment(image);
        var tool = new RegionSortTool("排序1") { RegionPath = "分割.Region" };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        AssertIndicesMatchTruth(ctx, "排序1", chips);
        Assert.Equal(chips.Max(c => c.GridRow) - chips.Min(c => c.GridRow) + 1, (int)ctx.GetVariable("排序1", "RowCount").Value);
        Assert.Equal(chips.Max(c => c.GridColumn) - chips.Min(c => c.GridColumn) + 1, (int)ctx.GetVariable("排序1", "ColumnCount").Value);
        var rowIndices = ctx.GetVariable("排序1", "RowIndices").GetValue<int[]>();
        var columnIndices = ctx.GetVariable("排序1", "ColumnIndices").GetValue<int[]>();
        for (int i = 1; i < rowIndices.Length; i++)
        {
            Assert.True(rowIndices[i] > rowIndices[i - 1]
                || (rowIndices[i] == rowIndices[i - 1] && columnIndices[i] > columnIndices[i - 1]),
                $"第 {i} 项未按行优先排序");
        }
        // 输出区域的对象顺序与行列号数组一致
        var region = (HalconRegion)ctx.GetVariable("排序1", "Region").Value;
        HOperatorSet.AreaCenter(region.Object, out _, out HTuple rows, out HTuple columns);
        var sortedRows = ctx.GetVariable("排序1", "Rows").GetValue<double[]>();
        var sortedColumns = ctx.GetVariable("排序1", "Columns").GetValue<double[]>();
        Assert.Equal(sortedRows, rows.ToDArr());
        Assert.Equal(sortedColumns, columns.ToDArr());
    }

    [Fact]
    public void 网格旋转_自动角度仍能正确编号_不估角度时编号错乱()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        double angle = 3 * Math.PI / 180;
        HObject image = SyntheticWafer.Create(angle, 11, new[] { (1, 1) }, out List<SyntheticWafer.Chip> chips);
        using var imageOwner = image;
        using FlowContext ctx = Segment(image);
        var auto = new RegionSortTool("排序1") { RegionPath = "分割.Region", AngleMode = GridAngleMode.Auto };
        var none = new RegionSortTool("排序2") { RegionPath = "分割.Region" };

        Assert.True(auto.Run(ctx).IsSuccess);
        Assert.True(none.Run(ctx).IsSuccess);

        AssertIndicesMatchTruth(ctx, "排序1", chips);
        Assert.InRange((double)ctx.GetVariable("排序1", "GridAngle").Value, angle - 0.005, angle + 0.005);
        // 3° 旋转在 2000 像素宽的晶圆上让同一行两端相差约 100 像素，超过行距，不估角度会合并或拆错行
        Assert.NotEqual(TruthByIndex(chips).Keys.Max(k => k.Item1) + 1, (int)ctx.GetVariable("排序2", "RowCount").Value);
    }

    [Fact]
    public void 列优先输出()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = SyntheticWafer.Create(0, 3, Array.Empty<(int, int)>(), out List<SyntheticWafer.Chip> chips, 500);
        using var imageOwner = image;
        using FlowContext ctx = Segment(image);
        var tool = new RegionSortTool("排序1") { RegionPath = "分割.Region", Order = RegionGridOrder.ColumnMajor };

        Assert.True(tool.Run(ctx).IsSuccess);

        AssertIndicesMatchTruth(ctx, "排序1", chips);
        var items = ctx.GetVariable("排序1", "Items").GetValue<RegionGridItem[]>();
        var expected = items.OrderBy(i => i.ColumnIndex).ThenBy(i => i.RowIndex).Select(i => i.Index);
        Assert.Equal(expected, items.Select(i => i.Index));
    }

    [Fact]
    public void 同一位置有两个区域_报告重复()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject sizeImage, "byte", 400, 400);
        sizeImage.Dispose();
        // 2×2 网格，左上格被分割成左右两半
        HOperatorSet.GenRectangle1(out HObject regions,
            new HTuple(100, 100, 100, 200, 200), new HTuple(80, 112, 200, 80, 200),
            new HTuple(130, 130, 130, 230, 230), new HTuple(108, 140, 260, 140, 260));
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("分割", "Region", HalconRegion.Owned(regions), 5));
        var tool = new RegionSortTool("排序1") { RegionPath = "分割.Region", ColumnTolerance = 20 };

        Assert.True(tool.Run(ctx).IsSuccess);

        Assert.Equal(1, (int)ctx.GetVariable("排序1", "DuplicateCount").Value);
        Assert.Equal(2, (int)ctx.GetVariable("排序1", "RowCount").Value);
        Assert.Equal(2, (int)ctx.GetVariable("排序1", "ColumnCount").Value);
        Assert.Contains(ctx.Log, l => l.Contains("同一行列位置"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 输入为空_按未找到策略处理(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenEmptyObj(out HObject empty);
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("分割", "Region", HalconRegion.Owned(empty), 0));
        var tool = new RegionSortTool("排序1") { RegionPath = "分割.Region", FailWhenNotFound = failWhenNotFound };

        NodeResult result = tool.Run(ctx);

        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.False((bool)ctx.GetVariable("排序1", "Found").Value);
        Assert.Empty(ctx.GetVariable("排序1", "RowIndices").GetValue<int[]>());
    }

    [Fact]
    public void 矩形测量数组与初始区域对象逐一对齐_失败项为NaN()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject sizeImage, "byte", 400, 400);
        sizeImage.Dispose();
        HOperatorSet.GenRectangle2(out HObject chip, 100, 100, 0.1, 40, 25);
        HOperatorSet.RegionToBin(chip, out HObject image, 220, 30, 400, 400);
        using var imageOwner = image;
        // 第 0 个对象落在空白处（无边缘，测量失败），第 1 个是 chip
        HOperatorSet.GenRectangle2(out HObject blank, 300, 300, 0, 40, 25);
        HOperatorSet.ConcatObj(blank, chip, out HObject seeds);
        blank.Dispose();
        chip.Dispose();
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        ctx.SetVariable(Variable.Object("排序", "Region", HalconRegion.Owned(seeds), 2));
        var tool = new RectangleFollowMeasureTool("测量1") { ImagePath = "Input.Image", InitRegionPath = "排序.Region" };

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, (int)ctx.GetVariable("测量1", "FailedCount").Value);
        var rows = ctx.GetVariable("测量1", "Rows").GetValue<double[]>();
        var columns = ctx.GetVariable("测量1", "Columns").GetValue<double[]>();
        var phis = ctx.GetVariable("测量1", "Phis").GetValue<double[]>();
        Assert.Equal(2, rows.Length);
        Assert.True(double.IsNaN(rows[0]) && double.IsNaN(columns[0]) && double.IsNaN(phis[0]));
        Assert.InRange(rows[1], 99.5, 100.5);
        Assert.InRange(columns[1], 99.5, 100.5);
        Assert.InRange(phis[1], 0.09, 0.11);
    }

    [Fact]
    public void 圆测量数组与初始区域对象逐一对齐()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject sizeImage, "byte", 400, 400);
        sizeImage.Dispose();
        HOperatorSet.GenCircle(out HObject discs, new HTuple(100, 250), new HTuple(100, 260), new HTuple(30, 45));
        HOperatorSet.RegionToBin(discs, out HObject image, 220, 30, 400, 400);
        using var imageOwner = image;
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        ctx.SetVariable(Variable.Object("排序", "Region", HalconRegion.Owned(discs), 2));
        var tool = new CircleFollowMeasureTool("圆1") { ImagePath = "Input.Image", InitRegionPath = "排序.Region" };

        Assert.True(tool.Run(ctx).IsSuccess);

        var radii = ctx.GetVariable("圆1", "Radii").GetValue<double[]>();
        Assert.Equal(2, radii.Length);
        Assert.InRange(radii[0], 29, 31);
        Assert.InRange(radii[1], 44, 46);
        Assert.InRange(ctx.GetVariable("圆1", "Columns").GetValue<double[]>()[1], 259, 261);
    }

    [Fact]
    public void 晶圆示例流程_500颗chip行列编号与亚像素测量()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        double angle = 1.5 * Math.PI / 180;
        HObject image = SyntheticWafer.Create(angle, 2024, new[] { (0, 0), (3, -2), (-4, 5) }, out List<SyntheticWafer.Chip> chips);
        using var imageOwner = image;
        SequenceNode root = FlowSerializer.Load(File.ReadAllText(
            RepoPaths.Find(Path.Combine("examples", "wafer-chips.vflow.json"))));
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        FlowRunResult run = new FlowEngine().Run(root, ctx);
        watch.Stop();

        Assert.True(run.IsSuccess, run.Message);
        Assert.InRange(chips.Count, 480, 520);
        Assert.Equal(chips.Count, (int)ctx.GetVariable("流程输出1", "ChipCount").Value);
        Assert.Equal(0, (int)ctx.GetVariable("流程输出1", "FailedCount").Value);
        int[] rowIndices = ToArray<int>(ctx.GetVariable("流程输出1", "RowIndices").Value);
        int[] columnIndices = ToArray<int>(ctx.GetVariable("流程输出1", "ColumnIndices").Value);
        double[] rows = ToArray<double>(ctx.GetVariable("流程输出1", "Rows").Value);
        double[] columns = ToArray<double>(ctx.GetVariable("流程输出1", "Columns").Value);
        double[] angles = ToArray<double>(ctx.GetVariable("流程输出1", "Angles").Value);
        double[] blobRows = ctx.GetVariable("行列排序", "Rows").GetValue<double[]>();
        double[] blobColumns = ctx.GetVariable("行列排序", "Columns").GetValue<double[]>();
        Assert.Equal(chips.Count, rows.Length);

        Dictionary<(int, int), SyntheticWafer.Chip> truth = TruthByIndex(chips);
        double measureMax = 0, measureSum = 0, blobMax = 0, blobSum = 0, angleMax = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.True(truth.TryGetValue((rowIndices[i], columnIndices[i]), out SyntheticWafer.Chip? chip),
                $"第 {i} 项行列号 [{rowIndices[i]},{columnIndices[i]}] 不在真值中");
            double measureError = Math.Sqrt(Math.Pow(rows[i] - chip.Row, 2) + Math.Pow(columns[i] - chip.Column, 2));
            double blobError = Math.Sqrt(Math.Pow(blobRows[i] - chip.Row, 2) + Math.Pow(blobColumns[i] - chip.Column, 2));
            // Angles 已由“角度换算”转为度并折算到 [-90°, 90°)
            Assert.InRange(angles[i], -90, 90 - 1e-9);
            double angleError = Math.Abs(angles[i] - chip.Phi * 180 / Math.PI) * Math.PI / 180;
            measureMax = Math.Max(measureMax, measureError);
            measureSum += measureError;
            blobMax = Math.Max(blobMax, blobError);
            blobSum += blobError;
            angleMax = Math.Max(angleMax, angleError);
        }
        _output.WriteLine($"chip={rows.Length}，{ctx.GetVariable("流程输出1", "RowCount").Value} 行 × {ctx.GetVariable("流程输出1", "ColumnCount").Value} 列，流程耗时 {watch.ElapsedMilliseconds} ms");
        _output.WriteLine($"灰度分割中心误差：平均 {blobSum / rows.Length:F3} px，最大 {blobMax:F3} px");
        _output.WriteLine($"矩形测量中心误差：平均 {measureSum / rows.Length:F3} px，最大 {measureMax:F3} px；角度最大误差 {angleMax * 180 / Math.PI:F3}°");
        Assert.True(measureMax < 0.25, $"矩形测量最大中心误差 {measureMax:F3} px");
        Assert.True(angleMax < 0.4 * Math.PI / 180, $"矩形测量最大角度误差 {angleMax * 180 / Math.PI:F3}°");
        Assert.True(measureSum * 3 < blobSum, "矩形测量中心应明显比灰度分割重心更准确");
    }

    private static T[] ToArray<T>(object value)
    {
        return ((System.Collections.IEnumerable)value).Cast<object>().Select(v => (T)v).ToArray();
    }
}
