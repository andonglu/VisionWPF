using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Variables;

namespace VisionFlow.Runtime
{
    /// <summary>子流程对外的一个输出（来自子流程顶层“流程输出”节点）。</summary>
    public sealed class SubFlowOutputDef
    {
        /// <summary>子流程中的流程输出节点名（运行后在子上下文中的模块名）。</summary>
        public string OutputNodeName { get; set; }
        public string Name { get; set; }
        public VariableKind Kind { get; set; }
        public VariableType Type { get; set; }
        public string ClrTypeName { get; set; }
    }

    /// <summary>已加载的子流程文件。</summary>
    public sealed class SubFlowDefinition
    {
        public string FullPath { get; set; }
        public SequenceNode Root { get; set; }
        public IReadOnlyList<SubFlowOutputDef> Outputs { get; set; }
        public IReadOnlyList<string> Warnings { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    /// <summary>
    /// 子流程文件缓存：按“完整路径 + 修改时间”缓存加载结果，文件被替换后自动重新加载并释放旧流程的工具资源。
    /// 同一子流程文件的多次调用共用一份流程定义（与普通流程中工具实例的复用方式一致）。
    /// </summary>
    public static class SubFlowLibrary
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, SubFlowDefinition> Cache =
            new Dictionary<string, SubFlowDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>加载（或取缓存的）子流程；文件不存在、格式错误时抛出异常，异常信息为中文说明。</summary>
        public static SubFlowDefinition Load(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                throw new InvalidOperationException("未选择子流程文件");
            }
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"子流程文件不存在：{fullPath}", fullPath);
            }

            DateTime writeTime = File.GetLastWriteTimeUtc(fullPath);
            SubFlowDefinition previous;
            lock (Sync)
            {
                if (Cache.TryGetValue(fullPath, out previous) && previous.LastWriteTimeUtc == writeTime)
                {
                    return previous;
                }
            }

            var warnings = new List<string>();
            SequenceNode root = FlowSerializer.LoadFile(fullPath, warnings);
            var definition = new SubFlowDefinition
            {
                FullPath = fullPath,
                Root = root,
                Outputs = root.Children.OfType<FlowOutputNode>()
                    .SelectMany(node => node.Outputs.Select(output => new SubFlowOutputDef
                    {
                        OutputNodeName = node.Name,
                        Name = output.Name,
                        Kind = output.Kind,
                        Type = output.Type,
                        ClrTypeName = output.ClrTypeName
                    }))
                    .ToList(),
                Warnings = warnings,
                LastWriteTimeUtc = writeTime
            };

            lock (Sync)
            {
                Cache[fullPath] = definition;
            }
            if (previous != null)
            {
                FlowResources.Release(previous.Root);
            }
            return definition;
        }

        /// <summary>尝试加载，失败时返回 null 并给出原因（供引用候选、校验等不应抛出异常的场合）。</summary>
        public static SubFlowDefinition TryLoad(string fullPath, out string error)
        {
            try
            {
                error = null;
                return Load(fullPath);
            }
            catch (Exception ex) when (IsLoadException(ex))
            {
                error = ex.Message;
                return null;
            }
        }

        public static bool IsLoadException(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException
                || ex is NotSupportedException || ex is System.Text.Json.JsonException || ex is ArgumentException;
        }

        /// <summary>
        /// 从 fullPath 出发检查子流程间的循环引用，发现时返回引用链（如 A → B → A），否则返回 null。
        /// 无法加载的文件不参与检查（由各自的校验报告）。
        /// </summary>
        public static string FindCycle(string fullPath)
        {
            return FindCycle(fullPath, new List<string>());
        }

        private static string FindCycle(string fullPath, List<string> chain)
        {
            if (chain.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                return string.Join(" → ", chain.Append(fullPath).Select(Path.GetFileName));
            }
            SubFlowDefinition definition = TryLoad(fullPath, out _);
            if (definition == null)
            {
                return null;
            }
            chain.Add(fullPath);
            foreach (SubFlowNode node in EnumerateNodes(definition.Root).OfType<SubFlowNode>())
            {
                string childPath = node.ResolvePath();
                if (childPath == null)
                {
                    continue;
                }
                string cycle = FindCycle(childPath, chain);
                if (cycle != null)
                {
                    return cycle;
                }
            }
            chain.RemoveAt(chain.Count - 1);
            return null;
        }

        /// <summary>遍历流程树中的全部节点（不进入子流程文件）。</summary>
        internal static IEnumerable<Core.FlowNode> EnumerateNodes(Core.FlowNode node)
        {
            yield return node;
            IEnumerable<Core.FlowNode> children;
            switch (node)
            {
                case SequenceNode sequence: children = sequence.Children; break;
                case IfElseNode ifElse: children = ifElse.IfBranch.Concat(ifElse.ElseBranch); break;
                case LoopNodeBase loop: children = loop.Body; break;
                case SwitchNode switchNode: children = switchNode.Cases; break;
                case SwitchCaseNode switchCase: children = switchCase.Children; break;
                default: children = Enumerable.Empty<Core.FlowNode>(); break;
            }
            foreach (Core.FlowNode child in children)
            {
                foreach (Core.FlowNode nested in EnumerateNodes(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>清空缓存并释放已加载子流程的工具资源（主要用于测试）。</summary>
        public static void Clear()
        {
            List<SubFlowDefinition> definitions;
            lock (Sync)
            {
                definitions = Cache.Values.ToList();
                Cache.Clear();
            }
            foreach (SubFlowDefinition definition in definitions)
            {
                FlowResources.Release(definition.Root);
            }
        }
    }
}
