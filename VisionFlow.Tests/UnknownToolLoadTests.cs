using System.Text.Json;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;

namespace VisionFlow.Tests;

/// <summary>
/// RF-01 回归：工具类型未安装/未登记时，加载降级为占位工具而不是崩溃。
/// </summary>
public class UnknownToolLoadTests
{
    private const string FlowWithUnknownTool = """
    {
      "Kind": "Sequence",
      "Name": "主流程",
      "Children": [
        {
          "Id": "n1",
          "Kind": "Tool",
          "Name": "灰度分割",
          "Tool": {
            "ToolId": "not-installed-tool",
            "ModuleName": "灰度分割",
            "Properties": {
              "MinGray": 150,
              "MaxGray": 255,
              "Connection": true
            }
          }
        }
      ]
    }
    """;

    [Fact]
    public void 未知ToolId_加载为占位工具并给出警告()
    {
        var warnings = new List<string>();

        SequenceNode root = FlowSerializer.Load(FlowWithUnknownTool, warnings);

        var toolNode = Assert.IsType<ToolNode>(Assert.Single(root.Children));
        var unknown = Assert.IsType<UnknownTool>(toolNode.Tool);
        Assert.Equal("灰度分割", unknown.ModuleName);
        Assert.Equal("not-installed-tool", unknown.StoredToolId);
        Assert.Single(warnings);
        Assert.Contains("not-installed-tool", warnings[0]);
    }

    [Fact]
    public void 未知ToolId_保存后原ToolId与参数保留()
    {
        SequenceNode root = FlowSerializer.Load(FlowWithUnknownTool, new List<string>());

        string json = FlowSerializer.Save(root);

        using var doc = JsonDocument.Parse(json);
        JsonElement tool = doc.RootElement.GetProperty("Children")[0].GetProperty("Tool");
        Assert.Equal("not-installed-tool", tool.GetProperty("ToolId").GetString());
        Assert.Equal("灰度分割", tool.GetProperty("ModuleName").GetString());
        Assert.Equal(150, tool.GetProperty("Properties").GetProperty("MinGray").GetInt32());
        Assert.True(tool.GetProperty("Properties").GetProperty("Connection").GetBoolean());
    }

    [Fact]
    public void 未知ToolId_占位工具运行抛出明确异常()
    {
        var unknown = new UnknownTool("灰度分割", "not-installed-tool", new Dictionary<string, JsonElement>());

        var ex = Assert.Throws<InvalidOperationException>(() => unknown.Run(null));
        Assert.Contains("not-installed-tool", ex.Message);
    }

    [Fact]
    public void 已知ToolId_加载仍是真实工具类型()
    {
        ToolboxRegistry.RegisterDefaults();
        var warnings = new List<string>();

        SequenceNode root = FlowSerializer.Load(FlowWithUnknownTool.Replace("not-installed-tool", "threshold"), warnings);

        var toolNode = Assert.IsType<ToolNode>(Assert.Single(root.Children));
        Assert.IsNotType<UnknownTool>(toolNode.Tool);
    }
}
