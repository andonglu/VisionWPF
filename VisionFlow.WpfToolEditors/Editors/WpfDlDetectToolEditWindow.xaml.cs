using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 深度学习检测编辑窗口（DL-02）：右侧为输入、模型与检测参数，
    /// 左侧显示当前 / 加载的图像与检测框叠加、结果列表（类别 / 分数 / 框位置）。
    /// 执行测试经 ToolTestRun 在一次性副本上进行，只有“确定”才把界面参数写回工具。
    /// </summary>
    public partial class WpfDlDetectToolEditWindow : Window
    {
        private sealed class ResultRow
        {
            public int Index { get; set; }
            public string ClassName { get; set; }
            public string Score { get; set; }
            public string Row1 { get; set; }
            public string Col1 { get; set; }
            public string Row2 { get; set; }
            public string Col2 { get; set; }
        }

        private readonly DlDetectTool _tool;
        private readonly ToolEditContext _context;
        private HObject _overlay;
        private HObject _loadedImage;

        public WpfDlDetectToolEditWindow(DlDetectTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "深度学习检测 - " + tool.ModuleName;
            InitializeOptions();
            LoadFromTool();
            ShowSourceImage();
            Closed += (s, e) =>
            {
                _overlay?.Dispose();
                _overlay = null;
                _loadedImage?.Dispose();
                _loadedImage = null;
            };
        }

        // ======================= 参数区 =======================

        private void InitializeOptions()
        {
            foreach (string name in Enum.GetNames(typeof(DeepLearningDevicePreference)))
            {
                DeviceCombo.Items.Add(name);
            }
            foreach (string name in Enum.GetNames(typeof(ModelCacheMode)))
            {
                ModelCacheModeCombo.Items.Add(name);
            }

            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ImagePathCombo.Items.Add("Input.Image");
            }
            RegionPathCombo.Items.Add(string.Empty);
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
                    RegionPathCombo.Items.Add(candidate.Path);
                }
            }
        }

        private void LoadFromTool()
        {
            ModuleNameText.Text = _tool.ModuleName;
            ImagePathCombo.Text = _tool.ImagePath ?? string.Empty;
            RegionPathCombo.Text = _tool.RegionPath ?? string.Empty;
            ModelFilePathText.Text = _tool.ModelFilePath ?? string.Empty;
            DeviceCombo.SelectedItem = _tool.Device.ToString();
            BatchSizeText.Text = _tool.BatchSize.ToString(CultureInfo.InvariantCulture);
            ModelCacheModeCombo.SelectedItem = _tool.ModelCacheMode.ToString();
            MinScoreText.Text = _tool.MinScore.ToString(CultureInfo.InvariantCulture);
            MaxDetectionsText.Text = _tool.MaxDetections.ToString(CultureInfo.InvariantCulture);
            OptimizeForInferenceCheck.IsChecked = _tool.OptimizeForInference;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）。</summary>
        private void ApplyTo(DlDetectTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text) ?? "Input.Image";
            target.RegionPath = NullIfEmpty(RegionPathCombo.Text);
            target.ModelFilePath = NullIfEmpty(ModelFilePathText.Text);
            target.Device = ParseEnum<DeepLearningDevicePreference>(DeviceCombo.SelectedItem as string, nameof(DlDetectTool.Device));
            target.BatchSize = ParseInt(BatchSizeText, nameof(DlDetectTool.BatchSize));
            target.ModelCacheMode = ParseEnum<ModelCacheMode>(ModelCacheModeCombo.SelectedItem as string, nameof(DlDetectTool.ModelCacheMode));
            target.MinScore = ParseDouble(MinScoreText, nameof(DlDetectTool.MinScore));
            target.MaxDetections = ParseInt(MaxDetectionsText, nameof(DlDetectTool.MaxDetections));
            target.OptimizeForInference = OptimizeForInferenceCheck.IsChecked == true;
            target.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
        }

        // ======================= 图像 =======================

        private HObject ResolveSourceImage()
        {
            if (_loadedImage != null && _loadedImage.IsInitialized())
            {
                return _loadedImage;
            }
            string path = (ImagePathCombo.Text ?? string.Empty).Trim();
            if (path.Equals("Input.Image", StringComparison.OrdinalIgnoreCase))
            {
                return _context.InputImage;
            }
            if (_context.LastRunContext == null || path.Length == 0)
            {
                return null;
            }
            try
            {
                return (VariableReference.Parse(path).Resolve(_context.LastRunContext) as HalconImage)?.Object;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void ShowSourceImage()
        {
            HObject image = ResolveSourceImage();
            if (image == null || !image.IsInitialized())
            {
                PreviewImageView.ClearImage();
                ImageInfoText.Text = "尚无图像：点击“取当前图像”（主窗口当前图像 / 已选输入引用）或“加载图像...”选择图片文件。";
                return;
            }
            PreviewImageView.ShowImage(image);
            if (_overlay != null && _overlay.IsInitialized())
            {
                PreviewImageView.SetOverlay(_overlay);
            }
            ImageInfoText.Text = _loadedImage != null
                ? "当前为窗口加载的图像（仅用于编辑器内测试，不影响流程）。"
                : "当前为流程输入图像。";
        }

        private void UseCurrentImage_Click(object sender, RoutedEventArgs e)
        {
            _loadedImage?.Dispose();
            _loadedImage = null;
            ShowSourceImage();
        }

        private void LoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择测试图像",
                Filter = "图像文件|*.png;*.bmp;*.tif;*.tiff;*.jpg;*.jpeg|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            try
            {
                HOperatorSet.ReadImage(out HObject image, dialog.FileName);
                _loadedImage?.Dispose();
                _loadedImage = image;
                ShowSourceImage();
                SetStatus("已加载图像：" + dialog.FileName);
            }
            catch (Exception ex) when (ex is HalconException || ex is InvalidOperationException || ex is ArgumentException)
            {
                ShowError("加载图像失败：" + ex.Message);
            }
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ShowSourceImage));
        }

        // ======================= 模型浏览与预览、确定与取消 =======================

        private void BrowseModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 .hdl 检测模型",
                Filter = "HALCON 深度学习模型|*.hdl|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) == true)
            {
                ModelFilePathText.Text = dialog.FileName;
            }
        }

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            RunPreview("执行测试");
        }

        private void RunPreview(string what)
        {
            ToolTestRun run = null;
            try
            {
                HObject inputImage = ResolveSourceImage();
                run = ToolTestRun.Run(_tool, copy => ApplyTo((DlDetectTool)copy), _context.LastRunContext, inputImage);
                _overlay?.Dispose();
                _overlay = null;
                ResultList.Items.Clear();
                if (!run.Result.IsSuccess)
                {
                    SummaryText.Text = "运行失败：" + run.Result.Message;
                    ShowSourceImage();
                    PreviewImageView.ClearOverlay();
                    SetStatus($"{what}：{run.Result.Message}");
                    return;
                }
                FlowContext ctx = run.Context;
                string module = run.ModuleName;
                var boxes = ctx.GetVariable(module, "Boxes").GetValue<object[]>();
                foreach (object item in boxes)
                {
                    if (item is not DlDetectBox box)
                    {
                        continue;
                    }
                    ResultList.Items.Add(new ResultRow
                    {
                        Index = ResultList.Items.Count + 1,
                        ClassName = box.ClassName,
                        Score = box.Score.ToString("0.0000", CultureInfo.InvariantCulture),
                        Row1 = box.Row1.ToString("0.0", CultureInfo.InvariantCulture),
                        Col1 = box.Col1.ToString("0.0", CultureInfo.InvariantCulture),
                        Row2 = box.Row2.ToString("0.0", CultureInfo.InvariantCulture),
                        Col2 = box.Col2.ToString("0.0", CultureInfo.InvariantCulture)
                    });
                }

                // 叠加：检测框轮廓（XLD）
                HObject contours = ((HalconXld)ctx.GetVariable(module, "Contours").Value).Object;
                HOperatorSet.CountObj(contours, out HTuple contourCount);
                if (contourCount.I > 0)
                {
                    _overlay = contours.CopyObj(1, -1);
                }
                ShowSourceImage();
                SummaryText.Text = $"检出数量 {boxes.Length}，Found {ctx.GetVariable(module, "Found").Value}，"
                    + $"设备 {ctx.GetVariable(module, "DeviceUsed").Value}";
                SetStatus($"{what}完成：{SummaryText.Text}");
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException || ex is HalconException || ex is ArgumentException)
            {
                ShowError($"{what}失败：{ex.Message}");
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
            catch (FormatException ex)
            {
                ShowError("参数保存失败：" + ex.Message);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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

        // ======================= 格式与解析 =======================

        private static string NullIfEmpty(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static int ParseInt(TextBox box, string name)
        {
            if (!int.TryParse((box.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new FormatException($"参数 {name} 应为整数：{box.Text}");
            }
            return value;
        }

        private static double ParseDouble(TextBox box, string name)
        {
            if (!double.TryParse((box.Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw new FormatException($"参数 {name} 应为数字：{box.Text}");
            }
            return value;
        }

        private static TEnum ParseEnum<TEnum>(string name, string parameter) where TEnum : struct
        {
            if (Enum.TryParse(name, out TEnum value))
            {
                return value;
            }
            throw new FormatException($"参数 {parameter} 取值无效：{name}");
        }
    }
}
