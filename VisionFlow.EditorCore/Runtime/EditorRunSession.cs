using System;
using System.Threading;
using System.Threading.Tasks;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Engine;
using VisionFlow.Variables;

namespace VisionFlow.Runtime
{
    public enum EditorRunOutcomeKind
    {
        /// <summary>已有运行进行中，本次请求被忽略。</summary>
        AlreadyRunning,
        /// <summary>运行在开始执行前被取消（未产生结果）。</summary>
        Cancelled,
        /// <summary>运行抛出未处理异常（未产生结果）。</summary>
        Error,
        /// <summary>运行结束并产生结果（成功/失败/取消跳过均算完成）。</summary>
        Completed
    }

    /// <summary>一次编辑器运行的归宿：结果、取消或异常三态，便于 UI 分支处理。</summary>
    public sealed class EditorRunOutcome
    {
        public EditorRunOutcomeKind Kind { get; private set; }
        public FlowRunResult Result { get; private set; }
        public Exception Error { get; private set; }

        public static readonly EditorRunOutcome AlreadyRunning = new EditorRunOutcome { Kind = EditorRunOutcomeKind.AlreadyRunning };
        public static readonly EditorRunOutcome Cancelled = new EditorRunOutcome { Kind = EditorRunOutcomeKind.Cancelled };

        public static EditorRunOutcome FromError(Exception error)
        {
            return new EditorRunOutcome { Kind = EditorRunOutcomeKind.Error, Error = error };
        }

        public static EditorRunOutcome FromResult(FlowRunResult result)
        {
            return new EditorRunOutcome { Kind = EditorRunOutcomeKind.Completed, Result = result };
        }
    }

    /// <summary>
    /// 编辑器运行会话（VF-08 从 MainWindow 平移）：持有取消源、上次运行上下文与运行专用输入图像副本。
    /// 只依赖运行时抽象，不含 UI；状态与进度通过事件外发。
    ///
    /// 资源协议（VF-04）：
    /// - 每次运行为输入图像做 CopyObj 副本，结果上下文持有副本，换图不影响上次运行结果；
    /// - 新结果替换旧结果时先完成交接，再释放旧上下文与旧副本；
    /// - 未产生结果（异常/启动前取消）时本次上下文与副本直接回收。
    /// </summary>
    public sealed class EditorRunSession
    {
        private readonly IVisionFlowRuntime _runtime;
        private CancellationTokenSource _runCts;
        private FlowContext _lastRunContext;
        /// <summary>当前调试会话的控制器（非调试运行为 null）。</summary>
        private FlowDebugController _debugController;
        /// <summary>当前正在运行（含暂停中）的实时上下文，供暂停时读取变量/日志。</summary>
        private FlowContext _activeContext;
        /// <summary>本次运行专用的输入图像副本（CopyObj），避免运行结果引用被换图释放的对象。</summary>
        private HObject _runImage;

