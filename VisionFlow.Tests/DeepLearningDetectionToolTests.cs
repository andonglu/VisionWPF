using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// DL-02 深度学习检测（dl-detect）：
/// 非 HALCON 用例验证工具箱登记、图标 key、路由分支、序列化往返、配置校验措辞与参数默认值；
/// HALCON 门禁用例用官方 detect_pills.hdl + pill_bag_001.png 做真实推理（§14 探测结论的实测断言），
/// 并对照测试内直接 HALCON 调用验证结果一致。
/// </summary>
public class DeepLearningDetectionToolTests
{
    private const string Module = "深度学习检测1";

    // ======================= 路径 helper =======================

    private static string DetectPillsModelPath()
    {
        string? examples = Environment.GetEnvironmentVariable("HALCONEXAMPLES");
        Assert.False(string.IsNullOrWhiteSpace(examples), "缺少环境变量 HALCONEXAMPLES，HALCON 门禁用例不计通过");
        string path = Path.Combine(examples!, "hdevelop", "Deep-Learning", "Detection", "detect_pills.hdl");
        Assert.True(File.Exists(path), "缺少官方示例模型：" + path);
        return path;
    }

    private static string ClassifyPillDefectsModelPath()
    {
        string? examples = Environment.GetEnvironmentVariable("HALCONEXAMPLES");
        Assert.False(string.IsNullOrWhiteSpace(examples), "缺少环境变量 HALCONEXAMPLES，HALCON 门禁用例不计通过");
        string path = Path.Combine(examples!, "hdevelop", "Deep-Learning", "Classification", "classify_pill_defects.hdl");
        Assert.True(File.Exists(path), "缺少官方示例模型：" + path);
        return path;
    }

    private static string PillBagImagePath()
    {
        string? images = Environment.GetEnvironmentVariable("HALCONIMAGES");
        Assert.False(string.IsNullOrWhiteSpace(images), "缺少环境变量 HALCONIMAGES，HALCON 门禁用例不计通过");
        string path = Path.Combine(images!, "pill_bag", "pill_bag_001.png");
        Assert.True(File.Exists(path), "缺少官方示例图像：" + path);
        return path;
    }

    private static DlDetectTool CreateTool(string regionPath = null)
    {
        return new DlDetectTool(Module)
        {
            ModelFilePath = DetectPillsModelPath(),
            ImagePath = "图像1.Image",
            RegionPath = regionPath,
            Device = DeepLearningDevicePreference.Cpu,
            BatchSize = 1,
            FailWhenNotFound = false
        };
    }

