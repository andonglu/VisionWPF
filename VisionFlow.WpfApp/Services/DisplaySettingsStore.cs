using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace VisionFlow.WpfApp.Services
{
    /// <summary>叠加显示设置的持久化模型。</summary>
    public sealed class DisplaySettingsModel
    {
        public string OverlayDrawMode { get; set; } = "margin";
        public double OverlayFillOpacity { get; set; } = 0.35;
        public double OverlayLineWidth { get; set; } = 2.0;
    }

    /// <summary>
    /// 显示设置存取（VF-08 从 MainWindow 平移）：%AppData%\VisionFlow\wpf-display-settings.json。
    /// 读取时做范围钳制；读取失败通过 showWarning 提示，不抛异常。
    /// </summary>
    public sealed class DisplaySettingsStore
    {
        private readonly Action<string, string> _showWarning;

        public DisplaySettingsStore(Action<string, string> showWarning)
        {
            _showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
        }

        public static string SettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VisionFlow", "wpf-display-settings.json");
        }

        public DisplaySettingsModel Load()
        {
            string path = SettingsPath();
            if (!File.Exists(path))
            {
                return new DisplaySettingsModel();
            }
            try
            {
                DisplaySettingsModel settings = JsonSerializer.Deserialize<DisplaySettingsModel>(File.ReadAllText(path));
                if (settings == null)
                {
                    return new DisplaySettingsModel();
                }
                settings.OverlayDrawMode = string.Equals(settings.OverlayDrawMode, "fill", StringComparison.OrdinalIgnoreCase)
                    ? "fill" : "margin";
                settings.OverlayFillOpacity = Math.Max(0.0, Math.Min(1.0, settings.OverlayFillOpacity));
                settings.OverlayLineWidth = Math.Max(1.0, settings.OverlayLineWidth);
                return settings;
            }
            catch (IOException ex)
            {
                _showWarning("系统显示设置", "读取显示设置失败：" + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _showWarning("系统显示设置", "读取显示设置失败：" + ex.Message);
            }
            catch (JsonException ex)
            {
                _showWarning("系统显示设置", "显示设置文件格式错误：" + ex.Message);
            }
            return new DisplaySettingsModel();
        }

        public void Save(DisplaySettingsModel settings)
        {
            string path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static string FormatPercent(double value)
        {
            return value.ToString("P0", CultureInfo.CurrentCulture);
        }
    }
}
