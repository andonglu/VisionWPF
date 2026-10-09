using System.Text.Json.Nodes;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;
using Xunit;

// System.Drawing 渲染仅用于本测试在 Windows 上合成文字图
#pragma warning disable CA1416

namespace VisionFlow.Tests;

/// <summary>
/// RC-02 字符识别：传统文本模型（TextModel）与 Deep OCR 两引擎。
/// 合成文字图用 System.Drawing 渲染（白底黑字 Arial 约 30px，仅测试用途）；标注 Requires=HALCON 的用例直接调用 HALCON 算子。
/// 探测结论（RECOGNITION-TOOLS-PLAN.md 第 12 节 B 段）：get_text_result 的 'text' 查询不可用（行文本由 class_line 拼接）；
/// reduce_domain 后字符区域为原图坐标；Deep OCR 无 clear_deep_ocr（释放仅丢引用）；22.11 set_deep_ocr_param 设备参数不可用。
/// 探测复测（实现期）：Deep OCR 的逐字符置信度从 words.char_candidates[词][0][字符].confidence 第 1 项取得；
/// 22.11 上 set_deep_ocr_param 对 confidence/alphabet 等参数同样报 #1302，ConfidenceThreshold / Alphabet 为结果侧过滤。
/// </summary>
public class RecognitionToolsBatch2Tests
{
    // ======================= 合成文字图 =======================

