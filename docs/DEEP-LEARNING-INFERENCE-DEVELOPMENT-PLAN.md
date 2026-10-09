# 深度学习推理模块开发计划

编写日期：2026-10-09
适用阶段：D0 ~ D5 已全部完成（2026-10-09）。阶段状态：

- **D0 探测：已完成**（结论在方案 §14，预处理配方与三类结果结构已钉死）。
- **D1 公共层与共享池：已完成**（`SharedDlModelCache` / `DlModelCachePolicy` / `ModelCacheMode` / `FlowContext.OwnerToken`；非 HALCON + HALCON 门禁共 17 用例）。
- **D2 检测 `dl-detect`：已完成**（11 用例，官方 detect_pills.hdl 逐值对照）。
- **D3 分类 `dl-classify`：已完成**（11 用例，官方 classify_pill_defects.hdl；实现期修复 Instance 缓存键缺陷）。
- **D4 分割 `dl-segment`：已完成**（12 用例，官方 segment_pill_defects.hdl）。
- **D5 示例与文档收尾：已完成**（3 个示例流程 + `DeepLearningExampleFlowTests` + README 双文档同步；示例 `FailWhenNotFound=false` 并登记典型值）。

全量 1405 通过。遗留（不阻塞）：三工具与 dl-infer 装载逻辑重复（可重构项，改共享池键规则或加公共参数时一并下沉到 `DlModelCachePolicy`）；共享池 static 计数依赖测试顺序隔离，前后差值断言的写法必须保持（批审 4.2）。
关联文档：

- `docs\DEEP-LEARNING-INFERENCE-PLAN.md`
- `docs\DEEP-LEARNING-INFERENCE-PLAN-REVIEW.md`
- `docs\DEEP-LEARNING-INFERENCE-PLAN-REVIEW2.md`
- `docs\DEEP-LEARNING-INFERENCE-PLAN-REVIEW3.md`
- `docs\RECOGNITION-TOOLS-PLAN.md`

## 1. 目标与范围

本计划用于把“深度学习推理模块方案”转成可执行的开发路径，供后续逐阶段实现与验收。

开发目标：

- 在现有 `dl-infer` 统一运行时基础上，完成深度学习检测、分类、分割三类专用工具。
- 先完成 HALCON 22.11 下的真实可行链路探测，再进入业务代码实现。
- 保持与现有识别线一致的工具风格：工具箱登记、持久化 ID、`INotFoundPolicy`、WPF 编辑器、真实 HALCON 门禁测试、README/计划文档同步。

本计划范围内包含：

- 方案落地前置探测。
- 公共帮助层补强。
- `dl-detect`、`dl-classify`、`dl-segment` 三个工具。
- 对应测试、文档、编辑器和示例流程。

本计划范围外暂不包含：

- 图像数组批量输入。
- 定位矩阵跟随。
- 训练、微调、标注、模型转换。
- 除 HALCON 官方 `.hdl` 模型之外的第三方推理后端。

## 2. 当前基线

当前已具备：

- `DeepLearningInferenceTool` 第一阶段统一运行时。
- `dl-infer` 的工具箱与持久化登记。
- 模型路径解析、模型类型识别、设备选择、batch / optimize 参数设置。
- 公共元数据输出：`Ready`、`ModelType`、`ResolvedModelPath`、`DeviceUsed`、`ClassNames`、`ClassIds`、`ClassCount`、`ImageWidth`、`ImageHeight`、`OptimizedForInference`、`ModelSummary`、`Info`。
- 官方检测模型 `detect_pills.hdl` 的真实预热回归测试。

已确认的开发约束：

- 第二阶段不继续膨胀 `dl-infer` 的业务输出。
- 三类专用工具统一以 `.hdl` 模型为入口。
- 分类工具不走 `apply_dl_classifier` 主路径，而是统一走 `apply_dl_model`。
- 第一版只做单图推理，`BatchSize` 仅保留为模型运行参数。
- ROI 第一版采用“裁剪或 reduce_domain 后推理，再映射回原图坐标”的策略，不并入定位矩阵跟随。

## 3. 开发原则

### 3.1 先探测后实现

在 HALCON 22.11 的 HalconDotNet 中，深度学习链路存在 HDevelop 示例与 .NET 暴露面不完全一致的问题。任何关键实现前，必须先做探测并记录结论，不能按文档记忆直接编码。

### 3.2 统一运行时与业务层分离

