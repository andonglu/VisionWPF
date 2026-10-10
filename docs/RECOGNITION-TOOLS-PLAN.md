# 识别补充开发计划

编写日期：2026-10-06
状态：RC-01（一维码扩展为读码，分支 `feature/recognition-tools`，探测见第 12 节 A，验收见第 13 节）；RC-02（字符识别）、RC-03（颜色识别）、RC-04（颜色分割）均已实现（同分支，验收见第 13 节）。计划内各项全部完成。
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


## 12. 算子探测结论（开工前实测，2026-10-09）

**探测环境**：HALCON 22.11 Steady（`HALCONROOT=C:\Program Files\MVTec\HALCON-22.11-Steady`），C# 经 HalconDotNet 直接调用；
合成测试图用 System.Drawing 渲染 + ZXing.Net 0.16.10 生成码图（仅探针/测试用途）。探测代码为临时探针工程，用完已删。

### A. 读码（RC-01）

| 探测点 | 结论 |
|---|---|
| 码制 × 识别强度 | 8 个码制（`QR Code` / `Data Matrix ECC 200` / `PDF417` / `Aztec Code` / `Micro QR Code` / `DotCode` / `GS1 QR Code` / `GS1 DataMatrix`）× 3 档 `default_parameters`（`standard_recognition` / `enhanced_recognition` / `maximum_recognition`）全部建模型成功 |
| 合成图解码 | ZXing 生成的 QR / DataMatrix / Aztec 图 `find_data_code_2d` 解码正确；**PDF417 小图（约 300×100）解码失败，放大到约 900×300 成功**——测试用足够大的 PDF417 |
| `stop_after_result_num`（2D） | 是 `find_data_code_2d` 的 **find 时参数**，不是模型参数（`set_data_code_2d_param` 设置报 #8831）；**传 0 表示"找到 0 个即停"（结果为空）**；**更正（2026-10-09 实现期复测）**：HALCON 文档明确"不传该参数时解码 1 个即停"，22.11 实测 3 码图不传只得 1 个——所以"读取全部"（MaxCodes=0）实现为传一个足够大的上限（999），`MaxCodes > 0` 时传 MaxCodes |
| `stop_after_result_num`（1D） | `set_bar_code_param(handle, 'stop_after_result_num', N)` 有效，N=1 时截断为 1 个 |
| 2D 质量评级 | 必须**按结果句柄逐个**查：`get_data_code_2d_results(Model, Handle[i], 'quality_isoiec15415')` 返回 14 元组，与 `quality_isoiec15415_labels`（Overall Quality / Contrast / …）一一对应，**第 1 个为总体等级**，数值 0~4 或 `'N/A'`；Candidate 传 `'all'`/`'all_results'` 报 #8825/#8827 |
| find 输出结构 | `SymbolXLDs` 每码一个 XLD；`DataCodeObjects`；`ResultHandles` 每码一个句柄；解码字符串与句柄同序 |
| XLD 转 Region | `gen_region_contour_xld(SymbolXLDs, 'filled')` 输出每码一个区域，数量与码数一致 |
| 训练 + 序列化 | 训练 = find 参数 `'train'='all'`（与 `stop_after_result_num` 同为 find 时参数）；`serialize_data_code_2d_model` → byte[] → `deserialize_data_code_2d_model` 往返后解码结果一致 |
| 1D 质量评级 | find 前 `set_bar_code_param(handle, 'persistence', 1)`；`get_bar_code_result(handle, 0, 'quality_isoiec15416')` 返回 9 元组，第 1 个为 Overall Quality（0~4 或 `'N/A'`）；质量查询须在 `clear_bar_code_model` 之前 |

### B. 字符识别（RC-02）

