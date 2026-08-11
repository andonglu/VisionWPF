using System;
using System.Threading;
using System.Threading.Tasks;
using VisionFlow.Core;
using VisionFlow.Engine;

namespace VisionFlow.Runtime
{
    /// <summary>默认运行时实现：同步/异步入口共用同一个 FlowEngine。</summary>
    public sealed class VisionFlowRuntime : IVisionFlowRuntime
    {
        private readonly FlowEngine _engine = new FlowEngine();

        public FlowRunResult Run(FlowNode root, FlowContext context)
        {
            return _engine.Run(root, context);
        }

        public Task<FlowRunResult> RunAsync(FlowNode root, FlowContext context,
            CancellationToken cancellationToken, IProgress<FlowProgress> progress)
        {
            return _engine.RunAsync(root, context, cancellationToken, progress);
        }
    }
}
