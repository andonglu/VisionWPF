# XLD-TOOLS-PLAN 开工评审意见

评审日期：2026-10-07
评审对象：`docs\XLD-TOOLS-PLAN.md`（XG-01 ~ XG-09）
评审方式：将计划逐条与当前代码核对（`VisionFlow.Tools\Tools\XldTools.cs`、`GeometryTools.cs`、`AngleTools.cs`、`PairingHelper.cs`、`RegionDistanceTools.cs`、`RegionTools.cs`、`DisplayOverlayBuilder.cs`、`WpfToolEditorRouter.cs`），确认假设成立、找出计划未覆盖的缺口与疑似算子签名错误。
评审结论：**可以开工，但第 7 节有一处必须修正的文档错误，两处算子签名需在动工前实测确认**（本项目惯例是探测结论先行）。XG-08 / XG-09 的前置（`PairingHelper`）已随 RG-07 落地，开发顺序可以照计划第 9 节执行。

## 1. 总体评价

- 拆分原则与 REGION 计划一致，5 增强 + 4 新建、净增 4 个入口的粒度合理。
- 依赖关系已闭环：配对规则直接引用已实现的 `PairingHelper`（`PairingHelper.cs`，与 RG-07、数组处理共用），XG-08 不用等 REGION。
- 兼容性默认值的约定（各模式默认等于现有行为）逐条核对成立。

## 2. 代码核对结果（计划假设均成立）

| 计划假设 | 核对结果 |
|---|---|
| `ContourCreateTool` 当前只有 `edges_color_sub_pix`，默认 `Filter="canny"` | ✅ 参数结构（Filter/Sigma/Low/High）与计划描述一致 |
| `SelectContourTool` 已支持多特征 and/or、Min/Max 与 MinValues/MaxValues | ✅ 计划 XG-02 是在此之上加 `SelectBy` 与取件，假设成立 |
| `XldSegmentMode` 末尾追加 `lines_ellipses` | ✅ 当前为 2 值枚举，追加无兼容问题 |
| `XldFeaturesTool.GetXldFeature` 以特征名 switch 分发 | ✅ 计划"新增 case + 未识别走 `get_contour_global_attrib_xld`"与现有结构完全吻合 |
| `IntersectionLinesTool` 有"直线输入解析方式"可沿用 | ✅ `TryGetLinePoints`（`GeometryTools.cs:236`）：两点轮廓取首尾、多点轮廓先 `fit_line_contour_xld`，正是 XG-05/09 需要的语义；但目前是 **private**，见 P2-1 |
| 交点输出用 cross XLD 叠加显示 | ✅ 现有实现 `GenCrossContourXld` 输出 `Xld`，XG-05 新增多交点可沿用同一做法 |
| XG-09 角度范围与"角度换算"工具约定一致 | ✅ `AngleMath.Fold` / `TryGetBounds` / `AngleRange`（`AngleTools.cs:20-74`，同程序集直接可用）。注意 MS-07"单位换算"尚未实现，约定的实际载体就是 `AngleMath` |
| XG-08 配对规则与 RG-07 一致 | ✅ `PairingHelper.TryGetPairCount` 语义完全一致；`RegionDistanceTool` 的空对象预检、行列长度校验、Segments 输出都是可直接照搬的模式 |

## 3. P1：动工前必须处理

### P1-1 第 7 节"枚举按名称序列化"是错误的（同 REGION 计划已纠正的问题）

实际枚举按**数字**保存（`"SegmentMethod": 0`）。本计划 XG-01/02/06 若把现有字符串参数（`Filter`、`Features`、`Operation`、`LightDark` 等）改成枚举，旧文件里的字符串会读不进数字枚举——**必须沿用 REGION 的做法**：枚举强制按名称序列化（`LightDark` / `BinaryMethod` 已有先例），并加"旧文件字符串值读入枚举"的加载用例。同时把第 7 节第一句更正为"枚举按数字序列化，新增值一律追加在末尾"。

### P1-2 `select_contours_xld` 的特征集疑似不含 `closed` / `open`（XG-02）

该算子的合法特征通常是 `contour_length` / `maximum_extent` / `direction` / `curvature`，计划列的 `closed` / `open` 疑似不合法（闭合判定已由 XG-04 的 `is_closed` 覆盖）。**动工前在 HALCON 20.11 上实测特征全集**，据此修正 XG-02 的 `ContourFeature` 取值清单。

### P1-3 `distance_cc_min_points` 疑似没有 Mode 参数（XG-08）