        public EditorRunSession(IVisionFlowRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        /// <summary>运行状态变化（true=开始，false=结束），在调用方上下文中触发。</summary>
        public event Action<bool> RunningChanged;

        /// <summary>运行进度（经 Progress&lt;T&gt; 编排到订阅方上下文）。</summary>
        public event Action<FlowProgress> Progressed;

        /// <summary>是否正在运行。运行期间结构编辑、参数修改、流程切换与换图应锁定。</summary>
        public bool IsRunning
        {
            get { return _runCts != null; }
        }

        /// <summary>上次运行结果上下文（仍归会话所有，调用方只读使用）。</summary>
        public FlowContext LastRunContext
        {
            get { return _lastRunContext; }
        }

        /// <summary>当前运行的实时上下文（运行中/暂停中可读，运行结束或失败时为 null）。</summary>
        public FlowContext ActiveContext
        {
            get { return _activeContext; }
        }

        /// <summary>调试会话是否正暂停在某个节点前。</summary>
        public bool IsPaused
        {
            get { return _debugController != null && _debugController.IsPaused; }
        }

        /// <summary>当前暂停节点 Id（未暂停时为 null）。</summary>
        public string PausedNodeId
        {
            get { return _debugController?.PausedNodeId; }
        }

        /// <summary>当前暂停节点名（未暂停时为 null）。</summary>
        public string PausedNodeName
        {
            get { return _debugController?.PausedNodeName; }
        }

        /// <summary>请求停止当前运行；无运行时为空操作。调试暂停中会先放行闸门再由取消令牌中止。</summary>
        public void Stop()
        {
            _debugController?.Stop();
            _runCts?.Cancel();
        }

        /// <summary>单步（步入）：恢复执行恰好一个节点，在下一个节点前再暂停。</summary>
        public void Step()
        {
            _debugController?.Step();
        }

        /// <summary>逐过程：暂停在复合节点上时执行整个子树不再暂停；普通节点等同单步。</summary>
        public void StepOver()
        {
            _debugController?.StepOver();
        }

        /// <summary>继续：解除所有暂停，自由运行到结束/失败。</summary>
        public void Continue()
        {
            _debugController?.Continue();
        }

        /// <summary>丢弃上次运行结果并回收其 HALCON 资源（旧结果替换后资源可回收）。</summary>
        public void DiscardLastRunResult()
        {
            _lastRunContext?.Dispose();
            _lastRunContext = null;
            _runImage?.Dispose();
            _runImage = null;
        }

        /// <summary>
        /// 执行一次运行。重复调用（运行中）返回 AlreadyRunning；
        /// 成功结束时用新结果替换旧结果并释放旧资源。
        /// </summary>
        public Task<EditorRunOutcome> RunAsync(FlowNode root, HObject inputImage, string inputImagePath)
        {
            return RunCoreAsync(root, inputImage, inputImagePath, enableDebug: false);
        }

        /// <summary>
        /// 以调试方式启动：第一步就暂停在第一个节点前，之后由 Step / StepOver / Continue / Stop 控制。
        /// 暂停时可通过 IsPaused / PausedNodeId / ActiveContext 观察现场。
        /// </summary>
        public Task<EditorRunOutcome> StartDebugAsync(FlowNode root, HObject inputImage, string inputImagePath)
        {
            return RunCoreAsync(root, inputImage, inputImagePath, enableDebug: true);
        }

        private async Task<EditorRunOutcome> RunCoreAsync(FlowNode root, HObject inputImage, string inputImagePath, bool enableDebug)
        {
            if (_runCts != null)
            {
                return EditorRunOutcome.AlreadyRunning;
            }

            _runCts = new CancellationTokenSource();
            RunningChanged?.Invoke(true);
            FlowRunResult result = null;
            var context = new FlowContext();
            if (enableDebug)
            {
                // 调试会话：控制器挂到上下文，引擎在每个节点边界回调；第一步即暂停
                _debugController = new FlowDebugController();
                _debugController.ArmStepping();
                context.DebugHooks = _debugController;
            }
            _activeContext = context;
            HObject newRunImage = null;
            try
            {
                if (inputImage != null && inputImage.IsInitialized())
                {
                    // 为本次运行复制输入图像：运行结果（含 LastRunContext）持有副本，
                    // 之后换图释放原图不会影响本次运行的变量与结果显示。
                    // 副本在运行结束替换结果时交接给 _runImage；未产生结果时随上下文一并回收。
                    HOperatorSet.CopyObj(inputImage, out newRunImage, 1, -1);
                    context.SetVariable(Variable.Object("Input", "Image", new HalconImage(newRunImage), 1));
                    context.AddLog(FlowLogLevel.Info, string.IsNullOrEmpty(inputImagePath)
                        ? "[输入] Input.Image"
                        : "[输入] Input.Image = " + inputImagePath);
                }
                result = await _runtime.RunAsync(root, context, _runCts.Token,
                    new Progress<FlowProgress>(p => Progressed?.Invoke(p)));
            }
            catch (OperationCanceledException)
            {
                // 未产生结果（取消）：本次上下文与图像副本不进入展示，直接回收（VF-04）
                context.Dispose();
                newRunImage?.Dispose();
                return EditorRunOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                context.Dispose();
                newRunImage?.Dispose();
                return EditorRunOutcome.FromError(ex);
            }
            finally
            {
                _debugController = null;
                _activeContext = null;
                _runCts?.Dispose();
                _runCts = null;
                RunningChanged?.Invoke(false);
            }

            // 用新结果替换旧结果：先完成交接，再回收旧资源（结果仍在展示时不得提前释放）
            FlowContext previousContext = _lastRunContext;
            HObject previousRunImage = _runImage;
            _lastRunContext = result.Context;
            _runImage = newRunImage;
            previousContext?.Dispose();
            previousRunImage?.Dispose();
            return EditorRunOutcome.FromResult(result);
        }
    }
}
