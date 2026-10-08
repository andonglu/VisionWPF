# PROJECT-REVIEW 整改进度记录

起始日期：2026-09-15
依据文档：`docs\PROJECT-REVIEW.md`
实施原则：按评审文档第 4 节的三阶段顺序推进；局部、可回退的改动；每项修复同步维护运行时、序列化、引用元数据和编辑界面。

范围调整：使用方已明确不需要撤销/重做，项目不提供该功能，也不再将其列为待办或验收缺口。参数取消、脏标记、保存失败保护继续保留。

阅读说明：下方第一轮变更日志是实施过程记录，测试数量和环境说明不代表当前状态；当前结果以总览和最后的复核修复记录为准。

## 状态图例

- 未开始 / 进行中 / 已完成 / 部分完成（说明范围）/ 暂缓（说明原因）

## 总览

| 编号 | 优先级 | 状态 | 说明 |
|---|---|---|---|
| VF-01 | P0 | 已完成 | 本轮收紧标量/集合类型，矩阵集合显式声明，补齐循环公共输出与外部数组作用域 |
| VF-02 | P0 | 已完成 | 见变更日志 2026-09-15 |
| VF-03 | P0 | 已完成 | 六种跟随工具共用矩阵预检，拒绝空元素及无效矩阵；降级权限仅属于显式预览上下文 |
| VF-04 | P1 | 已完成（本轮范围） | 独立会话资源登记，覆盖旧输出仍回收，预览按底层对象识别借用与别名；持续运行指标仍由 VF-11 跟进 |
| VF-05 | P1 | 已完成（本轮范围） | 必填 null 输入拒绝，日志快照与预览可变集合隔离；自定义引用类型结果遵循只读或 ICloneable 约定 |
| VF-06 | P1 | 已完成（本轮范围） | 46 项内置工具固定 ID、历史身份别名、冲突拒绝及冷启动加载；外部路径迁移等已知边界保留 |
| VF-07 | P1 | 已完成（代码接入） | 注册分发与编辑事务接通，两个历史编辑器契约类型前转通过；第三方插件实际弹窗仍需宿主交互验收 |
| VF-08 | P2 | 已完成 | MainWindow 职责拆分完成：命名/参数面板/编辑器路由/运行会话/文档控制器/显示叠加/显示设置平移到独立类，MainWindow 由约 1830 行降至约 950 行，新增 31 项单测 |
| VF-09 | P2 | 部分完成 | WinForms 双 UI 层（VisionFlow.App、VisionFlow.ToolEditors）已移除，仅保留 WPF；LD 插件与内置工具收敛仍暂缓（工具身份兼容风险），VF-06 的 ToolId 机制已为此铺路 |
| VF-10 | P2 | 已完成（当前需求范围） | 独立编辑副本、原子提交、取消不置脏、无变更确认不置脏，保存失败保留文档与原文件 |
| VF-11 | P1 | 部分完成 | 当前 192 项自动回归通过；补齐独立原生环境门禁、样例资源释放、可配置持续回归及 CSV 指标。两个样例各 1000 次运行完成；现场图像、实际 UI 和正式长期验收仍待补充，见 `docs\VF11-ACCEPTANCE.md` |

## 变更日志

### 2026-09-15 VF-01：统一引用解析、类型判断与作用域规则（已完成）

改动文件：

- `VisionFlow.Base/Editing/RefCandidates.cs`
  - 修复 `CollectUpstream`：目标在 IfElse 内时只收集所在分支的上游，互斥分支私有输出不再可见；
    目标在 ForLoop 外时不再收集循环体内部输出（零次执行时这些变量不存在）。
  - 修复 `CollectSubtreeOutputs`：嵌套 IfElse 只暴露显式公共输出，不再递归分支内部；不再递归循环体。
  - 新增 `RefScope` / `RefLoopMode`：候选 + 循环上下文的完整作用域，`ScopeForNode` / `ScopeForBranchOutput`。
  - `Input.Image` 外部输入候选移入候选服务（`AddExternalInputs`），编辑器下拉与校验器同源。
- `VisionFlow.Base/Editing/ReferenceSemantics.cs`（新增）
  - `ReferenceSemantics.Check`：与 `VariableReference` 运行时同一套规则，按“输出声明 → 数组下标 → 成员链”
    逐段推导类型，取代候选字符串精确匹配。
  - 静态错误（变量不存在、非数组下标、已知类型上不存在成员、Count 循环使用 Loop.Current、循环外使用 Loop.*）
    直接报出；数组长度、null 值、object 上的成员链留待运行期。
  - 数组整体引用（无下标）按元素类型数组推导，类型兼容同时接受元素类型与数组类型（与运行时工具行为一致）。
- `VisionFlow.Base/Validation/FlowValidator.cs`
  - `ValidateReference` / `ValidateBranchOutputOperand` 改用 `ReferenceSemantics.Check`；删除私有 `AddInputImage`。
  - 新增 `IsTypeCompatible`：推导类型未知（object）时不误拒。
- `VisionFlow.Tests/`（新增 xunit 工程，已加入 `VisionFlow.slnx`）
  - `ReferenceValidationTests.cs`：22 例覆盖下标/成员链/类型/分支作用域/循环作用域/嵌套组合。
  - `ExampleFlowTests.cs`：2 例，examples 下已发布流程文件仍可加载并通过校验。

行为变化（有意决策，已固化到测试）：

- 循环体内部工具的输出对循环外不可见（此前可见但零次执行时运行期必失败）。
- Else 分支不再能引用 If 分支私有输出（此前校验放行、运行期必失败）。
- `Source.Values[0]`、`Source.Values.Count`、`Source.Items[0].Score` 等引用不再被误拒。

验证：`dotnet test VisionFlow.Tests` 24/24 通过。

### 2026-09-15 VF-03：定位配置失效时不得隐式固定位置测量（已完成）

改动文件：

- `VisionFlow.Tools/Tools/MeasureTools.cs`
  - 删除死代码 `GetMatrixOrNull`（无调用点）；`GetMatrices` 改为 `TryGetMatrices(ctx, out matrices, out error)`。
  - 未配置 `MatrixPath`：固定位置测量（保留原有行为）。
  - 已配置但引用不存在 / 类型错误 / 矩阵集合为空：明确失败，错误信息含模块名、引用路径与原因。
  - 新增显式预览降级开关 `AllowMatrixFallback`（默认 false）：开启后退回固定位置并记录 Warning。
  - 5 个跟随测量工具（直线/矩形/圆/一维卡尺/圆弧卡尺）统一改为先解析矩阵再创建 HALCON 资源，失败时不产生任何输出。
