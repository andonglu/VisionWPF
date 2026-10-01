using System;
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
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
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

            HOperatorSet.GenEmptyObj(out HObject lineXld);
            for (int i = 0; i < results.Count; i++)
            {
                HOperatorSet.GenContourPolygonXld(out HObject single,
                    new HTuple(results[i].Row1, results[i].Row2), new HTuple(results[i].Column1, results[i].Column2));
                HOperatorSet.ConcatObj(lineXld, single, out HObject combined);
                lineXld.Dispose();
                single.Dispose();
                lineXld = combined;
            }
            bool found = results.Count > 0;
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(lineXld), results.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Results", VariableType.Object, results));
            SetOutput(ctx, Variable.Single(ModuleName, "Row1", VariableType.Double, found ? results[0].Row1 : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Column1", VariableType.Double, found ? results[0].Column1 : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Row2", VariableType.Double, found ? results[0].Row2 : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Column2", VariableType.Double, found ? results[0].Column2 : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, results.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, found));
            if (!found)
            {
                return NotFoundOutcome.Resolve(ctx, this, "拟合直线未找到结果");
            }
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
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
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

            HObject circleXld;
            if (results.Count > 0)
            {
                HOperatorSet.GenCircleContourXld(out circleXld, row, col, radius, start, end, order, 1);
            }
            else
            {
                HOperatorSet.GenEmptyObj(out circleXld);
            }
            bool found = results.Count > 0;
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(circleXld), results.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Results", VariableType.Object, results));
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, found ? results[0].Row : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, found ? results[0].Column : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Radius", VariableType.Double, found ? results[0].Radius : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, results.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, found));
            if (!found)
            {
                return NotFoundOutcome.Resolve(ctx, this, "拟合圆未找到结果");
            }
            ctx.AddLog(FlowLogLevel.Info, $"[拟合圆] 输出 {results.Count} 个圆");
            return NodeResult.Ok;
        }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Overlap", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class IntersectionLinesTool : ToolBase, INotFoundPolicy
    {
        [InputRef("直线1", typeof(HalconXld))]
        public string Line1Path { get; set; }

        [InputRef("直线2", typeof(HalconXld))]
        public string Line2Path { get; set; }

        /// <summary>两线平行或重合（无唯一交点）时是否失败（默认 true）；关闭后输出 Found=false、Row/Column=NaN 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public IntersectionLinesTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject line1 = Input<HalconXld>(ctx, Line1Path).Object;
            HObject line2 = Input<HalconXld>(ctx, Line2Path).Object;
            if (!TryGetLinePoints(line1, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2, out string error1))
            {
                return NodeResult.Fail("直线1" + error1);
            }
            if (!TryGetLinePoints(line2, out HTuple r3, out HTuple c3, out HTuple r4, out HTuple c4, out string error2))
            {
                return NodeResult.Fail("直线2" + error2);
            }
            HOperatorSet.IntersectionLines(r1, c1, r2, c2, r3, c3, r4, c4, out HTuple row, out HTuple col, out HTuple overlap);
            int overlapValue = overlap.Length > 0 ? overlap.I : 0;
            if (row.Length == 0 || col.Length == 0)
            {
                HOperatorSet.GenEmptyObj(out HObject empty);
                SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(empty), 0));
                SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, double.NaN));
                SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, double.NaN));
                SetOutput(ctx, Variable.Single(ModuleName, "Overlap", VariableType.Int, overlapValue));
                SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, false));
                string reason = overlapValue != 0 ? "两条直线重合" : "两条直线平行";
                return NotFoundOutcome.Resolve(ctx, this, $"[线线交点] {reason}，无唯一交点");
            }
            HOperatorSet.GenCrossContourXld(out HObject cross, row, col, 15, 0.57);

            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(cross), 1));
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, row.D));
            SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, col.D));
            SetOutput(ctx, Variable.Single(ModuleName, "Overlap", VariableType.Int, overlapValue));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, true));
            ctx.AddLog(FlowLogLevel.Info, $"[线线交点] Row={row.D:F2}, Column={col.D:F2}, Overlap={overlapValue}");
            return NodeResult.Ok;
        }

        /// <summary>
        /// 取 XLD 中第一条轮廓代表的直线：两点轮廓直接取首尾点（拟合直线、直线测量的输出）；
        /// 多点轮廓先做直线拟合，避免外接矩形把反斜线取成另一条对角线。
        /// </summary>
        private static bool TryGetLinePoints(HObject line, out HTuple row1, out HTuple col1,
            out HTuple row2, out HTuple col2, out string error)
        {
            row1 = col1 = row2 = col2 = null;
            error = null;
            HOperatorSet.CountObj(line, out HTuple count);
            if (count.I == 0)
            {
                error = " XLD 为空";
                return false;
            }
            HOperatorSet.SelectObj(line, out HObject first, 1);
            try
            {
                HOperatorSet.GetContourXld(first, out HTuple rows, out HTuple cols);
                if (rows.Length < 2)
                {
                    error = " 轮廓点数不足 2 个";
                    return false;
                }
                if (rows.Length == 2)
                {
                    row1 = rows[0];
                    col1 = cols[0];
                    row2 = rows[1];
                    col2 = cols[1];
                    return true;
                }
                HOperatorSet.FitLineContourXld(first, "tukey", -1, 0, 5, 2,
                    out row1, out col1, out row2, out col2, out _, out _, out _);
                if (row1.Length == 0)
                {
                    error = " 直线拟合失败";
                    return false;
                }
                return true;
            }
            finally
            {
                first.Dispose();
            }
        }
    }

    [ToolOutput("WorldRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("WorldColumn", VariableKind.Single, VariableType.Double)]
    public sealed class AffinePointTool : ToolBase, IToolResourceLifecycle
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

        private readonly object _calibrationSync = new object();
        private string _cachedCalibrationPath;
        private DateTime _cachedCalibrationWriteTime;
        private HomMat2D _cachedCalibration;

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
                if (!System.IO.File.Exists(CalibrationFile))
                {
                    return NodeResult.Fail($"标定文件不存在：{CalibrationFile}");
                }
                matrix = LoadCalibration(CalibrationFile);
            }

            matrix.TransformPoint(row, col, out double worldRow, out double worldCol);
            worldRow -= OriginRow;
            worldCol -= OriginColumn;
            SetOutput(ctx, Variable.Single(ModuleName, "WorldRow", VariableType.Double, worldRow));
            SetOutput(ctx, Variable.Single(ModuleName, "WorldColumn", VariableType.Double, worldCol));
            ctx.AddLog(FlowLogLevel.Info, $"[坐标转换] ({row:F2},{col:F2}) -> ({worldRow:F3},{worldCol:F3})");
            return NodeResult.Ok;
        }

        /// <summary>读取标定矩阵并按“路径 + 文件修改时间”缓存（TR-13），文件被替换后自动重新读取。</summary>
        private HomMat2D LoadCalibration(string path)
        {
            DateTime writeTime = System.IO.File.GetLastWriteTimeUtc(path);
            lock (_calibrationSync)
            {
                if (_cachedCalibration != null
                    && string.Equals(_cachedCalibrationPath, path, StringComparison.OrdinalIgnoreCase)
                    && _cachedCalibrationWriteTime == writeTime)
                {
                    return _cachedCalibration;
                }
                HOperatorSet.ReadTuple(path, out HTuple tuple);
                _cachedCalibration = new HomMat2D(tuple);
                _cachedCalibrationPath = path;
                _cachedCalibrationWriteTime = writeTime;
                return _cachedCalibration;
            }
        }

        /// <summary>预热：使用标定文件（未引用变换矩阵）时预先读取；文件不存在时抛出，便于投产前发现。</summary>
        public void Prepare()
        {
            if (!string.IsNullOrWhiteSpace(MatrixPath) || string.IsNullOrWhiteSpace(CalibrationFile))
            {
                return;
            }
            if (!System.IO.File.Exists(CalibrationFile))
            {
                throw new System.IO.FileNotFoundException($"标定文件不存在：{CalibrationFile}", CalibrationFile);
            }
            LoadCalibration(CalibrationFile);
        }

        public void ReleaseResources()
        {
            lock (_calibrationSync)
            {
                _cachedCalibration = null;
                _cachedCalibrationPath = null;
            }
        }
    }
}
