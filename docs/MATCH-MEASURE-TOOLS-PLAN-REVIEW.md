# MATCH-MEASURE-TOOLS-PLAN 开工评审意见

评审日期：2026-10-07
评审对象：`docs\MATCH-MEASURE-TOOLS-PLAN.md`（MT-01 ~ MT-06、MS-01 ~ MS-07，共 13 项）
评审方式：将计划与当前代码核对（`HalconTools.cs` 匹配基类、`MeasureTools.cs` 测量基类、`FollowMeasureTools.cs` 六个跟随测量工具、`DescriptorMatchTools.cs`），确认第一批（MT-01 + MS-01）的假设成立。
评审结论：**可以开工，建议按文档第 8 节分批实施，第一批为 MT-01（匹配基类搜索区域与排序）+ MS-01（metrology 高级参数与输出）**。两批之后的 MT-03 / MS-07 分别与 MT-02 / CB-01 有耦合，留待后续批次评审。

## 1. 总体评价

- 拆分合理：MT-01 / MS-01 都落在基类上，一批改动覆盖全部 5 个匹配工具和 4 个 metrology 测量工具，收益面最大，符合"先基类后工具"的次序。
- 计划的兼容性原则（新参数默认值 = HALCON 默认值 = 旧行为）明确且可测。
- 13 项之间依赖关系清楚（MT-02 示教界面与 MT-03 共用、MS-06 依赖 MS-01 的中间基类、MS-07 依赖 CB-01 标定服务），第 8 节的顺序可行。

## 2. 代码核对结果（第一批假设均成立）

| 计划假设 | 核对结果 |
|---|---|
| `HalconMatchToolBase.Run` 是 TryLoadImage → FindMatches → SetMatchOutputs 的结构，可在中间插入搜索区域 | ✅ `HalconTools.cs:165-223` 与计划描述一致；`FindMatches(ctx, image, model, items)` 接收图像参数，传入 `reduce_domain` 缩小图无需改子类签名 |
| 描述子匹配、模板匹配族都继承该基类，MT-01 自动生效 | ✅ 模板/灰度/缩放/变形经 `HalconTemplateMatchToolBase`，`DescriptorMatchTool` 直接继承 `HalconMatchToolBase`（`DescriptorMatchTools.cs:44`） |
| `AddMetrologyParams` / `ApplyMetrology` 是 4 个 metrology 工具共用的参数与执行入口，中间基类切分点正确 | ✅ `MeasureTools.cs:335-348`：当前只传 `measure_transition` / `measure_select`，`ApplyMetrology` 只取实例 0；4 个工具（直线/矩形/圆/椭圆）都走这两个方法；一维卡尺两个工具走独立的 `measure_pos` 路径，不受 MS-01 影响 |
| 角度、配对、可见性等基础设施可直接复用 | ✅ `AngleMath` / `XldLineHelper.Crosses`（十字 XLD）/ `IToolParameterVisibility` / `IToolConfigurationCheck` 均已就绪 |

## 3. P1：第一批动工前必须处理

### P1-1 排序会击穿 `SetMatchOutputs` 的 best 取值（本批唯一的行为敏感点）

`SetMatchOutputs` 现在取 `best = items[0]`（`HalconTools.cs:352`），之所以正确，只是因为 HALCON 按分数降序返回、items[0] 恰好是最高分。MT-01 对 items 重排后 items[0] 不再是最高分，**必须改为显式按 `Score` 取最高**（如 `OrderByDescending`），否则 `BestMatch` / `Row` / `Column` / `Angle` / `Score` 单值会随排序方式漂移，既违反计划"固定取最高"的约定，也改变默认行为（`SortBy = Score` 时虽顺序不变，但实现不能依赖这个巧合）。计划写了目标但没点出这行代码；同时"重写每项 Index"和"数组随新顺序输出"要用用例固定下来（`Scores` / `Items` / `HomMats` / `Contours` 的顺序变化是有意行为）。

### P1-2 第 6 节"枚举按名称序列化"的表述需先修正

