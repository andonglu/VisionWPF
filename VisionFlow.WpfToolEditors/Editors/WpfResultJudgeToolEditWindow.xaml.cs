using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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
    /// <summary>综合判定编辑窗口：表格管理判定项，实时检查配置与引用，运行预览显示每项实测值与判定结果。</summary>
    public partial class WpfResultJudgeToolEditWindow : Window
    {
        public sealed class JudgeRow : INotifyPropertyChanged
        {
            private string _name = string.Empty;
            private string _expression = string.Empty;
            private string _lowerText = string.Empty;
            private string _upperText = string.Empty;
            private string _ngCodeText = "1";
            private string _ngMessage = string.Empty;
            private string _status = string.Empty;
            private bool _hasError;
            private string _valueText = string.Empty;
            private string _judgeText = string.Empty;

            public event PropertyChangedEventHandler PropertyChanged;

            public string Name { get { return _name; } set { Set(ref _name, value ?? string.Empty); } }
            public string Expression { get { return _expression; } set { Set(ref _expression, value ?? string.Empty); } }
            public string LowerText { get { return _lowerText; } set { Set(ref _lowerText, value ?? string.Empty); Raise(nameof(RangeText)); } }
            public string UpperText { get { return _upperText; } set { Set(ref _upperText, value ?? string.Empty); Raise(nameof(RangeText)); } }
            public string NgCodeText { get { return _ngCodeText; } set { Set(ref _ngCodeText, value ?? string.Empty); } }
            public string NgMessage { get { return _ngMessage; } set { Set(ref _ngMessage, value ?? string.Empty); } }
            public string Status { get { return _status; } set { Set(ref _status, value); } }
            public bool HasError { get { return _hasError; } set { Set(ref _hasError, value); } }
            /// <summary>上次运行预览的实测值。</summary>
            public string ValueText { get { return _valueText; } set { Set(ref _valueText, value); } }
            /// <summary>上次运行预览的判定：OK / NG / 未判定。</summary>
            public string JudgeText { get { return _judgeText; } set { Set(ref _judgeText, value); } }

            public string RangeText
            {
                get
                {
                    string lower = LowerText.Trim();
                    string upper = UpperText.Trim();
                    if (lower.Length > 0 && upper.Length > 0)
                    {
                        return $"[{lower}, {upper}]";
                    }
                    if (lower.Length > 0)
                    {
                        return "≥ " + lower;
                    }
                    return upper.Length > 0 ? "≤ " + upper : "不限";
                }
            }

            /// <summary>转换为判定项；上下限或代码无法解析时 parseError 给出原因，相应字段取默认值。</summary>
            public ResultJudgeItem ToItem(int lineNumber, out string parseError)
            {
                parseError = null;
                if (!ResultJudgeTool.TryParseBound(LowerText, out double? lower))
                {
                    parseError = $"下限“{LowerText.Trim()}”不是数值";
                }
                if (!ResultJudgeTool.TryParseBound(UpperText, out double? upper))
                {
                    parseError = parseError ?? $"上限“{UpperText.Trim()}”不是数值";
                }
                int code = 1;
                string codeText = NgCodeText.Trim();
                if (codeText.Length > 0 && !int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                {
                    parseError = parseError ?? $"NG 代码“{codeText}”不是整数";
                    code = 1;
                }
                return new ResultJudgeItem
                {
                    LineNumber = lineNumber,
                    Name = Name.Trim(),
                    Expression = Expression.Trim(),
                    Lower = lower,
                    Upper = upper,
                    NgCode = code,
                    NgMessage = NgMessage.Trim()
                };
            }

            /// <summary>保存用的配置行（保留用户输入的原文）。</summary>
            public string ToLine()
            {
                return string.Join("|", Name.Trim(), Expression.Trim(), LowerText.Trim(), UpperText.Trim(),
                    NgCodeText.Trim(), NgMessage.Trim());
            }

            private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                {
                    return;
                }
                field = value;
                Raise(propertyName);
            }

            private void Raise(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private static readonly string[] ConfigProperties =
        {
            nameof(JudgeRow.Name), nameof(JudgeRow.Expression), nameof(JudgeRow.LowerText),
            nameof(JudgeRow.UpperText), nameof(JudgeRow.NgCodeText), nameof(JudgeRow.NgMessage)
        };

        private readonly ResultJudgeTool _tool;
        private readonly ToolEditContext _context;
        private readonly ObservableCollection<JudgeRow> _rows = new ObservableCollection<JudgeRow>();
        private readonly RefScope _scope;
        private bool _loaded;

        public WpfResultJudgeToolEditWindow(ResultJudgeTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            _scope = _context.Root != null && _context.Node != null
                ? RefCandidateService.ScopeForNode(_context.Root, _context.Node)
                : null;

            InitializeComponent();
            Title = "综合判定 - " + _tool.ModuleName;
            ModuleNameText.Text = _tool.ModuleName;
            OkCodeText.Text = _tool.OkCode.ToString(CultureInfo.InvariantCulture);
            OkMessageText.Text = _tool.OkMessage;
            StopAtFirstNgCheck.IsChecked = _tool.StopAtFirstNg;
            FunctionList.ItemsSource = ExpressionFunctions.All;
            ReferenceList.ItemsSource = _scope == null
                ? new List<string>()
                : _scope.Candidates.Select(c => "{" + c.Path + "}").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var issues = new List<ToolConfigurationIssue>();
            foreach (ResultJudgeItem item in ResultJudgeTool.ParseItems(_tool.Items, issues))
            {
                AddRow(new JudgeRow
                {
                    Name = item.Name,
                    Expression = item.Expression,
                    LowerText = ResultJudgeItem.FormatBound(item.Lower),
                    UpperText = ResultJudgeItem.FormatBound(item.Upper),
                    NgCodeText = item.NgCode.ToString(CultureInfo.InvariantCulture),
                    NgMessage = item.NgMessage
                });
            }
            RowsGrid.ItemsSource = _rows;
            _loaded = true;
            if (_rows.Count > 0)
            {
                RowsGrid.SelectedIndex = 0;
            }
            Revalidate();
            StatusText.Text = issues.Count > 0
                ? $"有 {issues.Count} 行判定项格式错误，已忽略；保存后这些行会被移除。"
                : "双击右侧变量或函数可插入到取值表达式光标处。";
        }

        private JudgeRow SelectedRow
        {
            get { return RowsGrid.SelectedItem as JudgeRow; }
        }

        private void AddRow(JudgeRow row, int index = -1)
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
            if (ConfigProperties.Contains(e.PropertyName))
            {
                Revalidate();
            }
        }

        private void OkCodeText_TextChanged(object sender, TextChangedEventArgs e)
        {
            Revalidate();
        }

        private bool TryGetOkCode(out int okCode)
        {
            return int.TryParse((OkCodeText.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out okCode);
        }

        /// <summary>逐项检查：上下限与代码格式、名称、重名、与合格代码冲突、语法与引用作用域。</summary>
        private void Revalidate()
        {
            if (!_loaded)
            {
                return;
            }
            bool okCodeValid = TryGetOkCode(out int okCode);
            var parseErrors = new string[_rows.Count];
            var items = new List<ResultJudgeItem>();
            for (int i = 0; i < _rows.Count; i++)
            {
                items.Add(_rows[i].ToItem(i + 1, out parseErrors[i]));
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                string error = parseErrors[i]
                    ?? ResultJudgeTool.CheckItem(items, i, okCodeValid ? okCode : int.MinValue, CheckExternalReference);
                _rows[i].HasError = error != null;
                _rows[i].Status = error ?? "✓ 正确";
            }
            if (!okCodeValid)
            {
                StatusText.Text = "合格代码必须是整数。";
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

        private void RowsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            JudgeRow row = SelectedRow;
            DetailCard.DataContext = row;
            DetailCard.IsEnabled = row != null;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            int n = _rows.Count + 1;
            while (_rows.Any(r => string.Equals(r.Name, "检测项" + n, StringComparison.OrdinalIgnoreCase)))
            {
                n++;
            }
            int nextCode = 1;
            while (_rows.Any(r => r.NgCodeText.Trim() == nextCode.ToString(CultureInfo.InvariantCulture))
                || (TryGetOkCode(out int okCode) && okCode == nextCode))
            {
                nextCode++;
            }
            var row = new JudgeRow
            {
                Name = "检测项" + n,
                Expression = "true",
                NgCodeText = nextCode.ToString(CultureInfo.InvariantCulture)
            };
            int index = RowsGrid.SelectedIndex < 0 ? _rows.Count : RowsGrid.SelectedIndex + 1;
            AddRow(row, index);
            RowsGrid.SelectedItem = row;
            Revalidate();
            ExpressionText.Focus();
            ExpressionText.SelectAll();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            JudgeRow row = SelectedRow;
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
            JudgeRow row = SelectedRow;
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

        /// <summary>在取值表达式光标处插入文本；caretBack 为插入后光标距插入文本末尾的字符数。</summary>
        private void InsertIntoExpression(string text, int caretBack)
        {
            if (SelectedRow == null)
            {
                StatusText.Text = "请先在左侧选择或添加一个判定项。";
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
            if (!TryCommit())
            {
                return;
            }
            foreach (JudgeRow row in _rows)
            {
                row.ValueText = string.Empty;
                row.JudgeText = string.Empty;
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

                List<double> values = ((IEnumerable)ctx.GetVariable(_tool.ModuleName, "Values").Value).Cast<double>().ToList();
                List<bool> oks = ((IEnumerable)ctx.GetVariable(_tool.ModuleName, "ItemOks").Value).Cast<bool>().ToList();
                var ngNames = new HashSet<string>(((IEnumerable)ctx.GetVariable(_tool.ModuleName, "NgNames").Value).Cast<string>(),
                    StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _rows.Count && i < values.Count; i++)
                {
                    _rows[i].ValueText = ExpressionValues.Format(values[i]);
                    _rows[i].JudgeText = oks[i] ? "OK" : ngNames.Contains(_rows[i].Name.Trim()) ? "NG" : "未判定";
                }

                bool ok = (bool)ctx.GetVariable(_tool.ModuleName, "Ok").Value;
                object code = ctx.GetVariable(_tool.ModuleName, "Code").Value;
                object message = ctx.GetVariable(_tool.ModuleName, "Message").Value;
                StatusText.Text = $"判定结果：{(ok ? "OK" : "NG")}，代码 {code}，{message}"
                    + (_context.LastRunContext == null ? "（尚未运行过流程，引用上游变量的项会判为不合格）" : string.Empty);
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

        private bool TryCommit()
        {
            if (!TryGetOkCode(out int okCode))
            {
                MessageBox.Show(this, "合格代码必须是整数。", "综合判定", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            _tool.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            _tool.OkCode = okCode;
            _tool.OkMessage = OkMessageText.Text ?? string.Empty;
            _tool.StopAtFirstNg = StopAtFirstNgCheck.IsChecked == true;
            _tool.Items = string.Join("\n", _rows.Select(r => r.ToLine()));
            return true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Any(r => r.HasError))
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    "部分判定项有错误，流程校验将不通过。仍然保存吗？", "综合判定",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            if (!TryCommit())
            {
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
