# 标定补充开发计划

编写日期：2026-10-06
状态：待开发
范围：图像坐标与物理坐标之间的标定、换算和纠偏，涉及 `VisionFlow.Tools\Tools\GeometryTools.cs`（`AffinePointTool`）、编辑器标定界面，以及供上层调用的标定接口。
HALCON 版本基线：20.11 及以上。
关联文档：

- [定位匹配与几何测量补充开发计划](MATCH-MEASURE-TOOLS-PLAN.md)：MS-07 单位换算读取本文的标定结果。
- [图像处理补充开发计划](IMAGE-TOOLS-PLAN.md)：IP-04 几何变换输出的矩阵可与本文的坐标转换串联。

## 1. 现状与边界

### 1.1 现状

| 能力 | 现有实现 | 限制 |
|---|---|---|
| 图像坐标转世界坐标 | `AffinePointTool`（`affine-point`） | 只能读取一个用 `write_tuple` 保存的仿射矩阵文件（按“路径 + 修改时间”缓存），或引用上游矩阵；只处理单个点；不输出角度；编辑器内无法生成标定文件 |

### 1.2 职责边界

- VisionFlow 负责：标定计算（点对拟合、旋转中心拟合、相机标定）、标定结果的保存与读取、运行时的坐标与长度换算、纠偏量计算。
- 上层项目负责：机器人或运动平台的移动、物理坐标的采集与通信、何时触发标定、标定文件的部署与版本管理。
- 机器人手眼标定中需要的“机器人坐标”由上层通过外部输入（`ExternalInputRegistry`）或标定接口传入，VisionFlow 不直接与机器人通信。
- 三维手眼标定（`calibrate_hand_eye`）、多相机拼接标定不在本计划范围。

## 2. 拆分结论

| 编号 | 内容 | 处理方式 |
|---|---|---|
| CB-01 | 标定结果文件格式、标定计算服务（UI 无关，上层也可直接调用） | 基础设施 |
| CB-02 | N 点标定（≥3 个点对求仿射或相似变换） | 编辑器标定助手 + CB-01 服务 |
| CB-03 | 旋转中心标定 | 编辑器标定助手 + CB-01 服务 |
| CB-04 | 相机标定（标定板，求内参、畸变和测量平面位姿） | 编辑器标定助手 + CB-01 服务 |
| CB-05 | 坐标转换：支持数组、角度、相机标定结果 | 增强“图像坐标转世界坐标” |
| CB-06 | 畸变校正（输出校正后、比例均匀的图像） | **新建工具** |
| CB-07 | 纠偏计算（当前位姿相对基准的物理偏移量） | **新建工具** |

结果：新建 2 个工具，增强 1 个现有工具，另有 1 项基础设施和 3 个编辑器标定助手，工具箱净增 2 个入口，放在新分类“09 标定”（“图像坐标转世界坐标”同时移入该分类）。

拆分说明：标定过程（采集点对、拍标定板）只在调试时做一次，运行时只需要换算。因此标定过程做成编辑器中的“标定助手”和可供上层调用的服务，不做成每次运行都执行的流程工具；运行时的换算工具只读取标定结果。

## 3. 开发通用做法

与前几份计划一致：新增枚举值追加在末尾、默认值等于现有行为；新输出补 `[ToolOutput]`；新工具登记 `BuiltinToolIdentities`、注册 `ToolboxRegistry`、路由编辑窗口；测试与 README 同步更新。

标定结果的读取统一走 CB-01 的加载与缓存逻辑，并实现 `IToolResourceLifecycle`，在预热时检查文件是否存在、格式是否正确，避免投产后才发现标定文件缺失。

## 4. 基础设施

### CB-01 标定结果格式与标定服务

**文件格式**

- 新格式为 JSON，扩展名 `.vfcal.json`，内容可读、可比对：
  - 公共字段：`FormatVersion`、`Kind`（`Affine2D` / `RotationCenter` / `Camera`）、`CreatedAt`、`Description`、`Unit`（如 `mm`）。
  - `Affine2D`：`HomMat2D`（6 个数）、`Points`（点对列表：图像行、列，物理 X、Y）、`RmsError`、`MaxError`、`TransformType`（`affine` / `similarity` / `rigid`）。
  - `RotationCenter`：图像坐标中的旋转中心 `Row` / `Column`、物理坐标中的 `X` / `Y`（有仿射标定时一并换算）、`Radius`、`Points`、`RmsError`。
  - `Camera`：`CamParam`（HALCON 相机参数元组，按名称和值保存）、`Pose`（测量平面位姿，7 个数）、`PlaneThickness`、`RmsError`、使用的标定板描述文件名、图像数量。
- 一个文件可同时包含 `Affine2D` 与 `RotationCenter`（同一工位常一起使用）。
- 兼容：`AffinePointTool` 继续支持旧的 `write_tuple` 矩阵文件，按文件内容自动识别格式。

**标定服务**