- `VisionFlow.Tools/Tools/HalconTools.cs`
  - `EllipseFollowMeasureTool`（不继承 FollowMeasureToolBase）存在同类回退，一并改为明确失败 + `AllowMatrixFallback` 开关。
- `VisionFlow.Tests/FollowMeasureMatrixTests.cs`（新增 7 例，覆盖未配置/单矩阵/集合/引用不存在/类型错误/空集合/显式降级）。

行为变化：配置错误的定位跟随不再输出“看似成功的固定位置结果”，节点失败并阻断流程。
已知影响：专用编辑器（WPF/WinForms）预览路径未自动开启降级，预览时同样会看到明确错误（有意为之，便于暴露配置问题）。

验证：`dotnet test VisionFlow.Tests` 31/31 通过。

### 2026-09-15 VF-02：隔离运行状态与编辑状态（已完成）

改动文件：

- `VisionFlow.WpfApp/MainWindow.xaml`：为工具栏按钮（新建/打开/保存/另存/打开图像/校验/运行）与上移/下移/删除按钮命名。
- `VisionFlow.WpfApp/MainWindow.xaml.cs`
  - 新增 `IsRunning` / `EnsureEditable(action)` 守卫；`SetRunningState` 统一禁用全部编辑入口
    （工具箱、参数面板、树编辑按钮、文件操作、换图、校验、运行），保留停止与只读查看。
  - 代码级兜底守卫：节点增删移动、双击打开编辑器、换图、新建/打开/保存流程、参数提交回调
    （文本/数字/下拉/勾选/按钮）在运行期间一律拒绝并提示。
  - 输入图像隔离：运行前 `CopyObj` 生成 `_runImage` 副本注入上下文，换图释放 `_inputImage`
    不再影响本次运行及 `_lastRunContext` 的结果显示；副本在下次运行前、新建流程或窗口关闭时释放。
  - 新增 `OnClosing`：运行期间禁止关闭窗口；`OnClosed` 释放 `_runImage` 与 `_inputImage`。
  - 取消语义保持协作式：HALCON 同步算子返回前不中断；`await` 结构保证恢复编辑发生在后台实际结束之后。

验证：`dotnet build VisionFlow.WpfApp` 0 错误 0 警告。UI 交互行为需在 WPF 环境手工验证。

### 2026-09-15 VF-05：完善外部输入与运行结果契约（已完成）

改动文件：

- `VisionFlow.Base/Editing/ExternalInputs.cs`（新增）：`ExternalInputDef`（路径/类型/必填/说明）+
  `ExternalInputRegistry`（默认注册 Input.Image，上层可注册 Input.PartId 等）。
- `VisionFlow.Base/Editing/RefCandidates.cs`：`AddExternalInputs` 改为枚举注册表，编辑器候选与校验器同源。
- `VisionFlow.Base/Engine/FlowEngine.cs`
  - 运行前必填外部输入检查：缺失或类型不符明确失败（`FlowErrorCodes.InputMissing`）。
  - `FlowRunResult` 的 `NodeReports` 改为快照（`ToList()`），新增 `Log` / `StructuredLogs` / `Trace` 快照；
    复用同一上下文再运行不再污染前一次结果。
  - 注释明确“执行成功 ≠ 业务判定 OK”以及上下文复用约定（变量保留、日志追加）。
- `VisionFlow.Base/Core/FlowContext.cs`：新增 `CreatePreviewContext()`——变量浅复制、记录独立。
- `VisionFlow.WpfToolEditors/.../WpfFollowMeasureToolEditWindow.xaml.cs` 与
  `VisionFlow.ToolEditors/Ui/FrmFollowMeasureToolEdit.cs`：工具预览改用派生上下文，不再污染主窗口上次运行结果。
- `VisionFlow.Tests/ExternalInputAndResultTests.cs`（新增 7 例）。

验证：`dotnet test VisionFlow.Tests` 38/38 通过。

### 2026-09-15 VF-06：流程文件版本与兼容机制（已完成）

改动文件：

- `VisionFlow.Base/Runtime/FlowSerializer.cs`
  - 文件携带 `FormatVersion`（当前 1）；高于支持版本明确 `NotSupportedException`。
  - `ToolDto` 新增 `ToolId`（`ToolboxToolAttribute.Id` 或类型全名）；加载优先按 ToolId 解析
    （按需扫描已加载程序集建立映射，宿主也可提前 `RegisterToolType`），失败退回旧版 `TypeName`。
  - 未知工具报错包含 ToolId 与 TypeName；文件中多余参数产生警告而非静默忽略。
  - `Load`/`LoadNode` 新增可选 `warnings` 参数；枚举值无法识别时警告并回退默认值。
- `VisionFlow.WpfApp/MainWindow.xaml.cs`：加载流程时弹出兼容性警告。
- `VisionFlow.Tests/FlowSerializerCompatTests.cs`（新增 7 例，含旧版文件兼容、ToolId 解析、版本拒绝、警告）。

验证：`dotnet test VisionFlow.Tests` 45/45 通过。

已知边界（未在本轮处理，见评审 VF-06 建议 5）：常量操作数 double 写成整数形式加载后变 int 的
启发式问题、外部文件路径迁移规则，留待后续格式版本升级时处理。

### 2026-09-15 VF-07：打通 WPF 插件编辑器的注册与打开链路（已完成）

改动文件：

- `VisionFlow.EditorCore/Ui/ToolEditContext.cs`（新增）：`ToolEditContext` 与 `ToolEditorAttribute`
  由 ToolEditors（WinForms）迁入 EditorCore——二者本身平台无关；命名空间保持 `VisionFlow.Ui` 不变，
  调用方无需修改。
- `VisionFlow.EditorCore/Editing/VisionFlowPluginLoader.cs`
  - 移除对 WinForms `ToolEditPageRegistry` 的直接依赖；新增 `EditorRegistrars`（`Func<Type, bool>` 列表），
    编辑器注册由宿主按平台接入。
  - 插件程序集中的所有 ToolBase 派生类同时注册持久化身份（`FlowSerializer.RegisterToolType`，配合 VF-06）。
- `VisionFlow.EditorCore/VisionFlow.EditorCore.csproj`：移除 ToolEditors 引用与 `UseWindowsForms`，
  TFM 降为 net9.0；补 halcondotnet 引用（ToolEditContext 使用 HObject）。
- `VisionFlow.ToolEditors/VisionFlow.ToolEditors.csproj`：改为引用 EditorCore（依赖方向翻转）；
  `ToolEditPageRegistry.cs` 移除已迁出的 `ToolEditContext`。
