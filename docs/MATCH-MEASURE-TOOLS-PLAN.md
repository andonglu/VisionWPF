# 定位匹配与几何测量补充开发计划

编写日期：2026-10-06
状态：第一批 MT-01 / MS-01 已实现（分支 `feature/match-measure-batch1`，见第 10 节）；第二批 MS-02 / MS-07（Fixed）已实现并修复第一批“执行测试”回归（分支 `feature/match-measure-batch2`，见第 11 节）；第三批 MT-02 / MT-03 已实现（分支 `feature/match-measure-batch3`，见第 12、13 节）；其余各项待开发，动工前各做一次小评审。
范围：工具箱“01 定位匹配”和“05 几何测量”中的模板匹配类、测量类工具（`HalconTools.cs`、`DescriptorMatchTools.cs`、`MeasureTools.cs`、`FollowMeasureTools.cs`、`AngleTools.cs`），以及新增的差分检测。
HALCON 版本基线：**22.11 及以上**（第三批起）。原文写“通用形状模型是 20.11 引入的算子”有误：`create_generic_shape_model` 等算子不在 20.11 中，仓库原先引用的 `halcondotnet.dll` 20.11.1 没有这些方法；第三批经使用方确认把编译引用换成 22.11.1（`HALCON-22.11-Steady\bin\dotnet35\halcondotnet.dll`），整个应用的最低 HALCON 版本随之提高到 22.11（见第 12 节）。
关联文档：

- [Region 相关算子补充开发计划](REGION-TOOLS-PLAN.md)
- [XLD 相关算子补充开发计划](XLD-TOOLS-PLAN.md)：拟合椭圆/矩形（XG-07）、轮廓距离（XG-08）、几何关系测量（XG-09）已在该文档中规划，本文不重复。
- [标定补充开发计划](CALIBRATION-TOOLS-PLAN.md)：MS-07 长度换算使用该文档定义的标定结果格式。
- [数据处理、判定与逻辑补充开发计划](LOGIC-DATA-TOOLS-PLAN.md)、[图像处理补充开发计划](IMAGE-TOOLS-PLAN.md)、[识别补充开发计划](RECOGNITION-TOOLS-PLAN.md)。

## 1. 现状

### 1.1 定位匹配

| 工具 | 类型 | 算子 |
|---|---|---|
| 模板匹配 `match` | `HalconModelMatchTool` | `create_shape_model` / `find_shape_model` |
| 灰度匹配 `gray-match` | `HalconGrayMatchTool` | `create_ncc_model` / `find_ncc_model` |
| 缩放形状匹配 `scaled-shape-match` | `HalconScaledShapeMatchTool` | `create_scaled_shape_model` / `find_scaled_shape_model` |
| 局部变形匹配 `deformable-match` | `HalconLocalDeformableMatchTool` | `create_local_deformable_model` / `find_local_deformable_model` |
| 描述子匹配 `descriptor-match` | `DescriptorMatchTool` | `create_uncalib_descriptor_model` / `find_uncalib_descriptor_model` |
| 区域定位 `regionpose` | `RegionPoseTool` | `area_center` + `orientation_region` |

已有的公共能力：基类 `HalconMatchToolBase` 统一模型句柄缓存、预热和释放，“未找到”处理，以及 `MatchCount` / `Items` / `HomMats` / `BestMatch` / `Contours` 等输出。示教在 `WpfMatchToolEditWindow` 中完成，支持多个包含 ROI 和排除 ROI。

### 1.2 几何测量

| 工具 | 类型 | 算子 |
|---|---|---|
| 直线、矩形、圆形、椭圆测量 | `LineFollowMeasureTool` 等 4 个 | metrology（`add_metrology_object_*_measure`） |
| 一维卡尺测量 | `OneDCaliperFollowMeasureTool` | `gen_measure_rectangle2` + `measure_pos` |
| 一维圆弧卡尺测量 | `ArcCaliperFollowMeasureTool` | 沿圆弧布置多个 `gen_measure_rectangle2` + `measure_pos`（径向测量） |
| 角度换算 `angle-convert` | `AngleConvertTool` | 纯计算 |
| 图像坐标转世界坐标 `affine-point` | `AffinePointTool` | 仿射矩阵变换点 |

已有的公共能力：基类 `FollowMeasureToolBase` 统一跟随矩阵、多定位结果遍历、失败项记录、区域初始化（矩形、圆形测量）。metrology 只设置了 `measure_transition` 和 `measure_select` 两个参数。

## 2. 缺口与拆分结论

| 编号 | 缺口 | 处理方式 |
|---|---|---|
| MT-01 | 搜索区域、结果排序 | 并入匹配基类，所有匹配工具共享 |
| MT-02 | 从 XLD / DXF 创建模型、模型原点设置 | 并入模板匹配、缩放形状匹配、通用形状匹配的示教 |
| MT-03 | 通用形状模型：各向异性缩放、多模板同时匹配、杂乱判定、越界匹配、最大变形量、超时 | **新建工具“通用形状匹配”**（原计划的各向异性缩放、多模板匹配并入此工具） |
| MT-04 | 透视变形匹配 | 并入局部变形匹配 |
| MT-05 | 组件匹配 | **新建工具“组件匹配”** |
| MT-06 | 差分检测（印刷、外观缺陷） | **新建工具“差分检测”** |
| MS-01 | metrology 高级参数、得分、边缘点、多实例输出 | 并入 4 个 metrology 测量工具的公共基类 |
| MS-02 | 边缘对（宽度）测量 | 并入一维卡尺和圆弧卡尺 |
| MS-03 | 沿圆弧方向测量 | 并入圆弧卡尺 |
| MS-04 | 模糊测量 | 并入一维卡尺和圆弧卡尺 |
| MS-05 | 灰度投影 | **新建工具“灰度投影”** |
| MS-06 | 找角 | **新建工具“找角”** |
| MS-07 | 长度单位换算（像素到毫米） | 并入角度换算，改名“单位换算” |

结果：共 13 项开发，新建 5 个工具（通用形状匹配、组件匹配、差分检测、灰度投影、找角），工具箱净增 5 个入口。

拆分原则与 Region / XLD 计划一致：输入输出与现有工具一致的并入现有工具；输入类型或输出结构不同的新建工具。现有的模板匹配、缩放形状匹配保持不变，保证历史流程可用；新的匹配能力统一加在“通用形状匹配”上。

## 3. 开发通用做法

每一项开发都按以下步骤进行，后文各项只写差异部分。

1. **工具类**：在对应文件中增加属性和逻辑。新增枚举值一律追加在末尾，新增属性的默认值必须等于现有行为。
2. **元数据**：新增输出同时补 `[ToolOutput]`，名称与 `SetOutput` 完全一致；新增输入用 `[InputRef]`，只在部分模式下需要的设为 `Optional = true`，并在 `Run` 中按模式检查、给出中文错误。
3. **持久化**：`FlowSerializer` 只保存 `string` / `int` / `double` / `bool` / `byte[]` / 枚举类型的公共读写属性。需要保存列表或复杂对象时，打包成一个 `byte[]` 或 CSV 字符串，不扩展序列化器。
4. **新工具**：`BuiltinToolIdentities` 登记固定 `ToolId`，`ToolboxRegistry.RegisterDefaults()` 注册，`WpfToolEditorRouter` 路由到编辑窗口。
5. **编辑窗口**：示教相关改 `WpfMatchToolEditWindow`，测量相关改 `WpfFollowMeasureToolEditWindow` / `WpfEllipseMeasureToolEditWindow`。参数按模式显示或隐藏，布局遵循 `VisionFlow.WpfToolEditors\WPF_EDITOR_DESIGN.md`。
6. **资源**：模型句柄走 `HalconMatchToolBase` 的缓存、预热和释放机制（`CurrentModelKey` / `LoadModel` / `ClearModel`），或实现 `IToolResourceLifecycle`；运行中生成的 HALCON 对象归运行上下文所有，借用的输入不得释放。
7. **测试**：`VisionFlow.Tests` 中每项至少覆盖结果正确性、默认值与旧版一致、`FailWhenNotFound` 两种取值；加载 `examples\*.vflow.json` 全部通过。
8. **文档**：README 工具表和本文状态同步更新。

## 4. 定位匹配

### MT-01 匹配基类增加搜索区域和结果排序

**功能**

- 搜索区域：新增可选输入 `[InputRef("搜索区域", typeof(HalconRegion), Optional = true)] SearchRegionPath`。配置后只在该区域内查找。
- 结果排序：新增 `SortBy` 枚举：`Score`（默认，等于现有顺序）/ `Row` / `Column` / `RowThenColumn` / `ColumnThenRow`；新增 `RowTolerance`（行优先排序时同一行的容差像素，默认 0 表示不分行）。

**开发方法**

- 修改 `HalconMatchToolBase.Run`：`TryLoadImage` 之后，若配置了搜索区域，用 `reduce_domain` 得到缩小定义域的图像传给 `FindMatches`，查找结束后释放该图像。输出 `Image` 仍为原始输入图像。
- 搜索区域为空区域时按“未找到”处理；引用无效时明确失败。
- 排序在 `FindMatches` 返回后、`SetMatchOutputs` 之前对 `items` 重排，并重写每项 `Index`。`BestMatch` 和 `Row` / `Column` / `Angle` / `Score` 单值固定取得分最高的一项，不随排序方式变化。
- 描述子匹配、通用形状匹配、组件匹配都继承该基类，自动获得两项能力。
- 编辑窗口：在查找参数区增加“搜索区域”引用和“排序方式”。

