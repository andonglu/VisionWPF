using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>拟合形状（XG-07）。</summary>
    public enum FitShapeKind
    {
        Ellipse,
        Rectangle2
    }

    /// <summary>fit_ellipse_contour_xld 的算法（成员名即 HALCON 参数值，10 种均已实测可用）。</summary>
    public enum EllipseFitAlgorithm
    {
        fitzgibbon,
        fhuber,
        ftukey,
        geometric,
        geohuber,
        geotukey,
        voss,
        focpoints,
        fphuber,
        fptukey
    }

    /// <summary>fit_rectangle2_contour_xld 的算法（成员名即 HALCON 参数值）。</summary>
    public enum RectangleFitAlgorithm
    {
        regression,
        huber,
        tukey
    }

    /// <summary>一个轮廓的拟合结果：椭圆时 Length1 / Length2 为长、短半轴，矩形时为半边长；Phi 为弧度，已折算到 [-π/2, π/2)。</summary>
    public sealed class ShapeFitResult
    {
        public double Row { get; set; }
        public double Column { get; set; }
        public double Phi { get; set; }
        public double Length1 { get; set; }
        public double Length2 { get; set; }
    }

    /// <summary>
    /// 拟合椭圆/矩形（XG-07）：fit_ellipse_contour_xld / fit_rectangle2_contour_xld，每个轮廓一个结果。
    /// 椭圆与矩形都是 180° 对称，Phi 统一经 <see cref="AngleMath.Fold"/> 折算到 [-90°, 90°)（focpoints 等算法返回的角度会多 π）。
    /// 矩形拟合需要致密轮廓（只有角点的多边形会被 HALCON 拒绝），拟合失败时节点失败并给出原因。
    /// </summary>
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Results", VariableKind.Array, VariableType.Object, ElementClrType = typeof(ShapeFitResult),
        Members = new[] { "Row", "Column", "Phi", "Length1", "Length2" })]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Phi", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Length1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Length2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class FitEllipseRectTool : XldToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public FitShapeKind FitShape { get; set; } = FitShapeKind.Ellipse;
        public EllipseFitAlgorithm EllipseAlgorithm { get; set; } = EllipseFitAlgorithm.fitzgibbon;
        public RectangleFitAlgorithm RectangleAlgorithm { get; set; } = RectangleFitAlgorithm.tukey;
        /// <summary>参与拟合的最大点数，-1 表示全部。</summary>
        public int MaxNumPoints { get; set; } = -1;
        /// <summary>首尾距离不超过该值时视为闭合轮廓。</summary>
        public double MaxClosureDist { get; set; }
        /// <summary>两端忽略的点数。</summary>
        public int ClippingEndPoints { get; set; }
        /// <summary>voss 算法的查表大小。</summary>
        public int VossTabSize { get; set; } = 200;
        public int Iterations { get; set; } = 3;
        public double ClippingFactor { get; set; } = 2;

        public FitEllipseRectTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (MaxNumPoints != -1 && MaxNumPoints < 5)
            {
                yield return new ToolConfigurationIssue(nameof(MaxNumPoints), "必须为 -1（全部点）或不小于 5");
            }
            if (Iterations < 0)
            {
                yield return new ToolConfigurationIssue(nameof(Iterations), "不能小于 0");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(EllipseAlgorithm):
                    return FitShape == FitShapeKind.Ellipse;
                case nameof(VossTabSize):
                    return FitShape == FitShapeKind.Ellipse && EllipseAlgorithm == EllipseFitAlgorithm.voss;
                case nameof(RectangleAlgorithm):
                    return FitShape == FitShapeKind.Rectangle2;
                default:
                    return true;
            }
        }

        /// <summary>把弧度角折算到 [-π/2, π/2)。</summary>
        public static double NormalizePhi(double phi)
        {
            return AngleMath.ToRadians(AngleMath.Fold(AngleMath.ToDegrees(phi), -90, 90));
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            HTuple rows;
            HTuple columns;
            HTuple phis;
            HTuple lengths1;
            HTuple lengths2;
            string name = FitShape == FitShapeKind.Ellipse ? "拟合椭圆" : "拟合矩形";
            try
            {
                if (FitShape == FitShapeKind.Ellipse)
                {
                    HOperatorSet.FitEllipseContourXld(xld, EllipseAlgorithm.ToString(), MaxNumPoints, MaxClosureDist, ClippingEndPoints,
                        VossTabSize, Iterations, ClippingFactor, out rows, out columns, out phis, out lengths1, out lengths2, out _, out _, out _);
                }
                else
                {
                    HOperatorSet.FitRectangle2ContourXld(xld, RectangleAlgorithm.ToString(), MaxNumPoints, MaxClosureDist, ClippingEndPoints,
                        Iterations, ClippingFactor, out rows, out columns, out phis, out lengths1, out lengths2, out _);
                }
            }
            catch (HalconException ex)
            {
                string hint = FitShape == FitShapeKind.Rectangle2 ? "（矩形拟合需要沿各边致密分布的轮廓点，只有角点的多边形无法拟合）" : string.Empty;
                return NodeResult.Fail($"{ModuleName} {name}失败：{ex.Message}{hint}");
            }

            var results = new List<ShapeFitResult>();
            for (int i = 0; i < rows.Length; i++)
            {
                if (!(lengths1[i].D > 0) || !(lengths2[i].D > 0))
                {
                    // HALCON 22.11 实测：focpoints / fphuber / fptukey 在部分闭合轮廓上返回短半轴 0（计划第 10 节），不输出这种退化结果
                    bool focal = FitShape == FitShapeKind.Ellipse && (EllipseAlgorithm == EllipseFitAlgorithm.focpoints
                        || EllipseAlgorithm == EllipseFitAlgorithm.fphuber || EllipseAlgorithm == EllipseFitAlgorithm.fptukey);
                    return NodeResult.Fail($"{ModuleName} {name}结果退化：第 {i} 个轮廓的半轴长度为 {lengths1[i].D:G6} / {lengths2[i].D:G6}"
                        + (focal ? $"；{EllipseAlgorithm} 等焦点类算法在部分闭合轮廓上会返回短半轴 0，请改用 fitzgibbon / geometric 等算法" : string.Empty));
                }
                results.Add(new ShapeFitResult
                {
                    Row = rows[i].D,
                    Column = columns[i].D,
                    Phi = NormalizePhi(phis[i].D),
                    Length1 = lengths1[i].D,
                    Length2 = lengths2[i].D
                });
            }

            HObject shapes;
            if (results.Count == 0)
            {
                HOperatorSet.GenEmptyObj(out shapes);
            }
            else
            {
                var r = new HTuple(results.Select(x => x.Row).ToArray());
                var c = new HTuple(results.Select(x => x.Column).ToArray());
                var p = new HTuple(results.Select(x => x.Phi).ToArray());
                var l1 = new HTuple(results.Select(x => x.Length1).ToArray());
                var l2 = new HTuple(results.Select(x => x.Length2).ToArray());
                if (FitShape == FitShapeKind.Ellipse)
                {
                    HOperatorSet.GenEllipseContourXld(out shapes, r, c, p, l1, l2,
                        HTuple.TupleGenConst(results.Count, 0.0), HTuple.TupleGenConst(results.Count, 2 * Math.PI),
                        HTuple.TupleGenConst(results.Count, "positive"), 1.5);
                }
                else
                {
                    HOperatorSet.GenRectangle2ContourXld(out shapes, r, c, p, l1, l2);
                }
            }

            bool found = results.Count > 0;
            ShapeFitResult first = found ? results[0] : null;
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(shapes), results.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Results", VariableType.Object, results));
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, found ? first.Row : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, found ? first.Column : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Phi", VariableType.Double, found ? first.Phi : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Length1", VariableType.Double, found ? first.Length1 : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Length2", VariableType.Double, found ? first.Length2 : double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, results.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, found));
            if (!found)
            {
                return NotFoundOutcome.Resolve(ctx, this, $"{name}未找到结果（输入轮廓为空）");
            }
            ctx.AddLog(FlowLogLevel.Info, $"[{name}] {(FitShape == FitShapeKind.Ellipse ? EllipseAlgorithm.ToString() : RectangleAlgorithm.ToString())}，输出 {results.Count} 个");
            return NodeResult.Ok;
        }
    }
}
