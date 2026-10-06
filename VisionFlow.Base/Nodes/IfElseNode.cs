using System.Collections.Generic;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Nodes
{
    /// <summary>IfElse 对外暴露的公共输出：If / Else 各自映射到同一个输出名。</summary>
    public sealed class BranchOutputDef
    {
        public string Name { get; set; }
        public VariableKind Kind { get; set; }
        public VariableType Type { get; set; }
        public string ClrTypeName { get; set; }
        public Operand IfValue { get; set; }
        public Operand ElseValue { get; set; }
    }

    /// <summary>
    /// IfElse 分支节点：条件成立执行 IfBranch，否则执行 ElseBranch。
    /// 分支是子节点列表，可以包含任意节点（包括 IfElse / For），因此支持嵌套。
    /// </summary>
    public sealed class IfElseNode : FlowNode
    {
        public ComparisonCondition Condition { get; set; }

        public List<FlowNode> IfBranch { get; } = new List<FlowNode>();
        public List<FlowNode> ElseBranch { get; } = new List<FlowNode>();
        public List<BranchOutputDef> Outputs { get; } = new List<BranchOutputDef>();

        public override bool IsComposite
        {
            get { return true; }
        }

        public IfElseNode(string name, ComparisonCondition condition = null) : base(name)
        {
            Condition = condition;
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            if (Condition == null)
            {
                return NodeResult.Fail($"{Name} 未设置条件");
            }

            bool pass = Condition.Evaluate(ctx);
            ctx.AddLog(FlowLogLevel.Info,
                $"[条件] {Name}：{Condition} → {(pass ? "成立，走 If 分支" : "不成立，走 Else 分支")}",
                Id, Name);

            NodeResult result = RunChildren(pass ? IfBranch : ElseBranch, ctx);
            if (!result.IsSuccess)
            {
                return result;
            }

            foreach (BranchOutputDef output in Outputs)
            {
                Operand operand = pass ? output.IfValue : output.ElseValue;
                if (operand == null)
                {
                    return NodeResult.Fail($"{Name}.{output.Name} 未配置{(pass ? "If" : "Else")}分支取值");
                }
                object value = operand.GetValue(ctx);
                ctx.SetVariable(CreateVariable(output, value));
                ctx.AddLog(FlowLogLevel.Info, $"[分支输出] {Name}.{output.Name} = {value ?? "null"}", Id, Name);
            }
            return NodeResult.Ok;
        }

        private Variable CreateVariable(BranchOutputDef output, object value)
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
