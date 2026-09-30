using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace VisionFlow.Ui
{
    /// <summary>先完成序列化和同目录文件写入，再原子替换目标文件。</summary>
    public static class FlowFileSaveService
    {
        public static bool TrySave(string path, Func<string> serialize, out string error)
        {
            string stagingPath = null;
            bool stagingCreated = false;
            error = null;
            try
            {
                ArgumentNullException.ThrowIfNull(serialize);
                string fullPath = Path.GetFullPath(path);
                string content = serialize();
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException("流程序列化结果为空，未覆盖原文件。");
                }
                stagingPath = Path.Combine(Path.GetDirectoryName(fullPath),
                    "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".saving");
                using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stagingCreated = true;
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
                    {
                        writer.Write(content);
                        writer.Flush();
                    }
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(stagingPath, fullPath, null);
                }
                else
                {
                    File.Move(stagingPath, fullPath);
                }
                stagingPath = null;
                error = null;
                return true;
            }
            catch (Exception ex) when (IsSaveException(ex))
            {
                error = ex.GetBaseException().Message;
                return false;
            }
            finally
            {
                if (stagingPath != null && stagingCreated)
                {
                    try
                    {
                        File.Delete(stagingPath);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        error = (error ?? "保存失败") + Environment.NewLine
                            + $"临时文件 '{stagingPath}' 清理失败：{ex.Message}";
                    }
                }
            }
        }

        public static bool IsSaveException(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException
                || error is JsonException || error is NotSupportedException
                || error is InvalidOperationException || error is ArgumentException
                || error is TargetInvocationException;
        }
    }
}
