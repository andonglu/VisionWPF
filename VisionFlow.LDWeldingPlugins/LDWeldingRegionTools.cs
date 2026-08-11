using System;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.LDWeldingPlugins
{
    public enum LDMorphologyOperation
    {
        closing,
        opening,
        dilation,
        erosion
    }

    public enum RegionShapeTransformType
    {
        convex,
        ellipse,
        inner_circle,
        outer_circle,
        rectangle1,
        rectangle2,
        inner_rectangle1,
        inner_center,
        outer_rectangle1
    }

    [ToolboxTool("03 区域处理", "Region 相减", Id = "ldwelding.region-difference", DefaultModuleName = "Region相减")]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDRegionDifferenceTool : ToolBase
    {
        [InputRef("区域1", typeof(HalconRegion))]
        public string RegionPath1 { get; set; }

        [InputRef("区域2", typeof(HalconRegion))]
        public string RegionPath2 { get; set; }

        public bool Connection { get; set; }

        public LDRegionDifferenceTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region1 = Input<HalconRegion>(ctx, RegionPath1).Object;
            HObject region2 = Input<HalconRegion>(ctx, RegionPath2).Object;
            HOperatorSet.Difference(region1, region2, out HObject output);
            if (Connection)
            {
                HOperatorSet.Union1(output, out HObject union);
                output.Dispose();
                HOperatorSet.Connection(union, out output);
                union.Dispose();
            }

            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("Region 相减结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding Region 相减] 输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("03 区域处理", "Region 合并", Id = "ldwelding.region-union2", DefaultModuleName = "Region合并")]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDRegionUnion2Tool : ToolBase
    {
        [InputRef("区域1", typeof(HalconRegion))]
        public string RegionPath1 { get; set; }

        [InputRef("区域2", typeof(HalconRegion))]
        public string RegionPath2 { get; set; }

        public bool Connection { get; set; }

        public LDRegionUnion2Tool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region1 = Input<HalconRegion>(ctx, RegionPath1).Object;
            HObject region2 = Input<HalconRegion>(ctx, RegionPath2).Object;
            HOperatorSet.Union2(region1, region2, out HObject output);
            if (Connection)
            {
                HOperatorSet.Connection(output, out HObject connected);
                output.Dispose();
                output = connected;
            }

            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("Region 合并结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding Region 合并] 输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("03 区域处理", "Region 形状转换", Id = "ldwelding.shape-trans", DefaultModuleName = "形状转换")]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDShapeTransTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public RegionShapeTransformType Type { get; set; } = RegionShapeTransformType.convex;

        public LDShapeTransTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.ShapeTrans(region, out HObject output, Type.ToString());
            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("Region 形状转换结果为空");
            }

            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding Region 形状转换] Type={Type}, 输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("03 区域处理", "Region Union1", Id = "ldwelding.region-union1", DefaultModuleName = "RegionUnion1")]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDRegionUnion1Tool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public bool Connection { get; set; }

        public LDRegionUnion1Tool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.Union1(region, out HObject output);
            if (Connection)
            {
                HOperatorSet.Connection(output, out HObject connected);
                output.Dispose();
                output = connected;
            }
            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("Region Union1 结果为空");
            }
            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding Region Union1] 输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("03 区域处理", "矩形形态学", Id = "ldwelding.morphology-rect", DefaultModuleName = "矩形形态学")]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDMorphologyRectTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public int Width { get; set; } = 5;
        public int Height { get; set; } = 5;
        public bool Connection { get; set; }
        public LDMorphologyOperation Operation { get; set; } = LDMorphologyOperation.closing;

        public LDMorphologyRectTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (Width <= 0 || Height <= 0)
            {
                return NodeResult.Fail("矩形形态学宽高必须大于 0");
            }

            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject output;
            switch (Operation)
            {
                case LDMorphologyOperation.dilation:
                    HOperatorSet.DilationRectangle1(region, out output, Width, Height);
                    break;
                case LDMorphologyOperation.erosion:
                    HOperatorSet.ErosionRectangle1(region, out output, Width, Height);
                    break;
                case LDMorphologyOperation.opening:
                    HOperatorSet.OpeningRectangle1(region, out output, Width, Height);
                    break;
                default:
                    HOperatorSet.ClosingRectangle1(region, out output, Width, Height);
                    break;
            }
            if (Connection)
            {
                HOperatorSet.Union1(output, out HObject union);
                output.Dispose();
                HOperatorSet.Connection(union, out output);
                union.Dispose();
            }
            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("矩形形态学结果为空");
            }
            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 矩形形态学] {Operation}，输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("03 区域处理", "圆形形态学", Id = "ldwelding.morphology-circle", DefaultModuleName = "圆形形态学")]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDMorphologyCircleTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public double Radius { get; set; } = 3.5;
        public bool Connection { get; set; }
        public LDMorphologyOperation Operation { get; set; } = LDMorphologyOperation.closing;

        public LDMorphologyCircleTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (Radius <= 0)
            {
                return NodeResult.Fail("圆形形态学半径必须大于 0");
            }

            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            HObject output;
            switch (Operation)
            {
                case LDMorphologyOperation.dilation:
                    HOperatorSet.DilationCircle(region, out output, Radius);
                    break;
                case LDMorphologyOperation.erosion:
                    HOperatorSet.ErosionCircle(region, out output, Radius);
                    break;
                case LDMorphologyOperation.opening:
                    HOperatorSet.OpeningCircle(region, out output, Radius);
                    break;
                default:
                    HOperatorSet.ClosingCircle(region, out output, Radius);
                    break;
            }
            if (Connection)
            {
                HOperatorSet.Union1(output, out HObject union);
                output.Dispose();
                HOperatorSet.Connection(union, out output);
                union.Dispose();
            }
            HOperatorSet.CountObj(output, out HTuple count);
            if (count.I == 0)
            {
                output.Dispose();
                return NodeResult.Fail("圆形形态学结果为空");
            }
            SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(output), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding 圆形形态学] {Operation}，输出区域数={count.I}");
            return NodeResult.Ok;
        }
    }

    [ToolboxTool("03 区域处理", "Region 特征值", Id = "ldwelding.region-features", DefaultModuleName = "Region特征")]
    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstValue", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDRegionFeaturesTool : ToolBase
    {
        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        public string Features { get; set; } = "area,row,column";

        public LDRegionFeaturesTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
            var values = new System.Collections.Generic.List<double>();
            foreach (string feature in LDHalconTupleConvert.SplitCsv(Features))
            {
                HOperatorSet.RegionFeatures(region, feature, out HTuple tuple);
                values.AddRange(LDHalconTupleConvert.ToDoubles(tuple));
            }

            SetOutput(ctx, Variable.Array(ModuleName, "Values", VariableType.Double, values));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstValue", VariableType.Double, values.Count > 0 ? values[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, values.Count));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding Region 特征值] {Features}，输出 {values.Count} 个数值");
            return NodeResult.Ok;
        }
    }

    internal static class LDHalconTupleConvert
    {
        public static string[] SplitCsv(string text)
        {
            return (text ?? string.Empty).Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public static System.Collections.Generic.IEnumerable<double> ToDoubles(HTuple tuple)
        {
            if (tuple == null || tuple.Length == 0)
            {
                yield break;
            }
            for (int i = 0; i < tuple.Length; i++)
            {
                if (tuple[i].Type == HTupleType.INTEGER || tuple[i].Type == HTupleType.LONG || tuple[i].Type == HTupleType.DOUBLE)
                {
                    yield return tuple[i].D;
                }
            }
        }
    }

}
