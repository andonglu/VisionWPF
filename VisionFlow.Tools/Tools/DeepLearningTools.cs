using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>深度学习模型类型：自动探测 / 分类 / 检测 / 分割。</summary>
    public enum DeepLearningModelKind
    {
        Auto,
        Classification,
        Detection,
        Segmentation
    }

    /// <summary>深度学习推理设备偏好：自动优先 GPU，其次 CPU。</summary>
    public enum DeepLearningDevicePreference
    {
        Auto,
        Cpu,
        Gpu
    }

    /// <summary>
    /// 模型句柄共享范围（§5.4）：Instance（默认 0，每工具实例一份，等同现状）/
    /// Flow（同一流程树根节点实例共享一份）/ Project（整个进程共享一份）。
    /// 追加在既有参数之后，默认值 0 保证旧序列化数据行为不变。
    /// </summary>
    public enum ModelCacheMode
    {
        Instance = 0,
        Flow = 1,
        Project = 2
    }

    /// <summary>统一推理工具第一阶段的公共输出对象，便于后续分类/检测/分割专用工具复用。</summary>
    public sealed class DeepLearningInferenceInfo
    {
        public string RequestedModelKind { get; set; }
        public string ModelKind { get; set; }
        public string RequestedDevice { get; set; }
        public string DeviceUsed { get; set; }
        public string ModelFilePath { get; set; }
        public string ResolvedModelPath { get; set; }
        public int BatchSize { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public bool OptimizedForInference { get; set; }
        public string[] ClassNames { get; set; } = Array.Empty<string>();
        public int[] ClassIds { get; set; } = Array.Empty<int>();
        public string ModelSummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// 深度学习统一推理工具（第一阶段公共框架）：
    /// 负责加载 .hdl、选择运行设备、探测模型参数并完成推理前预热。
    /// HALCON 官方工作流里的 DLSample / preprocess 辅助过程并非 .NET 内建算子，
    /// 因此第一阶段先把公共运行时稳定落地，后续分类/检测/分割专用工具在此基础上接具体推理流程。
    /// </summary>
    [ToolOutput("Ready", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("ModelType", VariableKind.Single, VariableType.String)]
    [ToolOutput("ResolvedModelPath", VariableKind.Single, VariableType.String)]
    [ToolOutput("DeviceUsed", VariableKind.Single, VariableType.String)]
    [ToolOutput("ClassNames", VariableKind.Array, VariableType.String)]
    [ToolOutput("ClassIds", VariableKind.Array, VariableType.Int)]
    [ToolOutput("ClassCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ImageWidth", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ImageHeight", VariableKind.Single, VariableType.Int)]
    [ToolOutput("OptimizedForInference", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("ModelSummary", VariableKind.Single, VariableType.String)]
    [ToolOutput("Info", VariableKind.Object, VariableType.Object, ElementClrType = typeof(DeepLearningInferenceInfo))]
    public sealed class DeepLearningInferenceTool : ToolBase, IToolResourceLifecycle, IToolConfigurationCheck
    {
        private sealed class CachedModel
        {
            public object Key { get; init; }

            public HTuple Handle { get; init; }

            public string ResolvedPath { get; init; }

            public string ModelType { get; init; }

            public string DeviceUsed { get; init; }

            public int ImageWidth { get; init; }

            public int ImageHeight { get; init; }

            public string[] ClassNames { get; init; } = Array.Empty<string>();

            public int[] ClassIds { get; init; } = Array.Empty<int>();

            public string ModelSummary { get; init; } = string.Empty;
        }

        /// <summary>.hdl 模型文件路径；支持环境变量、绝对路径、相对当前工作目录或应用目录的相对路径。</summary>
        public string ModelFilePath { get; set; }

        public DeepLearningModelKind ModelKind { get; set; } = DeepLearningModelKind.Auto;

        public DeepLearningDevicePreference Device { get; set; } = DeepLearningDevicePreference.Auto;

        /// <summary>统一设置模型 batch_size（默认 1，用于推理）。</summary>
        public int BatchSize { get; set; } = 1;

        /// <summary>加载后设置 optimize_for_inference=true，以降低推理态内存占用。</summary>
        public bool OptimizeForInference { get; set; } = true;

        /// <summary>
        /// 模型句柄共享范围（§5.4）：默认 Instance 与现状逐一致；
        /// Flow 需要入口能确定流程根锚点（FlowEngine.Run / Prepare(FlowNode root)），
        /// 快照预热入口自动回退 Instance 并写日志。
        /// </summary>
        public ModelCacheMode ModelCacheMode { get; set; } = ModelCacheMode.Instance;

        private readonly object _sync = new object();
        private CachedModel _cached;
        private DlModelCacheEntry _poolEntry;

        public DeepLearningInferenceTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            DeepLearningInferenceInfo info;
            try
            {
                lock (_sync)
                {
                    CachedModel model = EnsureCachedModel(ctx);
                    info = new DeepLearningInferenceInfo
                    {
                        RequestedModelKind = ModelKind.ToString(),
                        ModelKind = model.ModelType,
                        RequestedDevice = Device.ToString(),
                        DeviceUsed = model.DeviceUsed,
                        ModelFilePath = ModelFilePath ?? string.Empty,
                        ResolvedModelPath = model.ResolvedPath,
                        BatchSize = BatchSize,
                        ImageWidth = model.ImageWidth,
                        ImageHeight = model.ImageHeight,
                        OptimizedForInference = OptimizeForInference,
                        ClassNames = model.ClassNames,
                        ClassIds = model.ClassIds,
                        ModelSummary = model.ModelSummary
                    };
                }
            }
            catch (InvalidOperationException ex)
            {
                return NodeResult.Fail(ex.Message);
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 深度学习推理失败：{ex.Message}");
            }

            WriteOutputs(ctx, info);
            ctx.AddLog(FlowLogLevel.Info,
                $"[深度学习推理] 模型已就绪 type={info.ModelKind} device={info.DeviceUsed} class={info.ClassNames.Length}");
            return NodeResult.Ok;
        }

        private CachedModel EnsureCachedModel(FlowContext ctx)
        {
            string resolvedPath = ResolveExistingModelPath(ModelFilePath);
            if (ModelCacheMode != ModelCacheMode.Instance)
            {
                // 共享模式（Flow / Project，§5.4）：锚点缺失时 EnsureSharedCachedModel 返回 null 回退 Instance。
                CachedModel shared = EnsureSharedCachedModel(ctx, resolvedPath);
                if (shared != null)
                {
                    return shared;
                }
            }
            return EnsureInstanceCachedModel(resolvedPath);
        }

        private CachedModel EnsureSharedCachedModel(FlowContext ctx, string resolvedPath)
        {
            object anchor = DlModelCachePolicy.ResolveAnchor(ctx);
            if (!DlModelCachePolicy.TryResolveSharedScope(ModelCacheMode, anchor, out object scopeToken))
            {
                // Flow 模式但无法确定流程根锚点（Prepare(IEnumerable<ToolNode>) 快照预热入口）。
                // 退化发生在 Prepare 阶段、无 FlowContext 可写日志，按评审结论简单跳过该工具的
                // 共享加载并回退 Instance，日志推迟到工具下一次 Run 时补记。
                if (ctx != null)
                {
                    ctx.AddLog(FlowLogLevel.Warning,
                        $"[深度学习推理] {ModuleName} ModelCacheMode=Flow 但本次入口无流程根锚点（可能经快照预热），已回退为 Instance 模式加载");
                }
                return null;
            }

            DlModelPoolKey key = DlModelCachePolicy.BuildKey(scopeToken, resolvedPath,
                File.GetLastWriteTimeUtc(resolvedPath).Ticks, ModelKind, Device, BatchSize, OptimizeForInference);
            if (_poolEntry != null && _poolEntry.Key.Equals(key) && SharedDlModelCache.IsUsable(_poolEntry))
            {
                // 引用计数按"实例持有"：键不变复用已持有条目，不重复 Acquire。
                return _cached;
            }

            // 键变化（或已失效）：先归还旧键引用（或旧实例句柄），再 Acquire 新键。
            ReleaseResources();
            DlModelCacheEntry entry = SharedDlModelCache.Acquire(key, () => LoadSharedEntry(key));
            _poolEntry = entry;
            _cached = new CachedModel
            {
                Key = key,
                Handle = entry.Handle,
                ResolvedPath = entry.ResolvedPath,
                ModelType = entry.ModelType,
                DeviceUsed = entry.DeviceUsed,
                ImageWidth = entry.ImageWidth,
                ImageHeight = entry.ImageHeight,
                ClassNames = entry.ClassNames,
                ClassIds = entry.ClassIds,
                ModelSummary = entry.ModelSummary
            };
            return _cached;
        }

        private DlModelCacheEntry LoadSharedEntry(DlModelPoolKey key)
        {
            HTuple modelHandle = null;
            try
            {
                HOperatorSet.ReadDlModel(key.ResolvedPath, out modelHandle);
                string actualType = NormalizeModelType(ReadStringParam(modelHandle, "type", "generic"));
                if (ModelKind != DeepLearningModelKind.Auto && !string.Equals(actualType, NormalizeModelType(ModelKind.ToString()), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{ModuleName} 深度学习模型类型不匹配：配置为 {ModelKind}，实际模型为 {actualType}");
                }

                if (BatchSize > 0)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "batch_size", BatchSize);
                }
                if (OptimizeForInference)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "optimize_for_inference", "true");
                }

                string deviceUsed = ConfigureDevice(modelHandle, Device);
                HTuple handle = modelHandle;
                return new DlModelCacheEntry
                {
                    Key = key,
                    Handle = modelHandle,
                    ResolvedPath = key.ResolvedPath,
                    ModelType = actualType,
                    DeviceUsed = deviceUsed,
                    ImageWidth = ReadIntParam(modelHandle, "image_width", fallback: 0),
                    ImageHeight = ReadIntParam(modelHandle, "image_height", fallback: 0),
                    ClassNames = ReadStringArrayParam(modelHandle, "class_names"),
                    ClassIds = ReadIntArrayParam(modelHandle, "class_ids"),
                    ModelSummary = ReadStringParam(modelHandle, "summary", string.Empty),
                    ClearAction = () => TryClearDlModel(handle)
                };
            }
            catch
            {
                if (modelHandle != null)
                {
                    TryClearDlModel(modelHandle);
                }
                throw;
            }
        }

        private CachedModel EnsureInstanceCachedModel(string resolvedPath)
        {
            object key = (resolvedPath.ToUpperInvariant(), File.GetLastWriteTimeUtc(resolvedPath).Ticks, ModelKind, Device, BatchSize, OptimizeForInference);
            if (_cached != null && Equals(_cached.Key, key))
            {
                return _cached;
            }

            ReleaseResources();
            HTuple modelHandle = null;
            try
            {
                HOperatorSet.ReadDlModel(resolvedPath, out modelHandle);
                string actualType = NormalizeModelType(ReadStringParam(modelHandle, "type", "generic"));
                if (ModelKind != DeepLearningModelKind.Auto && !string.Equals(actualType, NormalizeModelType(ModelKind.ToString()), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{ModuleName} 深度学习模型类型不匹配：配置为 {ModelKind}，实际模型为 {actualType}");
                }

                if (BatchSize > 0)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "batch_size", BatchSize);
                }
                if (OptimizeForInference)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "optimize_for_inference", "true");
                }

                string deviceUsed = ConfigureDevice(modelHandle, Device);
                var cached = new CachedModel
                {
                    Key = key,
                    Handle = modelHandle,
                    ResolvedPath = resolvedPath,
                    ModelType = actualType,
                    DeviceUsed = deviceUsed,
                    ImageWidth = ReadIntParam(modelHandle, "image_width", fallback: 0),
                    ImageHeight = ReadIntParam(modelHandle, "image_height", fallback: 0),
                    ClassNames = ReadStringArrayParam(modelHandle, "class_names"),
                    ClassIds = ReadIntArrayParam(modelHandle, "class_ids"),
                    ModelSummary = ReadStringParam(modelHandle, "summary", string.Empty)
                };
                _cached = cached;
                return cached;
            }
            catch
            {
                if (modelHandle != null)
                {
                    TryClearDlModel(modelHandle);
                }
                throw;
            }
        }

        private static string ConfigureDevice(HTuple modelHandle, DeepLearningDevicePreference preference)
        {
            if (preference == DeepLearningDevicePreference.Cpu)
            {
                return SetModelDevice(modelHandle, "cpu");
            }
            if (preference == DeepLearningDevicePreference.Gpu)
            {
                return SetModelDevice(modelHandle, "gpu");
            }

            try
            {
                return SetModelDevice(modelHandle, "gpu");
            }
            catch (InvalidOperationException)
            {
                return SetModelDevice(modelHandle, "cpu");
            }
        }

        private static string SetModelDevice(HTuple modelHandle, string deviceType)
        {
            HTuple queryKinds = new HTuple("runtime");
            HTuple queryTypes = new HTuple(deviceType);
            HOperatorSet.QueryAvailableDlDevices(queryKinds, queryTypes, out HTuple devices);
            if (devices == null || devices.Length <= 0)
            {
                throw new InvalidOperationException($"未找到可用的深度学习 {deviceType.ToUpperInvariant()} 运行设备");
            }

            HTuple selected = devices[0];
            HOperatorSet.SetDlModelParam(modelHandle, "device", selected);
            HOperatorSet.GetDlDeviceParam(selected, "type", out HTuple actualType);
            return actualType.Length > 0 ? actualType[0].S : deviceType;
        }

        private static string ResolveExistingModelPath(string path)
        {
            foreach (string candidate in EnumerateModelPathCandidates(path))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException($"深度学习模型文件不存在：{path}");
        }

        private static IEnumerable<string> EnumerateModelPathCandidates(string path)
        {
            string text = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim().Trim('"'));
            if (text.Length == 0)
            {
                yield break;
            }

            if (Path.IsPathRooted(text))
            {
                yield return Path.GetFullPath(text);
                yield break;
            }

            yield return Path.GetFullPath(text, Environment.CurrentDirectory);
            yield return Path.GetFullPath(text, AppContext.BaseDirectory);
        }

        private static string NormalizeModelType(string modelType)
        {
            string text = (modelType ?? string.Empty).Trim();
            if (text.Equals("classification", StringComparison.OrdinalIgnoreCase))
            {
                return "classification";
            }
            if (text.Equals("detection", StringComparison.OrdinalIgnoreCase) || text.Equals("object_detection", StringComparison.OrdinalIgnoreCase))
            {
                return "detection";
            }
            if (text.Equals("segmentation", StringComparison.OrdinalIgnoreCase))
            {
                return "segmentation";
            }
            return text.Length == 0 ? "generic" : text;
        }

        private static string ReadStringParam(HTuple modelHandle, string key, string fallback)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                return value.Length > 0 ? value[0].S : fallback;
            }
            catch (HalconException)
            {
                return fallback;
            }
        }

        private static int ReadIntParam(HTuple modelHandle, string key, int fallback)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                return value.Length > 0 ? value[0].I : fallback;
            }
            catch (HalconException)
            {
                return fallback;
            }
        }

        private static string[] ReadStringArrayParam(HTuple modelHandle, string key)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                return TupleToStrings(value);
            }
            catch (HalconException)
            {
                return Array.Empty<string>();
            }
        }

        private static int[] ReadIntArrayParam(HTuple modelHandle, string key)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                if (value == null || value.Length == 0)
                {
                    return Array.Empty<int>();
                }

                var result = new int[value.Length];
                for (int i = 0; i < value.Length; i++)
                {
                    result[i] = value[i].I;
                }
                return result;
            }
            catch (HalconException)
            {
                return Array.Empty<int>();
            }
        }

        private static string[] TupleToStrings(HTuple tuple)
        {
            if (tuple == null || tuple.Length == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[tuple.Length];
            for (int i = 0; i < tuple.Length; i++)
            {
                result[i] = tuple[i].S ?? string.Empty;
            }
            return result;
        }

        private static void TryClearDlModel(HTuple modelHandle)
        {
            try
            {
                HOperatorSet.ClearDlModel(modelHandle);
            }
            catch (HalconException)
            {
            }
        }

        private void WriteOutputs(FlowContext ctx, DeepLearningInferenceInfo info)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "Ready", VariableType.Bool, true));
            SetOutput(ctx, Variable.Single(ModuleName, "ModelType", VariableType.String, info.ModelKind ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "ResolvedModelPath", VariableType.String, info.ResolvedModelPath ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "DeviceUsed", VariableType.String, info.DeviceUsed ?? string.Empty));
            SetOutput(ctx, Variable.Array(ModuleName, "ClassNames", VariableType.String, info.ClassNames ?? Array.Empty<string>()));
            SetOutput(ctx, Variable.Array(ModuleName, "ClassIds", VariableType.Int, info.ClassIds ?? Array.Empty<int>()));
            SetOutput(ctx, Variable.Single(ModuleName, "ClassCount", VariableType.Int, info.ClassNames?.Length ?? 0));
            SetOutput(ctx, Variable.Single(ModuleName, "ImageWidth", VariableType.Int, info.ImageWidth));
            SetOutput(ctx, Variable.Single(ModuleName, "ImageHeight", VariableType.Int, info.ImageHeight));
            SetOutput(ctx, Variable.Single(ModuleName, "OptimizedForInference", VariableType.Bool, info.OptimizedForInference));
            SetOutput(ctx, Variable.Single(ModuleName, "ModelSummary", VariableType.String, info.ModelSummary ?? string.Empty));
            SetOutput(ctx, Variable.Object(ModuleName, "Info", info, 1));
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (string.IsNullOrWhiteSpace(ModelFilePath))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "请先指定 .hdl 模型文件");
                yield break;
            }

            string expanded = Environment.ExpandEnvironmentVariables(ModelFilePath.Trim());
            if (Path.GetExtension(expanded).Length > 0 && !string.Equals(Path.GetExtension(expanded), ".hdl", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "模型文件应为 .hdl");
            }
            if (BatchSize <= 0)
            {
                yield return new ToolConfigurationIssue(nameof(BatchSize), "BatchSize 必须大于 0");
            }

            string path = EnumerateModelPathCandidates(ModelFilePath).FirstOrDefault(File.Exists);
            if (path == null)
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "模型文件不存在");
            }
        }

        public void Prepare()
        {
            lock (_sync)
            {
                EnsureCachedModel(null);
            }
        }

        public void ReleaseResources()
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
                    TryClearDlModel(_cached.Handle);
                    _cached = null;
                }
            }
        }
    }

    /// <summary>单个检出目标的只读结果对象（DlDetectTool.Boxes 的元素类型）。</summary>
    public sealed class DlDetectBox
    {
        public double Row1 { get; }
        public double Col1 { get; }
        public double Row2 { get; }
        public double Col2 { get; }
        public int ClassId { get; }
        public string ClassName { get; }
        public double Score { get; }

        public DlDetectBox(double row1, double col1, double row2, double col2, int classId, string className, double score)
        {
            Row1 = row1;
            Col1 = col1;
            Row2 = row2;
            Col2 = col2;
            ClassId = classId;
            ClassName = className ?? string.Empty;
            Score = score;
        }
    }

    /// <summary>
    /// 深度学习检测工具（DL-02，§7 / §14）：对整图或 ROI 内目标输出位置、类别和分数。
    /// 模型固定为 detection 类型（不匹配时报中文错误）；公共输出按 §5.3，专用输出按 §7。
    /// 说明：本工具输出变量 ClassNames / ClassIds 取 §7 专用语义（每个检出目标的类别），
    /// 与 Boxes / Scores 一一对应；模型类别表经 ClassCount 与 Info.ClassNames / Info.ClassIds 提供。
    /// ROI 策略（§14.3）：reduce_domain 对深度学习推理无效，必须先 reduce_domain + crop_domain
    /// 裁剪后推理，框坐标按（原图宽/网络宽、原图高/网络高）缩放后加裁剪原点偏移回原图。
    /// </summary>
    [ToolOutput("Ready", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("ModelType", VariableKind.Single, VariableType.String)]
    [ToolOutput("ResolvedModelPath", VariableKind.Single, VariableType.String)]
    [ToolOutput("DeviceUsed", VariableKind.Single, VariableType.String)]
    [ToolOutput("ClassNames", VariableKind.Array, VariableType.String)]
    [ToolOutput("ClassIds", VariableKind.Array, VariableType.Int)]
    [ToolOutput("ClassCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ImageWidth", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ImageHeight", VariableKind.Single, VariableType.Int)]
    [ToolOutput("OptimizedForInference", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("ModelSummary", VariableKind.Single, VariableType.String)]
    [ToolOutput("Info", VariableKind.Object, VariableType.Object, ElementClrType = typeof(DeepLearningInferenceInfo))]
    [ToolOutput("Boxes", VariableKind.Array, VariableType.Object, ElementClrType = typeof(DlDetectBox))]
    [ToolOutput("Regions", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Contours", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("BestClassName", VariableKind.Single, VariableType.String)]
    [ToolOutput("BestScore", VariableKind.Single, VariableType.Double)]
    public sealed class DlDetectTool : ToolBase, INotFoundPolicy, IToolResourceLifecycle, IToolConfigurationCheck
    {
        private sealed class LoadedModel
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

        private sealed class CachedModel
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

        private sealed class DetectionResult
        {
            public List<DlDetectBox> Boxes { get; init; }
            public HObject Regions { get; init; }
            public HObject Contours { get; init; }
            public int Count => Boxes.Count;
        }

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("ROI区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        /// <summary>.hdl 检测模型文件路径；支持环境变量、绝对路径、相对当前工作目录或应用目录的相对路径。</summary>
        public string ModelFilePath { get; set; }

        public DeepLearningDevicePreference Device { get; set; } = DeepLearningDevicePreference.Auto;

        /// <summary>统一设置模型 batch_size（默认 1，用于推理）。</summary>
        public int BatchSize { get; set; } = 1;

        /// <summary>结果最小置信度（0~1，默认 0.5）：低于该分数的检出目标直接丢弃。</summary>
        public double MinScore { get; set; } = 0.5;

        /// <summary>最多输出的检测数量：0 表示全部；大于 0 时按置信度（结果已降序）截断。</summary>
        public int MaxDetections { get; set; }

        /// <summary>加载后设置 optimize_for_inference=true，以降低推理态内存占用。</summary>
        public bool OptimizeForInference { get; set; } = true;

        /// <summary>
        /// 模型句柄共享范围（§5.4）：默认 Instance 与现状逐一致；
        /// Flow 需要入口能确定流程根锚点（FlowEngine.Run / Prepare(FlowNode root)），
        /// 快照预热入口自动回退 Instance 并写日志。共享句柄上的推理在条目 Gate 内串行（省显存、牺牲并发）。
        /// </summary>
        public ModelCacheMode ModelCacheMode { get; set; } = ModelCacheMode.Instance;

        /// <summary>未检出目标时是否失败（默认 true）；关闭后输出 Found=false、Count=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        private readonly object _sync = new object();
        private CachedModel _cached;
        private DlModelCacheEntry _poolEntry;

        public DlDetectTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject roi = null;
            if (!string.IsNullOrWhiteSpace(RegionPath))
            {
                roi = Input<HalconRegion>(ctx, RegionPath).Object;
            }

            try
            {
                CachedModel model;
                lock (_sync)
                {
                    model = EnsureCachedModel(ctx);
                }

                // ROI（§14.3）：reduce_domain 对 DL 推理无效，先裁剪后推理；
                // 裁剪原点取 ROI 最小外接矩形左上角，推理后框坐标加该偏移回原图。
                HObject runImage = image;
                HObject crop = null;
                double rowOffset = 0;
                double colOffset = 0;
                if (roi != null)
                {
                    HOperatorSet.SmallestRectangle1(roi, out HTuple roiRow1, out HTuple roiCol1, out _, out _);
                    rowOffset = roiRow1.D;
                    colOffset = roiCol1.D;
                    HOperatorSet.ReduceDomain(image, roi, out HObject reduced);
                    try
                    {
                        HOperatorSet.CropDomain(reduced, out crop);
                    }
                    finally
                    {
                        reduced.Dispose();
                    }
                    runImage = crop;
                }

                try
                {
                    HOperatorSet.GetImageSize(runImage, out HTuple imgWidth, out HTuple imgHeight);
                    HOperatorSet.CountChannels(runImage, out HTuple channels);

                    // 共享句柄上的推理在条目 Gate 上串行（§5.4：省显存、牺牲并发）。
                    object gate = model.PoolEntry != null ? model.PoolEntry.Gate : _sync;
                    DetectionResult detection;
                    lock (gate)
                    {
                        detection = Detect(model, runImage, imgWidth.I, imgHeight.I, channels.I, rowOffset, colOffset);
                    }

                    WriteOutputs(ctx, model, detection);
                    if (detection.Count == 0)
                    {
                        return NotFoundOutcome.Resolve(ctx, this, "深度学习检测未找到目标");
                    }
                    ctx.AddLog(FlowLogLevel.Info,
                        $"[深度学习检测] 检出数量={detection.Count} device={model.DeviceUsed} minScore={MinScore}");
                    return NodeResult.Ok;
                }
                finally
                {
                    crop?.Dispose();
                }
            }
            catch (InvalidOperationException ex)
            {
                return NodeResult.Fail(ex.Message);
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 深度学习检测失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 单图检测：预处理（§14.1）→ 样本构造 → apply_dl_model → 坐标缩放回原图 →
        /// MinScore 过滤与 MaxDetections 截断 → 生成区域 / 轮廓。返回的 HObject 所有权移交调用方（最终交给输出）。
        /// </summary>
        private DetectionResult Detect(CachedModel model, HObject runImage, int imgWidth, int imgHeight,
            int channels, double rowOffset, double colOffset)
        {
            if (model.ImageNumChannels > 0 && channels != model.ImageNumChannels)
            {
                throw new InvalidOperationException(
                    $"图像通道数（{channels}）与模型要求（{model.ImageNumChannels}）不符，无法执行深度学习检测");
            }

            HObject preprocessed = null;
            HTuple sample = null;
            HTuple result = null;
            try
            {
                preprocessed = DeepLearningToolShared.PreprocessForDlModel(
                    runImage, model.ImageWidth, model.ImageHeight, model.RangeMin, model.RangeMax);
                sample = DeepLearningToolShared.CreateImageSample(preprocessed);
                result = DeepLearningToolShared.ApplyDlModel(model.Handle, sample);

                // §14.2：检测结果为等长数组、已按置信度降序；坐标为预处理后的网络输入图坐标系。
                HTuple rows1 = DeepLearningToolShared.TryGetDictTuple(result, "bbox_row1");
                HTuple cols1 = DeepLearningToolShared.TryGetDictTuple(result, "bbox_col1");
                HTuple rows2 = DeepLearningToolShared.TryGetDictTuple(result, "bbox_row2");
                HTuple cols2 = DeepLearningToolShared.TryGetDictTuple(result, "bbox_col2");
                HTuple classIds = DeepLearningToolShared.TryGetDictTuple(result, "bbox_class_id");
                HTuple classNames = DeepLearningToolShared.TryGetDictTuple(result, "bbox_class_name");
                HTuple confidences = DeepLearningToolShared.TryGetDictTuple(result, "bbox_confidence");

                int raw = Math.Min(rows1.Length, Math.Min(cols1.Length, Math.Min(rows2.Length,
                    Math.Min(cols2.Length, Math.Min(classIds.Length, Math.Min(classNames.Length, confidences.Length))))));

                // 坐标缩放回原图：因子 = 原图宽/网络宽、原图高/网络高；有 ROI 时再加裁剪原点偏移。
                double scaleRow = imgHeight / (double)model.ImageHeight;
                double scaleCol = imgWidth / (double)model.ImageWidth;

                var boxes = new List<DlDetectBox>(raw);
                for (int i = 0; i < raw; i++)
                {
                    double score = confidences[i].D;
                    if (score < MinScore)
                    {
                        continue;
                    }
                    boxes.Add(new DlDetectBox(
                        rows1[i].D * scaleRow + rowOffset,
                        cols1[i].D * scaleCol + colOffset,
                        rows2[i].D * scaleRow + rowOffset,
                        cols2[i].D * scaleCol + colOffset,
                        classIds[i].I,
                        classNames[i].S,
                        score));
                }
                if (MaxDetections > 0 && boxes.Count > MaxDetections)
                {
                    // 结果已按置信度降序（§14.2），直接截断前 N 个。
                    boxes.RemoveRange(MaxDetections, boxes.Count - MaxDetections);
                }

                HObject regions = GenBoxRegions(boxes);
                HObject contours = GenBoxContours(boxes);
                return new DetectionResult { Boxes = boxes, Regions = regions, Contours = contours };
            }
            finally
            {
                preprocessed?.Dispose();
                // 样本 / 结果字典句柄：22.11 的 HalconDotNet 无 clear_dict，交由 HALCON 句柄回收。
            }
        }

        /// <summary>每目标一个矩形区域元组（gen_rectangle1）；0 检出时为空区域。</summary>
        private static HObject GenBoxRegions(List<DlDetectBox> boxes)
        {
            if (boxes.Count == 0)
            {
                HOperatorSet.GenEmptyRegion(out HObject empty);
                return empty;
            }

            var rows1 = new HTuple();
            var cols1 = new HTuple();
            var rows2 = new HTuple();
            var cols2 = new HTuple();
            foreach (DlDetectBox box in boxes)
            {
                rows1 = rows1.TupleConcat((int)Math.Round(box.Row1));
                cols1 = cols1.TupleConcat((int)Math.Round(box.Col1));
                rows2 = rows2.TupleConcat((int)Math.Round(box.Row2));
                cols2 = cols2.TupleConcat((int)Math.Round(box.Col2));
            }
            HOperatorSet.GenRectangle1(out HObject regions, rows1, cols1, rows2, cols2);
            return regions;
        }

        /// <summary>
        /// 每目标一个矩形 XLD 轮廓元组；0 检出时为空 XLD。
        /// 22.11 的 HalconDotNet 无 gen_rectangle1_contour_xld，用 gen_rectangle1 + gen_contour_region_xld 等价生成。
        /// </summary>
        private static HObject GenBoxContours(List<DlDetectBox> boxes)
        {
            if (boxes.Count == 0)
            {
                HOperatorSet.GenEmptyObj(out HObject empty);
                return empty;
            }

            HObject regions = GenBoxRegions(boxes);
            try
            {
                HOperatorSet.GenContourRegionXld(regions, out HObject contours, "center");
                return contours;
            }
            finally
            {
                regions.Dispose();
            }
        }

        // ======================= 模型加载（DlModelCachePolicy 三模式，§5.4） =======================

        private CachedModel EnsureCachedModel(FlowContext ctx)
        {
            string resolvedPath = DeepLearningToolShared.ResolveExistingModelPath(ModelFilePath);
            if (ModelCacheMode != ModelCacheMode.Instance)
            {
                // 共享模式（Flow / Project，§5.4）：锚点缺失时 EnsureSharedCachedModel 返回 null 回退 Instance。
                CachedModel shared = EnsureSharedCachedModel(ctx, resolvedPath);
                if (shared != null)
                {
                    return shared;
                }
            }
            return EnsureInstanceCachedModel(resolvedPath);
        }

        private CachedModel EnsureSharedCachedModel(FlowContext ctx, string resolvedPath)
        {
            object anchor = DlModelCachePolicy.ResolveAnchor(ctx);
            if (!DlModelCachePolicy.TryResolveSharedScope(ModelCacheMode, anchor, out object scopeToken))
            {
                // Flow 模式但无法确定流程根锚点（Prepare(IEnumerable<ToolNode>) 快照预热入口），回退 Instance 并补记日志。
                ctx?.AddLog(FlowLogLevel.Warning,
                    $"[深度学习检测] {ModuleName} ModelCacheMode=Flow 但本次入口无流程根锚点（可能经快照预热），已回退为 Instance 模式加载");
                return null;
            }

            DlModelPoolKey key = DlModelCachePolicy.BuildKey(scopeToken, resolvedPath,
                File.GetLastWriteTimeUtc(resolvedPath).Ticks, DeepLearningModelKind.Detection, Device, BatchSize, OptimizeForInference);
            if (_poolEntry != null && _poolEntry.Key.Equals(key) && SharedDlModelCache.IsUsable(_poolEntry))
            {
                // 引用计数按"实例持有"：键不变复用已持有条目，不重复 Acquire。
                return _cached;
            }

            // 键变化（或已失效）：先归还旧键引用（或旧实例句柄），再 Acquire 新键。
            ReleaseResources();
            DlModelCacheEntry entry = SharedDlModelCache.Acquire(key, () => LoadSharedEntry(key));
            _poolEntry = entry;
            _cached = ToCachedModel(entry, key);
            return _cached;
        }

        private DlModelCacheEntry LoadSharedEntry(DlModelPoolKey key)
        {
            HTuple modelHandle = null;
            try
            {
                LoadedModel loaded = LoadModel(key.ResolvedPath);
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

        private CachedModel EnsureInstanceCachedModel(string resolvedPath)
        {
            object key = (resolvedPath.ToUpperInvariant(), File.GetLastWriteTimeUtc(resolvedPath).Ticks,
                DeepLearningModelKind.Detection, Device, BatchSize, OptimizeForInference);
            if (_cached != null && Equals(_cached.Key, key))
            {
                return _cached;
            }

            ReleaseResources();
            LoadedModel loaded = LoadModel(resolvedPath);
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

        /// <summary>读取检测模型并校验类型（固定 detection，不匹配抛中文错误）、设置 batch/optimize 与设备。</summary>
        private LoadedModel LoadModel(string resolvedPath)
        {
            HOperatorSet.ReadDlModel(resolvedPath, out HTuple modelHandle);
            try
            {
                string actualType = DeepLearningToolShared.NormalizeModelType(
                    DeepLearningToolShared.ReadStringParam(modelHandle, "type", "generic"));
                if (!string.Equals(actualType, "detection", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{ModuleName} 深度学习模型类型不匹配：检测工具需要 detection 类型模型，实际模型为 {actualType}");
                }

                if (BatchSize > 0)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "batch_size", BatchSize);
                }
                if (OptimizeForInference)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "optimize_for_inference", "true");
                }

                string deviceUsed = DeepLearningToolShared.ConfigureDevice(modelHandle, Device);
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

        private CachedModel ToCachedModel(DlModelCacheEntry entry, object key)
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

        private CachedModel ToCachedModel(LoadedModel loaded, object key, string resolvedPath)
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

        // ======================= 输出 =======================

        private void WriteOutputs(FlowContext ctx, CachedModel model, DetectionResult detection)
        {
            var boxes = detection.Boxes;
            var classNames = new string[boxes.Count];
            var classIds = new int[boxes.Count];
            var scores = new double[boxes.Count];
            for (int i = 0; i < boxes.Count; i++)
            {
                classNames[i] = boxes[i].ClassName;
                classIds[i] = boxes[i].ClassId;
                scores[i] = boxes[i].Score;
            }

            SetOutput(ctx, Variable.Single(ModuleName, "Ready", VariableType.Bool, true));
            SetOutput(ctx, Variable.Single(ModuleName, "ModelType", VariableType.String, model.ModelType ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "ResolvedModelPath", VariableType.String, model.ResolvedPath ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "DeviceUsed", VariableType.String, model.DeviceUsed ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "ClassCount", VariableType.Int, model.ClassNames?.Length ?? 0));
            SetOutput(ctx, Variable.Single(ModuleName, "ImageWidth", VariableType.Int, model.ImageWidth));
            SetOutput(ctx, Variable.Single(ModuleName, "ImageHeight", VariableType.Int, model.ImageHeight));
            SetOutput(ctx, Variable.Single(ModuleName, "OptimizedForInference", VariableType.Bool, OptimizeForInference));
            SetOutput(ctx, Variable.Single(ModuleName, "ModelSummary", VariableType.String, model.ModelSummary ?? string.Empty));
            SetOutput(ctx, Variable.Object(ModuleName, "Info", new DeepLearningInferenceInfo
            {
                RequestedModelKind = DeepLearningModelKind.Detection.ToString(),
                ModelKind = model.ModelType,
                RequestedDevice = Device.ToString(),
                DeviceUsed = model.DeviceUsed,
                ModelFilePath = ModelFilePath ?? string.Empty,
                ResolvedModelPath = model.ResolvedPath,
                BatchSize = BatchSize,
                ImageWidth = model.ImageWidth,
                ImageHeight = model.ImageHeight,
                OptimizedForInference = OptimizeForInference,
                ClassNames = model.ClassNames,
                ClassIds = model.ClassIds,
                ModelSummary = model.ModelSummary
            }, 1));
            // §7 专用输出：ClassNames / ClassIds 取检出目标的类别（与 Boxes / Scores 一一对应）。
            SetOutput(ctx, Variable.Array(ModuleName, "Boxes", VariableType.Object, boxes));
            SetOutput(ctx, Variable.Object(ModuleName, "Regions", new HalconRegion(detection.Regions), boxes.Count));
            SetOutput(ctx, Variable.Object(ModuleName, "Contours", new HalconXld(detection.Contours), boxes.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "ClassNames", VariableType.String, classNames));
            SetOutput(ctx, Variable.Array(ModuleName, "ClassIds", VariableType.Int, classIds));
            SetOutput(ctx, Variable.Array(ModuleName, "Scores", VariableType.Double, scores));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, boxes.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, boxes.Count > 0));
            SetOutput(ctx, Variable.Single(ModuleName, "BestClassName", VariableType.String, boxes.Count > 0 ? boxes[0].ClassName : string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "BestScore", VariableType.Double, boxes.Count > 0 ? boxes[0].Score : 0.0));
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (string.IsNullOrWhiteSpace(ModelFilePath))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "请先指定 .hdl 模型文件");
                yield break;
            }

            string expanded = Environment.ExpandEnvironmentVariables(ModelFilePath.Trim());
            if (Path.GetExtension(expanded).Length > 0 && !string.Equals(Path.GetExtension(expanded), ".hdl", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "模型文件应为 .hdl");
            }
            if (BatchSize <= 0)
            {
                yield return new ToolConfigurationIssue(nameof(BatchSize), "BatchSize 必须大于 0");
            }
            if (MinScore < 0 || MinScore > 1)
            {
                yield return new ToolConfigurationIssue(nameof(MinScore), "MinScore 必须在 0~1 之间");
            }
            if (MaxDetections < 0)
            {
                yield return new ToolConfigurationIssue(nameof(MaxDetections), "MaxDetections 不能小于 0（0 表示全部）");
            }

            string path = DeepLearningToolShared.EnumerateModelPathCandidates(ModelFilePath).FirstOrDefault(File.Exists);
            if (path == null)
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "模型文件不存在");
            }
        }

        public void Prepare()
        {
            lock (_sync)
            {
                EnsureCachedModel(null);
            }
        }

        public void ReleaseResources()
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
    }

    /// <summary>
    /// 深度学习分类工具（DL-01，§6 / §14）：对整图或 ROI 输出类别、分数、TopK 与期望类别比对。
    /// 模型固定为 classification 类型（不匹配时报中文错误）；公共输出按 §5.3，专用输出按 §6。
    /// 结果字典（§14.2）：classification_class_ids / classification_class_names / classification_confidences
    /// 为全部类别、已按置信度降序，TopK 直接取前 K 项；分类结果与坐标无关，因此无检测类的坐标回写问题。
    /// ROI 策略（§14.3）：reduce_domain 对深度学习推理无效，先 reduce_domain + crop_domain 裁剪后推理，
    /// 裁剪只影响分类对象（喂给网络的图像内容），不影响结果结构。
    /// </summary>
    [ToolOutput("Ready", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("ModelType", VariableKind.Single, VariableType.String)]
    [ToolOutput("ResolvedModelPath", VariableKind.Single, VariableType.String)]
    [ToolOutput("DeviceUsed", VariableKind.Single, VariableType.String)]
    [ToolOutput("ClassNames", VariableKind.Array, VariableType.String)]
    [ToolOutput("ClassIds", VariableKind.Array, VariableType.Int)]
    [ToolOutput("ClassCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ImageWidth", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ImageHeight", VariableKind.Single, VariableType.Int)]
    [ToolOutput("OptimizedForInference", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("ModelSummary", VariableKind.Single, VariableType.String)]
    [ToolOutput("Info", VariableKind.Object, VariableType.Object, ElementClrType = typeof(DeepLearningInferenceInfo))]
    [ToolOutput("ClassId", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ClassName", VariableKind.Single, VariableType.String)]
    [ToolOutput("Score", VariableKind.Single, VariableType.Double)]
    [ToolOutput("TopClassIds", VariableKind.Array, VariableType.Int)]
    [ToolOutput("TopClassNames", VariableKind.Array, VariableType.String)]
    [ToolOutput("TopScores", VariableKind.Array, VariableType.Double)]
    [ToolOutput("MatchedExpected", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class DlClassifyTool : ToolBase, INotFoundPolicy, IToolResourceLifecycle, IToolConfigurationCheck
    {
        /// <summary>单个分类候选（TopK 截断与 MinScore 过滤之后的保留项）。</summary>
        private readonly struct Candidate
        {
            public int ClassId { get; }
            public string ClassName { get; }
            public double Score { get; }

            public Candidate(int classId, string className, double score)
            {
                ClassId = classId;
                ClassName = className ?? string.Empty;
                Score = score;
            }
        }

        private sealed class ClassificationResult
        {
            public List<Candidate> Candidates { get; init; }
            public int Count => Candidates.Count;
        }

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("ROI区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        /// <summary>.hdl 分类模型文件路径；支持环境变量、绝对路径、相对当前工作目录或应用目录的相对路径。</summary>
        public string ModelFilePath { get; set; }

        public DeepLearningDevicePreference Device { get; set; } = DeepLearningDevicePreference.Auto;

        /// <summary>统一设置模型 batch_size（默认 1，用于推理）。</summary>
        public int BatchSize { get; set; } = 1;

        /// <summary>结果最小置信度（0~1，默认 0.5）：低于该分数的候选类别直接丢弃。</summary>
        public double MinScore { get; set; } = 0.5;

        /// <summary>最多输出的分类数量（默认 1）：结果已按置信度降序（§14.2），直接取前 K 项；超过实际类别数自动截断，不报错。</summary>
        public int TopK { get; set; } = 1;

        /// <summary>期望类别（可选）：只做比对输出 MatchedExpected（不区分大小写，与 TopK 全部候选比对，数字串按类别 ID 比对），不改变主结果。</summary>
        public string ExpectedClass { get; set; }

        /// <summary>加载后设置 optimize_for_inference=true，以降低推理态内存占用。</summary>
        public bool OptimizeForInference { get; set; } = true;

        /// <summary>
        /// 模型句柄共享范围（§5.4）：默认 Instance 与现状逐一致；
        /// Flow 需要入口能确定流程根锚点（FlowEngine.Run / Prepare(FlowNode root)），
        /// 快照预热入口自动回退 Instance 并写日志。共享句柄上的推理在条目 Gate 内串行（省显存、牺牲并发）。
        /// </summary>
        public ModelCacheMode ModelCacheMode { get; set; } = ModelCacheMode.Instance;

        /// <summary>无有效分类结果（全部被 MinScore 过滤）时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        private sealed class CachedModel
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

        public DlClassifyTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject roi = null;
            if (!string.IsNullOrWhiteSpace(RegionPath))
            {
                roi = Input<HalconRegion>(ctx, RegionPath).Object;
            }

            try
            {
                CachedModel model;
                lock (_sync)
                {
                    model = EnsureCachedModel(ctx);
                }

                // ROI（§14.3）：reduce_domain 对 DL 推理无效，先裁剪后推理；
                // 分类结果与坐标无关，裁剪只改变分类对象（喂给网络的图像内容）。
                HObject runImage = image;
                HObject crop = null;
                if (roi != null)
                {
                    HOperatorSet.ReduceDomain(image, roi, out HObject reduced);
                    try
                    {
                        HOperatorSet.CropDomain(reduced, out crop);
                    }
                    finally
                    {
                        reduced.Dispose();
                    }
                    runImage = crop;
                }

                try
                {
                    HOperatorSet.CountChannels(runImage, out HTuple channels);

                    // 共享句柄上的推理在条目 Gate 上串行（§5.4：省显存、牺牲并发）。
                    object gate = model.PoolEntry != null ? model.PoolEntry.Gate : _sync;
                    ClassificationResult classification;
                    lock (gate)
                    {
                        classification = Classify(model, runImage, channels.I);
                    }

                    bool matched = ResolveMatchedExpected(classification.Candidates);
                    WriteOutputs(ctx, model, classification, matched);
                    if (classification.Count == 0)
                    {
                        return NotFoundOutcome.Resolve(ctx, this, "深度学习分类未找到有效类别（全部被 MinScore 过滤）");
                    }
                    ctx.AddLog(FlowLogLevel.Info,
                        $"[深度学习分类] Top1={classification.Candidates[0].ClassName}({classification.Candidates[0].Score:0.000}) "
                        + $"候选数={classification.Count} device={model.DeviceUsed} minScore={MinScore} topK={TopK}");
                    return NodeResult.Ok;
                }
                finally
                {
                    crop?.Dispose();
                }
            }
            catch (InvalidOperationException ex)
            {
                return NodeResult.Fail(ex.Message);
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 深度学习分类失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 单图分类：预处理（§14.1）→ 样本构造 → apply_dl_model → 取 classification_* 三键 →
        /// MinScore 过滤 → TopK 截断。结果已按置信度降序（§14.2），直接取前 K 项。
        /// </summary>
        private ClassificationResult Classify(CachedModel model, HObject runImage, int channels)
        {
            if (model.ImageNumChannels > 0 && channels != model.ImageNumChannels)
            {
                throw new InvalidOperationException(
                    $"图像通道数（{channels}）与模型要求（{model.ImageNumChannels}）不符，无法执行深度学习分类");
            }

            HObject preprocessed = null;
            HTuple sample = null;
            HTuple result = null;
            try
            {
                preprocessed = DeepLearningToolShared.PreprocessForDlModel(
                    runImage, model.ImageWidth, model.ImageHeight, model.RangeMin, model.RangeMax);
                sample = DeepLearningToolShared.CreateImageSample(preprocessed);
                result = DeepLearningToolShared.ApplyDlModel(model.Handle, sample);

                // §14.2：分类结果为全部类别、已按置信度降序；三键等长，取最短长度对齐。
                HTuple classIds = DeepLearningToolShared.TryGetDictTuple(result, "classification_class_ids");
                HTuple classNames = DeepLearningToolShared.TryGetDictTuple(result, "classification_class_names");
                HTuple confidences = DeepLearningToolShared.TryGetDictTuple(result, "classification_confidences");
                if (classIds.Length == 0 || classNames.Length == 0 || confidences.Length == 0)
                {
                    throw new InvalidOperationException(
                        "深度学习分类失败：结果字典缺少 classification_class_ids / classification_class_names / "
                        + "classification_confidences 键（模型可能不是分类模型或输出结构不符）");
                }

                int raw = Math.Min(classIds.Length, Math.Min(classNames.Length, confidences.Length));
                var candidates = new List<Candidate>(raw);
                for (int i = 0; i < raw; i++)
                {
                    double score = confidences[i].D;
                    if (score < MinScore)
                    {
                        continue;
                    }
                    candidates.Add(new Candidate(classIds[i].I, classNames[i].S, score));
                }
                if (TopK > 0 && candidates.Count > TopK)
                {
                    // 结果已按置信度降序（§14.2），直接截断前 K 个；TopK 超过类别数时自然截断，不报错。
                    candidates.RemoveRange(TopK, candidates.Count - TopK);
                }

                return new ClassificationResult { Candidates = candidates };
            }
            finally
            {
                preprocessed?.Dispose();
                // 样本 / 结果字典句柄：22.11 的 HalconDotNet 无 clear_dict，交由 HALCON 句柄回收。
            }
        }

        /// <summary>
        /// ExpectedClass 比对（§6：只影响 MatchedExpected，不改变主结果）：为空时恒 true；
        /// 否则与 TopK 全部候选比对——类别名不区分大小写，纯数字串再按类别 ID 比对。
        /// </summary>
        private bool ResolveMatchedExpected(List<Candidate> candidates)
        {
            string expected = (ExpectedClass ?? string.Empty).Trim();
            if (expected.Length == 0)
            {
                return true;
            }
            bool numeric = int.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out int expectedId);
            foreach (Candidate candidate in candidates)
            {
                if (string.Equals(candidate.ClassName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (numeric && candidate.ClassId == expectedId)
                {
                    return true;
                }
            }
            return false;
        }

        // ======================= 模型加载（DlModelCachePolicy 三模式，§5.4） =======================

        private CachedModel EnsureCachedModel(FlowContext ctx)
        {
            string resolvedPath = DeepLearningToolShared.ResolveExistingModelPath(ModelFilePath);
            if (ModelCacheMode != ModelCacheMode.Instance)
            {
                // 共享模式（Flow / Project，§5.4）：锚点缺失时 EnsureSharedCachedModel 返回 null 回退 Instance。
                CachedModel shared = EnsureSharedCachedModel(ctx, resolvedPath);
                if (shared != null)
                {
                    return shared;
                }
            }
            return EnsureInstanceCachedModel(resolvedPath);
        }

        private CachedModel EnsureSharedCachedModel(FlowContext ctx, string resolvedPath)
        {
            object anchor = DlModelCachePolicy.ResolveAnchor(ctx);
            if (!DlModelCachePolicy.TryResolveSharedScope(ModelCacheMode, anchor, out object scopeToken))
            {
                // Flow 模式但无法确定流程根锚点（Prepare(IEnumerable<ToolNode>) 快照预热入口），回退 Instance 并补记日志。
                ctx?.AddLog(FlowLogLevel.Warning,
                    $"[深度学习分类] {ModuleName} ModelCacheMode=Flow 但本次入口无流程根锚点（可能经快照预热），已回退为 Instance 模式加载");
                return null;
            }

            DlModelPoolKey key = DlModelCachePolicy.BuildKey(scopeToken, resolvedPath,
                File.GetLastWriteTimeUtc(resolvedPath).Ticks, DeepLearningModelKind.Classification, Device, BatchSize, OptimizeForInference);
            if (_poolEntry != null && _poolEntry.Key.Equals(key) && SharedDlModelCache.IsUsable(_poolEntry))
            {
                // 引用计数按"实例持有"：键不变复用已持有条目，不重复 Acquire。
                return _cached;
            }

            // 键变化（或已失效）：先归还旧键引用（或旧实例句柄），再 Acquire 新键。
            ReleaseResources();
            DlModelCacheEntry entry = SharedDlModelCache.Acquire(key, () => LoadSharedEntry(key));
            _poolEntry = entry;
            _cached = ToCachedModel(entry, key);
            return _cached;
        }

        private DlModelCacheEntry LoadSharedEntry(DlModelPoolKey key)
        {
            HTuple modelHandle = null;
            try
            {
                CachedModel loaded = LoadModel(key.ResolvedPath);
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

        private CachedModel EnsureInstanceCachedModel(string resolvedPath)
        {
            object key = (resolvedPath.ToUpperInvariant(), File.GetLastWriteTimeUtc(resolvedPath).Ticks,
                DeepLearningModelKind.Classification, Device, BatchSize, OptimizeForInference);
            if (_cached != null && Equals(_cached.Key, key))
            {
                return _cached;
            }

            ReleaseResources();
            CachedModel loaded = LoadModel(resolvedPath);
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

        /// <summary>读取分类模型并校验类型（固定 classification，不匹配抛中文错误）、设置 batch/optimize 与设备。</summary>
        private CachedModel LoadModel(string resolvedPath)
        {
            HOperatorSet.ReadDlModel(resolvedPath, out HTuple modelHandle);
            try
            {
                string actualType = DeepLearningToolShared.NormalizeModelType(
                    DeepLearningToolShared.ReadStringParam(modelHandle, "type", "generic"));
                if (!string.Equals(actualType, "classification", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{ModuleName} 深度学习模型类型不匹配：分类工具需要 classification 类型模型，实际模型为 {actualType}");
                }

                if (BatchSize > 0)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "batch_size", BatchSize);
                }
                if (OptimizeForInference)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "optimize_for_inference", "true");
                }

                string deviceUsed = DeepLearningToolShared.ConfigureDevice(modelHandle, Device);
                return new CachedModel
                {
                    Key = null,
                    Handle = modelHandle,
                    ResolvedPath = resolvedPath,
                    ModelType = actualType,
                    DeviceUsed = deviceUsed,
                    ImageWidth = DeepLearningToolShared.ReadIntParam(modelHandle, "image_width", fallback: 0),
                    ImageHeight = DeepLearningToolShared.ReadIntParam(modelHandle, "image_height", fallback: 0),
                    ImageNumChannels = DeepLearningToolShared.ReadIntParam(modelHandle, "image_num_channels", fallback: 0),
                    RangeMin = DeepLearningToolShared.ReadDoubleParam(modelHandle, "image_range_min", fallback: 0),
                    RangeMax = DeepLearningToolShared.ReadDoubleParam(modelHandle, "image_range_max", fallback: 255),
                    ClassNames = DeepLearningToolShared.ReadStringArrayParam(modelHandle, "class_names"),
                    ClassIds = DeepLearningToolShared.ReadIntArrayParam(modelHandle, "class_ids"),
                    ModelSummary = DeepLearningToolShared.ReadStringParam(modelHandle, "summary", string.Empty),
                    PoolEntry = null
                };
            }
            catch
            {
                DeepLearningToolShared.TryClearDlModel(modelHandle);
                throw;
            }
        }

        private CachedModel ToCachedModel(DlModelCacheEntry entry, object key)
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

        private CachedModel ToCachedModel(CachedModel loaded, object key, string resolvedPath)
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

        // ======================= 输出 =======================

        private void WriteOutputs(FlowContext ctx, CachedModel model, ClassificationResult classification, bool matchedExpected)
        {
            List<Candidate> candidates = classification.Candidates;
            var topIds = new int[candidates.Count];
            var topNames = new string[candidates.Count];
            var topScores = new double[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                topIds[i] = candidates[i].ClassId;
                topNames[i] = candidates[i].ClassName;
                topScores[i] = candidates[i].Score;
            }

            SetOutput(ctx, Variable.Single(ModuleName, "Ready", VariableType.Bool, true));
            SetOutput(ctx, Variable.Single(ModuleName, "ModelType", VariableType.String, model.ModelType ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "ResolvedModelPath", VariableType.String, model.ResolvedPath ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "DeviceUsed", VariableType.String, model.DeviceUsed ?? string.Empty));
            // §5.3：ClassNames / ClassIds 取模型类别表（与 ClassCount / Info.ClassNames / Info.ClassIds 一致），
            // 与检测工具"每目标类别"的语义不同——分类的 TopK 类别走 TopClassNames / TopClassIds / ClassName / ClassId。
            SetOutput(ctx, Variable.Array(ModuleName, "ClassNames", VariableType.String, model.ClassNames ?? Array.Empty<string>()));
            SetOutput(ctx, Variable.Array(ModuleName, "ClassIds", VariableType.Int, model.ClassIds ?? Array.Empty<int>()));
            SetOutput(ctx, Variable.Single(ModuleName, "ClassCount", VariableType.Int, model.ClassNames?.Length ?? 0));
            SetOutput(ctx, Variable.Single(ModuleName, "ImageWidth", VariableType.Int, model.ImageWidth));
            SetOutput(ctx, Variable.Single(ModuleName, "ImageHeight", VariableType.Int, model.ImageHeight));
            SetOutput(ctx, Variable.Single(ModuleName, "OptimizedForInference", VariableType.Bool, OptimizeForInference));
            SetOutput(ctx, Variable.Single(ModuleName, "ModelSummary", VariableType.String, model.ModelSummary ?? string.Empty));
            SetOutput(ctx, Variable.Object(ModuleName, "Info", new DeepLearningInferenceInfo
            {
                RequestedModelKind = DeepLearningModelKind.Classification.ToString(),
                ModelKind = model.ModelType,
                RequestedDevice = Device.ToString(),
                DeviceUsed = model.DeviceUsed,
                ModelFilePath = ModelFilePath ?? string.Empty,
                ResolvedModelPath = model.ResolvedPath,
                BatchSize = BatchSize,
                ImageWidth = model.ImageWidth,
                ImageHeight = model.ImageHeight,
                OptimizedForInference = OptimizeForInference,
                ClassNames = model.ClassNames,
                ClassIds = model.ClassIds,
                ModelSummary = model.ModelSummary
            }, 1));
            // §6 专用输出：Top1 平铺 + TopK 数组。
            SetOutput(ctx, Variable.Single(ModuleName, "ClassId", VariableType.Int, candidates.Count > 0 ? candidates[0].ClassId : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "ClassName", VariableType.String, candidates.Count > 0 ? candidates[0].ClassName : string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "Score", VariableType.Double, candidates.Count > 0 ? candidates[0].Score : 0.0));
            SetOutput(ctx, Variable.Array(ModuleName, "TopClassIds", VariableType.Int, topIds));
            SetOutput(ctx, Variable.Array(ModuleName, "TopClassNames", VariableType.String, topNames));
            SetOutput(ctx, Variable.Array(ModuleName, "TopScores", VariableType.Double, topScores));
            SetOutput(ctx, Variable.Single(ModuleName, "MatchedExpected", VariableType.Bool, matchedExpected));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, candidates.Count > 0));
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (string.IsNullOrWhiteSpace(ModelFilePath))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "请先指定 .hdl 模型文件");
                yield break;
            }

            string expanded = Environment.ExpandEnvironmentVariables(ModelFilePath.Trim());
            if (Path.GetExtension(expanded).Length > 0 && !string.Equals(Path.GetExtension(expanded), ".hdl", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "模型文件应为 .hdl");
            }
            if (BatchSize <= 0)
            {
                yield return new ToolConfigurationIssue(nameof(BatchSize), "BatchSize 必须大于 0");
            }
            if (MinScore < 0 || MinScore > 1)
            {
                yield return new ToolConfigurationIssue(nameof(MinScore), "MinScore 必须在 0~1 之间");
            }
            if (TopK < 1)
            {
                yield return new ToolConfigurationIssue(nameof(TopK), "TopK 必须大于等于 1");
            }

            string path = DeepLearningToolShared.EnumerateModelPathCandidates(ModelFilePath).FirstOrDefault(File.Exists);
            if (path == null)
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "模型文件不存在");
            }
        }

        public void Prepare()
        {
            lock (_sync)
            {
                EnsureCachedModel(null);
            }
        }

        public void ReleaseResources()
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
    }

    /// <summary>
    /// 深度学习分割工具（DL-03，§8 / §14）：输出像素级分类结果——每类一个区域（与 ColorSegmentTool 消费方式一致）、
    /// 类别掩膜图、各类面积与拒识区域。模型固定为 segmentation 类型（不匹配时报中文错误）。
    ///
    /// MinScore 语义（实现期定稿，§8"像素过滤还是区域过滤"）：双层过滤——
    /// ① 区域级：某类区域内像素的置信均值（segmentation_confidence 的 intensity 均值）≥ MinScore 才保留该类，
    ///    否则该类输出为空区域；② 像素级：保留类的输出区域 = 类区域 ∩ (confidence ≥ MinScore) 的像素。
    /// RejectedRegion 与 ColorSegmentTool 语义一致：整图定义域 − union1(各类区域)。
    /// 背景类语义（§9.3 定稿）：模型若含背景类（由模型 class_names 决定，工具不特殊判断），
    /// 与其他类一样计入 Regions / Areas / Count；Found = 存在任一非空类区域，全空走 NotFoundOutcome。
    /// ClassFilter（可选 CSV，不区分大小写）：限定参与输出的类别名；被过滤的类不进入 Regions / 数组任何输出。
    /// 结果结构（§14.2）：segmentation_image（像素值 = 类别 ID）与 segmentation_confidence（逐像素置信图）
    /// 均为模型输入尺寸的图像对象，需 zoom_image_size 回原图比例（类别图最近邻、置信图双线性），
    /// 有 ROI 时区域经缩放后再加裁剪原点偏移（§14.3，同 DL-02）。
    /// MaskImage 为原图尺寸 byte 图：按输出区域逐类填入类别 ID（与 Regions 严格一致），无类像素为 0——
    /// 若类别 0 本身是一个类，MaskImage 中 0 像素与该类区域可能重合，消费方应以 Regions 为准。
    /// </summary>
    [ToolOutput("Ready", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("ModelType", VariableKind.Single, VariableType.String)]
    [ToolOutput("ResolvedModelPath", VariableKind.Single, VariableType.String)]
    [ToolOutput("DeviceUsed", VariableKind.Single, VariableType.String)]
    [ToolOutput("ClassNames", VariableKind.Array, VariableType.String)]
    [ToolOutput("ClassIds", VariableKind.Array, VariableType.Int)]
    [ToolOutput("ClassCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ImageWidth", VariableKind.Single, VariableType.Int)]
    [ToolOutput("ImageHeight", VariableKind.Single, VariableType.Int)]
    [ToolOutput("OptimizedForInference", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("ModelSummary", VariableKind.Single, VariableType.String)]
    [ToolOutput("Info", VariableKind.Object, VariableType.Object, ElementClrType = typeof(DeepLearningInferenceInfo))]
    [ToolOutput("MaskImage", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Regions", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Areas", VariableKind.Array, VariableType.Int)]
    [ToolOutput("RejectedRegion", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class DlSegmentTool : ToolBase, INotFoundPolicy, IToolResourceLifecycle, IToolConfigurationCheck
    {
        private sealed class CachedModel
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

        /// <summary>分割输出：所有权已按输出移交（Regions / MaskImage / RejectedRegion 由 HalconRegion / HalconImage 包装后交给流程）。</summary>
        private sealed class SegmentationResult
        {
            public HObject Regions { get; init; }
            public HObject MaskImage { get; init; }
            public HObject RejectedRegion { get; init; }
            public int[] ClassIds { get; init; } = Array.Empty<int>();
            public string[] ClassNames { get; init; } = Array.Empty<string>();
            public int[] Areas { get; init; } = Array.Empty<int>();
            public int Count { get; init; }
        }

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("ROI区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        /// <summary>.hdl 分割模型文件路径；支持环境变量、绝对路径、相对当前工作目录或应用目录的相对路径。</summary>
        public string ModelFilePath { get; set; }

        public DeepLearningDevicePreference Device { get; set; } = DeepLearningDevicePreference.Auto;

        /// <summary>统一设置模型 batch_size（默认 1，用于推理）。</summary>
        public int BatchSize { get; set; } = 1;

        /// <summary>
        /// 像素置信度阈值（0~1，默认 0.5）：区域级——类区域内像素置信均值低于该值的类整类丢弃；
        /// 像素级——保留类的输出区域只含 confidence ≥ MinScore 的像素。语义详见类注释（§8 实现期定稿）。
        /// </summary>
        public double MinScore { get; set; } = 0.5;

        /// <summary>类别过滤（可选，CSV，如 pill,defect）：不区分大小写，限定参与输出的类别名；为空表示全部类别。</summary>
        public string ClassFilter { get; set; }

        /// <summary>加载后设置 optimize_for_inference=true，以降低推理态内存占用。</summary>
        public bool OptimizeForInference { get; set; } = true;

        /// <summary>
        /// 模型句柄共享范围（§5.4）：默认 Instance 与现状逐一致；
        /// Flow 需要入口能确定流程根锚点（FlowEngine.Run / Prepare(FlowNode root)），
        /// 快照预热入口自动回退 Instance 并写日志。共享句柄上的推理在条目 Gate 内串行（省显存、牺牲并发）。
        /// </summary>
        public ModelCacheMode ModelCacheMode { get; set; } = ModelCacheMode.Instance;

        /// <summary>所有类的区域均为空（Found=false）时是否失败（默认 true）；关闭后输出 Found=false、Count=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        private readonly object _sync = new object();
        private CachedModel _cached;
        private DlModelCacheEntry _poolEntry;

        public DlSegmentTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject roi = null;
            if (!string.IsNullOrWhiteSpace(RegionPath))
            {
                roi = Input<HalconRegion>(ctx, RegionPath).Object;
            }

            try
            {
                CachedModel model;
                lock (_sync)
                {
                    model = EnsureCachedModel(ctx);
                }

                // ROI（§14.3）：reduce_domain 对 DL 推理无效，先裁剪后推理；
                // 裁剪原点取 ROI 最小外接矩形左上角，分割区域缩放后加该偏移回原图（同 DL-02）。
                HObject runImage = image;
                HObject crop = null;
                double rowOffset = 0;
                double colOffset = 0;
                if (roi != null)
                {
                    HOperatorSet.SmallestRectangle1(roi, out HTuple roiRow1, out HTuple roiCol1, out _, out _);
                    rowOffset = roiRow1.D;
                    colOffset = roiCol1.D;
                    HOperatorSet.ReduceDomain(image, roi, out HObject reduced);
                    try
                    {
                        HOperatorSet.CropDomain(reduced, out crop);
                    }
                    finally
                    {
                        reduced.Dispose();
                    }
                    runImage = crop;
                }

                try
                {
                    HOperatorSet.GetImageSize(runImage, out HTuple imgWidth, out HTuple imgHeight);
                    HOperatorSet.CountChannels(runImage, out HTuple channels);

                    // 共享句柄上的推理在条目 Gate 上串行（§5.4：省显存、牺牲并发）。
                    object gate = model.PoolEntry != null ? model.PoolEntry.Gate : _sync;
                    SegmentationResult segmentation;
                    lock (gate)
                    {
                        segmentation = Segment(model, runImage, image, imgWidth.I, imgHeight.I, channels.I, rowOffset, colOffset);
                    }

                    WriteOutputs(ctx, model, segmentation);
                    if (segmentation.Count == 0)
                    {
                        return NotFoundOutcome.Resolve(ctx, this, "深度学习分割结果为空：所有类别的区域均为空");
                    }
                    ctx.AddLog(FlowLogLevel.Info,
                        $"[深度学习分割] 类数={segmentation.ClassNames.Length} 非空类={segmentation.Count} "
                        + $"device={model.DeviceUsed} minScore={MinScore}");
                    return NodeResult.Ok;
                }
                finally
                {
                    crop?.Dispose();
                }
            }
            catch (InvalidOperationException ex)
            {
                return NodeResult.Fail(ex.Message);
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 深度学习分割失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 单图分割：预处理（§14.1）→ 样本构造 → apply_dl_model → 取 segmentation_image / segmentation_confidence
        /// （图像对象，§14.2）→ 缩放回原图比例 → 逐类 threshold + MinScore 双层过滤（类注释语义）→
        /// 区域缩放加 ROI 偏移回原图 → MaskImage / RejectedRegion。返回的 HObject 所有权移交调用方（最终交给输出）。
        /// </summary>
        private SegmentationResult Segment(CachedModel model, HObject runImage, HObject fullImage,
            int imgWidth, int imgHeight, int channels, double rowOffset, double colOffset)
        {
            if (model.ImageNumChannels > 0 && channels != model.ImageNumChannels)
            {
                throw new InvalidOperationException(
                    $"图像通道数（{channels}）与模型要求（{model.ImageNumChannels}）不符，无法执行深度学习分割");
            }

            HObject preprocessed = null;
            HTuple sample = null;
            HTuple result = null;
            HObject segNet = null;
            HObject confNet = null;
            HObject segCrop = null;
            HObject confCrop = null;
            HObject segByte = null;
            HObject confPass = null;
            try
            {
                preprocessed = DeepLearningToolShared.PreprocessForDlModel(
                    runImage, model.ImageWidth, model.ImageHeight, model.RangeMin, model.RangeMax);
                sample = DeepLearningToolShared.CreateImageSample(preprocessed);
                result = DeepLearningToolShared.ApplyDlModel(model.Handle, sample);

                // §14.2：两键都是图像对象，用 get_dict_object 取（get_dict_tuple 报 #1302，缺键转中文）。
                segNet = DeepLearningToolShared.GetDictObjectOrThrow(result, "segmentation_image", "深度学习分割");
                confNet = DeepLearningToolShared.GetDictObjectOrThrow(result, "segmentation_confidence", "深度学习分割");

                // 网络输入图 → 裁剪图尺寸：类别图最近邻（保持类别 ID 不变），置信图双线性（阈值语义连续）。
                HOperatorSet.ZoomImageSize(segNet, out segCrop, imgWidth, imgHeight, "nearest_neighbor");
                HOperatorSet.ZoomImageSize(confNet, out confCrop, imgWidth, imgHeight, "bilinear");
                HOperatorSet.ConvertImageType(segCrop, out segByte, "byte");
                // 像素级过滤区域：confidence ≥ MinScore 的像素（类区域与之求交）。
                HOperatorSet.Threshold(confCrop, out confPass, MinScore, 999999.0);

                // 类别表：以 class_names 为准；class_ids 缺失或与类名数不齐时退化为 0..N-1 下标。
                string[] modelNames = model.ClassNames ?? Array.Empty<string>();
                int modelClassCount = modelNames.Length;
                var modelIds = new int[modelClassCount];
                for (int i = 0; i < modelClassCount; i++)
                {
                    modelIds[i] = model.ClassIds != null && model.ClassIds.Length == modelClassCount ? model.ClassIds[i] : i;
                }

                // ClassFilter（可选 CSV，不区分大小写）：限定参与输出的类别；null = 不过滤。
                HashSet<string> nameFilter = ParseClassFilter(ClassFilter);

                // 逐类 threshold(segByte, v, v) 得类区域（§14.2），顺序 = 模型类别序；
                // MinScore 语义：类区域内置信均值 ≥ MinScore 才保留该类，保留类的输出区域 = 类区域 ∩ (confidence ≥ MinScore)；
                // 被丢弃 / 过滤的类记为空区域（gen_empty_region），保持"每类一个区域对象"的元组 arity
                // ——与 ColorSegmentTool 的 Regions 消费方式一致。
                HObject regions = null;
                HObject moved = null;
                HObject domain = null;
                HObject union = null;
                HObject rejected = null;
                HObject mask = null;
                try
                {
                    HOperatorSet.GenEmptyObj(out regions);
                    var outIds = new List<int>(modelClassCount);
                    var outNames = new List<string>(modelClassCount);
                    for (int k = 0; k < modelClassCount; k++)
                    {
                        int id = modelIds[k];
                        string name = modelNames[k];
                        if (nameFilter != null && !nameFilter.Contains(name))
                        {
                            // ClassFilter 过滤：该类不进入 Regions / ClassNames / Areas 等任何输出。
                            continue;
                        }

                        HOperatorSet.Threshold(segByte, out HObject classRegion, id, id);
                        HOperatorSet.AreaCenter(classRegion, out HTuple classArea, out HTuple _, out HTuple _);
                        int rawArea = classArea.Length > 0 ? classArea[0].I : 0;

                        bool kept = rawArea > 0;
                        if (kept)
                        {
                            // 区域级 MinScore：类区域内像素置信均值（segmentation_confidence 的 intensity 均值）。
                            HOperatorSet.Intensity(classRegion, confCrop, out HTuple mean, out HTuple _);
                            kept = mean.Length > 0 && mean[0].D >= MinScore;
                        }

                        HObject outRegion;
                        if (kept)
                        {
                            // 像素级 MinScore：输出区域只保留 confidence ≥ MinScore 的像素。
                            HOperatorSet.Intersection(classRegion, confPass, out outRegion);
                        }
                        else
                        {
                            HOperatorSet.GenEmptyRegion(out outRegion);
                        }
                        classRegion.Dispose();

                        HOperatorSet.ConcatObj(regions, outRegion, out HObject combined);
                        regions.Dispose();
                        regions = combined;
                        outRegion.Dispose();
                        outIds.Add(id);
                        outNames.Add(name);
                    }

                    // 裁剪图坐标 → 原图坐标：类区域取自已缩放回原图比例的 segCrop（见上），
                    // 无需再缩放；有 ROI 时只需加裁剪原点偏移（§14.3，同 DL-02 框坐标回写）。
                    HOperatorSet.CountObj(regions, out HTuple regionCount);
                    if (regionCount.I > 0)
                    {
                        if (rowOffset != 0 || colOffset != 0)
                        {
                            HOperatorSet.MoveRegion(regions, out moved, rowOffset, colOffset);
                        }
                        else
                        {
                            moved = regions.CopyObj(1, -1);
                        }
                    }
                    else
                    {
                        HOperatorSet.GenEmptyObj(out moved);
                    }

                    // 面积与非空类数：顺序与 Regions 元组一一对应。
                    HOperatorSet.AreaCenter(moved, out HTuple outAreas, out HTuple _, out HTuple _);
                    var areas = new int[outAreas.Length];
                    int nonEmpty = 0;
                    for (int i = 0; i < outAreas.Length; i++)
                    {
                        areas[i] = outAreas[i].I;
                        if (areas[i] > 0)
                        {
                            nonEmpty++;
                        }
                    }

                    // RejectedRegion（与 ColorSegmentTool 语义一致）：整图定义域 − union1(各类区域)。
                    HOperatorSet.GetDomain(fullImage, out domain);
                    if (regionCount.I > 0)
                    {
                        HOperatorSet.Union1(moved, out union);
                        HOperatorSet.Difference(domain, union, out rejected);
                    }
                    else
                    {
                        rejected = domain.CopyObj(1, -1);
                    }

                    // MaskImage：原图尺寸 byte 图，按输出区域逐类填入类别 ID（与 Regions 严格一致）；
                    // 无类像素为 0（若类别 0 本身是一个类，消费方应以 Regions 为准，见类注释）。
                    HOperatorSet.GetImageSize(fullImage, out HTuple fullW, out HTuple fullH);
                    HOperatorSet.GenImageConst(out mask, "byte", fullW.I, fullH.I);
                    for (int i = 0; i < areas.Length; i++)
                    {
                        if (areas[i] <= 0)
                        {
                            continue;
                        }
                        HOperatorSet.SelectObj(moved, out HObject single, i + 1);
                        HOperatorSet.PaintRegion(single, mask, out HObject painted, outIds[i], "fill");
                        single.Dispose();
                        mask.Dispose();
                        mask = painted;
                    }

                    SegmentationResult segmentation = new SegmentationResult
                    {
                        Regions = moved,
                        MaskImage = mask,
                        RejectedRegion = rejected,
                        ClassIds = outIds.ToArray(),
                        ClassNames = outNames.ToArray(),
                        Areas = areas,
                        Count = nonEmpty
                    };
                    moved = null;
                    mask = null;
                    rejected = null;
                    return segmentation;
                }
                finally
                {
                    regions?.Dispose();
                    moved?.Dispose();
                    domain?.Dispose();
                    union?.Dispose();
                    rejected?.Dispose();
                    mask?.Dispose();
                }
            }
            finally
            {
                preprocessed?.Dispose();
                // 样本 / 结果字典句柄：22.11 的 HalconDotNet 无 clear_dict，交由 HALCON 句柄回收。
                segNet?.Dispose();
                confNet?.Dispose();
                segCrop?.Dispose();
                confCrop?.Dispose();
                segByte?.Dispose();
                confPass?.Dispose();
            }
        }

        /// <summary>解析 ClassFilter：空文本返回 null（不过滤）；否则返回去掉空项后的不区分大小写名称集合。</summary>
        private static HashSet<string> ParseClassFilter(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in text.Split(','))
            {
                string name = part.Trim();
                if (name.Length > 0)
                {
                    set.Add(name);
                }
            }
            return set.Count > 0 ? set : null;
        }

        // ======================= 模型加载（DlModelCachePolicy 三模式，§5.4） =======================

        private CachedModel EnsureCachedModel(FlowContext ctx)
        {
            string resolvedPath = DeepLearningToolShared.ResolveExistingModelPath(ModelFilePath);
            if (ModelCacheMode != ModelCacheMode.Instance)
            {
                // 共享模式（Flow / Project，§5.4）：锚点缺失时 EnsureSharedCachedModel 返回 null 回退 Instance。
                CachedModel shared = EnsureSharedCachedModel(ctx, resolvedPath);
                if (shared != null)
                {
                    return shared;
                }
            }
            return EnsureInstanceCachedModel(resolvedPath);
        }

        private CachedModel EnsureSharedCachedModel(FlowContext ctx, string resolvedPath)
        {
            object anchor = DlModelCachePolicy.ResolveAnchor(ctx);
            if (!DlModelCachePolicy.TryResolveSharedScope(ModelCacheMode, anchor, out object scopeToken))
            {
                // Flow 模式但无法确定流程根锚点（Prepare(IEnumerable<ToolNode>) 快照预热入口），回退 Instance 并补记日志。
                ctx?.AddLog(FlowLogLevel.Warning,
                    $"[深度学习分割] {ModuleName} ModelCacheMode=Flow 但本次入口无流程根锚点（可能经快照预热），已回退为 Instance 模式加载");
                return null;
            }

            DlModelPoolKey key = DlModelCachePolicy.BuildKey(scopeToken, resolvedPath,
                File.GetLastWriteTimeUtc(resolvedPath).Ticks, DeepLearningModelKind.Segmentation, Device, BatchSize, OptimizeForInference);
            if (_poolEntry != null && _poolEntry.Key.Equals(key) && SharedDlModelCache.IsUsable(_poolEntry))
            {
                // 引用计数按"实例持有"：键不变复用已持有条目，不重复 Acquire。
                return _cached;
            }

            // 键变化（或已失效）：先归还旧键引用（或旧实例句柄），再 Acquire 新键。
            ReleaseResources();
            DlModelCacheEntry entry = SharedDlModelCache.Acquire(key, () => LoadSharedEntry(key));
            _poolEntry = entry;
            _cached = ToCachedModel(entry, key);
            return _cached;
        }

        private DlModelCacheEntry LoadSharedEntry(DlModelPoolKey key)
        {
            HTuple modelHandle = null;
            try
            {
                CachedModel loaded = LoadModel(key.ResolvedPath);
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

        private CachedModel EnsureInstanceCachedModel(string resolvedPath)
        {
            object key = (resolvedPath.ToUpperInvariant(), File.GetLastWriteTimeUtc(resolvedPath).Ticks,
                DeepLearningModelKind.Segmentation, Device, BatchSize, OptimizeForInference);
            if (_cached != null && Equals(_cached.Key, key))
            {
                return _cached;
            }

            ReleaseResources();
            CachedModel loaded = LoadModel(resolvedPath);
            _cached = ToCachedModel(loaded, key, resolvedPath);
            return _cached;
        }

        /// <summary>读取分割模型并校验类型（固定 segmentation，不匹配抛中文错误）、设置 batch/optimize 与设备。</summary>
        private CachedModel LoadModel(string resolvedPath)
        {
            HOperatorSet.ReadDlModel(resolvedPath, out HTuple modelHandle);
            try
            {
                string actualType = DeepLearningToolShared.NormalizeModelType(
                    DeepLearningToolShared.ReadStringParam(modelHandle, "type", "generic"));
                if (!string.Equals(actualType, "segmentation", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{ModuleName} 深度学习模型类型不匹配：分割工具需要 segmentation 类型模型，实际模型为 {actualType}");
                }

                if (BatchSize > 0)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "batch_size", BatchSize);
                }
                if (OptimizeForInference)
                {
                    HOperatorSet.SetDlModelParam(modelHandle, "optimize_for_inference", "true");
                }

                string deviceUsed = DeepLearningToolShared.ConfigureDevice(modelHandle, Device);
                return new CachedModel
                {
                    Key = null,
                    Handle = modelHandle,
                    ResolvedPath = resolvedPath,
                    ModelType = actualType,
                    DeviceUsed = deviceUsed,
                    ImageWidth = DeepLearningToolShared.ReadIntParam(modelHandle, "image_width", fallback: 0),
                    ImageHeight = DeepLearningToolShared.ReadIntParam(modelHandle, "image_height", fallback: 0),
                    ImageNumChannels = DeepLearningToolShared.ReadIntParam(modelHandle, "image_num_channels", fallback: 0),
                    RangeMin = DeepLearningToolShared.ReadDoubleParam(modelHandle, "image_range_min", fallback: 0),
                    RangeMax = DeepLearningToolShared.ReadDoubleParam(modelHandle, "image_range_max", fallback: 255),
                    ClassNames = DeepLearningToolShared.ReadStringArrayParam(modelHandle, "class_names"),
                    ClassIds = DeepLearningToolShared.ReadIntArrayParam(modelHandle, "class_ids"),
                    ModelSummary = DeepLearningToolShared.ReadStringParam(modelHandle, "summary", string.Empty),
                    PoolEntry = null
                };
            }
            catch
            {
                DeepLearningToolShared.TryClearDlModel(modelHandle);
                throw;
            }
        }

        private CachedModel ToCachedModel(DlModelCacheEntry entry, object key)
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

        private CachedModel ToCachedModel(CachedModel loaded, object key, string resolvedPath)
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

        // ======================= 输出 =======================

        private void WriteOutputs(FlowContext ctx, CachedModel model, SegmentationResult segmentation)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "Ready", VariableType.Bool, true));
            SetOutput(ctx, Variable.Single(ModuleName, "ModelType", VariableType.String, model.ModelType ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "ResolvedModelPath", VariableType.String, model.ResolvedPath ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "DeviceUsed", VariableType.String, model.DeviceUsed ?? string.Empty));
            SetOutput(ctx, Variable.Single(ModuleName, "ClassCount", VariableType.Int, model.ClassNames?.Length ?? 0));
            SetOutput(ctx, Variable.Single(ModuleName, "ImageWidth", VariableType.Int, model.ImageWidth));
            SetOutput(ctx, Variable.Single(ModuleName, "ImageHeight", VariableType.Int, model.ImageHeight));
            SetOutput(ctx, Variable.Single(ModuleName, "OptimizedForInference", VariableType.Bool, OptimizeForInference));
            SetOutput(ctx, Variable.Single(ModuleName, "ModelSummary", VariableType.String, model.ModelSummary ?? string.Empty));
            SetOutput(ctx, Variable.Object(ModuleName, "Info", new DeepLearningInferenceInfo
            {
                RequestedModelKind = DeepLearningModelKind.Segmentation.ToString(),
                ModelKind = model.ModelType,
                RequestedDevice = Device.ToString(),
                DeviceUsed = model.DeviceUsed,
                ModelFilePath = ModelFilePath ?? string.Empty,
                ResolvedModelPath = model.ResolvedPath,
                BatchSize = BatchSize,
                ImageWidth = model.ImageWidth,
                ImageHeight = model.ImageHeight,
                OptimizedForInference = OptimizeForInference,
                ClassNames = model.ClassNames,
                ClassIds = model.ClassIds,
                ModelSummary = model.ModelSummary
            }, 1));
            // §8 专用输出：Regions 每类一个区域元组（顺序 = 模型类别序，应用 ClassFilter 后，与 ColorSegmentTool 消费一致）；
            // ClassNames / ClassIds / Areas 与 Regions 一一对应；RejectedRegion = 整图定义域 − union1(类区域)。
            SetOutput(ctx, Variable.Object(ModuleName, "MaskImage", new HalconImage(segmentation.MaskImage), 1));
            SetOutput(ctx, Variable.Object(ModuleName, "Regions", new HalconRegion(segmentation.Regions), segmentation.ClassNames.Length));
            SetOutput(ctx, Variable.Array(ModuleName, "ClassNames", VariableType.String, segmentation.ClassNames));
            SetOutput(ctx, Variable.Array(ModuleName, "ClassIds", VariableType.Int, segmentation.ClassIds));
            SetOutput(ctx, Variable.Array(ModuleName, "Areas", VariableType.Int, segmentation.Areas));
            SetOutput(ctx, Variable.Object(ModuleName, "RejectedRegion", new HalconRegion(segmentation.RejectedRegion), 1));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, segmentation.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, segmentation.Count > 0));
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (string.IsNullOrWhiteSpace(ModelFilePath))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "请先指定 .hdl 模型文件");
                yield break;
            }

            string expanded = Environment.ExpandEnvironmentVariables(ModelFilePath.Trim());
            if (Path.GetExtension(expanded).Length > 0 && !string.Equals(Path.GetExtension(expanded), ".hdl", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "模型文件应为 .hdl");
            }
            if (BatchSize <= 0)
            {
                yield return new ToolConfigurationIssue(nameof(BatchSize), "BatchSize 必须大于 0");
            }
            if (MinScore < 0 || MinScore > 1)
            {
                yield return new ToolConfigurationIssue(nameof(MinScore), "MinScore 必须在 0~1 之间");
            }
            if (!string.IsNullOrWhiteSpace(ClassFilter) && ParseClassFilter(ClassFilter) == null)
            {
                yield return new ToolConfigurationIssue(nameof(ClassFilter), "ClassFilter 格式无效：应为逗号分隔的类别名，如 pill,defect");
            }

            string path = DeepLearningToolShared.EnumerateModelPathCandidates(ModelFilePath).FirstOrDefault(File.Exists);
            if (path == null)
            {
                yield return new ToolConfigurationIssue(nameof(ModelFilePath), "模型文件不存在");
            }
        }

        public void Prepare()
        {
            lock (_sync)
            {
                EnsureCachedModel(null);
            }
        }

        public void ReleaseResources()
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
    }
}
