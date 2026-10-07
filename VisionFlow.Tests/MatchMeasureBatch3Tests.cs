using System.IO;
using System.Text.Json.Nodes;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>MATCH-MEASURE-TOOLS-PLAN 第三批：MT-02（XLD / DXF 建模与模型原点）、MT-03（通用形状匹配）。</summary>
public class MatchMeasureBatch3Tests
{
    // ======================= 公共构造 =======================

    /// <summary>L 形 + 小方块（非对称），左上角 (r, c)：竖条 60×12、底横条 12×45、方块 12×12。</summary>
    private static HObject Shape(double r, double c)
    {
        HOperatorSet.GenRectangle1(out HObject a, r, c, r + 60, c + 12);
        HOperatorSet.GenRectangle1(out HObject b, r + 48, c, r + 60, c + 45);
        HOperatorSet.GenRectangle1(out HObject sq, r + 10, c + 30, r + 22, c + 42);
        HOperatorSet.Union2(a, b, out HObject ab);
        HOperatorSet.Union2(ab, sq, out HObject shape);
        foreach (HObject o in new[] { a, b, sq, ab }) o.Dispose();
        return shape;
    }

    private static HObject Triangle(double r, double c)
    {
        HOperatorSet.GenRegionPolygonFilled(out HObject tri, new HTuple(r, r + 80, r + 80), new HTuple(c + 40, c, c + 90));
        return tri;
    }

    private static void Paint(ref HObject image, HObject region)
    {
        HOperatorSet.PaintRegion(region, image, out HObject painted, 220, "fill");
        image.Dispose();
        region.Dispose();
        image = painted;
    }

    private static HObject Blank(int width = 640, int height = 480)
    {
        HOperatorSet.GenImageConst(out HObject blank, "byte", width, height);
        HOperatorSet.GenRectangle1(out HObject all, 0, 0, height - 1, width - 1);
        HOperatorSet.PaintRegion(all, blank, out HObject image, 30, "fill");
        blank.Dispose();
        all.Dispose();
        return image;
    }

    /// <summary>L 形在 (100,100) 与 (100,330)（后者列方向拉伸 1.3 倍，anisotropic=false 时为原样），三角形在 (300,150)。</summary>
    private static HObject SceneImage(bool anisotropic)
    {
        HObject image = Blank();
        Paint(ref image, Shape(100, 100));
        HObject second = Shape(0, 0);
        HOperatorSet.HomMat2dIdentity(out HTuple id);
        HOperatorSet.HomMat2dScale(id, 1.0, anisotropic ? 1.3 : 1.0, 0, 0, out HTuple scale);
        HOperatorSet.HomMat2dTranslate(scale, 100, 330, out HTuple move);
        HOperatorSet.AffineTransRegion(second, out HObject placed, move, "nearest_neighbor");
        second.Dispose();
        Paint(ref image, placed);
        Paint(ref image, Triangle(300, 150));
        return image;
    }

    /// <summary>用 (90,90)~(170,155) 的 ROI 训练 L 形模板。</summary>
    private static HTuple TrainL(HalconGenericShapeMatchTool settings, HObject image)
    {
        HOperatorSet.GenRectangle1(out HObject roi, 90, 90, 170, 155);
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        roi.Dispose();
        try
        {
            return GenericShapeTraining.Train(settings, template, fromXld: false);
        }
        finally
        {
            template.Dispose();
        }
    }

    private static HTuple TrainTriangle(HalconGenericShapeMatchTool settings, HObject image)
    {
        HOperatorSet.GenRectangle1(out HObject roi, 290, 140, 390, 250);
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        roi.Dispose();
        try
        {
            return GenericShapeTraining.Train(settings, template, fromXld: false);
        }
        finally
        {
            template.Dispose();
        }
    }

    private static byte[] SerializeAndClear(HTuple model)
    {
        try
        {
            return GenericShapeTraining.Serialize(model);
        }
        finally
        {
            HOperatorSet.ClearHandle(model);
        }
    }

    private static HalconGenericShapeMatchTool GenericTool(params (string Name, byte[] Data)[] templates)
    {
        var tool = new HalconGenericShapeMatchTool("通用1") { ImagePath = "Input.Image", NumMatches = 10, MinScore = 0.7 };
        SetTemplates(tool, templates);
        return tool;
    }

    private static void SetTemplates(HalconGenericShapeMatchTool tool, params (string Name, byte[] Data)[] templates)
    {
        tool.ModelsData = GenericShapeModelData.Pack(templates.Select(t => new GenericShapeTemplate(t.Name, t.Data)));
        tool.TemplateNamesCsv = string.Join(",", templates.Select(t => t.Name));
    }

    private static FlowContext ImageContext(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static List<MatchResultItem> Items(FlowContext ctx, string module = "通用1") =>
        ((System.Collections.IEnumerable)ctx.GetVariable(module, "Items").Value).Cast<MatchResultItem>().ToList();

    private static T[] Array<T>(FlowContext ctx, string module, string name) =>
        ((System.Collections.IEnumerable)ctx.GetVariable(module, name).Value).Cast<T>().ToArray();

    private static double Single(FlowContext ctx, string module, string name) => Convert.ToDouble(ctx.GetVariable(module, name).Value);

    private static IReadOnlyList<string> ConfigIssues(ToolBase tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(tool));
        return FlowValidator.Validate(root).Issues.Select(i => i.Message).ToList();
    }

    // ======================= MT-03 持久化 =======================

    [Fact]
    public void 持久化_多模板往返_名称与模型字节逐一相同()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        var settings = new HalconGenericShapeMatchTool("设置");
        byte[] l = SerializeAndClear(TrainL(settings, image));
        byte[] tri = SerializeAndClear(TrainTriangle(settings, image));
        byte[] packed = GenericShapeModelData.Pack(new[] { new GenericShapeTemplate("L形", l), new GenericShapeTemplate("三角 ✓", tri) });

