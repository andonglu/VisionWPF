using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;

namespace VisionFlow.Tests;

/// <summary>
/// 深度学习示例流程回归：示例文件内置 loadimage，直接读取本机 HALCON 示例图像与预训练模型。
/// 模型/图像文件不存在时跳过，不影响无示例数据的环境。
/// </summary>
public class DeepLearningExampleFlowTests
{
    [Fact]
    public void 药片检测示例_至少检出一个目标且最高分高于阈值()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        if (!HasExampleAssets(
                Path.Combine("pill_bag", "pill_bag_001.png"),
                Path.Combine("hdevelop", "Deep-Learning", "Detection", "detect_pills.hdl")))
        {
            return;
        }

        using FlowRunScope scope = RunExample("dl-detect-pills.vflow.json");
        Assert.True(scope.Run.IsSuccess, scope.Run.Message);
        Assert.True((int)scope.Context.GetVariable("流程输出1", "Count").Value >= 1);
        string bestClassName = Assert.IsType<string>(scope.Context.GetVariable("流程输出1", "BestClassName").Value);
        Assert.False(string.IsNullOrWhiteSpace(bestClassName));
        double bestScore = (double)scope.Context.GetVariable("流程输出1", "BestScore").Value;
        Assert.True(bestScore > 0.5, $"BestScore 应高于 0.5：{bestScore}");
    }

    [Fact]
    public void 药片缺陷分类示例_TopK为三且分数降序()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        if (!HasExampleAssets(
                Path.Combine("pill", "ginseng", "contamination", "pill_ginseng_contamination_001.png"),
                Path.Combine("hdevelop", "Deep-Learning", "Classification", "classify_pill_defects.hdl")))
        {
            return;
        }

        using FlowRunScope scope = RunExample("dl-classify-pill-defects.vflow.json");
        Assert.True(scope.Run.IsSuccess, scope.Run.Message);
        string className = Assert.IsType<string>(scope.Context.GetVariable("流程输出1", "ClassName").Value);
        Assert.False(string.IsNullOrWhiteSpace(className));
        double score = (double)scope.Context.GetVariable("流程输出1", "Score").Value;
        Assert.True(score > 0, $"Top1 分数应大于 0：{score}");
        string[] topNames = ToArray<string>(scope.Context.GetVariable("流程输出1", "TopClassNames").Value);
        double[] topScores = ToArray<double>(scope.Context.GetVariable("缺陷分类", "TopScores").Value);
        Assert.Equal(3, topNames.Length);
        for (int i = 1; i < topScores.Length; i++)
        {
            Assert.True(topScores[i - 1] >= topScores[i], $"TopScores 应降序：{topScores[i - 1]} < {topScores[i]}");
        }
    }

    [Fact]
    public void 药片缺陷分割示例_至少一个非空类且面积与类别一一对应()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        if (!HasExampleAssets(
                Path.Combine("pill", "ginseng", "contamination", "pill_ginseng_contamination_001.png"),
                Path.Combine("hdevelop", "Deep-Learning", "Segmentation", "segment_pill_defects.hdl")))
        {
            return;
        }

        using FlowRunScope scope = RunExample("dl-segment-pill-defects.vflow.json");
        Assert.True(scope.Run.IsSuccess, scope.Run.Message);
        Assert.True((int)scope.Context.GetVariable("流程输出1", "Count").Value >= 1);
        int[] areas = ToArray<int>(scope.Context.GetVariable("流程输出1", "Areas").Value);
        string[] classNames = ToArray<string>(scope.Context.GetVariable("流程输出1", "ClassNames").Value);
        Assert.Equal(areas.Length, classNames.Length);
        Assert.Contains(areas, a => a > 0);
    }

    private static bool HasExampleAssets(string imageRelativePath, string modelRelativePath)
    {
        string imageRoot = Environment.GetEnvironmentVariable("HALCONIMAGES") ?? string.Empty;
        string modelRoot = Environment.GetEnvironmentVariable("HALCONEXAMPLES") ?? string.Empty;
        return File.Exists(Path.Combine(imageRoot, imageRelativePath))
            && File.Exists(Path.Combine(modelRoot, modelRelativePath));
    }

    private static FlowRunScope RunExample(string fileName)
    {
        SequenceNode root = FlowSerializer.Load(File.ReadAllText(RepoPaths.Find(Path.Combine("examples", fileName))));
        var context = new FlowContext();
        FlowRunResult run = new FlowEngine().Run(root, context);
        return new FlowRunScope(root, context, run);
    }

    private static T[] ToArray<T>(object value)
    {
        return ((System.Collections.IEnumerable)value).Cast<object>().Select(v => (T)v).ToArray();
    }

    private sealed class FlowRunScope : IDisposable
    {
        private readonly SequenceNode _root;

        public FlowRunScope(SequenceNode root, FlowContext context, FlowRunResult run)
        {
            _root = root;
            Context = context;
            Run = run;
        }

        public FlowContext Context { get; }

        public FlowRunResult Run { get; }

        public void Dispose()
        {
            FlowResources.Release(_root);
            Context.Dispose();
        }
    }
}
