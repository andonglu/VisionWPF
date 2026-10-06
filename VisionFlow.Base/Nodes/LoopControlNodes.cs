using VisionFlow.Core;

namespace VisionFlow.Nodes
{
    /// <summary>
    /// 跳出循环 / 跳过本次的公共实现：设置循环控制信号，由最内层循环消费。
    /// 只能放在循环体内（可经 IfElse 等嵌套）；不在循环内时运行失败，校验也会报错。
    /// </summary>
    public abstract class LoopControlNode : FlowNode
    {
        protected LoopControlNode(string name) : base(name)
        {
        }

        public abstract LoopControlSignal Signal { get; }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            if (ctx.CurrentLoop == null)
            {
                return NodeResult.Fail($"{Name} 只能放在循环体内");
            }
            ctx.LoopControl = Signal;
            ctx.AddLog(FlowLogLevel.Info,
                Signal == LoopControlSignal.Break ? $"[跳出循环] {Name}" : $"[跳过本次] {Name}", Id, Name);
            return NodeResult.Ok;
        }
    }

    /// <summary>跳出最内层循环。</summary>
    public sealed class BreakNode : LoopControlNode
    {
        public BreakNode(string name) : base(name)
        {
        }

        public override LoopControlSignal Signal
        {
            get { return LoopControlSignal.Break; }
        }
    }

    /// <summary>结束最内层循环的本次迭代，进入下一次。</summary>
    public sealed class ContinueNode : LoopControlNode
    {
        public ContinueNode(string name) : base(name)
        {
        }

        public override LoopControlSignal Signal
        {
            get { return LoopControlSignal.Continue; }
        }
    }
}
