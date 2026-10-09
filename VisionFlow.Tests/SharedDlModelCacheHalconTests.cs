using VisionFlow.Core;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;

namespace VisionFlow.Tests;

/// <summary>
/// D1 共享模型句柄池 HALCON 门禁用例（§5.4）：用官方 detect_pills.hdl 验证
/// Instance / Flow / Project 三档共享行为、mtime 变更触发换句柄、最后引用释放后池条目清除。
/// </summary>
[Trait("Requires", "HALCON")]
public class SharedDlModelCacheHalconTests
{
    private static string DetectPillsModelPath()
    {
        string? examples = Environment.GetEnvironmentVariable("HALCONEXAMPLES");
        Assert.False(string.IsNullOrWhiteSpace(examples), "缺少环境变量 HALCONEXAMPLES，HALCON 门禁用例不计通过");
        string path = Path.Combine(examples!, "hdevelop", "Deep-Learning", "Detection", "detect_pills.hdl");
        Assert.True(File.Exists(path), "缺少官方示例模型：" + path);
        return path;
    }

    private static DeepLearningInferenceTool CreateTool(string moduleName, string modelPath, ModelCacheMode mode)
    {
        return new DeepLearningInferenceTool(moduleName)
        {
            ModelFilePath = modelPath,
            ModelKind = DeepLearningModelKind.Detection,
            Device = DeepLearningDevicePreference.Auto,
            BatchSize = 1,
            OptimizeForInference = true,
            ModelCacheMode = mode
        };
    }

    [Fact]
    public void Project模式_两工具实例同模型_只加载一份_最后释放后池条目清除()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string modelPath = DetectPillsModelPath();
        int loads0 = SharedDlModelCache.DebugTotalLoadCount;
        int alive0 = SharedDlModelCache.DebugAliveEntryCount;
        var tool1 = CreateTool("共享甲", modelPath, ModelCacheMode.Project);
        var tool2 = CreateTool("共享乙", modelPath, ModelCacheMode.Project);

        using var ctx1 = new FlowContext();
        using var ctx2 = new FlowContext();
        NodeResult run1 = tool1.Run(ctx1);
        NodeResult run2 = tool2.Run(ctx2);
        Assert.True(run1.IsSuccess, run1.Message);
        Assert.True(run2.IsSuccess, run2.Message);

        Assert.Equal(1, SharedDlModelCache.DebugTotalLoadCount - loads0);
        Assert.Equal(1, SharedDlModelCache.DebugAliveEntryCount - alive0);

        tool1.ReleaseResources();
        Assert.Equal(1, SharedDlModelCache.DebugAliveEntryCount - alive0);