与 REGION / XLD 两批相同的问题：枚举**按数字保存**，新值追加在末尾。本批新枚举（`SortBy`、`MeasureInterpolation` 等）没有对应的旧字符串参数，不需要强制按名保存，但文档表述必须先改正，避免后续批次照抄出错。

### P1-3 搜索区域缩小图的生命周期（VF-04）

`reduce_domain` 产出的缩小图归本次运行所有，`FindMatches` 结束后立即释放；`Image` 输出仍写原输入图（计划已写）。实现时注意：多定位矩阵 / 循环场景只缩小一次，不要在循环里重复 `reduce_domain`。

### P1-4 编辑窗口改动纳入同步清单

本批无新工具箱入口，但有两处编辑器改动：`WpfMatchToolEditWindow` 加"搜索区域"引用 + "排序方式"（含 RowTolerance），跟随测量编辑窗口加"高级参数"折叠区（默认收起）。计划第 6 节的同步清单没有列编辑器，需补上。匹配示教窗口经 `WindowsFormsHost` 承载 ROI 控件，新增 WPF 控件注意 DPI 与现有布局风格。

## 4. P2：开发中必须落实

1. **多实例展开与现有种子机制对齐**：MS-01 的"定位结果 → 实例"展开顺序和 `InstanceSeedIndices`，应复用测量基类现有的 `SeedCount` / `SeedIndex` / `NewSeedArray()` 对齐机制（wafer 批引入），不要另起一套。
2. **`num_measures` 与 `measure_distance` 二选一传参**（计划已写）：`NumMeasures > 0` 传 `num_measures`，否则传 `measure_distance`，切勿同时传。
3. **默认值必须用 `get_param_info` 实测**（XLD 批的教训）：本机 HALCON 是 22.11 而非文档基线 20.11，"默认值 = HALCON 默认"的每个数值都要实测并在计划里注明版本，部署 20.11 前复核。
4. **`MeasurePoints` 十字 XLD** 直接用 `XldLineHelper.Crosses`，不要新造。
5. **回归用例**：默认参数（新参数全默认）下 MT-01 / MS-01 与旧版结果逐项一致（验收已有此条，务必保留为硬门禁）；排序用例覆盖 5 种 `SortBy` × `RowTolerance` 分行。
6. **搜索区域语义提示**：界面文字说明"限制的是模型参考点（示教区域重心），不是整个模板"（计划已写，属用户可见文案，别省略）。

## 5. P3：文档维护

1. 修正第 6 节枚举序列化表述；第一批完成后更新头部状态（建议按批次标注"MT-01 / MS-01 已实现"）。
2. 后续批次（MT-02/03、MS-02 ~ MS-07）动工前各做一次小评审，重点：MT-03 的 `ModelsData` 持久化格式与缓存键、MT-06 差分检测的模型体积与外部文件约定、MS-07 与 CB-01 的标定服务接口。
3. 本批不做 MS-07（依赖 CB-01，标定计划未开工）。

## 6. 开工顺序确认

第一批：MT-01 + MS-01（本评审覆盖）。第二批候选：MS-02（边缘对宽度，只动卡尺两工具，改动小）或 MT-02（示教建模，与 MT-03 强耦合建议合并评审）。具体由使用方按现场需求定。

---

# 第二批评审意见（MS-02 + MS-07 + 第一批回归修复）

评审日期：2026-10-07
评审对象：计划 MS-02（卡尺边缘对宽度）、MS-07（单位换算），以及第一批代码评审发现的回归项。
评审方式：与第一批实现后的代码核对（`MeasureTools.cs` 测量基类、`FollowMeasureTools.cs` 两个卡尺工具、`AngleTools.cs`、`WpfMatchToolEditWindow.xaml.cs`）。
评审结论：**可以开工，第二批 = MS-02 + MS-07（Fixed 当量模式）+ 匹配窗口"执行测试"回归修复。MS-07 的 `Calibration` 标定来源本批不做（CB-01 未开工），MT-02 + MT-03 合并为第三批。**

## B2-0. 第一批回归修复（最高优先，先做）

