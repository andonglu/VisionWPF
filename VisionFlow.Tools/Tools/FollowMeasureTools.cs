using System;
using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    [ToolOutput("Row1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Row2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("FailedCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<LineMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("InstanceRows1", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceColumns1", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceRows2", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceColumns2", VariableKind.Array, VariableType.Double)]
    public sealed class LineFollowMeasureTool : MetrologyMeasureToolBase
    {
        protected override string[] InstanceOutputNames
        {
            get { return new[] { "InstanceRows1", "InstanceColumns1", "InstanceRows2", "InstanceColumns2" }; }
        }

        public double BaseRow1 { get; set; } = 100;
        public double BaseColumn1 { get; set; } = 100;
        public double BaseRow2 { get; set; } = 100;
        public double BaseColumn2 { get; set; } = 250;

        public LineFollowMeasureTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            NodeResult invalid = CheckBeforeRun();
            if (invalid != null)
            {
                return invalid;
            }
            return RunMeasurements(ctx, "直线测量",
                (HObject image, HomMat2D matrix, int resultIndex, out HObject contour) =>
                    MeasureOne(ctx, image, matrix, resultIndex, out contour),
                successCount => SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, successCount)));
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, out HObject contour)
        {
            contour = null;
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
                HTuple param = ApplyMetrology(image, metrology, out HObject resultContour);
                if (param.Length < 4)
                {
                    resultContour.Dispose();
                    return NodeResult.Fail($"直线测量失败（基准 P1=({row1:F1},{col1:F1}), P2=({row2:F1},{col2:F1})）：未找到足够的边缘点");
                }

                contour = resultContour;
                var result = new LineMeasureResult
                {
                    Index = ResolveIndex(ctx, resultIndex),
                    Row1 = param[0].D,
                    Column1 = param[1].D,
                    Row2 = param[2].D,
                    Column2 = param[3].D,
                    Followed = followed
                };
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
    [ToolOutput("Rows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Columns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Phis", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("FailedCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<RectangleMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("InstanceRows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceColumns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstancePhis", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceLengths1", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceLengths2", VariableKind.Array, VariableType.Double)]
    public sealed class RectangleFollowMeasureTool : MetrologyMeasureToolBase, IRegionSeededMeasureTool
    {
        protected override string[] InstanceOutputNames
        {
            get { return new[] { "InstanceRows", "InstanceColumns", "InstancePhis", "InstanceLengths1", "InstanceLengths2" }; }
        }

        public double BaseRow { get; set; } = 100;
        public double BaseColumn { get; set; } = 150;
        public double BasePhi { get; set; }
        public double BaseLength1 { get; set; } = 80;
        public double BaseLength2 { get; set; } = 40;

        /// <summary>
        /// 初始区域（可选）：配置后对其中每个对象取 smallest_rectangle2 作为初始矩形做亚像素测量，
        /// 不再使用示教基准与变换矩阵；结果序号为区域对象序号。
        /// </summary>
        [InputRef("初始区域", typeof(HalconRegion), Optional = true)]
        public string InitRegionPath { get; set; }

        public RectangleFollowMeasureTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            NodeResult invalid = CheckBeforeRun();
            if (invalid != null)
            {
                return invalid;
            }
            double[] rows = null;
            double[] columns = null;
            double[] phis = null;
            Action<int> writeSummary = successCount =>
            {
                SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, successCount));
                SetOutput(ctx, Variable.Array(ModuleName, "Rows", VariableType.Double, rows ?? NewSeedArray()));
                SetOutput(ctx, Variable.Array(ModuleName, "Columns", VariableType.Double, columns ?? NewSeedArray()));
                SetOutput(ctx, Variable.Array(ModuleName, "Phis", VariableType.Double, phis ?? NewSeedArray()));
            };
            Action<RectangleMeasureResult> collect = result =>
            {
                if (rows == null)
                {
                    rows = NewSeedArray();
                    columns = NewSeedArray();
                    phis = NewSeedArray();
                }
                rows[SeedIndex] = result.Row;
                columns[SeedIndex] = result.Column;
                phis[SeedIndex] = result.Phi;
            };
            if (!string.IsNullOrWhiteSpace(InitRegionPath))
            {
                return RunRegionMeasurements(ctx, "矩形测量", InitRegionPath,
                    (HObject image, HObject region, int resultIndex, out HObject contour) =>
                    {
                        HOperatorSet.SmallestRectangle2(region, out HTuple row, out HTuple col, out HTuple phi,
                            out HTuple length1, out HTuple length2);
                        return MeasureAt(ctx, image, row.D, col.D, phi.D, length1.D, length2.D,
                            resultIndex, false, "区域", collect, out contour);
                    },
                    writeSummary);
            }
            return RunMeasurements(ctx, "矩形测量",
                (HObject image, HomMat2D matrix, int resultIndex, out HObject contour) =>
                    MeasureOne(ctx, image, matrix, resultIndex, collect, out contour),
                writeSummary);
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex,
            Action<RectangleMeasureResult> collect, out HObject contour)
        {
            double row = BaseRow;
            double col = BaseColumn;
            double phi = BasePhi;
            double length1 = BaseLength1;
            double length2 = BaseLength2;
            bool followed = false;
            if (matrix != null)
            {
                matrix.TransformPose(BaseRow, BaseColumn, BasePhi, out row, out col, out phi);
                length1 *= matrix.ScaleFactor;
                length2 *= matrix.ScaleFactor;
                followed = true;
            }
            return MeasureAt(ctx, image, row, col, phi, length1, length2, resultIndex, followed,
                followed ? "跟随" : "固定", collect, out contour);
        }

        private NodeResult MeasureAt(FlowContext ctx, HObject image, double row, double col, double phi,
            double length1, double length2, int resultIndex, bool followed, string mode,
            Action<RectangleMeasureResult> collect, out HObject contour)
        {
            contour = null;
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            try
            {
                HOperatorSet.SetMetrologyModelImageSize(metrology, width, height);
                AddMetrologyParams(out HTuple names, out HTuple values);
                HOperatorSet.AddMetrologyObjectRectangle2Measure(metrology, row, col, phi, length1, length2,
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold, names, values, out HTuple _);
                HTuple param = ApplyMetrology(image, metrology, out HObject resultContour);
                if (param.Length < 5)
                {
                    resultContour.Dispose();
                    return NodeResult.Fail($"矩形测量失败（初始中心 {row:F1},{col:F1}）：未找到足够的边缘点");
                }

                contour = resultContour;
                var result = new RectangleMeasureResult
                {
                    Index = ResolveIndex(ctx, resultIndex),
                    Row = param[0].D,
                    Column = param[1].D,
                    Phi = param[2].D,
                    Length1 = param[3].D,
                    Length2 = param[4].D,
                    Followed = followed
                };
                SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, result.Row));
                SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, result.Column));
                SetOutput(ctx, Variable.Single(ModuleName, "Phi", VariableType.Double, result.Phi));
                SetOutput(ctx, Variable.Single(ModuleName, "Length1", VariableType.Double, result.Length1));
                SetOutput(ctx, Variable.Single(ModuleName, "Length2", VariableType.Double, result.Length2));
                AddResult(ctx, ModuleName, result);
                collect(result);
                ctx.AddLog(FlowLogLevel.Info, mode == "区域"
                    ? $"[矩形测量]区域对象 #{result.Index} 中心=({result.Row:F2},{result.Column:F2}), Phi={result.Phi:F4}, Len1={result.Length1:F2}, Len2={result.Length2:F2}"
                    : $"[矩形测量]{mode} {result}");
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
    [ToolOutput("Rows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Columns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Radii", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("FailedCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<CircleMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("InstanceRows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceColumns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceRadii", VariableKind.Array, VariableType.Double)]
    public sealed class CircleFollowMeasureTool : MetrologyMeasureToolBase, IRegionSeededMeasureTool
    {
        protected override string[] InstanceOutputNames
        {
            get { return new[] { "InstanceRows", "InstanceColumns", "InstanceRadii" }; }
        }

        public double BaseRow { get; set; } = 100;
        public double BaseColumn { get; set; } = 150;
        public double BaseRadius { get; set; } = 50;
        public double StartPhi { get; set; } = 0;
        public double EndPhi { get; set; } = Math.PI * 2;

        /// <summary>
        /// 初始区域（可选）：配置后对其中每个对象取 smallest_circle 作为初始圆做亚像素测量（扫描范围仍按 StartPhi/EndPhi），
        /// 不再使用示教基准与变换矩阵；结果序号为区域对象序号。
        /// </summary>
        [InputRef("初始区域", typeof(HalconRegion), Optional = true)]
        public string InitRegionPath { get; set; }

        public CircleFollowMeasureTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            NodeResult invalid = CheckBeforeRun();
            if (invalid != null)
            {
                return invalid;
            }
            double[] rows = null;
            double[] columns = null;
            double[] radii = null;
            Action<int> writeSummary = successCount =>
            {
                SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, successCount));
                SetOutput(ctx, Variable.Array(ModuleName, "Rows", VariableType.Double, rows ?? NewSeedArray()));
                SetOutput(ctx, Variable.Array(ModuleName, "Columns", VariableType.Double, columns ?? NewSeedArray()));
                SetOutput(ctx, Variable.Array(ModuleName, "Radii", VariableType.Double, radii ?? NewSeedArray()));
            };
            Action<CircleMeasureResult> collect = result =>
            {
                if (rows == null)
                {
                    rows = NewSeedArray();
                    columns = NewSeedArray();
                    radii = NewSeedArray();
                }
                rows[SeedIndex] = result.Row;
                columns[SeedIndex] = result.Column;
                radii[SeedIndex] = result.Radius;
            };
            if (!string.IsNullOrWhiteSpace(InitRegionPath))
            {
                return RunRegionMeasurements(ctx, "圆测量", InitRegionPath,
                    (HObject image, HObject region, int resultIndex, out HObject contour) =>
                    {
                        HOperatorSet.SmallestCircle(region, out HTuple row, out HTuple col, out HTuple radius);
                        return MeasureAt(ctx, image, row.D, col.D, radius.D, StartPhi, EndPhi,
                            resultIndex, false, "区域", collect, out contour);
                    },
                    writeSummary);
            }
            return RunMeasurements(ctx, "圆测量",
                (HObject image, HomMat2D matrix, int resultIndex, out HObject contour) =>
                    MeasureOne(ctx, image, matrix, resultIndex, collect, out contour),
                writeSummary);
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex,
            Action<CircleMeasureResult> collect, out HObject contour)
        {
            double row = BaseRow;
            double col = BaseColumn;
            double radius = BaseRadius;
            double startPhi = StartPhi;
            double endPhi = EndPhi;
            bool followed = false;
            if (matrix != null)
            {
                FollowArc(matrix, BaseRow, BaseColumn, BaseRadius, StartPhi, EndPhi,
                    out row, out col, out radius, out startPhi, out endPhi);
                followed = true;
            }
            return MeasureAt(ctx, image, row, col, radius, startPhi, endPhi, resultIndex, followed,
                followed ? "跟随" : "固定", collect, out contour);
        }

        private NodeResult MeasureAt(FlowContext ctx, HObject image, double row, double col, double radius,
            double startPhi, double endPhi, int resultIndex, bool followed, string mode,
            Action<CircleMeasureResult> collect, out HObject contour)
        {
            contour = null;
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            try
            {
                HOperatorSet.SetMetrologyModelImageSize(metrology, width, height);
                AddMetrologyParams(out HTuple names, out HTuple values);
                names = names.TupleConcat("start_phi").TupleConcat("end_phi");
                values = values.TupleConcat(startPhi).TupleConcat(endPhi);
                HOperatorSet.AddMetrologyObjectCircleMeasure(metrology, row, col, radius,
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold, names, values, out HTuple _);
                HTuple param = ApplyMetrology(image, metrology, out HObject resultContour);
                if (param.Length < 3)
                {
                    resultContour.Dispose();
                    return NodeResult.Fail($"圆测量失败（初始圆心 {row:F1},{col:F1}, R={radius:F1}）：未找到足够的边缘点");
                }

                contour = resultContour;
                var result = new CircleMeasureResult
                {
                    Index = ResolveIndex(ctx, resultIndex),
                    Row = param[0].D,
                    Column = param[1].D,
                    Radius = param[2].D,
                    Followed = followed
                };
                SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, result.Row));
                SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, result.Column));
                SetOutput(ctx, Variable.Single(ModuleName, "Radius", VariableType.Double, result.Radius));
                AddResult(ctx, ModuleName, result);
                collect(result);
                ctx.AddLog(FlowLogLevel.Info, mode == "区域"
                    ? $"[圆测量]区域对象 #{result.Index} 圆心=({result.Row:F2},{result.Column:F2}), R={result.Radius:F2}"
                    : $"[圆测量]{mode} {result}");
                return NodeResult.Ok;
            }
            finally
            {
                HOperatorSet.ClearMetrologyModel(metrology);
            }
        }
    }

    /// <summary>
    /// 椭圆测量工具：只持有自己的初始测量位置（图像坐标），与匹配工具完全解耦。
    /// MatrixPath 配置了变换矩阵引用时：按矩阵对初始位置做仿射变换后测量（跟随模式）；
    /// 未配置时：直接在初始位置测量（固定模式）；已配置但解析失败时仅显式允许的预览可降级。
    /// 测量结果累积到 "模块名.Results" 变量（List&lt;EllipseMeasureResult&gt;），循环结束后即为全部结果。
    /// </summary>
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Phi", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Length1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Length2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("FailedCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<EllipseMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("InstanceRows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceColumns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstancePhis", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceLengths1", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceLengths2", VariableKind.Array, VariableType.Double)]
    public sealed class EllipseFollowMeasureTool : MetrologyMeasureToolBase
    {
        protected override string[] InstanceOutputNames
        {
            get { return new[] { "InstanceRows", "InstanceColumns", "InstancePhis", "InstanceLengths1", "InstanceLengths2" }; }
        }

        // 初始测量位置（图像坐标系下的椭圆定义）
        public double EllipseRow { get; set; } = 100;
        public double EllipseColumn { get; set; } = 100;
        public double EllipseAngle { get; set; }
        public double EllipseLength1 { get; set; } = 20;
        public double EllipseLength2 { get; set; } = 10;

        public EllipseFollowMeasureTool(string moduleName) : base(moduleName)
        {
            MeasureLength1 = 7;
            MeasureLength2 = 2;
            MeasureSigma = 1;
            MeasureThreshold = 1;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            NodeResult invalid = CheckBeforeRun();
            if (invalid != null)
            {
                return invalid;
            }
            return RunMeasurements(ctx, "椭圆测量",
                (HObject image, HomMat2D matrix, int resultIndex, out HObject contour) =>
                    MeasureOne(ctx, image, matrix, resultIndex, out contour),
                successCount => SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, successCount)));
        }

        /// <summary>单矩阵或固定位置时序号为 -1（不属于某次迭代），矩阵集合时按成功序号。</summary>
        protected override int DefaultResultIndex(int successCount, int matrixCount)
        {
            return matrixCount > 1 ? successCount : -1;
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, out HObject contour)
        {
            contour = null;
            double row = EllipseRow;
            double column = EllipseColumn;
            double phi = EllipseAngle;
            double length1 = EllipseLength1;
            double length2 = EllipseLength2;
            bool followed = false;
            if (matrix != null)
            {
                // 用矩阵把初始测量位姿（中心 + 方向角）变换到当前图像
                matrix.TransformPose(EllipseRow, EllipseColumn, EllipseAngle, out row, out column, out phi);
                length1 *= matrix.ScaleFactor;
                length2 *= matrix.ScaleFactor;
                followed = true;
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            try
            {
                HOperatorSet.SetMetrologyModelImageSize(metrology, width, height);
                AddMetrologyParams(out HTuple names, out HTuple values);
                HOperatorSet.AddMetrologyObjectEllipseMeasure(metrology,
                    row, column, phi, length1, length2,
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold,
                    names, values, out HTuple _);
                HTuple param = ApplyMetrology(image, metrology, out HObject resultContour);
                if (param.Length < 5)
                {
                    resultContour.Dispose();
                    return NodeResult.Fail($"椭圆测量失败（位置 {row:F1}, {column:F1}）：未找到足够的边缘点");
                }

                contour = resultContour;
                var measure = new EllipseMeasureResult
                {
                    Index = ResolveIndex(ctx, resultIndex),
                    Row = param[0].D,
                    Column = param[1].D,
                    Phi = param[2].D,
                    Length1 = param[3].D,
                    Length2 = param[4].D,
                    Followed = followed
                };
                AddResult(ctx, ModuleName, measure);
                SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, measure.Row));
                SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, measure.Column));
                SetOutput(ctx, Variable.Single(ModuleName, "Phi", VariableType.Double, measure.Phi));
                SetOutput(ctx, Variable.Single(ModuleName, "Length1", VariableType.Double, measure.Length1));
                SetOutput(ctx, Variable.Single(ModuleName, "Length2", VariableType.Double, measure.Length2));
                ctx.AddLog(FlowLogLevel.Info, $"[椭圆测量]{(followed ? "跟随" : "固定")} {measure}");
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
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("FailedCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<OneDCaliperMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    public sealed class OneDCaliperFollowMeasureTool : CaliperMeasureToolBase
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
            NodeResult invalid = CheckBeforeRun();
            if (invalid != null)
            {
                return invalid;
            }
            var edges = new List<OneDCaliperMeasureResult>();
            var distances = new List<double>();
            return RunMeasurements(ctx, "一维卡尺",
                (HObject image, HomMat2D matrix, int resultIndex, out HObject contour) =>
                    MeasureOne(ctx, image, matrix, resultIndex, edges, distances, out contour),
                _ => WriteCaliperSummary(ctx, edges, distances));
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex,
            List<OneDCaliperMeasureResult> edges, List<double> allDistances, out HObject contour)
        {
            contour = null;
            double row = BaseRow;
            double col = BaseColumn;
            double phi = BasePhi;
            double length1 = BaseLength1;
            double length2 = BaseLength2;
            bool followed = false;
            if (matrix != null)
            {
                matrix.TransformPose(BaseRow, BaseColumn, BasePhi, out row, out col, out phi);
                length1 *= matrix.ScaleFactor;
                length2 *= matrix.ScaleFactor;
                followed = true;
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.GenMeasureRectangle2(row, col, phi, length1, length2,
                width, height, "nearest_neighbor", out HTuple measureHandle);
            try
            {
                int edgeCount = MeasureCaliper(ctx, image, measureHandle, ResolveIndex(ctx, resultIndex), 0, 0, followed,
                    row, col, phi, length1, length2, 12, edges, allDistances, out contour);
                if (edgeCount == 0)
                {
                    return NodeResult.Fail($"一维卡尺测量失败（中心 {row:F1},{col:F1}）：{NotFoundText}");
                }
                ctx.AddLog(FlowLogLevel.Info, $"[一维卡尺]{(followed ? "跟随" : "固定")} {FoundText(edgeCount)}");
                return NodeResult.Ok;
            }
            finally
            {
                HOperatorSet.CloseMeasure(measureHandle);
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
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("FailedCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<OneDCaliperMeasureResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    public sealed class ArcCaliperFollowMeasureTool : CaliperMeasureToolBase
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
            NodeResult invalid = CheckBeforeRun();
            if (invalid != null)
            {
                return invalid;
            }
            var edges = new List<OneDCaliperMeasureResult>();
            var distances = new List<double>();
            return RunMeasurements(ctx, "一维圆弧卡尺",
                (HObject image, HomMat2D matrix, int resultIndex, out HObject contour) =>
                    MeasureOne(ctx, image, matrix, resultIndex, edges, distances, out contour),
                _ => WriteCaliperSummary(ctx, edges, distances));
        }

        /// <summary>
        /// 第 index 个卡尺的角度（HALCON 约定）。扫描范围达到整圆时均分 count 份，避免首尾卡尺重合；
        /// 否则包含起止两端。编辑器预览与运行时共用。
        /// </summary>
        public static double CaliperPhi(double startPhi, double endPhi, int count, int index)
        {
            if (count <= 1)
            {
                return (startPhi + endPhi) / 2.0;
            }
            double extent = endPhi - startPhi;
            double t = Math.Abs(extent) >= Math.PI * 2 - 1e-9
                ? (double)index / count
                : (double)index / (count - 1);
            return startPhi + extent * t;
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex,
            List<OneDCaliperMeasureResult> edges, List<double> allDistances, out HObject contour)
        {
            contour = null;
            double centerRow = BaseRow;
            double centerCol = BaseColumn;
            double radius = BaseRadius;
            double startPhi = StartPhi;
            double endPhi = EndPhi;
            bool followed = false;
            if (matrix != null)
            {
                FollowArc(matrix, BaseRow, BaseColumn, BaseRadius, StartPhi, EndPhi,
                    out centerRow, out centerCol, out radius, out startPhi, out endPhi);
                followed = true;
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.GenEmptyObj(out HObject localContours);
            int found = 0;
            int index = ResolveIndex(ctx, resultIndex);
            int caliperCount = Math.Max(1, CaliperCount);
            try
            {
                for (int i = 0; i < caliperCount; i++)
                {
                    double phi = CaliperPhi(startPhi, endPhi, caliperCount, i);
                    HomMat2D.PointOnCircle(centerRow, centerCol, radius, phi, out double row, out double col);
                    HOperatorSet.GenMeasureRectangle2(row, col, phi, MeasureLength1, MeasureLength2,
                        width, height, "nearest_neighbor", out HTuple measureHandle);
                    try
                    {
                        int edgeCount = MeasureCaliper(ctx, image, measureHandle, index, i, found, followed,
                            row, col, phi, MeasureLength1, MeasureLength2, 10, edges, allDistances, out HObject caliperContour);
                        if (edgeCount == 0)
                        {
                            continue;
                        }
                        found += edgeCount;

                        HOperatorSet.ConcatObj(localContours, caliperContour, out HObject combined);
                        localContours.Dispose();
                        caliperContour.Dispose();
                        localContours = combined;
                    }
                    finally
                    {
                        HOperatorSet.CloseMeasure(measureHandle);
                    }
                }

                if (found == 0)
                {
                    return NodeResult.Fail($"一维圆弧卡尺测量失败（圆心 {centerRow:F1},{centerCol:F1}, R={radius:F1}）：{NotFoundText}");
                }

                contour = localContours;
                localContours = null;
                ctx.AddLog(FlowLogLevel.Info, $"[一维圆弧卡尺]{(followed ? "跟随" : "固定")} 卡尺数={caliperCount}, {FoundText(found)}");
                return NodeResult.Ok;
            }
            finally
            {
                localContours?.Dispose();
            }
        }
    }
}