- `dl-infer` 继续只负责模型就绪与公共元数据。
- `dl-detect`、`dl-classify`、`dl-segment` 负责各自业务输出。
- 公共帮助逻辑可下沉为内部帮助类或基类，但对外仍是独立工具和独立持久化 ID。

### 3.3 与现有识别线保持一致

三类专用工具默认都应：

- 实现 `INotFoundPolicy`。
- 输出 `Found`、`Count` 或等价数量信息。
- 使用中文错误信息。
- 在 README、计划文档、工具箱、路由、测试中形成闭环。

## 4. 总体阶段划分

| 阶段 | 名称 | 目标 | 是否写业务代码 |
|---|---|---|---|
| D0 | 探测与设计定稿 | 钉死输入链路、结果结构、ROI 语义、错误口径 | 否 |
| D1 | 公共层补强 | 为三类专用工具准备公共帮助层和共享约定 | 是 |
| D2 | 深度学习检测 | 完成 `dl-detect` 全闭环 | 是 |
| D3 | 深度学习分类 | 完成 `dl-classify` 全闭环 | 是 |
| D4 | 深度学习分割 | 完成 `dl-segment` 全闭环 | 是 |
| D5 | 收尾与回归 | 文档、示例、编辑器人审、全量回归 | 是 |

推荐顺序固定为：`D0 → D1 → D2 → D3 → D4 → D5`。

## 5. D0：探测与设计定稿

### 5.1 阶段目标

在不修改主实现逻辑的前提下，通过临时探针彻底回答“能否把图像喂进 `apply_dl_model` 并拿到稳定结果”的问题，并把结论回填到方案文档。

### 5.2 必做探测项

#### A. 输入链路与预处理

- `create_dict` + `set_dict_object(image, 'image')` 后，`apply_dl_model` 是否接受。
- sample dict 的最小必填键集合。
- 输入图尺寸与模型 `image_width` / `image_height` 不一致时的行为。
- 输入图类型要求：灰度 / 三通道 / byte / 其他类型的行为与错误码。
- 若 HALCON 不自动处理尺寸或通道，工具层是否必须自行做 `zoom_image_size` / 通道转换。

#### B. 结果结构

- 分类模型返回的顶层 dict 键名、类别字段、分数字段、TopK 可读性。
- 检测模型返回的框坐标、类别、分数字段及其顺序。
- 分割模型返回的是 mask 图、类别图、概率图还是区域集合。

#### C. 坐标与 ROI 语义

- `reduce_domain` / 裁剪后推理时，检测与分割结果坐标是原图坐标还是输入网络图坐标。
- 若结果不是原图坐标，偏移和缩放的回写规则是什么。

#### D. 设备与批量

- `query_available_dl_devices('runtime','gpu')` 的本机结果。
- GPU 不可用时的稳定失败路径。
- `BatchSize > 1` 但输入仍为单图时的实际行为。

#### E. 分类接口收口

- 一次性验证 `apply_dl_classifier` 收到 `DLModelHandle` 的失败行为和错误码。
- 把“分类统一走 `apply_dl_model`”正式写回方案，不再保留双分支设计。

### 5.3 产物

- 方案文档补充探测结论节。
- 统一记录探测环境、模型路径、示例图路径、错误码、实现约定。
- 明确的输入输出协议定稿。

### 5.4 完成标准

- 所有探测项都有结论，不留“待实现时再看”。
- `DEEP-LEARNING-INFERENCE-PLAN.md` 已回填探测结论和实现约定。
- 后续编码所需的输入链路已确定，不再存在主路径分叉。

## 6. D1：公共层补强

### 6.1 阶段目标

在现有 `DeepLearningInferenceTool` 基础上，下沉三类专用工具共享的帮助逻辑，但不改变 `dl-infer` 的外部职责。

### 6.2 计划任务

#### A. 公共帮助结构

- 提炼模型路径解析、模型缓存键、设备选择、公共元数据读取的共享帮助方法。
- 统一中文异常包装，形成“模型文件错误 / 模型类型不匹配 / 设备不可用 / 输入图像非法 / 结果结构缺失”的标准错误口径。
- 统一 `NotFound` 处理约定，准备给三类专用工具复用。

#### B. 公共类型

- 如有必要，新增内部结果对象：
  - 分类结果对象。
  - 检测结果对象。
  - 分割结果对象。
- 这些对象只服务流程输出与编辑器显示，不引入新的外部依赖。

#### C. 公共约定补齐

- 第二阶段公共输出清单补齐 `OptimizedForInference`。
- 统一 `Found` / `Count` / `NotFound` 语义。
- 明确分割背景类是否计入 `Count` / `Found`。

