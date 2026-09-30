using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Validation;

namespace VisionFlow.Tests;

/// <summary>
/// VF-01/VF-06 回归：已发布的示例流程文件仍可加载并通过声明级校验。
/// </summary>
public class ExampleFlowTests
{
    private static string ExamplesDir
    {
        get
        {
            // 测试输出目录向上回溯到仓库根
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VisionFlow.slnx")))
            {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "未找到仓库根目录");
            return Path.Combine(dir!.FullName, "examples");
        }
    }

    [Theory]
    [InlineData("threshold-region.vflow.json")]
    [InlineData("xld-line.vflow.json")]
    public void 示例流程_加载并通过校验(string fileName)
    {
        string path = Path.Combine(ExamplesDir, fileName);
        Assert.True(File.Exists(path), $"示例文件不存在：{path}");

        SequenceNode root = FlowSerializer.Load(File.ReadAllText(path));
        FlowValidationResult result = FlowValidator.Validate(root);
        Assert.True(result.IsValid,
            $"{fileName} 校验失败：" + string.Join("；", result.Issues.Select(i => i.ToString())));
    }
}
