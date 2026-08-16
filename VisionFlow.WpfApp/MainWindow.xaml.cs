using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HalconDotNet;
using Microsoft.Win32;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Validation;
using VisionFlow.Variables;
using VisionFlow.WpfToolEditors.Controls;
using VisionFlow.WpfToolEditors.Editors;

namespace VisionFlow.WpfApp
{
    public partial class MainWindow : Window
    {
        private sealed class BranchRef
        {
            public IfElseNode Node { get; set; }
            public IfBranch Branch { get; set; }
        }

        private sealed class VariableRow
        {
            public string Path { get; set; }
            public string Type { get; set; }
            public int Count { get; set; }
            public string Value { get; set; }
        }

        private sealed class LogRow
        {
            public string Level { get; set; }
            public string NodeName { get; set; }
            public string Message { get; set; }
        }

        private sealed class DisplayOutputRow
        {
            public string Path { get; set; }
            public Variable Variable { get; set; }

            public override string ToString()
            {
                return Path;
            }
        }

        private sealed class DisplaySettingsModel
        {
            public string OverlayDrawMode { get; set; } = "margin";
            public double OverlayFillOpacity { get; set; } = 0.35;
            public double OverlayLineWidth { get; set; } = 2.0;
        }

        private readonly FlowEditModel _model = new FlowEditModel();
        private readonly IVisionFlowRuntime _runtime = new VisionFlowRuntime();
        private readonly Dictionary<string, TreeViewItem> _flowItemsByNodeId = new Dictionary<string, TreeViewItem>();
        private readonly List<PluginLoadResult> _pluginLoadResults = new List<PluginLoadResult>();
        private HObject _inputImage;
        private string _inputImagePath;
        private FlowContext _lastRunContext;
        private CancellationTokenSource _runCts;
        private string _overlayDrawMode = "margin";
        private double _overlayFillOpacity = 0.35;
        private double _overlayLineWidth = 2.0;

        public MainWindow()
        {
            InitializeComponent();
            LoadDisplaySettings();
            ApplyDisplaySettings();
            BootstrapEditor();
            BuildToolbox();
            RefreshFlowTree();
            WriteStartupHint();
        }

        private void BootstrapEditor()
        {
            ToolboxRegistry.RegisterDefaults();
            _pluginLoadResults.AddRange(VisionFlowPluginLoader.LoadFromDefaultDirectory());
        }

