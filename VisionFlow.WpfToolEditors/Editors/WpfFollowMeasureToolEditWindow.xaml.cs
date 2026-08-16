using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using VisionFlow.Controls.Roi;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;
using VisionFlow.WpfToolEditors.Controls;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfFollowMeasureToolEditWindow : Window
    {
        private const string TeachBaseText = "基准图像示教";
        private const string TeachCurrentText = "当前图像示教(按当前姿态反算回基准)";

        private sealed class ExtraTextBox
        {
            public NumericInputControl TextBox { get; set; }
        }

        private readonly FollowMeasureToolBase _tool;
        private readonly ToolEditContext _context;
        private readonly ExtraTextBox _startPhi = new ExtraTextBox();
        private readonly ExtraTextBox _endPhi = new ExtraTextBox();
        private readonly ExtraTextBox _caliperCount = new ExtraTextBox();

        public WpfFollowMeasureToolEditWindow(FollowMeasureToolBase tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = GetTitle(tool) + " - " + tool.ModuleName;
            DrawRoiButton.Content = "绘制/重绘" + GetShapeName(tool) + "基准";
            InitializeFields();
            TeachRoiEditor.Rois.Changed += (s, e) => UpdateCaliperOverlay();
            LoadBaseRoi();
            TryShowTeachImage();
            ShowLastRunResult();
        }

        private void InitializeFields()
        {
            ModuleNameText.Text = _tool.ModuleName;
            FillImageCandidates();
            TeachModeCombo.Items.Add(TeachBaseText);
            TeachModeCombo.Items.Add(TeachCurrentText);
            TeachModeCombo.Text = TeachBaseText;
            FillTeachPoseCandidates();
            FillMatrixCandidates(MatrixPathCombo, _tool.MatrixPath, includeCollections: true);
            FillRefCandidates(IndexPathCombo, typeof(int), _tool.IndexPath, optional: true);

            MeasureLength1Text.Value = _tool.MeasureLength1;
            MeasureLength2Text.Value = _tool.MeasureLength2;
            MeasureSigmaText.Value = _tool.MeasureSigma;
            MeasureThresholdText.Value = _tool.MeasureThreshold;
            AddItems(TransitionCombo, "all", "positive", "negative");
            TransitionCombo.Text = _tool.MeasureTransition;
            AddItems(SelectCombo, "all", "first", "last");
            SelectCombo.Text = _tool.MeasureSelect;

            if (_tool is CircleFollowMeasureTool circle)
            {
                _startPhi.TextBox = AddExtraNumber("起始角", circle.StartPhi);
                _endPhi.TextBox = AddExtraNumber("结束角", circle.EndPhi);
                _startPhi.TextBox.ValueChanged += MeasureParameter_ValueChanged;
                _endPhi.TextBox.ValueChanged += MeasureParameter_ValueChanged;
            }
            else if (_tool is ArcCaliperFollowMeasureTool arc)
            {
                _startPhi.TextBox = AddExtraNumber("起始角", arc.StartPhi);
                _endPhi.TextBox = AddExtraNumber("结束角", arc.EndPhi);
                _caliperCount.TextBox = AddExtraNumber("卡尺数量", arc.CaliperCount);
                _startPhi.TextBox.ValueChanged += MeasureParameter_ValueChanged;
                _endPhi.TextBox.ValueChanged += MeasureParameter_ValueChanged;
                _caliperCount.TextBox.ValueChanged += MeasureParameter_ValueChanged;
            }
        }

        private void FillImageCandidates()
        {
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ImagePathCombo.Items.Add("Input.Image");
            }
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HalconImage)))
                {
                    ImagePathCombo.Items.Add(candidate.Path);
                }
            }
            ImagePathCombo.Text = string.IsNullOrWhiteSpace(_tool.ImagePath) ? "Input.Image" : _tool.ImagePath;
        }

        private void FillMatrixCandidates(ComboBox combo, string current, bool includeCollections)
        {
            combo.Items.Add(string.Empty);
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForNode(_context.Root, _context.Node))
                {
                    if (typeof(HomMat2D).IsAssignableFrom(candidate.ClrType) && (!candidate.IsCollection || includeCollections))
                    {
                        combo.Items.Add(candidate.Path);
                    }
                }
            }
            combo.Text = current ?? string.Empty;
        }

        private void FillTeachPoseCandidates()
        {
            TeachPosePathCombo.Items.Add(string.Empty);
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForNode(_context.Root, _context.Node))
                {
                    if (!typeof(HomMat2D).IsAssignableFrom(candidate.ClrType))
                    {
                        continue;
                    }
                    if (!candidate.IsCollection)
                    {
                        TeachPosePathCombo.Items.Add(candidate.Path);
                        continue;
                    }
                    int count = ResolveCollectionCount(candidate.Path);
                    for (int i = 0; i < count; i++)
                    {
                        TeachPosePathCombo.Items.Add(candidate.Path + "[" + i + "]");
                    }
                }
            }
        }

        private void FillRefCandidates(ComboBox combo, Type expectedType, string current, bool optional)
        {
            if (optional)
            {
                combo.Items.Add(string.Empty);
            }
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, expectedType))
                {
                    combo.Items.Add(candidate.Path);
                }
            }
            combo.Text = current ?? string.Empty;
        }

        private NumericInputControl AddExtraNumber(string label, double value)
        {
            ArcSettingsPanel.Children.Add(new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("EditorFieldLabelStyle")
            });
            var input = new NumericInputControl
            {
                Value = value,
                Minimum = label.Contains("数量", StringComparison.Ordinal) ? 1 : -Math.PI * 4,
                Maximum = label.Contains("数量", StringComparison.Ordinal) ? 1000 : Math.PI * 4,
                Increment = label.Contains("数量", StringComparison.Ordinal) ? 1 : 0.01,
                DecimalPlaces = label.Contains("数量", StringComparison.Ordinal) ? 0 : 4
            };
            ArcSettingsPanel.Children.Add(input);
            return input;
        }

        private static void AddItems(ComboBox combo, params string[] items)
        {
            foreach (string item in items)
            {
                combo.Items.Add(item);
            }
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TryShowTeachImage();
        }

        private void TeachModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshTeachRoiFromBase();
        }

        private void TeachPosePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshTeachRoiFromBase();
        }

        private void MeasureParameter_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCaliperOverlay();
        }

        private void MeasureParameter_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateCaliperOverlay();
        }

        private void DrawRoi_Click(object sender, RoutedEventArgs e)
        {
            TeachRoiEditor.Rois.Clear();
            TeachRoiEditor.ClearOverlay();
            TeachRoiEditor.BeginAddRoi(GetRoiKind(_tool));
            StatusText.Text = "请在图像区域点击并拖动编辑 ROI。";
        }

        private void SaveTeach_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = SaveBaseRoi() ? "示教基准已保存到当前工具。" : "请先绘制匹配的基准 ROI。";
        }

        private void RunTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!SaveBaseRoi())
                {
                    StatusText.Text = "测试前请先绘制并保存基准 ROI。";
                    return;
                }
                ApplySettings();
                FlowContext ctx = BuildRunContext();
                ClearMeasureResults(ctx);
                NodeResult result = _tool.Run(ctx);
                _context.LastRunContext = ctx;
                ShowLastRunResult();
                StatusText.Text = result.IsSuccess ? "测试完成。" : "测试失败：" + result.Message;
            }
            catch (Exception ex)
            {
                StatusText.Text = "测试失败：" + ex.Message;
            }
        }

        private FlowContext BuildRunContext()
        {
            FlowContext ctx = _context.LastRunContext ?? new FlowContext();
            if (_context.InputImage != null && _context.InputImage.IsInitialized()
                && !ctx.TryGetVariable("Input", "Image", out _))
            {
                ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(_context.InputImage), 1));
            }
            return ctx;
        }

        private void TryShowTeachImage()
        {
            try
            {
                HObject image = ResolveImage(ImagePathCombo.Text);
                if (image != null && image.IsInitialized())
                {
                    TeachRoiEditor.ShowImage(image);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "示教图像加载失败：" + ex.Message;
            }
        }

        private HObject ResolveImage(string imageRef)
        {
            if (imageRef.Equals("Input.Image", StringComparison.OrdinalIgnoreCase)
                && _context.InputImage != null && _context.InputImage.IsInitialized())
            {
                return _context.InputImage;
            }

            if (_context.LastRunContext == null || string.IsNullOrWhiteSpace(imageRef))
            {
                return null;
            }

            object value = VariableReference.Parse(imageRef).Resolve(_context.LastRunContext);
            return value is HalconImage halconImage ? halconImage.Object : value as HObject;
        }

        private void LoadBaseRoi()
        {
            TeachRoiEditor.Rois.Clear();
            TeachRoiEditor.Rois.Add(CreateBaseRoi());
            UpdateCaliperOverlay();
        }

        private void RefreshTeachRoiFromBase()
        {
            if (TeachRoiEditor == null || TeachModeCombo == null)
            {
                return;
            }

            RoiShape roi = CreateBaseRoi();
            if (IsCurrentTeachMode())
            {
                HomMat2D pose = ResolveMatrix(TeachPosePathCombo.Text);
                if (pose != null)
                {
                    roi = TransformRoi(roi, pose);
                }
            }

            TeachRoiEditor.Rois.Clear();
            TeachRoiEditor.Rois.Add(roi);
            UpdateCaliperOverlay();
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

        private bool SaveBaseRoi()
        {
            RoiShape roi = FindBaseRoi();
            if (roi == null)
            {
                return false;
            }
            if (IsCurrentTeachMode())
            {
                HomMat2D pose = ResolveMatrix(TeachPosePathCombo.Text);
                if (pose == null)
                {
                    StatusText.Text = "当前图像示教必须选择可解析的当前姿态。";
                    return false;
                }
                roi = TransformRoi(roi, pose.Inverted());
            }
            ApplyBaseRoi(roi);
            return true;
        }

        private bool IsCurrentTeachMode()
        {
            return TeachModeCombo.Text == TeachCurrentText;
        }

        private RoiShape FindBaseRoi()
        {
            RoiKind kind = GetRoiKind(_tool);
            if (TeachRoiEditor.Rois.ActiveRoi != null && TeachRoiEditor.Rois.ActiveRoi.Kind == kind)
            {
                return TeachRoiEditor.Rois.ActiveRoi;
            }
            foreach (RoiShape roi in TeachRoiEditor.Rois.Items)
            {
                if (roi.Kind == kind)
                {
                    return roi;
                }
            }
            return null;
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
            if (_tool is ArcCaliperFollowMeasureTool arc && roi is CircleRoi arcRoi)
            {
                arc.BaseRow = arcRoi.Row;
                arc.BaseColumn = arcRoi.Column;
                arc.BaseRadius = arcRoi.Radius;
                return;
            }
            if (_tool is CircleFollowMeasureTool circle && roi is CircleRoi circleRoi)
            {
                circle.BaseRow = circleRoi.Row;
                circle.BaseColumn = circleRoi.Column;
                circle.BaseRadius = circleRoi.Radius;
            }
        }

        private RoiShape TransformRoi(RoiShape roi, HomMat2D matrix)
        {
            if (roi is LineRoi line)
            {
                matrix.TransformPoint(line.Row1, line.Column1, out double row1, out double column1);
                matrix.TransformPoint(line.Row2, line.Column2, out double row2, out double column2);
                return new LineRoi(line.Name, row1, column1, row2, column2);
            }
            if (roi is Rectangle2Roi rect)
            {
                matrix.TransformPose(rect.Row, rect.Column, rect.Phi, out double row, out double column, out double phi);
                return new Rectangle2Roi(rect.Name, row, column, phi, rect.Length1, rect.Length2);
            }
            var circle = (CircleRoi)roi;
            matrix.TransformPoint(circle.Row, circle.Column, out double centerRow, out double centerColumn);
            matrix.TransformPoint(circle.Row, circle.Column + circle.Radius, out double radiusRow, out double radiusColumn);
            double radius = Math.Sqrt((radiusRow - centerRow) * (radiusRow - centerRow) + (radiusColumn - centerColumn) * (radiusColumn - centerColumn));
            return new CircleRoi(circle.Name, centerRow, centerColumn, radius);
        }

        private HomMat2D ResolveMatrix(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || _context.LastRunContext == null)
            {
                return null;
            }

            try
            {
                return VariableReference.Parse(path).Resolve(_context.LastRunContext) as HomMat2D;
            }
            catch
            {
                return null;
            }
        }

        private int ResolveCollectionCount(string path)
        {
            if (_context.LastRunContext == null)
            {
                return 0;
            }

            try
            {
                object value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
                if (value is Array array)
                {
                    return array.Length;
                }
                if (value is ICollection collection)
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

        private void ApplySettings()
        {
            _tool.ModuleName = ModuleNameText.Text.Trim();
            _tool.ImagePath = ImagePathCombo.Text.Trim();
            _tool.MatrixPath = MatrixPathCombo.Text.Trim();
            _tool.IndexPath = IndexPathCombo.Text.Trim();
            _tool.MeasureLength1 = MeasureLength1Text.Value;
            _tool.MeasureLength2 = MeasureLength2Text.Value;
            _tool.MeasureSigma = MeasureSigmaText.Value;
            _tool.MeasureThreshold = MeasureThresholdText.Value;
            _tool.MeasureTransition = TransitionCombo.Text.Trim();
            _tool.MeasureSelect = SelectCombo.Text.Trim();

            if (_tool is CircleFollowMeasureTool circle)
            {
                circle.StartPhi = _startPhi.TextBox.Value;
                circle.EndPhi = _endPhi.TextBox.Value;
            }
            else if (_tool is ArcCaliperFollowMeasureTool arc)
            {
                arc.StartPhi = _startPhi.TextBox.Value;
                arc.EndPhi = _endPhi.TextBox.Value;
                arc.CaliperCount = Math.Max(1, (int)Math.Round(_caliperCount.TextBox.Value));
            }
        }

        private static double ParseDouble(string text, string name)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                throw new InvalidOperationException(name + " 不是有效数字。");
            }
            return value;
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
            else if (_tool is CircleFollowMeasureTool)
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<CircleMeasureResult>(), 0));
            }
            else
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<OneDCaliperMeasureResult>(), 0));
            }
        }

        private void ShowLastRunResult()
        {
            ResultGrid.ItemsSource = null;
            if (_context.LastRunContext == null)
            {
                return;
            }
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "ResultContour", out Variable contourVariable)
                && contourVariable.Value is HObject contour)
            {
                TeachRoiEditor.SetOverlay(contour);
            }
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "Results", out Variable resultsVariable))
            {
                ResultGrid.ItemsSource = resultsVariable.Value as IEnumerable;
            }
        }

        private void UpdateCaliperOverlay()
        {
            if (TeachRoiEditor == null || MeasureLength1Text == null || MeasureLength2Text == null)
            {
                return;
            }

            RoiShape roi = FindBaseRoi();
            if (roi == null)
            {
                TeachRoiEditor.ClearOverlay();
                return;
            }

            try
            {
                HObject overlay = BuildCalipers(roi);
                try
                {
                    TeachRoiEditor.SetOverlay(overlay);
                }
                finally
                {
                    overlay.Dispose();
                }
            }
            catch
            {
                TeachRoiEditor.ClearOverlay();
            }
        }

        private HObject BuildCalipers(RoiShape roi)
        {
            var rows = new HTuple();
            var columns = new HTuple();
            var phis = new HTuple();
            var length1 = new HTuple();
            var length2 = new HTuple();
            double measureLength1 = MeasureLength1Text.Value;
            double measureLength2 = MeasureLength2Text.Value;
            double measureSigma = MeasureSigmaText.Value;
            double measureThreshold = MeasureThresholdText.Value;

            if (roi is LineRoi line)
            {
                return BuildLineMeasurePreview(line.Row1, line.Column1, line.Row2, line.Column2,
                    measureLength1, measureLength2, measureSigma, measureThreshold);
            }
            else if (roi is Rectangle2Roi rect)
            {
                if (_tool is OneDCaliperFollowMeasureTool)
                {
                    AppendCaliper(ref rows, ref columns, ref phis, ref length1, ref length2,
                        rect.Row, rect.Column, rect.Phi, rect.Length1, rect.Length2);
                }
                else
                {
                    AppendRectangleSideCalipers(ref rows, ref columns, ref phis, ref length1, ref length2, rect, true, -rect.Length2, rect.Phi + Math.PI / 2.0, measureLength1, measureLength2);
                    AppendRectangleSideCalipers(ref rows, ref columns, ref phis, ref length1, ref length2, rect, true, rect.Length2, rect.Phi + Math.PI / 2.0, measureLength1, measureLength2);
                    AppendRectangleSideCalipers(ref rows, ref columns, ref phis, ref length1, ref length2, rect, false, -rect.Length1, rect.Phi, measureLength1, measureLength2);
                    AppendRectangleSideCalipers(ref rows, ref columns, ref phis, ref length1, ref length2, rect, false, rect.Length1, rect.Phi, measureLength1, measureLength2);
                }
            }
            else if (roi is CircleRoi circle)
            {
                double startPhi = _startPhi.TextBox == null ? 0 : _startPhi.TextBox.Value;
                double endPhi = _endPhi.TextBox == null ? Math.PI * 2 : _endPhi.TextBox.Value;
                int count = _tool is ArcCaliperFollowMeasureTool && _caliperCount.TextBox != null
                    ? Math.Max(1, (int)Math.Round(_caliperCount.TextBox.Value))
                    : Math.Max(12, (int)(Math.Abs(endPhi - startPhi) * circle.Radius / Math.Max(8, measureLength2 * 4)));
                for (int i = 0; i < count; i++)
                {
                    double phi = count == 1 ? (startPhi + endPhi) / 2.0 : startPhi + (endPhi - startPhi) * i / (count - 1);
                    AppendCaliper(ref rows, ref columns, ref phis, ref length1, ref length2,
                        circle.Row + circle.Radius * Math.Sin(phi),
                        circle.Column + circle.Radius * Math.Cos(phi),
                        phi, measureLength1, measureLength2);
                }
            }

            if (rows.Length == 0)
            {
                HOperatorSet.GenEmptyObj(out HObject empty);
                return empty;
            }

            HOperatorSet.GenRectangle2(out HObject rectangles, rows, columns, phis, length1, length2);
            return rectangles;
        }

        private static HObject BuildLineMeasurePreview(double row1, double column1, double row2, double column2,
            double measureLength1, double measureLength2, double measureSigma, double measureThreshold)
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
                    measureLength1, measureLength2, measureSigma, measureThreshold, new HTuple(), new HTuple(), out HTuple _);
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

        private static void AppendRectangleSideCalipers(ref HTuple rows, ref HTuple columns, ref HTuple phis, ref HTuple length1, ref HTuple length2,
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
                double column = rect.Column + local1 * cos - local2 * sin;
                double row = rect.Row + local1 * sin + local2 * cos;
                AppendCaliper(ref rows, ref columns, ref phis, ref length1, ref length2, row, column, phi, measureLength1, measureLength2);
            }
        }

        private static void AppendCaliper(ref HTuple rows, ref HTuple columns, ref HTuple phis, ref HTuple length1, ref HTuple length2,
            double row, double column, double phi, double measureLength1, double measureLength2)
        {
            rows = rows.TupleConcat(row);
            columns = columns.TupleConcat(column);
            phis = phis.TupleConcat(phi);
            length1 = length1.TupleConcat(measureLength1);
            length2 = length2.TupleConcat(measureLength2);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!SaveBaseRoi())
                {
                    StatusText.Text = "请先绘制匹配的基准 ROI。";
                    return;
                }
                ApplySettings();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = "保存失败：" + ex.Message;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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
