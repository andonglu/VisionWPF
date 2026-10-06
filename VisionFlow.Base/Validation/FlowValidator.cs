using System;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Expressions;
using VisionFlow.Nodes;
using VisionFlow.Variables;

namespace VisionFlow.Validation
{
    public enum FlowValidationSeverity
    {
        Error,
        Warning
    }

    public sealed class FlowValidationIssue
    {
        public FlowValidationSeverity Severity { get; private set; }
        public string NodeId { get; private set; }
        public string NodeName { get; private set; }
        public string Parameter { get; private set; }
        public string Message { get; private set; }

        public FlowValidationIssue(FlowValidationSeverity severity, FlowNode node, string parameter, string message)
        {
            Severity = severity;
            NodeId = node?.Id;
            NodeName = node?.Name;
            Parameter = parameter;
            Message = message;
        }

        public override string ToString()
        {
            string prefix = string.IsNullOrEmpty(NodeName) ? string.Empty : NodeName + " - ";
            string parameter = string.IsNullOrEmpty(Parameter) ? string.Empty : Parameter + "：";
            return $"{Severity}: {prefix}{parameter}{Message}";
        }
    }

    public sealed class FlowValidationResult
    {
        private readonly List<FlowValidationIssue> _issues = new List<FlowValidationIssue>();

        public IReadOnlyList<FlowValidationIssue> Issues
        {
            get { return _issues; }
        }

        public bool IsValid
        {
            get { return !_issues.Any(i => i.Severity == FlowValidationSeverity.Error); }
        }

        internal void Add(FlowValidationIssue issue)
        {
            _issues.Add(issue);
        }
    }

