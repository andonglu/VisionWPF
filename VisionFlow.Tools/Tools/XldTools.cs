using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    public enum XldSegmentMode
    {
        lines,
        lines_circles
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class ContourCreateTool : XldToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        public string Filter { get; set; } = "canny";
        public double Sigma { get; set; } = 1.0;
        public int Low { get; set; } = 20;
        public int High { get; set; } = 40;

        public ContourCreateTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.EdgesColorSubPix(image, out HObject xld, Filter, Sigma, Low, High);
            return SetXldOutput(ctx, xld, $"[边缘提取] {Filter}, Sigma={Sigma}, Low={Low}, High={High}");
        }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class SelectContourTool : XldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Features { get; set; } = "contlength";
        public string Operation { get; set; } = "and";
        /// <summary>所有特征共用的下限（未配置 MinValues 时使用）。</summary>
        public double Min { get; set; } = 10;
        /// <summary>所有特征共用的上限（未配置 MaxValues 时使用）。</summary>
        public double Max { get; set; } = 999999;
        /// <summary>按特征分别设置的下限（逗号分隔，数量须与 Features 一致）；为空时使用 Min。</summary>
        public string MinValues { get; set; }
        /// <summary>按特征分别设置的上限（逗号分隔，数量须与 Features 一致）；为空时使用 Max。</summary>
        public string MaxValues { get; set; }

        public SelectContourTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            string[] features = HalconTupleConvert.SplitCsv(Features);
            if (features.Length == 0)
            {
                return NodeResult.Fail("边缘选择未配置特征");
            }
            if (!TryBuildRange(MinValues, Min, features.Length, "MinValues", out HTuple mins, out string error)
                || !TryBuildRange(MaxValues, Max, features.Length, "MaxValues", out HTuple maxs, out error))
            {
                return NodeResult.Fail(error);
            }

            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            HOperatorSet.SelectShapeXld(xld, out HObject selected, features, Operation, mins, maxs);
            return SetXldOutput(ctx, selected, $"[边缘选择] {Features} ∈ [{mins}, {maxs}]");
        }

        private static bool TryBuildRange(string list, double fallback, int featureCount, string name,
            out HTuple range, out string error)
        {
            range = new HTuple();
            error = null;
            string[] parts = HalconTupleConvert.SplitCsv(list);
            if (parts.Length == 0)
            {
                for (int i = 0; i < featureCount; i++)
                {
                    range = range.TupleConcat(fallback);
                }
                return true;
            }
            if (parts.Length != featureCount)
            {
                error = $"边缘选择 {name} 数量（{parts.Length}）与特征数量（{featureCount}）不一致";
                return false;
            }
            foreach (string part in parts)
            {
                if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double value))
                {
                    error = $"边缘选择 {name} 中的“{part}”不是有效数字";
                    return false;
                }
                range = range.TupleConcat(value);
            }
            return true;
        }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class ConcatXldTool : XldToolBase
    {
        [InputRef("XLD1", typeof(HalconXld))]
        public string XldPath1 { get; set; }

        [InputRef("XLD2", typeof(HalconXld))]
        public string XldPath2 { get; set; }

        public ConcatXldTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld1 = Input<HalconXld>(ctx, XldPath1).Object;
            HObject xld2 = Input<HalconXld>(ctx, XldPath2).Object;
            HOperatorSet.ConcatObj(xld1, xld2, out HObject output);
            return SetXldOutput(ctx, output, "[边缘合并]");
        }
    }

    [ToolOutput("Xld", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class SegmentXldTool : XldToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public XldSegmentMode Mode { get; set; } = XldSegmentMode.lines_circles;
        public int SmoothCont { get; set; } = 5;
        public double MaxLineDist1 { get; set; } = 4.0;
        public double MaxLineDist2 { get; set; } = 2.0;

        public SegmentXldTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            HOperatorSet.SegmentContoursXld(xld, out HObject output, Mode.ToString(), SmoothCont, MaxLineDist1, MaxLineDist2);
            return SetXldOutput(ctx, output, $"[XLD 分割] {Mode}");
        }
    }

    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("FirstValue", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ObjectCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Features", VariableKind.Array, VariableType.Object, ElementClrType = typeof(FeatureValues),
        Members = new[] { "Name", "Count", "First", "Min", "Max", "Mean" })]
    public sealed class XldFeaturesTool : ToolBase
    {
        [InputRef("XLD", typeof(HalconXld))]
        public string XldPath { get; set; }

        public string Features { get; set; } = "contlength";

        public XldFeaturesTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject xld = Input<HalconXld>(ctx, XldPath).Object;
            HOperatorSet.CountObj(xld, out HTuple objectCount);
            var features = new List<FeatureValues>();
            foreach (string feature in HalconTupleConvert.SplitCsv(Features))
            {
                features.Add(FeatureValues.Create(feature, GetXldFeature(xld, feature).ToList()));
            }

            FeatureOutput.Set(ctx, ModuleName, features, objectCount.I);
            ctx.AddLog(FlowLogLevel.Info, $"[XLD 特征值] {Features}，对象数 {objectCount.I}，输出 {features.Sum(f => f.Count)} 个数值");
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
                    return HalconTupleConvert.ToDoubles(length);
                case "area":
                case "row":
                case "column":
                    HOperatorSet.AreaCenterXld(xld, out HTuple area, out HTuple row, out HTuple column, out _);
                    if (normalized == "row") return HalconTupleConvert.ToDoubles(row);
                    if (normalized == "column") return HalconTupleConvert.ToDoubles(column);
                    return HalconTupleConvert.ToDoubles(area);
                case "point_num":
                case "points":
                    HOperatorSet.ContourPointNumXld(xld, out HTuple points);
                    return HalconTupleConvert.ToDoubles(points);
                default:
                    HOperatorSet.GetContourGlobalAttribXld(xld, feature, out HTuple attrib);
                    return HalconTupleConvert.ToDoubles(attrib);
            }
        }
    }

    public abstract class XldToolBase : ToolBase, INotFoundPolicy
    {
        protected XldToolBase(string moduleName) : base(moduleName)
        {
        }

        /// <summary>结果为空时是否失败（默认 true）；关闭后输出 Found=false、Count=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        protected NodeResult SetXldOutput(FlowContext ctx, HObject xld, string logMessage)
        {
            HOperatorSet.CountObj(xld, out HTuple count);
            SetOutput(ctx, Variable.Object(ModuleName, "Xld", new HalconXld(xld), count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, count.I));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, count.I > 0));
            if (count.I == 0)
            {
                return NotFoundOutcome.Resolve(ctx, this, logMessage + " 结果为空");
            }
            ctx.AddLog(FlowLogLevel.Info, $"{logMessage}，输出轮廓数={count.I}");
            return NodeResult.Ok;
        }
    }
}
