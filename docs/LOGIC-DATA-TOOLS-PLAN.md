# 数据处理、判定与逻辑补充开发计划

编写日期：2026-10-06
状态：待开发
范围：流程节点（`VisionFlow.Base\Nodes`、`VisionFlow.Base\Conditions`）与工具箱“07 结果判定”中的数据处理、判定类工具。
关联文档：

- [Region 相关算子补充开发计划](REGION-TOOLS-PLAN.md)
- [XLD 相关算子补充开发计划](XLD-TOOLS-PLAN.md)
- [定位匹配与几何测量补充开发计划](MATCH-MEASURE-TOOLS-PLAN.md)
- [图像处理补充开发计划](IMAGE-TOOLS-PLAN.md)
- [识别补充开发计划](RECOGNITION-TOOLS-PLAN.md)
- [标定补充开发计划](CALIBRATION-TOOLS-PLAN.md)

## 1. 现状

| 能力 | 现有实现 | 限制 |
|---|---|---|
| 条件分支 | `IfElseNode` + `ComparisonCondition` | 只能写一个“左 比较 右”条件，没有“与 / 或”组合；非数值只支持相等、不等 |
| 循环 | `ForLoopNode`（按次数、按集合） | 没有条件循环；不能提前跳出或跳过本次 |
| 分支输出 | `IfElseNode.BranchOutputs` | 只有两路 |
| 数值分类 | `RangeClassifyTool`（数值区间分类） | 只处理一个数值 |
| 流程输出 | `FlowOutputNode`（`Ok` / `Code` / `Message` 等） | 只做映射，不做计算 |
| 数据计算 | 无 | 两个测量值相减、换算、字符串拼接等都无法在流程内完成 |
| 数组处理 | 无 | 无法对数组求最大、平均、计数、排序 |
| 流程复用 | 无 | 相同的一段流程只能复制 |

另外两个基础限制：

- 工具输出由类型上的 `[ToolOutput]` 静态声明，`ToolMetadata.GetOutputs(Type)` 只按类型取输出，工具无法按用户配置产生输出名。
- 校验器和引用候选只认 `[InputRef]` 属性，写在文本里的引用（如表达式中的变量）不会被校验。

## 2. 拆分结论

| 编号 | 内容 | 处理方式 |
|---|---|---|
| LD-01 | 表达式引擎、动态输出、文本内引用校验 | 基础设施，供 LD-02 / LD-04 / LD-05 使用 |
| LD-02 | 变量计算（数值、布尔、字符串表达式） | **新建工具** |
| LD-03 | 数组处理（统计、取元素、排序、筛选、拼接） | **新建工具** |
| LD-04 | 综合判定（多检测项汇总为 OK/NG 与原因） | **新建工具** |
| LD-05 | IfElse 条件组（与 / 或、表达式条件、新比较符） | 增强现有节点 |
| LD-06 | 多分支 Switch | **新建节点** |
| LD-07 | 条件循环 While、跳出循环、跳过本次 | **新建节点**（3 个） |
| LD-08 | 子流程调用 | **新建节点** |

工具箱分类“07 结果判定”改名为“07 数据与判定”，LD-02 ~ LD-04 与现有“数值区间分类”放在该分类；LD-06 ~ LD-08 放在“逻辑控制”。

字符串处理不单独做工具：输入是引用、输出是值，与变量计算一致，按拆分原则作为表达式函数并入 LD-02。

## 3. 开发通用做法

- 节点类改动涉及：节点类、`FlowSerializer`（DTO 与读写）、`FlowValidator`、`RefCandidateService`、`FlowEditModel`（嵌套规则）、`ToolEditTransaction`（复制）、`NodeNaming`、`ToolboxRegistry`、WPF 的 `ParameterPanelBuilder` 与 `MainWindow` 流程树、单步调试（`FlowDebugController` 暂停点）。
- 工具类改动按 Region / XLD 计划的通用做法执行（元数据、持久化、注册、编辑窗口、测试、README）。
- **流程文件版本**：`FlowSerializer.CurrentFormatVersion` 当前为 1，旧编辑器遇到高于自身的版本会明确拒绝打开。新节点、新条件结构只有在流程实际用到时才把文件版本写为 2；未使用新功能的流程仍写 1，旧编辑器照常可读。不能只加字段而不升版本，否则旧编辑器会静默忽略新字段、运行出错误结果。
- 中文提示、日志、默认名称与现有风格一致。

