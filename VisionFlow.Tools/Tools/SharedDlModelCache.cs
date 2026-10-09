using System;
using System.Collections.Generic;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Runtime;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 共享模型池键（§5.4）：(ScopeToken, 解析后路径, LastWriteTimeUtc.Ticks, ModelKind, Device, BatchSize, Optimize)。
    /// 模型身份部分与实例缓存键规则一致；ScopeToken 按引用相等判定（Flow 模式 = 流程树根节点实例，Project 模式 = 进程级哨兵）。
    /// batch_size / device 是句柄级参数，参数需求不同自然分成多个池条目，不强行共享。
    /// </summary>
    public readonly struct DlModelPoolKey : IEquatable<DlModelPoolKey>
    {
        public object ScopeToken { get; }
        public string ResolvedPath { get; }
        public long LastWriteTimeUtcTicks { get; }
        public DeepLearningModelKind ModelKind { get; }
        public DeepLearningDevicePreference Device { get; }
        public int BatchSize { get; }
        public bool OptimizeForInference { get; }

        public DlModelPoolKey(object scopeToken, string resolvedPath, long lastWriteTimeUtcTicks,
            DeepLearningModelKind modelKind, DeepLearningDevicePreference device, int batchSize, bool optimizeForInference)
        {
            ScopeToken = scopeToken;
            ResolvedPath = resolvedPath;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
            ModelKind = modelKind;
            Device = device;
            BatchSize = batchSize;
            OptimizeForInference = optimizeForInference;
        }

        public bool Equals(DlModelPoolKey other)
        {
            return ReferenceEquals(ScopeToken, other.ScopeToken)
                && string.Equals(ResolvedPath, other.ResolvedPath, StringComparison.OrdinalIgnoreCase)
                && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks
                && ModelKind == other.ModelKind
                && Device == other.Device
                && BatchSize == other.BatchSize
                && OptimizeForInference == other.OptimizeForInference;
        }

        public override bool Equals(object obj)
        {
            return obj is DlModelPoolKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                ScopeToken,
                ResolvedPath == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(ResolvedPath),
                LastWriteTimeUtcTicks,
                ModelKind,
                Device,
                BatchSize,
                OptimizeForInference);
        }
    }

    /// <summary>
    /// 共享池条目：模型句柄 + 公共元数据 + 引用计数 / 串行门 / 失效标记。
    /// 回收规则唯一：<see cref="RefCount"/> 归零即处置句柄，与 Stale 无关；
    /// Stale 只控制"是否允许被新 Acquire 复用"。
    /// </summary>
    public sealed class DlModelCacheEntry
    {
        public DlModelPoolKey Key { get; internal set; }
        public HTuple Handle { get; internal set; }
        public string ResolvedPath { get; set; }
        public string ModelType { get; set; }
        public string DeviceUsed { get; internal set; }
        public int ImageWidth { get; internal set; }
        public int ImageHeight { get; internal set; }
        public string[] ClassNames { get; internal set; } = Array.Empty<string>();
        public int[] ClassIds { get; internal set; } = Array.Empty<int>();
        public string ModelSummary { get; internal set; } = string.Empty;

        /// <summary>引用归零时处置句柄的动作（由加载方注入，通常为 clear_dl_model）。</summary>
        public Action ClearAction { get; set; }

        /// <summary>
        /// 条目级串行门：本期池只承担加载 / 释放，推理串行留给 D2 专用工具在该门上 lock
        /// （语义为"省显存、牺牲并发"）。
        /// </summary>
        internal object Gate { get; } = new object();

        internal int RefCount { get; set; }
        internal bool Stale { get; set; }
    }

    /// <summary>
    /// 深度学习模型句柄共享池（static，§5.4）。
    /// 引用计数按"工具实例持有当前键一份"累计，不按运行次数累计：
    /// 键不变时工具复用已持有条目、不重复 Acquire；键变化时先归还旧键再 Acquire 新键；
    /// ReleaseResources 只归还本实例持有的那一份。
    /// </summary>
    public static class SharedDlModelCache
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<DlModelPoolKey, DlModelCacheEntry> Entries = new Dictionary<DlModelPoolKey, DlModelCacheEntry>();
        private static readonly List<DlModelCacheEntry> All = new List<DlModelCacheEntry>();
        private static int _totalLoadCount;

        /// <summary>调试计数：池累计加载（factory 调用）次数，供测试断言"同一模型只加载一份"。</summary>
        public static int DebugTotalLoadCount
        {
            get { lock (Sync) { return _totalLoadCount; } }
        }

        /// <summary>调试计数：当前存活条目数（含被 Stale 替换、仍被旧使用者持有的条目）。</summary>
        public static int DebugAliveEntryCount
        {
            get { lock (Sync) { return All.Count; } }
        }

        /// <summary>
        /// 获取指定键的当前登记条目的引用计数；条目不存在（已清除）时返回 false。
        /// </summary>
        public static bool TryGetRefCount(DlModelPoolKey key, out int refCount)
        {
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out DlModelCacheEntry entry))
                {
                    refCount = entry.RefCount;
                    return true;
                }
            }
            refCount = 0;
            return false;
        }

        /// <summary>获取指定键的当前登记条目（可能被其他持有者增加引用）。</summary>
        public static bool TryGetEntry(DlModelPoolKey key, out DlModelCacheEntry entry)
        {
            lock (Sync)
            {
                return Entries.TryGetValue(key, out entry);
            }
        }

        /// <summary>
        /// 获取或加载条目：登记条目存在且未失效时复用（RefCount + 1）；
        /// 否则调用 factory 加载新条目并登记（旧失效条目仍归原持有者，归零后自行清除）。
        /// </summary>
        public static DlModelCacheEntry Acquire(DlModelPoolKey key, Func<DlModelCacheEntry> factory)
        {
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out DlModelCacheEntry existing) && !existing.Stale)
                {
                    existing.RefCount++;
                    return existing;
                }

                DlModelCacheEntry created = factory();
                created.Key = key;
                created.RefCount = 1;
                Entries[key] = created;
                All.Add(created);
                _totalLoadCount++;
                return created;
            }
        }

        /// <summary>条目是否仍存活（未被清除）且未被标记失效——工具同键复用前据此判断。</summary>
        public static bool IsUsable(DlModelCacheEntry entry)
        {
            lock (Sync)
            {
                return entry != null && All.Contains(entry) && !entry.Stale;
            }
        }

        /// <summary>归还一份引用：RefCount 归零即处置句柄并清除条目（与 Stale 无关）。</summary>
        public static void Release(DlModelCacheEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            Action clearAction;
            lock (Sync)
            {
                if (All.Contains(entry))
                {
                    entry.RefCount--;
                }
                if (entry.RefCount > 0 || !All.Contains(entry))
                {
                    return;
                }
                All.Remove(entry);
                if (Entries.TryGetValue(entry.Key, out DlModelCacheEntry registered) && ReferenceEquals(registered, entry))
                {
                    Entries.Remove(entry.Key);
                }
                clearAction = entry.ClearAction;
                entry.ClearAction = null;
            }
            clearAction?.Invoke();
        }

        /// <summary>
        /// 按池键归还一份引用。仅适用于"当前登记条目即目标条目"的场景
        /// （失效替换后旧条目已不在字典中，持有者应按 <see cref="Release(DlModelCacheEntry)"/> 归还）。
        /// </summary>
        public static void Release(DlModelPoolKey key)
        {
            DlModelCacheEntry entry;
            lock (Sync)
            {
                if (!Entries.TryGetValue(key, out entry))
                {
                    return;
                }
            }
            Release(entry);
        }

        /// <summary>按句柄引用归还一份引用（句柄与条目共生共灭，按引用相等查找）。</summary>
        public static void ReleaseHandle(HTuple handle)
        {
            DlModelCacheEntry entry;
            lock (Sync)
            {
                entry = All.Find(e => ReferenceEquals(e.Handle, handle));
            }
            Release(entry);
        }

        /// <summary>
        /// 标记失效（§5.4，钉死语义）：只把匹配解析路径的条目置 Stale，不立即处置正在使用的句柄；
        /// 新 Acquire 不复用 Stale 条目；条目在 RefCount 归零时才真正清除。
        /// 宿主在"模型文件更新"事件里调用它实现全项目强制切换。
        /// </summary>
        public static void Invalidate(string resolvedPath)
        {
            lock (Sync)
            {
                foreach (DlModelCacheEntry entry in All)
                {
                    if (string.Equals(entry.ResolvedPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Stale = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 深度学习工具接入共享池的统一帮助类：池键规则的唯一出处（§5.4），并统一
    /// Instance / Flow / Project 三档作用域判定与 Acquire / Release 调用约定。
    /// </summary>
    public static class DlModelCachePolicy
    {
        /// <summary>Project 作用域哨兵：进程内单例，按引用相等判定。</summary>
        public static readonly object ProjectScopeToken = new object();

        /// <summary>
        /// 构造池键（模型身份部分与实例缓存键规则一致：解析后路径 + mtime + ModelKind + Device + BatchSize + Optimize）。
        /// </summary>
        public static DlModelPoolKey BuildKey(object scopeToken, string resolvedPath, long lastWriteTimeUtcTicks,
            DeepLearningModelKind modelKind, DeepLearningDevicePreference device, int batchSize, bool optimizeForInference)
        {
            return new DlModelPoolKey(scopeToken, resolvedPath, lastWriteTimeUtcTicks, modelKind, device, batchSize, optimizeForInference);
        }

        /// <summary>
        /// 解析共享作用域。返回 false 表示回退 Instance（仅 Flow 模式在锚点缺失时发生，
        /// 典型入口是 <see cref="FlowResources.Prepare(System.Collections.Generic.IEnumerable{VisionFlow.Nodes.ToolNode})"/> 快照预热）；
        /// 返回 true 时 scopeToken 为 Flow 根节点实例或 <see cref="ProjectScopeToken"/>。
        /// </summary>
        public static bool TryResolveSharedScope(ModelCacheMode mode, object anchor, out object scopeToken)
        {
            switch (mode)
            {
                case ModelCacheMode.Project:
                    scopeToken = ProjectScopeToken;
                    return true;
                case ModelCacheMode.Flow:
                    if (anchor != null)
                    {
                        scopeToken = anchor;
                        return true;
                    }
                    break;
            }
            scopeToken = null;
            return false;
        }

        /// <summary>
        /// 解析 Flow 模式流程锚点：运行上下文 <see cref="FlowContext.OwnerToken"/> 优先，
        /// 其次预热环境锚点 <see cref="FlowResources.CurrentEnvironmentAnchor"/>；均无则返回 null。
        /// </summary>
        public static object ResolveAnchor(FlowContext ctx)
        {
            return ctx?.OwnerToken ?? FlowResources.CurrentEnvironmentAnchor;
        }
    }
}
