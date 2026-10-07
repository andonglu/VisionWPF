# 数据处理、判定与逻辑补充开发计划

编写日期：2026-10-06
状态：已实现（LD-01 ~ LD-08，分支 `feature/dynamic-tool-outputs`）
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
| LD-01 | 表达式引擎、动态输出、表达式引用校验 | 基础设施，供 LD-02 / LD-04 / LD-05 使用（**已完成**） |
| LD-02 | 变量计算（数值、布尔、字符串表达式） | **新建工具**（**已完成**） |
| LD-03 | 数组处理（统计、取元素、排序、筛选、拼接） | **新建工具**（**已完成**） |
| LD-04 | 综合判定（多检测项汇总为 OK/NG 与原因） | **新建工具**（**已完成**） |
| LD-05 | IfElse 条件组（与 / 或、表达式条件、新比较符） | 增强现有节点（**已完成**） |
| LD-06 | 多分支 Switch | **新建节点**（**已完成**） |
| LD-07 | 条件循环 While、跳出循环、跳过本次 | **新建节点**（3 个，**已完成**） |
| LD-08 | 子流程调用 | **新建节点**（**已完成**） |

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

**表达式引用校验**（已实现）

- 接口 `IExpressionTool { IEnumerable<ToolExpressionDef> GetExpressions(); }`（`VisionFlow.Base\Expressions\ToolExpressions.cs`）。`ToolExpressionDef` 包含：`Parameter`（校验结果中显示的参数名，如“计算式 第 2 行”）、`Text`、`LocalNames`（允许的局部名称）、`SelfOutputs`（允许引用的本工具输出，如变量计算中排在前面的结果）。
- `FlowValidator.ValidateTool` 在检查 `[InputRef]` 与动态输出之后，对每个表达式：
  - 解析失败时报告“表达式错误：第 N 个字符处……”；
  - 报告未声明的局部名称；
  - 对 `{模块.变量}` 引用执行与 `[InputRef]` 相同的 `ValidateReference`（上游可见性、分支与循环作用域、成员与下标），允许数组整体引用；`{本模块名.输出名}` 在 `SelfOutputs` 中声明时跳过作用域检查。
- 名称比较不区分大小写，与 `FlowContext` 的变量查找一致；工具在运行时提供局部名称取值时也应不区分大小写。
- 测试：`VisionFlow.Tests\ExpressionValidationTests.cs`。

**注意**

- 项目目前没有“模块改名时同步修改引用”的机制，`[InputRef]` 引用和表达式中的引用行为一致：改名后需手动修改，校验会报告失效引用。

## 5. 数据与判定工具

### LD-02 变量计算（新工具，已实现）

- 工具箱：`07 数据与判定 / 变量计算`，ID `expression-calc`，类 `ExpressionCalcTool : ToolBase, IDynamicOutputTool, IExpressionTool, IToolConfigurationCheck`（`VisionFlow.Tools\Tools\ExpressionCalcTools.cs`）。
- 一个工具内可写多条计算，每条产生一个输出，后面的计算可以引用前面的结果（`{本模块名.结果名}`）；引用排在后面的结果在校验和运行时都报错。
- 参数：`Expressions`（多行字符串，每行 `名称|类型|表达式`，类型为 `Int` / `Double` / `Bool` / `String`，不区分大小写；只按前两个 `|` 分隔，表达式中可以写 `||`；空行忽略）。编辑窗口以表格形式编辑，保存时拼成该字符串。
- 输出：每条计算一个单值输出，名称即用户填写的名称；名称不能为空、不能含空白和 `. [ ] { } |`，不能重复（不区分大小写）。没有静态输出。
- 运行：逐条求值，任何一条失败（引用无效、除零、类型转换失败）都按工具失败处理并指出行号和结果名，失败时不写出任何输出；结果为 NaN 不算失败。
- 校验：格式错误、类型无效、未配置计算式通过新增的 `IToolConfigurationCheck`（`ToolMetadata.cs`）报告；结果名问题由动态输出校验报告；表达式语法和引用由 LD-01 的表达式校验报告。参数名统一为“计算式 第 N 行”。
- 编辑窗口 `WpfExpressionCalcToolEditWindow`：
  - 左侧表格列出名称、类型、表达式、预览值、检查结果（正确为绿色，错误为红色），可添加、删除、上移、下移；下方编辑所选计算式。
  - 每次修改都调用 `ExpressionCalcTool.CheckItem` 逐行检查，外部引用按与流程校验相同的作用域（`ReferenceSemantics`）判断。
  - 右侧列出可用变量（上游输出加所选行之前的本模块结果）和全部函数，双击插入到表达式光标处（函数插入后光标停在括号内）。
  - “运行预览”基于上次流程运行的上下文求值，并在表格中显示每条结果。
  - 有错误时保存需确认。
