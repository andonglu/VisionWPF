using System.Text.Json.Nodes;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>LD-08 子流程：输入映射与沿用、输出回传、上下文隔离、HALCON 资源所有权、相对路径、缓存、循环引用与校验。</summary>
public sealed class SubFlowTests : IDisposable
{
    private readonly string _dir;

    public SubFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vf-subflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        SubFlowLibrary.Clear();
    }

    public void Dispose()
    {
        SubFlowLibrary.Clear();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    /// <summary>生成一张拥有的图像输出，用于验证所有权移交。</summary>
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class ImageMakerTool : ToolBase
    {
        public static HObject? LastCreated { get; private set; }

        public ImageMakerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            HOperatorSet.GenImageConst(out HObject image, "byte", 8, 8);
            LastCreated = image;
            SetOutput(ctx, Variable.Object(ModuleName, "Image", HalconImage.Owned(image), 1));
            return NodeResult.Ok;
        }
    }

    private static ToolNode Calc(string module, string expressions) =>
        new ToolNode(new ExpressionCalcTool(module) { Expressions = expressions });

    private static FlowOutputNode Output(string name, params (string Name, VariableKind Kind, VariableType Type, string Reference)[] outputs)
    {
        var node = new FlowOutputNode(name);
        foreach (var output in outputs)
        {
            node.Outputs.Add(new FlowOutputDef { Name = output.Name, Kind = output.Kind, Type = output.Type, Value = Operand.Ref(output.Reference) });
        }
        return node;
    }

    private string WriteFlow(string relativePath, params FlowNode[] children)
    {
        string path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = new SequenceNode("子主流程");
        root.Children.AddRange(children);
        File.WriteAllText(path, FlowSerializer.Save(root));
        return path;
    }

    /// <summary>子流程：Y = Input.X * 2。</summary>
    private string DoublerFlow(string relativePath = "doubler.vflow.json") =>
        WriteFlow(relativePath,
            Calc("内部", "y|Double|{Input.X} * 2"),
            Output("结果", ("Y", VariableKind.Single, VariableType.Double, "内部.y")));

    private static SequenceNode Root(params FlowNode[] children)
    {
        var root = new SequenceNode("根");
        root.Children.AddRange(children);
        return root;
    }

    private static SubFlowNode Sub(string file, params (string Input, Operand Value)[] inputs)
    {
        var node = new SubFlowNode("子1", file);
        foreach (var input in inputs)
        {
            node.Inputs.Add(new SubFlowInputMapping { InputPath = input.Input, Value = input.Value });
        }
        return node;
    }

    private static void AssertValid(FlowNode root)
    {
        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.True(result.IsValid, "期望校验通过，实际错误：" + string.Join("；", result.Issues.Select(i => i.ToString())));
    }

    // ---------------- 运行 ----------------

    [Fact]
    public void 输入映射与输出回传_子流程内部变量不进入当前流程()
    {
        SubFlowNode sub = Sub(DoublerFlow(), ("Input.X", Operand.Ref("父.x")));
        ToolNode after = Calc("之后", "z|Double|{子1.Y} + 1");
        SequenceNode root = Root(Calc("父", "x|Double|21"), sub, after);
        AssertValid(root);
        Assert.Contains(RefCandidateService.ForNode(root, after), c => c.Path == "子1.Y" && c.ClrType == typeof(double));

        using var ctx = new FlowContext();
        NodeResult result = root.Execute(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(42.0, ctx.GetVariable("子1", "Y").Value);
        Assert.Equal(43.0, ctx.GetVariable("之后", "z").Value);
        Assert.False(ctx.TryGetVariable("内部", "y", out _));
        Assert.False(ctx.TryGetVariable("结果", "Y", out _));
        Assert.Contains(ctx.StructuredLogs, l => l.Message.StartsWith("[子流程 子1] [变量计算] y=42"));
    }

    [Fact]
    public void 未映射的Input变量_沿用当前流程()
    {
        SequenceNode root = Root(Sub(DoublerFlow()));
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("Input", "X", VariableType.Double, 5.0));
        Assert.True(root.Execute(ctx).IsSuccess);
        Assert.Equal(10.0, ctx.GetVariable("子1", "Y").Value);
    }

    [Fact]
    public void 子流程失败_当前节点失败并带子流程日志()
    {
        string file = WriteFlow("fail.vflow.json", Calc("内部", "y|Double|1 / 0"));
        using var ctx = new FlowContext();
        NodeResult result = Root(Sub(file)).Execute(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("子流程失败", result.Message);
        Assert.Contains("除数为 0", result.Message);
        Assert.Contains(ctx.StructuredLogs, l => l.Message.StartsWith("[子流程 子1] [失败] 内部"));
    }

    [Fact]
    public void HALCON输出所有权移交给当前流程_借用输入不被释放()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string file = WriteFlow("image.vflow.json",
            new ToolNode(new ImageMakerTool("造图")),
            Output("结果", ("Image", VariableKind.Object, VariableType.Object, "造图.Image"), ("Input", VariableKind.Object, VariableType.Object, "Input.Image")));

        HOperatorSet.GenImageConst(out HObject input, "byte", 4, 4);
        using (input)
        {
            var ctx = new FlowContext();
            ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(input), 1));
            NodeResult result = Root(Sub(file)).Execute(ctx);
            Assert.True(result.IsSuccess, result.Message);

            HObject output = ((HalconImage)ctx.GetVariable("子1", "Image").Value).Object;
            Assert.Same(ImageMakerTool.LastCreated, output);
            Assert.True(output.IsInitialized());      // 子上下文已释放，输出仍可用
            Assert.True(input.IsInitialized());       // 借用输入未被子上下文释放

            ctx.Dispose();
            Assert.False(output.IsInitialized());     // 所有权已归当前上下文，随之回收
            Assert.True(input.IsInitialized());       // 借用输入经子流程回传后仍不被释放
        }
    }

    // ---------------- 路径与缓存 ----------------

    [Fact]
    public void 相对路径相对于所在流程文件_嵌套子流程按各自文件目录解析()
    {
        DoublerFlow(Path.Combine("lib", "doubler.vflow.json"));
        WriteFlow(Path.Combine("lib", "middle.vflow.json"),
            Sub("doubler.vflow.json", ("Input.X", Operand.Const(4.0))),
            Output("结果", ("Y", VariableKind.Single, VariableType.Double, "子1.Y")));
        var outer = new SubFlowNode("外层", Path.Combine("lib", "middle.vflow.json"));
        string mainPath = Path.Combine(_dir, "main.vflow.json");
        File.WriteAllText(mainPath, FlowSerializer.Save(Root(outer)));

        SequenceNode loaded = FlowSerializer.LoadFile(mainPath);
        Assert.Equal(_dir, ((SubFlowNode)loaded.Children[0]).BaseDirectory);
        AssertValid(loaded);

        using var ctx = new FlowContext();
        Assert.True(loaded.Execute(ctx).IsSuccess);
        Assert.Equal(8.0, ctx.GetVariable("外层", "Y").Value);
    }

    [Fact]
    public void 文件修改后_重新加载定义()
    {
        string file = DoublerFlow();
        SubFlowDefinition first = SubFlowLibrary.Load(file);
        Assert.Same(first, SubFlowLibrary.Load(file));

        WriteFlow("doubler.vflow.json",
            Calc("内部", "y|Double|{Input.X} * 3"),
            Output("结果", ("Y", VariableKind.Single, VariableType.Double, "内部.y")));
        File.SetLastWriteTimeUtc(file, first.LastWriteTimeUtc.AddSeconds(5));
        Assert.NotSame(first, SubFlowLibrary.Load(file));

        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("Input", "X", VariableType.Double, 2.0));
        Assert.True(Root(Sub(file)).Execute(ctx).IsSuccess);
        Assert.Equal(6.0, ctx.GetVariable("子1", "Y").Value);
    }

    // ---------------- 校验 ----------------

    [Fact]
    public void 文件缺失_校验报错且运行失败()
    {
        SequenceNode root = Root(Sub(Path.Combine(_dir, "missing.vflow.json")));
        FlowValidationIssue issue = Assert.Single(FlowValidator.Validate(root).Issues);
        Assert.Equal("子流程文件", issue.Parameter);
        Assert.Contains("不存在", issue.Message);

        using var ctx = new FlowContext();
        Assert.Contains("加载失败", root.Execute(ctx).Message);

        FlowValidationIssue empty = Assert.Single(FlowValidator.Validate(Root(new SubFlowNode("子1"))).Issues);
        Assert.Contains("未选择子流程文件", empty.Message);
    }

    [Fact]
    public void 循环引用_校验报告引用链_运行时按嵌套深度中止()
    {
        string a = Path.Combine(_dir, "a.vflow.json");
        string b = Path.Combine(_dir, "b.vflow.json");
        File.WriteAllText(a, FlowSerializer.Save(Root(new SubFlowNode("调B", b))));
        File.WriteAllText(b, FlowSerializer.Save(Root(new SubFlowNode("调A", a))));
        SequenceNode root = Root(Sub(a));

        FlowValidationIssue issue = Assert.Single(FlowValidator.Validate(root).Issues);
        Assert.Contains("循环引用", issue.Message);
        Assert.Contains("a.vflow.json → b.vflow.json → a.vflow.json", issue.Message);

        using var ctx = new FlowContext();
        NodeResult result = root.Execute(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("嵌套超过", result.Message);
    }

    [Fact]
    public void 子流程内部问题_以前缀报告_映射的输入视为已声明()
    {
        string file = WriteFlow("threshold.vflow.json",
            Calc("内部", "ok|Bool|{Input.Threshold} > 1"),
            Output("结果", ("Ok", VariableKind.Single, VariableType.Bool, "内部.ok")));

        FlowValidationIssue issue = Assert.Single(FlowValidator.Validate(Root(Sub(file))).Issues);
        Assert.Equal("子流程内部", issue.Parameter);
        Assert.Contains("Input.Threshold", issue.Message);

        AssertValid(Root(Sub(file, ("Input.Threshold", Operand.Const(3)))));
        Assert.DoesNotContain(ExternalInputRegistry.Items, i => i.Path == "Input.Threshold");
    }

    [Theory]
    [InlineData("Threshold", "输入路径应为")]
    [InlineData("Other.X", "输入路径应为")]
    [InlineData("Input.X[0]", "输入路径应为")]
    public void 输入映射路径错误_校验报错(string inputPath, string messagePart)
    {
        FlowValidationResult result = FlowValidator.Validate(Root(Sub(DoublerFlow(), (inputPath, Operand.Const(1)))));
        Assert.Contains(result.Issues, i => i.Message.Contains(messagePart));
    }

    [Fact]
    public void 输入映射取值引用无效_或输出重名_校验报错()
    {
        FlowValidationResult badRef = FlowValidator.Validate(Root(Sub(DoublerFlow(), ("Input.X", Operand.Ref("没有.变量")))));
        Assert.Contains(badRef.Issues, i => i.Parameter == "输入 Input.X" && i.Message.Contains("没有.变量"));

        string file = WriteFlow("dup.vflow.json",
            Calc("内部", "a|Int|1"),
            Output("输出甲", ("V", VariableKind.Single, VariableType.Int, "内部.a")),
            Output("输出乙", ("V", VariableKind.Single, VariableType.Int, "内部.a")));
        Assert.Contains(FlowValidator.Validate(Root(Sub(file))).Issues, i => i.Message.Contains("重名输出"));
    }

    [Fact]
    public void 预热_循环引用不会无限递归_文件缺失报告问题()
    {
        string a = Path.Combine(_dir, "a.vflow.json");
        string b = Path.Combine(_dir, "b.vflow.json");
        File.WriteAllText(a, FlowSerializer.Save(Root(new SubFlowNode("调B", b))));
        File.WriteAllText(b, FlowSerializer.Save(Root(new SubFlowNode("调A", a))));

        FlowPrepareResult cyclic = FlowResources.Prepare(Root(Sub(a)));
        Assert.True(cyclic.IsSuccess);
        SubFlowLibrary.Clear();

        FlowPrepareResult missing = FlowResources.Prepare(Root(Sub(Path.Combine(_dir, "missing.vflow.json"))));
        FlowPrepareIssue issue = Assert.Single(missing.Issues);
        Assert.Contains("子流程加载失败", issue.Message);
    }

    // ---------------- 流程文件与工具箱 ----------------

    [Fact]
    public void 子流程节点按版本2保存_基准目录不写入文件()
    {
        SubFlowNode sub = Sub("lib/doubler.vflow.json", ("Input.X", Operand.Ref("父.x")));
        sub.BaseDirectory = _dir;
        string json = FlowSerializer.Save(Root(sub));
        JsonNode saved = JsonNode.Parse(json)!;

        Assert.Equal(2, saved["FormatVersion"]!.GetValue<int>());
        Assert.Null(saved["Children"]![0]!["BaseDirectory"]);
        var loaded = Assert.IsType<SubFlowNode>(FlowSerializer.Load(json).Children[0]);
        Assert.Equal("lib/doubler.vflow.json", loaded.FlowFile);
        Assert.Equal("Input.X", loaded.Inputs.Single().InputPath);
        Assert.Equal("父.x", loaded.Inputs.Single().Value.ToString());
        Assert.Null(loaded.BaseDirectory);

        ToolboxRegistry.RegisterDefaults();
        Assert.IsType<SubFlowNode>(ToolboxRegistry.Items.Single(i => i.Id == "subflow").Factory());
    }
}
