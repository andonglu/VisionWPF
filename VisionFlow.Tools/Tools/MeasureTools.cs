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

    /// <summary>单次椭圆测量结果。</summary>
    public sealed class EllipseMeasureResult
    {
        public int Index { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Phi { get; set; }
        public double Length1 { get; set; }
        public double Length2 { get; set; }
        /// <summary>true = 按输入矩阵做了跟随变换；false = 固定位置测量。</summary>
        public bool Followed { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")} 中心=({Row:F2}, {Column:F2}), Phi={Phi:F4}, Len1={Length1:F2}, Len2={Length2:F2}";
        }
    }

    public sealed class OneDCaliperMeasureResult
    {
        public int Index { get; set; }
        public int EdgeIndex { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Amplitude { get; set; }
        /// <summary>与同一卡尺内下一条边缘的距离（measure_pos 的 Distance）；最后一条边缘没有下一条，为 NaN。</summary>
        public double Distance { get; set; }
        public bool Followed { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")}.{EdgeIndex} Edge=({Row:F2},{Column:F2}), Amp={Amplitude:F2}, Dist={Distance:F2}";
        }
    }

    /// <summary>
    /// 支持“区域初始化”的测量工具：配置 InitRegionPath 后，按初始区域中每个对象的形状
    /// （矩形取 smallest_rectangle2，圆取 smallest_circle）作为初始几何做亚像素测量，无需示教与变换矩阵。
    /// </summary>
    public interface IRegionSeededMeasureTool
    {
        string InitRegionPath { get; set; }
    }

    /// <summary>
    /// 跟随测量工具基类：统一矩阵解析、多定位结果遍历、失败项记录、轮廓合并与“未找到”处理。
    /// 公共输出：ResultContour（本次全部测量轮廓的 XLD）、Found（至少一项测量成功）、
    /// FailedCount（失败的定位结果数）、Results（跨循环累积的结果列表）。
    /// 单值输出（Row 等）为本次最后一次成功的测量；全部失败时置为 NaN，避免循环中读到上一轮的值。
    /// </summary>
    public abstract class FollowMeasureToolBase : ToolBase, INotFoundPolicy
    {
        /// <summary>单次测量：成功时返回 Ok；contour 为本次测量的显示轮廓（可为 null，归调用方释放）。</summary>
        protected delegate NodeResult MeasureOneHandler(HObject image, HomMat2D matrix, int resultIndex, out HObject contour);

        /// <summary>区域初始化的单次测量：region 为初始区域中的单个对象（调用结束后由基类释放）。</summary>
        protected delegate NodeResult RegionMeasureHandler(HObject image, HObject region, int resultIndex, out HObject contour);

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("变换矩阵", typeof(HomMat2D), Optional = true, AcceptsCollection = true)]
        public string MatrixPath { get; set; }

        [InputRef("结果序号", typeof(int), Optional = true)]
        public string IndexPath { get; set; }

        public double MeasureLength1 { get; set; } = 10;
        public double MeasureLength2 { get; set; } = 3;
        public double MeasureSigma { get; set; } = 1;
        public double MeasureThreshold { get; set; } = 20;
        public string MeasureTransition { get; set; } = "all";
        public string MeasureSelect { get; set; } = "all";
        /// <summary>全部测量都未找到边缘时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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

        /// <summary>结果序号：配置了 IndexPath 时取引用值，否则使用默认序号。</summary>
        protected int ResolveIndex(FlowContext ctx, int resultIndex)
        {
            return string.IsNullOrWhiteSpace(IndexPath) ? resultIndex : GetIndex(ctx);
        }

        /// <summary>未配置 IndexPath 时的默认结果序号。</summary>
        protected virtual int DefaultResultIndex(int successCount, int matrixCount)
        {
            return successCount;
        }

        /// <summary>
        /// 解析定位矩阵引用。
        /// 未配置 MatrixPath：返回 [null]，即固定位置测量（正常使用方式）。
        /// 已配置但解析失败：返回失败；仅显式允许降级的预览上下文可退回固定位置。
        /// </summary>
        protected bool TryGetMatrices(FlowContext ctx, out List<HomMat2D> matrices, out string error)
        {
            return FollowMatrixResolver.TryResolve(ctx, ModuleName, MatrixPath, out matrices, out error);
        }

        /// <summary>
        /// 对每个定位矩阵执行一次测量。部分失败时整体仍成功，但每个失败项都会记录警告并计入 FailedCount；
        /// 全部失败按 <see cref="FailWhenNotFound"/> 处理。writeSummary 在公共输出之前写入工具特有的汇总输出。
        /// </summary>
        protected NodeResult RunMeasurements(FlowContext ctx, string label, MeasureOneHandler measureOne,
            Action<int> writeSummary)
        {
            if (!TryGetMatrices(ctx, out List<HomMat2D> matrices, out string matrixError))
            {
                return NodeResult.Fail(matrixError);
            }
            HObject image = GetImage(ctx);
            return RunSeeds(ctx, label, "定位结果", matrices.Count,
                (int index, int successCount, out HObject contour) =>
                    measureOne(image, matrices[index], DefaultResultIndex(successCount, matrices.Count), out contour),
                writeSummary, null);
        }

        /// <summary>
        /// 区域初始化测量：对初始区域中的每个对象执行一次测量（结果序号 = 区域对象序号，0 起）。
        /// 区域本身给出了位置，因此不能同时配置变换矩阵；区域为空按“未找到”处理。
        /// </summary>
        protected NodeResult RunRegionMeasurements(FlowContext ctx, string label, string regionPath,
            RegionMeasureHandler measureOne, Action<int> writeSummary)
        {
            if (!string.IsNullOrWhiteSpace(MatrixPath))
            {
                return NodeResult.Fail($"{ModuleName} 已配置初始区域，不能同时配置变换矩阵（区域本身已给出测量位置）");
            }
            HObject regions = Input<HalconRegion>(ctx, regionPath).Object;
            HObject image = GetImage(ctx);
            HOperatorSet.CountObj(regions, out HTuple count);
            return RunSeeds(ctx, label, "区域对象", count.I,
                (int index, int successCount, out HObject contour) =>
                {
                    HOperatorSet.SelectObj(regions, out HObject region, index + 1);
                    try
                    {
                        HOperatorSet.AreaCenter(region, out HTuple area, out _, out _);
                        if (area.D <= 0)
                        {
                            contour = null;
                            return NodeResult.Fail($"{label}失败：第 {index} 个区域对象为空");
                        }
                        return measureOne(image, region, index, out contour);
                    }
                    finally
                    {
                        region.Dispose();
                    }
                },
                writeSummary, $"{label}失败：初始区域 '{regionPath}' 中没有区域对象");
        }

        private delegate NodeResult SeedRunner(int index, int successCount, out HObject contour);

        /// <summary>本次运行的测量项总数（定位矩阵数或初始区域对象数）。</summary>
        protected int SeedCount { get; private set; }

        /// <summary>当前测量项序号（0 起），用于写入与测量项逐一对齐的数组输出。</summary>
        protected int SeedIndex { get; private set; }

        /// <summary>创建与测量项逐一对齐、初值为 NaN 的数组（失败项保持 NaN）。</summary>
        protected double[] NewSeedArray()
        {
            double[] values = new double[SeedCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = double.NaN;
            }
            return values;
        }

        private NodeResult RunSeeds(FlowContext ctx, string label, string seedName, int seedCount, SeedRunner run,
            Action<int> writeSummary, string emptyMessage)
        {
            HOperatorSet.GenEmptyObj(out HObject allContours);
            int successCount = 0;
            int failedCount = 0;
            string lastError = seedCount == 0 ? emptyMessage : null;
            SeedCount = seedCount;
            SeedIndex = 0;
            try
            {
                for (int i = 0; i < seedCount; i++)
                {
                    SeedIndex = i;
                    NodeResult result;
                    HObject contour;
                    try
                    {
                        result = run(i, successCount, out contour);
                    }
                    catch (HalconException ex)
                    {
                        // 单个对象的 HALCON 错误（如区域过小、有效测量不足）只记为该项失败，不影响其他对象
                        contour = null;
                        result = NodeResult.Fail($"{label}失败：{ex.Message}");
                    }
                    if (contour != null)
                    {
                        HOperatorSet.ConcatObj(allContours, contour, out HObject combined);
                        allContours.Dispose();
                        contour.Dispose();
                        allContours = combined;
                    }
                    if (result.IsSuccess)
                    {
                        successCount++;
                    }
                    else
                    {
                        failedCount++;
                        lastError = result.Message;
                        if (seedCount > 1)
                        {
                            ctx.AddLog(FlowLogLevel.Warning, $"[{label}] 第 {i} 个{seedName}测量失败：{result.Message}");
                        }
                    }
                }
            }
            catch
            {
                allContours.Dispose();
                throw;
            }

            if (successCount == 0)
            {
                ClearSingleOutputs(ctx);
            }
            writeSummary?.Invoke(successCount);
            SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", new HalconXld(allContours), successCount));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, successCount > 0));
            SetOutput(ctx, Variable.Single(ModuleName, "FailedCount", VariableType.Int, failedCount));
            if (successCount > 0)
            {
                return NodeResult.Ok;
            }
            return NotFoundOutcome.Resolve(ctx, this, lastError ?? $"{label}失败：未找到有效结果");
        }

        /// <summary>把声明的 Double 单值输出置为 NaN。</summary>
        private void ClearSingleOutputs(FlowContext ctx)
        {
            foreach (ToolOutputDef output in ToolMetadata.GetOutputs(GetType()))
            {
                if (output.Kind == VariableKind.Single && output.Type == VariableType.Double)
                {
                    SetOutput(ctx, Variable.Single(ModuleName, output.Name, VariableType.Double, double.NaN));
                }
            }
        }

        /// <summary>累积写入跨循环共享的结果列表，并同步变量的数量。</summary>
        protected static void AddResult<T>(FlowContext ctx, string moduleName, T result)
        {
            List<T> results = ctx.TryGetVariable(moduleName, "Results", out Variable existing)
                ? existing.GetValue<List<T>>()
                : new List<T>();
            results.Add(result);
            ctx.SetVariable(Variable.Object(moduleName, "Results", results, results.Count));
        }

        protected void AddMetrologyParams(out HTuple names, out HTuple values)
        {
            names = new HTuple("measure_transition").TupleConcat("measure_select");
            values = new HTuple(MeasureTransition).TupleConcat(MeasureSelect);
        }

        /// <summary>执行 metrology 模型，返回第 0 个对象的结果参数与结果轮廓（轮廓归调用方释放）。</summary>
        protected static HTuple ApplyMetrology(HObject image, HTuple metrology, out HObject contour)
        {
            HOperatorSet.ApplyMetrologyModel(image, metrology);
            HOperatorSet.GetMetrologyObjectResult(metrology, 0, "all", "result_type", "all_param", out HTuple param);
            HOperatorSet.GetMetrologyObjectResultContour(out contour, metrology, "all", "all", 1.5);
            return param;
        }

        /// <summary>把 measure_pos 结果（边缘点）逐条转换为卡尺结果，Distance 为到下一条边缘的距离。</summary>
        protected static List<OneDCaliperMeasureResult> ToCaliperResults(HTuple rows, HTuple columns, HTuple amplitudes,
            HTuple distances, int index, int firstEdgeIndex, bool followed)
        {
            var results = new List<OneDCaliperMeasureResult>();
            for (int i = 0; i < rows.Length; i++)
            {
                results.Add(new OneDCaliperMeasureResult
                {
                    Index = index,
                    EdgeIndex = firstEdgeIndex + i,
                    Row = rows[i].D,
                    Column = columns[i].D,
                    Amplitude = amplitudes[i].D,
                    Distance = i < distances.Length ? distances[i].D : double.NaN,
                    Followed = followed
                });
            }
            return results;
        }

        /// <summary>卡尺显示轮廓：测量矩形（XLD）+ 边缘十字。</summary>
        protected static HObject BuildCaliperContour(double row, double col, double phi, double length1, double length2,
            HTuple rows, HTuple columns, double crossSize)
        {
            HOperatorSet.GenRectangle2ContourXld(out HObject rectangle, row, col, phi, length1, length2);
            if (rows.Length == 0)
            {
                return rectangle;
            }
            HOperatorSet.GenCrossContourXld(out HObject crosses, rows, columns, crossSize, phi);
            HOperatorSet.ConcatObj(rectangle, crosses, out HObject contour);
            rectangle.Dispose();
            crosses.Dispose();
            return contour;
        }

        /// <summary>
        /// 卡尺类工具的汇总输出：全部边缘点、相邻边缘距离（measure_pos 语义，每个卡尺 N-1 个）、首个值与边缘数量。
        /// </summary>
        protected void WriteCaliperSummary(FlowContext ctx, List<OneDCaliperMeasureResult> edges, List<double> distances)
        {
            SetOutput(ctx, Variable.Array(ModuleName, "Rows", VariableType.Double, edges.Select(e => e.Row)));
            SetOutput(ctx, Variable.Array(ModuleName, "Columns", VariableType.Double, edges.Select(e => e.Column)));
            SetOutput(ctx, Variable.Array(ModuleName, "Amplitudes", VariableType.Double, edges.Select(e => e.Amplitude)));
            SetOutput(ctx, Variable.Array(ModuleName, "Distances", VariableType.Double, distances));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstRow", VariableType.Double, edges.Count > 0 ? edges[0].Row : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstColumn", VariableType.Double, edges.Count > 0 ? edges[0].Column : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstAmplitude", VariableType.Double, edges.Count > 0 ? edges[0].Amplitude : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstDistance", VariableType.Double, distances.Count > 0 ? distances[0] : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, edges.Count));
        }

        /// <summary>
        /// 圆弧跟随：圆心按矩阵变换，半径按缩放系数变化，起止角只叠加定位旋转量（保持示教时的扫描范围）。
        /// 直接对起止点分别求角会让整圆（0..2π）塌成同一角度。
        /// </summary>
        protected static void FollowArc(HomMat2D matrix, double baseRow, double baseColumn, double baseRadius,
            double baseStartPhi, double baseEndPhi,
            out double row, out double column, out double radius, out double startPhi, out double endPhi)
        {
            matrix.TransformPoint(baseRow, baseColumn, out row, out column);
            HomMat2D.PointOnCircle(baseRow, baseColumn, baseRadius, baseStartPhi, out double baseStartRow, out double baseStartColumn);
            matrix.TransformPoint(baseStartRow, baseStartColumn, out double startRow, out double startColumn);
            double rotation = HomMat2D.DirectionToPhi(startRow - row, startColumn - column) - baseStartPhi;
            startPhi = baseStartPhi + rotation;
            endPhi = baseEndPhi + rotation;
            radius = Math.Sqrt((startRow - row) * (startRow - row) + (startColumn - column) * (startColumn - column));
        }
    }

    internal static class FollowMatrixResolver
    {
        public static bool TryResolve(FlowContext ctx, string moduleName, string matrixPath,
            out List<HomMat2D> matrices, out string error)
        {
            if (string.IsNullOrWhiteSpace(matrixPath))
            {
                matrices = new List<HomMat2D> { null };
                error = null;
                return true;
            }

            try
            {
                object value = VariableReference.Parse(matrixPath).Resolve(ctx);
                List<HomMat2D> resolved;
                if (value is HomMat2D single)
                {
                    resolved = new List<HomMat2D> { single };
                }
                else if (value is System.Collections.IEnumerable many && !(value is string))
                {
                    resolved = new List<HomMat2D>();
                    foreach (object item in many)
                    {
                        if (item != null && !(item is HomMat2D))
                        {
                            throw new InvalidOperationException(
                                $"矩阵集合第 {resolved.Count} 项类型为 {item.GetType().Name}，不是 HomMat2D");
                        }
                        resolved.Add((HomMat2D)item);
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        $"引用值类型为 {value?.GetType().Name ?? "null"}，不是 HomMat2D 或 HomMat2D 集合");
                }

                if (resolved.Count == 0)
                {
                    throw new InvalidOperationException("矩阵集合为空（上游定位没有任何结果）");
                }
                for (int i = 0; i < resolved.Count; i++)
                {
                    HomMat2D matrix = resolved[i];
                    if (matrix == null)
                    {
                        throw new InvalidOperationException($"矩阵集合第 {i} 项为 null");
                    }
                    try
                    {
                        if (matrix.Data == null || matrix.Data.Length != 6)
                        {
                            throw new InvalidOperationException("变换矩阵必须包含 6 个数值");
                        }
                        var data = new double[6];
                        for (int j = 0; j < data.Length; j++)
                        {
                            data[j] = matrix.Data[j].D;
                            if (!double.IsFinite(data[j]))
                            {
                                throw new InvalidOperationException("变换矩阵包含非有限数值");
                            }
                        }
                        double determinant = data[0] * data[4] - data[1] * data[3];
                        if (!double.IsFinite(determinant) || determinant == 0)
                        {
                            throw new InvalidOperationException("变换矩阵不可逆或超出数值范围");
                        }
                    }
                    catch (Exception ex) when (ex is HalconException || ex is InvalidOperationException)
                    {
                        throw new InvalidOperationException($"矩阵集合第 {i} 项无效：{ex.Message}", ex);
                    }
                }
                matrices = resolved;
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException
                || ex is OverflowException || ex is InvalidCastException
                || ex is InvalidOperationException || ex is KeyNotFoundException
                || ex is HalconException)
            {
                return ResolutionFailed(ctx, moduleName, matrixPath, $"解析失败：{ex.Message}", out matrices, out error);
            }
        }

        private static bool ResolutionFailed(FlowContext ctx, string moduleName, string matrixPath, string reason,
            out List<HomMat2D> matrices, out string error)
        {
            if (ctx.IsPreview && ctx.AllowMatrixFallback)
            {
                ctx.AddLog(FlowLogLevel.Warning,
                    $"[测量] {moduleName}：定位矩阵引用 '{matrixPath}' {reason}，按固定位置测量（预览已显式允许降级）");
                matrices = new List<HomMat2D> { null };
                error = null;
                return true;
            }
            matrices = null;
            error = $"{moduleName} 定位矩阵引用 '{matrixPath}' {reason}。" +
                    "已配置定位跟随但解析失败，不会退回固定位置测量；如确需预览降级请创建显式允许降级的预览上下文";
            return false;
        }
    }
}
