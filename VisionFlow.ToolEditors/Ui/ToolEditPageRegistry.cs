using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Core;

namespace VisionFlow.Ui
{
    /// <summary>打开工具配置页时的上下文：用于计算上游引用候选。</summary>
    public sealed class ToolEditContext
    {
        /// <summary>流程根节点。</summary>
        public FlowNode Root { get; set; }
        /// <summary>当前编辑的工具节点（候选只取它之前的上游）。</summary>
        public FlowNode Node { get; set; }
        /// <summary>主页面当前打开的输入图像，对应流程运行时的 Input.Image。</summary>
        public HObject InputImage { get; set; }
        /// <summary>主页面当前打开图像的文件路径，仅用于编辑窗体提示/预览。</summary>
        public string InputImagePath { get; set; }
        /// <summary>最近一次流程运行上下文，用于编辑窗体显示运行结果与最近定位姿态。</summary>
        public FlowContext LastRunContext { get; set; }
    }

    /// <summary>
    /// 工具配置页注册表：每种工具类型可注册一个专用编辑窗体。
    /// 之后为每个工具写处理页面时，在这里登记即可被编辑器双击打开。
    /// </summary>
    public static class ToolEditPageRegistry
    {
        private static readonly Dictionary<Type, Func<ToolBase, ToolEditContext, Form>> _pages
            = new Dictionary<Type, Func<ToolBase, ToolEditContext, Form>>();

        public static void Register(Type toolType, Func<ToolBase, ToolEditContext, Form> factory)
        {
            if (toolType == null || factory == null)
            {
                return;
            }
            _pages[toolType] = factory;
        }

        public static bool RegisterEditor(Type editorType)
        {
            if (editorType == null || !typeof(Form).IsAssignableFrom(editorType))
            {
                return false;
            }

            bool registered = false;
            foreach (ToolEditorAttribute attribute in editorType.GetCustomAttributes<ToolEditorAttribute>(inherit: false))
            {
                Type toolType = attribute.ToolType;
                if (toolType == null || !typeof(ToolBase).IsAssignableFrom(toolType))
                {
                    continue;
                }

                Register(toolType, (tool, context) => CreateEditorForm(editorType, tool, context));
                registered = true;
            }
            return registered;
        }

        public static int RegisterEditorsFromAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (RegisterEditor(type))
                {
                    count++;
                }
            }
            return count;
        }

        public static bool TryCreate(ToolBase tool, ToolEditContext context, out Form form)
        {
            if (_pages.TryGetValue(tool.GetType(), out Func<ToolBase, ToolEditContext, Form> factory))
            {
                form = factory(tool, context);
                return true;
            }
            form = null;
            return false;
        }

        private static Form CreateEditorForm(Type editorType, ToolBase tool, ToolEditContext context)
        {
            ConstructorInfo ctor = editorType.GetConstructor(new[] { tool.GetType(), typeof(ToolEditContext) });
            if (ctor != null)
            {
                return (Form)ctor.Invoke(new object[] { tool, context });
            }

            ctor = editorType.GetConstructor(new[] { typeof(ToolBase), typeof(ToolEditContext) });
            if (ctor != null)
            {
                return (Form)ctor.Invoke(new object[] { tool, context });
            }

            ctor = editorType.GetConstructor(new[] { tool.GetType() });
            if (ctor != null)
            {
                return (Form)ctor.Invoke(new object[] { tool });
            }

            ctor = editorType.GetConstructor(new[] { typeof(ToolBase) });
            if (ctor != null)
            {
                return (Form)ctor.Invoke(new object[] { tool });
            }

            ctor = editorType.GetConstructor(Type.EmptyTypes);
            if (ctor != null)
            {
                return (Form)ctor.Invoke(null);
            }

            throw new InvalidOperationException($"工具编辑窗体 {editorType.FullName} 缺少可用构造函数。");
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
    }
}
