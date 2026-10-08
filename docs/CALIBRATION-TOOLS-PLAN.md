# 标定补充开发计划

编写日期：2026-10-06
状态：第一批 CB-01 / CB-02 / CB-05 已实现（分支 `feature/calibration-batch1`，见第 11、12 节）；评审 P1-1 / P1-2 / P1-3 已在计划中处理（CB-06 输出改名、CB-07 符号约定、CB-04 示例图像探测，见第 13 节）；第二批 CB-03 / CB-07 已实现（分支 `feature/calibration-batch2`，见第 14 节）；CB-04 / CB-06（第三批）待开发，动工前做一次小评审。
范围：图像坐标与物理坐标之间的标定、换算和纠偏，涉及 `VisionFlow.Tools\Tools\GeometryTools.cs`（`AffinePointTool`）、编辑器标定界面，以及供上层调用的标定接口。
HALCON 版本基线：22.11 及以上（与仓库现行基线一致，见 MATCH-MEASURE 计划第 12 节；原文写 20.11）。
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

**实现说明（第一批已完成）**

- 代码：`VisionFlow.Tools\Calibration\CalibrationService.cs`（命名空间 `VisionFlow.Tools.Calibration`）。静态类、纯计算，不依赖 FlowContext 与界面：`SolveAffine(points, transformType)`、`ValidatePoints`、`TransformPoint`、`FromAffine`、`ToJson` / `Parse`（内嵌数据）、`Save` / `Load`（文件）、`TryGetWorldPlaneScale`。`SolveRotationCenter`、`CalibrateCamera` 留到第二、三批。
- 坐标约定：图像 (行, 列) 经矩阵得到物理 (X, Y)，即 `vector_to_hom_mat2d(Px = 行, Py = 列, Qx = X, Qy = Y)`；坐标转换的 `WorldRow` = X、`WorldColumn` = Y（沿用现有输出名与含义）。
- 文件格式 `.vfcal.json`（UTF-8 无 BOM、缩进、中文不转义）：公共字段 `FormatVersion`（1）、`Kind`、`CreatedAt`（带时区）、`Description`、`Unit`；载荷 `Affine2D`（`HomMat2D` 6 个数、`Points`（`Row` / `Column` / `X` / `Y` / `Residual`）、`RmsError`、`MaxError`、`TransformType`）、`RotationCenter`（`Row` / `Column` / `X` / `Y` / `Radius` / `Points`（`Row` / `Column` / `Angle`）/ `RmsError`）、`Camera`（`CamParam` 名称与值列表、`Pose` 7 个数、`PlaneThickness`、`RmsError`、`PlateDescription`、`ImageCount`）。没有数据的段不写；枚举按名称写（文件给人读，与流程文件里工具枚举按数字保存不同）。`Kind` 为主要类型，必须带对应的段；一个文件可同时带 `Affine2D` 与 `RotationCenter`。`RotationCenter` / `Camera` 本批只定义格式与读写校验。
- 读取：文件内容（跳过 BOM 与空白）以 `{` 开头按 JSON 解析，否则按旧版 `write_tuple` 读取（必须是 6 个数，视为 `Affine2D`，没有点对与误差）。格式错误、版本过高、`Kind` 缺段、矩阵 / 位姿个数不对都抛 `InvalidDataException`，信息带文件名（或“内嵌标定数据”）。
- 相机参数：`get_cam_par_names` 是 HDevelop 过程，.NET 中没有对应算子，按 HALCON 文档内置 area_scan_division / polynomial、area_scan_telecentric_division / polynomial、line_scan_division / polynomial 的参数名表；其他类型（如 tilt）明确报“暂不支持”，留到 CB-04 补充。`image_width` / `image_height` 回到 HALCON 元组时为整数。

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

**实现说明（第一批已完成）**

- 宿主窗口（评审 P1-5）：新建 `WpfAffinePointToolEditWindow`（`VisionFlow.WpfToolEditors\Editors`），选项卡“运行参数”与“N 点标定”；CB-03 / CB-04 的助手页在后续批次加入同一窗口。主窗口没有“工具”菜单，改为工具栏“标定助手”按钮：选中“图像坐标转世界坐标”节点时打开其编辑窗口并直接显示 N 点标定页；否则单独打开助手（只有 N 点标定页，结果只能保存为标定文件）。
- 取点：“取当前值”从上次运行结果读行 / 列变量，必须是单值，数组明确拒绝并提示引用元素（如 `排序1.Rows[0]`，评审 P2-4）；“在图像上点击取点”在标定图像上按下即加入一点、按住拖动微调；手动输入四个数加入一点。物理坐标可在表格中修改，或从 CSV 导入：每行“X,Y”依次写入各点（“序号,X,Y”同样可用），“行,列,X,Y”四列时整行导入；非数值行（标题）跳过。
- 计算：调用 `CalibrationService.SolveAffine`，显示每点残差、RMS、最大误差与矩阵；残差超过阈值的点整行标红并在“状态”列标“超阈”，可“删除选中点 / 删除超阈点”后重算。点对有任何改动即作废上次结果，必须重新计算才能保存或内嵌。表格显示按位数格式化（图像坐标 3 位、物理坐标与残差 4 位），编辑框绑定原值，不会因显示位数截断数据。
- 前置校验（22.11 实测，见第 11 节）：affine 对两点重合的 3 个点不报错而返回无意义矩阵，similarity / rigid 对共线点、甚至 2 个点也不报错，因此在调用算子前统一检查——至少 3 个点对、数值有限、图像点与物理点各自不重复的点不少于 3 个、且不共线（垂直主方向的分布不到主方向的 0.1% 视为共线），三种变换类型同样要求；算子仍报 #9211 时转成中文。rigid 用于有比例的点对时算子不报错，残差会直接暴露问题。
- 结果：“保存为标定文件…”写 `.vfcal.json` 并让当前工具改用该文件（清空内嵌数据）；“内嵌到当前工具”把同样的 JSON 写入 `CalibrationData` 并清空 `CalibrationFile`。两者都先作用于窗口内的待提交状态，确定时写回；执行测试经 `ToolTestRun`。打开窗口时若当前标定带点对，载入表格与残差，便于继续调整。

