using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>定位应用目录、当前目录及其上级目录中的资源文件；找不到时返回应用目录下的预期路径。</summary>
    public static class RepoPaths
    {
        public static string Find(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("资源路径不能为空", nameof(relativePath));
            }

            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            foreach (string root in CandidateRoots())
            {
                string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
        }

        private static IEnumerable<string> CandidateRoots()
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    yield return dir.FullName;
                    dir = dir.Parent;
                }
            }
        }
    }

    /// <summary>
    /// 单个匹配结果：位姿、分数、匹配后轮廓，
    /// 以及<b>相对于基准模板的刚体变换矩阵</b>（HomMat，6 元素），供下游工具做跟随。
    /// 实现 <see cref="IHalconResourceContainer"/>：上下文释放时回收轮廓（按引用去重，
    /// 与 Contours 列表中的同一对象不会重复释放）。
    /// </summary>
    public sealed class MatchResultItem : IHalconResourceContainer
    {
        public int Index { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Angle { get; set; }
        public double Scale { get; set; } = 1;
        public double ScaleRow { get; set; } = 1;
        public double ScaleColumn { get; set; } = 1;
        public double Score { get; set; }
        /// <summary>匹配后的轮廓（已仿射变换到图像位置）。</summary>
        public HObject Contour { get; set; }
        public int ContourPointCount { get; set; }
        /// <summary>基准模板位姿 → 本匹配位姿 的变换矩阵（专门类型，非一般 HTuple）。</summary>
        public HomMat2D HomMat { get; set; }
        /// <summary>完整的 3×3 投影矩阵（9 个元素，行优先；仅描述子匹配提供，其他匹配为 null）。</summary>
        public double[] ProjectiveHomMat { get; set; }

        /// <summary>该项持有的 HALCON 对象（归本次运行所有）。</summary>
        public IEnumerable<HObject> OwnedHalconObjects
        {
            get
            {
                if (Contour != null)
                {
                    yield return Contour;
                }
            }
        }

        public override string ToString()
        {
            return $"#{Index} Row={Row:F2}, Col={Column:F2}, Angle={Angle:F4}, Scale={Scale:F3}, Score={Score:F3}, 轮廓点数={ContourPointCount}";
        }
    }

    public interface IHalconTemplateMatchTool : INotFoundPolicy
    {
        string ModuleName { get; set; }
        string ImagePath { get; set; }
        byte[] ShapeModelData { get; set; }
        double FindStartAngle { get; set; }
        double FindExtentAngle { get; set; }
        double MinScore { get; set; }
        int NumMatches { get; set; }
        double MaxOverlap { get; set; }
        string SubPixel { get; set; }
        int NumLevelsFind { get; set; }
        double Greediness { get; set; }
        double BaseRow { get; set; }
        double BaseColumn { get; set; }
        double BaseAngle { get; set; }
    }

    /// <summary>
    /// 匹配工具公共基类：统一图像加载、输出写入、“未找到”处理、模型句柄缓存与预热/释放（TR-06/TR-10/TR-13）。
    /// 派生类提供模型来源（缓存键与加载方式）并负责查找，生成每个结果的轮廓与跟随矩阵。
    /// 输出：MatchCount、Found、Scores、Items、HomMats、BestMatch、BestHomMat、
    /// Contours（每个匹配的 XLD）、ResultContour（全部匹配轮廓合并的 XLD）、Image，
    /// 以及最佳结果的 Row / Column / Angle / Score 单值（未找到时为 NaN）。
    /// </summary>
    public abstract class HalconMatchToolBase : ToolBase, INotFoundPolicy, IToolResourceLifecycle
    {
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";
        public double MinScore { get; set; } = 0.5;
        public int NumMatches { get; set; } = 10;
        /// <summary>未匹配到目标时是否失败（默认 true）；关闭后输出 Found=false、MatchCount=0 并继续。</summary>
        public bool FailWhenNotFound { get; set; } = true;

        private readonly object _modelSync = new object();
        private HTuple _cachedModel;
        private object _cachedModelKey;

        protected HalconMatchToolBase(string moduleName) : base(moduleName)
        {
        }

        /// <summary>日志与错误信息中的工具名称。</summary>
        protected abstract string MatchLabel { get; }

        /// <summary>未创建模板时的错误信息。</summary>
        protected abstract string MissingModelMessage { get; }

        /// <summary>
        /// 当前模型数据的缓存键（按值或引用比较）；null 表示没有可缓存的模型数据（走兼容加载）。
        /// 键变化（重新示教/导入/修改训练参数）时释放旧句柄并重新加载。
        /// </summary>
        protected abstract object CurrentModelKey { get; }

        /// <summary>按当前模型数据加载（或创建）模型句柄。</summary>
        protected abstract HTuple LoadModel();

        /// <summary>释放模型句柄。</summary>
        protected abstract void ClearModel(HTuple model);

        /// <summary>没有可缓存模型数据时的兼容加载（如历史 .shm 路径），每次运行加载、用后释放；不支持时返回 null。</summary>
        protected virtual HTuple LoadFallbackModel()
        {
            return null;
        }

        /// <summary>在图像上查找，找到的结果写入 items（可为空），每项的 Contour 归本次运行所有。</summary>
        protected abstract void FindMatches(FlowContext ctx, HObject image, HTuple model, List<MatchResultItem> items);

        public override NodeResult Run(FlowContext ctx)
        {
            if (!TryLoadImage(ctx, out HObject image, out bool ownsImage, out string imageError))
            {
                return NodeResult.Fail(imageError);
            }

            var items = new List<MatchResultItem>();
            try
            {
                // 模型句柄在同一工具实例内复用（TR-13）；同一实例的并发运行串行执行
                lock (_modelSync)
                {
                    HTuple model;
                    bool temporary = false;
                    object key = CurrentModelKey;
                    if (key != null)
                    {
                        model = EnsureCachedModel(key, out long loadMilliseconds);
                        if (loadMilliseconds >= 0)
                        {
                            ctx.AddLog(FlowLogLevel.Info, $"[{MatchLabel}] 模型加载耗时 {loadMilliseconds} ms（已缓存，模板数据变化时重新加载；可通过预热提前加载）");
                        }
                    }
                    else
                    {
                        model = LoadFallbackModel();
                        if (model == null)
                        {
                            if (ownsImage)
                            {
                                image.Dispose();
                            }
                            return NodeResult.Fail(MissingModelMessage);
                        }
                        temporary = true;
                    }
                    try
                    {
                        FindMatches(ctx, image, model, items);
                    }
                    finally
                    {
                        if (temporary)
                        {
                            ClearModel(model);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DisposeContours(items);
                if (ownsImage)
                {
                    image.Dispose();
                }
                return NodeResult.Fail($"{ModuleName} {MatchLabel}失败：{ex.Message}");
            }

            SetMatchOutputs(ctx, image, ownsImage, items);
            if (items.Count == 0)
            {
                return NotFoundOutcome.Resolve(ctx, this, $"{MatchLabel}未匹配到任何目标");
            }
            return NodeResult.Ok;
        }

        /// <summary>
        /// 预热：按当前模型数据加载并缓存模型句柄（TR-13）。模板未创建时直接返回，由运行时报告；
        /// 兼容路径（如历史 ModelPath）每次运行加载，不参与预热。
        /// </summary>
        public void Prepare()
        {
            object key = CurrentModelKey;
            if (key == null)
            {
                return;
            }
            lock (_modelSync)
            {
                EnsureCachedModel(key, out _);
            }
        }

        /// <summary>释放缓存的模型句柄；正在运行的匹配结束后才会释放。</summary>
        public void ReleaseResources()
        {
            lock (_modelSync)
            {
                ReleaseCachedModel();
            }
        }

        /// <summary>
        /// 取得缓存的模型句柄：缓存键不变时复用，变化时释放旧句柄并重新加载。
        /// 本次发生加载时 loadMilliseconds 为加载耗时，否则为 -1。调用方须持有 _modelSync。
        /// </summary>
        private HTuple EnsureCachedModel(object key, out long loadMilliseconds)
        {
            loadMilliseconds = -1;
            if (_cachedModel != null && Equals(_cachedModelKey, key))
            {
                return _cachedModel;
            }
            ReleaseCachedModel();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _cachedModel = LoadModel();
            _cachedModelKey = key;
            loadMilliseconds = watch.ElapsedMilliseconds;
            return _cachedModel;
        }

        private void ReleaseCachedModel()
        {
            if (_cachedModel != null)
            {
                ClearModel(_cachedModel);
                _cachedModel = null;
                _cachedModelKey = null;
            }
        }

        /// <summary>
        /// 加载输入图像。ownsImage 为 true 表示图像是本次运行从文件读入的新对象
        /// （归本次运行所有）；false 表示引用的上游/调用方对象（借用，不得释放）。
        /// 兼容旧流程：ImagePath 也允许直接填写图像文件路径。
        /// </summary>
        protected bool TryLoadImage(FlowContext ctx, out HObject image, out bool ownsImage, out string error)
        {
            image = null;
            ownsImage = false;
            error = null;
            if (string.IsNullOrWhiteSpace(ImagePath))
            {
                error = "未配置输入图像";
                return false;
            }
            if (File.Exists(ImagePath))
            {
                HOperatorSet.ReadImage(out image, ImagePath);
                ownsImage = true;
                return true;
            }
            try
            {
                image = Input<HalconImage>(ctx, ImagePath).Object;
                return true;
            }
            catch (Exception ex)
            {
                error = $"输入图像引用无效：{ImagePath}，{ex.Message}";
                return false;
            }
        }

        /// <summary>生成单个匹配结果项（轮廓已变换到图像位置，归本次运行所有）。</summary>
        protected MatchResultItem CreateItem(FlowContext ctx, int index, double row, double column, double angle,
            double scale, double score, HObject contour, HomMat2D followMat)
        {
            var item = new MatchResultItem
            {
                Index = index,
                Row = row,
                Column = column,
                Angle = angle,
                Scale = scale,
                ScaleRow = scale,
                ScaleColumn = scale,
                Score = score,
                Contour = contour,
                ContourPointCount = CountContourPoints(contour),
                HomMat = followMat
            };
            ctx.AddLog(FlowLogLevel.Info, $"[{MatchLabel}] {item}");
            return item;
        }

        private void SetMatchOutputs(FlowContext ctx, HObject image, bool ownsImage, List<MatchResultItem> items)
        {
            HOperatorSet.GenEmptyObj(out HObject resultContour);
            foreach (MatchResultItem item in items)
            {
                HOperatorSet.ConcatObj(resultContour, item.Contour, out HObject combined);
                resultContour.Dispose();
                resultContour = combined;
            }

            List<HomMat2D> homMats = items.Select(i => i.HomMat).ToList();
            MatchResultItem best = items.Count > 0 ? items[0] : null;
            SetOutput(ctx, Variable.Single(ModuleName, "MatchCount", VariableType.Int, items.Count));
            SetOutput(ctx, Variable.Single(ModuleName, "Found", VariableType.Bool, best != null));
            SetOutput(ctx, Variable.Array(ModuleName, "Scores", VariableType.Double, items.Select(i => i.Score)));
            SetOutput(ctx, Variable.Array(ModuleName, "Items", VariableType.Object, items));
            SetOutput(ctx, Variable.Array(ModuleName, "HomMats", VariableType.Object, homMats));
            // 未找到时显式写 null / NaN，避免循环中下游读到上一轮的最佳结果
            SetOutput(ctx, Variable.Object(ModuleName, "BestMatch", best, best != null ? 1 : 0));
            SetOutput(ctx, Variable.Object(ModuleName, "BestHomMat", best?.HomMat, best != null ? 1 : 0));
            SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, best?.Row ?? double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, best?.Column ?? double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Angle", VariableType.Double, best?.Angle ?? double.NaN));
            SetOutput(ctx, Variable.Single(ModuleName, "Score", VariableType.Double, best?.Score ?? double.NaN));
            SetOutput(ctx, Variable.Array(ModuleName, "Contours", VariableType.Object,
                items.Select(i => new HalconXld(i.Contour))));
            SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", new HalconXld(resultContour), items.Count));
            // 图像输出：本次运行读入的归运行所有；引用的上游图像保持借用，不得随上下文释放（VF-04）
            Variable imageVariable = Variable.Object(ModuleName, "Image", new HalconImage(image), 1);
            if (ownsImage)
            {
                SetOutput(ctx, imageVariable);
            }
            else
            {
                SetBorrowedOutput(ctx, imageVariable);
            }
        }

        private static void DisposeContours(List<MatchResultItem> items)
        {
            foreach (MatchResultItem item in items)
            {
                item.Contour?.Dispose();
            }
            items.Clear();
        }

        protected static int CountContourPoints(HObject contourObject)
        {
            HOperatorSet.CountObj(contourObject, out HTuple contourCount);
            int pointCount = 0;
            for (int k = 1; k <= contourCount.I; k++)
            {
                HOperatorSet.SelectObj(contourObject, out HObject single, k);
                HOperatorSet.GetContourXld(single, out HTuple rows, out _);
                pointCount += rows.Length;
                single.Dispose();
            }
            return pointCount;
        }

        /// <summary>把模型轮廓（以模型原点为基准）按匹配位姿变换到图像位置。</summary>
        protected static HObject TransformModelContour(HObject modelContour, HomMat2D modelToImage)
        {
            HOperatorSet.AffineTransContourXld(modelContour, out HObject contour, modelToImage.Data);
            return contour;
        }
    }

    /// <summary>
    /// 模板匹配工具基类（形状 / 灰度 / 缩放形状 / 局部变形）：模型以 ShapeModelData 内嵌保存，
    /// 跟随矩阵统一为“固定基准位姿（BaseRow/BaseColumn/BaseAngle）→ 本匹配位姿”。
    /// </summary>
    public abstract class HalconTemplateMatchToolBase : HalconMatchToolBase, IHalconTemplateMatchTool
    {
        public byte[] ShapeModelData { get; set; }
        public double FindStartAngle { get; set; } = -0.39;
        public double FindExtentAngle { get; set; } = 0.79;
        public double MaxOverlap { get; set; } = 0.5;
        public string SubPixel { get; set; } = "least_squares";
        public int NumLevelsFind { get; set; }
        public double Greediness { get; set; } = 0.9;
        /// <summary>基准模板位姿：示教时设定的固定行坐标（不随运行改变）。</summary>
        public double BaseRow { get; set; }
        /// <summary>基准模板位姿：示教时设定的固定列坐标。</summary>
        public double BaseColumn { get; set; }
        /// <summary>基准模板位姿：示教时设定的固定角度（弧度）。</summary>
        public double BaseAngle { get; set; }

        protected HalconTemplateMatchToolBase(string moduleName) : base(moduleName)
        {
        }

        /// <summary>缓存键为 ShapeModelData 的引用：重新示教/导入会替换数组并触发重新加载。</summary>
        protected override object CurrentModelKey
        {
            get { return ShapeModelData != null && ShapeModelData.Length > 0 ? ShapeModelData : null; }
        }

        protected override HTuple LoadModel()
        {
            return DeserializeModel(ShapeModelData);
        }

        /// <summary>由 ShapeModelData 反序列化模型句柄。</summary>
        protected abstract HTuple DeserializeModel(byte[] data);
    }

    /// <summary>
    /// Halcon 形状模板匹配工具：find_shape_model。
    /// 基准模板位姿（BaseRow/BaseColumn/BaseAngle）是设置（示教）时确定的固定值；
    /// 每个匹配结果都携带“固定基准位姿 → 本匹配位姿”的变换矩阵 HomMat，供下游工具做跟随。
    /// </summary>
    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Angle", "Score", "HomMat" })]
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
    public sealed class HalconModelMatchTool : HalconTemplateMatchToolBase
    {
        /// <summary>兼容/导入用模型路径；正式运行优先使用 ShapeModelData。</summary>
        public string ModelPath { get; set; }

        /// <summary>历史属性名，等价于 FindStartAngle。</summary>
        public double AngleStart
        {
            get { return FindStartAngle; }
            set { FindStartAngle = value; }
        }

        /// <summary>历史属性名，等价于 FindExtentAngle。</summary>
        public double AngleExtent
        {
            get { return FindExtentAngle; }
            set { FindExtentAngle = value; }
        }

        public HalconModelMatchTool(string moduleName) : base(moduleName)
        {
        }

        protected override string MatchLabel
        {
            get { return "模板匹配"; }
        }

        protected override string MissingModelMessage
        {
            get { return "模板未创建或模型文件不存在"; }
        }

        public void ImportModelFile(string modelPath)
        {
            ShapeModelData = ShapeModelSerialization.ReadShapeModelFileAsBytes(modelPath);
            ModelPath = modelPath;
        }

        public void SetSerializedModel(byte[] modelData)
        {
            ShapeModelData = modelData;
        }

        protected override HTuple DeserializeModel(byte[] data)
        {
            return ShapeModelSerialization.Deserialize(data);
        }

        protected override void ClearModel(HTuple model)
        {
            HOperatorSet.ClearShapeModel(model);
        }

        protected override HTuple LoadFallbackModel()
        {
            if (string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath))
            {
                return null;
            }
            HOperatorSet.ReadShapeModel(ModelPath, out HTuple modelId);
            return modelId;
        }

        protected override void FindMatches(FlowContext ctx, HObject image, HTuple modelId, List<MatchResultItem> items)
        {
            HObject modelContours = null;
            try
            {
                HOperatorSet.GetShapeModelContours(out modelContours, modelId, 1);
                HOperatorSet.FindShapeModel(image, modelId, FindStartAngle, FindExtentAngle, MinScore,
                    NumMatches, MaxOverlap, SubPixel, NumLevelsFind, Greediness,
                    out HTuple rows, out HTuple columns, out HTuple angles, out HTuple scores);
                if (rows.Length > 0)
                {
                    ctx.AddLog(FlowLogLevel.Info, $"[匹配] 固定基准位姿：Row={BaseRow:F2}, Col={BaseColumn:F2}, Angle={BaseAngle:F4}");
                }
                for (int i = 0; i < rows.Length; i++)
                {
                    HObject contour = TransformModelContour(modelContours,
                        HomMat2D.FromPoses(0, 0, 0, rows[i].D, columns[i].D, angles[i].D));
                    HomMat2D followMat = HomMat2D.FromPoses(BaseRow, BaseColumn, BaseAngle, rows[i].D, columns[i].D, angles[i].D);
                    items.Add(CreateItem(ctx, i, rows[i].D, columns[i].D, angles[i].D, 1, scores[i].D, contour, followMat));
                }
            }
            finally
            {
                modelContours?.Dispose();
            }
        }
    }

    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Angle", "Score", "HomMat" })]
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
    public sealed class HalconGrayMatchTool : HalconTemplateMatchToolBase
    {
        public double TeachAngleStart { get; set; } = -0.39;
        public double TeachAngleExtent { get; set; } = 0.79;

        public HalconGrayMatchTool(string moduleName) : base(moduleName)
        {
            SubPixel = "true";
        }

        protected override string MatchLabel
        {
            get { return "灰度匹配"; }
        }

        protected override string MissingModelMessage
        {
            get { return "灰度模板未创建"; }
        }

        protected override HTuple DeserializeModel(byte[] data)
        {
            return ShapeModelSerialization.DeserializeNcc(data);
        }

        protected override void ClearModel(HTuple model)
        {
            HOperatorSet.ClearNccModel(model);
        }

        protected override void FindMatches(FlowContext ctx, HObject image, HTuple modelId, List<MatchResultItem> items)
        {
            HObject modelRegion = null;
            HObject modelContour = null;
            try
            {
                HOperatorSet.GetNccModelRegion(out modelRegion, modelId);
                HOperatorSet.GenContourRegionXld(modelRegion, out modelContour, "border");
                HOperatorSet.FindNccModel(image, modelId, FindStartAngle, FindExtentAngle, MinScore,
                    NumMatches, MaxOverlap, string.IsNullOrWhiteSpace(SubPixel) ? "true" : SubPixel, NumLevelsFind,
                    out HTuple rows, out HTuple columns, out HTuple angles, out HTuple scores);
                for (int i = 0; i < rows.Length; i++)
                {
                    HObject contour = TransformModelContour(modelContour,
                        HomMat2D.FromPoses(0, 0, 0, rows[i].D, columns[i].D, angles[i].D));
                    HomMat2D followMat = HomMat2D.FromPoses(BaseRow, BaseColumn, BaseAngle, rows[i].D, columns[i].D, angles[i].D);
                    items.Add(CreateItem(ctx, i, rows[i].D, columns[i].D, angles[i].D, 1, scores[i].D, contour, followMat));
                }
            }
            finally
            {
                modelRegion?.Dispose();
                modelContour?.Dispose();
            }
        }
    }

    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Angle", "Scale", "Score", "HomMat" })]
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
    public sealed class HalconScaledShapeMatchTool : HalconTemplateMatchToolBase
    {
        public double ScaleMin { get; set; } = 0.9;
        public double ScaleMax { get; set; } = 1.1;

        public HalconScaledShapeMatchTool(string moduleName) : base(moduleName)
        {
        }

        protected override string MatchLabel
        {
            get { return "缩放形状匹配"; }
        }

        protected override string MissingModelMessage
        {
            get { return "缩放形状模板未创建"; }
        }

        protected override HTuple DeserializeModel(byte[] data)
        {
            return ShapeModelSerialization.Deserialize(data);
        }

        protected override void ClearModel(HTuple model)
        {
            HOperatorSet.ClearShapeModel(model);
        }

        protected override void FindMatches(FlowContext ctx, HObject image, HTuple modelId, List<MatchResultItem> items)
        {
            HObject modelContours = null;
            try
            {
                HOperatorSet.GetShapeModelContours(out modelContours, modelId, 1);
                HOperatorSet.FindScaledShapeModel(image, modelId, FindStartAngle, FindExtentAngle,
                    ScaleMin, ScaleMax, MinScore, NumMatches, MaxOverlap, SubPixel, NumLevelsFind, Greediness,
                    out HTuple rows, out HTuple columns, out HTuple angles, out HTuple scales, out HTuple scores);
                for (int i = 0; i < rows.Length; i++)
                {
                    HObject contour = TransformModelContour(modelContours,
                        HomMat2D.FromScaledPose(rows[i].D, columns[i].D, angles[i].D, scales[i].D));
                    // 与其他匹配工具一致：固定基准位姿 → 本匹配位姿（含缩放），供下游跟随
                    HomMat2D followMat = HomMat2D.FromPosesScaled(BaseRow, BaseColumn, BaseAngle,
                        rows[i].D, columns[i].D, angles[i].D, scales[i].D);
                    items.Add(CreateItem(ctx, i, rows[i].D, columns[i].D, angles[i].D, scales[i].D, scores[i].D, contour, followMat));
                }
            }
            finally
            {
                modelContours?.Dispose();
            }
        }
    }

    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Score", "HomMat" })]
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
    public sealed class HalconLocalDeformableMatchTool : HalconTemplateMatchToolBase
    {
        public double ScaleRowMin { get; set; } = 0.9;
        public double ScaleRowMax { get; set; } = 1.1;
        public double ScaleColumnMin { get; set; } = 0.9;
        public double ScaleColumnMax { get; set; } = 1.1;
        public string DeformationSmoothness { get; set; } = "11";

        public HalconLocalDeformableMatchTool(string moduleName) : base(moduleName)
        {
        }

        protected override string MatchLabel
        {
            get { return "局部变形匹配"; }
        }

        protected override string MissingModelMessage
        {
            get { return "局部变形模板未创建"; }
        }

        protected override HTuple DeserializeModel(byte[] data)
        {
            return ShapeModelSerialization.DeserializeDeformable(data);
        }

        protected override void ClearModel(HTuple model)
        {
            HOperatorSet.ClearDeformableModel(model);
        }

        protected override void FindMatches(FlowContext ctx, HObject image, HTuple modelId, List<MatchResultItem> items)
        {
            HObject imageRectified = null;
            HObject vectorField = null;
            HObject deformedContours = null;
            try
            {
                HTuple genNames = new HTuple();
                HTuple genValues = new HTuple();
                if (!string.IsNullOrWhiteSpace(DeformationSmoothness))
                {
                    genNames = genNames.TupleConcat("deformation_smoothness");
                    genValues = genValues.TupleConcat(DeformationSmoothness);
                }
                HOperatorSet.FindLocalDeformableModel(image, out imageRectified, out vectorField, out deformedContours,
                    modelId, FindStartAngle, FindExtentAngle, ScaleRowMin, ScaleRowMax, ScaleColumnMin, ScaleColumnMax,
                    MinScore, NumMatches, MaxOverlap, NumLevelsFind, Greediness, "deformed_contours", genNames, genValues,
                    out HTuple scores, out HTuple rows, out HTuple columns);
                HOperatorSet.CountObj(deformedContours, out HTuple contourCount);
                for (int i = 0; i < rows.Length; i++)
                {
                    HObject contour;
                    if (i < contourCount.I)
                    {
                        HOperatorSet.SelectObj(deformedContours, out contour, i + 1);
                    }
                    else
                    {
                        HOperatorSet.GenEmptyObj(out contour);
                    }
                    HomMat2D followMat = HomMat2D.FromPoses(BaseRow, BaseColumn, BaseAngle, rows[i].D, columns[i].D, 0);
                    items.Add(CreateItem(ctx, i, rows[i].D, columns[i].D, 0, 1, scores[i].D, contour, followMat));
                }
            }
            finally
            {
                imageRectified?.Dispose();
                vectorField?.Dispose();
                deformedContours?.Dispose();
            }
        }
    }
}