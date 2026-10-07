using HalconDotNet;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 把 XLD 轮廓解释为直线 / 线段（交点计算、几何关系测量共用）：
    /// 两点轮廓直接取首尾点（拟合直线、直线测量的输出）；多点轮廓先做直线拟合，避免外接矩形把反斜线取成另一条对角线。
    /// </summary>
    internal static class XldLineHelper
    {
        public static int Count(HObject xld)
        {
            HOperatorSet.CountObj(xld, out HTuple count);
            return count.I;
        }

        /// <summary>取第 index 条轮廓（从 0 开始）代表的直线端点；失败时 error 以空格开头，便于拼在输入名后面。</summary>
        public static bool TryGetLine(HObject xld, int index, out double row1, out double column1,
            out double row2, out double column2, out string error)
        {
            row1 = column1 = row2 = column2 = double.NaN;
            error = null;
            int count = Count(xld);
            if (count == 0)
            {
                error = " XLD 为空";
                return false;
            }
            if (index < 0 || index >= count)
            {
                error = $" 没有第 {index} 条轮廓（共 {count} 条）";
                return false;
            }
            HOperatorSet.SelectObj(xld, out HObject contour, index + 1);
            try
            {
                HOperatorSet.GetContourXld(contour, out HTuple rows, out HTuple cols);
                if (rows.Length < 2)
                {
                    error = " 轮廓点数不足 2 个";
                    return false;
                }
                if (rows.Length == 2)
                {
                    row1 = rows[0].D;
                    column1 = cols[0].D;
                    row2 = rows[1].D;
                    column2 = cols[1].D;
                    return true;
                }
                HOperatorSet.FitLineContourXld(contour, "tukey", -1, 0, 5, 2,
                    out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2, out _, out _, out _);
                if (r1.Length == 0)
                {
                    error = " 直线拟合失败";
                    return false;
                }
                row1 = r1.D;
                column1 = c1.D;
                row2 = r2.D;
                column2 = c2.D;
                return true;
            }
            finally
            {
                contour.Dispose();
            }
        }

        /// <summary>在各点生成十字标记（交点、垂足的叠加显示）；没有点时返回空对象。</summary>
        public static HObject Crosses(System.Collections.Generic.IList<double> rows, System.Collections.Generic.IList<double> columns)
        {
            if (rows.Count == 0)
            {
                HOperatorSet.GenEmptyObj(out HObject empty);
                return empty;
            }
            var r = new double[rows.Count];
            var c = new double[rows.Count];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = rows[i];
                c[i] = columns[i];
            }
            HOperatorSet.GenCrossContourXld(out HObject cross, new HTuple(r), new HTuple(c), 15, 0.57);
            return cross;
        }

        /// <summary>把若干线段（起点、终点）合成一个 XLD（每段一条轮廓），用于最近点对、垂线的叠加显示。</summary>
        public static HObject Segments(System.Collections.Generic.IList<double> rows1, System.Collections.Generic.IList<double> columns1,
            System.Collections.Generic.IList<double> rows2, System.Collections.Generic.IList<double> columns2)
        {
            HOperatorSet.GenEmptyObj(out HObject segments);
            try
            {
                for (int i = 0; i < rows1.Count; i++)
                {
                    HOperatorSet.GenContourPolygonXld(out HObject segment, new HTuple(rows1[i], rows2[i]), new HTuple(columns1[i], columns2[i]));
                    HOperatorSet.ConcatObj(segments, segment, out HObject joined);
                    segment.Dispose();
                    segments.Dispose();
                    segments = joined;
                }
                return segments;
            }
            catch
            {
                segments.Dispose();
                throw;
            }
        }
    }
}
