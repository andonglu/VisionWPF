using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    public enum XldSegmentMode
    {
        lines,
        lines_circles,
        /// <summary>直线与椭圆弧（XG-03）。</summary>
        lines_ellipses
    }

    /// <summary>边缘提取方式（XG-01）。</summary>
    public enum ContourExtractMethod
    {
        /// <summary>edges_color_sub_pix（原有方式，默认）。</summary>
        ColorEdges,
        /// <summary>edges_sub_pix（灰度图亚像素边缘，滤波器更多）。</summary>
        Edges,
        /// <summary>threshold_sub_pix（亚像素阈值轮廓）。</summary>
        ThresholdSubPix,
        /// <summary>lines_gauss（线条中心线）。</summary>
        LinesGauss
    }

    /// <summary>
    /// 边缘滤波器（成员名即 HALCON 参数值）。原为字符串参数，按名称保存，文件内容与旧版一致、旧版程序仍可读取。
    /// 彩色边缘（edges_color_sub_pix）只支持前 9 个（canny / deriche1 / deriche2 / shen / sobel_fast 及其 _junctions），其余仅灰度边缘可用。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EdgeFilter
    {
        canny,
        deriche1,
        deriche2,
        shen,
        sobel_fast,
        canny_junctions,
        deriche1_junctions,
        deriche2_junctions,
        shen_junctions,
        lanser1,
        lanser2,
        mshen,
        sobel,
        lanser1_junctions,
        lanser2_junctions,
        mshen_junctions,
        sobel_junctions
    }

    /// <summary>lines_gauss 的线条模型；bar_shaped 对应 HALCON 的 "bar-shaped"。</summary>
    public enum LineGaussModel
    {
        none,
        bar_shaped,
        parabolic,
        gaussian
    }

    /// <summary>
    /// 边缘提取（XG-01）：按 ExtractMethod 选择 edges_color_sub_pix（默认，原有行为）/ edges_sub_pix / threshold_sub_pix / lines_gauss。
    /// lines_gauss 的阈值是二阶导数量纲，与梯度阈值不同，因此使用单独的 LineSigma / LineLow / LineHigh（默认取 HALCON 默认值）。
    /// </summary>
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class ContourCreateTool : XldToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>提取方式。</summary>
        public ContourExtractMethod ExtractMethod { get; set; } = ContourExtractMethod.ColorEdges;
        /// <summary>边缘滤波器（彩色边缘 / 灰度边缘）。</summary>
        public EdgeFilter Filter { get; set; } = EdgeFilter.canny;
        /// <summary>滤波平滑系数（作为 Alpha 传入）。</summary>
        public double Sigma { get; set; } = 1.0;
        public int Low { get; set; } = 20;
        public int High { get; set; } = 40;
        /// <summary>亚像素阈值（threshold_sub_pix）。</summary>
        public double Threshold { get; set; } = 128;
        /// <summary>线条提取的高斯平滑系数（lines_gauss）。</summary>
        public double LineSigma { get; set; } = 1.5;
        /// <summary>线条提取的低阈值（二阶导数）。</summary>
        public double LineLow { get; set; } = 3;
        /// <summary>线条提取的高阈值（二阶导数）。</summary>
        public double LineHigh { get; set; } = 8;
        /// <summary>提取亮线还是暗线（lines_gauss 只支持 light / dark）。</summary>
        public ThresholdLightDark LightDark { get; set; } = ThresholdLightDark.light;
        /// <summary>是否同时估计线宽。</summary>
        public bool ExtractWidth { get; set; } = true;
        /// <summary>线条模型（用于修正线位置与线宽）。</summary>
        public LineGaussModel LineModel { get; set; } = LineGaussModel.bar_shaped;
        /// <summary>是否补全交叉处断开的线条。</summary>
        public bool CompleteJunctions { get; set; } = true;

        public ContourCreateTool(string moduleName) : base(moduleName)
        {
        }

        /// <summary>彩色边缘（edges_color_sub_pix）支持的滤波器。</summary>
        public static bool IsColorFilter(EdgeFilter filter)
        {
            return filter <= EdgeFilter.shen_junctions;
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (ExtractMethod == ContourExtractMethod.ColorEdges && !IsColorFilter(Filter))
            {
                yield return new ToolConfigurationIssue(nameof(Filter),
                    $"彩色边缘提取不支持滤波器 {Filter}（可选 canny / deriche1 / deriche2 / shen / sobel_fast 及其 _junctions；其他滤波器请改用灰度边缘 Edges）");
            }
            if (ExtractMethod == ContourExtractMethod.LinesGauss && LightDark != ThresholdLightDark.light && LightDark != ThresholdLightDark.dark)
            {
                yield return new ToolConfigurationIssue(nameof(LightDark), "线条提取只支持 light / dark");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(Filter):
                case nameof(Sigma):
                case nameof(Low):
                case nameof(High):
                    return ExtractMethod == ContourExtractMethod.ColorEdges || ExtractMethod == ContourExtractMethod.Edges;
                case nameof(Threshold):
                    return ExtractMethod == ContourExtractMethod.ThresholdSubPix;
                case nameof(LineSigma):
                case nameof(LineLow):
                case nameof(LineHigh):
                case nameof(LightDark):
                case nameof(ExtractWidth):
                case nameof(LineModel):
                case nameof(CompleteJunctions):
                    return ExtractMethod == ContourExtractMethod.LinesGauss;
                default:
                    return true;
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject xld;
            string log;
            switch (ExtractMethod)
            {
                case ContourExtractMethod.Edges:
                    HOperatorSet.EdgesSubPix(image, out xld, Filter.ToString(), Sigma, Low, High);
                    log = $"[边缘提取] 灰度边缘 {Filter}, Alpha={Sigma}, Low={Low}, High={High}";
                    break;
                case ContourExtractMethod.ThresholdSubPix:
                    HOperatorSet.ThresholdSubPix(image, out xld, Threshold);
                    log = $"[边缘提取] 亚像素阈值 {Threshold}";
                    break;
                case ContourExtractMethod.LinesGauss:
                    string model = LineModel == LineGaussModel.bar_shaped ? "bar-shaped" : LineModel.ToString();
                    HOperatorSet.LinesGauss(image, out xld, LineSigma, LineLow, LineHigh, LightDark.ToString(),
                        ExtractWidth ? "true" : "false", model, CompleteJunctions ? "true" : "false");
                    log = $"[边缘提取] 线条 Sigma={LineSigma}, Low={LineLow}, High={LineHigh}, {LightDark}, {model}";
                    break;
                default:
                    HOperatorSet.EdgesColorSubPix(image, out xld, Filter.ToString(), Sigma, Low, High);
                    log = $"[边缘提取] {Filter}, Sigma={Sigma}, Low={Low}, High={High}";
                    break;
            }
            return SetXldOutput(ctx, xld, log);
        }
    }

    /// <summary>边缘选择依据（XG-02）。</summary>
    public enum ContourSelectBy
    {
        /// <summary>select_shape_xld 形状特征（原有方式，默认）。</summary>
        Shape,
        /// <summary>select_contours_xld 轮廓特征。</summary>
        Contour
    }

    /// <summary>select_contours_xld 的特征（成员名即 HALCON 参数值，见计划第 10 节实测清单）。</summary>
    public enum ContourFeature
    {
        /// <summary>轮廓长度 ∈ [Min1, Max1]。</summary>
        contour_length,
        /// <summary>最大外延 ∈ [Min1, Max1]。</summary>
        maximum_extent,
        /// <summary>回归直线方向（弧度）∈ [Min1, Max1]。</summary>
        direction,
        /// <summary>平均曲率 ∈ [Min1, Max1] 且曲率标准差 ∈ [Min2, Max2]。</summary>
        curvature,
        /// <summary>首尾距离 ≤ Max1（闭合轮廓）。</summary>
        closed,
        /// <summary>首尾距离 ≥ Min1（开口轮廓）。</summary>
        open
    }

    /// <summary>多特征之间的关系（成员名即 HALCON 参数值）。原为字符串参数，按名称保存以保持文件兼容。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum XldSelectOperation
    {
        and,
        or
    }

    /// <summary>选中轮廓后的取件方式。</summary>
    public enum ContourTakeMode
    {
        All,
        /// <summary>最长的一条（length_xld）。</summary>
        Longest,
        /// <summary>最短的一条。</summary>
        Shortest,
        First,
        /// <summary>按序号取（TakeIndex，从 0 开始）。</summary>
        ByIndex
    }

    /// <summary>
    /// 边缘选择（XG-02）：按形状特征（select_shape_xld，默认，原有行为）或轮廓特征（select_contours_xld）选择，再按 TakeMode 取件。
    /// 执行次序为先选择、后取件；按序号取件越界时输出为空并按 FailWhenNotFound 处理。
    /// </summary>
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class SelectContourTool : XldToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        /// <summary>选择依据。</summary>
        public ContourSelectBy SelectBy { get; set; } = ContourSelectBy.Shape;
        public string Features { get; set; } = "contlength";
        public XldSelectOperation Operation { get; set; } = XldSelectOperation.and;
        /// <summary>所有特征共用的下限（未配置 MinValues 时使用）。</summary>
        public double Min { get; set; } = 10;
        /// <summary>所有特征共用的上限（未配置 MaxValues 时使用）。</summary>
        public double Max { get; set; } = 999999;
        /// <summary>按特征分别设置的下限（逗号分隔，数量须与 Features 一致）；为空时使用 Min。</summary>
        public string MinValues { get; set; }
        /// <summary>按特征分别设置的上限（逗号分隔，数量须与 Features 一致）；为空时使用 Max。</summary>
        public string MaxValues { get; set; }
        /// <summary>轮廓特征（按轮廓特征选择时）。</summary>
        public ContourFeature ContourFeature { get; set; } = ContourFeature.contour_length;
        public double Min1 { get; set; } = 0.5;
        public double Max1 { get; set; } = 200;
        public double Min2 { get; set; } = -0.5;
        public double Max2 { get; set; } = 0.5;
        /// <summary>取件方式。</summary>
        public ContourTakeMode TakeMode { get; set; } = ContourTakeMode.All;
        /// <summary>按序号取件时的序号（选择结果中的第几条，从 0 开始）。</summary>
        public int TakeIndex { get; set; }

        public SelectContourTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (SelectBy == ContourSelectBy.Shape && HalconTupleConvert.SplitCsv(Features).Length == 0)
            {
                yield return new ToolConfigurationIssue(nameof(Features), "按形状特征选择需要配置特征");
            }
            if (TakeMode == ContourTakeMode.ByIndex && TakeIndex < 0)
            {
                yield return new ToolConfigurationIssue(nameof(TakeIndex), "序号不能小于 0");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            bool shape = SelectBy == ContourSelectBy.Shape;
            switch (propertyName)
            {
                case nameof(Features):
                case nameof(Operation):
                case nameof(Min):
                case nameof(Max):
                case nameof(MinValues):
                case nameof(MaxValues):
                    return shape;
                case nameof(ContourFeature):
                    return !shape;
                case nameof(Min1):
                    return !shape && ContourFeature != ContourFeature.closed;
                case nameof(Max1):
                    return !shape && ContourFeature != ContourFeature.open;
                case nameof(Min2):
                case nameof(Max2):
                    return !shape && ContourFeature == ContourFeature.curvature;
                case nameof(TakeIndex):
                    return TakeMode == ContourTakeMode.ByIndex;
                default:
                    return true;
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail(issue.Parameter == nameof(Features) ? "边缘选择未配置特征" : $"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject selected;
            string log;
            if (SelectBy == ContourSelectBy.Contour)
            {
                HObject xld = Input<HalconXld>(ctx, XldPath).Object;
                HOperatorSet.SelectContoursXld(xld, out selected, ContourFeature.ToString(), Min1, Max1, Min2, Max2);
                log = $"[边缘选择] 轮廓特征 {ContourFeature} Min1={Min1}, Max1={Max1}, Min2={Min2}, Max2={Max2}";
            }
            else
            {
                string[] features = HalconTupleConvert.SplitCsv(Features);
                if (!TryBuildRange(MinValues, Min, features.Length, "MinValues", out HTuple mins, out string error)
                    || !TryBuildRange(MaxValues, Max, features.Length, "MaxValues", out HTuple maxs, out error))
                {
                    return NodeResult.Fail(error);
                }
                HObject xld = Input<HalconXld>(ctx, XldPath).Object;
                HOperatorSet.SelectShapeXld(xld, out selected, features, Operation.ToString(), mins, maxs);
                log = $"[边缘选择] {Features} ∈ [{mins}, {maxs}]";
            }

            if (TakeMode != ContourTakeMode.All)
            {
                HObject taken = Take(selected, out string take);
                selected.Dispose();
                selected = taken;
                log += "，取 " + take;
            }
            return SetXldOutput(ctx, selected, log);
        }

        /// <summary>按取件方式取出一条；没有可取的轮廓（包括序号越界）时返回空对象。</summary>
        private HObject Take(HObject selected, out string description)
        {
            HOperatorSet.CountObj(selected, out HTuple countTuple);
            int count = countTuple.I;
            int pick = -1;
            switch (TakeMode)
            {
                case ContourTakeMode.First:
                    pick = count > 0 ? 0 : -1;
                    description = "第一条";
                    break;
                case ContourTakeMode.ByIndex:
                    pick = TakeIndex < count ? TakeIndex : -1;
                    description = $"第 {TakeIndex} 条（共 {count} 条）";
                    break;
                default:
                    if (count > 0)
                    {
                        HOperatorSet.LengthXld(selected, out HTuple lengths);
                        pick = 0;
                        for (int i = 1; i < count; i++)
                        {
                            bool better = TakeMode == ContourTakeMode.Longest ? lengths[i].D > lengths[pick].D : lengths[i].D < lengths[pick].D;
                            if (better)
                            {
                                pick = i;
                            }
                        }
                    }
                    description = TakeMode == ContourTakeMode.Longest ? "最长" : "最短";
                    break;
            }
            HObject result;
            if (pick < 0)
            {
                HOperatorSet.GenEmptyObj(out result);
            }
            else
            {
                HOperatorSet.SelectObj(selected, out result, pick + 1);
            }
            return result;
        }

        private static bool TryBuildRange(string list, double fallback, int featureCount, string name,
            out HTuple range, out string error)
        {
            range = new HTuple();
            error = null;
            string[] parts = HalconTupleConvert.SplitCsv(list);
            if (parts.Length == 0)
            {
                for (int i = 0; i < featureCount; i++)
                {
                    range = range.TupleConcat(fallback);
                }
                return true;
            }
            if (parts.Length != featureCount)
            {
                error = $"边缘选择 {name} 数量（{parts.Length}）与特征数量（{featureCount}）不一致";
                return false;
            }
            foreach (string part in parts)
            {
                if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double value))
                {
                    error = $"边缘选择 {name} 中的“{part}”不是有效数字";
                    return false;
                }
                range = range.TupleConcat(value);
            }
            return true;
        }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class ConcatXldTool : XldToolBase
    {
        [InputRef("XLD1", typeof(HalconXld))]
        public string XldPath1 { get; set; }

        [InputRef("XLD2", typeof(HalconXld))]
        public string XldPath2 { get; set; }

        public ConcatXldTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld1 = Input<HalconXld>(ctx, XldPath1).Object;
            HObject xld2 = Input<HalconXld>(ctx, XldPath2).Object;
            HOperatorSet.ConcatObj(xld1, xld2, out HObject output);
            return SetXldOutput(ctx, output, "[边缘合并]");
        }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class SegmentXldTool : XldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public XldSegmentMode Mode { get; set; } = XldSegmentMode.lines_circles;
        public int SmoothCont { get; set; } = 5;
        public double MaxLineDist1 { get; set; } = 4.0;
        public double MaxLineDist2 { get; set; } = 2.0;

        public SegmentXldTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            HOperatorSet.SegmentContoursXld(xld, out HObject output, Mode.ToString(), SmoothCont, MaxLineDist1, MaxLineDist2);
            return SetXldOutput(ctx, output, $"[XLD 分割] {Mode}");
        }
    }

    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstValue", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ObjectCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Features", VariableKind.Array, VariableType.Object, ElementClrType = typeof(FeatureValues),
        Members = new[] { "Name", "Count", "First", "Min", "Max", "Mean" })]
    public sealed class XldFeaturesTool : ToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Features { get; set; } = "contlength";

        public XldFeaturesTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            HOperatorSet.CountObj(xld, out HTuple objectCount);
            var features = new List<FeatureValues>();
            foreach (string feature in HalconTupleConvert.SplitCsv(Features))
            {
                features.Add(FeatureValues.Create(feature, GetXldFeature(xld, feature).ToList()));
            }

            FeatureOutput.Set(ctx, ModuleName, features, objectCount.I);
            ctx.AddLog(FlowLogLevel.Info, $"[XLD 特征值] {Features}，对象数 {objectCount.I}，输出 {features.Sum(f => f.Count)} 个数值");
            return NodeResult.Ok;
        }

        private static IEnumerable<double> GetXldFeature(HObject xld, string feature)
        {
            string normalized = (feature ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "contlength":
                case "length":
                    HOperatorSet.LengthXld(xld, out HTuple length);
                    return HalconTupleConvert.ToDoubles(length);
                case "area":
                case "row":
                case "column":
                    HOperatorSet.AreaCenterXld(xld, out HTuple area, out HTuple row, out HTuple column, out _);
                    if (normalized == "row") return HalconTupleConvert.ToDoubles(row);
                    if (normalized == "column") return HalconTupleConvert.ToDoubles(column);
                    return HalconTupleConvert.ToDoubles(area);
                case "point_num":
                case "points":
                    HOperatorSet.ContourPointNumXld(xld, out HTuple points);
                    return HalconTupleConvert.ToDoubles(points);
                // XG-04：与 select_shape_xld 同名的形状特征
                case "circularity":
                    HOperatorSet.CircularityXld(xld, out HTuple circularity);
                    return HalconTupleConvert.ToDoubles(circularity);
                case "compactness":
                    HOperatorSet.CompactnessXld(xld, out HTuple compactness);
                    return HalconTupleConvert.ToDoubles(compactness);
                case "convexity":
                    HOperatorSet.ConvexityXld(xld, out HTuple convexity);
                    return HalconTupleConvert.ToDoubles(convexity);
                case "anisometry":
                case "bulkiness":
                case "struct_factor":
                    HOperatorSet.EccentricityXld(xld, out HTuple anisometry, out HTuple bulkiness, out HTuple structFactor);
                    if (normalized == "anisometry") return HalconTupleConvert.ToDoubles(anisometry);
                    if (normalized == "bulkiness") return HalconTupleConvert.ToDoubles(bulkiness);
                    return HalconTupleConvert.ToDoubles(structFactor);
                case "orientation":
                    HOperatorSet.OrientationXld(xld, out HTuple orientation);
                    return HalconTupleConvert.ToDoubles(orientation);
                case "max_diameter":
                    HOperatorSet.DiameterXld(xld, out _, out _, out _, out _, out HTuple diameter);
                    return HalconTupleConvert.ToDoubles(diameter);
                case "rect2_phi":
                case "rect2_len1":
                case "rect2_len2":
                    HOperatorSet.SmallestRectangle2Xld(xld, out _, out _, out HTuple rectPhi, out HTuple len1, out HTuple len2);
                    if (normalized == "rect2_phi") return HalconTupleConvert.ToDoubles(rectPhi);
                    if (normalized == "rect2_len1") return HalconTupleConvert.ToDoubles(len1);
                    return HalconTupleConvert.ToDoubles(len2);
                case "outer_radius":
                    HOperatorSet.SmallestCircleXld(xld, out _, out _, out HTuple radius);
                    return HalconTupleConvert.ToDoubles(radius);
                case "ra":
                case "rb":
                case "phi":
                    HOperatorSet.EllipticAxisXld(xld, out HTuple ra, out HTuple rb, out HTuple phi);
                    if (normalized == "ra") return HalconTupleConvert.ToDoubles(ra);
                    if (normalized == "rb") return HalconTupleConvert.ToDoubles(rb);
                    return HalconTupleConvert.ToDoubles(phi);
                case "is_closed":
                    HOperatorSet.TestClosedXld(xld, out HTuple closed);
                    return HalconTupleConvert.ToDoubles(closed);
                default:
                    HOperatorSet.GetContourGlobalAttribXld(xld, feature, out HTuple attrib);
                    return HalconTupleConvert.ToDoubles(attrib);
            }
        }
    }

    public abstract class XldToolBase : ToolBase, INotFoundPolicy
    {
        protected XldToolBase(string moduleName) : base(moduleName)
        {
        }

        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false、Count=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        protected NodeResult SetXldOutput(FlowContext ctx, HObject xld, string logMessage)
        {
            HOperatorSet.CountObj(xld, out HTuple count);
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(xld), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, count.I > 0));
            if (count.I == 0)
            {
                return NotFoundOutcome.Resolve(ctx, this, logMessage + " 结果为空");
            }
            ctx.AddLog(FlowLogLevel.Info, $"{logMessage}，输出轮廓数={count.I}");
            return NodeResult.Ok;
        }
    }
}
