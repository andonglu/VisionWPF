# 深度学习推理模块开发计划

编写日期：2026-10-09
状态：第一阶段"统一运行时框架"与第二阶段三个专用工具（DL-01 分类 / DL-02 检测 / DL-03 分割）均已实现并通过回归测试（2026-10-09，探测见 §14，实现与验收见 §15）；`ModelCacheMode` 共享句柄池（Instance/Flow/Project）随 D1 落地。
范围：工具箱“06 识别工具”的深度学习推理能力，基于 `VisionFlow.Tools\Tools\DeepLearningTools.cs` 继续扩展。
HALCON 版本基线：22.11 Steady（本机已验证 `.hdl` 模型加载、设备选择、模型元数据读取与预热可用）。
关联文档：`docs\RECOGNITION-TOOLS-PLAN.md`（第 8 节将深度学习分类、检测、分割另行规划）、`README.md`（已登记第一阶段公共运行时能力）。

## 1. 现状

当前已完成一个基础工具：

| 工具 | ID | 类型 | 当前能力 | 已验证 |
|---|---|---|---|---|
| 深度学习推理 | `dl-infer` | `DeepLearningInferenceTool` | `.hdl` 模型加载、模型类型探测、设备选择、batch 设置、推理态优化、公共元数据输出 | 官方 `detect_pills.hdl` 预热通过 |

当前输出的是“模型已就绪”和公共元数据，不是业务结果。现阶段尚未输出：

- 分类结果、TopK、类别分数。
- 检测框、类别、分数、轮廓区域。
- 分割掩膜、类别区域、面积统计。

## 2. 目标

在现有统一运行时基础上，补齐深度学习推理的业务闭环，形成三类专用工具：

| 编号 | 工具 | 任务 | 目标输出 |
|---|---|---|---|
| DL-01 | 深度学习分类 | 单标签 / 多标签分类 | 类别、分数、TopK、是否命中 |
| DL-02 | 深度学习检测 | 目标检测 / 实例检测 | 框、轮廓、类别、分数、数量 |
| DL-03 | 深度学习分割 | 语义分割 / 区域分割 | 掩膜、各类区域、面积、是否发现 |

结果：保留一个公共运行时工具，再新增 3 个任务型工具入口。

## 3. 拆分结论

深度学习分类、检测、分割虽然共用 `.hdl`、设备、batch、输入图像，但输出结构完全不同，必须拆成 3 个工具，而不是继续堆在一个超大枚举工具里。

拆分依据：

- 分类输出是标签和分数，天然是标量 / 数组结构。
- 检测输出是目标级对象集合，天然是区域 / XLD / 位置结构。
- 分割输出是像素级结果，天然是图像 / 区域集合结构。
- 三者的编辑器体验也不同：分类更适合结果列表，检测需要叠加框，分割需要掩膜预览。

## 4. 第一阶段已落地能力

`DeepLearningInferenceTool` 已承担以下公共职责：

- 路径解析：支持环境变量、相对路径、绝对路径。
- 模型读取：`ReadDlModel`。
- 模型类型规范化：`classification` / `detection` / `segmentation`。
- 设备选择：`Auto` 优先 GPU，失败回退 CPU。
- 运行参数：`batch_size`、`optimize_for_inference`。
- 公共元数据：类别名、类别 ID、输入尺寸、模型摘要。
- 生命周期：缓存、预热、释放。

这一层不再继续膨胀业务输出，后续专用工具只复用它的模型解析和设备策略，不在一个类中混杂三种任务结构。

## 5. 第二阶段总体设计

### 5.1 设计原则

- 公共运行时与业务推理分层：模型加载、设备设置、公共元数据读取由统一层承担；分类 / 检测 / 分割只处理各自的输入输出。
- 输出结构面向流程编排：不仅要给字符串结果，也要给可被下游工具继续消费的区域、数组和对象。
- 默认值优先可落地：默认 batch=1、默认单图推理、默认只处理一张输入图，后续再扩展批量。
- 严守真实 HALCON 能力：不假设 HDevelop 示例中的辅助过程在 .NET 里天然可用，所有关键接口都以 22.11 实测为准。

