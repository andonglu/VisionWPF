using System;
using System.Collections;
using System.Collections.Generic;
using HalconDotNet;

namespace VisionFlow.Variables
{
    /// <summary>
    /// 持有 HALCON 资源的复合结果项（VF-04）：实现后 <see cref="VisionFlow.Core.FlowContext.Dispose"/>
    /// 能递归回收其内部对象。按引用去重，与其他输出共享同一底层对象时不会重复释放。
    /// </summary>
    public interface IHalconResourceContainer
    {
        /// <summary>该项持有的 HALCON 对象（归本次运行所有）。</summary>
        IEnumerable<HObject> OwnedHalconObjects { get; }
    }

    /// <summary>
    /// HALCON 所有权移交辅助（VF-04）。工具输出归本次运行所有：
    /// <see cref="VisionFlow.Core.ToolBase.SetOutput"/> 自动调用，直接写 <see cref="VisionFlow.Core.FlowContext.SetVariable"/>
    /// 的框架代码（如共享的输出辅助类）应显式调用 <see cref="Adopt"/>。
    /// </summary>
    public static class HalconOwnership
    {
        /// <summary>把新产出的包装对象（含集合元素）转为拥有。</summary>
        public static void Adopt(Variable variable)
        {
            Adopt(variable, _ => true);
        }

        internal static void Adopt(Variable variable, Predicate<HObject> canAdopt)
        {
            ArgumentNullException.ThrowIfNull(variable);
            AdoptValue(variable.Value, canAdopt, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private static void AdoptValue(object value, Predicate<HObject> canAdopt, HashSet<object> visited)
        {
            if (value == null || value is string || !visited.Add(value))
            {
                return;
            }
            switch (value)
            {
                case HalconImage image when canAdopt(image.Object):
                    image.Adopt();
                    break;
                case HalconRegion region when canAdopt(region.Object):
                    region.Adopt();
                    break;
                case HalconXld xld when canAdopt(xld.Object):
                    xld.Adopt();
                    break;
                case IDictionary dictionary:
                    foreach (object item in dictionary.Values)
                    {
                        AdoptValue(item, canAdopt, visited);
                    }
                    break;
                case IEnumerable enumerable:
                    foreach (object item in enumerable)
                    {
                        AdoptValue(item, canAdopt, visited);
                    }
                    break;
            }
        }
    }
}
