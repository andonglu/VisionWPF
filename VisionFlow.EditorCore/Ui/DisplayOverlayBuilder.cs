using System;
using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Ui
{
    /// <summary>
    /// 显示叠加构建器（VF-08 从 MainWindow 平移）：判断变量可显示性、
    /// 解析显示底图、把测量结果/区域/XLD 等变量拼成叠加层 HObject。
    /// 纯逻辑，不依赖具体 UI 框架；返回的叠加层由调用方负责释放。
    /// </summary>
    public static class DisplayOverlayBuilder
    {
        public static bool IsDisplayableVariable(Variable variable)
        {
            return variable.Value is HalconImage
                || variable.Value is HalconRegion
                || variable.Value is HalconXld
                || variable.Value is HObject
                || variable.Value is List<HObject>
                || variable.Value is List<LineMeasureResult>
                || variable.Value is List<RectangleMeasureResult>
                || variable.Value is List<CircleMeasureResult>
                || variable.Value is List<EllipseMeasureResult>;
        }

        /// <summary>解析变量的显示底图：自身是图像用自身，否则找同模块 Image 变量，最后回退输入图像。</summary>
        public static HObject ResolveDisplayBaseImage(FlowContext context, Variable variable)
        {
            if (variable.Value is HalconImage image)
            {
                return image.Object;
            }
            if (context.TryGetVariable(variable.ModuleName, "Image", out Variable sibling)
                && sibling.Value is HalconImage siblingImage)
            {
                return siblingImage.Object;
            }
            if (context.TryGetVariable("Input", "Image", out Variable input)
                && input.Value is HalconImage inputImage)
            {
                return inputImage.Object;
            }
            return null;
        }

        /// <summary>构建变量的叠加层对象（图像变量无叠加，返回空对象）。调用方负责释放返回值。</summary>
        public static HObject BuildVariableOverlay(Variable variable)
        {
            HOperatorSet.GenEmptyObj(out HObject overlay);
            if (variable.Value is HalconImage)
            {
                return overlay;
            }
            if (variable.Value is HalconRegion region)
            {
                AppendObject(ref overlay, region.Object);
            }
            else if (variable.Value is HalconXld xld)
            {
                AppendObject(ref overlay, xld.Object);
            }
            else if (variable.Value is HObject hObject)
            {
                AppendObject(ref overlay, hObject);
            }
            else if (variable.Value is List<HObject> objects)
            {
                foreach (HObject obj in objects)
                {
                    AppendObject(ref overlay, obj);
                }
            }
            else if (variable.Value is List<LineMeasureResult> lines)
            {
                foreach (LineMeasureResult line in lines)
                {
                    HOperatorSet.GenRegionLine(out HObject lineObject, line.Row1, line.Column1, line.Row2, line.Column2);
                    AppendAndDispose(ref overlay, lineObject);
                }
            }
            else if (variable.Value is List<RectangleMeasureResult> rectangles)
            {
                foreach (RectangleMeasureResult r in rectangles)
                {
                    HOperatorSet.GenRectangle2(out HObject rectangle, r.Row, r.Column, r.Phi, r.Length1, r.Length2);
                    AppendAndDispose(ref overlay, rectangle);
                }
            }
            else if (variable.Value is List<CircleMeasureResult> circles)
            {
                foreach (CircleMeasureResult c in circles)
                {
                    HOperatorSet.GenCircle(out HObject circle, c.Row, c.Column, c.Radius);
                    AppendAndDispose(ref overlay, circle);
                }
            }
            else if (variable.Value is List<EllipseMeasureResult> ellipses)
            {
                foreach (EllipseMeasureResult m in ellipses)
                {
                    HOperatorSet.GenEllipseContourXld(out HObject ellipse,
                        m.Row, m.Column, m.Phi, m.Length1, m.Length2, 0, Math.PI * 2.0, "positive", 1.5);
                    AppendAndDispose(ref overlay, ellipse);
                }
            }
            return overlay;
        }

        private static void AppendObject(ref HObject target, HObject obj)
        {
            if (obj == null || !obj.IsInitialized())
            {
                return;
            }
            HOperatorSet.ConcatObj(target, obj, out HObject combined);
            target.Dispose();
            target = combined;
        }

        private static void AppendAndDispose(ref HObject target, HObject obj)
        {
            try
            {
                AppendObject(ref target, obj);
            }
            finally
            {
                obj?.Dispose();
            }
        }
    }
}
