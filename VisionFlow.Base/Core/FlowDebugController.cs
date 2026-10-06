using System.Threading;

namespace VisionFlow.Core
{
    /// <summary>
    /// 节点边界的调试钩子抽象：引擎在每个节点执行前后调用。
    /// 引擎本身是同步执行（后台线程），实现方可以用阻塞等待来暂停，无需 async-ify。
    /// 挂在 <see cref="FlowContext.DebugHooks"/> 上，默认 null 表示不调试、零开销。
    /// </summary>
    public interface IFlowDebugHooks
    {
        /// <summary>节点即将执行（在进入节点日志/计时之前），实现方可在此阻塞暂停。</summary>
        void NodeEntering(FlowNode node, FlowContext ctx);

        /// <summary>节点执行结束（含失败/取消），用于维护嵌套深度等状态。</summary>
        void NodeExecuted(FlowNode node, FlowContext ctx);
    }

    /// <summary>
    /// 单步调试控制器（IFlowDebugHooks 的默认实现）。
    /// 引擎线程在每个节点前调用 NodeEntering 并阻塞在恢复闸门（ManualResetEventSlim）上，
    /// UI/宿主线程通过 Step / StepOver / Continue / Stop 放行。
    ///
    /// 步进语义：
    /// - Step（单步/步入）：恢复执行恰好一个节点，在下一个节点前再暂停（会进入 IfElse/ForLoop 的子节点）；
    /// - StepOver（逐过程）：当前暂停在复合节点（Sequence/IfElse/ForLoop）上时，恢复执行其整个子树不再暂停，
    ///   停在同一层（或更浅层）的下一个节点；普通节点等同 Step；
    /// - Continue（继续）：解除所有暂停，自由跑到结束/失败；
    /// - Stop（停止）：只负责放行闸门，实际取消走 FlowContext.CancellationToken 既有路径。
    /// </summary>
    public sealed class FlowDebugController : IFlowDebugHooks
    {
        private readonly object _sync = new object();
        private readonly ManualResetEventSlim _resumeGate = new ManualResetEventSlim(true);
        /// <summary>单步模式：开启时每个节点前都暂停（逐过程的子树跳过除外）。</summary>
        private bool _steppingEnabled;
        /// <summary>逐过程跳过的子树深度：&gt;=0 时深度大于该值的节点不暂停；-1 表示不跳过。</summary>
        private int _stepOverDepth = -1;
        /// <summary>当前执行嵌套深度（根节点为 0，进入节点时推进）。</summary>
        private int _depth;

        /// <summary>是否正暂停在某个节点前（引擎线程被阻塞）。</summary>
        public bool IsPaused { get; private set; }

        /// <summary>当前暂停节点 Id（未暂停时为 null）。</summary>
        public string PausedNodeId { get; private set; }

        /// <summary>当前暂停节点名（未暂停时为 null）。</summary>
        public string PausedNodeName { get; private set; }

        /// <summary>当前暂停节点是否为复合节点（决定 StepOver 是否跳过子树）。</summary>
        public bool PausedNodeIsComposite { get; private set; }

        /// <summary>开启单步模式：下一次 NodeEntering 起逐节点暂停（调试启动时调用，实现“第一步就暂停”）。</summary>
        public void ArmStepping()
        {
            lock (_sync)
            {
                _steppingEnabled = true;
                _stepOverDepth = -1;
            }
        }

        /// <summary>单步（步入）：恢复当前节点执行，并在下一个节点前再次暂停。</summary>
        public void Step()
        {
            lock (_sync)
            {
                _steppingEnabled = true;
                _stepOverDepth = -1;
                _resumeGate.Set();
            }
        }

        /// <summary>逐过程：暂停在复合节点上时跳过其子树，停在同层下一个节点；普通节点等同 Step。</summary>
        public void StepOver()
        {
            lock (_sync)
            {
                _steppingEnabled = true;
                // 暂停在复合节点上：子树（深度更大）内不再暂停；普通节点退化为单步
                _stepOverDepth = IsPaused && PausedNodeIsComposite ? _depth : -1;
                _resumeGate.Set();
            }
        }

        /// <summary>继续：解除所有暂停，自由运行到结束/失败。</summary>
        public void Continue()
        {
            lock (_sync)
            {
                _steppingEnabled = false;
                _stepOverDepth = -1;
                _resumeGate.Set();
            }
        }

        /// <summary>停止：放行闸门让引擎线程脱离阻塞，随后由 CancellationToken 完成取消。</summary>
        public void Stop()
        {
            lock (_sync)
            {
                _resumeGate.Set();
            }
        }

        void IFlowDebugHooks.NodeEntering(FlowNode node, FlowContext ctx)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();

            bool shouldPause;
            lock (_sync)
            {
                shouldPause = _steppingEnabled && (_stepOverDepth < 0 || _depth <= _stepOverDepth);
                if (shouldPause)
                {
                    IsPaused = true;
                    PausedNodeId = node.Id;
                    PausedNodeName = node.Name;
                    PausedNodeIsComposite = node.IsComposite;
                    _resumeGate.Reset();
                }
                else
                {
                    _depth++;
                }
            }
            if (!shouldPause)
            {
                return;
            }

            ctx.AddLog(FlowLogLevel.Info, $"[调试] 暂停于 {node.Name}", node.Id, node.Name);
            ctx.Progress?.Report(new FlowProgress(FlowProgressKind.DebugPaused, node.Id, node.Name));
            try
            {
                // 阻塞等待恢复；暂停期间取消令牌触发时抛出 OCE，走既有取消路径
                _resumeGate.Wait(ctx.CancellationToken);
            }
            catch
            {
                // 取消时未进入执行，深度不推进（对应也不会有 NodeExecuted）
                lock (_sync)
                {
                    IsPaused = false;
                    PausedNodeId = null;
                    PausedNodeName = null;
                    PausedNodeIsComposite = false;
                }
                throw;
            }

            lock (_sync)
            {
                IsPaused = false;
                PausedNodeId = null;
                PausedNodeName = null;
                PausedNodeIsComposite = false;
                _depth++;
            }
            ctx.Progress?.Report(new FlowProgress(FlowProgressKind.DebugResumed, node.Id, node.Name));
        }

        void IFlowDebugHooks.NodeExecuted(FlowNode node, FlowContext ctx)
        {
            lock (_sync)
            {
                _depth--;
            }
        }
    }
}
