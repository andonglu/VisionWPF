using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// 流程输出节点（FlowOutputNode）输出表编辑：编辑模型（FlowOutputsEditor）运行期行为
/// 与 WPF 编辑窗口 / 参数面板分支的源码级登记。不加载 WPF 程序集（Requires 未标注，纳入非 HALCON 门禁）。
/// </summary>
public class FlowOutputsEditorUiTests
{
    [Fact]
    public void 流程输出_加载为深拷贝_编辑不写回节点()
    {
        var node = new FlowOutputNode("流程输出");
        node.Outputs.Add(new FlowOutputDef { Name = "Ok", Kind = VariableKind.Single, Type = VariableType.Bool, Value = Operand.Const(true) });

        FlowOutputsEditor editor = FlowOutputsEditor.For(node);
        editor.Rows[0].Name = "改名";
        editor.Rows[0].ValueText = "ref:other.Value";

        Assert.Equal("Ok", node.Outputs[0].Name);
        Assert.True(node.Outputs[0].Value.IsConstant);
    }

    [Fact]
    public void 流程输出_确定写回新实例_再改草稿不影响节点()
    {
        var node = new FlowOutputNode("流程输出");
        var original = new FlowOutputDef { Name = "Ok", Kind = VariableKind.Single, Type = VariableType.Bool, Value = Operand.Const(true) };
        node.Outputs.Add(original);

        FlowOutputsEditor editor = FlowOutputsEditor.For(node);
        editor.Apply();

        Assert.Single(node.Outputs);
        Assert.Equal("Ok", node.Outputs[0].Name);
        Assert.NotSame(original, node.Outputs[0]);

        editor.Rows[0].Name = "再改";
        Assert.Equal("Ok", node.Outputs[0].Name);
    }

    [Fact]
    public void 流程输出_写回解析引用与常量()
    {
        var node = new FlowOutputNode("流程输出");
        FlowOutputsEditor editor = FlowOutputsEditor.For(node);
        editor.AddRow();
        editor.Rows[0].Name = "Code";
        editor.Rows[0].ValueText = "ref:测量1.Count";
        editor.AddRow();
        editor.Rows[1].Name = "Message";
        editor.Rows[1].ValueText = "OK";

        editor.Apply();

        Assert.False(node.Outputs[0].Value.IsConstant);
        Assert.Equal("测量1.Count", node.Outputs[0].Value.Reference.ToString());
        Assert.True(node.Outputs[1].Value.IsConstant);
        Assert.Equal("OK", node.Outputs[1].Value.ConstantValue);
    }

    [Fact]
    public void 流程输出_校验_空名_重名_取值未填()
    {
        FlowOutputsEditor editor = FlowOutputsEditor.For(new FlowOutputNode("流程输出"));
        editor.AddRow();
        editor.AddRow();
        editor.Rows[0].Name = "  ";

        IReadOnlyList<string> problems = editor.Validate();

        Assert.Contains(problems, p => p.Contains("不能为空"));
        Assert.Contains(problems, p => p.Contains("未填写取值"));

        editor.Rows[0].Name = "Code";
        editor.Rows[0].ValueText = "1";
        editor.Rows[1].Name = "code";
        editor.Rows[1].ValueText = "2";
        problems = editor.Validate();
        Assert.Contains(problems, p => p.Contains("重复"));
        Assert.DoesNotContain(problems, p => p.Contains("未填写取值"));
    }

    [Fact]
    public void 流程输出编辑窗口_文件存在_构造约定为编辑模型加流程根()
    {
        string xaml = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", "WpfFlowOutputsEditWindow.xaml"));
        string code = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", "WpfFlowOutputsEditWindow.xaml.cs"));
        Assert.True(File.Exists(xaml), xaml);
        Assert.True(File.Exists(code), code);
        string codeText = File.ReadAllText(code);
        Assert.Contains("partial class WpfFlowOutputsEditWindow : Window", codeText);
        Assert.Contains("public WpfFlowOutputsEditWindow(FlowOutputsEditor editor, FlowNode root)", codeText);
        // 校验写回：确定前阻止空名称 / 空取值（与 FlowOutputNode.OnExecute 的运行时失败条件一致）
        Assert.Contains("_editor.Validate()", codeText);
    }

    [Fact]
    public void 参数面板_流程输出分支打开编辑窗口且无过期提示()
    {
        string source = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Ui/ParameterPanelBuilder.cs"));
        int branch = source.IndexOf("node is FlowOutputNode", StringComparison.Ordinal);
        Assert.True(branch > 0, "ParameterPanelBuilder 缺少 FlowOutputNode 分支");
        string section = source.Substring(branch, source.IndexOf("private void BuildToolParameterPanel", StringComparison.Ordinal) - branch);
        Assert.Contains("BuildFlowOutputParameterPanel(outputNode);", section);
        Assert.DoesNotContain("WinForms", section);
        // 面板方法提供摘要、编辑入口与默认输出按钮，确定后写回并刷新
        int panel = source.IndexOf("private void BuildFlowOutputParameterPanel", StringComparison.Ordinal);
        Assert.True(panel > 0, "缺少 BuildFlowOutputParameterPanel 方法");
        string panelBody = source.Substring(panel, source.IndexOf("private void OpenFlowOutputsEditor", StringComparison.Ordinal) - panel);
        Assert.Contains("编辑输出...", panelBody);
        Assert.Contains("添加默认输出 Ok/Code/Message", panelBody);
        int open = source.IndexOf("private void OpenFlowOutputsEditor", StringComparison.Ordinal);
        string openBody = source.Substring(open, source.IndexOf("private void BuildSwitchParameterPanel", StringComparison.Ordinal) - open);
        Assert.Contains("new WpfFlowOutputsEditWindow(", openBody);
        Assert.Contains("editor.Apply();", openBody);
        // 双击入口同样能打开编辑窗口
        Assert.Contains("node is FlowOutputNode", source.Substring(source.IndexOf("public bool OpenNodeEditor", StringComparison.Ordinal)));
    }
}
