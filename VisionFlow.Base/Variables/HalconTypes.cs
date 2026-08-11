using System;
using HalconDotNet;

namespace VisionFlow.Variables
{
    /// <summary>
    /// Halcon 图像（专用类型）。虽然底层是 HObject，但它与区域等其它 HObject 是不同类型——
    /// 工具的输入输出声明、编辑器的引用选择都按"图像"识别，
    /// 区域变量不会出现在图像参数的候选中，反之亦然。
    /// </summary>
    public sealed class HalconImage
    {
        /// <summary>底层 Halcon 对象。</summary>
        public HObject Object { get; private set; }

        public HalconImage(HObject obj)
        {
            Object = obj ?? throw new ArgumentNullException(nameof(obj), "图像对象不能为空");
        }

        public override string ToString()
        {
            return "HalconImage";
        }
    }

    /// <summary>
    /// Halcon 区域（专用类型）。与图像同为 HObject，但语义不同：
    /// 引用候选按"区域"过滤，避免与图像混选。
    /// </summary>
    public sealed class HalconRegion
    {
        /// <summary>底层 Halcon 对象。</summary>
        public HObject Object { get; private set; }

        public HalconRegion(HObject obj)
        {
            Object = obj ?? throw new ArgumentNullException(nameof(obj), "区域对象不能为空");
        }

        public override string ToString()
        {
            return "HalconRegion";
        }
    }

    /// <summary>
    /// HALCON XLD 轮廓（专用类型）。与图像、区域同样基于 HObject，
    /// 但在工具输入输出声明中单独过滤，避免 XLD 与 Region 混选。
    /// </summary>
    public sealed class HalconXld
    {
        /// <summary>底层 Halcon XLD 对象。</summary>
        public HObject Object { get; private set; }

        public HalconXld(HObject obj)
        {
            Object = obj ?? throw new ArgumentNullException(nameof(obj), "XLD 对象不能为空");
        }

        public override string ToString()
        {
            return "HalconXld";
        }
    }
}
