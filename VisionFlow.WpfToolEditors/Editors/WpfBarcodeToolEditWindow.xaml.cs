using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Tools.Halcon;
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

        /// <summary>高级参数表格行：名称 / 值 / 说明摘要（与 ModelParams 文本双向同步）。</summary>
        private sealed class ParamRow
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Summary { get; set; }
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
            // 两个高级参数列表各自持有：只把当前读码方式的那份显示在表格/文本框中
            _barcodeParamsText = _tool.BarcodeParams ?? string.Empty;
            _dataCodeParamsText = _tool.DataCodeParams ?? string.Empty;
            ModelParamsText.Text = ParamsTextFor(SelectedCodeKind);
            _paramsLoaded = true;
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
            UpdatePickerScope();
        }

        // ======================= 高级参数列表与候选选择器 =======================

        private bool _syncingParams;

        /// <summary>两侧高级参数列表的编辑内容：当前读码方式的显示在表格/文本框，另一侧暂存这里，切回时恢复。</summary>
        private string _barcodeParamsText = string.Empty;
        private string _dataCodeParamsText = string.Empty;
        private bool _paramsLoaded;

        private string ParamsTextFor(CodeKind kind) =>
            kind == CodeKind.DataCode2D ? _dataCodeParamsText : _barcodeParamsText;

        /// <summary>确定/测试/训练前把当前文本框内容写回当前模式对应的字段。</summary>
        private void StashCurrentParamsText()
        {
            if (!_paramsLoaded)
            {
                return;
            }
            if (SelectedCodeKind == CodeKind.DataCode2D)
            {
                _dataCodeParamsText = ModelParamsText.Text ?? string.Empty;
            }
            else
            {
                _barcodeParamsText = ModelParamsText.Text ?? string.Empty;
            }
        }

        /// <summary>候选选择器数据源随读码方式 + 二维码码制切换；一维码给出每次运行生效提示。</summary>
        private void UpdatePickerScope()
        {
            bool is2D = SelectedCodeKind == CodeKind.DataCode2D;
            string scope = is2D
                ? "datacode2d:" + (NullIfEmpty(DataCodeTypeCombo.Text) ?? "QR Code")
                : "barcode1d";
            ParamPicker.Scope = scope;
            ParamsListLabel.Text = is2D ? "模型参数列表（二维码）" : "模型参数列表（一维码）";
            ModelParamsLabel.Text = is2D
                ? "高级参数（二维码，与上方列表双向同步）"
                : "高级参数（一维码，与上方列表双向同步）";
            PickerHintText.Text = is2D
                ? "二维码模型参数：从候选中选择添加，模型创建后生效。"
                : "一维码模型参数（每次运行生效）：从候选中选择添加。";
        }

        private void DataCodeTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePickerScope();
        }

        private string LookupParamSummary(string name)
        {
            HalconParamEntry entry = HalconParamCatalog.GetEntries(ParamPicker.Scope)
                .FirstOrDefault(e => e.Name == name);
            if (entry == null)
            {
                return string.Empty;
            }
            string description = entry.Description ?? string.Empty;
            int cut = description.IndexOfAny(new[] { '。', '；', ';', ':' });
            if (cut > 0)
            {
                description = description.Substring(0, cut);
            }
            return description.Length > 40 ? description.Substring(0, 40) + "…" : description;
        }

        /// <summary>文本框 → 表格：解析每行 名称=值（跳过空行与 # 注释），刷新表格。</summary>
        private void ModelParamsText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingParams)
            {
                return;
            }
            _syncingParams = true;
            try
            {
                ParamsList.Items.Clear();
                string[] lines = (ModelParamsText.Text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string name = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (name.Length == 0)
                    {
                        continue;
                    }
                    ParamsList.Items.Add(new ParamRow { Name = name, Value = value, Summary = LookupParamSummary(name) });
                }
            }
            finally
            {
                _syncingParams = false;
            }
        }

        /// <summary>表格 → 文本框：仅列表中的参数会写入 ModelParams。</summary>
        private void SyncParamTextFromTable()
        {
            _syncingParams = true;
            try
            {
                ModelParamsText.Text = string.Join("\r\n",
                    ParamsList.Items.Cast<ParamRow>().Select(r => r.Name + "=" + r.Value));
            }
            finally
            {
                _syncingParams = false;
            }
        }

        private void ParamPicker_ParameterAdded(string nameValue)
        {
            int eq = nameValue.IndexOf('=');
            if (eq <= 0)
            {
                return;
            }
            string name = nameValue.Substring(0, eq);
            string value = nameValue.Substring(eq + 1);
            ParamRow existing = ParamsList.Items.Cast<ParamRow>().FirstOrDefault(r => r.Name == name);
            if (existing != null)
            {
                existing.Value = value;
                ParamsList.Items.Refresh();
            }
            else
            {
                ParamsList.Items.Add(new ParamRow { Name = name, Value = value, Summary = LookupParamSummary(name) });
            }
            SyncParamTextFromTable();
            SetStatus($"已添加模型参数 {nameValue}。");
        }

        private void RemoveParam_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ParamRow row)
            {
                ParamsList.Items.Remove(row);
                SyncParamTextFromTable();
            }
        }

        private void CodeKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CodeKind incoming = SelectedCodeKind;
            UpdateVisibility();
            // 切换读码方式： outgoing 一侧的内容存回字段，载入 incoming 一侧（表格随文本解析刷新）
            CodeKind? outgoing = null;
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is string prev && Enum.TryParse(prev, out CodeKind prevKind))
            {
                outgoing = prevKind;
            }
            if (_paramsLoaded && outgoing != null && outgoing.Value != incoming)
            {
                if (outgoing.Value == CodeKind.DataCode2D)
                {
                    _dataCodeParamsText = ModelParamsText.Text ?? string.Empty;
                }
                else
                {
                    _barcodeParamsText = ModelParamsText.Text ?? string.Empty;
                }
                // 载入 incoming 一侧内容，TextChanged 会刷新参数表格
                ModelParamsText.Text = ParamsTextFor(incoming);
            }
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
            StashCurrentParamsText();
            target.BarcodeParams = NullIfEmpty(_barcodeParamsText);
            target.DataCodeParams = NullIfEmpty(_dataCodeParamsText);
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

        /// <summary>训练图像列表中“当前图像”条目的显示文本（训练时取预览图像）。</summary>
        private const string CurrentImageMarker = "（当前图像）";

        private void AddCurrentImage_Click(object sender, RoutedEventArgs e)
        {
            HObject image = ResolveSourceImage();
            if (image == null || !image.IsInitialized())
            {
                ShowError("没有可用的当前图像：请先选择图像输入并运行一次流程。");
                return;
            }
            if (!TrainImageList.Items.Contains(CurrentImageMarker))
            {
                TrainImageList.Items.Add(CurrentImageMarker);
            }
            SetStatus("已添加当前图像到训练列表。");
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择训练图像（可多选）",
                Filter = "图像文件 (*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff)|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件 (*.*)|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            int added = 0;
            foreach (string file in dialog.FileNames)
            {
                if (!TrainImageList.Items.Contains(file))
                {
                    TrainImageList.Items.Add(file);
                    added++;
                }
            }
            SetStatus($"已添加 {added} 张文件图像到训练列表（重复项已跳过）。");
        }

        private void RemoveTrainImage_Click(object sender, RoutedEventArgs e)
        {
            if (TrainImageList.SelectedItem is string item)
            {
                TrainImageList.Items.Remove(item);
            }
        }

        private void ClearTrainImages_Click(object sender, RoutedEventArgs e)
        {
            TrainImageList.Items.Clear();
        }

        /// <summary>按训练列表顺序逐张训练（当前图像项取预览图像，文件项临时读图），完成后把训练数据写回窗口字段（“确定”时随参数保存）。</summary>
        private void TrainAll_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCodeKind != CodeKind.DataCode2D)
            {
                ShowError("训练只对二维码（DataCode2D）可用。");
                return;
            }
            if (TrainImageList.Items.Count == 0)
            {
                ShowError("训练图像列表为空：请先“添加当前图像”或“添加文件…”。");
                return;
            }
            var images = new List<HObject>();
            var owned = new List<HObject>();
            ToolBase copy = null;
            try
            {
                foreach (string item in TrainImageList.Items.Cast<string>())
                {
                    if (item == CurrentImageMarker)
                    {
                        HObject image = ResolveSourceImage();
                        if (image == null || !image.IsInitialized())
                        {
                            ShowError("训练列表中的“当前图像”不可用：请先选择图像输入并运行一次流程。");
                            return;
                        }
                        images.Add(image);
                    }
                    else
                    {
                        HOperatorSet.ReadImage(out HObject fileImage, item);
                        owned.Add(fileImage);
                        images.Add(fileImage);
                    }
                }
                copy = ToolEditTransaction.CopyConfiguration(_tool);
                ApplyTo((Barcode1DTool)copy);
                ((Barcode1DTool)copy).TrainDataCodeModel(null, images);
                _modelData = ((Barcode1DTool)copy).DataCodeModelData;
                UpdateTrainInfo();
                SetStatus($"训练完成：共 {images.Count} 张图像，二维码模型数据 {_modelData.Length} 字节，已写入训练数据（“确定”后保存到工具）。");
            }
            catch (Exception ex) when (ex is HalconException || ex is InvalidOperationException || ex is ArgumentException || ex is System.IO.IOException)
            {
                ShowError("训练失败：" + ex.Message);
            }
            finally
            {
                if (copy != null)
                {
                    FlowResources.Release(copy);
                }
                foreach (HObject image in owned)
                {
                    image.Dispose();
                }
            }
        }

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
