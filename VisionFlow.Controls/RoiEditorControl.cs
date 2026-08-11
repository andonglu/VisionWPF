using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Controls.Roi;

namespace VisionFlow.Controls
{
    public sealed class RoiEditorControl : UserControl
    {
        private readonly ToolStrip _toolStrip;
        private readonly SplitContainer _split;
        private readonly ListBox _roiList;
        private bool _updatingList;

        public HalconImageView ImageView { get; private set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public RoiCollection Rois => ImageView.Rois;

        public RoiEditorControl()
        {
            Dock = DockStyle.Fill;

            _toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            AddButton("打开图像", OnOpenImage);
            AddButton("适应", (s, e) => ImageView.FitToWindow());
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddButton("矩形", (s, e) => ImageView.BeginAddRoi(RoiKind.Rectangle1));
            AddButton("旋转矩形", (s, e) => ImageView.BeginAddRoi(RoiKind.Rectangle2));
            AddButton("圆", (s, e) => ImageView.BeginAddRoi(RoiKind.Circle));
            AddButton("线", (s, e) => ImageView.BeginAddRoi(RoiKind.Line));
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddButton("包含", (s, e) => SetActivePolarity(RoiPolarity.Include));
            AddButton("排除", (s, e) => SetActivePolarity(RoiPolarity.Exclude));
            AddButton("删除", (s, e) => ImageView.Rois.RemoveActive());
            AddButton("清空", (s, e) => ImageView.Rois.Clear());
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddButton("保存Region", OnSaveRegion);
            AddButton("打开Region", OnOpenRegion);
            AddButton("清除叠加", (s, e) => ImageView.ClearOverlay());
            Controls.Add(_toolStrip);

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
                SplitterDistance = Math.Max(Width - 180, 200)
            };
            ImageView = new HalconImageView { Dock = DockStyle.Fill };
            ImageView.RoiChanged += (s, e) => RefreshRoiList();
            _split.Panel1.Controls.Add(ImageView);

            _roiList = new ListBox { Dock = DockStyle.Fill };
            _roiList.SelectedIndexChanged += OnSelectedRoiChanged;
            _split.Panel2.Controls.Add(_roiList);
            Controls.Add(_split);
            _split.BringToFront();
            _toolStrip.BringToFront();
        }

        public void ShowImage(HObject image)
        {
            ImageView.ShowImage(image);
        }

        public HRegion BuildRegion()
        {
            return ImageView.BuildRegion();
        }

        private void AddButton(string text, EventHandler click)
        {
            var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
            button.Click += click;
            _toolStrip.Items.Add(button);
        }

        private void OnOpenImage(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK || !File.Exists(dialog.FileName))
            {
                return;
            }

            HOperatorSet.ReadImage(out HObject image, dialog.FileName);
            try
            {
                ImageView.ShowImage(image);
            }
            finally
            {
                image.Dispose();
            }
        }

        private void OnSaveRegion(object sender, EventArgs e)
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "HALCON 对象|*.hobj|所有文件|*.*",
                DefaultExt = "hobj"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            using HRegion region = BuildRegion();
            HOperatorSet.WriteObject(region, dialog.FileName);
        }

        private void OnOpenRegion(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "HALCON 对象|*.hobj|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK || !File.Exists(dialog.FileName))
            {
                return;
            }

            HOperatorSet.ReadObject(out HObject region, dialog.FileName);
            try
            {
                ImageView.SetOverlay(region);
            }
            finally
            {
                region.Dispose();
            }
        }

        private void SetActivePolarity(RoiPolarity polarity)
        {
            RoiShape active = ImageView.Rois.ActiveRoi;
            if (active == null)
            {
                return;
            }

            active.Polarity = polarity;
            active.Color = polarity == RoiPolarity.Include ? "green" : "yellow";
            ImageView.Rois.NotifyActiveChanged();
        }

        private void OnSelectedRoiChanged(object sender, EventArgs e)
        {
            if (_updatingList)
            {
                return;
            }

            ImageView.Rois.SetActive(_roiList.SelectedIndex);
            ImageView.Redraw();
        }

        private void RefreshRoiList()
        {
            _updatingList = true;
            try
            {
                _roiList.Items.Clear();
                foreach (RoiShape roi in ImageView.Rois.Items)
                {
                    _roiList.Items.Add($"{roi.Name} [{roi.Kind}] {(roi.Polarity == RoiPolarity.Include ? "包含" : "排除")}");
                }
                if (ImageView.Rois.ActiveIndex >= 0 && ImageView.Rois.ActiveIndex < _roiList.Items.Count)
                {
                    _roiList.SelectedIndex = ImageView.Rois.ActiveIndex;
                }
            }
            finally
            {
                _updatingList = false;
            }
        }
    }
}
