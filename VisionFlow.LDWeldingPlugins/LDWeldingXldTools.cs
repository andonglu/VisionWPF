using System;
using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.LDWeldingPlugins
{
    public enum LDXldSegmentMode
    {
        lines,
        lines_circles
    }

    [ToolboxTool("04 XLD轮廓", "边缘提取", Id = "ldwelding.contour-create", DefaultModuleName = "边缘提取")]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDContourCreateTool : LDXldToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public string Filter { get; set; } = "canny";
        public double Sigma { get; set; } = 1.0;
        public int Low { get; set; } = 20;
        public int High { get; set; } = 40;

        public LDContourCreateTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.EdgesColorSubPix(image, out HObject xld, Filter, Sigma, Low, High);
            return SetXldOutput(ctx, xld, $"[LDWelding 边缘提取] {Filter}, Sigma={Sigma}, Low={Low}, High={High}");
        }
    }

    [ToolboxTool("04 XLD轮廓", "边缘选择", Id = "ldwelding.select-contour", DefaultModuleName = "边缘选择")]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDSelectContourTool : LDXldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Features { get; set; } = "contlength";
        public string Operation { get; set; } = "and";
        public double Min { get; set; } = 10;
        public double Max { get; set; } = 999999;

        public LDSelectContourTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            string[] features = LDHalconTupleConvert.SplitCsv(Features);
            var mins = new HTuple();
            var maxs = new HTuple();
            for (int i = 0; i < Math.Max(1, features.Length); i++)
            {
                mins = mins.TupleConcat(Min);
                maxs = maxs.TupleConcat(Max);
            }
            HOperatorSet.SelectShapeXld(xld, out HObject selected, features, Operation, mins, maxs);
            return SetXldOutput(ctx, selected, $"[LDWelding 边缘选择] {Features} ∈ [{Min}, {Max}]");
        }
    }

    [ToolboxTool("04 XLD轮廓", "边缘合并", Id = "ldwelding.concat-xld", DefaultModuleName = "边缘合并")]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDConcatXldTool : LDXldToolBase
    {
        [InputRef("XLD1", typeof(HalconXld))]
        public string XldPath1 { get; set; }

        [InputRef("XLD2", typeof(HalconXld))]
        public string XldPath2 { get; set; }

        public LDConcatXldTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld1 = Input<HalconXld>(ctx, XldPath1).Object;
            HObject xld2 = Input<HalconXld>(ctx, XldPath2).Object;
            HOperatorSet.ConcatObj(xld1, xld2, out HObject output);
            return SetXldOutput(ctx, output, "[LDWelding 边缘合并]");
        }
    }

    [ToolboxTool("04 XLD轮廓", "XLD 分割", Id = "ldwelding.segment-xld", DefaultModuleName = "XLD分割")]
    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDSegmentXldTool : LDXldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public LDXldSegmentMode Mode { get; set; } = LDXldSegmentMode.lines_circles;
        public int SmoothCont { get; set; } = 5;
        public double MaxLineDist1 { get; set; } = 4.0;
        public double MaxLineDist2 { get; set; } = 2.0;

        public LDSegmentXldTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            HOperatorSet.SegmentContoursXld(xld, out HObject output, Mode.ToString(), SmoothCont, MaxLineDist1, MaxLineDist2);
            return SetXldOutput(ctx, output, $"[LDWelding XLD 分割] {Mode}");
        }
    }

    [ToolboxTool("04 XLD轮廓", "XLD 特征值", Id = "ldwelding.xld-features", DefaultModuleName = "XLD特征")]
    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstValue", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class LDXldFeaturesTool : ToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Features { get; set; } = "contlength";

        public LDXldFeaturesTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            var values = new List<double>();
            foreach (string feature in LDHalconTupleConvert.SplitCsv(Features))
            {
                values.AddRange(GetXldFeature(xld, feature));
            }

            SetOutput(ctx, Variable.Array(ModuleName, "Values", VariableType.Double, values));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstValue", VariableType.Double, values.Count > 0 ? values[0] : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, values.Count));
            ctx.AddLog(FlowLogLevel.Info, $"[LDWelding XLD 特征值] {Features}，输出 {values.Count} 个数值");
            return NodeResult.Ok;
        }

        private static IEnumerable<double> GetXldFeature(HObject xld, string feature)
        {
            string normalized = (feature ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "contlength":
                case "length":
                    HOperatorSet.LengthXld(xld, out HTuple length);
                    return LDHalconTupleConvert.ToDoubles(length);
                case "area":
                case "row":
                case "column":
                    HOperatorSet.AreaCenterXld(xld, out HTuple area, out HTuple row, out HTuple column, out _);
                    if (normalized == "row") return LDHalconTupleConvert.ToDoubles(row);
                    if (normalized == "column") return LDHalconTupleConvert.ToDoubles(column);
                    return LDHalconTupleConvert.ToDoubles(area);
                case "point_num":
                case "points":
                    HOperatorSet.ContourPointNumXld(xld, out HTuple points);
                    return LDHalconTupleConvert.ToDoubles(points);
                default:
                    HOperatorSet.GetContourGlobalAttribXld(xld, feature, out HTuple attrib);
                    return LDHalconTupleConvert.ToDoubles(attrib);
            }
        }
    }

    public abstract class LDXldToolBase : ToolBase
    {
        protected LDXldToolBase(string moduleName) : base(moduleName)
        {
        }

        protected NodeResult SetXldOutput(FlowContext ctx, HObject xld, string logMessage)
        {
            HOperatorSet.CountObj(xld, out HTuple count);
            if (count.I == 0)
            {
                xld.Dispose();
                return NodeResult.Fail(logMessage + " 结果为空");
            }
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(xld), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            ctx.AddLog(FlowLogLevel.Info, $"{logMessage}，输出轮廓数={count.I}");
            return NodeResult.Ok;
        }
    }
}
