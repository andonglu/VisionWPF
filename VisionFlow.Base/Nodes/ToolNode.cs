using System;
using VisionFlow.Core;

namespace VisionFlow.Nodes
{
    /// <summary>
    /// 工具节点：把一个 ToolBase 包装成流程节点。
    /// </summary>
    public sealed class ToolNode : FlowNode
    {
        public ToolBase Tool { get; private set; }

        public ToolNode(ToolBase tool, string name = null)
            : base(name ?? tool.ModuleName)
        {
            Tool = tool;
        }

        public void ReplaceTool(ToolBase tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            Tool = tool;
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            return Tool.Run(ctx);
        }
    }
}