- 用途示例：`间隙|Double|{测量2.Row} - {测量1.Row}`、`条码正确|Bool|{读码1.FirstCode} == {Input.PartId}`、`显示文字|String|format("宽度 {0:F2} mm", {换算1.Value})`。
- 测试：`VisionFlow.Tests\ExpressionCalcToolTests.cs`。

### LD-03 数组处理（新工具，已实现）

- 工具箱：`07 数据与判定 / 数组处理`，ID `array-process`，类 `ArrayProcessTool : ToolBase, IDynamicOutputTool, IExpressionTool, IToolConfigurationCheck, INotFoundPolicy`（`VisionFlow.Tools\Tools\ArrayProcessTools.cs`）。
- 输入：数组（必填，期望类型 `IEnumerable`，候选只列出数组输出）；第二个数组（可选，拼接、逐元素运算需要；逐元素运算时也可以是单个数值）。单个值按只有一个元素的数组处理。
- 元素类型 `ElementType`：`Number`（默认，元素转为小数，非数值元素报错并提示改为文本）/ `Text`（元素转为文本）。
- 操作 `Operation`：

| 分组 | 操作 | 说明 |
|---|---|---|
| 统计 | `Statistics`（默认） | 不变换，只统计 |
| 取值 | `ElementAt` | 按 `Index` 取一个元素（负数表示从末尾数，-1 为最后一个），越界按“未找到”处理 |
| 排序 | `Sort` | 升序 / 降序（`Descending`），稳定排序，NaN 总在最后；文本按序数比较；输出原序号 `Indices` |
| 筛选 | `Filter` | 按表达式筛选，元素用 `{Item}`、序号用 `{ItemIndex}` 表示，例如 `{Item} > 10`；结果必须为布尔；输出原序号 |
| 变换 | `Map` | 按表达式逐元素计算，如 `{Item} * 0.0125`，可同时引用流程变量 |
| 拼接 | `Concat` | 两个数组首尾相接 |
| 逐元素 | `ElementWise` | 加、减、乘、除（`ElementWiseOperator`），配对规则与 RG-07 一致（等长逐一、单元素对多、否则报错）；除数为 0 时结果为 NaN |
| 去重 | `Distinct` | 按值去重，保持首次出现顺序，输出首次出现的序号 |

- 输出：
  - `Values`（数组）、`Value`（结果的第一个元素，空结果时为 NaN 或空串）：类型随元素类型变化（`Double` / `String`），通过动态输出声明，下游候选类型随之变化。
  - `Indices`（结果元素在输入中的序号）、`Count`、`Found`。
  - 统计 `ValidCount`、`Sum`、`Mean`、`Max`、`Min`、`StdDev`（总体标准差）、`Range`、`MaxIndex`、`MinIndex`：总是针对结果数组计算并忽略 NaN，因此筛选后的数量、合计等可直接引用；文本数组或没有有效数值时为 NaN，序号为 -1。
- `Filter` / `Map` 复用 LD-01 表达式引擎，`{Item}` / `{ItemIndex}` 为局部名称（不区分大小写），其他局部名称校验报错；其他操作不检查表达式。
- 配置检查：拼接、逐元素运算未配置第二个数组；统计、逐元素运算选择了文本元素。
- 结果为空时按 `FailWhenNotFound` 处理（默认失败，与其他工具一致）。
- 编辑窗口 `WpfArrayProcessToolEditWindow`：按操作只显示相关参数（取值的序号、排序方向、逐元素运算符、筛选 / 变换表达式、第二个数组），表达式可插入 `{Item}` / `{ItemIndex}` / 上游引用并实时检查；运行预览显示结果数组（序号、原序号、值）和全部统计输出。
- 测试：`VisionFlow.Tests\ArrayProcessToolTests.cs`。

