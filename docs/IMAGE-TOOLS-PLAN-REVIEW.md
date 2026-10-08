# IMAGE-TOOLS-PLAN 评审意见

评审日期：2026-10-08
评审对象：`docs\IMAGE-TOOLS-PLAN.md`（IP-01 ~ IP-08，共 8 项）
评审方式：与 main（`ccabebc`）当前代码核对（`ImageTools.cs` 的 `MeanImageTool` / `AddSubImageTool`、`WpfToolEditorRouter.IsVisualPreviewTool`、`WpfGenericToolEditWindow` 参数显隐、`ParameterPanelBuilder` 枚举渲染、`BuiltinToolIdentities` 固定 ID 机制、标定线三批的探测与验收先例）
评审结论：**可以开工。计划拆分合理、兼容约束清楚，但缺算子探测节（P1-1），必须"先探测、后实现"；本批只做 IP-01（图像滤波）+ IP-03（灰度增强），与计划第 9 节顺序一致。**

## 1. 总体评价

- 拆分依据正确：IP-01 改变局部邻域、IP-03 只改变灰度映射，分成两个工具避免单工具选项超过 20 个，与 Region / XLD 线的拆分原则一致。
- 兼容约束完备：`mean-image` / `add-sub-image` 只改显示名，ID 与类型名不变，`Method = Mean` 默认等于现有行为，历史流程不受影响的写法与前几线一致。
- 编辑窗口零额外开发已核实：`WpfGenericToolEditWindow`（视觉预览窗口的基类）已实现"按工具声明只显示当前方式用到的参数、切换枚举参数时立即刷新"（`WpfGenericToolEditWindow.xaml.cs:68`），侧栏 `ParameterPanelBuilder` 同样支持 `IToolParameterVisibility` 并把枚举渲染为下拉（`ParameterPanelBuilder.cs:776`）。计划里"编辑窗口按方式显示参数；Gauss 的 Size 用下拉框"只要参数建模正确（见 P1-2）即自动满足。
- 缺口：全文没有任何 HALCON 探测节，而 IP-01 / IP-03 涉及 16 个算子的参数细节（值域、顺序、报错码、多通道行为），其中多个计划的写法是凭文档记忆（如 gray_*_rect"参数顺序为高、宽"、gamma_image 的 `Encode`），前几线的经验是**这些细节必须以 22.11 实测为准**。

## 2. P1：开工前必须处理

### P1-1 补算子探测节，先探测后实现（硬性）

在计划中新增探测节（沿用标定线第 11 / 13 / 15 节格式），以下每一项都实测后再写代码，结论与源引用写入计划：

| 算子 | 要探测什么 |
|---|---|
| `gauss_filter` | `Size` 合法值集合（仅 3/5/7/9/11 还是任意奇数）、偶数 / 越界时报错码 |
| `median_image` | `MaskType`（circle/square）与 `Radius` 值域、`Margin` 合法值全集（mirrored/continued/cyclic/数字）、各组合报错行为 |
| `smooth_image` | `Filter` 各取值（deriche1/deriche2/shen/gauss）对 `Alpha` 的约束与报错 |
| `bilateral_filter` | 参数名与值域（`SigmaSpatial` / `SigmaRange` 能否为 0 或很大）、引导图 = 输入图自身时的行为 |
| `sobel_amp` | `FilterType` 全集（计划写了"等"，成员列表必须钉死）、`Size` 奇数约束 |
| `gray_erosion_rect` / `gray_dilation_rect` / `gray_opening_rect` / `gray_closing_rect` | 参数顺序实测确认（计划注"高、宽"，须验证）、宽高下限 |
| `emphasize` | `Factor` 值域（HALCON 文档 0.7~1.9？）、越界报错 |
| `gamma_image` | `Encode` 参数的形态（'true'/'false' 字符串？）、各参数值域 |
| `min_max_gray` | 签名与空 Region / 空定义域行为；`Percent` 范围（0~50？） |
| `scale_image_max` | 对 byte / uint2 / real 的分别行为；空定义域行为 |
| `equ_histo_image` | 非 byte 图像（uint2 / 三通道）是报错还是自动转换，报错码 |
| `rgb1_to_gray` | 单通道输入的报错码（用于中文错误映射） |
| `convert_image_type` | 类型名全集（byte/uint2/int2/int4/int8/real 等）、real→byte 截断行为 |
| `scale_image` | 结果超 uint2 / real 范围的行为（IP-03 Linear 组合出界时给出提示的依据） |

