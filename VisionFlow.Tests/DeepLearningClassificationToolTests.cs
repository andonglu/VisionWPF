using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// DL-01 深度学习分类（dl-classify）：
/// 非 HALCON 用例验证工具箱登记、图标 key、路由分支、序列化往返、配置校验措辞与参数默认值；
/// HALCON 门禁用例用官方 classify_pill_defects.hdl + pill_ginseng_contamination_001.png 做真实推理
/// （§14 探测结论的实测断言），并对照测试内直接 HALCON 调用验证结果一致。
/// 实测记录（2026-10-09，HALCON 22.11）：该模型 3 个类别（contamination/crack/good，ID 0/1/2），
/// 网络输入 300×300 三通道，image_range [-127,128]；示例图像置信度降序为 good≈0.6063 / crack≈0.3700 /
/// contamination≈0.0237，MinScore=0.99 时三个候选全部被过滤（Found=false）。
/// </summary>
public class DeepLearningClassificationToolTests
{
    private const string Module = "深度学习分类1";

    // ======================= 路径 helper =======================

    private static string ClassifyPillDefectsModelPath()
    {
        string? examples = Environment.GetEnvironmentVariable("HALCONEXAMPLES");
        Assert.False(string.IsNullOrWhiteSpace(examples), "缺少环境变量 HALCONEXAMPLES，HALCON 门禁用例不计通过");
        string path = Path.Combine(examples!, "hdevelop", "Deep-Learning", "Classification", "classify_pill_defects.hdl");
        Assert.True(File.Exists(path), "缺少官方示例模型：" + path);
        return path;
    }

    private static string DetectPillsModelPath()
    {
        string? examples = Environment.GetEnvironmentVariable("HALCONEXAMPLES");
        Assert.False(string.IsNullOrWhiteSpace(examples), "缺少环境变量 HALCONEXAMPLES，HALCON 门禁用例不计通过");
        string path = Path.Combine(examples!, "hdevelop", "Deep-Learning", "Detection", "detect_pills.hdl");
        Assert.True(File.Exists(path), "缺少官方示例模型：" + path);
        return path;
    }

    private static string PillGinsengImagePath()
    {
        string? images = Environment.GetEnvironmentVariable("HALCONIMAGES");
        Assert.False(string.IsNullOrWhiteSpace(images), "缺少环境变量 HALCONIMAGES，HALCON 门禁用例不计通过");
        string path = Path.Combine(images!, "pill", "ginseng", "contamination", "pill_ginseng_contamination_001.png");
        Assert.True(File.Exists(path), "缺少官方示例图像：" + path);
        return path;
    }

    private static DlClassifyTool CreateTool()
    {
        return new DlClassifyTool(Module)
        {
            ModelFilePath = ClassifyPillDefectsModelPath(),
            ImagePath = "图像1.Image",
            Device = DeepLearningDevicePreference.Cpu,
            BatchSize = 1,
            FailWhenNotFound = false
        };
    }

    private static FlowContext ContextWith(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        return ctx;
    }

    // ======================= 非 HALCON：登记 / 图标 / 路由 =======================

