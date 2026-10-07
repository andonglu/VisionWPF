using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Expressions;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    public enum ArrayOperation
    {
        /// <summary>不变换，只统计。</summary>
        Statistics,
        /// <summary>按序号取一个元素（负数表示从末尾数）。</summary>
        ElementAt,
        Sort,
        /// <summary>按表达式筛选，{Item} 为元素、{ItemIndex} 为序号。</summary>
        Filter,
        /// <summary>按表达式逐元素计算。</summary>
        Map,
        /// <summary>与第二个数组首尾相接。</summary>
        Concat,
        /// <summary>与第二个数组逐元素运算。</summary>
        ElementWise,
        /// <summary>去重，保持首次出现顺序。</summary>
        Distinct
    }

    public enum ArrayElementType
    {
        Number,
        Text
    }

    public enum ArrayElementWiseOperator
    {
        Add,
        Subtract,
        Multiply,
        Divide
    }

    /// <summary>
    /// 数组处理（LD-03）：对上游数组做取值、排序、筛选、变换、拼接、逐元素运算、去重，并对结果做统计。
    /// 元素类型为数值时结果输出为 Double 数组，为文本时输出为 String 数组（Values / Value 的类型随 ElementType 变化）。
    /// 统计输出（Sum、Mean 等）总是针对结果数组计算，忽略 NaN；文本数组的统计输出为 NaN。
    /// </summary>
    [ToolOutput("Indices", VariableKind.Array, VariableType.Int)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ValidCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Sum", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Mean", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Max", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Min", VariableKind.Single, VariableType.Double)]
    [ToolOutput("StdDev", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Range", VariableKind.Single, VariableType.Double)]
    [ToolOutput("MaxIndex", VariableKind.Single, VariableType.Int)]
    [ToolOutput("MinIndex", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class ArrayProcessTool : ToolBase, IDynamicOutputTool, IExpressionTool, IToolConfigurationCheck, INotFoundPolicy
    {
        private static readonly string[] LocalNames = { "Item", "ItemIndex" };

        [InputRef("数组", typeof(IEnumerable))]
        public string ArrayPath { get; set; }

        /// <summary>第二个数组（拼接、逐元素运算使用）；逐元素运算时也可以是单个数值。</summary>
        [InputRef("第二个数组", typeof(object), Optional = true)]
        public string SecondPath { get; set; }

        public ArrayOperation Operation { get; set; } = ArrayOperation.Statistics;

        public ArrayElementType ElementType { get; set; } = ArrayElementType.Number;

        /// <summary>取值的序号（从 0 开始，负数表示从末尾数，-1 为最后一个）。</summary>
        public int Index { get; set; }

        /// <summary>排序是否降序。</summary>
        public bool Descending { get; set; }

        /// <summary>筛选、变换使用的表达式，{Item} 为当前元素，{ItemIndex} 为当前序号。</summary>
        public string Expression { get; set; } = string.Empty;

        public ArrayElementWiseOperator ElementWiseOperator { get; set; } = ArrayElementWiseOperator.Add;

        /// <summary>结果为空（如筛选后没有元素、取值越界）时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public ArrayProcessTool(string moduleName) : base(moduleName)
        {
        }

        private VariableType ElementVariableType
        {
            get { return ElementType == ArrayElementType.Number ? VariableType.Double : VariableType.String; }
        }

        private bool UsesExpression
        {
            get { return Operation == ArrayOperation.Filter || Operation == ArrayOperation.Map; }
        }

        private bool UsesSecond
        {
            get { return Operation == ArrayOperation.Concat || Operation == ArrayOperation.ElementWise; }
        }

        public IReadOnlyList<ToolOutputDef> GetDynamicOutputs()
        {
            return new[]
            {
                new ToolOutputDef { Name = "Values", Kind = VariableKind.Array, Type = ElementVariableType },
                new ToolOutputDef { Name = "Value", Kind = VariableKind.Single, Type = ElementVariableType }
            };
        }

        public IEnumerable<ToolExpressionDef> GetExpressions()
        {
            if (UsesExpression)
            {
                yield return new ToolExpressionDef("表达式", Expression) { LocalNames = LocalNames };
            }
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (UsesSecond && string.IsNullOrWhiteSpace(SecondPath))
            {
                yield return new ToolConfigurationIssue("第二个数组", $"{OperationText(Operation)}需要配置第二个数组");
            }
            if (ElementType == ArrayElementType.Text
                && (Operation == ArrayOperation.ElementWise || Operation == ArrayOperation.Statistics))
            {
                yield return new ToolConfigurationIssue("Operation", $"{OperationText(Operation)}只支持数值数组");
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            List<object> values;
            List<int> indices;
            try
            {
                List<object> input = ReadElements(ctx, ArrayPath, "数组");
                switch (Operation)
                {
                    case ArrayOperation.ElementAt:
                        int index = Index < 0 ? input.Count + Index : Index;
                        bool inRange = index >= 0 && index < input.Count;
                        values = inRange ? new List<object> { input[index] } : new List<object>();
                        indices = inRange ? new List<int> { index } : new List<int>();
                        break;
                    case ArrayOperation.Sort:
                        Sort(input, out values, out indices);
                        break;
                    case ArrayOperation.Filter:
                        Filter(ctx, input, out values, out indices);
                        break;
                    case ArrayOperation.Map:
                        values = Map(ctx, input);
                        indices = Enumerable.Range(0, values.Count).ToList();
                        break;
                    case ArrayOperation.Concat:
                        values = input.Concat(ReadElements(ctx, SecondPath, "第二个数组")).ToList();
                        indices = Enumerable.Range(0, values.Count).ToList();
                        break;
                    case ArrayOperation.ElementWise:
                        values = ElementWise(input, ReadElements(ctx, SecondPath, "第二个数组"));
                        indices = Enumerable.Range(0, values.Count).ToList();
                        break;
                    case ArrayOperation.Distinct:
                        Distinct(input, out values, out indices);
                        break;
                    default:
                        values = input;
                        indices = Enumerable.Range(0, values.Count).ToList();
                        break;
                }
            }
            catch (ArrayProcessException ex)
            {
                return NodeResult.Fail($"{ModuleName} {ex.Message}");
            }

            WriteOutputs(ctx, values, indices);
            ctx.AddLog(FlowLogLevel.Info, $"[数组处理] {OperationText(Operation)}，结果 {values.Count} 个元素");
            if (values.Count == 0)
            {
                return NotFoundOutcome.Resolve(ctx, this, $"{OperationText(Operation)}结果为空");
            }
            return NodeResult.Ok;
        }

        /// <summary>读取数组引用：集合展开为元素，单个值视为只有一个元素的数组；元素按 ElementType 转换。</summary>
        private List<object> ReadElements(FlowContext ctx, string path, string what)
        {
            object raw;
            try
            {
                raw = VariableReference.Parse(path).Resolve(ctx);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException || ex is ArgumentException
                || ex is KeyNotFoundException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
            {
                throw new ArrayProcessException($"{what}引用 '{path}' 无法取值：{ex.Message}");
            }

            IEnumerable<object> items = raw is IEnumerable enumerable && !(raw is string)
                ? enumerable.Cast<object>()
                : new[] { raw };
            var elements = new List<object>();
            int i = 0;
            foreach (object item in items)
            {
                elements.Add(ConvertElement(item, $"{what}第 {i} 个元素"));
                i++;
            }
            return elements;
        }

        private object ConvertElement(object item, string what)
        {
            object value = ExpressionValues.Normalize(item);
            if (ElementType == ArrayElementType.Text)
            {
                return ExpressionValues.Format(value);
            }
            if (ExpressionValues.IsNumber(value))
            {
                return ExpressionValues.ToDouble(value);
            }
            throw new ArrayProcessException($"{what}为{ExpressionValues.TypeName(value)}，不是数值；文本数组请把元素类型设为 Text");
        }

        private void Sort(List<object> input, out List<object> values, out List<int> indices)
        {
            IEnumerable<int> order = Enumerable.Range(0, input.Count);
            if (ElementType == ArrayElementType.Number)
            {
                // NaN 总是排在最后
                order = Descending
                    ? order.OrderBy(i => double.IsNaN((double)input[i])).ThenByDescending(i => (double)input[i])
                    : order.OrderBy(i => double.IsNaN((double)input[i])).ThenBy(i => (double)input[i]);
            }
            else
            {
                order = Descending
                    ? order.OrderByDescending(i => (string)input[i], StringComparer.Ordinal)
                    : order.OrderBy(i => (string)input[i], StringComparer.Ordinal);
            }
            indices = order.ToList();
            values = indices.Select(i => input[i]).ToList();
        }

        private void Filter(FlowContext ctx, List<object> input, out List<object> values, out List<int> indices)
        {
            CompiledExpression expression = ParseExpression();
            values = new List<object>();
            indices = new List<int>();
            for (int i = 0; i < input.Count; i++)
            {
                object result = Evaluate(ctx, expression, input[i], i);
                if (!(result is bool keep))
                {
                    throw new ArrayProcessException(
                        $"筛选表达式在第 {i} 个元素上的结果为{ExpressionValues.TypeName(result)}，应为布尔值");
                }
                if (keep)
                {
                    values.Add(input[i]);
                    indices.Add(i);
                }
            }
        }

        private List<object> Map(FlowContext ctx, List<object> input)
        {
            CompiledExpression expression = ParseExpression();
            var values = new List<object>();
            for (int i = 0; i < input.Count; i++)
            {
                values.Add(ConvertElement(Evaluate(ctx, expression, input[i], i), $"变换结果第 {i} 个元素"));
            }
            return values;
        }

        private CompiledExpression ParseExpression()
        {
            if (!ExpressionParser.TryParse(Expression, out CompiledExpression expression, out ExpressionException error))
            {
                throw new ArrayProcessException("表达式：" + error.Message);
            }
            return expression;
        }

        private static object Evaluate(FlowContext ctx, CompiledExpression expression, object item, int index)
        {
            try
            {
                return expression.Evaluate(ctx, name =>
                {
                    if (string.Equals(name, "Item", StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }
                    if (string.Equals(name, "ItemIndex", StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                    throw new InvalidOperationException($"未定义的名称 {name}");
                });
            }
            catch (ExpressionException ex)
            {
                throw new ArrayProcessException($"表达式在第 {index} 个元素上求值失败：{ex.Message}");
            }
        }

        /// <summary>逐元素运算：按 <see cref="PairingHelper"/> 的规则配对（等长一一对应、一侧为 1 则一对多、否则报错）。除数为 0 时结果为 NaN。</summary>
        private List<object> ElementWise(List<object> left, List<object> right)
        {
            if (!PairingHelper.TryGetPairCount(left.Count, right.Count, out int count, out string error))
            {
                throw new ArrayProcessException(error);
            }
            var values = new List<object>();
            for (int i = 0; i < count; i++)
            {
                double a = (double)left[PairingHelper.SideIndex(i, left.Count)];
                double b = (double)right[PairingHelper.SideIndex(i, right.Count)];
                switch (ElementWiseOperator)
                {
                    case ArrayElementWiseOperator.Subtract: values.Add(a - b); break;
                    case ArrayElementWiseOperator.Multiply: values.Add(a * b); break;
                    case ArrayElementWiseOperator.Divide: values.Add(b == 0 ? double.NaN : a / b); break;
                    default: values.Add(a + b); break;
                }
            }
            return values;
        }

        private void Distinct(List<object> input, out List<object> values, out List<int> indices)
        {
            values = new List<object>();
            indices = new List<int>();
            var seen = new HashSet<object>();
            for (int i = 0; i < input.Count; i++)
            {
                if (seen.Add(input[i]))
                {
                    values.Add(input[i]);
                    indices.Add(i);
                }
            }
        }

        private void WriteOutputs(FlowContext ctx, List<object> values, List<int> indices)
        {
            VariableType type = ElementVariableType;
            if (ElementType == ArrayElementType.Number)
            {
                List<double> numbers = values.Cast<double>().ToList();
                SetOutput(ctx, Variable.Array(ModuleName, "Values", type, numbers));
                SetOutput(ctx, Variable.Single(ModuleName, "Value", type, numbers.Count > 0 ? numbers[0] : double.NaN));
            }
            else
            {
                List<string> texts = values.Cast<string>().ToList();
                SetOutput(ctx, Variable.Array(ModuleName, "Values", type, texts));
                SetOutput(ctx, Variable.Single(ModuleName, "Value", type, texts.Count > 0 ? texts[0] : string.Empty));
            }
            SetOutput(ctx, Variable.Array(ModuleName, "Indices", VariableType.Int, indices));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, values.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, values.Count > 0));
            WriteStatistics(ctx, values);
        }

        /// <summary>对结果数组统计（忽略 NaN）；文本数组或没有有效数值时统计值为 NaN、序号为 -1。标准差为总体标准差。</summary>
        private void WriteStatistics(FlowContext ctx, List<object> values)
        {
            var valid = new List<(double Value, int Index)>();
            if (ElementType == ArrayElementType.Number)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    double value = (double)values[i];
                    if (!double.IsNaN(value))
                    {
                        valid.Add((value, i));
                    }
                }
            }

            double sum = ElementType == ArrayElementType.Number ? valid.Sum(v => v.Value) : double.NaN;
            double mean = valid.Count > 0 ? sum / valid.Count : double.NaN;
            double max = valid.Count > 0 ? valid.Max(v => v.Value) : double.NaN;
            double min = valid.Count > 0 ? valid.Min(v => v.Value) : double.NaN;
            double stdDev = valid.Count > 0 ? Math.Sqrt(valid.Sum(v => (v.Value - mean) * (v.Value - mean)) / valid.Count) : double.NaN;
            int maxIndex = valid.Count > 0 ? valid.First(v => v.Value == max).Index : -1;
            int minIndex = valid.Count > 0 ? valid.First(v => v.Value == min).Index : -1;

            SetOutput(ctx, Variable.Single(ModuleName, "ValidCount", VariableType.Int, valid.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Sum", VariableType.Double, sum));
            SetOutput(ctx, Variable.Single(ModuleName, "Mean", VariableType.Double, mean));
            SetOutput(ctx, Variable.Single(ModuleName, "Max", VariableType.Double, max));
            SetOutput(ctx, Variable.Single(ModuleName, "Min", VariableType.Double, min));
            SetOutput(ctx, Variable.Single(ModuleName, "StdDev", VariableType.Double, stdDev));
            SetOutput(ctx, Variable.Single(ModuleName, "Range", VariableType.Double, max - min));
            SetOutput(ctx, Variable.Single(ModuleName, "MaxIndex", VariableType.Int, maxIndex));
            SetOutput(ctx, Variable.Single(ModuleName, "MinIndex", VariableType.Int, minIndex));
        }

        private static string OperationText(ArrayOperation operation)
        {
            switch (operation)
            {
                case ArrayOperation.ElementAt: return "取值";
                case ArrayOperation.Sort: return "排序";
                case ArrayOperation.Filter: return "筛选";
                case ArrayOperation.Map: return "变换";
                case ArrayOperation.Concat: return "拼接";
                case ArrayOperation.ElementWise: return "逐元素运算";
                case ArrayOperation.Distinct: return "去重";
                default: return "统计";
            }
        }

        /// <summary>数组处理中可预期的数据或配置问题，转换为节点失败信息。</summary>
        private sealed class ArrayProcessException : Exception
        {
            public ArrayProcessException(string message) : base(message)
            {
            }
        }
    }
}
