# XLD 相关算子补充开发计划

编写日期：2026-10-06
状态：已实现（XG-01 ~ XG-09，分支 `feature/xld-tools`；探测结论见第 10 节，评审处理见第 11 节）
范围：`VisionFlow.Tools` 中与 XLD 轮廓相关、尚未覆盖的 HALCON 算子，以及以拟合直线为输入的几何关系测量。
关联文档：[Region 相关算子补充开发计划](REGION-TOOLS-PLAN.md)（Region 与 XLD 互转见 RG-05 / RG-06，配对规则见 RG-07）。

## 1. 现状

已有 8 个 XLD 相关工具：

| 工具 | 类型 | 使用的算子 |
|---|---|---|
| 边缘提取 `contour-create` | `ContourCreateTool` | `edges_color_sub_pix` |
| 边缘选择 `select-contour` | `SelectContourTool` | `select_shape_xld`（多特征、and / or） |
| 边缘合并 `concat-xld` | `ConcatXldTool` | `concat_obj` |
| XLD 分割 `segment-xld` | `SegmentXldTool` | `segment_contours_xld`（`lines` / `lines_circles`） |
| XLD 特征值 `xld-features` | `XldFeaturesTool` | `length_xld`、`area_center_xld`、`contour_point_num_xld`、`get_contour_global_attrib_xld` |
| 拟合直线 `fit-line` | `FitLineTool` | `fit_line_contour_xld` |
| 拟合圆 `fit-circle` | `FitCircleTool` | `fit_circle_contour_xld` |
| 线线交点 `intersection-lines` | `IntersectionLinesTool` | `intersection_lines` |

未覆盖的算子约 30 个（其中约 10 个是轮廓特征函数）。

## 2. 拆分原则

与 Region 计划一致：

- 输入输出与现有工具一致的算子，作为现有工具的新选项并入，不新增工具箱入口。
- 输入类型或输出结构与现有工具都不同的算子，新建工具。
- 结果：5 个现有工具增强，4 个新工具（含 1 个几何关系测量），工具箱净增 4 个入口。

## 3. 并入现有工具

### XG-01 边缘提取（`ContourCreateTool`）增加提取方式

- 新增 `ExtractMethod` 枚举：`ColorEdges`（默认，即现有 `edges_color_sub_pix`，保证历史流程行为不变）、`Edges`、`ThresholdSubPix`、`LinesGauss`。

| 方式 | 算子 | 参数 |
|---|---|---|
| `Edges` | `edges_sub_pix` | 复用 `Filter` / `Low` / `High`；`Sigma` 作为 `Alpha` 传入。`Filter` 可选值扩展为 `canny` / `deriche1` / `deriche2` / `lanser1` / `lanser2` / `shen` / `mshen` / `sobel_fast` |
| `ThresholdSubPix` | `threshold_sub_pix` | 新增 `Threshold`（默认 128） |
| `LinesGauss` | `lines_gauss` | 复用 `Sigma` / `Low` / `High`；新增 `LightDark`（`light` / `dark`）、`ExtractWidth`（bool）、`LineModel`（`none` / `bar-shaped` / `parabolic` / `gaussian`）、`CompleteJunctions`（bool） |

- `ColorEdges` 与 `Edges` 的可选滤波器不同，编辑窗口按方式切换下拉选项；不合法的组合运行时给出中文错误。
- 输出不变：`Xld`、`Count`、`Found`。
- 实现说明：`Filter` 改为枚举 `EdgeFilter`（17 个取值，按名称保存）；彩色边缘只接受实测支持的 9 个（含 `_junctions`），不合法组合由流程校验提前报告。`lines_gauss` 的阈值量纲与梯度阈值不同，使用单独的 `LineSigma` / `LineLow` / `LineHigh`（默认 1.5 / 3 / 8），不复用 `Sigma` / `Low` / `High`；`LightDark` 复用 `ThresholdLightDark`，线条提取只接受 `light` / `dark`。编辑界面按方式只显示用到的参数。

### XG-02 边缘选择（`SelectContourTool`）增加轮廓选择和取件方式

