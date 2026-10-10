using System.Linq;
using VisionFlow.Tools.Halcon;

namespace VisionFlow.Tests;

/// <summary>HALCON 模型参数目录：条目规模、scope 覆盖、中文说明、候选值与跨码制合并。</summary>
public class ParamCatalogTests
{
    private static readonly string[] Symbologies2D =
    {
        "QR Code", "Data Matrix ECC 200", "PDF417", "Aztec Code",
        "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix"
    };

    [Fact]
    public void 一维码目录非空()
    {
        Assert.NotEmpty(HalconParamCatalog.GetEntries("barcode1d"));
    }

    [Fact]
    public void 全部八个码制scope都有条目()
    {
        foreach (string symbology in Symbologies2D)
        {
            Assert.NotEmpty(HalconParamCatalog.GetEntries("datacode2d:" + symbology));
        }
    }

    [Theory]
    [InlineData("polarity")]
    [InlineData("timeout")]
    [InlineData("mirrored")]
    [InlineData("strict_model")]
    [InlineData("module_size_min")]
    [InlineData("string_encoding")]
    [InlineData("stop_after_result_num")]
    [InlineData("element_size_min")]
    [InlineData("orientation")]
    public void 常用参数中文说明非空(string name)
    {
        HalconParamEntry entry = HalconParamCatalog.GetEntries("barcode1d")
            .Concat(Symbologies2D.SelectMany(s => HalconParamCatalog.GetEntries("datacode2d:" + s)))
            .FirstOrDefault(e => e.Name == name);
        Assert.NotNull(entry);
        Assert.False(string.IsNullOrWhiteSpace(entry.Description));
    }

    [Fact]
    public void 极性参数候选值含dark_on_light()
    {
        HalconParamEntry polarity = HalconParamCatalog.GetEntries("datacode2d:QR Code")
            .First(e => e.Name == "polarity");
        Assert.Contains("dark_on_light", polarity.Values);
        Assert.Contains("light_on_dark", polarity.Values);
        Assert.False(string.IsNullOrEmpty(polarity.Default));
    }

    [Fact]
    public void 一维码目录含元素尺寸参数()
    {
        Assert.Contains(HalconParamCatalog.GetEntries("barcode1d"), e => e.Name == "element_size_min");
        Assert.Contains(HalconParamCatalog.GetEntries("barcode1d"), e => e.Name == "meas_thresh");
    }

    [Fact]
    public void 同名参数跨码制合并()
    {
        // 全部 8 个码制共有的参数 AppliesTo 为 null（一次定义）。
        HalconParamEntry polarity = HalconParamCatalog.GetEntries("datacode2d:QR Code")
            .First(e => e.Name == "polarity");
        Assert.Null(polarity.AppliesTo);
        foreach (string symbology in Symbologies2D)
        {
            Assert.Contains(HalconParamCatalog.GetEntries("datacode2d:" + symbology), e => e.Name == "polarity");
        }

        // PDF417 专属参数 module_aspect 在其他码制 scope 中不可见。
        Assert.Contains(HalconParamCatalog.GetEntries("datacode2d:PDF417"), e => e.Name == "module_aspect_min");
        Assert.DoesNotContain(HalconParamCatalog.GetEntries("datacode2d:QR Code"), e => e.Name == "module_aspect_min");
    }

    [Fact]
    public void 部分码制参数标注适用码制()
    {
        // contrast_tolerance 仅 Data Matrix ECC 200 与 GS1 DataMatrix。
        HalconParamEntry entry = HalconParamCatalog.GetEntries("datacode2d:Data Matrix ECC 200")
            .First(e => e.Name == "contrast_tolerance");
        Assert.NotNull(entry.AppliesTo);
        Assert.Equal(2, entry.AppliesTo.Length);
        Assert.Contains(HalconParamCatalog.GetEntries("datacode2d:GS1 DataMatrix"), e => e.Name == "contrast_tolerance");
        Assert.DoesNotContain(HalconParamCatalog.GetEntries("datacode2d:PDF417"), e => e.Name == "contrast_tolerance");
    }

    [Fact]
    public void 空scope返回空_未知码制仅返回通用参数()
    {
        Assert.Empty(HalconParamCatalog.GetEntries(null));
        // 未知码制：AppliesTo = null 表示通用参数，仍会返回；专属参数不会。
        Assert.All(HalconParamCatalog.GetEntries("datacode2d:不存在的码制"), e => Assert.Null(e.AppliesTo));
    }
}
