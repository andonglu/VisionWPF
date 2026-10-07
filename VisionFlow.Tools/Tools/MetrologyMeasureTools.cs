using System.Collections.Generic;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>metrology 卡尺的插值方式（成员名即 HALCON 参数值）。</summary>
    public enum MetrologyInterpolation
    {
        nearest_neighbor,
        bilinear,
        bicubic
    }

    /// <summary>
    /// metrology 测量（直线 / 矩形 / 圆 / 椭圆）的公共基类（MS-01）：高级参数、得分、边缘点与多实例输出。
    /// 新参数的默认值等于 HALCON 运行时默认值（22.11 实测，见计划第 9 节），默认参数下结果与旧版完全相同。
    /// 一维卡尺类测量不走 metrology，不继承本类。
    /// 输出约定：
    /// - Score：最后一次成功测量的第一个实例的得分（与其他单值输出一致）；
    /// - Scores / InstanceSeedIndices / 各工具的 Instance* 数组：按“定位结果（或区域对象）→ 实例”顺序展开，
    ///   InstanceSeedIndices 为每个实例所属的测量项序号（即 <see cref="FollowMeasureToolBase.SeedIndex"/>）；
    /// - MeasurePoints：本次全部卡尺找到的边缘点（十字 XLD），测量失败的项也计入，便于判断卡尺是否找到边缘。
    /// </summary>
    [ToolOutput("Score", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double)]
    [ToolOutput("InstanceCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("InstanceSeedIndices", VariableKind.Array, VariableType.Int)]
    [ToolOutput("MeasurePoints", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    public abstract class MetrologyMeasureToolBase : FollowMeasureToolBase, IToolConfigurationCheck, IToolParameterVisibility
    {
        /// <summary>卡尺数；0 表示不设置，按 MeasureDistance 布置（与 HALCON 默认一致）。</summary>
        public int NumMeasures { get; set; }
        /// <summary>相邻卡尺中心的间距（像素），仅 NumMeasures = 0 时使用。</summary>
        public double MeasureDistance { get; set; } = 10;
        /// <summary>实例的最低得分（有效边缘点占比）。</summary>
        public double MinScore { get; set; } = 0.7;
        /// <summary>最多查找的实例数（如两条平行直线设为 2）。</summary>
        public int NumInstances { get; set; } = 1;
        /// <summary>边缘点到拟合几何的最大距离（像素），超过视为离群点。</summary>
        public double DistanceThreshold { get; set; } = 3.5;
        /// <summary>卡尺灰度插值方式。</summary>
        public MetrologyInterpolation MeasureInterpolation { get; set; } = MetrologyInterpolation.nearest_neighbor;

        private readonly List<double[]> _instanceParams = new List<double[]>();
        private readonly List<double> _instanceScores = new List<double>();
        private readonly List<int> _instanceSeeds = new List<int>();
        private readonly List<double> _pointRows = new List<double>();
        private readonly List<double> _pointColumns = new List<double>();
        private double _lastFirstScore = double.NaN;

        protected MetrologyMeasureToolBase(string moduleName) : base(moduleName)
        {
        }

        /// <summary>按实例展开的几何数组输出名，顺序与 get_metrology_object_result 的 all_param 一致。</summary>
        protected abstract string[] InstanceOutputNames { get; }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (NumMeasures < 0)
            {
                yield return new ToolConfigurationIssue(nameof(NumMeasures), "卡尺数不能小于 0（0 表示按卡尺间距布置）");
            }
            if (NumMeasures == 0 && !(MeasureDistance > 0))
            {
                yield return new ToolConfigurationIssue(nameof(MeasureDistance), "卡尺间距必须大于 0");
            }
            if (MinScore < 0 || MinScore > 1)
            {
                yield return new ToolConfigurationIssue(nameof(MinScore), "最低得分必须在 0 到 1 之间");
            }
            if (NumInstances < 1)
            {
                yield return new ToolConfigurationIssue(nameof(NumInstances), "实例数必须大于 0");
            }
            if (DistanceThreshold < 0)
            {
                yield return new ToolConfigurationIssue(nameof(DistanceThreshold), "距离阈值不能小于 0");
            }
        }

        public virtual bool IsParameterVisible(string propertyName)
        {
            return propertyName != nameof(MeasureDistance) || NumMeasures <= 0;
        }

        /// <summary>参数配置错误时返回失败结果，否则返回 null。</summary>
        protected NodeResult CheckBeforeRun()
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            return issue == null ? null : NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
        }

        /// <summary>
        /// metrology 通用参数：边缘极性与选择；NumMeasures &gt; 0 时传 num_measures，否则传 measure_distance（二者不能同时传，
        /// HALCON 会报错）；其余高级参数始终传入，默认值等于 HALCON 默认值。
        /// </summary>
        protected void AddMetrologyParams(out HTuple names, out HTuple values)
        {
            names = new HTuple("measure_transition").TupleConcat("measure_select");
            values = new HTuple(MeasureTransition).TupleConcat(MeasureSelect);
            if (NumMeasures > 0)
            {
                names = names.TupleConcat("num_measures");
                values = values.TupleConcat(NumMeasures);
            }
            else
            {
                names = names.TupleConcat("measure_distance");
                values = values.TupleConcat(MeasureDistance);
            }
            names = names.TupleConcat("min_score").TupleConcat("num_instances").TupleConcat("distance_threshold").TupleConcat("measure_interpolation");
            values = values.TupleConcat(MinScore).TupleConcat(NumInstances).TupleConcat(DistanceThreshold).TupleConcat(MeasureInterpolation.ToString());
        }

        /// <summary>
        /// 执行 metrology 模型，返回第 0 个对象全部实例的结果参数（按实例首尾相接）与结果轮廓（轮廓归调用方释放）。
        /// 同时记录本测量项的边缘点；至少得到一个完整实例时记录各实例的参数与得分（归属当前 SeedIndex）。
        /// </summary>
        protected HTuple ApplyMetrology(HObject image, HTuple metrology, out HObject contour)
        {
            HOperatorSet.ApplyMetrologyModel(image, metrology);
            HOperatorSet.GetMetrologyObjectResult(metrology, 0, "all", "result_type", "all_param", out HTuple param);
            HOperatorSet.GetMetrologyObjectResultContour(out contour, metrology, "all", "all", 1.5);

            HOperatorSet.GetMetrologyObjectMeasures(out HObject measureRegions, metrology, "all", "all", out HTuple rows, out HTuple columns);
            measureRegions.Dispose();
            _pointRows.AddRange(HalconTupleConvert.ToDoubles(rows));
            _pointColumns.AddRange(HalconTupleConvert.ToDoubles(columns));

            int perInstance = InstanceOutputNames.Length;
            int instances = param.Length / perInstance;
            if (instances > 0)
            {
                HOperatorSet.GetMetrologyObjectResult(metrology, 0, "all", "result_type", "score", out HTuple scores);
                for (int i = 0; i < instances; i++)
                {
                    var values = new double[perInstance];
                    for (int k = 0; k < perInstance; k++)
                    {
                        values[k] = param[i * perInstance + k].D;
                    }
                    _instanceParams.Add(values);
                    _instanceScores.Add(i < scores.Length ? scores[i].D : double.NaN);
                    _instanceSeeds.Add(SeedIndex);
                }
                _lastFirstScore = scores.Length > 0 ? scores[0].D : double.NaN;
            }
            return param;
        }

        protected override void OnSeedsStarting()
        {
            _instanceParams.Clear();
            _instanceScores.Clear();
            _instanceSeeds.Clear();
            _pointRows.Clear();
            _pointColumns.Clear();
            _lastFirstScore = double.NaN;
        }

        protected override void WriteSeedOutputs(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "Score", VariableType.Double, _instanceScores.Count > 0 ? _lastFirstScore : double.NaN));
            SetOutput(ctx, Variable.Array(ModuleName, "Scores", VariableType.Double, _instanceScores.ToList()));
            SetOutput(ctx, Variable.Single(ModuleName, "InstanceCount", VariableType.Int, _instanceParams.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "InstanceSeedIndices", VariableType.Int, _instanceSeeds.ToList()));
            string[] names = InstanceOutputNames;
            for (int k = 0; k < names.Length; k++)
            {
                int column = k;
                SetOutput(ctx, Variable.Array(ModuleName, names[k], VariableType.Double, _instanceParams.Select(p => p[column]).ToList()));
            }
            HObject points = XldLineHelper.Crosses(_pointRows, _pointColumns);
            HOperatorSet.CountObj(points, out HTuple pointCount);
            SetOutput(ctx, Variable.Object(ModuleName, "MeasurePoints", new HalconXld(points), pointCount.I));
        }
    }
}
