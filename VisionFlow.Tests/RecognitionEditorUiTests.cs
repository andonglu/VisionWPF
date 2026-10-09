using VisionFlow.Editing;
using VisionFlow.Tools;
using Xunit;

namespace VisionFlow.Tests;

/// <summary>
/// 识别线（RC-01~RC-04）专用编辑窗口与图标：图标 key、路由分支、编辑器文件登记。
/// 只读源码与资源文件断言，不调用 HALCON 算子（Requires 未标注，纳入非 HALCON 门禁）。
/// </summary>
public class RecognitionEditorUiTests
{
    public static IEnumerable<object[]> RecognitionToolIds()
    {
        yield return new object[] { "barcode1d", "WpfBarcodeToolEditWindow", "barcodeTool" };
        yield return new object[] { "ocr", "WpfOcrToolEditWindow", "ocrTool" };
        yield return new object[] { "color-classify", "WpfColorClassifyToolEditWindow", "colorClassifyTool" };
        yield return new object[] { "color-segment", "WpfColorSegmentToolEditWindow", "colorSegmentTool" };
    }

    [Theory]
    [MemberData(nameof(RecognitionToolIds))]
    public void 识别工具_图标key存在_编辑器文件存在_路由到专用窗口(string id, string windowType, string routerVariable)
    {
        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        Assert.Contains($"x:Key=\"ToolIcon.{id}\"", icons);

        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int previewStart = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        string preview = router.Substring(previewStart);
        Assert.DoesNotContain($"tool is {windowType}", preview);
        string branch = router.Substring(0, previewStart);
        Assert.Contains($"new {windowType}({routerVariable}, context)", branch);

        string xaml = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", windowType + ".xaml"));
        string code = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", windowType + ".xaml.cs"));
        Assert.True(File.Exists(xaml), xaml);
        Assert.True(File.Exists(code), code);
        string codeText = File.ReadAllText(code);
        Assert.Contains($"partial class {windowType} : Window", codeText);
        // 构造约定：(具体工具类型, ToolEditContext) 两参
        Assert.Contains($"public {windowType}(", codeText);
        Assert.Contains("ToolEditContext context", codeText);
    }

    [Fact]
    public void 识别工具_不会落入视觉预览回退()
    {
        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int previewStart = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        string preview = router.Substring(previewStart);
        foreach (string type in new[] { "Barcode1DTool", "OcrTool", "ColorClassifyTool", "ColorSegmentTool" })
        {
            Assert.DoesNotContain($"tool is {type}", preview);
        }
    }

    [Fact]
    public void 识别工具_工具箱登记_图标key按工具ID命名()
    {
        ToolboxRegistry.RegisterDefaults();
        foreach (string id in new[] { "barcode1d", "ocr", "color-classify", "color-segment" })
        {
            ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == id);
            Assert.Equal("06 识别工具", item.Category);
        }
        // 路由专用窗口的工具不需要加入 IsVisualPreviewTool 回退名单，此处只核对图标 key 与工具 ID 一致
        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        foreach (string id in new[] { "barcode1d", "ocr", "color-classify", "color-segment" })
        {
            Assert.Contains($"x:Key=\"ToolIcon.{id}\"", icons);
        }
    }
}
