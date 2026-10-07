using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Ui;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>子流程编辑窗口中的一行输入映射；取值为操作数文本（ref:模块.变量 或常量）。</summary>
    public sealed class SubFlowInputRow
    {
        public string InputPath { get; set; } = "Input.";

        public string ValueText { get; set; } = string.Empty;
    }

    /// <summary>
    /// 子流程节点的编辑草稿：文件路径与输入映射先在草稿中修改，确定后 <see cref="Apply"/> 一次性写回节点，取消则节点不变。
    /// 校验与流程校验使用同一规则（<see cref="FlowValidator"/>），试运行使用草稿的副本节点。
    /// </summary>
    public sealed class SubFlowEditor
    {
        private readonly SubFlowNode _node;

        private SubFlowEditor(SubFlowNode node)
        {
            _node = node;
            FlowFile = node.FlowFile ?? string.Empty;
            Inputs = node.Inputs
                .Select(m => new SubFlowInputRow { InputPath = m.InputPath ?? string.Empty, ValueText = OperandText.Format(m.Value) })
                .ToList();
        }

        public static SubFlowEditor For(SubFlowNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }
            return new SubFlowEditor(node);
        }

        public SubFlowNode Node
        {
            get { return _node; }
        }

        public string FlowFile { get; set; }

        public List<SubFlowInputRow> Inputs { get; private set; }

        /// <summary>相对路径的基准目录（所在流程文件的目录）；流程未保存时为 null，此时相对于程序目录。</summary>
        public string BaseDirectory
        {
            get { return _node.BaseDirectory; }
        }

        /// <summary>按草稿创建一个不在流程树中的副本节点（同名），用于加载定义与试运行。</summary>
        public SubFlowNode CreateDraft()
        {
            var draft = new SubFlowNode(_node.Name, (FlowFile ?? string.Empty).Trim()) { BaseDirectory = _node.BaseDirectory };
            foreach (SubFlowInputRow row in Inputs)
            {
                draft.Inputs.Add(new SubFlowInputMapping { InputPath = (row.InputPath ?? string.Empty).Trim(), Value = OperandText.Parse(row.ValueText) });
            }
            return draft;
        }

        /// <summary>保存的路径：在当前流程目录内（含子目录）时存相对路径，便于整体拷贝；否则存完整路径。</summary>
        public string ToStoredPath(string fullPath)
        {
            return ToStoredPath(fullPath, _node.BaseDirectory);
        }

        public static string ToStoredPath(string fullPath, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return fullPath;
            }
            string relative = Path.GetRelativePath(baseDirectory, fullPath);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? fullPath : relative;
        }

        /// <summary>
        /// 按草稿校验：临时把草稿写入节点，运行流程校验并只保留本节点的问题（含“子流程内部”的问题），随后恢复节点。
        /// </summary>
        public IReadOnlyList<FlowValidationIssue> Validate(FlowNode root)
        {
            string originalFile = _node.FlowFile;
            List<SubFlowInputMapping> originalInputs = _node.Inputs.ToList();
            try
            {
                WriteTo(_node);
                return FlowValidator.Validate(root ?? _node).Issues
                    .Where(i => string.Equals(i.NodeId, _node.Id, StringComparison.Ordinal))
                    .ToList();
            }
            finally
            {
                _node.FlowFile = originalFile;
                _node.Inputs.Clear();
                _node.Inputs.AddRange(originalInputs);
            }
        }

        /// <summary>可作为映射取值的候选：当前节点之前的上游输出（ref:路径）。</summary>
        public IReadOnlyList<string> ValueCandidates(FlowNode root)
        {
            if (root == null)
            {
                return new string[0];
            }
            return RefCandidateService.ForNode(root, _node)
                .Select(c => "ref:" + c.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>把草稿写回节点。</summary>
        public void Apply()
        {
            WriteTo(_node);
        }

        private void WriteTo(SubFlowNode node)
        {
            SubFlowNode draft = CreateDraft();
            node.FlowFile = draft.FlowFile;
            node.Inputs.Clear();
            node.Inputs.AddRange(draft.Inputs);
        }
    }
}