#### D. 共享句柄池与锚点机制（第三轮评审 P3 落地，`dl-infer` 同步接入）

- `ModelCacheMode` 参数（`Instance` 默认 / `Flow` / `Project`）落地到 `dl-infer` 与后续三个专用工具，默认值保持现有行为。
- 新增 `SharedDlModelCache`（static，引用计数 + 条目级锁 + Stale 标记）与 `DlModelCachePolicy` 帮助类（统一池键规则与 Acquire/Release 调用点）。
- 引用计数按"工具实例持有当前键一份"，不按运行次数累计；键变化先还旧键再取新键（第三轮评审 P1-1）。
- 框架锚点：`FlowContext.OwnerToken` 属性；`FlowEngine.Run` / `FlowResources.Prepare(FlowNode root)` 写入根节点；`CreatePreviewContext()` 复制该锚点（第三轮评审 P2-1）。
- 入口边界：`Flow` 模式仅对有根锚点的入口成立；`Prepare(IEnumerable<ToolNode>)` 快照路径回退 `Instance` 并写日志（第三轮评审 P1-2）。
- 失效语义：`Invalidate(path)` 只标 Stale，引用归零才真正 `clear_dl_model`（第三轮评审 P2-2）。
- 测试：非 HALCON 用例覆盖引用计数平衡、键切换、失效、预览副本、锚点缺失回退；HALCON 门禁覆盖三档模式共享行为、mtime 切换、最后引用释放。

### 6.3 预计改动文件

- `VisionFlow.Tools\Tools\DeepLearningTools.cs`
- 如需拆分，可新增：
  - `VisionFlow.Tools\Tools\DeepLearningToolShared.cs`
  - `VisionFlow.Tools\Tools\SharedDlModelCache.cs`
  - 或 `VisionFlow.Tools\DeepLearning\*.cs`
- 框架锚点（第三轮评审 P3 文件落点）：
  - `VisionFlow.Base\Core\FlowContext.cs`（`OwnerToken` + `CreatePreviewContext` 复制）
  - `VisionFlow.Base\Runtime\FlowResources.cs`（`Prepare(FlowNode root)` 写锚点；快照入口回退日志）
  - `VisionFlow.Base\Engine\FlowEngine.cs`（`Run` 写锚点）

### 6.4 完成标准

- `dl-infer` 现有行为和测试不回归。
- 专用工具开发已不再需要重复写模型装载与设备处理逻辑。
- 共享池非 HALCON 用例全过；`Instance` 默认模式下 `dl-infer` 的行为与 D1 之前逐一致。

## 7. D2：深度学习检测

### 7.1 阶段目标

完成 `dl-detect` 的工具实现、测试、编辑器和示例流程，形成第二阶段第一个真实可用的任务型深度学习工具。

### 7.2 功能范围

- 输入：图像、可选 ROI、模型路径、设备、BatchSize、MinScore、MaxDetections。
- 输出：`Regions`、`Contours`、`ClassIds`、`ClassNames`、`Scores`、`Count`、`Found`、`BestClassName`、`BestScore`。
- 策略：结果低于 `MinScore` 的目标直接丢弃；`MaxDetections=0` 表示不过滤数量。

### 7.3 计划任务

#### A. 运行时实现

- 读取检测模型。
- 构造输入 sample dict 或等价输入结构。
- 调用 `apply_dl_model`。
- 将 HALCON 返回结果转换为流程友好的区域 / 轮廓 / 数组输出。
- 处理 ROI 偏移回写。

#### B. 工具接入

- `BuiltinToolIdentities` 登记 `dl-detect`。
- `ToolboxRegistry` 加入“06 识别工具 / 深度学习检测”。
- `WpfToolEditorRouter` 路由专用编辑器。

#### C. 编辑器

- 新建 `WpfDlDetectToolEditWindow`。
- 支持加载当前图像测试。
- 支持叠加检测结果框或轮廓。
- 显示类别和分数列表。

#### D. 测试

- 工具箱登记测试。
- 持久化 ID 测试。
- 配置校验测试。
- 官方 `detect_pills.hdl` 真实门禁用例。
- ROI 与 `MinScore` / `MaxDetections` 行为测试。

#### E. 示例与文档

- 新增检测示例流程。
- 更新 README 和计划文档的实现状态。

### 7.4 完成标准

- 官方检测模型在本机可真实跑通。
- 结果位置肉眼与 HALCON 示例观感一致。
- 相关测试稳定通过。

## 8. D3：深度学习分类

### 8.1 阶段目标

