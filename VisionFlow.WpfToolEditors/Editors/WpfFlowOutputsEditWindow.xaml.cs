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
        private static readonly VariableType[] Types = { VariableType.String, VariableType.Int, VariableType.Double, VariableType.Bool, VariableType.Object };

        private readonly FlowOutputsEditor _editor;
        private readonly IReadOnlyList<CandidateItem> _candidates;

        public WpfFlowOutputsEditWindow(FlowOutputsEditor editor, FlowNode root)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _candidates = editor.CandidateRefs(root)
                .Select(c => new CandidateItem(c))
                .ToList();
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

            var objectType = new ComboBox { Height = 32, Visibility = ObjectTypeVisibility(row.Type) };
            foreach (FlowObjectTypeOption option in FlowObjectTypeOption.Common)
            {
                objectType.Items.Add(option.Label);
            }
            objectType.Items.Add("自定义…");
            SelectObjectType(objectType, row.ClrTypeName);

            type.SelectionChanged += (s, e) =>
            {
                if (type.SelectedIndex >= 0)
                {
                    row.Type = Types[type.SelectedIndex];
                    objectType.Visibility = ObjectTypeVisibility(row.Type);
                    if (row.Type == VariableType.Object && string.IsNullOrWhiteSpace(row.ClrTypeName))
                    {
                        row.ClrTypeName = FlowObjectTypeOption.Common[0].ClrTypeName;
                        SelectObjectType(objectType, row.ClrTypeName);
                    }
                }
            };
            objectType.SelectionChanged += (s, e) =>
            {
                if (objectType.SelectedIndex < 0)
                {
                    return;
                }
                if (objectType.SelectedIndex < FlowObjectTypeOption.Common.Count)
                {
                    row.ClrTypeName = FlowObjectTypeOption.Common[objectType.SelectedIndex].ClrTypeName;
                    return;
                }
                // “自定义…”：弹输入框填完整类型名，取消则恢复原选择
                if (PromptCustomClrType(this, row.ClrTypeName, out string custom))
                {
                    row.ClrTypeName = custom;
                }
                SelectObjectType(objectType, row.ClrTypeName);
            };
            AddCell(grid, 0, 4, type);
            AddCell(grid, 0, 6, objectType);

            var valueBox = new ComboBox
            {
                IsEditable = true,
                Height = 32,
                ItemsSource = _candidates,
                ItemTemplate = CandidateTemplate(),
                Text = row.ValueText
            };
            valueBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((s, e) => row.ValueText = valueBox.Text));
            valueBox.SelectionChanged += (s, e) =>
            {
                if (valueBox.SelectedItem is CandidateItem selected)
                {
                    row.ValueText = selected.RefText;
                }
            };
            AddCell(grid, 0, 8, valueBox);

            var remove = new Button { Content = "删除", MinWidth = 64 };
            remove.Click += (s, e) =>
            {
                _editor.Rows.Remove(row);
                RenderHeader();
                Render();
            };
            AddCell(grid, 0, 10, remove);

            return new Border { Style = (Style)FindResource("EditorCardStyle"), Child = grid };
        }

        private static Visibility ObjectTypeVisibility(VariableType type)
        {
            return type == VariableType.Object ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void SelectObjectType(ComboBox objectType, string clrTypeName)
        {
            int index = -1;
            for (int i = 0; i < FlowObjectTypeOption.Common.Count; i++)
            {
                if (string.Equals(FlowObjectTypeOption.Common[i].ClrTypeName, clrTypeName, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            objectType.SelectedIndex = index >= 0 ? index : FlowObjectTypeOption.Common.Count;
        }

        /// <summary>候选下拉项：左列 ref:路径，右列灰字类型标签（类型·数组）。</summary>
        private static DataTemplate CandidateTemplate()
        {
            var dock = new FrameworkElementFactory(typeof(DockPanel));
            var tag = new FrameworkElementFactory(typeof(TextBlock));
            tag.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CandidateItem.TagLabel)));
            tag.SetValue(DockPanel.DockProperty, Dock.Right);
            tag.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"));
            tag.SetValue(TextBlock.MarginProperty, new Thickness(12, 0, 0, 0));
            var path = new FrameworkElementFactory(typeof(TextBlock));
            path.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CandidateItem.RefText)));
            dock.AppendChild(path);
            dock.AppendChild(tag);
            var template = new DataTemplate(typeof(CandidateItem)) { VisualTree = dock };
            return template;
        }

        /// <summary>“自定义…”输入框：填完整 CLR 类型名（如 Namespace.Type, Assembly）。</summary>
        private static bool PromptCustomClrType(Window owner, string current, out string typeName)
        {
            typeName = null;
            var dialog = new Window
            {
                Title = "自定义对象类型",
                Width = 480,
                Height = 180,
                MinWidth = 400,
                MinHeight = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "填写完整 CLR 类型名（命名空间.类型名, 程序集名），保存到输出定义的 ClrTypeName。" };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            grid.Children.Add(hint);

            var box = new TextBox { Text = current ?? string.Empty, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetRow(box, 2);
            grid.Children.Add(box);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", MinWidth = 88, IsDefault = true };
            if (Application.Current.TryFindResource("AccentButtonStyle") is Style accent)
            {
                ok.Style = accent;
            }
            var cancel = new Button { Content = "取消", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 4);
            grid.Children.Add(buttons);
            dialog.Content = grid;

            bool confirmed = false;
            ok.Click += (s, e) =>
            {
                confirmed = true;
                dialog.DialogResult = true;
            };
            bool? result = dialog.ShowDialog();
            if (result == true && confirmed && !string.IsNullOrWhiteSpace(box.Text))
            {
                typeName = box.Text.Trim();
                return true;
            }
            return false;
        }

        private static void AddColumns(Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
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
            AddCell(HeaderGrid, 0, 6, HeaderLabel("对象类型"));
            AddCell(HeaderGrid, 0, 8, HeaderLabel("取值（ref:模块.变量 或常量）"));
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
                case VariableType.Object:
                    return "对象";
                default:
                    return "文本";
            }
        }

        /// <summary>取值下拉的一项：左列纯 ref:路径（选中后写回文本框，OperandText 格式不变），右列类型标签仅作展示。</summary>
        private sealed class CandidateItem
        {
            public string RefText { get; }
            public string TagLabel { get; }

            public CandidateItem(RefCandidate candidate)
            {
                RefText = "ref:" + candidate.Path;
                TagLabel = FlowOutputsEditor.TypeTagLabel(candidate.ClrType, candidate.IsCollection);
            }

            public override string ToString()
            {
                return RefText;
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
