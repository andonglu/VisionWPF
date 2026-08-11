using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Controls;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Ui
{
    /// <summary>模板匹配工具配置页：窗体负责创建/测试模板，工具只保存运行所需模型数据和搜索参数。</summary>
    public sealed class FrmMatchToolEdit : Form
    {
        private sealed class TeachSettings
        {
            public double AngleStart = -0.39;
            public double AngleExtent = 0.79;
            public double AngleStep = 0.01;
            public int NumLevels = 0;
            public string Optimization = "auto";
            public string Metric = "use_polarity";
            public int Contrast = -1;
            public int MinContrast = -1;
        }

        private static readonly TeachSettings LastTeachSettings = new TeachSettings();

        private readonly HalconModelMatchTool _tool;
        private readonly ToolEditContext _context;
        private readonly RoiEditorControl _roiEditor;
        private readonly DataGridView _resultGrid;
        private readonly HalconImageView _runView;
        private readonly DataGridView _runResultGrid;
        private Label _modelState;

        private TextBox _moduleName;
        private ComboBox _imagePath;
        private NumericUpDown _findStartAngle;
        private NumericUpDown _findExtentAngle;
        private NumericUpDown _minScore;
        private NumericUpDown _numMatches;
        private NumericUpDown _maxOverlap;
        private ComboBox _subPixel;
        private NumericUpDown _numLevelsFind;
        private NumericUpDown _greediness;
        private NumericUpDown _baseRow;
        private NumericUpDown _baseColumn;
        private NumericUpDown _baseAngle;

        private NumericUpDown _teachAngleStart;
        private NumericUpDown _teachAngleExtent;
        private NumericUpDown _teachAngleStep;
        private NumericUpDown _teachNumLevels;
        private NumericUpDown _teachContrast;
        private NumericUpDown _teachMinContrast;
        private ComboBox _teachOptimization;
        private ComboBox _teachMetric;

        private byte[] _modelData;
        private readonly List<MatchResultItem> _lastMatches = new List<MatchResultItem>();

        public FrmMatchToolEdit(HalconModelMatchTool tool, ToolEditContext context)
        {
            _tool = tool;
            _context = context;
            _modelData = tool.ShapeModelData;

            Text = $"模板匹配 - {tool.ModuleName}";
            Width = 1280;
            Height = 820;
            StartPosition = FormStartPosition.CenterParent;

            var tabs = new TabControl { Dock = DockStyle.Fill };
            Controls.Add(tabs);
            var teachTab = new TabPage("示教设置");
            var runTab = new TabPage("运行设置");
            tabs.TabPages.Add(teachTab);
            tabs.TabPages.Add(runTab);

            var teachMain = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            teachMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            teachMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            teachTab.Controls.Add(teachMain);
            _roiEditor = new RoiEditorControl { Dock = DockStyle.Fill };
            teachMain.Controls.Add(_roiEditor, 0, 0);

            var teachRight = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                ColumnCount = 1,
                Padding = new Padding(8)
            };
            teachRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
            teachRight.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            teachRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            teachMain.Controls.Add(teachRight, 1, 0);

            teachRight.Controls.Add(BuildTeachGroup(), 0, 0);

            _resultGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _resultGrid.Columns.Add("Index", "序号");
            _resultGrid.Columns.Add("Row", "Row");
            _resultGrid.Columns.Add("Column", "Column");
            _resultGrid.Columns.Add("Angle", "Angle");
            _resultGrid.Columns.Add("Score", "Score");
            teachRight.Controls.Add(_resultGrid, 0, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 90 };
            var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90 };
            var baseButton = new Button { Text = "选中结果设为基准", Width = 130 };
            baseButton.Click += (s, e) => SetSelectedAsBasePose();
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(baseButton);
            teachRight.Controls.Add(buttons, 0, 2);
            AcceptButton = okButton;
            CancelButton = cancelButton;

            var runMain = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            runMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            runMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            runTab.Controls.Add(runMain);
            _runView = new HalconImageView { Dock = DockStyle.Fill };
            runMain.Controls.Add(_runView, 0, 0);

            var runRight = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(8)
            };
            runRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
            runRight.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            runRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            runMain.Controls.Add(runRight, 1, 0);
            runRight.Controls.Add(BuildRunGroup(), 0, 0);
            _runResultGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            runRight.Controls.Add(_runResultGrid, 0, 1);

            var runButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            runButtons.Controls.Add(new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 90 });
            runButtons.Controls.Add(new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90 });
            var runTestButton = new Button { Text = "执行测试", Width = 90 };
            runTestButton.Click += (s, e) => TestMatchRun();
            runButtons.Controls.Add(runTestButton);
            runRight.Controls.Add(runButtons, 0, 2);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            TryShowSelectedImage();
            ShowLastRunResult();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                ApplyToTool();
            }
            base.OnClosing(e);
        }

        private GroupBox BuildRunGroup()
        {
            var group = new GroupBox { Text = "运行参数（保存到工具）", Dock = DockStyle.Fill };
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(6) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            group.Controls.Add(panel);

            int row = 0;
            _moduleName = AddText(panel, row, 0, "模块名", _tool.ModuleName);
            _imagePath = AddImageInputCombo(panel, row++, 2, _tool.ImagePath);
            AddBrowseButton(panel, 0, 3, _imagePath, "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*", TryShowSelectedImage);

            _findStartAngle = AddNum(panel, row, 0, "起始角", _tool.FindStartAngle, -10, 10, 3, 0.01);
            _findExtentAngle = AddNum(panel, row++, 2, "角范围", _tool.FindExtentAngle, -10, 10, 3, 0.01);
            _minScore = AddNum(panel, row, 0, "最小分", _tool.MinScore, 0, 1, 3, 0.01);
            _numMatches = AddNum(panel, row++, 2, "数量", _tool.NumMatches, 1, 999, 0, 1);
            _maxOverlap = AddNum(panel, row, 0, "重叠", _tool.MaxOverlap, 0, 1, 3, 0.01);
            _greediness = AddNum(panel, row++, 2, "贪婪度", _tool.Greediness, 0, 1, 3, 0.01);
            _numLevelsFind = AddNum(panel, row, 0, "金字塔", _tool.NumLevelsFind, 0, 10, 0, 1);
            _subPixel = AddCombo(panel, row++, 2, "亚像素", _tool.SubPixel, "least_squares", "least_squares_high", "least_squares_very_high", "interpolation");
            _modelState = new Label
            {
                Text = HasModel ? "模型：已内嵌" : "模型：未创建",
                ForeColor = HasModel ? Color.Green : Color.Red,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            };
            panel.Controls.Add(_modelState, 0, row);
            panel.SetColumnSpan(_modelState, 4);

            return group;
        }

        private GroupBox BuildTeachGroup()
        {
            var group = new GroupBox { Text = "创建模板/基准姿态", Dock = DockStyle.Fill };
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(6) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            group.Controls.Add(panel);

            int row = 0;
            _teachAngleStart = AddNum(panel, row, 0, "起始角", LastTeachSettings.AngleStart, -10, 10, 3, 0.01);
            _teachAngleExtent = AddNum(panel, row++, 2, "角范围", LastTeachSettings.AngleExtent, -10, 10, 3, 0.01);
            _teachAngleStep = AddNum(panel, row, 0, "角步长", LastTeachSettings.AngleStep, -1, 1, 4, 0.001);
            _teachNumLevels = AddNum(panel, row++, 2, "金字塔", LastTeachSettings.NumLevels, -1, 10, 0, 1);
            _teachContrast = AddNum(panel, row, 0, "对比度", LastTeachSettings.Contrast, -4, 255, 0, 1);
            _teachMinContrast = AddNum(panel, row++, 2, "最小对比", LastTeachSettings.MinContrast, -1, 255, 0, 1);
            _teachOptimization = AddCombo(panel, row, 0, "优化", LastTeachSettings.Optimization, "auto", "none", "point_reduction_low", "point_reduction_medium", "point_reduction_high");
            _teachMetric = AddCombo(panel, row++, 2, "极性", LastTeachSettings.Metric, "use_polarity", "ignore_global_polarity", "ignore_local_polarity", "ignore_color_polarity");
            _baseRow = AddNum(panel, row, 0, "基准Row", _tool.BaseRow, -100000, 100000, 3, 0.1);
            _baseColumn = AddNum(panel, row++, 2, "基准Col", _tool.BaseColumn, -100000, 100000, 3, 0.1);
            _baseAngle = AddNum(panel, row, 0, "基准角", _tool.BaseAngle, -10, 10, 5, 0.001);
            var baseHint = new Label { Text = "可从下方测试结果选中后设为基准", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            panel.Controls.Add(baseHint, 2, row++);
            panel.SetColumnSpan(baseHint, 2);

            var createButton = new Button { Text = "创建模板", Dock = DockStyle.Fill };
            createButton.Click += (s, e) => CreateTemplate();
            panel.Controls.Add(createButton, 0, row);
            panel.SetColumnSpan(createButton, 4);

            return group;
        }

        private bool HasModel => _modelData != null && _modelData.Length > 0;

        private void TryShowSelectedImage()
        {
            string path = _imagePath.Text.Trim();
            if (path.Equals("Input.Image", StringComparison.OrdinalIgnoreCase))
            {
                if (_context.InputImage != null && _context.InputImage.IsInitialized())
                {
                    _roiEditor.ShowImage(_context.InputImage);
                    _runView?.ShowImage(_context.InputImage);
                }
                return;
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            HOperatorSet.ReadImage(out HObject image, path);
            try
            {
                _roiEditor.ShowImage(image);
                _runView?.ShowImage(image);
            }
            finally
            {
                image.Dispose();
            }
        }

        private void ShowLastRunResult()
        {
            _runResultGrid.Columns.Clear();
            _runResultGrid.Rows.Clear();
            if (_context.LastRunContext == null)
            {
                return;
            }

            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "Image", out Variable imageVar)
                && imageVar.Value is HalconImage image)
            {
                _runView.ShowImage(image.Object);
            }
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "ResultContour", out Variable contourVar)
                && contourVar.Value is HObject contour)
            {
                _runView.SetOverlay(contour);
            }
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "Items", out Variable itemsVar))
            {
                _runResultGrid.Columns.Add("Index", "序号");
                _runResultGrid.Columns.Add("Row", "Row");
                _runResultGrid.Columns.Add("Column", "Column");
                _runResultGrid.Columns.Add("Angle", "Angle");
                _runResultGrid.Columns.Add("Score", "Score");
                if (itemsVar.Value is MatchResultItem[] items)
                {
                    foreach (MatchResultItem item in items)
                    {
                        _runResultGrid.Rows.Add(item.Index, item.Row, item.Column, item.Angle, item.Score);
                    }
                }
                else if (itemsVar.Value is List<MatchResultItem> list)
                {
                    foreach (MatchResultItem item in list)
                    {
                        _runResultGrid.Rows.Add(item.Index, item.Row, item.Column, item.Angle, item.Score);
                    }
                }
            }
        }

        private void CreateTemplate()
        {
            if (_roiEditor.ImageView.ImageObject == null)
            {
                MessageBox.Show(this, "请先打开或显示一张图像。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SaveTeachSettings();
            using HRegion region = _roiEditor.BuildRegion();
            HOperatorSet.AreaCenter(region, out HTuple area, out _, out _);
            if (area.Length == 0 || area.D <= 0)
            {
                MessageBox.Show(this, "请先绘制模板区域 ROI。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            HOperatorSet.ReduceDomain(_roiEditor.ImageView.ImageObject, region, out HObject reduced);
            try
            {
                HOperatorSet.CreateShapeModel(reduced,
                    ToAuto((int)_teachNumLevels.Value),
                    (double)_teachAngleStart.Value,
                    (double)_teachAngleExtent.Value,
                    ToAuto((double)_teachAngleStep.Value),
                    _teachOptimization.Text,
                    _teachMetric.Text,
                    ToContrast((int)_teachContrast.Value),
                    ToAuto((int)_teachMinContrast.Value),
                    out HTuple modelId);
                try
                {
                    _modelData = ShapeModelSerialization.Serialize(modelId);
                    _modelState.Text = $"模型：已创建 ({_modelData.Length} bytes)";
                    _modelState.ForeColor = Color.Green;
                    TestMatchTeach();
                }
                finally
                {
                    HOperatorSet.ClearShapeModel(modelId);
                }
            }
            finally
            {
                reduced.Dispose();
            }
        }

        private void TestMatchTeach()
        {
            if (_roiEditor.ImageView.ImageObject == null)
            {
                MessageBox.Show(this, "请先打开或显示一张图像。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ExecuteMatch(_roiEditor.ImageView.ImageObject, _roiEditor.ImageView, _resultGrid,
                (double)_teachAngleStart.Value,
                (double)_teachAngleExtent.Value,
                0.5,
                10,
                0.5,
                "least_squares",
                0,
                0.9,
                updateTeachMatches: true);
        }

        private void TestMatchRun()
        {
            if (_runView.ImageObject == null)
            {
                TryShowSelectedImage();
            }
            if (_runView.ImageObject == null)
            {
                MessageBox.Show(this, "请先选择运行图像。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ExecuteMatch(_runView.ImageObject, _runView, _runResultGrid,
                (double)_findStartAngle.Value,
                (double)_findExtentAngle.Value,
                (double)_minScore.Value,
                (int)_numMatches.Value,
                (double)_maxOverlap.Value,
                _subPixel.Text,
                (int)_numLevelsFind.Value,
                (double)_greediness.Value,
                updateTeachMatches: false);
        }

        private void ExecuteMatch(HObject image, HalconImageView view, DataGridView grid,
            double startAngle, double extentAngle, double minScore, int numMatches,
            double maxOverlap, string subPixel, int numLevels, double greediness, bool updateTeachMatches)
        {
            if (!HasModel)
            {
                MessageBox.Show(this, "请先创建模板。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            HObject modelContours = null;
            HObject resultContour = null;
            HTuple modelId = ShapeModelSerialization.Deserialize(_modelData);
            try
            {
                HOperatorSet.GetShapeModelContours(out modelContours, modelId, 1);
                HOperatorSet.FindShapeModel(image, modelId,
                    startAngle,
                    extentAngle,
                    minScore,
                    numMatches,
                    maxOverlap,
                    subPixel,
                    numLevels,
                    greediness,
                    out HTuple rows, out HTuple columns, out HTuple angles, out HTuple scores);

                if (updateTeachMatches)
                {
                    _lastMatches.Clear();
                }
                ResetMatchGrid(grid);
                HOperatorSet.GenEmptyObj(out resultContour);

                for (int i = 0; i < rows.Length; i++)
                {
                    HOperatorSet.VectorAngleToRigid(0, 0, 0, rows[i], columns[i], angles[i], out HTuple mat);
                    HOperatorSet.AffineTransContourXld(modelContours, out HObject contour, mat);
                    HOperatorSet.ConcatObj(resultContour, contour, out resultContour);
                    contour.Dispose();

                    var item = new MatchResultItem
                    {
                        Index = i,
                        Row = rows[i].D,
                        Column = columns[i].D,
                        Angle = angles[i].D,
                        Score = scores[i].D
                    };
                    if (updateTeachMatches)
                    {
                        _lastMatches.Add(item);
                    }
                    grid.Rows.Add(i, item.Row.ToString("F3"), item.Column.ToString("F3"),
                        item.Angle.ToString("F5"), item.Score.ToString("F4"));
                }

                view.SetOverlay(resultContour);
            }
            finally
            {
                modelContours?.Dispose();
                resultContour?.Dispose();
                HOperatorSet.ClearShapeModel(modelId);
            }
        }

        private static void ResetMatchGrid(DataGridView grid)
        {
            grid.Columns.Clear();
            grid.Rows.Clear();
            grid.Columns.Add("Index", "序号");
            grid.Columns.Add("Row", "Row");
            grid.Columns.Add("Column", "Column");
            grid.Columns.Add("Angle", "Angle");
            grid.Columns.Add("Score", "Score");
        }

        private void SetSelectedAsBasePose()
        {
            if (_resultGrid.SelectedRows.Count == 0)
            {
                return;
            }

            int index = Convert.ToInt32(_resultGrid.SelectedRows[0].Cells[0].Value);
            if (index < 0 || index >= _lastMatches.Count)
            {
                return;
            }

            MatchResultItem item = _lastMatches[index];
            _baseRow.Value = Clamp(_baseRow, item.Row);
            _baseColumn.Value = Clamp(_baseColumn, item.Column);
            _baseAngle.Value = Clamp(_baseAngle, item.Angle);
        }

        private void ApplyToTool()
        {
            _tool.ModuleName = _moduleName.Text.Trim();
            _tool.ImagePath = _imagePath.Text.Trim();
            _tool.ShapeModelData = _modelData;
            _tool.FindStartAngle = (double)_findStartAngle.Value;
            _tool.FindExtentAngle = (double)_findExtentAngle.Value;
            _tool.MinScore = (double)_minScore.Value;
            _tool.NumMatches = (int)_numMatches.Value;
            _tool.MaxOverlap = (double)_maxOverlap.Value;
            _tool.SubPixel = _subPixel.Text;
            _tool.NumLevelsFind = (int)_numLevelsFind.Value;
            _tool.Greediness = (double)_greediness.Value;
            _tool.BaseRow = (double)_baseRow.Value;
            _tool.BaseColumn = (double)_baseColumn.Value;
            _tool.BaseAngle = (double)_baseAngle.Value;
        }

        private void SaveTeachSettings()
        {
            LastTeachSettings.AngleStart = (double)_teachAngleStart.Value;
            LastTeachSettings.AngleExtent = (double)_teachAngleExtent.Value;
            LastTeachSettings.AngleStep = (double)_teachAngleStep.Value;
            LastTeachSettings.NumLevels = (int)_teachNumLevels.Value;
            LastTeachSettings.Optimization = _teachOptimization.Text;
            LastTeachSettings.Metric = _teachMetric.Text;
            LastTeachSettings.Contrast = (int)_teachContrast.Value;
            LastTeachSettings.MinContrast = (int)_teachMinContrast.Value;
        }

        private static HTuple ToAuto(int value)
        {
            return value < 0 ? new HTuple("auto") : new HTuple(value);
        }

        private static HTuple ToAuto(double value)
        {
            return value < 0 ? new HTuple("auto") : new HTuple(value);
        }

        private static HTuple ToContrast(int value)
        {
            switch (value)
            {
                case -1:
                    return "auto";
                case -2:
                    return "auto_contrast";
                case -3:
                    return "auto_contrast_hyst";
                case -4:
                    return "auto_min_size";
                default:
                    return value;
            }
        }

        private static decimal Clamp(NumericUpDown num, double value)
        {
            decimal decimalValue = (decimal)value;
            if (decimalValue < num.Minimum) return num.Minimum;
            if (decimalValue > num.Maximum) return num.Maximum;
            return decimalValue;
        }

        private static TextBox AddText(TableLayoutPanel panel, int row, int col, string label, string value)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, col, row);
            var text = new TextBox { Text = value ?? string.Empty, Dock = DockStyle.Fill };
            panel.Controls.Add(text, col + 1, row);
            return text;
        }

        private ComboBox AddImageInputCombo(TableLayoutPanel panel, int row, int col, string value)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            panel.Controls.Add(new Label { Text = "图像输入", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, col, row);
            var combo = new ComboBox { Text = value ?? string.Empty, Dock = DockStyle.Fill };
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                combo.Items.Add("Input.Image");
            }
            foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HalconImage)))
            {
                combo.Items.Add(candidate.Path);
            }
            if (string.IsNullOrWhiteSpace(combo.Text) && combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
            combo.SelectedIndexChanged += (s, e) => TryShowSelectedImage();
            panel.Controls.Add(combo, col + 1, row);
            return combo;
        }

        private static NumericUpDown AddNum(TableLayoutPanel panel, int row, int col, string label, double value,
            double min, double max, int decimals, double increment)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, col, row);
            var num = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = (decimal)min,
                Maximum = (decimal)max,
                DecimalPlaces = decimals,
                Increment = (decimal)increment,
                Value = ClampValue(value, min, max)
            };
            panel.Controls.Add(num, col + 1, row);
            return num;
        }

        private static ComboBox AddCombo(TableLayoutPanel panel, int row, int col, string label, string value, params string[] items)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, col, row);
            var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(items);
            combo.Text = string.IsNullOrEmpty(value) ? items[0] : value;
            if (combo.SelectedIndex < 0)
            {
                combo.SelectedIndex = 0;
            }
            panel.Controls.Add(combo, col + 1, row);
            return combo;
        }

        private static void AddBrowseButton(TableLayoutPanel panel, int row, int col, ComboBox target, string filter, Action afterSelect)
        {
            var button = new Button { Text = "...", Dock = DockStyle.Right, Width = 28 };
            button.Click += (s, e) =>
            {
                using var dialog = new OpenFileDialog { Filter = filter };
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    target.Text = dialog.FileName;
                    afterSelect?.Invoke();
                }
            };
            Control original = panel.GetControlFromPosition(col, row);
            panel.Controls.Remove(original);
            var host = new Panel { Dock = DockStyle.Fill };
            original.Dock = DockStyle.Fill;
            host.Controls.Add(original);
            host.Controls.Add(button);
            button.BringToFront();
            panel.Controls.Add(host, col, row);
        }

        private static decimal ClampValue(double value, double min, double max)
        {
            return (decimal)Math.Max(min, Math.Min(max, value));
        }
    }
}
