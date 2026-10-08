using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HalconDotNet;

namespace VisionFlow.Tools.Calibration
{
    /// <summary>标定文件的类型（`.vfcal.json` 的 Kind 字段，按名称保存）。</summary>
    public enum CalibrationFileKind
    {
        Affine2D,
        RotationCenter,
        Camera
    }

    /// <summary>N 点标定的变换类型（成员名即 HALCON 的 vector_to_* 算子含义，按名称保存）。</summary>
    public enum CalibrationTransformType
    {
        /// <summary>一般仿射（vector_to_hom_mat2d）：两个方向比例可不同、可有剪切，至少 3 个不共线的点。</summary>
        affine,
        /// <summary>相似变换（vector_to_similarity）：等比例、无剪切。</summary>
        similarity,
        /// <summary>刚体变换（vector_to_rigid）：只有旋转与平移，比例固定为 1。</summary>
        rigid
    }

    /// <summary>一个标定点对：图像行列与物理 X / Y（X 对应坐标转换的 WorldRow，Y 对应 WorldColumn）。</summary>
    public sealed class CalibrationPoint
    {
        public double Row { get; set; }
        public double Column { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>求解后的残差（物理单位）：图像点经矩阵变换后与物理点的距离；未求解时为空。</summary>
        public double? Residual { get; set; }

        public CalibrationPoint Clone()
        {
            return (CalibrationPoint)MemberwiseClone();
        }
    }

    /// <summary>N 点标定结果（Affine2D 载荷）。</summary>
    public sealed class Affine2DCalibration
    {
        /// <summary>图像 (行, 列) → 物理 (X, Y) 的 HALCON 仿射矩阵（6 个数）。</summary>
        public double[] HomMat2D { get; set; }
        public List<CalibrationPoint> Points { get; set; } = new List<CalibrationPoint>();
        /// <summary>残差均方根（物理单位）；旧版 write_tuple 文件没有点对，为空。</summary>
        public double? RmsError { get; set; }
        public double? MaxError { get; set; }
        public CalibrationTransformType TransformType { get; set; }
    }

    /// <summary>旋转中心采集点（CB-03）：特征点的图像行列与（可选的）旋转角度。</summary>
    public sealed class RotationCenterPoint
    {
        public double Row { get; set; }
        public double Column { get; set; }
        /// <summary>对应的旋转角度（弧度，可选；图像角度约定：与匹配 Angle 相同，屏幕上逆时针为正）。</summary>
        public double? Angle { get; set; }
        /// <summary>求解后的残差（像素）：|点到圆心距离 − 半径|；未求解时为空。</summary>
        public double? Residual { get; set; }

        public RotationCenterPoint Clone()
        {
            return (RotationCenterPoint)MemberwiseClone();
        }
    }

    /// <summary>旋转中心标定结果（RotationCenter 载荷，CB-03）。</summary>
    public sealed class RotationCenterCalibration
    {
        public double Row { get; set; }
        public double Column { get; set; }
        /// <summary>旋转中心的物理坐标（有 N 点标定结果时一并换算）。</summary>
        public double? X { get; set; }
        public double? Y { get; set; }
        public double Radius { get; set; }
        public List<RotationCenterPoint> Points { get; set; } = new List<RotationCenterPoint>();
        public double? RmsError { get; set; }
        public double? MaxError { get; set; }
    }

