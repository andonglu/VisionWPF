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
    /// 读码编辑窗口（RC-01）：右侧为输入与识别参数（一维码 / 二维码参数按读码方式显隐），
    /// 左侧显示当前图像与识别结果叠加、结果列表（内容 / 码制 / 质量等级）。
    /// “用当前图像训练”仅二维码可用：在一次性副本上训练，序列化模型写回窗口持有的训练数据，
    /// 随“确定”一并保存到工具。执行测试经 ToolTestRun，只有“确定”写回工具。
    /// </summary>
    public partial class WpfBarcodeToolEditWindow : Window
    {
        private sealed class ResultRow
        {
            public int Index { get; set; }
            public string Content { get; set; }
            public string CodeType { get; set; }
            public string Grade { get; set; }
        }

        private static readonly string[] OneDCodeTypes =
        {
            "auto", "EAN-13", "EAN-8", "EAN-13+2", "EAN-13+5", "UPC-A", "UPC-E", "Code 128",
            "Code 39", "Code 93", "Codabar", "Interleaved 2 of 5", "GS1-128", "GS1 DataBar"
        };

        private static readonly string[] DataCodeTypes =
        {
            "QR Code", "Data Matrix ECC 200", "PDF417", "Aztec Code", "GS1 DataMatrix", "Micro QR Code"
        };

        private static readonly string[] RecognitionLevels =
        {
            "standard_recognition", "enhanced_recognition", "maximum_recognition"
        };

        private readonly Barcode1DTool _tool;
        private readonly ToolEditContext _context;
        private byte[] _modelData;
        private HObject _overlay;

        public WpfBarcodeToolEditWindow(Barcode1DTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            _modelData = tool.DataCodeModelData;
            InitializeComponent();
            Title = "读码 - " + tool.ModuleName;
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

        private CodeKind SelectedCodeKind =>
            CodeKindCombo.SelectedItem is string name && Enum.TryParse(name, out CodeKind kind) ? kind : _tool.CodeKind;

        // ======================= 参数区 =======================

        private void InitializeOptions()
        {
            foreach (string name in Enum.GetNames(typeof(CodeKind)))
            {
                CodeKindCombo.Items.Add(name);
            }
            foreach (string type in OneDCodeTypes)
            {
                CodeTypeCombo.Items.Add(type);
            }
            foreach (string type in DataCodeTypes)
            {
                DataCodeTypeCombo.Items.Add(type);
            }
            foreach (string level in RecognitionLevels)
            {
                RecognitionLevelCombo.Items.Add(level);
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
            CodeKindCombo.SelectedItem = _tool.CodeKind.ToString();
            CodeTypeCombo.Text = _tool.CodeType ?? "auto";
            DataCodeTypeCombo.Text = _tool.DataCodeType ?? "QR Code";
            RecognitionLevelCombo.Text = _tool.RecognitionLevel ?? "standard_recognition";
            MaxCodesText.Text = _tool.MaxCodes.ToString(CultureInfo.InvariantCulture);
            GradeQualityCheck.IsChecked = _tool.GradeQuality;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
            UpdateTrainInfo();
        }

        private void UpdateVisibility()
        {
            bool is2D = SelectedCodeKind == CodeKind.DataCode2D;
            CodeTypeLabel.Visibility = is2D ? Visibility.Collapsed : Visibility.Visible;
            CodeTypeCombo.Visibility = CodeTypeLabel.Visibility;
            DataCodeTypeLabel.Visibility = is2D ? Visibility.Visible : Visibility.Collapsed;
            DataCodeTypeCombo.Visibility = DataCodeTypeLabel.Visibility;
            RecognitionLevelLabel.Visibility = DataCodeTypeLabel.Visibility;
            RecognitionLevelCombo.Visibility = DataCodeTypeLabel.Visibility;
            TrainButton.IsEnabled = is2D;
        }

        private void CodeKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateVisibility();
            UpdateTrainInfo();
        }

        private void UpdateTrainInfo()
        {
            TrainInfoText.Text = _modelData != null && _modelData.Length > 0
                ? $"已训练：二维码模型数据 {_modelData.Length} 字节（随“确定”保存到工具）；重新训练会替换现有训练数据。"
                : "尚未训练：按码制与识别强度新建模型；训练后在一次性副本上执行“用当前图像训练”可提升难识别样本的读取率。";
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试 / 训练时为一次性副本）。</summary>
        private void ApplyTo(Barcode1DTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text);
            target.RegionPath = NullIfEmpty(RegionPathCombo.Text);
            target.CodeKind = SelectedCodeKind;
            target.CodeType = NullIfEmpty(CodeTypeCombo.Text) ?? "auto";
            target.DataCodeType = NullIfEmpty(DataCodeTypeCombo.Text) ?? "QR Code";
            target.RecognitionLevel = NullIfEmpty(RecognitionLevelCombo.Text) ?? "standard_recognition";
            target.MaxCodes = ParseInt(MaxCodesText, nameof(Barcode1DTool.MaxCodes));
            target.GradeQuality = GradeQualityCheck.IsChecked == true;
            target.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
            target.DataCodeModelData = _modelData;
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

        // ======================= 训练 =======================

        /// <summary>用当前图像训练二维码模型：在一次性副本上应用界面参数后训练，序列化数据写回窗口训练数据字段（“确定”时随参数保存）。</summary>
        private void Train_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCodeKind != CodeKind.DataCode2D)
            {
                ShowError("训练只对二维码（DataCode2D）可用。");
                return;
            }
            HObject image = ResolveSourceImage();
            if (image == null || !image.IsInitialized())
            {
                ShowError("没有可训练的图像：请先选择图像输入并运行一次流程。");
                return;
            }
            ToolBase copy = null;
            try
            {
                copy = ToolEditTransaction.CopyConfiguration(_tool);
                ApplyTo((Barcode1DTool)copy);
                ((Barcode1DTool)copy).TrainDataCodeModel(null, image);
                _modelData = ((Barcode1DTool)copy).DataCodeModelData;
                UpdateTrainInfo();
                SetStatus($"训练完成：二维码模型数据 {_modelData.Length} 字节，已写入训练数据（“确定”后保存到工具）。");
            }
            catch (Exception ex) when (ex is HalconException || ex is InvalidOperationException || ex is ArgumentException)
            {
                ShowError("训练失败：" + ex.Message);
            }
            finally
            {
                if (copy != null)
                {
                    FlowResources.Release(copy);
                }
            }
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
                run = ToolTestRun.Run(_tool, copy => ApplyTo((Barcode1DTool)copy), _context.LastRunContext, _context.InputImage);
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
                var codes = ctx.GetVariable(module, "Codes").GetValue<string[]>();
                var codeTypes = ctx.GetVariable(module, "CodeTypes").GetValue<string[]>();
                var grades = ctx.GetVariable(module, "Grades").GetValue<double[]>();
                for (int i = 0; i < codes.Length; i++)
                {
                    ResultList.Items.Add(new ResultRow
                    {
                        Index = i + 1,
                        Content = codes[i],
                        CodeType = i < codeTypes.Length ? codeTypes[i] : string.Empty,
                        Grade = FormatGrade(i < grades.Length ? grades[i] : double.NaN)
                    });
                }

                // 叠加：二维码优先符号轮廓（XLD），一维码用码区域
                HObject contours = ((HalconXld)ctx.GetVariable(module, "SymbolContours").Value).Object;
                HObject regions = ((HalconRegion)ctx.GetVariable(module, "Region").Value).Object;
                HOperatorSet.CountObj(contours, out HTuple contourCount);
                HObject overlay = contourCount.I > 0 ? contours.CopyObj(1, -1) : regions.CopyObj(1, -1);
                _overlay = overlay;
                ShowSourceImage();
                SummaryText.Text = $"识别数量 {codes.Length}，码制 {SelectedCodeKind}，Found {ctx.GetVariable(module, "Found").Value}";
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

        private static string FormatGrade(double grade)
        {
            if (double.IsNaN(grade))
            {
                return "-";
            }
            string letter = grade >= 3.5 ? "A" : grade >= 2.5 ? "B" : grade >= 1.5 ? "C" : grade >= 0.5 ? "D" : "F";
            return $"{grade:0.#} ({letter})";
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
    }
}
