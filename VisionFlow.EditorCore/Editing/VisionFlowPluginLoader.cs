using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using VisionFlow.Core;

namespace VisionFlow.Editing
{
    public sealed class PluginLoadResult
    {
        public string AssemblyPath { get; set; }
        public int ToolCount { get; set; }
        public int EditorCount { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>
    /// 扫描 plugins 文件夹中的 DLL，并按特性自动注册工具和编辑页。
    /// 工具执行注册（工具箱、持久化身份）与平台相关编辑器注册分离：
    /// 编辑器注册通过 <see cref="EditorRegistrars"/> 由宿主接入
    /// （WinForms 宿主接入 ToolEditPageRegistry.RegisterEditor，
    /// WPF 宿主接入 WpfToolEditorRegistry.RegisterEditor）。
    /// </summary>
    public static class VisionFlowPluginLoader
    {
        private static readonly HashSet<string> _probeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _resolverRegistered;

        /// <summary>平台相关的编辑器注册入口：输入候选编辑器类型，返回是否注册成功。</summary>
        public static IList<Func<Type, bool>> EditorRegistrars { get; } = new List<Func<Type, bool>>();

        public static IReadOnlyList<PluginLoadResult> LoadFromDefaultDirectory()
        {
            string pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
            return LoadFromDirectory(pluginDirectory);
        }

        public static IReadOnlyList<PluginLoadResult> LoadFromDirectory(string pluginDirectory)
        {
            var results = new List<PluginLoadResult>();
            if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory))
            {
                return results;
            }

            RegisterResolver(pluginDirectory);
            foreach (string path in Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    _probeDirectories.Add(directory);
                }
                results.Add(LoadAssembly(path));
            }
            return results;
        }

        public static PluginLoadResult LoadAssembly(string assemblyPath)
        {
            var result = new PluginLoadResult { AssemblyPath = assemblyPath };
            try
            {
                Assembly assembly = Assembly.LoadFrom(assemblyPath);
                RegisterFromAssembly(assembly, result);
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
            }
            return result;
        }

        public static PluginLoadResult RegisterFromAssembly(Assembly assembly)
        {
            var result = new PluginLoadResult { AssemblyPath = assembly?.Location };
            RegisterFromAssembly(assembly, result);
            return result;
        }

        private static void RegisterFromAssembly(Assembly assembly, PluginLoadResult result)
        {
            if (assembly == null)
            {
                return;
            }

            foreach (Type type in GetLoadableTypes(assembly))
            {
                try
                {
                    if (!type.IsAbstract && typeof(ToolBase).IsAssignableFrom(type))
                    {
                        // 注册持久化身份（稳定 ID），与是否进入工具箱无关
                        FlowSerializer.RegisterToolType(type);
                        ToolboxToolAttribute attribute = type.GetCustomAttribute<ToolboxToolAttribute>(inherit: false);
                        if (attribute != null && ToolboxRegistry.RegisterTool(type, attribute))
                        {
                            result.ToolCount++;
                        }
                    }

                    bool editorRegistered = false;
                    foreach (Func<Type, bool> registrar in EditorRegistrars)
                    {
                        try
                        {
                            editorRegistered |= registrar(type);
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"编辑器注册失败 {type.FullName}: {ex.Message}");
                        }
                    }
                    if (editorRegistered)
                    {
                        result.EditorCount++;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{type.FullName}: {ex.Message}");
                }
            }
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
        }

        private static void RegisterResolver(string pluginDirectory)
        {
            _probeDirectories.Add(pluginDirectory);
            if (_resolverRegistered)
            {
                return;
            }

            _resolverRegistered = true;
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                foreach (string directory in _probeDirectories)
                {
                    string path = Path.Combine(directory, name.Name + ".dll");
                    if (File.Exists(path))
                    {
                        return context.LoadFromAssemblyPath(path);
                    }
                }
                return null;
            };
        }
    }
}
