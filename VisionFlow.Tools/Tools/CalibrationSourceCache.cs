using System;
using System.Collections.Generic;
using VisionFlow.Tools.Calibration;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 标定来源（文件 / 内嵌）的读取、缓存与互斥校验，坐标转换（CB-05）与纠偏计算（CB-07）共用：
    /// 文件按“完整路径 + 修改时间 + 大小”缓存（文件被替换后自动重新读取），内嵌数据按内容缓存。
    /// </summary>
    internal sealed class CalibrationSourceCache
    {
        private readonly object _sync = new object();
        private object _cachedKey;
        private CalibrationResult _cached;

        /// <summary>错误信息中的来源名称。</summary>
        public static string SourceName(CalibrationSource source, string file)
        {
            return source == CalibrationSource.Embedded ? "内嵌标定数据" : "标定文件 " + file;
        }

        /// <summary>来源为内嵌，或配置的标定文件存在（文件缺失不在校验阶段报，沿用批一约定）。</summary>
        public static bool IsReadable(CalibrationSource source, string file)
        {
            return source == CalibrationSource.Embedded || (!string.IsNullOrWhiteSpace(file) && System.IO.File.Exists(file));
        }

        /// <summary>来源互斥问题：两侧同时有值时报错并说明以哪个为准；embeddedEmptyHint 不为空时，内嵌来源没有数据也报错。</summary>
        public static void AddExclusivityIssues(List<ToolConfigurationIssue> issues, CalibrationSource source, string file, string data, string embeddedEmptyHint)
        {
            if (source == CalibrationSource.File && !string.IsNullOrWhiteSpace(data))
            {
                issues.Add(new ToolConfigurationIssue("CalibrationData",
                    "标定来源为文件，但仍保留内嵌标定数据：二者互斥，请在编辑窗口中重新选择来源（会清空另一侧），或清空 CalibrationData"));
            }
            if (source == CalibrationSource.Embedded && !string.IsNullOrWhiteSpace(file))
            {
                issues.Add(new ToolConfigurationIssue("CalibrationFile",
                    "标定来源为内嵌，但仍配置了标定文件：二者互斥，请在编辑窗口中重新选择来源（会清空另一侧），或清空 CalibrationFile"));
            }
            if (embeddedEmptyHint != null && source == CalibrationSource.Embedded && string.IsNullOrWhiteSpace(data))
            {
                issues.Add(new ToolConfigurationIssue("CalibrationData", "标定来源为内嵌，但没有内嵌标定数据：" + embeddedEmptyHint));
            }
        }

        /// <summary>
        /// 读取标定来源（带缓存）。文件缺失抛 FileNotFoundException（IOException），
        /// 格式错误抛 InvalidDataException（不是 IOException，调用方须单独捕获）。
        /// </summary>
        public CalibrationResult Load(CalibrationSource source, string file, string data)
        {
            object key;
            if (source == CalibrationSource.Embedded)
            {
                key = data ?? string.Empty;
            }
            else
            {
                var info = new System.IO.FileInfo(file);
                if (!info.Exists)
                {
                    throw new System.IO.FileNotFoundException($"标定文件不存在：{file}", file);
                }
                key = (info.FullName, info.LastWriteTimeUtc, info.Length);
            }
            lock (_sync)
            {
                if (_cached != null && Equals(_cachedKey, key))
                {
                    return _cached;
                }
                CalibrationResult loaded = source == CalibrationSource.Embedded
                    ? CalibrationService.Parse(data, "内嵌标定数据")
                    : CalibrationService.Load(file);
                _cached = loaded;
                _cachedKey = key;
                return loaded;
            }
        }

        /// <summary>读取时可能抛出、应转成中文配置问题的异常。</summary>
        public static bool IsLoadException(Exception ex)
        {
            return ex is System.IO.IOException || ex is System.IO.InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException;
        }

        public void Release()
        {
            lock (_sync)
            {
                _cached = null;
                _cachedKey = null;
            }
        }
    }
}
