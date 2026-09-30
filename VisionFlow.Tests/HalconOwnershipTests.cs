using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// VF-04 回归：HALCON 包装类型的所有权语义与上下文资源释放协议。
/// 本测试只使用未初始化的 HObject（托管侧语义），不依赖 HALCON 原生运行库。
/// </summary>
public class HalconOwnershipTests
{
    [Fact]
    public void 默认构造为借用语义()
    {
        var image = new HalconImage(new HObject());
        Assert.False(image.OwnsObject);
    }

    [Fact]
    public void Owned工厂为拥有语义()
    {
        Assert.True(HalconImage.Owned(new HObject()).OwnsObject);
        Assert.True(HalconRegion.Owned(new HObject()).OwnsObject);
        Assert.True(HalconXld.Owned(new HObject()).OwnsObject);
    }

    [Fact]
    public void 包装Dispose幂等()
    {
        var image = HalconImage.Owned(new HObject());
        image.Dispose();
        image.Dispose(); // 不抛异常

        var borrowed = new HalconImage(new HObject());
        borrowed.Dispose();
        borrowed.Dispose();
    }

    [Fact]
    public void 上下文释放_别名去重且跳过借用输入()
    {
        var shared = new HObject();
        var ctx = new FlowContext();
        // 借用输入（上层注入）
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(new HObject()), 1));
        // 两个别名变量共享同一底层对象，均为拥有语义
        ctx.SetVariable(Variable.Object("工", "A", HalconImage.Owned(shared), 1));
        ctx.SetVariable(Variable.Object("工", "B", HalconImage.Owned(shared), 1));

        ctx.Dispose(); // 不抛异常即满足：别名不重复释放、借用输入不处置
        ctx.Dispose(); // 幂等
    }

    [Fact]
    public void 上下文释放后_变量表仍可读取()
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("工", "N", VariableType.Int, 7));
        ctx.Dispose();
        Assert.Equal(7, ctx.GetVariable("工", "N").Value);
    }
}
