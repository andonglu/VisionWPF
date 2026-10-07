using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>交点计算模式。</summary>
    public enum IntersectionMode
    {
        /// <summary>直线与直线（intersection_lines，原“线线交点”）。</summary>
        LineLine,
        /// <summary>直线与轮廓（intersection_line_contour_xld）：输入 1 按直线（无限长）处理，输入 2 为任意轮廓。</summary>
        LineContour,
        /// <summary>轮廓与轮廓（intersection_contours_xld）。</summary>
        ContourContour
    }

    /// <summary>轮廓与轮廓求交的范围（成员名即 HALCON 参数值）。</summary>
    public enum ContourIntersectionType
    {
        /// <summary>只求两组轮廓之间的交点。</summary>
        mutual,
        /// <summary>两组之间以及各组内部的交点。</summary>
        all,
        /// <summary>各条轮廓自身的交点。</summary>
        self
    }

    /// <summary>
    /// 交点计算（原“线线交点”，XG-05）：直线与直线、直线与轮廓、轮廓与轮廓。
    /// 直线输入按 <see cref="XldLineHelper"/> 解释（两点轮廓取首尾点，多点轮廓先拟合直线）。
    /// 多个交点时输出 Rows / Columns 数组，Row / Column 取第一个；Xld 为各交点的十字标记，便于叠加显示。
    /// </summary>
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Overlap", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Rows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Columns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class IntersectionLinesTool : ToolBase, INotFoundPolicy, IToolParameterVisibility
    {
        [InputRef("直线/轮廓1", typeof(HalconXld))]
        public string Line1Path { get; set; }

        [InputRef("直线/轮廓2", typeof(HalconXld))]
        public string Line2Path { get; set; }

        /// <summary>交点计算模式；默认直线与直线，与原“线线交点”一致。</summary>
        public IntersectionMode Mode { get; set; } = IntersectionMode.LineLine;

        /// <summary>轮廓与轮廓求交的范围（仅轮廓与轮廓模式）。</summary>
        public ContourIntersectionType IntersectionType { get; set; } = ContourIntersectionType.mutual;

        /// <summary>没有交点（平行、重合或不相交）时是否失败（默认 true）；关闭后输出 Found=false、Row/Column=NaN 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public IntersectionLinesTool(string moduleName) : base(moduleName)
        {
        }

        public bool IsParameterVisible(string propertyName)
        {
            return propertyName != nameof(IntersectionType) || Mode == IntersectionMode.ContourContour;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject input1 = Input<HalconXld>(ctx, Line1Path).Object;
            HObject input2 = Input<HalconXld>(ctx, Line2Path).Object;
            HTuple rows;
            HTuple columns;
            HTuple overlap;
            string label;
            switch (Mode)
            {
                case IntersectionMode.LineContour:
                    if (!XldLineHelper.TryGetLine(input1, 0, out double lr1, out double lc1, out double lr2, out double lc2, out string lineError))
                    {
                        return NodeResult.Fail("直线/轮廓1" + lineError);
                    }
                    HOperatorSet.IntersectionLineContourXld(input2, lr1, lc1, lr2, lc2, out rows, out columns, out overlap);
                    label = "直线与轮廓交点";
                    break;
                case IntersectionMode.ContourContour:
                    HOperatorSet.IntersectionContoursXld(input1, input2, IntersectionType.ToString(), out rows, out columns, out overlap);
                    label = $"轮廓与轮廓交点（{IntersectionType}）";
                    break;
                default:
                    if (!XldLineHelper.TryGetLine(input1, 0, out double r1, out double c1, out double r2, out double c2, out string error1))
                    {
                        return NodeResult.Fail("直线1" + error1);
                    }
                    if (!XldLineHelper.TryGetLine(input2, 0, out double r3, out double c3, out double r4, out double c4, out string error2))
                    {
                        return NodeResult.Fail("直线2" + error2);
                    }
                    HOperatorSet.IntersectionLines(r1, c1, r2, c2, r3, c3, r4, c4, out rows, out columns, out overlap);
                    label = "线线交点";
                    break;
            }

            int overlapValue = 0;
            for (int i = 0; i < overlap.Length; i++)
            {
                overlapValue = Math.Max(overlapValue, overlap[i].I);
            }
            List<double> rowList = HalconTupleConvert.ToDoubles(rows).ToList();
            List<double> columnList = HalconTupleConvert.ToDoubles(columns).ToList();
            bool found = rowList.Count > 0 && rowList.Count == columnList.Count;
            if (!found)
            {
                rowList.Clear();
                columnList.Clear();
            }

            HObject cross = XldLineHelper.Crosses(rowList, columnList);
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(cross), rowList.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, found ? rowList[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, found ? columnList[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Overlap", VariableType.Int, overlapValue));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, found));
            SetOutput(ctx, Variable.Array(ModuleName, "Rows", VariableType.Double, rowList));
            SetOutput(ctx, Variable.Array(ModuleName, "Columns", VariableType.Double, columnList));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, rowList.Count));
            if (!found)
            {
                string reason = Mode == IntersectionMode.LineLine
                    ? (overlapValue != 0 ? "两条直线重合" : "两条直线平行") + "，无唯一交点"
                    : (overlapValue != 0 ? "存在重合部分，没有离散交点" : "没有交点");
                return NotFoundOutcome.Resolve(ctx, this, $"[{label}] {reason}");
            }
            ctx.AddLog(FlowLogLevel.Info, Mode == IntersectionMode.LineLine
                ? $"[线线交点] Row={rowList[0]:F2}, Column={columnList[0]:F2}, Overlap={overlapValue}"
                : $"[{label}] {rowList.Count} 个交点，第一个 Row={rowList[0]:F2}, Column={columnList[0]:F2}");
            return NodeResult.Ok;
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