**问题**：`WpfMatchToolEditWindow.TestRun_Click`（第一批改动）第一步调用 `ApplyToTool()`，把全部参数直接写入 `_tool`——路由层（`WpfToolEditorRouter`）传入的是**活节点不是副本**，用户点"执行测试"后点"取消"，参数已被改写，违背"编辑只在 OK 时到达节点"的约定（LD-08 起确立）。第一批前旧代码中 `_tool.*` 只出现在 OK 的处理里，测试按钮只解析局部值。
**修法**（二选一，建议方案 A）：
- A. 窗口打开时克隆一个工作副本（反序列化节点 JSON 即可），测试按钮与 OK 都作用于副本；OK 时把副本参数写回节点。
- B. 测试按钮解析文本框值到临时变量并赋值给一个一次性克隆，绝不触碰 `_tool`。
**门禁**：新增 UI 无关测试固定该行为——执行测试路径不得修改原节点实例的任何参数（可对 `TestRun_Click` 抽出的核心方法测）；并人工走查：改参数 → 执行测试 → 取消 → 重新打开窗口确认值未被改写。

## B2-1. P1：动工前确认

1. **MS-07 的 `Calibration` 来源本批砍掉**：`vfcal.json` 的加载与缓存是 CB-01 标定服务的内容，标定计划未开工，代码里不存在该服务。本批只做 `ScaleSource = Fixed`（直接填像素当量）；`Calibration` 枚举值可以保留但流程校验时报"标定服务尚未实现（依赖 CB-01）"的明确错误，或干脆本批不引入该枚举值，待 CB-01 评审后连同标定批一起加。**倾向后者：本批 `ScaleSource` 只有 `Fixed` 一个取值，不预留无效选项。**
2. **MS-02 的 Pair 模式参数取值集合变化必须体现在校验与编辑器**：`MeasureTransition` 在 Pair 下新增 `all_strongest` / `positive_strongest` / `negative_strongest` 三个值，`WpfFollowMeasureToolEditWindow` 的 Transition/Select 下拉是 `AddItems` 硬编码的，Edge / Pair 切换时下拉项要联动；`IToolConfigurationCheck` 校验按模式收紧。
3. **Pair 模式复用现有边缘输出的语义要写进文档**：`Rows` / `Columns` 在 Pair 模式下依次写每对的两条边缘，数组长度 = 2 × PairCount，旧下游引用仍能取到点但"第 N 条边缘"的序号含义变化——README 工具表和计划实现说明里必须写明。

## B2-2. P2：开发中落实

1. **卡尺两工具共用方法放对位置**：`measure_pairs` 的调用与结果转换放 `FollowMeasureToolBase` 的受保护方法（与 `measure_pos` 路径并列），两个卡尺工具只传各自的测量对象句柄；圆弧卡尺同样走 `measure_pairs`（HALCON 对圆弧量测句柄同样适用），用合成图实测确认。
2. **Pair 专属输出在 Edge 模式写空**：`Widths` / `Gaps` / `PairCenterRows` / `PairCenterColumns` / `FirstWidth` / `PairCount` / `PairResults` 在 Edge 模式写空数组与 NaN（与 MS-01 的空输出模式一致，防止循环中读到上一轮残留）；并用 `IToolParameterVisibility` 在 Edge 模式隐藏 Pair 专属输出与参数。
3. **宽度标注显示轮廓**：边缘对的显示轮廓用连线 + 宽度文字区分（计划已写），复用现有显示轮廓构建代码，XLD 文字用 `XldLineHelper` 或现有标注助手，不要新造。
4. **MS-07 的换算语义**：`Length` 模式下 `IsArea = true` 时结果 = 像素值 × PixelSize²；`LengthUnit = um` 时毫米结果 × 1000；`Value` / `Values` / `Count` 输出与 Angle 模式完全同构；`ConvertKind = Angle` 时行为与现版本逐项一致（回归硬门禁）。显示名改"单位换算"只动 `ToolboxRegistry` 的显示名，工具 ID `angle-convert` 与类型名不动。
5. **枚举序列化**：`EdgeMode` / `ConvertKind` / `LengthUnit`（及本批若引入的 `ScaleSource`）按数字保存、追加在末尾、默认值 = 现有行为；新枚举没有旧字符串参数，不需 `JsonStringEnumConverter`。
6. **编辑器同步**：MS-02 改 `WpfFollowMeasureToolEditWindow`（EdgeMode 切换 + 下拉联动）；MS-07 无专用编辑窗口（经侧栏属性配置），只需 `ToolboxRegistry` 显示名与 README；两工具都不涉及 `BuiltinToolIdentities` / `WpfToolEditorRouter` 变更。

