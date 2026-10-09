using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Variables;

namespace VisionFlow.Engine
{
    /// <summary>
    /// 一次流程运行的完整结果。
    /// 日志、节点报告与轨迹是运行结束时的稳定快照，后续复用同一上下文再次运行不会改变它们；
    /// Context 中的 HALCON 结果对象遵循资源协议约定有效期（仍被展示时不应提前释放）。
    /// 注意：IsSuccess 表示“流程执行成功”，不等同于视觉业务判定 OK；
    /// 产品是否合格应由上层读取业务输出变量后自行判定。
    /// </summary>
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

        /// <summary>节点报告快照（运行结束时刻的拷贝，不再随后续运行变化）。</summary>
        public IReadOnlyList<NodeRunReport> NodeReports { get; private set; }
        /// <summary>人类可读日志快照。</summary>
        public IReadOnlyList<string> Log { get; private set; }
        /// <summary>结构化日志快照。</summary>
        public IReadOnlyList<FlowLogEntry> StructuredLogs { get; private set; }
        /// <summary>节点执行轨迹快照。</summary>
        public IReadOnlyList<string> Trace { get; private set; }

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
            NodeReports = context != null ? context.NodeReports.ToList().AsReadOnly() : new List<NodeRunReport>().AsReadOnly();
            Log = context != null ? context.Log.ToList().AsReadOnly() : new List<string>().AsReadOnly();
            StructuredLogs = context != null ? context.StructuredLogs.ToList().AsReadOnly() : new List<FlowLogEntry>().AsReadOnly();
            Trace = context != null ? context.Trace.ToList().AsReadOnly() : new List<string>().AsReadOnly();
        }

        /// <summary>流程执行是否成功（不代表视觉业务判定 OK）。</summary>
        public bool IsSuccess
        {
            get { return Status == NodeStatus.Success; }
        }
    }

    /// <summary>
    /// 流程执行引擎：同步执行整棵节点树，返回结果与执行上下文（含变量表、日志、轨迹）。
    ///
    /// 上下文约定：
    /// - 默认每次执行使用独立上下文（Run(root) 重载或调用方自行 new FlowContext）；
    /// - 允许显式复用上下文（如工具预览），复用时变量跨次保留、同名覆盖，
    ///   日志与报告会追加——预览场景应使用 <see cref="FlowContext.CreatePreviewContext"/>
    ///   派生独立上下文，避免污染上一次运行的结果。
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
            if (root != null)
            {
                // 流程锚点（§5.4）：写入根节点实例，供 Flow 作用域资源共享判定"同一流程"。
                ctx.OwnerToken = root;
            }
            var watch = Stopwatch.StartNew();

            string inputError = CheckRequiredInputs(ctx);
            if (inputError != null)
            {
                watch.Stop();
                ctx.AddLog(FlowLogLevel.Error, $"[流程结束] {inputError}，耗时 {watch.ElapsedMilliseconds} ms",
                    root?.Id, root?.Name, FlowErrorCodes.InputMissing, watch.Elapsed);
                return new FlowRunResult(NodeStatus.Failed, inputError, watch.Elapsed, ctx,
                    FlowErrorCodes.InputMissing, FlowErrorSeverity.Error, root?.Id, root?.Name);
            }

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

        /// <summary>运行前检查：必填外部输入缺失或类型不符时返回错误信息，否则返回 null。</summary>
        private static string CheckRequiredInputs(FlowContext ctx)
        {
            foreach (ExternalInputDef input in ExternalInputRegistry.Items.Where(i => i.Required))
            {
                Variable variable;
                if (!ctx.TryGetVariable(input.ModuleName, input.VarName, out variable))
                {
                    return $"缺少必填外部输入 '{input.Path}'（{input.ClrType.Name}），请由上层注入后再运行";
                }
                object value = variable.Value;
                if (value == null)
                {
                    return $"必填外部输入 '{input.Path}' 的值为空，请由上层注入有效值后再运行";
                }
                if (input.ClrType != typeof(object) && !input.ClrType.IsInstanceOfType(value))
                {
                    return $"外部输入 '{input.Path}' 类型为 {value.GetType().Name}，应为 {input.ClrType.Name}";
                }
            }
            return null;
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
