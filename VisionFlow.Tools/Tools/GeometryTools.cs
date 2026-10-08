using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Tools.Calibration;
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

    /// <summary>坐标转换的标定方式（按数字保存，追加在末尾）。</summary>
    public enum CalibrationKind
    {
        /// <summary>仿射矩阵（引用的矩阵、旧版 write_tuple 文件或 .vfcal.json 的 Affine2D），现有行为。</summary>
        Affine2D,
        /// <summary>相机标定结果（.vfcal.json 的 Camera），image_points_to_world_plane。</summary>
        Camera
    }

    /// <summary>标定结果来源（按数字保存，追加在末尾）：外部文件或内嵌在工具中，二者互斥。</summary>
    public enum CalibrationSource
    {
        File,
        Embedded
    }

    /// <summary>
    /// 图像坐标转世界坐标（CB-05）：Row / Column 支持单值或数组（个数相等逐一对应，一侧为 1 个时一对多）；
    /// 可选角度输入（弧度）换算为物理角度。标定来自引用的矩阵（仅 Affine2D，优先）、标定文件或内嵌的标定数据。
    /// 坐标与角度约定：WorldRow = 物理 X、WorldColumn = 物理 Y（vector_to_hom_mat2d 的 Qx / Qy）；
    /// WorldAngle 与图像角度同一约定——方向向量 (ΔWorldRow, ΔWorldColumn)，WorldAngle = atan2(−ΔWorldRow, ΔWorldColumn)，
    /// 单位矩阵时等于输入角度；方向向量直接经变换求得，镜像（行列式为负）时自然正确。
    /// </summary>
    [ToolOutput("WorldRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("WorldColumn", VariableKind.Single, VariableType.Double)]
    [ToolOutput("WorldRows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("WorldColumns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("WorldAngle", VariableKind.Single, VariableType.Double)]
    [ToolOutput("WorldAngles", VariableKind.Array, VariableType.Double)]
    [ToolOutput("WorldAngleDeg", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Matrix", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    public sealed class AffinePointTool : ToolBase, IToolResourceLifecycle, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("Row", typeof(double), AcceptsCollection = true)]
        public string RowPath { get; set; }

        [InputRef("Column", typeof(double), AcceptsCollection = true)]
        public string ColumnPath { get; set; }

        [InputRef("角度", typeof(double), Optional = true, AcceptsCollection = true)]
        public string AnglePath { get; set; }

        [InputRef("变换矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        public CalibrationKind CalibrationKind { get; set; } = CalibrationKind.Affine2D;
        public CalibrationSource CalibrationSource { get; set; } = CalibrationSource.File;
        /// <summary>标定文件：.vfcal.json 或旧版 write_tuple 矩阵文件（按内容自动识别）。</summary>
        public string CalibrationFile { get; set; }
        /// <summary>内嵌的 .vfcal.json 内容（CalibrationSource = Embedded 时使用；随流程文件保存，中文按既有序列化行为转义为 \uXXXX）。</summary>
        public string CalibrationData { get; set; }
        public double OriginRow { get; set; }
        public double OriginColumn { get; set; }

        private readonly CalibrationSourceCache _calibrationCache = new CalibrationSourceCache();

        public AffinePointTool(string moduleName) : base(moduleName)
        {
        }

        /// <summary>改用标定文件：来源切到文件并清空内嵌数据（二者互斥）。</summary>
        public void UseCalibrationFile(string path)
        {
            CalibrationSource = CalibrationSource.File;
            CalibrationFile = path;
            CalibrationData = null;
        }

        /// <summary>改用内嵌标定数据：来源切到内嵌并清空标定文件路径（二者互斥）。</summary>
        public void UseEmbeddedCalibration(string json)
        {
            CalibrationSource = CalibrationSource.Embedded;
            CalibrationData = json;
            CalibrationFile = null;
        }

        /// <summary>Affine2D 方式且引用了变换矩阵时直接用引用的矩阵，不读标定来源（现有行为）。</summary>
        private bool UsesMatrixReference => CalibrationKind == CalibrationKind.Affine2D && !string.IsNullOrWhiteSpace(MatrixPath);

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(MatrixPath):
                case "Matrix":
                    return CalibrationKind == CalibrationKind.Affine2D;
                case nameof(CalibrationFile):
                    return CalibrationSource == CalibrationSource.File;
                case nameof(CalibrationData):
                    // 内嵌数据是整段 JSON，只在编辑窗口中生成与查看
                    return false;
                default:
                    return true;
            }
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            List<ToolConfigurationIssue> issues = StructuralIssues();
            if (issues.Count > 0 || UsesMatrixReference)
            {
                return issues;
            }
            // 文件缺失不在校验阶段报（沿用现有行为，文件可能在部署时才放置），由预热与运行报告；文件存在时检查格式与类型
            bool readable = CalibrationSource == CalibrationSource.Embedded
                || (!string.IsNullOrWhiteSpace(CalibrationFile) && System.IO.File.Exists(CalibrationFile));
            if (readable)
            {
                try
                {
                    CheckKind(LoadCalibration(), out string kindError);
                    if (kindError != null)
                    {
                        issues.Add(new ToolConfigurationIssue(nameof(CalibrationKind), kindError));
                    }
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is System.IO.InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    issues.Add(new ToolConfigurationIssue(CalibrationSource == CalibrationSource.Embedded ? nameof(CalibrationData) : nameof(CalibrationFile), ex.Message));
                }
            }
            return issues;
        }

        /// <summary>不需要读取标定内容就能判断的配置问题（来源互斥、方式与输入不配套、内嵌数据为空），运行时同样拒绝。</summary>
        private List<ToolConfigurationIssue> StructuralIssues()
        {
            var issues = new List<ToolConfigurationIssue>();
            if (CalibrationKind == CalibrationKind.Camera && !string.IsNullOrWhiteSpace(MatrixPath))
            {
                issues.Add(new ToolConfigurationIssue(nameof(MatrixPath), "Camera 方式使用标定结果中的相机参数，不使用变换矩阵引用，请清空“变换矩阵”"));
            }
            CalibrationSourceCache.AddExclusivityIssues(issues, CalibrationSource, CalibrationFile, CalibrationData,
                UsesMatrixReference ? null : "请在编辑窗口的 N 点标定页计算后“内嵌到当前工具”");
            return issues;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = StructuralIssues().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            List<double> rows, columns, angles = null;
            try
            {
                rows = RegionDistanceTool.ReadNumbers(ctx, RowPath, "Row ");
                columns = RegionDistanceTool.ReadNumbers(ctx, ColumnPath, "Column ");
                if (!string.IsNullOrWhiteSpace(AnglePath))
                {
                    angles = RegionDistanceTool.ReadNumbers(ctx, AnglePath, "角度 ");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException)
            {
                return NodeResult.Fail($"{ModuleName} 输入引用无效：{ex.Message}");
            }
            if (!PairingHelper.TryGetPairCount(rows.Count, columns.Count, out int count, out string pairError))
            {
                return NodeResult.Fail($"{ModuleName} Row 与 Column：{pairError}");
            }
            int pointCount = count;
            if (angles != null && !PairingHelper.TryGetPairCount(pointCount, angles.Count, out count, out pairError))
            {
                return NodeResult.Fail($"{ModuleName} 坐标点与角度：{pairError}");
            }

            var worldRows = new double[count];
            var worldColumns = new double[count];
            var worldAngles = angles == null ? new double[0] : new double[count];
            HomMat2D matrix = null;
            try
            {
                if (CalibrationKind == CalibrationKind.Affine2D)
                {
                    if (!TryResolveMatrix(ctx, out matrix, out string error))
                    {
                        return NodeResult.Fail(error);
                    }
                    for (int i = 0; i < count; i++)
                    {
                        // 坐标点一侧只有 1 个时 rows / columns 都只有 1 个，SideIndex 自然取 0
                        double row = rows[PairingHelper.SideIndex(i, rows.Count)];
                        double column = columns[PairingHelper.SideIndex(i, columns.Count)];
                        if (angles != null)
                        {
                            matrix.TransformPose(row, column, angles[PairingHelper.SideIndex(i, angles.Count)], out worldRows[i], out worldColumns[i], out worldAngles[i]);
                        }
                        else
                        {
                            matrix.TransformPoint(row, column, out worldRows[i], out worldColumns[i]);
                        }
                    }
                }
                else
                {
                    if (!TryTransformCamera(rows, columns, angles, count, worldRows, worldColumns, worldAngles, out string error))
                    {
                        return NodeResult.Fail(error);
                    }
                }
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 坐标转换失败：{ex.Message}");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException)
            {
                return NodeResult.Fail($"{ModuleName} 变换矩阵引用无效：{MatrixPath}，{ex.Message}");
            }

            for (int i = 0; i < count; i++)
            {
                worldRows[i] -= OriginRow;
                worldColumns[i] -= OriginColumn;
            }
            double worldAngle = worldAngles.Length > 0 ? worldAngles[0] : double.NaN;
            SetOutput(ctx, Variable.Single(ModuleName, "WorldRow", VariableType.Double, count > 0 ? worldRows[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "WorldColumn", VariableType.Double, count > 0 ? worldColumns[0] : double.NaN));
            SetOutput(ctx, Variable.Array(ModuleName, "WorldRows", VariableType.Double, worldRows));
            SetOutput(ctx, Variable.Array(ModuleName, "WorldColumns", VariableType.Double, worldColumns));
            SetOutput(ctx, Variable.Single(ModuleName, "WorldAngle", VariableType.Double, worldAngle));
            SetOutput(ctx, Variable.Array(ModuleName, "WorldAngles", VariableType.Double, worldAngles));
            SetOutput(ctx, Variable.Single(ModuleName, "WorldAngleDeg", VariableType.Double, AngleMath.ToDegrees(worldAngle)));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count));
            if (matrix != null)
            {
                SetBorrowedOutput(ctx, Variable.Object(ModuleName, "Matrix", matrix, 1));
            }
            if (count == 1)
            {
                ctx.AddLog(FlowLogLevel.Info, $"[坐标转换] ({rows[0]:F2},{columns[0]:F2}) -> ({worldRows[0]:F3},{worldColumns[0]:F3})");
            }
            else
            {
                ctx.AddLog(FlowLogLevel.Info, count == 0
                    ? "[坐标转换] 没有输入点"
                    : $"[坐标转换] {count} 个点，第一个 -> ({worldRows[0]:F3},{worldColumns[0]:F3})");
            }
            return NodeResult.Ok;
        }

        private bool TryResolveMatrix(FlowContext ctx, out HomMat2D matrix, out string error)
        {
            matrix = null;
            error = null;
            if (!string.IsNullOrWhiteSpace(MatrixPath))
            {
                matrix = VariableReference.Parse(MatrixPath).Resolve<HomMat2D>(ctx);
                return true;
            }
            if (!TryLoadForRun(out CalibrationResult calibration, out error))
            {
                return false;
            }
            matrix = new HomMat2D(new HTuple(calibration.Affine2D.HomMat2D));
            return true;
        }

        private bool TryTransformCamera(List<double> rows, List<double> columns, List<double> angles, int count,
            double[] worldRows, double[] worldColumns, double[] worldAngles, out string error)
        {
            if (!TryLoadForRun(out CalibrationResult calibration, out error))
            {
                return false;
            }
            if (!CalibrationService.TryGetWorldPlaneScale(calibration.Unit, out string scale))
            {
                error = $"{ModuleName} 标定数据的单位“{calibration.Unit}”不受支持，Camera 方式只支持 m / cm / mm / um";
                return false;
            }
            if (count == 0)
            {
                return true;
            }
            var pointRows = new double[count];
            var pointColumns = new double[count];
            for (int i = 0; i < count; i++)
            {
                pointRows[i] = rows[PairingHelper.SideIndex(i, rows.Count)];
                pointColumns[i] = columns[PairingHelper.SideIndex(i, columns.Count)];
            }
            HTuple camParam = calibration.Camera.ToHalconCamParam();
            HTuple pose = calibration.Camera.ToHalconPose();
            HOperatorSet.ImagePointsToWorldPlane(camParam, pose, pointRows, pointColumns, scale, out HTuple x, out HTuple y);
            double[] xs = x.ToDArr();
            double[] ys = y.ToDArr();
            Array.Copy(xs, worldRows, count);
            Array.Copy(ys, worldColumns, count);
            if (angles != null)
            {
                // 沿角度方向 1 像素处的第二点分别换算，由两点的物理方向求角度
                var secondRows = new double[count];
                var secondColumns = new double[count];
                for (int i = 0; i < count; i++)
                {
                    double phi = angles[PairingHelper.SideIndex(i, angles.Count)];
                    secondRows[i] = pointRows[i] - Math.Sin(phi);
                    secondColumns[i] = pointColumns[i] + Math.Cos(phi);
                }
                HOperatorSet.ImagePointsToWorldPlane(camParam, pose, secondRows, secondColumns, scale, out HTuple x2, out HTuple y2);
                for (int i = 0; i < count; i++)
                {
                    worldAngles[i] = HomMat2D.DirectionToPhi(x2[i].D - xs[i], y2[i].D - ys[i]);
                }
            }
            return true;
        }

        /// <summary>运行时读取标定来源；缺失、格式错误或类型不符时返回 false 与中文错误。</summary>
        private bool TryLoadForRun(out CalibrationResult calibration, out string error)
        {
            calibration = null;
            error = null;
            if (CalibrationSource == CalibrationSource.File)
            {
                if (string.IsNullOrWhiteSpace(CalibrationFile))
                {
                    error = CalibrationKind == CalibrationKind.Affine2D ? "未配置变换矩阵引用或 CalibrationFile" : "未配置 CalibrationFile";
                    return false;
                }
                if (!System.IO.File.Exists(CalibrationFile))
                {
                    error = $"标定文件不存在：{CalibrationFile}";
                    return false;
                }
            }
            try
            {
                calibration = LoadCalibration();
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is System.IO.InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                error = $"{ModuleName} {ex.Message}";
                return false;
            }
            CheckKind(calibration, out error);
            if (error != null)
            {
                error = $"{ModuleName} {error}";
                return false;
            }
            return true;
        }

        private string SourceName => CalibrationSourceCache.SourceName(CalibrationSource, CalibrationFile);

        /// <summary>标定内容是否带当前方式需要的数据；不符时给出“来源 + 实际类型 + 期望类型”。</summary>
        private void CheckKind(CalibrationResult calibration, out string error)
        {
            bool ok = CalibrationKind == CalibrationKind.Camera ? calibration.Camera != null : calibration.Affine2D != null;
            error = ok ? null
                : calibration.IsLegacyTuple
                    ? $"{SourceName} 是旧版 write_tuple 矩阵文件（只有 Affine2D 矩阵），{CalibrationKind} 方式需要包含 {CalibrationKind} 数据的 .vfcal.json"
                    : $"{SourceName} 的类型为 {calibration.Kind}（包含 {calibration.SectionsText()}），{CalibrationKind} 方式需要 {CalibrationKind} 数据";
        }

        /// <summary>读取标定来源（缓存规则见 <see cref="CalibrationSourceCache"/>）。</summary>
        private CalibrationResult LoadCalibration()
        {
            return _calibrationCache.Load(CalibrationSource, CalibrationFile, CalibrationData);
        }

        /// <summary>预热：使用标定文件或内嵌数据（未引用变换矩阵）时预先读取；文件缺失、格式错误、类型不符时抛出，便于投产前发现。</summary>
        public void Prepare()
        {
            if (UsesMatrixReference)
            {
                return;
            }
            if (CalibrationSource == CalibrationSource.File)
            {
                if (string.IsNullOrWhiteSpace(CalibrationFile))
                {
                    return;
                }
                if (!System.IO.File.Exists(CalibrationFile))
                {
                    throw new System.IO.FileNotFoundException($"标定文件不存在：{CalibrationFile}", CalibrationFile);
                }
            }
            else if (string.IsNullOrWhiteSpace(CalibrationData))
            {
                return;
            }
            CheckKind(LoadCalibration(), out string error);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }
        }

        public void ReleaseResources()
        {
            _calibrationCache.Release();
        }
    }
}
