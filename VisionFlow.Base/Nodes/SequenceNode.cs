using System.Collections.Generic;
using VisionFlow.Core;

namespace VisionFlow.Nodes
{
    /// <summary>
    /// 顺序容器：按添加顺序执行子节点，任一失败即中断。
    /// 流程的根节点通常就是一个 SequenceNode。
    /// </summary>
    public sealed class SequenceNode : FlowNode
    {
        public List<FlowNode> Children { get; } = new List<FlowNode>();

        public override bool IsComposite
        {
            get { return true; }
        }

        public SequenceNode(string name) : base(name)
        {
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            return RunChildren(Children, ctx);
        }
    }
}
