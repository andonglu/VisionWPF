using System;
using System.Threading;
using System.Threading.Tasks;
using VisionFlow.Core;
using VisionFlow.Engine;

namespace VisionFlow.Runtime
{
    /// <summary>
    /// VisionFlow 对外执行入口。同步入口用于设备项目后台流程线程；
    /// 异步入口用于编辑器 UI、调试界面和需要取消/进度的场景。
    /// </summary>
    public interface IVisionFlowRuntime
    {
        FlowRunResult Run(FlowNode root, FlowContext context);

        Task<FlowRunResult> RunAsync(FlowNode root, FlowContext context,
            CancellationToken cancellationToken, IProgress<FlowProgress> progress);
    }
}
