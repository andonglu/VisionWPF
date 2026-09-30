using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// VF-06 回归：流程文件版本、稳定工具标识与兼容性警告。
/// </summary>
public class FlowSerializerCompatTests
{
    /// <summary>测试用工具：提供 .ctor(string moduleName)，含可序列化参数。</summary>
    public sealed class ParamTool : VisionFlow.Core.ToolBase
    {
        public int MatchCount { get; set; } = 1;
        public double BaseScore { get; set; }
        public string? ArrayPath { get; set; }

        public ParamTool(string moduleName) : base(moduleName) { }
        public override VisionFlow.Core.NodeResult Run(VisionFlow.Core.FlowContext ctx) => VisionFlow.Core.NodeResult.Ok;
    }

    public sealed class RenamedMeasurementV2 : ToolBase
    {
        public double PixelScale { get; set; }
        public RenamedMeasurementV2(string moduleName) : base(moduleName) { }
        public override NodeResult Run(FlowContext ctx) => NodeResult.Ok;
    }

    public sealed class IdentityOwnerTool : ToolBase
    {
        public IdentityOwnerTool(string moduleName) : base(moduleName) { }
        public override NodeResult Run(FlowContext ctx) => NodeResult.Ok;
    }

    public sealed class IdentityContenderTool : ToolBase
    {
        public IdentityContenderTool(string moduleName) : base(moduleName) { }
        public override NodeResult Run(FlowContext ctx) => NodeResult.Ok;
    }

    [ToolboxTool("测试", "插件", Id = "tests.plugin.identity")]
    public sealed class PluginIdentityTool : ToolBase
    {
        public PluginIdentityTool(string moduleName) : base(moduleName) { }
        public override NodeResult Run(FlowContext ctx) => NodeResult.Ok;
    }

    private const string LegacyTypeName = "Legacy.Vision.OldMeasurementTool";
    private const string LegacyAssemblyQualifiedName =
        LegacyTypeName + ", Legacy.Vision.Algorithms, Version=2.3.0.0, Culture=neutral, PublicKeyToken=null";
    private const string MeasurementId = "tests.measurement-contract";

    private static SequenceNode SampleFlow()
    {
        var root = new SequenceNode("主流程");
        root.Children.Add(new ToolNode(new ParamTool("匹配1") { MatchCount = 3, BaseScore = 0.9, ArrayPath = "x" }));
        var loop = ForLoopNode.Each("遍历", "匹配1.Items");
        loop.Body.Add(new ToolNode(new ParamTool("记录")));
        root.Children.Add(loop);
        var ifElse = new IfElseNode("判定", new ComparisonCondition
        {
            Left = Operand.Ref("匹配1.MatchCount"),
            Right = Operand.Const(0),
            Operator = ComparisonOperator.Greater
        });
        ifElse.Outputs.Add(new BranchOutputDef
        {
            Name = "Ok",
            Kind = VariableKind.Single,
            Type = VariableType.Bool,
            IfValue = Operand.Const(true),
            ElseValue = Operand.Const(false)
        });
        root.Children.Add(ifElse);
        return root;
    }

    [Fact]
    public void 保存的文件携带格式版本与稳定工具标识()
    {
        string json = FlowSerializer.Save(SampleFlow());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(FlowSerializer.CurrentFormatVersion, doc.RootElement.GetProperty("FormatVersion").GetInt32());

        JsonElement tool = doc.RootElement.GetProperty("Children")[0].GetProperty("Tool");
        Assert.Equal(typeof(ParamTool).FullName, tool.GetProperty("ToolId").GetString());
        Assert.Equal(typeof(ParamTool).AssemblyQualifiedName, tool.GetProperty("TypeName").GetString());
    }

    [Fact]
    public void 保存再加载_结构与参数保持一致()
    {
        SequenceNode loaded = FlowSerializer.Load(FlowSerializer.Save(SampleFlow()));

        Assert.Equal(3, loaded.Children.Count);
        var match = Assert.IsType<ToolNode>(loaded.Children[0]);
        var matchTool = Assert.IsType<ParamTool>(match.Tool);
        Assert.Equal("匹配1", matchTool.ModuleName);
        Assert.Equal(3, matchTool.MatchCount);
        Assert.Equal(0.9, matchTool.BaseScore);

        var loop = Assert.IsType<ForLoopNode>(loaded.Children[1]);
        Assert.Equal(ForLoopMode.Each, loop.Mode);
        Assert.Equal("匹配1.Items", loop.ItemsPath);

        var ifElse = Assert.IsType<IfElseNode>(loaded.Children[2]);
        Assert.Single(ifElse.Outputs);
        Assert.Equal("Ok", ifElse.Outputs[0].Name);
    }

