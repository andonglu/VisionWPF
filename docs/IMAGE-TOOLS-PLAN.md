# 图像处理补充开发计划

编写日期：2026-10-06
状态：第一批 IP-01（图像滤波）/ IP-03（灰度增强）已实现（分支 `feature/image-tools-batch1`，算子探测见第 10 节，验收见第 11 节）；第二批 IP-08（阈值分割颜色方式）/ IP-02（图像运算）已实现（分支 `feature/image-tools-batch2`，算子探测见第 12 节，验收见第 13 节）；其余各项按评审建议分批（IP-04、IP-05 + IP-06、IP-07），动工前各做一次小评审。
范围：工具箱“02 图像处理”（`VisionFlow.Tools\Tools\ImageTools.cs`），以及与图像相关的“阈值分割”颜色方式。
HALCON 版本基线：22.11 及以上（与仓库现行基线一致；原文写 20.11）。
关联文档：[Region 相关算子补充开发计划](REGION-TOOLS-PLAN.md)（原暂缓项 `region_to_bin` / `paint_region` 在本文 IP-07 实现）、[数据处理、判定与逻辑补充开发计划](LOGIC-DATA-TOOLS-PLAN.md)。

## 1. 现状

| 工具 | 类型 | 算子 / 参数 |
|---|---|---|
| 均值滤波 `mean-image` | `MeanImageTool` | `mean_image`，`Width` / `Height` |
| 图像仿射变换 `affine-trans-image` | `AffineTransformImageTool` | `affine_trans_image`，必须输入变换矩阵 |
| ReduceDomain `reduce-domain` | `ReduceDomainTool` | `reduce_domain` |
| 图像加减 `add-sub-image` | `AddSubImageTool` | `add_image` / `sub_image`，`Multi` / `Add` |
| 通道分解 `decompose-channels` | `DecomposeChannelsTool` | `access_channel` |
| 三通道合成 `compose3` | `Compose3ImageTool` | `compose3` |
| RGB 色彩空间转换 `trans-color-space` | `TransColorSpaceTool` | `trans_from_rgb` |

缺少：常用滤波、锐化、梯度图、灰度形态学、对比度和亮度调整、缩放旋转裁剪、极坐标展开、颜色提取、区域转图像。

## 2. 拆分结论

| 编号 | 内容 | 处理方式 |
|---|---|---|
| IP-01 | 高斯、中值、平滑、双边滤波，锐化，梯度图，灰度形态学 | “均值滤波”扩展为“图像滤波” |
| IP-02 | 乘、除、差的绝对值、取较大、取较小 | “图像加减”扩展为“图像运算” |
| IP-03 | 线性拉伸、自动拉伸、直方图均衡、反相、伽马、光照校正、彩色转灰度、像素类型转换 | **新建工具“灰度增强”** |
| IP-04 | 缩放、旋转、镜像、裁剪 | **新建工具“图像几何变换”** |
| IP-05 | 极坐标展开 | **新建工具“极坐标展开”** |
| IP-06 | 极坐标结果映射回原图 | **新建工具“极坐标逆变换”** |
| IP-07 | 区域转二值图、区域绘制到图像 | **新建工具“区域转图像”** |
| IP-08 | 按颜色范围提取区域 | 并入“阈值分割”，新增颜色方式 |

结果：新建 5 个工具，增强 3 个现有工具，工具箱净增 5 个入口。

拆分说明：IP-01 与 IP-03 都是“一张图进、一张图出”，但一个改变局部邻域（滤波），一个只改变灰度映射（增强），参数与用途差别大，合在一起会让一个工具的选项超过 20 个，因此按用途分成两个。IP-04 与现有“图像仿射变换”分开，是因为后者必须输入矩阵，而几何变换的参数是直接填写的。

## 3. 开发通用做法

与 Region / XLD 计划一致：新增枚举值追加在末尾、默认值等于现有行为；新输出补 `[ToolOutput]`；新工具登记 `BuiltinToolIdentities`、注册 `ToolboxRegistry`；全部图像工具加入 `WpfToolEditorRouter.IsVisualPreviewTool()`，在编辑窗口中实时预览输出图像；测试与 README 同步更新。

图像输出遵循资源约定：工具新生成的图像归运行上下文所有；直接透传输入图像时用借用输出，不得释放调用方图像。

## 4. 增强现有工具

### IP-01 均值滤波扩展为图像滤波

- 工具箱显示名改为“图像滤波”，ID `mean-image` 与类型名 `MeanImageTool` 不变。
- 新增 `Method` 枚举，`Mean`（默认，现有逻辑）在首位：

| 方式 | 算子 | 参数 |
|---|---|---|
| `Mean` | `mean_image` | 复用 `Width` / `Height` |
| `Gauss` | `gauss_filter` | `GaussSize`（枚举 3、5、7、9、11，第 10 节） |
| `Median` | `median_image` | `MaskType`（`circle` / `square`）、`Radius`（≥ 1）、`Margin`（`mirrored` 默认 / `cyclic` / `continued`） |
| `Smooth` | `smooth_image` | `SmoothFilter`（`deriche1` / `deriche2` 默认 / `shen` / `gauss`）、`Alpha`（> 0） |
| `Bilateral` | `bilateral_filter` | `SigmaSpatial`（≥ 0.6）、`SigmaRange`（> 0）（引导图使用输入图本身） |
| `Emphasize` | `emphasize` | 复用 `Width` / `Height`（≥ 3），新增 `Factor`（≥ 0） |
| `SobelAmp` | `sobel_amp` | `SobelType`（12 个，见第 10 节）、`SobelSize`（枚举 3 ~ 13 的奇数；`value_list` 列到 39，22.11 实测 15 起报错） |
| `GrayErosion` / `GrayDilation` / `GrayOpening` / `GrayClosing` | `gray_erosion_rect` / `gray_dilation_rect` / `gray_opening_rect` / `gray_closing_rect` | 复用 `Width` / `Height`（HALCON 参数顺序为高、宽，第 10 节已实测确认） |

- 输出不变：`Image`。
- 编辑窗口按方式显示参数；`Gauss` 的 `Size` 用下拉框限定合法值。

**实现说明（第一批已完成）**