- 新增 `SelectBy` 枚举：`Shape`（默认，即现有 `select_shape_xld`）、`Contour`。
- `Contour` 时调用 `select_contours_xld`，新增 `ContourFeature`（`contour_length` / `direction` / `curvature` / `closed` / `open` / `maximum_extent`）、`Min1` / `Max1` / `Min2` / `Max2`。
- 新增 `TakeMode`，取值与区域筛选一致：`All`（默认）/ `Longest` / `Shortest` / `First` / `ByIndex`；新增 `TakeIndex`（从 0 开始）。
- 实现说明：`ContourFeature` 为实测的 6 个取值（见第 10 节，`closed` / `open` 合法），`Min1` / `Max1` / `Min2` / `Max2` 默认取 HALCON 默认值 0.5 / 200 / -0.5 / 0.5，界面按特征显示（`closed` 只显示 `Max1`，`open` 只显示 `Min1`，`curvature` 另显示 `Min2` / `Max2`）。`Operation` 改为枚举 `XldSelectOperation`（按名称保存）。先选择后取件，`ByIndex` 越界时输出为空并按 `FailWhenNotFound` 处理。隐藏的参数照常保存。

### XG-03 XLD 分割（`SegmentXldTool`）增加椭圆模式

- `XldSegmentMode` 末尾追加 `lines_ellipses`。

### XG-04 XLD 特征值（`XldFeaturesTool`）补充形状特征

在 `GetXldFeature` 中增加以下特征名，名称与 `select_shape_xld` 的特征名保持一致，便于“特征值”与“边缘选择”互相对照：

| 特征名 | 算子 |
|---|---|
| `circularity` | `circularity_xld` |
| `compactness` | `compactness_xld` |
| `convexity` | `convexity_xld` |
| `anisometry` / `bulkiness` / `struct_factor` | `eccentricity_xld` |
| `orientation` | `orientation_xld` |
| `max_diameter` | `diameter_xld` |
| `rect2_phi` / `rect2_len1` / `rect2_len2` | `smallest_rectangle2_xld` |
| `outer_radius` | `smallest_circle_xld` |
| `ra` / `rb` / `phi` | `elliptic_axis_xld` |
| `is_closed` | `test_closed_xld`（输出 0 / 1） |

- 未识别的特征名仍走 `get_contour_global_attrib_xld`，保持现有行为。

### XG-05 线线交点（`IntersectionLinesTool`）扩展为交点计算

- 工具箱显示名改为“交点计算”，ID `intersection-lines` 与类型名不变。
- 新增 `IntersectionMode` 枚举：`LineLine`（默认，现有逻辑）、`LineContour`、`ContourContour`。

| 模式 | 算子 | 说明 |
|---|---|---|
| `LineContour` | `intersection_line_contour_xld` | 输入 1 按直线处理（沿用现有线线交点对直线输入的解析方式），输入 2 为任意轮廓 |
| `ContourContour` | `intersection_contours_xld` | 新增 `IntersectionType`（`mutual` 默认 / `all` / `self`） |

- 两个输入仍为 XLD，必填校验不变。
- 新模式可能有多个交点，新增 `Rows` / `Columns` 数组和 `Count`；`Row` / `Column` 取第一个交点；`Overlap` 保持原义。
- 实现说明：模式属性名为 `Mode`；`IntersectionType` 默认 `mutual`（HALCON 默认 `all`），只在轮廓与轮廓模式显示。两个输入的显示名改为“直线/轮廓1”“直线/轮廓2”（属性名不变，历史文件不受影响）。直线解析抽成共享的 `XldLineHelper`，原工具改为调用它。`LineContour` 中直线按无限长处理。`Xld` 为全部交点的十字标记，`Overlap` 取各轮廓重合标志的最大值。直线与直线模式的原有输出与旧版一致，另外也写出 `Rows` / `Columns` / `Count`。

## 4. 新建工具

### XG-06 XLD 处理

- 工具箱：`04 XLD轮廓 / XLD 处理`，ID `xld-process`。
- 定位：一个 XLD 进、XLD 出，对应 Region 的“区域处理”。
- 输入：XLD（必填）；定位矩阵（可选，仅 `AffineTrans` 模式必填）。
- 输出：`Xld`、`Count`、`Found`（继承 `XldToolBase`）。

