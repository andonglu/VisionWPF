using System.Text.Json.Nodes;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>LD-07：While 条件循环、跳出循环、跳过本次（运行语义、作用域校验、流程文件）。</summary>
public class LoopControlTests
{
    private static SequenceNode Root(params FlowNode[] children)
    {
        var root = new SequenceNode("根");
        root.Children.AddRange(children);
        return root;
    }

    private static ComparisonCondition Compare(string reference, ComparisonOperator op, object value)
    {
        return new ComparisonCondition { Left = Operand.Ref(reference), Operator = op, Right = Operand.Const(value) };
    }

    private static IfElseNode When(ICondition condition, params FlowNode[] ifBranch)
    {
        var node = new IfElseNode("判断" + Guid.NewGuid().ToString("N").Substring(0, 4), condition);
        node.IfBranch.AddRange(ifBranch);
        return node;
    }

    private static ToolNode Record(List<object> sink, string path = "Loop.Index") =>
        new ToolNode(new RecorderTool("记录" + Guid.NewGuid().ToString("N").Substring(0, 4), path, sink));

    private static void AssertSuccess(FlowNode root, FlowContext ctx)
    {
        NodeResult result = root.Execute(ctx);
        Assert.True(result.IsSuccess, result.Message);
    }

    private static void AssertValid(FlowNode root)
    {
        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.True(result.IsValid, "期望校验通过，实际错误：" + string.Join("；", result.Issues.Select(i => i.ToString())));
    }

    // ---------------- 运行语义 ----------------

    [Fact]
    public void For循环内跳出循环_停止后续节点与后续迭代()
    {
        var before = new List<object>();
        var after = new List<object>();
        var loop = ForLoopNode.Count("循环", Operand.Const(5));
        loop.Body.Add(Record(before));
        loop.Body.Add(When(Compare("Loop.Index", ComparisonOperator.Equal, 2), new BreakNode("跳出")));
        loop.Body.Add(Record(after));
        SequenceNode root = Root(loop);
        AssertValid(root);

        using var ctx = new FlowContext();
        AssertSuccess(root, ctx);
        Assert.Equal(new object[] { 0, 1, 2 }, before);
        Assert.Equal(new object[] { 0, 1 }, after);
        Assert.Equal(LoopControlSignal.None, ctx.LoopControl);
    }

    [Fact]
    public void 跳过本次_只跳过当前迭代剩余节点()
    {
        var after = new List<object>();
        var loop = ForLoopNode.Count("循环", Operand.Const(4));
        loop.Body.Add(When(Compare("Loop.Index", ComparisonOperator.Equal, 1), new ContinueNode("跳过")));
        loop.Body.Add(Record(after));

        using var ctx = new FlowContext();
        AssertSuccess(Root(loop), ctx);
        Assert.Equal(new object[] { 0, 2, 3 }, after);
    }

    [Fact]
    public void 嵌套循环_跳出只影响最内层()
    {
        var outer = new List<object>();
        var inner = new List<object>();
        var outerLoop = ForLoopNode.Count("外层", Operand.Const(3));
        var innerLoop = ForLoopNode.Count("内层", Operand.Const(5));
        innerLoop.Body.Add(When(Compare("Loop.Index", ComparisonOperator.Equal, 1), new BreakNode("跳出")));
        innerLoop.Body.Add(Record(inner));
        outerLoop.Body.Add(innerLoop);
        outerLoop.Body.Add(Record(outer));

        using var ctx = new FlowContext();
        AssertSuccess(Root(outerLoop), ctx);
        Assert.Equal(new object[] { 0, 1, 2 }, outer);
        Assert.Equal(new object[] { 0, 0, 0 }, inner);
    }

    [Fact]
    public void 分支内跳过本次_不计算分支公共输出()
    {
        var loop = ForLoopNode.Count("循环", Operand.Const(2));
        var branch = When(Compare("Loop.Index", ComparisonOperator.GreaterOrEqual, 0), new ContinueNode("跳过"),
            new ToolNode(new ExpressionCalcTool("计算1") { Expressions = "x|Int|1" }));
        branch.Outputs.Add(new BranchOutputDef
        {
            Name = "X",
            Kind = VariableKind.Single,
            Type = VariableType.Int,
            IfValue = Operand.Ref("计算1.x"),
            ElseValue = Operand.Const(0)
        });
        loop.Body.Add(branch);

        using var ctx = new FlowContext();
        AssertSuccess(Root(loop), ctx);
        Assert.False(ctx.TryGetVariable(branch.Name, "X", out _));
    }

