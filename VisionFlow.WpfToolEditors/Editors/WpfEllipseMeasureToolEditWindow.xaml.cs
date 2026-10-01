using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfEllipseMeasureToolEditWindow : Window
    {
        private readonly EllipseFollowMeasureTool _tool;
        private readonly ToolEditContext _context;

        public WpfEllipseMeasureToolEditWindow(EllipseFollowMeasureTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "椭圆测量 - " + tool.ModuleName;
            InitializeFields();
        }

        private void InitializeFields()
        {
            ModuleNameText.Text = _tool.ModuleName;
            FillRefs(ImagePathCombo, typeof(HalconImage), _tool.ImagePath, optional: false);
            FillRefs(MatrixPathCombo, typeof(HomMat2D), _tool.MatrixPath, optional: true, acceptsCollection: true);
            FillRefs(IndexPathCombo, typeof(int), _tool.IndexPath, optional: true);
            EllipseRowText.Text = Format(_tool.EllipseRow);
            EllipseColumnText.Text = Format(_tool.EllipseColumn);
            EllipseAngleText.Text = Format(_tool.EllipseAngle);
            EllipseLength1Text.Text = Format(_tool.EllipseLength1);
            EllipseLength2Text.Text = Format(_tool.EllipseLength2);
            MeasureLength1Text.Text = Format(_tool.MeasureLength1);
            MeasureSigmaText.Text = Format(_tool.MeasureSigma);
            MeasureThresholdText.Text = Format(_tool.MeasureThreshold);
            FailWhenNotFoundCheck.IsChecked = _tool.FailWhenNotFound;
        }

        private void FillRefs(ComboBox combo, Type expectedType, string current, bool optional, bool acceptsCollection = false)
        {
            if (optional)
            {
                combo.Items.Add(string.Empty);
            }
            if (expectedType == typeof(HalconImage) && _context.InputImage != null && _context.InputImage.IsInitialized())
            {
                combo.Items.Add("Input.Image");
            }
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, expectedType, acceptsCollection))
                {
                    combo.Items.Add(candidate.Path);
                }
            }
            combo.Text = current ?? string.Empty;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _tool.ModuleName = ModuleNameText.Text.Trim();
                _tool.ImagePath = ImagePathCombo.Text.Trim();
                _tool.MatrixPath = MatrixPathCombo.Text.Trim();
                _tool.IndexPath = IndexPathCombo.Text.Trim();
                _tool.EllipseRow = Parse(EllipseRowText.Text, nameof(_tool.EllipseRow));
                _tool.EllipseColumn = Parse(EllipseColumnText.Text, nameof(_tool.EllipseColumn));
                _tool.EllipseAngle = Parse(EllipseAngleText.Text, nameof(_tool.EllipseAngle));
                _tool.EllipseLength1 = Parse(EllipseLength1Text.Text, nameof(_tool.EllipseLength1));
                _tool.EllipseLength2 = Parse(EllipseLength2Text.Text, nameof(_tool.EllipseLength2));
                _tool.MeasureLength1 = Parse(MeasureLength1Text.Text, nameof(_tool.MeasureLength1));
                _tool.MeasureSigma = Parse(MeasureSigmaText.Text, nameof(_tool.MeasureSigma));
                _tool.MeasureThreshold = Parse(MeasureThresholdText.Text, nameof(_tool.MeasureThreshold));
                _tool.FailWhenNotFound = FailWhenNotFoundCheck.IsChecked == true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = "保存失败：" + ex.Message;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string Format(double value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static double Parse(string text, string name)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                throw new InvalidOperationException(name + " 不是有效数字。");
            }
            return value;
        }
    }
}