### P1-2 参数建模：Gauss 的 Size 用显式数值枚举（硬性）

计划要求"Gauss 的 Size 用下拉框限定合法值（3、5、7、9、11）"。**不要做成 int + 校验**：做成枚举且显式赋值——

```csharp
public enum GaussFilterSize { Size3 = 3, Size5 = 5, Size7 = 7, Size9 = 9, Size11 = 11 }
```

枚举在参数面板自动渲染为下拉（已核实），按数字保存后流程文件里就是 `"Size": 5`，可读且非法值无从产生。同理：`SobelType` / `SmoothFilter` / `MedianMaskType` / `MedianMargin` / `ConvertTypeNewType` 等"取值集合固定"的参数一律枚举（成员名即 HALCON 字符串值），自由数值（Alpha、Factor、Sigma、Gamma、Percent 等）保留 double 并做范围校验。守卫测试固定：枚举按数字保存、成员顺序即 HALCON 取值、输出名 `Image` 不与任何参数同名。

### P1-3 各方式对通道数与图像类型的前置要求（计划未写，需补）

计划没写多通道行为，实现前必须在计划里钉死每种方式的输入要求与中文错误：

- `RgbToGray`：要求三通道，单通道明确报"需要三通道彩色图像"。
- `EquHisto` / `ConvertType` / `Gamma` / `PercentStretch` / `AutoStretch`：对三通道图是报错还是只取第一通道？**建议一律报错**（彩色图先接 RgbToGray 或通道分解），探测后按实测写明。
- 探测各算子在非 byte 类型上的自动转换行为，统一策略写进计划。

### P1-4 PercentStretch 的定义域语义（计划写了但实现易错）

`min_max_gray` 需要显式 Region 参数。计划要求"只统计图像定义域内的灰度；配合 ReduceDomain 可按 ROI 拉伸"——实现必须用 `get_domain` 取当前定义域（不是全图矩形），并探测空定义域的报错行为；这是 IP-03 唯一容易静默做错的语义。

## 3. P2：开发中落实

1. **新建工具登记清单**（`gray-enhance`）：`BuiltinToolIdentities` 注册（批二已证实序列化 `ToolId` 只来自这里，不再依赖 `ToolboxRegistry`）、`ToolboxRegistry` 登记到 `02 图像处理`、新图标键 `ToolIcon.gray-enhance`、`WpfToolEditorRouter.IsVisualPreviewTool()` 加入、`README` 工具表更新。
2. **默认模块名**：显示名改为"图像滤波"后，新建节点的默认模块名建议同步由"均值滤波N"改为"图像滤波N"（`ToolboxRegistry` 的 `NextModuleName`，纯显示、历史流程不受影响）；决定写进计划实现说明。
3. **共享参数的显隐**：`Width` / `Height` 被 Mean / Emphasize / GrayErosion / GrayDilation / GrayOpening / GrayClosing 复用，`IToolParameterVisibility` 按 Method 精确显隐；其余方式各自的参数互不串显。
4. **校验与运行一致**：每种方式的参数范围校验在 `CheckConfiguration` 与 `Run` 中给出相同中文信息（沿用标定线"校验、运行、预热措辞一致"的做法）；`Method = Mean` 时行为与错误信息与旧版逐字相同（回归用例）。
5. **图像资源约定**：IP-01 / IP-03 的输出全部是新生成图像（归运行上下文所有），无借用透传；测试里固定 ownership 语义，防止误释放输入图。
6. **日志**：沿用现有 `[均值滤波] ...` 风格改为 `[图像滤波] Method=...`；`Mean` 方式的日志格式不变。

