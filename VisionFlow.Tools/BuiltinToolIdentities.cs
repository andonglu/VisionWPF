using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using VisionFlow.Editing;

namespace VisionFlow.Tools
{
    internal static class BuiltinToolIdentities
    {
        // ID 与工具箱一致，但登记不依赖编辑器启动；重命名时保留 ID 并补充旧类型身份别名。
        [ModuleInitializer]
        [SuppressMessage("Usage", "CA2255", Justification = "无头宿主也必须在使用工具前登记持久化身份。")]
        internal static void Initialize()
        {
            FlowSerializer.RegisterToolType(typeof(HalconModelMatchTool), "match");
            FlowSerializer.RegisterToolType(typeof(HalconGrayMatchTool), "gray-match");
            FlowSerializer.RegisterToolType(typeof(HalconScaledShapeMatchTool), "scaled-shape-match");
            FlowSerializer.RegisterToolType(typeof(HalconLocalDeformableMatchTool), "deformable-match");
            FlowSerializer.RegisterToolType(typeof(HalconGenericShapeMatchTool), "generic-shape-match");
            FlowSerializer.RegisterToolType(typeof(CornerFindTool), "corner-find");
            FlowSerializer.RegisterToolType(typeof(GrayProjectionFollowTool), "gray-projection");
            FlowSerializer.RegisterToolType(typeof(VariationInspectTool), "variation-inspect");
            FlowSerializer.RegisterToolType(typeof(EllipseFollowMeasureTool), "measure");
            FlowSerializer.RegisterToolType(typeof(LineFollowMeasureTool), "measureline");
            FlowSerializer.RegisterToolType(typeof(OneDCaliperFollowMeasureTool), "measure-caliper1d");
            FlowSerializer.RegisterToolType(typeof(ArcCaliperFollowMeasureTool), "measure-arc-caliper1d");
            FlowSerializer.RegisterToolType(typeof(RectangleFollowMeasureTool), "measurerectangle");
            FlowSerializer.RegisterToolType(typeof(CircleFollowMeasureTool), "measurecircle");
            FlowSerializer.RegisterToolType(typeof(LoadImageTool), "loadimage");
            FlowSerializer.RegisterToolType(typeof(MeanImageTool), "mean-image");
            FlowSerializer.RegisterToolType(typeof(GrayEnhanceTool), "gray-enhance");
            FlowSerializer.RegisterToolType(typeof(ImageGeometryTool), "image-geometry");
            FlowSerializer.RegisterToolType(typeof(PolarUnwrapTool), "polar-unwrap");
            FlowSerializer.RegisterToolType(typeof(PolarInverseTool), "polar-inverse");
            FlowSerializer.RegisterToolType(typeof(RegionToImageTool), "region-to-image");
            FlowSerializer.RegisterToolType(typeof(AffineTransformImageTool), "affine-trans-image");
            FlowSerializer.RegisterToolType(typeof(ReduceDomainTool), "reduce-domain");
            FlowSerializer.RegisterToolType(typeof(AddSubImageTool), "add-sub-image");
            FlowSerializer.RegisterToolType(typeof(DecomposeChannelsTool), "decompose-channels");
            FlowSerializer.RegisterToolType(typeof(Compose3ImageTool), "compose3");
            FlowSerializer.RegisterToolType(typeof(TransColorSpaceTool), "trans-color-space");
            FlowSerializer.RegisterToolType(typeof(ThresholdTool), "threshold");
            FlowSerializer.RegisterToolType(typeof(AutoThresholdTool), "auto-threshold");
            FlowSerializer.RegisterToolType(typeof(BinaryThresholdTool), "binary-threshold");
            FlowSerializer.RegisterToolType(typeof(FastThresholdTool), "fast-threshold");
            FlowSerializer.RegisterToolType(typeof(CharThresholdTool), "char-threshold");
            FlowSerializer.RegisterToolType(typeof(VarThresholdTool), "var-threshold");
            FlowSerializer.RegisterToolType(typeof(RegionProcessTool), "regionprocess");
            FlowSerializer.RegisterToolType(typeof(ManualRegionTool), "manual-region");
            FlowSerializer.RegisterToolType(typeof(RegionDifferenceTool), "region-difference");
            FlowSerializer.RegisterToolType(typeof(RegionUnion2Tool), "region-union2");
            FlowSerializer.RegisterToolType(typeof(RegionIntersectionTool), "region-intersection");
            FlowSerializer.RegisterToolType(typeof(RegionShapeTransTool), "region-shape-trans");
            FlowSerializer.RegisterToolType(typeof(RegionUnion1Tool), "region-union1");
            FlowSerializer.RegisterToolType(typeof(MorphologyTool), "morphology");
            FlowSerializer.RegisterToolType(typeof(MorphologyRectTool), "morphology-rect");
            FlowSerializer.RegisterToolType(typeof(MorphologyCircleTool), "morphology-circle");
            FlowSerializer.RegisterToolType(typeof(RegionFeaturesTool), "region-features");
            FlowSerializer.RegisterToolType(typeof(RegionMinMaxGrayTool), "region-min-max-gray");
            FlowSerializer.RegisterToolType(typeof(SelectRegionTool), "selectregion");
            FlowSerializer.RegisterToolType(typeof(RegionToXldTool), "region-to-xld");
            FlowSerializer.RegisterToolType(typeof(XldToRegionTool), "xld-to-region");
            FlowSerializer.RegisterToolType(typeof(RegionDistanceTool), "region-distance");
            FlowSerializer.RegisterToolType(typeof(RegionSortTool), "region-sort");
            FlowSerializer.RegisterToolType(typeof(RegionPoseTool), "regionpose");
            FlowSerializer.RegisterToolType(typeof(ZoneInspectTool), "zone-inspect");
            FlowSerializer.RegisterToolType(typeof(DescriptorMatchTool), "descriptor-match");
            FlowSerializer.RegisterToolType(typeof(RangeClassifyTool), "range-classify");
            FlowSerializer.RegisterToolType(typeof(ExpressionCalcTool), "expression-calc");
            FlowSerializer.RegisterToolType(typeof(ResultJudgeTool), "result-judge");
            FlowSerializer.RegisterToolType(typeof(ArrayProcessTool), "array-process");
            FlowSerializer.RegisterToolType(typeof(AngleConvertTool), "angle-convert");
            FlowSerializer.RegisterToolType(typeof(ContourCreateTool), "contour-create");
            FlowSerializer.RegisterToolType(typeof(SelectContourTool), "select-contour");
            FlowSerializer.RegisterToolType(typeof(ConcatXldTool), "concat-xld");
            FlowSerializer.RegisterToolType(typeof(SegmentXldTool), "segment-xld");
            FlowSerializer.RegisterToolType(typeof(XldFeaturesTool), "xld-features");
            FlowSerializer.RegisterToolType(typeof(FitLineTool), "fit-line");
            FlowSerializer.RegisterToolType(typeof(FitCircleTool), "fit-circle");
            FlowSerializer.RegisterToolType(typeof(IntersectionLinesTool), "intersection-lines");
            FlowSerializer.RegisterToolType(typeof(XldProcessTool), "xld-process");
            FlowSerializer.RegisterToolType(typeof(FitEllipseRectTool), "fit-ellipse-rect");
            FlowSerializer.RegisterToolType(typeof(ContourDistanceTool), "contour-distance");
            FlowSerializer.RegisterToolType(typeof(GeometryRelationTool), "geometry-relation");
            FlowSerializer.RegisterToolType(typeof(AffinePointTool), "affine-point");
            FlowSerializer.RegisterToolType(typeof(AlignmentOffsetTool), "alignment-offset");
            FlowSerializer.RegisterToolType(typeof(ImageRectifyTool), "image-rectify");
            FlowSerializer.RegisterToolType(typeof(Barcode1DTool), "barcode1d", "ldwelding.barcode1d");
        }
    }
}
