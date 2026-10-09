using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HalconDotNet;
using VisionFlow.Tools;
using VisionFlow.WpfToolEditors.Services;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfHalconExamplePickerWindow : Window
    {
        private sealed class HalconExampleImageItem
        {
            public string GroupName { get; init; }

            public string DisplayName { get; init; }

            public string RelativePath { get; init; }

            public string ResolvedPath { get; init; }

            public string StoredPath { get; init; }
        }

        private static readonly string[] PreferredGroups =
        {
            "最近使用",
            "color",
            "ocr",
            "datacode",
            "barcode",
            "blister",
            "packaging",
            "matching",
            "surface",
            "calib"
        };

        private readonly HalconExampleImageStore _store = new HalconExampleImageStore();
        private readonly List<HalconExampleImageItem> _items;
        private readonly Dictionary<string, List<HalconExampleImageItem>> _groups;
        private readonly HalconExampleImageSettings _settings;

        public WpfHalconExamplePickerWindow(string initialStoredPath = null)
        {
            if (!LoadImageTool.TryGetHalconImagesRoot(out string imageRoot))
            {
                throw new InvalidOperationException("未找到 HALCONIMAGES 环境变量或图像目录不存在。");
            }

            InitializeComponent();
            RootPathText.Text = imageRoot;
            SearchText.Text = string.Empty;
            _settings = _store.Load();
            _items = ScanItems(imageRoot);
            _groups = BuildGroups(_items, _settings);
            PopulateGroups(initialStoredPath);
            if (!TrySelectInitialItem(initialStoredPath))
            {
                ApplyGroupSelection(_settings.LastGroup);
            }
        }

        public string SelectedStoredPath { get; private set; }

        private static List<HalconExampleImageItem> ScanItems(string imageRoot)
        {
            string[] files = Directory.GetFiles(imageRoot, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedImageFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var items = new List<HalconExampleImageItem>(files.Length);
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(imageRoot, file);
                string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string group = parts.Length > 1 ? parts[0] : "root";
                items.Add(new HalconExampleImageItem
                {
                    GroupName = group,
                    DisplayName = Path.GetFileName(file),
                    RelativePath = relative,
                    ResolvedPath = file,
                    StoredPath = LoadImageTool.ToStoredFilePath(file)
                });
            }

            return items;
        }

        private static bool IsSupportedImageFile(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, List<HalconExampleImageItem>> BuildGroups(
            List<HalconExampleImageItem> items,
            HalconExampleImageSettings settings)
        {
            var groups = items
                .GroupBy(item => item.GroupName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => group.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            List<string> pinnedStoredPaths = settings?.PinnedFiles ?? new List<string>();
            List<string> recentStoredPaths = settings?.RecentFiles ?? new List<string>();
            if (pinnedStoredPaths.Count > 0 || recentStoredPaths.Count > 0)
            {
                var recent = new List<HalconExampleImageItem>();
                foreach (string storedPath in pinnedStoredPaths.Concat(recentStoredPaths).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    HalconExampleImageItem item = items.FirstOrDefault(candidate =>
                        string.Equals(candidate.StoredPath, storedPath, StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                    {
                        recent.Add(item);
                    }
                }

                groups["最近使用"] = recent;
            }
            else
            {
                groups["最近使用"] = new List<HalconExampleImageItem>();
            }

            return groups;
        }

        private void PopulateGroups(string initialStoredPath)
        {
            string selectedGroup = ResolveInitialGroup(initialStoredPath);
            var groupNames = _groups.Keys
                .OrderBy(name => GroupOrder(name))
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            GroupList.ItemsSource = groupNames;
            GroupList.SelectedItem = groupNames.Contains(selectedGroup, StringComparer.OrdinalIgnoreCase)
                ? groupNames.First(name => string.Equals(name, selectedGroup, StringComparison.OrdinalIgnoreCase))
                : groupNames.FirstOrDefault();
        }

        private string ResolveInitialGroup(string initialStoredPath)
        {
            if (!string.IsNullOrWhiteSpace(initialStoredPath))
            {
                HalconExampleImageItem item = _items.FirstOrDefault(candidate =>
                    string.Equals(candidate.StoredPath, initialStoredPath.Trim(), StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    return item.GroupName;
                }
            }

            return string.IsNullOrWhiteSpace(_settings.LastGroup) ? "最近使用" : _settings.LastGroup;
        }

        private int GroupOrder(string name)
        {
            for (int i = 0; i < PreferredGroups.Length; i++)
            {
                if (string.Equals(PreferredGroups[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return PreferredGroups.Length + 1;
        }

        private bool TrySelectInitialItem(string initialStoredPath)
        {
            if (string.IsNullOrWhiteSpace(initialStoredPath))
            {
                return false;
            }

            HalconExampleImageItem target = _items.FirstOrDefault(item =>
                string.Equals(item.StoredPath, initialStoredPath.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                return false;
            }

            ApplyGroupSelection(target.GroupName);
            ImageList.SelectedItem = ImageList.Items.Cast<HalconExampleImageItem>()
                .FirstOrDefault(item => string.Equals(item.StoredPath, target.StoredPath, StringComparison.OrdinalIgnoreCase));
            return ImageList.SelectedItem != null;
        }

        private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyGroupSelection(GroupList.SelectedItem as string);
        }

        private void SearchText_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyGroupSelection(GroupList.SelectedItem as string);
        }

        private void ApplyGroupSelection(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName))
            {
                ImageList.ItemsSource = Array.Empty<HalconExampleImageItem>();
                UpdateSelection(null);
                return;
            }

            IEnumerable<HalconExampleImageItem> items = _groups.TryGetValue(groupName, out List<HalconExampleImageItem> groupItems)
                ? groupItems
                : Enumerable.Empty<HalconExampleImageItem>();
            string keyword = (SearchText.Text ?? string.Empty).Trim();
            if (keyword.Length > 0)
            {
                items = items.Where(item => item.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || item.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            List<HalconExampleImageItem> filtered = items.ToList();
            ImageList.ItemsSource = filtered;
            ImageList.SelectedItem = filtered.FirstOrDefault();
            StatusText.Text = filtered.Count == 0
                ? $"分组“{groupName}”没有匹配图像。"
                : $"分组“{groupName}”共 {filtered.Count} 张图像。";
        }

        private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelection(ImageList.SelectedItem as HalconExampleImageItem);
        }

        private void ImageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ImageList.SelectedItem is HalconExampleImageItem)
            {
                CommitSelectionAndClose();
            }
        }

        private void UpdateSelection(HalconExampleImageItem item)
        {
            SelectedNameText.Text = item?.DisplayName ?? "未选择图像";
            SelectedPathText.Text = item == null ? string.Empty : item.StoredPath;
            SelectedStoredPath = item?.StoredPath;
            PreviewCurrent(item);
        }

        private void PreviewCurrent(HalconExampleImageItem item)
        {
            if (item == null)
            {
                StatusText.Text = string.IsNullOrWhiteSpace(StatusText.Text) ? "请选择一张 HALCON 示例图像。" : StatusText.Text;
                return;
            }

            if (!File.Exists(item.ResolvedPath))
            {
                StatusText.Text = "图像文件不存在：" + item.RelativePath;
                return;
            }

            HOperatorSet.ReadImage(out HObject image, item.ResolvedPath);
            try
            {
                PreviewImageView.ShowImage(image);
                StatusText.Text = "已预览：" + item.RelativePath;
            }
            finally
            {
                image.Dispose();
            }
        }

        private void Select_Click(object sender, RoutedEventArgs e)
        {
            CommitSelectionAndClose();
        }

        private void CommitSelectionAndClose()
        {
            if (!(ImageList.SelectedItem is HalconExampleImageItem item))
            {
                StatusText.Text = "请选择一张图像。";
                return;
            }

            SelectedStoredPath = item.StoredPath;
            _store.RecordSelection(item.StoredPath, GroupList.SelectedItem as string);
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