    /// <summary>旋转中心求解结果：标定内容 + 提示（不阻止求解的问题，如角度覆盖过小）。</summary>
    public sealed class RotationCenterSolution
    {
        public RotationCenterCalibration Calibration { get; set; }
        /// <summary>是否按角度路径求解（否则为无角度的圆拟合）。</summary>
        public bool UsedAngles { get; set; }
        /// <summary>角度覆盖范围（度）：有角度路径按角度，无角度路径按各点绕圆心的覆盖弧度。</summary>
        public double CoverageDegrees { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>相机参数的一项（按名称和值保存；值为字符串或数值）。</summary>
    public sealed class CameraParameter
    {
        public string Name { get; set; }
        public object Value { get; set; }
    }

    /// <summary>相机标定结果（Camera 载荷，CB-04 生成；CB-05 的 Camera 方式读取）。</summary>
    public sealed class CameraCalibration
    {
        /// <summary>HALCON 相机参数，按名称与值依次保存（第一项为 camera_type）。</summary>
        public List<CameraParameter> CamParam { get; set; } = new List<CameraParameter>();
        /// <summary>测量平面位姿（7 个数，HALCON 位姿，长度单位为米）。</summary>
        public double[] Pose { get; set; }
        public double PlaneThickness { get; set; }
        public double? RmsError { get; set; }
        /// <summary>使用的标定板描述文件名（如 calplate_160mm.cpd）。</summary>
        public string PlateDescription { get; set; }
        public int ImageCount { get; set; }

        /// <summary>转成 HALCON 相机参数元组（image_width / image_height 为整数）。</summary>
        public HTuple ToHalconCamParam()
        {
            var tuple = new HTuple();
            foreach (CameraParameter parameter in CamParam)
            {
                if (parameter.Value is string text)
                {
                    tuple = tuple.TupleConcat(new HTuple(text));
                }
                else
                {
                    double value = Convert.ToDouble(parameter.Value, CultureInfo.InvariantCulture);
                    tuple = parameter.Name == "image_width" || parameter.Name == "image_height"
                        ? tuple.TupleConcat(new HTuple((int)Math.Round(value)))
                        : tuple.TupleConcat(new HTuple(value));
                }
            }
            return tuple;
        }

        /// <summary>转成 HALCON 位姿元组（前 6 个为实数，第 7 个位姿类型为整数）。</summary>
        public HTuple ToHalconPose()
        {
            var tuple = new HTuple(Pose.Take(6).ToArray());
            return tuple.TupleConcat(new HTuple((int)Math.Round(Pose[6])));
        }

        /// <summary>
        /// 各相机类型的参数名（HALCON 相机参数元组第 0 项为类型名，其后依次为这些参数）。get_cam_par_names 是 HDevelop 过程而非算子，
        /// .NET 中不可用，按 HALCON 文档列出常用类型；其他类型留待相机标定批（CB-04）按需补充。
        /// </summary>
        private static readonly Dictionary<string, string[]> ParameterNames = new Dictionary<string, string[]>
        {
            ["area_scan_division"] = new[] { "focus", "kappa", "sx", "sy", "cx", "cy", "image_width", "image_height" },
            ["area_scan_polynomial"] = new[] { "focus", "k1", "k2", "k3", "p1", "p2", "sx", "sy", "cx", "cy", "image_width", "image_height" },
            ["area_scan_telecentric_division"] = new[] { "magnification", "kappa", "sx", "sy", "cx", "cy", "image_width", "image_height" },
            ["area_scan_telecentric_polynomial"] = new[] { "magnification", "k1", "k2", "k3", "p1", "p2", "sx", "sy", "cx", "cy", "image_width", "image_height" },
            ["line_scan_division"] = new[] { "focus", "kappa", "sx", "sy", "cx", "cy", "image_width", "image_height", "vx", "vy", "vz" },
            ["line_scan_polynomial"] = new[] { "focus", "k1", "k2", "k3", "p1", "p2", "sx", "sy", "cx", "cy", "image_width", "image_height", "vx", "vy", "vz" }
        };

        /// <summary>由 HALCON 相机参数与位姿创建（参数按类型对应的名称保存）。</summary>
        public static CameraCalibration FromHalcon(HTuple camParam, HTuple pose)
        {
            if (camParam == null || camParam.Length == 0 || camParam[0].Type != HTupleType.STRING)
            {
                throw new ArgumentException("相机参数应以相机类型（字符串）开头", nameof(camParam));
            }
            string cameraType = camParam[0].S;
            if (!ParameterNames.TryGetValue(cameraType, out string[] names))
            {
                throw new NotSupportedException($"暂不支持的相机类型：{cameraType}");
            }
            if (camParam.Length != names.Length + 1)
            {
                throw new ArgumentException($"{cameraType} 相机参数应为 {names.Length + 1} 个值（当前 {camParam.Length} 个）", nameof(camParam));
            }
            var result = new CameraCalibration { Pose = pose.ToDArr() };
            result.CamParam.Add(new CameraParameter { Name = "camera_type", Value = cameraType });
            for (int i = 0; i < names.Length; i++)
            {
                result.CamParam.Add(new CameraParameter { Name = names[i], Value = camParam[i + 1].D });
            }
            return result;
        }
    }

    /// <summary>`.vfcal.json` 标定文件的内容（也用于工具内嵌的 CalibrationData）。</summary>
    public sealed class CalibrationResult
    {
        public const int CurrentFormatVersion = 1;

        public int FormatVersion { get; set; } = CurrentFormatVersion;
        /// <summary>主要类型；一个文件可同时带 Affine2D 与 RotationCenter 两段数据。</summary>
        public CalibrationFileKind Kind { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string Description { get; set; }
        /// <summary>物理单位（如 mm）；Camera 方式按它换算 image_points_to_world_plane 的输出单位（m / cm / mm / um）。</summary>
        public string Unit { get; set; }
        public Affine2DCalibration Affine2D { get; set; }
        public RotationCenterCalibration RotationCenter { get; set; }
        public CameraCalibration Camera { get; set; }

        /// <summary>由旧版 write_tuple 矩阵文件读入（只有 Affine2D 矩阵，没有点对与误差）。</summary>
        [JsonIgnore]
        public bool IsLegacyTuple { get; set; }

        /// <summary>文件中带有数据的段（用于错误信息）。</summary>
        public string SectionsText()
        {
            var sections = new List<string>();
            if (Affine2D != null) sections.Add(nameof(Affine2D));
            if (RotationCenter != null) sections.Add(nameof(RotationCenter));
            if (Camera != null) sections.Add(nameof(Camera));
            return sections.Count == 0 ? "无数据" : string.Join(" + ", sections);
        }
    }

    /// <summary>
    /// 标定计算与标定结果读写（CB-01）：静态、无 UI、不依赖 FlowContext，编辑器助手与上层项目都可直接调用。
    /// 坐标约定：图像 (行, 列) 经矩阵变换得到物理 (X, Y)，与 vector_to_hom_mat2d(Px = 行, Py = 列, Qx = X, Qy = Y) 一致；
    /// 坐标转换工具的 WorldRow = X、WorldColumn = Y。
    /// </summary>
    public static class CalibrationService
    {
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                // 独立的 .vfcal.json 中文直接可读；内嵌进流程文件后由流程序列化按既有行为转义为 \uXXXX
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        // ======================= N 点标定 =======================

        /// <summary>
        /// 由点对求图像 → 物理的变换（vector_to_hom_mat2d / vector_to_similarity / vector_to_rigid）与残差。
        /// 至少 3 个点对，图像点与物理点各自不重复的点不少于 3 个且不共线，否则抛出带中文说明的 <see cref="InvalidOperationException"/>
        /// （22.11 实测：affine 对两点重合的 3 点不报错而返回无意义矩阵，similarity / rigid 对共线点不报错，所以前置检查）。
        /// </summary>
        public static Affine2DCalibration SolveAffine(IReadOnlyList<CalibrationPoint> points, CalibrationTransformType transformType)
        {
            ValidatePoints(points);
            var rows = new HTuple(points.Select(p => p.Row).ToArray());
            var columns = new HTuple(points.Select(p => p.Column).ToArray());
            var xs = new HTuple(points.Select(p => p.X).ToArray());
            var ys = new HTuple(points.Select(p => p.Y).ToArray());
            HTuple matrix;
            try
            {
                switch (transformType)
                {
                    case CalibrationTransformType.similarity:
                        HOperatorSet.VectorToSimilarity(rows, columns, xs, ys, out matrix);
                        break;
                    case CalibrationTransformType.rigid:
                        HOperatorSet.VectorToRigid(rows, columns, xs, ys, out matrix);
                        break;
                    default:
                        HOperatorSet.VectorToHomMat2d(rows, columns, xs, ys, out matrix);
                        break;
                }
            }
            catch (HalconException ex) when (ex.GetErrorCode() == 9211)
            {
                throw new InvalidOperationException("点对退化（共线或重复），无法求解标定矩阵；请让标定点分布在两个方向上（如 3×3 网格）", ex);
            }
            catch (HalconException ex)
            {
                throw new InvalidOperationException("标定求解失败：" + ex.Message, ex);
            }

            var result = new Affine2DCalibration { HomMat2D = matrix.ToDArr(), TransformType = transformType };
            double sumSquares = 0;
            double max = 0;
            foreach (CalibrationPoint point in points)
            {
                TransformPoint(result.HomMat2D, point.Row, point.Column, out double x, out double y);
                double residual = Math.Sqrt((x - point.X) * (x - point.X) + (y - point.Y) * (y - point.Y));
                CalibrationPoint copy = point.Clone();
                copy.Residual = residual;
                result.Points.Add(copy);
                sumSquares += residual * residual;
                max = Math.Max(max, residual);
            }
            result.RmsError = Math.Sqrt(sumSquares / points.Count);
            result.MaxError = max;
            return result;
        }

        /// <summary>检查点对是否足以求解；不足时抛出带中文说明的 <see cref="InvalidOperationException"/>。</summary>
        public static void ValidatePoints(IReadOnlyList<CalibrationPoint> points)
        {
            int count = points?.Count ?? 0;
            if (count < 3)
            {
                throw new InvalidOperationException($"至少需要 3 个点对（当前 {count} 个）");
            }
            for (int i = 0; i < count; i++)
            {
                CalibrationPoint p = points[i];
                if (p == null || !IsFinite(p.Row) || !IsFinite(p.Column) || !IsFinite(p.X) || !IsFinite(p.Y))
                {
                    throw new InvalidOperationException($"第 {i + 1} 个点对含无效数值（空、NaN 或无穷大）");
                }
            }
            CheckSpread(points.Select(p => (p.Row, p.Column)).ToList(), "图像点");
            CheckSpread(points.Select(p => (p.X, p.Y)).ToList(), "物理点");
        }

        /// <summary>不重复的点不少于 3 个，且不共线（垂直主方向的分布不到主方向的 0.1% 视为共线）。</summary>
        private static void CheckSpread(List<(double A, double B)> points, string what, string collinearAdvice = "请让标定点分布在两个方向上（如 3×3 网格）")
        {
            double meanA = points.Average(p => p.A);
            double meanB = points.Average(p => p.B);
            double extent = points.Max(p => Math.Max(Math.Abs(p.A - meanA), Math.Abs(p.B - meanB)));
            double tolerance = 1e-9 * (1 + extent);
            var distinct = new List<(double A, double B)>();
            foreach ((double A, double B) p in points)
            {
                if (!distinct.Any(d => Math.Abs(d.A - p.A) <= tolerance && Math.Abs(d.B - p.B) <= tolerance))
                {
                    distinct.Add(p);
                }
            }
            if (distinct.Count < 3)
            {
                throw new InvalidOperationException($"{what}中不重复的点只有 {distinct.Count} 个，至少需要 3 个");
            }
            double saa = 0, sbb = 0, sab = 0;
            foreach ((double A, double B) p in points)
            {
                saa += (p.A - meanA) * (p.A - meanA);
                sbb += (p.B - meanB) * (p.B - meanB);
                sab += (p.A - meanA) * (p.B - meanB);
            }
            double trace = saa + sbb;
            double root = Math.Sqrt(Math.Max(0, (saa - sbb) * (saa - sbb) / 4 + sab * sab));
            double major = trace / 2 + root;
            double minor = Math.Max(0, trace / 2 - root);
            if (major <= 0 || Math.Sqrt(minor / major) < 1e-3)
            {
                throw new InvalidOperationException($"{what}共线（或几乎共线），无法求解；{collinearAdvice}");
            }
        }

        /// <summary>HALCON 仿射矩阵变换一个点（与 affine_trans_point_2d 相同，纯计算）。</summary>
        public static void TransformPoint(double[] homMat2D, double row, double column, out double x, out double y)
        {
            x = homMat2D[0] * row + homMat2D[1] * column + homMat2D[2];
            y = homMat2D[3] * row + homMat2D[4] * column + homMat2D[5];
        }

        /// <summary>
        /// 变换一个位姿（纯计算，与坐标转换 CB-05 的换算路径相同）：点经矩阵变换；角度取“沿角度方向 1 个单位”的第二点
        /// 一同变换后的方向，按图像角度约定 angle = atan2(−ΔX, ΔY)。镜像矩阵（行列式为负）时方向向量自然翻转。
        /// </summary>
        public static void TransformPose(double[] homMat2D, double row, double column, double phi, out double x, out double y, out double angle)
        {
            TransformPoint(homMat2D, row, column, out x, out y);
            TransformPoint(homMat2D, row - Math.Sin(phi), column + Math.Cos(phi), out double x2, out double y2);
            angle = Math.Atan2(-(x2 - x), y2 - y);
        }

        /// <summary>
        /// 绕中心旋转（计划第 6 节 CB-07 与 CB-03 共用的公式）：R(α)·(a, b) = (a·cos α − b·sin α, a·sin α + b·cos α)，
        /// 先平移到中心、旋转、再平移回去。物理系中正角为“从 +X 转向 +Y”；图像 (行, 列) 中与 hom_mat2d_rotate 相同（屏幕上逆时针）。
        /// </summary>
        public static void RotateAbout(double centerA, double centerB, double alpha, double a, double b, out double rotatedA, out double rotatedB)
        {
            double cos = Math.Cos(alpha), sin = Math.Sin(alpha);
            double da = a - centerA, db = b - centerB;
            rotatedA = centerA + da * cos - db * sin;
            rotatedB = centerB + da * sin + db * cos;
        }

        /// <summary>角度折算到 (−π, π]。</summary>
        public static double WrapAngle(double angle)
        {
            double wrapped = Math.Atan2(Math.Sin(angle), Math.Cos(angle));
            return wrapped <= -Math.PI ? Math.PI : wrapped;
        }

        // ======================= 旋转中心（CB-03） =======================

        /// <summary>
        /// 由同一特征点在多次旋转后的图像位置求旋转中心（纯 C#，不调用 HALCON）。
        /// 至少 2 个带角度的点时按角度路径：任意两点满足 (I − R(θi − θj))·c = p_i − R(θi − θj)·p_j，全部点对线性最小二乘；
        /// 否则至少 3 个点做 Kåsa 代数圆拟合。残差 = |点到圆心距离 − 半径|。点数不足、数值无效、点全部重合 / 共线、角度全部相同时
        /// 抛出带中文说明的 <see cref="InvalidOperationException"/>；角度覆盖小于 30° 等只在 Warnings 中提示。
        /// </summary>
        public static RotationCenterSolution SolveRotationCenter(IReadOnlyList<RotationCenterPoint> points)
        {
            int count = points?.Count ?? 0;
            if (count == 0)
            {
                throw new InvalidOperationException("没有采集点：至少需要 3 个点，或至少 2 个带角度的点");
            }
            for (int i = 0; i < count; i++)
            {
                RotationCenterPoint p = points[i];
                if (p == null || !IsFinite(p.Row) || !IsFinite(p.Column) || (p.Angle.HasValue && !IsFinite(p.Angle.Value)))
                {
                    throw new InvalidOperationException($"第 {i + 1} 个点含无效数值（空、NaN 或无穷大）");
                }
            }
            var solution = new RotationCenterSolution();
            List<RotationCenterPoint> withAngle = points.Where(p => p.Angle.HasValue).ToList();
            double centerRow, centerColumn, circleRadius = double.NaN;
            if (withAngle.Count >= 2)
            {
                if (DistinctCount(withAngle) < 2)
                {
                    throw new InvalidOperationException("带角度的点全部重合，无法确定旋转中心");
                }
                if (!TrySolveCenterFromAngles(withAngle, 1, out centerRow, out centerColumn, out double rotationRms))
                {
                    throw new InvalidOperationException("各点的角度全部相同（没有旋转），无法确定旋转中心");
                }
                solution.UsedAngles = true;
                if (withAngle.Count < count)
                {
                    solution.Warnings.Add($"{count - withAngle.Count} 个点没有角度，未参与求圆心（只计算残差）");
                }
                // 角度方向与图像约定相反时（如机构角度方向相反），按相反方向拟合会明显更好：只提示，不改变结果
                if (TrySolveCenterFromAngles(withAngle, -1, out _, out _, out double flippedRms) && rotationRms > 1e-6 && flippedRms < rotationRms * 0.25)
                {
                    solution.Warnings.Add("按相反的角度方向拟合残差明显更小：角度方向可能与图像约定（屏幕上逆时针为正）相反，请检查角度来源，必要时取反");
                }
                List<double> relative = withAngle.Select(p => WrapAngle(p.Angle.Value - withAngle[0].Angle.Value)).ToList();
                solution.CoverageDegrees = (relative.Max() - relative.Min()) * 180 / Math.PI;
            }
            else
            {
                if (count < 3)
                {
                    throw new InvalidOperationException($"至少需要 3 个点（当前 {count} 个），或至少 2 个带角度的点");
                }
                if (withAngle.Count == 1)
                {
                    solution.Warnings.Add("只有 1 个点带角度，按无角度的圆拟合求解");
                }
                CheckSpread(points.Select(p => (p.Row, p.Column)).ToList(), "旋转点", "请让旋转角度覆盖更大的范围，或填写各点的旋转角度");
                FitCircle(points, out centerRow, out centerColumn, out circleRadius);
            }

            List<double> distances = points.Select(p => Distance(p.Row, p.Column, centerRow, centerColumn)).ToList();
            double radius;
            if (solution.UsedAngles)
            {
                radius = distances.Average();
            }
            else
            {
                radius = circleRadius;
                solution.CoverageDegrees = ArcCoverageDegrees(points, centerRow, centerColumn);
            }
            var calibration = new RotationCenterCalibration { Row = centerRow, Column = centerColumn, Radius = radius };
            double sumSquares = 0, max = 0;
            for (int i = 0; i < count; i++)
            {
                double residual = Math.Abs(distances[i] - radius);
                RotationCenterPoint copy = points[i].Clone();
                copy.Residual = residual;
                calibration.Points.Add(copy);
                sumSquares += residual * residual;
                max = Math.Max(max, residual);
            }
            calibration.RmsError = Math.Sqrt(sumSquares / count);
            calibration.MaxError = max;
            if (solution.CoverageDegrees < 30)
            {
                solution.Warnings.Add($"角度覆盖只有 {solution.CoverageDegrees.ToString("F1", CultureInfo.InvariantCulture)}°，建议覆盖 30° 以上，否则圆心误差会偏大");
            }
            solution.Calibration = calibration;
            return solution;
        }

        /// <summary>角度路径：对全部点对累加最小二乘。M = I − R(Δ) 满足 MᵀM = (2 − 2·cos Δ)·I，因此中心 = Σ Mᵀ·rhs / Σ (2 − 2·cos Δ)。</summary>
        private static bool TrySolveCenterFromAngles(List<RotationCenterPoint> points, int sign, out double centerRow, out double centerColumn, out double rotationRms)
        {
            double weight = 0, sumA = 0, sumB = 0;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    double delta = sign * (points[i].Angle.Value - points[j].Angle.Value);
                    double cos = Math.Cos(delta), sin = Math.Sin(delta);
                    // rhs = p_i − R(Δ)·p_j
                    double rhsA = points[i].Row - (points[j].Row * cos - points[j].Column * sin);
                    double rhsB = points[i].Column - (points[j].Row * sin + points[j].Column * cos);
                    // Mᵀ = [[1 − cos, −sin], [sin, 1 − cos]]
                    sumA += (1 - cos) * rhsA - sin * rhsB;
                    sumB += sin * rhsA + (1 - cos) * rhsB;
                    weight += 2 - 2 * cos;
                }
            }
            centerRow = centerColumn = rotationRms = double.NaN;
            if (weight < 1e-12)
            {
                return false;
            }
            centerRow = sumA / weight;
            centerColumn = sumB / weight;
            // 旋转一致性残差：用 j 点绕中心转 Δ 预测 i 点
            double sumSquares = 0;
            int pairs = 0;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    double delta = sign * (points[i].Angle.Value - points[j].Angle.Value);
                    RotateAbout(centerRow, centerColumn, delta, points[j].Row, points[j].Column, out double predictedRow, out double predictedColumn);
                    double d = Distance(points[i].Row, points[i].Column, predictedRow, predictedColumn);
                    sumSquares += d * d;
                    pairs++;
                }
            }
            rotationRms = Math.Sqrt(sumSquares / pairs);
            return true;
        }