**注意**

- 搜索定义域限制的是模型参考点（示教区域的重心），不是整个模板，也不受 `set_shape_model_origin` 设置的原点影响。界面提示文字需说明这一点。
- 模板超出图像边界时默认找不到；需要越界匹配时使用通用形状匹配（MT-03）的 `border_shape_models`。

**实现说明（已完成）**

- 搜索区域先 `union1` 合并、检查面积后只 `reduce_domain` 一次，缩小图在 `finally` 中释放；多矩阵/循环场景不重复缩小。空区域输出 `MatchCount = 0` 并按 `FailWhenNotFound` 处理；引用无效直接失败。
- 最佳结果在排序**之前**按 `Score` 降序选出，再传给 `SetMatchOutputs`（不再取 `items[0]`）。`Score` 排序保持算子返回顺序（HALCON 已按得分降序），因此默认行为与旧版逐项一致。
- `RowThenColumn` 以每一行第一个结果的行坐标为基准，行差不超过 `RowTolerance` 的归为同一行，行内按列排序；`RowTolerance = 0` 时等同于先按行再按列的严格排序。`RowTolerance < 0` 在流程校验阶段报错，`RowTolerance` 只在 `RowThenColumn` 时显示。
- 匹配编辑窗口“运行参数”页的“执行测试”调用工具自身的 `Run`，因此搜索区域、排序与流程运行结果完全一致；“示教建模”页的测试行为不变。第一批的写法是先 `ApplyToTool()` 把界面参数写入窗口持有的工具实例再运行；主程序传给窗口的是编辑事务（`ToolEditTransaction`）的工作副本，取消时副本被丢弃，节点本身不会被改写，但窗口在测试时就改写了自己持有的实例，违背“参数只在确定时写入”的约定。第二批（评审 B2-0）改为在一次性副本上应用界面参数并运行（`ToolTestRun`），窗口持有的实例只在“确定”时写入，见第 11 节。
- 描述子匹配继承基类，自动获得搜索区域和排序；其专用编辑窗口本批未改，可在侧栏属性中配置。

### MT-02 示教增加 XLD / DXF 建模与模型原点

**影响工具**：模板匹配、缩放形状匹配、通用形状匹配（模型原点另适用于灰度匹配）。

**功能**

- 模型来源：除现有“图像 ROI”外，增加“XLD 轮廓”和“DXF 文件”。
  - XLD 轮廓：在示教窗口中选择上游 XLD 输出（如边缘提取、拟合结果）。
  - DXF 文件：`read_contour_xld_dxf` 读入。
  - 建模：模板匹配用 `create_shape_model_xld`，缩放形状匹配用 `create_scaled_shape_model_xld`，通用形状匹配用 `train_generic_shape_model` 传入 XLD。
- 模型原点：示教时可在图像上拖动或输入模型原点。形状类模型用 `set_shape_model_origin`，灰度匹配用 `set_ncc_model_origin`。匹配输出的 `Row` / `Column` 即为该点（例如引脚中心而不是模板中心）。

**开发方法**

- 运行时代码不变：模型以字节形式保存在工具中，DXF 只在示教时读取，运行不依赖 DXF 文件。
- `WpfMatchToolEditWindow` 增加“模型来源”选项和对应界面；XLD 建模时度量只能用 `ignore_local_polarity`，对比度参数不生效，界面锁定并说明原因。
- 工具新增 `ModelOriginRow` / `ModelOriginColumn`（相对模板参考点的偏移，默认 0）用于界面回显；原点写入模型本身，运行时无需再次设置。
- 修改原点后，跟随基准位姿 `BaseRow` / `BaseColumn` / `BaseAngle` 必须重新确定，界面需提示重新设置基准。

**实现说明（第三批已完成）**

- 示教记录属性（`HalconTemplateMatchToolBase`，默认值即原有行为）：`ModelSource`（`MatchModelSource`：`ImageRoi` 默认 / `Xld` / `Dxf`，按数字保存）、`TeachMetric`、`TeachXldPath`、`TeachXldIndex`（-1 = 全部轮廓）、`DxfPath`、`ModelOriginRow` / `ModelOriginColumn`。它们只记录示教方式，运行只用 `ShapeModelData`；已写入模型字节，侧栏不显示（侧栏改了也不会生效），只在示教页修改。
- 公共建模步骤放在 `MatchModelBuilder`（与界面无关，编辑窗口与测试共用）：`ReadDxf`（文件不存在、解析失败、没有轮廓分别给出明确错误）、`SelectContours`、`CreateShapeModelXld` / `CreateScaledShapeModelXld`（度量固定 `ignore_local_polarity`，最小对比度 auto 时取 5）、`ApplyOrigin`（偏移为 0 时不调用算子，模型字节与原来完全相同）/ `SetOrigin`、`OriginFromPickedPoint`。
- 多轮廓（评审 B3-1-6）：22.11 实测 `create_shape_model_xld` / `create_scaled_shape_model_xld` / `train_generic_shape_model` 都接受多条轮廓，全部轮廓共同组成模板，不会报错或只取第一条。示教页提供“轮廓序号”（-1 = 全部，≥0 取单条，越界明确报错），用于从 `edges_sub_pix` 等多轮廓输出中挑一条。
- 支持范围：模板匹配、缩放形状匹配支持 XLD / DXF 建模；灰度匹配只支持原点（`set_ncc_model_origin`）；局部变形匹配两者都不支持（示教页隐藏对应分组）。流程校验（运行时同样拒绝）：不支持 XLD 的工具配置了 XLD / DXF 来源、XLD / DXF 来源但 `TeachMetric` 不是 `ignore_local_polarity`、局部变形匹配配置了原点。
- 原点：示教页可输入偏移，也可在示教图像上点选（按下拾取、按住拖动，松开结束；参考点 = 示教匹配位置 − 当前原点），点“应用原点”写入模型（替换模型字节），确定时未应用的输入也会先写入。应用后重新做示教测试并显示原点标记；`BaseRow` / `BaseColumn` / `BaseAngle` 不随原点自动换算，界面显示“请重新确定跟随基准”的提示。22.11 实测：设置原点后 `find_*` 输出平移到原点，`get_shape_model_contours` / `get_ncc_model_region` 也相对新原点给出，显示轮廓仍画在目标上。
- XLD 模型的参考点是轮廓外接矩形中心（22.11 实测），示教测试按此位置在示教图像中挑出示教实例并设为基准。

### MT-03 通用形状匹配（新工具）

**定位**

- 工具箱：`01 定位匹配 / 通用形状匹配`，ID `generic-shape-match`，类 `HalconGenericShapeMatchTool : HalconMatchToolBase`。
- 用 HALCON 通用形状模型（22.11，见本文头部基线说明）实现，一个工具覆盖：旋转、等比缩放、行列方向不同比例缩放、多模板同时匹配、杂乱判定、越界匹配、最大变形量、超时。
- 原计划的“缩放形状匹配增加各向异性缩放”和“多模板匹配”新工具不再单独开发，统一由本工具提供。

**算子流程**

- 示教：`create_generic_shape_model` → `set_generic_shape_model_param`（建模参数）→ `train_generic_shape_model`（模板图像或 XLD）。
- 查找：`set_generic_shape_model_param`（查找参数）→ `find_generic_shape_model` → `get_generic_shape_model_result` / `get_generic_shape_model_result_object` → `clear_handle`（释放结果句柄）。
- 多模板：每个模板设置不同的 `model_identifier`，把全部模型句柄一次传给 `find_generic_shape_model`，用结果中的模型标识区分来源。

**参数**

| 分组 | 属性 | 通用形状模型参数 | 说明 |
|---|---|---|---|
| 角度 | `AngleStart` / `AngleEnd` | `angle_start` / `angle_end` | 弧度保存，界面按度显示；训练与查找共用（训练后可改） |
| 缩放 | `ScaleMode` 枚举：`None`（默认）/ `Isotropic` / `Anisotropic` | — | 决定训练时使用哪组缩放参数（建模参数） |
| 缩放 | `IsoScaleMin` / `IsoScaleMax` | `iso_scale_min` / `iso_scale_max` | 等比缩放（建模参数） |
| 缩放 | `ScaleRowMin` / `ScaleRowMax` / `ScaleColumnMin` / `ScaleColumnMax` | `scale_row_*` / `scale_column_*` | 行列方向不同比例缩放（建模参数） |
| 查找 | `MinScore`、`NumMatches`、`MaxOverlap`、`Greediness`、`SubPixel` | `min_score`、`num_matches`、`max_overlap`、`greediness`、`subpixel` | `MinScore` / `NumMatches` 继承自基类；`NumMatches = 0` 表示全部 |
| 查找 | `BorderShapeModels` | `border_shape_models` | 允许模板部分超出图像 |
| 查找 | `MaxDeformation` | `max_deformation` | 允许的轮廓偏移像素 |
| 查找 | `TimeoutMs` | `timeout` | 毫秒（22.11 实测），0 表示不限制 |
| 杂乱 | `UseClutter`、`MaxClutter`、`ClutterContrast` | `use_clutter`、`max_clutter`、`clutter_contrast` | 杂乱区域在示教时绘制，用 `set_generic_shape_model_object` 写入模型；开启时每个模板都须有杂乱区域 |
| 建模 | `NumLevels`、`Metric`、`Optimization`、`ContrastLow`、`ContrastHigh`、`MinContrast`、`MinSize` | `num_levels`、`metric`、`optimization`、`contrast_low`、`contrast_high`、`min_contrast`、`min_size` | 修改后必须重新训练（0 / 留空表示自动） |

