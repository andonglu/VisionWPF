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
