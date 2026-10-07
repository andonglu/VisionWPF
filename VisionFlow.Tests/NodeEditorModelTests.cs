using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>节点专用编辑窗口背后的编辑模型：IfElse / Switch 公共输出编辑、子流程编辑草稿。</summary>
public sealed class NodeEditorModelTests : IDisposable
{
    private readonly string _dir;

    public NodeEditorModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vf-node-editor-" + Guid.NewGuid().ToString("N"));
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

    private static ToolNode Calc(string module, string expressions) =>
        new ToolNode(new ExpressionCalcTool(module) { Expressions = expressions });

    private static SequenceNode Root(params FlowNode[] children)
    {
        var root = new SequenceNode("根");
        root.Children.AddRange(children);
        return root;
    }

    private static SwitchNode NewSwitch()
    {
        var node = new SwitchNode("多分支", Operand.Ref("前.k"));
        node.Cases.Add(new SwitchCaseNode("A", "1"));
        node.Cases.Add(new SwitchCaseNode("B", "2"));
        node.Cases.Add(new SwitchCaseNode("默认", isDefault: true));
        return node;
    }

    // ---------------- 公共输出 ----------------

    [Fact]
    public void IfElse公共输出_加载后原样写回()
    {
        var node = new IfElseNode("判断");
        node.Outputs.Add(new BranchOutputDef
        {
            Name = "Label", Kind = VariableKind.Single, Type = VariableType.String,
            IfValue = Operand.Ref("内部.t"), ElseValue = Operand.Const("NG")
        });

        SharedOutputsEditor editor = SharedOutputsEditor.For(node);
        Assert.Equal(new[] { "If 分支", "Else 分支" }, editor.BranchNames);
        SharedOutputRow row = Assert.Single(editor.Rows);
        Assert.Equal("Label", row.Name);
        Assert.Equal("文本", row.OutputType.Label);
        Assert.Equal(new[] { "ref:内部.t", "NG" }, row.Values);

        editor.Apply();
        BranchOutputDef output = Assert.Single(node.Outputs);
        Assert.Equal("Label", output.Name);
        Assert.Equal(VariableType.String, output.Type);
        Assert.Equal("内部.t", output.IfValue.Reference.ToString());
        Assert.Equal("NG", output.ElseValue.ConstantValue);
    }

    [Fact]
    public void IfElse新增输出_类型与取值写回()
    {
        var node = new IfElseNode("判断");
        SharedOutputsEditor editor = SharedOutputsEditor.For(node);
        SharedOutputRow row = editor.AddRow();
        Assert.Equal("输出1", row.Name);
        row.Name = "Score";
        row.OutputType = SharedOutputType.All.First(t => t.Kind == VariableKind.Single && t.Type == VariableType.Int);
        row.Values[0] = "ref:前.n";
        row.Values[1] = "0";
        Assert.Empty(editor.Validate());

        editor.Apply();
        BranchOutputDef output = Assert.Single(node.Outputs);
        Assert.Equal(VariableType.Int, output.Type);
        Assert.False(output.IfValue.IsConstant);
        Assert.True(output.ElseValue.IsConstant);
    }

    [Fact]
    public void Switch公共输出_重命名后各分支取值随之改名_旧名不残留()
    {
        SwitchNode node = NewSwitch();
        node.Outputs.Add(new SwitchOutputDef { Name = "Label", Kind = VariableKind.Single, Type = VariableType.String });
        ((SwitchCaseNode)node.Cases[0]).OutputValues["Label"] = Operand.Const("一");
        ((SwitchCaseNode)node.Cases[1]).OutputValues["Label"] = Operand.Const("二");
        ((SwitchCaseNode)node.Cases[2]).OutputValues["Label"] = Operand.Const("其他");

        SharedOutputsEditor editor = SharedOutputsEditor.For(node);
        Assert.Equal(new[] { "A", "B", "默认" }, editor.BranchNames);
        Assert.Equal(new[] { "一", "二", "其他" }, editor.Rows[0].Values);

        editor.Rows[0].Name = "Text";
        editor.Apply();
        Assert.Equal("Text", Assert.Single(node.Outputs).Name);
        foreach (SwitchCaseNode switchCase in node.CaseNodes)
        {
            Assert.False(switchCase.OutputValues.ContainsKey("Label"));
            Assert.True(switchCase.OutputValues.ContainsKey("Text"));
        }
    }

    [Fact]
    public void 编辑结果通过流程校验并可运行()
    {
        SwitchNode node = NewSwitch();
        ((SwitchCaseNode)node.Cases[0]).Children.Add(Calc("内部A", "t|String|\"甲\""));
        SequenceNode root = Root(Calc("前", "k|Int|1"), node, Calc("后", "r|String|{多分支.Label}"));

        SharedOutputsEditor editor = SharedOutputsEditor.For(node);
        SharedOutputRow row = editor.AddRow();
        row.Name = "Label";
        row.OutputType = SharedOutputType.All.First(t => t.Kind == VariableKind.Single && t.Type == VariableType.String);
        row.Values[0] = "ref:内部A.t";
        row.Values[1] = "乙";
        row.Values[2] = "其他";
        editor.Apply();

        FlowValidationResult validation = FlowValidator.Validate(root);
        Assert.True(validation.IsValid, string.Join("；", validation.Issues.Select(i => i.ToString())));
        using var ctx = new FlowContext();
        Assert.True(root.Execute(ctx).IsSuccess);
        Assert.Equal("甲", ctx.GetVariable("后", "r").Value);
    }

