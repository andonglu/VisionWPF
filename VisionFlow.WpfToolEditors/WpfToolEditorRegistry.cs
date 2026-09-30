using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using VisionFlow.Core;
using VisionFlow.Ui;

namespace VisionFlow.WpfToolEditors
{
    /// <summary>
    /// WPF 工具编辑器注册表（与 WinForms 的 ToolEditPageRegistry 平行）。
    /// 每种工具类型可注册一个专用编辑窗口；插件通过 [ToolEditor] 特性 + Window 派生类接入。
    /// 主窗口通过 TryCreate 分发，无注册时回退到内置编辑器或通用编辑器。
    /// </summary>
    public static class WpfToolEditorRegistry
    {
        private static readonly Dictionary<Type, Func<ToolBase, ToolEditContext, Window>> _editors
            = new Dictionary<Type, Func<ToolBase, ToolEditContext, Window>>();
        private static readonly List<string> _diagnostics = new List<string>();

        /// <summary>注册诊断信息（不兼容的编辑器声明、构造缺失等），供宿主展示。</summary>
        public static IReadOnlyList<string> Diagnostics
        {
            get { lock (_diagnostics) { return _diagnostics.ToList(); } }
        }

        /// <summary>直接注册某工具类型的编辑器工厂。后注册覆盖先注册（插件可覆盖内置）。</summary>
        public static void Register(Type toolType, Func<ToolBase, ToolEditContext, Window> factory)
        {
            if (toolType == null || factory == null)
            {
                return;
            }
            lock (_editors)
            {
                _editors[toolType] = factory;
            }
        }

        /// <summary>
        /// 按 [ToolEditor] 特性注册一个 WPF 窗口编辑器类型。
        /// 类型不是 Window 派生类（例如 WinForms Form）时记录诊断并返回 false。
        /// </summary>
        public static bool RegisterEditor(Type editorType)
        {
            if (editorType == null)
            {
                return false;
            }

            var attributes = editorType.GetCustomAttributes<ToolEditorAttribute>(inherit: false).ToList();
            if (attributes.Count == 0)
            {
                return false;
            }
            if (!typeof(Window).IsAssignableFrom(editorType))
            {
                AddDiagnostic($"编辑器 {editorType.FullName} 声明了 [ToolEditor] 但不是 WPF Window 派生类，WPF 端不可用（WinForms 编辑器请在 WinForms 宿主中使用）。");
                return false;
            }

            bool registered = false;
            foreach (ToolEditorAttribute attribute in attributes)
            {
                Type toolType = attribute.ToolType;
                if (toolType == null || !typeof(ToolBase).IsAssignableFrom(toolType))
                {
                    continue;
                }
                Register(toolType, (tool, context) => CreateEditorWindow(editorType, tool, context));
                registered = true;
            }
            return registered;
        }

        /// <summary>查找并创建工具的专用编辑器窗口；无注册时返回 false（调用方回退）。</summary>
        public static bool TryCreate(ToolBase tool, ToolEditContext context, out Window window)
        {
            window = null;
            if (tool == null)
            {
                return false;
            }
            Func<ToolBase, ToolEditContext, Window> factory;
            lock (_editors)
            {
                if (!_editors.TryGetValue(tool.GetType(), out factory))
                {
                    return false;
                }
            }
            try
            {
                window = factory(tool, context);
                return window != null;
            }
            catch (Exception ex)
            {
                AddDiagnostic($"创建编辑器失败（工具 {tool.GetType().FullName}）：{ex.Message}");
                return false;
            }
        }

        private static void AddDiagnostic(string message)
        {
            lock (_diagnostics)
            {
                _diagnostics.Add(message);
            }
        }

        private static Window CreateEditorWindow(Type editorType, ToolBase tool, ToolEditContext context)
        {
            ConstructorInfo ctor = editorType.GetConstructor(new[] { tool.GetType(), typeof(ToolEditContext) });
            if (ctor != null)
            {
                return (Window)ctor.Invoke(new object[] { tool, context });
            }

            ctor = editorType.GetConstructor(new[] { typeof(ToolBase), typeof(ToolEditContext) });
            if (ctor != null)
            {
                return (Window)ctor.Invoke(new object[] { tool, context });
            }

            ctor = editorType.GetConstructor(new[] { tool.GetType() });
            if (ctor != null)
            {
                return (Window)ctor.Invoke(new object[] { tool });
            }

            ctor = editorType.GetConstructor(new[] { typeof(ToolBase) });
            if (ctor != null)
            {
                return (Window)ctor.Invoke(new object[] { tool });
            }

            ctor = editorType.GetConstructor(Type.EmptyTypes);
            if (ctor != null)
            {
                return (Window)ctor.Invoke(null);
            }

            throw new InvalidOperationException($"工具编辑窗口 {editorType.FullName} 缺少可用构造函数。");
        }
    }
}