- `VisionFlow.WpfToolEditors/WpfToolEditorRegistry.cs`（新增）：WPF 侧编辑器注册表，
  支持 [ToolEditor] + Window 派生类自动注册、构造函数解析、后注册覆盖先注册；
  非 Window 编辑器（如 WinForms Form）记录到 `Diagnostics` 而不是静默忽略。
- `VisionFlow.WpfApp/MainWindow.xaml.cs`：`BootstrapEditor` 接入 `WpfToolEditorRegistry.RegisterEditor`；
  `OpenToolEditor` 改为注册表优先分发，未命中再走内置类型判断，最后回退通用编辑器；
  启动提示展示插件编辑器数量与注册诊断条数。
- `VisionFlow.App/Ui/FlowEditorForm.cs`：WinForms 宿主接入 `ToolEditPageRegistry.RegisterEditor`。

优先级约定：注册表（插件可覆盖）→ 内置编辑器 → 通用编辑器回退。
已知行为：LDWelding 插件编辑器是 WinForms Form，WPF 端会在 Diagnostics 记录“不可用”提示，
WinForms 端照常可用（评审建议 5 的兼容要求）。

验证：`dotnet build VisionFlow.slnx` 0 错误 0 警告；`dotnet test VisionFlow.Tests` 45/45 通过。
插件编辑器实际打开链路需在 WPF 环境手工验证。


### 2026-09-15 VF-04：HALCON 对象生命周期协议基元（部分完成）

改动文件：

- `VisionFlow.Base/Variables/HalconTypes.cs`
  - `HalconImage` / `HalconRegion` / `HalconXld` 三个包装类新增所有权协议：
    `OwnsObject`（默认 false＝借用语义，不改变现有行为）、`Owned()` 工厂、`Dispose` 幂等且只释放自有对象。
- `VisionFlow.Base/Core/FlowContext.cs`
  - `FlowContext` 实现 `IDisposable`：变量表中按引用去重（`ReferenceEqualityComparer`）后只释放 `OwnsObject` 的对象；
    同一底层对象被多个别名引用时只释放一次。
- `VisionFlow.Tools/Tools/HalconTools.cs`
  - `HalconModelMatchTool.Run` 增加模板加载耗时日志，便于定位运行期性能瓶颈。
- `VisionFlow.Tests/HalconOwnershipTests.cs`（新增 5 例，仅使用未初始化 HObject，不依赖 HALCON 原生运行库）。

未做部分及原因：运行路径的释放接线（工具输出谁负责释放、上下文结束时统一回收）未启用。
本环境 `lib/halcon` 只有托管 halcondotnet.dll、没有 HALCON 原生运行库，无法验证“对象仍在界面显示时被释放”
的风险；协议基元已就位，接线需在有完整运行库的环境验证后开启。

验证：`dotnet test VisionFlow.Tests` 50/50 通过。

### 2026-09-15 VF-10：脏标记与未保存变更管理（第一轮）

改动文件：

- `VisionFlow.EditorCore/Editing/FlowEditModel.cs`
  - 新增 `IsDirty` / `MarkDirty()` / `MarkSaved()` / `StructureChanged` 事件；
    结构编辑（`InsertToolboxNode`、`RemoveNode`、`InsertExistingNode`、两个 `MoveNode` 重载）
    统一走 `OnStructureChanged()` 置脏并通知；失败的操作（删根、越界移动等）不置脏。
  - `ReplaceRoot`（新建/打开）重置为未脏。
- `VisionFlow.WpfApp/MainWindow.xaml.cs`
  - 构造函数订阅 `StructureChanged` 刷新标题；新增 `MarkDirtyFromUi()` 统一置脏 + 刷新标题。
  - 参数提交全部置脏：文本（`CommitText`）、数字（`AddNumberRow`）、下拉（`AddComboRow`）、
    勾选（`AddScalarRow` bool）、按钮行（`AddButtonRow`）。
  - `OpenToolEditor` 的 7 个编辑器分支在 `ShowDialog() == true` 后置脏。
  - `SaveCurrentFlow` 保存成功后 `MarkSaved()`。
  - `ConfirmCloseCurrentFlow` 重写：无修改直接放行；有修改时 YesNoCancel
    （是＝保存后关闭，保存失败中止；否＝放弃修改；取消＝中止）。
    新建/打开流程沿用该确认；`OnClosing` 增加同一确认，关窗前不再静默丢弃修改。
  - `UpdateWindowTitle`：有未保存修改时标题加 `*` 前缀。
- `VisionFlow.Tests/VisionFlow.Tests.csproj`：新增 EditorCore 引用。
- `VisionFlow.Tests/FlowEditModelDirtyTests.cs`（新增 11 例：增/删/两种移动/插入置脏，
  失败操作不置脏，MarkSaved/ReplaceRoot 清除，手动 MarkDirty）。

范围说明：仅记录第一轮的脏标记、关闭确认和标题提示实现。后续复核发现的参数取消与保存失败问题见本轮修复记录；编辑历史不在使用方要求范围内。

验证：`dotnet test VisionFlow.Tests` 61/61 通过；`dotnet build VisionFlow.WpfApp` 0 错误 0 警告。
标题脏标记与关窗确认的交互需在 WPF 环境手工验证。

### 暂缓项说明（VF-08 / VF-09 / VF-11 未完部分）

- VF-08（MainWindow 职责拆分）：MainWindow.xaml.cs 约 1900 行，拆分为视图模型/服务属大重构，
  建议在 P0/P1 全部落地、回归网足够厚之后单独立项进行。EditorCore 的 WinForms 依赖已随 VF-07 切除。
- VF-09（LD 插件与内置工具收敛）：两侧逐行重复（含枚举与辅助类），合并需要统一工具身份并保持
  既有流程文件可加载；VF-06 的 ToolId 稳定标识已为此铺路，但收敛本身需专项设计与兼容测试，暂缓。
- VF-11（现场图像基线）：本机 HALCON 原生运行库可用，已有示例图像和资源回收用例；
  尚需现场图像、工况容差和持续运行数据，不能以示例通过代替工业效果验收。

## 2026-09-15 复核后的修复记录（当前实现）

本轮处理复核报告中的具体缺陷，没有扩展为主窗口整体 MVVM 重写或内置/LD 插件算法合并。

### 引用、外部输入与定位测量

