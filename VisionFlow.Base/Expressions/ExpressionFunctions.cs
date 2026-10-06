using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisionFlow.Expressions
{
    /// <summary>表达式内置函数的说明（编辑器用于函数列表与提示）。</summary>
    public sealed class ExpressionFunction
    {
        public string Name { get; private set; }
        /// <summary>调用形式，如 round(x, n)。</summary>
        public string Signature { get; private set; }
        public string Description { get; private set; }
        public int MinArgs { get; private set; }
        /// <summary>最多参数个数；int.MaxValue 表示不限。</summary>
        public int MaxArgs { get; private set; }

        private readonly Func<CallNode, EvalScope, object> _invoke;

        internal ExpressionFunction(string name, string signature, string description, int minArgs, int maxArgs,
            Func<CallNode, EvalScope, object> invoke)
        {
            Name = name;
            Signature = signature;
            Description = description;
            MinArgs = minArgs;
            MaxArgs = maxArgs;
            _invoke = invoke;
        }

        internal object Invoke(CallNode call, EvalScope scope)
        {
            return _invoke(call, scope);
        }
    }

    /// <summary>内置函数表。函数名不区分大小写。</summary>
    public static class ExpressionFunctions
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
        private static readonly ConcurrentDictionary<string, Regex> RegexCache = new ConcurrentDictionary<string, Regex>();
        private static readonly List<ExpressionFunction> FunctionList = Build();
        private static readonly Dictionary<string, ExpressionFunction> Functions =
            FunctionList.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        /// <summary>全部内置函数（按定义顺序）。</summary>
        public static IReadOnlyList<ExpressionFunction> All
        {
            get { return FunctionList; }
        }

        internal static bool TryGet(string name, out ExpressionFunction function)
        {
            return Functions.TryGetValue(name, out function);
        }

        private static List<ExpressionFunction> Build()
        {
            var list = new List<ExpressionFunction>
            {
                // 数学
                Unary("abs", "abs(x)", "绝对值", (c, v) => v is long l ? (object)CheckedAbs(c, l) : Math.Abs(Num(c, 0, v))),
                UnaryDouble("sqrt", "sqrt(x)", "平方根", Math.Sqrt),
                Fixed("pow", "pow(x, y)", "x 的 y 次方", 2, (c, a) => Math.Pow(Num(c, 0, a[0]), Num(c, 1, a[1]))),
                Variadic("min", "min(a, b, ...)", "最小值", 2, (c, a) => MinMax(c, a, true)),
                Variadic("max", "max(a, b, ...)", "最大值", 2, (c, a) => MinMax(c, a, false)),
                new ExpressionFunction("round", "round(x, n)", "四舍五入到 n 位小数（n 省略时为 0）", 1, 2, (c, s) => Round(c, Args(c, s))),
                UnaryDouble("floor", "floor(x)", "向下取整", Math.Floor),
                UnaryDouble("ceil", "ceil(x)", "向上取整", Math.Ceiling),
                UnaryDouble("sin", "sin(x)", "正弦（弧度）", Math.Sin),
                UnaryDouble("cos", "cos(x)", "余弦（弧度）", Math.Cos),
                UnaryDouble("tan", "tan(x)", "正切（弧度）", Math.Tan),
                Fixed("atan2", "atan2(y, x)", "反正切，返回弧度", 2, (c, a) => Math.Atan2(Num(c, 0, a[0]), Num(c, 1, a[1]))),
                Fixed("hypot", "hypot(x, y)", "直角三角形斜边长 sqrt(x²+y²)", 2, (c, a) => Hypot(Num(c, 0, a[0]), Num(c, 1, a[1]))),
                UnaryDouble("deg", "deg(x)", "弧度转角度", x => x * 180.0 / Math.PI),
                UnaryDouble("rad", "rad(x)", "角度转弧度", x => x * Math.PI / 180.0),

                // 判断
                Unary("isnan", "isnan(x)", "是否为 NaN（非数值返回 false）", (c, v) => v is double d && double.IsNaN(d)),
                Unary("isvalid", "isvalid(x)", "是否为有效值：数值不是 NaN 或无穷，其他值不为空", (c, v) => IsValid(v)),
                new ExpressionFunction("if", "if(条件, a, b)", "条件为真取 a，否则取 b（只计算被选中的一侧）", 3, 3, If),

                // 字符串
                Unary("len", "len(s)", "字符串长度", (c, v) => (long)Str(c, 0, v).Length),
                new ExpressionFunction("substr", "substr(s, start, length)", "截取子串，start 从 0 开始，超出范围时截到末尾（length 省略时取到末尾）", 2, 3, (c, s) => Substr(c, Args(c, s))),
                Fixed("contains", "contains(s, t)", "s 是否包含 t", 2, (c, a) => Str(c, 0, a[0]).IndexOf(Str(c, 1, a[1]), StringComparison.Ordinal) >= 0),
                Fixed("startswith", "startswith(s, t)", "s 是否以 t 开头", 2, (c, a) => Str(c, 0, a[0]).StartsWith(Str(c, 1, a[1]), StringComparison.Ordinal)),
                Fixed("endswith", "endswith(s, t)", "s 是否以 t 结尾", 2, (c, a) => Str(c, 0, a[0]).EndsWith(Str(c, 1, a[1]), StringComparison.Ordinal)),
                Fixed("replace", "replace(s, old, new)", "把 s 中的 old 全部替换为 new", 3, Replace),
                Unary("trim", "trim(s)", "去除首尾空白", (c, v) => Str(c, 0, v).Trim()),
                Unary("upper", "upper(s)", "转为大写", (c, v) => Str(c, 0, v).ToUpperInvariant()),
                Unary("lower", "lower(s)", "转为小写", (c, v) => Str(c, 0, v).ToLowerInvariant()),
                Fixed("regex", "regex(s, pattern)", "s 是否匹配正则表达式 pattern", 2, RegexMatch),
                Variadic("format", "format(模板, 参数...)", "按模板格式化，如 format(\"宽度 {0:F2}\", x)", 1, Format),
                Unary("str", "str(x)", "转为文本", (c, v) => ExpressionValues.Format(v)),
                Unary("num", "num(s)", "转为数值，无法转换时为 NaN", (c, v) => ToNumber(c, v)),

                // 数组
                Unary("count", "count(arr)", "数组元素个数", (c, v) => (long)Arr(c, 0, v).Count),
                Unary("sum", "sum(arr)", "数组求和（忽略 NaN）", (c, v) => NumbersOf(c, v).Sum()),
                Unary("mean", "mean(arr)", "数组平均值（忽略 NaN，空数组为 NaN）", (c, v) => Aggregate(c, v, e => e.Average())),
                Unary("maxof", "maxof(arr)", "数组最大值（忽略 NaN，空数组为 NaN）", (c, v) => Aggregate(c, v, e => e.Max())),
                Unary("minof", "minof(arr)", "数组最小值（忽略 NaN，空数组为 NaN）", (c, v) => Aggregate(c, v, e => e.Min())),
                Fixed("at", "at(arr, i)", "取第 i 个元素（从 0 开始），越界为 NaN", 2, At)
            };
            return list;
        }

        // ---------------- 定义辅助 ----------------

        private static ExpressionFunction Unary(string name, string signature, string description,
            Func<CallNode, object, object> body)
        {
            return new ExpressionFunction(name, signature, description, 1, 1,
                (c, s) => body(c, c.Arguments[0].Evaluate(s)));
        }

        private static ExpressionFunction UnaryDouble(string name, string signature, string description,
            Func<double, double> body)
        {
            return Unary(name, signature, description, (c, v) => body(Num(c, 0, v)));
        }

        private static ExpressionFunction Fixed(string name, string signature, string description, int count,
            Func<CallNode, object[], object> body)
        {
            return new ExpressionFunction(name, signature, description, count, count, (c, s) => body(c, Args(c, s)));
        }

        private static ExpressionFunction Variadic(string name, string signature, string description, int min,
            Func<CallNode, object[], object> body)
        {
            return new ExpressionFunction(name, signature, description, min, int.MaxValue, (c, s) => body(c, Args(c, s)));
        }

        private static object[] Args(CallNode call, EvalScope scope)
        {
            return call.Arguments.Select(a => a.Evaluate(scope)).ToArray();
        }

        private static ExpressionException ArgError(CallNode call, int index, string detail)
        {
            int position = index < call.Arguments.Count ? call.Arguments[index].Position : call.Position;
            return new ExpressionException($"{call.Function.Name} 的第 {index + 1} 个参数{detail}", position);
        }

        private static double Num(CallNode call, int index, object value)
        {
            if (!ExpressionValues.IsNumber(value))
            {
                throw ArgError(call, index, $"需要数值，实际为{ExpressionValues.TypeName(value)}");
            }
            return ExpressionValues.ToDouble(value);
        }

        private static string Str(CallNode call, int index, object value)
        {
            if (value is string s)
            {
                return s;
            }
            throw ArgError(call, index, $"需要字符串，实际为{ExpressionValues.TypeName(value)}");
        }

        private static List<object> Arr(CallNode call, int index, object value)
        {
            if (!ExpressionValues.IsArray(value))
            {
                throw ArgError(call, index, $"需要数组，实际为{ExpressionValues.TypeName(value)}");
            }
            return ExpressionValues.Elements(value);
        }

        private static long Integer(CallNode call, int index, object value)
        {
            if (value is long l)
            {
                return l;
            }
            if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d
                && d >= long.MinValue && d <= long.MaxValue)
            {
                return (long)d;
            }
            throw ArgError(call, index, $"需要整数，实际为 {ExpressionValues.Format(value)}");
        }

        // ---------------- 函数实现 ----------------

        private static long CheckedAbs(CallNode call, long value)
        {
            if (value == long.MinValue)
            {
                throw new ExpressionException("abs 整数溢出", call.Position);
            }
            return Math.Abs(value);
        }

        private static double Hypot(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }

        private static object MinMax(CallNode call, object[] args, bool min)
        {
            for (int i = 0; i < args.Length; i++)
            {
                Num(call, i, args[i]);
            }
            if (args.All(a => a is long))
            {
                return min ? args.Min(a => (long)a) : args.Max(a => (long)a);
            }
            double result = ExpressionValues.ToDouble(args[0]);
            for (int i = 1; i < args.Length; i++)
            {
                double value = ExpressionValues.ToDouble(args[i]);
                result = min ? Math.Min(result, value) : Math.Max(result, value);
            }
            return result;
        }

        private static object Round(CallNode call, object[] args)
        {
            double value = Num(call, 0, args[0]);
            long digits = args.Length > 1 ? Integer(call, 1, args[1]) : 0;
            if (digits < 0 || digits > 15)
            {
                throw ArgError(call, 1, "小数位数必须在 0 到 15 之间");
            }
            if (args[0] is long l)
            {
                return l;
            }
            return Math.Round(value, (int)digits, MidpointRounding.AwayFromZero);
        }

        private static bool IsValid(object value)
        {
            if (value is double d)
            {
                return !double.IsNaN(d) && !double.IsInfinity(d);
            }
            return value != null;
        }

        private static object If(CallNode call, EvalScope scope)
        {
            object condition = call.Arguments[0].Evaluate(scope);
            if (!(condition is bool b))
            {
                throw ArgError(call, 0, $"需要布尔值，实际为{ExpressionValues.TypeName(condition)}");
            }
            return b ? call.Arguments[1].Evaluate(scope) : call.Arguments[2].Evaluate(scope);
        }

        private static object Substr(CallNode call, object[] args)
        {
            string text = Str(call, 0, args[0]);
            long start = Integer(call, 1, args[1]);
            if (start < 0)
            {
                throw ArgError(call, 1, "不能为负数");
            }
            int begin = (int)Math.Min(start, text.Length);
            int length = text.Length - begin;
            if (args.Length > 2)
            {
                long requested = Integer(call, 2, args[2]);
                if (requested < 0)
                {
                    throw ArgError(call, 2, "不能为负数");
                }
                length = (int)Math.Min(requested, length);
            }
            return text.Substring(begin, length);
        }

        private static object Replace(CallNode call, object[] args)
        {
            string text = Str(call, 0, args[0]);
            string oldValue = Str(call, 1, args[1]);
            string newValue = Str(call, 2, args[2]);
            if (oldValue.Length == 0)
            {
                throw ArgError(call, 1, "不能为空字符串");
            }
            return text.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        private static object RegexMatch(CallNode call, object[] args)
        {
            string text = Str(call, 0, args[0]);
            string pattern = Str(call, 1, args[1]);
            Regex regex;
            try
            {
                regex = RegexCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant, RegexTimeout));
            }
            catch (ArgumentException ex)
            {
                throw ArgError(call, 1, $"不是合法的正则表达式：{ex.Message}");
            }
            try
            {
                return regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new ExpressionException("正则表达式匹配超时，请简化表达式", call.Position, ex);
            }
        }

        private static object Format(CallNode call, object[] args)
        {
            string template = Str(call, 0, args[0]);
            object[] values = args.Skip(1).Select(a => ExpressionValues.IsArray(a) ? ExpressionValues.Format(a) : a).ToArray();
            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, values);
            }
            catch (FormatException ex)
            {
                throw ArgError(call, 0, $"格式模板错误：{ex.Message}");
            }
        }

        private static object ToNumber(CallNode call, object value)
        {
            if (ExpressionValues.IsNumber(value))
            {
                return ExpressionValues.ToDouble(value);
            }
            string text = Str(call, 0, value);
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? number
                : double.NaN;
        }

        private static List<double> NumbersOf(CallNode call, object value)
        {
            var numbers = new List<double>();
            foreach (object element in Arr(call, 0, value))
            {
                if (!ExpressionValues.IsNumber(element))
                {
                    throw ArgError(call, 0, $"中含有非数值元素（{ExpressionValues.TypeName(element)}）");
                }
                double number = ExpressionValues.ToDouble(element);
                if (!double.IsNaN(number))
                {
                    numbers.Add(number);
                }
            }
            return numbers;
        }

        private static object Aggregate(CallNode call, object value, Func<List<double>, double> body)
        {
            List<double> numbers = NumbersOf(call, value);
            return numbers.Count == 0 ? double.NaN : body(numbers);
        }

        private static object At(CallNode call, object[] args)
        {
            List<object> elements = Arr(call, 0, args[0]);
            long index = Integer(call, 1, args[1]);
            return index >= 0 && index < elements.Count ? elements[(int)index] : double.NaN;
        }
    }
}