`fast_point_to_segment` / `point_to_segment` 疑似是 `distance_cc` 的参数；`distance_cc_min_points` 通常直接输出 `DistMin` + 最近点对坐标。**动工前实测 20.11 签名**：若确无 Mode，XG-08 的 `CcMode` 要么删除（固定行为），要么改用 `distance_cc` 并自行计算最近点对（多一次计算）；验收标准随之微调。

### P1-4 计划第 6 节漏了工具箱图标（同 REGION 评审 P1-3）

XG-06 / XG-07 / XG-08 / XG-09 四个新工具需要 SVG（`tool-xld-process.svg`、`tool-fit-ellipse-rect.svg`、`tool-contour-distance.svg`、`tool-geometry-relation.svg`），否则落 `ToolIcon._default`。动工时向使用方索取或明确接受默认图标。

## 4. P2：开发中必须落实

1. **直线解析方法要抽出共享**：`TryGetLinePoints` 目前是 `IntersectionLinesTool` 的 private 方法，XG-05（LineContour）和 XG-09（几何关系）都要用。抽成内部静态帮助类（如 `XldLineHelper`），`IntersectionLinesTool` 改调用。
2. **点类输出的统一做法**：XG-08 的输出清单照抄了 RG-07 但**漏了 `Segments`（最近点对连线的 XLD 输出）**——RG-07 靠它让最近点对在叠加层可见，XG-08 应保持一致。XG-05 的多交点、XG-09 的垂足同样用 cross/连线 XLD 输出，**不要**给 `DisplayOverlayBuilder` 新增画点能力（现有只画 Halcon 对象）。
3. **XG-06 `AffineTrans` 复用现有矩阵纪律**：`FollowMatrixResolver.TryResolve` + 单矩阵检查（`ManualRegionTool` 先例），未配置或解析失败明确失败、不退回原位；注意 `FollowMatrixResolver` 接受集合，单矩阵工具要显式拒绝多矩阵并提示用循环。
4. **条件显隐与模式必填校验直接实现现有接口**：`IToolParameterVisibility` / `IToolConfigurationCheck` 已存在（`RegionDistanceTool` 即先例），XG-01/02/05/06/08/09 按模式显隐参数、把"仅某模式必填"的输入纳入流程校验，不再只靠运行时。
5. **角度参数一律经 `AngleMath`**：XG-06 角度类参数"度显示、弧度传算子"用 `AngleMath.ToDegrees` / `ToRadians`；XG-09 的 `AngleDeg` 输出和范围折算用 `Fold` / `TryGetBounds`，与 `AngleConvertTool` 行为对齐。
6. **`distance_pc` 一对多可能需逐对调用**：按 HALCON 元组语义，点到轮廓的多对多大概率要 `SelectObj` 逐对计算；沿用 RG-07 的预检模式（空对象、行列长度一致），性能逐对调用可接受（轮廓数通常不大）。
7. **新算子按项目惯例先探测**：XG-04 的 `test_closed_xld`、XG-06 的 `union_cocircular_contours_xld` 长参数表、XG-07 的椭圆算法全集，先在 20.11 上逐一实测可用性与默认值，再写实现（与描述子匹配、区域排序两轮的做法一致）。
8. **验收补两条**：XG-02 的 `ContourFeature` 合法取值在界面上以下拉呈现（配合 P1-1 的枚举化）；XG-08 增加"配对中含空轮廓"与"行列/轮廓个数不一致"用例（照抄 RegionToolPlanTests 的既有模式）。

## 5. P3：文档维护

1. 修正第 7 节序列化描述（见 P1-1）；计划头部状态随开工更新。
2. 第 6 节"每项需要同步修改的位置"补充：工具箱图标（`ToolIcons.xaml`）；枚举改字符串参数时补"强制按名称序列化 + 旧文件加载用例"一项。
3. XG-08 输出清单补 `Segments`，并在第 6 节叠加显示一行注明"最近点对以 XLD 输出，无需 DisplayOverlayBuilder 新增能力"。
4. 开发顺序确认：计划第 9 节不变——XG-01~05 → XG-06 → XG-07 → XG-08/09（前置已就绪）。

## 6. 与 REGION 评审的横向对照

本轮评审发现的问题与 REGION 评审高度同构：字符串参数枚举化、图标遗漏、点类输出的叠加显示方案都是同一批模式，说明这些值得沉淀为"新增工具/新增选项 checklist"，建议后续在 `TOOLS-PLAN-INDEX`（总索引提议见 REGION 评审 P3）中固化，避免每条计划线重复踩点。