| 探测点 | 结论 |
|---|---|
| `find_text` | 对 System.Drawing 渲染的打印体文字（Arial 约 30px）识别有效；`reduce_domain` 后输出的字符区域为**原图坐标** |
| `get_text_result` 查询 | `'class'`（全部字符）、`'confidence'`（逐字符置信度）、`'num_lines'`、`['class_line', i]`（第 i 行字符）、`'polarity'` 可用；**`'text'` 在 `'auto'` 分割方式下不可用（#8338）**——行文本由 `class_line` 拼接 |
| `get_text_object` 查询 | `'all_lines'` 返回全部字符区域（每字符一个对象，行序排列）；`['line', i]` 返回该行字符区域 |
| Deep OCR 可用性 | 本机 `create_deep_ocr('mode','auto')` 与 `apply_deep_ocr` **可用**（无需额外授权即可跑通）；结果 dict 键：`word_boxes_on_score_maps` / `word_boxes_on_image` / `words` / `score_maps` / `image`；`words` 子 dict 键：`word` / `row` / `col` / `phi` / `length1` / `length2` / `line_index` / `char_candidates` / `word_image`；`word_boxes_on_image` 为 XLD |
| Deep OCR 设备参数 | 22.11 上 `set_deep_ocr_param` 设置 `'device'`/`'detection_device'`/`'recognition_device'`（值 `'cpu'`/`'gpu'`/整数/元组）**均报 #1203**，`get_deep_ocr_param` 亦失败——本版本设备参数不可用。**实现约定**：默认不设置设备参数；用户选 GPU 时在 `Prepare()` 尝试设置，失败给出明确中文错误 |
| Deep OCR 句柄清理 | `clear_dl_model` 不接受 Deep OCR 句柄（#2404），22.11 无 `clear_deep_ocr`——句柄不可显式释放，`ReleaseResources()` 仅丢弃引用 |
| Deep OCR 置信度（2026-10-09 实现期复测） | `words` dict 无 score/confidence 键（均 #1302）；逐字符置信度从 `words.char_candidates[词][0][字符]` 字典的 `'confidence'` 元组第 1 项取得（实现期探针：清晰印刷体约 0.998~0.9999）；`set_deep_ocr_param` 对 `'recognition_confidence_threshold'`/`'alphabet'` 等参数同样报 #1302——`ConfidenceThreshold`/`Alphabet` 只能做结果侧过滤；`word_boxes_on_image` 在 HalconDotNet 中无法从 dict 元组还原为 HObject（CopyObject 失败），文字框轮廓改由 `words` 的 row/col/phi/length1/length2 用 `gen_rectangle2_contour_xld` 生成 |
| 文本模型区域拼接（2026-10-09 实现期复测） | `get_text_object('all_lines')` 每字符一个对象、行序排列、原图坐标；行过滤须用**全局**字符序号 select_obj；`gen_empty_region` 会产生 1 个空区域对象（作 concat 种子会让输出多 1 个对象），区域累加须用 `gen_empty_obj` 作种子；空图 `find_text` 正常返回 num_lines=0（不抛异常） |
| 分类器文件 | `create_text_model_reader('auto', <name>)` 使用 HALCON 自带分类器（如 `Universal_0-9A-Z_Rej.occ`、`Industrial_0-9A-Z_NoRej.omc`），按名称加载 |

### C. 颜色识别（RC-03）

| 探测点 | 结论 |
|---|---|
| 逐区域均值 | `intensity(Regions, Channel, Mean, Deviation)` 对多对象区域逐对象返回均值元组，可直接算各通道均值 |
| 色彩空间转换 | `trans_from_rgb(R, G, B, o1, o2, o3, Format)` 有效取值：`'hsv'` / `'cielab'` / `'cieluv'`（`'lab'` 无效 #1301）；先 `decompose3` 得三单通道再转换；转换后各通道同样可 `intensity` 取均值 |

### D. 颜色分割（RC-04）

