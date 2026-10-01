using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>工具资源预热与释放（IToolResourceLifecycle / FlowResources）。</summary>
public class FlowResourcesTests
{
    private static HObject TestImage()
    {
        HOperatorSet.GenImageConst(out HObject sizeImage, "byte", 300, 300);
        sizeImage.Dispose();
        HOperatorSet.GenRectangle1(out HObject rect, 100, 100, 160, 200);
        HOperatorSet.RegionToBin(rect, out HObject image, 255, 0, 300, 300);
        rect.Dispose();
        return image;
    }

    private static byte[] CreateModelData(HObject image)
    {
        HOperatorSet.GenRectangle1(out HObject roi, 80, 80, 180, 220);
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        roi.Dispose();
        try
        {
            HOperatorSet.CreateShapeModel(template, "auto", -0.2, 0.4, "auto", "auto", "use_polarity", "auto", "auto", out HTuple modelId);
            try
            {
                return ShapeModelSerialization.Serialize(modelId);
            }
            finally
            {
                HOperatorSet.ClearShapeModel(modelId);
            }
        }
        finally
        {
            template.Dispose();
        }
    }

    private static HalconModelMatchTool MatchTool(string name, byte[]? data)
    {
        return new HalconModelMatchTool(name)
        {
            ImagePath = "Input.Image",
            ShapeModelData = data,
            NumMatches = 1,
            FindStartAngle = -0.2,
            FindExtentAngle = 0.4
        };
    }

    /// <summary>运行一次并返回本次是否发生了模型加载。</summary>
    private static bool RunLoadsModel(ToolBase tool, HObject image)
    {
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ctx.Log.Any(l => l.Contains("模型加载耗时"));
    }

    [Fact]
    public void 预热后首次运行不再加载_释放后按需重新加载()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = TestImage();
        using var imageOwner = image;
        var tool = MatchTool("匹配1", CreateModelData(image));
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));

        FlowPrepareResult prepared = FlowResources.Prepare(root);

        Assert.True(prepared.IsSuccess);
        Assert.Equal(1, prepared.PreparedCount);
        Assert.False(RunLoadsModel(tool, image));

        FlowResources.Release(root);
        Assert.True(RunLoadsModel(tool, image));
        Assert.False(RunLoadsModel(tool, image));
    }

    [Fact]
    public void 预热覆盖分支与循环体_损坏模型报告节点且不影响其他工具()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = TestImage();
        using var imageOwner = image;
        byte[] data = CreateModelData(image);
        var good = MatchTool("匹配好", data);
        var broken = MatchTool("匹配坏", new byte[] { 1, 2, 3, 4 });
        var untaught = MatchTool("未示教", null);
        var ifElse = new IfElseNode("分支");
        ifElse.ElseBranch.Add(new ToolNode(broken));
        var loop = ForLoopNode.Count("循环", Operand.Const(1));
        loop.Body.Add(new ToolNode(good));
        var root = new SequenceNode("根");
        root.Children.Add(ifElse);
        root.Children.Add(loop);
        root.Children.Add(new ToolNode(untaught));

        FlowPrepareResult prepared = FlowResources.Prepare(root);

        Assert.Equal(3, prepared.PreparedCount);
        FlowPrepareIssue issue = Assert.Single(prepared.Issues);
        Assert.Equal("匹配坏", issue.NodeName);
        Assert.False(RunLoadsModel(good, image));
        FlowResources.Release(root);
    }

    [Fact]
    public void 编辑事务_提交释放被替换的原工具_Dispose释放工作副本()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = TestImage();
        using var imageOwner = image;
        var original = MatchTool("匹配1", CreateModelData(image));
        var node = new ToolNode(original);
        original.Prepare();
        Assert.False(RunLoadsModel(original, image));

        HalconModelMatchTool workingCopy;
        using (var transaction = new ToolEditTransaction(node))
        {
            workingCopy = (HalconModelMatchTool)transaction.WorkingCopy;
            workingCopy.MinScore = 0.6;
            Assert.True(RunLoadsModel(workingCopy, image));
            Assert.False(RunLoadsModel(workingCopy, image));
            transaction.Commit();
        }

        Assert.NotSame(original, node.Tool);
        Assert.True(RunLoadsModel(original, image));
        Assert.True(RunLoadsModel(workingCopy, image));
        FlowResources.Release(original);
        FlowResources.Release(workingCopy);
    }

    [Fact]
    public void 删除节点_释放被删子树中的工具资源()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HObject image = TestImage();
        using var imageOwner = image;
        var tool = MatchTool("匹配1", CreateModelData(image));
        var model = new FlowEditModel();
        var loop = ForLoopNode.Count("循环", Operand.Const(1));
        loop.Body.Add(new ToolNode(tool));
        model.Root.Children.Add(loop);
        FlowResources.Prepare(model.Root);

        Assert.True(model.RemoveNode(loop));

        Assert.True(RunLoadsModel(tool, image));
        FlowResources.Release(tool);
    }

    [Fact]
    public void 坐标转换_预热读取标定文件_文件缺失时报告()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string file = Path.Combine(Path.GetTempPath(), $"vf-calib-{Guid.NewGuid():N}.tup");
        try
        {
            HOperatorSet.WriteTuple(new HTuple(1.0, 0.0, 10.0, 0.0, 1.0, 20.0), file);
            var tool = new AffinePointTool("坐标1") { CalibrationFile = file };
            var root = new SequenceNode("根");
            root.Children.Add(new ToolNode(tool));
            Assert.True(FlowResources.Prepare(root).IsSuccess);

            tool.CalibrationFile = file + ".missing";
            FlowPrepareResult missing = FlowResources.Prepare(root);
            Assert.Contains("标定文件不存在", Assert.Single(missing.Issues).Message);
            FlowResources.Release(root);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
