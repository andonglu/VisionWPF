# 内置视觉工具设计评审与整改记录

评审日期：2026-10-01
评审范围：`VisionFlow.Tools` 全部内置工具、`VisionFlow.Base\Variables\HomMat2D.cs`，以及工具在 `ToolboxRegistry` / `WpfToolEditorRouter` 中的接入情况。
评审方法：逐个阅读工具源码；对疑似缺陷用独立 HALCON 探测程序实测（结果见各条目“实测”）。
基线：评审开始时 `VisionFlow.Tests` 共 223 项，全部通过。

## 1. 工具清单

工具箱共 45 个条目：41 个视觉工具 + 4 个逻辑节点。（本表为 2026-10-01 评审基线快照；评审后各批次新增工具未逐行更新，仅"06 识别工具"行按 2026-10-09 识别线 RC-01~RC-04 完成更新为 4 个。）

| 分类 | 数量 | 工具（工具箱 ID） |
|---|---|---|
| 00 图像采集 | 1 | 图像加载 `loadimage` |
| 01 定位匹配 | 5 | 模板匹配 `match`、灰度匹配 `gray-match`、缩放形状匹配 `scaled-shape-match`、局部变形匹配 `deformable-match`、区域定位 `regionpose` |
| 02 图像处理 | 7 | 均值滤波、图像仿射变换、ReduceDomain、图像加减、通道分解、三通道合成、RGB 色彩空间转换 |
| 03 区域处理 | 12 | 阈值分割、区域处理、手动 Region、Region 相减 / 合并 / 交集 / 形状转换 / Union1、形态学、Region 特征值、灰度统计、区域筛选 |
| 04 XLD 轮廓 | 5 | 边缘提取、边缘选择、边缘合并、XLD 分割、XLD 特征值 |
| 05 几何测量 | 10 | 椭圆 / 直线 / 矩形 / 圆形测量、一维卡尺、一维圆弧卡尺、拟合直线、拟合圆、线线交点、图像坐标转世界坐标 |
| 06 识别工具 | 4 | 读码（一维码/二维码）、字符识别、颜色识别、颜色分割 |
| 逻辑控制 | 4 | IfElse 分支、For 循环(次数)、For 循环(集合)、流程输出 |

此外有 7 个仅用于加载历史流程的兼容类型（`AutoThresholdTool` 等 5 个阈值壳、`MorphologyRectTool` / `MorphologyCircleTool`），不进入工具箱。

## 2. 问题清单

优先级：P0 = 结果错误；P1 = 设计缺陷，影响流程搭建或结果可信度；P2 = 一致性、可维护性、性能；P3 = 工程整洁。

### TR-01（P0）`HomMat2D.TransformPose` 旋转方向相反

- 位置：`VisionFlow.Base\Variables\HomMat2D.cs` `TransformPose`。
- 问题：按 `(sin φ, cos φ)` 表示方向，但 HALCON 行列坐标下 `phi` 的方向向量是 `(-sin φ, cos φ)`（行轴向下，角度逆时针为正）。结果角度变成 `φ - θ`，正确值应为 `φ + θ`。
- 实测：`vector_angle_to_rigid` 旋转 +0.5 rad 后，`TransformPose(phi=0)` 得到 -0.5；用 HALCON `affine_trans_region` + `orientation_region` 得到 +0.5（模 π）。
- 影响：椭圆测量、矩形测量、一维卡尺的跟随角度，以及 WPF 跟随测量编辑器的 ROI 预览；只要定位结果有旋转就会出错。

### TR-02（P0）一维圆弧卡尺在斜向变成切向

- 位置：`VisionFlow.Tools\Tools\MeasureTools.cs` `ArcCaliperFollowMeasureTool.MeasureOne`。
- 问题：弧上点按 `(r + R·sin φ, c + R·cos φ)` 计算，而测量矩形的 `phi` 按 HALCON 约定解释。两者只在 0°/90°/180°/270° 重合，45° 等方向卡尺与半径垂直；跟随时用 `atan2(Δrow, Δcol)` 反推角度，也沿用了错误约定。
- 实测：半径 50 的理想圆盘、8 个卡尺，工具公式在 45°/135°/225°/315° 分别找到 6/4/10/4 个伪边缘；按 HALCON 约定每个卡尺恰好 1 个，距圆心 50±0.5。

### TR-03（P0）线线交点用外接矩形取直线端点

