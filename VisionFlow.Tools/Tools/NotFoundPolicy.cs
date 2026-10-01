using VisionFlow.Core;

namespace VisionFlow.Tools
{
    /// <summary>
    /// “未找到”处理策略（TR-06）：匹配 0 个、识别不到、筛选或运算结果为空、测量没找到边缘等
    /// 属于正常的检测结果而非执行错误。默认仍按失败处理（与历史流程一致）；
    /// 关闭后工具照常输出 Found=false、数量为 0，流程继续，由下游 IfElse 判定。
    /// </summary>
    public interface INotFoundPolicy
    {
        /// <summary>未找到时是否让节点失败并中断流程（默认 true）。</summary>
        bool FailWhenNotFound { get; set; }
    }

    internal static class NotFoundOutcome
    {
        /// <summary>按策略返回“未找到”的节点结果：失败，或记录警告后继续。</summary>
        public static NodeResult Resolve(FlowContext ctx, INotFoundPolicy policy, string message)
        {
            if (policy.FailWhenNotFound)
            {
                return NodeResult.Fail(message);
            }
            ctx.AddLog(FlowLogLevel.Warning, $"{message}（已设置未找到时继续：Found=false）");
            return NodeResult.Ok;
        }
    }
}
