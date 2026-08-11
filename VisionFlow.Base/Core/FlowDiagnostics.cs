using System;
using System.Collections.Generic;

namespace VisionFlow.Core
{
    public enum FlowLogLevel
    {
        Info,
        Warning,
        Error
    }

    public enum FlowErrorSeverity
    {
        None,
        Warning,
        Error,
        Fatal
    }

    public static class FlowErrorCodes
    {
        public const string None = "";
        public const string NodeFailed = "NODE_FAILED";
        public const string NodeException = "NODE_EXCEPTION";
        public const string NodeSkipped = "NODE_SKIPPED";
        public const string FlowCancelled = "FLOW_CANCELLED";
        public const string ValidationFailed = "VALIDATION_FAILED";
        public const string ReferenceInvalid = "REFERENCE_INVALID";
        public const string InputMissing = "INPUT_MISSING";
        public const string ToolFailed = "TOOL_FAILED";
    }

    public sealed class FlowLogEntry
    {
        public DateTime Time { get; private set; }
        public FlowLogLevel Level { get; private set; }
        public string NodeId { get; private set; }
        public string NodeName { get; private set; }
        public string ErrorCode { get; private set; }
        public string Message { get; private set; }
        public TimeSpan? Duration { get; private set; }

        public FlowLogEntry(FlowLogLevel level, string message, string nodeId = null, string nodeName = null,
            string errorCode = null, TimeSpan? duration = null)
        {
            Time = DateTime.Now;
            Level = level;
            Message = message;
            NodeId = nodeId;
            NodeName = nodeName;
            ErrorCode = errorCode;
            Duration = duration;
        }
    }

    public sealed class NodeRunReport
    {
        public string NodeId { get; set; }
        public string NodeName { get; set; }
        public NodeStatus Status { get; set; }
        public string ErrorCode { get; set; }
        public FlowErrorSeverity Severity { get; set; }
        public string Message { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
