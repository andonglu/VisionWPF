using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// 2026-10-08 既有行为修复（分支 fix/region-behavior）回归：
/// 1）区域工具空结果语义——RegionOutput.Set 先去掉面积 0 的区域对象，全部为 0 时输出 Count=0、Found=false，
///    未找到策略（FailWhenNotFound）生效；非空结果与修复前逐一致（含 Connection 拆分计数不变）。
/// 2）ReduceDomain 限定图像域——区域输入为多对象时先 union1 合并，定义域取全部区域的并集；单对象行为不变。
/// 全部用例直接调用 HALCON 算子，统一标注 Requires=HALCON 并在开头独立检查原生运行库。
/// </summary>
public class RegionBehaviorFixTests
{
    // ======================= 构造辅助 =======================

    private static HObject Rect(double row1, double column1, double row2, double column2)
    {
        HOperatorSet.GenRectangle1(out HObject rect, row1, column1, row2, column2);
        return rect;
    }

    /// <summary>把若干矩形拼成一个区域对象组（每个矩形一个对象）；无参数时得到 0 个对象。</summary>
    private static HObject Rects(params (double R1, double C1, double R2, double C2)[] rects)
    {
        HOperatorSet.GenEmptyObj(out HObject all);
        foreach (var r in rects)
        {
            using HObject rect = Rect(r.R1, r.C1, r.R2, r.C2);
            HOperatorSet.ConcatObj(all, rect, out HObject joined);
            all.Dispose();
            all = joined;
        }
        return all;
    }

    /// <summary>背景 0、各矩形填 200 的 byte 图。</summary>
    private static HObject ImageWithRects(int width, int height, params (double R1, double C1, double R2, double C2)[] rects)
    {
        HOperatorSet.GenImageConst(out HObject image, "byte", width, height);
        foreach (var r in rects)
        {
            using HObject rect = Rect(r.R1, r.C1, r.R2, r.C2);
            HOperatorSet.PaintRegion(rect, image, out HObject painted, 200, "fill");
            image.Dispose();
            image = painted;
        }
        return image;
    }