- `InputRefAttribute` / `ToolInputRefDef` 新增 `AcceptsCollection`，默认不允许把集合当成标量。
- 矩阵输入显式接受集合，循环次数、结果序号等保持标量；WPF、WinForms 主面板及专用页面同步候选过滤。
- 分支公共输出保留所在的最内层循环作用域；外部数组可用于下标、长度与集合循环。
- `FollowMatrixResolver` 在任何测量开始前检查整个集合：拒绝 null、错误类型、非六元素、非数值、非有限值、不可逆或数值溢出的矩阵。
- 椭圆与其他五种跟随测量共用该规则。未配置定位时保留固定测量；已配置但失效时正式运行失败。
- 删除工具属性 `AllowMatrixFallback`。旧 JSON 中该属性警告忽略，重存不再写入；只有 `CreatePreviewContext(allowMatrixFallback: true)` 可以授予本次预览降级权限。
- 必填外部输入即使变量存在，值为 null 时也明确失败。

### 资源会话与预览隔离

- `FlowContext` 独立登记拥有与借用的 HALCON 对象，不再仅在 Dispose 时查看当前变量表。
- 被同名输出覆盖的旧资源保留到会话结束统一释放，避免遗失，也避免其他别名仍引用时提前释放。
- 派生预览借用源上下文的底层对象；改名、别名或集合包装都不会使预览取得源资源所有权。
- `ToolBase.SetOutput` 不会把已知借用输入误转成自有资源，包装集合内的新产物也纳入登记。
- 预览复制数组、可变列表与字典，保留集合别名关系和字典比较器；日志、进度回调和取消状态与主运行隔离。
- 普通自定义引用类型结果不进行任意反射深拷贝，应保持只读或实现 `ICloneable`。当前测量仅追加新结果，复制结果列表即可避免污染主运行。
- 源上下文必须晚于借用它的预览释放；已释放上下文拒绝新写入，但仍允许读取结果元数据。

### 参数提交与文件保护

- WPF 与 WinForms 的工具编辑入口共用 `ToolEditTransaction`，包括专用页面、插件页面和通用回退。
- 编辑工作副本独立复制持久化配置及模型 `byte[]`，上下文保留目标的上游和嵌套引用作用域。
- 确认时先在新实例上完成全部参数复制与校验，再原子替换 `ToolNode.Tool`；取消、关闭、预览失败或属性校验失败不修改原工具。
- 参数完全相同的确认不替换工具、不置脏。参数面板的“打开专用编辑窗体”按钮不再自动置脏，文本/下拉失焦未改变值时不重复提交。
- `FlowFileSaveService` 先序列化，再写同目录临时文件并刷新到磁盘，最后原子替换目标。
- 空序列化结果、只读、独占、目录或权限错误均保留原文件；WPF 保存返回 false，保留当前文档和脏状态，关闭操作中止。
- 临时文件清理失败会包含在错误信息中，不再静默丢弃。
- 编辑历史不在需求范围内，未添加任何相关命令、按钮或历史栈。

### 稳定工具身份与旧插件

- `BuiltinToolIdentities` 登记 46 项与工具箱一致的固定 ID，不依赖编辑器启动。
- `FlowSerializer.RegisterToolType(type, stableId, aliases)` 支持旧完整类型名、程序集限定名及历史 ID。
- 重命名迁移通过显式别名完成；冲突原子拒绝，自动扫描不会覆盖已有稳定身份。
- 工具模块初始化在扫描前完成，独立加载 DLL 后首次按固定 ID 读取也可工作。
- `SaveNode` 同样保存 `FormatVersion`。
- 原 `VisionFlow.ToolEditors` 程序集增加 `ToolEditContext`、`ToolEditorAttribute` 的类型前转，保留旧插件引用的程序集级类型身份。

### 上一轮验证记录

从仓库根目录执行：

```powershell
dotnet test .\VisionFlow.Tests\VisionFlow.Tests.csproj --configuration Debug --no-restore --nologo --verbosity minimal
dotnet build .\VisionFlow.slnx --configuration Debug --no-restore --nologo --verbosity quiet
.\VisionFlow.Tests\VerifyToolEditorTypeForwarding.ps1 -NoBuild
```

结果：170 项测试通过，0 失败、0 跳过；整个解决方案构建 0 警告、0 错误；两个旧完整程序集限定类型名均成功转发到 `VisionFlow.EditorCore`。

覆盖包含原报告边界复现、实际测量输入消费、HALCON 资源和示例图像、独立加载上下文中的冷启动身份迁移、编辑配置事务与文件保存失败。

上述结果不等于第三方旧插件窗口已实际打开，也不替代 WPF/WinForms 按钮、示教、连续预览和现场长期运行的手工验收。

## 2026-09-15 VF-11 补充修复与当前验收记录

使用方确认本轮先处理 VF-11，VF-08 / VF-09 保留后续处理；没有启动主窗口重构或 LD 插件合并。

### 已实现

- 原生用例统一调用 `HalconRuntimeAvailabilityTests.RequireAvailable()`；环境缺失时独立失败，筛选运行也不会因早退而误报通过。
- 收紧环境探针的异常类型，释放探针中的 `HTuple`；新增独立进程缺失环境探针，不改动已安装 HALCON 或系统配置。
- `ExampleImageBaseline` 统一两个样例的加载与数值断言，修复前两个图像基线用例遗漏调用方输入释放的问题；结果消费失败同样清理输入。
- 资源生命周期用例增加异常路径释放，避免失败用例遗留资源影响后续观察。
- “已标定容差”统一理解为“已建立样例回归基线并设置容差”，不将约 ±5% 的样例容差等同于业务精度。
- 新增 `ContinuousRunTests`：默认每个样例预热 10 次、正式运行 100 次；次数、采样间隔、报告目录及内存/句柄/P95 阈值可配置。
- CSV 流式记录环境、图像/流程摘要、采样曲线与汇总；正式配置的阈值超限即失败。缺少阈值时明确标记为功能回归，也可要求三个阈值完整后才能运行。
- 新增参数解析和阈值边界用例，非法配置明确失败；没有引入新的测试框架或依赖。
- 新增 `docs\VF11-ACCEPTANCE.md`，提供命令、指标口径、UI 操作清单和现场/长期运行记录模板；结果目录已加入 git 忽略规则。

### 本轮执行结果

正常全套回归：**192 通过、0 失败、0 跳过**。报告：`TestResults\vf11-regression\regression.trx`。

独立进程模拟原生库缺失：4 个实际用例方法均抛出预期的环境门禁断言。
该结果不计入正常用例通过数量，记录在 `TestResults\vf11-regression\missing-native.txt`。

额外运行两个样例各 1000 次，预热各 10 次，每 100 次采样；未设置性能阈值，仅记录功能回归及观察数据：

| 样例 | 正式次数 | 总经过时间 ms | 采样私有内存峰值增量 MiB | 句柄峰值增量 | 每轮 P95 ms |
|---|---|---|---|---|---|
| threshold-region | 1000 | 887.495 | 1.047 | 0 | 1.072 |
| xld-line | 1000 | 13534.137 | 6.598 | 2 | 15.868 |

