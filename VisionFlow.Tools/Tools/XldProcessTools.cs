using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>XLD 处理方式（XG-06）。</summary>
    public enum XldProcessOp
    {
        /// <summary>平滑（smooth_contours_xld）。</summary>
        Smooth,
        /// <summary>闭合（close_contours_xld）。</summary>
        Close,
        /// <summary>按矩形裁剪（clip_contours_xld）。</summary>
        Clip,
        /// <summary>形状转换（shape_trans_xld）。</summary>
        ShapeTrans,
        /// <summary>排序（sort_contours_xld）。</summary>
        Sort,
        /// <summary>连接相邻轮廓（union_adjacent_contours_xld）。</summary>
        UnionAdjacent,
        /// <summary>连接共线轮廓（union_collinear_contours_xld）。</summary>
        UnionCollinear,
        /// <summary>连接共圆轮廓（union_cocircular_contours_xld）。</summary>
        UnionCocircular,
        /// <summary>仿射跟随（affine_trans_contour_xld，读取定位矩阵）。</summary>
        AffineTrans
    }

    /// <summary>shape_trans_xld 的形状（成员名即 HALCON 参数值）。</summary>
    public enum XldShapeType
    {
        convex,
        ellipse,
        rectangle1,
        rectangle2,
        outer_circle
    }

    /// <summary>sort_contours_xld 的排序方式（成员名即 HALCON 参数值）。</summary>
    public enum XldSortMode
    {
        upper_left,
        upper_right,
        lower_left,
        lower_right,
        character
    }

    /// <summary>排序时优先比较行还是列。</summary>
    public enum XldSortRowOrCol
    {
        row,
        column
    }

    /// <summary>合并轮廓时属性的处理方式（成员名即 HALCON 参数值）。</summary>
    public enum XldAttrMode
    {
        attr_keep,
        attr_forget
    }

    /// <summary>
    /// XLD 处理（XG-06）：一个 XLD 进、XLD 出，对应 Region 的“区域处理”。
    /// 角度类参数在界面上按“度”填写，内部经 <see cref="AngleMath.ToRadians"/> 转为弧度；默认值等于 HALCON 默认值。
    /// 仿射跟随必须配置定位矩阵，矩阵无效或为多个时明确失败，不退回原位置。
    /// </summary>
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class XldProcessTool : XldToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        /// <summary>仿射跟随使用的定位矩阵（如区域定位、匹配输出的矩阵）；其他方式不读取。</summary>
        [InputRef("定位矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        public XldProcessOp Method { get; set; } = XldProcessOp.Smooth;

        /// <summary>平滑时参与回归的点数（奇数，≥ 3）。</summary>
        public int NumRegrPoints { get; set; } = 5;

        public double ClipRow1 { get; set; }
        public double ClipColumn1 { get; set; }
        public double ClipRow2 { get; set; } = 512;
        public double ClipColumn2 { get; set; } = 512;

        public XldShapeType ShapeType { get; set; } = XldShapeType.convex;

        public XldSortMode SortMode { get; set; } = XldSortMode.upper_left;
        public bool SortAscending { get; set; } = true;
        public XldSortRowOrCol RowOrCol { get; set; } = XldSortRowOrCol.row;

        /// <summary>相邻 / 共线连接的最大绝对距离（像素）。</summary>
        public double MaxDistAbs { get; set; } = 10;
        /// <summary>相邻 / 共线连接的最大相对距离（相对于较短轮廓长度）。</summary>
        public double MaxDistRel { get; set; } = 1;
        /// <summary>共线连接的最大横向偏移（像素）。</summary>
        public double MaxShift { get; set; } = 2;
        /// <summary>共线连接的最大夹角（度）。</summary>
        public double MaxAngleDeg { get; set; } = AngleMath.ToDegrees(0.1);
        public XldAttrMode AttrMode { get; set; } = XldAttrMode.attr_keep;

        /// <summary>共圆连接：圆弧角度差上限（度）。</summary>
        public double MaxArcAngleDiffDeg { get; set; } = AngleMath.ToDegrees(0.5);
        /// <summary>共圆连接：圆弧重叠上限（度）。</summary>
        public double MaxArcOverlapDeg { get; set; } = AngleMath.ToDegrees(0.1);
        /// <summary>共圆连接：切线夹角上限（度）。</summary>
        public double MaxTangentAngleDeg { get; set; } = AngleMath.ToDegrees(0.2);
        /// <summary>共圆连接：端点最大距离（像素）。</summary>
        public double MaxDist { get; set; } = 30;
        public double MaxRadiusDiff { get; set; } = 10;
        public double MaxCenterDist { get; set; } = 10;
        public bool MergeSmallContours { get; set; } = true;
        public int Iterations { get; set; } = 1;

        public XldProcessTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (Method == XldProcessOp.Smooth && (NumRegrPoints < 3 || NumRegrPoints % 2 == 0))
            {
                yield return new ToolConfigurationIssue(nameof(NumRegrPoints), "平滑点数必须是不小于 3 的奇数");
            }
            if (Method == XldProcessOp.Clip && (ClipRow2 <= ClipRow1 || ClipColumn2 <= ClipColumn1))
            {
                yield return new ToolConfigurationIssue("ClipRow / ClipColumn", "裁剪范围无效：右下角必须大于左上角");
            }
            if (Method == XldProcessOp.UnionCocircular && Iterations < 1)
            {
                yield return new ToolConfigurationIssue(nameof(Iterations), "迭代次数必须大于 0");
            }
            if (Method == XldProcessOp.AffineTrans && string.IsNullOrWhiteSpace(MatrixPath))
            {
                yield return new ToolConfigurationIssue("定位矩阵", "仿射跟随需要指定定位矩阵");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(MatrixPath):
                    return Method == XldProcessOp.AffineTrans;
                case nameof(NumRegrPoints):
                    return Method == XldProcessOp.Smooth;
                case nameof(ClipRow1):
                case nameof(ClipColumn1):
                case nameof(ClipRow2):
                case nameof(ClipColumn2):
                    return Method == XldProcessOp.Clip;
                case nameof(ShapeType):
                    return Method == XldProcessOp.ShapeTrans;
                case nameof(SortMode):
                case nameof(SortAscending):
                case nameof(RowOrCol):
                    return Method == XldProcessOp.Sort;
                case nameof(MaxDistAbs):
                case nameof(MaxDistRel):
                case nameof(AttrMode):
                    return Method == XldProcessOp.UnionAdjacent || Method == XldProcessOp.UnionCollinear;
                case nameof(MaxShift):
                case nameof(MaxAngleDeg):
                    return Method == XldProcessOp.UnionCollinear;
                case nameof(MaxArcAngleDiffDeg):
                case nameof(MaxArcOverlapDeg):
                case nameof(MaxTangentAngleDeg):
                case nameof(MaxDist):
                case nameof(MaxRadiusDiff):
                case nameof(MaxCenterDist):
                case nameof(MergeSmallContours):
                case nameof(Iterations):
                    return Method == XldProcessOp.UnionCocircular;
                default:
                    return true;
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail(issue.Parameter == "定位矩阵" ? issue.Message : $"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HTuple matrixData = null;
            if (Method == XldProcessOp.AffineTrans)
            {
                if (!FollowMatrixResolver.TryResolve(ctx, ModuleName, MatrixPath, out List<HomMat2D> matrices, out string matrixError))
                {
                    return NodeResult.Fail(matrixError);
                }
                if (matrices.Count > 1)
                {
                    return NodeResult.Fail($"{ModuleName} 的定位矩阵只支持单个矩阵（当前为 {matrices.Count} 个）；多个工件请放在 For 循环中引用 Loop.Current.HomMat");
                }
                matrixData = matrices[0]?.Data;
            }

            HObject input = Input<HalconXld>(ctx, XldPath).Object;
            HObject output;
            string detail;
            switch (Method)
            {
                case XldProcessOp.Close:
                    HOperatorSet.CloseContoursXld(input, out output);
                    detail = "闭合";
                    break;
                case XldProcessOp.Clip:
                    HOperatorSet.ClipContoursXld(input, out output, ClipRow1, ClipColumn1, ClipRow2, ClipColumn2);
                    detail = $"裁剪 ({ClipRow1}, {ClipColumn1}) - ({ClipRow2}, {ClipColumn2})";
                    break;
                case XldProcessOp.ShapeTrans:
                    HOperatorSet.ShapeTransXld(input, out output, ShapeType.ToString());
                    detail = "形状转换 " + ShapeType;
                    break;
                case XldProcessOp.Sort:
                    HOperatorSet.SortContoursXld(input, out output, SortMode.ToString(), SortAscending ? "true" : "false", RowOrCol.ToString());
                    detail = $"排序 {SortMode}, {(SortAscending ? "升序" : "降序")}, {RowOrCol}";
                    break;
                case XldProcessOp.UnionAdjacent:
                    HOperatorSet.UnionAdjacentContoursXld(input, out output, MaxDistAbs, MaxDistRel, AttrMode.ToString());
                    detail = $"连接相邻 MaxDistAbs={MaxDistAbs}, MaxDistRel={MaxDistRel}";
                    break;
                case XldProcessOp.UnionCollinear:
                    HOperatorSet.UnionCollinearContoursXld(input, out output, MaxDistAbs, MaxDistRel, MaxShift,
                        AngleMath.ToRadians(MaxAngleDeg), AttrMode.ToString());
                    detail = $"连接共线 MaxDistAbs={MaxDistAbs}, MaxShift={MaxShift}, MaxAngle={MaxAngleDeg:F2}°";
                    break;
                case XldProcessOp.UnionCocircular:
                    HOperatorSet.UnionCocircularContoursXld(input, out output,
                        AngleMath.ToRadians(MaxArcAngleDiffDeg), AngleMath.ToRadians(MaxArcOverlapDeg), AngleMath.ToRadians(MaxTangentAngleDeg),
                        MaxDist, MaxRadiusDiff, MaxCenterDist, MergeSmallContours ? "true" : "false", Iterations);
                    detail = $"连接共圆 MaxDist={MaxDist}, MaxRadiusDiff={MaxRadiusDiff}";
                    break;
                case XldProcessOp.AffineTrans:
                    if (matrixData == null)
                    {
                        // 只在预览上下文显式允许降级时出现（FollowMatrixResolver 已记录警告）：保持原位置
                        HOperatorSet.CopyObj(input, out output, 1, -1);
                        detail = "仿射跟随（预览降级，保持原位置）";
                        break;
                    }
                    HOperatorSet.AffineTransContourXld(input, out output, matrixData);
                    detail = "仿射跟随 " + MatrixPath;
                    break;
                default:
                    HOperatorSet.SmoothContoursXld(input, out output, NumRegrPoints);
                    detail = $"平滑 {NumRegrPoints} 点";
                    break;
            }
            return SetXldOutput(ctx, output, "[XLD 处理] " + detail);
        }
    }
}
