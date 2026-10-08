using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using VisionFlow.Controls;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 阈值分割编辑窗口（IP-08）：运行参数按分割方式显示（同侧栏，标签为参数名）；颜色方式可在图像上点击取色，
    /// 按容差自动填入当前方式（HSV / RGB）的各通道范围并预览提取结果。执行测试经 ToolTestRun，确定时才写回工具。
    /// </summary>
    public partial class WpfThresholdToolEditWindow : Window
    {
        private sealed class Field
        {
            public PropertyInfo Property { get; set; }
            public FrameworkElement Editor { get; set; }
            public List<UIElement> Elements { get; } = new List<UIElement>();
        }

        private static readonly Type[] FieldTypes = { typeof(int), typeof(double), typeof(bool) };

        private readonly ThresholdTool _tool;
        private readonly ToolEditContext _context;
        private readonly List<Field> _fields = new List<Field>();
        private ComboBox _methodCombo;
        private HObject _overlay;
        private bool _picking;

        public WpfThresholdToolEditWindow(ThresholdTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "阈值分割 - " + tool.ModuleName;
            InitializeImagePaths();
            ModuleNameText.Text = _tool.ModuleName;
            ImagePathCombo.Text = _tool.ImagePath ?? string.Empty;
            BuildFields();
            UpdateVisibility();
            ShowSourceImage();
            PreviewImageView.PointPicked += OnPointPicked;
            Closed += (s, e) =>
            {
                PreviewImageView.PointPicked -= OnPointPicked;
                _overlay?.Dispose();
                _overlay = null;
            };
        }

        private ThresholdSegmentMethod SelectedMethod =>
            _methodCombo?.SelectedItem is string name && Enum.TryParse(name, out ThresholdSegmentMethod method) ? method : _tool.SegmentMethod;

        private bool IsColorMethod => SelectedMethod == ThresholdSegmentMethod.ColorHsv || SelectedMethod == ThresholdSegmentMethod.ColorRgb;

        // ======================= 参数区 =======================

        private void InitializeImagePaths()
        {
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

        /// <summary>按工具属性生成参数行：分割方式下拉在首行，其余 int / double / bool / 枚举参数依次排列，按方式显隐。</summary>
        private void BuildFields()
        {
            _methodCombo = new ComboBox();
            foreach (string name in Enum.GetNames(typeof(ThresholdSegmentMethod)))
            {
                _methodCombo.Items.Add(name);
            }
            _methodCombo.SelectedItem = _tool.SegmentMethod.ToString();
            AddRow(nameof(ThresholdTool.SegmentMethod), _methodCombo);
            _methodCombo.SelectionChanged += (s, e) => UpdateVisibility();

            foreach (PropertyInfo property in _tool.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite || property.Name == nameof(ThresholdTool.SegmentMethod)
                    || (!FieldTypes.Contains(property.PropertyType) && !property.PropertyType.IsEnum))
                {
                    continue;
                }
                var field = new Field { Property = property };
                object value = property.GetValue(_tool);
                if (property.PropertyType == typeof(bool))
                {
                    var check = new CheckBox { Content = property.Name, IsChecked = (bool)value, Margin = new Thickness(0, 4, 0, 8) };
                    field.Editor = check;
                    field.Elements.AddRange(AddRow(null, check));
                }
                else if (property.PropertyType.IsEnum)
                {
                    var combo = new ComboBox();
                    foreach (string name in Enum.GetNames(property.PropertyType))
                    {
                        combo.Items.Add(name);
                    }
                    combo.SelectedItem = value.ToString();
                    field.Editor = combo;
                    field.Elements.AddRange(AddRow(property.Name, combo));
                }
                else
                {
                    var text = new TextBox { Text = Convert.ToString(value, CultureInfo.InvariantCulture) };
                    field.Editor = text;
                    field.Elements.AddRange(AddRow(property.Name, text));
                }
                _fields.Add(field);
            }
        }

        private IEnumerable<UIElement> AddRow(string label, FrameworkElement editor)
        {
            int row = ParameterGrid.RowDefinitions.Count;
            ParameterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var elements = new List<UIElement>();
            if (label != null)
            {
                var text = new TextBlock { Text = label };
                text.SetResourceReference(StyleProperty, "EditorFieldLabelStyle");
                Grid.SetRow(text, row);
                Grid.SetColumn(text, 0);
                ParameterGrid.Children.Add(text);
                elements.Add(text);
            }
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, label == null ? 0 : 1);
            if (label == null)
            {
                Grid.SetColumnSpan(editor, 2);
            }
            if (editor is Control control)
            {
                control.Margin = new Thickness(0, 0, 0, 8);
            }
            ParameterGrid.Children.Add(editor);
            elements.Add(editor);
            return elements;
        }

        private void UpdateVisibility()
        {
            var probe = new ThresholdTool("显隐") { SegmentMethod = SelectedMethod };
            foreach (Field field in _fields)
            {
                Visibility state = probe.IsParameterVisible(field.Property.Name) ? Visibility.Visible : Visibility.Collapsed;
                field.Elements.ForEach(e => e.Visibility = state);
            }
            ColorHintText.Visibility = IsColorMethod ? Visibility.Visible : Visibility.Collapsed;
            PickColorButton.IsEnabled = IsColorMethod;
        }

        private Field FindField(string name) => _fields.First(f => f.Property.Name == name);

        private void SetFieldText(string name, int value)
        {
            ((TextBox)FindField(name).Editor).Text = value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）；数值无效时抛出说明参数名的异常。</summary>
        private void ApplyTo(ThresholdTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            string imagePath = (ImagePathCombo.Text ?? string.Empty).Trim();
            target.ImagePath = imagePath.Length == 0 ? null : imagePath;
            target.SegmentMethod = SelectedMethod;
            foreach (Field field in _fields)
            {
                Type type = field.Property.PropertyType;
                object value;
                if (field.Editor is CheckBox check)
                {
                    value = check.IsChecked == true;
                }
                else if (field.Editor is ComboBox combo)
                {
                    value = Enum.Parse(type, (string)combo.SelectedItem);
                }
                else
                {
                    string text = (((TextBox)field.Editor).Text ?? string.Empty).Trim();
                    if (type == typeof(int))
                    {
                        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                        {
                            throw new FormatException($"参数 {field.Property.Name} 应为整数：{text}");
                        }
                        value = number;
                    }
                    else
                    {
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                        {
                            throw new FormatException($"参数 {field.Property.Name} 应为数值：{text}");
                        }
                        value = number;
                    }
                }
                field.Property.SetValue(target, value);
            }
        }

        // ======================= 图像与取色 =======================

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

        private bool ShowSourceImage()
        {
            HObject image = ResolveSourceImage();
            if (image == null || !image.IsInitialized())
            {
                PreviewImageView.ClearImage();
                return false;
            }
            PreviewImageView.ShowImage(image);
            if (_overlay != null)
            {
                PreviewImageView.SetOverlay(_overlay);
            }
            return true;
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => ShowSourceImage()));
        }

        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            HObject image = ResolveSourceImage();
            if (image == null || !image.IsInitialized())
            {
                ShowError("没有可取色的图像：请先选择图像输入并运行一次流程。");
                return;
            }
            string imageError = CheckColorImage(image);
            if (imageError != null)
            {
                ShowError(imageError);
                return;
            }
            if (!TryParseTolerance(out _))
            {
                return;
            }
            ShowSourceImage();
            _picking = true;
            PreviewImageView.BeginPickPoint();
            SetStatus($"请在图像上点击要提取的颜色（{(SelectedMethod == ThresholdSegmentMethod.ColorHsv ? "HSV" : "RGB")}，容差 {ToleranceText.Text.Trim()}）。");
        }

        private void OnPointPicked(object sender, ImagePointEventArgs e)
        {
            if (!_picking)
            {
                return;
            }
            HObject image = ResolveSourceImage();
            if (image == null || CheckColorImage(image) != null || !TryParseTolerance(out int tolerance))
            {
                return;
            }
            int row = (int)Math.Round(e.Row);
            int column = (int)Math.Round(e.Column);
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            if (row < 0 || column < 0 || row >= height.I || column >= width.I)
            {
                SetStatus($"取色点 ({row}, {column}) 在图像外，请在图像范围内点击。");
                return;
            }
            HOperatorSet.GetGrayval(image, row, column, out HTuple rgb);
            int red = rgb[0].I, green = rgb[1].I, blue = rgb[2].I;
            ToHsv(red, green, blue, out int hue, out int saturation, out int value);
            PickInfoText.Text = $"取色 ({row}, {column})：RGB ({red}, {green}, {blue})，HSV ({hue}, {saturation}, {value})";
            if (SelectedMethod == ThresholdSegmentMethod.ColorHsv)
            {
                if (tolerance >= 128)
                {
                    SetFieldText(nameof(ThresholdTool.HueMin), 0);
                    SetFieldText(nameof(ThresholdTool.HueMax), 255);
                }
                else
                {
                    // 色相是环形量：范围越过 0 / 255 时回绕，得到 HueMin > HueMax（跨 0）
                    SetFieldText(nameof(ThresholdTool.HueMin), ((hue - tolerance) % 256 + 256) % 256);
                    SetFieldText(nameof(ThresholdTool.HueMax), (hue + tolerance) % 256);
                }
                SetFieldText(nameof(ThresholdTool.SaturationMin), Math.Max(0, saturation - tolerance));
                SetFieldText(nameof(ThresholdTool.SaturationMax), Math.Min(255, saturation + tolerance));
                SetFieldText(nameof(ThresholdTool.ValueMin), Math.Max(0, value - tolerance));
                SetFieldText(nameof(ThresholdTool.ValueMax), Math.Min(255, value + tolerance));
            }
            else
            {
                SetFieldText(nameof(ThresholdTool.RedMin), Math.Max(0, red - tolerance));
                SetFieldText(nameof(ThresholdTool.RedMax), Math.Min(255, red + tolerance));
                SetFieldText(nameof(ThresholdTool.GreenMin), Math.Max(0, green - tolerance));
                SetFieldText(nameof(ThresholdTool.GreenMax), Math.Min(255, green + tolerance));
                SetFieldText(nameof(ThresholdTool.BlueMin), Math.Max(0, blue - tolerance));
                SetFieldText(nameof(ThresholdTool.BlueMax), Math.Min(255, blue + tolerance));
            }
            RunPreview("取色后预览");
        }

        /// <summary>用 1 × 1 图像经 trans_from_rgb 换算，与工具运行时的 HSV 量化完全相同。</summary>
        private static void ToHsv(int red, int green, int blue, out int hue, out int saturation, out int value)
        {
            HOperatorSet.GenImageConst(out HObject r, "byte", 1, 1);
            HOperatorSet.GenImageConst(out HObject g, "byte", 1, 1);
            HOperatorSet.GenImageConst(out HObject b, "byte", 1, 1);
            HObject h = null, s = null, v = null;
            try
            {
                HOperatorSet.SetGrayval(r, 0, 0, red);
                HOperatorSet.SetGrayval(g, 0, 0, green);
                HOperatorSet.SetGrayval(b, 0, 0, blue);
                HOperatorSet.TransFromRgb(r, g, b, out h, out s, out v, "hsv");
                HOperatorSet.GetGrayval(h, 0, 0, out HTuple hv);
                HOperatorSet.GetGrayval(s, 0, 0, out HTuple sv);
                HOperatorSet.GetGrayval(v, 0, 0, out HTuple vv);
                hue = hv.I;
                saturation = sv.I;
                value = vv.I;
            }
            finally
            {
                r.Dispose();
                g.Dispose();
                b.Dispose();
                h?.Dispose();
                s?.Dispose();
                v?.Dispose();
            }
        }

        private static string CheckColorImage(HObject image)
        {
            HOperatorSet.CountChannels(image, out HTuple channels);
            int count = channels.Length > 0 ? channels[0].I : 0;
            if (count != 3)
            {
                return $"取色需要三通道彩色图像（当前 {count} 通道）";
            }
            HOperatorSet.GetImageType(image, out HTuple type);
            return type.Length > 0 && type[0].S == "byte" ? null : $"取色需要 byte 彩色图像（当前 {(type.Length > 0 ? type[0].S : "?")}）";
        }

        private bool TryParseTolerance(out int tolerance)
        {
            if (int.TryParse((ToleranceText.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tolerance)
                && tolerance >= 0 && tolerance <= 255)
            {
                return true;
            }
            ShowError("取色容差应为 0 ~ 255 的整数。");
            return false;
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
                run = ToolTestRun.Run(_tool, copy => ApplyTo((ThresholdTool)copy), _context.LastRunContext, _context.InputImage);
                _overlay?.Dispose();
                _overlay = null;
                if (!run.Result.IsSuccess)
                {
                    ResultSummaryText.Text = "运行失败：" + run.Result.Message;
                    ShowSourceImage();
                    PreviewImageView.ClearOverlay();
                    SetStatus($"{what}：{run.Result.Message}");
                    return;
                }
                FlowContext ctx = run.Context;
                string module = run.ModuleName;
                HObject region = ((HalconRegion)ctx.GetVariable(module, "Region").Value).Object;
                _overlay = region.CopyObj(1, -1);
                int count = Convert.ToInt32(ctx.GetVariable(module, "Count").Value, CultureInfo.InvariantCulture);
                HOperatorSet.Union1(region, out HObject all);
                HOperatorSet.AreaCenter(all, out HTuple area, out _, out _);
                all.Dispose();
                string used = ctx.TryGetVariable(module, "UsedThreshold", out Variable usedThreshold)
                    ? "，UsedThreshold " + Convert.ToDouble(usedThreshold.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
                    : string.Empty;
                ResultSummaryText.Text = $"区域数 {count}，总面积 {(area.Length > 0 ? area[0].I : 0)}{used}";
                ShowSourceImage();
                SetStatus($"{what}完成：{ResultSummaryText.Text}");
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
    }
}