- 位置：`VisionFlow.Tools\Tools\GeometryTools.cs` `IntersectionLinesTool.GetLinePoints`。
- 问题：`smallest_rectangle1_xld` 返回的是外接矩形左上/右下角。从右上到左下的直线被当成另一条对角线；折线或含噪轮廓也不代表直线。平行线时 `intersection_lines` 返回空元组，`row.D` 抛出 `HTupleAccessException`。
- 实测：直线 (0,100)→(100,0) 与竖线 col=20 的交点，工具输出 Row=20，正确值 Row=80；平行线抛异常。

### TR-04（P1）拟合直线多结果时 Xld 连成一条折线

- 位置：`GeometryTools.cs` `FitLineTool.Run`。
- 问题：`gen_contour_polygon_xld(row1 ++ row2, col1 ++ col2)` 把 N 条线的端点拼成一条 2N 点折线。
- 实测：两条输入轮廓得到 1 个 XLD 对象，4 个点呈之字形。

### TR-05（P0）缩放形状匹配的变换矩阵语义与其他匹配不一致

- 位置：`HalconTools.cs` `HalconScaledShapeMatchTool.Run`。
- 问题：`HomMats` / `BestHomMat` / `Items[i].HomMat` 用 `FromScaledPose`，即“模型原点 → 匹配位姿”，忽略 `BaseRow/BaseColumn/BaseAngle`；模板匹配、灰度匹配、局部变形匹配都是“固定基准位姿 → 匹配位姿”。下游测量接缩放匹配时，跟随位置整体偏移。

### TR-06（P1）“未找到目标”与“执行错误”都作为失败，流程停止

- 位置：各工具 `NodeResult.Fail`，`FlowNode.RunChildren` 遇到失败即中断。
- 问题：匹配 0 个、一维码未识别、区域筛选结果为空、XLD 结果为空、测量未找到边缘等都会中断整个流程，无法用 IfElse 按数量或是否找到走 NG 分支。
- 待决：处理方式涉及行为变化，需与使用方确认。

### TR-07（P1）部分轮廓输出类型为裸 `HObject`，无法接入 XLD 工具

- 位置：四个匹配工具的 `Contours` / `ResultContour`，六个跟随测量工具的 `ResultContour`。
- 问题：引用候选按 `expectedType.IsAssignableFrom` 过滤，`HObject` / `List<HObject>` 不能赋给 `HalconXld`。拟合直线 / 拟合圆 / 线线交点 / 边缘选择无法直接引用匹配或测量轮廓，例如“直线测量 → 线线交点”连不起来。

### TR-08（P1）多矩阵测量静默丢弃失败项

- 位置：六个跟随测量工具。
- 问题：遍历矩阵集合时只要有一项成功就返回成功，失败项不写日志；`Row` 等单值输出只保留最后一次成功结果；`Results` 变量的 `Count` 固定为 0。
- 附带：一维卡尺 `Distances` 与 HALCON `measure_pos` 语义不符（HALCON 返回 N-1 个相邻边距离，这里按边序号对齐，最后一条边补 0）。

### TR-09（P2）特征值输出拍平，无法对应特征与对象

- 位置：`RegionFeaturesTool`、`XldFeaturesTool`、`SelectContourTool`。
- 问题：多特征 × 多对象的值拍平成一个 `Values` 数组，下游无法区分；`SelectContourTool` 多个特征共用一组 `Min/Max`。

### TR-10（P2）重复实现

- `HalconModelMatchTool` 未继承 `HalconTemplateMatchToolBase`，重复一份参数、图像加载和输出逻辑。
- `EllipseFollowMeasureTool` 未继承 `FollowMeasureToolBase`，重复矩阵解析、结果累积逻辑，且缺少 `MeasureTransition/MeasureSelect`。

### TR-11（P2）工具功能重叠

- “区域处理”已包含连通拆分、Union1、圆形开闭/膨胀/腐蚀，工具箱中又有独立的“形态学”“Region Union1”；“阈值分割”也带 Connection 选项。

### TR-12（P2）默认值不合理

- `EllipseFollowMeasureTool.ImagePath` 默认 `"模板匹配.Image"`，而模块名总是带编号（“模板匹配1”），该引用永远不存在。
- 匹配与测量的基准坐标默认值（`BaseRow=99`、`EllipseRow=28.1559` 等）按示例图写死。