### CB-03 旋转中心标定

- 用途：吸嘴或旋转台带动工件旋转，相机拍到的同一特征点位置变化，拟合出旋转中心，用于 CB-07 纠偏计算。
- 采集：表格记录每次旋转后特征点的图像坐标（来源与 CB-02 相同），可选记录对应的旋转角度。
- 至少 3 个点，建议覆盖 30° 以上的角度范围；角度范围过小时提示误差会偏大。
- 若当前已有 N 点标定结果，同时输出旋转中心的物理坐标。

**约定（第二批，写代码前钉死）**

- 拟合在图像坐标 (行, 列) 中进行，纯 C# 计算，不调用 HALCON 算子。角度沿用图像角度约定：与匹配 `Angle` 相同，屏幕上逆时针为正（第 13 节实测），单位弧度。旋转公式 R(α)·(行, 列) = (行·cos α − 列·sin α, 行·sin α + 列·cos α)，与 `hom_mat2d_rotate` 一致，形式与 CB-07 物理系中的 R(α) 相同；绕中心 c 旋转为 c + R(α)·(p − c)。
- 有角度路径（至少 2 个带角度的点）：同一特征点绕中心旋转，任意两点满足 p_i = c + R(θ_i − θ_j)·(p_j − c)，即 (I − R(Δθ))·c = p_i − R(Δθ)·p_j；对全部点对做线性最小二乘求 c，半径取各点到 c 距离的平均。只有部分点带角度时，不带角度的点不参与求中心，结果给出提示。角度来源若是机构角度而方向与图像约定相反，需要取反；求解时若按相反方向拟合的残差明显更小，结果给出提示。
- 无角度路径（不足 2 个带角度的点时，至少 3 个点）：代数圆拟合（Kåsa：x² + y² + D·x + E·y + F = 0 最小二乘），圆心 (−D/2, −E/2)，半径 √((D² + E²)/4 − F)；在去均值坐标中求解以保证数值稳定。
- 残差 = |点到圆心距离 − 半径|（像素），输出每点残差、`RmsError`、`MaxError`。拒绝求解：点数不足、坐标或角度含 NaN / 无穷、点全部重合、无角度路径的点共线、有角度路径的角度全部相同；提示但照常求解：角度覆盖范围 < 30°（有角度路径按角度，无角度路径按各点绕圆心的覆盖弧度），以及上面两条角度提示。
- 编辑界面的角度列以度显示与输入，文件中 `RotationCenterPoint.Angle` 为弧度；“取当前值”读取的角度变量按弧度（如匹配 `Angle`）。
- 保存与内嵌（2026-10-08 与使用方确认）：当前工具的标定带 `Affine2D` 段时，结果与它合并写成一个 `.vfcal.json`（保留 `Affine2D` 段、新增 `RotationCenter` 段并换算圆心物理坐标 X / Y，`Kind` 仍为 `Affine2D`），供坐标转换与 CB-07 `RotateAroundCenter` 同时使用；没有 `Affine2D` 标定时只写 `RotationCenter` 段（`Kind = RotationCenter`）。“内嵌到当前工具”走 `UseEmbeddedCalibration`，互斥规则同 CB-02。

**实现说明（第二批已完成）**

- 服务：`CalibrationService.SolveRotationCenter(points)` 返回 `RotationCenterSolution`（`Calibration` 为 `RotationCenterCalibration`，另有 `UsedAngles`、`CoverageDegrees`、`Warnings`）。有角度路径利用 MᵀM = (2 − 2·cos Δθ)·I（M = I − R(Δθ)），中心 = Σ Mᵀ·rhs / Σ(2 − 2·cos Δθ)，无需通用矩阵求解；“角度方向相反”提示按旋转一致性残差（用 j 点绕中心转 Δθ 预测 i 点）比较两个方向，相反方向的残差不到四分之一时提示。新增纯计算的 `TransformPose`、`RotateAbout`、`WrapAngle`，供 CB-07 使用。
- 格式：`RotationCenterPoint` 新增 `Residual`，`RotationCenterCalibration` 新增 `MaxError`（都是可选字段，没有值时不写）；`Validate` 补 `RotationCenter` 段检查（圆心有限、`Radius` ≥ 0 且有限、误差与各点有限）。合并规则由 `CalibrationService.MergeRotationCenter(existing, center, unit, description)` 实现。重新做 N 点标定后保存 / 内嵌时，若当前标定已带旋转中心，保留它并按新矩阵重新换算圆心物理坐标，避免丢失。
- 界面：`WpfAffinePointToolEditWindow` 新增“旋转中心”页（单独打开的标定助手同样可用，只能保存为文件）：取点方式同 N 点标定（取当前值 / 图像点击 / 手动），角度列以度显示；求解后表格给出每点残差，图像上叠加旋转点十字、拟合圆与圆心；带 `Affine2D` 时显示圆心物理坐标；提示（覆盖不足 30°、部分点无角度、角度方向可能相反）以醒目颜色显示。只含旋转中心的标定不改当前工具的来源、也不能内嵌（坐标转换需要 `Affine2D`），会明确提示。界面叠加用到 `gen_circle_contour_xld`（只用于显示）。

### CB-04 相机标定（标定板）

