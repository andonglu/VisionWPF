using System.Collections.Generic;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Nodes
{
    /// <summary>流程最终输出节点：把上游变量/常量汇总成统一结果。</summary>
    public sealed class FlowOutputNode : FlowNode
    {
        public List<FlowOutputDef> Outputs { get; } = new List<FlowOutputDef>();

        public FlowOutputNode(string name) : base(name)
        {
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            foreach (FlowOutputDef output in Outputs)
            {
                if (string.IsNullOrWhiteSpace(output.Name))
                {
                    return NodeResult.Fail($"{Name} 存在未命名输出");
                }
                if (output.Value == null)
                {
                    return NodeResult.Fail($"{Name}.{output.Name} 未配置取值");
                }

                object value = output.Value.GetValue(ctx);
                ctx.SetVariable(CreateVariable(output, value));
                ctx.AddLog(FlowLogLevel.Info, $"[流程输出] {Name}.{output.Name} = {value ?? "null"}", Id, Name);
            }
            return NodeResult.Ok;
        }

        private Variable CreateVariable(FlowOutputDef output, object value)
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

    public sealed class FlowOutputDef
    {
        public string Name { get; set; }
        public VariableKind Kind { get; set; }
        public VariableType Type { get; set; }
        public string ClrTypeName { get; set; }
        public Operand Value { get; set; }
    }
}
