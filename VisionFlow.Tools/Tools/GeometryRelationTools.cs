using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    public enum GeometryRelationMode
    {
        /// <summary>点到直线距离（distance_pl）与垂足（projection_pl）。</summary>
        PointToLine,
        /// <summary>两直线夹角（angle_ll，从直线 1 转到直线 2 的有向角）。</summary>
        LineToLineAngle,
        /// <summary>线段到线段的最小 / 最大距离（distance_ss）。</summary>
        SegmentToSegment,
        /// <summary>线段（输入 1）到直线（输入 2）的最小 / 最大距离（distance_sl）。</summary>
        SegmentToLine
    }

    /// <summary>
    /// 几何关系测量（XG-09）：输入为拟合直线等工具输出的直线 XLD（按 <see cref="XldLineHelper"/> 解释）与点坐标。
    /// 多对象时按 <see cref="PairingHelper"/> 配对，数组与配对逐一对应，单值取第一对。
    /// 夹角输出 Angle（弧度）与 AngleDeg（度），可按 Range 折算，规则与“角度换算”一致（<see cref="AngleMath"/>）。
    /// 点到直线时 Xld 为垂线段与垂足十字，便于叠加显示。
    /// </summary>
    [ToolOutput("Distances", VariableKind.Array, VariableType.Double)]
    [ToolOutput("MaxDistances", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Angles", VariableKind.Array, VariableType.Double)]
    [ToolOutput("AnglesDeg", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FootRows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FootColumns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Distance", VariableKind.Single, VariableType.Double)]
    [ToolOutput("MaxDistance", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Angle", VariableKind.Single, VariableType.Double)]
    [ToolOutput("AngleDeg", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FootRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FootColumn", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class GeometryRelationTool : ToolBase, INotFoundPolicy, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("直线/线段 1", typeof(HalconXld))]
        public string Line1Path { get; set; }

        /// <summary>夹角、线段距离时的第二条直线 / 线段；点到直线时不读取。</summary>
        [InputRef("直线/线段 2", typeof(HalconXld), Optional = true)]
        public string Line2Path { get; set; }

        /// <summary>点到直线时点的行坐标（单值或数组，个数与列一致）。</summary>
        [InputRef("点行坐标", typeof(double), Optional = true, AcceptsCollection = true)]
        public string RowPath { get; set; }

        /// <summary>点到直线时点的列坐标（单值或数组，个数与行一致）。</summary>
        [InputRef("点列坐标", typeof(double), Optional = true, AcceptsCollection = true)]
        public string ColumnPath { get; set; }

        public GeometryRelationMode Mode { get; set; } = GeometryRelationMode.PointToLine;

        /// <summary>夹角的折算范围（与“角度换算”相同）；None 时保持 angle_ll 的 (-180°, 180°]。</summary>
        public AngleRange Range { get; set; } = AngleRange.None;
        /// <summary>自定义范围下限（度，含）。</summary>
        public double RangeMin { get; set; } = -90;
        /// <summary>自定义范围上限（度，不含）；上下限之差即周期，须在 (0, 360] 内。</summary>
        public double RangeMax { get; set; } = 90;

        /// <summary>没有可计算的配对（输入为空）时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public GeometryRelationTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (Mode == GeometryRelationMode.PointToLine)
            {
                if (string.IsNullOrWhiteSpace(RowPath) || string.IsNullOrWhiteSpace(ColumnPath))
                {
                    yield return new ToolConfigurationIssue("点行坐标 / 点列坐标", "点到直线需要指定点的行、列坐标");
                }
            }
            else if (string.IsNullOrWhiteSpace(Line2Path))
            {
                yield return new ToolConfigurationIssue("直线/线段 2", "该模式需要指定第二条直线或线段");
            }
            if (Mode == GeometryRelationMode.LineToLineAngle && Range == AngleRange.Custom && !(RangeMax > RangeMin && RangeMax - RangeMin <= 360))
            {
                yield return new ToolConfigurationIssue("RangeMin / RangeMax", "自定义角度范围无效：上限须大于下限，且宽度不超过 360°");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(Line2Path):
                    return Mode != GeometryRelationMode.PointToLine;
                case nameof(RowPath):
                case nameof(ColumnPath):
                    return Mode == GeometryRelationMode.PointToLine;
                case nameof(Range):
                    return Mode == GeometryRelationMode.LineToLineAngle;
                case nameof(RangeMin):
                case nameof(RangeMax):
                    return Mode == GeometryRelationMode.LineToLineAngle && Range == AngleRange.Custom;
                default:
                    return true;
            }
        }

        private sealed class Results
        {
            public readonly List<double> Distances = new List<double>();
            public readonly List<double> MaxDistances = new List<double>();
            public readonly List<double> Angles = new List<double>();
            public readonly List<double> AnglesDeg = new List<double>();
            public readonly List<double> FootRows = new List<double>();
            public readonly List<double> FootColumns = new List<double>();
            public readonly List<double> PointRows = new List<double>();
            public readonly List<double> PointColumns = new List<double>();
        }

        /// <summary>按 Range 折算夹角（度）；None 时原样返回。</summary>
        public double FoldDegrees(double degrees)
        {
            return AngleMath.TryGetBounds(Range, RangeMin, RangeMax, out double min, out double max)
                ? AngleMath.Fold(degrees, min, max)
                : degrees;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject lines1 = Input<HalconXld>(ctx, Line1Path).Object;
            int count1 = XldLineHelper.Count(lines1);
            HObject lines2 = null;
            List<double> rows = null;
            List<double> columns = null;
            int count2;
            if (Mode == GeometryRelationMode.PointToLine)
            {
                rows = RegionDistanceTool.ReadNumbers(ctx, RowPath, "点行坐标");
                columns = RegionDistanceTool.ReadNumbers(ctx, ColumnPath, "点列坐标");
                if (rows.Count != columns.Count)
                {
                    return NodeResult.Fail($"{ModuleName} 点的行、列个数不一致（行 {rows.Count} 个，列 {columns.Count} 个）");
                }
                count2 = rows.Count;
            }
            else
            {
                lines2 = Input<HalconXld>(ctx, Line2Path).Object;
                count2 = XldLineHelper.Count(lines2);
            }

            if (!PairingHelper.TryGetPairCount(count1, count2, out int pairCount, out string pairError))
            {
                return NodeResult.Fail($"{ModuleName} {pairError}");
            }

            var results = new Results();
            if (pairCount == 0)
            {
                WriteOutputs(ctx, results);
                return NotFoundOutcome.Resolve(ctx, this, $"几何关系测量没有可计算的配对（输入 1 有 {count1} 个，输入 2 有 {count2} 个）");
            }

            for (int i = 0; i < pairCount; i++)
            {
                int a = PairingHelper.SideIndex(i, count1);
                int b = PairingHelper.SideIndex(i, count2);
                if (!XldLineHelper.TryGetLine(lines1, a, out double r1, out double c1, out double r2, out double c2, out string error1))
                {
                    return NodeResult.Fail($"{ModuleName} 直线/线段 1 第 {a} 条{error1}");
                }
                if (Mode == GeometryRelationMode.PointToLine)
                {
                    double row = rows[b];
                    double column = columns[b];
                    HOperatorSet.DistancePl(row, column, r1, c1, r2, c2, out HTuple distance);
                    HOperatorSet.ProjectionPl(row, column, r1, c1, r2, c2, out HTuple footRow, out HTuple footColumn);
                    results.Distances.Add(distance.D);
                    results.FootRows.Add(footRow.D);
                    results.FootColumns.Add(footColumn.D);
                    results.PointRows.Add(row);
                    results.PointColumns.Add(column);
                    continue;
                }

                if (!XldLineHelper.TryGetLine(lines2, b, out double r3, out double c3, out double r4, out double c4, out string error2))
                {
                    return NodeResult.Fail($"{ModuleName} 直线/线段 2 第 {b} 条{error2}");
                }
                switch (Mode)
                {
                    case GeometryRelationMode.LineToLineAngle:
                        HOperatorSet.AngleLl(r1, c1, r2, c2, r3, c3, r4, c4, out HTuple angle);
                        double degrees = FoldDegrees(AngleMath.ToDegrees(angle.D));
                        results.AnglesDeg.Add(degrees);
                        results.Angles.Add(AngleMath.ToRadians(degrees));
                        break;
                    case GeometryRelationMode.SegmentToSegment:
                        HOperatorSet.DistanceSs(r1, c1, r2, c2, r3, c3, r4, c4, out HTuple ssMin, out HTuple ssMax);
                        results.Distances.Add(ssMin.D);
                        results.MaxDistances.Add(ssMax.D);
                        break;
                    default:
                        HOperatorSet.DistanceSl(r1, c1, r2, c2, r3, c3, r4, c4, out HTuple slMin, out HTuple slMax);
                        results.Distances.Add(slMin.D);
                        results.MaxDistances.Add(slMax.D);
                        break;
                }
            }

            WriteOutputs(ctx, results);
            ctx.AddLog(FlowLogLevel.Info, Mode == GeometryRelationMode.LineToLineAngle
                ? $"[几何关系] 夹角 {pairCount} 对，第一对 {results.AnglesDeg[0]:F3}°"
                : $"[几何关系] {Mode} {pairCount} 对，第一对距离 {results.Distances[0]:F3}");
            return NodeResult.Ok;
        }

        /// <summary>所有输出总是写出；当前模式不产生的数组为空、单值为 NaN。</summary>
        private void WriteOutputs(FlowContext ctx, Results results)
        {
            HObject xld;
            if (results.FootRows.Count > 0)
            {
                HObject perpendiculars = XldLineHelper.Segments(results.PointRows, results.PointColumns, results.FootRows, results.FootColumns);
                HObject feet = XldLineHelper.Crosses(results.FootRows, results.FootColumns);
                HOperatorSet.ConcatObj(perpendiculars, feet, out xld);
                perpendiculars.Dispose();
                feet.Dispose();
            }
            else
            {
                HOperatorSet.GenEmptyObj(out xld);
            }
            HOperatorSet.CountObj(xld, out HTuple xldCount);
            int pairCount = results.Distances.Count + results.Angles.Count;
            SetOutput(ctx, Variable.Array(ModuleName, "Distances", VariableType.Double, results.Distances));
            SetOutput(ctx, Variable.Array(ModuleName, "MaxDistances", VariableType.Double, results.MaxDistances));
            SetOutput(ctx, Variable.Array(ModuleName, "Angles", VariableType.Double, results.Angles));
            SetOutput(ctx, Variable.Array(ModuleName, "AnglesDeg", VariableType.Double, results.AnglesDeg));
            SetOutput(ctx, Variable.Array(ModuleName, "FootRows", VariableType.Double, results.FootRows));
            SetOutput(ctx, Variable.Array(ModuleName, "FootColumns", VariableType.Double, results.FootColumns));
            SetOutput(ctx, Variable.Single(ModuleName, "Distance", VariableType.Double, First(results.Distances)));
            SetOutput(ctx, Variable.Single(ModuleName, "MaxDistance", VariableType.Double, First(results.MaxDistances)));
            SetOutput(ctx, Variable.Single(ModuleName, "Angle", VariableType.Double, First(results.Angles)));
            SetOutput(ctx, Variable.Single(ModuleName, "AngleDeg", VariableType.Double, First(results.AnglesDeg)));
            SetOutput(ctx, Variable.Single(ModuleName, "FootRow", VariableType.Double, First(results.FootRows)));
            SetOutput(ctx, Variable.Single(ModuleName, "FootColumn", VariableType.Double, First(results.FootColumns)));
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(xld), xldCount.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, pairCount));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, pairCount > 0));
        }

        private static double First(List<double> values)
        {
            return values.Count > 0 ? values[0] : double.NaN;
        }
    }
}
