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
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 通用形状匹配编辑窗口（MT-03）：模板列表（添加、删除、改名、上移、下移），每个模板独立示教
    /// （图像 ROI / XLD / DXF）、独立原点与杂乱区域；建模参数只在训练时生效；查找参数对全部模板生效。
    /// 模板在窗口内编辑，确定时整体打包成新的 ModelsData 数组（新引用触发模型重新加载）。
    /// </summary>
    public partial class WpfGenericShapeMatchEditWindow : Window
    {
        private sealed class TemplateEntry
        {
            public string Name;
            public byte[] ModelData;
            public double OriginRow;
            public double OriginColumn;
            public double ClutterArea;

            public bool Trained => ModelData != null && ModelData.Length > 0;

            public override string ToString()
            {
                return Name + (Trained ? string.Empty : "（未训练）") + (ClutterArea > 0 ? "（杂乱区域）" : string.Empty);
            }
        }

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

        private sealed class MatchRow
        {
            public int Index { get; set; }
            public string ModelName { get; set; }
            public string Row { get; set; }
            public string Column { get; set; }
            public string Angle { get; set; }
            public string ScaleRow { get; set; }
            public string ScaleColumn { get; set; }
            public string Score { get; set; }
            public MatchResultItem Item { get; set; }
        }

        private static readonly Choice<MatchModelSource>[] SourceChoices =
        {
            new Choice<MatchModelSource>(MatchModelSource.ImageRoi, "图像 ROI"),
            new Choice<MatchModelSource>(MatchModelSource.Xld, "XLD 轮廓"),
            new Choice<MatchModelSource>(MatchModelSource.Dxf, "DXF 文件")
        };

        private static readonly Choice<ScaleMode>[] ScaleChoices =
        {
            new Choice<ScaleMode>(ScaleMode.None, "不缩放"),
            new Choice<ScaleMode>(ScaleMode.Isotropic, "等比缩放"),
            new Choice<ScaleMode>(ScaleMode.Anisotropic, "各向异性缩放（行列不同）")
        };

        private static readonly Choice<MatchSortBy>[] SortChoices =
        {
            new Choice<MatchSortBy>(MatchSortBy.Score, "得分（算子返回顺序）"),
            new Choice<MatchSortBy>(MatchSortBy.Row, "行（从上到下）"),
            new Choice<MatchSortBy>(MatchSortBy.Column, "列（从左到右）"),
            new Choice<MatchSortBy>(MatchSortBy.RowThenColumn, "先行后列（逐行从左到右）"),
            new Choice<MatchSortBy>(MatchSortBy.ColumnThenRow, "先列后行（逐列从上到下）")
        };

        private readonly HalconGenericShapeMatchTool _tool;
        private readonly ToolEditContext _context;
        private readonly List<TemplateEntry> _templates = new List<TemplateEntry>();
        private readonly List<MatchResultItem> _lastTeachMatches = new List<MatchResultItem>();
        private bool _templatesChanged;
        private string _metricBeforeLock;
        private TemplateEntry _lastSelectedTemplate;
        private bool _refreshingList;

        public WpfGenericShapeMatchEditWindow(HalconGenericShapeMatchTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "通用形状匹配 - " + tool.ModuleName;
            InitializeOptions();
            LoadFromTool();
            TryShowSelectedImage();
            ShowLastRunResult();
            TeachRoiEditor.ImageView.PointPicked += OnOriginPicked;
        }

        private TemplateEntry SelectedTemplate => TemplateList.SelectedItem as TemplateEntry;
        private MatchModelSource SelectedSource => (ModelSourceCombo.SelectedItem as Choice<MatchModelSource>)?.Value ?? MatchModelSource.ImageRoi;
        private ScaleMode SelectedScaleMode => (ScaleModeCombo.SelectedItem as Choice<ScaleMode>)?.Value ?? ScaleMode.None;
        private MatchSortBy SelectedSortBy => (SortByCombo.SelectedItem as Choice<MatchSortBy>)?.Value ?? MatchSortBy.Score;

        private void InitializeOptions()
        {
            ModelSourceCombo.ItemsSource = SourceChoices;
            ScaleModeCombo.ItemsSource = ScaleChoices;
            SortByCombo.ItemsSource = SortChoices;
            MetricCombo.ItemsSource = new[] { "use_polarity", "ignore_global_polarity", "ignore_local_polarity" };
            OptimizationCombo.ItemsSource = new[] { string.Empty, "none", "auto", "point_reduction_low", "point_reduction_medium", "point_reduction_high" };
            SubPixelCombo.ItemsSource = new[] { "none", "interpolation", "least_squares", "least_squares_high", "least_squares_very_high" };

            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ImagePathCombo.Items.Add("Input.Image");
            }
            SearchRegionCombo.Items.Add(string.Empty);
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HalconImage)))
                {
                    if (!ImagePathCombo.Items.Contains(candidate.Path))
                    {
                        ImagePathCombo.Items.Add(candidate.Path);
                    }
                }
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HalconRegion)))
                {
                    SearchRegionCombo.Items.Add(candidate.Path);
                }
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HalconXld)))
                {
                    TeachXldCombo.Items.Add(candidate.Path);
                }
            }
        }

        private void LoadFromTool()
        {
            ModuleNameText.Text = _tool.ModuleName;
            ImagePathCombo.Text = string.IsNullOrWhiteSpace(_tool.ImagePath) && ImagePathCombo.Items.Count > 0
                ? Convert.ToString(ImagePathCombo.Items[0], CultureInfo.CurrentCulture)
                : _tool.ImagePath ?? string.Empty;

            AngleStartText.Text = Format(Math.Round(AngleMath.ToDegrees(_tool.AngleStart), 6));
            AngleEndText.Text = Format(Math.Round(AngleMath.ToDegrees(_tool.AngleEnd), 6));
            ScaleModeCombo.SelectedItem = ScaleChoices.First(c => c.Value == _tool.ScaleMode);
            IsoScaleMinText.Text = Format(_tool.IsoScaleMin);
            IsoScaleMaxText.Text = Format(_tool.IsoScaleMax);
            ScaleRowMinText.Text = Format(_tool.ScaleRowMin);
            ScaleRowMaxText.Text = Format(_tool.ScaleRowMax);
            ScaleColumnMinText.Text = Format(_tool.ScaleColumnMin);
            ScaleColumnMaxText.Text = Format(_tool.ScaleColumnMax);
            NumLevelsText.Text = _tool.NumLevels.ToString(CultureInfo.CurrentCulture);
            MetricCombo.SelectedItem = string.IsNullOrWhiteSpace(_tool.Metric) ? "use_polarity" : _tool.Metric;
            OptimizationCombo.Text = _tool.Optimization ?? string.Empty;
            ContrastLowText.Text = _tool.ContrastLow.ToString(CultureInfo.CurrentCulture);
            ContrastHighText.Text = _tool.ContrastHigh.ToString(CultureInfo.CurrentCulture);
            MinContrastText.Text = _tool.MinContrast.ToString(CultureInfo.CurrentCulture);
            MinSizeText.Text = _tool.MinSize.ToString(CultureInfo.CurrentCulture);

            MinScoreText.Text = Format(_tool.MinScore);
            NumMatchesText.Text = _tool.NumMatches.ToString(CultureInfo.CurrentCulture);
            MaxOverlapText.Text = Format(_tool.MaxOverlap);
            GreedinessText.Text = Format(_tool.Greediness);
            SubPixelCombo.SelectedItem = string.IsNullOrWhiteSpace(_tool.SubPixel) ? "least_squares" : _tool.SubPixel;
            MaxDeformationText.Text = _tool.MaxDeformation.ToString(CultureInfo.CurrentCulture);
            TimeoutMsText.Text = _tool.TimeoutMs.ToString(CultureInfo.CurrentCulture);
            BorderShapeModelsCheck.IsChecked = _tool.BorderShapeModels;
            UseClutterCheck.IsChecked = _tool.UseClutter;
            MaxClutterText.Text = Format(_tool.MaxClutter);
            ClutterContrastText.Text = _tool.ClutterContrast.ToString(CultureInfo.CurrentCulture);
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
            SearchRegionCombo.Text = _tool.SearchRegionPath ?? string.Empty;
            SortByCombo.SelectedItem = SortChoices.First(c => c.Value == _tool.SortBy);
            RowToleranceText.Text = Format(_tool.RowTolerance);
            BaseRowText.Text = Format(_tool.BaseRow);
            BaseColumnText.Text = Format(_tool.BaseColumn);
            BaseAngleText.Text = Format(_tool.BaseAngle);

            ModelSourceCombo.SelectedItem = SourceChoices[0];
            LoadTemplates();
            ScaleModeCombo_SelectionChanged(null, null);
            UseClutterCheck_Changed(null, null);
            SortByCombo_SelectionChanged(null, null);
            ModelSourceCombo_SelectionChanged(null, null);
        }

        /// <summary>解包 ModelsData，读出每个模板的原点与杂乱区域用于回显。</summary>
        private void LoadTemplates()
        {
            _templates.Clear();
            try
            {
                foreach (GenericShapeTemplate template in GenericShapeModelData.Unpack(_tool.ModelsData))
                {
                    var entry = new TemplateEntry { Name = template.Name, ModelData = template.ModelData };
                    HTuple model = GenericShapeTraining.Deserialize(template.ModelData);
                    try
                    {
                        GenericShapeTraining.GetOrigin(model, out entry.OriginRow, out entry.OriginColumn);
                        if (GenericShapeTraining.TryGetClutterRegion(model, out HObject clutter, out double area))
                        {
                            clutter.Dispose();
                            entry.ClutterArea = area;
                        }
                    }
                    finally
                    {
                        HOperatorSet.ClearHandle(model);
                    }
                    _templates.Add(entry);
                }
            }
            catch (Exception ex)
            {
                _templates.Clear();
                SetStatus("模型数据无法读取，请重新示教：" + ex.Message);
            }
            RefreshTemplateList(_templates.FirstOrDefault());
        }

        private void RefreshTemplateList(TemplateEntry select)
        {
            _refreshingList = true;
            try
            {
                TemplateList.ItemsSource = null;
                TemplateList.ItemsSource = _templates.ToList();
                TemplateList.SelectedItem = select;
            }
            finally
            {
                _refreshingList = false;
            }
            OnTemplateSelected(select);
        }

        /// <summary>选中的模板变化时清除上一模板的示教结果、原点标记与提示；同一模板（列表刷新）时保留。</summary>
        private void OnTemplateSelected(TemplateEntry selected)
        {
            if (!ReferenceEquals(selected, _lastSelectedTemplate))
            {
                _lastTeachMatches.Clear();
                TeachRoiEditor.ImageView.SetMarker(null, null);
                OriginBaseHintText.Visibility = Visibility.Collapsed;
                _lastSelectedTemplate = selected;
            }
            UpdateTemplateState();
        }

        private void UpdateTemplateState()
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null)
            {
                TemplateStateText.Text = _templates.Count == 0 ? "尚无模板：输入名称后点“添加”，再训练。" : "请选中一个模板。";
                ClutterStateText.Text = string.Empty;
                return;
            }
            TemplateNameText.Text = entry.Name;
            ModelOriginRowText.Text = Format(entry.OriginRow);
            ModelOriginColumnText.Text = Format(entry.OriginColumn);
            TemplateStateText.Text = $"当前模板：{entry.Name}（序号 {_templates.IndexOf(entry)}），"
                + (entry.Trained ? $"已训练（{entry.ModelData.Length} bytes），原点 ({Format(entry.OriginRow)}, {Format(entry.OriginColumn)})" : "未训练");
            ClutterStateText.Text = entry.ClutterArea > 0 ? $"杂乱区域：已设置（面积 {entry.ClutterArea:F0}）" : "杂乱区域：未设置";
        }

        private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 刷新列表（改名、训练、应用原点后重建 ItemsSource）由 RefreshTemplateList 统一处理
            if (_refreshingList)
            {
                return;
            }
            OnTemplateSelected(SelectedTemplate);
        }

        // ======================= 模板列表 =======================

        private string ValidateName(string name, TemplateEntry except)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                throw new InvalidOperationException("模板名称不能为空");
            }
            if (name.Contains(','))
            {
                throw new InvalidOperationException("模板名称不能包含逗号");
            }
            if (_templates.Any(t => !ReferenceEquals(t, except) && t.Name == name))
            {
                throw new InvalidOperationException($"模板名称“{name}”已存在");
            }
            return name;
        }

        private void AddTemplate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string name = (TemplateNameText.Text ?? string.Empty).Trim();
                if (name.Length == 0 || _templates.Any(t => t.Name == name))
                {
                    int n = _templates.Count + 1;
                    while (_templates.Any(t => t.Name == "模板" + n))
                    {
                        n++;
                    }
                    name = "模板" + n;
                }
                var entry = new TemplateEntry { Name = ValidateName(name, null) };
                _templates.Add(entry);
                _templatesChanged = true;
                RefreshTemplateList(entry);
                SetStatus($"已添加模板“{entry.Name}”，请选择模型来源并训练。");
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void RenameTemplate_Click(object sender, RoutedEventArgs e)
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null)
            {
                ShowInfo("请先选中一个模板。");
                return;
            }
            try
            {
                string old = entry.Name;
                entry.Name = ValidateName(TemplateNameText.Text, entry);
                _templatesChanged = true;
                RefreshTemplateList(entry);
                SetStatus($"模板“{old}”已改名为“{entry.Name}”（输出 ModelNames / BestModelName 随之变化）。");
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null)
            {
                ShowInfo("请先选中一个模板。");
                return;
            }
            int index = _templates.IndexOf(entry);
            _templates.Remove(entry);
            _templatesChanged = true;
            TemplateOrderHintText.Visibility = index < _templates.Count ? Visibility.Visible : TemplateOrderHintText.Visibility;
            RefreshTemplateList(_templates.Count == 0 ? null : _templates[Math.Min(index, _templates.Count - 1)]);
            SetStatus($"已删除模板“{entry.Name}”。");
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            Move(-1);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            Move(1);
        }

        private void Move(int delta)
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null)
            {
                return;
            }
            int index = _templates.IndexOf(entry);
            int target = index + delta;
            if (target < 0 || target >= _templates.Count)
            {
                return;
            }
            _templates.RemoveAt(index);
            _templates.Insert(target, entry);
            _templatesChanged = true;
            TemplateOrderHintText.Visibility = Visibility.Visible;
            RefreshTemplateList(entry);
            SetStatus($"模板“{entry.Name}”已移到序号 {target}。");
        }

        // ======================= 模型来源与建模参数 =======================

        private void ModelSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MatchModelSource source = SelectedSource;
            XldSourcePanel.Visibility = source == MatchModelSource.Xld ? Visibility.Visible : Visibility.Collapsed;
            DxfSourcePanel.Visibility = source == MatchModelSource.Dxf ? Visibility.Visible : Visibility.Collapsed;
            ContourIndexPanel.Visibility = source == MatchModelSource.ImageRoi ? Visibility.Collapsed : Visibility.Visible;
            XldMetricHintText.Visibility = source == MatchModelSource.ImageRoi ? Visibility.Collapsed : Visibility.Visible;
            bool xld = source != MatchModelSource.ImageRoi;
            if (xld && MetricCombo.IsEnabled)
            {
                _metricBeforeLock = MetricCombo.SelectedItem as string;
                MetricCombo.SelectedItem = MatchModelBuilder.XldMetric;
                MetricCombo.IsEnabled = false;
            }
            else if (!xld && !MetricCombo.IsEnabled)
            {
                MetricCombo.IsEnabled = true;
                MetricCombo.SelectedItem = _metricBeforeLock ?? "use_polarity";
            }
            ContrastLowText.IsEnabled = !xld;
            ContrastHighText.IsEnabled = !xld;
            MinSizeText.IsEnabled = !xld;
        }

        private void ScaleModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ScaleMode mode = SelectedScaleMode;
            IsoScalePanel.Visibility = HalconGenericShapeMatchTool.IsScaleParameterVisible(mode, nameof(HalconGenericShapeMatchTool.IsoScaleMin))
                ? Visibility.Visible : Visibility.Collapsed;
            AnisoScalePanel.Visibility = HalconGenericShapeMatchTool.IsScaleParameterVisible(mode, nameof(HalconGenericShapeMatchTool.ScaleRowMin))
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UseClutterCheck_Changed(object sender, RoutedEventArgs e)
        {
            ClutterParamsPanel.Visibility = UseClutterCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SortByCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Visibility visibility = SelectedSortBy == MatchSortBy.RowThenColumn ? Visibility.Visible : Visibility.Collapsed;
            RowToleranceLabel.Visibility = visibility;
            RowToleranceText.Visibility = visibility;
        }

        private void BrowseDxf_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "DXF 文件|*.dxf|所有文件|*.*" };
            if (dialog.ShowDialog(this) == true)
            {
                DxfPathText.Text = dialog.FileName;
            }
        }

        /// <summary>训练当前模板：按模型来源取模板（图像 ROI / XLD / DXF），按建模参数训练，写入原点；会清除该模板原有的杂乱区域。</summary>
        private void TrainTemplate_Click(object sender, RoutedEventArgs e)
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null)
            {
                ShowInfo("请先在模板列表中添加并选中一个模板。");
                return;
            }
            MatchModelSource source = SelectedSource;
            HObject owned = null;
            HObject template = null;
            try
            {
                var settings = new HalconGenericShapeMatchTool("训练");
                ApplyTrainingTo(settings);
                ReadOriginTexts(out double originRow, out double originColumn);
                string description;
                if (source == MatchModelSource.ImageRoi)
                {
                    HObject image = TeachRoiEditor.CurrentImage;
                    if (image == null)
                    {
                        ShowInfo("请先打开或显示一张示教图像。");
                        return;
                    }
                    using HRegion region = TeachRoiEditor.BuildRegion();
                    HOperatorSet.AreaCenter(region, out HTuple area, out _, out _);
                    if (area.Length == 0 || area.D <= 0)
                    {
                        ShowInfo("请先绘制模板区域 ROI。多个包含 ROI 会合并，排除 ROI 会扣除。");
                        return;
                    }
                    HOperatorSet.ReduceDomain(image, region, out template);
                    description = "图像 ROI";
                }
                else
                {
                    int index = ParseInt(TeachXldIndexText.Text, "轮廓序号");
                    HObject contours;
                    if (source == MatchModelSource.Xld)
                    {
                        contours = ResolveXld((TeachXldCombo.Text ?? string.Empty).Trim());
                        description = "XLD 轮廓 " + TeachXldCombo.Text.Trim();
                    }
                    else
                    {
                        owned = MatchModelBuilder.ReadDxf((DxfPathText.Text ?? string.Empty).Trim());
                        contours = owned;
                        description = "DXF 文件";
                    }
                    template = MatchModelBuilder.SelectContours(contours, index);
                }

                HTuple model = GenericShapeTraining.Train(settings, template, source != MatchModelSource.ImageRoi);
                try
                {
                    if (originRow != 0 || originColumn != 0)
                    {
                        GenericShapeTraining.SetOrigin(model, originRow, originColumn);
                    }
                    entry.ModelData = GenericShapeTraining.Serialize(model);
                }
                finally
                {
                    HOperatorSet.ClearHandle(model);
                }
                entry.OriginRow = originRow;
                entry.OriginColumn = originColumn;
                entry.ClutterArea = 0;
                _templatesChanged = true;
                OriginBaseHintText.Visibility = Visibility.Collapsed;
                RefreshTemplateList(entry);
                int found = RunTeachTest(entry);
                if (_templates.IndexOf(entry) == 0 && _lastTeachMatches.Count > 0)
                {
                    // 第一个模板训练后用其示教匹配位置作为跟随基准（全部模板共用）
                    BaseRowText.Text = Format(_lastTeachMatches[0].Row);
                    BaseColumnText.Text = Format(_lastTeachMatches[0].Column);
                    BaseAngleText.Text = Format(_lastTeachMatches[0].Angle);
                }
                SetStatus($"模板“{entry.Name}”训练完成（{description}，{entry.ModelData.Length} bytes）：示教图像中匹配 {found} 个。");
            }
            catch (Exception ex)
            {
                ShowError("训练模板失败：" + ex.Message);
            }
            finally
            {
                template?.Dispose();
                owned?.Dispose();
            }
        }

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
            return xld ?? throw new InvalidOperationException("引用不是 XLD 轮廓：" + path);
        }

        /// <summary>
        /// 用当前模板单独在示教图像上查找（经工具自身的运行流程，查找参数取界面值、数量不限），
        /// 结果显示在示教页并作为原点点选的参考。返回找到的个数。
        /// </summary>
        private int RunTeachTest(TemplateEntry entry)
        {
            _lastTeachMatches.Clear();
            HObject image = TeachRoiEditor.CurrentImage;
            if (image == null || !entry.Trained)
            {
                TeachResultGrid.ItemsSource = null;
                return 0;
            }
            var probe = new HalconGenericShapeMatchTool("示教测试") { ImagePath = "Input.Image" };
            ApplySearchTo(probe);
            probe.NumMatches = 0;
            probe.FailWhenNotFound = false;
            probe.SearchRegionPath = null;
            probe.SortBy = MatchSortBy.Score;
            probe.ModelsData = GenericShapeModelData.Pack(new[] { new GenericShapeTemplate(entry.Name, entry.ModelData) });
            try
            {
                using var ctx = new FlowContext();
                ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
                NodeResult result = probe.Run(ctx);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(result.Message);
                }
                List<MatchResultItem> items = ((IEnumerable<MatchResultItem>)ctx.GetVariable(probe.ModuleName, "Items").Value).ToList();
                _lastTeachMatches.AddRange(items.Select(i => new MatchResultItem
                {
                    Index = i.Index, Row = i.Row, Column = i.Column, Angle = i.Angle, Scale = i.Scale,
                    ScaleRow = i.ScaleRow, ScaleColumn = i.ScaleColumn, Score = i.Score, ModelIndex = i.ModelIndex, ModelName = i.ModelName
                }));
                if (ctx.TryGetVariable(probe.ModuleName, "ResultContour", out Variable contour) && contour.Value is HalconXld xld)
                {
                    TeachRoiEditor.SetOverlay(xld.Object);
                }
                TeachResultGrid.ItemsSource = ToRows(_lastTeachMatches);
                return _lastTeachMatches.Count;
            }
            finally
            {
                FlowResources.Release(probe);
            }
        }

        // ======================= 原点与杂乱区域 =======================

        private void ReadOriginTexts(out double row, out double column)
        {
            row = ParseDouble(ModelOriginRowText.Text, "原点行偏移");
            column = ParseDouble(ModelOriginColumnText.Text, "原点列偏移");
        }

        private void PickOrigin_Click(object sender, RoutedEventArgs e)
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null || !entry.Trained || TeachRoiEditor.CurrentImage == null)
            {
                ShowInfo("请先训练当前模板并显示示教图像。");
                return;
            }
            if (_lastTeachMatches.Count == 0)
            {
                RunTeachTest(entry);
            }
            if (_lastTeachMatches.Count == 0)
            {
                ShowInfo("示教图像中没有找到当前模板，无法确定模板参考点。");
                return;
            }
            TeachRoiEditor.ImageView.BeginPickPoint();
            SetStatus("请在示教图像上点选模型原点（按住可拖动，松开结束），然后点“应用原点”。");
        }

        private void OnOriginPicked(object sender, ImagePointEventArgs e)
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null || _lastTeachMatches.Count == 0)
            {
                return;
            }
            MatchResultItem match = _lastTeachMatches[0];
            MatchModelBuilder.OriginFromPickedPoint(e.Row, e.Column, match.Row, match.Column, entry.OriginRow, entry.OriginColumn,
                out double row, out double column);
            ModelOriginRowText.Text = Format(Math.Round(row, 2));
            ModelOriginColumnText.Text = Format(Math.Round(column, 2));
            TeachRoiEditor.ImageView.SetMarker(e.Row, e.Column);
            SetStatus($"已点选原点：图像 ({e.Row:F2}, {e.Column:F2})，相对参考点偏移 ({row:F2}, {column:F2})。点“应用原点”写入当前模板。");
        }

        private void ApplyOrigin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyOriginToSelected();
            }
            catch (Exception ex)
            {
                ShowError("应用原点失败：" + ex.Message);
            }
        }

        private void ApplyOriginToSelected()
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null || !entry.Trained)
            {
                ShowInfo("请先训练当前模板。");
                return;
            }
            ReadOriginTexts(out double row, out double column);
            bool changed = row != entry.OriginRow || column != entry.OriginColumn;
            HTuple model = GenericShapeTraining.Deserialize(entry.ModelData);
            try
            {
                GenericShapeTraining.SetOrigin(model, row, column);
                entry.ModelData = GenericShapeTraining.Serialize(model);
            }
            finally
            {
                HOperatorSet.ClearHandle(model);
            }
            entry.OriginRow = row;
            entry.OriginColumn = column;
            _templatesChanged = true;
            if (changed)
            {
                OriginBaseHintText.Visibility = Visibility.Visible;
            }
            RefreshTemplateList(entry);
            RunTeachTest(entry);
            if (_lastTeachMatches.Count > 0)
            {
                TeachRoiEditor.ImageView.SetMarker(_lastTeachMatches[0].Row, _lastTeachMatches[0].Column);
            }
            SetStatus($"模板“{entry.Name}”的原点已更新为 ({Format(row)}, {Format(column)})；匹配输出的 Row / Column 即为该点。请重新确定跟随基准（基准 Row / Col / 角）。");
        }

        private void SetClutter_Click(object sender, RoutedEventArgs e)
        {
            TemplateEntry entry = SelectedTemplate;
            if (entry == null || !entry.Trained)
            {
                ShowInfo("请先训练当前模板。");
                return;
            }
            try
            {
                using HRegion region = TeachRoiEditor.BuildRegion();
                HOperatorSet.AreaCenter(region, out HTuple area, out _, out _);
                if (area.Length == 0 || area.D <= 0)
                {
                    ShowInfo("请先用 ROI 画出杂乱区域。");
                    return;
                }
                HTuple model = GenericShapeTraining.Deserialize(entry.ModelData);
                try
                {
                    GenericShapeTraining.SetClutterRegion(model, region);
                    entry.ModelData = GenericShapeTraining.Serialize(model);
                }
                finally
                {
                    HOperatorSet.ClearHandle(model);
                }
                entry.ClutterArea = area.D;
                _templatesChanged = true;
                TeachRoiEditor.SetOverlay(region);
                RefreshTemplateList(entry);
                SetStatus($"模板“{entry.Name}”的杂乱区域已设置（面积 {area.D:F0}）。" + (UseClutterCheck.IsChecked == true ? string.Empty : "在“运行参数”中启用杂乱判定后生效。"));
            }
            catch (Exception ex)
            {
                ShowError("设置杂乱区域失败：" + ex.Message);
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

        // ======================= 运行与提交 =======================

        private void TryShowSelectedImage()
        {
            string path = (ImagePathCombo.Text ?? string.Empty).Trim();
            HObject image = null;
            if (path.Equals("Input.Image", StringComparison.OrdinalIgnoreCase))
            {
                image = _context.InputImage;
            }
            else if (_context.LastRunContext != null && path.Length > 0)
            {
                try
                {
                    object value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
                    image = value is HalconImage halconImage ? halconImage.Object : value as HObject;
                }
                catch (Exception)
                {
                    image = null;
                }
            }
            if (image != null && image.IsInitialized())
            {
                TeachRoiEditor.ShowImage(image);
                RunImageView.ShowImage(image);
            }
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(TryShowSelectedImage));
        }

        private void ShowLastRunResult()
        {
            FlowContext last = _context.LastRunContext;
            if (last == null)
            {
                return;
            }
            if (last.TryGetVariable(_tool.ModuleName, "Image", out Variable imageVar) && imageVar.Value is HalconImage image)
            {
                RunImageView.ShowImage(image.Object);
            }
            if (last.TryGetVariable(_tool.ModuleName, "ResultContour", out Variable contourVar) && contourVar.Value is HalconXld contour)
            {
                RunImageView.SetOverlay(contour.Object);
            }
            if (last.TryGetVariable(_tool.ModuleName, "Items", out Variable itemsVar) && itemsVar.Value is IEnumerable<MatchResultItem> items)
            {
                RunResultGrid.ItemsSource = ToRows(items);
            }
        }

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            ToolTestRun run = null;
            try
            {
                run = ToolTestRun.Run(_tool, copy => ApplyTo((HalconGenericShapeMatchTool)copy), _context.LastRunContext, _context.InputImage);
                FlowContext ctx = run.Context;
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
                string counts = ctx.TryGetVariable(module, "CountsPerModel", out Variable countsVar) && countsVar.Value is int[] perModel
                    ? string.Join(" / ", perModel) : string.Empty;
                double bestScore = ctx.TryGetVariable(module, "Score", out Variable scoreVar) ? Convert.ToDouble(scoreVar.Value, CultureInfo.CurrentCulture) : double.NaN;
                SetStatus(run.Result.IsSuccess
                    ? $"运行测试完成：{RunResultGrid.Items.Count} 个结果（每模板 {counts}），最佳得分 {bestScore:F4}。"
                    : "运行测试：" + run.Result.Message);
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

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TemplateEntry entry = SelectedTemplate;
                if (entry != null && entry.Trained)
                {
                    ReadOriginTexts(out double row, out double column);
                    if (row != entry.OriginRow || column != entry.OriginColumn)
                    {
                        ApplyOriginToSelected();
                    }
                }
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
            DialogResult = false;
            Close();
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）；模板有改动时整体打包成新数组。</summary>
        private void ApplyTo(HalconGenericShapeMatchTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = (ImagePathCombo.Text ?? string.Empty).Trim();
            ApplyTrainingTo(target);
            ApplySearchTo(target);
            target.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
            string searchRegion = (SearchRegionCombo.Text ?? string.Empty).Trim();
            target.SearchRegionPath = searchRegion.Length == 0 ? null : searchRegion;
            target.SortBy = SelectedSortBy;
            target.RowTolerance = ParseDouble(RowToleranceText.Text, "行容差");
            target.BaseRow = ParseDouble(BaseRowText.Text, "基准 Row");
            target.BaseColumn = ParseDouble(BaseColumnText.Text, "基准 Col");
            target.BaseAngle = ParseDouble(BaseAngleText.Text, "基准角");
            if (_templatesChanged)
            {
                target.ModelsData = _templates.Count == 0
                    ? null
                    : GenericShapeModelData.Pack(_templates.Select(t => new GenericShapeTemplate(t.Name, t.ModelData)));
                target.TemplateNamesCsv = string.Join(",", _templates.Select(t => t.Name));
            }
        }

        private void ApplyTrainingTo(HalconGenericShapeMatchTool target)
        {
            target.AngleStart = AngleMath.ToRadians(ParseDouble(AngleStartText.Text, "起始角"));
            target.AngleEnd = AngleMath.ToRadians(ParseDouble(AngleEndText.Text, "终止角"));
            target.ScaleMode = SelectedScaleMode;
            target.IsoScaleMin = ParseDouble(IsoScaleMinText.Text, "等比缩放最小");
            target.IsoScaleMax = ParseDouble(IsoScaleMaxText.Text, "等比缩放最大");
            target.ScaleRowMin = ParseDouble(ScaleRowMinText.Text, "行缩放最小");
            target.ScaleRowMax = ParseDouble(ScaleRowMaxText.Text, "行缩放最大");
            target.ScaleColumnMin = ParseDouble(ScaleColumnMinText.Text, "列缩放最小");
            target.ScaleColumnMax = ParseDouble(ScaleColumnMaxText.Text, "列缩放最大");
            target.NumLevels = ParseInt(NumLevelsText.Text, "金字塔");
            // XLD / DXF 建模时度量下拉被锁定为 ignore_local_polarity，工具上保存的仍是图像建模的度量
            target.Metric = MetricCombo.IsEnabled ? MetricCombo.SelectedItem as string ?? "use_polarity" : _metricBeforeLock ?? "use_polarity";
            target.Optimization = string.IsNullOrWhiteSpace(OptimizationCombo.Text) ? null : OptimizationCombo.Text.Trim();
            target.ContrastLow = ParseInt(ContrastLowText.Text, "对比度下限");
            target.ContrastHigh = ParseInt(ContrastHighText.Text, "对比度上限");
            target.MinContrast = ParseInt(MinContrastText.Text, "最小对比度");
            target.MinSize = ParseInt(MinSizeText.Text, "最小尺寸");
            if (target.ScaleMode == ScaleMode.Isotropic && !(target.IsoScaleMin > 0 && target.IsoScaleMax >= target.IsoScaleMin))
            {
                throw new InvalidOperationException("等比缩放范围无效：最小值须大于 0 且不大于最大值");
            }
            if (target.ScaleMode == ScaleMode.Anisotropic && !(target.ScaleRowMin > 0 && target.ScaleRowMax >= target.ScaleRowMin
                && target.ScaleColumnMin > 0 && target.ScaleColumnMax >= target.ScaleColumnMin))
            {
                throw new InvalidOperationException("各向异性缩放范围无效：最小值须大于 0 且不大于最大值");
            }
        }

        private void ApplySearchTo(HalconGenericShapeMatchTool target)
        {
            target.AngleStart = AngleMath.ToRadians(ParseDouble(AngleStartText.Text, "起始角"));
            target.AngleEnd = AngleMath.ToRadians(ParseDouble(AngleEndText.Text, "终止角"));
            target.MinScore = ParseDouble(MinScoreText.Text, "最小分");
            target.NumMatches = ParseInt(NumMatchesText.Text, "数量");
            target.MaxOverlap = ParseDouble(MaxOverlapText.Text, "重叠");
            target.Greediness = ParseDouble(GreedinessText.Text, "贪婪度");
            target.SubPixel = SubPixelCombo.SelectedItem as string ?? "least_squares";
            target.MaxDeformation = ParseInt(MaxDeformationText.Text, "最大变形");
            target.TimeoutMs = ParseInt(TimeoutMsText.Text, "超时");
            target.BorderShapeModels = BorderShapeModelsCheck.IsChecked == true;
            target.UseClutter = UseClutterCheck.IsChecked == true;
            target.MaxClutter = ParseDouble(MaxClutterText.Text, "最大杂乱比例");
            target.ClutterContrast = ParseInt(ClutterContrastText.Text, "杂乱对比度");
        }

        private static List<MatchRow> ToRows(IEnumerable<MatchResultItem> items)
        {
            return items.Select(item => new MatchRow
            {
                Index = item.Index,
                ModelName = item.ModelName,
                Row = item.Row.ToString("F3", CultureInfo.CurrentCulture),
                Column = item.Column.ToString("F3", CultureInfo.CurrentCulture),
                Angle = item.Angle.ToString("F5", CultureInfo.CurrentCulture),
                ScaleRow = item.ScaleRow.ToString("F4", CultureInfo.CurrentCulture),
                ScaleColumn = item.ScaleColumn.ToString("F4", CultureInfo.CurrentCulture),
                Score = item.Score.ToString("F4", CultureInfo.CurrentCulture),
                Item = item
            }).ToList();
        }

        private static int ParseInt(string text, string name)
        {
            if (int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value))
            {
                return value;
            }
            throw new FormatException($"{name} 不是有效整数。");
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
            MessageBox.Show(this, message, "通用形状匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(message);
        }
    }
}