## 4. P3：文档维护

1. 计划头部状态按批更新；IP-01 / IP-03 补实现说明；探测结论记入新增探测节。
2. README 工具表："均值滤波"行改"图像滤波"并补方式列表，新增"灰度增强"行。
3. `examples\*.vflow.json` 回归与全量测试对账口径沿用：汇报注明新增测试中 HALCON 门禁用例数。

## 5. 开工顺序确认

- **本批：IP-01（图像滤波）+ IP-03（灰度增强）**——使用频率最高，纯图像进出的单工具，验收可与 HALCON 逐像素对照（门禁用例）。
- 后续批建议：IP-08 + IP-02（第二批）、IP-04（第三批）、IP-05 + IP-06（第四批）、IP-07（第五批），各批动工前按本文模式做小评审。
- 探测在批前进行（P1-1 清单即本批探测范围）；每批完成后按惯例评审。

---

## 第二批（IP-08 + IP-02）评审

评审日期：2026-10-08
评审对象：`origin/feature/image-tools-batch2`（`a6f1ec3`，单提交，已推送）
评审方式：提交结构核对 → 核心实现逐文件审查（`RegionTools.cs`、`ImageTools.cs`、`WpfThresholdToolEditWindow.xaml(.cs)`、路由与工具箱）→ 本地构建与全量测试对账 → 合并 `main`（`115527b..a6f1ec3`，已推送）

评审结论：**通过，已合并进 main。**

### 核对结果

| 项 | 结果 |
|---|---|
| 提交结构 | `origin/main..HEAD` 仅 1 个提交，无 "Agent host session" 快照，diff 11 个文件 +1559/−22，与工作声明一致 |
| 构建 | 0 错误；唯一警告为既有的 `MatchMeasureBatch4Tests.cs(143)` xUnit2000 |
| 本机测试对账 | 798 通过 / 348 失败 / 总计 1146，与预测完全吻合（基线 769/326 + 新增非门禁 29 / 门禁 22）；348 个失败全部带"找不到指定的模块"，即全部是本机缺 HALCON 原生库的门禁用例，无真失败 |
| 批二用例单独跑 | 51 个：29 非门禁全过，22 个门禁全部因缺原生库失败（符合 Trait 标注） |

### 实现审查意见（通过点）

- `ThresholdSegmentMethod` 在末尾追加 `ColorHsv` / `ColorRgb`，按数字保存，历史文件不受影响；12 个颜色参数缺省即全范围，旧文件反序列化安全。
- `ColorChannels()` 一处定义同时驱动流程校验、参数显隐与运行，措辞一致；色相跨 0 由两段 `threshold` 并集实现（`threshold` 不接受下限大于上限，与探测一致）。
- 取色的 HSV 换算用 1×1 图像经 `trans_from_rgb`，与运行时量化完全相同；色相回绕算术 `((hue - tol) % 256 + 256) % 256` 正确；容差 ≥ 128 取色相全范围。
- 编辑窗口"执行测试"经 `ToolTestRun` 在一次性副本上跑，取色/测试都不改节点，只有"确定"写回；失败时清理旧叠加，`_overlay` 随窗口关闭释放。
- `AddSubImageTool`：`Add` / `Sub` 成功路径、输出与 `[图像加减]` 日志逐字不变；七种运算统一前置宽×高×通道检查，算子层 #3117 / #3122 / #9001 兜底转同一中文措辞；新运算日志带输出类型与 byte 截断提示。
- 资源释放成对（`decompose3` / `trans_from_rgb` 产物、阈值中间区域、ToHsv 的 1×1 图像均有 finally 释放）。

### 偏差处理（4 条，均有依据）