收尾 GC 后私有内存相对基线增量分别为 1.047 MiB、4.625 MiB。单次观察不能区分缓存增长与长期泄漏，不据此宣称资源零增长。
环境：Windows 10.0.26200、x64、.NET 9.0.20、HALCON 20.11；实际 UTC 时间与图像/流程 SHA256 见 CSV。
报告：`TestResults\vf11-1000\soak.trx` 及同目录两个 CSV。

另做阈值负向验证：人为设置极小 P95 上限，两个持续运行用例均明确失败，原因均为 P95 超限，而非环境或流程错误。
这是门禁验证的预期失败，记录在 `TestResults\vf11-negative-control\expected-failure.trx`，不能与正常回归结果混淆。

### 仍未验收

上述运行仅持续数秒到十余秒，不构成长期稳定性或设备节拍验收。现场样本与真值、业务精度、WPF/WinForms 实际交互、
真实旧插件弹窗和满足事先约定持续时间的长期运行均未执行，验收模板保持“待验”。
因此 VF-11 仍为“部分完成”；不是缺少执行工具，而是缺少这些真实环境证据。

### 2026-09-15 移除 WinForms 程序，WPF 样式对齐 EquipmentShell

背景：使用方明确要求去掉 WinForms 程序只保留 WPF，并将 WPF 视觉风格对齐 `EquipmentShell` 项目（SemightBlue 浅色主题）。VF-09 的"双 UI 重复"问题随 WinForms 层的移除而消解。

改动内容：

- 删除 `VisionFlow.App`（WinForms 宿主）与 `VisionFlow.ToolEditors`（WinForms 工具编辑器，含 ToolEditContext/ToolEditorAttribute 类型前转兼容层），并从 `VisionFlow.slnx` 移除两个工程条目。
- `VisionFlow.WpfApp`、`VisionFlow.WpfToolEditors` 的 csproj 删除对 ToolEditors 的残留引用。
- `VisionFlow.LDWeldingPlugins`：删除 WinForms 编辑器 `LDWeldingPluginEditor.cs/.Designer.cs/.resx` 及 ToolEditors 引用，不再启用 UseWindowsForms；插件只复制到 WPF 宿主输出目录。插件工具在 WPF 端回退到通用编辑器。
- 保留 `VisionFlow.Controls`（HalconImageView、RoiEditorControl 等 WinForms 控件库）：WPF 主窗口与 WPF 工具编辑器通过 `WindowsFormsHost` 嵌入它承担图像显示与 ROI 编辑，删除等于重写显示层，不在本次范围内。WPF 工程保留 `UseWindowsForms=true` 仅为承载 WindowsFormsHost。
- 删除已失效的 `VisionFlow.Tests/VerifyToolEditorTypeForwarding.ps1`（类型前转程序集已不存在）。
- 样式：`VisionFlow.WpfApp/Themes/Tokens.xaml` 全部语义 key 名保持不变，仅改值——主色 #3366CC（hover #5F94FF、深色 #105186）、页面底 #F0F5FA、文字 #00133B/#8000133B、状态色 #19914B/#E08A00/#D83340、禁用 #858E9F、字体 Microsoft YaHei UI 优先；卡片圆角 8→10，阴影对齐 CardShadowEffect（BlurRadius 8、Direction 270、Opacity 0.18、ShadowDepth 2、Color #00133B）。
- `MainWindow.xaml`：顶栏改为 #F7FBFF→#FFFFFF 垂直渐变并加轻投影，标题文字用主色；其余业务 XAML 未动。
- 未引入 HandyControl/Prism 等新依赖，仅取 EquipmentShell 的视觉参数。
- 同步 README.md、`.github/copilot-instructions.md`、`VisionFlow.WpfToolEditors/WPF_EDITOR_DESIGN.md` 中的 WinForms 相关描述。

验证：`dotnet build VisionFlow.slnx` 0 错误 0 警告；`dotnet test VisionFlow.Tests` 192 通过、0 失败、0 跳过。WPF 界面实际视觉效果需人工运行 `VisionFlow.WpfApp` 确认。

### 2026-09-15 引入 HandyControl，样式资源与 EquipmentShell 彻底对齐

背景：使用方要求不止对齐色值，而是样式资源与 EquipmentShell 项目保持一致，允许引入 HandyControl。

改动内容：

- `VisionFlow.WpfApp` 新增 `HandyControl 3.5.1` PackageReference（与 EquipmentShell.UI 同版本）。
- 新增 `VisionFlow.WpfApp/Themes/Generic.xaml`，与 `EquipmentShell.UI/Themes/Generic.xaml` 合并结构一致：HandyControl `SkinDefault` + `Theme` + 12 个样式字典。
- 12 个样式文件（Basic/Button/TextBlock/RadioButton/TextBox/ComboBox/DataGrid/TabControl/ToggleButton/ListBox/CheckBox/ProgressBar）原样移植到 `VisionFlow.WpfApp/Themes/Styles/`，仅改写组件路径；`Basic.xaml` 移除 EquipmentShell 私有的 `MessageLevelBrushConverter`（依赖其 Core 日志枚举），`BooleanToVisibilityConverter` 改用 WPF 内置实现。
- `App.xaml` 现在依次合并 `Themes/Tokens.xaml`（VisionFlow 语义 token 与自有具名样式）与 `Themes/Generic.xaml`（EquipmentShell 主题）。
- `Tokens.xaml` 删除与 HandyControl/EquipmentShell 默认样式重复的隐式样式（Window、Button、TextBox、ComboBox、ComboBoxItem、DataGrid、DataGridColumnHeader、DataGridCell、GroupBox、ListBox），默认控件外观由 HandyControl + EquipmentShell 样式接管；`AccentButtonStyle` 去掉对隐式 Button 的 BasedOn 并补齐自给 Setter。保留 VisionFlow 自有具名样式（PanelBorderStyle、EditorCardStyle、ToolBarIconButtonStyle、ToolTileButtonStyle、ThinScrollBar 等）与全部语义色 key。
- `MainWindow.xaml` 顶栏改用 EquipmentShell 资源 `ToolBarBackgroundBrush`（#F7FBFF→#FFFFFF 渐变）与 `ToolBarShadowEffect`，标题文字用主色 #3366CC。

验证：`dotnet restore` + 全量 build 0 错误 0 警告；`dotnet test` 192 通过；`dotnet run` 启动后主窗口正常加载（资源合并链无解析错误）。实际视觉效果需人工对照 EquipmentShell 确认。