- 类型：`MeanImageTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility`，ID `mean-image` 与类型名不变；工具箱显示名改为“图像滤波”，新建节点的默认模块名由“均值滤波N”改为“图像滤波N”（评审 P2-2，纯显示，历史流程不受影响）。
- 参数建模（评审 P1-2）：新枚举 `ImageFilterMethod`（`Mean` 在首位即默认）；取值集合固定的参数一律为枚举、成员名即 HALCON 字符串值、按数字保存——`GaussFilterSize { Size3 = 3 … Size11 = 11 }`（属性 `GaussSize`，流程文件中即 `"GaussSize": 9`）、`SobelFilterSize { Size3 = 3 … Size13 = 13 }`（属性 `SobelSize`）、`MedianMaskType`、`MedianMargin`、`SmoothFilterType`（属性 `SmoothFilter`）、`SobelFilterType`（12 个）。两个尺寸枚举的属性分别命名为 `GaussSize` / `SobelSize`（同一工具中不能有两个 `Size`）。自由数值 `Radius`（int）、`Alpha`、`SigmaSpatial`、`SigmaRange`、`Factor` 做范围校验（第 10.3 节）。`Width` / `Height` 被 Mean / Emphasize / 四种灰度形态学复用，`IToolParameterVisibility` 按方式精确显隐；灰度形态学按 HALCON 的（高, 宽）顺序传参。
- `Mean` 方式保持旧代码路径：运行时错误信息“均值滤波核宽高必须大于 0”（不带模块名前缀）与日志“[均值滤波] Width=…, Height=…”逐字不变；新增的流程校验用同一句话（旧版只在运行时报）。其余方式日志为“[图像滤波] Method=…, …”。
- 校验与运行：`CheckConfiguration` 与 `Run` 共用同一组检查，运行时信息为“模块名 参数：说明”。调用算子前按第 10.2 节检查像素类型（不支持时说明支持哪些类型，建议先接灰度增强的 ConvertType）；随图像尺寸变化的上限（#3033、中值半径 #1302、双边 `SigmaSpatial` 过大）转成带图像尺寸的中文错误。多通道图逐通道处理（探测表明全部方式都支持）。
- 输出 `Image` 为新生成图像、归运行上下文所有，不透传、不释放输入图（用例固定）。

### IP-02 图像加减扩展为图像运算

- 工具箱显示名改为“图像运算”，ID `add-sub-image` 与类型名 `AddSubImageTool` 不变。
- `ImageArithmeticOperation` 末尾追加：

| 操作 | 算子 | 参数 |
|---|---|---|
| `Mult` | `mult_image`（g1 × g2 × Multi + Add） | 复用 `Multi` / `Add` |
| `Div` | `div_image`（g1 / g2 × Multi + Add，除数为 0 的像素为 0） | 复用 `Multi` / `Add` |
| `AbsDiff` | `abs_diff_image`（\|g1 − g2\| × Multi） | 复用 `Multi`（`abs_diff_image` 没有 Add） |
| `Max` | `max_image` | 无 |
| `Min` | `min_image` | 无 |

- 两张图尺寸或通道数不一致时明确失败（七种运算都在调用算子前检查，第 12.2 节）。
- byte 与 byte 运算的结果仍为 byte，超出 0 ~ 255 时饱和截断（第 12.1 节实测，不升为 uint2）；`Mult` / `Div` / `AbsDiff` 运行日志注明，编辑窗口提示配合 `Multi` / `Add` 调整，或接 IP-03 的像素类型转换。

**实现说明（第二批已完成）**

- 类型：`AddSubImageTool : ToolBase, IToolParameterVisibility`，ID `add-sub-image` 与类型名不变；工具箱显示名改为“图像运算”，新建节点的默认模块名由“图像加减N”改为“图像运算N”。`ImageArithmeticOperation` 末尾追加 `Mult` / `Div` / `AbsDiff` / `Max` / `Min`（按数字保存，历史文件缺省 `Add`）。
- 显隐：`AbsDiff` 隐藏 `Add`（`abs_diff_image` 没有加数），`Max` / `Min` 隐藏 `Multi` 与 `Add`。
- 前置检查（经使用方确认覆盖七种运算）：调用算子前比较两图“宽×高×通道”，不一致时报“两张图像的尺寸或通道数不一致：图像1 64×48×3，图像2 64×48×1（宽×高×通道）”；算子仍报 #3117 / #3122 时转成同一句话。像素类型不同时 `Add` 沿用 HALCON 的自动转换（byte + uint2 → uint2），其余运算的 #9001 转成“两张图像的像素类型不同或不受支持：图像1 …，图像2 …”。`Add` / `Sub` 的成功路径、输出与日志“[图像加减] …”不变；原来尺寸或通道不一致时直接抛出 HALCON 的英文错误，现在为上述中文说明。
- 日志：新增运算为“[图像运算] Op, 参数；输出 类型”，`Mult` / `Div` / `AbsDiff` 在 byte 输出时注明“超出 0 ~ 255 的部分被截断，需要完整范围时配合 Multi / Add，或先接灰度增强的 ConvertType 转成 real”。
- 编辑窗口：仍走通用视觉预览窗口，参数区末尾加一行静态说明（byte 结果截断、Mult 常需把 Multi 设小、Div 除数为 0 的像素为 0、两图宽高通道须一致）。

### IP-08 阈值分割增加颜色方式

- `ThresholdSegmentMethod` 末尾追加 `ColorHsv`、`ColorRgb`。
- 输入仍为一个图像引用，要求 **byte 三通道**彩色图（第 12.2 节：HSV / RGB 的 0 ~ 255 范围只对 byte 成立；`decompose3` 对四通道不报错，通道数由工具检查），单通道时明确失败。
- `ColorHsv` 参数：`HueMin` / `HueMax`（0~255，HALCON `trans_from_rgb` 的 HSV 量化范围）、`SaturationMin` / `SaturationMax`、`ValueMin` / `ValueMax`。`HueMin > HueMax` 表示跨越 0（如红色 230~20）。
- `ColorRgb` 参数：`RedMin` / `RedMax`、`GreenMin` / `GreenMax`、`BlueMin` / `BlueMax`。
- 实现：`decompose3` → （HSV 时）`trans_from_rgb` → 各通道 `threshold` → `intersection`；色相跨 0 时为两段阈值的并集再求交。
- 输出不变：`Region`、`UsedThreshold`（颜色方式输出 NaN）、`Count`、`Found`；`Connection` 选项照常生效。
- 编辑窗口：在图像上点击取色，自动填入以该点为中心的范围（容差可调），并实时预览提取结果。

**实现说明（第二批已完成）**

- 工具：`ThresholdSegmentMethod` 末尾追加 `ColorHsv`（7）/ `ColorRgb`（8），既有 7 个方式顺序不动；新参数 `HueMin` / `HueMax` / `SaturationMin` / `SaturationMax` / `ValueMin` / `ValueMax` 与 `RedMin` … `BlueMax`（int，0 ~ 255；下限默认 0、上限默认 255，历史文件缺省为全范围），`IToolParameterVisibility` 只在对应方式显示，颜色方式隐藏全部灰度阈值参数，`Connection` / `FailWhenNotFound` 照常。
- 校验（流程校验与运行措辞一致）：各值 0 ~ 255；色相以外的通道下限不能大于上限；色相下限大于上限表示跨 0，不报错。
- 运行：先检查三通道（`decompose3` 对四通道不报错，由工具检查）与 byte 类型（第 12.2 节），再 `decompose3` →（HSV 时 `trans_from_rgb`）→ 各通道 `threshold` → `intersection`，色相跨 0 时为 [HueMin, 255] ∪ [0, HueMax]。`UsedThreshold` 输出 NaN；日志“[颜色阈值 HSV] 色相 [230, 20]（跨 0）, 饱和度 […], 明度 […]，区域数 n”。
- 编辑窗口：新建 `WpfThresholdToolEditWindow`（`WpfToolEditorRouter` 改为路由到它，`IsVisualPreviewTool()` 中移除 `ThresholdTool`；旧版独立阈值工具的兼容壳同样路由到它）。左侧图像区显示输入图像与提取结果叠加，上方“取色容差”（默认 20）、“在图像上取色”（只在颜色方式可用）、“执行测试”；右侧为模块名、图像输入与全部运行参数（标签为参数名，与侧栏一致，按方式显隐）。取色经 `get_grayval` 读取 RGB，HSV 用 1 × 1 图像经 `trans_from_rgb` 换算（与运行时量化相同）；按容差填入当前方式的各通道范围（色相按 0 ~ 255 回绕，可得到跨 0 的范围；容差 ≥ 128 时色相取全范围），随即执行测试预览。执行测试经 `ToolTestRun`，确定时才写回工具。