1. `Add` / `Sub` 尺寸与类型错误由 HALCON 英文原文改为中文"宽×高×通道"对照——使用方确认。
2. 颜色方式额外要求 byte 类型——探测结论（0~255 范围只对 byte 成立），`decompose3` 不拒四通道故三通道检查由工具自做。
3. 空结果仍报 `Found = true`（`RegionOutput.Set` 按对象数判断，影响 10 个区域工具）——按使用方决定不并入本批，已记入 `REVIEW-FIX-PROGRESS.md`（含复现、影响与建议修法），修复时需改批二固定语义的用例。
4. 取色容差输入框宽 64 → 84（截图发现两位数显示不全）。

### 遗留

- 空结果 `Found = true` 单独立项（10 个区域工具），修复会改变默认行为，须带回归用例与迁移说明。

---

## 第三批（IP-04）评审

评审日期：2026-10-08
评审对象：`origin/feature/image-tools-batch3`（`fd437a0`，单提交，已推送）
评审方式：提交结构核对 → `ImageGeometryTool` 全文件审查 → 本地构建与全量测试对账 → 合并 `main`（`074eed1..fd437a0`，已推送）

评审结论：**通过，已合并进 main。**

### 核对结果

| 项 | 结果 |
|---|---|
| 提交结构 | `origin/main..HEAD` 仅 1 个提交，无 "Agent host session" 快照，diff 9 个文件 +968/−4，与工作声明一致 |
| 构建 | 0 错误，无新增警告 |
| 本机测试对账 | **823 通过 / 367 失败 / 总计 1190，与预测完全吻合**（基线 798/348 + 新增非门禁 25 / 门禁 19）；367 个失败全部带"找不到指定的模块"，全部是本机缺 HALCON 原生库的门禁用例，无真失败 |

### 实现审查意见（通过点）

- 三个枚举（`ImageGeometryMethod` / `GeometryInterpolation` / `ImageMirrorMode`）均按数字保存、成员名即 HALCON 取值；插值成员顺序与 `get_param_info` 的 `value_list` 一致；未改动已持久化的 `ImageInterpolationMode`（与三算子取值不同，另建枚举是对的）。
- HomMat 坐标映射与第 14 节实测一致：缩放用像素中心约定且 s 取**实际输出尺寸比**（非参数值）；旋转绕 ((h−1)/2, (w−1)/2) 映射到输出中心；三种镜像与两种裁剪的平移矩阵均正确。
- 校验分层正确：与图像无关的约束走 `CheckConfiguration`（流程校验与运行共用同一函数，措辞一致），随图像尺寸变化的约束（缩放后尺寸 1~32768、裁剪起点在图内）在运行时检查；NaN / Infinity 系数被 `!(x > 0)` 与 `IsInfinity` 兜住。
- `CropRegion` 先 `union1` 合并多区域对象（避开 `reduce_domain` 只用第一个的坑），空交集明确报错而不是输出 1×1 空图；`crop_domain` 前先取定义域外接矩形作平移矩阵，与算子实际裁剪范围一致。
- 矩阵输出沿用代码库既有 `HomMat2D` 变量机制（与区域位姿、畸变校正同法），未新造类型；`output` 所有权移交后置空，finally 只释放未移交的对象。

### 偏差处理（5 条，均有依据）

1. 另建 `GeometryInterpolation` 不改既有 `ImageInterpolationMode`——既有枚举已持久化且取值集合不同。
2. 校验分流程 / 运行两处——缩放系数有效范围随图像尺寸变化，物理上无法在流程校验期定死。
3. 两处 HALCON 行为由工具补齐（多区域合并、空交集报错），两处沿用算子原行为并写入实现说明（右下角超界不报错、旋转不用输入定义域）。
4. `ReduceDomainTool` 多区域只用第一个的既有问题——按使用方决定不并入本批，已记入 `REVIEW-FIX-PROGRESS.md`（含 121/297 像素复现数据与建议修法）。
5. 补 `ToolIcon.image-geometry` 图标——与前几批惯例一致。

### 遗留

- `ReduceDomainTool` 多区域对象只用第一个（单独立项；修复会改变多对象输入的现有输出，须先确认现场流程）。
- 区域工具空结果 `Found = true`（批二遗留，未开始）。
