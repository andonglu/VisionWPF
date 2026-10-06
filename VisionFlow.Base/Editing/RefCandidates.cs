using System;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>一个可选的引用候选项（如 "模板匹配1.Image"、"Loop.Current.HomMat"）。</summary>
    public sealed class RefCandidate
    {
        public string Path { get; set; }
        /// <summary>值的 CLR 类型，用于按期望类型过滤。集合输出时为元素类型。</summary>
        public Type ClrType { get; set; }
        /// <summary>是否为集合输出（数组变量）。</summary>
        public bool IsCollection { get; set; }
        /// <summary>集合元素上可用于成员链的成员名（内部使用，展开 Loop.Current.成员）。</summary>
        public string[] Members { get; set; } = new string[0];

        public override string ToString()
        {
            return Path;
        }
    }

    /// <summary>目标节点的循环上下文。</summary>
    public enum RefLoopMode
    {
        /// <summary>不在任何循环内。</summary>
        None,
        /// <summary>最内层是按次数循环：可用 Loop.Index / Loop.Count，没有 Loop.Current。</summary>
        Count,
        /// <summary>最内层是按集合循环：额外可用 Loop.Current 及其成员。</summary>
        Each,
        /// <summary>最内层是条件循环：只有 Loop.Index（总次数事先未知）。</summary>
        While
    }

    /// <summary>
    /// 引用作用域：目标节点可见的候选输出 + 循环上下文。
    /// 声明级校验与编辑器下拉共用同一份作用域，保证两处语义一致。
    /// </summary>
    public sealed class RefScope
    {
        public List<RefCandidate> Candidates { get; private set; }
        public RefLoopMode LoopMode { get; private set; }
        /// <summary>Each 模式循环源的元素类型；无法静态确定时为 null（成员链留待运行期验证）。</summary>
        public Type LoopCurrentElementType { get; private set; }

        public RefScope(List<RefCandidate> candidates, RefLoopMode loopMode, Type loopCurrentElementType)
        {
            Candidates = candidates ?? new List<RefCandidate>();
            LoopMode = loopMode;
            LoopCurrentElementType = loopCurrentElementType;
        }
    }

    /// <summary>
    /// 引用候选服务：给定流程根与目标节点，按执行顺序枚举它"上游"的全部可引用输出，
    /// 并处理循环上下文（Loop.Index / Loop.Count / Loop.Current.成员）。
    /// 编辑器用它把"手输引用字符串"变成"下拉选择"，校验器用它做作用域判断。
    ///
    /// 作用域规则（与运行时一致）：
    /// - If / Else 分支内部工具的输出是分支私有的：对互斥分支和分支外均不可见；
    /// - IfElse 对外只暴露显式配置的公共输出（嵌套 IfElse 同样适用）；
    /// - 循环体内部输出对循环外不可见（零次执行时这些变量不存在）；
    /// - Input.Image 等外部输入对所有节点可见。
    /// </summary>
    public static class RefCandidateService
    {
        /// <summary>目标节点可用的全部引用候选。</summary>
        public static List<RefCandidate> ForNode(FlowNode root, FlowNode target)
        {
            return ScopeForNode(root, target).Candidates;
        }

        /// <summary>目标节点的完整引用作用域（候选 + 循环上下文）。</summary>
        public static RefScope ScopeForNode(FlowNode root, FlowNode target)
        {
            var candidates = new List<RefCandidate>();
            CollectUpstream(root, target, candidates);
            AddExternalInputs(candidates);

            // 循环上下文：只为最内层循环生成 Loop.* 候选（框架语义即最内层）
            RefLoopMode loopMode = RefLoopMode.None;
            Type loopCurrentElementType = null;
            List<LoopNodeBase> loopAncestors = FindLoopAncestors(root, target);
            LoopNodeBase innermostLoop = loopAncestors.LastOrDefault();
            if (innermostLoop is WhileLoopNode)
            {
                loopMode = RefLoopMode.While;
                candidates.Add(new RefCandidate { Path = "Loop.Index", ClrType = typeof(int) });
            }
            else if (innermostLoop is ForLoopNode innermost)
            {
                loopMode = innermost.Mode == ForLoopMode.Each ? RefLoopMode.Each : RefLoopMode.Count;
                candidates.Add(new RefCandidate { Path = "Loop.Index", ClrType = typeof(int) });
                candidates.Add(new RefCandidate { Path = "Loop.Count", ClrType = typeof(int) });

                if (innermost.Mode == ForLoopMode.Each && !string.IsNullOrEmpty(innermost.ItemsPath))
                {
                    // 找到循环源声明，展开元素成员（如 Loop.Current.HomMat）
                    RefCandidate source = candidates.FirstOrDefault(c => SamePath(c.Path, innermost.ItemsPath) && c.IsCollection);
                    if (source != null)
                    {
                        loopCurrentElementType = source.ClrType;
                        candidates.Add(new RefCandidate { Path = "Loop.Current", ClrType = source.ClrType });
                        foreach (string member in source.Members)
                        {
                            Type memberType = source.ClrType.GetProperty(member)?.PropertyType ?? typeof(object);
                            candidates.Add(new RefCandidate
                            {
                                Path = "Loop.Current." + member,
                                ClrType = memberType
                            });
                        }
                    }
                }
            }
            return new RefScope(candidates, loopMode, loopCurrentElementType);
        }

        /// <summary>
        /// While 循环条件的作用域：条件在本循环的循环帧内求值，可用 Loop.Index（指向本循环）；
        /// 先执行后判断时，循环体的输出（与循环体末尾可见的相同）也可引用。
        /// </summary>
        public static RefScope ScopeForWhileCondition(FlowNode root, WhileLoopNode loop)
        {
            List<RefCandidate> candidates = ScopeForNode(root, loop).Candidates
                .Where(c => !c.Path.StartsWith("Loop.", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (loop.TestAfterBody)
            {
                foreach (FlowNode child in loop.Body)
                {
                    CollectSubtreeOutputs(child, candidates);
                }
            }
            candidates.Add(new RefCandidate { Path = "Loop.Index", ClrType = typeof(int) });
            return new RefScope(candidates, RefLoopMode.While, null);
        }

        /// <summary>IfElse 公共输出配置可用的候选：外部上游 + 指定分支内部输出。</summary>
        public static List<RefCandidate> ForBranchOutput(FlowNode root, IfElseNode ifElse, IfBranch branch)
        {
            return ScopeForBranchOutput(root, ifElse, branch).Candidates;
        }

        /// <summary>IfElse 公共输出配置的完整作用域，保留分支所在的最内层循环上下文。</summary>
        public static RefScope ScopeForBranchOutput(FlowNode root, IfElseNode ifElse, IfBranch branch)
        {
            RefScope scope = ScopeForNode(root, ifElse);
            List<RefCandidate> candidates = scope.Candidates;
            IList<FlowNode> branchNodes = branch == IfBranch.If ? ifElse.IfBranch : ifElse.ElseBranch;
            foreach (FlowNode child in branchNodes)
            {
                CollectSubtreeOutputs(child, candidates);
            }
            return new RefScope(candidates, scope.LoopMode, scope.LoopCurrentElementType);
        }

        /// <summary>按期望类型过滤（如 HTuple → 只有变换矩阵候选）。</summary>
        public static List<RefCandidate> ForInput(FlowNode root, FlowNode target, Type expectedType)
        {
            return ForInput(root, target, expectedType, false);
        }

        public static List<RefCandidate> ForInput(FlowNode root, FlowNode target, Type expectedType,
            bool acceptsCollection)
        {
            return ForNode(root, target)
                .Where(c =>
                {
                    Type valueType = c.IsCollection ? c.ClrType.MakeArrayType() : c.ClrType;
                    return expectedType.IsAssignableFrom(valueType)
                        || (acceptsCollection && valueType.IsArray
                            && expectedType.IsAssignableFrom(valueType.GetElementType()));
                })
                .ToList();
        }

        /// <summary>全部集合输出候选（ForEach 循环源选择用）。</summary>
        public static List<RefCandidate> Collections(FlowNode root, FlowNode target)
        {
            return ForNode(root, target).Where(c => c.IsCollection).ToList();
        }

        // ---------------- 内部实现 ----------------

        /// <summary>按执行顺序 DFS，收集目标节点之前的工具输出；遇到目标即停。</summary>
        private static bool CollectUpstream(FlowNode node, FlowNode target, List<RefCandidate> candidates)
        {
            if (node == target)
            {
                return true;
            }

            if (node is ToolNode toolNode)
            {
                AddToolOutputs(toolNode, candidates);
                return false;
            }

            if (node is FlowOutputNode outputNode)
            {
                AddFlowOutputs(outputNode, candidates);
                return false;
            }

            if (node is IfElseNode ifElse)
            {
                if (!ContainsNode(ifElse, target))
                {
                    // 分支外只暴露显式公共输出
                    AddBranchOutputs(ifElse, candidates);
                    return false;
                }
                // 目标在分支内：只收集所在分支的上游，互斥分支的私有输出不可见
                IList<FlowNode> branch = ContainsNodeIn(ifElse.IfBranch, target) ? ifElse.IfBranch : ifElse.ElseBranch;
                foreach (FlowNode child in branch)
                {
                    if (CollectUpstream(child, target, candidates))
                    {
                        return true;
                    }
                }
                return false;
            }

            if (node is LoopNodeBase loop)
            {
                if (!ContainsNode(loop, target))
                {
                    // 循环体内部输出对循环外不可见：零次执行时这些变量不存在
                    return false;
                }
                foreach (FlowNode child in loop.Body)
                {
                    if (CollectUpstream(child, target, candidates))
                    {
                        return true;
                    }
                }
                return false;
            }

            foreach (FlowNode child in EnumerateChildren(node))
            {
                if (CollectUpstream(child, target, candidates))
                {
                    return true;
                }
            }
            return false;
        }

        private static void CollectSubtreeOutputs(FlowNode node, List<RefCandidate> candidates)
        {
            if (node is ToolNode toolNode)
            {
                AddToolOutputs(toolNode, candidates);
                return;
            }
            if (node is FlowOutputNode outputNode)
            {
                AddFlowOutputs(outputNode, candidates);
                return;
            }
            // 嵌套 IfElse 只暴露显式公共输出，不递归分支内部
            if (node is IfElseNode ifElse)
            {
                AddBranchOutputs(ifElse, candidates);
                return;
            }
            // 循环体内部输出对外不可见，不递归循环体
            if (node is LoopNodeBase)
            {
                return;
            }
            foreach (FlowNode child in EnumerateChildren(node))
            {
                CollectSubtreeOutputs(child, candidates);
            }
        }

        /// <summary>外部输入对所有节点可见（来源：ExternalInputRegistry，含默认的 Input.Image）。</summary>
        private static void AddExternalInputs(List<RefCandidate> candidates)
        {
            int insertAt = 0;
            foreach (ExternalInputDef input in ExternalInputRegistry.Items)
            {
                if (candidates.Any(c => SamePath(c.Path, input.Path)))
                {
                    continue;
                }
                candidates.Insert(insertAt++, new RefCandidate
                {
                    Path = input.Path,
                    ClrType = input.ClrType.IsArray ? input.ClrType.GetElementType() : input.ClrType,
                    IsCollection = input.ClrType.IsArray
                });
            }
        }

        private static void AddFlowOutputs(FlowOutputNode outputNode, List<RefCandidate> candidates)
        {
            foreach (FlowOutputDef output in outputNode.Outputs)
            {
                candidates.Add(new RefCandidate
                {
                    Path = outputNode.Name + "." + output.Name,
                    ClrType = ClrTypeOf(output),
                    IsCollection = output.Kind == VariableKind.Array
                });
            }
        }

        private static void AddToolOutputs(ToolNode toolNode, List<RefCandidate> candidates)
        {
            foreach (ToolOutputDef def in ToolMetadata.GetOutputs(toolNode.Tool))
            {
                candidates.Add(new RefCandidate
                {
                    Path = toolNode.Tool.ModuleName + "." + def.Name,
                    ClrType = ClrTypeOf(def),
                    IsCollection = def.Kind == VariableKind.Array,
                    Members = def.Members
                });
            }
        }

        private static void AddBranchOutputs(IfElseNode ifElse, List<RefCandidate> candidates)
        {
            foreach (BranchOutputDef output in ifElse.Outputs)
            {
                candidates.Add(new RefCandidate
                {
                    Path = ifElse.Name + "." + output.Name,
                    ClrType = ClrTypeOf(output),
                    IsCollection = output.Kind == VariableKind.Array
                });
            }
        }

        private static bool ContainsNode(FlowNode node, FlowNode target)
        {
            return ContainsNodeIn(EnumerateChildren(node), target);
        }

        private static bool ContainsNodeIn(IEnumerable<FlowNode> nodes, FlowNode target)
        {
            foreach (FlowNode child in nodes)
            {
                if (child == target || ContainsNode(child, target))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>找目标节点的循环祖先（外层→内层）。</summary>
        private static List<LoopNodeBase> FindLoopAncestors(FlowNode root, FlowNode target)
        {
            var stack = new List<LoopNodeBase>();
            var result = new List<LoopNodeBase>();
            FindLoopAncestors(root, target, stack, result);
            return result;
        }

        private static bool FindLoopAncestors(FlowNode node, FlowNode target, List<LoopNodeBase> stack, List<LoopNodeBase> path)
        {
            if (node == target)
            {
                path.AddRange(stack);
                return true;
            }
            if (node is LoopNodeBase loop)
            {
                stack.Add(loop);
            }
            foreach (FlowNode child in EnumerateChildren(node))
            {
                if (FindLoopAncestors(child, target, stack, path))
                {
                    return true;
                }
            }
            if (node is LoopNodeBase)
            {
                stack.RemoveAt(stack.Count - 1);
            }
            return false;
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
            if (node is LoopNodeBase loop)
            {
                return loop.Body;
            }
            return Enumerable.Empty<FlowNode>();
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static Type ClrTypeOf(ToolOutputDef def)
        {
            if (def.Kind != VariableKind.Object)
            {
                switch (def.Type)
                {
                    case VariableType.Int: return typeof(int);
                    case VariableType.Double: return typeof(double);
                    case VariableType.String: return typeof(string);
                    case VariableType.Bool: return typeof(bool);
                    default: return def.ElementClrType ?? typeof(object);
                }
            }
            return def.ElementClrType ?? typeof(object);
        }

        private static Type ClrTypeOf(BranchOutputDef def)
        {
            if (def.Kind != VariableKind.Object)
            {
                switch (def.Type)
                {
                    case VariableType.Int: return typeof(int);
                    case VariableType.Double: return typeof(double);
                    case VariableType.String: return typeof(string);
                    case VariableType.Bool: return typeof(bool);
                }
            }
            if (!string.IsNullOrWhiteSpace(def.ClrTypeName))
            {
                return Type.GetType(def.ClrTypeName, throwOnError: false) ?? typeof(object);
            }
            return typeof(object);
        }

        private static Type ClrTypeOf(FlowOutputDef def)
        {
            if (def.Kind != VariableKind.Object)
            {
                switch (def.Type)
                {
                    case VariableType.Int: return typeof(int);
                    case VariableType.Double: return typeof(double);
                    case VariableType.String: return typeof(string);
                    case VariableType.Bool: return typeof(bool);
                }
            }
            if (!string.IsNullOrWhiteSpace(def.ClrTypeName))
            {
                return Type.GetType(def.ClrTypeName, throwOnError: false) ?? typeof(object);
            }
            return typeof(object);
        }
    }
}