### TR-13（P2）每次运行重复加载

- 匹配模型每次运行反序列化并清除；一维码每次新建条码模型；坐标转换每次读取标定文件。

### TR-14（P2）通道分解的输出声明与实际不符

- 位置：`ImageTools.cs` `DecomposeChannelsTool`。
- 问题：灰度图只产出 `Channel1`，但元数据声明了 `Channel1~3`；校验通过的 `Channel2` 引用在运行时找不到。`Index` 越界时静默截断。

### TR-15（P3）测试替身在正式程序集中

- 位置：`VisionFlow.Tools\Tools\MockTools.cs`（`MockMatchTool`、`SumTool`、`RecorderTool`、`DelegateTool` 为 public）。

## 3. 整改进度

决策记录（2026-10-01，使用方确认）：

- TR-06：每个相关工具新增 bool 参数 `FailWhenNotFound`（默认 true，保持历史行为）；关闭后输出 `Found=false`、数量为 0 并继续。
- TR-11：工具箱隐藏“形态学”“Region Union1”入口，类型与 ID 保留用于历史流程；功能统一到“区域处理”，并补齐矩形结构元素形态学。

| 编号 | 优先级 | 状态 | 说明 |
|---|---|---|---|
| TR-01 | P0 | 已完成 | `TransformPose` 改用 HALCON 方向约定；同类问题一并修正：WPF 矩形边缘卡尺预览、`Rectangle2Roi` 手柄与旋转手柄 |
| TR-02 | P0 | 已完成 | 圆弧卡尺按 HALCON 约定取点；跟随时保持扫描范围（整圆不再塌缩）、半径随缩放；整圆时首尾卡尺不重合；圆形测量跟随时起止角同步旋转 |
| TR-03 | P0 | 已完成 | 两点轮廓直接取端点，多点轮廓先拟合直线；平行/重合明确报告 |
| TR-04 | P1 | 已完成 | 每条拟合直线输出一条独立 XLD |
| TR-05 | P0 | 已完成 | 缩放匹配矩阵改为“基准位姿 → 匹配位姿（含缩放）”；矩形/椭圆/一维卡尺 ROI 尺寸随矩阵缩放系数变化 |
| TR-06 | P1 | 已完成 | 见决策记录；覆盖匹配 4、测量 6、区域 11（含区域定位、灰度统计）、XLD 6（含拟合直线/圆）、线线交点、一维码 |
| TR-07 | P1 | 已完成 | 匹配 `Contours` 改为 `HalconXld[]`、`ResultContour` 改为 `HalconXld`；测量 `ResultContour` 改为 `HalconXld`（卡尺矩形改为 XLD 轮廓） |
| TR-08 | P1 | 已完成 | 失败项逐条警告并输出 `FailedCount`；`Results` 数量同步；全部失败时单值输出为 NaN；卡尺 `Distances` 改为 measure_pos 语义 |
| TR-09 | P2 | 已完成 | 新增 `ObjectCount`、`Features`（按特征汇总）；边缘选择新增 `MinValues/MaxValues` |
| TR-10 | P2 | 已完成 | 四个匹配工具共用 `HalconTemplateMatchToolBase`；六个测量工具共用 `FollowMeasureToolBase.RunMeasurements`，椭圆测量补齐边缘极性/选择参数 |
| TR-11 | P2 | 已完成 | 见决策记录 |
| TR-12 | P2 | 已完成 | 椭圆测量默认图像改为 `Input.Image`；示例相关的基准/测量位置移到工具箱工厂 |
| TR-13 | P2 | 已完成（按数据取舍） | 匹配模型按实例缓存；标定文件按路径+修改时间缓存；条码模型不缓存（理由见变更日志） |
| TR-14 | P2 | 已完成 | 缺少的通道输出为 null 并警告；Index 越界失败；色彩转换 Index 同样校验 |
| TR-15 | P3 | 已完成 | 测试替身移入测试工程；删除未被引用的临时探针 `Probe.cs` |

## 4. 变更日志

### 2026-10-01 整改 TR-01 ~ TR-15

改动文件：