- 输入：多张标定板图像（从文件夹加入，或把当前输入图像加入），标定板描述文件（HALCON 标准标定板，如 `calplate_160mm.cpd`），相机模型（`area_scan_division` / `area_scan_polynomial`），初始参数（焦距、像元宽高、图像宽高）。
- 算子：`create_calib_data` → `set_calib_data_cam_param` / `set_calib_data_calib_object` → 逐张 `find_calib_object` → `calibrate_cameras` → `get_calib_data`（相机参数、位姿）。
- 测量平面：选择一张标定板放在测量平面上的图像作为参考位姿，填写标定板厚度，用 `set_origin_pose` 把位姿修正到测量平面。
- 显示：每张图像的标定板识别结果、重投影误差；识别失败的图像标出并允许移除。
- 建议 10~20 张、覆盖视野不同位置和倾斜角度，界面给出提示。
- 开工前置结论（评审 P1-3，22.11 实测，详见第 13 节）：HALCON 自带的单相机示例是 `%HALCONIMAGES%\calib\calib_single_camera_01.png` ~ `_07.png`（7 张，1292×964）配 `%HALCONROOT%\calib\calplate_80mm.cpd`（不是 160 mm），初始参数 `area_scan_division`、焦距 8 mm、像元 3.7 µm、主点 (646, 482)；实跑 7 张全部找到标定板，反投影误差 0.0775 像素。验收用这组数据，误差上限取 0.1 像素；路径由环境变量 `HALCONROOT` / `HALCONIMAGES` 解析，缺任一文件时用例明确失败、不计通过。`gen_cam_par_area_scan_division` 等是 HDevelop 过程，.NET 中直接拼参数元组（类型名在前）。

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

**实现说明（第一批已完成）**

- 输入：`Row` / `Column` / 新增“角度”（`AnglePath`，弧度，可选）均为 `AcceptsCollection`；个数按 `PairingHelper` 配对（相等逐一对应、一侧为 1 个时一对多、其他报错，任一侧为空时数量为 0 并输出 NaN / 空数组）。单值输入时输出与旧版逐项一致（回归用例）。
- 标定来源：`CalibrationKind`（`Affine2D` 默认 / `Camera`）、`CalibrationSource`（`File` 默认 / `Embedded`）按数字保存；`Affine2D` 方式引用了变换矩阵时直接用该矩阵（现有行为，优先于标定来源）。文件按“完整路径 + 修改时间 + 大小”缓存，内嵌数据按内容缓存。互斥沿用第五批差分检测的做法（评审 P2-2）：`UseCalibrationFile` / `UseEmbeddedCalibration` 切换来源时清空另一侧，编辑窗口切换来源也走这两个方法；侧栏只改 `CalibrationSource`、手工改流程文件造成两侧同时有值时，流程校验与运行都报错并说明以哪个为准。内嵌数据只在编辑窗口生成与查看，侧栏隐藏 `CalibrationData`，来源为内嵌时同时隐藏 `CalibrationFile`；`Camera` 方式隐藏变换矩阵输入与 `Matrix` 输出。
- 类型校验（评审 P1-4）：`Camera` 方式要求标定内容带 `Camera` 段，`Affine2D` 方式要求带 `Affine2D` 段（旧版矩阵文件视为 `Affine2D`）；不符时流程校验、运行、预热都报“来源（文件名或内嵌标定数据）+ 实际类型（含哪些段）+ 期望类型”，旧版矩阵文件用于 `Camera` 方式时单独说明。`Camera` 方式配置了变换矩阵引用时报错。文件缺失仍不在校验阶段报（沿用现有行为，文件可能部署时才放置），由预热与运行报告；文件存在时校验阶段即检查格式与类型。
- 换算：`Affine2D` 用 `HomMat2D.TransformPose`（点与“沿角度方向 1 个单位”的第二点同时变换求方向）；`Camera` 用 `image_points_to_world_plane` 对点与沿角度方向 1 像素的第二点分别换算，`Scale` 取标定内容的 `Unit`（m / cm / mm / um，空为 m，其他单位明确报错）。
- 角度约定（开工时与使用方确认）：`WorldAngle` 与图像角度同一约定——在 (`WorldRow`, `WorldColumn`) 坐标中，方向向量 (ΔWorldRow, ΔWorldColumn) 的 `WorldAngle` = atan2(−ΔWorldRow, ΔWorldColumn)。单位矩阵时等于输入角度，纯旋转 θ 时为 φ + θ；方向向量直接经变换求得，镜像（行列式为负）时自然正确（列镜像得 π − φ、行镜像得 −φ，用例固定），不用“φ + 矩阵旋转量”。注意 `Camera` 方式下测量平面 X 轴沿图像列方向（第 11 节），因此近正对的相机 `WorldAngle` ≈ 输入角度 − 90°，这是同一约定在 WorldRow = X 时的结果。
- 输出：`WorldRow` / `WorldColumn`（第一个点，没有点时 NaN）、`WorldRows` / `WorldColumns`、`WorldAngle` / `WorldAngles` / `WorldAngleDeg`（没有角度输入时为 NaN / 空数组）、`Count`、`Matrix`（`Affine2D` 时为所用矩阵；引用的矩阵按借用语义写出；`Camera` 方式不写）。输出名均不与参数名相同（第二批守卫测试）。
- 分类：工具箱移入新分类 `09 标定`，ID `affine-point`、类型名与图标不变；编辑器路由改到新窗口（原走通用视觉预览窗口）。

### CB-06 畸变校正（新工具）

- 工具箱：`09 标定 / 畸变校正`，ID `image-rectify`，类 `ImageRectifyTool : ToolBase, IToolResourceLifecycle`。
- 用途：把有镜头畸变和透视的图像校正为测量平面上比例均匀的图像，之后的像素距离可直接乘固定比例得到物理长度。
- 输入：图像（必填）。
- 参数：`CalibrationFile` / `CalibrationData`（`Camera` 类型）、`PixelSize`（校正后每像素对应的物理长度，0 表示按原图中心比例自动计算）、输出区域（以测量平面坐标给出的左上角和宽高，默认覆盖整个视野）、`Interpolation`。
- 算子：`gen_image_to_world_plane_map`（只在参数或标定变化时生成，结果缓存，参与预热）+ `map_image`。
- 输出：`Image`；`ActualPixelSize`（实际比例；参数 `PixelSize` 为 0 时由原图中心比例算出）；`Matrix`（校正图像素坐标 → 测量平面坐标的仿射矩阵，供“图像坐标转世界坐标”和单位换算使用）。
- 命名（评审 P1-1）：输出原写 `PixelSize`，与同名参数冲突，会使第二批“输出名不与参数名相同”的守卫测试失败，改名 `ActualPixelSize`；参数 `PixelSize` 保持不变（与 MS-07 单位换算的 `PixelSize` 含义一致）。

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
  - `CameraMounting`：`Fixed`（相机固定，看工件）/ `OnAxis`（相机随轴运动）；决定偏移量的符号约定（见下方“符号约定”）。
  - `AngleUnit`：输出角度用度或弧度。