        List<GenericShapeTemplate> back = GenericShapeModelData.Unpack(packed);
        Assert.Equal(new[] { "L形", "三角 ✓" }, back.Select(t => t.Name));
        Assert.Equal(l, back[0].ModelData);
        Assert.Equal(tri, back[1].ModelData);
        Assert.Equal(new[] { "L形", "三角 ✓" }, GenericShapeModelData.ReadNames(packed));
        Assert.Equal(1, BitConverter.ToInt32(packed, 0));
        Assert.Equal(2, BitConverter.ToInt32(packed, 4));
        Assert.Empty(GenericShapeModelData.Unpack(null));
        Assert.Empty(GenericShapeModelData.Unpack(GenericShapeModelData.Pack(System.Array.Empty<GenericShapeTemplate>())));

        // 流程文件往返：ModelsData 以 byte[] 保存
        var tool = GenericTool(("L形", l), ("三角 ✓", tri));
        var loaded = (HalconGenericShapeMatchTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(FlowSerializer.SaveNode(new ToolNode(tool)))).Tool;
        Assert.Equal(tool.ModelsData, loaded.ModelsData);
        Assert.Equal("L形,三角 ✓", loaded.TemplateNamesCsv);
        Assert.Equal(new[] { "L形", "三角 ✓" }, loaded.TemplateNames);
    }

    [Fact]
    public void 持久化_版本不符截断与多余字节给出明确错误_不运行()
    {
        byte[] packed = GenericShapeModelData.Pack(new[] { new GenericShapeTemplate("A", new byte[] { 1, 2, 3, 4 }) });

        byte[] wrongVersion = (byte[])packed.Clone();
        BitConverter.GetBytes(2).CopyTo(wrongVersion, 0);
        var versionError = Assert.Throws<InvalidDataException>(() => GenericShapeModelData.Unpack(wrongVersion));
        Assert.Contains("版本 2 不受支持（当前版本 1）", versionError.Message);

        Assert.Contains("截断", Assert.Throws<InvalidDataException>(() => GenericShapeModelData.Unpack(packed.Take(packed.Length - 2).ToArray())).Message);
        Assert.Contains("多余字节", Assert.Throws<InvalidDataException>(() => GenericShapeModelData.Unpack(packed.Concat(new byte[] { 0 }).ToArray())).Message);
        Assert.Contains("模板数", Assert.Throws<InvalidDataException>(() => GenericShapeModelData.Unpack(new byte[] { 1, 0, 0, 0, 255, 255, 255, 255 })).Message);

        var tool = new HalconGenericShapeMatchTool("通用1") { ImagePath = "Input.Image", ModelsData = wrongVersion };
        Assert.Contains(ConfigIssues(tool), m => m.Contains("版本 2 不受支持"));
        NodeResult result = tool.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Contains("版本 2 不受支持", result.Message);
    }

    [Fact]
    public void 模板名称校验_CSV不一致_重名_逗号_空名()
    {
        var tool = new HalconGenericShapeMatchTool("通用1") { ImagePath = "Input.Image" };
        SetTemplates(tool, ("A", new byte[] { 1 }), ("B", new byte[] { 2 }));
        Assert.DoesNotContain(ConfigIssues(tool), m => m.Contains("模板名称"));
        tool.TemplateNamesCsv = "A,C";
        Assert.Contains(ConfigIssues(tool), m => m.Contains("模板名称 CSV 与模型数据不一致（模型数据中为“A,B”）"));
        SetTemplates(tool, ("A", new byte[] { 1 }), ("A", new byte[] { 2 }));
        Assert.Contains(ConfigIssues(tool), m => m.Contains("模板名称“A”重复"));
        SetTemplates(tool, ("A,1", new byte[] { 1 }));
        Assert.Contains(ConfigIssues(tool), m => m.Contains("不能包含逗号"));
        SetTemplates(tool, (" ", new byte[] { 1 }));
        Assert.Contains(ConfigIssues(tool), m => m.Contains("模板名称不能为空"));
    }

    [Fact]
    public void 缓存键_沿用引用语义_同一数组复用句柄_替换数组重新加载()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        var settings = new HalconGenericShapeMatchTool("设置");
        byte[] l = SerializeAndClear(TrainL(settings, image));
        byte[] tri = SerializeAndClear(TrainTriangle(settings, image));
        var tool = GenericTool(("L", l));
        int Loads(FlowContext ctx) => ctx.Log.Count(m => m.Contains("模型加载耗时"));

        using (FlowContext ctx = ImageContext(image))
        {
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(1, Loads(ctx));
            Assert.Equal(2, (int)ctx.GetVariable("通用1", "MatchCount").Value);
            // 示教/导入整体替换数组 → 新引用 → 重新加载（增加模板后结果随之变化）
            SetTemplates(tool, ("L", l), ("三角", tri));
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(2, Loads(ctx));
            Assert.Equal(3, (int)ctx.GetVariable("通用1", "MatchCount").Value);
            // 释放资源后再次运行重新加载
            FlowResources.Release(tool);
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(3, Loads(ctx));
        }
    }

    // ======================= MT-03 匹配结果 =======================

    [Fact]
    public void 各向异性缩放_HomMats与显示轮廓与直接算子调用逐项一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(true);
        var settings = new HalconGenericShapeMatchTool("设置")
        {
            AngleStart = -0.3, AngleEnd = 0.3, ScaleMode = ScaleMode.Anisotropic,
            ScaleRowMin = 0.9, ScaleRowMax = 1.1, ScaleColumnMin = 0.9, ScaleColumnMax = 1.4
        };
        byte[] l = SerializeAndClear(TrainL(settings, image));
        var tool = GenericTool(("L", l));
        tool.AngleStart = -0.3;
        tool.AngleEnd = 0.3;

        // 直接算子调用（同样的查找参数）
        HTuple model = GenericShapeTraining.Deserialize(l);
        var expectedMats = new List<double[]>();
        var expectedContours = new List<(double[] Rows, double[] Cols)>();
        var expectedScales = new List<(double R, double C)>();
        try
        {
            HOperatorSet.SetGenericShapeModelParam(model, "angle_start", -0.3);
            HOperatorSet.SetGenericShapeModelParam(model, "angle_end", 0.3);
            HOperatorSet.SetGenericShapeModelParam(model, "min_score", 0.7);
            HOperatorSet.SetGenericShapeModelParam(model, "num_matches", 10);
            HOperatorSet.FindGenericShapeModel(image, model, out HTuple result, out HTuple count);
            try
            {
                for (int i = 0; i < count.I; i++)
                {
                    HOperatorSet.GetGenericShapeModelResult(result, i, "hom_mat_2d", out HTuple mat);
                    expectedMats.Add(mat.DArr);
                    HOperatorSet.GetGenericShapeModelResult(result, i, "scale_row", out HTuple sr);
                    HOperatorSet.GetGenericShapeModelResult(result, i, "scale_column", out HTuple sc);
                    expectedScales.Add((sr.D, sc.D));
                    HOperatorSet.GetShapeModelContours(out HObject modelContours, model, 1);
                    HOperatorSet.AffineTransContourXld(modelContours, out HObject placed, mat);
                    HOperatorSet.GetContourXld(placed.SelectObj(1), out HTuple rows, out HTuple cols);
                    expectedContours.Add((rows.DArr, cols.DArr));
                    modelContours.Dispose();
                    placed.Dispose();
                }
            }
            finally
            {
                HOperatorSet.ClearHandle(result);
            }
        }
        finally
        {
            HOperatorSet.ClearHandle(model);
        }

        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        List<MatchResultItem> items = Items(ctx);
        Assert.Equal(2, items.Count);
        Assert.Equal(expectedMats.Count, items.Count);
        HomMat2D[] homMats = Array<HomMat2D>(ctx, "通用1", "HomMats");
        for (int i = 0; i < items.Count; i++)
        {
            // 基准位姿为 0：跟随矩阵就是 hom_mat_2d 本身
            Assert.Equal(expectedMats[i], homMats[i].Data.DArr);
            Assert.Equal(expectedScales[i].R, items[i].ScaleRow);
            Assert.Equal(expectedScales[i].C, items[i].ScaleColumn);
            Assert.Equal((expectedScales[i].R + expectedScales[i].C) / 2, items[i].Scale);
            HOperatorSet.GetContourXld(items[i].Contour.SelectObj(1), out HTuple rows, out HTuple cols);
            Assert.Equal(expectedContours[i].Rows, rows.DArr);
            Assert.Equal(expectedContours[i].Cols, cols.DArr);
        }
        // 拉伸副本的列缩放约 1.3，且矩阵线性部分确实各向异性（不能用 Scale 重建）
        MatchResultItem stretched = items.Single(i => i.Column > 300);
        Assert.InRange(stretched.ScaleColumn, 1.25, 1.35);
        Assert.InRange(stretched.ScaleRow, 0.97, 1.03);
        Assert.InRange(stretched.HomMat.Data[4].D / stretched.HomMat.Data[0].D, 1.25, 1.35);
        Assert.Equal(items.Select(i => i.ScaleRow), Array<double>(ctx, "通用1", "ScaleRows"));
        Assert.Equal(items.Select(i => i.ScaleColumn), Array<double>(ctx, "通用1", "ScaleColumns"));

        // 基准位姿非 0：HomMat = hom_mat_2d · 基准位姿⁻¹
        tool.BaseRow = 130;
        tool.BaseColumn = 122.5;
        tool.BaseAngle = 0.05;
        using FlowContext based = ImageContext(image);
        Assert.True(tool.Run(based).IsSuccess);
        HomMat2D[] followed = Array<HomMat2D>(based, "通用1", "HomMats");
        HOperatorSet.VectorAngleToRigid(0, 0, 0, 130, 122.5, 0.05, out HTuple basePose);
        HOperatorSet.HomMat2dInvert(basePose, out HTuple inverse);
        for (int i = 0; i < followed.Length; i++)
        {
            HOperatorSet.HomMat2dCompose(new HTuple(expectedMats[i]), inverse, out HTuple expected);
            Assert.Equal(expected.DArr, followed[i].Data.DArr);
        }
    }

    [Fact]
    public void 多模板同时匹配_模板序号名称与每模板数量()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        var settings = new HalconGenericShapeMatchTool("设置");
        var tool = GenericTool(("L形", SerializeAndClear(TrainL(settings, image))), ("三角", SerializeAndClear(TrainTriangle(settings, image))));
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        List<MatchResultItem> items = Items(ctx);
        Assert.Equal(3, items.Count);
        int[] indices = Array<int>(ctx, "通用1", "ModelIndices");
        string[] names = Array<string>(ctx, "通用1", "ModelNames");
        Assert.Equal(items.Select(i => i.ModelIndex), indices);
        Assert.Equal(indices.Select(i => i == 0 ? "L形" : "三角"), names);
        Assert.All(items.Where(i => i.ModelIndex == 1), i => Assert.InRange(i.Row, 300, 400));
        Assert.Equal(new[] { 2, 1 }, Array<int>(ctx, "通用1", "CountsPerModel"));
        MatchResultItem best = (MatchResultItem)ctx.GetVariable("通用1", "BestMatch").Value;
        Assert.Equal(best.ModelIndex, (int)ctx.GetVariable("通用1", "BestModelIndex").Value);
        Assert.Equal(best.ModelName, (string)ctx.GetVariable("通用1", "BestModelName").Value);
        Assert.Equal(items.Max(i => i.Score), best.Score);
    }

    [Fact]
    public void 搜索区域与排序_在通用形状匹配上同样生效()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        var settings = new HalconGenericShapeMatchTool("设置");
        var tool = GenericTool(("L形", SerializeAndClear(TrainL(settings, image))), ("三角", SerializeAndClear(TrainTriangle(settings, image))));
        tool.SortBy = MatchSortBy.Column;
        using (FlowContext ctx = ImageContext(image))
        {
            Assert.True(tool.Run(ctx).IsSuccess);
            List<MatchResultItem> sorted = Items(ctx);
            Assert.Equal(sorted.Select(i => i.Column).OrderBy(c => c), sorted.Select(i => i.Column));
            Assert.Equal(Enumerable.Range(0, 3), sorted.Select(i => i.Index));
            Assert.Equal(sorted.Select(i => i.ModelIndex), Array<int>(ctx, "通用1", "ModelIndices"));
            Assert.Equal(sorted.Max(i => i.Score), Single(ctx, "通用1", "Score"));
        }

        HOperatorSet.GenRectangle1(out HObject right, 0, 250, 479, 639);
        tool.SearchRegionPath = "区域.R";
        tool.SortBy = MatchSortBy.Score;
        using (FlowContext ctx = ImageContext(image))
        {
            ctx.SetVariable(Variable.Object("区域", "R", new HalconRegion(right), 1));
            Assert.True(tool.Run(ctx).IsSuccess);
            List<MatchResultItem> found = Items(ctx);
            Assert.Single(found);
            Assert.True(found[0].Column > 250);
            Assert.Equal(new[] { 1, 0 }, Array<int>(ctx, "通用1", "CountsPerModel"));
        }
    }

    [Fact]
    public void 杂乱区域_超出最大杂乱比例的目标被剔除_关闭后保留()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject clean = SceneImage(false);
        var settings = new HalconGenericShapeMatchTool("设置");
        HTuple model = TrainL(settings, clean);
        // 杂乱区域在示教图像坐标中（L 形右侧的空白处），HALCON 按训练位姿换算
        HOperatorSet.GenRectangle1(out HObject clutter, 100, 160, 160, 180);
        GenericShapeTraining.SetClutterRegion(model, clutter);
        Assert.True(GenericShapeTraining.TryGetClutterRegion(model, out HObject back, out double area));
        back.Dispose();
        Assert.True(area > 1000);
        var tool = GenericTool(("L", SerializeAndClear(model)));

        // 第一个目标的杂乱区里出现一条亮线
        HObject dirty = clean.CopyObj(1, -1);
        HOperatorSet.GenRectangle1(out HObject junk, 110, 165, 150, 170);
        Paint(ref dirty, junk);
        try
        {
            tool.UseClutter = true;
            tool.MaxClutter = 0.05;
            using (FlowContext ctx = ImageContext(dirty))
            {
                Assert.True(tool.Run(ctx).IsSuccess);
                MatchResultItem only = Assert.Single(Items(ctx));
                Assert.True(only.Column > 300);
            }
            tool.UseClutter = false;
            using (FlowContext ctx = ImageContext(dirty))
            {
                Assert.True(tool.Run(ctx).IsSuccess);
                Assert.Equal(2, Items(ctx).Count);
            }

            // 多模板：开启杂乱判定时每个模板都必须有杂乱区域（HALCON 要求同一次查找的全部模型一致）
            byte[] withClutter = tool.ModelsData;
            byte[] withoutClutter = SerializeAndClear(TrainTriangle(settings, clean));
            SetTemplates(tool, ("L", GenericShapeModelData.Unpack(withClutter)[0].ModelData), ("三角", withoutClutter));
            tool.UseClutter = true;
            using (FlowContext ctx = ImageContext(dirty))
            {
                NodeResult result = tool.Run(ctx);
                Assert.False(result.IsSuccess);
                Assert.Contains("启用杂乱判定时每个模板都必须设置杂乱区域", result.Message);
                Assert.Contains("“三角”没有杂乱区域", result.Message);
            }
            tool.UseClutter = false;
            using (FlowContext ctx = ImageContext(dirty))
            {
                Assert.True(tool.Run(ctx).IsSuccess);
                Assert.Equal(new[] { 2, 1 }, Array<int>(ctx, "通用1", "CountsPerModel"));
            }
        }
        finally
        {
            clutter.Dispose();
            dirty.Dispose();
        }
    }

    [Fact]
    public void 越界匹配与超时_超时单位为毫秒()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject scene = SceneImage(false);
        var settings = new HalconGenericShapeMatchTool("设置");
        byte[] l = SerializeAndClear(TrainL(settings, scene));

        // L 形左侧 10 列超出图像
        HObject border = Blank();
        Paint(ref border, Shape(200, -10));
        try
        {
            var tool = GenericTool(("L", l));
            tool.MinScore = 0.5;
            tool.FailWhenNotFound = false;
            using (FlowContext ctx = ImageContext(border))
            {
                Assert.True(tool.Run(ctx).IsSuccess);
                Assert.Equal(0, (int)ctx.GetVariable("通用1", "MatchCount").Value);
            }
            tool.BorderShapeModels = true;
            using (FlowContext ctx = ImageContext(border))
            {
                Assert.True(tool.Run(ctx).IsSuccess);
                Assert.Equal(1, (int)ctx.GetVariable("通用1", "MatchCount").Value);
            }
        }
        finally
        {
            border.Dispose();
        }

        // 超时：全角度、宽缩放、低分数在大幅噪声图上搜索，远超 50 ms
        var slowSettings = new HalconGenericShapeMatchTool("设置")
        {
            AngleStart = -3.14, AngleEnd = 3.14, ScaleMode = ScaleMode.Isotropic, IsoScaleMin = 0.6, IsoScaleMax = 1.4
        };
        byte[] slow = SerializeAndClear(TrainL(slowSettings, scene));
        HOperatorSet.GenImageConst(out HObject flat, "byte", 1600, 1200);
        HOperatorSet.AddNoiseWhite(flat, out HObject noise, 120);
        flat.Dispose();
        try
        {
            var timed = GenericTool(("L", slow));
            timed.AngleStart = -3.14;
            timed.AngleEnd = 3.14;
            timed.MinScore = 0.1;
            timed.Greediness = 0;
            timed.NumMatches = 0;
            timed.TimeoutMs = 50;
            using FlowContext ctx = ImageContext(noise);
            timed.Prepare();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            NodeResult result = timed.Run(ctx);
            watch.Stop();
            Assert.False(result.IsSuccess);
            Assert.Contains("查找超时（超过 50 ms）", result.Message);
            Assert.True(watch.ElapsedMilliseconds < 5000, $"超时后应很快返回，实际 {watch.ElapsedMilliseconds} ms");
        }
        finally
        {
            noise.Dispose();
        }
    }

    [Fact]
    public void 通用形状模型原点_输出Row_Column为原点位置()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        var settings = new HalconGenericShapeMatchTool("设置");
        byte[] plain = SerializeAndClear(TrainL(settings, image));
        HTuple model = TrainL(settings, image);
        GenericShapeTraining.SetOrigin(model, -20, -10);
        GenericShapeTraining.GetOrigin(model, out double originRow, out double originColumn);
        Assert.Equal((-20.0, -10.0), (originRow, originColumn));
        byte[] shifted = SerializeAndClear(model);

        var tool = GenericTool(("L", plain));
        tool.SortBy = MatchSortBy.Column;
        using FlowContext a = ImageContext(image);
        Assert.True(tool.Run(a).IsSuccess);
        List<MatchResultItem> before = Items(a);
        SetTemplates(tool, ("L", shifted));
        using FlowContext b = ImageContext(image);
        Assert.True(tool.Run(b).IsSuccess);
        List<MatchResultItem> after = Items(b);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Row - 20, after[i].Row, 2);
            Assert.Equal(before[i].Column - 10, after[i].Column, 2);
        }
        // 左边目标的原点正好落在 L 形竖条中部（参考点 (130, 122.5) + (-20, -10)）
        Assert.Equal(110, after[0].Row, 1);
        Assert.Equal(112.5, after[0].Column, 1);
    }

    // ======================= MT-02 XLD / DXF / 原点 =======================

    private static HObject LContour(double r, double c)
    {
        using HObject region = Shape(r, c);
        HOperatorSet.GenContourRegionXld(region, out HObject contour, "border");
        return contour;
    }

    [Fact]
    public void 原点_模板匹配与灰度匹配输出即原点_零偏移不改变模型字节()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        HOperatorSet.GenRectangle1(out HObject roi, 90, 90, 170, 155);
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        roi.Dispose();
        try
        {
            HOperatorSet.CreateShapeModel(template, "auto", -0.3, 0.6, "auto", "auto", "use_polarity", "auto", "auto", out HTuple shape);
            byte[] plainShape = ShapeModelSerialization.Serialize(shape);
            MatchModelBuilder.ApplyOrigin(shape, false, 0, 0);
            Assert.Equal(plainShape, ShapeModelSerialization.Serialize(shape));
            MatchModelBuilder.ApplyOrigin(shape, false, -20, -10);
            MatchModelBuilder.GetOrigin(shape, false, out double r, out double c);
            Assert.Equal((-20.0, -10.0), (r, c));
            byte[] shiftedShape = ShapeModelSerialization.Serialize(shape);
            HOperatorSet.ClearShapeModel(shape);

            HOperatorSet.CreateNccModel(template, "auto", -0.3, 0.6, "auto", "use_polarity", out HTuple ncc);
            byte[] plainNcc = ShapeModelSerialization.SerializeNcc(ncc);
            MatchModelBuilder.ApplyOrigin(ncc, true, 0, 0);
            Assert.Equal(plainNcc, ShapeModelSerialization.SerializeNcc(ncc));
            MatchModelBuilder.ApplyOrigin(ncc, true, -20, -10);
            byte[] shiftedNcc = ShapeModelSerialization.SerializeNcc(ncc);
            HOperatorSet.ClearNccModel(ncc);

            foreach ((Func<byte[], HalconTemplateMatchToolBase> create, byte[] plain, byte[] shifted) in new (Func<byte[], HalconTemplateMatchToolBase>, byte[], byte[])[]
                     {
                         (d => new HalconModelMatchTool("匹配1") { ShapeModelData = d }, plainShape, shiftedShape),
                         (d => new HalconGrayMatchTool("匹配1") { ShapeModelData = d }, plainNcc, shiftedNcc)
                     })
            {
                HalconTemplateMatchToolBase before = create(plain);
                HalconTemplateMatchToolBase after = create(shifted);
                foreach (HalconTemplateMatchToolBase t in new[] { before, after })
                {
                    t.ImagePath = "Input.Image";
                    t.MinScore = 0.7;
                    t.FindStartAngle = -0.3;
                    t.FindExtentAngle = 0.6;
                    t.SortBy = MatchSortBy.Column;
                }
                after.ModelOriginRow = -20;
                after.ModelOriginColumn = -10;
                using FlowContext a = ImageContext(image);
                using FlowContext b = ImageContext(image);
                Assert.True(before.Run(a).IsSuccess);
                Assert.True(after.Run(b).IsSuccess);
                List<MatchResultItem> x = Items(a, "匹配1");
                List<MatchResultItem> y = Items(b, "匹配1");
                Assert.Equal(2, y.Count);
                for (int i = 0; i < x.Count; i++)
                {
                    Assert.Equal(x[i].Row - 20, y[i].Row, 2);
                    Assert.Equal(x[i].Column - 10, y[i].Column, 2);
                }
                // 显示轮廓仍画在目标上（模型轮廓随原点一起平移）
                HOperatorSet.SmallestRectangle1Xld(y[0].Contour, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                Assert.InRange(r1.TupleMin().D, 85, 105);
                Assert.InRange(r2.TupleMax().D, 155, 175);
            }
        }
        finally
        {
            template.Dispose();
        }
    }

    [Fact]
    public void XLD建模_多条轮廓共同组成模板_按序号取单条_越界报错()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        using HObject l = LContour(0, 0);
        using HObject tri = LContourOfTriangle();
        HOperatorSet.ConcatObj(l, tri, out HObject both);
        try
        {
            HOperatorSet.CountObj(l, out HTuple lCount);
            HOperatorSet.CountObj(both, out HTuple total);
            using (HObject all = MatchModelBuilder.SelectContours(both, -1))
            {
                HOperatorSet.CountObj(all, out HTuple n);
                Assert.Equal(total.I, n.I);
            }
            using (HObject first = MatchModelBuilder.SelectContours(both, 0))
            {
                HOperatorSet.CountObj(first, out HTuple n);
                Assert.Equal(1, n.I);
            }
            var outOfRange = Assert.Throws<InvalidOperationException>(() => MatchModelBuilder.SelectContours(both, total.I));
            Assert.Contains($"轮廓序号 {total.I} 超出范围（共 {total.I} 条", outOfRange.Message);
            HOperatorSet.GenEmptyObj(out HObject empty);
            Assert.Contains("XLD 轮廓为空", Assert.Throws<InvalidOperationException>(() => MatchModelBuilder.SelectContours(empty, -1)).Message);
            empty.Dispose();

            // 用 L 形的全部轮廓（L 形与方块两条）建模，能在图像中找到两个 L 形
            using HObject lAll = MatchModelBuilder.SelectContours(l, -1);
            HTuple model = MatchModelBuilder.CreateShapeModelXld(lAll, "auto", -0.3, 0.6, "auto", "auto", 5);
            HOperatorSet.GetShapeModelContours(out HObject modelContours, model, 1);
            HOperatorSet.CountObj(modelContours, out HTuple modelCount);
            modelContours.Dispose();
            Assert.Equal(lCount.I, modelCount.I);
            var tool = new HalconModelMatchTool("匹配1")
            {
                ImagePath = "Input.Image", ShapeModelData = ShapeModelSerialization.Serialize(model), MinScore = 0.7,
                FindStartAngle = -0.3, FindExtentAngle = 0.6, SortBy = MatchSortBy.Column,
                ModelSource = MatchModelSource.Xld, TeachMetric = MatchModelBuilder.XldMetric, TeachXldPath = "边缘.Xld", TeachXldIndex = -1
            };
            HOperatorSet.ClearShapeModel(model);
            Assert.Empty(tool.CheckConfiguration());
            using FlowContext ctx = ImageContext(image);
            Assert.True(tool.Run(ctx).IsSuccess);
            List<MatchResultItem> found = Items(ctx, "匹配1");
            Assert.Equal(2, found.Count);
            // XLD 模型的参考点是轮廓外接矩形中心：L 形 (0..60, 0..45) → 图像中 (130, 122.5)
            Assert.Equal(130, found[0].Row, 0);
            Assert.Equal(122.5, found[0].Column, 0);

            using HObject lScaled = MatchModelBuilder.SelectContours(l, -1);
            HTuple scaled = MatchModelBuilder.CreateScaledShapeModelXld(lScaled, "auto", -0.3, 0.6, "auto", 0.9, 1.1, "auto", "auto", 5);
            HOperatorSet.FindScaledShapeModel(image, scaled, -0.3, 0.6, 0.9, 1.1, 0.7, 0, 0.5, "least_squares", 0, 0.9,
                out HTuple rows, out _, out _, out _, out _);
            HOperatorSet.ClearShapeModel(scaled);
            Assert.Equal(2, rows.Length);

            // 示教页金字塔默认值为 0：XLD 建模算子不接受 0（#1301），按自动处理，与 "auto" 得到相同模型
            // （模型序列化字节每次创建有少量不同，按模型参数与查找结果比较）
            HTuple zeroLevels = MatchModelBuilder.CreateShapeModelXld(lAll, 0, -0.3, 0.6, "auto", "auto", 5);
            HTuple autoLevels = MatchModelBuilder.CreateShapeModelXld(lAll, "auto", -0.3, 0.6, "auto", "auto", 5);
            HOperatorSet.GetShapeModelParams(zeroLevels, out HTuple zeroNum, out _, out _, out _, out _, out _, out _, out _, out _);
            HOperatorSet.GetShapeModelParams(autoLevels, out HTuple autoNum, out _, out _, out _, out _, out _, out _, out _, out _);
            Assert.Equal(autoNum.L, zeroNum.L);
            HOperatorSet.FindShapeModel(image, zeroLevels, -0.3, 0.6, 0.7, 0, 0.5, "least_squares", 0, 0.9, out HTuple zr, out HTuple zc, out _, out HTuple zs);
            HOperatorSet.FindShapeModel(image, autoLevels, -0.3, 0.6, 0.7, 0, 0.5, "least_squares", 0, 0.9, out HTuple ar, out HTuple ac, out _, out HTuple scoresAuto);
            Assert.Equal(ar.DArr, zr.DArr);
            Assert.Equal(ac.DArr, zc.DArr);
            Assert.Equal(scoresAuto.DArr, zs.DArr);
            HOperatorSet.ClearShapeModel(zeroLevels);
            HOperatorSet.ClearShapeModel(autoLevels);
            HTuple scaledZero = MatchModelBuilder.CreateScaledShapeModelXld(lAll, 0, -0.3, 0.6, "auto", 0.9, 1.1, "auto", "auto", 5);
            HOperatorSet.ClearShapeModel(scaledZero);
        }
        finally
        {
            both.Dispose();
        }
    }

    private static HObject LContourOfTriangle()
    {
        using HObject region = Triangle(0, 0);
        HOperatorSet.GenContourRegionXld(region, out HObject contour, "border");
        return contour;
    }

    [Fact]
    public void DXF建模_读取成功_文件不存在与解析失败明确报错_通用形状可从XLD训练()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        string dir = Path.Combine(Path.GetTempPath(), "vf-mm3-dxf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using HObject l = LContour(0, 0);
            string dxf = Path.Combine(dir, "l.dxf");
            HOperatorSet.WriteContourXldDxf(l, dxf);
            using HObject read = MatchModelBuilder.ReadDxf(dxf);
            HOperatorSet.CountObj(read, out HTuple n);
            HOperatorSet.CountObj(l, out HTuple expected);
            Assert.Equal(expected.I, n.I);

            Assert.Contains("DXF 文件不存在", Assert.Throws<InvalidOperationException>(() => MatchModelBuilder.ReadDxf(Path.Combine(dir, "nope.dxf"))).Message);
            string bad = Path.Combine(dir, "bad.dxf");
            File.WriteAllText(bad, "not a dxf");
            Assert.Contains("DXF 文件解析失败", Assert.Throws<InvalidOperationException>(() => MatchModelBuilder.ReadDxf(bad)).Message);
            Assert.Contains("未选择 DXF 文件", Assert.Throws<InvalidOperationException>(() => MatchModelBuilder.ReadDxf(" ")).Message);

            // 通用形状：DXF 轮廓训练（度量自动为 ignore_local_polarity），运行不依赖 DXF 文件
            using HObject image = SceneImage(false);
            var settings = new HalconGenericShapeMatchTool("设置") { Metric = "use_polarity" };
            HTuple model = GenericShapeTraining.Train(settings, read, fromXld: true);
            HOperatorSet.GetGenericShapeModelParam(model, "metric", out HTuple metric);
            Assert.Equal(MatchModelBuilder.XldMetric, metric.S);
            var tool = GenericTool(("DXF-L", SerializeAndClear(model)));
            File.Delete(dxf);
            using FlowContext ctx = ImageContext(image);
            Assert.True(tool.Run(ctx).IsSuccess);
            Assert.Equal(2, (int)ctx.GetVariable("通用1", "MatchCount").Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void XLD来源校验_度量非ignore_local_polarity报错且不运行_不支持的工具报错()
    {
        var model = new HalconModelMatchTool("匹配1") { ImagePath = "Input.Image", ModelSource = MatchModelSource.Xld, TeachMetric = "use_polarity" };
        Assert.Contains(ConfigIssues(model), m => m.Contains("XLD / DXF 建模的度量只能是 ignore_local_polarity"));
        NodeResult result = model.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Contains("TeachMetric", result.Message);
        model.ModelSource = MatchModelSource.Dxf;
        Assert.Contains(ConfigIssues(model), m => m.Contains("XLD / DXF 建模的度量只能是"));
        model.TeachMetric = MatchModelBuilder.XldMetric;
        Assert.Empty(model.CheckConfiguration());

        var gray = new HalconGrayMatchTool("灰度1") { ModelSource = MatchModelSource.Xld, TeachMetric = MatchModelBuilder.XldMetric };
        Assert.Contains(gray.CheckConfiguration(), i => i.Message == "灰度匹配不支持 XLD / DXF 建模");
        var deformable = new HalconLocalDeformableMatchTool("变形1") { ModelOriginRow = 3 };
        Assert.Contains(deformable.CheckConfiguration(), i => i.Message == "局部变形匹配不支持模型原点");
        Assert.True(new HalconScaledShapeMatchTool("x").SupportsXldModel);
        Assert.False(new HalconGrayMatchTool("x").SupportsXldModel);
        Assert.True(new HalconGrayMatchTool("x").SupportsModelOrigin);
        Assert.False(new HalconLocalDeformableMatchTool("x").SupportsModelOrigin);
    }

    [Fact]
    public void 回归门禁_四个匹配工具新属性默认等于原行为_历史文件加载不变_示教记录不进侧栏()
    {
        foreach (HalconTemplateMatchToolBase tool in new HalconTemplateMatchToolBase[]
                 {
                     new HalconModelMatchTool("a"), new HalconGrayMatchTool("b"), new HalconScaledShapeMatchTool("c"), new HalconLocalDeformableMatchTool("d")
                 })
        {
            Assert.Equal(MatchModelSource.ImageRoi, tool.ModelSource);
            Assert.Null(tool.TeachMetric);
            Assert.Equal(-1, tool.TeachXldIndex);
            Assert.Equal(0, tool.ModelOriginRow);
            Assert.Equal(0, tool.ModelOriginColumn);
            Assert.Empty(tool.CheckConfiguration());
            foreach (string name in new[] { "ModelSource", "TeachMetric", "TeachXldPath", "TeachXldIndex", "DxfPath", "ModelOriginRow", "ModelOriginColumn" })
            {
                Assert.False(tool.IsParameterVisible(name), name);
            }
            Assert.True(tool.IsParameterVisible("MinScore"));

            var node = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(tool)))!.AsObject();
            JsonObject properties = node["Tool"]!["Properties"]!.AsObject();
            foreach (string name in new[] { "ModelSource", "TeachMetric", "TeachXldPath", "TeachXldIndex", "DxfPath", "ModelOriginRow", "ModelOriginColumn" })
            {
                properties.Remove(name);
            }
            var legacy = (HalconTemplateMatchToolBase)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(node.ToJsonString())).Tool;
            Assert.Equal(MatchModelSource.ImageRoi, legacy.ModelSource);
            Assert.Equal(0, legacy.ModelOriginRow);
        }
        string json = FlowSerializer.SaveNode(new ToolNode(new HalconModelMatchTool("a") { ModelSource = MatchModelSource.Dxf, TeachMetric = MatchModelBuilder.XldMetric }));
        Assert.Contains("\"ModelSource\": 2", json);
    }

    [Fact]
    public void 回归门禁_模板匹配图像ROI模型的运行结果与直接调用逐项一致()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        HOperatorSet.GenRectangle1(out HObject roi, 90, 90, 170, 155);
        HOperatorSet.ReduceDomain(image, roi, out HObject template);
        roi.Dispose();
        HOperatorSet.CreateShapeModel(template, "auto", -0.3, 0.6, "auto", "auto", "use_polarity", "auto", "auto", out HTuple model);
        template.Dispose();
        byte[] data = ShapeModelSerialization.Serialize(model);
        HOperatorSet.FindShapeModel(image, model, -0.3, 0.6, 0.7, 10, 0.5, "least_squares", 0, 0.9, out HTuple rows, out HTuple cols, out HTuple angles, out HTuple scores);
        HOperatorSet.ClearShapeModel(model);
        var tool = new HalconModelMatchTool("匹配1") { ImagePath = "Input.Image", ShapeModelData = data, MinScore = 0.7, FindStartAngle = -0.3, FindExtentAngle = 0.6 };
        using FlowContext ctx = ImageContext(image);
        Assert.True(tool.Run(ctx).IsSuccess);
        List<MatchResultItem> items = Items(ctx, "匹配1");
        Assert.Equal(rows.DArr, items.Select(i => i.Row));
        Assert.Equal(cols.DArr, items.Select(i => i.Column));
        Assert.Equal(angles.DArr, items.Select(i => i.Angle));
        Assert.Equal(scores.DArr, items.Select(i => i.Score));
        Assert.All(items, i => Assert.Equal(0, i.ModelIndex));
        Assert.All(items, i => Assert.Null(i.ModelName));
    }

    // ======================= MT-03 元数据与登记 =======================

    [Fact]
    public void 命名守卫_TemplateNamesCsv参数与ModelNames输出不冲突()
    {
        var tool = new HalconGenericShapeMatchTool("通用1");
        var properties = new HashSet<string>(tool.GetType().GetProperties().Select(p => p.Name));
        IReadOnlyList<ToolOutputDef> outputs = ToolMetadata.GetOutputs(tool);
        Assert.DoesNotContain(outputs, o => properties.Contains(o.Name));
        Assert.Contains("TemplateNamesCsv", properties);
        Assert.DoesNotContain("ModelNames", properties);
        foreach ((string name, VariableKind kind, VariableType type) in new[]
                 {
                     ("ScaleRows", VariableKind.Array, VariableType.Double), ("ScaleColumns", VariableKind.Array, VariableType.Double),
                     ("ModelIndices", VariableKind.Array, VariableType.Int), ("ModelNames", VariableKind.Array, VariableType.String),
                     ("BestModelIndex", VariableKind.Single, VariableType.Int), ("BestModelName", VariableKind.Single, VariableType.String),
                     ("CountsPerModel", VariableKind.Array, VariableType.Int)
                 })
        {
            Assert.Contains(outputs, o => o.Name == name && o.Kind == kind && o.Type == type);
        }
        // 继承匹配基类的全部输出
        foreach (ToolOutputDef baseOutput in ToolMetadata.GetOutputs(typeof(HalconModelMatchTool)))
        {
            Assert.Contains(outputs, o => o.Name == baseOutput.Name);
        }
    }

    [Fact]
    public void 参数显隐_建模参数侧栏不显示_杂乱参数随开关_缩放组随缩放方式()
    {
        var tool = new HalconGenericShapeMatchTool("通用1");
        foreach (string name in HalconGenericShapeMatchTool.TrainingParameters.Append("TemplateNamesCsv"))
        {
            Assert.False(tool.IsParameterVisible(name), name);
        }
        foreach (string name in new[] { "AngleStart", "AngleEnd", "MinScore", "NumMatches", "MaxOverlap", "Greediness", "SubPixel", "BorderShapeModels", "MaxDeformation", "TimeoutMs", "UseClutter", "SearchRegionPath" })
        {
            Assert.True(tool.IsParameterVisible(name), name);
        }
        Assert.False(tool.IsParameterVisible("MaxClutter"));
        tool.UseClutter = true;
        Assert.True(tool.IsParameterVisible("MaxClutter"));
        Assert.True(tool.IsParameterVisible("ClutterContrast"));

        Assert.False(HalconGenericShapeMatchTool.IsScaleParameterVisible(ScaleMode.None, "IsoScaleMin"));
        Assert.False(HalconGenericShapeMatchTool.IsScaleParameterVisible(ScaleMode.None, "ScaleRowMin"));
        Assert.True(HalconGenericShapeMatchTool.IsScaleParameterVisible(ScaleMode.Isotropic, "IsoScaleMax"));
        Assert.False(HalconGenericShapeMatchTool.IsScaleParameterVisible(ScaleMode.Isotropic, "ScaleColumnMax"));
        Assert.True(HalconGenericShapeMatchTool.IsScaleParameterVisible(ScaleMode.Anisotropic, "ScaleColumnMax"));
        Assert.False(HalconGenericShapeMatchTool.IsScaleParameterVisible(ScaleMode.Anisotropic, "IsoScaleMin"));

        Assert.Contains(ConfigIssues(new HalconGenericShapeMatchTool("x") { ImagePath = "Input.Image", TimeoutMs = -1 }), m => m.Contains("超时不能小于 0"));
        Assert.Contains(ConfigIssues(new HalconGenericShapeMatchTool("x") { ImagePath = "Input.Image", UseClutter = true, MaxClutter = 1.5 }), m => m.Contains("最大杂乱比例必须在 0 到 1 之间"));
        Assert.Contains(ConfigIssues(new HalconGenericShapeMatchTool("x") { ImagePath = "Input.Image", AngleStart = 1, AngleEnd = 0 }), m => m.Contains("角度范围终点不能小于起点"));
    }

    [Fact]
    public void 新工具登记_固定ID_工具箱_枚举按数字保存()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "generic-shape-match");
        Assert.Equal("01 定位匹配", item.Category);
        Assert.Equal("通用形状匹配", item.DisplayName);
        ToolNode node = Assert.IsType<ToolNode>(item.Factory());
        var created = Assert.IsType<HalconGenericShapeMatchTool>(node.Tool);
        Assert.Equal("Input.Image", created.ImagePath);
        Assert.Equal(ScaleMode.None, created.ScaleMode);
        created.ScaleMode = ScaleMode.Anisotropic;
        string json = FlowSerializer.SaveNode(node);
        Assert.Contains("\"ToolId\": \"generic-shape-match\"", json);
        Assert.Contains("\"ScaleMode\": 2", json);
        Assert.Equal(ScaleMode.Anisotropic, ((HalconGenericShapeMatchTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool).ScaleMode);
    }

    [Fact]
    public void 未创建模板时运行失败_说明需要示教()
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        using HObject image = SceneImage(false);
        var tool = new HalconGenericShapeMatchTool("通用1") { ImagePath = "Input.Image" };
        using FlowContext ctx = ImageContext(image);
        NodeResult result = tool.Run(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains("通用形状模板未创建", result.Message);
    }
}
