using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
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
                || variable.Value is IEnumerable<HalconXld>
                || variable.Value is List<LineMeasureResult>
                || variable.Value is List<RectangleMeasureResult>
                || variable.Value is List<CircleMeasureResult>
                || variable.Value is List<EllipseMeasureResult>;
        }

        /// <summary>
        /// 解析变量的显示底图，按顺序：自身是图像用自身；同模块 Image 变量；
        /// 给了流程根节点时沿来源工具的输入引用向上追溯图像（多图像流程中区域画在它真正来源的图像上）；
        /// 输入图像；最后回退上下文中的第一个图像变量（如流程用“图像加载”供图时，其他模块的输出也能找到底图）。
        /// </summary>
        public static HObject ResolveDisplayBaseImage(FlowContext context, Variable variable, FlowNode flowRoot = null)
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
            if (flowRoot != null)
            {
                HObject traced = TraceSourceImage(context, flowRoot, variable.ModuleName);
                if (traced != null)
                {
                    return traced;
                }
            }
            if (context.TryGetVariable("Input", "Image", out Variable input)
                && input.Value is HalconImage inputImage)
            {
                return inputImage.Object;
            }
            foreach (Variable candidate in context.GetAllVariables())
            {
                if (candidate.Value is HalconImage fallbackImage)
                {
                    return fallbackImage.Object;
                }
            }
            return null;
        }

        private const int MaxTraceDepth = 32;

        /// <summary>
        /// 从产生变量的工具开始追溯底图：先按声明顺序解析它的图像输入（期望类型 HalconImage，或历史声明的 HObject），
        /// 第一个解析出图像的即底图；都不可用时，沿其余输入引用到上游模块（先看上游模块的 Image 输出，再继续追溯）。
        /// 找不到来源工具（如 Input 模块）或追溯不到图像时返回 null，由调用方落到后续回退。
        /// </summary>
        private static HObject TraceSourceImage(FlowContext context, FlowNode flowRoot, string moduleName)
        {
            var tools = new Dictionary<string, ToolBase>(StringComparer.Ordinal);
            foreach (ToolNode toolNode in NodeNaming.EnumerateNodes(flowRoot).OfType<ToolNode>())
            {
                if (toolNode.Tool != null && !string.IsNullOrEmpty(toolNode.Tool.ModuleName) && !tools.ContainsKey(toolNode.Tool.ModuleName))
                {
                    tools.Add(toolNode.Tool.ModuleName, toolNode.Tool);
                }
            }
            return TraceSourceImage(context, tools, moduleName, new HashSet<string>(StringComparer.Ordinal), 0);
        }

        private static HObject TraceSourceImage(FlowContext context, IReadOnlyDictionary<string, ToolBase> tools, string moduleName,
            HashSet<string> visited, int depth)
        {
            if (depth > MaxTraceDepth || moduleName == null || !visited.Add(moduleName) || !tools.TryGetValue(moduleName, out ToolBase tool))
            {
                return null;
            }
            var upstreamPaths = new List<string>();
            foreach (ToolInputRefDef input in ToolMetadata.GetInputRefs(tool.GetType()))
            {
                string path = input.Property.GetValue(tool) as string;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                if ((input.ExpectedType == typeof(HalconImage) || input.ExpectedType == typeof(HObject))
                    && TryResolve(context, path, out object value) && value is HalconImage resolved)
                {
                    return resolved.Object;
                }
                upstreamPaths.Add(path);
            }
            foreach (string path in upstreamPaths)
            {
                string upstreamModule;
                try
                {
                    upstreamModule = VariableReference.Parse(path).ModuleName;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException)
                {
                    continue;
                }
                if (context.TryGetVariable(upstreamModule, "Image", out Variable upstreamImage)
                    && upstreamImage.Value is HalconImage upstreamHalconImage)
                {
                    return upstreamHalconImage.Object;
                }
                HObject traced = TraceSourceImage(context, tools, upstreamModule, visited, depth + 1);
                if (traced != null)
                {
                    return traced;
                }
            }
            return null;
        }

        private static bool TryResolve(FlowContext context, string path, out object value)
        {
            try
            {
                value = VariableReference.Parse(path).Resolve(context);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException
                || ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException)
            {
                value = null;
                return false;
            }
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
            else if (variable.Value is IEnumerable<HalconXld> xlds)
            {
                foreach (HalconXld item in xlds)
                {
                    AppendObject(ref overlay, item?.Object);
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
