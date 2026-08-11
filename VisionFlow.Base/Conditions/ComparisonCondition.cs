using System;
using VisionFlow.Core;
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
        LessOrEqual
    }

    /// <summary>
    /// 二元比较条件：左操作数 与 右操作数 比较。
    /// 两侧均为数值时按数值比较；否则按相等性比较（仅支持 == / !=）。
    /// 操作数可以是常量，也可以是变量引用（如 "匹配1.Scores[0]"、"Loop.Index"）。
    /// </summary>
    public sealed class ComparisonCondition
    {
        public Operand Left { get; set; }
        public Operand Right { get; set; }
        public ComparisonOperator Operator { get; set; }

        public bool Evaluate(FlowContext ctx)
        {
            object left = Left.GetValue(ctx);
            object right = Right.GetValue(ctx);

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

        public override string ToString()
        {
            return $"{Left} {ToSymbol(Operator)} {Right}";
        }

        private static string ToSymbol(ComparisonOperator op)
        {
            switch (op)
            {
                case ComparisonOperator.Equal: return "==";
                case ComparisonOperator.NotEqual: return "!=";
                case ComparisonOperator.Greater: return ">";
                case ComparisonOperator.GreaterOrEqual: return ">=";
                case ComparisonOperator.Less: return "<";
                case ComparisonOperator.LessOrEqual: return "<=";
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