- ~~多模板时 `MinScore` 等查找参数可以按模板分别设置（CSV 字符串，留空表示全部使用同一值）。~~ 按第三批评审 B3-1-3 砍掉：查找参数全局生效，对所有模板相同；现场确有需要时另行立项。

**输出**

- 继承匹配基类的全部输出。
- 新增 `ScaleRows` / `ScaleColumns`（数组）。`MatchResultItem` 已有 `ScaleRow` / `ScaleColumn` 字段，直接填入；`Scale` 取两者平均值。
- 多模板相关：`ModelIndices`（数组，每个结果所属模板序号，从 0 开始）、`ModelNames`（数组）、`BestModelIndex`、`BestModelName`、`CountsPerModel`（数组）。

**开发方法**

- 持久化：全部模板打包成一个 `byte[] ModelsData`。格式：版本号、模板数量，然后逐个写入名称长度、名称（UTF-8）、模型长度、模型字节（`serialize_shape_model`，通用形状模型同样适用，22.11 已实测）。模板名另存一份 `TemplateNamesCsv` CSV 字符串，便于在流程文件中查看（原计划命名为 `ModelNames`，与输出 `ModelNames` 冲突，按评审 B3-1-1 改名）。
- `CurrentModelKey` 沿用现有模式：`ModelsData` 的数组引用，示教/导入整体替换数组（按评审 B3-1-5，不做内容哈希）；`LoadModel` 反序列化全部模型；`ClearModel` 逐个释放。
- 查找参数在每次 `FindMatches` 时写入缓存的模型句柄（开销小），建模参数只在示教时设置。基类已对同一实例的运行加锁，写参数不存在并发问题。
- 跟随矩阵和模型轮廓变换使用 `get_generic_shape_model_result` 返回的完整变换矩阵，不能只用 `Scale` 重建，否则各向异性缩放时结果错误。
- 编辑窗口：复用 `WpfMatchToolEditWindow` 的示教区，增加模板列表（添加、删除、改名、上移、下移）；每个模板单独示教，可单独设置原点（MT-02）和杂乱区域。模板顺序调整后提示下游引用的模板序号可能需要更新。

**实现说明（第三批已完成）**

- 类型与文件：`GenericShapeMatchTools.cs`（`ScaleMode` 枚举、`GenericShapeTemplate`、`GenericShapeModelData` 打包格式、`GenericShapeTraining` 示教步骤、`HalconGenericShapeMatchTool`）；四件套登记：`BuiltinToolIdentities`（`generic-shape-match`）、`ToolboxRegistry`（`01 定位匹配 / 通用形状匹配`，默认模块名“通用形状匹配N”）、`WpfToolEditorRouter`（在单模板匹配窗口之前路由）、README；手绘图标 `ToolIcon.generic-shape-match`。
- `ModelsData` 格式（小端）：`int32` 版本号 1、`int32` 模板数，逐模板 `int32` 名称字节数 + UTF-8 名称 + `int32` 模型字节数 + 模型字节。版本不符（“版本 N 不受支持（当前版本 1）”）、长度越界（“截断或损坏”）、末尾多余字节、模板数无效都给出明确错误；流程校验与运行开始时同样拒绝。模板名不能为空、不能含逗号、不能重名；`TemplateNamesCsv` 非空且与模型数据中的名称不一致时校验报错。
- 模型标识：加载时把模板序号（字符串 "0"、"1"…）写入 `model_identifier`（22.11 实测该参数只接受字符串，默认是随机的 `SBM-xxxx`），结果按标识映射回模板序号与名称。
- 输出：继承匹配基类全部输出；`MatchResultItem` 新增 `ModelIndex` / `ModelName`（单模板匹配分别为 0 / null）；新增 `ScaleRows`、`ScaleColumns`、`ModelIndices`、`ModelNames`、`BestModelIndex`（未找到为 -1）、`BestModelName`、`CountsPerModel`（长度 = 模板数）。数组随 `SortBy` 排序；搜索区域与排序由基类自动获得（用例固定）。
- 跟随矩阵：`HomMat = hom_mat_2d · 基准位姿⁻¹`（基准位姿全部模板共用，为 0 时就是 `hom_mat_2d` 本身）；显示轮廓直接取 `get_generic_shape_model_result_object(…, 'contours')`，22.11 实测与 `affine_trans_contour_xld(模型轮廓, hom_mat_2d)` 逐点相同；`hom_mat_2d` 等于按 scale_row / scale_column → angle → row, column 组合的矩阵。用例固定各向异性缩放下 `HomMats` 与轮廓和直接算子调用逐项相同。
- 建模参数（按使用方确认）：缩放方式与范围、金字塔、度量、优化、对比度、最小对比度、最小尺寸只在训练时生效（22.11 实测训练后修改缩放、金字塔、度量、对比度、优化、最小尺寸会使模型需要重新训练），侧栏不显示，示教页按 `ScaleMode` 显示对应的缩放分组（`IsScaleParameterVisible`）。角度范围训练与查找共用，侧栏可改。
- 杂乱区域：示教页用 ROI 画出（示教图像坐标），`set_generic_shape_model_object(…, 'clutter_region')` 写入当前模板；HALCON 按训练位姿自动换算（`clutter_hom_mat_2d`），序列化后保留。22.11 实测：同一次查找的全部模型杂乱判定须一致——没有杂乱区域的模型不能开启 `use_clutter`（#8516），开关不一致时报 #8517，因此开启杂乱判定时运行前检查每个模板都有杂乱区域，缺少时明确报出模板名。重新训练会清除该模板的杂乱区域。
- 句柄：结果句柄在 `finally` 中 `clear_handle`（含异常路径）；模板句柄逐个 `clear_handle`；超时（#9400）与“需要重新训练”（#8673）映射为中文错误；编辑窗口的示教测试与执行测试都在一次性副本上运行，结束后 `FlowResources.Release` 释放副本缓存的句柄。
- 编辑窗口：评审建议复用 `WpfMatchToolEditWindow`，实际新建专用窗口 `WpfGenericShapeMatchEditWindow`：通用形状匹配是多模板、参数集合也不同（没有 `ShapeModelData`、`FindStartAngle` 等），塞进单模板窗口需要在每个分支判断工具类型。新窗口沿用同样的页签、卡片布局、ROI 控件、原点点选和 `ToolTestRun`。示教页：模板列表（添加、删除、改名、上移、下移，顺序调整后提示下游序号）、模型来源（图像 ROI / XLD / DXF，XLD 时度量锁定）、建模参数、原点、杂乱区域、基准姿态、示教测试结果；运行参数页：查找参数（对全部模板生效）、搜索区域、排序与测试结果（含模板名列）。

### MT-04 局部变形匹配增加透视变形模式

**功能**

- 新增 `DeformationType` 枚举：`Local`（默认，现有逻辑）/ `Planar`。
- `Planar` 调用 `create_planar_uncalib_deformable_model` / `find_planar_uncalib_deformable_model`，适合倾斜拍摄的平面目标（标签、包装面）。

**开发方法**

- `Planar` 返回投影变换矩阵。参照 `DescriptorMatchTool` 的做法：用投影变换得到模型角点的图像位置，再用 `vector_to_hom_mat2d` 求近似仿射矩阵写入 `HomMat`，并用投影变换后的模型轮廓作为显示轮廓。
- 结果中新增 `Projective`（9 元素数组，保存原始投影矩阵），供需要精确投影的下游使用。
- `CurrentModelKey` 包含 `DeformationType`；切换后需重新示教。

### MT-05 组件匹配（新工具）

**定位**

- 工具箱：`01 定位匹配 / 组件匹配`，ID `component-match`，类 `HalconComponentMatchTool : HalconMatchToolBase`。
- 用于由多个部件组成、部件之间相对位置可在一定范围内变化的目标（如带活动部件的零件、多个元件组成的组件）。

**算子流程**

- 示教：在模型图像上绘制多个部件 ROI，为每个部件设置允许的相对偏移（行、列、角度），调用 `create_component_model`。
- 查找：`find_component_model` → `get_found_component_model`（取部件区域用于显示）。

**参数**

- 部件变化范围：`VariationRows` / `VariationColumns` / `VariationAngles`（CSV，按部件顺序）。
- 根部件：`RootComponent`（默认按 `create_component_model` 返回的排名取第一个）。
- 查找：`AngleStart` / `AngleExtent`、`MinScore`、`NumMatches`、`MaxOverlap`、`MinScoreComp`、`SubPixelComp`、`GreedinessComp`。
- 策略：`IfRootNotFound`（`select_new_root` / `stop_search`）、`IfComponentNotFound`（`prune_branch` / `search_from_upper` / `search_from_best`）、`PosePrediction`（`none` / `from_neighbors` / `from_all`）。

**输出**

- 继承匹配基类的全部输出；每个找到的整体实例作为一个 `MatchResultItem`，位置取根部件位姿。
- 新增部件输出（数组，按“实例 → 部件”顺序展开）：`ComponentRows`、`ComponentColumns`、`ComponentAngles`、`ComponentScores`、`ComponentIndices`（部件序号）、`ComponentInstanceIndices`（所属实例序号）。
- 新增 `ComponentCount`（每个实例找到的部件数）。

