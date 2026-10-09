using VisionFlow.Core;
using VisionFlow.Tools;

namespace VisionFlow.Tests;

/// <summary>
/// 图像资源管理回归：LoadImageTool 支持环境变量路径与仓库相对路径。
/// </summary>
public class LoadImageToolPathTests
{
    [Fact]
    public void ResolveFilePath_仓库相对路径_解析到实际文件()
    {
        string resolved = LoadImageTool.ResolveFilePath("src/Image/razors1.png");
        Assert.True(File.Exists(resolved), "相对路径未解析到实际文件：" + resolved);
        Assert.EndsWith(Path.Combine("src", "Image", "razors1.png"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolveExistingFilePath_环境变量路径_解析成功()
    {
        string imagePath = RepoPaths.Find("src/Image/razors1.png");
        string variableName = "VF_TEST_IMAGE_PATH";
        string? oldValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, imagePath);
            bool ok = LoadImageTool.TryResolveExistingFilePath($"%{variableName}%", out string resolved, out string error);
            Assert.True(ok, error);
            Assert.Equal(Path.GetFullPath(imagePath), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, oldValue);
        }
    }

    [Fact]
    public void ToStoredFilePath_Halcon示例路径_自动转成环境变量表达式()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "VisionFlow.LoadImageToolPathTests", Guid.NewGuid().ToString("N"));
        string imagePath = Path.Combine(tempRoot, "color", "pizza_01.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, Array.Empty<byte>());

        string? oldValue = Environment.GetEnvironmentVariable("HALCONIMAGES");
        try
        {
            Environment.SetEnvironmentVariable("HALCONIMAGES", tempRoot);
            string stored = LoadImageTool.ToStoredFilePath(imagePath);
            Assert.Equal($"%HALCONIMAGES%{Path.DirectorySeparatorChar}color{Path.DirectorySeparatorChar}pizza_01.png", stored);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALCONIMAGES", oldValue);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ToStoredFilePath_非Halcon路径_保持原值()
    {
        string imagePath = RepoPaths.Find("src/Image/razors1.png");
        string stored = LoadImageTool.ToStoredFilePath(imagePath);
        Assert.Equal(imagePath, stored);
    }

    [Fact]
    public void Run_环境变量路径_能够读取图像()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string imagePath = RepoPaths.Find("src/Image/razors1.png");
        string variableName = "VF_TEST_IMAGE_PATH";
        string? oldValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, imagePath);
            using var ctx = new FlowContext();
            var tool = new LoadImageTool("加载1") { FilePath = $"%{variableName}%" };
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, result.Message);
            Assert.NotNull(ctx.GetVariable("加载1", "Image"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, oldValue);
        }
    }

    [Fact]
    public void Run_相对路径_能够读取图像()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using var ctx = new FlowContext();
        var tool = new LoadImageTool("加载1") { FilePath = "src/Image/razors1.png" };
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.NotNull(ctx.GetVariable("加载1", "Image"));
    }
}