- `VisionFlow.Base\Variables\HomMat2D.cs`：`TransformPose` 修正；新增 `DirectionToPhi`、`PointOnCircle`（HALCON 约定）、`FromPosesScaled`、`ScaleFactor`。
- `VisionFlow.Tools\Tools\NotFoundPolicy.cs`（新增）：`INotFoundPolicy` 与统一的“未找到”结果处理。
- `VisionFlow.Tools\Tools\HalconTools.cs`：匹配工具统一基类（图像加载、输出、未找到、模型缓存）；`HalconModelMatchTool` 改为继承基类，保留 `ModelPath`、`AngleStart/AngleExtent` 历史属性；椭圆测量移出。
- `VisionFlow.Tools\Tools\MeasureTools.cs` / `FollowMeasureTools.cs`（拆分新增）：测量基类统一流程；六个测量工具（含椭圆）改为共用；圆弧/圆形跟随、卡尺距离与轮廓修正。
- `VisionFlow.Tools\Tools\GeometryTools.cs`：线线交点、拟合直线/圆、坐标转换标定缓存。
- `VisionFlow.Tools\Tools\RegionTools.cs`：`RegionOutput` 统一区域输出与未找到处理；区域处理补齐矩形形态学；特征值结构化输出；区域定位空输入处理。
- `VisionFlow.Tools\Tools\XldTools.cs`、`RecognitionTools.cs`、`ImageTools.cs`：未找到策略、边缘选择分特征范围、XLD 特征结构化输出、通道分解契约。
- `VisionFlow.EditorCore\Editing\ToolboxRegistry.cs`：隐藏“形态学”“Region Union1”；示例默认值移入工厂。
- `VisionFlow.EditorCore\Ui\DisplayOverlayBuilder.cs`：支持 `HalconXld` 集合叠加显示。
- `VisionFlow.Controls\Roi\Rectangle2Roi.cs`：手柄位置与旋转手柄按 HALCON 约定计算，与 `DispRectangle2` 绘制一致。
- `VisionFlow.WpfToolEditors`：路由先匹配椭圆测量专用窗口；匹配/跟随测量/椭圆测量编辑器新增“未找到时失败”复选框；`ResultContour` 按 `HalconXld` 显示；圆弧/矩形卡尺预览改用运行时同一套几何。
- `VisionFlow.Tests`：新增 `ToolGeometryTests`（10 项）、`ToolBehaviorTests`（17 项），迁入 `MockTools.cs`。
- `README.md`：工具清单与未找到策略说明。

行为变化（有意决策）：

- 定位结果有旋转时，椭圆/矩形/一维卡尺/圆弧卡尺/圆形测量的跟随角度改变（此前方向相反）；依赖旧结果的现场参数需复核。
- 缩放形状匹配的 `HomMats`/`BestHomMat`/`Items[i].HomMat` 含义改变；已保存流程中该工具的 `BaseRow/BaseColumn/BaseAngle` 现在参与计算，需确认示教值。
- 匹配工具 `Contours` 由 `List<HObject>` 改为 `HalconXld[]`，`ResultContour` 与测量工具 `ResultContour` 由 `HObject` 改为 `HalconXld`。
- 各工具在“未找到”时也会写出输出（数量 0、`Found=false`、单值 NaN、最佳结果为 null），默认仍返回失败。
- 一维卡尺/圆弧卡尺：`Distances` 只包含相邻边缘距离（每个卡尺 N-1 个），单条结果中最后一条边缘的 `Distance` 为 NaN（此前为 0）；空结果时 `First*` 为 NaN（此前为 0）。
- 特征值、灰度统计的 `First*` 在无结果时为 NaN（此前为 0）。
- 通道分解 `Index` 超出通道数时失败（此前静默截断）。
- 匹配基类默认 `BaseRow/BaseColumn` 由 99/79 改为 0；工具箱新建“模板匹配”时仍按示例模板设为 99/79。
- 工具箱条目由 45 个减为 43 个（39 个视觉工具 + 4 个逻辑节点）。

TR-13 取舍依据（独立探测程序实测，razors1.png 上创建的 42 KB 形状模型，Release，50 次平均）：

- 模型反序列化 9.06 ms，`find_shape_model` 2.23 ms，加载占单次匹配约 80%，因此按工具实例缓存模型句柄；缓存键为 `ShapeModelData` 引用，重新示教/导入会替换数组并触发重新加载；同一实例的并发运行串行执行；编辑事务按属性新建实例（并克隆 `byte[]`），副本之间不共享句柄。
- 条码模型创建 0.13 ms，且 `find_bar_code` 会把结果写入模型句柄，跨运行复用在并发时不安全，因此保持每次运行新建。

