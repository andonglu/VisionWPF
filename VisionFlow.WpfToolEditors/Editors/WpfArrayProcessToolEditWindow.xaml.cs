using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Expressions;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>数组处理编辑窗口：按操作显示对应参数，实时检查筛选/变换表达式，运行预览查看结果数组与统计值。</summary>
    public partial class WpfArrayProcessToolEditWindow : Window
    {
        public sealed class Choice<T>
        {
            public Choice(T value, string label, string help = null)
            {
                Value = value;
                Label = label;
                Help = help;
            }

            public T Value { get; private set; }

            public string Label { get; private set; }

            public string Help { get; private set; }
        }

        public sealed class ValueRow
        {
            public int Position { get; set; }

            public string SourceIndex { get; set; }

            public string Value { get; set; }
        }

        public sealed class StatRow
        {
            public string Name { get; set; }

            public string Value { get; set; }
        }

        private static readonly Choice<ArrayOperation>[] Operations =
        {
            new Choice<ArrayOperation>(ArrayOperation.Statistics, "统计", "不改变数组，只计算个数、求和、均值、最大最小值等统计输出。"),
            new Choice<ArrayOperation>(ArrayOperation.ElementAt, "取值", "按序号取出一个元素，输出到 Value；越界时结果为空。"),
            new Choice<ArrayOperation>(ArrayOperation.Sort, "排序", "按数值或文本排序；Indices 输出排序后每个元素在原数组中的序号。"),
            new Choice<ArrayOperation>(ArrayOperation.Filter, "筛选", "保留表达式结果为真的元素；Indices 输出保留元素的原序号。"),
            new Choice<ArrayOperation>(ArrayOperation.Map, "变换", "对每个元素计算表达式，结果组成新数组。"),
            new Choice<ArrayOperation>(ArrayOperation.Concat, "拼接", "把第二个数组接在数组后面。"),
            new Choice<ArrayOperation>(ArrayOperation.ElementWise, "逐元素运算", "两个数值数组逐元素加减乘除。"),
            new Choice<ArrayOperation>(ArrayOperation.Distinct, "去重", "去掉重复元素，保留首次出现的顺序。")
        };

        private static readonly Choice<ArrayElementWiseOperator>[] Operators =
        {
            new Choice<ArrayElementWiseOperator>(ArrayElementWiseOperator.Add, "加 (+)"),
            new Choice<ArrayElementWiseOperator>(ArrayElementWiseOperator.Subtract, "减 (-)"),
            new Choice<ArrayElementWiseOperator>(ArrayElementWiseOperator.Multiply, "乘 (×)"),
            new Choice<ArrayElementWiseOperator>(ArrayElementWiseOperator.Divide, "除 (÷)")
        };

        private static readonly string[] LocalNames = { "Item", "ItemIndex" };

        private static readonly (string Name, string Label)[] Statistics =
        {
            ("Count", "个数"), ("ValidCount", "有效数值个数"), ("Found", "有结果"), ("Value", "第一个值"),
            ("Sum", "求和"), ("Mean", "均值"), ("Max", "最大值"), ("Min", "最小值"),
            ("StdDev", "标准差"), ("Range", "极差"), ("MaxIndex", "最大值序号"), ("MinIndex", "最小值序号")
        };

        private readonly ArrayProcessTool _tool;
        private readonly ToolEditContext _context;
        private readonly RefScope _scope;
        private bool _loaded;

        public WpfArrayProcessToolEditWindow(ArrayProcessTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            bool hasScope = _context.Root != null && _context.Node != null;
            _scope = hasScope ? RefCandidateService.ScopeForNode(_context.Root, _context.Node) : null;

            InitializeComponent();
            Title = "数组处理 - " + _tool.ModuleName;
            ModuleNameText.Text = _tool.ModuleName;

            ArrayPathCombo.ItemsSource = hasScope
                ? RefCandidateService.Collections(_context.Root, _context.Node).Select(c => c.Path).ToList()
                : new List<string>();
            SecondPathCombo.ItemsSource = hasScope
                ? RefCandidateService.ForNode(_context.Root, _context.Node).Select(c => c.Path).ToList()
                : new List<string>();
            ArrayPathCombo.Text = _tool.ArrayPath ?? string.Empty;
            SecondPathCombo.Text = _tool.SecondPath ?? string.Empty;

            OperationCombo.ItemsSource = Operations;
            OperationCombo.SelectedItem = Operations.FirstOrDefault(o => o.Value == _tool.Operation) ?? Operations[0];
            ElementTypeCombo.ItemsSource = new[] { "数值", "文本" };
            ElementTypeCombo.SelectedIndex = _tool.ElementType == ArrayElementType.Text ? 1 : 0;
            IndexText.Text = _tool.Index.ToString(CultureInfo.InvariantCulture);
            DescendingCheck.IsChecked = _tool.Descending;
            OperatorCombo.ItemsSource = Operators;
            OperatorCombo.SelectedItem = Operators.FirstOrDefault(o => o.Value == _tool.ElementWiseOperator) ?? Operators[0];
            ExpressionText.Text = _tool.Expression ?? string.Empty;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
            ReferenceCombo.ItemsSource = _scope == null
                ? new List<string>()
                : _scope.Candidates.Select(c => "{" + c.Path + "}").ToList();

            _loaded = true;
            UpdateOperationPanels();
            CheckExpression();
            StatusText.Text = "数组为数值时可做全部操作；文本数组不支持统计和逐元素运算。";
        }

        private ArrayOperation SelectedOperation
        {
            get { return (OperationCombo.SelectedItem as Choice<ArrayOperation>)?.Value ?? ArrayOperation.Statistics; }
        }

        private ArrayElementType SelectedElementType
        {
            get { return ElementTypeCombo.SelectedIndex == 1 ? ArrayElementType.Text : ArrayElementType.Number; }
        }

        private void UpdateOperationPanels()
        {
            if (!_loaded)
            {
                return;
            }
            ArrayOperation operation = SelectedOperation;
            bool usesSecond = operation == ArrayOperation.Concat || operation == ArrayOperation.ElementWise;
            bool usesExpression = operation == ArrayOperation.Filter || operation == ArrayOperation.Map;
            SetVisible(SecondLabel, usesSecond);
            SetVisible(SecondPathCombo, usesSecond);
            SetVisible(IndexPanel, operation == ArrayOperation.ElementAt);
            SetVisible(DescendingCheck, operation == ArrayOperation.Sort);
            SetVisible(OperatorPanel, operation == ArrayOperation.ElementWise);
            SetVisible(ExpressionPanel, usesExpression);

            string help = (OperationCombo.SelectedItem as Choice<ArrayOperation>)?.Help ?? string.Empty;
            if (operation == ArrayOperation.Filter)
            {
                help += " 例：{Item} > 10 and {ItemIndex} < 5";
            }
            else if (operation == ArrayOperation.Map)
            {
                help += SelectedElementType == ArrayElementType.Number
                    ? " 例：{Item} * {Calib.Scale}"
                    : " 例：\"ID-\" + {Item}";
            }
            if (SelectedElementType == ArrayElementType.Text
                && (operation == ArrayOperation.Statistics || operation == ArrayOperation.ElementWise))
            {
                help += " ⚠ 该操作只支持数值数组。";
            }
            OperationHelpText.Text = help;
        }

        private static void SetVisible(UIElement element, bool visible)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>检查表达式语法、局部名称与引用作用域（与流程校验一致）。</summary>
        private void CheckExpression()
        {
            if (!_loaded)
            {
                return;
            }
            string error = ExpressionError(ExpressionText.Text);
            ExpressionCheckText.Text = error ?? "✓ 正确";
            ExpressionCheckText.Foreground = (System.Windows.Media.Brush)FindResource(error == null
                ? "SystemFillColorSuccessBrush"
                : "SystemFillColorCriticalBrush");
        }

        private string ExpressionError(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "表达式不能为空";
            }
            if (!ExpressionParser.TryParse(text, out CompiledExpression expression, out ExpressionException error))
            {
                return "表达式错误：" + error.Message;
            }
            string unknown = expression.LocalNames.FirstOrDefault(n => !LocalNames.Contains(n, StringComparer.OrdinalIgnoreCase));
            if (unknown != null)
            {
                return $"未定义的名称 {{{unknown}}}，可用 {{Item}}、{{ItemIndex}}";
            }
            if (_scope != null)
            {
                foreach (string path in expression.References)
                {
                    RefTypeCheckResult check = ReferenceSemantics.Check(VariableReference.Parse(path), _scope);
                    if (!check.Success)
                    {
                        return check.Error;
                    }
                }
            }
            return null;
        }

        private void OperationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateOperationPanels();
        }

        private void ElementTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateOperationPanels();
        }

        private void ExpressionText_TextChanged(object sender, TextChangedEventArgs e)
        {
            CheckExpression();
        }

        private void InsertItem_Click(object sender, RoutedEventArgs e)
        {
            InsertIntoExpression("{Item}");
        }

        private void InsertItemIndex_Click(object sender, RoutedEventArgs e)
        {
            InsertIntoExpression("{ItemIndex}");
        }

        private void ReferenceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReferenceCombo.SelectedItem is string path)
            {
                InsertIntoExpression(path);
                ReferenceCombo.SelectedIndex = -1;
            }
        }

        private void InsertIntoExpression(string text)
        {
            int start = ExpressionText.SelectionStart;
            ExpressionText.SelectedText = text;
            ExpressionText.SelectionLength = 0;
            ExpressionText.CaretIndex = start + text.Length;
            ExpressionText.Focus();
        }

        /// <summary>把界面参数写回工具；序号无效时返回错误信息且不写入。</summary>
        private string Commit()
        {
            string indexText = (IndexText.Text ?? string.Empty).Trim();
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                if (SelectedOperation == ArrayOperation.ElementAt)
                {
                    return "序号必须是整数";
                }
                index = _tool.Index;
            }

            _tool.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            _tool.ArrayPath = (ArrayPathCombo.Text ?? string.Empty).Trim();
            _tool.SecondPath = (SecondPathCombo.Text ?? string.Empty).Trim();
            _tool.Operation = SelectedOperation;
            _tool.ElementType = SelectedElementType;
            _tool.Index = index;
            _tool.Descending = DescendingCheck.IsChecked == true;
            _tool.ElementWiseOperator = (OperatorCombo.SelectedItem as Choice<ArrayElementWiseOperator>)?.Value
                ?? ArrayElementWiseOperator.Add;
            _tool.Expression = (ExpressionText.Text ?? string.Empty).Trim();
            _tool.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
            return null;
        }

        private void RunPreview_Click(object sender, RoutedEventArgs e)
        {
            string commitError = Commit();
            if (commitError != null)
            {
                StatusText.Text = commitError;
                return;
            }
            ValuesGrid.ItemsSource = null;
            StatsGrid.ItemsSource = null;

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
                ShowPreview(ctx);
                if (!result.IsSuccess)
                {
                    StatusText.Text = "预览失败：" + result.Message;
                    return;
                }
                StatusText.Text = _context.LastRunContext == null
                    ? "预览完成（尚未运行过流程，引用上游变量时需先运行流程）。"
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

        private void ShowPreview(FlowContext ctx)
        {
            string module = _tool.ModuleName;
            if (!ctx.TryGetVariable(module, "Values", out Variable valuesVariable))
            {
                return;
            }
            List<object> values = AsList(valuesVariable.Value);
            List<object> indices = ctx.TryGetVariable(module, "Indices", out Variable indicesVariable)
                ? AsList(indicesVariable.Value)
                : new List<object>();
            ValuesGrid.ItemsSource = values
                .Select((value, i) => new ValueRow
                {
                    Position = i,
                    SourceIndex = i < indices.Count ? ExpressionValues.Format(indices[i]) : string.Empty,
                    Value = ExpressionValues.Format(value)
                })
                .ToList();

            var stats = new List<StatRow>();
            foreach ((string name, string label) in Statistics)
            {
                if (ctx.TryGetVariable(module, name, out Variable variable))
                {
                    stats.Add(new StatRow { Name = $"{label} ({name})", Value = ExpressionValues.Format(variable.Value) });
                }
            }
            StatsGrid.ItemsSource = stats;
        }

        private static List<object> AsList(object value)
        {
            return value is System.Collections.IEnumerable items && !(value is string)
                ? items.Cast<object>().ToList()
                : new List<object> { value };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            ArrayOperation operation = SelectedOperation;
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(ArrayPathCombo.Text))
            {
                problems.Add("未选择数组");
            }
            if ((operation == ArrayOperation.Concat || operation == ArrayOperation.ElementWise)
                && string.IsNullOrWhiteSpace(SecondPathCombo.Text))
            {
                problems.Add("未选择第二个数组");
            }
            if (operation == ArrayOperation.Filter || operation == ArrayOperation.Map)
            {
                string error = ExpressionError(ExpressionText.Text);
                if (error != null)
                {
                    problems.Add(error);
                }
            }
            if (SelectedElementType == ArrayElementType.Text
                && (operation == ArrayOperation.Statistics || operation == ArrayOperation.ElementWise))
            {
                problems.Add("文本数组不支持" + ((Choice<ArrayOperation>)OperationCombo.SelectedItem).Label);
            }
            if (problems.Count > 0)
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    string.Join(Environment.NewLine, problems) + Environment.NewLine + Environment.NewLine + "流程校验将不通过。仍然保存吗？",
                    "数组处理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            string commitError = Commit();
            if (commitError != null)
            {
                StatusText.Text = commitError;
                IndexText.Focus();
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