        /// <summary>Kåsa 代数圆拟合（去均值坐标）：x² + y² + D·x + E·y + F = 0，圆心 (−D/2, −E/2)，半径 √((D² + E²)/4 − F)。</summary>
        private static void FitCircle(IReadOnlyList<RotationCenterPoint> points, out double centerRow, out double centerColumn, out double radius)
        {
            int n = points.Count;
            double meanA = points.Average(p => p.Row);
            double meanB = points.Average(p => p.Column);
            double suu = 0, suv = 0, svv = 0, suz = 0, svz = 0, sz = 0;
            foreach (RotationCenterPoint p in points)
            {
                double u = p.Row - meanA, v = p.Column - meanB, z = u * u + v * v;
                suu += u * u;
                suv += u * v;
                svv += v * v;
                suz += u * z;
                svz += v * z;
                sz += z;
            }
            // 去均值后 Σu = Σv = 0：F = −Σz / n，[Σuu Σuv; Σuv Σvv]·[D; E] = −[Σuz; Σvz]
            double det = suu * svv - suv * suv;
            if (Math.Abs(det) <= 1e-12 * Math.Max(1, suu * svv))
            {
                throw new InvalidOperationException("旋转点共线（或几乎共线），无法拟合圆；请让旋转角度覆盖更大的范围，或填写各点的旋转角度");
            }
            double d = (-suz * svv + svz * suv) / det;
            double e = (-svz * suu + suz * suv) / det;
            double f = -sz / n;
            double radiusSquared = (d * d + e * e) / 4 - f;
            if (!(radiusSquared > 0))
            {
                throw new InvalidOperationException("旋转点无法拟合成圆（半径无效）");
            }
            centerRow = meanA - d / 2;
            centerColumn = meanB - e / 2;
            radius = Math.Sqrt(radiusSquared);
        }

