using VisionFlow.Core;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;

namespace VisionFlow.Tests;

/// <summary>EditorRunSession（VF-08 从 MainWindow 平移）的行为测试：重入、取消、异常与上下文资源交接。</summary>
public class EditorRunSessionTests
{
    /// <summary>可控桩运行时：按注入的行为执行，便于确定性地模拟阻塞、取消与异常。</summary>
    private sealed class StubRuntime : IVisionFlowRuntime
    {
        private readonly Func<FlowNode, FlowContext, CancellationToken, FlowRunResult> _behavior;

        public StubRuntime(Func<FlowNode, FlowContext, CancellationToken, FlowRunResult> behavior)
        {
            _behavior = behavior;
        }

        public FlowRunResult Run(FlowNode root, FlowContext context)
        {
            return _behavior(root, context, CancellationToken.None);
        }

        public Task<FlowRunResult> RunAsync(FlowNode root, FlowContext context,
            CancellationToken cancellationToken, IProgress<FlowProgress> progress)
        {
            context.CancellationToken = cancellationToken;
            context.Progress = progress;
            return Task.Run(() => _behavior(root, context, cancellationToken), cancellationToken);
        }
    }

    private static SequenceNode CreateFlow(params ToolBase[] tools)
    {
        var root = new SequenceNode("主流程");
        foreach (ToolBase tool in tools)
        {
            root.Children.Add(new ToolNode(tool));
        }
        return root;
    }

    private static FlowRunResult SuccessResult(FlowNode root, FlowContext ctx)
    {
        return new FlowRunResult(NodeStatus.Success, null, TimeSpan.Zero, ctx,
            null, FlowErrorSeverity.None, null, null);
    }

    [Fact]
    public async Task RunAsync_CompletesAndOwnsResultContext()
    {
        var runtime = new VisionFlowRuntime();
        var session = new EditorRunSession(runtime);
        var runningChanges = new List<bool>();
        session.RunningChanged += running => runningChanges.Add(running);

        EditorRunOutcome outcome = await session.RunAsync(
            CreateFlow(new MockMatchTool("匹配", 2, 0.9)), null, null);

        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        Assert.False(session.IsRunning);
        Assert.Same(outcome.Result.Context, session.LastRunContext);
        Assert.Equal(new[] { true, false }, runningChanges);
        // 结果上下文归会话所有，可读取变量
        Assert.True(session.LastRunContext.TryGetVariable("匹配", "MatchCount", out _));

        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task RunAsync_WhileRunning_ReturnsAlreadyRunning()
    {
        var gate = new ManualResetEventSlim(false);
        var runtime = new StubRuntime((root, ctx, ct) =>
        {
            gate.Wait(ct);
            return SuccessResult(root, ctx);
        });
        var session = new EditorRunSession(runtime);

        Task<EditorRunOutcome> first = session.RunAsync(CreateFlow(), null, null);
        // 等第一次运行进入执行体
        Assert.True(SpinWait.SpinUntil(() => session.IsRunning, TimeSpan.FromSeconds(5)));

        EditorRunOutcome second = await session.RunAsync(CreateFlow(), null, null);
        Assert.Equal(EditorRunOutcomeKind.AlreadyRunning, second.Kind);

        gate.Set();
        EditorRunOutcome firstOutcome = await first;
        Assert.Equal(EditorRunOutcomeKind.Completed, firstOutcome.Kind);
        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task Stop_DuringRun_ProducesCancelledOutcomeAndCleansUp()
    {
        var runtime = new StubRuntime((root, ctx, ct) =>
        {
            // 等测试线程叫停，再以 OCE 中止（令牌已取消 → Task 进入 Canceled）
            Assert.True(SpinWait.SpinUntil(() => ct.IsCancellationRequested, TimeSpan.FromSeconds(5)));
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("不可达");
        });
        var session = new EditorRunSession(runtime);

        Task<EditorRunOutcome> run = session.RunAsync(CreateFlow(), null, null);
        Assert.True(SpinWait.SpinUntil(() => session.IsRunning, TimeSpan.FromSeconds(5)));
        session.Stop();

        EditorRunOutcome outcome = await run;
        Assert.Equal(EditorRunOutcomeKind.Cancelled, outcome.Kind);
        Assert.False(session.IsRunning);
        Assert.Null(session.LastRunContext);
    }

    [Fact]
    public async Task RunAsync_RuntimeThrows_ReturnsErrorAndKeepsNoContext()
    {
        var runtime = new StubRuntime((root, ctx, ct) => throw new InvalidOperationException("爆炸"));
        var session = new EditorRunSession(runtime);

        EditorRunOutcome outcome = await session.RunAsync(CreateFlow(), null, null);

        Assert.Equal(EditorRunOutcomeKind.Error, outcome.Kind);
        Assert.IsType<InvalidOperationException>(outcome.Error);
        Assert.False(session.IsRunning);
        Assert.Null(session.LastRunContext);
    }

    [Fact]
    public async Task SecondRun_ReplacesAndDisposesPreviousContext()
    {
        var runtime = new VisionFlowRuntime();
        var session = new EditorRunSession(runtime);

        EditorRunOutcome first = await session.RunAsync(
            CreateFlow(new MockMatchTool("匹配", 1, 0.5)), null, null);
        FlowContext firstContext = first.Result.Context;

        EditorRunOutcome second = await session.RunAsync(
            CreateFlow(new MockMatchTool("匹配", 1, 0.6)), null, null);

        Assert.Equal(EditorRunOutcomeKind.Completed, second.Kind);
        Assert.Same(second.Result.Context, session.LastRunContext);
        // 旧上下文在新结果交接后被释放
        Assert.Throws<ObjectDisposedException>(() =>
            firstContext.SetVariable(VisionFlow.Variables.Variable.Single("M", "V", VisionFlow.Variables.VariableType.Int, 1)));

        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task DiscardLastRunResult_DisposesContextAndIsIdempotent()
    {
        var runtime = new VisionFlowRuntime();
        var session = new EditorRunSession(runtime);

        EditorRunOutcome outcome = await session.RunAsync(
            CreateFlow(new MockMatchTool("匹配", 1, 0.5)), null, null);
        FlowContext context = outcome.Result.Context;

        session.DiscardLastRunResult();
        Assert.Null(session.LastRunContext);
        Assert.Throws<ObjectDisposedException>(() =>
            context.SetVariable(VisionFlow.Variables.Variable.Single("M", "V", VisionFlow.Variables.VariableType.Int, 1)));

        // 幂等：再次丢弃不抛异常
        session.DiscardLastRunResult();
        Assert.Null(session.LastRunContext);
    }

    [Fact]
    public async Task CancelledFlow_ThroughRealEngine_ReportsSkippedResult()
    {
        // 真实引擎路径：执行中取消由 FlowEngine 转成 Skipped 结果（而非异常）
        using var cts = new CancellationTokenSource();
        var blocking = new DelegateTool("阻塞", ctx =>
        {
            while (!ctx.CancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(10);
            }
            ctx.CancellationToken.ThrowIfCancellationRequested();
        });
        var runtime = new VisionFlowRuntime();
        var session = new EditorRunSession(runtime);

        Task<EditorRunOutcome> run = session.RunAsync(CreateFlow(blocking), null, null);
        Assert.True(SpinWait.SpinUntil(() => session.IsRunning, TimeSpan.FromSeconds(5)));
        session.Stop();

        EditorRunOutcome outcome = await run;
        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(NodeStatus.Skipped, outcome.Result.Status);
        session.DiscardLastRunResult();
    }
}
