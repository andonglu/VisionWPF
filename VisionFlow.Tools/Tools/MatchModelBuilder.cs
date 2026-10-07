using System;
using System.IO;
using HalconDotNet;

namespace VisionFlow.Tools
{
    /// <summary>模板匹配的模型来源（MT-02）。</summary>
    public enum MatchModelSource
    {
        /// <summary>图像 ROI（默认，即原有示教方式）。</summary>
        ImageRoi,
        /// <summary>上游 XLD 轮廓输出。</summary>
        Xld,
        /// <summary>DXF 文件（只在示教时读取）。</summary>
        Dxf
    }

    /// <summary>
    /// 模板示教的公共建模步骤（MT-02，与界面无关，编辑窗口与测试共用）：读 DXF、选轮廓、从 XLD 建模、写模型原点。
    /// HALCON 22.11 实测（计划第 12 节）：create_shape_model_xld / create_scaled_shape_model_xld 接受多条轮廓，
    /// 全部轮廓共同组成模板；XLD 建模的度量只能是 ignore_local_polarity（其他值报 #1306）。
    /// </summary>
    public static class MatchModelBuilder
    {
        /// <summary>XLD / DXF 建模唯一可用的度量。</summary>
        public const string XldMetric = "ignore_local_polarity";

        /// <summary>读取 DXF 轮廓；文件不存在、解析失败或没有轮廓时给出明确错误。调用方负责释放返回值。</summary>
        public static HObject ReadDxf(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("未选择 DXF 文件");
            }
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("DXF 文件不存在：" + path);
            }
            HObject contours;
            try
            {
                HOperatorSet.ReadContourXldDxf(out contours, path, new HTuple(), new HTuple(), out _);
            }
            catch (HalconException ex)
            {
                throw new InvalidOperationException($"DXF 文件解析失败：{path}，{ex.Message}", ex);
            }
            HOperatorSet.CountObj(contours, out HTuple count);
            if (count.I == 0)
            {
                contours.Dispose();
                throw new InvalidOperationException("DXF 文件中没有可用的轮廓：" + path);
            }
            return contours;
        }

        /// <summary>
        /// 选择建模轮廓：index &lt; 0 取全部轮廓（共同组成模板），否则取第 index 条（0 起）。
        /// 轮廓为空或序号越界时报错。返回新对象，调用方负责释放。
        /// </summary>
        public static HObject SelectContours(HObject contours, int index)
        {
            if (contours == null || !contours.IsInitialized())
            {
                throw new InvalidOperationException("没有可用于建模的 XLD 轮廓");
            }
            HOperatorSet.CountObj(contours, out HTuple count);
            if (count.I == 0)
            {
                throw new InvalidOperationException("XLD 轮廓为空，无法建模");
            }
            if (index < 0)
            {
                return contours.CopyObj(1, -1);
            }
            if (index >= count.I)
            {
                throw new InvalidOperationException($"轮廓序号 {index} 超出范围（共 {count.I} 条，序号 0 ~ {count.I - 1}，-1 表示全部）");
            }
            HOperatorSet.SelectObj(contours, out HObject selected, index + 1);
            return selected;
        }

        /// <summary>从 XLD 创建形状模型（度量固定为 ignore_local_polarity）。</summary>
        public static HTuple CreateShapeModelXld(HObject contours, HTuple numLevels, double angleStart, double angleExtent,
            HTuple angleStep, string optimization, HTuple minContrast)
        {
            HOperatorSet.CreateShapeModelXld(contours, AutoLevels(numLevels), angleStart, angleExtent, angleStep,
                optimization, XldMetric, minContrast, out HTuple modelId);
            return modelId;
        }

        /// <summary>从 XLD 创建等比缩放形状模型（度量固定为 ignore_local_polarity）。</summary>
        public static HTuple CreateScaledShapeModelXld(HObject contours, HTuple numLevels, double angleStart, double angleExtent,
            HTuple angleStep, double scaleMin, double scaleMax, HTuple scaleStep, string optimization, HTuple minContrast)
        {
            HOperatorSet.CreateScaledShapeModelXld(contours, AutoLevels(numLevels), angleStart, angleExtent, angleStep,
                scaleMin, scaleMax, scaleStep, optimization, XldMetric, minContrast, out HTuple modelId);
            return modelId;
        }

        /// <summary>
        /// 金字塔层数 ≤ 0 视为自动：示教页默认值 0 可直接传给 create_shape_model，但 *_xld 建模算子不接受 0（22.11 实测报 #1301）。
        /// </summary>
        private static HTuple AutoLevels(HTuple numLevels)
        {
            if (numLevels == null || numLevels.Length == 0)
            {
                return new HTuple("auto");
            }
            return numLevels.Type != HTupleType.STRING && numLevels.L <= 0 ? new HTuple("auto") : numLevels;
        }

        /// <summary>
        /// 把模型原点（相对模板参考点的偏移）写入形状模型 / 灰度模型。偏移为 (0, 0) 时不调用算子，
        /// 模型字节与未设置原点时完全相同（第一批起的示教行为不变）。
        /// </summary>
        public static void ApplyOrigin(HTuple modelId, bool ncc, double row, double column)
        {
            if (row == 0 && column == 0)
            {
                return;
            }
            SetOrigin(modelId, ncc, row, column);
        }

        /// <summary>设置模型原点（绝对值，相对模板参考点）；与 <see cref="ApplyOrigin"/> 不同，偏移为 0 时也调用算子（用于把已有原点改回参考点）。</summary>
        public static void SetOrigin(HTuple modelId, bool ncc, double row, double column)
        {
            if (ncc)
            {
                HOperatorSet.SetNccModelOrigin(modelId, row, column);
            }
            else
            {
                HOperatorSet.SetShapeModelOrigin(modelId, row, column);
            }
        }

        /// <summary>读取模型当前的原点偏移。</summary>
        public static void GetOrigin(HTuple modelId, bool ncc, out double row, out double column)
        {
            HTuple r;
            HTuple c;
            if (ncc)
            {
                HOperatorSet.GetNccModelOrigin(modelId, out r, out c);
            }
            else
            {
                HOperatorSet.GetShapeModelOrigin(modelId, out r, out c);
            }
            row = r.D;
            column = c.D;
        }

        /// <summary>
        /// 由图像上点选的位置计算新的原点偏移：模板参考点 = 示教匹配位置 − 当前原点（示教角度近 0 时成立），
        /// 新原点 = 点选位置 − 参考点。
        /// </summary>
        public static void OriginFromPickedPoint(double pickedRow, double pickedColumn, double matchRow, double matchColumn,
            double currentOriginRow, double currentOriginColumn, out double originRow, out double originColumn)
        {
            originRow = pickedRow - (matchRow - currentOriginRow);
            originColumn = pickedColumn - (matchColumn - currentOriginColumn);
        }
    }
}
