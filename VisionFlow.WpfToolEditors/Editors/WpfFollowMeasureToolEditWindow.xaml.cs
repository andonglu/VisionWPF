using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private readonly FlowContext _sourceRunContext;
        // 本窗口创建的预览上下文：替换或关闭时回收其 HALCON 资源（VF-04）。
        // 注意不得处置 _context.LastRunContext 的初始值——它属于主窗口的上次运行。
        private FlowContext _previewContext;
        private readonly ExtraTextBox _startPhi = new ExtraTextBox();
        private readonly ExtraTextBox _endPhi = new ExtraTextBox();
        private readonly ExtraTextBox _caliperCount = new ExtraTextBox();
        private MetrologyAdvancedExpander _advanced;

        public WpfFollowMeasureToolEditWindow(FollowMeasureToolBase tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            _sourceRunContext = _context.LastRunContext;
            InitializeComponent();
            Title = GetTitle(tool) + " - " + tool.ModuleName;
            DrawRoiButton.Content = "绘制/重绘" + GetShapeName(tool) + "基准";
            InitializeFields();
            TeachRoiEditor.Rois.Changed += (s, e) => UpdateCaliperOverlay();
            if (tool is GrayProjectionFollowTool)
            {
                ProfileChart.SizeChanged += (s, e) => UpdateProfileChart(FindBaseRoi() as Rectangle2Roi);
            }
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
            if (_tool is IRegionSeededMeasureTool seeded)
            {
                FillRefCandidates(InitRegionPathCombo, typeof(HalconRegion), seeded.InitRegionPath, optional: true);
            }
            else
            {
                InitRegionLabel.Visibility = Visibility.Collapsed;
                InitRegionPathCombo.Visibility = Visibility.Collapsed;
            }

            MeasureLength1Text.Value = _tool.MeasureLength1;
            MeasureLength2Text.Value = _tool.MeasureLength2;
            MeasureSigmaText.Value = _tool.MeasureSigma;
            MeasureThresholdText.Value = _tool.MeasureThreshold;
            if (_tool is CaliperMeasureToolBase caliperTool)
            {
                EdgeModeCombo.Items.Add(new EdgeModeChoice(EdgeMode.Edge, "单边缘"));
                EdgeModeCombo.Items.Add(new EdgeModeChoice(EdgeMode.Pair, "边缘对（宽度测量）"));
                EdgeModeCombo.SelectedIndex = caliperTool.EdgeMode == EdgeMode.Pair ? 1 : 0;
                FillTransitionItems(caliperTool.EdgeMode);
            }
            else
            {
                EdgeModeLabel.Visibility = Visibility.Collapsed;
                EdgeModeCombo.Visibility = Visibility.Collapsed;
                AddItems(TransitionCombo, "all", "positive", "negative");
            }
            TransitionCombo.Text = _tool.MeasureTransition;
            AddItems(SelectCombo, "all", "first", "last");
            SelectCombo.Text = _tool.MeasureSelect;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
            if (_tool is GrayProjectionFollowTool projection)
            {
                // 灰度投影不找边缘：卡尺长度、边缘阈值、极性、选择等参数与它无关
                foreach (UIElement element in new UIElement[]
                         {
                             MeasureLength1Label, MeasureLength1Text, MeasureLength2Label, MeasureLength2Text, MeasureSigmaLabel, MeasureSigmaText,
                             MeasureThresholdLabel, MeasureThresholdText, TransitionLabel, TransitionCombo, SelectLabel, SelectCombo
                         })
                {
                    element.Visibility = Visibility.Collapsed;
                }
                ProjectionPanel.Visibility = Visibility.Visible;
                SmoothText.Value = projection.Smooth;
                FailWhenNotFoundCheck.Content = "测量矩形超出图像时失败（取消后输出 Found=false 并继续）";
            }
            if (_tool is MetrologyMeasureToolBase metrology)
            {
                // 一维卡尺类工具不走 metrology，没有高级参数
                _advanced = new MetrologyAdvancedExpander();
                _advanced.Load(metrology);
                AdvancedHost.Content = _advanced;
            }

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

        private sealed class EdgeModeChoice
        {
            public EdgeModeChoice(EdgeMode value, string label)
            {
                Value = value;
                Label = label;
            }

            public EdgeMode Value { get; private set; }
            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }

        private EdgeMode SelectedEdgeMode => (EdgeModeCombo.SelectedItem as EdgeModeChoice)?.Value ?? EdgeMode.Edge;

        /// <summary>边缘极性下拉随边缘模式切换：边缘对模式多出 *_strongest 三项；当前值在新模式下不可用时退回 all。</summary>
        private void FillTransitionItems(EdgeMode mode)
        {
            string current = TransitionCombo.Text;
            TransitionCombo.Items.Clear();
            AddItems(TransitionCombo, CaliperMeasureToolBase.TransitionsFor(mode).ToArray());
            TransitionCombo.Text = CaliperMeasureToolBase.TransitionsFor(mode).Contains(current) ? current : "all";
        }

        private void EdgeModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || !(_tool is CaliperMeasureToolBase))
            {
                return;
            }
            FillTransitionItems(SelectedEdgeMode);
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged 时可编辑下拉框的 Text 尚未更新为新选项，等选择生效后再取图
            Dispatcher.BeginInvoke(new Action(TryShowTeachImage));
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
            if (_tool is CornerFindTool)
            {
                // 找角依次画两条边：已有两条时重新开始；第一条为边 1，第二条为边 2
                if (CornerRois().Count >= 2)
                {
                    TeachRoiEditor.Rois.Clear();
                    TeachRoiEditor.ClearOverlay();
                }
                TeachRoiEditor.BeginAddRoi(RoiKind.Line);
                StatusText.Text = CornerRois().Count == 0
                    ? "请在图像上点击放置边 1（再点一次“绘制”放置边 2），拖动端点调整。"
                    : "请在图像上点击放置边 2，拖动端点调整。";
                return;
            }
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
            FlowContext previousPreview = _previewContext;
            FlowContext ctx = null;
            try
            {
                if (!SaveBaseRoi())
                {
                    StatusText.Text = "测试前请先绘制并保存基准 ROI。";
                    return;
                }
                ApplySettings();
                ctx = BuildRunContext();
                ClearMeasureResults(ctx);
                NodeResult result = _tool.Run(ctx);
                _context.LastRunContext = ctx;
                _previewContext = ctx;
                ShowLastRunResult();
                StatusText.Text = result.IsSuccess ? "测试完成。" : "测试失败：" + result.Message;
            }
            catch (Exception ex)
            {
                StatusText.Text = "测试失败：" + ex.Message;
            }
            finally
            {
                if (!ReferenceEquals(ctx, _previewContext))
                {
                    ctx?.Dispose();
                }
                if (!ReferenceEquals(previousPreview, _previewContext))
                {
                    previousPreview?.Dispose();
                }
            }
        }

        private FlowContext BuildRunContext()
        {
            // 预览在派生上下文上运行：借用上次运行的变量（含 Input.Image），
            // 日志/报告/轨迹独立，不污染主窗口的上次运行结果
            FlowContext ctx = (_sourceRunContext ?? new FlowContext()).CreatePreviewContext();
            if (_context.InputImage != null && _context.InputImage.IsInitialized()
                && !ctx.TryGetVariable("Input", "Image", out _))
            {
                ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(_context.InputImage), 1));
            }
            return ctx;
        }

        protected override void OnClosed(EventArgs e)
        {
            // 窗口关闭：回收本窗口预览产生的 HALCON 资源（共享变量不受影响）
            _previewContext?.Dispose();
            _previewContext = null;
            base.OnClosed(e);
        }

        private void TryShowTeachImage()
        {
            try
            {
                HObject image = ResolveImage(ImagePathCombo.Text);
                if (image != null && image.IsInitialized())
                {
                    TeachRoiEditor.ShowImage(image);
                    if (_tool is GrayProjectionFollowTool)
                    {
                        UpdateProfileChart(FindBaseRoi() as Rectangle2Roi);
                    }
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
            foreach (RoiShape roi in CreateBaseRois())
            {
                TeachRoiEditor.Rois.Add(roi);
            }
            UpdateCaliperOverlay();
        }

        private void RefreshTeachRoiFromBase()
        {
            if (TeachRoiEditor == null || TeachModeCombo == null)
            {
                return;
            }

            HomMat2D pose = IsCurrentTeachMode() ? ResolveMatrix(TeachPosePathCombo.Text) : null;
            TeachRoiEditor.Rois.Clear();
            foreach (RoiShape roi in CreateBaseRois())
            {
                TeachRoiEditor.Rois.Add(pose != null ? TransformRoi(roi, pose) : roi);
            }
            UpdateCaliperOverlay();
        }

        /// <summary>示教基准 ROI：找角为两条边（边1、边2），其余工具一个。</summary>
        private List<RoiShape> CreateBaseRois()
        {
            if (_tool is CornerFindTool corner)
            {
                return new List<RoiShape>
                {
                    new LineRoi("边1", corner.TeachLine1Row1, corner.TeachLine1Column1, corner.TeachLine1Row2, corner.TeachLine1Column2),
                    new LineRoi("边2", corner.TeachLine2Row1, corner.TeachLine2Column1, corner.TeachLine2Row2, corner.TeachLine2Column2)
                };
            }
            return new List<RoiShape> { CreateBaseRoi() };
        }

        /// <summary>找角的两条边：按 ROI 列表顺序取前两条直线 ROI。</summary>
        private List<LineRoi> CornerRois()
        {
            return TeachRoiEditor.Rois.Items.OfType<LineRoi>().Take(2).ToList();
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
            if (_tool is GrayProjectionFollowTool projection)
            {
                return new Rectangle2Roi("投影矩形", projection.BaseRow, projection.BaseColumn, projection.BasePhi, projection.BaseLength1, projection.BaseLength2);
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
            if (_tool is CornerFindTool corner)
            {
                return SaveCornerRois(corner);
            }
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

        private double CurrentTeachRotation()
        {
            if (TeachModeCombo == null || !IsCurrentTeachMode())
            {
                return 0;
            }
            HomMat2D pose = ResolveMatrix(TeachPosePathCombo.Text);
            return pose == null ? 0 : pose.RotationAngle;
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

        /// <summary>找角：前两条直线 ROI 依次写入边 1、边 2 的示教位置（当前图像示教时先按当前姿态反算回基准）。</summary>
        private bool SaveCornerRois(CornerFindTool corner)
        {
            List<LineRoi> lines = CornerRois();
            if (lines.Count < 2)
            {
                return false;
            }
            HomMat2D inverse = null;
            if (IsCurrentTeachMode())
            {
                HomMat2D pose = ResolveMatrix(TeachPosePathCombo.Text);
                if (pose == null)
                {
                    StatusText.Text = "当前图像示教必须选择可解析的当前姿态。";
                    return false;
                }
                inverse = pose.Inverted();
            }
            LineRoi edge1 = inverse == null ? lines[0] : (LineRoi)TransformRoi(lines[0], inverse);
            LineRoi edge2 = inverse == null ? lines[1] : (LineRoi)TransformRoi(lines[1], inverse);
            corner.TeachLine1Row1 = edge1.Row1;
            corner.TeachLine1Column1 = edge1.Column1;
            corner.TeachLine1Row2 = edge1.Row2;
            corner.TeachLine1Column2 = edge1.Column2;
            corner.TeachLine2Row1 = edge2.Row1;
            corner.TeachLine2Column1 = edge2.Column1;
            corner.TeachLine2Row2 = edge2.Row2;
            corner.TeachLine2Column2 = edge2.Column2;
            return true;
        }

        private void ApplyBaseRoi(RoiShape roi)
        {
            if (_tool is GrayProjectionFollowTool projection && roi is Rectangle2Roi projectionRoi)
            {
                projection.BaseRow = projectionRoi.Row;
                projection.BaseColumn = projectionRoi.Column;
                projection.BasePhi = projectionRoi.Phi;
                projection.BaseLength1 = projectionRoi.Length1;
                projection.BaseLength2 = projectionRoi.Length2;
                return;
            }
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
                double scale = matrix.ScaleFactor;
                return new Rectangle2Roi(rect.Name, row, column, phi, rect.Length1 * scale, rect.Length2 * scale);
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
            if (_tool is IRegionSeededMeasureTool seeded)
            {
                seeded.InitRegionPath = InitRegionPathCombo.Text.Trim();
            }
            _tool.MeasureLength1 = MeasureLength1Text.Value;
            _tool.MeasureLength2 = MeasureLength2Text.Value;
            _tool.MeasureSigma = MeasureSigmaText.Value;
            _tool.MeasureThreshold = MeasureThresholdText.Value;
            _tool.MeasureTransition = TransitionCombo.Text.Trim();
            _tool.MeasureSelect = SelectCombo.Text.Trim();
            if (_tool is CaliperMeasureToolBase caliperTool)
            {
                caliperTool.EdgeMode = SelectedEdgeMode;
            }
            _tool.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
            if (_tool is MetrologyMeasureToolBase metrology && _advanced != null)
            {
                _advanced.Apply(metrology);
            }

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
            else if (_tool is GrayProjectionFollowTool projection)
            {
                projection.Smooth = SmoothText.Value;
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
            else if (_tool is CornerFindTool)
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<CornerMeasureResult>(), 0));
            }
            else if (_tool is GrayProjectionFollowTool)
            {
                ctx.SetVariable(Variable.Object(_tool.ModuleName, "Results", new List<OneDProjectionResult>(), 0));
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
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "ResultContour", out Variable contourVariable))
            {
                HObject contour = contourVariable.Value is HalconXld xld ? xld.Object : contourVariable.Value as HObject;
                if (contour != null)
                {
                    TeachRoiEditor.SetOverlay(contour);
                }
            }
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "Results", out Variable resultsVariable))
            {
                ResultGrid.ItemsSource = resultsVariable.Value as IEnumerable;
            }
            // 边缘对模式的结果表显示每对的宽度与中心
            if (_tool is CaliperMeasureToolBase caliperTool && caliperTool.EdgeMode == EdgeMode.Pair
                && _context.LastRunContext.TryGetVariable(_tool.ModuleName, "PairResults", out Variable pairsVariable))
            {
                ResultGrid.ItemsSource = pairsVariable.Value as IEnumerable;
            }
        }

        private void UpdateCaliperOverlay()
        {
            if (TeachRoiEditor == null || MeasureLength1Text == null || MeasureLength2Text == null)
            {
                return;
            }

            if (_tool is CornerFindTool)
            {
                UpdateCornerOverlay();
                return;
            }

            RoiShape roi = FindBaseRoi();
            if (roi == null)
            {
                TeachRoiEditor.ClearOverlay();
                UpdateProfileChart(null);
                return;
            }
            UpdateProfileChart(roi as Rectangle2Roi);

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

        /// <summary>找角：两条边各自的卡尺与模型线叠加显示。</summary>
        private void UpdateCornerOverlay()
        {
            List<LineRoi> lines = CornerRois();
            if (lines.Count == 0)
            {
                TeachRoiEditor.ClearOverlay();
                return;
            }
            try
            {
                HOperatorSet.GenEmptyObj(out HObject overlay);
                foreach (LineRoi line in lines)
                {
                    HObject preview = BuildLineMeasurePreview(line.Row1, line.Column1, line.Row2, line.Column2,
                        MeasureLength1Text.Value, MeasureLength2Text.Value, MeasureSigmaText.Value, MeasureThresholdText.Value);
                    HOperatorSet.ConcatObj(overlay, preview, out HObject joined);
                    overlay.Dispose();
                    preview.Dispose();
                    overlay = joined;
                }
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

        /// <summary>
        /// 灰度投影：按当前测量矩形（示教图像坐标）与平滑系数在示教图像上计算曲线并画成 Polyline（与运行时同一算法），随 ROI 与参数刷新。
        /// </summary>
        private void UpdateProfileChart(Rectangle2Roi rect)
        {
            if (!(_tool is GrayProjectionFollowTool) || ProfileLine == null)
            {
                return;
            }
            ProfileLine.Points.Clear();
            DerivativeLine.Points.Clear();
            HObject image = TeachRoiEditor.CurrentImage;
            if (rect == null || image == null)
            {
                ProfileSummaryText.Text = image == null ? "没有示教图像，无法显示曲线。" : "请先绘制投影矩形。";
                return;
            }
            try
            {
                if (!GrayProjectionFollowTool.TryComputeProfile(image, rect.Row, rect.Column, rect.Phi, rect.Length1, rect.Length2,
                        SmoothText.Value, out double[] profile, out double[] derivative, out string error))
                {
                    ProfileSummaryText.Text = "曲线：" + error;
                    return;
                }
                double width = ProfileChart.ActualWidth > 4 ? ProfileChart.ActualWidth - 2 : 330;
                double height = ProfileChart.Height - 2;
                double maxSlope = Math.Max(1e-9, derivative.Select(Math.Abs).DefaultIfEmpty(0).Max());
                for (int i = 0; i < profile.Length; i++)
                {
                    double x = profile.Length == 1 ? 0 : width * i / (profile.Length - 1);
                    ProfileLine.Points.Add(new Point(x, height - Math.Max(0, Math.Min(255, profile[i])) / 255.0 * height));
                    if (i < derivative.Length)
                    {
                        DerivativeLine.Points.Add(new Point(x, height / 2 - derivative[i] / maxSlope * height / 2 * 0.9));
                    }
                }
                int minPosition = Array.IndexOf(profile, profile.Min());
                int maxPosition = Array.IndexOf(profile, profile.Max());
                ProfileSummaryText.Text = string.Format(CultureInfo.InvariantCulture,
                    "曲线 {0} 点：最小 {1:F2}（序号 {2}），最大 {3:F2}（序号 {4}），均值 {5:F2}；虚线为一阶导数。",
                    profile.Length, profile[minPosition], minPosition, profile[maxPosition], maxPosition, profile.Average());
            }
            catch (HalconException ex)
            {
                ProfileSummaryText.Text = "曲线计算失败：" + ex.Message;
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
                if (_tool is OneDCaliperFollowMeasureTool || _tool is GrayProjectionFollowTool)
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
                // 起止角始终按基准填写；当前图像示教时按定位旋转量显示，与运行时 FollowArc 一致
                double rotation = CurrentTeachRotation();
                startPhi += rotation;
                endPhi += rotation;
                int count = _tool is ArcCaliperFollowMeasureTool && _caliperCount.TextBox != null
                    ? Math.Max(1, (int)Math.Round(_caliperCount.TextBox.Value))
                    : Math.Max(12, (int)(Math.Abs(endPhi - startPhi) * circle.Radius / Math.Max(8, measureLength2 * 4)));
                for (int i = 0; i < count; i++)
                {
                    double phi = ArcCaliperFollowMeasureTool.CaliperPhi(startPhi, endPhi, count, i);
                    HomMat2D.PointOnCircle(circle.Row, circle.Column, circle.Radius, phi, out double caliperRow, out double caliperColumn);
                    AppendCaliper(ref rows, ref columns, ref phis, ref length1, ref length2,
                        caliperRow, caliperColumn, phi, measureLength1, measureLength2);
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
                // HALCON 约定：Length1 方向(行,列) = (-sin, cos)，Length2 方向 = (-cos, -sin)
                double column = rect.Column + local1 * cos - local2 * sin;
                double row = rect.Row - local1 * sin - local2 * cos;
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
            if (tool is LineFollowMeasureTool || tool is CornerFindTool) return RoiKind.Line;
            if (tool is RectangleFollowMeasureTool || tool is OneDCaliperFollowMeasureTool || tool is GrayProjectionFollowTool) return RoiKind.Rectangle2;
            return RoiKind.Circle;
        }

        private static string GetTitle(FollowMeasureToolBase tool)
        {
            if (tool is LineFollowMeasureTool) return "直线测量";
            if (tool is CornerFindTool) return "找角";
            if (tool is GrayProjectionFollowTool) return "灰度投影";
            if (tool is OneDCaliperFollowMeasureTool) return "一维卡尺测量";
            if (tool is ArcCaliperFollowMeasureTool) return "一维圆弧卡尺测量";
            if (tool is RectangleFollowMeasureTool) return "矩形测量";
            if (tool is CircleFollowMeasureTool) return "圆形测量";
            return "测量";
        }

        private static string GetShapeName(FollowMeasureToolBase tool)
        {
            if (tool is LineFollowMeasureTool) return "直线";
            if (tool is CornerFindTool) return "两条边";
            if (tool is GrayProjectionFollowTool) return "投影矩形";
            if (tool is OneDCaliperFollowMeasureTool) return "卡尺";
            if (tool is ArcCaliperFollowMeasureTool) return "圆弧卡尺";
            if (tool is RectangleFollowMeasureTool) return "矩形";
            return "圆";
        }
    }
}
