using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    /// 深度学习分割编辑窗口（DL-03）：右侧为输入、模型与分割参数，
    /// 左侧显示当前 / 加载的图像与分割叠加（每类调色板颜色、拒识灰色，参照颜色分割编辑窗口）、
    /// 各类面积列表（类别 / 类别 ID / 面积）。执行测试经 ToolTestRun 在一次性副本上进行，
    /// 只有“确定”才把界面参数写回工具。
    /// </summary>
    public partial class WpfDlSegmentToolEditWindow : Window
    {
        private sealed class ResultRow
        {
            public int Index { get; set; }
            public string ClassName { get; set; }
            public string ClassId { get; set; }
            public string Area { get; set; }
        }

        /// <summary>类显示颜色（WPF 色），按类序循环使用；拒识区域固定灰色（128）。</summary>
        private static readonly Color[] ClassPalette =
        {
            Color.FromRgb(196, 43, 28),
            Color.FromRgb(46, 125, 50),
            Color.FromRgb(31, 78, 121),
            Color.FromRgb(220, 170, 20),
            Color.FromRgb(176, 48, 150),
            Color.FromRgb(30, 160, 170),
            Color.FromRgb(230, 120, 30),
            Color.FromRgb(160, 130, 40)
        };

        private readonly DlSegmentTool _tool;
        private readonly ToolEditContext _context;
        private HObject _overlay;
        private HObject _loadedImage;

        public WpfDlSegmentToolEditWindow(DlSegmentTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "深度学习分割 - " + tool.ModuleName;
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
            ClassFilterText.Text = _tool.ClassFilter ?? string.Empty;
            OptimizeForInferenceCheck.IsChecked = _tool.OptimizeForInference;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）。</summary>
        private void ApplyTo(DlSegmentTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text) ?? "Input.Image";
            target.RegionPath = NullIfEmpty(RegionPathCombo.Text);
            target.ModelFilePath = NullIfEmpty(ModelFilePathText.Text);
            target.Device = ParseEnum<DeepLearningDevicePreference>(DeviceCombo.SelectedItem as string, nameof(DlSegmentTool.Device));
            target.BatchSize = ParseInt(BatchSizeText, nameof(DlSegmentTool.BatchSize));
            target.ModelCacheMode = ParseEnum<ModelCacheMode>(ModelCacheModeCombo.SelectedItem as string, nameof(DlSegmentTool.ModelCacheMode));
            target.MinScore = ParseDouble(MinScoreText, nameof(DlSegmentTool.MinScore));
            target.ClassFilter = NullIfEmpty(ClassFilterText.Text);
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
                Title = "选择 .hdl 分割模型",
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
                run = ToolTestRun.Run(_tool, copy => ApplyTo((DlSegmentTool)copy), _context.LastRunContext, inputImage);
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
                string[] classNames = ctx.GetVariable(module, "ClassNames").GetValue<string[]>();
                int[] classIds = ctx.GetVariable(module, "ClassIds").GetValue<int[]>();
                int[] areas = ctx.GetVariable(module, "Areas").GetValue<int[]>();
                for (int i = 0; i < classNames.Length; i++)
                {
                    ResultList.Items.Add(new ResultRow
                    {
                        Index = i + 1,
                        ClassName = classNames[i],
                        ClassId = i < classIds.Length ? classIds[i].ToString(CultureInfo.InvariantCulture) : string.Empty,
                        Area = i < areas.Length ? areas[i].ToString(CultureInfo.InvariantCulture) : string.Empty
                    });
                }

                // 叠加：每类区域按类序着调色板颜色，拒识区域灰色（参照颜色分割编辑窗口）。
                HObject classRegions = ((HalconRegion)ctx.GetVariable(module, "Regions").Value).Object;
                HObject rejected = ((HalconRegion)ctx.GetVariable(module, "RejectedRegion").Value).Object;
                _overlay = BuildSegmentOverlay(classRegions, rejected);
                ShowSourceImage();
                SummaryText.Text = $"类数 {classNames.Length}，非空类 {ctx.GetVariable(module, "Count").Value}，"
                    + $"Found {ctx.GetVariable(module, "Found").Value}，设备 {ctx.GetVariable(module, "DeviceUsed").Value}";
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

        /// <summary>构造分割预览叠加：每类区域按类序着调色板颜色，拒识区域灰色。</summary>
        private HObject BuildSegmentOverlay(HObject classRegions, HObject rejected)
        {
            HObject source = ResolveSourceImage();
            if (source == null || !source.IsInitialized())
            {
                return null;
            }
            HOperatorSet.GetImageSize(source, out HTuple width, out HTuple height);
            HOperatorSet.CountObj(classRegions, out HTuple classCount);
            HObject red = null, green = null, blue = null;
            try
            {
                HOperatorSet.GenImageConst(out red, "byte", width, height);
                HOperatorSet.GenImageConst(out green, "byte", width, height);
                HOperatorSet.GenImageConst(out blue, "byte", width, height);
                for (int i = 1; i <= classCount.I; i++)
                {
                    Color wpf = ClassPalette[(i - 1) % ClassPalette.Length];
                    HOperatorSet.SelectObj(classRegions, out HObject single, i);
                    Paint(ref red, single, wpf.R);
                    Paint(ref green, single, wpf.G);
                    Paint(ref blue, single, wpf.B);
                    single.Dispose();
                }
                Paint(ref red, rejected, 128);
                Paint(ref green, rejected, 128);
                Paint(ref blue, rejected, 128);
                HOperatorSet.Compose3(red, green, blue, out HObject overlay);
                HObject result = overlay.CopyObj(1, -1);
                overlay.Dispose();
                return result;
            }
            finally
            {
                red?.Dispose();
                green?.Dispose();
                blue?.Dispose();
            }
        }

        private static void Paint(ref HObject channel, HObject region, byte gray)
        {
            HOperatorSet.CountObj(region, out HTuple count);
            if (count.I > 0)
            {
                HOperatorSet.PaintRegion(region, channel, out HObject painted, gray, "fill");
                channel.Dispose();
                channel = painted;
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