| 探测点 | 结论 |
|---|---|
| 建分类器 | `create_class_mlp(3, NumHidden, NumClasses, 'softmax', 'normalization', 3, RandSeed, Handle)`；GMM：`create_class_gmm(3, NumClasses, 1, 'full', 'none', 3, RandSeed, Handle)` |
| 加样本 | `add_samples_image_class_mlp(Image, Regions, Handle)`：**Regions 对象数必须等于类别数**（不等报 #1502），第 i 个对象为第 i 类样本；GMM 侧多一个百分比参数（实测试传 100.0） |
| 训练 | `train_class_mlp(Handle, 200, 1.0, 0.01, out Error, out ErrorLog)`；GMM：`train_class_gmm(Handle, 100, 0.001, 'training', 1, out Centers, out Iter)` |
| 逐像素分类 | `classify_image_class_mlp(Image, out ClassRegions, Handle, RejectionThreshold)`：输出**每类一个区域，顺序 = 训练类序**；阈值越大越严格（0 时背景等低置信像素也会归类，0.9 时仅高置信像素）；**拒识像素不进任何类** → `RejectedRegion` = 图像定义域 − `union1(ClassRegions)`。GMM 的 `classify_image_class_gmm` 同构 |
| 序列化 | `serialize_class_mlp` / `deserialize_class_mlp`（GMM 同）byte[] 往返后分类结果一致 |

### 由探测确定的实现约定

1. RC-01：`MaxCodes = 0` 时 find 不传 `stop_after_result_num`（传 0 会得到空结果）；2D 模型参数变化（码制/识别强度/训练数据）时按实例缓存句柄重建；质量等级取各质量查询元组第 1 项（0~4，F~A），`'N/A'` 时输出 NaN。
2. RC-02：`Text` 输出由 `class_line` 逐行拼接（`'text'` 查询不可用）；Deep OCR 设备参数不可用时按上表约定在预热报错；句柄不显式释放。
3. RC-03：默认 `cielab`，欧氏距离 + 允许距离阈值。
4. RC-04：`Regions` 顺序与 `ClassNames` 一致；`RejectedRegion` 用定义域差集计算；MLP/GMM 均实现，序列化字节存工具属性。


## 13. 实现与验收记录（2026-10-09，分支 `feature/recognition-tools`）

### 实现范围

- **RC-01 读码**（`Barcode1DTool` 扩展，ID `barcode1d` 不变）：新增 `CodeKind`（`Barcode` 默认 / `DataCode2D`）、`DataCodeType`、`RecognitionLevel`、`MaxCodes`、`GradeQuality`、`DataCodeModelData`；新输出 `CodeTypes` / `SymbolContours` / `Grades` / `FirstGrade`；2D 模型按实例缓存（键=码制+识别强度+训练数据引用），实现 `IToolResourceLifecycle` / `IToolConfigurationCheck` / `IToolParameterVisibility`；编辑窗口支持"用当前图像训练"。工具箱显示名"一维码"→"读码"（`ToolboxRegistry`，ID 与类型名不变）。
- **RC-02 字符识别**（`OcrTool`，ID `ocr`）：`Engine` = `TextModel`（默认）/ `DeepOcr`；TextModel 全参数（Classifier / Polarity / DotPrint / 字符高度与笔画宽 / 行分隔符 / Alphabet）、DeepOcr（Mode / Device / Alphabet / `ConfidenceThreshold`——参数原名 MinConfidence 与输出冲突，改名并注明）；输出 `Text` / `Lines` / `LineCount` / `Chars` / `Confidences` / `MinConfidence` / `CharRegions` / `WordContours` / `PatternOk` / `Found`；定位矩阵跟随复用 `FollowMatrixResolver`（VF-03 语义，单位阵/平移/旋转 20° 实测回变误差 <0.1px）；句柄缓存 + 预热；`WpfOcrToolEditWindow` 低置信字符标红。
- **RC-03 颜色识别**（`ColorClassifyTool`，ID `color-classify`）：`ColorSpace`（Rgb / Hsv / Cielab 默认）、`References`（`名称|c1|c2|c3|允许距离`）、`UnknownLabel`；输出 `Labels` / `FirstLabel` / `Distances` / `MeanColors`（1×1 三通道图像数组）/ `Count` / `AllKnown`；编辑窗口支持框选样本自动取均值。
- **RC-04 颜色分割**（`ColorSegmentTool`，ID `color-segment`）：`Classifier` = Mlp（默认）/ Gmm，`ClassNames` CSV、`RejectionThreshold`、`ClassifierData`；`Train(ctx, image, classRegions)` 训练并序列化；输出 `Regions`（顺序=类名序）/ `ClassAreas` / `RejectedRegion`（定义域−类区域并集）/ `Found`；编辑窗口类管理 + 框选样本 + 训练后实时预览。
- 登记：`BuiltinToolIdentities` + `ToolboxRegistry`（06 识别工具：读码→字符识别→颜色识别→颜色分割）；图标 `ToolIcons.xaml` 4 个；路由 4 个专用窗口分支；README 工具表更新。
- 测试依赖（仅 `VisionFlow.Tests`，使用方已批准）：`ZXing.Net 0.16.10`（合成码图）、`System.Drawing.Common 9.0.0`（渲染文字/色块图）。

