using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
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

    /// <summary>长度换算的当量来源。标定结果来源待标定功能（CB-01）上线后追加。</summary>
    public enum ScaleSource
    {
        /// <summary>直接填写像素当量（毫米/像素）。</summary>
        Fixed
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
    /// Length：像素值 × PixelSize（毫米/像素）；IsArea 时 × PixelSize²；单位 um 时长度再 × 1000、面积再 × 1000²。
    /// 输入可以是单个数值或数组（如矩形测量的 Phis、卡尺的 Widths），NaN（测量失败项）原样输出。
    /// </summary>
    [ToolOutput("Value", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class AngleConvertTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        private static readonly string[] AngleParameters = { nameof(InputUnit), nameof(OutputUnit), nameof(Range), nameof(RangeMin), nameof(RangeMax) };
        private static readonly string[] LengthParameters = { nameof(ScaleSource), nameof(PixelSize), nameof(LengthUnit), nameof(IsArea) };

        [InputRef("输入值", typeof(double), AcceptsCollection = true)]
        public string ValuePath { get; set; }

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
        /// <summary>长度换算的输出单位。</summary>
        public LengthUnit LengthUnit { get; set; } = LengthUnit.mm;
        /// <summary>输入为面积（像素²）时按当量的平方换算。</summary>
        public bool IsArea { get; set; }

        public AngleConvertTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (ConvertKind == ConvertKind.Length && !(PixelSize > 0))
            {
                yield return new ToolConfigurationIssue(nameof(PixelSize), "像素当量必须大于 0");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            return ConvertKind == ConvertKind.Length
                ? !AngleParameters.Contains(propertyName)
                : !LengthParameters.Contains(propertyName);
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (ConvertKind == ConvertKind.Length)
            {
                if (!(PixelSize > 0))
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
            double[] outputs = inputs.Select(Convert).ToArray();

            SetOutput(ctx, Variable.Single(ModuleName, "Value", VariableType.Double, outputs.Length > 0 ? outputs[0] : double.NaN));
            SetOutput(ctx, Variable.Array(ModuleName, "Values", VariableType.Double, outputs));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, outputs.Length));
            if (ConvertKind == ConvertKind.Length)
            {
                string unit = LengthUnit.ToString() + (IsArea ? "²" : string.Empty);
                ctx.AddLog(FlowLogLevel.Info, isCollection
                    ? $"[单位换算] {outputs.Length} 个值，像素当量 {PixelSize:G6} mm/像素 → {unit}"
                    : $"[单位换算] {inputs[0]:G6} 像素{(IsArea ? "²" : string.Empty)} → {outputs[0]:G6} {unit}");
                return NodeResult.Ok;
            }
            string angleUnit = OutputUnit == AngleUnit.Degree ? "°" : " rad";
            ctx.AddLog(FlowLogLevel.Info, isCollection
                ? $"[角度换算] {outputs.Length} 个值，{InputUnit} → {OutputUnit}，范围 {Range}"
                : $"[角度换算] {inputs[0]:G6} → {outputs[0]:F4}{angleUnit}");
            return NodeResult.Ok;
        }

        /// <summary>按当前设置换算单个值。</summary>
        public double Convert(double value)
        {
            if (ConvertKind == ConvertKind.Length)
            {
                double scale = LengthUnit == LengthUnit.um ? PixelSize * 1000.0 : PixelSize;
                return IsArea ? value * scale * scale : value * scale;
            }
            double degrees = InputUnit == AngleUnit.Radian ? AngleMath.ToDegrees(value) : value;
            if (AngleMath.TryGetBounds(Range, RangeMin, RangeMax, out double min, out double max))
            {
                degrees = AngleMath.Fold(degrees, min, max);
            }
            return OutputUnit == AngleUnit.Radian ? AngleMath.ToRadians(degrees) : degrees;
        }

        private static double ToDouble(object value)
        {
            return value == null ? double.NaN : System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
