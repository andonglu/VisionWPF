using System;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>流程输出表中的一行草稿：名称、形态、类型与取值文本（ref:模块.变量 或常量）。</summary>
    public sealed class FlowOutputRow
    {
        private VariableKind _kind = VariableKind.Single;
        private VariableType _type = VariableType.String;

        public string Name { get; set; } = string.Empty;

        public VariableKind Kind
        {
            get { return _kind; }
            set
            {
                if (value != _kind)
                {
                    _kind = value;
                    ClrTypeName = null;
                }
            }
        }

        public VariableType Type
        {
            get { return _type; }
            set
            {
                if (value != _type)
                {
                    _type = value;
                    ClrTypeName = null;
                }
            }
        }

        /// <summary>原有输出的 CLR 类型名，类型改变时清除。</summary>
        public string ClrTypeName { get; set; }

        /// <summary>取值文本：ref:模块.变量 或常量字面量。</summary>
        public string ValueText { get; set; } = string.Empty;
    }

    /// <summary>
    /// 流程输出节点的编辑模型：加载时深拷贝输出表为草稿（取消不写入节点），校验通过后整表写回。
    /// 校验规则与 <see cref="FlowOutputNode.OnExecute"/> 的运行时失败条件一致：空名称、空取值在运行时会报错。
    /// </summary>
    public sealed class FlowOutputsEditor
    {
        private readonly FlowOutputNode _node;

        public FlowNode Node
        {
            get { return _node; }
        }

        public List<FlowOutputRow> Rows { get; } = new List<FlowOutputRow>();

        private FlowOutputsEditor(FlowOutputNode node)
        {
            _node = node;
        }

        public static FlowOutputsEditor For(FlowOutputNode node)
        {
            var editor = new FlowOutputsEditor(node ?? throw new ArgumentNullException(nameof(node)));
            foreach (FlowOutputDef output in node.Outputs)
            {
                editor.Rows.Add(new FlowOutputRow
                {
                    Name = output.Name,
                    Kind = output.Kind,
                    Type = output.Type,
                    ClrTypeName = output.ClrTypeName,
                    ValueText = OperandText.Format(output.Value)
                });
            }
            return editor;
        }

        /// <summary>新增一行（名称自动编号，取值留空）。</summary>
        public FlowOutputRow AddRow()
        {
            int n = Rows.Count + 1;
            while (Rows.Any(r => string.Equals(r.Name, "输出" + n, StringComparison.OrdinalIgnoreCase)))
            {
                n++;
            }
            var row = new FlowOutputRow { Name = "输出" + n };
            Rows.Add(row);
            return row;
        }

        /// <summary>取值可用的引用候选（流程树中该节点之前可见的全部输出），格式为 ref:模块.变量。</summary>
        public IReadOnlyList<string> Candidates(FlowNode root)
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

        /// <summary>检查输出名与取值是否完整；返回问题列表，空表示可以保存。引用是否有效由流程校验负责。</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FlowOutputRow row in Rows)
            {
                string name = (row.Name ?? string.Empty).Trim();
                if (!ToolMetadata.IsValidOutputName(name))
                {
                    problems.Add($"输出名“{name}”不能为空，也不能包含空白或 . [ ] {{ }} 字符");
                    continue;
                }
                if (!names.Add(name))
                {
                    problems.Add($"输出名“{name}”重复");
                }
                if (string.IsNullOrWhiteSpace(row.ValueText))
                {
                    problems.Add($"输出“{name}”未填写取值");
                }
            }
            return problems;
        }

        /// <summary>写回节点（覆盖原有输出表，全部替换为新实例）。调用前应先 <see cref="Validate"/>。</summary>
        public void Apply()
        {
            _node.Outputs.Clear();
            foreach (FlowOutputRow row in Rows)
            {
                _node.Outputs.Add(new FlowOutputDef
                {
                    Name = row.Name.Trim(),
                    Kind = row.Kind,
                    Type = row.Type,
                    ClrTypeName = row.ClrTypeName,
                    Value = OperandText.Parse(row.ValueText)
                });
            }
        }
    }
}
