# DEEP-LEARNING-INFERENCE-PLAN 评审意见（第三轮）

评审日期：2026-10-09
评审对象：`docs\DEEP-LEARNING-INFERENCE-PLAN.md`、`docs\DEEP-LEARNING-INFERENCE-DEVELOPMENT-PLAN.md`
评审方式：与当前代码核对（`VisionFlow.Tools\Tools\DeepLearningTools.cs`、`VisionFlow.Base\Core\FlowContext.cs`、`VisionFlow.Base\Runtime\FlowResources.cs`、`VisionFlow.Base\Engine\FlowEngine.cs`、`VisionFlow.Base\Core\IToolResourceLifecycle.cs`）并针对新增“跨流程共享句柄”方案做生命周期与框架入口一致性审查。
评审结论：**方向正确，`Instance / Flow / Project` 三档缓存语义与“省显存、牺牲并发”的目标成立，值得继续推进；但当前方案仍有 2 个 P1 级问题和 3 个 P2 级问题未闭合，暂不建议直接编码。需要先把 Acquire/Release 生命周期、预热/预览锚点一致性与失效语义补清，再进入实现。**

## 1. 总体评价

- 新增“共享范围用户可配”的方向是合理的，能覆盖现场“同一模型跨流程只加载一份”的真实需求。
- `ModelCacheMode = Instance / Flow / Project` 的拆分方式清晰，默认 `Instance` 保持兼容性，这一点是正确的。
- 共享池键把 `ModelKind` / `Device` / `BatchSize` / `Optimize` 纳入身份，符合前两轮评审提出的“同一份缓存键规则”要求。
- 明确写出“共享句柄串行推理，省显存但牺牲并发”，这是必须保留的好点。

## 2. 已核实的成立部分

| 项 | 结论 |
|---|---|
| 共享需求本身成立 | 方案已把“同一模型跨流程共享”作为显式用户需求写入，并提供 `ModelCacheMode` 让用户自行选择，不强加给所有现场 |
| 兼容性思路成立 | 默认值 `Instance` = 现有行为，不改既有流程加载/释放语义 |
| 池键思路成立 | `(ScopeToken, 解析后路径, LastWriteTimeUtc, ModelKind, Device, BatchSize, Optimize)` 能把句柄级参数差异隔离开，不会错误复用 |
| 并发取舍表达成立 | 共享句柄串行访问的取舍已在方案中写明，语义明确 |

## 3. P1：动工前必须处理

### P1-1 Acquire/Release 生命周期会导致引用计数泄漏（硬性）

当前方案写法是：

- 工具在 `Run` 中 Acquire；
- `ReleaseResources()` 时归还池条目。

这与当前工具生命周期并不匹配。现有 `IToolResourceLifecycle` 的 `ReleaseResources()` 只在流程关闭、节点删除、配方切换等时机调用，不会在每次 `Run` 后自动调用。实际代码里：

- `FlowResources.Release(ToolBase)` 才会调用 `ReleaseResources()`；
- `FlowEngine.Run(...)` 只执行节点，不负责工具级缓存归还。

因此如果共享池按“每次 Run 都 Acquire 一次”实现，同一个工具实例在生产中连续运行 N 次，`RefCount` 会增长 N 次，但流程关闭时只 Release 1 次，池条目无法归零，形成稳定泄漏。

**建议修正为：**

- 每个工具实例按“当前缓存键”最多 Acquire 一次；
- 键不变则复用已持有条目，不重复加引用；
- 键变化时先 Release 旧键，再 Acquire 新键；
- `ReleaseResources()` 只负责归还当前实例持有的那一份引用。

这条要写回方案正文，否则实现期很容易按现文字误做。

### P1-2 `Flow` 模式与现有预热入口不自洽（硬性）

方案提出：

- `FlowContext.OwnerToken` 作为 `Flow` 锚点；
- `FlowEngine.Run` / `FlowResources.Prepare(root)` 写入锚点。

但当前框架里还有一个公开入口：

- `FlowResources.Prepare(IEnumerable<ToolNode>)`

该入口直接遍历工具节点并调用 `lifecycle.Prepare()`，没有 `FlowContext`，也没有 `OwnerToken` 可注入。也就是说，后台“先快照节点列表、再预热”的标准路径下，`Flow` 模式拿不到你定义的锚点。

这会导致两个问题：

1. 运行时和预热时的共享语义可能不一致；
2. 文档里宣称的 `Flow` 模式在部分入口下实际上无法成立。

**建议二选一并写死：**

- 要么扩框架，让 `Prepare(IEnumerable<ToolNode>)` 也能带 `OwnerToken`；
- 要么正式规定：`Flow` 模式只对 `Prepare(root)` / `Run(root, ctx)` 这类有流程根锚点的入口生效，快照预热路径自动退化为 `Instance` 并记录日志。

当前文档没有明确这一点，必须补。

## 4. P2：开发前应补清