    [Fact]
    public void While先判断_按条件执行且循环体可用LoopIndex()
    {
        var seen = new List<object>();
        var loop = new WhileLoopNode("条件循环", Compare("Loop.Index", ComparisonOperator.Less, 3));
        loop.Body.Add(Record(seen));
        SequenceNode root = Root(loop);
        AssertValid(root);

        using var ctx = new FlowContext();
        AssertSuccess(root, ctx);
        Assert.Equal(new object[] { 0, 1, 2 }, seen);
        Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("共执行 3 次"));
    }

    [Fact]
    public void While先判断且条件一开始不成立_一次也不执行()
    {
        var seen = new List<object>();
        var loop = new WhileLoopNode("条件循环", Compare("Loop.Index", ComparisonOperator.Less, 0));
        loop.Body.Add(Record(seen));

        using var ctx = new FlowContext();
        AssertSuccess(Root(loop), ctx);
        Assert.Empty(seen);
    }

    [Fact]
    public void While先执行后判断_条件可引用循环体输出()
    {
        var loop = new WhileLoopNode("重试", Compare("计算1.x", ComparisonOperator.Less, 4)) { TestAfterBody = true };
        loop.Body.Add(new ToolNode(new ExpressionCalcTool("计算1") { Expressions = "x|Int|{Loop.Index} * 2" }));
        SequenceNode root = Root(loop);
        AssertValid(root);

        using var ctx = new FlowContext();
        AssertSuccess(root, ctx);
        Assert.Equal(4, ctx.GetVariable("计算1", "x").Value);
        Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("共执行 3 次"));
    }

    [Fact]
    public void While达到最多执行次数_失败()
    {
        var loop = new WhileLoopNode("死循环", new ExpressionCondition { Expression = "true" }) { MaxIterations = 5 };
        using var ctx = new FlowContext();
        NodeResult result = Root(loop).Execute(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("已执行 5 次", result.Message);
    }

    [Fact]
    public void While内跳出循环()
    {
        var seen = new List<object>();
        var loop = new WhileLoopNode("条件循环", new ExpressionCondition { Expression = "true" }) { MaxIterations = 10 };
        loop.Body.Add(Record(seen));
        loop.Body.Add(When(new ExpressionCondition { Expression = "{Loop.Index} >= 3" }, new BreakNode("跳出")));

        using var ctx = new FlowContext();
        AssertSuccess(Root(loop), ctx);
        Assert.Equal(new object[] { 0, 1, 2, 3 }, seen);
    }

    [Fact]
    public void 循环外的跳出循环_运行失败且校验报错()
    {
        SequenceNode root = Root(new BreakNode("跳出"), new ContinueNode("跳过"));
        FlowValidationResult validation = FlowValidator.Validate(root);
        Assert.Equal(2, validation.Issues.Count(i => i.Message.Contains("只能放在循环体内")));

        using var ctx = new FlowContext();
        NodeResult result = root.Execute(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("只能放在循环体内", result.Message);
    }

    [Fact]
    public void 嵌套在分支中的跳出循环_只要在循环内就通过校验()
    {
        var loop = ForLoopNode.Count("循环", Operand.Const(1));
        loop.Body.Add(When(new ExpressionCondition { Expression = "true" }, When(new ExpressionCondition { Expression = "true" }, new BreakNode("跳出"))));
        AssertValid(Root(loop));
    }

    // ---------------- 作用域 ----------------

    [Theory]
    [InlineData("Loop.Count", "不提供 Loop.Count")]
    [InlineData("Loop.Current", "不提供 Loop.Current")]
    public void While条件与循环体_只提供LoopIndex(string reference, string messagePart)
    {
        var loop = new WhileLoopNode("条件循环", Compare(reference, ComparisonOperator.Less, 3));
        loop.Body.Add(new ToolNode(new ExpressionCalcTool("计算1") { Expressions = $"v|String|str({{{reference}}})" }));
        FlowValidationResult result = FlowValidator.Validate(Root(loop));
        Assert.Equal(2, result.Issues.Count(i => i.Message.Contains(messagePart)));

        RefScope bodyScope = RefCandidateService.ScopeForNode(Root(loop), loop.Body[0]);
        Assert.Equal(RefLoopMode.While, bodyScope.LoopMode);
        Assert.Contains(bodyScope.Candidates, c => c.Path == "Loop.Index");
        Assert.DoesNotContain(bodyScope.Candidates, c => c.Path == "Loop.Count");
    }

    [Fact]
    public void While先判断时_条件不能引用循环体输出()
    {
        var loop = new WhileLoopNode("条件循环", Compare("计算1.x", ComparisonOperator.Less, 4));
        loop.Body.Add(new ToolNode(new ExpressionCalcTool("计算1") { Expressions = "x|Int|1" }));
        FlowValidationIssue issue = Assert.Single(FlowValidator.Validate(Root(loop)).Issues);
        Assert.Contains("计算1.x", issue.Message);
    }

    [Fact]
    public void While配置问题_校验报告()
    {
        FlowValidationResult result = FlowValidator.Validate(Root(new WhileLoopNode("条件循环") { MaxIterations = 0 }));
        Assert.Contains(result.Issues, i => i.Parameter == "条件" && i.Message.Contains("未设置条件"));
        Assert.Contains(result.Issues, i => i.Parameter == "最多执行次数");
    }

    [Fact]
    public void While循环体内部输出_对循环外不可见()
    {
        var loop = new WhileLoopNode("条件循环", Compare("Loop.Index", ComparisonOperator.Less, 1));
        loop.Body.Add(new ToolNode(new ExpressionCalcTool("计算1") { Expressions = "x|Int|1" }));
        var after = new ToolNode(new ExpressionCalcTool("计算2") { Expressions = "y|Int|{计算1.x}" });
        FlowValidationIssue issue = Assert.Single(FlowValidator.Validate(Root(loop, after)).Issues);
        Assert.Contains("计算1.x", issue.Message);
    }

    // ---------------- 流程文件与编辑模型 ----------------

    [Fact]
    public void While与跳出跳过_按版本2保存并完整往返()
    {
        var condition = new ConditionGroup { Logic = ConditionLogic.And };
        condition.Items.Add(Compare("Loop.Index", ComparisonOperator.Less, 3));
        condition.Items.Add(new ExpressionCondition { Expression = "true" });
        var loop = new WhileLoopNode("条件循环", condition) { MaxIterations = 7, TestAfterBody = true };
        loop.Body.Add(new ContinueNode("跳过"));
        loop.Body.Add(new BreakNode("跳出"));
        string json = FlowSerializer.Save(Root(loop));

        Assert.Equal(2, JsonNode.Parse(json)!["FormatVersion"]!.GetValue<int>());
        var loaded = Assert.IsType<WhileLoopNode>(FlowSerializer.Load(json).Children[0]);
        Assert.Equal((7, true, "条件循环"), (loaded.MaxIterations, loaded.TestAfterBody, loaded.Name));
        Assert.Equal(condition.ToString(), loaded.Condition.ToString());
        Assert.IsType<ContinueNode>(loaded.Body[0]);
        Assert.IsType<BreakNode>(loaded.Body[1]);
    }

    [Fact]
    public void 普通For循环_仍按版本1保存且不写While字段()
    {
        var loop = ForLoopNode.Count("循环", Operand.Const(2));
        string json = FlowSerializer.Save(Root(loop));
        JsonNode node = JsonNode.Parse(json)!;
        Assert.Equal(1, node["FormatVersion"]!.GetValue<int>());
        Assert.Null(node["Children"]![0]!["Loop"]!["MaxIterations"]);
    }

    [Fact]
    public void 编辑模型_可向While循环体插入节点()
    {
        var loop = new WhileLoopNode("条件循环");
        Assert.Same(loop.Body, FlowEditModel.GetChildList(loop));

        ToolboxRegistry.RegisterDefaults();
        Assert.IsType<WhileLoopNode>(ToolboxRegistry.Items.Single(i => i.Id == "whileloop").Factory());
        Assert.IsType<BreakNode>(ToolboxRegistry.Items.Single(i => i.Id == "break").Factory());
        Assert.IsType<ContinueNode>(ToolboxRegistry.Items.Single(i => i.Id == "continue").Factory());
    }
}
