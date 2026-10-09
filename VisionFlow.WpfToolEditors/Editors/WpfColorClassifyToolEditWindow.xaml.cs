using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HalconDotNet;
using VisionFlow.Controls.Roi;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 颜色识别编辑窗口（RC-03）：右侧为输入与识别参数（References 每行一个参考颜色），
    /// 左侧显示当前图像、参考颜色代表色块与试运行结果（每区域标签 / 距离 / 均值色）。
    /// “框选样本”在图像上框一个矩形区域，按所选色彩空间取均值自动追加一行参考颜色。
    /// 执行测试经 ToolTestRun，只有“确定”写回工具。
    /// </summary>
    public partial class WpfColorClassifyToolEditWindow : Window
    {
        private sealed class ResultRow
        {
            public int Index { get; set; }
            public string Label { get; set; }
            public string Distance { get; set; }
            public Brush MeanBrush { get; set; } = Brushes.Transparent;
        }

        private sealed class ReferenceRow
        {
            public string Name;
            public double C1, C2, C3;
        }

        private const double DefaultMaxDistance = 60;

        private readonly ColorClassifyTool _tool;
        private readonly ToolEditContext _context;
        private HObject _overlay;
        private bool _sampling;
        private bool _syncingList;

        public WpfColorClassifyToolEditWindow(ColorClassifyTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "颜色识别 - " + tool.ModuleName;
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

        private ColorClassifySpace SelectedColorSpace =>
            ColorSpaceCombo.SelectedItem is string name && Enum.TryParse(name, out ColorClassifySpace space) ? space : _tool.ColorSpace;

        // ======================= 参数区 =======================

        private void InitializeOptions()
        {
            foreach (string name in Enum.GetNames(typeof(ColorClassifySpace)))
            {
                ColorSpaceCombo.Items.Add(name);
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
            ColorSpaceCombo.SelectedItem = _tool.ColorSpace.ToString();
            ReferencesText.Text = _tool.References ?? string.Empty;
            UnknownLabelText.Text = _tool.UnknownLabel ?? string.Empty;
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
            RefreshLegend();
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）。</summary>
        private void ApplyTo(ColorClassifyTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text);
            target.RegionPath = NullIfEmpty(RegionPathCombo.Text);
            target.ColorSpace = SelectedColorSpace;
            target.References = NullIfEmpty(ReferencesText.Text);
            target.UnknownLabel = NullIfEmpty(UnknownLabelText.Text) ?? "未知";
            target.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
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
            if (_overlay != null && _overlay.IsInitialized())
            {
                PreviewImageView.SetOverlay(_overlay);
            }
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ShowSourceImage));
        }

        private void ColorSpaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshLegend();
        }

        private void ReferencesText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_syncingList)
            {
                RefreshLegend();
            }
        }

        private void Sample_Click(object sender, RoutedEventArgs e)
        {
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
            _sampling = true;
            PreviewImageView.BeginAddRoi(RoiKind.Rectangle1);
            SetStatus($"请在图像上框选样本区域：取均值后按 {SelectedColorSpace} 色彩空间追加一行参考颜色。");
        }

        /// <summary>框选完成：取区域均值（与工具运行同一换算），以默认名称 / 距离追加到 References。</summary>
        private void OnRoiChanged(object sender, RoiChangedEventArgs e)
        {
            if (!_sampling || !(e.Roi is Rectangle1Roi rectangle))
            {
                return;
            }
            _sampling = false;
            HObject image = ResolveSourceImage();
            if (image == null || !image.IsInitialized())
            {
                return;
            }
            HObject region = null;
            try
            {
                region = rectangle.ToRegion();
                // 与工具运行一致：先分解三通道，再按所选色彩空间转换后取区域均值
                double c1, c2, c3;
                HObject first = null, second = null, third = null, converted1 = null, converted2 = null, converted3 = null;
                try
                {
                    HOperatorSet.Decompose3(image, out first, out second, out third);
                    HObject channel1 = first, channel2 = second, channel3 = third;
                    if (SelectedColorSpace != ColorClassifySpace.Rgb)
                    {
                        HOperatorSet.TransFromRgb(first, second, third, out converted1, out converted2, out converted3,
                            SelectedColorSpace == ColorClassifySpace.Hsv ? "hsv" : "cielab");
                        channel1 = converted1;
                        channel2 = converted2;
                        channel3 = converted3;
                    }
                    HOperatorSet.Intensity(region, channel1, out HTuple m1, out HTuple _);
                    HOperatorSet.Intensity(region, channel2, out HTuple m2, out HTuple _);
                    HOperatorSet.Intensity(region, channel3, out HTuple m3, out HTuple _);
                    c1 = m1.D;
                    c2 = m2.D;
                    c3 = m3.D;
                }
                finally
                {
                    first?.Dispose();
                    second?.Dispose();
                    third?.Dispose();
                    converted1?.Dispose();
                    converted2?.Dispose();
                    converted3?.Dispose();
                }
                string name = "颜色" + (ParseReferences().Count + 1);
                string line = string.Format(CultureInfo.InvariantCulture, "{0}|{1:0.#}|{2:0.#}|{3:0.#}|{4:0.#}",
                    name, c1, c2, c3, DefaultMaxDistance);
                AppendReferenceLine(line);
                SetStatus($"已追加参考颜色 {name}（{SelectedColorSpace} {c1:0.#} / {c2:0.#} / {c3:0.#}，允许距离 {DefaultMaxDistance:0.#}）。");
            }
            catch (HalconException ex)
            {
                ShowError("取样本均值失败：" + ex.Message);
            }
            finally
            {
                region?.Dispose();
                // 采样完成后移除临时 ROI，避免与样本框混淆
                _syncingList = true;
                try
                {
                    PreviewImageView.Rois.RemoveActive();
                }
                finally
                {
                    _syncingList = false;
                }
            }
        }

        private void AppendReferenceLine(string line)
        {
            string text = (ReferencesText.Text ?? string.Empty).TrimEnd('\r', '\n');
            _syncingList = true;
            try
            {
                ReferencesText.Text = text.Length == 0 ? line : text + Environment.NewLine + line;
            }
            finally
            {
                _syncingList = false;
            }
            RefreshLegend();
        }

        // ======================= 参考颜色图例 =======================

        private System.Collections.Generic.List<ReferenceRow> ParseReferences()
        {
            var rows = new System.Collections.Generic.List<ReferenceRow>();
            string text = ReferencesText.Text ?? string.Empty;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }
                string[] parts = line.Split('|');
                if (parts.Length < 4)
                {
                    continue;
                }
                if (!TryParse(parts[1], out double c1) || !TryParse(parts[2], out double c2) || !TryParse(parts[3], out double c3))
                {
                    continue;
                }
                rows.Add(new ReferenceRow { Name = parts[0].Trim(), C1 = c1, C2 = c2, C3 = c3 });
            }
            return rows;
        }

        /// <summary>把参考颜色（所选色彩空间）换算回 RGB 显示代表色块；无法换算的行跳过。</summary>
        private void RefreshLegend()
        {
            ReferenceLegend.Children.Clear();
            ColorClassifySpace space = SelectedColorSpace;
            foreach (ReferenceRow row in ParseReferences())
            {
                Brush brush = null;
                try
                {
                    byte r, g, b;
                    if (space == ColorClassifySpace.Rgb)
                    {
                        r = ToByte(row.C1);
                        g = ToByte(row.C2);
                        b = ToByte(row.C3);
                    }
                    else
                    {
                        HOperatorSet.GenImageConst(out HObject c1, "byte", 1, 1);
                        HOperatorSet.GenImageConst(out HObject c2, "byte", 1, 1);
                        HOperatorSet.GenImageConst(out HObject c3, "byte", 1, 1);
                        HObject r1 = null, r2 = null, r3 = null;
                        try
                        {
                            HOperatorSet.SetGrayval(c1, 0, 0, row.C1);
                            HOperatorSet.SetGrayval(c2, 0, 0, row.C2);
                            HOperatorSet.SetGrayval(c3, 0, 0, row.C3);
                            HOperatorSet.TransToRgb(c1, c2, c3, out r1, out r2, out r3, space == ColorClassifySpace.Hsv ? "hsv" : "cielab");
                            r = GetByte(r1);
                            g = GetByte(r2);
                            b = GetByte(r3);
                        }
                        finally
                        {
                            c1.Dispose();
                            c2.Dispose();
                            c3.Dispose();
                            r1?.Dispose();
                            r2?.Dispose();
                            r3?.Dispose();
                        }
                    }
                    brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                }
                catch (HalconException)
                {
                    // 色彩空间取值超出可换算范围时该行不显示色块
                }
                var item = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
                item.Children.Add(new Border
                {
                    Width = 36,
                    Height = 16,
                    CornerRadius = new CornerRadius(2),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(SystemColors.GrayTextColor),
                    Background = brush ?? Brushes.Transparent
                });
                item.Children.Add(new TextBlock { Text = row.Name, FontSize = 11 });
                ReferenceLegend.Children.Add(item);
            }
        }

        private static byte GetByte(HObject image)
        {
            HOperatorSet.GetGrayval(image, 0, 0, out HTuple gray);
            return gray.Length > 0 ? ToByte(gray[0].D) : (byte)0;
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
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
                run = ToolTestRun.Run(_tool, copy => ApplyTo((ColorClassifyTool)copy), _context.LastRunContext, _context.InputImage);
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
                var labels = ctx.GetVariable(module, "Labels").GetValue<string[]>();
                var distances = ctx.GetVariable(module, "Distances").GetValue<double[]>();
                HalconImage[] meanColors = ctx.GetVariable(module, "MeanColors").GetValue<HalconImage[]>();
                bool allKnown = ctx.GetVariable(module, "AllKnown").GetValue<bool>();
                for (int i = 0; i < labels.Length; i++)
                {
                    Brush brush = Brushes.Transparent;
                    if (i < meanColors.Length)
                    {
                        HObject ch1 = null, ch2 = null, ch3 = null, rr = null, gg = null, bb = null;
                        try
                        {
                            // MeanColors 为所选色彩空间的通道值，非 RGB 时先转回 RGB 再取色，
                            // 否则色块颜色与实际颜色不符（如默认 Cielab 下直接当 RGB 显示是错的）。
                            HOperatorSet.Decompose3(meanColors[i].Object, out ch1, out ch2, out ch3);
                            HObject rCh = ch1, gCh = ch2, bCh = ch3;
                            if (SelectedColorSpace != ColorClassifySpace.Rgb)
                            {
                                HOperatorSet.TransToRgb(ch1, ch2, ch3, out rr, out gg, out bb,
                                    SelectedColorSpace == ColorClassifySpace.Hsv ? "hsv" : "cielab");
                                rCh = rr; gCh = gg; bCh = bb;
                            }
                            HOperatorSet.GetGrayval(rCh, 0, 0, out HTuple rv);
                            HOperatorSet.GetGrayval(gCh, 0, 0, out HTuple gv);
                            HOperatorSet.GetGrayval(bCh, 0, 0, out HTuple bv);
                            brush = new SolidColorBrush(Color.FromRgb(ToByte(rv[0].D), ToByte(gv[0].D), ToByte(bv[0].D)));
                        }
                        catch (HalconException)
                        {
                            // 均值色读取失败时该行不显示色块
                        }
                        finally
                        {
                            ch1?.Dispose(); ch2?.Dispose(); ch3?.Dispose();
                            rr?.Dispose(); gg?.Dispose(); bb?.Dispose();
                        }
                    }
                    ResultList.Items.Add(new ResultRow
                    {
                        Index = i + 1,
                        Label = labels[i],
                        Distance = i < distances.Length && !double.IsNaN(distances[i])
                            ? distances[i].ToString("0.#", CultureInfo.InvariantCulture)
                            : "-",
                        MeanBrush = brush
                    });
                }

                // 叠加：引用的区域（原图坐标）
                HObject overlay = ResolveRegion(ctx);
                _overlay = overlay;
                ShowSourceImage();
                SummaryText.Text = $"区域数 {labels.Length}，全部识别为已知颜色：{allKnown}";
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

        /// <summary>在预览上下文中解析“区域”引用，作为叠加显示；解析失败返回 null。</summary>
        private HObject ResolveRegion(FlowContext ctx)
        {
            string path = (RegionPathCombo.Text ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                return null;
            }
            try
            {
                HObject region = (VariableReference.Parse(path).Resolve(ctx) as HalconRegion)?.Object;
                return region != null && region.IsInitialized() ? region.CopyObj(1, -1) : null;
            }
            catch (Exception)
            {
                return null;
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

        private static bool TryParse(string text, out double value)
        {
            return double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string NullIfEmpty(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
