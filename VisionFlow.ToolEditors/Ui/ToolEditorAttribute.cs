using System;
using VisionFlow.Core;

namespace VisionFlow.Ui
{
    /// <summary>
    /// 标记工具专用编辑窗体。窗体需继承 Form，并提供以下构造之一：
    /// (具体工具类型, ToolEditContext)、(ToolBase, ToolEditContext)、(具体工具类型)、(ToolBase) 或无参构造。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ToolEditorAttribute : Attribute
    {
        public Type ToolType { get; private set; }

        public ToolEditorAttribute(Type toolType)
        {
            ToolType = toolType;
        }
    }
}
