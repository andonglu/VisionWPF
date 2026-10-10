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

    /// <summary>对象输出的常用 CLR 类型选项（显示标签 + 可持久化/可解析的类型名）。</summary>
    public sealed class FlowObjectTypeOption
    {
        public string Label { get; private set; }

        /// <summary>写入 FlowOutputDef.ClrTypeName 的类型名（AssemblyQualifiedName，Type.GetType 可解析）。</summary>
        public string ClrTypeName { get; private set; }

        private FlowObjectTypeOption(string label, string clrTypeName)
        {
            Label = label;
            ClrTypeName = clrTypeName;
        }

        public static readonly IReadOnlyList<FlowObjectTypeOption> Common = new[]
        {
            new FlowObjectTypeOption("图像", typeof(HalconImage).AssemblyQualifiedName),
            new FlowObjectTypeOption("区域", typeof(HalconRegion).AssemblyQualifiedName),
            new FlowObjectTypeOption("XLD轮廓", typeof(HalconXld).AssemblyQualifiedName),
            new FlowObjectTypeOption("通用对象", typeof(object).AssemblyQualifiedName),
        };
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

        /// <summary>取值可用的引用候选（流程树中该节点之前可见的全部输出），按路径去重，保留类型与是否集合。</summary>
        public IReadOnlyList<RefCandidate> CandidateRefs(FlowNode root)
        {
            if (root == null)
            {
                return new RefCandidate[0];
            }
            return RefCandidateService.ForNode(root, _node)
                .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>取值可用的引用候选（流程树中该节点之前可见的全部输出），格式为 ref:模块.变量。</summary>
        public IReadOnlyList<string> Candidates(FlowNode root)
        {
            return CandidateRefs(root)
                .Select(c => "ref:" + c.Path)
                .ToList();
        }

        /// <summary>
        /// 候选的类型标签：HalconImage→图像、HalconRegion→区域、HalconXld→XLD、
        /// int/double/string/bool→整数/小数/文本/布尔，其余取 CLR 短名；集合加"·数组"后缀。
        /// 供编辑器下拉右侧灰字显示。
        /// </summary>
        public static string TypeTagLabel(Type clrType, bool isCollection)
        {
            string label;
            if (clrType == typeof(HalconImage))
            {
                label = "图像";
            }
            else if (clrType == typeof(HalconRegion))
            {
                label = "区域";
            }
            else if (clrType == typeof(HalconXld))
            {
                label = "XLD";
            }
            else if (clrType == typeof(int))
            {
                label = "整数";
            }
            else if (clrType == typeof(double))
            {
                label = "小数";
            }
            else if (clrType == typeof(string))
            {
                label = "文本";
            }
            else if (clrType == typeof(bool))
            {
                label = "布尔";
            }
            else
            {
                label = clrType == null ? "对象" : clrType.Name;
            }
            return isCollection ? label + "·数组" : label;
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
                if (row.Type == VariableType.Object && string.IsNullOrWhiteSpace(row.ClrTypeName))
                {
                    problems.Add($"输出“{name}”是对象类型，请在下拉中选择对象类型（图像/区域/XLD/自定义）");
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