## 5. 新建工具

### IP-03 灰度增强

- 工具箱：`02 图像处理 / 灰度增强`，ID `gray-enhance`，类 `GrayEnhanceTool`。
- 输入：图像（必填）。输出：`Image`。

| 方式 `Method` | 算子 | 参数 |
|---|---|---|
| `Linear` | `scale_image` | `Mult`、`Add` |
| `AutoStretch` | `scale_image_max` | 无（拉伸到全灰度范围；输出一律为 byte，第 10 节） |
| `PercentStretch` | `get_domain` + `min_max_gray`（`Percent`）+ `scale_image` | `Percent`（两端各舍弃的百分比，0 ~ 50，默认 1） |
| `EquHisto` | `equ_histo_image` | 无 |
| `Invert` | `invert_image` | 无 |
| `Gamma` | `gamma_image` | `Gamma`、`Offset`、`Threshold`、`MaxGray`、`Encode`（`bool`，对应 HALCON 的 `'true'` / `'false'`） |
| `Illuminate` | `illuminate` | `MaskWidth`、`MaskHeight`、`Factor` |
| `RgbToGray` | `rgb1_to_gray` | 无（工具自行检查三通道，第 10 节） |
| `ConvertType` | `convert_image_type` | `NewType`（`byte` / `uint2` / `int2` / `real`） |

- 共 9 种方式，`Linear` 在首位即默认。各方式对通道数与像素类型的要求见第 10.2 节。

- `PercentStretch` 只统计图像定义域内的灰度；配合 ReduceDomain 可按 ROI 拉伸。

**实现说明（第一批已完成）**

- 类型：`GrayEnhanceTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility`（`VisionFlow.Tools\Tools\GrayEnhanceTools.cs`），`BuiltinToolIdentities` 登记 `gray-enhance`（流程文件的 `ToolId` 只来自这里），工具箱 `02 图像处理 / 灰度增强`（默认模块名“灰度增强N”），图标 `ToolIcon.gray-enhance`，加入 `WpfToolEditorRouter.IsVisualPreviewTool()`（通用视觉预览窗口，零窗口开发）。
- 参数：`GrayEnhanceMethod`（9 种，`Linear` 在首位即默认）、`Mult` / `Add`、`Percent`（0 ~ 50）、`Gamma` / `Offset` / `Threshold` / `MaxGray`（默认值与 `gamma_image` 相同）、`Encode`（`bool`，默认 `true`）、`MaskWidth` / `MaskHeight` / `Factor`（默认值与 `illuminate` 相同）、`NewType`（枚举 `ConvertImageNewType`：`byte` / `uint2` / `int2` / `real`，按数字保存）；按方式精确显隐。
- PercentStretch（评审 P1-4）：用 `get_domain` 取定义域做 `min_max_gray`，按定义域的 [Min, Max] 拉伸；只接受单通道 byte / uint2；空定义域失败；灰度全部相同时输出副本并警告（第 10.3 节）。
- RgbToGray：工具自行检查三通道（`rgb1_to_gray` 对单通道不报错），否则报“需要三通道彩色图像（当前 n 通道）”。其余方式按第 10.2 节逐通道处理并检查像素类型。
- Linear：`scale_image` 出界饱和截断不报错，工具按定义域各通道的灰度范围估算结果，超出像素类型范围时写警告日志。
- 校验与运行措辞一致（同图像滤波）；输出 `Image` 为新生成图像（PercentStretch 无法拉伸时同样输出副本，不透传输入）。

### IP-04 图像几何变换

- 工具箱：`02 图像处理 / 图像几何变换`，ID `image-geometry`，类 `ImageGeometryTool`。
- 输入：图像（必填）；裁剪区域（可选，仅 `CropRegion` 必填）。

| 方式 `Method` | 算子 | 参数 |
|---|---|---|
| `ZoomFactor` | `zoom_image_factor` | `ScaleWidth`、`ScaleHeight`、`Interpolation` |
| `ZoomSize` | `zoom_image_size` | `TargetWidth`、`TargetHeight`、`Interpolation` |
| `Rotate` | `rotate_image` | `AngleDeg`、`Interpolation` |
| `Mirror` | `mirror_image` | `MirrorMode`（`row` / `column` / `diagonal`） |
| `CropRectangle` | `crop_rectangle1` | `Row1`、`Column1`、`Row2`、`Column2` |
| `CropRegion` | `reduce_domain` + `crop_domain` | 读取裁剪区域 |

- 输出：`Image`；`HomMat`（原图坐标 → 新图坐标）与 `InverseHomMat`（新图坐标 → 原图坐标），使后续在新图上的检测结果可用“图像坐标转世界坐标”或 XLD / Region 仿射变换映射回原图。
- 开发方法：每种方式按 HALCON 的变换定义构造 `HomMat2D`（`rotate_image` 绕图像中心旋转，开发时用测试核对中心定义）；`InverseHomMat` 用 `hom_mat2d_invert`。

### IP-05 极坐标展开

- 工具箱：`02 图像处理 / 极坐标展开`，ID `polar-unwrap`，类 `PolarUnwrapTool`。
- 用途：把圆环区域展开成矩形，便于检测瓶口、轴承、O 形圈、圆周字符等。
- 输入：图像（必填）；圆心行、列引用（可选，例如接圆形测量的 `Row` / `Column`，不配置时使用固定参数）；定位矩阵（可选，跟随）。
- 参数：`CenterRow`、`CenterColumn`、`AngleStartDeg`、`AngleEndDeg`、`RadiusStart`、`RadiusEnd`、`OutputWidth`、`OutputHeight`（0 表示按周长和半径差自动计算）、`Interpolation`。
- 算子：`polar_trans_image_ext`。
- 输出：`Image`（展开图）；`PolarParams`（对象，记录实际使用的圆心、起止角、起止半径、展开图尺寸、原图尺寸），供 IP-06 映射回原图。
- 编辑窗口：在原图上拖动圆心和内外半径，旁边实时显示展开图。

### IP-06 极坐标逆变换

