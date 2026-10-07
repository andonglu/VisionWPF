using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>区域转轮廓的方式（成员名即 HALCON 参数值）。</summary>
    public enum RegionContourMode
    {
        /// <summary>外边界。</summary>
        border,
        /// <summary>外边界与孔洞边界。</summary>
        border_holes,
        /// <summary>沿像素中心。</summary>
        center
    }

    /// <summary>轮廓转区域的方式（成员名即 HALCON 参数值）。</summary>
    public enum XldRegionMode
    {
        /// <summary>填充轮廓内部。</summary>
        filled,
        /// <summary>只取轮廓线经过的像素。</summary>
        margin
    }

    /// <summary>
    /// Region 转 XLD（RG-05，gen_contour_region_xld）：每个区域生成对应的轮廓，输出名与其他 XLD 工具一致为 Xld。
    /// 反向转换见“XLD 转 Region”（RG-06）。
    /// </summary>
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class RegionToXldTool : XldToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public RegionContourMode Mode { get; set; } = RegionContourMode.border;

        public RegionToXldTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.GenContourRegionXld(region, out HObject xld, Mode.ToString());
            return SetXldOutput(ctx, xld, $"[Region 转 XLD] {Mode}");
        }
    }

    /// <summary>
    /// XLD 转 Region（RG-06，gen_region_contour_xld）：每条轮廓生成对应的区域。
    /// 与 Region 转 XLD 分成两个工具，使各自的必填输入都能被流程校验检查。
    /// </summary>
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class XldToRegionTool : ToolBase, INotFoundPolicy
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public XldRegionMode Mode { get; set; } = XldRegionMode.filled;

        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public XldToRegionTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            HOperatorSet.GenRegionContourXld(xld, out HObject region, Mode.ToString());
            return RegionOutput.Set(ctx, ModuleName, this, region, "XLD 转 Region", Mode.ToString());
        }
    }
}