| 操作 `Method` | 算子 | 参数 |
|---|---|---|
| `Smooth` | `smooth_contours_xld` | `NumRegrPoints`（奇数，≥3，默认 5） |
| `Close` | `close_contours_xld` | 无 |
| `Clip` | `clip_contours_xld` | `ClipRow1` / `ClipColumn1` / `ClipRow2` / `ClipColumn2` |
| `ShapeTrans` | `shape_trans_xld` | `ShapeType`（`convex` / `ellipse` / `rectangle1` / `rectangle2` / `outer_circle`） |
| `Sort` | `sort_contours_xld` | `SortMode`（`upper_left` / `upper_right` / `lower_left` / `lower_right` / `character`）、`SortAscending`、`RowOrCol` |
| `UnionAdjacent` | `union_adjacent_contours_xld` | `MaxDistAbs`、`MaxDistRel`、`AttrMode`（`attr_keep` / `attr_forget`） |
| `UnionCollinear` | `union_collinear_contours_xld` | `MaxDistAbs`、`MaxDistRel`、`MaxShift`、`MaxAngle`、`AttrMode` |
| `UnionCocircular` | `union_cocircular_contours_xld` | `MaxArcAngleDiff`、`MaxArcOverlap`、`MaxTangentAngle`、`MaxDist`、`MaxRadiusDiff`、`MaxCenterDist`、`MergeSmallContours`、`Iterations` |
| `AffineTrans` | `affine_trans_contour_xld` | 读取可选输入“定位矩阵”（`HomMat2D`） |

- `AffineTrans` 且定位矩阵为空时运行失败：“仿射跟随需要指定定位矩阵”；引用无效时明确失败，不退回原位置（与 README“运行资源与预览约定”一致）。
- 角度类参数在编辑界面按“度”显示，内部转换为弧度传给 HALCON。
- 实现说明：共 9 种操作（即上表全部）。角度参数为 `MaxAngleDeg`、`MaxArcAngleDiffDeg`、`MaxArcOverlapDeg`、`MaxTangentAngleDeg`，默认值等于 HALCON 默认弧度换算成的度数，经 `AngleMath.ToRadians` 传入。各操作的参数默认值取实测的 HALCON 默认值。
  - 流程校验提前报告的配置错误：平滑点数须为奇数且 ≥ 3；裁剪范围须有效；仿射跟随须指定定位矩阵。
  - 仿射跟随使用 `FollowMatrixResolver`：引用无效时明确失败；多个矩阵时提示改用 For 循环。只有在预览上下文显式允许降级时，才保持原位置（与测量工具一致）。

### XG-07 拟合椭圆/矩形

- 工具箱：`05 几何测量 / 拟合椭圆/矩形`，ID `fit-ellipse-rect`。
- 输入：XLD（必填）。
- `FitShape` 枚举：`Ellipse`（默认）/ `Rectangle2`。

| 形状 | 算子 | 算法 `Algorithm` |
|---|---|---|
| `Ellipse` | `fit_ellipse_contour_xld` | `fitzgibbon`（默认）/ `fhuber` / `ftukey` / `geometric` / `geohuber` / `geotukey` / `voss` / `focpoints` / `fphuber` / `fptukey` |
| `Rectangle2` | `fit_rectangle2_contour_xld` | `regression` / `huber` / `tukey`（默认） |

- 公共参数：`MaxNumPoints`、`MaxClosureDist`、`ClippingEndPoints`、`Iterations`、`ClippingFactor`；`VossTabSize` 仅椭圆使用。
- 两种形状输出结构相同（椭圆为半轴，矩形为半边长）：
  - 单值：`Row`、`Column`、`Phi`、`Length1`、`Length2`（取第一个轮廓）。
  - 数组：`Results`（每个轮廓一项）。
  - 图形：`Xld`（用 `gen_ellipse_contour_xld` / `gen_rectangle2_contour_xld` 生成，用于叠加显示）。
  - `Count`、`Found`。
