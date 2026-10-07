using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using Microsoft.Win32;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 差分检测编辑窗口（MT-06）：训练样本只存活于本次编辑会话（训练后即弃，不进流程文件）；
    /// 训练结果整体替换 VariationModelData 数组并清空外部模型文件；外部文件与内嵌数据互斥；
    /// 标准图 / 偏差图用 get_variation_model 读回预览；执行测试经 ToolTestRun 在一次性副本上运行。
    /// </summary>
    public partial class WpfVariationInspectToolEditWindow : Window
    {
        private const string PreviewCurrent = "当前图像";
        private const string PreviewIdeal = "标准图";
        private const string PreviewVariation = "偏差图";
        private const string PreviewResult = "检测结果";

        private static readonly string[] ImageExtensions = { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" };

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

        private sealed class DefectRow
        {
            public int Index { get; set; }
            public string Area { get; set; }
        }

        private static readonly Choice<VariationModelMode>[] ModeChoices =
        {
            new Choice<VariationModelMode>(VariationModelMode.standard, "standard（均值 + 标准差，多张样本）"),
            new Choice<VariationModelMode>(VariationModelMode.robust, "robust（中值 + 中值偏差，抗个别坏样本）"),
            new Choice<VariationModelMode>(VariationModelMode.direct, "direct（单张良品图）")
        };

        private static readonly Choice<VariationCompareMode>[] CompareChoices =
        {
            new Choice<VariationCompareMode>(VariationCompareMode.absolute, "absolute（过亮或过暗）"),
            new Choice<VariationCompareMode>(VariationCompareMode.light, "light（只检过亮）"),
            new Choice<VariationCompareMode>(VariationCompareMode.dark, "dark（只检过暗）"),
            new Choice<VariationCompareMode>(VariationCompareMode.light_dark, "light_dark（亮、暗分别比较）")
        };

        private readonly VariationInspectTool _tool;
        private readonly ToolEditContext _context;
        private readonly List<HObject> _samples = new List<HObject>();
        /// <summary>窗口内待提交的模型来源（内嵌数据 / 外部文件，互斥），确定时写回工具。</summary>
        private readonly VariationInspectTool _modelState = new VariationInspectTool("模型");

        private byte[] PendingModelData => _modelState.VariationModelData;
        private string PendingModelFile => string.IsNullOrWhiteSpace(_modelState.ModelFile) ? null : _modelState.ModelFile;
        private HObject _resultImage;
        private HObject _resultRegion;

        public WpfVariationInspectToolEditWindow(VariationInspectTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "差分检测 - " + tool.ModuleName;
            InitializeOptions();
            LoadFromTool();
            UpdateSampleText();
            UpdateModelInfo();
            if (!ShowLastRunResult())
            {
                PreviewCombo.SelectedItem = PreviewCurrent;
            }
            Closed += (s, e) => ReleaseSessionObjects();
        }

        private VariationModelMode SelectedMode => (ModelModeCombo.SelectedItem as Choice<VariationModelMode>)?.Value ?? VariationModelMode.standard;
        private VariationCompareMode SelectedCompareMode => (CompareModeCombo.SelectedItem as Choice<VariationCompareMode>)?.Value ?? VariationCompareMode.absolute;

        private void InitializeOptions()
        {
            PreviewCombo.ItemsSource = new[] { PreviewCurrent, PreviewIdeal, PreviewVariation, PreviewResult };
            ModelModeCombo.ItemsSource = ModeChoices;
            CompareModeCombo.ItemsSource = CompareChoices;
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ImagePathCombo.Items.Add("Input.Image");
            }
            MatrixPathCombo.Items.Add(string.Empty);
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
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(HomMat2D)))
                {
                    MatrixPathCombo.Items.Add(candidate.Path);
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
            ImagePathCombo.Text = string.IsNullOrWhiteSpace(_tool.ImagePath) && ImagePathCombo.Items.Count > 0
                ? Convert.ToString(ImagePathCombo.Items[0], CultureInfo.CurrentCulture)
                : _tool.ImagePath ?? string.Empty;
            MatrixPathCombo.Text = _tool.MatrixPath ?? string.Empty;
            RegionPathCombo.Text = _tool.RegionPath ?? string.Empty;
            ModelModeCombo.SelectedItem = ModeChoices.First(c => c.Value == _tool.ModelMode);
            CompareModeCombo.SelectedItem = CompareChoices.First(c => c.Value == _tool.CompareMode);
            AbsThresholdText.Text = _tool.AbsThreshold ?? string.Empty;
            VarThresholdText.Text = _tool.VarThreshold ?? string.Empty;
            MinDefectAreaText.Text = _tool.MinDefectArea.ToString(CultureInfo.CurrentCulture);
            _modelState.VariationModelData = _tool.VariationModelData;
            _modelState.ModelFile = _tool.ModelFile;
        }

        // ======================= 训练样本 =======================

        private void AddCurrentSample_Click(object sender, RoutedEventArgs e)
        {
            HObject image = ResolveCurrentImage();
            if (image == null)
            {
                ShowError("没有可用的当前图像：请先在主窗口加载图像或运行一次流程。");
                return;
            }
            try
            {
                HomMat2D matrix = ResolveLastRunMatrix();
                HObject sample;
                if (matrix != null)
                {
                    // 与运行时一致：按定位矩阵的逆矩阵把样本对齐到模型位置
                    HOperatorSet.AffineTransImage(image, out sample, matrix.Inverted().Data, "constant", "false");
                }
                else
                {
                    sample = image.CopyObj(1, -1);
                }
                _samples.Add(sample);
                UpdateSampleText();
                SetStatus(matrix != null ? "已加入当前图像（已按定位矩阵对齐到模型位置）。" : "已加入当前图像。");
            }
            catch (Exception ex) when (ex is HalconException || ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException)
            {
                ShowError("加入当前图像失败：" + ex.Message);
            }
        }

        private void AddFolderSamples_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "选择良品图像文件夹" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            int added = 0;
            int skipped = 0;
            var failed = new List<string>();
            foreach (string file in Directory.EnumerateFiles(dialog.FolderName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    HOperatorSet.ReadImage(out HObject image, file);
                    _samples.Add(image);
                    added++;
                }
                catch (HalconException ex)
                {
                    failed.Add(Path.GetFileName(file) + "（" + ex.Message + "）");
                }
            }
            UpdateSampleText();
            string message = $"批量加入 {added} 张（跳过非图像文件 {skipped} 个）";
            if (failed.Count > 0)
            {
                ShowError(message + $"；读取失败 {failed.Count} 个，已跳过：" + string.Join("、", failed));
            }
            else
            {
                SetStatus(message + "。");
            }
        }

        private void ClearSamples_Click(object sender, RoutedEventArgs e)
        {
            DisposeSamples();
            UpdateSampleText();
            SetStatus("已清空训练样本。");
        }

        private void Train_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                VariationModelMode mode = SelectedMode;
                byte[] data = VariationModelTraining.Train(_samples, mode);
                // 重新训练后改用内嵌的新模型（清空外部文件），避免运行时仍从外部旧文件加载
                _modelState.UseEmbeddedModel(data);
                UpdateModelInfo();
                string hint = VariationModelTraining.LargeModelHint(data.Length);
                SetStatus($"训练完成：{mode}，样本 {_samples.Count} 张，模型 {VariationModelTraining.FormatSize(data.Length)}。" + (hint == null ? string.Empty : " " + hint + "。"));
                PreviewCombo.SelectedItem = PreviewIdeal;
                ShowPreview();
            }
            catch (Exception ex) when (ex is HalconException || ex is InvalidOperationException)
            {
                ShowError("训练失败：" + ex.Message);
            }
        }

        private void ModelModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded)
            {
                UpdateModelInfo();
            }
        }

        // ======================= 模型存储 =======================

        private void ExportModelFile_Click(object sender, RoutedEventArgs e)
        {
            byte[] data = CurrentModelBytes(out string error);
            if (data == null)
            {
                ShowError(error ?? "尚未训练模型，无法导出。");
                return;
            }
            var dialog = new SaveFileDialog
            {
                Title = "导出差分模型",
                Filter = "差分模型 (*.vfvm)|*.vfvm|所有文件 (*.*)|*.*",
                FileName = (ModuleNameText.Text ?? "差分检测").Trim() + ".vfvm"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            try
            {
                File.WriteAllBytes(dialog.FileName, data);
                _modelState.UseModelFile(dialog.FileName);
                UpdateModelInfo();
                SetStatus("已导出外部模型文件，内嵌数据已清空：" + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowError("导出失败：" + ex.Message);
            }
        }

        private void SelectModelFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择差分模型文件",
                Filter = "差分模型 (*.vfvm)|*.vfvm|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            try
            {
                if (!VariationModelPackage.TryReadHeader(File.ReadAllBytes(dialog.FileName), out VariationModelPackage header, out string error))
                {
                    ShowError("模型文件无效：" + error);
                    return;
                }
                _modelState.UseModelFile(dialog.FileName);
                ModelModeCombo.SelectedItem = ModeChoices.First(c => c.Value == header.Mode);
                UpdateModelInfo();
                SetStatus("已改用外部模型文件，内嵌数据已清空：" + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowError("读取模型文件失败：" + ex.Message);
            }
        }

        private void EmbedModel_Click(object sender, RoutedEventArgs e)
        {
            if (PendingModelFile == null)
            {
                SetStatus("当前已是内嵌模型。");
                return;
            }
            try
            {
                byte[] data = File.ReadAllBytes(PendingModelFile);
                if (!VariationModelPackage.TryReadHeader(data, out _, out string error))
                {
                    ShowError("模型文件无效：" + error);
                    return;
                }
                _modelState.UseEmbeddedModel(data);
                UpdateModelInfo();
                SetStatus("已把外部模型文件改为内嵌（外部文件配置已清空）。");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowError("读取模型文件失败：" + ex.Message);
            }
        }

        private byte[] CurrentModelBytes(out string error)
        {
            error = null;
            if (PendingModelFile != null)
            {
                if (!File.Exists(PendingModelFile))
                {
                    error = "外部模型文件不存在：" + PendingModelFile;
                    return null;
                }
                return File.ReadAllBytes(PendingModelFile);
            }
            return PendingModelData != null && PendingModelData.Length > 0 ? PendingModelData : null;
        }

        private void UpdateSampleText()
        {
            SampleCountText.Text = $"样本 {_samples.Count} 张";
        }

        private void UpdateModelInfo()
        {
            ModelFileText.Text = PendingModelFile ?? string.Empty;
            ModeMismatchText.Visibility = Visibility.Collapsed;
            LargeModelHintText.Visibility = Visibility.Collapsed;
            byte[] data;
            try
            {
                data = CurrentModelBytes(out string error);
                if (data == null)
                {
                    ModelInfoText.Text = error ?? "模型：未训练";
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ModelInfoText.Text = "读取外部模型文件失败：" + ex.Message;
                return;
            }
            if (!VariationModelPackage.TryReadHeader(data, out VariationModelPackage header, out string headerError))
            {
                ModelInfoText.Text = headerError;
                return;
            }
            ModelInfoText.Text = $"模型：{header.Mode}，{header.Width}×{header.Height}，样本 {header.SampleCount} 张，"
                + $"{VariationModelTraining.FormatSize(data.Length)}（{data.Length.ToString(CultureInfo.InvariantCulture)} 字节，{(PendingModelFile != null ? "外部文件" : "内嵌")}）";
            if (header.Mode != SelectedMode)
            {
                ModeMismatchText.Text = $"建模方式已改为 {SelectedMode}，需要重新训练（当前模型按 {header.Mode} 训练）。";
                ModeMismatchText.Visibility = Visibility.Visible;
            }
            string hint = PendingModelFile == null ? VariationModelTraining.LargeModelHint(data.Length) : null;
            if (hint != null)
            {
                LargeModelHintText.Text = hint + "。";
                LargeModelHintText.Visibility = Visibility.Visible;
            }
        }

        // ======================= 预览 =======================

        private void PreviewCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ShowPreview));
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PreviewCombo.SelectedItem as string == PreviewCurrent)
                {
                    ShowPreview();
                }
            }));
        }

        private void ShowPreview()
        {
            string selected = PreviewCombo.SelectedItem as string;
            PreviewImageView.ClearOverlay();
            if (selected == PreviewIdeal || selected == PreviewVariation)
            {
                ShowModelImage(selected == PreviewIdeal);
                return;
            }
            if (selected == PreviewResult)
            {
                if (_resultImage == null)
                {
                    PreviewImageView.ClearImage();
                    PreviewInfoText.Text = "尚无检测结果，请先执行测试。";
                    return;
                }
                PreviewImageView.ShowImage(_resultImage);
                if (_resultRegion != null)
                {
                    PreviewImageView.SetOverlay(_resultRegion);
                }
                PreviewInfoText.Text = "检测结果：" + ResultSummaryText.Text;
                return;
            }
            HObject image = ResolveCurrentImage();
            if (image != null)
            {
                PreviewImageView.ShowImage(image);
                PreviewInfoText.Text = "当前图像：" + (ImagePathCombo.Text ?? string.Empty).Trim();
            }
            else
            {
                PreviewImageView.ClearImage();
                PreviewInfoText.Text = "没有可用的当前图像（请先运行流程）。";
            }
        }

        private void ShowModelImage(bool ideal)
        {
            byte[] data;
            try
            {
                data = CurrentModelBytes(out string error);
                if (data == null)
                {
                    PreviewImageView.ClearImage();
                    PreviewInfoText.Text = error ?? "尚未训练模型，没有标准图 / 偏差图可预览。";
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PreviewImageView.ClearImage();
                PreviewInfoText.Text = "读取外部模型文件失败：" + ex.Message;
                return;
            }
            HObject idealImage = null;
            HObject variationImage = null;
            HObject scaled = null;
            HObject display = null;
            try
            {
                VariationModelTraining.GetPreview(data, out idealImage, out variationImage);
                if (ideal)
                {
                    PreviewImageView.ShowImage(idealImage);
                }
                else
                {
                    // 偏差图为 real 图像：按 0 ~ 最大值线性映射到 0 ~ 255（不用 scale_image_max，避免把微小差异拉伸成黑白对比）
                    HOperatorSet.GetDomain(variationImage, out HObject domain);
                    HOperatorSet.MinMaxGray(domain, variationImage, 0, out _, out HTuple max, out _);
                    domain.Dispose();
                    HOperatorSet.ScaleImage(variationImage, out scaled, max.D > 0 ? 255.0 / max.D : 0.0, 0);
                    HOperatorSet.ConvertImageType(scaled, out display, "byte");
                    PreviewImageView.ShowImage(display);
                }
                PreviewInfoText.Text = ideal ? "标准图：良品的平均外观。" : "偏差图：各像素允许的灰度波动（按 0 ~ 最大值显示，越亮越宽松）。";
            }
            catch (Exception ex) when (ex is HalconException || ex is InvalidDataException)
            {
                PreviewImageView.ClearImage();
                PreviewInfoText.Text = "读取标准图 / 偏差图失败：" + ex.Message;
            }
            finally
            {
                idealImage?.Dispose();
                variationImage?.Dispose();
                scaled?.Dispose();
                display?.Dispose();
            }
        }

        private HObject ResolveCurrentImage()
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
            return image != null && image.IsInitialized() ? image : null;
        }

        private HomMat2D ResolveLastRunMatrix()
        {
            string path = (MatrixPathCombo.Text ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                return null;
            }
            if (_context.LastRunContext == null)
            {
                throw new InvalidOperationException("已配置定位矩阵，但还没有运行结果可取矩阵：请先运行一次流程再加入样本。");
            }
            object value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
            return value as HomMat2D ?? throw new InvalidOperationException($"定位矩阵 {path} 不是单个 HomMat2D");
        }

        // ======================= 运行与提交 =======================

        private bool ShowLastRunResult()
        {
            FlowContext last = _context.LastRunContext;
            if (last == null || !last.TryGetVariable(_tool.ModuleName, "DefectRegion", out Variable regionVar) || !(regionVar.Value is HalconRegion region))
            {
                return false;
            }
            HalconImage image = last.TryGetVariable(_tool.ModuleName, "Image", out Variable imageVar) ? imageVar.Value as HalconImage : null;
            double[] areas = last.TryGetVariable(_tool.ModuleName, "DefectAreas", out Variable areasVar) && areasVar.Value is double[] values ? values : new double[0];
            KeepResult(image?.Object, region.Object, areas);
            PreviewCombo.SelectedItem = PreviewResult;
            return true;
        }

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            ToolTestRun run = null;
            try
            {
                run = ToolTestRun.Run(_tool, copy => ApplyTo((VariationInspectTool)copy), _context.LastRunContext, _context.InputImage);
                if (!run.Result.IsSuccess)
                {
                    ResultSummaryText.Text = "运行失败：" + run.Result.Message;
                    DefectGrid.ItemsSource = null;
                    SetStatus("运行测试：" + run.Result.Message);
                    return;
                }
                FlowContext ctx = run.Context;
                string module = run.ModuleName;
                HalconImage image = ctx.TryGetVariable(module, "Image", out Variable imageVar) ? imageVar.Value as HalconImage : null;
                HalconRegion region = ctx.TryGetVariable(module, "DefectRegion", out Variable regionVar) ? regionVar.Value as HalconRegion : null;
                double[] areas = ctx.TryGetVariable(module, "DefectAreas", out Variable areasVar) && areasVar.Value is double[] values ? values : new double[0];
                KeepResult(image?.Object, region?.Object, areas);
                PreviewCombo.SelectedItem = PreviewResult;
                ShowPreview();
                SetStatus("运行测试完成：" + ResultSummaryText.Text);
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

        /// <summary>复制一份检测结果留给预览（运行上下文随即释放）。</summary>
        private void KeepResult(HObject image, HObject region, double[] areas)
        {
            _resultImage?.Dispose();
            _resultRegion?.Dispose();
            _resultImage = image != null && image.IsInitialized() ? image.CopyObj(1, -1) : null;
            _resultRegion = region != null && region.IsInitialized() ? region.CopyObj(1, -1) : null;
            DefectGrid.ItemsSource = areas.Select((area, i) => new DefectRow
            {
                Index = i,
                Area = area.ToString("F0", CultureInfo.CurrentCulture)
            }).ToList();
            ResultSummaryText.Text = areas.Length > 0
                ? $"缺陷 {areas.Length} 个，最大面积 {areas.Max().ToString("F0", CultureInfo.CurrentCulture)}"
                : "未发现缺陷";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
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

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）。</summary>
        private void ApplyTo(VariationInspectTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = (ImagePathCombo.Text ?? string.Empty).Trim();
            string matrix = (MatrixPathCombo.Text ?? string.Empty).Trim();
            target.MatrixPath = matrix.Length == 0 ? null : matrix;
            string region = (RegionPathCombo.Text ?? string.Empty).Trim();
            target.RegionPath = region.Length == 0 ? null : region;
            target.ModelMode = SelectedMode;
            target.CompareMode = SelectedCompareMode;
            target.AbsThreshold = (AbsThresholdText.Text ?? string.Empty).Trim();
            target.VarThreshold = (VarThresholdText.Text ?? string.Empty).Trim();
            if (!int.TryParse((MinDefectAreaText.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int minArea))
            {
                throw new FormatException("最小缺陷面积不是有效整数。");
            }
            target.MinDefectArea = minArea;
            target.VariationModelData = PendingModelData;
            target.ModelFile = PendingModelFile;
        }

        private void DisposeSamples()
        {
            foreach (HObject sample in _samples)
            {
                sample.Dispose();
            }
            _samples.Clear();
        }

        private void ReleaseSessionObjects()
        {
            DisposeSamples();
            _resultImage?.Dispose();
            _resultRegion?.Dispose();
            _resultImage = null;
            _resultRegion = null;
        }

        private void SetStatus(string message)
        {
            StatusText.Text = message;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "差分检测", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(message);
        }
    }
}
