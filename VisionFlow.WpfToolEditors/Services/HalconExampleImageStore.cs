using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VisionFlow.WpfToolEditors.Services
{
    internal sealed class HalconExampleImageSettings
    {
        public string LastGroup { get; set; } = "最近使用";

        public List<string> RecentFiles { get; set; } = new List<string>();

        public List<string> PinnedFiles { get; set; } = new List<string>();
    }

    internal sealed class HalconExampleImageStore
    {
        private const int MaxRecentCount = 12;

        public static string SettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VisionFlow", "halcon-example-images.json");
        }

        public HalconExampleImageSettings Load()
        {
            string path = SettingsPath();
            if (!File.Exists(path))
            {
                return new HalconExampleImageSettings();
            }

            try
            {
                HalconExampleImageSettings settings = JsonSerializer.Deserialize<HalconExampleImageSettings>(File.ReadAllText(path));
                if (settings == null)
                {
                    return new HalconExampleImageSettings();
                }

                settings.LastGroup = string.IsNullOrWhiteSpace(settings.LastGroup) ? "最近使用" : settings.LastGroup.Trim();
                settings.RecentFiles = Normalize(settings.RecentFiles);
                settings.PinnedFiles = Normalize(settings.PinnedFiles);
                return settings;
            }
            catch (IOException)
            {
                return new HalconExampleImageSettings();
            }
            catch (UnauthorizedAccessException)
            {
                return new HalconExampleImageSettings();
            }
            catch (JsonException)
            {
                return new HalconExampleImageSettings();
            }
        }

        public void Save(HalconExampleImageSettings settings)
        {
            settings = settings ?? new HalconExampleImageSettings();
            settings.RecentFiles = Normalize(settings.RecentFiles);
            settings.PinnedFiles = Normalize(settings.PinnedFiles);
            string path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        public void RecordSelection(string storedFilePath, string selectedGroup)
        {
            HalconExampleImageSettings settings = Load();
            settings.LastGroup = string.IsNullOrWhiteSpace(selectedGroup) ? settings.LastGroup : selectedGroup.Trim();
            if (!string.IsNullOrWhiteSpace(storedFilePath))
            {
                string current = storedFilePath.Trim();
                settings.RecentFiles.RemoveAll(pathText => string.Equals(pathText, current, StringComparison.OrdinalIgnoreCase));
                settings.RecentFiles.Insert(0, current);
            }

            Save(settings);
        }

        public void TogglePinned(string storedFilePath)
        {
            if (string.IsNullOrWhiteSpace(storedFilePath))
            {
                return;
            }

            HalconExampleImageSettings settings = Load();
            string current = storedFilePath.Trim();
            int index = settings.PinnedFiles.FindIndex(pathText => string.Equals(pathText, current, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                settings.PinnedFiles.RemoveAt(index);
            }
            else
            {
                settings.PinnedFiles.Insert(0, current);
            }

            Save(settings);
        }

        public void ClearRecent()
        {
            HalconExampleImageSettings settings = Load();
            settings.RecentFiles.Clear();
            Save(settings);
        }

        public bool IsPinned(string storedFilePath)
        {
            if (string.IsNullOrWhiteSpace(storedFilePath))
            {
                return false;
            }

            HalconExampleImageSettings settings = Load();
            return settings.PinnedFiles.Any(pathText => string.Equals(pathText, storedFilePath.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> Normalize(List<string> paths)
        {
            return (paths ?? new List<string>())
                .Where(pathText => !string.IsNullOrWhiteSpace(pathText))
                .Select(pathText => pathText.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentCount)
                .ToList();
        }
    }
}
