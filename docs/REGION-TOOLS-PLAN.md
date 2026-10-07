# Region 相关算子补充开发计划

编写日期：2026-10-06
状态：已实现（RG-01 ~ RG-07，分支 `feature/region-tools`；评审意见的处理见第 10 节）
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
- 实现说明：`LightDark` / `BinaryMethod` 由字符串改为枚举 `ThresholdLightDark` / `BinaryThresholdMethod`（成员名即 HALCON 参数值），并标注按名称保存，流程文件内容与旧版完全相同、旧版程序仍可读取；旧版 `BinaryThresholdTool.Method` 同义属性保留。二值阈值选择 `equal` / `not_equal` 时校验与运行均报“二值阈值只支持 light / dark”。
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
- 实现说明：`FillFeature`（枚举 `RegionFillFeature`，默认 `area`，`FillMin` / `FillMax` 默认 1 / 100）、`BoundaryType`（枚举 `RegionBoundaryType`）、`Percent`（默认 20）；取反结果为一个区域（多个输入区域按合并后计算）。各方式的参数合法性（宽高、半径、范围、裁剪图像）由流程校验提前报告，运行时同样检查。

### RG-03 区域筛选（`SelectRegionTool`）增加多条件、灰度筛选、按序号取件

- 多条件：新增 `Features` / `Mins` / `Maxs`（逗号分隔字符串，个数必须一致）和 `Operation`（`and` / `or`）。保留原 `Feature` / `Min` / `Max`：新字段为空时按原单条件执行，保证历史流程行为不变。
- 灰度筛选：新增 `FilterBy` 枚举（`Shape` / `Gray`），`Gray` 时调用 `select_gray`；新增可选输入 `[InputRef("图像", typeof(HalconImage), Optional = true)] ImagePath`，从工具箱新建时默认 `Input.Image`。`FilterBy = Gray` 且图像为空时运行失败。
- 按序号取件：`RegionTakeMode` 末尾追加 `ByIndex`，新增 `TakeIndex`（从 0 开始），越界按 `FailWhenNotFound` 处理。
- 实现说明：
  - `Operation` 为枚举 `RegionSelectOperation`（`and` / `or`）。`Features` / `Mins` / `Maxs` 任一非空即按多条件执行，三者个数不一致、为 0 或上下限不是数值时，校验与运行均报错（信息含三者个数或出错的项），不静默退回单条件。
  - 执行次序：先按条件（形状或灰度）筛选，再按 `TakeMode` 取件；`ByIndex` 的序号针对筛选结果，越界时输出空区域并按 `FailWhenNotFound` 处理，信息为“取 第 N 个（共 M 个）”。
  - 新字段为空且按形状筛选时，调用与日志均与旧版相同。

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
- 输出：`Xld`（XLD）、`Count`、`Found`。输出名与现有 XLD 工具（边缘提取、边缘选择等，均派生自 `XldToolBase`）一致，下游 XLD 工具引用方式相同；原计划的 `Contours` 不再使用。
- `border` 轮廓沿像素外沿，`center` 轮廓经过像素中心；需要“转 XLD 再转回 Region 面积不变”时用 `center`。
- 反向转换见 RG-06“XLD 转 Region”（`03 区域处理`）。

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
  - 显示：`Segments`（XLD，区域到区域时每个配对一条最近点连线），现有叠加显示直接支持，不需要新增“数组画点”能力；点到区域时为空。
- 实现说明：配对规则由公共帮助类 `PairingHelper` 实现，数组处理的逐元素运算已改用它；所有输出总是写出，当前模式不产生的数组为空、单值为 NaN；行、列个数不一致时报“点的行、列个数不一致（行 N 个，列 M 个）”；含空区域的配对报“第 i 对含空区域（区域第 a 个）”并按 `FailWhenNotFound` 处理。
- 参考：VisionPro 的 12 个 `CogDistance*Tool` 和 VisionMaster 的点点、点线、线线、点圆、线圆、圆圆测量，都是一次只算一对，批量靠脚本或 Group 循环，均无区域间距工具；HALCON `distance_rr_min` 为逐一配对，且要求两侧个数相等。

## 5. 暂缓

- `select_region_point`：作为区域筛选的“包含指定点”取件方式，需额外加行、列引用，等有实际需求再做。
- `watersheds`：同时输出盆地与分水岭，用得少。
- `region_to_bin` / `paint_region`：已在 [图像处理补充开发计划](IMAGE-TOOLS-PLAN.md) IP-07“区域转图像”中规划。
- `hysteresis_threshold` / `regiongrowing`：见 RG-01。

## 6. 每项需要同步修改的位置

- 工具类及 `[ToolOutput]` / `[InputRef]` 元数据（`VisionFlow.Tools`）。
- 新工具：`BuiltinToolIdentities` 登记固定 `ToolId`；`ToolboxRegistry.RegisterDefaults()` 注册。
- WPF 编辑：`WpfToolEditorRouter.IsVisualPreviewTool()` 路由；编辑窗口按当前选项显示或隐藏相关参数——工具实现 `IToolParameterVisibility`，视觉预览/通用编辑窗口和侧边参数面板在切换枚举参数时刷新（隐藏的参数照常保存）。
- 只在某些方式下必填的可选输入、方式相关的参数范围：工具实现 `IToolConfigurationCheck`，流程校验提前报告，运行时同样检查。
- 工具箱图标：`VisionFlow.WpfApp\Themes\ToolIcons.xaml`。本期三个新工具的图标为手工绘制（与其他手工追加图标同风格）；如提供 `tool-region-to-xld.svg` 等 SVG，可用 `tools\svg_to_tool_icons.py` 重新生成替换。
- README 工具表；必要时在 `examples` 增加示例流程。
- `VisionFlow.Tests` 增加对应用例。