    [Fact]
    public void 旧版文件_无版本号与ToolId_仍可读取()
    {
        string json = FlowSerializer.Save(SampleFlow());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node.AsObject().Remove("FormatVersion");
        foreach (var child in node["Children"]!.AsArray())
        {
            child!["Tool"]?.AsObject().Remove("ToolId");
        }
        SequenceNode loaded = FlowSerializer.Load(node.ToJsonString());
        Assert.Equal(3, loaded.Children.Count);
        Assert.IsType<ParamTool>(Assert.IsType<ToolNode>(loaded.Children[0]).Tool);
    }

    [Fact]
    public void 高于当前支持的版本_明确失败()
    {
        string json = FlowSerializer.Save(SampleFlow());
        json = json.Replace("\"FormatVersion\": 1", "\"FormatVersion\": 999");
        var ex = Assert.Throws<NotSupportedException>(() => FlowSerializer.Load(json));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void 单节点保存_携带版本且拒绝高版本()
    {
        var node = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new ParamTool("节点"))))!;
        Assert.Equal(FlowSerializer.CurrentFormatVersion, node["FormatVersion"]!.GetValue<int>());
        Assert.IsType<ToolNode>(FlowSerializer.LoadNode(node.ToJsonString()));
        node["FormatVersion"] = FlowSerializer.CurrentFormatVersion + 1;
        Assert.Throws<NotSupportedException>(() => FlowSerializer.LoadNode(node.ToJsonString()));
    }

    [Theory]
    [InlineData(null, LegacyTypeName)]
    [InlineData(null, LegacyAssemblyQualifiedName)]
    [InlineData(MeasurementId, LegacyAssemblyQualifiedName)]
    [InlineData(LegacyTypeName, null)]
    [InlineData(LegacyAssemblyQualifiedName, null)]
    [InlineData("legacy.measurement.alias", null)]
    [InlineData("unrecognized.old.id", LegacyAssemblyQualifiedName)]
    public void 真实新版类型_通过旧类型与旧程序集身份加载(string? toolId, string? typeName)
    {
        FlowSerializer.RegisterToolType(typeof(RenamedMeasurementV2), MeasurementId,
            LegacyTypeName, LegacyAssemblyQualifiedName, "legacy.measurement.alias");
        Assert.NotEqual(LegacyTypeName, typeof(RenamedMeasurementV2).FullName);
        Assert.Null(Type.GetType(LegacyAssemblyQualifiedName));
        string json = JsonSerializer.Serialize(new
        {
            Kind = "Sequence",
            Name = "历史流程",
            Children = new[]
            {
                new
                {
                    Kind = "Tool",
                    Tool = new { ToolId = toolId, TypeName = typeName, ModuleName = "旧测量", Properties = new { PixelScale = 0.25 } }
                }
            }
        });

        SequenceNode loaded = FlowSerializer.Load(json);
        var tool = Assert.IsType<RenamedMeasurementV2>(Assert.IsType<ToolNode>(loaded.Children[0]).Tool);
        Assert.Equal("旧测量", tool.ModuleName);
        Assert.Equal(0.25, tool.PixelScale);
        var saved = JsonNode.Parse(FlowSerializer.Save(loaded))!["Children"]![0]!["Tool"]!;
        Assert.Equal(MeasurementId, saved["ToolId"]!.GetValue<string>());
        Assert.Equal(typeof(RenamedMeasurementV2).AssemblyQualifiedName, saved["TypeName"]!.GetValue<string>());
    }

    [Fact]
    public void 显式稳定ID与别名冲突_拒绝且不部分登记()
    {
        FlowSerializer.RegisterToolType(typeof(IdentityOwnerTool), "tests.identity-owner", "tests.historical-alias");
        var idError = Assert.Throws<InvalidOperationException>(() =>
            FlowSerializer.RegisterToolType(typeof(IdentityContenderTool), "TESTS.IDENTITY-OWNER"));
        Assert.Contains("冲突", idError.Message);
        Assert.Contains(nameof(IdentityOwnerTool), idError.Message);
        Assert.Contains(nameof(IdentityContenderTool), idError.Message);

        var aliasError = Assert.Throws<InvalidOperationException>(() =>
            FlowSerializer.RegisterToolType(typeof(IdentityContenderTool), "tests.identity-contender", "tests.historical-alias"));
        Assert.Contains("tests.historical-alias", aliasError.Message);
        Assert.Equal(typeof(IdentityContenderTool).FullName,
            SavedToolId(new IdentityContenderTool("未登记")));
        Assert.Throws<InvalidOperationException>(() =>
            FlowSerializer.RegisterToolType(typeof(IdentityOwnerTool), "tests.changed-owner"));
        Assert.Equal("tests.identity-owner", SavedToolId(new IdentityOwnerTool("已有登记")));
    }

    [Fact]
    public void 旧注册入口和程序集扫描_不覆盖显式稳定ID与别名()
    {
        FlowSerializer.RegisterToolType(typeof(IdentityOwnerTool), "tests.identity-owner", "tests.historical-alias");
        FlowSerializer.RegisterToolType(typeof(IdentityOwnerTool));
        var json = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new IdentityOwnerTool("工具"))))!;
        json["Tool"]!["ToolId"] = "tests.historical-alias";
        json["Tool"]!.AsObject().Remove("TypeName");
        Assert.IsType<IdentityOwnerTool>(Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json.ToJsonString())).Tool);
        Assert.Equal("tests.identity-owner", SavedToolId(new IdentityOwnerTool("工具")));
    }

    [Fact]
    public void 插件属性ID_继续作为持久化契约()
    {
        Assert.Equal("tests.plugin.identity", SavedToolId(new PluginIdentityTool("插件")));
        var json = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new PluginIdentityTool("插件"))))!;
        json["Tool"]!.AsObject().Remove("TypeName");
        Assert.IsType<PluginIdentityTool>(Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json.ToJsonString())).Tool);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Retired.Vision.LoadImageTool, Retired.Vision.Tools, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")]
    public void 仅加载工具程序集_首次按固定ID加载不依赖构造工具或编辑器(string? oldTypeName)
    {
        var context = CreateIsolatedContext("headless-tool-identities");
        try
        {
            Assembly core = context.LoadFromAssemblyPath(typeof(FlowSerializer).Assembly.Location);
            context.LoadFromAssemblyPath(typeof(LoadImageTool).Assembly.Location);
            Type serializer = core.GetType(typeof(FlowSerializer).FullName!, throwOnError: true)!;
            string json = JsonSerializer.Serialize(new
            {
                Kind = "Tool",
                Tool = new { ToolId = "loadimage", TypeName = oldTypeName, ModuleName = "首次无头加载" }
            });
            object node = serializer.GetMethod("LoadNode", new[] { typeof(string), typeof(bool) })!
                .Invoke(null, new object[] { json, false })!;
            string saved = (string)serializer.GetMethod("SaveNode")!.Invoke(null, new[] { node })!;
            Assert.Equal("loadimage", JsonNode.Parse(saved)!["Tool"]!["ToolId"]!.GetValue<string>());
            Assert.DoesNotContain(context.Assemblies, a => a.GetName().Name == "VisionFlow.EditorCore");
        }
        finally
        {
            context.Unload();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 自动扫描身份冲突_明确拒绝覆盖显式ID或别名(bool conflictWithAlias)
    {
        var context = CreateIsolatedContext("conflicting-tool-identities");
        try
        {
            Assembly core = context.LoadFromAssemblyPath(typeof(FlowSerializer).Assembly.Location);
            Assembly tests = context.LoadFromAssemblyPath(typeof(IdentityOwnerTool).Assembly.Location);
            Type owner = tests.GetType(typeof(IdentityOwnerTool).FullName!, throwOnError: true)!;
            Type serializer = core.GetType(typeof(FlowSerializer).FullName!, throwOnError: true)!;
            string stableId = conflictWithAlias ? "tests.identity-owner" : "tests.plugin.identity";
            string[] aliases = conflictWithAlias ? new[] { "tests.plugin.identity" } : Array.Empty<string>();
            serializer.GetMethod("RegisterToolType", new[] { typeof(Type), typeof(string), typeof(string[]) })!
                .Invoke(null, new object[] { owner, stableId, aliases });

            var ex = Assert.Throws<TargetInvocationException>(() =>
                serializer.GetMethod("LoadNode", new[] { typeof(string), typeof(bool) })!.Invoke(null,
                    new object[] { """{"Kind":"Tool","Tool":{"ToolId":"tests.plugin.identity","ModuleName":"冲突"}}""", false }));
            string error = ex.GetBaseException().Message;
            Assert.Contains("冲突", error);
            Assert.Contains("tests.plugin.identity", error);
            Assert.Contains(nameof(IdentityOwnerTool), error);
            Assert.Contains(nameof(PluginIdentityTool), error);

            object tool = Activator.CreateInstance(owner, "显式登记")!;
            object node = Activator.CreateInstance(core.GetType(typeof(ToolNode).FullName!)!, tool, "显式登记")!;
            string saved = (string)serializer.GetMethod("SaveNode")!.Invoke(null, new[] { node })!;
            Assert.Equal(stableId, JsonNode.Parse(saved)!["Tool"]!["ToolId"]!.GetValue<string>());
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void 内置视觉工具_无需编辑器启动就保存独立固定ID()
    {
        Type[] types = typeof(LoadImageTool).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ToolBase).IsAssignableFrom(t)
                && t.GetConstructor(new[] { typeof(string) }) != null)
            .ToArray();
        Assert.NotEmpty(types);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Type type in types)
        {
            var tool = (ToolBase)Activator.CreateInstance(type, "无头工具")!;
            string id = SavedToolId(tool);
            Assert.NotEqual(type.FullName, id);
            Assert.True(ids.Add(id), $"内置工具 ID 重复：{id}");
            FlowSerializer.RegisterToolType(type);
            Assert.Equal(id, SavedToolId(tool));
        }
        Assert.Equal("match", SavedToolId(new HalconModelMatchTool("定位")));
        Assert.Equal("loadimage", SavedToolId(new LoadImageTool("输入")));
    }

    [Fact]
    public void 内置固定ID_与全部视觉工具箱条目一致()
    {
        ToolboxRegistry.RegisterDefaults();
        foreach (ToolboxItem item in ToolboxRegistry.Items)
        {
            if (item.Factory() is ToolNode node && node.Tool.GetType().Assembly == typeof(LoadImageTool).Assembly)
            {
                Assert.Equal(item.Id, SavedToolId(node.Tool));
            }
        }
    }

    [Theory]
    [InlineData("VisionFlow.Tools.LoadImageTool", false)]
    [InlineData("VisionFlow.Tools.LoadImageTool", true)]
    [InlineData("VisionFlow.Tools.LoadImageTool, VisionFlow.Tools", false)]
    [InlineData("VisionFlow.Tools.LoadImageTool, VisionFlow.Tools, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", false)]
    public void 内置工具的旧FullName与程序集限定名_仍可加载(string identity, bool asToolId)
    {
        var node = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new LoadImageTool("旧输入"))))!;
        node.AsObject().Remove("FormatVersion");
        node["Tool"]!.AsObject().Remove(asToolId ? "TypeName" : "ToolId");
        node["Tool"]![asToolId ? "ToolId" : "TypeName"] = identity;
        var loaded = Assert.IsType<ToolNode>(FlowSerializer.LoadNode(node.ToJsonString()));
        Assert.IsType<LoadImageTool>(loaded.Tool);
        Assert.Equal("loadimage", SavedToolId(loaded.Tool));
    }

    [Theory]
    [InlineData(typeof(EllipseFollowMeasureTool))]
    [InlineData(typeof(LineFollowMeasureTool))]
    [InlineData(typeof(RectangleFollowMeasureTool))]
    [InlineData(typeof(CircleFollowMeasureTool))]
    [InlineData(typeof(OneDCaliperFollowMeasureTool))]
    [InlineData(typeof(ArcCaliperFollowMeasureTool))]
    public void 旧测量回退权限_警告忽略且不回灌工具配置(Type type)
    {
        var tool = (ToolBase)Activator.CreateInstance(type, "历史测量")!;
        var node = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(tool)))!;
        node["Tool"]!["Properties"]!["AllowMatrixFallback"] = true;
        var warnings = new List<string>();

        var loaded = Assert.IsType<ToolNode>(FlowSerializer.LoadNode(node.ToJsonString(), false, warnings));
        Assert.Contains(warnings, w => w.Contains("AllowMatrixFallback") && w.Contains("已忽略"));
        Assert.Null(type.GetProperty("AllowMatrixFallback"));
        Assert.DoesNotContain("AllowMatrixFallback", FlowSerializer.SaveNode(loaded));
    }

    private static string SavedToolId(ToolBase tool)
    {
        using var json = JsonDocument.Parse(FlowSerializer.SaveNode(new ToolNode(tool)));
        return json.RootElement.GetProperty("Tool").GetProperty("ToolId").GetString()!;
    }

    private static AssemblyLoadContext CreateIsolatedContext(string name)
    {
        var context = new AssemblyLoadContext(name, isCollectible: true);
        context.Resolving += (loader, assemblyName) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
            return File.Exists(path) ? loader.LoadFromAssemblyPath(path) : null;
        };
        return context;
    }

    [Fact]
    public void 未知工具_明确报错且包含身份信息()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(FlowSerializer.Save(SampleFlow()))!;
        var tool = node["Children"]![0]!["Tool"]!;
        tool["TypeName"] = "No.Such.Tool, NoAssembly";
        tool["ToolId"] = "no.such.id";
        var ex = Assert.Throws<InvalidOperationException>(() => FlowSerializer.Load(node.ToJsonString()));
        Assert.Contains("no.such.id", ex.Message);
    }

    [Fact]
    public void 文件中多余的参数_产生警告而非静默丢失()
    {
        string json = FlowSerializer.Save(SampleFlow());
        // 给第一个工具塞入一个不存在的参数
        json = json.Replace("\"Properties\": {", "\"Properties\": {\n        \"RemovedParam\": 123,", StringComparison.Ordinal);
        var warnings = new List<string>();
        FlowSerializer.Load(json, warnings);
        Assert.Contains(warnings, w => w.Contains("RemovedParam"));
    }
}
