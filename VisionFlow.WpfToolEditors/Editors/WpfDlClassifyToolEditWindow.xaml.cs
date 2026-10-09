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
    /// 深度学习分类编辑窗口（DL-01）：右侧为输入、模型与分类参数，
    /// 左侧显示当前 / 加载的图像与 TopK 分类结果列表（Top1 高亮、低于 MinScore 标识）、MatchedExpected 显示。
    /// 执行测试经 ToolTestRun 在一次性副本上进行，只有“确定”才把界面参数写回工具。
    /// </summary>
    public partial class WpfDlClassifyToolEditWindow : Window
    {
        private sealed class ResultRow
        {
            public int Rank { get; set; }
            public string ClassName { get; set; }
            public string Score { get; set; }
            public bool IsTop1 { get; set; }
            public bool BelowThreshold { get; set; }
            public string ThresholdText => BelowThreshold ? "低于阈值" : "达标";
        }

        private readonly DlClassifyTool _tool;
        private readonly ToolEditContext _context;
        private HObject _loadedImage;

        public WpfDlClassifyToolEditWindow(DlClassifyTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "深度学习分类 - " + tool.ModuleName;
            InitializeOptions();
            LoadFromTool();
            ShowSourceImage();
            Closed += (s, e) =>
            {
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
            TopKText.Text = _tool.TopK.ToString(CultureInfo.InvariantCulture);
            ExpectedClassText.Text = _tool.ExpectedClass ?? string.Empty;
            OptimizeForInferenceCheck.IsChecked = _tool.OptimizeForInference;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）。</summary>
        private void ApplyTo(DlClassifyTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text) ?? "Input.Image";
            target.RegionPath = NullIfEmpty(RegionPathCombo.Text);
            target.ModelFilePath = NullIfEmpty(ModelFilePathText.Text);
            target.Device = ParseEnum<DeepLearningDevicePreference>(DeviceCombo.SelectedItem as string, nameof(DlClassifyTool.Device));
            target.BatchSize = ParseInt(BatchSizeText, nameof(DlClassifyTool.BatchSize));
            target.ModelCacheMode = ParseEnum<ModelCacheMode>(ModelCacheModeCombo.SelectedItem as string, nameof(DlClassifyTool.ModelCacheMode));
            target.MinScore = ParseDouble(MinScoreText, nameof(DlClassifyTool.MinScore));
            target.TopK = ParseInt(TopKText, nameof(DlClassifyTool.TopK));
            target.ExpectedClass = NullIfEmpty(ExpectedClassText.Text);
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
                Title = "选择 .hdl 分类模型",
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
                run = ToolTestRun.Run(_tool, copy => ApplyTo((DlClassifyTool)copy), _context.LastRunContext, inputImage);
                ResultList.Items.Clear();
                if (!run.Result.IsSuccess)
                {
                    SummaryText.Text = "运行失败：" + run.Result.Message;
                    ShowSourceImage();
                    SetStatus($"{what}：{run.Result.Message}");
                    return;
                }

                double minScore = ParseDouble(MinScoreText, nameof(DlClassifyTool.MinScore));
                FlowContext ctx = run.Context;
                string module = run.ModuleName;
                string[] names = ctx.GetVariable(module, "TopClassNames").GetValue<string[]>();
                double[] scores = ctx.GetVariable(module, "TopScores").GetValue<double[]>();
                for (int i = 0; i < names.Length; i++)
                {
                    ResultList.Items.Add(new ResultRow
                    {
                        Rank = i + 1,
                        ClassName = names[i],
                        Score = scores[i].ToString("0.0000", CultureInfo.InvariantCulture),
                        IsTop1 = i == 0,
                        BelowThreshold = scores[i] < minScore
                    });
                }

                string className = (string)ctx.GetVariable(module, "ClassName").Value;
                bool matched = (bool)ctx.GetVariable(module, "MatchedExpected").Value;
                bool expectedConfigured = NullIfEmpty(ExpectedClassText.Text) != null;
                string matchedText = !expectedConfigured ? "未设置期望类别" : matched ? "命中期望类别" : "未命中期望类别";
                SummaryText.Text = $"Top1 {className}，Found {ctx.GetVariable(module, "Found").Value}，"
                    + $"{matchedText}，设备 {ctx.GetVariable(module, "DeviceUsed").Value}";
                ShowSourceImage();
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
