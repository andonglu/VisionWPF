using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Expressions;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 子流程编辑窗口：选择文件、查看输出、编辑输入映射、按当前映射校验子流程并试运行。
    /// 修改只作用于 <see cref="SubFlowEditor"/> 草稿，确定后由调用方执行 <see cref="SubFlowEditor.Apply"/>。
    /// </summary>
    public partial class WpfSubFlowEditWindow : Window
    {
        public sealed class OutputRow
        {
            public string Name { get; set; }

            public string Path { get; set; }

            public string Type { get; set; }

            public string Value { get; set; }
        }

        private readonly SubFlowEditor _editor;
        private readonly FlowNode _root;
        private readonly FlowContext _lastRunContext;
        private readonly HObject _inputImage;
        private readonly IReadOnlyList<string> _candidates;
        private List<OutputRow> _outputs = new List<OutputRow>();

        public WpfSubFlowEditWindow(SubFlowEditor editor, FlowNode root, FlowContext lastRunContext, HObject inputImage)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _root = root;
            _lastRunContext = lastRunContext;
            _inputImage = inputImage;
            _candidates = _editor.ValueCandidates(root);

            InitializeComponent();
            Title = "子流程 - " + _editor.Node.Name;
            FlowFileText.Text = _editor.FlowFile;
            RenderInputs();
            ReloadDefinition();
            StatusText.Text = string.IsNullOrWhiteSpace(_editor.BaseDirectory)
                ? "当前流程尚未保存，相对路径相对于程序目录；建议先保存流程，再选择子流程文件。"
                : "相对路径相对于当前流程文件所在目录：" + _editor.BaseDirectory;
        }

        /// <summary>按草稿路径重新加载子流程定义，刷新路径、状态、输出列表与校验结果。</summary>
        private void ReloadDefinition()
        {
            _editor.FlowFile = (FlowFileText.Text ?? string.Empty).Trim();
            SubFlowNode draft = _editor.CreateDraft();
            string path = draft.ResolvePath();
            ResolvedPathText.Text = path == null ? string.Empty : "完整路径：" + path;

            SubFlowDefinition definition = draft.TryLoadDefinition(out string error);
            if (definition == null)
            {
                SetLoadStatus(error, false);
                _outputs = new List<OutputRow>();
            }
            else
            {
                string summary = $"已加载：{definition.Outputs.Count} 个输出";
                if (definition.Warnings != null && definition.Warnings.Count > 0)
                {
                    summary += "；" + string.Join("；", definition.Warnings);
                }
                SetLoadStatus(summary, true);
                _outputs = definition.Outputs
                    .Select(o => new OutputRow
                    {
                        Name = o.Name,
                        Path = _editor.Node.Name + "." + o.Name,
                        Type = o.Kind == VariableKind.Array ? o.Type + "[]" : o.Type.ToString()
                    })
                    .ToList();
            }
            OutputsGrid.ItemsSource = _outputs;
            RunValidation();
        }

        private void SetLoadStatus(string text, bool ok)
        {
            LoadStatusText.Text = (ok ? "✓ " : "✗ ") + text;
            LoadStatusText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        }

        private void RunValidation()
        {
            IReadOnlyList<FlowValidationIssue> issues = _editor.Validate(_root);
            IssuesList.ItemsSource = issues.Count == 0
                ? new List<string> { "✓ 没有发现问题" }
                : issues.Select(i => (i.Severity == FlowValidationSeverity.Error ? "✗ " : "⚠ ")
                    + (string.IsNullOrEmpty(i.Parameter) ? string.Empty : i.Parameter + "：") + i.Message).ToList();
        }

        private void RenderInputs()
        {
            InputsPanel.Children.Clear();
            if (_editor.Inputs.Count == 0)
            {
                InputsPanel.Children.Add(new TextBlock
                {
                    Text = "没有输入映射，子流程只使用当前流程的 Input.* 外部输入。",
                    Margin = new Thickness(4, 4, 0, 0),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush")
                });
            }
            foreach (SubFlowInputRow row in _editor.Inputs)
            {
                InputsPanel.Children.Add(CreateInputRow(row));
            }
        }

        private Grid CreateInputRow(SubFlowInputRow row)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var path = new TextBox { Text = row.InputPath, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "子流程中的输入，形如 Input.名称" };
            path.TextChanged += (s, e) => row.InputPath = path.Text;
            path.LostFocus += (s, e) => RunValidation();
            grid.Children.Add(path);

            var arrow = new TextBlock { Text = "←", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);

            var value = new ComboBox { IsEditable = true, Height = 32, ItemsSource = _candidates, Text = row.ValueText };
            value.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((s, e) => row.ValueText = value.Text));
            value.SelectionChanged += (s, e) =>
            {
                if (value.SelectedItem is string selected)
                {
                    row.ValueText = selected;
                }
            };
            value.LostFocus += (s, e) => RunValidation();
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);

            var remove = new Button { Content = "删除", MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (s, e) =>
            {
                _editor.Inputs.Remove(row);
                RenderInputs();
                RunValidation();
            };
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);
            return grid;
        }

        private void FlowFileText_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!string.Equals((FlowFileText.Text ?? string.Empty).Trim(), _editor.FlowFile, StringComparison.Ordinal))
            {
                ReloadDefinition();
            }
        }

        private void FlowFileText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ReloadDefinition();
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            string current = _editor.CreateDraft().ResolvePath();
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "VisionFlow 流程 (*.vflow.json)|*.vflow.json|JSON (*.json)|*.json|所有文件|*.*",
                InitialDirectory = current != null && System.IO.File.Exists(current)
                    ? System.IO.Path.GetDirectoryName(current)
                    : _editor.BaseDirectory ?? string.Empty
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            FlowFileText.Text = _editor.ToStoredPath(dialog.FileName);
            ReloadDefinition();
        }

        private void AddInput_Click(object sender, RoutedEventArgs e)
        {
            _editor.Inputs.Add(new SubFlowInputRow());
            RenderInputs();
            StatusText.Text = "已添加输入映射，请填写子流程输入名称（Input.名称）和取值。";
        }

        private void Validate_Click(object sender, RoutedEventArgs e)
        {
            ReloadDefinition();
        }

        private void RunPreview_Click(object sender, RoutedEventArgs e)
        {
            _editor.FlowFile = (FlowFileText.Text ?? string.Empty).Trim();
            SubFlowNode draft = _editor.CreateDraft();
            foreach (OutputRow row in _outputs)
            {
                row.Value = string.Empty;
            }

            FlowContext ctx = null;
            try
            {
                // 派生上下文：借用主窗口上次运行的变量，释放时只回收试运行写入的输出
                ctx = (_lastRunContext ?? new FlowContext()).CreatePreviewContext();
                if (_inputImage != null && _inputImage.IsInitialized())
                {
                    ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(_inputImage), 1));
                }
                NodeResult result = draft.Execute(ctx);
                foreach (OutputRow row in _outputs)
                {
                    if (ctx.TryGetVariable(draft.Name, row.Name, out Variable variable))
                    {
                        row.Value = ExpressionValues.Format(variable.Value);
                    }
                }
                StatusText.Text = result.IsSuccess
                    ? (_lastRunContext == null ? "试运行完成（尚未运行过流程，引用上游变量的映射需先运行流程）。" : "试运行完成，基于上次运行结果。")
                    : "试运行失败：" + result.Message;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is HOperatorException)
            {
                StatusText.Text = "试运行失败：" + ex.Message;
            }
            finally
            {
                ctx?.Dispose();
            }
            OutputsGrid.ItemsSource = null;
            OutputsGrid.ItemsSource = _outputs;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _editor.FlowFile = (FlowFileText.Text ?? string.Empty).Trim();
            List<FlowValidationIssue> errors = _editor.Validate(_root)
                .Where(i => i.Severity == FlowValidationSeverity.Error)
                .ToList();
            if (errors.Count > 0)
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    string.Join(Environment.NewLine, errors.Take(8).Select(i => i.Parameter + "：" + i.Message))
                        + Environment.NewLine + Environment.NewLine + "流程校验将不通过。仍然保存吗？",
                    "子流程", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
