# REGION-TOOLS-PLAN 开工评审意见

评审日期：2026-10-07
评审对象：`docs\REGION-TOOLS-PLAN.md`（RG-01 ~ RG-07）
评审方式：将计划逐条与当前代码核对（`VisionFlow.Tools\Tools\RegionTools.cs`、`ArrayProcessTools.cs`、`XldTools.cs`、`ToolboxRegistry.cs`、`BuiltinToolIdentities.cs`、`WpfToolEditorRouter.cs`、`DisplayOverlayBuilder.cs`、参数面板构建器），确认假设成立、找出计划未覆盖的缺口。
评审结论：**可以开工**。计划质量高、范围合理，拆分原则与兼容性要求明确；以下 P1 问题建议在动工前与使用方确认方案，P2 问题在开发中落实，不必阻塞开工。

## 1. 总体评价

- 范围合理：4 个现有工具增强 + 3 个新工具，工具箱净增 3 个入口，延续 TR-11 收拢入口的做法。
- 拆分原则正确：并入/新建的判断标准（输入输出一致则并入选项、语义不同则新建）与 ThresholdTool、MorphologyTool 的已有先例一致。
- 兼容性要求可操作：枚举按名称序列化且追加在末尾、新增可选输入在历史流程中为空，均与 `FlowSerializer` 现行行为吻合。
- 验收标准基本可测，个别条目需补充（见 P2-8、P3-10）。

## 2. 代码核对结果（计划假设均成立）

| 计划假设 | 核对结果 |
|---|---|
| ThresholdTool 已是合并工具，枚举末尾追加 `DynThreshold` 无兼容问题 | ✅ `ThresholdSegmentMethod` 为 6 值枚举，末尾追加即可；`UsedThreshold` 输出已存在 |
| RegionProcessTool / SelectRegionTool / RegionMinMaxGrayTool 结构与计划描述一致 | ✅ 三个类的参数、输出、`INotFoundPolicy` 均如计划所述 |
| RG-07 配对规则与现有规则一致 | ✅ `ArrayProcessTool.ElementWise` 现行实现即"等长一一对应、一侧为 1 则一对多、否则报错"（`ArrayProcessTools.cs:326-331`），LD 验收已固化此规则，RG-07 与之完全吻合 |
| Complement 的 `Input.Image` 默认值放在工具箱注册处 | ✅ 可行。内置工具在 `ToolboxRegistry` 注册 lambda 中设置默认值（如 ThresholdTool 的 `ImagePath = "Input.Image"`）；插件工具另有 `ApplyDefaultInputRefs` 自动填图像引用。注意内置注册路径**不会**自动填默认，须在注册 lambda 中显式写 |
| 新工具需要登记固定 ID、注册工具箱、路由编辑器 | ✅ `BuiltinToolIdentities.cs`（threshold/regionprocess/region-min-max-gray/selectregion 均在册）、`ToolboxRegistry.RegisterDefaults()`、`WpfToolEditorRouter.IsVisualPreviewTool()` 三个入口确认存在 |
| RG-05 输出 XLD 可被叠加显示 | ✅ `DisplayOverlayBuilder` 已支持 `HalconXld` 及其集合 |
| `distance_rr_min` 逐一配对且要求两侧个数相等、`distance_pr` 输出 Distance/MaxDistance | ✅ 与 HALCON 20.11 语义一致 |

## 3. P1：动工前需要明确的三个问题

### P1-1 字符串参数是自由文本，无预置选项

`LightDark`（RG-01 要扩到 4 个取值）、`BinaryMethod`、RG-02 的 `FillFeature` / `BoundaryType`、RG-03 的 `Features` / `Mins` / `Maxs` 都是 `string` 属性。参数面板与通用编辑器只对**枚举**给下拉，字符串一律给文本框（`ParameterPanelBuilder` 中字符串属性走 `AddTextRow`，仅枚举走 `AddComboRow`）。用户手敲 `not_equal` 敲错一个字母，只有跑到那一刻才被 HALCON 报错。

**建议**：`LightDark`、`FillFeature`、`BoundaryType` 改为枚举（按名称序列化，兼容无风险；旧文件中的字符串值能正常反序列化到同名枚举成员，需用一个加载用例验证）。`Features` 这类开放列表保留字符串可以接受，但 `Mins` / `Maxs` 与 `Features` 的个数一致性必须在运行时给出明确中文错误（见 P2-4）。

### P1-2 "按当前选项显示或隐藏相关参数"超出通用编辑器现有能力

计划第 6 节要求编辑窗口按选项联动显示参数，但通用/视觉预览编辑器目前**平铺全部参数**。现状已偏冗长（ThresholdTool 6 种方式的参数并集全部显示），RegionProcessTool 再加 5 种操作会更严重。

