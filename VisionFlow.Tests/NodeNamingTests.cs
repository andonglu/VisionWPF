using System.Linq;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// VF-08 回归：NodeNaming 从主窗口平移到 EditorCore 后行为不变——
/// 模块名去数字取基名、新节点命名唯一且与节点名同步、嵌套节点参与枚举。
/// </summary>
public class NodeNamingTests
{
    [Theory]
    [InlineData("模板匹配2", "模板匹配")]
    [InlineData("Region筛选10", "Region筛选")]
    [InlineData("123", "123")]
    [InlineData("", "工具")]
    public void 模块名基名_去掉尾随数字(string moduleName, string expected)
    {
        Assert.Equal(expected, NodeNaming.ModuleNameBase(moduleName));
    }

    [Fact]
    public void 新工具节点_模块名去重并同步节点名()
    {
        var root = new SequenceNode("根");
        var first = new ToolNode(new ThresholdTool("阈值分割")) { Name = "阈值分割" };
        root.Children.Add(first);
        var added = new ToolNode(new ThresholdTool("阈值分割"));

        NodeNaming.EnsureUniqueNewNodeName(root, added);

        Assert.Equal("阈值分割1", added.Tool.ModuleName);
        Assert.Equal("阈值分割1", added.Name);
    }

    [Fact]
    public void 新工具节点_排除自身_不与自身冲突()
    {
        var root = new SequenceNode("根");
        var added = new ToolNode(new ThresholdTool("二值化")) { Name = "二值化" };
        root.Children.Add(added);

        NodeNaming.EnsureUniqueNewNodeName(root, added);

        Assert.Equal("二值化1", added.Tool.ModuleName);
    }

    [Fact]
    public void 命名去重_嵌套节点与大小写都参与冲突()
    {
        var root = new SequenceNode("根");
        var ifElse = new IfElseNode("分支");
        ifElse.IfBranch.Add(new ToolNode(new ThresholdTool("定位1")) { Name = "定位1" });
        var loop = ForLoopNode.Count("循环", Operand.Const(1));
        loop.Body.Add(new ToolNode(new ThresholdTool("定位2")) { Name = "定位2" });
        root.Children.Add(ifElse);
        root.Children.Add(loop);

        var added = new ToolNode(new ThresholdTool("定位"));
        root.Children.Add(added);
        NodeNaming.EnsureUniqueNewNodeName(root, added);
        Assert.Equal("定位3", added.Tool.ModuleName);

        // 大小写不敏感：已有 "ABC1" 时 "abc" 的下一个可用名跳过 abc1
        root.Children.Add(new ToolNode(new ThresholdTool("ABC1")) { Name = "ABC1" });
        var another = new ToolNode(new ThresholdTool("abc"));
        NodeNaming.EnsureUniqueNewNodeName(root, another);
        Assert.Equal("abc2", another.Tool.ModuleName);
    }

    [Fact]
    public void 非工具节点_不改名()
    {
        var root = new SequenceNode("根");
        var added = new IfElseNode("条件分支");
        NodeNaming.EnsureUniqueNewNodeName(root, added);
        Assert.Equal("条件分支", added.Name);
    }
}
