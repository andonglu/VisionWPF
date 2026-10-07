using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 灰度投影的一项结果（一条曲线）。Profile 沿测量矩形长轴（Length1 方向，即 Phi 方向）从“中心 − Length1”到“中心 + Length1”
    /// 逐像素采样，每点为 Length2 宽度上的平均灰度（measure_projection，22.11 实测共 floor(2·Length1)+1 点）；Smooth &gt; 0 时为平滑后的曲线。
    /// MinPosition / MaxPosition 为该方向上的采样序号（0 起）。
    /// </summary>
    public sealed class OneDProjectionResult
    {
        /// <summary>定位结果序号。</summary>
        public int Index { get; set; }
        public bool Followed { get; set; }
        /// <summary>本次测量矩形（跟随后）。</summary>
        public double Row { get; set; }
        public double Column { get; set; }
        public double Phi { get; set; }
        public double Length1 { get; set; }
        public double Length2 { get; set; }
        public double[] Profile { get; set; }
        public double[] Derivative { get; set; }
        public double MinGray { get; set; }
        public double MaxGray { get; set; }
        public double MeanGray { get; set; }
        public int MinPosition { get; set; }
        public int MaxPosition { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")} {Profile?.Length ?? 0} 点，Min={MinGray:F2}@{MinPosition}, Max={MaxGray:F2}@{MaxPosition}, Mean={MeanGray:F2}";
        }
    }

    /// <summary>
    /// 灰度投影（MS-05）：gen_measure_rectangle2 + measure_projection 输出测量矩形长轴方向的灰度曲线及一阶导数（derivate_funct_1d），
    /// 用于观察边缘形态、调卡尺参数或直接按曲线判断（有无、亮度均匀性）。测量区域与跟随矩阵逻辑与一维卡尺相同。
    /// 多个定位矩阵时每个矩阵一条曲线（全部进入 Results），单值取最后一次成功的测量；测量矩形超出图像的矩阵按该项未找到处理
    /// （measure_projection 对图像外的像素按 0 计入且不报错，22.11 实测，因此由工具判定）。
    /// </summary>
    [ToolOutput("Profile", VariableKind.Array, VariableType.Double)]
    [ToolOutput("Derivative", VariableKind.Array, VariableType.Double)]
    [ToolOutput("MinGray", VariableKind.Single, VariableType.Double)]
    [ToolOutput("MaxGray", VariableKind.Single, VariableType.Double)]
    [ToolOutput("MeanGray", VariableKind.Single, VariableType.Double)]
    [ToolOutput("MinPosition", VariableKind.Single, VariableType.Int)]
    [ToolOutput("MaxPosition", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("FailedCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<OneDProjectionResult>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    public sealed class GrayProjectionFollowTool : FollowMeasureToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>卡尺边缘参数与灰度投影无关，侧栏不显示。</summary>
        private static readonly string[] CaliperParameters =
        {
            nameof(MeasureLength1), nameof(MeasureLength2), nameof(MeasureSigma), nameof(MeasureThreshold),
            nameof(MeasureTransition), nameof(MeasureSelect)
        };

        public double BaseRow { get; set; } = 100;
        public double BaseColumn { get; set; } = 150;
        public double BasePhi { get; set; }
        public double BaseLength1 { get; set; } = 80;
        public double BaseLength2 { get; set; } = 8;
        /// <summary>曲线高斯平滑系数（smooth_funct_1d_gauss）；0 表示不平滑。</summary>
        public double Smooth { get; set; }

        private OneDProjectionResult _last;

        public GrayProjectionFollowTool(string moduleName) : base(moduleName)
        {
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (Smooth < 0)
            {
                yield return new ToolConfigurationIssue(nameof(Smooth), "平滑系数不能小于 0（0 表示不平滑）");
            }
            if (BaseLength1 < 1)
            {
                yield return new ToolConfigurationIssue(nameof(BaseLength1), "测量矩形半长至少为 1（曲线至少 3 个采样点）");
            }
            if (!(BaseLength2 > 0))
            {
                yield return new ToolConfigurationIssue(nameof(BaseLength2), "测量矩形半宽必须大于 0");
            }
        }

        public bool IsParameterVisible(string propertyName)
        {
            return !CaliperParameters.Contains(propertyName);
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }
            return RunMeasurements(ctx, "灰度投影",
                (HObject image, HomMat2D matrix, int resultIndex, out HObject contour) =>
                    MeasureOne(ctx, image, matrix, resultIndex, out contour),
                null);
        }

        private NodeResult MeasureOne(FlowContext ctx, HObject image, HomMat2D matrix, int resultIndex, out HObject contour)
        {
            double row = BaseRow;
            double col = BaseColumn;
            double phi = BasePhi;
            double length1 = BaseLength1;
            double length2 = BaseLength2;
            bool followed = false;
            if (matrix != null)
            {
                matrix.TransformPose(BaseRow, BaseColumn, BasePhi, out row, out col, out phi);
                length1 *= matrix.ScaleFactor;
                length2 *= matrix.ScaleFactor;
                followed = true;
            }
            HOperatorSet.GenRectangle2ContourXld(out contour, row, col, phi, length1, length2);
            if (!TryComputeProfile(image, row, col, phi, length1, length2, Smooth, out double[] profile, out double[] derivative, out string error))
            {
                return NodeResult.Fail($"灰度投影失败（中心 {row:F1},{col:F1}）：{error}");
            }

            int minPosition = Array.IndexOf(profile, profile.Min());
            int maxPosition = Array.IndexOf(profile, profile.Max());
            var result = new OneDProjectionResult
            {
                Index = ResolveIndex(ctx, resultIndex),
                Followed = followed,
                Row = row,
                Column = col,
                Phi = phi,
                Length1 = length1,
                Length2 = length2,
                Profile = profile,
                Derivative = derivative,
                MinGray = profile[minPosition],
                MaxGray = profile[maxPosition],
                MeanGray = profile.Average(),
                MinPosition = minPosition,
                MaxPosition = maxPosition
            };
            _last = result;
            AddResult(ctx, ModuleName, result);
            ctx.AddLog(FlowLogLevel.Info, $"[灰度投影]{(followed ? "跟随" : "固定")} {result}");
            return NodeResult.Ok;
        }

        /// <summary>
        /// 计算一条灰度投影曲线（工具运行与编辑窗口预览共用）。测量矩形超出图像时返回 false（图像外像素会按 0 计入）；
        /// smooth &gt; 0 时高斯平滑，σ 相对曲线过大时 HALCON 报 #3022（22.11 实测 101 点约 σ ≤ 12.8，约为 (点数−1)/8），转成中文错误。
        /// 导数为平滑后曲线的一阶导数；平坦（全零 / 常数）曲线平滑与求导都不产生 NaN（22.11 实测）。
        /// </summary>
        public static bool TryComputeProfile(HObject image, double row, double column, double phi, double length1, double length2,
            double smooth, out double[] profile, out double[] derivative, out string error)
        {
            profile = new double[0];
            derivative = new double[0];
            error = null;
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            if (!RectangleInside(row, column, phi, length1, length2, width.D, height.D))
            {
                error = "测量矩形超出图像（图像外的像素会按 0 灰度计入，不予采用）";
                return false;
            }
            HOperatorSet.GenMeasureRectangle2(row, column, phi, length1, length2, width, height, "nearest_neighbor", out HTuple handle);
            HTuple gray;
            try
            {
                HOperatorSet.MeasureProjection(image, handle, out gray);
            }
            finally
            {
                HOperatorSet.CloseMeasure(handle);
            }
            if (gray.Length == 0)
            {
                error = "测量矩形内没有采样点";
                return false;
            }

            HOperatorSet.CreateFunct1dArray(gray, out HTuple function);
            if (smooth > 0)
            {
                try
                {
                    HOperatorSet.SmoothFunct1dGauss(function, smooth, out function);
                }
                catch (HalconException ex) when (ex.GetErrorCode() == 3022)
                {
                    error = $"平滑系数 {smooth:G4} 相对曲线长度过大（{gray.Length} 个采样点，可用的平滑系数约不超过 {(gray.Length - 1) / 8.0:F1}）";
                    return false;
                }
            }
            HOperatorSet.Funct1dToPairs(function, out _, out HTuple values);
            profile = HalconTupleConvert.ToDoubles(values).ToArray();
            if (profile.Length >= 2)
            {
                HOperatorSet.DerivateFunct1d(function, "first", out HTuple derived);
                HOperatorSet.Funct1dToPairs(derived, out _, out HTuple slopes);
                derivative = HalconTupleConvert.ToDoubles(slopes).ToArray();
            }
            else
            {
                derivative = new double[profile.Length];
            }
            return true;
        }

        /// <summary>测量矩形四个角都在图像内（像素中心坐标 -0.5 ~ 尺寸-0.5）。</summary>
        private static bool RectangleInside(double row, double column, double phi, double length1, double length2, double width, double height)
        {
            double cos = Math.Cos(phi);
            double sin = Math.Sin(phi);
            foreach ((double a, double b) in new[] { (1.0, 1.0), (1.0, -1.0), (-1.0, 1.0), (-1.0, -1.0) })
            {
                // HALCON 约定：Length1 方向(行,列) = (-sin, cos)，Length2 方向 = (-cos, -sin)
                double r = row - a * length1 * sin - b * length2 * cos;
                double c = column + a * length1 * cos - b * length2 * sin;
                if (r < -0.5 || c < -0.5 || r > height - 0.5 || c > width - 0.5)
                {
                    return false;
                }
            }
            return true;
        }

        protected override void OnSeedsStarting()
        {
            _last = null;
        }

        /// <summary>单值与曲线取最后一次成功的测量；全部失败时写空数组、NaN 与 -1。</summary>
        protected override void WriteSeedOutputs(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Array(ModuleName, "Profile", VariableType.Double, _last?.Profile ?? new double[0]));
            SetOutput(ctx, Variable.Array(ModuleName, "Derivative", VariableType.Double, _last?.Derivative ?? new double[0]));
            SetOutput(ctx, Variable.Single(ModuleName, "MinGray", VariableType.Double, _last?.MinGray ?? double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "MaxGray", VariableType.Double, _last?.MaxGray ?? double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "MeanGray", VariableType.Double, _last?.MeanGray ?? double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "MinPosition", VariableType.Int, _last?.MinPosition ?? -1));
            SetOutput(ctx, Variable.Single(ModuleName, "MaxPosition", VariableType.Int, _last?.MaxPosition ?? -1));
        }
    }
}
