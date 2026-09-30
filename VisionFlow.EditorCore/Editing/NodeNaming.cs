using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Nodes;

namespace VisionFlow.Editing
{
    /// <summary>
    /// 结构编辑的命名辅助：新工具节点加入流程后生成不重复的模块名。
    /// 从 WPF 主窗口平移，供编辑器与无头测试共用（VF-08）。
    /// </summary>
    public static class NodeNaming
    {
        /// <summary>新增 ToolNode 的模块名去重：去掉尾随数字后追加序号，并同步节点名。</summary>
        public static void EnsureUniqueNewNodeName(FlowNode root, FlowNode added)
        {
            if (!(added is ToolNode toolNode))
            {
                return;
            }

            string baseName = ModuleNameBase(toolNode.Tool.ModuleName);
            string unique = NextAvailableModuleName(root, baseName, added);
            toolNode.Tool.ModuleName = unique;
            toolNode.Name = unique;
        }

        public static string NextAvailableModuleName(FlowNode root, string baseName, FlowNode exclude)
        {
            HashSet<string> names = ExistingNodeNames(root, exclude);
            for (int i = 1; i < 100000; i++)
            {
                string candidate = baseName + i.ToString(CultureInfo.InvariantCulture);
                if (!names.Contains(candidate))
                {
                    return candidate;
                }
            }
            throw new InvalidOperationException("无法生成不重复的工具名称：" + baseName);
        }

        public static HashSet<string> ExistingNodeNames(FlowNode root, FlowNode exclude)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FlowNode node in EnumerateNodes(root))
            {
                if (ReferenceEquals(node, exclude))
                {
                    continue;
                }
                if (node is ToolNode toolNode && !string.IsNullOrWhiteSpace(toolNode.Tool.ModuleName))
                {
                    names.Add(toolNode.Tool.ModuleName);
                }
                if (!string.IsNullOrWhiteSpace(node.Name))
                {
                    names.Add(node.Name);
                }
            }
            return names;
        }

        public static IEnumerable<FlowNode> EnumerateNodes(FlowNode node)
        {
            if (node == null)
            {
                yield break;
            }
            yield return node;
            if (node is SequenceNode sequence)
            {
                foreach (FlowNode child in sequence.Children)
                {
                    foreach (FlowNode nested in EnumerateNodes(child))
                    {
                        yield return nested;
                    }
                }
            }
            else if (node is IfElseNode ifElse)
            {
                foreach (FlowNode child in ifElse.IfBranch.Concat(ifElse.ElseBranch))
                {
                    foreach (FlowNode nested in EnumerateNodes(child))
                    {
                        yield return nested;
                    }
                }
            }
            else if (node is ForLoopNode loop)
            {
                foreach (FlowNode child in loop.Body)
                {
                    foreach (FlowNode nested in EnumerateNodes(child))
                    {
                        yield return nested;
                    }
                }
            }
        }

        public static string ModuleNameBase(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return "工具";
            }
            int end = moduleName.Length;
            while (end > 0 && char.IsDigit(moduleName[end - 1]))
            {
                end--;
            }
            return end == 0 ? moduleName : moduleName.Substring(0, end);
        }
    }
}
