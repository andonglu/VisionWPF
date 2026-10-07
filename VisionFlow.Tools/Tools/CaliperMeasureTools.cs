using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 一维卡尺与一维圆弧卡尺的公共基类（MS-02）：单边缘（measure_pos，默认，即原有行为）与边缘对（measure_pairs）两种模式。
    /// 边缘对模式：
    /// - Widths / Gaps / PairCenterRows / PairCenterColumns / FirstWidth / PairCount / PairResults 为本次运行全部边缘对的结果；
    /// - 原有边缘输出（Rows / Columns / Amplitudes / Results 等）依次写入每对的第一、第二条边缘（长度 = 2 × PairCount），
    ///   Distances 为同一卡尺内相邻边缘的距离（宽度、间距交替）；
    /// - 显示轮廓在卡尺矩形和边缘十字之外加尺寸线（两边缘连线 + 两端挡线）。
    /// 单边缘模式下边缘对输出写空数组、0 和 NaN，并对引用候选隐藏。
    /// </summary>
    [ToolOutput("Widths", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Gaps", VariableKind.Array, VariableType.Double)]
    [ToolOutput("PairCenterRows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("PairCenterColumns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstWidth", VariableKind.Single, VariableType.Double)]
    [ToolOutput("PairCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("PairResults", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<OneDCaliperPairResult>))]
    public abstract class CaliperMeasureToolBase : FollowMeasureToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>单边缘模式可用的边缘极性（measure_pos）。</summary>
        public static readonly string[] EdgeTransitions = { "all", "positive", "negative" };
        /// <summary>边缘对模式可用的边缘极性（measure_pairs）。</summary>
        public static readonly string[] PairTransitions = { "all", "positive", "negative", "all_strongest", "positive_strongest", "negative_strongest" };
        /// <summary>两种模式共用的边缘选择。</summary>
        public static readonly string[] Selections = { "all", "first", "last" };

        /// <summary>只在边缘对模式下有意义的输出（单边缘模式对引用候选隐藏）。</summary>
        public static readonly string[] PairOutputNames = { "Widths", "Gaps", "PairCenterRows", "PairCenterColumns", "FirstWidth", "PairCount", "PairResults" };

        /// <summary>边缘模式：单边缘（默认）或边缘对。</summary>
        public EdgeMode EdgeMode { get; set; } = EdgeMode.Edge;

        private readonly List<OneDCaliperPairResult> _pairs = new List<OneDCaliperPairResult>();
        private readonly List<double> _gaps = new List<double>();

        protected CaliperMeasureToolBase(string moduleName) : base(moduleName)
        {
        }

        /// <summary>当前模式可用的边缘极性。</summary>
        public static IReadOnlyList<string> TransitionsFor(EdgeMode mode)
        {
            return mode == EdgeMode.Pair ? PairTransitions : EdgeTransitions;
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (!TransitionsFor(EdgeMode).Contains(MeasureTransition ?? string.Empty))
            {
                yield return new ToolConfigurationIssue(nameof(MeasureTransition), EdgeMode == EdgeMode.Pair
                    ? "边缘对模式的边缘极性只能是 " + string.Join(" / ", PairTransitions)
                    : "单边缘模式的边缘极性只能是 " + string.Join(" / ", EdgeTransitions)
                        + (PairTransitions.Contains(MeasureTransition ?? string.Empty) ? "（*_strongest 只用于边缘对模式）" : string.Empty));
            }
            if (!Selections.Contains(MeasureSelect ?? string.Empty))
            {
                yield return new ToolConfigurationIssue(nameof(MeasureSelect), "边缘选择只能是 " + string.Join(" / ", Selections));
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            return EdgeMode == EdgeMode.Pair || !PairOutputNames.Contains(propertyName);
        }

        /// <summary>参数配置错误时返回失败结果，否则返回 null。</summary>
        protected NodeResult CheckBeforeRun()
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            return issue == null ? null : NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
        }

        /// <summary>日志与失败信息中的结果名称。</summary>
        protected string FoundText(int edgeCount)
        {
            return EdgeMode == EdgeMode.Pair ? $"找到边缘对数={edgeCount / 2}" : $"找到边缘数={edgeCount}";
        }

        protected string NotFoundText => EdgeMode == EdgeMode.Pair ? "未找到边缘对" : "未找到边缘";

        /// <summary>
        /// 在一个卡尺测量句柄上按当前模式测量：结果追加到 edges / allDistances（边缘点语义，单边缘模式与原实现逐项相同），
        /// 边缘对累积到本次运行的边缘对输出。返回找到的边缘数（边缘对模式为 2 × 对数），0 表示未找到（此时 contour 为 null）。
        /// </summary>
        protected int MeasureCaliper(FlowContext ctx, HObject image, HTuple measureHandle, int index, int caliperIndex,
            int firstEdgeIndex, bool followed, double row, double col, double phi, double length1, double length2, double crossSize,
            List<OneDCaliperMeasureResult> edges, List<double> allDistances, out HObject contour)
        {
            contour = null;
            if (EdgeMode != EdgeMode.Pair)
            {
                HOperatorSet.MeasurePos(image, measureHandle, MeasureSigma, MeasureThreshold,
                    MeasureTransition, MeasureSelect, out HTuple rows, out HTuple columns, out HTuple amplitudes, out HTuple distances);
                if (rows.Length == 0)
                {
                    return 0;
                }
                foreach (OneDCaliperMeasureResult result in ToCaliperResults(rows, columns, amplitudes, distances, index, firstEdgeIndex, followed))
                {
                    AddResult(ctx, ModuleName, result);
                    edges.Add(result);
                }
                allDistances.AddRange(distances.Length > 0 ? distances.DArr : new double[0]);
                contour = BuildCaliperContour(row, col, phi, length1, length2, rows, columns, crossSize);
                return rows.Length;
            }

            List<OneDCaliperPairResult> pairs = MeasureCaliperPairs(image, measureHandle, MeasureSigma, MeasureThreshold,
                MeasureTransition, MeasureSelect, index, caliperIndex, followed, out double[] gaps);
            if (pairs.Count == 0)
            {
                return 0;
            }
            List<OneDCaliperMeasureResult> pairEdges = PairsToEdgeResults(pairs, gaps, index, firstEdgeIndex, followed, out List<double> pairDistances);
            foreach (OneDCaliperMeasureResult result in pairEdges)
            {
                AddResult(ctx, ModuleName, result);
                edges.Add(result);
            }
            allDistances.AddRange(pairDistances);
            _pairs.AddRange(pairs);
            _gaps.AddRange(gaps);

            HObject caliper = BuildCaliperContour(row, col, phi, length1, length2,
                new HTuple(pairEdges.Select(e => e.Row).ToArray()), new HTuple(pairEdges.Select(e => e.Column).ToArray()), crossSize);
            // 挡线贯穿卡尺宽度并再伸出一个十字大小，与边缘十字区分开
            HObject dimensions = BuildPairDimensionLines(pairs, phi, length2 + crossSize);
            HOperatorSet.ConcatObj(caliper, dimensions, out contour);
            caliper.Dispose();
            dimensions.Dispose();
            return pairEdges.Count;
        }

        protected override void OnSeedsStarting()
        {
            _pairs.Clear();
            _gaps.Clear();
        }

        protected override void WriteSeedOutputs(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Array(ModuleName, "Widths", VariableType.Double, _pairs.Select(p => p.Width)));
            SetOutput(ctx, Variable.Array(ModuleName, "Gaps", VariableType.Double, _gaps.ToArray()));
            SetOutput(ctx, Variable.Array(ModuleName, "PairCenterRows", VariableType.Double, _pairs.Select(p => p.CenterRow)));
            SetOutput(ctx, Variable.Array(ModuleName, "PairCenterColumns", VariableType.Double, _pairs.Select(p => p.CenterColumn)));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstWidth", VariableType.Double, _pairs.Count > 0 ? _pairs[0].Width : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "PairCount", VariableType.Int, _pairs.Count));
            var results = new List<OneDCaliperPairResult>(_pairs);
            SetOutput(ctx, Variable.Object(ModuleName, "PairResults", results, results.Count));
        }
    }
}
