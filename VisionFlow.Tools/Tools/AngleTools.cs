using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Tools.Calibration;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>角度单位。</summary>
    public enum AngleUnit
    {
        /// <summary>弧度（HALCON 算子与匹配、测量工具的输出单位）。</summary>
        Radian,
        /// <summary>角度（度）。</summary>
        Degree
    }

    /// <summary>角度范围：把角度按周期折算到区间内，区间宽度即周期。</summary>
    public enum AngleRange
    {
        /// <summary>不限制，只换算单位。</summary>
        None,
        /// <summary>[-180°, 180°)，周期 360°。</summary>
        Minus180To180,
        /// <summary>[0°, 360°)，周期 360°。</summary>
        ZeroTo360,
        /// <summary>[-90°, 90°)，周期 180°（方向不分正反，如直线、矩形长边）。</summary>
        Minus90To90,
        /// <summary>[0°, 180°)，周期 180°。</summary>
        ZeroTo180,
        /// <summary>[-45°, 45°)，周期 90°（近似正方形目标，长短边不区分）。</summary>
        Minus45To45,
        /// <summary>[RangeMin, RangeMax)，周期 RangeMax - RangeMin（单位：度）。</summary>
        Custom
    }

    /// <summary>角度换算与范围折算的公共计算。</summary>
    public static class AngleMath
    {
        public static double ToDegrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        public static double ToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        /// <summary>把角度（度）按周期 max - min 折算到 [min, max)；NaN / 无穷原样返回。</summary>
        public static double Fold(double degrees, double min, double max)
        {
            if (!double.IsFinite(degrees))
            {
                return degrees;
            }
            double period = max - min;
            double folded = min + (((degrees - min) % period) + period) % period;
            // 浮点误差可能得到恰好等于上限的值，按半开区间归到下限
            return folded >= max ? min : folded;
        }

        /// <summary>取预设范围的上下限（度）；None 返回 false。</summary>
        public static bool TryGetBounds(AngleRange range, double customMin, double customMax, out double min, out double max)
        {
            switch (range)
            {
                case AngleRange.Minus180To180: min = -180; max = 180; return true;
                case AngleRange.ZeroTo360: min = 0; max = 360; return true;
                case AngleRange.Minus90To90: min = -90; max = 90; return true;
                case AngleRange.ZeroTo180: min = 0; max = 180; return true;
                case AngleRange.Minus45To45: min = -45; max = 45; return true;
                case AngleRange.Custom: min = customMin; max = customMax; return true;
                default: min = 0; max = 0; return false;
            }
        }
    }

    /// <summary>换算类别。</summary>
    public enum ConvertKind
    {
        /// <summary>角度：弧度/角度互转与范围折算（默认，即原有行为）。</summary>
        Angle,
        /// <summary>长度：像素长度（或面积）换算为物理单位。</summary>
        Length
    }

    /// <summary>长度换算的当量来源（按数字保存，新值追加在末尾）。</summary>
    public enum ScaleSource
    {
        /// <summary>直接填写像素当量（毫米/像素），默认，即原有行为。</summary>
        Fixed,
        /// <summary>由标定矩阵求当量：引用的标定矩阵优先，否则读取标定文件（.vfcal.json 的 Affine2D 段或旧版 write_tuple 矩阵文件）。</summary>
        Calibration
    }

    /// <summary>长度换算的输出单位（面积时为其平方）。</summary>
    public enum LengthUnit
    {
        mm,
        um
    }

    /// <summary>
    /// 单位换算（工具箱原名“角度换算”，ID 与类型名不变）。
    /// Angle：弧度与角度互转，并可把角度折算到指定范围（如 -90°~90°、0°~180°）。
    /// 范围按周期折算而不是截断：区间宽度即周期，例如 [-90°, 90°) 把 100° 折算为 -80°，
    /// 适用于方向不分正反的目标（矩形、直线）；需要区分正反方向时请选 360° 宽的范围。
    /// Length：像素值 × 当量（毫米/像素）；IsArea 时 × 当量²；单位 um 时长度再 × 1000、面积再 × 1000²。
    /// 当量来源 Fixed 时为 PixelSize；Calibration 时由标定矩阵 [a, b, c; d, e, f]（图像 (行, 列) → 物理 (X, Y)）求得：
    /// 行方向缩放 √(a² + d²)、列方向缩放 √(b² + e²)（与 hom_mat2d_to_affine_par 的缩放等价，镜像时取正），取两者平均，
    /// 相差超过 1% 时日志给出警告；再按标定单位折算为毫米/像素（m ×1000、cm ×10、mm ×1、um ÷1000）。
    /// 旧版 write_tuple 矩阵文件、未注明单位的 .vfcal.json 与引用的裸矩阵都不带单位信息，按 mm 处理。
    /// 输入可以是单个数值或数组（如矩形测量的 Phis、卡尺的 Widths），NaN（测量失败项）原样输出。
    /// </summary>
    [ToolOutput("Value", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class AngleConvertTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>行、列方向缩放的相对差（相对两者平均值）超过该值时日志给出警告。</summary>
        public const double ScaleMismatchTolerance = 0.01;

        private static readonly string[] AngleParameters = { nameof(InputUnit), nameof(OutputUnit), nameof(Range), nameof(RangeMin), nameof(RangeMax) };
        private static readonly string[] LengthParameters = { nameof(ScaleSource), nameof(PixelSize), nameof(LengthUnit), nameof(IsArea), nameof(MatrixPath), nameof(CalibrationFile) };
        private static readonly string[] CalibrationParameters = { nameof(MatrixPath), nameof(CalibrationFile) };

        private readonly CalibrationSourceCache _calibrationCache = new CalibrationSourceCache();

        [InputRef("输入值", typeof(double), AcceptsCollection = true)]
        public string ValuePath { get; set; }

        /// <summary>标定矩阵引用（ScaleSource = Calibration 时可选，如坐标转换或畸变校正的 Matrix）；引用后不读标定文件。裸矩阵不带单位，按 mm 处理。</summary>
        [InputRef("标定矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        /// <summary>换算类别。</summary>
        public ConvertKind ConvertKind { get; set; } = ConvertKind.Angle;
        /// <summary>输入单位。</summary>
        public AngleUnit InputUnit { get; set; } = AngleUnit.Radian;
        /// <summary>输出单位。</summary>
        public AngleUnit OutputUnit { get; set; } = AngleUnit.Degree;
        /// <summary>角度范围。</summary>
        public AngleRange Range { get; set; } = AngleRange.None;
        /// <summary>自定义范围下限（度，含）。</summary>
        public double RangeMin { get; set; } = -90;
        /// <summary>自定义范围上限（度，不含）；上下限之差即周期，须在 (0, 360] 内。</summary>
        public double RangeMax { get; set; } = 90;
        /// <summary>长度换算的当量来源。</summary>
        public ScaleSource ScaleSource { get; set; } = ScaleSource.Fixed;
        /// <summary>像素当量（毫米/像素），须大于 0。</summary>
        public double PixelSize { get; set; } = 1;
        /// <summary>标定文件（ScaleSource = Calibration 且未引用标定矩阵时使用）：.vfcal.json（须带 Affine2D 段）或旧版 write_tuple 矩阵文件。</summary>
        public string CalibrationFile { get; set; }
        /// <summary>长度换算的输出单位。</summary>
        public LengthUnit LengthUnit { get; set; } = LengthUnit.mm;
        /// <summary>输入为面积（像素²）时按当量的平方换算。</summary>
        public bool IsArea { get; set; }

        public AngleConvertTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (ConvertKind != ConvertKind.Length)
            {
                yield break;
            }
            if (ScaleSource == ScaleSource.Fixed)
            {
                if (!(PixelSize > 0))
                {
                    yield return new ToolConfigurationIssue(nameof(PixelSize), "像素当量必须大于 0");
                }
                yield break;
            }
            if (UsesMatrixReference)
            {
                yield break;
            }
            if (string.IsNullOrWhiteSpace(CalibrationFile))
            {
                yield return new ToolConfigurationIssue(nameof(CalibrationFile), NoCalibrationSourceMessage);
                yield break;
            }
            // 文件缺失不在校验阶段报（沿用标定线约定，文件可能在部署时才放置），由运行报告；文件存在时检查格式、类型与单位
            if (!System.IO.File.Exists(CalibrationFile))
            {
                yield break;
            }
            string error;
            try
            {
                error = CheckCalibration(LoadCalibration(), out _, out _);
            }
            catch (Exception ex) when (CalibrationSourceCache.IsLoadException(ex))
            {
                error = ex.Message;
            }
            if (error != null)
            {
                yield return new ToolConfigurationIssue(nameof(CalibrationFile), error);
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            if (ConvertKind != ConvertKind.Length)
            {
                return !LengthParameters.Contains(propertyName);
            }
            if (AngleParameters.Contains(propertyName))
            {
                return false;
            }
            return ScaleSource == ScaleSource.Calibration
                ? propertyName != nameof(PixelSize)
                : !CalibrationParameters.Contains(propertyName);
        }

        public override NodeResult Run(FlowContext ctx)
        {
            double millimetersPerPixel = PixelSize;
            if (ConvertKind == ConvertKind.Length)
            {
                if (ScaleSource == ScaleSource.Calibration)
                {
                    if (!TryGetCalibrationScale(ctx, out millimetersPerPixel, out string error))
                    {
                        return NodeResult.Fail($"{ModuleName} {error}");
                    }
                }
                else if (!(PixelSize > 0))
                {
                    return NodeResult.Fail($"{ModuleName} 像素当量必须大于 0");
                }
            }
            else if (Range == AngleRange.Custom && !(RangeMax > RangeMin && RangeMax - RangeMin <= 360))
            {
                return NodeResult.Fail($"{ModuleName} 自定义角度范围无效：上限须大于下限，且宽度不超过 360°");
            }

            object raw = VariableReference.Parse(ValuePath).Resolve(ctx);
            bool isCollection = raw is IEnumerable && !(raw is string);
            List<double> inputs = isCollection
                ? ((IEnumerable)raw).Cast<object>().Select(ToDouble).ToList()
                : new List<double> { ToDouble(raw) };
            double[] outputs = inputs.Select(value => Convert(value, millimetersPerPixel)).ToArray();

            SetOutput(ctx, Variable.Single(ModuleName, "Value", VariableType.Double, outputs.Length > 0 ? outputs[0] : double.NaN));
            SetOutput(ctx, Variable.Array(ModuleName, "Values", VariableType.Double, outputs));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, outputs.Length));
            if (ConvertKind == ConvertKind.Length)
            {
                string unit = LengthUnit.ToString() + (IsArea ? "²" : string.Empty);
                ctx.AddLog(FlowLogLevel.Info, isCollection
                    ? $"[单位换算] {outputs.Length} 个值，像素当量 {millimetersPerPixel:G6} mm/像素 → {unit}"
                    : $"[单位换算] {inputs[0]:G6} 像素{(IsArea ? "²" : string.Empty)} → {outputs[0]:G6} {unit}");
                return NodeResult.Ok;
            }
            string angleUnit = OutputUnit == AngleUnit.Degree ? "°" : " rad";
            ctx.AddLog(FlowLogLevel.Info, isCollection
                ? $"[角度换算] {outputs.Length} 个值，{InputUnit} → {OutputUnit}，范围 {Range}"
                : $"[角度换算] {inputs[0]:G6} → {outputs[0]:F4}{angleUnit}");
            return NodeResult.Ok;
        }

        /// <summary>按当前设置换算单个值；长度换算使用固定当量 PixelSize（Calibration 来源的当量在运行时由标定求得）。</summary>
        public double Convert(double value)
        {
            return Convert(value, PixelSize);
        }

        /// <summary>按当前设置换算单个值，长度换算使用给定的当量（毫米/像素）。</summary>
        public double Convert(double value, double millimetersPerPixel)
        {
            if (ConvertKind == ConvertKind.Length)
            {
                double scale = LengthUnit == LengthUnit.um ? millimetersPerPixel * 1000.0 : millimetersPerPixel;
                return IsArea ? value * scale * scale : value * scale;
            }
            double degrees = InputUnit == AngleUnit.Radian ? AngleMath.ToDegrees(value) : value;
            if (AngleMath.TryGetBounds(Range, RangeMin, RangeMax, out double min, out double max))
            {
                degrees = AngleMath.Fold(degrees, min, max);
            }
            return OutputUnit == AngleUnit.Radian ? AngleMath.ToRadians(degrees) : degrees;
        }

        /// <summary>
        /// 仿射矩阵（[a, b, c, d, e, f]，图像 (行, 列) → 物理 (X, Y)）的行、列方向缩放：
        /// 行方向 √(a² + d²)、列方向 √(b² + e²)，即矩阵两列的范数（镜像时同样为正）。纯计算，不调用 HALCON。
        /// </summary>
        public static void GetAxisScales(double[] homMat2D, out double rowScale, out double columnScale)
        {
            rowScale = Math.Sqrt(homMat2D[0] * homMat2D[0] + homMat2D[3] * homMat2D[3]);
            columnScale = Math.Sqrt(homMat2D[1] * homMat2D[1] + homMat2D[4] * homMat2D[4]);
        }

        /// <summary>标定单位折算为毫米的系数；空单位按 mm（不带单位信息的来源沿用 mm 惯例）。不支持的单位返回 false。</summary>
        public static bool TryGetMillimetersPerUnit(string unit, out double millimetersPerUnit)
        {
            string normalized = string.IsNullOrWhiteSpace(unit) ? "mm" : unit.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "m": millimetersPerUnit = 1000.0; return true;
                case "cm": millimetersPerUnit = 10.0; return true;
                case "mm": millimetersPerUnit = 1.0; return true;
                case "um":
                case "μm":
                case "µm": millimetersPerUnit = 0.001; return true;
                default: millimetersPerUnit = double.NaN; return false;
            }
        }

        private const string NoCalibrationSourceMessage = "当量来源为标定，请配置标定文件或引用标定矩阵";

        private bool UsesMatrixReference => !string.IsNullOrWhiteSpace(MatrixPath);

        private string SourceName => CalibrationSourceCache.SourceName(CalibrationSource.File, CalibrationFile);

        private CalibrationResult LoadCalibration()
        {
            return _calibrationCache.Load(CalibrationSource.File, CalibrationFile, null);
        }

        /// <summary>标定内容须带 Affine2D 段且单位受支持；不符时返回“来源 + 实际类型 + 期望类型”或单位说明（校验与运行共用同一措辞）。</summary>
        private string CheckCalibration(CalibrationResult calibration, out double[] matrix, out double millimetersPerUnit)
        {
            matrix = null;
            millimetersPerUnit = double.NaN;
            if (calibration.Affine2D?.HomMat2D == null || calibration.Affine2D.HomMat2D.Length != 6)
            {
                return $"{SourceName} 的类型为 {calibration.Kind}（包含 {calibration.SectionsText()}），单位换算需要 Affine2D 数据";
            }
            string unit = calibration.IsLegacyTuple ? null : calibration.Unit;
            if (!TryGetMillimetersPerUnit(unit, out millimetersPerUnit))
            {
                return $"{SourceName} 的单位“{unit}”不受支持，单位换算只支持 m / cm / mm / um";
            }
            matrix = calibration.Affine2D.HomMat2D;
            return null;
        }

        /// <summary>求标定当量（毫米/像素）：引用了标定矩阵时直接用（不带单位，按 mm），否则读取标定文件；过程写入日志。</summary>
        private bool TryGetCalibrationScale(FlowContext ctx, out double millimetersPerPixel, out string error)
        {
            millimetersPerPixel = double.NaN;
            error = null;
            double[] matrix;
            double millimetersPerUnit;
            string source;
            if (UsesMatrixReference)
            {
                try
                {
                    matrix = VariableReference.Parse(MatrixPath).Resolve<HomMat2D>(ctx).Data.ToDArr();
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException
                    || ex is FormatException || ex is ArgumentException || ex is OverflowException)
                {
                    error = $"标定矩阵引用无效：{MatrixPath}，{ex.Message}";
                    return false;
                }
                millimetersPerUnit = 1.0;
                source = string.IsNullOrWhiteSpace(CalibrationFile)
                    ? $"引用的标定矩阵 {MatrixPath}（不带单位，按 mm）"
                    : $"引用的标定矩阵 {MatrixPath}（不带单位，按 mm；已引用矩阵，标定文件 {CalibrationFile} 不参与）";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(CalibrationFile))
                {
                    error = NoCalibrationSourceMessage;
                    return false;
                }
                CalibrationResult calibration;
                try
                {
                    calibration = LoadCalibration();
                }
                catch (Exception ex) when (CalibrationSourceCache.IsLoadException(ex))
                {
                    error = ex.Message;
                    return false;
                }
                error = CheckCalibration(calibration, out matrix, out millimetersPerUnit);
                if (error != null)
                {
                    return false;
                }
                string unit = calibration.IsLegacyTuple ? "旧版矩阵文件无单位，按 mm" : string.IsNullOrWhiteSpace(calibration.Unit) ? "未注明单位，按 mm" : "单位 " + calibration.Unit.Trim();
                source = $"{SourceName}（{unit}）";
            }

            if (matrix == null || matrix.Length != 6)
            {
                error = "标定矩阵应为 6 个数";
                return false;
            }
            GetAxisScales(matrix, out double rowScale, out double columnScale);
            double rowMillimeters = rowScale * millimetersPerUnit;
            double columnMillimeters = columnScale * millimetersPerUnit;
            if (!(rowMillimeters > 0) || !(columnMillimeters > 0) || double.IsInfinity(rowMillimeters) || double.IsInfinity(columnMillimeters))
            {
                error = $"标定矩阵的缩放无效（行方向 {rowMillimeters:G6}、列方向 {columnMillimeters:G6} mm/像素），无法求像素当量";
                return false;
            }
            millimetersPerPixel = (rowMillimeters + columnMillimeters) / 2.0;
            double mismatch = Math.Abs(rowMillimeters - columnMillimeters) / millimetersPerPixel;
            bool exceeds = mismatch > ScaleMismatchTolerance;
            ctx.AddLog(exceeds ? FlowLogLevel.Warning : FlowLogLevel.Info,
                $"[单位换算] 标定当量来自{source}：行方向 {rowMillimeters:G6}、列方向 {columnMillimeters:G6} mm/像素，相差 {mismatch * 100:F2}%"
                + (exceeds ? $"（超过 {ScaleMismatchTolerance * 100:F0}%，行列缩放不一致），" : "，")
                + $"取平均 {millimetersPerPixel:G6} mm/像素");
            return true;
        }

        private static double ToDouble(object value)
        {
            return value == null ? double.NaN : System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
