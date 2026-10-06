using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Expressions;
using VisionFlow.Variables;

namespace VisionFlow.Nodes
{
    /// <summary>Switch 对外暴露的公共输出：每个分支各自给出取值（<see cref="SwitchCaseNode.OutputValues"/>）。</summary>
    public sealed class SwitchOutputDef
    {
        public string Name { get; set; }
        public VariableKind Kind { get; set; }
        public VariableType Type { get; set; }
        public string ClrTypeName { get; set; }
    }

    /// <summary>
    /// Switch 的一个分支：匹配值（逗号分隔，任一相等即命中）和分支内的子节点；默认分支没有匹配值。
    /// 分支作为节点出现在流程树中，可以选中、插入子节点、上移下移；默认分支总在最后且不能删除。
    /// </summary>
    public sealed class SwitchCaseNode : FlowNode
    {
        public List<FlowNode> Children { get; } = new List<FlowNode>();

        public bool IsDefault { get; set; }

        /// <summary>匹配值，多个用逗号分隔，如 "1, 2" 或 "正放,侧放"。</summary>
        public string Values { get; set; } = string.Empty;

        /// <summary>本分支给出的公共输出取值（按输出名，不区分大小写）。</summary>
        public Dictionary<string, Operand> OutputValues { get; } = new Dictionary<string, Operand>(StringComparer.OrdinalIgnoreCase);

        public override bool IsComposite
        {
            get { return true; }
        }

        public SwitchCaseNode(string name, string values = "", bool isDefault = false) : base(name)
        {
            Values = values ?? string.Empty;
            IsDefault = isDefault;
        }

        /// <summary>拆分后的匹配值（去掉首尾空白与空项）。</summary>
        public IReadOnlyList<string> MatchValues
        {
            get
            {
                return (Values ?? string.Empty)
                    .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .Where(v => v.Length > 0)
                    .ToList();
            }
        }

        /// <summary>
        /// 选择值是否命中本分支：选择值为数值且匹配值能解析为数值时按数值比较，否则按文本（区分大小写）比较。
        /// </summary>
        public bool Matches(object selector)
        {
            object value = ExpressionValues.Normalize(selector);
            string text = ExpressionValues.Format(value);
            foreach (string candidate in MatchValues)
            {
                if (ExpressionValues.IsNumber(value)
                    && double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    if (ExpressionValues.ToDouble(value) == number)
                    {
                        return true;
                    }
                    continue;
                }
                if (string.Equals(text, candidate, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            return RunChildren(Children, ctx);
        }
    }

    /// <summary>
    /// Switch 多分支：按选择值（常量或引用，如 {分类1.Label}、{匹配1.BestModelIndex}）执行第一个匹配的分支，
    /// 都不匹配时执行默认分支。分支内部输出对外不可见，对外只暴露公共输出（与 IfElse 相同）。
    /// Cases 中只能放 <see cref="SwitchCaseNode"/>，默认分支有且只有一个并且在最后。
    /// </summary>
    public sealed class SwitchNode : FlowNode
    {
        public Operand Selector { get; set; }

        /// <summary>分支列表（元素均为 <see cref="SwitchCaseNode"/>），类型为 FlowNode 列表以便复用通用的结构编辑。</summary>
        public List<FlowNode> Cases { get; } = new List<FlowNode>();

        public List<SwitchOutputDef> Outputs { get; } = new List<SwitchOutputDef>();

        public override bool IsComposite
        {
            get { return true; }
        }

        public SwitchNode(string name, Operand selector = null) : base(name)
        {
            Selector = selector;
        }

        public IEnumerable<SwitchCaseNode> CaseNodes
        {
            get { return Cases.OfType<SwitchCaseNode>(); }
        }

        public SwitchCaseNode DefaultCase
        {
            get { return CaseNodes.FirstOrDefault(c => c.IsDefault); }
        }

        /// <summary>选择值匹配的分支：第一个匹配的普通分支，否则默认分支（没有时为 null）。</summary>
        public SwitchCaseNode SelectCase(object selector)
        {
            return CaseNodes.FirstOrDefault(c => !c.IsDefault && c.Matches(selector)) ?? DefaultCase;
        }

        /// <summary>在默认分支之前新增一个普通分支（名称“分支 N”，匹配值 N），返回新分支。</summary>
        public SwitchCaseNode AddCase()
        {
            int number = CaseNodes.Count(c => !c.IsDefault) + 1;
            while (CaseNodes.Any(c => c.Name == "分支 " + number))
            {
                number++;
            }
            var switchCase = new SwitchCaseNode("分支 " + number, number.ToString(CultureInfo.InvariantCulture));
            int defaultIndex = Cases.FindIndex(c => c is SwitchCaseNode existing && existing.IsDefault);
            Cases.Insert(defaultIndex < 0 ? Cases.Count : defaultIndex, switchCase);
            return switchCase;
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            if (Selector == null)
            {
                return NodeResult.Fail($"{Name} 未设置选择值");
            }

            object selector = Selector.GetValue(ctx);
            SwitchCaseNode selected = SelectCase(selector);
            string selectorText = selector == null ? "null" : ExpressionValues.Format(ExpressionValues.Normalize(selector));
            if (selected == null)
            {
                return NodeResult.Fail($"{Name} 选择值 {selectorText} 没有匹配的分支，且没有默认分支");
            }
            ctx.AddLog(FlowLogLevel.Info, $"[分支] {Name}：{Selector}（{selectorText}） → {selected.Name}", Id, Name);

            NodeResult result = selected.Execute(ctx);
            if (!result.IsSuccess)
            {
                return result;
            }
            if (ctx.LoopControl != LoopControlSignal.None)
            {
                return NodeResult.Ok;
            }

            foreach (SwitchOutputDef output in Outputs)
            {
                if (!selected.OutputValues.TryGetValue(output.Name, out Operand operand) || operand == null)
                {
                    return NodeResult.Fail($"{Name}.{output.Name} 未配置分支“{selected.Name}”的取值");
                }
                object value = operand.GetValue(ctx);
                ctx.SetVariable(CreateVariable(output, value));
                ctx.AddLog(FlowLogLevel.Info, $"[分支输出] {Name}.{output.Name} = {value ?? "null"}", Id, Name);
            }
            return NodeResult.Ok;
        }

        private Variable CreateVariable(SwitchOutputDef output, object value)
        {
            if (output.Kind == VariableKind.Array)
            {
                return Variable.Array(Name, output.Name, output.Type, ToEnumerable(value));
            }
            if (output.Kind == VariableKind.Object)
            {
                return Variable.Object(Name, output.Name, value, value == null ? 0 : 1);
            }
            return Variable.Single(Name, output.Name, output.Type, value);
        }

        private static IEnumerable<object> ToEnumerable(object value)
        {
            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                foreach (object item in enumerable)
                {
                    yield return item;
                }
                yield break;
            }
            if (value != null)
            {
                yield return value;
            }
        }
    }
}