### 实现期探测修正（已回填第 12 节）

- `stop_after_result_num`：2D 在 22.11 上**不传参数只返回 1 个**（HALCON 文档明示"不指定时找到一个即停"，初测有误）；实现为恒传——`MaxCodes>0` 传 MaxCodes，`=0` 传 999。1D 不传=读全部，维持只在 >0 时设置。
- 2D 实际码制查询：`'symbology'` 不存在（#8831），改用 `get_data_code_2d_param(model,'symbol_type')`；1D `'decoded_types'` 存在。
- 1D 质量查询须 find 前设 `'persistence'=1`（事后设报 #8729）；2D 质量为混合元组（含 `'N/A'`），第 1 项为总体等级。
- Deep OCR：22.11 上 `set_deep_ocr_param` 对所有参数报 #1302/#1203，故 `ConfidenceThreshold` / `Alphabet` 为结果侧过滤；逐字符置信度取 `words.char_candidates[词][0][字符]['confidence']` 第 1 项；`word_boxes_on_image` 的 XLD 无法从 dict 还原，`WordContours` 由 `gen_rectangle2_contour_xld` 按词盒参数生成；`CharRegions` 按词盒均分近似。
- MLP 低拒识阈值（0.5）时白底会被外推归类，重合率用例用 0.9（测试注释已说明语义）。

### 验证（2026-10-09，本机 HALCON 22.11）

- 构建：0 错误（仅存量警告）。
- 全量 `dotnet test --filter "Category!=Soak"`：**1320 通过、0 失败、0 跳过**（基线 1254 + 新增 66）；非 HALCON 口径 1171；门禁（`Requires=HALCON`）149。Soak 未跑（与既有批次口径一致）。
- 新增用例：`RecognitionToolsBatch1Tests` 18（RC-01）、`RecognitionToolsBatch2Tests` 21（RC-02）、`RecognitionToolsBatch3Tests` 21（RC-03/04）、`RecognitionEditorUiTests` 6（图标/路由/构造约定）+ `ToolBehaviorTests` 调整 1 处（`ColorClassifyTool` 无 `Found` 输出，未找到信号为 `Count=0`，按规格豁免）。

### 已知边界与遗留

- 4 个编辑窗口的 UI 交互（框选、叠加渲染、深/浅色主题观感）需人工验收，清单见提交说明。
- Deep OCR 设备参数（GPU）在 HALCON 22.11 不可用：选 GPU 时预热报中文错误（实测路径有测试覆盖）。
- 已新增 4 个识别示例流程：`datacode-default-settings.vflow.json`、`ocr-expiration-date.vflow.json`、`color-fuses-classify.vflow.json`、`color-pieces-mlp.vflow.json`；流程内置 `loadimage`，默认使用 `%HALCONIMAGES%` 路径，`图像加载` 编辑窗支持通过 `HALCON 示例` 打开按目录分组的示例图像浏览窗口并记录最近使用，主编辑窗顶部提供最近图像快捷入口，支持固定/取消固定与清空最近记录。


## 14. 读码高级参数与多图训练（2026-10-10，分支 `main`）

### 探测结论摘要（2026-10-10 实测 HALCON 22.11）

