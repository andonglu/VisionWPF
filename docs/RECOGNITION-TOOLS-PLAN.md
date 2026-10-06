# 识别补充开发计划

编写日期：2026-10-06
状态：待开发
范围：工具箱“06 识别工具”（`VisionFlow.Tools\Tools\RecognitionTools.cs`）。
HALCON 版本基线：20.11 及以上（Deep OCR 在 20.11 中可用）。
关联文档：[数据处理、判定与逻辑补充开发计划](LOGIC-DATA-TOOLS-PLAN.md)（读码结果与工单比对、字符结果判定通过变量计算和综合判定完成）、[定位匹配与几何测量补充开发计划](MATCH-MEASURE-TOOLS-PLAN.md)（差分检测 MT-06）。

## 1. 现状

只有一个工具：

| 工具 | 类型 | 算子 | 输出 |
|---|---|---|---|
| 一维码 `barcode1d` | `Barcode1DTool` | `create_bar_code_model` / `find_bar_code` | `Codes`、`FirstCode`、`Region`、`Count`、`Found` |

已支持：可选 ROI 区域、码制（默认 `auto`）、“未找到”处理。每次运行新建条码模型，不跨运行复用（模型会保存本次结果，避免并发覆盖）。

缺少：二维码、码质量评级、字符识别、颜色识别。

## 2. 拆分结论

| 编号 | 内容 | 处理方式 |
|---|---|---|
| RC-01 | 二维码（QR、DataMatrix、PDF417、Aztec、Micro QR、DotCode）、一次读多个码、码质量评级 | “一维码”扩展为“读码” |
| RC-02 | 字符识别（传统 OCR、Deep OCR） | **新建工具“字符识别”** |
| RC-03 | 颜色识别（按样本颜色判断区域颜色） | **新建工具“颜色识别”** |
| RC-04 | 颜色分割（按训练的颜色分类器逐像素分类） | **新建工具“颜色分割”** |

结果：新建 3 个工具，增强 1 个现有工具，工具箱净增 3 个入口。

拆分说明：一维码与二维码的输入（图像、ROI）和输出（码内容、码区域、数量）结构相同，按原则合并为一个工具。颜色识别输出每个区域的颜色标签，颜色分割输出每种颜色的区域，输出结构不同，分成两个工具。

## 3. 开发通用做法

与前几份计划一致：新增枚举值追加在末尾、默认值等于现有行为；新输出补 `[ToolOutput]`；新工具登记 `BuiltinToolIdentities`、注册 `ToolboxRegistry`、路由编辑窗口；模型或分类器文件路径之外的训练结果以 `byte[]` 或字符串保存在工具中；测试与 README 同步更新。

识别类工具普遍需要“用当前图像试运行”的编辑体验：编辑窗口显示图像、识别结果叠加和结果列表，参数修改后可立即重试。

## 4. RC-01 一维码扩展为读码

### 功能

- 工具箱显示名改为“读码”，ID `barcode1d` 与类型名 `Barcode1DTool` 不变。
- 新增 `CodeKind` 枚举：`Barcode`（默认，现有逻辑）/ `DataCode2D`。
- 二维码码制 `DataCodeType`：`QR Code` / `Data Matrix ECC 200` / `PDF417` / `Aztec Code` / `Micro QR Code` / `DotCode` / `GS1 QR Code` / `GS1 DataMatrix` 等（取值即 HALCON 码制名称）。
- 识别强度 `RecognitionLevel`：`standard_recognition`（默认）/ `enhanced_recognition` / `maximum_recognition`（二维码 `default_parameters`）。
- 读取数量 `MaxCodes`：0 表示全部；大于 0 时找到该数量后停止（一维码 `stop_after_result_num`，二维码 `stop_after_result_num`）。
- 质量评级 `GradeQuality`（默认 false）：一维码按 ISO/IEC 15416（`get_bar_code_result` 的 `quality_isoiec15416`），二维码按 ISO/IEC 15415（`get_data_code_2d_results` 的 `quality_isoiec15415`）。

### 输出

- 现有输出不变：`Codes`、`FirstCode`、`Region`、`Count`、`Found`。二维码的码区域由符号轮廓转换为 Region（`gen_region_contour_xld`），保持 `Region` 的含义一致。
- 新增：`CodeTypes`（数组，每个码的实际码制）、`SymbolContours`（XLD，二维码符号轮廓；一维码为空）、`Grades`（数组，总体质量等级，数值 0~4，对应 F~A）、`FirstGrade`。

