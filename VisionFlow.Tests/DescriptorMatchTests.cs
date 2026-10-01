using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>描述子匹配（DescriptorMatchTool）、数值区间分类（RangeClassifyTool）与饼干盒示例。</summary>
public class DescriptorMatchTests
{
    [Theory]
    [InlineData(90.0, ClassifyValueMode.Degrees, "侧放", 0)]
    [InlineData(270.0, ClassifyValueMode.Degrees, "侧放", 1)]
    [InlineData(-90.0, ClassifyValueMode.Degrees, "侧放", 1)]
    [InlineData(180.0, ClassifyValueMode.Degrees, "倒放", 2)]
    [InlineData(45.0, ClassifyValueMode.Degrees, "正放", -1)]
    [InlineData(Math.PI, ClassifyValueMode.RadiansToDegrees, "倒放", 2)]
    [InlineData(-0.1, ClassifyValueMode.RadiansToDegrees, "正放", -1)]
    [InlineData(double.NaN, ClassifyValueMode.RadiansToDegrees, "未找到", -1)]
    public void 区间分类_开区间按顺序匹配_角度归一与无效值(double value, ClassifyValueMode mode, string label, int ruleIndex)
    {
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("上游", "Angle", VariableType.Double, value));
        var tool = new RangeClassifyTool("分类1")
        {
            ValuePath = "上游.Angle",
            ValueMode = mode,
            Rules = "侧放=45~135；侧放=225~315;倒放=135~225",
            DefaultLabel = "正放",
            InvalidLabel = "未找到"
        };

        Assert.True(tool.Run(ctx).IsSuccess);

