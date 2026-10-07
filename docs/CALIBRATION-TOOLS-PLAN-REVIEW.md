# CALIBRATION-TOOLS-PLAN 开工评审意见

评审日期：2026-10-07
评审对象：`docs\CALIBRATION-TOOLS-PLAN.md`（CB-01 ~ CB-07，共 7 项）
评审方式：与 main（`fb6d568`）当前代码核对（`GeometryTools.cs` 的 `AffinePointTool`、`WpfToolEditorRouter` 路由、`ToolboxRegistry` 分类编号、第二批引入的输出/参数同名守卫测试、第三批 `VariationInspectTool` 的内嵌/外部文件互斥先例）。
评审结论：**可以开工，建议按文档第 10 节分三批：批一 CB-01 + CB-02 + CB-05（9 点标定闭环），批二 CB-03 + CB-07（机器人对位闭环），批三 CB-04 + CB-06（相机标定与畸变校正）。动工前先处理 P1-1 的命名冲突；CB-07 的符号约定（P1-2）必须在写代码前钉死。**

## 1. 总体评价

- 拆分合理：标定过程（采集、拟合）放编辑器助手与服务，运行时只读结果——与前几条线"调试期配置、运行期轻量"的架构一致。
- 职责边界清楚：机器人通信、自动走位、文件部署都归上层，VisionFlow 只做计算与换算，`CalibrationService` 静态无 UI 依赖、上层可直接调用，设计正确。
- CB-01 同时服务 MS-07 单位换算（标定当量来源）与 CB-05/06/07，是本线的正确起点。
- 与现有代码的衔接点已核实：`AffinePointTool` 现有输入/输出/`CalibrationFile`（路径 + 修改时间缓存）结构简单清晰，增强空间大；`VariationInspectTool` 的 `UseEmbeddedModel` / `UseModelFile` 互斥模式可直接借鉴到 `CalibrationSource`（File / Embedded）。

## 2. 代码核对结果

| 计划假设 | 核对结果 |
|---|---|
| `AffinePointTool` 可增强（数组输入、Camera 方式、Matrix 输出） | ✅ `GeometryTools.cs:313-353`：结构简单，`Row` / `Column` 为单值 `[InputRef]`，改 `AcceptsCollection = true` 不影响单值路径；矩阵引用 / 文件两条路径清晰 |
| 标定助手放"图像坐标转世界坐标的编辑窗口" | ⚠️ **该窗口目前不存在**：`AffinePointTool` 在 `WpfToolEditorRouter.cs:107` 落入 `IsVisualPreviewTool`，走通用 `WpfVisualToolEditWindow`。需新建专用窗口（见 P1-5） |
| 新分类"09 标定" | ✅ 现有分类 00~08（08 缺陷检测为第五批新增），编号衔接无冲突 |
| 旧 `write_tuple` 矩阵文件兼容 | ✅ 现有 `AffinePointTool` 按路径读文件，CB-01 的"按内容自动识别"在其加载点前置格式判断即可 |
| 输出/参数同名守卫 | ⚠️ CB-06 有一处冲突（见 P1-1） |

## 3. P1：动工前必须处理

### P1-1 CB-06 参数与输出同名 `PixelSize`（硬性，守卫测试会红灯）

CB-06 的参数 `PixelSize`（校正后每像素物理长度，0 = 自动）与输出 `PixelSize`（实际比例）**同名**，必然触发第二批守卫测试"任何工具的输出名都不与其参数名相同"。**输出改名 `ActualPixelSize`**（参数名保持，与 MS-07 的 `PixelSize` 语义一致）；计划 CB-06 条目同步改。

### P1-2 CB-07 的符号约定必须先用文字钉死（现场 bug 最高发区）

计划只写了 Mode 名称，没写符号约定。写代码前必须在计划中补一节"符号约定"，至少回答：

- `dθ` 的定义与方向：建议 `dθ = 当前角 − 基准角`（图像坐标，顺时针为正，与匹配 `Angle` 一致）；
- `DeltaX` / `DeltaY` 的语义：是"平台需要移动的量"还是"工件相对基准的偏移"？两者符号相反，选定一个并全文统一（建议：输出 = 平台补偿量，即把当前件移回基准所需的运动量）；
- `RotateAroundCenter` 的旋转中心用在哪个坐标系：用物理坐标系中的旋转中心（CB-03 输出 `X` / `Y`），换算路径写清（当前物理坐标 → 绕中心转 −dθ → 与基准物理坐标求差）；
- `CameraMounting` = `Fixed` / `OnAxis` 各自的符号表（固定相机看工件：平台补偿量 = 基准 − 当前；相机随轴动：符号相反）——用两行公式写明，不许只写"决定符号约定"。

验收用例（第 9 节已有"构造已知平移与旋转"）按此约定构造真值，用例即约定的可执行文档。