## B2-3. P3：文档维护

1. 计划头部状态更新为"第二批 MS-02 / MS-07（Fixed）已实现"；MS-02、MS-07 条目补实现说明；MS-07 条目注明 `Calibration` 来源推迟到 CB-01 之后的标定批。
2. README 工具表：几何测量行补"一维卡尺/圆弧卡尺支持边缘对宽度测量（宽度、间距、中心点）"；单位换算行改名并补"支持像素长度/面积换算（固定当量），标定当量待标定功能上线"。
3. 第一批计划第 10 节"执行测试"描述改为如实表述（当前实现直写节点，修复后按实际写法更新）。

## B2-4. 开工顺序确认

第二批：回归修复 + MS-02 + MS-07（Fixed）。第三批候选：MT-02 + MT-03（示教界面一次改完，合并评审）。MS-07 的 Calibration 来源、MS-03 / MS-04（沿圆弧、模糊测量）留待标定批或第四批评审。

---

# 第三批评审意见（MT-02 + MT-03，合并）

评审日期：2026-10-07
评审对象：计划 MT-02（XLD / DXF 建模与模型原点）、MT-03（通用形状匹配新工具）。
评审方式：与第二批实现后的代码核对（`HalconTools.cs` 匹配基类与模型缓存、`MatchResultItem`、`WpfMatchToolEditWindow` 示教区、第二批引入的输出可见性守卫测试）。
评审结论：**可以开工，第三批 = MT-02 + MT-03（新工具 `HalconGenericShapeMatchTool`）。本批是 MATCH-MEASURE 线中风险最高的一批：示教界面大改 + 新工具 + 多模板持久化格式，必须带上列出的 P1 门禁。**

## B3-1. P1：动工前确认

1. **`ModelNames` 命名冲突（硬性，第二批守卫测试会红灯）**：计划同时定义"模板名另存 `ModelNames` CSV 字符串参数"和"输出 `ModelNames`（数组）"。第二批已加守卫测试"任何工具的输出名都不与其参数名相同"（`MatchMeasureBatch2Tests`），本批若照计划命名必然失败。**参数改名**，如 `TemplateNamesCsv`（流程文件可读的 CSV 字符串属性），输出保留 `ModelNames`。
2. **通用形状模型的原点设置方式需 HALCON 实测**：`set_shape_model_origin` 只适用于经典形状模型，通用形状模型（20.11 `create_generic_shape_model`）的原点是否支持、用哪个参数名（`set_generic_shape_model_param`），必须先探测。不支持则 MT-02 的原点功能对通用形状匹配降级（界面禁用并说明），或在结果坐标上加偏移实现但要在计划里记录差异；**不得**假设 API 存在。
3. **多模板"按模板分别设置查找参数（CSV 字符串）"建议砍掉**：全局查找参数对所有模板生效已覆盖绝大多数场景；CSV 按模板覆盖引入解析、校验、界面三重复杂度，收益不明。**本批只实现全局查找参数**；如现场确实需要，单独立项。计划条目需同步改。
4. **`ModelsData` 持久化格式**：版本号 + 模板数 + 逐模板（名称长度、UTF-8 名称、模型长度、模型字节）。用 `serialize_shape_model` 序列化通用形状模型句柄**必须先实测确认可行**（计划已写"开发时实测确认"），不可行则改用逐个 `write_dict`/`serialize` 替代方案并记录。加载时版本不符给出明确错误，不得静默误读。
5. **模型缓存键**：现有模板匹配基类的 `CurrentModelKey` 用 `ShapeModelData` 数组**引用**（`HalconTools.cs:608-612`）。MT-03 若按计划在原数组上原地修改，`ContentHash` 必须覆盖名称与全部模板字节；建议直接沿用"示教/导入替换整个数组"的现有模式（新数组 → 新引用 → 触发重载），比引入哈希更简单且与既有行为一致。若坚持哈希，须用例固定：任一模板增删改名后缓存键变化。
6. **XLD / DXF 建模的输入约束**：`create_shape_model_xld` 要求单一轮廓对象——上游 XLD 输出若为多条轮廓（`edges_sub_pix` 常见），示教界面必须提供轮廓选择（按序号）或明确报错；`read_contour_xld_dxf` 读入多条时同理。DXF 文件不存在 / 解析失败在示教时明确报错，运行时**不得**依赖 DXF 文件（计划已写）。

