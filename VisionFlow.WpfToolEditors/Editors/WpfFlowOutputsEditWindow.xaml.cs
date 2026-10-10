using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 流程输出节点的输出表编辑窗口：每行一个输出（名称、形态、类型、取值）。
    /// 修改只作用于 <see cref="FlowOutputsEditor"/> 的草稿（深拷贝自节点），确定后由调用方执行 <see cref="FlowOutputsEditor.Apply"/>。
    /// </summary>
    public partial class WpfFlowOutputsEditWindow : Window
    {
        private static readonly VariableType[] Types = { VariableType.String, VariableType.Int, VariableType.Double, VariableType.Bool };

        private readonly FlowOutputsEditor _editor;
        private readonly IReadOnlyList<string> _candidates;

        public WpfFlowOutputsEditWindow(FlowOutputsEditor editor, FlowNode root)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _candidates = editor.Candidates(root);
            InitializeComponent();
            Title = "流程输出 - " + _editor.Node.Name;
            RenderHeaderRow();
            RenderHeader();
            Render();
        }

        private void RenderHeader()
        {
            HeaderText.Text = $"{_editor.Node.Name} 的流程输出（{_editor.Rows.Count} 条）";
        }

        private void Render()
        {
            RowsPanel.Children.Clear();
            if (_editor.Rows.Count == 0)
            {
                RowsPanel.Children.Add(new TextBlock
                {
                    Text = "还没有流程输出，点“添加输出”新增。",
                    Margin = new Thickness(4, 8, 0, 0)
                });
            }
            for (int i = 0; i < _editor.Rows.Count; i++)
            {
                RowsPanel.Children.Add(CreateCard(_editor.Rows[i]));
            }
        }

        private Border CreateCard(FlowOutputRow row)
        {
            var grid = new Grid();
            AddColumns(grid);

            var name = new TextBox { Text = row.Name, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
            name.TextChanged += (s, e) => row.Name = name.Text;
            AddCell(grid, 0, 0, name);

            var kind = new ComboBox { Height = 32 };
            kind.Items.Add("单值");
            kind.Items.Add("数组");
            kind.SelectedIndex = row.Kind == VariableKind.Array ? 1 : 0;
            kind.SelectionChanged += (s, e) =>
                row.Kind = kind.SelectedIndex == 1 ? VariableKind.Array : VariableKind.Single;
            AddCell(grid, 0, 2, kind);

            var type = new ComboBox { Height = 32 };
            foreach (VariableType value in Types)
            {
                type.Items.Add(TypeLabel(value));
                if (value == row.Type)
                {
                    type.SelectedIndex = type.Items.Count - 1;
                }
            }
            type.SelectionChanged += (s, e) =>
            {
                if (type.SelectedIndex >= 0)
                {
                    row.Type = Types[type.SelectedIndex];
                }
            };
            AddCell(grid, 0, 4, type);

            var valueBox = new ComboBox { IsEditable = true, Height = 32, ItemsSource = _candidates, Text = row.ValueText };
            valueBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((s, e) => row.ValueText = valueBox.Text));
            valueBox.SelectionChanged += (s, e) =>
            {
                if (valueBox.SelectedItem is string selected)
                {
                    row.ValueText = selected;
                }
            };
            AddCell(grid, 0, 6, valueBox);

            var remove = new Button { Content = "删除", MinWidth = 64 };
            remove.Click += (s, e) =>
            {
                _editor.Rows.Remove(row);
                RenderHeader();
                Render();
            };
            AddCell(grid, 0, 8, remove);

            return new Border { Style = (Style)FindResource("EditorCardStyle"), Child = grid };
        }

        private static void AddColumns(Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        private static void AddCell(Grid grid, int row, int column, UIElement element)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        private static TextBlock HeaderLabel(string text)
        {
            var label = new TextBlock { Text = text, Margin = new Thickness(4, 0, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            return label;
        }

        private void RenderHeaderRow()
        {
            AddColumns(HeaderGrid);
            AddCell(HeaderGrid, 0, 0, HeaderLabel("输出名"));
            AddCell(HeaderGrid, 0, 2, HeaderLabel("形态"));
            AddCell(HeaderGrid, 0, 4, HeaderLabel("类型"));
            AddCell(HeaderGrid, 0, 6, HeaderLabel("取值（ref:模块.变量 或常量）"));
        }

        private static string TypeLabel(VariableType type)
        {
            switch (type)
            {
                case VariableType.Int:
                    return "整数";
                case VariableType.Double:
                    return "小数";
                case VariableType.Bool:
                    return "布尔";
                default:
                    return "文本";
            }
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            _editor.AddRow();
            RenderHeader();
            Render();
            StatusText.Text = "已添加输出，请填写名称和取值。";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<string> problems = _editor.Validate();
            if (problems.Count > 0)
            {
                MessageBox.Show(this, string.Join("\r\n", problems.Take(12)), "流程输出未填写完整",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
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
