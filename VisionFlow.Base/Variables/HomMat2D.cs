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

        public static HomMat2D FromScaledPose(double row, double col, double angle, double scale)
        {
            HOperatorSet.HomMat2dIdentity(out HTuple hom);
            HOperatorSet.HomMat2dScale(hom, scale, scale, 0, 0, out hom);
            HOperatorSet.HomMat2dRotate(hom, angle, 0, 0, out hom);
            HOperatorSet.HomMat2dTranslate(hom, row, col, out hom);
            return new HomMat2D(hom);
        }

        /// <summary>
        /// 由基准位姿到带缩放的目标位姿创建相似变换矩阵：先刚体对齐（位姿1 → 位姿2），再以目标点为中心等比缩放。
        /// </summary>
        public static HomMat2D FromPosesScaled(double row1, double col1, double angle1,
            double row2, double col2, double angle2, double scale)
        {
            HOperatorSet.VectorAngleToRigid(row1, col1, angle1, row2, col2, angle2, out HTuple hom);
            HOperatorSet.HomMat2dScale(hom, scale, scale, row2, col2, out hom);
            return new HomMat2D(hom);
        }

        /// <summary>等比缩放系数（线性部分行列式绝对值的平方根），刚体变换为 1。</summary>
        public double ScaleFactor
        {
            get
            {
                double[] d = Data.DArr;
                return Math.Sqrt(Math.Abs(d[0] * d[4] - d[1] * d[3]));
            }
        }

        /// <summary>旋转量（HALCON 约定，逆时针为正）：方向角 phi 经本矩阵变换后变为 phi + RotationAngle。</summary>
        public double RotationAngle
        {
            get
            {
                TransformPose(0, 0, 0, out _, out _, out double phi);
                return phi;
            }
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
        /// HALCON 方向约定：行轴向下、角度逆时针为正，方向向量(行,列) = (-sin(phi), cos(phi))。
        /// </summary>
        public void TransformPose(double row, double col, double phi,
            out double outRow, out double outCol, out double outPhi)
        {
            TransformPoint(row, col, out outRow, out outCol);
            TransformPoint(row - Math.Sin(phi), col + Math.Cos(phi), out double dirRow, out double dirCol);
            outPhi = DirectionToPhi(dirRow - outRow, dirCol - outCol);
        }

        /// <summary>HALCON 约定下，由方向向量(行,列)求角度。</summary>
        public static double DirectionToPhi(double deltaRow, double deltaColumn)
        {
            return Math.Atan2(-deltaRow, deltaColumn);
        }

        /// <summary>HALCON 约定下，以 (row, col) 为圆心、半径 radius、角度 phi 的圆周点。</summary>
        public static void PointOnCircle(double row, double col, double radius, double phi,
            out double pointRow, out double pointCol)
        {
            pointRow = row - radius * Math.Sin(phi);
            pointCol = col + radius * Math.Cos(phi);
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