验证：`dotnet build .\VisionFlow.slnx` 0 错误；`dotnet test` 250 项全部通过（原 223 项 + 新增 27 项）。

### 2026-10-01 代码审查跟进

- 审查发现：跟随测量编辑器“当前图像示教”模式下，圆弧卡尺/圆形测量预览没有叠加定位旋转量，而运行时 `FollowArc` 会叠加，导致部分圆弧的预览位置与实际测量位置不一致。
- 修复：新增 `HomMat2D.RotationAngle`；起止角输入框始终表示基准角度，预览时按当前姿态的旋转量显示，与运行时一致。
- 验证：全量 build 0 警告 0 错误；`dotnet test` 250 项通过；WPF 编辑器启动正常。

### 2026-10-01 工具资源预热与释放（TR-13 延伸）

背景：对比 LDWelding 直接持有 `HShapeModel` 并用 `BinaryFormatter` 序列化的做法后决定：保留“字节数据为唯一持久化内容 + 运行期按需缓存”，把“提前加载”和“确定性释放”做成显式接口，而不是放进反序列化过程。

- `VisionFlow.Base\Core\IToolResourceLifecycle.cs`（新增）：`Prepare()` / `ReleaseResources()`，可重复调用，与 Run 线程安全。
- `VisionFlow.Base\Runtime\FlowResources.cs`（新增）：遍历整棵流程树（含分支、循环体）执行预热/释放；预热逐节点收集失败（`FlowPrepareResult`），不中断其他节点。
- 匹配工具基类、坐标转换实现该接口；模板未创建时预热直接跳过，交给运行时报告。
- 编辑器接入：打开流程后后台预热（先取节点快照；完成后若文档已切换、窗口已关闭，或工具在预热期间被删除/替换，就补做释放）；新建/打开时释放旧流程；`FlowEditModel.RemoveNode` 释放被删子树；`ToolEditTransaction` 改为 `IDisposable`，提交时释放被替换的原工具、Dispose 时释放编辑副本，确认后预热新配置；主窗口关闭时释放全部。
- 测试：新增 `FlowResourcesTests`（5 项）。全量 build 0 警告 0 错误；`dotnet test` 255 项通过；WPF 编辑器启动正常。

### 2026-10-01 区域初始化测量（识别区域后的亚像素测量）

背景：对照 HALCON 示例，“粗区域 → 精确测量”有三种做法：区域定位后跟随测量（`apply_metrology_model_diamond`），现有工具已经支持；区域边界带上做亚像素边缘拟合（`find_pads`、`circles`），缺 `boundary`、拟合矩形等工具，暂不做；区域形状初始化测量（`smallest_rectangle2`/`smallest_circle` → metrology），按使用方选择本次实现。

- `RectangleFollowMeasureTool`、`CircleFollowMeasureTool` 新增可选输入 `InitRegionPath`（`IRegionSeededMeasureTool`）：对其中每个区域对象取 `smallest_rectangle2` / `smallest_circle` 作为初始几何，做亚像素测量；结果序号为区域对象序号；与变换矩阵同时配置时明确失败；区域为空按 `FailWhenNotFound` 处理。
- `FollowMeasureToolBase`：矩阵模式与区域模式共用一套循环；单个对象抛出的 HALCON 异常（如区域过小导致有效测量不足 #8573）只记为该项失败，不再中断整个节点（矩阵模式同样受益）。
- 跟随测量编辑器：矩形/圆形测量新增“初始区域”下拉框。
- 示例：新增 `examples\region-measure.vflow.json`（razors1.png 上 12 片刀片测出、1 片边缘截断失败），接入示例加载与真实图像基线回归。
- 测试：新增 `RegionSeededMeasureTests`（5 项）与示例基线 2 项；全量 build 0 警告 0 错误；`dotnet test` 262 项通过。

### 2026-10-01 分区检测与手动 Region 阵列（复现 HALCON check_blister）

背景：评估 `check_blister.hdev`。对齐（区域定位 + 图像仿射）、分割（局部阈值 + 形态学 + 筛选）都能用现有工具搭出来；缺的是“逐格判定”：手动 Region 输出的是多对象区域，不是数组，ForEach 无法遍历；IfElse 只有单个比较，也没有计数。另外手画 15 个检测格既费时又难对齐。

