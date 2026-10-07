using System;
using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>找角的一项结果：两条边（各取第一个实例）的端点、交点与夹角。</summary>
    public sealed class CornerMeasureResult
    {
        public int Index { get; set; }
        public double CornerRow { get; set; }
        public double CornerColumn { get; set; }
        /// <summary>从边 1 到边 2 的夹角（弧度，angle_ll，逆时针为正，范围 [-π, π]）。</summary>
        public double Angle { get; set; }
        public double AngleDeg { get; set; }
        public double Line1Row1 { get; set; }
        public double Line1Column1 { get; set; }
        public double Line1Row2 { get; set; }
        public double Line1Column2 { get; set; }
        public double Line2Row1 { get; set; }
        public double Line2Column1 { get; set; }
        public double Line2Row2 { get; set; }
        public double Line2Column2 { get; set; }
        public double Score1 { get; set; }
        public double Score2 { get; set; }
        public bool Followed { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")} Corner=({CornerRow:F2},{CornerColumn:F2}), Angle={AngleDeg:F2}°, Score={Score1:F3}/{Score2:F3}";
        }
    }

    /// <summary>
    /// 找角（MS-06）：在一个 metrology 模型中放两个直线测量对象，执行一次后按对象分别读取，求两条边的交点（intersection_lines）
    /// 与夹角（angle_ll）。对标 VisionPro CogFindCorner。
    /// 多实例本批固定：每条边只取第一个实例（实例数不开放，模型按 1 个实例设置）；Scores 依次为两条边的得分。
    /// 两条边平行（夹角或其补角小于 0.5°）、任一边未找到或得分低于 MinScore 时按“未找到”处理（交点与角度写 NaN）。
    /// 示教参数为 TeachLine1Row1 等 8 个（与输出 Line1Row1 等区分）。
    /// </summary>
    [ToolOutput("CornerRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("CornerColumn", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Angle", VariableKind.Single, VariableType.Double)]
    [ToolOutput("AngleDeg", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Line1Row1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Line1Column1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Line1Row2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Line1Column2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Line2Row1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Line2Column1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Line2Row2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Line2Column2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Score1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Score2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("FailedCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<CornerMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    public sealed class CornerFindTool : MetrologyMeasureToolBase
    {
        /// <summary>两条边平行的判定阈值（度）：夹角或其补角小于该值时没有可靠交点。</summary>
        public const double ParallelToleranceDeg = 0.5;

        public double TeachLine1Row1 { get; set; } = 100;
        public double TeachLine1Column1 { get; set; } = 100;
        public double TeachLine1Row2 { get; set; } = 100;
        public double TeachLine1Column2 { get; set; } = 250;
        public double TeachLine2Row1 { get; set; } = 100;
        public double TeachLine2Column1 { get; set; } = 100;
        public double TeachLine2Row2 { get; set; } = 250;
        public double TeachLine2Column2 { get; set; } = 100;

        public CornerFindTool(string moduleName) : base(moduleName)
        {
        }

        /// <summary>两条边的端点由 Line1* / Line2* 单值输出，不再按实例展开数组。</summary>
        protected override string[] InstanceOutputNames
        {
            get { return new string[0]; }
        }

        public override bool ExposesNumInstances
        {
            get { return false; }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            NodeResult invalid = CheckBeforeRun();
            if (invalid != null)
            {
                return invalid;
            }
            if (Distance(TeachLine1Row1, TeachLine1Column1, TeachLine1Row2, TeachLine1Column2) < 1e-6
                || Distance(TeachLine2Row1, TeachLine2Column1, TeachLine2Row2, TeachLine2Column2) < 1e-6)
            {
                return NodeResult.Fail($"{ModuleName} 示教的边的两个端点重合，请重新示教两条边");
            }
            return RunMeasurements(ctx, "找角",
                (HObject image, HomMat2D matrix, int resultIndex, out HObject contour) =>
                    MeasureOne(ctx, image, matrix, resultIndex, out contour),
                null);
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, out HObject contour)
        {
            contour = null;
            double[] teach =
            {
                TeachLine1Row1, TeachLine1Column1, TeachLine1Row2, TeachLine1Column2,
                TeachLine2Row1, TeachLine2Column1, TeachLine2Row2, TeachLine2Column2
            };
            bool followed = matrix != null;
            if (followed)
            {
                for (int i = 0; i < teach.Length; i += 2)
                {
                    matrix.TransformPoint(teach[i], teach[i + 1], out teach[i], out teach[i + 1]);
                }
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            try
            {
                HOperatorSet.SetMetrologyModelImageSize(metrology, width, height);
                AddMetrologyParams(out HTuple names, out HTuple values);
                HOperatorSet.AddMetrologyObjectLineMeasure(metrology, teach[0], teach[1], teach[2], teach[3],
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold, names, values, out HTuple _);
                HOperatorSet.AddMetrologyObjectLineMeasure(metrology, teach[4], teach[5], teach[6], teach[7],
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold, names, values, out HTuple _);
                // 同一模型只执行一次，再按对象读取（边缘点、得分与种子由基类累积）
                RunMetrology(image, metrology);
                HTuple line1 = ReadMetrologyObject(metrology, 0, 4, out HObject contour1);
                HTuple line2 = ReadMetrologyObject(metrology, 1, 4, out HObject contour2);
                HOperatorSet.ConcatObj(contour1, contour2, out contour);
                contour1.Dispose();
                contour2.Dispose();

                if (line1.Length < 4 || line2.Length < 4)
                {
                    return NodeResult.Fail($"找角失败：{(line1.Length < 4 ? "边 1" : "边 2")} 未找到足够的边缘点");
                }
                double score1 = FirstScore(metrology, 0);
                double score2 = FirstScore(metrology, 1);
                if (score1 < MinScore || score2 < MinScore)
                {
                    return NodeResult.Fail($"找角失败：边得分 {score1:F3} / {score2:F3} 低于最低得分 {MinScore}");
                }

                // angle_ll：从边 1 到边 2 的夹角，弧度，逆时针为正，范围 [-π, π]（22.11 实测，反向共线为 -π）
                HOperatorSet.AngleLl(line1[0], line1[1], line1[2], line1[3], line2[0], line2[1], line2[2], line2[3], out HTuple angle);
                double acute = Math.Min(Math.Abs(angle.D), Math.PI - Math.Abs(angle.D));
                if (AngleMath.ToDegrees(acute) < ParallelToleranceDeg)
                {
                    return NodeResult.Fail($"找角失败：两条边近似平行（夹角 {AngleMath.ToDegrees(angle.D):F3}°），没有可靠交点");
                }
                // intersection_lines：平行或共线时交点为空（共线时 IsOverlapping = 1，22.11 实测）
                HOperatorSet.IntersectionLines(line1[0], line1[1], line1[2], line1[3], line2[0], line2[1], line2[2], line2[3],
                    out HTuple cornerRow, out HTuple cornerColumn, out HTuple _);
                if (cornerRow.Length == 0)
                {
                    return NodeResult.Fail("找角失败：两条边平行，没有交点");
                }

                var result = new CornerMeasureResult
                {
                    Index = ResolveIndex(ctx, resultIndex),
                    CornerRow = cornerRow.D,
                    CornerColumn = cornerColumn.D,
                    Angle = angle.D,
                    AngleDeg = AngleMath.ToDegrees(angle.D),
                    Line1Row1 = line1[0].D,
                    Line1Column1 = line1[1].D,
                    Line1Row2 = line1[2].D,
                    Line1Column2 = line1[3].D,
                    Line2Row1 = line2[0].D,
                    Line2Column1 = line2[1].D,
                    Line2Row2 = line2[2].D,
                    Line2Column2 = line2[3].D,
                    Score1 = score1,
                    Score2 = score2,
                    Followed = followed
                };
                HObject cross = XldLineHelper.Crosses(new[] { result.CornerRow }, new[] { result.CornerColumn });
                HOperatorSet.ConcatObj(contour, cross, out HObject withCross);
                cross.Dispose();
                contour.Dispose();
                contour = withCross;
                WriteSingles(ctx, result);
                AddResult(ctx, ModuleName, result);
                ctx.AddLog(FlowLogLevel.Info, $"[找角]{(followed ? "跟随" : "固定")} {result}");
                return NodeResult.Ok;
            }
            finally
            {
                HOperatorSet.ClearMetrologyModel(metrology);
            }
        }

        private static double FirstScore(HTuple metrology, int index)
        {
            HOperatorSet.GetMetrologyObjectResult(metrology, index, 0, "result_type", "score", out HTuple score);
            return score.Length > 0 ? score[0].D : double.NaN;
        }

        private void WriteSingles(FlowContext ctx, CornerMeasureResult r)
        {
            void Write(string name, double value) => SetOutput(ctx, Variable.Single(ModuleName, name, VariableType.Double, value));
            Write("CornerRow", r.CornerRow);
            Write("CornerColumn", r.CornerColumn);
            Write("Angle", r.Angle);
            Write("AngleDeg", r.AngleDeg);
            Write("Line1Row1", r.Line1Row1);
            Write("Line1Column1", r.Line1Column1);
            Write("Line1Row2", r.Line1Row2);
            Write("Line1Column2", r.Line1Column2);
            Write("Line2Row1", r.Line2Row1);
            Write("Line2Column1", r.Line2Column1);
            Write("Line2Row2", r.Line2Row2);
            Write("Line2Column2", r.Line2Column2);
            Write("Score1", r.Score1);
            Write("Score2", r.Score2);
        }

        private static double Distance(double r1, double c1, double r2, double c2)
        {
            return Math.Sqrt((r1 - r2) * (r1 - r2) + (c1 - c2) * (c1 - c2));
        }
    }
}
