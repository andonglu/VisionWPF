using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>检测格判定状态（字符串，便于 IfElse 直接与常量比较）。</summary>
    public static class ZoneState
    {
        public const string OK = "OK";
        public const string Wrong = "错误";
        public const string Missing = "缺失";
    }

    /// <summary>单个检测格的检测结果。</summary>
    public sealed class ZoneResult
    {
        /// <summary>检测格序号（0 起，与检测格区域中的对象顺序一致）。</summary>
        public int Index { get; set; }
        /// <summary>格内目标面积（目标区域与检测格的交集）。</summary>
        public double Area { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        /// <summary>格内目标的最小灰度（未接入图像或格内无目标时为 NaN）。</summary>
        public double MinGray { get; set; }
        /// <summary>格内目标的最大灰度（未接入图像或格内无目标时为 NaN）。</summary>
        public double MaxGray { get; set; }
        /// <summary>判定状态：OK / 错误 / 缺失（见 <see cref="ZoneState"/>）。</summary>
        public string State { get; set; }
        /// <summary>判定原因（OK 时为空）。</summary>
        public string Reason { get; set; }

        public override string ToString()
        {
            return $"#{Index} {State} 面积={Area:F0}{(double.IsNaN(MinGray) ? string.Empty : $", 灰度=[{MinGray:F0},{MaxGray:F0}]")}"
                + (string.IsNullOrEmpty(Reason) ? string.Empty : $"（{Reason}）");
        }
    }

    /// <summary>
    /// 分区检测（完整性检查）：检测格区域中的每个对象是一个格子（如泡罩、穴位、针脚位），
    /// 目标区域是分割出的目标（如药片）。逐格求交集，按面积与灰度判定 OK / 错误 / 缺失，并汇总数量。
    /// 对应 HALCON 示例 check_blister 中 intersection → area_center → min_max_gray 的逐格判定。
    /// 检测格通常来自“手动 Region”（输出模式 PerShape，可用阵列生成），顺序即格子序号。
    /// </summary>
    [ToolOutput("Zones", VariableKind.Array, VariableType.Object, ElementClrType = typeof(ZoneResult),
        Members = new[] { "Index", "Area", "Row", "Column", "MinGray", "MaxGray", "State", "Reason" })]
    [ToolOutput("Areas", VariableKind.Array, VariableType.Double)]
    [ToolOutput("ZoneCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("OkCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("WrongCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("MissingCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("AllOk", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("TargetRegion", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("WrongRegion", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("MissingRegion", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    public sealed class ZoneInspectTool : ToolBase
    {
        [InputRef("检测格区域", typeof(HalconRegion))]
        public string ZonePath { get; set; }

        [InputRef("目标区域", typeof(HalconRegion))]
        public string TargetPath { get; set; }

        /// <summary>灰度判定用图像（可选；配置了灰度上下限时必填）。</summary>
        [InputRef("灰度图像", typeof(HalconImage), Optional = true)]
        public string ImagePath { get; set; }

        /// <summary>格内目标面积不大于此值视为缺失。</summary>
        public double MissingArea { get; set; }
        /// <summary>格内目标面积下限（小于则错误）；0 表示不判。</summary>
        public double MinArea { get; set; }
        /// <summary>格内目标面积上限（大于则错误）；0 表示不判。</summary>
        public double MaxArea { get; set; }
        /// <summary>格内目标最小灰度须不小于此值，否则错误；-1 表示不判。</summary>
        public double MinGrayLimit { get; set; } = -1;
        /// <summary>格内目标最大灰度须不大于此值，否则错误；-1 表示不判。</summary>
        public double MaxGrayLimit { get; set; } = -1;
        /// <summary>min_max_gray 的 Percent（0~50，用于排除极端灰度）。</summary>
        public double GrayPercent { get; set; }

        public ZoneInspectTool(string moduleName) : base(moduleName)
        {
        }

        private bool UsesGray
        {
            get { return MinGrayLimit >= 0 || MaxGrayLimit >= 0; }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (GrayPercent < 0 || GrayPercent > 50)
            {
                return NodeResult.Fail("分区检测 GrayPercent 必须在 0 到 50 之间");
            }
            if (UsesGray && string.IsNullOrWhiteSpace(ImagePath))
            {
                return NodeResult.Fail("分区检测配置了灰度上下限，必须指定灰度图像");
            }

            HObject zones = Input<HalconRegion>(ctx, ZonePath).Object;
            HObject targets = Input<HalconRegion>(ctx, TargetPath).Object;
            HObject image = string.IsNullOrWhiteSpace(ImagePath) ? null : Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.CountObj(zones, out HTuple zoneCount);
            if (zoneCount.I == 0)
            {
                return NodeResult.Fail("分区检测的检测格区域为空，请检查手动 Region 配置");
            }

            var results = new List<ZoneResult>();
            HOperatorSet.Union1(targets, out HObject targetUnion);
            HOperatorSet.GenEmptyObj(out HObject zoneTargets);
            HOperatorSet.GenEmptyObj(out HObject wrongRegion);
            HOperatorSet.GenEmptyObj(out HObject missingRegion);
            bool completed = false;
            try
            {
                for (int i = 0; i < zoneCount.I; i++)
                {
                    HOperatorSet.SelectObj(zones, out HObject zone, i + 1);
                    HOperatorSet.Intersection(zone, targetUnion, out HObject target);
                    try
                    {
                        ZoneResult result = Inspect(i, target, image);
                        results.Add(result);
                        Append(ref zoneTargets, target);
                        if (result.State == ZoneState.Missing)
                        {
                            Append(ref missingRegion, zone);
                        }
                        else if (result.State == ZoneState.Wrong)
                        {
                            Append(ref wrongRegion, target);
                        }
                    }
                    finally
                    {
                        zone.Dispose();
                        target.Dispose();
                    }
                }

                int ok = results.Count(r => r.State == ZoneState.OK);
                int wrong = results.Count(r => r.State == ZoneState.Wrong);
                int missing = results.Count(r => r.State == ZoneState.Missing);
                SetOutput(ctx, Variable.Array(ModuleName, "Zones", VariableType.Object, results));
                SetOutput(ctx, Variable.Array(ModuleName, "Areas", VariableType.Double, results.Select(r => r.Area)));
                SetOutput(ctx, Variable.Single(ModuleName, "ZoneCount", VariableType.Int, results.Count));
                SetOutput(ctx, Variable.Single(ModuleName, "OkCount", VariableType.Int, ok));
                SetOutput(ctx, Variable.Single(ModuleName, "WrongCount", VariableType.Int, wrong));
                SetOutput(ctx, Variable.Single(ModuleName, "MissingCount", VariableType.Int, missing));
                SetOutput(ctx, Variable.Single(ModuleName, "AllOk", VariableType.Bool, wrong == 0 && missing == 0));
                SetOutput(ctx, Variable.Object(ModuleName, "TargetRegion", new HalconRegion(zoneTargets), results.Count));
                SetOutput(ctx, Variable.Object(ModuleName, "WrongRegion", new HalconRegion(wrongRegion), wrong));
                SetOutput(ctx, Variable.Object(ModuleName, "MissingRegion", new HalconRegion(missingRegion), missing));
                completed = true;
                ctx.AddLog(FlowLogLevel.Info, $"[分区检测] 格数={results.Count}, OK={ok}, 错误={wrong}, 缺失={missing}");
                foreach (ZoneResult result in results.Where(r => r.State != ZoneState.OK))
                {
                    ctx.AddLog(FlowLogLevel.Info, $"[分区检测] {result}");
                }
                return NodeResult.Ok;
            }
            finally
            {
                targetUnion.Dispose();
                if (!completed)
                {
                    zoneTargets.Dispose();
                    wrongRegion.Dispose();
                    missingRegion.Dispose();
                }
            }
        }

        private ZoneResult Inspect(int index, HObject target, HObject image)
        {
            HOperatorSet.AreaCenter(target, out HTuple area, out HTuple row, out HTuple column);
            var result = new ZoneResult
            {
                Index = index,
                Area = area.D,
                Row = area.D > 0 ? row.D : double.NaN,
                Column = area.D > 0 ? column.D : double.NaN,
                MinGray = double.NaN,
                MaxGray = double.NaN,
                State = ZoneState.OK
            };
            if (result.Area <= MissingArea)
            {
                result.State = ZoneState.Missing;
                result.Reason = $"面积 {result.Area:F0} ≤ {MissingArea:F0}";
                return result;
            }

            if (image != null && result.Area > 0)
            {
                HOperatorSet.MinMaxGray(target, image, GrayPercent, out HTuple min, out HTuple max, out _);
                result.MinGray = min.D;
                result.MaxGray = max.D;
            }

            var reasons = new List<string>();
            if (MinArea > 0 && result.Area < MinArea)
            {
                reasons.Add($"面积 {result.Area:F0} < {MinArea:F0}");
            }
            if (MaxArea > 0 && result.Area > MaxArea)
            {
                reasons.Add($"面积 {result.Area:F0} > {MaxArea:F0}");
            }
            if (MinGrayLimit >= 0 && result.MinGray < MinGrayLimit)
            {
                reasons.Add($"最小灰度 {result.MinGray:F0} < {MinGrayLimit:F0}");
            }
            if (MaxGrayLimit >= 0 && result.MaxGray > MaxGrayLimit)
            {
                reasons.Add($"最大灰度 {result.MaxGray:F0} > {MaxGrayLimit:F0}");
            }
            if (reasons.Count > 0)
            {
                result.State = ZoneState.Wrong;
                result.Reason = string.Join("；", reasons);
            }
            return result;
        }

        private static void Append(ref HObject target, HObject item)
        {
            HOperatorSet.ConcatObj(target, item, out HObject combined);
            target.Dispose();
            target = combined;
        }
    }
}