- `VisionFlow.Tools\Tools\ZoneInspectTools.cs`（新增）：`ZoneInspectTool`（工具箱“03 区域处理 / 分区检测”，ID `zone-inspect`）。检测格与目标求交集，按缺失面积、面积上下限、灰度上下限判定 OK / 错误 / 缺失；输出 `Zones`（含序号、面积、中心、灰度、状态、原因）、`Areas`、各状态数量、`AllOk`、`TargetRegion`、`WrongRegion`、`MissingRegion`。状态用字符串，便于 IfElse 与常量比较。
- 手动 Region：
  - 新增输出模式 `PerShape`：每个包含 ROI 单独输出，各自扣除排除区域，顺序与绘制顺序一致。
  - 新增 `ManualRegionArray` / `ManualRegionShape.Offset`。
  - 编辑器新增“阵列生成”（行数、列数、行距、列距），以选中 ROI 为第 1 行第 1 列，先行后列生成并替换选中 ROI。
- 接入：`BuiltinToolIdentities`、`ToolboxRegistry`、`WpfToolEditorRouter`（视觉预览窗口）、工具箱图标 `ToolIcon.zone-inspect`。
- 示例：新增 `examples\blister-check.vflow.json`。示教基准按示例方法由 `blister_reference` 计算，检测格 5×3 阵列与示例坐标一致。在 `blister_01~06` 上的 OK/错误/缺失数量与 `check_blister.hdev` 的逐算子移植逐张一致：(15,0,0)、(13,2,0)、(13,2,0)、(13,1,1)、(14,0,1)、(14,1,0)。
- 测试：新增 `ZoneInspectTests`（5 项，其中示例回归依赖 `HALCONIMAGES` 下的 blister 图像，图像缺失时不执行）。全量 build 0 警告 0 错误；`dotnet test` 268 项通过。

### 2026-10-01 手动 Region 跟随位姿

背景：检测格只能固定在示教坐标，要按工件位姿检测就必须先对整张图做仿射变换，大幅面相机上开销明显，结果还要反算回原图坐标。

- `ManualRegionTool` 新增可选输入 `MatrixPath`（变换矩阵）：ROI 按示教基准保存，运行时用 `affine_trans_region` 逐对象变换，保持 PerShape 的格子顺序。矩阵复用跟随测量的预检规则：解析失败明确报错，仅显式允许的预览可降级。矩阵集合明确拒绝，多个工件请在循环中引用 `Loop.Current.HomMat`。
- `ManualRegionShape.Transform`：编辑器示教换算用。轴对齐矩形在旋转或缩放时转为旋转矩形，平移时保持轴对齐；逆变换可还原。
- 手动 Region 编辑器：
  - 新增“运行跟随矩阵”“示教模式（基准图像 / 当前图像）”“当前姿态”。
  - 当前图像模式下 ROI 按当前姿态显示；切换模式或姿态时先换算回基准再换算到新姿态；确定时一律换算回基准保存。
  - 阵列生成在当前显示坐标下进行。
- 示例：`blister-check.vflow.json` 改为检测格跟随版本（去掉整图仿射变换），6 张图的结果与 HALCON 原示例逐张一致。
- 测试：新增 `ManualRegionFollowTests`（3 项）；全量 build 0 警告 0 错误；`dotnet test` 271 项通过。

### 2026-10-01 色彩空间补全（复现 HALCON color_segmentation_pizza）

背景：评估 `color_segmentation_pizza.hdev`。除 `trans_from_rgb(..., 'cielab')` 外，其余步骤（通道分解、阈值、取最大区域、凸包、限定域、面积筛选、圆形闭/开运算）现有工具都能实现。

- `ColorTransformSpace` 在原有 hsv / hls / yuv / i1i2i3 之后追加 yiq、argyb、ciexyz、ihs、hsi、cielab、cieluv、cielchab、cielchuv、lms，均已在 HALCON 20.11 上逐一实测可用。只在末尾追加，已保存流程中的取值不变；通用编辑器自动列出新选项。
- 示例：新增 `examples\pizza-salami.vflow.json`，在 `pizza_01~03` 上与原示例逐像素一致。
- 测试：新增 `PizzaColorSegmentationExampleTests`（依赖 `HALCONIMAGES`，图像缺失时不执行）。另外修正 `ManualRegionFollowTests` 的测试顺序依赖：HALCON 按已知最大图像尺寸裁剪新建区域，测试中改为先建立图像尺寸。全量 build 0 警告 0 错误；`dotnet test` 273 项通过（连续 3 次）。

