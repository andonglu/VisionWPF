using System.Text.Json.Nodes;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>LD-06 Switch 多分支：匹配规则、执行、公共输出、作用域、校验、流程文件与编辑模型的结构规则。</summary>
public class SwitchNodeTests
{
    private static SwitchNode NewSwitch(Operand selector)
    {
        var node = new SwitchNode("多分支", selector);
        node.Cases.Add(new SwitchCaseNode("正放", "0, 180"));
        node.Cases.Add(new SwitchCaseNode("侧放", "90,270"));
        node.Cases.Add(new SwitchCaseNode("默认", isDefault: true));
        return node;
    }

    private static SwitchCaseNode Case(SwitchNode node, int index) => (SwitchCaseNode)node.Cases[index];

    private static ToolNode Calc(string module, string expressions) =>
        new ToolNode(new ExpressionCalcTool(module) { Expressions = expressions });

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

    [Theory]
    [InlineData(180, "正放")]
    [InlineData(180.0, "正放")]
    [InlineData(270, "侧放")]
    [InlineData("90", "侧放")]
    [InlineData(45, "默认")]
    [InlineData("abc", "默认")]
    public void 匹配规则_数值按数值比较_文本按文本比较(object selector, string expected)
    {
        Assert.Equal(expected, NewSwitch(Operand.Const(selector)).SelectCase(selector).Name);
    }

    [Fact]
    public void 文本匹配区分大小写_支持中文逗号()
    {
        var switchCase = new SwitchCaseNode("合格", "OK，Pass");
        Assert.True(switchCase.Matches("Pass"));
        Assert.False(switchCase.Matches("ok"));
    }