### 5.2 公共输入约定

三类专用工具统一采用以下公共输入：

- 图像（必填）。
- 可选 ROI 区域。
- 模型路径或模型资源引用。
- 设备偏好（Auto / Cpu / Gpu）。
- BatchSize。
- 结果最小置信度。

说明：

- 第一版只支持单张图像输入，不在本批引入图像数组批量推理。
- ROI 的策略统一为“先裁再推理”还是“整图推理后映射回 ROI”，必须在探测后定稿，不凭经验决定。

### 5.3 公共输出约定

三类专用工具统一保留这些公共输出：

- `Ready`
- `ModelType`
- `ResolvedModelPath`
- `DeviceUsed`
- `ClassNames`
- `ClassIds`
- `ClassCount`
- `ImageWidth`
- `ImageHeight`
- `OptimizedForInference`
- `ModelSummary`
- `Info`

然后再叠加各自专用输出。

### 5.4 模型句柄缓存策略（共享范围用户可配）

**需求背景**：显存较小的现场希望同一模型只加载一份；是否共享、共享到什么范围，由用户在工具参数上自行决定，不由框架写死。

**参数**：每个深度学习工具新增 `ModelCacheMode` 枚举（追加在既有参数之后，默认值 = 现有行为）：

| 取值 | 语义 |
|---|---|
| `Instance`（默认） | 现状：每工具实例一份句柄，不共享。默认即现有行为，保证兼容性 |
| `Flow` | 同一流程文档内共享一份句柄：同流程多个工具/同一工具的循环引用复用同一句柄；流程关闭、引用归零即释放 |
| `Project` | 整个进程（项目）共享一份句柄：跨流程复用同一模型句柄，最后一个使用者释放或强制清池时才 `clear_dl_model` |

**实现方案**：

