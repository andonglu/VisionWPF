using System;
using HalconDotNet;

namespace VisionFlow.Variables
{
    /// <summary>
    /// 二维变换矩阵（HomMat2D，6 元素仿射矩阵 [a, b, tx, c, d, ty]）。
    /// 专门类型：虽然底层数据是 HTuple，但它与普通 HTuple 是不同类型——
    /// 工具的输入输出声明、编辑器的引用选择都按"变换矩阵"识别，
    /// 一般 HTuple 不会出现在矩阵参数的候选中，反之亦然。
    /// </summary>
    public sealed class HomMat2D
    {
        /// <summary>矩阵数据（6 元素）。</summary>
        public HTuple Data { get; private set; }

        public HomMat2D(HTuple data)
        {
            if (data == null || data.Length != 6)
            {
                throw new ArgumentException("变换矩阵必须是 6 元素的 HTuple", nameof(data));
            }
            Data = data;
        }

        /// <summary>单位矩阵。</summary>
        public static HomMat2D Identity
        {
            get { return new HomMat2D(new HTuple(1.0, 0.0, 0.0, 0.0, 1.0, 0.0)); }
        }

        /// <summary>由两组位姿创建刚体变换矩阵（位姿1 → 位姿2，vector_angle_to_rigid）。</summary>
        public static HomMat2D FromPoses(double row1, double col1, double angle1,
            double row2, double col2, double angle2)
        {
            HOperatorSet.VectorAngleToRigid(row1, col1, angle1, row2, col2, angle2, out HTuple hom);
            return new HomMat2D(hom);
        }

        /// <summary>变换一个点（affine_trans_point_2d）。</summary>
        public void TransformPoint(double row, double col, out double outRow, out double outCol)
        {
            HOperatorSet.AffineTransPoint2d(Data, row, col, out HTuple r, out HTuple c);
            outRow = r.D;
            outCol = c.D;
        }

        /// <summary>
        /// 变换一个位姿（点 + 方向角）。
        /// 角度通过"中心 + 方向向量点"两点变换求得，刚体/相似变换均成立。
        /// Halcon 方向约定：方向向量(行,列) = (sin(phi), cos(phi))。
        /// </summary>
        public void TransformPose(double row, double col, double phi,
            out double outRow, out double outCol, out double outPhi)
        {
            TransformPoint(row, col, out outRow, out outCol);
            TransformPoint(row + Math.Sin(phi), col + Math.Cos(phi), out double dirRow, out double dirCol);
            outPhi = Math.Atan2(dirRow - outRow, dirCol - outCol);
        }

        /// <summary>返回当前仿射矩阵的逆矩阵。</summary>
        public HomMat2D Inverted()
        {
            HOperatorSet.HomMat2dInvert(Data, out HTuple inverted);
            return new HomMat2D(inverted);
        }

        public double[] ToArray()
        {
            return Data.DArr;
        }

        public override string ToString()
        {
            double[] d = Data.DArr;
            return $"HomMat2D[{d[0]:F3}, {d[1]:F3}, {d[2]:F2} | {d[3]:F3}, {d[4]:F3}, {d[5]:F2}]";
        }
    }
}
