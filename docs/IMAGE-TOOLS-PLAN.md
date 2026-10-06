# 图像处理补充开发计划

编写日期：2026-10-06
状态：待开发
范围：工具箱“02 图像处理”（`VisionFlow.Tools\Tools\ImageTools.cs`），以及与图像相关的“阈值分割”颜色方式。
HALCON 版本基线：20.11 及以上。
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
| `Gauss` | `gauss_filter` | `Size`（3、5、7、9、11） |
| `Median` | `median_image` | `MaskType`（`circle` / `square`）、`Radius`、`Margin`（默认 `mirrored`） |
| `Smooth` | `smooth_image` | `SmoothFilter`（`deriche1` / `deriche2` / `shen` / `gauss`）、`Alpha` |
| `Bilateral` | `bilateral_filter` | `SigmaSpatial`、`SigmaRange`（引导图使用输入图本身） |
| `Emphasize` | `emphasize` | 复用 `Width` / `Height`，新增 `Factor` |
| `SobelAmp` | `sobel_amp` | `SobelType`（`sum_abs` / `sum_sqrt` / `x` / `y` / `x_binomial` / `y_binomial` 等）、`Size` |
| `GrayErosion` / `GrayDilation` / `GrayOpening` / `GrayClosing` | `gray_erosion_rect` / `gray_dilation_rect` / `gray_opening_rect` / `gray_closing_rect` | 复用 `Width` / `Height`（注意 HALCON 参数顺序为高、宽） |

- 输出不变：`Image`。
- 编辑窗口按方式显示参数；`Gauss` 的 `Size` 用下拉框限定合法值。

### IP-02 图像加减扩展为图像运算

- 工具箱显示名改为“图像运算”，ID `add-sub-image` 与类型名 `AddSubImageTool` 不变。
- `ImageArithmeticOperation` 末尾追加：

| 操作 | 算子 | 参数 |
|---|---|---|
| `Mult` | `mult_image` | 复用 `Multi` / `Add` |
| `Div` | `div_image` | 复用 `Multi` / `Add` |
| `AbsDiff` | `abs_diff_image` | 复用 `Multi` |
| `Max` | `max_image` | 无 |
| `Min` | `min_image` | 无 |

- 两张图尺寸或通道数不一致时明确失败。
- `Mult` / `Div` 结果可能超出字节图范围，编辑窗口提示配合 `Multi` / `Add` 调整，或接 IP-03 的像素类型转换。

### IP-08 阈值分割增加颜色方式

- `ThresholdSegmentMethod` 末尾追加 `ColorHsv`、`ColorRgb`。
- 输入仍为一个图像引用，要求三通道彩色图，单通道时明确失败。
- `ColorHsv` 参数：`HueMin` / `HueMax`（0~255，HALCON `trans_from_rgb` 的 HSV 量化范围）、`SaturationMin` / `SaturationMax`、`ValueMin` / `ValueMax`。`HueMin > HueMax` 表示跨越 0（如红色 230~20）。
- `ColorRgb` 参数：`RedMin` / `RedMax`、`GreenMin` / `GreenMax`、`BlueMin` / `BlueMax`。
- 实现：`decompose3` → （HSV 时）`trans_from_rgb` → 各通道 `threshold` → `intersection`；色相跨 0 时为两段阈值的并集再求交。
- 输出不变：`Region`、`UsedThreshold`（颜色方式输出 NaN）、`Count`、`Found`；`Connection` 选项照常生效。
- 编辑窗口：在图像上点击取色，自动填入以该点为中心的范围（容差可调），并实时预览提取结果。

## 5. 新建工具

### IP-03 灰度增强

- 工具箱：`02 图像处理 / 灰度增强`，ID `gray-enhance`，类 `GrayEnhanceTool`。
- 输入：图像（必填）。输出：`Image`。

| 方式 `Method` | 算子 | 参数 |
|---|---|---|
| `Linear` | `scale_image` | `Mult`、`Add` |
| `AutoStretch` | `scale_image_max` | 无（拉伸到全灰度范围） |
| `PercentStretch` | `min_max_gray`（`Percent`）+ `scale_image` | `Percent`（两端各舍弃的百分比，默认 1） |
| `EquHisto` | `equ_histo_image` | 无 |
| `Invert` | `invert_image` | 无 |
| `Gamma` | `gamma_image` | `Gamma`、`Offset`、`Threshold`、`MaxGray`、`Encode` |
| `Illuminate` | `illuminate` | `MaskWidth`、`MaskHeight`、`Factor` |
| `RgbToGray` | `rgb1_to_gray` | 无 |
| `ConvertType` | `convert_image_type` | `NewType`（`byte` / `uint2` / `int2` / `real`） |

- `PercentStretch` 只统计图像定义域内的灰度；配合 ReduceDomain 可按 ROI 拉伸。

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

- IP-01：每种方式输出与直接调用 HALCON 算子逐像素一致；`Mean` 结果与旧版一致。
- IP-02：新增运算与直接调用 HALCON 一致；尺寸不一致时明确失败。
- IP-03：`PercentStretch` 对已知直方图的合成图像结果正确；`ConvertType` 输出类型正确。
- IP-04：在新图上取一点，用 `InverseHomMat` 映射回原图，与原图对应点误差小于 0.5 像素（缩放、旋转、镜像、裁剪各一例）。
- IP-05 / IP-06：在合成圆环上画一个缺陷，展开后检测到的区域经逆变换后与原缺陷重合（面积误差小于 5%）。
- IP-07：二值图前景、背景灰度正确；绘制方式只改变区域内（或边缘）像素。
- IP-08：合成色块图中，指定颜色范围只提取对应色块；色相跨 0 的红色能正确提取。
- 全部回归测试和 `examples\*.vflow.json` 通过。

## 9. 开发顺序

1. IP-01（图像滤波）、IP-03（灰度增强）：使用频率最高。
2. IP-08（颜色阈值）、IP-02（图像运算）。
3. IP-04（图像几何变换）。
4. IP-05、IP-06（极坐标展开与逆变换），一起开发。
5. IP-07（区域转图像）。