- 新增 `SharedDlModelCache`（`VisionFlow.Tools`，static）：池键 = `(ScopeToken, 解析后路径, LastWriteTimeUtc, ModelKind, Device, BatchSize, Optimize)`——模型身份部分与现行实例缓存键规则完全一致（落实评审 P1-2"同一份缓存键规则"）；条目 = `{ Handle, RefCount, Gate, Stale }`。**回收规则唯一、无歧义：`RefCount` 归零即 `clear_dl_model`，与 `Stale` 无关；`Stale` 只控制"是否允许被新 Acquire 复用"，不影响回收时机**（避免"非失效才清除"字面导致失效条目永久泄漏）。
- **引用计数按"实例持有"，不按运行次数累计**（第三轮评审 P1-1，硬性）：每个工具实例对"当前池键"最多 Acquire 一次，键不变则复用已持有条目、不重复加引用；键变化时先归还旧键引用、再 Acquire 新键；`ReleaseResources()` 只归还本实例持有的那一份。`FlowEngine.Run` 不会在每次运行后调用工具级归还，按"每次 Run 都 Acquire"实现会形成稳定泄漏。
- **流程锚点（框架小改，注入机制写死）**：`FlowContext` 增加 `OwnerToken`（object，可空）属性。锚点经两条通道注入，**均不改 `IToolResourceLifecycle` 无参签名**：① 运行路径——`FlowEngine.Run(root, ctx)` 把根节点写入 `ctx.OwnerToken`，工具 `Run` 中 Acquire 时读取；② 预热路径——`FlowResources` 增加 static `AsyncLocal<FlowNode>` 环境锚点，**仅 `Prepare(FlowNode root)` 重载**在预热循环期间设置（该重载当前直接委托 `Prepare(IEnumerable)` 实现，需把核心循环拆为私有方法、root 重载包一层设置/清除），工具 `Prepare` 中 Acquire 时环境读取。两通道取到的锚点都存入工具自身字段，`ReleaseResources` 用存储值归还池——`Release(ToolBase)` 无根入口因此也不受影响。**`Flow` 模式仅在可确定流程根锚点的入口下成立**：`Prepare(IEnumerable<ToolNode>)` 快照路径没有环境锚点，该路径下 `Flow` 模式自动回退为 `Instance` 并写日志（第三轮评审 P1-2，选择"环境锚点 + 快照退化"而非改接口签名）。
- **预览上下文继承锚点**（第三轮评审 P2-1）：`FlowContext.CreatePreviewContext()` 显式复制 `OwnerToken`——编辑器"执行测试"与正式运行属于同一流程共享；不继承则预览会落到不同池条目，"编辑副本天然配对"论证不成立。
- **"同一流程"的判定**（第三轮评审 P2-3，钉死）：`Flow` 作用域 = **同一个流程树根节点实例**，不是"同一路径文件"。同一路径的两个编辑标签页、子流程被不同父流程引用、流程重载，都不构成 `Flow` 共享；跨流程共享统一走 `Project`。
- **并发语义**：共享句柄上的推理调用在条目 `Gate` 上串行（`lock`）。语义明确为"省显存、牺牲并发"，写入参数注释与文档。`batch_size`/`device` 是句柄级参数，故进入池键——参数需求不同自然分成多个池条目，不强行共享。
- **模型文件更新与失效语义**：每次 `Run`/`Prepare` 重算池键（含 `LastWriteTimeUtc`）；键变化则换入新条目，旧条目待其余使用者引用归零后自动清除——多流程场景下各流程下次运行自然切换到新模型，不打断其他流程。`SharedDlModelCache.Invalidate(resolvedPath)` 为**标记失效**语义（第三轮评审 P2-2，钉死）：只把匹配条目置 `Stale`，不立即 `clear_dl_model` 正在使用的句柄；新 Acquire 不复用 Stale 条目；条目在 `RefCount == 0` 时才真正清除。宿主在"模型文件更新"事件里调用它实现全项目强制切换。
- **编辑器兼容**：`ModelCacheMode` 是普通工具参数，走既有属性编辑与序列化；编辑事务的工作副本 Acquire/Release 天然配对（配合"实例持有"计数规则），确认/取消都不误释放正主句柄。
- **统一接入**：三个专用工具与 `dl-infer` 通过同一个 `DlModelCachePolicy` 帮助类接入池（`Acquire`/`Release` 两处调用），键规则只有一份定义。

**验收补充**（并入 §11）：

- 非 HALCON：引用计数平衡（按实例持有：同键不重复加引用、键切换先还后取、ReleaseResources 只还一份）、不同参数组合分池、编辑副本不泄漏引用、锚点缺失与 `Prepare(IEnumerable<ToolNode>)` 快照入口回退 Instance（有日志）、`Invalidate` 标脏后新 Acquire 不复用且引用归零才清除、预览上下文继承 `OwnerToken`。
- HALCON 门禁：`Instance / Flow / Project` 三档共享行为（同根多工具一份句柄、跨根不共享、跨流程 Project 共享一份）、文件 mtime 变更触发换句柄、最后一个 Release 后显存条目清除、`Flow` 模式推理串行可重入。

## 6. DL-01 深度学习分类

### 定位

- 工具箱：`06 识别工具 / 深度学习分类`
- ID：建议 `dl-classify`
- 读取整图或 ROI 的分类结果，适合缺陷类别、工件型号、正反面、颜色/状态等任务。

### 输入与参数

- 图像（必填）。
- ROI（可选）。
- `ModelFilePath`
- `Device`
- `BatchSize`
- `MinScore`
- `TopK`（默认 1）
- `ExpectedClass`（可选，只做比对输出）

### 输出