    [Fact]
    public void 校验_名称非法_重名_取值未填()
    {
        SharedOutputsEditor editor = SharedOutputsEditor.For(new IfElseNode("判断"));
        SharedOutputRow a = editor.AddRow();
        a.Name = "A";
        a.Values[0] = "1";
        a.Values[1] = "2";
        SharedOutputRow b = editor.AddRow();
        b.Name = "a";
        b.Values[0] = "1";
        SharedOutputRow c = editor.AddRow();
        c.Name = "x.y";

        IReadOnlyList<string> problems = editor.Validate();
        Assert.Contains(problems, p => p.Contains("“a”重复"));
        Assert.Contains(problems, p => p.Contains("“a”在Else 分支中未填写取值"));
        Assert.Contains(problems, p => p.Contains("“x.y”不能为空"));
        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void 分支取值候选_只含本分支内部输出()
    {
        SwitchNode node = NewSwitch();
        ((SwitchCaseNode)node.Cases[0]).Children.Add(Calc("内部A", "t|String|\"甲\""));
        ((SwitchCaseNode)node.Cases[1]).Children.Add(Calc("内部B", "t|String|\"乙\""));
        SequenceNode root = Root(Calc("前", "k|Int|1"), node);

        SharedOutputsEditor editor = SharedOutputsEditor.For(node);
        IReadOnlyList<string> first = editor.CandidatesFor(root, 0);
        IReadOnlyList<string> second = editor.CandidatesFor(root, 1);
        Assert.Contains("ref:前.k", first);
        Assert.Contains("ref:内部A.t", first);
        Assert.DoesNotContain("ref:内部B.t", first);
        Assert.Contains("ref:内部B.t", second);
        Assert.DoesNotContain("ref:内部A.t", second);
    }

    // ---------------- 子流程 ----------------

    private string DoublerFlow()
    {
        string path = Path.Combine(_dir, "sub", "doubler.vflow.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new FlowOutputNode("结果");
        output.Outputs.Add(new FlowOutputDef { Name = "Y", Kind = VariableKind.Single, Type = VariableType.Double, Value = Operand.Ref("内部.y") });
        File.WriteAllText(path, FlowSerializer.Save(Root(Calc("内部", "y|Double|{Input.X} * 2"), output)));
        return path;
    }

    [Fact]
    public void 子流程草稿_取消前不修改节点_确定后写回()
    {
        DoublerFlow();
        var node = new SubFlowNode("子1") { BaseDirectory = _dir };
        SequenceNode root = Root(Calc("父", "x|Double|21"), node);

        SubFlowEditor editor = SubFlowEditor.For(node);
        editor.FlowFile = editor.ToStoredPath(Path.Combine(_dir, "sub", "doubler.vflow.json"));
        Assert.Equal(Path.Combine("sub", "doubler.vflow.json"), editor.FlowFile);
        editor.Inputs.Add(new SubFlowInputRow { InputPath = "Input.X", ValueText = "ref:父.x" });
        Assert.Equal(string.Empty, node.FlowFile);
        Assert.Empty(node.Inputs);

        Assert.Empty(editor.Validate(root));
        Assert.Equal(string.Empty, node.FlowFile);
        Assert.Empty(node.Inputs);

        editor.Apply();
        Assert.Equal(Path.Combine("sub", "doubler.vflow.json"), node.FlowFile);
        SubFlowInputMapping mapping = Assert.Single(node.Inputs);
        Assert.Equal("父.x", mapping.Value.Reference.ToString());
        using var ctx = new FlowContext();
        Assert.True(root.Execute(ctx).IsSuccess);
        Assert.Equal(42.0, ctx.GetVariable("子1", "Y").Value);
    }

    [Fact]
    public void 子流程草稿校验_未映射的输入报告为子流程内部问题_映射引用无效报错()
    {
        string file = DoublerFlow();
        var node = new SubFlowNode("子1", file);
        SequenceNode root = Root(node);
        SubFlowEditor editor = SubFlowEditor.For(node);

        Assert.Contains(editor.Validate(root), i => i.Parameter == "子流程内部" && i.Message.Contains("Input.X"));

        editor.Inputs.Add(new SubFlowInputRow { InputPath = "Input.X", ValueText = "ref:不存在.x" });
        IReadOnlyList<FlowValidationIssue> issues = editor.Validate(root);
        Assert.Contains(issues, i => i.Parameter == "输入 Input.X");
        Assert.DoesNotContain(issues, i => i.Parameter == "子流程内部");

        editor.Inputs[0].ValueText = "3";
        Assert.Empty(editor.Validate(root));
        Assert.Empty(node.Inputs);
    }

    [Fact]
    public void 子流程草稿_试运行副本不在流程树中_输出以节点名回传()
    {
        string file = DoublerFlow();
        var node = new SubFlowNode("子1", file);
        SubFlowEditor editor = SubFlowEditor.For(node);
        editor.Inputs.Add(new SubFlowInputRow { InputPath = "Input.X", ValueText = "5" });

        SubFlowNode draft = editor.CreateDraft();
        Assert.NotSame(node, draft);
        using var ctx = new FlowContext();
        Assert.True(draft.Execute(ctx).IsSuccess);
        Assert.Equal(10.0, ctx.GetVariable("子1", "Y").Value);
    }

    [Theory]
    [InlineData(@"C:\flows\sub\a.vflow.json", @"C:\flows", @"sub\a.vflow.json")]
    [InlineData(@"C:\other\a.vflow.json", @"C:\flows", @"C:\other\a.vflow.json")]
    [InlineData(@"C:\other\a.vflow.json", null, @"C:\other\a.vflow.json")]
    public void 子流程路径_流程目录内存相对路径_否则存完整路径(string fullPath, string? baseDirectory, string expected)
    {
        Assert.Equal(expected, SubFlowEditor.ToStoredPath(fullPath, baseDirectory));
    }
}