    /// <summary>区域归上下文所有（Owned），调用方不要再释放传入的区域。</summary>
    private static FlowContext ContextWith(HObject image, HObject? region = null)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        if (region != null)
        {
            HOperatorSet.CountObj(region, out HTuple count);
            ctx.SetVariable(Variable.Object("上游", "Region", HalconRegion.Owned(region), count.I));
        }
        return ctx;
    }

    private static double DomainArea(HObject image)
    {
        HOperatorSet.GetDomain(image, out HObject domain);
        HOperatorSet.AreaCenter(domain, out HTuple area, out _, out _);
        domain.Dispose();
        return area.D;
    }

    private static int ObjectCount(HObject region)
    {
        HOperatorSet.CountObj(region, out HTuple count);
        return count.I;
    }

    private static bool SameRegion(HObject expected, HObject actual)
    {
        HOperatorSet.Union1(expected, out HObject e);
        HOperatorSet.Union1(actual, out HObject a);
        HOperatorSet.SymmDifference(e, a, out HObject diff);
        HOperatorSet.AreaCenter(diff, out HTuple area, out _, out _);
        e.Dispose();
        a.Dispose();
        diff.Dispose();
        return area.D == 0;
    }

    private static HObject OutputRegion(FlowContext ctx, string module, string name = "Region") =>
        ((HalconRegion)ctx.GetVariable(module, name).Value).Object;

    // ======================= 修复 1：空结果语义 =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(true)]
    [InlineData(false)]
    public void 阈值分割_空结果_Count与Found按新语义_未找到策略生效(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = ImageWithRects(64, 48);
        using var ctx = ContextWith(image);
        // 阈值范围 250 ~ 255 取不到任何像素：HALCON store_empty_region 默认 true 时输出 1 个面积 0 的空区域对象
        var tool = new ThresholdTool("阈值1")
        {
            ImagePath = "图像1.Image",
            MinGray = 250,
            MaxGray = 255,
            FailWhenNotFound = failWhenNotFound
        };

        NodeResult result = tool.Run(ctx);

        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.Equal(0, ctx.GetVariable("阈值1", "Count").Value);
        Assert.Equal(false, ctx.GetVariable("阈值1", "Found").Value);
        // 修复后空结果输出 0 个对象（与 select_shape 无匹配等既有“未找到”一致），不再输出 1 个空区域
        Assert.Equal(0, ObjectCount(OutputRegion(ctx, "阈值1")));
        if (!failWhenNotFound)
        {
            Assert.Contains("结果为空", ctx.Log.Last());
        }
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(true)]
    [InlineData(false)]
    public void 阈值分割_空结果_拆连通域后同样按未找到(bool failWhenNotFound)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = ImageWithRects(64, 48);
        using var ctx = ContextWith(image);
        // 22.11 的 connection 对空区域也返回 1 个面积 0 的空对象，修复后同样判为未找到
        var tool = new ThresholdTool("阈值1")
        {
            ImagePath = "图像1.Image",
            MinGray = 250,
            MaxGray = 255,
            Connection = true,
            FailWhenNotFound = failWhenNotFound
        };

        NodeResult result = tool.Run(ctx);

        Assert.Equal(!failWhenNotFound, result.IsSuccess);
        Assert.Equal(0, ctx.GetVariable("阈值1", "Count").Value);
        Assert.Equal(false, ctx.GetVariable("阈值1", "Found").Value);
        Assert.Equal(0, ObjectCount(OutputRegion(ctx, "阈值1")));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 阈值分割_非空结果_与修复前逐一致_Connection拆分计数不变()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        // 三个互不相邻的亮块，Connection 后应得 3 个对象
        using HObject image = ImageWithRects(64, 48, (5, 5, 15, 15), (5, 30, 15, 40), (30, 5, 40, 15));
        HOperatorSet.Threshold(image, out HObject expectedRaw, 150, 255);
        using (expectedRaw)
        {
            // 不拆连通域：与直接调用 threshold 逐像素一致，Count 为 1
            var single = new ThresholdTool("阈值1") { ImagePath = "图像1.Image", MinGray = 150, MaxGray = 255 };
            using var singleCtx = ContextWith(image);
            Assert.True(single.Run(singleCtx).IsSuccess);
            Assert.Equal(1, singleCtx.GetVariable("阈值1", "Count").Value);
            Assert.Equal(true, singleCtx.GetVariable("阈值1", "Found").Value);
            Assert.True(SameRegion(expectedRaw, OutputRegion(singleCtx, "阈值1")));

            // 拆连通域：Count 与直接调用 connection 的对象数一致（修复不得改变非空结果的拆分计数）
            var connected = new ThresholdTool("阈值2")
            {
                ImagePath = "图像1.Image",
                MinGray = 150,
                MaxGray = 255,
                Connection = true
            };
            using var connectedCtx = ContextWith(image);
            Assert.True(connected.Run(connectedCtx).IsSuccess);
            HOperatorSet.Connection(expectedRaw, out HObject expectedConn);
            using (expectedConn)
            {
                Assert.Equal(3, ObjectCount(expectedConn));
                Assert.Equal(3, connectedCtx.GetVariable("阈值2", "Count").Value);
                Assert.Equal(true, connectedCtx.GetVariable("阈值2", "Found").Value);
                Assert.True(SameRegion(expectedConn, OutputRegion(connectedCtx, "阈值2")));
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 阈值分割_空结果_关闭未找到后下游继续执行()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = ImageWithRects(64, 48);
        using var ctx = ContextWith(image);
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new ThresholdTool("阈值1")
        {
            ImagePath = "图像1.Image",
            MinGray = 250,
            MaxGray = 255,
            FailWhenNotFound = false
        }));
        root.Children.Add(new ToolNode(new DelegateTool("后续", c => c.AddLog(FlowLogLevel.Info, "后续节点已执行"))));

        FlowRunResult run = new FlowEngine().Run(root, ctx);

        Assert.True(run.IsSuccess, run.Message);
        Assert.Equal(0, ctx.GetVariable("阈值1", "Count").Value);
        Assert.Equal(false, ctx.GetVariable("阈值1", "Found").Value);
        Assert.Contains(ctx.Log, l => l.Contains("后续节点已执行"));
    }

    // ======================= 修复 2：ReduceDomain 多对象定义域 =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void ReduceDomain_多对象区域_定义域为并集_修复前只取第一个()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = ImageWithRects(101, 71, (5, 5, 15, 15), (50, 80, 60, 95));
        // 两个矩形：11×11 = 121 像素、11×16 = 176 像素，并集 297 像素
        HObject multi = Rects((5, 5, 15, 15), (50, 80, 60, 95));
        using var ctx = ContextWith(image, multi);
        var tool = new ReduceDomainTool("限定1") { ImagePath = "图像1.Image", RegionPath = "上游.Region" };

        Assert.True(tool.Run(ctx).IsSuccess);
        HObject output = ((HalconImage)ctx.GetVariable("限定1", "Image").Value).Object;

        // 修复前 HALCON 的 reduce_domain 只用第一个对象，定义域面积 121；修复后为并集 297
        Assert.Equal(297.0, DomainArea(output));

        // 与直接对并集 reduce_domain 一致
        HOperatorSet.Union1(multi, out HObject union);
        using (union)
        {
            HOperatorSet.ReduceDomain(image, union, out HObject expected);
            using (expected)
            {
                Assert.Equal(DomainArea(expected), DomainArea(output));
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void ReduceDomain_单对象区域_行为不变()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = ImageWithRects(101, 71, (5, 5, 15, 15));
        HObject single = Rects((5, 5, 15, 15));
        using var ctx = ContextWith(image, single);
        var tool = new ReduceDomainTool("限定1") { ImagePath = "图像1.Image", RegionPath = "上游.Region" };

        Assert.True(tool.Run(ctx).IsSuccess);
        HObject output = ((HalconImage)ctx.GetVariable("限定1", "Image").Value).Object;

        Assert.Equal(121.0, DomainArea(output));
        HOperatorSet.ReduceDomain(image, single, out HObject expected);
        using (expected)
        {
            Assert.Equal(DomainArea(expected), DomainArea(output));
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void ReduceDomain_空对象元组_行为按实测固定()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = ImageWithRects(101, 71);
        // 实测（HALCON 22.11）：reduce_domain 对 0 个对象的区域元组不报错，输出一张定义域为空的退化图像
        // （get_domain 为 0 个对象、area_center 面积 0，get_image_size 返回空元组，下游工具无法继续）。
        // 修复 2 只在 >1 个对象时 union1，0 对象路径不改变，故此处固定该既有行为。
        using HObject none = Rects();
        using var ctx = ContextWith(image, none);
        var tool = new ReduceDomainTool("限定1") { ImagePath = "图像1.Image", RegionPath = "上游.Region" };

        Assert.True(tool.Run(ctx).IsSuccess);
        HObject output = ((HalconImage)ctx.GetVariable("限定1", "Image").Value).Object;
        HOperatorSet.GetDomain(output, out HObject domain);
        using (domain)
        {
            Assert.Equal(0, ObjectCount(domain));
        }

        // 对照：1 个空区域对象（不是 0 个）同样成功，输出定义域为空（1 个面积 0 的对象）的图像
        HOperatorSet.GenEmptyRegion(out HObject emptyRegion);
        using var emptyCtx = ContextWith(image, emptyRegion);
        var emptyTool = new ReduceDomainTool("限定2") { ImagePath = "图像1.Image", RegionPath = "上游.Region" };
        Assert.True(emptyTool.Run(emptyCtx).IsSuccess);
        HObject emptyOutput = ((HalconImage)emptyCtx.GetVariable("限定2", "Image").Value).Object;
        Assert.Equal(0.0, DomainArea(emptyOutput));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void ReduceDomain_合并定义域上运行下游工具_与直接对并集reduce_domain一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = ImageWithRects(101, 71, (5, 5, 15, 15), (50, 80, 60, 95));
        HObject multi = Rects((5, 5, 15, 15), (50, 80, 60, 95));
        using var ctx = ContextWith(image, multi);

        // 流程：限定图像域 → 阈值分割（下游）
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new ReduceDomainTool("限定1") { ImagePath = "图像1.Image", RegionPath = "上游.Region" }));
        root.Children.Add(new ToolNode(new ThresholdTool("阈值1") { ImagePath = "限定1.Image", MinGray = 150, MaxGray = 255 }));

        FlowRunResult run = new FlowEngine().Run(root, ctx);
        Assert.True(run.IsSuccess, run.Message);

        // 直接对并集 reduce_domain 后再阈值
        HOperatorSet.Union1(multi, out HObject union);
        using (union)
        {
            HOperatorSet.ReduceDomain(image, union, out HObject reduced);
            using (reduced)
            {
                HOperatorSet.Threshold(reduced, out HObject expected, 150, 255);
                using (expected)
                {
                    Assert.True(SameRegion(expected, OutputRegion(ctx, "阈值1")));
                }
            }
        }
    }
}
