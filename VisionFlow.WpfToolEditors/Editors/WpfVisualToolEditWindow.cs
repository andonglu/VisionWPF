using VisionFlow.Core;
using VisionFlow.Tools;
using VisionFlow.Ui;

namespace VisionFlow.WpfToolEditors.Editors
{
    public sealed class WpfVisualToolEditWindow : WpfGenericToolEditWindow
    {
        public WpfVisualToolEditWindow(ToolBase tool, ToolEditContext context) : base(tool, context)
        {
            Title = "视觉处理编辑 - " + tool.ModuleName;
        }
    }
}