        /// <summary>各点绕圆心的覆盖弧度（度）= 360° − 相邻方向之间最大的空隙。</summary>
        private static double ArcCoverageDegrees(IReadOnlyList<RotationCenterPoint> points, double centerRow, double centerColumn)
        {
            List<double> directions = points.Select(p => Math.Atan2(-(p.Row - centerRow), p.Column - centerColumn)).OrderBy(a => a).ToList();
            double maxGap = 2 * Math.PI - (directions[directions.Count - 1] - directions[0]);
            for (int i = 1; i < directions.Count; i++)
            {
                maxGap = Math.Max(maxGap, directions[i] - directions[i - 1]);
            }
            return (2 * Math.PI - maxGap) * 180 / Math.PI;
        }

        private static int DistinctCount(IReadOnlyList<RotationCenterPoint> points)
        {
            var distinct = new List<RotationCenterPoint>();
            foreach (RotationCenterPoint p in points)
            {
                if (!distinct.Any(d => Math.Abs(d.Row - p.Row) <= 1e-9 && Math.Abs(d.Column - p.Column) <= 1e-9))
                {
                    distinct.Add(p);
                }
            }
            return distinct.Count;
        }

        private static double Distance(double a1, double b1, double a2, double b2)
        {
            return Math.Sqrt((a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
        }

        /// <summary>
        /// 把旋转中心结果并入标定内容（CB-03 保存 / 内嵌规则）：已有 Affine2D 时保留它并新增 RotationCenter 段（换算圆心物理坐标，Kind 仍为 Affine2D）；
        /// 否则只写 RotationCenter 段（Kind = RotationCenter）。existing 为空或没有 Affine2D 段时视为没有 N 点标定。
        /// </summary>
        public static CalibrationResult MergeRotationCenter(CalibrationResult existing, RotationCenterCalibration center, string unit, string description)
        {
            RotationCenterCalibration copy = CloneCenter(center);
            Affine2DCalibration affine = existing?.Affine2D;
            if (affine != null)
            {
                TransformPoint(affine.HomMat2D, copy.Row, copy.Column, out double x, out double y);
                copy.X = x;
                copy.Y = y;
            }
            else
            {
                copy.X = null;
                copy.Y = null;
            }
            return new CalibrationResult
            {
                Kind = affine != null ? CalibrationFileKind.Affine2D : CalibrationFileKind.RotationCenter,
                CreatedAt = DateTimeOffset.Now,
                Unit = affine != null ? existing.Unit : (string.IsNullOrWhiteSpace(unit) ? null : unit.Trim()),
                Description = string.IsNullOrWhiteSpace(description) ? existing?.Description : description.Trim(),
                Affine2D = affine,
                RotationCenter = copy
            };
        }

        private static RotationCenterCalibration CloneCenter(RotationCenterCalibration center)
        {
            return new RotationCenterCalibration
            {
                Row = center.Row,
                Column = center.Column,
                X = center.X,
                Y = center.Y,
                Radius = center.Radius,
                Points = center.Points.Select(p => p.Clone()).ToList(),
                RmsError = center.RmsError,
                MaxError = center.MaxError
            };
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // ======================= 读写 =======================

        /// <summary>由 N 点标定结果创建可保存的标定内容。</summary>
        public static CalibrationResult FromAffine(Affine2DCalibration affine, string unit, string description)
        {
            return new CalibrationResult
            {
                Kind = CalibrationFileKind.Affine2D,
                CreatedAt = DateTimeOffset.Now,
                Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Affine2D = affine
            };
        }

        /// <summary>序列化为 `.vfcal.json` 文本（同时用于内嵌的 CalibrationData）。</summary>
        public static string ToJson(CalibrationResult result)
        {
            Validate(result, "标定数据");
            return JsonSerializer.Serialize(result, JsonOptions);
        }

        /// <summary>保存 `.vfcal.json`（UTF-8，无 BOM）。</summary>
        public static void Save(string path, CalibrationResult result)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("标定文件路径为空", nameof(path));
            }
            File.WriteAllText(path, ToJson(result), new UTF8Encoding(false));
        }

        /// <summary>
        /// 读取标定文件：内容以 `{` 开头按 `.vfcal.json` 解析，否则按旧版 write_tuple 矩阵文件读取（6 个数，视为 Affine2D）。
        /// 文件不存在抛 <see cref="FileNotFoundException"/>，格式错误抛 <see cref="InvalidDataException"/>（中文说明，含文件名）。
        /// </summary>
        public static CalibrationResult Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException($"标定文件不存在：{path}", path);
            }
            byte[] bytes = File.ReadAllBytes(path);
            if (StartsWithJsonObject(bytes))
            {
                return Parse(new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF'), "标定文件 " + path);
            }
            HTuple tuple;
            try
            {
                HOperatorSet.ReadTuple(path, out tuple);
            }
            catch (HalconException ex)
            {
                throw new InvalidDataException($"标定文件 {path} 既不是 .vfcal.json，也不是 write_tuple 矩阵文件：{ex.Message}");
            }
            if (tuple.Length != 6 || tuple.Type == HTupleType.STRING || tuple.Type == HTupleType.MIXED)
            {
                throw new InvalidDataException($"标定文件 {path} 不是 .vfcal.json，按 write_tuple 读到 {tuple.Length} 个值，仿射矩阵应为 6 个数");
            }
            return new CalibrationResult
            {
                Kind = CalibrationFileKind.Affine2D,
                IsLegacyTuple = true,
                Affine2D = new Affine2DCalibration { HomMat2D = tuple.ToDArr(), TransformType = CalibrationTransformType.affine }
            };
        }