**开发方法**

- 持久化：`serialize_component_model` 得到的字节保存在 `byte[] ComponentModelData`；部件名称另存 `ComponentNames` CSV。
- 编辑窗口：新建 `WpfComponentMatchToolEditWindow`，复用 `WpfRoiEditorControl` 绘制多个部件 ROI，每个部件一行参数（名称、行列角度变化范围）。
- 第一期只支持手动设置变化范围；用多张训练图自动求部件相对运动（`train_model_components` + `create_trained_component_model`）作为第二期。

### MT-06 差分检测（新工具）

**定位**

- 工具箱：新增分类 `08 缺陷检测 / 差分检测`，ID `variation-inspect`，类 `VariationInspectTool : ToolBase, IToolResourceLifecycle`。
- 用多张良品图训练出“标准图 + 允许偏差图”，检测时找出超出偏差的区域，用于印刷、标签、表面外观缺陷。对标 VisionPro PatInspect。

**算子流程**

- 训练：`create_variation_model` → 多次 `train_variation_model` → `prepare_variation_model`。
- 只有一张良品图时：`prepare_direct_variation_model`（以良品图为标准图，以其边缘幅值图作为偏差图）。
- 检测：`compare_ext_variation_model` → 可选 `connection` + 按面积筛选。

**输入**

- 图像（必填）。
- 定位矩阵（可选）：配置后先用 `affine_trans_image` 把当前图像对齐到模型位置再比较，通常接在模板匹配的 `BestHomMat` 之后。
- 检测区域（可选）：只在该区域内比较，忽略区域外的差异。

**参数**

- `ModelMode`：`standard` / `robust` / `direct`（建模方式，修改后需重新训练）。
- `AbsThreshold`、`VarThreshold`：亮、暗两个方向可分别设置（CSV 两个值，或一个值共用）。
- `CompareMode`：`absolute` / `light` / `dark` / `light_dark`。
- `MinDefectArea`：小于该面积的差异区域忽略。

**输出**

- `DefectRegion`（缺陷区域，已按面积筛选并拆分连通域）、`DefectCount`、`DefectAreas`（数组）、`MaxDefectArea`、`HasDefect`（bool）。
- 未检测到缺陷是正常结果，不按“未找到”处理，不使用 `FailWhenNotFound`。

**开发方法**

- 持久化：`serialize_variation_model` 得到的字节保存在 `byte[] VariationModelData`。模型至少包含两幅浮点图，体积至少约为“图像宽 × 高 × 8 字节”，大图会使流程文件明显变大。
  - 保存时超过 10 MB 给出提示。
  - 另提供 `ModelFile`（外部模型文件路径）选项：配置后从文件加载，不再内嵌。外部文件由上层项目负责部署，与 README 中外部文件路径的约定一致。
- 模型句柄按 `VariationModelData` 哈希或文件路径加修改时间缓存，支持预热和释放。
- 检测时图像尺寸必须与模型一致，否则明确失败。
- 编辑窗口：新建 `WpfVariationInspectToolEditWindow`，提供“加入当前图像为训练样本”“从文件夹批量加入”“清空样本”“训练”，显示已训练样本数、标准图和偏差图预览，以及当前图像的检测结果叠加。训练样本图像不保存进流程文件，只保存训练结果。

### 4.7 暂缓

- 三维匹配（表面匹配、三维形状匹配）：需要三维数据输入，超出当前二维图像流程的范围，不纳入本计划。

## 5. 几何测量

### MS-01 metrology 测量增加高级参数和输出

**影响工具**：直线、矩形、圆形、椭圆测量。

**新增参数**（默认值等于 HALCON 默认值，保证现有结果不变）：

| 属性 | metrology 参数 | 默认 |
|---|---|---|
| `NumMeasures` | `num_measures` | 0（不设置，按 `measure_distance` 布置） |
| `MeasureDistance` | `measure_distance` | 10 |
| `MinScore` | `min_score` | 0.7 |
| `NumInstances` | `num_instances` | 1 |
| `DistanceThreshold` | `distance_threshold` | 3.5 |
| `MeasureInterpolation` | `measure_interpolation` | `nearest_neighbor` |

**新增输出**

- `Score`（单值）和 `Scores`（数组）：`get_metrology_object_result` 的 `score`。
- `MeasurePoints`（XLD 十字）：`get_metrology_object_measures` 得到的全部边缘点，用于判断卡尺是否找到边缘。
- 多实例（`NumInstances > 1`）：`InstanceCount`，以及按实例展开的几何参数数组（直线为 `InstanceRows1` / `InstanceColumns1` / `InstanceRows2` / `InstanceColumns2`，圆为 `InstanceRows` / `InstanceColumns` / `InstanceRadii`，矩形、椭圆同理）。现有单值输出仍取第一个实例。

**开发方法**

- 在 `FollowMeasureToolBase` 与 4 个 metrology 工具之间增加中间基类 `MetrologyMeasureToolBase`，新参数只放在这个基类上，一维卡尺类工具不受影响。类层次变化不影响序列化（按属性名保存）。
- 扩展 `AddMetrologyParams`：`NumMeasures > 0` 时传 `num_measures`，否则传 `measure_distance`；其余参数始终传入。
- 扩展 `ApplyMetrology`：取全部实例（`get_metrology_object_result` 的实例参数传 `all`）、得分和边缘点。
- 多个定位矩阵与多实例同时存在时，实例数组按“定位结果 → 实例”顺序展开，并输出 `InstanceSeedIndices`（所属定位结果序号）。
- 编辑窗口增加“高级参数”折叠区，默认收起。

**实现说明（已完成）**

- 新基类位于 `MetrologyMeasureTools.cs`，新增 `MetrologyInterpolation` 枚举（`nearest_neighbor` / `bilinear` / `bicubic`）。测量基类 `RunSeeds` 增加 `OnSeedsStarting` / `WriteSeedOutputs` 两个虚钩子，一维卡尺类工具不重写，行为不变。
- 多实例沿用 `SeedCount` / `SeedIndex` 机制：每个实例记录所属定位结果序号，输出 `InstanceSeedIndices`。
- `Score` 为最后一次成功测量第一个实例的得分（无结果时为 NaN）；`MeasurePoints` 用 `XldLineHelper.Crosses` 生成，包含测量失败的定位结果上找到的边缘点，便于排查卡尺问题。
- 参数校验（流程校验阶段报错，运行时也检查）：卡尺数不能小于 0；按间距布置时卡尺间距必须大于 0；最低得分在 0 到 1 之间；实例数大于 0；距离阈值不能小于 0。`NumMeasures > 0` 时隐藏 `MeasureDistance`。
- “高级参数”折叠区（`MetrologyAdvancedExpander`）同时用于跟随测量编辑窗口和椭圆测量编辑窗口。

### MS-02 卡尺增加边缘对（宽度）测量

**影响工具**：一维卡尺测量、一维圆弧卡尺测量。

**功能**

- 新增 `EdgeMode` 枚举：`Edge`（默认，现有 `measure_pos`）/ `Pair`（`measure_pairs`）。
- `Pair` 时 `MeasureTransition` 可选 `all` / `positive` / `negative` / `all_strongest` / `positive_strongest` / `negative_strongest`，`MeasureSelect` 可选 `all` / `first` / `last`。

**新增输出**（仅 `Pair` 模式写入，`Edge` 模式写空数组和 NaN）

- `Widths`（每对边缘的宽度，`IntraDistance`）、`Gaps`（相邻边缘对之间的间距，`InterDistance`）。
- `PairCenterRows` / `PairCenterColumns`。
- `FirstWidth`、`PairCount`。
- `PairResults`：新结果类型 `OneDCaliperPairResult`（第一边缘、第二边缘、宽度、中心、所属卡尺序号）。

**开发方法**

- 在 `FollowMeasureToolBase` 增加受保护的公共方法执行 `measure_pairs` 并转换结果，供两个卡尺工具共用。
- `Pair` 模式下现有边缘输出（`Rows` / `Columns` 等）依次写入每对的两条边缘，保持“边缘点”语义，旧的下游引用仍能取到数据。
- 显示轮廓中，边缘对用连线和宽度标注区分。
- 对标 VisionPro `CogCaliperTool` 的边缘对模式和 VisionMaster 的卡尺宽度测量。

**实现说明（第二批已完成）**

