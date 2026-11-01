# 健壮性修复项：加载崩溃两处（RF-01 / RF-02）

> 来源：2026-11-01 运行演示时发现。加载失败/环境缺失时进程直接崩溃，应降级为提示。

## RF-01 加载含稳定 ToolId 的流程导致进程崩溃

### 现象

WPF 编辑器点击"打开"加载 `examples/wafer-chips.vflow.json`，进程直接退出。

事件日志：`InvalidOperationException: 工具节点缺少工具类型（ToolId: threshold）`，抛自
`FlowSerializer.ResolveToolType`（`VisionFlow.Base/Runtime/FlowSerializer.cs:279`），
经 `FlowDocumentController.Load`（`VisionFlow.EditorCore/Ui/FlowDocumentController.cs:78`）
冒泡到 `MainWindow.LoadFlow_Click`（`MainWindow.xaml.cs:550`），无捕获 → 进程崩溃。

### 根因（两层）

1. **内建工具程序集延迟加载，稳定 ID 尚未登记。**
   `BuiltinToolIdentities` 用 `[ModuleInitializer]` 登记 `threshold` 等稳定 ID，但模块初始化器
   只在 `VisionFlow.Tools` 程序集被加载时运行。`ToolboxRegistry.RegisterDefaults()`
   （`MainWindow` 启动即调用）只创建带工厂 lambda 的 `ToolboxItem`，lambda 体内的工具类型
   在被**调用前**不触发程序集加载；工具箱界面只显示名称，也不实例化工具。
   因此用户若先打开流程再碰工具箱，`VisionFlow.Tools` 尚未加载 → `_toolIdentities` 没有
   `threshold` → 解析失败。（`threshold-region.vflow.json` 老式 `TypeName` 格式能加载，
   正是因为 `Type.GetType` 顺带加载了程序集。）

2. **解析失败无降级路径。** `ResolveToolType` 直接 `throw`，`FromDto` 递归、`Load`、
   `LoadFile`、`FlowDocumentController.Load` 全链均无捕获，编辑器进程崩溃；
   子流程（`SubFlowNode`）加载同样走 `FlowSerializer`，也存在相同风险。

### 修复方案

1. **启动时强制初始化内建工具模块。** `ToolboxRegistry.RegisterDefaults()` 开头加
   `RuntimeHelpers.RunModuleConstructor(typeof(ThresholdTool).Module.ModuleHandle);`，
   保证编辑器/无头宿主在登记工具箱前完成稳定 ID 登记（该方法是 app 与测试共用的咽喉点）。
2. **未知工具类型降级为占位工具。** `FlowSerializer` 新增 `UnknownTool : ToolBase`
   （放 `VisionFlow.Base`，序列器不能依赖 Tools 程序集）：解析失败时不再抛异常，
   创建占位工具，原样保留 `ToolId`、`ModuleName` 与全部原始参数（`Dictionary<string, JsonElement>`），
   并向 `warnings` 追加"工具类型未安装，已保留参数"提示。保存时按原 `ToolId` 与原始参数写回，
   保证插件/工具库装回后数据不丢。运行时 `Run` 抛出带 `ToolId` 的明确异常，由运行错误通道呈现。
   `ToToolDto` 对 `UnknownTool` 特判写出。
3. **编辑器加载入口兜底。** `MainWindow.LoadFlow_Click` 对 `_document.Load(...)` 包 try/catch，
   失败时 `ShowWarning` 提示并保留原流程，不再崩溃（防御格式错误、版本过高等其余加载异常）。

### 验收

- 冷启动 WPF 编辑器（不点工具箱）直接打开 `wafer-chips.vflow.json`：正常加载、弹出兼容性提示。
- 打开含不存在 `ToolId` 的流程：加载成功、树中显示占位节点、警告提示、保存后 JSON 原参数不丢。
- `dotnet build` 0 警告 0 错误；`dotnet test`（Category!=Soak）全绿，含新增用例。

---

## RF-02 无 HALCON 环境时"打开图像"崩溃

### 现象

进程 PATH 缺少 HALCON bin 目录时，点击工具栏"打开图像"选择文件后进程直接退出。

事件日志：`DllNotFoundException: Unable to load DLL 'halcon'`，抛自
`MainWindow.OpenImage_Click`（`MainWindow.xaml.cs:486`）的 `HOperatorSet.ReadImage`，未捕获。

### 根因

`OpenImage_Click` 对 `ReadImage` 无任何异常处理。HALCON DLL 缺失、文件不存在、
文件不是图像格式等任何读取失败都会以未处理异常终结进程。

### 修复方案

`OpenImage_Click` 中 `ReadImage` 及后续状态更新包 try/catch：

- `DllNotFoundException`：提示"未找到 HALCON 运行库，请检查 HALCON 安装或 PATH 配置"。
- 其他异常（文件不存在/格式损坏等）：提示"图像打开失败"及基础异常信息。
- 失败时保持原有输入图像与状态不变。

### 验收

- 无 HALCON PATH 启动应用 → 打开图像 → 弹出提示框，进程存活，可继续操作。
- 正常环境下打开图像行为不变（回归 `03-image-loaded` 演示路径）。
