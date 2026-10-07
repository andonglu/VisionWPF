# 定位匹配与几何测量补充开发计划

编写日期：2026-10-06
状态：第一批 MT-01 / MS-01 已实现（分支 `feature/match-measure-batch1`，见第 10 节）；其余各项待开发，动工前各做一次小评审。
范围：工具箱“01 定位匹配”和“05 几何测量”中的模板匹配类、测量类工具（`HalconTools.cs`、`DescriptorMatchTools.cs`、`MeasureTools.cs`、`FollowMeasureTools.cs`、`AngleTools.cs`），以及新增的差分检测。
HALCON 版本基线：**20.11 及以上**。可以直接使用通用形状模型（`*_generic_shape_model`）等 20.11 引入的算子，无需兼容更早版本。
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
- 匹配编辑窗口“运行参数”页的“执行测试”改为在工作副本上调用工具自身的 `Run`，因此搜索区域、排序与流程运行结果完全一致；“示教建模”页的测试行为不变。
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

### MT-03 通用形状匹配（新工具）

**定位**

- 工具箱：`01 定位匹配 / 通用形状匹配`，ID `generic-shape-match`，类 `HalconGenericShapeMatchTool : HalconMatchToolBase`。
- 用 HALCON 20.11 的通用形状模型实现，一个工具覆盖：旋转、等比缩放、行列方向不同比例缩放、多模板同时匹配、杂乱判定、越界匹配、最大变形量、超时。
- 原计划的“缩放形状匹配增加各向异性缩放”和“多模板匹配”新工具不再单独开发，统一由本工具提供。

**算子流程**

- 示教：`create_generic_shape_model` → `set_generic_shape_model_param`（建模参数）→ `train_generic_shape_model`（模板图像或 XLD）。
- 查找：`set_generic_shape_model_param`（查找参数）→ `find_generic_shape_model` → `get_generic_shape_model_result` / `get_generic_shape_model_result_object` → `clear_handle`（释放结果句柄）。
- 多模板：每个模板设置不同的 `model_identifier`，把全部模型句柄一次传给 `find_generic_shape_model`，用结果中的模型标识区分来源。

**参数**

| 分组 | 属性 | 通用形状模型参数 | 说明 |
|---|---|---|---|
| 角度 | `AngleStart` / `AngleEnd` | `angle_start` / `angle_end` | 界面按度显示 |
| 缩放 | `ScaleMode` 枚举：`None`（默认）/ `Isotropic` / `Anisotropic` | — | 决定使用哪组缩放参数 |
| 缩放 | `IsoScaleMin` / `IsoScaleMax` | `iso_scale_min` / `iso_scale_max` | 等比缩放 |
| 缩放 | `ScaleRowMin` / `ScaleRowMax` / `ScaleColumnMin` / `ScaleColumnMax` | `scale_row_*` / `scale_column_*` | 行列方向不同比例缩放 |
| 查找 | `MinScore`、`NumMatches`、`MaxOverlap`、`Greediness`、`SubPixel`、`NumLevels` | `min_score`、`num_matches`、`max_overlap`、`greediness`、`subpixel`、`num_levels` | `MinScore` / `NumMatches` 继承自基类 |
| 查找 | `BorderShapeModels` | `border_shape_models` | 允许模板部分超出图像 |
| 查找 | `MaxDeformation` | `max_deformation` | 允许的轮廓偏移像素 |
| 查找 | `TimeoutMs` | `timeout` | 0 表示不限制 |
| 杂乱 | `UseClutter`、`MaxClutter`、`ClutterContrast` | `use_clutter`、`max_clutter`、`clutter_contrast` | 杂乱区域在示教时绘制，用 `set_generic_shape_model_object` 写入模型 |
| 建模 | `Metric`、`Optimization`、`ContrastLow`、`ContrastHigh`、`MinContrast`、`MinSize` | `metric`、`optimization`、`contrast_low`、`contrast_high`、`min_contrast`、`min_size` | 修改后必须重新训练 |

- 多模板时 `MinScore` 等查找参数可以按模板分别设置（CSV 字符串，留空表示全部使用同一值）。

**输出**

- 继承匹配基类的全部输出。
- 新增 `ScaleRows` / `ScaleColumns`（数组）。`MatchResultItem` 已有 `ScaleRow` / `ScaleColumn` 字段，直接填入；`Scale` 取两者平均值。
- 多模板相关：`ModelIndices`（数组，每个结果所属模板序号，从 0 开始）、`ModelNames`（数组）、`BestModelIndex`、`BestModelName`、`CountsPerModel`（数组）。

**开发方法**

- 持久化：全部模板打包成一个 `byte[] ModelsData`。格式：版本号、模板数量，然后逐个写入名称长度、名称（UTF-8）、模型长度、模型字节（`serialize_shape_model`，通用形状模型同样适用，开发时实测确认）。模板名另存一份 `ModelNames` CSV 字符串，便于在流程文件中查看。
- `CurrentModelKey` 使用 `ModelsData` 的内容哈希；`LoadModel` 反序列化全部模型；`ClearModel` 逐个释放。
- 查找参数在每次 `FindMatches` 时写入缓存的模型句柄（开销小），建模参数只在示教时设置。基类已对同一实例的运行加锁，写参数不存在并发问题。
- 跟随矩阵和模型轮廓变换使用 `get_generic_shape_model_result` 返回的完整变换矩阵，不能只用 `Scale` 重建，否则各向异性缩放时结果错误。
- 编辑窗口：复用 `WpfMatchToolEditWindow` 的示教区，增加模板列表（添加、删除、改名、上移、下移）；每个模板单独示教，可单独设置原点（MT-02）和杂乱区域。模板顺序调整后提示下游引用的模板序号可能需要更新。

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
- MS-07：`Angle` 模式结果与旧版一致；`Length` 模式下固定当量和标定文件两种方式结果正确。
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
- 验证中未发现应用本身的问题。
