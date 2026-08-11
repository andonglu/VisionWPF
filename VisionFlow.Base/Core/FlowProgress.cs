using System;

namespace VisionFlow.Core
{
    public enum FlowProgressKind
    {
        NodeStarted,
        NodeCompleted,
        NodeFailed,
        FlowCompleted,
        FlowCancelled
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