    [Fact]
    public void 深度学习分类_工具箱已登记()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "dl-classify");
        Assert.Equal("06 识别工具", item.Category);
        Assert.Equal("深度学习分类", item.DisplayName);
    }

    [Fact]
    public void 深度学习分类_图标key存在_编辑器文件存在_路由到专用窗口()
    {
        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        Assert.Contains("x:Key=\"ToolIcon.dl-classify\"", icons);

        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int previewStart = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        Assert.DoesNotContain("tool is DlClassifyTool", router.Substring(previewStart));
        string branch = router.Substring(0, previewStart);
        Assert.Contains("new WpfDlClassifyToolEditWindow(dlClassifyTool, context)", branch);

        string xaml = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", "WpfDlClassifyToolEditWindow.xaml"));
        string code = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", "WpfDlClassifyToolEditWindow.xaml.cs"));
        Assert.True(File.Exists(xaml), xaml);
        Assert.True(File.Exists(code), code);
        string codeText = File.ReadAllText(code);
        Assert.Contains("partial class WpfDlClassifyToolEditWindow : Window", codeText);
        Assert.Contains("ToolEditContext context", codeText);
    }

    [Fact]
    public void 深度学习分类_持久化身份已登记_参数序列化往返()
    {
        string json = FlowSerializer.SaveNode(new ToolNode(new DlClassifyTool(Module)
        {
            ModelFilePath = @"C:\model.hdl",
            MinScore = 0.7,
            TopK = 3,
            ExpectedClass = "good",
            ModelCacheMode = ModelCacheMode.Flow,
            FailWhenNotFound = false
        }));

        Assert.Contains("\"ToolId\": \"dl-classify\"", json);
        DlClassifyTool loaded = Assert.IsType<DlClassifyTool>(Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool);
        Assert.Equal(@"C:\model.hdl", loaded.ModelFilePath);
        Assert.Equal(0.7, loaded.MinScore);
        Assert.Equal(3, loaded.TopK);
        Assert.Equal("good", loaded.ExpectedClass);
        Assert.Equal(ModelCacheMode.Flow, loaded.ModelCacheMode);
        Assert.False(loaded.FailWhenNotFound);
    }

    [Fact]
    public void 深度学习分类_配置校验措辞覆盖模型路径与数值范围()
    {
        var empty = new DlClassifyTool(Module);
        List<ToolConfigurationIssue> issues = empty.CheckConfiguration().ToList();
        Assert.Contains(issues, i => i.Parameter == nameof(DlClassifyTool.ModelFilePath) && i.Message.Contains("请先指定"));

        var badExt = new DlClassifyTool(Module) { ModelFilePath = "model.txt" };
        Assert.Contains(badExt.CheckConfiguration(), i => i.Parameter == nameof(DlClassifyTool.ModelFilePath) && i.Message.Contains(".hdl"));

        var missing = new DlClassifyTool(Module) { ModelFilePath = "no_such_model.hdl" };
        Assert.Contains(missing.CheckConfiguration(), i => i.Parameter == nameof(DlClassifyTool.ModelFilePath) && i.Message.Contains("不存在"));

        var badBatch = new DlClassifyTool(Module) { ModelFilePath = "no_such_model.hdl", BatchSize = 0 };
        Assert.Contains(badBatch.CheckConfiguration(), i => i.Parameter == nameof(DlClassifyTool.BatchSize));

        var badMin = new DlClassifyTool(Module) { ModelFilePath = "no_such_model.hdl", MinScore = 1.5 };
        Assert.Contains(badMin.CheckConfiguration(), i => i.Parameter == nameof(DlClassifyTool.MinScore) && i.Message.Contains("0~1"));

        var badTopK = new DlClassifyTool(Module) { ModelFilePath = "no_such_model.hdl", TopK = 0 };
        Assert.Contains(badTopK.CheckConfiguration(), i => i.Parameter == nameof(DlClassifyTool.TopK) && i.Message.Contains("TopK"));
    }

    [Fact]
    public void 深度学习分类_默认值保持兼容()
    {
        var tool = new DlClassifyTool(Module);
        Assert.Equal("Input.Image", tool.ImagePath);
        Assert.Null(tool.RegionPath);
        Assert.Equal(0.5, tool.MinScore);
        Assert.Equal(1, tool.TopK);
        Assert.True(string.IsNullOrEmpty(tool.ExpectedClass));
        Assert.Equal(ModelCacheMode.Instance, tool.ModelCacheMode);
        Assert.True(tool.FailWhenNotFound);
        Assert.True(tool.OptimizeForInference);
        Assert.Equal(1, tool.BatchSize);
    }

    // ======================= HALCON 门禁：真实模型推理 =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分类_官方分类模型真实推理_输出结构完整()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlClassifyTool tool = CreateTool();
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.True(run.IsSuccess, run.Message);

                Assert.True((bool)ctx.GetVariable(Module, "Found").Value);
                // 实测：Top1 = good（置信度约 0.606），见类注释实测记录。
                Assert.Equal("good", (string)ctx.GetVariable(Module, "ClassName").Value);
                Assert.Equal(2, (int)ctx.GetVariable(Module, "ClassId").Value);
                double score = (double)ctx.GetVariable(Module, "Score").Value;
                Assert.InRange(score, 0.6, 0.62);

                // TopK=1 默认：各 Top* 数组只有一项且与 Top1 平铺一致。
                Assert.Equal(new[] { 2 }, ctx.GetVariable(Module, "TopClassIds").GetValue<int[]>());
                Assert.Equal(new[] { "good" }, ctx.GetVariable(Module, "TopClassNames").GetValue<string[]>());
                Assert.Equal(new[] { score }, ctx.GetVariable(Module, "TopScores").GetValue<double[]>(), (a, b) => Math.Abs(a - b) < 1e-9);
                Assert.True((bool)ctx.GetVariable(Module, "MatchedExpected").Value);

                // 公共输出（§5.3）
                Assert.True((bool)ctx.GetVariable(Module, "Ready").Value);
                Assert.Equal("classification", (string)ctx.GetVariable(Module, "ModelType").Value);
                Assert.False(string.IsNullOrWhiteSpace((string)ctx.GetVariable(Module, "ResolvedModelPath").Value));
                Assert.Equal("cpu", (string)ctx.GetVariable(Module, "DeviceUsed").Value);
                Assert.Equal(3, (int)ctx.GetVariable(Module, "ClassCount").Value);
                Assert.Equal(new[] { "contamination", "crack", "good" }, ctx.GetVariable(Module, "ClassNames").GetValue<string[]>());
                Assert.Equal(new[] { 0, 1, 2 }, ctx.GetVariable(Module, "ClassIds").GetValue<int[]>());
                Assert.Equal(300, (int)ctx.GetVariable(Module, "ImageWidth").Value);
                Assert.Equal(300, (int)ctx.GetVariable(Module, "ImageHeight").Value);
                Assert.True((bool)ctx.GetVariable(Module, "OptimizedForInference").Value);
                Assert.False(string.IsNullOrWhiteSpace((string)ctx.GetVariable(Module, "ModelSummary").Value));
                var info = Assert.IsType<DeepLearningInferenceInfo>(ctx.GetVariable(Module, "Info").Value);
                Assert.Equal(3, info.ClassNames.Length);
            }
            finally
            {
                tool.ReleaseResources();
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分类_与直接HALCON调用结果一致_TopK3降序()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        // 参考路径：测试内直接按 §14.1 配方调用 HALCON（与工具内部相同步骤），再与工具输出对照。
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        {
            HOperatorSet.ReadDlModel(ClassifyPillDefectsModelPath(), out HTuple model);
            try
            {
                HOperatorSet.SetDlModelParam(model, "batch_size", 1);
                HOperatorSet.SetDlModelParam(model, "optimize_for_inference", "true");
                HOperatorSet.QueryAvailableDlDevices("runtime", "cpu", out HTuple devices);
                HOperatorSet.SetDlModelParam(model, "device", devices[0]);
                HOperatorSet.GetDlModelParam(model, "image_width", out HTuple netW);
                HOperatorSet.GetDlModelParam(model, "image_height", out HTuple netH);
                HOperatorSet.GetDlModelParam(model, "image_range_min", out HTuple rangeMin);
                HOperatorSet.GetDlModelParam(model, "image_range_max", out HTuple rangeMax);

                HOperatorSet.ConvertImageType(image, out HObject real, "real");
                HOperatorSet.ScaleImage(real, out HObject scaled, (rangeMax[0].D - rangeMin[0].D) / 255.0, rangeMin[0].D);
                HOperatorSet.ZoomImageSize(scaled, out HObject net, netW.I, netH.I, "constant");
                HOperatorSet.CreateDict(out HTuple sample);
                HOperatorSet.SetDictObject(net, sample, "image");
                HOperatorSet.ApplyDlModel(model, sample, new HTuple(), out HTuple reference);

                HOperatorSet.GetDictTuple(reference, "classification_class_ids", out HTuple refIds);
                HOperatorSet.GetDictTuple(reference, "classification_class_names", out HTuple refNames);
                HOperatorSet.GetDictTuple(reference, "classification_confidences", out HTuple refConfs);

                using (var ctx = ContextWith(image))
                {
                    DlClassifyTool tool = CreateTool();
                    tool.TopK = 3;
                    tool.MinScore = 0; // 保留全部 3 个候选（默认 0.5 会过滤掉 0.37/0.024 两项）
                    try
                    {
                        NodeResult run = tool.Run(ctx);
                        Assert.True(run.IsSuccess, run.Message);

                        // TopK=3：模型共 3 类，全部返回，且与直接 HALCON 调用逐项一致（§14.2：结果已按置信度降序）。
                        int[] ids = ctx.GetVariable(Module, "TopClassIds").GetValue<int[]>();
                        string[] names = ctx.GetVariable(Module, "TopClassNames").GetValue<string[]>();
                        double[] scores = ctx.GetVariable(Module, "TopScores").GetValue<double[]>();
                        Assert.Equal(3, ids.Length);
                        Assert.Equal(3, names.Length);
                        Assert.Equal(3, scores.Length);
                        for (int i = 0; i < 3; i++)
                        {
                            Assert.Equal(refIds[i].I, ids[i]);
                            Assert.Equal(refNames[i].S, names[i]);
                            Assert.Equal(refConfs[i].D, scores[i], 6);
                        }
                        for (int i = 1; i < scores.Length; i++)
                        {
                            Assert.True(scores[i] <= scores[i - 1], "TopScores 应按置信度降序");
                        }

                        // Top1 平铺与 TopK 首项一致。
                        Assert.Equal(ids[0], (int)ctx.GetVariable(Module, "ClassId").Value);
                        Assert.Equal(names[0], (string)ctx.GetVariable(Module, "ClassName").Value);
                        Assert.Equal(scores[0], (double)ctx.GetVariable(Module, "Score").Value, 6);
                    }
                    finally
                    {
                        tool.ReleaseResources();
                    }
                }
            }
            finally
            {
                HOperatorSet.ClearDlModel(model);
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分类_TopK超过类别数自动截断()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlClassifyTool tool = CreateTool();
            // 实测模型共 3 类（见类注释）；TopK=10 超过类别数，应自动截断且不报错（§6）。
            // MinScore=0：默认 0.5 会先过滤掉低分候选，导致截断无从发生。
            tool.TopK = 10;
            tool.MinScore = 0;
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.True(run.IsSuccess, run.Message);
                Assert.Equal(3, ctx.GetVariable(Module, "TopClassNames").GetValue<string[]>().Length);
                Assert.Equal(3, ctx.GetVariable(Module, "TopClassIds").GetValue<int[]>().Length);
                Assert.Equal(3, ctx.GetVariable(Module, "TopScores").GetValue<double[]>().Length);
                Assert.True((bool)ctx.GetVariable(Module, "Found").Value);
            }
            finally
            {
                tool.ReleaseResources();
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分类_MinScore099过滤全部候选走NotFound()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        {
            // 实测（见类注释）：示例图像三个候选置信度约 0.606 / 0.370 / 0.024，MinScore=0.99 全部低于阈值被过滤。
            using (var ctx = ContextWith(image))
            {
                DlClassifyTool tool = CreateTool();
                tool.MinScore = 0.99;
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    Assert.False((bool)ctx.GetVariable(Module, "Found").Value);
                    Assert.Empty(ctx.GetVariable(Module, "TopClassNames").GetValue<string[]>());
                    Assert.Equal(string.Empty, (string)ctx.GetVariable(Module, "ClassName").Value);
                    Assert.Equal(0.0, (double)ctx.GetVariable(Module, "Score").Value);
                    // ExpectedClass 未设置时 MatchedExpected 恒 true。
                    Assert.True((bool)ctx.GetVariable(Module, "MatchedExpected").Value);
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }

            // FailWhenNotFound=true（默认）时 0 候选应失败并走 NotFoundOutcome 措辞。
            using (var ctx = ContextWith(image))
            {
                var tool = new DlClassifyTool(Module)
                {
                    ModelFilePath = ClassifyPillDefectsModelPath(),
                    ImagePath = "图像1.Image",
                    Device = DeepLearningDevicePreference.Cpu,
                    MinScore = 0.99,
                    FailWhenNotFound = true
                };
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.False(run.IsSuccess);
                    Assert.Contains("MinScore", run.Message);
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分类_ExpectedClass比对命中与未命中()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        {
            // 命中：不区分大小写；TopK=3 时与全部候选比对（crack 是第 2 名，TopK=1 时不会命中）。
            using (var ctx = ContextWith(image))
            {
                DlClassifyTool tool = CreateTool();
                tool.TopK = 3;
                tool.MinScore = 0; // 保留第 2 名 crack（0.37 < 默认 0.5）
                tool.ExpectedClass = "CRACK";
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    Assert.True((bool)ctx.GetVariable(Module, "MatchedExpected").Value);
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }

            // 未命中：期望类别不在 TopK 候选中。
            using (var ctx = ContextWith(image))
            {
                DlClassifyTool tool = CreateTool();
                tool.ExpectedClass = "contamination";
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    Assert.False((bool)ctx.GetVariable(Module, "MatchedExpected").Value);
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }

            // 数字串按类别 ID 比对：Top1 good 的类别 ID 为 2（实测）。
            using (var ctx = ContextWith(image))
            {
                DlClassifyTool tool = CreateTool();
                tool.ExpectedClass = "2";
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    Assert.True((bool)ctx.GetVariable(Module, "MatchedExpected").Value);
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分类_模型类型不匹配报中文错误()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlClassifyTool tool = CreateTool();
            tool.ModelFilePath = DetectPillsModelPath();
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.False(run.IsSuccess);
                Assert.Contains("类型不匹配", run.Message);
                Assert.Contains("classification", run.Message);
            }
            finally
            {
                tool.ReleaseResources();
            }
        }
    }
}