    [Fact]
    public void 执行命中分支并写出公共输出()
    {
        SwitchNode node = NewSwitch(Operand.Ref("计算1.Angle"));
        node.Outputs.Add(new SwitchOutputDef { Name = "Label", Kind = VariableKind.Single, Type = VariableType.String });
        Case(node, 0).Children.Add(Calc("内部1", "t|String|\"正\""));
        Case(node, 0).OutputValues["Label"] = Operand.Ref("内部1.t");
        Case(node, 1).OutputValues["Label"] = Operand.Const("侧");
        Case(node, 2).OutputValues["Label"] = Operand.Const("其他");
        SequenceNode root = Root(Calc("计算1", "Angle|Int|180"), node);
        AssertValid(root);

        using var ctx = new FlowContext();
        NodeResult result = root.Execute(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("正", ctx.GetVariable("多分支", "Label").Value);
        Assert.Contains("正放", ctx.Trace);
        Assert.DoesNotContain("侧放", ctx.Trace);
        Assert.True(ctx.StructuredLogs.Any(l => l.Message.Contains("计算1.Angle（180） → 正放")),
            string.Join(" | ", ctx.StructuredLogs.Select(l => l.Message)));
    }

    [Fact]
    public void 都不匹配且没有默认分支_失败()
    {
        var node = new SwitchNode("多分支", Operand.Const(5));
        node.Cases.Add(new SwitchCaseNode("一", "1"));
        using var ctx = new FlowContext();
        NodeResult result = Root(node).Execute(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("没有匹配的分支", result.Message);
    }

    [Fact]
    public void 循环中分支内跳过本次_后续节点与公共输出都跳过()
    {
        var seen = new List<object>();
        SwitchNode node = NewSwitch(Operand.Ref("Loop.Index"));
        node.Cases.Insert(0, new SwitchCaseNode("跳过", "1"));
        Case(node, 0).Children.Add(new ContinueNode("跳过本次"));
        var loop = ForLoopNode.Count("循环", Operand.Const(3));
        loop.Body.Add(node);
        loop.Body.Add(new ToolNode(new RecorderTool("记录", "Loop.Index", seen)));

        using var ctx = new FlowContext();
        Assert.True(Root(loop).Execute(ctx).IsSuccess);
        Assert.Equal(new object[] { 0, 2 }, seen);
    }

    // ---------------- 作用域与校验 ----------------

    [Fact]
    public void 分支内部输出_对其他分支与Switch外不可见_公共输出对外可见()
    {
        SwitchNode node = NewSwitch(Operand.Const(0));
        node.Outputs.Add(new SwitchOutputDef { Name = "Value", Kind = VariableKind.Single, Type = VariableType.Double });
        foreach (SwitchCaseNode c in node.CaseNodes)
        {
            c.OutputValues["Value"] = Operand.Const(1.0);
        }
        Case(node, 0).Children.Add(Calc("内部1", "x|Double|1"));
        Case(node, 1).Children.Add(Calc("内部2", "y|Double|{内部1.x}"));
        ToolNode after = Calc("之后", "z|Double|{内部1.x} + {多分支.Value}");
        FlowValidationResult result = FlowValidator.Validate(Root(node, after));

        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, i => Assert.Contains("内部1.x", i.Message));
        Assert.Contains(RefCandidateService.ForNode(Root(node, after), after), c => c.Path == "多分支.Value" && c.ClrType == typeof(double));
    }

    [Fact]
    public void 公共输出取值_可引用本分支内部输出_缺失或越界报错()
    {
        SwitchNode node = NewSwitch(Operand.Const(0));
        node.Outputs.Add(new SwitchOutputDef { Name = "Value", Kind = VariableKind.Single, Type = VariableType.Double });
        Case(node, 0).Children.Add(Calc("内部1", "x|Double|1"));
        Case(node, 0).OutputValues["Value"] = Operand.Ref("内部1.x");
        Case(node, 1).OutputValues["Value"] = Operand.Ref("内部1.x");
        FlowValidationResult result = FlowValidator.Validate(Root(node));

        Assert.Contains(result.Issues, i => i.Message.Contains("分支“侧放”") && i.Message.Contains("内部1.x"));
        Assert.Contains(result.Issues, i => i.Message.Contains("分支“默认”未配置取值"));
        Assert.DoesNotContain(result.Issues, i => i.Message.Contains("分支“正放”"));
    }

    [Fact]
    public void 结构问题_校验报告()
    {
        var node = new SwitchNode("多分支");
        node.Cases.Add(new SwitchCaseNode("一", ""));
        node.Cases.Add(new SwitchCaseNode("二", "1,2"));
        node.Cases.Add(new SwitchCaseNode("三", "2"));
        node.Cases.Add(Calc("误放", "a|Int|1"));
        FlowValidationResult result = FlowValidator.Validate(Root(node));

        Assert.Contains(result.Issues, i => i.Parameter == "选择值");
        Assert.Contains(result.Issues, i => i.Message.Contains("只能直接放分支"));
        Assert.Contains(result.Issues, i => i.Message.Contains("有且只有一个默认分支"));
        Assert.Contains(result.Issues, i => i.NodeName == "一" && i.Message.Contains("未设置匹配值"));
        Assert.Contains(result.Issues, i => i.NodeName == "三" && i.Message.Contains("“2”已用于分支“二”"));
    }

    // ---------------- 流程文件 ----------------

    [Fact]
    public void Switch按版本2保存并完整往返()
    {
        SwitchNode node = NewSwitch(Operand.Ref("计算1.Angle"));
        node.Outputs.Add(new SwitchOutputDef { Name = "Label", Kind = VariableKind.Single, Type = VariableType.String });
        Case(node, 0).OutputValues["Label"] = Operand.Const("正");
        Case(node, 0).Children.Add(new BreakNode("跳出"));
        string json = FlowSerializer.Save(Root(Calc("计算1", "Angle|Int|0"), node));

        Assert.Equal(2, JsonNode.Parse(json)!["FormatVersion"]!.GetValue<int>());
        var loaded = Assert.IsType<SwitchNode>(FlowSerializer.Load(json).Children[1]);
        Assert.Equal("计算1.Angle", loaded.Selector.ToString());
        Assert.Equal(new[] { "正放", "侧放", "默认" }, loaded.CaseNodes.Select(c => c.Name).ToArray());
        Assert.Equal("0, 180", Case(loaded, 0).Values);
        Assert.True(Case(loaded, 2).IsDefault);
        Assert.Equal("正", Case(loaded, 0).OutputValues["label"].ConstantValue);
        Assert.IsType<BreakNode>(Case(loaded, 0).Children.Single());
        Assert.Equal(VariableType.String, loaded.Outputs.Single().Type);
    }

    // ---------------- 编辑模型 ----------------

    private static (FlowEditModel Model, SwitchNode Switch) ModelWithSwitch()
    {
        ToolboxRegistry.RegisterDefaults();
        var model = new FlowEditModel();
        var node = (SwitchNode)model.AddNode("switch");
        return (model, node);
    }

    [Fact]
    public void 工具箱默认Switch_两个分支加默认分支()
    {
        var (_, node) = ModelWithSwitch();
        Assert.Equal(3, node.Cases.Count);
        Assert.True(Case(node, 2).IsDefault);
    }

    [Fact]
    public void 节点放入Switch_进入第一个分支_放入分支则进入该分支()
    {
        var (model, node) = ModelWithSwitch();
        FlowNode intoSwitch = model.AddNode("expression-calc", node);
        Assert.Contains(intoSwitch, Case(node, 0).Children);

        FlowNode intoDefault = model.AddNode("expression-calc", Case(node, 2));
        Assert.Contains(intoDefault, Case(node, 2).Children);
        Assert.Equal(3, node.Cases.Count);
    }

    [Fact]
    public void 新增分支在默认分支之前_默认分支不能删除也不能移动()
    {
        var (model, node) = ModelWithSwitch();
        SwitchCaseNode added = model.AddSwitchCase(node);
        Assert.Equal(new[] { "分支 1", "分支 2", "分支 3", "默认" }, node.CaseNodes.Select(c => c.Name).ToArray());
        Assert.Equal("3", added.Values);

        SwitchCaseNode defaultCase = node.DefaultCase;
        Assert.False(model.RemoveNode(defaultCase));
        Assert.False(model.MoveNode(defaultCase, -1));
        Assert.False(model.MoveNode(added, 1));
        Assert.True(model.MoveNode(added, -1));
        Assert.Equal("分支 3", Case(node, 1).Name);
        Assert.True(model.RemoveNode(added));
    }

    [Fact]
    public void 分支只能留在Switch的分支列表中_普通节点不能直接放进分支列表()
    {
        var (model, node) = ModelWithSwitch();
        FlowNode tool = model.AddNode("expression-calc");
        Assert.False(model.CanMoveNode(Case(node, 0), tool, IfBranch.If, FlowInsertPosition.Below));
        Assert.False(model.CanMoveNode(tool, Case(node, 0), IfBranch.If, FlowInsertPosition.Below));
        Assert.True(model.CanMoveNode(tool, Case(node, 1), IfBranch.If, FlowInsertPosition.Into));
        Assert.True(model.CanMoveNode(Case(node, 1), Case(node, 0), IfBranch.If, FlowInsertPosition.Above));
        Assert.False(model.CanMoveNode(Case(node, 0), node.DefaultCase, IfBranch.If, FlowInsertPosition.Below));
    }
}
