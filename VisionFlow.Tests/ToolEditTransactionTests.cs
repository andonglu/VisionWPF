using System.Reflection;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

public class ToolEditTransactionTests
{
    [ToolOutput("Values", VariableKind.Array, VariableType.Int)]
    private sealed class EditableTool : ToolBase
    {
        public byte[] Data { get; set; } = new byte[] { 1, 2 };
        public int Count { get; set; } = 3;
        public bool RejectOnCopy { get; set; }
        private string _label = "原值";
        public string Label
        {
            get => _label;
            set
            {
                if (RejectOnCopy)
                    throw new InvalidOperationException("配置校验失败");
                _label = value;
            }
        }

        public EditableTool(string moduleName) : base(moduleName) { }
        public override NodeResult Run(FlowContext ctx)
        {
            Data[0] = 99;
            Count++;
            ctx.Trace.Add("preview");
            return NodeResult.Ok;
        }
    }

    private sealed class ConstructorlessTool : ToolBase
    {
        public ConstructorlessTool() : base("不可复制") { }
        public override NodeResult Run(FlowContext ctx) => NodeResult.Ok;
    }

    [Fact]
    public void PreviewThenCancel_KeepsOriginalConfigurationAndCleanDocument()
    {
        var original = new EditableTool("模块");
        var node = new ToolNode(original);
        var model = new FlowEditModel();
        model.Root.Children.Add(node);
        using var lastRun = new FlowContext();
        var context = new ToolEditContext { Root = model.Root, Node = node, LastRunContext = lastRun };
        var edit = new ToolEditTransaction(node, context);

        Assert.NotSame(original, edit.WorkingCopy);
        Assert.NotSame(original.Data, ((EditableTool)edit.WorkingCopy).Data);
        edit.WorkingCopy.ModuleName = "预览模块";
        using (FlowContext preview = lastRun.CreatePreviewContext())
            edit.WorkingCopy.Run(preview);

        Assert.Same(original, node.Tool);
        Assert.Equal("模块", original.ModuleName);
        Assert.Equal(new byte[] { 1, 2 }, original.Data);
        Assert.Equal(3, original.Count);
        Assert.Empty(lastRun.Trace);
        Assert.False(model.IsDirty);
        Assert.Same(node, context.Node);
    }

    [Fact]
    public void Commit_PublishesIndependentConfigurationOnce()
    {
        var original = new EditableTool("模块");
        var node = new ToolNode(original);
        var edit = new ToolEditTransaction(node);
        var working = (EditableTool)edit.WorkingCopy;
        working.ModuleName = "新模块";
        working.Count = 7;
        working.Data[0] = 42;

        edit.Commit();

        Assert.True(edit.HasCommittedChanges);
        var saved = Assert.IsType<EditableTool>(node.Tool);
        Assert.NotSame(original, saved);
        Assert.NotSame(working, saved);
        Assert.Equal("新模块", saved.ModuleName);
        Assert.Equal(7, saved.Count);
        Assert.Equal(new byte[] { 42, 2 }, saved.Data);
        working.Data[0] = 88;
        Assert.Equal(42, saved.Data[0]);
        Assert.Equal(1, original.Data[0]);
        Assert.Throws<InvalidOperationException>(() => edit.Commit());
    }

    [Fact]
    public void ConfirmWithoutChanges_DoesNotReplaceToolOrRequestDirtyState()
    {
        var original = new EditableTool("模块");
        var node = new ToolNode(original);
        var edit = new ToolEditTransaction(node);

        edit.Commit();

        Assert.False(edit.HasCommittedChanges);
        Assert.Same(original, node.Tool);
    }

    [Fact]
    public void Commit_WhenAConfigurationSetterFails_DoesNotPublishPartialChanges()
    {
        var original = new EditableTool("模块");
        var node = new ToolNode(original);
        var edit = new ToolEditTransaction(node);
        var working = (EditableTool)edit.WorkingCopy;
        working.ModuleName = "新模块";
        working.Count = 42;
        working.Data[0] = 66;
        working.RejectOnCopy = true;

        Assert.Throws<TargetInvocationException>(() => edit.Commit());

        Assert.Same(original, node.Tool);
        Assert.Equal("模块", original.ModuleName);
        Assert.Equal(3, original.Count);
        Assert.Equal(new byte[] { 1, 2 }, original.Data);
        Assert.Equal("原值", original.Label);
        Assert.False(original.RejectOnCopy);
    }

    [Fact]
    public void IncompleteFieldParsing_DoesNotModifyOriginal()
    {
        var original = new EditableTool("模块");
        var node = new ToolNode(original);
        var edit = new ToolEditTransaction(node);
        edit.WorkingCopy.ModuleName = "还未校验";
        Assert.Throws<FormatException>(() => ((EditableTool)edit.WorkingCopy).Count = int.Parse("无效"));
        Assert.Same(original, node.Tool);
        Assert.Equal("模块", original.ModuleName);
        Assert.Equal(3, original.Count);
    }

    [Fact]
    public void WorkingNode_PreservesUpstreamBranchAndInnermostLoopScope()
    {
        var upstream = new ToolNode(new EditableTool("上游"));
        var hidden = new ToolNode(new EditableTool("另一分支"));
        var target = new ToolNode(new EditableTool("目标"));
        var later = new ToolNode(new EditableTool("下游"));
        var branch = new IfElseNode("分支");
        var loop = ForLoopNode.Each("循环", "上游.Values");
        loop.Body.Add(target);
        loop.Body.Add(later);
        branch.IfBranch.Add(loop);
        branch.ElseBranch.Add(hidden);
        var root = new SequenceNode("根");
        root.Children.Add(upstream);
        root.Children.Add(branch);
        var edit = new ToolEditTransaction(target, new ToolEditContext { Root = root, Node = target });

        Assert.NotSame(root, edit.Context.Root);
        Assert.Same(edit.WorkingCopy, Assert.IsType<ToolNode>(edit.Context.Node).Tool);
        Assert.Equal(target.Id, edit.Context.Node.Id);
        RefScope expected = RefCandidateService.ScopeForNode(root, target);
        RefScope actual = RefCandidateService.ScopeForNode(edit.Context.Root, edit.Context.Node);
        Assert.Equal(expected.LoopMode, actual.LoopMode);
        Assert.Equal(RefLoopMode.Each, actual.LoopMode);
        Assert.Equal(expected.Candidates.Select(c => c.Path), actual.Candidates.Select(c => c.Path));
        Assert.Contains(actual.Candidates, c => c.Path == "Loop.Current");
        Assert.Contains(actual.Candidates, c => c.Path == "上游.Values");
        Assert.DoesNotContain(actual.Candidates, c => c.Path.StartsWith("下游.") || c.Path.StartsWith("另一分支."));
        Assert.Same(target, loop.Body[0]);
    }

    [Fact]
    public void UnsupportedToolConstructor_FailsWithoutTouchingOriginal()
    {
        var original = new ConstructorlessTool();
        var node = new ToolNode(original);
        Assert.Throws<NotSupportedException>(() => new ToolEditTransaction(node));
        Assert.Same(original, node.Tool);
    }
}