- 新增 `VisionFlow.Tools\Calibration\CalibrationService`（静态方法，无 UI 依赖）：
  - `SolveAffine(points, transformType)` → 矩阵与误差（`vector_to_hom_mat2d` / `vector_to_similarity` / `vector_to_rigid`，误差由残差计算）。
  - `SolveRotationCenter(imagePoints)` → 圆心、半径与误差（点拟合圆，≥3 个点）。
  - `CalibrateCamera(images, plateDescription, cameraModel, initialParams)` → 相机参数、位姿与误差（CB-04）。
  - `Load(path)` / `Save(path, result)`：读写 `.vfcal.json`。
- 上层项目在自动标定（机器人走 9 点、旋转 N 次）时，运行流程取得图像坐标，自行采集物理坐标，然后直接调用这些方法生成标定文件，不需要经过编辑器。

**引用方式**

- 运行工具通过 `CalibrationFile`（路径）引用标定结果，与现有 `AffinePointTool.CalibrationFile` 一致，多个流程可共用同一工位的标定文件。
- 另支持把标定结果内嵌在工具中（字符串属性 `CalibrationData`，保存 JSON 内容），用于单流程自包含的场景；`CalibrationSource` 枚举选择 `File`（默认）/ `Embedded`。

## 5. 编辑器标定助手

三个助手放在“图像坐标转世界坐标”工具的编辑窗口中（以选项卡区分），也可从主窗口“工具”菜单单独打开。生成结果后可“保存为标定文件”或“内嵌到当前工具”。

### CB-02 N 点标定

- 点对表格：序号、图像行、图像列、物理 X、物理 Y、残差。
- 图像坐标来源：
  - 从上次运行的变量中选择（如 `匹配1.Row` / `匹配1.Column`），点击“取当前值”追加一行；
  - 在图像上点击；
  - 手动输入。
- 物理坐标：手动输入，或从 CSV 导入（与上层导出的机器人点位对接）。
- 变换类型：`affine`（默认，允许不同方向比例不同）/ `similarity`（等比例，无剪切）/ `rigid`（只有旋转平移，用于已知比例为 1 的场景）。
- 计算后显示每个点的残差、均方根误差与最大误差，残差超过阈值的点标红，可删除后重算。
- 至少 3 个点；少于 3 个或点共线时明确提示。

### CB-03 旋转中心标定

- 用途：吸嘴或旋转台带动工件旋转，相机拍到的同一特征点位置变化，拟合出旋转中心，用于 CB-07 纠偏计算。
- 采集：表格记录每次旋转后特征点的图像坐标（来源与 CB-02 相同），可选记录对应的旋转角度。
- 至少 3 个点，建议覆盖 30° 以上的角度范围；角度范围过小时提示误差会偏大。
- 若当前已有 N 点标定结果，同时输出旋转中心的物理坐标。

### CB-04 相机标定（标定板）

- 输入：多张标定板图像（从文件夹加入，或把当前输入图像加入），标定板描述文件（HALCON 标准标定板，如 `calplate_160mm.cpd`），相机模型（`area_scan_division` / `area_scan_polynomial`），初始参数（焦距、像元宽高、图像宽高）。
- 算子：`create_calib_data` → `set_calib_data_cam_param` / `set_calib_data_calib_object` → 逐张 `find_calib_object` → `calibrate_cameras` → `get_calib_data`（相机参数、位姿）。
- 测量平面：选择一张标定板放在测量平面上的图像作为参考位姿，填写标定板厚度，用 `set_origin_pose` 把位姿修正到测量平面。
- 显示：每张图像的标定板识别结果、重投影误差；识别失败的图像标出并允许移除。
- 建议 10~20 张、覆盖视野不同位置和倾斜角度，界面给出提示。

## 6. 运行工具

### CB-05 图像坐标转世界坐标增强

- ID `affine-point` 与类型名 `AffinePointTool` 不变，移入“09 标定”分类。
- 输入：
  - `Row` / `Column` 改为支持数组（`AcceptsCollection = true`），单值时行为不变。
  - 新增可选输入“角度”（弧度，单值或数组），输出对应的物理角度。
- 新增 `CalibrationKind`：`Affine2D`（默认，现有逻辑：矩阵引用或矩阵文件）/ `Camera`（`image_points_to_world_plane`，使用相机参数与测量平面位姿，单位由 `Unit` 指定）。
- 角度换算：
  - `Affine2D`：取一个单位方向向量经矩阵变换后的方向角，正确处理镜像（矩阵行列式为负）的情况。
  - `Camera`：用点和沿角度方向的第二点分别换算后求方向角。
- 输出：现有 `WorldRow` / `WorldColumn` 不变；新增 `WorldRows` / `WorldColumns`（数组）、`WorldAngle` / `WorldAngles`（弧度）、`WorldAngleDeg`、`Count`、`Matrix`（`Affine2D` 时输出所用矩阵，供 MS-07 单位换算等下游引用）。
- `OriginRow` / `OriginColumn` 的偏移含义不变。

### CB-06 畸变校正（新工具）

