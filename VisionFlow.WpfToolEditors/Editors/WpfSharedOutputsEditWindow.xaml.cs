using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VisionFlow.Core;
using VisionFlow.Editing;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// IfElse / Switch 公共输出编辑窗口：每个输出一张卡片（名称、类型、各分支取值）。
    /// 修改只作用于 <see cref="SharedOutputsEditor"/>，确定后由调用方执行 <see cref="SharedOutputsEditor.Apply"/>。
    /// </summary>
    public partial class WpfSharedOutputsEditWindow : Window
    {
        private readonly SharedOutputsEditor _editor;
        private readonly List<IReadOnlyList<string>> _candidates;

        public WpfSharedOutputsEditWindow(SharedOutputsEditor editor, FlowNode root)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _candidates = Enumerable.Range(0, _editor.BranchNames.Count)
                .Select(i => root == null ? (IReadOnlyList<string>)new string[0] : _editor.CandidatesFor(root, i))
                .ToList();
            InitializeComponent();
            Title = "公共输出 - " + _editor.Node.Name;
            HeaderText.Text = $"{_editor.Node.Name} 的公共输出（{_editor.BranchNames.Count} 个分支）";
            Render();
        }

        private void Render()
        {
            RowsPanel.Children.Clear();
            if (_editor.Rows.Count == 0)
            {
                RowsPanel.Children.Add(new TextBlock
                {
                    Text = "还没有公共输出，点“添加输出”新增。",
                    Margin = new Thickness(4, 8, 0, 0)
                });
            }
            for (int i = 0; i < _editor.Rows.Count; i++)
            {
                RowsPanel.Children.Add(CreateCard(_editor.Rows[i]));
            }
        }

        private Border CreateCard(SharedOutputRow row)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            AddRow(grid);
            AddCell(grid, 0, 0, Label("输出名"));
            var name = new TextBox { Text = row.Name, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
            name.TextChanged += (s, e) => row.Name = name.Text;
            AddCell(grid, 0, 1, name);
            AddCell(grid, 0, 3, Label("类型"));
            var type = new ComboBox { ItemsSource = SharedOutputType.All, SelectedItem = row.OutputType, Height = 32 };
            type.SelectionChanged += (s, e) => row.OutputType = (SharedOutputType)type.SelectedItem;
            AddCell(grid, 0, 4, type);
            var remove = new Button { Content = "删除", MinWidth = 64 };
            remove.Click += (s, e) =>
            {
                _editor.Rows.Remove(row);
                Render();
            };
            AddCell(grid, 0, 6, remove);

            for (int i = 0; i < _editor.BranchNames.Count; i++)
            {
                int index = i;
                AddRow(grid);
                int gridRow = grid.RowDefinitions.Count - 1;
                AddCell(grid, gridRow, 0, Label(_editor.BranchNames[i]));
                var value = new ComboBox { IsEditable = true, Height = 32, ItemsSource = _candidates[i], Text = row.Values[i] };
                value.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                    new TextChangedEventHandler((s, e) => row.Values[index] = value.Text));
                value.SelectionChanged += (s, e) =>
                {
                    if (value.SelectedItem is string selected)
                    {
                        row.Values[index] = selected;
                    }
                };
                AddCell(grid, gridRow, 1, value, columnSpan: 6);
            }

            var card = new Border { Style = (Style)FindResource("EditorCardStyle"), Child = grid };
            return card;
        }

        private static void AddRow(Grid grid)
        {
            if (grid.RowDefinitions.Count > 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            }
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        private static void AddCell(Grid grid, int row, int column, UIElement element, int columnSpan = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, columnSpan);
            grid.Children.Add(element);
        }

        private TextBlock Label(string text)
        {
            return new TextBlock { Text = text, Style = (Style)FindResource("EditorFieldLabelStyle") };
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            _editor.AddRow();
            Render();
            StatusText.Text = "已添加输出，请填写名称、类型和每个分支的取值。";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<string> problems = _editor.Validate();
            if (problems.Count > 0)
            {
                MessageBox.Show(this, string.Join("\r\n", problems.Take(12)), "公共输出未填写完整",
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