    /// <summary>声明级流程校验：不运行工具，只根据输出声明、引用路径和作用域判断流程是否可执行。</summary>
    public static class FlowValidator
    {
        public static FlowValidationResult Validate(FlowNode root)
        {
            var result = new FlowValidationResult();
            if (root == null)
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, null, null, "流程根节点为空"));
                return result;
            }

            ValidateDuplicateNamespaces(root, result);
            ValidateNode(root, root, result);
            return result;
        }

        private static void ValidateNode(FlowNode root, FlowNode node, FlowValidationResult result)
        {
            if (node is ToolNode toolNode)
            {
                ValidateTool(root, toolNode, result);
            }
            else if (node is IfElseNode ifElse)
            {
                ValidateIfElse(root, ifElse, result);
            }
            else if (node is ForLoopNode loop)
            {
                ValidateLoop(root, loop, result);
            }
            else if (node is FlowOutputNode outputNode)
            {
                ValidateFlowOutput(root, outputNode, result);
            }

            foreach (FlowNode child in EnumerateChildren(node))
            {
                ValidateNode(root, child, result);
            }
        }

        private static void ValidateTool(FlowNode root, ToolNode node, FlowValidationResult result)
        {
            foreach (ToolInputRefDef def in ToolMetadata.GetInputRefs(node.Tool.GetType()))
            {
                string path = def.Property.GetValue(node.Tool) as string;
                if (string.IsNullOrWhiteSpace(path))
                {
                    if (!def.Optional)
                    {
                        result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, def.DisplayName, "必填引用为空"));
                    }
                    continue;
                }

                ValidateReference(root, node, path.Trim(), def.ExpectedType, def.DisplayName, result, def.AcceptsCollection);
            }

            ValidateDynamicOutputs(node, result);
            ValidateExpressions(root, node, result);
        }

        /// <summary>
        /// 检查工具参数中的表达式：语法、局部名称，以及其中的变量引用（与 [InputRef] 使用同一作用域规则，
        /// 数组整体引用也允许，供 count / sum 等函数使用）。
        /// </summary>
        private static void ValidateExpressions(FlowNode root, ToolNode node, FlowValidationResult result)
        {
            if (!(node.Tool is IExpressionTool expressionTool))
            {
                return;
            }

            foreach (ToolExpressionDef def in expressionTool.GetExpressions() ?? new ToolExpressionDef[0])
            {
                if (def == null)
                {
                    continue;
                }
                if (!ExpressionParser.TryParse(def.Text, out CompiledExpression expression, out ExpressionException error))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, def.Parameter,
                        "表达式错误：" + error.Message));
                    continue;
                }

                foreach (string name in expression.LocalNames)
                {
                    if (!ContainsName(def.LocalNames, name))
                    {
                        result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, def.Parameter,
                            $"未定义的名称 {{{name}}}"));
                    }
                }

                foreach (string path in expression.References)
                {
                    VariableReference reference = VariableReference.Parse(path);
                    if (string.Equals(reference.ModuleName, node.Tool.ModuleName, StringComparison.OrdinalIgnoreCase)
                        && ContainsName(def.SelfOutputs, reference.VarName))
                    {
                        continue;
                    }
                    ValidateReference(root, node, path, typeof(object), def.Parameter, result, acceptsCollection: true);
                }
            }
        }

        private static bool ContainsName(IReadOnlyCollection<string> names, string name)
        {
            return names != null && names.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>动态输出名必须可被引用，且不能与该工具的其他输出重名（引用按不区分大小写匹配）。</summary>
        private static void ValidateDynamicOutputs(ToolNode node, FlowValidationResult result)
        {
            if (!(node.Tool is IDynamicOutputTool dynamicTool))
            {
                return;
            }

            var names = new HashSet<string>(ToolMetadata.GetOutputs(node.Tool.GetType()).Select(o => o.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (ToolOutputDef output in dynamicTool.GetDynamicOutputs() ?? new ToolOutputDef[0])
            {
                if (output == null)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(output.Name))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, "输出", "输出名不能为空"));
                    continue;
                }
                if (!ToolMetadata.IsValidOutputName(output.Name))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, output.Name,
                        "输出名不能包含空白或 . [ ] { } 字符"));
                    continue;
                }
                if (!names.Add(output.Name))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, output.Name,
                        "输出名与该工具的其他输出重复"));
                }
            }
        }

        private static void ValidateIfElse(FlowNode root, IfElseNode node, FlowValidationResult result)
        {
            if (node.Condition == null)
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, "条件", "未设置条件"));
            }
            else
            {
                ValidateOperand(root, node, node.Condition.Left, null, "左操作数", result);
                ValidateOperand(root, node, node.Condition.Right, null, "右操作数", result);
            }

            var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BranchOutputDef output in node.Outputs)
            {
                if (string.IsNullOrWhiteSpace(output.Name))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, "公共输出", "输出名不能为空"));
                    continue;
                }
                if (!outputNames.Add(output.Name))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, output.Name, "公共输出名重复"));
                }

                Type expected = ClrTypeOf(output);
                ValidateBranchOutputOperand(root, node, output, IfBranch.If, output.IfValue, expected, result);
                ValidateBranchOutputOperand(root, node, output, IfBranch.Else, output.ElseValue, expected, result);
            }
        }

        private static void ValidateLoop(FlowNode root, ForLoopNode node, FlowValidationResult result)
        {
            if (node.Mode == ForLoopMode.Count)
            {
                if (node.CountSource == null)
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, "次数", "未设置循环次数来源"));
                }
                else
                {
                    ValidateOperand(root, node, node.CountSource, typeof(int), "次数", result);
                }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(node.ItemsPath))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, "循环源", "未设置集合来源"));
                }
                else
                {
                    List<RefCandidate> collections = RefCandidateService.Collections(root, node);
                    if (!collections.Any(c => SamePath(c.Path, node.ItemsPath)))
                    {
                        result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, "循环源",
                            $"引用 '{node.ItemsPath}' 不是当前节点可用的上游集合输出"));
                    }
                }
            }
        }

        private static void ValidateFlowOutput(FlowNode root, FlowOutputNode node, FlowValidationResult result)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FlowOutputDef output in node.Outputs)
            {
                if (string.IsNullOrWhiteSpace(output.Name))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, "流程输出", "输出名不能为空"));
                    continue;
                }
                if (!names.Add(output.Name))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, output.Name, "输出名重复"));
                }
                if (output.Value == null)
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, output.Name, "未配置取值"));
                    continue;
                }
                ValidateOperand(root, node, output.Value, ClrTypeOf(output), output.Name, result);
            }
        }

        private static void ValidateBranchOutputOperand(FlowNode root, IfElseNode node, BranchOutputDef output,
            IfBranch branch, Operand operand, Type expected, FlowValidationResult result)
        {
            string branchName = branch == IfBranch.If ? "If" : "Else";
            if (operand == null)
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, output.Name,
                    $"{branchName} 分支取值未配置"));
                return;
            }
            if (operand.IsConstant)
            {
                return;
            }

            RefScope scope = RefCandidateService.ScopeForBranchOutput(root, node, branch);
            RefTypeCheckResult check = ReferenceSemantics.Check(operand.Reference, scope);
            if (!check.Success)
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, output.Name,
                    $"{branchName} 分支{check.Error}"));
                return;
            }
            if (!IsTypeCompatible(expected, check))
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, output.Name,
                    $"{branchName} 分支引用 '{operand.Reference}' 类型为 {check.ClrType.Name}，不能作为 {expected.Name} 输出"));
            }
        }

        private static void ValidateOperand(FlowNode root, FlowNode node, Operand operand, Type expectedType,
            string parameter, FlowValidationResult result)
        {
            if (operand == null)
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, parameter, "操作数为空"));
                return;
            }
            if (!operand.IsConstant)
            {
                ValidateReference(root, node, operand.Reference.ToString(), expectedType, parameter, result);
            }
        }

        private static void ValidateReference(FlowNode root, FlowNode node, string path, Type expectedType,
            string parameter, FlowValidationResult result, bool acceptsCollection = false)
        {
            VariableReference reference;
            try
            {
                reference = VariableReference.Parse(path);
            }
            catch (Exception ex)
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, parameter,
                    $"引用 '{path}' 格式错误：{ex.Message}"));
                return;
            }

            RefScope scope = RefCandidateService.ScopeForNode(root, node);
            RefTypeCheckResult check = ReferenceSemantics.Check(reference, scope);
            if (!check.Success)
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, parameter, check.Error));
                return;
            }
            if (!IsTypeCompatible(expectedType, check, acceptsCollection))
            {
                result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, parameter,
                    $"引用 '{path}' 类型为 {check.ClrType.Name}，不能赋给 {expectedType.Name}"));
            }
        }

        /// <summary>
        /// 期望类型与引用推导类型的兼容判断。
        /// 推导类型未知（object）时留待运行期；集合仅在输入元数据显式允许时按元素类型消费。
        /// </summary>
        private static bool IsTypeCompatible(Type expected, RefTypeCheckResult check, bool acceptsCollection = false)
        {
            if (expected == null || expected == typeof(object) || check.ClrType == typeof(object))
            {
                return true;
            }
            if (expected.IsAssignableFrom(check.ClrType))
            {
                return true;
            }
            return acceptsCollection && check.IsCollectionValue && check.ClrType.IsArray
                && expected.IsAssignableFrom(check.ClrType.GetElementType());
        }

        private static void ValidateDuplicateNamespaces(FlowNode root, FlowValidationResult result)
        {
            var seen = new Dictionary<string, FlowNode>(StringComparer.OrdinalIgnoreCase);
            foreach (FlowNode node in EnumerateTree(root))
            {
                string name = null;
                if (node is ToolNode toolNode)
                {
                    name = toolNode.Tool.ModuleName;
                }
                else if (node is IfElseNode ifElse && ifElse.Outputs.Count > 0)
                {
                    name = ifElse.Name;
                }
                else if (node is FlowOutputNode outputNode && outputNode.Outputs.Count > 0)
                {
                    name = outputNode.Name;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (seen.TryGetValue(name, out FlowNode existing))
                {
                    result.Add(new FlowValidationIssue(FlowValidationSeverity.Error, node, "名称",
                        $"输出命名空间 '{name}' 与节点 '{existing.Name}' 重复，会导致变量覆盖"));
                }
                else
                {
                    seen[name] = node;
                }
            }
        }

        private static IEnumerable<FlowNode> EnumerateTree(FlowNode node)
        {
            yield return node;
            foreach (FlowNode child in EnumerateChildren(node))
            {
                foreach (FlowNode nested in EnumerateTree(child))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<FlowNode> EnumerateChildren(FlowNode node)
        {
            if (node is SequenceNode sequence)
            {
                return sequence.Children;
            }
            if (node is IfElseNode ifElse)
            {
                return ifElse.IfBranch.Concat(ifElse.ElseBranch);
            }
            if (node is ForLoopNode loop)
            {
                return loop.Body;
            }
            if (node is FlowOutputNode)
            {
                return Enumerable.Empty<FlowNode>();
            }
            return Enumerable.Empty<FlowNode>();
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static Type ClrTypeOf(BranchOutputDef def)
        {
            return ClrTypeOf(def.Kind, def.Type, def.ClrTypeName);
        }

        private static Type ClrTypeOf(FlowOutputDef def)
        {
            return ClrTypeOf(def.Kind, def.Type, def.ClrTypeName);
        }

        private static Type ClrTypeOf(VariableKind kind, VariableType type, string clrTypeName)
        {
            Type valueType = typeof(object);
            if (kind != VariableKind.Object)
            {
                switch (type)
                {
                    case VariableType.Int: valueType = typeof(int); break;
                    case VariableType.Double: valueType = typeof(double); break;
                    case VariableType.String: valueType = typeof(string); break;
                    case VariableType.Bool: valueType = typeof(bool); break;
                }
            }
            if (valueType == typeof(object) && !string.IsNullOrWhiteSpace(clrTypeName))
            {
                valueType = Type.GetType(clrTypeName, throwOnError: false) ?? typeof(object);
            }
            return kind == VariableKind.Array ? valueType.MakeArrayType() : valueType;
        }
    }
}
