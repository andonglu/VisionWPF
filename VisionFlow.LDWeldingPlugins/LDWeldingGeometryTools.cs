using System;
using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.LDWeldingPlugins
{
    public sealed class LDLineFitResult
    {
        public double Row1 { get; set; }
        public double Column1 { get; set; }
        public double Row2 { get; set; }
        public double Column2 { get; set; }
        public double Nr { get; set; }
        public double Nc { get; set; }
        public double Distance { get; set; }
    }

    public sealed class LDCircleFitResult
    {
        public double Row { get; set; }
        public double Column { get; set; }
        public double Radius { get; set; }
        public double StartPhi { get; set; }
        public double EndPhi { get; set; }
        public string Order { get; set; }
    }

    [ToolboxTool("05 几何测量", "拟合直线", Id = "ldwelding.fit-line", DefaultModuleName = "拟合直线")]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Results", VariableKind.Array, VariableType.Object, ElementClrType = typeof(LDLineFitResult),
        Members = new[] { "Row1", "Column1", "Row2", "Column2", "Nr", "Nc", "Distance" })]
    [ToolOutput("Row1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Row2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDFitLineTool : LDXldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Algorithm { get; set; } = "tukey";
        public int MaxNumPoints { get; set; } = -1;
        public int ClippingEndPoint { get; set; }
        public int Iterations { get; set; } = 3;
        public double ClippingFactor { get; set; } = 2;

        public LDFitLineTool(string moduleName) : base(moduleName)
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

            var results = new List<LDLineFitResult>();
            for (int i = 0; i < row1.Length; i++)
            {
                results.Add(new LDLineFitResult
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
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 拟合直线] 输出 {results.Count} 条直线");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("05 几何测量", "拟合圆", Id = "ldwelding.fit-circle", DefaultModuleName = "拟合圆")]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Results", VariableKind.Array, VariableType.Object, ElementClrType = typeof(LDCircleFitResult),
        Members = new[] { "Row", "Column", "Radius", "StartPhi", "EndPhi", "Order" })]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Radius", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDFitCircleTool : LDXldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Algorithm { get; set; } = "algebraic";
        public int MaxNumPoints { get; set; } = -1;
        public int MaxClosureDist { get; set; }
        public int ClippingEndPoints { get; set; }
        public int Iterations { get; set; } = 3;
        public double ClippingFactor { get; set; } = 2;

        public LDFitCircleTool(string moduleName) : base(moduleName)
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

            var results = new List<LDCircleFitResult>();
            for (int i = 0; i < row.Length; i++)
            {
                results.Add(new LDCircleFitResult
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
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 拟合圆] 输出 {results.Count} 个圆");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("05 几何测量", "线线交点", Id = "ldwelding.intersection-lines", DefaultModuleName = "线线交点")]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Overlap", VariableKind.Single, VariableType.Int)]
    public sealed class LDIntersectionLinesTool : ToolBase
    {
        [InputRef("直线1", typeof(HalconXld))]
        public string Line1Path { get; set; }

        [InputRef("直线2", typeof(HalconXld))]
        public string Line2Path { get; set; }

        public LDIntersectionLinesTool(string moduleName) : base(moduleName)
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
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 线线交点] Row={row.D:F2}, Column={col.D:F2}, Overlap={overlap.I}");
            return NodeResult.Ok;
        }

        private static void GetLinePoints(HObject line, out HTuple row1, out HTuple col1, out HTuple row2, out HTuple col2)
        {
            HOperatorSet.SelectObj(line, out HObject first, 1);
            HOperatorSet.SmallestRectangle1Xld(first, out row1, out col1, out row2, out col2);
            first.Dispose();
        }
    }

    [ToolboxTool("05 几何测量", "图像坐标转世界坐标", Id = "ldwelding.affine-point", DefaultModuleName = "坐标转换")]
    [ToolOutput("WorldRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("WorldColumn", VariableKind.Single, VariableType.Double)]
    public sealed class LDAffinePointTool : ToolBase
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

        public LDAffinePointTool(string moduleName) : base(moduleName)
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
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 坐标转换] ({row:F2},{col:F2}) -> ({worldRow:F3},{worldCol:F3})");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("06 识别工具", "一维码", Id = "ldwelding.barcode1d", DefaultModuleName = "一维码")]
    [ToolOutput("Codes", VariableKind.Array, VariableType.String)]
    [ToolOutput("FirstCode", VariableKind.Single, VariableType.String)]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDBarcode1DTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("ROI区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        public string CodeType { get; set; } = "auto";

        public LDBarcode1DTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject runImage = image;
            bool ownsReduced = false;
            if (!string.IsNullOrWhiteSpace(RegionPath))
            {
                HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
                HOperatorSet.ReduceDomain(image, region, out runImage);
                ownsReduced = true;
            }

            HOperatorSet.CreateBarCodeModel(new HTuple(), new HTuple(), out HTuple handle);
            try
            {
                HOperatorSet.FindBarCode(runImage, out HObject symbolRegions, handle, CodeType, out HTuple codes);
                var strings = new List<string>();
                for (int i = 0; i < codes.Length; i++)
                {
                    strings.Add(codes[i].S);
                }
                SetOutput(ctx, Variable.Array(ModuleName, "Codes", VariableType.String, strings));
                SetOutput(ctx, Variable.Single(ModuleName, "FirstCode", VariableType.String, strings.Count > 0 ? strings[0] : string.Empty));
                SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(symbolRegions), strings.Count));
                SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, strings.Count));
                ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 一维码] 识别数量={strings.Count}");
                return strings.Count > 0 ? NodeResult.Ok : NodeResult.Fail("未识别到一维码");
            }
            finally
            {
                HOperatorSet.ClearBarCodeModel(handle);
                if (ownsReduced)
                {
                    runImage.Dispose();
                }
            }
        }
    }
}