## 4. 基础设施

### LD-01 表达式引擎、动态输出与文本内引用

**表达式语法**（已实现）

- 变量引用写在花括号内：`{匹配1.Score}`、`{测量1.Rows[0]}`、`{测量1.Rows.Count}`、`{匹配1.BestMatch.Row}`、`{Loop.Index}`、`{Loop.Current.Score}`，解析与 `VariableReference` 完全一致。不含点的 `{名称}` 为局部名称（如数组处理的 `{Item}`），由调用方在求值时提供取值。
- 常量：整数、小数、科学计数法、`true` / `false`、`pi`、双引号字符串（支持 `\"`、`\\`、`\n`、`\t`）。常量名与函数名不区分大小写。
- 运算符（优先级从低到高）：`?:`（右结合）、`||`、`&&`、`== !=`、`> >= < <=`、`+ -`、`* / %`、一元 `! -`。`&&`、`||`、`?:`、`if` 只计算需要的一侧。
- 函数（完整列表与说明见 `ExpressionFunctions.All`）：
  - 数学：`abs`、`sqrt`、`pow`、`min`、`max`、`round(x, n)`（远离零舍入）、`floor`、`ceil`、`sin`、`cos`、`tan`、`atan2`、`deg`、`rad`、`hypot`。
  - 判断：`isnan`、`isvalid`（数值不是 NaN 或无穷，其他值不为空）、`if(cond, a, b)`。
  - 字符串：`len`、`substr(s, start, length)`（超出范围截到末尾）、`contains`、`startswith`、`endswith`、`replace`、`trim`、`upper`、`lower`、`regex(s, pattern)`（是否匹配，200 ms 超时）、`format(模板, 参数...)`（`{0}` 占位，数值格式如 `{0:F2}`）、`str(x)`、`num(s)`（失败为 NaN）。
  - 数组：`count`、`sum`、`mean`、`maxof`、`minof`（忽略 NaN；空数组的平均、最大、最小为 NaN）、`at(arr, i)`（越界为 NaN）。
- 类型规则：
  - 值只有整数（long）、小数（double）、布尔、字符串、空值和数组；变量中的其他数值类型自动规范。
  - 整数之间的 `+ - * %` 结果为整数（溢出报错）；`/` 结果总是小数；有小数参与时结果为小数；除数为 0 报错。
  - `+` 一侧为字符串时为拼接。
  - `== !=`：数值按数值比较；一侧为字符串时按文本比较（如 `"123" == 123` 为真）；布尔只能与布尔比较。`> >= < <=` 只用于数值之间或字符串之间。NaN 参与比较时只有 `!=` 为真。
  - 类型不匹配、除零、引用无法取值都抛出 `ExpressionException`，带出错位置（`Position`，消息以“第 N 个字符处”开头）。
- 结果转换为变量类型：`ExpressionValues.ConvertTo(value, VariableType)`；Int 只接受整数或没有小数部分的有限小数。

**实现**（已实现）

