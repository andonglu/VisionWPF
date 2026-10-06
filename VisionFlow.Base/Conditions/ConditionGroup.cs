using System.Collections.Generic;
using VisionFlow.Core;

namespace VisionFlow.Conditions
{
    /// <summary>
    /// 分支条件。实现：<see cref="ComparisonCondition"/>（单条比较）、<see cref="ConditionGroup"/>（与 / 或组合，可嵌套）、
    /// <see cref="ExpressionCondition"/>（布尔表达式）。
    /// </summary>
    public interface ICondition
    {
        /// <summary>求值；trace 非空时追加每个条件项的实际值与结果，供日志和单步调试查看。</summary>
        bool Evaluate(FlowContext ctx, IList<string> trace);
    }

    public enum ConditionLogic
    {
        /// <summary>全部满足。</summary>
        And,
        /// <summary>任一满足。</summary>
        Or
    }

    /// <summary>条件组：按 Logic 组合多个条件（短路求值），组内可以再嵌套组。</summary>
    public sealed class ConditionGroup : ICondition
    {
        public ConditionLogic Logic { get; set; }

        public List<ICondition> Items { get; } = new List<ICondition>();

        public bool Evaluate(FlowContext ctx, IList<string> trace)
        {
            if (Items.Count == 0)
            {
                throw new System.InvalidOperationException("条件组为空");
            }
            foreach (ICondition item in Items)
            {
                bool pass = item.Evaluate(ctx, trace);
                if (Logic == ConditionLogic.And ? !pass : pass)
                {
                    return pass;
                }
            }
            return Logic == ConditionLogic.And;
        }

        public static string LogicText(ConditionLogic logic)
        {
            return logic == ConditionLogic.And ? "且" : "或";
        }

        public override string ToString()
        {
            return "(" + string.Join(" " + LogicText(Logic) + " ", Items) + ")";
        }
    }
}
