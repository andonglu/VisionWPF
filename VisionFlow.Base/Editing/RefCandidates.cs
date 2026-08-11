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
        /// <summary>值的 CLR 类型，用于按期望类型过滤。</summary>
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

    /// <summary>
    /// 引用候选服务：给定流程根与目标节点，按执行顺序枚举它"上游"的全部可引用输出，
    /// 并处理循环上下文（Loop.Index / Loop.Count / Loop.Current.成员）。
    /// 编辑器用它把"手输引用字符串"变成"下拉选择"。
    /// </summary>
    public static class RefCandidateService
    {
        /// <summary>目标节点可用的全部引用候选。</summary>
        public static List<RefCandidate> ForNode(FlowNode root, FlowNode target)
        {
            var candidates = new List<RefCandidate>();
            CollectUpstream(root, target, candidates);

            // 循环上下文：只为最内层循环生成 Loop.* 候选（框架语义即最内层）
            List<ForLoopNode> loopAncestors = FindLoopAncestors(root, target);
            ForLoopNode innermost = loopAncestors.LastOrDefault();
            if (innermost != null)
            {
                candidates.Add(new RefCandidate { Path = "Loop.Index", ClrType = typeof(int) });
                candidates.Add(new RefCandidate { Path = "Loop.Count", ClrType = typeof(int) });

                if (innermost.Mode == ForLoopMode.Each && !string.IsNullOrEmpty(innermost.ItemsPath))
                {
                    // 找到循环源声明，展开元素成员（如 Loop.Current.HomMat）
                    RefCandidate source = candidates.FirstOrDefault(c => c.Path == innermost.ItemsPath && c.IsCollection);
                    if (source != null)
                    {
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
            return candidates;
        }

        /// <summary>IfElse 公共输出配置可用的候选：外部上游 + 指定分支内部输出。</summary>
        public static List<RefCandidate> ForBranchOutput(FlowNode root, IfElseNode ifElse, IfBranch branch)
        {
            var candidates = new List<RefCandidate>();
            CollectUpstream(root, ifElse, candidates);
            IList<FlowNode> branchNodes = branch == IfBranch.If ? ifElse.IfBranch : ifElse.ElseBranch;
            foreach (FlowNode child in branchNodes)
            {
                CollectSubtreeOutputs(child, candidates);
            }
            return candidates;
        }

        /// <summary>按期望类型过滤（如 HTuple → 只有变换矩阵候选）。</summary>
        public static List<RefCandidate> ForInput(FlowNode root, FlowNode target, Type expectedType)
        {
            return ForNode(root, target)
                .Where(c => !c.IsCollection && expectedType.IsAssignableFrom(c.ClrType))
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

            if (node is IfElseNode ifElse && !ContainsNode(ifElse, target))
            {
                AddBranchOutputs(ifElse, candidates);
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
            }
            else if (node is IfElseNode ifElse)
            {
                AddBranchOutputs(ifElse, candidates);
            }
            else if (node is FlowOutputNode outputNode)
            {
                AddFlowOutputs(outputNode, candidates);
            }

            foreach (FlowNode child in EnumerateChildren(node))
            {
                CollectSubtreeOutputs(child, candidates);
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
            foreach (ToolOutputDef def in ToolMetadata.GetOutputs(toolNode.Tool.GetType()))
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
            foreach (FlowNode child in EnumerateChildren(node))
            {
                if (child == target || ContainsNode(child, target))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>找目标节点的循环祖先（外层→内层）。</summary>
        private static List<ForLoopNode> FindLoopAncestors(FlowNode root, FlowNode target)
        {
            var stack = new List<ForLoopNode>();
            var result = new List<ForLoopNode>();
            FindLoopAncestors(root, target, stack, result);
            return result;
        }

        private static bool FindLoopAncestors(FlowNode node, FlowNode target, List<ForLoopNode> stack, List<ForLoopNode> result)
        {
            if (node == target)
            {
                result.AddRange(stack);
                return true;
            }
            if (node is ForLoopNode loop)
            {
                stack.Add(loop);
            }
            foreach (FlowNode child in EnumerateChildren(node))
            {
                if (FindLoopAncestors(child, target, stack, result))
                {
                    return true;
                }
            }
            if (node is ForLoopNode)
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
            if (node is ForLoopNode loop)
            {
                return loop.Body;
            }
            return Enumerable.Empty<FlowNode>();
        }

        private static Type ClrTypeOf(ToolOutputDef def)
        {
            if (def.Kind == VariableKind.Single)
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
            if (def.Kind == VariableKind.Single)
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
            if (def.Kind == VariableKind.Single)
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