### 开发方法

- 一维码路径保持“每次运行新建模型”的现有做法。
- 二维码模型创建较慢且可训练，按工具实例缓存句柄（参照 `HalconMatchToolBase` 的缓存与预热），运行时加锁串行使用，避免结果被并发覆盖；码制、识别强度、训练参数变化时重建。
- 训练：编辑窗口提供“用当前图像训练”，调用 `find_data_code_2d` 的 `train` 参数让模型适应样本，训练后的模型以 `serialize_data_code_2d_model` 字节保存在 `DataCodeModelData`；未训练时按码制和识别强度新建。
- 比对工单号、判断码内容格式不在本工具内做，用 LD-02 变量计算或 LD-04 综合判定完成。

## 5. RC-02 字符识别（新工具）

### 定位

- 工具箱：`06 识别工具 / 字符识别`，ID `ocr`，类 `OcrTool : ToolBase, INotFoundPolicy, IToolResourceLifecycle`。
- 读取生产日期、批号、序列号等印刷或喷码字符。

### 引擎

| 引擎 `Engine` | 算子 | 说明 |
|---|---|---|
| `TextModel`（默认） | `create_text_model_reader('auto', 分类器)` + `find_text` + `get_text_result` | 传统方法，自动分割字符，使用 HALCON 自带的预训练字体分类器，无需深度学习授权 |
| `DeepOcr` | `create_deep_ocr` + `apply_deep_ocr` | 深度学习方法，自动检测文字区域并识别，对字体、背景变化更稳健；需要深度学习推理授权，建议使用 GPU |

### 输入与参数

- 输入：图像（必填）；ROI 区域（可选）；定位矩阵（可选，用于把倾斜的文字区域跟随到当前位置）。
- `TextModel` 参数：
  - `Classifier`：预训练分类器名称（如 `Universal_0-9A-Z_Rej.occ`、`Industrial_0-9A-Z_NoRej.omc`、`DotPrint_0-9A-Z_NoRej.omc`，从 HALCON 安装目录的 OCR 文件中选择）。
  - `Polarity`（`dark_on_light` / `light_on_dark` / `both`）、`DotPrint`、`MinCharHeight` / `MaxCharHeight`、`MinStrokeWidth` / `MaxStrokeWidth`、`TextLineSeparators` 等常用 `set_text_model_param` 参数。
  - `Alphabet`：限制可识别字符（如只允许数字），在结果中过滤。
- `DeepOcr` 参数：`Mode`（`auto` / `recognition`，后者用于已裁好的单行文字）、`Device`（CPU / GPU）、`Alphabet`（同上）、`MinConfidence`。
- 期望格式：`ExpectedPattern`（可选，正则表达式，如 `^\d{8}$`），只用于输出 `PatternOk`，不影响识别结果。

### 输出

- `Text`（全部字符按行拼接，行之间用换行分隔）、`Lines`（数组，每行文字）、`LineCount`。
- `Chars`（数组，单个字符）、`Confidences`（数组，每个字符的置信度）、`MinConfidence`。
- `CharRegions`（每个字符的区域）、`WordContours`（Deep OCR 的文字框轮廓）。
- `PatternOk`、`Found`。

### 开发方法

- 文本模型、分类器、Deep OCR 句柄按工具实例缓存，参与预热和释放（`IToolResourceLifecycle`）；分类器文件按“名称 + 修改时间”缓存。
- 定位矩阵跟随：用矩阵把 ROI 变换到当前位置，并按矩阵角度把 ROI 内图像旋转为水平后识别，结果区域再变换回原图坐标。
- 行顺序：先按行（垂直位置）分组，再在行内按列排序，保证多行文字顺序稳定。
- `DeepOcr` 在运行环境缺少授权或深度学习运行库时，预热即报告明确的中文错误，不在生产运行中才失败。
- 编辑窗口：新建 `WpfOcrToolEditWindow`，显示识别结果叠加、每个字符及置信度，置信度低于阈值的字符标红。
- 自定义字体训练暂不在本工具内实现（见暂缓）。

## 6. RC-03 颜色识别（新工具）

### 定位

- 工具箱：`06 识别工具 / 颜色识别`，ID `color-classify`，类 `ColorClassifyTool`。
- 判断每个区域的颜色属于哪一类，例如线缆颜色、指示灯颜色、零件颜色防错。对标 VisionMaster 颜色识别。

