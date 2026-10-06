# Region 相关算子补充开发计划

编写日期：2026-10-06
状态：待开发
范围：`VisionFlow.Tools` 中与 Region 相关、尚未覆盖的 HALCON 算子。
关联文档：[XLD 相关算子补充开发计划](XLD-TOOLS-PLAN.md)（XG-08 轮廓距离、XG-09 几何关系测量沿用本文 RG-07 的配对规则）。

## 1. 现状

已覆盖的 Region 能力：

- 阈值分割：`threshold` / `auto_threshold` / `binary_threshold` / `fast_threshold` / `char_threshold` / `var_threshold`。
- 区域处理：`connection` / `fill_up` / `union1` / `skeleton`，圆形与矩形结构元素的开、闭、膨胀、腐蚀。
- 集合运算：`union2` / `difference` / `intersection`。
- 形状转换：`shape_trans`。
- 筛选与特征：`select_shape`（单特征 + 取件方式）、`region_features`、`min_max_gray`。
- 其他：手动 Region（含 `affine_trans_region` 跟随）、区域排序、分区检测、区域定位。

## 2. 拆分原则

- 输入输出与现有工具一致的算子，作为现有工具的新选项并入，不新增工具箱入口（延续 TR-11 收拢入口的做法）。
- 输入类型或输出语义与现有工具都不同的算子，新建工具。
- 结果：4 个现有工具增强，3 个新工具，工具箱净增 3 个入口。

## 3. 并入现有工具

### RG-01 阈值分割（`ThresholdTool`）增加动态阈值

- 算子：`mean_image` + `dyn_threshold`。
- `ThresholdSegmentMethod` 末尾追加 `DynThreshold`。
- 工具内部先对输入图做均值滤波得到参考图，再执行 `dyn_threshold`，仍只需一个图像输入。
- 参数：复用 `MaskWidth` / `MaskHeight`（均值滤波掩膜）；`LightDark` 需支持 `light` / `dark` / `equal` / `not_equal`；新增 `Offset`（默认 5）。
- `UsedThreshold` 输出 `Offset`。
- 后续可选：`HysteresisThreshold`、`RegionGrowing` 以同样方式追加，暂不在本期范围。

### RG-02 区域处理（`RegionProcessTool`）增加 5 种操作

`RegionProcessOp` 末尾依次追加：

| 选项 | 算子 | 参数 |
|---|---|---|
| `FillUpShape` | `fill_up_shape` | 新增 `FillFeature`（`area` / `compactness` / `convexity` / `anisometry` 等），新增 `FillMin` / `FillMax` |
| `Boundary` | `boundary` | 新增 `BoundaryType`（`inner` / `inner_filled` / `outer`） |
| `PartitionRectangle` | `partition_rectangle` | 复用 `Width` / `Height` |
| `PartitionDynamic` | `partition_dynamic` | 复用 `Width`；新增 `Percent` |
| `Complement` | 见下文 | 新增裁剪图像输入 |

#### Complement 的裁剪范围

不使用 HALCON `complement` 依赖系统 `clip_region` 的隐式行为，改为显式裁剪：

- 新增可选输入 `[InputRef("裁剪图像", typeof(HalconImage), Optional = true)] ClipImagePath`。
- 新增默认图像功能：从工具箱新建“区域处理”节点时，`ClipImagePath` 默认填入 `Input.Image`，用户可改为任意上游图像。
- 该默认值在 `ToolboxRegistry` 注册处设置，不放在构造函数中，避免历史流程加载后凭空多出一个 `Input.Image` 引用并触发校验报错。
- 实现：`get_image_size` 取裁剪图像宽高，`gen_rectangle1(0, 0, H-1, W-1)` 得到裁剪范围，再用 `difference(裁剪范围, 输入区域)` 得到补集。
- `Method = Complement` 且 `ClipImagePath` 为空时，运行返回失败：“取反需要指定裁剪图像”。
- 其他操作不读取 `ClipImagePath`；编辑窗口只在选择 `Complement` 时显示该输入。

### RG-03 区域筛选（`SelectRegionTool`）增加多条件、灰度筛选、按序号取件

- 多条件：新增 `Features` / `Mins` / `Maxs`（逗号分隔字符串，个数必须一致）和 `Operation`（`and` / `or`）。保留原 `Feature` / `Min` / `Max`：新字段为空时按原单条件执行，保证历史流程行为不变。
- 灰度筛选：新增 `FilterBy` 枚举（`Shape` / `Gray`），`Gray` 时调用 `select_gray`；新增可选输入 `[InputRef("图像", typeof(HalconImage), Optional = true)] ImagePath`，从工具箱新建时默认 `Input.Image`。`FilterBy = Gray` 且图像为空时运行失败。
- 按序号取件：`RegionTakeMode` 末尾追加 `ByIndex`，新增 `TakeIndex`（从 0 开始），越界按 `FailWhenNotFound` 处理。

### RG-04 灰度统计（`RegionMinMaxGrayTool`）增加均值和标准差

- 算子：`intensity`。
- 新增输出：`Means`、`Deviations`（数组，与区域对象逐一对应），`FirstMean`、`FirstDeviation`（单值）。
- 工具箱显示名由“MinMaxGray 灰度统计”改为“区域灰度统计”，工具 ID `region-min-max-gray` 与类型名不变。

