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
        }

        private bool HasModel => _modelData != null && _modelData.Length > 0;
        private bool IsGrayMatch => _tool is HalconGrayMatchTool;
        private bool IsScaledShapeMatch => _tool is HalconScaledShapeMatchTool;
        private bool IsDeformableMatch => _tool is HalconLocalDeformableMatchTool;

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
        }

        private void RefreshModelState()
        {
            ModelStateText.Text = HasModel ? $"模型：已创建 ({_modelData.Length} bytes)" : "模型：未创建";
            ModelStateText.Foreground = (System.Windows.Media.Brush)FindResource(
                HasModel ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
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

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
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
            if (TeachRoiEditor.ImageView.ImageObject == null)
            {
                ShowInfo("请先打开或显示一张图像。");
                return;
            }

            try
            {
                SaveTeachSettings();
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
                        _modelData = SerializeModel(modelId);
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
                            expectedRow: teachRow,
                            expectedColumn: teachColumn);
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

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            if (RunImageView.ImageObject == null)
            {
                TryShowSelectedImage();
            }
            if (RunImageView.ImageObject == null)
            {
                ShowInfo("请先选择运行图像。");
                return;
            }

            try
            {
                ExecuteMatch(RunImageView.ImageObject, RunImageView, RunResultGrid,
                    ParseDouble(FindStartAngleText.Text, "起始角"),
                    ParseDouble(FindExtentAngleText.Text, "角范围"),
                    ParseDouble(MinScoreText.Text, "最小分"),
                    ParseInt(NumMatchesText.Text, "数量"),
                    ParseDouble(MaxOverlapText.Text, "重叠"),
                    SubPixelCombo.Text,
                    _tool.NumLevelsFind,
                    ParseDouble(GreedinessText.Text, "贪婪度"),
                    updateTeachMatches: false);
                SetStatus("运行测试完成。");
            }
            catch (Exception ex)
            {
                ShowError("运行测试失败：" + ex.Message);
            }
        }

        private void ExecuteMatch(HObject image, HalconImageView view, DataGrid grid,
            double startAngle, double extentAngle, double minScore, int numMatches,
            double maxOverlap, string subPixel, int numLevels, double greediness, bool updateTeachMatches)
        {
            if (!HasModel)
            {
                ShowInfo("请先创建模板。");
                return;
            }

            HTuple modelId = DeserializeModel();
            try
            {
                ExecuteMatch(modelId, image, view, grid, startAngle, extentAngle, minScore, numMatches,
                    maxOverlap, subPixel, numLevels, greediness, updateTeachMatches);
            }
            finally
            {
                ClearModel(modelId);
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
            _tool.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            _tool.ImagePath = (ImagePathCombo.Text ?? string.Empty).Trim();
            _tool.ShapeModelData = _modelData;
            _tool.FindStartAngle = ParseDouble(FindStartAngleText.Text, "查找起始角");
            _tool.FindExtentAngle = ParseDouble(FindExtentAngleText.Text, "查找角范围");
            _tool.MinScore = ParseDouble(MinScoreText.Text, "最小分");
            _tool.NumMatches = ParseInt(NumMatchesText.Text, "数量");
            _tool.MaxOverlap = ParseDouble(MaxOverlapText.Text, "重叠");
            _tool.SubPixel = SubPixelCombo.Text;
            _tool.Greediness = ParseDouble(GreedinessText.Text, "贪婪度");
            _tool.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
            _tool.BaseRow = ParseDouble(BaseRowText.Text, "基准 Row");
            _tool.BaseColumn = ParseDouble(BaseColumnText.Text, "基准 Col");
            _tool.BaseAngle = ParseDouble(BaseAngleText.Text, "基准角");
            if (_tool is HalconScaledShapeMatchTool scaled)
            {
                scaled.ScaleMin = ParseDouble(ScaleMinText.Text, "最小缩放");
                scaled.ScaleMax = ParseDouble(ScaleMaxText.Text, "最大缩放");
            }
            if (_tool is HalconLocalDeformableMatchTool deformable)
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
