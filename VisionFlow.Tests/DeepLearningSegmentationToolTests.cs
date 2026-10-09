using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// DL-03 深度学习分割（dl-segment）：
/// 非 HALCON 用例验证工具箱登记、图标 key、路由分支、序列化往返、配置校验措辞与参数默认值；
/// HALCON 门禁用例用官方 segment_pill_defects.hdl + pill_ginseng_contamination_001.png 做真实推理
/// （§14 探测结论的实测断言），并对照测试内直接 HALCON 调用验证各类面积逐值一致。
/// 实测记录（2026-10-09，HALCON 22.11）：该模型 3 个类别；contamination 图 defect 类像素极少
/// （探测实测类 1 面积约 81/160000），MinScore 双层过滤
/// （类区域置信均值 ≥ MinScore 才保留该类；保留类只输出 confidence ≥ MinScore 的像素）后
/// Regions 每类一个区域（空类为空区域，对象数恒 = 输出类数）。
/// 另实测：crop_domain 与 crop_part 逐像素一致；CPU 同模型两番推理结果逐位一致
/// （跨句柄亦确定），因此"工具 vs 测试内直接 HALCON 调用"的逐值对照是稳定断言。
/// </summary>
public class DeepLearningSegmentationToolTests
{
    private const string Module = "深度学习分割1";

    // ======================= 路径 helper =======================

