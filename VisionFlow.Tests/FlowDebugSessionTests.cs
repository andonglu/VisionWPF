using System.Collections.Concurrent;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// 单步调试测试：调试启动逐节点暂停、逐过程跳过循环体、继续跑到结束、停止取消，
/// 以及不挂调试钩子时的行为回归。均通过 EditorRunSession + 真实引擎无头执行。
/// </summary>
public class FlowDebugSessionTests
{
    /// <summary>等待引擎暂停在指定节点前（以节点 Id 为条件，避免两次暂停之间的瞬时恢复窗口造成误判）。</summary>
    private static void WaitForPausedAt(EditorRunSession session, string nodeId)
    {
        Assert.True(
            SpinWait.SpinUntil(() => session.IsPaused && session.PausedNodeId == nodeId, TimeSpan.FromSeconds(5)),
            $"未在预期节点 {nodeId} 前暂停，当前暂停节点：{session.PausedNodeName ?? "（无）"}");
    }

    private static ToolNode Tool(string name, Action<FlowContext>? action = null)
    {
        return new ToolNode(new DelegateTool(name, action ?? (_ => { })));
    }

    [Fact]
    public async Task DebugStart_PausesAtFirstNodeThenStepsThroughEveryNodeInOrder()
    {
        var session = new EditorRunSession(new VisionFlowRuntime());
        var root = new SequenceNode("主流程");
        ToolNode toolA = Tool("工具A");
        ToolNode toolB = Tool("工具B");
        root.Children.Add(toolA);
        root.Children.Add(toolB);
        var pausedIds = new ConcurrentQueue<string>();
        session.Progressed += p =>
        {
            if (p.Kind == FlowProgressKind.DebugPaused)
            {
                pausedIds.Enqueue(p.NodeId);
            }
        };

        Task<EditorRunOutcome> run = session.StartDebugAsync(root, null, null);

        // 调试启动：第一步就暂停在根节点前，之后每次单步恰好推进一个节点
        foreach (string expectedId in new[] { root.Id, toolA.Id, toolB.Id })
        {
            WaitForPausedAt(session, expectedId);
            session.Step();
        }

        EditorRunOutcome outcome = await run;
        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        Assert.Equal(new[] { root.Id, toolA.Id, toolB.Id }, pausedIds.ToArray());
        Assert.False(session.IsPaused);
        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task Step_OnIfElse_EntersTakenBranchChildren()
    {
        var session = new EditorRunSession(new VisionFlowRuntime());
        var root = new SequenceNode("主流程");
        var ifElse = new IfElseNode("判定", new ComparisonCondition
        {
            Left = Operand.Const(1),
            Right = Operand.Const(0),
            Operator = ComparisonOperator.Greater
        });
        ToolNode branchTool = Tool("分支内");
        ifElse.IfBranch.Add(branchTool);
        ToolNode after = Tool("后续");
        root.Children.Add(ifElse);
        root.Children.Add(after);

        Task<EditorRunOutcome> run = session.StartDebugAsync(root, null, null);

        // 单步会进入 IfElse 已命中的分支子节点：主流程 → 判定 → 分支内 → 后续
        foreach (FlowNode expected in new FlowNode[] { root, ifElse, branchTool, after })
        {
            WaitForPausedAt(session, expected.Id);
            session.Step();
        }

        EditorRunOutcome outcome = await run;
        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task StepOver_OnForLoop_SkipsBodyAndStopsAtNextSibling()
    {
        var session = new EditorRunSession(new VisionFlowRuntime());
        var root = new SequenceNode("主流程");
        var loop = ForLoopNode.Count("循环", Operand.Const(3));
        int bodyRuns = 0;
        loop.Body.Add(Tool("循环体", _ => bodyRuns++));
        ToolNode after = Tool("后续");
        root.Children.Add(loop);
        root.Children.Add(after);

        Task<EditorRunOutcome> run = session.StartDebugAsync(root, null, null);

        WaitForPausedAt(session, root.Id);
        session.Step();
        WaitForPausedAt(session, loop.Id);

        // 逐过程：整个循环子树（3 次循环体）执行完，中途不暂停，停在同层下一个节点
        session.StepOver();
        WaitForPausedAt(session, after.Id);
        Assert.Equal(3, bodyRuns);

        session.Continue();
        EditorRunOutcome outcome = await run;
        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task StepOver_OnPlainTool_EqualsStep()
    {
        var session = new EditorRunSession(new VisionFlowRuntime());
        var root = new SequenceNode("主流程");
        ToolNode toolA = Tool("工具A");
        ToolNode toolB = Tool("工具B");
        root.Children.Add(toolA);
        root.Children.Add(toolB);

        Task<EditorRunOutcome> run = session.StartDebugAsync(root, null, null);

        WaitForPausedAt(session, root.Id);
        session.Step();
        WaitForPausedAt(session, toolA.Id);

        // 普通节点上逐过程等同单步：执行工具A 后停在工具B 前
        session.StepOver();
        WaitForPausedAt(session, toolB.Id);

        session.Continue();
        EditorRunOutcome outcome = await run;
        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task Continue_FromPause_RunsToEndWithoutFurtherPauses()
    {
        var session = new EditorRunSession(new VisionFlowRuntime());
        var root = new SequenceNode("主流程");
        root.Children.Add(Tool("工具A"));
        root.Children.Add(Tool("工具B"));
        int pauseCount = 0;
        session.Progressed += p =>
        {
            if (p.Kind == FlowProgressKind.DebugPaused)
            {
                Interlocked.Increment(ref pauseCount);
            }
        };

        Task<EditorRunOutcome> run = session.StartDebugAsync(root, null, null);
        WaitForPausedAt(session, root.Id);

        session.Continue();
        EditorRunOutcome outcome = await run;

        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        Assert.Equal(1, pauseCount);
        Assert.Equal(new[] { "主流程", "工具A", "工具B" }, outcome.Result.Trace);
        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task Stop_WhilePaused_CancelsFlowThroughExistingToken()
    {
        var session = new EditorRunSession(new VisionFlowRuntime());
        var root = new SequenceNode("主流程");
        root.Children.Add(Tool("工具A"));

        Task<EditorRunOutcome> run = session.StartDebugAsync(root, null, null);
        WaitForPausedAt(session, root.Id);

        session.Stop();
        EditorRunOutcome outcome = await run;

        // 取消走既有 CancellationToken 路径：引擎转换为 Skipped 结果而非异常
        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(NodeStatus.Skipped, outcome.Result.Status);
        Assert.False(session.IsRunning);
        Assert.False(session.IsPaused);
        Assert.Null(session.PausedNodeId);
        session.DiscardLastRunResult();
    }

    [Fact]
    public async Task NormalRun_WithoutDebugHooks_ProducesNoPauseEvents()
    {
        // 回归：普通运行不挂调试钩子，行为与既有 RunAsync 完全一致
        var session = new EditorRunSession(new VisionFlowRuntime());
        var root = new SequenceNode("主流程");
        root.Children.Add(new ToolNode(new MockMatchTool("匹配", 1, 0.5)));
        var kinds = new ConcurrentQueue<FlowProgressKind>();
        session.Progressed += p => kinds.Enqueue(p.Kind);

        EditorRunOutcome outcome = await session.RunAsync(root, null, null);

        Assert.Equal(EditorRunOutcomeKind.Completed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        Assert.DoesNotContain(kinds, k => k == FlowProgressKind.DebugPaused || k == FlowProgressKind.DebugResumed);
        Assert.False(session.IsPaused);
        Assert.Null(session.ActiveContext);
        session.DiscardLastRunResult();
    }
}
