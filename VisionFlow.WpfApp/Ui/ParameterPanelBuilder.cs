using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HalconDotNet;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;
using VisionFlow.WpfToolEditors.Controls;

namespace VisionFlow.WpfApp.Ui
{
    /// <summary>
    /// 参数面板构建器（VF-08 从 MainWindow 平移）：按节点类型生成名称、输入引用、标量参数等编辑行。
    /// 主窗口通过委托注入可编辑检查、脏标记、状态栏、流程树刷新与工具编辑器入口，本类不持有窗口引用。
    /// </summary>
    public sealed class ParameterPanelBuilder
    {
        private readonly StackPanel _panel;
        private readonly Func<FlowNode> _flowRoot;
        private readonly Func<HObject> _inputImage;
        private readonly Func<string, bool> _ensureEditable;
        private readonly Action _markDirty;
        private readonly Action<string, string> _showWarning;
        private readonly Action _refreshFlowTree;
        private readonly Action<string> _setStatus;
        private readonly Action<ToolNode> _openToolEditor;

        public ParameterPanelBuilder(
            StackPanel panel,
            Func<FlowNode> flowRoot,
            Func<HObject> inputImage,
            Func<string, bool> ensureEditable,
            Action markDirty,
            Action<string, string> showWarning,
            Action refreshFlowTree,
            Action<string> setStatus,
            Action<ToolNode> openToolEditor)
        {
            _panel = panel ?? throw new ArgumentNullException(nameof(panel));
            _flowRoot = flowRoot ?? throw new ArgumentNullException(nameof(flowRoot));
            _inputImage = inputImage ?? throw new ArgumentNullException(nameof(inputImage));
            _ensureEditable = ensureEditable ?? throw new ArgumentNullException(nameof(ensureEditable));
            _markDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));
            _showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
            _refreshFlowTree = refreshFlowTree ?? throw new ArgumentNullException(nameof(refreshFlowTree));
            _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
            _openToolEditor = openToolEditor ?? throw new ArgumentNullException(nameof(openToolEditor));
        }

        public void Build(FlowNode node)
        {
            _panel.Children.Clear();
            if (node == null)
            {
                AddInfo("请选择流程节点。");
                return;
            }

            AddTextRow("节点名称", node.Name, value =>
            {
                node.Name = value;
                _refreshFlowTree();
            });

            if (node is ToolNode toolNode)
            {
                BuildToolParameterPanel(toolNode);
            }
            else if (node is IfElseNode ifElse)
            {
                BuildIfElseParameterPanel(ifElse);
            }
            else if (node is ForLoopNode loop)
            {
                BuildLoopParameterPanel(loop);
            }
            else if (node is WhileLoopNode whileLoop)
            {
                BuildWhileParameterPanel(whileLoop);
            }
            else if (node is LoopControlNode control)
            {
                AddInfo(control.Signal == LoopControlSignal.Break
                    ? "执行到此处时跳出最内层循环，循环体中后续节点不再执行。只能放在循环体内（可放在 IfElse 分支中）。"
                    : "执行到此处时结束最内层循环的本次迭代，直接进入下一次。只能放在循环体内（可放在 IfElse 分支中）。");
            }
            else if (node is FlowOutputNode outputNode)
            {
                AddInfo("流程输出节点的复杂输出表仍复用 WinForms 编辑页；可保存后继续用原编辑器精细配置。");
                AddButtonRow("快速添加默认输出", () =>
                {
                    if (outputNode.Outputs.Count == 0)
                    {
                        outputNode.Outputs.Add(new FlowOutputDef { Name = "Ok", Kind = VariableKind.Single, Type = VariableType.Bool, Value = Operand.Const(true) });
                        outputNode.Outputs.Add(new FlowOutputDef { Name = "Code", Kind = VariableKind.Single, Type = VariableType.Int, Value = Operand.Const(0) });
                        outputNode.Outputs.Add(new FlowOutputDef { Name = "Message", Kind = VariableKind.Single, Type = VariableType.String, Value = Operand.Const("OK") });
                    }
                });
            }
        }

        private void BuildToolParameterPanel(ToolNode node)
        {
            ToolBase tool = node.Tool;
            AddTextRow("模块名", tool.ModuleName, value =>
            {
                tool.ModuleName = value;
                node.Name = value;
                _refreshFlowTree();
            });

            IReadOnlyList<ToolInputRefDef> inputRefs = ToolMetadata.GetInputRefs(tool.GetType());
            AddSection("输入引用");
            if (inputRefs.Count == 0)
            {
                AddInfo("无输入引用。");
            }
            foreach (ToolInputRefDef input in inputRefs)
            {
                AddInputRefRow(node, input);
            }

            AddSection("参数");
            bool hasScalar = false;
            foreach (PropertyInfo property in SerializableProperties(tool.GetType()))
            {
                if (inputRefs.Any(i => i.PropertyName == property.Name))
                {
                    continue;
                }
                hasScalar = true;
                AddScalarRow(tool, property);
            }
            if (!hasScalar)
            {
                AddInfo("无可编辑标量参数。");
            }

            AddButtonRow("打开专用编辑窗体", () => _openToolEditor(node), marksDirty: false);
        }

        private void BuildIfElseParameterPanel(IfElseNode node)
        {
            AddConditionEditor(node.Condition, RefCandidateService.ForNode(_flowRoot(), node), condition => node.Condition = condition);
        }

        private void BuildWhileParameterPanel(WhileLoopNode node)
        {
            RefScope scope = RefCandidateService.ScopeForWhileCondition(_flowRoot(), node);
            AddConditionEditor(node.Condition, scope.Candidates, condition => node.Condition = condition);
            AddInfo("条件成立时重复执行循环体，条件中的 Loop.Index 为本循环的迭代序号（从 0 开始）。");

            AddSection("循环设置");
            var testAfter = new CheckBox
            {
                Content = "先执行一次再判断条件（条件可引用循环体的输出）",
                IsChecked = node.TestAfterBody,
                Margin = new Thickness(0, 0, 0, 16)
            };
            RoutedEventHandler toggle = (s, e) =>
            {
                bool value = testAfter.IsChecked == true;
                if (value == node.TestAfterBody || !_ensureEditable("参数修改"))
                {
                    return;
                }
                node.TestAfterBody = value;
                _markDirty();
                // 作用域随之变化（循环体输出是否可用），重建面板刷新引用候选
                Build(node);
            };
            testAfter.Checked += toggle;
            testAfter.Unchecked += toggle;
            _panel.Children.Add(testAfter);

            AddNumberRow("最多执行次数", node.MaxIterations, value =>
            {
                int max = (int)Math.Round(value);
                if (max <= 0)
                {
                    throw new ArgumentException("最多执行次数必须大于 0");
                }
                node.MaxIterations = max;
            });
            AddInfo("达到最多执行次数时条件仍成立，按可能的死循环中止并报错。");
        }

        /// <summary>
        /// 条件编辑区：卡片式条件列表（比较 / 表达式 / 条件组，可嵌套），修改写入草稿，点“应用条件”才通过 apply 生效。
        /// </summary>
        private void AddConditionEditor(ICondition current, IEnumerable<RefCandidate> scopeCandidates, Action<ICondition> apply)
        {
            AddSection("条件");
            ConditionDraft root = ConditionDraft.RootFrom(current);
            List<string> candidates = scopeCandidates
                .Select(c => "ref:" + c.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var host = new StackPanel();
            _panel.Children.Add(host);
            Action render = null;
            render = () =>
            {
                host.Children.Clear();
                RenderConditionGroup(host, root, string.Empty, 0, candidates, render);
            };
            render();

            AddButtonRow("应用条件", () =>
            {
                string incomplete = root.FindIncomplete(string.Empty);
                if (incomplete != null)
                {
                    _showWarning("条件未填写完整", incomplete);
                    return;
                }
                apply(root.ToNodeCondition());
                _markDirty();
                _setStatus("条件已更新");
            }, marksDirty: false);
            AddInfo("比较条件的操作数：引用写 ref:模块.变量，常量直接输入数字、true/false 或文本。"
                + "表达式条件：引用写在花括号内，如 {匹配1.MatchCount} == 2 && isvalid({测量1.Row})。");
        }

        /// <summary>按草稿渲染一组条件；结构变化（增删、移动、切换组合方式）后调用 rerender 整体重绘。</summary>
        private void RenderConditionGroup(Panel host, ConditionDraft group, string path, int depth,
            IReadOnlyList<string> candidates, Action rerender)
        {
            if (group.Items.Count > 1 || depth > 0)
            {
                var logic = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
                logic.Items.Add("全部满足（且）");
                logic.Items.Add("任一满足（或）");
                logic.SelectedIndex = group.Logic == ConditionLogic.And ? 0 : 1;
                logic.SelectionChanged += (s, e) => group.Logic = logic.SelectedIndex == 0 ? ConditionLogic.And : ConditionLogic.Or;
                host.Children.Add(logic);
            }

            for (int i = 0; i < group.Items.Count; i++)
            {
                int index = i;
                ConditionDraft item = group.Items[i];
                string itemPath = path.Length == 0 ? (i + 1).ToString(CultureInfo.InvariantCulture) : path + "." + (i + 1);
                var card = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                card.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                var body = new StackPanel();
                card.Child = body;

                var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
                header.Children.Add(SmallButton("删除", () => { group.Items.RemoveAt(index); rerender(); }, Dock.Right));
                header.Children.Add(SmallButton("下移", () => { MoveItem(group.Items, index, 1); rerender(); }, Dock.Right));
                header.Children.Add(SmallButton("上移", () => { MoveItem(group.Items, index, -1); rerender(); }, Dock.Right));
                string kindText = item.Kind == ConditionDraftKind.Compare ? "比较" : item.Kind == ConditionDraftKind.Expression ? "表达式" : "条件组";
                var title = new TextBlock { Text = $"条件 {itemPath}（{kindText}）", VerticalAlignment = VerticalAlignment.Center };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                header.Children.Add(title);
                body.Children.Add(header);

                switch (item.Kind)
                {
                    case ConditionDraftKind.Compare:
                        RenderComparison(body, item, candidates);
                        break;
                    case ConditionDraftKind.Expression:
                        var expression = new TextBox
                        {
                            Text = item.Expression,
                            FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI"),
                            TextWrapping = TextWrapping.Wrap
                        };
                        expression.TextChanged += (s, e) => item.Expression = expression.Text;
                        body.Children.Add(expression);
                        break;
                    default:
                        RenderConditionGroup(body, item, itemPath, depth + 1, candidates, rerender);
                        break;
                }
                host.Children.Add(card);
            }

            var addRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            addRow.Children.Add(SmallButton("+ 比较", () => { group.Items.Add(ConditionDraft.NewComparison()); rerender(); }, null));
            addRow.Children.Add(SmallButton("+ 表达式", () => { group.Items.Add(new ConditionDraft { Kind = ConditionDraftKind.Expression }); rerender(); }, null));
            addRow.Children.Add(SmallButton("+ 条件组", () =>
            {
                var nested = new ConditionDraft { Kind = ConditionDraftKind.Group };
                nested.Items.Add(ConditionDraft.NewComparison());
                group.Items.Add(nested);
                rerender();
            }, null));
            host.Children.Add(addRow);
        }

        private static void RenderComparison(Panel body, ConditionDraft item, IReadOnlyList<string> candidates)
        {
            ComboBox left = CandidateCombo(item.Left, candidates);
            left.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((s, e) => item.Left = left.Text));
            left.SelectionChanged += (s, e) => item.Left = left.SelectedItem as string ?? left.Text;
            body.Children.Add(left);

            var op = new ComboBox { Margin = new Thickness(0, 6, 0, 6) };
            foreach (ComparisonOperator value in Enum.GetValues(typeof(ComparisonOperator)))
            {
                op.Items.Add(new ComboBoxItem { Content = ComparisonCondition.ToSymbol(value), Tag = value });
                if (value == item.Operator)
                {
                    op.SelectedIndex = op.Items.Count - 1;
                }
            }
            body.Children.Add(op);

            ComboBox right = CandidateCombo(item.Right, candidates);
            right.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((s, e) => item.Right = right.Text));
            right.SelectionChanged += (s, e) => item.Right = right.SelectedItem as string ?? right.Text;
            right.Visibility = ComparisonCondition.IsUnary(item.Operator) ? Visibility.Collapsed : Visibility.Visible;
            body.Children.Add(right);

            op.SelectionChanged += (s, e) =>
            {
                if (op.SelectedItem is ComboBoxItem selected)
                {
                    item.Operator = (ComparisonOperator)selected.Tag;
                    right.Visibility = ComparisonCondition.IsUnary(item.Operator) ? Visibility.Collapsed : Visibility.Visible;
                }
            };
        }

        private static ComboBox CandidateCombo(string text, IReadOnlyList<string> candidates)
        {
            var combo = new ComboBox { IsEditable = true };
            foreach (string candidate in candidates)
            {
                combo.Items.Add(candidate);
            }
            combo.Text = text ?? string.Empty;
            return combo;
        }

        private static Button SmallButton(string text, Action click, Dock? dock)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 0,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 0, 0, 0)
            };
            button.Click += (s, e) => click();
            if (dock.HasValue)
            {
                DockPanel.SetDock(button, dock.Value);
            }
            return button;
        }

        private static void MoveItem(List<ConditionDraft> items, int index, int offset)
        {
            int target = index + offset;
            if (target < 0 || target >= items.Count)
            {
                return;
            }
            ConditionDraft item = items[index];
            items.RemoveAt(index);
            items.Insert(target, item);
        }

        private enum ConditionDraftKind
        {
            Compare,
            Expression,
            Group
        }

        /// <summary>条件编辑草稿：面板中的修改先写入草稿，点“应用条件”时才转换为节点条件。</summary>
        private sealed class ConditionDraft
        {
            public ConditionDraftKind Kind { get; set; }
            public string Left { get; set; } = string.Empty;
            public ComparisonOperator Operator { get; set; }
            public string Right { get; set; } = string.Empty;
            public string Expression { get; set; } = string.Empty;
            public ConditionLogic Logic { get; set; }
            public List<ConditionDraft> Items { get; } = new List<ConditionDraft>();

            public static ConditionDraft NewComparison()
            {
                return new ConditionDraft { Kind = ConditionDraftKind.Compare };
            }

            /// <summary>顶层总是一个组；节点条件为单条比较或表达式时作为组内唯一一项。</summary>
            public static ConditionDraft RootFrom(ICondition condition)
            {
                if (condition is ConditionGroup)
                {
                    return From(condition);
                }
                var root = new ConditionDraft { Kind = ConditionDraftKind.Group };
                root.Items.Add(condition == null ? NewComparison() : From(condition));
                return root;
            }

            private static ConditionDraft From(ICondition condition)
            {
                switch (condition)
                {
                    case ComparisonCondition comparison:
                        return new ConditionDraft
                        {
                            Kind = ConditionDraftKind.Compare,
                            Left = OperandText(comparison.Left),
                            Operator = comparison.Operator,
                            Right = OperandText(comparison.Right)
                        };
                    case ExpressionCondition expression:
                        return new ConditionDraft { Kind = ConditionDraftKind.Expression, Expression = expression.Expression ?? string.Empty };
                    case ConditionGroup group:
                        var draft = new ConditionDraft { Kind = ConditionDraftKind.Group, Logic = group.Logic };
                        draft.Items.AddRange(group.Items.Select(From));
                        return draft;
                    default:
                        return NewComparison();
                }
            }

            /// <summary>找出未填写完整的条件（比较缺左操作数、表达式为空、组内无条件），返回提示；都完整时返回 null。</summary>
            public string FindIncomplete(string path)
            {
                if (Kind == ConditionDraftKind.Compare)
                {
                    return string.IsNullOrWhiteSpace(Left) ? $"条件 {path} 的左操作数为空" : null;
                }
                if (Kind == ConditionDraftKind.Expression)
                {
                    return string.IsNullOrWhiteSpace(Expression) ? $"条件 {path} 的表达式为空" : null;
                }
                if (Items.Count == 0)
                {
                    return path.Length == 0 ? "请至少添加一个条件" : $"条件组 {path} 中没有条件";
                }
                for (int i = 0; i < Items.Count; i++)
                {
                    string itemPath = path.Length == 0 ? (i + 1).ToString(CultureInfo.InvariantCulture) : path + "." + (i + 1);
                    string message = Items[i].FindIncomplete(itemPath);
                    if (message != null)
                    {
                        return message;
                    }
                }
                return null;
            }

            /// <summary>转换为节点条件；顶层只有一项时直接使用该项，单条比较因此保持流程文件版本 1 的格式。</summary>
            public ICondition ToNodeCondition()
            {
                return Items.Count == 1 ? Items[0].ToCondition() : ToCondition();
            }

            private ICondition ToCondition()
            {
                switch (Kind)
                {
                    case ConditionDraftKind.Compare:
                        return new ComparisonCondition
                        {
                            Left = ParseOperand(Left),
                            Operator = Operator,
                            Right = ComparisonCondition.IsUnary(Operator) ? null : ParseOperand(Right)
                        };
                    case ConditionDraftKind.Expression:
                        return new ExpressionCondition { Expression = Expression.Trim() };
                    default:
                        var group = new ConditionGroup { Logic = Logic };
                        group.Items.AddRange(Items.Select(i => i.ToCondition()));
                        return group;
                }
            }
        }

        private void BuildLoopParameterPanel(ForLoopNode node)
        {
            if (node.Mode == ForLoopMode.Count)
            {
                TextBox count = AddTextRow("循环次数", OperandText(node.CountSource), null);
                AddButtonRow("应用次数", () =>
                {
                    node.CountSource = ParseOperand(count.Text);
                    _setStatus("循环次数已更新");
                });
            }
            else
            {
                ComboBox items = AddComboRow("集合来源", RefCandidateService.Collections(_flowRoot(), node).Select(c => c.Path), node.ItemsPath, null);
                AddButtonRow("应用集合", () =>
                {
                    node.ItemsPath = items.Text;
                    _setStatus("循环集合已更新");
                });
            }
        }

        private void AddInputRefRow(ToolNode node, ToolInputRefDef input)
        {
            var values = new List<string>();
            if (input.Optional)
            {
                values.Add(string.Empty);
            }
            HObject inputImage = _inputImage();
            if (input.ExpectedType == typeof(HalconImage) && inputImage != null && inputImage.IsInitialized())
            {
                values.Add("Input.Image");
            }
            values.AddRange(RefCandidateService.ForInput(_flowRoot(), node, input.ExpectedType, input.AcceptsCollection).Select(c => c.Path));

            AddComboRow(input.DisplayName, values.Distinct(StringComparer.OrdinalIgnoreCase),
                input.Property.GetValue(node.Tool) as string ?? string.Empty,
                value => input.Property.SetValue(node.Tool, value));
        }

        private void AddScalarRow(ToolBase tool, PropertyInfo property)
        {
            Type type = property.PropertyType;
            if (type == typeof(bool))
            {
                var check = new CheckBox
                {
                    Content = property.Name,
                    IsChecked = (bool)property.GetValue(tool),
                    Margin = new Thickness(0, 0, 0, 16)
                };
                check.Checked += (s, e) =>
                {
                    if (_ensureEditable("参数修改"))
                    {
                        property.SetValue(tool, true);
                        _markDirty();
                    }
                };
                check.Unchecked += (s, e) =>
                {
                    if (_ensureEditable("参数修改"))
                    {
                        property.SetValue(tool, false);
                        _markDirty();
                    }
                };
                _panel.Children.Add(check);
                return;
            }

            if (type.IsEnum)
            {
                AddComboRow(property.Name, Enum.GetNames(type), property.GetValue(tool).ToString(),
                    value => property.SetValue(tool, Enum.Parse(type, value)));
                return;
            }

            if (IsNumericType(type))
            {
                AddNumberRow(property.Name, Convert.ToDouble(property.GetValue(tool), CultureInfo.InvariantCulture),
                    value => property.SetValue(tool, ConvertNumber(value, type)));
                return;
            }

            AddTextRow(property.Name, Convert.ToString(property.GetValue(tool), CultureInfo.CurrentCulture),
                value => property.SetValue(tool, ConvertText(value, type)));
        }

        private NumericInputControl AddNumberRow(string label, double value, Action<double> commit)
        {
            AddLabel(label);
            var input = new NumericInputControl
            {
                Value = value,
                Minimum = -1000000,
                Maximum = 1000000,
                Increment = Math.Abs(value) >= 10 ? 1 : 0.1,
                DecimalPlaces = Math.Abs(value - Math.Round(value)) < 1e-9 ? 0 : 3,
                Margin = new Thickness(0, 0, 0, 16)
            };
            double committedValue = input.Value;
            input.ValueChanged += (s, e) =>
            {
                if (input.Value.Equals(committedValue))
                {
                    return;
                }
                if (!_ensureEditable("参数修改"))
                {
                    return;
                }
                try
                {
                    commit(input.Value);
                    committedValue = input.Value;
                    _markDirty();
                }
                catch (Exception ex)
                {
                    _showWarning("参数错误", ex.Message);
                }
            };
            _panel.Children.Add(input);
            return input;
        }

        private void AddSection(string text)
        {
            _panel.Children.Add(new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 8)
            });
        }

        private void AddInfo(string text)
        {
            _panel.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 16)
            });
        }

        private TextBox AddTextRow(string label, string value, Action<string> commit)
        {
            AddLabel(label);
            var textBox = new TextBox { Text = value ?? string.Empty, Margin = new Thickness(0, 0, 0, 16) };
            if (commit != null)
            {
                string committedText = textBox.Text;
                Action commitChangedText = () =>
                {
                    string editedText = textBox.Text;
                    if (!string.Equals(editedText, committedText, StringComparison.Ordinal)
                        && CommitText(textBox, commit))
                    {
                        committedText = editedText;
                    }
                };
                textBox.LostFocus += (s, e) => commitChangedText();
                textBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        commitChangedText();
                        e.Handled = true;
                    }
                };
            }
            _panel.Children.Add(textBox);
            return textBox;
        }

        private ComboBox AddComboRow(string label, IEnumerable<string> values, string selected, Action<string> commit)
        {
            AddLabel(label);
            var combo = new ComboBox
            {
                IsEditable = true,
                Margin = new Thickness(0, 0, 0, 16)
            };
            foreach (string value in values ?? Enumerable.Empty<string>())
            {
                combo.Items.Add(value);
            }
            combo.Text = selected ?? string.Empty;
            if (commit != null)
            {
                string committedText = combo.Text;
                Action<string> commitChangedValue = value =>
                {
                    if (string.Equals(value, committedText, StringComparison.Ordinal)
                        || !_ensureEditable("参数修改"))
                    {
                        return;
                    }
                    try
                    {
                        commit(value);
                        committedText = value;
                        _markDirty();
                    }
                    catch (Exception ex) when (ToolEditTransaction.IsConfigurationException(ex))
                    {
                        _showWarning("参数错误", ex.GetBaseException().Message);
                    }
                };
                combo.LostFocus += (s, e) => commitChangedValue(combo.Text);
                combo.SelectionChanged += (s, e) =>
                {
                    if (combo.SelectedItem != null)
                    {
                        commitChangedValue(combo.SelectedItem.ToString());
                    }
                };
            }
            _panel.Children.Add(combo);
            return combo;
        }

        private void AddButtonRow(string text, Action click, bool marksDirty = true)
        {
            var button = new Button
            {
                Content = text,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 16, 0, 0)
            };
            button.Click += (s, e) =>
            {
                if (_ensureEditable("参数修改"))
                {
                    click();
                    if (marksDirty)
                    {
                        _markDirty();
                    }
                }
            };
            _panel.Children.Add(button);
        }

        private void AddLabel(string label)
        {
            _panel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        private bool CommitText(TextBox textBox, Action<string> commit)
        {
            if (!_ensureEditable("参数修改"))
            {
                return false;
            }
            try
            {
                commit(textBox.Text);
                _markDirty();
                return true;
            }
            catch (Exception ex)
            {
                _showWarning("参数错误", ex.Message);
                return false;
            }
        }

        internal static IEnumerable<PropertyInfo> SerializableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(ToolBase.ModuleName)
                    && (p.PropertyType == typeof(string)
                        || p.PropertyType == typeof(int)
                        || p.PropertyType == typeof(double)
                        || p.PropertyType == typeof(bool)
                        || p.PropertyType.IsEnum));
        }

        internal static object ConvertText(string value, Type type)
        {
            if (type == typeof(string))
            {
                return value;
            }
            if (type == typeof(int))
            {
                return int.Parse(value, CultureInfo.CurrentCulture);
            }
            if (type == typeof(double))
            {
                return double.Parse(value, CultureInfo.CurrentCulture);
            }
            throw new NotSupportedException("不支持编辑属性类型 " + type.Name);
        }

        internal static bool IsNumericType(Type type)
        {
            return type == typeof(int)
                || type == typeof(double);
        }

        internal static object ConvertNumber(double value, Type type)
        {
            if (type == typeof(int))
            {
                return (int)Math.Round(value);
            }
            if (type == typeof(double))
            {
                return value;
            }
            throw new NotSupportedException("不支持编辑属性类型 " + type.Name);
        }

        internal static Operand ParseOperand(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
            {
                return Operand.Ref(text.Substring(4).Trim());
            }
            if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            {
                return Operand.Const(null);
            }
            if (text == "\"\"")
            {
                return Operand.Const(string.Empty);
            }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double d))
            {
                return Operand.Const(d);
            }
            if (bool.TryParse(text, out bool b))
            {
                return Operand.Const(b);
            }
            return Operand.Const(text);
        }

        internal static string OperandText(Operand operand)
        {
            if (operand == null)
            {
                return string.Empty;
            }
            return operand.IsConstant ? Convert.ToString(operand.ConstantValue, CultureInfo.CurrentCulture) : "ref:" + operand.Reference;
        }
    }
}