### 2026-10-01 描述子匹配与数值区间分类（复现 HALCON locate_cookie_box_multiple_models）

背景：原示例使用标定描述子匹配（`create/find_calib_descriptor_model`），依赖相机参数并输出三维位姿，按 `Pose[5]` 判断正放/侧放/倒放。项目没有描述子匹配，也没有相机参数与三维位姿类型。使用方选择方案 A：非标定描述子匹配加角度区间分类，不引入相机标定体系。

探测结论（独立程序，HALCON 20.11，Release）：
- 非标定描述子在 `cookie_box_11~21` 上找到的面与原示例完全相同，按平面内转角的判定也一致（符号与 `Pose[5]` 相反，但判定区间对称）。
- `find_uncalib_descriptor_model` 返回的投影矩阵以模板定义域重心为原点（在参考图上搜索自身，得到平移 (315, 336)，即 ROI 中心）。
- 按示例参数（depth=11、ferns=30），单个模型训练 4~10 s，序列化后 37 MB，反序列化 41 ms；4 个模型约 150 MB，不宜内嵌在流程文件中。
- 模板若直接裁成 ROI 大小，边缘特征响应与 `reduce_domain` 不同，`cookie_box_21` 会少找到 1 个面；改为“带边距裁剪 + 训练时限定定义域”后 11 张全部一致。
- `serialize_image` 的字节在不同进程间不完全相同，磁盘缓存键改按像素内容计算。

改动：
- `HalconMatchToolBase`（新增公共基类）：从原模板匹配基类中抽出图像加载、输出、未找到策略、模型句柄缓存（按缓存键）与预热/释放；`HalconTemplateMatchToolBase` 改为继承它，行为不变。所有匹配工具新增最佳结果单值输出 `Row` / `Column` / `Angle` / `Score`（未找到时为 NaN）；`MatchResultItem` 新增 `ProjectiveHomMat`。
- `DescriptorMatchTool`（工具箱“01 定位匹配 / 描述子匹配”，ID `descriptor-match`）：
  - 流程只保存带边距的模板裁剪图、ROI、裁剪偏移与训练参数。
  - 模型在预热或首次运行时训练，并写入本机缓存 `%LocalAppData%\VisionFlow\DescriptorModelCache`（可关闭）。
  - 输出投影四边形轮廓、参考图到当前图的仿射近似 `HomMat`、平面内转角与完整投影矩阵。
- `RangeClassifyTool`（工具箱“07 结果判定 / 数值区间分类”，ID `range-classify`）：按“标签=下限~上限”规则分类（开区间、按顺序匹配），支持弧度/角度归一到 [0, 360)，有默认标签与无效值标签；规则格式错误时明确报错。
- `WpfDescriptorMatchToolEditWindow`（新增）：框选模板、后台训练（界面不阻塞，训练中禁止关闭）、训练/查找参数、在当前图像上测试匹配并叠加投影轮廓。`WpfRoiEditorControl` 新增 `CurrentImage`。工具箱图标两个。
- 示例：新增 `examples\cookie-box.vflow.json`（HALCON 图像有版权，不内嵌模板；ROI 预填，在编辑器中打开参考图后创建模型即可）。
- 测试：新增 `DescriptorMatchTests`（区间分类 11 项、合成纹理上的描述子定位与模型重载 2 项、饼干盒示例回归 1 项）。示例回归从 `HALCONIMAGES` 现场示教，首次运行约 30 s（训练 4 个模型并写入约 165 MB 本机缓存），之后约 4 s。全量 build 0 警告 0 错误；`dotnet test` 288 项通过。

### 2026-10-01 区域排序（行列编号）与测量对齐数组（晶圆 chip 行列定位）

背景：需求是定位晶圆上约 500 颗 chip 的行列号、中心与角度，流程为“灰度分割 → 按区域做矩形测量取亚像素坐标”，需要在测量前按行列排序。

探测结论（合成晶圆图像，2400×2400，498 颗 chip，带抗锯齿边缘与噪声）：
- 模板匹配“数量”填 0 即返回全部目标，500 颗约 20 ms；结果按分数排序，不按位置排序。
- HALCON `sort_region` 只按单一坐标排序：同一行内各 chip 的行坐标有几个像素的抖动，按行排序后列顺序被打乱；它也不给出行列号，网格旋转后无法分行。
- 矩形测量（初始区域模式）的结果按区域对象序号输出，但只有 `Results` 列表，没有可直接对齐的数组。
- chip 内部的方向标记会让灰度分割的重心偏离几何中心（实测平均 0.45 px）；按外边缘做矩形测量平均误差 0.06 px。

