using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// VF-05 回归：可声明的外部输入、必填检查、结果快照与上下文复用约定。
/// </summary>
public class ExternalInputAndResultTests : IDisposable
{
    public ExternalInputAndResultTests()
    {
        ExternalInputRegistry.ResetToDefaults();
    }

    public void Dispose()
    {
        ExternalInputRegistry.ResetToDefaults();
    }

    private sealed class StringConsumerTool : ToolBase
    {
        [InputRef("文本", typeof(string), Optional = true)]
        public string? TextPath { get; set; }

        public StringConsumerTool(string moduleName) : base(moduleName) { }
        public override NodeResult Run(FlowContext ctx) => NodeResult.Ok;
    }

    private static SequenceNode Root(params FlowNode[] children)
    {
        var root = new SequenceNode("根");
        root.Children.AddRange(children);
        return root;
    }

    [Fact]
    public void 自定义外部输入_可被声明级校验识别()
    {
        ExternalInputRegistry.Register(new ExternalInputDef("Input.PartId", typeof(string)));
        var root = Root(new ToolNode(new StringConsumerTool("消") { TextPath = "Input.PartId" }));
        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.True(result.IsValid,
            "期望校验通过，实际错误：" + string.Join("；", result.Issues.Select(i => i.ToString())));
    }

    [Fact]
    public void 自定义外部输入_出现在编辑器候选中()
    {
        ExternalInputRegistry.Register(new ExternalInputDef("Input.PartId", typeof(string)));
        var consumer = new ToolNode(new StringConsumerTool("消"));
        var root = Root(consumer);
        Assert.Contains(RefCandidateService.ForNode(root, consumer), c => c.Path == "Input.PartId");
    }

    [Fact]
    public void 必填外部输入缺失_运行明确失败()
    {
        ExternalInputRegistry.Register(new ExternalInputDef("Input.PartId", typeof(string), required: true));
        var root = Root(new ToolNode(new DelegateTool("工", _ => { })));
        FlowRunResult result = new FlowEngine().Run(root, new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal(FlowErrorCodes.InputMissing, result.ErrorCode);
        Assert.Contains("Input.PartId", result.Message);
    }

    [Fact]
    public void 必填外部输入类型不符_运行明确失败()
    {
        ExternalInputRegistry.Register(new ExternalInputDef("Input.PartId", typeof(string), required: true));
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("Input", "PartId", VariableType.Int, 42));
        FlowRunResult result = new FlowEngine().Run(Root(new ToolNode(new DelegateTool("工", _ => { }))), ctx);
        Assert.False(result.IsSuccess);
        Assert.Equal(FlowErrorCodes.InputMissing, result.ErrorCode);
    }

    [Fact]
    public void 必填外部输入齐备_正常运行()
    {
        ExternalInputRegistry.Register(new ExternalInputDef("Input.PartId", typeof(string), required: true));
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("Input", "PartId", VariableType.String, "SN-001"));
        FlowRunResult result = new FlowEngine().Run(Root(new ToolNode(new DelegateTool("工", _ => { }))), ctx);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void 必填外部输入为null_运行明确失败()
    {
        ExternalInputRegistry.Register(new ExternalInputDef("Input.PartId", typeof(string), required: true));
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single<string?>("Input", "PartId", VariableType.String, null));

        FlowRunResult result = new FlowEngine().Run(Root(), ctx);

        Assert.False(result.IsSuccess);
        Assert.Equal(FlowErrorCodes.InputMissing, result.ErrorCode);
        Assert.Contains("Input.PartId", result.Message);
        Assert.Contains("为空", result.Message);
    }

    [Fact]
    public void 结果快照_复用上下文再运行不污染前次结果()
    {
        var engine = new FlowEngine();
        var root = Root(new ToolNode(new DelegateTool("工", _ => { })));
        var ctx = new FlowContext();

        FlowRunResult first = engine.Run(root, ctx);
        int firstReportCount = first.NodeReports.Count;
        int firstLogCount = first.Log.Count;

        engine.Run(root, ctx);

        Assert.Equal(firstReportCount, first.NodeReports.Count);
        Assert.Equal(firstLogCount, first.Log.Count);
        Assert.True(ctx.NodeReports.Count > firstReportCount); // 上下文确实被复用并追加
    }

    [Fact]
    public void 预览派生上下文_变量可见且记录独立()
    {
        var main = new FlowContext();
        main.SetVariable(Variable.Single("Input", "PartId", VariableType.String, "SN-001"));
        main.AddLog(FlowLogLevel.Info, "主运行日志");

        FlowContext preview = main.CreatePreviewContext();
        Assert.Equal("SN-001", preview.GetVariable("Input", "PartId").Value);
        Assert.Empty(preview.Log);

        preview.AddLog(FlowLogLevel.Info, "预览日志");
        Assert.Single(main.Log); // 预览写入不污染源上下文
    }
}