**建议二选一**：
- 参照 `WpfArrayProcessToolEditWindow` 的先例，为阈值分割、区域处理各做一个小型专用窗口（选项中文、只显示当前方式相关参数）；
- 或给通用编辑器增加"条件行"能力（工作量更大，且是全局性改动，不建议为本计划单独做）。

若使用方接受"先平铺、后优化"，需在计划中显式记录这个取舍，而不是保留无法兑现的第 6 节承诺。

### P1-3 计划漏了工具箱图标这一项

第 6 节"每项需要同步修改的位置"没有列 `Themes\ToolIcons.xaml`。现有 51 个图标由使用方提供的 `tool-{工具ID}.svg` 经 `tools\svg_to_tool_icons.py` 生成。RG-05 / RG-06 / RG-07 三个新工具没有 SVG 就会落到 `ToolIcon._default` 回退图标。

**建议**：动工时向使用方索取 3 个 SVG（命名 `tool-region-to-xld.svg`、`tool-xld-to-region.svg`、`tool-region-distance.svg`）；若暂不可得，明确接受默认图标并在计划中注明。

## 4. P2：开发中必须落实的问题

1. **RG-03 多条件个数一致性**：`Features` / `Mins` / `Maxs` 个数不一致时运行时中文报错；部分填写（如只填 `Mins` 不填 `Features`）的行为需定义——建议"任一新字段非空即按多条件模式执行，字段不完整直接报错"，不要静默退回单条件。
2. **RG-03 `ByIndex` 与筛选的次序**：写明"先按多条件/灰度筛选，再按 `TakeIndex` 取件"；`TakeIndex` 越界按 `FailWhenNotFound` 处理，与现有取件逻辑整合而非另起一套。
3. **RG-07 空区域预检**：`distance_rr_min` / `distance_pr` 遇空区域对象会抛 HALCON 异常，实现需先逐对象预检（任一配对含空对象时按 `FailWhenNotFound` 输出中文信息），验收补"配对中含空区域"用例。
4. **RG-07 点模式的行列长度校验**：行、列数组长度不一致时明确报错（信息含两个长度）；长度取点一侧的个数，规则已与计划一致，补一条对应验收。
5. **RG-04 空输入输出形态**：沿用现有模式——空数组 + `FirstMean` / `FirstDeviation` 为 NaN + `Found = false` + 按 `FailWhenNotFound` 处理；`intensity` 与 `min_max_gray` 的区域对象逐一对应关系写一个对齐用例。
6. **配对帮助类必须落地**：按第 9 节建议抽公共配对帮助类（如 `PairingHelper`），并让 `ArrayProcessTool.ElementWise` 同步改用它，消除两处同规则实现；这是 XLD 计划 XG-08 / XG-09 的前置，抽取后 XLD 计划相应条目可直接引用。
7. **RG-05 / RG-06 的编辑器路由**：新工具须加入 `WpfToolEditorRouter.IsVisualPreviewTool()`，否则落通用编辑器无图像预览；两工具都需要视觉预览（看转换结果）。
8. **RG-07 最近点对的显示**：`Rows1` / `Columns1` / `Rows2` / `Columns2` 是 double 数组，`DisplayOverlayBuilder` 目前只显示 Halcon 图像/区域/XLD 对象，不画点。要么在验收中明确"最近点对不在本期叠加显示"，要么加"双数组 → cross 标记"的叠加支持（XLD 计划 XG-09 也需要点/交点显示，可合并考虑）。

## 5. P3：文档维护

1. 计划头部"状态：待开发"随开工更新；`LOGIC-DATA-TOOLS-PLAN.md` 头部状态同样漏改（LD-01 ~ LD-08 均已实现，属上一轮遗留）。
2. 建议验收章节补两条：新枚举值的旧文件加载用例（验证字符串到同名枚举成员的兼容读取）；`SelectRegionTool` 新字段为空时输出与旧版逐字节一致的历史流程回归。
3. RG-06 说明"拆成两个工具是为让必填输入能被校验器检查"——这个理由成立，但建议同时在 RG-05 文档处写明反向引用（从 XLD 转 Region 的去处），方便使用方检索。

## 6. 开工顺序确认

维持计划第 9 节顺序：RG-01 ~ RG-04（现有工具增强）→ RG-05 / RG-06（互转）→ RG-07（区域距离 + 配对帮助类）。配对帮助类在 RG-07 完成，XG-08 / XG-09 依赖满足，REGION 线收尾后即可无缝接 XLD 计划。

每个 RG 条目完成时按第 6 节清单同步：工具元数据 → `BuiltinToolIdentities` → `ToolboxRegistry` → `IsVisualPreviewTool` 路由 → README 工具表 → 测试用例 →（新工具）图标。
