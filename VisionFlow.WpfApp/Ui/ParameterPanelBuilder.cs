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
            AddSection("条件");
            TextBox left = AddTextRow("左操作数", OperandText(node.Condition?.Left), null);
            ComboBox op = AddComboRow("比较符", Enum.GetNames(typeof(ComparisonOperator)), node.Condition?.Operator.ToString() ?? ComparisonOperator.Equal.ToString(), null);
            TextBox right = AddTextRow("右操作数", OperandText(node.Condition?.Right), null);
            AddButtonRow("应用条件", () =>
            {
                node.Condition = new ComparisonCondition
                {
                    Left = ParseOperand(left.Text),
                    Operator = (ComparisonOperator)Enum.Parse(typeof(ComparisonOperator), op.Text),
                    Right = ParseOperand(right.Text)
                };
                _setStatus("条件已更新");
            });
            AddInfo("引用请使用 ref:模块.变量；常量可直接输入数字、true/false 或文本。");
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
