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
    public void 流程输出_对象行_加载与写回保留ClrTypeName()
    {
        var node = new FlowOutputNode("流程输出");
        node.Outputs.Add(new FlowOutputDef
        {
            Name = "Image",
            Kind = VariableKind.Single,
            Type = VariableType.Object,
            ClrTypeName = typeof(HalconImage).AssemblyQualifiedName,
            Value = Operand.Const(0)
        });

        FlowOutputsEditor editor = FlowOutputsEditor.For(node);

        // 加载：对象行保留 Type=Object 与 ClrTypeName（深拷贝到草稿）
        Assert.Equal(VariableType.Object, editor.Rows[0].Type);
        Assert.Equal(typeof(HalconImage).AssemblyQualifiedName, editor.Rows[0].ClrTypeName);

        // 编辑 ClrTypeName 后 Apply 写回 FlowOutputDef
        editor.Rows[0].ClrTypeName = typeof(HalconRegion).AssemblyQualifiedName;
        editor.Apply();

        Assert.Equal(typeof(HalconRegion).AssemblyQualifiedName, node.Outputs[0].ClrTypeName);
        Assert.Equal(VariableType.Object, node.Outputs[0].Type);
        Assert.Equal(VariableKind.Single, node.Outputs[0].Kind);
    }

    [Fact]
    public void 流程输出_校验_对象行缺ClrTypeName提示()
    {
        FlowOutputsEditor editor = FlowOutputsEditor.For(new FlowOutputNode("流程输出"));
        editor.AddRow();
        editor.Rows[0].Name = "Region";
        editor.Rows[0].ValueText = "ref:分割1.Regions";
        editor.Rows[0].Type = VariableType.Object;

        IReadOnlyList<string> problems = editor.Validate();
        Assert.Contains(problems, p => p.Contains("对象类型"));

        editor.Rows[0].ClrTypeName = typeof(HalconRegion).AssemblyQualifiedName;
        problems = editor.Validate();
        Assert.DoesNotContain(problems, p => p.Contains("对象类型"));
    }

    [Fact]
    public void 流程输出_候选保留类型与集合信息()
    {
        var root = new SequenceNode("主流程");
        root.Children.Add(new ToolNode(new ColorSegmentTool("分割1")));
        var outputNode = new FlowOutputNode("流程输出");
        root.Children.Add(outputNode);

        FlowOutputsEditor editor = FlowOutputsEditor.For(outputNode);
        IReadOnlyList<RefCandidate> refs = editor.CandidateRefs(root);

        RefCandidate regions = refs.Single(c => c.Path == "分割1.Regions");
        Assert.Equal(typeof(HalconRegion), regions.ClrType);
        Assert.False(regions.IsCollection);
        RefCandidate areas = refs.Single(c => c.Path == "分割1.ClassAreas");
        Assert.Equal(typeof(int), areas.ClrType);
        Assert.True(areas.IsCollection);

        // 字符串候选保持原格式 ref:模块.变量（OperandText 不变）
        IReadOnlyList<string> strings = editor.Candidates(root);
        Assert.Contains("ref:分割1.Regions", strings);
    }

    [Fact]
    public void 流程输出_类型标签映射_常用与集合后缀()
    {
        Assert.Equal("图像", FlowOutputsEditor.TypeTagLabel(typeof(HalconImage), false));
        Assert.Equal("区域", FlowOutputsEditor.TypeTagLabel(typeof(HalconRegion), false));
        Assert.Equal("XLD", FlowOutputsEditor.TypeTagLabel(typeof(HalconXld), false));
        Assert.Equal("整数", FlowOutputsEditor.TypeTagLabel(typeof(int), false));
        Assert.Equal("小数", FlowOutputsEditor.TypeTagLabel(typeof(double), false));
        Assert.Equal("文本", FlowOutputsEditor.TypeTagLabel(typeof(string), false));
        Assert.Equal("布尔", FlowOutputsEditor.TypeTagLabel(typeof(bool), false));
        Assert.Equal("区域·数组", FlowOutputsEditor.TypeTagLabel(typeof(HalconRegion), true));
        Assert.Equal("整数·数组", FlowOutputsEditor.TypeTagLabel(typeof(int), true));
        // 未映射类型取 CLR 短名
        Assert.Equal("Version", FlowOutputsEditor.TypeTagLabel(typeof(Version), false));
        Assert.Equal("对象", FlowOutputsEditor.TypeTagLabel(null, false));
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
        // 对象类型：类型下拉含“对象”，对象类型下拉（常用 CLR 类型 + 自定义…）写回 ClrTypeName
        Assert.Contains("FlowObjectTypeOption", codeText);
        Assert.Contains("自定义", codeText);
        // 候选下拉两列：ref:路径 + 右侧类型标签（TypeTagLabel），选中后仍存纯 ref:路径
        Assert.Contains("ItemTemplate", codeText);
        Assert.Contains("TypeTagLabel", codeText);
        Assert.Contains("CandidateItem", codeText);
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
