using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>polar_trans_image_ext / polar_trans_region_inv 的插值（成员与顺序同 get_param_info 的 value_list，名称即 HALCON 参数值）。</summary>
    public enum PolarInterpolation
    {
        nearest_neighbor,
        bilinear
    }

    /// <summary>
    /// 极坐标展开实际使用的参数（IP-05 输出，IP-06 输入）：圆心、起止角（弧度，HALCON 方向约定）、起止半径、展开图尺寸、原图尺寸。
    /// 记录的是引用 / 跟随 / 自动计算之后的实际值，不是工具的配置值。
    /// </summary>
    public sealed class PolarParams
    {
        public double CenterRow { get; set; }
        public double CenterColumn { get; set; }
        /// <summary>起始角（弧度）。</summary>
        public double AngleStart { get; set; }
        /// <summary>终止角（弧度）。</summary>
        public double AngleEnd { get; set; }
        public double RadiusStart { get; set; }
        public double RadiusEnd { get; set; }
        /// <summary>展开图宽（列对应角度）。</summary>
        public int Width { get; set; }
        /// <summary>展开图高（行对应半径）。</summary>
        public int Height { get; set; }
        /// <summary>原图宽。</summary>
        public int ImageWidth { get; set; }
        /// <summary>原图高。</summary>
        public int ImageHeight { get; set; }
        public PolarInterpolation Interpolation { get; set; }

        public double AngleStartDeg => AngleStart * 180.0 / Math.PI;
        public double AngleEndDeg => AngleEnd * 180.0 / Math.PI;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "PolarParams[圆心 ({0:0.###}, {1:0.###})，角度 {2:0.###}° ~ {3:0.###}°，半径 {4:0.###} ~ {5:0.###}，展开图 {6}×{7}，原图 {8}×{9}]",
                CenterRow, CenterColumn, AngleStartDeg, AngleEndDeg, RadiusStart, RadiusEnd, Width, Height, ImageWidth, ImageHeight);
        }
    }

    /// <summary>
    /// 极坐标展开（IP-05）：把圆环展开成矩形（polar_trans_image_ext），列对应角度、行对应半径（IMAGE-TOOLS-PLAN 第 16 节实测）。
    /// 圆心来自圆心行、列引用（如圆形测量的 Row / Column），或固定参数并可按定位矩阵跟随；展开尺寸为 0 时按外圈周长与半径差自动计算。
    /// 输出展开图与实际使用的 PolarParams，供“极坐标逆变换”把展开图上的检测结果映射回原图。
    /// </summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("PolarParams", VariableKind.Object, VariableType.Object, ElementClrType = typeof(PolarParams))]
    public sealed class PolarUnwrapTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>展开图宽高上限（22.11 实测）。</summary>
        public const int MaxSize = 32768;

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>圆心行引用（可选，须与圆心列引用同时配置；配置后不用固定圆心）。</summary>
        [InputRef("圆心行", typeof(double), Optional = true)]
        public string CenterRowPath { get; set; }

        /// <summary>圆心列引用（可选）。</summary>
        [InputRef("圆心列", typeof(double), Optional = true)]
        public string CenterColumnPath { get; set; }

        /// <summary>定位矩阵（可选，跟随：固定圆心经矩阵变换、起止角加矩阵旋转量、半径乘矩阵缩放）；与圆心引用互斥。</summary>
        [InputRef("定位矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        /// <summary>固定圆心行（未配置圆心引用时使用）。</summary>
        public double CenterRow { get; set; } = 256;
        /// <summary>固定圆心列。</summary>
        public double CenterColumn { get; set; } = 256;
        /// <summary>起始角（度，逆时针为正，0° 指向列增大方向）。</summary>
        public double AngleStartDeg { get; set; }
        /// <summary>终止角（度）；小于起始角时按顺时针展开。</summary>
        public double AngleEndDeg { get; set; } = 360;
        /// <summary>起始半径（≥ 0），对应展开图第 0 行。</summary>
        public double RadiusStart { get; set; }
        /// <summary>终止半径（≥ 0，不能等于起始半径），对应展开图最后一行。</summary>
        public double RadiusEnd { get; set; } = 100;
        /// <summary>展开图宽（0 = 按外圈周长自动计算，否则 1 ~ 32768）。</summary>
        public int OutputWidth { get; set; }
        /// <summary>展开图高（0 = 按半径差自动计算，否则 1 ~ 32768）。</summary>
        public int OutputHeight { get; set; }
        /// <summary>插值方式。</summary>
        public PolarInterpolation Interpolation { get; set; } = PolarInterpolation.nearest_neighbor;

        public PolarUnwrapTool(string moduleName) : base(moduleName)
        {
        }

        private bool HasRowRef => !string.IsNullOrWhiteSpace(CenterRowPath);
        private bool HasColumnRef => !string.IsNullOrWhiteSpace(CenterColumnPath);
        private bool UsesCenterRefs => HasRowRef && HasColumnRef;

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (HasRowRef != HasColumnRef)
            {
                yield return new ToolConfigurationIssue("CenterRowPath / CenterColumnPath", $"圆心行、列引用须同时配置（当前只配置了{(HasRowRef ? "圆心行" : "圆心列")}）");
            }
            if ((HasRowRef || HasColumnRef) && !string.IsNullOrWhiteSpace(MatrixPath))
            {
                yield return new ToolConfigurationIssue(nameof(MatrixPath), "已配置圆心引用，不能同时配置定位矩阵（引用已给出当前圆心）");
            }
            if (!UsesCenterRefs)
            {
                if (!double.IsFinite(CenterRow))
                {
                    yield return new ToolConfigurationIssue(nameof(CenterRow), $"圆心行 CenterRow 必须是有限数（当前 {Format(CenterRow)}）");
                }
                if (!double.IsFinite(CenterColumn))
                {
                    yield return new ToolConfigurationIssue(nameof(CenterColumn), $"圆心列 CenterColumn 必须是有限数（当前 {Format(CenterColumn)}）");
                }
            }
            if (!double.IsFinite(AngleStartDeg))
            {
                yield return new ToolConfigurationIssue(nameof(AngleStartDeg), $"起始角 AngleStartDeg 必须是有限数（当前 {Format(AngleStartDeg)}）");
            }
            if (!double.IsFinite(AngleEndDeg))
            {
                yield return new ToolConfigurationIssue(nameof(AngleEndDeg), $"终止角 AngleEndDeg 必须是有限数（当前 {Format(AngleEndDeg)}）");
            }
            if (double.IsFinite(AngleStartDeg) && AngleStartDeg == AngleEndDeg)
            {
                yield return new ToolConfigurationIssue(nameof(AngleEndDeg), $"起止角 AngleStartDeg 与 AngleEndDeg 不能相同（当前都为 {Format(AngleEndDeg)}）");
            }
            if (!(RadiusStart >= 0) || double.IsInfinity(RadiusStart))
            {
                yield return new ToolConfigurationIssue(nameof(RadiusStart), $"起始半径 RadiusStart 不能小于 0（当前 {Format(RadiusStart)}）");
            }
            if (!(RadiusEnd >= 0) || double.IsInfinity(RadiusEnd))
            {
                yield return new ToolConfigurationIssue(nameof(RadiusEnd), $"终止半径 RadiusEnd 不能小于 0（当前 {Format(RadiusEnd)}）");
            }
            else if (RadiusStart == RadiusEnd)
            {
                yield return new ToolConfigurationIssue(nameof(RadiusEnd), $"起止半径 RadiusStart 与 RadiusEnd 不能相同（当前都为 {Format(RadiusEnd)}）");
            }
            if (OutputWidth < 0 || OutputWidth > MaxSize)
            {
                yield return new ToolConfigurationIssue(nameof(OutputWidth), $"展开宽度 OutputWidth 必须为 0（自动）或 1 ~ {MaxSize}（当前 {OutputWidth}）");
            }
            if (OutputHeight < 0 || OutputHeight > MaxSize)
            {
                yield return new ToolConfigurationIssue(nameof(OutputHeight), $"展开高度 OutputHeight 必须为 0（自动）或 1 ~ {MaxSize}（当前 {OutputHeight}）");
            }
            if (!Enum.IsDefined(typeof(PolarInterpolation), Interpolation))
            {
                yield return new ToolConfigurationIssue(nameof(Interpolation), $"插值方式 Interpolation 只能是 nearest_neighbor / bilinear（当前 {(int)Interpolation}）");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(CenterRow):
                case nameof(CenterColumn):
                    // 配置了圆心引用时固定圆心不参与运行
                    return !UsesCenterRefs;
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
            HOperatorSet.GetImageSize(image, out HTuple widthTuple, out HTuple heightTuple);
            int imageWidth = widthTuple[0].I, imageHeight = heightTuple[0].I;

            double row, column, rotation = 0, scale = 1;
            string source;
            if (UsesCenterRefs)
            {
                row = Input<double>(ctx, CenterRowPath);
                column = Input<double>(ctx, CenterColumnPath);
                if (!double.IsFinite(row) || !double.IsFinite(column))
                {
                    return NodeResult.Fail($"{ModuleName} 圆心引用的值无效（{CenterRowPath} = {Format(row)}，{CenterColumnPath} = {Format(column)}），上游可能未找到圆");
                }
                source = $"圆心引用 {CenterRowPath} / {CenterColumnPath}";
            }
            else
            {
                if (!FollowMatrixResolver.TryResolve(ctx, ModuleName, MatrixPath, out List<HomMat2D> matrices, out string matrixError))
                {
                    return NodeResult.Fail(matrixError);
                }
                if (matrices.Count > 1)
                {
                    return NodeResult.Fail($"{ModuleName} 的定位矩阵只支持单个矩阵（当前为 {matrices.Count} 个）；多个工件请放在 For 循环中引用 Loop.Current.HomMat");
                }
                HomMat2D matrix = matrices[0];
                row = CenterRow;
                column = CenterColumn;
                source = "固定圆心";
                if (matrix != null)
                {
                    double[] m = matrix.Data.ToDArr();
                    double determinant = m[0] * m[4] - m[1] * m[3];
                    if (!(determinant > 0))
                    {
                        return NodeResult.Fail($"{ModuleName} 定位矩阵 {MatrixPath} 含镜像或退化（行列式 {Format(determinant)}），极坐标展开的角度方向无法跟随");
                    }
                    matrix.TransformPoint(CenterRow, CenterColumn, out row, out column);
                    rotation = matrix.RotationAngle;
                    scale = matrix.ScaleFactor;
                    source = $"固定圆心经定位矩阵 {MatrixPath} 跟随";
                }
            }

            var parameters = new PolarParams
            {
                CenterRow = row,
                CenterColumn = column,
                AngleStart = AngleStartDeg * Math.PI / 180.0 + rotation,
                AngleEnd = AngleEndDeg * Math.PI / 180.0 + rotation,
                RadiusStart = RadiusStart * scale,
                RadiusEnd = RadiusEnd * scale,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
                Interpolation = Interpolation
            };
            // 自动尺寸（第 16 节）：宽 = |Δθ| × 外半径、高 = |ΔR|，外圈与径向采样间距约 1 像素
            double autoWidth = Math.Round(Math.Abs(parameters.AngleEnd - parameters.AngleStart) * Math.Max(parameters.RadiusStart, parameters.RadiusEnd));
            double autoHeight = Math.Round(Math.Abs(parameters.RadiusEnd - parameters.RadiusStart));
            double width = OutputWidth > 0 ? OutputWidth : Math.Max(1, autoWidth);
            double height = OutputHeight > 0 ? OutputHeight : Math.Max(1, autoHeight);
            if (width > MaxSize)
            {
                return NodeResult.Fail($"{ModuleName} OutputWidth：自动计算的展开宽度 {width:0}（角度跨度 {Format(Math.Abs(AngleEndDeg - AngleStartDeg))}° × 外半径 {Format(Math.Max(parameters.RadiusStart, parameters.RadiusEnd))}）超过 {MaxSize}，请填写 OutputWidth");
            }
            if (height > MaxSize)
            {
                return NodeResult.Fail($"{ModuleName} OutputHeight：自动计算的展开高度 {height:0}（半径差）超过 {MaxSize}，请填写 OutputHeight");
            }
            parameters.Width = (int)width;
            parameters.Height = (int)height;

            HObject polar;
            try
            {
                HOperatorSet.PolarTransImageExt(image, out polar, parameters.CenterRow, parameters.CenterColumn, parameters.AngleStart, parameters.AngleEnd,
                    parameters.RadiusStart, parameters.RadiusEnd, parameters.Width, parameters.Height, Interpolation.ToString());
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 极坐标展开失败（{parameters}，插值 {Interpolation}）：{ex.Message}");
            }
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(polar), 1));
            SetOutput(ctx, Variable.Object(ModuleName, "PolarParams", parameters, 1));
            ctx.AddLog(FlowLogLevel.Info, $"[极坐标展开] {source}，{parameters}，插值 {Interpolation}");
            return NodeResult.Ok;
        }

        private static string Format(double value)
        {
            return value.ToString("G", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 极坐标逆变换（IP-06）：把展开图上的区域 / XLD 映射回原图（polar_trans_region_inv / polar_trans_contour_xld_inv），
    /// 参数取自“极坐标展开”输出的 PolarParams。输出直接在原图坐标系（第 16 节实测可与原图区域求交）。
    /// 配置区域时 Region / Count / Found 沿用 RegionOutput 的既有机制；只配置 XLD 时 Count / Found 取轮廓数；未配置的一侧输出空对象。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class PolarInverseTool : ToolBase, INotFoundPolicy, IToolConfigurationCheck
    {
        [InputRef("极坐标参数", typeof(PolarParams))]
        public string PolarParamsPath { get; set; }

        [InputRef("区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        [InputRef("XLD", typeof(HalconXld), Optional = true)]
        public string XldPath { get; set; }

        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public PolarInverseTool(string moduleName) : base(moduleName)
        {
        }

        private bool HasRegion => !string.IsNullOrWhiteSpace(RegionPath);
        private bool HasXld => !string.IsNullOrWhiteSpace(XldPath);

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (string.IsNullOrWhiteSpace(PolarParamsPath))
            {
                yield return new ToolConfigurationIssue(nameof(PolarParamsPath), "需要配置极坐标参数（引用“极坐标展开”输出的 PolarParams）");
            }
            if (!HasRegion && !HasXld)
            {
                yield return new ToolConfigurationIssue("RegionPath / XldPath", "区域与 XLD 至少配置一个");
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }
            PolarParams p = Input<PolarParams>(ctx, PolarParamsPath);
            if (p == null || p.Width < 1 || p.Height < 1 || p.ImageWidth < 1 || p.ImageHeight < 1)
            {
                return NodeResult.Fail($"{ModuleName} 极坐标参数 {PolarParamsPath} 无效（{p?.ToString() ?? "null"}）");
            }

            HObject region = null, xld = null;
            try
            {
                if (HasRegion)
                {
                    HObject polarRegion = Input<HalconRegion>(ctx, RegionPath).Object;
                    HOperatorSet.PolarTransRegionInv(polarRegion, out region, p.CenterRow, p.CenterColumn, p.AngleStart, p.AngleEnd,
                        p.RadiusStart, p.RadiusEnd, p.Width, p.Height, p.ImageWidth, p.ImageHeight, "nearest_neighbor");
                }
                if (HasXld)
                {
                    HObject polarXld = Input<HalconXld>(ctx, XldPath).Object;
                    HOperatorSet.PolarTransContourXldInv(polarXld, out xld, p.CenterRow, p.CenterColumn, p.AngleStart, p.AngleEnd,
                        p.RadiusStart, p.RadiusEnd, p.Width, p.Height, p.ImageWidth, p.ImageHeight);
                }
            }
            catch (HalconException ex)
            {
                region?.Dispose();
                xld?.Dispose();
                return NodeResult.Fail($"{ModuleName} 极坐标逆变换失败（{p}）：{ex.Message}");
            }

            string detail = $"{(HasRegion ? "区域 " + RegionPath : string.Empty)}{(HasRegion && HasXld ? "，" : string.Empty)}{(HasXld ? "XLD " + XldPath : string.Empty)}，{p}";
            int xldCount = 0;
            if (xld != null)
            {
                HOperatorSet.CountObj(xld, out HTuple count);
                xldCount = count.I;
                SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(xld), xldCount));
            }
            else
            {
                HOperatorSet.GenEmptyObj(out HObject emptyXld);
                SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(emptyXld), 0));
            }
            if (region != null)
            {
                return RegionOutput.Set(ctx, ModuleName, this, region, "极坐标逆变换", detail);
            }

            HOperatorSet.GenEmptyObj(out HObject emptyRegion);
            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(emptyRegion), 0));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, xldCount));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, xldCount > 0));
            if (xldCount == 0)
            {
                return NotFoundOutcome.Resolve(ctx, this, $"极坐标逆变换结果为空（{detail}）");
            }
            ctx.AddLog(FlowLogLevel.Info, $"[极坐标逆变换] {detail}，轮廓数 {xldCount}");
            return NodeResult.Ok;
        }
    }
}
