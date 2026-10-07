using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>通用形状匹配的缩放方式（决定训练时启用哪组缩放参数）。</summary>
    public enum ScaleMode
    {
        /// <summary>不缩放（默认）。</summary>
        None,
        /// <summary>等比缩放（iso_scale_min / iso_scale_max）。</summary>
        Isotropic,
        /// <summary>行、列方向不同比例缩放（scale_row_* / scale_column_*）。</summary>
        Anisotropic
    }

    /// <summary>通用形状匹配的一个模板：名称 + 序列化的通用形状模型（serialize_shape_model）。</summary>
    public sealed class GenericShapeTemplate
    {
        public GenericShapeTemplate(string name, byte[] modelData)
        {
            Name = name;
            ModelData = modelData;
        }

        public string Name { get; private set; }
        public byte[] ModelData { get; private set; }
    }

    /// <summary>
    /// 通用形状匹配的多模板打包格式（ModelsData，小端）：
    /// int32 版本号（当前 1）、int32 模板数，然后逐个模板写 int32 名称字节数、UTF-8 名称、int32 模型字节数、模型字节。
    /// 读取时版本不符、长度越界、末尾有多余字节都给出明确错误，不静默误读。
    /// </summary>
    public static class GenericShapeModelData
    {
        public const int CurrentVersion = 1;
        private const int MaxTemplates = 1000;

        public static byte[] Pack(IEnumerable<GenericShapeTemplate> templates)
        {
            List<GenericShapeTemplate> list = (templates ?? Enumerable.Empty<GenericShapeTemplate>()).ToList();
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(CurrentVersion);
                writer.Write(list.Count);
                foreach (GenericShapeTemplate template in list)
                {
                    if (template.ModelData == null || template.ModelData.Length == 0)
                    {
                        throw new InvalidOperationException($"模板“{template.Name}”尚未训练，不能保存");
                    }
                    byte[] name = Encoding.UTF8.GetBytes(template.Name ?? string.Empty);
                    writer.Write(name.Length);
                    writer.Write(name);
                    writer.Write(template.ModelData.Length);
                    writer.Write(template.ModelData);
                }
            }
            return stream.ToArray();
        }

        /// <summary>解包；数据为空时返回空列表，格式错误抛出 <see cref="InvalidDataException"/>（中文说明）。</summary>
        public static List<GenericShapeTemplate> Unpack(byte[] data)
        {
            var templates = new List<GenericShapeTemplate>();
            Read(data, (name, offset, length) => templates.Add(new GenericShapeTemplate(name, Slice(data, offset, length))));
            return templates;
        }

        /// <summary>只读取模板名称（不复制模型字节）；格式错误同 <see cref="Unpack"/>。</summary>
        public static List<string> ReadNames(byte[] data)
        {
            var names = new List<string>();
            Read(data, (name, offset, length) => names.Add(name));
            return names;
        }

        public static bool TryReadNames(byte[] data, out List<string> names, out string error)
        {
            try
            {
                names = ReadNames(data);
                error = null;
                return true;
            }
            catch (InvalidDataException ex)
            {
                names = new List<string>();
                error = ex.Message;
                return false;
            }
        }

        private static void Read(byte[] data, Action<string, int, int> onTemplate)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }
            int position = 0;
            int version = ReadInt(data, ref position, "版本号");
            if (version != CurrentVersion)
            {
                throw new InvalidDataException($"通用形状模型数据版本 {version} 不受支持（当前版本 {CurrentVersion}），请用对应版本的程序打开或重新示教");
            }
            int count = ReadInt(data, ref position, "模板数");
            if (count < 0 || count > MaxTemplates)
            {
                throw new InvalidDataException($"通用形状模型数据格式错误：模板数 {count} 无效");
            }
            for (int i = 0; i < count; i++)
            {
                int nameLength = ReadInt(data, ref position, $"第 {i} 个模板的名称长度");
                RequireBytes(data, position, nameLength, $"第 {i} 个模板的名称");
                string name = Encoding.UTF8.GetString(data, position, nameLength);
                position += nameLength;
                int modelLength = ReadInt(data, ref position, $"第 {i} 个模板的模型长度");
                if (modelLength <= 0)
                {
                    throw new InvalidDataException($"通用形状模型数据格式错误：第 {i} 个模板（{name}）的模型为空");
                }
                RequireBytes(data, position, modelLength, $"第 {i} 个模板（{name}）的模型");
                onTemplate(name, position, modelLength);
                position += modelLength;
            }
            if (position != data.Length)
            {
                throw new InvalidDataException($"通用形状模型数据格式错误：末尾有 {data.Length - position} 个多余字节");
            }
        }

        private static int ReadInt(byte[] data, ref int position, string what)
        {
            RequireBytes(data, position, 4, what);
            int value = BitConverter.ToInt32(data, position);
            position += 4;
            return value;
        }

        private static void RequireBytes(byte[] data, int position, int length, string what)
        {
            if (length < 0 || position + length > data.Length)
            {
                throw new InvalidDataException($"通用形状模型数据格式错误：{what}超出数据长度（数据被截断或损坏）");
            }
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            var bytes = new byte[length];
            Buffer.BlockCopy(data, offset, bytes, 0, length);
            return bytes;
        }
    }

    /// <summary>
    /// 通用形状匹配的示教步骤（与界面无关，编辑窗口与测试共用）：训练、模型原点、杂乱区域、序列化。
    /// HALCON 22.11 实测（计划第 12 节）：缩放范围、金字塔层数、度量、对比度、优化、最小尺寸只能在训练前设置
    /// （训练后修改会使模型需要重新训练）；角度范围、原点、杂乱参数与全部查找参数训练后可随时修改。
    /// </summary>
    public static class GenericShapeTraining
    {
        /// <summary>按 settings 的建模参数创建并训练一个通用形状模型；template 为缩小定义域的图像或 XLD 轮廓。</summary>
        public static HTuple Train(HalconGenericShapeMatchTool settings, HObject template, bool fromXld)
        {
            HOperatorSet.CreateGenericShapeModel(out HTuple model);
            try
            {
                HOperatorSet.SetGenericShapeModelParam(model, "angle_start", settings.AngleStart);
                HOperatorSet.SetGenericShapeModelParam(model, "angle_end", settings.AngleEnd);
                switch (settings.ScaleMode)
                {
                    case ScaleMode.Isotropic:
                        HOperatorSet.SetGenericShapeModelParam(model, "iso_scale_min", settings.IsoScaleMin);
                        HOperatorSet.SetGenericShapeModelParam(model, "iso_scale_max", settings.IsoScaleMax);
                        break;
                    case ScaleMode.Anisotropic:
                        HOperatorSet.SetGenericShapeModelParam(model, "scale_row_min", settings.ScaleRowMin);
                        HOperatorSet.SetGenericShapeModelParam(model, "scale_row_max", settings.ScaleRowMax);
                        HOperatorSet.SetGenericShapeModelParam(model, "scale_column_min", settings.ScaleColumnMin);
                        HOperatorSet.SetGenericShapeModelParam(model, "scale_column_max", settings.ScaleColumnMax);
                        break;
                }
                if (settings.NumLevels > 0)
                {
                    HOperatorSet.SetGenericShapeModelParam(model, "num_levels", settings.NumLevels);
                }
                // XLD 没有灰度极性，度量只能是 ignore_local_polarity
                HOperatorSet.SetGenericShapeModelParam(model, "metric", fromXld ? MatchModelBuilder.XldMetric : settings.Metric);
                if (!string.IsNullOrWhiteSpace(settings.Optimization))
                {
                    HOperatorSet.SetGenericShapeModelParam(model, "optimization", settings.Optimization);
                }
                if (!fromXld)
                {
                    if (settings.ContrastLow > 0)
                    {
                        HOperatorSet.SetGenericShapeModelParam(model, "contrast_low", settings.ContrastLow);
                    }
                    if (settings.ContrastHigh > 0)
                    {
                        HOperatorSet.SetGenericShapeModelParam(model, "contrast_high", settings.ContrastHigh);
                    }
                    if (settings.MinSize > 0)
                    {
                        HOperatorSet.SetGenericShapeModelParam(model, "min_size", settings.MinSize);
                    }
                }
                if (settings.MinContrast > 0)
                {
                    HOperatorSet.SetGenericShapeModelParam(model, "min_contrast", settings.MinContrast);
                }
                HOperatorSet.TrainGenericShapeModel(template, model);
                return model;
            }
            catch
            {
                HOperatorSet.ClearHandle(model);
                throw;
            }
        }

        /// <summary>写入模型原点（相对模板参考点的偏移，origin_row / origin_column）。</summary>
        public static void SetOrigin(HTuple model, double row, double column)
        {
            HOperatorSet.SetGenericShapeModelParam(model, "origin_row", row);
            HOperatorSet.SetGenericShapeModelParam(model, "origin_column", column);
        }

        public static void GetOrigin(HTuple model, out double row, out double column)
        {
            HOperatorSet.GetGenericShapeModelParam(model, "origin_row", out HTuple r);
            HOperatorSet.GetGenericShapeModelParam(model, "origin_column", out HTuple c);
            row = r.D;
            column = c.D;
        }

        /// <summary>写入杂乱区域：坐标为示教图像（或 XLD）坐标，HALCON 按训练位姿自动换算（clutter_hom_mat_2d）。</summary>
        public static void SetClutterRegion(HTuple model, HObject region)
        {
            HOperatorSet.SetGenericShapeModelObject(region, model, "clutter_region");
        }

        /// <summary>取模型中的杂乱区域（示教坐标）；未设置时返回 false。</summary>
        public static bool TryGetClutterRegion(HTuple model, out HObject region, out double area)
        {
            region = null;
            area = 0;
            try
            {
                HOperatorSet.GetGenericShapeModelObject(out region, model, "clutter_region");
                HOperatorSet.AreaCenter(region, out HTuple a, out _, out _);
                area = a.Length > 0 ? a.TupleSum().D : 0;
                if (area > 0)
                {
                    return true;
                }
            }
            catch (HalconException)
            {
            }
            region?.Dispose();
            region = null;
            return false;
        }

        public static byte[] Serialize(HTuple model)
        {
            return ShapeModelSerialization.SerializeInMemory(model);
        }

        public static HTuple Deserialize(byte[] data)
        {
            return ShapeModelSerialization.DeserializeInMemory(data);
        }
    }

    /// <summary>
    /// 通用形状匹配（MT-03，HALCON 22.11 通用形状模型）：旋转、等比 / 各向异性缩放、多模板同时匹配、杂乱判定、
    /// 越界匹配、最大变形量与超时。多模板打包在 ModelsData（格式见 <see cref="GenericShapeModelData"/>），
    /// 模板名另存 TemplateNamesCsv（只读镜像，便于在流程文件中查看）。
    /// 查找参数全局生效（对所有模板相同），每次查找前写入缓存的模型句柄；建模参数（缩放、金字塔、度量、对比度等）
    /// 只在示教页训练时生效，侧栏不显示。
    /// 跟随矩阵与显示轮廓使用 get_generic_shape_model_result 的完整 hom_mat_2d（含各向异性缩放），不用 Scale 重建：
    /// HomMat = hom_mat_2d · 基准位姿⁻¹（基准位姿为 0 时即 hom_mat_2d 本身）；轮廓取 get_generic_shape_model_result_object。
    /// </summary>
    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Angle", "Scale", "ScaleRow", "ScaleColumn", "Score", "ModelIndex", "ModelName", "HomMat" })]
    [ToolOutput("HomMats", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("BestMatch", VariableKind.Object, VariableType.Object, ElementClrType = typeof(MatchResultItem))]
    [ToolOutput("BestHomMat", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("Contours", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Angle", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Score", VariableKind.Single, VariableType.Double)]
    [ToolOutput("ScaleRows", VariableKind.Array, VariableType.Double)]
    [ToolOutput("ScaleColumns", VariableKind.Array, VariableType.Double)]
    [ToolOutput("ModelIndices", VariableKind.Array, VariableType.Int)]
    [ToolOutput("ModelNames", VariableKind.Array, VariableType.String)]
    [ToolOutput("BestModelIndex", VariableKind.Single, VariableType.Int)]
    [ToolOutput("BestModelName", VariableKind.Single, VariableType.String)]
    [ToolOutput("CountsPerModel", VariableKind.Array, VariableType.Int)]
    public sealed class HalconGenericShapeMatchTool : HalconMatchToolBase
    {
        /// <summary>全部模板打包后的数据；示教或导入时整体替换数组（新引用触发模型重新加载）。</summary>
        public byte[] ModelsData { get; set; }
        /// <summary>模板名称（逗号分隔，按模板顺序），ModelsData 中名称的只读镜像。</summary>
        public string TemplateNamesCsv { get; set; }

        /// <summary>角度范围起点（弧度，界面按度显示）；训练与查找都使用。</summary>
        public double AngleStart { get; set; } = -0.39;
        /// <summary>角度范围终点（弧度）。</summary>
        public double AngleEnd { get; set; } = 0.39;

        /// <summary>缩放方式（建模参数）。</summary>
        public ScaleMode ScaleMode { get; set; } = ScaleMode.None;
        public double IsoScaleMin { get; set; } = 0.9;
        public double IsoScaleMax { get; set; } = 1.1;
        public double ScaleRowMin { get; set; } = 0.9;
        public double ScaleRowMax { get; set; } = 1.1;
        public double ScaleColumnMin { get; set; } = 0.9;
        public double ScaleColumnMax { get; set; } = 1.1;

        public double MaxOverlap { get; set; } = 0.5;
        public double Greediness { get; set; } = 0.9;
        public string SubPixel { get; set; } = "least_squares";
        /// <summary>允许模板部分超出图像。</summary>
        public bool BorderShapeModels { get; set; }
        /// <summary>允许的轮廓偏移（像素）。</summary>
        public int MaxDeformation { get; set; }
        /// <summary>查找超时（毫秒，HALCON 22.11 实测 timeout 单位为毫秒）；0 表示不限制。</summary>
        public int TimeoutMs { get; set; }
        /// <summary>启用杂乱判定（每个模板都须在示教时绘制杂乱区域：HALCON 要求同一次查找的全部模型一致）。</summary>
        public bool UseClutter { get; set; }
        /// <summary>杂乱区域内允许的最大杂乱比例（0 ~ 1）。</summary>
        public double MaxClutter { get; set; }
        /// <summary>杂乱判定的对比度；0 表示使用模型默认值。</summary>
        public int ClutterContrast { get; set; }

        /// <summary>金字塔层数（建模参数）；0 表示自动。</summary>
        public int NumLevels { get; set; }
        /// <summary>度量（建模参数）；XLD 模板固定为 ignore_local_polarity。</summary>
        public string Metric { get; set; } = "use_polarity";
        /// <summary>点优化（建模参数）；留空表示 HALCON 默认。</summary>
        public string Optimization { get; set; }
        /// <summary>对比度下限（建模参数）；0 表示自动。</summary>
        public int ContrastLow { get; set; }
        /// <summary>对比度上限（建模参数）；0 表示自动。</summary>
        public int ContrastHigh { get; set; }
        /// <summary>最小对比度（建模参数）；0 表示自动。</summary>
        public int MinContrast { get; set; }
        /// <summary>最小轮廓尺寸（建模参数）；0 表示自动。</summary>
        public int MinSize { get; set; }

        /// <summary>基准模板位姿（全部模板共用）：跟随矩阵 = hom_mat_2d · 基准位姿⁻¹。</summary>
        public double BaseRow { get; set; }
        public double BaseColumn { get; set; }
        public double BaseAngle { get; set; }

        /// <summary>建模参数（只在示教页训练模板时生效），侧栏不显示。</summary>
        public static readonly string[] TrainingParameters =
        {
            nameof(ScaleMode), nameof(IsoScaleMin), nameof(IsoScaleMax), nameof(ScaleRowMin), nameof(ScaleRowMax),
            nameof(ScaleColumnMin), nameof(ScaleColumnMax), nameof(NumLevels), nameof(Metric), nameof(Optimization),
            nameof(ContrastLow), nameof(ContrastHigh), nameof(MinContrast), nameof(MinSize)
        };

        private byte[] _namesSource;
        private List<string> _names = new List<string>();
        // 与缓存的模型句柄一一对应：该模板是否带杂乱区域（LoadModel 时读出）
        private bool[] _loadedClutter = new bool[0];

        public HalconGenericShapeMatchTool(string moduleName) : base(moduleName)
        {
        }

        protected override string MatchLabel
        {
            get { return "通用形状匹配"; }
        }

        protected override string MissingModelMessage
        {
            get { return "通用形状模板未创建（请在编辑窗口中添加并训练模板）"; }
        }

        /// <summary>缓存键为 ModelsData 的引用：示教/导入整体替换数组并触发重新加载（与其他匹配工具相同）。</summary>
        protected override object CurrentModelKey
        {
            get { return ModelsData != null && ModelsData.Length > 0 ? ModelsData : null; }
        }

        /// <summary>模板名称（按模板顺序，来自 ModelsData）；数据格式错误时为空列表。</summary>
        public IReadOnlyList<string> TemplateNames
        {
            get
            {
                if (!ReferenceEquals(_namesSource, ModelsData))
                {
                    GenericShapeModelData.TryReadNames(ModelsData, out List<string> names, out _);
                    _names = names;
                    _namesSource = ModelsData;
                }
                return _names;
            }
        }

        /// <summary>缩放参数在示教页按缩放方式显示：等比只显示 IsoScale*，各向异性只显示 ScaleRow* / ScaleColumn*。</summary>
        public static bool IsScaleParameterVisible(ScaleMode mode, string propertyName)
        {
            if (propertyName == nameof(IsoScaleMin) || propertyName == nameof(IsoScaleMax))
            {
                return mode == ScaleMode.Isotropic;
            }
            if (propertyName == nameof(ScaleRowMin) || propertyName == nameof(ScaleRowMax)
                || propertyName == nameof(ScaleColumnMin) || propertyName == nameof(ScaleColumnMax))
            {
                return mode == ScaleMode.Anisotropic;
            }
            return true;
        }

        public override bool IsParameterVisible(string propertyName)
        {
            if (propertyName == nameof(TemplateNamesCsv) || TrainingParameters.Contains(propertyName))
            {
                return false;
            }
            if (propertyName == nameof(MaxClutter) || propertyName == nameof(ClutterContrast))
            {
                return UseClutter;
            }
            return base.IsParameterVisible(propertyName);
        }

        protected override IEnumerable<ToolConfigurationIssue> CheckModelConfiguration()
        {
            if (!GenericShapeModelData.TryReadNames(ModelsData, out List<string> names, out string error))
            {
                yield return new ToolConfigurationIssue(nameof(ModelsData), error);
            }
            else
            {
                if (names.Any(string.IsNullOrWhiteSpace))
                {
                    yield return new ToolConfigurationIssue(nameof(ModelsData), "模板名称不能为空");
                }
                if (names.Any(n => n != null && n.Contains(',')))
                {
                    yield return new ToolConfigurationIssue(nameof(ModelsData), "模板名称不能包含逗号");
                }
                string duplicate = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
                if (duplicate != null)
                {
                    yield return new ToolConfigurationIssue(nameof(ModelsData), $"模板名称“{duplicate}”重复");
                }
                string expected = string.Join(",", names);
                if (!string.IsNullOrEmpty(TemplateNamesCsv) && TemplateNamesCsv != expected)
                {
                    yield return new ToolConfigurationIssue(nameof(TemplateNamesCsv),
                        $"模板名称 CSV 与模型数据不一致（模型数据中为“{expected}”），请在编辑窗口中修改模板名称");
                }
            }
            if (AngleEnd < AngleStart)
            {
                yield return new ToolConfigurationIssue(nameof(AngleEnd), "角度范围终点不能小于起点");
            }
            if (TimeoutMs < 0)
            {
                yield return new ToolConfigurationIssue(nameof(TimeoutMs), "超时不能小于 0（0 表示不限制）");
            }
            if (MaxDeformation < 0)
            {
                yield return new ToolConfigurationIssue(nameof(MaxDeformation), "最大变形量不能小于 0");
            }
            if (UseClutter && (MaxClutter < 0 || MaxClutter > 1))
            {
                yield return new ToolConfigurationIssue(nameof(MaxClutter), "最大杂乱比例必须在 0 到 1 之间");
            }
            if (ClutterContrast < 0)
            {
                yield return new ToolConfigurationIssue(nameof(ClutterContrast), "杂乱对比度不能小于 0（0 表示使用模型默认值）");
            }
        }

        protected override HTuple LoadModel()
        {
            List<GenericShapeTemplate> templates = GenericShapeModelData.Unpack(ModelsData);
            var handles = new HTuple();
            var clutter = new bool[templates.Count];
            try
            {
                for (int i = 0; i < templates.Count; i++)
                {
                    HTuple handle = GenericShapeTraining.Deserialize(templates[i].ModelData);
                    handles = handles.TupleConcat(handle);
                    // 模板序号作为模型标识（字符串），结果按 model_identifier 区分来源
                    HOperatorSet.SetGenericShapeModelParam(handle, "model_identifier", i.ToString(CultureInfo.InvariantCulture));
                    if (GenericShapeTraining.TryGetClutterRegion(handle, out HObject region, out _))
                    {
                        region.Dispose();
                        clutter[i] = true;
                    }
                }
                _loadedClutter = clutter;
                return handles;
            }
            catch
            {
                ClearModel(handles);
                throw;
            }
        }

        protected override void ClearModel(HTuple model)
        {
            if (model == null)
            {
                return;
            }
            for (int i = 0; i < model.Length; i++)
            {
                HOperatorSet.ClearHandle(model.TupleSelect(i));
            }
        }

        protected override void FindMatches(FlowContext ctx, HObject image, HTuple models, List<MatchResultItem> items)
        {
            IReadOnlyList<string> names = TemplateNames;
            if (UseClutter)
            {
                // HALCON 22.11：同一次查找的全部模型杂乱判定须一致，没有杂乱区域的模型不能开启（#8516 / #8517）
                string[] missing = Enumerable.Range(0, models.Length)
                    .Where(i => i >= _loadedClutter.Length || !_loadedClutter[i])
                    .Select(i => i < names.Count ? "“" + names[i] + "”" : "第 " + i + " 个模板")
                    .ToArray();
                if (missing.Length > 0)
                {
                    throw new InvalidOperationException($"启用杂乱判定时每个模板都必须设置杂乱区域（同一次查找的全部模板须一致），{string.Join("、", missing)}没有杂乱区域");
                }
            }
            for (int i = 0; i < models.Length; i++)
            {
                ApplySearchParams(models.TupleSelect(i));
            }

            HTuple result;
            HTuple count;
            try
            {
                HOperatorSet.FindGenericShapeModel(image, models, out result, out count);
            }
            catch (HalconException ex) when (ex.GetErrorCode() == 9400)
            {
                throw new InvalidOperationException($"查找超时（超过 {TimeoutMs} ms）", ex);
            }
            catch (HalconException ex) when (ex.GetErrorCode() == 8673)
            {
                throw new InvalidOperationException("模板需要重新训练（训练后修改了建模参数）", ex);
            }

            try
            {
                HomMat2D baseInverse = HomMat2D.FromPoses(0, 0, 0, BaseRow, BaseColumn, BaseAngle).Inverted();
                for (int i = 0; i < count.I; i++)
                {
                    double row = Result(result, i, "row");
                    double column = Result(result, i, "column");
                    double angle = Result(result, i, "angle");
                    double scaleRow = Result(result, i, "scale_row");
                    double scaleColumn = Result(result, i, "scale_column");
                    double score = Result(result, i, "score");
                    HOperatorSet.GetGenericShapeModelResult(result, i, "model_identifier", out HTuple identifier);
                    HOperatorSet.GetGenericShapeModelResult(result, i, "hom_mat_2d", out HTuple homMat);
                    HOperatorSet.GetGenericShapeModelResultObject(out HObject contour, result, i, "contours");
                    string identifierText = identifier.Type == HTupleType.STRING ? identifier.S : identifier.ToString();
                    int modelIndex = int.TryParse(identifierText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                        ? parsed : -1;
                    // 完整变换矩阵（含各向异性缩放）；基准位姿为 0 时 baseInverse 为单位矩阵
                    HOperatorSet.HomMat2dCompose(homMat, baseInverse.Data, out HTuple follow);
                    MatchResultItem item = CreateItem(ctx, i, row, column, angle, (scaleRow + scaleColumn) / 2.0, score, contour, new HomMat2D(follow));
                    item.ScaleRow = scaleRow;
                    item.ScaleColumn = scaleColumn;
                    item.ModelIndex = modelIndex;
                    item.ModelName = modelIndex >= 0 && modelIndex < names.Count ? names[modelIndex] : null;
                    items.Add(item);
                }
            }
            finally
            {
                HOperatorSet.ClearHandle(result);
            }
        }

        /// <summary>查找参数（全局，对所有模板相同）写入模型句柄；训练后修改不需要重新训练（22.11 实测）。</summary>
        private void ApplySearchParams(HTuple model)
        {
            HOperatorSet.SetGenericShapeModelParam(model, "angle_start", AngleStart);
            HOperatorSet.SetGenericShapeModelParam(model, "angle_end", AngleEnd);
            HOperatorSet.SetGenericShapeModelParam(model, "min_score", MinScore);
            HOperatorSet.SetGenericShapeModelParam(model, "num_matches", NumMatches > 0 ? new HTuple(NumMatches) : new HTuple("all"));
            HOperatorSet.SetGenericShapeModelParam(model, "max_overlap", MaxOverlap);
            HOperatorSet.SetGenericShapeModelParam(model, "greediness", Greediness);
            HOperatorSet.SetGenericShapeModelParam(model, "subpixel", string.IsNullOrWhiteSpace(SubPixel) ? "least_squares" : SubPixel);
            HOperatorSet.SetGenericShapeModelParam(model, "border_shape_models", BorderShapeModels ? "true" : "false");
            HOperatorSet.SetGenericShapeModelParam(model, "max_deformation", MaxDeformation);
            // timeout 单位为毫秒（22.11 实测：50 → 约 50 ms 后报 #9400）；"false" 表示不限制
            HOperatorSet.SetGenericShapeModelParam(model, "timeout", TimeoutMs > 0 ? new HTuple(TimeoutMs) : new HTuple("false"));
            HOperatorSet.SetGenericShapeModelParam(model, "use_clutter", UseClutter ? "true" : "false");
            if (UseClutter)
            {
                HOperatorSet.SetGenericShapeModelParam(model, "max_clutter", MaxClutter);
                if (ClutterContrast > 0)
                {
                    HOperatorSet.SetGenericShapeModelParam(model, "clutter_contrast", ClutterContrast);
                }
            }
        }

        private static double Result(HTuple result, int index, string name)
        {
            HOperatorSet.GetGenericShapeModelResult(result, index, name, out HTuple value);
            return value.D;
        }

        protected override void WriteExtraOutputs(FlowContext ctx, List<MatchResultItem> items, MatchResultItem best)
        {
            IReadOnlyList<string> names = TemplateNames;
            SetOutput(ctx, Variable.Array(ModuleName, "ScaleRows", VariableType.Double, items.Select(i => i.ScaleRow)));
            SetOutput(ctx, Variable.Array(ModuleName, "ScaleColumns", VariableType.Double, items.Select(i => i.ScaleColumn)));
            SetOutput(ctx, Variable.Array(ModuleName, "ModelIndices", VariableType.Int, items.Select(i => i.ModelIndex)));
            SetOutput(ctx, Variable.Array(ModuleName, "ModelNames", VariableType.String, items.Select(i => i.ModelName ?? string.Empty)));
            SetOutput(ctx, Variable.Single(ModuleName, "BestModelIndex", VariableType.Int, best?.ModelIndex ?? -1));
            SetOutput(ctx, Variable.Single(ModuleName, "BestModelName", VariableType.String, best?.ModelName ?? string.Empty));
            SetOutput(ctx, Variable.Array(ModuleName, "CountsPerModel", VariableType.Int,
                Enumerable.Range(0, names.Count).Select(m => items.Count(i => i.ModelIndex == m))));
        }
    }
}
