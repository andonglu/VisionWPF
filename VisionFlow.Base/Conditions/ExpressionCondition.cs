using System;
using System.Collections.Generic;
using VisionFlow.Core;
using VisionFlow.Expressions;

namespace VisionFlow.Conditions
{
    /// <summary>布尔表达式条件，如 {匹配1.MatchCount} == 2 &amp;&amp; isvalid({测量1.Row})。</summary>
    public sealed class ExpressionCondition : ICondition
    {
        public string Expression { get; set; }

        public bool Evaluate(FlowContext ctx, IList<string> trace)
        {
            object value = ExpressionParser.Parse(Expression).Evaluate(ctx);
            if (!(value is bool pass))
            {
                throw new InvalidOperationException(
                    $"条件表达式 {Expression} 的结果为{ExpressionValues.TypeName(value)}，应为布尔值");
            }
            trace?.Add($"{Expression} → {(pass ? "成立" : "不成立")}");
            return pass;
        }

        public override string ToString()
        {
            return Expression ?? string.Empty;
        }
    }
}
