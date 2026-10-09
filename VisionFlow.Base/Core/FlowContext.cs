using System;
using System.Collections.Generic;
using System.Threading;
using HalconDotNet;
using VisionFlow.Variables;

namespace VisionFlow.Core
{
    /// <summary>
    /// 循环控制信号：跳出循环 / 跳过本次节点设置后，顺序执行的子节点立即停止，
    /// 由最内层循环读取并清除。
    /// </summary>
    public enum LoopControlSignal
    {
        None,
        Break,
        Continue
    }

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
    ///
    /// 资源协议（VF-04）：
    /// - 调用方注入的输入变量（如 Input.Image）必须以借用包装注入，上下文释放时不予处置；
    /// - 工具输出经 SetOutput/Owned 归本次运行所有；变量表中的裸 HObject 一律视为本次运行产物；
    /// - 资源独立于变量表跟踪，覆盖变量不会遗失旧资源，同一底层对象按引用去重；
    /// - 预览借用源上下文资源，源上下文必须在预览结束后才能释放；
    /// - 结果仍在界面展示时不得调用 Dispose；由宿主在结果被替换或窗口关闭时调用。
    /// </summary>
    public sealed class FlowContext : IDisposable
    {
        private readonly Dictionary<string, Variable> _variables = new Dictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<LoopFrame> _loopStack = new Stack<LoopFrame>();
        private readonly Dictionary<HObject, IDisposable> _ownedResources =
            new Dictionary<HObject, IDisposable>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<HObject> _borrowedResources =
            new HashSet<HObject>(ReferenceEqualityComparer.Instance);
        private bool _disposed;

        public CancellationToken CancellationToken { get; set; }
        public IProgress<FlowProgress> Progress { get; set; }
        /// <summary>
        /// 调试钩子（如 <see cref="FlowDebugController"/>），默认 null 表示不调试。
        /// 预览上下文不继承该钩子，工具预览永不暂停。
        /// </summary>
        public IFlowDebugHooks DebugHooks { get; set; }
        public bool IsPreview { get; private set; }
        public bool AllowMatrixFallback { get; private set; }

        /// <summary>
        /// 流程锚点（§5.4）：本次运行所属流程树的根节点实例（引用相等判定），
        /// 供 Flow 作用域的模型句柄共享确定"同一流程"。
        /// 由 <see cref="FlowEngine"/> 在运行前写入；预览上下文经 <see cref="CreatePreviewContext"/> 复制继承；
        /// 仅作弱标识引用，上下文生命周期结束即失效。
        /// </summary>
        public object OwnerToken { get; set; }

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

        /// <summary>待处理的循环控制信号（由跳出循环 / 跳过本次节点设置，最内层循环消费）。</summary>
        public LoopControlSignal LoopControl { get; set; }

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
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(variable);
            string key = Key(variable.ModuleName, variable.Name);
            if (_variables.TryGetValue(key, out Variable previous))
            {
                TrackResources(previous.Value);
            }
            TrackResources(variable.Value);
            _variables[key] = variable;
        }