- 工具箱：`02 图像处理 / 极坐标逆变换`，ID `polar-inverse`，类 `PolarInverseTool`。
- 用途：把在展开图上找到的缺陷区域、轮廓映射回原图显示和计算位置。
- 输入：极坐标参数（必填，引用 IP-05 的 `PolarParams`）；区域（可选）；XLD（可选）；两者至少配置一个。
- 算子：`polar_trans_region_inv`、`polar_trans_contour_xld_inv`。
- 输出：`Region`、`Xld`、`Count`、`Found`。
- 与 IP-05 拆成两个工具的原因：逆变换必须在展开图上的检测完成之后才能运行，不能放在同一个节点里。

### IP-07 区域转图像

- 工具箱：`02 图像处理 / 区域转图像`，ID `region-to-image`，类 `RegionToImageTool`。
- 输入：区域（必填）；参考图像（`PaintOnImage` 必填，`Binary` 可选，用于取尺寸）。

| 方式 `Method` | 算子 | 参数 |
|---|---|---|
| `Binary` | `region_to_bin` | `ForegroundGray`（默认 255）、`BackgroundGray`（默认 0）；尺寸取参考图像，未配置时用 `Width` / `Height` |
| `PaintOnImage` | `paint_region` | `Gray`、`PaintType`（`fill` / `margin`） |

- 输出：`Image`。
- 用途：生成掩膜图、把检测结果画到图上作为结果图交给上层保存。

## 6. 暂缓

- 频域处理（`fft_image` 等）、纹理滤波（`texture_laws`）：使用少，按需再加入 IP-01。
- 多帧平均、图像拼接、高动态范围融合：涉及多张图像输入，与上层采集方式相关，待有需求时另行规划。
- 清晰度评估：可用 IP-01 的 `SobelAmp` 加“MinMaxGray 灰度统计”（RG-04 增加均值、标准差后）组合实现，不单独做工具。

## 7. 兼容性要求

- “均值滤波”“图像加减”只改显示名，ID 与类型名不变；`Method = Mean`、原有加减运算的结果不变。
- `ThresholdTool` 原有分割方式与默认值不变。
- 新增枚举值全部追加在末尾。

## 8. 验收

- IP-01：每种方式输出与直接调用 HALCON 算子逐像素一致；`Mean` 结果与旧版一致（第一批已完成，见第 11 节）。
- IP-02：新增运算与直接调用 HALCON 一致；尺寸不一致时明确失败（第二批已完成，见第 13 节）。
- IP-03：`PercentStretch` 对已知直方图的合成图像结果正确；`ConvertType` 输出类型正确（第一批已完成，另有各方式与 HALCON 逐像素一致，见第 11 节）。
- IP-04：在新图上取一点，用 `InverseHomMat` 映射回原图，与原图对应点误差小于 0.5 像素（缩放、旋转、镜像、裁剪各一例）。
- IP-05 / IP-06：在合成圆环上画一个缺陷，展开后检测到的区域经逆变换后与原缺陷重合（面积误差小于 5%）。
- IP-07：二值图前景、背景灰度正确；绘制方式只改变区域内（或边缘）像素。
- IP-08：合成色块图中，指定颜色范围只提取对应色块；色相跨 0 的红色能正确提取（第二批已完成，见第 13 节）。
- 全部回归测试和 `examples\*.vflow.json` 通过。

## 9. 开发顺序

1. IP-01（图像滤波）、IP-03（灰度增强）：使用频率最高。
2. IP-08（颜色阈值）、IP-02（图像运算）。
3. IP-04（图像几何变换）。
4. IP-05、IP-06（极坐标展开与逆变换），一起开发。
5. IP-07（区域转图像）。

## 10. 第一批算子探测结论（IP-01 / IP-03）

**探测环境**：HALCON 22.11 Steady（`C:\Program Files\MVTec\HALCON-22.11-Steady`），部署到其他版本前需复核。探测在写 IP-01 / IP-03 代码之前完成（评审 P1-1）。合成图像为 64 × 48 字节图（水平渐变加小噪声），另由它转换得到 uint2 / int2 / real 单通道图、三通道彩色图（原图、反相图、0.5 倍图合成）与空定义域图；参数取值集合来自 `get_param_info` 的 `value_list`，参数顺序来自 `get_param_names`，再逐项实测。

### 10.1 逐项结论