完成 `dl-classify` 的工具实现与验证，统一走 `.hdl + apply_dl_model` 分类路径。

### 8.2 功能范围

- 输入：图像、可选 ROI、模型路径、设备、BatchSize、MinScore、TopK、ExpectedClass。
- 输出：`ClassId`、`ClassName`、`Score`、`TopClassIds`、`TopClassNames`、`TopScores`、`MatchedExpected`、`Found`。

### 8.3 计划任务

#### A. 运行时实现

- 读取分类模型结果 dict。
- 解析主类别、TopK 结果与分数。
- 处理 `ExpectedClass` 比对。
- 明确 `Found` 在分类工具中的语义：
  - 推荐为“存在有效分类结果且分数达到阈值”。

#### B. 工具接入

- `BuiltinToolIdentities` 登记 `dl-classify`。
- `ToolboxRegistry` 加入“06 识别工具 / 深度学习分类”。
- 路由专用编辑器。

#### C. 编辑器

- 新建 `WpfDlClassifyToolEditWindow`。
- 显示当前图像、主分类结果、TopK 列表和分数。
- 对低于阈值的结果做明显标识。

#### D. 测试

- 官方 `classify_pill_defects.hdl` 真实门禁用例。
- `TopK` 截断测试。
- `ExpectedClass` 比对测试。
- `MinScore` 和 `NotFound` 语义测试。

#### E. 示例与文档

- 新增分类示例流程。
- 更新 README 和计划文档状态。

### 8.4 完成标准

- 分类模型在本机可真实跑通。
- `TopK`、`ExpectedClass`、`MinScore` 语义全部稳定。

## 9. D4：深度学习分割

### 9.1 阶段目标

完成 `dl-segment` 的工具实现与验证，解决像素级结果向流程变量与区域对象的映射问题。

### 9.2 功能范围

- 输入：图像、可选 ROI、模型路径、设备、BatchSize、MinScore、OutputMode、ClassFilter。
- 输出：`MaskImage`、`Regions`、`ClassIds`、`ClassNames`、`Areas`、`Count`、`Found`。

### 9.3 计划任务

#### A. 运行时实现

- 读取分割模型结果结构。
- 确定 mask / 类别图到区域的转换方式。
- 明确 `MinScore` 在分割中的语义：
  - 若有像素级置信结果，则决定是像素过滤还是区域级过滤。
- 明确背景类不计入还是计入 `Count` / `Found`。

#### B. 工具接入

- `BuiltinToolIdentities` 登记 `dl-segment`。
- `ToolboxRegistry` 加入“06 识别工具 / 深度学习分割”。
- 路由专用编辑器。

#### C. 编辑器

- 新建 `WpfDlSegmentToolEditWindow`。
- 支持 mask 叠加预览。
- 支持按类别显示区域与面积。

#### D. 测试

- 官方 `segment_pill_defects.hdl` 真实门禁用例。
- 背景类语义测试。
- `OutputMode` 行为测试。
- `ClassFilter` 过滤测试。

#### E. 示例与文档

- 新增分割示例流程。
- 更新 README 与计划文档状态。

### 9.4 完成标准

- 分割结果能稳定转为“每类一个区域”的流程输出。
- mask 叠加观感与 HALCON 示例一致。

## 10. D5：收尾与回归

### 10.1 阶段目标

完成整条深度学习推理线的文档、示例、编辑器人审和测试收口。

### 10.2 计划任务

- 对三类工具的编辑器做人工验收：
  - 叠加结果是否真实。
  - 低分结果是否明显。
  - 深浅色主题下观感是否可用。
- 补齐 README 能力表与示例说明。
- 在 `DEEP-LEARNING-INFERENCE-PLAN.md` 中补“实现与验收记录”。
- 补充或更新：
  - 示例流程 README。
  - 门禁用例数量记录。
  - 已知边界与遗留。

### 10.3 完成标准

- 三类工具都具备：工具箱入口、持久化、编辑器、示例流程、真实门禁测试、文档登记。
- 无未记录的接口边界或已知风险。

## 11. 文件改动落点规划

预计会涉及以下文件或目录：

### 11.1 工具实现

- `VisionFlow.Tools\Tools\DeepLearningTools.cs`
- `VisionFlow.Tools\Tools\DeepLearning*.cs` 或 `VisionFlow.Tools\DeepLearning\*.cs`
- `VisionFlow.Tools\BuiltinToolIdentities.cs`

### 11.2 编辑器

