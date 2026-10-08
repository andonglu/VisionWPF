using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    /// 极坐标展开编辑窗口（IP-05）：原图上用两个 CircleRoi（内、外半径，圆心同步）拖动圆心与内外半径，下方实时显示展开图；
    /// 右侧为输入引用与展开参数。预览经 ToolTestRun 在一次性副本上运行，只有“确定”写回工具。
    /// </summary>
    public partial class WpfPolarUnwrapToolEditWindow : Window
    {
        private readonly PolarUnwrapTool _tool;
        private readonly ToolEditContext _context;
        private readonly DispatcherTimer _previewTimer;
        private CircleRoi _inner;
        private CircleRoi _outer;
        private HObject _polar;
        private bool _syncing;

        public WpfPolarUnwrapToolEditWindow(PolarUnwrapTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "极坐标展开 - " + tool.ModuleName;
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _previewTimer.Tick += (s, e) =>
            {
                _previewTimer.Stop();
                RunPreview("预览");
            };
            InitializeOptions();
            LoadFromTool();
            SourceImageView.RoiChanged += OnRoiChanged;
            Loaded += (s, e) =>
            {
                ShowSourceImage();
                if (_context.LastRunContext != null || _context.InputImage != null)
                {
                    SchedulePreview();
                }
            };
            Closed += (s, e) =>
            {
                _previewTimer.Stop();
                SourceImageView.RoiChanged -= OnRoiChanged;
                _polar?.Dispose();
                _polar = null;
            };
        }

        // ======================= 参数区 =======================

        private void InitializeOptions()
        {
            InterpolationCombo.ItemsSource = Enum.GetNames(typeof(PolarInterpolation));
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ImagePathCombo.Items.Add("Input.Image");
            }
            CenterRowPathCombo.Items.Add(string.Empty);
            CenterColumnPathCombo.Items.Add(string.Empty);
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
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(double)))
                {
                    CenterRowPathCombo.Items.Add(candidate.Path);
                    CenterColumnPathCombo.Items.Add(candidate.Path);
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
            CenterRowPathCombo.Text = _tool.CenterRowPath ?? string.Empty;
            CenterColumnPathCombo.Text = _tool.CenterColumnPath ?? string.Empty;
            MatrixPathCombo.Text = _tool.MatrixPath ?? string.Empty;
            CenterRowText.Text = Format(_tool.CenterRow);
            CenterColumnText.Text = Format(_tool.CenterColumn);
            AngleStartDegText.Text = Format(_tool.AngleStartDeg);
            AngleEndDegText.Text = Format(_tool.AngleEndDeg);
            RadiusStartText.Text = Format(_tool.RadiusStart);
            RadiusEndText.Text = Format(_tool.RadiusEnd);
            OutputWidthText.Text = _tool.OutputWidth.ToString(CultureInfo.InvariantCulture);
            OutputHeightText.Text = _tool.OutputHeight.ToString(CultureInfo.InvariantCulture);
            InterpolationCombo.SelectedItem = _tool.Interpolation.ToString();
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，预览时为一次性副本）；数值无效时抛出说明参数名的异常。</summary>
        private void ApplyTo(PolarUnwrapTool target)
        {
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.ImagePath = NullIfEmpty(ImagePathCombo.Text);
            target.CenterRowPath = NullIfEmpty(CenterRowPathCombo.Text);
            target.CenterColumnPath = NullIfEmpty(CenterColumnPathCombo.Text);
            target.MatrixPath = NullIfEmpty(MatrixPathCombo.Text);
            target.CenterRow = ParseDouble(CenterRowText, "CenterRow");
            target.CenterColumn = ParseDouble(CenterColumnText, "CenterColumn");
            target.AngleStartDeg = ParseDouble(AngleStartDegText, "AngleStartDeg");
            target.AngleEndDeg = ParseDouble(AngleEndDegText, "AngleEndDeg");
            target.RadiusStart = ParseDouble(RadiusStartText, "RadiusStart");
            target.RadiusEnd = ParseDouble(RadiusEndText, "RadiusEnd");
            target.OutputWidth = ParseInt(OutputWidthText, "OutputWidth");
            target.OutputHeight = ParseInt(OutputHeightText, "OutputHeight");
            target.Interpolation = InterpolationCombo.SelectedItem is string name
                ? (PolarInterpolation)Enum.Parse(typeof(PolarInterpolation), name)
                : PolarInterpolation.nearest_neighbor;
        }

        private void Field_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateRoisFromFields();
            SchedulePreview();
        }

        private void InterpolationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SchedulePreview();
        }

        private void ImagePathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowSourceImage();
                SchedulePreview();
            }));
        }

        // ======================= 原图与拖动 =======================

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
                SourceImageView.ClearImage();
                return;
            }
            SourceImageView.ShowImage(image);
            BuildRois();
        }

        /// <summary>按当前字段生成内、外两个圆（外圆后加入，拖动重合的圆心时命中外圆，再同步到内圆）。</summary>
        private void BuildRois()
        {
            if (!TryReadGeometry(out double row, out double column, out double radiusStart, out double radiusEnd))
            {
                return;
            }
            _syncing = true;
            try
            {
                SourceImageView.Rois.Clear();
                _inner = new CircleRoi("内半径", row, column, Math.Max(1, radiusStart)) { Color = "yellow" };
                _outer = new CircleRoi("外半径", row, column, Math.Max(1, radiusEnd)) { Color = "green" };
                SourceImageView.Rois.Add(_inner);
                SourceImageView.Rois.Add(_outer);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void UpdateRoisFromFields()
        {
            if (_inner == null || _outer == null || !TryReadGeometry(out double row, out double column, out double radiusStart, out double radiusEnd))
            {
                return;
            }
            _inner.Row = _outer.Row = row;
            _inner.Column = _outer.Column = column;
            _inner.Radius = Math.Max(1, radiusStart);
            _outer.Radius = Math.Max(1, radiusEnd);
            SourceImageView.Redraw();
        }

        private bool TryReadGeometry(out double row, out double column, out double radiusStart, out double radiusEnd)
        {
            radiusStart = radiusEnd = 0;
            column = 0;
            return TryParse(CenterRowText.Text, out row) && TryParse(CenterColumnText.Text, out column)
                && TryParse(RadiusStartText.Text, out radiusStart) && TryParse(RadiusEndText.Text, out radiusEnd);
        }

        /// <summary>拖动任一圆：圆心同步到另一个圆并写回固定圆心，半径写回 RadiusStart / RadiusEnd（内半径 ≤ 1 视为 0），随后延时预览。</summary>
        private void OnRoiChanged(object sender, RoiChangedEventArgs e)
        {
            if (_syncing || _inner == null || _outer == null || !(e.Roi is CircleRoi moved) || (moved != _inner && moved != _outer))
            {
                return;
            }
            _syncing = true;
            try
            {
                CircleRoi other = moved == _inner ? _outer : _inner;
                if (other.Row != moved.Row || other.Column != moved.Column)
                {
                    other.Row = moved.Row;
                    other.Column = moved.Column;
                    SourceImageView.Redraw();
                }
                CenterRowText.Text = Format(Math.Round(moved.Row, 2));
                CenterColumnText.Text = Format(Math.Round(moved.Column, 2));
                RadiusStartText.Text = Format(_inner.Radius <= 1 ? 0 : Math.Round(_inner.Radius, 2));
                RadiusEndText.Text = Format(Math.Round(_outer.Radius, 2));
            }
            finally
            {
                _syncing = false;
            }
            SchedulePreview();
        }

        // ======================= 预览、确定与取消 =======================

        private void SchedulePreview()
        {
            if (!IsLoaded)
            {
                return;
            }
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Stop();
            UpdateRoisFromFields();
            RunPreview("执行测试");
        }

        private void RunPreview(string what)
        {
            ToolTestRun run = null;
            try
            {
                run = ToolTestRun.Run(_tool, copy => ApplyTo((PolarUnwrapTool)copy), _context.LastRunContext, _context.InputImage);
                if (!run.Result.IsSuccess)
                {
                    SummaryText.Text = "运行失败：" + run.Result.Message;
                    PolarImageView.ClearImage();
                    SourceImageView.SetMarker(null, null);
                    SetStatus($"{what}：{run.Result.Message}");
                    return;
                }
                FlowContext ctx = run.Context;
                HObject polar = ((HalconImage)ctx.GetVariable(run.ModuleName, "Image").Value).Object;
                var parameters = (PolarParams)ctx.GetVariable(run.ModuleName, "PolarParams").Value;
                _polar?.Dispose();
                _polar = polar.CopyObj(1, -1);
                PolarImageView.ShowImage(_polar);
                SourceImageView.SetMarker(parameters.CenterRow, parameters.CenterColumn);
                SummaryText.Text = string.Format(CultureInfo.InvariantCulture,
                    "展开图 {0}×{1}；实际圆心 ({2}, {3})，角度 {4}° ~ {5}°，半径 {6} ~ {7}",
                    parameters.Width, parameters.Height, Format(Math.Round(parameters.CenterRow, 3)), Format(Math.Round(parameters.CenterColumn, 3)),
                    Format(Math.Round(parameters.AngleStartDeg, 3)), Format(Math.Round(parameters.AngleEndDeg, 3)),
                    Format(Math.Round(parameters.RadiusStart, 3)), Format(Math.Round(parameters.RadiusEnd, 3)));
                SetStatus($"{what}完成：{SummaryText.Text}");
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException || ex is HalconException || ex is ArgumentException)
            {
                SummaryText.Text = $"{what}失败：{ex.Message}";
                SetStatus(SummaryText.Text);
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
                MessageBox.Show(this, "参数保存失败：" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("参数保存失败：" + ex.Message);
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

        private static int ParseInt(TextBox box, string name)
        {
            if (!int.TryParse((box.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new FormatException($"参数 {name} 应为整数：{box.Text}");
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
