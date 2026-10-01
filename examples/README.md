# VisionFlow 示例流程

这些示例用于验证流程加载、参数编辑、运行预览和输出读取。示例默认使用运行时传入的 `Input.Image`，可在编辑器中先打开 `src\Image\razors1.png` 后运行。

| 文件 | 覆盖能力 |
|---|---|
| `threshold-region.vflow.json` | 二值化、区域形态学、区域筛选、区域特征、流程输出 |
| `xld-line.vflow.json` | 边缘提取、边缘筛选、拟合直线、流程输出 |
| `region-measure.vflow.json` | 二值化、区域筛选（按高度排除边框和小斑点）、按区域形状初始化的亚像素矩形测量、部分失败计数、流程输出 |
| `blister-check.vflow.json` | 复现 HALCON `check_blister`：区域定位 → 5×3 阵列检测格（PerShape）按 `BaseToCurrentMatrix` 跟随 → B 通道局部阈值分割药片 → 分区检测（面积 < 3800 或最小灰度 < 60 为错误，无药片为缺失）。原示例是先把整图对齐到参考位姿，这里改为检测格跟随，结果逐张一致。使用 HALCON 示例图像 `%HALCONIMAGES%\blister\blister_01~06.png`，图像不随仓库分发；示教基准取自 `blister_reference.png` |
| `pizza-salami.vflow.json` | 复现 HALCON `color_segmentation_pizza`：通道分解 → RGB 转 CIELab → b 通道阈值分割披萨（取最大区域、凸包）→ 限定域内阈值分割香肠 → 面积筛选 → 圆形闭运算/开运算。在 `%HALCONIMAGES%\color\pizza_01~03.png` 上与原示例逐像素一致（香肠区域 7 / 5 / 4 个）；图像不随仓库分发 |
| `cookie-box.vflow.json` | 复现 HALCON `locate_cookie_box_multiple_models`：饼干盒 4 个面各一个描述子匹配（未找到时继续），各接一个数值区间分类（转角 45~135° 或 225~315° 为侧放，135~225° 为倒放，其余为正放，未找到为“未找到”），流程输出 Face1~Face4。原示例使用标定描述子与三维位姿，这里改用非标定描述子与平面内转角，在 `cookie_box_11~21` 上与原示例的结论逐张一致。模板图不随仓库分发：ROI 已预填，使用前在编辑器中逐个打开 `%HALCONIMAGES%\packaging\cookie_box_reference_01~04.png` 并点击“创建模型”（每个模型首次训练数秒，之后命中本机缓存） |
| `wafer-chips.vflow.json` | 晶圆 chip 行列定位：灰度分割 → 面积筛选 → 区域排序（行列编号，自动网格角度）→ 以排序后的区域为初始区域做亚像素矩形测量 → 角度换算（弧度转度并折算到 [-90°, 90°)），流程输出 ChipCount / RowCount / ColumnCount 与逐一对齐的 RowIndices、ColumnIndices、Rows、Columns、Angles 数组。配套合成图像 `images\wafer-chips.png`（498 颗 chip，27 行 × 23 列，网格旋转 1.5°，边缘为圆形，中间缺 3 颗；每颗 chip 左上角有暗色方向标记）：全部编号正确、0 失败，单次约 50 ms。在加了噪声的同类图像上，矩形测量中心平均误差 0.06 px，灰度分割重心因方向标记偏离约 0.45 px |

示例流程只保存视觉流程结构和工具参数，不包含相机、PLC、配方或生产业务配置。