- `VisionFlow.WpfToolEditors\WpfToolEditorRouter.cs`
- `VisionFlow.WpfToolEditors\Editors\WpfDlDetectToolEditWindow.xaml`
- `VisionFlow.WpfToolEditors\Editors\WpfDlDetectToolEditWindow.xaml.cs`
- `VisionFlow.WpfToolEditors\Editors\WpfDlClassifyToolEditWindow.xaml`
- `VisionFlow.WpfToolEditors\Editors\WpfDlClassifyToolEditWindow.xaml.cs`
- `VisionFlow.WpfToolEditors\Editors\WpfDlSegmentToolEditWindow.xaml`
- `VisionFlow.WpfToolEditors\Editors\WpfDlSegmentToolEditWindow.xaml.cs`

### 11.3 编辑器核心与登记

- `VisionFlow.EditorCore\Editing\ToolboxRegistry.cs`
- `VisionFlow.WpfToolEditors\Resources\ToolIcons.xaml`（如需新图标）

### 11.4 测试

- `VisionFlow.Tests\DeepLearningInferenceToolTests.cs`
- `VisionFlow.Tests\DeepLearningDetectionToolTests.cs`
- `VisionFlow.Tests\DeepLearningClassificationToolTests.cs`
- `VisionFlow.Tests\DeepLearningSegmentationToolTests.cs`
- D1 共享池与框架锚点专项（第三轮评审 P3 补落点）：
  - `VisionFlow.Tests\SharedDlModelCacheTests.cs`（非 HALCON：引用计数平衡、键切换先还后取、参数分池、`Invalidate` 标脏与归零回收、编辑副本不泄漏）
  - `VisionFlow.Tests\FlowOwnerTokenTests.cs`（非 HALCON：`FlowEngine.Run` 写锚点、`Prepare(FlowNode root)` 环境锚点、快照 `Prepare(IEnumerable)` 回退日志、`CreatePreviewContext` 继承锚点）
  - `VisionFlow.Tests\SharedDlModelCacheHalconTests.cs`（`Requires=HALCON`：`Instance / Flow / Project` 三档共享行为、mtime 切换、最后引用释放）
- 如需 UI 结构测试，再补 `RecognitionEditorUiTests` 风格用例

### 11.5 文档与示例

- `docs\DEEP-LEARNING-INFERENCE-PLAN.md`
- `README.md`
- `examples\*.vflow.json`
- `examples\README.md`

## 12. 里程碑与交付物

| 里程碑 | 交付物 | 通过条件 |
|---|---|---|
| M0 | 探测结论回填后的方案文档 | 输入链路与结果结构已定稿 |
| M1 | 公共帮助层补强 | `dl-infer` 不回归，专用工具共享逻辑 ready |
| M2 | `dl-detect` 完整闭环 | 真实检测模型通过，编辑器可视化可用 |
| M3 | `dl-classify` 完整闭环 | 真实分类模型通过，TopK 语义稳定 |
| M4 | `dl-segment` 完整闭环 | 真实分割模型通过，区域输出稳定 |
| M5 | 总体验收完成 | 文档、示例、测试、人审全部收口 |

## 13. 风险与停止条件

### 13.1 主要风险

- HalconDotNet 对 DLSample / 预处理链路支持不足。
- 分割模型结果结构在 .NET 中与 HDevelop 示例不一致。
- ROI 后坐标回写存在缩放与偏移双重映射风险。
- GPU 不可用导致部分设备策略无法在本机闭环。

### 13.2 停止条件

出现以下任一情况时，应暂停当前阶段编码，先补探测或调整方案：

- `apply_dl_model` 无法接受手工构造的输入 sample dict。
- 输入图必须经过工具层手工预处理，但预处理规则无法稳定复现。
- 检测或分割结果坐标语义无法确定。
- 官方模型在本机 HALCON 22.11 下无法形成稳定结果。

## 14. 推荐执行方式

后续按本计划开发时，严格遵循以下节奏：

1. 先完成 D0，更新方案文档。
2. 每完成一个阶段，就做一次小评审和回归。
3. 每新增一个工具，都同步完成：
   - 实现
   - 工具箱登记
   - 持久化 ID
   - 编辑器
   - 测试
   - 示例
   - 文档
4. 不跨阶段并行抢做，优先保证单阶段闭环。

## 15. 当前结论

这份开发计划确认了后续工作不是“直接写三个工具”，而是：

- 先完成输入链路与结果结构探测；
- 再补公共层；
- 然后按“检测 → 分类 → 分割”逐个闭环开发；
- 最后统一收尾验收。

后续实际开发以本计划和方案文档的最新版本为准。