- 新增中间基类 `CaliperMeasureToolBase`（`CaliperMeasureTools.cs`，一维卡尺与圆弧卡尺共用），放 `EdgeMode`、边缘对输出、`IToolConfigurationCheck` / `IToolParameterVisibility`，与 MS-01 的 `MetrologyMeasureToolBase` 同一做法，metrology 工具不受影响。`measure_pairs` 的调用与结果转换（`MeasureCaliperPairs`、`PairsToEdgeResults`）及尺寸线构建放在 `FollowMeasureToolBase`，与 `measure_pos` 的 `ToCaliperResults` 并列。
- 圆弧卡尺沿圆弧布置的每个卡尺本身就是 `gen_measure_rectangle2` 矩形测量句柄（径向），直接对这些句柄调用 `measure_pairs`；合成圆环（内外半径 40 / 55）实测每个径向卡尺得到一对、宽度约 15，与逐卡尺直接调用结果逐项相同。
- `Edge` 模式代码路径与原实现逐项相同（用例对照直接调用 `measure_pos`），边缘对输出写空数组、`PairCount = 0`、`FirstWidth = NaN`，并通过 `IToolParameterVisibility` 按输出名对引用候选隐藏（编辑器下拉与声明级校验共用同一作用域，单边缘模式下引用这些输出会在校验时报不可见）。
- `Pair` 模式下原有边缘输出的语义：`Rows` / `Columns` / `Amplitudes` / `Results` 依次写入每对的第一、第二条边缘，长度 = 2 × `PairCount`，“第 N 条边缘”的序号含义随之变化；`Distances` 仍为同一卡尺内相邻边缘的距离，依次为宽度、间距交替（每个卡尺 2 × 对数 − 1 个）；`FirstDistance` 即第一对的宽度。
- `PairResults`（`OneDCaliperPairResult`：定位结果序号、卡尺序号、卡尺内对序号、两条边缘的坐标与幅值、宽度、中心）与其他边缘对输出一样只含本次运行的结果（`Results` 仍按原约定跨循环累积）。
- 校验（流程校验与运行前）：单边缘模式边缘极性只能是 `all` / `positive` / `negative`，选了 `*_strongest` 提示“只用于边缘对模式”；边缘对模式可用 6 个值；两种模式的边缘选择都只能是 `all` / `first` / `last`。
- 显示轮廓：卡尺矩形 + 边缘十字之外，每对加 3 段尺寸线（两边缘连线 + 两端垂直于测量方向的挡线，挡线半长 = 卡尺半宽 + 十字大小），全部由 `XldLineHelper.Segments` 生成；仓库没有文字叠加能力、XLD 也不能携带文字，宽度数值在 `Widths` 输出、日志和编辑窗口结果表（边缘对模式显示 `PairResults`）中查看。
- 编辑窗口 `WpfFollowMeasureToolEditWindow`：卡尺类工具显示“边缘模式”下拉（单边缘 / 边缘对（宽度测量）），边缘极性下拉随模式切换（当前值在新模式不可用时退回 `all`），边缘选择两种模式相同。

### MS-03 圆弧卡尺增加沿圆弧方向测量

**功能**

- 新增 `ArcDirection` 枚举：`Radial`（默认，现有逻辑：沿半径方向找边，每个卡尺一个矩形测量区域）/ `Tangential`（`gen_measure_arc`：沿圆周方向找边，如齿轮齿、圆周上的槽）。
- `Tangential` 新增 `AnnulusRadius`（圆环半宽），复用 `StartPhi` / `EndPhi`；`CaliperCount` 不使用。

**开发方法**

- `ArcCaliperFollowMeasureTool.MeasureOne` 按方向分支；`Tangential` 只生成一个圆弧测量句柄，结果统一转换为 `OneDCaliperMeasureResult`。
- `Distance` 在 `Tangential` 模式下为沿圆弧的距离（`measure_pos` 原生输出），在日志和界面中注明。
- 与 MS-02 组合后可测量圆周方向的边缘对宽度。
- 编辑窗口预览需绘制圆环测量区域。

### MS-04 卡尺增加模糊测量

**影响工具**：一维卡尺测量、一维圆弧卡尺测量。

**功能**

- 新增 `FuzzyEnabled`（默认 false）。开启后使用 `fuzzy_measure_pos` / `fuzzy_measure_pairs`，按以下隶属条件挑选边缘：
  - 对比度：`FuzzyContrastLow` / `FuzzyContrastHigh`（`set_fuzzy_measure` 的 `contrast`）。
  - 边缘对宽度（仅 `Pair`）：`FuzzyPairSize` / `FuzzyPairSizeTolerance`（`size`）。
  - 阈值：`FuzzyThreshold`（默认 0.5）。
- 新增输出 `FuzzyScores`。

**开发方法**

- 用 `create_funct_1d_pairs` 由上述参数生成隶属函数，测量句柄创建后调用 `set_fuzzy_measure`。
- 只暴露对比度和宽度两种常用隶属条件，其他条件（位置、灰度）暂不开放。

### MS-05 灰度投影（新工具）

**定位**

- 工具箱：`05 几何测量 / 灰度投影`，ID `gray-projection`，类 `GrayProjectionFollowTool : FollowMeasureToolBase`。
- 沿矩形测量区域的长轴方向输出灰度曲线，用于观察边缘形态、调卡尺参数，或直接按曲线做判断（如检测有无、亮度均匀性）。
- 输出是曲线而不是边缘，按拆分原则新建工具；测量区域、跟随矩阵逻辑与一维卡尺相同。

**参数与算子**

- 测量区域：`BaseRow` / `BaseColumn` / `BasePhi` / `BaseLength1` / `BaseLength2`（与一维卡尺一致），`gen_measure_rectangle2` + `measure_projection`。
- `Smooth`：可选高斯平滑（`smooth_funct_1d_gauss`），0 表示不平滑。

**输出**

- `Profile`（数组，灰度曲线）、`Derivative`（数组，一阶导数，`derivate_funct_1d`）。
- `MinGray`、`MaxGray`、`MeanGray`、`MinPosition`、`MaxPosition`（沿长轴的位置）。
- 多个定位矩阵时，单值取最后一次成功的测量（与基类约定一致），全部曲线放入 `Results`。

**开发方法**

- 编辑窗口：在现有跟随测量窗口中增加曲线显示区（WPF `Polyline` 即可），曲线随参数实时刷新。

### MS-06 找角（新工具）

**定位**

- 工具箱：`05 几何测量 / 找角`，ID `corner-find`，类 `CornerFindTool : MetrologyMeasureToolBase`。
- 在一个 metrology 模型中放两个直线测量对象，测出两条边后求交点和夹角。对标 VisionPro `CogFindCorner`。

**参数**

- 两条边的示教位置：`Line1Row1` / `Line1Column1` / `Line1Row2` / `Line1Column2`，`Line2Row1` / `Line2Column1` / `Line2Row2` / `Line2Column2`。
- 其他测量参数继承 `MetrologyMeasureToolBase`（MS-01）。

**输出**

- `CornerRow` / `CornerColumn`（`intersection_lines`）。
- `Angle`（弧度）/ `AngleDeg`（度）：两条边的夹角（`angle_ll`），角度范围约定与“单位换算”一致。
- 两条边的端点：`Line1Row1` 等 8 个单值。
- `Score1` / `Score2`、`Found`、`Results`（多个定位矩阵时逐个记录）。

**开发方法**

- 两条边平行（夹角小于 0.5°）时无交点，按“未找到”处理。
- 编辑窗口：在现有直线测量窗口基础上支持绘制两条线。

### MS-07 角度换算扩展为单位换算

**功能**

- 工具箱显示名由“角度换算(弧度/角度)”改为“单位换算”，ID `angle-convert` 与类型名不变。
- 新增 `ConvertKind` 枚举：`Angle`（默认，现有逻辑）/ `Length`。
- `Length` 模式把像素长度换算为物理长度，换算系数来源 `ScaleSource`：
  - `Fixed`：直接填写像素当量 `PixelSize`（毫米/像素）。
  - `Calibration`：读取标定结果（[标定补充开发计划](CALIBRATION-TOOLS-PLAN.md) CB-01 的 `.vfcal.json`，兼容旧的矩阵文件），或引用标定矩阵（新增可选输入 `[InputRef("标定矩阵", typeof(HomMat2D), Optional = true)]`，可接“图像坐标转世界坐标”或“畸变校正”输出的 `Matrix`），用 `hom_mat2d_to_affine_par` 取行、列方向缩放系数；两者不同时取平均值并在日志中给出两者差异。
- 新增 `LengthUnit`（`mm` / `um`）。

**开发方法**

- 输入、输出（`Value` / `Values` / `Count`）与现有工具一致，按拆分原则并入。
- 标定文件读取复用 CB-01 标定服务的加载与缓存逻辑。
- 面积换算（像素当量平方）作为 `Length` 模式下的 `IsArea` 开关一并提供。

**实现说明（第二批已完成，仅 Fixed 当量）**

- 本批 `ScaleSource` 只有 `Fixed` 一个取值；`Calibration` 来源（标定文件 / 标定矩阵）推迟到 CB-01 标定批评审后连同标定服务一起加，不预留无效选项。
- 新增 `ConvertKind`（`Angle` 默认 / `Length`）、`ScaleSource`（`Fixed`）、`PixelSize`（毫米/像素，默认 1，须大于 0）、`LengthUnit`（`mm` 默认 / `um`）、`IsArea`；新枚举按数字保存，历史文件缺省时为 `Angle`，行为与旧版逐项相同（用例对照旧版公式穷举单位与范围组合）。
- 换算：长度 = 像素值 × `PixelSize`；`IsArea` 时 × `PixelSize`²；`um` 时长度再 × 1000、面积再 × 1000²（mm² → um²）。`Value` / `Values` / `Count` 与 `Angle` 模式同构，NaN 原样输出。
- 校验：`Length` 模式 `PixelSize` 必须大于 0（流程校验与运行时）；`Angle` 模式不新增任何校验，自定义范围仍只在运行时检查（与旧版相同）。
- 参数显隐：`Angle` 模式只显示角度参数，`Length` 模式只显示 `ScaleSource` / `PixelSize` / `LengthUnit` / `IsArea`；输入引用的显示名由“角度”改为“输入值”。
- 工具箱显示名改为“单位换算”，ID `angle-convert`、类型名 `AngleConvertTool` 与新建节点的默认模块名（“角度换算N”）不变；无专用编辑窗口，经侧栏配置。

