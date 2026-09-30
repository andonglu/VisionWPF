using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using HalconDotNet;
using VisionFlow.Controls.Roi;
using VisionFlow.Core;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfManualRegionToolEditWindow : Window
    {
        private readonly ManualRegionTool _tool;
        private readonly ToolEditContext _context;

        public WpfManualRegionToolEditWindow(ManualRegionTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();

            InitializeComponent();
            ModuleNameText.Text = _tool.ModuleName;
            foreach (string name in Enum.GetNames(typeof(ManualRegionOutputMode)))
            {
                OutputModeCombo.Items.Add(name);
            }
            OutputModeCombo.Text = _tool.OutputMode.ToString();

            LoadImage();
            LoadRois();
            PreviewRegion();
        }

        private void LoadImage()
        {
            HObject image = _context.InputImage;
            if ((image == null || !image.IsInitialized()) && _context.LastRunContext != null)
            {
                image = _context.LastRunContext.GetAllVariables()
                    .Select(v => v.Value)
                    .OfType<HalconImage>()
                    .Select(i => i.Object)
                    .FirstOrDefault(i => i != null && i.IsInitialized());
            }

            if (image != null && image.IsInitialized())
            {
                RoiEditor.ShowImage(image);
            }
        }

        private void LoadRois()
        {
            ManualRegionDefinition definition;
            try
            {
                definition = ManualRegionSerializer.Deserialize(_tool.RoiJson);
            }
            catch (Exception ex)
            {
                StatusText.Text = "ROI 数据解析失败：" + ex.Message;
                return;
            }

            RoiEditor.Rois.Clear();
            foreach (ManualRegionShape shape in definition.Items)
            {
                RoiShape roi = ToRoi(shape);
                if (roi == null)
                {
                    continue;
                }
                ApplyPolarity(roi, shape.Polarity);
                RoiEditor.Rois.Add(roi);
            }
        }

        private static RoiShape ToRoi(ManualRegionShape shape)
        {
            string name = string.IsNullOrWhiteSpace(shape.Name) ? shape.Kind.ToString() : shape.Name;
            switch (shape.Kind)
            {
                case ManualRegionShapeKind.Rectangle1:
                    return new Rectangle1Roi(name, shape.Row1, shape.Column1, shape.Row2, shape.Column2);
                case ManualRegionShapeKind.Rectangle2:
                    return new Rectangle2Roi(name, shape.Row, shape.Column, shape.Phi, shape.Length1, shape.Length2);
                case ManualRegionShapeKind.Circle:
                    return new CircleRoi(name, shape.Row, shape.Column, shape.Radius);
                case ManualRegionShapeKind.Line:
                    return new LineRoi(name, shape.Row1, shape.Column1, shape.Row2, shape.Column2);
                default:
                    return null;
            }
        }

        private static ManualRegionShape FromRoi(RoiShape roi)
        {
            var shape = new ManualRegionShape
            {
                Name = roi.Name,
                Polarity = roi.Polarity == RoiPolarity.Exclude ? ManualRegionPolarity.Exclude : ManualRegionPolarity.Include
            };

            if (roi is Rectangle1Roi rectangle1)
            {
                shape.Kind = ManualRegionShapeKind.Rectangle1;
                shape.Row1 = rectangle1.Row1;
                shape.Column1 = rectangle1.Column1;
                shape.Row2 = rectangle1.Row2;
                shape.Column2 = rectangle1.Column2;
            }
            else if (roi is Rectangle2Roi rectangle2)
            {
                shape.Kind = ManualRegionShapeKind.Rectangle2;
                shape.Row = rectangle2.Row;
                shape.Column = rectangle2.Column;
                shape.Phi = rectangle2.Phi;
                shape.Length1 = rectangle2.Length1;
                shape.Length2 = rectangle2.Length2;
            }
            else if (roi is CircleRoi circle)
            {
                shape.Kind = ManualRegionShapeKind.Circle;
                shape.Row = circle.Row;
                shape.Column = circle.Column;
                shape.Radius = circle.Radius;
            }
            else if (roi is LineRoi line)
            {
                shape.Kind = ManualRegionShapeKind.Line;
                shape.Row1 = line.Row1;
                shape.Column1 = line.Column1;
                shape.Row2 = line.Row2;
                shape.Column2 = line.Column2;
            }
            else
            {
                throw new NotSupportedException("不支持的 ROI 类型：" + roi.GetType().Name);
            }

            return shape;
        }

        private static void ApplyPolarity(RoiShape roi, ManualRegionPolarity polarity)
        {
            roi.Polarity = polarity == ManualRegionPolarity.Exclude ? RoiPolarity.Exclude : RoiPolarity.Include;
            roi.Color = roi.Polarity == RoiPolarity.Exclude ? "yellow" : "green";
        }

        private ManualRegionDefinition BuildDefinition()
        {
            var definition = new ManualRegionDefinition();
            foreach (RoiShape roi in RoiEditor.Rois.Items)
            {
                definition.Items.Add(FromRoi(roi));
            }
            return definition;
        }

        private void PreviewRegion_Click(object sender, RoutedEventArgs e)
        {
            PreviewRegion();
        }

        private void PreviewRegion()
        {
            try
            {
                using HRegion region = RoiEditor.BuildRegion();
                HOperatorSet.CountObj(region, out HTuple count);
                RoiEditor.SetOverlay(region);
                StatusText.Text = "Region 预览完成，区域数=" + count.I.ToString(CultureInfo.CurrentCulture);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Region 预览失败：" + ex.Message;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _tool.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
                _tool.OutputMode = (ManualRegionOutputMode)Enum.Parse(typeof(ManualRegionOutputMode), OutputModeCombo.Text);
                _tool.RoiJson = ManualRegionSerializer.Serialize(BuildDefinition());
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "参数保存失败：" + ex.Message, "手动 Region", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
