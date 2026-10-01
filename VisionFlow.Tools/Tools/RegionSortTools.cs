using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>区域排序的输出顺序。</summary>
    public enum RegionGridOrder
    {
        /// <summary>行优先：先按行号，再按列号（从上到下、从左到右）。</summary>
        RowMajor,
        /// <summary>列优先：先按列号，再按行号（从左到右、从上到下）。</summary>
        ColumnMajor
    }

    /// <summary>网格方向角的确定方式。</summary>
    public enum GridAngleMode
    {
        /// <summary>网格与图像轴平行（角度 0）。</summary>
        None,
        /// <summary>取各区域 smallest_rectangle2 方向角（折算到 [-45°, 45°)）的中位数。</summary>
        Auto,
        /// <summary>使用 GridAngle 指定的角度（弧度）。</summary>
        Fixed
    }

    /// <summary>排序后的单个区域：网格行列号与区域中心。</summary>
    public sealed class RegionGridItem
    {
        /// <summary>排序后的序号（0 起，与输出 Region 中的对象顺序一致）。</summary>
        public int Index { get; set; }
        /// <summary>网格行号（0 起，从上到下）。</summary>
        public int RowIndex { get; set; }
        /// <summary>网格列号（0 起，从左到右）。</summary>
        public int ColumnIndex { get; set; }
        /// <summary>区域中心（area_center）。</summary>
        public double Row { get; set; }
        public double Column { get; set; }
        public double Area { get; set; }

        public override string ToString()
        {
            return $"#{Index} [{RowIndex},{ColumnIndex}] 中心=({Row:F2},{Column:F2}), 面积={Area:F0}";
        }
    }

    /// <summary>
    /// 区域排序（行列编号）：把阵列排布的区域（如晶圆上的 chip、料盘穴位）按网格分配行号、列号并排序。
    /// 先把区域中心旋转到网格坐标系，再分别按行方向、列方向做一维聚类：相邻中心之差不超过容差视为同一行（列）。
    /// 行列号是全局的：边缘缺料（如圆形晶圆边缘）或漏检不会让后面的列号前移。
    /// 输出 Region 的对象顺序即排序结果，下游“矩形测量 / 圆形测量”用它做初始区域时，
    /// 测量数组（Rows / Columns / Phis 等）与本工具的 RowIndices / ColumnIndices 逐一对齐。
    /// 与 HALCON sort_region 相比：sort_region 只按单一坐标排序，同一行内的行坐标抖动会打乱列顺序，
    /// 也不给出行列号。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("RowIndices", VariableKind.Array, VariableType.Int)]
    [ToolOutput("ColumnIndices", VariableKind.Array, VariableType.Int)]
    [ToolOutput("Rows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Columns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(RegionGridItem),
        Members = new[] { "Index", "RowIndex", "ColumnIndex", "Row", "Column", "Area" })]
    [ToolOutput("RowCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ColumnCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("DuplicateCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("GridAngle", VariableKind.Single, VariableType.Double)]
    public sealed class RegionSortTool : ToolBase, INotFoundPolicy
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>输出顺序。</summary>
        public RegionGridOrder Order { get; set; } = RegionGridOrder.RowMajor;
        /// <summary>网格方向角的确定方式。</summary>
        public GridAngleMode AngleMode { get; set; } = GridAngleMode.None;
        /// <summary>固定网格方向角（弧度，仅 AngleMode = Fixed 时使用）。</summary>
        public double GridAngle { get; set; }
        /// <summary>同一行的中心最大间隔（像素，沿行号方向）；0 = 自动（区域高度中位数的一半）。</summary>
        public double RowTolerance { get; set; }
        /// <summary>同一列的中心最大间隔（像素，沿列号方向）；0 = 自动（区域宽度中位数的一半）。</summary>
        public double ColumnTolerance { get; set; }
        /// <summary>输入区域为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public RegionSortTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (RowTolerance < 0 || ColumnTolerance < 0)
            {
                return NodeResult.Fail("区域排序的行/列容差不能为负数（0 表示自动）");
            }

            HObject input = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.CountObj(input, out HTuple countTuple);
            int count = countTuple.I;
            var centers = new List<RegionCenter>();
            if (count > 0)
            {
                HOperatorSet.AreaCenter(input, out HTuple areas, out HTuple rows, out HTuple columns);
                HOperatorSet.SmallestRectangle2(input, out _, out _, out HTuple phis, out HTuple length1, out HTuple length2);
                for (int i = 0; i < count; i++)
                {
                    if (areas[i].D <= 0)
                    {
                        continue;
                    }
                    centers.Add(new RegionCenter
                    {
                        ObjectIndex = i,
                        Row = rows[i].D,
                        Column = columns[i].D,
                        Area = areas[i].D,
                        Phi = phis[i].D,
                        Length1 = length1[i].D,
                        Length2 = length2[i].D
                    });
                }
                if (centers.Count < count)
                {
                    ctx.AddLog(FlowLogLevel.Warning, $"[区域排序] 忽略 {count - centers.Count} 个空区域对象");
                }
            }

            if (centers.Count == 0)
            {
                HOperatorSet.GenEmptyObj(out HObject empty);
                WriteOutputs(ctx, empty, new List<RegionGridItem>(), 0, 0, 0, 0);
                return NotFoundOutcome.Resolve(ctx, this, "区域排序的输入区域为空");
            }

            double angle = ResolveAngle(centers);
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            var halfHeights = new List<double>();
            var halfWidths = new List<double>();
            foreach (RegionCenter center in centers)
            {
                // 旋转到网格坐标系：列方向单位向量 (-sin, cos)，行方向单位向量 (cos, sin)
                center.GridRow = center.Row * cos + center.Column * sin;
                center.GridColumn = -center.Row * sin + center.Column * cos;
                double delta = center.Phi - angle;
                halfWidths.Add(center.Length1 * Math.Abs(Math.Cos(delta)) + center.Length2 * Math.Abs(Math.Sin(delta)));
                halfHeights.Add(center.Length1 * Math.Abs(Math.Sin(delta)) + center.Length2 * Math.Abs(Math.Cos(delta)));
            }

            double rowTolerance = RowTolerance > 0 ? RowTolerance : Math.Max(Median(halfHeights), 1);
            double columnTolerance = ColumnTolerance > 0 ? ColumnTolerance : Math.Max(Median(halfWidths), 1);
            int rowCount = AssignClusters(centers, c => c.GridRow, rowTolerance, (c, index) => c.RowIndex = index);
            int columnCount = AssignClusters(centers, c => c.GridColumn, columnTolerance, (c, index) => c.ColumnIndex = index);

            List<RegionCenter> sorted = Order == RegionGridOrder.ColumnMajor
                ? centers.OrderBy(c => c.ColumnIndex).ThenBy(c => c.RowIndex).ThenBy(c => c.GridRow).ToList()
                : centers.OrderBy(c => c.RowIndex).ThenBy(c => c.ColumnIndex).ThenBy(c => c.GridColumn).ToList();
            int duplicateCount = sorted.GroupBy(c => new { c.RowIndex, c.ColumnIndex }).Sum(g => g.Count() - 1);

            HOperatorSet.SelectObj(input, out HObject output,
                new HTuple(sorted.Select(c => c.ObjectIndex + 1).ToArray()));
            var items = sorted.Select((c, i) => new RegionGridItem
            {
                Index = i,
                RowIndex = c.RowIndex,
                ColumnIndex = c.ColumnIndex,
                Row = c.Row,
                Column = c.Column,
                Area = c.Area
            }).ToList();
            WriteOutputs(ctx, output, items, rowCount, columnCount, duplicateCount, angle);

            ctx.AddLog(FlowLogLevel.Info,
                $"[区域排序] {items.Count} 个区域，{rowCount} 行 × {columnCount} 列，网格角度 {angle * 180 / Math.PI:F3}°，"
                + $"行容差 {rowTolerance:F1}，列容差 {columnTolerance:F1}");
            if (duplicateCount > 0)
            {
                ctx.AddLog(FlowLogLevel.Warning,
                    $"[区域排序] 有 {duplicateCount} 个区域与其他区域落在同一行列位置，请检查分割结果或减小容差");
            }
            return NodeResult.Ok;
        }

        private void WriteOutputs(FlowContext ctx, HObject region, List<RegionGridItem> items,
            int rowCount, int columnCount, int duplicateCount, double angle)
        {
            Variable regionVariable = Variable.Object(ModuleName, "Region", new HalconRegion(region), items.Count);
            HalconOwnership.Adopt(regionVariable);
            ctx.SetVariable(regionVariable);
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, items.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, items.Count > 0));
            SetOutput(ctx, Variable.Array(ModuleName, "RowIndices", VariableType.Int, items.Select(i => i.RowIndex)));
            SetOutput(ctx, Variable.Array(ModuleName, "ColumnIndices", VariableType.Int, items.Select(i => i.ColumnIndex)));
            SetOutput(ctx, Variable.Array(ModuleName, "Rows", VariableType.Double, items.Select(i => i.Row)));
            SetOutput(ctx, Variable.Array(ModuleName, "Columns", VariableType.Double, items.Select(i => i.Column)));
            SetOutput(ctx, Variable.Array(ModuleName, "Items", VariableType.Object, items));
            SetOutput(ctx, Variable.Single(ModuleName, "RowCount", VariableType.Int, rowCount));
            SetOutput(ctx, Variable.Single(ModuleName, "ColumnCount", VariableType.Int, columnCount));
            SetOutput(ctx, Variable.Single(ModuleName, "DuplicateCount", VariableType.Int, duplicateCount));
            SetOutput(ctx, Variable.Single(ModuleName, "GridAngle", VariableType.Double, angle));
        }

        private double ResolveAngle(List<RegionCenter> centers)
        {
            switch (AngleMode)
            {
                case GridAngleMode.Fixed:
                    return GridAngle;
                case GridAngleMode.Auto:
                    return Median(centers.Select(c => FoldQuarter(c.Phi)).ToList());
                default:
                    return 0;
            }
        }

        /// <summary>把方向角折算到 [-π/4, π/4)：矩形的长边、短边方向对网格而言等价。</summary>
        internal static double FoldQuarter(double phi)
        {
            double quarter = Math.PI / 2;
            double folded = phi - Math.Floor((phi + quarter / 2) / quarter) * quarter;
            return folded;
        }

        /// <summary>一维聚类：按值排序后，相邻值之差大于容差处断开；返回簇数，簇号按值从小到大。</summary>
        private static int AssignClusters(List<RegionCenter> centers, Func<RegionCenter, double> key, double tolerance,
            Action<RegionCenter, int> assign)
        {
            int cluster = 0;
            double previous = double.NaN;
            foreach (RegionCenter center in centers.OrderBy(key))
            {
                double value = key(center);
                if (!double.IsNaN(previous) && value - previous > tolerance)
                {
                    cluster++;
                }
                assign(center, cluster);
                previous = value;
            }
            return cluster + 1;
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }
            List<double> ordered = values.OrderBy(v => v).ToList();
            int middle = ordered.Count / 2;
            return ordered.Count % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
        }

        private sealed class RegionCenter
        {
            public int ObjectIndex;
            public double Row;
            public double Column;
            public double Area;
            public double Phi;
            public double Length1;
            public double Length2;
            public double GridRow;
            public double GridColumn;
            public int RowIndex;
            public int ColumnIndex;
        }
    }
}