- `ClassId`
- `ClassName`
- `Score`
- `TopClassIds`
- `TopClassNames`
- `TopScores`
- `MatchedExpected`
- `Found`

### 开发方法

- 分类推理统一走 `ApplyDlModel`（已按第二轮评审 3-2 钉死）：`ApplyDlClassifier` 接收旧 CNN 分类器句柄（`DLClassifierHandle`），与 `.hdl` 的 `DLModelHandle` 不通用，不保留备选分支；探测阶段仅做一次不兼容验证并记录错误码。
- `ExpectedClass` 只影响 `MatchedExpected`，不改变主结果。
- `TopK` 超过实际类别数时自动截断，不报错。

## 7. DL-02 深度学习检测

### 定位

- 工具箱：`06 识别工具 / 深度学习检测`
- ID：建议 `dl-detect`
- 对整图或 ROI 内目标输出位置、类别和分数，适合作为深度学习版目标定位入口。

### 输入与参数

- 图像（必填）。
- ROI（可选）。
- `ModelFilePath`
- `Device`
- `BatchSize`
- `MinScore`
- `MaxDetections`（0 = 全部）
- `OutputContourMode`（Rectangle / Region / Xld，待探测后定）

### 输出

- `Boxes`（对象数组，输出最小外接矩形信息）
- `Regions`
- `Contours`
- `ClassIds`
- `ClassNames`
- `Scores`
- `Count`
- `Found`
- `BestClassName`
- `BestScore`

### 开发方法

- 先以官方 `detect_pills.hdl` 为主验证模型输出结构，确定检测结果 dict 中的键名、坐标语义和类别映射方式。
- 如果 HALCON 直接返回矩形参数，工具内负责同步生成 Region / XLD，保证下游流程不用再做一次对象转换。
- `MaxDetections = 0` 约定为不过滤；大于 0 时按分数排序后截断。

## 8. DL-03 深度学习分割

### 定位

- 工具箱：`06 识别工具 / 深度学习分割`
- ID：建议 `dl-segment`
- 输出像素级分类结果，适合前景分割、区域分类、复杂纹理区域提取。

### 输入与参数

- 图像（必填）。
- ROI（可选）。
- `ModelFilePath`
- `Device`
- `BatchSize`
- `MinScore`
- `OutputMode`（Region / MaskImage / Both）
- `ClassFilter`（可选，CSV）

### 输出

- `MaskImage`
- `Regions`
- `ClassIds`
- `ClassNames`
- `Areas`
- `Count`
- `Found`

### 开发方法

- 优先确认 HALCON 分割模型的结果是类别图、概率图还是 dict 中的区域描述，再决定 `MaskImage` 的内部表示。
- 第一版优先输出“每类一个区域”，保持与现有 `ColorSegmentTool` 的消费方式一致。
- 若概率图可取得，后续再增加置信图输出，不放在第一版必做范围。

## 9. 开工前必须完成的探测

当前最大的技术风险不是 WPF 或流程集成，而是 HALCON 深度学习结果结构在 .NET 侧的实际落点。正式写代码前，必须补一轮探测，结论写回本计划。

### 9.1 必测接口

| 接口 / 主题 | 要确认什么 |
|---|---|
| `ApplyDlModel` | 输入签名、返回结构、dict 键名、不同模型类型下的差异 |
| 样本构造与预处理 | 22.11 无 `preprocess_dl_samples` / `gen_dl_sample`（第二轮评审 3-1 已核实）：`create_dict` + `set_dict_object(image,'image')` 手工组 sample 是否可行、必填键；输入图尺寸 ≠ 模型 `image_width/height` 时的行为（报错/内部缩放/坐标映射）；灰度/三通道/byte 类型要求；若需手工 `zoom_image_size`，检测框坐标是原图还是网络输入图坐标 |
| `ApplyDlClassifier` | 仅需一次性验证：其接收 `DLClassifierHandle`（旧 CNN 分类器体系）与 `.hdl` 的 `DLModelHandle` 不通用，分类钉死走 `apply_dl_model`（第二轮评审 3-2，记录报错码即可） |
| 分类结果结构 | 类别 ID、类别名、分数、TopK 是否可直接取得 |
| 检测结果结构 | 框坐标格式、类别字段、分数字段、目标顺序、最大数量控制 |
| 分割结果结构 | 掩膜图类型、类别图编码、是否直接给区域 |
| ROI 策略 | ReduceDomain 是否影响深度学习推理，坐标回写是否需要偏移 |
| 设备行为 | GPU / CPU 的可用性与失败信息是否可稳定转中文；记录本机有无可用 GPU |
| 批量维度 | `BatchSize > 1` 但单图输入时的 HALCON 行为 |

