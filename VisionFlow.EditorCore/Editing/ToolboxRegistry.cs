using System;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>工具箱条目：分类、显示名与节点工厂（创建带默认参数的新节点）。</summary>
    public sealed class ToolboxItem
    {
        public string Id { get; private set; }
        public string Category { get; private set; }
        public string DisplayName { get; private set; }
        public Func<FlowNode> Factory { get; private set; }

        public ToolboxItem(string id, string category, string displayName, Func<FlowNode> factory)
        {
            Id = id;
            Category = category;
            DisplayName = displayName;
            Factory = factory;
        }
    }

    /// <summary>工具箱注册表：编辑器与无头测试共用。</summary>
    public static class ToolboxRegistry
    {
        private static readonly Dictionary<string, int> _nameCounter = new Dictionary<string, int>();
        private static readonly List<ToolboxItem> _items = new List<ToolboxItem>();
        private static readonly HashSet<string> _registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _defaultsRegistered;

        /// <summary>生成不重复的模块名（模板匹配1、模板匹配2……），避免输出变量互相覆盖。</summary>
        private static string NextModuleName(string baseName)
        {
            _nameCounter.TryGetValue(baseName, out int n);
            n++;
            _nameCounter[baseName] = n;
            return baseName + n;
        }

        public static IReadOnlyList<ToolboxItem> Items
        {
            get { return _items.AsReadOnly(); }
        }

        public static void RegisterDefaults()
        {
            if (_defaultsRegistered)
            {
                return;
            }
            _defaultsRegistered = true;

            Register(new ToolboxItem("match", "01 定位匹配", "模板匹配", () =>
                new ToolNode(new HalconModelMatchTool(NextModuleName("模板匹配"))
                {
                    ImagePath = "Input.Image",
                    ModelPath = RepoPaths.Find("src/Image/temp.shm"),
                    NumMatches = 10,
                    // 示例模板 temp.shm 的示教基准位姿
                    BaseRow = 99,
                    BaseColumn = 79
                })));
            Register(new ToolboxItem("gray-match", "01 定位匹配", "灰度匹配", () =>
                new ToolNode(new HalconGrayMatchTool(NextModuleName("灰度匹配"))
                {
                    ImagePath = "Input.Image",
                    NumMatches = 10
                })));
            Register(new ToolboxItem("scaled-shape-match", "01 定位匹配", "缩放形状匹配", () =>
                new ToolNode(new HalconScaledShapeMatchTool(NextModuleName("缩放匹配"))
                {
                    ImagePath = "Input.Image",
                    NumMatches = 10
                })));
            Register(new ToolboxItem("deformable-match", "01 定位匹配", "局部变形匹配", () =>
                new ToolNode(new HalconLocalDeformableMatchTool(NextModuleName("变形匹配"))
                {
                    ImagePath = "Input.Image",
                    NumMatches = 10
                })));
            Register(new ToolboxItem("generic-shape-match", "01 定位匹配", "通用形状匹配", () =>
                new ToolNode(new HalconGenericShapeMatchTool(NextModuleName("通用形状匹配"))
                {
                    ImagePath = "Input.Image",
                    NumMatches = 10
                })));
            Register(new ToolboxItem("descriptor-match", "01 定位匹配", "描述子匹配", () =>
                new ToolNode(new DescriptorMatchTool(NextModuleName("描述子匹配")))));
            Register(new ToolboxItem("measure", "05 几何测量", "椭圆测量", () =>
                new ToolNode(new EllipseFollowMeasureTool(NextModuleName("椭圆测量"))
                {
                    // 示例图像 razors1.png + 示例模板 temp.shm 下的初始测量位置
                    EllipseRow = 28.1559,
                    EllipseColumn = 82.9631,
                    EllipseAngle = -Math.PI / 2,
                    EllipseLength1 = 10,
                    EllipseLength2 = 4
                })));
            Register(new ToolboxItem("measureline", "05 几何测量", "直线测量", () =>
                new ToolNode(new LineFollowMeasureTool(NextModuleName("直线测量")))));
            Register(new ToolboxItem("measure-caliper1d", "05 几何测量", "一维卡尺测量", () =>
                new ToolNode(new OneDCaliperFollowMeasureTool(NextModuleName("一维卡尺")))));
            Register(new ToolboxItem("measure-arc-caliper1d", "05 几何测量", "一维圆弧卡尺测量", () =>
                new ToolNode(new ArcCaliperFollowMeasureTool(NextModuleName("圆弧卡尺")))));
            Register(new ToolboxItem("corner-find", "05 几何测量", "找角", () =>
                new ToolNode(new CornerFindTool(NextModuleName("找角")))));
            Register(new ToolboxItem("gray-projection", "05 几何测量", "灰度投影", () =>
                new ToolNode(new GrayProjectionFollowTool(NextModuleName("灰度投影")))));
            Register(new ToolboxItem("measurerectangle", "05 几何测量", "矩形测量", () =>
                new ToolNode(new RectangleFollowMeasureTool(NextModuleName("矩形测量")))));
            Register(new ToolboxItem("measurecircle", "05 几何测量", "圆形测量", () =>
                new ToolNode(new CircleFollowMeasureTool(NextModuleName("圆形测量")))));
            Register(new ToolboxItem("loadimage", "00 图像采集", "图像加载", () =>
                new ToolNode(new LoadImageTool(NextModuleName("图像加载"))
                {
                    FilePath = RepoPaths.Find("src/Image/razors1.png")
                })));
            Register(new ToolboxItem("mean-image", "02 图像处理", "图像滤波", () =>
                new ToolNode(new MeanImageTool(NextModuleName("图像滤波")))));
            Register(new ToolboxItem("gray-enhance", "02 图像处理", "灰度增强", () =>
                new ToolNode(new GrayEnhanceTool(NextModuleName("灰度增强")))));
            Register(new ToolboxItem("image-geometry", "02 图像处理", "图像几何变换", () =>
                new ToolNode(new ImageGeometryTool(NextModuleName("几何变换")))));
            Register(new ToolboxItem("polar-unwrap", "02 图像处理", "极坐标展开", () =>
                new ToolNode(new PolarUnwrapTool(NextModuleName("极坐标展开")))));
            Register(new ToolboxItem("polar-inverse", "02 图像处理", "极坐标逆变换", () =>
                new ToolNode(new PolarInverseTool(NextModuleName("极坐标逆变换")))));
            Register(new ToolboxItem("region-to-image", "02 图像处理", "区域转图像", () =>
                new ToolNode(new RegionToImageTool(NextModuleName("区域转图像")))));
            Register(new ToolboxItem("affine-trans-image", "02 图像处理", "图像仿射变换", () =>
                new ToolNode(new AffineTransformImageTool(NextModuleName("图像仿射")))));
            Register(new ToolboxItem("reduce-domain", "02 图像处理", "ReduceDomain 限定图像域", () =>
                new ToolNode(new ReduceDomainTool(NextModuleName("限定图像域")))));
            Register(new ToolboxItem("add-sub-image", "02 图像处理", "图像运算", () =>
                new ToolNode(new AddSubImageTool(NextModuleName("图像运算")))));
            Register(new ToolboxItem("decompose-channels", "02 图像处理", "通道分解", () =>
                new ToolNode(new DecomposeChannelsTool(NextModuleName("通道分解")))));
            Register(new ToolboxItem("compose3", "02 图像处理", "三通道合成", () =>
                new ToolNode(new Compose3ImageTool(NextModuleName("三通道合成")))));
            Register(new ToolboxItem("trans-color-space", "02 图像处理", "RGB 色彩空间转换", () =>
                new ToolNode(new TransColorSpaceTool(NextModuleName("色彩转换")))));
            Register(new ToolboxItem("threshold", "03 区域处理", "阈值分割", () =>
                new ToolNode(new ThresholdTool(NextModuleName("阈值分割"))
                {
                    ImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("regionprocess", "03 区域处理", "区域处理", () =>
                new ToolNode(new RegionProcessTool(NextModuleName("区域处理"))
                {
                    // 只有“取反”读取裁剪图像；默认值放在工具箱而非构造函数，历史流程加载后保持为空
                    ClipImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("manual-region", "03 区域处理", "手动 Region", () =>
                new ToolNode(new ManualRegionTool(NextModuleName("手动Region")))));
            Register(new ToolboxItem("region-difference", "03 区域处理", "Region 相减", () =>
                new ToolNode(new RegionDifferenceTool(NextModuleName("Region相减")))));
            Register(new ToolboxItem("region-union2", "03 区域处理", "Region 合并", () =>
                new ToolNode(new RegionUnion2Tool(NextModuleName("Region合并")))));
            Register(new ToolboxItem("region-intersection", "03 区域处理", "Region 交集", () =>
                new ToolNode(new RegionIntersectionTool(NextModuleName("Region交集")))));
            Register(new ToolboxItem("region-shape-trans", "03 区域处理", "Region 形状转换", () =>
                new ToolNode(new RegionShapeTransTool(NextModuleName("形状转换")))));
            // “Region Union1”“形态学”已并入“区域处理”（TR-11），不再提供独立入口；类型与 ID 保留用于历史流程
            Register(new ToolboxItem("region-features", "03 区域处理", "Region 特征值", () =>
                new ToolNode(new RegionFeaturesTool(NextModuleName("Region特征")))));
            Register(new ToolboxItem("region-min-max-gray", "03 区域处理", "区域灰度统计", () =>
                new ToolNode(new RegionMinMaxGrayTool(NextModuleName("灰度统计")))));
            Register(new ToolboxItem("selectregion", "03 区域处理", "区域筛选", () =>
                new ToolNode(new SelectRegionTool(NextModuleName("区域筛选"))
                {
                    // 只有“按灰度筛选”读取图像；默认值放在工具箱，历史流程加载后保持为空
                    ImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("region-sort", "03 区域处理", "区域排序(行列编号)", () =>
                new ToolNode(new RegionSortTool(NextModuleName("区域排序")))));
            Register(new ToolboxItem("xld-to-region", "03 区域处理", "XLD 转 Region", () =>
                new ToolNode(new XldToRegionTool(NextModuleName("XLD转Region")))));
            Register(new ToolboxItem("zone-inspect", "03 区域处理", "分区检测", () =>
                new ToolNode(new ZoneInspectTool(NextModuleName("分区检测")))));
            Register(new ToolboxItem("regionpose", "01 定位匹配", "区域定位", () =>
                new ToolNode(new RegionPoseTool(NextModuleName("区域定位")))));
            Register(new ToolboxItem("contour-create", "04 XLD轮廓", "边缘提取", () =>
                new ToolNode(new ContourCreateTool(NextModuleName("边缘提取")))));
            Register(new ToolboxItem("select-contour", "04 XLD轮廓", "边缘选择", () =>
                new ToolNode(new SelectContourTool(NextModuleName("边缘选择")))));
            Register(new ToolboxItem("concat-xld", "04 XLD轮廓", "边缘合并", () =>
                new ToolNode(new ConcatXldTool(NextModuleName("边缘合并")))));
            Register(new ToolboxItem("segment-xld", "04 XLD轮廓", "XLD 分割", () =>
                new ToolNode(new SegmentXldTool(NextModuleName("XLD分割")))));
            Register(new ToolboxItem("xld-features", "04 XLD轮廓", "XLD 特征值", () =>
                new ToolNode(new XldFeaturesTool(NextModuleName("XLD特征")))));
            Register(new ToolboxItem("region-to-xld", "04 XLD轮廓", "Region 转 XLD", () =>
                new ToolNode(new RegionToXldTool(NextModuleName("Region转XLD")))));
            Register(new ToolboxItem("xld-process", "04 XLD轮廓", "XLD 处理", () =>
                new ToolNode(new XldProcessTool(NextModuleName("XLD处理")))));
            Register(new ToolboxItem("fit-line", "05 几何测量", "拟合直线", () =>
                new ToolNode(new FitLineTool(NextModuleName("拟合直线")))));
            Register(new ToolboxItem("fit-circle", "05 几何测量", "拟合圆", () =>
                new ToolNode(new FitCircleTool(NextModuleName("拟合圆")))));
            Register(new ToolboxItem("intersection-lines", "05 几何测量", "交点计算", () =>
                new ToolNode(new IntersectionLinesTool(NextModuleName("交点计算")))));
            Register(new ToolboxItem("region-distance", "05 几何测量", "区域距离", () =>
                new ToolNode(new RegionDistanceTool(NextModuleName("区域距离")))));
            Register(new ToolboxItem("fit-ellipse-rect", "05 几何测量", "拟合椭圆/矩形", () =>
                new ToolNode(new FitEllipseRectTool(NextModuleName("拟合椭圆矩形")))));
            Register(new ToolboxItem("contour-distance", "05 几何测量", "轮廓距离", () =>
                new ToolNode(new ContourDistanceTool(NextModuleName("轮廓距离")))));
            Register(new ToolboxItem("geometry-relation", "05 几何测量", "几何关系测量", () =>
                new ToolNode(new GeometryRelationTool(NextModuleName("几何关系")))));
            Register(new ToolboxItem("angle-convert", "05 几何测量", "单位换算", () =>
                new ToolNode(new AngleConvertTool(NextModuleName("角度换算")))));
            Register(new ToolboxItem("barcode1d", "06 识别工具", "读码", () =>
                new ToolNode(new Barcode1DTool(NextModuleName("读码")))));
            Register(new ToolboxItem("ocr", "06 识别工具", "字符识别", () =>
                new ToolNode(new OcrTool(NextModuleName("字符识别")))));
            Register(new ToolboxItem("color-classify", "06 识别工具", "颜色识别", () =>
                new ToolNode(new ColorClassifyTool(NextModuleName("颜色识别")))));
            Register(new ToolboxItem("color-segment", "06 识别工具", "颜色分割", () =>
                new ToolNode(new ColorSegmentTool(NextModuleName("颜色分割")))));
            Register(new ToolboxItem("range-classify", "07 数据与判定", "数值区间分类", () =>
                new ToolNode(new RangeClassifyTool(NextModuleName("区间分类")))));
            Register(new ToolboxItem("expression-calc", "07 数据与判定", "变量计算", () =>
                new ToolNode(new ExpressionCalcTool(NextModuleName("变量计算")))));
            Register(new ToolboxItem("array-process", "07 数据与判定", "数组处理", () =>
                new ToolNode(new ArrayProcessTool(NextModuleName("数组处理")))));
            Register(new ToolboxItem("result-judge", "07 数据与判定", "综合判定", () =>
                new ToolNode(new ResultJudgeTool(NextModuleName("综合判定")))));
            Register(new ToolboxItem("variation-inspect", "08 缺陷检测", "差分检测", () =>
                new ToolNode(new VariationInspectTool(NextModuleName("差分检测")))));
            // 工具分类调整只影响工具箱显示，工具 ID 与类型名不变，历史流程照常加载
            Register(new ToolboxItem("affine-point", "09 标定", "图像坐标转世界坐标", () =>
                new ToolNode(new AffinePointTool(NextModuleName("坐标转换")))));
            Register(new ToolboxItem("alignment-offset", "09 标定", "纠偏计算", () =>
                new ToolNode(new AlignmentOffsetTool(NextModuleName("纠偏计算")))));
            Register(new ToolboxItem("image-rectify", "09 标定", "畸变校正", () =>
                new ToolNode(new ImageRectifyTool(NextModuleName("畸变校正")))));
            Register(new ToolboxItem("ifelse", "逻辑控制", "IfElse 分支", () =>
                new IfElseNode("条件分支")));
            Register(new ToolboxItem("forcount", "逻辑控制", "For 循环(次数)", () =>
                ForLoopNode.Count("按次数循环", Operand.Const(1))));
            Register(new ToolboxItem("foreach", "逻辑控制", "For 循环(集合)", () =>
                ForLoopNode.Each("遍历循环", string.Empty))); // 循环源由编辑器下拉选择
            Register(new ToolboxItem("whileloop", "逻辑控制", "While 循环(条件)", () =>
                new WhileLoopNode("条件循环", new ComparisonCondition
                {
                    Left = Operand.Ref("Loop.Index"),
                    Operator = ComparisonOperator.Less,
                    Right = Operand.Const(3)
                })));
            Register(new ToolboxItem("break", "逻辑控制", "跳出循环", () => new BreakNode("跳出循环")));
            Register(new ToolboxItem("continue", "逻辑控制", "跳过本次", () => new ContinueNode("跳过本次")));
            Register(new ToolboxItem("switch", "逻辑控制", "Switch 多分支", () =>
            {
                var node = new SwitchNode("多分支");
                node.Cases.Add(new SwitchCaseNode("分支 1", "1"));
                node.Cases.Add(new SwitchCaseNode("分支 2", "2"));
                node.Cases.Add(new SwitchCaseNode("默认", isDefault: true));
                return node;
            }));
            Register(new ToolboxItem("subflow", "逻辑控制", "子流程", () => new SubFlowNode(NextModuleName("子流程"))));
            Register(new ToolboxItem("flowoutput", "逻辑控制", "流程输出", () =>
                new FlowOutputNode(NextModuleName("流程输出"))
                {
                    Outputs =
                    {
                        new FlowOutputDef { Name = "Ok", Kind = VariableKind.Single, Type = VariableType.Bool, Value = Operand.Const(true) },
                        new FlowOutputDef { Name = "Code", Kind = VariableKind.Single, Type = VariableType.Int, Value = Operand.Const(0) },
                        new FlowOutputDef { Name = "Message", Kind = VariableKind.Single, Type = VariableType.String, Value = Operand.Const("OK") }
                    }
                }));
        }

        public static bool Register(ToolboxItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || _registeredIds.Contains(item.Id))
            {
                return false;
            }
            if (_items.Any(i => string.Equals(i.Category, item.Category, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            _registeredIds.Add(item.Id);
            _items.Add(item);
            return true;
        }

        public static bool RegisterTool(Type toolType, ToolboxToolAttribute attribute)
        {
            if (toolType == null || attribute == null || !typeof(ToolBase).IsAssignableFrom(toolType))
            {
                return false;
            }

            string id = string.IsNullOrWhiteSpace(attribute.Id) ? toolType.FullName : attribute.Id;
            string category = string.IsNullOrWhiteSpace(attribute.Category) ? "插件工具" : attribute.Category;
            string displayName = string.IsNullOrWhiteSpace(attribute.DisplayName) ? toolType.Name : attribute.DisplayName;
            string defaultModuleName = string.IsNullOrWhiteSpace(attribute.DefaultModuleName)
                ? displayName
                : attribute.DefaultModuleName;

            return Register(new ToolboxItem(id, category, displayName, () =>
            {
                var tool = Activator.CreateInstance(toolType, NextModuleName(defaultModuleName)) as ToolBase;
                if (tool == null)
                {
                    throw new InvalidOperationException($"插件工具 {toolType.FullName} 必须提供构造函数 .ctor(string moduleName)。");
                }
                ApplyDefaultInputRefs(tool);
                return new ToolNode(tool);
            }));
        }

        private static void ApplyDefaultInputRefs(ToolBase tool)
        {
            foreach (ToolInputRefDef def in ToolMetadata.GetInputRefs(tool.GetType()))
            {
                if (def.ExpectedType.Name == "HalconImage"
                    && def.Property.PropertyType == typeof(string)
                    && string.IsNullOrWhiteSpace(def.Property.GetValue(tool) as string))
                {
                    def.Property.SetValue(tool, "Input.Image");
                }
            }
        }

        public static ToolboxItem Find(string id)
        {
            foreach (ToolboxItem item in Items)
            {
                if (item.Id == id)
                {
                    return item;
                }
            }
            throw new KeyNotFoundException($"工具箱中不存在条目 '{id}'");
        }
    }
}
