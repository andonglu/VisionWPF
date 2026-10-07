using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    public enum ContourDistanceMode
    {
        /// <summary>点到轮廓（distance_pc）。</summary>
        PointToContour,
        /// <summary>轮廓到轮廓的最近距离与最近点对（distance_cc_min_points）。</summary>
        ContourToContour
    }

    /// <summary>distance_cc_min_points 的距离计算方式（成员名即 HALCON 参数值，见计划第 10 节实测）。</summary>
    public enum ContourDistanceCcMode
    {
        fast_point_to_segment,
        point_to_segment
    }

    /// <summary>
    /// 轮廓距离（XG-08）。两组对象按 <see cref="PairingHelper"/> 配对（与区域距离 RG-07 相同：等长逐一、一侧为 1 则一对多、否则报错），
    /// 每对单独调用算子（distance_cc_min_points 要求两侧个数相等，distance_pc 对多轮廓只返回一个值）。
    /// 数组输出与配对逐一对应，单值输出取第一对；轮廓到轮廓另输出最近点对连线 Segments（XLD），便于叠加显示。
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
    public sealed class ContourDistanceTool : ToolBase, INotFoundPolicy, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("轮廓", typeof(HalconXld))]
        public string XldPath { get; set; }

        /// <summary>轮廓到轮廓时的第二组轮廓。</summary>
        [InputRef("轮廓 2", typeof(HalconXld), Optional = true)]
        public string XldPath2 { get; set; }

        /// <summary>点到轮廓时点的行坐标（单值或数组，个数与列一致）。</summary>
        [InputRef("点行坐标", typeof(double), Optional = true, AcceptsCollection = true)]
        public string RowPath { get; set; }

        /// <summary>点到轮廓时点的列坐标（单值或数组，个数与行一致）。</summary>
        [InputRef("点列坐标", typeof(double), Optional = true, AcceptsCollection = true)]
        public string ColumnPath { get; set; }

        public ContourDistanceMode Mode { get; set; } = ContourDistanceMode.ContourToContour;

        /// <summary>轮廓到轮廓的距离计算方式。</summary>
        public ContourDistanceCcMode CcMode { get; set; } = ContourDistanceCcMode.fast_point_to_segment;

        /// <summary>没有可计算的配对（输入为空或配对中含空轮廓）时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public ContourDistanceTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (Mode == ContourDistanceMode.ContourToContour && string.IsNullOrWhiteSpace(XldPath2))
            {
                yield return new ToolConfigurationIssue("轮廓 2", "轮廓到轮廓需要指定轮廓 2");
            }
            if (Mode == ContourDistanceMode.PointToContour && (string.IsNullOrWhiteSpace(RowPath) || string.IsNullOrWhiteSpace(ColumnPath)))
            {
                yield return new ToolConfigurationIssue("点行坐标 / 点列坐标", "点到轮廓需要指定点的行、列坐标");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(XldPath2):
                case nameof(CcMode):
                    return Mode == ContourDistanceMode.ContourToContour;
                case nameof(RowPath):
                case nameof(ColumnPath):
                    return Mode == ContourDistanceMode.PointToContour;
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

            HObject contours = Input<HalconXld>(ctx, XldPath).Object;
            int contourCount = XldLineHelper.Count(contours);

            int otherCount;
            HObject contours2 = null;
            List<double> rows = null;
            List<double> columns = null;
            if (Mode == ContourDistanceMode.ContourToContour)
            {
                contours2 = Input<HalconXld>(ctx, XldPath2).Object;
                otherCount = XldLineHelper.Count(contours2);
            }
            else
            {
                rows = RegionDistanceTool.ReadNumbers(ctx, RowPath, "点行坐标");
                columns = RegionDistanceTool.ReadNumbers(ctx, ColumnPath, "点列坐标");
                if (rows.Count != columns.Count)
                {
                    return NodeResult.Fail($"{ModuleName} 点的行、列个数不一致（行 {rows.Count} 个，列 {columns.Count} 个）");
                }
                otherCount = rows.Count;
            }

            if (!PairingHelper.TryGetPairCount(contourCount, otherCount, out int pairCount, out string pairError))
            {
                return NodeResult.Fail($"{ModuleName} {pairError}");
            }

            var results = new Results();
            if (pairCount == 0)
            {
                WriteOutputs(ctx, results, null);
                return NotFoundOutcome.Resolve(ctx, this, $"轮廓距离没有可计算的配对（轮廓 {contourCount} 个，另一侧 {otherCount} 个）");
            }

            // HALCON 不会产生 0 点轮廓（见计划第 10 节），此处仍防御性检查，避免算子报晦涩的错误
            List<int> points1 = PointCounts(contours, contourCount);
            List<int> points2 = contours2 == null ? null : PointCounts(contours2, otherCount);
            for (int i = 0; i < pairCount; i++)
            {
                int a = PairingHelper.SideIndex(i, contourCount);
                int b = PairingHelper.SideIndex(i, otherCount);
                bool empty1 = points1[a] == 0;
                bool empty2 = points2 != null && points2[b] == 0;
                if (empty1 || empty2)
                {
                    WriteOutputs(ctx, results, null);
                    return NotFoundOutcome.Resolve(ctx, this, $"轮廓距离第 {i} 对含空轮廓（{(empty1 ? $"轮廓第 {a} 条" : $"轮廓 2 第 {b} 条")}）");
                }
            }

            for (int i = 0; i < pairCount; i++)
            {
                HOperatorSet.SelectObj(contours, out HObject contour1, PairingHelper.SideIndex(i, contourCount) + 1);
                try
                {
                    if (Mode == ContourDistanceMode.ContourToContour)
                    {
                        HOperatorSet.SelectObj(contours2, out HObject contour2, PairingHelper.SideIndex(i, otherCount) + 1);
                        try
                        {
                            HOperatorSet.DistanceCcMinPoints(contour1, contour2, CcMode.ToString(), out HTuple distance,
                                out HTuple row1, out HTuple column1, out HTuple row2, out HTuple column2);
                            results.Distances.Add(distance.D);
                            results.Rows1.Add(row1.D);
                            results.Columns1.Add(column1.D);
                            results.Rows2.Add(row2.D);
                            results.Columns2.Add(column2.D);
                        }
                        finally
                        {
                            contour2.Dispose();
                        }
                    }
                    else
                    {
                        int p = PairingHelper.SideIndex(i, otherCount);
                        HOperatorSet.DistancePc(contour1, rows[p], columns[p], out HTuple min, out HTuple max);
                        results.Distances.Add(min.D);
                        results.MaxDistances.Add(max.D);
                    }
                }
                finally
                {
                    contour1.Dispose();
                }
            }

            HObject segments = Mode == ContourDistanceMode.ContourToContour
                ? XldLineHelper.Segments(results.Rows1, results.Columns1, results.Rows2, results.Columns2)
                : null;
            WriteOutputs(ctx, results, segments);
            ctx.AddLog(FlowLogLevel.Info, Mode == ContourDistanceMode.ContourToContour
                ? $"[轮廓距离] 轮廓到轮廓（{CcMode}）{contourCount} 对 {otherCount}，{pairCount} 对，第一对距离 {results.Distances[0]:F3}"
                : $"[轮廓距离] 点到轮廓 {contourCount} 对 {otherCount}，{pairCount} 对，第一对距离 {results.Distances[0]:F3}");
            return NodeResult.Ok;
        }

        private static List<int> PointCounts(HObject contours, int count)
        {
            if (count == 0)
            {
                return new List<int>();
            }
            HOperatorSet.ContourPointNumXld(contours, out HTuple points);
            return Enumerable.Range(0, count).Select(i => i < points.Length ? points[i].I : 0).ToList();
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
    }
}
