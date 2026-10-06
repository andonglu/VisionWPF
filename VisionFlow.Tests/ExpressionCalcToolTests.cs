using System.Text.Json.Nodes;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>变量计算（LD-02）：多行计算、结果互相引用、类型转换、配置与引用校验、持久化。</summary>
public class ExpressionCalcToolTests
{
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Code", VariableKind.Single, VariableType.String)]
    public sealed class ProducerTool : ToolBase
    {
        public ProducerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, 4.0));
            SetOutput(ctx, Variable.Single(ModuleName, "Code", VariableType.String, "SN-001"));
            return NodeResult.Ok;
        }
    }

    public sealed class DoubleConsumerTool : ToolBase
    {
        [InputRef("数值", typeof(double))]
        public string ValuePath { get; set; } = "";

        public double LastValue { get; private set; }

        public DoubleConsumerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            LastValue = Input<double>(ctx, ValuePath);
            return NodeResult.Ok;
        }
    }

    private static ExpressionCalcTool Calc(params string[] lines)
    {
        return new ExpressionCalcTool("计算1") { Expressions = string.Join("\n", lines) };
    }

    private static SequenceNode Root(params FlowNode[] children)
    {
        var root = new SequenceNode("根");
        root.Children.AddRange(children);
        return root;
    }

    private static void AssertValid(FlowNode root)
    {
        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.True(result.IsValid, "期望校验通过，实际错误：" + string.Join("；", result.Issues.Select(i => i.ToString())));
    }

    private static FlowValidationIssue AssertIssue(FlowNode root, string messagePart)
    {
        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.False(result.IsValid, $"期望校验失败（{messagePart}），实际通过");
        return Assert.Single(result.Issues, i => i.Message.Contains(messagePart));
    }

    [Fact]
    public void 多行计算_按类型输出且后面可引用前面的结果()
    {
        ExpressionCalcTool tool = Calc(
            "间隙|Double|{测量1.Row} - 1.5",
            "间隙两倍|Double|{计算1.间隙} * 2",
            "合格|Bool|{计算1.间隙} > 0 && startswith({测量1.Code}, \"SN\")",
            "数量|Int|3 + 4",
            "文字|String|format(\"间隙 {0:F1}\", {计算1.间隙})");
        SequenceNode root = Root(new ToolNode(new ProducerTool("测量1")), new ToolNode(tool));
        AssertValid(root);

        using var ctx = new FlowContext();
        NodeResult result = root.Execute(ctx);
        Assert.True(result.IsSuccess, result.Message);

        Assert.Equal(2.5, ctx.GetVariable("计算1", "间隙").Value);
        Assert.Equal(5.0, ctx.GetVariable("计算1", "间隙两倍").Value);
        Assert.Equal(true, ctx.GetVariable("计算1", "合格").Value);
        Variable count = ctx.GetVariable("计算1", "数量");
        Assert.Equal(VariableType.Int, count.Type);
        Assert.Equal(7, count.Value);
        Assert.Equal("间隙 2.5", ctx.GetVariable("计算1", "文字").Value);
    }

    [Fact]
    public void 计算结果_出现在下游候选并可被引用()
    {
        var consumer = new ToolNode(new DoubleConsumerTool("消费1") { ValuePath = "计算1.间隙" });
        SequenceNode root = Root(new ToolNode(new ProducerTool("测量1")),
            new ToolNode(Calc("间隙|Double|{测量1.Row} - 1.5", "合格|Bool|true")), consumer);

        RefCandidate candidate = RefCandidateService.ForNode(root, consumer).Single(c => c.Path == "计算1.间隙");
        Assert.Equal(typeof(double), candidate.ClrType);
        Assert.Contains(RefCandidateService.ForNode(root, consumer), c => c.Path == "计算1.合格" && c.ClrType == typeof(bool));
        AssertValid(root);

        using var ctx = new FlowContext();
        Assert.True(root.Execute(ctx).IsSuccess);
        Assert.Equal(2.5, ((DoubleConsumerTool)consumer.Tool).LastValue);
    }

    [Fact]
    public void 引用排在后面的结果_校验与运行都报错()
    {
        ExpressionCalcTool tool = Calc("a|Double|{计算1.b} + 1", "b|Double|1");
        FlowValidationIssue issue = AssertIssue(Root(new ToolNode(tool)), "计算1.b");
        Assert.Equal("计算式 第 1 行", issue.Parameter);

        using var ctx = new FlowContext();
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("第 1 行（a）", result.Message);
        Assert.Contains("只能引用排在前面的计算结果", result.Message);
    }

    [Theory]
    [InlineData("a|Double", "计算式 第 1 行", "格式应为")]
    [InlineData("a|Number|1", "计算式 第 1 行", "类型“Number”无效")]
    [InlineData("a|1|1", "计算式 第 1 行", "类型“1”无效")]
    [InlineData("", "计算式", "未配置计算式")]
    [InlineData("  \n  ", "计算式", "未配置计算式")]
    public void 配置格式错误_校验与运行都报错(string expressions, string parameter, string messagePart)
    {
        var tool = new ExpressionCalcTool("计算1") { Expressions = expressions };
        FlowValidationIssue issue = AssertIssue(Root(new ToolNode(tool)), messagePart);
        Assert.Equal(parameter, issue.Parameter);

        using var ctx = new FlowContext();
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains(messagePart, result.Message);
    }

    [Theory]
    [InlineData("a|Double|1\nA|Int|2", "重复")]
    [InlineData("宽 度|Double|1", "不能包含")]
    [InlineData("|Double|1", "不能为空")]
    public void 结果名重复或非法_校验与运行都报错(string expressions, string messagePart)
    {
        var tool = new ExpressionCalcTool("计算1") { Expressions = expressions };
        FlowValidationResult validation = FlowValidator.Validate(Root(new ToolNode(tool)));
        Assert.Contains(validation.Issues, i => i.Message.Contains(messagePart));

        using var ctx = new FlowContext();
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains(messagePart, result.Message);
    }

    [Fact]
    public void 表达式语法错误_校验报告行与位置()
    {
        FlowValidationIssue issue = AssertIssue(Root(new ToolNode(Calc("a|Double|1", "b|Double|2 +"))), "表达式错误");
        Assert.Equal("计算式 第 2 行", issue.Parameter);
        Assert.Contains("第 4 个字符处", issue.Message);
    }

    [Fact]
    public void 类型转换失败_报告行号且不写出任何输出()
    {
        ExpressionCalcTool tool = Calc("a|Double|1", "n|Int|2.5");
        using var ctx = new FlowContext();
        NodeResult result = tool.Run(ctx);

        Assert.False(result.IsSuccess);
        Assert.Contains("第 2 行（n）", result.Message);
        Assert.Contains("不能转换为 Int", result.Message);
        Assert.False(ctx.TryGetVariable("计算1", "a", out _));
    }

    [Fact]
    public void 除零与引用无效_运行失败()
    {
        using var ctx = new FlowContext();
        Assert.Contains("除数为 0", Calc("a|Double|1 / 0").Run(ctx).Message);
        Assert.Contains("{没有.变量}", Calc("a|Double|{没有.变量}").Run(ctx).Message);
    }

    [Fact]
    public void 结果为NaN_不算失败()
    {
        using var ctx = new FlowContext();
        Assert.True(Calc("x|Double|num(\"abc\")").Run(ctx).IsSuccess);
        Assert.True(double.IsNaN((double)ctx.GetVariable("计算1", "x").Value));
    }

    [Fact]
    public void 表达式中含竖线_只按前两个竖线分隔()
    {
        ExpressionCalcTool tool = Calc("ok|Bool|false || true", "s|String|\"a|b\"");
        using var ctx = new FlowContext();
        Assert.True(tool.Run(ctx).IsSuccess);
        Assert.Equal(true, ctx.GetVariable("计算1", "ok").Value);
        Assert.Equal("a|b", ctx.GetVariable("计算1", "s").Value);
    }

    [Fact]
    public void 解析与格式化_往返一致且忽略空行和回车()
    {
        List<ExpressionCalcItem> items = ExpressionCalcTool.ParseItems(" a | double | 1 + 2 \r\n\r\nb|Bool|true\r\n", new List<ToolConfigurationIssue>());
        Assert.Equal(2, items.Count);
        Assert.Equal(("a", VariableType.Double, "1 + 2", 1), (items[0].Name, items[0].Type, items[0].Expression, items[0].LineNumber));
        Assert.Equal(3, items[1].LineNumber);
        Assert.Equal("a|Double|1 + 2\nb|Bool|true", ExpressionCalcTool.FormatItems(items));
    }

    [Fact]
    public void 编辑器逐行检查()
    {
        var items = new List<ExpressionCalcItem>
        {
            new() { Name = "a", Type = VariableType.Double, Expression = "{计算1.b} + {外部.x}" },
            new() { Name = "b", Type = VariableType.Double, Expression = "{计算1.a} * 2" },
            new() { Name = "c|d", Type = VariableType.Double, Expression = "1" },
            new() { Name = "e", Type = VariableType.Double, Expression = "{Item}" }
        };

        Assert.Contains("只能引用排在前面", ExpressionCalcTool.CheckItem(items, 0, "计算1", _ => null));
        Assert.Null(ExpressionCalcTool.CheckItem(items, 1, "计算1", _ => null));
        Assert.Contains("|", ExpressionCalcTool.CheckItem(items, 2, "计算1", _ => null));
        Assert.Contains("未定义的名称", ExpressionCalcTool.CheckItem(items, 3, "计算1", _ => null));

        items[0].Expression = "{外部.x}";
        Assert.Equal("外部错误", ExpressionCalcTool.CheckItem(items, 0, "计算1", path => path == "外部.x" ? "外部错误" : null));
        Assert.Null(ExpressionCalcTool.CheckItem(items, 0, "计算1", null));
    }

    [Fact]
    public void 工具箱与流程文件_使用稳定ID并保存计算式()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "expression-calc");
        Assert.Equal("07 数据与判定", item.Category);
        Assert.IsType<ExpressionCalcTool>(Assert.IsType<ToolNode>(item.Factory()).Tool);

        SequenceNode root = Root(new ToolNode(Calc("a|Double|1 + 2", "b|Bool|{计算1.a} > 2")));
        string json = FlowSerializer.Save(root);
        Assert.Equal("expression-calc", JsonNode.Parse(json)!["Children"]![0]!["Tool"]!["ToolId"]!.GetValue<string>());

        SequenceNode loaded = FlowSerializer.Load(json);
        var tool = Assert.IsType<ExpressionCalcTool>(Assert.IsType<ToolNode>(loaded.Children[0]).Tool);
        Assert.Equal("a|Double|1 + 2\nb|Bool|{计算1.a} > 2", tool.Expressions);
        AssertValid(loaded);
    }
}