- 不并入拟合直线、拟合圆：两者输出结构不同，合并会破坏现有引用。
- 实现说明：类型为 `FitEllipseRectTool`，算法分为 `EllipseAlgorithm` / `RectangleAlgorithm` 两个参数（矩形默认 `tukey`，HALCON 默认 `regression`），`VossTabSize` 只在 `voss` 算法时显示。`Phi` 经 `AngleMath.Fold` 折算到 [-90°, 90°)。
  - 矩形拟合需要致密轮廓：只有角点的多边形会被 HALCON 拒绝，工具明确失败并说明原因。
  - 半轴 ≤ 0 的退化结果明确失败，不输出；HALCON 22.11 上焦点类算法在部分闭合轮廓上会出现这种结果，见第 10 节。

### XG-08 轮廓距离

- 工具箱：`05 几何测量 / 轮廓距离`，ID `contour-distance`。
- 模式 `DistanceMode`：

| 模式 | 算子 | 输入 |
|---|---|---|
| `PointToContour` | `distance_pc` | 轮廓（必填）、行和列引用（可选，本模式必填） |
| `ContourToContour` | `distance_cc_min_points` | 轮廓 1（必填）、轮廓 2（可选，本模式必填） |

- `ContourToContour` 新增 `CcMode`：`fast_point_to_segment`（默认）/ `point_to_segment`。
- 配对规则与 RG-07 完全一致，直接使用已实现的 `PairingHelper`（`VisionFlow.Tools\Tools\PairingHelper.cs`，RG-07 与数组处理的逐元素运算共用）：
  - 两侧个数相等：第 i 个与第 i 个配对（`distance_cc_min_points` 的原生语义，要求个数相等）。
  - 一侧只有 1 个：与另一侧每个对象分别计算。
  - 两侧个数不等且都大于 1：运行失败，提示“两组对象个数不一致（N 对 M），无法逐一配对”。
  - 不提供 N×M 全组合，需要时用 For 循环（集合）遍历其中一组。
- 输出命名与 RG-07 一致：
  - 数组：`Distances`；`MaxDistances`（仅点到轮廓）；`Rows1` / `Columns1` / `Rows2` / `Columns2`（仅轮廓到轮廓，最近点对）。
  - 单值：`Distance`、`MaxDistance`、`Row1` / `Column1` / `Row2` / `Column2`（取第一个配对）。
  - 显示：`Segments`（XLD，轮廓到轮廓时每个配对一条最近点连线；点到轮廓时为空），由现有叠加显示直接支持，`DisplayOverlayBuilder` 不需要新增画点能力（同 RG-07）。
  - `Count`、`Found`。
- 实现说明：`distance_cc_min_points` 要求两侧个数相等，`distance_pc` 对多个轮廓只返回一个值，因此都按配对逐对 `select_obj` 调用。没有可计算的配对（某一侧没有对象）时按 `FailWhenNotFound` 处理；另对 0 点轮廓做防御性检查（HALCON 实际不产生这种轮廓）。行、列个数不一致时报“点的行、列个数不一致（行 N 个，列 M 个）”。
- 不与 RG-07 区域距离合并：两者输入类型不同，合并后必填输入只能全部设为可选，校验器无法检查。

### XG-09 几何关系测量

- 工具箱：`05 几何测量 / 几何关系测量`，ID `geometry-relation`。
- 定位：输入为拟合直线等工具输出的直线 XLD（沿用线线交点对直线输入的解析方式），以及点坐标；补齐 VisionPro / VisionMaster 中最常用的点线、线线测量。

| 模式 | 算子 | 输入 | 输出 |
|---|---|---|---|
| `PointToLine` | `distance_pl` | 点（行、列引用）、直线 | `Distance`，以及垂足 `FootRow` / `FootColumn`（`projection_pl`） |
| `LineToLineAngle` | `angle_ll` | 直线 1、直线 2 | `Angle`（弧度）、`AngleDeg`（度） |
| `SegmentToSegment` | `distance_ss` | 线段 1、线段 2 | `Distance`、`MaxDistance` |
| `SegmentToLine` | `distance_sl` | 线段、直线 | `Distance`、`MaxDistance` |

