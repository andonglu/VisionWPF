using System;
using VisionFlow.Core;

namespace VisionFlow.Variables
{
    /// <summary>
    /// 操作数：要么是常量，要么是对上游变量（或循环变量）的引用。
    /// 用于条件表达式和循环次数来源。
    /// </summary>
    public sealed class Operand
    {
        public bool IsConstant { get; private set; }
        public object ConstantValue { get; private set; }
        public VariableReference Reference { get; private set; }

        public static Operand Const(object value)
        {
            return new Operand { IsConstant = true, ConstantValue = value };
        }

        public static Operand Ref(string path)
        {
            return new Operand { IsConstant = false, Reference = VariableReference.Parse(path) };
        }

        public object GetValue(FlowContext ctx)
        {
            return IsConstant ? ConstantValue : Reference.Resolve(ctx);
        }

        public int GetInt(FlowContext ctx)
        {
            return Convert.ToInt32(GetValue(ctx));
        }

        public double GetDouble(FlowContext ctx)
        {
            return Convert.ToDouble(GetValue(ctx));
        }

        public override string ToString()
        {
            return IsConstant ? (ConstantValue?.ToString() ?? "null") : Reference.ToString();
        }
    }
}
