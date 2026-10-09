using VisionFlow.Core;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Runtime;

namespace VisionFlow.Tests;

/// <summary>
/// D1 流程锚点机制专项（§5.4，非 HALCON 用例）：
/// FlowEngine.Run 写 <see cref="FlowContext.OwnerToken"/>；
/// <see cref="FlowResources.Prepare(FlowNode)"/> 经 AsyncLocal 环境锚点对预热循环可见、try/finally 清除；
/// 快照 <see cref="FlowResources.Prepare(IEnumerable{ToolNode})"/> 无锚点；
/// <see cref="FlowContext.CreatePreviewContext"/> 继承锚点；异常路径不泄漏环境锚点。
/// </summary>
public class FlowOwnerTokenTests
{
    private sealed class AnchorRecordingTool : ToolBase, IToolResourceLifecycle
    {
        public object ObservedAnchor { get; private set; }
        public bool ThrowOnPrepare { get; set; }

        public AnchorRecordingTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ObservedAnchor = ctx.OwnerToken;
            return NodeResult.Ok;
        }

        public void Prepare()
        {
            if (ThrowOnPrepare)
            {
                throw new InvalidOperationException("预热失败");
            }
            ObservedAnchor = FlowResources.CurrentEnvironmentAnchor;
        }

        public void ReleaseResources()
        {
        }
    }

    [Fact]
    public void Run写锚点_工具读到根节点实例()
    {
        var tool = new AnchorRecordingTool("锚点1");
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));

        FlowRunResult result = new FlowEngine().Run(root);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Same(root, tool.ObservedAnchor);
        Assert.Same(root, result.Context.OwnerToken);
    }

    [Fact]
    public void Prepare根节点重载_环境锚点对预热可见且结束后清除()
    {
        var tool = new AnchorRecordingTool("锚点1");
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));

        FlowPrepareResult result = FlowResources.Prepare(root);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.PreparedCount);
        Assert.Same(root, tool.ObservedAnchor);
        Assert.Null(FlowResources.CurrentEnvironmentAnchor);
    }

    [Fact]
    public void Prepare快照入口_无环境锚点()
    {
        var tool = new AnchorRecordingTool("锚点1");
        var node = new ToolNode(tool);

        FlowPrepareResult result = FlowResources.Prepare(new[] { node });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.PreparedCount);
        Assert.Null(tool.ObservedAnchor);
        Assert.Null(FlowResources.CurrentEnvironmentAnchor);
    }

    [Fact]
    public void CreatePreviewContext_继承OwnerToken()
    {
        var root = new SequenceNode("根");
        using var ctx = new FlowContext();
        ctx.OwnerToken = root;

        using FlowContext preview = ctx.CreatePreviewContext();

        Assert.Same(root, preview.OwnerToken);
        Assert.True(preview.IsPreview);
    }

    [Fact]
    public void 预热异常路径_不泄漏环境锚点且其他工具不受影响()
    {
        var throwing = new AnchorRecordingTool("失败工具") { ThrowOnPrepare = true };
        var normal = new AnchorRecordingTool("正常工具");
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(throwing));
        root.Children.Add(new ToolNode(normal));

        FlowPrepareResult result = FlowResources.Prepare(root);

        Assert.False(result.IsSuccess);
        Assert.Equal("失败工具", Assert.Single(result.Issues).NodeName);
        Assert.Equal(2, result.PreparedCount);
        Assert.Same(root, normal.ObservedAnchor);
        Assert.Null(FlowResources.CurrentEnvironmentAnchor);
    }
}