    private static (byte[] Pixels, int Width, int Height) Render(string[] lines, int fontPx = 30, int pad = 20, int lineGap = 20)
    {
        using var font = new System.Drawing.Font("Arial", fontPx, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        var size = new System.Drawing.Size(1, 1);
        using (var tmp = new System.Drawing.Bitmap(1, 1))
        using (var g = System.Drawing.Graphics.FromImage(tmp))
        {
            foreach (string line in lines)
            {
                System.Drawing.SizeF s = g.MeasureString(line, font);
                size.Width = Math.Max(size.Width, (int)Math.Ceiling(s.Width));
                size.Height += (int)Math.Ceiling(s.Height);
            }
        }
        int w = size.Width + pad * 2;
        int h = size.Height + pad * 2 + lineGap * (lines.Length - 1);
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            float y = pad;
            foreach (string line in lines)
            {
                g.DrawString(line, font, System.Drawing.Brushes.Black, pad, y);
                y += fontPx + lineGap;
            }
        }
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        System.Drawing.Imaging.BitmapData data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var bytes = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bytes[y * w + x] = System.Runtime.InteropServices.Marshal.ReadByte(data.Scan0 + y * data.Stride + x * 3);
            }
        }
        bmp.UnlockBits(data);
        return (bytes, w, h);
    }

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

    private static HObject RenderImage(params string[] lines)
    {
        (byte[] pixels, int w, int h) = Render(lines);
        return ToImage(pixels, w, h);
    }

    private static HObject BlankImage(int width = 200, int height = 100)
    {
        return ToImage(Enumerable.Repeat((byte)255, width * height).ToArray(), width, height);
    }

    /// <summary>把 patch 铺到白色画布 (offsetRow, offsetCol) 处，模拟“当前位置”图像。</summary>
    private static HObject PlaceOnCanvas((byte[] Pixels, int Width, int Height) patch, int canvasH, int canvasW, int offsetRow, int offsetCol)
    {
        var canvas = Enumerable.Repeat((byte)255, canvasW * canvasH).ToArray();
        for (int y = 0; y < patch.Height; y++)
        {
            Array.Copy(patch.Pixels, y * patch.Width, canvas, (offsetRow + y) * canvasW + offsetCol, patch.Width);
        }
        return ToImage(canvas, canvasW, canvasH);
    }

    private static HObject RectangleRegion(int row1, int col1, int row2, int col2)
    {
        HOperatorSet.GenRectangle1(out HObject region, row1, col1, row2, col2);
        return region;
    }

    // ======================= 运行 helper =======================

    private static FlowContext ContextWith(HObject image)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Object("图像1", "Image", new HalconImage(image), 1));
        return ctx;
    }

    private static OcrTool Ocr(string module = "字符识别1") =>
        new OcrTool(module) { ImagePath = "图像1.Image" };

    private static FlowContext RunOk(OcrTool tool, HObject image)
    {
        FlowContext ctx = ContextWith(image);
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ctx;
    }

    private static string TextOf(FlowContext ctx, string module = "字符识别1") =>
        (string)ctx.GetVariable(module, "Text").Value;

    private static int RegionCount(FlowContext ctx, string output, string module = "字符识别1")
    {
        HObject regions = ((HalconRegion)ctx.GetVariable(module, output).Value).Object;
        HOperatorSet.CountObj(regions, out HTuple count);
        return count.I;
    }

    private static int ContourCount(FlowContext ctx, string output, string module = "字符识别1")
    {
        HObject contours = ((HalconXld)ctx.GetVariable(module, output).Value).Object;
        HOperatorSet.CountObj(contours, out HTuple count);
        return count.I;
    }

    // ======================= TextModel（HALCON） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 文本模型_纯数字_识别结果与真值一致_输出合法()
    {
        // 注：探测中 "2026-10-06" 的连字符曾被误识，选用例以实测稳定为准——纯数字 "20261006" 探测已确认逐字符正确
        HObject image = RenderImage("20261006");
        OcrTool tool = Ocr();

        FlowContext ctx = RunOk(tool, image);

        Assert.Equal("20261006", TextOf(ctx));
        Assert.Equal(new[] { "20261006" }, ctx.GetVariable("字符识别1", "Lines").GetValue<string[]>());
        Assert.Equal(1, ctx.GetVariable("字符识别1", "LineCount").Value);
        Assert.Equal(new[] { "2", "0", "2", "6", "1", "0", "0", "6" }, ctx.GetVariable("字符识别1", "Chars").GetValue<string[]>());
        double[] confidences = ctx.GetVariable("字符识别1", "Confidences").GetValue<double[]>();
        Assert.Equal(8, confidences.Length);
        Assert.All(confidences, c => Assert.True(c >= 0 && c <= 1, $"置信度 {c} 应在 0~1 之间"));
        double minConfidence = (double)ctx.GetVariable("字符识别1", "MinConfidence").Value;
        Assert.Equal(confidences.Min(), minConfidence);
        Assert.True(RegionCount(ctx, "CharRegions") == 8, "每字符一个区域");
        Assert.Equal(0, ContourCount(ctx, "WordContours")); // TextModel 无文字框轮廓
        Assert.Equal(true, ctx.GetVariable("字符识别1", "PatternOk").Value); // 无期望格式时恒 true
        Assert.Equal(true, ctx.GetVariable("字符识别1", "Found").Value);
        Assert.Contains(ctx.StructuredLogs, l => l.Message.StartsWith("[字符识别] TextModel 识别字符=8 行数=1"));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 文本模型_多行文字_行序与行内顺序正确()
    {
        HObject image = RenderImage("ABC123", "XYZ789");
        OcrTool tool = Ocr();

        FlowContext ctx = RunOk(tool, image);

        Assert.Equal(new[] { "ABC123", "XYZ789" }, ctx.GetVariable("字符识别1", "Lines").GetValue<string[]>());
        Assert.Equal(2, ctx.GetVariable("字符识别1", "LineCount").Value);
        Assert.Equal("ABC123" + Environment.NewLine + "XYZ789", TextOf(ctx));
        string[] chars = ctx.GetVariable("字符识别1", "Chars").GetValue<string[]>();
        Assert.Equal(12, chars.Length);
        // 行内按列排序、行间按行分组：前 6 个字符的行坐标应小于后 6 个
        HObject regions = ((HalconRegion)ctx.GetVariable("字符识别1", "CharRegions").Value).Object;
        HOperatorSet.AreaCenter(regions, out HTuple _, out HTuple rows, out _);
        Assert.True(rows.DArr.Take(6).Max() < rows.DArr.Skip(6).Min(),
            $"前两行字符应分行：{string.Join(",", rows.DArr.Select(r => r.ToString("F0")))}");
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 文本模型_Alphabet过滤_结果只含字符集内字符_日志注明数量()
    {
        HObject image = RenderImage("20261006");
        OcrTool tool = Ocr();
        tool.Alphabet = "0";

        FlowContext ctx = RunOk(tool, image);

        string[] chars = ctx.GetVariable("字符识别1", "Chars").GetValue<string[]>();
        Assert.NotEmpty(chars);
        Assert.All(chars, c => Assert.Equal("0", c));
        Assert.Equal(string.Concat(chars), TextOf(ctx));
        Assert.Equal(chars.Length, RegionCount(ctx, "CharRegions"));
        Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("过滤") && l.Message.Contains("个字符"));
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 文本模型_ROI限定_结果在ROI内且为原图坐标()
    {
        (byte[] pixels, int w, int h) = Render(new[] { "20261006" });
        // 画布比文字图大一圈，文字铺在 (40, 60) 处；ROI 覆盖文字区域
        HObject image = PlaceOnCanvas((pixels, w, h), h + 100, w + 150, 40, 60);
        OcrTool tool = Ocr();
        tool.RegionPath = "ROI.Region";
        FlowContext roiCtx = ContextWith(image);
        HOperatorSet.GenRectangle1(out HObject roi, 30, 50, 40 + h + 10, 60 + w + 10);
        roiCtx.SetVariable(Variable.Object("ROI", "Region", new HalconRegion(roi), 1));

        NodeResult result = tool.Run(roiCtx);
        Assert.True(result.IsSuccess, result.Message);

        Assert.Equal("20261006", TextOf(roiCtx));
        // 字符区域为原图坐标：行坐标应落在 ROI 行范围 [30, 40+h+10] 内
        HObject regions = ((HalconRegion)roiCtx.GetVariable("字符识别1", "CharRegions").Value).Object;
        HOperatorSet.AreaCenter(regions, out HTuple _, out HTuple rows, out HTuple cols);
        Assert.All(rows.DArr, r => Assert.True(r > 30 && r < 40 + h + 10, $"行坐标 {r} 应在 ROI 内"));
        Assert.All(cols.DArr, c => Assert.True(c > 50 && c < 60 + w + 10, $"列坐标 {c} 应在 ROI 内"));

        // ROI 只覆盖空白区域时按“未找到”处理
        FlowContext blankRoiCtx = ContextWith(image);
        HOperatorSet.GenRectangle1(out HObject blankRoi, 0, 0, 25, w + 150);
        blankRoiCtx.SetVariable(Variable.Object("ROI", "Region", new HalconRegion(blankRoi), 1));
        NodeResult blankResult = tool.Run(blankRoiCtx);
        Assert.False(blankResult.IsSuccess);
        Assert.Equal("未识别到字符", blankResult.Message);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 文本模型_句柄按实例缓存_预热与释放幂等_参数变化重建()
    {
        HObject image = RenderImage("20261006");
        OcrTool tool = Ocr();

        try
        {
            tool.Prepare();
            tool.Prepare();
            FlowContext ctx = RunOk(tool, image);
            Assert.Equal("20261006", TextOf(ctx));
            FlowContext again = RunOk(tool, image);
            Assert.Equal("20261006", TextOf(again));
            // 分类器/参数变化触发重建；ReleaseResources 幂等
            tool.Polarity = "both";
            tool.ReleaseResources();
            tool.ReleaseResources();
            tool.Prepare();
            FlowContext rebuilt = RunOk(tool, image);
            Assert.Equal("20261006", TextOf(rebuilt));
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    // ======================= Deep OCR（HALCON） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void DeepOcr_auto_识别正确_文字框轮廓非空_置信度可取()
    {
        HObject image = RenderImage("20261006");
        var tool = new OcrTool("字符识别1") { ImagePath = "图像1.Image", Engine = OcrEngine.DeepOcr };

        try
        {
            FlowContext ctx = RunOk(tool, image);

            Assert.Equal("20261006", TextOf(ctx));
            Assert.True(ContourCount(ctx, "WordContours") >= 1, "Deep OCR 文字框轮廓应非空");
            double[] confidences = ctx.GetVariable("字符识别1", "Confidences").GetValue<double[]>();
            Assert.Equal(8, confidences.Length);
            Assert.All(confidences, c => Assert.True(c >= 0 && c <= 1, $"置信度 {c} 应在 0~1 之间"));
            double minConfidence = (double)ctx.GetVariable("字符识别1", "MinConfidence").Value;
            Assert.Equal(confidences.Min(), minConfidence);
            Assert.True(minConfidence > 0.5, $"Deep OCR 清晰印刷体置信度应较高，实际 {minConfidence}");
            // 逐字符区域由词盒均分近似，数量与字符一致
            Assert.Equal(8, RegionCount(ctx, "CharRegions"));
            Assert.Equal(true, ctx.GetVariable("字符识别1", "Found").Value);
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void DeepOcr_设备Gpu_预热抛中文异常_运行同样失败()
    {
        var tool = new OcrTool("字符识别1") { ImagePath = "图像1.Image", Engine = OcrEngine.DeepOcr, Device = "Gpu" };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => tool.Prepare());
        Assert.Contains("GPU", ex.Message);
        Assert.Contains("Cpu", ex.Message);

        NodeResult result = tool.Run(ContextWith(BlankImage()));
        Assert.False(result.IsSuccess);
        Assert.Contains("GPU", result.Message);
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void DeepOcr_置信度阈值过滤_低置信字符被移除()
    {
        HObject image = RenderImage("20261006");
        var tool = new OcrTool("字符识别1") { ImagePath = "图像1.Image", Engine = OcrEngine.DeepOcr, ConfidenceThreshold = 0.9999999 };

        try
        {
            // 阈值高于任何字符的置信度 → 全部过滤 → 未找到（过滤数量的日志在成功路径用例中验证）
            tool.FailWhenNotFound = false;
            FlowContext ctx = ContextWith(image);
            NodeResult result = tool.Run(ctx);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(false, ctx.GetVariable("字符识别1", "Found").Value);
            Assert.Equal(string.Empty, ctx.GetVariable("字符识别1", "Text").Value);
        }
        finally
        {
            tool.ReleaseResources();
        }
    }

    // ======================= 定位矩阵跟随（HALCON） =======================

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 定位矩阵_单位阵_结果与无矩阵一致()
    {
        HObject image = RenderImage("20261006");
        OcrTool tool = Ocr();
        tool.MatrixPath = "定位.HomMat";

        FlowContext ctx = ContextWith(image);
        ctx.SetVariable(Variable.Object("定位", "HomMat", HomMat2D.Identity, 1));
        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);

        FlowContext baseline = RunOk(Ocr(), RenderImage("20261006"));
        Assert.Equal(TextOf(baseline), TextOf(ctx));
        Assert.Equal(baseline.GetVariable("字符识别1", "Chars").GetValue<string[]>(),
            ctx.GetVariable("字符识别1", "Chars").GetValue<string[]>());
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 定位矩阵_平移_ROI随矩阵移动_坐标为原图坐标()
    {
        (byte[] pixels, int w, int h) = Render(new[] { "20261006" });
        int dr = 40, dc = 60;
        HObject teachImage = ToImage(pixels, w, h);
        HObject currentImage = PlaceOnCanvas((pixels, w, h), h + 120, w + 160, dr, dc);

        // 示教 ROI：文字图全幅（示教坐标系下文字位置）
        HOperatorSet.GenRectangle1(out HObject teachRoi, 0, 0, h - 1, w - 1);
        // 平移矩阵：示教位姿 → 当前位姿
        HOperatorSet.HomMat2dIdentity(out HTuple translate);
        HOperatorSet.HomMat2dTranslate(translate, dr, dc, out translate);

        OcrTool tool = Ocr();
        tool.RegionPath = "ROI.Region";
        tool.MatrixPath = "定位.HomMat";
        FlowContext ctx = ContextWith(currentImage);
        ctx.SetVariable(Variable.Object("ROI", "Region", new HalconRegion(teachRoi), 1));
        ctx.SetVariable(Variable.Object("定位", "HomMat", new HomMat2D(translate), 1));

        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("20261006", TextOf(ctx));

        // 无矩阵基线（示教图 + 示教 ROI）的字符中心 + 平移量 ≈ 跟随运行的字符中心
        FlowContext baseline = ContextWith(teachImage);
        baseline.SetVariable(Variable.Object("ROI", "Region", new HalconRegion(RectangleRegion(0, 0, h - 1, w - 1)), 1));
        OcrTool baselineTool = Ocr();
        baselineTool.RegionPath = "ROI.Region";
        Assert.True(baselineTool.Run(baseline).IsSuccess);
        HObject baseRegions = ((HalconRegion)baseline.GetVariable("字符识别1", "CharRegions").Value).Object;
        HOperatorSet.AreaCenter(baseRegions, out HTuple _, out HTuple baseRows, out HTuple baseCols);
        HObject followedRegions = ((HalconRegion)ctx.GetVariable("字符识别1", "CharRegions").Value).Object;
        HOperatorSet.AreaCenter(followedRegions, out HTuple _, out HTuple rows, out HTuple cols);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(baseRows[i].D + dr, rows[i].D, 1.5);
            Assert.Equal(baseCols[i].D + dc, cols[i].D, 1.5);
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 定位矩阵_旋转_ROI水平化后识别_结果变换回原图坐标()
    {
        (byte[] pixels, int w, int h) = Render(new[] { "20261006" });
        HObject teachImage = ToImage(pixels, w, h);
        double theta = 20 * Math.PI / 180;
        HOperatorSet.HomMat2dIdentity(out HTuple rotate);
        HOperatorSet.HomMat2dRotate(rotate, -theta, h / 2.0, w / 2.0, out rotate);
        HOperatorSet.AffineTransImage(teachImage, out HObject tilted, rotate, "constant", "false");

        OcrTool tool = Ocr();
        tool.RegionPath = "ROI.Region";
        tool.MatrixPath = "定位.HomMat";
        FlowContext ctx = ContextWith(tilted);
        ctx.SetVariable(Variable.Object("ROI", "Region", new HalconRegion(RectangleRegion(0, 0, h - 1, w - 1)), 1));
        ctx.SetVariable(Variable.Object("定位", "HomMat", new HomMat2D(rotate), 1));

        NodeResult result = tool.Run(ctx);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("20261006", TextOf(ctx));

        // 原图坐标：跟随结果的第 1 个字符中心 ≈ 基线中心经矩阵变换后的位置
        FlowContext baseline = RunOk(Ocr(), RenderImage("20261006"));
        HObject baseRegions = ((HalconRegion)baseline.GetVariable("字符识别1", "CharRegions").Value).Object;
        HOperatorSet.AreaCenter(baseRegions, out HTuple _, out HTuple baseRows, out HTuple baseCols);
        HOperatorSet.AffineTransPoint2d(rotate, baseRows.DArr[0], baseCols.DArr[0], out HTuple expectedRow, out HTuple expectedCol);
        HObject followedRegions = ((HalconRegion)ctx.GetVariable("字符识别1", "CharRegions").Value).Object;
        HOperatorSet.AreaCenter(followedRegions, out HTuple _, out HTuple rows, out HTuple cols);
        Assert.Equal(expectedRow.D, rows.DArr[0], 3.0);
        Assert.Equal(expectedCol.D, cols.DArr[0], 3.0);
    }

    [Fact]
    public void 定位矩阵_引用类型错误_拒绝运行_非HALCON()
    {
        var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("定位", "Score", VariableType.Double, 0.9));
        OcrTool tool = Ocr();
        tool.MatrixPath = "定位.Score";

        NodeResult result = tool.Run(ctx);

        Assert.False(result.IsSuccess);
        Assert.Contains("定位矩阵引用", result.Message);
    }

    // ======================= 未找到、期望格式（HALCON） =======================

    [Theory]
    [Trait("Requires", "HALCON")]
    [InlineData(true)]
    [InlineData(false)]
    public void 空图_未找到策略两态(bool failWhenNotFound)
    {
        OcrTool tool = Ocr();
        tool.FailWhenNotFound = failWhenNotFound;
        FlowContext ctx = ContextWith(BlankImage());

        NodeResult result = tool.Run(ctx);

        if (failWhenNotFound)
        {
            Assert.False(result.IsSuccess);
            Assert.Equal("未识别到字符", result.Message);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(false, ctx.GetVariable("字符识别1", "Found").Value);
            Assert.Equal(0, ctx.GetVariable("字符识别1", "LineCount").Value);
            Assert.Equal(string.Empty, ctx.GetVariable("字符识别1", "Text").Value);
            Assert.True(double.IsNaN((double)ctx.GetVariable("字符识别1", "MinConfidence").Value));
            Assert.Contains(ctx.StructuredLogs, l => l.Message.Contains("未识别到字符（已设置未找到时继续：Found=false）"));
        }
    }

    [Fact]
    [Trait("Requires", "HALCON")]
    public void 期望格式_只影响PatternOk_不影响识别结果()
    {
        HObject digits = RenderImage("20261006");
        HObject letters = RenderImage("ABC123");

        OcrTool matching = Ocr();
        matching.ExpectedPattern = "^\\d{8}$";
        FlowContext digitsCtx = RunOk(matching, digits);
        Assert.Equal("20261006", TextOf(digitsCtx));
        Assert.Equal(true, digitsCtx.GetVariable("字符识别1", "PatternOk").Value);

        FlowContext lettersCtx = RunOk(matching, letters);
        Assert.Equal("ABC123", TextOf(lettersCtx));
        Assert.Equal(false, lettersCtx.GetVariable("字符识别1", "PatternOk").Value);
        Assert.Equal(true, lettersCtx.GetVariable("字符识别1", "Found").Value);
    }

    // ======================= 校验、显隐、序列化、登记（非 HALCON） =======================

    [Fact]
    public void 字符识别参数校验_流程校验与运行措辞一致()
    {
        OcrTool badPattern = Ocr();
        badPattern.ExpectedPattern = "[";
        ToolConfigurationIssue patternIssue = Assert.Single(badPattern.CheckConfiguration());
        Assert.Equal(nameof(OcrTool.ExpectedPattern), patternIssue.Parameter);
        Assert.StartsWith("期望格式不是有效的正则表达式", patternIssue.Message);
        NodeResult result = badPattern.Run(new FlowContext());
        Assert.False(result.IsSuccess);
        Assert.Equal($"字符识别1 {nameof(OcrTool.ExpectedPattern)}：{patternIssue.Message}", result.Message);

        OcrTool badPolarity = Ocr();
        badPolarity.Polarity = "invalid";
        ToolConfigurationIssue polarityIssue = Assert.Single(badPolarity.CheckConfiguration());
        Assert.Equal(nameof(OcrTool.Polarity), polarityIssue.Parameter);
        Assert.Equal("极性必须是 dark_on_light / light_on_dark / both", polarityIssue.Message);

        OcrTool emptyClassifier = Ocr();
        emptyClassifier.Classifier = "  ";
        ToolConfigurationIssue classifierIssue = Assert.Single(emptyClassifier.CheckConfiguration());
        Assert.Equal((nameof(OcrTool.Classifier), "字符分类器不能为空"), (classifierIssue.Parameter, classifierIssue.Message));

        var deep = new OcrTool("字符识别1") { Engine = OcrEngine.DeepOcr };
        deep.Mode = "detect";
        ToolConfigurationIssue modeIssue = Assert.Single(deep.CheckConfiguration());
        Assert.Equal(nameof(OcrTool.Mode), modeIssue.Parameter);
        Assert.Equal("Deep OCR 模式必须是 auto / recognition", modeIssue.Message);

        deep.Mode = "auto";
        deep.Device = "Tpu";
        ToolConfigurationIssue deviceIssue = Assert.Single(deep.CheckConfiguration());
        Assert.Equal(nameof(OcrTool.Device), deviceIssue.Parameter);
        Assert.Equal("设备必须是 Cpu / Gpu", deviceIssue.Message);

        deep.Device = "Cpu";
        deep.ConfidenceThreshold = 1.5;
        ToolConfigurationIssue thresholdIssue = Assert.Single(deep.CheckConfiguration());
        Assert.Equal(nameof(OcrTool.ConfidenceThreshold), thresholdIssue.Parameter);
        Assert.Equal("最低字符置信度必须在 0~1 之间（0 表示不过滤）", thresholdIssue.Message);

        // DeepOcr 不校验 TextModel 参数，默认配置无问题
        deep.ConfidenceThreshold = 0;
        Assert.Empty(deep.CheckConfiguration());
        Assert.Empty(Ocr().CheckConfiguration());
    }

    [Fact]
    public void 字符识别参数按引擎显隐()
    {
        OcrTool textModel = Ocr();
        Assert.True(textModel.IsParameterVisible(nameof(OcrTool.Classifier)));
        Assert.True(textModel.IsParameterVisible(nameof(OcrTool.Polarity)));
        Assert.True(textModel.IsParameterVisible(nameof(OcrTool.TextLineSeparators)));
        Assert.False(textModel.IsParameterVisible(nameof(OcrTool.Mode)));
        Assert.False(textModel.IsParameterVisible(nameof(OcrTool.Device)));
        Assert.False(textModel.IsParameterVisible(nameof(OcrTool.ConfidenceThreshold)));
        Assert.True(textModel.IsParameterVisible(nameof(OcrTool.Alphabet)));
        Assert.True(textModel.IsParameterVisible(nameof(OcrTool.ExpectedPattern)));

        var deep = new OcrTool("字符识别1") { Engine = OcrEngine.DeepOcr };
        Assert.False(deep.IsParameterVisible(nameof(OcrTool.Classifier)));
        Assert.False(deep.IsParameterVisible(nameof(OcrTool.DotPrint)));
        Assert.False(deep.IsParameterVisible(nameof(OcrTool.MinCharHeight)));
        Assert.True(deep.IsParameterVisible(nameof(OcrTool.Mode)));
        Assert.True(deep.IsParameterVisible(nameof(OcrTool.Device)));
        Assert.True(deep.IsParameterVisible(nameof(OcrTool.ConfidenceThreshold)));
    }

    [Fact]
    public void OcrEngine枚举追加在末尾_默认值等于现有行为()
    {
        Assert.Equal(new[] { "TextModel", "DeepOcr" }, Enum.GetNames<OcrEngine>());
        Assert.Equal(0, (int)OcrEngine.TextModel);
        OcrTool tool = Ocr();
        Assert.Equal(OcrEngine.TextModel, tool.Engine);
        Assert.Equal("Universal_0-9A-Z_Rej.occ", tool.Classifier);
        Assert.Equal("dark_on_light", tool.Polarity);
        Assert.Equal("auto", tool.Mode);
        Assert.Equal("Cpu", tool.Device);
        Assert.Equal(0, tool.ConfidenceThreshold);
        Assert.True(tool.FailWhenNotFound);
    }

    [Fact]
    public void 字符识别序列化往返_JSON含ocr_枚举按数字保存()
    {
        var tool = new OcrTool("字符识别1")
        {
            ImagePath = "Input.Image",
            Engine = OcrEngine.DeepOcr,
            Mode = "recognition",
            Device = "Cpu",
            ConfidenceThreshold = 0.5,
            Alphabet = "0123456789",
            ExpectedPattern = "^\\d{8}$",
            RegionPath = "ROI.Region",
            MatrixPath = "定位.HomMat",
            FailWhenNotFound = false
        };
        string json = FlowSerializer.SaveNode(new ToolNode(tool));
        Assert.Contains("\"ToolId\": \"ocr\"", json);
        Assert.Contains("\"Engine\": 1", json);
        Assert.Contains("\"Mode\": \"recognition\"", json);
        Assert.Contains("\"ConfidenceThreshold\": 0.5", json);

        var loaded = (OcrTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(json)).Tool;
        Assert.Equal((OcrEngine.DeepOcr, "recognition", "Cpu", 0.5, "0123456789", "^\\d{8}$"),
            (loaded.Engine, loaded.Mode, loaded.Device, loaded.ConfidenceThreshold, loaded.Alphabet, loaded.ExpectedPattern));
        Assert.Equal(("ROI.Region", "定位.HomMat", false), (loaded.RegionPath, loaded.MatrixPath, loaded.FailWhenNotFound));

        // 历史文件缺省新属性：TextModel 行为不变
        JsonObject legacy = JsonNode.Parse(FlowSerializer.SaveNode(new ToolNode(Ocr())))!.AsObject();
        JsonObject properties = legacy["Tool"]!["Properties"]!.AsObject();
        foreach (string name in new[] { "Engine", "Classifier", "Polarity", "DotPrint", "MinCharHeight", "MaxCharHeight",
                     "MinStrokeWidth", "MaxStrokeWidth", "TextLineSeparators", "Alphabet", "Mode", "Device",
                     "ConfidenceThreshold", "ExpectedPattern" })
        {
            Assert.True(properties.Remove(name), name);
        }
        var old = (OcrTool)Assert.IsType<ToolNode>(FlowSerializer.LoadNode(legacy.ToJsonString())).Tool;
        Assert.Equal(OcrEngine.TextModel, old.Engine);
        Assert.Equal("Universal_0-9A-Z_Rej.occ", old.Classifier);
        Assert.Equal("dark_on_light", old.Polarity);
        Assert.Null(old.Alphabet);
        Assert.Null(old.ExpectedPattern);
        Assert.True(old.FailWhenNotFound);
    }

    [Fact]
    public void 字符识别输出不与参数同名()
    {
        var parameters = typeof(OcrTool).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain(ToolMetadata.GetOutputs(typeof(OcrTool)), o => parameters.Contains(o.Name));
        Assert.Equal(new[] { "Text", "Lines", "LineCount", "Chars", "Confidences", "MinConfidence", "CharRegions", "WordContours", "PatternOk", "Found" },
            ToolMetadata.GetOutputs(typeof(OcrTool)).Select(o => o.Name).ToArray());
    }

    [Fact]
    public void 工具箱_字符识别有条目()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "ocr");
        Assert.Equal(("06 识别工具", "字符识别"), (item.Category, item.DisplayName));
        var created = Assert.IsType<OcrTool>(Assert.IsType<ToolNode>(item.Factory()).Tool);
        Assert.StartsWith("字符识别", created.ModuleName);
        Assert.Equal("Input.Image", created.ImagePath);
        Assert.Equal(OcrEngine.TextModel, created.Engine);
    }
}