- 直线输入都为 XLD；点模式下第二个直线输入为可选，点引用为可选、本模式必填。
- 多对象时按 XG-08 的配对规则输出数组与首个单值。
- 角度范围与“单位换算”（原“角度换算”，见匹配与测量计划 MS-07）工具的 `AngleRange` 约定一致。
- 实现说明：
  - 输入：`直线/线段 1`（必填）、`直线/线段 2`（点到直线时不读取）、点行 / 列坐标（仅点到直线）。“仅某模式必填”的输入由流程校验提前报告。
  - 输出：数组 `Distances` / `MaxDistances` / `Angles` / `AnglesDeg` / `FootRows` / `FootColumns`；对应的单值（取第一对）；`Xld`（点到直线时为垂线段与垂足十字）；`Count` / `Found`。
  - 夹角折算参数 `Range` / `RangeMin` / `RangeMax` 与 `AngleConvertTool` 相同，经 `AngleMath.TryGetBounds` / `Fold` 计算；`Angle`（弧度）与 `AngleDeg`（度）是同一个折算结果。

## 5. 暂缓

- `lines_facet`、`zero_crossing_sub_pix`：特殊线条和过零点提取，按需再加入 XG-01。
- `gen_parallel_contour_xld`：生成平行轮廓，按需再加入 XG-06。
- `select_xld_point`：按点选取轮廓，按需再加入 XG-02。
- `moments_xld` 等矩特征：按需再加入 XG-04。
- `gen_polygons_xld`、`split_contours_xld`：多边形近似，XLD 分割已覆盖大部分需求。

## 6. 每项需要同步修改的位置

- 工具类及 `[ToolOutput]` / `[InputRef]` 元数据（`VisionFlow.Tools`）。新增输出名必须与实际 `SetOutput` 一致。
- 新工具：`BuiltinToolIdentities` 登记固定 `ToolId`；`ToolboxRegistry.RegisterDefaults()` 注册。
- WPF 编辑：`WpfToolEditorRouter.IsVisualPreviewTool()` 路由；编辑窗口按当前模式显示或隐藏相关参数和可选输入——工具实现 `IToolParameterVisibility`；只在某模式下必填的输入实现 `IToolConfigurationCheck`，由流程校验提前报告。
- 叠加显示：新工具输出的 XLD、交点（十字）、最近点对（`Segments` 连线）、垂足（垂线 + 十字）都以 XLD 输出，现有 `DisplayOverlayBuilder` 直接支持，不新增画点能力。
- 工具箱图标：`VisionFlow.WpfApp\Themes\ToolIcons.xaml`。本期四个新工具（`xld-process`、`fit-ellipse-rect`、`contour-distance`、`geometry-relation`）为手绘图标，风格与其他手工追加图标一致；如提供 SVG，可用 `tools\svg_to_tool_icons.py` 重新生成替换。
- 由字符串改为枚举的参数：枚举类型标注 `JsonStringEnumConverter` 强制按名称保存，并补“旧文件字符串值读入枚举”的加载用例。
- 共享帮助类：直线解析 `XldLineHelper`（交点计算、几何关系测量共用），配对规则 `PairingHelper`，数值输入读取 `RegionDistanceTool.ReadNumbers`。
- README 工具表；必要时在 `examples` 增加示例流程。
- `VisionFlow.Tests` 增加对应用例。

## 7. 兼容性要求

- 枚举参数按**数字**序列化（如 `"SegmentMethod": 0`），新增值一律追加在末尾，不得插入或调整已有成员顺序；新增的模式枚举默认值等于现有行为。
- 由字符串改为枚举的现有参数（`ContourCreateTool.Filter`、`SelectContourTool.Operation`）沿用 REGION 的做法：枚举类型标注 `JsonStringEnumConverter` 强制按名称保存，文件内容与旧版一致、旧版程序仍可读取；并有“旧文件字符串值读入枚举”的加载用例。成员名即 HALCON 参数值（必要时含下划线，如 `sobel_fast`）。
- 历史 `.vflow.json` 加载后行为不变：`ContourCreateTool` 默认仍为 `edges_color_sub_pix`；`SelectContourTool` 默认仍为 `select_shape_xld` 且全部保留；`IntersectionLinesTool` 默认仍为直线交点，原有输出不变。
- 改名的工具（交点计算）只改显示名，工具 ID 与类型名不变。
- 可选输入只在被选中的模式下才必需：工具实现 `IToolConfigurationCheck`，流程校验提前报告，运行时也给出明确中文错误信息。

## 8. 验收

