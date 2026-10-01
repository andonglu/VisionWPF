using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using HalconDotNet;
using VisionFlow.Controls.Roi;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfManualRegionToolEditWindow : Window
    {
        private const string TeachBaseText = "基准图像";
        private const string TeachCurrentText = "当前图像";

        private readonly ManualRegionTool _tool;
        private readonly ToolEditContext _context;
        /// <summary>编辑区 ROI 当前所处的坐标：null 为示教基准坐标，否则为按此姿态变换后的当前图像坐标。</summary>
        private HomMat2D _displayPose;
        private bool _loading = true;

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
            FillMatrixCandidates();
            TeachModeCombo.Items.Add(TeachBaseText);
            TeachModeCombo.Items.Add(TeachCurrentText);
            TeachModeCombo.SelectedItem = TeachBaseText;
            TeachPosePathCombo.Text = _tool.MatrixPath ?? string.Empty;

            LoadImage();
            LoadRois();
            _loading = false;
            PreviewRegion();
        }

        private void FillMatrixCandidates()
        {
            MatrixPathCombo.Items.Add(string.Empty);
            TeachPosePathCombo.Items.Add(string.Empty);
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForNode(_context.Root, _context.Node))
                {
                    if (!typeof(HomMat2D).IsAssignableFrom(candidate.ClrType))
                    {
                        continue;
                    }
                    if (!candidate.IsCollection)
                    {
                        MatrixPathCombo.Items.Add(candidate.Path);
                        TeachPosePathCombo.Items.Add(candidate.Path);
                        continue;
                    }
                    int count = ResolveCollectionCount(candidate.Path);
                    for (int i = 0; i < count; i++)
                    {
                        TeachPosePathCombo.Items.Add(candidate.Path + "[" + i + "]");
                    }
                }
            }
            MatrixPathCombo.Text = _tool.MatrixPath ?? string.Empty;
        }

        private int ResolveCollectionCount(string path)
        {
            try
            {
                return _context.LastRunContext != null
                    && VariableReference.Parse(path).Resolve(_context.LastRunContext) is System.Collections.ICollection collection
                    ? collection.Count
                    : 0;
            }
            catch (Exception ex) when (ToolEditTransaction.IsConfigurationException(ex) || ex is KeyNotFoundException)
            {
                return 0;
            }
        }

        private HomMat2D ResolvePose(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || _context.LastRunContext == null)
            {
                return null;
            }
            try
            {
                return VariableReference.Parse(path).Resolve(_context.LastRunContext) as HomMat2D;
            }
            catch (Exception ex) when (ToolEditTransaction.IsConfigurationException(ex) || ex is KeyNotFoundException)
            {
                return null;
            }
        }

        private void TeachMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RefreshDisplayPose();
        }

        private void TeachPose_LostFocus(object sender, RoutedEventArgs e)
        {
            RefreshDisplayPose();
        }

        /// <summary>按示教模式切换 ROI 显示坐标：先换算回基准，再按新姿态变换显示。</summary>
        private void RefreshDisplayPose()
        {
            if (_loading)
            {
                return;
            }
            HomMat2D pose = null;
            if (Equals(TeachModeCombo.SelectedItem, TeachCurrentText))
            {
                string path = (TeachPosePathCombo.SelectedItem as string) ?? TeachPosePathCombo.Text;
                pose = ResolvePose(path?.Trim());
                if (pose == null)
                {
                    StatusText.Text = "当前图像示教需要选择可解析的当前姿态（请先运行流程）；ROI 仍按基准坐标显示。";
                }
            }
            if (ReferenceEquals(pose, _displayPose))
            {
                return;
            }
            try
            {
                List<ManualRegionShape> shapes = ToBaseShapes();
                if (pose != null)
                {
                    shapes = shapes.Select(s => s.Transform(pose)).ToList();
                }
                ShowShapes(shapes);
                _displayPose = pose;
                PreviewRegion();
                if (pose != null)
                {
                    StatusText.Text = "ROI 按当前姿态显示；确定时将用逆矩阵换算回示教基准保存。";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "姿态换算失败：" + ex.Message;
            }
        }

        /// <summary>编辑区 ROI 换算回示教基准坐标。</summary>
        private List<ManualRegionShape> ToBaseShapes()
        {
            List<ManualRegionShape> shapes = RoiEditor.Rois.Items.Select(FromRoi).ToList();
            if (_displayPose == null)
            {
                return shapes;
            }
            HomMat2D inverse = _displayPose.Inverted();
            return shapes.Select(s => s.Transform(inverse)).ToList();
        }

        private void ShowShapes(IEnumerable<ManualRegionShape> shapes)
        {
            RoiEditor.Rois.Clear();
            foreach (ManualRegionShape shape in shapes)
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
            definition.Items.AddRange(ToBaseShapes());
            return definition;
        }

        private void PreviewRegion_Click(object sender, RoutedEventArgs e)
        {
            PreviewRegion();
        }

        /// <summary>以选中 ROI 为第 1 行第 1 列，按行列间距生成阵列并替换选中 ROI（其余 ROI 保持原顺序）。</summary>
        private void GenerateArray_Click(object sender, RoutedEventArgs e)
        {
            RoiShape active = RoiEditor.Rois.ActiveRoi;
            if (active == null)
            {
                StatusText.Text = "请先选中一个 ROI 作为阵列的第 1 行第 1 列。";
                return;
            }
            int rows = (int)Math.Round(ArrayRowsText.Value);
            int columns = (int)Math.Round(ArrayColumnsText.Value);
            if (rows * columns <= 1)
            {
                StatusText.Text = "阵列行数 × 列数需大于 1。";
                return;
            }
            if ((rows > 1 && ArrayRowPitchText.Value == 0) || (columns > 1 && ArrayColumnPitchText.Value == 0))
            {
                StatusText.Text = "多行/多列时行距、列距不能为 0。";
                return;
            }

            try
            {
                List<ManualRegionShape> generated = ManualRegionArray.Generate(FromRoi(active), rows, columns,
                    ArrayRowPitchText.Value, ArrayColumnPitchText.Value);
                List<RoiShape> items = RoiEditor.Rois.Items.ToList();
                int activeIndex = items.IndexOf(active);
                items.RemoveAt(activeIndex);
                items.InsertRange(activeIndex, generated.Select(shape =>
                {
                    RoiShape roi = ToRoi(shape);
                    ApplyPolarity(roi, shape.Polarity);
                    return roi;
                }));
                RoiEditor.Rois.Clear();
                foreach (RoiShape roi in items)
                {
                    RoiEditor.Rois.Add(roi);
                }
                PreviewRegion();
                StatusText.Text = $"已生成 {rows}×{columns} 阵列，共 {generated.Count} 个 ROI。分区检测请选择输出模式 PerShape。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "阵列生成失败：" + ex.Message;
            }
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
                _tool.MatrixPath = (MatrixPathCombo.Text ?? string.Empty).Trim();
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