        internal void SetOwnedVariable(Variable variable)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(variable);
            HalconOwnership.Adopt(variable, obj => !_borrowedResources.Contains(obj));
            SetVariable(variable);
        }

        /// <summary>子流程嵌套深度（顶层流程为 0），用于防止循环引用导致的无限递归。</summary>
        internal int SubFlowDepth { get; set; }

        /// <summary>写入借用的变量：其中的 HALCON 资源归别的上下文所有，本上下文释放时不处置（子流程输入）。</summary>
        internal void SetBorrowedVariable(Variable variable)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(variable);
            foreach (HObject obj in CollectHalconObjects(variable.Value))
            {
                if (!_ownedResources.ContainsKey(obj))
                {
                    _borrowedResources.Add(obj);
                }
            }
            SetVariable(variable);
        }

        /// <summary>
        /// 把值中的 HALCON 资源移出本上下文的所有权（改为借用），本上下文释放时不再处置；
        /// 子流程把输出交给父上下文前调用，所有权随后由父上下文接管。
        /// </summary>
        internal void ReleaseOwnership(object value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            TrackResources(value);
            foreach (HObject obj in CollectHalconObjects(value))
            {
                if (_ownedResources.Remove(obj))
                {
                    _borrowedResources.Add(obj);
                }
            }
        }

        private static List<HObject> CollectHalconObjects(object value)
        {
            var result = new List<HObject>();
            CollectHalconObjects(value, result, new HashSet<object>(ReferenceEqualityComparer.Instance));
            return result;
        }

        private static void CollectHalconObjects(object value, List<HObject> result, HashSet<object> visited)
        {
            if (value == null || value is string || !visited.Add(value))
            {
                return;
            }
            switch (value)
            {
                case HalconImage image:
                    AddObject(image.Object, result);
                    return;
                case HalconRegion region:
                    AddObject(region.Object, result);
                    return;
                case HalconXld xld:
                    AddObject(xld.Object, result);
                    return;
                case HObject obj:
                    AddObject(obj, result);
                    return;
                case IHalconResourceContainer container:
                    foreach (HObject owned in container.OwnedHalconObjects)
                    {
                        AddObject(owned, result);
                    }
                    return;
                case System.Collections.IDictionary dictionary:
                    foreach (object item in dictionary.Values)
                    {
                        CollectHalconObjects(item, result, visited);
                    }
                    return;
                case System.Collections.IEnumerable enumerable:
                    foreach (object item in enumerable)
                    {
                        CollectHalconObjects(item, result, visited);
                    }
                    return;
            }
        }

        private static void AddObject(HObject obj, List<HObject> result)
        {
            if (obj != null)
            {
                result.Add(obj);
            }
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

        /// <summary>
        /// 派生预览上下文：复制可变集合，HALCON 资源保持借用，运行记录独立。
        /// 自定义引用类型结果应保持只读，或实现 ICloneable 提供预览副本。
        /// 降级权限只属于本次预览，不会保存到工具配置。
        /// </summary>
        public FlowContext CreatePreviewContext(bool allowMatrixFallback = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (Variable variable in _variables.Values)
            {
                TrackResources(variable.Value);
            }
            var ctx = new FlowContext
            {
                IsPreview = true,
                AllowMatrixFallback = allowMatrixFallback,
                OwnerToken = OwnerToken
            };
            ctx._borrowedResources.UnionWith(_borrowedResources);
            ctx._borrowedResources.UnionWith(_ownedResources.Keys);
            var copies = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
            bool completed = false;
            try
            {
                foreach (Variable variable in _variables.Values)
                {
                    ctx.SetVariable(variable.CopyForPreview(copies));
                }
                foreach (LoopFrame frame in ReverseLoopFrames())
                {
                    ctx.PushLoop(new LoopFrame(frame.Index, frame.Count,
                        PreviewValueCopy.Copy(frame.CurrentItem, copies)));
                }
                completed = true;
                return ctx;
            }
            finally
            {
                if (!completed)
                {
                    ctx.Dispose();
                }
            }
        }

        private IEnumerable<LoopFrame> ReverseLoopFrames()
        {
            LoopFrame[] frames = _loopStack.ToArray();
            for (int i = frames.Length - 1; i >= 0; i--)
            {
                yield return frames[i];
            }
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

        /// <summary>
        /// 释放本次运行拥有的全部 HALCON 资源（包括被覆盖的输出，幂等）。
        /// 预览的共享资源按底层对象识别，改名、别名和集合包装不会改变借用关系。
        /// 结果仍在展示时不得调用。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            foreach (Variable variable in _variables.Values)
            {
                TrackResources(variable.Value);
            }
            _disposed = true;
            foreach (IDisposable resource in _ownedResources.Values)
            {
                resource.Dispose();
            }
            _ownedResources.Clear();
            _borrowedResources.Clear();
        }

        private void TrackResources(object value)
        {
            TrackResources(value, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private void TrackResources(object value, HashSet<object> visited)
        {
            if (value == null || value is string || !visited.Add(value))
            {
                return;
            }
            switch (value)
            {
                case HalconImage image:
                    TrackResource(image.Object, image.OwnsObject ? image : null);
                    return;
                case HalconRegion region:
                    TrackResource(region.Object, region.OwnsObject ? region : null);
                    return;
                case HalconXld xld:
                    TrackResource(xld.Object, xld.OwnsObject ? xld : null);
                    return;
                case HObject obj:
                    TrackResource(obj, obj);
                    return;
                case IHalconResourceContainer container:
                    foreach (HObject owned in container.OwnedHalconObjects)
                    {
                        TrackResource(owned, owned);
                    }
                    return;
                case System.Collections.IDictionary dictionary:
                    foreach (object item in dictionary.Values)
                    {
                        TrackResources(item, visited);
                    }
                    return;
                case System.Collections.IEnumerable enumerable:
                    foreach (object item in enumerable)
                    {
                        TrackResources(item, visited);
                    }
                    return;
            }
        }

        private void TrackResource(HObject obj, IDisposable owner)
        {
            if (obj == null || _borrowedResources.Contains(obj) || _ownedResources.ContainsKey(obj))
            {
                return;
            }
            if (owner == null)
            {
                _borrowedResources.Add(obj);
            }
            else
            {
                _ownedResources.Add(obj, owner);
            }
        }
    }
}
