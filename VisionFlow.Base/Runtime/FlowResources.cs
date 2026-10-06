using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Nodes;

namespace VisionFlow.Runtime
{
    /// <summary>单个节点的预热失败信息。</summary>
    public sealed class FlowPrepareIssue
    {
        public string NodeId { get; set; }
        public string NodeName { get; set; }
        public string Message { get; set; }

        public override string ToString()
        {
            return $"{NodeName}：{Message}";
        }
    }

    /// <summary>流程预热结果。预热失败不会中断其他节点的预热。</summary>
    public sealed class FlowPrepareResult
    {
        public IReadOnlyList<FlowPrepareIssue> Issues { get; set; } = new List<FlowPrepareIssue>();
        /// <summary>参与预热（实现了 <see cref="IToolResourceLifecycle"/>）的工具数量。</summary>
        public int PreparedCount { get; set; }
        public TimeSpan Duration { get; set; }

        public bool IsSuccess
        {
            get { return Issues.Count == 0; }
        }
    }

    /// <summary>
    /// 流程资源生命周期辅助：对整棵流程树（含分支与循环体）中的工具执行预热或释放。
    /// 调用时机：
    /// - 加载流程 / 切换配方后、投入生产前：<see cref="Prepare"/>，检查结果，失败则不投产；
    /// - 切换配方：先预热新流程，替换成功后再释放旧流程（旧流程不得仍在运行）；
    /// - 删除节点、工具参数被整体替换、关闭流程或程序退出：<see cref="Release"/>。
    /// </summary>
    public static class FlowResources
    {
        public static FlowPrepareResult Prepare(FlowNode root)
        {
            return Prepare(EnumerateToolNodes(root).ToList());
        }

        /// <summary>
        /// 预热给定的工具节点。后台预热时应先在拥有流程树的线程上取快照（<see cref="EnumerateToolNodes"/>.ToList()），
        /// 避免遍历期间流程结构被编辑。
        /// </summary>
        public static FlowPrepareResult Prepare(IEnumerable<ToolNode> toolNodes)
        {
            var watch = Stopwatch.StartNew();
            var issues = new List<FlowPrepareIssue>();
            int prepared = 0;
            foreach (ToolNode node in toolNodes)
            {
                if (!(node.Tool is IToolResourceLifecycle lifecycle))
                {
                    continue;
                }
                prepared++;
                try
                {
                    lifecycle.Prepare();
                }
                catch (Exception ex)
                {
                    issues.Add(new FlowPrepareIssue
                    {
                        NodeId = node.Id,
                        NodeName = node.Name,
                        Message = ex.GetBaseException().Message
                    });
                }
            }
            watch.Stop();
            return new FlowPrepareResult { Issues = issues, PreparedCount = prepared, Duration = watch.Elapsed };
        }

        /// <summary>释放流程树中全部工具的缓存资源。单个工具释放失败不影响其他工具。</summary>
        public static void Release(FlowNode root)
        {
            foreach (ToolNode node in EnumerateToolNodes(root))
            {
                Release(node.Tool);
            }
        }

        /// <summary>释放单个工具的缓存资源（工具未实现生命周期接口时忽略）。</summary>
        public static void Release(ToolBase tool)
        {
            if (tool is IToolResourceLifecycle lifecycle)
            {
                try
                {
                    lifecycle.ReleaseResources();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"释放工具 {tool.ModuleName} 资源失败：{ex.GetBaseException().Message}");
                }
            }
        }

        /// <summary>枚举流程树中的全部工具节点（顺序、分支两侧、循环体）。</summary>
        public static IEnumerable<ToolNode> EnumerateToolNodes(FlowNode root)
        {
            if (root == null)
            {
                yield break;
            }
            if (root is ToolNode toolNode)
            {
                yield return toolNode;
                yield break;
            }
            foreach (FlowNode child in Children(root))
            {
                foreach (ToolNode nested in EnumerateToolNodes(child))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<FlowNode> Children(FlowNode node)
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
            if (node is SwitchNode switchNode)
            {
                return switchNode.Cases;
            }
            if (node is SwitchCaseNode switchCase)
            {
                return switchCase.Children;
            }
            return Enumerable.Empty<FlowNode>();
        }
    }
}