| 算子 | 结论 |
|---|---|
| `gauss_filter` | `Size` 只能是 3 / 5 / 7 / 9 / 11（`value_list` 同），其余（含 1、偶数、13 及以上）报 #3022。byte / uint2 / int2 / real 与三通道（逐通道）都可处理；int1 / int4 报 #9001 |
| `median_image` | `MaskType` 只有 `circle` / `square`（其他报 #1301）；`Radius` ≥ 1 才可用（0、负数报 #1302），上限随图像尺寸变化：64 × 48 图最大 23、512 × 512 图在 211 ~ 261 之间、2000 × 2000 图在 961 ~ 1011 之间（约为短边的一半），超出报 #1302，只能在运行时发现；`Margin` 取 `mirrored` / `cyclic` / `continued` 或一个灰度常数（0 ~ 255，300 报 #1303，−1 被当作 `continued`、12.5 截为 12），其他字符串报 #1303。byte / uint2 / int2 / real 与三通道都可处理 |
| `smooth_image` | `Filter` 为 `deriche1` / `deriche2` / `shen` / `gauss`（其他报 #1301），四种都要求 `Alpha` > 0（0、负数报 #1302），没有上限（实测到 100）。int2 报 #9001；byte / uint2 / real 与三通道可处理 |
| `bilateral_filter` | 参数为 `(Image, ImageJoint, SigmaSpatial, SigmaRange, GenParamName, GenParamValue)`；`SigmaSpatial` ≥ 0.6（0.5999 报 #1301，0.6 可用），过大时报 #3033“滤波尺寸超过图像”（64 × 48 图 20 可用、25 不可用）；`SigmaRange` > 0（0、负数报 #1302），上限未见（100000 可用）。引导图传输入图自身即普通双边滤波。int2 报 #9001；byte / uint2 / real 与三通道可处理 |
| `sobel_amp` | `FilterType` 全集 12 个：`sum_abs`、`thin_sum_abs`、`thin_max_abs`、`sum_sqrt`、`x`、`y`、`sum_abs_binomial`、`thin_sum_abs_binomial`、`thin_max_abs_binomial`、`sum_sqrt_binomial`、`x_binomial`、`y_binomial`（其他报 #1301）。`Size` 的 `value_list` 列出 3 ~ 39 的奇数，**但 22.11 实测只接受 3 / 5 / 7 / 9 / 11 / 13**（15 及以上在 64 × 48、512 × 512、2000 × 2000 图上都报 #1302，与图像尺寸无关），以实测为准。byte 输入时 `x` / `y` 系列输出 int1（带符号），其余输出 byte。byte / uint2 / int2 / real 与三通道都可处理 |
| `gray_erosion_rect` 等四个 | 参数为 `(Image, MaskHeight, MaskWidth)`——**先高后宽**，与 `mean_image` / `emphasize`（先宽后高）相反。实测：单个暗点经 `gray_erosion_rect(·, 1, 7)` 扩展为 1 行 7 列、`(·, 7, 1)` 扩展为 7 行 1 列。宽高 ≥ 1（0、负数报 #1301 / #1302），偶数可用；上限随图像尺寸（64 × 48 图 121 可用、131 报 #3033）。byte / uint2 / int2 / real 与三通道都可处理 |
| `emphasize` | 参数为 `(Image, MaskWidth, MaskHeight, Factor)`；宽、高都须 ≥ 3（1、2 报 #1301 / #1302），上限随图像尺寸（#3033）；`Factor` ≥ 0（−1 报 #1303），0 与 100 都可用，没有 0.7 ~ 1.9 的硬性限制。real 报 #9001；byte / uint2 / int2 与三通道可处理 |
| `mean_image`（Mean 方式） | 参数为 `(Image, MaskWidth, MaskHeight)`；宽高 ≥ 1（0 报 #1301），上限随图像尺寸（64 × 48 图 121 可用、131 报 #3033）。byte / uint2 / int2 / real 与三通道都可处理 |
| `gamma_image` | 参数为 `(Image, Gamma, Offset, Threshold, MaxGray, Encode)`，默认值 0.416666666667 / 0.055 / 0.0031308 / 255 / `'true'`。`Encode` 是字符串 `'true'` / `'false'`（整数 1 / 0 报 #1205 类型错误，`'yes'` 报 #1305）；`Gamma` > 0（0、负数报 #1301）；`Offset` ≥ 0（−1 报 #1302）；`Threshold` ≥ 0（−1 报 #1303）；`MaxGray` > 0（0、负数报 #1304）。int2 报 #9001；byte / uint2 / real 与三通道可处理 |
| `min_max_gray` | 参数为 `(Regions, Image, Percent, Min, Max, Range)`，`Percent` 取 0 ~ 50（−1、51 报 #1301；50 时最小值 = 最大值）。**空区域、空定义域不报错**，返回 Min = Max = Range = 0；区域完全在图像外同样返回 0。只统计传入区域与图像定义域的交集：缩小定义域的图像传全图矩形时结果与定义域相同，因此 PercentStretch 用 `get_domain` 取定义域（评审 P1-4）。**三通道图只统计第一通道**（结果与单通道原图相同），不报错 |
| `scale_image_max` | 对 byte / uint2 / int2 / real 输入**一律输出 byte**，等价于按定义域最小、最大值线性拉伸到 0 ~ 255（与手算 `scale_image(255 / (max − min), −min · 255 / (max − min))` 逐像素一致；uint2 放大 100 倍后结果与 byte 原图相同）。三通道逐通道拉伸（输出 3 通道 byte，各通道比例不同）。只拉伸定义域、保留定义域；空定义域不报错；常数图不报错，输出 128 |
| `equ_histo_image` | byte / uint2 可处理（类型不变），int2 / real 报 #9001；三通道逐通道均衡；空定义域不报错 |
| `rgb1_to_gray` | 三通道（byte / uint2 / real）输出同类型单通道（例：(110, 145, 85) → 128）。**单通道输入不报错，原样输出单通道**；2 通道、4 通道报 #9009“不是三通道彩色图”。所以 RgbToGray 的“需要三通道彩色图像”须由工具自行检查通道数 |
| `convert_image_type` | `NewType` 全集：`int1`、`int2`、`uint2`、`int4`、`int8`、`byte`、`real`、`direction`、`cyclic`、`complex`（其他报 #1301）。real → byte 四舍五入并截断到 0 ~ 255（−5 → 0、12.4 → 12、12.5 → 13、300 → 255）；real → uint2 截断到 0 ~ 65535，→ int2 截断到 −32768 ~ 32767。三通道逐通道转换 |
| `scale_image` | 结果超出像素类型范围时**饱和截断、不报错**：byte 截到 0 ~ 255，uint2 到 0 ~ 65535，int2 到 −32768 ~ 32767；real 不截断，可得到 ∞。结果四舍五入（0.5 × 110 + 0.6 = 55.6 → 56）。三通道逐通道处理；保留定义域 |
| `invert_image` | byte 为 255 − g，uint2 为 65535 − g，int2 / real 为 −g；三通道逐通道 |
| `illuminate` | 参数为 `(Image, MaskWidth, MaskHeight, Factor)`，默认 101 / 101 / 0.7；`Factor` ≥ 0（−1 报 #1303）；**宽为 0 或负数不报错**（结果与宽 1 相同），上限随图像尺寸（#3033）。real 报 #9001；byte / uint2 / int2 与三通道可处理 |

### 10.2 各方式的输入要求（评审 P1-3）

所有方式只对图像**定义域**处理并保留定义域；空定义域图像除 PercentStretch 外都正常输出（空定义域）。下表“类型”只列已实测的 byte / uint2 / int2 / real，其他类型（int1、int4 等）一律视为不支持；不支持的类型在调用算子前由工具检查并给出中文错误，说明支持哪些类型，并建议先接灰度增强的 ConvertType。

| 工具 / 方式 | 通道 | 类型 |
|---|---|---|
| 图像滤波 Mean / Median / SobelAmp / GrayErosion / GrayDilation / GrayOpening / GrayClosing | 单通道或多通道（逐通道） | byte / uint2 / int2 / real |
| 图像滤波 Gauss | 同上 | byte / uint2 / int2 / real |
| 图像滤波 Smooth / Bilateral | 同上 | byte / uint2 / real |
| 图像滤波 Emphasize | 同上 | byte / uint2 / int2 |
| 灰度增强 Linear / Invert / ConvertType | 同上 | byte / uint2 / int2 / real |
| 灰度增强 AutoStretch | 同上（逐通道拉伸，彩色图各通道比例不同，色调会变化） | byte / uint2 / int2 / real，输出 byte |
| 灰度增强 EquHisto | 同上（逐通道均衡，色调会变化） | byte / uint2 |
| 灰度增强 Gamma | 同上 | byte / uint2 / real |
| 灰度增强 Illuminate | 同上 | byte / uint2 / int2 |
| 灰度增强 PercentStretch | **只接受单通道**：`min_max_gray` 对彩色图只统计第一通道、不报错，结果会悄悄错误，因此彩色图报错“先接灰度增强的 RgbToGray 或通道分解” | byte（拉伸到 0 ~ 255）/ uint2（拉伸到 0 ~ 65535） |
| 灰度增强 RgbToGray | **只接受三通道**：单通道时 `rgb1_to_gray` 不报错、原样输出，工具自行检查并报“需要三通道彩色图像” | byte / uint2 / real |

多通道策略经使用方确认（2026-10-08）：探测表明除 `min_max_gray` 外，上述算子对三通道图都逐通道正确处理，因此只有 PercentStretch 拒绝彩色图，其余方式逐通道处理；评审 P1-3 建议的“EquHisto / ConvertType / Gamma / PercentStretch / AutoStretch 一律报错”按实测收窄为只有 PercentStretch。

### 10.3 由探测确定的实现约定

