using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Tools.Calibration;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>畸变校正的插值方式（成员名即 gen_image_to_world_plane_map 的 MapType，按数字保存，追加在末尾）。</summary>
    public enum RectifyInterpolation
    {
        bilinear,
        nearest_neighbor
    }

    /// <summary>
    /// 畸变校正（CB-06）：用相机标定结果（.vfcal.json 的 Camera 段）把图像校正为测量平面上比例均匀的图像，之后像素距离乘固定当量即为物理长度。
    /// gen_image_to_world_plane_map（只在相机参数 / 当量 / 输出区域 / 插值方式变化时生成并缓存，参与预热）+ map_image。
    /// 22.11 实测（计划第 15 节）：Scale 为每像素对应的米数；校正图 (行, 列) 对应平面 (X, Y) = (x0 + 列·s, y0 + 行·s)，
    /// 因此输出的 Matrix（校正图 行, 列 → WorldRow = X, WorldColumn = Y，标定单位）为 [0, s, x0, s, 0, y0]。
    /// PixelSize、输出区域与 ActualPixelSize 使用标定内容的单位（Unit，m / cm / mm / um）。
    /// </summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("ActualPixelSize", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Matrix", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    public sealed class ImageRectifyTool : ToolBase, IToolResourceLifecycle, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>单边最大输出尺寸（像素），防止当量过小或区域过大时生成巨大的映射。</summary>
        public const int MaxMappedSize = 16384;

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>标定来源，默认 File。</summary>
        public CalibrationSource CalibrationSource { get; set; } = CalibrationSource.File;
        /// <summary>标定文件（须带 Camera 段）。</summary>
        public string CalibrationFile { get; set; }
        /// <summary>内嵌的 .vfcal.json 内容（CalibrationSource = Embedded 时使用）。</summary>
        public string CalibrationData { get; set; }
        /// <summary>校正后每像素对应的物理长度（标定单位）；0 = 按原图中心的比例自动计算。</summary>
        public double PixelSize { get; set; }
        /// <summary>输出区域左上角（测量平面坐标，标定单位）；RegionWidth / RegionHeight 不大于 0 时自动覆盖整个视野。</summary>
        public double RegionX { get; set; }
        public double RegionY { get; set; }
        public double RegionWidth { get; set; }
        public double RegionHeight { get; set; }
        /// <summary>插值方式，默认 bilinear。</summary>
        public RectifyInterpolation Interpolation { get; set; } = RectifyInterpolation.bilinear;

        private readonly CalibrationSourceCache _calibrationCache = new CalibrationSourceCache();
        private readonly object _mapSync = new object();
        private string _mapKey;
        private RectifyMap _map;

        /// <summary>生成好的映射与对应的几何（米 / 像素）。</summary>
        private sealed class RectifyMap : IDisposable
        {
            public HObject Map;
            public double PixelSizeMeters;
            public double OriginXMeters;
            public double OriginYMeters;
            public int Width;
            public int Height;
            public int SourceWidth;
            public int SourceHeight;

            public void Dispose()
            {
                Map?.Dispose();
                Map = null;
            }
        }

        public ImageRectifyTool(string moduleName) : base(moduleName)
        {
        }

        public void UseCalibrationFile(string path)
        {
            CalibrationSource = CalibrationSource.File;
            CalibrationFile = path;
            CalibrationData = null;
        }

        public void UseEmbeddedCalibration(string json)
        {
            CalibrationSource = CalibrationSource.Embedded;
            CalibrationData = json;
            CalibrationFile = null;
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(CalibrationFile):
                    return CalibrationSource == CalibrationSource.File;
                case nameof(CalibrationData):
                    return false;
                default:
                    return true;
            }
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            List<ToolConfigurationIssue> issues = StructuralIssues();
            if (issues.Count > 0 || !CalibrationSourceCache.IsReadable(CalibrationSource, CalibrationFile))
            {
                return issues;
            }
            try
            {
                CalibrationResult calibration = _calibrationCache.Load(CalibrationSource, CalibrationFile, CalibrationData);
                string error = CheckSections(calibration) ?? CheckUnit(calibration, out _);
                if (error != null)
                {
                    issues.Add(new ToolConfigurationIssue(SourceParameter, error));
                }
            }
            catch (Exception ex) when (CalibrationSourceCache.IsLoadException(ex))
            {
                issues.Add(new ToolConfigurationIssue(SourceParameter, ex.Message));
            }
            return issues;
        }

        private List<ToolConfigurationIssue> StructuralIssues()
        {
            var issues = new List<ToolConfigurationIssue>();
            if (!(PixelSize >= 0) || double.IsInfinity(PixelSize))
            {
                issues.Add(new ToolConfigurationIssue(nameof(PixelSize), "像素当量不能为负数（0 表示按原图中心比例自动计算）"));
            }
            if ((RegionWidth > 0) != (RegionHeight > 0))
            {
                issues.Add(new ToolConfigurationIssue(nameof(RegionWidth), "输出区域的宽和高须同时大于 0（指定区域），或同时不大于 0（自动覆盖整个视野）"));
            }
            foreach (double value in new[] { RegionX, RegionY, RegionWidth, RegionHeight })
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    issues.Add(new ToolConfigurationIssue(nameof(RegionX), "输出区域含无效数值"));
                    break;
                }
            }
            CalibrationSourceCache.AddExclusivityIssues(issues, CalibrationSource, CalibrationFile, CalibrationData,
                "请在编辑窗口中把标定文件内嵌到工具");
            return issues;
        }

        private string SourceName => CalibrationSourceCache.SourceName(CalibrationSource, CalibrationFile);

        private string SourceParameter => CalibrationSource == CalibrationSource.Embedded ? nameof(CalibrationData) : nameof(CalibrationFile);

        /// <summary>须带 Camera 段；不符时给出“来源 + 实际类型（含哪些段）+ 期望 Camera 段”，校验、运行、预热措辞相同。</summary>
        private string CheckSections(CalibrationResult calibration)
        {
            if (calibration.Camera != null)
            {
                return null;
            }
            return calibration.IsLegacyTuple
                ? $"{SourceName} 是旧版 write_tuple 矩阵文件（只有 Affine2D 矩阵），畸变校正需要 Camera 数据"
                : $"{SourceName} 的类型为 {calibration.Kind}（包含 {calibration.SectionsText()}），畸变校正需要 Camera 数据";
        }

        /// <summary>标定单位换算为米的系数；不支持的单位返回错误。</summary>
        private static string CheckUnit(CalibrationResult calibration, out double metersPerUnit)
        {
            metersPerUnit = 1;
            if (!CalibrationService.TryGetWorldPlaneScale(calibration.Unit, out string scale))
            {
                return $"标定数据的单位“{calibration.Unit}”不受支持，畸变校正只支持 m / cm / mm / um";
            }
            metersPerUnit = scale == "cm" ? 0.01 : scale == "mm" ? 0.001 : scale == "um" ? 1e-6 : 1;
            return null;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = StructuralIssues().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }
            // 先读标定与映射（缺段等配置问题与图像无关，措辞与校验、预热一致），再取图像
            if (!TryGetMap(out RectifyMap map, out double metersPerUnit, out bool created, out string error))
            {
                return NodeResult.Fail($"{ModuleName} {error}");
            }
            if (created)
            {
                ctx.AddLog(FlowLogLevel.Info, string.Format(CultureInfo.InvariantCulture, "[畸变校正] 生成校正映射 {0}×{1}（{2}）", map.Width, map.Height, Interpolation));
            }
            HObject image;
            try
            {
                image = Input<HalconImage>(ctx, ImagePath).Object;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException)
            {
                return NodeResult.Fail($"{ModuleName} 输入图像引用无效：{ImagePath}，{ex.Message}");
            }
            try
            {
                HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
                if (width.I != map.SourceWidth || height.I != map.SourceHeight)
                {
                    return NodeResult.Fail($"{ModuleName} 图像尺寸 {width.I}×{height.I} 与相机标定的 {map.SourceWidth}×{map.SourceHeight} 不一致");
                }
                HObject rectified;
                // 持锁使用映射：防止并发的参数变化在 map_image 期间释放它
                lock (_mapSync)
                {
                    if (!ReferenceEquals(map, _map) && !TryGetMap(out map, out metersPerUnit, out _, out error))
                    {
                        return NodeResult.Fail($"{ModuleName} {error}");
                    }
                    HOperatorSet.MapImage(image, map.Map, out rectified);
                }
                double pixelSize = map.PixelSizeMeters / metersPerUnit;
                double originX = map.OriginXMeters / metersPerUnit;
                double originY = map.OriginYMeters / metersPerUnit;
                var matrix = new HomMat2D(new HTuple(0.0, pixelSize, originX, pixelSize, 0.0, originY));
                SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(rectified), 1));
                SetOutput(ctx, Variable.Single(ModuleName, "ActualPixelSize", VariableType.Double, pixelSize));
                SetOutput(ctx, Variable.Object(ModuleName, "Matrix", matrix, 1));
                ctx.AddLog(FlowLogLevel.Info, string.Format(CultureInfo.InvariantCulture,
                    "[畸变校正] {0}×{1} → {2}×{3}，当量 {4:G6}/像素，左上角 ({5:G6}, {6:G6})", width.I, height.I, map.Width, map.Height, pixelSize, originX, originY));
                return NodeResult.Ok;
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 畸变校正失败：{ex.Message}");
            }
        }

        /// <summary>读取标定并取得（必要时生成）映射；缓存键 = 相机参数 + 测量平面位姿 + 单位 + PixelSize + 输出区域 + 插值方式。</summary>
        private bool TryGetMap(out RectifyMap map, out double metersPerUnit, out bool created, out string error)
        {
            map = null;
            metersPerUnit = 1;
            created = false;
            if (CalibrationSource == CalibrationSource.File)
            {
                if (string.IsNullOrWhiteSpace(CalibrationFile))
                {
                    error = "未配置 CalibrationFile";
                    return false;
                }
                if (!System.IO.File.Exists(CalibrationFile))
                {
                    error = $"标定文件不存在：{CalibrationFile}";
                    return false;
                }
            }
            CalibrationResult calibration;
            try
            {
                calibration = _calibrationCache.Load(CalibrationSource, CalibrationFile, CalibrationData);
            }
            catch (Exception ex) when (CalibrationSourceCache.IsLoadException(ex))
            {
                error = ex.Message;
                return false;
            }
            error = CheckSections(calibration) ?? CheckUnit(calibration, out metersPerUnit);
            if (error != null)
            {
                return false;
            }
            CameraCalibration camera = calibration.Camera;
            string key = string.Join("|",
                string.Join(",", camera.CamParam.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture))),
                string.Join(",", camera.Pose.Select(v => v.ToString("R", CultureInfo.InvariantCulture))),
                metersPerUnit.ToString("R", CultureInfo.InvariantCulture),
                PixelSize.ToString("R", CultureInfo.InvariantCulture),
                RegionX.ToString("R", CultureInfo.InvariantCulture), RegionY.ToString("R", CultureInfo.InvariantCulture),
                RegionWidth.ToString("R", CultureInfo.InvariantCulture), RegionHeight.ToString("R", CultureInfo.InvariantCulture),
                Interpolation);
            lock (_mapSync)
            {
                if (_map != null && _mapKey == key)
                {
                    map = _map;
                    return true;
                }
                try
                {
                    RectifyMap newMap = CreateMap(camera, metersPerUnit, out error);
                    if (newMap == null)
                    {
                        return false;
                    }
                    _map?.Dispose();
                    _map = newMap;
                    _mapKey = key;
                    map = newMap;
                    created = true;
                    return true;
                }
                catch (HalconException ex)
                {
                    error = "生成校正映射失败：" + ex.Message;
                    return false;
                }
            }
        }

        private RectifyMap CreateMap(CameraCalibration camera, double metersPerUnit, out string error)
        {
            error = null;
            HTuple cameraParameters = camera.ToHalconCamParam();
            HTuple pose = camera.ToHalconPose();
            int sourceWidth = (int)Math.Round(camera.GetValue("image_width"));
            int sourceHeight = (int)Math.Round(camera.GetValue("image_height"));
            if (!(sourceWidth > 0) || !(sourceHeight > 0))
            {
                error = "相机参数缺少图像宽高（image_width / image_height）";
                return null;
            }

            double pixelSize;
            if (PixelSize > 0)
            {
                pixelSize = PixelSize * metersPerUnit;
            }
            else
            {
                // 原图中心像素与右、下相邻像素投影到测量平面，取两个方向的平均间距（计划第 15 节）
                double centerRow = (sourceHeight - 1) / 2.0, centerColumn = (sourceWidth - 1) / 2.0;
                HOperatorSet.ImagePointsToWorldPlane(cameraParameters, pose, new HTuple(centerRow, centerRow, centerRow + 1),
                    new HTuple(centerColumn, centerColumn + 1, centerColumn), "m", out HTuple x, out HTuple y);
                double alongColumn = Math.Sqrt(Math.Pow(x[1].D - x[0].D, 2) + Math.Pow(y[1].D - y[0].D, 2));
                double alongRow = Math.Sqrt(Math.Pow(x[2].D - x[0].D, 2) + Math.Pow(y[2].D - y[0].D, 2));
                pixelSize = (alongColumn + alongRow) / 2;
            }

            double originX, originY, width, height;
            if (RegionWidth > 0 && RegionHeight > 0)
            {
                originX = RegionX * metersPerUnit;
                originY = RegionY * metersPerUnit;
                width = RegionWidth * metersPerUnit;
                height = RegionHeight * metersPerUnit;
            }
            else
            {
                // 原图边界（四条边各取 9 个点）投影到测量平面，取外接矩形
                var rows = new List<double>();
                var columns = new List<double>();
                for (int k = 0; k <= 8; k++)
                {
                    double t = k / 8.0;
                    rows.AddRange(new[] { 0.0, sourceHeight - 1.0, t * (sourceHeight - 1), t * (sourceHeight - 1) });
                    columns.AddRange(new[] { t * (sourceWidth - 1), t * (sourceWidth - 1), 0.0, sourceWidth - 1.0 });
                }
                HOperatorSet.ImagePointsToWorldPlane(cameraParameters, pose, new HTuple(rows.ToArray()), new HTuple(columns.ToArray()), "m", out HTuple x, out HTuple y);
                double[] xs = x.ToDArr(), ys = y.ToDArr();
                originX = xs.Min();
                originY = ys.Min();
                width = xs.Max() - originX;
                height = ys.Max() - originY;
            }
            int mappedWidth = (int)Math.Ceiling(width / pixelSize);
            int mappedHeight = (int)Math.Ceiling(height / pixelSize);
            if (mappedWidth < 1 || mappedHeight < 1 || mappedWidth > MaxMappedSize || mappedHeight > MaxMappedSize)
            {
                error = $"校正图尺寸 {mappedWidth}×{mappedHeight} 超出范围（1 ~ {MaxMappedSize}）：请调整像素当量或输出区域";
                return null;
            }
            // 映射位姿：把测量平面位姿的原点移到输出区域左上角
            HOperatorSet.SetOriginPose(pose, originX, originY, 0, out HTuple mapPose);
            HOperatorSet.GenImageToWorldPlaneMap(out HObject mapImage, cameraParameters, mapPose, sourceWidth, sourceHeight,
                mappedWidth, mappedHeight, pixelSize, Interpolation.ToString());
            return new RectifyMap
            {
                Map = mapImage,
                PixelSizeMeters = pixelSize,
                OriginXMeters = originX,
                OriginYMeters = originY,
                Width = mappedWidth,
                Height = mappedHeight,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight
            };
        }

        /// <summary>预热：读取标定并生成映射；文件缺失、格式错误、缺 Camera 段或生成失败时抛出（文件缺失不在校验阶段报，沿用批一约定）。</summary>
        public void Prepare()
        {
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
            if (StructuralIssues().Count > 0)
            {
                return;
            }
            if (!TryGetMap(out _, out _, out _, out string error))
            {
                throw new InvalidOperationException(error);
            }
        }

        public void ReleaseResources()
        {
            _calibrationCache.Release();
            lock (_mapSync)
            {
                _map?.Dispose();
                _map = null;
                _mapKey = null;
            }
        }
    }
}