    private static string SegmentPillDefectsModelPath()
    {
        string? examples = Environment.GetEnvironmentVariable("HALCONEXAMPLES");
        Assert.False(string.IsNullOrWhiteSpace(examples), "缺少环境变量 HALCONEXAMPLES，HALCON 门禁用例不计通过");
        string path = Path.Combine(examples!, "hdevelop", "Deep-Learning", "Segmentation", "segment_pill_defects.hdl");
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

    private static DlSegmentTool CreateTool()
    {
        return new DlSegmentTool(Module)
        {
            ModelFilePath = SegmentPillDefectsModelPath(),
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
    public void 深度学习分割_工具箱已登记()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "dl-segment");
        Assert.Equal("06 识别工具", item.Category);
        Assert.Equal("深度学习分割", item.DisplayName);
    }

    [Fact]
    public void 深度学习分割_图标key存在_编辑器文件存在_路由到专用窗口()
    {
        string icons = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfApp/Themes/ToolIcons.xaml"));
        Assert.Contains("x:Key=\"ToolIcon.dl-segment\"", icons);

        string router = File.ReadAllText(RepoPaths.Find("VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs"));
        int previewStart = router.IndexOf("private static bool IsVisualPreviewTool", StringComparison.Ordinal);
        Assert.DoesNotContain("tool is DlSegmentTool", router.Substring(previewStart));
        string branch = router.Substring(0, previewStart);
        Assert.Contains("new WpfDlSegmentToolEditWindow(dlSegmentTool, context)", branch);

        string xaml = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", "WpfDlSegmentToolEditWindow.xaml"));
        string code = RepoPaths.Find(Path.Combine("VisionFlow.WpfToolEditors", "Editors", "WpfDlSegmentToolEditWindow.xaml.cs"));
        Assert.True(File.Exists(xaml), xaml);
        Assert.True(File.Exists(code), code);
        string codeText = File.ReadAllText(code);
        Assert.Contains("partial class WpfDlSegmentToolEditWindow : Window", codeText);
        Assert.Contains("ToolEditContext context", codeText);
    }

    [Fact]
    public void 深度学习分割_持久化身份已登记_参数序列化往返()
    {
        string json = FlowSerializer.SaveNode(new ToolNode(new DlSegmentTool(Module)
        {
            ModelFilePath = @"C:\model.hdl",
            MinScore = 0.7,
            ClassFilter = "pill,defect",
            ModelCacheMode = ModelCacheMode.Flow,
            FailWhenNotFound = false
        }));

        Assert.Contains("\"ToolId\": \"dl-segment\"", json);
        DlSegmentTool loaded = Assert.IsType<DlSegmentTool>(Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool);
        Assert.Equal(@"C:\model.hdl", loaded.ModelFilePath);
        Assert.Equal(0.7, loaded.MinScore);
        Assert.Equal("pill,defect", loaded.ClassFilter);
        Assert.Equal(ModelCacheMode.Flow, loaded.ModelCacheMode);
        Assert.False(loaded.FailWhenNotFound);
    }

    [Fact]
    public void 深度学习分割_配置校验措辞覆盖模型路径与数值范围()
    {
        var empty = new DlSegmentTool(Module);
        List<ToolConfigurationIssue> issues = empty.CheckConfiguration().ToList();
        Assert.Contains(issues, i => i.Parameter == nameof(DlSegmentTool.ModelFilePath) && i.Message.Contains("请先指定"));

        var badExt = new DlSegmentTool(Module) { ModelFilePath = "model.txt" };
        Assert.Contains(badExt.CheckConfiguration(), i => i.Parameter == nameof(DlSegmentTool.ModelFilePath) && i.Message.Contains(".hdl"));

        var missing = new DlSegmentTool(Module) { ModelFilePath = "no_such_model.hdl" };
        Assert.Contains(missing.CheckConfiguration(), i => i.Parameter == nameof(DlSegmentTool.ModelFilePath) && i.Message.Contains("不存在"));

        var badBatch = new DlSegmentTool(Module) { ModelFilePath = "no_such_model.hdl", BatchSize = 0 };
        Assert.Contains(badBatch.CheckConfiguration(), i => i.Parameter == nameof(DlSegmentTool.BatchSize));

        var badMin = new DlSegmentTool(Module) { ModelFilePath = "no_such_model.hdl", MinScore = 1.5 };
        Assert.Contains(badMin.CheckConfiguration(), i => i.Parameter == nameof(DlSegmentTool.MinScore) && i.Message.Contains("0~1"));

        var badFilter = new DlSegmentTool(Module) { ModelFilePath = "no_such_model.hdl", ClassFilter = ",,," };
        Assert.Contains(badFilter.CheckConfiguration(), i => i.Parameter == nameof(DlSegmentTool.ClassFilter) && i.Message.Contains("ClassFilter"));
    }

    [Fact]
    public void 深度学习分割_默认值保持兼容()
    {
        var tool = new DlSegmentTool(Module);
        Assert.Equal("Input.Image", tool.ImagePath);
        Assert.Null(tool.RegionPath);
        Assert.Equal(0.5, tool.MinScore);
        Assert.True(string.IsNullOrEmpty(tool.ClassFilter));
        Assert.Equal(ModelCacheMode.Instance, tool.ModelCacheMode);
        Assert.True(tool.FailWhenNotFound);
        Assert.True(tool.OptimizeForInference);
        Assert.Equal(1, tool.BatchSize);
    }

    // ======================= HALCON 门禁：真实模型推理 =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分割_官方分割模型真实推理_输出结构完整()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlSegmentTool tool = CreateTool();
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.True(run.IsSuccess, run.Message);

                Assert.True((bool)ctx.GetVariable(Module, "Found").Value);
                Assert.Equal(3, (int)ctx.GetVariable(Module, "ClassCount").Value);

                // Regions：每类一个区域对象，对象数 = 模型类别数（实测 3），与 ColorSegmentTool 消费一致。
                HObject regions = ((HalconRegion)ctx.GetVariable(Module, "Regions").Value).Object;
                HOperatorSet.CountObj(regions, out HTuple regionCount);
                Assert.Equal(3, regionCount.I);

                // 各类面积与 Regions 一一对应；Count = 非空类数。
                int[] areas = ctx.GetVariable(Module, "Areas").GetValue<int[]>();
                Assert.Equal(3, areas.Length);
                Assert.Equal(areas.Count(a => a > 0), (int)ctx.GetVariable(Module, "Count").Value);
                string[] classNames = ctx.GetVariable(Module, "ClassNames").GetValue<string[]>();
                int[] classIds = ctx.GetVariable(Module, "ClassIds").GetValue<int[]>();
                Assert.Equal(3, classNames.Length);
                Assert.Equal(3, classIds.Length);

                // MaskImage：原图尺寸 byte 图（像素 = 类别 ID）。
                HObject mask = ((HalconImage)ctx.GetVariable(Module, "MaskImage").Value).Object;
                HOperatorSet.GetImageSize(image, out HTuple imgW, out HTuple imgH);
                HOperatorSet.GetImageSize(mask, out HTuple maskW, out HTuple maskH);
                Assert.Equal(imgW.I, maskW.I);
                Assert.Equal(imgH.I, maskH.I);
                HOperatorSet.GetImageType(mask, out HTuple maskType);
                Assert.Equal("byte", maskType.S);

                // RejectedRegion：与 ColorSegmentTool 语义一致——整图定义域 − union1(类区域)，两者交集必为空。
                HObject rejected = ((HalconRegion)ctx.GetVariable(Module, "RejectedRegion").Value).Object;
                HOperatorSet.GetDomain(image, out HObject domain);
                HObject union = null;
                HObject overlap = null;
                try
                {
                    HOperatorSet.Union1(regions, out union);
                    HOperatorSet.Intersection(rejected, union, out overlap);
                    HOperatorSet.AreaCenter(overlap, out HTuple overlapArea, out HTuple _, out HTuple _);
                    Assert.Equal(0, overlapArea.Length > 0 ? overlapArea[0].I : 0);
                }
                finally
                {
                    domain?.Dispose();
                    union?.Dispose();
                    overlap?.Dispose();
                }

                // 公共输出（§5.3）
                Assert.True((bool)ctx.GetVariable(Module, "Ready").Value);
                Assert.Equal("segmentation", (string)ctx.GetVariable(Module, "ModelType").Value);
                Assert.False(string.IsNullOrWhiteSpace((string)ctx.GetVariable(Module, "ResolvedModelPath").Value));
                Assert.Equal("cpu", (string)ctx.GetVariable(Module, "DeviceUsed").Value);
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
    public void 深度学习分割_与直接HALCON调用结果一致_各类面积逐值一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        // 参考路径：测试内直接按 §14.1 配方调用 HALCON（与工具内部相同步骤），
        // 再按类注释 MinScore 语义（类区域置信均值 ≥ MinScore 保留该类；保留类 = 类区域 ∩ confidence ≥ MinScore）
        // 计算各类期望面积，与工具输出 Areas 逐值对照。
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        {
            HOperatorSet.GetImageSize(image, out HTuple imgW, out HTuple imgH);
            HOperatorSet.ReadDlModel(SegmentPillDefectsModelPath(), out HTuple model);
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
                HOperatorSet.GetDlModelParam(model, "class_names", out HTuple classNamesTuple);
                HOperatorSet.GetDlModelParam(model, "class_ids", out HTuple classIdsTuple);

                HOperatorSet.ConvertImageType(image, out HObject real, "real");
                HOperatorSet.ScaleImage(real, out HObject scaled, (rangeMax[0].D - rangeMin[0].D) / 255.0, rangeMin[0].D);
                HOperatorSet.ZoomImageSize(scaled, out HObject net, netW.I, netH.I, "constant");
                HOperatorSet.CreateDict(out HTuple sample);
                HOperatorSet.SetDictObject(net, sample, "image");
                HOperatorSet.ApplyDlModel(model, sample, new HTuple(), out HTuple reference);

                // §14.2：两键都是图像对象，用 get_dict_object 取。
                HOperatorSet.GetDictObject(out HObject segNet, reference, "segmentation_image");
                HOperatorSet.GetDictObject(out HObject confNet, reference, "segmentation_confidence");
                HOperatorSet.ZoomImageSize(segNet, out HObject segCrop, imgW.I, imgH.I, "nearest_neighbor");
                HOperatorSet.ZoomImageSize(confNet, out HObject confCrop, imgW.I, imgH.I, "bilinear");
                HOperatorSet.ConvertImageType(segCrop, out HObject segByte, "byte");

                const double minScore = 0.5; // 与 CreateTool 默认值一致
                HOperatorSet.Threshold(confCrop, out HObject confPass, minScore, 999999.0);

                // 逐类期望输出区域（未缩放回原图前的裁剪图坐标）。
                HOperatorSet.GenEmptyObj(out HObject expectedRegions);
                var expectedNames = new List<string>();
                for (int k = 0; k < classNamesTuple.Length; k++)
                {
                    int id = classIdsTuple[k].I;
                    HOperatorSet.Threshold(segByte, out HObject classRegion, id, id);
                    HOperatorSet.AreaCenter(classRegion, out HTuple classArea, out HTuple _, out HTuple _);
                    bool kept = classArea.Length > 0 && classArea[0].I > 0;
                    if (kept)
                    {
                        HOperatorSet.Intensity(classRegion, confCrop, out HTuple mean, out HTuple _);
                        kept = mean.Length > 0 && mean[0].D >= minScore;
                    }
                    HObject outRegion;
                    if (kept)
                    {
                        HOperatorSet.Intersection(classRegion, confPass, out outRegion);
                    }
                    else
                    {
                        HOperatorSet.GenEmptyRegion(out outRegion);
                    }
                    classRegion.Dispose();
                    HOperatorSet.ConcatObj(expectedRegions, outRegion, out HObject combined);
                    expectedRegions.Dispose();
                    expectedRegions = combined;
                    outRegion.Dispose();
                    expectedNames.Add(classNamesTuple[k].S);
                }

                // 与工具相同的语义：类区域取自已缩放回原图比例的 segCrop（无 ROI 时即原图坐标），
                // 不再做二次缩放。
                HOperatorSet.AreaCenter(expectedRegions, out HTuple expectedAreas, out HTuple _, out HTuple _);
                Assert.Equal(3, expectedAreas.Length);

                using (var ctx = ContextWith(image))
                {
                    DlSegmentTool tool = CreateTool();
                    try
                    {
                        NodeResult run = tool.Run(ctx);
                        Assert.True(run.IsSuccess, run.Message);

                        // 类别名与面积逐值一致。
                        Assert.Equal(expectedNames.ToArray(), ctx.GetVariable(Module, "ClassNames").GetValue<string[]>());
                        int[] areas = ctx.GetVariable(Module, "Areas").GetValue<int[]>();
                        Assert.Equal(expectedAreas.Length, areas.Length);
                        for (int i = 0; i < areas.Length; i++)
                        {
                            Assert.Equal(expectedAreas[i].I, areas[i]);
                        }

                        // MaskImage 与 Areas 一致：对类别 ID ≠ 0 的类，threshold(MaskImage, id, id) 面积 = 该类面积。
                        HObject mask = ((HalconImage)ctx.GetVariable(Module, "MaskImage").Value).Object;
                        int[] ids = ctx.GetVariable(Module, "ClassIds").GetValue<int[]>();
                        for (int i = 0; i < ids.Length; i++)
                        {
                            if (ids[i] == 0 || areas[i] == 0)
                            {
                                continue;
                            }
                            HOperatorSet.Threshold(mask, out HObject maskClassRegion, ids[i], ids[i]);
                            HOperatorSet.AreaCenter(maskClassRegion, out HTuple maskArea, out HTuple _, out HTuple _);
                            maskClassRegion.Dispose();
                            Assert.Equal(areas[i], maskArea.Length > 0 ? maskArea[0].I : 0);
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
    public void 深度学习分割_ClassFilter过滤生效()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();

        // 模型类别名（实测 3 个）取第一个用于过滤。
        HOperatorSet.ReadDlModel(SegmentPillDefectsModelPath(), out HTuple model);
        string firstName;
        try
        {
            HOperatorSet.GetDlModelParam(model, "class_names", out HTuple classNamesTuple);
            firstName = classNamesTuple[0].S;
        }
        finally
        {
            HOperatorSet.ClearDlModel(model);
        }

        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        {
            // 只保留一个类：Regions 对象数 = 1，ClassNames / ClassIds / Areas 长度均为 1。
            using (var ctx = ContextWith(image))
            {
                DlSegmentTool tool = CreateTool();
                tool.ClassFilter = firstName;
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    HObject regions = ((HalconRegion)ctx.GetVariable(Module, "Regions").Value).Object;
                    HOperatorSet.CountObj(regions, out HTuple regionCount);
                    Assert.Equal(1, regionCount.I);
                    Assert.Equal(new[] { firstName }, ctx.GetVariable(Module, "ClassNames").GetValue<string[]>());
                    Assert.Single(ctx.GetVariable(Module, "Areas").GetValue<int[]>());
                    Assert.Single(ctx.GetVariable(Module, "ClassIds").GetValue<int[]>());
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }

            // 大小写不敏感：小写形式同样命中。
            using (var ctx = ContextWith(image))
            {
                DlSegmentTool tool = CreateTool();
                tool.ClassFilter = firstName.ToUpperInvariant();
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    Assert.Single(ctx.GetVariable(Module, "ClassNames").GetValue<string[]>());
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }

            // 过滤掉全部类（不存在的类别名）：0 类输出、Found=false、执行成功（FailWhenNotFound=false）。
            using (var ctx = ContextWith(image))
            {
                DlSegmentTool tool = CreateTool();
                tool.ClassFilter = "no_such_class";
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    Assert.False((bool)ctx.GetVariable(Module, "Found").Value);
                    Assert.Equal(0, (int)ctx.GetVariable(Module, "Count").Value);
                    Assert.Empty(ctx.GetVariable(Module, "ClassNames").GetValue<string[]>());
                    HObject regions = ((HalconRegion)ctx.GetVariable(Module, "Regions").Value).Object;
                    HOperatorSet.CountObj(regions, out HTuple regionCount);
                    Assert.Equal(0, regionCount.I);
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
    public void 深度学习分割_RejectedRegion语义固定_为定义域减类区域并集()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlSegmentTool tool = CreateTool();
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.True(run.IsSuccess, run.Message);

                // RejectedRegion = 整图定义域 − union1(类区域)：与 ColorSegmentTool 语义一致。
                HObject regions = ((HalconRegion)ctx.GetVariable(Module, "Regions").Value).Object;
                HObject rejected = ((HalconRegion)ctx.GetVariable(Module, "RejectedRegion").Value).Object;
                HOperatorSet.GetDomain(image, out HObject domain);
                HObject union = null;
                HObject expectedRejected = null;
                try
                {
                    HOperatorSet.Union1(regions, out union);
                    HOperatorSet.Difference(domain, union, out expectedRejected);
                    HOperatorSet.AreaCenter(expectedRejected, out HTuple expectedArea, out HTuple _, out HTuple _);
                    HOperatorSet.AreaCenter(rejected, out HTuple actualArea, out HTuple _, out HTuple _);
                    Assert.Equal(expectedArea[0].I, actualArea[0].I);
                }
                finally
                {
                    domain?.Dispose();
                    union?.Dispose();
                    expectedRejected?.Dispose();
                }
            }
            finally
            {
                tool.ReleaseResources();
            }
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分割_MinScore拉满全部类被过滤走NotFound()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        {
            // MinScore=1：类区域置信均值必然 < 1，全部类被区域级过滤丢弃。
            using (var ctx = ContextWith(image))
            {
                DlSegmentTool tool = CreateTool();
                tool.MinScore = 1.0;
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);
                    Assert.False((bool)ctx.GetVariable(Module, "Found").Value);
                    Assert.Equal(0, (int)ctx.GetVariable(Module, "Count").Value);
                    Assert.All(ctx.GetVariable(Module, "Areas").GetValue<int[]>(), a => Assert.Equal(0, a));
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }

            // FailWhenNotFound=true（默认）时全部为空应失败并走 NotFoundOutcome 措辞。
            using (var ctx = ContextWith(image))
            {
                var tool = new DlSegmentTool(Module)
                {
                    ModelFilePath = SegmentPillDefectsModelPath(),
                    ImagePath = "图像1.Image",
                    Device = DeepLearningDevicePreference.Cpu,
                    MinScore = 1.0,
                    FailWhenNotFound = true
                };
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.False(run.IsSuccess);
                    Assert.Contains("所有类别的区域均为空", run.Message);
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
    public void 深度学习分割_ROI先裁剪后推理_区域偏移回原图()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        {
            HOperatorSet.GetImageSize(image, out HTuple imgW, out HTuple imgH);
            // ROI 取带原点偏移 (40,30) 的内接矩形：工具先裁剪后推理，输出区域应映射回原图坐标系
            // 并落在该矩形范围内（§14.3 坐标回写语义，同 DL-02）。
            // 注意：两次独立推理在置信阈值边界上存在像素级抖动（CPU 多线程归约不保证逐位一致），
            // 因此本用例只断言几何映射（与 D2 ROI 用例同一策略），不做跨推理的逐值比较。
            const int rowOffset = 40;
            const int colOffset = 30;
            int cropHeight = imgH.I - 2 * rowOffset;
            int cropWidth = imgW.I - 2 * colOffset;
            HOperatorSet.GenRectangle1(out HObject roi, rowOffset, colOffset,
                rowOffset + cropHeight - 1, colOffset + cropWidth - 1);

            using (var ctx = ContextWith(image))
            {
                ctx.SetVariable(Variable.Object("ROI1", "Region", new HalconRegion(roi), 1));
                DlSegmentTool tool = CreateTool();
                tool.RegionPath = "ROI1.Region";
                try
                {
                    NodeResult run = tool.Run(ctx);
                    Assert.True(run.IsSuccess, run.Message);

                    HObject regions = ((HalconRegion)ctx.GetVariable(Module, "Regions").Value).Object;
                    HOperatorSet.CountObj(regions, out HTuple regionCount);
                    Assert.Equal(3, regionCount.I);
                    Assert.Equal(3, ctx.GetVariable(Module, "Areas").GetValue<int[]>().Length);

                    // 所有类区域 ⊆ ROI 矩形（映射回了原图坐标系，而不是停留在裁剪图坐标系）。
                    HOperatorSet.Union1(regions, out HObject union);
                    HOperatorSet.Complement(roi, out HObject outsideRoi);
                    HObject leaked = null;
                    try
                    {
                        HOperatorSet.Intersection(union, outsideRoi, out leaked);
                        HOperatorSet.AreaCenter(leaked, out HTuple leakArea, out HTuple _, out HTuple _0);
                        Assert.True(leakArea.Length == 0 || leakArea[0].I == 0,
                            "类区域不得超出 ROI 矩形（偏移未正确加回）");

                        // 正向断言：类区域确实覆盖到 ROI 矩形边缘（背景类大面积贴边，容差 2 px），
                        // 说明区域被平移到了 (40,30) 之后的位置而非贴原图左上角。
                        HOperatorSet.SmallestRectangle1(union, out HTuple uRow1, out HTuple uCol1, out HTuple uRow2, out HTuple uCol2);
                        Assert.InRange(uRow1.D, rowOffset - 2, rowOffset + 2);
                        Assert.InRange(uCol1.D, colOffset - 2, colOffset + 2);
                        Assert.InRange(uRow2.D, rowOffset + cropHeight - 3, rowOffset + cropHeight - 1);
                        Assert.InRange(uCol2.D, colOffset + cropWidth - 3, colOffset + cropWidth - 1);
                    }
                    finally
                    {
                        union?.Dispose();
                        outsideRoi?.Dispose();
                        leaked?.Dispose();
                    }

                    // MaskImage 仍是原图尺寸。
                    HObject mask = ((HalconImage)ctx.GetVariable(Module, "MaskImage").Value).Object;
                    HOperatorSet.GetImageSize(mask, out HTuple maskW, out HTuple maskH);
                    Assert.Equal(imgW.I, maskW.I);
                    Assert.Equal(imgH.I, maskH.I);
                }
                finally
                {
                    tool.ReleaseResources();
                }
            }

            roi?.Dispose();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 深度学习分割_模型类型不匹配报中文错误()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        HOperatorSet.ReadImage(out HObject image, PillGinsengImagePath());
        using (image)
        using (var ctx = ContextWith(image))
        {
            DlSegmentTool tool = CreateTool();
            tool.ModelFilePath = DetectPillsModelPath();
            try
            {
                NodeResult run = tool.Run(ctx);
                Assert.False(run.IsSuccess);
                Assert.Contains("类型不匹配", run.Message);
                Assert.Contains("segmentation", run.Message);
            }
            finally
            {
                tool.ReleaseResources();
            }
        }
    }
}