- 每个新选项、新工具至少一个结果正确性用例，并覆盖空输入时 `FailWhenNotFound` 两种取值。
- XG-01：同一张灰度图，`Edges` 与 `ColorEdges` 在相同滤波器下结果一致或差异可解释；`LinesGauss` 能在示例图中提取到预期线条。
- XG-04：新增特征值与 `select_shape_xld` 使用同名特征筛选的结果一致。
- XG-05：直线-轮廓、轮廓-轮廓的交点数量与位置与 HALCON 直接调用一致；`LineLine` 模式结果与旧版一致。
- XG-06：`AffineTrans` 无矩阵时失败；`UnionAdjacent` 能把断开的轮廓连成一条。
- XG-07：对 `gen_ellipse_contour_xld` / `gen_rectangle2_contour_xld` 生成的理想轮廓，拟合结果与真值误差在 0.1 像素和 0.1° 内。
- XG-08：覆盖 1 对 1、N 对 N、1 对 N、N 对 1，以及 N 对 M（N≠M）报错；逐一配对结果与 `distance_cc_min_points` 一致。
- XG-09：对已知几何构造的点、线，距离与夹角与解析值一致。
- 加载现有 `examples\*.vflow.json` 全部通过，回归测试全部通过。
- 用例：`VisionFlow.Tests\XldToolPlanTests.cs`（含旧文件 `Filter` / `Operation` 字符串读入枚举、各工具与 HALCON 直接调用对照、XG-08 的 1 对 1 / N 对 N / 1 对 N / N 对 1 / N 对 M 报错 / 配对含空轮廓）。
- 界面验收：用 UI Automation 驱动 `VisionFlow.WpfApp.exe` 完成全部操作——从正常文件对话框打开测试流程，在编辑窗口中修改，从工具箱新建三个新工具节点，然后运行、保存、重新加载。界面显示的数值与无界面运行同一流程的结果逐字比对（结果见提交说明）。

## 9. 开发顺序

1. XG-01 ~ XG-05（现有工具增强）。
2. XG-06（XLD 处理）。
3. XG-07（拟合椭圆/矩形）。
4. XG-08、XG-09（距离与几何关系，与 RG-07 共用配对逻辑，建议放在 RG-07 之后或同期开发）。

## 10. 算子探测结论（开工前实测）

**探测环境**：本机安装的是 **HALCON 22.11 Steady**（`get_system('version')` = 22.11，`HALCONROOT=...\HALCON-22.11-Steady`），没有 20.11，以下结论全部在 22.11 上取得。所用算子在 20.11 中均已存在，但取值清单与默认值以 22.11 实测为准，部署到 20.11 前需复核。方法：用 `get_param_names` / `get_param_info`（`default_value`、`value_list`）读取已安装 HALCON 的参数表，再用合成轮廓逐个实际调用。

评审的两处疑点：

| 疑点 | 实测结论 | 对计划的影响 |
|---|---|---|
| P1-2 `select_contours_xld` 是否支持 `closed` / `open` | **支持**。`Feature` 合法取值为 `contour_length` / `maximum_extent` / `direction` / `curvature` / `closed` / `open`，默认 `Min1=0.5`、`Max1=200`、`Min2=-0.5`、`Max2=0.5`。实测：闭合矩形与开口折线中，`closed`（首尾距离 ≤ `Max1`）选中两者，`open`（首尾距离 ≥ `Min1`）只选中开口折线 | `ContourFeature` 保留计划中的 6 个取值；`closed` 只用 `Max1`，`open` 只用 `Min1`，`curvature` 另用 `Min2` / `Max2`，编辑界面按特征显隐 |
| P1-3 `distance_cc_min_points` 是否有 `Mode` | **有**。`Mode` 取值为 `point_to_segment` / `fast_point_to_segment`，默认 `fast_point_to_segment`；输出 `DistanceMin`、`Row1`、`Column1`、`Row2`、`Column2`。`point_to_point` 只属于 `distance_cc`，而 `distance_cc` 不接受 `fast_point_to_segment`。另外，两侧个数不等时（1 对 2）报错 #1502 | `CcMode` 按计划保留，取值为上述两项。1 对 N 不能直接传给算子，按配对逐对 `select_obj` 调用；最近点对由算子直接给出，验收标准不变 |

其他实测结论（与计划不一致之处已在实现中更正）：

