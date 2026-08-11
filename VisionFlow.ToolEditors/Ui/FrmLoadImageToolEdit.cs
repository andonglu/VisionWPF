using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Controls;
using VisionFlow.Tools;

namespace VisionFlow.Ui
{
    public sealed class FrmLoadImageToolEdit : Form
    {
        private readonly LoadImageTool _tool;
        private readonly TextBox _moduleName;
        private readonly TextBox _filePath;
        private readonly HalconImageView _preview;

        public FrmLoadImageToolEdit(LoadImageTool tool)
        {
            _tool = tool;
            Text = $"图像加载 - {tool.ModuleName}";
            Width = 900;
            Height = 620;
            StartPosition = FormStartPosition.CenterParent;

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(8)
            };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(main);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            main.Controls.Add(top, 0, 0);

            top.Controls.Add(new Label { Text = "模块名", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
            _moduleName = new TextBox { Text = tool.ModuleName, Dock = DockStyle.Fill };
            top.Controls.Add(_moduleName, 1, 0);
            top.Controls.Add(new Label { Text = "图像文件", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 2, 0);
            _filePath = new TextBox { Text = tool.FilePath, Dock = DockStyle.Fill };
            top.Controls.Add(_filePath, 3, 0);
            var browse = new Button { Text = "...", Dock = DockStyle.Fill };
            browse.Click += (s, e) => BrowseImage();
            top.Controls.Add(browse, 4, 0);

            _preview = new HalconImageView { Dock = DockStyle.Fill };
            main.Controls.Add(_preview, 0, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90 };
            var show = new Button { Text = "预览", Width = 90 };
            show.Click += (s, e) => PreviewImage();
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(show);
            main.Controls.Add(buttons, 0, 2);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PreviewImage();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                _tool.ModuleName = _moduleName.Text.Trim();
                _tool.FilePath = _filePath.Text.Trim();
            }
            base.OnClosing(e);
        }

        private void BrowseImage()
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            _filePath.Text = dialog.FileName;
            PreviewImage();
        }

        private void PreviewImage()
        {
            string path = _filePath.Text.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            HOperatorSet.ReadImage(out HObject image, path);
            try
            {
                _preview.ShowImage(image);
            }
            finally
            {
                image.Dispose();
            }
        }
    }
}
