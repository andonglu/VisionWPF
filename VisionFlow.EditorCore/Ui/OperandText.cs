using System;
using System.Globalization;
using VisionFlow.Variables;

namespace VisionFlow.Ui
{
    /// <summary>
    /// 编辑界面中操作数的文本形式：引用写 ref:模块.变量，常量直接写数字、true / false、null、"" 或文本。
    /// 参数面板与各编辑窗口共用，保证同一操作数在不同界面显示和解析一致。
    /// </summary>
    public static class OperandText
    {
        public static Operand Parse(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
            {
                return Operand.Ref(text.Substring(4).Trim());
            }
            if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            {
                return Operand.Const(null);
            }
            if (text == "\"\"")
            {
                return Operand.Const(string.Empty);
            }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double d))
            {
                return Operand.Const(d);
            }
            if (bool.TryParse(text, out bool b))
            {
                return Operand.Const(b);
            }
            return Operand.Const(text);
        }

        public static string Format(Operand operand)
        {
            if (operand == null)
            {
                return string.Empty;
            }
            return operand.IsConstant ? Convert.ToString(operand.ConstantValue, CultureInfo.CurrentCulture) : "ref:" + operand.Reference;
        }
    }
}