改动：
- `RegionSortTool`（工具箱“03 区域处理 / 区域排序(行列编号)”，ID `region-sort`）：
  - 把区域中心旋转到网格坐标系后，分别沿行、列方向做一维聚类，相邻中心之差超过容差即分到新的行或列。容差为 0 时自动取目标高度、宽度中位数的一半。
  - 行列号是全局编号，边缘缺料或漏检不会让后面的编号前移。
  - 网格角度可选 `None`、`Auto`（各区域 smallest_rectangle2 方向角折算到 [-45°, 45°) 后取中位数）或 `Fixed`。
  - 支持行优先或列优先输出，输出 Region 的对象顺序即排序结果。
  - 其他输出：`RowIndices`、`ColumnIndices`、`Rows`、`Columns`、`Items`、`RowCount`、`ColumnCount`、`DuplicateCount`（同一行列位置出现多个区域）、`GridAngle`。
  - 空区域对象被忽略并记录警告；输入为空时按 `FailWhenNotFound` 处理。
  - 使用视觉预览编辑器，并新增工具箱图标。
- 矩形测量新增 `Rows` / `Columns` / `Phis` 数组，圆形测量新增 `Rows` / `Columns` / `Radii` 数组：与测量项（初始区域对象或定位矩阵）逐一对齐，失败项为 NaN。测量基类新增 `SeedCount`、`SeedIndex`、`NewSeedArray()` 支持对齐写入。原有单值、`Results`、`Count` 输出不变。
- 示例：新增 `examples\wafer-chips.vflow.json` 与合成图像 `examples\images\wafer-chips.png`（无噪声版，约 95 KB）。
- 测试：新增 `RegionSortTests`，共 9 项：
  - 行列号与真值一致，缺料不影响编号。
  - 3° 旋转时 `Auto` 编号正确，`None` 编号错误。
  - 列优先输出、重复报告、空输入策略。
  - 矩形和圆形测量的数组对齐。
  - 示例流程：498 颗全部编号正确且 0 失败，测量中心误差最大 0.151 px、平均 0.060 px，角度最大误差 0.25°，单次约 50 ms。
  全量 build 0 警告 0 错误；`dotnet test` 298 项通过。

### 2026-10-01 角度换算（弧度/角度互转与范围折算）

背景：匹配、测量工具输出的角度为弧度，范围随算子而定。例如矩形测量的方向角，长边方向相差 180° 是同一个姿态。上位机通常需要度，并且需要固定的范围（-90°~90° 或 0°~180°）。

改动：
- `AngleConvertTool`（工具箱“05 几何测量 / 角度换算(弧度/角度)”，ID `angle-convert`）：
  - 输入单位、输出单位分别可选弧度或度。
  - 范围可选不限制、[-180°, 180°)、[0°, 360°)、[-90°, 90°)、[0°, 180°)、[-45°, 45°)，或自定义 [RangeMin, RangeMax)（单位为度，宽度须在 (0, 360] 内）。
  - 范围按周期折算而不是截断，区间宽度即周期（如 [-90°, 90°) 把 100° 折算为 -80°），这样不会把有效角度改成边界值。
  - 输入可以是单值或数组（`AcceptsCollection`，可直接接 `Phis`）；NaN 原样输出。
  - 输出：`Value`（单值，或数组的第一项；空数组时为 NaN）、`Values`、`Count`。
  - 使用通用编辑器，并新增工具箱图标。
- 新增公共计算类 `AngleMath`（`ToDegrees`、`ToRadians`、`Fold`、`TryGetBounds`）。数值区间分类的角度归一改为调用 `AngleMath.Fold`，行为不变。
- 示例 `wafer-chips.vflow.json` 在矩形测量后接角度换算（度，[-90°, 90°)），流程输出 `Angles` 改为度。
- 测试：新增 `AngleConvertTests`，共 24 项：折算边界与负数、NaN、单值与数组换算、空数组、自定义范围校验、保存加载与数组引用校验。晶圆示例回归改为校验度数与范围。全量 build 0 警告 0 错误；`dotnet test` 322 项通过。