## 6. 兼容性要求

- 枚举参数按**数字**序列化（如 `"SortBy": 0`），新增值一律追加在末尾，不得插入或调整已有成员顺序；新增模式枚举的默认值等于现有行为。只有把现有字符串参数改成枚举时，才需要标注 `JsonStringEnumConverter` 强制按名称保存（REGION / XLD 两批的做法）；本计划第一批的新枚举（`MatchSortBy`、`MetrologyInterpolation`）没有对应的旧字符串参数，正常追加即可。
- 历史 `.vflow.json` 加载后行为不变：
  - 匹配：未配置搜索区域、`SortBy = Score`、`DeformationType = Local`；模板匹配和缩放形状匹配算法不变。
  - 测量：metrology 新参数等于 HALCON 默认值；`EdgeMode = Edge`、`ArcDirection = Radial`、`FuzzyEnabled = false`；`ConvertKind = Angle`。
- 已有输出名称、类型和含义不变，只新增输出。
- 改名的工具（单位换算）只改显示名，工具 ID 与类型名不变。
- 修改模型类型的选项（`DeformationType` 等）必须纳入模型缓存键，切换后要求重新示教，不能用旧模型数据运行新模式。
- 同步清单（含编辑器）：新参数除工具类、`[ToolOutput]` / `[InputRef]`、`IToolParameterVisibility` / `IToolConfigurationCheck`、测试外，还必须同步编辑窗口——匹配类改 `WpfMatchToolEditWindow`（经 `WindowsFormsHost` 承载 ROI 控件，新增 WPF 控件注意 DPI 与布局风格），metrology 测量改 `WpfFollowMeasureToolEditWindow` 和 `WpfEllipseMeasureToolEditWindow`；新工具另需 `BuiltinToolIdentities`、`ToolboxRegistry`、`WpfToolEditorRouter` 与 README 工具表。

## 7. 验收

- MT-01：搜索区域内外各放一个目标，只找到区域内的目标；排序结果正确且 `BestMatch` 始终为最高分。
- MT-02：用 `gen_rectangle2_contour_xld` 生成的 XLD 建模，能在对应图像中找到目标；设置原点后输出坐标等于原点位置。
- MT-03：
  - 行、列方向不同缩放的合成图像，`ScaleRow` / `ScaleColumn` 与真值误差在 1% 内。
  - 两个模板各放若干目标，`ModelIndices` 与真值一致；`ModelsData` 保存后重新加载结果一致。
  - 部分超出图像的目标在开启 `BorderShapeModels` 后能找到。
  - 同一图像、同一模板下，`ScaleMode = None` 的结果与模板匹配工具一致（位置误差 0.1 像素内）。
- MT-04：对透视变换后的合成图像，模型角点位置误差在 1 像素内。
- MT-05：合成图像中部件在允许范围内移动后仍能找到全部部件；超出范围的部件按 `IfComponentNotFound` 策略处理。
- MT-06：在良品图上人工加入划痕和污点，`DefectRegion` 能覆盖缺陷且良品图检测结果为无缺陷；内嵌模型和外部模型文件两种方式结果一致。
- MS-01：默认参数下结果与旧版完全一致；`NumMeasures`、`MinScore` 生效；`MeasurePoints` 点数与卡尺数一致；`NumInstances = 2` 时能测出两条平行直线。
- MS-02：对已知宽度的合成条纹，`Widths` 误差在 0.1 像素内；`Edge` 模式结果与旧版一致。
- MS-03：对合成齿轮图像，`Tangential` 找到的边缘数与齿数对应。
- MS-04：在含干扰边缘的图像中，开启模糊测量后只保留符合宽度的边缘对。
- MS-05：对已知灰度渐变的合成图像，`Profile` 与真值一致。
- MS-06：对合成矩形角，交点误差在 0.2 像素内，夹角误差在 0.1° 内。
- MS-07：`Angle` 模式结果与旧版一致；`Length` 模式下固定当量方式结果正确（标定文件方式随 CB-01 标定批验收）。
- 全部回归测试和 `examples\*.vflow.json` 通过。

## 8. 开发顺序

1. MT-01（搜索区域、排序）与 MS-01（metrology 参数）：改动集中在基类，收益覆盖面最大。
2. MS-02（边缘对宽度）、MS-07（单位换算）：现场需求最常见，改动小。
3. MT-03（通用形状匹配）与 MT-02（XLD / DXF 建模、模型原点）：一起做，示教界面一次改完。
4. MS-06（找角）、MS-05（灰度投影）。
5. MT-06（差分检测）。
6. MT-05（组件匹配）、MT-04（透视变形）、MS-03（沿圆弧测量）、MS-04（模糊测量）。

## 9. 第一批算子探测结论（MS-01）

**探测环境**：本机安装的是 **HALCON 22.11 Steady**（`get_system('version')` = 22.11），不是本文的版本基线 20.11，以下结论都在 22.11 上取得；部署到 20.11 前需复核。方法：`get_param_info` 读取 metrology 算子的参数表；再对新建的 metrology 模型（只加一条直线、不设任何通用参数）用 `get_metrology_object_param` 读出运行时实际默认值；最后在合成图上实际测量，对比“旧版参数”和“显式传入全部默认值”的结果。

| 参数 | 实测默认 | 说明 |
|---|---|---|
| `num_measures` | 由长度推算（400 像素直线读出 41） | 未设置时不是 0，而是按 `measure_distance` 布置后得到的卡尺数。本工具用 `NumMeasures = 0` 表示“不设置，按 `measure_distance` 布置” |
| `measure_distance` | 10 | `get_metrology_object_param` 不允许读取（#1303），由 400 像素直线布置 41 个卡尺（400 / 10 + 1）间接证实 |
| `min_score` | 0.7 | |
| `num_instances` | 1 | |
| `distance_threshold` | 3.5 | |
| `measure_interpolation` | `nearest_neighbor` | `nearest_neighbor` / `bilinear` / `bicubic` 三个取值实测均可用 |
| （参考）`rand_seed` | 42 | 默认固定随机种子，同一图像结果可复现 |

- `get_param_info` 给出的通用参数名清单（`set_metrology_object_param` 的 `GenParamName`）含上述全部 6 项；它不提供每个通用参数各自的默认值，所以默认值以运行时读出为准。
- **兼容性实测**：同一图像上，旧版调用（只传 `measure_transition` / `measure_select`）与显式传入 `measure_distance=10, min_score=0.7, num_instances=1, distance_threshold=3.5, measure_interpolation=nearest_neighbor` 的结果完全相同（拟合参数、得分、41 个边缘点、41 个卡尺）。
- **二选一**：同时传 `num_measures` 与 `measure_distance` 时 `add_metrology_object_line_measure` 报 #1211，因此只能传其中一个。
- **多实例**：两条平行边、卡尺覆盖两者、`num_instances = 2` 时得到两个实例，参数按实例首尾相接返回（`get_metrology_object_result` 的实例参数传 `all`），`score` 每个实例一个值；`get_metrology_object_measures` 返回全部边缘点。

## 10. 第一批（MT-01 + MS-01）评审处理与验收记录

**评审意见处理**（[MATCH-MEASURE-TOOLS-PLAN-REVIEW.md](MATCH-MEASURE-TOOLS-PLAN-REVIEW.md)）

| 条目 | 处理 |
|---|---|
| P1-1 排序击穿 best 取值 | 排序前按 `Score` 降序显式选出最佳结果并传入 `SetMatchOutputs`；用例固定单值不随 5 种排序变化、数组按新顺序输出且 `Index` 重写 |
| P1-2 枚举序列化表述 | 第 6 节已改为“按数字保存、新值追加在末尾” |
| P1-3 缩小图生命周期 | 只缩小一次，`finally` 释放；`Image` 输出仍为原图 |
| P1-4 编辑器纳入同步清单 | 第 6 节补充；匹配窗口加搜索区域/排序，跟随与椭圆测量窗口加“高级参数”折叠区 |
| P2-1 复用种子机制 | `InstanceSeedIndices` 由 `SeedIndex` 生成，多矩阵 × 多实例按“定位结果 → 实例”展开 |
| P2-2 二选一传参 | `NumMeasures > 0` 只传 `num_measures`，否则只传 `measure_distance` |
| P2-3 默认值实测 | 见第 9 节（HALCON 22.11） |
| P2-4 `MeasurePoints` | 直接用 `XldLineHelper.Crosses` |
| P2-5 回归门禁 | 默认参数下形状匹配、灰度匹配和 4 个 metrology 测量工具与旧版直接调用逐项一致；5 种排序 × 行容差 {0, 16} |
| P2-6 语义提示 | 匹配窗口搜索区域下方提示“限制的是模型参考点（示教区域的重心）” |

**验收结果**

