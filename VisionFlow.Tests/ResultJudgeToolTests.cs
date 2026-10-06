using System.Text.Json.Nodes;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>综合判定（LD-04）：区间与布尔判定、不合格原因、停止策略、配置校验、持久化。</summary>
public class ResultJudgeToolTests
{
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Code", VariableKind.Single, VariableType.String)]
    public sealed class ProducerTool : ToolBase
    {
        public double Row { get; set; } = 4.0;

        public ProducerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, Row));
            SetOutput(ctx, Variable.Single(ModuleName, "Code", VariableType.String, "SN-001"));
            return NodeResult.Ok;
        }
    }

    private static ResultJudgeTool Judge(params string[] lines)
    {
        return new ResultJudgeTool("判定1") { Items = string.Join("\n", lines) };
    }

    private static FlowContext Run(ResultJudgeTool tool, double row = 4.0)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new ProducerTool("测量1") { Row = row }));
        root.Children.Add(new ToolNode(tool));
        var ctx = new FlowContext();
        NodeResult result = root.Execute(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ctx;
    }

    private static T Get<T>(FlowContext ctx, string name) => (T)ctx.GetVariable("判定1", name).Value;

    private static FlowValidationResult Validate(ResultJudgeTool tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new ProducerTool("测量1")));
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root);
    }

    [Fact]
    public void 全部合格_输出OK与每项结果()
    {
        ResultJudgeTool tool = Judge(
            "宽度|{测量1.Row}|3|5|10|宽度超差",
            "条码|startswith({测量1.Code}, \"SN\")|||11|条码错误");
        Assert.True(Validate(tool).IsValid);

        using FlowContext ctx = Run(tool);
        Assert.True(Get<bool>(ctx, "Ok"));
        Assert.Equal(0, Get<int>(ctx, "Code"));
        Assert.Equal("OK", Get<string>(ctx, "Message"));
        Assert.Equal(0, Get<int>(ctx, "NgCount"));
        Assert.Equal(new[] { 4.0, 1.0 }, Get<double[]>(ctx, "Values"));
        Assert.Equal(new[] { true, true }, Get<bool[]>(ctx, "ItemOks"));
        Assert.Empty(Get<string[]>(ctx, "NgNames"));
    }

    [Fact]
    public void 不合格_代码与信息取第一个不合格项且记录全部原因()
    {
        ResultJudgeTool tool = Judge(
            "宽度|{测量1.Row}|4.5|5|10|宽度超差",
            "条码|startswith({测量1.Code}, \"XX\")|||11|条码错误",
            "数量|2|1|3|12|");

        using FlowContext ctx = Run(tool);
        Assert.False(Get<bool>(ctx, "Ok"));
        Assert.Equal(10, Get<int>(ctx, "Code"));
        Assert.Equal("宽度超差", Get<string>(ctx, "Message"));
        Assert.Equal(2, Get<int>(ctx, "NgCount"));
        Assert.Equal(new[] { "宽度", "条码" }, Get<string[]>(ctx, "NgNames"));
        Assert.Equal(new[] { "宽度超差", "条码错误" }, Get<string[]>(ctx, "NgMessages"));
        Assert.Equal(new[] { "宽度：实测 4，要求 [4.5, 5]", "条码：条件不成立" }, Get<string[]>(ctx, "NgDetails"));
        Assert.Equal(new[] { false, false, true }, Get<bool[]>(ctx, "ItemOks"));
        Assert.Equal(new[] { 4.0, 0.0, 2.0 }, Get<double[]>(ctx, "Values"));
    }

    [Fact]
    public void 遇到第一个不合格项即停止_后续项不判定()
    {
        ResultJudgeTool tool = Judge("a|1|2||1|", "b|false|||2|", "c|true|||3|");
        tool.StopAtFirstNg = true;

        using FlowContext ctx = Run(tool);
        Assert.Equal(1, Get<int>(ctx, "NgCount"));
        Assert.Equal(1, Get<int>(ctx, "Code"));
        Assert.Equal(new[] { false, false, false }, Get<bool[]>(ctx, "ItemOks"));
        double[] values = Get<double[]>(ctx, "Values");
        Assert.Equal(1.0, values[0]);
        Assert.True(double.IsNaN(values[1]) && double.IsNaN(values[2]));
    }

    [Theory]
    [InlineData("a|{测量1.Row}|4|4|1|", true)]
    [InlineData("a|{测量1.Row}|4||1|", true)]
    [InlineData("a|{测量1.Row}||4|1|", true)]
    [InlineData("a|{测量1.Row}|||1|", true)]
    [InlineData("a|{测量1.Row}|4.0001||1|", false)]
    [InlineData("a|{测量1.Row}||3.9999|1|", false)]
    public void 上下限为闭区间且可只设一侧(string line, bool expected)
    {
        using FlowContext ctx = Run(Judge(line));
        Assert.Equal(expected, Get<bool>(ctx, "Ok"));
    }

    [Theory]
    [InlineData("a|num(\"x\")|||1|", "取值无效（NaN）")]
    [InlineData("a|1 / 0|||1|", "无法取值")]
    [InlineData("a|{测量1.Code}|||1|", "取值为字符串")]
    [InlineData("a|true|0|1|1|", "取值为布尔，不能设置上下限")]
    public void 无法判定的取值_判为不合格并说明原因_工具不失败(string line, string reason)
    {
        using FlowContext ctx = Run(Judge(line));
        Assert.False(Get<bool>(ctx, "Ok"));
        Assert.Contains(reason, Get<string[]>(ctx, "NgDetails").Single());
        Assert.True(double.IsNaN(Get<double[]>(ctx, "Values")[0]) || line.Contains("true"));
    }

    [Fact]
    public void 缺省NG信息与代码()
    {
        using FlowContext ctx = Run(Judge("宽度|false||||"));
        Assert.Equal(1, Get<int>(ctx, "Code"));
        Assert.Equal("宽度不合格", Get<string>(ctx, "Message"));
    }

    [Fact]
    public void 取值表达式含竖线_从行尾分隔()
    {
        List<ResultJudgeItem> items = ResultJudgeTool.ParseItems("x|false || true|||2|说明", null);
        ResultJudgeItem item = Assert.Single(items);
        Assert.Equal(("x", "false || true", (double?)null, (double?)null, 2, "说明"),
            (item.Name, item.Expression, item.Lower, item.Upper, item.NgCode, item.NgMessage));

        using FlowContext ctx = Run(Judge("x|false || true|||2|说明"));
        Assert.True(Get<bool>(ctx, "Ok"));
    }

    [Theory]
    [InlineData("a|1|2|3", "判定项 第 1 行", "格式应为")]
    [InlineData("|1|||1|", "判定项 第 1 行", "名称不能为空")]
    [InlineData("a|1|x||1|", "判定项 第 1 行", "下限“x”不是数值")]
    [InlineData("a|1||y|1|", "判定项 第 1 行", "上限“y”不是数值")]
    [InlineData("a|1|5|4|1|", "判定项 第 1 行", "下限不能大于上限")]
    [InlineData("a|1|||1.5|", "判定项 第 1 行", "不是整数")]
    [InlineData("a|1|||0|", "判定项 第 1 行", "与合格代码相同")]
    [InlineData("a|1|||1|\nA|2|||2|", "判定项 第 2 行", "重复")]
    [InlineData("", "判定项", "未配置判定项")]
    public void 配置错误_校验与运行都报错(string items, string parameter, string messagePart)
    {
        var tool = new ResultJudgeTool("判定1") { Items = items };
        FlowValidationResult result = Validate(tool);
        FlowValidationIssue issue = Assert.Single(result.Issues, i => i.Message.Contains(messagePart));
        Assert.Equal(parameter, issue.Parameter);

        using var ctx = new FlowContext();
        NodeResult run = tool.Run(ctx);
        Assert.False(run.IsSuccess);
        Assert.Contains(messagePart, run.Message);
    }

    [Fact]
    public void 取值表达式的引用与语法_按行校验()
    {
        FlowValidationResult result = Validate(Judge("a|{测量1.Row}|||1|", "b|{测量1.Missing} > 0|||2|", "c|1 +|||3|"));
        Assert.Contains(result.Issues, i => i.Parameter == "判定项 第 2 行" && i.Message.Contains("测量1.Missing"));
        Assert.Contains(result.Issues, i => i.Parameter == "判定项 第 3 行" && i.Message.Contains("表达式错误"));
        Assert.Equal(2, result.Issues.Count);
    }

    [Fact]
    public void 输出可接流程输出且类型正确()
    {
        var judge = new ToolNode(Judge("a|true|||1|"));
        var output = new FlowOutputNode("流程输出");
        output.Outputs.Add(new FlowOutputDef { Name = "Ok", Kind = VariableKind.Single, Type = VariableType.Bool, Value = VisionFlow.Variables.Operand.Ref("判定1.Ok") });
        output.Outputs.Add(new FlowOutputDef { Name = "Code", Kind = VariableKind.Single, Type = VariableType.Int, Value = VisionFlow.Variables.Operand.Ref("判定1.Code") });
        output.Outputs.Add(new FlowOutputDef { Name = "Message", Kind = VariableKind.Single, Type = VariableType.String, Value = VisionFlow.Variables.Operand.Ref("判定1.Message") });
        var root = new SequenceNode("根");
        root.Children.Add(judge);
        root.Children.Add(output);

        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.True(result.IsValid, string.Join("；", result.Issues.Select(i => i.ToString())));
        using var ctx = new FlowContext();
        Assert.True(root.Execute(ctx).IsSuccess);
        Assert.Equal(true, ctx.GetVariable("流程输出", "Ok").Value);
    }

    [Fact]
    public void 格式化与解析_往返一致()
    {
        const string text = "宽度|{测量1.Row}|3.5|5|10|宽度超差\n条码|true|||11|";
        Assert.Equal(text, ResultJudgeTool.FormatItems(ResultJudgeTool.ParseItems(text, null)));
    }

    [Fact]
    public void 编辑器逐项检查()
    {
        var items = new List<ResultJudgeItem>
        {
            new() { Name = "a", Expression = "{外部.x} > 1", NgCode = 1 },
            new() { Name = "a2", Expression = "true", NgCode = 2 },
            new() { Name = "b", Expression = "true", NgCode = 0 },
            new() { Name = "c", Expression = "true", NgCode = 3, NgMessage = "x|y" },
            new() { Name = "d", Expression = "{Item}", NgCode = 4 },
            new() { Name = "e", Expression = "1", Lower = 5, Upper = 4, NgCode = 5 }
        };

        Assert.Equal("外部错误", ResultJudgeTool.CheckItem(items, 0, 0, _ => "外部错误"));
        Assert.Null(ResultJudgeTool.CheckItem(items, 0, 0, null));
        Assert.Null(ResultJudgeTool.CheckItem(items, 1, 0, null));
        items[1].Name = "A";
        Assert.Contains("重复", ResultJudgeTool.CheckItem(items, 1, 0, null));
        Assert.Contains("与合格代码相同", ResultJudgeTool.CheckItem(items, 2, 0, null));
        Assert.Contains("|", ResultJudgeTool.CheckItem(items, 3, 0, null));
        Assert.Contains("未定义的名称", ResultJudgeTool.CheckItem(items, 4, 0, null));
        Assert.Contains("下限不能大于上限", ResultJudgeTool.CheckItem(items, 5, 0, null));
    }

    [Fact]
    public void 工具箱与流程文件_使用稳定ID并保存全部参数()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "result-judge");
        Assert.Equal("07 数据与判定", item.Category);
        Assert.IsType<ResultJudgeTool>(Assert.IsType<ToolNode>(item.Factory()).Tool);

        ResultJudgeTool tool = Judge("a|true|||5|不合格");
        tool.StopAtFirstNg = true;
        tool.OkCode = 100;
        tool.OkMessage = "良品";
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));

        string json = FlowSerializer.Save(root);
        Assert.Equal("result-judge", JsonNode.Parse(json)!["Children"]![0]!["Tool"]!["ToolId"]!.GetValue<string>());
        var loaded = Assert.IsType<ResultJudgeTool>(Assert.IsType<ToolNode>(FlowSerializer.Load(json).Children[0]).Tool);
        Assert.Equal((tool.Items, true, 100, "良品"), (loaded.Items, loaded.StopAtFirstNg, loaded.OkCode, loaded.OkMessage));
    }
}
