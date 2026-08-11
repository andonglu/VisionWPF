using System;
using System.Collections.Generic;
using System.Threading;
using VisionFlow.Variables;

namespace VisionFlow.Core
{
    /// <summary>
    /// 循环帧：进入循环时压栈，退出时弹栈。
    /// 嵌套循环中 Loop.Index / Loop.Current 始终指向最内层。
    /// </summary>
    public sealed class LoopFrame
    {
        public int Index { get; private set; }
        public int Count { get; private set; }
        public object CurrentItem { get; private set; }

        public LoopFrame(int index, int count, object currentItem)
        {
            Index = index;
            Count = count;
            CurrentItem = currentItem;
        }
    }

    /// <summary>
    /// 流程执行上下文。变量为全局作用域：任何节点写入的变量，
    /// 后续所有节点（包括分支、循环内外）都可见。
    /// </summary>
    public sealed class FlowContext
    {
        private readonly Dictionary<string, Variable> _variables = new Dictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<LoopFrame> _loopStack = new Stack<LoopFrame>();

        public CancellationToken CancellationToken { get; set; }
        public IProgress<FlowProgress> Progress { get; set; }

        /// <summary>执行日志（人类可读）。</summary>
        public List<string> Log { get; } = new List<string>();

        /// <summary>结构化执行日志。</summary>
        public List<FlowLogEntry> StructuredLogs { get; } = new List<FlowLogEntry>();

        /// <summary>每个节点的运行报告。</summary>
        public List<NodeRunReport> NodeReports { get; } = new List<NodeRunReport>();

        /// <summary>节点执行轨迹（按执行顺序记录节点名，供断言与调试）。</summary>
        public List<string> Trace { get; } = new List<string>();

        /// <summary>节点执行轨迹（按执行顺序记录节点 ID，供 UI 精确定位）。</summary>
        public List<string> TraceIds { get; } = new List<string>();

        public LoopFrame CurrentLoop
        {
            get { return _loopStack.Count > 0 ? _loopStack.Peek() : null; }
        }

        public void PushLoop(LoopFrame frame)
        {
            _loopStack.Push(frame);
        }

        public void PopLoop()
        {
            _loopStack.Pop();
        }

        /// <summary>写入（或覆盖）一个变量。</summary>
        public void SetVariable(Variable variable)
        {
            _variables[Key(variable.ModuleName, variable.Name)] = variable;
        }

        /// <summary>按 模块名.变量名 读取变量，不存在时抛出带中文说明的异常。</summary>
        public Variable GetVariable(string moduleName, string name)
        {
            Variable variable;
            if (!_variables.TryGetValue(Key(moduleName, name), out variable))
            {
                throw new KeyNotFoundException($"找不到变量 '{moduleName}.{name}'，请确认上游工具已执行并输出该变量");
            }
            return variable;
        }

        public bool TryGetVariable(string moduleName, string name, out Variable variable)
        {
            return _variables.TryGetValue(Key(moduleName, name), out variable);
        }

        /// <summary>枚举当前上下文中的全部变量（供结果展示）。</summary>
        public IEnumerable<Variable> GetAllVariables()
        {
            return _variables.Values;
        }

        public void AddLog(FlowLogLevel level, string message, string nodeId = null, string nodeName = null,
            string errorCode = null, TimeSpan? duration = null)
        {
            Log.Add(message);
            StructuredLogs.Add(new FlowLogEntry(level, message, nodeId, nodeName, errorCode, duration));
        }

        private static string Key(string moduleName, string name)
        {
            return moduleName + "." + name;
        }
    }
}
