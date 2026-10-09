using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;

namespace VisionFlow.Tests;

/// <summary>
/// 识别示例流程回归：示例文件内置 loadimage，直接读取本机 HALCON 示例图像。
/// 图像不存在时跳过，不影响无示例数据的环境。
/// </summary>
public class RecognitionExampleFlowTests
{
    private static string ExampleImage(string relativePath)
    {
        string imageRoot = Environment.GetEnvironmentVariable("HALCONIMAGES") ?? string.Empty;
        return Path.Combine(imageRoot, relativePath);
    }

    [Fact]
    public void 二维码默认强度示例_三档均能输出结果()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string imagePath = ExampleImage(Path.Combine("datacode", "qrcode", "qr_workpiece_01.png"));
        if (!File.Exists(imagePath))
        {
            return;
        }

        using FlowRunScope scope = RunExample("datacode-default-settings.vflow.json");
        Assert.True(scope.Run.IsSuccess, scope.Run.Message);

        string standard = Assert.IsType<string>(scope.Context.GetVariable("流程输出1", "StandardCode").Value);
        string enhanced = Assert.IsType<string>(scope.Context.GetVariable("流程输出1", "EnhancedCode").Value);
        string maximum = Assert.IsType<string>(scope.Context.GetVariable("流程输出1", "MaximumCode").Value);
        Assert.False(string.IsNullOrWhiteSpace(standard));
        Assert.Equal(standard, enhanced);
        Assert.Equal(enhanced, maximum);
        Assert.True((int)scope.Context.GetVariable("流程输出1", "StandardCount").Value >= 1);
        Assert.True((int)scope.Context.GetVariable("流程输出1", "EnhancedCount").Value >= 1);
        Assert.True((int)scope.Context.GetVariable("流程输出1", "MaximumCount").Value >= 1);
        // GradeQuality=true：三档均输出总体质量评分 FirstGrade（0~4 或 N/A→NaN）
        foreach (string name in new[] { "StandardGrade", "EnhancedGrade", "MaximumGrade" })
        {
            double grade = (double)scope.Context.GetVariable("流程输出1", name).Value;
            Assert.True(double.IsNaN(grade) || (grade >= 0 && grade <= 4), $"{name} 数值越界：{grade}");
        }
    }

    [Fact]
    public void 药盒有效期示例_正则匹配为真()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string imagePath = ExampleImage(Path.Combine("ocr", "medication_package_02_right.png"));
        if (!File.Exists(imagePath))
        {
            return;
        }

        using FlowRunScope scope = RunExample("ocr-expiration-date.vflow.json");
        Assert.True(scope.Run.IsSuccess, scope.Run.Message);
        Assert.True((bool)scope.Context.GetVariable("流程输出1", "PatternOk").Value);
        Assert.True((int)scope.Context.GetVariable("流程输出1", "LineCount").Value >= 1);
        string text = Assert.IsType<string>(scope.Context.GetVariable("流程输出1", "Text").Value);
        Assert.Contains('/', text);
    }

    [Fact]
    public void 保险丝颜色识别示例_输出五种已知标签()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string imagePath = ExampleImage(Path.Combine("color", "color_fuses_00.png"));
        if (!File.Exists(imagePath))
        {
            return;
        }

        using FlowRunScope scope = RunExample("color-fuses-classify.vflow.json");
        Assert.True(scope.Run.IsSuccess, scope.Run.Message);
        Assert.True((bool)scope.Context.GetVariable("流程输出1", "AllKnown").Value);
        string[] labels = ToArray<string>(scope.Context.GetVariable("流程输出1", "Labels").Value);
        Assert.Equal(new[] { "Orange", "Red", "Blue", "Yellow", "Green" }, labels);
    }

    [Fact]
    public void 彩色棋子颜色分割示例_输出至少三个非空类()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string imagePath = ExampleImage(Path.Combine("color", "color_pieces_01.png"));
        if (!File.Exists(imagePath))
        {
            return;
        }

        using FlowRunScope scope = RunExample("color-pieces-mlp.vflow.json");
        Assert.True(scope.Run.IsSuccess, scope.Run.Message);
        int nonEmpty = (int)scope.Context.GetVariable("流程输出1", "NonEmptyClassCount").Value;
        int[] areas = ToArray<int>(scope.Context.GetVariable("流程输出1", "ClassAreas").Value);
        Assert.True(nonEmpty >= 3, $"非空类数量过少：{nonEmpty}");
        Assert.True(areas.Length >= 4, $"类别面积输出不足：{areas.Length}");
        Assert.True(areas[0] > 0 && areas[1] > 0 && areas[2] > 0, "前三个颜色类区域面积应大于 0");
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
