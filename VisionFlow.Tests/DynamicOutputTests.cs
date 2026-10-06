using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// 动态输出（LD-01）：工具按用户配置产生的输出名，必须同时被引用候选、声明级校验和流程文件保存加载识别。
/// </summary>
public class DynamicOutputTests
{
    /// <summary>按 Outputs（"名称|类型;名称|类型"）声明并写出输出的测试工具，另有一个静态输出 Count。</summary>
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class ConfigurableOutputTool : ToolBase, IDynamicOutputTool
    {
        public string Outputs { get; set; } = "";

        public ConfigurableOutputTool(string moduleName) : base(moduleName) { }

        public IReadOnlyList<ToolOutputDef> GetDynamicOutputs()
        {
            return Parse().Select(p => new ToolOutputDef
            {
                Name = p.Name,
                Kind = VariableKind.Single,
                Type = p.Type
            }).ToList();
        }

        public override NodeResult Run(FlowContext ctx)
        {
            var outputs = Parse();
            foreach (var (name, type) in outputs)
            {
                object value = type switch
                {
                    VariableType.Int => 7,
                    VariableType.Bool => true,
                    VariableType.String => "文本",
                    _ => 1.5
                };
                SetOutput(ctx, Variable.Single(ModuleName, name, type, value));
            }
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, outputs.Count));
            return NodeResult.Ok;
        }

        private List<(string Name, VariableType Type)> Parse()
        {
            return Outputs.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Split('|'))
                .Where(parts => parts.Length == 2 && Enum.TryParse(parts[1], out VariableType _))
                .Select(parts => (parts[0], Enum.Parse<VariableType>(parts[1])))
                .ToList();
        }
    }

    public sealed class DoubleConsumerTool : ToolBase
    {
        [InputRef("Double输入", typeof(double))]
        public string ValuePath { get; set; } = "";

        public double LastValue { get; private set; }

        public DoubleConsumerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            LastValue = Input<double>(ctx, ValuePath);
            return NodeResult.Ok;
        }
    }

    private static (SequenceNode Root, ToolNode Producer, ToolNode Consumer) Flow(string outputs, string consumerPath)
    {
        var producer = new ToolNode(new ConfigurableOutputTool("计算1") { Outputs = outputs });
        var consumer = new ToolNode(new DoubleConsumerTool("消费1") { ValuePath = consumerPath });
        var root = new SequenceNode("根");
        root.Children.Add(producer);
        root.Children.Add(consumer);
        return (root, producer, consumer);
    }

    private static void AssertValid(FlowValidationResult result)
    {
        Assert.True(result.IsValid,
            "期望校验通过，实际错误：" + string.Join("；", result.Issues.Select(i => i.ToString())));
    }

    [Fact]
    public void 下游候选_同时包含动态输出与静态输出且类型正确()
    {
        var (root, _, consumer) = Flow("宽度|Double;合格|Bool;编号|String", "计算1.宽度");

        List<RefCandidate> candidates = RefCandidateService.ForNode(root, consumer);

        Assert.Equal(typeof(double), candidates.Single(c => c.Path == "计算1.宽度").ClrType);
        Assert.Equal(typeof(bool), candidates.Single(c => c.Path == "计算1.合格").ClrType);
        Assert.Equal(typeof(string), candidates.Single(c => c.Path == "计算1.编号").ClrType);
        Assert.Equal(typeof(int), candidates.Single(c => c.Path == "计算1.Count").ClrType);
    }

    [Fact]
    public void 引用动态输出_校验通过且运行取到值()
    {
        var (root, _, consumer) = Flow("宽度|Double", "计算1.宽度");

        AssertValid(FlowValidator.Validate(root));
        using var ctx = new FlowContext();
        NodeResult execution = root.Execute(ctx);
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.Equal(1.5, ((DoubleConsumerTool)consumer.Tool).LastValue);
    }

    [Fact]
    public void 修改输出名后_原引用被校验报告()
    {
        var (root, producer, _) = Flow("宽度|Double", "计算1.宽度");
        AssertValid(FlowValidator.Validate(root));

        ((ConfigurableOutputTool)producer.Tool).Outputs = "高度|Double";

        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Message.Contains("计算1.宽度") && i.Message.Contains("不存在"));
    }

    [Fact]
    public void 动态输出类型不符_校验报告()
    {
        var (root, _, _) = Flow("编号|String", "计算1.编号");

        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Message.Contains("不能赋给"));
    }

    [Theory]
    [InlineData("宽 度|Double", "宽 度")]
    [InlineData("a.b|Double", "a.b")]
    [InlineData("值[0]|Double", "值[0]")]
    [InlineData("{值}|Double", "{值}")]
    public void 非法输出名_校验报错且不进入候选(string outputs, string name)
    {
        var (root, _, consumer) = Flow(outputs, "计算1.Count");

        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.Contains(result.Issues, i => i.Parameter == name && i.Message.Contains("不能包含"));
        Assert.DoesNotContain(RefCandidateService.ForNode(root, consumer), c => c.Path.StartsWith("计算1." + name));
    }

    [Theory]
    [InlineData("宽度|Double;宽度|Int", "宽度")]
    [InlineData("宽度|Double;宽度|Double", "宽度")]
    [InlineData("count|Int", "count")]
    public void 输出名重复或与静态输出同名_校验报错且候选不重复(string outputs, string name)
    {
        var (root, _, consumer) = Flow(outputs, "计算1.Count");

        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.Contains(result.Issues, i => i.Parameter == name && i.Message.Contains("重复"));
        var paths = RefCandidateService.ForNode(root, consumer).Select(c => c.Path.ToLowerInvariant()).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public void 非动态工具_按实例取输出与按类型一致()
    {
        var tool = new MockMatchTool("匹配1", 1, 0.5);
        Assert.Same(ToolMetadata.GetOutputs(typeof(MockMatchTool)), ToolMetadata.GetOutputs(tool));
    }

    [Fact]
    public void 保存加载后_动态输出仍被识别()
    {
        var (root, _, _) = Flow("宽度|Double", "计算1.宽度");

        SequenceNode loaded = FlowSerializer.Load(FlowSerializer.Save(root));

        var producer = Assert.IsType<ConfigurableOutputTool>(Assert.IsType<ToolNode>(loaded.Children[0]).Tool);
        Assert.Equal("宽度|Double", producer.Outputs);
        AssertValid(FlowValidator.Validate(loaded));
        Assert.Contains(RefCandidateService.ForNode(loaded, loaded.Children[1]), c => c.Path == "计算1.宽度");
    }
}
