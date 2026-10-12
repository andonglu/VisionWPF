using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 一次深度学习模型装载请求的参数快照（§5.4 三模式共用）：
    /// 由各工具在 Ensure / Prepare 调用时按当前配置组装（ModuleName 可变，故按调用时快照传入）；
    /// 类型校验错误与 Flow 无锚点回退日志经消息工厂注入，保留各工具的确切措辞。
    /// </summary>
    internal sealed class DlModelLoadRequest
    {
        public string ModuleName { get; set; }

        public string ModelFilePath { get; set; }

        public DeepLearningDevicePreference Device { get; set; }

        public int BatchSize { get; set; }

        public bool OptimizeForInference { get; set; }

        public ModelCacheMode ModelCacheMode { get; set; }

        /// <summary>进入缓存键的模型种类：专用工具为固定值；统一推理工具为配置的 ModelKind（含 Auto）。</summary>
        public DeepLearningModelKind ModelKind { get; set; }

        /// <summary>类型校验：入参为规范化后的实际模型类型；返回 null 表示通过，否则为类型不匹配错误消息。</summary>
        public Func<string, string> ValidateModelType { get; set; }

        /// <summary>Flow 模式无锚点回退日志的消息工厂：入参为模块名，返回完整日志文本。</summary>
        public Func<string, string> BuildNoAnchorFallbackLog { get; set; }
    }

    /// <summary>
    /// 深度学习模型装载器（§5.4）：四个深度学习工具共用的"三模式模型缓存 + 生命周期"实现。
    /// 内部持有 _sync / _cached / _poolEntry，Ensure / Prepare / Release 内部自行加锁串行化；
    /// Instance 模式句柄由本实例直接持有，Flow / Project 模式经 SharedDlModelCache 按"实例持有一份"引用计数共享。
    /// </summary>
    internal sealed class DlModelLoader
    {
        /// <summary>模型读取结果（中间型）：刚读出的句柄与全部元数据，尚未绑定缓存键。</summary>
        internal sealed class LoadedModel
        {
            public HTuple Handle { get; init; }
            public string ModelType { get; init; }
            public string DeviceUsed { get; init; }
            public int ImageWidth { get; init; }
            public int ImageHeight { get; init; }
            public int ImageNumChannels { get; init; }
            public double RangeMin { get; init; }
            public double RangeMax { get; init; }
            public string[] ClassNames { get; init; } = Array.Empty<string>();
            public int[] ClassIds { get; init; } = Array.Empty<int>();
            public string ModelSummary { get; init; } = string.Empty;
        }

        /// <summary>缓存模型：绑定缓存键与持有方式（PoolEntry 为空 = Instance 模式直接持有句柄）。</summary>
        internal sealed class CachedModel
        {
            public object Key { get; init; }
            public HTuple Handle { get; init; }
            public string ResolvedPath { get; init; }
            public string ModelType { get; init; }
            public string DeviceUsed { get; init; }
            public int ImageWidth { get; init; }
            public int ImageHeight { get; init; }
            public int ImageNumChannels { get; init; }
            public double RangeMin { get; init; }
            public double RangeMax { get; init; }
            public string[] ClassNames { get; init; } = Array.Empty<string>();
            public int[] ClassIds { get; init; } = Array.Empty<int>();
            public string ModelSummary { get; init; } = string.Empty;

            /// <summary>共享池条目；Instance 模式下为 null（句柄由本实例直接持有）。</summary>
            public DlModelCacheEntry PoolEntry { get; init; }
        }

        private readonly object _sync = new object();
        private CachedModel _cached;
        private DlModelCacheEntry _poolEntry;

        /// <summary>Instance 模式下的推理串行门（共享模式的推理在条目 Gate 上串行）。</summary>
        internal object Sync => _sync;

        /// <summary>确保模型已按请求加载（三模式，§5.4），返回当前缓存模型；内部加锁串行化。</summary>
        internal CachedModel Ensure(FlowContext ctx, DlModelLoadRequest request)
        {
            lock (_sync)
            {
                return EnsureCachedModel(ctx, request);
            }
        }

        /// <summary>预热加载（无流程上下文，Flow 模式无锚点时回退 Instance 且不写日志）。</summary>
        internal void Prepare(DlModelLoadRequest request)
        {
            Ensure(null, request);
        }

        /// <summary>释放本实例持有的模型：共享模式归还一份池引用，Instance 模式直接清句柄。</summary>
        internal void Release()
        {
            lock (_sync)
            {
                if (_poolEntry != null)
                {
                    // 只归还本实例持有的那一份；句柄由池在引用归零时统一处置。
                    SharedDlModelCache.Release(_poolEntry);
                    _poolEntry = null;
                    _cached = null;
                    return;
                }
                if (_cached != null)
                {
                    DeepLearningToolShared.TryClearDlModel(_cached.Handle);
                    _cached = null;
                }
            }
        }

        /// <summary>
        /// 配置校验的模型路径段（四个深度学习工具共用）：空路径 → "请先指定 .hdl 模型文件"（并终止后续校验）、
        /// 扩展名非 .hdl → "模型文件应为 .hdl"、BatchSize≤0 → "BatchSize 必须大于 0"、文件不存在 → "模型文件不存在"；
        /// toolSpecificIssues（MinScore / TopK / MaxDetections / ClassFilter 等工具特有校验）按原顺序插在数值校验与存在性校验之间。
        /// </summary>
        internal static IEnumerable<ToolConfigurationIssue> CheckModelConfiguration(
            string modelFilePath, int batchSize, string modelFilePathParameter, string batchSizeParameter,
            IEnumerable<ToolConfigurationIssue> toolSpecificIssues)
        {
            if (string.IsNullOrWhiteSpace(modelFilePath))
            {
                yield return new ToolConfigurationIssue(modelFilePathParameter, "请先指定 .hdl 模型文件");
                yield break;
            }

            string expanded = Environment.ExpandEnvironmentVariables(modelFilePath.Trim());
            if (Path.GetExtension(expanded).Length > 0 && !string.Equals(Path.GetExtension(expanded), ".hdl", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ToolConfigurationIssue(modelFilePathParameter, "模型文件应为 .hdl");
            }
            if (batchSize <= 0)
            {
                yield return new ToolConfigurationIssue(batchSizeParameter, "BatchSize 必须大于 0");
            }

            foreach (ToolConfigurationIssue issue in toolSpecificIssues)
            {
                yield return issue;
            }

            string path = DeepLearningToolShared.EnumerateModelPathCandidates(modelFilePath).FirstOrDefault(File.Exists);
            if (path == null)
            {
                yield return new ToolConfigurationIssue(modelFilePathParameter, "模型文件不存在");
            }
        }

        // ======================= 三模式装载（DlModelCachePolicy，§5.4） =======================

        private CachedModel EnsureCachedModel(FlowContext ctx, DlModelLoadRequest request)
        {
            string resolvedPath = DeepLearningToolShared.ResolveExistingModelPath(request.ModelFilePath);
            if (request.ModelCacheMode != ModelCacheMode.Instance)
            {
                // 共享模式（Flow / Project，§5.4）：锚点缺失时 EnsureSharedCachedModel 返回 null 回退 Instance。
                CachedModel shared = EnsureSharedCachedModel(ctx, resolvedPath, request);
                if (shared != null)
                {
                    return shared;
                }
            }
            return EnsureInstanceCachedModel(resolvedPath, request);
        }

        private CachedModel EnsureSharedCachedModel(FlowContext ctx, string resolvedPath, DlModelLoadRequest request)
        {
            object anchor = DlModelCachePolicy.ResolveAnchor(ctx);
            if (!DlModelCachePolicy.TryResolveSharedScope(request.ModelCacheMode, anchor, out object scopeToken))
            {
                // Flow 模式但无法确定流程根锚点（Prepare(IEnumerable<ToolNode>) 快照预热入口），回退 Instance 并补记日志。
                ctx?.AddLog(FlowLogLevel.Warning, request.BuildNoAnchorFallbackLog(request.ModuleName));
                return null;
            }

            DlModelPoolKey key = DlModelCachePolicy.BuildKey(scopeToken, resolvedPath,
                File.GetLastWriteTimeUtc(resolvedPath).Ticks, request.ModelKind, request.Device, request.BatchSize, request.OptimizeForInference);
            if (_poolEntry != null && _poolEntry.Key.Equals(key) && SharedDlModelCache.IsUsable(_poolEntry))
            {
                // 引用计数按"实例持有"：键不变复用已持有条目，不重复 Acquire。
                return _cached;
            }

            // 键变化（或已失效）：先归还旧键引用（或旧实例句柄），再 Acquire 新键。
            Release();
            DlModelCacheEntry entry = SharedDlModelCache.Acquire(key, () => LoadSharedEntry(key, request));
            _poolEntry = entry;
            _cached = ToCachedModel(entry, key);
            return _cached;
        }

        private DlModelCacheEntry LoadSharedEntry(DlModelPoolKey key, DlModelLoadRequest request)
        {
            HTuple modelHandle = null;
            try
            {
                LoadedModel loaded = LoadModel(key.ResolvedPath, request);
                modelHandle = loaded.Handle;
                HTuple handle = loaded.Handle;
                return new DlModelCacheEntry
                {
                    Key = key,
                    Handle = loaded.Handle,
                    ResolvedPath = key.ResolvedPath,
                    ModelType = loaded.ModelType,
                    DeviceUsed = loaded.DeviceUsed,
                    ImageWidth = loaded.ImageWidth,
                    ImageHeight = loaded.ImageHeight,
                    ClassNames = loaded.ClassNames,
                    ClassIds = loaded.ClassIds,
                    ModelSummary = loaded.ModelSummary,
                    ClearAction = () => DeepLearningToolShared.TryClearDlModel(handle)
                };
            }
            catch
            {
                if (modelHandle != null)
                {
                    DeepLearningToolShared.TryClearDlModel(modelHandle);
                }
                throw;
            }
        }

        private CachedModel EnsureInstanceCachedModel(string resolvedPath, DlModelLoadRequest request)
        {
            object key = (resolvedPath.ToUpperInvariant(), File.GetLastWriteTimeUtc(resolvedPath).Ticks,
                request.ModelKind, request.Device, request.BatchSize, request.OptimizeForInference);
            if (_cached != null && Equals(_cached.Key, key))
            {
                return _cached;
            }

            Release();
            LoadedModel loaded = LoadModel(resolvedPath, request);
            try
            {
                _cached = ToCachedModel(loaded, key, resolvedPath);
                return _cached;
            }
            catch
            {
                DeepLearningToolShared.TryClearDlModel(loaded.Handle);
                throw;
            }
        }

        /// <summary>读取模型并按请求校验类型（不匹配时按消息工厂抛中文错误）、设置 batch/optimize 与设备。</summary>
        private static LoadedModel LoadModel(string resolvedPath, DlModelLoadRequest request)
        {
            HOperatorSet.ReadDlModel(resolvedPath, out HTuple modelHandle);
            try
            {
                string actualType = DeepLearningToolShared.NormalizeModelType(
                    DeepLearningToolShared.ReadStringParam(modelHandle, "type", "generic"));
                string typeError = request.ValidateModelType(actualType);
                if (typeError != null)
                {
                    throw new InvalidOperationException(typeError);
                }

                if (request.BatchSize > 0)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "batch_size", request.BatchSize);
                }
                if (request.OptimizeForInference)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "optimize_for_inference", "true");
                }

                string deviceUsed = DeepLearningToolShared.ConfigureDevice(modelHandle, request.Device);
                return new LoadedModel
                {
                    Handle = modelHandle,
                    ModelType = actualType,
                    DeviceUsed = deviceUsed,
                    ImageWidth = DeepLearningToolShared.ReadIntParam(modelHandle, "image_width", fallback: 0),
                    ImageHeight = DeepLearningToolShared.ReadIntParam(modelHandle, "image_height", fallback: 0),
                    ImageNumChannels = DeepLearningToolShared.ReadIntParam(modelHandle, "image_num_channels", fallback: 0),
                    RangeMin = DeepLearningToolShared.ReadDoubleParam(modelHandle, "image_range_min", fallback: 0),
                    RangeMax = DeepLearningToolShared.ReadDoubleParam(modelHandle, "image_range_max", fallback: 255),
                    ClassNames = DeepLearningToolShared.ReadStringArrayParam(modelHandle, "class_names"),
                    ClassIds = DeepLearningToolShared.ReadIntArrayParam(modelHandle, "class_ids"),
                    ModelSummary = DeepLearningToolShared.ReadStringParam(modelHandle, "summary", string.Empty)
                };
            }
            catch
            {
                DeepLearningToolShared.TryClearDlModel(modelHandle);
                throw;
            }
        }

        private static CachedModel ToCachedModel(DlModelCacheEntry entry, object key)
        {
            // 预处理参数（range/通道数）不进入池键（模型身份的一部分），随句柄每次读取。
            HTuple handle = entry.Handle;
            return new CachedModel
            {
                Key = key,
                Handle = entry.Handle,
                ResolvedPath = entry.ResolvedPath,
                ModelType = entry.ModelType,
                DeviceUsed = entry.DeviceUsed,
                ImageWidth = entry.ImageWidth,
                ImageHeight = entry.ImageHeight,
                ImageNumChannels = DeepLearningToolShared.ReadIntParam(handle, "image_num_channels", fallback: 0),
                RangeMin = DeepLearningToolShared.ReadDoubleParam(handle, "image_range_min", fallback: 0),
                RangeMax = DeepLearningToolShared.ReadDoubleParam(handle, "image_range_max", fallback: 255),
                ClassNames = entry.ClassNames,
                ClassIds = entry.ClassIds,
                ModelSummary = entry.ModelSummary,
                PoolEntry = entry
            };
        }

        private static CachedModel ToCachedModel(LoadedModel loaded, object key, string resolvedPath)
        {
            return new CachedModel
            {
                Key = key,
                Handle = loaded.Handle,
                ResolvedPath = resolvedPath,
                ModelType = loaded.ModelType,
                DeviceUsed = loaded.DeviceUsed,
                ImageWidth = loaded.ImageWidth,
                ImageHeight = loaded.ImageHeight,
                ImageNumChannels = loaded.ImageNumChannels,
                RangeMin = loaded.RangeMin,
                RangeMax = loaded.RangeMax,
                ClassNames = loaded.ClassNames,
                ClassIds = loaded.ClassIds,
                ModelSummary = loaded.ModelSummary,
                PoolEntry = null
            };
        }
    }
}
