using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 字符识别编辑窗口（RC-02）：右侧为输入与识别参数（TextModel / DeepOcr 引擎参数按 Engine 显隐，同工具 IToolParameterVisibility），
    /// 左侧显示当前图像与字符区域叠加、逐字符结果列表（置信度低于阈值的标红）。执行测试经 ToolTestRun，只有“确定”写回工具。
    /// </summary>
    public partial class WpfOcrToolEditWindow : Window
    {
        private sealed class ResultRow
        {
            public int Index { get; set; }
            public string Char { get; set; }
            public string Confidence { get; set; }
            public string Line { get; set; }
            public Brush Brush { get; set; } = Brushes.OrangeRed;
        }

        private static readonly string[] Classifiers =
        {
            "Universal_0-9A-Z_Rej.occ", "Universal_0-9A-Z_NoRej.occ",
            "Industrial_0-9A-Z_NoRej.occ", "Document_0-9A-Z_NoRej.occ", "DotPrint_0-9A-Z_NoRej.occ"
        };

        private static readonly string[] Polarities = { "dark_on_light", "light_on_dark", "both" };
        private static readonly string[] Modes = { "auto", "recognition" };
        private static readonly string[] Devices = { "Cpu", "Gpu" };

        /// <summary>TextModel 引擎无置信度阈值参数，列表标红用的固定阈值。</summary>
        private const double DefaultConfidenceMark = 0.5;

        private readonly OcrTool _tool;
        private readonly ToolEditContext _context;
        private HObject _overlay;

        public WpfOcrToolEditWindow(OcrTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "字符识别 - " + tool.ModuleName;
            InitializeOptions();
            LoadFromTool();
            UpdateVisibility();
            ShowSourceImage();
            Closed += (s, e) =>
            {
                _overlay?.Dispose();
                _overlay = null;
            };
        }

        private OcrEngine SelectedEngine =>
            EngineCombo.SelectedItem is string name && Enum.TryParse(name, out OcrEngine engine) ? engine : _tool.Engine;

        private double ConfidenceMark =>
            SelectedEngine == OcrEngine.DeepOcr && TryParse(ConfidenceThresholdText.Text, out double threshold) && threshold > 0
                ? threshold
                : DefaultConfidenceMark;

        // ======================= 参数区 =======================

        private void InitializeOptions()
        {
            foreach (string name in Enum.GetNames(typeof(OcrEngine)))
            {
                EngineCombo.Items.Add(name);
            }
            foreach (string classifier in Classifiers)
            {
                ClassifierCombo.Items.Add(classifier);
            }
            foreach (string polarity in Polarities)
            {
                PolarityCombo.Items.Add(polarity);
            }
            foreach (string mode in Modes)
            {
                ModeCombo.Items.Add(mode);
            }
            foreach (string device in Devices)
            {
                DeviceCombo.Items.Add(device);
            }

            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ImagePathCombo.Items.Add("Input.Image");
            }
            RegionPathCombo.Items.Add(string.Empty);
            MatrixPathCombo.Items.Add(string.Empty);
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
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HomMat2D)))
                {
                    MatrixPathCombo.Items.Add(candidate.Path);
                }
            }
        }

        private void LoadFromTool()
        {
            ModuleNameText.Text = _tool.ModuleName;
            ImagePathCombo.Text = _tool.ImagePath ?? string.Empty;
            RegionPathCombo.Text = _tool.RegionPath ?? string.Empty;
            MatrixPathCombo.Text = _tool.MatrixPath ?? string.Empty;
            EngineCombo.SelectedItem = _tool.Engine.ToString();
            ClassifierCombo.Text = _tool.Classifier ?? string.Empty;
            PolarityCombo.Text = _tool.Polarity ?? string.Empty;
            DotPrintCheck.IsChecked = _tool.DotPrint;
            MinCharHeightText.Text = Format(_tool.MinCharHeight);
            MaxCharHeightText.Text = Format(_tool.MaxCharHeight);
            MinStrokeWidthText.Text = Format(_tool.MinStrokeWidth);
            MaxStrokeWidthText.Text = Format(_tool.MaxStrokeWidth);
            TextLineSeparatorsText.Text = _tool.TextLineSeparators ?? string.Empty;
            ModeCombo.Text = _tool.Mode ?? string.Empty;
            DeviceCombo.Text = _tool.Device ?? string.Empty;
            ConfidenceThresholdText.Text = Format(_tool.ConfidenceThreshold);
            AlphabetText.Text = _tool.Alphabet ?? string.Empty;
            ExpectedPatternText.Text = _tool.ExpectedPattern ?? string.Empty;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
        }

        /// <summary>按引擎显隐参数组（与 OcrTool.IsParameterVisible 一致）。</summary>
        private void UpdateVisibility()
        {
            bool textModel = SelectedEngine == OcrEngine.TextModel;
            ClassifierLabel.Visibility = ClassifierCombo.Visibility = textModel ? Visibility.Visible : Visibility.Collapsed;
            PolarityLabel.Visibility = PolarityCombo.Visibility = textModel ? Visibility.Visible : Visibility.Collapsed;
            DotPrintCheck.Visibility = textModel ? Visibility.Visible : Visibility.Collapsed;
            MinCharHeightLabel.Visibility = MinCharHeightText.Visibility = textModel ? Visibility.Visible : Visibility.Collapsed;
            MaxCharHeightLabel.Visibility = MaxCharHeightText.Visibility = textModel ? Visibility.Visible : Visibility.Collapsed;
            MinStrokeWidthLabel.Visibility = MinStrokeWidthText.Visibility = textModel ? Visibility.Visible : Visibility.Collapsed;
            MaxStrokeWidthLabel.Visibility = MaxStrokeWidthText.Visibility = textModel ? Visibility.Visible : Visibility.Collapsed;
            TextLineSeparatorsLabel.Visibility = TextLineSeparatorsText.Visibility = textModel ? Visibility.Visible : Visibility.Collapsed;
            ModeLabel.Visibility = ModeCombo.Visibility = textModel ? Visibility.Collapsed : Visibility.Visible;
            DeviceLabel.Visibility = DeviceCombo.Visibility = textModel ? Visibility.Collapsed : Visibility.Visible;
            ConfidenceThresholdLabel.Visibility = ConfidenceThresholdText.Visibility = textModel ? Visibility.Collapsed : Visibility.Visible;
        }

        private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateVisibility();
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）；数值无效时抛出说明参数名的异常。</summary>
        private void ApplyTo(OcrTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text);
            target.RegionPath = NullIfEmpty(RegionPathCombo.Text);
            target.MatrixPath = NullIfEmpty(MatrixPathCombo.Text);
            target.Engine = SelectedEngine;
            target.Classifier = NullIfEmpty(ClassifierCombo.Text) ?? "Universal_0-9A-Z_Rej.occ";
            target.Polarity = NullIfEmpty(PolarityCombo.Text) ?? "dark_on_light";
            target.DotPrint = DotPrintCheck.IsChecked == true;
            target.MinCharHeight = ParseDouble(MinCharHeightText, nameof(OcrTool.MinCharHeight));
            target.MaxCharHeight = ParseDouble(MaxCharHeightText, nameof(OcrTool.MaxCharHeight));
            target.MinStrokeWidth = ParseDouble(MinStrokeWidthText, nameof(OcrTool.MinStrokeWidth));
            target.MaxStrokeWidth = ParseDouble(MaxStrokeWidthText, nameof(OcrTool.MaxStrokeWidth));
            target.TextLineSeparators = NullIfEmpty(TextLineSeparatorsText.Text);
            target.Mode = NullIfEmpty(ModeCombo.Text) ?? "auto";
            target.Device = NullIfEmpty(DeviceCombo.Text) ?? "Cpu";
            target.ConfidenceThreshold = ParseDouble(ConfidenceThresholdText, nameof(OcrTool.ConfidenceThreshold));
            target.Alphabet = NullIfEmpty(AlphabetText.Text);
            target.ExpectedPattern = NullIfEmpty(ExpectedPatternText.Text);
            target.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
        }

        // ======================= 图像 =======================

        private HObject ResolveSourceImage()
        {
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
                return;
            }
            PreviewImageView.ShowImage(image);
            if (_overlay != null && _overlay.IsInitialized())
            {
                PreviewImageView.SetOverlay(_overlay);
            }
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ShowSourceImage));
        }

        // ======================= 预览、确定与取消 =======================

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            RunPreview("执行测试");
        }

        private void RunPreview(string what)
        {
            ToolTestRun run = null;
            try
            {
                run = ToolTestRun.Run(_tool, copy => ApplyTo((OcrTool)copy), _context.LastRunContext, _context.InputImage);
                _overlay?.Dispose();
                _overlay = null;
                ResultList.Items.Clear();
                OverlayInfoText.Text = string.Empty;
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
                var chars = ctx.GetVariable(module, "Chars").GetValue<string[]>();
                var confidences = ctx.GetVariable(module, "Confidences").GetValue<double[]>();
                var lines = ctx.GetVariable(module, "Lines").GetValue<string[]>();
                string text = ctx.GetVariable(module, "Text").GetValue<string>();
                bool patternOk = ctx.GetVariable(module, "PatternOk").GetValue<bool>();
                double minConfidence = ctx.GetVariable(module, "MinConfidence").GetValue<double>();

                // 逐字符所在行：Chars 按行分组连续排列，行内字符数 = Lines[i].Length
                var lineOf = new int[chars.Length];
                int cursor = 0;
                for (int line = 0; line < lines.Length && cursor < chars.Length; line++)
                {
                    for (int k = 0; k < lines[line].Length && cursor < chars.Length; k++, cursor++)
                    {
                        lineOf[cursor] = line;
                    }
                }

                double mark = ConfidenceMark;
                var normal = new SolidColorBrush(SystemColors.WindowTextColor);
                int below = 0;
                for (int i = 0; i < chars.Length; i++)
                {
                    double confidence = i < confidences.Length ? confidences[i] : double.NaN;
                    bool low = !double.IsNaN(confidence) && confidence < mark;
                    if (low)
                    {
                        below++;
                    }
                    ResultList.Items.Add(new ResultRow
                    {
                        Index = i + 1,
                        Char = chars[i],
                        Confidence = double.IsNaN(confidence) ? "-" : confidence.ToString("0.###", CultureInfo.InvariantCulture),
                        Line = lineOf[i] < lines.Length ? lines[lineOf[i]] : string.Empty,
                        Brush = low ? Brushes.OrangeRed : normal
                    });
                }

                // 叠加：优先字符区域，DeepOcr 的词轮廓作为补充
                HObject charRegions = ((HalconRegion)ctx.GetVariable(module, "CharRegions").Value).Object;
                HObject wordContours = ((HalconXld)ctx.GetVariable(module, "WordContours").Value).Object;
                HOperatorSet.CountObj(charRegions, out HTuple regionCount);
                HOperatorSet.CountObj(wordContours, out HTuple contourCount);
                HObject overlay = regionCount.I > 0 ? charRegions.CopyObj(1, -1) : wordContours.CopyObj(1, -1);
                _overlay = overlay;
                OverlayInfoText.Text = contourCount.I > 0 && regionCount.I > 0
                    ? "同时输出了词轮廓（WordContours），可在流程中引用。"
                    : string.Empty;
                ShowSourceImage();
                SummaryText.Text = $"字符 {chars.Length} 个、行数 {lines.Length}，最低置信度 {(double.IsNaN(minConfidence) ? "-" : minConfidence.ToString("0.###", CultureInfo.InvariantCulture))}，PatternOk {patternOk}，低于阈值 {below} 个";
                SetStatus($"{what}完成：识别文本「{(text.Length > 40 ? text.Substring(0, 40) + "…" : text.Replace(Environment.NewLine, " "))}」");
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

        private static string Format(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool TryParse(string text, out double value)
        {
            return double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static double ParseDouble(TextBox box, string name)
        {
            if (!TryParse(box.Text, out double value))
            {
                throw new FormatException($"参数 {name} 应为数值：{box.Text}");
            }
            return value;
        }

        private static string NullIfEmpty(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