- 工具箱：`09 标定 / 畸变校正`，ID `image-rectify`，类 `ImageRectifyTool : ToolBase, IToolResourceLifecycle`。
- 用途：把有镜头畸变和透视的图像校正为测量平面上比例均匀的图像，之后的像素距离可直接乘固定比例得到物理长度。
- 输入：图像（必填）。
- 参数：`CalibrationFile` / `CalibrationData`（`Camera` 类型）、`PixelSize`（校正后每像素对应的物理长度，0 表示按原图中心比例自动计算）、输出区域（以测量平面坐标给出的左上角和宽高，默认覆盖整个视野）、`Interpolation`。
- 算子：`gen_image_to_world_plane_map`（只在参数或标定变化时生成，结果缓存，参与预热）+ `map_image`。
- 输出：`Image`；`PixelSize`（实际比例）；`Matrix`（校正图像素坐标 → 测量平面坐标的仿射矩阵，供“图像坐标转世界坐标”和单位换算使用）。

### CB-07 纠偏计算（新工具）

- 工具箱：`09 标定 / 纠偏计算`，ID `alignment-offset`，类 `AlignmentOffsetTool`。
- 用途：根据当前定位结果与示教时的基准位姿，计算机器人或平台需要补偿的物理偏移量 `dX` / `dY` / `dθ`。对标 VisionMaster 的对位、纠偏功能。
- 输入：当前位姿（行、列、角度引用，通常来自匹配的 `Row` / `Column` / `Angle`）。
- 参数：
  - `CalibrationFile` / `CalibrationData`（需包含 `Affine2D`，使用旋转补偿时还需 `RotationCenter`）。
  - 基准位姿 `BaseRow` / `BaseColumn` / `BaseAngle`（编辑窗口中“用当前结果设为基准”一键写入）。
  - `Mode`：
    - `TranslationOnly`：只补偿平移（物理坐标差值）。
    - `RotateAroundCenter`（默认）：先绕旋转中心转回 `dθ`，再计算剩余平移，适用于吸嘴或旋转台。
  - `CameraMounting`：`Fixed`（相机固定，看工件）/ `OnAxis`（相机随轴运动）；决定偏移量的符号约定。
  - `AngleUnit`：输出角度用度或弧度。
- 输出：`DeltaX`、`DeltaY`、`DeltaAngle`、`WorldX`、`WorldY`（当前位置的物理坐标）、`Valid`（输入不是 NaN 时为 true）。
- 输入为 NaN（如匹配未找到）时输出 NaN 与 `Valid = false`，不按失败处理，由后续判定决定 NG。

## 7. 暂缓

- 三维手眼标定、多相机拼接标定、线扫相机标定。
- 自动标定流程编排（机器人按 9 点走位、自动采集）：属于上层项目，VisionFlow 提供 CB-01 服务接口。
- 标定结果的有效期管理、版本审批：属于上层项目的文件与配方管理。

## 8. 兼容性要求

- `AffinePointTool` 的现有输入、输出、`CalibrationFile`（旧矩阵文件）、`MatrixPath`、`OriginRow` / `OriginColumn` 行为不变；新输入均为可选。
- 新增枚举值追加在末尾；`CalibrationKind = Affine2D`、`CalibrationSource = File` 为默认值。
- 工具分类调整只影响工具箱显示，不影响工具 ID 与流程文件。

## 9. 验收

- CB-01：`.vfcal.json` 保存后重新读取，所有数值一致；旧 `write_tuple` 矩阵文件仍可被读取。
- CB-02：用已知仿射变换生成的点对（加 0.1 像素噪声）求解，矩阵误差在噪声量级内；共线点、少于 3 个点给出明确提示。
- CB-03：对已知圆心旋转生成的点（加噪声），圆心误差小于 0.2 像素。
- CB-04：使用 HALCON 安装目录自带的标定板示例图像完成标定，重投影误差在 HALCON 示例给出的量级内；缺少示例图像时用例明确失败，不计为通过（与现有 HALCON 用例约定一致）。
- CB-05：数组输入与逐个单值输入结果一致；含镜像的矩阵下角度换算正确；`Camera` 方式与直接调用 `image_points_to_world_plane` 一致。
- CB-06：校正后的标定板图像中，相邻标记点间距与真实间距误差小于 0.5%。
- CB-07：构造已知平移与旋转的前后位姿，输出的 `DeltaX` / `DeltaY` / `DeltaAngle` 与真值一致；`TranslationOnly` 与 `RotateAroundCenter` 两种方式各一例；输入 NaN 时 `Valid = false`。
- 全部回归测试和 `examples\*.vflow.json` 通过。

## 10. 开发顺序

1. CB-01（文件格式与服务）、CB-02（N 点标定助手）、CB-05（坐标转换增强）：最常用的 9 点标定闭环。
2. CB-03（旋转中心）、CB-07（纠偏计算）：机器人对位闭环。
3. CB-04（相机标定）、CB-06（畸变校正）。
