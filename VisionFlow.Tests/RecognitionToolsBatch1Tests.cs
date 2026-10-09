using System.Text.Json.Nodes;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;
using Xunit;
using ZXing;
using ZXing.Common;

namespace VisionFlow.Tests;

/// <summary>
/// RC-01 读码：一维码工具扩展为读码（一维码 / 二维码）。
/// 合成码图用 ZXing.Net 编码 + 手工铺像素生成（仅测试用途）；标注 Requires=HALCON 的用例直接调用 HALCON 算子。
/// 探测结论（RECOGNITION-TOOLS-PLAN.md 第 12 节 A 段）：PDF417 需约 900×300 大图；
/// MaxCodes=0 时 find 不传 stop_after_result_num；质量等级取质量元组第 1 项（0~4，'N/A' 为 NaN）。
/// </summary>
public class RecognitionToolsBatch1Tests
{
    // ======================= 合成码图 =======================

    private static (byte[] Pixels, int Width, int Height) Render(Writer writer, BarcodeFormat format,
        string content, int width, int height, int margin = 2)
    {
        BitMatrix matrix = writer.encode(content, format, width, height);
        int w = matrix.Width + margin * 2;
        int h = matrix.Height + margin * 2;
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int mx = x - margin;
                int my = y - margin;
                bool black = mx >= 0 && mx < matrix.Width && my >= 0 && my < matrix.Height && matrix[mx, my];
                pixels[y * w + x] = black ? (byte)0 : (byte)255;
            }
        }
        return (pixels, w, h);
    }

    /// <summary>二维码渲染：留白按模块尺寸放大（约 4 个模块），多码并排时避免静区不足。</summary>
    private static (byte[] Pixels, int Width, int Height) RenderQr(string content, int size = 120) =>
        Render(new ZXing.QrCode.QRCodeWriter(), BarcodeFormat.QR_CODE, content, size, size, margin: Math.Max(10, size / 5));

    private static HObject ToImage(byte[] pixels, int width, int height)
    {
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            HOperatorSet.GenImage1(out HObject image, "byte", width, height, new HTuple(pin.AddrOfPinnedObject().ToInt64()));
            return image;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>多个码图横向排列（间隔与四周留白 40 像素），用于一次读取多个码。</summary>
    private static HObject ComposeRow(params (byte[] Pixels, int Width, int Height)[] patches)
    {
        int height = patches.Max(p => p.Height);
        int width = patches.Sum(p => p.Width) + 40 * (patches.Length + 1);
        var canvas = Enumerable.Repeat((byte)255, width * height).ToArray();
        int x = 40;
        foreach ((byte[] pixels, int pw, int ph) in patches)
        {
            for (int y = 0; y < ph; y++)
            {
                Array.Copy(pixels, y * pw, canvas, y * width + x, pw);
            }
            x += pw + 40;
        }
        return ToImage(canvas, width, height);
    }

    private static HObject BlankImage(int width = 200, int height = 100)
    {
        return ToImage(Enumerable.Repeat((byte)255, width * height).ToArray(), width, height);
    }

    // ======================= 运行 helper =======================

    private static FlowContext ContextWith(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static Barcode1DTool Barcode(string module = "读码1") =>
        new Barcode1DTool(module) { ImagePath = "图像1.Image" };

    private static Barcode1DTool DataCode2d(string dataCodeType, string module = "读码1") =>
        new Barcode1DTool(module) { ImagePath = "图像1.Image", CodeKind = CodeKind.DataCode2D, DataCodeType = dataCodeType };

    private static FlowContext RunOk(Barcode1DTool tool, HObject image)
    {
        FlowContext ctx = ContextWith(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ctx;
    }

    private static int RegionArea(FlowContext ctx, string module)
    {
        HObject region = ((HalconRegion)ctx.GetVariable(module, "Region").Value).Object;
        HOperatorSet.AreaCenter(region, out HTuple area, out _, out _);
        return area.I;
    }

    private static int ContourCount(FlowContext ctx, string module)
    {
        HObject contours = ((HalconXld)ctx.GetVariable(module, "SymbolContours").Value).Object;
        HOperatorSet.CountObj(contours, out HTuple count);
        return count.I;
    }

    // ======================= 一维码（HALCON） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 一维码_Code128_解码正确_输出与旧版语义一致()
    {
        (byte[] pixels, int w, int h) = Render(new ZXing.OneD.Code128Writer(), BarcodeFormat.CODE_128, "SN-12345", 300, 100, margin: 4);
        HObject image = ToImage(pixels, w, h);
        Barcode1DTool tool = Barcode();

        FlowContext ctx = RunOk(tool, image);

        Assert.Equal(new[] { "SN-12345" }, ctx.GetVariable("读码1", "Codes").GetValue<string[]>());
        Assert.Equal("SN-12345", ctx.GetVariable("读码1", "FirstCode").Value);
        Assert.Equal(1, ctx.GetVariable("读码1", "Count").Value);
        Assert.Equal(true, ctx.GetVariable("读码1", "Found").Value);
        Assert.True(RegionArea(ctx, "读码1") > 0);
        // 新输出：实际码制；未启用质量评级时 Grades 为空、FirstGrade 为 NaN；一维码无符号轮廓
        Assert.Equal(new[] { "Code 128" }, ctx.GetVariable("读码1", "CodeTypes").GetValue<string[]>());
        Assert.Empty(ctx.GetVariable("读码1", "Grades").GetValue<double[]>());
        Assert.True(double.IsNaN((double)ctx.GetVariable("读码1", "FirstGrade").Value));
        Assert.Equal(0, ContourCount(ctx, "读码1"));
        Assert.Contains(ctx.StructuredLogs, l => l.Message.StartsWith("[读码] 一维码 识别数量=1"));
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(true)]
    [InlineData(false)]
    public void 一维码_空图_未找到策略两态(bool failWhenNotFound)
    {
        Barcode1DTool tool = Barcode();
        tool.FailWhenNotFound = failWhenNotFound;
        FlowContext ctx = ContextWith(BlankImage());

        NodeResult result = tool.Run(ctx);

        if (failWhenNotFound)
        {
            Assert.False(result.IsSuccess);
            Assert.Equal("未识别到一维码", result.Message);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(false, ctx.GetVariable("读码1", "Found").Value);
            Assert.Equal(0, ctx.GetVariable("读码1", "Count").Value);
            Assert.Equal(string.Empty, ctx.GetVariable("读码1", "FirstCode").Value);
            Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("未识别到一维码（已设置未找到时继续：Found=false）"));
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 一维码_MaxCodes_截断读取数量()
    {
        // Code128 单行条码里有两个独立条码的图难以稳定合成，用一张图放两个码分居两行验证截断
        (byte[] top, int tw, int th) = Render(new ZXing.OneD.Code128Writer(), BarcodeFormat.CODE_128, "CODE-A", 300, 60, margin: 4);
        (byte[] bottom, int bw, int bh) = Render(new ZXing.OneD.Code128Writer(), BarcodeFormat.CODE_128, "CODE-B", 300, 60, margin: 4);
        int width = Math.Max(tw, bw);
        var canvas = Enumerable.Repeat((byte)255, width * (th + bh + 40)).ToArray();
        for (int y = 0; y < th; y++) Array.Copy(top, y * tw, canvas, y * width, tw);
        for (int y = 0; y < bh; y++) Array.Copy(bottom, y * bw, canvas, (th + 40 + y) * width, bw);
        HObject image = ToImage(canvas, width, th + bh + 40);

        Barcode1DTool tool = Barcode();
        FlowContext all = RunOk(tool, image);
        Assert.Equal(2, all.GetVariable("读码1", "Count").Value);

        tool.MaxCodes = 1;
        FlowContext limited = RunOk(tool, image);
        Assert.Equal(1, limited.GetVariable("读码1", "Count").Value);
    }

    // ======================= 二维码（HALCON） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData("QR Code", "QR-RC01-42", 200, 200)]
    [InlineData("Data Matrix ECC 200", "DM-RC01-7", 200, 200)]
    [InlineData("PDF417", "PDF417-RC01-LONG-CONTENT-1234567890", 900, 300)]
    public void 二维码_三种码制_解码正确_区域与符号轮廓有效(string dataCodeType, string content, int width, int height)
    {
        (Writer writer, BarcodeFormat format) = dataCodeType switch
        {
            "QR Code" => ((Writer)new ZXing.QrCode.QRCodeWriter(), BarcodeFormat.QR_CODE),
            "Data Matrix ECC 200" => (new ZXing.Datamatrix.DataMatrixWriter(), BarcodeFormat.DATA_MATRIX),
            _ => (new ZXing.PDF417.PDF417Writer(), BarcodeFormat.PDF_417)
        };
        (byte[] pixels, int w, int h) = Render(writer, format, content, width, height, margin: 10);
        HObject image = ToImage(pixels, w, h);
        Barcode1DTool tool = DataCode2d(dataCodeType);

        try
        {
            FlowContext ctx = RunOk(tool, image);

            Assert.Equal(new[] { content }, ctx.GetVariable("读码1", "Codes").GetValue<string[]>());
            Assert.Equal(content, ctx.GetVariable("读码1", "FirstCode").Value);
            Assert.Equal(1, ctx.GetVariable("读码1", "Count").Value);
            Assert.Equal(true, ctx.GetVariable("读码1", "Found").Value);
            Assert.True(RegionArea(ctx, "读码1") > 0, "二维码 Region 面积应大于 0");
            Assert.Equal(1, ContourCount(ctx, "读码1"));
            // get_data_code_2d_results 无 'symbology' 查询（22.11 探测 #8831），回退为模型 symbol_type
            Assert.Equal(new[] { dataCodeType }, ctx.GetVariable("读码1", "CodeTypes").GetValue<string[]>());
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_MaxCodes_三码图_2得2个_0得3个()
    {
        HObject image = ComposeRow(RenderQr("QR-MAX-1"), RenderQr("QR-MAX-2"), RenderQr("QR-MAX-3"));
        Barcode1DTool tool = DataCode2d("QR Code");

        try
        {
            tool.MaxCodes = 2;
            FlowContext limited = RunOk(tool, image);
            Assert.Equal(2, limited.GetVariable("读码1", "Count").Value);

            tool.MaxCodes = 0;
            FlowContext all = RunOk(tool, image);
            Assert.Equal(3, all.GetVariable("读码1", "Count").Value);
            Assert.Equal(3, all.GetVariable("读码1", "Codes").GetValue<string[]>().Length);
            Assert.Equal(3, ContourCount(all, "读码1"));
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(CodeKind.Barcode)]
    [InlineData(CodeKind.DataCode2D)]
    public void 质量评级_Grades数值在0到4之间或NaN_长度等于码数(CodeKind kind)
    {
        HObject image;
        Barcode1DTool tool;
        if (kind == CodeKind.Barcode)
        {
            (byte[] pixels, int w, int h) = Render(new ZXing.OneD.Code128Writer(), BarcodeFormat.CODE_128, "GRADE-77", 300, 100, margin: 4);
            image = ToImage(pixels, w, h);
            tool = Barcode();
        }
        else
        {
            image = ComposeRow(RenderQr("GRADE-2D-1"), RenderQr("GRADE-2D-2"));
            tool = DataCode2d("QR Code");
        }
        tool.GradeQuality = true;

        try
        {
            FlowContext ctx = RunOk(tool, image);
            int count = (int)ctx.GetVariable("读码1", "Count").Value;
            double[] grades = ctx.GetVariable("读码1", "Grades").GetValue<double[]>();
            Assert.Equal(count, grades.Length);
            Assert.All(grades, g => Assert.True(double.IsNaN(g) || (g >= 0 && g <= 4), $"等级 {g} 应在 0~4 之间或为 NaN"));
            double first = (double)ctx.GetVariable("读码1", "FirstGrade").Value;
            Assert.True(double.IsNaN(first) || (first >= 0 && first <= 4));
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_训练保存与重载_结果与未训练一致()
    {
        HObject trainImage = ComposeRow(RenderQr("TRAIN-1"), RenderQr("TRAIN-2"));
        HObject runImage = ComposeRow(RenderQr("RUN-1"), RenderQr("RUN-2"));
        var trainTool = new Barcode1DTool("读码T") { ImagePath = "图像1.Image", CodeKind = CodeKind.DataCode2D, DataCodeType = "QR Code" };
        Barcode1DTool untrained = DataCode2d("QR Code", "读码B");

        try
        {
            trainTool.TrainDataCodeModel(null, trainImage);
            byte[] data = trainTool.DataCodeModelData;
            Assert.NotNull(data);
            Assert.True(data.Length > 0, "训练后的模型应有序列化数据");

            var trained = new Barcode1DTool("读码A") { ImagePath = "图像1.Image", CodeKind = CodeKind.DataCode2D, DataCodeType = "QR Code", DataCodeModelData = data };
            try
            {
                FlowContext expected = RunOk(untrained, runImage);
                FlowContext actual = RunOk(trained, runImage);
                Assert.Equal(expected.GetVariable("读码B", "Codes").GetValue<string[]>(),
                    actual.GetVariable("读码A", "Codes").GetValue<string[]>());
                Assert.Equal(2, actual.GetVariable("读码A", "Count").Value);
            }
            finally
            {
                trained.ReleaseResources();
            }
        }
        finally
        {
            trainTool.ReleaseResources();
            untrained.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_模型按实例缓存_预热与释放幂等()
    {
        HObject image = ComposeRow(RenderQr("CACHE-1"), RenderQr("CACHE-2"));
        Barcode1DTool tool = DataCode2d("QR Code");

        try
        {
            tool.Prepare();
            tool.Prepare();
            FlowContext ctx = RunOk(tool, image);
            Assert.Equal(2, ctx.GetVariable("读码1", "Count").Value);
            // 缓存复用下再次运行结果一致；码制变化触发重建
            FlowContext again = RunOk(tool, image);
            Assert.Equal(2, again.GetVariable("读码1", "Count").Value);
            tool.DataCodeType = "Data Matrix ECC 200";
            tool.ReleaseResources();
            tool.ReleaseResources();
            tool.Prepare();
            tool.ReleaseResources();
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    // ======================= 校验、显隐、序列化、登记（非 HALCON） =======================

    [Fact]
    public void 读码参数校验_流程校验与运行措辞一致()
    {
        Barcode1DTool negative = DataCode2d("QR Code");
        negative.MaxCodes = -1;
        ToolConfigurationIssue issue = Assert.Single(negative.CheckConfiguration());
        Assert.Equal((nameof(Barcode1DTool.MaxCodes), "读取数量不能小于 0（0 表示读取全部）"), (issue.Parameter, issue.Message));
        NodeResult result = negative.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"读码1 {nameof(Barcode1DTool.MaxCodes)}：读取数量不能小于 0（0 表示读取全部）", result.Message);

        Barcode1DTool badLevel = DataCode2d("QR Code");
        badLevel.RecognitionLevel = "ultra_recognition";
        ToolConfigurationIssue levelIssue = Assert.Single(badLevel.CheckConfiguration());
        Assert.Equal(nameof(Barcode1DTool.RecognitionLevel), levelIssue.Parameter);
        Assert.Equal("识别强度必须是 standard_recognition / enhanced_recognition / maximum_recognition", levelIssue.Message);

        Barcode1DTool emptyType = DataCode2d("  ");
        ToolConfigurationIssue typeIssue = Assert.Single(emptyType.CheckConfiguration());
        Assert.Equal((nameof(Barcode1DTool.DataCodeType), "二维码码制不能为空"), (typeIssue.Parameter, typeIssue.Message));

        // 一维码方式不校验二维码参数
        Barcode1DTool barcode = Barcode();
        barcode.MaxCodes = 3;
        Assert.Empty(barcode.CheckConfiguration());
    }

    [Fact]
    public void 读码参数按方式显隐()
    {
        Barcode1DTool barcode = Barcode();
        Assert.True(barcode.IsParameterVisible(nameof(Barcode1DTool.CodeType)));
        Assert.False(barcode.IsParameterVisible(nameof(Barcode1DTool.DataCodeType)));
        Assert.False(barcode.IsParameterVisible(nameof(Barcode1DTool.RecognitionLevel)));
        Assert.False(barcode.IsParameterVisible(nameof(Barcode1DTool.DataCodeModelData)));
        Assert.True(barcode.IsParameterVisible(nameof(Barcode1DTool.MaxCodes)));
        Assert.True(barcode.IsParameterVisible(nameof(Barcode1DTool.GradeQuality)));

        Barcode1DTool code2d = DataCode2d("QR Code");
        Assert.False(code2d.IsParameterVisible(nameof(Barcode1DTool.CodeType)));
        Assert.True(code2d.IsParameterVisible(nameof(Barcode1DTool.DataCodeType)));
        Assert.True(code2d.IsParameterVisible(nameof(Barcode1DTool.RecognitionLevel)));
        Assert.True(code2d.IsParameterVisible(nameof(Barcode1DTool.DataCodeModelData)));
    }

    [Fact]
    public void CodeKind枚举追加在末尾_默认值等于现有行为()
    {
        Assert.Equal(new[] { "Barcode", "DataCode2D" }, Enum.GetNames<CodeKind>());
        Assert.Equal(0, (int)CodeKind.Barcode);
        Barcode1DTool tool = Barcode();
        Assert.Equal(CodeKind.Barcode, tool.CodeKind);
        Assert.Equal("auto", tool.CodeType);
        Assert.Equal("QR Code", tool.DataCodeType);
        Assert.Equal("standard_recognition", tool.RecognitionLevel);
        Assert.Equal(0, tool.MaxCodes);
        Assert.False(tool.GradeQuality);
    }

    [Fact]
    public void 读码序列化往返_JSON含barcode1d_枚举按数字保存()
    {
        var tool = new Barcode1DTool("读码1")
        {
            ImagePath = "Input.Image",
            CodeKind = CodeKind.DataCode2D,
            DataCodeType = "Data Matrix ECC 200",
            RecognitionLevel = "enhanced_recognition",
            MaxCodes = 5,
            GradeQuality = true
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"barcode1d\"", json);
        Assert.Contains("\"CodeKind\": 1", json);
        Assert.Contains("\"DataCodeType\": \"Data Matrix ECC 200\"", json);
        Assert.Contains("\"MaxCodes\": 5", json);

        var loaded = (Barcode1DTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((CodeKind.DataCode2D, "Data Matrix ECC 200", "enhanced_recognition", 5, true),
            (loaded.CodeKind, loaded.DataCodeType, loaded.RecognitionLevel, loaded.MaxCodes, loaded.GradeQuality));
        Assert.Equal("auto", loaded.CodeType);
        Assert.True(loaded.FailWhenNotFound);

        // 历史文件缺省新属性：一维码行为完全不变
        JsonObject legacy = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new Barcode1DTool("读码1") { CodeType = "EAN-13" })))!.AsObject();
        JsonObject properties = legacy["Tool"]!["Properties"]!.AsObject();
        foreach (string name in new[] { "CodeKind", "DataCodeType", "RecognitionLevel", "MaxCodes", "GradeQuality", "DataCodeModelData" })
        {
            Assert.True(properties.Remove(name), name);
        }
        var old = (Barcode1DTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy.ToJsonString())).Tool;
        Assert.Equal((CodeKind.Barcode, "EAN-13", "QR Code", "standard_recognition", 0, false),
            (old.CodeKind, old.CodeType, old.DataCodeType, old.RecognitionLevel, old.MaxCodes, old.GradeQuality));
        Assert.Null(old.DataCodeModelData);
    }

    [Fact]
    public void 读码输出不与参数同名()
    {
        var parameters = typeof(Barcode1DTool).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain(ToolMetadata.GetOutputs(typeof(Barcode1DTool)), o => parameters.Contains(o.Name));
        Assert.Equal(new[] { "Codes", "FirstCode", "Region", "Count", "Found", "CodeTypes", "SymbolContours", "Grades", "FirstGrade" },
            ToolMetadata.GetOutputs(typeof(Barcode1DTool)).Select(o => o.Name).ToArray());
    }

    [Fact]
    public void 工具箱_读码改名_ID与类型名不变()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "barcode1d");
        Assert.Equal(("06 识别工具", "读码"), (item.Category, item.DisplayName));
        var created = Assert.IsType<Barcode1DTool>(Assert.IsType<ToolNode>(item.Factory()).Tool);
        Assert.StartsWith("读码", created.ModuleName);
        Assert.Equal(CodeKind.Barcode, created.CodeKind);
    }
}
