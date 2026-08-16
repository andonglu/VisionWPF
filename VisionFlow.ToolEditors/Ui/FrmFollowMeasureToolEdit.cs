using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Controls;
using VisionFlow.Controls.Roi;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Ui
{
    public sealed class FrmFollowMeasureToolEdit : Form
    {
        private const string OptionalNoneText = "(不使用)";
        private const string TeachBaseText = "基准图像示教";
        private const string TeachCurrentText = "当前图像示教(按当前姿态反算回基准)";

        private readonly FollowMeasureToolBase _tool;
        private readonly ToolEditContext _context;
        private readonly RoiEditorControl _teachEditor;
        private readonly HalconImageView _runView;
        private readonly DataGridView _resultGrid;
        private readonly TextBox _moduleName;
        private readonly ComboBox _teachImagePath;
        private readonly ComboBox _teachMode;
        private readonly ComboBox _teachPosePath;
        private readonly ComboBox _runMatrixPath;
        private readonly NumericUpDown _measureLength1;
        private readonly NumericUpDown _measureLength2;
        private readonly NumericUpDown _measureSigma;
        private readonly NumericUpDown _measureThreshold;
        private readonly ComboBox _transition;
        private readonly ComboBox _select;
        private readonly NumericUpDown _startPhi;
        private readonly NumericUpDown _endPhi;
        private readonly NumericUpDown _caliperCount;

        public FrmFollowMeasureToolEdit(FollowMeasureToolBase tool, ToolEditContext context)
        {
            _tool = tool;
            _context = context;
            Text = $"{GetTitle(tool)} - {tool.ModuleName}";
            Width = 1180;
            Height = 760;
            StartPosition = FormStartPosition.CenterParent;

            var tabs = new TabControl { Dock = DockStyle.Fill };
            Controls.Add(tabs);

            var teachTab = new TabPage("示教设置");
            var runTab = new TabPage("运行设置");
            tabs.TabPages.Add(teachTab);
            tabs.TabPages.Add(runTab);

            var teachSplit = CreateEditorSplit();
            teachTab.Controls.Add(teachSplit);
            _teachEditor = new RoiEditorControl { Dock = DockStyle.Fill };
            _teachEditor.ImageView.RoiChanged += (s, e) => UpdateCaliperOverlay();
            teachSplit.Panel1.Controls.Add(_teachEditor);

            var teachLayout = CreateSettingsLayout();
            teachSplit.Panel2.Controls.Add(teachLayout);
            int row = 0;
            _moduleName = AddTextRow(teachLayout, row++, "模块名", tool.ModuleName);
            _teachImagePath = AddRefRow(teachLayout, row++, "示教图像",
                RefCandidateService.ForInput(context.Root, context.Node, typeof(HalconImage)),
                tool.ImagePath, optional: false, includeInputImage: context.InputImage != null && context.InputImage.IsInitialized());
            _teachImagePath.SelectedIndexChanged += (s, e) => TryShowTeachImage();

            _teachMode = AddComboRow(teachLayout, row++, "示教模式", TeachBaseText, TeachBaseText, TeachCurrentText);
            _teachMode.SelectedIndexChanged += (s, e) => RefreshTeachRoiFromBase();
            _teachPosePath = AddRefRow(teachLayout, row++, "当前姿态",
                MatrixCandidates(includeCollections: false, expandCollections: true),
                string.Empty, optional: true, includeInputImage: false);
            _teachPosePath.SelectedIndexChanged += (s, e) => RefreshTeachRoiFromBase();

            var drawButton = new Button { Text = $"绘制/重绘{GetShapeName(tool)}基准", Dock = DockStyle.Fill };
            drawButton.Click += (s, e) => BeginDrawBaseRoi();
            teachLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            teachLayout.Controls.Add(drawButton, 1, row++);

            AddHint(teachLayout, row++, "说明", "基准图像示教会直接保存ROI；当前图像示教会用当前姿态的逆矩阵反算回基准ROI。");
            var saveTeachButton = new Button { Text = "保存示教", Dock = DockStyle.Fill };
            saveTeachButton.Click += (s, e) =>
            {
                if (SaveBaseRoi())
                {
                    MessageBox.Show(this, "示教基准已保存到当前工具。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            teachLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            teachLayout.Controls.Add(saveTeachButton, 1, row++);

            var runSplit = CreateEditorSplit();
            runTab.Controls.Add(runSplit);
            _runView = new HalconImageView { Dock = DockStyle.Fill };
            runSplit.Panel1.Controls.Add(_runView);

            var runPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            runPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));
            runPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            runSplit.Panel2.Controls.Add(runPanel);

            var runLayout = CreateSettingsLayout();
            runPanel.Controls.Add(runLayout, 0, 0);
            row = 0;
            _runMatrixPath = AddRefRow(runLayout, row++, "运行跟随",
                MatrixCandidates(includeCollections: true, expandCollections: false),
                tool.MatrixPath, optional: true, includeInputImage: false);
            _measureLength1 = AddNumRow(runLayout, row++, "卡尺长度1", tool.MeasureLength1, 2);
            _measureLength2 = AddNumRow(runLayout, row++, "卡尺长度2", tool.MeasureLength2, 2);
            _measureSigma = AddNumRow(runLayout, row++, "平滑Sigma", tool.MeasureSigma, 2);
            _measureThreshold = AddNumRow(runLayout, row++, "边缘阈值", tool.MeasureThreshold, 2);
            _transition = AddComboRow(runLayout, row++, "边缘极性", tool.MeasureTransition, "all", "positive", "negative");
            _select = AddComboRow(runLayout, row++, "边缘选择", tool.MeasureSelect, "all", "first", "last");

            _startPhi = null;
            _endPhi = null;
            _caliperCount = null;
            if (tool is CircleFollowMeasureTool circle)
            {
                _startPhi = AddNumRow(runLayout, row++, "起始角", circle.StartPhi, 4);
                _endPhi = AddNumRow(runLayout, row++, "结束角", circle.EndPhi, 4);
                _startPhi.ValueChanged += (s, e) => UpdateCaliperOverlay();
                _endPhi.ValueChanged += (s, e) => UpdateCaliperOverlay();
            }
            else if (tool is ArcCaliperFollowMeasureTool arc)
            {
                _startPhi = AddNumRow(runLayout, row++, "起始角", arc.StartPhi, 4);
                _endPhi = AddNumRow(runLayout, row++, "结束角", arc.EndPhi, 4);
                _caliperCount = AddNumRow(runLayout, row++, "卡尺数量", arc.CaliperCount, 0);
                _caliperCount.Minimum = 1;
                _caliperCount.Maximum = 1000;
                _startPhi.ValueChanged += (s, e) => UpdateCaliperOverlay();
                _endPhi.ValueChanged += (s, e) => UpdateCaliperOverlay();
                _caliperCount.ValueChanged += (s, e) => UpdateCaliperOverlay();
            }

            _measureLength1.ValueChanged += (s, e) => UpdateCaliperOverlay();
            _measureLength2.ValueChanged += (s, e) => UpdateCaliperOverlay();

            var runTestButton = new Button { Text = "执行测试", Dock = DockStyle.Fill };
            runTestButton.Click += (s, e) => TestMeasureRun();
            runLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            runLayout.Controls.Add(runTestButton, 1, row++);

            _resultGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            runPanel.Controls.Add(_resultGrid, 0, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90 };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            Controls.Add(buttons);
            buttons.BringToFront();
            AcceptButton = ok;
            CancelButton = cancel;

            TryShowTeachImage();
            RefreshTeachRoiFromBase();
            ShowLastRunResult();

            Shown += (s, e) =>
            {
                EnsureSettingsPanelVisible(teachSplit);
                EnsureSettingsPanelVisible(runSplit);
            };
            Resize += (s, e) =>
            {
                EnsureSettingsPanelVisible(teachSplit);
                EnsureSettingsPanelVisible(runSplit);
            };
        }

        private static SplitContainer CreateEditorSplit()
        {
            return new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel2
            };
        }

        private static void EnsureSettingsPanelVisible(SplitContainer split)
        {
            const int minSettingsWidth = 360;
            if (split.Width <= split.Panel1MinSize + minSettingsWidth + split.SplitterWidth)
            {
                return;
            }
            if (split.Panel2MinSize != minSettingsWidth)
            {
                split.Panel2MinSize = minSettingsWidth;
            }
            int settingsWidth = Math.Min(420, Math.Max(360, split.Width / 3));
            int distance = Math.Max(split.Panel1MinSize, split.Width - settingsWidth - split.SplitterWidth);
            int maxDistance = split.Width - split.Panel2MinSize - split.SplitterWidth;
            if (distance > maxDistance)
            {
                distance = maxDistance;
            }
            if (distance >= split.Panel1MinSize && distance <= maxDistance)
            {
                split.SplitterDistance = distance;
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                if (!SaveBaseRoi())
                {
                    e.Cancel = true;
                    MessageBox.Show(this, $"请先绘制一个{GetShapeName(_tool)}基准 ROI。", "测量基准未设置",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                ApplyRunSettingsToTool();
            }
            base.OnClosing(e);
        }

        private void TestMeasureRun()
        {
            if (!SaveBaseRoi())
            {
                MessageBox.Show(this, $"请先绘制一个{GetShapeName(_tool)}基准 ROI。", "测量基准未设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ApplyRunSettingsToTool();

            FlowContext ctx = _context.LastRunContext ?? new FlowContext();
            if (_context.InputImage != null && _context.InputImage.IsInitialized()
                && !ctx.TryGetVariable("Input", "Image", out _))
            {
                ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(_context.InputImage), 1));
            }
            ClearMeasureResults(ctx);
            NodeResult result = _tool.Run(ctx);
            _context.LastRunContext = ctx;
            ShowLastRunResult();
            if (!result.IsSuccess)
            {
                MessageBox.Show(this, result.Message, "测试失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ApplyRunSettingsToTool()
        {
            _tool.ModuleName = _moduleName.Text.Trim();
            _tool.ImagePath = RefText(_teachImagePath);
            _tool.MatrixPath = RefText(_runMatrixPath);
            _tool.IndexPath = string.Empty;
            _tool.MeasureLength1 = (double)_measureLength1.Value;
            _tool.MeasureLength2 = (double)_measureLength2.Value;
            _tool.MeasureSigma = (double)_measureSigma.Value;
            _tool.MeasureThreshold = (double)_measureThreshold.Value;
            _tool.MeasureTransition = _transition.Text.Trim();
            _tool.MeasureSelect = _select.Text.Trim();
            if (_tool is CircleFollowMeasureTool circle)
            {
                circle.StartPhi = (double)_startPhi.Value;
                circle.EndPhi = (double)_endPhi.Value;
            }
            else if (_tool is ArcCaliperFollowMeasureTool arc)
            {
                arc.StartPhi = (double)_startPhi.Value;
                arc.EndPhi = (double)_endPhi.Value;
                arc.CaliperCount = (int)_caliperCount.Value;
            }
        }

        private void ClearMeasureResults(FlowContext ctx)
        {
            if (_tool is LineFollowMeasureTool)
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<LineMeasureResult>(), 0));
            }
            else if (_tool is RectangleFollowMeasureTool)
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<RectangleMeasureResult>(), 0));
            }
            else if (_tool is OneDCaliperFollowMeasureTool)
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<OneDCaliperMeasureResult>(), 0));
            }
            else if (_tool is ArcCaliperFollowMeasureTool)
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<OneDCaliperMeasureResult>(), 0));
            }
            else if (_tool is CircleFollowMeasureTool)
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<CircleMeasureResult>(), 0));
            }
        }

        private void TryShowTeachImage()
        {
            HObject image = ResolveImage(RefText(_teachImagePath));
            if (image != null && image.IsInitialized())
            {
                _teachEditor.ShowImage(image);
                _runView.ShowImage(image);
            }
        }

        private HObject ResolveImage(string imageRef)
        {
            if (imageRef.Equals("Input.Image", StringComparison.OrdinalIgnoreCase)
                && _context.InputImage != null && _context.InputImage.IsInitialized())
            {
                return _context.InputImage;
            }

            if (File.Exists(imageRef))
            {
                HOperatorSet.ReadImage(out HObject image, imageRef);
                return image;
            }

            try
            {
                if (_context.LastRunContext != null)
                {
                    object value = VariableReference.Parse(imageRef).Resolve(_context.LastRunContext);
                    if (value is HalconImage halconImage)
                    {
                        return halconImage.Object;
                    }
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        private void BeginDrawBaseRoi()
        {
            _teachEditor.Rois.Clear();
            _teachEditor.ImageView.ClearOverlay();
            _teachEditor.ImageView.BeginAddRoi(GetRoiKind(_tool));
        }

        private void RefreshTeachRoiFromBase()
        {
            _teachEditor.Rois.Clear();
            RoiShape roi = CreateBaseRoi();
            HomMat2D pose = IsCurrentTeachMode() ? ResolveMatrix(RefText(_teachPosePath)) : null;
            if (pose != null)
            {
                roi = TransformRoi(roi, pose);
            }
            _teachEditor.Rois.Add(roi);
            UpdateCaliperOverlay();
        }

        private bool SaveBaseRoi()
        {
            RoiShape roi = FindBaseRoi();
            if (roi == null)
            {
                return false;
            }

            if (IsCurrentTeachMode())
            {
                HomMat2D pose = ResolveMatrix(RefText(_teachPosePath));
                if (pose == null)
                {
                    MessageBox.Show(this, "当前图像示教必须选择当前姿态。", "缺少当前姿态",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                roi = TransformRoi(roi, pose.Inverted());
            }

            ApplyBaseRoi(roi);
            return true;
        }

        private bool IsCurrentTeachMode()
        {
            return _teachMode.Text == TeachCurrentText;
        }

        private RoiShape CreateBaseRoi()
        {
            if (_tool is LineFollowMeasureTool line)
            {
                return new LineRoi("基准直线", line.BaseRow1, line.BaseColumn1, line.BaseRow2, line.BaseColumn2);
            }
            if (_tool is RectangleFollowMeasureTool rect)
            {
                return new Rectangle2Roi("基准矩形", rect.BaseRow, rect.BaseColumn, rect.BasePhi, rect.BaseLength1, rect.BaseLength2);
            }
            if (_tool is OneDCaliperFollowMeasureTool caliper)
            {
                return new Rectangle2Roi("基准卡尺", caliper.BaseRow, caliper.BaseColumn, caliper.BasePhi, caliper.BaseLength1, caliper.BaseLength2);
            }
            if (_tool is ArcCaliperFollowMeasureTool arc)
            {
                return new CircleRoi("基准圆弧", arc.BaseRow, arc.BaseColumn, arc.BaseRadius);
            }
            var circle = (CircleFollowMeasureTool)_tool;
            return new CircleRoi("基准圆", circle.BaseRow, circle.BaseColumn, circle.BaseRadius);
        }

        private void ApplyBaseRoi(RoiShape roi)
        {
            if (_tool is LineFollowMeasureTool line && roi is LineRoi lineRoi)
            {
                line.BaseRow1 = lineRoi.Row1;
                line.BaseColumn1 = lineRoi.Column1;
                line.BaseRow2 = lineRoi.Row2;
                line.BaseColumn2 = lineRoi.Column2;
                return;
            }
            if (_tool is RectangleFollowMeasureTool rect && roi is Rectangle2Roi rectRoi)
            {
                rect.BaseRow = rectRoi.Row;
                rect.BaseColumn = rectRoi.Column;
                rect.BasePhi = rectRoi.Phi;
                rect.BaseLength1 = rectRoi.Length1;
                rect.BaseLength2 = rectRoi.Length2;
                return;
            }
            if (_tool is OneDCaliperFollowMeasureTool caliper && roi is Rectangle2Roi caliperRoi)
            {
                caliper.BaseRow = caliperRoi.Row;
                caliper.BaseColumn = caliperRoi.Column;
                caliper.BasePhi = caliperRoi.Phi;
                caliper.BaseLength1 = caliperRoi.Length1;
                caliper.BaseLength2 = caliperRoi.Length2;
                return;
            }
            if (_tool is CircleFollowMeasureTool circle && roi is CircleRoi circleRoi)
            {
                circle.BaseRow = circleRoi.Row;
                circle.BaseColumn = circleRoi.Column;
                circle.BaseRadius = circleRoi.Radius;
                return;
            }
            if (_tool is ArcCaliperFollowMeasureTool arc && roi is CircleRoi arcRoi)
            {
                arc.BaseRow = arcRoi.Row;
                arc.BaseColumn = arcRoi.Column;
                arc.BaseRadius = arcRoi.Radius;
            }
        }

        private RoiShape TransformRoi(RoiShape roi, HomMat2D matrix)
        {
            if (roi is LineRoi line)
            {
                matrix.TransformPoint(line.Row1, line.Column1, out double r1, out double c1);
                matrix.TransformPoint(line.Row2, line.Column2, out double r2, out double c2);
                return new LineRoi(line.Name, r1, c1, r2, c2);
            }
            if (roi is Rectangle2Roi rect)
            {
                matrix.TransformPose(rect.Row, rect.Column, rect.Phi, out double row, out double col, out double phi);
                return new Rectangle2Roi(rect.Name, row, col, phi, rect.Length1, rect.Length2);
            }
            var circle = (CircleRoi)roi;
            matrix.TransformPoint(circle.Row, circle.Column, out double cr, out double cc);
            matrix.TransformPoint(circle.Row, circle.Column + circle.Radius, out double rr, out double rc);
            double radius = Math.Sqrt((rr - cr) * (rr - cr) + (rc - cc) * (rc - cc));
            return new CircleRoi(circle.Name, cr, cc, radius);
        }

        private HomMat2D ResolveMatrix(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            try
            {
                object value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
                return value as HomMat2D;
            }
            catch
            {
                return null;
            }
        }

        private List<RefCandidate> MatrixCandidates(bool includeCollections, bool expandCollections)
        {
            var result = new List<RefCandidate>();
            foreach (RefCandidate candidate in RefCandidateService.ForNode(_context.Root, _context.Node))
            {
                bool isMatrix = typeof(HomMat2D).IsAssignableFrom(candidate.ClrType);
                if (!isMatrix)
                {
                    continue;
                }
                if (!candidate.IsCollection)
                {
                    result.Add(candidate);
                    continue;
                }
                if (includeCollections)
                {
                    result.Add(candidate);
                }
                if (expandCollections && _context.LastRunContext != null)
                {
                    int count = ResolveCollectionCount(candidate.Path);
                    for (int i = 0; i < count; i++)
                    {
                        result.Add(new RefCandidate { Path = $"{candidate.Path}[{i}]", ClrType = typeof(HomMat2D) });
                    }
                }
            }
            return result;
        }

        private int ResolveCollectionCount(string path)
        {
            try
            {
                object value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
                if (value is Array array)
                {
                    return array.Length;
                }
                if (value is System.Collections.ICollection collection)
                {
                    return collection.Count;
                }
            }
            catch
            {
                return 0;
            }
            return 0;
        }

        private void ShowLastRunResult()
        {
            TryShowTeachImage();
            _runView.ClearOverlay();
            _resultGrid.Columns.Clear();
            _resultGrid.Rows.Clear();
            if (_context.LastRunContext == null)
            {
                return;
            }

            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "ResultContour", out Variable contourVar)
                && contourVar.Value is HObject contour)
            {
                _runView.SetOverlay(contour);
            }
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "Results", out Variable resultsVar))
            {
                FillResultGrid(resultsVar.Value);
            }
        }

        private void FillResultGrid(object value)
        {
            _resultGrid.Columns.Add("Index", "Index");
            if (value is List<LineMeasureResult> lines)
            {
                _resultGrid.Columns.Add("Row1", "Row1");
                _resultGrid.Columns.Add("Column1", "Column1");
                _resultGrid.Columns.Add("Row2", "Row2");
                _resultGrid.Columns.Add("Column2", "Column2");
                foreach (LineMeasureResult r in lines)
                {
                    _resultGrid.Rows.Add(r.Index, r.Row1, r.Column1, r.Row2, r.Column2);
                }
            }
            else if (value is List<RectangleMeasureResult> rects)
            {
                _resultGrid.Columns.Add("Row", "Row");
                _resultGrid.Columns.Add("Column", "Column");
                _resultGrid.Columns.Add("Phi", "Phi");
                _resultGrid.Columns.Add("Length1", "Length1");
                _resultGrid.Columns.Add("Length2", "Length2");
                foreach (RectangleMeasureResult r in rects)
                {
                    _resultGrid.Rows.Add(r.Index, r.Row, r.Column, r.Phi, r.Length1, r.Length2);
                }
            }
            else if (value is List<OneDCaliperMeasureResult> calipers)
            {
                _resultGrid.Columns.Add("Edge", "Edge");
                _resultGrid.Columns.Add("Row", "Row");
                _resultGrid.Columns.Add("Column", "Column");
                _resultGrid.Columns.Add("Amplitude", "Amplitude");
                _resultGrid.Columns.Add("Distance", "Distance");
                foreach (OneDCaliperMeasureResult r in calipers)
                {
                    _resultGrid.Rows.Add(r.Index, r.EdgeIndex, r.Row, r.Column, r.Amplitude, r.Distance);
                }
            }
            else if (value is List<CircleMeasureResult> circles)
            {
                _resultGrid.Columns.Add("Row", "Row");
                _resultGrid.Columns.Add("Column", "Column");
                _resultGrid.Columns.Add("Radius", "Radius");
                foreach (CircleMeasureResult r in circles)
                {
                    _resultGrid.Rows.Add(r.Index, r.Row, r.Column, r.Radius);
                }
            }
        }

        private RoiShape FindBaseRoi()
        {
            RoiKind kind = GetRoiKind(_tool);
            if (_teachEditor.Rois.ActiveRoi != null && _teachEditor.Rois.ActiveRoi.Kind == kind)
            {
                return _teachEditor.Rois.ActiveRoi;
            }
            foreach (RoiShape roi in _teachEditor.Rois.Items)
            {
                if (roi.Kind == kind)
                {
                    return roi;
                }
            }
            return null;
        }

        private void UpdateCaliperOverlay()
        {
            RoiShape roi = FindBaseRoi();
            if (roi == null)
            {
                _teachEditor.ImageView.ClearOverlay();
                return;
            }
            HObject overlay = BuildCalipers(roi);
            try
            {
                _teachEditor.ImageView.SetOverlay(overlay);
            }
            finally
            {
                overlay?.Dispose();
            }
        }

        private HObject BuildCalipers(RoiShape roi)
        {
            var rows = new HTuple();
            var cols = new HTuple();
            var phis = new HTuple();
            var len1 = new HTuple();
            var len2 = new HTuple();
            double ml1 = (double)_measureLength1.Value;
            double ml2 = (double)_measureLength2.Value;
            double sigma = (double)_measureSigma.Value;
            double threshold = (double)_measureThreshold.Value;

            if (roi is LineRoi line)
            {
                return BuildLineMeasurePreview(line.Row1, line.Column1, line.Row2, line.Column2, ml1, ml2, sigma, threshold);
            }
            else if (roi is Rectangle2Roi rect)
            {
                if (_tool is OneDCaliperFollowMeasureTool)
                {
                    AppendCaliper(ref rows, ref cols, ref phis, ref len1, ref len2,
                        rect.Row, rect.Column, rect.Phi, rect.Length1, rect.Length2);
                    HOperatorSet.GenRectangle2(out HObject caliperRectangle, rows, cols, phis, len1, len2);
                    HObject arrows = BuildDirectionArrows(rows, cols, rect.Phi, rect.Length1);
                    HOperatorSet.ConcatObj(caliperRectangle, arrows, out HObject overlay);
                    caliperRectangle.Dispose();
                    arrows.Dispose();
                    return overlay;
                }
                AppendRectangleSideCalipers(ref rows, ref cols, ref phis, ref len1, ref len2, rect, true, -rect.Length2, rect.Phi + Math.PI / 2.0, ml1, ml2);
                AppendRectangleSideCalipers(ref rows, ref cols, ref phis, ref len1, ref len2, rect, true, rect.Length2, rect.Phi + Math.PI / 2.0, ml1, ml2);
                AppendRectangleSideCalipers(ref rows, ref cols, ref phis, ref len1, ref len2, rect, false, -rect.Length1, rect.Phi, ml1, ml2);
                AppendRectangleSideCalipers(ref rows, ref cols, ref phis, ref len1, ref len2, rect, false, rect.Length1, rect.Phi, ml1, ml2);
            }
            else if (roi is CircleRoi circle)
            {
                double start = _startPhi == null ? 0 : (double)_startPhi.Value;
                double end = _endPhi == null ? Math.PI * 2.0 : (double)_endPhi.Value;
                int count = _tool is ArcCaliperFollowMeasureTool && _caliperCount != null
                    ? Math.Max(1, (int)_caliperCount.Value)
                    : Math.Max(12, (int)(Math.Abs(end - start) * circle.Radius / Math.Max(8, ml2 * 4)));
                for (int i = 0; i < count; i++)
                {
                    double t = count == 1 ? (start + end) / 2.0 : start + (end - start) * i / (count - 1);
                    AppendCaliper(ref rows, ref cols, ref phis, ref len1, ref len2,
                        circle.Row + circle.Radius * Math.Sin(t),
                        circle.Column + circle.Radius * Math.Cos(t),
                        t, ml1, ml2);
                }
            }

            if (rows.Length == 0)
            {
                HOperatorSet.GenEmptyObj(out HObject empty);
                return empty;
            }
            HOperatorSet.GenRectangle2(out HObject rectangles, rows, cols, phis, len1, len2);
            return rectangles;
        }

        private static HObject BuildLineMeasurePreview(double row1, double column1, double row2, double column2,
            double measureLength1, double measureLength2, double sigma, double threshold)
        {
            double deltaRow = row2 - row1;
            double deltaColumn = column2 - column1;
            if (Math.Sqrt(deltaRow * deltaRow + deltaColumn * deltaColumn) < 1e-6)
            {
                HOperatorSet.GenEmptyObj(out HObject empty);
                return empty;
            }

            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            HObject modelContour = null;
            HObject measureContours = null;
            try
            {
                HOperatorSet.AddMetrologyObjectLineMeasure(metrology, row1, column1, row2, column2,
                    measureLength1, measureLength2, sigma, threshold, new HTuple(), new HTuple(), out HTuple _);
                HOperatorSet.GetMetrologyObjectModelContour(out modelContour, metrology, "all", 1.5);
                HOperatorSet.GetMetrologyObjectMeasures(out measureContours, metrology, "all", "all", out HTuple _, out HTuple _);
                HOperatorSet.ConcatObj(measureContours, modelContour, out HObject overlay);
                return overlay;
            }
            finally
            {
                modelContour?.Dispose();
                measureContours?.Dispose();
                HOperatorSet.ClearMetrologyModel(metrology);
            }
        }

        private static HObject BuildDirectionArrows(HTuple rows, HTuple cols, double phi, double length)
        {
            HOperatorSet.GenEmptyObj(out HObject arrows);
            double half = Math.Max(6, length * 0.75);
            double dr = Math.Sin(phi) * half;
            double dc = Math.Cos(phi) * half;
            int step = Math.Max(1, rows.Length / 8);
            for (int i = 0; i < rows.Length; i += step)
            {
                HOperatorSet.GenRegionLine(out HObject arrow,
                    rows[i].D - dr, cols[i].D - dc,
                    rows[i].D + dr, cols[i].D + dc);
                HOperatorSet.ConcatObj(arrows, arrow, out HObject combined);
                arrows.Dispose();
                arrow.Dispose();
                arrows = combined;
            }
            return arrows;
        }

        private static void AppendRectangleSideCalipers(ref HTuple rows, ref HTuple cols, ref HTuple phis, ref HTuple len1, ref HTuple len2,
            Rectangle2Roi rect, bool alongLength1, double fixedLocal, double phi, double measureLength1, double measureLength2)
        {
            double sideLength = alongLength1 ? rect.Length1 : rect.Length2;
            int count = Math.Max(3, (int)(sideLength * 2.0 / Math.Max(8, measureLength2 * 4)));
            for (int i = 0; i <= count; i++)
            {
                double moving = -sideLength + sideLength * 2.0 * i / count;
                double local1 = alongLength1 ? moving : fixedLocal;
                double local2 = alongLength1 ? fixedLocal : moving;
                double cos = Math.Cos(rect.Phi);
                double sin = Math.Sin(rect.Phi);
                double col = rect.Column + local1 * cos - local2 * sin;
                double row = rect.Row + local1 * sin + local2 * cos;
                AppendCaliper(ref rows, ref cols, ref phis, ref len1, ref len2, row, col, phi, measureLength1, measureLength2);
            }
        }

        private static void AppendCaliper(ref HTuple rows, ref HTuple cols, ref HTuple phis, ref HTuple len1, ref HTuple len2,
            double row, double col, double phi, double measureLength1, double measureLength2)
        {
            rows = rows.TupleConcat(row);
            cols = cols.TupleConcat(col);
            phis = phis.TupleConcat(phi);
            len1 = len1.TupleConcat(measureLength1);
            len2 = len2.TupleConcat(measureLength2);
        }

        private static TableLayoutPanel CreateSettingsLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return layout;
        }

        private static TextBox AddTextRow(TableLayoutPanel layout, int row, string label, string value)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var textBox = new TextBox { Text = value ?? string.Empty, Dock = DockStyle.Fill };
            layout.Controls.Add(textBox, 1, row);
            return textBox;
        }

        private static ComboBox AddRefRow(TableLayoutPanel layout, int row, string label,
            List<RefCandidate> candidates, string currentValue, bool optional, bool includeInputImage)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var combo = new ComboBox { Text = currentValue ?? string.Empty, Dock = DockStyle.Fill };
            if (optional)
            {
                combo.Items.Add(OptionalNoneText);
            }
            if (includeInputImage)
            {
                combo.Items.Add("Input.Image");
            }
            foreach (RefCandidate candidate in candidates)
            {
                combo.Items.Add(candidate.Path);
            }
            if (optional && string.IsNullOrWhiteSpace(combo.Text))
            {
                combo.Text = OptionalNoneText;
            }
            layout.Controls.Add(combo, 1, row);
            return combo;
        }

        private static NumericUpDown AddNumRow(TableLayoutPanel layout, int row, string label, double value, int decimals)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var num = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = -1000000,
                Maximum = 1000000,
                DecimalPlaces = decimals,
                Increment = decimals >= 3 ? 0.001m : 1m,
                Value = (decimal)value
            };
            layout.Controls.Add(num, 1, row);
            return num;
        }

        private static ComboBox AddComboRow(TableLayoutPanel layout, int row, string label, string value, params string[] items)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(items);
            combo.Text = string.IsNullOrWhiteSpace(value) ? items[0] : value;
            layout.Controls.Add(combo, 1, row);
            return combo;
        }

        private static void AddHint(TableLayoutPanel layout, int row, string label, string value)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            layout.Controls.Add(new Label { Text = value, Dock = DockStyle.Fill, AutoSize = false }, 1, row);
        }

        private static string RefText(ComboBox combo)
        {
            string text = combo.Text.Trim();
            return text == OptionalNoneText ? string.Empty : text;
        }

        private static RoiKind GetRoiKind(FollowMeasureToolBase tool)
        {
            if (tool is LineFollowMeasureTool) return RoiKind.Line;
            if (tool is RectangleFollowMeasureTool || tool is OneDCaliperFollowMeasureTool) return RoiKind.Rectangle2;
            return RoiKind.Circle;
        }

        private static string GetTitle(FollowMeasureToolBase tool)
        {
            if (tool is LineFollowMeasureTool) return "直线测量";
            if (tool is OneDCaliperFollowMeasureTool) return "一维卡尺测量";
            if (tool is ArcCaliperFollowMeasureTool) return "一维圆弧卡尺测量";
            if (tool is RectangleFollowMeasureTool) return "矩形测量";
            if (tool is CircleFollowMeasureTool) return "圆形测量";
            return "测量";
        }

        private static string GetShapeName(FollowMeasureToolBase tool)
        {
            if (tool is LineFollowMeasureTool) return "直线";
            if (tool is OneDCaliperFollowMeasureTool) return "卡尺";
            if (tool is ArcCaliperFollowMeasureTool) return "圆弧卡尺";
            if (tool is RectangleFollowMeasureTool) return "矩形";
            return "圆";
        }
    }
}
