using System;
using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    [ToolOutput("Codes", VariableKind.Array, VariableType.String)]
    [ToolOutput("FirstCode", VariableKind.Single, VariableType.String)]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class Barcode1DTool : ToolBase
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("ROI区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        public string CodeType { get; set; } = "auto";

        public Barcode1DTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject runImage = image;
            bool ownsReduced = false;
            if (!string.IsNullOrWhiteSpace(RegionPath))
            {
                HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
                HOperatorSet.ReduceDomain(image, region, out runImage);
                ownsReduced = true;
            }

            HOperatorSet.CreateBarCodeModel(new HTuple(), new HTuple(), out HTuple handle);
            try
            {
                HOperatorSet.FindBarCode(runImage, out HObject symbolRegions, handle, CodeType, out HTuple codes);
                var strings = new List<string>();
                for (int i = 0; i < codes.Length; i++)
                {
                    strings.Add(codes[i].S);
                }
                SetOutput(ctx, Variable.Array(ModuleName, "Codes", VariableType.String, strings));
                SetOutput(ctx, Variable.Single(ModuleName, "FirstCode", VariableType.String, strings.Count > 0 ? strings[0] : string.Empty));
                SetOutput(ctx, Variable.Object(ModuleName, "Region", new HalconRegion(symbolRegions), strings.Count));
                SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, strings.Count));
                ctx.AddLog(FlowLogLevel.Info, $"[一维码] 识别数量={strings.Count}");
                return strings.Count > 0 ? NodeResult.Ok : NodeResult.Fail("未识别到一维码");
            }
            finally
            {
                HOperatorSet.ClearBarCodeModel(handle);
                if (ownsReduced)
                {
                    runImage.Dispose();
                }
            }
        }
    }
}