    private static FlowContext ContextWith(HObject image, HObject roi = null)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        if (roi != null)
        {
            ctx.SetVariable(Variable.Object("ROI1", "Region", new HalconRegion(roi), 1));
        }
        return ctx;
    }

    private static DlDetectBox[] BoxesOf(FlowContext ctx)
    {
        return ctx.GetVariable(Module, "Boxes").GetValue<object[]>()
            .OfType<DlDetectBox>().ToArray();
    }

    // ======================= 非 HALCON：登记 / 图标 / 路由 =======================

    [Fact]
    public void 深度学习检测_工具箱已登记()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "dl-detect");
        Assert.Equal("06 识别工具", item.Category);
        Assert.Equal("深度学习检测", item.DisplayName);
    }

    [Fact]
    public void 深度学习检测_图标key存在_编辑器文件存在_路由到专用窗口()
    {
        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        Assert.Contains("x:Key=\"ToolIcon.dl-detect\"", icons);

        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int previewStart = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        Assert.DoesNotContain("tool is DlDetectTool", router.Substring(previewStart));
        string branch = router.Substring(0, previewStart);
        Assert.Contains("new WpfDlDetectToolEditWindow(dlDetectTool, context)", branch);

        string xaml = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", "WpfDlDetectToolEditWindow.xaml"));
        string code = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", "WpfDlDetectToolEditWindow.xaml.cs"));
        Assert.True(File.Exists(xaml), xaml);
        Assert.True(File.Exists(code), code);
        string codeText = File.ReadAllText(code);
        Assert.Contains("partial class WpfDlDetectToolEditWindow : Window", codeText);
        Assert.Contains("ToolEditContext context", codeText);
    }

    [Fact]
    public void 深度学习检测_持久化身份已登记_参数序列化往返()
    {
        string json = FlowSerializer.SaveNode(new ToolNode(new DlDetectTool(Module)
        {
            ModelFilePath = @"C:\model.hdl",
            MinScore = 0.7,
            MaxDetections = 3,
            ModelCacheMode = ModelCacheMode.Flow,
            FailWhenNotFound = false
        }));

        Assert.Contains("\"ToolId\": \"dl-detect\"", json);
        DlDetectTool loaded = Assert.IsType<DlDetectTool>(Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool);
        Assert.Equal(@"C:\model.hdl", loaded.ModelFilePath);
        Assert.Equal(0.7, loaded.MinScore);
        Assert.Equal(3, loaded.MaxDetections);
        Assert.Equal(ModelCacheMode.Flow, loaded.ModelCacheMode);
        Assert.False(loaded.FailWhenNotFound);
    }

    [Fact]
    public void 深度学习检测_配置校验措辞覆盖模型路径与数值范围()
    {
        var empty = new DlDetectTool(Module);
        List<ToolConfigurationIssue> issues = empty.CheckConfiguration().ToList();
        Assert.Contains(issues, i => i.Parameter == nameof(DlDetectTool.ModelFilePath) && i.Message.Contains("请先指定"));

        var badExt = new DlDetectTool(Module) { ModelFilePath = "model.txt" };
        Assert.Contains(badExt.CheckConfiguration(), i => i.Parameter == nameof(DlDetectTool.ModelFilePath) && i.Message.Contains(".hdl"));

        var missing = new DlDetectTool(Module) { ModelFilePath = "no_such_model.hdl" };
        Assert.Contains(missing.CheckConfiguration(), i => i.Parameter == nameof(DlDetectTool.ModelFilePath) && i.Message.Contains("不存在"));

        var badBatch = new DlDetectTool(Module) { ModelFilePath = "no_such_model.hdl", BatchSize = 0 };
        Assert.Contains(badBatch.CheckConfiguration(), i => i.Parameter == nameof(DlDetectTool.BatchSize));

        var badMin = new DlDetectTool(Module) { ModelFilePath = "no_such_model.hdl", MinScore = 1.5 };
        Assert.Contains(badMin.CheckConfiguration(), i => i.Parameter == nameof(DlDetectTool.MinScore) && i.Message.Contains("0~1"));

        var badMax = new DlDetectTool(Module) { ModelFilePath = "no_such_model.hdl", MaxDetections = -1 };
        Assert.Contains(badMax.CheckConfiguration(), i => i.Parameter == nameof(DlDetectTool.MaxDetections));
    }

    [Fact]
    public void 深度学习检测_默认值保持兼容()
    {
        var tool = new DlDetectTool(Module);
        Assert.Equal("Input.Image", tool.ImagePath);
        Assert.Equal(0.5, tool.MinScore);
        Assert.Equal(0, tool.MaxDetections);
        Assert.Equal(ModelCacheMode.Instance, tool.ModelCacheMode);
        Assert.True(tool.FailWhenNotFound);
        Assert.True(tool.OptimizeForInference);
        Assert.Equal(1, tool.BatchSize);
    }

    // ======================= HALCON 门禁：真实模型推理 =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习检测_官方检测模型真实推理_输出结构完整()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillBagImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlDetectTool tool = CreateTool();
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.True(run.IsSuccess, run.Message);

                Assert.True((bool)ctx.GetVariable(Module, "Found").Value);
                int count = (int)ctx.GetVariable(Module, "Count").Value;
                Assert.True(count >= 1, "官方示例图像应至少检出一个目标");

                // 分数降序（§14.2：结果已按置信度降序）
                double[] scores = ctx.GetVariable(Module, "Scores").GetValue<double[]>();
                Assert.Equal(count, scores.Length);
                for (int i = 1; i < scores.Length; i++)
                {
                    Assert.True(scores[i] <= scores[i - 1], "Scores 应按置信度降序");
                }

                // 类别名非空、Best* 与首个一致
                string[] names = ctx.GetVariable(Module, "ClassNames").GetValue<string[]>();
                Assert.Equal(count, names.Length);
                Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
                Assert.Equal(scores[0], (double)ctx.GetVariable(Module, "BestScore").Value, 6);
                Assert.Equal(names[0], (string)ctx.GetVariable(Module, "BestClassName").Value);

                // 框坐标在图像范围内（含 ROI 偏移为 0 的原图坐标系）
                HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
                DlDetectBox[] boxes = BoxesOf(ctx);
                Assert.Equal(count, boxes.Length);
                Assert.All(boxes, b =>
                {
                    Assert.InRange(b.Row1, -1, height.I + 1);
                    Assert.InRange(b.Row2, -1, height.I + 1);
                    Assert.InRange(b.Col1, -1, width.I + 1);
                    Assert.InRange(b.Col2, -1, width.I + 1);
                    Assert.True(b.Row2 > b.Row1 && b.Col2 > b.Col1);
                    Assert.False(double.IsNaN(b.Score));
                });

                // 区域 / 轮廓与检出数量一一对应
                HOperatorSet.CountObj(((HalconRegion)ctx.GetVariable(Module, "Regions").Value).Object, out HTuple regionCount);
                HOperatorSet.CountObj(((HalconXld)ctx.GetVariable(Module, "Contours").Value).Object, out HTuple contourCount);
                Assert.Equal(count, regionCount.I);
                Assert.Equal(count, contourCount.I);

                // 公共输出（§5.3）
                Assert.True((bool)ctx.GetVariable(Module, "Ready").Value);
                Assert.Equal("detection", (string)ctx.GetVariable(Module, "ModelType").Value);
                Assert.False(string.IsNullOrWhiteSpace((string)ctx.GetVariable(Module, "ResolvedModelPath").Value));
                Assert.Equal("cpu", (string)ctx.GetVariable(Module, "DeviceUsed").Value);
                Assert.Equal(10, (int)ctx.GetVariable(Module, "ClassCount").Value);
                Assert.Equal(512, (int)ctx.GetVariable(Module, "ImageWidth").Value);
                Assert.Equal(320, (int)ctx.GetVariable(Module, "ImageHeight").Value);
                Assert.True((bool)ctx.GetVariable(Module, "OptimizedForInference").Value);
                Assert.False(string.IsNullOrWhiteSpace((string)ctx.GetVariable(Module, "ModelSummary").Value));
                var info = Assert.IsType<DeepLearningInferenceInfo>(ctx.GetVariable(Module, "Info").Value);
                Assert.Equal(10, info.ClassNames.Length);
            }
            finally
            {
                tool.ReleaseResources();
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习检测_与直接HALCON调用结果一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        // 参考路径：测试内直接按 §14.1 配方调用 HALCON（与工具内部相同步骤），再与工具输出对照。
        HOperatorSet.ReadImage(out HObject image, PillBagImagePath());
        using (image)
        {
            HOperatorSet.ReadDlModel(DetectPillsModelPath(), out HTuple model);
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
                HOperatorSet.GetImageSize(image, out HTuple imgW, out HTuple imgH);

                HOperatorSet.ConvertImageType(image, out HObject real, "real");
                HOperatorSet.ScaleImage(real, out HObject scaled, (rangeMax[0].D - rangeMin[0].D) / 255.0, rangeMin[0].D);
                HOperatorSet.ZoomImageSize(scaled, out HObject net, netW.I, netH.I, "constant");
                HOperatorSet.CreateDict(out HTuple sample);
                HOperatorSet.SetDictObject(net, sample, "image");
                HOperatorSet.ApplyDlModel(model, sample, new HTuple(), out HTuple reference);

                HOperatorSet.GetDictTuple(reference, "bbox_row1", out HTuple refR1);
                HOperatorSet.GetDictTuple(reference, "bbox_col1", out HTuple refC1);
                HOperatorSet.GetDictTuple(reference, "bbox_row2", out HTuple refR2);
                HOperatorSet.GetDictTuple(reference, "bbox_col2", out HTuple refC2);
                HOperatorSet.GetDictTuple(reference, "bbox_class_name", out HTuple refNames);
                HOperatorSet.GetDictTuple(reference, "bbox_confidence", out HTuple refScores);

                double scaleRow = imgH.I / (double)netH.I;
                double scaleCol = imgW.I / (double)netW.I;
                double minScore = 0.5;
                var refFiltered = new List<(string Name, double Score, double R1, double C1, double R2, double C2)>();
                for (int i = 0; i < refScores.Length; i++)
                {
                    if (refScores[i].D >= minScore)
                    {
                        refFiltered.Add((refNames[i].S, refScores[i].D,
                            refR1[i].D * scaleRow, refC1[i].D * scaleCol, refR2[i].D * scaleRow, refC2[i].D * scaleCol));
                    }
                }

                using (var ctx = ContextWith(image))
                {
                    DlDetectTool tool = CreateTool();
                    try
                    {
                        NodeResult run = tool.Run(ctx);
                        Assert.True(run.IsSuccess, run.Message);
                        DlDetectBox[] boxes = BoxesOf(ctx);
                        Assert.Equal(refFiltered.Count, boxes.Length);
                        for (int i = 0; i < boxes.Length; i++)
                        {
                            Assert.Equal(refFiltered[i].Name, boxes[i].ClassName);
                            Assert.Equal(refFiltered[i].Score, boxes[i].Score, 6);
                            Assert.Equal(refFiltered[i].R1, boxes[i].Row1, 3);
                            Assert.Equal(refFiltered[i].C1, boxes[i].Col1, 3);
                            Assert.Equal(refFiltered[i].R2, boxes[i].Row2, 3);
                            Assert.Equal(refFiltered[i].C2, boxes[i].Col2, 3);
                        }
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
    public void 深度学习检测_MinScore过滤生效()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillBagImagePath());
        using (image)
        {
            int CountAt(double minScore)
            {
                using var ctx = ContextWith(image);
                DlDetectTool tool = CreateTool();
                tool.MinScore = minScore;
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    return (int)ctx.GetVariable(Module, "Count").Value;
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }

            int atDefault = CountAt(0.5);
            int atHigh = CountAt(0.99);
            Assert.True(atDefault >= 1);
            Assert.True(atHigh < atDefault, $"MinScore=0.99 应比 0.5 过滤更多（{atHigh} < {atDefault}）");
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习检测_MaxDetections截断()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillBagImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlDetectTool tool = CreateTool();
            tool.MaxDetections = 1;
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.True(run.IsSuccess, run.Message);
                Assert.Equal(1, (int)ctx.GetVariable(Module, "Count").Value);
                double[] scores = ctx.GetVariable(Module, "Scores").GetValue<double[]>();
                Assert.Single(scores);
                Assert.Equal(scores[0], (double)ctx.GetVariable(Module, "BestScore").Value, 6);
            }
            finally
            {
                tool.ReleaseResources();
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习检测_模型类型不匹配报中文错误()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillBagImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlDetectTool tool = CreateTool();
            tool.ModelFilePath = ClassifyPillDefectsModelPath();
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.False(run.IsSuccess);
                Assert.Contains("类型不匹配", run.Message);
                Assert.Contains("detection", run.Message);
            }
            finally
            {
                tool.ReleaseResources();
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习检测_ROI裁剪后坐标映射回原图()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillBagImagePath());
        using (image)
        {
            // 固定 ROI（实测）：圈住整图最佳目标 Cognivia（原图坐标约 r 257~377 c 689~811）。
            HOperatorSet.GenRectangle1(out HObject roi, 150.0, 550.0, 500.0, 950.0);
            using (roi)
            using (var ctx = ContextWith(image, roi))
            {
                DlDetectTool tool = CreateTool(regionPath: "ROI1.Region");
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    DlDetectBox[] boxes = BoxesOf(ctx);
                    Assert.True(boxes.Length >= 1, "ROI 内应至少检出一个目标");

                    // 所有框坐标已加裁剪原点偏移（150, 550），应落回 ROI 范围内（容差 2 px）。
                    Assert.All(boxes, b =>
                    {
                        Assert.InRange(b.Row1, 148, 502);
                        Assert.InRange(b.Row2, 148, 502);
                        Assert.InRange(b.Col1, 548, 952);
                        Assert.InRange(b.Col2, 548, 952);
                    });

                    // 实测：ROI 推理下 Cognivia 映射回原图的中心约 (320.6, 710.6)，
                    // 与整图 Cognivia 中心（约 (317, 750)）同属一个目标。
                    DlDetectBox cognivia = Assert.Single(boxes, b => b.ClassName == "Cognivia");
                    double centerRow = (cognivia.Row1 + cognivia.Row2) / 2;
                    double centerCol = (cognivia.Col1 + cognivia.Col2) / 2;
                    Assert.True(Math.Abs(centerRow - 320.6) < 1.5, $"中心行 {centerRow:F1} 应在实测值 320.6 附近");
                    Assert.True(Math.Abs(centerCol - 710.6) < 1.5, $"中心列 {centerCol:F1} 应在实测值 710.6 附近");
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }
        }
    }
}