        /// <summary>解析 `.vfcal.json` 文本（文件内容或内嵌的 CalibrationData）；sourceName 用于错误信息。</summary>
        public static CalibrationResult Parse(string json, string sourceName)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException($"{sourceName} 为空");
            }
            CalibrationResult result;
            try
            {
                result = JsonSerializer.Deserialize<CalibrationResult>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{sourceName} 格式错误：{ex.Message}");
            }
            if (result == null)
            {
                throw new InvalidDataException($"{sourceName} 格式错误：内容为空");
            }
            NormalizeCameraValues(result);
            Validate(result, sourceName);
            return result;
        }

        private static void Validate(CalibrationResult result, string sourceName)
        {
            if (result == null)
            {
                throw new InvalidDataException($"{sourceName} 为空");
            }
            if (result.FormatVersion < 1 || result.FormatVersion > CalibrationResult.CurrentFormatVersion)
            {
                throw new InvalidDataException($"{sourceName} 的格式版本 {result.FormatVersion} 不受支持（当前支持 1 ~ {CalibrationResult.CurrentFormatVersion}）");
            }
            bool hasKindSection = result.Kind == CalibrationFileKind.Affine2D ? result.Affine2D != null
                : result.Kind == CalibrationFileKind.RotationCenter ? result.RotationCenter != null
                : result.Camera != null;
            if (!hasKindSection)
            {
                throw new InvalidDataException($"{sourceName} 的 Kind 为 {result.Kind}，但缺少 {result.Kind} 数据");
            }
            if (result.Affine2D != null)
            {
                double[] m = result.Affine2D.HomMat2D;
                if (m == null || m.Length != 6 || m.Any(v => !IsFinite(v)))
                {
                    throw new InvalidDataException($"{sourceName} 的 Affine2D.HomMat2D 应为 6 个有限数值");
                }
            }
            if (result.RotationCenter != null)
            {
                RotationCenterCalibration center = result.RotationCenter;
                bool optionalFinite(double? v) => !v.HasValue || IsFinite(v.Value);
                if (!IsFinite(center.Row) || !IsFinite(center.Column) || !optionalFinite(center.X) || !optionalFinite(center.Y))
                {
                    throw new InvalidDataException($"{sourceName} 的 RotationCenter 圆心坐标应为有限数值");
                }
                if (!IsFinite(center.Radius) || center.Radius < 0)
                {
                    throw new InvalidDataException($"{sourceName} 的 RotationCenter.Radius 应为不小于 0 的有限数值");
                }
                if (!optionalFinite(center.RmsError) || !optionalFinite(center.MaxError))
                {
                    throw new InvalidDataException($"{sourceName} 的 RotationCenter 误差应为有限数值");
                }
                if (center.Points != null)
                {
                    for (int i = 0; i < center.Points.Count; i++)
                    {
                        RotationCenterPoint p = center.Points[i];
                        if (p == null || !IsFinite(p.Row) || !IsFinite(p.Column) || !optionalFinite(p.Angle) || !optionalFinite(p.Residual))
                        {
                            throw new InvalidDataException($"{sourceName} 的 RotationCenter.Points 第 {i + 1} 项含无效数值");
                        }
                    }
                }
            }
            if (result.Camera != null)
            {
                CameraCalibration camera = result.Camera;
                if (camera.CamParam == null || camera.CamParam.Count < 2 || !(camera.CamParam[0].Value is string))
                {
                    throw new InvalidDataException($"{sourceName} 的 Camera.CamParam 应以 camera_type（字符串）开头");
                }
                if (camera.Pose == null || camera.Pose.Length != 7 || camera.Pose.Any(v => !IsFinite(v)))
                {
                    throw new InvalidDataException($"{sourceName} 的 Camera.Pose 应为 7 个有限数值");
                }
            }
        }

        /// <summary>System.Text.Json 把 object 读成 JsonElement：还原为字符串或数值。</summary>
        private static void NormalizeCameraValues(CalibrationResult result)
        {
            if (result.Camera?.CamParam == null)
            {
                return;
            }
            foreach (CameraParameter parameter in result.Camera.CamParam)
            {
                if (parameter?.Value is JsonElement element)
                {
                    parameter.Value = element.ValueKind == JsonValueKind.String ? element.GetString()
                        : element.ValueKind == JsonValueKind.Number ? element.GetDouble()
                        : (object)null;
                }
            }
        }

        private static bool StartsWithJsonObject(byte[] bytes)
        {
            int i = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n'))
            {
                i++;
            }
            return i < bytes.Length && bytes[i] == '{';
        }

        /// <summary>
        /// Camera 方式的输出单位：映射为 image_points_to_world_plane 的 Scale（22.11 实测：m / cm / mm / um 时输出即该单位，
        /// 数值 s 时输出为“米 ÷ s”，如 0.001 与 mm 相同；位姿的长度单位为米）。空单位按 m。
        /// </summary>
        public static bool TryGetWorldPlaneScale(string unit, out string scale)
        {
            string normalized = string.IsNullOrWhiteSpace(unit) ? "m" : unit.Trim().ToLowerInvariant();
            if (normalized == "μm")
            {
                normalized = "um";
            }
            scale = normalized == "m" || normalized == "cm" || normalized == "mm" || normalized == "um" ? normalized : null;
            return scale != null;
        }
    }
}
