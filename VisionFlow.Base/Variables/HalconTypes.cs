using System;
using HalconDotNet;

namespace VisionFlow.Variables
{
    /// <summary>
    /// Halcon 图像（专用类型）。虽然底层是 HObject，但它与区域等其它 HObject 是不同类型——
    /// 工具的输入输出声明、编辑器的引用选择都按"图像"识别，
    /// 区域变量不会出现在图像参数的候选中，反之亦然。
    ///
    /// 生命周期协议（VF-04）：
    /// - 默认构造为“借用”语义（OwnsObject = false），包装不负责释放底层对象；
    /// - <see cref="Owned"/> 创建的包装在 Dispose 时释放底层对象；
    /// - 同一底层对象被多个包装引用（别名）时，由 FlowContext.Dispose 按引用去重，只释放一次。
    /// </summary>
    public sealed class HalconImage : IDisposable
    {
        /// <summary>底层 Halcon 对象。</summary>
        public HObject Object { get; private set; }
        /// <summary>是否拥有底层对象（Dispose 时是否释放它）。</summary>
        public bool OwnsObject { get; private set; }
        private bool _disposed;

        public HalconImage(HObject obj)
            : this(obj, ownsObject: false)
        {
        }

        private HalconImage(HObject obj, bool ownsObject)
        {
            Object = obj ?? throw new ArgumentNullException(nameof(obj), "图像对象不能为空");
            OwnsObject = ownsObject;
        }

        /// <summary>创建拥有底层对象的包装（Dispose 时释放）。</summary>
        public static HalconImage Owned(HObject obj)
        {
            return new HalconImage(obj, ownsObject: true);
        }

        /// <summary>把包装从借用转为拥有（框架内部用于工具输出所有权移交，业务代码不应调用）。幂等。</summary>
        public void Adopt()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            OwnsObject = true;
        }

        /// <summary>幂等：重复调用安全；借用语义下不释放底层对象。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (OwnsObject)
            {
                Object?.Dispose();
            }
        }

        public override string ToString()
        {
            return "HalconImage";
        }
    }

    /// <summary>
    /// Halcon 区域（专用类型）。与图像同为 HObject，但语义不同：
    /// 引用候选按"区域"过滤，避免与图像混选。
    /// 生命周期协议与 <see cref="HalconImage"/> 相同（默认借用，Owned 拥有，Dispose 幂等）。
    /// </summary>
    public sealed class HalconRegion : IDisposable
    {
        /// <summary>底层 Halcon 对象。</summary>
        public HObject Object { get; private set; }
        /// <summary>是否拥有底层对象（Dispose 时是否释放它）。</summary>
        public bool OwnsObject { get; private set; }
        private bool _disposed;

        public HalconRegion(HObject obj)
            : this(obj, ownsObject: false)
        {
        }

        private HalconRegion(HObject obj, bool ownsObject)
        {
            Object = obj ?? throw new ArgumentNullException(nameof(obj), "区域对象不能为空");
            OwnsObject = ownsObject;
        }

        /// <summary>创建拥有底层对象的包装（Dispose 时释放）。</summary>
        public static HalconRegion Owned(HObject obj)
        {
            return new HalconRegion(obj, ownsObject: true);
        }

        /// <summary>把包装从借用转为拥有（框架内部用于工具输出所有权移交，业务代码不应调用）。幂等。</summary>
        public void Adopt()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            OwnsObject = true;
        }

        /// <summary>幂等：重复调用安全；借用语义下不释放底层对象。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (OwnsObject)
            {
                Object?.Dispose();
            }
        }

        public override string ToString()
        {
            return "HalconRegion";
        }
    }

    /// <summary>
    /// HALCON XLD 轮廓（专用类型）。与图像、区域同样基于 HObject，
    /// 但在工具输入输出声明中单独过滤，避免 XLD 与 Region 混选。
    /// 生命周期协议与 <see cref="HalconImage"/> 相同（默认借用，Owned 拥有，Dispose 幂等）。
    /// </summary>
    public sealed class HalconXld : IDisposable
    {
        /// <summary>底层 Halcon XLD 对象。</summary>
        public HObject Object { get; private set; }
        /// <summary>是否拥有底层对象（Dispose 时是否释放它）。</summary>
        public bool OwnsObject { get; private set; }
        private bool _disposed;

        public HalconXld(HObject obj)
            : this(obj, ownsObject: false)
        {
        }

        private HalconXld(HObject obj, bool ownsObject)
        {
            Object = obj ?? throw new ArgumentNullException(nameof(obj), "XLD 对象不能为空");
            OwnsObject = ownsObject;
        }

        /// <summary>创建拥有底层对象的包装（Dispose 时释放）。</summary>
        public static HalconXld Owned(HObject obj)
        {
            return new HalconXld(obj, ownsObject: true);
        }

        /// <summary>把包装从借用转为拥有（框架内部用于工具输出所有权移交，业务代码不应调用）。幂等。</summary>
        public void Adopt()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            OwnsObject = true;
        }

        /// <summary>幂等：重复调用安全；借用语义下不释放底层对象。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (OwnsObject)
            {
                Object?.Dispose();
            }
        }

        public override string ToString()
        {
            return "HalconXld";
        }
    }
}
