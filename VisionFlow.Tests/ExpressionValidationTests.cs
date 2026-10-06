using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Expressions;
using VisionFlow.Nodes;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>表达式引用校验（LD-01）：参数中的表达式与 [InputRef] 使用同一套作用域规则。</summary>
public class ExpressionValidationTests
{
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    public sealed class ProducerTool : ToolBase
    {
        public ProducerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, 1.0));
            SetOutput(ctx, Variable.Array(ModuleName, "Scores", VariableType.Double, new[] { 0.5, 0.75 }));
            return NodeResult.Ok;
        }
    }

    /// <summary>每行一个表达式；LocalNames / SelfOutputs 为允许的局部名称与本工具输出（逗号分隔）。</summary>
    public sealed class ExpressionTool : ToolBase, IExpressionTool
    {
        public string Expressions { get; set; } = "";
        public string LocalNames { get; set; } = "";
        public string SelfOutputs { get; set; } = "";

        public ExpressionTool(string moduleName) : base(moduleName) { }

        public IEnumerable<ToolExpressionDef> GetExpressions()
        {
            string[] lines = Expressions.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                yield return new ToolExpressionDef($"表达式 第 {i + 1} 行", lines[i])
                {
                    LocalNames = Split(LocalNames),
                    SelfOutputs = Split(SelfOutputs)
                };
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            return NodeResult.Ok;
        }

        private static string[] Split(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    private static ToolNode Producer(string name) => new ToolNode(new ProducerTool(name));

    private static ToolNode Expr(string expressions, string localNames = "", string selfOutputs = "")
    {
        return new ToolNode(new ExpressionTool("计算1")
        {
            Expressions = expressions,
            LocalNames = localNames,
            SelfOutputs = selfOutputs
        });
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

    private static FlowValidationIssue AssertSingleError(FlowNode root, string messagePart)
    {
        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.False(result.IsValid, $"期望校验失败（{messagePart}），实际通过");
        FlowValidationIssue issue = Assert.Single(result.Issues);
        Assert.Contains(messagePart, issue.Message);
        return issue;
    }

    [Fact]
    public void 引用上游输出与数组整体_通过()
    {
        AssertValid(Root(Producer("测量1"),
            Expr("{测量1.Row} * 2\ncount({测量1.Scores}) > 1\n{测量1.Scores[0]} + {测量1.Scores.Count}")));
    }

    [Fact]
    public void 引用不存在的变量_报告所在行()
    {
        FlowValidationIssue issue = AssertSingleError(Root(Producer("测量1"),
            Expr("{测量1.Row}\n{测量1.Missing} + 1")), "测量1.Missing");
        Assert.Equal("表达式 第 2 行", issue.Parameter);
    }

    [Fact]
    public void 引用下游节点输出_拒绝()
    {
        AssertSingleError(Root(Expr("{测量1.Row} > 0"), Producer("测量1")), "测量1.Row");
    }

    [Fact]
    public void 引用已知类型上不存在的成员_拒绝()
    {
        AssertSingleError(Root(Producer("测量1"), Expr("{测量1.Scores[0].Missing}")), "不存在成员");
    }

    [Fact]
    public void 语法错误_报告位置()
    {
        FlowValidationIssue issue = AssertSingleError(Root(Producer("测量1"), Expr("{测量1.Row} +")), "表达式错误");
        Assert.Contains("第 12 个字符处", issue.Message);
    }

    [Fact]
    public void 空表达式_报错()
    {
        AssertSingleError(Root(Expr("  ")), "不能为空");
    }

    [Fact]
    public void 局部名称_只允许声明过的()
    {
        AssertValid(Root(Expr("{Item} > 1 && {itemindex} < 5", localNames: "Item,ItemIndex")));
        AssertSingleError(Root(Expr("{Item} > 1", localNames: "ItemIndex")), "未定义的名称 {Item}");
    }

    [Fact]
    public void 本工具输出_只允许声明过的()
    {
        AssertValid(Root(Expr("{计算1.宽度} * 2", selfOutputs: "宽度")));
        AssertSingleError(Root(Expr("{计算1.高度} * 2", selfOutputs: "宽度")), "计算1.高度");
    }

    [Fact]
    public void 循环变量_只在循环内可用()
    {
        var loop = ForLoopNode.Count("循环", Operand.Const(3));
        loop.Body.Add(Expr("{Loop.Index} + 1"));
        AssertValid(Root(loop));

        AssertSingleError(Root(Expr("{Loop.Index} + 1")), "Loop.Index");
    }

    [Fact]
    public void 分支内部输出_分支外引用拒绝()
    {
        var ifElse = new IfElseNode("分支", new ComparisonCondition
        {
            Left = Operand.Const(1),
            Right = Operand.Const(1),
            Operator = ComparisonOperator.Equal
        });
        ifElse.IfBranch.Add(Producer("测量1"));

        AssertSingleError(Root(ifElse, Expr("{测量1.Row}")), "测量1.Row");
    }
}
