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
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class Barcode1DTool : ToolBase, INotFoundPolicy
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("ROI区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        public string CodeType { get; set; } = "auto";

        /// <summary>未识别到条码时是否失败（默认 true）；关闭后输出 Found=false、Count=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

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

            // 条码模型会保存本次识别结果，不跨运行复用（创建耗时约 0.1 ms），避免并发运行互相覆盖
            HTuple handle = null;
            try
            {
                HOperatorSet.CreateBarCodeModel(new HTuple(), new HTuple(), out handle);
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
                SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, strings.Count > 0));
                if (strings.Count == 0)
                {
                    return NotFoundOutcome.Resolve(ctx, this, "未识别到一维码");
                }
                ctx.AddLog(FlowLogLevel.Info, $"[一维码] 识别数量={strings.Count}");
                return NodeResult.Ok;
            }
            finally
            {
                if (handle != null)
                {
                    HOperatorSet.ClearBarCodeModel(handle);
                }
                if (ownsReduced)
                {
                    runImage.Dispose();
                }
            }
        }
    }
}
