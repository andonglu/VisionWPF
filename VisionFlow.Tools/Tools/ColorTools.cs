using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>颜色识别色彩空间：RGB / HSV / CIELAB（默认 CIELAB，色差与人眼感受更接近，探测结论 RECOGNITION-TOOLS-PLAN 第 12 节 C 段）。</summary>
    public enum ColorClassifySpace
    {
        Rgb,
        Hsv,
        Cielab
    }

    /// <summary>一条参考颜色：名称、所选色彩空间的三个通道值与允许距离（欧氏距离阈值）。</summary>
    internal sealed class ColorReference
    {
        public const double DefaultMaxDistance = 60;

        public string Name;
        public double Channel1;
        public double Channel2;
        public double Channel3;
        public double MaxDistance;

        public double DistanceTo(double c1, double c2, double c3)
        {
            double d1 = c1 - Channel1;
            double d2 = c2 - Channel2;
            double d3 = c3 - Channel3;
            return Math.Sqrt(d1 * d1 + d2 * d2 + d3 * d3);
        }
    }

    /// <summary>解析“颜色识别”的参考颜色配置（每行 名称|通道1|通道2|通道3|允许距离，允许距离可省略）。</summary>
    internal static class ColorReferenceParser
    {
        public static List<ColorReference> Parse(string text)
        {
            var references = new List<ColorReference>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return references;
            }
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim().TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }
                int lineNo = i + 1;
                string[] parts = line.Split('|');
                if (parts.Length < 4 || parts.Length > 5)
                {
                    throw new FormatException(
                        $"第 {lineNo} 行格式错误：应为 名称|通道1|通道2|通道3|允许距离（允许距离可省略，默认 {ColorReference.DefaultMaxDistance}），实际为「{line}」");
                }
                string name = parts[0].Trim();
                if (name.Length == 0)
                {
                    throw new FormatException($"第 {lineNo} 行缺少参考颜色名称");
                }
                double c1 = ParseChannel(parts[1], lineNo);
                double c2 = ParseChannel(parts[2], lineNo);
                double c3 = ParseChannel(parts[3], lineNo);
                double maxDistance = ColorReference.DefaultMaxDistance;
                if (parts.Length == 5)
                {
                    string textDistance = parts[4].Trim();
                    if (!double.TryParse(textDistance, NumberStyles.Float, CultureInfo.InvariantCulture, out maxDistance))
                    {
                        throw new FormatException($"第 {lineNo} 行允许距离「{textDistance}」不是有效数字");
                    }
                    if (maxDistance < 0)
                    {
                        throw new FormatException($"第 {lineNo} 行允许距离不能为负数（当前 {maxDistance}）");
                    }
                }
                references.Add(new ColorReference
                {
                    Name = name,
                    Channel1 = c1,
                    Channel2 = c2,
                    Channel3 = c3,
                    MaxDistance = maxDistance
                });
            }
            return references;
        }

        private static double ParseChannel(string text, int lineNo)
        {
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw new FormatException($"第 {lineNo} 行通道值「{text.Trim()}」不是有效数字");
            }
            return value;
        }
    }

    [ToolOutput("Labels", VariableKind.Array, VariableType.String)]
    [ToolOutput("FirstLabel", VariableKind.Single, VariableType.String)]
    [ToolOutput("Distances", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    /// <summary>
    /// 每个区域的颜色均值：1×1 三通道图像，通道值为<strong>所选色彩空间</strong>（Rgb / Hsv / Cielab）的均值，
    /// 并非真实 RGB 颜色；用于显示时需先按同一色彩空间 <c>trans_to_rgb</c> 换算（编辑器已如此处理）。
    /// </summary>
    [ToolOutput("MeanColors", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    [ToolOutput("AllKnown", VariableKind.Single, VariableType.Bool)]
    public sealed class ColorClassifyTool : ToolBase, INotFoundPolicy, IToolConfigurationCheck
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        [InputRef("区域", typeof(HalconRegion))]
        public string RegionPath { get; set; }

        /// <summary>色彩空间：Rgb / Hsv / Cielab（默认 Cielab，色差与人眼感受更接近）。</summary>
        public ColorClassifySpace ColorSpace { get; set; } = ColorClassifySpace.Cielab;

        /// <summary>参考颜色列表：每行 名称|通道1|通道2|通道3|允许距离（允许距离可省略，默认 60）；通道值为所选色彩空间的值。</summary>
        public string References { get; set; }

        /// <summary>与所有参考颜色的距离都超过允许距离时的标签。</summary>
        public string UnknownLabel { get; set; } = "未知";

        /// <summary>区域对象数为 0 时是否失败（默认 true）；关闭后输出 Count=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        public ColorClassifyTool(string moduleName) : base(moduleName)
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
            HOperatorSet.CountChannels(image, out HTuple channelCount);
            int channels = channelCount.Length > 0 ? channelCount[0].I : 0;
            if (channels != 3)
            {
                return NodeResult.Fail($"{ModuleName} 颜色识别需要三通道彩色图像（当前 {channels} 通道）");
            }

            HObject regions = Input<HalconRegion>(ctx, RegionPath).Object;
            HOperatorSet.CountObj(regions, out HTuple objectCount);
            int count = objectCount.I;
            if (count == 0)
            {
                WriteOutputs(ctx, new List<string>(), string.Empty, new List<double>(), new List<HalconImage>(), allKnown: false);
                return NotFoundOutcome.Resolve(ctx, this, "未找到待识别颜色的区域");
            }

            List<ColorReference> references = ColorReferenceParser.Parse(References);
            var labels = new List<string>(count);
            var distances = new List<double>(count);
            var meanColors = new List<HalconImage>(count);
            HObject first = null;
            HObject second = null;
            HObject third = null;
            HObject converted1 = null;
            HObject converted2 = null;
            HObject converted3 = null;
            try
            {
                HOperatorSet.Decompose3(image, out first, out second, out third);
                HObject channel1 = first;
                HObject channel2 = second;
                HObject channel3 = third;
                if (ColorSpace != ColorClassifySpace.Rgb)
                {
                    // 探测结论 C：trans_from_rgb 有效取值 'hsv' / 'cielab'（'lab' 无效 #1301）
                    HOperatorSet.TransFromRgb(first, second, third, out converted1, out converted2, out converted3,
                        ColorSpace == ColorClassifySpace.Hsv ? "hsv" : "cielab");
                    channel1 = converted1;
                    channel2 = converted2;
                    channel3 = converted3;
                }
                // 探测结论 C：intensity 对多对象区域逐对象返回均值元组
                HOperatorSet.Intensity(regions, channel1, out HTuple mean1, out HTuple _);
                HOperatorSet.Intensity(regions, channel2, out HTuple mean2, out HTuple _);
                HOperatorSet.Intensity(regions, channel3, out HTuple mean3, out HTuple _);

                bool allKnown = true;
                for (int i = 0; i < count; i++)
                {
                    double c1 = mean1[i].D;
                    double c2 = mean2[i].D;
                    double c3 = mean3[i].D;
                    meanColors.Add(MeanColorImage(c1, c2, c3));

                    ColorReference nearest = null;
                    double nearestDistance = double.MaxValue;
                    foreach (ColorReference reference in references)
                    {
                        double distance = reference.DistanceTo(c1, c2, c3);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearest = reference;
                        }
                    }
                    if (nearest != null && nearestDistance <= nearest.MaxDistance)
                    {
                        labels.Add(nearest.Name);
                        distances.Add(nearestDistance);
                    }
                    else
                    {
                        labels.Add(UnknownLabel);
                        distances.Add(double.NaN);
                        allKnown = false;
                    }
                }
                WriteOutputs(ctx, labels, labels[0], distances, meanColors, allKnown);
            }
            catch (HalconException ex)
            {
                return NodeResult.Fail($"{ModuleName} 颜色识别失败：{ex.Message}");
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
                third?.Dispose();
                converted1?.Dispose();
                converted2?.Dispose();
                converted3?.Dispose();
            }

            ctx.AddLog(FlowLogLevel.Info,
                $"[颜色识别] {ColorSpace} 区域数={count} 已知颜色={labels.Count(label => label != UnknownLabel)}");
            return NodeResult.Ok;
        }

        private void WriteOutputs(FlowContext ctx, List<string> labels, string firstLabel,
            List<double> distances, List<HalconImage> meanColors, bool allKnown)
        {
            SetOutput(ctx, Variable.Array(ModuleName, "Labels", VariableType.String, labels));
            SetOutput(ctx, Variable.Single(ModuleName, "FirstLabel", VariableType.String, firstLabel));
            SetOutput(ctx, Variable.Array(ModuleName, "Distances", VariableType.Double, distances));
            SetOutput(ctx, Variable.Array(ModuleName, "MeanColors", VariableType.Object, meanColors));
            SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, labels.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "AllKnown", VariableType.Bool, allKnown));
        }

        /// <summary>每个区域一个 1×1 三通道图像，像素值为所选色彩空间的通道均值（byte，四舍五入并截断到 0~255）。</summary>
        private static HalconImage MeanColorImage(double c1, double c2, double c3)
        {
            var red = new[] { ToByte(c1) };
            var green = new[] { ToByte(c2) };
            var blue = new[] { ToByte(c3) };
            GCHandle pinR = GCHandle.Alloc(red, GCHandleType.Pinned);
            GCHandle pinG = GCHandle.Alloc(green, GCHandleType.Pinned);
            GCHandle pinB = GCHandle.Alloc(blue, GCHandleType.Pinned);
            try
            {
                HOperatorSet.GenImage3(out HObject image, "byte", 1, 1,
                    new HTuple(pinR.AddrOfPinnedObject().ToInt64()),
                    new HTuple(pinG.AddrOfPinnedObject().ToInt64()),
                    new HTuple(pinB.AddrOfPinnedObject().ToInt64()));
                return new HalconImage(image);
            }
            finally
            {
                pinR.Free();
                pinG.Free();
                pinB.Free();
            }
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            if (string.IsNullOrWhiteSpace(References))
            {
                yield return new ToolConfigurationIssue(nameof(References),
                    $"参考颜色不能为空：每行一个 名称|通道1|通道2|通道3|允许距离（允许距离可省略，默认 {ColorReference.DefaultMaxDistance}）");
                yield break;
            }
            string parseError = null;
            try
            {
                _ = ColorReferenceParser.Parse(References);
            }
            catch (FormatException ex)
            {
                parseError = ex.Message;
            }
            if (parseError != null)
            {
                yield return new ToolConfigurationIssue(nameof(References), parseError);
            }
        }
    }

    /// <summary>颜色分割分类器：MLP（默认）或 GMM（探测结论 RECOGNITION-TOOLS-PLAN 第 12 节 D 段）。</summary>
    public enum ColorSegmentClassifier
    {
        Mlp,
        Gmm
    }

    [ToolOutput("Regions", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("ClassAreas", VariableKind.Array, VariableType.Int)]
    [ToolOutput("RejectedRegion", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconRegion))]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Count", VariableKind.Single, VariableType.Int)]
    public sealed class ColorSegmentTool : ToolBase, INotFoundPolicy, IToolResourceLifecycle, IToolConfigurationCheck
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";

        /// <summary>分类器类型：Mlp（默认，create_class_mlp）或 Gmm（create_class_gmm）；训练与运行须使用同一类型。</summary>
        public ColorSegmentClassifier Classifier { get; set; } = ColorSegmentClassifier.Mlp;

        /// <summary>类名（CSV，如 红,绿,蓝）：顺序与训练样本区域、输出 Regions 对象的类序一一对应。</summary>
        public string ClassNames { get; set; }

        /// <summary>拒识阈值 [0,1)：置信度/概率低于该值的像素归为拒识；越大越严格（探测结论 D：0 时背景也会归类，0.9 时仅高置信像素）。</summary>
        public double RejectionThreshold { get; set; } = 0.5;

        /// <summary>训练结果（serialize_class_mlp / serialize_class_gmm 字节）；非空时按此反序列化分类器，变化时重建。</summary>
        public byte[] ClassifierData { get; set; }

        /// <summary>所有颜色类的区域均为空（Found=false）时是否失败（默认 true）；关闭后输出 Found=false、Count=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        // 固定随机种子：同一训练样本训练出的分类器可复现，测试与现场结果稳定
        private const int RandomSeed = 42;

        // 分类器句柄按实例缓存（键 = 训练数据引用 + 分类器类型 + 类名），参与预热和释放；
        // classify_image_class_* 保存本次分类结果，同一实例的并发运行串行执行，避免结果被并发覆盖
        private readonly object _sync = new object();
        private HTuple _cachedClassifier;
        private object _cachedKey;
        private bool _cachedIsGmm;

        public ColorSegmentTool(string moduleName) : base(moduleName)
        {
        }

        /// <summary>
        /// 用当前图像训练颜色分类器（编辑窗口“为每个类框选样本后训练”）：
        /// create_class_mlp / create_class_gmm → add_samples_image_class_*（区域对象数必须等于类数，#1502）→
        /// train → serialize 字节保存到 <see cref="ClassifierData"/>。配置不合法时抛出含中文说明的 InvalidOperationException。
        /// </summary>
        public void Train(FlowContext ctx, HObject image, HObject classRegions)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }
            if (classRegions == null)
            {
                throw new ArgumentNullException(nameof(classRegions));
            }
            List<string> classNames = ParseClassNames(ClassNames, out string classNameError);
            if (classNameError != null)
            {
                throw new InvalidOperationException($"{ModuleName} {classNameError}");
            }
            HOperatorSet.CountObj(classRegions, out HTuple objectCount);
            if (objectCount.I != classNames.Count)
            {
                throw new InvalidOperationException(
                    $"{ModuleName} 训练样本数（{objectCount.I}）与类名数（{classNames.Count}）不一致：每个类需要一个样本区域（HALCON #1502）");
            }
            HOperatorSet.CountChannels(image, out HTuple channelCount);
            int channels = channelCount.Length > 0 ? channelCount[0].I : 0;
            if (channels != 3)
            {
                throw new InvalidOperationException($"{ModuleName} 颜色分割训练需要三通道彩色图像（当前 {channels} 通道）");
            }

            lock (_sync)
            {
                HTuple handle = CreateClassifier(classNames.Count);
                try
                {
                    if (Classifier == ColorSegmentClassifier.Mlp)
                    {
                        HOperatorSet.AddSamplesImageClassMlp(image, classRegions, handle);
                        HOperatorSet.TrainClassMlp(handle, 200, 1.0, 0.01, out HTuple _, out HTuple _);
                    }
                    else
                    {
                        HOperatorSet.AddSamplesImageClassGmm(image, classRegions, handle, 100.0);
                        HOperatorSet.TrainClassGmm(handle, 100, 0.001, "training", 1, out HTuple _, out HTuple _);
                    }
                    ClassifierData = SerializeClassifier(handle, Classifier == ColorSegmentClassifier.Gmm);
                    ctx?.AddLog(FlowLogLevel.Info,
                        $"[颜色分割] 分类器已训练（{Classifier}，{classNames.Count} 类：{string.Join("、", classNames)}），训练数据 {ClassifierData.Length} 字节；重新训练会替换现有训练数据");
                }
                finally
                {
                    ClearClassifier(handle, Classifier == ColorSegmentClassifier.Gmm);
                }
            }
        }

        private HTuple CreateClassifier(int classCount)
        {
            if (Classifier == ColorSegmentClassifier.Mlp)
            {
                HOperatorSet.CreateClassMlp(3, 10, classCount, "softmax", "normalization", 3, RandomSeed, out HTuple handle);
                return handle;
            }
            HOperatorSet.CreateClassGmm(3, classCount, 1, "full", "none", 3, RandomSeed, out HTuple gmm);
            return gmm;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }
            if (ClassifierData == null || ClassifierData.Length == 0)
            {
                return NodeResult.Fail($"{ModuleName} 颜色分割分类器尚未训练：请在编辑窗口为每个类框选样本区域并执行训练");
            }

            HObject image = Input<HalconImage>(ctx, ImagePath).Object;
            HOperatorSet.CountChannels(image, out HTuple channelCount);
            int channels = channelCount.Length > 0 ? channelCount[0].I : 0;
            if (channels != 3)
            {
                return NodeResult.Fail($"{ModuleName} 颜色分割需要三通道彩色图像（当前 {channels} 通道）");
            }

            HObject classRegions;
            lock (_sync)
            {
                HTuple classifier;
                try
                {
                    classifier = EnsureCachedClassifier(ctx);
                }
                catch (Exception ex)
                {
                    return NodeResult.Fail($"{ModuleName} 颜色分割分类器加载失败：{ex.Message}");
                }
                try
                {
                    if (Classifier == ColorSegmentClassifier.Mlp)
                    {
                        HOperatorSet.ClassifyImageClassMlp(image, out classRegions, classifier, RejectionThreshold);
                    }
                    else
                    {
                        HOperatorSet.ClassifyImageClassGmm(image, out classRegions, classifier, RejectionThreshold);
                    }
                }
                catch (HalconException ex)
                {
                    return NodeResult.Fail($"{ModuleName} 颜色分割失败：{ex.Message}");
                }
            }

            HObject domain = null;
            HObject union = null;
            try
            {
                HOperatorSet.CountObj(classRegions, out HTuple regionCount);
                int classCount = regionCount.I;
                HOperatorSet.AreaCenter(classRegions, out HTuple area, out HTuple _, out HTuple _);
                var areas = new int[classCount];
                int nonEmpty = 0;
                for (int i = 0; i < classCount; i++)
                {
                    areas[i] = area[i].I;
                    if (areas[i] > 0)
                    {
                        nonEmpty++;
                    }
                }
                bool found = nonEmpty > 0;

                // 探测结论 D：拒识像素不进任何类 → RejectedRegion = 图像定义域 − union1(类区域)
                HOperatorSet.GetDomain(image, out domain);
                HOperatorSet.Union1(classRegions, out union);
                HOperatorSet.Difference(domain, union, out HObject rejected);

                SetOutput(ctx, Variable.Object(ModuleName, "Regions", new HalconRegion(classRegions), classCount));
                SetOutput(ctx, Variable.Array(ModuleName, "ClassAreas", VariableType.Int, areas));
                SetOutput(ctx, Variable.Object(ModuleName, "RejectedRegion", new HalconRegion(rejected), 1));
                SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, found));
                SetOutput(ctx, Variable.Single(ModuleName, "Count", VariableType.Int, nonEmpty));
                classRegions = null;
                if (!found)
                {
                    return NotFoundOutcome.Resolve(ctx, this, "颜色分割结果为空：所有颜色类的区域均为空");
                }
                ctx.AddLog(FlowLogLevel.Info, $"[颜色分割] {Classifier} 类数={classCount} 非空类={nonEmpty}");
                return NodeResult.Ok;
            }
            finally
            {
                classRegions?.Dispose();
                domain?.Dispose();
                union?.Dispose();
            }
        }

        private object CurrentClassifierKey => (ClassifierData, Classifier, ClassNames ?? string.Empty);

        /// <summary>取得缓存的分类器句柄：键不变时复用，变化时释放旧句柄并重新反序列化。调用方须持有 _sync。</summary>
        private HTuple EnsureCachedClassifier(FlowContext ctx)
        {
            object key = CurrentClassifierKey;
            if (_cachedClassifier != null && Equals(_cachedKey, key))
            {
                return _cachedClassifier;
            }
            ReleaseCachedClassifier();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _cachedClassifier = DeserializeClassifier(ClassifierData, Classifier == ColorSegmentClassifier.Gmm);
            _cachedIsGmm = Classifier == ColorSegmentClassifier.Gmm;
            _cachedKey = key;
            ctx?.AddLog(FlowLogLevel.Info,
                $"[颜色分割] 分类器加载耗时 {watch.ElapsedMilliseconds} ms（已缓存，训练数据/分类器类型/类名变化时重建；可通过预热提前加载）");
            return _cachedClassifier;
        }

        private void ReleaseCachedClassifier()
        {
            if (_cachedClassifier != null)
            {
                ClearClassifier(_cachedClassifier, _cachedIsGmm);
                _cachedClassifier = null;
                _cachedKey = null;
            }
        }

        /// <summary>预热：按当前训练数据/分类器类型/类名反序列化并缓存分类器；尚未训练时直接返回。</summary>
        public void Prepare()
        {
            if (ClassifierData == null || ClassifierData.Length == 0)
            {
                return;
            }
            lock (_sync)
            {
                EnsureCachedClassifier(null);
            }
        }

        /// <summary>释放缓存的分类器句柄；正在运行的分类结束后才会释放。</summary>
        public void ReleaseResources()
        {
            lock (_sync)
            {
                ReleaseCachedClassifier();
            }
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            _ = ParseClassNames(ClassNames, out string classNameError);
            if (classNameError != null)
            {
                yield return new ToolConfigurationIssue(nameof(ClassNames), classNameError);
            }
            if (RejectionThreshold < 0 || RejectionThreshold >= 1)
            {
                yield return new ToolConfigurationIssue(nameof(RejectionThreshold), "拒识阈值必须在 0~1 之间（含 0、不含 1；越大越严格）");
            }
        }

        private static List<string> ParseClassNames(string text, out string error)
        {
            error = null;
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "类名不能为空：请用逗号分隔填写，如 红,绿,蓝";
                return names;
            }
            foreach (string part in text.Split(','))
            {
                string name = part.Trim();
                if (name.Length == 0)
                {
                    error = "类名不能为空：请用逗号分隔填写，如 红,绿,蓝";
                    return names;
                }
                if (names.Contains(name))
                {
                    error = $"类名重复：{name}";
                    return names;
                }
                names.Add(name);
            }
            return names;
        }

        private static byte[] SerializeClassifier(HTuple handle, bool gmm)
        {
            HTuple item;
            if (gmm)
            {
                HOperatorSet.SerializeClassGmm(handle, out item);
            }
            else
            {
                HOperatorSet.SerializeClassMlp(handle, out item);
            }
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

        private static HTuple DeserializeClassifier(byte[] bytes, bool gmm)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("颜色分割分类器训练数据为空");
            }
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            HTuple item = null;
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                HOperatorSet.CreateSerializedItemPtr(new HTuple(pointer.ToInt64()), bytes.Length, "true", out item);
                if (gmm)
                {
                    HOperatorSet.DeserializeClassGmm(item, out HTuple gmmHandle);
                    return gmmHandle;
                }
                HOperatorSet.DeserializeClassMlp(item, out HTuple mlpHandle);
                return mlpHandle;
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

        private static void ClearClassifier(HTuple handle, bool gmm)
        {
            if (gmm)
            {
                HOperatorSet.ClearClassGmm(handle);
            }
            else
            {
                HOperatorSet.ClearClassMlp(handle);
            }
        }
    }
}
