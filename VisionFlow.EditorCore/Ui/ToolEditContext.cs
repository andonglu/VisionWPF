using System;
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
    /// 标记工具专用编辑窗体。WPF 窗体需继承 Window，WinForms 窗体需继承 Form，
    /// 并提供以下构造之一：
    /// (具体工具类型, ToolEditContext)、(ToolBase, ToolEditContext)、(具体工具类型)、(ToolBase) 或无参构造。
    /// 平台相关的注册由各自的编辑器注册表完成（WpfToolEditorRegistry / ToolEditPageRegistry）。
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
