using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// VF-04 回归（真实运行库）：工具输出归本次运行所有，上下文替换/结束时资源可回收；
/// 借用输入不被释放；别名与共享对象不重复释放；预览上下文不动源上下文的对象。
/// 依赖本机 HALCON 原生运行库与授权；环境缺失时每个用例明确失败。
/// </summary>
public class HalconResourceLifecycleTests
{
    private static HObject GenImage(int width = 16, int height = 16)
    {
        HOperatorSet.GenImageConst(out HObject image, "byte", width, height);
        return image;
    }

    [Fact]
    public void 上下文释放_拥有对象真正回收_借用输入保留()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        using HObject borrowed = GenImage();
        using HObject owned = GenImage();
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(borrowed), 1));
        ctx.SetVariable(Variable.Object("工", "Image", HalconImage.Owned(owned), 1));

        ctx.Dispose();

        Assert.True(borrowed.IsInitialized()); // 借用输入不得释放
        Assert.False(owned.IsInitialized());   // 拥有输出已回收
    }

    [Fact]
    public void 上下文释放_裸HObject与集合与结果项_按引用去重回收()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        using HObject raw = GenImage();
        using HObject shared = GenImage();
        var item = new MatchResultItem { Contour = shared };
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("工", "ResultContour", raw, 1));
        ctx.SetVariable(Variable.Object("工", "Contours", new List<HObject> { shared }, 1));
        ctx.SetVariable(Variable.Array("工", "Items", VariableType.Object, new List<MatchResultItem> { item }));

        ctx.Dispose(); // shared 被 Contours 与 item.Contour 同时引用，只释放一次

        Assert.False(raw.IsInitialized());
        Assert.False(shared.IsInitialized());
    }

    [Fact]
    public void SetOutput输出_随上下文释放回收()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        string imagePath = RepoPaths.Find("src/Image/razors1.png");
        Assert.True(File.Exists(imagePath), "测试图像缺失：" + imagePath);

        using var ctx = new FlowContext();
        var tool = new LoadImageTool("加载1") { FilePath = imagePath };
        NodeResult result = tool.Run(ctx);
        Assert.True(result.Status == NodeStatus.Success, result.Message);

        HObject output = ((HalconImage)ctx.GetVariable("加载1", "Image").Value).Object;
        Assert.True(output.IsInitialized());

        ctx.Dispose();
        Assert.False(output.IsInitialized());
    }

    [Fact]
    public void 预览上下文_只释放自身新写入_不动共享变量()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        using HObject shared = GenImage();
        using var source = new FlowContext();
        source.SetVariable(Variable.Object("工", "Image", HalconImage.Owned(shared), 1));

        using FlowContext preview = source.CreatePreviewContext();
        using HObject previewOutput = GenImage();
        // 预览运行写入自己的输出（与源上下文键不同）
        preview.SetVariable(Variable.Object("预览", "Image", HalconImage.Owned(previewOutput), 1));

        preview.Dispose();

        Assert.True(shared.IsInitialized());          // 共享变量不受预览释放影响
        Assert.False(previewOutput.IsInitialized());  // 预览自身输出已回收

        source.Dispose();
        Assert.False(shared.IsInitialized());
    }

    [Fact]
    public void 预览上下文_覆盖共享键_只释放新值()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        using HObject shared = GenImage();
        using var source = new FlowContext();
        source.SetVariable(Variable.Object("工", "Image", HalconImage.Owned(shared), 1));

        using FlowContext preview = source.CreatePreviewContext();
        using HObject overwritten = GenImage();
        // 预览覆盖了共享键：新值归预览所有，旧值仍归源上下文
        preview.SetVariable(Variable.Object("工", "Image", HalconImage.Owned(overwritten), 1));

        preview.Dispose();

        Assert.True(shared.IsInitialized());
        Assert.False(overwritten.IsInitialized());

        source.Dispose();
        Assert.False(shared.IsInitialized());
    }

    [Fact]
    public void 重复运行_每次结果独立回收()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        string imagePath = RepoPaths.Find("src/Image/razors1.png");
        var engine = new FlowEngine();
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new LoadImageTool("加载1") { FilePath = imagePath }));
        root.Children.Add(new ToolNode(new ThresholdTool("二值化1")
        {
            ImagePath = "加载1.Image",
            MinGray = 80,
            MaxGray = 255,
            Connection = true
        }));

        // 模拟宿主“新结果替换旧结果”的循环：旧上下文替换后即回收
        FlowContext? previous = null;
        FlowContext? current = null;
        try
        {
            for (int i = 0; i < 10; i++)
            {
                current = new FlowContext();
                FlowRunResult result = engine.Run(root, current);
                Assert.True(result.IsSuccess, result.Message);

                HObject region = ((HalconRegion)current.GetVariable("二值化1", "Region").Value).Object;
                Assert.True(region.IsInitialized());

                previous?.Dispose();
                if (previous != null)
                {
                    HObject oldRegion = ((HalconRegion)previous.GetVariable("二值化1", "Region").Value).Object;
                    Assert.False(oldRegion.IsInitialized());
                }
                previous = current;
                current = null;
            }
        }
        finally
        {
            current?.Dispose();
            previous?.Dispose();
        }
    }
}