- **参数校验（流程校验与运行一致）**：只校验与图像无关的取值约束——Mean 宽高 > 0（沿用旧版信息“均值滤波核宽高必须大于 0”）；Median 半径 ≥ 1；Smooth `Alpha` > 0；Bilateral `SigmaSpatial` ≥ 0.6、`SigmaRange` > 0；Emphasize 宽高 ≥ 3、`Factor` ≥ 0；灰度形态学宽高 ≥ 1；Gamma `Gamma` > 0、`Offset` ≥ 0、`Threshold` ≥ 0、`MaxGray` > 0；Illuminate 宽高 ≥ 1（HALCON 对 0 与负数不报错，工具仍拒绝，避免静默当作 1）、`Factor` ≥ 0；PercentStretch `Percent` 0 ~ 50；Linear `Mult` / `Add` 为有限数。枚举参数手工改成未定义的数字时同样报错。
- **随图像尺寸变化的上限**（滤波核超过图像 #3033、中值半径超过约半个短边 #1302、双边 `SigmaSpatial` 过大 #3033）无法在流程校验时判断，运行时转成中文错误，带图像尺寸。
- **PercentStretch**：`get_domain` 取定义域 → `min_max_gray(定义域, 图像, Percent)` → `scale_image` 把 [Min, Max] 线性映射到 byte 的 0 ~ 255 或 uint2 的 0 ~ 65535（超出部分按 `scale_image` 规则截断）。空定义域运行失败“图像定义域为空，无法统计灰度”；定义域内 Min = Max（含 `Percent` = 50）时输出输入图的副本并写警告日志，不中断流程（使用方确认）。实现时补测：灰度 50 ~ 150 均匀分布的 byte 图，`Percent` = 0 的结果与 `scale_image_max` 逐像素一致；`scale_image` 对 `Add` = −50 × 2.55（双精度为 −127.49999999999999）的取整使灰度 50 映射为 1 而不是 0（传字面量 −127.5 时为 0），`scale_image_max` 的结果同样为 1，属 HALCON 自身的量化行为，验收按与 HALCON 直接调用逐像素一致判定。
- **Linear**：`scale_image` 出界时饱和截断不报错；工具按定义域内各通道的最小、最大灰度估算结果范围，超出像素类型范围时写警告日志（说明会被截断，建议调整 `Mult` / `Add` 或先 ConvertType 到 real）。
- **Gamma 的 `Encode`**：HALCON 只接受字符串 `'true'` / `'false'`，取值即布尔，参数用 `bool`（默认 `true`），调用时转成字符串；不建成名为 `true` / `false` 的枚举（C# 关键字）。
- **Median 的 `Margin`**：枚举只含 `mirrored`（默认）/ `cyclic` / `continued` 三个字符串取值；HALCON 另支持的“灰度常数”边界本批不提供（不常用，且常数范围随像素类型变化），需要时再追加枚举成员与常数参数。
- **SobelAmp 的 `Size`**：枚举只列实测可用的 3 ~ 13 六个奇数（`value_list` 中的 15 ~ 39 实测报错）。
- **ConvertType 的 `NewType`**：枚举按计划提供 `byte`（默认）/ `uint2` / `int2` / `real`；`int1` / `int4` / `int8` / `direction` / `cyclic` / `complex` 本批不提供（多数图像工具不支持，见 10.1），需要时追加在末尾。

## 11. 第一批（IP-01 + IP-03）评审处理与验收记录

**开工依据**：基线 `origin/main`（`ccabebc`）；评审 `IMAGE-TOOLS-PLAN-REVIEW.md` 的 P1-1（先探测后实现）在写代码前完成并记入第 10 节，计划与探测冲突处已按探测改正（第 4、5 节表格）。

**评审意见处理**

| 评审项 | 处理 |
|---|---|
| P1-1 探测 | 14 行清单逐项实测，另补 `mean_image` / `invert_image` / `illuminate`，结论与报错码记入第 10.1 节 |
| P1-2 参数建模 | 固定取值集合一律枚举、成员名即 HALCON 取值、按数字保存；`GaussFilterSize` / `SobelFilterSize` 显式数值；守卫用例固定成员顺序与取值 |
| P1-3 通道与类型 | 第 10.2 节表格；多通道策略经使用方确认按实测收窄为只有 PercentStretch 拒绝彩色图；RgbToGray 由工具检查三通道 |
| P1-4 定义域 | PercentStretch 用 `get_domain`，用例固定“只统计定义域而非全图”；空定义域与灰度一致两种边界经使用方确认 |
| P2-1 登记清单 | `BuiltinToolIdentities`、`ToolboxRegistry`、`ToolIcon.gray-enhance`、`IsVisualPreviewTool()`、README |
| P2-2 默认模块名 | 改为“图像滤波N” |
| P2-3 共享参数显隐 | `Width` / `Height` 只在 Mean / Emphasize / 灰度形态学显示，其余参数互不串显（逐方式用例） |
| P2-4 校验与运行一致 | 每种方式至少一个越界用例，校验信息与运行信息相同；Mean 错误信息与旧版逐字相同 |
| P2-5 图像资源 | 输出均为新生成图像，用例固定输出与输入不是同一对象、运行后输入图仍有效 |
| P2-6 日志 | Mean 沿用“[均值滤波] …”，其余“[图像滤波] Method=…”、“[灰度增强] Method=…” |

**与任务说明或计划不一致处（如实记录）**

| 项 | 处理 |
|---|---|
| “灰度增强十种方式” | 计划第 5 节表格为 9 种（Linear、AutoStretch、PercentStretch、EquHisto、Invert、Gamma、Illuminate、RgbToGray、ConvertType），按表格实现 9 种 |
| SobelAmp 的 `Size` | `get_param_info` 列出 3 ~ 39，22.11 实测 15 起一律报错，枚举只列 3 ~ 13 |
| 评审 P1-3 建议五种方式拒绝彩色图 | 探测表明只有 `min_max_gray` 对彩色图静默出错，经使用方确认只有 PercentStretch 拒绝彩色图 |
| Gamma 的 `Encode` | HALCON 只接受 `'true'` / `'false'`，参数用 `bool`（`true` / `false` 不能作为 C# 枚举成员名） |
| Median 的 `Margin` | 只提供三个字符串取值，灰度常数边界暂不提供 |
| `rgb1_to_gray` 单通道 | HALCON 不报错、原样输出，“需要三通道彩色图像”由工具自行检查 |
| 图标与路由的守卫用例 | 测试工程是 `net9.0`、不引用 WPF 工程，图标键与 `IsVisualPreviewTool()` 用源文件文本检查；实际路由由界面验收覆盖 |
| Mean 方式的流程校验 | 旧版宽高 ≤ 0 只在运行时报错，现在流程校验也报同一句话（运行时行为与信息不变） |

**验收结果**