## 7. 兼容性要求

- 枚举参数默认按**整数值**保存（如 `"SegmentMethod": 0`），新增值一律追加在末尾，不得插入或调整已有成员顺序，原有默认值不变。由字符串改为枚举的参数（`LightDark`、`BinaryMethod`）标注 `JsonStringEnumConverter` 按名称保存，保持文件内容不变。
- 历史 `.vflow.json` 加载后行为不变：新增参数取默认值时等价于旧逻辑；新增的可选图像输入在历史流程中保持为空。
- 可选输入只在被选中的模式下才必需：由 `IToolConfigurationCheck` 在流程校验时提前报告，运行时也给出明确中文错误信息。

## 8. 验收

- 每个新选项、新工具至少一个结果正确性用例，并覆盖空输入时 `FailWhenNotFound` 两种取值。
- `Complement`：补集与 `裁剪范围 - 区域` 面积一致；未设置裁剪图像时运行失败并提示。
- 区域筛选多条件：与串联多个单条件节点的结果一致；新字段为空时与旧版结果一致。
- 区域距离：覆盖 1 对 1、N 对 N、1 对 N、N 对 1，以及 N 对 M（N≠M）报错；逐一配对结果与 HALCON `distance_rr_min` 一致。
- 加载现有 `examples\*.vflow.json` 全部通过，回归测试全部通过。
- 用例：`VisionFlow.Tests\RegionToolPlanTests.cs`（含旧文件中 `LightDark` 字符串读入枚举、新字段为空时区域筛选与旧版调用和日志一致、配对中含空区域、行列个数不一致等评审补充项）。
- 界面验收（2026-10-07）：用 UI Automation 驱动 `VisionFlow.WpfApp.exe` 完成全部操作。所有编辑都在编辑窗口和侧栏中进行，区域距离节点从工具箱新建，依次运行、保存、重新加载，共 64 项检查全部通过；界面运行结果与无界面运行同一流程的数值逐字一致。
- 验收中发现的既有问题（不属于本计划）：流程用“图像加载”工具取图、未打开 `Input.Image` 时，直接显示其他模块的区域 / XLD 输出没有底图。原因是 `DisplayOverlayBuilder.ResolveDisplayBaseImage` 只查找同模块的 `Image` 或 `Input.Image`。先显示该图像再切换到叠加对象可以正常查看。

## 9. 开发顺序

1. RG-01 ~ RG-04（现有工具增强）。
2. RG-05、RG-06（Region / XLD 互转）。
3. RG-07（区域距离）。配对逻辑建议抽成公共帮助类，供 XLD 计划中的 XG-08 / XG-09 复用。

## 10. 评审意见处理（REGION-TOOLS-PLAN-REVIEW.md）

| 编号 | 处理 |
|---|---|
| P1-1 字符串参数无预置选项 | 采纳。`LightDark`、`BinaryMethod`、`FillFeature`、`BoundaryType`、多条件 `Operation`、互转 `Mode` 均为枚举，编辑界面给下拉。`Features` / `Mins` / `Maxs` 保留逗号分隔文本，个数与数值格式由校验和运行时检查。 |
| P1-2 按选项显示参数 | 不做专用窗口，也不改造成全局“条件行”。新增工具声明接口 `IToolParameterVisibility`，由视觉预览/通用编辑窗口和侧边参数面板统一遵守。这样阈值分割、区域处理、区域筛选、区域距离都保留图像预览，改动只作用于实现该接口的工具。 |
| P1-3 图标 | 三个新工具先用手工绘制的图标（同其他手工追加图标风格），收到 SVG 后可用脚本替换。 |
| P2-1 多条件个数 | 按评审建议：任一新字段非空即按多条件执行，字段不完整直接报错。 |
| P2-2 取件次序 | 先筛选后取件，`ByIndex` 越界并入现有未找到处理。 |
| P2-3 空区域预检 | 已实现并有用例。 |
| P2-4 行列长度 | 已实现并有用例。 |
| P2-5 灰度统计空输入 | 沿用现有模式（空数组、NaN、`Found=false`），并有对齐用例。 |
| P2-6 配对帮助类 | `PairingHelper` 已落地，数组处理逐元素运算改用它；XLD 计划 XG-08 已注明直接使用。 |
| P2-7 编辑器路由 | `XldToRegionTool`、`RegionDistanceTool` 加入视觉预览路由；`RegionToXldTool` 派生自 `XldToolBase`，原有路由已覆盖。 |
| P2-8 最近点对显示 | 不新增“数组画点”能力，改为输出 `Segments`（XLD 连线），现有叠加显示直接支持。 |
| P3-1 文档状态 | 本计划与 `LOGIC-DATA-TOOLS-PLAN.md` 头部状态已更新。 |
| P3-2 兼容性验收 | 已补旧文件字符串读入枚举、新字段为空时与旧版一致的用例。核对中发现枚举实际按整数保存（评审与原计划均写为“按名称”），第 7 节已更正。 |
| P3-3 反向引用 | RG-05 已注明反向转换去处。 |