| 算子 | 结论 |
|---|---|
| `edges_color_sub_pix` | `Filter`：`canny` / `deriche1` / `deriche2` / `shen` / `sobel_fast` 及各自的 `_junctions` 变体；**不支持** `lanser1` / `lanser2` / `mshen` / `sobel`（报 #1301）。默认 `Alpha=1, Low=20, High=40` |
| `edges_sub_pix` | `Filter`：另含 `lanser1` / `lanser2` / `mshen` / `sobel` 及其 `_junctions` 变体，默认值同上 |
| `threshold_sub_pix` | `Threshold` 默认 128 |
| `lines_gauss` | 默认 `Sigma=1.5, Low=3, High=8, LightDark=light, ExtractWidth=true, LineModel=bar-shaped, CompleteJunctions=true`；`LightDark` 只有 `light` / `dark`；`LineModel` 为 `none` / `bar-shaped` / `parabolic` / `gaussian`（4 种实测均可用）。`Low` / `High` 是二阶导数阈值，与边缘提取的梯度阈值量纲不同，因此**不复用** `Sigma` / `Low` / `High`，单设 `LineSigma` / `LineLow` / `LineHigh` 并取 HALCON 默认值 |
| `segment_contours_xld` | `Mode` 含 `lines_ellipses` |
| `select_shape_xld` | XG-04 列出的 15 个特征名全部合法（`circularity` … `phi`），可与“特征值”对照 |
| `test_closed_xld` | 可用；闭合输出 1、开口输出 0；空对象输出空元组 |
| `intersection_line_contour_xld` | 不相交时输出空元组，`IsOverlapping=0` |
| `intersection_contours_xld` | `IntersectionType` 为 `all` / `self` / `mutual`，HALCON 默认 `all`；本工具默认 `mutual`（只求两组之间的交点，符合“交点计算”的语义） |
| `smooth_contours_xld` | `NumRegrPoints` 必须为奇数（偶数报 #1301），默认 5 |
| `clip_contours_xld` | 默认 `0, 0, 512, 512`；完全在范围外时不输出对象 |
| `shape_trans_xld` | `Type` 为 `convex` / `ellipse` / `outer_circle` / `rectangle1` / `rectangle2`（无 `inner_circle`） |
| `sort_contours_xld` | `SortMode` 为 `character` / `upper_left` / `lower_left` / `upper_right` / `lower_right`；`Order` 为 `true` / `false`；`RowOrCol` 为 `row` / `column` |
| `union_adjacent_contours_xld` | 默认 `MaxDistAbs=10, MaxDistRel=1, Mode=attr_keep`（另有 `attr_forget`） |
| `union_collinear_contours_xld` | 默认 `MaxDistAbs=10, MaxDistRel=1, MaxShift=2, MaxAngle=0.1 rad, Mode=attr_keep` |
| `union_cocircular_contours_xld` | 8 个参数，默认 `MaxArcAngleDiff=0.5, MaxArcOverlap=0.1, MaxTangentAngle=0.2`（均为弧度），`MaxDist=30, MaxRadiusDiff=10, MaxCenterDist=10, MergeSmallContours=true, Iterations=1`；实测两段同圆圆弧合并为 1 条 |
| `fit_ellipse_contour_xld` | 10 种算法全部可用，默认 `fitzgibbon`，`MaxNumPoints=-1, MaxClosureDist=0, ClippingEndPoints=0, VossTabSize=200, Iterations=3, ClippingFactor=2`。`focpoints` / `fphuber` / `fptukey` 返回的 `Phi` 比其他算法多 π（同一椭圆），工具统一折算到 [-90°, 90°)。**补充实测（开发中发现）**：这三种焦点类算法在 `gen_ellipse_contour_xld` 生成的完整闭合椭圆上（首尾重合，或首尾距离 ≤ `MaxClosureDist`）返回**短半轴 0**，同一椭圆的开口弧结果正确；而 `threshold_sub_pix` 从图像得到的闭合椭圆轮廓（`test_closed=1`，首尾距离 0，383 点）结果正常（界面验收中实测）。即“退化”只出现在部分闭合轮廓上，不是所有闭合轮廓。工具检测到半轴 ≤ 0 时明确失败并提示改用 `fitzgibbon` / `geometric` 等算法，不输出退化结果 |
| `fit_rectangle2_contour_xld` | 算法 `regression`（HALCON 默认）/ `huber` / `tukey`，其他默认值同椭圆（无 `VossTabSize`）。**需要致密轮廓**：`gen_rectangle2_contour_xld` 只有 5 个角点，拟合报 #3274；`gen_contour_region_xld` 生成的阶梯边界报 #3266；按 0.5 像素插值的理想矩形三种算法都精确还原。验收用插值后的理想轮廓 |
| `distance_pc` | 单轮廓对多个点按点返回；多个轮廓对一个点只返回 1 个值，因此按配对逐对调用 |
| `distance_pl` / `projection_pl` / `angle_ll` / `distance_ss` / `distance_sl` | 签名与计划一致；`angle_ll` 为从直线 A 转到直线 B 的有向角，取值 (-π, π]，水平线到竖直向下的线为 -π/2 |
| 空轮廓 | HALCON 不产生 0 点轮廓对象：`clip_end_points_contours_xld` / `crop_contours_xld` 裁空时直接不输出对象，`gen_contour_polygon_xld` 不接受空元组。因此“配对含空轮廓”在实际中表现为某一侧 XLD 没有对象，按“没有可计算的配对”及 `FailWhenNotFound` 处理；工具另对点数为 0 的轮廓保留防御性检查 |

