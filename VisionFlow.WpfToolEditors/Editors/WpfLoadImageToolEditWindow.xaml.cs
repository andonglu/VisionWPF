using System;
using System.IO;
using System.Windows;
using HalconDotNet;
using Microsoft.Win32;
using VisionFlow.Tools;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfLoadImageToolEditWindow : Window
    {
        private readonly LoadImageTool _tool;

        public WpfLoadImageToolEditWindow(LoadImageTool tool)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            InitializeComponent();
            Title = "图像加载 - " + tool.ModuleName;
            ModuleNameText.Text = tool.ModuleName;
            FilePathText.Text = tool.FilePath ?? string.Empty;
            PreviewImage();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) == true)
            {
                FilePathText.Text = dialog.FileName;
                PreviewImage();
            }
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            PreviewImage();
        }

        private void PreviewImage()
        {
            string path = (FilePathText.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                StatusText.Text = "请选择有效图像文件。";
                return;
            }

            HOperatorSet.ReadImage(out HObject image, path);
            try
            {
                PreviewImageView.ShowImage(image);
                StatusText.Text = "已预览：" + Path.GetFileName(path);
            }
            finally
            {
                image.Dispose();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _tool.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            _tool.FilePath = (FilePathText.Text ?? string.Empty).Trim();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
