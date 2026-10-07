using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    public enum RegionDistanceMode
    {
        /// <summary>点到区域（distance_pr）。</summary>
        PointToRegion,
        /// <summary>区域到区域的最近距离（distance_rr_min）。</summary>
        RegionToRegion
    }

    /// <summary>
    /// 区域距离（RG-07）。两组对象按 <see cref="PairingHelper"/> 配对：个数相等时逐一配对，一侧只有 1 个时一对多，否则报错；
    /// 不做全组合，也不先合并区域（需要整体距离时先用“区域处理 / Union1”）。
    /// 数组输出与配对逐一对应，单值输出取第一对。区域到区域另输出最近点对连线 Segments（XLD），便于叠加显示。
    /// </summary>
    [ToolOutput("Distances", VariableKind.Array, VariableType.Double)]
    [ToolOutput("MaxDistances", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Rows1", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Columns1", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Rows2", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Columns2", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Distance", VariableKind.Single, VariableType.Double)]
    [ToolOutput("MaxDistance", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Row1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Row2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Segments", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionDistanceTool : ToolBase, INotFoundPolicy, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>区域到区域时的第二组区域。</summary>
        [InputRef("区域 2", typeof(HalconRegion), Optional = true)]
        public string RegionPath2 { get; set; }

        /// <summary>点到区域时点的行坐标（单值或数组，个数与列一致）。</summary>
        [InputRef("点行坐标", typeof(double), Optional = true, AcceptsCollection = true)]
        public string RowPath { get; set; }

        /// <summary>点到区域时点的列坐标（单值或数组，个数与行一致）。</summary>
        [InputRef("点列坐标", typeof(double), Optional = true, AcceptsCollection = true)]
        public string ColumnPath { get; set; }

        public RegionDistanceMode Mode { get; set; } = RegionDistanceMode.RegionToRegion;

        /// <summary>没有可计算的配对（输入为空或配对中含空区域）时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public RegionDistanceTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (Mode == RegionDistanceMode.RegionToRegion && string.IsNullOrWhiteSpace(RegionPath2))
            {
                yield return new ToolConfigurationIssue("区域 2", "区域到区域需要指定区域 2");
            }
            if (Mode == RegionDistanceMode.PointToRegion && (string.IsNullOrWhiteSpace(RowPath) || string.IsNullOrWhiteSpace(ColumnPath)))
            {
                yield return new ToolConfigurationIssue("点行坐标 / 点列坐标", "点到区域需要指定点的行、列坐标");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(RegionPath2):
                    return Mode == RegionDistanceMode.RegionToRegion;
                case nameof(RowPath):
                case nameof(ColumnPath):
                    return Mode == RegionDistanceMode.PointToRegion;
                default:
                    return true;
            }
        }

        private sealed class Results
        {
            public readonly List<double> Distances = new List<double>();
            public readonly List<double> MaxDistances = new List<double>();
            public readonly List<double> Rows1 = new List<double>();
            public readonly List<double> Columns1 = new List<double>();
            public readonly List<double> Rows2 = new List<double>();
            public readonly List<double> Columns2 = new List<double>();
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject regions = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.AreaCenter(regions, out HTuple areas, out _, out _);
            int regionCount = areas.Length;

            int otherCount;
            HObject regions2 = null;
            HTuple areas2 = null;
            List<double> rows = null;
            List<double> columns = null;
            if (Mode == RegionDistanceMode.RegionToRegion)
            {
                regions2 = Input<HalconRegion>(ctx, RegionPath2).Object;
                HOperatorSet.AreaCenter(regions2, out areas2, out _, out _);
                otherCount = areas2.Length;
            }
            else
            {
                rows = ReadNumbers(ctx, RowPath, "点行坐标");
                columns = ReadNumbers(ctx, ColumnPath, "点列坐标");
                if (rows.Count != columns.Count)
                {
                    return NodeResult.Fail($"{ModuleName} 点的行、列个数不一致（行 {rows.Count} 个，列 {columns.Count} 个）");
                }
                otherCount = rows.Count;
            }

            if (!PairingHelper.TryGetPairCount(regionCount, otherCount, out int pairCount, out string pairError))
            {
                return NodeResult.Fail($"{ModuleName} {pairError}");
            }

            var results = new Results();
            if (pairCount == 0)
            {
                WriteOutputs(ctx, results, null);
                return NotFoundOutcome.Resolve(ctx, this, $"区域距离没有可计算的配对（区域 {regionCount} 个，另一侧 {otherCount} 个）");
            }

            // distance_rr_min / distance_pr 不接受空区域，先逐对检查
            for (int i = 0; i < pairCount; i++)
            {
                int a = PairingHelper.SideIndex(i, regionCount);
                int b = PairingHelper.SideIndex(i, otherCount);
                bool empty1 = areas[a].D <= 0;
                bool empty2 = Mode == RegionDistanceMode.RegionToRegion && areas2[b].D <= 0;
                if (empty1 || empty2)
                {
                    WriteOutputs(ctx, results, null);
                    string side = empty1 ? $"区域第 {a} 个" : $"区域 2 第 {b} 个";
                    return NotFoundOutcome.Resolve(ctx, this, $"区域距离第 {i} 对含空区域（{side}）");
                }
            }

            HObject segments = null;
            try
            {
                if (Mode == RegionDistanceMode.RegionToRegion)
                {
                    HOperatorSet.GenEmptyObj(out segments);
                    for (int i = 0; i < pairCount; i++)
                    {
                        HOperatorSet.SelectObj(regions, out HObject region1, PairingHelper.SideIndex(i, regionCount) + 1);
                        HOperatorSet.SelectObj(regions2, out HObject region2, PairingHelper.SideIndex(i, otherCount) + 1);
                        try
                        {
                            HOperatorSet.DistanceRrMin(region1, region2, out HTuple distance,
                                out HTuple row1, out HTuple column1, out HTuple row2, out HTuple column2);
                            results.Distances.Add(distance.D);
                            results.Rows1.Add(row1.D);
                            results.Columns1.Add(column1.D);
                            results.Rows2.Add(row2.D);
                            results.Columns2.Add(column2.D);
                            HOperatorSet.GenContourPolygonXld(out HObject segment, new HTuple(row1.D, row2.D), new HTuple(column1.D, column2.D));
                            HOperatorSet.ConcatObj(segments, segment, out HObject joined);
                            segment.Dispose();
                            segments.Dispose();
                            segments = joined;
                        }
                        finally
                        {
                            region1.Dispose();
                            region2.Dispose();
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < pairCount; i++)
                    {
                        HOperatorSet.SelectObj(regions, out HObject region, PairingHelper.SideIndex(i, regionCount) + 1);
                        try
                        {
                            int p = PairingHelper.SideIndex(i, otherCount);
                            HOperatorSet.DistancePr(region, rows[p], columns[p], out HTuple min, out HTuple max);
                            results.Distances.Add(min.D);
                            results.MaxDistances.Add(max.D);
                        }
                        finally
                        {
                            region.Dispose();
                        }
                    }
                }
            }
            catch
            {
                segments?.Dispose();
                throw;
            }

            WriteOutputs(ctx, results, segments);
            ctx.AddLog(FlowLogLevel.Info, Mode == RegionDistanceMode.RegionToRegion
                ? $"[区域距离] 区域到区域 {regionCount} 对 {otherCount}，{pairCount} 对，第一对距离 {results.Distances[0]:F3}"
                : $"[区域距离] 点到区域 {regionCount} 对 {otherCount}，{pairCount} 对，第一对距离 {results.Distances[0]:F3}");
            return NodeResult.Ok;
        }

        /// <summary>所有输出总是写出；当前模式不产生的数组为空、单值为 NaN。</summary>
        private void WriteOutputs(FlowContext ctx, Results results, HObject segments)
        {
            if (segments == null)
            {
                HOperatorSet.GenEmptyObj(out segments);
            }
            HOperatorSet.CountObj(segments, out HTuple segmentCount);
            SetOutput(ctx, Variable.Array(ModuleName, "Distances", VariableType.Double, results.Distances));
            SetOutput(ctx, Variable.Array(ModuleName, "MaxDistances", VariableType.Double, results.MaxDistances));
            SetOutput(ctx, Variable.Array(ModuleName, "Rows1", VariableType.Double, results.Rows1));
            SetOutput(ctx, Variable.Array(ModuleName, "Columns1", VariableType.Double, results.Columns1));
            SetOutput(ctx, Variable.Array(ModuleName, "Rows2", VariableType.Double, results.Rows2));
            SetOutput(ctx, Variable.Array(ModuleName, "Columns2", VariableType.Double, results.Columns2));
            SetOutput(ctx, Variable.Single(ModuleName, "Distance", VariableType.Double, First(results.Distances)));
            SetOutput(ctx, Variable.Single(ModuleName, "MaxDistance", VariableType.Double, First(results.MaxDistances)));
            SetOutput(ctx, Variable.Single(ModuleName, "Row1", VariableType.Double, First(results.Rows1)));
            SetOutput(ctx, Variable.Single(ModuleName, "Column1", VariableType.Double, First(results.Columns1)));
            SetOutput(ctx, Variable.Single(ModuleName, "Row2", VariableType.Double, First(results.Rows2)));
            SetOutput(ctx, Variable.Single(ModuleName, "Column2", VariableType.Double, First(results.Columns2)));
            SetOutput(ctx, Variable.Object(ModuleName, "Segments", new HalconXld(segments), segmentCount.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, results.Distances.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, results.Distances.Count > 0));
        }

        private static double First(List<double> values)
        {
            return values.Count > 0 ? values[0] : double.NaN;
        }

        /// <summary>读取单值或数组引用为数值列表。</summary>
        private static List<double> ReadNumbers(FlowContext ctx, string path, string what)
        {
            object raw = VariableReference.Parse(path).Resolve(ctx);
            IEnumerable<object> items = raw is IEnumerable enumerable && !(raw is string)
                ? enumerable.Cast<object>()
                : new[] { raw };
            var values = new List<double>();
            foreach (object item in items)
            {
                try
                {
                    values.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture));
                }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                {
                    throw new InvalidOperationException($"{what}第 {values.Count} 个值不是数值", ex);
                }
            }
            return values;
        }
    }
}