## 11. 评审意见处理（XLD-TOOLS-PLAN-REVIEW.md）

| 编号 | 处理 |
|---|---|
| P1-1 枚举序列化描述错误 | 已更正第 7 节：枚举按数字保存，新增值追加在末尾。`Filter`、`Operation` 改为枚举并强制按名称保存，补了旧文件加载用例 |
| P1-2 `select_contours_xld` 的 `closed` / `open` | 已实测（22.11）：**合法**，评审疑点不成立。`ContourFeature` 保留 6 个取值，见第 10 节 |
| P1-3 `distance_cc_min_points` 的 `Mode` | 已实测（22.11）：**有** `Mode`（`point_to_segment` / `fast_point_to_segment`），评审疑点不成立。`CcMode` 保留，验收标准不变；该算子要求两侧个数相等，因此按配对逐对调用 |
| P1-4 图标 | 四个新工具先用手绘图标，可用脚本以 SVG 替换 |
| P2-1 直线解析共享 | 抽出 `XldLineHelper`，交点计算与几何关系测量共用，原线线交点改为调用它，原有用例全部通过 |
| P2-2 点类输出 | XG-08 增加 `Segments`；XG-05 的交点、XG-09 的垂足以十字 / 连线 XLD 输出，未修改 `DisplayOverlayBuilder` |
| P2-3 仿射跟随 | 复用 `FollowMatrixResolver`：明确拒绝多矩阵，引用无效时失败、不退回原位置 |
| P2-4 显隐与模式必填 | XG-01 / 02 / 05 / 06 / 07 / 08 / 09 实现 `IToolParameterVisibility`。XG-01 / 02 / 06 / 07 / 08 / 09 实现 `IToolConfigurationCheck`；XG-05 的两个输入在各模式下都必填，由原有校验覆盖 |
| P2-5 角度经 `AngleMath` | XG-06 的角度参数按度填写、经 `ToRadians` 传入；XG-07 的 `Phi` 经 `Fold` 折算；XG-09 的夹角经 `TryGetBounds` / `Fold`，用例与 `AngleConvertTool` 对照 |
| P2-6 `distance_pc` 逐对调用 | 已实测，多轮廓对单点只返回一个值，因此逐对调用 |
| P2-7 新算子先探测 | 全部先实测，结论见第 10 节；开发中另发现焦点类椭圆算法的退化结果，已补入第 10 节 |
| P2-8 验收补充 | `ContourFeature` 以下拉呈现（界面验收已核对 6 个取值）；XG-08 补“配对中含空轮廓”“行列 / 轮廓个数不一致”用例 |
| P3 文档 | 第 6、7 节已更新，头部状态已更新，开发顺序未变 |
| 探测环境 | 本机只有 HALCON 22.11，没有 20.11，探测与验收均在 22.11 上完成；部署到 20.11 前需复核第 10 节的取值清单与默认值 |
