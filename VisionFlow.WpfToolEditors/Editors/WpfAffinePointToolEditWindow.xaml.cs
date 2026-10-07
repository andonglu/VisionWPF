using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using Microsoft.Win32;
using VisionFlow.Controls;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Tools.Calibration;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 图像坐标转世界坐标编辑窗口（CB-05 运行参数 + CB-02 N 点标定助手）。
    /// 运行参数页配置输入、标定方式与来源；N 点标定页采集点对（取当前值 / 图像点击 / 手动 / CSV）、求解并显示残差，
    /// 结果“保存为标定文件”或“内嵌到当前工具”。所有改动先作用于窗口内的待提交状态，确定时写回工具；执行测试经 ToolTestRun。
    /// 单独打开（主窗口工具栏“标定助手”）时只显示助手页，结果只能保存为标定文件。
    /// </summary>
    public partial class WpfAffinePointToolEditWindow : Window
    {
        private sealed class Choice<T>
        {
            public Choice(T value, string label)
            {
                Value = value;
                Label = label;
            }

            public T Value { get; private set; }
            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }

        private sealed class PointRow
        {
            public int Index { get; set; }
            public double Row { get; set; }
            public double Column { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double? Residual { get; set; }
            public bool IsOutlier { get; set; }
            public string ResidualText => Residual.HasValue ? Residual.Value.ToString("F4", CultureInfo.CurrentCulture) : string.Empty;
            public string State => IsOutlier ? "超阈" : string.Empty;
        }

        private sealed class RunRow
        {
            public int Index { get; set; }
            public string WorldRow { get; set; }
            public string WorldColumn { get; set; }
            public string WorldAngle { get; set; }
        }

        private static readonly Choice<CalibrationKind>[] KindChoices =
        {
            new Choice<CalibrationKind>(CalibrationKind.Affine2D, "仿射矩阵（Affine2D）"),
            new Choice<CalibrationKind>(CalibrationKind.Camera, "相机标定（Camera）")
        };

        private static readonly Choice<CalibrationSource>[] SourceChoices =
        {
            new Choice<CalibrationSource>(CalibrationSource.File, "标定文件"),
            new Choice<CalibrationSource>(CalibrationSource.Embedded, "内嵌在工具中")
        };

        private static readonly Choice<CalibrationTransformType>[] TransformChoices =
        {
            new Choice<CalibrationTransformType>(CalibrationTransformType.affine, "affine（仿射，两方向比例可不同）"),
            new Choice<CalibrationTransformType>(CalibrationTransformType.similarity, "similarity（等比例，无剪切）"),
            new Choice<CalibrationTransformType>(CalibrationTransformType.rigid, "rigid（只有旋转平移，比例为 1）")
        };

        private readonly AffinePointTool _tool;
        private readonly ToolEditContext _context;
        private readonly bool _standalone;
        /// <summary>窗口内待提交的标定来源（文件 / 内嵌，互斥），确定时写回工具。</summary>
        private readonly AffinePointTool _calibrationState = new AffinePointTool("标定");
        private readonly List<PointRow> _points = new List<PointRow>();
        private Affine2DCalibration _solved;
        private bool _loading;
        private PointRow _pickingRow;
        private bool _pickActive;

        /// <param name="standalone">单独打开的标定助手：不编辑流程中的工具，只显示助手页，结果只能保存为标定文件。</param>
        public WpfAffinePointToolEditWindow(AffinePointTool tool, ToolEditContext context, bool standalone = false)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            _standalone = standalone;
            InitializeComponent();
            Title = standalone ? "N 点标定助手" : "图像坐标转世界坐标 - " + tool.ModuleName;
            _loading = true;
            InitializeOptions();
            LoadFromTool();
            _loading = false;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
            LoadPointsFromCalibration();
            ShowCalibrationImage();
            CalibImageView.PointPicked += OnImagePointPicked;
            if (standalone)
            {
                RunTab.Visibility = Visibility.Collapsed;
                EmbedButton.IsEnabled = false;
                EmbedButton.ToolTip = "单独打开的标定助手没有关联工具，只能保存为标定文件";
                OkButton.Visibility = Visibility.Collapsed;
                CancelButton.Content = "关闭";
                MainTabs.SelectedItem = AssistantTab;
            }
            Closed += (s, e) => CalibImageView.PointPicked -= OnImagePointPicked;
        }

        /// <summary>打开后直接显示 N 点标定页（主窗口“标定助手”按钮对选中的坐标转换节点使用）。</summary>
        public void ShowCalibrationAssistant()
        {
            MainTabs.SelectedItem = AssistantTab;
        }

        private CalibrationKind SelectedKind => (KindCombo.SelectedItem as Choice<CalibrationKind>)?.Value ?? CalibrationKind.Affine2D;
        private CalibrationSource SelectedSource => (SourceCombo.SelectedItem as Choice<CalibrationSource>)?.Value ?? CalibrationSource.File;
        private CalibrationTransformType SelectedTransform => (TransformTypeCombo.SelectedItem as Choice<CalibrationTransformType>)?.Value ?? CalibrationTransformType.affine;

        private void InitializeOptions()
        {
            KindCombo.ItemsSource = KindChoices;
            SourceCombo.ItemsSource = SourceChoices;
            TransformTypeCombo.ItemsSource = TransformChoices;
            TransformTypeCombo.SelectedIndex = 0;
            AnglePathCombo.Items.Add(string.Empty);
            MatrixPathCombo.Items.Add(string.Empty);
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(double), acceptsCollection: true))
                {
                    RowPathCombo.Items.Add(candidate.Path);
                    ColumnPathCombo.Items.Add(candidate.Path);
                    AnglePathCombo.Items.Add(candidate.Path);
                }
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HomMat2D)))
                {
                    MatrixPathCombo.Items.Add(candidate.Path);
                }
            }
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                CalibImageCombo.Items.Add("Input.Image");
            }
            if (_context.LastRunContext != null)
            {
                foreach (Variable variable in _context.LastRunContext.GetAllVariables())
                {
                    string path = variable.ModuleName + "." + variable.Name;
                    if (variable.Value is HalconImage)
                    {
                        if (!CalibImageCombo.Items.Contains(path))
                        {
                            CalibImageCombo.Items.Add(path);
                        }
                    }
                    else if (IsNumeric(variable.Value) || (variable.Kind == VariableKind.Array && variable.Value is IEnumerable && !(variable.Value is string)
                        && (variable.Type == VariableType.Double || variable.Type == VariableType.Int)))
                    {
                        PickRowVarCombo.Items.Add(path);
                        PickColumnVarCombo.Items.Add(path);
                    }
                }
            }
            if (CalibImageCombo.Items.Count > 0)
            {
                CalibImageCombo.SelectedIndex = 0;
            }
        }

        private static bool IsNumeric(object value)
        {
            return value is double || value is float || value is int || value is long || value is short || value is byte || value is decimal;
        }

        private void LoadFromTool()
        {
            ModuleNameText.Text = _tool.ModuleName;
            RowPathCombo.Text = _tool.RowPath ?? string.Empty;
            ColumnPathCombo.Text = _tool.ColumnPath ?? string.Empty;
            AnglePathCombo.Text = _tool.AnglePath ?? string.Empty;
            MatrixPathCombo.Text = _tool.MatrixPath ?? string.Empty;
            KindCombo.SelectedItem = KindChoices.First(c => c.Value == _tool.CalibrationKind);
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == _tool.CalibrationSource);
            _calibrationState.CalibrationKind = _tool.CalibrationKind;
            _calibrationState.CalibrationSource = _tool.CalibrationSource;
            _calibrationState.CalibrationFile = _tool.CalibrationFile;
            _calibrationState.CalibrationData = _tool.CalibrationData;
            CalibrationFileText.Text = _tool.CalibrationFile ?? string.Empty;
            OriginRowText.Text = Format(_tool.OriginRow);
            OriginColumnText.Text = Format(_tool.OriginColumn);
        }

        // ======================= 运行参数页：标定来源 =======================

        private void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }
            _calibrationState.CalibrationKind = SelectedKind;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SelectedSource == _calibrationState.CalibrationSource)
            {
                return;
            }
            // 切换来源清空另一侧（与工具的 UseCalibrationFile / UseEmbeddedCalibration 一致）
            if (SelectedSource == CalibrationSource.File)
            {
                _calibrationState.UseCalibrationFile(string.IsNullOrWhiteSpace(CalibrationFileText.Text) ? null : CalibrationFileText.Text.Trim());
                SetStatus("已切换为标定文件，内嵌标定数据已清空。");
            }
            else
            {
                _calibrationState.UseEmbeddedCalibration(null);
                CalibrationFileText.Text = string.Empty;
                SetStatus("已切换为内嵌：标定文件路径已清空，请在 N 点标定页计算后“内嵌到当前工具”。");
            }
            UpdateSourcePanels();
            UpdateCalibrationInfo();
        }

        private void CalibrationFileText_LostFocus(object sender, RoutedEventArgs e)
        {
            string path = CalibrationFileText.Text.Trim();
            if (_calibrationState.CalibrationSource == CalibrationSource.File && !string.Equals(path, _calibrationState.CalibrationFile ?? string.Empty, StringComparison.Ordinal))
            {
                _calibrationState.UseCalibrationFile(path.Length == 0 ? null : path);
                UpdateCalibrationInfo();
            }
        }

        private void BrowseCalibrationFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择标定文件",
                Filter = "标定文件 (*.vfcal.json;*.tup;*.dat)|*.vfcal.json;*.tup;*.dat|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            SetFileSource(dialog.FileName);
            SetStatus("已选择标定文件：" + dialog.FileName);
        }

        private void SetFileSource(string path)
        {
            _loading = true;
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == CalibrationSource.File);
            _loading = false;
            _calibrationState.UseCalibrationFile(path);
            CalibrationFileText.Text = path ?? string.Empty;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
        }

        private void UpdateSourcePanels()
        {
            bool file = _calibrationState.CalibrationSource == CalibrationSource.File;
            CalibrationFileLabel.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            CalibrationFilePanel.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            bool affine = SelectedKind == CalibrationKind.Affine2D;
            MatrixPathLabel.Visibility = affine ? Visibility.Visible : Visibility.Collapsed;
            MatrixPathCombo.Visibility = affine ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>显示待提交标定来源的内容摘要；格式或类型问题直接给出与流程校验相同的说明。</summary>
        private void UpdateCalibrationInfo()
        {
            CalibrationResult calibration = TryLoadPendingCalibration(out string error);
            if (calibration == null)
            {
                CalibrationInfoText.Text = error;
                return;
            }
            string origin = _calibrationState.CalibrationSource == CalibrationSource.Embedded ? "内嵌" : "文件";
            string text;
            if (calibration.IsLegacyTuple)
            {
                text = "标定：旧版 write_tuple 矩阵文件（Affine2D）";
            }
            else if (calibration.Affine2D != null)
            {
                Affine2DCalibration affine = calibration.Affine2D;
                text = $"标定：Affine2D，{affine.TransformType}，{affine.Points.Count} 个点，RMS {FormatError(affine.RmsError)}，最大 {FormatError(affine.MaxError)}，单位 {calibration.Unit ?? "未填"}";
            }
            else
            {
                text = $"标定：{calibration.Kind}（包含 {calibration.SectionsText()}），单位 {calibration.Unit ?? "未填"}";
            }
            string kindIssue = _calibrationState.CheckConfiguration().FirstOrDefault(i => i.Parameter == nameof(AffinePointTool.CalibrationKind))?.Message;
            CalibrationInfoText.Text = text + "（" + origin + "）" + (kindIssue == null ? string.Empty : "；" + kindIssue);
        }

        private CalibrationResult TryLoadPendingCalibration(out string error)
        {
            error = null;
            try
            {
                if (_calibrationState.CalibrationSource == CalibrationSource.Embedded)
                {
                    if (string.IsNullOrWhiteSpace(_calibrationState.CalibrationData))
                    {
                        error = "标定：内嵌，尚无标定数据（在 N 点标定页计算后“内嵌到当前工具”）";
                        return null;
                    }
                    return CalibrationService.Parse(_calibrationState.CalibrationData, "内嵌标定数据");
                }
                if (string.IsNullOrWhiteSpace(_calibrationState.CalibrationFile))
                {
                    error = "标定：未配置标定文件";
                    return null;
                }
                return CalibrationService.Load(_calibrationState.CalibrationFile);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is HalconException)
            {
                error = "标定：" + ex.Message;
                return null;
            }
        }

        // ======================= 运行参数页：执行测试与提交 =======================

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            ToolTestRun run = null;
            try
            {
                run = ToolTestRun.Run(_tool, copy => ApplyTo((AffinePointTool)copy), _context.LastRunContext, _context.InputImage);
                if (!run.Result.IsSuccess)
                {
                    RunResultGrid.ItemsSource = null;
                    RunSummaryText.Text = "运行失败：" + run.Result.Message;
                    SetStatus("执行测试：" + run.Result.Message);
                    return;
                }
                FlowContext ctx = run.Context;
                string module = run.ModuleName;
                double[] rows = (double[])ctx.GetVariable(module, "WorldRows").Value;
                double[] columns = (double[])ctx.GetVariable(module, "WorldColumns").Value;
                double[] angles = (double[])ctx.GetVariable(module, "WorldAngles").Value;
                RunResultGrid.ItemsSource = rows.Select((r, i) => new RunRow
                {
                    Index = i,
                    WorldRow = r.ToString("R", CultureInfo.InvariantCulture),
                    WorldColumn = columns[i].ToString("R", CultureInfo.InvariantCulture),
                    WorldAngle = angles.Length > i ? angles[i].ToString("R", CultureInfo.InvariantCulture) : string.Empty
                }).ToList();
                RunSummaryText.Text = $"{rows.Length} 个点" + (angles.Length > 0 ? $"，第一个角度 {AngleMath.ToDegrees(angles[0]).ToString("F4", CultureInfo.CurrentCulture)}°" : string.Empty);
                SetStatus("执行测试完成：" + RunSummaryText.Text);
            }
            catch (Exception ex)
            {
                ShowError("执行测试失败：" + ex.Message);
            }
            finally
            {
                run?.Dispose();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyTo(_tool);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("参数保存失败：" + ex.Message);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_standalone)
            {
                Close();
                return;
            }
            DialogResult = false;
            Close();
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）。</summary>
        private void ApplyTo(AffinePointTool target)
        {
            CalibrationFileText_LostFocus(this, null);
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.RowPath = NullIfEmpty(RowPathCombo.Text);
            target.ColumnPath = NullIfEmpty(ColumnPathCombo.Text);
            target.AnglePath = NullIfEmpty(AnglePathCombo.Text);
            target.MatrixPath = SelectedKind == CalibrationKind.Affine2D ? NullIfEmpty(MatrixPathCombo.Text) : null;
            target.CalibrationKind = SelectedKind;
            target.CalibrationSource = _calibrationState.CalibrationSource;
            target.CalibrationFile = _calibrationState.CalibrationFile;
            target.CalibrationData = _calibrationState.CalibrationData;
            target.OriginRow = ParseDouble(OriginRowText.Text, "OriginRow");
            target.OriginColumn = ParseDouble(OriginColumnText.Text, "OriginColumn");
        }

        // ======================= N 点标定页：采集 =======================

        private void TakeCurrentValue_Click(object sender, RoutedEventArgs e)
        {
            if (_context.LastRunContext == null)
            {
                ShowError("还没有运行结果：请先运行一次流程，再取当前值。");
                return;
            }
            try
            {
                double row = ReadSingleValue(PickRowVarCombo.Text, "行变量");
                double column = ReadSingleValue(PickColumnVarCombo.Text, "列变量");
                AddPoint(row, column, double.NaN, double.NaN);
                SetStatus($"已取当前值：图像 ({Format(row)}, {Format(column)})，请填写物理坐标。");
            }
            catch (InvalidOperationException ex)
            {
                ShowError(ex.Message);
            }
        }

        /// <summary>取单值：数组变量明确拒绝并提示引用具体元素（评审 P2-4）。</summary>
        private double ReadSingleValue(string pathText, string what)
        {
            string path = (pathText ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                throw new InvalidOperationException($"请先选择{what}");
            }
            object value;
            try
            {
                value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException || ex is ArgumentException)
            {
                throw new InvalidOperationException($"{what} {path} 无法读取：{ex.Message}");
            }
            if (value is IEnumerable sequence && !(value is string))
            {
                int count = sequence.Cast<object>().Count();
                throw new InvalidOperationException($"{what} {path} 是数组（{count} 个值），取当前值要求单值：请引用具体元素，如 {path}[0]");
            }
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                throw new InvalidOperationException($"{what} {path} 不是数值");
            }
        }

        private void PickOnImage_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowCalibrationImage())
            {
                ShowError("没有可显示的标定图像：请先加载图像或运行一次流程。");
                return;
            }
            _pickActive = true;
            _pickingRow = null;
            CalibImageView.BeginPickPoint();
            SetStatus("请在图像上点击要采集的点（按住拖动可微调）。");
        }

        private void OnImagePointPicked(object sender, ImagePointEventArgs e)
        {
            if (!_pickActive)
            {
                return;
            }
            if (_pickingRow == null)
            {
                _pickingRow = AddPoint(e.Row, e.Column, double.NaN, double.NaN);
            }
            else
            {
                _pickingRow.Row = e.Row;
                _pickingRow.Column = e.Column;
                PointsChanged();
            }
            SetStatus($"已在图像上取点：({Format(e.Row)}, {Format(e.Column)})，请填写物理坐标。");
        }

        private void AddManualPoint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AddPoint(ParseDouble(ManualRowText.Text, "图像行"), ParseDouble(ManualColumnText.Text, "图像列"),
                    ParseDouble(ManualXText.Text, "物理 X"), ParseDouble(ManualYText.Text, "物理 Y"));
                SetStatus($"已手动添加第 {_points.Count} 个点。");
            }
            catch (FormatException ex)
            {
                ShowError(ex.Message);
            }
        }

        private void ImportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "导入物理坐标 CSV", Filter = "CSV 文件 (*.csv;*.txt)|*.csv;*.txt|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            try
            {
                List<double[]> records = ReadCsv(dialog.FileName);
                int width = records.Count == 0 ? 0 : records[0].Length;
                if (records.Count == 0 || records.Any(r => r.Length != width) || width < 2 || width > 4)
                {
                    ShowError("CSV 格式不对：每行应为“X,Y”、“序号,X,Y”或“行,列,X,Y”，且各行列数一致（标题行可省略）。");
                    return;
                }
                for (int i = 0; i < records.Count; i++)
                {
                    double[] r = records[i];
                    PointRow point = i < _points.Count ? _points[i] : AddPoint(double.NaN, double.NaN, double.NaN, double.NaN, refresh: false);
                    if (width == 4)
                    {
                        point.Row = r[0];
                        point.Column = r[1];
                    }
                    point.X = r[width - 2];
                    point.Y = r[width - 1];
                }
                PointsChanged();
                SetStatus($"已从 CSV 导入 {records.Count} 行{(width == 4 ? "点对" : "物理坐标")}：{Path.GetFileName(dialog.FileName)}");
            }
            catch (IOException ex)
            {
                ShowError("读取 CSV 失败：" + ex.Message);
            }
        }

        /// <summary>读取数值行（逗号、分号、制表符或空格分隔），跳过空行与标题等非数值行。</summary>
        private static List<double[]> ReadCsv(string path)
        {
            var records = new List<double[]>();
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split(new[] { ',', ';', '\t', ' ', '，' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }
                var values = new double[parts.Length];
                bool numeric = true;
                for (int i = 0; i < parts.Length && numeric; i++)
                {
                    numeric = double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
                }
                if (numeric)
                {
                    records.Add(values);
                }
            }
            return records;
        }

        private PointRow AddPoint(double row, double column, double x, double y, bool refresh = true)
        {
            var point = new PointRow { Row = row, Column = column, X = x, Y = y };
            _points.Add(point);
            if (refresh)
            {
                PointsChanged();
            }
            return point;
        }

        private void DeletePoint_Click(object sender, RoutedEventArgs e)
        {
            if (!(PointGrid.SelectedItem is PointRow point))
            {
                SetStatus("请先在表格中选中要删除的点。");
                return;
            }
            _points.Remove(point);
            PointsChanged();
            SetStatus("已删除选中点，请重新计算。");
        }

        private void DeleteOutliers_Click(object sender, RoutedEventArgs e)
        {
            int removed = _points.RemoveAll(p => p.IsOutlier);
            PointsChanged();
            SetStatus(removed == 0 ? "没有超阈的点。" : $"已删除 {removed} 个超阈点，请重新计算。");
        }

        private void ClearPoints_Click(object sender, RoutedEventArgs e)
        {
            _points.Clear();
            PointsChanged();
            SetStatus("已清空点对。");
        }

        private void PointGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit)
            {
                Dispatcher.BeginInvoke(new Action(PointsChanged));
            }
        }

        /// <summary>点对有变化：作废上次求解结果（须重新计算才能保存 / 内嵌），刷新表格与图像十字。</summary>
        private void PointsChanged()
        {
            _solved = null;
            foreach (PointRow point in _points)
            {
                point.Residual = null;
                point.IsOutlier = false;
            }
            SolveResultText.Text = _points.Count == 0 ? string.Empty : "点对已修改，请重新计算。";
            MatrixText.Text = string.Empty;
            RefreshPoints();
        }

        private void RefreshPoints()
        {
            for (int i = 0; i < _points.Count; i++)
            {
                _points[i].Index = i;
            }
            PointGrid.ItemsSource = null;
            PointGrid.ItemsSource = _points;
            ShowPointCrosses();
        }

        // ======================= N 点标定页：求解与保存 =======================

        private void Solve_Click(object sender, RoutedEventArgs e)
        {
            double threshold;
            try
            {
                threshold = ParseDouble(ResidualThresholdText.Text, "残差阈值");
            }
            catch (FormatException ex)
            {
                ShowError(ex.Message);
                return;
            }
            try
            {
                List<CalibrationPoint> points = _points.Select(p => new CalibrationPoint { Row = p.Row, Column = p.Column, X = p.X, Y = p.Y }).ToList();
                Affine2DCalibration solved = CalibrationService.SolveAffine(points, SelectedTransform);
                ApplySolved(solved, threshold);
                int outliers = _points.Count(p => p.IsOutlier);
                SetStatus($"计算完成：{SolveResultText.Text}" + (outliers > 0 ? $"；{outliers} 个点残差超过阈值 {Format(threshold)}，已标红，可删除后重算。" : "。"));
            }
            catch (InvalidOperationException ex)
            {
                _solved = null;
                SolveResultText.Text = "无法计算：" + ex.Message;
                MatrixText.Text = string.Empty;
                ShowError("无法计算：" + ex.Message);
            }
        }

        private void ApplySolved(Affine2DCalibration solved, double threshold)
        {
            for (int i = 0; i < _points.Count; i++)
            {
                _points[i].Residual = solved.Points[i].Residual;
                _points[i].IsOutlier = threshold > 0 && solved.Points[i].Residual > threshold;
            }
            RefreshPoints();
            _solved = solved;
            SolveResultText.Text = $"{solved.TransformType}，{solved.Points.Count} 个点，RMS {FormatError(solved.RmsError)}，最大 {FormatError(solved.MaxError)}（{(string.IsNullOrWhiteSpace(UnitText.Text) ? "物理单位" : UnitText.Text.Trim())}）";
            MatrixText.Text = "矩阵：" + string.Join(", ", solved.HomMat2D.Select(v => v.ToString("G8", CultureInfo.InvariantCulture)));
        }

        private CalibrationResult BuildResult()
        {
            if (_solved == null)
            {
                ShowError(_points.Count == 0 ? "还没有点对。" : "请先计算（点对修改后需要重新计算）。");
                return null;
            }
            return CalibrationService.FromAffine(_solved, UnitText.Text, DescriptionText.Text);
        }

        private void SaveCalibrationFile_Click(object sender, RoutedEventArgs e)
        {
            CalibrationResult result = BuildResult();
            if (result == null)
            {
                return;
            }
            var dialog = new SaveFileDialog
            {
                Title = "保存标定文件",
                Filter = "标定文件 (*.vfcal.json)|*.vfcal.json|所有文件 (*.*)|*.*",
                FileName = (_standalone ? "标定" : (ModuleNameText.Text ?? "坐标转换").Trim()) + ".vfcal.json"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            try
            {
                CalibrationService.Save(dialog.FileName, result);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowError("保存失败：" + ex.Message);
                return;
            }
            if (_standalone)
            {
                SetStatus("已保存标定文件：" + dialog.FileName);
                return;
            }
            _loading = true;
            KindCombo.SelectedItem = KindChoices.First(c => c.Value == CalibrationKind.Affine2D);
            _loading = false;
            _calibrationState.CalibrationKind = CalibrationKind.Affine2D;
            SetFileSource(dialog.FileName);
            SetStatus("已保存标定文件，当前工具改用该文件（内嵌数据已清空）：" + dialog.FileName);
        }

        private void EmbedCalibration_Click(object sender, RoutedEventArgs e)
        {
            CalibrationResult result = BuildResult();
            if (result == null)
            {
                return;
            }
            _loading = true;
            KindCombo.SelectedItem = KindChoices.First(c => c.Value == CalibrationKind.Affine2D);
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == CalibrationSource.Embedded);
            _loading = false;
            _calibrationState.CalibrationKind = CalibrationKind.Affine2D;
            _calibrationState.UseEmbeddedCalibration(CalibrationService.ToJson(result));
            CalibrationFileText.Text = string.Empty;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
            SetStatus("已内嵌到当前工具（标定文件路径已清空），确定后生效。");
        }

        /// <summary>打开时若当前标定带点对，载入表格便于继续调整。</summary>
        private void LoadPointsFromCalibration()
        {
            CalibrationResult calibration = TryLoadPendingCalibration(out _);
            Affine2DCalibration affine = calibration?.Affine2D;
            if (affine == null || affine.Points.Count == 0)
            {
                return;
            }
            foreach (CalibrationPoint point in affine.Points)
            {
                _points.Add(new PointRow { Row = point.Row, Column = point.Column, X = point.X, Y = point.Y });
            }
            TransformTypeCombo.SelectedItem = TransformChoices.First(c => c.Value == affine.TransformType);
            if (!string.IsNullOrWhiteSpace(calibration.Unit))
            {
                UnitText.Text = calibration.Unit;
            }
            DescriptionText.Text = calibration.Description ?? string.Empty;
            double threshold = double.TryParse(ResidualThresholdText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double t) ? t : 0;
            ApplySolved(affine, threshold);
        }

        // ======================= 图像 =======================

        private void CalibImageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => ShowCalibrationImage()));
        }

        private bool ShowCalibrationImage()
        {
            string path = (CalibImageCombo.Text ?? string.Empty).Trim();
            HObject image = null;
            if (path.Equals("Input.Image", StringComparison.OrdinalIgnoreCase))
            {
                image = _context.InputImage;
            }
            else if (_context.LastRunContext != null && path.Length > 0)
            {
                try
                {
                    image = (VariableReference.Parse(path).Resolve(_context.LastRunContext) as HalconImage)?.Object;
                }
                catch (Exception)
                {
                    image = null;
                }
            }
            if (image == null || !image.IsInitialized())
            {
                return false;
            }
            CalibImageView.ShowImage(image);
            ShowPointCrosses();
            return true;
        }

        private void ShowPointCrosses()
        {
            List<PointRow> valid = _points.Where(p => !double.IsNaN(p.Row) && !double.IsNaN(p.Column)).ToList();
            if (valid.Count == 0)
            {
                CalibImageView.ClearOverlay();
                return;
            }
            HOperatorSet.GenCrossContourXld(out HObject crosses, new HTuple(valid.Select(p => p.Row).ToArray()),
                new HTuple(valid.Select(p => p.Column).ToArray()), 16, 0.785398);
            CalibImageView.SetOverlay(crosses);
            crosses.Dispose();
        }

        // ======================= 工具方法 =======================

        private static string NullIfEmpty(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static double ParseDouble(string text, string name)
        {
            if (double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double value))
            {
                return value;
            }
            throw new FormatException($"{name} 不是有效数字。");
        }

        private static string Format(double value)
        {
            return value.ToString("G", CultureInfo.CurrentCulture);
        }

        private static string FormatError(double? value)
        {
            return value.HasValue ? value.Value.ToString("F4", CultureInfo.CurrentCulture) : "无";
        }

        private void SetStatus(string message)
        {
            StatusText.Text = message;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(message);
        }
    }
}
