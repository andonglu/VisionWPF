using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>图像几何变换方式（按数字保存；ZoomFactor 在首位即默认）。</summary>
    public enum ImageGeometryMethod
    {
        ZoomFactor,
        ZoomSize,
        Rotate,
        Mirror,
        CropRectangle,
        CropRegion
    }

    /// <summary>zoom_image_factor / zoom_image_size / rotate_image 的插值（成员与顺序同 get_param_info 的 value_list，名称即 HALCON 参数值）。</summary>
    public enum GeometryInterpolation
    {
        nearest_neighbor,
        bilinear,
        bicubic,
        constant,
        weighted
    }

    /// <summary>mirror_image 的 Mode（名称即 HALCON 参数值）。</summary>
    public enum ImageMirrorMode
    {
        row,
        column,
        diagonal
    }

    /// <summary>
    /// 图像几何变换（IP-04）：缩放、旋转、镜像、矩形裁剪、按区域裁剪。输出新图像，以及原图坐标 → 新图坐标的 HomMat 与其逆 InverseHomMat，
    /// 便于把新图上的检测结果映射回原图。坐标映射按 IMAGE-TOOLS-PLAN 第 14 节的 22.11 实测构造：缩放为像素中心约定
    /// r' = (r + 0.5) × s − 0.5（s 取实际输出尺寸比）；旋转绕 ((h − 1) / 2, (w − 1) / 2)、映射到输出图的 ((h₂ − 1) / 2, (w₂ − 1) / 2)；
    /// 镜像 row / column / diagonal 分别为 (h − 1 − r, c) / (r, w − 1 − c) / (c, r)；裁剪为平移 (−r1, −c1)。
    /// </summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("HomMat", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("InverseHomMat", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    public sealed class ImageGeometryTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>zoom_image_factor / zoom_image_size 允许的最大输出宽高（22.11 实测）。</summary>
        public const int MaxImageSize = 32768;

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>裁剪区域（仅 CropRegion 使用且必填；多个区域对象先合并）。</summary>
        [InputRef("裁剪区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        /// <summary>变换方式。</summary>
        public ImageGeometryMethod Method { get; set; } = ImageGeometryMethod.ZoomFactor;
        /// <summary>宽度缩放系数（ZoomFactor，> 0）。</summary>
        public double ScaleWidth { get; set; } = 0.5;
        /// <summary>高度缩放系数（ZoomFactor，> 0）。</summary>
        public double ScaleHeight { get; set; } = 0.5;
        /// <summary>目标宽度（ZoomSize，1 ~ 32768）。</summary>
        public int TargetWidth { get; set; } = 512;
        /// <summary>目标高度（ZoomSize，1 ~ 32768）。</summary>
        public int TargetHeight { get; set; } = 512;
        /// <summary>旋转角度（Rotate，度，逆时针为正）。</summary>
        public double AngleDeg { get; set; } = 90;
        /// <summary>插值方式（ZoomFactor / ZoomSize / Rotate）。</summary>
        public GeometryInterpolation Interpolation { get; set; } = GeometryInterpolation.constant;
        /// <summary>镜像方式（Mirror）。</summary>
        public ImageMirrorMode MirrorMode { get; set; } = ImageMirrorMode.row;
        /// <summary>裁剪矩形左上角行（CropRectangle，≥ 0）。</summary>
        public int Row1 { get; set; } = 100;
        /// <summary>裁剪矩形左上角列（CropRectangle，≥ 0）。</summary>
        public int Column1 { get; set; } = 100;
        /// <summary>裁剪矩形右下角行（CropRectangle，≥ Row1；超出图像时超出部分不在定义域内）。</summary>
        public int Row2 { get; set; } = 200;
        /// <summary>裁剪矩形右下角列（CropRectangle，≥ Column1）。</summary>
        public int Column2 { get; set; } = 200;

        public ImageGeometryTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            switch (Method)
            {
                case ImageGeometryMethod.ZoomFactor:
                    if (!(ScaleWidth > 0) || double.IsInfinity(ScaleWidth))
                    {
                        yield return new ToolConfigurationIssue(nameof(ScaleWidth), $"宽度缩放系数 ScaleWidth 必须大于 0（当前 {Format(ScaleWidth)}）");
                    }
                    if (!(ScaleHeight > 0) || double.IsInfinity(ScaleHeight))
                    {
                        yield return new ToolConfigurationIssue(nameof(ScaleHeight), $"高度缩放系数 ScaleHeight 必须大于 0（当前 {Format(ScaleHeight)}）");
                    }
                    break;
                case ImageGeometryMethod.ZoomSize:
                    if (TargetWidth < 1 || TargetWidth > MaxImageSize)
                    {
                        yield return new ToolConfigurationIssue(nameof(TargetWidth), $"目标宽度 TargetWidth 必须在 1 ~ {MaxImageSize} 之间（当前 {TargetWidth}）");
                    }
                    if (TargetHeight < 1 || TargetHeight > MaxImageSize)
                    {
                        yield return new ToolConfigurationIssue(nameof(TargetHeight), $"目标高度 TargetHeight 必须在 1 ~ {MaxImageSize} 之间（当前 {TargetHeight}）");
                    }
                    break;
                case ImageGeometryMethod.Rotate:
                    if (!double.IsFinite(AngleDeg))
                    {
                        yield return new ToolConfigurationIssue(nameof(AngleDeg), $"旋转角度 AngleDeg 必须是有限数（当前 {Format(AngleDeg)}）");
                    }
                    break;
                case ImageGeometryMethod.Mirror:
                    if (!Enum.IsDefined(typeof(ImageMirrorMode), MirrorMode))
                    {
                        yield return new ToolConfigurationIssue(nameof(MirrorMode), $"镜像方式 MirrorMode 只能是 row / column / diagonal（当前 {(int)MirrorMode}）");
                    }
                    break;
                case ImageGeometryMethod.CropRectangle:
                    if (Row1 < 0)
                    {
                        yield return new ToolConfigurationIssue(nameof(Row1), $"裁剪起点 Row1 不能小于 0（当前 {Row1}）");
                    }
                    if (Column1 < 0)
                    {
                        yield return new ToolConfigurationIssue(nameof(Column1), $"裁剪起点 Column1 不能小于 0（当前 {Column1}）");
                    }
                    if (Row2 < Row1)
                    {
                        yield return new ToolConfigurationIssue(nameof(Row2), $"裁剪终点 Row2 不能小于 Row1（当前 Row1 = {Row1}，Row2 = {Row2}）");
                    }
                    if (Column2 < Column1)
                    {
                        yield return new ToolConfigurationIssue(nameof(Column2), $"裁剪终点 Column2 不能小于 Column1（当前 Column1 = {Column1}，Column2 = {Column2}）");
                    }
                    break;
                case ImageGeometryMethod.CropRegion:
                    if (string.IsNullOrWhiteSpace(RegionPath))
                    {
                        yield return new ToolConfigurationIssue(nameof(RegionPath), "CropRegion 方式需要配置裁剪区域");
                    }
                    break;
                default:
                    yield return new ToolConfigurationIssue(nameof(Method), $"未知的几何变换方式 {(int)Method}");
                    break;
            }
            if (UsesInterpolation && !Enum.IsDefined(typeof(GeometryInterpolation), Interpolation))
            {
                yield return new ToolConfigurationIssue(nameof(Interpolation), $"插值方式 Interpolation 只能是 nearest_neighbor / bilinear / bicubic / constant / weighted（当前 {(int)Interpolation}）");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(ScaleWidth):
                case nameof(ScaleHeight):
                    return Method == ImageGeometryMethod.ZoomFactor;
                case nameof(TargetWidth):
                case nameof(TargetHeight):
                    return Method == ImageGeometryMethod.ZoomSize;
                case nameof(AngleDeg):
                    return Method == ImageGeometryMethod.Rotate;
                case nameof(Interpolation):
                    return UsesInterpolation;
                case nameof(MirrorMode):
                    return Method == ImageGeometryMethod.Mirror;
                case nameof(Row1):
                case nameof(Column1):
                case nameof(Row2):
                case nameof(Column2):
                    return Method == ImageGeometryMethod.CropRectangle;
                case nameof(RegionPath):
                    return Method == ImageGeometryMethod.CropRegion;
                default:
                    return true;
            }
        }

        private bool UsesInterpolation => Method == ImageGeometryMethod.ZoomFactor || Method == ImageGeometryMethod.ZoomSize
            || Method == ImageGeometryMethod.Rotate;

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.GetImageSize(image, out HTuple widthTuple, out HTuple heightTuple);
            int width = widthTuple[0].I, height = heightTuple[0].I;
            HObject output = null;
            HObject region = null;
            try
            {
                string inputError = CheckAgainstImage(width, height);
                if (inputError != null)
                {
                    return NodeResult.Fail($"{ModuleName} {inputError}");
                }
                if (Method == ImageGeometryMethod.CropRegion)
                {
                    HObject source = Input<HalconRegion>(ctx, RegionPath).Object;
                    HOperatorSet.Union1(source, out region);
                }
                double[] matrix = Transform(image, region, width, height, out output, out string detail, out string error);
                if (error != null)
                {
                    return NodeResult.Fail($"{ModuleName} {error}");
                }
                HOperatorSet.HomMat2dInvert(new HTuple(matrix), out HTuple inverse);
                HOperatorSet.GetImageSize(output, out HTuple newWidth, out HTuple newHeight);
                SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(output), 1));
                output = null;
                SetOutput(ctx, Variable.Object(ModuleName, "HomMat", new HomMat2D(new HTuple(matrix)), 1));
                SetOutput(ctx, Variable.Object(ModuleName, "InverseHomMat", new HomMat2D(inverse), 1));
                ctx.AddLog(FlowLogLevel.Info, $"[图像几何变换] Method={Method}, {detail}，输出 {newWidth[0].I}×{newHeight[0].I}");
                return NodeResult.Ok;
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 图像几何变换 {Method} 失败（{ParameterText()}，图像 {width}×{height}）：{ex.Message}");
            }
            finally
            {
                output?.Dispose();
                region?.Dispose();
            }
        }

        /// <summary>随图像尺寸变化的约束（第 14 节）：缩放后宽高 floor(原尺寸 × 系数 + 0.5) 须在 1 ~ 32768；裁剪起点须在图像内。</summary>
        private string CheckAgainstImage(int width, int height)
        {
            if (Method == ImageGeometryMethod.ZoomFactor)
            {
                double zoomedWidth = Math.Floor(width * ScaleWidth + 0.5), zoomedHeight = Math.Floor(height * ScaleHeight + 0.5);
                if (zoomedWidth < 1 || zoomedWidth > MaxImageSize)
                {
                    return $"ScaleWidth：宽度缩放系数 ScaleWidth = {Format(ScaleWidth)} 使缩放后宽度为 {zoomedWidth:0}（原宽 {width}），须在 1 ~ {MaxImageSize} 之间";
                }
                if (zoomedHeight < 1 || zoomedHeight > MaxImageSize)
                {
                    return $"ScaleHeight：高度缩放系数 ScaleHeight = {Format(ScaleHeight)} 使缩放后高度为 {zoomedHeight:0}（原高 {height}），须在 1 ~ {MaxImageSize} 之间";
                }
            }
            if (Method == ImageGeometryMethod.CropRectangle)
            {
                if (Row1 >= height)
                {
                    return $"Row1：裁剪起点 Row1 = {Row1} 超出图像（高 {height}，行号 0 ~ {height - 1}）";
                }
                if (Column1 >= width)
                {
                    return $"Column1：裁剪起点 Column1 = {Column1} 超出图像（宽 {width}，列号 0 ~ {width - 1}）";
                }
            }
            return null;
        }

        /// <summary>执行变换并返回原图坐标 → 新图坐标的 HomMat（[a, b, tx, c, d, ty]，作用于 (行, 列)）。</summary>
        private double[] Transform(HObject image, HObject region, int width, int height, out HObject output, out string detail, out string error)
        {
            error = null;
            switch (Method)
            {
                case ImageGeometryMethod.ZoomFactor:
                case ImageGeometryMethod.ZoomSize:
                {
                    if (Method == ImageGeometryMethod.ZoomFactor)
                    {
                        HOperatorSet.ZoomImageFactor(image, out output, ScaleWidth, ScaleHeight, Interpolation.ToString());
                        detail = $"ScaleWidth={Format(ScaleWidth)}, ScaleHeight={Format(ScaleHeight)}, Interpolation={Interpolation}";
                    }
                    else
                    {
                        HOperatorSet.ZoomImageSize(image, out output, TargetWidth, TargetHeight, Interpolation.ToString());
                        detail = $"TargetWidth={TargetWidth}, TargetHeight={TargetHeight}, Interpolation={Interpolation}";
                    }
                    HOperatorSet.GetImageSize(output, out HTuple zoomedWidth, out HTuple zoomedHeight);
                    // 像素中心约定：r' = (r + 0.5) × s − 0.5，s 取实际输出尺寸比（不是参数值）
                    double sr = (double)zoomedHeight[0].I / height, sc = (double)zoomedWidth[0].I / width;
                    return new[] { sr, 0, 0.5 * sr - 0.5, 0, sc, 0.5 * sc - 0.5 };
                }
                case ImageGeometryMethod.Rotate:
                {
                    HOperatorSet.RotateImage(image, out output, AngleDeg, Interpolation.ToString());
                    HOperatorSet.GetImageSize(output, out HTuple rotatedWidth, out HTuple rotatedHeight);
                    double phi = AngleDeg * Math.PI / 180.0, cos = Math.Cos(phi), sin = Math.Sin(phi);
                    // 绕 ((h − 1) / 2, (w − 1) / 2) 逆时针旋转，映射到输出图的 ((h₂ − 1) / 2, (w₂ − 1) / 2)
                    double r1 = (height - 1) / 2.0, c1 = (width - 1) / 2.0;
                    double r2 = (rotatedHeight[0].I - 1) / 2.0, c2 = (rotatedWidth[0].I - 1) / 2.0;
                    detail = $"AngleDeg={Format(AngleDeg)}, Interpolation={Interpolation}";
                    return new[] { cos, -sin, r2 - (cos * r1 - sin * c1), sin, cos, c2 - (sin * r1 + cos * c1) };
                }
                case ImageGeometryMethod.Mirror:
                    HOperatorSet.MirrorImage(image, out output, MirrorMode.ToString());
                    detail = $"MirrorMode={MirrorMode}";
                    switch (MirrorMode)
                    {
                        case ImageMirrorMode.row: return new double[] { -1, 0, height - 1, 0, 1, 0 };
                        case ImageMirrorMode.column: return new double[] { 1, 0, 0, 0, -1, width - 1 };
                        default: return new double[] { 0, 1, 0, 1, 0, 0 };
                    }
                case ImageGeometryMethod.CropRectangle:
                    HOperatorSet.CropRectangle1(image, out output, Row1, Column1, Row2, Column2);
                    detail = $"Row1={Row1}, Column1={Column1}, Row2={Row2}, Column2={Column2}"
                        + (Row2 >= height || Column2 >= width ? "（超出原图的部分不在定义域内）" : string.Empty);
                    return new double[] { 1, 0, -Row1, 0, 1, -Column1 };
                case ImageGeometryMethod.CropRegion:
                {
                    HOperatorSet.ReduceDomain(image, region, out HObject reduced);
                    try
                    {
                        HOperatorSet.GetDomain(reduced, out HObject domain);
                        HOperatorSet.AreaCenter(domain, out HTuple area, out _, out _);
                        HOperatorSet.SmallestRectangle1(domain, out HTuple top, out HTuple left, out HTuple bottom, out HTuple right);
                        domain.Dispose();
                        if (area.Length == 0 || area[0].D <= 0)
                        {
                            output = null;
                            detail = null;
                            error = $"RegionPath：裁剪区域 {RegionPath} 与图像（{width}×{height}）的定义域没有交集，无法裁剪";
                            return null;
                        }
                        HOperatorSet.CropDomain(reduced, out output);
                        detail = $"RegionPath={RegionPath}，外接矩形 ({top[0].I}, {left[0].I}) ~ ({bottom[0].I}, {right[0].I})";
                        return new double[] { 1, 0, -top[0].I, 0, 1, -left[0].I };
                    }
                    finally
                    {
                        reduced.Dispose();
                    }
                }
                default:
                    throw new InvalidOperationException($"未知的几何变换方式 {(int)Method}");
            }
        }

        private string ParameterText()
        {
            switch (Method)
            {
                case ImageGeometryMethod.ZoomFactor: return $"ScaleWidth = {Format(ScaleWidth)}，ScaleHeight = {Format(ScaleHeight)}，Interpolation = {Interpolation}";
                case ImageGeometryMethod.ZoomSize: return $"TargetWidth = {TargetWidth}，TargetHeight = {TargetHeight}，Interpolation = {Interpolation}";
                case ImageGeometryMethod.Rotate: return $"AngleDeg = {Format(AngleDeg)}，Interpolation = {Interpolation}";
                case ImageGeometryMethod.Mirror: return $"MirrorMode = {MirrorMode}";
                case ImageGeometryMethod.CropRectangle: return $"Row1 = {Row1}，Column1 = {Column1}，Row2 = {Row2}，Column2 = {Column2}";
                default: return $"RegionPath = {RegionPath}";
            }
        }

        private static string Format(double value)
        {
            return value.ToString("G", CultureInfo.InvariantCulture);
        }
    }
}