- 2D 模型参数（`set_data_code_2d_param`，建模后设置）：可用清单按码制不同，经 `query_data_code_2d_params(model, 'set_model_params')` 可查（QR 29 个、DataMatrix 37 个）。共有常用项：`polarity`、`mirrored`、`strict_model`、`timeout`、`small_modules_robustness`、`module_size`/`module_size_min/max`、`symbol_size_min/max`、`contrast_min`、`string_encoding` 等。参数名非法报 #8831，值非法报 #8835（如 `module_size=small`）。
- 2D find 参数在 22.11 不能枚举（`'set_find_params'` 查询报 #8831），已支持的 find 参数就是现有的 `stop_after_result_num` 与训练用 `train`，不再开放其它 find 参数。
- 1D 模型参数（`set_bar_code_param`，每次运行新建模型后设置）：`query_bar_code_params(model, 'all')` 得 26 个（`element_size_min/max`、`meas_thresh`、`orientation`、`timeout`、`contrast_min`、`small_elements_robustness` 等）。**值类型敏感**：数值参数必须传数值，传字符串报 #1203——实现按值文本自动推断（整数 → int，含小数点/e → double，其余字符串）。
- 训练：`find_data_code_2d` 的 `'train'='all'` 在同一句柄上逐张累积；ZXing 同尺寸合成图训练后序列化恒为 178 字节且内容一致（训练收敛值相同），不同模块尺寸样本训练内容不同。

### 实现

- `Barcode1DTool.BarcodeParams` / `Barcode1DTool.DataCodeParams`（各为 string，多行 `名称=值`，可空 = 不设置）：按读码方式分开保存的两个高级参数列表，解析跳过空行与 # 注释行，非法行在 `CheckConfiguration` 给中文错误（注明列表归属 + 行号 + 内容）。应用时机：2D `DataCodeParams` 在 `LoadDataCodeModel` 创建/反序列化句柄后、进缓存前统一应用（句柄随缓存键隔离）；1D `BarcodeParams` 在每次 `create_bar_code_model` 后应用——切读码方式只对应当前侧列表生效，对侧残留参数不会被应用。2D 缓存键加入 `DataCodeParams`（1D 模型每次新建无需入键）；清空 `DataCodeParams` → 键变化 → 重建默认模型。参数应用失败包装为中文错误："高级参数第 N 行「名称=值」设置失败：HALCON 错误 #code"（#8831 附"该码制/模型不支持此参数名"，#8835/#1203 附"参数值非法"）。旧字段 `ModelParams` 已删除（功能上线首日即拆分）：FlowSerializer 对未知字段忽略并给兼容性警告，不报错。
- 多图训练 API：`TrainDataCodeModel(FlowContext, IEnumerable<HObject>)`，同一锁内逐张 `find('train','all')`，全部完成后序列化一次、更新键、日志记录图像数；原单图方法保留并转调多图版。
- 编辑窗口（`WpfBarcodeToolEditWindow`）"高级"折叠区（默认折叠）：模型参数表格（名称/值/说明摘要/删除）+ `HalconParamPicker` 候选选择器（scope 随读码方式与二维码码制切换，数据源 `HalconParamCatalog`）+ 当前模式对应的多行文本框（与表格双向同步；切读码方式时两侧列表内容各自暂存、切回恢复）+ 训练图像列表（"（当前图像）"或文件路径；添加当前图像 / 添加文件…多选 / 移除 / 清空 / 训练全部）。训练全部在工作副本上按序调多图 API，完成后显示训练数据字节数；沿用窗口既有约定（训练产物窗口字段 + 确定时写回）。
- 兼容性：旧流程无 `BarcodeParams` / `DataCodeParams` 属性 = 默认空，行为不变（序列化往返与历史文件缺省有测试覆盖）。

### 验证（2026-10-10，本机 HALCON 22.11）

- 构建 0 错误；`RecognitionToolsBatch*` 69 通过（新增 `RecognitionToolsBatch4Tests` 8 个：2D/1D 高级参数生效、#8831/#8835 中文错误、参数变化触发重建、多图训练一次性/分批、格式校验、序列化往返）。