### 2026-09-15 移除 LDWeldingPlugins 插件工程，一维码转为内置工具

背景：LD 插件 22 个工具中 21 个与内置工具同名重复（VF-09 指出的重复实现），且全仓库无任何流程文件引用 `ldwelding.*` 工具 ID。使用方决定删除整个插件工程，只保留缺失的功能。

改动内容：

- 一维码识别移植为内置工具 `Barcode1DTool`（`VisionFlow.Tools/Tools/RecognitionTools.cs`），实现与接口（输入 ImagePath/RegionPath/CodeType，输出 Codes/FirstCode/Region/Count）与原 `LDBarcode1DTool` 一致。
- 固定工具 ID 登记为 `barcode1d`，并保留历史身份别名 `ldwelding.barcode1d`（`BuiltinToolIdentities`），旧插件保存的流程文件仍可加载；工具箱新增"06 识别工具 / 一维码"条目。
- 删除 `VisionFlow.LDWeldingPlugins` 整个工程及 slnx 条目。其余 21 个重复工具（图像处理/区域/XLD/几何）随之移除，VF-09 的"内置与插件重复实现"问题清零。
- 插件加载机制（`VisionFlowPluginLoader`、`[ToolboxTool]` 特性、`plugins` 目录扫描）保留，供第三方插件使用。
- README 与 `.github/copilot-instructions.md` 同步：内置工具表新增"识别工具：一维码"，LDWelding 插件段落改为通用插件机制说明。

验证：全量 build 0 错误；`dotnet test` 192 通过（含内置工具 ID 唯一性枚举与工具箱一致性用例，自动覆盖新工具）。

### 2026-09-16 工具箱 51 个图标全部换新

背景：旧图标约 25 种图案覆盖 51 个工具，大量共用（6 个阈值工具同图、通道三工具同图等）。使用方按设计需求提供了全套 24x24 SVG（`tool-{工具ID}.svg` 命名，SemightBlue 色板，每工具唯一图案）。

改动内容：

- 新增 `tools/svg_to_tool_icons.py`：把 SVG 转成 WPF 资源字典，支持 path/rect/line/circle/ellipse/polygon/polyline/g(rotate)/text 元素；`fill-opacity` 转 ARGB，dasharray 按线宽换算，主题色映射为 DynamicResource 画刷，便于以后重新生成。
- 生成 `VisionFlow.WpfApp/Themes/ToolIcons.xaml`：51 个 `ToolIcon.{工具ID}` 资源（`x:Shared="False"` 的 Viewbox+Canvas），外加插件工具回退 `ToolIcon._default`；在 `App.xaml` 合并。
- `MainWindow.xaml.cs`：`CreateToolButton` 改为按工具 ID 精确查找图标资源（取代原来的关键词匹配），删除 `GetToolIconData`/`GetToolIconFill`/`GetToolIconStroke` 三个方法；未登记图标的插件工具走通用回退图标。

验证：全量 build 0 错误；`dotnet test` 192 通过；`dotnet run` 启动正常、工具箱图标资源无解析错误。每个图标的实际观感以界面为准。

### 2026-09-16 合并阈值与形态学工具

背景：使用方要求 6 个阈值工具合并为一个（工具内选择分割方式），矩形/圆形形态学合并为一个（工具内选择结构元素形状）。

改动内容：

- `ThresholdTool` 改为非 sealed 的合并工具，新增 `SegmentMethod` 枚举参数（Threshold/AutoThreshold/BinaryThreshold/FastThreshold/CharThreshold/VarThreshold），参数并集保留（MinGray/MaxGray/MinSize/Sigma/Percent/BinaryMethod/LightDark/MaskWidth/MaskHeight/StdDevScale/AbsThreshold/Connection），Run 按方式分发，Binary/Char 方式输出 UsedThreshold。
- 原 5 个独立阈值类（Auto/Binary/Fast/Char/Var）改为继承 ThresholdTool 的持久化兼容壳：不进工具箱，仅保留稳定 ID（auto-threshold 等）供历史流程文件加载，行为与合并工具对应方式一致；BinaryThresholdTool 保留历史属性名 Method（映射 BinaryMethod）。
- 新增 `MorphologyTool`（稳定 ID `morphology`），`Shape` 参数选择 Rectangle/Circle；原 MorphologyRectTool/MorphologyCircleTool 改为兼容壳。
- 工具箱 8 个旧条目替换为 2 个："阈值分割"（ID `threshold`）与"形态学"（ID `morphology`）；`IsVisualPreviewTool` 路由改用基类判断；`ToolIcons.xaml` 手工追加 `ToolIcon.morphology`（圆+矩形双结构元素图标）。
- README 工具表同步。

验证：全量 build 0 错误；`dotnet test` 192 通过（含示例流程回归——旧 TypeName 的 ThresholdTool/MorphologyCircleTool 正常加载运行、序列化身份唯一性与工具箱一致性用例）。

### 2026-09-17 VF-08：MainWindow 职责拆分（已完成）

背景：MainWindow.xaml.cs 约 1830 行，文档编排、运行会话、参数面板、显示叠加、工具编辑器路由、节点命名、显示设置持久化全部堆在窗口类里，无法单测。本轮按"纯平移不改行为、不搞 MVVM、不动 VisionFlow.Base"的原则拆分为独立类。

改动内容：

- `VisionFlow.EditorCore/Editing/NodeNaming.cs`（新）：节点命名相关静态方法（EnsureUniqueNewNodeName/NextAvailableModuleName/ExistingNodeNames/EnumerateNodes/ModuleNameBase），首参传 `FlowNode root`；MainWindow 旧私有方法删除。
- `VisionFlow.WpfApp/Ui/ParameterPanelBuilder.cs`（新）：参数面板构建整段平移，构造注入面板与 8 个回调委托（取根节点/取输入图/编辑守卫/置脏/警告/刷树/状态/开编辑器）；画刷改走 `Application.Current.FindResource`；SerializableProperties/ConvertText 等静态辅助一并移入。
- `VisionFlow.WpfToolEditors/WpfToolEditorRouter.cs`（新）：工具编辑器窗口路由（注册编辑器→匹配/读图/跟随测量/手动区域/椭圆测量专用窗→视觉预览窗→通用编辑器），`CreateToolEditor`/`IsVisualPreviewTool` 从 MainWindow 平移。
- `VisionFlow.EditorCore/Runtime/EditorRunSession.cs`（新）：运行会话，持有取消源、上次运行上下文与运行专用输入图像副本；`RunningChanged(bool)`/`Progressed(FlowProgress)` 事件，`RunAsync` 返回 `EditorRunOutcome`（AlreadyRunning/Cancelled/Error/Completed 四态）；VF-04 资源交接协议（CopyObj 副本、新结果交接后再释放旧上下文与旧副本、无结果直接回收）原样保留。MainWindow 的 Run_Click/Stop_Click/RunFlowAsync 变为接线。
- `VisionFlow.EditorCore/Ui/FlowDocumentController.cs`（新）：持有 CurrentFlowPath，封装 New/Load/Save/ConfirmClose 编排与 `NormalizeFlowFileName`；保存对话框、关闭确认（Save/Discard/Cancel 三态枚举）、警告提示均构造注入委托；保存成功发 `Saved` 事件。MainWindow 的 NewFlow/LoadFlow/SaveFlow/OnClosing 变为接线 + UI 清理。
- `VisionFlow.EditorCore/Ui/DisplayOverlayBuilder.cs`（新）：IsDisplayableVariable/ResolveDisplayBaseImage/BuildVariableOverlay 及两个 Append 辅助，纯 Variable/HObject 逻辑。
- `VisionFlow.WpfApp/Services/DisplaySettingsStore.cs`（新）：DisplaySettingsModel 上移，`Load`（含范围钳制与读失败警告回调）/`Save`/`SettingsPath`/`FormatPercent`。

