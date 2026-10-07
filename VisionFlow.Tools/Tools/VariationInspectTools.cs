using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>差分模型的建模方式（成员名即 HALCON create_variation_model 的 Mode 值）。</summary>
    public enum VariationModelMode
    {
        /// <summary>多张良品图的均值与标准差。</summary>
        standard,
        /// <summary>多张良品图的中值与中值绝对偏差（对个别不良样本更稳健）。</summary>
        robust,
        /// <summary>只用一张良品图：以它为标准图、以它的边缘幅值图为偏差图（prepare_direct_variation_model）。</summary>
        direct
    }

    /// <summary>差分比较方式（成员名即 compare_ext_variation_model 的 Mode 值）。</summary>
    public enum VariationCompareMode
    {
        /// <summary>比标准图亮或暗超出允许偏差的都算缺陷。</summary>
        absolute,
        /// <summary>只检过亮的缺陷。</summary>
        light,
        /// <summary>只检过暗的缺陷。</summary>
        dark,
        /// <summary>亮、暗分别比较后合并。</summary>
        light_dark
    }

    /// <summary>
    /// 差分模型数据（VariationModelData / ModelFile 的内容，小端）：魔数 "VFVM"、int32 版本 1、int32 建模方式、int32 宽、int32 高、
    /// int32 训练样本数、int32 载荷长度、载荷。载荷：standard / robust 为保留训练数据、未 prepare 的 serialize_variation_model
    /// （阈值在加载时 prepare，修改阈值不需要重新训练）；direct 为良品图的 serialize_image（加载时重新求边缘幅值图并 prepare_direct）。
    /// HALCON 22.11 实测：prepare 会把阈值写进模型，而 clear_train_data 之后不能再 prepare，所以训练数据必须保留（约 12 字节 / 像素）。
    /// </summary>
    public sealed class VariationModelPackage
    {
        public const int CurrentVersion = 1;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VFVM");

        public VariationModelMode Mode { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int SampleCount { get; set; }
        public byte[] Payload { get; set; }

        public byte[] ToBytes()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(CurrentVersion);
                writer.Write((int)Mode);
                writer.Write(Width);
                writer.Write(Height);
                writer.Write(SampleCount);
                writer.Write(Payload.Length);
                writer.Write(Payload);
            }
            return stream.ToArray();
        }

        /// <summary>解析；格式错误抛出 <see cref="InvalidDataException"/>（中文说明）。</summary>
        public static VariationModelPackage FromBytes(byte[] data)
        {
            if (data == null || data.Length < 28 || !data.Take(4).SequenceEqual(Magic))
            {
                throw new InvalidDataException("差分模型数据格式错误：不是 VisionFlow 差分模型（缺少 VFVM 标识或数据过短）");
            }
            using var reader = new BinaryReader(new MemoryStream(data));
            reader.ReadBytes(4);
            int version = reader.ReadInt32();
            if (version != CurrentVersion)
            {
                throw new InvalidDataException($"差分模型数据版本 {version} 不受支持（当前版本 {CurrentVersion}），请重新训练");
            }
            int mode = reader.ReadInt32();
            if (!Enum.IsDefined(typeof(VariationModelMode), mode))
            {
                throw new InvalidDataException($"差分模型数据格式错误：建模方式 {mode} 无效");
            }
            var package = new VariationModelPackage
            {
                Mode = (VariationModelMode)mode,
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                SampleCount = reader.ReadInt32()
            };
            int length = reader.ReadInt32();
            if (length <= 0 || 28 + length != data.Length)
            {
                throw new InvalidDataException("差分模型数据格式错误：载荷长度与数据不符（数据被截断或损坏）");
            }
            package.Payload = reader.ReadBytes(length);
            return package;
        }

        /// <summary>只读头部（不复制载荷），用于校验与界面显示。</summary>
        public static bool TryReadHeader(byte[] data, out VariationModelPackage header, out string error)
        {
            try
            {
                header = FromBytes(data);
                header.Payload = null;
                error = null;
                return true;
            }
            catch (InvalidDataException ex)
            {
                header = null;
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>差分检测的亮 / 暗阈值（CSV：1 个值亮暗共用，2 个值依次为亮、暗）。</summary>
    public static class VariationThresholds
    {
        public static bool TryParse(string text, string name, out double[] values, out string error)
        {
            values = null;
            error = null;
            string[] parts = (text ?? string.Empty).Split(new[] { ',', '，' }, StringSplitOptions.None).Select(p => p.Trim()).ToArray();
            var parsed = new List<double>();
            bool ok = parts.Length >= 1 && parts.Length <= 2;
            foreach (string part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !(value > 0) || double.IsInfinity(value))
                {
                    ok = false;
                    break;
                }
                parsed.Add(value);
            }
            if (!ok)
            {
                error = $"{name}格式错误：“{text}”。应为 1 个正数（亮暗共用，如 20）或用逗号分开的 2 个正数（依次为亮、暗，如 20,30）";
                return false;
            }
            values = parsed.ToArray();
            return true;
        }
    }

    /// <summary>差分模型的训练与加载（与界面无关，编辑窗口与测试共用）。</summary>
    public static class VariationModelTraining
    {
        /// <summary>模型数据超过该字节数时建议改用外部模型文件（10 MB）。</summary>
        public const long LargeModelBytes = 10L * 1024 * 1024;

        /// <summary>是否需要提示改用外部模型文件。</summary>
        public static bool ShouldSuggestModelFile(long bytes)
        {
            return bytes > LargeModelBytes;
        }

        /// <summary>界面显示用的模型大小（MB，两位小数）。</summary>
        public static string FormatSize(long bytes)
        {
            return (bytes / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>模型超过 10 MB 时的提示文字（编辑窗口状态区与运行日志共用）；未超过返回 null。</summary>
        public static string LargeModelHint(long bytes)
        {
            return ShouldSuggestModelFile(bytes)
                ? $"差分模型 {FormatSize(bytes)}，超过 10 MB，建议导出为外部模型文件（ModelFile），减小流程文件体积"
                : null;
        }

        /// <summary>
        /// 训练：standard / robust 逐张 train_variation_model（保留训练数据，不 prepare）；direct 只接受一张良品图。
        /// 全部样本须同尺寸、同类型。
        /// </summary>
        public static byte[] Train(IList<HObject> samples, VariationModelMode mode)
        {
            if (samples == null || samples.Count == 0)
            {
                throw new InvalidOperationException("没有训练样本，请先加入良品图像");
            }
            if (mode == VariationModelMode.direct && samples.Count != 1)
            {
                throw new InvalidOperationException($"direct 建模只用一张良品图（当前 {samples.Count} 张）；多张样本请改用 standard 或 robust");
            }
            HOperatorSet.GetImageSize(samples[0], out HTuple width, out HTuple height);
            HOperatorSet.GetImageType(samples[0], out HTuple type);
            for (int i = 1; i < samples.Count; i++)
            {
                HOperatorSet.GetImageSize(samples[i], out HTuple w, out HTuple h);
                HOperatorSet.GetImageType(samples[i], out HTuple t);
                if (w.I != width.I || h.I != height.I || t.S != type.S)
                {
                    throw new InvalidOperationException($"第 {i + 1} 张样本为 {w.I}×{h.I}（{t.S}），与第 1 张 {width.I}×{height.I}（{type.S}）不一致");
                }
            }
            byte[] payload;
            if (mode == VariationModelMode.direct)
            {
                payload = HalconImageSerialization.Serialize(samples[0]);
            }
            else
            {
                HOperatorSet.CreateVariationModel(width, height, type, mode.ToString(), out HTuple model);
                try
                {
                    foreach (HObject sample in samples)
                    {
                        HOperatorSet.TrainVariationModel(sample, model);
                    }
                    payload = SerializeModel(model);
                }
                finally
                {
                    HOperatorSet.ClearVariationModel(model);
                }
            }
            return new VariationModelPackage
            {
                Mode = mode,
                Width = width.I,
                Height = height.I,
                SampleCount = samples.Count,
                Payload = payload
            }.ToBytes();
        }

        /// <summary>direct 建模的偏差图：良品图的边缘幅值（sobel_amp，sum_abs，3×3）。</summary>
        public static HObject DirectVariationImage(HObject reference)
        {
            HOperatorSet.SobelAmp(reference, out HObject edges, "sum_abs", 3);
            return edges;
        }

        /// <summary>读回标准图与偏差图（编辑窗口预览；standard / robust 用 get_variation_model，direct 用良品图与其边缘幅值图）。调用方释放。</summary>
        public static void GetPreview(byte[] data, out HObject ideal, out HObject variation)
        {
            VariationModelPackage package = VariationModelPackage.FromBytes(data);
            if (package.Mode == VariationModelMode.direct)
            {
                ideal = HalconImageSerialization.Deserialize(package.Payload);
                variation = DirectVariationImage(ideal);
                return;
            }
            HTuple model = DeserializeModel(package.Payload);
            try
            {
                HOperatorSet.GetVariationModel(out ideal, out variation, model);
            }
            finally
            {
                HOperatorSet.ClearVariationModel(model);
            }
        }

        public static byte[] SerializeModel(HTuple model)
        {
            HOperatorSet.SerializeVariationModel(model, out HTuple item);
            try
            {
                HOperatorSet.GetSerializedItemPtr(item, out HTuple pointer, out HTuple size);
                var bytes = new byte[size.L];
                Marshal.Copy(new IntPtr(pointer.L), bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                HOperatorSet.ClearSerializedItem(item);
            }
        }

        public static HTuple DeserializeModel(byte[] bytes)
        {
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            HTuple item = null;
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                HOperatorSet.CreateSerializedItemPtr(new HTuple(pointer.ToInt64()), bytes.Length, "true", out item);
                HOperatorSet.DeserializeVariationModel(item, out HTuple model);
                return model;
            }
            finally
            {
                if (item != null)
                {
                    HOperatorSet.ClearSerializedItem(item);
                }
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    /// <summary>
    /// 已加载的差分模型句柄（standard / robust：保留训练数据的模型；direct：模型 + 良品图 + 偏差图），按需用新阈值重新 prepare。
    /// </summary>
    internal sealed class LoadedVariationModel : IDisposable
    {
        private HObject _reference;
        private HObject _variation;
        private string _preparedThresholds;

        public LoadedVariationModel(VariationModelPackage package)
        {
            Mode = package.Mode;
            Width = package.Width;
            Height = package.Height;
            if (package.Mode == VariationModelMode.direct)
            {
                _reference = HalconImageSerialization.Deserialize(package.Payload);
                _variation = VariationModelTraining.DirectVariationImage(_reference);
                HOperatorSet.GetImageType(_reference, out HTuple type);
                HOperatorSet.CreateVariationModel(package.Width, package.Height, type, "direct", out HTuple model);
                Model = model;
            }
            else
            {
                Model = VariationModelTraining.DeserializeModel(package.Payload);
            }
        }

        public HTuple Model { get; private set; }
        public VariationModelMode Mode { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>阈值与上次 prepare 不同时重新 prepare（不重新训练）；返回本次是否重新 prepare。</summary>
        public bool EnsurePrepared(double[] absThreshold, double[] varThreshold)
        {
            string key = string.Join(",", absThreshold.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "|"
                + string.Join(",", varThreshold.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            if (key == _preparedThresholds)
            {
                return false;
            }
            if (Mode == VariationModelMode.direct)
            {
                HOperatorSet.PrepareDirectVariationModel(_reference, _variation, Model, new HTuple(absThreshold), new HTuple(varThreshold));
            }
            else
            {
                HOperatorSet.PrepareVariationModel(Model, new HTuple(absThreshold), new HTuple(varThreshold));
            }
            _preparedThresholds = key;
            return true;
        }

        public void Dispose()
        {
            if (Model != null)
            {
                HOperatorSet.ClearVariationModel(Model);
                Model = null;
            }
            _reference?.Dispose();
            _variation?.Dispose();
            _reference = null;
            _variation = null;
        }
    }

    /// <summary>
    /// 差分检测（MT-06）：用良品图训练出“标准图 + 允许偏差图”，检测时找出超出偏差的区域（印刷、标签、表面外观缺陷）。对标 VisionPro PatInspect。
    /// 定位矩阵（可选，通常接模板匹配的 BestHomMat，即“示教位姿 → 当前位姿”）：当前图像按其逆矩阵 affine_trans_image 对齐到模型位置后比较，
    /// 缺陷区域再变换回当前图像坐标输出。检测区域（可选）在模型（对齐后）坐标中，比较前 reduce_domain，区域外的差异不计；区域为空按失败处理。
    /// 未检测到缺陷是正常结果（HasDefect = false），不使用“未找到”策略。
    /// 模型缓存：键为模型来源（内嵌数据引用，或外部文件路径 + 修改时间 + 大小）；阈值变化时在缓存的句柄上重新 prepare（各建模方式都不需要重新训练）。
    /// </summary>
    [ToolOutput("DefectRegion", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("DefectCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("DefectAreas", VariableKind.Array, VariableType.Double)]
    [ToolOutput("MaxDefectArea", VariableKind.Single, VariableType.Double)]
    [ToolOutput("HasDefect", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class VariationInspectTool : ToolBase, IToolResourceLifecycle, IToolConfigurationCheck
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("定位矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        [InputRef("检测区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        /// <summary>内嵌的差分模型（格式见 <see cref="VariationModelPackage"/>）；与 ModelFile 互斥。重新训练会整体替换数组。</summary>
        public byte[] VariationModelData { get; set; }
        /// <summary>外部模型文件（与内嵌数据互斥，配置后从文件加载；文件由上层项目部署）。</summary>
        public string ModelFile { get; set; }
        /// <summary>建模方式（训练时使用；与已训练模型不一致时须重新训练）。</summary>
        public VariationModelMode ModelMode { get; set; } = VariationModelMode.standard;
        /// <summary>绝对阈值（灰度差）：1 个值亮暗共用，或“亮,暗”两个值。允许偏差 = max(绝对阈值, 相对阈值 × 偏差图)（22.11 实测）。</summary>
        public string AbsThreshold { get; set; } = "20";
        /// <summary>相对阈值（偏差图的倍数）：1 个值亮暗共用，或“亮,暗”两个值。</summary>
        public string VarThreshold { get; set; } = "3";
        public VariationCompareMode CompareMode { get; set; } = VariationCompareMode.absolute;
        /// <summary>面积（像素）小于该值的差异区域忽略。</summary>
        public int MinDefectArea { get; set; } = 10;

        private readonly object _sync = new object();
        private LoadedVariationModel _cached;
        private object _cachedKey;

        public VariationInspectTool(string moduleName) : base(moduleName)
        {
        }

        /// <summary>训练完成或改为内嵌：整体替换内嵌数组（新引用触发重新加载），并清空外部模型文件，避免运行时仍加载外部旧文件。</summary>
        public void UseEmbeddedModel(byte[] data)
        {
            VariationModelData = data;
            ModelFile = null;
        }

        /// <summary>改用外部模型文件：清空内嵌数据（两者互斥）。</summary>
        public void UseModelFile(string path)
        {
            ModelFile = path;
            VariationModelData = null;
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (!VariationThresholds.TryParse(AbsThreshold, "绝对阈值", out _, out string absError))
            {
                yield return new ToolConfigurationIssue(nameof(AbsThreshold), absError);
            }
            if (!VariationThresholds.TryParse(VarThreshold, "相对阈值", out _, out string varError))
            {
                yield return new ToolConfigurationIssue(nameof(VarThreshold), varError);
            }
            if (MinDefectArea < 0)
            {
                yield return new ToolConfigurationIssue(nameof(MinDefectArea), "最小缺陷面积不能小于 0");
            }
            bool hasData = VariationModelData != null && VariationModelData.Length > 0;
            if (hasData && !string.IsNullOrWhiteSpace(ModelFile))
            {
                yield return new ToolConfigurationIssue(nameof(ModelFile),
                    "外部模型文件与内嵌模型数据不能同时存在：配置外部文件时以文件为准，请在编辑窗口中“导出 / 选择外部文件”（会清空内嵌数据），或清空 ModelFile 继续使用内嵌模型");
            }
            else if (hasData)
            {
                if (!VariationModelPackage.TryReadHeader(VariationModelData, out VariationModelPackage header, out string error))
                {
                    yield return new ToolConfigurationIssue(nameof(VariationModelData), error);
                }
                else if (header.Mode != ModelMode)
                {
                    yield return new ToolConfigurationIssue(nameof(ModelMode),
                        $"建模方式已改为 {ModelMode}，需要重新训练（当前模型按 {header.Mode} 训练）");
                }
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }
            VariationThresholds.TryParse(AbsThreshold, "绝对阈值", out double[] absThreshold, out _);
            VariationThresholds.TryParse(VarThreshold, "相对阈值", out double[] varThreshold, out _);
            HObject image;
            try
            {
                image = Input<HalconImage>(ctx, ImagePath).Object;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException)
            {
                return NodeResult.Fail($"{ModuleName} 输入图像引用无效：{ImagePath}，{ex.Message}");
            }
            if (!FollowMatrixResolver.TryResolve(ctx, ModuleName, MatrixPath, out List<HomMat2D> matrices, out string matrixError))
            {
                return NodeResult.Fail(matrixError);
            }
            if (matrices.Count > 1)
            {
                return NodeResult.Fail($"{ModuleName} 的定位矩阵只支持单个矩阵（当前为 {matrices.Count} 个）；多个工件请放在 For 循环中引用 Loop.Current.HomMat");
            }
            HomMat2D matrix = matrices[0];
            HObject region = null;
            if (!string.IsNullOrWhiteSpace(RegionPath))
            {
                try
                {
                    region = Input<HalconRegion>(ctx, RegionPath).Object;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException)
                {
                    return NodeResult.Fail($"{ModuleName} 检测区域引用无效：{RegionPath}，{ex.Message}");
                }
            }

            var owned = new List<HObject>();
            try
            {
                lock (_sync)
                {
                    LoadedVariationModel model = EnsureModel(ctx, out string loadError);
                    if (model == null)
                    {
                        return NodeResult.Fail($"{ModuleName} {loadError}");
                    }
                    if (model.EnsurePrepared(absThreshold, varThreshold))
                    {
                        ctx.AddLog(FlowLogLevel.Info, $"[差分检测] 按阈值 绝对 {AbsThreshold} / 相对 {VarThreshold} 准备模型（{model.Mode}，不需要重新训练）");
                    }

                    HObject aligned = image;
                    if (matrix != null)
                    {
                        // 定位矩阵为“示教位姿 → 当前位姿”，按其逆矩阵把当前图像对齐到模型位置；对齐后定义域为图像内的部分
                        HOperatorSet.AffineTransImage(image, out aligned, matrix.Inverted().Data, "constant", "false");
                        owned.Add(aligned);
                    }
                    HOperatorSet.GetImageSize(aligned, out HTuple width, out HTuple height);
                    if (width.I != model.Width || height.I != model.Height)
                    {
                        return NodeResult.Fail($"{ModuleName} 图像尺寸 {width.I}×{height.I}（{(matrix != null ? "对齐后" : "输入")}）与差分模型尺寸 {model.Width}×{model.Height} 不一致");
                    }

                    HObject compared = aligned;
                    if (region != null)
                    {
                        HOperatorSet.Union1(region, out HObject union);
                        owned.Add(union);
                        HOperatorSet.AreaCenter(union, out HTuple area, out _, out _);
                        if (area.Length == 0 || area.D <= 0)
                        {
                            // 配置了空的检测区域几乎必然是配置错误，按失败处理而不是静默输出“无缺陷”
                            return NodeResult.Fail($"{ModuleName} 检测区域为空：{RegionPath}（没有可比较的区域，无法判定有无缺陷）");
                        }
                        // 用 reduce_domain 而不是裁剪：图像与模型保持同尺寸，区域外的差异不参与比较
                        HOperatorSet.GetDomain(aligned, out HObject domain);
                        HOperatorSet.Intersection(domain, union, out HObject inspect);
                        domain.Dispose();
                        HOperatorSet.ReduceDomain(aligned, inspect, out compared);
                        inspect.Dispose();
                        owned.Add(compared);
                    }

                    // 差分模型算子没有 timeout 参数（与 find_generic_shape_model 不同），不存在 #9400 超时映射
                    HOperatorSet.CompareExtVariationModel(compared, out HObject differences, model.Model, CompareMode.ToString());
                    owned.Add(differences);
                    HOperatorSet.Union1(differences, out HObject merged);
                    owned.Add(merged);
                    HOperatorSet.Connection(merged, out HObject parts);
                    owned.Add(parts);
                    HObject kept = parts;
                    if (MinDefectArea > 0)
                    {
                        HOperatorSet.SelectShape(parts, out kept, "area", "and", MinDefectArea, int.MaxValue);
                        owned.Add(kept);
                    }
                    HObject defects;
                    if (matrix != null)
                    {
                        HOperatorSet.AffineTransRegion(kept, out defects, matrix.Data, "nearest_neighbor");
                    }
                    else
                    {
                        defects = kept.CopyObj(1, -1);
                    }
                    HOperatorSet.CountObj(defects, out HTuple count);
                    double[] areas = new double[0];
                    if (count.I > 0)
                    {
                        HOperatorSet.AreaCenter(defects, out HTuple defectAreas, out _, out _);
                        areas = defectAreas.ToDArr();
                    }
                    SetOutput(ctx, Variable.Object(ModuleName, "DefectRegion", new HalconRegion(defects), count.I));
                    SetOutput(ctx, Variable.Single(ModuleName, "DefectCount", VariableType.Int, count.I));
                    SetOutput(ctx, Variable.Array(ModuleName, "DefectAreas", VariableType.Double, areas));
                    SetOutput(ctx, Variable.Single(ModuleName, "MaxDefectArea", VariableType.Double, areas.Length > 0 ? areas.Max() : 0.0));
                    SetOutput(ctx, Variable.Single(ModuleName, "HasDefect", VariableType.Bool, count.I > 0));
                    SetBorrowedOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(image), 1));
                    ctx.AddLog(FlowLogLevel.Info, count.I > 0
                        ? $"[差分检测] 缺陷 {count.I} 个，最大面积 {areas.Max():F0}"
                        : "[差分检测] 未发现缺陷");
                    return NodeResult.Ok;
                }
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 差分检测失败：{ex.Message}");
            }
            finally
            {
                foreach (HObject obj in owned)
                {
                    obj.Dispose();
                }
            }
        }

        /// <summary>当前模型来源的缓存键：内嵌数据的数组引用，或外部文件的路径 + 修改时间 + 大小；没有模型时为 null。</summary>
        private object CurrentModelKey(out string error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(ModelFile))
            {
                var info = new FileInfo(ModelFile);
                if (!info.Exists)
                {
                    error = "外部模型文件不存在：" + ModelFile;
                    return null;
                }
                return (info.FullName, info.LastWriteTimeUtc, info.Length);
            }
            if (VariationModelData == null || VariationModelData.Length == 0)
            {
                error = "差分模型未训练（请在编辑窗口中加入良品样本并训练，或配置外部模型文件）";
                return null;
            }
            return VariationModelData;
        }

        private LoadedVariationModel EnsureModel(FlowContext ctx, out string error)
        {
            object key = CurrentModelKey(out error);
            if (key == null)
            {
                return null;
            }
            if (_cached != null && Equals(_cachedKey, key))
            {
                return _cached;
            }
            ReleaseCached();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            byte[] data = key is byte[] embedded ? embedded : File.ReadAllBytes(ModelFile);
            VariationModelPackage package;
            try
            {
                package = VariationModelPackage.FromBytes(data);
            }
            catch (InvalidDataException ex)
            {
                error = (key is byte[] ? "" : "外部模型文件 " + ModelFile + "：") + ex.Message;
                return null;
            }
            if (package.Mode != ModelMode)
            {
                error = $"建模方式已改为 {ModelMode}，需要重新训练（当前模型按 {package.Mode} 训练）";
                return null;
            }
            _cached = new LoadedVariationModel(package);
            _cachedKey = key;
            ctx?.AddLog(FlowLogLevel.Info, $"[差分检测] 模型加载耗时 {watch.ElapsedMilliseconds} ms（{package.Mode}，{package.Width}×{package.Height}，样本 {package.SampleCount} 张）");
            string hint = key is byte[] ? VariationModelTraining.LargeModelHint(data.Length) : null;
            if (hint != null)
            {
                ctx?.AddLog(FlowLogLevel.Warning, $"[差分检测] {ModuleName}：{hint}");
            }
            return _cached;
        }

        /// <summary>预热：加载模型并按当前阈值 prepare。未训练或阈值格式错误时直接返回，由运行时报告。</summary>
        public void Prepare()
        {
            if (!VariationThresholds.TryParse(AbsThreshold, "绝对阈值", out double[] abs, out _)
                || !VariationThresholds.TryParse(VarThreshold, "相对阈值", out double[] variation, out _))
            {
                return;
            }
            lock (_sync)
            {
                if (CurrentModelKey(out _) == null)
                {
                    return;
                }
                LoadedVariationModel model = EnsureModel(null, out string error);
                if (model == null)
                {
                    throw new InvalidOperationException(error);
                }
                model.EnsurePrepared(abs, variation);
            }
        }

        public void ReleaseResources()
        {
            lock (_sync)
            {
                ReleaseCached();
            }
        }

        private void ReleaseCached()
        {
            _cached?.Dispose();
            _cached = null;
            _cachedKey = null;
        }
    }
}
