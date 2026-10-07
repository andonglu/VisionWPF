using System;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>公共输出的类型选项（形态 + 类型）。</summary>
    public sealed class SharedOutputType
    {
        public string Label { get; private set; }
        public VariableKind Kind { get; private set; }
        public VariableType Type { get; private set; }

        private SharedOutputType(string label, VariableKind kind, VariableType type)
        {
            Label = label;
            Kind = kind;
            Type = type;
        }

        public static IReadOnlyList<SharedOutputType> All { get; } = new[]
        {
            new SharedOutputType("小数", VariableKind.Single, VariableType.Double),
            new SharedOutputType("整数", VariableKind.Single, VariableType.Int),
            new SharedOutputType("布尔", VariableKind.Single, VariableType.Bool),
            new SharedOutputType("文本", VariableKind.Single, VariableType.String),
            new SharedOutputType("小数数组", VariableKind.Array, VariableType.Double),
            new SharedOutputType("整数数组", VariableKind.Array, VariableType.Int),
            new SharedOutputType("文本数组", VariableKind.Array, VariableType.String),
            new SharedOutputType("对象", VariableKind.Object, VariableType.Object)
        };

        /// <summary>找到与形态、类型对应的选项；没有完全对应的（如对象数组）按形态归为数组或对象。</summary>
        public static SharedOutputType Find(VariableKind kind, VariableType type)
        {
            return All.FirstOrDefault(t => t.Kind == kind && t.Type == type)
                ?? All.First(t => t.Kind == (kind == VariableKind.Array ? VariableKind.Array : VariableKind.Object)
                    && (kind != VariableKind.Array || t.Type == VariableType.Double));
        }

        public override string ToString()
        {
            return Label;
        }
    }

    /// <summary>公共输出表中的一行：输出名、类型，以及每个分支的取值文本（ref:模块.变量 或常量）。</summary>
    public sealed class SharedOutputRow
    {
        private SharedOutputType _outputType = SharedOutputType.All[0];

        public string Name { get; set; } = string.Empty;

        public SharedOutputType OutputType
        {
            get { return _outputType; }
            set
            {
                if (value != null && value != _outputType)
                {
                    _outputType = value;
                    ClrTypeName = null;
                }
            }
        }

        /// <summary>原有输出的 CLR 类型名（对象输出用于更精确的候选过滤），类型改变时清除。</summary>
        public string ClrTypeName { get; set; }
        /// <summary>按分支顺序排列的取值文本。</summary>
        public List<string> Values { get; } = new List<string>();
    }

    /// <summary>
    /// IfElse / Switch 公共输出的编辑模型：加载为“输出 × 分支”的表格，校验后写回节点。
    /// IfElse 的分支为 If、Else；Switch 的分支为各个分支节点（含默认分支，按顺序）。
    /// </summary>
    public sealed class SharedOutputsEditor
    {
        private readonly IfElseNode _ifElse;
        private readonly SwitchNode _switch;

        public FlowNode Node
        {
            get { return (FlowNode)_ifElse ?? _switch; }
        }

        /// <summary>分支列名（IfElse 为 If 分支、Else 分支；Switch 为各分支名）。</summary>
        public IReadOnlyList<string> BranchNames { get; private set; }

        public List<SharedOutputRow> Rows { get; } = new List<SharedOutputRow>();

        private SharedOutputsEditor(IfElseNode ifElse, SwitchNode switchNode)
        {
            _ifElse = ifElse;
            _switch = switchNode;
        }

        public static SharedOutputsEditor For(IfElseNode node)
        {
            var editor = new SharedOutputsEditor(node ?? throw new ArgumentNullException(nameof(node)), null)
            {
                BranchNames = new[] { "If 分支", "Else 分支" }
            };
            foreach (BranchOutputDef output in node.Outputs)
            {
                var row = new SharedOutputRow
                {
                    Name = output.Name,
                    OutputType = SharedOutputType.Find(output.Kind, output.Type),
                    ClrTypeName = output.ClrTypeName
                };
                row.Values.Add(OperandText.Format(output.IfValue));
                row.Values.Add(OperandText.Format(output.ElseValue));
                editor.Rows.Add(row);
            }
            return editor;
        }

        public static SharedOutputsEditor For(SwitchNode node)
        {
            List<SwitchCaseNode> cases = (node ?? throw new ArgumentNullException(nameof(node))).CaseNodes.ToList();
            var editor = new SharedOutputsEditor(null, node)
            {
                BranchNames = cases.Select(c => c.IsDefault && c.Name.IndexOf("默认", StringComparison.Ordinal) < 0 ? c.Name + "（默认）" : c.Name).ToList()
            };
            foreach (SwitchOutputDef output in node.Outputs)
            {
                var row = new SharedOutputRow
                {
                    Name = output.Name,
                    OutputType = SharedOutputType.Find(output.Kind, output.Type),
                    ClrTypeName = output.ClrTypeName
                };
                foreach (SwitchCaseNode switchCase in cases)
                {
                    switchCase.OutputValues.TryGetValue(output.Name, out Operand value);
                    row.Values.Add(OperandText.Format(value));
                }
                editor.Rows.Add(row);
            }
            return editor;
        }

        /// <summary>新增一行（名称自动编号，取值留空）。</summary>
        public SharedOutputRow AddRow()
        {
            int n = Rows.Count + 1;
            while (Rows.Any(r => string.Equals(r.Name, "输出" + n, StringComparison.OrdinalIgnoreCase)))
            {
                n++;
            }
            var row = new SharedOutputRow { Name = "输出" + n };
            row.Values.AddRange(BranchNames.Select(_ => string.Empty));
            Rows.Add(row);
            return row;
        }

        /// <summary>某个分支的取值可用的引用候选（含该分支内部输出），格式为 ref:模块.变量。</summary>
        public IReadOnlyList<string> CandidatesFor(FlowNode root, int branchIndex)
        {
            RefScope scope;
            if (_ifElse != null)
            {
                scope = RefCandidateService.ScopeForBranchOutput(root, _ifElse, branchIndex == 0 ? IfBranch.If : IfBranch.Else);
            }
            else
            {
                scope = RefCandidateService.ScopeForSwitchCaseOutput(root, _switch, _switch.CaseNodes.ElementAt(branchIndex));
            }
            return scope.Candidates.Select(c => "ref:" + c.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>检查输出名与取值是否完整；返回问题列表，空表示可以保存。引用是否有效由流程校验负责。</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SharedOutputRow row in Rows)
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
                for (int i = 0; i < BranchNames.Count; i++)
                {
                    if (i >= row.Values.Count || string.IsNullOrWhiteSpace(row.Values[i]))
                    {
                        problems.Add($"输出“{name}”在{BranchNames[i]}中未填写取值");
                    }
                }
            }
            return problems;
        }

        /// <summary>写回节点（覆盖原有公共输出）。调用前应先 <see cref="Validate"/>。</summary>
        public void Apply()
        {
            if (_ifElse != null)
            {
                _ifElse.Outputs.Clear();
                foreach (SharedOutputRow row in Rows)
                {
                    _ifElse.Outputs.Add(new BranchOutputDef
                    {
                        Name = row.Name.Trim(),
                        Kind = row.OutputType.Kind,
                        Type = row.OutputType.Type,
                        ClrTypeName = row.ClrTypeName,
                        IfValue = OperandText.Parse(ValueAt(row, 0)),
                        ElseValue = OperandText.Parse(ValueAt(row, 1))
                    });
                }
                return;
            }

            List<SwitchCaseNode> cases = _switch.CaseNodes.ToList();
            _switch.Outputs.Clear();
            foreach (SwitchCaseNode switchCase in cases)
            {
                switchCase.OutputValues.Clear();
            }
            foreach (SharedOutputRow row in Rows)
            {
                string name = row.Name.Trim();
                _switch.Outputs.Add(new SwitchOutputDef
                {
                    Name = name,
                    Kind = row.OutputType.Kind,
                    Type = row.OutputType.Type,
                    ClrTypeName = row.ClrTypeName
                });
                for (int i = 0; i < cases.Count; i++)
                {
                    cases[i].OutputValues[name] = OperandText.Parse(ValueAt(row, i));
                }
            }
        }

        private static string ValueAt(SharedOutputRow row, int index)
        {
            return index < row.Values.Count ? row.Values[index] : string.Empty;
        }
    }
}