### LD-04 综合判定（新工具，已实现）

- 工具箱：`07 数据与判定 / 综合判定`，ID `result-judge`，类 `ResultJudgeTool : ToolBase, IExpressionTool, IToolConfigurationCheck`（`VisionFlow.Tools\Tools\ResultJudgeTools.cs`）。
- 把多个检测项汇总成一个 OK/NG 结论与不合格原因，对标 VisionPro `CogResultsAnalysisTool` 和 VisionMaster 条件检测。
- 参数：
  - `Items`（多行字符串，每行一项：`名称|取值表达式|下限|上限|NG代码|NG信息`）。从行尾向前取后四个 `|` 分隔，取值表达式中可以写 `||`；名称和 NG 信息不能含 `|`。下限或上限留空表示不限制；NG 代码留空为 1；NG 信息留空为“名称不合格”。
  - `StopAtFirstNg`（默认 false，判完全部项）、`OkCode`（默认 0）、`OkMessage`（默认“OK”）。
- 判定规则：
  - 取值为数值时按闭区间判定；为布尔时 `true` 为合格，此时不能设置上下限。
  - NaN 或无穷、引用无法取值（含除零等求值错误）、取值类型不是数值或布尔，都判为不合格并记录原因。
  - 判定结果是数据而不是失败：只有配置错误（格式、上下限非数值或下限大于上限、NG 代码非整数、名称重复、NG 代码与合格代码相同、表达式语法错误）时工具运行失败。
- 输出：
  - `Ok`（bool）、`Code`（int，第一个不合格项的 NG 代码，合格为 `OkCode`）、`Message`（string，第一个不合格项的 NG 信息，合格为 `OkMessage`）。
  - `NgCount`、`NgNames`、`NgMessages`、`NgDetails`（如“宽度：实测 4，要求 [4.5, 5]”），以及与判定项一一对应的 `Values`（布尔记为 1 / 0，无法取值为 NaN）和 `ItemOks`。`StopAtFirstNg` 时未判定的项 `Values` 为 NaN、`ItemOks` 为 false，且不计入不合格项。
- 输出名与 `FlowOutputNode` 默认的 `Ok` / `Code` / `Message` 对应，流程输出可直接引用。
- 编辑窗口 `WpfResultJudgeToolEditWindow`：
  - 上方设置合格代码、合格信息和停止策略；表格列出名称、取值、合格范围、代码、实测值、判定（OK 绿色、NG 红色、未判定灰色）和检查结果，可添加、删除、上移、下移；下方编辑所选项。
  - 每次修改调用 `ResultJudgeTool.CheckItem` 实时检查，外部引用按与流程校验相同的作用域判断；新增项自动分配未使用的 NG 代码。
  - 右侧可双击插入变量和函数；“运行预览”基于上次流程运行结果显示每项实测值与判定，并在状态栏给出总结论。
- 测试：`VisionFlow.Tests\ResultJudgeToolTests.cs`。

## 6. 流程节点

### LD-05 IfElse 条件组（已实现）

**功能**

- 条件可以是单条比较、条件组或布尔表达式：多个条件用“全部满足（且）”或“任一满足（或）”组合（短路求值）；组内可以再嵌套组，实现“(A 且 B) 或 C”。
- 条件项类型“表达式”：直接写一个布尔表达式（LD-01），例如 `{匹配1.MatchCount} == 2 && isvalid({测量1.Row})`；结果不是布尔值时节点失败。
- 比较符在末尾追加：`Contains`、`NotContains`、`StartsWith`、`EndsWith`（按文本比较，数值按其文本形式）、`IsValid`、`IsInvalid`（一元：非空且数值不是 NaN 或无穷为有效，不使用右操作数）。

**实现**

