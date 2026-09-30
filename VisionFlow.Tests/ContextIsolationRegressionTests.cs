using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

public class ContextIsolationRegressionTests
{
    private sealed class OutputTool : ToolBase
    {
        private readonly object _value;

        public OutputTool(object value) : base("Output")
        {
            _value = value;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Object(ModuleName, "Value", _value, 1));
            return NodeResult.Ok;
        }
    }

    private sealed class AppendMeasureTool : FollowMeasureToolBase
    {
        public AppendMeasureTool() : base("Measure") { }

        public override NodeResult Run(FlowContext ctx)
        {
            AddResult(ctx, ModuleName, new LineMeasureResult());
            return NodeResult.Ok;
        }
    }

    private static HObject NewImage()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.GenImageConst(out HObject image, "byte", 8, 8);
        return image;
    }

    [Fact]
    public void 覆盖输出_旧资源保留到会话结束并一并释放()
    {
        using HObject first = NewImage();
        using HObject second = NewImage();
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Tool", "Image", HalconImage.Owned(first), 1));
        ctx.SetVariable(Variable.Object("Tool", "Image", HalconImage.Owned(second), 1));

        Assert.True(first.IsInitialized());
        ctx.Dispose();

        Assert.False(first.IsInitialized());
        Assert.False(second.IsInitialized());
    }

    [Fact]
    public void 预览新键别名_不会释放源上下文拥有的图像()
    {
        using HObject image = NewImage();
        using var source = new FlowContext();
        var wrapper = HalconImage.Owned(image);
        source.SetVariable(Variable.Object("Source", "Image", wrapper, 1));
        using FlowContext preview = source.CreatePreviewContext();
        preview.SetVariable(Variable.Object("Alias", "Image", wrapper, 1));
        preview.SetVariable(Variable.Object("Alias", "Raw", image, 1));
        preview.SetVariable(Variable.Object("Alias", "Items", new List<HObject> { image }, 1));

        preview.Dispose();
        Assert.True(image.IsInitialized());
        source.Dispose();
        Assert.False(image.IsInitialized());
    }

    [Fact]
    public void 输出借用输入_不会改变输入包装所有权或释放调用方图像()
    {
        using HObject image = NewImage();
        var input = new HalconImage(image);
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", input, 1));

        new OutputTool(input).Run(ctx);
        ctx.Dispose();

        Assert.False(input.OwnsObject);
        Assert.True(image.IsInitialized());
    }

    [Fact]
    public void 工具输出包装集合_拥有其中的新资源()
    {
        using HObject image = NewImage();
        using var ctx = new FlowContext();
        new OutputTool(new[] { new HalconImage(image) }).Run(ctx);
        ctx.Dispose();
        Assert.False(image.IsInitialized());
    }

    [Fact]
    public void 预览包装副本_不采用或释放借用源图像()
    {
        using HObject image = NewImage();
        var wrapper = HalconImage.Owned(image);
        using var source = new FlowContext();
        source.SetVariable(Variable.Object("Source", "Image", wrapper, 1));
        using FlowContext preview = source.CreatePreviewContext();
        var previewWrapper = preview.GetVariable("Source", "Image").GetValue<HalconImage>();
        Assert.NotSame(wrapper, previewWrapper);
        Assert.False(previewWrapper.OwnsObject);

        new OutputTool(previewWrapper).Run(preview);
        preview.Dispose();

        Assert.False(previewWrapper.OwnsObject);
        Assert.True(image.IsInitialized());
    }

    [Fact]
    public void 预览测量追加_不会修改主运行Results集合()
    {
        using var source = new FlowContext();
        var original = new List<LineMeasureResult> { new LineMeasureResult() };
        source.SetVariable(Variable.Object("Measure", "Results", original, original.Count));
        using FlowContext preview = source.CreatePreviewContext();

        new AppendMeasureTool().Run(preview);

        Assert.Single(original);
        Assert.Equal(2, preview.GetVariable("Measure", "Results").GetValue<List<LineMeasureResult>>().Count);
    }

    [Fact]
    public void 预览集合_保持别名关系并隔离嵌套可变集合()
    {
        using var source = new FlowContext();
        var values = new List<int[]> { new[] { 1, 2 } };
        source.SetVariable(Variable.Object("Source", "A", values, 1));
        source.SetVariable(Variable.Object("Source", "B", values, 1));
        using FlowContext preview = source.CreatePreviewContext();
        var a = preview.GetVariable("Source", "A").GetValue<List<int[]>>();
        var b = preview.GetVariable("Source", "B").GetValue<List<int[]>>();

        a[0][0] = 9;
        a.Add(new[] { 3 });

        Assert.Same(a, b);
        Assert.Single(values);
        Assert.Equal(1, values[0][0]);
    }

    [Fact]
    public void 预览字典_保留比较器并隔离值集合()
    {
        using var source = new FlowContext();
        var values = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Result"] = new List<int> { 1 }
        };
        source.SetVariable(Variable.Object("Source", "Values", values, 1));
        using FlowContext preview = source.CreatePreviewContext();
        var copy = preview.GetVariable("Source", "Values").GetValue<Dictionary<string, List<int>>>();
        copy["RESULT"].Add(2);

        Assert.Single(values["result"]);
        Assert.Equal(2, copy["result"].Count);
    }

    [Fact]
    public void 预览集合循环引用_能够复制且不递归溢出()
    {
        using var source = new FlowContext();
        var values = new List<object>();
        values.Add(values);
        source.SetVariable(Variable.Object("Source", "Values", values, 1));
        using FlowContext preview = source.CreatePreviewContext();
        var copy = preview.GetVariable("Source", "Values").GetValue<List<object>>();

        Assert.NotSame(values, copy);
        Assert.Same(copy, copy[0]);
    }

    [Fact]
    public void 预览保留最内层循环数据_不继承已取消令牌和进度回调()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var source = new FlowContext
        {
            CancellationToken = cancelled.Token,
            Progress = new Progress<FlowProgress>()
        };
        source.PushLoop(new LoopFrame(0, 1, "outer"));
        source.PushLoop(new LoopFrame(2, 3, new List<int> { 1 }));
        using FlowContext preview = source.CreatePreviewContext();

        Assert.Equal(2, preview.CurrentLoop.Index);
        Assert.False(preview.CancellationToken.IsCancellationRequested);
        Assert.Null(preview.Progress);
        preview.PopLoop();
        Assert.Equal("outer", preview.CurrentLoop.CurrentItem);
        Assert.Equal(2, source.CurrentLoop.Index);
    }

    [Fact]
    public void 普通运行不具有降级权限_只允许显式预览开启()
    {
        using var ctx = new FlowContext();
        using FlowContext preview = ctx.CreatePreviewContext();
        using FlowContext fallback = ctx.CreatePreviewContext(allowMatrixFallback: true);
        Assert.False(ctx.IsPreview);
        Assert.False(ctx.AllowMatrixFallback);
        Assert.True(preview.IsPreview);
        Assert.False(preview.AllowMatrixFallback);
        Assert.True(fallback.IsPreview);
        Assert.True(fallback.AllowMatrixFallback);
    }

    [Fact]
    public void 已释放上下文拒绝新输出_但允许读取结果元数据()
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("Output", "Count", VariableType.Int, 1));
        ctx.Dispose();
        Assert.Equal(1, ctx.GetVariable("Output", "Count").Value);
        Assert.Throws<ObjectDisposedException>(() =>
            ctx.SetVariable(Variable.Single("Output", "Count", VariableType.Int, 2)));
        Assert.Throws<ObjectDisposedException>(() => ctx.CreatePreviewContext());
    }
}
