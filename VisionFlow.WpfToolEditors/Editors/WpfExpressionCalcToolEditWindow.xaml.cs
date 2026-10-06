using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Expressions;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>变量计算编辑窗口：表格管理多条计算式，实时检查语法与引用，可插入变量和函数并运行预览。</summary>
    public partial class WpfExpressionCalcToolEditWindow : Window
    {
        public sealed class CalcRow : INotifyPropertyChanged
        {
            private string _name = string.Empty;
            private VariableType _type = VariableType.Double;
            private string _expression = string.Empty;
            private string _status = string.Empty;
            private string _value = string.Empty;
            private bool _hasError;

            public event PropertyChangedEventHandler PropertyChanged;

            public string Name
            {
                get { return _name; }
                set { Set(ref _name, value ?? string.Empty); }
            }

            public VariableType Type
            {
                get { return _type; }
                set { Set(ref _type, value); }
            }

            public string Expression
            {
                get { return _expression; }
                set { Set(ref _expression, value ?? string.Empty); }
            }

            /// <summary>检查结果（无问题时为提示文字）。</summary>
            public string Status
            {
                get { return _status; }
                set { Set(ref _status, value); }
            }

            public bool HasError
            {
                get { return _hasError; }
                set { Set(ref _hasError, value); }
            }

            /// <summary>上次运行预览的结果。</summary>
            public string Value
            {
                get { return _value; }
                set { Set(ref _value, value); }
            }

            public ExpressionCalcItem ToItem(int lineNumber)
            {
                return new ExpressionCalcItem { LineNumber = lineNumber, Name = Name.Trim(), Type = Type, Expression = Expression.Trim() };
            }

            private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                {
                    return;
                }
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private readonly ExpressionCalcTool _tool;
        private readonly ToolEditContext _context;
        private readonly ObservableCollection<CalcRow> _rows = new ObservableCollection<CalcRow>();
        private readonly RefScope _scope;
        private bool _loaded;

        public WpfExpressionCalcToolEditWindow(ExpressionCalcTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            _scope = _context.Root != null && _context.Node != null
                ? RefCandidateService.ScopeForNode(_context.Root, _context.Node)
                : null;

            InitializeComponent();
            Title = "变量计算 - " + _tool.ModuleName;
            ModuleNameText.Text = _tool.ModuleName;
            TypeCombo.ItemsSource = ExpressionCalcTool.SupportedTypes;
            FunctionList.ItemsSource = ExpressionFunctions.All;

            var issues = new List<ToolConfigurationIssue>();
            foreach (ExpressionCalcItem item in ExpressionCalcTool.ParseItems(_tool.Expressions, issues))
            {
                AddRow(new CalcRow { Name = item.Name, Type = item.Type, Expression = item.Expression });
            }
            RowsGrid.ItemsSource = _rows;
            _loaded = true;

            if (_rows.Count > 0)
            {
                RowsGrid.SelectedIndex = 0;
            }
            Revalidate();
            RefreshReferences();
            int malformed = issues.Count(i => i.Parameter != "计算式");
            StatusText.Text = malformed > 0
                ? $"有 {malformed} 行计算式格式错误，已忽略；保存后这些行会被移除。"
                : "双击右侧变量或函数可插入到表达式光标处。";
        }

        private string ModuleName
        {
            get { return (ModuleNameText.Text ?? string.Empty).Trim(); }
        }

        private CalcRow SelectedRow
        {
            get { return RowsGrid.SelectedItem as CalcRow; }
        }

        private void AddRow(CalcRow row, int index = -1)
        {
            row.PropertyChanged += Row_PropertyChanged;
            if (index < 0 || index > _rows.Count)
            {
                _rows.Add(row);
            }
            else
            {
                _rows.Insert(index, row);
            }
        }

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CalcRow.Name) || e.PropertyName == nameof(CalcRow.Type)
                || e.PropertyName == nameof(CalcRow.Expression))
            {
                Revalidate();
                if (e.PropertyName == nameof(CalcRow.Name))
                {
                    RefreshReferences();
                }
            }
        }

        private List<ExpressionCalcItem> CurrentItems()
        {
            return _rows.Select((row, i) => row.ToItem(i + 1)).ToList();
        }

        /// <summary>逐行检查：结果名、语法、局部名称、引用作用域（与流程校验一致）。</summary>
        private void Revalidate()
        {
            if (!_loaded)
            {
                return;
            }
            List<ExpressionCalcItem> items = CurrentItems();
            for (int i = 0; i < items.Count; i++)
            {
                string error = ExpressionCalcTool.CheckItem(items, i, ModuleName, CheckExternalReference);
                _rows[i].HasError = error != null;
                _rows[i].Status = error ?? "✓ 正确";
            }
        }

        private string CheckExternalReference(string path)
        {
            if (_scope == null)
            {
                return null;
            }
            RefTypeCheckResult check = ReferenceSemantics.Check(VariableReference.Parse(path), _scope);
            return check.Success ? null : check.Error;
        }

        /// <summary>可插入的变量：上游可见输出，加上所选行之前的本模块结果。</summary>
        private void RefreshReferences()
        {
            if (!_loaded)
            {
                return;
            }
            var paths = new List<string>();
            if (_scope != null)
            {
                paths.AddRange(_scope.Candidates.Select(c => "{" + c.Path + "}"));
            }
            int selected = RowsGrid.SelectedIndex < 0 ? _rows.Count : RowsGrid.SelectedIndex;
            paths.AddRange(_rows.Take(selected)
                .Where(r => ToolMetadata.IsValidOutputName(r.Name.Trim()))
                .Select(r => "{" + ModuleName + "." + r.Name.Trim() + "}"));
            ReferenceList.ItemsSource = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void ModuleNameText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            Revalidate();
            RefreshReferences();
        }

        private void RowsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            CalcRow row = SelectedRow;
            DetailCard.DataContext = row;
            DetailCard.IsEnabled = row != null;
            RefreshReferences();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            int n = _rows.Count + 1;
            while (_rows.Any(r => string.Equals(r.Name, "结果" + n, StringComparison.OrdinalIgnoreCase)))
            {
                n++;
            }
            var row = new CalcRow { Name = "结果" + n, Type = VariableType.Double, Expression = "0" };
            int index = RowsGrid.SelectedIndex < 0 ? _rows.Count : RowsGrid.SelectedIndex + 1;
            AddRow(row, index);
            RowsGrid.SelectedItem = row;
            Revalidate();
            ExpressionText.Focus();
            ExpressionText.SelectAll();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            CalcRow row = SelectedRow;
            if (row == null)
            {
                return;
            }
            int index = _rows.IndexOf(row);
            row.PropertyChanged -= Row_PropertyChanged;
            _rows.Remove(row);
            RowsGrid.SelectedIndex = Math.Min(index, _rows.Count - 1);
            Revalidate();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            Move(-1);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            Move(1);
        }

        private void Move(int offset)
        {
            CalcRow row = SelectedRow;
            if (row == null)
            {
                return;
            }
            int index = _rows.IndexOf(row);
            int target = index + offset;
            if (target < 0 || target >= _rows.Count)
            {
                return;
            }
            _rows.Move(index, target);
            RowsGrid.SelectedItem = row;
            Revalidate();
            RefreshReferences();
        }

        private void ReferenceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceList.SelectedItem is string path)
            {
                InsertIntoExpression(path, 0);
            }
        }

        private void FunctionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FunctionList.SelectedItem is ExpressionFunction function)
            {
                InsertIntoExpression(function.Name + "()", 1);
            }
        }

        /// <summary>在表达式光标处插入文本；caretBack 为插入后光标距插入文本末尾的字符数（函数插入后停在括号内）。</summary>
        private void InsertIntoExpression(string text, int caretBack)
        {
            if (SelectedRow == null)
            {
                StatusText.Text = "请先在左侧选择或添加一条计算式。";
                return;
            }
            int start = ExpressionText.SelectionStart;
            ExpressionText.SelectedText = text;
            ExpressionText.SelectionLength = 0;
            ExpressionText.CaretIndex = start + text.Length - caretBack;
            ExpressionText.Focus();
        }

        private void RunPreview_Click(object sender, RoutedEventArgs e)
        {
            Commit();
            foreach (CalcRow row in _rows)
            {
                row.Value = string.Empty;
            }

            FlowContext ctx = null;
            try
            {
                // 派生上下文：借用主窗口上次运行的变量，释放时只回收本窗口写入的输出
                ctx = (_context.LastRunContext ?? new FlowContext()).CreatePreviewContext();
                if (_context.InputImage != null && _context.InputImage.IsInitialized())
                {
                    ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(_context.InputImage), 1));
                }
                NodeResult result = _tool.Run(ctx);
                if (!result.IsSuccess)
                {
                    StatusText.Text = "预览失败：" + result.Message;
                    return;
                }
                foreach (CalcRow row in _rows)
                {
                    if (ctx.TryGetVariable(_tool.ModuleName, row.Name.Trim(), out Variable variable))
                    {
                        row.Value = ExpressionValues.Format(variable.Value);
                    }
                }
                StatusText.Text = _context.LastRunContext == null
                    ? "预览完成（尚未运行过流程，引用上游变量的计算式需先运行流程）。"
                    : "预览完成，基于上次运行结果。";
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is HOperatorException)
            {
                StatusText.Text = "预览失败：" + ex.Message;
            }
            finally
            {
                ctx?.Dispose();
            }
        }

        private void Commit()
        {
            _tool.ModuleName = ModuleName;
            _tool.Expressions = ExpressionCalcTool.FormatItems(CurrentItems());
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Any(r => r.HasError))
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    "部分计算式有错误，流程校验将不通过。仍然保存吗？", "变量计算",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            Commit();
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