        tool2.ReleaseResources();
        Assert.Equal(0, SharedDlModelCache.DebugAliveEntryCount - alive0);
    }

    [Fact]
    public void Instance模式_互不共享_不进入共享池()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string modelPath = DetectPillsModelPath();
        int loads0 = SharedDlModelCache.DebugTotalLoadCount;
        int alive0 = SharedDlModelCache.DebugAliveEntryCount;
        var tool1 = CreateTool("实例甲", modelPath, ModelCacheMode.Instance);
        var tool2 = CreateTool("实例乙", modelPath, ModelCacheMode.Instance);

        using var ctx1 = new FlowContext();
        using var ctx2 = new FlowContext();
        NodeResult run1 = tool1.Run(ctx1);
        NodeResult run2 = tool2.Run(ctx2);
        Assert.True(run1.IsSuccess, run1.Message);
        Assert.True(run2.IsSuccess, run2.Message);

        Assert.Equal(0, SharedDlModelCache.DebugTotalLoadCount - loads0);
        Assert.Equal(0, SharedDlModelCache.DebugAliveEntryCount - alive0);

        tool1.ReleaseResources();
        tool2.ReleaseResources();
    }

    [Fact]
    public void Flow模式_同一根节点共享_跨根节点不共享_预热与运行锚点一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string modelPath = DetectPillsModelPath();
        int loads0 = SharedDlModelCache.DebugTotalLoadCount;
        int alive0 = SharedDlModelCache.DebugAliveEntryCount;
        var engine = new FlowEngine();

        // 同一根节点：预热（环境锚点）与运行（OwnerToken）读到同一个根实例 → 共享一份。
        var toolA = CreateTool("流程甲", modelPath, ModelCacheMode.Flow);
        var toolB = CreateTool("流程乙", modelPath, ModelCacheMode.Flow);
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(toolA));
        root.Children.Add(new ToolNode(toolB));

        Assert.True(FlowResources.Prepare(root).IsSuccess);
        Assert.Equal(1, SharedDlModelCache.DebugAliveEntryCount - alive0);
        FlowRunResult run = engine.Run(root);
        Assert.True(run.IsSuccess, run.Message);
        Assert.Equal(1, SharedDlModelCache.DebugTotalLoadCount - loads0);
        Assert.Equal(1, SharedDlModelCache.DebugAliveEntryCount - alive0);

        FlowResources.Release(root);

        // 另一个根节点实例（同一文件、同一工具参数）不构成 Flow 共享。
        var toolC = CreateTool("流程丙", modelPath, ModelCacheMode.Flow);
        var otherRoot = new SequenceNode("另一根");
        otherRoot.Children.Add(new ToolNode(toolC));
        FlowRunResult otherRun = engine.Run(otherRoot);
        Assert.True(otherRun.IsSuccess, otherRun.Message);

        Assert.Equal(2, SharedDlModelCache.DebugTotalLoadCount - loads0);
        Assert.Equal(1, SharedDlModelCache.DebugAliveEntryCount - alive0);

        FlowResources.Release(otherRoot);
        Assert.Equal(0, SharedDlModelCache.DebugAliveEntryCount - alive0);
    }

    [Fact]
    public void 文件mtime变更_键变化触发换句柄_旧条目清除()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string dir = Path.Combine(Path.GetTempPath(), "vf-dlcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, "detect_pills.hdl");
        File.Copy(DetectPillsModelPath(), copy);
        var tool = CreateTool("换句柄", copy, ModelCacheMode.Project);

        try
        {
            using var ctx = new FlowContext();
            NodeResult firstRun = tool.Run(ctx);
            Assert.True(firstRun.IsSuccess, firstRun.Message);
            DlModelPoolKey firstKey = PoolKeyFor(copy);
            Assert.True(SharedDlModelCache.TryGetEntry(firstKey, out DlModelCacheEntry firstEntry));

            // 内容不变、mtime 变化 → 池键变化 → 换入新条目，旧条目随本实例还引用即清除。
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(5));
            NodeResult secondRun = tool.Run(ctx);
            Assert.True(secondRun.IsSuccess, secondRun.Message);

            DlModelPoolKey secondKey = PoolKeyFor(copy);
            Assert.NotEqual(firstKey, secondKey);
            Assert.True(SharedDlModelCache.TryGetEntry(secondKey, out DlModelCacheEntry secondEntry));
            Assert.NotSame(firstEntry, secondEntry);
            Assert.Equal(1, SharedDlModelCache.TryGetRefCount(secondKey, out int refCount) ? refCount : 0);

            tool.ReleaseResources();
            Assert.False(SharedDlModelCache.TryGetRefCount(secondKey, out _));
        }
        finally
        {
            tool.ReleaseResources();
            Directory.Delete(dir, true);
        }
    }

    private static DlModelPoolKey PoolKeyFor(string resolvedPath)
    {
        return DlModelCachePolicy.BuildKey(
            DlModelCachePolicy.ProjectScopeToken,
            Path.GetFullPath(resolvedPath),
            File.GetLastWriteTimeUtc(resolvedPath).Ticks,
            DeepLearningModelKind.Detection,
            DeepLearningDevicePreference.Auto,
            batchSize: 1,
            optimizeForInference: true);
    }
}
