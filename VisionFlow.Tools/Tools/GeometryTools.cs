using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    public sealed class LineFitResult
    {
        public double Row1 { get; set; }
        public double Column1 { get; set; }
        public double Row2 { get; set; }
        public double Column2 { get; set; }
        public double Nr { get; set; }
        public double Nc { get; set; }
        public double Distance { get; set; }
    }

    public sealed class CircleFitResult
    {
        public double Row { get; set; }
        public double Column { get; set; }
        public double Radius { get; set; }
        public double StartPhi { get; set; }
        public double EndPhi { get; set; }
        public string Order { get; set; }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Results", VariableKind.Array, VariableType.Object, ElementClrType = typeof(LineFitResult),
        Members = new[] { "Row1", "Column1", "Row2", "Column2", "Nr", "Nc", "Distance" })]
    [ToolOutput("Row1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Row2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class FitLineTool : XldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Algorithm { get; set; } = "tukey";
        public int MaxNumPoints { get; set; } = -1;
        public int ClippingEndPoint { get; set; }
        public int Iterations { get; set; } = 3;
        public double ClippingFactor { get; set; } = 2;

        public FitLineTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            int maxPoints = MaxNumPoints < 3 && MaxNumPoints != -1 ? 3 : MaxNumPoints;
            HOperatorSet.FitLineContourXld(xld, Algorithm, maxPoints, ClippingEndPoint, Iterations, ClippingFactor,
                out HTuple row1, out HTuple col1, out HTuple row2, out HTuple col2, out HTuple nr, out HTuple nc, out HTuple dist);
            if (row1.Length == 0)
            {
                return NodeResult.Fail("拟合直线未找到结果");
            }

            var results = new List<LineFitResult>();
            for (int i = 0; i < row1.Length; i++)
            {
                results.Add(new LineFitResult
                {
                    Row1 = row1[i].D,
                    Column1 = col1[i].D,
                    Row2 = row2[i].D,
                    Column2 = col2[i].D,
                    Nr = nr[i].D,
                    Nc = nc[i].D,
                    Distance = dist[i].D
                });
            }

            HOperatorSet.GenContourPolygonXld(out HObject lineXld, row1.TupleConcat(row2), col1.TupleConcat(col2));
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(lineXld), results.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Results", VariableType.Object, results));
            SetOutput(ctx, Variable.Single(ModuleName, "Row1", VariableType.Double, results[0].Row1));
            SetOutput(ctx, Variable.Single(ModuleName, "Column1", VariableType.Double, results[0].Column1));
            SetOutput(ctx, Variable.Single(ModuleName, "Row2", VariableType.Double, results[0].Row2));
            SetOutput(ctx, Variable.Single(ModuleName, "Column2", VariableType.Double, results[0].Column2));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, results.Count));
            ctx.AddLog(FlowLogLevel.Info, $"[拟合直线] 输出 {results.Count} 条直线");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Results", VariableKind.Array, VariableType.Object, ElementClrType = typeof(CircleFitResult),
        Members = new[] { "Row", "Column", "Radius", "StartPhi", "EndPhi", "Order" })]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Radius", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class FitCircleTool : XldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Algorithm { get; set; } = "algebraic";
        public int MaxNumPoints { get; set; } = -1;
        public int MaxClosureDist { get; set; }
        public int ClippingEndPoints { get; set; }
        public int Iterations { get; set; } = 3;
        public double ClippingFactor { get; set; } = 2;

        public FitCircleTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            int maxPoints = MaxNumPoints < 3 && MaxNumPoints != -1 ? 3 : MaxNumPoints;
            HOperatorSet.FitCircleContourXld(xld, Algorithm, maxPoints, MaxClosureDist, ClippingEndPoints,
                Iterations, ClippingFactor, out HTuple row, out HTuple col, out HTuple radius,
                out HTuple start, out HTuple end, out HTuple order);
            if (row.Length == 0)
            {
                return NodeResult.Fail("拟合圆未找到结果");
            }

            var results = new List<CircleFitResult>();
            for (int i = 0; i < row.Length; i++)
            {
                results.Add(new CircleFitResult
                {
                    Row = row[i].D,
                    Column = col[i].D,
                    Radius = radius[i].D,
                    StartPhi = start[i].D,
                    EndPhi = end[i].D,
                    Order = order[i].S
                });
            }

            HOperatorSet.GenCircleContourXld(out HObject circleXld, row, col, radius, start, end, order, 1);
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(circleXld), results.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Results", VariableType.Object, results));
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, results[0].Row));
            SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, results[0].Column));
            SetOutput(ctx, Variable.Single(ModuleName, "Radius", VariableType.Double, results[0].Radius));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, results.Count));
            ctx.AddLog(FlowLogLevel.Info, $"[拟合圆] 输出 {results.Count} 个圆");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Overlap", VariableKind.Single, VariableType.Int)]
    public sealed class IntersectionLinesTool : ToolBase
    {
        [InputRef("直线1", typeof(HalconXld))]
        public string Line1Path { get; set; }

        [InputRef("直线2", typeof(HalconXld))]
        public string Line2Path { get; set; }

        public IntersectionLinesTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject line1 = Input<HalconXld>(ctx, Line1Path).Object;
            HObject line2 = Input<HalconXld>(ctx, Line2Path).Object;
            GetLinePoints(line1, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
            GetLinePoints(line2, out HTuple r3, out HTuple c3, out HTuple r4, out HTuple c4);
            HOperatorSet.IntersectionLines(r1, c1, r2, c2, r3, c3, r4, c4, out HTuple row, out HTuple col, out HTuple overlap);
            HOperatorSet.GenCrossContourXld(out HObject cross, row, col, 15, 0.57);

            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(cross), 1));
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, row.D));
            SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, col.D));
            SetOutput(ctx, Variable.Single(ModuleName, "Overlap", VariableType.Int, overlap.I));
            ctx.AddLog(FlowLogLevel.Info, $"[线线交点] Row={row.D:F2}, Column={col.D:F2}, Overlap={overlap.I}");
            return NodeResult.Ok;
        }

        private static void GetLinePoints(HObject line, out HTuple row1, out HTuple col1, out HTuple row2, out HTuple col2)
        {
            HOperatorSet.SelectObj(line, out HObject first, 1);
            HOperatorSet.SmallestRectangle1Xld(first, out row1, out col1, out row2, out col2);
            first.Dispose();
        }
    }

    [ToolOutput("WorldRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("WorldColumn", VariableKind.Single, VariableType.Double)]
    public sealed class AffinePointTool : ToolBase
    {
        [InputRef("Row", typeof(double))]
        public string RowPath { get; set; }

        [InputRef("Column", typeof(double))]
        public string ColumnPath { get; set; }

        [InputRef("变换矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        public string CalibrationFile { get; set; }
        public double OriginRow { get; set; }
        public double OriginColumn { get; set; }

        public AffinePointTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            double row = VariableReference.Parse(RowPath).Resolve<double>(ctx);
            double col = VariableReference.Parse(ColumnPath).Resolve<double>(ctx);
            HomMat2D matrix;
            if (!string.IsNullOrWhiteSpace(MatrixPath))
            {
                matrix = VariableReference.Parse(MatrixPath).Resolve<HomMat2D>(ctx);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(CalibrationFile))
                {
                    return NodeResult.Fail("未配置变换矩阵引用或 CalibrationFile");
                }
                HOperatorSet.ReadTuple(CalibrationFile, out HTuple tuple);
                matrix = new HomMat2D(tuple);
            }

            matrix.TransformPoint(row, col, out double worldRow, out double worldCol);
            worldRow -= OriginRow;
            worldCol -= OriginColumn;
            SetOutput(ctx, Variable.Single(ModuleName, "WorldRow", VariableType.Double, worldRow));
            SetOutput(ctx, Variable.Single(ModuleName, "WorldColumn", VariableType.Double, worldCol));
            ctx.AddLog(FlowLogLevel.Info, $"[坐标转换] ({row:F2},{col:F2}) -> ({worldRow:F3},{worldCol:F3})");
            return NodeResult.Ok;
        }
    }
}