## 4. 新建工具

### RG-05 Region 转 XLD

- 工具箱：`04 XLD轮廓 / Region 转 XLD`，ID `region-to-xld`。
- 算子：`gen_contour_region_xld`。
- 输入：区域（必填）。
- 参数：`Mode`（`border` / `border_holes` / `center`）。
- 输出：`Contours`（XLD）、`Count`、`Found`。

### RG-06 XLD 转 Region

- 工具箱：`03 区域处理 / XLD 转 Region`，ID `xld-to-region`。
- 算子：`gen_region_contour_xld`。
- 输入：XLD（必填）。
- 参数：`Mode`（`filled` / `margin`）。
- 输出：`Region`、`Count`、`Found`。
- 与 RG-05 拆成两个工具，是为了让必填输入能被校验器检查；合成一个工具只能把两个输入都设为可选。

### RG-07 区域距离

- 工具箱：`05 几何测量 / 区域距离`，ID `region-distance`。
- 模式 `DistanceMode`：
  - `PointToRegion`：`distance_pr`。输入区域（必填），行、列引用（可选，本模式必填）。
  - `RegionToRegion`：`distance_rr_min`。输入区域 1（必填），区域 2（可选，本模式必填）。
- 配对规则（已确认：逐一配对 + 一对多）：
  - 两侧个数相等：第 i 个与第 i 个配对，与 HALCON `distance_rr_min` 原生语义一致。
  - 一侧只有 1 个：该对象与另一侧每个对象分别计算（一对多）。
  - 两侧个数不等且都大于 1：运行失败，提示“两组对象个数不一致（N 对 M），无法逐一配对”。
  - 点到区域同样适用上述规则，“点”一侧的个数取行、列数组长度（行、列长度必须一致）。
  - 不提供 N×M 全组合；需要时用 For 循环（集合）遍历其中一组，循环体内用 `Loop.Current` 对另一组做一对多。
  - 不对输入做 `union1`；需要“整体最近距离”时，先用“区域处理 / Union1”合并再输入。
  - 空区域：`distance_rr_min` 不接受空区域，任一配对含空区域时按 `FailWhenNotFound` 处理。
- 输出（数组与配对结果逐一对应，另给第一个结果的单值，方便单对使用）：
  - 数组：`Distances`；`MaxDistances`（仅点到区域）；`Rows1` / `Columns1` / `Rows2` / `Columns2`（仅区域到区域，最近点对）。
  - 单值：`Distance`、`MaxDistance`、`Row1` / `Column1` / `Row2` / `Column2`（取第一个配对）。
  - 统计：`Count`（配对数）、`Found`。
- 参考：VisionPro 的 12 个 `CogDistance*Tool` 和 VisionMaster 的点点、点线、线线、点圆、线圆、圆圆测量，都是一次只算一对，批量靠脚本或 Group 循环，均无区域间距工具；HALCON `distance_rr_min` 为逐一配对，且要求两侧个数相等。

## 5. 暂缓

- `select_region_point`：作为区域筛选的“包含指定点”取件方式，需额外加行、列引用，等有实际需求再做。
- `watersheds`：同时输出盆地与分水岭，用得少。
- `region_to_bin` / `paint_region`：已在 [图像处理补充开发计划](IMAGE-TOOLS-PLAN.md) IP-07“区域转图像”中规划。
- `hysteresis_threshold` / `regiongrowing`：见 RG-01。

## 6. 每项需要同步修改的位置

- 工具类及 `[ToolOutput]` / `[InputRef]` 元数据（`VisionFlow.Tools`）。
- 新工具：`BuiltinToolIdentities` 登记固定 `ToolId`；`ToolboxRegistry.RegisterDefaults()` 注册。
- WPF 编辑：`WpfToolEditorRouter.IsVisualPreviewTool()` 路由；编辑窗口按当前选项显示或隐藏相关参数。
- README 工具表；必要时在 `examples` 增加示例流程。
- `VisionFlow.Tests` 增加对应用例。

## 7. 兼容性要求

- 枚举按名称序列化，新增值一律追加在末尾，原有默认值不变。
- 历史 `.vflow.json` 加载后行为不变：新增参数取默认值时等价于旧逻辑；新增的可选图像输入在历史流程中保持为空。
- 可选输入只在被选中的模式下才必需，校验器无法提前发现漏填，必须在运行时给出明确中文错误信息。

## 8. 验收

- 每个新选项、新工具至少一个结果正确性用例，并覆盖空输入时 `FailWhenNotFound` 两种取值。
- `Complement`：补集与 `裁剪范围 - 区域` 面积一致；未设置裁剪图像时运行失败并提示。
- 区域筛选多条件：与串联多个单条件节点的结果一致；新字段为空时与旧版结果一致。
- 区域距离：覆盖 1 对 1、N 对 N、1 对 N、N 对 1，以及 N 对 M（N≠M）报错；逐一配对结果与 HALCON `distance_rr_min` 一致。
- 加载现有 `examples\*.vflow.json` 全部通过，回归测试全部通过。

## 9. 开发顺序

1. RG-01 ~ RG-04（现有工具增强）。
2. RG-05、RG-06（Region / XLD 互转）。
3. RG-07（区域距离）。配对逻辑建议抽成公共帮助类，供 XLD 计划中的 XG-08 / XG-09 复用。