新增测试：

- `NodeNamingTests`（4 用例 8 断言）、`EditorRunSessionTests`（7 用例：完成/重入/停止取消/异常/二次运行释放旧上下文/丢弃幂等/真实引擎取消转 Skipped）、`FlowDocumentControllerTests`（16 用例：路径规范化、首存对话框、取消不置脏、已有路径跳过对话框、写盘失败保原文档与原文件、关闭确认三分支、新建复位、保存-加载回环）。

验证：全量 `dotnet build VisionFlow.slnx` 0 警告 0 错误；`dotnet test` 223 通过（基线 192 + 新增 31）。MainWindow.xaml.cs 降至约 950 行，窗口类只保留 UI 接线与对话框。新建/加载/保存/运行/停止/双击编辑器/未保存关闭提示的交互手感以界面手工冒烟为准。

## 2026-10-07 独立小项：叠加显示底图回退（REGION 验收发现的既有问题）

背景：REGION 计划界面验收（64 项检查）中发现——流程用“图像加载”工具供图、未打开 `Input.Image` 时，
在结果显示下拉中直接选中其他模块的区域 / XLD 输出，叠加层画在黑底上；先选中图像变量再切换则正常。
原因：`DisplayOverlayBuilder.ResolveDisplayBaseImage` 只按三级查找——变量自身是图像 → 同模块 `Image` 输出 →
`Input.Image`；图像由“图像加载”节点提供时三级全部落空，返回 null，主窗口回退不到任何底图。

改动：

- `VisionFlow.EditorCore/Ui/DisplayOverlayBuilder.cs`：新增第四级回退——按上下文变量写入顺序扫描
  `GetAllVariables()`，返回第一个 `HalconImage` 变量作为底图。查找优先级不变：自身 → 同模块 Image →
  Input.Image → 上下文第一个图像。多图像并存时取最先写入者（通常即供图节点），已写进 XML 注释。
- `VisionFlow.Tests/DisplayOverlayBaseImageTests.cs`（新增 5 例）：图像加载供图时区域输出回退找到底图、
  Input.Image 优先于扫描回退、同模块 Image 输出仍最优先、图像变量返回自身、无任何图像时返回 null。
  用例只引用未初始化 HObject，不依赖 HALCON 原生运行库。
- `docs\REGION-TOOLS-PLAN.md` 验收章节的问题记录更新为“已另行修复”。

验证：`dotnet build VisionFlow.slnx` 0 警告 0 错误；无 HALCON 原生环境的 585 项非 HALCON 测试全部通过
（含新增 5 例）；本机 shell 无 HALCON 原生运行库，105 项 HALCON 环境门禁用例独立失败，属预期，不计为回归。
WPF 中“图像加载”供图流程的叠加显示效果以界面手工冒烟为准（已于合入 main 后补做界面验收，见下）。

