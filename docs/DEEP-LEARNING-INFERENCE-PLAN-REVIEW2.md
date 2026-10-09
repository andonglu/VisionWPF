# DEEP-LEARNING-INFERENCE-PLAN 评审意见（第二轮）

评审日期：2026-10-09
评审对象：`docs\DEEP-LEARNING-INFERENCE-PLAN.md`、`docs\DEEP-LEARNING-INFERENCE-PLAN-REVIEW.md`
评审方式：与代码核对（`VisionFlow.Tools\Tools\DeepLearningTools.cs`、`VisionFlow.Tests\DeepLearningInferenceToolTests.cs`、`ToolboxRegistry` / `BuiltinToolIdentities` 登记）+ HALCON 22.11 本机安装实测（算子导出、官方模型文件）。
评审结论：**同意第一轮评审结论——方案方向正确，先探测后编码，顺序"检测 → 分类 → 分割"。第一轮评审的 P1 全部成立。但探测清单漏掉了比"结果 dict 结构"更前置的一项：预处理与 DLSample 构造链路（22.11 的 HalconDotNet 无 `preprocess_dl_samples` / `gen_dl_sample`），必须补为硬性探测项；另有两处可直接钉死、不必留到探测期。**

## 1. 已核实的前提（与代码/本机实测核对）

| 项 | 结论 |
|---|---|
| 第一阶段工具真实可用 | `DeepLearningInferenceTool`（448 行）已实现模型读取、类型规范化、设备策略（Auto 优先 GPU 回退 CPU）、batch / optimize 参数、公共元数据、缓存 + 预热 + 释放、中文校验（`DeepLearningTools.cs:64-447`） |
| 登记已打通 | `dl-infer` 已在 `BuiltinToolIdentities.cs:91` 与 `ToolboxRegistry.cs:226`（"06 识别工具 / 深度学习推理"）登记 |
| 三类官方模型齐备 | 本机 HALCON 示例自带 `classify_pill_defects.hdl`（分类）、`detect_pills.hdl`（检测）、`segment_pill_defects.hdl`（分割），探测与验收的模型、图像都有真实来源（`%HALCONEXAMPLES%\hdevelop\Deep-Learning\...`） |
| `ApplyDlClassifier` 存在于 22.11 | 签名 `ApplyDlClassifier(HObject, HTuple, out HTuple)`，但它接收的是 **DLClassifierHandle**（旧 CNN 分类器体系），与 `read_dl_model` 的 **DLModelHandle** 不通用（见 3-2） |
| 预处理算子缺失 | 22.11 的 `halcondotnet.dll` 导出中**没有** `preprocess_dl_samples`，也没有 `gen_dl_sample` 等 DLSample 构造算子（安装目录 XML 已核实） |

## 2. 对第一轮评审意见的评价

- P1-1（先补结果结构探测）：成立，同意为硬性前置。
- P1-2（公共运行时与业务层边界）：成立，且代码现状与该边界一致（`dl-infer` 目前确实只输出公共元数据），保持即可。
- P1-3（ROI 语义先定）：成立。同意第一版采用"裁剪/reduce_domain 后推理、结果映射回原图坐标、矩阵跟随不进第一版"。
- P1-4（第一版只做单图）：成立，`BatchSize` 仅作模型运行参数保留。
- 顺序（检测 → 分类 → 分割）：同意。检测有 `detect_pills.hdl` 预热基础，且肉眼验收最直接。

## 3. 本轮新增意见

### 3-1 【P1 新增】探测清单必须补"预处理与 DLSample 构造链路"（最大遗漏）

计划和第一轮评审都把最大风险定在"结果 dict 的 .NET 落点"，但**更前置**的一环没列：22.11 的 HalconDotNet 没有 `preprocess_dl_samples`，也没有 `gen_dl_sample`——HDevelop 示例里的 `preprocess_dl_samples(DLSampleBatch, DLModelHandle)` 辅助流程在 .NET 里**没有对应算子可调用**，必须由探测回答以下问题，否则 `apply_dl_model` 根本喂不进去：

1. sample dict 手工构造：`create_dict` + `set_dict_object(image, 'image')` 后 `apply_dl_model` 是否接受；必填键有哪些（`'image'` 之外是否要求 `'image_id'` 等）。
2. 输入图尺寸：尺寸 ≠ 模型 `image_width/image_height` 时 HALCON 的行为（报错、内部缩放、还是结果按比例映射）——这直接决定工具内要不要自己做 `zoom_image_size`。
3. 输入图类型：灰度 / 三通道 / byte 的要求与报错码。
4. 若需手工预处理：检测结果坐标是**原图坐标**还是**网络输入图坐标**；若是后者，"先裁再推理"路径的偏移回写规则要连同 3-2 一起定死。

结论写回计划第 9 节，格式沿用前几条线（探测环境 → 逐项结论含错误码 → 实现约定）。

### 3-2 【可预先钉死】`ApplyDlClassifier` 探测项缩小为一次验证

22.11 中 `apply_dl_classifier` 的句柄体系（`create_dl_classifier` / `read_dl_classifier`，CNN 分类器）与 `.hdl` 的 DLModelHandle 体系**不通用**。三个专用工具统一只读 `.hdl`，因此：

- 设计稿直接钉死：分类也走 `apply_dl_model`，不留"分类可能走 apply_dl_classifier"这条分支。
- 探测只需一次性验证 `apply_dl_classifier` 收到 DLModelHandle 时报错（预期句柄类型错误，类似 #2404），把错误码记入探测结论即可。

### 3-3 【P2】公共输出清单与代码不一致

计划 §5.3 的公共输出清单漏了 `OptimizedForInference`（`dl-infer` 代码 `:61` 已有该输出）。第二阶段定义三类工具公共输出时以代码现状为准补齐，避免"文档清单 ≠ 实际输出"的回归。

### 3-4 【P2】三个专用工具补充约定（写入计划即可，无需探测）

- 未找到策略：三个工具均实现 `INotFoundPolicy`（`FailWhenNotFound` 默认 true），空结果输出 `Found=false` + 数量 0 后走 `NotFoundOutcome.Resolve`——与识别线工具一致；计划目前只在输出里列了 `Found`，没写策略接入。
- `MinScore` 语义：检测为"低于即丢弃该目标"；分割需探测后定（按像素置信过滤还是先按像素再按区域面积过滤），写入探测项。
- 分割背景类：参考 `color-pieces-mlp` 示例引入 `background` 类的做法，计划中明确"背景类是否计入 `Count`/`Found`"的语义，避免实现期再议。

### 3-5 【P3】探测环境记录

- 官方模型与图像在 `%HALCONEXAMPLES%`（`C:\Users\Public\Documents\MVTec\...`），不在 `HALCONROOT` 下；探测结论中路径写法与本机实际位置一致。
- 记录本机 `query_available_dl_devices('runtime','gpu')` 是否有可用 GPU：没有则 GPU 用例按"预热明确失败"路径固定语义（与 Deep OCR 设备参数的处理方式一致）。

## 4. 结论

- 同意第一轮评审：**现在正确的动作是补探测（加上 3-1 的预处理链路），不是开写业务代码**。
- 3-2 可在探测开头 10 分钟钉死，省一条设计分支；3-3 / 3-4 直接写入计划正文。
- 探测完成、结论回填计划后，按"DL-02 检测 → DL-01 分类 → DL-03 分割"进入编码。
