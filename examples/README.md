# VisionFlow 示例流程

这些示例用于验证流程加载、参数编辑、运行预览和输出读取。示例默认使用运行时传入的 `Input.Image`，可在编辑器中先打开 `src\Image\razors1.png` 后运行。

| 文件 | 覆盖能力 |
|---|---|
| `threshold-region.vflow.json` | 二值化、区域形态学、区域筛选、区域特征、流程输出 |
| `xld-line.vflow.json` | 边缘提取、边缘筛选、拟合直线、流程输出 |

示例流程只保存视觉流程结构和工具参数，不包含相机、PLC、配方或生产业务配置。
