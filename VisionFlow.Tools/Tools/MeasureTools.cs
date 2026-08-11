using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    public sealed class LineMeasureResult
    {
        public int Index { get; set; }
        public double Row1 { get; set; }
        public double Column1 { get; set; }
        public double Row2 { get; set; }
        public double Column2 { get; set; }
        public bool Followed { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")} P1=({Row1:F2},{Column1:F2}), P2=({Row2:F2},{Column2:F2})";
        }
    }

    public sealed class RectangleMeasureResult
    {
        public int Index { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Phi { get; set; }
        public double Length1 { get; set; }
        public double Length2 { get; set; }
        public bool Followed { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")} 中心=({Row:F2},{Column:F2}), Phi={Phi:F4}, Len1={Length1:F2}, Len2={Length2:F2}";
        }
    }

    public sealed class CircleMeasureResult
    {
        public int Index { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Radius { get; set; }
        public bool Followed { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")} 圆心=({Row:F2},{Column:F2}), R={Radius:F2}";
        }
    }

    public sealed class OneDCaliperMeasureResult
    {
        public int Index { get; set; }
        public int EdgeIndex { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Amplitude { get; set; }
        public double Distance { get; set; }
        public bool Followed { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")}.{EdgeIndex} Edge=({Row:F2},{Column:F2}), Amp={Amplitude:F2}, Dist={Distance:F2}";
        }
    }

    public abstract class FollowMeasureToolBase : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("变换矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        [InputRef("结果序号", typeof(int), Optional = true)]
        public string IndexPath { get; set; }

        public double MeasureLength1 { get; set; } = 10;
        public double MeasureLength2 { get; set; } = 3;
        public double MeasureSigma { get; set; } = 1;
        public double MeasureThreshold { get; set; } = 20;
        public string MeasureTransition { get; set; } = "all";
        public string MeasureSelect { get; set; } = "all";

        protected FollowMeasureToolBase(string moduleName) : base(moduleName)
        {
        }

        protected HObject GetImage(FlowContext ctx)
        {
            return Input<HalconImage>(ctx, ImagePath).Object;
        }

        protected int GetIndex(FlowContext ctx)
        {
            if (string.IsNullOrWhiteSpace(IndexPath))
            {
                return -1;
            }
            return VariableReference.Parse(IndexPath).Resolve<int>(ctx);
        }

        protected HomMat2D GetMatrixOrNull(FlowContext ctx)
        {
            if (string.IsNullOrWhiteSpace(MatrixPath))
            {
                return null;
            }

            try
            {
                return VariableReference.Parse(MatrixPath).Resolve<HomMat2D>(ctx);
            }
            catch (Exception ex)
            {
                ctx.AddLog(FlowLogLevel.Warning, $"[测量] 矩阵引用 '{MatrixPath}' 解析失败（{ex.Message}），按固定位置测量");
                return null;
            }
        }

        protected List<HomMat2D> GetMatrices(FlowContext ctx)
        {
            var matrices = new List<HomMat2D>();
            if (string.IsNullOrWhiteSpace(MatrixPath))
            {
                matrices.Add(null);
                return matrices;
            }

            try
            {
                object value = VariableReference.Parse(MatrixPath).Resolve(ctx);
                if (value is HomMat2D single)
                {
                    matrices.Add(single);
                }
                else if (value is IEnumerable<HomMat2D> many)
                {
                    matrices.AddRange(many);
                }
                else
                {
                    ctx.AddLog(FlowLogLevel.Warning, $"[测量] 矩阵引用 '{MatrixPath}' 不是 HomMat2D 或 HomMat2D 集合，按固定位置测量");
                    matrices.Add(null);
                }
            }
            catch (Exception ex)
            {
                ctx.AddLog(FlowLogLevel.Warning, $"[测量] 矩阵引用 '{MatrixPath}' 解析失败（{ex.Message}），按固定位置测量");
                matrices.Add(null);
            }

            if (matrices.Count == 0)
            {
                matrices.Add(null);
            }
            return matrices;
        }

        protected static void AddResult<T>(FlowContext ctx, string moduleName, T result)
        {
            List<T> results;
            if (ctx.TryGetVariable(moduleName, "Results", out Variable existing))
            {
                results = existing.GetValue<List<T>>();
            }
            else
            {
                results = new List<T>();
                ctx.SetVariable(Variable.Object(moduleName, "Results", results, 0));
            }
            results.Add(result);
        }

        protected void AddMetrologyParams(out HTuple names, out HTuple values)
        {
            names = new HTuple("measure_transition").TupleConcat("measure_select");
            values = new HTuple(MeasureTransition).TupleConcat(MeasureSelect);
        }
    }

    [ToolOutput("Row1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Row2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<LineMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    public sealed class LineFollowMeasureTool : FollowMeasureToolBase
    {
        public double BaseRow1 { get; set; } = 100;
        public double BaseColumn1 { get; set; } = 100;
        public double BaseRow2 { get; set; } = 100;
        public double BaseColumn2 { get; set; } = 250;

        public LineFollowMeasureTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = GetImage(ctx);
            HOperatorSet.GenEmptyObj(out HObject allContours);
            int successCount = 0;
            string lastError = null;

            foreach (HomMat2D matrix in GetMatrices(ctx))
            {
                NodeResult result = MeasureOne(ctx, image, matrix, successCount, allContours, out HObject newContours);
                if (!ReferenceEquals(allContours, newContours))
                {
                    allContours.Dispose();
                    allContours = newContours;
                }
                if (result.IsSuccess)
                {
                    successCount++;
                }
                else
                {
                    lastError = result.Message;
                }
            }

            SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", allContours, successCount));
            return successCount > 0 ? NodeResult.Ok : NodeResult.Fail(lastError ?? "直线测量失败：未找到有效结果");
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, HObject allContours, out HObject newContours)
        {
            newContours = allContours;
            double row1 = BaseRow1;
            double col1 = BaseColumn1;
            double row2 = BaseRow2;
            double col2 = BaseColumn2;
            bool followed = false;
            if (matrix != null)
            {
                matrix.TransformPoint(BaseRow1, BaseColumn1, out row1, out col1);
                matrix.TransformPoint(BaseRow2, BaseColumn2, out row2, out col2);
                followed = true;
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            try
            {
                HOperatorSet.SetMetrologyModelImageSize(metrology, width, height);
                AddMetrologyParams(out HTuple names, out HTuple values);
                HOperatorSet.AddMetrologyObjectLineMeasure(metrology, row1, col1, row2, col2,
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold, names, values, out HTuple _);
                HOperatorSet.ApplyMetrologyModel(image, metrology);
                HOperatorSet.GetMetrologyObjectResult(metrology, 0, "all", "result_type", "all_param", out HTuple param);
                if (param.Length < 4)
                {
                    return NodeResult.Fail($"直线测量失败（基准 P1=({row1:F1},{col1:F1}), P2=({row2:F1},{col2:F1})）：未找到足够的边缘点");
                }

                var result = new LineMeasureResult
                {
                    Index = string.IsNullOrWhiteSpace(IndexPath) ? resultIndex : GetIndex(ctx),
                    Row1 = param[0].D,
                    Column1 = param[1].D,
                    Row2 = param[2].D,
                    Column2 = param[3].D,
                    Followed = followed
                };
                HOperatorSet.GetMetrologyObjectResultContour(out HObject contour, metrology, "all", "all", 1.5);
                HOperatorSet.ConcatObj(allContours, contour, out newContours);
                contour.Dispose();
                SetOutput(ctx, Variable.Single(ModuleName, "Row1", VariableType.Double, result.Row1));
                SetOutput(ctx, Variable.Single(ModuleName, "Column1", VariableType.Double, result.Column1));
                SetOutput(ctx, Variable.Single(ModuleName, "Row2", VariableType.Double, result.Row2));
                SetOutput(ctx, Variable.Single(ModuleName, "Column2", VariableType.Double, result.Column2));
                AddResult(ctx, ModuleName, result);
                ctx.AddLog(FlowLogLevel.Info, $"[直线测量]{(followed ? "跟随" : "固定")} {result}");
                return NodeResult.Ok;
            }
            finally
            {
                HOperatorSet.ClearMetrologyModel(metrology);
            }
        }
    }

    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Phi", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Length1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Length2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<RectangleMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    public sealed class RectangleFollowMeasureTool : FollowMeasureToolBase
    {
        public double BaseRow { get; set; } = 100;
        public double BaseColumn { get; set; } = 150;
        public double BasePhi { get; set; }
        public double BaseLength1 { get; set; } = 80;
        public double BaseLength2 { get; set; } = 40;

        public RectangleFollowMeasureTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = GetImage(ctx);
            HOperatorSet.GenEmptyObj(out HObject allContours);
            int successCount = 0;
            string lastError = null;

            foreach (HomMat2D matrix in GetMatrices(ctx))
            {
                NodeResult result = MeasureOne(ctx, image, matrix, successCount, allContours, out HObject newContours);
                if (!ReferenceEquals(allContours, newContours))
                {
                    allContours.Dispose();
                    allContours = newContours;
                }
                if (result.IsSuccess)
                {
                    successCount++;
                }
                else
                {
                    lastError = result.Message;
                }
            }

            SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", allContours, successCount));
            return successCount > 0 ? NodeResult.Ok : NodeResult.Fail(lastError ?? "矩形测量失败：未找到有效结果");
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, HObject allContours, out HObject newContours)
        {
            newContours = allContours;
            double row = BaseRow;
            double col = BaseColumn;
            double phi = BasePhi;
            bool followed = false;
            if (matrix != null)
            {
                matrix.TransformPose(BaseRow, BaseColumn, BasePhi, out row, out col, out phi);
                followed = true;
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            try
            {
                HOperatorSet.SetMetrologyModelImageSize(metrology, width, height);
                AddMetrologyParams(out HTuple names, out HTuple values);
                HOperatorSet.AddMetrologyObjectRectangle2Measure(metrology, row, col, phi, BaseLength1, BaseLength2,
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold, names, values, out HTuple _);
                HOperatorSet.ApplyMetrologyModel(image, metrology);
                HOperatorSet.GetMetrologyObjectResult(metrology, 0, "all", "result_type", "all_param", out HTuple param);
                if (param.Length < 5)
                {
                    return NodeResult.Fail($"矩形测量失败（基准中心 {row:F1},{col:F1}）：未找到足够的边缘点");
                }

                var result = new RectangleMeasureResult
                {
                    Index = string.IsNullOrWhiteSpace(IndexPath) ? resultIndex : GetIndex(ctx),
                    Row = param[0].D,
                    Column = param[1].D,
                    Phi = param[2].D,
                    Length1 = param[3].D,
                    Length2 = param[4].D,
                    Followed = followed
                };
                HOperatorSet.GetMetrologyObjectResultContour(out HObject contour, metrology, "all", "all", 1.5);
                HOperatorSet.ConcatObj(allContours, contour, out newContours);
                contour.Dispose();
                SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, result.Row));
                SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, result.Column));
                SetOutput(ctx, Variable.Single(ModuleName, "Phi", VariableType.Double, result.Phi));
                SetOutput(ctx, Variable.Single(ModuleName, "Length1", VariableType.Double, result.Length1));
                SetOutput(ctx, Variable.Single(ModuleName, "Length2", VariableType.Double, result.Length2));
                AddResult(ctx, ModuleName, result);
                ctx.AddLog(FlowLogLevel.Info, $"[矩形测量]{(followed ? "跟随" : "固定")} {result}");
                return NodeResult.Ok;
            }
            finally
            {
                HOperatorSet.ClearMetrologyModel(metrology);
            }
        }
    }

    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Radius", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<CircleMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    public sealed class CircleFollowMeasureTool : FollowMeasureToolBase
    {
        public double BaseRow { get; set; } = 100;
        public double BaseColumn { get; set; } = 150;
        public double BaseRadius { get; set; } = 50;
        public double StartPhi { get; set; } = 0;
        public double EndPhi { get; set; } = Math.PI * 2;

        public CircleFollowMeasureTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = GetImage(ctx);
            HOperatorSet.GenEmptyObj(out HObject allContours);
            int successCount = 0;
            string lastError = null;

            foreach (HomMat2D matrix in GetMatrices(ctx))
            {
                NodeResult result = MeasureOne(ctx, image, matrix, successCount, allContours, out HObject newContours);
                if (!ReferenceEquals(allContours, newContours))
                {
                    allContours.Dispose();
                    allContours = newContours;
                }
                if (result.IsSuccess)
                {
                    successCount++;
                }
                else
                {
                    lastError = result.Message;
                }
            }

            SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", allContours, successCount));
            return successCount > 0 ? NodeResult.Ok : NodeResult.Fail(lastError ?? "圆测量失败：未找到有效结果");
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, HObject allContours, out HObject newContours)
        {
            newContours = allContours;
            double row = BaseRow;
            double col = BaseColumn;
            bool followed = false;
            if (matrix != null)
            {
                matrix.TransformPoint(BaseRow, BaseColumn, out row, out col);
                followed = true;
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            try
            {
                HOperatorSet.SetMetrologyModelImageSize(metrology, width, height);
                AddMetrologyParams(out HTuple names, out HTuple values);
                names = names.TupleConcat("start_phi").TupleConcat("end_phi");
                values = values.TupleConcat(StartPhi).TupleConcat(EndPhi);
                HOperatorSet.AddMetrologyObjectCircleMeasure(metrology, row, col, BaseRadius,
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold, names, values, out HTuple _);
                HOperatorSet.ApplyMetrologyModel(image, metrology);
                HOperatorSet.GetMetrologyObjectResult(metrology, 0, "all", "result_type", "all_param", out HTuple param);
                if (param.Length < 3)
                {
                    return NodeResult.Fail($"圆测量失败（基准圆心 {row:F1},{col:F1}, R={BaseRadius:F1}）：未找到足够的边缘点");
                }

                var result = new CircleMeasureResult
                {
                    Index = string.IsNullOrWhiteSpace(IndexPath) ? resultIndex : GetIndex(ctx),
                    Row = param[0].D,
                    Column = param[1].D,
                    Radius = param[2].D,
                    Followed = followed
                };
                HOperatorSet.GetMetrologyObjectResultContour(out HObject contour, metrology, "all", "all", 1.5);
                HOperatorSet.ConcatObj(allContours, contour, out newContours);
                contour.Dispose();
                SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, result.Row));
                SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, result.Column));
                SetOutput(ctx, Variable.Single(ModuleName, "Radius", VariableType.Double, result.Radius));
                AddResult(ctx, ModuleName, result);
                ctx.AddLog(FlowLogLevel.Info, $"[圆测量]{(followed ? "跟随" : "固定")} {result}");
                return NodeResult.Ok;
            }
            finally
            {
                HOperatorSet.ClearMetrologyModel(metrology);
            }
        }
    }

    [ToolOutput("Rows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Columns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Amplitudes", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Distances", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstColumn", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstAmplitude", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstDistance", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<OneDCaliperMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    public sealed class OneDCaliperFollowMeasureTool : FollowMeasureToolBase
        {
            public double BaseRow { get; set; } = 100;
            public double BaseColumn { get; set; } = 150;
            public double BasePhi { get; set; }
            public double BaseLength1 { get; set; } = 80;
            public double BaseLength2 { get; set; } = 8;

            public OneDCaliperFollowMeasureTool(string moduleName) : base(moduleName)
            {
            }

            public override NodeResult Run(FlowContext ctx)
            {
                HObject image = GetImage(ctx);
                HOperatorSet.GenEmptyObj(out HObject allContours);
                var allRows = new List<double>();
                var allColumns = new List<double>();
                var allAmplitudes = new List<double>();
                var allDistances = new List<double>();
                int successCount = 0;
                string lastError = null;

                foreach (HomMat2D matrix in GetMatrices(ctx))
                {
                    NodeResult result = MeasureOne(ctx, image, matrix, successCount, allContours,
                        allRows, allColumns, allAmplitudes, allDistances, out HObject newContours);
                    if (!ReferenceEquals(allContours, newContours))
                    {
                        allContours.Dispose();
                        allContours = newContours;
                    }
                    if (result.IsSuccess)
                    {
                        successCount++;
                    }
                    else
                    {
                        lastError = result.Message;
                    }
                }

                SetOutput(ctx, Variable.Array(ModuleName, "Rows", VariableType.Double, allRows));
                SetOutput(ctx, Variable.Array(ModuleName, "Columns", VariableType.Double, allColumns));
                SetOutput(ctx, Variable.Array(ModuleName, "Amplitudes", VariableType.Double, allAmplitudes));
                SetOutput(ctx, Variable.Array(ModuleName, "Distances", VariableType.Double, allDistances));
                SetOutput(ctx, Variable.Single(ModuleName, "FirstRow", VariableType.Double, allRows.Count > 0 ? allRows[0] : 0));
                SetOutput(ctx, Variable.Single(ModuleName, "FirstColumn", VariableType.Double, allColumns.Count > 0 ? allColumns[0] : 0));
                SetOutput(ctx, Variable.Single(ModuleName, "FirstAmplitude", VariableType.Double, allAmplitudes.Count > 0 ? allAmplitudes[0] : 0));
                SetOutput(ctx, Variable.Single(ModuleName, "FirstDistance", VariableType.Double, allDistances.Count > 0 ? allDistances[0] : 0));
                SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, allRows.Count));
                SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", allContours, allRows.Count));
                return allRows.Count > 0 ? NodeResult.Ok : NodeResult.Fail(lastError ?? "一维卡尺测量失败：未找到边缘");
            }

            private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, HObject allContours,
                List<double> allRows, List<double> allColumns, List<double> allAmplitudes, List<double> allDistances,
                out HObject newContours)
            {
                newContours = allContours;
                double row = BaseRow;
                double col = BaseColumn;
                double phi = BasePhi;
                bool followed = false;
                if (matrix != null)
                {
                    matrix.TransformPose(BaseRow, BaseColumn, BasePhi, out row, out col, out phi);
                    followed = true;
                }

                HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
                HOperatorSet.GenMeasureRectangle2(row, col, phi, BaseLength1, BaseLength2,
                    width, height, "nearest_neighbor", out HTuple measureHandle);
                try
                {
                    HOperatorSet.MeasurePos(image, measureHandle, MeasureSigma, MeasureThreshold,
                        MeasureTransition, MeasureSelect, out HTuple rows, out HTuple columns, out HTuple amplitudes, out HTuple distances);
                    if (rows.Length == 0)
                    {
                        return NodeResult.Fail($"一维卡尺测量失败（中心 {row:F1},{col:F1}）：未找到边缘");
                    }

                    int index = string.IsNullOrWhiteSpace(IndexPath) ? resultIndex : GetIndex(ctx);
                    for (int i = 0; i < rows.Length; i++)
                    {
                        double distance = i < distances.Length ? distances[i].D : 0;
                        var result = new OneDCaliperMeasureResult
                        {
                            Index = index,
                            EdgeIndex = i,
                            Row = rows[i].D,
                            Column = columns[i].D,
                            Amplitude = amplitudes[i].D,
                            Distance = distance,
                            Followed = followed
                        };
                        allRows.Add(result.Row);
                        allColumns.Add(result.Column);
                        allAmplitudes.Add(result.Amplitude);
                        allDistances.Add(result.Distance);
                        AddResult(ctx, ModuleName, result);
                    }

                    HObject contour = BuildResultContour(row, col, phi, rows, columns);
                    HOperatorSet.ConcatObj(allContours, contour, out newContours);
                    contour.Dispose();
                    ctx.AddLog(FlowLogLevel.Info, $"[一维卡尺]{(followed ? "跟随" : "固定")} 找到边缘数={rows.Length}");
                    return NodeResult.Ok;
                }
                finally
                {
                    HOperatorSet.CloseMeasure(measureHandle);
                }
            }

            private HObject BuildResultContour(double row, double col, double phi, HTuple rows, HTuple columns)
            {
                HOperatorSet.GenRectangle2(out HObject rectangle, row, col, phi, BaseLength1, BaseLength2);
                HObject crosses = null;
                if (rows.Length > 0)
                {
                    HOperatorSet.GenCrossContourXld(out crosses, rows, columns, 12, phi);
                }
                else
                {
                    HOperatorSet.GenEmptyObj(out crosses);
                }
                HOperatorSet.ConcatObj(rectangle, crosses, out HObject contour);
                rectangle.Dispose();
                crosses.Dispose();
                return contour;
            }
        }

    [ToolOutput("Rows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Columns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Amplitudes", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Distances", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstRow", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstColumn", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstAmplitude", VariableKind.Single, VariableType.Double)]
    [ToolOutput("FirstDistance", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<OneDCaliperMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    public sealed class ArcCaliperFollowMeasureTool : FollowMeasureToolBase
    {
        public double BaseRow { get; set; } = 100;
        public double BaseColumn { get; set; } = 150;
        public double BaseRadius { get; set; } = 50;
        public double StartPhi { get; set; } = 0;
        public double EndPhi { get; set; } = Math.PI * 2;
        public int CaliperCount { get; set; } = 16;

        public ArcCaliperFollowMeasureTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = GetImage(ctx);
            HOperatorSet.GenEmptyObj(out HObject allContours);
            var allRows = new List<double>();
            var allColumns = new List<double>();
            var allAmplitudes = new List<double>();
            var allDistances = new List<double>();
            int successCount = 0;
            string lastError = null;

            foreach (HomMat2D matrix in GetMatrices(ctx))
            {
                NodeResult result = MeasureOne(ctx, image, matrix, successCount, allContours,
                    allRows, allColumns, allAmplitudes, allDistances, out HObject newContours);
                if (!ReferenceEquals(allContours, newContours))
                {
                    allContours.Dispose();
                    allContours = newContours;
                }
                if (result.IsSuccess)
                {
                    successCount++;
                }
                else
                {
                    lastError = result.Message;
                }
            }

            SetOutput(ctx, Variable.Array(ModuleName, "Rows", VariableType.Double, allRows));
            SetOutput(ctx, Variable.Array(ModuleName, "Columns", VariableType.Double, allColumns));
            SetOutput(ctx, Variable.Array(ModuleName, "Amplitudes", VariableType.Double, allAmplitudes));
            SetOutput(ctx, Variable.Array(ModuleName, "Distances", VariableType.Double, allDistances));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstRow", VariableType.Double, allRows.Count > 0 ? allRows[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstColumn", VariableType.Double, allColumns.Count > 0 ? allColumns[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstAmplitude", VariableType.Double, allAmplitudes.Count > 0 ? allAmplitudes[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstDistance", VariableType.Double, allDistances.Count > 0 ? allDistances[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, allRows.Count));
            SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", allContours, allRows.Count));
            return allRows.Count > 0 ? NodeResult.Ok : NodeResult.Fail(lastError ?? "一维圆弧卡尺测量失败：未找到边缘");
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, HObject allContours,
            List<double> allRows, List<double> allColumns, List<double> allAmplitudes, List<double> allDistances,
            out HObject newContours)
        {
            newContours = allContours;
            double centerRow = BaseRow;
            double centerCol = BaseColumn;
            double startPhi = StartPhi;
            double endPhi = EndPhi;
            bool followed = false;
            if (matrix != null)
            {
                matrix.TransformPoint(BaseRow, BaseColumn, out centerRow, out centerCol);
                matrix.TransformPoint(BaseRow + BaseRadius * Math.Sin(StartPhi), BaseColumn + BaseRadius * Math.Cos(StartPhi), out double startRow, out double startCol);
                matrix.TransformPoint(BaseRow + BaseRadius * Math.Sin(EndPhi), BaseColumn + BaseRadius * Math.Cos(EndPhi), out double endRow, out double endCol);
                startPhi = Math.Atan2(startRow - centerRow, startCol - centerCol);
                endPhi = Math.Atan2(endRow - centerRow, endCol - centerCol);
                followed = true;
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.GenEmptyObj(out HObject localContours);
            int found = 0;
            int index = string.IsNullOrWhiteSpace(IndexPath) ? resultIndex : GetIndex(ctx);
            int caliperCount = Math.Max(1, CaliperCount);
            try
            {
                for (int i = 0; i < caliperCount; i++)
                {
                    double t = caliperCount == 1 ? 0.5 : (double)i / (caliperCount - 1);
                    double phi = startPhi + (endPhi - startPhi) * t;
                    double row = centerRow + BaseRadius * Math.Sin(phi);
                    double col = centerCol + BaseRadius * Math.Cos(phi);
                    HOperatorSet.GenMeasureRectangle2(row, col, phi, MeasureLength1, MeasureLength2,
                        width, height, "nearest_neighbor", out HTuple measureHandle);
                    try
                    {
                        HOperatorSet.MeasurePos(image, measureHandle, MeasureSigma, MeasureThreshold,
                            MeasureTransition, MeasureSelect, out HTuple rows, out HTuple columns, out HTuple amplitudes, out HTuple distances);
                        if (rows.Length == 0)
                        {
                            continue;
                        }

                        for (int edge = 0; edge < rows.Length; edge++)
                        {
                            double distance = edge < distances.Length ? distances[edge].D : 0;
                            var result = new OneDCaliperMeasureResult
                            {
                                Index = index,
                                EdgeIndex = found,
                                Row = rows[edge].D,
                                Column = columns[edge].D,
                                Amplitude = amplitudes[edge].D,
                                Distance = distance,
                                Followed = followed
                            };
                            allRows.Add(result.Row);
                            allColumns.Add(result.Column);
                            allAmplitudes.Add(result.Amplitude);
                            allDistances.Add(result.Distance);
                            AddResult(ctx, ModuleName, result);
                            found++;
                        }

                        HObject contour = BuildCaliperContour(row, col, phi, rows, columns);
                        HOperatorSet.ConcatObj(localContours, contour, out HObject combined);
                        localContours.Dispose();
                        contour.Dispose();
                        localContours = combined;
                    }
                    finally
                    {
                        HOperatorSet.CloseMeasure(measureHandle);
                    }
                }

                if (found == 0)
                {
                    return NodeResult.Fail($"一维圆弧卡尺测量失败（圆心 {centerRow:F1},{centerCol:F1}, R={BaseRadius:F1}）：未找到边缘");
                }

                HOperatorSet.ConcatObj(allContours, localContours, out newContours);
                ctx.AddLog(FlowLogLevel.Info, $"[一维圆弧卡尺]{(followed ? "跟随" : "固定")} 卡尺数={caliperCount}, 找到边缘数={found}");
                return NodeResult.Ok;
            }
            finally
            {
                localContours.Dispose();
            }
        }

        private HObject BuildCaliperContour(double row, double col, double phi, HTuple rows, HTuple columns)
        {
            HOperatorSet.GenRectangle2(out HObject rectangle, row, col, phi, MeasureLength1, MeasureLength2);
            HObject crosses = null;
            if (rows.Length > 0)
            {
                HOperatorSet.GenCrossContourXld(out crosses, rows, columns, 10, phi);
            }
            else
            {
                HOperatorSet.GenEmptyObj(out crosses);
            }
            HOperatorSet.ConcatObj(rectangle, crosses, out HObject contour);
            rectangle.Dispose();
            crosses.Dispose();
            return contour;
        }
    }
}