- 单元/集成测试：新增 `MatchMeasureBatch1Tests` 33 个用例，全量 `dotnet test`（`Category!=Soak`）797 个通过；`examples\*.vflow.json` 全部可加载。
- 界面验收：脚本经 Windows UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`，在文件对话框打开含模板匹配和跟随测量的流程，完成编辑、运行、保存、重新加载，共 43 项检查全部通过。覆盖：默认参数与旧版直接调用逐字一致（兼容门禁）；5 种排序（含行容差 0 / 16）顺序正确且最佳得分始终为最高分；行容差仅在先行后列时显示、为负时校验报错；搜索区域下拉列出上游区域、配置后只找到区域内目标；高级参数默认收起、展开后显示 6 项且卡尺数大于 0 时隐藏卡尺间距、实例数为 0 校验报错；椭圆测量窗口同样有折叠区；`MeasurePoints` 和匹配轮廓叠加显示；界面数值（12 / 33 项）与不走界面的对照运行逐字一致；保存文件与期望配置一致，重新加载后配置与结果不变。
- 验证中未发现应用本身的问题。第二批代码评审指出“执行测试”会先把界面参数写入窗口持有的工具实例（主程序中该实例是编辑事务的工作副本，取消后丢弃，节点未被改写），已在第二批修复，见第 11 节。

## 11. 第二批（回归修复 + MS-02 + MS-07 Fixed）评审处理与验收记录

**评审意见处理**（[MATCH-MEASURE-TOOLS-PLAN-REVIEW.md](MATCH-MEASURE-TOOLS-PLAN-REVIEW.md) 第二批评审意见）

| 条目 | 处理 |
|---|---|
| B2-0 执行测试直写工具 | 采用方案 B：新增 UI 无关的 `ToolTestRun`（`VisionFlow.EditorCore\Ui`），复制配置（`ToolEditTransaction.CopyConfiguration`）→ 把界面参数写入副本 → 在预览上下文运行 → 释放副本缓存的模型句柄；匹配窗口 `ApplyToTool` 拆出 `ApplyTo(target)`，测试作用于副本、确定作用于窗口持有的工具。不选方案 A：主程序已用 `ToolEditTransaction` 为窗口提供工作副本并在确定时写回节点，窗口内再维护一份副本只是重复这层机制。核对时发现评审所说“路由传入活节点”与代码不符（`MainWindow.OpenToolEditor` 传入 `transaction.WorkingCopy`），因此主程序中取消从未改写节点；修复后窗口自身也满足“只在确定时写入”。用例固定：副本上运行用新参数、原工具全部参数不变、参数解析失败时原工具不变、编辑事务中测试后不提交节点不变 |
| B2-1-1 Calibration 来源 | 本批 `ScaleSource` 只有 `Fixed`，不引入无效选项 |
| B2-1-2 Pair 参数取值与编辑器 | 边缘极性按模式收紧校验；编辑窗口边缘极性下拉随模式切换 |
| B2-1-3 Pair 复用边缘输出的语义 | 写入 MS-02 实现说明与 README |
| B2-2-1 共用方法位置 | `measure_pairs` 调用与转换在 `FollowMeasureToolBase`；圆弧卡尺的径向矩形句柄实测可用 |
| B2-2-2 Edge 模式写空并隐藏 | 写空数组 / 0 / NaN；`IToolParameterVisibility` 扩展为也可按输出名隐藏引用候选（用例保证现有工具的输出名都不与参数名重名，不会误伤） |
| B2-2-3 宽度标注 | 尺寸线（连线 + 两端挡线）由 `XldLineHelper.Segments` 生成；仓库没有文字叠加能力，按使用方确认不新增，宽度数值见 `Widths`、日志与结果表 |
| B2-2-4 换算语义 | 按评审实现；面积在 `um` 时乘 1000²（mm² → um²），评审的“× 1000”针对长度 |
| B2-2-5 枚举序列化 | `EdgeMode` / `ConvertKind` / `ScaleSource` / `LengthUnit` 按数字保存，默认值为原行为 |
| B2-2-6 编辑器同步 | 只改 `WpfFollowMeasureToolEditWindow`；单位换算只改 `ToolboxRegistry` 显示名与 README |
| B2-3 文档 | 头部状态、MS-02 / MS-07 实现说明、第 10 节表述、README 均已更新 |

**验收结果**

- 单元/集成测试：新增 `MatchMeasureBatch2Tests` 24 个用例（回归修复 3、MS-02 15、MS-07 4、输出隐藏防误伤 1、保存加载 1），全量 `dotnet test`（`Category!=Soak`）821 个通过；`examples\*.vflow.json` 全部可加载。
- 界面验收：脚本经 Windows UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`，在文件对话框打开测试流程，完成编辑、运行、保存、重新加载，共 42 项检查全部通过。覆盖：匹配窗口改数量与排序 → 执行测试（用新参数得 2 个结果）→ 取消 → 流程未标记修改、重新打开参数仍为原值，确定后才写入；卡尺窗口边缘模式下拉、边缘极性在两种模式间联动（`*_strongest` 切回单边缘时退回 `all`）、边缘对执行测试结果表宽度 20 / 20 / 40 与对照一致；单边缘选 `*_strongest` 校验报错；圆弧卡尺窗口同样可切边缘对；工具箱显示“单位换算”，侧栏 Angle / Length 参数显隐正确，`Length` 输入值下拉只列出边缘对模式的 `Widths`；边缘对显示轮廓比同位置单边缘多出尺寸线（像素对比与截图）；卡尺单边缘与旧版 `measure_pos`、边缘对与直接 `measure_pairs`、角度换算与旧版公式逐字一致；界面 29 个单值与 12 个数组长度与不走界面的对照运行逐字一致；保存文件与期望配置一致（枚举按数字保存，ID 仍为 `angle-convert`），重新加载后配置与结果不变。
- 验证中发现的应用问题：侧栏数字参数按当前值是否为整数决定小数位，`PixelSize`（默认 1）输入 0.02 后文本框立即显示为 `0`（实际值 0.02 已写入，重新选中节点后显示正确）。这是共享控件 `NumericInputControl` 的既有行为，经使用方确认做最小修复：值的精度超过 `DecimalPlaces` 时补足有效小数（最多 6 位），精度不超过的值显示与原来完全相同。

## 12. 第三批算子探测结论（MT-02 / MT-03）

**探测环境**：本机原生运行库 **HALCON 22.11 Steady**（`get_system('version')` = 22.11）。**关键发现**：仓库原先编译引用的 `lib\halcon\halcondotnet.dll` 是 **20.11.1**，其中没有 `CreateGenericShapeModel` / `FindGenericShapeModel` 等方法——通用形状模型不是 20.11 的算子，计划原文“20.11 引入”有误。经使用方确认，把编译引用换成 22.11.1（`C:\Program Files\MVTec\HALCON-22.11-Steady\bin\dotnet35\halcondotnet.dll`，库文件不入库，需各开发机自行替换），应用最低版本随之提高到 22.11；替换后全量回归通过。以下结论均在 22.11 上取得。

| 问题 | 结论 |
|---|---|
| 通用形状模型能否设置原点（评审 B3-1-2） | 能。`set_generic_shape_model_param` 的参数表含 `origin_row` / `origin_column`（读写均可，序列化后保留）；`set_shape_model_origin` 对通用形状模型句柄也可用。设置后 `find_generic_shape_model` 的 row / column / `hom_mat_2d` 平移到原点，结果轮廓仍在目标上。本批用 `origin_row` / `origin_column`，原点对通用形状匹配不降级 |
| `serialize_shape_model` 能否序列化通用形状模型（B3-1-4） | 能。训练后的通用句柄可 `serialize_shape_model` / `deserialize_shape_model`（也可 `write_shape_model` / `read_shape_model`），往返后参数、原点、杂乱区域与 `use_clutter` 都保留；`clear_shape_model` 与 `clear_handle` 都能释放。未训练的句柄序列化后无法查找（#8673），打包前必须已训练 |
| `find_generic_shape_model` 的 `timeout` 单位 | **毫秒**：`timeout = 50` 约 51 ms 后报 #9400，`1000` 约 1029 ms 后报 #9400；默认值 `"false"`（不限制） |
| `create_shape_model_xld` 多轮廓（B3-1-6） | 不报错、也不只取第一条：多条轮廓全部进入模型（`get_shape_model_contours` 条数 = 输入条数）；`create_scaled_shape_model_xld`、`train_generic_shape_model` 相同。XLD 建模的度量只能是 `ignore_local_polarity`（`use_polarity` 报 #1306）；`train_generic_shape_model` 传 XLD 时自动用 `ignore_local_polarity`。XLD 模型参考点为轮廓外接矩形中心 |
| 训练后修改哪些参数需要重新训练 | 需要重新训练（查找报 #8673）：`iso_scale_*`、`scale_row_*` / `scale_column_*`、`num_levels`、`metric`、`contrast_low`、`optimization`、`min_size`。不需要：`angle_start` / `angle_end`、`min_score`、`num_matches`（含 `"all"`）、`max_overlap`、`greediness`、`subpixel`、`border_shape_models`、`max_deformation`、`timeout`、`use_clutter`、`max_clutter`、`clutter_contrast`、`model_identifier`、`origin_*`、`min_contrast`。据此划分建模参数与查找参数 |
| `model_identifier` | 只接受字符串（整数报 #1203），默认是随机的 `SBM-xxxx`；结果中按 `model_identifier` 区分模板 |
| 结果矩阵与轮廓 | `get_generic_shape_model_result(…, 'hom_mat_2d')` 等于 `hom_mat2d_scale(scale_row, scale_column)` → `rotate(angle)` → `translate(row, column)` 的组合；`get_generic_shape_model_result_object(…, 'contours')` 与 `affine_trans_contour_xld(get_shape_model_contours, hom_mat_2d)` 逐点相同 |
| 杂乱区域 | `set_generic_shape_model_object(区域, 模型, 'clutter_region')`，区域用示教图像坐标，`clutter_hom_mat_2d` 自动取训练位姿（模板参考点）；区域离模型轮廓太近报 #8515。多模板时全部模型的杂乱判定须一致：没有杂乱区域的模型设 `use_clutter = true` 报 #8516，开关不一致查找报 #8517 |
| 越界匹配 | 目标左侧 10 列出图：`border_shape_models = false` 找不到，`true` 找到（得分 0.68） |
| 经典模型原点（MT-02） | `set_shape_model_origin` / `set_ncc_model_origin` 后 `find_*` 输出平移原点偏移量，写读模型后原点保留；`get_shape_model_contours` / `get_ncc_model_region` 相对新原点给出 |
| DXF | `read_contour_xld_dxf` 正常读入多条轮廓；文件不存在 #5200，内容非法 #3278 / #3279（`MatchModelBuilder.ReadDxf` 统一转成“DXF 文件不存在 / DXF 文件解析失败”） |
| XLD 建模的金字塔参数 | `create_shape_model_xld` / `create_scaled_shape_model_xld` 不接受 `NumLevels = 0`（#1301，`create_shape_model` 接受）；示教页默认值 0 按 auto 传入（UI 验收发现，见第 13 节） |
| 模型字节的确定性 | 同参数两次创建同一模型，序列化字节约有 7 个不同（共约 2 万字节），模型字节只能在同一句柄上比较；跨次建模按模型参数与查找结果比较 |
| 通用形状模型的 XLD 参考点 | 与经典 XLD 模型不同，不是外接矩形中心：同一三角形轮廓，经典模型参考点 (370.00, 495.00)，通用形状模型约 (369.92, 495.0)；设置原点 (-30, 0) 后输出在锯齿三角形上平移 -30.056（L 形等直边目标平移误差 < 0.005），属亚像素拟合差异 |

