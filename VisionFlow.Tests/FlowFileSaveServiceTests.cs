using System.Text.Json;
using VisionFlow.Editing;
using VisionFlow.Runtime;
using VisionFlow.Ui;

namespace VisionFlow.Tests;

public sealed class FlowFileSaveServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "SaveRegression-" + Guid.NewGuid().ToString("N"));

    public FlowFileSaveServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Save_ReplacesExistingFile_WithoutLeavingStagingFiles()
    {
        string path = Path.Combine(_directory, "flow.vflow.json");
        File.WriteAllText(path, "original");

        Assert.True(FlowFileSaveService.TrySave(path, () => "new content", out string error), error);

        Assert.Null(error);
        Assert.Equal("new content", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Save_CreatesNewFile()
    {
        string path = Path.Combine(_directory, "flow.vflow.json");
        Assert.True(FlowFileSaveService.TrySave(path, () => "content", out string error), error);
        Assert.Equal("content", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void EmptySerializationResult_DoesNotOverwriteOriginal()
    {
        string path = Path.Combine(_directory, "flow.vflow.json");
        File.WriteAllText(path, "original");

        Assert.False(FlowFileSaveService.TrySave(path, () => string.Empty, out string error));

        Assert.Contains("为空", error);
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void SerializationFailure_KeepsOriginalFileAndDirtyDocument()
    {
        string path = Path.Combine(_directory, "flow.vflow.json");
        File.WriteAllText(path, "original");
        var model = new FlowEditModel();
        model.MarkDirty();
        var originalRoot = model.Root;

        bool saved = FlowFileSaveService.TrySave(path, () => throw new JsonException("不能序列化"), out string error);
        if (saved)
            model.MarkSaved();

        Assert.False(saved);
        Assert.Contains("不能序列化", error);
        Assert.True(model.IsDirty);
        Assert.Same(originalRoot, model.Root);
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void LockedDestinationFailure_KeepsOriginalAndCleansStagingFile()
    {
        string path = Path.Combine(_directory, "flow.vflow.json");
        File.WriteAllText(path, "original");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(FlowFileSaveService.TrySave(path, () => "replacement", out string error));
            Assert.NotEmpty(error);
        }
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ReadOnlyDestinationFailure_KeepsOriginalFile()
    {
        string path = Path.Combine(_directory, "flow.vflow.json");
        File.WriteAllText(path, "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(FlowFileSaveService.TrySave(path, () => "replacement", out string error));
            Assert.NotEmpty(error);
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(_directory));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void MissingDirectoryFailure_ReturnsErrorWithoutCreatingDocument()
    {
        string path = Path.Combine(_directory, "missing", "flow.vflow.json");
        Assert.False(FlowFileSaveService.TrySave(path, () => "replacement", out string error));
        Assert.NotEmpty(error);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void UnsupportedSerializationFailure_IsContained()
    {
        string path = Path.Combine(_directory, "flow.vflow.json");
        Assert.False(FlowFileSaveService.TrySave(path, () => throw new NotSupportedException("不支持该节点"), out string error));
        Assert.Contains("不支持该节点", error);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ActualFlowSerializationFailure_DoesNotOverwriteFile()
    {
        string path = Path.Combine(_directory, "flow.vflow.json");
        File.WriteAllText(path, "original");
        var model = new FlowEditModel();
        model.Root.Children.Add(new VisionFlow.Nodes.ToolNode(new NonFiniteTool("参数")));

        Assert.False(FlowFileSaveService.TrySave(path, () => FlowSerializer.Save(model.Root), out string error));
        Assert.NotEmpty(error);
        Assert.Equal("original", File.ReadAllText(path));
    }

    private sealed class NonFiniteTool : VisionFlow.Core.ToolBase
    {
        public double Value { get; set; } = double.NaN;
        public NonFiniteTool(string moduleName) : base(moduleName) { }
        public override VisionFlow.Core.NodeResult Run(VisionFlow.Core.FlowContext ctx) => VisionFlow.Core.NodeResult.Ok;
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
