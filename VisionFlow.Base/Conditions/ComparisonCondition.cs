using System;
using System.Collections.Generic;
using VisionFlow.Core;
using VisionFlow.Expressions;
using VisionFlow.Variables;

namespace VisionFlow.Conditions
{
    public enum ComparisonOperator
    {
        Equal,
        NotEqual,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
        /// <summary>左侧文本包含右侧文本。</summary>
        Contains,
        NotContains,
        StartsWith,
        EndsWith,
        /// <summary>左侧为有效值（非空；数值时不是 NaN 或无穷），不使用右操作数。</summary>
        IsValid,
        IsInvalid
    }

    /// <summary>
    /// 比较条件：左操作数 与 右操作数 比较。
    /// 两侧均为数值时按数值比较；否则按相等性比较（仅支持 == / !=）。
    /// 包含、开头是、结尾是按文本比较；有效、无效只看左操作数。
    /// 操作数可以是常量，也可以是变量引用（如 "匹配1.Scores[0]"、"Loop.Index"）。
    /// </summary>
    public sealed class ComparisonCondition : ICondition
    {
        public Operand Left { get; set; }
        public Operand Right { get; set; }
        public ComparisonOperator Operator { get; set; }

        /// <summary>只使用左操作数的比较符（有效、无效）。</summary>
        public static bool IsUnary(ComparisonOperator op)
        {
            return op == ComparisonOperator.IsValid || op == ComparisonOperator.IsInvalid;
        }

        /// <summary>是否为流程文件格式版本 1 已支持的比较符。</summary>
        public static bool IsLegacyOperator(ComparisonOperator op)
        {
            return op <= ComparisonOperator.LessOrEqual;
        }

        public bool Evaluate(FlowContext ctx)
        {
            return Evaluate(ctx, null);
        }

        public bool Evaluate(FlowContext ctx, IList<string> trace)
        {
            object left = Left.GetValue(ctx);
            object right = IsUnary(Operator) ? null : Right.GetValue(ctx);
            bool pass = Compare(left, right);
            if (trace != null)
            {
                string rightText = IsUnary(Operator) ? string.Empty : " " + Describe(Right, right);
                trace.Add($"{Describe(Left, left)} {ToSymbol(Operator)}{rightText} → {(pass ? "成立" : "不成立")}");
            }
            return pass;
        }

        /// <summary>引用显示为“引用（实际值）”，常量只显示常量本身。</summary>
        private static string Describe(Operand operand, object value)
        {
            if (operand == null || operand.IsConstant)
            {
                return operand?.ToString() ?? "null";
            }
            return $"{operand}（{(value == null ? "null" : ExpressionValues.Format(ExpressionValues.Normalize(value)))}）";
        }

        private bool Compare(object left, object right)
        {
            switch (Operator)
            {
                case ComparisonOperator.IsValid:
                    return IsValidValue(left);
                case ComparisonOperator.IsInvalid:
                    return !IsValidValue(left);
                case ComparisonOperator.Contains:
                    return Text(left).IndexOf(Text(right), StringComparison.Ordinal) >= 0;
                case ComparisonOperator.NotContains:
                    return Text(left).IndexOf(Text(right), StringComparison.Ordinal) < 0;
                case ComparisonOperator.StartsWith:
                    return Text(left).StartsWith(Text(right), StringComparison.Ordinal);
                case ComparisonOperator.EndsWith:
                    return Text(left).EndsWith(Text(right), StringComparison.Ordinal);
            }

            if (IsNumeric(left) && IsNumeric(right))
            {
                double l = Convert.ToDouble(left);
                double r = Convert.ToDouble(right);
                switch (Operator)
                {
                    case ComparisonOperator.Equal: return l == r;
                    case ComparisonOperator.NotEqual: return l != r;
                    case ComparisonOperator.Greater: return l > r;
                    case ComparisonOperator.GreaterOrEqual: return l >= r;
                    case ComparisonOperator.Less: return l < r;
                    case ComparisonOperator.LessOrEqual: return l <= r;
                }
            }

            bool equal = Equals(left, right);
            switch (Operator)
            {
                case ComparisonOperator.Equal: return equal;
                case ComparisonOperator.NotEqual: return !equal;
                default:
                    throw new InvalidOperationException($"非数值比较仅支持 Equal / NotEqual（左：{left ?? "null"}，右：{right ?? "null"}）");
            }
        }

        private static bool IsValidValue(object value)
        {
            if (value is double d)
            {
                return !double.IsNaN(d) && !double.IsInfinity(d);
            }
            if (value is float f)
            {
                return !float.IsNaN(f) && !float.IsInfinity(f);
            }
            return value != null;
        }

        private static string Text(object value)
        {
            return ExpressionValues.Format(ExpressionValues.Normalize(value));
        }

        public override string ToString()
        {
            return IsUnary(Operator) ? $"{Left} {ToSymbol(Operator)}" : $"{Left} {ToSymbol(Operator)} {Right}";
        }

        /// <summary>比较符的显示文字。</summary>
        public static string ToSymbol(ComparisonOperator op)
        {
            switch (op)
            {
                case ComparisonOperator.Equal: return "==";
                case ComparisonOperator.NotEqual: return "!=";
                case ComparisonOperator.Greater: return ">";
                case ComparisonOperator.GreaterOrEqual: return ">=";
                case ComparisonOperator.Less: return "<";
                case ComparisonOperator.LessOrEqual: return "<=";
                case ComparisonOperator.Contains: return "包含";
                case ComparisonOperator.NotContains: return "不包含";
                case ComparisonOperator.StartsWith: return "开头是";
                case ComparisonOperator.EndsWith: return "结尾是";
                case ComparisonOperator.IsValid: return "有效";
                case ComparisonOperator.IsInvalid: return "无效";
                default: return op.ToString();
            }
        }

        private static bool IsNumeric(object value)
        {
            return value is sbyte || value is byte
                || value is short || value is ushort
                || value is int || value is uint
                || value is long || value is ulong
                || value is float || value is double || value is decimal;
        }
    }
}
