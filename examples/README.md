# VisionFlow 示例流程

这些示例用于验证流程加载、参数编辑、运行预览和输出读取。
- 结构型示例默认使用运行时传入的 `Input.Image`，可在编辑器中先打开 `src\Image\razors1.png` 后运行。
- HALCON 官方对照示例内置 `loadimage`，直接读取本机 HALCON 安装自带图像；图像路径默认保存为 `%HALCONIMAGES%\...`，可在 `图像加载` 编辑窗里点 `HALCON 示例` 打开分组浏览窗口，按目录分类挑图并记录最近使用；主编辑窗顶部提供最近图像快捷入口，支持固定/取消固定与清空最近记录。

| 文件 | 覆盖能力 |
|---|---|
| `threshold-region.vflow.json` | 二值化、区域形态学、区域筛选、区域特征、流程输出 |
| `xld-line.vflow.json` | 边缘提取、边缘筛选、拟合直线、流程输出 |
| `region-measure.vflow.json` | 二值化、区域筛选（按高度排除边框和小斑点）、按区域形状初始化的亚像素矩形测量、部分失败计数、流程输出 |
| `blister-check.vflow.json` | 复现 HALCON `check_blister`：区域定位 → 5×3 阵列检测格（PerShape）按 `BaseToCurrentMatrix` 跟随 → B 通道局部阈值分割药片 → 分区检测（面积 < 3800 或最小灰度 < 60 为错误，无药片为缺失）。原示例是先把整图对齐到参考位姿，这里改为检测格跟随，结果逐张一致。使用 HALCON 示例图像 `%HALCONIMAGES%\blister\blister_01~06.png`，图像不随仓库分发；示教基准取自 `blister_reference.png` |
| `pizza-salami.vflow.json` | 复现 HALCON `color_segmentation_pizza`：通道分解 → RGB 转 CIELab → b 通道阈值分割披萨（取最大区域、凸包）→ 限定域内阈值分割香肠 → 面积筛选 → 圆形闭运算/开运算。在 `%HALCONIMAGES%\color\pizza_01~03.png` 上与原示例逐像素一致（香肠区域 7 / 5 / 4 个）；图像不随仓库分发 |
| `cookie-box.vflow.json` | 复现 HALCON `locate_cookie_box_multiple_models`：饼干盒 4 个面各一个描述子匹配（未找到时继续），各接一个数值区间分类（转角 45~135° 或 225~315° 为侧放，135~225° 为倒放，其余为正放，未找到为“未找到”），流程输出 Face1~Face4。原示例使用标定描述子与三维位姿，这里改用非标定描述子与平面内转角，在 `cookie_box_11~21` 上与原示例的结论逐张一致。模板图不随仓库分发：ROI 已预填，使用前在编辑器中逐个打开 `%HALCONIMAGES%\packaging\cookie_box_reference_01~04.png` 并点击“创建模型”（每个模型首次训练数秒，之后命中本机缓存） |
| `wafer-chips.vflow.json` | 晶圆 chip 行列定位：灰度分割 → 面积筛选 → 区域排序（行列编号，自动网格角度）→ 以排序后的区域为初始区域做亚像素矩形测量 → 角度换算（弧度转度并折算到 [-90°, 90°)），流程输出 ChipCount / RowCount / ColumnCount 与逐一对齐的 RowIndices、ColumnIndices、Rows、Columns、Angles 数组。配套合成图像 `images\wafer-chips.png`（498 颗 chip，27 行 × 23 列，网格旋转 1.5°，边缘为圆形，中间缺 3 颗；每颗 chip 左上角有暗色方向标记）：全部编号正确、0 失败，单次约 50 ms。在加了噪声的同类图像上，矩形测量中心平均误差 0.06 px，灰度分割重心因方向标记偏离约 0.45 px |
| `datacode-default-settings.vflow.json` | 对照 HALCON `2d_data_codes_default_settings`：载入 `%HALCONIMAGES%\datacode\qrcode\qr_workpiece_01.png`，分别以 `standard_recognition / enhanced_recognition / maximum_recognition` 运行 `读码` 工具，输出三档识别结果与质量评分，用于演示 `RC-01` 的二维读码三档强度配置 |
| `ocr-expiration-date.vflow.json` | 对照 HALCON `find_text_expiration_date`：载入 `%HALCONIMAGES%\ocr\medication_package_02_right.png`，使用 `字符识别` 的 TextModel 引擎与 `Industrial_Rej` 分类器读取药盒有效期，并用正则 `\d\d/\d\d\d\d` 输出 `PatternOk`，用于演示 `RC-02` 的日期码识别 |
| `color-fuses-classify.vflow.json` | 对照 HALCON `color_fuses`：载入 `%HALCONIMAGES%\color\color_fuses_00.png`，用预置 ROI 框出 5 个保险丝，再用 `颜色识别` 以 HSV 参考色均值进行逐区域分类，输出 `Orange / Red / Blue / Yellow / Green` 标签序列，演示 `RC-03` 的参考色判别 |
| `color-pieces-mlp.vflow.json` | 对照 HALCON `color_pieces`：训练样本取自 `%HALCONIMAGES%\color\color_pieces_00.png`，将预训练好的 `MLP` 分类器嵌入流程，对 `%HALCONIMAGES%\color\color_pieces_01.png` 执行 `颜色分割`，输出非空类数量与各类区域面积，演示 `RC-04` 的分类器式颜色分割 |
| `dl-detect-pills.vflow.json` | 对照 HALCON `detect_pills`：载入 `%HALCONIMAGES%\pill_bag\pill_bag_001.png`，用预训练检测模型 `%HALCONEXAMPLES%\hdevelop\Deep-Learning\Detection\detect_pills.hdl` 执行 `深度学习检测`（Device=Auto，MinScore=0.5），输出检出数量与最优目标的类别/分数，演示深度学习目标检测。典型结果：Count=12，BestClassName=Cognivia，BestScore≈1.0；示例设 `FailWhenNotFound=false`，空结果不判失败 |
| `dl-classify-pill-defects.vflow.json` | 对照 HALCON `classify_pill_defects`：载入 `%HALCONIMAGES%\pill\ginseng\contamination\pill_ginseng_contamination_001.png`，用预训练分类模型 `%HALCONEXAMPLES%\hdevelop\Deep-Learning\Classification\classify_pill_defects.hdl` 执行 `深度学习分类`（TopK=3，MinScore=0），输出 Top1 类别/分数与 Top3 类别序列，演示深度学习缺陷分类。典型结果：Top1=good（0.606），Top3=[good, crack, contamination]；`FailWhenNotFound=false` |
| `dl-segment-pill-defects.vflow.json` | 对照 HALCON `segment_pill_defects`：载入 `%HALCONIMAGES%\pill\ginseng\contamination\pill_ginseng_contamination_001.png`，用预训练分割模型 `%HALCONEXAMPLES%\hdevelop\Deep-Learning\Segmentation\segment_pill_defects.hdl` 执行 `深度学习分割`（MinScore=0.5），输出非空类数量、各类区域面积与类别名，演示深度学习像素级分割。典型结果：Count=2（共 3 类），Areas=[189507, 93, 0]（good 占绝大部分、contamination 仅 93 px、crack 为 0）；`FailWhenNotFound=false` |

示例流程只保存视觉流程结构和工具参数，不包含相机、PLC、配方或生产业务配置。