### 9.2 探测输出要求

- 形成临时探针代码并删除，不把试验代码留在主工程。
- 记录模型文件、输入图、返回 dict 键名和示例值。
- 记录所有不支持或与 HDevelop 文档不一致的点。
- 若某种模型只能走某条接口，计划中直接钉死，不再做“自动猜测”。

## 10. 兼容性要求

- 已上线的 `dl-infer` ID、类型名和公共输出语义保持不变。
- 第一阶段公共运行时继续可单独使用，不因为新增专用工具而废弃。
- 新工具默认值必须保证“只填模型和图像就能跑通官方示例模型”的最短路径。
- `ModelCacheMode` 默认 `Instance`，共享功能为显式开启，不影响任何既有流程的加载与释放行为。
- 仅测试项目可引入额外的辅助依赖，主项目不得新增与推理结果可视化无关的外部库。

## 11. 验收

- DL-01：对官方分类模型和示例图像，分类结果正确，`TopK` 与 `ExpectedClass` 行为正确。
- DL-02：对官方检测模型和示例图像，检测数量、类别、分数合法，叠加框位置与 HALCON 示例观感一致。
- DL-03：对官方分割模型和示例图像，输出每类区域，面积统计稳定，分割区域与示例结果一致。
- 三类工具都要覆盖：
  - 工具箱登记。
  - 持久化 ID。
  - 配置校验。
  - 至少一个真实 HALCON 官方模型用例。
- 编辑器 UI 的叠加预览与结果列表做人审，不只看单测。

## 12. 开发顺序建议

1. **先补探测节**：把 `ApplyDlModel` / 预处理与 DLSample 构造链路 / 分割输出结构彻底钉死（含第二轮评审 3-1：22.11 无 `preprocess_dl_samples`，样本构造与输入尺寸行为必须实测）。
2. **第一批做 DL-02 检测**：现有第一阶段已用 `detect_pills.hdl` 验证过真实模型预热，衔接最顺；与本期同步落地 `SharedDlModelCache` / `DlModelCachePolicy` / `ModelCacheMode`（§5.4，含 `FlowContext.OwnerToken` 框架小改），三个后续工具直接复用。
3. **再做 DL-01 分类**：输出结构相对简单，可复用更多公共协议。
4. **最后做 DL-03 分割**：像素级输出和可视化复杂度最高，放最后更稳。

## 13. 当前评审结论摘要

- 这条线现在不缺“是否要做”，缺的是“先把接口探测钉死，再进入编码”。
- 当前统一运行时方案方向正确，不建议推倒重来。
- 第二阶段不建议继续扩展 `DeepLearningInferenceTool` 一个类，而应新增分类 / 检测 / 分割专用工具。


## 14. 算子探测结论（D0 实测，2026-10-09）

**探测环境**：HALCON 22.11 Steady；本机 `query_available_dl_devices` 实测 **GPU 1 个、CPU 1 个**（GPU 路径可在本机真实验证）。模型：官方 `detect_pills.hdl`（检测）、`classify_pill_defects.hdl`（分类）、`segment_pill_defects.hdl`（分割）；图像：`%HALCONIMAGES%\pill_bag\pill_bag_001.png`、`%HALCONIMAGES%\pill\ginseng\contamination\pill_ginseng_contamination_001.png`。探测代码为临时探针工程，用完已删。