- `VisionFlow.Base\Conditions\`：`ICondition`（`Evaluate(ctx, trace)`）、`ComparisonCondition`、`ConditionGroup`（`Logic`：`And` / `Or`，`Items`）、`ExpressionCondition`。`ComparisonCondition.Evaluate(ctx)` 保留，旧比较符的结果不变。
- `IfElseNode.Condition` 类型改为 `ICondition`。运行时在原有“[条件]”日志之后增加“[条件明细]”日志，逐项记录实际值与结果，例如 `测量1.Count（2） == 2 → 成立`（引用显示实际值，常量只显示常量），单步调试暂停时可直接查看。
- 序列化：`ConditionDto` 增加 `Kind`（省略表示单条比较、`Group`、`Expression`）、`Logic`、`Items`、`Expression`，新字段为空时不写出。`FlowSerializer.CurrentFormatVersion` 升为 2，新增 `BaseFormatVersion = 1` 与 `RequiredFormatVersion(node)`：保存时按流程实际用到的功能写最低所需版本——只有旧比较符的单条比较时写 1，文件与旧版完全一致；用到条件组、表达式条件或新增比较符（含任意嵌套位置）时写 2，旧编辑器打开时明确提示版本过高。
- `FlowValidator` 递归校验：空组报“条件组为空”；嵌套条件按位置报告，如“条件 2.1 左操作数”；表达式条件按 LD-01 规则检查语法与引用；一元比较不检查右操作数；顶层单条比较沿用“左操作数 / 右操作数”参数名。
- `ParameterPanelBuilder` 的 IfElse 面板：条件以卡片列表显示（条件 1、条件 2.1……），每项可上移、下移、删除；组和多条件时显示“全部满足（且）/ 任一满足（或）”；“+ 比较 / + 表达式 / + 条件组”添加条件；比较的左右操作数为可编辑下拉框，列出上游可用引用（`ref:模块.变量`）；一元比较符隐藏右操作数。修改先写入草稿，点“应用条件”才生效；有比较缺左操作数、表达式为空或组内无条件时拒绝应用并提示位置。顶层只有一项时保存为该项本身，单条比较因此仍按版本 1 保存。
- 测试：`VisionFlow.Tests\ConditionGroupTests.cs`。

### LD-06 多分支 Switch（新节点，已实现）

- 工具箱：`逻辑控制 / Switch 多分支`，ID `switch`；类 `SwitchNode`、`SwitchCaseNode`、`SwitchOutputDef`（`VisionFlow.Base\Nodes\SwitchNode.cs`）。新建时带“分支 1（匹配值 1）”“分支 2（匹配值 2）”和默认分支。
- 选择值：一个操作数（常量或引用），例如 `ref:分类1.Label`、`ref:通用形状匹配1.BestModelIndex`。
- 分支：每个分支是流程树中的一个节点（`SwitchCaseNode`），有名称、匹配值（逗号分隔，中英文逗号都可）和子节点；默认分支有且只有一个、固定在最后、不能删除。按顺序执行第一个命中的分支，都不命中时执行默认分支。
- 匹配规则：选择值为数值且匹配值能解析为数值时按数值比较（`180` 与 `180.0` 相等），否则按文本比较（区分大小写）。
- 公共输出：`SwitchNode.Outputs` 声明输出名与类型，每个分支在 `OutputValues` 中各自给出取值；节点之后通过 `Switch名.输出名` 引用，分支内部的输出对其他分支和 Switch 外不可见（与 IfElse 一致）。分支内跳出循环 / 跳过本次时不计算公共输出。
- 实现要点：
  - 运行时把命中的分支作为节点执行，执行轨迹、日志和单步调试都能看到进入了哪个分支；日志记录 `选择值（实际值） → 分支名`。
  - `RefCandidateService`：Switch 外只暴露公共输出，分支内只看到 Switch 上游和本分支内的上游；新增 `ScopeForSwitchCaseOutput` 用于校验各分支的公共输出取值。
  - `FlowValidator`：选择值、只能直接放分支、默认分支唯一且在最后、至少一个普通分支、匹配值为空、匹配值在多个分支重复（后者永远不会因该值命中）、公共输出名重复、每个分支都给出取值且引用在作用域内、类型兼容。
  - `FlowEditModel`：分支只能放在 Switch 的分支列表里，普通节点不能直接放进分支列表；把节点“放入” Switch 时放进第一个分支；普通分支不能移到默认分支之后，默认分支不能移动或删除；新增 `AddSwitchCase`（委托 `SwitchNode.AddCase`）在默认分支前新增“分支 N（匹配值 N）”。
  - 序列化 Kind 为 `Switch`（`Selector`、`SwitchOutputs`、分支放在 `Children`）与 `SwitchCase`（`Values`、`IsDefault`、`OutputValues`），文件版本 2；新字段为空时不写出。
  - 流程树中 Switch 下直接显示各分支（“分支: 名称（匹配值）”“默认: 名称”）；侧边面板：Switch 设置选择值（可选上游引用）、查看分支并“添加分支”、查看并“编辑公共输出...”；选中分支设置匹配值和各公共输出的取值（候选来自该分支的作用域）。
- 公共输出编辑窗口 `WpfSharedOutputsEditWindow`（IfElse 与 Switch 共用）：每个输出一张卡片，设置名称、类型（小数 / 整数 / 布尔 / 文本 / 三种数组 / 对象）和每个分支的取值（候选来自该分支的作用域）；确定前检查名称合法、不重名、各分支都已填写。编辑模型 `SharedOutputsEditor`（EditorCore）在确定后一次性写回，Switch 输出改名时各分支取值随之改名；测试：`VisionFlow.Tests\NodeEditorModelTests.cs`。
- 测试：`VisionFlow.Tests\SwitchNodeTests.cs`。

### LD-07 While 循环、跳出循环、跳过本次（新节点，已实现）

- `WhileLoopNode`（工具箱“逻辑控制 / While 循环(条件)”，ID `whileloop`）：条件（LD-05 的条件组、表达式或单条比较）成立时重复执行循环体。
  - 默认先判断后执行；勾选“先执行一次再判断”（`TestAfterBody`）为 do-while，此时条件可以引用循环体的输出（如“重试直到找到”）。
  - 条件在本循环的循环帧内求值，`Loop.Index` 指向本循环（先判断时为即将执行的序号，后判断时为刚执行完的序号）；循环体内同样可用 `Loop.Index`。条件循环不提供 `Loop.Count`（总次数事先未知）和 `Loop.Current`，引用时校验报错。
  - `MaxIterations`（默认 100，必须大于 0）防止死循环：达到上限时条件仍成立按失败处理，并提示调大次数。
  - 新建时默认条件为 `Loop.Index < 3`；每次判断都记录条件明细日志，结束时记录共执行次数。
- `BreakNode`（跳出循环，ID `break`）、`ContinueNode`（跳过本次，ID `continue`）：作用于最内层循环（For 或 While），可放在循环体内的 IfElse 分支中（任意层）；放在循环外时校验报错，运行时也明确失败。
- 实现：
  - `FlowContext.LoopControl`（`LoopControlSignal`：无 / 跳出 / 跳过本次）；跳出 / 跳过本次节点设置信号后返回成功。
  - `FlowNode.RunChildren` 每执行完一个子节点检查信号，有信号则停止执行后续兄弟节点；`IfElseNode` 在信号存在时不再计算公共输出（本次迭代已放弃，分支内被跳过的节点没有输出）。
  - 新增 `LoopNodeBase`（`ForLoopNode`、`WhileLoopNode` 的基类）：持有循环体，`RunIteration` 统一压栈循环帧并在每次迭代后读取、清除信号。引用候选、校验、序列化、资源预热、编辑模型、节点命名、流程树等原来只认 `ForLoopNode` 循环体的地方改为按 `LoopNodeBase` 处理，循环体内部输出对外不可见的规则对两种循环一致。
  - `RefLoopMode.While` 与 `RefCandidateService.ScopeForWhileCondition`：条件的作用域为循环前的上游，加上 `Loop.Index`；`TestAfterBody` 时再加上循环体输出。
  - 序列化 Kind 为 `WhileLoop`（条件写在 `Condition`，`Loop` 中写 `MaxIterations` / `TestAfterBody`）、`Break`、`Continue`，任一出现时文件版本为 2；普通 For 循环的格式不变。
  - 侧边参数面板：While 复用 IfElse 的卡片式条件编辑器（引用候选来自上述作用域），另有“先执行一次再判断”和“最多执行次数”；跳出 / 跳过本次显示用途说明。
- 测试：`VisionFlow.Tests\LoopControlTests.cs`。

### LD-08 子流程（新节点，已实现）

- 工具箱：`逻辑控制 / 子流程`，ID `subflow`；类 `SubFlowNode`（`VisionFlow.Base\Nodes\SubFlowNode.cs`）、`SubFlowLibrary` / `SubFlowDefinition`（`VisionFlow.Base\Runtime\SubFlowLibrary.cs`）。
- 用途：把常用的一段流程（如“定位 + 测宽”）保存为独立的 `.vflow.json`，在多个流程或同一流程的多个位置调用。
- 配置：
  - `FlowFile`：子流程文件路径。相对路径相对于当前流程文件所在目录（`BaseDirectory`，不写入文件）：`FlowSerializer.LoadFile` 加载时按文件目录设置（嵌套子流程按各自文件目录解析），编辑器保存后同步更新；未设置时相对于程序目录。编辑器“选择文件”时，文件在当前流程目录内（含子目录）则存相对路径，否则存完整路径。
  - 输入：子流程默认沿用当前流程的全部 `Input.*` 外部输入（如 `Input.Image`）；输入映射（`Input.名称` ← 当前流程中的引用或常量）可以追加或覆盖。
  - 输出：子流程**顶层**“流程输出”节点的全部输出，以本节点名为模块名回到当前流程（如 `子流程1.Ok`），引用候选和类型随之可见；放在分支、循环里的流程输出不作为子流程输出（不一定执行）。
- 运行：
  - 每次调用使用独立的子 `FlowContext`，子流程内部变量不会出现在当前流程中；取消令牌随父上下文。
  - 资源所有权：传入的输入在子上下文中为借用，子流程不会释放调用方的图像；输出中的 HALCON 对象从子上下文移交给当前上下文（`FlowContext.ReleaseOwnership` / `SetOwnedVariable`），随当前上下文释放；其余中间结果随子上下文释放。
  - 子流程日志加“[子流程 名称]”前缀并入当前流程；子流程失败时本节点失败并带上原因。单步调试不进入子流程内部（整体作为一步）。
- 缓存与预热：`SubFlowLibrary` 按“完整路径 + 修改时间”缓存，文件被替换后自动重新加载并释放旧定义的工具资源；`FlowResources.Prepare(root)` 会加载并预热流程中引用的子流程（按文件去重，循环引用不会重复进入，加载失败作为预热问题报告）。`FlowResources.Release` 不释放共享的子流程定义。
- 循环引用：校验时静态检查并报告引用链（如 `a.vflow.json → b.vflow.json → a.vflow.json`）；运行时嵌套超过 `SubFlowNode.MaxDepth`（8 层）明确失败。
- 校验：未选择文件、文件缺失或无法解析、循环引用、子流程输出重名、输入路径不是 `Input.名称`、输入重复、映射取值在当前流程作用域内；并以映射的输入作为额外外部输入（`ExternalInputRegistry.BeginScope`，仅当前线程、可撤销）校验子流程本身，问题以“子流程内部”参数报告。
- 序列化 Kind 为 `SubFlow`（`FlowFile`、`SubFlowInputs`），文件版本 2。
- 编辑器：流程树显示“子流程: 名称（文件名）”；侧边面板可输入或选择文件，查看完整路径、输出列表和输入映射摘要。双击节点或点“打开子流程编辑窗口...”打开 `WpfSubFlowEditWindow`：选择文件并显示加载状态、编辑输入映射（取值可选上游引用或常量）、按当前映射校验（含“子流程内部”的问题，如子流程用到但未提供的 `Input.*`）、基于上次运行结果试运行并显示各输出的值；编辑作用于草稿 `SubFlowEditor`（EditorCore），确定后才写回节点。打开子流程本身进行编辑请使用“加载流程”，暂不支持在当前窗口内展开编辑。
- 测试：`VisionFlow.Tests\SubFlowTests.cs`（含真实 HALCON 对象的所有权移交用例）。

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
