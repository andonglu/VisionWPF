using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HalconDotNet;
using VisionFlow.Controls.Roi;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 描述子匹配编辑窗口：框选模板 ROI 创建模型（后台训练）、设置训练与查找参数、在当前图像上测试匹配。
    /// 只操作编辑事务的工作副本；训练好的模型同时写入本机缓存，确认后的工具首次运行可直接加载。
    /// </summary>
    public partial class WpfDescriptorMatchToolEditWindow : Window
    {
        private readonly DescriptorMatchTool _tool;
        private readonly ToolEditContext _context;
        private bool _busy;

        public WpfDescriptorMatchToolEditWindow(DescriptorMatchTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "描述子匹配 - " + tool.ModuleName;
            InitializeFields();
            ShowInputImage();
            // 模板未创建但已预填 ROI（如示例流程）时也显示，打开参考图后直接创建模型即可
            if (_tool.TemplateRow2 > _tool.TemplateRow1 && _tool.TemplateColumn2 > _tool.TemplateColumn1)
            {
                TeachRoiEditor.Rois.Add(new Rectangle1Roi("模板", _tool.TemplateRow1, _tool.TemplateColumn1,
                    _tool.TemplateRow2, _tool.TemplateColumn2));
            }
            RefreshTemplateState();
        }

        private bool HasTemplate
        {
            get { return _tool.TemplateData != null && _tool.TemplateData.Length > 0; }
        }

        private void InitializeFields()
        {
            ModuleNameText.Text = _tool.ModuleName;
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
            ImagePathCombo.Text = _tool.ImagePath ?? "Input.Image";

            foreach (string detector in new[] { "harris_binomial", "harris", "lepetit" })
            {
                DetectorTypeCombo.Items.Add(detector);
            }
            DetectorTypeCombo.Text = _tool.DetectorType;
            DetectorTypeCombo.SelectedItem = _tool.DetectorType;
            DepthText.Value = _tool.Depth;
            NumberFernsText.Value = _tool.NumberFerns;
            PatchSizeText.Value = _tool.PatchSize;
            SeedText.Value = _tool.Seed;
            MinScaleText.Value = _tool.MinScale;
            MaxScaleText.Value = _tool.MaxScale;

            MinScoreText.Value = _tool.MinScore;
            NumMatchesText.Value = _tool.NumMatches;
            foreach (string scoreType in new[] { "num_points", "inlier_ratio" })
            {
                ScoreTypeCombo.Items.Add(scoreType);
            }
            ScoreTypeCombo.SelectedItem = _tool.ScoreType;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
            UseModelFileCacheCheck.IsChecked = _tool.UseModelFileCache;
        }

        private void ShowInputImage()
        {
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                TeachRoiEditor.ShowImage(_context.InputImage);
            }
        }

        private void ShowInputImage_Click(object sender, RoutedEventArgs e)
        {
            ShowInputImage();
        }

        private void RefreshTemplateState()
        {
            TemplateStateText.Text = HasTemplate
                ? $"模板：({_tool.TemplateRow1},{_tool.TemplateColumn1}) - ({_tool.TemplateRow2},{_tool.TemplateColumn2})，"
                    + $"{_tool.TemplateColumn2 - _tool.TemplateColumn1 + 1}×{_tool.TemplateRow2 - _tool.TemplateRow1 + 1} 像素，数据 {_tool.TemplateData.Length / 1024} KB"
                : "模板：未创建";
            TemplateStateText.Foreground = (System.Windows.Media.Brush)FindResource(
                HasTemplate ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        }

        /// <summary>把界面上的训练与查找参数写入工作副本。</summary>
        private void ApplySettings()
        {
            _tool.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            _tool.ImagePath = (ImagePathCombo.Text ?? string.Empty).Trim();
            _tool.DetectorType = (DetectorTypeCombo.SelectedItem as string) ?? DetectorTypeCombo.Text;
            _tool.Depth = (int)Math.Round(DepthText.Value);
            _tool.NumberFerns = (int)Math.Round(NumberFernsText.Value);
            _tool.PatchSize = (int)Math.Round(PatchSizeText.Value);
            _tool.Seed = (int)Math.Round(SeedText.Value);
            _tool.MinScale = MinScaleText.Value;
            _tool.MaxScale = MaxScaleText.Value;
            _tool.MinScore = MinScoreText.Value;
            _tool.NumMatches = (int)Math.Round(NumMatchesText.Value);
            _tool.ScoreType = (ScoreTypeCombo.SelectedItem as string) ?? "num_points";
            _tool.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
            _tool.UseModelFileCache = UseModelFileCacheCheck.IsChecked == true;
        }

        private async void CreateModel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }
            HObject image = TeachRoiEditor.CurrentImage;
            Rectangle1Roi roi = (TeachRoiEditor.Rois.ActiveRoi as Rectangle1Roi)
                ?? TeachRoiEditor.Rois.Items.OfType<Rectangle1Roi>().FirstOrDefault();
            if (image == null || roi == null)
            {
                StatusText.Text = "请先打开参考图像并绘制一个矩形 ROI。";
                return;
            }
            if (MinScaleText.Value >= MaxScaleText.Value)
            {
                StatusText.Text = "最小尺度必须小于最大尺度。";
                return;
            }

            try
            {
                ApplySettings();
                _tool.CreateTemplate(image, roi.Row1, roi.Column1, roi.Row2, roi.Column2);
            }
            catch (Exception ex)
            {
                StatusText.Text = "模板创建失败：" + ex.Message;
                return;
            }
            RefreshTemplateState();

            SetBusy(true, "正在训练描述子模型（数秒到数十秒）……");
            var watch = Stopwatch.StartNew();
            try
            {
                await Task.Run(() => _tool.Prepare());
                StatusText.Text = $"模型已创建，耗时 {watch.Elapsed.TotalSeconds:F1} s。可在当前图像上测试匹配。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "模型训练失败：" + ex.GetBaseException().Message;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async void TestMatch_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }
            HObject image = TeachRoiEditor.CurrentImage;
            if (image == null)
            {
                StatusText.Text = "请先打开或显示一张测试图像。";
                return;
            }
            if (!HasTemplate)
            {
                StatusText.Text = "请先创建模型。";
                return;
            }

            ApplySettings();
            // 在工作副本上运行（复用已缓存的模型），图像固定取当前显示的图像
            string imagePath = _tool.ImagePath;
            bool failWhenNotFound = _tool.FailWhenNotFound;
            HObject testImage = image.Clone();
            SetBusy(true, "正在匹配……");
            try
            {
                var ctx = new FlowContext();
                ctx.SetVariable(Variable.Object("Input", "Image", HalconImage.Owned(testImage), 1));
                _tool.ImagePath = "Input.Image";
                _tool.FailWhenNotFound = false;
                var watch = Stopwatch.StartNew();
                NodeResult result;
                try
                {
                    result = await Task.Run(() => _tool.Run(ctx));
                }
                finally
                {
                    _tool.ImagePath = imagePath;
                    _tool.FailWhenNotFound = failWhenNotFound;
                }
                using (ctx)
                {
                    if (!result.IsSuccess)
                    {
                        StatusText.Text = "匹配失败：" + result.Message;
                        return;
                    }
                    int count = (int)ctx.GetVariable(_tool.ModuleName, "MatchCount").Value;
                    var contour = (HalconXld)ctx.GetVariable(_tool.ModuleName, "ResultContour").Value;
                    TeachRoiEditor.SetOverlay(contour.Object);
                    if (count == 0)
                    {
                        StatusText.Text = $"未找到目标（{watch.ElapsedMilliseconds} ms）。";
                        return;
                    }
                    double score = (double)ctx.GetVariable(_tool.ModuleName, "Score").Value;
                    double angle = RangeClassifyTool.Normalize((double)ctx.GetVariable(_tool.ModuleName, "Angle").Value,
                        ClassifyValueMode.RadiansToDegrees);
                    StatusText.Text = $"找到 {count} 个，最佳得分 {score:F1}，平面内转角 {angle:F1}°，耗时 {watch.ElapsedMilliseconds} ms（含首次加载模型）。";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "匹配失败：" + ex.GetBaseException().Message;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            CreateModelButton.IsEnabled = !busy;
            TestMatchButton.IsEnabled = !busy;
            OkButton.IsEnabled = !busy;
            if (message != null)
            {
                StatusText.Text = message;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (MinScaleText.Value >= MaxScaleText.Value)
            {
                StatusText.Text = "最小尺度必须小于最大尺度。";
                return;
            }
            ApplySettings();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                StatusText.Text = "正在训练或匹配，请稍候再关闭。";
                return;
            }
            DialogResult = false;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                StatusText.Text = "正在训练或匹配，请稍候再关闭。";
                return;
            }
            base.OnClosing(e);
        }
    }
}
