using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionFlow.Core
{
    public enum NodeStatus
    {
        Success,
        Failed,
        Skipped
    }

    /// <summary>节点执行结果。Failed 时 Message 携带原因，流程默认出错即停。</summary>
    public sealed class NodeResult
    {
        public NodeStatus Status { get; private set; }
        public string Message { get; private set; }
        public string ErrorCode { get; private set; }
        public FlowErrorSeverity Severity { get; private set; }
        public string NodeId { get; private set; }
        public string NodeName { get; private set; }
        public TimeSpan Duration { get; private set; }

        private NodeResult(NodeStatus status, string message, string errorCode, FlowErrorSeverity severity,
            string nodeId = null, string nodeName = null, TimeSpan? duration = null)
        {
            Status = status;
            Message = message;
            ErrorCode = errorCode;
            Severity = severity;
            NodeId = nodeId;
            NodeName = nodeName;
            Duration = duration ?? TimeSpan.Zero;
        }

        public bool IsSuccess
        {
            get { return Status == NodeStatus.Success; }
        }

        public static readonly NodeResult Ok = new NodeResult(NodeStatus.Success, null, null, FlowErrorSeverity.None);

        public static NodeResult Fail(string message, string errorCode = FlowErrorCodes.NodeFailed,
            FlowErrorSeverity severity = FlowErrorSeverity.Error)
        {
            return new NodeResult(NodeStatus.Failed, message, errorCode, severity);
        }

        public static NodeResult Skip(string message, string errorCode = FlowErrorCodes.NodeSkipped,
            FlowErrorSeverity severity = FlowErrorSeverity.Warning)
        {
            return new NodeResult(NodeStatus.Skipped, message, errorCode, severity);
        }

        public NodeResult WithExecution(string nodeId, string nodeName, TimeSpan duration)
        {
            return new NodeResult(Status, Message, ErrorCode, Severity,
                string.IsNullOrWhiteSpace(NodeId) ? nodeId : NodeId,
                string.IsNullOrWhiteSpace(NodeName) ? nodeName : NodeName,
                duration);
        }
    }

    /// <summary>
    /// 流程节点抽象基类。工具、IfElse、For 循环、顺序容器都是节点，
    /// 分支与循环体本身是子节点列表，因此可以任意嵌套。
    /// 执行方式为同步执行。
    /// </summary>
    public abstract class FlowNode
    {
        public string Id { get; set; }
        public string Name { get; set; }

        protected FlowNode(string name)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = name;
        }

        /// <summary>是否为复合节点（含子树的容器：顺序/分支/循环）。逐过程（StepOver）在复合节点上跳过整棵子树。</summary>
        public virtual bool IsComposite
        {
            get { return false; }
        }

        /// <summary>执行入口：记录轨迹与日志，并把未捕获异常转换为 Failed。</summary>
        public NodeResult Execute(FlowContext ctx)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            // 调试钩子：单步/断点时在此阻塞等待恢复；取消时直接抛 OCE，不进入 try（深度未推进）
            IFlowDebugHooks debugHooks = ctx.DebugHooks;
            debugHooks?.NodeEntering(this, ctx);
            try
            {
                return ExecuteCore(ctx);
            }
            finally
            {
                debugHooks?.NodeExecuted(this, ctx);
            }
        }

        private NodeResult ExecuteCore(FlowContext ctx)
        {
            ctx.Trace.Add(Name);
            ctx.TraceIds.Add(Id);
            ctx.AddLog(FlowLogLevel.Info, $"[开始] {Name}", Id, Name);
            ctx.Progress?.Report(new FlowProgress(FlowProgressKind.NodeStarted, Id, Name));

            var watch = Stopwatch.StartNew();
            NodeResult result;
            try
            {
                result = OnExecute(ctx) ?? NodeResult.Ok;
            }
            catch (OperationCanceledException)
            {
                watch.Stop();
                ctx.AddLog(FlowLogLevel.Warning, $"[取消] {Name}，耗时 {watch.ElapsedMilliseconds} ms",
                    Id, Name, FlowErrorCodes.FlowCancelled, watch.Elapsed);
                ctx.NodeReports.Add(new NodeRunReport
                {
                    NodeId = Id,
                    NodeName = Name,
                    Status = NodeStatus.Skipped,
                    ErrorCode = FlowErrorCodes.FlowCancelled,
                    Severity = FlowErrorSeverity.Warning,
                    Message = "流程已取消",
                    Duration = watch.Elapsed
                });
                ctx.Progress?.Report(new FlowProgress(FlowProgressKind.FlowCancelled, Id, Name,
                    NodeStatus.Skipped, "流程已取消", watch.Elapsed, FlowErrorCodes.FlowCancelled, FlowErrorSeverity.Warning));
                throw;
            }
            catch (Exception ex)
            {
                result = NodeResult.Fail($"{Name} 执行异常：{ex.Message}", FlowErrorCodes.NodeException, FlowErrorSeverity.Fatal);
            }

            watch.Stop();
            result = result.WithExecution(Id, Name, watch.Elapsed);
            ctx.AddLog(result.IsSuccess ? FlowLogLevel.Info : FlowLogLevel.Error,
                result.IsSuccess
                ? $"[完成] {Name}，耗时 {watch.ElapsedMilliseconds} ms"
                : $"[失败] {Name}：{result.Message}，耗时 {watch.ElapsedMilliseconds} ms",
                Id, Name, result.ErrorCode, watch.Elapsed);
            ctx.NodeReports.Add(new NodeRunReport
            {
                NodeId = Id,
                NodeName = Name,
                Status = result.Status,
                ErrorCode = result.ErrorCode,
                Severity = result.Severity,
                Message = result.Message,
                Duration = watch.Elapsed
            });
            ctx.Progress?.Report(new FlowProgress(result.IsSuccess ? FlowProgressKind.NodeCompleted : FlowProgressKind.NodeFailed,
                Id, Name, result.Status, result.Message, watch.Elapsed, result.ErrorCode, result.Severity));
            return result;
        }

        protected abstract NodeResult OnExecute(FlowContext ctx);

        /// <summary>顺序执行一组子节点，任一失败即中断并向上传播。</summary>
        protected static NodeResult RunChildren(IEnumerable<FlowNode> children, FlowContext ctx)
        {
            foreach (FlowNode node in children)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                NodeResult result = node.Execute(ctx);
                if (!result.IsSuccess)
                {
                    return result;
                }
            }
            return NodeResult.Ok;
        }
    }
}