## B3-2. P2：开发中落实

1. **MT-02 的度量锁定与校验**：XLD 建模时 `Metric` 强制 `ignore_local_polarity`、对比度参数不生效——界面锁定并说明原因（计划已写），同时 `CheckConfiguration` 对"XLD 来源 + 非 ignore_local_polarity"的历史配置给出流程校验错误，不允许带病运行。
2. **模型原点的基准提示**：修改原点后界面提示重新确定跟随基准（计划已写）；`BaseRow` / `BaseColumn` / `BaseAngle` 不回填原点偏移，两者语义保持独立。
3. **通用形状模型结果矩阵**：跟随矩阵与模型轮廓变换必须用 `get_generic_shape_model_result` 的完整 2D 变换矩阵，**禁止**用 `Scale` 重建（各向异性缩放时错误，计划已写——用例固定：各向异性缩放下 `HomMats` 与直接算子调用逐项一致）。
4. **句柄生命周期**：`find_generic_shape_model` 的结果句柄必须 `clear_handle`（含异常路径）；多模板句柄逐个释放；`ClearModel` / 编辑事务 Dispose 路径不得泄漏（第一批 `ToolTestRun` 的 `FlowResources.Release` 机制沿用）。
5. **示教界面布局**：示教页要加模型来源（图像 ROI / XLD / DXF）、原点编辑、模板列表（增删改排序）、杂乱区域绘制，现有窗口已较拥挤——新增控件按现有页签/分区风格组织，模板列表与杂乱绘制放示教页的独立分组，注意 `WindowsFormsHost` 的 DPI 与遮挡问题（现有 ROI 控件是 WinForms 承载）。
6. **新工具登记清单**：`BuiltinToolIdentities`、`ToolboxRegistry`、`WpfToolEditorRouter`（在缩放形状匹配之前路由，注意既有"椭圆测量先于通用跟随窗口"的排序注释模式）、README 工具表、计划头部状态。
7. **枚举**：`ScaleMode`（`None` / `Isotropic` / `Anisotropic`）按数字保存、追加、默认 `None`；`BorderShapeModels` / `UseClutter` 用 bool；`TimeoutMs` 为 int 毫秒，写入算子前确认单位（`timeout` 参数实测单位并注释）。
8. **回归门禁**：现有 4 个匹配工具图像 ROI 示教行为逐项不变（含模型字节、`CurrentModelKey` 引用语义）；`HalconMatchToolBase` 的搜索区域与排序（第一批）在通用形状匹配上同样生效（继承自动获得，但要有一个用例固定）。

## B3-3. P3：文档维护

1. 计划头部状态、MT-02 / MT-03 实现说明（含 P1-2 / P1-3 的实际结论）、第 6 节同步清单若新增项一并更新。
2. README 工具表"定位匹配"行补通用形状匹配与 XLD / DXF 建模、模型原点。
3. HALCON 探测结论（原点参数、`serialize_shape_model` 对通用句柄、`timeout` 单位）记入计划新一节，注明 22.11 环境与 20.11 复核要求（沿用第 9 节模式）。

## B3-4. 开工顺序确认

第三批：MT-02 + MT-03。第四批候选：MS-06（找角）+ MS-05（灰度投影）；之后 MT-06（差分检测）；MT-05 / MT-04 / MS-03 / MS-04 与标定批（CB-01~07 + MS-07 Calibration）按现场需求排。
