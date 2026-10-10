using System.Runtime.InteropServices;
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
/// 读码高级参数（ModelParams）与多图训练（Barcode1DTool 扩展）。
/// 合成码图用 ZXing.Net 渲染（仅测试用途）；标注 Requires=HALCON 的用例直接调用 HALCON 算子。
/// 探测结论（RECOGNITION-TOOLS-PLAN.md 第 14 节）：2D 高级参数经 set_data_code_2d_param 在建模/反序列化后应用，
/// 参数名非法报 #8831、值非法报 #8835；1D 经 set_bar_code_param，数值参数必须传数值（字符串报 #1203）；
/// find_data_code_2d 'train'='all' 在同一句柄上逐张累积，多图训练只序列化一次。
/// </summary>
public class RecognitionToolsBatch4Tests
{
    // ======================= 合成码图与运行 helper（同 Batch1 约定） =======================

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

    private static (byte[] Pixels, int Width, int Height) RenderQr(string content, int size = 120) =>
        Render(new ZXing.QrCode.QRCodeWriter(), BarcodeFormat.QR_CODE, content, size, size, margin: Math.Max(10, size / 5));

    private static HObject ToImage(byte[] pixels, int width, int height)
    {
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
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

    private static int LoadLogCount(FlowContext ctx) =>
        ctx.StructuredLogs.Count(l => l.Message.Contains("二维码模型加载耗时"));

    // ======================= 高级参数（HALCON） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_高级参数_polarity与strict_model_解码与默认一致()
    {
        const string content = "PARAM-QR-1";
        (byte[] pixels, int w, int h) = RenderQr(content);
        HObject image = ToImage(pixels, w, h);
        Barcode1DTool tool = DataCode2d("QR Code");
        tool.ModelParams = "# 注释行与空行被跳过\n\npolarity=dark_on_light\nstrict_model=yes\ntimeout=2000";

        try
        {
            FlowContext actual = RunOk(tool, image);
            Assert.Equal(new[] { content }, actual.GetVariable("读码1", "Codes").GetValue<string[]>());

            Barcode1DTool baseline = DataCode2d("QR Code", "读码B");
            try
            {
                FlowContext expected = RunOk(baseline, image);
                Assert.Equal(expected.GetVariable("读码B", "Codes").GetValue<string[]>(),
                    actual.GetVariable("读码1", "Codes").GetValue<string[]>());
            }
            finally
            {
                baseline.ReleaseResources();
            }
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_高级参数_非法参数名_中文错误含参数名与8831()
    {
        (byte[] p, int w, int h) = RenderQr("PARAM-QR-2");
        HObject image = ToImage(p, w, h);
        Barcode1DTool tool = DataCode2d("QR Code");
        tool.ModelParams = "no_such=1";

        try
        {
            NodeResult result = tool.Run(ContextWith(image));
            Assert.False(result.IsSuccess);
            Assert.Contains("高级参数第 1 行", result.Message);
            Assert.Contains("no_such=1", result.Message);
            Assert.Contains("#8831", result.Message);
            Assert.Contains("不支持此参数名", result.Message);
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_高级参数_非法值_中文错误含8835()
    {
        (byte[] p, int w, int h) = RenderQr("PARAM-QR-3");
        HObject image = ToImage(p, w, h);
        Barcode1DTool tool = DataCode2d("QR Code");
        tool.ModelParams = "timeout=2000\nmodule_size=small";

        try
        {
            NodeResult result = tool.Run(ContextWith(image));
            Assert.False(result.IsSuccess);
            Assert.Contains("高级参数第 2 行", result.Message);
            Assert.Contains("module_size=small", result.Message);
            Assert.Contains("#8835", result.Message);
            Assert.Contains("参数值非法", result.Message);
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 一维码_高级参数_数值与字符串参数可运行()
    {
        (byte[] pixels, int w, int h) = Render(new ZXing.OneD.Code128Writer(), BarcodeFormat.CODE_128, "PARAM-1D-1", 300, 100, margin: 4);
        HObject image = ToImage(pixels, w, h);
        Barcode1DTool tool = Barcode();
        // 数值参数自动按数值传入（传字符串报 #1203，见探测结论）
        tool.ModelParams = "element_size_min=1.5\norientation=0";

        FlowContext ctx = RunOk(tool, image);

        Assert.Equal(new[] { "PARAM-1D-1" }, ctx.GetVariable("读码1", "Codes").GetValue<string[]>());
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_高级参数变化_触发模型重建且训练数据保持可解码()
    {
        (byte[] p, int w, int h) = RenderQr("REBUILD-1");
        HObject image = ToImage(p, w, h);
        Barcode1DTool tool = DataCode2d("QR Code");
        tool.ModelParams = "timeout=2000";

        try
        {
            FlowContext first = RunOk(tool, image);
            Assert.Equal(1, LoadLogCount(first));
            // 缓存键不变时复用，不再加载
            FlowContext second = RunOk(tool, image);
            Assert.Equal(0, LoadLogCount(second));

            tool.ModelParams = "timeout=3000";
            FlowContext rebuilt = RunOk(tool, image);
            Assert.Equal(1, LoadLogCount(rebuilt));
            Assert.Equal(new[] { "REBUILD-1" }, rebuilt.GetVariable("读码1", "Codes").GetValue<string[]>());

            // 训练数据 + 高级参数组合：清空参数后重建，训练数据仍生效可解码
            tool.TrainDataCodeModel(null, image);
            Assert.NotNull(tool.DataCodeModelData);
            tool.ModelParams = null;
            FlowContext trained = RunOk(tool, image);
            Assert.Equal(1, LoadLogCount(trained));
            Assert.Equal(new[] { "REBUILD-1" }, trained.GetVariable("读码1", "Codes").GetValue<string[]>());
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    // ======================= 多图训练（HALCON） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_多图训练_一次性训练_数据非空_新实例可解码()
    {
        (byte[] p1, int w1, int h1) = RenderQr("MULTI-A");
        (byte[] p2, int w2, int h2) = RenderQr("MULTI-B-DIFF");
        HObject image1 = ToImage(p1, w1, h1);
        HObject image2 = ToImage(p2, w2, h2);
        var once = new Barcode1DTool("读码A") { ImagePath = "图像1.Image", CodeKind = CodeKind.DataCode2D, DataCodeType = "QR Code" };

        try
        {
            FlowContext trainCtx = ContextWith(image1);
            once.TrainDataCodeModel(trainCtx, new[] { image1, image2 });
            byte[] both = once.DataCodeModelData;
            Assert.NotNull(both);
            Assert.True(both.Length > 0);
            Assert.Contains(trainCtx.StructuredLogs, l => l.Message.Contains("已用 2 张图像训练"));

            // 新实例加载多图训练数据后可解码（两张图各自可识别）
            var loaded = new Barcode1DTool("读码C") { ImagePath = "图像1.Image", CodeKind = CodeKind.DataCode2D, DataCodeType = "QR Code", DataCodeModelData = both };
            try
            {
                FlowContext ctx1 = RunOk(loaded, image1);
                Assert.Equal(new[] { "MULTI-A" }, ctx1.GetVariable("读码C", "Codes").GetValue<string[]>());
                FlowContext ctx2 = RunOk(loaded, image2);
                Assert.Equal(new[] { "MULTI-B-DIFF" }, ctx2.GetVariable("读码C", "Codes").GetValue<string[]>());
            }
            finally
            {
                loaded.ReleaseResources();
            }
        }
        finally
        {
            once.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 二维码_多图训练_分批累积_训练数据随样本变化()
    {
        // 不同模块尺寸（100 / 300）的训练样本使模型适应不同参数：序列化内容随之变化
        // （同尺寸样本训练值收敛一致，序列化恒 178 字节且内容相同，见计划文档第 14 节探测）
        (byte[] ps, int ws, int hs) = RenderQr("SIZE-A", 100);
        (byte[] pl, int wl, int hl) = RenderQr("SIZE-B", 300);
        HObject small = ToImage(ps, ws, hs);
        HObject large = ToImage(pl, wl, hl);
        var single = new Barcode1DTool("读码A") { CodeKind = CodeKind.DataCode2D, DataCodeType = "QR Code" };
        var stepwise = new Barcode1DTool("读码B") { CodeKind = CodeKind.DataCode2D, DataCodeType = "QR Code" };

        try
        {
            single.TrainDataCodeModel(null, small);
            byte[] one = single.DataCodeModelData;
            Assert.NotNull(one);
            Assert.True(one.Length > 0);

            stepwise.TrainDataCodeModel(null, small);
            stepwise.TrainDataCodeModel(null, new[] { large });
            byte[] cumulative = stepwise.DataCodeModelData;
            Assert.NotNull(cumulative);
            Assert.NotEqual(one, cumulative);
        }
        finally
        {
            single.ReleaseResources();
            stepwise.ReleaseResources();
        }
    }

    // ======================= 校验、默认值、序列化（非 HALCON） =======================

    [Fact]
    public void 高级参数格式校验_缺等号与空名称_行号与内容()
    {
        Barcode1DTool missingEq = DataCode2d("QR Code");
        missingEq.ModelParams = "timeout=2000\npolarity_dark_on_light";
        ToolConfigurationIssue eqIssue = Assert.Single(missingEq.CheckConfiguration());
        Assert.Equal(nameof(Barcode1DTool.ModelParams), eqIssue.Parameter);
        Assert.Equal("高级参数第 2 行「polarity_dark_on_light」缺少等号：应为 名称=值", eqIssue.Message);
        NodeResult eqResult = missingEq.Run(new FlowContext());
        Assert.False(eqResult.IsSuccess);
        Assert.Contains("缺少等号", eqResult.Message);

        Barcode1DTool emptyName = DataCode2d("QR Code");
        emptyName.ModelParams = "=2000";
        ToolConfigurationIssue nameIssue = Assert.Single(emptyName.CheckConfiguration());
        Assert.Equal("高级参数第 1 行「=2000」名称为空：应为 名称=值", nameIssue.Message);

        // 注释行、空行合法；可空不报错
        Barcode1DTool commentOnly = DataCode2d("QR Code");
        commentOnly.ModelParams = "# 只含注释\n\n   \n";
        Assert.Empty(commentOnly.CheckConfiguration());
        Barcode1DTool tool = Barcode();
        Assert.Null(tool.ModelParams);
        Assert.Empty(tool.CheckConfiguration());
    }

    [Fact]
    public void 高级参数序列化往返_保留值_历史文件缺省为null()
    {
        var tool = new Barcode1DTool("读码1")
        {
            ImagePath = "Input.Image",
            CodeKind = CodeKind.DataCode2D,
            ModelParams = "polarity=dark_on_light\ntimeout=2000"
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ModelParams\": \"polarity=dark_on_light\\ntimeout=2000\"", json);

        var loaded = (Barcode1DTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal("polarity=dark_on_light\ntimeout=2000", loaded.ModelParams);

        // 历史文件缺省新属性：默认空，行为不变
        JsonObject legacy = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(new Barcode1DTool("读码1") { CodeType = "EAN-13" })))!.AsObject();
        JsonObject properties = legacy["Tool"]!["Properties"]!.AsObject();
        Assert.True(properties.Remove(nameof(Barcode1DTool.ModelParams)));
        Barcode1DTool old = (Barcode1DTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy.ToJsonString())).Tool;
        Assert.Null(old.ModelParams);
    }
}
