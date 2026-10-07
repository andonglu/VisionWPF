using System;
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
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfMatchToolEditWindow : Window
    {
        private sealed class TeachSettings
        {
            public double AngleStart = -0.39;
            public double AngleExtent = 0.79;
            public double AngleStep = -1;
            public int NumLevels = 0;
            public string Optimization = "auto";
            public string Metric = "use_polarity";
            public int Contrast = -1;
            public int MinContrast = -1;
        }

        private sealed class MatchRow
        {
            public int Index { get; set; }
            public string Row { get; set; }
            public string Column { get; set; }
            public string Angle { get; set; }
            public string Scale { get; set; }
            public string Score { get; set; }
            public MatchResultItem Item { get; set; }
        }

        private static readonly TeachSettings LastTeachSettings = new TeachSettings();

        private readonly IHalconTemplateMatchTool _tool;
        private readonly ToolEditContext _context;
        private readonly List<MatchResultItem> _lastTeachMatches = new List<MatchResultItem>();
        private byte[] _modelData;
        // MT-02 示教记录：以最近一次创建模板时的实际方式为准，确定时写回工具
        private MatchModelSource _teachSource;
        private string _teachMetric;
        private string _teachXldPath;
        private int _teachXldIndex = -1;
        private string _teachDxfPath;
        private double _appliedOriginRow;
        private double _appliedOriginColumn;
        private string _metricBeforeLock;

        public WpfMatchToolEditWindow(IHalconTemplateMatchTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            _modelData = tool.ShapeModelData;

            InitializeComponent();
            Title = "模板匹配 - " + tool.ModuleName;
            InitializeOptions();
            LoadFromTool();
            TryShowSelectedImage();
            ShowLastRunResult();
            RefreshModelState();
            TeachRoiEditor.ImageView.PointPicked += OnOriginPicked;
        }

        private bool HasModel => _modelData != null && _modelData.Length > 0;
        private bool IsGrayMatch => _tool is HalconGrayMatchTool;
        private bool IsScaledShapeMatch => _tool is HalconScaledShapeMatchTool;
        private bool IsDeformableMatch => _tool is HalconLocalDeformableMatchTool;
        private HalconTemplateMatchToolBase TemplateTool => _tool as HalconTemplateMatchToolBase;
        private bool SupportsXld => TemplateTool?.SupportsXldModel == true;
        private bool SupportsOrigin => TemplateTool?.SupportsModelOrigin == true;

        private sealed class SourceChoice
        {
            public SourceChoice(MatchModelSource value, string label)
            {
                Value = value;
                Label = label;
            }

            public MatchModelSource Value { get; private set; }
            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }

        private static readonly SourceChoice[] SourceChoices =
        {
            new SourceChoice(MatchModelSource.ImageRoi, "图像 ROI"),
            new SourceChoice(MatchModelSource.Xld, "XLD 轮廓"),
            new SourceChoice(MatchModelSource.Dxf, "DXF 文件")
        };

        private MatchModelSource SelectedSource =>
            SupportsXld ? (ModelSourceCombo.SelectedItem as SourceChoice)?.Value ?? MatchModelSource.ImageRoi : MatchModelSource.ImageRoi;

        private void InitializeOptions()
        {
            TeachOptimizationCombo.ItemsSource = new[] { "auto", "none", "point_reduction_low", "point_reduction_medium", "point_reduction_high" };
            TeachMetricCombo.ItemsSource = new[] { "use_polarity", "ignore_global_polarity", "ignore_local_polarity", "ignore_color_polarity" };
            SubPixelCombo.ItemsSource = new[] { "least_squares", "least_squares_high", "least_squares_very_high", "interpolation" };
            TeachAngleStepText.ItemsSource = new[] { "auto", "0.01", "0.005", "0.001" };
            TeachNumLevelsText.ItemsSource = new[] { "auto", "0", "1", "2", "3", "4", "5", "6" };
            TeachContrastText.ItemsSource = new[] { "auto", "auto_contrast", "auto_contrast_hyst", "auto_min_size", "10", "20", "30", "40" };
            TeachMinContrastText.ItemsSource = new[] { "auto", "5", "10", "20", "30" };
            ScaleStepText.ItemsSource = new[] { "auto", "0.01", "0.02", "0.05", "0.1" };
            ScaleRowStepText.ItemsSource = new[] { "auto", "0.01", "0.02", "0.05", "0.1" };
            ScaleColumnStepText.ItemsSource = new[] { "auto", "0.01", "0.02", "0.05", "0.1" };
            DeformationSmoothnessText.ItemsSource = new[] { "3", "7", "11", "15", "21" };
            if (IsGrayMatch)
            {
                SubPixelCombo.ItemsSource = new[] { "true", "false" };
            }
            ScaledShapePanel.Visibility = IsScaledShapeMatch ? Visibility.Visible : Visibility.Collapsed;
            DeformablePanel.Visibility = IsDeformableMatch ? Visibility.Visible : Visibility.Collapsed;

            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ImagePathCombo.Items.Add("Input.Image");
            }
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HalconImage)))
                {
                    if (!ImagePathCombo.Items.Contains(candidate.Path))
                    {
                        ImagePathCombo.Items.Add(candidate.Path);
                    }
                }
            }

            SearchRegionCombo.Items.Add(string.Empty);
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HalconRegion)))
                {
                    if (!SearchRegionCombo.Items.Contains(candidate.Path))
                    {
                        SearchRegionCombo.Items.Add(candidate.Path);
                    }
                }
            }
            SortByCombo.ItemsSource = SortChoices;

            ModelSourceCombo.ItemsSource = SourceChoices;
            ModelSourceCard.Visibility = SupportsXld ? Visibility.Visible : Visibility.Collapsed;
            ModelOriginCard.Visibility = SupportsOrigin ? Visibility.Visible : Visibility.Collapsed;
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HalconXld)))
                {
                    if (!TeachXldCombo.Items.Contains(candidate.Path))
                    {
                        TeachXldCombo.Items.Add(candidate.Path);
                    }
                }
            }
        }

        private sealed class SortChoice
        {
            public SortChoice(MatchSortBy value, string label)
            {
                Value = value;
                Label = label;
            }

            public MatchSortBy Value { get; private set; }
            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }

        private static readonly SortChoice[] SortChoices =
        {
            new SortChoice(MatchSortBy.Score, "得分（算子返回顺序）"),
            new SortChoice(MatchSortBy.Row, "行（从上到下）"),
            new SortChoice(MatchSortBy.Column, "列（从左到右）"),
            new SortChoice(MatchSortBy.RowThenColumn, "先行后列（逐行从左到右）"),
            new SortChoice(MatchSortBy.ColumnThenRow, "先列后行（逐列从上到下）")
        };

        private HalconMatchToolBase MatchTool => (HalconMatchToolBase)_tool;

        private MatchSortBy SelectedSortBy => (SortByCombo.SelectedItem as SortChoice)?.Value ?? MatchSortBy.Score;

        private void SortByCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 行容差只在“先行后列”时生效
            Visibility visibility = SelectedSortBy == MatchSortBy.RowThenColumn ? Visibility.Visible : Visibility.Collapsed;
            RowToleranceLabel.Visibility = visibility;
            RowToleranceText.Visibility = visibility;
        }

        private void LoadFromTool()
        {
            ModuleNameText.Text = _tool.ModuleName;
            ImagePathCombo.Text = string.IsNullOrWhiteSpace(_tool.ImagePath) && ImagePathCombo.Items.Count > 0
                ? Convert.ToString(ImagePathCombo.Items[0], CultureInfo.CurrentCulture)
                : _tool.ImagePath ?? string.Empty;

            TeachAngleStartText.Text = Format(LastTeachSettings.AngleStart);
            TeachAngleExtentText.Text = Format(LastTeachSettings.AngleExtent);
            TeachAngleStepText.Text = FormatAuto(LastTeachSettings.AngleStep);
            TeachNumLevelsText.Text = FormatAuto(LastTeachSettings.NumLevels);
            TeachContrastText.Text = FormatContrast(LastTeachSettings.Contrast);
            TeachMinContrastText.Text = FormatAuto(LastTeachSettings.MinContrast);
            TeachOptimizationCombo.Text = LastTeachSettings.Optimization;
            TeachMetricCombo.Text = LastTeachSettings.Metric;

            BaseRowText.Text = Format(_tool.BaseRow);
            BaseColumnText.Text = Format(_tool.BaseColumn);
            BaseAngleText.Text = Format(_tool.BaseAngle);

            FindStartAngleText.Text = Format(_tool.FindStartAngle);
            FindExtentAngleText.Text = Format(_tool.FindExtentAngle);
            MinScoreText.Text = Format(_tool.MinScore);
            NumMatchesText.Text = _tool.NumMatches.ToString(CultureInfo.CurrentCulture);
            MaxOverlapText.Text = Format(_tool.MaxOverlap);
            GreedinessText.Text = Format(_tool.Greediness);
            SubPixelCombo.Text = string.IsNullOrWhiteSpace(_tool.SubPixel) ? "least_squares" : _tool.SubPixel;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
            SearchRegionCombo.Text = MatchTool.SearchRegionPath ?? string.Empty;
            SortByCombo.SelectedItem = SortChoices.First(c => c.Value == MatchTool.SortBy);
            RowToleranceText.Text = Format(MatchTool.RowTolerance);
            SortByCombo_SelectionChanged(null, null);
            if (IsGrayMatch && string.IsNullOrWhiteSpace(SubPixelCombo.Text))
            {
                SubPixelCombo.Text = "true";
            }

            if (_tool is HalconScaledShapeMatchTool scaled)
            {
                ScaleMinText.Text = Format(scaled.ScaleMin);
                ScaleMaxText.Text = Format(scaled.ScaleMax);
                ScaleStepText.Text = "auto";
            }
            if (_tool is HalconLocalDeformableMatchTool deformable)
            {
                ScaleRowMinText.Text = Format(deformable.ScaleRowMin);
                ScaleRowMaxText.Text = Format(deformable.ScaleRowMax);
                ScaleRowStepText.Text = "auto";
                ScaleColumnMinText.Text = Format(deformable.ScaleColumnMin);
                ScaleColumnMaxText.Text = Format(deformable.ScaleColumnMax);
                ScaleColumnStepText.Text = "auto";
                DeformationSmoothnessText.Text = deformable.DeformationSmoothness;
            }

            HalconTemplateMatchToolBase templateTool = TemplateTool;
            if (templateTool != null)
            {
                _teachSource = templateTool.ModelSource;
                _teachMetric = templateTool.TeachMetric;
                _teachXldPath = templateTool.TeachXldPath;
                _teachXldIndex = templateTool.TeachXldIndex;
                _teachDxfPath = templateTool.DxfPath;
                _appliedOriginRow = templateTool.ModelOriginRow;
                _appliedOriginColumn = templateTool.ModelOriginColumn;
            }
            ModelSourceCombo.SelectedItem = SourceChoices.First(c => c.Value == (SupportsXld ? _teachSource : MatchModelSource.ImageRoi));
            TeachXldCombo.Text = _teachXldPath ?? string.Empty;
            TeachXldIndexText.Text = _teachXldIndex.ToString(CultureInfo.CurrentCulture);
            DxfPathText.Text = _teachDxfPath ?? string.Empty;
            ModelOriginRowText.Text = Format(_appliedOriginRow);
            ModelOriginColumnText.Text = Format(_appliedOriginColumn);
            ModelSourceCombo_SelectionChanged(null, null);
        }

        /// <summary>模型来源切换：XLD / DXF 建模时度量锁定为 ignore_local_polarity、对比度不可用。</summary>
        private void ModelSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MatchModelSource source = SelectedSource;
            XldSourcePanel.Visibility = source == MatchModelSource.Xld ? Visibility.Visible : Visibility.Collapsed;
            DxfSourcePanel.Visibility = source == MatchModelSource.Dxf ? Visibility.Visible : Visibility.Collapsed;
            ContourIndexPanel.Visibility = source == MatchModelSource.ImageRoi ? Visibility.Collapsed : Visibility.Visible;
            XldMetricHintText.Visibility = source == MatchModelSource.ImageRoi ? Visibility.Collapsed : Visibility.Visible;
            if (source != MatchModelSource.ImageRoi)
            {
                if (TeachMetricCombo.IsEnabled)
                {
                    _metricBeforeLock = TeachMetricCombo.Text;
                }
                TeachMetricCombo.SelectedItem = MatchModelBuilder.XldMetric;
                TeachMetricCombo.IsEnabled = false;
                TeachContrastText.IsEnabled = false;
            }
            else
            {
                if (!TeachMetricCombo.IsEnabled)
                {
                    TeachMetricCombo.IsEnabled = true;
                    TeachMetricCombo.SelectedItem = _metricBeforeLock ?? LastTeachSettings.Metric;
                }
                TeachContrastText.IsEnabled = true;
            }
        }

        private void RefreshModelState()
        {
            ModelStateText.Text = HasModel ? $"模型：已创建 ({_modelData.Length} bytes)" : "模型：未创建";
            ModelStateText.Foreground = (System.Windows.Media.Brush)FindResource(
                HasModel ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        }

        /// <summary>按引用从上次运行结果取图像（借用，不得释放）；没有运行结果或引用无效时返回 null。</summary>
        private HObject ResolveRunImage(string path)
        {
            if (_context.LastRunContext == null)
            {
                return null;
            }
            try
            {
                object value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
                HObject image = value is HalconImage halconImage ? halconImage.Object : value as HObject;
                return image != null && image.IsInitialized() ? image : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException || ex is ArgumentException)
            {
                return null;
            }
        }

        private void TryShowSelectedImage()
        {
            string path = (ImagePathCombo.Text ?? string.Empty).Trim();
            if (path.Equals("Input.Image", StringComparison.OrdinalIgnoreCase))
            {
                if (_context.InputImage != null && _context.InputImage.IsInitialized())
                {
                    TeachRoiEditor.ShowImage(_context.InputImage);
                    RunImageView.ShowImage(_context.InputImage);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!File.Exists(path))
            {
                // 上游节点的图像输出（如 图像1.Image）取自上次运行结果；示教页的 XLD 建模测试与原点点选需要它
                HObject upstream = ResolveRunImage(path);
                if (upstream != null)
                {
                    TeachRoiEditor.ShowImage(upstream);
                    RunImageView.ShowImage(upstream);
                }
                return;
            }

            HOperatorSet.ReadImage(out HObject image, path);
            try
            {
                TeachRoiEditor.ShowImage(image);
                RunImageView.ShowImage(image);
            }
            finally
            {
                image.Dispose();
            }
        }

        private void ShowLastRunResult()
        {
            if (_context.LastRunContext == null)
            {
                return;
            }

            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "Image", out Variable imageVar)
                && imageVar.Value is HalconImage image)
            {
                RunImageView.ShowImage(image.Object);
            }
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "ResultContour", out Variable contourVar))
            {
                HObject contour = contourVar.Value is HalconXld xld ? xld.Object : contourVar.Value as HObject;
                if (contour != null)
                {
                    RunImageView.SetOverlay(contour);
                }
            }
            if (_context.LastRunContext.TryGetVariable(_tool.ModuleName, "Items", out Variable itemsVar))
            {
                IEnumerable<MatchResultItem> items = Enumerable.Empty<MatchResultItem>();
                if (itemsVar.Value is MatchResultItem[] array)
                {
                    items = array;
                }
                else if (itemsVar.Value is List<MatchResultItem> list)
                {
                    items = list;
                }
                RunResultGrid.ItemsSource = ToRows(items);
            }
        }

        private void CreateTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSource != MatchModelSource.ImageRoi)
            {
                CreateTemplateFromXld(SelectedSource);
                return;
            }

            if (TeachRoiEditor.ImageView.ImageObject == null)
            {
                ShowInfo("请先打开或显示一张图像。");
                return;
            }

            try
            {
                SaveTeachSettings();
                ReadOriginTexts(out double originRow, out double originColumn);
                using HRegion region = TeachRoiEditor.BuildRegion();
                HOperatorSet.AreaCenter(region, out HTuple area, out HTuple teachRows, out HTuple teachColumns);
                if (area.Length == 0 || area.D <= 0)
                {
                    ShowInfo("请先绘制模板区域 ROI。多个包含 ROI 会合并，排除 ROI 会扣除。");
                    return;
                }
                double teachRow = teachRows.D;
                double teachColumn = teachColumns.D;
                TeachRoiEditor.ImageView.SetOverlay(region);
                HOperatorSet.ReduceDomain(TeachRoiEditor.ImageView.ImageObject, region, out HObject reduced);
                try
                {
                    HTuple modelId = CreateModel(reduced);
                    try
                    {
                        // 原点写入模型本身；偏移为 0 时不调用算子，模型字节与原来完全相同
                        if (SupportsOrigin)
                        {
                            MatchModelBuilder.ApplyOrigin(modelId, IsGrayMatch, originRow, originColumn);
                        }
                        _modelData = SerializeModel(modelId);
                        RecordTeach(MatchModelSource.ImageRoi, originRow, originColumn);
                        RefreshModelState();
                        double testStartAngle = ParseDouble(TeachAngleStartText.Text, "起始角");
                        double testExtentAngle = ParseDouble(TeachAngleExtentText.Text, "角范围");
                        const double testMinScore = 0.5;
                        const int testNumMatches = 1;
                        const double testMaxOverlap = 0.5;
                        const string testSubPixel = "least_squares";
                        const int testNumLevels = 0;
                        const double testGreediness = 0.9;
                        int matchCount = ExecuteMatch(modelId, TeachRoiEditor.ImageView.ImageObject, TeachRoiEditor.ImageView,
                            TeachResultGrid,
                            testStartAngle,
                            testExtentAngle,
                            testMinScore,
                            testNumMatches,
                            testMaxOverlap,
                            testSubPixel,
                            testNumLevels,
                            testGreediness,
                            updateTeachMatches: true,
                            expectedRow: teachRow + originRow,
                            expectedColumn: teachColumn + originColumn);
                        if (_lastTeachMatches.Count > 0)
                        {
                            BaseRowText.Text = Format(_lastTeachMatches[0].Row);
                            BaseColumnText.Text = Format(_lastTeachMatches[0].Column);
                            BaseAngleText.Text = Format(_lastTeachMatches[0].Angle);
                        }
                        int persistedMatchCount = CountMatches(DeserializeModel(),
                            TeachRoiEditor.ImageView.ImageObject,
                            testStartAngle,
                            testExtentAngle,
                            testMinScore,
                            testNumMatches,
                            testMaxOverlap,
                            testSubPixel,
                            testNumLevels,
                            testGreediness);
                        string diagnostic = matchCount == 0
                            ? "；低阈值诊断最高分=" + Format(GetBestScore(modelId, TeachRoiEditor.ImageView.ImageObject, testStartAngle, testExtentAngle, testMaxOverlap, testSubPixel, testNumLevels))
                            : string.Empty;
                        SetStatus($"模板创建成功。原始模型匹配 {matchCount} 个，持久化模型匹配 {persistedMatchCount} 个；示教重心=({teachRow:F1},{teachColumn:F1}){diagnostic}。");
                    }
                    finally
                    {
                        ClearModel(modelId);
                    }
                }
                finally
                {
                    reduced.Dispose();
                }
            }
            catch (Exception ex)
            {
                ShowError("创建模板失败：" + ex.Message);
            }
        }

        private void ReadOriginTexts(out double row, out double column)
        {
            row = 0;
            column = 0;
            if (SupportsOrigin)
            {
                row = ParseDouble(ModelOriginRowText.Text, "原点行偏移");
                column = ParseDouble(ModelOriginColumnText.Text, "原点列偏移");
            }
        }

        /// <summary>记录本次建模的方式与已写入模型的原点（确定时写回工具）。</summary>
        private void RecordTeach(MatchModelSource source, double originRow, double originColumn)
        {
            _teachSource = source;
            _teachMetric = source == MatchModelSource.ImageRoi ? TeachMetricCombo.Text : MatchModelBuilder.XldMetric;
            _teachXldPath = source == MatchModelSource.Xld ? (TeachXldCombo.Text ?? string.Empty).Trim() : null;
            _teachDxfPath = source == MatchModelSource.Dxf ? (DxfPathText.Text ?? string.Empty).Trim() : null;
            _teachXldIndex = source == MatchModelSource.ImageRoi ? -1 : ParseInt(TeachXldIndexText.Text, "轮廓序号");
            _appliedOriginRow = originRow;
            _appliedOriginColumn = originColumn;
            OriginBaseHintText.Visibility = Visibility.Collapsed;
        }

        /// <summary>从上游 XLD 或 DXF 文件建模（MT-02）：度量固定 ignore_local_polarity；示教图像中能找到时用于测试与确定基准。</summary>
        private void CreateTemplateFromXld(MatchModelSource source)
        {
            HObject owned = null;
            HObject selected = null;
            try
            {
                SaveTeachSettings();
                ReadOriginTexts(out double originRow, out double originColumn);
                int index = ParseInt(TeachXldIndexText.Text, "轮廓序号");
                HObject contours;
                if (source == MatchModelSource.Xld)
                {
                    contours = ResolveXld((TeachXldCombo.Text ?? string.Empty).Trim());
                }
                else
                {
                    owned = MatchModelBuilder.ReadDxf((DxfPathText.Text ?? string.Empty).Trim());
                    contours = owned;
                }
                selected = MatchModelBuilder.SelectContours(contours, index);
                HOperatorSet.CountObj(selected, out HTuple contourCount);
                int minContrast = ParseAutoInt(TeachMinContrastText.Text, "最小对比");
                HTuple minContrastValue = minContrast < 0 ? new HTuple(5) : new HTuple(minContrast);
                HTuple modelId = IsScaledShapeMatch
                    ? MatchModelBuilder.CreateScaledShapeModelXld(selected,
                        ToAuto(ParseAutoInt(TeachNumLevelsText.Text, "金字塔")),
                        ParseDouble(TeachAngleStartText.Text, "起始角"),
                        ParseDouble(TeachAngleExtentText.Text, "角范围"),
                        ToAuto(ParseAutoDouble(TeachAngleStepText.Text, "角步长")),
                        ParseDouble(ScaleMinText.Text, "最小缩放"),
                        ParseDouble(ScaleMaxText.Text, "最大缩放"),
                        ToAuto(ParseAutoDouble(ScaleStepText.Text, "缩放步长")),
                        TeachOptimizationCombo.Text,
                        minContrastValue)
                    : MatchModelBuilder.CreateShapeModelXld(selected,
                        ToAuto(ParseAutoInt(TeachNumLevelsText.Text, "金字塔")),
                        ParseDouble(TeachAngleStartText.Text, "起始角"),
                        ParseDouble(TeachAngleExtentText.Text, "角范围"),
                        ToAuto(ParseAutoDouble(TeachAngleStepText.Text, "角步长")),
                        TeachOptimizationCombo.Text,
                        minContrastValue);
                try
                {
                    MatchModelBuilder.ApplyOrigin(modelId, false, originRow, originColumn);
                    _modelData = SerializeModel(modelId);
                    RecordTeach(source, originRow, originColumn);
                    RefreshModelState();
                    int matchCount = 0;
                    if (TeachRoiEditor.ImageView.ImageObject != null)
                    {
                        // XLD 模型的参考点是轮廓外接矩形中心
                        HOperatorSet.SmallestRectangle1Xld(selected, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                        double centerRow = (r1.TupleMin().D + r2.TupleMax().D) / 2.0 + originRow;
                        double centerColumn = (c1.TupleMin().D + c2.TupleMax().D) / 2.0 + originColumn;
                        matchCount = ExecuteMatch(modelId, TeachRoiEditor.ImageView.ImageObject, TeachRoiEditor.ImageView, TeachResultGrid,
                            ParseDouble(TeachAngleStartText.Text, "起始角"), ParseDouble(TeachAngleExtentText.Text, "角范围"),
                            0.5, 1, 0.5, "least_squares", 0, 0.9, updateTeachMatches: true,
                            expectedRow: centerRow, expectedColumn: centerColumn);
                        if (_lastTeachMatches.Count > 0)
                        {
                            BaseRowText.Text = Format(_lastTeachMatches[0].Row);
                            BaseColumnText.Text = Format(_lastTeachMatches[0].Column);
                            BaseAngleText.Text = Format(_lastTeachMatches[0].Angle);
                        }
                    }
                    SetStatus($"已从{(source == MatchModelSource.Xld ? "XLD 轮廓" : "DXF 文件")}创建模板：{contourCount.I} 条轮廓，度量 {MatchModelBuilder.XldMetric}；示教图像中匹配 {matchCount} 个。");
                }
                finally
                {
                    ClearModel(modelId);
                }
            }
            catch (Exception ex)
            {
                ShowError("创建模板失败：" + ex.Message);
            }
            finally
            {
                selected?.Dispose();
                owned?.Dispose();
            }
        }

        /// <summary>取上游 XLD 输出（借用上次运行的结果，不得释放）。</summary>
        private HObject ResolveXld(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("请选择上游 XLD 输出");
            }
            if (_context.LastRunContext == null)
            {
                throw new InvalidOperationException("XLD 引用需要先运行一次流程：" + path);
            }
            object value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
            HObject xld = value is HalconXld halconXld ? halconXld.Object : value as HObject;
            if (xld == null)
            {
                throw new InvalidOperationException("引用不是 XLD 轮廓：" + path);
            }
            return xld;
        }

        private void BrowseDxf_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "DXF 文件|*.dxf|所有文件|*.*" };
            if (dialog.ShowDialog(this) == true)
            {
                DxfPathText.Text = dialog.FileName;
            }
        }

        private void PickOrigin_Click(object sender, RoutedEventArgs e)
        {
            if (!HasModel || TeachRoiEditor.ImageView.ImageObject == null)
            {
                ShowInfo("请先创建模板并显示示教图像。");
                return;
            }
            if (_lastTeachMatches.Count == 0)
            {
                RefreshTeachMatches(_appliedOriginRow, _appliedOriginColumn);
            }
            if (_lastTeachMatches.Count == 0)
            {
                ShowInfo("示教图像中没有找到模板，无法确定模板参考点。");
                return;
            }
            TeachRoiEditor.ImageView.BeginPickPoint();
            SetStatus("请在示教图像上点选模型原点（按住可拖动，松开结束），然后点“应用原点”。");
        }

        private void OnOriginPicked(object sender, ImagePointEventArgs e)
        {
            if (_lastTeachMatches.Count == 0)
            {
                return;
            }
            MatchResultItem match = _lastTeachMatches[0];
            MatchModelBuilder.OriginFromPickedPoint(e.Row, e.Column, match.Row, match.Column,
                _appliedOriginRow, _appliedOriginColumn, out double row, out double column);
            ModelOriginRowText.Text = Format(Math.Round(row, 2));
            ModelOriginColumnText.Text = Format(Math.Round(column, 2));
            TeachRoiEditor.ImageView.SetMarker(e.Row, e.Column);
            SetStatus($"已点选原点：图像 ({e.Row:F2}, {e.Column:F2})，相对参考点偏移 ({row:F2}, {column:F2})。点“应用原点”写入模型。");
        }

        private void ApplyOrigin_Click(object sender, RoutedEventArgs e)
        {
            if (!HasModel)
            {
                ShowInfo("请先创建模板。");
                return;
            }
            try
            {
                ApplyOriginToModel();
            }
            catch (Exception ex)
            {
                ShowError("应用原点失败：" + ex.Message);
            }
        }

        /// <summary>把界面上的原点写入已创建的模型（替换模型数据），重新做示教测试，并提示重新确定跟随基准。</summary>
        private void ApplyOriginToModel()
        {
            ReadOriginTexts(out double row, out double column);
            double previousRow = _appliedOriginRow;
            double previousColumn = _appliedOriginColumn;
            HTuple modelId = DeserializeModel();
            try
            {
                MatchModelBuilder.SetOrigin(modelId, IsGrayMatch, row, column);
                _modelData = SerializeModel(modelId);
            }
            finally
            {
                ClearModel(modelId);
            }
            _appliedOriginRow = row;
            _appliedOriginColumn = column;
            RefreshModelState();
            if (row != previousRow || column != previousColumn)
            {
                OriginBaseHintText.Visibility = Visibility.Visible;
            }
            RefreshTeachMatches(previousRow, previousColumn);
            if (_lastTeachMatches.Count > 0)
            {
                TeachRoiEditor.ImageView.SetMarker(_lastTeachMatches[0].Row, _lastTeachMatches[0].Column);
            }
            SetStatus($"模型原点已更新为 ({row:F2}, {column:F2})；匹配输出的 Row / Column 即为该点。请重新确定跟随基准（基准 Row / Col / 角）。");
        }

        /// <summary>
        /// 用当前模型在示教图像上重新做示教测试；按“原示教实例 + 原点变化量”的位置排序，保证第一项仍是原来的示教实例。
        /// </summary>
        private void RefreshTeachMatches(double previousOriginRow, double previousOriginColumn)
        {
            if (!HasModel || TeachRoiEditor.ImageView.ImageObject == null)
            {
                return;
            }
            double? expectedRow = null;
            double? expectedColumn = null;
            if (_lastTeachMatches.Count > 0)
            {
                expectedRow = _lastTeachMatches[0].Row - previousOriginRow + _appliedOriginRow;
                expectedColumn = _lastTeachMatches[0].Column - previousOriginColumn + _appliedOriginColumn;
            }
            HTuple modelId = DeserializeModel();
            try
            {
                ExecuteMatch(modelId, TeachRoiEditor.ImageView.ImageObject, TeachRoiEditor.ImageView, TeachResultGrid,
                    ParseDouble(TeachAngleStartText.Text, "起始角"), ParseDouble(TeachAngleExtentText.Text, "角范围"),
                    0.5, 10, 0.5, IsGrayMatch ? "true" : "least_squares", 0, 0.9, updateTeachMatches: true,
                    expectedRow: expectedRow, expectedColumn: expectedColumn);
            }
            finally
            {
                ClearModel(modelId);
            }
        }

        /// <summary>
        /// “运行参数”页的测试：把界面参数写入工具的一次性副本，按运行时的完整流程执行（含搜索区域与结果排序），
        /// 与流程运行结果一致；窗口持有的工具不被修改，参数只在“确定”时写入。
        /// 图像引用基于上次运行结果，Input.Image 取主窗口当前图像。
        /// </summary>
        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            if (!HasModel)
            {
                ShowInfo("请先创建模板。");
                return;
            }

            ToolTestRun run = null;
            try
            {
                run = ToolTestRun.Run((ToolBase)_tool, copy => ApplyTo((IHalconTemplateMatchTool)copy),
                    _context.LastRunContext, _context.InputImage);
                FlowContext ctx = run.Context;
                NodeResult result = run.Result;
                string module = run.ModuleName;
                if (ctx.TryGetVariable(module, "Image", out Variable imageVar) && imageVar.Value is HalconImage image)
                {
                    RunImageView.ShowImage(image.Object);
                }
                if (ctx.TryGetVariable(module, "ResultContour", out Variable contourVar) && contourVar.Value is HalconXld contour)
                {
                    RunImageView.SetOverlay(contour.Object);
                }
                else
                {
                    RunImageView.ClearOverlay();
                }
                IEnumerable<MatchResultItem> items = ctx.TryGetVariable(module, "Items", out Variable itemsVar)
                    && itemsVar.Value is IEnumerable<MatchResultItem> found ? found : Enumerable.Empty<MatchResultItem>();
                RunResultGrid.ItemsSource = ToRows(items);
                double bestScore = ctx.TryGetVariable(module, "Score", out Variable scoreVar) ? Convert.ToDouble(scoreVar.Value, CultureInfo.CurrentCulture) : double.NaN;
                SetStatus(result.IsSuccess
                    ? $"运行测试完成：{RunResultGrid.Items.Count} 个结果（按{SortByCombo.SelectedItem}排序），最佳得分 {bestScore:F4}。"
                    : "运行测试：" + result.Message);
            }
            catch (Exception ex)
            {
                ShowError("运行测试失败：" + ex.Message);
            }
            finally
            {
                run?.Dispose();
            }
        }

        private int ExecuteMatch(HTuple modelId, HObject image, HalconImageView view, DataGrid grid,
            double startAngle, double extentAngle, double minScore, int numMatches,
            double maxOverlap, string subPixel, int numLevels, double greediness, bool updateTeachMatches,
            double? expectedRow = null, double? expectedColumn = null)
        {
            if (IsGrayMatch)
            {
                return ExecuteGrayMatch(modelId, image, view, grid, startAngle, extentAngle, minScore, numMatches,
                    maxOverlap, subPixel, numLevels, updateTeachMatches, expectedRow, expectedColumn);
            }
            if (IsScaledShapeMatch)
            {
                return ExecuteScaledShapeMatch(modelId, image, view, grid, startAngle, extentAngle, minScore, numMatches,
                    maxOverlap, subPixel, numLevels, greediness, updateTeachMatches, expectedRow, expectedColumn);
            }
            if (IsDeformableMatch)
            {
                return ExecuteDeformableMatch(modelId, image, view, grid, startAngle, extentAngle, minScore, numMatches,
                    maxOverlap, numLevels, greediness, updateTeachMatches);
            }

            HObject modelContours = null;
            HObject resultContour = null;
            HObject searchImage = null;
            var rows = new List<MatchResultItem>();
            try
            {
                HOperatorSet.FullDomain(image, out searchImage);
                HOperatorSet.GetShapeModelContours(out modelContours, modelId, 1);
                HOperatorSet.FindShapeModel(searchImage, modelId, startAngle, extentAngle, minScore,
                    numMatches, maxOverlap, subPixel, numLevels, greediness,
                    out HTuple foundRows, out HTuple foundColumns, out HTuple foundAngles, out HTuple scores);

                for (int i = 0; i < foundRows.Length; i++)
                {
                    rows.Add(new MatchResultItem
                    {
                        Index = i,
                        Row = foundRows[i].D,
                        Column = foundColumns[i].D,
                        Angle = foundAngles[i].D,
                        Score = scores[i].D
                    });
                }

                if (expectedRow.HasValue && expectedColumn.HasValue)
                {
                    rows = rows
                        .OrderBy(item => Distance(item.Row, item.Column, expectedRow.Value, expectedColumn.Value))
                        .ThenByDescending(item => item.Score)
                        .ToList();
                    for (int i = 0; i < rows.Count; i++)
                    {
                        rows[i].Index = i;
                    }
                }

                HOperatorSet.GenEmptyObj(out resultContour);
                foreach (MatchResultItem item in rows)
                {
                    HOperatorSet.VectorAngleToRigid(0, 0, 0, item.Row, item.Column, item.Angle, out HTuple mat);
                    HOperatorSet.AffineTransContourXld(modelContours, out HObject contour, mat);
                    HOperatorSet.ConcatObj(resultContour, contour, out HObject concat);
                    resultContour.Dispose();
                    resultContour = concat;
                    contour.Dispose();
                }

                if (updateTeachMatches)
                {
                    _lastTeachMatches.Clear();
                    _lastTeachMatches.AddRange(rows);
                }
                grid.ItemsSource = ToRows(rows);
                view.SetOverlay(resultContour);
                return rows.Count;
            }
            finally
            {
                searchImage?.Dispose();
                modelContours?.Dispose();
                resultContour?.Dispose();
            }
        }

        private static double Distance(double row1, double column1, double row2, double column2)
        {
            double rowDelta = row1 - row2;
            double columnDelta = column1 - column2;
            return Math.Sqrt(rowDelta * rowDelta + columnDelta * columnDelta);
        }

        private int ExecuteGrayMatch(HTuple modelId, HObject image, HalconImageView view, DataGrid grid,
            double startAngle, double extentAngle, double minScore, int numMatches,
            double maxOverlap, string subPixel, int numLevels, bool updateTeachMatches,
            double? expectedRow, double? expectedColumn)
        {
            HObject modelRegion = null;
            HObject modelContours = null;
            HObject resultContour = null;
            HObject searchImage = null;
            var rows = new List<MatchResultItem>();
            try
            {
                HOperatorSet.FullDomain(image, out searchImage);
                HOperatorSet.GetNccModelRegion(out modelRegion, modelId);
                HOperatorSet.GenContourRegionXld(modelRegion, out modelContours, "border");
                HOperatorSet.FindNccModel(searchImage, modelId, startAngle, extentAngle, minScore,
                    numMatches, maxOverlap, string.IsNullOrWhiteSpace(subPixel) ? "true" : subPixel, numLevels,
                    out HTuple foundRows, out HTuple foundColumns, out HTuple foundAngles, out HTuple scores);
                for (int i = 0; i < foundRows.Length; i++)
                {
                    rows.Add(new MatchResultItem
                    {
                        Index = i,
                        Row = foundRows[i].D,
                        Column = foundColumns[i].D,
                        Angle = foundAngles[i].D,
                        Score = scores[i].D
                    });
                }
                SortByExpected(rows, expectedRow, expectedColumn);
                HOperatorSet.GenEmptyObj(out resultContour);
                foreach (MatchResultItem item in rows)
                {
                    HOperatorSet.VectorAngleToRigid(0, 0, 0, item.Row, item.Column, item.Angle, out HTuple mat);
                    HOperatorSet.AffineTransContourXld(modelContours, out HObject contour, mat);
                    HOperatorSet.ConcatObj(resultContour, contour, out HObject concat);
                    resultContour.Dispose();
                    resultContour = concat;
                    contour.Dispose();
                }
                if (updateTeachMatches)
                {
                    _lastTeachMatches.Clear();
                    _lastTeachMatches.AddRange(rows);
                }
                grid.ItemsSource = ToRows(rows);
                view.SetOverlay(resultContour);
                return rows.Count;
            }
            finally
            {
                searchImage?.Dispose();
                modelRegion?.Dispose();
                modelContours?.Dispose();
                resultContour?.Dispose();
            }
        }

        private int ExecuteScaledShapeMatch(HTuple modelId, HObject image, HalconImageView view, DataGrid grid,
            double startAngle, double extentAngle, double minScore, int numMatches,
            double maxOverlap, string subPixel, int numLevels, double greediness, bool updateTeachMatches,
            double? expectedRow, double? expectedColumn)
        {
            HObject modelContours = null;
            HObject resultContour = null;
            HObject searchImage = null;
            var rows = new List<MatchResultItem>();
            try
            {
                HOperatorSet.FullDomain(image, out searchImage);
                HOperatorSet.GetShapeModelContours(out modelContours, modelId, 1);
                HOperatorSet.FindScaledShapeModel(searchImage, modelId, startAngle, extentAngle,
                    ParseDouble(ScaleMinText.Text, "最小缩放"), ParseDouble(ScaleMaxText.Text, "最大缩放"),
                    minScore, numMatches, maxOverlap, subPixel, numLevels, greediness,
                    out HTuple foundRows, out HTuple foundColumns, out HTuple foundAngles, out HTuple scales, out HTuple scores);
                for (int i = 0; i < foundRows.Length; i++)
                {
                    rows.Add(new MatchResultItem
                    {
                        Index = i,
                        Row = foundRows[i].D,
                        Column = foundColumns[i].D,
                        Angle = foundAngles[i].D,
                        Scale = scales[i].D,
                        Score = scores[i].D
                    });
                }
                SortByExpected(rows, expectedRow, expectedColumn);
                HOperatorSet.GenEmptyObj(out resultContour);
                foreach (MatchResultItem item in rows)
                {
                    HomMat2D mat = HomMat2D.FromScaledPose(item.Row, item.Column, item.Angle, item.Scale);
                    HOperatorSet.AffineTransContourXld(modelContours, out HObject contour, mat.Data);
                    HOperatorSet.ConcatObj(resultContour, contour, out HObject concat);
                    resultContour.Dispose();
                    resultContour = concat;
                    contour.Dispose();
                }
                if (updateTeachMatches)
                {
                    _lastTeachMatches.Clear();
                    _lastTeachMatches.AddRange(rows);
                }
                grid.ItemsSource = ToRows(rows);
                view.SetOverlay(resultContour);
                return rows.Count;
            }
            finally
            {
                searchImage?.Dispose();
                modelContours?.Dispose();
                resultContour?.Dispose();
            }
        }

        private int ExecuteDeformableMatch(HTuple modelId, HObject image, HalconImageView view, DataGrid grid,
            double startAngle, double extentAngle, double minScore, int numMatches,
            double maxOverlap, int numLevels, double greediness, bool updateTeachMatches)
        {
            HObject searchImage = null;
            HObject imageRectified = null;
            HObject vectorField = null;
            HObject deformedContours = null;
            var rows = new List<MatchResultItem>();
            try
            {
                HOperatorSet.FullDomain(image, out searchImage);
                BuildDeformableFindParams(out HTuple genNames, out HTuple genValues);
                HOperatorSet.FindLocalDeformableModel(searchImage, out imageRectified, out vectorField, out deformedContours,
                    modelId, startAngle, extentAngle,
                    ParseDouble(ScaleRowMinText.Text, "行缩放最小"), ParseDouble(ScaleRowMaxText.Text, "行缩放最大"),
                    ParseDouble(ScaleColumnMinText.Text, "列缩放最小"), ParseDouble(ScaleColumnMaxText.Text, "列缩放最大"),
                    minScore, numMatches, maxOverlap, numLevels, greediness, "deformed_contours", genNames, genValues,
                    out HTuple scores, out HTuple foundRows, out HTuple foundColumns);
                for (int i = 0; i < foundRows.Length; i++)
                {
                    rows.Add(new MatchResultItem
                    {
                        Index = i,
                        Row = foundRows[i].D,
                        Column = foundColumns[i].D,
                        Score = scores[i].D
                    });
                }
                if (updateTeachMatches)
                {
                    _lastTeachMatches.Clear();
                    _lastTeachMatches.AddRange(rows);
                }
                grid.ItemsSource = ToRows(rows);
                view.SetOverlay(deformedContours);
                return rows.Count;
            }
            finally
            {
                searchImage?.Dispose();
                imageRectified?.Dispose();
                vectorField?.Dispose();
                deformedContours?.Dispose();
            }
        }

        private static void SortByExpected(List<MatchResultItem> rows, double? expectedRow, double? expectedColumn)
        {
            if (!expectedRow.HasValue || !expectedColumn.HasValue)
            {
                return;
            }
            var sorted = rows
                .OrderBy(item => Distance(item.Row, item.Column, expectedRow.Value, expectedColumn.Value))
                .ThenByDescending(item => item.Score)
                .ToList();
            rows.Clear();
            rows.AddRange(sorted);
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Index = i;
            }
        }

        private HTuple CreateModel(HObject reduced)
        {
            if (IsGrayMatch)
            {
                HOperatorSet.CreateNccModel(reduced,
                    ToAuto(ParseAutoInt(TeachNumLevelsText.Text, "金字塔")),
                    ParseDouble(TeachAngleStartText.Text, "起始角"),
                    ParseDouble(TeachAngleExtentText.Text, "角范围"),
                    ToAuto(ParseAutoDouble(TeachAngleStepText.Text, "角步长")),
                    TeachMetricCombo.Text,
                    out HTuple nccModelId);
                return nccModelId;
            }
            if (IsScaledShapeMatch)
            {
                HOperatorSet.CreateScaledShapeModel(reduced,
                    ToAuto(ParseAutoInt(TeachNumLevelsText.Text, "金字塔")),
                    ParseDouble(TeachAngleStartText.Text, "起始角"),
                    ParseDouble(TeachAngleExtentText.Text, "角范围"),
                    ToAuto(ParseAutoDouble(TeachAngleStepText.Text, "角步长")),
                    ParseDouble(ScaleMinText.Text, "最小缩放"),
                    ParseDouble(ScaleMaxText.Text, "最大缩放"),
                    ToAuto(ParseAutoDouble(ScaleStepText.Text, "缩放步长")),
                    TeachOptimizationCombo.Text,
                    TeachMetricCombo.Text,
                    ToContrast(ParseContrast(TeachContrastText.Text, "对比度")),
                    ToAuto(ParseAutoInt(TeachMinContrastText.Text, "最小对比")),
                    out HTuple scaledModelId);
                return scaledModelId;
            }
            if (IsDeformableMatch)
            {
                HOperatorSet.CreateLocalDeformableModel(reduced,
                    ToAuto(ParseAutoInt(TeachNumLevelsText.Text, "金字塔")),
                    ParseDouble(TeachAngleStartText.Text, "起始角"),
                    ParseDouble(TeachAngleExtentText.Text, "角范围"),
                    ToAuto(ParseAutoDouble(TeachAngleStepText.Text, "角步长")),
                    ParseDouble(ScaleRowMinText.Text, "行缩放最小"),
                    ParseDouble(ScaleRowMaxText.Text, "行缩放最大"),
                    ToAuto(ParseAutoDouble(ScaleRowStepText.Text, "行缩放步长")),
                    ParseDouble(ScaleColumnMinText.Text, "列缩放最小"),
                    ParseDouble(ScaleColumnMaxText.Text, "列缩放最大"),
                    ToAuto(ParseAutoDouble(ScaleColumnStepText.Text, "列缩放步长")),
                    TeachOptimizationCombo.Text,
                    TeachMetricCombo.Text,
                    ToContrast(ParseContrast(TeachContrastText.Text, "对比度")),
                    ToAuto(ParseAutoInt(TeachMinContrastText.Text, "最小对比")),
                    new HTuple(), new HTuple(),
                    out HTuple deformableModelId);
                return deformableModelId;
            }

            HOperatorSet.CreateShapeModel(reduced,
                ToAuto(ParseAutoInt(TeachNumLevelsText.Text, "金字塔")),
                ParseDouble(TeachAngleStartText.Text, "起始角"),
                ParseDouble(TeachAngleExtentText.Text, "角范围"),
                ToAuto(ParseAutoDouble(TeachAngleStepText.Text, "角步长")),
                TeachOptimizationCombo.Text,
                TeachMetricCombo.Text,
                ToContrast(ParseContrast(TeachContrastText.Text, "对比度")),
                ToAuto(ParseAutoInt(TeachMinContrastText.Text, "最小对比")),
                out HTuple modelId);
            return modelId;
        }

        private byte[] SerializeModel(HTuple modelId)
        {
            if (IsGrayMatch)
            {
                return ShapeModelSerialization.SerializeNcc(modelId);
            }
            if (IsDeformableMatch)
            {
                return ShapeModelSerialization.SerializeDeformable(modelId);
            }
            return ShapeModelSerialization.Serialize(modelId);
        }

        private HTuple DeserializeModel()
        {
            if (IsGrayMatch)
            {
                return ShapeModelSerialization.DeserializeNcc(_modelData);
            }
            if (IsDeformableMatch)
            {
                return ShapeModelSerialization.DeserializeDeformable(_modelData);
            }
            return ShapeModelSerialization.Deserialize(_modelData);
        }

        private void ClearModel(HTuple modelId)
        {
            if (modelId == null)
            {
                return;
            }
            if (IsGrayMatch)
            {
                HOperatorSet.ClearNccModel(modelId);
            }
            else if (IsDeformableMatch)
            {
                HOperatorSet.ClearDeformableModel(modelId);
            }
            else
            {
                HOperatorSet.ClearShapeModel(modelId);
            }
        }

        private void BuildDeformableFindParams(out HTuple names, out HTuple values)
        {
            names = new HTuple();
            values = new HTuple();
            if (!string.IsNullOrWhiteSpace(DeformationSmoothnessText.Text))
            {
                names = names.TupleConcat("deformation_smoothness");
                values = values.TupleConcat(DeformationSmoothnessText.Text.Trim());
            }
        }

        private int CountMatches(HTuple modelId, HObject image,
            double startAngle, double extentAngle, double minScore, int numMatches,
            double maxOverlap, string subPixel, int numLevels, double greediness)
        {
            HObject searchImage = null;
            HObject imageRectified = null;
            HObject vectorField = null;
            HObject deformedContours = null;
            try
            {
                HOperatorSet.FullDomain(image, out searchImage);
                if (IsGrayMatch)
                {
                    HOperatorSet.FindNccModel(searchImage, modelId, startAngle, extentAngle, minScore,
                        numMatches, maxOverlap, string.IsNullOrWhiteSpace(subPixel) ? "true" : subPixel, numLevels,
                        out HTuple rows, out _, out _, out _);
                    return rows.Length;
                }
                if (IsScaledShapeMatch)
                {
                    HOperatorSet.FindScaledShapeModel(searchImage, modelId, startAngle, extentAngle,
                        ParseDouble(ScaleMinText.Text, "最小缩放"), ParseDouble(ScaleMaxText.Text, "最大缩放"),
                        minScore, numMatches, maxOverlap, subPixel, numLevels, greediness,
                        out HTuple rows, out _, out _, out _, out _);
                    return rows.Length;
                }
                if (IsDeformableMatch)
                {
                    BuildDeformableFindParams(out HTuple genNames, out HTuple genValues);
                    HOperatorSet.FindLocalDeformableModel(searchImage, out imageRectified, out vectorField, out deformedContours,
                        modelId, startAngle, extentAngle,
                        ParseDouble(ScaleRowMinText.Text, "行缩放最小"), ParseDouble(ScaleRowMaxText.Text, "行缩放最大"),
                        ParseDouble(ScaleColumnMinText.Text, "列缩放最小"), ParseDouble(ScaleColumnMaxText.Text, "列缩放最大"),
                        minScore, numMatches, maxOverlap, numLevels, greediness, new HTuple(), genNames, genValues,
                        out _, out HTuple rows, out _);
                    return rows.Length;
                }
                HOperatorSet.FindShapeModel(searchImage, modelId, startAngle, extentAngle, minScore,
                    numMatches, maxOverlap, subPixel, numLevels, greediness,
                    out HTuple shapeRows, out _, out _, out _);
                return shapeRows.Length;
            }
            finally
            {
                searchImage?.Dispose();
                imageRectified?.Dispose();
                vectorField?.Dispose();
                deformedContours?.Dispose();
                ClearModel(modelId);
            }
        }

        private double GetBestScore(HTuple modelId, HObject image,
            double startAngle, double extentAngle, double maxOverlap, string subPixel, int numLevels)
        {
            HObject searchImage = null;
            HObject imageRectified = null;
            HObject vectorField = null;
            HObject deformedContours = null;
            try
            {
                HOperatorSet.FullDomain(image, out searchImage);
                HTuple scores;
                if (IsGrayMatch)
                {
                    HOperatorSet.FindNccModel(searchImage, modelId, startAngle, extentAngle, 0.0,
                        10, maxOverlap, string.IsNullOrWhiteSpace(subPixel) ? "true" : subPixel, numLevels,
                        out _, out _, out _, out scores);
                }
                else if (IsScaledShapeMatch)
                {
                    HOperatorSet.FindScaledShapeModel(searchImage, modelId, startAngle, extentAngle,
                        ParseDouble(ScaleMinText.Text, "最小缩放"), ParseDouble(ScaleMaxText.Text, "最大缩放"),
                        0.0, 10, maxOverlap, subPixel, numLevels, 0.0,
                        out _, out _, out _, out _, out scores);
                }
                else if (IsDeformableMatch)
                {
                    BuildDeformableFindParams(out HTuple genNames, out HTuple genValues);
                    HOperatorSet.FindLocalDeformableModel(searchImage, out imageRectified, out vectorField, out deformedContours,
                        modelId, startAngle, extentAngle,
                        ParseDouble(ScaleRowMinText.Text, "行缩放最小"), ParseDouble(ScaleRowMaxText.Text, "行缩放最大"),
                        ParseDouble(ScaleColumnMinText.Text, "列缩放最小"), ParseDouble(ScaleColumnMaxText.Text, "列缩放最大"),
                        0.0, 10, maxOverlap, numLevels, 0.0, "deformed_contours", genNames, genValues,
                        out scores, out _, out _);
                }
                else
                {
                    HOperatorSet.FindShapeModel(searchImage, modelId, startAngle, extentAngle, 0.0,
                        10, maxOverlap, subPixel, numLevels, 0.0,
                        out _, out _, out _, out scores);
                }
                if (scores.Length == 0)
                {
                    return 0;
                }
                return scores.DArr.Max();
            }
            finally
            {
                searchImage?.Dispose();
                imageRectified?.Dispose();
                vectorField?.Dispose();
                deformedContours?.Dispose();
            }
        }

        private void SetSelectedBase_Click(object sender, RoutedEventArgs e)
        {
            if (TeachResultGrid.SelectedItem is MatchRow row && row.Item != null)
            {
                BaseRowText.Text = Format(row.Item.Row);
                BaseColumnText.Text = Format(row.Item.Column);
                BaseAngleText.Text = Format(row.Item.Angle);
                SetStatus("已使用选中匹配结果更新基准姿态。");
            }
        }

        private void BrowseImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) == true)
            {
                ImagePathCombo.Text = dialog.FileName;
                TryShowSelectedImage();
            }
        }

        private void ImagePathCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            TryShowSelectedImage();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 输入了原点但未点“应用原点”时，确定前先写入模型，保证记录与模型一致
                ReadOriginTexts(out double row, out double column);
                if (HasModel && (row != _appliedOriginRow || column != _appliedOriginColumn))
                {
                    ApplyOriginToModel();
                }
                ApplyToTool();
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
            DialogResult = false;
            Close();
        }

        private void ApplyToTool()
        {
            ApplyTo(_tool);
        }

        /// <summary>把界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）。</summary>
        private void ApplyTo(IHalconTemplateMatchTool target)
        {
            var matchTarget = (HalconMatchToolBase)target;
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = (ImagePathCombo.Text ?? string.Empty).Trim();
            target.ShapeModelData = _modelData;
            target.FindStartAngle = ParseDouble(FindStartAngleText.Text, "查找起始角");
            target.FindExtentAngle = ParseDouble(FindExtentAngleText.Text, "查找角范围");
            target.MinScore = ParseDouble(MinScoreText.Text, "最小分");
            target.NumMatches = ParseInt(NumMatchesText.Text, "数量");
            target.MaxOverlap = ParseDouble(MaxOverlapText.Text, "重叠");
            target.SubPixel = SubPixelCombo.Text;
            target.Greediness = ParseDouble(GreedinessText.Text, "贪婪度");
            target.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
            string searchRegion = (SearchRegionCombo.Text ?? string.Empty).Trim();
            matchTarget.SearchRegionPath = searchRegion.Length == 0 ? null : searchRegion;
            matchTarget.SortBy = SelectedSortBy;
            matchTarget.RowTolerance = ParseDouble(RowToleranceText.Text, "行容差");
            target.BaseRow = ParseDouble(BaseRowText.Text, "基准 Row");
            target.BaseColumn = ParseDouble(BaseColumnText.Text, "基准 Col");
            target.BaseAngle = ParseDouble(BaseAngleText.Text, "基准角");
            if (target is HalconTemplateMatchToolBase templateTarget)
            {
                templateTarget.ModelSource = _teachSource;
                templateTarget.TeachMetric = _teachMetric;
                templateTarget.TeachXldPath = _teachXldPath;
                templateTarget.TeachXldIndex = _teachXldIndex;
                templateTarget.DxfPath = _teachDxfPath;
                templateTarget.ModelOriginRow = _appliedOriginRow;
                templateTarget.ModelOriginColumn = _appliedOriginColumn;
            }
            if (target is HalconScaledShapeMatchTool scaled)
            {
                scaled.ScaleMin = ParseDouble(ScaleMinText.Text, "最小缩放");
                scaled.ScaleMax = ParseDouble(ScaleMaxText.Text, "最大缩放");
            }
            if (target is HalconLocalDeformableMatchTool deformable)
            {
                deformable.ScaleRowMin = ParseDouble(ScaleRowMinText.Text, "行缩放最小");
                deformable.ScaleRowMax = ParseDouble(ScaleRowMaxText.Text, "行缩放最大");
                deformable.ScaleColumnMin = ParseDouble(ScaleColumnMinText.Text, "列缩放最小");
                deformable.ScaleColumnMax = ParseDouble(ScaleColumnMaxText.Text, "列缩放最大");
                deformable.DeformationSmoothness = DeformationSmoothnessText.Text.Trim();
            }
        }

        private void SaveTeachSettings()
        {
            LastTeachSettings.AngleStart = ParseDouble(TeachAngleStartText.Text, "起始角");
            LastTeachSettings.AngleExtent = ParseDouble(TeachAngleExtentText.Text, "角范围");
            LastTeachSettings.AngleStep = ParseAutoDouble(TeachAngleStepText.Text, "角步长");
            LastTeachSettings.NumLevels = ParseAutoInt(TeachNumLevelsText.Text, "金字塔");
            LastTeachSettings.Optimization = TeachOptimizationCombo.Text;
            LastTeachSettings.Metric = TeachMetricCombo.Text;
            LastTeachSettings.Contrast = ParseContrast(TeachContrastText.Text, "对比度");
            LastTeachSettings.MinContrast = ParseAutoInt(TeachMinContrastText.Text, "最小对比");
        }

        private static List<MatchRow> ToRows(IEnumerable<MatchResultItem> items)
        {
            return items.Select(item => new MatchRow
            {
                Index = item.Index,
                Row = item.Row.ToString("F3", CultureInfo.CurrentCulture),
                Column = item.Column.ToString("F3", CultureInfo.CurrentCulture),
                Angle = item.Angle.ToString("F5", CultureInfo.CurrentCulture),
                Scale = item.Scale.ToString("F4", CultureInfo.CurrentCulture),
                Score = item.Score.ToString("F4", CultureInfo.CurrentCulture),
                Item = item
            }).ToList();
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
                case -1: return "auto";
                case -2: return "auto_contrast";
                case -3: return "auto_contrast_hyst";
                case -4: return "auto_min_size";
                default: return value;
            }
        }

        private static string FormatAuto(double value)
        {
            return value < 0 ? "auto" : Format(value);
        }

        private static string FormatAuto(int value)
        {
            return value < 0 ? "auto" : value.ToString(CultureInfo.CurrentCulture);
        }

        private static string FormatContrast(int value)
        {
            switch (value)
            {
                case -1: return "auto";
                case -2: return "auto_contrast";
                case -3: return "auto_contrast_hyst";
                case -4: return "auto_min_size";
                default: return value.ToString(CultureInfo.CurrentCulture);
            }
        }

        private static int ParseInt(string text, string name)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value))
            {
                return value;
            }
            throw new FormatException($"{name} 不是有效整数。");
        }

        private static int ParseAutoInt(string text, string name)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }
            return ParseInt(value, name);
        }

        private static double ParseAutoDouble(string text, string name)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }
            return ParseDouble(value, name);
        }

        private static int ParseContrast(string text, string name)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }
            if (value.Equals("auto_contrast", StringComparison.OrdinalIgnoreCase))
            {
                return -2;
            }
            if (value.Equals("auto_contrast_hyst", StringComparison.OrdinalIgnoreCase))
            {
                return -3;
            }
            if (value.Equals("auto_min_size", StringComparison.OrdinalIgnoreCase))
            {
                return -4;
            }
            return ParseInt(value, name);
        }

        private static double ParseDouble(string text, string name)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value))
            {
                return value;
            }
            throw new FormatException($"{name} 不是有效数字。");
        }

        private static string Format(double value)
        {
            return value.ToString("G", CultureInfo.CurrentCulture);
        }

        private void SetStatus(string message)
        {
            StatusText.Text = message;
        }

        private void ShowInfo(string message)
        {
            MessageBox.Show(this, message, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "模板匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(message);
        }
    }
}