- 输出：`DeltaX`、`DeltaY`、`DeltaAngle`、`AngleDifference`（测得的角度差 dθ，仅供参考与判定）、`WorldX`、`WorldY`（当前位置的物理坐标）、`Valid`（输入不是 NaN 时为 true）。
- 输入为 NaN（如匹配未找到）时输出 NaN 与 `Valid = false`，不按失败处理，由后续判定决定 NG。

**符号约定（评审 P1-2，写代码前钉死，2026-10-07 与使用方逐项确认）**

坐标系与量（全部在物理坐标系中计算）：

- 物理坐标沿用 CB-05：X = `WorldRow`、Y = `WorldColumn`（标定点对的物理 X / Y）。旋转 R(α) 以“从 +X 转向 +Y”为正：R(α)·(x, y) = (x·cos α − y·sin α, x·sin α + y·cos α)；绕点 C 旋转记 R(C, α)·P = C + R(α)·(P − C)。
- 当前位姿：P = (X, Y) 与 θ 由当前图像位姿 (行, 列, 角度) 经 CB-05 的同一换算得到；基准位姿：B 与 θb 由 `BaseRow` / `BaseColumn` / `BaseAngle` 同样换算。
- 角度差 dθ = θ − θb（折算到 (−π, π]）：用 CB-05 的 `WorldAngle` 相减，正值表示当前件相对基准“从 +X 转向 +Y”转过的角度。镜像标定（矩阵行列式为负）时依然正确——方向向量经变换后再求角，图像中的逆时针在物理系中自动变成相应方向。
- 角度方向更正：评审原文“图像坐标，顺时针为正，与匹配 Angle 一致”自相矛盾。22.11 实测（第 13 节）HALCON 匹配 `Angle` 与 `hom_mat2d_rotate` 都是**屏幕上逆时针为正**（行轴向下）；本约定不在图像系中定义角度差，避免这一歧义。
- 旋转中心 C：取标定内容中 `RotationCenter` 的物理坐标 X / Y（CB-03 输出；只有图像坐标时用同一 `Affine2D` 换算）。假定旋转中心是在拍照时的机构位置下标定的。

输出含义：**平台补偿量**——把当前件移回基准位姿所需的运动；执行顺序为先绕 C 转 `DeltaAngle`，再平移 (`DeltaX`, `DeltaY`)。`AngleDifference` 总是输出 dθ（不论方式），`DeltaAngle` 只在实际补偿旋转时非零。

| `CameraMounting` | `Mode` | `DeltaAngle` | (`DeltaX`, `DeltaY`) |
|---|---|---|---|
| `Fixed`（相机固定看工件，轴带着工件动） | `RotateAroundCenter` | −dθ | B − R(C, −dθ)·P |
| `Fixed` | `TranslationOnly` | 0 | B − P |
| `OnAxis`（相机装在轴上看固定目标） | `TranslationOnly` | 0 | P − B（与 `Fixed` 符号相反：轴移动 Δ，目标在相机中的相对位置移动 −Δ） |
| `OnAxis` | `RotateAroundCenter` | — | 第二批不支持，流程校验报错 |

两行公式（评审要求）：

- 相机固定：`DeltaAngle = −dθ`，`(DeltaX, DeltaY) = B − R(C, −dθ)·P`（`TranslationOnly` 时按 dθ = 0 计算，即 `B − P`，`DeltaAngle = 0`）。
- 相机随轴：`DeltaAngle = 0`，`(DeltaX, DeltaY) = P − B`（只支持 `TranslationOnly`）。

`OnAxis` + `RotateAroundCenter` 的限制：相机随轴旋转后，后续平移所对应的相对位移取决于机构叠放方式（θ 轴装在 XY 上，还是 XY 装在 θ 轴上），没有实际机构信息前无法给出唯一公式。第二批对该组合在流程校验与运行中都给出中文说明（建议改用 `TranslationOnly`，或由上层按机构计算），待有现场机构时单独立项。

验收用例按约定反推真值（用例即约定的可执行文档）：给定标定矩阵（含一例镜像矩阵）、旋转中心 C、基准 B / θb 与期望补偿 (Δ, −dθ)，由 P = R(C, dθ)·(B − Δ)、θ = θb + dθ 反算当前物理位姿，再经标定逆变换得到当前图像位姿作为输入；输出须与 Δ、−dθ 一致。`OnAxis` 用例验证 P − B 与 `Fixed` 结果互为相反数，`OnAxis` + `RotateAroundCenter` 验证校验报错。

**实现说明（第二批已完成）**

