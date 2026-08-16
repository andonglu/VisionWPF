using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using Microsoft.Win32;
using VisionFlow.Controls.Roi;

namespace VisionFlow.WpfToolEditors.Controls
{
    public partial class WpfRoiEditorControl : UserControl
    {
        private bool _updatingList;

        public WpfRoiEditorControl()
        {
            InitializeComponent();
            ImageView.RoiChanged += (s, e) => RefreshRoiList();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public RoiCollection Rois
        {
            get { return ImageView.Rois; }
        }

        public void ShowImage(HObject image)
        {
            ImageView.ShowImage(image);
        }

        public void BeginAddRoi(RoiKind kind)
        {
            ImageView.BeginAddRoi(kind);
        }

        public void SetOverlay(HObject overlay)
        {
            ImageView.SetOverlay(overlay);
        }

        public void ClearOverlay()
        {
            ImageView.ClearOverlay();
        }

        public HRegion BuildRegion()
        {
            return ImageView.BuildRegion();
        }

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true || !File.Exists(dialog.FileName))
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

        private void Fit_Click(object sender, RoutedEventArgs e)
        {
            ImageView.FitToWindow();
        }

        private void Rectangle_Click(object sender, RoutedEventArgs e)
        {
            ImageView.BeginAddRoi(RoiKind.Rectangle1);
        }

        private void Rectangle2_Click(object sender, RoutedEventArgs e)
        {
            ImageView.BeginAddRoi(RoiKind.Rectangle2);
        }

        private void Circle_Click(object sender, RoutedEventArgs e)
        {
            ImageView.BeginAddRoi(RoiKind.Circle);
        }

        private void Line_Click(object sender, RoutedEventArgs e)
        {
            ImageView.BeginAddRoi(RoiKind.Line);
        }

        private void Include_Click(object sender, RoutedEventArgs e)
        {
            SetActivePolarity(RoiPolarity.Include);
        }

        private void Exclude_Click(object sender, RoutedEventArgs e)
        {
            SetActivePolarity(RoiPolarity.Exclude);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            ImageView.Rois.RemoveActive();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            ImageView.Rois.Clear();
        }

        private void SaveRegion_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "HALCON 对象|*.hobj|所有文件|*.*",
                DefaultExt = "hobj"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            using HRegion region = BuildRegion();
            HOperatorSet.WriteObject(region, dialog.FileName);
        }

        private void OpenRegion_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "HALCON 对象|*.hobj|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true || !File.Exists(dialog.FileName))
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

        private void ClearOverlay_Click(object sender, RoutedEventArgs e)
        {
            ImageView.ClearOverlay();
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

        private void RoiList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingList)
            {
                return;
            }

            ImageView.Rois.SetActive(RoiList.SelectedIndex);
            ImageView.Redraw();
        }

        private void RefreshRoiList()
        {
            _updatingList = true;
            try
            {
                RoiList.Items.Clear();
                foreach (RoiShape roi in ImageView.Rois.Items)
                {
                    RoiList.Items.Add($"{roi.Name} [{roi.Kind}] {(roi.Polarity == RoiPolarity.Include ? "包含" : "排除")}");
                }
                if (ImageView.Rois.ActiveIndex >= 0 && ImageView.Rois.ActiveIndex < RoiList.Items.Count)
                {
                    RoiList.SelectedIndex = ImageView.Rois.ActiveIndex;
                }
            }
            finally
            {
                _updatingList = false;
            }
        }
    }
}