### 输入与参数

- 输入：彩色图像（必填）；区域（必填，每个区域对象分别判断）。
- `ColorSpace`：`rgb` / `hsv` / `cielab`（默认 `cielab`，色差与人眼感受更接近）。
- `References`：参考颜色列表（字符串，每行 `名称|通道1|通道2|通道3|允许距离`），在编辑窗口中通过“在图像上框选样本”自动取均值生成。
- `UnknownLabel`：与所有参考颜色的距离都超过允许距离时的标签（默认“未知”）。

### 算法

- 对每个区域分别求各通道均值（`intensity`），在选定色彩空间中计算到每个参考颜色的欧氏距离，取最近且在允许距离内的参考颜色。

### 输出

- `Labels`（数组，每个区域的颜色名称）、`FirstLabel`、`Distances`（数组，到所选参考色的距离）、`MeanColors`（数组，每个区域的颜色均值，对象类型）、`Count`、`AllKnown`（全部区域都识别为已知颜色）。

## 7. RC-04 颜色分割（新工具）

### 定位

- 工具箱：`06 识别工具 / 颜色分割`，ID `color-segment`，类 `ColorSegmentTool`。
- 按训练的颜色分类器把图像逐像素分为若干颜色类，适合颜色复杂、阈值难以描述的场景（如多色线束、彩色印刷）。

### 算子与参数

- 训练：`create_class_mlp`（输入维度为通道数）→ `add_samples_image_class_mlp`（每类一个样本区域）→ `train_class_mlp`；或使用 `create_class_gmm` 系列（`Classifier`：`Mlp` / `Gmm`）。
- 运行：`classify_image_class_mlp` / `classify_image_class_gmm`。
- 参数：`ClassNames`（CSV）、`RejectionThreshold`（MLP 置信度或 GMM 概率阈值，低于时归为拒识）。
- 训练结果以 `serialize_class_mlp` / `serialize_class_gmm` 字节保存在 `ClassifierData`。

### 输出

- `Regions`（数组，每类一个区域，顺序与 `ClassNames` 一致）、`ClassAreas`（数组）、`RejectedRegion`、`Found`。
- 编辑窗口：为每个颜色类在图像上框选样本区域，训练后实时预览分割结果，不同类用不同颜色叠加。

## 8. 暂缓

- **字符验证（OCV）**：用 RC-02 字符识别 + LD-04 综合判定（内容比对）+ MT-06 差分检测（印刷质量）组合实现，不单独做工具。
- **自定义字体训练、Deep OCR 微调**：需要标注样本与训练流程，建议在 MVTec 工具中完成后，把分类器或模型文件交给 RC-02 使用。
- **深度学习分类、检测、分割**：另行规划为深度学习推理模块。

## 9. 兼容性要求

- “一维码”只改显示名，ID 与类型名不变；`CodeKind = Barcode` 时行为与输出完全不变。
- 新增输出不影响现有输出的含义；二维码的 `Region` 与一维码一样表示码所在区域。
- 外部分类器、Deep OCR 模型文件路径由上层项目保证有效，与 README 中外部文件的约定一致；预热时检查并报告。

## 10. 验收

- RC-01：用 HALCON 生成的合成一维码、二维码图像（各码制至少一例），内容正确；`MaxCodes` 生效；质量评级输出合法等级；`CodeKind = Barcode` 结果与旧版一致；训练后的二维码模型保存、重新加载后结果一致。
- RC-02：
  - `TextModel`：对合成的打印字符图像（如 `2026-10-06`），识别结果与真值一致；`Alphabet` 限制生效；多行顺序正确。
  - `DeepOcr`：运行环境具备授权时，对同一图像结果正确；缺少授权时预热给出明确错误。相关用例在缺少环境时明确失败，不计为通过（与现有 HALCON 用例约定一致）。
- RC-03：合成多色块图像中，每个区域的颜色标签正确；超出允许距离的颜色标为“未知”。
- RC-04：合成多色图像中，分割区域与真值的重合率大于 95%；分类器保存后重新加载结果一致。
- 全部回归测试和 `examples\*.vflow.json` 通过。

## 11. 开发顺序

1. RC-01（读码）：现场需求最常见，改动集中在现有工具。
2. RC-02（字符识别）：先做 `TextModel`，再做 `DeepOcr`。
3. RC-03（颜色识别）。
4. RC-04（颜色分割）。