界面验收（2026-10-07，修复合入 `main` 后补做，提交 c2c0cf2）：用 UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`，
流程为“图像加载”供图（背景灰度 140、亮矩形 230）+ 阈值分割（阈值分割没有 `Image` 输出，底图只能来自新增的回退）。
在正常文件对话框打开流程并运行后，在结果显示下拉中**第一次就直接**选中 `阈值1.Region`：

| 检查 | 合入后的 `main` | 修复前（batch5 `7b3be38`，同一脚本对照） |
|---|---|---|
| 直接显示区域输出时有底图 | 通过：画面平均亮度 81.6 | 失败：平均亮度 0.0（叠加画在黑底上） |
| 与“先显示 `图像1.Image` 再切换到区域”的画面一致 | 通过：差异 0 像素，相对纯图像多出叠加 74655 像素 | 失败：差异 526264 像素 |

同一脚本在修复前后结果相反，说明检查能区分有无修复，修复在界面上生效。

## 2026-10-07 状态更新：REGION 工具计划完成

依据 `docs\REGION-TOOLS-PLAN-REVIEW.md` 的评审意见完成 RG-01 ~ RG-07（分支 `feature/region-tools`，
提交 a18d7f5），界面验收 64 项检查全部通过，评审文档与计划状态已同步。

## 2026-10-07 测试修复：EditorRunSessionTests 真实引擎取消用例的竞态

现象：`CancelledFlow_ThroughRealEngine_ReportsSkippedResult` 在全量测试中偶发失败（期望 `Completed` + `Skipped`，实得 `Cancelled`）。

原因（测试自身的竞态，产品行为正确）：`EditorRunSession.IsRunning` 在创建取消源后即为 true，早于
`FlowEngine.RunAsync` 内 `Task.Run(…, token)` 真正开始执行；用例一看到 `IsRunning` 就叫停，若停在这个窗口里，
任务直接以取消结束、引擎根本没有运行，会话按设计返回 `Cancelled`——这是“开始前取消”，不是用例要测的“执行中取消”。
用临时循环复现：沿用旧写法 300 次中 297 次得到 `Cancelled`；改为等工具开始执行后再叫停，300 次全部得到 `Skipped`。

改动：用例中的阻塞工具开始执行时置位 `ManualResetEventSlim`，测试等它置位后再 `Stop()`（并删除未使用的
`CancellationTokenSource`）。产品代码未改；运行时以取消（`OperationCanceledException`）结束时会话返回 `Cancelled`，由 `Stop_DuringRun_ProducesCancelledOutcomeAndCleansUp` 覆盖。

## 2026-10-08 独立小项：多图像流程叠加底图按区域来源溯源（原"已知边界（未修）"，已完成）

现象（标定批二界面冒烟中发现，2026-10-08，Copilot 主动报告）：同一流程有两幅图像时，在结果显示下拉中直接选中第二幅图像上产生的区域（如 `阈值2.Region`），叠加层画在第一幅图像上，显示错位。

原因：2026-10-07 的"叠加显示底图回退"独立小项中，第四级回退按上下文变量写入顺序取**第一个** `HalconImage` 变量作为底图。单供图流程中它通常就是供图节点，正确；多图像流程中固定取最先写入者，与区域的实际来源图像无关，必然选错。`CALIBRATION-TOOLS-PLAN.md` 第 14 节的问题记录 2 亦注明了此边界。

处理决定：**不并入标定线各批**，单独立项修复（分支 `feature/overlay-base-image-source`，基线 `c4ef3aa`）。本项只读工具输入元数据与上次运行的上下文，不涉及 HALCON 算子行为，无需探测。

改动：

- `VisionFlow.EditorCore/Ui/DisplayOverlayBuilder.cs`：`ResolveDisplayBaseImage` 新增可选参数 `FlowNode flowRoot = null`，
  在"同模块 `Image` 输出"之后、`Input.Image` 之前插入一级"区域来源溯源"。查找优先级：自身是图像 → 同模块 Image →
  **来源溯源** → Input.Image → 上下文第一个图像。`flowRoot` 为 null 时跳过新增级，与原四级查找逐项相同。
  溯源做法：用 `NodeNaming.EnumerateNodes` 按变量模块名找到来源 `ToolNode`（含 If/Else 分支与循环体内的节点），
  按 `ToolMetadata.GetInputRefs` 的声明顺序解析其图像输入（期望类型 `HalconImage`；历史声明为 `HObject` 的输入以能解析出
  `HalconImage` 为准），第一个解析出图像的即底图；输入指向 `Input.Image` 时自然得到输入图像。来源工具没有可用的图像输入时
  （如区域排序只有区域输入），沿它的其余输入引用到上游模块继续追溯：先看上游模块的 `Image` 输出，再递归追溯该模块
  （经使用方确认的改进，原建议方案只看一级）。追溯带已访问集合与深度上限 32，引用成环或链过长时落到后续回退。
  找不到来源工具（如 `Input` 模块、手工写入的变量）、路径为空 / 格式错误 / 指向不存在的变量或非图像、全部解析失败时
  均不抛异常，落到后续回退。
- `VisionFlow.WpfApp/MainWindow.xaml.cs`：`DisplaySelectedOutput` 调用时传入 `_model.Root`。
- `VisionFlow.Tests/DisplayOverlaySourceImageTests.cs`（新增 18 例：11 个 Fact + 1 个 7 组数据的 Theory）：
  双图像流程中 `阈值2.Region` 底图为 `图像2.Image`、`阈值1.Region` 为 `图像1.Image`（`Assert.Same`）；
  不传 `flowRoot` 时仍取第一个图像变量（原行为）；图像输入指向 `Input.Image`；来源工具不在流程中时落到
  `Input.Image` / 第一个图像变量；自身图像与同模块 `Image` 输出仍最优先；空路径、空白、不存在的变量、无点号、
  下标格式错误、指向区域、`Loop.Current` 七种图像输入均不抛异常并落到下一级；区域排序沿区域引用溯源到第二幅图；
  溯源途中上游模块有 `Image` 输出时用该输出；循环体内的来源工具；引用成环；超过追溯深度；`HObject` 声明的图像输入。
  用例只引用未初始化 HObject，不依赖 HALCON 原生运行库。原 `DisplayOverlayBaseImageTests` 5 例逐字未改。
- `docs\CALIBRATION-TOOLS-PLAN.md` 第 14 节问题记录 2 补注"已另行修复"。

已知边界：溯源按**当前**流程结构解析来源工具的输入配置、按上次运行的上下文取值；运行后改了工具的图像输入而未重新运行时，
底图按新配置在旧结果中解析（取不到则落到后续回退）。工具同时有多个图像输入时取声明顺序第一个能解析的。

验证：`dotnet build VisionFlow.slnx` 0 错误，唯一警告为既有的 `MatchMeasureBatch4Tests.cs(143)` xUnit2000，无新增；
`dotnet test`（`Category!=Soak`）978 项全部通过（基线 960 + 新增 18）。本机装有 HALCON 22.11，HALCON 门禁用例同样实际运行并通过。

界面验收（2026-10-08）：用 UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`，沿用 2026-10-07 的脚本做法。
双供图流程为 `图像1`（背景灰度 60、左上亮块 230）、`图像2`（背景 180、右下亮块 250）、`阈值1` / `阈值2`（各取自己的图像）、
`排序2`（区域输入 `阈值2.Region`，没有图像输入）；单供图流程与 2026-10-07 相同。每个区域都在重新加载、运行后**第一次就直接**选中，
再与"先显示来源图像再切回"以及"另一幅图像"的画面逐像素比对；同一脚本对修复前的 `c4ef3aa`（临时工作树构建）对照运行：

| 检查 | 本分支（连续两次） | 修复前 `c4ef3aa` |
|---|---|---|
| 单供图：直接显示 `阈值1.Region` 有底图 | 通过：平均亮度 81.6 | 通过：81.6（无回归，与 2026-10-07 记录一致） |
| 单供图：与先显示图像再切换一致 | 通过：差异 0 像素，叠加 74655 像素 | 通过：同左 |
| 双供图：直接显示 `阈值2.Region` 画在 `图像2` 上 | 通过：相对 `图像2` 只多出叠加 53465 像素，与 `图像1` 底图差异 525636 像素 | 失败：相对 `图像2` 差异 525636 像素、与 `图像1` 只差 53465 像素（画在第一幅图上） |
| 双供图：直接显示 `阈值1.Region` 画在 `图像1` 上 | 通过：叠加 48392 像素，与 `图像2` 差异 525636 像素 | 通过（第一幅图本来就是第一个图像变量） |
| 双供图：直接显示 `排序2.Region`（递归溯源）画在 `图像2` 上 | 通过：同 `阈值2.Region` | 失败：同 `阈值2.Region` |

说明：任务建议的"与先选中 `图像2.Image` 再切换的画面差异为 0"单独不能区分有无修复——底图解析不依赖之前显示过什么，修复前切回后
同样画在第一幅图上，差异也是 0（对照运行中两侧都是 0 像素）。因此每项同时比对直接显示画面与两幅纯图像的差异，修复前后结果正好互换。

状态：**已完成**。
