using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// 深度学习统一推理框架第一阶段：
/// 非 HALCON 用例只验证登记与配置；HALCON 用例使用官方 detect_pills.hdl 做一次真实推理，
/// 只断言公共元数据输出，不绑定后续分类/检测/分割专用结构。
/// </summary>
public class DeepLearningInferenceToolTests
{
    private const string Module = "深度学习推理1";

    [Fact]
    public void 深度学习推理_工具箱已登记()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "dl-infer");
        Assert.Equal("06 识别工具", item.Category);
        Assert.Equal("深度学习推理", item.DisplayName);
    }

    [Fact]
    public void 深度学习推理_配置校验覆盖缺失模型与非法批大小()
    {
        var tool = new DeepLearningInferenceTool(Module)
        {
            ModelFilePath = "missing_model.txt",
            BatchSize = 0
        };

        List<ToolConfigurationIssue> issues = tool.CheckConfiguration().ToList();
        Assert.Contains(issues, i => i.Parameter == nameof(DeepLearningInferenceTool.ModelFilePath) && i.Message.Contains(".hdl"));
        Assert.Contains(issues, i => i.Parameter == nameof(DeepLearningInferenceTool.BatchSize));
    }

    [Fact]
    public void 深度学习推理_持久化身份已登记()
    {
        string json = FlowSerializer.SaveNode(new ToolNode(new DeepLearningInferenceTool(Module)
        {
            ModelFilePath = @"C:\model.hdl"
        }));

        Assert.Contains("\"ToolId\": \"dl-infer\"", json);
        DeepLearningInferenceTool loaded = Assert.IsType<DeepLearningInferenceTool>(Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool);
        Assert.Equal(@"C:\model.hdl", loaded.ModelFilePath);
    }

    [Fact]
    public void 深度学习推理_官方检测模型可完成模型预热并输出公共信息()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string modelPath = @"C:\Users\Public\Documents\MVTec\HALCON-22.11-Steady\examples\hdevelop\Deep-Learning\Detection\detect_pills.hdl";
        if (!File.Exists(modelPath))
        {
            return;
        }

        using var ctx = new FlowContext();
        var tool = new DeepLearningInferenceTool(Module)
        {
            ModelFilePath = modelPath,
            ModelKind = DeepLearningModelKind.Detection,
            Device = DeepLearningDevicePreference.Auto,
            BatchSize = 1,
            OptimizeForInference = true
        };

        NodeResult run = tool.Run(ctx);

        Assert.True(run.IsSuccess, run.Message);
        Assert.Equal("detection", Assert.IsType<string>(ctx.GetVariable(Module, "ModelType").Value));
        Assert.True((bool)ctx.GetVariable(Module, "Ready").Value);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(ctx.GetVariable(Module, "DeviceUsed").Value)));
        Assert.True((int)ctx.GetVariable(Module, "ClassCount").Value > 0);
        string summary = Assert.IsType<string>(ctx.GetVariable(Module, "ModelSummary").Value);
        Assert.False(string.IsNullOrWhiteSpace(summary));
    }
}
