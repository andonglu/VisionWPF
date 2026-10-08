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
using VisionFlow.Tools.Calibration;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 畸变校正编辑窗口（CB-06）：图像输入、相机标定来源（文件 / 内嵌，互斥）、像素当量 / 输出区域 / 插值；
    /// 执行测试经 ToolTestRun，左侧预览原图或校正结果；确定时才写回工具。
    /// </summary>
    public partial class WpfImageRectifyToolEditWindow : Window
    {
        private const string PreviewSource = "原图";
        private const string PreviewRectified = "校正结果";

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

        private static readonly Choice<CalibrationSource>[] SourceChoices =
        {
            new Choice<CalibrationSource>(CalibrationSource.File, "标定文件"),
            new Choice<CalibrationSource>(CalibrationSource.Embedded, "内嵌在工具中")
        };

        private static readonly Choice<RectifyInterpolation>[] InterpolationChoices =
        {
            new Choice<RectifyInterpolation>(RectifyInterpolation.bilinear, "bilinear（双线性）"),
            new Choice<RectifyInterpolation>(RectifyInterpolation.nearest_neighbor, "nearest_neighbor（最近邻）")
        };

        private readonly ImageRectifyTool _tool;
        private readonly ToolEditContext _context;
        private readonly ImageRectifyTool _calibrationState = new ImageRectifyTool("标定");
        private HObject _rectified;
        private bool _loading;

        public WpfImageRectifyToolEditWindow(ImageRectifyTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "畸变校正 - " + tool.ModuleName;
            _loading = true;
            InitializeOptions();
            LoadFromTool();
            _loading = false;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
            PreviewCombo.SelectedItem = PreviewSource;
            Closed += (s, e) => _rectified?.Dispose();
        }

        private CalibrationSource SelectedSource => (SourceCombo.SelectedItem as Choice<CalibrationSource>)?.Value ?? CalibrationSource.File;
        private RectifyInterpolation SelectedInterpolation => (InterpolationCombo.SelectedItem as Choice<RectifyInterpolation>)?.Value ?? RectifyInterpolation.bilinear;

        private void InitializeOptions()
        {
            PreviewCombo.ItemsSource = new[] { PreviewSource, PreviewRectified };
            SourceCombo.ItemsSource = SourceChoices;
            InterpolationCombo.ItemsSource = InterpolationChoices;
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
            ImagePathCombo.Text = _tool.ImagePath ?? string.Empty;
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == _tool.CalibrationSource);
            InterpolationCombo.SelectedItem = InterpolationChoices.First(c => c.Value == _tool.Interpolation);
            _calibrationState.CalibrationSource = _tool.CalibrationSource;
            _calibrationState.CalibrationFile = _tool.CalibrationFile;
            _calibrationState.CalibrationData = _tool.CalibrationData;
            CalibrationFileText.Text = _tool.CalibrationFile ?? string.Empty;
            PixelSizeText.Text = Format(_tool.PixelSize);
            RegionXText.Text = Format(_tool.RegionX);
            RegionYText.Text = Format(_tool.RegionY);
            RegionWidthText.Text = Format(_tool.RegionWidth);
            RegionHeightText.Text = Format(_tool.RegionHeight);
        }

        // ======================= 标定来源 =======================

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SelectedSource == _calibrationState.CalibrationSource)
            {
                return;
            }
            if (SelectedSource == CalibrationSource.File)
            {
                _calibrationState.UseCalibrationFile(NullIfEmpty(CalibrationFileText.Text));
                SetStatus("已切换为标定文件，内嵌标定数据已清空。");
            }
            else
            {
                _calibrationState.UseEmbeddedCalibration(null);
                CalibrationFileText.Text = string.Empty;
                SetStatus("已切换为内嵌：标定文件路径已清空，请选择标定文件后“把标定文件内嵌到工具”。");
            }
            UpdateSourcePanels();
            UpdateCalibrationInfo();
        }

        private void CalibrationFileText_LostFocus(object sender, RoutedEventArgs e)
        {
            string path = NullIfEmpty(CalibrationFileText.Text);
            if (_calibrationState.CalibrationSource == CalibrationSource.File && !string.Equals(path, _calibrationState.CalibrationFile, StringComparison.Ordinal))
            {
                _calibrationState.UseCalibrationFile(path);
                UpdateCalibrationInfo();
            }
        }

        private void BrowseCalibrationFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "选择标定文件", Filter = "标定文件 (*.vfcal.json)|*.vfcal.json|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            _loading = true;
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == CalibrationSource.File);
            _loading = false;
            _calibrationState.UseCalibrationFile(dialog.FileName);
            CalibrationFileText.Text = dialog.FileName;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
            SetStatus("已选择标定文件：" + dialog.FileName);
        }

        private void EmbedFile_Click(object sender, RoutedEventArgs e)
        {
            string path = _calibrationState.CalibrationSource == CalibrationSource.File ? _calibrationState.CalibrationFile : null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowError("请先选择存在的标定文件，再内嵌到工具。");
                return;
            }
            try
            {
                CalibrationResult calibration = CalibrationService.Load(path);
                if (calibration.IsLegacyTuple)
                {
                    ShowError("旧版 write_tuple 矩阵文件没有相机标定，不能用于畸变校正。");
                    return;
                }
                _calibrationState.UseEmbeddedCalibration(CalibrationService.ToJson(calibration));
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                ShowError("读取标定文件失败：" + ex.Message);
                return;
            }
            _loading = true;
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == CalibrationSource.Embedded);
            _loading = false;
            CalibrationFileText.Text = string.Empty;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
            SetStatus("已把标定文件内嵌到工具（标定文件路径已清空），确定后生效。");
        }

        private void UpdateSourcePanels()
        {
            bool file = _calibrationState.CalibrationSource == CalibrationSource.File;
            CalibrationFileLabel.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            CalibrationFilePanel.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            EmbedFileButton.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCalibrationInfo()
        {
            string origin = _calibrationState.CalibrationSource == CalibrationSource.Embedded ? "内嵌" : "文件";
            try
            {
                CalibrationResult calibration;
                if (_calibrationState.CalibrationSource == CalibrationSource.Embedded)
                {
                    if (string.IsNullOrWhiteSpace(_calibrationState.CalibrationData))
                    {
                        CalibrationInfoText.Text = "标定：内嵌，尚无标定数据";
                        return;
                    }
                    calibration = CalibrationService.Parse(_calibrationState.CalibrationData, "内嵌标定数据");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(_calibrationState.CalibrationFile))
                    {
                        CalibrationInfoText.Text = "标定：未配置标定文件";
                        return;
                    }
                    calibration = CalibrationService.Load(_calibrationState.CalibrationFile);
                }
                string text = $"标定：{calibration.Kind}（包含 {calibration.SectionsText()}），单位 {calibration.Unit ?? "未填"}";
                if (calibration.Camera != null)
                {
                    CameraCalibration camera = calibration.Camera;
                    text += string.Format(CultureInfo.CurrentCulture, "；相机 {0}，{1}×{2}，误差 {3:F4} 像素",
                        camera.CameraType, camera.GetValue("image_width"), camera.GetValue("image_height"), camera.RmsError ?? double.NaN);
                }
                string issue = _calibrationState.CheckConfiguration().FirstOrDefault(i => i.Parameter == "CalibrationFile" || i.Parameter == "CalibrationData")?.Message;
                CalibrationInfoText.Text = text + "（" + origin + "）" + (issue == null ? string.Empty : "；" + issue);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is HalconException)
            {
                CalibrationInfoText.Text = "标定：" + ex.Message;
            }
        }

        // ======================= 预览与测试 =======================

        private void PreviewCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ShowPreview));
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PreviewCombo.SelectedItem as string == PreviewSource)
                {
                    ShowPreview();
                }
            }));
        }

        private void ShowPreview()
        {
            HObject image = PreviewCombo.SelectedItem as string == PreviewRectified ? _rectified : ResolveSourceImage();
            if (image != null && image.IsInitialized())
            {
                PreviewImageView.ShowImage(image);
            }
            else
            {
                PreviewImageView.ClearImage();
            }
        }

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

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            ToolTestRun run = null;
            try
            {
                run = ToolTestRun.Run(_tool, copy => ApplyTo((ImageRectifyTool)copy), _context.LastRunContext, _context.InputImage);
                if (!run.Result.IsSuccess)
                {
                    ResultSummaryText.Text = "运行失败：" + run.Result.Message;
                    SetStatus("执行测试：" + run.Result.Message);
                    return;
                }
                FlowContext ctx = run.Context;
                string module = run.ModuleName;
                HObject image = ((HalconImage)ctx.GetVariable(module, "Image").Value).Object;
                _rectified?.Dispose();
                _rectified = image.CopyObj(1, -1);
                HOperatorSet.GetImageSize(_rectified, out HTuple width, out HTuple height);
                double pixelSize = (double)ctx.GetVariable(module, "ActualPixelSize").Value;
                double[] matrix = ((HomMat2D)ctx.GetVariable(module, "Matrix").Value).ToArray();
                ResultSummaryText.Text = string.Format(CultureInfo.InvariantCulture, "校正图 {0}×{1}，ActualPixelSize {2}，左上角 ({3}, {4})",
                    width.I, height.I, pixelSize.ToString("R", CultureInfo.InvariantCulture),
                    matrix[2].ToString("R", CultureInfo.InvariantCulture), matrix[5].ToString("R", CultureInfo.InvariantCulture));
                PreviewCombo.SelectedItem = PreviewRectified;
                ShowPreview();
                SetStatus("执行测试完成：" + ResultSummaryText.Text);
            }
            catch (Exception ex)
            {
                ShowError("执行测试失败：" + ex.Message);
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

        private void ApplyTo(ImageRectifyTool target)
        {
            CalibrationFileText_LostFocus(this, null);
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text);
            target.CalibrationSource = _calibrationState.CalibrationSource;
            target.CalibrationFile = _calibrationState.CalibrationFile;
            target.CalibrationData = _calibrationState.CalibrationData;
            target.PixelSize = ParseDouble(PixelSizeText.Text, "像素当量");
            target.RegionX = ParseDouble(RegionXText.Text, "区域左上 X");
            target.RegionY = ParseDouble(RegionYText.Text, "区域左上 Y");
            target.RegionWidth = ParseDouble(RegionWidthText.Text, "区域宽");
            target.RegionHeight = ParseDouble(RegionHeightText.Text, "区域高");
            target.Interpolation = SelectedInterpolation;
        }

        private static string NullIfEmpty(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static double ParseDouble(string text, string name)
        {
            if (double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }
            throw new FormatException($"{name} 不是有效数字。");
        }

        private static string Format(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
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
    }
}
