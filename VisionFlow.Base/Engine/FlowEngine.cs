using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisionFlow.Core;

namespace VisionFlow.Engine
{
    /// <summary>一次流程运行的完整结果。</summary>
    public sealed class FlowRunResult
    {
        public NodeStatus Status { get; private set; }
        public string Message { get; private set; }
        public string ErrorCode { get; private set; }
        public FlowErrorSeverity Severity { get; private set; }
        public string FailedNodeId { get; private set; }
        public string FailedNodeName { get; private set; }
        public TimeSpan Duration { get; private set; }
        public FlowContext Context { get; private set; }
        public IReadOnlyList<NodeRunReport> NodeReports { get; private set; }

        public FlowRunResult(NodeStatus status, string message, TimeSpan duration, FlowContext context,
            string errorCode = null, FlowErrorSeverity severity = FlowErrorSeverity.None,
            string failedNodeId = null, string failedNodeName = null)
        {
            Status = status;
            Message = message;
            Duration = duration;
            Context = context;
            ErrorCode = errorCode;
            Severity = severity;
            FailedNodeId = failedNodeId;
            FailedNodeName = failedNodeName;
            NodeReports = context?.NodeReports?.AsReadOnly() ?? new List<NodeRunReport>().AsReadOnly();
        }

        public bool IsSuccess
        {
            get { return Status == NodeStatus.Success; }
        }
    }

    /// <summary>
    /// 流程执行引擎：同步执行整棵节点树，返回结果与执行上下文（含变量表、日志、轨迹）。
    /// </summary>
    public sealed class FlowEngine
    {
        public FlowRunResult Run(FlowNode root)
        {
            return Run(root, new FlowContext());
        }

        public FlowRunResult Run(FlowNode root, FlowContext initialContext)
        {
            var ctx = initialContext ?? new FlowContext();
            var watch = Stopwatch.StartNew();

            NodeResult result;
            try
            {
                result = root.Execute(ctx);
            }
            catch (OperationCanceledException)
            {
                watch.Stop();
                ctx.AddLog(FlowLogLevel.Warning, $"[流程结束] 已取消，耗时 {watch.ElapsedMilliseconds} ms",
                    root?.Id, root?.Name, FlowErrorCodes.FlowCancelled, watch.Elapsed);
                ctx.Progress?.Report(new FlowProgress(FlowProgressKind.FlowCancelled, root?.Id, root?.Name,
                    NodeStatus.Skipped, "流程已取消", watch.Elapsed, FlowErrorCodes.FlowCancelled, FlowErrorSeverity.Warning));
                return new FlowRunResult(NodeStatus.Skipped, "流程已取消", watch.Elapsed, ctx,
                    FlowErrorCodes.FlowCancelled, FlowErrorSeverity.Warning, root?.Id, root?.Name);
            }

            watch.Stop();
            ctx.AddLog(result.IsSuccess ? FlowLogLevel.Info : FlowLogLevel.Error,
                result.IsSuccess
                ? $"[流程结束] 成功，耗时 {watch.ElapsedMilliseconds} ms"
                : $"[流程结束] 失败：{result.Message}，耗时 {watch.ElapsedMilliseconds} ms",
                result.NodeId ?? root.Id, result.NodeName ?? root.Name, result.ErrorCode, watch.Elapsed);
            ctx.Progress?.Report(new FlowProgress(FlowProgressKind.FlowCompleted, root.Id, root.Name,
                result.Status, result.Message, watch.Elapsed, result.ErrorCode, result.Severity));

            NodeRunReport failedReport = ctx.NodeReports.FirstOrDefault(r => r.Status == NodeStatus.Failed);
            return new FlowRunResult(result.Status, result.Message, watch.Elapsed, ctx,
                result.ErrorCode, result.Severity,
                result.NodeId ?? failedReport?.NodeId,
                result.NodeName ?? failedReport?.NodeName);
        }

        public Task<FlowRunResult> RunAsync(FlowNode root, FlowContext initialContext,
            CancellationToken cancellationToken, IProgress<FlowProgress> progress)
        {
            var ctx = initialContext ?? new FlowContext();
            ctx.CancellationToken = cancellationToken;
            ctx.Progress = progress;
            return Task.Run(() => Run(root, ctx), cancellationToken);
        }
    }
}
