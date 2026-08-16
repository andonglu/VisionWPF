# VisionFlow WPF 编辑页设计规范

## 项目位置

WPF 编辑器项目：`VisionFlow.WpfApp`

WPF 工具编辑页项目：`VisionFlow.WpfToolEditors`

WinForms 编辑器项目：`VisionFlow.App`

共用编辑核心项目：`VisionFlow.EditorCore`

## 编辑页布局规范

- 窗口最小尺寸：`1180 x 760`，复杂工具推荐 `1280 x 820`。
- 主体采用左右两栏：左侧图像/ROI/预览区，右侧参数区。
- 左右栏间距：`12px`；页面内容边距：`16px`。
- 参数区使用卡片分组，卡片圆角 `8px`，卡片内边距 `16px`，卡片间距 `12px`。
- 参数字段使用两列网格：标签宽 `96px`，输入控件自适应。
- 字段行间距：`10px`，分组之间 `16px`。
- 输入控件高度：`32px`；主要按钮高度：`32px`，最小宽 `96px`。
- DataGrid 行高：`28px`，用于运行结果和示教结果。

## 模板匹配编辑页规范

- 顶部 Tab：`示教建模` / `运行参数`。
- `示教建模`：
  - 左侧显示 ROI 编辑器。
  - 右侧依次显示：模板状态、创建模板参数、基准姿态、示教测试结果。
  - `创建模板` 按钮放在创建参数卡片底部。
- `运行参数`：
  - 左侧显示测试图像与匹配轮廓。
  - 右侧显示：输入与模型状态、查找参数、运行测试结果。
- 错误必须用弹窗显示明确原因；HALCON 创建模板异常不能静默失败。

## 通用工具编辑页规范

- 没有专用 WPF 编辑页的工具统一使用 `WpfGenericToolEditWindow`。
- 左侧为参数区，按 `基础 / 输入引用 / 参数` 三个卡片分组。
- 右侧为预览区，包含查看对象下拉框、运行预览、适应窗口和统计表。
- 字符串文件路径参数提供浏览按钮；输入引用参数从上游候选中选择。
- 运行预览基于 `ToolEditContext.LastRunContext` 和 `Input.Image` 构建临时上下文，不修改流程运行状态。

## ROI 编辑控件规范

- WPF 工具编辑页使用 `WpfRoiEditorControl`。
- 工具栏按钮只显示图标，含 Tooltip 文本说明。
- ROI 图像交互暂复用现有 `HalconImageView`，外层工具栏和 ROI 列表必须使用 WPF 实现。

## 主题 token

所有颜色使用 `Themes\Tokens.xaml` 的语义资源，例如：

- 背景：`SolidBackgroundFillColorBaseBrush`
- 卡片：`LayerFillColorDefaultBrush`
- 文本：`TextFillColorPrimaryBrush` / `TextFillColorSecondaryBrush`
- 描边：`ControlStrokeColorDefaultBrush`
- 主按钮：`AccentFillColorDefaultBrush`
- 错误：`SystemFillColorCriticalBrush`
- 成功：`SystemFillColorSuccessBrush`