### 14.1 样本构造与预处理（本轮最大风险点，已闭合）

| 探测点 | 结论 |
|---|---|
| 样本构造 | `create_dict` + `set_dict_object(image, sample, "image")` 手工构造可行；缺 `'image'` 键报 **#7783**（DL: Inputs missing in input dict） |
| 预处理算子 | 22.11 的 HalconDotNet **确认无** `preprocess_dl_samples` / `gen_dl_sample`；必须手工预处理 |
| 手工预处理配方（实测可推理） | ① `convert_image_type(img,'real')`；② 按模型参数 `image_range_min/max` 线性缩放（`scale_image`，0..255 → [min,max]）；③ `zoom_image_size` 到模型 `image_width/image_height`。检测模型 CPU 单次推理约 0.4 s |
| 类型要求 | 直接喂 byte 图报 **#9001**（wrong gray value type）；灰度图喂三通道模型报 **#3359**（通道数不符）——类型与通道校验必须在工具内做并转中文 |

### 14.2 三类结果结构（`.hdl` 统一走 `apply_dl_model`，已钉死）

| 任务 | 结果 dict 键 | 结构说明 |
|---|---|---|
| 检测 `detect_pills` | `bbox_row1` / `bbox_col1` / `bbox_row2` / `bbox_col2` / `bbox_class_id` / `bbox_class_name` / `bbox_confidence` | 全部为等长数组（每目标一组）；**已按置信度降序**；坐标为**预处理后的网络输入图坐标系**（512×320），工具必须按比例缩放回原图坐标；类别名直接给出，无需查表 |
| 分类 `classify_pill_defects` | `classification_class_ids` / `classification_class_names` / `classification_confidences` | 全部类别（本例 3 个）、**已按置信度降序**——TopK 直接取前 K 个即可；`classification_class_ids` 与模型 `class_names` 下标一致 |
| 分割 `segment_pill_defects` | `segmentation_image` / `segmentation_confidence` | 两者都是图像对象（用 `get_dict_object` 取，`get_dict_tuple` 报 #1302）；`segmentation_image` 为模型输入尺寸单通道图，**像素值 = 类别 ID**（实测该图含 0/1 两类）；逐类区域 = `threshold(seg, v, v)`；`segmentation_confidence` 为同尺寸 real 逐像素置信图。掩膜同样需缩放回原图尺寸 |

### 14.3 ROI / 批量 / 设备 / 句柄体系

| 探测点 | 结论 |
|---|---|
| ROI（`reduce_domain`） | **对深度学习推理无效**：限定半图后结果与整图逐值一致（HALCON 忽略图像定义域）——第一版 ROI 必须"先裁剪后推理"（`crop_domain`/`crop_rectangle1`），框坐标经缩放后**加裁剪原点偏移**回原图 |
| `BatchSize>1` 单样本 | 不报错、结果与 batch=1 一致（模型参数层面保留 BatchSize 无风险） |
| 多样本 | 传入样本元组 → 结果为**逐样本 dict 元组**，每样本键结构相同（留作后续批量扩展，第一版不做） |
| 设备 | 本机 GPU/CPU 各 1；`Auto` 先试 GPU 回退 CPU 的策略与第一阶段代码一致，可本机验证 GPU 路径 |
| `apply_dl_classifier` | 收 `DLModelHandle` 报 **#2404**（Invalid handle type）——第二轮评审 3-2 钉死成立，分类只走 `apply_dl_model` |

### 14.4 由探测确定的实现约定