- 单元/集成测试：新增 `ImageToolsBatch1Tests` 96 个用例（含 Theory 数据行），其中 39 个为 HALCON 门禁（`Requires=HALCON`）：图像滤波 11 种方式在字节图、彩色图、缩小定义域图上与直接调用算子逐像素一致（宽 7 高 3，固定灰度形态学的高宽顺序）；单个暗点按宽 7 高 1 只沿行方向扩展；Mean 与旧版 `mean_image` 一致且日志不变；不支持的像素类型与随图像尺寸变化的上限给出中文错误；灰度增强 7 种方式（三种输入）与 HALCON 一致；RgbToGray 三通道一致、单通道与双通道报错；PercentStretch 已知直方图（Percent 0 与 `scale_image_max` 一致，Percent 10 与手算一致）、只统计定义域、uint2 拉伸到 65535、空定义域失败、灰度一致输出副本并警告、彩色图报错；ConvertType 四种输出类型；Linear 出界警告。非门禁 57 个：两个工具每种方式的越界校验与运行措辞一致、Mean 错误信息逐字回归、默认参数校验通过且只校验当前方式、逐方式参数显隐、枚举成员顺序与取值、按数字保存与历史文件缺省 `Mean` / `Linear`、输出只有 `Image` 且不与参数同名、工具箱改名与登记、图标键与视觉预览路由。
- 全量 `dotnet test`（`Category!=Soak`）1095 个通过（基线 999 + 新增 96，含 `examples\*.vflow.json` 加载用例）；排除带 `Requires=HALCON` 标注的用例后 1047 个通过（基线 990 + 新增非门禁 57）。构建 0 错误，唯一警告为既有的 `MatchMeasureBatch4Tests.cs(143)` xUnit2000。
- 界面验收：脚本经 Windows UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`，43 项检查全部通过（连续两次）：工具箱 `02 图像处理` 中已无“均值滤波”，新建“图像滤波1”“灰度增强1”；侧栏按方式显隐；默认参数运行结果（经下游“区域灰度统计”的最小、最大、均值、标准差）与无界面运行逐字一致；图像滤波编辑窗口方式下拉 11 项，Gauss / Median / Smooth / Bilateral / Emphasize / SobelAmp / GrayClosing / Mean 逐一切换显隐正确，`GaussSize` 下拉 5 项、`SobelSize` 6 项、`SobelType` 12 项；预览后“查看内容”默认为本工具输出，Gauss 与 SobelAmp 的预览画面不同；Median square / 2 / cyclic 运行与无界面一致；灰度增强方式下拉 9 项，Linear（1.5, −20）、Gamma（2.2）、RgbToGray 依次切换，显隐（含 `Encode` 复选框）正确，预览逐次更新，运行结果与无界面一致；保存后两个 `ToolId` 与参数正确，重新加载再运行一致，侧栏回显。
- 验证中发现并修复的问题（既有行为，经使用方确认本批修复）：通用 / 视觉预览编辑窗口（`WpfGenericToolEditWindow`，约 30 个工具共用）预览后“查看内容”默认选中预览上下文中的**最后一个变量**；预览上下文派生自上次运行，工具不是流程最后一个节点时，最后一个变量属于下游节点（本次为“统计增强.Found”），预览区显示的不是本工具的结果。改为预览成功后默认选中本工具最后一个可显示的输出（图像 / 区域 / 轮廓），没有时取本工具最后一个输出，再没有时沿用原来的最后一个变量。
- 脚本问题（已修脚本）：初始流程有意引用两个待新建节点，打开时的“流程校验”提示框需要先关闭（并核对提示内容）；一次双击流程树节点后编辑窗口未打开（既有现象），脚本改为重试一次。

## 12. 第二批算子探测结论（IP-08 / IP-02）

**探测环境**：HALCON 22.11 Steady（同第 10 节）。探测在写 IP-08 / IP-02 代码之前完成。合成图像为 15 个 20 × 20 色块（纯红、红偏蓝 30、红偏绿 30、黄、绿、青、蓝、品红、橙、灰 128、黑、白、暗红 100、粉 (255, 150, 150)、(255, 0, 1)）、360 级色相扫描条，以及第 10 节的 64 × 48 字节图与其反相图、三通道图。

### 12.1 逐项结论

| 算子 | 结论 |
|---|---|
| `trans_from_rgb`（`'hsv'`） | 三个输入各为单通道；**byte 输入时 H / S / V 都是 byte、范围 0 ~ 255**：H 由 0° ~ 360° 线性量化（红 0、橙 21、黄 43、绿 85、青 128、蓝 170、品红 213，色相扫描最大 254，(255, 0, 1) 为 255），S、V 为 0 ~ 255；灰度色（S = 0）的 H 为 0。**uint2 输入时量纲不同**：H 为 0 ~ 3600（0.1°）、S 为 0 ~ 10000、V 为原灰度；real 输入 H 为弧度、S 为 0 ~ 1；int4 同 uint2；**int2 报 #9001**。三幅输入尺寸不同报 #3117。第一个输入传三通道图不报错（只用第一通道） |
| `decompose3` | 单通道、双通道报 **#3359**“通道数不对”；**四通道不报错，取前三个通道**；uint2 三通道正常 |
| `get_grayval` | 对三通道图一次返回三个通道的值（例：黄色块 → [255, 255, 0]），点超出图像报 #1302。编辑窗口取色直接用它 |
| `threshold` | 下限大于上限报 #3100，因此色相跨 0 不能用一次 `threshold`，须分两段 |
| 色相跨 0 方案 | `threshold(H, HueMin, 255)` ∪ `threshold(H, 0, HueMax)`，再与 S、V 的阈值区域求交：H ∈ [230, 255] ∪ [0, 20]、S ≥ 100、V ≥ 100 时恰好得到红、红偏蓝、红偏绿、暗红、粉、(255, 0, 1) 六个色块（相邻色块经 `connection` 合并为两块，面积各 1200），不含其他色块；不跨 0 的 H ∈ [30, 60] 只得到黄色块。方案可行 |
| `add_image` / `sub_image` / `mult_image` / `div_image` / `abs_diff_image` / `max_image` / `min_image` | 公式：加 (g1 + g2) × Mult + Add，减 (g1 − g2) × Mult + Add，乘 g1 × g2 × Mult + Add，除 g1 / g2 × Mult + Add，差的绝对值 \|g1 − g2\| × Mult（无 Add），取大 / 取小无参数；结果四舍五入（100 × 100 × 0.001 + 0.5 → 11）。**byte 与 byte 运算的输出仍为 byte（不升为 uint2），超出 0 ~ 255 时饱和截断、不报错**（200 + 200 → 255、100 × 50 → 255、\|0 − 255\| × 2 → 255）；**除数为 0 的像素结果为 0，不报错**（byte 与 real 都如此）。两图尺寸不同报 **#3117**，通道数不同报 **#3122**（三通道与单通道互不兼容）；三通道与三通道逐通道运算。像素类型不同时：`add_image` 接受 byte + uint2（输出 uint2），其余六个报 **#9001**；uint2 / int2 / real 同类型都可运算。两图定义域不同时取交集 |

### 12.2 由探测确定的实现约定

- **颜色方式只接受 byte 三通道图**：工具先检查通道数（`decompose3` 对四通道不报错，不能依赖 #3359），不是三通道时报“需要三通道彩色图像（当前 n 通道）”；不是 byte 时报“颜色方式需要 byte 彩色图像（当前 uint2）”——HSV 的 0 ~ 255 范围只对 byte 成立（uint2 / real 的 H、S 量纲不同），RGB 阈值的 0 ~ 255 同理，建议先接灰度增强的 ConvertType 转成 byte。`decompose3` 的 #3359 不再出现，但仍转成同一句中文作为后备。
- **参数**：`HueMin` / `HueMax` / `SaturationMin` / `SaturationMax` / `ValueMin` / `ValueMax` 与 `RedMin` … `BlueMax` 均为 0 ~ 255 的整数；`HueMin > HueMax` 表示跨 0（两段并集），其余通道下限大于上限时报错（`threshold` 会报 #3100）。
- **图像运算的前置检查**：七种运算（含原有 `Add` / `Sub`，经使用方确认）在调用算子前检查两图宽、高、通道数，不一致时报“宽×高×通道”对照的中文错误；算子仍报 #3117 / #3122 时转成同一句话。像素类型不同时 `Add` 沿用 HALCON 的自动转换，其余运算的 #9001 转成带两种类型的中文错误。`Add` / `Sub` 成功路径、输出与日志不变。
- **byte 结果出界**：`Mult` / `Div` / `AbsDiff` 在 byte 输入时日志注明输出类型为 byte、超出部分被截断，并建议配合 `Multi` / `Add` 或先接灰度增强的 ConvertType；编辑窗口加一行静态说明。除数为 0 的像素结果为 0，一并写入说明。

## 13. 第二批（IP-08 + IP-02）实现与验收记录

**开工依据**：基线 `origin/main`（`115527b`，含第一批）；按第一批评审的模式先探测后实现，第 12 节在写代码前完成，计划与探测冲突处已改正（IP-02 表格补公式与 `AbsDiff` 无加数、byte 结果截断；IP-08 改为要求 byte 三通道）。

**与任务说明不一致处（如实记录）**

| 项 | 处理 |
|---|---|
| “Add / Sub 现有行为与错误信息逐字不变”与“运行时校验两图尺寸与通道数” | 经使用方确认七种运算都做前置检查：`Add` / `Sub` 成功路径、输出与日志不变，尺寸或通道不一致时由原来 HALCON 的英文 #3117 / #3122 改为中文“宽×高×通道”对照；`Sub` 的像素类型不一致（#9001）同样改为中文 |
| 颜色方式的输入 | 任务写“非三通道报错”。探测表明 HSV / RGB 的 0 ~ 255 范围只对 byte 成立，另要求 byte 类型（中文说明并建议 ConvertType）；`decompose3` 对四通道不报错，三通道由工具检查 |
| 颜色方式的“未找到” | 空结果在 22.11 下仍是 1 个空区域对象（连 `connection` 也是），`Found = true`、`FailWhenNotFound` 不生效——这是 `RegionOutput.Set` 的既有问题，影响 10 个区域工具；经使用方确认不并入本批，颜色方式沿用与灰度方式相同的语义（用例固定），问题另记 `REVIEW-FIX-PROGRESS.md`“2026-10-08 已知边界（未修）” |
| 取色的图像点击 | 沿用批一标定窗口的做法，用真实鼠标点击（`HalconImageView.BeginPickPoint`） |

**验收结果**

- 单元/集成测试：新增 `ImageToolsBatch2Tests` 51 个用例（含 Theory 数据行），其中 22 个为 HALCON 门禁（`Requires=HALCON`）：
  - 颜色方式：合成色块图（红、绿、蓝、黄、偏蓝红 (255, 0, 30)、灰、白）上，ColorHsv 黄色只提取黄色块；色相 230 ~ 20（跨 0）提取红色与偏蓝红色两块，`Connection` 拆成两块；ColorRgb 绿 / 蓝 / 黄各只提取对应色块；每例都与直接调用 `decompose3` / `trans_from_rgb` / `threshold` / `intersection` 的结果逐像素一致；`UsedThreshold` 为 NaN；单 / 双 / 四通道与 uint2 输入的中文错误；空结果语义与灰度阈值相同；原有手动阈值不受影响。
  - 图像运算：五种新运算在字节图、彩色图、缩小定义域图、real 图上与直接调用逐像素一致，输出为新图像且不释放输入；`Add` / `Sub` 与旧版一致且日志不变；byte + uint2 时 `Add` 输出 uint2、`Sub` 报中文；七种运算的尺寸 / 通道不一致中文错误；byte 出界日志。
  - 非门禁 29 个：12 个颜色参数越界、5 个通道下限大于上限（色相跨 0 不报错）的校验与运行措辞一致；颜色参数只在对应方式显示与校验；阈值方式枚举顺序与按数字保存、历史文件缺省全范围；图像运算逐运算显隐、枚举顺序与按数字保存、历史文件缺省 `Add`；输出不与参数同名；工具箱改名与默认模块名、阈值分割路由到专用窗口且移出视觉预览、通用窗口的静态说明。
- 全量 `dotnet test`（`Category!=Soak`）1146 个通过（基线 1095 + 新增 51，含 `examples\*.vflow.json` 加载用例）；排除带 `Requires=HALCON` 标注的用例后 1076 个通过（基线 1047 + 新增非门禁 29）。构建 0 错误，唯一警告为既有的 `MatchMeasureBatch4Tests.cs(143)` xUnit2000。
- 界面验收：脚本经 Windows UI Automation 驱动真实的 `VisionFlow.WpfApp.exe`，41 项检查全部通过（连续两次）：工具箱已无“图像加减”，新建“图像运算1”，默认参数运行与无界面一致；阈值分割打开新的专用窗口，方式下拉 9 项，Threshold / ColorHsv / ColorRgb 显隐正确；容差 15、真实鼠标在红色块取色，显示 RGB (255, 0, 0) / HSV (0, 255, 255)，自动填入色相 241 ~ 15（跨 0）、饱和度与明度 240 ~ 255，即时预览提取红色与偏蓝红色两块（面积 5000）；切到 ColorRgb 在绿色块取色，填入 R 0 ~ 15、G 240 ~ 255、B 0 ~ 15，只提取绿色块；切回 HSV（取色结果保留）开启 Connection 执行测试得到 2 块；运行结果（数量、`UsedThreshold`、下游区域面积）与无界面一致。图像运算编辑窗口运算下拉 7 项并显示静态说明，Mult / Div / AbsDiff / Max / Min 依次切换，`Multi` / `Add` 显隐正确、预览为本工具输出且随运算变化，每次运行的下游灰度统计与无界面一致；保存后 `SegmentMethod` 7、`Operation` 6，重新加载再运行一致，侧栏回显。
- 验证中发现的问题：
  1. 阈值分割窗口的“取色容差”输入框宽 64 时两位数显示不全（截图发现），改为 84。
  2. 既有问题（不属于本批，已单独记录）：区域工具的空结果仍报 `Found = true`，见上表。
- 脚本问题：无（初始流程有意引用待新建节点，打开时的校验提示框由脚本关闭并核对内容，沿用第一批做法）。
