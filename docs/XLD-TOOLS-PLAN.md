# XLD 相关算子补充开发计划

编写日期：2026-10-06
状态：待开发
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

### XG-02 边缘选择（`SelectContourTool`）增加轮廓选择和取件方式

- 新增 `SelectBy` 枚举：`Shape`（默认，即现有 `select_shape_xld`）、`Contour`。
- `Contour` 时调用 `select_contours_xld`，新增 `ContourFeature`（`contour_length` / `direction` / `curvature` / `closed` / `open` / `maximum_extent`）、`Min1` / `Max1` / `Min2` / `Max2`。
- 新增 `TakeMode`，取值与区域筛选一致：`All`（默认）/ `Longest` / `Shortest` / `First` / `ByIndex`；新增 `TakeIndex`（从 0 开始）。

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
  - `Count`、`Found`。
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

## 5. 暂缓

- `lines_facet`、`zero_crossing_sub_pix`：特殊线条和过零点提取，按需再加入 XG-01。
- `gen_parallel_contour_xld`：生成平行轮廓，按需再加入 XG-06。
- `select_xld_point`：按点选取轮廓，按需再加入 XG-02。
- `moments_xld` 等矩特征：按需再加入 XG-04。
- `gen_polygons_xld`、`split_contours_xld`：多边形近似，XLD 分割已覆盖大部分需求。

## 6. 每项需要同步修改的位置

- 工具类及 `[ToolOutput]` / `[InputRef]` 元数据（`VisionFlow.Tools`）。新增输出名必须与实际 `SetOutput` 一致。
- 新工具：`BuiltinToolIdentities` 登记固定 `ToolId`；`ToolboxRegistry.RegisterDefaults()` 注册。
- WPF 编辑：`WpfToolEditorRouter.IsVisualPreviewTool()` 路由；编辑窗口按当前模式显示或隐藏相关参数和可选输入。
- 叠加显示：新工具输出的 XLD、交点、最近点对、垂足需在 `DisplayOverlayBuilder` 中可见。
- README 工具表；必要时在 `examples` 增加示例流程。
- `VisionFlow.Tests` 增加对应用例。

## 7. 兼容性要求

- 枚举按名称序列化，新增值一律追加在末尾；新增的模式枚举默认值等于现有行为。
- 历史 `.vflow.json` 加载后行为不变：`ContourCreateTool` 默认仍为 `edges_color_sub_pix`；`SelectContourTool` 默认仍为 `select_shape_xld` 且全部保留；`IntersectionLinesTool` 默认仍为直线交点，原有输出不变。
- 改名的工具（交点计算）只改显示名，工具 ID 与类型名不变。
- 可选输入只在被选中的模式下才必需，校验器无法提前发现漏填，必须在运行时给出明确中文错误信息。

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

## 9. 开发顺序

1. XG-01 ~ XG-05（现有工具增强）。
2. XG-06（XLD 处理）。
3. XG-07（拟合椭圆/矩形）。
4. XG-08、XG-09（距离与几何关系，与 RG-07 共用配对逻辑，建议放在 RG-07 之后或同期开发）。
