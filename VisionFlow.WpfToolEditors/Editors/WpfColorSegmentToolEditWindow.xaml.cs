using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    /// 颜色分割编辑窗口（RC-04）：左侧管理颜色类（增删改名）并为每类在图像上框选样本区域，
    /// 全部类有样本后“训练”在一次性副本上调用 ColorSegmentTool.Train，序列化分类器写回窗口训练数据
    /// 字段（随“确定”保存到工具）；“执行测试”按类显示分割叠加（每类一个颜色、拒识区域灰色）。
    /// </summary>
    public partial class WpfColorSegmentToolEditWindow : Window
    {
        /// <summary>类显示颜色（HALCON 颜色名 + WPF 色），按类序循环使用。</summary>
        private static readonly (string Halcon, Color Wpf)[] ClassPalette =
        {
            ("red", Color.FromRgb(196, 43, 28)),
            ("green", Color.FromRgb(46, 125, 50)),
            ("blue", Color.FromRgb(31, 78, 121)),
            ("yellow", Color.FromRgb(220, 170, 20)),
            ("magenta", Color.FromRgb(176, 48, 150)),
            ("cyan", Color.FromRgb(30, 160, 170)),
            ("orange", Color.FromRgb(230, 120, 30)),
            ("gold", Color.FromRgb(160, 130, 40))
        };

        private sealed class ClassItem : INotifyPropertyChanged
        {
            public string Id { get; set; }
            public Rectangle1Roi SampleRoi { get; set; }
            public int Area { get; set; } = -1;

            private string _name;
            public string Name
            {
                get { return _name; }
                set
                {
                    _name = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
                }
            }

            public Brush SwatchBrush { get; set; } = Brushes.Transparent;
            public string SampleText => SampleRoi != null ? "已框选" : "未框选";
            public string AreaText => Area >= 0 ? Area.ToString(CultureInfo.InvariantCulture) : "-";

            public event PropertyChangedEventHandler PropertyChanged;

            public void RefreshSample()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SampleText)));
            }

            public void RefreshArea()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AreaText)));
            }
        }

        private readonly ColorSegmentTool _tool;
        private readonly ToolEditContext _context;
        private readonly List<ClassItem> _classes = new List<ClassItem>();
        private byte[] _classifierData;
        private HObject _overlay;
        private ClassItem _samplingClass;
        private bool _syncingRois;
        private int _classCounter;

        public WpfColorSegmentToolEditWindow(ColorSegmentTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            _classifierData = tool.ClassifierData;
            InitializeComponent();
            Title = "颜色分割 - " + tool.ModuleName;
            InitializeOptions();
            LoadFromTool();
            ShowSourceImage();
            PreviewImageView.RoiChanged += OnRoiChanged;
            Closed += (s, e) =>
            {
                PreviewImageView.RoiChanged -= OnRoiChanged;
                _overlay?.Dispose();
                _overlay = null;
            };
        }

        private ClassItem SelectedClass => ClassList.SelectedItem as ClassItem;

        // ======================= 参数区与类管理 =======================

        private void InitializeOptions()
        {
            foreach (string name in Enum.GetNames(typeof(ColorSegmentClassifier)))
            {
                ClassifierCombo.Items.Add(name);
            }
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
            ClassifierCombo.SelectedItem = _tool.Classifier.ToString();
            RejectionThresholdText.Text = Format(_tool.RejectionThreshold);
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
            foreach (string name in ParseClassNames(_tool.ClassNames))
            {
                AddClass(name);
            }
            UpdateTrainInfo();
        }

        private void AddClass(string name)
        {
            _classCounter++;
            (string halcon, Color wpf) = ClassPalette[_classes.Count % ClassPalette.Length];
            var item = new ClassItem
            {
                Id = "类" + _classCounter,
                Name = name,
                SwatchBrush = new SolidColorBrush(wpf)
            };
            item.SampleRoi = null;
            _classes.Add(item);
            ClassList.Items.Add(item);
            ClassList.SelectedItem = item;
        }

        private void AddClass_Click(object sender, RoutedEventArgs e)
        {
            int next = _classes.Count + 1;
            string name = "颜色" + next;
            while (_classes.Any(c => c.Name == name))
            {
                next++;
                name = "颜色" + next;
            }
            AddClass(name);
            SetStatus($"已添加类“{name}”：请在图像上为它框选样本区域。");
        }

        private void RemoveClass_Click(object sender, RoutedEventArgs e)
        {
            ClassItem item = SelectedClass;
            if (item == null)
            {
                ShowError("请先在列表中选中一个颜色类。");
                return;
            }
            _classes.Remove(item);
            ClassList.Items.Remove(item);
            RebuildSampleRois();
            _classifierData = null;
            UpdateTrainInfo();
            SetStatus($"已删除类“{item.Name}”（训练数据已失效，请重新训练）。");
        }

        /// <summary>按类序重画样本 ROI（增删类或清除样本后调用）。</summary>
        private void RebuildSampleRois()
        {
            _syncingRois = true;
            try
            {
                PreviewImageView.Rois.Clear();
                foreach (ClassItem item in _classes)
                {
                    if (item.SampleRoi != null)
                    {
                        (string halcon, _) = PaletteOf(item);
                        item.SampleRoi.Color = halcon;
                        PreviewImageView.Rois.Add(item.SampleRoi);
                    }
                }
            }
            finally
            {
                _syncingRois = false;
            }
        }

        private (string Halcon, Color Wpf) PaletteOf(ClassItem item)
        {
            return ClassPalette[_classes.IndexOf(item) % ClassPalette.Length];
        }

        private void ClearSample_Click(object sender, RoutedEventArgs e)
        {
            ClassItem item = SelectedClass;
            if (item == null)
            {
                ShowError("请先在列表中选中一个颜色类。");
                return;
            }
            item.SampleRoi = null;
            item.RefreshSample();
            RebuildSampleRois();
            SetStatus($"已清除类“{item.Name}”的样本区域。");
        }

        private static List<string> ParseClassNames(string text)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return names;
            }
            foreach (string part in text.Split(','))
            {
                string name = part.Trim();
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
            return names;
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，训练 / 执行测试时为一次性副本）。</summary>
        private void ApplyTo(ColorSegmentTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text);
            target.Classifier = ClassifierCombo.SelectedItem is string name && Enum.TryParse(name, out ColorSegmentClassifier classifier)
                ? classifier
                : _tool.Classifier;
            target.RejectionThreshold = ParseDouble(RejectionThresholdText, nameof(ColorSegmentTool.RejectionThreshold));
            target.ClassNames = string.Join(",", _classes.Select(c => (c.Name ?? string.Empty).Trim()).Where(n => n.Length > 0));
            target.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
            target.ClassifierData = _classifierData;
        }

        private void UpdateTrainInfo()
        {
            int sampled = _classes.Count(c => c.SampleRoi != null);
            TrainInfoText.Text = _classifierData != null && _classifierData.Length > 0
                ? $"已训练：分类器数据 {_classifierData.Length} 字节（{sampled}/{_classes.Count} 类有样本）；重新训练会替换现有训练数据。"
                : $"尚未训练：{sampled}/{_classes.Count} 类已框选样本，全部类有样本后可训练。";
            TrainButton.IsEnabled = _classes.Count > 0 && sampled == _classes.Count;
        }

        // ======================= 图像与框选样本 =======================

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
            RebuildSampleRois();
            if (_overlay != null && _overlay.IsInitialized())
            {
                PreviewImageView.SetOverlay(_overlay);
            }
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ShowSourceImage));
        }

        private void Sample_Click(object sender, RoutedEventArgs e)
        {
            ClassItem item = SelectedClass;
            if (item == null)
            {
                ShowError("请先在列表中选中一个颜色类，再框选样本。");
                return;
            }
            HObject image = ResolveSourceImage();
            if (image == null || !image.IsInitialized())
            {
                ShowError("没有可框选的图像：请先选择图像输入并运行一次流程。");
                return;
            }
            HOperatorSet.CountChannels(image, out HTuple channelCount);
            if (channelCount.Length == 0 || channelCount[0].I != 3)
            {
                ShowError($"框选样本需要三通道彩色图像（当前 {(channelCount.Length > 0 ? channelCount[0].I : 0)} 通道）。");
                return;
            }
            _samplingClass = item;
            PreviewImageView.BeginAddRoi(RoiKind.Rectangle1);
            SetStatus($"请在图像上为类“{item.Name}”框选样本区域（该类已有样本时新框选会替换旧的）。");
        }

        /// <summary>框选完成：把矩形区域记为选中类的样本（替换旧样本），按类序着色显示。</summary>
        private void OnRoiChanged(object sender, RoiChangedEventArgs e)
        {
            if (_syncingRois)
            {
                return;
            }
            ClassItem target = _samplingClass;
            _samplingClass = null;
            if (target == null || !(e.Roi is Rectangle1Roi rectangle))
            {
                return;
            }
            target.SampleRoi = rectangle;
            target.RefreshSample();
            RebuildSampleRois();
            UpdateTrainInfo();
            SetStatus($"类“{target.Name}”样本已更新（拖动矩形可调整，重新框选可替换）。");
        }

        // ======================= 训练 =======================

        /// <summary>为每个类按类序拼接样本区域，在一次性副本上训练，序列化分类器写回窗口训练数据字段（“确定”时随参数保存）。</summary>
        private void Train_Click(object sender, RoutedEventArgs e)
        {
            if (_classes.Count == 0)
            {
                ShowError("请至少添加一个颜色类。");
                return;
            }
            ClassItem missing = _classes.FirstOrDefault(c => c.SampleRoi == null);
            if (missing != null)
            {
                ShowError($"类“{missing.Name}”还没有样本区域：请为每个类各框选一个样本。");
                return;
            }
            HObject image = ResolveSourceImage();
            if (image == null || !image.IsInitialized())
            {
                ShowError("没有可训练的图像：请先选择图像输入并运行一次流程。");
                return;
            }
            foreach (ClassItem item in _classes)
            {
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    ShowError("存在类名为空的类：请为每个类填写名称。");
                    return;
                }
            }
            if (_classes.Select(c => c.Name.Trim()).Distinct().Count() != _classes.Count)
            {
                ShowError("类名重复：请为每个类填写不同的名称。");
                return;
            }

            HObject classRegions = null;
            ToolBase copy = null;
            try
            {
                HOperatorSet.GenEmptyObj(out classRegions);
                foreach (ClassItem item in _classes)
                {
                    using HObject region = item.SampleRoi.ToRegion();
                    HOperatorSet.ConcatObj(classRegions, region, out HObject combined);
                    classRegions.Dispose();
                    classRegions = combined;
                }
                copy = ToolEditTransaction.CopyConfiguration(_tool);
                ApplyTo((ColorSegmentTool)copy);
                ((ColorSegmentTool)copy).Train(null, image, classRegions);
                _classifierData = ((ColorSegmentTool)copy).ClassifierData;
                UpdateTrainInfo();
                SetStatus($"训练完成：{_classes.Count} 类（{string.Join("、", _classes.Select(c => c.Name))}），分类器数据 {_classifierData.Length} 字节。");
                RunPreview("训练后预览");
            }
            catch (Exception ex) when (ex is HalconException || ex is InvalidOperationException || ex is ArgumentException)
            {
                ShowError("训练失败：" + ex.Message);
            }
            finally
            {
                classRegions?.Dispose();
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
                run = ToolTestRun.Run(_tool, copy => ApplyTo((ColorSegmentTool)copy), _context.LastRunContext, _context.InputImage);
                _overlay?.Dispose();
                _overlay = null;
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
                var areas = ctx.GetVariable(module, "ClassAreas").GetValue<int[]>();
                for (int i = 0; i < _classes.Count && i < areas.Length; i++)
                {
                    _classes[i].Area = areas[i];
                    _classes[i].RefreshArea();
                }
                HObject classRegions = ((HalconRegion)ctx.GetVariable(module, "Regions").Value).Object;
                HObject rejected = ((HalconRegion)ctx.GetVariable(module, "RejectedRegion").Value).Object;
                _overlay = BuildSegmentOverlay(classRegions, rejected);
                ShowSourceImage();
                int nonEmpty = areas.Count(a => a > 0);
                SummaryText.Text = $"类数 {areas.Length}，非空类 {nonEmpty}，Found {ctx.GetVariable(module, "Found").Value}";
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
            HObject overlay = null;
            try
            {
                HOperatorSet.GenImageConst(out red, "byte", width, height);
                HOperatorSet.GenImageConst(out green, "byte", width, height);
                HOperatorSet.GenImageConst(out blue, "byte", width, height);
                for (int i = 1; i <= classCount.I; i++)
                {
                    (_, Color wpf) = ClassPalette[(i - 1) % ClassPalette.Length];
                    HOperatorSet.SelectObj(classRegions, out HObject single, i);
                    Paint(ref red, single, wpf.R);
                    Paint(ref green, single, wpf.G);
                    Paint(ref blue, single, wpf.B);
                    single.Dispose();
                }
                Paint(ref red, rejected, 128);
                Paint(ref green, rejected, 128);
                Paint(ref blue, rejected, 128);
                HOperatorSet.Compose3(red, green, blue, out overlay);
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

        private static string Format(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static double ParseDouble(TextBox box, string name)
        {
            if (!double.TryParse((box.Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
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
