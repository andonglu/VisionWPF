using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using Microsoft.Win32;
using VisionFlow.Tools;
using VisionFlow.WpfToolEditors.Services;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfLoadImageToolEditWindow : Window
    {
        private readonly LoadImageTool _tool;
        private readonly HalconExampleImageStore _halconStore = new HalconExampleImageStore();

        public WpfLoadImageToolEditWindow(LoadImageTool tool)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            InitializeComponent();
            Title = "图像加载 - " + tool.ModuleName;
            ModuleNameText.Text = tool.ModuleName;
            FilePathText.Text = tool.FilePath ?? string.Empty;
            RefreshRecentButtons();
            PreviewImage();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            TryInitDialogDirectory(dialog);
            if (dialog.ShowDialog(this) == true)
            {
                FilePathText.Text = LoadImageTool.ToStoredFilePath(dialog.FileName);
                TryRecordRecentSelection(FilePathText.Text);
                RefreshRecentButtons();
                PreviewImage();
            }
        }

        private void BrowseHalconExample_Click(object sender, RoutedEventArgs e)
        {
            if (!LoadImageTool.TryGetHalconImagesRoot(out _))
            {
                StatusText.Text = "未找到 HALCON 示例图像目录，请先配置 HALCONIMAGES 环境变量。";
                return;
            }

            var dialog = new WpfHalconExamplePickerWindow(FilePathText.Text)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                FilePathText.Text = dialog.SelectedStoredPath ?? string.Empty;
                RefreshRecentButtons();
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
            if (!LoadImageTool.TryResolveExistingFilePath(path, out string resolvedPath, out string error))
            {
                StatusText.Text = string.IsNullOrWhiteSpace(path) ? "请选择有效图像文件。" : error;
                return;
            }

            HOperatorSet.ReadImage(out HObject image, resolvedPath);
            try
            {
                PreviewImageView.ShowImage(image);
                StatusText.Text = string.Equals(path, resolvedPath, StringComparison.OrdinalIgnoreCase)
                    ? "已预览：" + Path.GetFileName(resolvedPath)
                    : "已预览：" + Path.GetFileName(resolvedPath) + "（由路径表达式解析）";
            }
            finally
            {
                image.Dispose();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _tool.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            _tool.FilePath = LoadImageTool.ToStoredFilePath(FilePathText.Text ?? string.Empty);
            TryRecordRecentSelection(_tool.FilePath);
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TryInitDialogDirectory(OpenFileDialog dialog)
        {
            string current = (FilePathText.Text ?? string.Empty).Trim();
            if (LoadImageTool.TryResolveExistingFilePath(current, out string resolvedPath, out _))
            {
                string directory = Path.GetDirectoryName(resolvedPath) ?? string.Empty;
                if (Directory.Exists(directory))
                {
                    dialog.InitialDirectory = directory;
                    return;
                }
            }

            if (LoadImageTool.TryGetHalconImagesRoot(out string imageRoot))
            {
                dialog.InitialDirectory = imageRoot;
            }
        }

        private void RefreshRecentButtons()
        {
            RecentFilesPanel.Children.Clear();
            HalconExampleImageSettings settings = _halconStore.Load();
            string[] displayPaths = GetDisplayStoredPaths(settings).Take(5).ToArray();
            ClearRecentButton.IsEnabled = settings.RecentFiles.Count > 0;
            if (displayPaths.Length == 0)
            {
                RecentFilesPanel.Children.Add(new TextBlock
                {
                    Text = "暂无 HALCON 最近图像",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = TryFindResource("TextFillColorSecondaryBrush") as System.Windows.Media.Brush
                });
                return;
            }

            foreach (string storedPath in displayPaths)
            {
                bool pinned = settings.PinnedFiles.Any(pathText => string.Equals(pathText, storedPath, StringComparison.OrdinalIgnoreCase));
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 8, 8)
                };
                var button = new Button
                {
                    Content = BuildRecentButtonLabel(storedPath, pinned),
                    MinHeight = 30,
                    ToolTip = storedPath,
                    Tag = storedPath,
                    Padding = new Thickness(10, 2, 10, 2)
                };
                button.Click += RecentButton_Click;
                panel.Children.Add(button);

                var pinButton = new Button
                {
                    Content = pinned ? "取消固定" : "固定",
                    MinHeight = 30,
                    Margin = new Thickness(6, 0, 0, 0),
                    Tag = storedPath,
                    Padding = new Thickness(8, 2, 8, 2)
                };
                pinButton.Click += PinRecentButton_Click;
                panel.Children.Add(pinButton);
                RecentFilesPanel.Children.Add(panel);
            }
        }

        private void RecentButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is string storedPath))
            {
                return;
            }

            FilePathText.Text = storedPath;
            TryRecordRecentSelection(storedPath);
            RefreshRecentButtons();
            PreviewImage();
        }

        private string BuildRecentButtonLabel(string storedPath, bool pinned)
        {
            string fileName = Path.GetFileName((storedPath ?? string.Empty).Trim());
            if (LoadImageTool.TryResolveExistingFilePath(storedPath, out string resolvedPath, out _)
                && LoadImageTool.TryGetHalconImagesRoot(out string imageRoot)
                && TryMakeRelativeToRoot(resolvedPath, imageRoot, out string relativePath))
            {
                string[] parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length > 1)
                {
                    return pinned ? $"[固定] {parts[0]} / {fileName}" : $"{parts[0]} / {fileName}";
                }
            }

            return pinned ? $"[固定] {fileName}" : fileName;
        }

        private void TryRecordRecentSelection(string storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
            {
                return;
            }

            if (!LoadImageTool.TryResolveExistingFilePath(storedPath, out string resolvedPath, out _))
            {
                return;
            }

            if (!LoadImageTool.TryGetHalconImagesRoot(out string imageRoot))
            {
                return;
            }

            if (!TryMakeRelativeToRoot(resolvedPath, imageRoot, out _))
            {
                return;
            }

            _halconStore.RecordSelection(LoadImageTool.ToStoredFilePath(storedPath), "最近使用");
        }

        private void PinRecentButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is string storedPath))
            {
                return;
            }

            _halconStore.TogglePinned(storedPath);
            RefreshRecentButtons();
        }

        private void ClearRecent_Click(object sender, RoutedEventArgs e)
        {
            _halconStore.ClearRecent();
            RefreshRecentButtons();
        }

        private IEnumerable<string> GetDisplayStoredPaths(HalconExampleImageSettings settings)
        {
            settings = settings ?? new HalconExampleImageSettings();
            string currentGroup = GetPathGroupName(FilePathText.Text);
            var recentOrder = settings.RecentFiles
                .Select((pathText, index) => new { pathText, index })
                .ToDictionary(item => item.pathText, item => item.index, StringComparer.OrdinalIgnoreCase);
            var pinnedSet = new HashSet<string>(settings.PinnedFiles, StringComparer.OrdinalIgnoreCase);
            var all = settings.PinnedFiles
                .Concat(settings.RecentFiles)
                .Where(pathText => !string.IsNullOrWhiteSpace(pathText))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return all
                .OrderByDescending(pathText => string.Equals(GetPathGroupName(pathText), currentGroup, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(pathText => pinnedSet.Contains(pathText))
                .ThenBy(pathText => recentOrder.TryGetValue(pathText, out int index) ? index : int.MaxValue)
                .ThenBy(pathText => pathText, StringComparer.OrdinalIgnoreCase);
        }

        private string GetPathGroupName(string storedPath)
        {
            if (!LoadImageTool.TryResolveExistingFilePath(storedPath, out string resolvedPath, out _)
                || !LoadImageTool.TryGetHalconImagesRoot(out string imageRoot)
                || !TryMakeRelativeToRoot(resolvedPath, imageRoot, out string relativePath))
            {
                return string.Empty;
            }

            string[] parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Length > 1 ? parts[0] : "root";
        }

        private static bool TryMakeRelativeToRoot(string fullPath, string rootPath, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(rootPath))
            {
                return false;
            }

            string normalizedFull = Path.GetFullPath(fullPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            if (!normalizedFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relativePath = normalizedFull.Substring(prefix.Length);
            return relativePath.Length > 0;
        }
    }
}