### P1-3 CB-04 依赖 HALCON 示例图像，先探测路径与文件名

CB-04 验收写"使用 HALCON 安装目录自带的标定板示例图像"。**先探测确认 22.11 示例图像的实际路径与文件名**（标定板图像与 `.cpd` 描述文件都在 `%HALCONROOT%\examples` 下，但具体子路径需实测），用例遵循现有门禁约定：示例缺失时明确失败、不计通过（与 `ZoneInspectTests` 等的 HALCON 用例约定一致）。CB-04/06 两批必须在装有 HALCON 的机器上开发与验收（Copilot 环境满足）。

### P1-4 CB-05 `Camera` 方式的校验双路径

`CalibrationKind = Camera` 而标定文件 `Kind = Affine2D`（或反之）时，`CheckConfiguration` 与运行时都必须给出明确错误（文件名 + 实际 Kind + 期望 Kind）；`image_points_to_world_plane` 的 `scale` 参数语义（物理单位/像素）探测后注释。

### P1-5 标定助手宿主窗口需新建（计划措辞与实际不符）

计划写"放在'图像坐标转世界坐标'工具的编辑窗口中（以选项卡区分）"，但该工具现走通用预览窗口、无专用编辑窗口。**新建 `WpfAffinePointToolEditWindow`**：运行参数页 + CB-02 / CB-03 / CB-04 三个助手页（选项卡），生成结果可"保存为标定文件"或"内嵌到当前工具"；"工具"菜单单独打开 = 打开该窗口的助手页。参照第三批 `WpfGenericShapeMatchEditWindow` 的先例。测试运行必须经 `ToolTestRun`。

## 4. P2：开发中落实

1. **CB-01 服务形态**：`VisionFlow.Tools\Calibration\CalibrationService` 静态类、纯函数式（输入值类型/元组，输出结果对象），不依赖 FlowContext，便于上层直接调用与单测；`.vfcal.json` 读写与旧 `write_tuple` 自动识别（JSON 以 `{` 开头）放服务的 `Load`。
2. **内嵌 / 文件互斥**：`CalibrationSource` = `File`（默认）/ `Embedded`；借鉴第五批 `UseEmbeddedModel` / `UseModelFile` 模式——切换来源时清空另一侧，校验层兜手工改文件的情况。内嵌 JSON 的中文按 `\uXXXX` 转义是既有序列化行为，文档注明。
3. **CB-06 的映射缓存键**：`gen_image_to_world_plane_map` 的缓存键 = 相机参数 + `PixelSize` + 输出区域 + 插值方式，任一变化重新生成；`IToolResourceLifecycle.Prepare` 预热生成映射。
4. **CB-02 的"取当前值"**：从上次运行变量取图像坐标时，变量为数组要给出明确行为（取第 0 个并提示，或要求单值——建议后者，配置错误早暴露）。
5. **登记与分类**：新分类 `09 标定`；`AffinePointTool` 移分类不改 ID / 类型名；`BuiltinToolIdentities` / `ToolboxRegistry` / `WpfToolEditorRouter` / `ToolIcons.xaml` / README 工具表新行。
6. **枚举与默认值**：`CalibrationKind`（`Affine2D` 默认）/ `CalibrationSource`（`File` 默认）/ CB-07 `Mode`（`RotateAroundCenter` 默认）/ `CameraMounting` / `AngleUnit` 按数字保存、追加在末尾。
7. **守卫测试保持绿**：输出名不与参数名相同（P1-1 处理后）。

## 5. P3：文档维护

1. 计划头部状态按批更新；CB-06 / CB-07 条目分别补 P1-1 / P1-2 的最终结论。
2. README 工具表新增"09 标定"行；外部文件约定处补 `.vfcal.json`。
3. 探测结论（CB-04 示例图像路径、`image_points_to_world_plane` 的 scale、`gen_image_to_world_plane_map` 参数行为）记入计划新一节，沿用第 12 节模式。
4. **MS-07 收尾小项**：CB-01 落地后，`ScaleSource` 增加 `Calibration` 来源（读 `.vfcal.json` 或引用矩阵，MS-07 条目已预留）——单独立项，属标定线收尾，不并入本线三批。

## 6. 开工顺序确认

- **批一：CB-01 + CB-02 + CB-05**——9 点标定闭环，最常用的现场需求；含新窗口（P1-5）与文件格式，体量最大但风险分散。
- **批二：CB-03 + CB-07**——旋转中心 + 纠偏计算，机器人对位闭环；P1-2 符号约定是这批的前置。
- **批三：CB-04 + CB-06**——相机标定 + 畸变校正；HALCON 依赖最重（示例图像、标定板描述文件），放最后。

三批各自的 HALCON 探测在批前进行；每批完成后按惯例评审。