        private void BuildToolbox()
        {
            ToolboxPanel.Children.Clear();
            foreach (IGrouping<string, ToolboxItem> group in ToolboxRegistry.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
            {
                var grid = new UniformGrid
                {
                    Columns = 4,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                foreach (ToolboxItem item in group.OrderBy(i => i.DisplayName))
                {
                    grid.Children.Add(CreateToolButton(item));
                }

                var expander = new Expander
                {
                    Header = group.Key,
                    Content = grid,
                    Style = (Style)FindResource("ToolboxExpanderStyle")
                };
                ToolboxPanel.Children.Add(expander);
            }
        }

        private Button CreateToolButton(ToolboxItem item)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(GetToolIconData(item)),
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stroke = GetToolIconStroke(item),
                Fill = GetToolIconFill(item)
            };
            var viewbox = new Viewbox
            {
                Width = 24,
                Height = 24,
                Child = path
            };
            var button = new Button
            {
                Content = viewbox,
                Tag = item,
                ToolTip = "单击或双击添加：" + item.DisplayName,
                Style = (Style)FindResource("ToolTileButtonStyle")
            };
            button.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1)
                {
                    AddToolboxNode(item.Id);
                    e.Handled = true;
                }
            };
            return button;
        }

        private System.Windows.Media.Brush GetToolIconFill(ToolboxItem item)
        {
            string key = (item.Id + " " + item.DisplayName + " " + item.Category).ToLowerInvariant();
            if (key.Contains("output") || key.Contains("输出"))
            {
                return (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush");
            }
            return Brushes.Transparent;
        }

        private System.Windows.Media.Brush GetToolIconStroke(ToolboxItem item)
        {
            string key = (item.Id + " " + item.DisplayName + " " + item.Category).ToLowerInvariant();
            if (key.Contains("gray-match") || key.Contains("灰度匹配"))
            {
                return (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
            }
            if (key.Contains("scaled-shape-match") || key.Contains("缩放形状匹配") || key.Contains("缩放匹配"))
            {
                return (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush");
            }
            if (key.Contains("deformable-match") || key.Contains("局部变形匹配") || key.Contains("变形匹配"))
            {
                return (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
            }
            return (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush");
        }

        private static string GetToolIconData(ToolboxItem item)
        {
            string key = (item.Id + " " + item.DisplayName + " " + item.Category).ToLowerInvariant();

            if (key.Contains("loadimage") || key.Contains("图像加载") || key.Contains("图像采集"))
            {
                return "M3,5 L21,5 L21,19 L3,19 Z M5,17 L10,12 L13,15 L15,13 L20,18 M16,8 L18,8 L18,10 L16,10 Z";
            }
            if (key.Contains("mean") || key.Contains("均值"))
            {
                return "M4,5 L20,5 L20,19 L4,19 Z M4,10 L20,10 M4,15 L20,15 M9,5 L9,19 M15,5 L15,19";
            }
            if (key.Contains("add-sub") || key.Contains("加减"))
            {
                return "M5,7 L11,7 M8,4 L8,10 M14,7 L20,7 M5,17 L11,17 M8,14 L8,20 M14,17 L20,17";
            }
            if (key.Contains("channel") || key.Contains("compose") || key.Contains("color") || key.Contains("通道") || key.Contains("三通道") || key.Contains("色彩"))
            {
                return "M8,7 C10,5 13,6 13,9 C13,12 10,13 8,11 C6,13 3,12 3,9 C3,6 6,5 8,7 M16,7 C18,5 21,6 21,9 C21,12 18,13 16,11 C14,13 11,12 11,9 C11,6 14,5 16,7 M12,15 C14,13 17,14 17,17 C17,20 14,21 12,19 C10,21 7,20 7,17 C7,14 10,13 12,15";
            }
            if (key.Contains("gray-match") || key.Contains("灰度匹配"))
            {
                return "M5,5 L19,5 L19,19 L5,19 Z M8,8 L16,8 M8,12 L16,12 M8,16 L16,16 M12,3 L12,6 M12,18 L12,21 M3,12 L6,12 M18,12 L21,12";
            }
            if (key.Contains("scaled-shape-match") || key.Contains("缩放形状匹配") || key.Contains("缩放匹配"))
            {
                return "M12,6 C15.3,6 18,8.7 18,12 C18,15.3 15.3,18 12,18 C8.7,18 6,15.3 6,12 C6,8.7 8.7,6 12,6 M9,12 L15,12 M12,9 L12,15 M4,4 L9,4 M4,4 L4,9 M4,4 L10,10 M20,20 L15,20 M20,20 L20,15 M20,20 L14,14";
            }
            if (key.Contains("deformable-match") || key.Contains("局部变形匹配") || key.Contains("变形匹配"))
            {
                return "M12,4 L12,7 M12,17 L12,20 M4,12 L7,12 M17,12 L20,12 M7,9 C9,6 14,7 15,10 C17,10 18,12 17,14 C16,17 11,18 9,15 C7,15 6,12 7,9 M9,12 C10.5,10.5 13.5,13.5 15,12";
            }
            if (key.Contains("match") || key.Contains("pose") || key.Contains("匹配") || key.Contains("定位"))
            {
                return "M6,6 L18,6 L18,18 L6,18 Z M12,3 L12,7 M12,17 L12,21 M3,12 L7,12 M17,12 L21,12 M10,12 L14,12 M12,10 L12,14";
            }
            if (key.Contains("threshold") || key.Contains("二值化"))
            {
                return "M4,5 L20,5 L20,19 L4,19 Z M12,5 L12,19 M7,9 L10,9 M7,12 L10,12 M7,15 L10,15 M15,9 L18,9 M15,12 L18,12 M15,15 L18,15";
            }
            if (key.Contains("difference") || key.Contains("相减"))
            {
                return "M9,7 C12,4 17,6 17,11 C17,16 12,18 9,15 C6,18 2,16 2,11 C2,6 6,4 9,7 M13,7 C16,4 21,6 21,11 C21,16 16,18 13,15 M14,11 L20,11";
            }
            if (key.Contains("union") || key.Contains("合并"))
            {
                return "M9,7 C12,4 17,6 17,11 C17,16 12,18 9,15 C6,18 2,16 2,11 C2,6 6,4 9,7 M15,7 C18,4 22,6 22,11 C22,16 18,18 15,15 M12,11 L18,11 M15,8 L15,14";
            }
            if (key.Contains("morphology-rect") || key.Contains("矩形形态"))
            {
                return "M6,7 L18,7 L18,17 L6,17 Z M3,12 L6,12 M18,12 L21,12 M12,4 L12,7 M12,17 L12,20";
            }
            if (key.Contains("morphology-circle") || key.Contains("圆形形态"))
            {
                return "M12,6 C15,6 18,9 18,12 C18,15 15,18 12,18 C9,18 6,15 6,12 C6,9 9,6 12,6 M3,12 L6,12 M18,12 L21,12 M12,3 L12,6 M12,18 L12,21";
            }
            if (key.Contains("region") || key.Contains("区域"))
            {
                return "M8,6 C11,3 17,5 18,9 C22,10 22,16 18,17 C16,21 9,21 8,17 C4,17 2,12 5,10 C4,8 6,6 8,6";
            }
            if (key.Contains("feature") || key.Contains("特征"))
            {
                return "M4,20 L20,20 M6,17 L6,12 M11,17 L11,7 M16,17 L16,10 M5,5 L19,5";
            }
            if (key.Contains("contour") || key.Contains("xld") || key.Contains("边缘") || key.Contains("轮廓"))
            {
                return "M3,16 C6,7 10,20 14,9 C16,4 19,7 21,12 M4,20 L20,20";
            }
            if (key.Contains("segment") || key.Contains("分割"))
            {
                return "M4,8 L9,8 M11,8 L16,8 M18,8 L21,8 M4,16 L10,16 M12,16 L18,16";
            }
            if (key.Contains("select") || key.Contains("筛选") || key.Contains("选择"))
            {
                return "M4,5 L20,5 L14,12 L14,19 L10,17 L10,12 Z";
            }
            if (key.Contains("caliper") || key.Contains("卡尺"))
            {
                return "M5,5 L19,19 M8,5 L5,8 M11,8 L8,11 M14,11 L11,14 M17,14 L14,17 M19,5 L19,10 M5,19 L10,19";
            }
            if (key.Contains("ellipse") || key.Contains("椭圆"))
            {
                return "M4,12 C4,7 8,5 12,5 C16,5 20,7 20,12 C20,17 16,19 12,19 C8,19 4,17 4,12 M8,12 L16,12";
            }
            if (key.Contains("rectangle") || key.Contains("矩形测量"))
            {
                return "M5,6 L19,6 L19,18 L5,18 Z M8,4 L8,8 M16,16 L16,20";
            }
            if (key.Contains("circle") || key.Contains("圆形测量") || key.Contains("拟合圆"))
            {
                return "M12,4 C16,4 20,8 20,12 C20,16 16,20 12,20 C8,20 4,16 4,12 C4,8 8,4 12,4 M12,12 L16,12";
            }
            if (key.Contains("fit-line") || key.Contains("直线") || key.Contains("拟合直线"))
            {
                return "M4,18 L20,6 M6,16 L6,16 M10,13 L10,13 M14,10 L14,10 M18,7 L18,7";
            }
            if (key.Contains("intersection") || key.Contains("交点"))
            {
                return "M5,5 L19,19 M19,5 L5,19 M10,12 L14,12 M12,10 L12,14";
            }
            if (key.Contains("affine") || key.Contains("坐标"))
            {
                return "M5,19 L5,5 M5,19 L19,19 M5,5 L8,8 M5,5 L2,8 M19,19 L16,16 M19,19 L16,22 M8,16 L16,8";
            }
            if (key.Contains("ifelse") || key.Contains("条件"))
            {
                return "M12,3 L21,12 L12,21 L3,12 Z M8,12 L11,15 L17,9";
            }
            if (key.Contains("for") || key.Contains("循环"))
            {
                return "M7,8 C10,4 17,5 19,10 M19,10 L19,5 M19,10 L14,10 M17,16 C14,20 7,19 5,14 M5,14 L5,19 M5,14 L10,14";
            }
            if (key.Contains("output") || key.Contains("输出"))
            {
                return "M4,6 L14,6 L20,12 L14,18 L4,18 Z M9,12 L19,12 M15,9 L19,12 L15,15";
            }
            if (key.Contains("barcode") || key.Contains("码"))
            {
                return "M5,5 L5,19 M8,5 L8,19 M12,5 L12,19 M14,5 L14,19 M19,5 L19,19";
            }
            return "M4,4 L20,4 L20,20 L4,20 Z M8,8 L16,8 M8,12 L16,12 M8,16 L13,16";
        }

        private void RefreshFlowTree()
        {
            _flowItemsByNodeId.Clear();
            FlowTree.Items.Clear();
            TreeViewItem root = CreateFlowItem(_model.Root);
            root.IsExpanded = true;
            FlowTree.Items.Add(root);
            BuildParameterPanel(GetSelectedFlowNode());
        }

        private TreeViewItem CreateFlowItem(FlowNode node)
        {
            var item = new TreeViewItem { Header = GetNodeHeader(node), Tag = node, IsExpanded = true };
            _flowItemsByNodeId[node.Id] = item;

            if (node is SequenceNode sequence)
            {
                foreach (FlowNode child in sequence.Children)
                {
                    item.Items.Add(CreateFlowItem(child));
                }
            }
            else if (node is IfElseNode ifElse)
            {
                var ifItem = new TreeViewItem { Header = "If 分支", Tag = new BranchRef { Node = ifElse, Branch = IfBranch.If }, IsExpanded = true };
                foreach (FlowNode child in ifElse.IfBranch)
                {
                    ifItem.Items.Add(CreateFlowItem(child));
                }
                var elseItem = new TreeViewItem { Header = "Else 分支", Tag = new BranchRef { Node = ifElse, Branch = IfBranch.Else }, IsExpanded = true };
                foreach (FlowNode child in ifElse.ElseBranch)
                {
                    elseItem.Items.Add(CreateFlowItem(child));
                }
                item.Items.Add(ifItem);
                item.Items.Add(elseItem);
            }
            else if (node is ForLoopNode loop)
            {
                foreach (FlowNode child in loop.Body)
                {
                    item.Items.Add(CreateFlowItem(child));
                }
            }

            return item;
        }

        private static string GetNodeHeader(FlowNode node)
        {
            if (node is SequenceNode)
            {
                return "顺序: " + node.Name;
            }
            if (node is ToolNode toolNode)
            {
                return "工具: " + toolNode.Tool.ModuleName;
            }
            if (node is IfElseNode)
            {
                return "IfElse: " + node.Name;
            }
            if (node is ForLoopNode loop)
            {
                return "For(" + (loop.Mode == ForLoopMode.Count ? "次数" : "集合") + "): " + node.Name;
            }
            if (node is FlowOutputNode)
            {
                return "输出: " + node.Name;
            }
            return node.Name;
        }

        private void BuildParameterPanel(FlowNode node)
        {
            ParameterPanel.Children.Clear();
            if (node == null)
            {
                AddInfo("请选择流程节点。");
                return;
            }

            AddTextRow("节点名称", node.Name, value =>
            {
                node.Name = value;
                RefreshFlowTree();
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
                RefreshFlowTree();
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

            AddButtonRow("打开专用编辑窗体", () => OpenToolEditor(node));
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
                SetStatus("条件已更新");
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
                    SetStatus("循环次数已更新");
                });
            }
            else
            {
                ComboBox items = AddComboRow("集合来源", RefCandidateService.Collections(_model.Root, node).Select(c => c.Path), node.ItemsPath, null);
                AddButtonRow("应用集合", () =>
                {
                    node.ItemsPath = items.Text;
                    SetStatus("循环集合已更新");
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
            if (input.ExpectedType == typeof(HalconImage) && _inputImage != null && _inputImage.IsInitialized())
            {
                values.Add("Input.Image");
            }
            values.AddRange(RefCandidateService.ForInput(_model.Root, node, input.ExpectedType).Select(c => c.Path));

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
                    Margin = new Thickness(0, 4, 0, 4)
                };
                check.Checked += (s, e) => property.SetValue(tool, true);
                check.Unchecked += (s, e) => property.SetValue(tool, false);
                ParameterPanel.Children.Add(check);
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
                Margin = new Thickness(0, 0, 0, 8)
            };
            input.ValueChanged += (s, e) =>
            {
                try
                {
                    commit(input.Value);
                }
                catch (Exception ex)
                {
                    ShowWarning("参数错误", ex.Message);
                }
            };
            ParameterPanel.Children.Add(input);
            return input;
        }

        private void AddSection(string text)
        {
            ParameterPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 6)
            });
        }

        private void AddInfo(string text)
        {
            ParameterPanel.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, 4, 0, 4)
            });
        }

        private TextBox AddTextRow(string label, string value, Action<string> commit)
        {
            AddLabel(label);
            var textBox = new TextBox { Text = value ?? string.Empty, Margin = new Thickness(0, 0, 0, 8) };
            if (commit != null)
            {
                textBox.LostFocus += (s, e) => CommitText(textBox, commit);
                textBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        CommitText(textBox, commit);
                        e.Handled = true;
                    }
                };
            }
            ParameterPanel.Children.Add(textBox);
            return textBox;
        }

        private ComboBox AddComboRow(string label, IEnumerable<string> values, string selected, Action<string> commit)
        {
            AddLabel(label);
            var combo = new ComboBox
            {
                IsEditable = true,
                Margin = new Thickness(0, 0, 0, 8)
            };
            foreach (string value in values ?? Enumerable.Empty<string>())
            {
                combo.Items.Add(value);
            }
            combo.Text = selected ?? string.Empty;
            if (commit != null)
            {
                combo.LostFocus += (s, e) => commit(combo.Text);
                combo.SelectionChanged += (s, e) =>
                {
                    if (combo.SelectedItem != null)
                    {
                        commit(combo.SelectedItem.ToString());
                    }
                };
            }
            ParameterPanel.Children.Add(combo);
            return combo;
        }

        private void AddButtonRow(string text, Action click)
        {
            var button = new Button
            {
                Content = text,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };
            button.Click += (s, e) => click();
            ParameterPanel.Children.Add(button);
        }

        private void AddLabel(string label)
        {
            ParameterPanel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, 4, 0, 3)
            });
        }

        private void CommitText(TextBox textBox, Action<string> commit)
        {
            try
            {
                commit(textBox.Text);
            }
            catch (Exception ex)
            {
                ShowWarning("参数错误", ex.Message);
            }
        }

        private void OpenToolEditor(ToolNode node)
        {
            var context = new ToolEditContext
            {
                Root = _model.Root,
                Node = node,
                InputImage = _inputImage,
                InputImagePath = _inputImagePath,
                LastRunContext = _lastRunContext
            };

            if (node.Tool is IHalconTemplateMatchTool matchTool)
            {
                var window = new WpfMatchToolEditWindow(matchTool, context)
                {
                    Owner = this
                };
                if (window.ShowDialog() == true)
                {
                    node.Name = matchTool.ModuleName;
                    RefreshFlowTree();
                }
                return;
            }

            if (node.Tool is LoadImageTool loadImageTool)
            {
                var window = new WpfLoadImageToolEditWindow(loadImageTool)
                {
                    Owner = this
                };
                if (window.ShowDialog() == true)
                {
                    node.Name = loadImageTool.ModuleName;
                    RefreshFlowTree();
                }
                return;
            }

            if (node.Tool is FollowMeasureToolBase followMeasureTool)
            {
                var window = new WpfFollowMeasureToolEditWindow(followMeasureTool, context)
                {
                    Owner = this
                };
                if (window.ShowDialog() == true)
                {
                    node.Name = followMeasureTool.ModuleName;
                    RefreshFlowTree();
                }
                return;
            }

            if (node.Tool is EllipseFollowMeasureTool ellipseMeasureTool)
            {
                var window = new WpfEllipseMeasureToolEditWindow(ellipseMeasureTool, context)
                {
                    Owner = this
                };
                if (window.ShowDialog() == true)
                {
                    node.Name = ellipseMeasureTool.ModuleName;
                    RefreshFlowTree();
                }
                return;
            }

            Window genericWindow = IsVisualPreviewTool(node.Tool)
                ? new WpfVisualToolEditWindow(node.Tool, context)
                : new WpfGenericToolEditWindow(node.Tool, context);
            genericWindow.Owner = this;
            if (genericWindow.ShowDialog() == true)
            {
                node.Name = node.Tool.ModuleName;
                RefreshFlowTree();
            }
        }

        private static bool IsVisualPreviewTool(ToolBase tool)
        {
            return tool is MeanImageTool
                || tool is AddSubImageTool
                || tool is DecomposeChannelsTool
                || tool is Compose3ImageTool
                || tool is TransColorSpaceTool
                || tool is ThresholdTool
                || tool is AutoThresholdTool
                || tool is BinaryThresholdTool
                || tool is FastThresholdTool
                || tool is CharThresholdTool
                || tool is VarThresholdTool
                || tool is RegionProcessTool
                || tool is RegionDifferenceTool
                || tool is RegionUnion2Tool
                || tool is RegionShapeTransTool
                || tool is RegionUnion1Tool
                || tool is MorphologyRectTool
                || tool is MorphologyCircleTool
                || tool is RegionFeaturesTool
                || tool is SelectRegionTool
                || tool is RegionPoseTool
                || tool is XldToolBase
                || tool is XldFeaturesTool
                || tool is FitLineTool
                || tool is FitCircleTool
                || tool is IntersectionLinesTool
                || tool is AffinePointTool;
        }

        private void AddToolboxNode(string toolboxId)
        {
            FlowNode selected = GetSelectedFlowNode();
            IfBranch branch = IfBranch.If;
            object tag = (FlowTree.SelectedItem as TreeViewItem)?.Tag;
            if (tag is BranchRef branchRef)
            {
                selected = branchRef.Node;
                branch = branchRef.Branch;
            }

            FlowNode added = _model.AddNode(toolboxId, selected, branch);
            EnsureUniqueNewNodeName(added);
            RefreshFlowTree();
            SelectNode(added.Id);
            SetStatus("已添加节点：" + added.Name);
        }

        private void EnsureUniqueNewNodeName(FlowNode added)
        {
            if (!(added is ToolNode toolNode))
            {
                return;
            }

            string baseName = ModuleNameBase(toolNode.Tool.ModuleName);
            string unique = NextAvailableModuleName(baseName, added);
            toolNode.Tool.ModuleName = unique;
            toolNode.Name = unique;
        }

        private string NextAvailableModuleName(string baseName, FlowNode exclude)
        {
            HashSet<string> names = ExistingNodeNames(exclude);
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

        private HashSet<string> ExistingNodeNames(FlowNode exclude)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FlowNode node in EnumerateNodes(_model.Root))
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

        private static IEnumerable<FlowNode> EnumerateNodes(FlowNode node)
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

        private static string ModuleNameBase(string moduleName)
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

        private void FlowTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            BuildParameterPanel(GetSelectedFlowNode());
        }

        private void FlowTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GetSelectedFlowNode() is ToolNode node)
            {
                OpenToolEditor(node);
            }
        }

        private FlowNode GetSelectedFlowNode()
        {
            object tag = (FlowTree.SelectedItem as TreeViewItem)?.Tag;
            if (tag is FlowNode node)
            {
                return node;
            }
            if (tag is BranchRef branchRef)
            {
                return branchRef.Node;
            }
            return _model.Root;
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(-1);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(1);
        }

        private void MoveSelected(int delta)
        {
            FlowNode node = GetSelectedFlowNode();
            if (_model.MoveNode(node, delta))
            {
                RefreshFlowTree();
                SelectNode(node.Id);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            FlowNode node = GetSelectedFlowNode();
            if (node == _model.Root)
            {
                return;
            }
            if (_model.RemoveNode(node))
            {
                RefreshFlowTree();
            }
        }

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            HOperatorSet.ReadImage(out HObject image, dialog.FileName);
            _inputImage?.Dispose();
            _inputImage = image;
            _inputImagePath = dialog.FileName;
            ImageView.ShowImage(_inputImage);
            ImageView.ClearOverlay();
            OutputDisplayCombo.SelectedItem = null;
            SetStatus("已打开图像：" + dialog.FileName);
        }

        private void SaveFlow_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "VisionFlow 流程|*.vflow.json|JSON 文件|*.json|所有文件|*.*",
                FileName = "flow.vflow.json",
                DefaultExt = "vflow.json",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string fileName = NormalizeFlowFileName(dialog.FileName);
            File.WriteAllText(fileName, FlowSerializer.Save(_model.Root));
            SetStatus("已保存流程：" + fileName);
        }

        private void LoadFlow_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "VisionFlow 流程|*.vflow.json;*.json|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            SequenceNode root = FlowSerializer.Load(File.ReadAllText(dialog.FileName));
            _model.ReplaceRoot(root);
            RefreshFlowTree();
            SetStatus("已加载流程：" + dialog.FileName);

            FlowValidationResult validation = FlowValidator.Validate(_model.Root);
            if (!validation.IsValid)
            {
                ShowValidationResult(validation, "流程已加载，但校验发现问题。");
            }
        }

        private void Validate_Click(object sender, RoutedEventArgs e)
        {
            FlowValidationResult validation = FlowValidator.Validate(_model.Root);
            ShowValidationResult(validation, validation.IsValid ? "流程校验通过。" : "流程校验发现问题。");
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            await RunFlowAsync();
        }

        private async Task RunFlowAsync()
        {
            if (_runCts != null)
            {
                return;
            }

            FlowValidationResult validation = FlowValidator.Validate(_model.Root);
            if (!validation.IsValid)
            {
                ShowValidationResult(validation, "流程校验失败，已阻止运行。");
                return;
            }

            _runCts = new CancellationTokenSource();
            SetRunningState(true);
            FlowRunResult result = null;
            try
            {
                var context = new FlowContext();
                if (_inputImage != null && _inputImage.IsInitialized())
                {
                    context.SetVariable(Variable.Object("Input", "Image", new HalconImage(_inputImage), 1));
                    context.AddLog(FlowLogLevel.Info, string.IsNullOrEmpty(_inputImagePath)
                        ? "[输入] Input.Image"
                        : "[输入] Input.Image = " + _inputImagePath);
                }
                result = await _runtime.RunAsync(_model.Root, context, _runCts.Token, new Progress<FlowProgress>(OnProgress));
            }
            catch (OperationCanceledException)
            {
                SetStatus("已取消");
            }
            catch (Exception ex)
            {
                ShowWarning("运行异常", "流程运行异常：" + ex.Message);
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                SetRunningState(false);
            }

            if (result == null)
            {
                return;
            }

            _lastRunContext = result.Context;
            FillVariables(result.Context);
            FillLog(result.Context);
            FillDisplayOutputs(result.Context);
            DisplaySelectedOutput();
            if (!result.IsSuccess && result.Status != NodeStatus.Skipped)
            {
                if (!string.IsNullOrWhiteSpace(result.FailedNodeId))
                {
                    SelectNode(result.FailedNodeId);
                }
                ShowWarning("运行结果", $"流程失败：{result.Message}\r\n错误码：{result.ErrorCode ?? "UNKNOWN"}\r\n节点：{result.FailedNodeName ?? "-"}");
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _runCts?.Cancel();
            SetStatus("正在停止...");
            StopButton.IsEnabled = false;
        }

        private void OutputDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DisplaySelectedOutput();
        }

        private void ShowInputImage_Click(object sender, RoutedEventArgs e)
        {
            OutputDisplayCombo.SelectedItem = null;
            DisplayCurrentImage();
            SetStatus("正在显示输入图像。");
        }

        private void DisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            var drawMode = new ComboBox { IsEditable = false, Margin = new Thickness(0, 0, 0, 12) };
            drawMode.Items.Add("margin");
            drawMode.Items.Add("fill");
            drawMode.Text = _overlayDrawMode;

            var opacity = new Slider { Minimum = 0.05, Maximum = 1.0, Value = _overlayFillOpacity, TickFrequency = 0.05, IsSnapToTickEnabled = false };
            var opacityValue = new TextBlock { Text = FormatPercent(opacity.Value), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            opacity.ValueChanged += (s, args) => opacityValue.Text = FormatPercent(opacity.Value);

            var lineWidth = new Slider { Minimum = 1, Maximum = 8, Value = _overlayLineWidth, TickFrequency = 0.5, IsSnapToTickEnabled = false };
            var lineWidthValue = new TextBlock { Text = lineWidth.Value.ToString("F1", CultureInfo.CurrentCulture), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            lineWidth.ValueChanged += (s, args) => lineWidthValue.Text = lineWidth.Value.ToString("F1", CultureInfo.CurrentCulture);

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = "叠加显示方式", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(drawMode);
            panel.Children.Add(new TextBlock { Text = "Fill 透明度", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
            panel.Children.Add(new DockPanel { LastChildFill = true, Children = { opacityValue, opacity } });
            panel.Children.Add(new TextBlock { Text = "线宽", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(new DockPanel { LastChildFill = true, Children = { lineWidthValue, lineWidth } });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var ok = new Button { Content = "确定", MinWidth = 96, Style = (Style)FindResource("AccentButtonStyle") };
            var cancel = new Button { Content = "取消", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            var window = new Window
            {
                Title = "系统显示设置",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 360,
                Height = 300,
                ResizeMode = ResizeMode.NoResize,
                Content = panel
            };
            ok.Click += (s, args) =>
            {
                _overlayDrawMode = drawMode.Text;
                _overlayFillOpacity = opacity.Value;
                _overlayLineWidth = lineWidth.Value;
                ApplyDisplaySettings();
                SaveDisplaySettings();
                DisplaySelectedOutput();
                window.DialogResult = true;
            };
            cancel.Click += (s, args) => window.DialogResult = false;
            window.ShowDialog();
        }

        private void ApplyDisplaySettings()
        {
            if (ImageView == null)
            {
                return;
            }
            ImageView.OverlayDrawMode = _overlayDrawMode;
            ImageView.OverlayFillOpacity = _overlayFillOpacity;
            ImageView.OverlayLineWidth = _overlayLineWidth;
        }

        private void LoadDisplaySettings()
        {
            string path = DisplaySettingsPath();
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                DisplaySettingsModel settings = JsonSerializer.Deserialize<DisplaySettingsModel>(File.ReadAllText(path));
                if (settings == null)
                {
                    return;
                }
                _overlayDrawMode = string.Equals(settings.OverlayDrawMode, "fill", StringComparison.OrdinalIgnoreCase) ? "fill" : "margin";
                _overlayFillOpacity = Math.Max(0.0, Math.Min(1.0, settings.OverlayFillOpacity));
                _overlayLineWidth = Math.Max(1.0, settings.OverlayLineWidth);
            }
            catch (IOException ex)
            {
                MessageBox.Show(this, "读取显示设置失败：" + ex.Message, "系统显示设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageBox.Show(this, "读取显示设置失败：" + ex.Message, "系统显示设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (JsonException ex)
            {
                MessageBox.Show(this, "显示设置文件格式错误：" + ex.Message, "系统显示设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveDisplaySettings()
        {
            string path = DisplaySettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var settings = new DisplaySettingsModel
            {
                OverlayDrawMode = _overlayDrawMode,
                OverlayFillOpacity = _overlayFillOpacity,
                OverlayLineWidth = _overlayLineWidth
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string DisplaySettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VisionFlow", "wpf-display-settings.json");
        }

        private static string FormatPercent(double value)
        {
            return value.ToString("P0", CultureInfo.CurrentCulture);
        }

        private void OnProgress(FlowProgress progress)
        {
            if (!string.IsNullOrWhiteSpace(progress.NodeId))
            {
                SelectNode(progress.NodeId);
            }

            switch (progress.Kind)
            {
                case FlowProgressKind.NodeStarted:
                    SetStatus("正在执行：" + progress.NodeName);
                    break;
                case FlowProgressKind.NodeCompleted:
                    SetStatus($"{progress.NodeName} 完成（{progress.Duration?.TotalMilliseconds:F0} ms）");
                    break;
                case FlowProgressKind.NodeFailed:
                    SetStatus(progress.NodeName + " 失败");
                    break;
                case FlowProgressKind.FlowCompleted:
                    SetStatus(progress.Status == NodeStatus.Success
                        ? $"运行完成（{progress.Duration?.TotalMilliseconds:F0} ms）"
                        : $"运行失败（{progress.Duration?.TotalMilliseconds:F0} ms）");
                    break;
                case FlowProgressKind.FlowCancelled:
                    SetStatus("已取消");
                    break;
            }
        }

        private void FillVariables(FlowContext context)
        {
            VariablesGrid.ItemsSource = context.GetAllVariables()
                .OrderBy(v => v.ModuleName)
                .ThenBy(v => v.Name)
                .Select(v => new VariableRow
                {
                    Path = v.ModuleName + "." + v.Name,
                    Type = v.Kind + "/" + v.Type,
                    Count = v.Count,
                    Value = FormatValue(v.Value)
                })
                .ToList();
        }

        private void FillLog(FlowContext context)
        {
            LogGrid.ItemsSource = context.StructuredLogs.Select(l => new LogRow
            {
                Level = l.Level.ToString(),
                NodeName = l.NodeName,
                Message = l.Message
            }).ToList();
        }

        private void FillDisplayOutputs(FlowContext context)
        {
            var rows = context.GetAllVariables()
                .Where(IsDisplayableVariable)
                .OrderBy(v => string.Equals(v.ModuleName, "Input", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => v.ModuleName)
                .ThenBy(v => v.Name)
                .Select(v => new DisplayOutputRow
                {
                    Path = v.ModuleName + "." + v.Name,
                    Variable = v
                })
                .ToList();
            OutputDisplayCombo.ItemsSource = rows;
            OutputDisplayCombo.SelectedItem = rows.LastOrDefault(r => !string.Equals(r.Variable.ModuleName, "Input", StringComparison.OrdinalIgnoreCase))
                ?? rows.FirstOrDefault();
        }

        private void DisplayCurrentImage()
        {
            if (_inputImage != null && _inputImage.IsInitialized())
            {
                ImageView.ShowImage(_inputImage);
                ImageView.ClearOverlay();
            }
        }

        private void DisplaySelectedOutput()
        {
            if (_lastRunContext == null || !(OutputDisplayCombo.SelectedItem is DisplayOutputRow row))
            {
                DisplayCurrentImage();
                return;
            }

            HObject overlay = null;
            try
            {
                HObject imageToShow = ResolveDisplayBaseImage(_lastRunContext, row.Variable);
                if (imageToShow != null && imageToShow.IsInitialized())
                {
                    ImageView.ShowImage(imageToShow);
                }
                else
                {
                    DisplayCurrentImage();
                }

                overlay = BuildVariableOverlay(row.Variable);
                if (overlay != null && overlay.IsInitialized())
                {
                    ImageView.SetOverlay(overlay);
                }
                else
                {
                    ImageView.ClearOverlay();
                }
                SetStatus("正在显示：" + row.Path);
            }
            catch (Exception ex)
            {
                ShowWarning("显示失败", ex.Message);
            }
            finally
            {
                overlay?.Dispose();
            }
        }

        private static bool IsDisplayableVariable(Variable variable)
        {
            return variable.Value is HalconImage
                || variable.Value is HalconRegion
                || variable.Value is HalconXld
                || variable.Value is HObject
                || variable.Value is List<HObject>
                || variable.Value is List<LineMeasureResult>
                || variable.Value is List<RectangleMeasureResult>
                || variable.Value is List<CircleMeasureResult>
                || variable.Value is List<EllipseMeasureResult>;
        }

        private static HObject ResolveDisplayBaseImage(FlowContext context, Variable variable)
        {
            if (variable.Value is HalconImage image)
            {
                return image.Object;
            }
            if (context.TryGetVariable(variable.ModuleName, "Image", out Variable sibling)
                && sibling.Value is HalconImage siblingImage)
            {
                return siblingImage.Object;
            }
            if (context.TryGetVariable("Input", "Image", out Variable input)
                && input.Value is HalconImage inputImage)
            {
                return inputImage.Object;
            }
            return null;
        }

        private static HObject BuildVariableOverlay(Variable variable)
        {
            HOperatorSet.GenEmptyObj(out HObject overlay);
            if (variable.Value is HalconImage)
            {
                return overlay;
            }
            if (variable.Value is HalconRegion region)
            {
                AppendObject(ref overlay, region.Object);
            }
            else if (variable.Value is HalconXld xld)
            {
                AppendObject(ref overlay, xld.Object);
            }
            else if (variable.Value is HObject hObject)
            {
                AppendObject(ref overlay, hObject);
            }
            else if (variable.Value is List<HObject> objects)
            {
                foreach (HObject obj in objects)
                {
                    AppendObject(ref overlay, obj);
                }
            }
            else if (variable.Value is List<LineMeasureResult> lines)
            {
                foreach (LineMeasureResult line in lines)
                {
                    HOperatorSet.GenRegionLine(out HObject lineObject, line.Row1, line.Column1, line.Row2, line.Column2);
                    AppendAndDispose(ref overlay, lineObject);
                }
            }
            else if (variable.Value is List<RectangleMeasureResult> rectangles)
            {
                foreach (RectangleMeasureResult r in rectangles)
                {
                    HOperatorSet.GenRectangle2(out HObject rectangle, r.Row, r.Column, r.Phi, r.Length1, r.Length2);
                    AppendAndDispose(ref overlay, rectangle);
                }
            }
            else if (variable.Value is List<CircleMeasureResult> circles)
            {
                foreach (CircleMeasureResult c in circles)
                {
                    HOperatorSet.GenCircle(out HObject circle, c.Row, c.Column, c.Radius);
                    AppendAndDispose(ref overlay, circle);
                }
            }
            else if (variable.Value is List<EllipseMeasureResult> ellipses)
            {
                foreach (EllipseMeasureResult m in ellipses)
                {
                    HOperatorSet.GenEllipseContourXld(out HObject ellipse,
                        m.Row, m.Column, m.Phi, m.Length1, m.Length2, 0, Math.PI * 2.0, "positive", 1.5);
                    AppendAndDispose(ref overlay, ellipse);
                }
            }
            return overlay;
        }

        private static void AppendObject(ref HObject target, HObject obj)
        {
            if (obj == null || !obj.IsInitialized())
            {
                return;
            }
            HOperatorSet.ConcatObj(target, obj, out HObject combined);
            target.Dispose();
            target = combined;
        }

        private static void AppendAndDispose(ref HObject target, HObject obj)
        {
            try
            {
                AppendObject(ref target, obj);
            }
            finally
            {
                obj?.Dispose();
            }
        }

        private void SetRunningState(bool running)
        {
            StopButton.IsEnabled = running;
            Cursor = running ? Cursors.Wait : Cursors.Arrow;
            if (running)
            {
                SetStatus("正在运行...");
            }
        }

        private void ShowValidationResult(FlowValidationResult validation, string title)
        {
            FlowValidationIssue firstError = validation.Issues.FirstOrDefault(i => i.Severity == FlowValidationSeverity.Error);
            if (!string.IsNullOrWhiteSpace(firstError?.NodeId))
            {
                SelectNode(firstError.NodeId);
            }

            string message = validation.Issues.Count == 0
                ? title
                : title + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, validation.Issues.Take(20).Select(i => i.ToString()));
            if (validation.Issues.Count > 20)
            {
                message += Environment.NewLine + "... 还有 " + (validation.Issues.Count - 20) + " 条";
            }
            MessageBox.Show(this, message, "流程校验", MessageBoxButton.OK,
                validation.IsValid ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void SelectNode(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || !_flowItemsByNodeId.TryGetValue(nodeId, out TreeViewItem item))
            {
                return;
            }
            item.IsSelected = true;
            item.BringIntoView();
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
            TitleStatusText.Text = text;
        }

        private void ShowWarning(string title, string message)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void WriteStartupHint()
        {
            int loadedTools = _pluginLoadResults.Sum(r => r.ToolCount);
            int failed = _pluginLoadResults.Sum(r => r.Errors.Count);
            SetStatus("WPF 版编辑器已启用：左侧工具箱，中间流程和参数，右侧图像/变量/日志。"
                + (loadedTools > 0 ? $" 已加载插件工具 {loadedTools} 个。" : string.Empty)
                + (failed > 0 ? $" 插件加载错误 {failed} 条。" : string.Empty));
        }

        private static IEnumerable<PropertyInfo> SerializableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(ToolBase.ModuleName)
                    && (p.PropertyType == typeof(string)
                        || p.PropertyType == typeof(int)
                        || p.PropertyType == typeof(double)
                        || p.PropertyType == typeof(bool)
                        || p.PropertyType.IsEnum));
        }

        private static object ConvertText(string value, Type type)
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

        private static bool IsNumericType(Type type)
        {
            return type == typeof(int)
                || type == typeof(double);
        }

        private static object ConvertNumber(double value, Type type)
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

        private static string NormalizeFlowFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
            if (fileName.EndsWith(".vflow.vflow.json", StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(0, fileName.Length - ".vflow.vflow.json".Length) + ".vflow.json";
            }
            if (fileName.EndsWith(".vflow", StringComparison.OrdinalIgnoreCase))
            {
                return fileName + ".json";
            }
            return fileName;
        }

        private static Operand ParseOperand(string text)
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

        private static string OperandText(Operand operand)
        {
            if (operand == null)
            {
                return string.Empty;
            }
            return operand.IsConstant ? Convert.ToString(operand.ConstantValue, CultureInfo.CurrentCulture) : "ref:" + operand.Reference;
        }

        private static string FormatValue(object value)
        {
            if (value == null)
            {
                return "null";
            }
            if (value is Array array)
            {
                return "Array[" + array.Length + "]";
            }
            return value.ToString();
        }
    }
}