- 类型：`AlignmentOffsetTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility, IToolResourceLifecycle`（`VisionFlow.Tools\Tools\AlignmentOffsetTools.cs`），ID `alignment-offset`，工具箱 `09 标定 / 纠偏计算`，图标 `ToolIcon.alignment-offset`。新枚举 `AlignmentMode`（`TranslationOnly` / `RotateAroundCenter`，默认后者）、`CameraMounting`（`Fixed` 默认 / `OnAxis`），`AngleUnit` 复用单位换算的枚举（弧度默认 / 度），均按数字保存。持久化 ID 须在 `BuiltinToolIdentities` 登记（`ToolboxRegistry` 只负责工具箱条目，不决定流程文件中的 `ToolId`），与其他内置工具一致。
- 计算逐字按上方“符号约定”：P / θ 与 B / θb 用 `CalibrationService.TransformPose`（纯计算，与 CB-05 的换算路径相同，用例与坐标转换的 `WorldRow` / `WorldColumn` / `WorldAngle` 对照一致），dθ = `WrapAngle(θ − θb)`；旋转中心取 `RotationCenter.X / Y`，没有时用同一矩阵换算 `Row / Column`；`DeltaAngle` 与 `AngleDifference` 按 `AngleUnit` 输出，−0 按 0 输出。
- 输入：`Row` / `Column` 必填、角度可选，均为单值（数组明确拒绝，多个目标放在 For 循环中逐个计算）。`RotateAroundCenter` 必须有角度输入（校验与运行都报错）；`TranslationOnly` 没有角度输入时 `AngleDifference` 为 NaN、`DeltaAngle` 为 0、`Valid` 仍为 true。引用无法解析按运行失败；解析成功但含 NaN 时全部输出 NaN、`Valid = false`、写警告日志，节点不失败。
- 校验（与运行、预热措辞相同）：标定内容须带 `Affine2D` 段，`RotateAroundCenter` 还须带 `RotationCenter` 段，缺段时报“来源（文件名或内嵌标定数据）+ 实际类型（含哪些段）+ 期望段”；旧版 `write_tuple` 矩阵文件可用于 `TranslationOnly`，用于 `RotateAroundCenter` 时单独说明。`OnAxis` + `RotateAroundCenter` 按计划原文拒绝。文件 / 内嵌互斥与缓存沿用批一（抽出共用的 `CalibrationSourceCache`，坐标转换改为调用它，行为与提示不变）；文件缺失不在校验阶段报，由预热与运行报告。
- 编辑窗口：`WpfAlignmentOffsetToolEditWindow`：当前位姿引用、标定来源（选择文件 / 把标定文件内嵌到工具，切换来源清空另一侧）与内容摘要（含哪些段、旋转中心物理坐标，缺段时直接显示校验说明）、纠偏方式（选到 `OnAxis` + `RotateAroundCenter` 时即时提示不支持）、基准位姿与“用当前结果设为基准”（从上次运行结果读取当前位姿引用的值，数组与 NaN 明确拒绝），执行测试经 `ToolTestRun`，结果表按 R 格式列出全部 7 个输出。

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
- CB-04：使用 HALCON 安装目录自带的标定板示例图像完成标定（`calib_single_camera_01~07` + `calplate_80mm.cpd`，见 CB-04 前置结论），重投影误差不超过 0.1 像素（22.11 实测 0.0775）；缺少示例图像或描述文件时用例明确失败，不计为通过（与现有 HALCON 用例约定一致）。
- CB-05：数组输入与逐个单值输入结果一致；含镜像的矩阵下角度换算正确；`Camera` 方式与直接调用 `image_points_to_world_plane` 一致。
- CB-06：校正后的标定板图像中，相邻标记点间距与真实间距误差小于 0.5%；输出名 `ActualPixelSize` 与参数 `PixelSize` 不冲突（守卫测试保持通过）。
- CB-07：按 CB-07“符号约定”反推真值构造前后位姿（含镜像标定一例），输出的 `DeltaX` / `DeltaY` / `DeltaAngle` / `AngleDifference` 与真值一致；`Fixed` 下 `TranslationOnly` 与 `RotateAroundCenter` 各一例，`OnAxis` 的 `TranslationOnly` 与 `Fixed` 互为相反数，`OnAxis` + `RotateAroundCenter` 校验报错；输入 NaN 时 `Valid = false`。
- 全部回归测试和 `examples\*.vflow.json` 通过。

## 10. 开发顺序

1. CB-01（文件格式与服务）、CB-02（N 点标定助手）、CB-05（坐标转换增强）：最常用的 9 点标定闭环。
2. CB-03（旋转中心）、CB-07（纠偏计算）：机器人对位闭环。
3. CB-04（相机标定）、CB-06（畸变校正）。

## 11. 第一批算子探测结论（CB-01 / CB-02 / CB-05）

**探测环境**：HALCON 22.11 Steady（原生库与 .NET 库均为 22.11，见 MATCH-MEASURE 计划第 12 节）；部署到其他版本前需复核。

| 问题 | 结论 |
|---|---|
| `image_points_to_world_plane` 的 `Scale` | 是“输出单位”，不是“物理单位 / 像素”：`m` / `cm` / `mm` / `um` 时世界坐标即以该单位输出；数值 s 时输出为“米 ÷ s”（0.001 与 `mm` 相同，1000 得到千米级）。默认 `m`，`get_param_info` 的取值列表为空。位姿的长度单位为米。例：焦距 16 mm、像元 5 µm、平面在 0.5 m 处，100 像素 → 0.015625 m = 15.625 mm。测量平面 X 轴沿图像列方向、Y 轴沿行方向（正对时列 +100 → X 增加，行 +100 → Y 增加） |
| `vector_to_hom_mat2d` 共线 / 重复 | 3 点或 4 点共线、3 点全部重合：#9211（Matrix is not positive definite）；**3 点中两点重合（只有 2 个不同点）不报错，返回无意义矩阵**；少于 3 点 #1401；图像点与物理点个数不同 #1403 |
| `vector_to_similarity` / `vector_to_rigid` | 共线点、甚至只有 2 个点都能求解（相似、刚体变换 2 点即确定）；全部重合 #9211；1 点 #1401。rigid 用于有比例的点对不报错，返回比例为 1 的矩阵与错误的平移，只有残差能暴露 |
| 结论 | 服务在调用算子前统一做中文前置检查（第 5 节 CB-02 实现说明），#9211 仍转成中文 |
| `vector_to_hom_mat2d` 可复现性（界面验收中发现） | 同一组输入在同一进程内反复调用，会得到两种结果（300 次中 231 / 69 次），差 1~2 ULP（如 0.001800549770709211 与 0.0018005497707092102），跨进程同样。运行时只用标定文件中保存的矩阵，换算结果确定；但“用相同点对重新求解得到逐位相同的矩阵”不成立，比较重解结果要按相对 1e-12 |
| `get_cam_par_names` | 是 HDevelop 过程，.NET 的 `HOperatorSet` 中没有；参数名按 HALCON 文档内置（CB-01 实现说明） |