- `VisionFlow.Base\Expressions\`：`ExpressionLexer`（词法）、`ExpressionParser`（递归下降语法分析，按文本缓存解析结果）、`ExpressionNodes`（语法树与求值）、`ExpressionFunctions`（内置函数表）、`ExpressionValues`（值模型与类型转换）、`ExpressionException`。不引入第三方库，不使用动态编译；成员访问只通过 `VariableReference`。
- 公开接口：`ExpressionParser.Parse` / `TryParse` → `CompiledExpression`（`References`、`LocalNames`、`Evaluate(FlowContext, resolveLocal)`、`Evaluate(Func<string, object>)`），解析结果不可变、可并发求值。
- 测试：`VisionFlow.Tests\ExpressionTests.cs`。

**动态输出**（已实现）

- 接口 `IDynamicOutputTool { IReadOnlyList<ToolOutputDef> GetDynamicOutputs(); }`（`VisionFlow.Base\Variables\ToolMetadata.cs`）。
- `ToolMetadata.GetOutputs(ToolBase tool)`：返回静态 `[ToolOutput]` 加上动态输出；名称非法或与已有输出重名（不区分大小写）的动态输出不进入结果。`RefCandidateService` 改为按工具实例取输出，引用候选与声明级校验因此同时识别动态输出。
- 输出名规则 `ToolMetadata.IsValidOutputName`：非空，不含空白和 `. [ ] { }`。
- `FlowValidator` 对动态输出报告：输出名为空、含非法字符、与该工具的其他输出重名。
- 测试：`VisionFlow.Tests\DynamicOutputTests.cs`。

**文本内引用校验**

- 新增接口 `IReferencingTool { IEnumerable<ToolTextReference> GetTextReferences(); }`（含引用路径、所在参数名、显示名）。
- `FlowValidator.ValidateTool` 在检查 `[InputRef]` 之后，对这些引用执行同样的 `ValidateReference`（上游可见性、循环上下文）。表达式语法错误作为校验错误报告。

**注意**

- 项目目前没有“模块改名时同步修改引用”的机制，`[InputRef]` 引用和表达式中的引用行为一致：改名后需手动修改，校验会报告失效引用。

## 5. 数据与判定工具

### LD-02 变量计算（新工具）

- 工具箱：`07 数据与判定 / 变量计算`，ID `expression-calc`，类 `ExpressionCalcTool : ToolBase, IDynamicOutputTool, IReferencingTool`。
- 一个工具内可写多条计算，每条产生一个输出，后面的计算可以引用前面的结果（`{本模块名.结果名}`）。
- 参数：`Expressions`（多行字符串，每行 `名称|类型|表达式`，类型为 `Int` / `Double` / `Bool` / `String`）。编辑窗口以表格形式编辑，保存时拼成该字符串。
- 输出：每条计算一个单值输出，名称即用户填写的名称；名称必须是合法标识符且不与其他行重复。
- 运行：逐条求值，任何一条失败（引用无效、除零、类型错误）都按工具失败处理并指出行号；结果为 NaN 不算失败。
- 编辑窗口：表格（名称、类型、表达式、上次运行值），表达式输入框支持插入引用候选和函数列表，实时显示语法错误。
- 用途示例：`间隙|Double|{测量2.Row} - {测量1.Row}`、`条码正确|Bool|{读码1.FirstCode} == {Input.PartId}`、`显示文字|String|format("宽度 {0:F2} mm", {换算1.Value})`。

### LD-03 数组处理（新工具）

- 工具箱：`07 数据与判定 / 数组处理`，ID `array-process`，类 `ArrayProcessTool`。
- 输入：数组（必填，`AcceptsCollection`）；第二个数组（可选，仅拼接、逐元素运算需要）。
- 操作 `Operation`：

| 分组 | 操作 | 说明 |
|---|---|---|
| 统计 | `Statistics` | 一次输出 `Count`、`Sum`、`Mean`、`Max`、`Min`、`StdDev`、`Range`、`MaxIndex`、`MinIndex`；忽略 NaN 并输出 `ValidCount` |
| 取值 | `ElementAt` | 按 `Index` 取一个元素（负数表示从末尾数），越界按“未找到”处理 |
| 排序 | `Sort` | 升序 / 降序，输出排序后的数组和原序号数组 `Indices` |
| 筛选 | `Filter` | 按表达式筛选，元素用 `{Item}`、序号用 `{ItemIndex}` 表示，例如 `{Item} > 10`；输出筛选结果和原序号 |
| 变换 | `Map` | 按表达式逐元素计算，如 `{Item} * 0.0125` |
| 拼接 | `Concat` | 两个数组首尾相接 |
| 逐元素 | `ElementWise` | 两个数组逐元素加减乘除，配对规则与 RG-07 一致（等长逐一、单元素对多、否则报错） |
| 去重 | `Distinct` | 按值去重，保持首次出现顺序 |

- 输出：`Values`（数组）、`Value`（单值，取第一个或统计值）、`Indices`、统计类单值、`Count`、`Found`。数值数组输出类型为 `Double`；字符串数组只支持取值、排序、筛选、拼接、去重、计数。
- `Filter` / `Map` 复用 LD-01 表达式引擎，`{Item}` / `{ItemIndex}` 作为保留引用。

### LD-04 综合判定（新工具）

- 工具箱：`07 数据与判定 / 综合判定`，ID `result-judge`，类 `ResultJudgeTool : ToolBase, IReferencingTool`。
- 把多个检测项汇总成一个 OK/NG 结论与不合格原因，对标 VisionPro `CogResultsAnalysisTool` 和 VisionMaster 条件检测。
- 参数：`Items`（多行字符串，每行一项：`名称|取值表达式|下限|上限|NG代码|NG信息`；下限或上限留空表示不限制；取值为布尔时下限、上限留空，`true` 为合格）。`StopAtFirstNg`（默认 false，判完全部项）。`OkCode`（默认 0）、`OkMessage`（默认“OK”）。
- 判定规则：取值为 NaN、引用无效均判为不合格并记录原因；区间为闭区间。
- 输出：
  - `Ok`（bool）、`Code`（int，第一个不合格项的 NG 代码，合格为 `OkCode`）、`Message`（string，第一个不合格项的 NG 信息）。
  - `NgCount`、`NgNames`（数组）、`NgMessages`（数组）、`Values`（每项实测值数组）、`ItemOks`（每项是否合格数组）。
- 输出名与 `FlowOutputNode` 默认的 `Ok` / `Code` / `Message` 对应，流程输出可直接引用。
- 编辑窗口：表格编辑各项，显示上次运行的实测值和每项判定结果（合格绿色，不合格红色）。

## 6. 流程节点

### LD-05 IfElse 条件组

**功能**

- 条件改为条件组：多条比较用“全部满足（与）”或“任一满足（或）”组合；组内可以再嵌套组，实现“(A 与 B) 或 C”。
- 新增条件项类型“表达式”：直接写一个布尔表达式（LD-01），例如 `{匹配1.MatchCount} == 2 && isvalid({测量1.Row})`。
- 比较符在末尾追加：`Contains`、`NotContains`、`StartsWith`、`EndsWith`（字符串）、`IsValid`、`IsInvalid`（一元，判断是否为 NaN 或无效，不需要右操作数）。

**开发方法**

- 新增 `ICondition`（`Evaluate(ctx)`、`GetReferences()`），`ComparisonCondition`、新增的 `ConditionGroup`（`Logic`：`And` / `Or`，`Items`）、`ExpressionCondition` 实现该接口。
- `IfElseNode.Condition` 类型由 `ComparisonCondition` 改为 `ICondition`；单条比较仍用 `ComparisonCondition`，旧代码与测试继续可用。
- 序列化：`ConditionDto` 增加 `Kind`（`Compare` / `Group` / `Expression`）、`Logic`、`Items`、`Expression`。只有一条比较时按原格式写出，文件版本保持 1；用到组或表达式时文件版本写 2。读取时缺少 `Kind` 按原格式解析。
- `FlowValidator.ValidateIfElse` 递归校验组内全部条件；空组报错。
- `ParameterPanelBuilder` 的 IfElse 面板改为条件列表：每行一个条件，支持添加、删除、上移、下移、切换“与 / 或”，“添加分组”打开子列表。
- 日志与单步调试：记录每个条件项的实际值与结果，便于定位为什么走了某个分支。

### LD-06 多分支 Switch（新节点）

- 工具箱：`逻辑控制 / Switch 多分支`，类 `SwitchNode : FlowNode`。
- 选择值：一个操作数（常量或引用，整数或字符串），例如 `{分类1.Label}`、`{通用形状匹配1.BestModelIndex}`。
- 分支：若干 `Case`（匹配值，可写多个值用逗号分隔）和一个 `Default`；按顺序取第一个匹配的分支执行。
- 公共输出：与 IfElse 的 `BranchOutputs` 相同的机制，每个分支各自给出取值，节点之后的工具通过公共输出引用，不直接引用分支内部的变量。
- 开发方法：参照 `IfElseNode` 的分支结构与公共输出实现；`FlowEditModel` 支持把节点拖入任一分支；`RefCandidateService` 中分支内部只能看到分支外上游与本分支内的输出；序列化 Kind 为 `Switch`，文件版本 2。

### LD-07 While 循环、跳出循环、跳过本次（新节点）

- `WhileLoopNode`：条件（LD-05 的条件组）为真时重复执行循环体；`MaxIterations`（默认 100，必须大于 0）防止死循环，超过时按失败处理并给出中文提示。循环体内可用 `Loop.Index`。
- `BreakNode`（跳出循环）、`ContinueNode`（跳过本次）：只能放在循环体内（直接或经 IfElse / Switch 嵌套），放在循环外时校验报错。
- 开发方法：
  - 在 `FlowContext` 增加循环控制标志（无 / 跳出 / 跳过本次）；`BreakNode` / `ContinueNode` 设置标志后返回成功。
  - `FlowNode.RunChildren` 每执行完一个子节点检查标志，有标志则停止执行后续兄弟节点并返回。
  - `ForLoopNode` 与 `WhileLoopNode` 每次迭代后读取并清除标志：跳出则结束循环，跳过本次则进入下一次。
  - 序列化 Kind 为 `WhileLoop` / `Break` / `Continue`，文件版本 2。

### LD-08 子流程（新节点）

- 工具箱：`逻辑控制 / 子流程`，类 `SubFlowNode : FlowNode`。
- 用途：把常用的一段流程（如“定位 + 测宽”）保存为独立的 `.vflow.json`，在多个流程或同一流程的多个位置调用。
- 配置：
  - `FlowFile`：子流程文件路径（相对当前流程文件所在目录保存，便于整体拷贝）。
  - 输入映射：子流程中声明的外部输入（如 `Input.Image`、`Input.Threshold`）→ 当前流程中的引用或常量。
  - 输出：子流程中“流程输出”节点的全部输出，作为本节点的输出（模块名即本节点名），供后续节点引用。
- 运行：每次调用使用独立的子 `FlowContext`，变量互不污染；子上下文中的 HALCON 资源在输出复制到父上下文后统一释放，输出中的 HALCON 对象按所有权规则转移给父上下文。
- 约束：
  - 加载时检测循环引用（A 调 B、B 调 A），发现时明确失败。
  - 子流程文件按“路径 + 修改时间”缓存，并参与 `FlowResources` 预热。
  - 子流程文件缺失或输入映射不完整时，校验报错。
- 编辑器：双击节点打开子流程文件（新窗口或新标签）；参数面板显示输入映射表与输出列表。
- 本项改动面最大，排在最后。

## 7. 暂缓

- **脚本（C#）**：VisionPro 与 VisionMaster 都提供脚本，但涉及编译、部署和安全风险。变量计算、数组处理、综合判定已覆盖大部分需求，待确有需要时另行评估。
- **计数器、产量统计、跨次运行的全局变量**：属于生产运行状态，由上层项目负责；需要的数值可通过外部输入（`ExternalInputRegistry`）注入。

## 8. 兼容性要求

- 现有 IfElse、For 循环、流程输出的文件格式与行为不变；未使用新功能的流程保存后文件版本仍为 1。
- 新节点、条件组、表达式条件只在被使用时把文件版本写为 2；旧编辑器打开时明确提示版本过高。
- `ComparisonCondition` 的现有比较符与求值结果不变，新比较符追加在枚举末尾。
- 分类改名（“07 结果判定”→“07 数据与判定”）只影响工具箱显示，不影响工具 ID 与流程文件。

## 9. 验收

- LD-01：表达式单元测试覆盖全部运算符、函数、优先级、类型错误、引用解析（含数组下标、成员、循环变量）；引用无效时校验能指出所在参数与行号。
- LD-02：多行计算互相引用正确；输出名出现在下游引用候选中；修改输出名后下游引用被校验报告。
- LD-03：统计结果与手算一致；NaN 被忽略；`Filter` / `Map` 表达式正确；逐元素配对规则与 RG-07 一致。
- LD-04：全部合格时 `Ok = true`；任一不合格时 `Code` / `Message` 来自第一个不合格项；`StopAtFirstNg` 生效。
- LD-05：“(A 与 B) 或 C” 的真值表全部正确；只用单条比较的流程保存后与旧文件逐字节一致（字段顺序除外）且版本为 1。
- LD-06：选择值匹配多个值、默认分支、公共输出均正确。
- LD-07：`MaxIterations` 生效；嵌套在 IfElse 中的跳出、跳过本次对最内层循环生效；放在循环外时校验报错。
- LD-08：子流程输入输出映射正确；循环引用被拒绝；子流程运行后父上下文中无泄漏的 HALCON 对象。
- 全部回归测试和 `examples\*.vflow.json` 通过。

## 10. 开发顺序

1. LD-01（表达式引擎、动态输出、文本内引用校验）。
2. LD-02（变量计算）、LD-04（综合判定）。
3. LD-05（IfElse 条件组）。
4. LD-03（数组处理）。
5. LD-07（While、跳出、跳过本次）、LD-06（Switch）。
6. LD-08（子流程）。
