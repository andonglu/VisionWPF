using System.Windows;
using VisionFlow.Core;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.WpfToolEditors.Editors;

namespace VisionFlow.WpfToolEditors
{
    /// <summary>
    /// WPF 工具编辑器路由（VF-08 从 MainWindow 平移）：注册编辑器（插件）→ 内置专用窗口 →
    /// 视觉预览窗口 → 通用编辑器。新增普通工具无需修改任何路由分支，自动落通用编辑器。
    /// 所有路由只接收编辑事务的工作副本。
    /// </summary>
    public static class WpfToolEditorRouter
    {
        public static Window Create(ToolBase tool, ToolEditContext context)
        {
            if (WpfToolEditorRegistry.TryCreate(tool, context, out Window registeredWindow))
            {
                return registeredWindow;
            }
            // 通用形状匹配是多模板工具，须先于单模板的模板匹配窗口（模板 / 灰度 / 缩放形状 / 局部变形）路由到专用窗口
            if (tool is HalconGenericShapeMatchTool genericShapeTool)
            {
                return new WpfGenericShapeMatchEditWindow(genericShapeTool, context);
            }
            if (tool is IHalconTemplateMatchTool matchTool)
            {
                return new WpfMatchToolEditWindow(matchTool, context);
            }
            if (tool is DescriptorMatchTool descriptorTool)
            {
                return new WpfDescriptorMatchToolEditWindow(descriptorTool, context);
            }
            if (tool is LoadImageTool loadImageTool)
            {
                return new WpfLoadImageToolEditWindow(loadImageTool);
            }
            // 椭圆测量也派生自 FollowMeasureToolBase，须先于通用跟随测量窗口路由到专用窗口；
            // 找角（两条边）与灰度投影（测量矩形 + 曲线显示）走通用跟随测量窗口
            if (tool is EllipseFollowMeasureTool ellipseMeasureTool)
            {
                return new WpfEllipseMeasureToolEditWindow(ellipseMeasureTool, context);
            }
            if (tool is FollowMeasureToolBase followMeasureTool)
            {
                return new WpfFollowMeasureToolEditWindow(followMeasureTool, context);
            }
            if (tool is ManualRegionTool manualRegionTool)
            {
                return new WpfManualRegionToolEditWindow(manualRegionTool, context);
            }
            if (tool is ExpressionCalcTool expressionCalcTool)
            {
                return new WpfExpressionCalcToolEditWindow(expressionCalcTool, context);
            }
            if (tool is ResultJudgeTool resultJudgeTool)
            {
                return new WpfResultJudgeToolEditWindow(resultJudgeTool, context);
            }
            if (tool is ArrayProcessTool arrayProcessTool)
            {
                return new WpfArrayProcessToolEditWindow(arrayProcessTool, context);
            }
            if (tool is VariationInspectTool variationTool)
            {
                return new WpfVariationInspectToolEditWindow(variationTool, context);
            }
            if (tool is AffinePointTool affinePointTool)
            {
                return new WpfAffinePointToolEditWindow(affinePointTool, context);
            }
            if (tool is AlignmentOffsetTool alignmentTool)
            {
                return new WpfAlignmentOffsetToolEditWindow(alignmentTool, context);
            }
            if (tool is ImageRectifyTool rectifyTool)
            {
                return new WpfImageRectifyToolEditWindow(rectifyTool, context);
            }
            // 阈值分割（含旧版独立阈值工具的兼容壳 AutoThresholdTool 等派生类）走专用窗口：颜色方式需要在图像上点击取色
            if (tool is ThresholdTool thresholdTool)
            {
                return new WpfThresholdToolEditWindow(thresholdTool, context);
            }
            return IsVisualPreviewTool(tool)
                ? (Window)new WpfVisualToolEditWindow(tool, context)
                : new WpfGenericToolEditWindow(tool, context);
        }

        private static bool IsVisualPreviewTool(ToolBase tool)
        {
            return tool is MeanImageTool
                || tool is GrayEnhanceTool
                || tool is AffineTransformImageTool
                || tool is ReduceDomainTool
                || tool is AddSubImageTool
                || tool is DecomposeChannelsTool
                || tool is Compose3ImageTool
                || tool is TransColorSpaceTool
                || tool is RegionProcessTool
                || tool is ManualRegionTool
                || tool is RegionDifferenceTool
                || tool is RegionUnion2Tool
                || tool is RegionIntersectionTool
                || tool is RegionShapeTransTool
                || tool is RegionUnion1Tool
                || tool is MorphologyTool
                || tool is RegionFeaturesTool
                || tool is RegionMinMaxGrayTool
                || tool is SelectRegionTool
                || tool is RegionSortTool
                || tool is ZoneInspectTool
                || tool is RegionPoseTool
                || tool is XldToolBase
                || tool is XldToRegionTool
                || tool is RegionDistanceTool
                || tool is ContourDistanceTool
                || tool is GeometryRelationTool
                || tool is XldFeaturesTool
                || tool is FitLineTool
                || tool is FitCircleTool
                || tool is IntersectionLinesTool;
        }
    }
}