## 12. 第一批（CB-01 + CB-02 + CB-05）评审处理与验收记录

**评审意见处理**（[CALIBRATION-TOOLS-PLAN-REVIEW.md](CALIBRATION-TOOLS-PLAN-REVIEW.md)）

| 条目 | 处理 |
|---|---|
| P1-1 CB-06 `PixelSize` 同名 | 已在计划中处理：CB-06 输出改名 `ActualPixelSize`、参数 `PixelSize` 不变（第 6 节 CB-06、第 9 节）；实现属第三批。本批新增输出均不与参数同名（守卫用例） |
| P1-2 CB-07 符号约定 | 已在计划中钉死（第 6 节 CB-07“符号约定”，与使用方逐项确认），并更正评审中“顺时针为正”的表述（第 13 节实测）；实现属第二批，CB-07 建立在本批 CB-05 的角度约定上 |
| P1-3 CB-04 示例图像 | 已探测：示例图像、标定板描述文件、初始参数与参考误差见 CB-04 前置结论与第 13 节；实现属第三批 |
| P1-4 Camera 校验双路径 | 流程校验、运行、预热都报“来源 + 实际类型 + 期望类型”（用例覆盖文件、旧版矩阵文件、内嵌三种来源）；`Scale` 语义见第 11 节并在代码中注释 |
| P1-5 新建宿主窗口 | `WpfAffinePointToolEditWindow`（运行参数页 + N 点标定页），执行测试经 `ToolTestRun`；主窗口没有“工具”菜单，以工具栏“标定助手”按钮代替 |
| P2-1 服务形态 | 静态、纯计算、不依赖 FlowContext；旧版 `write_tuple` 按内容识别放在 `Load` |
| P2-2 内嵌 / 文件互斥 | `UseCalibrationFile` / `UseEmbeddedCalibration` 切换清空另一侧；校验与运行拦截手工改出的冲突；内嵌 JSON 在流程文件中按既有行为转义为 `\uXXXX`（README 已注明） |
| P2-4 取当前值 | 要求单值，数组明确拒绝并提示引用元素 |
| P2-5 登记与分类 | 新分类 `09 标定`，`affine-point` 移入，ID / 类型名 / 图标不变；`BuiltinToolIdentities` 已有固定 ID、`ToolIcons.xaml` 已有图标，无需新增；路由改到新窗口；README 新增“标定”行 |
| P2-6 枚举 | `CalibrationKind` / `CalibrationSource` 按数字保存、默认值为现有行为（历史流程加载用例）；文件内的 `Kind` / `TransformType` 按名称写 |
| P2-7 守卫测试 | 保持通过 |
| P3 文档 | 计划头部、CB-01 / CB-02 / CB-05 实现说明、第 11 节、README（工具表、`.vfcal.json` 约定） |

**验收结果**