        Assert.Equal(label, ctx.GetVariable("分类1", "Label").Value);
        Assert.Equal(ruleIndex, ctx.GetVariable("分类1", "RuleIndex").Value);
        Assert.Equal(ruleIndex >= 0, ctx.GetVariable("分类1", "Matched").Value);
    }

    [Theory]
    [InlineData("侧放45~135", "标签=下限~上限")]
    [InlineData("侧放=a~135", "不是有效数字")]
    [InlineData("侧放=135~45", "下限必须小于上限")]
    public void 区间分类_规则格式错误明确失败(string rules, string message)
    {
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("上游", "Angle", VariableType.Double, 1.0));
        var tool = new RangeClassifyTool("分类1") { ValuePath = "上游.Angle", Rules = rules };

        NodeResult result = tool.Run(ctx);

        Assert.False(result.IsSuccess);
        Assert.Contains(message, result.Message);
    }

    /// <summary>可复现的纹理图：固定随机种子的噪声经平滑，角点丰富。</summary>
    private static HObject TexturedImage()
    {
        HOperatorSet.SetSystem("seed_rand", 42);
        HOperatorSet.GenImageConst(out HObject blank, "byte", 400, 400);
        HOperatorSet.GenImageProto(blank, out HObject gray, 128);
        blank.Dispose();
        HOperatorSet.AddNoiseWhite(gray, out HObject noisy, 120);
        gray.Dispose();
        HOperatorSet.MeanImage(noisy, out HObject texture, 5, 5);
        noisy.Dispose();
        return texture;
    }

    private static DescriptorMatchTool SmallTool()
    {
        // 测试用较小的训练参数以缩短训练时间；不写本机缓存，避免测试之间互相影响
        return new DescriptorMatchTool("描述子1")
        {
            ImagePath = "Input.Image",
            Depth = 7,
            NumberFerns = 10,
            PatchSize = 17,
            MinScale = 0.8,
            MaxScale = 1.2,
            UseModelFileCache = false
        };
    }

    [Fact]
    public void 描述子匹配_旋转平移后定位到模板中心并给出平面内转角()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject reference = TexturedImage();
        DescriptorMatchTool tool = SmallTool();
        tool.CreateTemplate(reference, 120, 120, 280, 280);
        HomMat2D motion = HomMat2D.FromPoses(200, 200, 0, 210, 190, 0.6);
        HOperatorSet.AffineTransImage(reference, out HObject moved, motion.Data, "constant", "false");
        using var movedOwner = moved;
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(moved), 1));

        NodeResult result = tool.Run(ctx);

        Assert.True(result.IsSuccess, result.Message);
        Assert.InRange((double)ctx.GetVariable("描述子1", "Row").Value, 208, 212);
        Assert.InRange((double)ctx.GetVariable("描述子1", "Column").Value, 188, 192);
        Assert.InRange((double)ctx.GetVariable("描述子1", "Angle").Value, 0.57, 0.63);
        var best = (MatchResultItem)ctx.GetVariable("描述子1", "BestMatch").Value;
        Assert.Equal(9, best.ProjectiveHomMat.Length);
        best.HomMat.TransformPoint(120, 120, out double cornerRow, out double cornerColumn);
        motion.TransformPoint(120, 120, out double expectedRow, out double expectedColumn);
        Assert.InRange(cornerRow, expectedRow - 2, expectedRow + 2);
        Assert.InRange(cornerColumn, expectedColumn - 2, expectedColumn + 2);
        var contour = (HalconXld)ctx.GetVariable("描述子1", "ResultContour").Value;
        HOperatorSet.GetContourXld(contour.Object, out HTuple rows, out _);
        Assert.Equal(5, rows.Length);
        tool.ReleaseResources();
    }

    [Fact]
    public void 描述子匹配_模板未创建或过小明确失败_训练参数变化后重新加载()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject reference = TexturedImage();
        DescriptorMatchTool tool = SmallTool();
        using (var empty = new FlowContext())
        {
            empty.SetVariable(Variable.Object("Input", "Image", new HalconImage(reference), 1));
            NodeResult missing = tool.Run(empty);
            Assert.False(missing.IsSuccess);
            Assert.Contains("模板未创建", missing.Message);
        }
        Assert.Throws<InvalidOperationException>(() => tool.CreateTemplate(reference, 10, 10, 20, 20));

        tool.CreateTemplate(reference, 120, 120, 280, 280);
        int Loads()
        {
            using var ctx = new FlowContext();
            ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(reference), 1));
            Assert.True(tool.Run(ctx).IsSuccess);
            return ctx.Log.Count(l => l.Contains("模型加载耗时"));
        }
        Assert.Equal(1, Loads());
        Assert.Equal(0, Loads());
        tool.Seed = 7;
        Assert.Equal(1, Loads());
        tool.ReleaseResources();
    }

    [Fact]
    public void 饼干盒示例_与HALCON示例各面姿态判断逐图一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string imageRoot = Environment.GetEnvironmentVariable("HALCONIMAGES") ?? string.Empty;
        if (!File.Exists(Path.Combine(imageRoot, "packaging", "cookie_box_reference_01.png")))
        {
            // 依赖 HALCON 安装附带的示例图像（版权原因不随仓库分发）；未安装示例图像时不执行
            return;
        }
        SequenceNode root = FlowSerializer.Load(File.ReadAllText(
            RepoPaths.Find(Path.Combine("examples", "cookie-box.vflow.json"))));
        // 示教：按预填的 ROI，从各面的参考图创建模板（首次训练较慢，之后命中本机模型缓存）
        DescriptorMatchTool[] tools = root.Children.OfType<ToolNode>().Select(n => n.Tool).OfType<DescriptorMatchTool>().ToArray();
        Assert.Equal(4, tools.Length);
        for (int m = 0; m < tools.Length; m++)
        {
            HOperatorSet.ReadImage(out HObject reference, $"packaging/cookie_box_reference_{m + 1:00}");
            using var referenceOwner = reference;
            tools[m].CreateTemplate(reference, tools[m].TemplateRow1, tools[m].TemplateColumn1,
                tools[m].TemplateRow2, tools[m].TemplateColumn2);
        }
        // 期望值：对 locate_cookie_box_multiple_models.hdev（标定描述子 + Pose[5] 判定）的逐算子移植
        string[] expected =
        {
            "未找到,侧放,未找到,侧放", "未找到,未找到,正放,未找到", "未找到,倒放,倒放,未找到", "未找到,未找到,侧放,倒放",
            "未找到,未找到,侧放,未找到", "未找到,未找到,侧放,未找到", "未找到,倒放,倒放,未找到", "正放,未找到,正放,未找到",
            "未找到,未找到,倒放,未找到", "未找到,正放,未找到,未找到", "正放,正放,正放,未找到"
        };
        try
        {
            for (int i = 0; i < expected.Length; i++)
            {
                HOperatorSet.ReadImage(out HObject image, $"packaging/cookie_box_{i + 11:00}");
                using var imageOwner = image;
                using var ctx = new FlowContext();
                ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));

                FlowRunResult run = new FlowEngine().Run(root, ctx);

                Assert.True(run.IsSuccess, run.Message);
                string actual = string.Join(",", Enumerable.Range(1, 4).Select(f => (string)ctx.GetVariable("流程输出1", $"Face{f}").Value));
                Assert.True(expected[i] == actual, $"cookie_box_{i + 11:00}：期望 {expected[i]}，实际 {actual}");
            }
        }
        finally
        {
            VisionFlow.Runtime.FlowResources.Release(root);
        }
    }
}
