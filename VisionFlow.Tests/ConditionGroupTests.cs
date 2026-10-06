using System.Text.Json.Nodes;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>IfElse 条件组（LD-05）：与 / 或嵌套、表达式条件、新比较符、日志明细、按需升级的文件版本与校验。</summary>
public class ConditionGroupTests
{
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Code", VariableKind.Single, VariableType.String)]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    public sealed class ProducerTool : ToolBase
    {
        public ProducerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, 2));
            SetOutput(ctx, Variable.Single(ModuleName, "Code", VariableType.String, "SN-001"));
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, double.NaN));
            return NodeResult.Ok;
        }
    }

    private static ComparisonCondition Compare(object left, ComparisonOperator op, object? right = null)
    {
        return new ComparisonCondition
        {
            Left = left is string s && s.StartsWith("ref:") ? Operand.Ref(s.Substring(4)) : Operand.Const(left),
            Operator = op,
            Right = right is string r && r.StartsWith("ref:") ? Operand.Ref(r.Substring(4)) : Operand.Const(right)
        };
    }

    private static ComparisonCondition Bool(bool value) => Compare(value, ComparisonOperator.Equal, true);

    private static ConditionGroup Group(ConditionLogic logic, params ICondition[] items)
    {
        var group = new ConditionGroup { Logic = logic };
        group.Items.AddRange(items);
        return group;
    }

    private static (SequenceNode Root, IfElseNode IfElse) Flow(ICondition condition)
    {
        var ifElse = new IfElseNode("分支", condition);
        ifElse.Outputs.Add(new BranchOutputDef
        {
            Name = "Taken",
            Kind = VariableKind.Single,
            Type = VariableType.String,
            IfValue = Operand.Const("If"),
            ElseValue = Operand.Const("Else")
        });
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new ProducerTool("测量1")));
        root.Children.Add(ifElse);
        return (root, ifElse);
    }

    // ---------------- 求值 ----------------

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void A且B或C_真值表(bool a, bool b, bool c, bool expected)
    {
        ConditionGroup condition = Group(ConditionLogic.Or, Group(ConditionLogic.And, Bool(a), Bool(b)), Bool(c));
        using var ctx = new FlowContext();
        Assert.Equal(expected, condition.Evaluate(ctx, null));
    }

    [Fact]
    public void 条件组短路_不计算后续项()
    {
        ComparisonCondition explode = Compare("abc", ComparisonOperator.Greater, 1);
        using var ctx = new FlowContext();
        Assert.False(Group(ConditionLogic.And, Bool(false), explode).Evaluate(ctx, null));
        Assert.True(Group(ConditionLogic.Or, Bool(true), explode).Evaluate(ctx, null));
        Assert.Throws<InvalidOperationException>(() => Group(ConditionLogic.And, Bool(true), explode).Evaluate(ctx, null));
    }

    [Theory]
    [InlineData("SN-001", ComparisonOperator.Contains, "-00", true)]
    [InlineData("SN-001", ComparisonOperator.NotContains, "XX", true)]
    [InlineData("SN-001", ComparisonOperator.StartsWith, "SN", true)]
    [InlineData("SN-001", ComparisonOperator.EndsWith, "002", false)]
    [InlineData(12345, ComparisonOperator.StartsWith, "12", true)]
    [InlineData(1.5, ComparisonOperator.Contains, ".", true)]
    public void 文本比较符(object left, ComparisonOperator op, object right, bool expected)
    {
        using var ctx = new FlowContext();
        Assert.Equal(expected, Compare(left, op, right).Evaluate(ctx));
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(null, false)]
    [InlineData("", true)]
    public void 有效与无效_只看左操作数(object? left, bool valid)
    {
        using var ctx = new FlowContext();
        Assert.Equal(valid, new ComparisonCondition { Left = Operand.Const(left), Operator = ComparisonOperator.IsValid }.Evaluate(ctx));
        Assert.Equal(!valid, new ComparisonCondition { Left = Operand.Const(left), Operator = ComparisonOperator.IsInvalid }.Evaluate(ctx));
    }

    [Fact]
    public void IfElse按条件组与表达式选择分支_并记录条件明细()
    {
        ConditionGroup condition = Group(ConditionLogic.And,
            Compare("ref:测量1.Count", ComparisonOperator.Equal, 2),
            Compare("ref:测量1.Code", ComparisonOperator.StartsWith, "SN"),
            new ExpressionCondition { Expression = "!isvalid({测量1.Row})" });
        var (root, _) = Flow(condition);
        Assert.True(FlowValidator.Validate(root).IsValid);

        using var ctx = new FlowContext();
        Assert.True(root.Execute(ctx).IsSuccess);
        Assert.Equal("If", ctx.GetVariable("分支", "Taken").Value);
        string details = ctx.StructuredLogs.Single(l => l.Message.StartsWith("[条件明细]")).Message;
        Assert.Contains("测量1.Count（2） == 2 → 成立", details);
        Assert.Contains("测量1.Code（SN-001） 开头是 SN → 成立", details);
        Assert.Contains("!isvalid({测量1.Row}) → 成立", details);
    }

    [Fact]
    public void 表达式条件结果不是布尔_节点失败()
    {
        var (root, _) = Flow(new ExpressionCondition { Expression = "{测量1.Count} + 1" });
        using var ctx = new FlowContext();
        NodeResult result = root.Execute(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("应为布尔值", result.Message);
    }

    // ---------------- 流程文件 ----------------

    private static JsonNode SavedCondition(string json) => JsonNode.Parse(json)!["Children"]![1]!["Condition"]!;

    [Fact]
    public void 单条旧比较符_按版本1格式保存且不写新字段()
    {
        var (root, _) = Flow(Compare("ref:测量1.Count", ComparisonOperator.Greater, 0));
        string json = FlowSerializer.Save(root);

        Assert.Equal(1, JsonNode.Parse(json)!["FormatVersion"]!.GetValue<int>());
        JsonObject condition = SavedCondition(json).AsObject();
        Assert.Equal(new[] { "Left", "Operator", "Right" }, condition.Select(p => p.Key).ToArray());

        var loaded = Assert.IsType<IfElseNode>(FlowSerializer.Load(json).Children[1]);
        var comparison = Assert.IsType<ComparisonCondition>(loaded.Condition);
        Assert.Equal(ComparisonOperator.Greater, comparison.Operator);
    }

    [Fact]
    public void 条件组与表达式_按版本2保存并完整往返()
    {
        ICondition condition = Group(ConditionLogic.Or,
            Group(ConditionLogic.And, Compare("ref:测量1.Count", ComparisonOperator.Equal, 2), new ExpressionCondition { Expression = "true" }),
            new ComparisonCondition { Left = Operand.Ref("测量1.Row"), Operator = ComparisonOperator.IsInvalid });
        var (root, _) = Flow(condition);
        string json = FlowSerializer.Save(root);

        Assert.Equal(2, JsonNode.Parse(json)!["FormatVersion"]!.GetValue<int>());
        Assert.Equal("Group", SavedCondition(json)["Kind"]!.GetValue<string>());
        Assert.Null(SavedCondition(json)["Items"]![1]!["Right"]);

        var loaded = Assert.IsType<IfElseNode>(FlowSerializer.Load(json).Children[1]);
        var outer = Assert.IsType<ConditionGroup>(loaded.Condition);
        Assert.Equal(ConditionLogic.Or, outer.Logic);
        var inner = Assert.IsType<ConditionGroup>(outer.Items[0]);
        Assert.Equal(ConditionLogic.And, inner.Logic);
        Assert.Equal("true", Assert.IsType<ExpressionCondition>(inner.Items[1]).Expression);
        Assert.Equal(ComparisonOperator.IsInvalid, Assert.IsType<ComparisonCondition>(outer.Items[1]).Operator);
        Assert.Equal(condition.ToString(), loaded.Condition.ToString());
    }

    [Fact]
    public void 新比较符或嵌套位置的新条件_都要求版本2()
    {
        var (root, _) = Flow(Compare("ref:测量1.Code", ComparisonOperator.Contains, "SN"));
        Assert.Equal(2, FlowSerializer.RequiredFormatVersion(root));

        var loop = ForLoopNode.Count("循环", Operand.Const(1));
        loop.Body.Add(new IfElseNode("内层", new ExpressionCondition { Expression = "true" }));
        var nested = new SequenceNode("根");
        nested.Children.Add(loop);
        Assert.Equal(2, FlowSerializer.RequiredFormatVersion(nested));
        Assert.Equal(2, JsonNode.Parse(FlowSerializer.SaveNode(loop))!["FormatVersion"]!.GetValue<int>());

        string json = FlowSerializer.Save(nested).Replace("\"FormatVersion\": 2", "\"FormatVersion\": 3");
        Assert.Throws<NotSupportedException>(() => FlowSerializer.Load(json));
    }

    // ---------------- 校验 ----------------

    private static FlowValidationResult Validate(ICondition condition) => FlowValidator.Validate(Flow(condition).Root);

    [Fact]
    public void 条件组为空_报错()
    {
        FlowValidationResult result = Validate(Group(ConditionLogic.And, Bool(true), Group(ConditionLogic.Or)));
        FlowValidationIssue issue = Assert.Single(result.Issues);
        Assert.Equal("条件 2", issue.Parameter);
        Assert.Contains("条件组为空", issue.Message);
    }

    [Fact]
    public void 嵌套条件的引用与表达式_按位置报告()
    {
        FlowValidationResult result = Validate(Group(ConditionLogic.And,
            new ExpressionCondition { Expression = "{测量1.Count} >" },
            Group(ConditionLogic.Or, Compare("ref:测量1.Missing", ComparisonOperator.Equal, 1), new ExpressionCondition { Expression = "{没有.变量} > 0" })));

        Assert.Equal(3, result.Issues.Count);
        Assert.Contains(result.Issues, i => i.Parameter == "条件 1" && i.Message.Contains("表达式错误"));
        Assert.Contains(result.Issues, i => i.Parameter == "条件 2.1 左操作数" && i.Message.Contains("测量1.Missing"));
        Assert.Contains(result.Issues, i => i.Parameter == "条件 2.2" && i.Message.Contains("没有.变量"));
    }

    [Fact]
    public void 顶层单条比较_沿用原参数名_一元比较不检查右操作数()
    {
        FlowValidationIssue issue = Assert.Single(Validate(Compare("ref:测量1.Missing", ComparisonOperator.Equal, 1)).Issues);
        Assert.Equal("左操作数", issue.Parameter);

        Assert.True(Validate(new ComparisonCondition { Left = Operand.Ref("测量1.Row"), Operator = ComparisonOperator.IsValid }).IsValid);
    }

    [Fact]
    public void 顶层表达式条件_参数名为条件表达式()
    {
        FlowValidationIssue issue = Assert.Single(Validate(new ExpressionCondition { Expression = "{测量1.Nope} == 1" }).Issues);
        Assert.Equal("条件表达式", issue.Parameter);
    }
}
