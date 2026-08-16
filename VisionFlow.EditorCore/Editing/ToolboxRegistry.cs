using System;
using System.Collections.Generic;
using System.Linq;
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
                    NumMatches = 10
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
            Register(new ToolboxItem("measure", "05 几何测量", "椭圆测量", () =>
                new ToolNode(new EllipseFollowMeasureTool(NextModuleName("椭圆测量")))));
            Register(new ToolboxItem("measureline", "05 几何测量", "直线测量", () =>
                new ToolNode(new LineFollowMeasureTool(NextModuleName("直线测量")))));
            Register(new ToolboxItem("measure-caliper1d", "05 几何测量", "一维卡尺测量", () =>
                new ToolNode(new OneDCaliperFollowMeasureTool(NextModuleName("一维卡尺")))));
            Register(new ToolboxItem("measure-arc-caliper1d", "05 几何测量", "一维圆弧卡尺测量", () =>
                new ToolNode(new ArcCaliperFollowMeasureTool(NextModuleName("圆弧卡尺")))));
            Register(new ToolboxItem("measurerectangle", "05 几何测量", "矩形测量", () =>
                new ToolNode(new RectangleFollowMeasureTool(NextModuleName("矩形测量")))));
            Register(new ToolboxItem("measurecircle", "05 几何测量", "圆形测量", () =>
                new ToolNode(new CircleFollowMeasureTool(NextModuleName("圆形测量")))));
            Register(new ToolboxItem("loadimage", "00 图像采集", "图像加载", () =>
                new ToolNode(new LoadImageTool(NextModuleName("图像加载"))
                {
                    FilePath = RepoPaths.Find("src/Image/razors1.png")
                })));
            Register(new ToolboxItem("mean-image", "02 图像处理", "均值滤波", () =>
                new ToolNode(new MeanImageTool(NextModuleName("均值滤波")))));
            Register(new ToolboxItem("add-sub-image", "02 图像处理", "图像加减", () =>
                new ToolNode(new AddSubImageTool(NextModuleName("图像加减")))));
            Register(new ToolboxItem("decompose-channels", "02 图像处理", "通道分解", () =>
                new ToolNode(new DecomposeChannelsTool(NextModuleName("通道分解")))));
            Register(new ToolboxItem("compose3", "02 图像处理", "三通道合成", () =>
                new ToolNode(new Compose3ImageTool(NextModuleName("三通道合成")))));
            Register(new ToolboxItem("trans-color-space", "02 图像处理", "RGB 色彩空间转换", () =>
                new ToolNode(new TransColorSpaceTool(NextModuleName("色彩转换")))));
            Register(new ToolboxItem("threshold", "03 区域处理", "二值化", () =>
                new ToolNode(new ThresholdTool(NextModuleName("二值化"))
                {
                    ImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("auto-threshold", "03 区域处理", "AutoThreshold 自动阈值", () =>
                new ToolNode(new AutoThresholdTool(NextModuleName("自动阈值"))
                {
                    ImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("binary-threshold", "03 区域处理", "BinaryThreshold 二值阈值", () =>
                new ToolNode(new BinaryThresholdTool(NextModuleName("二值阈值"))
                {
                    ImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("fast-threshold", "03 区域处理", "FastThreshold 快速阈值", () =>
                new ToolNode(new FastThresholdTool(NextModuleName("快速阈值"))
                {
                    ImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("char-threshold", "03 区域处理", "CharThreshold 字符阈值", () =>
                new ToolNode(new CharThresholdTool(NextModuleName("字符阈值"))
                {
                    ImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("var-threshold", "03 区域处理", "VarThreshold 局部阈值", () =>
                new ToolNode(new VarThresholdTool(NextModuleName("局部阈值"))
                {
                    ImagePath = "Input.Image"
                })));
            Register(new ToolboxItem("regionprocess", "03 区域处理", "区域处理", () =>
                new ToolNode(new RegionProcessTool(NextModuleName("区域处理")))));
            Register(new ToolboxItem("region-difference", "03 区域处理", "Region 相减", () =>
                new ToolNode(new RegionDifferenceTool(NextModuleName("Region相减")))));
            Register(new ToolboxItem("region-union2", "03 区域处理", "Region 合并", () =>
                new ToolNode(new RegionUnion2Tool(NextModuleName("Region合并")))));
            Register(new ToolboxItem("region-shape-trans", "03 区域处理", "Region 形状转换", () =>
                new ToolNode(new RegionShapeTransTool(NextModuleName("形状转换")))));
            Register(new ToolboxItem("region-union1", "03 区域处理", "Region Union1", () =>
                new ToolNode(new RegionUnion1Tool(NextModuleName("RegionUnion1")))));
            Register(new ToolboxItem("morphology-rect", "03 区域处理", "矩形形态学", () =>
                new ToolNode(new MorphologyRectTool(NextModuleName("矩形形态学")))));
            Register(new ToolboxItem("morphology-circle", "03 区域处理", "圆形形态学", () =>
                new ToolNode(new MorphologyCircleTool(NextModuleName("圆形形态学")))));
            Register(new ToolboxItem("region-features", "03 区域处理", "Region 特征值", () =>
                new ToolNode(new RegionFeaturesTool(NextModuleName("Region特征")))));
            Register(new ToolboxItem("selectregion", "03 区域处理", "区域筛选", () =>
                new ToolNode(new SelectRegionTool(NextModuleName("区域筛选")))));
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
            Register(new ToolboxItem("fit-line", "05 几何测量", "拟合直线", () =>
                new ToolNode(new FitLineTool(NextModuleName("拟合直线")))));
            Register(new ToolboxItem("fit-circle", "05 几何测量", "拟合圆", () =>
                new ToolNode(new FitCircleTool(NextModuleName("拟合圆")))));
            Register(new ToolboxItem("intersection-lines", "05 几何测量", "线线交点", () =>
                new ToolNode(new IntersectionLinesTool(NextModuleName("线线交点")))));
            Register(new ToolboxItem("affine-point", "05 几何测量", "图像坐标转世界坐标", () =>
                new ToolNode(new AffinePointTool(NextModuleName("坐标转换")))));
            Register(new ToolboxItem("ifelse", "逻辑控制", "IfElse 分支", () =>
                new IfElseNode("条件分支")));
            Register(new ToolboxItem("forcount", "逻辑控制", "For 循环(次数)", () =>
                ForLoopNode.Count("按次数循环", Operand.Const(1))));
            Register(new ToolboxItem("foreach", "逻辑控制", "For 循环(集合)", () =>
                ForLoopNode.Each("遍历循环", string.Empty))); // 循环源由编辑器下拉选择
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
