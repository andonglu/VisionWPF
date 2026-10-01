using System;
using System.Collections.Generic;
using System.Globalization;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>数值区间分类的取值预处理。</summary>
    public enum ClassifyValueMode
    {
        /// <summary>按原值分类。</summary>
        Raw,
        /// <summary>输入为弧度：转为角度并归一到 [0, 360)。</summary>
        RadiansToDegrees,
        /// <summary>输入为角度：归一到 [0, 360)。</summary>
        Degrees
    }

    /// <summary>一条分类规则：Lower &lt; 值 &lt; Upper 时取 Label。</summary>
    public sealed class ClassifyRule
    {
        public string Label { get; set; }
        public double Lower { get; set; }
        public double Upper { get; set; }
    }

    /// <summary>
    /// 数值区间分类：按“标签=下限~上限”规则把一个数值映射为文字标签（如按转角判断正放/侧放/倒放），
    /// 替代多层 IfElse。规则按顺序匹配，区间为开区间（与 HALCON 示例中的 &gt; / &lt; 判定一致）；
    /// 同一标签可出现多次（多个区间）；都不满足时取 DefaultLabel；值为 NaN（如上游未找到）时取 InvalidLabel。
    /// 规则示例：侧放=45~135;侧放=225~315;倒放=135~225
    /// </summary>
    [ToolOutput("Label", VariableKind.Single, VariableType.String)]
    [ToolOutput("Matched", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Value", VariableKind.Single, VariableType.Double)]
    [ToolOutput("RuleIndex", VariableKind.Single, VariableType.Int)]
    public sealed class RangeClassifyTool : ToolBase
    {
        [InputRef("数值", typeof(double))]
        public string ValuePath { get; set; }

        public ClassifyValueMode ValueMode { get; set; } = ClassifyValueMode.Raw;
        /// <summary>分类规则：标签=下限~上限，多条用分号分隔。</summary>
        public string Rules { get; set; } = string.Empty;
        /// <summary>不满足任何规则时的标签。</summary>
        public string DefaultLabel { get; set; } = "其他";
        /// <summary>值无效（NaN / 无穷）时的标签。</summary>
        public string InvalidLabel { get; set; } = "无效";

        public RangeClassifyTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (!TryParseRules(Rules, out List<ClassifyRule> rules, out string error))
            {
                return NodeResult.Fail($"{ModuleName} 规则格式错误：{error}");
            }

            double value = Normalize(Input<double>(ctx, ValuePath), ValueMode);
            string label;
            int ruleIndex = -1;
            if (!double.IsFinite(value))
            {
                label = InvalidLabel;
            }
            else
            {
                ruleIndex = rules.FindIndex(r => value > r.Lower && value < r.Upper);
                label = ruleIndex >= 0 ? rules[ruleIndex].Label : DefaultLabel;
            }

            SetOutput(ctx, Variable.Single(ModuleName, "Label", VariableType.String, label));
            SetOutput(ctx, Variable.Single(ModuleName, "Matched", VariableType.Bool, ruleIndex >= 0));
            SetOutput(ctx, Variable.Single(ModuleName, "Value", VariableType.Double, value));
            SetOutput(ctx, Variable.Single(ModuleName, "RuleIndex", VariableType.Int, ruleIndex));
            ctx.AddLog(FlowLogLevel.Info, $"[数值区间分类] 值={value:F3} → {label}");
            return NodeResult.Ok;
        }

        public static double Normalize(double value, ClassifyValueMode mode)
        {
            if (!double.IsFinite(value) || mode == ClassifyValueMode.Raw)
            {
                return value;
            }
            double degrees = mode == ClassifyValueMode.RadiansToDegrees ? AngleMath.ToDegrees(value) : value;
            return AngleMath.Fold(degrees, 0, 360);
        }

        public static bool TryParseRules(string text, out List<ClassifyRule> rules, out string error)
        {
            rules = new List<ClassifyRule>();
            error = null;
            foreach (string raw in (text ?? string.Empty).Split(new[] { ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                if (part.Length == 0)
                {
                    continue;
                }
                int equals = part.IndexOf('=');
                int tilde = part.IndexOf('~', Math.Max(0, equals));
                if (equals <= 0 || tilde < 0)
                {
                    error = $"“{part}” 应为 标签=下限~上限";
                    return false;
                }
                string label = part.Substring(0, equals).Trim();
                if (!double.TryParse(part.Substring(equals + 1, tilde - equals - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lower)
                    || !double.TryParse(part.Substring(tilde + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double upper))
                {
                    error = $"“{part}” 的上下限不是有效数字";
                    return false;
                }
                if (lower >= upper)
                {
                    error = $"“{part}” 的下限必须小于上限";
                    return false;
                }
                rules.Add(new ClassifyRule { Label = label, Lower = lower, Upper = upper });
            }
            return true;
        }
    }
}
