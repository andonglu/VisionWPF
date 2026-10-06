using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VisionFlow.Variables;

namespace VisionFlow.Expressions
{
    /// <summary>
    /// 表达式的值模型与转换。表达式内部只使用以下几种值：
    /// long（整数）、double（小数）、bool、string、null，以及原样保留的集合（数组）和其他对象。
    /// </summary>
    public static class ExpressionValues
    {
        /// <summary>把外部值（变量值、函数结果）规范为表达式内部的值。</summary>
        public static object Normalize(object value)
        {
            switch (value)
            {
                case null: return null;
                case bool _: return value;
                case string _: return value;
                case long _: return value;
                case double _: return value;
                case int i: return (long)i;
                case short s: return (long)s;
                case sbyte sb: return (long)sb;
                case byte b: return (long)b;
                case ushort us: return (long)us;
                case uint ui: return (long)ui;
                case ulong ul: return ul <= long.MaxValue ? (object)(long)ul : (double)ul;
                case float f: return (double)f;
                case decimal d: return (double)d;
                default: return value;
            }
        }

        public static bool IsNumber(object value)
        {
            return value is long || value is double;
        }

        /// <summary>是否为数组（集合）值；字符串不算数组。</summary>
        public static bool IsArray(object value)
        {
            return value is IEnumerable && !(value is string);
        }

        public static double ToDouble(object value)
        {
            return value is long l ? l : (double)value;
        }

        /// <summary>值的文本形式（不区分区域设置）：整数原样，小数为最短往返格式，布尔为 true / false，null 为空串。</summary>
        public static string Format(object value)
        {
            switch (value)
            {
                case null: return string.Empty;
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return d.ToString(CultureInfo.InvariantCulture);
                case IEnumerable items:
                    return "[" + string.Join(", ", items.Cast<object>().Select(i => Format(Normalize(i)))) + "]";
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>值类型的中文名，用于错误提示。</summary>
        public static string TypeName(object value)
        {
            switch (value)
            {
                case null: return "空值";
                case long _: return "整数";
                case double _: return "小数";
                case bool _: return "布尔";
                case string _: return "字符串";
                case IEnumerable _: return "数组";
                default: return value.GetType().Name;
            }
        }

        /// <summary>
        /// 把表达式结果转换为指定的变量类型。Int 只接受整数，或没有小数部分的有限小数；
        /// Double 接受数值；Bool 只接受布尔；String 接受任意值（按 <see cref="Format"/>）；Object 原样返回。
        /// </summary>
        public static object ConvertTo(object value, VariableType type)
        {
            value = Normalize(value);
            switch (type)
            {
                case VariableType.Int:
                    if (value is long l && l >= int.MinValue && l <= int.MaxValue)
                    {
                        return (int)l;
                    }
                    if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d)
                        && Math.Floor(d) == d && d >= int.MinValue && d <= int.MaxValue)
                    {
                        return (int)d;
                    }
                    throw new ExpressionException($"值 {Format(value)}（{TypeName(value)}）不能转换为 Int", -1);
                case VariableType.Double:
                    if (IsNumber(value))
                    {
                        return ToDouble(value);
                    }
                    throw new ExpressionException($"值 {Format(value)}（{TypeName(value)}）不能转换为 Double", -1);
                case VariableType.Bool:
                    if (value is bool)
                    {
                        return value;
                    }
                    throw new ExpressionException($"值 {Format(value)}（{TypeName(value)}）不能转换为 Bool", -1);
                case VariableType.String:
                    return Format(value);
                default:
                    return value;
            }
        }

        /// <summary>把数组值展开为规范后的元素列表。</summary>
        internal static List<object> Elements(object array)
        {
            return ((IEnumerable)array).Cast<object>().Select(Normalize).ToList();
        }
    }
}
