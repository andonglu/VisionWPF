using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Runtime;
using VisionFlow.Variables;

namespace VisionFlow.Nodes
{
    /// <summary>子流程的一个输入映射：子流程中的外部输入（如 Input.Image、Input.Threshold）← 当前流程中的引用或常量。</summary>
    public sealed class SubFlowInputMapping
    {
        /// <summary>子流程中的输入路径，形如 Input.名称。</summary>
        public string InputPath { get; set; }

        public Operand Value { get; set; }
    }

    /// <summary>
    /// 子流程：调用另一个 .vflow.json 文件。
    /// - 每次调用使用独立的子上下文，子流程内部变量不会出现在当前流程中；
    /// - 子流程默认沿用当前流程的 Input.* 外部输入（借用，不会被子流程释放），输入映射可以追加或覆盖；
    /// - 子流程顶层“流程输出”节点的输出，以本节点名为模块名回到当前流程（如 子流程1.Ok），其中的 HALCON 资源所有权移交给当前流程；
    /// - 子流程的日志加上“[子流程 名称]”前缀并入当前流程；单步调试不进入子流程内部。
    /// </summary>
    public sealed class SubFlowNode : FlowNode
    {
        /// <summary>允许的最大嵌套深度，超过时视为循环引用。</summary>
        public const int MaxDepth = 8;

        /// <summary>子流程文件路径；相对路径相对于 <see cref="BaseDirectory"/>（所在流程文件的目录）。</summary>
        public string FlowFile { get; set; } = string.Empty;

        /// <summary>
        /// 解析相对路径的基准目录，由 FlowSerializer.LoadFile 或编辑器按所在流程文件设置，不保存到流程文件；
        /// 未设置时相对于程序目录。
        /// </summary>
        public string BaseDirectory { get; set; }

        public List<SubFlowInputMapping> Inputs { get; } = new List<SubFlowInputMapping>();

        public SubFlowNode(string name, string flowFile = "") : base(name)
        {
            FlowFile = flowFile ?? string.Empty;
        }

        /// <summary>子流程文件的完整路径；未选择文件时为 null。</summary>
        public string ResolvePath()
        {
            if (string.IsNullOrWhiteSpace(FlowFile))
            {
                return null;
            }
            string path = FlowFile.Trim();
            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(BaseDirectory ?? AppContext.BaseDirectory, path));
        }

        /// <summary>加载子流程定义（带缓存），失败时返回 null 并给出原因。</summary>
        public SubFlowDefinition TryLoadDefinition(out string error)
        {
            string path = ResolvePath();
            if (path == null)
            {
                error = "未选择子流程文件";
                return null;
            }
            return SubFlowLibrary.TryLoad(path, out error);
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            if (ctx.SubFlowDepth >= MaxDepth)
            {
                return NodeResult.Fail($"{Name} 子流程嵌套超过 {MaxDepth} 层，可能存在循环引用");
            }
            SubFlowDefinition definition = TryLoadDefinition(out string loadError);
            if (definition == null)
            {
                return NodeResult.Fail($"{Name} 子流程加载失败：{loadError}");
            }

            using (var child = new FlowContext { CancellationToken = ctx.CancellationToken, SubFlowDepth = ctx.SubFlowDepth + 1 })
            {
                foreach (Variable input in ctx.GetAllVariables().Where(v => string.Equals(v.ModuleName, "Input", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    child.SetBorrowedVariable(input);
                }
                foreach (SubFlowInputMapping mapping in Inputs)
                {
                    VariableReference target = VariableReference.Parse(mapping.InputPath);
                    child.SetBorrowedVariable(CreateInput(ctx, mapping.Value, target.ModuleName, target.VarName));
                }

                NodeResult result = definition.Root.Execute(child);
                foreach (FlowLogEntry entry in child.StructuredLogs)
                {
                    ctx.AddLog(entry.Level, $"[子流程 {Name}] {entry.Message}", entry.NodeId, entry.NodeName, entry.ErrorCode, entry.Duration);
                }
                if (!result.IsSuccess)
                {
                    return NodeResult.Fail($"{Name} 子流程失败：{result.Message}", result.ErrorCode, result.Severity);
                }

                foreach (SubFlowOutputDef output in definition.Outputs)
                {
                    if (!child.TryGetVariable(output.OutputNodeName, output.Name, out Variable value))
                    {
                        return NodeResult.Fail($"{Name} 子流程没有产生输出 {output.OutputNodeName}.{output.Name}");
                    }
                    // 输出中的 HALCON 资源从子上下文移交给当前上下文，子上下文释放时不再处置
                    child.ReleaseOwnership(value.Value);
                    ctx.SetOwnedVariable(value.WithName(Name, output.Name));
                }
            }
            ctx.AddLog(FlowLogLevel.Info, $"[子流程] {Name}：{Path.GetFileName(definition.FullPath)} 完成，输出 {definition.Outputs.Count} 项", Id, Name);
            return NodeResult.Ok;
        }

        /// <summary>按映射取值构造子流程输入：整变量引用沿用原变量的形态与类型，其他按值推断。</summary>
        private static Variable CreateInput(FlowContext ctx, Operand operand, string module, string name)
        {
            if (operand != null && !operand.IsConstant && operand.Reference.ModuleName != "Loop"
                && !operand.Reference.ElementIndex.HasValue && operand.Reference.MemberPath.Length == 0)
            {
                return ctx.GetVariable(operand.Reference.ModuleName, operand.Reference.VarName).WithName(module, name);
            }

            object value = operand?.GetValue(ctx);
            switch (value)
            {
                case bool b: return Variable.Single(module, name, VariableType.Bool, b);
                case int i: return Variable.Single(module, name, VariableType.Int, i);
                case double d: return Variable.Single(module, name, VariableType.Double, d);
                case string s: return Variable.Single(module, name, VariableType.String, s);
                case System.Collections.IEnumerable items:
                    return Variable.Array(module, name, VariableType.Object, items.Cast<object>());
                default:
                    return Variable.Object(module, name, value, value == null ? 0 : 1);
            }
        }
    }
}