1. **公共预处理帮助方法**（D1）：`PreprocessForDlModel(image, model)` 实现 14.1 配方三步，类型/通道不符时抛中文错误（#9001/#3359 包装）。
2. **DL-02 检测**：结果直取 7 键；坐标缩放回原图（因子 = 原图/网络输入）；`MaxDetections` 按置信度截断（结果已排序）；`Regions` 由框 `gen_rectangle1` 生成、`Contours` 由 `gen_rectangle1_contour_xld` 生成。
3. **DL-01 分类**：TopK = 结果数组前 K 项截断；`ExpectedClass` 只比对 `ClassName`/`ClassIds`。
4. **DL-03 分割**：逐类 `threshold` 得区域（顺序 = 模型 `class_names`，与 `ColorSegmentTool` 消费方式一致）；`RejectedRegion` 语义参考颜色分割（置信图低于阈值处/未出现类）——具体过滤策略实现期定；掩膜缩放回原图尺寸输出 `MaskImage`。
5. 所有专用工具共享 14.1 预处理与样本构造代码，错误码 #7783/#9001/#3359 统一转中文。


## 15. 实现与验收记录（2026-10-09）

### 实现范围

- **D1 公共层**：`SharedDlModelCache`（引用计数池，按实例持有、归零即清、Stale 只控复用）+ `DlModelCachePolicy`（键规则唯一出处）+ `DeepLearningToolShared`（路径/设备/预处理/样本构造/中文错误包装）；框架锚点 `FlowContext.OwnerToken`（Run 写入、`CreatePreviewContext` 继承）、`FlowResources` 环境锚点（仅 root 重载，try/finally）；`dl-infer` 加 `ModelCacheMode`（默认 Instance 行为不变）。
- **DL-02 检测** `dl-detect`：三档缓存 + ROI 裁剪（reduce_domain 对 DL 无效，探测结论）+ 预处理 + 框坐标缩放回原图 + MinScore/MaxDetections；`Contours` 用 `gen_rectangle1` + `gen_contour_region_xld`（22.11 无 `gen_rectangle1_contour_xld`）。
- **DL-01 分类** `dl-classify`：TopK 直取已降序结果、ExpectedClass 比对、MinScore 先过滤再 TopK。
- **DL-03 分割** `dl-segment`：逐类 threshold 区域（顺序=模型类别序，与 ColorSegmentTool 消费一致）、MaskImage 与 Regions 严格一致、双层 MinScore（区域均值 + 像素置信）、RejectedRegion 与颜色分割同语义。
- 三个专用编辑器（最小闭环：参数 + 执行测试 + 叠加预览）；登记/图标/路由齐备。
- 示例流程（批审 3.1 补齐）：`dl-detect-pills` / `dl-classify-pill-defects` / `dl-segment-pill-defects` 三个 `.vflow.json`（loadimage 内置 `%HALCONIMAGES%` 图像、模型 `%HALCONEXAMPLES%` 环境变量路径，打开即跑），配套 `DeepLearningExampleFlowTests` 真实推理回归。
- 实现期修复：`DlClassifyTool` Instance 缓存键未赋值导致每次 Run 重载（D4 评审发现，已修并回归）。

### 验证

- 构建 0 错误；全量 `dotnet test --filter "Category!=Soak"`：**1399 通过、0 失败**（含新增：D1 池/锚点 17、D2 检测 11、D3 分类 11、D4 分割 12、框架锚点与既有 1348）。Soak 未跑（与既有批次口径一致）。
- HALCON 门禁全部真实推理官方模型（detect_pills / classify_pill_defects / segment_pill_defects），与直接 HALCON 调用逐值对照一致。
- 已知边界（22.11）：无 `preprocess_dl_samples`（手工预处理配方见 §14.1，已封装）；`clear_dict` 不存在，样本/结果字典交由 HALCON 运行时回收；GPU 可用（本机 1 卡），Auto 先试 GPU 回退 CPU。
- UI 人工验收项：三个新编辑器与 dl-infer 编辑器的叠加预览、共享模式切换后的工具箱行为。
