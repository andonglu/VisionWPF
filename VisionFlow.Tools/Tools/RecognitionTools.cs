using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>读码方式：一维码（每次运行新建模型）或二维码（模型按工具实例缓存，可训练后序列化保存）。</summary>
    public enum CodeKind
    {
        /// <summary>一维码：create_bar_code_model / find_bar_code（原有行为）。</summary>
        Barcode,
        /// <summary>二维码：create_data_code_2d_model / find_data_code_2d。</summary>
        DataCode2D
    }

    [ToolOutput("Codes", VariableKind.Array, VariableType.String)]
    [ToolOutput("FirstCode", VariableKind.Single, VariableType.String)]
    [ToolOutput("Region", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("CodeTypes", VariableKind.Array, VariableType.String)]
    [ToolOutput("SymbolContours", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Grades", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("FirstGrade", VariableKind.Single, VariableType.Double)]
    public sealed class Barcode1DTool : ToolBase, INotFoundPolicy, IToolResourceLifecycle, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("ROI区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        /// <summary>一维码码制（HALCON 条码类型名），默认 auto。</summary>
        public string CodeType { get; set; } = "auto";

        /// <summary>读码方式：一维码（默认，原有行为）或二维码。</summary>
        public CodeKind CodeKind { get; set; } = CodeKind.Barcode;

        /// <summary>二维码码制（取值即 HALCON 码制名称，如 QR Code / Data Matrix ECC 200 / PDF417）。</summary>
        public string DataCodeType { get; set; } = "QR Code";

        /// <summary>二维码识别强度（create_data_code_2d_model 的 default_parameters）。</summary>
        public string RecognitionLevel { get; set; } = "standard_recognition";

        /// <summary>最多读取码数：0 表示全部；大于 0 时找到该数量后停止。</summary>
        public int MaxCodes { get; set; }

        /// <summary>输出码质量评级（一维码 ISO/IEC 15416，二维码 ISO/IEC 15415）；默认 false 时 Grades 为空、FirstGrade 为 NaN。</summary>
        public bool GradeQuality { get; set; }

        /// <summary>训练后的二维码模型序列化数据（serialize_data_code_2d_model）；非空时优先按此反序列化模型，变化时重建。</summary>
        public byte[] DataCodeModelData { get; set; }

        /// <summary>未识别到条码时是否失败（默认 true）；关闭后输出 Found=false、Count=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        private static readonly string[] RecognitionLevels = { "standard_recognition", "enhanced_recognition", "maximum_recognition" };

        /// <summary>
        /// 高级模型参数（自由键值对，可空 = 不设置）：每行一条「名称=值」，空行与 # 开头注释行跳过。
        /// 两种读码方式共用：Barcode → set_bar_code_param（每次运行新建模型后应用）；
        /// DataCode2D → set_data_code_2d_param（模型创建/反序列化后、进缓存前应用，并纳入缓存键）。
        /// 值类型自动推断：整数按 int、含小数点/e 按 double，其余按字符串。
        /// </summary>
        public string ModelParams { get; set; }

        /// <summary>高级参数的一条解析结果。</summary>
        private readonly struct ModelParamEntry
        {
            public readonly string Name;
            public readonly HTuple Value;
            public readonly string RawText;
            public readonly int LineNumber;

            public ModelParamEntry(string name, HTuple value, string rawText, int lineNumber)
            {
                Name = name;
                Value = value;
                RawText = rawText;
                LineNumber = lineNumber;
            }
        }

        /// <summary>MaxCodes=0（读取全部）时传给 find_data_code_2d 的 stop_after_result_num 上限；查找会在搜完所有候选后自然结束。</summary>
        private const int AllCodesStopCount = 999;

        // 二维码模型创建较慢且保存本次结果，按工具实例缓存句柄（参照 HalconMatchToolBase）；
        // 键 = 码制 + 识别强度 + 训练数据引用，任一变化时释放旧句柄并重建
        private readonly object _modelSync = new object();
        private HTuple _cachedDataCodeModel;
        private object _cachedDataCodeModelKey;

        public Barcode1DTool(string moduleName) : base(moduleName)
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
            HObject runImage = image;
            bool ownsReduced = false;
            if (!string.IsNullOrWhiteSpace(RegionPath))
            {
                HObject region = Input<HalconRegion>(ctx, RegionPath).Object;
                HOperatorSet.ReduceDomain(image, region, out runImage);
                ownsReduced = true;
            }

            try
            {
                if (CodeKind == CodeKind.DataCode2D)
                {
                    return RunDataCode2d(ctx, runImage);
                }
                return RunBarcode(ctx, runImage);
            }
            finally
            {
                if (ownsReduced)
                {
                    runImage.Dispose();
                }
            }
        }

        private NodeResult RunBarcode(FlowContext ctx, HObject runImage)
        {
            // 条码模型会保存本次识别结果，不跨运行复用（创建耗时约 0.1 ms），避免并发运行互相覆盖
            HTuple handle = null;
            try
            {
                HOperatorSet.CreateBarCodeModel(new HTuple(), new HTuple(), out handle);
                try
                {
                    if (GradeQuality)
                    {
                        // 质量查询（quality_isoiec15416）需要模型保留结果
                        HOperatorSet.SetBarCodeParam(handle, "persistence", 1);
                    }
                    if (MaxCodes > 0)
                    {
                        HOperatorSet.SetBarCodeParam(handle, "stop_after_result_num", MaxCodes);
                    }
                    // 高级参数最后应用，可覆盖内置参数（如 stop_after_result_num）
                    ApplyBarcodeModelParams(handle);
                }
                catch (HalconException ex)
                {
                    return NodeResult.Fail($"{ModuleName} 一维码参数设置失败：{ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    return NodeResult.Fail($"{ModuleName} {ex.Message}");
                }
                HOperatorSet.FindBarCode(runImage, out HObject symbolRegions, handle, CodeType, out HTuple codes);
                var strings = new List<string>();
                for (int i = 0; i < codes.Length; i++)
                {
                    strings.Add(codes[i].S);
                }
                var codeTypes = new List<string>(strings.Count);
                var grades = new List<double>();
                for (int i = 0; i < strings.Count; i++)
                {
                    codeTypes.Add(QueryBarcodeType(handle, i));
                    if (GradeQuality)
                    {
                        grades.Add(QueryBarcodeGrade(handle, i));
                    }
                }
                HOperatorSet.GenEmptyObj(out HObject emptyContours);
                WriteOutputs(ctx, strings, new HalconRegion(symbolRegions), codeTypes, new HalconXld(emptyContours), grades);
                if (strings.Count == 0)
                {
                    return NotFoundOutcome.Resolve(ctx, this, "未识别到一维码");
                }
                ctx.AddLog(FlowLogLevel.Info, $"[读码] 一维码 识别数量={strings.Count}（码制 {CodeType}）");
                return NodeResult.Ok;
            }
            finally
            {
                if (handle != null)
                {
                    HOperatorSet.ClearBarCodeModel(handle);
                }
            }
        }

        private NodeResult RunDataCode2d(FlowContext ctx, HObject runImage)
        {
            // 模型按实例缓存且保存本次结果，同一实例的并发运行串行执行，避免结果被并发覆盖
            lock (_modelSync)
            {
                HTuple model;
                try
                {
                    model = EnsureCachedDataCodeModel(ctx);
                }
                catch (Exception ex)
                {
                    return NodeResult.Fail($"{ModuleName} 二维码模型加载失败：{ex.Message}");
                }

                HObject symbolXlds = null;
                HObject symbolRegions = null;
                try
                {
                    HTuple names = new HTuple();
                    HTuple values = new HTuple();
                    // find_data_code_2d 不传 stop_after_result_num 时解码 1 个即停（HALCON 文档明确、22.11 实测），
                    // 所以“读取全部”必须显式给一个足够大的上限；传 0 会得到空结果
                    names = names.TupleConcat("stop_after_result_num");
                    values = values.TupleConcat(MaxCodes > 0 ? MaxCodes : AllCodesStopCount);
                    HOperatorSet.FindDataCode2d(runImage, out symbolXlds, model, names, values, out HTuple resultHandles, out HTuple decoded);
                    HOperatorSet.GenRegionContourXld(symbolXlds, out symbolRegions, "filled");
                    var strings = new List<string>();
                    for (int i = 0; i < decoded.Length; i++)
                    {
                        strings.Add(decoded[i].S);
                    }
                    var codeTypes = new List<string>(strings.Count);
                    var grades = new List<double>();
                    for (int i = 0; i < strings.Count; i++)
                    {
                        codeTypes.Add(QueryDataCodeType(model, resultHandles, i));
                        if (GradeQuality)
                        {
                            grades.Add(QueryDataCodeGrade(model, resultHandles, i));
                        }
                    }
                    WriteOutputs(ctx, strings, new HalconRegion(symbolRegions), codeTypes, new HalconXld(symbolXlds), grades);
                    // 所有权已移交给输出，本次运行不再释放
                    symbolRegions = null;
                    symbolXlds = null;
                    if (strings.Count == 0)
                    {
                        return NotFoundOutcome.Resolve(ctx, this, "未识别到二维码");
                    }
                    ctx.AddLog(FlowLogLevel.Info, $"[读码] 二维码 识别数量={strings.Count}（码制 {DataCodeType}）");
                    return NodeResult.Ok;
                }
                catch (Exception ex)
                {
                    return NodeResult.Fail($"{ModuleName} 二维码识别失败：{ex.Message}");
                }
                finally
                {
                    symbolRegions?.Dispose();
                    symbolXlds?.Dispose();
                }
            }
        }

        private void WriteOutputs(FlowContext ctx, List<string> strings, HalconRegion region,
            List<string> codeTypes, HalconXld symbolContours, List<double> grades)
        {
            SetOutput(ctx, Variable.Array(ModuleName, "Codes", VariableType.String, strings));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstCode", VariableType.String, strings.Count > 0 ? strings[0] : string.Empty));
            SetOutput(ctx, Variable.Object(ModuleName, "Region", region, strings.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, strings.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, strings.Count > 0));
            SetOutput(ctx, Variable.Array(ModuleName, "CodeTypes", VariableType.String, codeTypes));
            SetOutput(ctx, Variable.Object(ModuleName, "SymbolContours", symbolContours, strings.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Grades", VariableType.Double, grades));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstGrade", VariableType.Double, grades.Count > 0 ? grades[0] : double.NaN));
        }

        /// <summary>每个码的实际码制：优先按结果查询，查询名不存在时回退为模型/配置的码制。</summary>
        private string QueryBarcodeType(HTuple handle, int index)
        {
            try
            {
                HOperatorSet.GetBarCodeResult(handle, index, "decoded_types", out HTuple types);
                return types.Length > 0 ? types[0].S : CodeType;
            }
            catch (HalconException)
            {
                return CodeType;
            }
        }

        private static double QueryBarcodeGrade(HTuple handle, int index)
        {
            try
            {
                HOperatorSet.GetBarCodeResult(handle, index, "quality_isoiec15416", out HTuple quality);
                return QualityValue(quality, 0);
            }
            catch (HalconException)
            {
                return double.NaN;
            }
        }

        private string QueryDataCodeType(HTuple model, HTuple resultHandles, int index)
        {
            try
            {
                HOperatorSet.GetDataCode2dResults(model, resultHandles[index], "symbology", out HTuple symbology);
                return symbology.Length > 0 ? symbology[0].S : CurrentSymbolType(model) ?? DataCodeType;
            }
            catch (HalconException)
            {
                return CurrentSymbolType(model) ?? DataCodeType;
            }
        }

        private static double QueryDataCodeGrade(HTuple model, HTuple resultHandles, int index)
        {
            try
            {
                HOperatorSet.GetDataCode2dResults(model, resultHandles[index], "quality_isoiec15415", out HTuple quality);
                return QualityValue(quality, 0);
            }
            catch (HalconException)
            {
                return double.NaN;
            }
        }

        /// <summary>质量查询元组第 1 项为总体等级（0~4，F~A）；'N/A' 或查询失败时为 NaN。</summary>
        private static double QualityValue(HTuple tuple, int index)
        {
            if (tuple == null || tuple.Length <= index)
            {
                return double.NaN;
            }
            HTuple item = tuple[index];
            return item.Type == HTupleType.STRING ? double.NaN : item.D;
        }

        private static string CurrentSymbolType(HTuple model)
        {
            try
            {
                HOperatorSet.GetDataCode2dParam(model, "symbol_type", out HTuple symbolType);
                return symbolType.Length > 0 ? symbolType[0].S : null;
            }
            catch (HalconException)
            {
                return null;
            }
        }

        /// <summary>
        /// 用当前图像训练二维码模型（编辑窗口“用当前图像训练”）：以 find 参数 'train'='all' 让模型适应样本，
        /// 训练结果序列化保存到 <see cref="DataCodeModelData"/>。训练数据变化后缓存键随之更新（句柄即刚训练的模型）。
        /// </summary>
        public void TrainDataCodeModel(FlowContext ctx, HObject image)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }
            TrainDataCodeModel(ctx, new[] { image });
        }

        /// <summary>
        /// 用多张图像依次训练二维码模型：在同一模型句柄上逐张 find('train','all') 累积训练，
        /// 全部完成后只序列化一次并保存到 <see cref="DataCodeModelData"/>。
        /// </summary>
        public void TrainDataCodeModel(FlowContext ctx, IEnumerable<HObject> images)
        {
            if (images == null)
            {
                throw new ArgumentNullException(nameof(images));
            }
            lock (_modelSync)
            {
                HTuple model = EnsureCachedDataCodeModel(ctx);
                int count = 0;
                foreach (HObject image in images)
                {
                    if (image == null)
                    {
                        continue;
                    }
                    HOperatorSet.FindDataCode2d(image, out HObject symbolXlds, model, "train", "all", out HTuple _, out HTuple _);
                    symbolXlds.Dispose();
                    count++;
                }
                if (count == 0)
                {
                    throw new ArgumentException("训练图像列表为空", nameof(images));
                }
                DataCodeModelData = SerializeDataCode2dModel(model);
                _cachedDataCodeModelKey = CurrentDataCodeModelKey;
                ctx?.AddLog(FlowLogLevel.Info,
                    $"[读码] 二维码模型已用 {count} 张图像训练（{DataCodeType}），训练数据 {DataCodeModelData.Length} 字节；重新训练会替换现有训练数据");
            }
        }

        private object CurrentDataCodeModelKey
        {
            get { return (DataCodeType ?? string.Empty, RecognitionLevel ?? string.Empty, ModelParams ?? string.Empty, DataCodeModelData); }
        }

        /// <summary>
        /// 取得缓存的二维码模型句柄：缓存键不变时复用，变化时释放旧句柄并重新创建/反序列化。
        /// 调用方须持有 _modelSync。
        /// </summary>
        private HTuple EnsureCachedDataCodeModel(FlowContext ctx)
        {
            object key = CurrentDataCodeModelKey;
            if (_cachedDataCodeModel != null && Equals(_cachedDataCodeModelKey, key))
            {
                return _cachedDataCodeModel;
            }
            ReleaseCachedDataCodeModel();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _cachedDataCodeModel = LoadDataCodeModel();
            _cachedDataCodeModelKey = key;
            ctx?.AddLog(FlowLogLevel.Info,
                $"[读码] 二维码模型加载耗时 {watch.ElapsedMilliseconds} ms（已缓存，码制/识别强度/训练数据变化时重建；可通过预热提前加载）");
            return _cachedDataCodeModel;
        }

        private HTuple LoadDataCodeModel()
        {
            HTuple handle;
            if (DataCodeModelData != null && DataCodeModelData.Length > 0)
            {
                handle = DeserializeDataCode2dModel(DataCodeModelData);
            }
            else
            {
                HOperatorSet.CreateDataCode2dModel(
                    string.IsNullOrWhiteSpace(DataCodeType) ? "QR Code" : DataCodeType,
                    "default_parameters",
                    string.IsNullOrWhiteSpace(RecognitionLevel) ? "standard_recognition" : RecognitionLevel,
                    out handle);
            }
            // 高级参数统一在建模/反序列化后、进缓存前应用；句柄随缓存键隔离，失败时释放句柄再上抛
            try
            {
                ApplyDataCodeModelParams(handle);
            }
            catch
            {
                HOperatorSet.ClearDataCode2dModel(handle);
                throw;
            }
            return handle;
        }

        private void ReleaseCachedDataCodeModel()
        {
            if (_cachedDataCodeModel != null)
            {
                HOperatorSet.ClearDataCode2dModel(_cachedDataCodeModel);
                _cachedDataCodeModel = null;
                _cachedDataCodeModelKey = null;
            }
        }

        /// <summary>预热：按当前码制/识别强度/训练数据加载并缓存二维码模型；一维码每次运行新建模型，无需预热。</summary>
        public void Prepare()
        {
            if (CodeKind != CodeKind.DataCode2D)
            {
                return;
            }
            lock (_modelSync)
            {
                EnsureCachedDataCodeModel(null);
            }
        }

        /// <summary>释放缓存的二维码模型句柄；正在运行的识别结束后才会释放。</summary>
        public void ReleaseResources()
        {
            lock (_modelSync)
            {
                ReleaseCachedDataCodeModel();
            }
        }

        /// <summary>
        /// 解析高级参数：每行一条「名称=值」（等号两侧去空白），空行与 # 开头注释行跳过。
        /// 格式非法时 error 含行号与原始内容。值类型推断：整数 → int，含小数点/e → double，其余字符串。
        /// </summary>
        private bool TryParseModelParams(out List<ModelParamEntry> entries, out string error)
        {
            entries = new List<ModelParamEntry>();
            error = null;
            if (string.IsNullOrWhiteSpace(ModelParams))
            {
                return true;
            }
            string[] lines = ModelParams.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    error = $"高级参数第 {i + 1} 行「{line}」缺少等号：应为 名称=值";
                    return false;
                }
                string name = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (name.Length == 0)
                {
                    error = $"高级参数第 {i + 1} 行「{line}」名称为空：应为 名称=值";
                    return false;
                }
                entries.Add(new ModelParamEntry(name, InferParamValue(value), line, i + 1));
            }
            return true;
        }

        /// <summary>值类型推断：整数按 int，含小数点/e 的数值按 double，其余按字符串（探测：数值参数传字符串报 #1203）。</summary>
        private static HTuple InferParamValue(string text)
        {
            if (int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int intValue))
            {
                return new HTuple(intValue);
            }
            if ((text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0)
                && double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double doubleValue))
            {
                return new HTuple(doubleValue);
            }
            return new HTuple(text);
        }

        /// <summary>一维码高级参数：每次运行新建模型后应用（set_bar_code_param）。格式错误已在 CheckConfiguration 拦截，此处不再处理。</summary>
        private void ApplyBarcodeModelParams(HTuple handle)
        {
            if (string.IsNullOrWhiteSpace(ModelParams))
            {
                return;
            }
            if (!TryParseModelParams(out List<ModelParamEntry> entries, out _))
            {
                return;
            }
            foreach (ModelParamEntry entry in entries)
            {
                try
                {
                    HOperatorSet.SetBarCodeParam(handle, entry.Name, entry.Value);
                }
                catch (HalconException ex)
                {
                    throw new InvalidOperationException(BuildModelParamError(entry, ex, "一维码"), ex);
                }
            }
        }

        /// <summary>二维码高级参数：模型创建/反序列化后应用（set_data_code_2d_param）。格式错误已在 CheckConfiguration 拦截，此处不再处理。</summary>
        private void ApplyDataCodeModelParams(HTuple model)
        {
            if (string.IsNullOrWhiteSpace(ModelParams))
            {
                return;
            }
            if (!TryParseModelParams(out List<ModelParamEntry> entries, out _))
            {
                return;
            }
            foreach (ModelParamEntry entry in entries)
            {
                try
                {
                    HOperatorSet.SetDataCode2dParam(model, entry.Name, entry.Value);
                }
                catch (HalconException ex)
                {
                    throw new InvalidOperationException(BuildModelParamError(entry, ex, "二维码"), ex);
                }
            }
        }

        /// <summary>参数应用失败的中文错误：含行号、原文与 HALCON 错误码；#8831 为参数名不支持，#8835/#1203 为参数值非法。</summary>
        private static string BuildModelParamError(ModelParamEntry entry, HalconException ex, string kind)
        {
            int code = ex.GetErrorCode();
            string hint = code == 8831
                ? "：该码制/模型不支持此参数名"
                : code == 8835 || code == 1203 ? "：参数值非法" : string.Empty;
            return $"高级参数第 {entry.LineNumber} 行「{entry.RawText}」设置失败（{kind}）：HALCON 错误 #{code}{hint}";
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (MaxCodes < 0)
            {
                yield return new ToolConfigurationIssue(nameof(MaxCodes), "读取数量不能小于 0（0 表示读取全部）");
            }
            if (!string.IsNullOrWhiteSpace(ModelParams)
                && !TryParseModelParams(out _, out string modelParamsError))
            {
                yield return new ToolConfigurationIssue(nameof(ModelParams), modelParamsError);
            }
            if (CodeKind == CodeKind.DataCode2D)
            {
                if (string.IsNullOrWhiteSpace(DataCodeType))
                {
                    yield return new ToolConfigurationIssue(nameof(DataCodeType), "二维码码制不能为空");
                }
                if (!string.IsNullOrWhiteSpace(RecognitionLevel) && Array.IndexOf(RecognitionLevels, RecognitionLevel) < 0)
                {
                    yield return new ToolConfigurationIssue(nameof(RecognitionLevel),
                        $"识别强度必须是 {string.Join(" / ", RecognitionLevels)}");
                }
            }
        }

        /// <summary>按读码方式显隐参数：一维码码制只在 Barcode 下显示，二维码码制/识别强度/训练数据只在 DataCode2D 下显示。</summary>
        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(CodeType):
                    return CodeKind == CodeKind.Barcode;
                case nameof(DataCodeType):
                case nameof(RecognitionLevel):
                case nameof(DataCodeModelData):
                    return CodeKind == CodeKind.DataCode2D;
                default:
                    return true;
            }
        }

        private static byte[] SerializeDataCode2dModel(HTuple handle)
        {
            HOperatorSet.SerializeDataCode2dModel(handle, out HTuple item);
            try
            {
                HOperatorSet.GetSerializedItemPtr(item, out HTuple pointer, out HTuple size);
                var bytes = new byte[size.I];
                Marshal.Copy(new IntPtr(pointer.L), bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                HOperatorSet.ClearSerializedItem(item);
            }
        }

        private static HTuple DeserializeDataCode2dModel(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("二维码模型训练数据为空");
            }
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            HTuple item = null;
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                HOperatorSet.CreateSerializedItemPtr(new HTuple(pointer.ToInt64()), bytes.Length, "true", out item);
                HOperatorSet.DeserializeDataCode2dModel(item, out HTuple handle);
                return handle;
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

    /// <summary>字符识别引擎：传统文本模型（默认，无需深度学习授权）或 Deep OCR。</summary>
    public enum OcrEngine
    {
        /// <summary>create_text_model_reader('auto', 分类器) + find_text，自动分割字符。</summary>
        TextModel,
        /// <summary>create_deep_ocr + apply_deep_ocr，自动检测文字区域并识别。</summary>
        DeepOcr
    }

    /// <summary>一次识别的中间结果：按行分组的字符、置信度与（可选的）逐字符区域/文字框。</summary>
    internal sealed class OcrRecognition
    {
        public List<string> Lines = new List<string>();
        public List<string> Chars = new List<string>();
        public List<double> Confidences = new List<double>();
        public int CharCount;
        public HObject CharRegions;
        public HObject WordContours;
    }

    [ToolOutput("Text", VariableKind.Single, VariableType.String)]
    [ToolOutput("Lines", VariableKind.Array, VariableType.String)]
    [ToolOutput("LineCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Chars", VariableKind.Array, VariableType.String)]
    [ToolOutput("Confidences", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("MinConfidence", VariableKind.Single, VariableType.Double)]
    /// <summary>DeepOcr 引擎不输出逐字符框，CharRegions 为按词框沿长轴均分的<strong>近似</strong>字符区域，
    /// 仅用于定位参考，不可用于字符级几何测量；TextModel 引擎为算子分割的真实字符区域。</summary>
    [ToolOutput("CharRegions", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("WordContours", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("PatternOk", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    public sealed class OcrTool : ToolBase, INotFoundPolicy, IToolResourceLifecycle, IToolConfigurationCheck, IToolParameterVisibility
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("ROI区域", typeof(HalconRegion), Optional = true)]
        public string RegionPath { get; set; }

        /// <summary>
        /// 变换矩阵（可选，通常接模板匹配的 BestHomMat，即“示教位姿 → 当前位姿”）：
        /// ROI 按矩阵变换到当前位置，并按矩阵角度把 ROI 内图像旋转为水平后识别，
        /// 结果区域/轮廓再变换回原图坐标。只支持单个矩阵；多个工件请在循环中引用 Loop.Current.HomMat。
        /// </summary>
        [InputRef("变换矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        /// <summary>识别引擎：传统文本模型（默认）或 Deep OCR。</summary>
        public OcrEngine Engine { get; set; } = OcrEngine.TextModel;

        /// <summary>预训练字符分类器名称（create_text_model_reader 第二个参数），如 Universal_0-9A-Z_Rej.occ。</summary>
        public string Classifier { get; set; } = "Universal_0-9A-Z_Rej.occ";

        /// <summary>字符极性：dark_on_light（默认）/ light_on_dark / both。</summary>
        public string Polarity { get; set; } = "dark_on_light";

        /// <summary>喷码点阵字符（set_text_model_param 'dot_print'）。</summary>
        public bool DotPrint { get; set; }

        /// <summary>最小字符高度（像素）；0 或负数表示不设置。</summary>
        public double MinCharHeight { get; set; }

        /// <summary>最大字符高度（像素）；0 或负数表示不设置。</summary>
        public double MaxCharHeight { get; set; }

        /// <summary>最小笔画宽度（像素）；0 或负数表示不设置。</summary>
        public double MinStrokeWidth { get; set; }

        /// <summary>最大笔画宽度（像素）；0 或负数表示不设置。</summary>
        public double MaxStrokeWidth { get; set; }

        /// <summary>行分隔符（set_text_model_param 'text_line_separators'），可空表示用默认。</summary>
        public string TextLineSeparators { get; set; }

        /// <summary>允许出现的字符集，可空表示不过滤；不在字符集内的字符从结果中过滤掉（两引擎一致）。</summary>
        public string Alphabet { get; set; }

        /// <summary>Deep OCR 应用模式：auto（默认，检测+识别）/ recognition（已裁好的单行文字）。</summary>
        public string Mode { get; set; } = "auto";

        /// <summary>Deep OCR 运行设备：Cpu（默认）/ Gpu；22.11 上 set_deep_ocr_param 设备参数不可用，选 Gpu 时预热报中文错误。</summary>
        public string Device { get; set; } = "Cpu";

        /// <summary>Deep OCR 最低字符置信度：低于该值的字符从结果中过滤掉；0 表示不过滤。输出名 MinConfidence 已占用，参数故用此名。</summary>
        public double ConfidenceThreshold { get; set; }

        /// <summary>期望格式（可选，正则表达式，如 ^\d{8}$），只影响 PatternOk，不影响识别结果；可空。</summary>
        public string ExpectedPattern { get; set; }

        /// <summary>未识别到字符时是否失败（默认 true）；关闭后输出 Found=false 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        private static readonly string[] Polarities = { "dark_on_light", "light_on_dark", "both" };
        private static readonly string[] DeepOcrModes = { "auto", "recognition" };
        private static readonly string[] DeepOcrDevices = { "Cpu", "Gpu" };

        private const double AngleEpsilon = 1e-9;

        // 文本模型 reader 按实例缓存（键 = 分类器 + 相关参数），clear_text_model 释放；
        // Deep OCR 句柄按实例缓存（22.11 无 clear_deep_ocr、clear_dl_model 不接受 #2404，释放仅丢引用）
        private readonly object _sync = new object();
        private HTuple _cachedTextModel;
        private object _cachedTextModelKey;
        private HTuple _cachedDeepOcr;
        private object _cachedDeepOcrKey;

        public OcrTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }

            if (!FollowMatrixResolver.TryResolve(ctx, ModuleName, MatrixPath, out List<HomMat2D> matrices, out string matrixError))
            {
                return NodeResult.Fail(matrixError);
            }
            if (matrices.Count > 1)
            {
                return NodeResult.Fail($"{ModuleName} 的变换矩阵只支持单个矩阵（当前为 {matrices.Count} 个）；多个工件请放在 For 循环中引用 Loop.Current.HomMat");
            }
            HomMat2D matrix = matrices[0];

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HObject roi = null;
            if (!string.IsNullOrWhiteSpace(RegionPath))
            {
                roi = Input<HalconRegion>(ctx, RegionPath).Object;
            }

            var owned = new List<HObject>();
            OcrRecognition recognition;
            try
            {
                HObject runImage = BuildRunImage(image, roi, matrix, owned, out HTuple levelingInverse);

                lock (_sync)
                {
                    recognition = Engine == OcrEngine.DeepOcr
                        ? RunDeepOcr(ctx, runImage)
                        : RunTextModel(ctx, runImage);
                }

                if (levelingInverse != null)
                {
                    // 结果从水平化坐标系变换回原图坐标
                    HOperatorSet.AffineTransRegion(recognition.CharRegions, out HObject backRegions, levelingInverse, "nearest_neighbor");
                    recognition.CharRegions.Dispose();
                    recognition.CharRegions = backRegions;
                    HOperatorSet.AffineTransContourXld(recognition.WordContours, out HObject backContours, levelingInverse);
                    recognition.WordContours.Dispose();
                    recognition.WordContours = backContours;
                }
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 字符识别失败：{ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                // 句柄缓存加载失败（如分类器不存在、GPU 不可用），失败信息已含中文说明
                return NodeResult.Fail(ex.Message);
            }
            finally
            {
                foreach (HObject item in owned)
                {
                    item.Dispose();
                }
            }

            int filtered = recognition.CharCount - recognition.Chars.Count;
            string text = string.Join(Environment.NewLine, recognition.Lines);
            bool patternOk = true;
            if (!string.IsNullOrWhiteSpace(ExpectedPattern))
            {
                patternOk = System.Text.RegularExpressions.Regex.IsMatch(text, ExpectedPattern);
            }

            WriteOutputs(ctx, recognition, text, patternOk);
            if (recognition.Chars.Count == 0)
            {
                return NotFoundOutcome.Resolve(ctx, this, "未识别到字符");
            }
            string log = $"[字符识别] {Engine} 识别字符={recognition.Chars.Count} 行数={recognition.Lines.Count}";
            if (Engine == OcrEngine.TextModel)
            {
                log += $"（分类器 {Classifier}）";
            }
            if (filtered > 0)
            {
                log += $"；按字符集/置信度过滤 {filtered} 个字符";
            }
            ctx.AddLog(FlowLogLevel.Info, log);
            return NodeResult.Ok;
        }

        /// <summary>
        /// 构造识别用图像：无矩阵时 reduce_domain 限定 ROI（结果区域为原图坐标，探测结论 B5）；
        /// 有矩阵时 ROI 先按矩阵变换到当前位置，再按矩阵旋转角把图像旋转为水平（绕 ROI 中心，无 ROI 绕图像中心）。
        /// 返回识别图像；levelingInverse 非空表示结果需按该逆矩阵变换回原图坐标。
        /// </summary>
        private HObject BuildRunImage(HObject image, HObject roi, HomMat2D matrix,
            List<HObject> owned, out HTuple levelingInverse)
        {
            levelingInverse = null;
            HObject currentImage = image;
            HObject currentRoi = roi;
            if (matrix != null && currentRoi != null)
            {
                HOperatorSet.AffineTransRegion(currentRoi, out HObject followed, matrix.Data, "nearest_neighbor");
                owned.Add(followed);
                currentRoi = followed;
            }
            if (matrix != null)
            {
                double[] d = matrix.Data.DArr;
                double angle = Math.Atan2(d[3], d[0]);
                if (Math.Abs(angle) > AngleEpsilon)
                {
                    double centerRow, centerCol;
                    if (currentRoi != null)
                    {
                        HOperatorSet.Union1(currentRoi, out HObject union);
                        owned.Add(union);
                        HOperatorSet.AreaCenter(union, out HTuple area, out HTuple row, out HTuple col);
                        centerRow = row.D;
                        centerCol = col.D;
                    }
                    else
                    {
                        HOperatorSet.GetImageSize(currentImage, out HTuple width, out HTuple height);
                        centerRow = height.D / 2;
                        centerCol = width.D / 2;
                    }
                    // 文字相对示教方向偏了 angle，把图像绕中心反向旋转使其水平；
                    // 只取旋转分量 leveling，平移已由 ROI 变换覆盖，缩放分量不改变文字方向（已知边界）
                    HOperatorSet.HomMat2dIdentity(out HTuple leveling);
                    HOperatorSet.HomMat2dRotate(leveling, -angle, centerRow, centerCol, out leveling);
                    HOperatorSet.AffineTransImage(currentImage, out HObject rotated, leveling, "constant", "false");
                    owned.Add(rotated);
                    currentImage = rotated;
                    if (currentRoi != null)
                    {
                        HOperatorSet.AffineTransRegion(currentRoi, out HObject leveledRoi, leveling, "nearest_neighbor");
                        owned.Add(leveledRoi);
                        currentRoi = leveledRoi;
                    }
                    HOperatorSet.HomMat2dInvert(leveling, out levelingInverse);
                }
            }
            if (currentRoi != null)
            {
                HOperatorSet.ReduceDomain(currentImage, currentRoi, out HObject reduced);
                owned.Add(reduced);
                return reduced;
            }
            return currentImage;
        }

        private OcrRecognition RunTextModel(FlowContext ctx, HObject runImage)
        {
            HTuple reader;
            try
            {
                reader = EnsureCachedTextModel(ctx);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{ModuleName} 字符分类器加载失败：{ex.Message}", ex);
            }

            HOperatorSet.FindText(runImage, reader, out HTuple textResult);
            HObject charRegions = null;
            try
            {
                HOperatorSet.GetTextResult(textResult, "num_lines", out HTuple numLines);
                HOperatorSet.GetTextResult(textResult, "confidence", out HTuple confidences);
                HOperatorSet.GetTextObject(out charRegions, textResult, "all_lines");
                HOperatorSet.CountObj(charRegions, out HTuple regionCount);

                var recognition = new OcrRecognition { CharRegions = charRegions, CharCount = regionCount.I };
                charRegions = null; // 所有权移交 recognition
                var allKept = new List<int>();
                int globalIndex = 0;
                for (int line = 0; line < numLines.I; line++)
                {
                    HOperatorSet.GetTextResult(textResult, new HTuple("class_line").TupleConcat(line), out HTuple lineChars);
                    var keptChars = new List<string>();
                    for (int i = 0; i < lineChars.Length; i++)
                    {
                        double confidence = confidences.Length > globalIndex ? confidences[globalIndex].D : double.NaN;
                        if (KeepChar(lineChars[i].S))
                        {
                            allKept.Add(globalIndex);
                            keptChars.Add(lineChars[i].S);
                            recognition.Chars.Add(lineChars[i].S);
                            recognition.Confidences.Add(confidence);
                        }
                        globalIndex++;
                    }
                    recognition.Lines.Add(string.Concat(keptChars));
                }
                recognition.CharRegions = ReplaceRegionObjects(recognition.CharRegions, allKept, regionCount.I);
                HOperatorSet.GenEmptyObj(out HObject emptyContours);
                recognition.WordContours = emptyContours;
                return recognition;
            }
            finally
            {
                charRegions?.Dispose();
            }
        }

        private OcrRecognition RunDeepOcr(FlowContext ctx, HObject runImage)
        {
            HTuple ocr = EnsureCachedDeepOcr(ctx);
            HOperatorSet.ApplyDeepOcr(runImage, ocr, Mode, out HTuple resultDict);
            HOperatorSet.GetDictTuple(resultDict, "words", out HTuple words);
            HOperatorSet.GetDictTuple(words, "word", out HTuple wordArr);

            var recognition = new OcrRecognition();
            HOperatorSet.GenEmptyObj(out HObject charRegions);
            HOperatorSet.GenEmptyObj(out HObject wordContours);
            try
            {
                if (wordArr.Length == 0)
                {
                    recognition.CharRegions = charRegions;
                    recognition.WordContours = wordContours;
                    charRegions = null;
                    wordContours = null;
                    return recognition;
                }
                HOperatorSet.GetDictTuple(words, "line_index", out HTuple lineIndex);
                HOperatorSet.GetDictTuple(words, "col", out HTuple wordCol);
                HOperatorSet.GetDictTuple(words, "row", out HTuple wordRow);
                HOperatorSet.GetDictTuple(words, "phi", out HTuple wordPhi);
                HOperatorSet.GetDictTuple(words, "length1", out HTuple wordLength1);
                HOperatorSet.GetDictTuple(words, "length2", out HTuple wordLength2);
                HOperatorSet.GetDictTuple(words, "char_candidates", out HTuple charCandidates);

                // 行序：line_index 升序，行内按 col 升序（探测结论：结果本身按此序返回，此处固定排序保证稳定）
                int[] order = Enumerable.Range(0, wordArr.Length)
                    .OrderBy(i => lineIndex[i].I)
                    .ThenBy(i => wordCol[i].D)
                    .ToArray();

                var lines = new Dictionary<int, List<int>>();
                foreach (int i in order)
                {
                    if (!lines.TryGetValue(lineIndex[i].I, out List<int> wordIndices))
                    {
                        wordIndices = new List<int>();
                        lines[lineIndex[i].I] = wordIndices;
                    }
                    wordIndices.Add(i);
                }

                foreach (List<int> wordIndices in lines.Values)
                {
                    var lineChars = new List<string>();
                    foreach (int i in wordIndices)
                    {
                        string word = wordArr[i].S;
                        double[] confidences = QueryCharConfidences(charCandidates, i, word.Length);
                        // 逐字符区域/文字框由词的有向包围盒均分得到（Deep OCR 不输出逐字符框，均为近似值）
                        for (int c = 0; c < word.Length; c++)
                        {
                            recognition.CharCount++;
                            double confidence = c < confidences.Length ? confidences[c] : double.NaN;
                            if (!KeepChar(word[c].ToString()))
                            {
                                continue;
                            }
                            if (ConfidenceThreshold > 0 && confidence < ConfidenceThreshold)
                            {
                                continue;
                            }
                            lineChars.Add(word[c].ToString());
                            recognition.Chars.Add(word[c].ToString());
                            recognition.Confidences.Add(confidence);
                            AppendCharRegion(ref charRegions, wordRow[i].D, wordCol[i].D, wordPhi[i].D,
                                wordLength1[i].D, wordLength2[i].D, word.Length, c);
                        }
                        HOperatorSet.GenRectangle2ContourXld(out HObject box, wordRow[i].D, wordCol[i].D,
                            wordPhi[i].D, wordLength1[i].D, wordLength2[i].D);
                        HOperatorSet.ConcatObj(wordContours, box, out HObject combined);
                        wordContours.Dispose();
                        box.Dispose();
                        wordContours = combined;
                    }
                    recognition.Lines.Add(string.Concat(lineChars));
                }
                recognition.CharRegions = charRegions;
                recognition.WordContours = wordContours;
                charRegions = null;
                wordContours = null;
                return recognition;
            }
            finally
            {
                charRegions?.Dispose();
                wordContours?.Dispose();
            }
        }

        /// <summary>Deep OCR 逐字符置信度：words.char_candidates[词][0] 为逐字符候选字典，取每个字符第 1 候选的 confidence。</summary>
        private static double[] QueryCharConfidences(HTuple charCandidates, int wordIndex, int charCount)
        {
            var confidences = new double[charCount];
            for (int i = 0; i < charCount; i++)
            {
                confidences[i] = double.NaN;
            }
            try
            {
                if (charCandidates == null || charCandidates.Length <= wordIndex)
                {
                    return confidences;
                }
                HOperatorSet.GetDictTuple(charCandidates[wordIndex], new HTuple(0), out HTuple perChar);
                for (int c = 0; c < Math.Min(charCount, perChar.Length); c++)
                {
                    HOperatorSet.GetDictTuple(perChar[c], "confidence", out HTuple confidence);
                    if (confidence.Length > 0)
                    {
                        confidences[c] = confidence[0].D;
                    }
                }
            }
            catch (HalconException)
            {
                // 候选字典结构不可用时置信度保持 NaN，不影响识别结果
            }
            return confidences;
        }

        /// <summary>按词的有向包围盒（row/col/phi/length1/length2）沿长轴均分生成第 c 个字符的矩形区域并追加。</summary>
        private static void AppendCharRegion(ref HObject regions, double row, double col, double phi,
            double length1, double length2, int charCount, int charIndex)
        {
            double offset = (charIndex - (charCount - 1) / 2.0) * (2.0 * length1 / charCount);
            double charRow = row - offset * Math.Sin(phi);
            double charCol = col + offset * Math.Cos(phi);
            HOperatorSet.GenRectangle2(out HObject rectangle, charRow, charCol, phi, length1 / charCount, length2);
            HOperatorSet.ConcatObj(regions, rectangle, out HObject combined);
            regions.Dispose();
            rectangle.Dispose();
            regions = combined;
        }

        /// <summary>按保留序号从多对象区域中挑出对象重新组合（全部保留时原样返回）。</summary>
        private static HObject ReplaceRegionObjects(HObject regions, List<int> kept, int total)
        {
            if (kept.Count == total)
            {
                return regions;
            }
            HOperatorSet.GenEmptyObj(out HObject filtered);
            foreach (int index in kept)
            {
                HOperatorSet.SelectObj(regions, out HObject selected, index + 1);
                HOperatorSet.ConcatObj(filtered, selected, out HObject combined);
                filtered.Dispose();
                selected.Dispose();
                filtered = combined;
            }
            regions.Dispose();
            return filtered;
        }

        /// <summary>字符是否保留：Alphabet 非空时必须在其中（两引擎一致）；Deep OCR 的置信度阈值在结果组装处另行判断。</summary>
        private bool KeepChar(string ch)
        {
            return string.IsNullOrEmpty(Alphabet) || Alphabet.IndexOf(ch) >= 0;
        }

        private void WriteOutputs(FlowContext ctx, OcrRecognition recognition, string text, bool patternOk)
        {
            double minConfidence = recognition.Confidences.Count > 0 ? recognition.Confidences.Min() : double.NaN;
            HOperatorSet.CountObj(recognition.CharRegions, out HTuple regionCount);
            HOperatorSet.CountObj(recognition.WordContours, out HTuple contourCount);
            SetOutput(ctx, Variable.Single(ModuleName, "Text", VariableType.String, text));
            SetOutput(ctx, Variable.Array(ModuleName, "Lines", VariableType.String, recognition.Lines));
            SetOutput(ctx, Variable.Single(ModuleName, "LineCount", VariableType.Int, recognition.Lines.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Chars", VariableType.String, recognition.Chars));
            SetOutput(ctx, Variable.Array(ModuleName, "Confidences", VariableType.Double, recognition.Confidences));
            SetOutput(ctx, Variable.Single(ModuleName, "MinConfidence", VariableType.Double, minConfidence));
            SetOutput(ctx, Variable.Object(ModuleName, "CharRegions", new HalconRegion(recognition.CharRegions), regionCount.I));
            SetOutput(ctx, Variable.Object(ModuleName, "WordContours", new HalconXld(recognition.WordContours), contourCount.I));
            SetOutput(ctx, Variable.Single(ModuleName, "PatternOk", VariableType.Bool, patternOk));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, recognition.Chars.Count > 0));
        }

        private object CurrentTextModelKey =>
            (Classifier ?? string.Empty, Polarity ?? string.Empty, DotPrint,
                MinCharHeight, MaxCharHeight, MinStrokeWidth, MaxStrokeWidth, TextLineSeparators ?? string.Empty);

        /// <summary>取得缓存的文本模型 reader：键不变时复用，变化时 clear_text_model 释放旧句柄重建。调用方须持有 _sync。</summary>
        private HTuple EnsureCachedTextModel(FlowContext ctx)
        {
            object key = CurrentTextModelKey;
            if (_cachedTextModel != null && Equals(_cachedTextModelKey, key))
            {
                return _cachedTextModel;
            }
            ReleaseCachedTextModel();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            HOperatorSet.CreateTextModelReader("auto", string.IsNullOrWhiteSpace(Classifier) ? "Universal_0-9A-Z_Rej.occ" : Classifier, out HTuple reader);
            if (!string.IsNullOrWhiteSpace(Polarity))
            {
                HOperatorSet.SetTextModelParam(reader, "polarity", Polarity);
            }
            if (DotPrint)
            {
                HOperatorSet.SetTextModelParam(reader, "dot_print", "true");
            }
            SetTextModelParamIfPositive(reader, "min_char_height", MinCharHeight);
            SetTextModelParamIfPositive(reader, "max_char_height", MaxCharHeight);
            SetTextModelParamIfPositive(reader, "min_stroke_width", MinStrokeWidth);
            SetTextModelParamIfPositive(reader, "max_stroke_width", MaxStrokeWidth);
            if (!string.IsNullOrEmpty(TextLineSeparators))
            {
                HOperatorSet.SetTextModelParam(reader, "text_line_separators", TextLineSeparators);
            }
            _cachedTextModel = reader;
            _cachedTextModelKey = key;
            ctx?.AddLog(FlowLogLevel.Info,
                $"[字符识别] 文本模型加载耗时 {watch.ElapsedMilliseconds} ms（已缓存，分类器/参数变化时重建；可通过预热提前加载）");
            return reader;
        }

        private static void SetTextModelParamIfPositive(HTuple reader, string name, double value)
        {
            if (value > 0)
            {
                HOperatorSet.SetTextModelParam(reader, name, value);
            }
        }

        private void ReleaseCachedTextModel()
        {
            if (_cachedTextModel != null)
            {
                HOperatorSet.ClearTextModel(_cachedTextModel);
                _cachedTextModel = null;
                _cachedTextModelKey = null;
            }
        }

        private object CurrentDeepOcrKey => (Mode ?? string.Empty, Device ?? string.Empty);

        /// <summary>取得缓存的 Deep OCR 句柄：键不变时复用。调用方须持有 _sync。</summary>
        private HTuple EnsureCachedDeepOcr(FlowContext ctx)
        {
            object key = CurrentDeepOcrKey;
            if (_cachedDeepOcr != null && Equals(_cachedDeepOcrKey, key))
            {
                return _cachedDeepOcr;
            }
            _cachedDeepOcr = null;
            _cachedDeepOcrKey = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            HTuple ocr;
            HOperatorSet.CreateDeepOcr("mode", string.IsNullOrWhiteSpace(Mode) ? "auto" : Mode, out ocr);
            if (string.Equals(Device, "Gpu", StringComparison.OrdinalIgnoreCase))
            {
                // 22.11 实测 set_deep_ocr_param 设备参数报 #1203（见 RECOGNITION-TOOLS-PLAN.md 第 12 节 B 段），
                // 选 GPU 时在此给出明确中文错误而不是等到生产运行才失败
                try
                {
                    HOperatorSet.SetDeepOcrParam(ocr, "device", "gpu");
                }
                catch (HalconException ex)
                {
                    throw new InvalidOperationException(
                        $"字符识别 DeepOcr 引擎：当前 HALCON 版本不支持设置 GPU 设备（set_deep_ocr_param 报 #{ex.GetErrorCode()}），请把 Device 改为 Cpu 或升级 HALCON", ex);
                }
            }
            _cachedDeepOcr = ocr;
            _cachedDeepOcrKey = key;
            ctx?.AddLog(FlowLogLevel.Info,
                $"[字符识别] Deep OCR 模型加载耗时 {watch.ElapsedMilliseconds} ms（已缓存，模式/设备变化时重建；可通过预热提前加载）");
            return ocr;
        }

        /// <summary>预热：按当前引擎/参数加载并缓存文本模型或 Deep OCR 句柄；DeepOcr 选 GPU 且不可用时抛出中文异常。</summary>
        public void Prepare()
        {
            lock (_sync)
            {
                if (Engine == OcrEngine.DeepOcr)
                {
                    EnsureCachedDeepOcr(null);
                }
                else
                {
                    EnsureCachedTextModel(null);
                }
            }
        }

        /// <summary>释放缓存资源：文本模型 clear_text_model；Deep OCR 句柄 22.11 无法显式释放（#2404），仅丢弃引用。</summary>
        public void ReleaseResources()
        {
            lock (_sync)
            {
                ReleaseCachedTextModel();
                _cachedDeepOcr = null;
                _cachedDeepOcrKey = null;
            }
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (Engine == OcrEngine.TextModel)
            {
                if (string.IsNullOrWhiteSpace(Classifier))
                {
                    yield return new ToolConfigurationIssue(nameof(Classifier), "字符分类器不能为空");
                }
                if (!string.IsNullOrWhiteSpace(Polarity) && Array.IndexOf(Polarities, Polarity) < 0)
                {
                    yield return new ToolConfigurationIssue(nameof(Polarity),
                        $"极性必须是 {string.Join(" / ", Polarities)}");
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(Mode) && Array.IndexOf(DeepOcrModes, Mode) < 0)
                {
                    yield return new ToolConfigurationIssue(nameof(Mode),
                        $"Deep OCR 模式必须是 {string.Join(" / ", DeepOcrModes)}");
                }
                if (!string.IsNullOrWhiteSpace(Device) && Array.IndexOf(DeepOcrDevices, Device) < 0)
                {
                    yield return new ToolConfigurationIssue(nameof(Device),
                        $"设备必须是 {string.Join(" / ", DeepOcrDevices)}");
                }
                if (ConfidenceThreshold < 0 || ConfidenceThreshold > 1)
                {
                    yield return new ToolConfigurationIssue(nameof(ConfidenceThreshold),
                        "最低字符置信度必须在 0~1 之间（0 表示不过滤）");
                }
            }
            if (!string.IsNullOrWhiteSpace(ExpectedPattern))
            {
                string patternError = null;
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(ExpectedPattern);
                }
                catch (ArgumentException ex)
                {
                    patternError = ex.Message;
                }
                if (patternError != null)
                {
                    yield return new ToolConfigurationIssue(nameof(ExpectedPattern),
                        $"期望格式不是有效的正则表达式：{patternError}");
                }
            }
        }

        /// <summary>按引擎显隐参数：TextModel 参数与 DeepOcr 参数互斥，Alphabet / ExpectedPattern / 输入始终显示。</summary>
        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(Classifier):
                case nameof(Polarity):
                case nameof(DotPrint):
                case nameof(MinCharHeight):
                case nameof(MaxCharHeight):
                case nameof(MinStrokeWidth):
                case nameof(MaxStrokeWidth):
                case nameof(TextLineSeparators):
                    return Engine == OcrEngine.TextModel;
                case nameof(Mode):
                case nameof(Device):
                case nameof(ConfidenceThreshold):
                    return Engine == OcrEngine.DeepOcr;
                default:
                    return true;
            }
        }
    }
}