### P2-1 预览上下文与 `Flow` 锚点语义未闭合

方案写到：

- 编辑器工作副本的 Acquire/Release 天然配对；
- 引用计数保证“确定”和“取消”都不误释放正主句柄。

但现有 `FlowContext.CreatePreviewContext()` 并不会自动继承任何未来新增的 `OwnerToken` 字段，除非显式扩展实现。若不补这一点，`Flow` 模式下编辑器测试与正式运行可能落到不同池条目，文档中的“天然配对”论证并不成立。

**建议补充约定：**

- 预览上下文是否继承源上下文的 `OwnerToken`；
- 若继承，编辑器测试属于同一流程共享；
- 若不继承，则预览固定退化为 `Instance`，并把该限制写入方案。

### P2-2 `Invalidate(resolvedPath)` 的失效语义不完整

方案中一方面写：

- 模型文件更新时间变更后，旧条目等待引用归零再清除；

另一方面又引入：

- `SharedDlModelCache.Invalidate(resolvedPath)` 按路径强制清池。

问题在于：文档没有说明“强制清池”遇到 `RefCount > 0` 时怎么办。

如果直接 `clear_dl_model`：

- 可能清掉正在运行中的句柄。

如果只是标记失效：

- 那它其实不是“立即清池”，而是“标脏后延迟回收”。

**建议补明确语义：**

- `Invalidate(path)` 只标记 stale，不立刻释放仍被引用的条目；
- 新 Acquire 不再复用 stale 条目；
- 条目在 `RefCount == 0` 时再真正 `clear_dl_model`。

### P2-3 “同一流程文档”的判定对象需要钉死

当前方案写“`Flow` 模式锚点 = 根节点”，这是可行的，但还缺一句关键定义：

- 这里的“同一流程文档”是指“同一个根节点实例”，还是“同一路径加载出的同一文件”？

这会影响：

- 同一路径的两个编辑标签页是否共享；
- 子流程被不同父流程引用时是否共享；
- 流程重载后是否应沿用旧共享条目。

**建议直接钉死为：**

- `Flow` 作用域 = 同一个流程树根节点实例；
- 不是“同一路径文件级共享”；
- 跨流程共享统一走 `Project`。

这样边界最清晰，也最贴近当前框架对象模型。

## 5. P3：开发计划需要同步更新

当前方案已经把共享缓存写得很具体，但开发计划还没有同步到同等粒度，存在“方案已加、执行稿未跟”的问题。

目前开发计划中的 D1 仅写了：

- 提炼模型缓存键；
- 准备共享帮助逻辑；

但没有把以下内容作为明确交付项写进去：

- `ModelCacheMode` 参数落地；
- `SharedDlModelCache` 与 `DlModelCachePolicy` 实现；
- `FlowContext.OwnerToken` 或等价锚点机制；
- `FlowEngine` / `FlowResources` 的入口改造；
- 共享缓存专项单测与 HALCON 门禁测试。

**建议把开发计划补成显式任务：**

1. D1 增加“共享池与锚点机制”子任务；
2. 文件落点规划增加：
   - `VisionFlow.Base\Core\FlowContext.cs`
   - `VisionFlow.Base\Runtime\FlowResources.cs`
   - `VisionFlow.Base\Engine\FlowEngine.cs`
3. 测试规划增加：
   - 非 HALCON：引用计数、键切换、失效、预览副本、锚点缺失回退；
   - HALCON：`Instance / Flow / Project` 三档共享行为、mtime 切换、最后引用释放。

## 6. 建议写回方案的修订点

为避免实现期再争议，建议在方案正文中直接补入以下定稿语句：

1. **引用计数语义**
   - “共享池引用计数按工具实例持有，而不是按运行次数累计。”

2. **键变化语义**
   - “工具实例持有的缓存键发生变化时，先归还旧键，再获取新键。”

3. **预热入口语义**
   - “`Flow` 模式仅在可确定流程根锚点的入口下成立；锚点不可确定时自动回退为 `Instance` 并写日志。”

4. **预览语义**
   - “预览上下文是否继承 `OwnerToken` 必须在实现前定稿，并据此确定编辑器是否参与 `Flow` 级共享。”

5. **失效语义**
   - “`Invalidate(path)` 仅标记条目失效，不立即释放仍被引用的句柄；新获取不再复用失效条目，待引用归零后再清除。”

## 7. 结论

- 这次新增的“跨流程共享”方案值得保留，不建议删回去。
- 但必须先修正文档里的生命周期和入口一致性问题，再进入代码实现。
- 当前最重要的不是马上开写共享池，而是先把以下三点在文档中钉死：
  - 引用计数按“实例持有”而非“运行次数”；
  - `Flow` 模式在不同预热入口下的成立边界；
  - `Invalidate(path)` 的延迟回收语义。

处理完这些问题后，再按开发计划把共享缓存纳入 D1 与 D2 的正式开发范围是合理的。
