using System;

namespace VisionFlow.Core
{
    public enum FlowProgressKind
    {
        NodeStarted,
        NodeCompleted,
        NodeFailed,
        FlowCompleted,
        FlowCancelled,
        /// <summary>单步调试：引擎在节点前暂停，携带节点 Id。</summary>
        DebugPaused,
        /// <summary>单步调试：暂停被解除、节点继续执行，携带节点 Id。</summary>
        DebugResumed
    }

    public sealed class FlowProgress
    {
        public FlowProgressKind Kind { get; private set; }
        public string NodeId { get; private set; }
        public string NodeName { get; private set; }
        public NodeStatus? Status { get; private set; }
        public string Message { get; private set; }
        public TimeSpan? Duration { get; private set; }
        public string ErrorCode { get; private set; }
        public FlowErrorSeverity Severity { get; private set; }

        public FlowProgress(FlowProgressKind kind, string nodeId, string nodeName,
            NodeStatus? status = null, string message = null, TimeSpan? duration = null,
            string errorCode = null, FlowErrorSeverity severity = FlowErrorSeverity.None)
        {
            Kind = kind;
            NodeId = nodeId;
            NodeName = nodeName;
            Status = status;
            Message = message;
            Duration = duration;
            ErrorCode = errorCode;
            Severity = severity;
        }
    }
}
