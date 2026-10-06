using System.Collections.Generic;
using VisionFlow.Core;

namespace VisionFlow.Nodes
{
    /// <summary>
    /// 循环节点基类（For 循环、While 循环）：循环体是子节点列表，可任意嵌套；
    /// 统一处理循环帧压栈与跳出循环 / 跳过本次信号。
    /// </summary>
    public abstract class LoopNodeBase : FlowNode
    {
        public List<FlowNode> Body { get; } = new List<FlowNode>();

        public override bool IsComposite
        {
            get { return true; }
        }

        protected LoopNodeBase(string name) : base(name)
        {
        }

        /// <summary>
        /// 在给定循环帧中执行一次循环体。返回失败时应结束循环并向上传播；
        /// breakLoop 为 true 表示循环体内执行了跳出循环。跳过本次信号在此清除。
        /// </summary>
        protected NodeResult RunIteration(FlowContext ctx, LoopFrame frame, out bool breakLoop)
        {
            breakLoop = false;
            ctx.PushLoop(frame);
            try
            {
                NodeResult result = RunChildren(Body, ctx);
                if (!result.IsSuccess)
                {
                    return result;
                }
            }
            finally
            {
                ctx.PopLoop();
            }

            LoopControlSignal signal = ctx.LoopControl;
            ctx.LoopControl = LoopControlSignal.None;
            if (signal == LoopControlSignal.Break)
            {
                breakLoop = true;
                ctx.AddLog(FlowLogLevel.Info, $"[循环] {Name}：第 {frame.Index + 1} 次执行中跳出循环", Id, Name);
            }
            return NodeResult.Ok;
        }
    }
}