部署到其他机器前需确认已安装 22.11（或更高）运行库与授权；若后续仍需支持 20.11 运行库，通用形状匹配不可用，需要另行评审降级方案。

## 13. 第三批（MT-02 + MT-03）评审处理与验收记录

**评审意见处理**（[MATCH-MEASURE-TOOLS-PLAN-REVIEW.md](MATCH-MEASURE-TOOLS-PLAN-REVIEW.md) 第三批评审意见）

| 条目 | 处理 |
|---|---|
| B3-1-1 `ModelNames` 命名冲突 | 参数改名 `TemplateNamesCsv`，输出保留 `ModelNames`；第二批守卫测试（输出名不与参数名相同）保持通过，另加专门用例 |
| B3-1-2 通用形状模型原点 | 实测支持 `origin_row` / `origin_column`，原点功能对通用形状匹配完整提供，不降级 |
| B3-1-3 按模板的查找参数 CSV | 砍掉，查找参数全局生效，MT-03 条目已同步修改 |
| B3-1-4 `ModelsData` 格式 | 按评审格式实现（版本 1），`serialize_shape_model` 实测可用；版本不符、截断、多余字节明确报错，流程校验与运行都拒绝 |
| B3-1-5 缓存键 | 沿用数组引用，示教/导入整体替换数组；用例固定同一数组复用句柄、替换数组重新加载、释放后重新加载 |
| B3-1-6 XLD / DXF 输入约束 | 实测多轮廓共同组成模板（不报错），示教页提供轮廓序号；DXF 不存在 / 解析失败示教时明确报错，运行不依赖 DXF（用例删除 DXF 文件后照常运行） |
| B3-2-1 度量锁定与校验 | XLD / DXF 来源时示教页度量锁定为 `ignore_local_polarity`、对比度不可用并说明原因；“XLD 来源 + 非 ignore_local_polarity”流程校验报错且运行拒绝 |
| B3-2-2 原点与基准 | 修改原点后显示“请重新确定跟随基准”，`Base*` 不回填（UI 验收核对） |
| B3-2-3 结果矩阵 | 跟随矩阵与轮廓用完整 `hom_mat_2d`；各向异性缩放下与直接算子调用逐项一致（单元测试 + UI 验收） |
| B3-2-4 句柄生命周期 | 结果句柄 `finally` 释放（含异常路径），模板句柄逐个释放，编辑窗口测试走一次性副本并 `FlowResources.Release` |
| B3-2-5 示教界面布局 | 模型来源、原点作为模板匹配示教页的独立卡片；通用形状匹配用专用窗口（理由见 MT-03 实现说明），模板列表与杂乱区域为示教页独立分组；ROI 控件仍由 `WindowsFormsHost` 承载 |
| B3-2-6 新工具登记 | `BuiltinToolIdentities`、`ToolboxRegistry`、`WpfToolEditorRouter`（先于单模板匹配窗口路由并注释）、README、计划头部状态 |
| B3-2-7 枚举与单位 | `ScaleMode`、`MatchModelSource` 按数字保存、默认值为原行为；`TimeoutMs` 为毫秒，写入算子处注释实测结论 |
| B3-2-8 回归门禁 | 模板匹配图像 ROI 模型运行结果与直接调用逐项一致；原点为 0 时模型字节不变（形状、灰度）；新属性默认值与历史文件加载；搜索区域与排序在通用形状匹配上生效 |
| B3-3 文档 | 头部状态与 HALCON 基线、MT-02 / MT-03 实现说明、第 12 节探测结论、README；`.github/copilot-instructions.md` 的 HALCON 先决条件同步为 22.11 |

**验收结果**

- 单元/集成测试：新增 `MatchMeasureBatch3Tests` 20 个用例（持久化往返与格式错误、模板名校验、缓存键、各向异性矩阵与轮廓、多模板、搜索区域与排序、杂乱区域含多模板一致性、越界与超时、通用形状原点、经典模型原点与零偏移字节不变、XLD 多轮廓与序号及金字塔 0、DXF 读取与错误、XLD 来源校验、回归门禁 ×2、命名守卫、参数显隐、新工具登记、未建模提示），全量 `dotnet test`（`Category!=Soak`）841 个通过。
- 界面验收：脚本经 Windows UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`（文件对话框打开流程、工具箱新建节点、在示教图像上用真实鼠标点击画 ROI 与点选原点），59 项检查全部通过：
  - 模板匹配示教页：模型来源三选一；XLD / DXF 时度量锁定为 `ignore_local_polarity`、对比度不可用，切回图像 ROI 恢复原度量；DXF 文件不存在明确报错；DXF 建模与从 `轮廓T.Xld` 建模（示教结果在三角形外接矩形中心）；在图像上点选原点（回显偏移误差 < 2.5 像素）、应用原点后示教结果移到原点、显示“重新确定跟随基准”提示且基准不变；重新打开回显来源、原点、锁定状态；保存的模型原点为 (-30, 0)，查找结果与无界面同样建模的模型逐项相同。
  - 通用形状匹配：工具箱新建；侧栏只显示查找参数（建模参数、`TemplateNamesCsv`、未启用时的杂乱参数隐藏）；模板列表添加（空名称自动命名）、改名、上移、下移、删除，重名与含逗号被拒，顺序调整提示；缩放方式切换显示对应分组；图像 ROI 训练各向异性 L 形（拉伸副本列缩放 1.3013）、从 XLD 训练三角形（度量锁定）、三角原点 (-30, 0)；两个模板分别画杂乱区域；运行参数页：不启用杂乱 4 个结果（每模板 3 / 1），只有一个模板有杂乱区域时开启杂乱判定明确报错，两个都有后启用杂乱 0.01 剔除杂乱区有亮线的 L3（每模板 2 / 1）。
  - 对照：界面运行结果与无界面运行保存文件的 23 个单值、13 个数组长度逐字一致；编辑窗口测试结果表（序号 / 模板 / Row / Column / Angle / ScaleRow / ScaleColumn / Score）与无界面结果逐字一致；`HomMats` 与直接调用 `find_generic_shape_model` 的 `hom_mat_2d · 基准⁻¹` 逐项一致；模板默认节点与旧版直接调用逐字一致（回归门禁）；两种匹配的显示轮廓叠加在图像上；文件中 ID、ScaleMode / ModelSource 按数字、示教记录与模板名正确；重新加载后再运行逐字一致，模板列表、原点、建模参数回显正确。
- 验证中发现并修复的应用问题：
  1. XLD 建模报 #1301：示教页金字塔默认 0 不被 `create_*_shape_model_xld` 接受（单元测试当时传的是 "auto" 没覆盖到）。`MatchModelBuilder` 把 ≤ 0 的层数按 auto 处理，并补用例。
  2. 模板匹配示教页只显示 `Input.Image` 或图像文件，图像来自上游节点（如 `图像1.Image`）时示教页没有图像，XLD 建模测试与原点点选无法进行（既有限制，本批功能依赖它）。改为从上次运行结果解析上游图像引用。
  3. 通用形状匹配窗口应用原点后，列表刷新触发的选中事件立刻把“重新确定跟随基准”提示隐藏。改为只在选中的模板真正变化时清除示教结果与提示。
- 说明：流程文件中的中文（含 `TemplateNamesCsv`）按现有序列化设置写成 `\uXXXX` 转义，名称为中文时在文件里不能直接阅读，这是既有行为，本批未改。验收期间 Lenovo 屏保（不响应模拟输入）遮住屏幕导致一次运行无效，由使用方解除后重跑。
