using System.Collections.Generic;
using VisionFlow.Conditions;
using VisionFlow.Core;

namespace VisionFlow.Nodes
{
    /// <summary>
    /// 条件循环：条件成立时重复执行循环体，循环体内可用 Loop.Index（从 0 开始的迭代序号）。
    /// 默认先判断后执行；TestAfterBody 为 true 时先执行一次再判断（条件可以引用循环体的输出）。
    /// 条件在本循环的循环帧内求值，因此条件中的 Loop.Index 指向本循环：
    /// 先判断时为即将执行的迭代序号，后判断时为刚执行完的迭代序号。
    /// MaxIterations 防止死循环：达到上限时条件仍成立，按失败处理。
    /// </summary>
    public sealed class WhileLoopNode : LoopNodeBase
    {
        public const int DefaultMaxIterations = 100;

        public ICondition Condition { get; set; }

        /// <summary>最多执行次数，必须大于 0。</summary>
        public int MaxIterations { get; set; } = DefaultMaxIterations;

        /// <summary>先执行循环体再判断条件（do-while）。</summary>
        public bool TestAfterBody { get; set; }

        public WhileLoopNode(string name, ICondition condition = null) : base(name)
        {
            Condition = condition;
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            if (Condition == null)
            {
                return NodeResult.Fail($"{Name} 未设置条件");
            }
            if (MaxIterations <= 0)
            {
                return NodeResult.Fail($"{Name} 最多执行次数必须大于 0（{MaxIterations}）");
            }

            int executed = 0;
            for (int i = 0; ; i++)
            {
                if (!TestAfterBody && !EvaluateCondition(ctx, i))
                {
                    break;
                }
                if (i >= MaxIterations)
                {
                    return NodeResult.Fail($"{Name} 已执行 {MaxIterations} 次，条件仍成立，按可能的死循环中止；如确需更多次，请调大最多执行次数");
                }

                NodeResult result = RunIteration(ctx, new LoopFrame(i, -1, null), out bool breakLoop);
                executed++;
                if (!result.IsSuccess)
                {
                    return result;
                }
                if (breakLoop)
                {
                    break;
                }
                if (TestAfterBody && !EvaluateCondition(ctx, i))
                {
                    break;
                }
            }

            ctx.AddLog(FlowLogLevel.Info, $"[循环] {Name}：条件循环，共执行 {executed} 次", Id, Name);
            return NodeResult.Ok;
        }

        /// <summary>在本循环第 index 次迭代的循环帧内求值条件。</summary>
        private bool EvaluateCondition(FlowContext ctx, int index)
        {
            ctx.PushLoop(new LoopFrame(index, -1, null));
            try
            {
                var trace = new List<string>();
                bool pass = Condition.Evaluate(ctx, trace);
                ctx.AddLog(FlowLogLevel.Info,
                    $"[条件] {Name}（第 {index + 1} 次）：{string.Join("；", trace)} → {(pass ? "继续循环" : "结束循环")}", Id, Name);
                return pass;
            }
            finally
            {
                ctx.PopLoop();
            }
        }
    }
}
