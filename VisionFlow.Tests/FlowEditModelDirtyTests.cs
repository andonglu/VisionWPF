using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;

namespace VisionFlow.Tests;

/// <summary>
/// VF-10 回归：结构编辑（增/删/移动）置脏并触发 StructureChanged；
/// MarkSaved/ReplaceRoot 清除脏标记；失败的操作不影响脏状态。
/// </summary>
public class FlowEditModelDirtyTests
{
    static FlowEditModelDirtyTests()
    {
        ToolboxRegistry.RegisterDefaults();
    }

    private static FlowEditModel NewModelWithNode(out FlowNode node)
    {
        var model = new FlowEditModel();
        node = model.AddNode("mean-image");
        return model;
    }

    [Fact]
    public void NewModel_IsNotDirty()
    {
        var model = new FlowEditModel();
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void AddNode_SetsDirtyAndRaisesStructureChanged()
    {
        var model = new FlowEditModel();
        int raised = 0;
        model.StructureChanged += (s, e) => raised++;

        FlowNode node = model.AddNode("mean-image");

        Assert.NotNull(node);
        Assert.True(model.IsDirty);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void RemoveNode_SetsDirtyAndRaisesStructureChanged()
    {
        FlowEditModel model = NewModelWithNode(out FlowNode node);
        model.MarkSaved();
        int raised = 0;
        model.StructureChanged += (s, e) => raised++;

        Assert.True(model.RemoveNode(node));
        Assert.True(model.IsDirty);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void RemoveNode_FailedRemove_KeepsCleanState()
    {
        var model = new FlowEditModel();
        // 根节点不可删除：失败操作不得置脏
        Assert.False(model.RemoveNode(model.Root));
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void MoveNode_ByDelta_SetsDirty()
    {
        var model = new FlowEditModel();
        FlowNode first = model.AddNode("mean-image");
        model.AddNode("ifelse");
        model.MarkSaved();

        Assert.True(model.MoveNode(first, 1));
        Assert.True(model.IsDirty);
        Assert.Equal(model.Root.Children[1], first);
    }

    [Fact]
    public void MoveNode_ByDelta_OutOfRange_KeepsCleanState()
    {
        FlowEditModel model = NewModelWithNode(out FlowNode node);
        model.MarkSaved();

        Assert.False(model.MoveNode(node, 1));
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void MoveNode_ToTarget_SetsDirty()
    {
        var model = new FlowEditModel();
        FlowNode child = model.AddNode("mean-image");
        FlowNode container = model.AddNode("ifelse");
        model.MarkSaved();

        Assert.True(model.MoveNode(child, container, IfBranch.If, FlowInsertPosition.Into));
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void InsertExistingNode_SetsDirty()
    {
        FlowEditModel model = NewModelWithNode(out FlowNode node);
        model.RemoveNode(node);
        model.MarkSaved();

        Assert.True(model.InsertExistingNode(node));
        Assert.True(model.IsDirty);
        Assert.Contains(node, model.Root.Children);
    }

    [Fact]
    public void MarkDirty_SetsDirty_ForParameterChanges()
    {
        var model = new FlowEditModel();
        model.MarkDirty();
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void MarkSaved_ClearsDirty()
    {
        FlowEditModel model = NewModelWithNode(out _);
        Assert.True(model.IsDirty);

        model.MarkSaved();
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void ReplaceRoot_ClearsDirty()
    {
        FlowEditModel model = NewModelWithNode(out _);
        Assert.True(model.IsDirty);

        model.ReplaceRoot(new SequenceNode("主流程"));
        Assert.False(model.IsDirty);
        Assert.Empty(model.Root.Children);
    }
}