- 单元/集成测试：新增 `CalibrationBatch1Tests` 32 个用例：`.vfcal.json` 全字段保存读取一致（含 RotationCenter / Camera 段、中文可读、重新序列化逐字相同）；旧版 `write_tuple` 文件、BOM 与前导空白识别；5 种格式错误；相机参数名表往返；已知仿射加 ±0.1 像素噪声求解（网格内与真值之差 < 0.005 mm，即噪声 × 比例）；相似 / 刚体变换恢复真值；三种变换类型 × 5 种退化（少于 3 点、共线、重复、物理点共线、NaN）共 15 例；数组与逐个单值一致、一对多配对、个数不符、空数组；单位矩阵 / 旋转 / 列镜像 / 行镜像 / 镜像加转置的角度；Camera 方式与直接调用 `image_points_to_world_plane` 逐位一致及不支持的单位；三种来源的类型不符在校验、运行、预热中给出相同信息；互斥与冲突拦截；旧矩阵文件单值、引用矩阵优先、未配置与文件缺失提示、文件替换重读、历史流程默认值（回归门禁）；保存加载、命名守卫、工具箱 `09 标定`。全量 `dotnet test`（`Category!=Soak`）923 个通过。
- 界面验收：脚本经 Windows UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`，35 项检查全部通过（修复后连续三次）：
  - 用正常文件对话框打开流程（图像加载 → 阈值 → 区域排序 → 方向特征，9 个旋转 20° 的目标），从工具箱 `09 标定` 新建坐标转换；选中该节点点工具栏“标定助手”，直接打开其编辑窗口的 N 点标定页。
  - 三种取点：数组变量“取当前值”被拒绝并提示引用元素，改为 `排序1.Rows[0]` 后取到的值与目标中心逐位相同；在图像上真实鼠标点击取点（与真值差 < 1.5 像素）；手动输入 7 个点。
  - 经系统打开对话框导入 CSV（标题行跳过），网格中心点物理 X 故意偏 1 mm：计算后只有该点超阈（残差 0.89 mm，其余约 0.11 mm），整行标红；删除超阈点重算后 8 个点无超阈。
  - 经保存对话框保存 `.vfcal.json`，运行参数页回写来源与路径；以数组输入（9 个中心 + 方向角）执行测试，结果表与无界面输出逐字一致；界面运行 6 个单值、3 个数组长度与无界面运行保存的文件逐字一致；文件中的点对与输入逐位相同，矩阵与用同样点对无界面求解一致（相对 1e-12，见第 11 节可复现性）；9 个中心换算与真值最大偏差 0.005 ~ 0.012 mm、角度偏差 0.002° ~ 0.012°（真值 = 图像方向 + 2°；随每次点击取点的位置变化，各次运行均在 0.08 mm 的验收界内）。
  - “内嵌到当前工具”后来源为内嵌、标定文件框隐藏，侧栏隐藏 `CalibrationFile` / `CalibrationData`；运行结果与文件标定一致；侧栏只把来源改回 File 时运行被流程校验拦截（内嵌数据仍在），改回后恢复。
  - 保存 → 重新加载 → 再运行与无界面对照逐字一致，重新打开窗口来源、角度输入与标定摘要回显正确；未选中坐标转换节点时，工具栏按钮单独打开助手（只有 N 点标定页，“内嵌到当前工具”不可用）。
- 验证中发现并处理的问题：
  1. `InvalidDataException` 不是 `IOException` 的子类，内嵌数据损坏时流程校验抛出异常而不是报告问题（单元测试发现）；校验、运行与编辑窗口的捕获补上该类型。
  2. 点对表格直接显示完整精度的双精度数，在助手页的列宽下全部被截断成“240....”（界面截图发现）；改为按位数格式化显示、编辑框绑定原值，并加宽右栏。
  3. `vector_to_hom_mat2d` 结果不可逐位复现（见第 11 节）：属 HALCON 行为，不是应用问题；验收脚本原先要求重解矩阵逐位相同，改为相对 1e-12 并记录在案。
- 脚本问题（已修脚本）：区域方向特征以 180° 为周期（实测 −160.92° 与 20° 同一方向轴）；DataGrid 按需生成行，读取与截图前先滚动到目标行；运行后侧栏不再显示该工具，需要重新选中节点；PowerShell 数组字面量中逗号优先于 `+`，批量替换脚本丢失了几行，改用编辑工具。

## 13. 后续批次前置结论（评审 P1-1 / P1-2 / P1-3）

**探测环境**：HALCON 22.11 Steady（`HALCONROOT` = `C:\Program Files\MVTec\HALCON-22.11-Steady`，`HALCONIMAGES` = `C:\Users\Public\Documents\MVTec\HALCON-22.11-Steady\examples\images`）；部署到其他版本或其他安装路径前需复核。

| 条目 | 结论 |
|---|---|
| P1-1 CB-06 命名 | 输出 `PixelSize` → `ActualPixelSize`，参数 `PixelSize` 不变（第 6 节 CB-06）。第二批引入的“输出名不与参数名相同”守卫测试在第三批实现时保持通过 |
| P1-2 匹配角度方向 | 实测：把同一图形在屏幕上逆时针转 10°（长臂朝右上、行坐标变小），`find_shape_model` 的 `Angle` = +10.14°；顺时针转 10° 得 −10.11°；`hom_mat2d_rotate(+10°)` 把中心右侧的点变到行更小的位置。即 HALCON 匹配角度与旋转矩阵都是**屏幕上逆时针为正**（行轴向下），与 `HomMat2D` 中的注释一致；评审“顺时针为正，与匹配 Angle 一致”的表述不成立 |
| P1-2 物理系角度差 | CB-05 的 `WorldAngle` = atan2(−ΔX, ΔY)。对物理系中从 +X 转向 +Y 的方向角 β，`WorldAngle` = β − 90°（恒等式），因此 `WorldAngle` 之差就是物理系中“从 +X 转向 +Y”的转角，与镜像无关。CB-07 的 dθ 据此定义 |
| P1-2 使用方确认的 4 项选择 | ① 角度差在物理系中计算；② 输出为平台补偿量（把当前件移回基准的运动）；③ `TranslationOnly` 时 `DeltaAngle = 0`，测得的角度差放在新输出 `AngleDifference`；④ `OnAxis` 第二批只支持 `TranslationOnly`（补偿 = P − B，与 `Fixed` 相反），`OnAxis` + `RotateAroundCenter` 由流程校验拒绝。公式与表见第 6 节 CB-07“符号约定” |
| P1-3 标定板描述文件 | `%HALCONROOT%\calib\` 下有 `calplate_5mm` / `10mm` / `20mm` / `40mm` / `80mm` / `160mm` / `320mm` / `640mm` / `1200mm.cpd`（20 / 40 / 80 mm 另有 `_dark_on_light` 版本），以及旧式 `caltab_*.descr` |
| P1-3 示例图像 | `%HALCONIMAGES%\calib\` 共 153 个文件；HALCON 示例 `Calibration\Multi-View\calibrate_cameras_monocular.hdev` 用其中的 `calib_single_camera_01.png` ~ `_07.png`（7 张，1292×964）配 `calplate_80mm.cpd`，初始参数 `area_scan_division`、焦距 0.008 m、kappa 0、像元 3.7e-6 m、主点 (646, 482)。`calplate_160mm.cpd` 只在多相机质量检查与拼接示例中使用，不适合作单相机验收 |
| P1-3 参考误差 | 按上述参数在 .NET 中实跑 `create_calib_data` → `set_calib_data_cam_param` / `set_calib_data_calib_object` → 逐张 `find_calib_object` → `calibrate_cameras`：7 张全部找到标定板，反投影误差 0.0775 像素；结果焦距 8.343 mm、kappa −1546.0、主点 (638.73, 470.68)，`sy` 保持 3.7e-6（默认不优化）。CB-04 验收上限取 0.1 像素 |
| P1-3 实现注意 | `gen_cam_par_area_scan_division` 与 `get_cam_par_names` 一样是 HDevelop 过程，.NET 中没有，初始参数直接拼元组（类型名在前）；`read_image` 的相对路径按 `HALCONIMAGES` 解析。用例取 `HALCONROOT` / `HALCONIMAGES` 拼出绝对路径，缺任一文件时明确失败 |

## 14. 第二批（CB-03 + CB-07）实现与验收记录

**开工依据**：基线 `origin/main` 含批一与 P1 处理（`be6bb1c`）；CB-07 逐字按第 6 节“符号约定”实现（使用方要求以计划为准，不以任务草案为准）；CB-03 的约定在动工前补入第 5 节（角度约定、两条求解路径、拒绝与提示、合并保存规则）。本批不新增 HALCON 算子的计算路径（CB-03 / CB-07 都是纯 C#），界面叠加用到的 `gen_circle_contour_xld` 只用于显示，无需探测。

**与任务说明不一致处（如实记录）**

| 项 | 处理 |
|---|---|
| 旋转中心“只带 RotationCenter 段”保存 | 这样 CB-07 `RotateAroundCenter` 无法从界面得到同时带两段的标定，内嵌到坐标转换还会让其 `Affine2D` 方式缺段。经使用方确认改为：当前标定带 `Affine2D` 时合并保存（`Kind` 仍为 `Affine2D`），否则只写 `RotationCenter` |
| “`BuiltinToolIdentities` 无需新增” | 流程文件中的 `ToolId` 只由 `FlowSerializer.RegisterToolType`（在 `BuiltinToolIdentities`）决定，`ToolboxRegistry` 不登记持久化身份；不登记则保存的 `ToolId` 不是 `alignment-offset`，守卫用例会失败。已按其他内置工具的做法登记 |
| 草案中的旋转公式“顺时针为正” | 以计划第 6 节为准：R(α)·(x, y) = (x·cos α − y·sin α, x·sin α + y·cos α)，物理系正角为“从 +X 转向 +Y”；CB-03 在图像 (行, 列) 中使用同一形式，与 `hom_mat2d_rotate`（屏幕上逆时针为正）一致，用例用 HALCON 生成旋转点对照 |
| `TranslationOnly` 的 `DeltaAngle` | 按计划第 6 节输出 0（草案写“仍输出 −dθ”），测得的角度差在 `AngleDifference` |

**验收结果**

- 单元/集成测试：新增 `CalibrationBatch2Tests` 26 个用例：
  - CB-03：有角度路径（7 点覆盖 60°，±0.2 像素噪声，圆心误差 < 0.2 像素；无噪声 < 1e-9）、无角度路径（10 点覆盖 180°，同噪声，圆心误差 < 0.2 像素）、与 `hom_mat2d_rotate` 生成的旋转点对照（角度约定一致）、三类提示（覆盖不足 30°、部分点无角度、角度方向相反）与只有 1 个带角度点时退回圆拟合、7 种退化输入的中文提示、`RotationCenter` 段保存读取往返与非法载荷、合并保存后同一文件两段可读且坐标转换与纠偏计算都能使用。
  - CB-07（按符号约定反推真值）：`Fixed` + `RotateAroundCenter`（一般仿射、镜像标定各一例）、镜像标定下图像角度差与物理角度差方向相反仍按物理系输出、`Fixed` + `TranslationOnly`（`DeltaAngle` = 0、`AngleDifference` = dθ）、`OnAxis` + `TranslationOnly` 与 `Fixed` 互为相反数、`OnAxis` + `RotateAroundCenter` 校验与运行拒绝（说明逐字为计划原文）、角度单位为度、物理角与坐标转换 `WorldAngle` 一致（含镜像）、`Row` 或角度为 NaN 时全部 NaN 且 `Valid = false`、节点不失败；缺段（只有 `RotationCenter`、`RotateAroundCenter` 缺 `RotationCenter`、旧版矩阵文件、内嵌数据）在校验 / 运行 / 预热中措辞一致，`TranslationOnly` 可用旧版矩阵文件；缺角度、互斥、文件缺失（校验不报、运行与预热报）、数组输入拒绝；保存加载、默认值、命名守卫、工具箱 `09 标定` 与 `"ToolId": "alignment-offset"`。
  - 坐标转换改用共用的 `CalibrationSourceCache` 后，批一 32 个用例与预热用例全部保持通过。全量 `dotnet test`（`Category!=Soak`）949 个通过（含 `examples\*.vflow.json` 加载用例）。
- 界面验收：脚本经 Windows UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`，31 项检查全部通过（连续两次）：
  - 坐标转换编辑窗口的“旋转中心”页：取当前值（角度变量留空）、真实鼠标在图像上点击取点、手动输入，圆拟合结果与服务对同一组表格数据的无界面求解一致，带 `Affine2D` 时显示圆心物理坐标；改为 4 个带角度的点按角度求解，圆心 (240.500, 320.250) 与真值一致，表格角度以度显示、每点残差；删除一点后结果作废、未重新求解不能保存；合并保存后坐标转换改用合并文件，文件 `Kind = Affine2D`、两段齐全、圆心物理坐标按矩阵换算、角度以弧度保存。
  - 工具箱 `09 标定` 新建纠偏计算：经系统对话框选择合并文件，摘要显示两段与旋转中心物理坐标；“用当前结果设为基准”逐位写入上次运行值，当前 = 基准时补偿为 0；改基准后执行测试的 7 个输出与无界面同参数运行逐字一致（`AngleDifference` = −0.05）；选 `OnAxis` + `RotateAroundCenter` 时窗口即时提示且运行被拒绝。
  - 界面运行 7 个单值与无界面运行保存的文件逐字一致；文件含 `"ToolId": "alignment-offset"`、`Mode` 1、基准位姿；重新加载后再运行一致，两个窗口回显正确（旋转中心页从合并文件载入 4 个点并给出同样结果）。
- 验证中发现的问题：
  1. 纠偏计算在 dθ = 0 时 `DeltaAngle` 为 −0，结果表显示“-0”（界面验收设计时发现）；角度输出把 −0 按 0 输出。
  2. 既有行为（不属于本批）：同一流程有两幅图像时，结果显示下拉中直接选第二幅图像上的区域（如 `阈值2.Region`），叠加画在第一幅图像上——叠加修复的底图回退取“上下文中第一个图像变量”（`REVIEW-FIX-PROGRESS.md` 已注明多图像时取最先写入者），在多图像流程中会选错底图。建议后续单独立项（如按区域来源工具的图像输入回退）。
- 脚本问题（已修脚本）：4 次运行中有 1 次双击流程树节点后编辑窗口未打开（之后 3 次未复现，判断为双击未被识别）；栅格化小圆的区域中心按像素取整，与亚像素真值每轴差到 0.5 像素，对照前提改为距离 < 0.75 像素；只覆盖 60° 的圆拟合对点击误差很敏感，改为与服务对同一组表格数据的无界面求解比对。
