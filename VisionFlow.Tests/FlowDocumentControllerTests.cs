using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Ui;

namespace VisionFlow.Tests;

/// <summary>FlowDocumentController（VF-08 从 MainWindow 平移）的编排测试：对话框桩驱动 保存/关闭确认/路径规范化。</summary>
public class FlowDocumentControllerTests : IDisposable
{
    private readonly string _tempDir;

    public FlowDocumentControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vf-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结果
        }
    }

    private string TempPath(string fileName)
    {
        return Path.Combine(_tempDir, fileName);
    }

    private static FlowDocumentController CreateController(
        FlowEditModel model,
        Func<string, string?>? saveDialog = null,
        Func<ConfirmCloseChoice>? confirmClose = null,
        List<(string Title, string Message)>? warnings = null,
        Action<string>? onSaved = null)
    {
        var controller = new FlowDocumentController(
            model,
            currentPath => saveDialog?.Invoke(currentPath),
            () => confirmClose?.Invoke() ?? ConfirmCloseChoice.Cancel,
            (title, message) => warnings?.Add((title, message)));
        if (onSaved != null)
        {
            controller.Saved += onSaved;
        }
        return controller;
    }

    [Theory]
    [InlineData("flow.vflow.json", "flow.vflow.json")]
    [InlineData("flow.vflow", "flow.vflow.json")]
    [InlineData("flow.vflow.vflow.json", "flow.vflow.json")]
    [InlineData("FLOW.VFLOW", "FLOW.VFLOW.json")]
    [InlineData("flow.json", "flow.json")]
    [InlineData("", "")]
    public void NormalizeFlowFileName_FixesExtension(string input, string expected)
    {
        Assert.Equal(expected, FlowDocumentController.NormalizeFlowFileName(input));
    }

    [Fact]
    public void Save_FirstTime_UsesSaveDialogAndMarksSaved()
    {
        var model = new FlowEditModel();
        string savedPath = null!;
        var controller = CreateController(model,
            saveDialog: _ => TempPath("a.vflow"),
            onSaved: path => savedPath = path);

        Assert.True(controller.Save(saveAs: false));

        string expected = Path.GetFullPath(TempPath("a.vflow.json"));
        Assert.Equal(expected, controller.CurrentFlowPath);
        Assert.Equal(expected, savedPath);
        Assert.False(model.IsDirty);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void Save_DialogCancelled_KeepsDocumentUntouched()
    {
        var model = new FlowEditModel();
        model.MarkDirty();
        var warnings = new List<(string, string)>();
        var controller = CreateController(model, saveDialog: _ => null, warnings: warnings);

        Assert.False(controller.Save(saveAs: false));

        Assert.Null(controller.CurrentFlowPath);
        Assert.True(model.IsDirty);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Save_ExistingPath_SkipsDialogUnlessSaveAs()
    {
        var model = new FlowEditModel();
        int dialogCalls = 0;
        string path = TempPath("b.vflow.json");
        var controller = CreateController(model, saveDialog: _ => { dialogCalls++; return path; });

        Assert.True(controller.Save(saveAs: false));
        Assert.Equal(1, dialogCalls);

        model.MarkDirty();
        Assert.True(controller.Save(saveAs: false));
        Assert.Equal(1, dialogCalls); // 已有路径，直接保存

        Assert.True(controller.Save(saveAs: true));
        Assert.Equal(2, dialogCalls); // 另存为总是询问
    }

    [Fact]
    public void Save_WriteFailure_KeepsOriginalFileAndDirtyState()
    {
        var model = new FlowEditModel();
        string path = TempPath("c.vflow.json");
        var controller = CreateController(model, saveDialog: _ => path);
        Assert.True(controller.Save(saveAs: false));
        string originalContent = File.ReadAllText(path);

        // 构造写盘失败：目标路径指向一个已存在的目录
        model.MarkDirty();
        var warnings = new List<(string, string)>();
        var failing = CreateController(model,
            saveDialog: _ => TempPath("subdir"), warnings: warnings);
        Directory.CreateDirectory(TempPath("subdir"));

        Assert.False(failing.Save(saveAs: true));

        Assert.Single(warnings);
        Assert.True(model.IsDirty); // 文档状态未变
        Assert.Equal(originalContent, File.ReadAllText(path)); // 原文件未受影响
    }

    [Fact]
    public void ConfirmClose_NotDirty_PassesWithoutAsking()
    {
        var model = new FlowEditModel();
        int asked = 0;
        var controller = CreateController(model, confirmClose: () => { asked++; return ConfirmCloseChoice.Cancel; });

        Assert.True(controller.ConfirmClose());
        Assert.Equal(0, asked);
    }

    [Fact]
    public void ConfirmClose_DiscardOrCancel_FollowsUserChoice()
    {
        var model = new FlowEditModel();
        model.MarkDirty();
        var discarding = CreateController(model, confirmClose: () => ConfirmCloseChoice.Discard);
        Assert.True(discarding.ConfirmClose());

        var cancelling = CreateController(model, confirmClose: () => ConfirmCloseChoice.Cancel);
        Assert.False(cancelling.ConfirmClose());
    }

    [Fact]
    public void ConfirmClose_SaveChoice_SavesThenPasses()
    {
        var model = new FlowEditModel();
        model.MarkDirty();
        var controller = CreateController(model,
            saveDialog: _ => TempPath("d.vflow.json"),
            confirmClose: () => ConfirmCloseChoice.Save);

        Assert.True(controller.ConfirmClose());

        Assert.False(model.IsDirty);
        Assert.True(File.Exists(TempPath("d.vflow.json")));
    }

    [Fact]
    public void ConfirmClose_SaveChoiceButSaveFails_BlocksClose()
    {
        var model = new FlowEditModel();
        model.MarkDirty();
        // 用户选择保存，但在保存对话框中点了取消 → 保存未完成，关闭被中止
        var controller = CreateController(model,
            saveDialog: _ => null,
            confirmClose: () => ConfirmCloseChoice.Save);

        Assert.False(controller.ConfirmClose());
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void New_ResetsDocumentAndPath()
    {
        var model = new FlowEditModel();
        var controller = CreateController(model, saveDialog: _ => TempPath("e.vflow.json"));
        Assert.True(controller.Save(saveAs: false));
        Assert.NotNull(controller.CurrentFlowPath);

        controller.New();

        Assert.Null(controller.CurrentFlowPath);
        Assert.False(model.IsDirty);
        Assert.Equal("主流程", model.Root.Name);
        Assert.Empty(model.Root.Children);
    }

    [Fact]
    public void Load_RoundTripsSavedFlow()
    {
        var model = new FlowEditModel();
        model.Root.Children.Add(new VisionFlow.Nodes.ToolNode(
            new VisionFlow.Tools.ThresholdTool("分割1")));
        var controller = CreateController(model, saveDialog: _ => TempPath("f.vflow.json"));
        Assert.True(controller.Save(saveAs: false));

        var model2 = new FlowEditModel();
        var controller2 = CreateController(model2);
        FlowLoadResult load = controller2.Load(TempPath("f.vflow.json"));

        Assert.NotNull(load.Validation);
        Assert.Equal(Path.GetFullPath(TempPath("f.vflow.json")), controller2.CurrentFlowPath);
        VisionFlow.Nodes.ToolNode toolNode = Assert.IsType<VisionFlow.Nodes.ToolNode>(Assert.Single(model2.Root.Children));
        Assert.Equal("分割1", toolNode.Tool.ModuleName);
    }
}
