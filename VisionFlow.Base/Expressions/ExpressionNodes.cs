using System;
using System.Collections.Generic;

namespace VisionFlow.Expressions
{
    /// <summary>求值时的引用解析器：传入引用文本（花括号内的内容），返回引用的值。</summary>
    internal sealed class EvalScope
    {
        public Func<string, object> Resolve { get; set; }
    }

    internal abstract class ExprNode
    {
        public int Position { get; set; }

        public abstract object Evaluate(EvalScope scope);
    }

    internal sealed class ConstantNode : ExprNode
    {
        public object Value { get; set; }

        public override object Evaluate(EvalScope scope)
        {
            return Value;
        }
    }

    internal sealed class ReferenceNode : ExprNode
    {
        public string Path { get; set; }

        public override object Evaluate(EvalScope scope)
        {
            object value;
            try
            {
                value = scope.Resolve(Path);
            }
            catch (ExpressionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ExpressionException($"引用 {{{Path}}} 无法取值：{ex.Message}", Position, ex);
            }
            return ExpressionValues.Normalize(value);
        }
    }

    internal sealed class UnaryNode : ExprNode
    {
        public string Operator { get; set; }
        public ExprNode Operand { get; set; }

        public override object Evaluate(EvalScope scope)
        {
            object value = Operand.Evaluate(scope);
            if (Operator == "!")
            {
                if (value is bool b)
                {
                    return !b;
                }
                throw new ExpressionException($"运算符 ! 需要布尔值，实际为{ExpressionValues.TypeName(value)}", Position);
            }

            if (value is long l)
            {
                if (l == long.MinValue)
                {
                    throw new ExpressionException("整数取负溢出", Position);
                }
                return -l;
            }
            if (value is double d)
            {
                return -d;
            }
            throw new ExpressionException($"运算符 - 需要数值，实际为{ExpressionValues.TypeName(value)}", Position);
        }
    }

    internal sealed class BinaryNode : ExprNode
    {
        public string Operator { get; set; }
        public ExprNode Left { get; set; }
        public ExprNode Right { get; set; }

        public override object Evaluate(EvalScope scope)
        {
            if (Operator == "&&" || Operator == "||")
            {
                bool left = RequireBool(Left.Evaluate(scope), Left.Position);
                if (Operator == "&&" ? !left : left)
                {
                    return left;
                }
                return RequireBool(Right.Evaluate(scope), Right.Position);
            }

            object l = Left.Evaluate(scope);
            object r = Right.Evaluate(scope);
            switch (Operator)
            {
                case "==": return AreEqual(l, r);
                case "!=": return !AreEqual(l, r);
                case ">":
                case ">=":
                case "<":
                case "<=":
                    return CompareOrdered(l, r);
                default:
                    return Arithmetic(l, r);
            }
        }

        private bool RequireBool(object value, int position)
        {
            if (value is bool b)
            {
                return b;
            }
            throw new ExpressionException($"运算符 {Operator} 需要布尔值，实际为{ExpressionValues.TypeName(value)}", position);
        }

        private bool AreEqual(object l, object r)
        {
            if (ExpressionValues.IsNumber(l) && ExpressionValues.IsNumber(r))
            {
                if (l is long a && r is long b)
                {
                    return a == b;
                }
                return ExpressionValues.ToDouble(l) == ExpressionValues.ToDouble(r);
            }
            if (l == null || r == null)
            {
                return l == null && r == null;
            }
            // 字符串与其他值比较时按文本比较，例如读到的码 "123" == 123
            if (l is string || r is string)
            {
                return string.Equals(ExpressionValues.Format(l), ExpressionValues.Format(r), StringComparison.Ordinal);
            }
            if (l is bool lb && r is bool rb)
            {
                return lb == rb;
            }
            throw TypeError(l, r);
        }

        private bool CompareOrdered(object l, object r)
        {
            int comparison;
            if (ExpressionValues.IsNumber(l) && ExpressionValues.IsNumber(r))
            {
                if (l is long a && r is long b)
                {
                    comparison = a.CompareTo(b);
                }
                else
                {
                    double x = ExpressionValues.ToDouble(l);
                    double y = ExpressionValues.ToDouble(r);
                    if (double.IsNaN(x) || double.IsNaN(y))
                    {
                        return false;
                    }
                    comparison = x.CompareTo(y);
                }
            }
            else if (l is string ls && r is string rs)
            {
                comparison = string.CompareOrdinal(ls, rs);
            }
            else
            {
                throw TypeError(l, r);
            }

            switch (Operator)
            {
                case ">": return comparison > 0;
                case ">=": return comparison >= 0;
                case "<": return comparison < 0;
                default: return comparison <= 0;
            }
        }

        private object Arithmetic(object l, object r)
        {
            if (Operator == "+" && (l is string || r is string))
            {
                return ExpressionValues.Format(l) + ExpressionValues.Format(r);
            }
            if (!ExpressionValues.IsNumber(l) || !ExpressionValues.IsNumber(r))
            {
                throw TypeError(l, r);
            }

            if (l is long a && r is long b && Operator != "/")
            {
                try
                {
                    switch (Operator)
                    {
                        case "+": return checked(a + b);
                        case "-": return checked(a - b);
                        case "*": return checked(a * b);
                        default:
                            if (b == 0)
                            {
                                throw new ExpressionException("除数为 0", Right.Position);
                            }
                            return a % b;
                    }
                }
                catch (OverflowException ex)
                {
                    throw new ExpressionException($"整数运算 {a} {Operator} {b} 溢出", Position, ex);
                }
            }

            double x = ExpressionValues.ToDouble(l);
            double y = ExpressionValues.ToDouble(r);
            switch (Operator)
            {
                case "+": return x + y;
                case "-": return x - y;
                case "*": return x * y;
                default:
                    if (y == 0)
                    {
                        throw new ExpressionException("除数为 0", Right.Position);
                    }
                    return Operator == "/" ? x / y : x % y;
            }
        }

        private ExpressionException TypeError(object l, object r)
        {
            return new ExpressionException(
                $"运算符 {Operator} 不支持{ExpressionValues.TypeName(l)}与{ExpressionValues.TypeName(r)}", Position);
        }
    }

    internal sealed class ConditionalNode : ExprNode
    {
        public ExprNode Condition { get; set; }
        public ExprNode WhenTrue { get; set; }
        public ExprNode WhenFalse { get; set; }

        public override object Evaluate(EvalScope scope)
        {
            object condition = Condition.Evaluate(scope);
            if (!(condition is bool b))
            {
                throw new ExpressionException($"条件运算 ?: 的条件需要布尔值，实际为{ExpressionValues.TypeName(condition)}",
                    Condition.Position);
            }
            return b ? WhenTrue.Evaluate(scope) : WhenFalse.Evaluate(scope);
        }
    }

    internal sealed class CallNode : ExprNode
    {
        public ExpressionFunction Function { get; set; }
        public IReadOnlyList<ExprNode> Arguments { get; set; }

        public override object Evaluate(EvalScope scope)
        {
            return Function.Invoke(this, scope);
        }
    }
}
